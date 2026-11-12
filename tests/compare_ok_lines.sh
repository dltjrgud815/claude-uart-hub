#!/usr/bin/env bash
# 시뮬 결과의 "ok:" 줄 집합을 기준(아래 BASE)과 비교한다(계획 T1-0·T1 검증 "기대값 불변").
#   사용: tests/compare_ok_lines.sh <결과 파일>            차이를 출력하고, 차이가 0 이면 exit 0(아니면 1)
#         tests/compare_ok_lines.sh --make <결과 파일>     정규화한 집합을 표준 출력으로(기준 파일 만들기)
# 대상: 기준 파일 머리 "# ids=" 의 시나리오 ID(기준 결과의 # registered=, 72개)에 속한 "    ok: …" 줄만. 새 시나리오의 줄은 보지 않는다.
# 정규화(실행마다 달라지는 값만 자리표시로): 임시 경로·빌드 버전(17자리)·16진 난수(GUID·req_id·u+16진)·pid·포트 번호·
#   ISO 시각·파일명 시각(_HHMMSS[_n])·링 epoch(13자리[:위치])·hub.log 줄 번호·받은/읽은 줄 수·시간 JSON 필드(b_ms since_ms uptime_s idle_s …)·
#   N ms·N(.n)초·소수·쓰기 실패 RX 바이트 수. 규칙은 아래 R 하나뿐이며 기준 파일도 이 규칙으로 만든다.
set -uo pipefail
HUB_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# 기준 파일: UART_HUB_BASELINE → tests/baseline_ok_lines.txt → 비공개 internal/baseline_ok_lines.txt(옛 Phase A 기준, 공개 저장소에는 없음). 없으면 --make 로 만든다.
BASE="${UART_HUB_BASELINE:-$HUB_DIR/tests/baseline_ok_lines.txt}"
[[ -f "$BASE" || -n "${UART_HUB_BASELINE:-}" ]] || BASE="$HUB_DIR/internal/baseline_ok_lines.txt"
mode=compare
if [[ "${1:-}" == "--make" ]]; then mode=make; shift; fi
[[ $# -eq 1 && -f "$1" ]] || { echo "사용: $0 [--make] <결과 파일>" >&2; exit 2; }
[[ $mode == make || -f "$BASE" ]] || { echo "기준 파일 없음: $BASE" >&2; exit 2; }

python3 -I - "$mode" "$1" "$BASE" <<'PY'
import re, sys
mode, res_path, base_path = sys.argv[1:4]

R = [
    (r'[A-Za-z]:\\[^\s"\',)]*uart_hub_test[^\s"\',)]*', '<TMP>'),
    (r'(?:[A-Za-z]:|/mnt/[a-z])[\\/][^\s"\',)]*[Tt]emp[\\/][^\s"\',)]*', '<TMP>'),
    (r'\b\d{17}\b', '<VER>'),
    (r'\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b', '<HEX>'),
    (r'"(req_id|run_id)":"[^"]*"', r'"\1":"<HEX>"'),
    (r'(?<![0-9A-Za-z])u[0-9a-f]{5,8}(?![0-9A-Za-z])', '<HEX>'),
    (r'\b(?=[0-9a-f]*[a-f])(?=[0-9a-f]*[0-9])[0-9a-f]{6,}\b', '<HEX>'),
    (r'((?:core_|sup_|_)?pid["=: ]+)\d+', r'\1<PID>'),
    (r'127\.0\.0\.1:\d+', '<LOOPBACK>:<PORT>'),
    (r'("?(?:control_|tcp_|base_)?port"?[=: ]+)\d+', r'\1<PORT>'),
    (r'\b\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d+)?', '<TIME>'),
    (r'_\d{6}(?:_\d+)?\.(log|raw|idx)\b', r'_<HHMMSS>.\1'),
    (r'\b\d{13}:\d+', '<EPOCH>:<N>'),
    (r'\b\d{13}\b', '<EPOCH>'),
    (r'(줄 )\d+', r'\1<N>'),
    (r'\d+(?=줄)', '<N>'),
    (r'"(b_ms|since_ms|uptime_s|start_ticks|age_ms|idle_ms|idle_s)":\d+(?:\.\d+)?', r'"\1":<N>'),
    (r'(RX )\d+B', r'\1<N>B'),
    (r'\b\d+ms\b', '<N>ms'),
    (r'\b\d+(?:\.\d+)?(?=초)', '<F>'),
    (r'\d+\.\d+', '<F>'),
]

def norm(s):
    for a, b in R:
        s = re.sub(a, b, s)
    return s

def parse(p):
    reg, cur, ok, status = None, None, [], {}
    with open(p, encoding='utf-8', errors='replace') as f:
        for l in f:
            l = l.rstrip('\n').rstrip('\r')
            if l.startswith('# registered='):
                reg = [x for x in l.split('=', 1)[1].split(',') if x]
            m = re.match(r'^\[(PASS|FAIL)\] (\S+)', l)
            if m:
                cur = m.group(2); status[cur] = m.group(1); continue
            if l.startswith('    ok: ') and cur:
                ok.append((cur, l[8:]))
    return reg, ok, status

reg, ok, status = parse(res_path)
if mode == 'make':
    if not reg: sys.exit('결과 파일에 # registered= 없음')
    print('# ok: 줄 정규화 집합(tests/compare_ok_lines.sh 규칙). 원본=' + res_path.replace('\\', '/').split('/')[-1] + ' ids=' + str(len(reg)))
    print('# ids=' + ','.join(reg))
    for i, l in sorted(set((i, norm(l)) for i, l in ok if i in reg)):
        print(i + '\t' + l)
    sys.exit(0)

ids, base = None, set()
with open(base_path, encoding='utf-8') as f:
    for l in f:
        l = l.rstrip('\n')
        if l.startswith('# ids='): ids = l[6:].split(','); continue
        if l.startswith('#') or not l: continue
        base.add(tuple(l.split('\t', 1)))
if not ids: sys.exit('기준 파일에 # ids= 없음')
got = set((i, norm(l)) for i, l in ok if i in ids)
missing = [i for i in ids if i not in status]
notpass = [i for i in ids if status.get(i) == 'FAIL']
only_base = sorted(base - got)
only_got = sorted(got - base)
print('# 비교: 결과=' + res_path + ' 기준=' + base_path)
print('# 기준 ID %d개 · 결과에 없는 ID %d · FAIL %d · 기준 줄 %d · 결과 줄 %d' % (len(ids), len(missing), len(notpass), len(base), len(got)))
if missing: print('# 결과에 없는 ID: ' + ','.join(missing))
if notpass: print('# FAIL ID: ' + ','.join(notpass))
for i, l in only_base: print('- ' + i + '\t' + l)
for i, l in only_got: print('+ ' + i + '\t' + l)
print('# 차이 %d (기준에만 %d · 결과에만 %d)' % (len(only_base) + len(only_got), len(only_base), len(only_got)))
sys.exit(0 if not only_base and not only_got and not missing else 1)
PY
