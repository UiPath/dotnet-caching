#!/usr/bin/env bash
# Starts and stops the Redis servers of the key move tests, the same on CI and on a developer machine.
#
#   eng/keymove-kit.sh up cluster [--base 17100]   three masters and three replicas on base to base+5
#   eng/keymove-kit.sh up replica [--base 17110]   a primary on base and its replica on base+1
#   eng/keymove-kit.sh down [--base N]             removes the containers of that base
#
# Every container runs on the host network with restart=always, so a test restarts a server with SHUTDOWN NOSAVE and the container comes back
# empty; DEBUG is enabled for the fault tests. "up" prints the variables the tests read, ready for eval.
set -euo pipefail

image=${KEYMOVE_KIT_IMAGE:-redis:7.4}
command=${1:-}
topology=""
base=""
[[ $# -gt 0 ]] && shift
if [[ "$command" = "up" ]] && [[ $# -gt 0 ]]; then
  topology=$1
  shift
fi

while [[ $# -gt 0 ]]; do
  case "$1" in
    --base) base=${2:?--base needs a port}; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

prefix() { echo "keymove-kit-$1"; }

start() {
  local port=$1
  shift
  docker run -d --name "$(prefix "$base")-$port" --network host --restart always "$image" \
    redis-server --port "$port" --save "" --appendonly no --enable-debug-command yes "$@" >/dev/null
}

wait_ping() {
  local port=$1 tries=0
  until docker exec "$(prefix "$base")-$port" redis-cli -p "$port" ping 2>/dev/null | grep -q PONG; do
    tries=$((tries + 1))
    if [[ "$tries" -gt 60 ]]; then
      echo "the server on $port did not answer" >&2
      exit 1
    fi
    sleep 1
  done
}

up_cluster() {
  base=${base:-17100}
  local nodes=() port
  for port in $(seq "$base" $((base + 5))); do
    start "$port" --cluster-enabled yes --cluster-config-file nodes.conf --cluster-node-timeout 2000
    nodes+=("127.0.0.1:$port")
  done

  for port in $(seq "$base" $((base + 5))); do
    wait_ping "$port"
  done

  docker exec "$(prefix "$base")-$base" redis-cli --cluster create "${nodes[@]}" --cluster-replicas 1 --cluster-yes >/dev/null

  # Every node must agree on ok and see three replicas linked before a test runs.
  local tries=0
  until docker exec "$(prefix "$base")-$base" redis-cli -p "$base" cluster info | grep -q cluster_state:ok \
    && [[ "$(docker exec "$(prefix "$base")-$base" redis-cli -p "$base" cluster nodes | grep -c slave)" -eq 3 ]]; do
    tries=$((tries + 1))
    if [[ "$tries" -gt 60 ]]; then
      echo "the cluster on $base did not settle" >&2
      exit 1
    fi
    sleep 1
  done

  local masters
  masters=$(IFS=,; echo "${nodes[*]:0:3}")
  echo "export REDIS_CLUSTER_CONNECTION_STRING='$masters'"
  echo "export REDIS_CLUSTER_REPLICAS=1"
}

up_replica() {
  base=${base:-17110}
  start "$base"
  start $((base + 1)) --replicaof 127.0.0.1 "$base"
  wait_ping "$base"
  wait_ping $((base + 1))

  local tries=0
  until docker exec "$(prefix "$base")-$((base + 1))" redis-cli -p $((base + 1)) info replication | grep -q master_link_status:up; do
    tries=$((tries + 1))
    if [[ "$tries" -gt 60 ]]; then
      echo "the replica on $((base + 1)) did not link" >&2
      exit 1
    fi
    sleep 1
  done

  echo "export REDIS_CONNECTION_STRING='127.0.0.1:$base'"
  echo "export REDIS_REPLICA_CONNECTION_STRING='127.0.0.1:$((base + 1))'"
}

down() {
  local filter="name=keymove-kit-"
  if [[ -n "$base" ]]; then
    filter="name=$(prefix "$base")-"
  fi

  local ids
  ids=$(docker ps -aq --filter "$filter")
  if [[ -n "$ids" ]]; then
    # shellcheck disable=SC2086 # one id per word
    docker rm -f $ids >/dev/null
  fi
}

case "$command" in
  up)
    case "$topology" in
      cluster) up_cluster ;;
      replica) up_replica ;;
      *) echo "usage: $0 up cluster|replica [--base N]" >&2; exit 2 ;;
    esac
    ;;
  down)
    down
    ;;
  *)
    echo "usage: $0 up cluster|replica [--base N] | down [--base N]" >&2
    exit 2
    ;;
esac
