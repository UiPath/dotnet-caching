#!/usr/bin/env bash
# Runs the UiPath.Caching.Tests integration suite against each Redis-compatible server in a container.
# Usage: eng/compat-matrix.sh [results-dir] [name ...]
# Needs Docker. Builds the tests once (Release, net10.0), then for each server: start, wait for PING, run, stop.
# redis-cluster is the one entry that is not one container: three cluster-enabled masters on the host network, joined with redis-cli, and the suite
# connects to one of them as localhost, which the cluster lists again by its address, as an entry endpoint (Azure Managed Redis) is listed.
# Writes <results-dir>/<name>.log per server and prints one summary line per server.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
results="${1:-$root/TestResults/compat}"
shift || true
port=16379

# name|image|container port|extra docker args|server args|connection string options
servers=(
  "redis-6.2|redis:6.2|6379|||"
  "redis-7.4|redis:7.4|6379|||"
  "redis-8|redis:8|6379|||"
  "valkey-8.1|valkey/valkey:8.1|6379|||"
  "valkey-9|valkey/valkey:9|6379|||"
  "dragonfly|docker.dragonflydb.io/dragonflydb/dragonfly:v2.1.0|6379|--ulimit memlock=-1|--proactor_threads=2"
  "kvrocks|apache/kvrocks:2.17.0|6666|||,protocol=resp2"  # RESP3 Pub/Sub messages are mis-framed, see docs/reference/server-support.md
  "garnet|ghcr.io/microsoft/garnet:2.2.13|6379||--lua"
)

cluster_port=17601
cluster_nodes=3

run_cluster() {
  local name=redis-cluster nodes=() i
  for i in $(seq 0 $((cluster_nodes - 1))); do
    docker rm -f "compat-$name-$i" >/dev/null 2>&1 || true
    nodes+=("127.0.0.1:$((cluster_port + i))")
    if ! docker run -d --name "compat-$name-$i" --network host redis:7.4 redis-server --port "$((cluster_port + i))" --cluster-enabled yes --cluster-config-file nodes.conf --appendonly no --save "" >/dev/null 2>"$results/$name.log"; then
      printf '%-12s START FAILED (see %s)\n' "$name" "$results/$name.log"
      stop_cluster
      return
    fi
  done

  local ready=0
  for _ in $(seq 1 60); do
    if docker exec "compat-$name-0" redis-cli -p "$cluster_port" ping 2>/dev/null | grep -q PONG \
      && docker exec "compat-$name-0" redis-cli --cluster create "${nodes[@]}" --cluster-replicas 0 --cluster-yes >>"$results/$name.log" 2>&1; then
      ready=1; break
    fi
    sleep 1
  done
  # The slots are served once the cluster says ok on every node.
  for _ in $(seq 1 60); do
    [[ "$ready" -eq 1 ]] || break
    if docker exec "compat-$name-0" redis-cli -p "$cluster_port" cluster info | grep -q cluster_state:ok; then break; fi
    sleep 1
  done
  if [[ "$ready" -ne 1 ]]; then
    printf '%-12s NOT READY\n' "$name"
    stop_cluster
    return
  fi

  RUN_REDIS_INTEGRATION_TESTS=1 REDIS_CONNECTION_STRING="localhost:$cluster_port" \
    timeout 1800 "$exe" -noColor >"$results/$name.log" 2>&1 || true
  stop_cluster

  summary="$({ grep -E 'Total: ' "$results/$name.log" || true; } | tail -1 | sed -E 's/^.*(Total: )/\1/')"
  printf '%-12s %s\n' "$name" "${summary:-no summary (see $results/$name.log)}"
}

stop_cluster() {
  local i
  for i in $(seq 0 $((cluster_nodes - 1))); do
    docker rm -f "compat-redis-cluster-$i" >/dev/null 2>&1 || true
  done
}

mkdir -p "$results"
dotnet build "$root/tests/UiPath.Caching.Tests" -c Release -f net10.0 -v q -nologo | tail -2
exe="$root/tests/UiPath.Caching.Tests/bin/Release/net10.0/UiPath.Caching.Tests"

if [[ "$#" -eq 0 ]] || [[ " $* " =~ " redis-cluster " ]]; then
  run_cluster
fi

for entry in "${servers[@]}"; do
  IFS='|' read -r name image cport dargs sargs copts _ <<<"${entry%%  #*}"
  if [[ "$#" -gt 0 ]] && [[ ! " $* " =~ " $name " ]]; then continue; fi

  container="compat-$name"
  docker rm -f "$container" >/dev/null 2>&1 || true
  # shellcheck disable=SC2086
  if ! docker run -d --name "$container" -p "$port:$cport" $dargs "$image" $sargs >/dev/null 2>"$results/$name.log"; then
    printf '%-12s START FAILED (see %s)\n' "$name" "$results/$name.log"
    continue
  fi

  ready=0
  for _ in $(seq 1 60); do
    if (exec 3<>"/dev/tcp/127.0.0.1/$port" && printf 'PING\r\n' >&3 && timeout 1 head -c 5 <&3 | grep -q PONG) 2>/dev/null; then
      ready=1; break
    fi
    sleep 1
  done
  if [[ "$ready" -ne 1 ]]; then
    printf '%-12s NOT READY\n' "$name"
    docker logs "$container" >>"$results/$name.log" 2>&1 || true
    docker rm -f "$container" >/dev/null
    continue
  fi

  RUN_REDIS_INTEGRATION_TESTS=1 REDIS_CONNECTION_STRING="127.0.0.1:$port$copts" \
    timeout 1800 "$exe" -noColor >"$results/$name.log" 2>&1 || true
  docker rm -f "$container" >/dev/null

  summary="$({ grep -E 'Total: ' "$results/$name.log" || true; } | tail -1 | sed -E 's/^.*(Total: )/\1/')"
  printf '%-12s %s\n' "$name" "${summary:-no summary (see $results/$name.log)}"
done
