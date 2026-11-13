# UART 허브 설치 안내 (Claude 가 등록)

보드 UART 콘솔을 **사람(터미널)과 Claude 가 같이 쓰게 하는 UART 허브**를 이 PC 에 등록한다.
설치하지 않는다 — 받은 폴더의 `UartHub.exe` 를 **그 자리에서 그대로** 쓴다(복사·시작프로그램 등록 없음, 관리자 권한 필요 없음).

## 사용자용 요약

1. 받은 폴더(`UartHub_<버전>/`, zip 이면 풀어서)를 **이 PC 의 로컬 디스크에서 늘 둘 곳**에 둔다. 예: `C:\Tools\UartHub_<버전>\`
   (네트워크 폴더·USB·WSL 안(`\\wsl.localhost\…`)은 안 된다). 등록한 뒤에는 폴더를 옮기거나 지우지 않는다(옮겼으면 새 위치에서 같은 요청을 한 번 더).
2. Claude Code 에서 다음과 같이 요청한다(WSL 의 Claude Code 면 경로를 `/mnt/c/Tools/UartHub_<버전>/INSTALL_UART_HUB.md` 로).
   ```
   C:\Tools\UartHub_<버전>\INSTALL_UART_HUB.md 를 참고해 UART 허브를 등록해줘
   ```
   더블클릭·확인 창은 없다 — 이 요청이 동의다. Claude 가 먼저 할 일(계획)을 보여 주고 등록한다.
   받은 전달 메시지에 `EXE_SHA256` 값이 있으면 요청에 같이 붙여 준다(바꿔치기 확인용).
3. 등록이 끝나면 Claude Code 에 **`/connect-uart` 를 직접 입력**해 본다. 슬래시 명령 목록에 없으면 **Claude Code 세션을 새로 시작**한다.
4. 사용법(자세한 것은 같은 폴더의 `USAGE_ONE_PAGE.md`):
   - 연결: `/connect-uart COM8`(COM 번호를 모르면 `/connect-uart` 만) · 끊기: `/disconnect-uart COM8` — **사람만** 입력한다.
   - 보드 작업: "EVB-A1 보드에서 uname 해줘", "보드 재부팅하고 부팅 끝나면 알려줘".
5. **로그온 때 저절로 켜지게 하려면(직접 한 번)**: Win+R → `shell:startup` → 열린 폴더에 `UartHub.exe` 를 **오른쪽 버튼으로 끌어 놓고 '바로 가기 만들기'**. Claude 는 시작프로그램을 만들지 않는다.
6. 바탕화면 허브 아이콘을 열 때 "게시자를 확인할 수 없습니다" 보안 경고가 뜨면: `UartHub.exe` 오른쪽 버튼 → **속성** → 아래쪽 **'차단 해제'** 체크 → 확인(인터넷에서 받은 파일 표시를 지움 — 사람이 직접).

## 배포 폴더 구성

```text
UartHub_<버전>/
├── INSTALL_UART_HUB.md   이 문서(사용자 요약 + Claude 가 따르는 등록·업데이트·제거 절차)
├── INSTALL_UART_HUB.en.md 이 문서의 영어판
├── UartHub.exe           허브 본체(서명 없는 단일 exe). 이 자리에서 실행한다 — 복사·이동·이름 바꾸기 금지
├── USAGE_ONE_PAGE.md     사람용 사용법 한 장
├── USAGE_ONE_PAGE.en.md  사람용 사용법 한 장(영어)
├── GUIDE.md              짧은 사용 가이드(목적·설치 한 줄·매일 쓰기·에이징·막힐 때)
├── SHA256SUMS            위 파일들의 SHA-256(sha256sum -c 형식) — 받는 중 깨졌는지만 확인한다(바꿔치기는 전달 메시지의 EXE_SHA256 으로)
└── UartHub.profile.json  (선택) 제품 프로필 — 보드·펌웨어별 로더 표시·앱 오류 패턴 등. 있는 배포에서만 들어 있다.
                          exe 와 같은 폴더에 있어야 읽힌다 — 옮기거나 이름을 바꾸지 않는다(없으면 일반 Linux 기본값)
```

---

## Claude 설치 절차

> 이 절은 등록을 요청받은 Claude 가 따르는 절차다. 순서대로 진행하고, 마지막에 7단계 형식으로 보고한다.
> 이 문서에 적힌 명령 말고는 실행하지 않고, 파일을 만들거나 고치지 않는다(exe 가 스스로 쓰는 파일 말고). git commit·push 는 하지 않는다.
> 하지 않는 것은 맨 아래 **"금지"** 를 먼저 읽는다 — 특히 exe 복사·이동, `-ExecutionPolicy Bypass`, 시작프로그램 쓰기, MotW 지우기, `uart connect`.

### 시작 전 — 실행 환경과 경로 표기
exe 는 Windows 프로그램이고 어느 셸에서 불러도 결과는 같다(Windows 쪽에 등록되고, skills 는 Windows 와 `.claude` 가 있는 **모든** WSL 배포판에 들어간다).
아래 표에서 지금 셸을 고르고, 이 문서의 `<PKG>`·`$EXE`·`$UART` 를 그 표기로 쓴다.

| 환경 | 판단 | `<PKG>` (예: `C:\Tools\UartHub_<버전>`) | `$UART`(skills 래퍼) | 출력 받기 |
|---|---|---|---|---|
| WSL | `uname -r` 에 `microsoft` | `/mnt/c/Tools/UartHub_<버전>` — 드라이브 문자 그대로 `/mnt/<드라이브>/…`(`wslpath -u 'C:\…'`) | `~/.claude/skills/uart/uart` | 표준 출력 그대로, 종료코드 `$?` |
| Windows(Git Bash) | `uname -s` 가 `MINGW64_NT…`·`MSYS_NT…` | `/c/Tools/UartHub_<버전>`(`cygpath -u 'C:\…'`) | `"$(cygpath -u "$USERPROFILE")/.claude/skills/uart/uart"` | **`\| cat` 을 붙인다**(창 프로그램이라 출력이 사라질 수 있음), 종료코드 `${PIPESTATUS[0]}` |

- `EXE="<PKG>/UartHub.exe"`. PowerShell 은 쓰지 않는다(exe 를 직접 실행하면 되므로 실행 정책을 바꿀 일이 없다).
- **Bash 도구 timeout 은 600000 ms(10분)** 로 준다(WSL 배포판 탐침·skills 배치는 setup 이 2분 안으로 끊고, 감독 기동 확인은 최대 30초).
- **결과 파일** `%LOCALAPPDATA%\uart_hub\setup_last.json` — `--json` 출력과 같은 JSON 한 줄(한글은 `\uXXXX`). setup 은 시작하자마자 이 파일에 `"result":"running"` 을 먼저 쓰고 끝날 때 결과로 바꾼다.
  - WSL: `"$(wslpath -u "$(cmd.exe /c "chcp 65001>nul & echo %LOCALAPPDATA%" 2>/dev/null | tr -d '\r')")/uart_hub/setup_last.json"`
  - Git Bash: `"$(cygpath -u "$LOCALAPPDATA")/uart_hub/setup_last.json"`
  - 실행 직전에 `T0=$(date '+%Y-%m-%dT%H:%M:%S')` 를 적어 두고, 파일의 `at` 이 `T0` 보다 같거나 뒤일 때만 이번 실행의 결과로 믿는다. `result:"running"` 이면 아직 실행 중이거나 중간에 끊긴 것이다.
  - **Bash 도구가 시간 초과로 끝나도 다시 실행하지 않는다** — 1분 뒤 이 파일을 읽어 `at`·`result` 를 보고, 그래도 `running` 이면 보고하고 멈춘다.

### 1단계 — 원본 폴더 확인
- `<PKG>/UartHub.exe`·`<PKG>/USAGE_ONE_PAGE.md`·`<PKG>/SHA256SUMS`·`<PKG>/INSTALL_UART_HUB.md` 가 모두 있는지 본다. 하나라도 없으면 중단하고 없는 파일을 보고한다.
  `<PKG>/UartHub.profile.json`(제품 프로필)은 선택이다 — 없어도 진행한다.
- **위치**: `<PKG>` 는 이 PC 의 로컬 고정 디스크여야 한다(WSL 표기 `/mnt/<드라이브>/…`, Git Bash 표기 `/<드라이브>/…`). `\\wsl.localhost\…`·`//wsl$/…`·네트워크 드라이브·USB 이면 **멈추고** 사용자에게 로컬 폴더(예: `C:\Tools\`)로 옮겨 달라고 한 번 묻는다(Claude 가 옮기지 않는다 — setup 도 이 위치면 exit 3 으로 막는다).
- 임시 위치(`Downloads` 의 zip 안·`%TEMP%`·메일 첨부 미리보기)로 보이면 등록 전에 **한 번만** 묻는다: "이 폴더에서 그대로 쓰게 됩니다 — 이 위치에 계속 두실 건가요?"
- 해시 대조:
  ```bash
  cd '<PKG>' && sha256sum -c SHA256SUMS && sha256sum UartHub.exe
  ```
  `SHA256SUMS` 의 모든 줄이 `OK` 여야 한다(아니면 **중단**·보고 — 받는 중 깨진 파일). `SHA256SUMS` 는 같은 폴더에 있어 바꿔치기는 막지 못한다 —
  사용자가 전달 메시지의 `EXE_SHA256` 을 주었으면 마지막 줄의 해시와 같은지 보고, 다르면 **중단**·보고한다. 주지 않았으면 보고에 해시만 적는다.

### 2단계 — 계획 보고(아무것도 바꾸지 않음): `setup --json`
WSL:
```bash
EXE='/mnt/c/Tools/UartHub_<버전>/UartHub.exe'
T0=$(date '+%Y-%m-%dT%H:%M:%S'); "$EXE" setup --json; echo "rc=$?"
```
Git Bash:
```bash
EXE='/c/Tools/UartHub_<버전>/UartHub.exe'
T0=$(date '+%Y-%m-%dT%H:%M:%S'); "$EXE" setup --json | cat; echo "rc=${PIPESTATUS[0]}"
cat "$(cygpath -u "$LOCALAPPDATA")/uart_hub/setup_last.json"     # 위 출력이 비었을 때만(at ≥ T0 확인)
```
`--yes` 가 없으면 결과 파일 말고는 **아무것도 바꾸지 않는다**(꺼진 WSL 배포판은 깨우지 않고 `unknown` 으로 둔다). JSON 에서 볼 것:

| 키 | 뜻 |
|---|---|
| `result`·`rc` | `planned`(0) = 진행 가능 · `blocked`(3) = 중단 — `message`·`next` 를 보고 아래 표대로 |
| `exe`·`version`·`location` | 등록될 exe(이 폴더 그대로)와 판. `location` 이 null 이 아니면 위치 문제(blocked) |
| `log_root`·`hub_dir` | 로그 위치(D: 가 로컬 고정 디스크면 `D:\uart_logs`, 아니면 `%USERPROFILE%\uart_logs` — 예전 허브 폴더가 있으면 그것)·허브 폴더. 그 아래 `에이징\<이름>_<시각>\`(에이징 세션 — 콘솔.log·이벤트\·요약.txt)·`hub\`(숨김 `임시기록\` = 세션 밖 콘솔 로그 24시간) |
| `install` | 지금 등록(`install.json`) — 처음이면 `null` |
| `running` | 실행 중 허브(`pid`·`exe`·`version`·`same_exe`). 다른 exe 면 `--yes` 가 그 허브를 끄고 이 exe 로 바꾼다(포트·설정·로그 유지, 열린 터미널은 수 초 끊김) |
| `downgrade` | null 이 아니면 이 exe 가 실행 중·등록된 판보다 오래됨(`from`→`to`) — 되돌리기 |
| `stopped` | true 면 허브가 '정지' 상태(트레이에서 정지함) — 등록은 되지만 사용자가 트레이 → **시작** 을 눌러야 기록한다 |
| `legacy` | 옛 설치 방식(예전 설치기) 잔재 — 있으면 `blocked` |
| `skills` | 넣을 곳: `windows` + WSL 배포판. `result` = `install`(넣음)·`update`(갱신)·`same`(그대로)·`skipped`(.claude 없음 등)·`unknown`(꺼진 배포판 — 계획에서는 깨우지 않음, `--yes` 때 확인)·`failed`(`note` 가 `budget…` 이면 시간 예산 초과) |
| `motw` | `present:true` = 인터넷에서 받은 표시(Zone.Identifier)가 있음(`null` = 확인 못 함). **지우지 않는다** — 7단계에서 사용자에게 안내만 |
| `warnings` | 다른 프로그램이 쓰는 허브 포트, 옛 PowerShell 판 작업 `\uart_hub`, 환경변수 `UART_HUB_DIR` — 보고만(막지 않음) |
| `autostart` | `shell:startup` 에 있는 UartHub 바로 가기(대상·`this_exe`) — 읽기만 |
| `actions` | `--yes` 가 할 일(빈 배열 = 이미 이 exe 로 등록되어 있음) |

- 사용자에게 `actions`·`log_root`·`running`(있으면)·`warnings`(있으면)를 짧게 보여 준다. 사용자가 이미 등록을 요청했으므로 아래 경우가 아니면 **다시 묻지 않고 3단계로** 간다.
- `blocked`(rc 3) 처리 — `message` 로 구분:

| `message` | 할 일 |
|---|---|
| 옛 설치 방식(설치기)의 잔재 | 사용자에게 `legacy`·`next` 를 알리고 **허락을 받은 뒤에만** `"$EXE" uninstall --yes`(Git Bash 는 `\| cat`) → 2단계부터 다시. **그 뒤에도 같은 잔재로 막히면 멈추고 보고**(실행 중인 옛 exe 등 — 사람이 확인) |
| 다른 설치기·제거기(UART 허브 창)가 실행 중 | 사용자에게 화면의 "UART 허브" 창(설치·제거 확인 창)을 닫아 달라고 한 뒤 **한 번만** 다시 |
| 다른 설치기·업데이트가 교체 중 | 1분 뒤 **한 번만** 다시 |
| 감독이 실행 중인데 누구인지 확인할 수 없습니다 · 등록되지 않은 감독이 이미 실행 중 | 사용자에게 트레이 아이콘 → **허브 끄기** 를 부탁한 뒤 다시 |
| 이 exe 가 네트워크·UNC 경로 / 이동식 디스크 … 에 있습니다 | 멈추고 로컬 고정 디스크로 옮겨 달라고 부탁(1단계) |
| 되돌리기: 이 exe … 가 지금 판 … 보다 오래됐습니다 | 사용자에게 **한 번** 묻는다: "지금 v`downgrade.from` 이 돌고 있습니다 — v`downgrade.to` 로 되돌리기인가요?" 예면 3단계 명령에 `--allow-downgrade` 를 붙인다. 아니면 멈춤 |

### 3단계 — 등록 전 확인, 그리고 `setup --yes --json`
- `running` 이 null 이 아니고 다른 exe(교체)면 먼저 지금 연결된 보드를 본다: `"$UART" ls --json`(Git Bash 는 `| cat`). `cables[].status` 에 `attached` 가 하나라도 있으면
  "허브를 바꾸는 수 초 동안 열린 터미널이 끊기고 에이징 기록·사건 감지가 잠깐 멈춥니다 — 지금 바꿀까요?" 를 **한 번** 묻는다.
- 등록:

WSL:
```bash
T0=$(date '+%Y-%m-%dT%H:%M:%S'); "$EXE" setup --yes --json; echo "rc=$?"
```
Git Bash:
```bash
T0=$(date '+%Y-%m-%dT%H:%M:%S'); "$EXE" setup --yes --json | cat; echo "rc=${PIPESTATUS[0]}"
```

| `result` (`rc`) | 뜻 | 다음 |
|---|---|---|
| `registered` (0) | 처음 등록 — 허브 실행 중 | 4단계 |
| `replaced` (0) | 다른 exe(또는 다른 판)의 허브를 끄고 이 exe 로 바꿈 | 4단계 |
| `updated` (0) | 등록을 맞춤(skills 다시 넣음·꺼져 있던 허브 켬 등) | 4단계 |
| `unchanged` (0) | 이미 이 exe 로 등록·실행 중 — 바꾼 것 없음 | 4단계 |
| `blocked` (3) | 아무것도 바꾸지 않고 중단 | 2단계의 표 |
| `rolled_back` (1) | 바꾸다 실패해 **이전 상태로 되돌림**(교체였으면 이전 허브를 다시 켬, 처음 등록이었으면 띄운 허브를 내리고 등록을 지움) | 보고 — 다시 실행하지 않는다. `next` 에 "이전 exe 를 더블클릭" 이 있으면 그대로 안내 |
| `failed` (1) | 실패 — `message`·`log`(허브 폴더의 `install.log`)를 보고 | 보고 — 다시 실행하지 않는다 |

- `health.ok:true` = 감독을 띄우고 제어 포트가 응답함(최대 30초). `hub_running`·`control_port` 도 같이 본다.
- 같은 exe 로 다시 실행해도 안전하다(바꿀 것이 없으면 `unchanged`).
- 사용자가 "지금 켜지는 말고 등록만"이라고 했으면 `--no-start` 를 붙인다(허브는 아이콘·Claude 의 `uart` 호출·exe 더블클릭 때 켜진다).

### 4단계 — 검증
skills 래퍼(`$UART`, 표 참고)로 허브에 닿는지 본다. **읽기만 하는 두 명령만** 쓴다(Git Bash 는 각각 `| cat`):
```bash
"$UART" ls --json; echo "rc=$?"
"$UART" hub status --json; echo "rc=$?"
```
- `ls --json` → `rc=0`·`"status":"ok"`. `cables` 가 비어 있거나 `pending` 이어도 정상이다(필요할 때만 연결 — `/connect-uart` 전에는 COM 을 잡지 않는다).
- `hub status --json` → `rc=0`, `version` 이 setup JSON 의 `version` 과 같다. `profile` 은 제품 프로필이 없으면 `null` 이다 — `profile.error` 가 있으면 프로필이 깨져 일반 기본값으로 도는 것이니 그 문구를 7단계에 보고한다.
- `uart connect` 는 부르지 않는다(인자가 없어도 — 사람 전용 명령이다).
- 래퍼가 없으면(`No such file`): 3단계 JSON 의 `skills` 에서 이 환경이 `skipped`(.claude 없음)인지 본다.
- `rc=103`(hub_down)이면 5초 뒤 **한 번만** 다시. 그래도 103 이면 보고(트레이 아이콘 확인을 부탁).

### 5단계 — `/connect-uart` 안내(항상)
- `/connect-uart`·`/disconnect-uart` 는 사람만 부르는 슬래시 명령이라 Claude 는 목록에 보이는지 확인할 수 없다.
- 그래서 **항상** 이렇게 안내한다: "Claude Code 에 `/connect-uart` 를 직접 입력해 보세요. 목록에 없으면 Claude Code 세션을 새로 시작하세요."
  (WSL 에서 등록했고 Windows 의 Claude Code 도 쓰면 그쪽 세션도 마찬가지.)

### 6단계 — (해당할 때) 정지 상태·경고
- `stopped:true` 면 "허브가 정지 상태입니다 — 트레이 아이콘 → **시작** 을 눌러야 보드를 기록합니다" 를 알린다.
- `warnings` 가 있으면 그대로 알린다(포트를 다른 프로그램이 씀 → 그 프로그램을 끄거나 `config.json` 의 `control_port`·`base_port`, 옛 작업 `\uart_hub` → 작업 스케줄러에서 사용자가 삭제, `UART_HUB_DIR` → 사용자가 환경변수 삭제).

### 7단계 — 사용자 보고
다음만 간결하게 보고한다.
1. 결과(`result`)·판(`version`)·exe 위치(이 폴더 그대로 씀 — 옮기지 말 것)·`sha256sum UartHub.exe` 값(전달 메시지 값과 같았는지). 교체했으면 이전 exe·판(`running`).
2. 로그 위치(`log_root`)와 바꾼 것(`actions` — `unchanged` 면 "바꾼 것 없음"). 옛 판에서 올리면 `actions` 에 `config.json 에 "log_layout": 3 보장`(없으면 더하고 2 면 3 으로)·`로그 폴더 정리(…)`가 보인다: 허브가 꺼진 동안 아주 옛 배치의 날짜 폴더·`live`·`events` 를 `콘솔로그\`·`에이징이벤트\`·`hub\events` 로 같은 드라이브 안에서 이름만 바꿔 옮긴다(이미 있는 것은 그대로, 줄마다 `install.log`). `콘솔로그\`·`에이징이벤트\` 는 옛 기록으로 남고, 새 기록은 에이징 세션 폴더(`에이징\`)와 숨김 `hub\임시기록` 에 쓴다.
3. skills 를 넣은 곳(`skills` 의 `installed`·`updated` — Windows·WSL 배포판 이름), 건너뛴 곳과 이유.
4. 검증(4단계): `uart ls` rc·케이블 수, `hub status` 의 `version`.
5. 안내:
   - `/connect-uart` 를 직접 입력해 보기(없으면 세션 새로 시작), `/connect-uart COMx` 로 연결·`/disconnect-uart COMx` 로 끊기.
   - 자동 실행은 사용자가 직접: Win+R → `shell:startup` → `UartHub.exe` 를 오른쪽 버튼으로 끌어 놓고 '바로 가기 만들기'.
     `autostart` 에 `this_exe:false` 인 바로 가기가 있으면 "예전 exe 를 가리킵니다 — 지우고 새 exe 로 다시 만드세요"(그대로 두어도 예전 exe 는 등록된 새 판으로 켠다).
   - `motw.present:true` 면: 아이콘을 열 때 보안 경고가 뜨면 `UartHub.exe` 오른쪽 버튼 → 속성 → '차단 해제'(Claude 는 하지 않음).
   - 6단계에 해당하면 그 내용.

### 종료코드(요약)
| 명령 | 0 | 1 | 3 | 106 |
|---|---|---|---|---|
| `setup`(계획만) | 진행 가능 | 오류 | 실제 실행이 막힐 상황(옛 잔재·위치·다른 설치기·누구인지 모르는 감독·되돌리기) | 인자 오류 |
| `setup --yes` | 등록·교체·맞춤·바꾼 것 없음 | `rolled_back`(되돌림)·`failed` | 무변경 중단 | 인자 오류 |
| `uninstall --yes` | 제거함 | 일부 실패(나머지는 지움 — `# 제거 일부 실패` 줄) | 등록이 없음(`%LOCALAPPDATA%\uart_hub` 에 설정·결과 파일만)·다른 설치기 실행 중 | 인자 오류 |

---

## 업데이트 방법

새 배포 폴더(`UartHub_<새 버전>/`)를 받아 로컬 디스크의 둘 곳에 두고 같은 요청을 한다.
```
<새 폴더>\INSTALL_UART_HUB.md 를 참고해 UART 허브를 등록해줘
```
- Claude 는 위 절차를 그대로 따른다. 2단계 `running` 이 이전 폴더의 exe 로 나오고, 3단계 결과는 `replaced` — 이전 허브를 끄고(수 초, 열린 터미널은 끊김) 새 exe 로 켠다.
  포트 번호·연결해 둔 케이블·설정·로그·바탕화면 아이콘(대상만 새 exe 로)·skills(새 exe 로 갱신)는 그대로 이어진다. 새 exe 가 뜨지 않으면 `rolled_back` 으로 이전 허브가 다시 켜진다.
- 이전 폴더는 사용자가 확인한 뒤 지운다(Claude 가 지우지 않는다). 자동 실행 바로 가기를 만들어 두었으면 새 exe 로 다시 만든다(7단계 안내).
- 되돌리려면 이전 폴더의 `INSTALL_UART_HUB.md` 로 같은 요청을 한다 — 2단계가 `downgrade` 로 막히면 사용자에게 한 번 묻고 `--allow-downgrade` 를 붙인다.

## 제거 방법

Claude 에게 "INSTALL_UART_HUB.md 의 제거 방법대로 UART 허브를 제거해줘" 라고 요청한다. Claude 는:
1. "시작 전" 표대로 `EXE`(이 폴더의 `UartHub.exe` — 등록된 판과 달라도 된다)를 정하고 실행한다.
   - WSL: `"$EXE" uninstall --yes; echo "rc=$?"`
   - Git Bash: `"$EXE" uninstall --yes | cat; echo "rc=${PIPESTATUS[0]}"`
2. 종료코드: 0 = 제거함 · 3 = 등록이 없음(또는 다른 설치기 실행 중) · 1 = 일부 실패(`# 제거 일부 실패` 줄과 `오류:` 줄을 보고 — 나머지 단계는 이미 지움). 다시 실행하지 않는다.
3. 지우는 것: 허브 끄기, 바탕화면 허브 아이콘(자기 것만), Claude skills `uart`·`connect-uart`·`disconnect-uart`(이 등록이 넣은 것만 — 표시 `.uart_hub` 가 맞을 때),
   `%LOCALAPPDATA%\uart_hub\install.json`·`icons.json`, 예전 설치기 잔재(자기 표시가 있는 시작프로그램·시작 메뉴 바로가기, 설치 폴더의 `UartHub*.exe`).
   남기는 것: exe 폴더, 로그·설정(`log_root`, 허브 폴더의 `token`·`config.json`·`state.json`), `%LOCALAPPDATA%\uart_hub` 폴더(`prefs.json`·`setup_last.json`).
4. 보고: 끈 허브, 지운 skills, 남은 것(exe 폴더·로그 위치). 자동 실행 바로 가기를 만들었으면 사용자가 `shell:startup` 에서 지우도록, exe 폴더·로그가 필요 없으면 사용자가 지우도록 안내한다.

## 자동 실행 켜기·끄기 (사람이 직접)

- 켜기: Win+R → `shell:startup` → 열린 폴더에 `UartHub.exe` 를 오른쪽 버튼으로 끌어 놓고 **'바로 가기 만들기'**. 로그온 때 그 exe 가 창 없이 허브를 켠다
  (바로 가기가 예전 exe 를 가리켜도 등록된 판이 더 새것이면 등록된 exe 로 켜고 등록은 바꾸지 않는다).
- 끄기: 그 폴더에서 바로 가기를 지운다.
- Claude 는 이 폴더·레지스트리 Run 키·작업 스케줄러에 쓰지 않는다 — 일부 백신의 행동 탐지가 "자기 복사 → 시작프로그램 등록"을 악성 드로퍼로 막은 적이 있다.

## 금지 (Claude)

- exe 복사·이동·이름 바꾸기 — 받은 폴더의 `UartHub.exe` 를 그대로 실행한다. 예전 설치기 `UartHub.exe install`(자기 복사·시작프로그램 등록)도 부르지 않는다.
- `powershell -ExecutionPolicy Bypass` 등 실행 정책 바꾸기 — PowerShell 자체가 필요 없다.
- 시작프로그램 쓰기: `shell:startup` 폴더, `HKCU\…\Run` 키, 작업 스케줄러(옛 작업 `\uart_hub` 를 지우는 것도 사용자 몫).
- MotW(Zone.Identifier) 지우기: `Unblock-File`, `Remove-Item -Stream Zone.Identifier`, `:Zone.Identifier` 스트림 덮어쓰기 — 사용자에게 속성 → '차단 해제' 를 안내만 한다.
- `uart connect`·`uart disconnect` 실행(인자가 있든 없든 — 사람 전용 `/connect-uart`·`/disconnect-uart`).
- `--allow-downgrade` 를 사용자에게 묻지 않고 붙이기.
- 실패를 감추려는 반복 실행·다른 방법 시도 — 정해진 한 번의 재시도 말고는 보고하고 사용자 판단을 기다린다.

## 문제가 생기면

- `failed`·`rolled_back`·`blocked` 의 `message`, 그리고 `log`(허브 폴더의 `install.log`)·`setup_last` 경로를 담당자에게 보낸다.
