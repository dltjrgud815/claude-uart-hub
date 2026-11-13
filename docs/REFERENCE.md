**English** | [한국어](REFERENCE.ko.md)

# Technical reference

The full technical description of UART Hub: architecture, subcommands, product profile, build, tests and releases. For a short introduction and quick start, see the [README](../README.md).

The tray and dialog UI, the CLI's human-readable messages and the documents shipped in the release folder are in Korean.
Where this document quotes a Korean UI label, message, or file/folder name, the Korean text is kept as is and followed by an English gloss in parentheses.

## Why this exists

Compared with giving an AI agent direct access to a serial port:

- **The hub owns the COM port.** Each cable is served as telnet on `127.0.0.1:70xx`, so a human terminal (TeraTerm, PuTTY, MobaXterm) and Claude (through the `uart` CLI) use the same console at the same time.
- **Human-first input arbitration.** The hub tracks the shell prompt state and lets the agent type only when the shell is at an idle prompt and the human is not typing. Otherwise the agent gets exit code 101 (`busy`, with a `blocked_reason`) and nothing is sent.
- **`run` returns a real exit code over a plain serial console.** The command is sealed as `sh -c '…' </dev/null` between begin/end markers, and the hub returns its clean output (`out`) and the board's exit code (`rc`). On timeout the hub cleans up with Ctrl-C; a reboot during the command is detected and the run ends as `stranded` with `rebooted:true`.
- **Logging does not depend on clients.** The full console log is written continuously, whoever is connected. For long aging runs the hub detects kernel panic / Oops / BUG / OOM, app errors defined by a product profile, and unexpected reboots classified as `cmd` / `soft` / `hard`, and writes context files (log before and after each event) and session summaries.
- **Built for Windows bench setups.** FTDI USB-serial adapters (FTDI VCP driver; CH340 and CP210x are not supported yet). If an allow-listed terminal (`MoTTY` of MobaXterm, `ttermpro`, `putty`, `kitty`) already holds the port, `/connect-uart` asks for confirmation and then closes only that terminal session. It works from Claude Code in WSL or Git Bash.

## Requirements

- Windows 10/11 x64 (developed and tested on Windows 11; Windows 10 is not verified, see §8).
- FTDI (VID_0403) USB-UART adapters with the FTDI VCP driver. CH340, CP210x and other USB-UARTs are not recognized yet (see §8).
- Claude Code in WSL or Git Bash.
- On the user's PC: a single portable exe — no installer, no admin rights, no pwsh or .NET needed.
- To build: the .NET 10 SDK in WSL at `~/.dotnet` (§4). To run the simulation tests: Windows `pwsh` (PowerShell 7) (§5).

---

## Overview

UART Hub stays resident on Windows, holds the FTDI cables, and gives each cable a fixed TCP port (`127.0.0.1:70xx`) so that a human (terminal over telnet) and Claude (the `uart` CLI)
share the same console. The full log is kept without gaps, independent of any client. The human has input priority.
The deliverable is a single unsigned file, `UartHub.exe` (.NET 10 self-contained, WinExe). The user's PC needs neither pwsh nor .NET.

- **No installation (portable)**: the user puts the exe in any folder (e.g. `C:\Tools\UartHub\`) and double-clicks it; that exe becomes the hub, in place.
  It does not copy itself, run a copy from another location, create Startup or Start-menu shortcuts, or use Task Scheduler. The old installer's "copy itself → register in Startup" behavior was blocked as a malicious dropper by the behavior detection of some antivirus products,
  and the compressed single-file exe was also falsely flagged as a packed executable and deleted. That is why it is portable and uncompressed. For autostart, the user creates a shortcut in `shell:startup` themselves.
- **Deployment registered by Claude**: the release is a folder `UartHub_<ver>/{INSTALL_UART_HUB.md, UartHub.exe, USAGE_ONE_PAGE.md, GUIDE.md, SHA256SUMS}` (`build.sh --package`; plus `UartHub.profile.json` if there is a product profile). The user puts the folder where it should live and
  asks Claude Code to "register it following INSTALL_UART_HUB.md". Claude runs `UartHub.exe setup --json` (plan only) → `setup --yes --json` (window-less portable registration; replaces a hub running from another exe; rolls back on failure) → checks with `uart ls` and `uart hub status` (`uart connect` is for humans only). Double-clicking remains as an alternative.
- **Connect only when needed**: the portable config is `on_demand: true` — plugging in a cable does not open the COM port (Pending, no question asked). When the user types `/connect-uart COMx` in Claude Code,
  the supervisor checks which program holds the port (for an allow-listed terminal session: a confirmation dialog, then only that session is ended), connects, and opens a terminal. `/disconnect-uart COMx` releases the port.
- **Aging event detection**: the core checks every line of each connected cable against patterns (kernel panic, oops, bug, oom; app errors defined by the product profile; boots classified cmd/soft/hard),
  appends to `<log_root>\events\<port>.jsonl`, and for each error keeps the surrounding log (200 lines before; 100 lines or 30 seconds after). With `log_layout: 2` (added by a portable start and by setup), the jsonl goes to `<hub folder>\events\` (internal record),
  the human-readable .txt files — only for groups of notified kinds — go to `<base>\에이징이벤트\<date>\<HHMMSS>_<board>(<port>)_<kind>[_의심].txt` (`<base>` = the base log folder; 에이징이벤트 = "aging events", 의심 = "suspect"; header → 200 lines before → after → `결과:` (Result:)), and console logs go to `<base>\콘솔로그\` (콘솔로그 = "console logs"; .raw, .idx and live are hidden). The supervisor shows tray balloons (`events.notify` — by default only panic, oops, app_error, confirm, and boots whose cause is panic/oops;
  reboots, bug, oom and missed are only recorded, so that the hard reset in every cycle of a power on/off aging run does not flood notifications; at most one per port per minute). Claude reads events with `uart events --follow` (default watch `--kind panic,oops,app_error,confirm`) and `uart aging`.
- **Automatic prompt wake-up after a reboot (config `auto_nudge`, default true)**: when the tracker reports a real reboot inside a run window
  (before B, the run's begin marker: a loader marker, or a kernel timestamp more than 2 seconds smaller than the last value outside the run — also after garbage characters, as in `abce[    0.314164]`; after B: only a loader marker preceded by at least 0.5 s of RX silence) with an 'R' marker,
  the Arbiter immediately ends that run as `stranded` + `rebooted:true` + `started`, and the lock is released without human input when the first prompt after the reboot is confirmed (hub.log "직렬화 잠금 해제 … (재부팅으로 명령 소멸 …)" — "serialization lock released … (command lost to reboot …)").
  While Claude is waiting (a lock left by a command lost to a reboot, a queued Claude request, or a booting/unknown/serialized 101 within 60 seconds), if the tracker's `CanNudge` is true (UNKNOWN, or BOOTING after the kernel stage, with **kernel lines** silent for `quiet_ms` —
  even while app lines keep flowing — the human idle, no line typed by the human, not in screen mode), the lane sends one CR (at most once per `auto_nudge_gap_s` = 10 seconds and 3 times per boot; .log `>>> [hub-nudge]`). In an UNKNOWN state with no reboot seen since the connect/restore (e.g. a cable connected for the first time) it sends `^E ^U CR` instead of a bare CR, like `probe`, so a half-typed line left over from another terminal is cleared rather than executed. If the board does not answer, the state stays BUSY (101 `busy_since` with `hint:"no_output"`) and the hub does not nudge again — press Enter in the terminal. `wait --nudge` uses the same relaxed condition.
  A run waiting on booting, unknown or a reboot lock returns 101 after 30 seconds, even before `--timeout`. With `auto_nudge:false` everything behaves as before this feature (the test harness default — the early scenarios check that contract).
- **Aging sessions (`log_layout: 3` — new registrations, setup and portable start set it to 3; 2 is upgraded to 3)**: DiskWriter still writes to `cfg.LogRoot` = `<hub folder>\임시기록` (임시기록 = "temporary records", hidden), and the core's `SpoolSweeper`
  deletes closed files older than `spool_keep_hours` (24) and the oldest files beyond a total of `spool_max_mb` (2048) (never the file being written or files changed within the last minute). Sessions (per port, `SessionStore` = `<hub folder>\sessions.json`, resumed after a restart) are
  changed with the core command `session start <c> [name]|stop <c>|status` (does not open or close the COM port — Claude calls it too; a port that is not connected gives 102 plus a /connect-uart hint) and from the tray: COM ▸ `에이징 시작…` (Start aging…, AgingForm) / `에이징 끝내기` (End aging).
  Session folder `<base>\에이징\<name>_<yyyyMMdd-HHmm>\` (에이징 = "aging") = `콘솔.log` ("console log"; the event tracking thread writes the same RX lines with the same line prefix as DiskWriter; after a restart or reconnect, `*** 기록 끊김 … ***` ("recording gap")) · `이벤트\<HHMMSS>_<kind>.txt` (이벤트 = "events") · `요약.txt` ("summary") when the session ends.
  Layout 3 evaluates only ports with a session (jsonl and balloons too). `uart session summary` = `aging` (the old name is kept). Recording indicators: tray `tray_rec.ico` (warning > aging > running > stopped), tooltip "에이징 중 N개" (N aging),
  menu info line "● 에이징 중 · 이름 · 경과 · 마지막 출력 N초 전" (● aging · name · elapsed · last output N s ago) (● is a drawn dot — green; red after more than 10 minutes without output) ("출력 없음 N분" (no output for N min) after 10 minutes), and terminal window titles ending in " · 에이징 중" (· aging) (TermTitler; the cable's `session` in `ls --detail`).
- **Product profile**: defaults that differ per board and firmware (loader marker, boot boundary, U-Boot prompt, app error patterns, app error codes) live outside the code in `UartHub.profile.json` (§3). Without it, generic Linux defaults are used.
- **UI ([UI_COPY.md](../UI_COPY.md))**: every window has a title bar at the top (orange star + bold title), a bold first line, "자세히 ▸" (Details ▸ — technical information such as paths and PIDs) and system buttons at the bottom right; progress windows show three steps (the log is under Details). The tray icon reflects the state
  (`tray_run`, `tray_rec`, `tray_stop`, `tray_warn`). The menu is: status line → aging session lines → `COM 포트 ▸` (COM ports ▸: ✓ `연결됨` (connected) · `터미널 열기` (open terminal) · `에이징 시작…`/`에이징 끝내기` (start/end aging) · `끊기` (disconnect) / `연결` (connect)) → `터미널 프로그램 ▸` (terminal program) → `로그 폴더 열기` (open log folder) → `에이징 폴더 열기` (open aging folder) → `설정 ▸` (settings: `터미널 프로그램 ▸` · `미연결 UART 정리…` (clean up unconnected UARTs…) · `제거…` (uninstall…)) → `시작`·`정지` (start/stop) → `허브 끄기` (shut down hub). There is no autostart menu item — only the `shell:startup` instructions in the docs.
  The hidden review subcommand `UartHub.exe render-ui <folder>` draws every window and a mock-up of the tray menu as PNG at 100% and 150% without showing any window (off-screen handles without Show + DrawToBitmap).

| Document | Contents |
|---|---|
| [README.md](../README.md) · [README.ko.md](../README.ko.md) | Short introduction and quick start (English · Korean) |
| [docs/REFERENCE.md](REFERENCE.md) | This document (architecture, subcommands, product profile, build, tests, making a release) |
| [docs/REFERENCE.ko.md](REFERENCE.ko.md) | Korean version of this document |
| [INSTALL_UART_HUB.md](../INSTALL_UART_HUB.md) | Install guide shipped in the release folder (user summary + the register/update/uninstall procedure Claude follows) |
| [docs/GUIDE.md](GUIDE.md) · [docs/GUIDE.ko.md](GUIDE.ko.md) | Illustrated user guide (English · Korean) |
| [USAGE_ONE_PAGE.md](../USAGE_ONE_PAGE.md) | One-page usage guide for users |
| [GUIDE.md](../GUIDE.md) | Short user guide (install, connect, asking Claude, aging, troubleshooting) |
| [UI_COPY.md](../UI_COPY.md) | Text and look of the windows, tray and balloons |
| [skill/SKILL.md](../skill/SKILL.md) | Rules for Claude (the source the exe puts into `~/.claude/skills/uart/` in each environment) |
| [LICENSE](../LICENSE) | MIT |

## 1. Architecture

```
double-click · shell:startup shortcut (made by the user) ─→ UartHub.exe (no arguments = portable run: reconcile install.json → sup with the same exe)
UartHub.exe sup        supervisor + tray (Local\uart_hub_sup): core restart (exponential backoff), adoption, hang detection, desktop icons, first-connect question (none if on_demand), request spool sup\req → sup\resp,
                       connect/disconnect flow (ConnectFlow: find holders with ComHolders → confirmation dialog → end allow-listed session → cable decide|include|exclude → terminal), port-holder balloons
  ⇅ mutual watch       supervisor → core: restart, adopt / core → supervisor: checks the mutex every sup_watch_s (30 s), starts sup if missing (production mode only)
  └ UartHub.exe core   core (Local\uart_hub_core)
       ├ recording core  CableManager (WMI polling) · PortReader · ChunkQueue · DiskWriter (.raw/.log/.idx) · RingBuffer
       └ service layer   PromptTracker · Arbiter (single TX queue) · TelnetServer :70xx · ControlServer :7200 (JSON lines, token/HMAC handshake) · SpoolWatcher
                         · EventDetector (created by ControlServer — follows each cable's ring with its own cursor, classifies and records events)
UartHub.exe cli …      starts and exits per call (control-port handshake → one response line). WSL and Git Bash call it through the wrapper skill/uart as cli --parent-pipe --b64nul
UartHub.exe open <p>   launches a terminal (TeraTerm → PuTTY → MobaXterm) or brings the open window to the front
UartHub.exe setup [--yes] [--json] [--no-start] [--allow-downgrade]   window-less portable registration (Claude calls it following INSTALL_UART_HUB.md; plan only without --yes)
UartHub.exe uninstall [--yes] | version · install (old installer — kept only as an explicit subcommand)
```

### Repository layout

| Path | Contents |
|---|---|
| `src/*.cs` | Core library (`UartHub.Core.csproj`): recording core (`PortReader RingBuffer ChunkQueue DiskWriter`), prompt tracking and TX arbitration (`PromptTracker Arbiter TxTokens`), service layer (`TelnetServer ControlServer SpoolWatcher`), supervisor (`Supervisor`), config and product profile (`Config ProductProfile`), event detection (`Events EventLog Sessions`). Event detection: `Events` (EventKinds, EventPatterns, EventScanner, EventDetector — classification and recording) · `EventLog` (EventRec, aging summary, EventTail, EventBalloons — reading, grouping balloons). Product profile: `ProductProfile` (loading, validation, app error codes). Aging sessions: `Sessions`, `SpoolSweeper` |
| `src/host/` | `CoreMain` (core) · `Cli` (cli; `Cli.Events` = events, aging) · `OpenCmd`, `Terminals`, `DesktopIcons`, `ShellLink` (human access) · `ConnectFlow`, `ComHolders` (connect/disconnect; finding and ending holder processes) · `Installer`, `InstallInfo`, `TokenFile`, `SkillsDeployer` (installation) · `SystemAccess` (single system-access layer for test isolation) |
| `app/` | WinForms tray exe (`UartHub.csproj`, WinExe, exe icon `assets/UartHub.ico`): `Program` (subcommand dispatch) · `UiKit` (common window look) · `TrayApp` (tray, menu, balloons) · `InstallForm` · `FirstConnectForm` · `ConnectForms` (connect confirmation and info dialogs) · `RenderUi` (PNGs for review). Embeds `skill/*` and `assets/` (tray icons, star) as EmbeddedResource |
| `testing/` | Test DLL (`UartHub.Tests.csproj`, class library): simulation scenarios, FakeBoard, SimCore. Loaded by the pwsh harness; the core loads it with `--sim-dll` (not part of the production exe) |
| `tests/` | Runners `run_sim.sh`, `run_sim_wsl.sh`, `run_sim_gitbash.sh` (WSL drivers), `run_sim.ps1`, `run_sim_wsl.ps1` (pwsh harness), `compare_ok_lines.sh`, `check_skills_wsl.sh`, `baseline_phaseA.sha256`, `baseline_ok_lines.txt`, `profile.test.json` (fictional product profile for tests) |
| `docs/` | Illustrated user guide (`GUIDE.md` English, `GUIDE.ko.md` Korean), this technical reference (`REFERENCE.md`, `REFERENCE.ko.md`) and the images (`images/` — UI renders from `UartHub.exe render-ui`, overview diagram) |
| `skill/` | Source of the Claude Code skills the exe deploys: `SKILL.md` (uart), wrapper `uart` (no python3 needed), user-only slash skills `connect-uart/SKILL.md` and `disconnect-uart/SKILL.md` (`disable-model-invocation: true`) |
| `build.sh`, `Directory.Build.props` | Build (§4), common build properties |
| `README.md` and the other docs, `LICENSE` | See the document table above; MIT |

Not published (`.gitignore`):

| Path | Contents |
|---|---|
| `internal/` | Private product profile (`profile.json`), excerpts of real-board logs and golden captures plus the scenarios that use them, the old PowerShell prototype, team release records. If this folder exists, the test DLL also compiles those private scenarios — a clone of the public repository runs only the public scenarios |
| `out/<ver>/` | Build output `UartHub.exe`, `lib/UartHub.{Core,Tests}.dll`, `vB/UartHub.exe` (identical except for version +1, for update tests). `out/release/` holds release candidates, `out/package/` release folders |
| `artifacts/` | dotnet intermediate output (obj/bin/publish, `gen/BuildInfo.g.cs`) |
| `tests/results/` | Test results |

Files on a PC running the portable hub:

- the `UartHub.exe` the user placed (used as is — no copies) and `UartHub.profile.json` next to it (if present);
- `%LOCALAPPDATA%\uart_hub\{install.json(portable:true, exe = that exe), icons.json, prefs.json, setup_last.json(setup result)}` (settings and result files only — no executables or .lnk files);
- `<log_root>\hub\{config.json, state.json, token, control.json, core.pid, sup.json, hub.log, install.log, uninstall_<time>.log, alive\, sup\req|resp\}`, `<log_root>\YYYYMMDD\<port>_<board>_<HHMMSS>.{log,raw,idx}`,
  `<log_root>\events\{<port>.jsonl, YYYYMMDD\<port>_<board>_<HHMMSS>_<kind>.txt}` (events and surrounding logs — when `log_layout` is absent);
- with `log_layout: 3`: `<base>\{에이징\<name>_<yyyyMMdd-HHmm>\{콘솔.log, 이벤트\<HHMMSS>_<kind>.txt, 요약.txt}, hub\{…, sessions.json, events\<port>.jsonl, 임시기록\{YYYYMMDD\…, live\}(hidden, 24 h / 2 GB)}}` (when going from 2 to 3, the old `콘솔로그\` and `에이징이벤트\` are left as they are);
- with `log_layout: 2`: `<base>\{콘솔로그\{YYYYMMDD\…, live\}, 에이징이벤트\YYYYMMDD\<HHMMSS>_<board>(<port>)_<kind>.txt, hub\{…, events\<port>.jsonl}}` (the old layout is moved once, by a same-volume rename, at a portable start or setup while the hub is not running — LogMigration, install.log);
- `retired_ports` in state.json (numbers removed with `미연결 UART 정리…` (clean up unconnected UARTs) — never handed out again);
- desktop `<board|COMx> (<port>).lnk` (target = that exe; if the desktop is on OneDrive, the Start menu folder `UART 허브\` (UART Hub) instead), and in each environment `~/.claude/skills/uart/{SKILL.md, uart, .uart_hub}` (the wrapper holds that exe's path)
  and `~/.claude/skills/{connect-uart,disconnect-uart}/{SKILL.md, .uart_hub}`.

`log_root` is `D:\uart_logs` if D: is a local fixed disk, otherwise `%USERPROFILE%\uart_logs` (decided once on the first run and recorded in `install.json`).
PCs installed with the old installer (`install`) additionally have `%LOCALAPPDATA%\uart_hub\{UartHub.exe, install.log}`, the Startup shortcut `UART 허브.lnk` and the Start menu entries `UART 허브\{UART 허브, UART 허브 제거}` (UART Hub, Uninstall UART Hub).

## 2. Subcommands

| Command | Behavior |
|---|---|
| (no arguments) | **Portable run**. First time (no portable `install.json`): one confirmation dialog (log location, where the skills go, how to set up autostart yourself) → [시작] (Start) → hub folder, token, config (if missing, `{"on_demand": true}` — if it exists without an `on_demand` key, only that one line is added; other keys and comments are left untouched) → `install.json` (exe = this exe, state=installed, portable:true) → `sup --icons-dir <desktop>` with the same exe → health check → skills. Later runs: without a window, point the exe and version in `install.json` at this exe (if they changed, also the targets of its own icons and the skills that pointed at the old exe) → sup. If a supervisor is already running: same exe = only a tray balloon "이미 실행 중" (already running; supervisor request `notice_running`); different exe = confirmation dialog "기존 허브를 끄고 새 exe 로 바꿀까요?" (Stop the existing hub and switch to the new exe?) [예] (Yes) → update mutex → shutdown → new sup (health check: version == vB). If a non-portable `install.json` (old installer) exists, it shows uninstall instructions and exits 3. Exception: running the old installed copy `%LOCALAPPDATA%\uart_hub\UartHub.exe` without arguments means `sup` |
| `sup [--no-tray] [--hub-dir D] [--sup-mutex M] [--core-mutex M] [--update-mutex M] [--sim SPEC --sim-dll Tests.dll] [--tick-ms N] [--icons-dir D]` | Supervisor. Hub folder: `--hub-dir` → `UART_HUB_DIR` → `install.json` (production mode only in this last case). Exits 0 if the update mutex exists |
| `core --hub-dir D …` | Core (started by the supervisor). Exits 3 if it cannot take the mutex within 3 seconds; `core.stop` for a clean exit |
| `cli <command> …` / `cli -B64Req <b64 JSON>` / `cli --b64nul <token>` | CLI (table below). If `--parent-pipe` is the first argument, exits 103 on EOF on standard input (cleanup when the wrapper gets TERM) |
| `open <port> [--from balloon]` | Opens a terminal. If it cannot reach the core, it self-heals (restarts the supervisor and core) |
| `install [--dry-run]` / `install --repair --quiet` | Old installer (copy, shortcuts, update, rollback) — kept only as an explicit subcommand (not the default behavior). `--dry-run` only prints |
| `setup [--yes] [--json] [--no-start] [--allow-downgrade]` | **Window-less portable registration**. Writes `result:"running"` to the result file as soon as it starts. Without `--yes`, plan only (no changes except the result file): exe; location (blocked unless on a local fixed disk — UNC, `\\wsl.localhost`, network, removable); log location; hub folder (stopped state `stopped`); where the skills go (Windows + WSL distros — the plan probes only running distros with the read-only `skills_wsl.sh probe`; stopped ones are `unknown`); running hub (pid, version, exe); rollback to an older version (`downgrade`); leftovers of the old install method (non-portable `install.json`, `UartHub*.exe` in the install folder, self-marked Startup/Start menu shortcuts); MotW (`Zone.Identifier` — reported only, never removed; null if unreadable); warnings (hub ports held by another program, the old PS task `\uart_hub` (read-only `/query`), `UART_HUB_DIR`); UartHub shortcuts in `shell:startup` (read-only); actions to take. Blocked (exit 3, same for the plan and `--yes`, no changes): installer mutex (another installer or confirmation dialog), old leftovers, location, update mutex, an unidentified supervisor (the sup mutex exists but cannot be verified through sup.json), an unregistered supervisor, downgrade (without `--allow-downgrade`). `--yes` (holds the installer mutex): first time = the same registration as the first portable run (hub folder, token, config — ensures `on_demand` if config exists → `install.json` portable, exe = this exe → `sup` (started with job breakaway, without passing on `UART_HUB_DIR`) → health check → 3 skills); if already registered = the portable replacement path (update mutex → if the supervisor runs from another exe or another version: shutdown, core.stop, kill of verified PIDs → icon targets, `install.json` → hub files → sup → health check → skills if anything changed). If there is nothing to do, nothing is written (`unchanged`; no install.log entry, no balloon). Failure = `rolled_back` (1): a first registration removes the started hub and `install.json`; a replacement or reconciliation stops the new hub, restores the exe and version in `install.json` and the icons to the previous values and, if a hub was running before, restarts the supervisor with that exe and health-checks it. Health check = sup.json and core.pid + `cli hub status` (control port responds, version matches), up to 30 seconds. Distro probing and skills placement share setup's overall 120-second budget (exceeding it gives `failed` `budget…`); `--yes` reuses the plan's probe results for placement (it does not wake a distro twice). Result = lines in Korean (or with `--json`, a single JSON line only) + `%LOCALAPPDATA%\uart_hub\setup_last.json` (same JSON) `{command, mode(plan\|apply), result(running\|planned\|registered\|replaced\|updated\|unchanged\|blocked\|rolled_back\|failed), rc, message, version, exe, location, motw, legacy, install, running, downgrade, log_root, hub_dir, config, stopped, warnings, autostart, skills, actions, started, health, hub_running, control_port, next, log, setup_last, at}`. Exit codes 0, 1, 3, 106 |
| `uninstall [--dry-run \| --yes]` | `--yes` = same scope without the confirmation dialog (exit 3 if another installer is running). A portable uninstall catches errors at each step and carries on, then exits 1 if anything failed (`# 제거 일부 실패` — "uninstall partly failed"); it also removes self-marked old leftovers (Startup and Start menu shortcuts, `UartHub*.exe` in the install folder). If `%LOCALAPPDATA%\uart_hub` contains only `prefs.json` and `setup_last.json`, there is no registration (exit 3). With a portable `install.json`: confirmation dialog → stop the hub (shutdown) → its own icons and `icons.json` → skills (those whose marker matches) → delete `install.json`. The exe, logs and folders are kept, and it does not move its own image. For an old installed copy, the previous uninstall (shortcuts, install folder) |
| `version [--json]` | Version. `--json` gives `version`, `core_mvid`, `self_contained`, `runtime` |
| anything else | exit 106 |

CLI commands (path style `UART_PATH_STYLE=win|wsl|mixed`, set by the wrapper). Target `<c>` resolution: integer (TCP port) → `COM\d+` → alias → board name (105 if more than one matches).

| Command | Behavior |
|---|---|
| `ls [--detail]` | Cable list (`attached/detached/pending/released/ignored/held_by_other/bind_failed`, board name, prompt state). `--detail` adds `description`, `mode_explicit`, `rx_since_attach` (plus `hint:"no_output"` and `hint_text` when it is 0) |
| `status <c>` | Details of one cable (`state`, `blocked_reason`, `last_lines`, `log`, `rx_since_attach`, `hint` …). A 101 from run, send or reboot also carries `rx_since_attach` and `hint` when 0 bytes have been received |
| `connect [COMx [baud]]` · `disconnect COMx` | **Human only** (slash skills `/connect-uart`, `/disconnect-uart`). Handed to the supervisor through the supervisor request spool (`args`); waits up to 175 seconds. connect: if the holder is an allow-listed terminal (MoTTY, ttermpro, putty, kitty), a confirmation dialog [끊고 연결] (Disconnect and connect) / [취소] (Cancel); otherwise an info dialog + a recheck every 2 seconds (120 seconds) → pending gets `cable decide board`, released gets `include` → terminal → `rx_bytes` after 2 seconds. Response `{status, com, port, baud, board, state, rx_bytes, holder_closed, terminal}`; cancel = 106 `cancelled`, timeout = 100 (neither changes anything). With a baud (standard rates 1200–921600 only, otherwise 106), after clearing the holder it sends the core `cable baud <id> <rate>` (in-memory setting + saved as `config.json` `cables.<id>.baud` — other keys keep their original text, top-level comments are not kept; if the cable is attached, the port is closed and reopened at the new baud on the next scan, with `*** baud A -> B ***` in the .log); without one, the cable's current baud is used. With no arguments, a table of connection state and baud per COM port. disconnect = `cable exclude` (the port number is kept) |
| `run <c> "<cmd>"` | Runs the command sealed as `sh -c '…' </dev/null` and collects `out` and `rc`. `[--timeout s] [--idle s] [--inline] [--no-interrupt] [--force] [--req-id X] [--clear-line] [--json]` |
| `send <c> "<raw>"` | Sends using the token syntax (`{ENTER}` `{ESC}` `{NONL}` …); the response includes `rx_offset` |
| `probe <c>` | When the state is unknown and conditions allow, sends `^E^U` → one CR. Right after attach it needs `--user-ok` |
| `wait <c> --pattern RE` | `[--since epoch:offset] [--timeout s] [--nudge]`. Searches the ring buffer retroactively; `since_expired` if out of range |
| `reboot <c>` | Sends `reboot` at AT_PROMPT and returns the `rx_offset` from just before sending |
| `log <c> [--tail N \| --since OFF]` · `logpath <c>` | Read the log · current `.log`, `.raw`, `.idx` paths |
| `hub status \| stop \| start \| upgrade` | Status / stop (`stopped` persists, releases COM ports, removes icons) / resume / restart the core with the same exe. `profile` in `hub status --json` = `{name, path, error}` (null if there is no product profile, §3) |
| `events [<c>] [--since ISO\|12h\|30m] [--kind k1,k2] [--follow [--max N]] [--json]` | **Read-only** (sends nothing to the core or the board; works with the hub stopped): `<log_root>\events\<port>.jsonl` (log_root follows the same rule as the core: config.json → last-good → parent of the hub folder; with `log_layout: 2`, `<hub folder>\events` — Config.EventsJsonlDir). For a renamed txt (merged into a more severe kind, `context_was`), the `context` of earlier records is also read under the new name. `--json` = `{status, exit, events_dir, ports, since, count, events:[event lines]}` (capped at 2000). `--kind` takes `panic,oops,bug,oom,app_error,boot,confirm,missed,attach,detach` and `boot:cmd\|soft\|hard`. `--follow` = a header line `{follow:true, ts(start time),…}` followed by one JSON line per new event (it tails the file, so core restarts do not matter). Ends with: 0 after `--max N` lines · 141 when the reader of standard output closes · 103 on `--parent-pipe` EOF when the wrapper gets TERM. `<c>` = port, COMx (the attach com in the event records), alias or board name (state.json) — resolved without the hub (none 102, several 105). `--follow`, `--kind` and `--max` are taken only by these two read-only commands (core commands return 106) |
| `session start <c> [name]` · `session stop <c>` · `session status [--json]` | Aging session (core): does not open or close the COM port and sends nothing to the board. start = connected ports only (otherwise 102 + a `/connect-uart` hint); response name, folder. stop = saves `요약.txt` (summary); response summary, summary_text. status = per session port, com, board, name, folder, started, elapsed_s, console_log{path,bytes,lines}, last_rx_age_s, events{total,by_kind}, boots, gaps, connected (non-JSON output: one line each) |
| `session summary <c> [--since …] [--json]` = `aging <c> [--since …] [--json]` | Read-only: number of boots (cmd/soft/hard), `unexpected_reboots`, `boot_interval_s` (min, median, max; intervals that span a recording gap are left out — `excluded_gap`), `boots.with_cause` (resets after panic/oops), `kinds` (count, first, last, `no_context`, `suspect` for panic, oops, bug, oom, app_error, confirm, missed), `contexts` (list of surrounding logs, whether each exists, suspect), `period_s`, `covered_s`, `gaps` (time the cable was not attached: detach to the next attach — from the start of the period if the last record before `--since` is a detach —, and the `gap_s` of attaches made after the hub had been down). Counts include real-time events only — suspect ones are counted separately in `suspect_events`, `boots.suspect`, `kinds.*.suspect` Without `--since`, a port with a running aging session is summarized from the session start (`scope:"session"` plus `session{name, started, folder, console_log}`); with `--since`, `scope:"since"`. `boot_detection` shows the loader marker / `boot_re` / profile used to count boots, and `hint:"no_boots_seen"` is added when 30+ minutes passed with no boot — on a board that does reboot this means the hub does not recognize its boot lines (fix the product profile). |
| `cable …` | **Tray only** — `cli` returns 106 "트레이에서만" (tray only), so that Claude does not answer on the user's behalf. Core verbs: `decide <id> board\|ignore` · `exclude <id>` · `include <id>` · `baud <id> <rate>` (sent only by the supervisor's connect flow) |

Exit codes (the formal contract for decisions is `status` and `rc` in `--json`):

| Code | status | Meaning |
|---|---|---|
| 0~255 | `ok` | For `run`, the board's rc as is. 0 for every other command |
| 100 | `timeout` / `stranded` | Timed out / the start of the sent line was not confirmed (`rc:null`, do not retry) |
| 101 | `busy` / `interrupted` | Not at a prompt, or a human is using the console (`blocked_reason`) / the human pressed Ctrl-C during the run |
| 102 | `no_cable` | No cable, or detached; `blocked_reason` `pending`, `released`, `ignored` |
| 103 | `hub_down` | Hub not responding (`stopped:true`, `restarted:true`), the peer on the control port is not the hub, proof mismatch |
| 104 | `md5_mismatch` | push verification failed (feature not implemented yet) |
| 105 | `ambiguous` | Several targets match (`candidates`) |
| 106 | `rejected` / `since_expired` / `unsupported` | Rejected / `--since` out of range / `hub stop/start` on a core without a supervisor |

- Control connection handshake: the hub speaks first with `{"uart_hub":1,"nonce":…}`, the client answers with `hello = HMAC-SHA256(token, "c|"+nonce)` and `cnonce`, and the hub replies `proof = HMAC-SHA256(token, "s|"+cnonce)`. The token itself never goes over the wire. The CLI's control port: `UART_HUB_CONTROL_PORT` → `core.pid` of a live core → `config.json` → 7200.
- Telnet connections are not dropped when a cable is unplugged (detach/reconnect banners appear only on the terminal screen). They are dropped only on hub stop, core restart, or for a slow client (send queue over 1 MB).
- **Clearing the line on reconnect**: if a cable is unplugged while a shell line is being edited, `^E^U` is prepended to the first input sent to the board after it is plugged back in (`>>> [hub-clear] …` in the `.log`). This keeps framing-error bytes (`??????`) from being executed by the next Enter. Screen programs such as vi, and BUSY, are excluded. To turn it off per cable: `config.json` `cables.<id>.replug_clear: false`. The real fix is `stty inpck ignpar ignbrk -brkint` in the board's console initialization.

## 3. Product profile (`UartHub.profile.json`)

Defaults that differ per board and firmware — loader marker, boot boundary, U-Boot prompt, app error patterns, app error code names — are not kept in the code but in a JSON file next to the exe (the product profile).
Without a profile the hub runs with generic Linux defaults. The profile is optional.

- **Lookup order**: environment variable `UART_HUB_PROFILE` (file path — an empty value means no profile; for development and tests) → `UartHub.profile.json` in the same folder as `UartHub.exe` → none (generic defaults).
  Each process reads it once, on first use, so after editing it restart the hub (tray → `허브 끄기` (Shut down hub) → run the exe).
- **Precedence**: generic defaults < profile < `config.json`. If config sets `loader_marker_re`, `boot_re` or `uboot_prompt_re`, that value wins.
  For event patterns, the profile's `event_patterns` are added to the generic default patterns, and then `add` and `off` from config `events.patterns` are applied on top (`off` can also name profile pattern strings).
- **A broken profile** (JSON error, unknown key, invalid regex) is ignored entirely and the hub runs with generic defaults. The reason is recorded in `profile.error` of `uart hub status --json` and in hub.log.
  `profile` in `hub status --json` = `{name, path, error}` (`null` if there is no profile, `error:null` if it is valid).
- The format is JSONC (comments and trailing commas allowed). Keys:

| Key | Meaning | Without a profile (generic default) |
|---|---|---|
| `name` | Display name (`profile.name` in `hub status`) | — |
| `loader_marker_re` | Loader marker: a loader banner that always means a real reset, wherever it appears in a line (even after a kernel timestamp or garbage characters). `""` = none | none |
| `boot_re` | Boot boundary line | `reboot: Restarting system\|^\[\s+0\.0\d+\]` |
| `uboot_prompt_re` | U-Boot prompt | `^=> ` |
| `event_patterns` | `{kind: [regex…]}` — per kind, added to the generic default patterns. Kinds = `panic`, `oops`, `bug`, `oom`, `app_error` | panic, oops, bug, oom = standard Linux kernel messages (`Kernel panic - not syncing`, `Unable to handle kernel`·`Internal error: Oops`, `BUG: `·`WARNING: CPU:`·soft lockup·rcu stall·task blocked, `Out of memory: Kill`·`invoked oom-killer`); `app_error` = none |
| `app_codes` | `[{"re": regex, "code"?: string}]` — the code in the app error txt name `앱오류_<code>` (앱오류 = "app error"). The first matching rule wins. Without `code`, the matched text itself | none — the first word of the text matched by the app error pattern |

Example (a fictional product — names and strings are for illustration only):

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

- `LDR>T` is the marker this fictional board's loader prints on every reset. Loader markers never go into the kernel ring buffer or pstore, so they are not reprinted as part of old logs; defining one in the profile makes the classification more robust (§8, classification). On boards without one, the kernel boot line from `boot_re` is the boundary.
- The negative lookahead in `APPFAULT\((?!(?i:BA\)\(000f\)))` shows how to exclude a normal-exit signature (here `APPFAULT(BA)(000f)` — for example the code an app leaves when it exits normally on reboot) from app errors.
- `app_codes`: an `APPFAULT(ED)` line gives `앱오류_APPFAULT(ED).txt`, an `app_wdt …` line gives `앱오류_WDT.txt`.
- **Distribution**: each team (product) puts its profile next to the exe in its own release folder. `build.sh --package <ver>` copies the private `internal/profile.json`, if present, into the release folder as `UartHub.profile.json`. The fictional profile for tests is [`tests/profile.test.json`](../tests/profile.test.json) (in development and tests, `UART_HUB_PROFILE` can point at any profile path).

## 4. Build

From the repository root (this folder):

```bash
./build.sh                    # new build (version = current time, 17 digits)
./build.sh --pin <ver>        # pin a release candidate (no build, §6)
./build.sh --use <ver>        # only check the Windows copy of the given build (no build; called by the test drivers' no-build option)
./build.sh --package <ver>    # release folder out/package/UartHub_<ver>/ + .zip (the tested, pinned exe as is — <ver> required and must be pinned with --pin; prints EXE_SHA256 and ZIP_SHA256 at the end, §6)
~/.dotnet/dotnet build-server shutdown   # when done (on PCs with little memory)
```

- Build only in WSL. The .NET SDK 10 (10.0.403) lives in `~/.dotnet` (user permissions). The three projects are compiled once and bundled with `publish --no-build`, so the test DLL is the same compilation output as the Core inside the exe (the harness checks this with `# mvid=… match`).
- Settings: self-contained single-file, **compression off** (some antivirus products flag a compressed single-file exe as a packed executable and delete it — the exe is about 100 MB instead), R2R off, WinExe (GUI subsystem bit). A WinExe writes to standard output only when it is a pipe — the Git Bash wrapper goes through `| cat`.
- Output: `out/<ver>/{UartHub.exe, lib/, vB/}` + a Windows copy in `%LOCALAPPDATA%\uart_hub_dev\<ver>\` (+ the test harness `tests\*.ps1`, and the golden files if `internal/` exists). pwsh will not run `.ps1` files from `\\wsl.localhost` because of the execution policy, so the harness always runs from this copy.
- Cleanup: only the 3 most recent folders with 17-digit names are kept. Versions pinned as `out/release/UartHub-<ver>.exe` and `release-*` copies are never deleted.
- Last lines: `VERSION=` / `EXE_WIN=` / `EXE_SHA256=` (preceded by `BUILD=reuse` when nothing was built).

## 5. Tests (no hardware, FakeBoard)

Windows needs `pwsh` (PowerShell 7). The runners are invoked from WSL.

| Name | Command | Contents |
|---|---|---|
| part 1, 2, 3 | `tests/run_sim.sh --quiet --part k/3` | Splits the scenarios by registration order `i % 3 == k-1` (600-second limit per command). Results go to `tests/results/YYYYMMDD_HHMMSS.txt`. The connect family is `connect-units`, `connect-holders`, `connect-ondemand` (holders, termination and confirmation dialogs are test-isolation fakes: `<root>\fake_holders.json`, `fake_kill.log`, `fake_ui.json`). Event detection: `events-units` (part 1, lines fed straight to the classifier), `events-hub` (part 2, ring tracking inside the hub), `events-cli` (part 3, real exe supervisor and core with injected `emit` and `reset`; the balloon window is `UART_HUB_TEST_EVENTS="<window>,<wait>"` seconds). Registration: `setup-portable` (part 2 — stop on old leftovers, plan-only makes no changes, first registration with `--yes`, idempotence, `uninstall --yes`, `--no-start`, an exe with MotW attached), `setup-replace` (part 3 — replaces a hub running from another exe without windows, keeping ports, settings and logs). Real-board log replay (`events-fixtures`) and golden-capture scenarios are included only when `internal/` exists |
| wsl | `tests/run_sim_wsl.sh --quiet` | WSL wrapper contract: 10 cases + 13 extras (`wsl_*.txt`; x11–x13 = events, aging, events --follow) |
| gitbash | `tests/run_sim_gitbash.sh --quiet` | Git Bash wrapper contract: 10 cases + extras g1–g13 (`gitbash_*.txt`; g11–g13 = events, aging, events --follow) |

- Run **one at a time** (overlapping runs interfere with each other's ports and copy cleanup). Before starting, check that no orphaned `run_sim.ps1` pwsh process is left:
  `pwsh.exe -NoProfile -Command "Get-CimInstance Win32_Process -Filter \"Name='pwsh.exe'\" | ? CommandLine -match 'run_sim' | select ProcessId,CommandLine"`
- **No-build option**: `--exe-version <ver>` (or `UART_HUB_USE_BUILD=<ver>`) runs against the given build without rebuilding (`build.sh --use`: the `release-<ver>` copy first, then `<ver>`; exe, lib and vB must be byte-identical to `out/<ver>`). The result header has `BUILD=reuse`, `VERSION=`, `EXE_SHA256=` (the exe that was tested). Without the option, a fresh build is made as before.
- **Comparing expected values (optional)**: before a change, make a baseline from a result (`tests/compare_ok_lines.sh --make <result> > tests/baseline_ok_lines.txt`); after the change, compare it with the `cat`-ed results (the set of normalized `ok:` lines — paths, times and ports that vary per run become placeholders).
  ```bash
  cat tests/results/{p1,p2,p3}.txt > /tmp/all.txt && tests/compare_ok_lines.sh /tmp/all.txt   # expect "# 차이 0" (0 differences) and exit 0
  ```
- `run_sim.sh` also checks that WSL `~/.claude/skills/{uart,connect-uart,disconnect-uart}` is the same before and after the run (`[FAIL] wsl-skills-unchanged`). `tests/check_skills_wsl.sh` tests `skills_wsl.sh` directly with dash in a temporary HOME (including extras). Every install-related scenario asserts that the real Startup, desktop, skills, Start menu, `%LOCALAPPDATA%\uart_hub` and schtasks are unchanged and that `TestIsolationViolation 0`.
- A single scenario: `tests/run_sim.sh fakeboard-selftest 14`. Under heavy load (load 60+), hang-related scenarios can fail on timeouts — rerun just that ID to confirm.

## 6. Making a release (tested bytes = released bytes)

1. Fresh build from the current tree: `build.sh` → `VERSION=<ver>`.
2. Pin the candidate: `build.sh --pin <ver>` → `out/release/UartHub-<ver>.exe`, `out/release/UartHub-<ver>.sha256` (`sha256sum -c` format), and `%LOCALAPPDATA%\uart_hub_dev\release-<ver>\` with the same bytes.
3. Run the 5 suites against the candidate, in order: `run_sim.sh --quiet --part 1/3 --exe-version <ver>` → `2/3` → `3/3` → `run_sim_wsl.sh --quiet --exe-version <ver>` → `run_sim_gitbash.sh --quiet --exe-version <ver>`.
   The result header's `EXE_SHA256` must equal the `.sha256`, and (if you made a baseline) compare_ok_lines must show 0 differences. On a failure, rerun just that ID; if it fails twice in a row, investigate the cause.
4. Release folder: `build.sh --package <ver>` → `out/package/UartHub_<ver>/{INSTALL_UART_HUB.md, UartHub.exe(= UartHub-<ver>.exe, renamed only), USAGE_ONE_PAGE.md, GUIDE.md, SHA256SUMS}` and `out/package/UartHub_<ver>.zip`.
   If the private `internal/profile.json` exists, it is included as `UartHub.profile.json` (§3). The exe hash is checked against the `.sha256`, and the folder is created only if `sha256sum -c SHA256SUMS` passes. Only for versions that are pinned and have passed the 5 suites (no version, or an unpinned version, is refused). Put the `EXE_SHA256` and `ZIP_SHA256` from the last lines into the handover message (SHA256SUMS only catches corruption; substitution is caught with these values).
5. Register and connect once from that folder on a real PC, then distribute the folder (or the zip). **Do not rebuild for the release** (a changed version string means different bytes).
6. If the code changes afterwards: new build → new `--pin` → run the 5 suites again.

## 7. Test isolation and the production hub

- With `UART_HUB_TEST_ROOT=<root>`, the exe's known folders (LOCALAPPDATA, APPDATA, Desktop, USERPROFILE, TEMP), mutexes (`Local\uart_hub_test_<root hash>_{sup,core,update,installer}`), `wsl.exe`, `schtasks.exe`, WMI queries and drive classification are replaced by fakes under `<root>` ([`src/host/SystemAccess.cs`](../src/host/SystemAccess.cs)). Any access to a real path outside the test root, or any real external process launch, is a `TestIsolationViolation`.
- The test core uses `vids: []` (does not enumerate real FTDI devices) and refuses control or cable ports below 17000. The test supervisor does not show a tray.
- Running the harness directly (`run_sim_wsl.ps1` given `--hub-dir`, mutexes and ports) also uses ports in the 17400/18050 range and random mutex names.
- **Never touched**: a production hub running on the same PC (control 7200, cables 70xx, default mutexes `Local\uart_hub_sup`, `Local\uart_hub_core`), real FTDI devices, the real installation, skills placement, desktop, Start menu, Startup.
- **No windows**: `start`, `explorer`, `Start-Process` with a window, mintty, `UseShellExecute`, `sup` with a tray. Tests and measurements use only `--no-tray` and pipes (a window opened by a test hijacks the work of the person at the PC).

## 8. Known limitations

- If the supervisor and the core die at the same time (rare, except at logoff or reboot), nothing is recorded until the next logon, icon double-click or CLI call. If only the supervisor dies, the core restarts it within ≤30 seconds.
- In WSL, TERM and INT do not reach Windows processes. The wrapper cleans up via `--parent-pipe`, but always pass `--timeout`.
- Notifications are `NotifyIcon` balloons, so there is no queue and a click is tied only to the last balloon. Clicks after a balloon has moved to the notification center are not guaranteed to work.
- The exe is unsigned, so SmartScreen needs "추가 정보 → 실행" (More info → Run anyway). A proper antivirus scan is up to the user (right-click the exe).
- The portable hub uses the downloaded exe as is, so its MotW (Zone.Identifier) stays (there is no stream-dropping copy as in the old installer). If SmartScreen appears at every logon, the user opens the file's Properties → '차단 해제' (Unblock) (the exe does not remove MotW by itself — that could look like defense evasion).
- Even after the tray's `허브 끄기` (shut down hub, i.e. shutdown), desktop icons and Claude's `uart` calls bring the hub back via self-healing. If such a call happens while you overwrite the exe, the file is locked and the overwrite can fail (shut down again → overwrite).
- If you move the exe, the skills wrapper and the icons still point at the old path — double-click it once in the new location to fix them (without a window if no supervisor is running, with the replace confirmation dialog if one is). Move the product profile (`UartHub.profile.json`) together with the exe.
- Windows 10 is not verified. **Only FTDI (VCP driver) devices are seen**: the device scan enumerates `FTDIBUS` devices only, and `vids` merely filters among them, so CH340, CP210x and other USB-UARTs cannot be added through configuration.
- If several processes append (`>>`) to the `.tx` spool (DrvFs) at the same time, lines get lost regardless of the hub — use `uart send`.
- During a core restart all cables have a gap of a few seconds (`*** gap … ***`). `config.json` has no hot reload (a core restart is needed). The same applies to the product profile.
- After sending a lone ESC with no echo, BUSY can block the next token line. `^D` does not clear HUMAN_TYPING. A reboot during a stranded lock is not detected.
- `partial` (the human's in-progress line in a 101) is reconstructed from the RX echo — it is inaccurate if an app log without timestamps is appended to the same line or the line wraps at 80 columns more than twice.
- Not implemented yet: `push` (106), `uboot`, `term`, telnet status banner and replay of recent lines, compression of logs older than 7 days, low free space warning for D:. Releasing and taking COM ports is done only by the human-only `connect` and `disconnect`.
- Holder detection first opens only known serial tools (the allow-list + MobaXterm*, SecureCRT, RealTerm, CoolTerm, HTerm, plink, python*, Arduino* …); only for COM ports it could not find that way — in a `connect` invoked by a human, or its table — does it look at all processes in the same session (port-holder balloons look at candidates only). Antivirus, security and DRM programs (list in [`src/host/ComHolders.cs`](../src/host/ComHolders.cs)) and the names in `config.json` `holder_scan_skip` (array of names; a trailing `*` means prefix match) are never opened. It asks for names only of handles opened read+write, and for a stuck query it closes the duplicated handle when that thread returns.
- Holder detection only sees processes in the same session with the same privileges. On a PC with UAC enabled, a terminal run as administrator may not be found (info dialog "어느 프로그램인지 찾지 못함" (could not find which program) → if you cancel in that state, that cable stays disconnected (released)).
- A config.json containing the `on_demand` key is rejected with "unknown key" by versions from before that key existed — when rolling back to an older exe, delete that line. The same applies to `events` and `cables.<id>.events` for event detection.
- Event detection settings live in `config.json` `events`: `{"enabled": true, "max_mb": 2048, "notify": ["panic","oops","app_error","confirm"], "patterns": {"<panic|oops|bug|oom|app_error>": {"add": [regex…], "off": true | [default pattern string…]}}}`,
  plus per cable `cables.<id>.events` (`enabled`, `patterns` — added on top). No hot reload (core restart). `notify` (top level only) = the kinds that raise a tray balloon (same names as CLI `--kind`, including `boot:soft` and `boot:hard` — an empty array means no balloons).
  Even with the defaults, a boot whose `cause` is panic/oops ("panic 뒤 재부팅" (reboot after panic)) is notified as that kind. The default patterns are `EventKinds.GenericDefaults` in [`src/Events.cs`](../src/Events.cs) (standard Linux kernel messages) + the product profile's `event_patterns` (§3) — `app_error` has no generic default pattern, so it exists only through a profile or `events.patterns.app_error.add` (the kernel oom-killer line is `oom`). `off` applies to both generic and profile patterns.
- Classification (it only filters — there is no rule that "accepts as real-time" based on uptime):
  - A **loader marker** (`loader_marker_re`; no generic default — only boards that define one in the product profile or config, e.g. `LDR>T`) anywhere in a line (even after a kernel timestamp or garbage characters) is a real-time reset. Markers more than 1 second apart are separate boots (a double loader reset counts as two).
    On boards without a loader marker, the kernel boot line from `boot_re` (`[    0.0…]`) is the boundary — lines of the same boot are merged by `boot_merge_ms` (from the first match) or as kernel first-second lines within 15 seconds after the loader.
  - **Command output window** = from a sent CR (empty Enter, recall with arrow/Tab/Ctrl-P, or any edit) to the real prompt (`prompt_re` left open without a newline plus 300 ms of RX silence — a prompt inside dumped text is followed by a newline and does not close the window), a real-time boot, or a detach (hard close).
    If RX and TX are quiet for 10 seconds, the window closes softly (after the last line of a foreground program, an aging script, `tail -f` or `dmesg -w` — lines arriving after that are real-time). After a soft close, a single sent byte without CR (space or q in a pager) reopens it.
    Inside the window, a loader marker counts as a real-time reset only if preceded by at least 0.5 s of RX silence (a loader marker inside a dumped capture log is a suspect boot — all 225 real-board resets had 0.625 s or more). When paging (`more`) through a UART capture stored on the board, the first loader marker on a page can be counted as a reset, because the human's pause creates silence (known limitation).
    **Deliberate reset/crash commands** (`reboot`, `echo … > /proc/sysrq-trigger`, `/sys/kernel/debug/provoke-crash/*`, lkdtm — on a line mixed only with non-dumping commands such as `sync`, `echo`, `mount`, `sleep`, also inside `sh -c '…'`) do not open a window: their output (panic, oops, Restarting) is a real-time event.
    Loader marker lines and kernel timestamp lines are never treated as echo or "typing" (a `LDR>T###%%> ` left open after an idle prompt is also a reset); "typing" means only an open line that matches characters the human actually sent.
    Kernel and app event lines inside the window all get `suspect:"cmd_output"` (they do not change the boot/restart state and raise no balloon; surrounding logs within the per-boot budget). A kernel boot line inside the window is a real-time boot only if preceded by at least 1 s of RX silence; otherwise it is a suspect boot.
  - Outside the window, lines are real-time — except that a line is suspect if its kernel timestamp is larger than (the time since the loader of the current boot seen in this run + 5 s), or more than 2 s smaller than the largest kernel timestamp of real-time lines outside windows in this boot (the case of a boot script printing pstore).
    The kernel clock stops during suspend but never goes backwards, so lines after repeated board suspend/resume stay real-time. There is no rule that turns a line into real-time.
  - The only lines that are not classified are definite reprints: command echo (a prompt followed by characters of a line sent within 2 s, part of that sent line, the line the human is typing, the first command line after a CR sent with arrow/Tab/Ctrl-P) and `^(\d+[:-])?<[0-7]>[ts]` (also after a file name or a `grep -n` line number).
- Reboot classification is done only by looking back at the moment of a real-time boot (no state is accumulated between boots): `cmd` = a `reboot` at the command position (sent — `busybox` and `/sbin/` prefixes allowed, but not `echo reboot` or `grep reboot` — or a recalled echo, or `reset` at the U-Boot prompt) within 60 s (a `Restarting system` arriving within that window extends it)
  → `hard` + `cause:{kind,line,ts,context}` = a panic/oops line in the previous 20 s of RX (real-time or suspect alike, except lines in a dump after which a shell prompt came back) → `soft` = the kernel line right before the loader marker is a `Restarting system` within 2 s (all 54 cases on real boards were 0.625–0.735 s; not if a real prompt came in between) → otherwise `hard` + `silence_s` (silence before the reset).
  `confirm` is not emitted if the last received line is an open pager line (`--More--`, `(END)`, `:`) or if bytes were sent after that panic. When the hub attaches to a running board, the kernel clock monotonicity check stays off until it sees a real-time boot, or until two kernel lines outside a window, more than 5 s apart, agree with each other.
  A suspect panic/oops is confirmed as that `cause` if a real-time boot follows within 60 s (balloon "panic 뒤 재부팅" (reboot after panic)); without a boot, after 30 s of RX silence a `confirm` is recorded (balloon "panic 뒤 응답 없음" (no response after panic)); if a prompt arrives, it is cancelled. `since_boot_s` uses monotonic time when the boot was seen in this run.
- Surrounding-log budget: per boot — for the same kind in the same boot, 3 per hour, after that 1 per 10 minutes; 30 per hour per port (exempt: the first real-time `panic` and `oops` of each boot and the lines before soft/hard boots); once the events folder exceeds `events.max_mb`, no more files are created (nothing is deleted). When a new kind joins a group that was being grouped without a file, it is judged again against that kind's budget (it does not inherit the group's skipped state). Such an event is written to jsonl with `context:null` and `context_skipped` (`rate`, `hourly`, `disk`); the number skipped is recorded in the next file as `skipped_before`, in its header and in its `# 끝` (end) line; and a one-line warning goes to hub.log when the budget kicks in. Events reduced by `rate` raise no balloon either.
- Writing: one jsonl stream per port is kept open and flushed per line; antivirus or sharing violations are retried for up to 2 s → queue (2000 lines) → retry every 200 ms. Events that can never be written are logged to hub.log as `사건 기록 :<port> 버림(…): <사건 JSON>` ("event record :<port> dropped (…): <event JSON>"). A truncated last line (power loss) is split off with a newline at the next start.
- On cable detach a `detach` is written (after reading the ring up to the detach point); on hub start or cable reconnect an `attach` is written to jsonl (com, and `gap_s` = from the detach time if the last record is a detach, otherwise from the later of the previous core's last heartbeat and its last record — because the alive heartbeat keeps running while detached; a reconnect counts from the detach time). If the gap exceeds 30 s, the start time of the current boot is set to unknown. On hub stop, the core waits 1.5 s for the tracking thread so that it can write `# 끝 — 허브 정지` ("end — hub stopped") into open surrounding-log files.
- Balloons: at most one per port per minute (several ports in the same tick share one balloon), and since `NotifyIcon` has no queue, a click is tied only to the last balloon. A click opens only `.txt` files under the events folder.

## Appendix. Old PowerShell version and serial bridge

- The old PowerShell prototype (supervisor `uart_hub.ps1`, registered as the Task Scheduler task `\uart_hub`) and the PowerShell serial bridge (`uart_bridge.ps1`) are not published (`internal/`). The current exe deals with their traces only as follows.
- The old installer (`install` subcommand) stops installing and explains how to stop that process if it sees it running (no automatic kill), and deletes the old task `\uart_hub` only if its action is `uart_hub.ps1`. setup only shows that task as a warning (read-only).
- The hub and another serial tool **cannot hold the same COM port at the same time** — to use that tool, first make the hub release the COM port with `/disconnect-uart COMx` (or tray → `정지` (Stop)).
