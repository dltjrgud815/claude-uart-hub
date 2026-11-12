#!/usr/bin/env bash
# WSL 계약 테스트(계획 §4 S6 "WSL 계약 테스트", 팀 배포 계획 C-7 래퍼 v2). 하드웨어 없음(FakeBoard), 실제 COM 미사용.
#   사용: tests/run_sim_wsl.sh [--quiet] [--exe-version <ver>]
#   --exe-version <ver>(또는 환경변수 UART_HUB_USE_BUILD=<ver>): 다시 빌드하지 않고 지정 빌드(build.sh --use)로 돈다(run_sim.sh 와 같은 규약).
#   --quiet: 화면에는 [FAIL] 줄과 '# fail N'만 낸다(run_sim.sh 와 같은 규약). 전체 출력은 결과 파일에 남는다.
# 순서
#   1. build.sh(dotnet → 개발 사본 %LOCALAPPDATA%\uart_hub_dev\<ver>\, 실행 정책 변경 없음)
#   2. tests/run_sim_wsl.ps1 을 백그라운드로 띄운다: 시험 허브(UartHub.exe sup --no-tray --sim → 코어 안 FakeBoard, 제어 17400·케이블 17450,
#      mutex Local\uart_hub_test_*_wsl_*, 폴더 %TEMP%\uart_hub_test\wsl_<시각>\) 준비 → ready.txt
#   3. 케이스 10개(" ' \ $ | & 한글 빈 인자 '-'로 시작하는 인자)를 래퍼 v2(claude/uart/hub/skill/uart → UartHub.exe cli --parent-pipe --b64nul)로 실행 + 부가 검사
#      (래퍼 rc 42 전달, logpath /mnt/, hub status 경로, push 경로 변환, exe 없음 103, x10 래퍼 TERM·INT → Windows cli ≤2초,
#       X6: x11 events --json(보드가 찍은 panic 줄 → context /mnt/ 파일 있음) · x12 aging --json·비JSON · x13 events --follow --json(새 oom 줄, 래퍼 TERM → cli ≤2초))
#   4. wsl_done → 하네스가 같은 케이스를 UartHub.exe cli 프로세스(ArgumentList, interop 없음)로 실행 → 정리(잔존 감독·코어 0 확인) 후 종료
#   5. 비교: 종료코드 같음, stderr 같음, JSON 은 아래 제외 필드를 뺀 뒤 같음. 경로 필드는 Windows 값을 wslpath -u 로 바꾼 값과 같아야 함.
#      제외: 시각(since_ms b_ms uptime_s started updated core_started last_exit_at next_start_in_s)
#            · 호출 순서에 따라 달라지는 위치·식별자(rx_offset raw_offset raw_end req_id run_id) — 두 호출이 같은 링을 차례로 쓰므로 값이 다를 수밖에 없다.
# 결과: tests/results/wsl_YYYYMMDD_HHMMSS.txt. 종료코드: 모두 PASS 면 0, 아니면 1.
set -uo pipefail
HUB_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
UART="$HUB_DIR/skill/uart"
RES_DIR="$HUB_DIR/tests/results"
mkdir -p "$RES_DIR"
ts="$(date +%Y%m%d_%H%M%S)"
res="$RES_DIR/wsl_$ts.txt"
quiet=0
use="${UART_HUB_USE_BUILD:-}"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --quiet) quiet=1; shift ;;
    --exe-version) [[ $# -ge 2 ]] || { echo "run_sim_wsl: $1 에 값이 필요" >&2; exit 2; }; use="$2"; shift 2 ;;
    *) break ;;
  esac
done
: > "$res"

say() { printf '%s\n' "$*" >> "$res"; (( quiet )) || printf '%s\n' "$*"; }
fails=0
pass() { say "[PASS] $*"; }
fail() { say "[FAIL] $*"; fails=$((fails + 1)); }
finish() {
  local rc=$1
  if (( quiet )); then
    grep -E '^\[FAIL\]' "$res"
    echo "# fail $(grep -cE '^\[FAIL\]' "$res")"
    (( rc != 0 )) && echo "run_sim_wsl rc=$rc (결과 파일: $res)"
  else
    echo "결과 파일: $res"
  fi
  exit "$rc"
}

say "# run_sim_wsl $(date '+%F %T')${use:+ exe-version=$use}"
[[ -x "$UART" ]] || { fail "래퍼 없음: $UART"; finish 1; }

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

# ---- 2. 하네스(시험 허브)
tmp_win="$(cmd.exe /c 'echo %TEMP%' 2>/dev/null | tr -d '\r')"
[[ -n "$tmp_win" && "$tmp_win" != *%* ]] || { fail "%TEMP% 를 얻지 못함"; finish 1; }
WORK_WIN="$tmp_win\\uart_hub_test\\wsl_$ts"
WORK="$(wslpath -u "$WORK_WIN")"
mkdir -p "$WORK/wsl"
say "# work=$WORK_WIN"
pwsh.exe -NoProfile -File "$DEV_WIN\\tests\\run_sim_wsl.ps1" -Version "$ver" -Work "$WORK_WIN" -Exe "$EXE_WIN" > "$WORK/harness.txt" 2>&1 &
hpid=$!
# 중간에 끊겨도 하네스가 허브를 정리하고 끝나게 한다
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
HUBU="$(wslpath -u "$HUBW")"
say "# hub_dir=$HUBW ctl=$CTL port=$P"

# 래퍼 환경: 개발 사본 UartHub.exe, 허브 폴더는 WSL 형식으로 준다(래퍼가 Windows 형식으로 바꿔 넘기는 것도 함께 확인)
export UART_HUB_EXE="$EXE_WIN" UART_HUB_DIR="$HUBU" UART_HUB_CONTROL_PORT="$CTL"
unset UART_PATH_STYLE UART_WSL_MOUNT UART_WRAPPER_ECHO

# ---- 3. 케이스(10). 마지막 인자들은 모두 --json. 기대 out 은 FakeBoard(미니 ash) 기준.
python3 - "$P" "$WORK/cases.json" "$WORK/expect.json" <<'PY'
import json, sys
P, cases_path, expect_path = sys.argv[1:4]
cases = [
    # (설명, argv, 기대 rc, 기대 out 또는 None)
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
PY
ncase="$(python3 -c 'import json,sys; print(len(json.load(open(sys.argv[1]))))' "$WORK/cases.json")"
for ((i = 0; i < ncase; i++)); do
  n="$(printf '%02d' $((i + 1)))"
  mapfile -d '' argv < <(python3 -c 'import json,sys; sys.stdout.write("\0".join(json.load(open(sys.argv[1]))[int(sys.argv[2])]) + "\0")' "$WORK/cases.json" "$i")
  "$UART" "${argv[@]}" > "$WORK/wsl/$n.out" 2> "$WORK/wsl/$n.err"
  echo "$?" > "$WORK/wsl/$n.rc"
done

# ---- 3'. 부가 검사(래퍼 단독)
out="$("$UART" run "$P" 'exit 42')"; rc=$?
[[ $rc -eq 42 && -z "$out" ]] && pass "x1 래퍼 비JSON run 'exit 42' → 셸 종료코드 42" || fail "x1 래퍼 rc 42 전달 (rc=$rc out=$out)"
lp="$("$UART" logpath "$P")"; rc=$?
[[ $rc -eq 0 && "$lp" == /mnt/* && -f "$lp" ]] && pass "x2 logpath → $lp (/mnt/ 로 시작, WSL 에서 파일 존재)" || fail "x2 logpath (rc=$rc '$lp')"
lj="$("$UART" logpath "$P" --json)"; rc=$?
python3 - "$lj" <<'PY' && pass "x3 logpath --json: log·raw·idx 모두 /mnt/ 이고 WSL 에서 파일 존재" || fail "x3 logpath --json ($lj)"
import json, os, sys
j = json.loads(sys.argv[1])
ok = all(isinstance(j.get(k), str) and j[k].startswith("/mnt/") and os.path.isfile(j[k]) for k in ("log", "raw", "idx"))
sys.exit(0 if ok and j["exit"] == 0 else 1)
PY
hs="$("$UART" hub status --json)"; rc=$?
python3 - "$hs" "$HUBU" <<'PY' && pass "x4 hub status --json: supervisor.hub_dir = $HUBU, log_root·config·core_config 도 /mnt/" || fail "x4 hub status 경로 ($hs)"
import json, sys
j, hubu = json.loads(sys.argv[1]), sys.argv[2]
s = j.get("supervisor") or {}
ok = j.get("exit") == 0 and s.get("hub_dir") == hubu and j.get("log_root", "").startswith("/mnt/") \
     and s.get("config", "").startswith(hubu + "/") and s.get("core_config", "").startswith(hubu + "/")
sys.exit(0 if ok else 1)
PY
hsn="$("$UART" hub status)"; rc=$?
grep -qxF "supervisor.hub_dir=$HUBU" <<<"$hsn" && pass "x5 hub status(비JSON): supervisor.hub_dir=$HUBU" || fail "x5 hub status 비JSON ($hsn)"
pf="$WORK/wsl/push_src.bin"; printf 'hi\n' > "$pf"
( cd "$WORK/wsl" && UART_WRAPPER_ECHO=1 "$UART" push "$P" ./push_src.bin /tmp/x --json 2>&1 >/dev/null ) > "$WORK/wsl/push_echo.txt"
expw="$(wslpath -w "$pf")"
python3 - "$WORK/wsl/push_echo.txt" "$expw" <<'PY' && pass "x6 push <local> 상대 경로 → wslpath -w ($expw)" || fail "x6 push 경로 변환 ($(cat "$WORK/wsl/push_echo.txt"))"
import sys
a = dict(l.rstrip("\n").split("=", 1) for l in open(sys.argv[1], encoding="utf-8") if l.startswith("argv["))
sys.exit(0 if a.get("argv[2]") == sys.argv[2] and a.get("argv[3]") == "/tmp/x" else 1)
PY
pj="$(cd "$WORK/wsl" && "$UART" push "$P" ./push_src.bin /tmp/x --json)"; rc=$?
python3 - "$pj" "$rc" <<'PY' && pass "x7 push(Phase B 미구현) → 허브 응답 106 rejected 를 그대로 전달(rc 106)" || fail "x7 push 106 전달 (rc=$rc $pj)"
import json, sys
j = json.loads(sys.argv[1])
sys.exit(0 if int(sys.argv[2]) == 106 and j.get("exit") == 106 and j.get("status") == "rejected" else 1)
PY
nj="$(UART_HUB_EXE='C:\uart_hub_nonexistent\UartHub.exe' "$UART" ls --json)"; rc=$?
[[ $rc -eq 103 && "$nj" == '{"status":"hub_down","exit":103,'* ]] && pass "x8 UartHub.exe 없음 → 103 hub_down(JSON)" || fail "x8 exe 없음 (rc=$rc $nj)"

# x10: 래퍼가 TERM·INT 를 받으면 Windows cli 가 2초 안에 끝남(--parent-pipe — 래퍼가 쥔 파이프가 닫혀 exe 가 EOF). WinExe 에는 신호가 가지 않는다(K4).
#      timeout --foreground 는 래퍼 bash 에만 신호를 보낸다(WSL 중계 프로세스는 그대로 — 래퍼의 파이프만으로 끝나는지 본다). UartHub.exe 개수(시험 허브 감독·코어 + cli)로 판정.
ncli() { tasklist.exe /fo csv /nh /fi "imagename eq UartHub.exe" 2>/dev/null | tr -d '\r' | grep -c '^"UartHub.exe"'; }
x10=()
for sig in TERM INT; do
  base="$(ncli)"
  timeout --foreground -s "$sig" 3 "$UART" wait "$P" --pattern NEVER_MATCH_X10 --timeout 60 --json > "$WORK/wsl/x10_$sig.out" 2> "$WORK/wsl/x10_$sig.err" &
  tp=$!
  n=0
  for _ in $(seq 1 40); do n="$(ncli)"; (( n > base )) && break; sleep 0.1; done
  wait "$tp"; trc=$?
  t0=$(date +%s%3N); t1=-1
  for _ in $(seq 1 100); do n="$(ncli)"; if (( n <= base )); then t1=$(date +%s%3N); break; fi; sleep 0.05; done
  if (( t1 >= 0 && t1 - t0 <= 2000 && trc == 124 )); then x10+=("$sig $((t1 - t0))ms")
  else x10+=("$sig 실패(t=$(( t1 >= 0 ? t1 - t0 : -1 ))ms timeout rc=$trc 남은 수 $n 기준 $base)"); fi
done
if [[ "${x10[*]}" != *실패* ]]; then pass "x10 래퍼 TERM·INT → Windows cli 가 2초 안에 끝남(${x10[*]}, --parent-pipe)"
else fail "x10 래퍼 신호 → Windows cli 정리 (${x10[*]})"; fi

# x11~x13(X6 에이징 사건): 보드가 찍은 줄(명령 에코에는 패턴이 없게 printf %s)을 허브가 사건으로 기록 → 래퍼로 읽기(경로 /mnt/)
"$UART" run "$P" "printf 'Kernel %s - not syncing: wsl x11\n' panic" --json > "$WORK/wsl/x11_run.out"
ej=''
for _ in $(seq 1 50); do
  ej="$("$UART" events "$P" --json --kind panic)"
  [[ "$ej" == *'"kind":"panic"'* ]] && break
  sleep 0.2
done
python3 - "$ej" <<'PY2' && pass "x11 events --json: run 출력의 panic 줄 → suspect:cmd_output 사건(명령 출력 속, 부록 C.6 — context·log·events_dir 모두 /mnt/, WSL 에서 앞뒤 로그 파일 존재)" || fail "x11 events --json ($ej)"
import json, os, sys
j = json.loads(sys.argv[1])
e = [x for x in j.get("events", []) if x.get("kind") == "panic"]
ok = j.get("exit") == 0 and j.get("events_dir", "").startswith("/mnt/") and len(e) >= 1 and "wsl x11" in e[-1].get("line", "") \
     and e[-1].get("context", "").startswith("/mnt/") and os.path.isfile(e[-1]["context"]) and e[-1].get("log", "").startswith("/mnt/") and e[-1].get("suspect") == "cmd_output"
sys.exit(0 if ok else 1)
PY2
aj="$("$UART" aging "$P" --json)"; rc=$?
an="$("$UART" aging "$P")"
python3 - "$aj" "$rc" <<'PY2' && [[ "$an" == *"부팅 "*"회: cmd "* && "$an" == *"앞뒤 로그 "* ]] && pass "x12 aging --json(kinds.panic count+suspect ≥1·contexts /mnt/ 파일 존재·events_file /mnt/)·비JSON 요약" || fail "x12 aging (rc=$rc $aj | $an)"
import json, os, sys
j = json.loads(sys.argv[1])
cs = j.get("contexts", [])
pk = j.get("kinds", {}).get("panic", {})
ok = int(sys.argv[2]) == 0 and pk.get("count", 0) + pk.get("suspect", 0) >= 1 and j.get("events_file", "").startswith("/mnt/") \
     and len(cs) >= 1 and all(c.get("context", "").startswith("/mnt/") and os.path.isfile(c["context"]) for c in cs)
sys.exit(0 if ok else 1)
PY2
base="$(ncli)"
timeout --foreground -s TERM 10 "$UART" events "$P" --follow --json > "$WORK/wsl/x13.out" 2> "$WORK/wsl/x13.err" &
tp=$!
for _ in $(seq 1 50); do [[ -s "$WORK/wsl/x13.out" ]] && break; sleep 0.1; done
"$UART" run "$P" "printf 'Out of memory: %s process 1 (x13)\n' Kill" --json > "$WORK/wsl/x13_run.out"
for _ in $(seq 1 60); do grep -q '"kind":"oom"' "$WORK/wsl/x13.out" && break; sleep 0.1; done
wait "$tp"; trc=$?
t0=$(date +%s%3N); t1=-1
for _ in $(seq 1 100); do n="$(ncli)"; if (( n <= base )); then t1=$(date +%s%3N); break; fi; sleep 0.05; done
python3 - "$WORK/wsl/x13.out" <<'PY2' && (( trc == 124 && t1 >= 0 && t1 - t0 <= 2000 )) && pass "x13 events --follow --json: 머리 줄 뒤 새 oom 사건 한 줄(context /mnt/), 래퍼 TERM → Windows cli $((t1 - t0))ms 안에 끝남" || fail "x13 events --follow (timeout rc=$trc t=$(( t1 >= 0 ? t1 - t0 : -1 ))ms $(head -c 600 "$WORK/wsl/x13.out"))"
import json, os, sys
ls = [json.loads(l) for l in open(sys.argv[1], encoding="utf-8") if l.strip()]
ok = len(ls) >= 2 and ls[0].get("follow") is True and ls[0].get("events_dir", "").startswith("/mnt/") \
     and any(x.get("kind") == "oom" and "x13" in x.get("line", "") and x.get("context", "").startswith("/mnt/") and os.path.isfile(x["context"]) for x in ls[1:])
sys.exit(0 if ok else 1)
PY2

# ---- 4. Windows 기준 호출 + 정리
: > "$WORK/wsl_done"
wait "$hpid"; hrc=$?
trap - EXIT
tr -d '\r' < "$WORK/harness.txt" | while IFS= read -r l; do say "  harness: $l"; done
[[ $hrc -eq 0 ]] || fail "하네스 rc=$hrc"
if grep -qx 'LEFTOVER_SUP=0' "$WORK/cleanup.txt" 2>/dev/null && grep -qx 'LEFTOVER_CORE=0' "$WORK/cleanup.txt"; then
  pass "x9 시험이 띄운 감독·코어(UartHub.exe) 잔존 0"
else
  fail "x9 잔존 감독·코어 ($(tr '\n' ' ' < "$WORK/cleanup.txt" 2>/dev/null))"
fi

# ---- 5. 비교
python3 - "$WORK" "$ncase" > "$WORK/compare.txt" <<'PY'
import json, os, subprocess, sys
work, n = sys.argv[1], int(sys.argv[2])
cases = json.load(open(os.path.join(work, "cases.json"), encoding="utf-8"))
expect = json.load(open(os.path.join(work, "expect.json"), encoding="utf-8"))
PATHS = {"log", "raw", "idx", "path", "log_root", "hub_dir", "config", "core_config"}
TIMES = {"since_ms", "b_ms", "uptime_s", "started", "updated", "core_started", "last_exit_at", "next_start_in_s"}
ORDER = {"rx_offset", "raw_offset", "raw_end", "req_id", "run_id"}
def rd(side, num, ext):
    with open(os.path.join(work, side, f"{num}.{ext}"), encoding="utf-8") as f:
        return f.read()
def u(p):
    return subprocess.run(["wslpath", "-u", p], capture_output=True, text=True).stdout.rstrip("\n")
def strip(o, w, where, bad):
    # o: WSL 값, w: Windows 값. 경로 필드는 wslpath -u(w) == o 확인 뒤 제거, 시각·순서 필드는 제거.
    if isinstance(o, dict) and isinstance(w, dict):
        a, b = {}, {}
        for k in set(o) | set(w):
            if k in PATHS and isinstance(o.get(k), str) and isinstance(w.get(k), str) and len(w[k]) > 2 and w[k][1] == ":":
                if u(w[k]) != o[k]: bad.append(f"{where}{k}: wslpath -u({w[k]}) != {o[k]}")
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
        wo, wr, we = rd("wsl", num, "out"), int(rd("wsl", num, "rc")), rd("wsl", num, "err")
        no, nr, ne = rd("win", num, "out"), int(rd("win", num, "rc")), rd("win", num, "err")
    except Exception as ex:
        print(f"[FAIL] {tag}: 결과 파일 없음 ({ex})"); continue
    why = []
    if wr != nr: why.append(f"rc WSL {wr} != Windows {nr}")
    if we != ne: why.append(f"stderr 다름 WSL={we!r} Windows={ne!r}")
    if "\r" in wo: why.append("WSL stdout 에 CR")
    try:
        jo, jw = json.loads(wo), json.loads(no)
    except Exception as ex:
        why.append(f"JSON 아님 ({ex}) WSL={wo!r} Windows={no!r}"); jo = jw = None
    if jo is not None:
        bad = []
        a, b = strip(jo, jw, "", bad)
        why += bad
        if a != b: why.append("JSON 다름\n      WSL     " + json.dumps(a, ensure_ascii=False, sort_keys=True) + "\n      Windows " + json.dumps(b, ensure_ascii=False, sort_keys=True))
        if e["rc"] is not None and (wr != e["rc"] or jo.get("rc") != e["rc"]): why.append(f"기대 rc {e['rc']} (WSL 종료코드 {wr}, json rc {jo.get('rc')})")
        if e["rc"] is None and not (100 <= wr <= 106 and jo.get("exit") == wr): why.append(f"도구 오류(100~106)를 기대했는데 rc {wr}")
        if e["out"] is not None and jo.get("out") != e["out"]: why.append(f"기대 out {e['out']!r} != {jo.get('out')!r}")
    argv = json.dumps(cases[i], ensure_ascii=False)
    if why:
        print(f"[FAIL] {tag} argv={argv}")
        for w in why: print("    " + w)
    else:
        summ = f"rc={wr} status={jo.get('status')}" + (f" out={json.dumps(jo.get('out'), ensure_ascii=False)}" if "out" in jo else "") + (f" error={jo.get('error')!r}" if jo.get("error") else "")
        print(f"[PASS] {tag}: WSL == Windows ({summ}) argv={argv}")
PY
while IFS= read -r l; do
  say "$l"
  [[ "$l" == "[FAIL]"* ]] && fails=$((fails + 1))
done < "$WORK/compare.txt"
npass="$(grep -cE '^\[PASS\] [0-9]{2} ' "$res")"
say "합계: 케이스 $npass/$ncase PASS, 부가 검사 $(grep -cE '^\[PASS\] x[0-9]' "$res")/13 PASS, FAIL $fails"
(( fails == 0 )) && finish 0
finish 1
