# UART 허브 (UartHub.exe)

[English](README.md) | **한국어**

> **처음이신가요?** 그림으로 보는 **[사용 가이드](docs/GUIDE.ko.md)** 부터 보세요 — 설치, 보드 연결, Claude 에게 맡기기, 끊기, 에이징을 화면과 함께 설명합니다.

---

## 개요

Windows 에 상주하며 FTDI 케이블을 잡고, 케이블마다 고정 TCP 포트(`127.0.0.1:70xx`)로 사람(터미널 telnet)과 Claude(`uart` CLI)가
같은 콘솔을 나눠 쓰게 한다. 전체 로그는 클라이언트와 무관하게 끊김 없이 남는다. 입력 우선권은 사람에게 있다.
배포물은 서명 없는 단일 파일 `UartHub.exe`(.NET 10 self-contained, WinExe) 하나다. 사용자 PC 에 pwsh·.NET 이 없어도 된다.

- **설치하지 않는다(휴대용)**: 사용자가 exe 를 아무 폴더(예: `C:\Tools\UartHub\`)에 두고 더블클릭하면 그 exe 가 그 자리에서 허브가 된다.
  자기 복사·다른 위치의 복사본 실행·시작프로그램/시작 메뉴 바로가기·작업 스케줄러를 쓰지 않는다 — 예전 설치기의 "자기 복사 → 시작프로그램 등록" 동작을 일부 백신의 행동 탐지가 악성 드로퍼로 막았고,
  압축한 단일 파일 exe 도 실행 압축 개체로 오탐해 지웠다. 그래서 휴대용이고 압축하지 않는다. 자동 실행은 사용자가 `shell:startup` 에 바로 가기를 직접 만든다.
- **Claude 가 등록하는 배포**: 배포물은 폴더 `UartHub_<ver>/{INSTALL_UART_HUB.md, UartHub.exe, USAGE_ONE_PAGE.md, GUIDE.md, SHA256SUMS}`(`build.sh --package`, 제품 프로필이 있으면 `UartHub.profile.json` 도). 사용자는 폴더를 둘 곳에 두고
  Claude Code 에 "INSTALL_UART_HUB.md 참고해 등록해줘" — Claude 가 `UartHub.exe setup --json`(계획만) → `setup --yes --json`(창 없는 휴대용 등록·다른 exe 의 허브 교체, 실패하면 되돌림) → `uart ls`·`uart hub status` 로 확인한다(`uart connect` 는 사람 전용). 더블클릭은 대안으로 남는다.
- **필요할 때만 연결**: 휴대용 config 는 `on_demand: true` — 꽂아도 COM 을 열지 않고(Pending, 묻지 않음), 사용자가 Claude Code 에 `/connect-uart COMx` 를 입력하면
  감독이 점유 프로그램을 확인(허용 목록 터미널 세션은 확인 창 뒤 그 세션만 끝냄)하고 연결·터미널을 띄운다. `/disconnect-uart COMx` 는 놓는다.
- **에이징 이벤트 감지**: 코어가 연결된 케이블의 줄마다 패턴을 보고(커널 panic·oops·bug·oom, 제품 프로필이 정한 앱 오류, 부팅 cmd/soft/hard)
  `<log_root>\events\<port>.jsonl` 에 쌓고 오류마다 앞뒤 로그(앞 200줄·뒤 100줄 또는 30초)를 남긴다. `log_layout: 2`(휴대용 시작·setup 이 더함)면 jsonl 은 `<허브 폴더>\events\`(내부 기록),
  사람이 보는 txt 는 알림 종류 묶음만 `<기준>\에이징이벤트\<날짜>\<HHMMSS>_<보드>(<포트>)_<종류>[_의심].txt`(머리 → 앞 200줄 → 이후 → `결과:`), 콘솔 로그는 `<기준>\콘솔로그\`(.raw·.idx·live 숨김). 감독은 트레이 풍선(`events.notify` — 기본 panic·oops·app_error·confirm 과 panic/oops 가 cause 인 부팅만,
  재부팅·bug·oom·missed 는 기록만: 전원 on/off 에이징의 매 회차 hard 리셋으로 알림이 넘치지 않게 · 포트당 1분에 1번), Claude 는 `uart events --follow`(기본 감시 `--kind panic,oops,app_error,confirm`)·`uart aging` 으로 읽는다.
- **재부팅 뒤 자동 프롬프트 깨우기(config `auto_nudge`, 기본 true)**: 추적기가 run 구간 안의 실제 재부팅
  (B 전: 로더 표시 또는 커널 시각이 run 밖의 마지막 값보다 2초 넘게 작음 — 깨진 글자 뒤 `abce[    0.314164]` 도, B 뒤: 앞 RX 무음 0.5초 이상의 로더 표시만)을 'R' 마커로 알리면
  Arbiter 가 그 run 을 바로 `stranded` + `rebooted:true` + `started` 로 끝내고 잠금은 재부팅 뒤 첫 프롬프트 확정 때 사람 입력 없이 푼다(hub.log "직렬화 잠금 해제 … (재부팅으로 명령 소멸 …)").
  Claude 가 기다릴 때(재부팅 소멸 잠금·큐의 Claude 요청·60초 안의 booting/unknown/serialized 101) 추적기 `CanNudge`(UNKNOWN 이거나 커널 단계 뒤 BOOTING, **커널 줄** 무음 `quiet_ms` —
  앱 줄이 계속 흘러도, 사람 유휴·사람이 친 줄 없음·화면 모드 아님)가 참이면 레인이 CR 1회(`auto_nudge_gap_s` 10초에 1번·부팅당 3번, .log `>>> [hub-nudge]`). 연결·복원 뒤 아직 재부팅을 보지 못한 UNKNOWN(처음 연결하는 케이블 등)이면 맨 CR 대신 probe 처럼 `^E ^U CR` — 다른 터미널에서 치다 만 줄을 지운 뒤 깨워 그 줄을 실행하지 않는다. 깨운 CR 에 답이 없으면 BUSY(101 `busy_since` + `hint:"no_output"`)로 남고 더 깨우지 않는다(사람이 터미널에서 Enter). `wait --nudge` 도 같은 완화 조건.
  booting·unknown·재부팅 잠금으로 기다리는 run 은 30초 넘으면 `--timeout` 전이라도 101. `auto_nudge:false` 면 이 기능 전 동작 그대로(시험 하네스 기본값 — 초기 시나리오는 그 계약을 본다).
- **에이징 세션(`log_layout: 3` — 새 등록·setup·휴대용 시작이 3 으로 맞춤, 2 는 3 으로)**: DiskWriter 는 그대로 `cfg.LogRoot` = `<허브 폴더>\임시기록`(숨김)에 쓰고, 코어의 `SpoolSweeper` 가
  닫힌 파일 중 `spool_keep_hours`(24)를 넘은 것·합계 `spool_max_mb`(2048)를 넘는 오래된 것을 지운다(지금 쓰는 파일·1분 안에 바뀐 파일은 지우지 않음). 세션(포트별, `SessionStore` = `<허브 폴더>\sessions.json`, 재시작 뒤 이어 감)은
  코어 명령 `session start <c> [이름]|stop <c>|status`(COM 을 열고 닫지 않음 — Claude 도 부름, 연결 안 된 포트는 102 + /connect-uart 안내)·트레이 COM ▸ 에이징 시작…(AgingForm)/에이징 끝내기로 바꾼다.
  세션 폴더 `<기준>\에이징\<이름>_<yyyyMMdd-HHmm>\` = `콘솔.log`(사건 추적 스레드가 같은 RX 줄을 DiskWriter 와 같은 줄머리로, 재시작·재연결 뒤 `*** 기록 끊김 … ***`) · `이벤트\<HHMMSS>_<종류>.txt` · 끝낼 때 `요약.txt`.
  layout 3 은 세션 중인 포트만 판정한다(jsonl·풍선도 세션만). `uart session summary` = `aging`(이전 이름 유지). 기록 중 표시: 트레이 `tray_rec.ico`(경고 > 에이징 > 실행 > 정지)·툴팁 "에이징 중 N개"·
  메뉴 정보 줄 "● 에이징 중 · 이름 · 경과 · 마지막 출력 N초 전"(● 는 그린 점 — 초록, 10분 넘게 출력 없으면 빨강)(10분 넘으면 "출력 없음 N분")·터미널 창 제목 끝 " · 에이징 중"(TermTitler, ls --detail 의 케이블 `session`).
- **제품 프로필**: 보드·펌웨어마다 다른 기본값(로더 표시·부팅 경계·U-Boot 프롬프트·앱 오류 패턴·앱 오류 코드)은 코드 밖 `UartHub.profile.json` 에 둔다(§3). 없으면 일반 Linux 기본값.
- **화면(UI_COPY.md)**: 창마다 위쪽 제목 줄(주황 별 + 굵은 제목)·굵은 첫 줄·"자세히 ▸"(경로·pid 등 기술 정보)·오른쪽 아래 시스템 버튼, 진행 창은 단계 3개(로그는 자세히 안). 트레이 아이콘은 상태별
  (`tray_run`·`tray_rec`·`tray_stop`·`tray_warn`), 메뉴는 상태 줄 → 에이징 세션 줄 → COM 포트 ▸(✓ 연결됨 · 터미널 열기 · 에이징 시작…/에이징 끝내기 · 끊기 / 연결) → 터미널 프로그램 ▸ → 로그 폴더 열기 → 에이징 폴더 열기 → 설정 ▸(터미널 프로그램 ▸ · 미연결 UART 정리… · 제거…) → 시작·정지 → 허브 끄기(자동 실행 메뉴 없음 — 문서의 shell:startup 안내만).
  검토용 숨은 하위 명령 `UartHub.exe render-ui <폴더>` 가 창을 띄우지 않고(Show 없이 화면 밖 핸들 + DrawToBitmap) 모든 창·트레이 메뉴 모형을 100%·150% PNG 로 그린다.

| 문서 | 내용 |
|---|---|
| `README.ko.md` | 이 문서(구조·하위 명령·제품 프로필·빌드·시험·배포 판 만들기) |
| `README.md` | 이 문서의 영어판 |
| `INSTALL_UART_HUB.md` | 배포 폴더에 들어가는 설치 안내(사용자 요약 + Claude 가 따르는 등록·업데이트·제거 절차) |
| `docs/GUIDE.ko.md` · `docs/GUIDE.md` | 그림으로 보는 사용 가이드(한국어 · 영어) |
| `USAGE_ONE_PAGE.md` | 사용자용 사용법 한 장 |
| `GUIDE.md` | 짧은 사용 가이드(설치·연결·Claude 에게 시키기·에이징·막힐 때) |
| `UI_COPY.md` | 창·트레이·풍선의 문구와 모양 |
| `skill/SKILL.md` | Claude 사용 규칙(exe 가 환경마다 `~/.claude/skills/uart/` 에 넣는 원본) |
| `LICENSE` | MIT |

## 1. 구조

```
더블클릭 · (사용자가 만든) shell:startup 바로 가기 ─→ UartHub.exe (인자 없음 = 휴대용 실행: install.json 맞추기 → 같은 exe 로 sup)
UartHub.exe sup        감독 + 트레이(Local\uart_hub_sup): 코어 재시작(지수 백오프)·입양·hang 감지, 바탕화면 아이콘, 첫 연결 질문(on_demand 면 없음), 요청 스풀 sup\req → sup\resp,
                       connect·disconnect 흐름(ConnectFlow: 점유자 찾기 ComHolders → 확인 창 → 허용 목록 세션 끝내기 → cable decide|include|exclude → 터미널), 점유 알림 풍선
  ⇅ 상호 감시          감독 → 코어: 재시작·입양 / 코어 → 감독: sup_watch_s(30초)마다 mutex 확인, 없으면 sup 기동(운영 모드만)
  └ UartHub.exe core   코어(Local\uart_hub_core)
       ├ 기록 코어  CableManager(WMI 폴링) · PortReader · ChunkQueue · DiskWriter(.raw/.log/.idx) · RingBuffer
       └ 서비스 층  PromptTracker · Arbiter(TX 단일 큐) · TelnetServer :70xx · ControlServer :7200(JSON lines, 토큰·HMAC 인사) · SpoolWatcher
                    · EventDetector(ControlServer 가 만듦 — 케이블마다 링을 자기 커서로 따라 읽어 사건 판정·기록)
UartHub.exe cli …      호출마다 떴다가 끝남(제어 포트 인사 → 응답 한 줄). WSL·Git Bash 는 래퍼 skill/uart 가 cli --parent-pipe --b64nul 로 부른다
UartHub.exe open <p>   터미널 실행(TeraTerm → PuTTY → MobaXterm) 또는 열린 창을 앞으로
UartHub.exe setup [--yes] [--json] [--no-start] [--allow-downgrade]   창 없는 휴대용 등록(Claude 가 INSTALL_UART_HUB.md 를 따라 부름, --yes 없으면 계획만)
UartHub.exe uninstall [--yes] | version · install(예전 설치기 — 명시적 하위 명령으로만 남김)
```

### 저장소 구성

| 위치 | 내용 |
|---|---|
| `src/*.cs` | Core 라이브러리(`UartHub.Core.csproj`): 기록 코어(`PortReader RingBuffer ChunkQueue DiskWriter`), 프롬프트 추적·송신 중재(`PromptTracker Arbiter TxTokens`), 서비스 층(`TelnetServer ControlServer SpoolWatcher`), 감독(`Supervisor`), 설정·제품 프로필(`Config ProductProfile`), 사건 감지(`Events EventLog Sessions`). 사건 감지: `Events`(EventKinds·EventPatterns·EventScanner·EventDetector — 판정·기록) · `EventLog`(EventRec·aging 요약·EventTail·EventBalloons — 읽기·풍선 묶기). 제품 프로필: `ProductProfile`(읽기·검증·앱 오류 코드). 에이징 세션: `Sessions`·`SpoolSweeper` |
| `src/host/` | `CoreMain`(core) · `Cli`(cli, `Cli.Events` = events·aging) · `OpenCmd`·`Terminals`·`DesktopIcons`·`ShellLink`(사람 접속) · `ConnectFlow`·`ComHolders`(connect·disconnect, 점유 프로세스 찾기·끝내기) · `Installer`·`InstallInfo`·`TokenFile`·`SkillsDeployer`(설치) · `SystemAccess`(시험 격리를 위한 단일 시스템 접근 계층) |
| `app/` | WinForms 트레이 exe(`UartHub.csproj`, WinExe, exe 아이콘 `assets/UartHub.ico`): `Program`(하위 명령 분기) · `UiKit`(창 공통 모양) · `TrayApp`(트레이·메뉴·풍선) · `InstallForm` · `FirstConnectForm` · `ConnectForms`(connect 확인 창·안내 창) · `RenderUi`(검토용 PNG). `skill/*`·`assets/`(트레이 아이콘·별)를 EmbeddedResource 로 넣는다 |
| `testing/` | 시험 DLL(`UartHub.Tests.csproj`, 클래스 라이브러리): 시뮬레이션 시나리오·FakeBoard·SimCore. pwsh 하네스가 로드하고, 코어는 `--sim-dll` 로 로드한다(운영 exe 에는 들어가지 않음) |
| `tests/` | 실행기 `run_sim.sh`·`run_sim_wsl.sh`·`run_sim_gitbash.sh`(WSL 드라이버), `run_sim.ps1`·`run_sim_wsl.ps1`(pwsh 하네스), `compare_ok_lines.sh`, `check_skills_wsl.sh`, `baseline_phaseA.sha256`, `baseline_ok_lines.txt`, `profile.test.json`(시험용 가상 제품 프로필) |
| `docs/` | 그림 사용 가이드(`GUIDE.ko.md` 한국어, `GUIDE.md` 영어)와 그림(`images/` — `UartHub.exe render-ui` 로 그린 화면, 개념도) |
| `skill/` | exe 가 배포하는 Claude Code skill 원본: `SKILL.md`(uart), 래퍼 `uart`(python3 불필요), 사용자 전용 slash skill `connect-uart/SKILL.md`·`disconnect-uart/SKILL.md`(`disable-model-invocation: true`) |
| `build.sh`, `Directory.Build.props` | 빌드(§4)·공통 빌드 속성 |
| `README.md` 외 문서, `LICENSE` | 위 문서 표, MIT |

공개하지 않는 것(`.gitignore`):

| 위치 | 내용 |
|---|---|
| `internal/` | 비공개 제품 프로필(`profile.json`), 실보드 로그 발췌·golden 캡처와 그것을 쓰는 시나리오, 예전 PowerShell 프로토타입, 팀 배포 기록. 이 폴더가 있으면 시험 DLL 이 그 비공개 시나리오도 함께 컴파일한다 — 공개 저장소를 받은 경우에는 공개 시나리오만 돈다 |
| `out/<ver>/` | 빌드 산출 `UartHub.exe`, `lib/UartHub.{Core,Tests}.dll`, `vB/UartHub.exe`(버전만 +1 인 업데이트 시험용). `out/release/` 는 배포 후보, `out/package/` 는 배포 폴더 |
| `artifacts/` | dotnet 중간 산출(obj/bin/publish, `gen/BuildInfo.g.cs`) |
| `tests/results/` | 시험 결과 |

휴대용 실행 PC 의 파일: 사용자가 둔 `UartHub.exe`(그대로 — 복사본 없음)와 그 옆의 `UartHub.profile.json`(있으면), `%LOCALAPPDATA%\uart_hub\{install.json(portable:true, exe = 그 exe), icons.json, prefs.json, setup_last.json(setup 결과)}`(설정·결과 파일만, 실행 파일·.lnk 없음),
`<log_root>\hub\{config.json, state.json, token, control.json, core.pid, sup.json, hub.log, install.log, uninstall_<시각>.log, alive\, sup\req|resp\}`, `<log_root>\YYYYMMDD\<port>_<board>_<HHMMSS>.{log,raw,idx}`,
`<log_root>\events\{<port>.jsonl, YYYYMMDD\<port>_<board>_<HHMMSS>_<kind>.txt}`(사건·앞뒤 로그 — `log_layout` 없을 때),
`log_layout: 3` 이면 `<기준>\{에이징\<이름>_<yyyyMMdd-HHmm>\{콘솔.log, 이벤트\<HHMMSS>_<종류>.txt, 요약.txt}, hub\{…, sessions.json, events\<port>.jsonl, 임시기록\{YYYYMMDD\…, live\}(숨김, 24시간·2GB)}}`(2 → 3 때 옛 `콘솔로그\`·`에이징이벤트\` 는 그대로),
`log_layout: 2` 면 `<기준>\{콘솔로그\{YYYYMMDD\…, live\}, 에이징이벤트\YYYYMMDD\<HHMMSS>_<보드>(<포트>)_<종류>.txt, hub\{…, events\<port>.jsonl}}`(옛 배치는 허브가 꺼진 휴대용 시작·setup 때 같은 볼륨 이름 바꾸기로 한 번 옮김 — LogMigration, install.log),
state.json 의 `retired_ports`(미연결 UART 정리로 지운 번호 — 다시 주지 않음),
바탕화면 `<보드|COMx> (<포트>).lnk`(대상 = 그 exe, OneDrive 바탕화면이면 시작 메뉴 `UART 허브\`), 환경마다 `~/.claude/skills/uart/{SKILL.md, uart, .uart_hub}`(래퍼에 그 exe 경로)
와 `~/.claude/skills/{connect-uart,disconnect-uart}/{SKILL.md, .uart_hub}`.
`log_root` 는 D: 가 로컬 고정 디스크면 `D:\uart_logs`, 아니면 `%USERPROFILE%\uart_logs`(처음 실행 때 한 번 정하고 `install.json` 에 남김).
예전 설치기(`install`)로 설치한 PC 는 여기에 `%LOCALAPPDATA%\uart_hub\{UartHub.exe, install.log}`, 시작프로그램 `UART 허브.lnk`, 시작 메뉴 `UART 허브\{UART 허브, UART 허브 제거}` 가 더 있다.

## 2. 하위 명령

| 명령 | 동작 |
|---|---|
| (인자 없음) | **휴대용 실행**. 처음(portable `install.json` 없음): 확인 창 1개(로그 위치·skills 넣을 곳·자동 실행 직접 안내) → [시작] → 허브 폴더·토큰·config(없으면 `{"on_demand": true}` — 있는데 `on_demand` 키가 없으면 그 한 줄만 더함, 다른 키·주석 그대로) → `install.json`(exe = 이 exe, state=installed, portable:true) → 같은 exe 로 `sup --icons-dir <바탕화면>` → 건강 확인 → skills. 다음부터: 창 없이 `install.json` exe·version 을 이 exe 로(바뀌었으면 옛 exe 를 가리키는 자기 아이콘 대상·skills 도) → sup. 감독이 이미 있으면 같은 exe = 트레이 풍선 "이미 실행 중"(감독 요청 `notice_running`)만, 다른 exe = 확인 창 "기존 허브를 끄고 새 exe 로 바꿀까요?" [예] → update mutex → shutdown → 새 sup(건강 확인 version == vB). portable 아닌 `install.json`(예전 설치기)이 있으면 제거 안내 후 exit 3. 예외: 예전 설치본 `%LOCALAPPDATA%\uart_hub\UartHub.exe` 를 인자 없이 실행하면 `sup` |
| `sup [--no-tray] [--hub-dir D] [--sup-mutex M] [--core-mutex M] [--update-mutex M] [--sim 사양 --sim-dll Tests.dll] [--tick-ms N] [--icons-dir D]` | 감독. 허브 폴더 `--hub-dir` → `UART_HUB_DIR` → `install.json`(이때만 운영 모드). update mutex 가 있으면 exit 0 |
| `core --hub-dir D …` | 코어(감독이 띄움). mutex 3초 대기 실패 exit 3, `core.stop` 으로 정상 종료 |
| `cli <명령> …` / `cli -B64Req <b64 JSON>` / `cli --b64nul <토큰>` | CLI(아래 표). `--parent-pipe` 가 첫 인자면 표준 입력 EOF 에 exit 103(래퍼 TERM 정리) |
| `open <포트> [--from balloon]` | 터미널 열기. 코어에 닿지 못하면 자가 복구(감독·코어를 다시 띄움) |
| `install [--dry-run]` / `install --repair --quiet` | 예전 설치기(복사·바로가기·업데이트·되돌림) — 명시적 하위 명령으로만 남김(기본 동작 아님). `--dry-run` 은 출력만 |
| `setup [--yes] [--json] [--no-start] [--allow-downgrade]` | **창 없는 휴대용 등록**. 시작하자마자 결과 파일에 `result:"running"`. `--yes` 없으면 계획만(결과 파일 말고 무변경): exe·위치(로컬 고정 디스크가 아니면 막음 — UNC·`\\wsl.localhost`·네트워크·이동식)·로그 위치·허브 폴더(정지 상태 `stopped`)·skills 넣을 곳(Windows + WSL 배포판 — 계획은 켜진 배포판만 `skills_wsl.sh probe` 읽기 탐침, 꺼진 것은 `unknown`)·실행 중 허브(pid·버전·exe)·되돌리기(`downgrade`)·옛 설치 방식 잔재(portable 아닌 `install.json`·설치 폴더의 `UartHub*.exe`·자기 표시 시작프로그램/시작 메뉴 바로가기)·MotW(`Zone.Identifier` — 보고만, 지우지 않음, 못 읽으면 null)·경고(다른 프로그램이 쥔 허브 포트·옛 PS 작업 `\uart_hub`(`/query` 읽기만)·`UART_HUB_DIR`)·`shell:startup` 의 UartHub 바로 가기(읽기만)·할 일. 막힘(exit 3, 계획·`--yes` 같음, 무변경): installer mutex(다른 설치기·확인 창)·옛 잔재·위치·update mutex·누구인지 모르는 감독(sup mutex 는 있는데 sup.json 으로 확인 안 됨)·등록 안 된 감독·되돌리기(`--allow-downgrade` 없을 때). `--yes`(installer mutex): 처음이면 휴대용 첫 실행과 같은 등록(허브 폴더·토큰·config — 있으면 `on_demand` 보장 → `install.json` portable·exe = 이 exe → `sup`(작업 이탈 기동, `UART_HUB_DIR` 안 물려줌) → 건강 확인 → skills 3개), 등록이 있으면 휴대용 교체 경로(update mutex → 다른 exe·다른 판의 감독이면 shutdown·core.stop·검증 PID kill → 아이콘 대상·`install.json` → 허브 파일 → sup → 건강 확인 → 바뀐 곳이 있으면 skills). 할 일이 없으면 아무것도 쓰지 않음(`unchanged`, install.log 도 안 씀, 풍선 없음). 실패 = `rolled_back`(1): 처음 등록은 띄운 허브·`install.json` 을 지우고, 교체·맞춤은 새 허브를 내리고 `install.json` exe·version·아이콘을 이전 값으로, 돌던 허브였으면 그 exe 로 감독을 다시 켜고 건강 확인. 건강 확인 = sup.json·core.pid + `cli hub status`(제어 포트 응답, 버전 일치) 최대 30초. 배포판 탐침·skills 배치는 setup 전체 120초 예산 안(넘으면 `failed` `budget…`), `--yes` 는 계획 탐침 결과를 배치에 다시 쓴다(두 번 깨우지 않음). 결과 = 한국어 줄(또는 `--json` = JSON 한 줄만) + `%LOCALAPPDATA%\uart_hub\setup_last.json`(같은 JSON) `{command, mode(plan\|apply), result(running\|planned\|registered\|replaced\|updated\|unchanged\|blocked\|rolled_back\|failed), rc, message, version, exe, location, motw, legacy, install, running, downgrade, log_root, hub_dir, config, stopped, warnings, autostart, skills, actions, started, health, hub_running, control_port, next, log, setup_last, at}`. 종료코드 0·1·3·106 |
| `uninstall [--dry-run \| --yes]` | `--yes` = 확인 창 없이 같은 범위(다른 설치기가 돌면 exit 3). 휴대용 제거는 단계마다 오류를 잡고 계속한 뒤 실패가 있으면 exit 1(`# 제거 일부 실패`), 자기 표시 옛 잔재(시작프로그램·시작 메뉴 바로가기, 설치 폴더 `UartHub*.exe`)도 지운다. `%LOCALAPPDATA%\uart_hub` 에 `prefs.json`·`setup_last.json` 만 있으면 등록 없음(exit 3). portable `install.json` 이면 확인 창 → 허브 정지(shutdown) → 자기 아이콘·`icons.json` → skills(표시가 맞는 것) → `install.json` 삭제. exe·로그·폴더는 남기고 자기 이미지를 옮기지 않는다. 예전 설치본이면 지금까지의 제거(바로가기·설치 폴더) |
| `version [--json]` | 버전. `--json` 은 `version`·`core_mvid`·`self_contained`·`runtime` |
| 그 밖 | exit 106 |

CLI 명령(경로 표기 `UART_PATH_STYLE=win|wsl|mixed`, 래퍼가 정함). 대상 `<c>` 해석: 정수(TCP 포트) → `COM\d+` → alias → 보드명(2개 이상이면 105).

| 명령 | 동작 |
|---|---|
| `ls [--detail]` | 케이블 목록(`attached/detached/pending/released/ignored/held_by_other/bind_failed`, 보드명, 프롬프트 상태). `--detail` 은 `description`·`mode_explicit`·`rx_since_attach`(+0 이면 `hint:"no_output"`·`hint_text`) 포함 |
| `status <c>` | 케이블 하나의 상세(`state`, `blocked_reason`, `last_lines`, `log`, `rx_since_attach`·`hint` …). run·send·reboot 의 101 도 0바이트면 `rx_since_attach`·`hint` |
| `connect [COMx [baud]]` · `disconnect COMx` | **사람 전용**(slash skill `/connect-uart`·`/disconnect-uart`). 감독 요청 스풀(`args`)로 감독에 넘기고 최대 175초 기다린다. connect: 점유 프로그램이 허용 목록 터미널(MoTTY·ttermpro·putty·kitty)이면 확인 창 [끊고 연결]/[취소], 그 밖은 안내 창 + 2초마다 다시 확인(120초) → pending 은 `cable decide board`·released 는 `include` → 터미널 → 2초 뒤 `rx_bytes`. 응답 `{status, com, port, baud, board, state, rx_bytes, holder_closed, terminal}`, 취소 106 `cancelled`·시간 초과 100(둘 다 무변경). baud(표준 1200~921600 만, 아니면 106)를 주면 점유자를 비운 뒤 코어 `cable baud <id> <rate>`(메모리 설정 + `config.json` `cables.<id>.baud` 저장 — 다른 키 원문 그대로, 최상위 주석은 남지 않음; 붙어 있으면 닫았다가 다음 스캔에서 새 보오로 다시 열고 .log 에 `*** baud A -> B ***`), 주지 않으면 그 케이블의 지금 보오. 인자 없으면 COM 마다 연결 상태·보오 표. disconnect = `cable exclude`(포트 유지) |
| `run <c> "<cmd>"` | `sh -c '…' </dev/null` 로 봉인해 실행하고 `out`·`rc` 회수. `[--timeout s] [--idle s] [--inline] [--no-interrupt] [--force] [--req-id X] [--clear-line] [--json]` |
| `send <c> "<raw>"` | 토큰 문법(`{ENTER}` `{ESC}` `{NONL}` …)으로 송신, 응답에 `rx_offset` |
| `probe <c>` | 상태를 모를 때 조건이 맞으면 `^E^U` → CR 1회. attach 직후는 `--user-ok` 필요 |
| `wait <c> --pattern RE` | `[--since epoch:offset] [--timeout s] [--nudge]`. 링 버퍼 소급 검색, 범위 밖이면 `since_expired` |
| `reboot <c>` | AT_PROMPT 에서 `reboot` 송신, 송신 직전 `rx_offset` 반환 |
| `log <c> [--tail N \| --since OFF]` · `logpath <c>` | 로그 읽기 · 현재 `.log`·`.raw`·`.idx` 경로 |
| `hub status \| stop \| start \| upgrade` | 상태 / 정지(`stopped` 영속, COM 해제, 아이콘 삭제) / 재개 / 같은 exe 로 코어 재시작. `hub status --json` 의 `profile` = `{name, path, error}`(제품 프로필이 없으면 null, §3) |
| `events [<c>] [--since ISO\|12h\|30m] [--kind k1,k2] [--follow [--max N]] [--json]` | **읽기만**(코어·보드에 보내지 않음, 허브가 꺼져도 됨): `<log_root>\events\<port>.jsonl`(log_root 는 코어와 같은 규칙: config.json → last-good → 허브 폴더의 부모, `log_layout: 2` 면 `<허브 폴더>\events` — Config.EventsJsonlDir). 이름이 바뀐 txt(더 심한 종류 합류, `context_was`)는 앞 기록의 `context` 도 새 이름으로 읽는다. `--json` = `{status, exit, events_dir, ports, since, count, events:[사건 줄]}`(2000건 상한). `--kind` 는 `panic,oops,bug,oom,app_error,boot,confirm,missed,attach,detach`·`boot:cmd\|soft\|hard`. `--follow` = 머리 줄 `{follow:true, ts(시작 시각),…}` 뒤 새 사건마다 JSON 한 줄(파일을 이어 읽으므로 코어 재시작과 무관). 끝 = `--max N` 줄 뒤 0 · 표준 출력을 읽는 쪽이 닫힘 141 · 래퍼 TERM → `--parent-pipe` EOF 103. `<c>` = 포트·COMx(사건 기록의 attach com)·alias·보드명(state.json) — 허브 없이 푼다(없음 102·여럿 105). `--follow`·`--kind`·`--max` 는 이 두 명령만 받는다(코어 명령은 106) |
| `session start <c> [이름]` · `session stop <c>` · `session status [--json]` | 에이징 세션(코어): COM 을 열고 닫지 않고 보드로 보내지 않음. start = 연결된 포트만(아니면 102 + `/connect-uart` 안내), 응답 name·folder. stop = 요약.txt 저장, 응답 summary·summary_text. status = 세션마다 port·com·board·name·folder·started·elapsed_s·console_log{path,bytes,lines}·last_rx_age_s·events{total,by_kind}·boots·gaps·connected(비JSON 은 한 줄씩) |
| `session summary <c> [--since …] [--json]` = `aging <c> [--since …] [--json]` | 읽기만: 부팅 수(cmd/soft/hard)·`unexpected_reboots`·`boot_interval_s`(최소·중간·최대, 기록 공백을 걸친 간격은 뺌 — `excluded_gap`), `boots.with_cause`(panic/oops 뒤 리셋), `kinds`(panic·oops·bug·oom·app_error·confirm·missed 의 수·첫·마지막·`no_context`·`suspect`), `contexts`(앞뒤 로그 목록·있음 여부·suspect), `period_s`·`covered_s`·`gaps`(케이블이 붙어 있지 않던 시간: detach ~ 다음 attach — `--since` 앞의 마지막 기록이 detach 면 기간 시작부터 —, 허브가 꺼져 있던 attach 의 `gap_s`). 수는 실시간 사건만 — suspect 는 `suspect_events`·`boots.suspect`·`kinds.*.suspect` 로 따로 `--since` 가 없고 그 포트가 에이징 중이면 세션 시작부터 요약한다(`scope:"session"` + `session{name, started, folder, console_log}`), 주면 `scope:"since"`. `boot_detection` = 부팅을 세는 로더 표시·`boot_re`·프로필, 30분 넘게 부팅이 0이면 `hint:"no_boots_seen"` — 재부팅하는 보드라면 허브가 그 부팅 줄을 모르는 것(제품 프로필을 맞춘다). |
| `cable …` | **트레이 전용** — `cli` 는 106 "트레이에서만"(Claude 가 대신 답하지 않게). 코어 동사: `decide <id> board\|ignore` · `exclude <id>` · `include <id>` · `baud <id> <rate>`(감독의 connect 흐름만 보냄) |

종료코드(판정의 정식 계약은 `--json` 의 `status`·`rc`):

| 코드 | status | 의미 |
|---|---|---|
| 0~255 | `ok` | `run` 은 보드 rc 그대로. 그 밖의 명령은 0 |
| 100 | `timeout` / `stranded` | 시간 초과 / 보낸 줄의 시작이 확인되지 않음(`rc:null`, 재시도 금지) |
| 101 | `busy` / `interrupted` | 비프롬프트·사람 사용 중(`blocked_reason`) / 실행 중 사람 Ctrl-C |
| 102 | `no_cable` | 케이블 없음·분리, `blocked_reason` `pending`·`released`·`ignored` |
| 103 | `hub_down` | 허브 미응답(`stopped:true`·`restarted:true`), 제어 포트의 상대가 허브가 아님, proof 불일치 |
| 104 | `md5_mismatch` | push 검증 실패(아직 없는 기능) |
| 105 | `ambiguous` | 대상이 여러 개(`candidates`) |
| 106 | `rejected` / `since_expired` / `unsupported` | 거부 / `--since` 범위 밖 / 감독 미연결 코어의 `hub stop/start` |

- 제어 연결 인사: 허브가 먼저 `{"uart_hub":1,"nonce":…}`, 클라이언트는 `hello = HMAC-SHA256(token, "c|"+nonce)` 와 `cnonce`, 허브는 `proof = HMAC-SHA256(token, "s|"+cnonce)`. 토큰 원문은 오가지 않는다. CLI 의 제어 포트: `UART_HUB_CONTROL_PORT` → 살아 있는 코어의 `core.pid` → `config.json` → 7200.
- telnet 연결은 케이블을 뽑아도 끊지 않는다(분리·재연결 배너는 터미널 화면에만). 끊기는 것은 허브 정지·코어 재시작·느린 클라이언트(송신 큐 1MB 초과)뿐이다.
- **재연결 줄 지우기**: 셸 줄 편집 중에 뽑은 케이블은 다시 꽂은 뒤 보드에 처음 가는 입력 앞에 `^E^U` 를 붙인다(`.log` 에 `>>> [hub-clear] …`). 프레이밍 오류 바이트(`??????`)가 다음 Enter 에 실행되는 것을 막는다. vi 같은 화면 프로그램·BUSY 는 제외. 케이블별로 끄려면 `config.json` `cables.<id>.replug_clear: false`. 근본 대책은 보드 콘솔 초기화의 `stty inpck ignpar ignbrk -brkint`.

## 3. 제품 프로필 (`UartHub.profile.json`)

보드·펌웨어마다 다른 기본값 — 로더 표시·부팅 경계·U-Boot 프롬프트·앱 오류 패턴·앱 오류 코드 이름 — 은 코드에 두지 않고 exe 옆의 JSON 파일(제품 프로필)로 둔다.
프로필이 없으면 일반 Linux 기본값으로 돈다. 프로필은 선택이다.

- **찾는 순서**: 환경변수 `UART_HUB_PROFILE`(파일 경로 — 빈 값이면 프로필 없음, 개발·시험용) → `UartHub.exe` 와 같은 폴더의 `UartHub.profile.json` → 없음(일반 기본값).
  프로세스마다 처음 쓸 때 한 번 읽으므로 고친 뒤에는 허브를 다시 켠다(트레이 → 허브 끄기 → exe 실행).
- **우선순위**: 일반 기본값 < 프로필 < `config.json`. config 에 `loader_marker_re`·`boot_re`·`uboot_prompt_re` 를 주면 그 값이 이긴다.
  사건 패턴은 프로필의 `event_patterns` 가 일반 기본 패턴에 더해지고, 그 위에 config `events.patterns` 의 `add`·`off` 가 적용된다(`off` 에 프로필 패턴 문자열도 쓸 수 있다).
- **깨진 프로필**(JSON 오류·모르는 키·틀린 정규식)은 통째로 무시하고 일반 기본값으로 돈다. 이유는 `uart hub status --json` 의 `profile.error` 와 hub.log 에 남는다.
  `hub status --json` 의 `profile` = `{name, path, error}`(프로필이 없으면 `null`, 정상이면 `error:null`).
- 형식은 JSONC(주석·끝 쉼표 허용). 키:

| 키 | 뜻 | 프로필이 없을 때(일반 기본값) |
|---|---|---|
| `name` | 표시용 이름(`hub status` 의 `profile.name`) | — |
| `loader_marker_re` | 로더 표시: 줄 어디에 있든(앞에 커널 시각·깨진 글자가 있어도) 언제나 실제 리셋을 뜻하는 로더 배너. `""` = 없음 | 없음 |
| `boot_re` | 부팅 경계 줄 | `reboot: Restarting system\|^\[\s+0\.0\d+\]` |
| `uboot_prompt_re` | U-Boot 프롬프트 | `^=> ` |
| `event_patterns` | `{종류: [정규식…]}` — 종류마다 일반 기본 패턴에 더한다. 종류 = `panic`·`oops`·`bug`·`oom`·`app_error` | panic·oops·bug·oom = 표준 Linux 커널 메시지(`Kernel panic - not syncing`, `Unable to handle kernel`·`Internal error: Oops`, `BUG: `·`WARNING: CPU:`·soft lockup·rcu stall·task blocked, `Out of memory: Kill`·`invoked oom-killer`), `app_error` = 없음 |
| `app_codes` | `[{"re": 정규식, "code"?: 문자열}]` — 앱 오류 txt 이름 `앱오류_<코드>` 의 코드. 처음 맞은 규칙이 이긴다. `code` 가 없으면 맞은 글자 그대로 | 없음 — 맞은 앱 오류 패턴 글자의 첫 낱말 |

예(가상 제품 — 이름·문자열은 설명용):

```json
{
  "name": "evb-a",
  "loader_marker_re": "LDR>T",
  "boot_re": "reboot: Restarting system|LDR>T|^\\[\\s+0\\.0\\d+\\]",
  "uboot_prompt_re": "^evb: ",
  "event_patterns": { "app_error": ["APPFAULT\\((?!(?i:BA\\)\\(000f\\)))", "APP_ABORT reason="] },
  "app_codes": [ { "re": "APPFAULT\\([^)\\s]*\\)" }, { "re": "(?i)app_wdt", "code": "WDT" } ]
}
```

- `LDR>T` 는 이 가상 보드의 로더가 리셋 때마다 찍는 표시다. 로더 표시는 커널 링·pstore 에 들어가지 않아 옛 로그로 다시 찍히지 않으므로, 프로필에 정하면 판정이 더 단단해진다(§8 판정). 정하지 않은 보드는 `boot_re` 의 커널 부팅 줄이 경계다.
- `APPFAULT\((?!(?i:BA\)\(000f\)))` 의 부정 전방 탐색은 정상 종료 서명(여기서는 `APPFAULT(BA)(000f)` — 예를 들어 reboot 때 앱이 정상 종료하며 남기는 코드)을 앱 오류에서 빼는 방법을 보여 준다.
- `app_codes`: `APPFAULT(ED)` 줄은 `앱오류_APPFAULT(ED).txt`, `app_wdt …` 줄은 `앱오류_WDT.txt`.
- **배포**: 프로필은 팀(제품)마다 자기 배포 폴더에서 exe 옆에 둔다. `build.sh --package <ver>` 는 비공개 `internal/profile.json` 이 있으면 배포 폴더에 `UartHub.profile.json` 으로 넣는다. 시험용 가상 프로필은 `tests/profile.test.json` 이다(개발·시험에서는 `UART_HUB_PROFILE` 로 아무 프로필 경로나 줄 수 있다).

## 4. 빌드

저장소 루트(이 폴더)에서:

```bash
./build.sh                    # 새 빌드(버전 = 지금 시각 17자리)
./build.sh --pin <ver>        # 배포 후보 고정(빌드 안 함, §6)
./build.sh --use <ver>        # 지정 빌드의 Windows 사본 확인만(빌드 안 함, 시험 드라이버의 무빌드 옵션이 부름)
./build.sh --package <ver>    # 배포 폴더 out/package/UartHub_<ver>/ + .zip(시험한 고정판 exe 그대로 — <ver> 필수·--pin 돼 있어야, 끝에 EXE_SHA256·ZIP_SHA256, §6)
~/.dotnet/dotnet build-server shutdown   # 끝나면(메모리가 적은 PC)
```

- WSL 에서만 빌드한다. .NET SDK 10(10.0.403)은 `~/.dotnet`(사용자 권한). 세 프로젝트를 한 번만 컴파일하고 `publish --no-build` 로 번들 → 시험 DLL 이 exe 안의 Core 와 같은 컴파일 결과다(하네스가 `# mvid=… match` 로 확인).
- 설정: self-contained single-file, **압축 off**(일부 백신이 압축된 단일 파일 exe 를 실행 압축 개체로 오탐해 지운다 — 대신 exe 가 약 100MB), R2R off, WinExe(GUI 비트). WinExe 의 표준 출력은 파이프일 때만 나온다 — Git Bash 래퍼는 `| cat` 경유.
- 산출: `out/<ver>/{UartHub.exe, lib/, vB/}` + Windows 사본 `%LOCALAPPDATA%\uart_hub_dev\<ver>\`(+ 시험 하네스 `tests\*.ps1`, `internal/` 이 있으면 golden). pwsh 는 `\\wsl.localhost` 의 `.ps1` 을 실행 정책 때문에 못 돌리므로 하네스는 항상 이 사본에서 돈다.
- 정리: 17자리 이름 폴더만 최근 3개를 남긴다. `out/release/UartHub-<ver>.exe` 로 고정된 버전과 `release-*` 사본은 지우지 않는다.
- 마지막 줄 `VERSION=` / `EXE_WIN=` / `EXE_SHA256=`(무빌드면 앞에 `BUILD=reuse`).

## 5. 시험 (하드웨어 없음, FakeBoard)

Windows 쪽에 `pwsh`(PowerShell 7)가 있어야 한다. 실행기는 WSL 에서 부른다.

| 이름 | 명령 | 내용 |
|---|---|---|
| part 1·2·3 | `tests/run_sim.sh --quiet --part k/3` | 시나리오를 등록 순서 `i % 3 == k-1` 로 나눔(명령당 600초 제한). 결과 `tests/results/YYYYMMDD_HHMMSS.txt`. connect 계열은 `connect-units`·`connect-holders`·`connect-ondemand`(점유자·끝내기·확인 창은 시험 격리 가짜: `<root>\fake_holders.json`·`fake_kill.log`·`fake_ui.json`). 사건 감지는 `events-units`(part 1, 판정기에 줄 직접)·`events-hub`(part 2, 허브 안 링 추적)·`events-cli`(part 3, 실제 exe 감독·코어 주입 `emit`·`reset`, 풍선 창은 `UART_HUB_TEST_EVENTS="창,대기"`초). 등록은 `setup-portable`(part 2 — 옛 잔재 중단·계획만 무변경·`--yes` 첫 등록·멱등·`uninstall --yes`·`--no-start`, MotW 붙인 exe)·`setup-replace`(part 3 — 다른 exe 의 허브를 창 없이 교체, 포트·설정·로그 유지). 실보드 로그 재생(`events-fixtures`)·golden 캡처 시나리오는 `internal/` 이 있을 때만 들어간다 |
| wsl | `tests/run_sim_wsl.sh --quiet` | WSL 래퍼 계약 10케이스 + 부가 13(`wsl_*.txt`, x11~x13 = events·aging·events --follow) |
| gitbash | `tests/run_sim_gitbash.sh --quiet` | Git Bash 래퍼 계약 10케이스 + 부가 g1~g13(`gitbash_*.txt`, g11~g13 = events·aging·events --follow) |

- **한 번에 하나씩** 돌린다(겹치면 포트·사본 정리가 서로 간섭한다). 시작 전에 고아 `run_sim.ps1` pwsh 가 없는지 본다:
  `pwsh.exe -NoProfile -Command "Get-CimInstance Win32_Process -Filter \"Name='pwsh.exe'\" | ? CommandLine -match 'run_sim' | select ProcessId,CommandLine"`
- **무빌드 옵션**: `--exe-version <ver>`(또는 `UART_HUB_USE_BUILD=<ver>`) — 다시 빌드하지 않고 지정 빌드로 돈다(`build.sh --use`: `release-<ver>` → `<ver>` 사본 순, exe·lib·vB 가 `out/<ver>` 와 바이트가 같아야 함). 결과 머리에 `BUILD=reuse`·`VERSION=`·`EXE_SHA256=`(시험한 exe). 옵션이 없으면 지금처럼 새로 빌드한다.
- **기대값 비교(선택)**: 바꾸기 전 결과로 기준을 만들고(`tests/compare_ok_lines.sh --make <결과> > tests/baseline_ok_lines.txt`), 바꾼 뒤 `cat` 한 결과와 비교한다(정규화한 `ok:` 줄 집합 — 실행마다 달라지는 경로·시각·포트는 자리표시).
  ```bash
  cat tests/results/{p1,p2,p3}.txt > /tmp/all.txt && tests/compare_ok_lines.sh /tmp/all.txt   # "# 차이 0" 이고 exit 0
  ```
- `run_sim.sh` 는 실행 전후 WSL `~/.claude/skills/{uart,connect-uart,disconnect-uart}` 가 같은지도 본다(`[FAIL] wsl-skills-unchanged`). `tests/check_skills_wsl.sh` 는 `skills_wsl.sh` 를 임시 HOME 에서 dash 로 직접 시험한다(extras 포함). 각 설치 계열 시나리오는 실제 Startup·바탕화면·skills·시작 메뉴·`%LOCALAPPDATA%\uart_hub`·schtasks 무변경과 `TestIsolationViolation 0` 을 단언한다.
- 하나만: `tests/run_sim.sh fakeboard-selftest 14`. 고부하(load 60+) 때 hang 계열이 시간 초과로 실패할 수 있다 — 같은 ID 만 다시 돌려 확인한다.

## 6. 배포 판 만들기 (시험한 바이트 = 배포한 바이트)

1. 지금 트리로 새로 빌드: `build.sh` → `VERSION=<ver>`.
2. 후보 고정: `build.sh --pin <ver>` → `out/release/UartHub-<ver>.exe`, `out/release/UartHub-<ver>.sha256`(`sha256sum -c` 형식), 같은 바이트의 `%LOCALAPPDATA%\uart_hub_dev\release-<ver>\`.
3. 후보로 5종을 차례로: `run_sim.sh --quiet --part 1/3 --exe-version <ver>` → `2/3` → `3/3` → `run_sim_wsl.sh --quiet --exe-version <ver>` → `run_sim_gitbash.sh --quiet --exe-version <ver>`.
   결과 머리 `EXE_SHA256` = `.sha256`, (기준을 만들었으면) compare_ok_lines 차이 0. 실패하면 그 ID 만 다시 돌려 확인하고, 두 번 연속 실패면 원인을 조사한다.
4. 배포 폴더: `build.sh --package <ver>` → `out/package/UartHub_<ver>/{INSTALL_UART_HUB.md, UartHub.exe(= UartHub-<ver>.exe 이름만 바꿈), USAGE_ONE_PAGE.md, GUIDE.md, SHA256SUMS}` 와 `out/package/UartHub_<ver>.zip`.
   비공개 `internal/profile.json` 이 있으면 `UartHub.profile.json` 으로 함께 넣는다(§3). exe 해시는 `.sha256` 과 대조하고 `sha256sum -c SHA256SUMS` 를 통과해야 만든다. 고정·5종 시험을 마친 버전만(버전 없이·고정 안 된 버전은 거부). 전달 메시지에 끝줄의 `EXE_SHA256`·`ZIP_SHA256` 을 적는다(SHA256SUMS 는 깨짐만 잡고, 바꿔치기는 이 값으로 확인).
5. 실제 PC 에서 그 폴더로 한 번 등록·연결해 본 뒤 그 폴더(또는 zip)를 배포한다. **배포 판을 위해 다시 빌드하지 않는다**(버전 문자열이 바뀌면 다른 바이트다).
6. 그 뒤 코드를 바꾸면 새 빌드 → 새 `--pin` → 5종 시험을 다시 한다.

## 7. 시험 격리와 운영 허브 주의

- `UART_HUB_TEST_ROOT=<root>` 가 있으면 exe 의 알려진 폴더(LOCALAPPDATA·APPDATA·바탕화면·USERPROFILE·TEMP)·mutex(`Local\uart_hub_test_<root 해시>_{sup,core,update,installer}`)·`wsl.exe`·`schtasks.exe`·WMI 조회·드라이브 판정이 `<root>` 아래 가짜로 바뀐다(`src/host/SystemAccess.cs`). 시험 루트 밖 실제 경로 접근이나 실제 외부 실행은 `TestIsolationViolation`.
- 시험 코어는 `vids: []`(실제 FTDI 열거 안 함)이고 제어·케이블 포트가 17000 미만이면 거부한다. 시험 감독은 트레이를 띄우지 않는다.
- 하네스 직접 실행(`--hub-dir`·mutex·포트를 주는 `run_sim_wsl.ps1`)도 17400/18050 대 포트와 난수 mutex 를 쓴다.
- **건드리지 않는 것**: 같은 PC 에서 돌고 있는 운영 허브(제어 7200·케이블 70xx, 기본 mutex `Local\uart_hub_sup`·`Local\uart_hub_core`), 실제 FTDI, 실제 설치·skills 배치·바탕화면·시작 메뉴·Startup.
- **창을 띄우지 않는다**: `start`, `explorer`, 창 있는 `Start-Process`, mintty, `UseShellExecute`, 트레이 있는 `sup`. 시험·측정은 `--no-tray` 와 파이프로만 한다(시험이 띄운 창은 사람의 작업을 가로챈다).

## 8. 알려진 한계

- 감독·코어가 동시에 죽으면(로그오프·재부팅 말고는 드묾) 다음 로그온·아이콘 더블클릭·CLI 호출까지 기록이 없다. 감독만 죽으면 ≤30초에 코어가 다시 띄운다.
- WSL 에서 TERM·INT 는 Windows 프로세스로 가지 않는다. 래퍼는 `--parent-pipe` 로 정리하지만 `--timeout` 은 항상 준다.
- 알림은 `NotifyIcon` 풍선이라 대기열이 없고, 클릭은 마지막 풍선에만 연결된다. 알림 센터로 넘어간 뒤의 클릭은 보장하지 않는다.
- 서명이 없어 SmartScreen "추가 정보 → 실행"이 필요하다. 백신 정식 검사는 사용자가 exe 를 우클릭해 직접 한다.
- 휴대용은 받은 exe 를 그대로 쓰므로 MotW(Zone.Identifier)가 남는다(예전 설치기의 스트림 복사가 없다). 로그온 때마다 SmartScreen 이 뜨면 사용자가 파일 속성 → '차단 해제'(exe 가 스스로 MotW 를 지우지 않는다 — 방어 회피로 보일 수 있음).
- 트레이 "허브 끄기"(shutdown) 뒤에도 바탕화면 아이콘·Claude 의 `uart` 호출은 자가 복구로 허브를 다시 켠다. exe 를 덮어쓰는 동안 그런 호출이 있으면 파일이 잠겨 덮어쓰기가 실패할 수 있다(다시 종료 → 덮어쓰기).
- exe 를 옮기면 skills 래퍼·아이콘이 옛 경로를 가리킨다 — 새 위치에서 한 번 더블클릭하면 맞춘다(감독이 없을 때 창 없이, 있으면 교체 확인 창). 제품 프로필(`UartHub.profile.json`)도 exe 와 같이 옮긴다.
- Windows 10 은 미검증. **FTDI 드라이버(VCP) 장치만 본다** — 장치 검색이 `FTDIBUS` 장치만 열거하고 `vids` 는 그 안에서 거르기만 하므로 CH340·CP210x 같은 다른 USB-UART 는 설정으로도 잡지 못한다.
- `.tx` 스풀(DrvFs)에 여러 프로세스가 동시에 `>>` 하면 허브와 무관하게 줄이 사라진다 — `uart send` 를 쓴다.
- 코어 재시작 동안 모든 케이블에 수 초 공백(`*** gap … ***`). `config.json` 은 핫 리로드 없음(코어 재시작 필요). 제품 프로필도 같다.
- ESC 단독 송신 뒤 에코가 없으면 BUSY 로 다음 토큰 줄이 막힐 수 있다. `^D` 는 HUMAN_TYPING 을 풀지 않는다. stranded 잠금 중의 재부팅은 감지하지 않는다.
- `partial`(101 의 사람 입력 중 줄)은 RX 에코로 재구성한다 — 타임스탬프 없는 앱 로그가 같은 줄에 붙거나 80열 접힘이 두 번을 넘으면 정확하지 않다.
- 아직 없는 기능: `push`(106), `uboot`, `term`, telnet 상태 배너·최근 줄 재생, 7일 지난 로그 압축, D: 여유 경고. COM 놓기·잡기는 사람 전용 `connect`·`disconnect` 뿐이다.
- 점유자 찾기는 알려진 시리얼 도구(허용 목록 + MobaXterm*·SecureCRT·RealTerm·CoolTerm·HTerm·plink·python*·Arduino* …)만 먼저 열고, 사람이 부른 `connect`·표에서 그것으로 못 찾은 COM 만 같은 세션 전체를 본다(점유 알림 풍선은 후보만). 백신·보안·DRM 프로그램(목록은 `src/host/ComHolders.cs`)과 `config.json` `holder_scan_skip`(이름 배열, 끝 `*` 는 앞부분 일치)은 열지 않는다. 읽기+쓰기로 연 핸들만 이름을 묻고, 멈춘 질의는 그 스레드가 돌아오면 복제 핸들을 닫는다.
- 점유자 찾기는 같은 세션·같은 권한 프로세스만 본다. UAC 가 켜진 PC 에서 관리자 권한으로 실행한 터미널은 찾지 못할 수 있다(안내 창 "어느 프로그램인지 찾지 못함" → 그 상태에서 취소하면 그 케이블은 끊김(released)으로 남는다).
- `on_demand` 키가 든 config.json 은 그 키가 생기기 전 판이 "알 수 없는 키"로 거부한다 — 옛 exe 로 되돌리면 그 줄을 지운다. 사건 감지의 `events`·`cables.<id>.events` 도 같다.
- 사건 감지 설정은 `config.json` `events`: `{"enabled": true, "max_mb": 2048, "notify": ["panic","oops","app_error","confirm"], "patterns": {"<panic|oops|bug|oom|app_error>": {"add": [정규식…], "off": true | [기본 패턴 문자열…]}}}`,
  케이블별 `cables.<id>.events`(`enabled`·`patterns` — 덧붙임). 핫 리로드 없음(코어 재시작). `notify`(최상위만) = 트레이 풍선을 띄울 종류(CLI `--kind` 와 같은 이름, `boot:soft`·`boot:hard` 도 — 빈 배열이면 풍선 없음).
  기본값에서도 panic/oops 가 `cause` 인 부팅("panic 뒤 재부팅")은 그 종류로 알린다. 기본 패턴은 `src/Events.cs` `EventKinds.GenericDefaults`(표준 Linux 커널 메시지) + 제품 프로필의 `event_patterns`(§3) — `app_error` 는 일반 기본 패턴이 없어 프로필이나 `events.patterns.app_error.add` 로만 생긴다(커널 oom-killer 줄은 `oom`). `off` 는 일반·프로필 패턴 모두에 쓴다.
- 판정(uptime 으로 "실시간이라 받아들이기" 없이 거르기만):
  - **로더 표시**(`loader_marker_re`, 일반 기본값은 없음 — 제품 프로필이나 config 로 정한 보드만, 예 `LDR>T`)가 줄 어디에 있든(앞에 커널 시각·깨진 글자가 있어도) 실시간 리셋. 앞 표시와 1초 넘게 떨어지면 별개 부팅(로더 이중 리셋도 둘).
    로더 표시가 없는 보드는 `boot_re` 의 커널 부팅 줄(`[    0.0…]`)이 경계 — 같은 부팅 병합은 `boot_merge_ms`(첫 매치 기준) 또는 로더 뒤 15초 안의 커널 첫 1초 줄.
  - **명령 출력 구간** = 송신 CR(빈 Enter·화살표/Tab/Ctrl-P 회상·편집 모두) ~ 진짜 프롬프트(`prompt_re` 가 개행 없이 열린 채 RX 300ms 무음 — 덤프된 글 속 프롬프트는 개행이 따라와 닫지 않음)·실시간 부팅·분리(굳게 닫힘).
    RX·TX 가 10초 조용하면 무르게 닫힌다(전경 프로그램·에이징 스크립트·`tail -f`·`dmesg -w` 의 지난 줄 뒤 — 그 뒤 붙는 줄은 실시간). 무르게 닫힌 뒤엔 CR 없는 송신 바이트 하나(페이저의 스페이스·q)로 다시 열린다.
    구간 안의 로더 표시는 앞 RX 무음 0.5초 이상일 때만 실시간 리셋(덤프된 캡처 로그 속 로더 표시는 suspect 부팅 — 실보드 리셋 225번은 모두 0.625초 이상). 페이저(`more`)로 보는 보드 저장 UART 캡처의 쪽 첫 로더 표시는 사람이 쉰 만큼 무음이 생겨 리셋으로 칠 수 있다(알려진 한계).
    **일부러 일으키는 리셋·충돌 명령**(`reboot`, `echo … > /proc/sysrq-trigger`, `/sys/kernel/debug/provoke-crash/*`, lkdtm — `sync`·`echo`·`mount`·`sleep` 같은 덤프하지 않는 명령과만 섞인 줄, `sh -c '…'` 안도)은 구간을 열지 않는다: 그 출력(panic·oops·Restarting)은 실시간 사건이다.
    로더 표시·커널 시각 줄은 에코·"치는 중" 으로 보지 않는다(유휴 프롬프트 뒤 열린 채 끊긴 `LDR>T###%%> ` 도 리셋), 치는 중은 사람이 실제로 보낸 글자와 맞는 열린 줄만.
    구간 안의 커널·앱 사건 줄은 모두 `suspect:"cmd_output"`(부팅·재시작 상태를 바꾸지 않고 풍선 없음, 앞뒤 로그는 부팅 단위 예산 안에서). 구간 안 커널 부팅 줄은 앞 RX 무음 1초 이상일 때만 실시간 부팅, 아니면 suspect 부팅.
  - 구간 밖은 실시간 — 단 커널 시각이 (이 실행에서 본 지금 부팅의 로더 뒤 경과 + 5초)보다 크거나, 이 부팅의 구간 밖 실시간 줄 최대 커널 시각보다 2초 넘게 작으면 suspect(부팅 스크립트가 pstore 를 찍는 경우).
    커널 시계는 절전 중 멈추되 뒤로 가지 않으므로 보드 절전·깨어남 반복 뒤의 줄은 실시간으로 남는다. 실시간으로 바꾸는 규칙은 없다.
  - 판정하지 않는 줄은 확실한 재출력뿐: 명령 에코(프롬프트 + 2초 안 송신 줄의 글자, 그 송신 줄의 일부, 사람이 치는 중인 줄, 화살표·Tab·Ctrl-P 송신 CR 뒤 첫 명령 줄)와 `^(\d+[:-])?<[0-7]>[ts]`(파일 이름·`grep -n` 줄번호 뒤 포함).
- 재부팅 분류는 실시간 부팅 순간의 되돌아보기로만(부팅 사이에 쌓는 상태 없음): `cmd` = 명령 자리의 `reboot`(송신 — `busybox`·`/sbin/` 앞붙이 허용, `echo reboot`·`grep reboot` 은 아님 — 또는 회상 에코, U-Boot 프롬프트의 `reset`)가 60초 안(그 안에 온 `Restarting system` 이 창을 잇는다)
  → `hard` + `cause:{kind,line,ts,context}` = 직전 20초 RX 에 panic/oops 줄(실시간·suspect 불문, 셸 프롬프트가 돌아온 덤프 속 줄은 뺌) → `soft` = 로더 표시 바로 앞 커널 줄이 2초 안의 `Restarting system`(실보드 54번 모두 0.625~0.735초, 진짜 프롬프트가 사이에 오면 아님) → 그 밖 `hard` + `silence_s`(리셋 전 무음).
  `confirm` 은 마지막 수신 줄이 페이저의 열린 줄(`--More--`·`(END)`·`:`)이거나 그 panic 뒤 송신 바이트가 있었으면 내지 않는다. 허브가 실행 중에 붙은 뒤엔 실시간 부팅을 보거나 구간 밖 두 커널 줄이 5초 넘게 떨어져 서로 맞을 때까지 커널 시계 단조 검사를 켜지 않는다.
  suspect panic/oops 는 60초 안 실시간 부팅이 오면 그 `cause` 로 확인(풍선 "panic 뒤 재부팅"), 부팅 없이 RX 30초 무음이면 `confirm` 기록(풍선 "panic 뒤 응답 없음"), 프롬프트가 오면 취소. `since_boot_s` 는 이 실행에서 본 부팅이면 단조 시각 기준.
- 앞뒤 로그 예산: 부팅마다 — 같은 부팅의 같은 종류는 1시간에 3개 뒤로는 10분에 1개, 포트마다 1시간 30개(부팅마다 첫 실시간 `panic`·`oops` 와 soft·hard 부팅 앞 줄은 면제), events 폴더가 `events.max_mb` 를 넘으면 더 만들지 않는다(아무것도 지우지 않음). 파일 없이 묶던 묶음에 새 종류가 들어오면 그 종류의 예산으로 다시 판단한다(묶음의 skipped 를 물려받지 않음). 그 사건은 jsonl 에 `context:null`·`context_skipped`(`rate`·`hourly`·`disk`)로, 다음 파일에 `skipped_before`·머리말·`# 끝` 줄로 생략 수를 남기고, 걸릴 때 hub.log 경고 한 줄. `rate` 로 줄인 사건은 풍선도 띄우지 않는다.
- 쓰기: 포트마다 jsonl 스트림을 열어 두고 줄마다 flush, 백신·공유 위반은 2초까지 다시 시도 → 대기열(2000줄) → 200ms 마다 다시. 끝내 못 쓴 사건은 hub.log 에 `사건 기록 :<port> 버림(…): <사건 JSON>` 으로 남긴다. 끊긴 마지막 줄(전원 차단)은 다음 시작 때 개행으로 떼어 낸다.
- 케이블 분리 때 `detach`(링을 분리 위치까지 읽은 뒤), 허브 시작·케이블 다시 연결 때 jsonl 에 `attach`(com·`gap_s` = 마지막 기록이 detach 면 그 시각부터, 아니면 이전 코어 마지막 하트비트·마지막 기록 중 늦은 것부터 — alive 하트비트는 분리 중에도 돌기 때문, 다시 연결은 분리 시각부터)를 남기고, 공백이 30초를 넘으면 지금 부팅의 시작 시각을 모름으로 둔다. 허브 정지 때 코어는 추적 스레드를 1.5초 기다려 열린 앞뒤 로그에 `# 끝 — 허브 정지` 를 쓴다.
- 풍선은 포트당 1분에 1번(같은 틱의 여러 포트는 한 풍선)이고 `NotifyIcon` 대기열이 없어 클릭은 마지막 풍선에만 연결된다. 클릭은 events 폴더 아래 `.txt` 만 연다.

## 부록. 예전 PowerShell 판·시리얼 브리지

- 예전 PowerShell 프로토타입(감독 `uart_hub.ps1`, 작업 스케줄러 `\uart_hub` 로 등록)과 PowerShell 시리얼 브리지(`uart_bridge.ps1`)는 공개하지 않는다(`internal/`). 지금의 exe 는 그 흔적을 다음처럼만 다룬다.
- 예전 설치기(`install` 하위 명령)는 그 프로세스가 보이면 설치를 멈추고 정지 방법을 안내하며(자동 kill 안 함), 옛 작업 `\uart_hub` 를 동작이 `uart_hub.ps1` 일 때만 지운다. setup 은 그 작업을 경고로만 보여 준다(읽기만).
- 허브와 다른 시리얼 도구는 **같은 COM 을 동시에 잡을 수 없다** — 그 도구를 쓰려면 먼저 `/disconnect-uart COMx`(또는 트레이 → 정지)로 허브가 COM 을 놓게 한다.
