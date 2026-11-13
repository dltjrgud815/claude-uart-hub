# UART Hub install guide (Claude registers it)

This registers UART Hub, which lets a person (terminal) and Claude share a board's UART console, on this PC.
Nothing is installed. The `UartHub.exe` in the folder you received is used **where it is, as it is** (no copying, no startup entry, no administrator rights needed).

## Summary for users

1. Put the folder you received (`UartHub_<version>/`; unzip it if it is a zip) **where it will always stay on a local disk of this PC**, e.g. `C:\Tools\UartHub_<version>\`
   (not a network folder, a USB drive or inside WSL (`\\wsl.localhost\…`)). After registering, don't move or delete the folder (if you moved it, make the same request once more from the new location).
2. Ask Claude Code as follows (for Claude Code in WSL, use the path `/mnt/c/Tools/UartHub_<version>/INSTALL_UART_HUB.en.md`).
   ```
   Register the UART hub by following C:\Tools\UartHub_<version>\INSTALL_UART_HUB.en.md
   ```
   There is no double-click and no confirmation dialog: this request is your consent. Claude first shows what it will do (the plan), then registers the hub.
   If the message that came with the package has an `EXE_SHA256` value, include it in your request (it checks that the exe was not swapped).
3. When registration is done, **type `/connect-uart` in Claude Code yourself**. If it isn't in the slash-command list, **start a new Claude Code session**.
4. Usage (details in `USAGE_ONE_PAGE.en.md` in the same folder):
   - Connect: `/connect-uart COM8` (just `/connect-uart` if you don't know the COM number) · Disconnect: `/disconnect-uart COM8`. **Only a person** types these.
   - Board work: "Run uname on the EVB-A1 board", "Reboot the board and tell me when it has booted".
5. **To start the hub automatically at logon (do this yourself once)**: Win+R → `shell:startup` → **right-drag `UartHub.exe` into the folder that opens and choose 'Create shortcuts here'**. Claude does not create startup entries.
6. If a "The publisher could not be verified" security warning appears when you open the desktop hub icon: right-click `UartHub.exe` → **Properties** → tick **'Unblock'** at the bottom → OK (this clears the mark for files downloaded from the internet; a person does this).

## Package folder contents

```text
UartHub_<version>/
├── INSTALL_UART_HUB.md     This document in Korean (user summary + the register, update and remove procedure Claude follows)
├── INSTALL_UART_HUB.en.md  This document in English
├── UartHub.exe             The hub itself (a single unsigned exe). Run it from here: don't copy, move or rename it
├── USAGE_ONE_PAGE.md       One-page usage for people, in Korean
├── USAGE_ONE_PAGE.en.md    One-page usage in English
├── GUIDE.md                Short usage guide in Korean (purpose, one-line install, daily use, aging, when stuck)
├── SHA256SUMS              SHA-256 of the files above (sha256sum -c format). It only checks that nothing broke in transit (swapping is checked with EXE_SHA256 from the message)
└── UartHub.profile.json    (optional) Product profile: loader marker, app error patterns and so on per board and firmware. Only some packages include it.
                            It is read only when it is in the same folder as the exe, so don't move or rename it (without it, generic Linux defaults apply)
```

---

## Claude's install procedure

> This section is the procedure Claude follows when asked to register the hub. Go through it in order, and report at the end in the Step 7 format.
> Don't run any command that is not in this document, and don't create or change files (other than the files the exe writes itself). Don't git commit or push.
> For what not to do, read **"Not allowed"** at the bottom first, especially: copying or moving the exe, `-ExecutionPolicy Bypass`, writing startup entries, removing MotW, `uart connect`.

### Before you start: environment and path notation
The exe is a Windows program and gives the same result from any shell (it registers on the Windows side, and the skills go to Windows and to **every** WSL distro that has `.claude`).
Pick your current shell in the table below, and write `<PKG>`, `$EXE` and `$UART` in this document in that notation.

| Environment | How to tell | `<PKG>` (e.g. `C:\Tools\UartHub_<version>`) | `$UART` (skills wrapper) | Getting output |
|---|---|---|---|---|
| WSL | `uname -r` contains `microsoft` | `/mnt/c/Tools/UartHub_<version>`: keep the drive letter, `/mnt/<drive>/…` (`wslpath -u 'C:\…'`) | `~/.claude/skills/uart/uart` | Standard output as is, exit code `$?` |
| Windows (Git Bash) | `uname -s` is `MINGW64_NT…` or `MSYS_NT…` | `/c/Tools/UartHub_<version>` (`cygpath -u 'C:\…'`) | `"$(cygpath -u "$USERPROFILE")/.claude/skills/uart/uart"` | **Append `\| cat`** (it is a GUI program, so output can be lost), exit code `${PIPESTATUS[0]}` |

- `EXE="<PKG>/UartHub.exe"`. Don't use PowerShell (you run the exe directly, so there is no execution policy to change).
- **Set the Bash tool timeout to 600000 ms (10 minutes)** (setup cuts WSL distro probing and skills placement off within 2 minutes, and checking that the supervisor started takes up to 30 seconds).
- **Result file** `%LOCALAPPDATA%\uart_hub\setup_last.json`: the same one-line JSON as the `--json` output (non-ASCII text such as Korean is written as `\uXXXX`). setup writes `"result":"running"` to this file as soon as it starts, and replaces it with the result when it ends.
  - WSL: `"$(wslpath -u "$(cmd.exe /c "chcp 65001>nul & echo %LOCALAPPDATA%" 2>/dev/null | tr -d '\r')")/uart_hub/setup_last.json"`
  - Git Bash: `"$(cygpath -u "$LOCALAPPDATA")/uart_hub/setup_last.json"`
  - Note `T0=$(date '+%Y-%m-%dT%H:%M:%S')` right before running, and trust the file as this run's result only if its `at` is equal to or later than `T0`. `result:"running"` means it is still running or was cut off midway.
  - **If the Bash tool ends with a timeout, don't run it again.** Read this file after 1 minute and check `at` and `result`. If it is still `running`, report and stop.

### Step 1: check the source folder
- Check that `<PKG>/UartHub.exe`, `<PKG>/USAGE_ONE_PAGE.md`, `<PKG>/SHA256SUMS` and `<PKG>/INSTALL_UART_HUB.md` all exist. If any of them is missing, stop and report the missing files.
  `<PKG>/UartHub.profile.json` (product profile) is optional: continue without it.
- **Location**: `<PKG>` must be on a local fixed disk of this PC (WSL notation `/mnt/<drive>/…`, Git Bash notation `/<drive>/…`). If it is `\\wsl.localhost\…`, `//wsl$/…`, a network drive or USB, **stop** and ask the user once to move it to a local folder (e.g. `C:\Tools\`). Claude doesn't move it, and setup also blocks this location with exit 3.
- If it looks like a temporary location (inside a zip in `Downloads`, `%TEMP%`, a mail attachment preview), ask **only once** before registering: "The hub will run from this folder as it is. Will you keep it here?"
- Hash check:
  ```bash
  cd '<PKG>' && sha256sum -c SHA256SUMS && sha256sum UartHub.exe
  ```
  Every line of `SHA256SUMS` must be `OK` (otherwise **stop** and report: a file broke in transit). `SHA256SUMS` sits in the same folder, so it can't catch a swapped file.
  If the user gave the `EXE_SHA256` from the message they received, check that it equals the hash on the last line, and if it doesn't, **stop** and report. If they didn't give it, just put the hash in the report.

### Step 2: report the plan (changes nothing): `setup --json`
WSL:
```bash
EXE='/mnt/c/Tools/UartHub_<version>/UartHub.exe'
T0=$(date '+%Y-%m-%dT%H:%M:%S'); "$EXE" setup --json; echo "rc=$?"
```
Git Bash:
```bash
EXE='/c/Tools/UartHub_<version>/UartHub.exe'
T0=$(date '+%Y-%m-%dT%H:%M:%S'); "$EXE" setup --json | cat; echo "rc=${PIPESTATUS[0]}"
cat "$(cygpath -u "$LOCALAPPDATA")/uart_hub/setup_last.json"     # only if the output above was empty (check at ≥ T0)
```
Without `--yes` it **changes nothing** except the result file (stopped WSL distros are not woken and are left as `unknown`). What to look at in the JSON:

| Key | Meaning |
|---|---|
| `result`, `rc` | `planned`(0) = can proceed · `blocked`(3) = stop: read `message` and `next` and follow the table below |
| `exe`, `version`, `location` | The exe to be registered (this folder as is) and its version. If `location` is not null, there is a location problem (blocked) |
| `log_root`, `hub_dir` | Log location (`D:\uart_logs` if D: is a local fixed disk, otherwise `%USERPROFILE%\uart_logs`; an existing older hub folder if there is one) and hub folder. Under it: `aging\<name>_<stamp>\` (one aging run: `console.log`, `events\`, `summary.txt`; when the hub runs in Korean: `에이징\`, `콘솔.log`, `이벤트\`, `요약.txt`) and `hub\` (hidden `spool\` = console log outside aging runs, kept 24 hours; `임시기록\` in Korean, and an existing `임시기록\` keeps being used) |
| `install` | Current registration (`install.json`); `null` the first time |
| `running` | The running hub (`pid`, `exe`, `version`, `same_exe`). If it is another exe, `--yes` stops that hub and switches to this exe (ports, settings and logs are kept; open terminals disconnect for a few seconds) |
| `downgrade` | If not null, this exe is older than the running or registered version (`from`→`to`): a downgrade |
| `stopped` | If true, the hub is in the 'stopped' state (stopped from the tray). Registration works, but the user must click tray → **Start** before it records |
| `legacy` | Leftovers of the old install method (the earlier installer). If present, `blocked` |
| `skills` | Where they go: `windows` + WSL distros. `result` = `install` (placed), `update` (updated), `same` (unchanged), `skipped` (no .claude, etc.), `unknown` (stopped distro: not woken for the plan, checked with `--yes`), `failed` (if `note` is `budget…`, the time budget ran out) |
| `motw` | `present:true` = it has the downloaded-from-the-internet mark (Zone.Identifier) (`null` = could not check). **Don't remove it**: only tell the user about it in Step 7 |
| `warnings` | A hub port used by another program, the old PowerShell version's task `\uart_hub`, the environment variable `UART_HUB_DIR`. Report only (they don't block) |
| `autostart` | UartHub shortcuts in `shell:startup` (target, `this_exe`). Read only |
| `actions` | What `--yes` will do (empty array = already registered with this exe) |

- Show the user `actions`, `log_root`, `running` (if any) and `warnings` (if any) briefly. The user has already asked to register, so unless one of the cases below applies, go on to Step 3 **without asking again**.
- Handling `blocked` (rc 3), told apart by `message`:

> The hub prints `message`, `next`, `actions` and its other text in its own language: Korean when Windows shows Korean, otherwise English, or as set by `"language"` in `config.json`. The messages below are the English ones. Match them by meaning or by `result`/`rc`.

| `message` | What to do |
|---|---|
| leftovers of the old install method (installer) | Tell the user about `legacy` and `next`, and **only after they agree** run `"$EXE" uninstall --yes` (Git Bash: with `\| cat`) → start again from Step 2. **If it is still blocked by the same leftovers after that, stop and report** (an old exe still running, for example: a person must check) |
| another installer/uninstaller (UART Hub window) is running | Ask the user to close the "UART Hub" window on screen (the install or remove confirmation window), then retry **only once** |
| another installer/update is swapping files | Retry **only once** after 1 minute |
| supervisor is running but cannot be identified · unregistered UART Hub supervisor is already running | Ask the user to click tray icon → **Quit hub**, then retry |
| this exe is on network or UNC path / removable disk … | Stop and ask the user to move it to a local fixed disk (Step 1) |
| downgrade: this exe … is older than the current version … | Ask the user **once**: "v`downgrade.from` is running now. Do you want to go back to v`downgrade.to`?" If yes, add `--allow-downgrade` to the Step 3 command. Otherwise stop |

### Step 3: check before registering, then `setup --yes --json`
- If `running` is not null and is another exe (a replacement), first check which boards are connected now: `"$UART" ls --json` (Git Bash: with `| cat`). If any `cables[].status` is `attached`,
  ask **once**: "Switching the hub takes a few seconds. During that time open terminals disconnect, and aging recording and event detection pause briefly. Switch now?"
- Register:

WSL:
```bash
T0=$(date '+%Y-%m-%dT%H:%M:%S'); "$EXE" setup --yes --json; echo "rc=$?"
```
Git Bash:
```bash
T0=$(date '+%Y-%m-%dT%H:%M:%S'); "$EXE" setup --yes --json | cat; echo "rc=${PIPESTATUS[0]}"
```

| `result` (`rc`) | Meaning | Next |
|---|---|---|
| `registered` (0) | First registration; the hub is running | Step 4 |
| `replaced` (0) | Stopped the hub of another exe (or another version) and switched to this exe | Step 4 |
| `updated` (0) | Brought the registration in line (placed the skills again, started a stopped hub, etc.) | Step 4 |
| `unchanged` (0) | Already registered and running with this exe; nothing changed | Step 4 |
| `blocked` (3) | Stopped without changing anything | The table in Step 2 |
| `rolled_back` (1) | Failed while changing things, so it **went back to the previous state** (for a replacement it restarted the previous hub; for a first registration it stopped the hub it had started and removed the registration) | Report. Don't run it again. If `next` contains "double-click the previous exe", pass that on as is |
| `failed` (1) | Failed: read `message` and `log` (`install.log` in the hub folder) | Report. Don't run it again |

- `health.ok:true` = the supervisor started and the control port responds (up to 30 seconds). Also check `hub_running` and `control_port`.
- Running it again with the same exe is safe (if there is nothing to change, the result is `unchanged`).
- If the user said "just register, don't start it now", add `--no-start` (the hub then starts from the icon, from Claude's `uart` call or when the exe is double-clicked).

### Step 4: verify
Check that the skills wrapper (`$UART`, see the table) reaches the hub. **Use only these two read-only commands** (Git Bash: with `| cat` on each):
```bash
"$UART" ls --json; echo "rc=$?"
"$UART" hub status --json; echo "rc=$?"
```
- `ls --json` → `rc=0` and `"status":"ok"`. An empty `cables`, or one with `pending`, is also normal (it connects only when needed: the hub doesn't take a COM port before `/connect-uart`).
- `hub status --json` → `rc=0`, and `version` equals the `version` in the setup JSON. `profile` is `null` when there is no product profile. If `profile.error` is present, the profile is broken and the hub runs on generic defaults, so report that text in Step 7.
- Don't call `uart connect` (not even without arguments: it is a human-only command).
- If the wrapper is missing (`No such file`): check in `skills` of the Step 3 JSON whether this environment is `skipped` (no .claude).
- If `rc=103` (hub_down), retry **only once** after 5 seconds. If it is still 103, report (ask the user to check the tray icon).

### Step 5: point the user to `/connect-uart` (always)
- `/connect-uart` and `/disconnect-uart` are slash commands that only a person invokes, so Claude can't check whether they appear in the list.
- So **always** say this: "Type `/connect-uart` in Claude Code yourself. If it isn't in the list, start a new Claude Code session."
  (If you registered from WSL and the user also uses Claude Code on Windows, the same goes for that session.)

### Step 6: (when it applies) stopped state and warnings
- If `stopped:true`, say: "The hub is stopped. Click tray icon → **Start** so that it records the boards."
- If there are `warnings`, pass them on as they are (a port used by another program → quit that program, or change `control_port` or `base_port` in `config.json`; old task `\uart_hub` → the user deletes it in Task Scheduler; `UART_HUB_DIR` → the user deletes the environment variable).

### Step 7: report to the user
Report only the following, briefly.
1. Result (`result`), version (`version`), exe location (used from this folder as is: don't move it) and the `sha256sum UartHub.exe` value (whether it matched the value in the message). For a replacement, also the previous exe and version (`running`).
2. Log location (`log_root`) and what changed (`actions`; for `unchanged`, "nothing changed"). When upgrading from an old version, `actions` shows `ensure "log_layout": 3 in config.json` (added if missing, 2 changed to 3) and `Log folder cleanup (…)`: while the hub is stopped, the date folders, `live` and `events` of a very old layout are moved to `콘솔로그\`, `에이징이벤트\` and `hub\events` by a rename within the same drive (anything already there is left as is; one line each in `install.log`). `콘솔로그\` and `에이징이벤트\` stay as old records. New records go to the aging run folders (`aging\`, or `에이징\` in Korean) and the hidden `hub\spool` (or `hub\임시기록`).
3. Where the skills were placed (`installed` and `updated` in `skills`: Windows and WSL distro names), and where they were skipped and why.
4. Verification (Step 4): `uart ls` rc and number of cables, `version` from `hub status`.
5. Guidance:
   - Type `/connect-uart` yourself (if it's missing, start a new session). Connect with `/connect-uart COMx`, disconnect with `/disconnect-uart COMx`.
   - Autostart is up to the user: Win+R → `shell:startup` → right-drag `UartHub.exe` there and choose 'Create shortcuts here'.
     If `autostart` has a shortcut with `this_exe:false`, say: "It points to the old exe. Delete it and create it again for the new exe." (Left as is, the old exe still starts the newer registered version.)
   - If `motw.present:true`: if a security warning appears when opening the icon, right-click `UartHub.exe` → Properties → 'Unblock' (Claude doesn't do this).
   - If Step 6 applies, what it says.

### Exit codes (summary)
| Command | 0 | 1 | 3 | 106 |
|---|---|---|---|---|
| `setup` (plan only) | Can proceed | Error | The real run would be blocked (old leftovers, location, another installer, unidentified supervisor, downgrade) | Argument error |
| `setup --yes` | Registered, replaced, brought in line, nothing changed | `rolled_back` (rolled back), `failed` | Stopped without changes | Argument error |
| `uninstall --yes` | Removed | Partly failed (the rest was removed; see the `# removal partly failed` line) | No registration (only settings and result files in `%LOCALAPPDATA%\uart_hub`), another installer running | Argument error |

---

## Updating

Get the new package folder (`UartHub_<new version>/`), put it where it will stay on a local disk, and make the same request.
```
Register the UART hub by following <new folder>\INSTALL_UART_HUB.en.md
```
- Claude follows the procedure above as is. In Step 2, `running` shows the exe in the previous folder, and the Step 3 result is `replaced`: it stops the previous hub (a few seconds; open terminals disconnect) and starts the new exe.
  Port numbers, connected cables, settings, logs, desktop icons (only their target changes to the new exe) and skills (updated for the new exe) carry over. If the new exe doesn't start, the result is `rolled_back` and the previous hub starts again.
- The user deletes the previous folder after checking (Claude doesn't delete it). If the user made an autostart shortcut, recreate it for the new exe (Step 7 guidance).
- To go back, make the same request with `INSTALL_UART_HUB.md` in the previous folder. If Step 2 is blocked by `downgrade`, ask the user once and add `--allow-downgrade`.

## Removing

Ask Claude: "Remove the UART hub as described in INSTALL_UART_HUB.en.md". Claude:
1. Sets `EXE` as in the "Before you start" table (`UartHub.exe` in this folder; it may be a different version from the registered one) and runs:
   - WSL: `"$EXE" uninstall --yes; echo "rc=$?"`
   - Git Bash: `"$EXE" uninstall --yes | cat; echo "rc=${PIPESTATUS[0]}"`
2. Exit code: 0 = removed · 3 = no registration (or another installer is running) · 1 = partly failed (report the `# removal partly failed` line and the `error:` lines; the other steps have already done their removal). Don't run it again.
3. What it removes: stops the hub, the desktop hub icons (only its own), the Claude skills `uart`, `connect-uart` and `disconnect-uart` (only those this registration placed: when the marker `.uart_hub` matches),
   `%LOCALAPPDATA%\uart_hub\install.json` and `icons.json`, leftovers of the old installer (startup and Start menu shortcuts carrying its own marker, `UartHub*.exe` in the install folder).
   What it keeps: the exe folder, logs and settings (`log_root`; `token`, `config.json` and `state.json` in the hub folder), the `%LOCALAPPDATA%\uart_hub` folder (`prefs.json`, `setup_last.json`).
4. Report: the hub that was stopped, the skills that were removed, and what remains (exe folder, log location). Tell the user to delete the autostart shortcut in `shell:startup` if they made one, and to delete the exe folder and logs themselves if they no longer need them.

## Turning autostart on and off (a person does this)

- On: Win+R → `shell:startup` → right-drag `UartHub.exe` into the folder that opens and choose **'Create shortcuts here'**. At logon that exe starts the hub without a window
  (if the shortcut points to an old exe and the registered version is newer, it starts the registered exe and leaves the registration unchanged).
- Off: delete the shortcut from that folder.
- Claude doesn't write to this folder, the registry Run key or Task Scheduler. Some antivirus behavior detection has blocked "copy itself → register at startup" as a malicious dropper.

## Not allowed (Claude)

- Copying, moving or renaming the exe. Run the `UartHub.exe` in the received folder as is. Don't call the old installer `UartHub.exe install` (copies itself, registers at startup) either.
- Changing the execution policy, e.g. `powershell -ExecutionPolicy Bypass`. PowerShell itself isn't needed.
- Writing startup entries: the `shell:startup` folder, the `HKCU\…\Run` key, Task Scheduler (deleting the old task `\uart_hub` is also up to the user).
- Removing MotW (Zone.Identifier): `Unblock-File`, `Remove-Item -Stream Zone.Identifier`, overwriting the `:Zone.Identifier` stream. Only tell the user about Properties → 'Unblock'.
- Running `uart connect` or `uart disconnect` (with or without arguments: a person uses `/connect-uart` and `/disconnect-uart`).
- Adding `--allow-downgrade` without asking the user.
- Running again or trying other ways to hide a failure. Apart from the one retry given above, report and wait for the user to decide.

## If something goes wrong

- Send the `message` of `failed`, `rolled_back` or `blocked`, together with the `log` (`install.log` in the hub folder) and the `setup_last` path, to whoever looks after the hub.
