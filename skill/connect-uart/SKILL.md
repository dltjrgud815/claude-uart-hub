---
name: connect-uart
description: UART 허브가 보드 케이블 COM 포트를 잡게 하고(다른 프로그램이 쓰고 있으면 확인 창) 터미널을 띄운다. 보오를 함께 적으면 그 속도로 연다. 사용자가 /connect-uart COMx [baud] 로만 부른다.
disable-model-invocation: true
argument-hint: "[COMx] [baud]"
---

# /connect-uart — 보드 콘솔 연결(사용자 전용)

사용자가 `/connect-uart $ARGUMENTS` 를 입력했다. 아래 명령 하나를 실행하고 결과를 한국어로 짧게 알린다.

```bash
~/.claude/skills/uart/uart connect $ARGUMENTS --json
```

- **Bash 도구 timeout 은 180000 ms(180초) 이상**으로 준다. 다른 프로그램이 COM 을 쓰고 있으면 화면에 확인 창이 뜨고 사용자의 답이나 그 프로그램이 COM 을 놓기를 최대 2분 기다린다.
- 한 번만 실행한다. 결과가 실패여도 스스로 다시 실행하거나 다른 방법(`uart cable …`, `uart hub start`)을 쓰지 않는다.
- `$ARGUMENTS` 가 비어 있으면 그대로 `connect --json` 을 실행한다(꽂힌 COM 마다 연결 상태 표).
- 사용: `/connect-uart COM3` · `/connect-uart COM3 9600` · `/connect-uart COM3 115200`. 보오를 적지 않으면 그 케이블의 지금 보오(처음이면 115200)를 그대로 쓴다.
  적으면 그 케이블에 기억되고(설정 파일 `cables.<id>.baud`), 이미 연결돼 있으면 새 보오로 다시 연다. 쓸 수 있는 값: 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600(그 밖은 106).

## 결과 알리기 (`status` 기준)

| `status` | 알릴 내용 |
|---|---|
| `ok` | "`com` 연결됨 — 포트 `port`(`board`), `baud`bps"(`old_baud` 가 있으면 "`old_baud` → `baud` 로 바꿔 다시 열었습니다"). `holder_closed` 가 있으면 "그 COM 을 쓰던 `holder_closed.label`(pid `holder_closed.pid`) 세션을 끝냈습니다". `terminal` 이 `launched`(새 터미널)·`focused`(열린 창을 앞으로)·`none`(`terminal_note` 그대로). `rx_bytes` 가 0 이면 `message`(보드 출력 없음 — 전원·배선 확인 뒤 터미널에서 Enter)를 그대로 |
| `ok` + `cables` | 인자 없는 표: 줄마다 `com` 과 `text`(연결됨 포트·보드 / 연결 안 됨 / <프로그램> 사용 중 · 보오) |
| `cancelled` (106) | 사용자가 확인 창에서 취소함 — 아무것도 바꾸지 않았다 |
| `timeout` (100) | 그 프로그램이 COM 을 놓지 않음(`holder.label`) — 아무것도 바꾸지 않았다. 그 프로그램에서 연결을 끊은 뒤 다시 `/connect-uart` 를 부탁 |
| `no_cable` (102) | 그 COM 이 없음. `coms` 의 목록(꽂힌 COM)을 보여 준다 |
| `hub_down` (103) | `error` 그대로. `restarted:true` 면 허브를 다시 켰다 — 5초 뒤 사용자가 다시 `/connect-uart` 를 입력하도록 안내 |
| `rejected` (106) | `error` 그대로(보오가 틀렸으면 쓸 수 있는 값 목록이 들어 있다) |

연결된 뒤 그 보드에 명령을 보내는 것은 `uart` skill 규칙(포트 번호로 `run`·`wait` …)을 따른다.
