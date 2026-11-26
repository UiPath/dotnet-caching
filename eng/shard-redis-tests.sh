#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."
export LC_ALL=C
dry_run=false
coverage=true
tfms=(net8.0 net10.0)
while (($#)); do
  case "$1" in
    --dry-run) dry_run=true ;;
    --no-coverage) coverage=false ;;
    --tfm) tfms=("$2"); shift ;;
    *) echo "Usage: $0 [--dry-run] [--no-coverage] [--tfm <tfm>]" >&2; exit 2 ;;
  esac
  shift
done

results="$PWD/TestResults"
scratch=$(mktemp -d)
container="caching-tests-$$"
pids=()
cleanup() {
  if ((${#pids[@]})); then
    kill "${pids[@]}" 2>/dev/null || true
    wait "${pids[@]}" 2>/dev/null || true
  fi
  if ! "$dry_run"; then docker rm -f "$container" >/dev/null 2>&1 || true; fi
  rm -rf "$scratch"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
mkdir -p "$results"

# Read collection metadata without constructing fixtures or changing test discovery.
cat > "$scratch/Discover.csproj" <<'PROJECT'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
PROJECT
cat > "$scratch/Program.cs" <<'CS'
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

var assemblyPath = Path.GetFullPath(args[0]);
var context = new AssemblyLoadContext("discovery", isCollectible: true);
context.Resolving += (_, name) =>
{
    var path = Path.Combine(Path.GetDirectoryName(assemblyPath)!, name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};
var assembly = context.LoadFromAssemblyPath(assemblyPath);
var classes = JsonSerializer.Deserialize<string[]>(File.ReadAllText(args[1]))!;
if (classes.Length == 0 || classes.Distinct().Count() != classes.Length)
    throw new Exception("Discovery returned no classes or duplicate classes.");
var integration = classes.Order(StringComparer.Ordinal).ToArray();
var count = Math.Min(int.Parse(args[3]), integration.Length);
var groups = Enumerable.Range(0, count + 1).Select(_ => new List<string>()).ToArray();
groups[0].AddRange(classes.Except(integration).Order(StringComparer.Ordinal));
for (var i = 0; i < integration.Length; i++) groups[1 + i % count].Add(integration[i]);
var assigned = groups.SelectMany(g => g).ToArray();
if (assigned.Length != classes.Length || assigned.Distinct().Count() != classes.Length ||
    !assigned.ToHashSet().SetEquals(classes))
    throw new Exception("The shard union must equal discovery, with each class assigned exactly once.");
Directory.CreateDirectory(args[2]);
for (var i = 0; i < groups.Length; i++)
{
    if (groups[i].Count == 0) continue;
    var name = i == 0 ? "unit" : $"shard-{i}";
    File.WriteAllText(Path.Combine(args[2], name + ".classes"), string.Join('\n', groups[i]) + "\n");
    var estimate = i == 0 || args[4] == "0" ? "unmeasured" :
        $"~{double.Parse(args[4]) * groups[i].Count / integration.Length:F0}s (class-count estimate)";
    Console.WriteLine($"{args[5]}/{name}: {groups[i].Count} classes, {estimate}");
}
Console.WriteLine($"{args[5]}: union verified for all {classes.Length} classes.");
CS
dotnet build "$scratch/Discover.csproj" -o "$scratch/helper" --verbosity quiet \
  -p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false -p:SonarQubeExclude=true

apps=() plans=() ports=() names=() projects=() frameworks=()
for project in tests/*/*.csproj; do
  module=$(basename "$project" .csproj)
  case "$module" in
    UiPath.Caching.Tests) shards=4; seconds=480 ;;
    UiPath.Caching.Migration.Tests) shards=2; seconds=222 ;;
    *) shards=1; seconds=0 ;;
  esac
  for tfm in "${tfms[@]}"; do
    app="$PWD/$(dirname "$project")/bin/Release/$tfm/$module"
    [[ -f "$app.exe" ]] && app="$app.exe"
    [[ -f "$app" ]] || { echo "Missing test executable: $app" >&2; exit 1; }
    label="$module/$tfm"
    plan="$scratch/$label"
    mkdir -p "$plan"
    "$app" -list classes/json -noColor > "$plan/listing.txt"
    head -n 1 "$plan/listing.txt" > "$plan/discovered.json"
    dotnet "$scratch/helper/Discover.dll" "${app%.exe}.dll" "$plan/discovered.json" \
      "$plan" "$shards" "$seconds" "$label"
    for classes in "$plan/"*.classes; do
      name="$label/$(basename "$classes" .classes)"
      port=0
      [[ "$classes" == */unit.classes ]] || port=$((16400 + 10 * ${#apps[@]}))
      apps+=("$app") plans+=("$classes") ports+=("$port") names+=("$name")
      projects+=("$(dirname "$project")") frameworks+=("$tfm")
      printf '%s: primary port %s\n' "$name" "$port"
      if "$dry_run"; then sed "s|^|  $name |" "$classes"; fi
    done
  done
done
((${#apps[@]})) || { echo 'No test modules found.' >&2; exit 1; }
"$dry_run" && exit 0

# Each integration process owns all six endpoints, including both replica pairs.
echo "Cores: $(nproc), shards: ${#apps[@]}"
started=$SECONDS
docker run -d --name "$container" --network host redis:7 sh -c '
  for base do
    [ "$base" -ne 0 ] || continue
    for offset in 0 1 2 3 4 5; do
      mkdir -p "/tmp/$((base + offset))"
      set --
      case "$offset" in
        1) set -- --replicaof 127.0.0.1 "$base" ;;
        5) set -- --replicaof 127.0.0.1 "$((base + 4))" ;;
      esac
      redis-server --port "$((base + offset))" --dir "/tmp/$((base + offset))" \
        --bind 127.0.0.1 --save "" --appendonly no \
        --enable-debug-command yes --repl-diskless-sync-delay 0 "$@" &
    done
  done
  wait
' sh "${ports[@]}" > /dev/null
if ! timeout 60s docker exec "$container" sh -c '
  for base do
    [ "$base" -ne 0 ] || continue
    for offset in 0 1 2 3 4 5; do
      until redis-cli -p "$((base + offset))" ping | grep -q PONG; do sleep 0.1; done
    done
    for offset in 1 5; do
      until redis-cli -p "$((base + offset))" info replication | grep -q master_link_status:up; do sleep 0.1; done
    done
  done
' sh "${ports[@]}"; then
  docker logs "$container"
  exit 1
fi

for i in "${!apps[@]}"; do
  mkdir -p "$results/${names[i]}"
  (
    mapfile -t classes < "${plans[i]}"
    options=()
    port=${ports[i]}
    if ((port)); then
      export RUN_REDIS_INTEGRATION_TESTS=1 REDIS_CONNECTION_STRING="127.0.0.1:$port"
      export REDIS_REPLICA_CONNECTION_STRING="127.0.0.1:$((port + 1))"
      export CLAIM_REPLICA_CONNECTION_STRING="$REDIS_REPLICA_CONNECTION_STRING"
      export TWO_SERVER_SRC="127.0.0.1:$((port + 2))" TWO_SERVER_DST="127.0.0.1:$((port + 3))"
      export MOVE_SIDES_MASTER="127.0.0.1:$((port + 4))" MOVE_SIDES_REPLICA="127.0.0.1:$((port + 5))"
    fi
    if "$coverage"; then options+=(--coverage --coverage-output-format xml --coverage-output coverage.xml); fi
    # dotnet test starts the runner on the testing platform, which owns the trx, coverage and class filter.
    exec timeout 10m dotnet test --project "${projects[i]}" --framework "${frameworks[i]}" -c Release --no-build \
      --filter-class "${classes[@]}" \
      --results-directory "$results/${names[i]}" --report-xunit-trx "${options[@]}"
  ) > "$results/${names[i]}/output.log" 2>&1 &
  pids+=("$!")
done

status=0
for i in "${!pids[@]}"; do
  if wait "${pids[i]}"; then
    echo "Passed: ${names[i]} at $((SECONDS - started))s"
  else
    echo "Failed: ${names[i]} at $((SECONDS - started))s" >&2
    cat "$results/${names[i]}/output.log"
    status=1
  fi
  if "$coverage" && [[ ! -s "$results/${names[i]}/coverage.xml" ]]; then
    echo "Missing coverage: ${names[i]}" >&2
    status=1
  fi
done
pids=()
exit "$status"
