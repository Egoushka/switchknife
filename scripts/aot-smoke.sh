#!/bin/sh
# Publishes samples/Switchknife.AotSmoke with Native AOT (warnings are errors) and runs it.
# Usage: scripts/aot-smoke.sh [rid]   (default: this machine's). Healthy: exit 0, one "ok" line per package. Test-support packages (Switchknife.Testing.*) without AOT support have no case.
set -eu
cd "$(dirname "$0")/.."
rid=${1:-$(dotnet --info | sed -n 's/^ *RID: *//p' | head -n 1)}
out=$(mktemp -d)
trap 'rm -rf "$out"' EXIT
dotnet publish samples/Switchknife.AotSmoke -c Release -r "$rid" -warnaserror -o "$out" -v quiet -nologo
echo "binary: $(du -k "$out/Switchknife.AotSmoke" | cut -f1) KiB"
"$out/Switchknife.AotSmoke"
