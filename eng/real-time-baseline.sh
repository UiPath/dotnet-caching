#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
config=tests/.editorconfig
scratch="$(mktemp -d)"
cp "$config" "$scratch/editorconfig"
restore() {
  cp "$scratch/editorconfig" "$config"
  rm -rf "$scratch"
}
trap restore EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

awk '{ sub(/\r$/, "") }
  /^\[/ && $0 != "[*.cs]" { path = substr($0, 2, length($0) - 2) }
  /^dotnet_diagnostic\.RS0030\.severity = none$/ { print path }' \
  "$config" | LC_ALL=C sort -u > "$scratch/baseline"
printf 'Baselined files: %s\n' "$(wc -l < "$scratch/baseline" | tr -d ' ')"

# Rebuild with exemptions disabled so the analyzer can identify stale entries.
sed 's/dotnet_diagnostic.RS0030.severity = \(none\|error\)/dotnet_diagnostic.RS0030.severity = warning/' \
  "$scratch/editorconfig" > "$config"
if ! dotnet build UiPath.Caching.slnx -c Release -t:Rebuild -warnaserror -warnnotaserror:RS0030 \
    > "$scratch/build.log" 2>&1; then
  tail -n 50 "$scratch/build.log"
  exit 1
fi

sed 's|\\|/|g' "$scratch/build.log" | \
  sed -nE 's|^.*[/]tests/(.*)\([0-9]+,[0-9]+\): warning RS0030:.*|\1|p' | \
  LC_ALL=C sort -u > "$scratch/actual"
comm -23 "$scratch/baseline" "$scratch/actual" > "$scratch/stale"
comm -13 "$scratch/baseline" "$scratch/actual" > "$scratch/new"
if [[ -s "$scratch/stale" ]]; then
  printf 'Stale baseline sections must be deleted:\n'
  cat "$scratch/stale"
  exit 1
fi
if [[ -s "$scratch/new" ]]; then
  printf 'Unbaselined files use banned members:\n'
  cat "$scratch/new"
  exit 1
fi
printf 'Every baseline section still has analyzer-reported banned members.\n'
