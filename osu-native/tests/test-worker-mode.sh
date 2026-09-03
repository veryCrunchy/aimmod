#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
    echo "Usage: $0 /path/to/AimMod" >&2
    exit 2
fi

aimmod_executable=$1
if [[ ! -x "$aimmod_executable" ]]; then
    echo "AimMod executable is missing or not executable: $aimmod_executable" >&2
    exit 2
fi

python3 - "$aimmod_executable" <<'PY'
import json
import subprocess
import sys

executable = sys.argv[1]
requests = [
    {"id": "11111111-1111-1111-1111-111111111111", "protocolVersion": 1, "command": "hello"},
    {"id": "22222222-2222-2222-2222-222222222222", "protocolVersion": 1, "command": "shutdown"},
]

completed = subprocess.run(
    [executable, "--worker"],
    input="".join(json.dumps(request) + "\n" for request in requests),
    text=True,
    stdout=subprocess.PIPE,
    stderr=subprocess.PIPE,
    timeout=15,
    check=False,
)

if completed.returncode != 0:
    raise SystemExit(f"worker exited with {completed.returncode}: {completed.stderr}")

lines = completed.stdout.splitlines()
if len(lines) != 2:
    raise SystemExit(f"worker stdout was not protocol-only: {completed.stdout!r}")

responses = [json.loads(line) for line in lines]
if [response.get("id") for response in responses] != [request["id"] for request in requests]:
    raise SystemExit(f"worker response ids did not match requests: {responses!r}")
if not all(response.get("success") is True for response in responses):
    raise SystemExit(f"worker protocol smoke test failed: {responses!r}")

print("Single-apphost worker mode passed.")
PY
