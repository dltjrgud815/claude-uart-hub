#!/usr/bin/env bash
# Git Bash 계약 테스트(계획 T4, C-7 래퍼 v2 — Windows Claude Code 경로). 하드웨어 없음(FakeBoard), 실제 COM 미사용.
#   사용: tests/run_sim_gitbash.sh [--quiet] [--exe-version <ver>]
#   --exe-version <ver>(또는 환경변수 UART_HUB_USE_BUILD=<ver>): 다시 빌드하지 않고 지정 빌드(build.sh --use)로 돈다(run_sim.sh 와 같은 규약).
#   --quiet: 화면에는 [FAIL] 줄과 '# fail N'만 낸다(run_sim.sh 와 같은 규약). 전체 출력은 결과 파일에 남는다.
# 순서
#   1. build.sh(dotnet → 개발 사본 %LOCALAPPDATA%\uart_hub_dev\<ver>\)
#   2. tests/run_sim_wsl.ps1 을 그대로 Windows 쪽 하네스로 백그라운드 실행: 시험 허브(sup --no-tray --sim → 코어 안 FakeBoard, 제어 18050·케이블 18060,
#      mutex 난수) → ready.txt. 작업 폴더는 D: 가 있으면 D:\uart_hub_test\gitbash_<시각>\(로그 경로가 D:/… 로 나오게), 없으면 %TEMP%\uart_hub_test\.
#   3. run_sim_wsl.sh 와 같은 케이스 10개를 WSL 에서 NUL 구분 파일(<작업>\gb\cases\NN.bin)로 만들어 넘기고(interop 명령줄 재해석 방지),
#      래퍼(claude/uart/hub/skill/uart)와 Git Bash 쪽 스크립트를 작업 폴더에 복사한 뒤 Git Bash(C:\Program Files\Git\bin\bash.exe)를 **파이프로만** 실행한다
#      (표준 입력 /dev/null, 출력은 파일 — 창 없음). Git Bash 쪽이 케이스마다 <작업>\gb\NN.{out,err,rc} 와 부가 검사 결과(<작업>\gb\extra.txt)를 남긴다.
#      부가: g1 rc 42 · g2 logpath 가 D:/(작업 폴더 드라이브)로 시작하고 Git Bash 에서 파일 존재 · g3 logpath --json 의 log·raw·idx · g4 hub status 경로 ·
#            g5 '/'로 시작하는 인자 보존(MSYS 경로 변환 없음) · g6 push <local> → cygpath -w · g7 exe 없음 → 103 ·
#            g8 래퍼 TERM → Windows cli ≤2초 · g9 래퍼 INT → Windows cli ≤2초(--parent-pipe, timeout --foreground 로 래퍼 bash 에만 신호) ·
#            X6: g11 events --json(보드가 찍은 panic 줄 → context D:/… 파일 있음) · g12 aging --json·비JSON · g13 events --follow --json(| cat 경유로 새 oom 줄, 래퍼 TERM → cli ≤2초).
#   4. wsl_done → 하네스가 같은 케이스를 UartHub.exe cli 프로세스(ArgumentList, Windows 기준)로 실행 → 정리(잔존 감독·코어 0 — g10)
#   5. 비교(run_sim_wsl.sh 와 같은 규칙): 종료코드·stderr 같음, JSON 은 시각·순서 필드를 뺀 뒤 같음, 경로 필드는 Windows 값의 '\'→'/' 와 같아야 함.
# 결과: tests/results/gitbash_YYYYMMDD_HHMMSS.txt. 종료코드: 모두 PASS 면 0, 아니면 1.
set -uo pipefail
HUB_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
UART_SRC="$HUB_DIR/skill/uart"
GITBASH="/mnt/c/Program Files/Git/bin/bash.exe"
RES_DIR="$HUB_DIR/tests/results"
mkdir -p "$RES_DIR"
ts="$(date +%Y%m%d_%H%M%S)"
res="$RES_DIR/gitbash_$ts.txt"
quiet=0
use="${UART_HUB_USE_BUILD:-}"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --quiet) quiet=1; shift ;;
    --exe-version) [[ $# -ge 2 ]] || { echo "run_sim_gitbash: $1 에 값이 필요" >&2; exit 2; }; use="$2"; shift 2 ;;
    *) break ;;
  esac
done
: > "$res"

say() { printf '%s\n' "$*" >> "$res"; (( quiet )) || printf '%s\n' "$*"; }
fails=0
fail() { say "[FAIL] $*"; fails=$((fails + 1)); }
finish() {
  local rc=$1
  if (( quiet )); then
    grep -E '^\[FAIL\]' "$res"
    echo "# fail $(grep -cE '^\[FAIL\]' "$res")"
    (( rc != 0 )) && echo "run_sim_gitbash rc=$rc (결과 파일: $res)"
  else
    echo "결과 파일: $res"
  fi
  exit "$rc"
}

say "# run_sim_gitbash $(date '+%F %T')${use:+ exe-version=$use}"
[[ -f "$UART_SRC" ]] || { fail "래퍼 없음: $UART_SRC"; finish 1; }
[[ -f "$GITBASH" ]] || { fail "Git Bash 없음: $GITBASH"; finish 1; }

# ---- 1. build
if [[ -n "$use" ]]; then build_out="$("$HUB_DIR/build.sh" --use "$use" 2>&1)"; else build_out="$("$HUB_DIR/build.sh" 2>&1)"; fi
brc=$?
ver="$(sed -n 's/^VERSION=//p' <<<"$build_out")"
EXE_WIN="$(sed -n 's/^EXE_WIN=//p' <<<"$build_out")"
DEV_WIN="${EXE_WIN%\\*}"
cd /mnt/c || exit 1
say "# dev=$DEV_WIN"
grep -E '^(BUILD|VERSION|EXE_SHA256)=' <<<"$build_out" | while read -r l; do say "$l"; done
if [[ $brc -ne 0 || -z "$ver" || -z "$EXE_WIN" ]]; then say "$build_out"; fail "build 실패 (rc=$brc)"; finish 1; fi

# ---- 2. 하네스(시험 허브). 작업 폴더: D: 가 있으면 D:\uart_hub_test\ (최근 3개만 남김)
if [[ -d /mnt/d ]]; then
  base_win='D:\uart_hub_test'
  base_u=/mnt/d/uart_hub_test
  mkdir -p "$base_u"
  ls -1 "$base_u" | grep -E '^gitbash_[0-9]{8}_[0-9]{6}$' | sort -r | tail -n +3 | while read -r o; do rm -rf "${base_u:?}/$o"; done
else
  tmp_win="$(cmd.exe /c 'echo %TEMP%' 2>/dev/null | tr -d '\r')"
  [[ -n "$tmp_win" && "$tmp_win" != *%* ]] || { fail "%TEMP% 를 얻지 못함"; finish 1; }
  base_win="$tmp_win\\uart_hub_test"
fi
WORK_WIN="$base_win\\gitbash_$ts"
WORK="$(wslpath -u "$WORK_WIN")"
WORK_MIX="${WORK_WIN//\\//}"
DRIVE="${WORK_WIN:0:2}"
mkdir -p "$WORK/gb/cases"
say "# work=$WORK_WIN"
pwsh.exe -NoProfile -File "$DEV_WIN\\tests\\run_sim_wsl.ps1" -Version "$ver" -Work "$WORK_WIN" -Exe "$EXE_WIN" -CtlPort 18050 -BasePort 18060 > "$WORK/harness.txt" 2>&1 &
hpid=$!
trap '[[ -e "$WORK/wsl_done" ]] || : > "$WORK/abort"; wait "$hpid" 2>/dev/null' EXIT

for _ in $(seq 1 300); do
  [[ -s "$WORK/ready.txt" ]] && break
  kill -0 "$hpid" 2>/dev/null || break
  sleep 0.2
done
if [[ ! -s "$WORK/ready.txt" ]]; then
  : > "$WORK/abort"; wait "$hpid"
  say "$(tr -d '\r' < "$WORK/harness.txt")"
  fail "시험 허브 준비 실패"; finish 1
fi
HUBW="$(sed -n 's/^HUB_DIR=//p' "$WORK/ready.txt" | tr -d '\r')"
CTL="$(sed -n 's/^CTL=//p' "$WORK/ready.txt" | tr -d '\r')"
P="$(sed -n 's/^PORT=//p' "$WORK/ready.txt" | tr -d '\r')"
say "# hub_dir=$HUBW ctl=$CTL port=$P"

# ---- 3. 케이스(run_sim_wsl.sh 와 같은 10개). cases.json 은 하네스(Windows 기준 호출)용, NN.bin 은 Git Bash 용(인자마다 NUL 로 끝남).
python3 - "$P" "$WORK/cases.json" "$WORK/expect.json" "$WORK/gb/cases" <<'PY'
import json, os, sys
P, cases_path, expect_path, bin_dir = sys.argv[1:5]
cases = [
    ("큰따옴표·작은따옴표", ["run", P, "echo \"dq 1\" 'sq \"2\"' it\"'\"s", "--json"], 0, ["dq 1 sq \"2\" it's"]),
    ("역슬래시", ["run", P, r"printf '%s|%s\n' 'a\b' a\\b", "--json"], 0, [r"a\b|a\b"]),
    ("$ 변수·$?·리터럴 $", ["run", P, "X=v1; false; echo \"$X ${X}x $? \\$X\" '$X'", "--json"], 0, ["v1 v1x 1 $X $X"]),
    ("파이프 |", ["run", P, "echo 'p|q' | grep -c 'p|q'", "--json"], 0, ["1"]),
    ("& 백그라운드·&&", ["run", P, "sleep 0 & echo 'a&b' && echo \"c&&d\"", "--json"], 0, ["a&b", "c&&d"]),
    ("한글", ["run", P, "echo '한글 테스트 ㄱㄴ' \"가나\"", "--json"], 0, ["한글 테스트 ㄱㄴ 가나"]),
    ("빈 인자(명령 자리 '') → 거부", ["run", P, "", "--json"], None, None),
    ("빈 인자를 옵션 값으로(--req-id '') → 거부", ["run", "--req-id", "", P, "echo x", "--json"], None, None),
    ("'-'로 시작하는 인자('--' 뒤 위치 인자 '-n' → 보드가 명령으로 실행)", ["run", "--json", "--timeout", "9", "--", P, "-n"], 127, ["sh: -n: not found"]),
    ("보드 rc 42 + 모든 특수문자 한 줄", ["run", P, "echo \"a'b\" 'c\"d' \\\\ \\$ \\| \\& 한; exit 42", "--json"], 42, ["a'b c\"d \\ $ | & 한"]),
]
json.dump([c[1] for c in cases], open(cases_path, "w", encoding="utf-8"), ensure_ascii=False)
json.dump([{"desc": c[0], "rc": c[2], "out": c[3]} for c in cases], open(expect_path, "w", encoding="utf-8"), ensure_ascii=False)
for i, c in enumerate(cases):
    with open(os.path.join(bin_dir, "%02d.bin" % (i + 1)), "wb") as f:
        f.write(b"".join(a.encode("utf-8") + b"\0" for a in c[1]))
PY
ncase="$(ls "$WORK/gb/cases" | grep -c '\.bin$')"

# Git Bash 쪽 준비물: 래퍼 사본(Windows 파일 시스템), 환경 파일, 실행 스크립트
cp "$UART_SRC" "$WORK/gb/uart"
q() { printf "'%s'" "${1//\'/\'\\\'\'}"; }
{
  echo "EXE_WIN=$(q "$EXE_WIN")"
  echo "HUBW=$(q "$HUBW")"
  echo "CTL=$(q "$CTL")"
  echo "P=$(q "$P")"
  echo "DRIVE=$(q "$DRIVE")"
  echo "WORK_MIX=$(q "$WORK_MIX")"
} > "$WORK/gb/env.sh"
cat > "$WORK/gb/run.sh" <<'GB'
#!/usr/bin/env bash
# (Git Bash 쪽 — run_sim_gitbash.sh 가 만든다) 인자: <작업 폴더(D:/… 형식)>
set -uo pipefail
W="$(cygpath -u "$1")"
. "$W/gb/env.sh"
UART="$W/gb/uart"
x() { printf '%s\n' "$*" >> "$W/gb/extra.txt"; }
: > "$W/gb/extra.txt"
x "# uname=$(uname -s) bash=$BASH_VERSION"
export UART_HUB_EXE="$EXE_WIN" UART_HUB_DIR="$(cygpath -u "$HUBW")" UART_HUB_CONTROL_PORT="$CTL"
unset UART_PATH_STYLE UART_WRAPPER_ECHO
x "# UART_HUB_DIR(Git Bash 형식)=$UART_HUB_DIR"

for f in "$W"/gb/cases/*.bin; do
  n="$(basename "$f" .bin)"
  mapfile -d '' argv < "$f"
  "$UART" "${argv[@]}" > "$W/gb/$n.out" 2> "$W/gb/$n.err"
  echo "$?" > "$W/gb/$n.rc"
done

# g1 rc 42
out="$("$UART" run "$P" 'exit 42')"; rc=$?
[[ $rc -eq 42 && -z "$out" ]] && x "[PASS] g1 래퍼 비JSON run 'exit 42' → 셸 종료코드 42" || x "[FAIL] g1 rc 42 전달 (rc=$rc out=$out)"
# g2 logpath D:/ + 파일 존재
lp="$("$UART" logpath "$P")"; rc=$?
[[ $rc -eq 0 && "$lp" == "$DRIVE/"* && -f "$lp" ]] && x "[PASS] g2 logpath → $lp ($DRIVE/ 로 시작, Git Bash 에서 파일 존재)" || x "[FAIL] g2 logpath (rc=$rc '$lp')"
# g3 logpath --json: log·raw·idx
lj="$("$UART" logpath "$P" --json)"; rc=$?
ok=1
for k in log raw idx; do
  v="$(printf '%s' "$lj" | sed -n "s/.*\"$k\":\"\([^\"]*\)\".*/\1/p")"
  [[ "$v" == "$DRIVE/"* && -f "$v" ]] || ok=0
done
[[ $rc -eq 0 && $ok -eq 1 ]] && x "[PASS] g3 logpath --json: log·raw·idx 모두 $DRIVE/ 이고 Git Bash 에서 파일 존재" || x "[FAIL] g3 logpath --json (rc=$rc $lj)"
# g4 hub status --json: supervisor.hub_dir = 혼합 형식
hs="$("$UART" hub status --json)"; rc=$?
hm="${HUBW//\\//}"
[[ $rc -eq 0 && "$hs" == *"\"hub_dir\":\"$hm\""* && "$hs" == *"\"log_root\":\"$DRIVE/"* ]] && x "[PASS] g4 hub status --json: hub_dir = $hm, log_root 도 $DRIVE/" || x "[FAIL] g4 hub status 경로 (rc=$rc $hs)"
# g5 '/'로 시작하는 인자(MSYS 가 경로로 바꾸기 쉬운 모양) 보존
sj="$("$UART" status /c/uart_hub_msys_probe --json)"; rc=$?
[[ $rc -eq 102 && "$sj" == *"'/c/uart_hub_msys_probe'"* ]] && x "[PASS] g5 '/'로 시작하는 인자 /c/uart_hub_msys_probe 그대로 전달(102 오류 문구에 그대로)" || x "[FAIL] g5 '/' 인자 (rc=$rc $sj)"
# g6 push <local> 상대 경로 → cygpath -w
printf 'hi\n' > "$W/gb/push_src.bin"
pe="$(cd "$W/gb" && UART_WRAPPER_ECHO=1 "$UART" push "$P" ./push_src.bin /tmp/x --json 2>&1 >/dev/null)"
expw="$(cygpath -w "$W/gb/push_src.bin")"
[[ "$pe" == *"argv[2]=$expw"* && "$pe" == *"argv[3]=/tmp/x"* ]] && x "[PASS] g6 push <local> 상대 경로 → cygpath -w ($expw), <remote> /tmp/x 그대로" || x "[FAIL] g6 push 경로 변환 ($pe)"
# g7 exe 없음 → 103
nj="$(UART_HUB_EXE='C:\uart_hub_nonexistent\UartHub.exe' "$UART" ls --json)"; rc=$?
[[ $rc -eq 103 && "$nj" == '{"status":"hub_down","exit":103,'* ]] && x "[PASS] g7 UartHub.exe 없음 → 103 hub_down(JSON)" || x "[FAIL] g7 exe 없음 (rc=$rc $nj)"
# g8·g9 래퍼가 TERM·INT 를 받음 → Windows cli(--parent-pipe)가 2초 안에 끝남
ncli() { MSYS_NO_PATHCONV=1 tasklist /fo csv /nh /fi "imagename eq UartHub.exe" 2>/dev/null | tr -d '\r' | grep -c '^"UartHub.exe"'; }
sig_case() {   # $1 = 번호 $2 = 신호
  local id=$1 sig=$2 base n t0 t1 i tp trc
  base="$(ncli)"
  timeout --foreground -s "$sig" 3 "$UART" wait "$P" --pattern NEVER_MATCH_GB --timeout 60 --json > "$W/gb/$id.out" 2> "$W/gb/$id.err" &
  tp=$!
  n=0
  for i in $(seq 1 40); do n="$(ncli)"; (( n > base )) && break; sleep 0.1; done
  wait "$tp"; trc=$?
  t0=$(date +%s%3N)
  t1=-1
  for i in $(seq 1 60); do n="$(ncli)"; if (( n <= base )); then t1=$(date +%s%3N); break; fi; sleep 0.05; done
  if (( t1 >= 0 && t1 - t0 <= 2000 && trc == 124 )); then x "[PASS] $id 래퍼 $sig → Windows cli $((t1 - t0))ms 안에 끝남(≤2000ms, --parent-pipe, UartHub.exe $base → $((base + 1)) → $base)"
  else x "[FAIL] $id 래퍼 $sig → Windows cli 가 2초 안에 끝나지 않음(t=$(( t1 >= 0 ? t1 - t0 : -1 ))ms timeout rc=$trc 남은 수 $n 기준 $base)"; fi
}
sig_case g8 TERM
sig_case g9 INT
# g11~g13(X6 에이징 사건): 보드가 찍은 줄(명령 에코에는 패턴이 없게 printf %s) → 허브의 사건 기록 → 래퍼로 읽기(경로 D:/…)
jv() { printf '%s' "$1" | sed -n "s/.*\"$2\":\"\([^\"]*\)\".*/\1/p"; }   # 그 키의 마지막 문자열 값
"$UART" run "$P" "printf 'Kernel %s - not syncing: gb g11\n' panic" --json > "$W/gb/g11_run.out"
ej=''
for i in $(seq 1 50); do ej="$("$UART" events "$P" --json --kind panic)"; [[ "$ej" == *'"kind":"panic"'* ]] && break; sleep 0.2; done
c11="$(jv "$ej" context)"; d11="$(jv "$ej" events_dir)"
[[ "$ej" == *"gb g11"* && "$ej" == *'"suspect":"cmd_output"'* && "$c11" == "$DRIVE/"* && -f "$c11" && "$d11" == "$DRIVE/"* ]] && x "[PASS] g11 events --json: run 출력의 panic 줄 → suspect:cmd_output 사건(명령 출력 속, 부록 C.6 — context·events_dir $DRIVE/, Git Bash 에서 앞뒤 로그 파일 존재)" || x "[FAIL] g11 events --json ($ej)"
aj="$("$UART" aging "$P" --json)"; rc=$?
an="$("$UART" aging "$P")"
c12="$(jv "$aj" context)"; f12="$(jv "$aj" events_file)"
[[ $rc -eq 0 && ( "$aj" == *'"panic":{"count":'[1-9]* || "$aj" == *'"panic":{"count":0,"first":null,"last":null,"no_context":0,"suspect":'[1-9]* ) && "$c12" == "$DRIVE/"* && -f "$c12" && "$f12" == "$DRIVE/"* && "$an" == *"부팅 "*"회: cmd "* && "$an" == *"앞뒤 로그 "* ]] \
  && x "[PASS] g12 aging --json(kinds.panic count 또는 suspect ≥1·contexts·events_file $DRIVE/, 파일 존재)·비JSON 요약" || x "[FAIL] g12 aging (rc=$rc $aj | $an)"
base="$(ncli)"
timeout --foreground -s TERM 10 "$UART" events "$P" --follow --json > "$W/gb/g13.out" 2> "$W/gb/g13.err" &
tp=$!
for i in $(seq 1 50); do [[ -s "$W/gb/g13.out" ]] && break; sleep 0.1; done
"$UART" run "$P" "printf 'Out of memory: %s process 1 (g13)\n' Kill" --json > "$W/gb/g13_run.out"
for i in $(seq 1 60); do grep -q '"kind":"oom"' "$W/gb/g13.out" && break; sleep 0.1; done
wait "$tp"; trc=$?
t0=$(date +%s%3N); t1=-1
for i in $(seq 1 100); do n="$(ncli)"; if (( n <= base )); then t1=$(date +%s%3N); break; fi; sleep 0.05; done
ol="$(grep '"kind":"oom"' "$W/gb/g13.out" | head -1)"; c13="$(jv "$ol" context)"
if [[ "$(head -1 "$W/gb/g13.out")" == *'"follow":true'* && "$ol" == *"g13"* && "$c13" == "$DRIVE/"* && -f "$c13" ]] && (( trc == 124 && t1 >= 0 && t1 - t0 <= 2000 )); then
  x "[PASS] g13 events --follow --json(| cat 경유): 머리 줄 뒤 새 oom 사건 한 줄(context $DRIVE/), 래퍼 TERM → Windows cli $((t1 - t0))ms 안에 끝남"
else x "[FAIL] g13 events --follow (timeout rc=$trc t=$(( t1 >= 0 ? t1 - t0 : -1 ))ms $(head -c 600 "$W/gb/g13.out"))"; fi
x "# done"
GB

say "# Git Bash: $GITBASH $WORK_MIX/gb/run.sh (표준 입력 /dev/null, 출력 파일 — 창 없음)"
t0=$(date +%s%3N)
"$GITBASH" "$WORK_MIX/gb/run.sh" "$WORK_MIX" < /dev/null > "$WORK/gb/console.txt" 2>&1
gbrc=$?
say "# Git Bash rc=$gbrc ($(( $(date +%s%3N) - t0 ))ms)"
[[ -s "$WORK/gb/console.txt" ]] && tr -d '\r' < "$WORK/gb/console.txt" | head -20 | while IFS= read -r l; do say "  gitbash: $l"; done
if [[ -f "$WORK/gb/extra.txt" ]]; then
  while IFS= read -r l; do
    say "$l"
    [[ "$l" == "[FAIL]"* ]] && fails=$((fails + 1))
  done < <(tr -d '\r' < "$WORK/gb/extra.txt")
else
  fail "Git Bash 쪽 결과(extra.txt) 없음"
fi
grep -qx '# done' <(tr -d '\r' < "$WORK/gb/extra.txt" 2>/dev/null) || fail "Git Bash 쪽 스크립트가 끝까지 돌지 않음"

# ---- 4. Windows 기준 호출 + 정리
: > "$WORK/wsl_done"
wait "$hpid"; hrc=$?
trap - EXIT
tr -d '\r' < "$WORK/harness.txt" | while IFS= read -r l; do say "  harness: $l"; done
[[ $hrc -eq 0 ]] || fail "하네스 rc=$hrc"
if grep -qx 'LEFTOVER_SUP=0' <(tr -d '\r' < "$WORK/cleanup.txt" 2>/dev/null) && grep -qx 'LEFTOVER_CORE=0' <(tr -d '\r' < "$WORK/cleanup.txt"); then
  say "[PASS] g10 시험이 띄운 감독·코어(UartHub.exe) 잔존 0"
else
  fail "g10 잔존 감독·코어 ($(tr '\n' ' ' < "$WORK/cleanup.txt" 2>/dev/null))"
fi

# ---- 5. 비교
python3 - "$WORK" "$ncase" > "$WORK/compare.txt" <<'PY'
import json, os, sys
work, n = sys.argv[1], int(sys.argv[2])
cases = json.load(open(os.path.join(work, "cases.json"), encoding="utf-8"))
expect = json.load(open(os.path.join(work, "expect.json"), encoding="utf-8"))
PATHS = {"log", "raw", "idx", "path", "log_root", "hub_dir", "config", "core_config"}
TIMES = {"since_ms", "b_ms", "uptime_s", "started", "updated", "core_started", "last_exit_at", "next_start_in_s"}
ORDER = {"rx_offset", "raw_offset", "raw_end", "req_id", "run_id"}
def rd(side, num, ext):
    with open(os.path.join(work, side, f"{num}.{ext}"), encoding="utf-8", newline="") as f:
        return f.read()
def strip(o, w, where, bad):
    # o: Git Bash 값, w: Windows 값. 경로 필드는 w 의 '\' → '/' == o 확인 뒤 제거, 시각·순서 필드는 제거.
    if isinstance(o, dict) and isinstance(w, dict):
        a, b = {}, {}
        for k in set(o) | set(w):
            if k in PATHS and isinstance(o.get(k), str) and isinstance(w.get(k), str) and len(w[k]) > 2 and w[k][1] == ":":
                if w[k].replace("\\", "/") != o[k]: bad.append(f"{where}{k}: mixed({w[k]}) != {o[k]}")
                continue
            if k in TIMES or k in ORDER: continue
            a[k], b[k] = strip(o.get(k), w.get(k), where + k + ".", bad)
        return a, b
    if isinstance(o, list) and isinstance(w, list) and len(o) == len(w):
        pairs = [strip(x, y, where, bad) for x, y in zip(o, w)]
        return [p[0] for p in pairs], [p[1] for p in pairs]
    return o, w
for i in range(n):
    num = f"{i + 1:02d}"
    e = expect[i]
    tag = f"{num} {e['desc']}"
    try:
        go, gr, ge = rd("gb", num, "out"), int(rd("gb", num, "rc")), rd("gb", num, "err")
        no, nr, ne = rd("win", num, "out"), int(rd("win", num, "rc")), rd("win", num, "err")
    except Exception as ex:
        print(f"[FAIL] {tag}: 결과 파일 없음 ({ex})"); continue
    why = []
    if gr != nr: why.append(f"rc Git Bash {gr} != Windows {nr}")
    if ge != ne: why.append(f"stderr 다름 Git Bash={ge!r} Windows={ne!r}")
    if "\r" in go: why.append("Git Bash stdout 에 CR")
    try:
        jo, jw = json.loads(go), json.loads(no)
    except Exception as ex:
        why.append(f"JSON 아님 ({ex}) Git Bash={go!r} Windows={no!r}"); jo = jw = None
    if jo is not None:
        bad = []
        a, b = strip(jo, jw, "", bad)
        why += bad
        if a != b: why.append("JSON 다름\n      Git Bash " + json.dumps(a, ensure_ascii=False, sort_keys=True) + "\n      Windows  " + json.dumps(b, ensure_ascii=False, sort_keys=True))
        if e["rc"] is not None and (gr != e["rc"] or jo.get("rc") != e["rc"]): why.append(f"기대 rc {e['rc']} (Git Bash 종료코드 {gr}, json rc {jo.get('rc')})")
        if e["rc"] is None and not (100 <= gr <= 106 and jo.get("exit") == gr): why.append(f"도구 오류(100~106)를 기대했는데 rc {gr}")
        if e["out"] is not None and jo.get("out") != e["out"]: why.append(f"기대 out {e['out']!r} != {jo.get('out')!r}")
    argv = json.dumps(cases[i], ensure_ascii=False)
    if why:
        print(f"[FAIL] {tag} argv={argv}")
        for w in why: print("    " + w)
    else:
        summ = f"rc={gr} status={jo.get('status')}" + (f" out={json.dumps(jo.get('out'), ensure_ascii=False)}" if "out" in jo else "") + (f" error={jo.get('error')!r}" if jo.get("error") else "")
        print(f"[PASS] {tag}: Git Bash == Windows ({summ}) argv={argv}")
PY
while IFS= read -r l; do
  say "$l"
  [[ "$l" == "[FAIL]"* ]] && fails=$((fails + 1))
done < "$WORK/compare.txt"
npass="$(grep -cE '^\[PASS\] [0-9]{2} ' "$res")"
say "합계: 케이스 $npass/$ncase PASS, 부가 검사 $(grep -cE '^\[PASS\] g[0-9]+ ' "$res")/13 PASS, FAIL $fails"
(( fails == 0 )) && finish 0
finish 1
