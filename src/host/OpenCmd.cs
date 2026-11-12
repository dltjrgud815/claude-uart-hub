// `UartHub.exe open <포트> [--from balloon]` (계획 §2.3 f, T3, C-9): 바탕화면 보드 아이콘 더블클릭·트레이 풍선 클릭이 부른다.
//   1. 허브 폴더(C-2: UART_HUB_DIR → install.json hub_dir). 없으면 exit 103(창 없음 — 아이콘은 설치본만 만든다).
//   2. 코어 `ls --detail`(제어 포트). 닿지 못하면 C-9: UART_HUB_DIR 이 없고(install.json 으로 정함)·install.json installed·감독 mutex 와 update mutex 가
//      모두 없고·정지(control.json stopped)가 아니면 install.json 의 exe 로 sup 을 분리 기동하고 코어를 ≤10초 기다린다. 그래도 터미널은 연다.
//   3. 창 제목(트레이 포트 줄과 같은 "<보드> · <COM> · <p>", 모르는 칸은 뺌): ls 의 보드명·COM → 코어가 없으면 icons.json 의 label(COMn 이면 COM 칸) → "<p>".
//   4. 터미널: prefs.json(auto|지정) → Terminals.Pick. 하나도 없으면 안내 창("127.0.0.1:<p> 로 telnet") → exit 3.
//   5. 이미 열린 창(TeraTerm·PuTTY, 명령줄의 접속 대상 127.0.0.1:<p>·-P <p> 로 찾음 — 창 제목은 감독이 바꾸므로 보지 않는다): 앞으로(거부되면 FlashWindowEx) → exit 0.
//      끊긴 PuTTY((inactive))·MobaXterm(늘 새 탭)·창 없음은 새로 실행. 새로 띄운 TeraTerm·PuTTY 는 <허브>\terminals\<pid>.json 에 남긴다(감독이 보드명이 잡히면 제목을 바꾼다, TermTitles.cs).
//   hub.log([open]) 한 줄: "open <p>[ (from balloon)] → front pid=N (teraterm)" | "… → launch teraterm pid=N <경로> <인자>" | "… → 터미널 없음(안내 창)".
//   같은 줄을 표준 출력에도 쓴다(파이프가 있을 때 — 시험). 종료코드: 0 앞으로·실행 · 3 터미널 없음·실행 실패(안내 창) · 103 허브 폴더 없음 · 106 인자 오류.
//   시스템 접근(파일·프로세스·창·실행·안내 창)은 SystemAccess(C-13) — 시험 모드에서는 아무것도 띄우지 않고 "[ext:fake]" 줄만 hub.log 에 남긴다.
//   4~5 단계(OpenTerminal)는 /connect-uart(ConnectFlow, X5 부록 B.3-6)도 같은 코드로 부른다(안내 창 없이 결과만).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace UartHub
{
    public static class OpenCmd
    {
        public const int RcNoTerminal = 3;

        static void Out(string s)
        {
            try
            {
                using var o = Console.OpenStandardOutput();
                var b = new UTF8Encoding(false).GetBytes(s + "\n");
                o.Write(b, 0, b.Length);
            }
            catch (Exception) { }
        }

        public static int Run(string[] a)
        {
            int port = 0;
            bool balloon = false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] == "--from") { balloon = i + 1 < a.Length && a[++i] == "balloon"; continue; }
                if (port == 0 && int.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out int p) && p >= 1 && p <= 65535) { port = p; continue; }
                Out("open: E106 인자 오류 '" + a[i] + "' — open <포트> [--from balloon]");
                return 106;
            }
            if (port == 0) { Out("open: E106 포트가 없음 — open <포트> [--from balloon]"); return 106; }
            NativeHandles.DontInheritStd();                              // 띄우는 감독·터미널이 호출 쪽 파이프를 물고 남지 않게

            string hubDir = InstallInfo.ResolveHubDir(out bool fromInstall);
            if (string.IsNullOrEmpty(hubDir)) { Out("open: E103 설치되지 않음(UART_HUB_DIR·" + InstallInfo.DefaultPath + " 없음)"); return 103; }
            var log = new HubLog(Path.Combine(hubDir, "hub.log"), SystemClock.Instance, "open");
            SystemAccess.ExternalLog = s => log.Write(s);
            string head = "open " + port.ToString(CultureInfo.InvariantCulture) + (balloon ? " (from balloon)" : "");
            void Result(string s) { log.Write(head + " → " + s); Out(head + " → " + s); }
            try
            {
                // ---- 코어(이름·C-9)
                var rows = Ls(hubDir);
                if (rows == null)
                {
                    string why = SelfHealBlock(hubDir, fromInstall);
                    if (why == null)
                    {
                        string exe = CoreMain.RespawnExe(out _);
                        var sp = SystemAccess.StartDetached(exe, new[] { "sup" });
                        log.Write(head + ": 자가 복구(C-9) — 코어 미응답·감독 없음 → 감독 다시 띄움 pid=" + sp.Pid + ", 코어를 ≤10초 기다림");
                        var sw = Stopwatch.StartNew();
                        while ((rows == null || !rows.Any(r => r.Port == port)) && sw.ElapsedMilliseconds < 10000) { Thread.Sleep(300); rows = Ls(hubDir) ?? rows; }
                        log.Write(head + ": 코어 " + (rows != null ? "응답(" + sw.ElapsedMilliseconds + "ms)" : "10초 안에 응답 없음 — 터미널은 그대로 연다"));
                    }
                    else log.Write(head + ": 코어 미응답 — 감독을 띄우지 않음(" + why + "), 터미널은 그대로 연다");
                }
                var row = rows?.FirstOrDefault(r => r.Port == port);
                string board = row?.Board, com = row?.Com;
                if (row == null)
                {
                    string fb = DesktopIcons.LabelFromList(port);                // 아이콘 label = 보드명 또는 COMn
                    if (fb != null && Regex.IsMatch(fb, @"^COM\d+$", RegexOptions.IgnoreCase)) com = fb; else board = fb;
                }

                // ---- 터미널
                string kind = OpenTerminal(hubDir, port, board, com, true, x => log.Write(head + ": " + x), Result);
                return kind == "none" ? RcNoTerminal : 0;
            }
            finally { SystemAccess.ExternalLog = null; }
        }

        /// <summary>
        /// 터미널 열기(open·/connect-uart 공통, X5 부록 B.3-6): prefs.json(auto|지정) → Terminals.Pick → 그 포트로 이미 열린 창(TeraTerm·PuTTY)이면 앞으로,
        /// 아니면 실행(창 제목 = Terminals.Title(board, com, port), PuTTY 는 실행 뒤 SetWindowText). 결과 "focused" | "launched" | "none"(터미널 없음·실행 실패).
        /// note = 안내 창(open 만 — connect 는 응답으로 알린다). 띄운 TeraTerm·PuTTY 는 TermRecords 에 남긴다(감독이 제목을 맞춘다).
        /// info = 중간 기록 한 줄(prefs 대체·끊긴 PuTTY·창 없음), result = 결과 한 줄("front pid=…" · "launch …" · "터미널 없음(…)" · "실행 실패 …").
        /// launched = 새로 띄운 터미널 pid(실행 직후, 창 제목 대기 전 — connect 가 그 pid 에만 전면 권한을 준다). 시험 모드 pid 는 0.
        /// </summary>
        public static string OpenTerminal(string hubDir, int port, string board, string com, bool note, Action<string> info, Action<string> result, Action<int> launched = null)
        {
            var t = Terminals.Pick(TermPrefs.Load(), out string pickNote);
            if (pickNote != null) info(pickNote);
            if (t == null)
            {
                result(note ? "터미널 없음(안내 창)" : "터미널 없음");
                if (note)
                    SystemAccess.ShowNote("UART 허브", "터미널(TeraTerm·PuTTY·MobaXterm)을 찾지 못했습니다.\n\n127.0.0.1:" + port + " 로 telnet 접속하세요.\n"
                                                  + "터미널을 설치했다면 트레이 아이콘 → 터미널 변경 → 찾아보기 로 지정하세요.");
                return "none";
            }
            var names = Terminals.ProcessNames(t.Kind);
            var procs = names.Length > 0 ? SystemAccess.ProcessesNamed(names) : new List<ProcRow>();
            var hit = Terminals.FindOpen(t.Kind, port, procs, out string skipped);
            if (skipped != null) info(skipped);
            if (hit != null)
            {
                string fr = SystemAccess.FrontWindow(hit);
                if (fr != null)
                {
                    result("front pid=" + hit.Pid + " (" + t.Kind + (fr == "flash" ? ", 전면 거부 → FlashWindowEx" : "") + ")");
                    return "focused";
                }
                info("pid=" + hit.Pid + " 의 창이 없음 — 새로 실행");
            }
            string title = Terminals.Title(board, com, port);
            string args = Terminals.Args(t.Kind, port, title);
            int pid;
            try { pid = SystemAccess.LaunchGui(t.Path, args); }
            catch (TestIsolationViolation) { throw; }
            catch (Exception e)
            {
                result("실행 실패 " + t.Kind + " " + t.Path + ": " + e.Message);
                if (note) SystemAccess.ShowNote("UART 허브", Terminals.Label(t.Kind) + " 를 실행하지 못했습니다: " + e.Message + "\n\n127.0.0.1:" + port + " 로 telnet 접속하세요.");
                return "none";
            }
            launched?.Invoke(pid);
            result("launch " + t.Kind + " pid=" + pid + " " + t.Path + " " + args + " (" + t.Source + ")");
            TermRecords.Add(hubDir, port, pid, SystemAccess.ProcessStartTicks(pid), t.Kind, Terminals.WindowTitle(t.Kind, title));
            if (t.Kind == "putty") SystemAccess.SetWindowTitle(pid, Terminals.WindowTitle(t.Kind, title), 3000);
            return "launched";
        }

        /// <summary>C-9 자가 복구를 막는 이유(null = 감독을 띄운다). Cli.HubDown 과 같은 조건.</summary>
        static string SelfHealBlock(string hubDir, bool fromInstall)
        {
            if (!fromInstall) return "UART_HUB_DIR 로 허브 폴더를 정함(운영 설치가 아님)";
            if (HubFiles.IsStopped(hubDir)) return "정지 상태(control.json stopped)";
            if (CoreMain.MutexExists(SystemAccess.MutexName("sup"))) return "감독 실행 중(코어 재시작 대기)";
            if (CoreMain.MutexExists(SystemAccess.MutexName("update"))) return "업데이트 중(update mutex)";
            return CoreMain.RespawnExe(out string why) == null ? why : null;
        }

        /// <summary>코어 ls --detail. 닿지 못하면 null.</summary>
        static List<CableRow> Ls(string hubDir)
        {
            string line = ControlClient.CallWithTokenFile(ControlPort(hubDir), Path.Combine(hubDir, "token"), 2000, "[\"ls\",\"--detail\"]");
            if (line == null) return null;
            try
            {
                using var d = JsonDocument.Parse(line);
                if (!d.RootElement.TryGetProperty("cables", out var cs) || cs.ValueKind != JsonValueKind.Array) return null;
                return cs.EnumerateArray().Select(CableRow.From).ToList();
            }
            catch (JsonException) { return null; }
        }

        /// <summary>제어 포트: UART_HUB_CONTROL_PORT → 살아 있는 코어의 core.pid control_port → config.json → 7200(Cli 와 같은 순서, 오류는 기본값).</summary>
        static int ControlPort(string hubDir)
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("UART_HUB_CONTROL_PORT"), NumberStyles.None, CultureInfo.InvariantCulture, out int e) && e >= 1 && e <= 65535) return e;
            var cp = HubFiles.ReadJson(HubFiles.CorePid(hubDir));
            if (cp.HasValue)
            {
                long p = HubFiles.Long(cp.Value, "control_port");
                if (p >= 1 && p <= 65535 && HubFiles.IsAlive(HubFiles.Long(cp.Value, "pid"), HubFiles.Long(cp.Value, "start_ticks"))) return (int)p;
            }
            try
            {
                string cfg = HubFiles.ConfigPath(hubDir);
                if (SystemAccess.FileExists(cfg)) return Config.Load(cfg).ControlPort;
            }
            catch (Exception) { }
            return 7200;
        }
    }
}
