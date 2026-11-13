# UART Hub usage (one page)

The hub takes a board cable (COM port) **only when needed**. When you type **`/connect-uart COMx`** in Claude Code, the hub takes that COM port and opens a terminal. From then on everything is logged, and a person (terminal) and Claude share the same console. A person's typing always comes first. When you're done, release it with **`/disconnect-uart COMx`** (so you can use it again in MobaXterm and so on).

- **Only FTDI (VID_0403) USB-UART cables work.** The hub doesn't see cables with other chips such as CP210x or CH340.
- No administrator rights needed. **Nothing is installed**: it runs from where you put the exe (no copying, no startup entry).

## 1. Getting started: ask Claude to register it (recommended, no confirmation dialog)
1. Put the folder you received, `UartHub_<version>` (unzip it if it is a zip), where it will always stay on a local disk of this PC (not a network folder or USB). E.g. `C:\Tools\UartHub_<version>\`. **Don't move it after registering** (if you did, ask again from the new location).
2. Tell Claude Code (in WSL or on Windows, either works): **"Register the UART hub by following `C:\Tools\UartHub_<version>\INSTALL_UART_HUB.en.md`"**. Claude shows what it will do and registers the hub with no double-click and no confirmation dialog (the UART Hub icon appears at the right end of the taskbar; if you don't see it, look inside **`^` (Show hidden icons)**. `[Figure 2: the icon inside ^ (screenshot placeholder)]`).
3. When it's done, try typing **`/connect-uart`**. If it isn't in the slash-command list, start a new Claude Code session.
4. **To start it automatically at logon (do this yourself once)**: Win+R → `shell:startup` → **right-drag** the exe into the folder that opens and choose **'Create shortcuts here'**.
5. If a "The publisher could not be verified" warning appears when you open the desktop hub icon, right-click `UartHub.exe` → Properties → **'Unblock'**.
- **Another way (double-click)**: double-click `UartHub.exe` in the folder (if the blue "Windows protected your PC" window appears, click **More info → Run anyway** `[Figure 1: SmartScreen screen (screenshot placeholder)]`) → the first time only, a confirmation dialog: **[Start]**. After that it starts without a window, and if it is already running you only get an "already running" notice.

## 2. Connecting: `/connect-uart COM8` in Claude Code
- If you don't know the COM number, type just **`/connect-uart`**. You get a table with a row for each plugged-in COM port: "Connected 7002 EVB-A1 · 115200 / Not connected / In use by MobaXterm session".
- **Baud (speed)**: 115200 by default. For another speed, add it at the end, like **`/connect-uart COM3 9600`** (standard values from 1200 to 921600). It is remembered for that cable, and if the cable is already connected, the port is reopened at the new speed. After that, giving just the COM port uses that speed.
- If **another program is using** that COM port, a window appears on screen.
  - **MobaXterm session, TeraTerm, PuTTY, KiTTY**: a "COM8 in use: MobaXterm session" window → **[Disconnect and connect]**: ends **only that one session** and connects (other MobaXterm tabs stay as they are). **[Cancel]** (Enter and Esc also cancel): changes nothing.
  - **Other programs** (MobaXterm's main program, other serial programs): the hub doesn't end them. A "Please disconnect COM8 in that program" window appears, and once you disconnect, **the hub connects right away** (up to 2 minutes; the window shows the time left).
- Once connected, a terminal opens (the first one installed of **TeraTerm → PuTTY → MobaXterm**) and a **`board (port)`** icon appears on the desktop (e.g. `EVB-A1 (7002)`). If Claude says "No board output yet", check the board's power and wiring and press Enter in the terminal.
- **The connection is remembered until you disconnect** (when the hub starts again, it takes the port again). If another program is using that COM port at that point, the hub doesn't end it and only shows a notice: "COM8 is in use by MobaXterm. … To connect again: /connect-uart COM8".
- Tray icon → **COM ports** → that COM port → **Connect** does the same. A connected COM port is marked with an orange dot and offers **Open terminal** and **Disconnect**.

## 3. Disconnecting: `/disconnect-uart COM8`
- The hub closes the COM port and the desktop icon disappears. Now you can open that COM port again in MobaXterm and so on. Tray → COM ports → that COM port → **Disconnect** does the same.
- To release the whole hub for a while: tray icon → **Stop** (when you're done → **Start**).

## 4. Terminal and port numbers
- **Double-click the desktop icon = terminal.** TeraTerm and PuTTY bring an already open window to the front (MobaXterm always opens a new tab). To change it: tray → Settings → **Terminal program**. If none of the three is installed, telnet to `127.0.0.1:<port>`.
- The port number (7001, 7002…) is **assigned on first connect and stays with that cable**. Labeling your cables helps.

## 5. Asking Claude Code (WSL or Windows)
- E.g. "Run uname on the EVB-A1 board" · "Reboot the board and tell me when it has booted". If a person is typing in the terminal, Claude doesn't send anything and tells you that someone is typing.
- For a board that isn't connected, Claude says **"Please type `/connect-uart COMx`"**. **Claude never connects or disconnects by itself** (it could end another program's session, so only a person types these).
- **A permission screen appears the first time.** Pick the option that stops it asking again (the first time in each project folder). `[Figure 3: Claude Code permission screen (screenshot placeholder)]`
- If you installed Claude Code after the hub, ask for the registration in section 1 once more (it places the skills `uart`, `connect-uart` and `disconnect-uart`).

## 6. Aging runs and event detection
- **Name an aging run when you start it**: tray → COM ports → that COM port → **Start aging…** → name (default `<board>_<date-time>`) → [Start]. You can also tell Claude "Start aging on 7001, call it nightly" (if that COM port isn't connected, `/connect-uart` first).
- Only during an aging run, the hub writes **`console.log`** (that port's console lines) and **`events\`** (txt files for panics, Oopses and app errors) in `D:\uart_logs\aging\<name>_<date-time>\` (when the hub runs in Korean: `에이징\`, `콘솔.log`, `이벤트\`, `요약.txt`). When you choose **End aging** (or say "End aging on 7001 and summarize"), it writes **`summary.txt`** (duration, boot count, event list, gaps in recording). If the hub is stopped and started again, recording continues.
- **To check that it is recording**: the tray icon shows a **star + green dot**, the tooltip shows "UART Hub · 3 connected · Aging 1", the top of the menu shows "● Aging · nightly · 2 h 13 min · Last output 3 s ago" (with no output for over 10 minutes, "No output 12 min": check the board and cable), and the terminal window title ends with " · aging". You can also ask Claude "Is aging recording right now?".
- Console lines outside an aging run are kept in the hidden `spool` in the hub folder (`임시기록` in Korean) for 24 hours only (2 GB in total), with no event detection and no balloons.
- During an aging run the hub keeps watching the console lines and records **kernel panic, Oops, BUG, OOM**, **app errors** (patterns defined in the product profile `UartHub.profile.json`, e.g. the firmware recovery code `APPFAULT(ED)` or a watchdog reset message. Normal-exit signatures can be left out in the profile. Without a profile, app errors are only the patterns you add with `events.patterns` in config) and **reboots** (a restart with no command is an unexpected reboot). A `reboot` typed by a person or Claude only counts as an "expected reboot".
- **Balloons are shown only for panic, Oops and app errors** (including reboot after a panic and no response after a panic): "EVB-A1 (7001): Kernel panic, 17:38:51. Click to open the context log." Other reboots, BUGs and OOMs are **only recorded**, because in a power on/off aging run every cycle is a hard reset and the balloons would pile up (you can change this with `hub\config.json` `events.notify`).
- For each notified event, the hub writes **one txt file** in the run folder's `events\`. The name alone tells you what happened: `173851_kernel-panic.txt` · `…_oops.txt` · `…_app-error_APPFAULT(ED).txt` (`_suspect` at the end if it was inside command output). At the top come the event kind, board, time, cause line and console log file, then the 200 lines before and the lines after, and at the end **`Result:`** (the time it rebooted / no response / kept running). **Clicking the balloon opens that file in Notepad** (grouped to one balloon per port per minute, "(+N more)").
- To see them all: tray → **Open aging folder** (`D:\uart_logs\aging`). Reboot, BUG, OOM and cable connect/disconnect records don't get a txt and stay only in `hub` (used for the boot count and intervals in the summary). Tell Claude "Start aging and tell me if it panics" / "Summarize last night's aging" and it watches or tabulates the results (it sends nothing to the board).
- Error lines in the output between someone pressing Enter and the next prompt (`dmesg`, pstore, `cat` of a log file, `tail -f`, `echo c > /proc/sysrq-trigger` and so on) may be old logs, so they are recorded only as **"inside command output" (suspect)** and don't trigger a balloon by themselves (they aren't discarded).
  When the output has been quiet for 10 seconds, that command's output is treated as finished, so if you leave an aging script or `tail -f` running and walk away, errors after that are notified normally.
  What comes next shows whether it was real: if the board resets within 60 seconds, you get a **"Reboot after panic"** balloon (with the cause line in that reboot's record); if there is no output at all for 30 seconds and no reset, you get a **"No response after panic"** balloon. If the prompt comes back, it was an old log.
  Lines that start with `<number>[time]`, like `/proc/kmsg` or `grep -n` output, are certainly reprints and are not recorded. On boards whose product profile defines a loader marker (e.g. `LDR>T`), that marker always counts as a real reset.
- If the same error repeats within one boot, fewer txt files are written (after 3 in an hour, 1 per 10 minutes); the first panic of each boot is always kept. If the folder grows past 2 GB, no more are created (nothing is deleted: clean up yourself).

## 7. Update, quit, remove, logs
- **Update**: get the new folder `UartHub_<new version>` and ask Claude the same thing ("Register the UART hub by following `<new folder>\INSTALL_UART_HUB.en.md`"). It stops the existing hub and switches to the new exe. Ports, connections and logs stay. Delete the old folder yourself after checking (and recreate the autostart shortcut for the new exe). With a double-click: double-click the new exe → "Switch to the new version?" → **[Switch]**.
- When you upgrade from an earlier version (one that took a cable as soon as it was plugged in), the line `"on_demand": true` is added to `hub\config.json`. Cables you already used (that were connected) are still taken; cables the hub hasn't seen before are connected with `/connect-uart`. **If you go back from this version to an old exe**, the old version doesn't know that line and won't start, so delete the line before running it.
- **Quit**: tray → **Quit hub**. It starts again when Claude calls `uart` or you click a desktop icon. **If something seems wrong**: quit, then double-click the exe again.
- **Remove**: ask Claude "Remove the UART hub as described in INSTALL_UART_HUB.en.md" (or tray → **Remove…** → [Remove]). It deletes only the desktop hub icons, the Claude skills and the settings files. **The exe and the logs stay** (also delete any startup shortcut you made yourself).
- Log location: `D:\uart_logs` (`%USERPROFILE%\uart_logs` if there is no D:). Tray → **Open log folder**: `aging\<name>_<date-time>\` (aging runs), `hub` (settings, `hub.log`, hidden `spool`). Don't delete `hub\config.json`.
- When you upgrade to this version, `"log_layout"` in `hub\config.json` becomes `3` (added if missing). `콘솔로그\` and `에이징이벤트\` written by earlier versions stay as old records, and only new records go to the locations above.
- **If you plug a 4-channel adapter into a different USB port, its COM numbers change** → always use the same USB port. Clear out leftover records in tray → Settings → **Clean up disconnected UARTs…**.

## 8. If something goes wrong
- **Contact the person who sent you this file.** If an error window shows a log file path, send that file along.
