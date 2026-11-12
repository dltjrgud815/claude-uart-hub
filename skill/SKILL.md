---
name: uart
description: 보드 UART 시리얼 콘솔을 UART 허브(Windows 트레이에 상주하며 TeraTerm·PuTTY 같은 사람 터미널과 같은 콘솔을 공유)로 다룬다. 보드에서 명령을 실행하고 출력과 종료코드를 받을 때(run), 보드를 재부팅하고 부팅이 끝날 때까지 기다릴 때(reboot·wait), 케이블·보드 상태를 볼 때(ls·status·probe), 콘솔 로그 파일 위치나 최근 줄이 필요할 때(logpath·log) 사용. "EVB-A1 보드에서 uname 해줘", "보드 재부팅하고 부팅 끝나면 알려줘", "UART 콘솔 로그 봐 줘", "7002 보드에 명령 쳐 줘", "COM5 보드 상태 확인해 줘" 같은 요청. 에이징 세션을 시작·끝내고(session start·stop — 이름을 붙인 폴더에 콘솔 로그·이벤트·요약) 기록 중인지 볼 때(session status), 세션 중 panic·앱 오류를 지켜보거나(events --follow) 결과를 정리할 때(session summary)도 — "7001 에이징 시작해줘, 이름은 야간", "7001 에이징 끝내고 정리해줘", "지금 에이징 기록 중이야?", "에이징 걸고 panic 나면 알려줘", "어젯밤 에이징 결과 정리해줘".
---

# UART 허브 — 보드 콘솔 사용 규칙

UART 허브(`UartHub.exe`, Windows 트레이 아이콘)가 보드 케이블(COM)을 잡고, 케이블마다 고정 TCP 포트(`127.0.0.1:70xx`)를 연다.
**허브는 사용자가 `/connect-uart COMx` 로 연결한 케이블만 잡는다**(꽂기만 해서는 잡지 않는다). 끊기는 `/disconnect-uart COMx`. 둘 다 사용자만 부른다.
사람은 연결 때 뜬 터미널이나 바탕화면 보드 아이콘(`<보드> (<포트>)`)으로, Claude 는 아래 `uart` 명령으로 **같은 콘솔**을 쓴다.
**입력 우선권은 사람에게 있다.** Claude 의 입력은 셸이 프롬프트에 있고 사람이 치고 있지 않을 때만 보드로 나간다. 아래 규칙은 이 전제를 깨지 않기 위한 것이다.

## 명령 위치

```bash
U=~/.claude/skills/uart/uart      # WSL 과 Windows(Git Bash) 공통. 어느 폴더에서 실행해도 된다
$U ls --json
```

- WSL Claude Code 는 그대로, Windows Claude Code 는 Bash 도구(Git Bash)에서 쓴다. cmd·PowerShell 에서는 쓰지 않는다(103).
- 이 폴더의 파일(`SKILL.md`·`uart`·`.uart_hub`)과 옆의 `connect-uart`·`disconnect-uart` 폴더는 UartHub.exe 가 처음 실행·업데이트 때 넣고 덮어쓴다. 고치지 않는다.
- `uart` 가 없다(No such file)면 이 환경에서 허브를 아직 시작하지 않은 것이다 — 사용자에게 `UartHub.exe` 를 더블클릭해 달라고 부탁한다.

## 이럴 때 이 명령 (`U=~/.claude/skills/uart/uart`, 모두 `--json`)

| 사용자 말 | 명령 | 볼 것 |
|---|---|---|
| "보드들 상태 알려줘" | `$U ls --detail --json` | 포트·보드·state, `hint:"no_output"`(보드 출력 0 — 전원·배선), `session`(에이징 중) |
| "7001 에서 X 해줘" | (에이징 중이면 규칙 10) `$U run 7001 'X' --json` | `status`·`rc`·`out` |
| "재부팅하고 끝나면 알려줘" | `$U reboot 7001 --json` → `$U wait 7001 --since <rx_offset> --pattern '^root@' --nudge --json` | ② |
| "로그 마지막 N줄" | `$U log 7001 --tail N --json` | `out`(ANSI 색 코드는 빼고 보여 준다) |
| "왜 BUSY 야?" | `$U status 7001 --json` + `$U log 7001 --tail 20 --json` | `blocked_reason`, 로그의 `>>>`·`!!!` 줄 |
| "에이징 기록 중이야?" | `$U session status --json` | ⑧ |
| "에이징 (지금까지) 결과 정리해줘" | `$U session summary 7001 --json` — **세션은 끝내지 않는다** | ⑤ |
| "에이징 끝내고 정리해줘" | `$U session stop 7001 --json` | ⑦ |

## 반드시 지킬 것

1. **판정에는 항상 `--json`.** 셸 종료코드만 보고 판정하지 않는다 — 보드 rc(0~255)와 도구 오류 코드 100~106 이 겹친다.
2. **대상은 포트 번호(`7002` 등)로 지정한다.** 보드 이름은 같은 모델 여러 대에 겹친다(105 ambiguous). 먼저 `$U ls --json` 으로 포트를 확인한다.
3. **Bash 도구 timeout ≥ `--timeout` + 30초.** `--timeout`(기본 30초)은 "프롬프트를 기다리는 시간 + 실행 시간" 전체다. 래퍼 기동에도 약 0.2초가 든다.
4. **120초를 넘는 `wait` 는 Bash `run_in_background`** 로 돌리고 끝 알림을 받는다. `--timeout` 없이 오래 기다리게 하지 않는다.
5. **reboot → wait 는 `--since` 로 잇는다.** `reboot --json` 의 `rx_offset`(송신 직전 위치)을 `wait --since` 에 그대로 넘겨야 재부팅 직후 출력을 놓치지 않는다.
6. **사람 우선 — 101(`busy`)이면 대신 보내지 않는다.** 다른 방법(`--force`, `--clear-line`, `probe --user-ok`, `send`)으로 밀어 넣지 않는다. `blocked_reason`·`partial`(사람이 치고 있는 줄)·`last_lines` 를 사용자에게 그대로 알리고, 다시 시도할지는 사용자에게 묻는다.
7. **연결 안 된 케이블이면 스스로 연결하지 않는다.** 대상 COM 이 `ls` 에서 `status` `pending`·`released` 이거나, 명령이 102 + `blocked_reason` `pending` | `released` 를 주거나, 쓰려는 보드가 `ls` 에 attached 로 없으면
   사용자에게 **"Claude Code 에 `/connect-uart COMx` 를 입력해 연결해 주세요"**(COMx = 응답·`ls` 의 `com`, 모르면 인자 없는 `/connect-uart` 로 목록)라고 부탁하고 기다린다.
   `$U connect`·`$U disconnect` 는 **사람 전용**(slash skill `/connect-uart`·`/disconnect-uart`)이다 — 다른 프로그램의 세션을 끝낼 수 있으므로 Claude 가 직접 부르지 않는다.
   예전 방식(on_demand 가 꺼진 PC)이면 화면의 **새 케이블 확인 창**에 답하거나 트레이의 UART 허브 아이콘 → **케이블 설정**에서 확인해 달라고 해도 된다. 케이블 판정은 사람만 한다(`$U cable …` 은 106 으로 막혀 있다).
8. **103(`hub_down`)**
   - `restarted:true` 면 허브 감독을 방금 다시 띄운 것이다. **5초 기다린 뒤 같은 명령을 한 번만** 다시 한다. 또 103 이면 보고한다.
   - `stopped:true` 면 사용자가 허브를 정지한 것이다(COM 을 다른 도구로 쓰는 중). `$U hub start` 를 스스로 하지 말고 사용자에게 묻는다.
   - 업데이트 중(사용자가 새 `UartHub.exe` 를 실행해 교체하는 동안 — 실행 중이던 긴 명령도 103 으로 끝난다)이거나 그 밖의 103 은 재시도하지 말고 `error` 문구를 그대로 보고하며 트레이 아이콘 상태를 확인해 달라고 부탁한다.
9. **`stranded`(100, `rc:null`)는 재시도하지 말고 보고한다.** 보낸 줄이 tty 에 남아 나중에 실행될 수 있다(이중 실행 위험). `interrupted`(101)는 사람이 Ctrl-C 로 끊은 것이다 — 같은 명령을 다시 보내지 않고 보고한다.
10. **에이징 중인 포트에 보내기 전에 먼저 알린다.** 대상 포트가 `ls --detail` 의 `session`·`session status` 에 에이징 중으로 나오면, `run`·`send`·`reboot`·`probe`·`wait --nudge` 전에
   "7001 은 에이징 '<이름>' 기록 중(<경과>)입니다 — 보내면 그 기록에 섞이고 on/off 지그면 명령 중 전원이 끊길 수 있습니다. 보낼까요?" 처럼 알리고 답을 기다린다(사용자가 이미 그 사실을 알고 시킨 경우는 그대로).

## 종료코드와 JSON 판정

| `status` | 셸 종료코드 | 의미 · 할 일 |
|---|---|---|
| `ok` | 보드 rc(`run`) / 0 | 성공. `run` 은 이때만 `rc` 가 보드 종료코드 |
| `timeout` | 100 | 시간 초과. `run` 이면 허브가 Ctrl-C 로 정리(`cleanup`). `status` 로 상태 확인 뒤 판단 |
| `stranded` | 100 | 보낸 줄의 시작이 돌아오지 않음 — 재시도 금지, 보고(규칙 9). 단 **`rebooted:true` + `started:false`**(보낸 직후 보드가 재부팅해 명령이 사라짐)면 같은 명령을 **1회만** 다시 보낸다(허브가 재부팅 뒤 프롬프트를 깨우고 잠금을 푼다 — 다시 보낸 `run` 은 프롬프트까지 기다렸다 실행). `rebooted:true` + `started:true`(명령이 돌던 중 재부팅)는 다시 보내지 말고 보고 |
| `busy` | 101 | 보내지 않음(보드로 0바이트). `blocked_reason` 을 보고(규칙 6) |
| `interrupted` | 101 | 사람이 실행 중 Ctrl-C, 또는 `send` 조각 사이에 사람이 입력 — 재시도 금지, 보고 |
| `no_cable` | 102 | 케이블 없음·분리됨, 또는 `blocked_reason` `pending`(새 케이블 확인 대기)·`released`(트레이에서 제외)·`ignored`(설정 파일이 제외) — 규칙 7 |
| `hub_down` | 103 | 허브에 닿지 못함 — 규칙 8. `상대가 허브가 아님` 이면 제어 포트를 다른 프로그램이 쓰는 것(재시도해도 같음, 보고) |
| `md5_mismatch` | 104 | push 검증 실패(아직 없는 기능) |
| `ambiguous` | 105 | 대상이 여러 개 — `candidates` 의 포트로 다시 |
| `rejected` | 106 | 거부(인자 오류·빈 명령·제어문자·`--inline` 위반·아직 없는 명령·`cable`) |
| `since_expired` | 106 | `--since` 가 링 버퍼 밖이거나 허브가 다시 시작됨 — `log`/`logpath` 로 파일에서 읽는다 |
| `unsupported` | 106 | 감독과 연결되지 않은 코어의 `hub stop/start` |

- 판정 = `status` + `rc`. `run` 성공은 `status=="ok"` 이고, 그때만 `rc` 가 보드 값이다(도구 오류면 `rc:null` 이거나 없음).
- `out` 은 출력 줄 배열(`\r` 없음), `kernel` 은 printk 줄의 `out` 인덱스 배열, `trailing_newline:false` 면 마지막 줄에 개행이 없었다.
- 101 의 `blocked_reason`: `partial_line`(사람이 입력 중) · `partial_line_orphaned`(입력 중이던 사람 터미널이 끊김) · `fullscreen`(vi·top 화면) · `booting` · `unknown`(상태 모름 → `status`) · `busy_since`(명령 실행 중) · `serialized`(다른 run 진행 중) · `human_idle`(사람이 방금 입력) · `human_active`.
  `booting`·`unknown`·(재부팅으로 사라진 run 의) `serialized` 는 **허브가 이미 `--timeout`(최대 30초) 동안 기다리며 Enter 로 깨워 본 결과**다(config `auto_nudge`, 커널 단계 뒤에만·사람 입력이 없을 때만·부팅당 3번). 같은 명령을 바로 되풀이하지 말고 상태(`last_lines`)를 보고한다 — 부팅이 길면 `wait --pattern '^root@' --nudge` 로 프롬프트를 기다린 뒤 한 번 더.
- `hint:"no_output"`(+ `hint_text`, `rx_since_attach: 0` — `status`·`ls --detail`·101 응답): 연결 뒤 보드에서 **한 바이트도 받지 못했다.** 보드 전원·배선(TX/RX)·다른 보드 케이블일 수 있다고 사용자에게 알리고, 터미널에서 Enter 를 쳐 보게 한다. 같은 명령을 되풀이하지 않는다.
- `rx_offset`·`raw_offset` 은 `epoch:offset` 문자열이다. `wait --since` 에 그대로 넘긴다.
- 비JSON 모드의 도구 오류는 stderr 에 `uart: E10x <status> …` 한 줄이 찍힌다.

## 세 장면 예시

### ① "EVB-A1 보드에서 uname 해줘"

```bash
$U ls --json
#   {"status":"ok","exit":0,…,"cables":[{"cable":"FT1ABCDE","com":"COM5","port":7002,"board":"EVB-A1","status":"attached","state":"AT_PROMPT",…}]}
#   board 가 EVB-A1 인 attached 케이블의 port 를 고른다(여러 개면 사용자에게 어느 것인지 묻는다)
$U run 7002 'uname -a' --timeout 30 --json          # Bash timeout 60초 이상
#   {"status":"ok","exit":0,"rc":0,"out":["Linux EVB-A1 5.10.0 … aarch64 GNU/Linux"],…}
```
보고: `out` 그대로와 `rc`(0). `status` 가 `ok` 가 아니면 위 표대로.

### ② "보드 재부팅하고 부팅 끝나면 알려줘" — 기다림은 두 단계

```bash
$U reboot 7002 --json
#   {"status":"ok","exit":0,"rx_offset":"1762230000123:48213",…}   ← rx_offset 값을 다음 명령에 그대로
$U wait 7002 --since '1762230000123:48213' --pattern 'Restarting system' --timeout 60 --json
#   1단계: 재부팅이 실제로 시작됐는지. 셸은 reboot 직후 프롬프트를 한 번 더 찍는다(예: "root@EVB-A1:/$ [End] /etc/init.d/K00_app") —
#   여기서 바로 '^root@' 를 기다리면 0.1초 만에 거짓 완료가 된다.
#   {"status":"ok","exit":0,"rx_offset":"1762230000123:51877","match":"Restarting system",…}   ← 이 rx_offset 을 2단계에
$U wait 7002 --since '1762230000123:51877' --pattern '^root@' --nudge --timeout 120 --json
#   2단계: 새 부팅의 셸 프롬프트. Bash timeout 150초 이상(또는 run_in_background). --pattern 은 그 보드의 셸 프롬프트에 맞게.
#   --nudge: 커널 로그가 끝나고 2초 조용하면 Enter 1회로 프롬프트를 깨운다(부팅 뒤 셸은 프롬프트를 스스로 찍지 않는 보드가 있다).
#   {"status":"ok","exit":0,"out":[…,"Booting Linux …",…,"root@EVB-A1:~# "],…}
```
보고: 프롬프트가 보였다는 것과 걸린 시간·눈에 띄는 오류 줄. 1단계가 `timeout`(100)이면 재부팅이 시작되지 않은 것이다(사람이 입력 중이었는지 `status` 로 확인). 2단계 `timeout` 이면 `out` 의 마지막 줄들을 보여 주고 사용자에게 알린다.

### ③ 사람이 터미널에서 치고 있는 중에 시킨 경우

```bash
$U run 7002 'uname -a' --json
#   {"status":"busy","exit":101,"blocked_reason":"partial_line","partial":"cat /pro","state":"AT_PROMPT",…}
```
보드로 **0바이트** 보냈다. 보고 예: "터미널에서 `cat /pro` 를 입력하는 중이라 명령을 보내지 않았습니다. 입력을 마치신 뒤 다시 시킬까요?" — 사람의 줄을 지우거나(`--clear-line`) 끼어들지(`--force`) 않는다.

## 에이징 — 세션·감시·정리

**에이징 세션**(포트별)을 시작한 동안만 허브가 그 포트의 콘솔 줄을 사람이 보는 폴더에 남기고 **사건**을 감지한다(`log_layout: 3`, 새 등록의 기본).
세션 밖의 콘솔 줄은 숨김 임시 기록(`<허브 폴더>/임시기록`, 24시간·2GB)에만 있다 — `log`·`logpath`·`wait` 는 지금처럼 쓴다. 세션 밖에서는 사건 감지·풍선이 없다.

- 세션 폴더 `<로그 폴더>/에이징/<이름>_<yyyyMMdd-HHmm>/`: `콘솔.log`(세션 동안 RX 줄, 줄머리 `[시각 +경과초]`, 허브 재시작·케이블 재연결 뒤 이어 쓰고 `*** 기록 끊김 … ***` 표시) ·
  `이벤트/<HHMMSS>_<종류>.txt`(panic·oops·앱 오류마다, 아래 이벤트 txt 형식) · 끝낼 때 `요약.txt`(기간·부팅 수 cmd/soft/hard·이벤트 목록·기록 끊긴 구간).
- 세션 명령은 **COM 을 열고 닫지 않고 보드로 보내지도 않는다** — 사용자가 시키면 Claude 가 불러도 된다. 연결 안 된 포트면 시작이 102 로 거부된다(사용자에게 `/connect-uart COMx` 를 부탁 — 규칙 7).
  세션은 허브·코어가 다시 켜져도 이어 간다(`<허브 폴더>/sessions.json`). 트레이의 COM 포트 ▸ COMx ▸ "에이징 시작…" · "에이징 끝내기" 와 같은 일이다.

```bash
$U session start 7001 "야간 전원 에이징"     # 이름은 폴더 이름이 된다(못 쓰는 글자는 '_'), 생략하면 <보드>_<yyyyMMdd-HHmm>. 응답 folder·name
$U session status --json                     # 세션마다 port·com·board·name·folder·started·elapsed_s·console_log{path,bytes,lines}·last_rx_age_s·events{total,by_kind}·boots·gaps·connected
$U session stop 7001 --json                  # 세션을 끝내고 요약.txt 를 쓴다. 응답 summary(경로)·summary_text(내용)
$U session summary 7001 --json              # 그 포트가 에이징 중이면 세션 시작부터(scope "session" + session{name,started,folder,console_log}). --since 24h 처럼 주면 그 기간(scope "since"). 이전 이름 aging 도 그대로
```

### ⑥ "7001 에이징 시작해줘, 이름은 X" — 세션 시작

1. `$U session start 7001 "X" --json`. `status:"ok"` 면 `name`(다듬은 이름 — `name_given` 이 있으면 바뀐 것)과 `folder` 를 보고한다: "에이징을 시작했습니다. 기록 폴더: …".
2. 102(`blocked_reason:"not_connected"`) = 연결 안 됨 — 사용자에게 `/connect-uart COMx` 를 부탁하고 멈춘다. 106 `이미 에이징 중` = 그 세션 이름·폴더를 알려 준다.
3. 지켜보라는 말까지 있으면 ④ 를 이어서 한다.

### ⑦ "7001 에이징 끝내고 정리해줘" — 세션 끝·요약

1. `$U session stop 7001 --json` → `summary`(요약.txt 경로)를 Read 로 읽는다(응답의 `summary_text` 와 같다).
2. 요약의 기간·기록한 시간·부팅 표(cmd/soft/hard)·이벤트 목록·기록 끊긴 구간을 표로 보고한다. 이벤트 줄의 `→ 이벤트\<HHMMSS>_<종류>.txt` 는 세션 폴더 기준 경로다 — 몇 개를 Read 로 읽어 원인 줄·Call trace 맨 위 함수·모듈을 덧붙인다(⑤ 와 같은 방식).
3. 106 `에이징 중이 아님` 이면 `$U session status --json` 으로 지금 세션을 보여 주고 어느 포트인지 묻는다.

### ⑧ "지금 에이징 기록 중이야?" · "7001 에이징 잘 되고 있어?" — 세션 상태

`$U session status --json` 을 보고 세션마다 **이름 · 경과(`elapsed_s`) · 마지막 출력(`last_rx_age_s` 초 전) · 이벤트 수(`events.total`, `by_kind`)** 를 한 줄씩 알린다.
`count:0` 이면 "에이징 기록 중인 포트가 없습니다". `last_rx_age_s` 가 600 을 넘거나 null 이면 "보드 출력이 N분째 없습니다(보드·케이블 확인)" 를, `connected:false` 면 "케이블이 빠져 있습니다" 를, `gaps` 가 있으면 기록 끊긴 구간을 함께 알린다.

### 사건 기록(jsonl)

허브는 세션 중인 포트(옛 배치 `log_layout` 1·2 는 연결된 모든 포트)의 콘솔 줄을 검사해 **사건**을 `<허브 폴더>/events/<포트>.jsonl`(옛 배치는 `<로그 폴더>/events/`) 에 쌓는다(허브가 기록 — Claude 가 지켜보지 않아도, Claude Code 를 꺼도 남는다). 경로는 `uart events --json` 의 `events_dir` 를 쓴다.
`events`·`session summary`(`aging`) 은 그 파일을 **읽기만** 한다 — 보드로 한 바이트도 보내지 않고, 허브가 꺼져 있어도 지난 기록을 읽는다. 대상은 포트 번호(권장)·COMx·보드명.

| `kind` | 뜻 |
|---|---|
| `panic` · `oops` · `bug` · `oom` | 커널 오류(Kernel panic · Oops/Unable to handle kernel · BUG/WARNING/soft lockup/rcu stall/task blocked · Out of memory) |
| `app_error` | 앱 오류(제품 프로필 `UartHub.profile.json` 의 패턴 — 예: 펌웨어 복구 코드 `APPFAULT(ED)`·와치독 리셋 메시지 `APP_ABORT reason=`). 프로필이 정상 종료 서명(예: reboot 때의 `APPFAULT(BA)(000f)`)을 제외할 수 있다 — 제외된 줄은 사건이 아니다. 프로필이 없으면 앱 오류 패턴도 없다(`hub status --json` 의 `profile`) |
| `boot` | 새 부팅(리셋 순간 직전 줄로 분류). `class`: `cmd`(60초 안에 허브·사람이 보낸 reboot — 예상한 재부팅) · `hard` + **`cause`**(직전 20초 안 panic/oops 줄 `{kind, line, ts, context}` — panic 뒤 리셋) · `soft`(직전 10초 안 `reboot: Restarting system` — 앱·시스템이 재부팅) · `hard` + `silence_s`(그것도 없이 — 와치독·전원, 리셋 전 몇 초 조용했는지). `soft`·`hard` 가 **예상 못 한 재부팅** |
| `confirm` | 명령 출력 속(suspect) panic/oops 뒤 리셋도 없이 30초 동안 아무 출력이 없음 — **멈춤 의심**(`of` = 그 종류, `line`·`context` = 그 줄) |
| `missed` | 허브가 일부 구간을 검사하지 못함(`skipped` 바이트) — 그 사이 사건을 놓쳤을 수 있다고 보고 |
| `attach` · `detach` | 사건이 아니라 기록 표시: 허브가 이 케이블 기록을 시작·다시 연결(`gap_s` = 그 전 기록 공백 초)·에이징 세션 시작/끝(`reason` `session`·`session_end`) · 케이블이 빠짐. 공백 동안의 사건은 모른다 |

- 사건 한 줄: `{ts, port, cable, board, kind, class(boot 만), line(그 줄), log, log_line(rx_offset), context(앞뒤 로그 파일|null), boot_seq, since_boot_s, suspect·cause·silence_s(있을 때만)}`.
  `log` 는 감지할 때 기록 중이던 .log 다 — 자정·크기 전환 바로 뒤의 사건이면 다음 파일일 수 있으니 정확한 자리는 `ts` 와 `log_line` 으로 찾는다.
- 알림 종류(panic·oops·app_error) 사건마다 **에이징 이벤트 txt**(`context`): 세션 중이면 `<세션 폴더>/이벤트/<HHMMSS>_<종류>[_의심].txt`, 옛 배치(`log_layout: 2`)는 `<로그 폴더>/에이징이벤트/<날짜>/<HHMMSS>_<보드>(<포트>)_<종류>[_의심].txt`(종류 = `커널패닉`·`Oops`·`앱오류_<코드>` — 코드는 프로필 `app_codes` 가 정한다).
  머리 줄(`이벤트 종류:`·`보드:`·`시각:`·`원인 줄:`·`콘솔 로그 파일:`, 의심이면 `의심 이유:`) → `----- 앞 200줄 -----` → `----- 이후 -----`(`>>>` = 사건 줄, 뒤 100줄 또는 30초) → 끝 줄 `결과:`(재부팅 시각 / 응답 없음 / 계속 동작 — 사건 뒤 최대 60초 지나 생김, 그 전엔 아직 쓰는 중).
  같은 부팅의 30초 안 연속 줄(Call trace, oops 뒤 panic 등)은 **한 파일**이고 종류마다 사건 한 줄. 더 심한 종류가 합류하면 파일 이름이 바뀐다(그 사건의 `context_was` = 옛 이름 — `context` 파일이 없으면 뒤 사건의 `context` 를 본다).
  bug·oom·부팅·연결/분리에는 txt 가 없다(`context:null`, jsonl 에만 — `aging` 이 부팅 수·간격·기록 공백에 쓴다). 옛 배치(`log_layout` 없음)는 `<로그 폴더>/events/<날짜>/` 의 앞뒤 로그(끝 줄 `# 끝 — …`).
- 같은 부팅에서 같은 종류가 잦으면 앞뒤 로그를 줄인다(1시간에 3개 뒤로는 10분에 1개, 포트당 1시간 30개, 폴더 2GB): 그 사건은 `context:null` + `context_skipped`(`rate`·`hourly`·`disk`) 로 jsonl 에만 남는다.
  부팅마다 첫 `panic`·`oops` 와 `soft`·`hard` 부팅의 직전 200줄은 줄이지 않는다(폴더 2GB 만 적용).
- **`suspect:"cmd_output"`** = 명령 출력 속 줄(옛 로그일 수도, 그 명령이 일으킨 진짜 오류일 수도). 누군가 Enter 를 친 뒤 다음 프롬프트까지(`dmesg`·`cat`·`tail -f`·`echo c > /proc/sysrq-trigger` 의 출력 등)의
  panic·oops·앱 오류 줄은 모두 suspect 로 기록된다(커널 시각으로 실시간이라 받아들이지 않는다). 다만 출력이 10초 조용하면 그 구간은 끝난 것으로 보므로, 전경 프로그램·에이징 스크립트·`tail -f` 를 띄워 둔 뒤 나중에 나는 오류는 실시간 사건이다.
  일부러 일으키는 충돌·리셋 명령(`reboot`, `echo c > /proc/sysrq-trigger`, `echo PANIC > /sys/kernel/debug/provoke-crash/DIRECT`)의 출력은 suspect 가 아니라 실시간 사건이다 — 충돌 시험 결과로 그대로 보고한다.
  suspect 는 부팅 수·재부팅 판정을 바꾸지 않고 그 자체로는 풍선도 없다.
  **진짜였는지는 그 뒤가 확인한다**: 60초 안에 리셋이 오면 그 `boot` 의 `cause` 가 되어 "panic 뒤 재부팅" 풍선이 뜨고, 리셋 없이 30초 동안 출력이 없으면 `confirm`("panic 뒤 응답 없음") 이 남는다.
- 트레이 풍선은 `panic`·`oops`·`app_error`·`confirm` 과 panic/oops 가 `cause` 인 부팅만(config `events.notify`). 그 밖의 재부팅·`bug`·`oom`·`missed` 는 jsonl 에 기록만 하고 알리지 않는다.
  프롬프트가 돌아오면(셸이 살아 있음) 옛 로그를 본 것이다. 확인되지 않은 suspect 는 `context` 를 읽어 판단해서 보고한다.
- 로더 표시(프로필·config `loader_marker_re` — 설정한 보드만, 예: `LDR>T`)는 줄 어디에 있든 언제나 실제 리셋이다(옛 로그에 다시 찍히지 않는다). 설정하지 않은 보드는 커널 부팅 줄로 리셋을 판정한다.
- 판정하지 않는 줄은 확실한 재출력뿐이다: 명령 에코, `<숫자>[시각]` 으로 시작하는 줄(`/proc/kmsg`·`dmesg-ramoops`·`grep -n` 출력 — 시리얼 콘솔은 `<N>` 을 찍지 않음).

```bash
$U events 7002 --since 12h --json            # {status, exit, count, events:[사건 줄…]}. --kind panic,oops,boot:hard 로 거르기
$U events 7002 --follow --json               # 머리 줄 {"follow":true,…} 뒤 새 사건마다 JSON 한 줄(--max N 이면 N 줄 뒤 끝)
$U session summary 7002 --since 24h --json   # 부팅 수(cmd/soft/hard)·간격(최소·중간·최대), 종류별 수·첫/마지막 시각, 앞뒤 로그 목록, 기록 공백(gaps·covered_s) — 이전 이름 aging
```

### ④ "에이징 걸고 panic 나면 알려줘" — 에이징 감시

1. `$U ls --json` 으로 그 보드의 포트를 확인한다(attached 가 아니면 규칙 7 — 사용자에게 `/connect-uart COMx`).
   `$U session status --json` 에 그 포트의 세션이 없으면 **사건 감지가 꺼져 있다**(`log_layout: 3`) — 사용자에게 "에이징 세션을 시작할까요?(이름)" 를 묻고 ⑥ 으로 시작한 뒤 감시한다.
2. 사용자에게 먼저 알린다: "허브는 세션이 끝날 때까지 감시와 상관없이 기록합니다. 제가 지켜보는 동안 사건이 오면 바로 알려 드리고, 놓쳐도 나중에 요약으로 정리할 수 있습니다."
3. **Claude Code 의 Monitor 도구**로 아래 명령을 `timeout_ms: 1800000`(최대 30분)으로 건다. `$U` 는 다음 명령에 남지 않으니 **래퍼 경로를 그대로** 쓴다:
   ```bash
   ~/.claude/skills/uart/uart events 7002 --follow --json --kind panic,oops,app_error,confirm
   ```
   재부팅은 허브가 기록만 하고 지켜보지 않는다(전원 on/off 에이징은 매 회차가 hard 리셋이라 알림이 넘친다) — 사용자가 재부팅도 지켜보라고 분명히 말하면 `--kind` 에 `boot:soft,boot:hard` 를 더한다.
   첫 줄 `{"status":"ok","follow":true,"ts":"<시작 시각>","ports":[7002],…}` 이 감시 시작이다. 그 뒤 줄 하나 = 사건 하나.
   - **Monitor 가 30분 만료로 끝나면 바로 다시 건다**: 마지막으로 받은 사건의 `ts` 를 `--since '<그 ts>'` 로 붙여 다시 걸고(그 사이 사건을 먼저 받는다), `ts`+`kind`+`boot_seq` 가 같은 줄은 이미 보고한 것이니 건너뛴다.
     받은 사건이 없었으면 첫 줄(머리 줄)의 `ts` 를 `--since` 로 쓴다.
   - **명령이 0 이 아닌 종료코드로 끝났으면(102 대상 없음 · 103 래퍼 끊김 · 106 인자 오류 등) 다시 걸지 않는다** — 그 종료코드와 마지막 줄을 사용자에게 보고하고 멈춘다(같은 명령을 되풀이하지 않는다).
   - Monitor 가 없으면 Bash `run_in_background` 로 `… --follow --json --kind … --max 1` 을 돌린다(사건 하나가 오면 exit 0 으로 끝나 알림이 온다). 보고한 뒤 같은 방식(`--since` 마지막 `ts`)으로 다시 건다 — 0 이 아닌 종료코드면 위와 같이 멈추고 보고한다.
4. 사건 줄이 오면 **바로** 보고한다:
   - 오류 사건(`context` 있음): 그 `context` 파일을 Read 로 읽는다(끝 줄 `결과:`(옛 배치는 `# 끝`)이 아직 없으면 몇 초 뒤 다시 읽는다 — 이후 줄은 최대 30초, 결과 줄은 최대 60초 뒤에 생긴다). `>>>` 줄(원인 줄), 그 아래 `Call trace:` 의 맨 위 함수들,
     `pc :`·`lr :`·`Comm:`·`Modules linked in:` 의 모듈·프로세스, 앞쪽 줄에서 직전에 무슨 일이 있었는지를 요약한다. 같은 `context` 의 사건(oops 뒤 panic 등)은 한 번에 묶어 보고한다.
     `context:null`(`context_skipped`)이면 같은 종류가 잦아 앞뒤 로그를 줄인 것 — `line` 과 "또 일어남(몇 번째)"만 짧게 보고한다.
   - (`boot` 를 더해 지켜볼 때) `boot` 에 `cause` 가 있으면 "panic(또는 oops) 뒤 리셋" 으로 보고한다 — `cause.line`(원인 줄)과 `cause.context`(그 panic 의 앞뒤 로그)를 읽어 위의 오류 사건처럼 요약한다. 그 panic 이 suspect 로 먼저 왔었다면 이것이 그 확인이다.
   - `confirm`: "명령 출력 속 panic 뒤 응답 없음(멈춤 의심)" 으로 바로 보고하고 `context` 를 읽어 요약한다(보드는 리셋되지 않고 멈춰 있을 수 있음 — 사용자에게 알린다).
   - `suspect:"cmd_output"` 이 붙은 사건(이 감시의 `--kind` 에 걸린 것): "명령 출력 속에서 본 줄" 로 구분해 짧게 보고한다. 뒤따르는 `boot` 의 `cause` 나 `confirm` 이 오면 그때 확인된 것으로 다시 알리고, 그 전에 프롬프트로 돌아왔다면 옛 로그를 본 것으로 본다.
   - (`boot:soft,boot:hard` 를 더해 지켜볼 때) `boot`(soft·hard): "예상 못 한 재부팅(#`boot_seq`, `class`)" 으로 보고하고, `context`(직전 200줄)를 읽어 리셋 직전 마지막 줄들을 함께 알린다.
   - `missed`: "허브가 N바이트를 검사하지 못했습니다(그 사이 사건을 놓쳤을 수 있음)".
5. **감시 중에는 보드로 아무것도 보내지 않는다** — `run`·`send`·`probe`·`reboot`·`wait --nudge` 를 쓰지 않는다(에이징을 흐트러뜨리고 사람 입력과 겹친다). 보드에서 무언가 확인해야 하면 사용자에게 먼저 묻는다.
6. 사용자가 그만하라고 하면 Monitor(또는 백그라운드 작업)를 멈춘다. 허브의 사건 기록은 세션이 끝날 때까지 감시와 상관없이 계속 쌓인다(세션까지 끝내라는 말이면 ⑦).

### ⑤ "어젯밤 에이징 결과 정리해줘" — 에이징 결과 정리

**정리는 세션을 끝내지 않는다**(끝내기는 사용자가 "끝내고"라고 할 때만 — ⑦). 세션을 끝낸 뒤면 그 세션 폴더의 `요약.txt` 를 먼저 읽는다. 진행 중인 세션은 `$U session summary 7002 --json`(세션 시작부터), 기간을 직접 정해 보려면:
```bash
$U session summary 7002 --since 24h --json
#   {"status":"ok","boots":{"total":288,"cmd":280,"soft":5,"hard":3},"unexpected_reboots":8,
#    "boot_interval_s":{"min":290.1,"median":300.2,"max":912.5,…},"kinds":{"panic":{"count":1,"first":"…","last":"…"},…},
#    "contexts":[{"ts":"…","kind":"oops","kinds":["oops","panic"],"line":"…","context":"…/에이징/야간_20261105-2200/이벤트/031522_커널패닉.txt","exists":true},…]}
```
- 기간 머리말(포트·보드·`since`~`until`, **기록한 시간 `covered_s` / 기간 `period_s`** — `gaps` 가 있으면 그 동안은 허브가 꺼져 있었거나 케이블이 빠져 있어 사건을 모른다고 적는다), **부팅 표**(cmd/soft/hard 수, 간격 최소·중간·최대 — 기록 공백을 걸친 간격은 빠져 있음, 평소 간격보다 크게 벌어진 것은 그사이 멈춤이 있었다는 뜻일 수 있음), **종류별 표**(수·첫·마지막 시각·앞뒤 로그 없는 수 `no_context`)를 만든다. 수는 실시간 사건만이다 — 명령 출력 속 사건은 `kinds.<종류>.suspect`·`boots.suspect`·`suspect_events` 에 따로 있으니 표에 따로 적는다.
  `boots.with_cause` = panic/oops 뒤 리셋 수(그 `boot` 의 `cause` 가 원인 줄 — suspect panic 이 확인된 것도 여기), `kinds.confirm` = 명령 출력 속 panic 뒤 멈춤 의심 수. `contexts` 의 `suspect:"cmd_output"` 파일은 cause·confirm 으로 확인되지 않았으면 옛 로그를 본 것일 수 있으니 읽어 판단한다.
- `contexts` 의 파일마다 Read 로 읽어 **사건 표**(시각 · 종류 · 원인 줄 · Call trace 맨 위 함수/모듈 · 부팅 번호)를 만든다. 많으면 종류별로 대표 몇 개를 읽고 나머지는 원인 줄(`line`)로 묶는다.
- 시간대별로 더 보려면 `$U events 7002 --since 24h --kind boot:hard,panic --json`. `app_error` 의 앱 오류 코드 뜻은 사용자에게 묻거나 그 펌웨어의 코드표를 따른다.
- **`boots.total` 이 0 이거나 `hint:"no_boots_seen"` 이면 그대로 "재부팅 없음"이라고 말하지 않는다.** 허브는 `boot_detection` 의 `loader_marker_re`·`boot_re` 로만 부팅을 센다 —
  로더 표시가 다르거나 커널이 quiet 인 보드는 재부팅해도 0 이다. 세션 이름·사용자 말로 재부팅이 예상되면(on/off 에이징 등) `session.console_log.path` 를 grep 해 부팅마다 찍히는 줄
  (로더 표시·init·앱 시작)을 세어 확인하고, 다르면 사용자에게 그 줄과 함께 "제품 프로필(`UartHub.profile.json`)이나 config 의 `loader_marker_re`·`boot_re` 를 그 보드에 맞춰야 한다"고 알린다.
- 정리하는 동안에도 보드로는 보내지 않는다(읽기만).

## 경로 표기

로그 경로 필드(`log`·`raw`·`idx`·`context`, `logpath` 출력, `hub status` 의 폴더, `events_dir`·`events_file`)는 부른 환경에 맞춰 나온다. 그대로 그 환경의 명령(`tail`, `grep`, Read)에 쓴다.

- WSL: `/mnt/<드라이브 소문자>/…` (예: `/mnt/c/Users/<사용자>/uart_logs/20261104/7002_EVB-A1_101500.log`)
- Windows Git Bash: `<드라이브>:/…` (예: `C:/Users/<사용자>/uart_logs/20261104/7002_EVB-A1_101500.log`)
- 환경변수 `UART_PATH_STYLE=win` 이면 Windows 형식(`\` 구분자) 그대로 받는다.

```bash
$U logpath 7002                        # 지금 기록 중인 .log 파일 경로
$U log 7002 --tail 50 --json           # 최근 50줄
$U status 7002 --json                  # state·blocked_reason·last_lines
```

## 금지

- tty·콘솔 전역 설정을 바꾸는 명령을 `run` 으로: `stty`, `dmesg -n`, `setserial` — 사람 터미널과 허브의 프롬프트 판정을 함께 망가뜨린다.
- 대화형·화면 명령을 `run` 으로: `vi`, `top`, `less`, `more`, 인자 없는 `cat` — 사람용이다(`run` 은 `</dev/null` 로 봉인돼 표준 입력을 못 읽는다).
- `--inline` 으로 `exit`·`exec`·`logout`(허브가 106 으로 거부)이나 그 밖의 셸 상태 변경. `--inline` 은 `cd`/`export`/`unset`/`umask`/`alias` 한 개 명령만.
- `--force`(프롬프트 조건을 건너뜀), `--clear-line`(사람이 친 줄을 지움), `probe --user-ok` 는 사용자가 명시적으로 지시했을 때만.
- 같은 COM 을 다른 프로그램(시리얼 터미널, 직접 만든 스크립트)으로 여는 것 — 허브가 잡고 있다. 허브 없이 COM 을 써야 하면 사용자가 `/disconnect-uart COMx` 한다.
- 로그 폴더의 spool 파일(`live/*.tx`)에 쓰는 것 — 쓰지 않는다. 원시 바이트가 필요하면 `send` 를 쓴다(사람 우선 규칙이 같이 적용된다).
- `$U cable …`·`$U hub stop`·`$U hub start` 를 사용자 지시 없이. `$U connect`·`$U disconnect` 는 사용자 지시가 있어도 부르지 않는다 — 사용자에게 `/connect-uart COMx`·`/disconnect-uart COMx` 를 직접 입력해 달라고 한다.

## 알아 둘 것

- 케이블을 뽑았다 꽂으면 허브가 그 콘솔의 다음 입력 앞에 `^E^U`(줄 끝으로·줄 지우기)를 붙인다(분리 때 셸 줄 편집 중이었을 때만, 로그에 `>>> [hub-clear]`). 탈착 잡음이 셸 줄에 `??????` 로 남아 Enter 때 엉뚱한 명령이 실행되는 것을 막는다. 사용자가 "Enter 쳤더니 이상한 명령이 실행됐다"고 하면 이 원인을 알려 준다.
- 케이블 재연결·허브 재시작 뒤에는 상태가 `UNKNOWN` 이다 → `$U status <포트> --json` 확인 뒤, 필요하면 사용자 동의를 받아 `$U probe <포트> --user-ok --json`.
- COM 놓아주기·다시 잡기는 사용자의 `/disconnect-uart`·`/connect-uart` 다(Claude 용 `release`/`grab` 은 없다).
- 아직 없는 기능(쓰면 106 이거나 명령이 없음): `push`(파일 전송), `uboot`(U-Boot 진입), `term`.
