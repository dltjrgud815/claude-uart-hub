// US-007 감독·코어 수명 시나리오(계획 §3.8, §4 S6 시뮬 20·33 + hang). 실제 별도 pwsh 프로세스로 감독(uart_hub.ps1 -NoTray)과
// 코어(hub_core.ps1 -Sim)를 띄운다 — 프로세스 수명이 핵심이라 in-process 흉내를 쓰지 않는다. 코어 안 FakeBoard(SimCore)만 쓰고 실제 COM 은 열지 않는다.
// 격리: mutex Local\uart_hub_test_{sup,core}_<id>_<난수>, 포트 제어 17300~17302·케이블 17350/17360/17370(기존 시나리오 17000~17290 과 안 겹침),
//       허브 폴더·로그 루트 = %TEMP%\uart_hub_test\<run>\<id>\{hub,logs}. 시간 상수 축소: backoff_s [1,2,4], hang_s 4, heartbeat_s 0.5, ping 0.5초.
// 정리: 시나리오가 띄운 pwsh(명령줄에 이 허브 폴더가 들어간 감독·코어)는 끝날 때 모두 kill 하고 잔존 0 을 확인한다(실패해도 Defer 로 정리).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace UartHub.Testing
{
    internal sealed class LifeRig
    {
        public ScenarioContext Ctx;
        public string HubDir, LogRoot, SpecPath, AppDir, SupMutex, CoreMutex, Cable;
        public int Ctl, Base;
        public double[] Backoff = { 1, 2, 4 };
        public double HangS = 4;
        readonly List<Process> sups = new List<Process>();

        static readonly Regex LogLine = new Regex(@"^\[(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3})\] \[(sup|core)\] (.*)$");

        public static LifeRig Create(ScenarioContext ctx, string tag, int ctl, int basePort, string cable, string com, string host, double backoffResetS = 30)
        {
            string rnd = Guid.NewGuid().ToString("N").Substring(0, 6);
            var r = new LifeRig
            {
                Ctx = ctx, Ctl = ctl, Base = basePort, Cable = cable,
                HubDir = Path.Combine(ctx.Dir, "hub"), LogRoot = Path.Combine(ctx.Dir, "logs"),
                AppDir = Path.GetDirectoryName(TestEnv.UartHubExe),
                SupMutex = @"Local\uart_hub_test_sup_" + tag + "_" + rnd, CoreMutex = @"Local\uart_hub_test_core_" + tag + "_" + rnd,
            };
            Directory.CreateDirectory(r.HubDir);
            r.WriteConfig(r.ConfigJson(backoffResetS));
            r.SpecPath = Path.Combine(ctx.Dir, "sim_spec.json");
            File.WriteAllText(r.SpecPath, "{\"boards\":[{\"cable\":" + Json.Str(cable) + ",\"com\":" + Json.Str(com) + ",\"hostname\":" + Json.Str(host) + ",\"printk_ms\":300}]}");
            ctx.Defer(r.KillAll);
            ctx.Log("hub_dir=" + r.HubDir + " mutex=" + r.SupMutex + "/" + r.CoreMutex);
            return r;
        }

        public string ConfigJson(double backoffResetS)
        {
            var inv = CultureInfo.InvariantCulture;
            return "{\n" +
                "  \"log_root\": " + Json.Str(LogRoot) + ", \"hub_dir\": " + Json.Str(HubDir) + ",\n" +
                "  \"base_port\": " + Base + ", \"control_port\": " + Ctl + ", \"vids\": [],\n" +
                "  \"scan_interval_ms\": 50, \"read_timeout_ms\": 50, \"writer_wait_ms\": 20, \"ring_mb\": 1, \"queue_mb\": 16,\n" +
                "  \"heartbeat_s\": 0.5, \"hang_s\": " + HangS.ToString(inv) + ", \"ping_interval_s\": 0.5,\n" +
                "  \"backoff_s\": [" + string.Join(", ", Backoff.Select(b => b.ToString(inv))) + "], \"backoff_reset_s\": " + backoffResetS.ToString(inv) + ",\n" +
                "  \"crash_loop_count\": 3, \"crash_loop_window_s\": 60,\n" +
                "  \"idle_s\": 0.3, \"quiet_ms\": 600, \"default_timeout_s\": 10\n}\n";
        }

        public void WriteConfig(string text) => File.WriteAllText(Path.Combine(HubDir, "config.json"), text);

        // ------------------------------------------------------------------ 프로세스
        public Process StartSup()
        {
            var psi = new ProcessStartInfo(TestEnv.UartHubExe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppDir };
            foreach (var a in new[] { "sup", "--no-tray", "--hub-dir", HubDir, "--sup-mutex", SupMutex, "--core-mutex", CoreMutex,
                                      "--update-mutex", SupMutex.Replace("_sup_", "_update_"), "--sim", SpecPath, "--sim-dll", TestEnv.TestsDll, "--tick-ms", "100" })
                psi.ArgumentList.Add(a);
            var p = Process.Start(psi);
            lock (sups) sups.Add(p);
            Ctx.Log("감독 시작 pid=" + p.Id);
            return p;
        }

        /// <summary>명령줄에 이 허브 폴더가 들어간 UartHub.exe 프로세스(WMI). kind = " core " | " sup "(하위 명령).</summary>
        public List<int> Procs(string kind)
        {
            var l = new List<int>();
            using var s = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'UartHub.exe'");
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    string cmd = o["CommandLine"] as string;
                    if (cmd != null && cmd.IndexOf(HubDir, StringComparison.OrdinalIgnoreCase) >= 0 && cmd.IndexOf(kind, StringComparison.OrdinalIgnoreCase) >= 0)
                        l.Add(Convert.ToInt32(o["ProcessId"], CultureInfo.InvariantCulture));
                }
            }
            return l;
        }

        public List<int> CoreProcs() => Procs(" core ");
        public List<int> SupProcs() => Procs(" sup ");

        public static bool Alive(int pid)
        {
            try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch (Exception) { return false; }
        }

        public static void Kill(int pid)
        {
            try { using var p = Process.GetProcessById(pid); p.Kill(); p.WaitForExit(5000); } catch (Exception) { }
        }

        /// <summary>이 시나리오가 띄운 감독·코어를 모두 끝낸다(감독 먼저 — 코어를 다시 띄우지 못하게).</summary>
        public void KillAll()
        {
            for (int round = 0; round < 3; round++)
            {
                var s = SupProcs();
                List<Process> mine;
                lock (sups) mine = sups.ToList();
                foreach (var p in mine) { try { if (!p.HasExited) { p.Kill(); p.WaitForExit(5000); } } catch (Exception) { } }
                foreach (int pid in s) Kill(pid);
                foreach (int pid in CoreProcs()) Kill(pid);
                if (SupProcs().Count == 0 && CoreProcs().Count == 0) return;
                Thread.Sleep(300);
            }
        }

        /// <summary>정리 후 잔존 0 확인(시나리오 끝).</summary>
        public void CheckNoLeftovers()
        {
            KillAll();
            var c = CoreProcs();
            var s = SupProcs();
            Ctx.Check(c.Count == 0 && s.Count == 0, "시험이 띄운 pwsh 잔존 0 (코어 " + c.Count + ", 감독 " + s.Count + ")");
        }

        // ------------------------------------------------------------------ 파일
        public JsonElement? ReadHubJson(string name) => HubFiles.ReadJson(Path.Combine(HubDir, name));
        public JsonElement? Sup() => ReadHubJson("sup.json");
        public JsonElement? CorePid() => ReadHubJson("core.pid");
        public string HubLogText() => HubFiles.ReadShared(Path.Combine(HubDir, "hub.log")) ?? "";
        public string CableLogs() => LogFiles.AllLogText(LogRoot);

        public bool CoreMutexHeld()
        {
            using var m = new Mutex(false, CoreMutex);
            bool got;
            try { got = m.WaitOne(0); } catch (AbandonedMutexException) { got = true; }
            if (got) { m.ReleaseMutex(); return false; }
            return true;
        }

        /// <summary>hub.log 줄(시각, 출처, 본문). 시각은 로컬 ms.</summary>
        public List<(DateTime T, string Src, string Msg)> HubLines()
        {
            var l = new List<(DateTime, string, string)>();
            foreach (var line in HubLogText().Split('\n'))
            {
                var m = LogLine.Match(line.TrimEnd('\r'));
                if (m.Success) l.Add((DateTime.ParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture), m.Groups[2].Value, m.Groups[3].Value));
            }
            return l;
        }

        /// <summary>새 코어(notPid 가 아님)가 core.pid 를 쓰고 살아 있고 제어 포트를 열 때까지. 그 PID.</summary>
        public int WaitCore(int notPid, int timeoutMs, string what)
        {
            int pid = 0;
            Ctx.WaitUntil(() =>
            {
                var j = CorePid();
                if (!j.HasValue) return false;
                pid = (int)HubFiles.Long(j.Value, "pid");
                return pid != notPid && HubFiles.Str(j.Value, "control") == "listening" && HubFiles.IsAlive(pid, HubFiles.Long(j.Value, "start_ticks"));
            }, timeoutMs, what + " (core.pid 기록·제어 포트 listen)");
            return pid;
        }

        public void WaitAttached(string what)
        {
            Ctx.WaitUntil(() =>
            {
                try
                {
                    var ls = Call("ls");
                    return ls.GetProperty("cables").EnumerateArray().Any(c => J.S(c, "cable") == Cable && J.S(c, "status") == "attached");
                }
                catch (Exception) { return false; }
            }, 10000, what + ": ls 에 " + Cable + " attached");
        }

        public string Token => (HubFiles.ReadShared(Path.Combine(HubDir, "token")) ?? "").Trim();

        public JsonElement Call(params string[] argv) => CtlClient.Call(Ctl, Token, 15000, argv);

        public void Inject(int pid, string cmd)
        {
            string dir = Path.Combine(HubDir, "sim");
            Directory.CreateDirectory(dir);
            string ack = Path.Combine(dir, "ack." + pid.ToString(CultureInfo.InvariantCulture));
            HubFiles.TryDelete(ack);
            File.WriteAllText(Path.Combine(dir, "inject." + pid.ToString(CultureInfo.InvariantCulture)), cmd);
            if (cmd != "crash") Ctx.WaitUntil(() => File.Exists(ack), 5000, "주입 " + cmd + " → 코어 pid=" + pid + " 처리");
        }

        /// <summary>UartHub.exe cli 를 프로세스로(UART_HUB_DIR = 이 허브 폴더). (종료코드, stdout, stderr)</summary>
        public (int Code, string Out, string Err) Uart(params string[] args)
        {
            var psi = new ProcessStartInfo(TestEnv.UartHubExe)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
            };
            psi.ArgumentList.Add("cli");
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["UART_HUB_DIR"] = HubDir;
            psi.Environment["UART_HUB_CONTROL_PORT"] = Ctl.ToString(CultureInfo.InvariantCulture);
            using var p = Process.Start(psi);
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(60000)) { try { p.Kill(); } catch (Exception) { } Ctx.Fail("uart.ps1 시간 초과: " + string.Join(" ", args)); }
            p.WaitForExit();
            var res = (p.ExitCode, so.Result.Trim(), se.Result.Trim());
            Ctx.Log("uart.ps1 " + string.Join(" ", args) + " → rc=" + res.ExitCode + " out=" + Trunc(res.Item2) + (res.Item3.Length > 0 ? " err=" + Trunc(res.Item3) : ""));
            return res;
        }

        static string Trunc(string s) => s.Length > 500 ? s.Substring(0, 500) + "…" : s;

        public static JsonElement Parse(string s)
        {
            using var d = JsonDocument.Parse(s);
            return d.RootElement.Clone();
        }

        /// <summary>감독 로그에서 "코어 종료 pid=X" 시각 → 그다음 "코어 시작" 시각 사이(초).</summary>
        public double RestartDelay(int deadPid)
        {
            var l = HubLines();
            int i = l.FindIndex(x => x.Src == "sup" && x.Msg.StartsWith("코어 종료 pid=" + deadPid + " ", StringComparison.Ordinal));
            if (i < 0) Ctx.Fail("hub.log 에 '코어 종료 pid=" + deadPid + "' 없음");
            int k = l.FindIndex(i + 1, x => x.Src == "sup" && x.Msg.StartsWith("코어 시작 pid=", StringComparison.Ordinal));
            if (k < 0) Ctx.Fail("hub.log 에 pid=" + deadPid + " 종료 뒤 '코어 시작' 없음");
            return (l[k].T - l[i].T).TotalSeconds;
        }

        /// <summary>케이블 로그에서 *** hub restart (reason) *** 다음 줄의 *** gap N (hub down) *** 값(초). 없으면 실패.</summary>
        public double RestartGap(string reason)
        {
            var lines = CableLogs().Split('\n').Select(x => x.TrimEnd('\r')).ToList();
            string want = "*** hub restart (" + reason + ") ***";
            int i = lines.FindIndex(x => x.EndsWith(want, StringComparison.Ordinal));
            if (i < 0) Ctx.Fail("케이블 로그에 '" + want + "' 없음");
            var m = i + 1 < lines.Count ? Regex.Match(lines[i + 1], @"\*\*\* gap (\d+\.\d) \(hub down\) \*\*\*$") : Match.Empty;
            if (!m.Success) Ctx.Fail("'" + want + "' 다음 줄이 *** gap … (hub down) *** 가 아님: " + (i + 1 < lines.Count ? lines[i + 1] : "(없음)"));
            Ctx.Log("ok: 케이블 로그 " + want + " → " + lines[i + 1].Substring(Math.Max(0, lines[i + 1].IndexOf("***", StringComparison.Ordinal))));
            return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }
    }

    internal static class LifecycleScenarios
    {
        static string S(JsonElement? e, string k) => e.HasValue ? HubFiles.Str(e.Value, k) : null;
        static long N(JsonElement? e, string k) => e.HasValue ? HubFiles.Long(e.Value, k, long.MinValue) : long.MinValue;
        static bool B(JsonElement? e, string k) => e.HasValue && HubFiles.Bool(e.Value, k);

        // ================================================================== 20
        [Scenario("20", "코어 강제 종료(kill) → 감독이 백오프(축소 1초) 안에 재시작, 케이블 로그에 *** hub restart (exit=-1) *** + *** gap N (hub down) ***. UnhandledException 주입 → *** hub crash *** 후 exit 70 → 다음 백오프(2초)로 재시작. hub upgrade(같은 버전) → exit 0 → 즉시 재기동·*** hub restart (upgrade) ***. hub stop → exit 0 은 재시작 안 함. 잔존 pwsh 0",
                  Order = 800, TimeoutS = 180)]
        public static void S20(ScenarioContext ctx)
        {
            var r = LifeRig.Create(ctx, "s20", 17300, 17350, "SIMCAB20", "COM320", "SIM20");
            r.StartSup();
            int c1 = r.WaitCore(0, 30000, "첫 코어");
            r.WaitAttached("첫 코어");
            ctx.Check(r.CoreProcs().Count == 1 && r.SupProcs().Count == 1, "감독 1·코어 1 프로세스");
            ctx.Check(!r.CableLogs().Contains("*** hub restart"), "첫 기동에는 *** hub restart *** 없음");
            var st = r.Call("hub", "status");
            ctx.Check(J.S(st, "status") == "ok" && st.TryGetProperty("supervisor", out var sv) && sv.ValueKind == JsonValueKind.Object
                      && J.S(sv, "state") == "running" && J.N(sv, "core_pid") == c1 && J.B(sv, "alive") == true,
                      "hub status(코어 경유)에 supervisor 상태: running, core_pid=" + c1);
            Thread.Sleep(1200);                                       // 하트비트(0.5초) 몇 번

            // ---- 강제 종료
            var sw = Stopwatch.StartNew();
            LifeRig.Kill(c1);
            int c2 = r.WaitCore(c1, 20000, "kill 뒤 재시작된 코어");
            double took = sw.Elapsed.TotalSeconds;
            double d1 = r.RestartDelay(c1);
            ctx.Check(d1 >= r.Backoff[0] - 0.05 && d1 < r.Backoff[0] + 1.0, "감독: 종료 감지 → 재시작까지 백오프 1단계 " + d1.ToString("0.00", CultureInfo.InvariantCulture) + "초 (설정 " + r.Backoff[0] + "초)");
            ctx.Check(took < r.Backoff[0] + 5, "kill → 새 코어 제어 포트 listen 까지 " + took.ToString("0.0", CultureInfo.InvariantCulture) + "초 (백오프 + pwsh 기동 < 5초)");
            r.WaitAttached("재시작된 코어");
            var sup = r.Sup();
            ctx.Check(N(sup, "restarts") == 1 && N(sup, "last_exit") == -1 && S(sup, "last_exit_reason") == "exit=-1", "sup.json: restarts=1, last_exit=-1");
            ctx.WaitUntil(() => r.CableLogs().Contains("*** hub restart (exit=-1) ***"), 5000, "새 코어 로그에 restart 줄");
            double g1 = r.RestartGap("exit=-1");
            ctx.Check(g1 >= r.Backoff[0] && g1 < took + 1.5, "gap " + g1 + "초 = 이전 코어 마지막 하트비트부터(≥ 백오프 " + r.Backoff[0] + "초, ≤ 실제 공백 " + took.ToString("0.0", CultureInfo.InvariantCulture) + "초 + 하트비트 주기)");
            var hl = r.HubLogText();
            ctx.Check(Regex.IsMatch(hl, @"\[sup\] 코어 종료 pid=" + c1 + @" exit=-1 ") && Regex.IsMatch(hl, @"\[sup\] 재시작 대기 1초 \(백오프 0단계, exit=-1\)")
                      && Regex.IsMatch(hl, @"\[sup\] 코어 시작 pid=" + c2 + @" .* reason=exit=-1"), "hub.log: 코어 종료 exit=-1 → 재시작 대기 1초 → 코어 시작 reason=exit=-1");
            ctx.Check(r.CoreProcs().Count == 1, "코어 프로세스 1개");

            // ---- UnhandledException → *** hub crash *** → exit 70 → 백오프 2단계
            Thread.Sleep(800);
            r.Inject(c2, "crash");
            int c3 = r.WaitCore(c2, 20000, "crash 뒤 재시작된 코어");
            ctx.Check(Regex.IsMatch(r.CableLogs(), @"\*\*\* hub crash \(InvalidOperationException: sim crash\) \*\*\*"), "crash 한 코어의 케이블 로그에 *** hub crash (InvalidOperationException: sim crash) *** (큐 flush 뒤)");
            hl = r.HubLogText();
            ctx.Check(hl.Contains("[core] hub crash: 큐 flush 완료 → exit 70") && Regex.IsMatch(hl, @"\[sup\] 코어 종료 pid=" + c2 + @" exit=70 "), "hub.log: 코어 crash 처리기 flush 완료 → exit 70, 감독이 exit=70 감지");
            double d2 = r.RestartDelay(c2);
            ctx.Check(d2 >= r.Backoff[1] - 0.05 && d2 < r.Backoff[1] + 1.0, "두 번째 재시작은 백오프 2단계 " + d2.ToString("0.00", CultureInfo.InvariantCulture) + "초 (설정 " + r.Backoff[1] + "초)");
            r.WaitAttached("crash 뒤 코어");
            ctx.WaitUntil(() => r.CableLogs().Contains("*** hub restart (exit=70) ***"), 5000, "새 코어 로그에 restart (exit=70)");
            double g2 = r.RestartGap("exit=70");
            ctx.Check(g2 >= r.Backoff[1], "gap " + g2 + "초 ≥ 백오프 " + r.Backoff[1] + "초");
            sup = r.Sup();
            ctx.Check(N(sup, "restarts") == 2 && N(sup, "last_exit") == 70, "sup.json: restarts=2, last_exit=70");

            // ---- 업그레이드 골격(같은 버전): 코어 정상 종료(exit 0) → 감독이 바로(백오프 없이) 다시 띄움 → *** hub restart (upgrade) ***
            var u = r.Uart("hub", "upgrade", "--json");
            var uj = LifeRig.Parse(u.Out);
            ctx.Check(u.Code == 0 && J.S(uj, "action") == "upgrading" && J.S(uj, "to_version") == Hub.Version, "uart hub upgrade → action=upgrading, to_version=" + Hub.Version);
            int c4 = r.WaitCore(c3, 20000, "upgrade 뒤 코어");
            ctx.Check(r.HubLogText().Contains("[sup] 코어 종료 pid=" + c3 + " exit=0 ") && r.HubLogText().Contains("[sup] upgrade: 코어 재시작 v" + Hub.Version + " → v" + Hub.Version),
                      "hub.log: 이전 코어 exit 0 → upgrade: 코어 재시작 v… → v…");
            ctx.Check(r.RestartDelay(c3) < 0.5, "upgrade 재시작은 백오프 없이 즉시(" + r.RestartDelay(c3).ToString("0.00", CultureInfo.InvariantCulture) + "초)");
            r.WaitAttached("upgrade 뒤 코어");
            ctx.WaitUntil(() => r.CableLogs().Contains("*** hub restart (upgrade) ***"), 5000, "케이블 로그 restart (upgrade)");
            r.RestartGap("upgrade");
            c3 = c4;

            // ---- hub stop → exit 0 → 재시작 없음
            u = r.Uart("hub", "stop", "--json");
            uj = LifeRig.Parse(u.Out);
            ctx.Check(u.Code == 0 && J.S(uj, "status") == "ok" && J.B(uj, "stopped") == true && J.S(uj, "action") == "stopping", "uart hub stop(코어 경유 → 감독) → ok, stopped:true, action=stopping");
            ctx.WaitUntil(() => !LifeRig.Alive(c3), 10000, "코어 정상 종료");
            ctx.WaitUntil(() => S(r.Sup(), "state") == "stopped" && N(r.Sup(), "last_exit") == 0, 5000, "sup.json state=stopped, last_exit=0");
            Thread.Sleep((int)(r.Backoff[r.Backoff.Length - 1] * 1000) + 1000);
            ctx.Check(r.CoreProcs().Count == 0 && !r.CoreMutexHeld() && !File.Exists(Path.Combine(r.HubDir, "core.pid")), "exit 0 뒤 백오프 최대치 + 1초 지나도 코어 없음(core.pid 삭제, 코어 mutex 해제)");
            ctx.Check(Regex.IsMatch(r.CableLogs(), @"\*\*\* hub stop \*\*\*"), "케이블 로그에 *** hub stop ***");
            r.CheckNoLeftovers();
        }

        // ================================================================== 33
        [Scenario("33", "수명: 감독 중복 기동 → 즉시 exit 0. hub stop → stopped 영속, 감독 재기동해도 코어 미기동·COM 미점유(ls 103 stopped), hub start(감독 직접) → 기동. 감독만 kill → 새 감독이 기존 코어 입양(코어 1개). 깨진 config → last-good 기동 + 경고. 크래시 루프 → 백오프 1·2·4초 + 경고, 정상 운용 뒤 초기화. 감독·코어 함께 사라짐 → 새 감독이 restart(exit=unknown)+gap. 잔존 pwsh 0",
                  Order = 810, TimeoutS = 300)]
        public static void S33(ScenarioContext ctx)
        {
            var r = LifeRig.Create(ctx, "s33", 17301, 17360, "SIMCAB33", "COM330", "SIM33", backoffResetS: 6);
            string lg = Path.Combine(r.HubDir, "config.last-good.json");
            string ctlPath = Path.Combine(r.HubDir, "control.json");

            // ---- A. 중복 감독 / hub stop / stopped 상태 재기동 / hub start
            var sup1 = r.StartSup();
            int c1 = r.WaitCore(0, 30000, "첫 코어");
            r.WaitAttached("첫 코어");
            var dup = r.StartSup();
            ctx.Check(dup.WaitForExit(20000) && dup.ExitCode == 0, "두 번째 감독(같은 mutex) → 즉시 exit 0 (1분 반복 트리거 IgnoreNew 대응)");
            ctx.Check(r.CoreProcs().Count == 1 && r.SupProcs().Count == 1, "여전히 감독 1·코어 1");
            ctx.Check(File.Exists(lg) && File.ReadAllText(lg) == File.ReadAllText(Path.Combine(r.HubDir, "config.json")), "검증 통과한 config.json → config.last-good.json 갱신");

            var u = r.Uart("hub", "stop", "--json");
            var uj = LifeRig.Parse(u.Out);
            ctx.Check(u.Code == 0 && J.B(uj, "stopped") == true, "uart hub stop → ok, stopped:true");
            ctx.WaitUntil(() => !LifeRig.Alive(c1), 10000, "코어 정상 종료");
            ctx.WaitUntil(() => S(r.Sup(), "state") == "stopped", 5000, "감독 state=stopped");
            ctx.Check(N(r.Sup(), "last_exit") == 0, "코어 exit 0");
            var ctlJ = HubFiles.ReadJson(ctlPath);
            ctx.Check(ctlJ.HasValue && HubFiles.Bool(ctlJ.Value, "stopped") && HubFiles.Str(ctlJ.Value, "by") == "core", "control.json stopped:true (by=core — CLI → 코어 → 감독 요청 스풀)");
            var hl = r.HubLines();
            int iCtl = hl.FindIndex(x => x.Src == "sup" && x.Msg.StartsWith("정지 요청(core) → control.json stopped=true", StringComparison.Ordinal));
            int iExit = hl.FindIndex(x => x.Src == "sup" && x.Msg.StartsWith("코어 종료 pid=" + c1 + " exit=0 ", StringComparison.Ordinal));
            int iCoreExit = hl.FindIndex(x => x.Src == "core" && x.Msg == "코어 정상 종료(exit 0)");
            ctx.Check(iCtl >= 0 && iExit > iCtl && iCoreExit > iCtl, "control.json stopped 를 먼저 쓰고(줄 " + iCtl + ") 그다음 코어 정상 종료(줄 " + iCoreExit + "·" + iExit + ")");
            Thread.Sleep((int)(r.Backoff[r.Backoff.Length - 1] * 1000) + 500);
            ctx.Check(r.CoreProcs().Count == 0, "exit 0 → 재시작 없음");

            LifeRig.Kill(sup1.Id);
            ctx.Check(r.SupProcs().Count == 0, "감독 kill");
            var sup2 = r.StartSup();
            ctx.WaitUntil(() => N(r.Sup(), "pid") == sup2.Id && S(r.Sup(), "state") == "stopped", 20000, "새 감독(반복 트리거 흉내) → state=stopped");
            Thread.Sleep(2500);
            ctx.Check(r.CoreProcs().Count == 0 && !r.CoreMutexHeld() && !File.Exists(Path.Combine(r.HubDir, "core.pid")),
                      "stopped 상태 감독 재기동: 2.5초 동안 코어 프로세스 0, 코어 mutex 미점유 → COM(FakeBoard) 미점유");
            ctx.Check(r.HubLogText().Contains("[sup] 정지 상태(control.json stopped) — 코어를 띄우지 않음(COM 미점유)"), "hub.log: 정지 상태라 코어를 띄우지 않음");
            u = r.Uart("ls", "--json");
            uj = LifeRig.Parse(u.Out);
            ctx.Check(u.Code == 103 && J.B(uj, "stopped") == true && (J.S(uj, "error") ?? "").Contains("hub start"), "정지 중 uart ls → 103 + stopped:true + 'hub start' 안내");
            u = r.Uart("ls");
            ctx.Check(u.Code == 103 && u.Err.StartsWith("uart: E103 hub_down 허브 정지됨", StringComparison.Ordinal), "비JSON: stderr 'uart: E103 hub_down 허브 정지됨…'");
            u = r.Uart("hub", "status", "--json");
            uj = LifeRig.Parse(u.Out);
            ctx.Check(u.Code == 0 && uj.TryGetProperty("supervisor", out var sv) && J.S(sv, "state") == "stopped" && J.B(sv, "stopped") == true && J.IsNull(sv, "core_pid"),
                      "코어 없이 uart hub status → 감독 직접(요청 스풀): state=stopped, stopped:true, core_pid:null");
            u = r.Uart("hub", "start", "--json");
            uj = LifeRig.Parse(u.Out);
            ctx.Check(u.Code == 0 && J.S(uj, "action") == "started" && J.B(uj, "stopped") == false, "uart hub start(감독 직접) → action=started, stopped:false");
            int c2 = r.WaitCore(c1, 30000, "hub start 뒤 코어");
            r.WaitAttached("hub start 뒤 코어");
            ctlJ = HubFiles.ReadJson(ctlPath);
            ctx.Check(ctlJ.HasValue && !HubFiles.Bool(ctlJ.Value, "stopped") && HubFiles.Str(ctlJ.Value, "by") == "cli", "control.json stopped:false (by=cli)");
            u = r.Uart("hub", "start", "--json");
            ctx.Check(u.Code == 0 && J.S(LifeRig.Parse(u.Out), "action") == "already_running", "실행 중 hub start(코어 경유) → already_running");

            // ---- B. 감독만 kill → 새 감독이 기존 코어 입양
            LifeRig.Kill(sup2.Id);
            Thread.Sleep(1500);
            ctx.Check(LifeRig.Alive(c2) && r.CoreProcs().Count == 1, "감독만 kill → 코어 pid=" + c2 + " 는 계속 실행(Job Object 없음)");
            string alivePath = Path.Combine(r.HubDir, "alive", r.Base.ToString(CultureInfo.InvariantCulture));
            var a1 = HubFiles.ReadShared(alivePath) ?? "";
            Thread.Sleep(1000);
            var a2 = HubFiles.ReadShared(alivePath) ?? "";
            ctx.Check(a1.Length > 0 && a1 != a2, "감독 없이도 코어 하트비트 갱신(" + a1.Trim() + " → " + a2.Trim() + ")");
            var sup3 = r.StartSup();
            ctx.WaitUntil(() => N(r.Sup(), "pid") == sup3.Id && B(r.Sup(), "adopted") && N(r.Sup(), "core_pid") == c2, 20000, "새 감독이 core.pid(PID·시작 시각 일치) 로 기존 코어 입양");
            for (int i = 0; i < 4; i++)
            {
                Thread.Sleep(750);
                int n = r.CoreProcs().Count;
                if (n != 1) ctx.Fail("입양 뒤 코어 프로세스 " + n + "개");
            }
            ctx.Log("ok: 입양 뒤 3초 동안 코어 프로세스 1개(새 코어 안 띄움)");
            ctx.Check(r.HubLogText().Contains("[sup] 기존 코어 입양 pid=" + c2 + " "), "hub.log: 기존 코어 입양 pid=" + c2);
            var st = r.Call("hub", "status");
            ctx.Check(st.TryGetProperty("supervisor", out var sv3) && J.N(sv3, "pid") == sup3.Id && J.B(sv3, "adopted") == true && J.N(st, "pid") == c2,
                      "hub status: supervisor.pid=새 감독, adopted:true, 코어 pid 그대로");
            LifeRig.Kill(c2);
            int c3 = r.WaitCore(c2, 20000, "입양한 코어 kill 뒤 재시작");
            ctx.Check(r.HubLogText().Contains("[sup] 코어 종료 pid=" + c2 + " exit=-1 ") && r.HubLogText().Contains("(입양)"), "입양한 코어의 종료(exit=-1)도 감지 → 재시작");
            r.WaitAttached("입양 뒤 재시작된 코어");

            // ---- C. 깨진 config → last-good
            string goodCfg = File.ReadAllText(Path.Combine(r.HubDir, "config.json"));
            r.WriteConfig("{ \"log_root\": \"C:\\\\nowhere\", \"base_port\": \"x\", \"bogus_key\": 1 }");
            ctx.Check(Config.ValidateFile(Path.Combine(r.HubDir, "config.json")).Count > 0, "config.json 을 깨뜨림(같은 파서로 검증 실패)");
            LifeRig.Kill(c3);
            int c4 = r.WaitCore(c3, 20000, "깨진 config 뒤 재시작");
            var cp = r.CorePid();
            ctx.Check(S(cp, "config") == lg, "코어가 config.last-good.json 으로 기동(core.pid config=" + S(cp, "config") + ")");
            r.WaitAttached("last-good 코어");
            ctx.Check(Regex.IsMatch(r.HubLogText(), @"\[sup\] config\.json 검증 실패: .* → config\.last-good\.json 으로 기동"), "hub.log 경고: config.json 검증 실패 → last-good 으로 기동");
            ctx.Check(File.ReadAllText(lg) == goodCfg, "깨진 config 로 last-good 을 덮어쓰지 않음");
            st = r.Call("hub", "status");
            ctx.Check(st.TryGetProperty("supervisor", out var sv4) && (J.S(sv4, "config_warning") ?? "").StartsWith("config.json 검증 실패", StringComparison.Ordinal),
                      "hub status supervisor.config_warning = " + J.S(sv4, "config_warning"));
            r.WriteConfig(goodCfg);

            // ---- D. 크래시 루프 백오프
            ctx.WaitUntil(() => N(r.Sup(), "backoff_step") == 0 && !B(r.Sup(), "crash_loop"), 15000, "코어가 backoff_reset_s(6초) 정상 운용 → 백오프 0단계");
            Directory.CreateDirectory(Path.Combine(r.HubDir, "sim"));
            File.WriteAllText(Path.Combine(r.HubDir, "sim", "crash_on_start"), "200");
            LifeRig.Kill(c4);
            ctx.WaitUntil(() => B(r.Sup(), "crash_loop"), 60000, "연속 크래시 → sup.json crash_loop:true");
            int StartsAfterKill(List<(DateTime T, string Src, string Msg)> ls, int from) => ls.Skip(from + 1).Count(x => x.Src == "sup" && x.Msg.StartsWith("코어 시작 pid=", StringComparison.Ordinal));
            int KillLine(List<(DateTime T, string Src, string Msg)> ls) => ls.FindIndex(x => x.Src == "sup" && x.Msg.StartsWith("코어 종료 pid=" + c4 + " ", StringComparison.Ordinal));
            ctx.WaitUntil(() => { var ls = r.HubLines(); int k = KillLine(ls); return k >= 0 && StartsAfterKill(ls, k) >= 3; }, 30000, "kill 뒤 재시작 3회");
            HubFiles.TryDelete(Path.Combine(r.HubDir, "sim", "crash_on_start"));
            var lines = r.HubLines();
            int kill = KillLine(lines);
            var delays = new List<double>();
            var exits = new List<string>();
            for (int i = kill; i >= 0 && i < lines.Count && delays.Count < 3; i++)
            {
                if (lines[i].Src != "sup" || !lines[i].Msg.StartsWith("코어 종료 pid=", StringComparison.Ordinal)) continue;
                int k = lines.FindIndex(i + 1, x => x.Src == "sup" && x.Msg.StartsWith("코어 시작 pid=", StringComparison.Ordinal));
                if (k < 0) break;
                delays.Add((lines[k].T - lines[i].T).TotalSeconds);
                exits.Add(Regex.Match(lines[i].Msg, @"exit=(\S+)").Groups[1].Value);
            }
            ctx.Log("재시작 간격: " + string.Join(", ", delays.Select(d => d.ToString("0.00", CultureInfo.InvariantCulture))) + " / exit: " + string.Join(",", exits));
            ctx.Check(delays.Count == 3 && exits[0] == "-1" && exits.Skip(1).All(e => e == "70"), "kill(exit -1) 뒤 crash_on_start 코어들이 exit 70 으로 연속 사망");
            for (int i = 0; i < 3; i++)
            {
                double want = r.Backoff[Math.Min(i, r.Backoff.Length - 1)];
                ctx.Check(delays[i] >= want - 0.05 && delays[i] < want + 1.0, "재시작 " + (i + 1) + " 간격 " + delays[i].ToString("0.00", CultureInfo.InvariantCulture) + "초 ≈ 백오프 " + want + "초(지수)");
            }
            ctx.Check(r.HubLogText().Contains("[sup] 크래시 루프 경고: 코어가 3회 연속 기동 뒤 60초 안에 죽음"), "hub.log: 크래시 루프 경고(3회 연속 1분 안 사망)");
            // crash_on_start 를 지운 뒤: 같은 코어가 1.5초 넘게 살아 있으면 회복(지우기 전에 파일을 읽은 코어는 한 번 더 죽을 수 있다)
            int seen = 0;
            var seenAt = DateTime.MinValue;
            ctx.WaitUntil(() =>
            {
                var j = r.CorePid();
                if (!j.HasValue) return false;
                int p = (int)HubFiles.Long(j.Value, "pid");
                if (HubFiles.Str(j.Value, "control") != "listening" || !HubFiles.IsAlive(p, HubFiles.Long(j.Value, "start_ticks"))) return false;
                if (p != seen) { seen = p; seenAt = DateTime.Now; return false; }
                return (DateTime.Now - seenAt).TotalSeconds > 1.5;
            }, 40000, "crash_on_start 제거 뒤 코어가 살아남음");
            ctx.WaitUntil(() => !B(r.Sup(), "crash_loop") && N(r.Sup(), "backoff_step") == 0, 15000, "backoff_reset_s 정상 운용 → 크래시 루프·백오프 초기화");
            ctx.Check(r.HubLogText().Contains("정상 운용 → 백오프·크래시 루프 초기화"), "hub.log: 백오프·크래시 루프 초기화");
            ctx.Check(r.CoreProcs().Count == 1 && r.SupProcs().Count == 1, "감독 1·코어 1");

            // ---- E. 감독·코어가 함께 사라짐(PC 재부팅·로그오프 흉내) → 새 감독이 core.pid 잔재로 비정상 종료를 알아채고 restart(exit=unknown) + gap
            int cLast = (int)N(r.Sup(), "core_pid");
            foreach (int p in r.SupProcs()) LifeRig.Kill(p);
            LifeRig.Kill(cLast);
            ctx.Check(File.Exists(Path.Combine(r.HubDir, "core.pid")) && r.CoreProcs().Count == 0, "감독·코어 함께 kill → core.pid 잔재(정상 종료 흔적 없음)");
            r.StartSup();
            r.WaitCore(cLast, 30000, "새 감독이 띄운 코어");
            r.WaitAttached("새 감독의 코어");
            ctx.Check(r.HubLogText().Contains(" reason=exit=unknown"), "hub.log: 코어 시작 reason=exit=unknown");
            ctx.WaitUntil(() => r.CableLogs().Contains("*** hub restart (exit=unknown) ***"), 5000, "케이블 로그 restart (exit=unknown)");
            r.RestartGap("exit=unknown");
            r.CheckNoLeftovers();
        }

        // ================================================================== hang
        [Scenario("hang", "hang 감지: 코어 DiskWriter 정지(alive 하트비트 멈춤, ping 은 응답) → hang_s(축소 4초) 뒤 감독이 kill 후 재시작(*** hub restart (exit=-1 hang=heartbeat) *** + gap). ControlServer 정지(ping 실패, 하트비트 계속) → 같은 방식(hang=ping). 잔존 pwsh 0",
                  Order = 820, TimeoutS = 180)]
        public static void SHang(ScenarioContext ctx)
        {
            var r = LifeRig.Create(ctx, "hang", 17302, 17370, "SIMCABHG", "COM340", "SIMHG");
            r.StartSup();
            int c1 = r.WaitCore(0, 30000, "첫 코어");
            r.WaitAttached("첫 코어");
            Thread.Sleep(1500);
            ctx.Check(LifeRig.Alive(c1), "hang_s(4초)보다 오래 정상 운용해도 kill 안 함(하트비트·ping 정상)");

            // ---- 하트비트 정지
            var t0 = DateTime.Now;
            r.Inject(c1, "pause-writers");
            var st = r.Call("hub", "status");
            ctx.Check(J.S(st, "status") == "ok", "writer 정지 중에도 ControlServer ping 은 응답");
            int c2 = r.WaitCore(c1, 30000, "하트비트 정지 뒤 재시작");
            var hl = r.HubLines();
            var det = hl.FirstOrDefault(x => x.Src == "sup" && x.Msg.StartsWith("hang 감지(하트비트 alive\\" + r.Base + " ", StringComparison.Ordinal));
            ctx.Check(det.Msg != null, "hub.log: hang 감지(하트비트 alive\\" + r.Base + " …) → kill");
            double after = (det.T - t0).TotalSeconds;
            ctx.Check(after >= r.HangS - 0.6 && after < r.HangS + 2.0, "주입 뒤 " + after.ToString("0.00", CultureInfo.InvariantCulture) + "초에 감지(hang_s " + r.HangS + "초, 마지막 하트비트는 주입 전 ≤0.5초)");
            ctx.Check(r.HubLogText().Contains("[sup] 코어 종료 pid=" + c1 + " exit=-1 ") && r.HubLogText().Contains(" hang=heartbeat"), "hub.log: 코어 종료 exit=-1 hang=heartbeat");
            r.WaitAttached("하트비트 hang 뒤 코어");
            ctx.WaitUntil(() => r.CableLogs().Contains("*** hub restart (exit=-1 hang=heartbeat) ***"), 5000, "케이블 로그 restart 줄");
            double g = r.RestartGap("exit=-1 hang=heartbeat");
            ctx.Check(g >= r.HangS, "gap " + g + "초 ≥ hang_s " + r.HangS + "초(멈춘 하트비트부터)");

            // ---- ControlServer 정지(ping 실패)
            Thread.Sleep(1000);
            var t1 = DateTime.Now;
            r.Inject(c2, "stop-control");
            int c3 = r.WaitCore(c2, 30000, "ping 실패 뒤 재시작");
            hl = r.HubLines();
            var det2 = hl.FirstOrDefault(x => x.Src == "sup" && x.Msg.StartsWith("hang 감지(ping 응답 없음 ", StringComparison.Ordinal));
            ctx.Check(det2.Msg != null, "hub.log: hang 감지(ping 응답 없음 …) → kill");
            double after2 = (det2.T - t1).TotalSeconds;
            ctx.Check(after2 >= r.HangS - 0.6 && after2 < r.HangS + 2.5, "주입 뒤 " + after2.ToString("0.00", CultureInfo.InvariantCulture) + "초에 감지(hang_s " + r.HangS + "초, 마지막 ping 성공은 주입 전 ≤0.5초)");
            r.WaitAttached("ping hang 뒤 코어");
            ctx.WaitUntil(() => r.CableLogs().Contains("*** hub restart (exit=-1 hang=ping) ***"), 5000, "케이블 로그 restart (hang=ping)");
            r.RestartGap("exit=-1 hang=ping");
            var sup = r.Sup();
            ctx.Check(N(sup, "restarts") == 2 && N(sup, "core_pid") == c3, "sup.json restarts=2, core_pid=" + c3);
            ctx.Check(r.CoreProcs().Count == 1, "코어 프로세스 1개");
            r.CheckNoLeftovers();
        }
    }
}
