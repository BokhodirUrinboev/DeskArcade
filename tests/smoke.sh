#!/usr/bin/env bash
# Smoke test for a published build: start Desk Arcade in demo mode (it plays the games by itself), check
# it stays up, open the stats window and quit through the --signal channel, and expect a clean exit.
# Usage: tests/smoke.sh path/to/DeskArcade[.exe]   (on Linux, run it under xvfb-run and dbus-run-session)
set -euo pipefail

exe="$1"
args=(--profile smoke)

"$exe" "${args[@]}" --demo &
pid=$!

sleep 20
if ! kill -0 "$pid" 2>/dev/null; then
    echo "::error::Desk Arcade exited during start-up"
    wait "$pid" || true
    exit 1
fi

"$exe" "${args[@]}" --signal stats
sleep 3
if ! kill -0 "$pid" 2>/dev/null; then
    echo "::error::Desk Arcade exited after --signal stats"
    exit 1
fi

"$exe" "${args[@]}" --signal quit
for _ in $(seq 30); do
    if ! kill -0 "$pid" 2>/dev/null; then
        status=0
        wait "$pid" || status=$?
        if [ "$status" -ne 0 ]; then
            echo "::error::Desk Arcade quit with exit code $status"
            exit 1
        fi
        echo "Desk Arcade started, opened the stats window and quit cleanly."
        exit 0
    fi
    sleep 1
done

echo "::error::Desk Arcade did not quit within 30 s of --signal quit"
kill "$pid" || true
exit 1
