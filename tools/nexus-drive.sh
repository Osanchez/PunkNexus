#!/usr/bin/env bash
# Drive PUNK Nexus's diagnostic harness without guessing at sleeps.
#
# The harness reads commands from devcmd.txt and answers in devout.txt. Driving that with fixed
# sleeps is what produced most of the bad test results in this project: a click issued before its
# dialog existed landed on whatever was underneath, and the run carried on looking successful. Every
# helper here waits for the ANSWER to the command it just sent, so a step cannot overtake the UI.
#
# Usage:
#   . tools/nexus-drive.sh
#   nx_click "Install"          # send, wait for the harness to answer, print it
#   nx_wait  "Install all 2"    # block until a control is clickable (harness-side watch)
#   nx_dialog                   # what modal is open, if any
#   nx_shot  name               # screenshot
NX_ROOT="${LOCALAPPDATA:-$HOME/AppData/Local}/PunkNexus"
NX_CMD="$NX_ROOT/devcmd.txt"
NX_OUT="$NX_ROOT/devout.txt"

# Send one command and return once devout has grown, i.e. the harness has answered THIS command.
nx() {
    local before after tries=0
    before=$(wc -c < "$NX_OUT" 2>/dev/null || echo 0)
    printf '%s\n' "$*" >> "$NX_CMD"
    while [ $tries -lt 120 ]; do
        after=$(wc -c < "$NX_OUT" 2>/dev/null || echo 0)
        if [ "$after" -gt "$before" ]; then
            tail -c "$((after - before))" "$NX_OUT"
            return 0
        fi
        sleep 0.25
        tries=$((tries + 1))
    done
    echo "  nx: TIMED OUT waiting for a reply to '$*'"
    return 1
}

# Wait for a control to be clickable. The harness watches on its own timer and answers when ready,
# so this returns as soon as the UI is actually in the expected state.
nx_wait() {
    local text="$1" secs="${2:-30}" before after tries=0
    before=$(wc -c < "$NX_OUT" 2>/dev/null || echo 0)
    printf 'waitfor %s %s\n' "$text" "$secs" >> "$NX_CMD"
    while [ $tries -lt $(( (secs + 5) * 4 )) ]; do
        after=$(wc -c < "$NX_OUT" 2>/dev/null || echo 0)
        if [ "$after" -gt "$before" ] && tail -c "$((after - before))" "$NX_OUT" | grep -q "waitfor: '"; then
            tail -c "$((after - before))" "$NX_OUT" | grep "waitfor:" | tail -1
            tail -c "$((after - before))" "$NX_OUT" | grep -q "TIMED OUT" && return 1
            return 0
        fi
        sleep 0.25
        tries=$((tries + 1))
    done
    echo "  nx_wait: no answer for '$text'"
    return 1
}

# Wait for it, then click it. The pairing is the point: never click something not yet there.
nx_click() { nx_wait "$1" "${2:-30}" >/dev/null && nx "click $1"; }
nx_dialog() { nx "dialog"; }
nx_shot()   { nx "screenshot $1"; }
nx_text()   { nx "settext $1 $2"; }
