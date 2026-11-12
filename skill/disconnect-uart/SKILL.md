---
name: disconnect-uart
description: UART 허브가 잡은 보드 케이블 COM 포트를 놓는다(MobaXterm 같은 다른 프로그램이 그 COM 을 다시 쓸 수 있게). 사용자가 /disconnect-uart COMx 로만 부른다.
disable-model-invocation: true
argument-hint: "COMx"
---

# /disconnect-uart — 보드 콘솔 끊기(사용자 전용)

사용자가 `/disconnect-uart $ARGUMENTS` 를 입력했다. 아래 명령 하나를 실행하고 결과를 한국어로 짧게 알린다.

```bash
~/.claude/skills/uart/uart disconnect $ARGUMENTS --json
```

- **Bash 도구 timeout 은 180000 ms(180초) 이상**으로 준다.
- `$ARGUMENTS` 가 비어 있으면 실행하지 말고 "끊을 COM 을 함께 적어 주세요(예: `/disconnect-uart COM8`)"라고 답한다.
- 한 번만 실행한다. 실패해도 다른 방법(`uart cable …`, `uart hub stop`)을 쓰지 않는다.

## 결과 알리기 (`status` 기준)

| `status` | 알릴 내용 |
|---|---|
| `ok` | "`com` 끊음 — 허브가 COM 을 닫았습니다(포트 `port` 번호는 유지, 바탕화면 아이콘은 사라짐). 이제 다른 프로그램이 그 COM 을 쓸 수 있습니다". `already:true` 면 `message`(이미 끊김·연결된 적 없음) |
| `no_cable` (102) | 그 COM 이 없음. `coms` 의 목록을 보여 준다 |
| `hub_down` (103) | `error` 그대로 |
| `rejected` (106) | `error` 그대로 |

다시 연결하려면 사용자가 `/connect-uart COMx` 를 입력한다.
