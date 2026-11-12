#!/usr/bin/env bash
# 시뮬 겹침 방지 잠금(사람·에이전트 공용). 사용: tests/sim_lock.sh <명령…>  — 잠금을 얻을 때까지 30초마다 기다린 뒤 명령을 돌리고 잠금을 푼다.
# 잠금 = tests/.sim_lock 디렉터리(mkdir 은 원자적). 주인 정보는 그 안 owner 파일. 4시간 넘은 잠금은 낡은 것으로 보고 치운다.
L="$(cd "$(dirname "$0")" && pwd)/.sim_lock"
while ! mkdir "$L" 2>/dev/null; do
  if [ -n "$(find "$L" -maxdepth 0 -mmin +240 2>/dev/null)" ]; then echo "# sim_lock: 4시간 넘은 잠금 치움($(cat "$L/owner" 2>/dev/null))" >&2; rm -rf "$L"; continue; fi
  echo "# sim_lock: 대기 — $(cat "$L/owner" 2>/dev/null)" >&2; sleep 30
done
echo "pid=$$ $(date '+%F %T') $*" > "$L/owner"
trap 'rm -rf "$L"' EXIT
"$@"
