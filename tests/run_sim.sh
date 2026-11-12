#!/usr/bin/env bash
# UartHub 시뮬레이션: build.sh(dotnet, 한 번 컴파일) → tests/run_sim.ps1(개발 사본). 종료코드는 run_sim.ps1 의 것을 그대로 돌려준다.
#   사용: tests/run_sim.sh [--quiet] [--part k/n] [--exe-version <ver>] [id ...]   (기본 전체. 예: run_sim.sh fakeboard-selftest 14)
#   --exe-version <ver>(또는 환경변수 UART_HUB_USE_BUILD=<ver>): 다시 빌드하지 않고 지정 빌드(out/<ver>/ 와 그 Windows 사본,
#            build.sh --use)로 돈다. 결과 머리 EXE_SHA256 = 시험한 UartHub.exe. 없으면 지금처럼 build.sh 로 새로 빌드.
#   --part k/n: 등록 순서 i 번째 중 i % n == k-1 인 시나리오만(ralph verify 의 명령당 10분 제한 때문에 나눠 돈다)
#   --quiet: 화면에는 실패 줄과 '# fail N'만 낸다(ralph 피드백 게이트용 — verify 는 출력 줄 전체를 실패 서명으로 본다).
#            전체 출력은 결과 파일에 그대로 남는다.
# 시험 대상(계획 T1 H2): pwsh 가 같은 빌드의 lib\UartHub.Core.dll·lib\UartHub.Tests.dll 을 로드하고, 프로세스 시나리오는 UartHub.exe(sup·core·cli)를 띄운다.
# 결과는 tests/results/YYYYMMDD_HHMMSS.txt 에 남는다. 실행 정책은 바꾸지 않는다(로컬 사본에서 실행, S0-1).
# C-13: 설치 계열 시나리오는 Windows 쪽에서 WSL 을 볼 수 없으므로, 실행 전후 이 배포판의 ~/.claude/skills/{uart,connect-uart,disconnect-uart} 목록·SHA-256 이 같은지 여기서 본다
#       (없으면 "없음"이 같은지). 다르면 [FAIL] wsl-skills-unchanged 줄과 rc=1.
set -uo pipefail
HUB_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RES_DIR="$HUB_DIR/tests/results"
mkdir -p "$RES_DIR"
res="$RES_DIR/$(date +%Y%m%d_%H%M%S).txt"
quiet=0
part=""
use="${UART_HUB_USE_BUILD:-}"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --quiet) quiet=1; shift ;;
    --part | --exe-version)
      [[ $# -ge 2 ]] || { echo "run_sim: $1 에 값이 필요" >&2; exit 2; }
      if [[ $1 == --part ]]; then part="$2"; else use="$2"; fi
      shift 2 ;;
    *) break ;;
  esac
done
ids="$*"

t0=$(date +%s%3N)
if [[ -n "$use" ]]; then build_out="$("$HUB_DIR/build.sh" --use "$use" 2>&1)"; else build_out="$("$HUB_DIR/build.sh" 2>&1)"; fi
brc=$?
ver="$(sed -n 's/^VERSION=//p' <<<"$build_out")"
exe_win="$(sed -n 's/^EXE_WIN=//p' <<<"$build_out")"
{
  echo "# run_sim $(date '+%F %T') ids=${ids:-all}${part:+ part=$part}${use:+ exe-version=$use}"
  echo "# dev=${exe_win%\\*}"
  echo "build_ms=$(( $(date +%s%3N) - t0 ))"
  grep -E '^(BUILD|VERSION|EXE_SHA256)=' <<<"$build_out"
} > "$res"
if [[ $brc -ne 0 || -z "$ver" || -z "$exe_win" ]]; then
  { echo "$build_out"; echo "[FAIL] build 실패 (rc=$brc)"; } | tee -a "$res"
  echo "결과 파일: $res"
  exit 1
fi
DEV_V="${exe_win%\\*}"

# WSL ~/.claude/skills/{uart, connect-uart, disconnect-uart} 상태(폴더마다 파일 목록·크기·권한 + 내용 SHA-256 을 한 해시로). 없으면 "없음".
skills_state() {
  local out='' e d n h
  for e in uart connect-uart disconnect-uart; do
    d="$HOME/.claude/skills/$e"
    if [[ -e "$d" ]]; then
      n="$(find "$d" | wc -l)"
      h="$( (cd "$d" && find . -printf '%p %s %m\n' | LC_ALL=C sort && find . -type f -print0 | LC_ALL=C sort -z | xargs -0 -r sha256sum) | sha256sum | cut -c1-16)"
      out+="${out:+, }$e 항목 ${n}개 sha=${h}"
    else out+="${out:+, }$e 없음"; fi
  done
  echo "$out"
}
skills_before="$(skills_state)"

cd /mnt/c || exit 1
# 시뮬 34 는 Windows 쪽에서 wsl.exe 로 같은 배포판의 bash 를 띄운다(배포판 이름을 pwsh 에 넘김).
[[ -n "${WSL_DISTRO_NAME:-}" ]] && export WSLENV="${WSLENV:+$WSLENV:}WSL_DISTRO_NAME"

args=(-NoProfile -File "$DEV_V\\tests\\run_sim.ps1" -Version "$ver" -Exe "$exe_win")
[[ -n "$ids" ]] && args+=(-Ids "${ids// /,}")
[[ -z "$ids" && -n "$part" ]] && args+=(-Part "$part")
# 실행 뒤 skills 비교: 결과 줄을 sl 에, 다르면 rc=1(현재 셸에서 — 파이프 안 subshell 이 아님)
check_skills() {
  local after; after="$(skills_state)"
  if [[ "$after" == "$skills_before" ]]; then
    sl="# wsl ~/.claude/skills: 실행 전 ${skills_before} → 실행 뒤 ${after} (같음)"
  else
    sl="[FAIL] wsl-skills-unchanged WSL ~/.claude/skills(uart·connect-uart·disconnect-uart)가 바뀜: 실행 전 ${skills_before} → 실행 뒤 ${after}"
    rc=1
  fi
}
if (( quiet )); then
  pwsh.exe "${args[@]}" 2>&1 | tr -d '\r' >> "$res"
  rc=${PIPESTATUS[0]}
  check_skills; echo "$sl" >> "$res"
  grep -E '^\[FAIL\]' "$res"
  echo "# fail $(grep -cE '^\[FAIL\]' "$res")"
  (( rc != 0 )) && echo "run_sim rc=$rc (결과 파일: $res)"
else
  pwsh.exe "${args[@]}" 2>&1 | tr -d '\r' | tee -a "$res"
  rc=${PIPESTATUS[0]}
  check_skills; echo "$sl" | tee -a "$res"
  echo "결과 파일: $res"
fi
exit "$rc"
