#!/usr/bin/env bash
# Wait until the ROS stack log matches a pattern (or time out).
# Usage: wait_ros_stack.sh [log=/tmp/hmi_stack.log] [pattern='map loaded|Map: loaded|Traceback'] [timeout_s=120]
# Exit 0 on match, 1 on timeout. Prints the last relevant log lines.
LOG="${1:-/tmp/hmi_stack.log}"
PATTERN="${2:-map loaded|Map: loaded|Traceback}"
TIMEOUT="${3:-120}"

found=1
for _ in $(seq 1 $(( (TIMEOUT + 1) / 2 ))); do
  if [ -f "$LOG" ] && grep -qiE "$PATTERN" "$LOG"; then found=0; break; fi
  sleep 2
done
[ -f "$LOG" ] && grep -iE "$PATTERN|ERROR|Keepout mask|Starting server|status" "$LOG" | tail -15
[ "$found" -eq 0 ] || echo "timeout after ${TIMEOUT}s waiting for: $PATTERN" >&2
exit $found
