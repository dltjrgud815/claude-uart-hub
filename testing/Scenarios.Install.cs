// US-105(계획 T2 첫 설치·제거·dry-run, C-13) 설치 계열 시나리오. 모두 실제 UartHub.exe 를 UART_HUB_TEST_ROOT 격리 아래에서 띄운다.
//   시험 루트 = %TEMP%\uart_hub_test\<run>\<id>\<case>\ : LocalAppData·AppData(시작프로그램·시작 메뉴)·Desktop·UserProfile·Temp, fake_*.json, seed_config.json(17xxx 포트),
//   sim_spec.json + sim_dll.txt(코어 안 FakeBoard — 실제 COM·FTDI 열거 안 함). 확인 창은 시험 모드 자동 응답("[ui] 확인 창" 줄)이라 화면에 창이 뜨지 않는다.
//   포트: install-isolated 17700/17710, install-first-fail 17720/17730, uninstall-isolated 17740/17750, logroot-policy 17790~(dry-run 이라 listen 없음).
// 각 시나리오 끝: "실제 시스템 무변경" — 실제 Startup·바탕화면 *.lnk·%USERPROFILE%\.claude\skills·시작 메뉴 'UART 허브'·%LOCALAPPDATA%\uart_hub·
//   schtasks /query /tn uart_hub /xml 결과가 시작 전과 같고, exe 출력·남은 로그에 실제 외부 호출([ext] wsl.exe·schtasks.exe·WMI) 0, TestIsolationViolation 0.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace UartHub.Testing
{
    internal sealed class InstallRig
    {
        public ScenarioContext Ctx;
        public string Root, Cable, Com, Host;
        public int Ctl, Base;
        public readonly List<string> Outputs = new List<string>();

        string P(params string[] s) => Path.Combine(new[] { Root }.Concat(s).ToArray());
        public string InstallDir => P("LocalAppData", "uart_hub");
        public string InstalledExe => Path.Combine(InstallDir, "UartHub.exe");
        public string InstallJson => Path.Combine(InstallDir, "install.json");
        public string StartMenu => P("AppData", "Microsoft", "Windows", "Start Menu", "Programs");
        public string StartupLnk => Path.Combine(StartMenu, "Startup", "UART 허브.lnk");
        public string MenuDir => Path.Combine(StartMenu, "UART 허브");
        public string Desktop => P("Desktop");
        public string UserProfile => P("UserProfile");
        public string Temp => P("Temp");

        public static InstallRig Create(ScenarioContext ctx, string sub, int ctl, int basePort, bool sim = true)
        {
            var r = new InstallRig
            {
                Ctx = ctx, Root = Path.Combine(ctx.Dir, sub), Ctl = ctl, Base = basePort,
                Cable = "SIMINS" + ctl.ToString(CultureInfo.InvariantCulture), Com = "COM" + (ctl % 1000).ToString(CultureInfo.InvariantCulture),
                Host = "SIMI" + (ctl % 1000).ToString(CultureInfo.InvariantCulture),
            };
            foreach (var d in new[] { r.P("LocalAppData"), r.P("AppData"), r.Desktop, r.UserProfile, r.Temp }) Directory.CreateDirectory(d);
            r.Write("seed_config.json", r.SeedConfig());
            if (sim)
            {
                r.Write("sim_spec.json", "{\"boards\":[{\"cable\":" + Json.Str(r.Cable) + ",\"com\":" + Json.Str(r.Com) + ",\"hostname\":" + Json.Str(r.Host) + ",\"printk_ms\":300}]}");
                r.Write("sim_dll.txt", TestEnv.TestsDll);
            }
            ctx.Defer(r.KillAll);
            ctx.Log("시험 루트 " + r.Root + " (mutex " + SystemAccess.MutexNameFor(r.Root, "*") + ", 제어 " + ctl + "·케이블 " + basePort + ")");
            return r;
        }

        public string SeedConfig()
        {
            return "{\n  \"base_port\": " + Base + ", \"control_port\": " + Ctl + ", \"vids\": [],\n" +
                   "  \"scan_interval_ms\": 50, \"read_timeout_ms\": 50, \"writer_wait_ms\": 20, \"ring_mb\": 1, \"queue_mb\": 16,\n" +
                   "  \"heartbeat_s\": 0.5, \"hang_s\": 8, \"ping_interval_s\": 0.5, \"backoff_s\": [1, 2, 4], \"backoff_reset_s\": 30,\n" +
                   "  \"crash_loop_count\": 3, \"crash_loop_window_s\": 60, \"idle_s\": 0.3, \"quiet_ms\": 600, \"default_timeout_s\": 10, \"sup_watch_s\": 2\n}\n";
        }

        public void Write(string rel, string text)
        {
            string p = P(rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, text, new UTF8Encoding(false));
        }

        public void FakeWsl(string names) { Write(Path.Combine("fake_wsl", "l_q.txt"), names); }

        public void FakeTask(string name, string command, string arguments)
        {
            Write(Path.Combine("fake_tasks", name + ".xml"),
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n<Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n" +
                "  <Actions Context=\"Author\">\n    <Exec>\n      <Command>" + command + "</Command>\n      <Arguments>" + arguments + "</Arguments>\n    </Exec>\n  </Actions>\n</Task>\n");
        }

        public string HubDirFrom(JsonElement info) => HubFiles.Str(info, "hub_dir");

        // ------------------------------------------------------------------ 실행
        /// <summary>exe 를 실행(UART_HUB_TEST_ROOT = 이 루트, UART_HUB_DIR 없음). (종료코드, stdout, stderr, ms)</summary>
        public (int Code, string Out, string Err, long Ms) Run(string exe, string[] args, Dictionary<string, string> env = null, int timeoutMs = 120000)
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = Path.GetDirectoryName(exe),
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            foreach (var k in new[] { "UART_HUB_DIR", "UART_HUB_CONTROL_PORT", "UART_HUB_TEST_FAIL", "UART_HUB_TEST_ANSWER", "UART_HUB_TEST_C14", "UART_HUB_TEST_HEALTH_S" })
                psi.Environment.Remove(k);
            psi.Environment["UART_HUB_TEST_ROOT"] = Root;
            if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
            var sw = Stopwatch.StartNew();
            using var p = Process.Start(psi);
            p.StandardInput.Close();
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch (Exception) { } Ctx.Fail("시간 초과: " + Path.GetFileName(exe) + " " + string.Join(" ", args)); }
            p.WaitForExit();
            if (!so.Wait(15000)) Ctx.Fail("표준 출력이 닫히지 않음(띄운 감독이 파이프를 물고 있음?): " + string.Join(" ", args));
            se.Wait(5000);
            var res = (p.ExitCode, so.Result, se.IsCompleted ? se.Result : "", sw.ElapsedMilliseconds);
            lock (Outputs) Outputs.Add(res.Item2 + "\n" + res.Item3);
            Ctx.Log(Path.GetFileName(Path.GetDirectoryName(exe)) + "\\" + Path.GetFileName(exe) + " " + string.Join(" ", args) + " → rc=" + res.ExitCode + " (" + res.Item4 + "ms)");
            return res;
        }

        public (int Code, string Out, string Err, long Ms) Run(string exe, params string[] args) => Run(exe, args, null);

        // ------------------------------------------------------------------ 프로세스(명령줄에 이 루트가 들어간 UartHub.exe)
        public List<(int Pid, string Cmd)> Procs()
        {
            var l = new List<(int, string)>();
            using var s = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'UartHub.exe'");
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    string cmd = o["CommandLine"] as string;
                    if (cmd != null && cmd.IndexOf(Root, StringComparison.OrdinalIgnoreCase) >= 0) l.Add((Convert.ToInt32(o["ProcessId"], CultureInfo.InvariantCulture), cmd));
                }
            return l;
        }

        public void KillAll()
        {
            for (int round = 0; round < 4; round++)
            {
                var ps = Procs();
                if (ps.Count == 0) return;
                foreach (var p in ps.OrderBy(x => x.Cmd.Contains(" core ") ? 1 : 0)) LifeRig.Kill(p.Pid);   // 감독 먼저(코어를 다시 띄우지 못하게)
                Thread.Sleep(300);
            }
        }

        public void CheckNoProcs(string what)
        {
            KillAll();
            var ps = Procs();
            Ctx.Check(ps.Count == 0, what + ": 이 시험 루트의 UartHub.exe 잔존 0 (" + ps.Count + ")");
        }

        // ------------------------------------------------------------------ 파일
        /// <summary>폴더 아래 파일·폴더 목록(상대경로 → 크기|수정시각). 없으면 빈 목록.</summary>
        public static SortedDictionary<string, string> Snapshot(params string[] dirs)
        {
            var d = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in dirs)
            {
                if (!Directory.Exists(dir)) { d[dir + "|"] = "없음"; continue; }
                foreach (var e in Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories))
                {
                    var fi = new FileInfo(e);
                    d[e] = Directory.Exists(e) ? "dir" : fi.Length.ToString(CultureInfo.InvariantCulture) + "|" + fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
                }
            }
            return d;
        }

        public static string Diff(SortedDictionary<string, string> a, SortedDictionary<string, string> b)
        {
            var l = new List<string>();
            foreach (var k in a.Keys.Union(b.Keys, StringComparer.OrdinalIgnoreCase))
            {
                a.TryGetValue(k, out var x); b.TryGetValue(k, out var y);
                if (x != y) l.Add(k + ": " + (x ?? "(없음)") + " → " + (y ?? "(없음)"));
            }
            return l.Count == 0 ? "" : string.Join(" ; ", l.Take(6)) + (l.Count > 6 ? " … 외 " + (l.Count - 6) : "");
        }

        public JsonElement? InstallInfoJson() => HubFiles.ReadJson(InstallJson);

        public static int Count(string text, string what) => Regex.Matches(text ?? "", Regex.Escape(what)).Count;

        public string AllText()
        {
            var sb = new StringBuilder();
            lock (Outputs) foreach (var o in Outputs) sb.Append(o).Append('\n');
            if (Directory.Exists(Root))
                foreach (var f in Directory.EnumerateFiles(Root, "*.log", SearchOption.AllDirectories))
                    if (Regex.IsMatch(Path.GetFileName(f), @"^(install|install_failed_.*|uninstall_.*)\.log$")) sb.Append(HubFiles.ReadShared(f)).Append('\n');
            return sb.ToString();
        }

        public bool AdsExists(string path, string stream)
        {
            try { using var fs = new FileStream(path + ":" + stream, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); return true; }
            catch (Exception) { return false; }
        }
    }

    /// <summary>실제 시스템(시험 루트 밖) 기준 상태 — 시나리오 시작 때 뜨고 끝에 같은지 본다(C-13).</summary>
    internal sealed class RealSystem
    {
        SortedDictionary<string, string> before;

        static string Sf(Environment.SpecialFolder f) => Environment.GetFolderPath(f, Environment.SpecialFolderOption.DoNotVerify);

        public static RealSystem Capture(ScenarioContext ctx)
        {
            var r = new RealSystem { before = Take() };
            ctx.Log("실제 시스템 기준: " + string.Join(", ", r.before.Where(k => k.Key.StartsWith("#", StringComparison.Ordinal)).Select(k => k.Key + "=" + k.Value)));
            return r;
        }

        static SortedDictionary<string, string> Take()
        {
            var d = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void Add(string tag, string dir, string pattern, bool rec)
            {
                if (!Directory.Exists(dir)) { d["#" + tag] = "없음"; return; }
                var files = Directory.GetFiles(dir, pattern, rec ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
                d["#" + tag] = files.Length + "개";
                foreach (var f in files) { var fi = new FileInfo(f); d[tag + ":" + f] = fi.Length + "|" + fi.LastWriteTimeUtc.Ticks; }
            }
            Add("startup", Sf(Environment.SpecialFolder.Startup), "*", false);
            Add("desktop_lnk", Sf(Environment.SpecialFolder.DesktopDirectory), "*.lnk", false);
            Add("claude_skills", Path.Combine(Sf(Environment.SpecialFolder.UserProfile), ".claude", "skills"), "*", true);
            Add("startmenu_uart_hub", Path.Combine(Sf(Environment.SpecialFolder.Programs), "UART 허브"), "*", true);
            d["#localappdata_uart_hub"] = Directory.Exists(Path.Combine(Sf(Environment.SpecialFolder.LocalApplicationData), "uart_hub")) ? "있음" : "없음";
            d["#schtasks_uart_hub"] = Schtasks();
            return d;
        }

        /// <summary>schtasks /query /tn uart_hub /xml (정의만 — 다음 실행 시각 같은 변동값 없음): rc + 출력 SHA-256.</summary>
        static string Schtasks()
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
            foreach (var a in new[] { "/query", "/tn", "uart_hub", "/xml" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            p.StandardInput.Close();
            var ms = new MemoryStream();
            var t = p.StandardOutput.BaseStream.CopyToAsync(ms);
            p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            t.Wait(5000);
            return "rc=" + p.ExitCode + " sha=" + Convert.ToHexString(SHA256.HashData(ms.ToArray())).Substring(0, 16).ToLowerInvariant();
        }

        /// <summary>끝: 실제 시스템 같음 + 실제 외부 호출 0 + TestIsolationViolation 0.</summary>
        public void Verify(ScenarioContext ctx, params InstallRig[] rigs)
        {
            var after = Take();
            string diff = InstallRig.Diff(before, after);
            ctx.Check(diff == "", "실제 시스템 무변경(Startup·바탕화면 *.lnk·.claude\\skills·시작 메뉴 UART 허브·%LOCALAPPDATA%\\uart_hub·schtasks uart_hub)" + (diff == "" ? "" : ": " + diff));
            string all = string.Concat(rigs.Select(r => r.AllText()));
            int realExt = Regex.Matches(all, @"\[ext\] (wsl\.exe|schtasks\.exe|WMI)").Count;
            int fakeExt = Regex.Matches(all, @"\[ext:fake\] (wsl\.exe|schtasks\.exe)").Count;
            ctx.Check(realExt == 0, "실제 wsl.exe·schtasks.exe·WMI 호출 0 (가짜 호출 " + fakeExt + "회)");
            int viol = Regex.Matches(all, "TestIsolationViolation").Count;
            foreach (var r in rigs)
            {
                string vf = Path.Combine(r.Root, "isolation_violations.log");
                if (File.Exists(vf)) viol += File.ReadAllLines(vf).Count(l => l.Trim().Length > 0);
            }
            ctx.Check(viol == 0, "TestIsolationViolation 0");
        }
    }

    public static class InstallScenarios
    {
        static readonly string[] InstallSteps = { "== 0.", "== 1. 사전 점검", "== 2. 확인 창", "== 3.", "== 4.", "== 5.", "== 6.", "== 7.", "== 8." };
        static readonly string[] UninstallSteps = { "== 1.", "== 2. 확인 창", "== 3.", "== 4.", "== 5.", "== 6.", "== 7.", "== 8.", "== 9.", "== 10." };

        static string Dev => TestEnv.UartHubExe;

        static Match LogRootLine(string o) => Regex.Match(o, @"^  로그 위치: (.+) \((.+)\)\s*$", RegexOptions.Multiline);

        // ================================================================== logroot-policy
        [Scenario("logroot-policy", "로그 위치(§2.3 j): install --dry-run(C-13 가짜 드라이브)으로 D: 고정 → D:\\uart_logs, D: 없음·이동식·네트워크·준비 안 됨 → %USERPROFILE%\\uart_logs, 이전 허브 폴더(token) 재사용(사용자 폴더·D:), 기존 config.json 은 건드리지 않음(새 케이블 질문 꺼짐 표시). 매번 시험 루트 무변경, 실제 시스템 무변경. + C.12 단위: log_layout 3 경로·임시 기록 보관 정리·세션 이름 다듬기·트레이 정보 줄·아이콘 우선순위·터미널 제목 ' · 에이징 중'·요약 글",
                  Order = 900, TimeoutS = 180)]
        public static void LogrootPolicy(ScenarioContext ctx)
        {
            var real = RealSystem.Capture(ctx);
            AgingScenarios.Units(ctx);                 // 부록 C.12 단위(시험 폴더 안에서만): log_layout 3 경로·임시 기록 정리·이름·글·아이콘·제목·요약
            const string fixedD = "{\"D\":{\"type\":\"Fixed\",\"ready\":true}}";
            var cases = new (string Id, string Drives, string PrevTok, string Expect, string Why)[]
            {
                ("d-fixed", fixedD, null, "drive_D", "D: 가 로컬 고정 디스크"),
                ("d-none", null, null, "UserProfile", "D: 없음"),
                ("d-removable", "{\"D\":{\"type\":\"Removable\",\"ready\":true}}", null, "UserProfile", "D: 이동식"),
                ("d-network", "{\"D\":{\"type\":\"Network\",\"ready\":true}}", null, "UserProfile", "D: 네트워크"),
                ("d-notready", "{\"D\":{\"type\":\"Fixed\",\"ready\":false}}", null, "UserProfile", "D: 준비 안 됨"),
                ("reuse-user", fixedD, "UserProfile", "UserProfile", "이전 허브 폴더 재사용(%USERPROFILE%"),
                ("reuse-d", "{\"D\":{\"type\":\"Removable\",\"ready\":true}}", "drive_D", "drive_D", "이전 허브 폴더 재사용(D:"),
            };
            var rigs = new List<InstallRig>();
            int port = 17790;
            foreach (var c in cases)
            {
                var r = InstallRig.Create(ctx, c.Id, port, port + 1, sim: false);
                port += 2;
                rigs.Add(r);
                if (c.Drives != null) r.Write("fake_drives.json", c.Drives);
                if (c.PrevTok != null)
                {
                    r.Write(Path.Combine(c.PrevTok, "uart_logs", "hub", "token"), "0123456789abcdef0123456789abcdef");
                    r.Write(Path.Combine(c.PrevTok, "uart_logs", "hub", "config.json"), "{\"ask_new_cables\": false}\n");
                }
                var snap = InstallRig.Snapshot(r.Root);
                var o = r.Run(Dev, "install", "--dry-run");
                var m = LogRootLine(o.Out);
                string expect = Path.Combine(r.Root, c.Expect, "uart_logs");
                ctx.Check(o.Code == 0 && m.Success && string.Equals(m.Groups[1].Value, expect, StringComparison.OrdinalIgnoreCase) && m.Groups[2].Value.StartsWith(c.Why, StringComparison.Ordinal),
                          c.Id + ": 로그 위치 = " + c.Expect + "\\uart_logs (" + c.Why + "…) — 출력 '" + (m.Success ? m.Groups[1].Value.Replace(r.Root, "<root>") + " (" + m.Groups[2].Value + ")" : "(줄 없음) rc=" + o.Code) + "'");
                ctx.Check(o.Out.Contains("  허브 폴더: " + Path.Combine(expect, "hub")), c.Id + ": 허브 폴더 = 로그 위치\\hub");
                if (c.PrevTok != null)
                    ctx.Check(o.Out.Contains("config.json: 있음 · 새 케이블 질문: 꺼짐(기존 config.json)") && o.Out.Contains("config.json 이미 있음 — 건드리지 않음"),
                              c.Id + ": 기존 config.json 은 그대로(새 케이블 질문: 꺼짐 표시)");
                else ctx.Check(o.Out.Contains("config.json: 없음 → 새로 만듦"), c.Id + ": config.json 없음 → 새로 만듦(시험: seed_config.json)");
                string d = InstallRig.Diff(snap, InstallRig.Snapshot(r.Root));
                ctx.Check(d == "", c.Id + ": dry-run 뒤 시험 루트 무변경" + (d == "" ? "" : ": " + d));
            }
            real.Verify(ctx, rigs.ToArray());
        }

        // ================================================================== install-isolated
        [Scenario("install-isolated", "첫 설치(C-13 격리, 실제 exe): 관리자 → 중단(exit 3)·PS 판 프로세스(가짜 목록) → PID·정지 방법·중단·중복 실행(installer mutex) → exit 0 · 셋 모두 무변경, install --dry-run 이 0~8 단계 출력·무변경. 실제 설치(MotW 붙은 받은 exe): 확인 창 1회(자동 응답), install.json installing→installed, 건강 확인 ≤10초, 바로가기 3개·토큰 ACL·config(seed)·prefs·이전 PS 작업 삭제·MotW 없음, 운영 모드 감독·코어 + C-14 Repair 0회, ls 에 FakeBoard. 실제 시스템 무변경",
                  Order = 910, TimeoutS = 240)]
        public static void InstallIsolated(ScenarioContext ctx)
        {
            var real = RealSystem.Capture(ctx);
            var r = InstallRig.Create(ctx, "root", 17700, 17710);
            r.Write("fake_drives.json", "{\"D\":{\"type\":\"Removable\",\"ready\":true}}");
            r.FakeWsl("Ubuntu-22.04\ndocker-desktop\ndocker-desktop-data\nUbuntu-24.04\n");
            r.FakeTask("uart_hub", "C:\\Program Files\\PowerShell\\7\\pwsh.exe", "-NoProfile -File \"C:\\Users\\x\\AppData\\Local\\uart_hub\\uart_hub.ps1\"");
            Directory.CreateDirectory(Path.Combine(r.UserProfile, ".claude"));

            // ① 관리자 → 중단
            r.Write("fake_elevated", "");
            var snap = InstallRig.Snapshot(r.Root);
            var a = r.Run(Dev, "install");
            ctx.Check(a.Code == 3 && a.Out.Contains("elevated=true") && a.Out.Contains("중단: 관리자 권한으로 실행됨"), "관리자 권한 → 설치 중단(exit 3, '관리자 권한으로 실행됨')");
            File.Delete(Path.Combine(r.Root, "fake_elevated"));
            snap.Remove(Path.Combine(r.Root, "fake_elevated"));
            string d0 = InstallRig.Diff(snap, InstallRig.Snapshot(r.Root));
            ctx.Check(d0 == "" && !Directory.Exists(r.InstallDir), "관리자 중단: 시험 루트 무변경·설치 폴더 없음" + (d0 == "" ? "" : ": " + d0));
            // UAC 가 꺼진 PC(EnableLUA=0 — 사용자 PC 실측)는 모든 프로세스가 관리자 토큰이라 막지 않고 경고만(dry-run 으로 판정만 확인)
            r.Write("fake_elevated", "uac_off");
            var au = r.Run(Dev, "install", "--dry-run");
            ctx.Check(au.Code == 0 && au.Out.Contains("elevated=true(uac_off)") && au.Out.Contains("경고: 이 PC 는 UAC 가 꺼져") && !au.Out.Contains("중단: 관리자 권한"),
                      "UAC 꺼진 관리자 계정(EnableLUA=0) → 중단하지 않고 경고만(dry-run exit 0)");
            File.Delete(Path.Combine(r.Root, "fake_elevated"));

            // ② PS 판·구 브리지 프로세스(가짜 목록) → PID·정지 방법·중단
            var fake = new (int Pid, string Name, string Cmd)[]
            {
                (4242, "pwsh.exe", "\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -NoProfile -File C:\\Users\\x\\AppData\\Local\\Temp\\uart_hub_trial\\uart_hub.ps1 -NoTray"),
                (4243, "pwsh.exe", "pwsh.exe -NoProfile -File C:\\x\\hub_core.ps1 -Version 1"),
                (4244, "powershell.exe", "powershell.exe -File C:\\x\\uart_bridge.ps1"),
                (4245, "pwsh.exe", "pwsh.exe -File C:\\x\\other.ps1"),
            };
            r.Write("fake_procs.json", "[" + string.Join(",", fake.Select(f => "{\"pid\":" + f.Pid + ",\"name\":" + Json.Str(f.Name) + ",\"cmd\":" + Json.Str(f.Cmd) + "}")) + "]");
            snap = InstallRig.Snapshot(r.Root);
            var b = r.Run(Dev, "install");
            ctx.Check(b.Code == 3 && b.Out.Contains("pid 4242") && b.Out.Contains("pid 4243") && b.Out.Contains("pid 4244") && !b.Out.Contains("pid 4245"),
                      "PS 판 감독·코어·구 브리지 프로세스 → PID 4242·4243·4244 표시, 무관한 pwsh(4245)는 표시 안 함, 중단(exit 3)");
            ctx.Check(Regex.Matches(b.Out, @"^  중단: PowerShell 판 프로세스 실행 중: pid \d+ .*정지 방법:", RegexOptions.Multiline).Count == 3 && b.Out.Contains("감독 끝내기") && b.Out.Contains("hub stop") && b.Out.Contains("bridge.sh stop") && b.Out.Contains("자동으로 끄지 않습니다"),
                      "PS 판 중단: 프로세스마다 정지 방법(감독 끝내기·hub stop·bridge.sh stop), 자동 kill 안 함");
            File.Delete(Path.Combine(r.Root, "fake_procs.json"));
            snap.Remove(Path.Combine(r.Root, "fake_procs.json"));
            string d1 = InstallRig.Diff(snap, InstallRig.Snapshot(r.Root));
            ctx.Check(d1 == "" && !Directory.Exists(r.InstallDir), "PS 판 중단: 시험 루트 무변경" + (d1 == "" ? "" : ": " + d1));

            // ③ 중복 실행(installer mutex 가 이미 있음) → exit 0, 아무것도 안 함
            snap = InstallRig.Snapshot(r.Root);
            using (var m = new Mutex(false, SystemAccess.MutexNameFor(r.Root, "installer")))
            {
                bool got = m.WaitOne(0);
                var c = r.Run(Dev, "install");
                ctx.Check(got && c.Code == 0 && c.Out.Contains("다른 설치기가 이미 실행 중") && c.Ms < 10000, "중복 실행(installer mutex 있음) → 바로 exit 0 (" + c.Ms + "ms)");
                if (got) m.ReleaseMutex();
            }
            string d2 = InstallRig.Diff(snap, InstallRig.Snapshot(r.Root));
            ctx.Check(d2 == "", "중복 실행: 시험 루트 무변경" + (d2 == "" ? "" : ": " + d2));

            // ④ dry-run: 모든 단계 출력, 무변경
            snap = InstallRig.Snapshot(r.Root);
            var dr = r.Run(Dev, "install", "--dry-run");
            var missing = InstallSteps.Where(s => !dr.Out.Contains(s)).ToList();
            ctx.Check(dr.Code == 0 && missing.Count == 0 && dr.Out.Contains("# dry-run 끝 — 시스템을 바꾸지 않았습니다."),
                      "install --dry-run: 단계 0~8 모두 출력·exit 0" + (missing.Count > 0 ? " — 빠짐: " + string.Join(",", missing) : ""));
            ctx.Check(InstallRig.Count(dr.Out, "[dry-run] ") >= 15 && !dr.Out.Contains("[ui] 확인 창") && dr.Out.Contains("WSL 배포판: Ubuntu-22.04, Ubuntu-24.04")
                      && dr.Out.Contains("이전 PS 작업 \\uart_hub: 있음(동작 = uart_hub.ps1) → 삭제"),
                      "dry-run: 동작 줄은 모두 [dry-run](" + InstallRig.Count(dr.Out, "[dry-run] ") + "줄), 확인 창 없음, 배포판 이름만(docker-desktop* 제외), 이전 PS 작업 삭제 예정 표시");
            string d3 = InstallRig.Diff(snap, InstallRig.Snapshot(r.Root));
            ctx.Check(d3 == "", "dry-run: 시험 루트 무변경(설치 폴더·바로가기·토큰·작업·mutex 없음)" + (d3 == "" ? "" : ": " + d3));

            // ⑤ 실제 설치: MotW 가 붙은 "받은 exe"에서
            string recv = Path.Combine(ctx.Dir, "recv", "UartHub.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(recv));
            File.Copy(Dev, recv, true);
            File.WriteAllText(recv + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
            bool srcMotw = r.AdsExists(recv, "Zone.Identifier");
            var states = new List<string>();
            bool polling = true;
            var poller = new Thread(() =>
            {
                string last = null;
                while (Volatile.Read(ref polling))
                {
                    var j = r.InstallInfoJson();
                    string s = j.HasValue ? HubFiles.Str(j.Value, "state") : null;
                    if (s != null && s != last) { lock (states) states.Add(s); last = s; }
                    Thread.Sleep(10);
                }
            }) { IsBackground = true };
            poller.Start();
            var e = r.Run(recv, "install");
            Thread.Sleep(100);
            Volatile.Write(ref polling, false);
            poller.Join(2000);
            ctx.Check(e.Code == 0 && e.Out.Contains("# 설치 완료"), "설치 exit 0 (" + e.Ms + "ms)" + (e.Code != 0 ? " — 출력 꼬리: " + Tail(e.Out) : ""));
            ctx.Check(InstallRig.Count(e.Out, "[ui] 확인 창(자동 응답=설치)") == 1 && InstallRig.Count(e.Out, "[ui] 확인 창") == 1, "확인 창 1회(시험 모드 자동 응답 — 화면에 창 없음)");
            ctx.Check(e.Out.Contains("[ui] 완료 창(5초 뒤 닫힘)"), "진행 창 완료 표시(5초 뒤 저절로 닫힘)");
            List<string> seen; lock (states) seen = states.ToList();
            ctx.Check(seen.SequenceEqual(new[] { "installing", "installed" }), "install.json state 관측 순서 installing → installed (관측: " + string.Join(" → ", seen) + ")");
            string ilog = HubFiles.ReadShared(Path.Combine(r.InstallDir, "install.log")) ?? "";
            int iI = ilog.IndexOf("install.json state=installing", StringComparison.Ordinal), iS = ilog.IndexOf("sup 분리 기동", StringComparison.Ordinal),
                iH = ilog.IndexOf("건강 확인 OK", StringComparison.Ordinal), iD = ilog.IndexOf("install.json state=installed 확정", StringComparison.Ordinal);
            ctx.Check(iI >= 0 && iI < iS && iS < iH && iH < iD, "install.log 순서: installing 기록 → sup 기동 → 건강 확인 OK → installed 확정");
            var hm = Regex.Match(e.Out, @"건강 확인 OK (\d+\.\d)초");
            double hs = hm.Success ? double.Parse(hm.Groups[1].Value, CultureInfo.InvariantCulture) : 999;
            ctx.Check(hm.Success && hs <= 10.0, "건강 확인 " + hs.ToString("0.0", CultureInfo.InvariantCulture) + "초 ≤ 10초(합격선)");
            ctx.Check(ilog.Contains("elevated=false"), "install.log elevated=false");

            var info = r.InstallInfoJson();
            ctx.Check(info.HasValue, "install.json 있음");
            var iv = info.Value;
            string hubDir = HubFiles.Str(iv, "hub_dir"), logRoot = HubFiles.Str(iv, "log_root"), id = HubFiles.Str(iv, "install_id");
            string expRoot = Path.Combine(r.UserProfile, "uart_logs");
            ctx.Check(HubFiles.Str(iv, "state") == "installed" && HubFiles.Str(iv, "version") == Hub.Version && SystemAccess.SamePath(HubFiles.Str(iv, "exe"), r.InstalledExe)
                      && SystemAccess.SamePath(logRoot, expRoot) && SystemAccess.SamePath(hubDir, Path.Combine(expRoot, "hub")) && Regex.IsMatch(id ?? "", "^[0-9a-f]{32}$")
                      && iv.TryGetProperty("skills", out var sk) && sk.ValueKind == JsonValueKind.Array && sk.GetArrayLength() == 3,
                      "install.json: state=installed·version=이 빌드·exe=설치본·log_root=<USERPROFILE>\\uart_logs(D: 이동식)·hub_dir·install_id(32 hex)·skills=[Windows·WSL 2](US-109)");
            ctx.Check(File.Exists(r.InstalledExe) && File.Exists(Path.Combine(r.InstallDir, "prefs.json")) && File.Exists(Path.Combine(r.InstallDir, "install.log"))
                      && !File.Exists(Path.Combine(r.InstallDir, "UartHub.new.exe")), "설치 폴더: UartHub.exe·install.json·prefs.json·install.log (UartHub.new.exe 없음)");
            bool dstMotw = r.AdsExists(r.InstalledExe, "Zone.Identifier");
            ctx.Check(srcMotw && !dstMotw, "MotW: 받은 exe 에는 Zone.Identifier 있음 → 설치본에는 없음(스트림 복사)");
            ctx.Check(e.Out.Contains("— 일치") && e.Out.Contains("version --json"), "복사본 version --json = 자기 버전·Core MVID 확인");

            var st = ShellLink.Read(r.StartupLnk);
            ctx.Check(st != null && SystemAccess.SamePath(st.Target, r.InstalledExe) && st.Arguments == "sup" && (st.Description ?? "").Contains("[uart_hub:" + id + "]"),
                      "시작프로그램 'UART 허브.lnk' → 설치본 UartHub.exe sup ([uart_hub:<install_id>] 표시)");
            var mm = ShellLink.Read(Path.Combine(r.MenuDir, "UART 허브.lnk"));
            var mu = ShellLink.Read(Path.Combine(r.MenuDir, "UART 허브 제거.lnk"));
            ctx.Check(mm != null && mu != null && SystemAccess.SamePath(mm.Target, r.InstalledExe) && mm.Arguments == "sup" && SystemAccess.SamePath(mu.Target, r.InstalledExe) && mu.Arguments == "uninstall",
                      "시작 메뉴 'UART 허브\\{UART 허브 → sup, UART 허브 제거 → uninstall}'");

            string tok = Path.Combine(hubDir, "token");
            string tokText = File.ReadAllText(tok);
            var sec = new FileInfo(tok).GetAccessControl(AccessControlSections.Access);
            var rules = sec.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
            using (var me = WindowsIdentity.GetCurrent())
                ctx.Check(Regex.IsMatch(tokText, "^[0-9a-f]{32}$") && sec.AreAccessRulesProtected && rules.Count == 1 && rules[0].IdentityReference.Value == me.User.Value
                          && rules[0].FileSystemRights.HasFlag(FileSystemRights.FullControl) && rules[0].AccessControlType == AccessControlType.Allow && !rules[0].IsInherited,
                          "토큰 32자 hex + ACL: 상속 끊김, 현재 사용자 SID 1개 FullControl(icacls 대체)");
            ctx.Check(File.ReadAllText(Path.Combine(hubDir, "config.json")) == r.SeedConfig() && Directory.Exists(Path.Combine(logRoot, "live")),
                      "config.json 없던 곳에 새로 만듦(시험: seed_config.json 그대로) · <log_root>\\live");
            var prefs = HubFiles.ReadJson(Path.Combine(r.InstallDir, "prefs.json"));
            ctx.Check(prefs.HasValue && HubFiles.Str(prefs.Value, "terminal") == "auto", "prefs.json terminal=auto(터미널 경로 후보 탐지)");
            ctx.Check(!File.Exists(Path.Combine(r.Root, "fake_tasks", "uart_hub.xml")) && e.Out.Contains("이전 PS 작업 \\uart_hub 삭제(동작이 uart_hub.ps1)"),
                      "이전 PS 작업 \\uart_hub(동작 uart_hub.ps1) 삭제(가짜 작업 스케줄러)");
            ctx.Check(e.Out.Contains("skills Windows: 넣음 — " + Path.Combine(r.UserProfile, ".claude", "skills", "uart")) && e.Out.Contains("skills WSL Ubuntu-22.04: 건너뜀(.claude 없음)"),
                      "skills 배치(US-109): Windows .claude 있음 → 넣음, 가짜 배포판(홈에 .claude 없음) → 건너뜀");

            // 허브: 운영 모드 감독·코어, C-14 Repair 0회, ls
            var sj = HubFiles.ReadJson(HubFiles.SupState(hubDir));
            var cj = HubFiles.ReadJson(HubFiles.CorePid(hubDir));
            ctx.Check(sj.HasValue && HubFiles.Str(sj.Value, "state") == "running" && cj.HasValue && HubFiles.Str(cj.Value, "control") == "listening", "sup.json running · core.pid listening");
            string hubLog() => HubFiles.ReadShared(Path.Combine(hubDir, "hub.log")) ?? "";
            ctx.WaitUntil(() => hubLog().Contains("[sup] 설치 상태 일치"), 15000, "C-14: 설치기가 끝난 뒤 감독이 다시 판정 → '설치 상태 일치'");
            string hl = hubLog();
            ctx.Check(hl.Contains("(운영 모드)") && hl.Contains("[core] 감독 감시 시작"), "운영 모드 감독(install.json 의 hub_dir) → 코어에 --watch-sup(감독 감시 시작)");
            ctx.Check(hl.Contains("[sup] 설치기 마무리 대기") && InstallRig.Count(hl, "Repair 시작") == 0 && !ilog.Contains("== Repair("),
                      "C-14: 설치기 생존 중 '설치기 마무리 대기'만, 정상 경로 Repair 0회");
            JsonElement ls = default;
            int lsRc = -1;
            ctx.WaitUntil(() =>
            {
                var o = r.Run(r.InstalledExe, "cli", "ls", "--json");
                lsRc = o.Code;
                if (o.Code != 0) return false;
                try { ls = LifeRig.Parse(o.Out); } catch (Exception) { return false; }
                return ls.GetProperty("cables").EnumerateArray().Any(c => J.S(c, "cable") == r.Cable && J.S(c, "status") == "attached");
            }, 15000, "설치본 cli ls(UART_HUB_DIR 없이 install.json 으로 허브 폴더 결정) → FakeBoard " + r.Cable + " attached");

            r.CheckNoProcs("정리");
            real.Verify(ctx, r);
        }

        // ================================================================== install-first-fail
        [Scenario("install-first-fail", "첫 설치 건강 확인 실패 주입(UART_HUB_TEST_FAIL=health, 감독·코어는 실제로 뜬 상태) → 8 단계: update mutex 재획득·shutdown 으로 감독·코어 정상 종료·시작프로그램·시작 메뉴 바로가기 삭제·install_failed_<시각>.log·설치 폴더 삭제, 허브 폴더(token·config·hub.log)·로그 폴더 유지, 오류 창(로그 경로). 정리 뒤 30초 동안 바로가기·설치 폴더·프로세스가 되살아나지 않음. 동작이 uart_hub.ps1 이 아닌 이전 작업은 남김. 실제 시스템 무변경",
                  Order = 920, TimeoutS = 240)]
        public static void InstallFirstFail(ScenarioContext ctx)
        {
            var real = RealSystem.Capture(ctx);
            var r = InstallRig.Create(ctx, "root", 17720, 17730);
            r.Write("fake_drives.json", "{\"D\":{\"type\":\"Fixed\",\"ready\":true}}");
            r.FakeTask("uart_hub", "C:\\Windows\\System32\\notepad.exe", "C:\\x\\memo.txt");
            var e = r.Run(Dev, new[] { "install" }, new Dictionary<string, string> { ["UART_HUB_TEST_FAIL"] = "health" });
            string logRoot = Path.Combine(r.Root, "drive_D", "uart_logs"), hubDir = Path.Combine(logRoot, "hub");
            ctx.Check(e.Code == 1 && e.Out.Contains("건강 확인 OK") && e.Out.Contains("시험 주입(UART_HUB_TEST_FAIL=health)") && e.Out.Contains("== 8. 첫 설치 실패 정리"),
                      "건강 확인 실패 주입 → 8 단계 정리, exit 1" + (e.Code != 1 ? " — 출력 꼬리: " + Tail(e.Out) : ""));
            var fm = Regex.Match(e.Out, @"\[ui\] 오류 창: 설치하지 못했습니다\. 로그 파일: (\S+install_failed_\d{8}_\d{6}\.log)");
            ctx.Check(fm.Success && SystemAccess.SamePath(Path.GetDirectoryName(fm.Groups[1].Value), hubDir) && File.Exists(fm.Groups[1].Value),
                      "오류 창(시험: 기록)에 install_failed_<시각>.log 경로, 그 파일이 허브 폴더에 있음");
            string fl = fm.Success ? File.ReadAllText(fm.Groups[1].Value) : "";
            ctx.Check(fl.Contains("== 1. 사전 점검") && fl.Contains("== 6.") && fl.Contains("== 8. 첫 설치 실패 정리") && fl.Contains("설치 폴더 삭제"),
                      "install_failed 로그에 사전 점검~8 단계(설치 폴더 삭제까지)");
            ctx.Check(e.Out.Contains("update mutex 재획득") && Regex.IsMatch(e.Out, "감독 요청 shutdown → \\{\"status\":\"ok\"") && e.Out.Contains("허브 정지 완료"),
                      "update mutex 재획득 → 감독에 shutdown → 감독·코어 정상 종료(kill 없이)");
            ctx.Check(!e.Out.Contains("검증 PID kill pid="), "검증 PID kill 이 필요 없었음(정상 종료 경로)");
            ctx.Check(!File.Exists(r.StartupLnk) && !Directory.Exists(r.MenuDir), "시작프로그램 바로가기·시작 메뉴 'UART 허브' 폴더 삭제");
            ctx.Check(!Directory.Exists(r.InstallDir), "설치 폴더(%LOCALAPPDATA%\\uart_hub, exe 포함) 삭제");
            ctx.Check(File.Exists(Path.Combine(hubDir, "token")) && File.Exists(Path.Combine(hubDir, "config.json")) && File.Exists(Path.Combine(hubDir, "hub.log"))
                      && Directory.Exists(Path.Combine(logRoot, "live")), "허브 폴더(token·config.json·hub.log)·로그 폴더(D:\\uart_logs, live) 유지");
            string hl = HubFiles.ReadShared(Path.Combine(hubDir, "hub.log")) ?? "";
            ctx.Check(hl.Contains("코어 정상 종료(exit 0)") && hl.Contains("종료 요청(install-first-fail)"), "hub.log: 감독 shutdown(install-first-fail) → 코어 정상 종료(exit 0)");
            ctx.Check(!HubFiles.IsStopped(hubDir), "control.json stopped 아님(shutdown 은 정지 상태를 남기지 않음)");
            ctx.Check(File.Exists(Path.Combine(r.Root, "fake_tasks", "uart_hub.xml")) && e.Out.Contains("동작이 uart_hub.ps1 아님"), "동작이 uart_hub.ps1 이 아닌 이전 작업 \\uart_hub 는 남김(P3)");
            ctx.WaitUntil(() => r.Procs().Count == 0, 10000, "이 시험 루트의 UartHub.exe(감독·코어·설치기) 0");

            // 30초 동안 되살아나지 않음(감독 C-14 Repair·코어 C-11 이 없어야 함)
            var sw = Stopwatch.StartNew();
            string back = null;
            while (sw.ElapsedMilliseconds < 30000 && back == null)
            {
                if (File.Exists(r.StartupLnk)) back = "시작프로그램 바로가기";
                else if (Directory.Exists(r.MenuDir)) back = "시작 메뉴 폴더";
                else if (Directory.Exists(r.InstallDir)) back = "설치 폴더";
                else if (r.Procs().Count > 0) back = "UartHub.exe 프로세스";
                Thread.Sleep(500);
            }
            ctx.Check(back == null, "정리 뒤 30초 동안 바로가기·설치 폴더·프로세스가 되살아나지 않음" + (back != null ? " — 되살아남: " + back : ""));
            r.CheckNoProcs("정리");
            real.Verify(ctx, r);
        }

        // ================================================================== uninstall-isolated
        [Scenario("uninstall-isolated", "제거(C-13 격리, 설치본 exe 로 실행): uninstall --dry-run 이 1~10 단계 출력·무변경(허브 계속 실행) → uninstall: 확인 창 1회, 시작프로그램·시작 메뉴 바로가기 삭제, shutdown 으로 감독·코어 정상 종료(프로세스 0), 자기 아이콘만 삭제(icons.json 의 남의 미끼·사용자 바로가기 보존), 자기 이미지를 %TEMP%\\uart_hub_removed_<시각>.exe 로 옮긴 뒤 설치 폴더 삭제, 로그·허브 폴더 유지·control.json stopped 아님. 다음 install --dry-run 이 옮긴 이미지를 지울 것으로 표시. 실제 시스템 무변경",
                  Order = 930, TimeoutS = 240)]
        public static void UninstallIsolated(ScenarioContext ctx)
        {
            var real = RealSystem.Capture(ctx);
            var r = InstallRig.Create(ctx, "root", 17740, 17750);
            var inst = r.Run(Dev, "install");
            ctx.Check(inst.Code == 0, "설치 exit 0" + (inst.Code != 0 ? " — " + Tail(inst.Out) : ""));
            var iv = r.InstallInfoJson().Value;
            string id = HubFiles.Str(iv, "install_id"), hubDir = HubFiles.Str(iv, "hub_dir"), logRoot = HubFiles.Str(iv, "log_root");
            ctx.WaitUntil(() => (HubFiles.ReadShared(Path.Combine(hubDir, "hub.log")) ?? "").Contains("[sup] 설치 상태 일치"), 15000, "설치 뒤 감독 C-14 판정 끝");

            // 아이콘: 자기 것 1 + icons.json 에 적힌 남의 미끼 1 + icons.json 밖 사용자 바로가기 1
            string own = Path.Combine(r.Desktop, r.Host + " (" + r.Base + ").lnk");
            string decoy = Path.Combine(r.Desktop, "EVB-A1 (7002).lnk");
            string user = Path.Combine(r.Desktop, "메모장.lnk");
            string np = Path.Combine(Environment.SystemDirectory, "notepad.exe");
            ShellLink.Save(own, new ShellLinkInfo { Target = r.InstalledExe, Arguments = "open " + r.Base, Description = "UART 허브 " + ShellLink.Marker(id) });
            ShellLink.Save(decoy, new ShellLinkInfo { Target = np, Arguments = "x", Description = "남의 바로가기" });
            ShellLink.Save(user, new ShellLinkInfo { Target = np, Arguments = "", Description = "사용자" });
            File.WriteAllText(Path.Combine(r.InstallDir, "icons.json"),
                "{\"icons\":[{\"port\":" + r.Base + ",\"path\":" + Json.Str(own) + "},{\"port\":7002,\"path\":" + Json.Str(decoy) + "}]}\n");

            // dry-run(설치본 exe): 모든 단계, 무변경
            string[] watch = { r.InstallDir, Path.Combine(r.Root, "AppData"), r.Desktop, r.Temp, r.UserProfile };
            var snap = InstallRig.Snapshot(watch.Where(x => !x.StartsWith(Path.Combine(r.UserProfile, "uart_logs"), StringComparison.OrdinalIgnoreCase)).ToArray());
            snap = Filter(snap, hubDir, logRoot);
            var dr = r.Run(r.InstalledExe, "uninstall", "--dry-run");
            var missing = UninstallSteps.Where(s => !dr.Out.Contains(s)).ToList();
            ctx.Check(dr.Code == 0 && missing.Count == 0 && dr.Out.Contains("# dry-run 끝") && !dr.Out.Contains("[ui] 확인 창"),
                      "uninstall --dry-run: 단계 1~10 모두 출력·확인 창 없음·exit 0" + (missing.Count > 0 ? " — 빠짐: " + string.Join(",", missing) : ""));
            ctx.Check(dr.Out.Contains("[dry-run] 삭제 " + own) && !dr.Out.Contains("[dry-run] 삭제 " + decoy) && dr.Out.Contains("자기 것이 아님"),
                      "dry-run: 자기 아이콘만 삭제 예정, icons.json 의 남의 미끼는 남김 표시");
            var snap2 = Filter(InstallRig.Snapshot(watch.Where(x => !x.StartsWith(Path.Combine(r.UserProfile, "uart_logs"), StringComparison.OrdinalIgnoreCase)).ToArray()), hubDir, logRoot);
            string dd = InstallRig.Diff(snap, snap2);
            ctx.Check(dd == "" && r.Procs().Count >= 2, "uninstall --dry-run: 설치 폴더·바로가기·바탕화면·TEMP 무변경, 허브 계속 실행" + (dd == "" ? "" : ": " + dd));

            // 실제 제거(설치본 exe = 시작 메뉴 'UART 허브 제거')
            var u = r.Run(r.InstalledExe, "uninstall");
            ctx.Check(u.Code == 0 && u.Out.Contains("# 제거 완료"), "uninstall exit 0" + (u.Code != 0 ? " — " + Tail(u.Out) : ""));
            ctx.Check(InstallRig.Count(u.Out, "[ui] 확인 창(자동 응답=제거)") == 1, "확인 창 1회(자동 응답)");
            ctx.Check(!File.Exists(r.StartupLnk) && !Directory.Exists(r.MenuDir), "시작프로그램 바로가기·시작 메뉴 폴더 삭제");
            ctx.Check(Regex.IsMatch(u.Out, "감독 요청 shutdown → \\{\"status\":\"ok\"") && u.Out.Contains("허브 정지 완료") && !u.Out.Contains("검증 PID kill pid="), "shutdown 으로 감독·코어 정상 종료(kill 없음)");
            ctx.WaitUntil(() => r.Procs().Count == 0, 10000, "UartHub.exe 프로세스 0(감독·코어·제거기)");
            ctx.Check(!File.Exists(own) && File.Exists(decoy) && File.Exists(user), "자기 아이콘만 삭제 — icons.json 의 남의 미끼 'EVB-A1 (7002).lnk'·사용자 바로가기 보존");
            ctx.Check(!Directory.Exists(r.InstallDir), "설치 폴더 삭제");
            var moved = Directory.GetFiles(r.Temp, "uart_hub_removed_*.exe");
            ctx.Check(moved.Length == 1 && Regex.IsMatch(Path.GetFileName(moved[0]), @"^uart_hub_removed_\d{8}_\d{6}\.exe$") && new FileInfo(moved[0]).Length > 1000000,
                      "실행 중이던 자기 이미지 → %TEMP%\\uart_hub_removed_<시각>.exe");
            ctx.Check(File.Exists(Path.Combine(hubDir, "token")) && File.Exists(Path.Combine(hubDir, "config.json")) && Directory.Exists(Path.Combine(logRoot, "live"))
                      && Directory.GetFiles(hubDir, "uninstall_*.log").Length == 1, "로그·허브 폴더(token·config.json·live) 유지, 제거 기록 uninstall_<시각>.log");
            string hl = HubFiles.ReadShared(Path.Combine(hubDir, "hub.log")) ?? "";
            ctx.Check(hl.Contains("종료 요청(uninstall)") && hl.Contains("코어 정상 종료(exit 0)"), "hub.log: 감독 shutdown(uninstall) → 코어 정상 종료");
            ctx.Check(!HubFiles.IsStopped(hubDir), "control.json stopped 아님(다시 설치하면 바로 기록)");
            Thread.Sleep(3000);
            ctx.Check(r.Procs().Count == 0 && !Directory.Exists(r.InstallDir) && !File.Exists(r.StartupLnk), "3초 뒤에도 감독 재기동 없음(install.json 삭제·update mutex)");

            var nx = r.Run(Dev, "install", "--dry-run");
            ctx.Check(nx.Code == 0 && nx.Out.Contains("[dry-run] 지움 " + moved[0]) && nx.Out.Contains("이전 허브 폴더 재사용"),
                      "다음 install --dry-run: %TEMP%\\uart_hub_removed_*.exe 지울 예정, 남긴 허브 폴더 재사용");
            r.CheckNoProcs("정리");
            real.Verify(ctx, r);
        }

        static SortedDictionary<string, string> Filter(SortedDictionary<string, string> s, params string[] skip)
        {
            var d = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in s)
                if (!skip.Any(x => kv.Key.StartsWith(x, StringComparison.OrdinalIgnoreCase))) d[kv.Key] = kv.Value;
            return d;
        }

        static string Tail(string s) => s == null ? "" : (s.Length > 1500 ? s.Substring(s.Length - 1500) : s).Replace("\n", " ⏎ ");
    }
}
