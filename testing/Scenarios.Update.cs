// US-106(계획 T2 업데이트 0~5·되돌림·잔재 복구·무인 복구 C-11·자가 복구 C-9·C-8 mutex·C-12 왕복 보존·C-14) 시나리오.
// 모두 실제 UartHub.exe 를 UART_HUB_TEST_ROOT 격리(C-13) 아래에서 띄우고, 끝에 "실제 시스템 무변경"(RealSystem)을 단언한다.
// 두 번째 빌드(vB) = build.sh 가 만드는 <개발 사본>\vB\UartHub.exe — 같은 소스를 BuildInfo.Version 만 <ver>+1 로 다시 컴파일·publish 한 것.
// 포트: install-update 17800/17810(옛 이미지를 쥔 별도 코어 17815/17817) · install-rollback 17820+5i/+2 · finalize-race 17900~17917
//       · update-shutdown 17920/17922 · sup-killed-respawn 17930/17932(LifeRig 17935/17937) · mutex-race 17940/17942(cli 에 준 죽은 포트 17949)
//       · selfheal-prod 17950/17952 · state-roundtrip 17960/17962
// kill 은 이 시험이 띄운 프로세스(자기 자식 Process) 또는 WMI 명령줄에 이 시험 루트가 들어간 UartHub.exe 의 PID 만 한다.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace UartHub.Testing
{
    /// <summary>업데이트 계열 시나리오 도우미(InstallRig 위).</summary>
    internal static class Upd
    {
        public static string Dev => TestEnv.UartHubExe;
        public static string VB => Path.Combine(Path.GetDirectoryName(TestEnv.UartHubExe), "vB", "UartHub.exe");
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        static readonly string[] DropEnv = { "UART_HUB_DIR", "UART_HUB_CONTROL_PORT", "UART_HUB_TEST_FAIL", "UART_HUB_TEST_ANSWER", "UART_HUB_TEST_C14",
                                             "UART_HUB_TEST_HEALTH_S", "UART_HUB_TEST_DELAY", "UART_HUB_TEST_RESPAWN" };

        public static Dictionary<string, string> Env(params string[] kv)
        {
            var d = new Dictionary<string, string>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
            return d;
        }

        public static string HubDir(InstallRig r) => HubFiles.Str(r.InstallInfoJson().Value, "hub_dir");
        public static string LogRoot(InstallRig r) => HubFiles.Str(r.InstallInfoJson().Value, "log_root");
        public static string HubLog(string hubDir) => HubFiles.ReadShared(Path.Combine(hubDir, "hub.log")) ?? "";
        public static string InstLog(InstallRig r) => HubFiles.ReadShared(Path.Combine(r.InstallDir, "install.log")) ?? "";
        public static int Count(string text, string what) => InstallRig.Count(text, what);
        public static string Sha(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).Substring(0, 16) : "(없음)";
        public static string Tail(string s) => s == null ? "" : (s.Length > 1500 ? s.Substring(s.Length - 1500) : s).Replace("\n", " ⏎ ");
        public static JsonElement Parse(string s) { try { return LifeRig.Parse(s); } catch (Exception) { return default; } }
        public static bool True(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && J.B(e, k) == true;
        public static string S(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object ? J.S(e, k) : null;
        public static string Board(string cable, string com, string host) => "{\"cable\":" + Json.Str(cable) + ",\"com\":" + Json.Str(com) + ",\"hostname\":" + Json.Str(host) + ",\"printk_ms\":300}";

        /// <summary>설치 폴더의 UartHub*.exe 이름(정렬).</summary>
        public static string Files(InstallRig r)
            => Directory.Exists(r.InstallDir) ? string.Join(",", Directory.GetFiles(r.InstallDir, "UartHub*.exe").Select(Path.GetFileName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) : "(설치 폴더 없음)";

        /// <summary>이 시험 루트의 UartHub.exe 중 하위 명령이 sub 인 것(WMI 명령줄 — 이 루트가 들어간 것만).</summary>
        public static List<int> Kind(InstallRig r, string sub)
            => r.Procs().Where(p => Regex.IsMatch(p.Cmd, @"\.exe""?\s+" + sub + @"(\s|$)", RegexOptions.IgnoreCase)).Select(p => p.Pid).OrderBy(x => x).ToList();

        /// <summary>exe 를 비동기로 띄운다(InstallRig.Run 과 같은 환경). 출력은 Output() 으로(끝난 뒤 한 번) — InstallRig.Outputs 에도 넣는다(격리 단언).</summary>
        public static (Process P, Func<string> Output) Start(InstallRig r, string exe, string[] args, Dictionary<string, string> env)
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8, WorkingDirectory = Path.GetDirectoryName(exe),
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            foreach (var k in DropEnv) psi.Environment.Remove(k);
            psi.Environment["UART_HUB_TEST_ROOT"] = r.Root;
            if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
            var p = Process.Start(psi);
            p.StandardInput.Close();
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();
            r.Ctx.Defer(() => { try { if (!p.HasExited) { p.Kill(); p.WaitForExit(5000); } } catch (Exception) { } });
            r.Ctx.Log("비동기 기동 " + Path.GetFileName(exe) + " " + string.Join(" ", args) + " → pid=" + p.Id);
            string Output()
            {
                Task.WaitAll(new Task[] { so, se }, 15000);
                string t = (so.IsCompletedSuccessfully ? so.Result : "") + "\n" + (se.IsCompletedSuccessfully ? se.Result : "");
                lock (r.Outputs) r.Outputs.Add(t);
                return t;
            }
            return (p, Output);
        }

        /// <summary>install(또는 업데이트) → exit 0 → 감독의 C-14 판정("설치 상태 일치")이 judged 번째까지 나올 때까지.</summary>
        public static string InstallAndWait(ScenarioContext ctx, InstallRig r, string exe, Dictionary<string, string> env, int judged, string what)
        {
            var e = r.Run(exe, new[] { "install" }, env);
            ctx.Check(e.Code == 0 && (e.Out.Contains("# 설치 완료") || e.Out.Contains("# 업데이트 완료")), what + ": exit 0 (" + e.Ms + "ms)" + (e.Code != 0 ? " — " + Tail(e.Out) : ""));
            string hub = HubDir(r);
            ctx.WaitUntil(() => Count(HubLog(hub), "[sup] 설치 상태 일치") >= judged, 20000, what + ": 감독 C-14 '설치 상태 일치'(" + judged + "번째)");
            return e.Out;
        }

        /// <summary>cli ls --json 의 케이블 표 "cable=port/status; …"(attached 로 기다릴 케이블들이 붙을 때까지).</summary>
        public static string Ls(ScenarioContext ctx, InstallRig r, string what, params string[] attached)
        {
            string t = null;
            ctx.WaitUntil(() =>
            {
                var o = r.Run(r.InstalledExe, "cli", "ls", "--json");
                if (o.Code != 0) return false;
                var j = Parse(o.Out);
                if (j.ValueKind != JsonValueKind.Object || !j.TryGetProperty("cables", out var cs)) return false;
                var l = cs.EnumerateArray().ToList();
                if (attached.Any(c => !l.Any(x => J.S(x, "cable") == c && J.S(x, "status") == "attached"))) return false;
                t = string.Join("; ", l.Select(x => J.S(x, "cable") + "=" + J.N(x, "port").ToString(CultureInfo.InvariantCulture) + "/" + J.S(x, "status")).OrderBy(x => x, StringComparer.Ordinal));
                return true;
            }, 25000, what + ": ls 에 " + string.Join(",", attached) + " attached");
            ctx.Log(what + ": " + t);
            return t;
        }

        public sealed class St { public string Ver, SupVer, CoreVer; public long SupPid, CorePid; public override string ToString() => "v=" + Ver + " sup.v=" + SupVer + " core_version=" + CoreVer + " sup=" + SupPid + " core=" + CorePid; }

        /// <summary>cli hub status --json: version == supervisor.version == supervisor.core_version == expect 가 될 때까지.</summary>
        public static St Status(ScenarioContext ctx, InstallRig r, string expect, string what)
        {
            St s = null;
            ctx.WaitUntil(() =>
            {
                var o = r.Run(r.InstalledExe, "cli", "hub", "status", "--json");
                if (o.Code != 0) return false;
                var j = Parse(o.Out);
                if (j.ValueKind != JsonValueKind.Object || !j.TryGetProperty("supervisor", out var sv) || sv.ValueKind != JsonValueKind.Object) return false;
                s = new St { Ver = J.S(j, "version"), SupVer = J.S(sv, "version"), CoreVer = J.S(sv, "core_version"), SupPid = J.N(sv, "pid"), CorePid = J.N(sv, "core_pid") };
                return s.Ver == expect && s.SupVer == expect && s.CoreVer == expect;
            }, 25000, what + ": hub status version == supervisor.version == supervisor.core_version == v" + expect);
            ctx.Log(what + ": " + s);
            return s;
        }

        /// <summary>token·config.json 해시, state.json 의 assign·released(정렬 문자열).</summary>
        public static string Keep(string hubDir)
        {
            var st = HubFiles.ReadJson(Path.Combine(hubDir, "state.json"));
            string assign = "", released = "";
            if (st.HasValue)
            {
                if (st.Value.TryGetProperty("assign", out var a)) assign = string.Join(",", a.EnumerateObject().Select(p => p.Name + "=" + p.Value.GetRawText()).OrderBy(x => x, StringComparer.Ordinal));
                if (st.Value.TryGetProperty("released", out var rl)) released = string.Join(",", rl.EnumerateArray().Select(x => x.GetString()).OrderBy(x => x, StringComparer.Ordinal));
            }
            return "token=" + Sha(Path.Combine(hubDir, "token")) + " config=" + Sha(Path.Combine(hubDir, "config.json")) + " assign={" + assign + "} released=[" + released + "]";
        }

        // ------------------------------------------------------------------ 케이블 로그
        static readonly Regex LogTs = new Regex(@"^\[(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3}) ");

        /// <summary>log_root 아래 포트 port 의 .log 줄(시각순 — 파일 이름이 보드명에 따라 달라도).</summary>
        public static List<(DateTime T, string Line, string File)> CableLines(string logRoot, int port)
        {
            var l = new List<(DateTime, string, string)>();
            string pre = port.ToString(CultureInfo.InvariantCulture) + "_";
            foreach (var rel in LogFiles.All(logRoot).Where(x => x.EndsWith(".log", StringComparison.Ordinal) && Path.GetFileName(x).StartsWith(pre, StringComparison.Ordinal)))
            {
                string path = Path.Combine(logRoot, rel);
                foreach (var line in LogFiles.ReadShared(path).Split('\n'))
                {
                    var m = LogTs.Match(line);
                    if (m.Success) l.Add((DateTime.ParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture), line.TrimEnd('\r'), path));
                }
            }
            return l.OrderBy(x => x.Item1).ToList();
        }

        /// <summary>마지막 *** hub start (UartHub ver) *** 와 그 앞 마지막 *** hub stop *** 사이(초, COM 공백). after = hub start 뒤 줄들.</summary>
        public static double Gap(ScenarioContext ctx, string logRoot, int port, string ver, string what, out List<string> after)
        {
            var l = CableLines(logRoot, port);
            int s = l.FindLastIndex(x => x.Line.Contains("*** hub start (UartHub " + ver + ") ***"));
            if (s < 0) ctx.Fail(what + ": 케이블 로그(포트 " + port + ")에 *** hub start (UartHub " + ver + ") *** 없음");
            int e = s > 0 ? l.FindLastIndex(s - 1, x => x.Line.Contains("*** hub stop ***")) : -1;
            after = l.Skip(s + 1).Take(4).Select(x => x.Line).ToList();
            if (e < 0) return double.NaN;
            double g = (l[s].T - l[e].T).TotalSeconds;
            ctx.Log(what + ": COM 공백 " + g.ToString("0.000", CultureInfo.InvariantCulture) + "초 (" + l[e].Line.Substring(1, 23) + " *** hub stop *** → " + l[s].Line.Substring(1, 23) + " *** hub start ***)");
            return g;
        }

        // ------------------------------------------------------------------ 감독 kill·재기동
        static readonly Regex RespawnRe = new Regex(@"^\[(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3})\] \[core\] sup respawn pid=(\d+)", RegexOptions.Multiline);

        public static List<(DateTime T, int Pid)> Respawns(string hubDir)
            => RespawnRe.Matches(HubLog(hubDir)).Select(m => (DateTime.ParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                                                              int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))).ToList();

        /// <summary>이 루트의 감독(1개여야 함)을 kill. (kill 끝난 시각, 그때까지의 respawn 수).</summary>
        public static (DateTime T, int Before) KillSup(ScenarioContext ctx, InstallRig r, string hubDir, string what)
        {
            var sups = Kind(r, "sup");
            ctx.Check(sups.Count == 1, what + ": 감독 1개(" + string.Join(",", sups) + ")");
            int before = Respawns(hubDir).Count;
            LifeRig.Kill(sups[0]);
            ctx.Check(!LifeRig.Alive(sups[0]), what + ": 감독 pid=" + sups[0] + " kill(WMI 명령줄로 이 시험 루트 확인)");
            return (DateTime.Now, before);
        }

        public static void KillPid(ScenarioContext ctx, InstallRig r, int pid, string what)
        {
            ctx.Check(r.Procs().Any(p => p.Pid == pid), what + ": pid " + pid + " 은 이 시험 루트의 UartHub.exe(명령줄 확인)");
            LifeRig.Kill(pid);
            ctx.Check(!LifeRig.Alive(pid), what + ": pid " + pid + " kill");
        }

        /// <summary>
        /// 감독·코어를 둘 다 먼저 확인(WMI 명령줄)한 뒤 잇달아 끝낸다 — 하나씩 확인하며 끝내면 그 사이(WMI 가 느릴 때 2초 넘게) 코어의 감독 감시(sup_watch_s)가
        /// 감독을 다시 띄워 "둘 다 없음" 상태가 만들어지지 않는다(시험 경합).
        /// </summary>
        public static void KillBoth(ScenarioContext ctx, InstallRig r, int sup, int core)
        {
            var procs = r.Procs();
            ctx.Check(procs.Any(p => p.Pid == sup) && procs.Any(p => p.Pid == core), "감독 pid " + sup + "·코어 pid " + core + " 은 이 시험 루트의 UartHub.exe(명령줄 확인)");
            LifeRig.Kill(sup);
            LifeRig.Kill(core);
            ctx.Check(!LifeRig.Alive(sup) && !LifeRig.Alive(core), "감독·코어 kill(잇달아)");
        }

        public static (DateTime T, int Pid) WaitRespawn(ScenarioContext ctx, string hubDir, int before, int ms, string what)
        {
            (DateTime, int) got = default;
            ctx.WaitUntil(() => { var l = Respawns(hubDir); if (l.Count <= before) return false; got = l[before]; return true; }, ms, what + ": [core] sup respawn");
            return got;
        }

        public static void SleepSince(DateTime t, int ms)
        {
            double left = ms - (DateTime.Now - t).TotalMilliseconds;
            if (left > 0) Thread.Sleep((int)left);
        }

        public static void HubOff(ScenarioContext ctx, InstallRig r, string what)
            => ctx.WaitUntil(() => Kind(r, "sup").Count == 0 && Kind(r, "core").Count == 0, 15000, what + ": 허브 꺼짐(감독·코어 0)");

        public static void SetInstall(InstallRig r, string key, string value)
        {
            var o = HubFiles.ToJObj(r.InstallInfoJson().Value).Set(key, value);
            File.WriteAllText(r.InstallJson, o.ToString() + "\n", Utf8);
        }

        /// <summary>오류 창 문구(첫 줄부터 "로그 파일:" 줄까지).</summary>
        public static string ErrBox(string o)
        {
            int i = o.IndexOf("[ui] 오류 창: ", StringComparison.Ordinal);
            if (i < 0) return "";
            int k = o.IndexOf("로그 파일:", i, StringComparison.Ordinal);
            return k < 0 ? o.Substring(i) : o.Substring(i, k - i);
        }
    }

    public static class UpdateScenarios
    {
        static string Dev => Upd.Dev;

        // ================================================================== install-update
        [Scenario("install-update", "업데이트(A1-4·NF-3, C-13 격리, 실제 exe): 같은 exe 두 번째 실행 = 같은 버전 복구, 버전만 다른 vB 빌드 = 업데이트. 매번 포트표·status(제외 케이블 released 그대로)·token/config SHA-256·state.json assign/released 동일, 감독·코어 PID 바뀜, hub status 의 version == supervisor.version == core_version, 케이블 로그 *** hub stop *** → *** hub start *** 공백 ≤15초(가짜 WSL 배포판 2개·-l -q 9초 지연은 공백 밖), 닫힌 로그 파일 해시 동일. 긴 cli wait 중 교체 → 그 cli 103·교체 성공, 실행 중인 옛 이미지(UartHub.old.exe)는 5 단계에서 못 지우고 그 프로세스가 끝난 뒤 다음 감독 기동 때 삭제. 정상 경로 Repair 0. 더 오래된 판은 확인 창에 표시(dry-run). 실제 시스템 무변경",
                  Order = 940, TimeoutS = 480)]
        public static void InstallUpdate(ScenarioContext ctx)
        {
            var real = RealSystem.Capture(ctx);
            var r = InstallRig.Create(ctx, "root", 17800, 17810);
            string vA = Hub.Version;
            ctx.Check(File.Exists(Upd.VB), "두 번째 빌드 있음: " + Upd.VB);
            string vB = r.Run(Upd.VB, "version").Out.Trim();
            ctx.Check(vB.Length == 17 && vB != vA && string.CompareOrdinal(vB, vA) > 0, "vB = 버전 문자열만 다른 더 새 빌드(vA " + vA + " → vB " + vB + ")");
            string cabA = r.Cable, cabB = r.Cable + "B";
            r.Write("sim_spec.json", "{\"boards\":[" + Upd.Board(cabA, "COM810", "SIMUPA") + "," + Upd.Board(cabB, "COM811", "SIMUPB") + "]}");
            string hub0 = Path.Combine(r.UserProfile, "uart_logs", "hub");
            r.Write(Path.Combine("UserProfile", "uart_logs", "hub", "state.json"),
                    "{\n  \"assign\": {\n    " + Json.Str(cabA) + ": 17810,\n    " + Json.Str(cabB) + ": 17811\n  },\n  \"released\": [" + Json.Str(cabB) + "],\n  \"boards\": {}\n}\n");
            r.FakeWsl("Ubuntu-Fake-1\nUbuntu-Fake-2\n");
            r.Write(Path.Combine("fake_wsl", "l_q.delay_ms"), "9000");

            Upd.InstallAndWait(ctx, r, Dev, null, 1, "vA 첫 설치");
            string hub = Upd.HubDir(r), logRoot = Upd.LogRoot(r);
            ctx.Check(SystemAccess.SamePath(hub, hub0), "허브 폴더 = <USERPROFILE>\\uart_logs\\hub(제외 케이블을 둔 state.json)");
            string ls0 = Upd.Ls(ctx, r, "vA", cabA);
            ctx.Check(ls0.Contains(cabA + "=17810/attached") && ls0.Contains(cabB + "=17811/released"), "vA 포트표: " + ls0);
            string keep0 = Upd.Keep(hub);
            var st0 = Upd.Status(ctx, r, vA, "vA");

            // ---- 1차: 같은 exe 다시 실행 = 같은 버전 복구
            var u1 = r.Run(Dev, "install");
            ctx.Check(u1.Code == 0 && u1.Out.Contains("v" + vA + " → v" + vA + " — 같은 버전 — 복구(다시 설치)") && u1.Out.Contains("[ui] 확인 창(자동 응답=복구)") && u1.Out.Contains("# 업데이트 완료"),
                      "1차(같은 exe): 같은 버전 복구 경로·확인 창 1회·exit 0 (" + u1.Ms + "ms)" + (u1.Code != 0 ? " — " + Upd.Tail(u1.Out) : ""));
            ctx.Check(u1.Out.Contains("WSL 배포판: Ubuntu-Fake-1, Ubuntu-Fake-2") && u1.Out.Contains("== U2.") && u1.Out.Contains("교체 OK(1번째 시도)"), "1차: 사전 점검(가짜 배포판 2개, 9초 지연) → U2 공백 → File.Replace 1번에 교체");
            ctx.WaitUntil(() => Upd.Count(Upd.HubLog(hub), "[sup] 설치 상태 일치") >= 2, 20000, "1차 뒤 새 감독 C-14 '설치 상태 일치'");
            ctx.Check(Upd.Ls(ctx, r, "1차 뒤", cabA) == ls0, "1차 뒤 포트표·status 같음(" + cabB + " released 그대로)");
            var st1 = Upd.Status(ctx, r, vA, "1차 뒤");
            ctx.Check(st1.SupPid != st0.SupPid && st1.CorePid != st0.CorePid, "1차: 감독·코어 PID 바뀜(" + st0.SupPid + "/" + st0.CorePid + " → " + st1.SupPid + "/" + st1.CorePid + ")");
            string keep1 = Upd.Keep(hub);
            ctx.Check(keep1 == keep0, "1차: " + keep1 + " 같음");
            double g1 = Upd.Gap(ctx, logRoot, 17810, vA, "1차", out _);
            ctx.Check(g1 <= 15, "1차: COM 공백 " + g1.ToString("0.0", CultureInfo.InvariantCulture) + "초 ≤ 15초");
            var lines = Upd.CableLines(logRoot, 17810);
            string closed = lines.First(x => x.Line.Contains("*** hub stop ***")).File;
            string closedSha = Upd.Sha(closed);
            ctx.Log("닫힌 로그 파일: " + Path.GetFileName(closed) + " sha=" + closedSha);

            // ---- 2차: vB. 그 전에 옛 이미지(vA)를 쥔 프로세스 두 개 — 긴 cli wait, 별도 허브 폴더의 코어(그 cli 처럼 끝나지 않는 대리)
            string hold = Path.Combine(r.Root, "hold", "hub");
            Directory.CreateDirectory(hold);
            File.WriteAllText(Path.Combine(hold, "config.json"), "{\"base_port\": 17817, \"control_port\": 17815, \"vids\": [], \"heartbeat_s\": 0.5}\n");
            r.Write(Path.Combine("hold", "spec.json"), "{\"boards\":[]}");
            var (hp, _) = Upd.Start(r, r.InstalledExe, new[] { "core", "--hub-dir", hold, "--core-mutex", SystemAccess.MutexNameFor(r.Root, "hold"),
                                                            "--sim", Path.Combine(r.Root, "hold", "spec.json"), "--sim-dll", TestEnv.TestsDll }, null);
            ctx.WaitUntil(() => { var j = HubFiles.ReadJson(Path.Combine(hold, "core.pid")); return j.HasValue && HubFiles.Str(j.Value, "control") == "listening"; }, 20000,
                          "옛 이미지를 쥔 프로세스: 설치본(vA) core --hub-dir <별도 폴더>(포트 17815, 보드 없음)");
            var (wp, wout) = Upd.Start(r, r.InstalledExe, new[] { "cli", "wait", "17810", "--pattern", "NEVER_MATCH_US106", "--timeout", "120", "--json" }, null);
            Thread.Sleep(1500);
            ctx.Check(!wp.HasExited, "긴 cli wait(설치본 vA 이미지) 대기 중");
            var u2 = r.Run(Upd.VB, "install");
            ctx.Check(u2.Code == 0 && u2.Out.Contains("v" + vA + " → v" + vB + " — 업데이트") && u2.Out.Contains("[ui] 확인 창(자동 응답=업데이트)") && u2.Out.Contains("# 업데이트 완료"),
                      "2차(vB): 업데이트 v" + vA + " → v" + vB + " exit 0 (" + u2.Ms + "ms)" + (u2.Code != 0 ? " — " + Upd.Tail(u2.Out) : ""));
            ctx.Check(wp.WaitForExit(15000) && wp.ExitCode == 103, "교체 중 긴 cli wait → 103 (rc " + (wp.HasExited ? wp.ExitCode.ToString(CultureInfo.InvariantCulture) : "아직 실행 중") + ")");
            string wo = wout();
            ctx.Check(wo.Contains("\"exit\":103"), "cli wait 출력: " + Upd.Tail(wo.Trim()));
            ctx.Check(u2.Out.Contains("교체 OK(1번째 시도): UartHub.exe = v" + vB + ", 백업 UartHub.old.exe = v" + vA), "File.Replace: 옛 이미지가 실행 중이어도 교체(백업 UartHub.old.exe)");
            ctx.WaitUntil(() => Upd.Count(Upd.HubLog(hub), "[sup] 설치 상태 일치") >= 3, 20000, "2차 뒤 새 감독 C-14 '설치 상태 일치'");
            ctx.Check(Upd.Ls(ctx, r, "2차 뒤", cabA) == ls0, "2차 뒤 포트표·status 같음(" + cabB + " released 그대로)");
            var st2 = Upd.Status(ctx, r, vB, "2차 뒤");
            ctx.Check(st2.SupPid != st1.SupPid && st2.CorePid != st1.CorePid, "2차: 감독·코어 PID 바뀜 · version == supervisor.version == core_version == v" + vB);
            ctx.Check(Upd.Keep(hub) == keep0, "2차: token·config.json SHA-256·state.json assign/released 같음");
            double g2 = Upd.Gap(ctx, logRoot, 17810, vB, "2차", out _);
            ctx.Check(g2 <= 15, "2차: COM 공백 " + g2.ToString("0.0", CultureInfo.InvariantCulture) + "초 ≤ 15초(가짜 WSL 지연 9초는 사전 점검 — 공백 밖)");
            ctx.Check(Upd.Sha(closed) == closedSha, "닫힌 로그 파일 " + Path.GetFileName(closed) + " 해시 그대로");
            var iv = r.InstallInfoJson().Value;
            ctx.Check(HubFiles.Str(iv, "version") == vB && HubFiles.Str(iv, "state") == "installed", "install.json version=" + vB + " state=installed");
            ctx.Check(Upd.Count(Upd.HubLog(hub), "Repair 시작") == 0 && !Upd.InstLog(r).Contains("== Repair("), "정상 경로(복구·업데이트 두 번) Repair 0회");
            string old = Path.Combine(r.InstallDir, "UartHub.old.exe");
            ctx.Check(File.Exists(old) && u2.Out.Contains("옛 이미지 UartHub.old.exe 잠겨 있음(실행 중) — 다음 감독 기동 때 지움"), "5 단계: 실행 중인 옛 이미지 UartHub.old.exe 는 지우지 못함 → 다음 감독 기동 때");

            // 옛 이미지를 쥔 프로세스가 끝난 뒤 다음 감독 기동(감독 kill → 코어 C-11 재기동) 때 삭제
            File.WriteAllText(Path.Combine(hold, "core.stop"), "{\"pid\":" + hp.Id + ",\"reason\":\"test\"}");
            ctx.Check(hp.WaitForExit(15000), "옛 이미지를 쥔 코어 정상 종료(core.stop)");
            var (tk, before) = Upd.KillSup(ctx, r, hub, "다음 감독 기동 유도");
            Upd.WaitRespawn(ctx, hub, before, 8000, "코어 감시가 새 감독(vB) 기동");
            ctx.WaitUntil(() => !File.Exists(old) && Upd.HubLog(hub).Contains("옛 이미지 삭제: UartHub.old.exe"), 10000, "다음 감독 기동 때 UartHub.old.exe 삭제(hub.log '옛 이미지 삭제')");
            ctx.Check(Upd.Files(r) == "UartHub.exe", "설치 폴더 exe = UartHub.exe 하나(" + Upd.Files(r) + ")");

            // 더 오래된 판(vA)을 다시 실행 → 확인 창에 표시(dry-run: 무변경)
            var snap = InstallRig.Snapshot(r.InstallDir);
            var d = r.Run(Dev, "install", "--dry-run");
            ctx.Check(d.Code == 0 && d.Out.Contains("v" + vB + " → v" + vA + " — 더 오래된 판으로 바꿈") && d.Out.Contains("주의: 받은 판 v" + vA + " 가 설치된 판 v" + vB + " 보다 오래되었습니다"),
                      "더 오래된 판 실행 → 확인 창 내용에 '더 오래된 판' 주의(dry-run)");
            ctx.Check(InstallRig.Diff(snap, InstallRig.Snapshot(r.InstallDir)) == "", "dry-run: 설치 폴더 무변경");
            r.CheckNoProcs("정리");
            real.Verify(ctx, r);
        }

        // ================================================================== install-rollback
        [Scenario("install-rollback", "실패 주입 매트릭스(UART_HUB_TEST_FAIL·UART_HUB_TEST_DELAY, C-13): shutdown 무응답 → core.stop(*** hub stop ***) → vB · core.stop 도 무응답 → 검증 PID kill(*** gap ***) → vB · 교체 재시도 소진 → vA · 새 sup 기동 실패 → 되돌림 vA(ls 같음) · 새 코어 크래시 루프(중단 임계) → 되돌림 vA · 되돌림 이름 바꾸기 실패 → bad·old 남고 exe 없음 · 옛 sup 기동 실패 → vA 파일 제자리·허브 꺼짐 · 잔재 A(1177 중 강제 종료) → 다시 실행이 old 복구 → vB · 잔재 B(교체 전 강제 종료) → 로그온 vA → 다시 실행 → vB · 잔재 C(마무리 전 강제 종료) → 감독 C-14 Repair 정확히 1회 · ReplaceFile 1176(계속·1회) · 1177(계속·1회·되돌림 실패) · 잔재 D(bad) 다음 실행이 지움 · exe 없음(old·new 없음) → 같은 버전 복구. 실제 시스템 무변경",
                  Order = 945, TimeoutS = 1500)]
        public static void InstallRollback(ScenarioContext ctx)
        {
            var real = RealSystem.Capture(ctx);
            var rigs = new List<InstallRig>();
            string vA = Hub.Version, vB = null;
            int n = 0;

            (InstallRig R, string Hub, string LogRoot, string Ls) Setup(string id)
            {
                int ctl = 17820 + 5 * n++;
                var r = InstallRig.Create(ctx, id, ctl, ctl + 2);
                rigs.Add(r);
                vB ??= r.Run(Upd.VB, "version").Out.Trim();
                Upd.InstallAndWait(ctx, r, Dev, null, 1, id + ": vA 설치");
                return (r, Upd.HubDir(r), Upd.LogRoot(r), Upd.Ls(ctx, r, id + ": vA", r.Cable));
            }
            Dictionary<string, string> Inj(string fail, string delay = "") => Upd.Env("UART_HUB_TEST_FAIL", fail, "UART_HUB_TEST_DELAY", delay, "UART_HUB_TEST_HEALTH_S", "15");
            void Running(InstallRig r, string ver, string ls0, string id)
            {
                Upd.Status(ctx, r, ver, id);
                ctx.Check(Upd.Ls(ctx, r, id, r.Cable) == ls0, id + ": v" + ver + " 동작, ls 같음");
            }
            string ExeVer(InstallRig r, string file) => r.Run(Path.Combine(r.InstallDir, file), "version").Out.Trim();
            void Done(InstallRig r) { r.KillAll(); }

            // ① shutdown 무응답 → core.stop 대체 경로 → vB
            {
                var (r, hub, lr, ls0) = Setup("shutdown");
                var u = r.Run(Upd.VB, new[] { "install" }, Inj("shutdown"));
                ctx.Check(u.Code == 0 && u.Out.Contains("감독 요청 shutdown → 무응답(시험 주입") && u.Out.Contains("대체 경로: 감독이 내리지 못함 → core.stop(") && u.Out.Contains("# 업데이트 완료"),
                          "① shutdown 무응답 → install.log 대체 경로 core.stop → 교체 → exit 0" + (u.Code != 0 ? " — " + Upd.Tail(u.Out) : ""));
                Running(r, vB, ls0, "①");
                Upd.Gap(ctx, lr, r.Base, vB, "①", out var after);
                ctx.Check(Upd.CableLines(lr, r.Base).Any(x => x.Line.Contains("*** hub stop ***")) && !after.Any(x => x.Contains("hub restart")), "① 케이블 로그: core.stop 정상 종료 *** hub stop *** 있음, vB 시작 뒤 restart·gap 없음");
                ctx.Check(Upd.Files(r) == "UartHub.exe", "① 설치 폴더 = UartHub.exe(" + Upd.Files(r) + ")");
                Done(r);
            }
            // ② core.stop 도 무응답 → 검증 PID kill → vB(*** gap ***)
            {
                var (r, hub, lr, ls0) = Setup("corestop");
                var u = r.Run(Upd.VB, new[] { "install" }, Inj("shutdown,core.stop"));
                ctx.Check(u.Code == 0 && u.Out.Contains("core.stop(pid=") && u.Out.Contains("무응답(시험 주입 UART_HUB_TEST_FAIL=core.stop") && Upd.Count(u.Out, "대체 경로: 검증 PID kill pid=") == 2,
                          "② core.stop 도 무응답 → 검증 PID kill 2개(감독 먼저·코어) → exit 0" + (u.Code != 0 ? " — " + Upd.Tail(u.Out) : ""));
                Running(r, vB, ls0, "②");
                Upd.Gap(ctx, lr, r.Base, vB, "②", out var after);
                ctx.Check(after.Any(x => x.Contains("*** hub restart (exit=unknown) ***")) && after.Any(x => Regex.IsMatch(x, @"\*\*\* gap \d+\.\d \(hub down\) \*\*\*")), "② 케이블 로그: vB 시작 뒤 *** hub restart (exit=unknown) *** + *** gap N (hub down) ***");
                Done(r);
            }
            // ③ new → exe 교체 재시도 소진 → vA
            {
                var (r, hub, lr, ls0) = Setup("replace");
                var u = r.Run(Upd.VB, new[] { "install" }, Inj("replace"));
                ctx.Check(u.Code == 1 && u.Out.Contains("교체 실패 20/20") && u.Out.Contains("== R. 교체하지 못함") && u.Out.Contains("[ui] 오류 창: 업데이트하지 못했습니다(파일 교체 실패)"),
                          "③ 교체 재시도 소진(20×250ms) → 교체 안 함 → 오류 창, exit 1" + (u.Code != 1 ? " — " + Upd.Tail(u.Out) : ""));
                ctx.Check(Upd.Files(r) == "UartHub.exe" && ExeVer(r, "UartHub.exe") == vA, "③ 파일: UartHub.exe = v" + vA + "(new 지움, old 없음) — " + Upd.Files(r));
                Running(r, vA, ls0, "③");
                ctx.Check(HubFiles.Str(r.InstallInfoJson().Value, "version") == vA, "③ install.json version 그대로 v" + vA);
                Done(r);
            }
            // ④ 새 sup 기동 실패 → 되돌림 vA, 이어서 잔재 D(bad) 를 다음 실행이 지움
            {
                var (r, hub, lr, ls0) = Setup("supstart");
                var u = r.Run(Upd.VB, new[] { "install" }, Inj("sup-start"));
                ctx.Check(u.Code == 1 && u.Out.Contains("== R. 되돌림: 새 감독 기동 실패") && u.Out.Contains("install --repair --quiet → rc=0") && u.Out.Contains("[ui] 오류 창: 업데이트하지 못해 이전 판 v" + vA + " 로 되돌렸습니다"),
                          "④ 새 sup 기동 실패 → 되돌림(bad·old → exe·옛 exe repair) → 오류 창, exit 1" + (u.Code != 1 ? " — " + Upd.Tail(u.Out) : ""));
                ctx.Check(Upd.Files(r) == "UartHub.bad.exe,UartHub.exe" && ExeVer(r, "UartHub.exe") == vA && ExeVer(r, "UartHub.bad.exe") == vB, "④ 파일: UartHub.exe = v" + vA + ", UartHub.bad.exe = v" + vB);
                Running(r, vA, ls0, "④");
                var u2 = r.Run(Upd.VB, "install");
                ctx.Check(u2.Code == 0 && u2.Out.Contains("잔재 D(되돌림이 남긴 UartHub.bad.exe) 지움") && Upd.Files(r) == "UartHub.exe", "잔재 D: 다음 설치기 실행이 UartHub.bad.exe 를 지우고 업데이트 → exit 0");
                Running(r, vB, ls0, "잔재 D 뒤");
                Done(r);
            }
            // ⑤ 새 코어 건강 확인 실패(크래시 루프 → 중단 임계, 시험 15초) → 되돌림 vA
            {
                var (r, hub, lr, ls0) = Setup("crashloop");
                var u = r.Run(Upd.VB, new[] { "install" }, Inj("core-crash"));
                ctx.Check(u.Code == 1 && u.Out.Contains("건강 확인 실패: 15초 안에 준비되지 않음") && u.Out.Contains("== R. 되돌림: 건강 확인 실패") && u.Out.Contains("[ui] 오류 창: 업데이트하지 못해 이전 판"),
                          "⑤ 새 코어 크래시 루프 → 중단 임계 초과 → 되돌림 → 오류 창, exit 1" + (u.Code != 1 ? " — " + Upd.Tail(u.Out) : ""));
                ctx.Check(Upd.HubLog(hub).Contains("시험 주입(UART_HUB_TEST_FAIL=core-crash)") && Upd.HubLog(hub).Contains("크래시 루프 경고"), "⑤ hub.log: vB 코어 즉시 종료 반복(크래시 루프)");
                Running(r, vA, ls0, "⑤");
                ctx.Check(Upd.Files(r) == "UartHub.bad.exe,UartHub.exe", "⑤ 파일: UartHub.exe(vA)·UartHub.bad.exe(vB)");
                Done(r);
            }
            // ⑥ 되돌림 이름 바꾸기 실패 → bad·old 남고 exe 없음 → 다시 실행이 복구(잔재 A + D)
            {
                var (r, hub, lr, ls0) = Setup("rbrename");
                var u = r.Run(Upd.VB, new[] { "install" }, Inj("sup-start,rollback-rename"));
                string box = Upd.ErrBox(u.Out);
                ctx.Check(u.Code == 1 && u.Out.Contains("되돌림 이름 바꾸기 실패") && box.Contains("받은 exe 를 다시 실행하면 복구됩니다") && !box.Contains("시작 메뉴"),
                          "⑥ 되돌림 이름 바꾸기 실패 → 오류 창 '받은 exe 를 다시 실행(복구)'(시작 메뉴 안내 없음), exit 1" + (u.Code != 1 ? " — " + Upd.Tail(u.Out) : ""));
                ctx.Check(Upd.Files(r) == "UartHub.bad.exe,UartHub.old.exe", "⑥ 파일: UartHub.bad.exe·UartHub.old.exe 남고 UartHub.exe 없음(" + Upd.Files(r) + ")");
                Upd.HubOff(ctx, r, "⑥");
                var u2 = r.Run(Upd.VB, "install");
                ctx.Check(u2.Code == 0 && u2.Out.Contains("[진행 창] 이전 업데이트가 끝나지 않아 먼저 복구함") && u2.Out.Contains("잔재 A(") && u2.Out.Contains("잔재 D(") && Upd.Files(r) == "UartHub.exe",
                          "⑥ 받은 exe 다시 실행 → 0 단계가 old 복구·bad 삭제 → 업데이트 exit 0");
                Running(r, vB, ls0, "⑥ 복구 뒤");
                Done(r);
            }
            // ⑦ 옛 sup 기동 실패 → vA 파일 제자리, 허브 꺼짐
            {
                var (r, hub, lr, ls0) = Setup("oldsup");
                var u = r.Run(Upd.VB, new[] { "install" }, Inj("sup-start,old-sup-start"));
                string box = Upd.ErrBox(u.Out);
                ctx.Check(u.Code == 1 && u.Out.Contains("옛 감독 기동 실패") && box.Contains("허브 꺼짐") && box.Contains("받은 exe 를 다시 실행"),
                          "⑦ 옛 sup 기동 실패 → 오류 창 '받은 exe 를 다시 실행', exit 1" + (u.Code != 1 ? " — " + Upd.Tail(u.Out) : ""));
                ctx.Check(ExeVer(r, "UartHub.exe") == vA && Upd.Files(r) == "UartHub.bad.exe,UartHub.exe", "⑦ vA 파일 제자리(UartHub.exe = v" + vA + ")");
                Upd.HubOff(ctx, r, "⑦");
                Thread.Sleep(3000);
                ctx.Check(Upd.Kind(r, "sup").Count == 0 && Upd.Kind(r, "core").Count == 0, "⑦ 3초 뒤에도 허브 꺼짐");
                Done(r);
            }
            // ⑧ 잔재 A: 1177 상태(UartHub.exe 없음 + old + new)에서 설치기 강제 종료 → 다시 실행이 복구
            {
                var (r, hub, lr, ls0) = Setup("residueA");
                var (p, o) = Upd.Start(r, Upd.VB, new[] { "install" }, Inj("replace-1177", "after-1177:60000"));
                ctx.WaitUntil(() => Upd.InstLog(r).Contains("[test] 지연 after-1177"), 60000, "⑧ 설치기가 1177 상태에서 멈춤");
                p.Kill(); p.WaitForExit(5000); o();
                ctx.Check(Upd.Files(r) == "UartHub.new.exe,UartHub.old.exe", "⑧ 강제 종료 뒤: UartHub.exe 없음 + old + new(" + Upd.Files(r) + ")");
                Upd.HubOff(ctx, r, "⑧");
                var lnk = ShellLink.Read(r.StartupLnk);
                ctx.Check(lnk != null && !File.Exists(lnk.Target), "⑧ 시작프로그램 바로가기 대상 없음(로그온 때 띄울 수 없음 — 받은 exe 다시 실행이 유일한 복구)");
                var u2 = r.Run(Upd.VB, "install");
                ctx.Check(u2.Code == 0 && u2.Out.Contains("[진행 창] 이전 업데이트가 끝나지 않아 먼저 복구함(잔재 A") && u2.Out.Contains("UartHub.new.exe 지움") && Upd.Files(r) == "UartHub.exe",
                          "⑧ 다시 실행 → old 를 UartHub.exe 로 되돌리고 new 를 지운 뒤 업데이트 → exit 0" + (u2.Code != 0 ? " — " + Upd.Tail(u2.Out) : ""));
                Running(r, vB, ls0, "⑧ 복구 뒤");
                Done(r);
            }
            // ⑨ 잔재 B: 교체 직전 강제 종료(UartHub.exe = vA + new) → 로그온 때 vA → 다시 실행 → vB
            {
                var (r, hub, lr, ls0) = Setup("residueB");
                var (p, o) = Upd.Start(r, Upd.VB, new[] { "install" }, Inj("", "pre-replace:60000"));
                ctx.WaitUntil(() => Upd.InstLog(r).Contains("[test] 지연 pre-replace"), 60000, "⑨ 설치기가 교체 직전(허브 정지 뒤)에서 멈춤");
                p.Kill(); p.WaitForExit(5000); o();
                ctx.Check(Upd.Files(r) == "UartHub.exe,UartHub.new.exe" && ExeVer(r, "UartHub.exe") == vA, "⑨ 강제 종료 뒤: UartHub.exe = v" + vA + " + new");
                Upd.HubOff(ctx, r, "⑨");
                var lnk = ShellLink.Read(r.StartupLnk);
                Upd.Start(r, lnk.Target, lnk.Arguments.Split(' '), null);
                Running(r, vA, ls0, "⑨ 로그온(시작프로그램 바로가기 대상·인자 그대로 실행)");
                var u2 = r.Run(Upd.VB, "install");
                ctx.Check(u2.Code == 0 && u2.Out.Contains("[진행 창] 이전 업데이트가 끝나지 않아 먼저 복구함(잔재 B") && u2.Out.Contains("잔재 B: UartHub.new.exe 지움") && Upd.Files(r) == "UartHub.exe",
                          "⑨ 다시 실행 → new 삭제 후 업데이트 → exit 0" + (u2.Code != 0 ? " — " + Upd.Tail(u2.Out) : ""));
                Running(r, vB, ls0, "⑨ 뒤");
                Done(r);
            }
            // ⑩ 잔재 C: 교체·건강 확인 뒤, 5 단계 전에 강제 종료 → vB 감독의 C-14 가 Repair 정확히 1회
            {
                var (r, hub, lr, ls0) = Setup("residueC");
                var (p, o) = Upd.Start(r, Upd.VB, new[] { "install" }, Inj("", "finalize:60000"));
                ctx.WaitUntil(() => Upd.InstLog(r).Contains("[test] 지연 finalize"), 60000, "⑩ 설치기가 5 단계 앞에서 멈춤(vB 기동·건강 확인 끝)");
                ctx.WaitUntil(() => Upd.Count(Upd.HubLog(hub), "[sup] 설치기 마무리 대기") >= 2, 10000, "⑩ vB 감독: 설치기 마무리 대기(Repair 안 함)");
                ctx.Check(Upd.Count(Upd.HubLog(hub), "Repair 시작") == 0 && File.Exists(Path.Combine(r.InstallDir, "UartHub.old.exe")), "⑩ 설치기 생존 중 Repair 0, UartHub.exe(vB) + old");
                p.Kill(); p.WaitForExit(5000); o();
                ctx.WaitUntil(() => Upd.HubLog(hub).Contains("Repair 끝 rc=0"), 15000, "⑩ 설치기가 죽음 → 감독 C-14 다시 판정 → Repair");
                Thread.Sleep(3000);
                string hl = Upd.HubLog(hub);
                ctx.Check(Upd.Count(hl, "Repair 시작") == 1 && hl.Contains("설치 상태 불일치(install.json version=" + vA + " ≠ " + vB + ")") && Upd.Count(hl, "풍선: 허브 설정을 복구했습니다.") == 1,
                          "⑩ Repair 정확히 1회(version " + vA + " ≠ " + vB + ") · 트레이 풍선 1회");
                var iv = r.InstallInfoJson().Value;
                ctx.Check(HubFiles.Str(iv, "version") == vB && HubFiles.Str(iv, "state") == "installed" && Upd.Files(r) == "UartHub.exe", "⑩ Repair 결과: install.json version=" + vB + " installed, UartHub.old.exe 삭제");
                Running(r, vB, ls0, "⑩");
                Done(r);
            }
            // ⑪ ReplaceFile 1176(매번): 두 파일 제자리 → 재시도 → 소진 → vA
            {
                var (r, hub, lr, ls0) = Setup("r1176");
                var u = r.Run(Upd.VB, new[] { "install" }, Inj("replace-1176"));
                ctx.Check(u.Code == 1 && Upd.Count(u.Out, "(1176)") == 20 && u.Out.Contains("[ui] 오류 창: 업데이트하지 못했습니다(파일 교체 실패)") && Upd.Files(r) == "UartHub.exe" && ExeVer(r, "UartHub.exe") == vA,
                          "⑪ 1176 ×20 → 잔재 B 와 같은 상태로 재시도 → 소진 → UartHub.exe = v" + vA + "·오류 창" + (u.Code != 1 ? " — " + Upd.Tail(u.Out) : ""));
                Running(r, vA, ls0, "⑪");
                Done(r);
            }
            // ⑫ ReplaceFile 1176(첫 시도만) → 재시도로 교체 → vB
            {
                var (r, hub, lr, ls0) = Setup("r1176once");
                var u = r.Run(Upd.VB, new[] { "install" }, Inj("replace-1176-once"));
                ctx.Check(u.Code == 0 && Upd.Count(u.Out, "(1176)") == 1 && u.Out.Contains("교체 OK(2번째 시도)"), "⑫ 1176 한 번 → 2번째 시도에 교체 → exit 0");
                Running(r, vB, ls0, "⑫");
                Done(r);
            }
            // ⑬ ReplaceFile 1177(매번): 원본만 old 로 옮겨진 상태 → 즉시 되돌림 → 재시도 → 소진 → vA
            {
                var (r, hub, lr, ls0) = Setup("r1177");
                var u = r.Run(Upd.VB, new[] { "install" }, Inj("replace-1177"));
                ctx.Check(u.Code == 1 && Upd.Count(u.Out, "(1177)") == 20 && Upd.Count(u.Out, "→ UartHub.exe 즉시 되돌림(1177 상태 해소)") == 20 && Upd.Files(r) == "UartHub.exe" && ExeVer(r, "UartHub.exe") == vA,
                          "⑬ 1177 ×20 → 매번 old 를 즉시 되돌림 → 소진 → UartHub.exe = v" + vA + "·오류 창" + (u.Code != 1 ? " — " + Upd.Tail(u.Out) : ""));
                Running(r, vA, ls0, "⑬");
                Done(r);
            }
            // ⑭ ReplaceFile 1177(첫 시도만) → 되돌린 뒤 재시도로 교체 → vB
            {
                var (r, hub, lr, ls0) = Setup("r1177once");
                var u = r.Run(Upd.VB, new[] { "install" }, Inj("replace-1177-once"));
                ctx.Check(u.Code == 0 && Upd.Count(u.Out, "즉시 되돌림(1177 상태 해소)") == 1 && u.Out.Contains("교체 OK(2번째 시도)") && Upd.Files(r) == "UartHub.exe", "⑭ 1177 한 번 → 되돌림 → 2번째 시도에 교체 → exit 0");
                Running(r, vB, ls0, "⑭");
                Done(r);
            }
            // ⑮ ReplaceFile 1177 + 되돌리지 못함 → '되돌림 이름 바꾸기 실패' 행(UartHub.exe 없음, old·new 남음)
            {
                var (r, hub, lr, ls0) = Setup("r1177stuck");
                var u = r.Run(Upd.VB, new[] { "install" }, Inj("replace-1177,rollback-rename"));
                ctx.Check(u.Code == 1 && u.Out.Contains("되돌리지 못함") && Upd.ErrBox(u.Out).Contains("받은 exe 를 다시 실행하면 복구됩니다") && Upd.Files(r) == "UartHub.new.exe,UartHub.old.exe",
                          "⑮ 1177 뒤 되돌림 실패 → UartHub.exe 없음·old·new 남음·오류 창 '받은 exe 를 다시 실행'" + (u.Code != 1 ? " — " + Upd.Tail(u.Out) : ""));
                Upd.HubOff(ctx, r, "⑮");
                Done(r);
            }
            // ⑯ UartHub.exe 없음 + old·new 없음(사용자가 지움) → 같은 버전 업데이트(복구) 경로로 새로 놓음
            {
                var (r, hub, lr, ls0) = Setup("noexe");
                string keep0 = Upd.Keep(hub);
                var sd = SupervisorChannel.Request(hub, "shutdown", "test-noexe", 10000);
                ctx.Check(sd.ToString().Contains("\"status\":\"ok\""), "⑯ 허브 shutdown(시험)");
                Upd.HubOff(ctx, r, "⑯");
                File.Delete(r.InstalledExe);
                var u = r.Run(Upd.VB, "install");
                ctx.Check(u.Code == 0 && u.Out.Contains("UartHub.exe 없음(old·new 도 없음") && u.Out.Contains("[진행 창] 복구: UartHub.exe 가 없어 새로 놓음") && u.Out.Contains("(되돌릴 백업 없음)"),
                          "⑯ exe 없음 → 진행 창 '복구' → 새로 놓음 → exit 0" + (u.Code != 0 ? " — " + Upd.Tail(u.Out) : ""));
                Running(r, vB, ls0, "⑯");
                ctx.Check(Upd.Keep(hub) == keep0, "⑯ 포트·설정·토큰 그대로(" + keep0 + ")");
                Done(r);
            }
            foreach (var r in rigs) r.CheckNoProcs("정리 " + Path.GetFileName(r.Root));
            real.Verify(ctx, rigs.ToArray());
        }

        // ================================================================== finalize-race
        [Scenario("finalize-race", "C-14 설치기 마무리 경쟁(첫 설치 7 단계 지연 UART_HUB_TEST_DELAY=finalize): ① 지연 뒤 정상 종료 → 감독은 '설치기 마무리 대기'만, 끝난 뒤 다시 판정해도 Repair 0 ② 지연 중 설치기 kill → Repair 정확히 1회(install.json installed·이 판) ③ 상한(시험 5초) 초과 멈춤 → Repair 0 + 경고·풍선, 설치기가 끝난 뒤 일치 ④ 상한 초과 후 설치기 kill → 1초 간격 재확인으로 Repair 정확히 1회. 실제 시스템 무변경",
                  Order = 950, TimeoutS = 420)]
        public static void FinalizeRace(ScenarioContext ctx)
        {
            var real = RealSystem.Capture(ctx);
            var rigs = new List<InstallRig>();
            string v = Hub.Version;
            InstallRig Rig(string id, int ctl) { var r = InstallRig.Create(ctx, id, ctl, ctl + 2); rigs.Add(r); return r; }
            string HubOf(InstallRig r) => Path.Combine(r.UserProfile, "uart_logs", "hub");

            // ①
            {
                var r = Rig("delay", 17900);
                var e = r.Run(Dev, new[] { "install" }, Upd.Env("UART_HUB_TEST_DELAY", "finalize:6000"));
                ctx.Check(e.Code == 0 && e.Out.Contains("[test] 지연 finalize 6000ms"), "① 7 단계 6초 지연 뒤 설치 exit 0");
                ctx.WaitUntil(() => Upd.HubLog(HubOf(r)).Contains("[sup] 설치 상태 일치"), 15000, "① 설치기가 끝난 뒤 감독이 다시 판정");
                string hl = Upd.HubLog(HubOf(r));
                int w = hl.IndexOf("[sup] 설치기 마무리 대기", StringComparison.Ordinal), f = hl.IndexOf("[sup] 설치기 끝남", StringComparison.Ordinal), ok = hl.IndexOf("[sup] 설치 상태 일치", StringComparison.Ordinal);
                ctx.Check(w >= 0 && w < f && f < ok && Upd.Count(hl, "Repair 시작") == 0, "① 순서: 설치기 마무리 대기 → 설치기 끝남 → 설치 상태 일치, Repair 0");
                r.KillAll();
            }
            // ②
            {
                var r = Rig("kill", 17905);
                var (p, o) = Upd.Start(r, Dev, new[] { "install" }, Upd.Env("UART_HUB_TEST_DELAY", "finalize:30000"));
                ctx.WaitUntil(() => Upd.InstLog(r).Contains("[test] 지연 finalize") && Upd.HubLog(HubOf(r)).Contains("[sup] 설치기 마무리 대기"), 40000, "② 설치기 7 단계 지연 중·감독은 마무리 대기");
                ctx.Check(HubFiles.Str(r.InstallInfoJson().Value, "state") == "installing", "② 이때 install.json state=installing");
                p.Kill(); p.WaitForExit(5000); o();
                ctx.WaitUntil(() => Upd.HubLog(HubOf(r)).Contains("Repair 끝 rc=0"), 15000, "② 설치기 kill → 감독 C-14 Repair");
                Thread.Sleep(3000);
                string hl = Upd.HubLog(HubOf(r));
                var iv = r.InstallInfoJson().Value;
                ctx.Check(Upd.Count(hl, "Repair 시작") == 1 && hl.Contains("install.json state=installing(설치기가 마무리 전에 끝남)") && HubFiles.Str(iv, "state") == "installed" && HubFiles.Str(iv, "version") == v,
                          "② Repair 정확히 1회 → install.json installed·v" + v);
                r.KillAll();
            }
            // ③
            {
                var r = Rig("stall", 17910);
                var e = r.Run(Dev, new[] { "install" }, Upd.Env("UART_HUB_TEST_DELAY", "finalize:10000", "UART_HUB_TEST_C14", "5,1"));
                ctx.Check(e.Code == 0, "③ 7 단계 10초 지연(상한 5초 초과) 뒤 설치 exit 0");
                ctx.WaitUntil(() => Upd.HubLog(HubOf(r)).Contains("[sup] 설치 상태 일치"), 15000, "③ 설치기가 끝난 뒤 감독이 다시 판정");
                string hl = Upd.HubLog(HubOf(r));
                int warn = hl.IndexOf("경고: 설치기가 5초 안에 끝나지 않음", StringComparison.Ordinal), f = hl.IndexOf("[sup] 설치기 끝남", StringComparison.Ordinal);
                ctx.Check(warn >= 0 && warn < f && hl.Contains("풍선: 등록이 끝나지 않았습니다. 열린 UART 허브 창을 확인하거나 exe를 다시 실행해 주세요.") && Upd.Count(hl, "Repair 시작") == 0, "③ 상한 초과 → 경고·풍선 1회, Repair 0 → 설치기가 끝난 뒤 일치");
                r.KillAll();
            }
            // ④
            {
                var r = Rig("stallkill", 17915);
                var (p, o) = Upd.Start(r, Dev, new[] { "install" }, Upd.Env("UART_HUB_TEST_DELAY", "finalize:40000", "UART_HUB_TEST_C14", "5,1"));
                ctx.WaitUntil(() => Upd.HubLog(HubOf(r)).Contains("경고: 설치기가 5초 안에 끝나지 않음"), 40000, "④ 상한(5초) 초과 경고");
                ctx.Check(Upd.Count(Upd.HubLog(HubOf(r)), "Repair 시작") == 0, "④ 경고 뒤에도 설치기 생존 중 Repair 0");
                p.Kill(); p.WaitForExit(5000); o();
                var tk = DateTime.Now;
                ctx.WaitUntil(() => Upd.HubLog(HubOf(r)).Contains("Repair 끝 rc=0"), 10000, "④ 상한 뒤 설치기 kill → 1초 간격 재확인 → Repair");
                Thread.Sleep(3000);
                var iv = r.InstallInfoJson().Value;
                ctx.Check(Upd.Count(Upd.HubLog(HubOf(r)), "Repair 시작") == 1 && HubFiles.Str(iv, "state") == "installed", "④ Repair 정확히 1회 → installed");
                r.KillAll();
            }
            foreach (var r in rigs) r.CheckNoProcs("정리 " + Path.GetFileName(r.Root));
            real.Verify(ctx, rigs.ToArray());
        }

        // ================================================================== update-shutdown
        [Scenario("update-shutdown", "감독 요청 shutdown(C-5): 운영 모드 허브 → 코어 exit 0(*** hub stop ***)·감독 끝·control.json 그대로(없으면 없는 채)·6초 동안 재시작 없음(코어 C-11 도 끝남). 정지(stopped) 허브 → 감독 끝·control.json stopped:true 바이트 그대로·재시작 없음. 실제 시스템 무변경",
                  Order = 955, TimeoutS = 240)]
        public static void UpdateShutdown(ScenarioContext ctx)
        {
            var real = RealSystem.Capture(ctx);
            var r = InstallRig.Create(ctx, "root", 17920, 17922);
            Upd.InstallAndWait(ctx, r, Dev, null, 1, "설치");
            string hub = Upd.HubDir(r), lr = Upd.LogRoot(r), ctl = HubFiles.Control(hub);
            Upd.Ls(ctx, r, "설치 뒤", r.Cable);
            string c0 = File.Exists(ctl) ? File.ReadAllText(ctl) : null;
            var resp = SupervisorChannel.Request(hub, "shutdown", "update-shutdown", 10000).ToString();
            ctx.Check(resp.Contains("\"status\":\"ok\"") && resp.Contains("\"action\":\"shutting_down\""), "shutdown 요청 → ok(shutting_down)");
            Upd.HubOff(ctx, r, "shutdown");
            string hl = Upd.HubLog(hub);
            ctx.Check(hl.Contains("[sup] 종료 요청(update-shutdown) — control.json 은 그대로") && hl.Contains("[core] 코어 정상 종료(exit 0)") && hl.Contains("종료 요청 처리: 코어 종료(exit=0) → 감독 끝냄(control.json 그대로)"),
                      "hub.log: 코어 정상 종료(exit 0) → 감독 끝냄");
            ctx.Check((File.Exists(ctl) ? File.ReadAllText(ctl) : null) == c0, "control.json 그대로(" + (c0 == null ? "없음" : c0.Trim()) + ")");
            ctx.Check(Upd.CableLines(lr, r.Base).Last().Line.EndsWith("*** hub stop ***", StringComparison.Ordinal), "케이블 로그 마지막 줄 *** hub stop ***");
            int sups = Upd.Count(Upd.HubLog(hub), "[sup] 감독 시작");
            Thread.Sleep(6000);
            ctx.Check(Upd.Kind(r, "sup").Count == 0 && Upd.Kind(r, "core").Count == 0 && Upd.Count(Upd.HubLog(hub), "[sup] 감독 시작") == sups && Upd.Respawns(hub).Count == 0, "6초 동안 재시작 없음");

            // 정지 상태에서 shutdown
            var lnk = ShellLink.Read(r.StartupLnk);
            Upd.Start(r, lnk.Target, lnk.Arguments.Split(' '), null);
            Upd.Ls(ctx, r, "로그온 뒤", r.Cable);
            var h = r.Run(r.InstalledExe, "cli", "hub", "stop", "--json");
            ctx.Check(h.Code == 0, "cli hub stop");
            ctx.WaitUntil(() => HubFiles.IsStopped(hub) && Upd.Kind(r, "core").Count == 0, 15000, "정지 상태(control.json stopped·코어 끝)");
            string c1 = File.ReadAllText(ctl);
            var resp2 = SupervisorChannel.Request(hub, "shutdown", "update-shutdown-2", 10000).ToString();
            ctx.Check(resp2.Contains("\"status\":\"ok\"") && resp2.Contains("\"stopped\":true"), "정지 상태 shutdown → ok");
            Upd.HubOff(ctx, r, "정지 상태 shutdown");
            ctx.Check(File.ReadAllText(ctl) == c1 && c1.Contains("\"stopped\":true"), "control.json stopped:true 바이트 그대로");
            Thread.Sleep(6000);
            ctx.Check(Upd.Kind(r, "sup").Count == 0 && Upd.Kind(r, "core").Count == 0, "6초 동안 재시작 없음");
            r.CheckNoProcs("정리");
            real.Verify(ctx, r);
        }

        // ================================================================== sup-killed-respawn
        [Scenario("sup-killed-respawn", "C-11 무인 복구(C-13 아래 운영 모드 + FakeBoard, sup_watch_s 2초·백오프 시험값 2→4→8초·즉사 창 5초): 감독 kill → 2초(+기동 0.5초) 안에 코어가 [core] sup respawn → 새 감독이 기존 코어 입양(코어 1개·PID 그대로). update mutex 가 있는 동안·install.json installing 동안 다시 띄우지 않음(풀리면 다음 회차에 기동). 띄운 감독이 연달아 즉사하면 간격이 늘어남(2·4·8초 백오프). --hub-dir 를 받은 LifeRig(운영 모드 아님)는 다시 띄우지 않음. 실제 시스템 무변경",
                  Order = 960, TimeoutS = 400)]
        public static void SupKilledRespawn(ScenarioContext ctx)
        {
            var real = RealSystem.Capture(ctx);
            var r = InstallRig.Create(ctx, "root", 17930, 17932);
            Upd.InstallAndWait(ctx, r, Dev, Upd.Env("UART_HUB_TEST_RESPAWN", "2,4,8,16,5,30"), 1, "설치(운영 모드)");
            string hub = Upd.HubDir(r);
            ctx.Check(Upd.HubLog(hub).Contains("[core] 감독 감시 시작(sup_watch_s=2,") && Upd.HubLog(hub).Contains("즉사 백오프 2→4→8→16초"), "코어 감독 감시(sup_watch_s=2, 시험 백오프)");
            var cores = Upd.Kind(r, "core");
            ctx.Check(cores.Count == 1, "코어 1개");
            int corePid = cores[0];
            string adoptKey = "기존 코어 입양 pid=" + corePid + " ";

            // ① 감독 kill → 2초 안 재기동·입양
            int adopt0 = Upd.Count(Upd.HubLog(hub), adoptKey);
            var (t0, b0) = Upd.KillSup(ctx, r, hub, "①");
            var s1 = Upd.WaitRespawn(ctx, hub, b0, 8000, "①");
            double lat = (s1.T - t0).TotalSeconds;
            ctx.Check(lat <= 2.5, "① 감독 kill → " + lat.ToString("0.00", CultureInfo.InvariantCulture) + "초 뒤 [core] sup respawn (sup_watch_s 2초 + 기동 0.5초 이내)");
            ctx.WaitUntil(() => Upd.Count(Upd.HubLog(hub), adoptKey) > adopt0, 10000, "① 새 감독이 기존 코어 pid=" + corePid + " 입양");
            ctx.Check(Upd.Kind(r, "core").SequenceEqual(new[] { corePid }) && Upd.Kind(r, "sup").SequenceEqual(new[] { s1.Pid }), "① 코어 1개(PID 그대로)·감독 1개(새 pid " + s1.Pid + ")");

            // ② update mutex 동안 재기동 없음 → 닫으면 다음 회차에 기동
            Upd.SleepSince(s1.T, 6000);
            int b1;
            using (var um = new Mutex(false, SystemAccess.MutexNameFor(r.Root, "update")))
            {
                ctx.Check(um.WaitOne(0), "② update mutex(시험 이름) 획득");
                (_, b1) = Upd.KillSup(ctx, r, hub, "②");
                Thread.Sleep(5000);
                ctx.Check(Upd.Respawns(hub).Count == b1 && Upd.Kind(r, "sup").Count == 0, "② update mutex 가 있는 동안 5초(감시 2회+) 재기동 0");
                um.ReleaseMutex();
            }
            var tr = DateTime.Now;
            var s2 = Upd.WaitRespawn(ctx, hub, b1, 8000, "② update mutex 핸들을 닫은 뒤");
            ctx.Check((s2.T - tr).TotalSeconds <= 2.5, "② 핸들 닫고 " + (s2.T - tr).TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + "초 뒤 기동(다음 회차)");

            // ③ install.json state=installing 이면 재기동 없음
            Upd.SleepSince(s2.T, 6000);
            ctx.WaitUntil(() => Upd.Kind(r, "sup").Count == 1, 10000, "③ 감독 1개");
            Upd.SetInstall(r, "state", "installing");
            var (t3, b3) = Upd.KillSup(ctx, r, hub, "③");
            Thread.Sleep(5000);
            ctx.Check(Upd.Respawns(hub).Count == b3 && Upd.Kind(r, "sup").Count == 0 && Upd.HubLog(hub).Contains("[core] 감독 없음 — 재기동하지 않음: install.json state=installing"),
                      "③ install.json state=installing 동안 재기동 0(hub.log 사유)");
            Upd.SetInstall(r, "state", "installed");
            var s3 = Upd.WaitRespawn(ctx, hub, b3, 8000, "③ installed 로 돌린 뒤");

            // ④ 띄운 감독이 연달아 즉사 → 백오프
            var chain = new List<DateTime> { s3.T };
            int last = s3.Pid;
            for (int k = 1; k <= 3; k++)
            {
                int before = Upd.Respawns(hub).Count;
                Upd.KillPid(ctx, r, last, "④ 띄운 감독 즉시 kill " + k);
                var nx = Upd.WaitRespawn(ctx, hub, before, 25000, "④ 다음 기동 " + k);
                chain.Add(nx.T); last = nx.Pid;
            }
            var gaps = chain.Zip(chain.Skip(1), (a, b) => (b - a).TotalSeconds).ToList();
            string gs = string.Join(" → ", gaps.Select(x => x.ToString("0.0", CultureInfo.InvariantCulture)));
            string hl = Upd.HubLog(hub);
            int i2 = hl.IndexOf("(연속 1회) → 다음 기동은 2초 뒤", StringComparison.Ordinal), i4 = hl.IndexOf("(연속 2회) → 다음 기동은 4초 뒤", StringComparison.Ordinal), i8 = hl.IndexOf("(연속 3회) → 다음 기동은 8초 뒤", StringComparison.Ordinal);
            ctx.Check(gaps[0] < gaps[1] && gaps[1] < gaps[2] && i2 >= 0 && i2 < i4 && i4 < i8, "④ 연속 즉사 → 기동 간격이 늘어남 " + gs + "초(hub.log 백오프 2→4→8초)");
            ctx.WaitUntil(() => Upd.Kind(r, "sup").SequenceEqual(new[] { last }), 10000, "④ 마지막 감독은 살아 남음");
            ctx.Check(Upd.Kind(r, "core").SequenceEqual(new[] { corePid }), "①~④ 내내 코어 PID " + corePid + " 그대로(코어는 한 번도 재시작 안 함)");

            // ⑤ LifeRig(--hub-dir, 운영 모드 아님)
            var lrg = LifeRig.Create(ctx, "respawn", 17935, 17937, "SIMRSP935", "COM935", "SIMR935");
            lrg.StartSup();
            lrg.WaitCore(0, 20000, "⑤ LifeRig 코어");
            foreach (int pid in lrg.SupProcs()) LifeRig.Kill(pid);
            Thread.Sleep(5000);
            string ll = lrg.HubLogText();
            ctx.Check(lrg.SupProcs().Count == 0 && lrg.CoreProcs().Count == 1 && !ll.Contains("감독 감시 시작") && !ll.Contains("sup respawn"), "⑤ LifeRig(--hub-dir): 감독 kill 뒤 5초 동안 재기동 없음(코어에 --watch-sup 없음)");
            lrg.CheckNoLeftovers();
            r.CheckNoProcs("정리");
            real.Verify(ctx, r);
        }

        // ================================================================== mutex-race
        [Scenario("mutex-race", "C-8 mutex 존재 확인(TryOpenExisting 후 즉시 닫기): ① 감독이 mutex 획득 직전에 멈춘 상태(UART_HUB_TEST_DELAY=sup-pre-mutex:3000)에서 cli(코어에 닿지 못함)와 코어 감시가 동시에 감독을 띄움 → 결국 감독 1개·코어 입양 1번 ② update mutex 동안 감시·cli 0회 기동, 그때 직접 띄운 sup 은 바로 exit 0 ③ 핸들을 닫은 직후 다음 감시 회차에 기동 ④ 다른 확인자의 핸들로 생긴 거짓 '존재' 동안은 건너뛰고, 핸들이 닫힌 다음 회차에 회복. 실제 시스템 무변경",
                  Order = 965, TimeoutS = 300)]
        public static void MutexRace(ScenarioContext ctx)
        {
            var real = RealSystem.Capture(ctx);
            var r = InstallRig.Create(ctx, "root", 17940, 17942);
            var env = Upd.Env("UART_HUB_TEST_DELAY", "sup-pre-mutex:3000", "UART_HUB_TEST_RESPAWN", "2,4,8,16,5,30");
            Upd.InstallAndWait(ctx, r, Dev, env, 1, "설치(감독은 mutex 획득 직전 3초 멈춤 — 코어까지 환경 상속)");
            string hub = Upd.HubDir(r);
            int corePid = Upd.Kind(r, "core").Single();
            var cliEnv = Upd.Env("UART_HUB_TEST_DELAY", "sup-pre-mutex:3000", "UART_HUB_CONTROL_PORT", "17949");
            int Adopt() => Upd.Count(Upd.HubLog(hub), "기존 코어 입양 pid=" + corePid + " ");

            // ①
            int a0 = Adopt();
            var (t0, b0) = Upd.KillSup(ctx, r, hub, "①");
            var c = r.Run(r.InstalledExe, new[] { "cli", "ls", "--json" }, cliEnv);
            var cj = Upd.Parse(c.Out);
            var pm = Regex.Match(Upd.S(cj, "error") ?? "", @"다시 띄움\(pid (\d+)");
            ctx.Check(c.Code == 103 && Upd.True(cj, "restarted") && pm.Success, "① cli(제어 포트 17949 = 코어에 닿지 못함) → 103 restarted:true, 감독 분리 기동");
            int cliSup = int.Parse(pm.Groups[1].Value, CultureInfo.InvariantCulture);
            var cs = Upd.WaitRespawn(ctx, hub, b0, 6000, "① 코어 감시도 감독을 띄움(cli 의 감독이 아직 mutex 전)");
            ctx.Check(cs.Pid != cliSup, "① 동시 기동 감독 2개: cli pid " + cliSup + " · 코어 감시 pid " + cs.Pid);
            ctx.WaitUntil(() => Adopt() > a0 && Upd.Kind(r, "sup").Count == 1, 15000, "① 하나만 감독 mutex 를 잡고 나머지는 끝남");
            Thread.Sleep(2500);
            var sups = Upd.Kind(r, "sup");
            ctx.Check(sups.Count == 1 && (sups[0] == cliSup || sups[0] == cs.Pid) && Adopt() == a0 + 1 && Upd.Kind(r, "core").SequenceEqual(new[] { corePid }),
                      "① 결국 감독 1개(pid " + string.Join(",", sups) + ")·코어 입양 1번·코어 1개(PID 그대로)");

            // ②
            Upd.SleepSince(cs.T, 6000);
            var um = new Mutex(false, SystemAccess.MutexNameFor(r.Root, "update"));
            ctx.Check(um.WaitOne(0), "② update mutex(시험 이름) 획득");
            var (t1, b1) = Upd.KillSup(ctx, r, hub, "②");
            var c2 = r.Run(r.InstalledExe, new[] { "cli", "ls", "--json" }, cliEnv);
            var c2j = Upd.Parse(c2.Out);
            ctx.Check(c2.Code == 103 && !Upd.True(c2j, "restarted"), "② update mutex 동안 cli → 103, restarted 없음(기동 0)");
            var d = r.Run(r.InstalledExe, new[] { "sup" }, env);
            ctx.Check(d.Code == 0 && d.Ms < 2500, "② update mutex 동안 직접 띄운 sup → 바로 exit 0 (" + d.Ms + "ms — mutex 앞 3초 지연 전에 끝남)");
            Thread.Sleep(5000);
            ctx.Check(Upd.Respawns(hub).Count == b1 && Upd.Kind(r, "sup").Count == 0, "② update mutex 동안 5초(감시 2회+) 코어 감시 기동 0·감독 0");
            ctx.Log("② open 은 T3(US-107 이후)의 하위 명령이라 이 판에 없음 — 넣을 때 cli 와 같은 판정(CoreMain.RespawnExe + 감독·update mutex 존재 확인)을 쓴다");

            // ③
            um.ReleaseMutex(); um.Dispose();
            var tr = DateTime.Now;
            var s3 = Upd.WaitRespawn(ctx, hub, b1, 6000, "③ 핸들을 닫은 뒤");
            ctx.Check((s3.T - tr).TotalSeconds <= 2.5, "③ 핸들 닫고 " + (s3.T - tr).TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + "초 뒤 다음 감시 회차에 기동");
            ctx.WaitUntil(() => Adopt() > a0 + 1 && Upd.Kind(r, "sup").Count == 1, 10000, "③ 새 감독이 코어 입양");

            // ④
            Upd.SleepSince(s3.T, 6000);
            var fake = new Mutex(false, SystemAccess.MutexNameFor(r.Root, "sup"));   // 다른 확인자가 이 이름의 핸들을 쥔 상태(소유하지 않음)
            var (t4, b4) = Upd.KillSup(ctx, r, hub, "④");
            Thread.Sleep(4500);
            ctx.Check(Upd.Respawns(hub).Count == b4, "④ 거짓 '존재'(감독은 죽었지만 핸들이 남음) 동안 그 회차들은 건너뜀");
            fake.Dispose();
            var tf = DateTime.Now;
            var s4 = Upd.WaitRespawn(ctx, hub, b4, 6000, "④ 핸들이 닫힌 뒤");
            ctx.Check((s4.T - tf).TotalSeconds <= 2.5, "④ " + (s4.T - tf).TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + "초 뒤 다음 회차에 회복");
            ctx.WaitUntil(() => Upd.Kind(r, "sup").Count == 1 && Upd.Kind(r, "core").SequenceEqual(new[] { corePid }), 10000, "④ 감독 1개·코어 그대로");
            r.CheckNoProcs("정리");
            real.Verify(ctx, r);
        }

        // ================================================================== selfheal-prod
        [Scenario("selfheal-prod", "C-9 운영 경로(C-13 아래 UART_HUB_DIR 없이 cli): 감독만 kill(코어 살아 있음) → cli ls 정상(103 아님). 코어·감독 모두 kill → cli ls 103 restarted:true → 5초 뒤 cli ls 정상. 정지(stopped) 상태 → 감독이 있든 없든 103 stopped:true, 감독을 새로 띄우지 않음. 실제 시스템 무변경",
                  Order = 970, TimeoutS = 240)]
        public static void SelfhealProd(ScenarioContext ctx)
        {
            var real = RealSystem.Capture(ctx);
            var r = InstallRig.Create(ctx, "root", 17950, 17952);
            Upd.InstallAndWait(ctx, r, Dev, Upd.Env("UART_HUB_TEST_RESPAWN", "2,4,8,16,5,30"), 1, "설치");
            string hub = Upd.HubDir(r);
            Upd.Ls(ctx, r, "설치 뒤", r.Cable);

            // 감독만 kill
            var (t0, b0) = Upd.KillSup(ctx, r, hub, "감독만");
            var a = r.Run(r.InstalledExe, "cli", "ls", "--json");
            ctx.Check(a.Code == 0 && !a.Out.Contains("restarted"), "감독만 kill·코어 살아 있음 → cli ls 정상(rc 0, 103 아님)");
            var s = Upd.WaitRespawn(ctx, hub, b0, 8000, "감독은 코어가 다시 띄움(C-11)");
            ctx.WaitUntil(() => Upd.Kind(r, "sup").Count == 1, 10000, "감독 1개");

            // 코어·감독 모두 kill
            Upd.SleepSince(s.T, 6000);
            int sp = Upd.Kind(r, "sup").Single(), cp = Upd.Kind(r, "core").Single();
            Upd.KillPid(ctx, r, sp, "감독");
            Upd.KillPid(ctx, r, cp, "코어");
            var b = r.Run(r.InstalledExe, "cli", "ls", "--json");
            var bj = Upd.Parse(b.Out);
            ctx.Check(b.Code == 103 && Upd.True(bj, "restarted") && !Upd.True(bj, "stopped"), "코어·감독 모두 kill → cli ls 103 restarted:true (" + b.Out.Trim() + ")");
            Thread.Sleep(5000);
            var c = r.Run(r.InstalledExe, "cli", "ls", "--json");
            ctx.Check(c.Code == 0, "5초 뒤 cli ls 정상(rc " + c.Code + ")");
            ctx.Check(Upd.Kind(r, "sup").Count == 1 && Upd.Kind(r, "core").Count == 1, "감독 1개·코어 1개");

            // 정지 상태
            var h = r.Run(r.InstalledExe, "cli", "hub", "stop", "--json");
            ctx.Check(h.Code == 0, "cli hub stop");
            ctx.WaitUntil(() => HubFiles.IsStopped(hub) && Upd.Kind(r, "core").Count == 0, 15000, "정지(control.json stopped·코어 끝)");
            var d = r.Run(r.InstalledExe, "cli", "ls", "--json");
            var dj = Upd.Parse(d.Out);
            ctx.Check(d.Code == 103 && Upd.True(dj, "stopped") && !Upd.True(dj, "restarted"), "정지 + 감독 살아 있음 → 103 stopped:true (Phase A)");
            Upd.KillPid(ctx, r, Upd.Kind(r, "sup").Single(), "정지 상태 감독");
            var e = r.Run(r.InstalledExe, "cli", "ls", "--json");
            var ej = Upd.Parse(e.Out);
            ctx.Check(e.Code == 103 && Upd.True(ej, "stopped") && !Upd.True(ej, "restarted"), "정지 + 감독도 없음 → 103 stopped:true, 감독을 띄우지 않음");
            Thread.Sleep(3000);
            ctx.Check(Upd.Kind(r, "sup").Count == 0 && Upd.Kind(r, "core").Count == 0, "3초 뒤에도 감독·코어 0");
            r.CheckNoProcs("정리");
            real.Verify(ctx, r);
        }

        // ================================================================== state-roundtrip
        [Scenario("state-roundtrip", "C-12 JSON 왕복 보존: state.json(StateStore 저장)·install.json(InstallInfo — 업데이트·C-14 Repair 저장)의 모르는 최상위 키가 원문 그대로 남음, 모르는 키가 없으면 state.json 은 Phase A 와 같은 바이트. prefs.json·icons.json 은 업데이트가 건드리지 않음(바이트 동일). 실제 exe: vA 설치 → 키 추가 → 보드 하나 더(state.json 새 배정 저장) → vB 업데이트 → 감독 kill·버전 어긋남 → Repair. 실제 시스템 무변경",
                  Order = 975, TimeoutS = 240)]
        public static void StateRoundtrip(ScenarioContext ctx)
        {
            var real = RealSystem.Capture(ctx);
            const string fut = "{\"n\": [1, 2], \"s\": \"한글\"}";
            // 코드 단위(시험 DLL 안 — 같은 컴파일의 Core)
            string dir = Path.Combine(ctx.Dir, "inproc");
            Directory.CreateDirectory(dir);
            string sp = Path.Combine(dir, "state.json");
            File.WriteAllText(sp, "{\"assign\": {\"A\": 17001}, \"released\": [], \"x_future\": " + fut + ", \"boards\": {}, \"x_flag\": true}");
            new StateStore(sp).GetOrAssignPort("B", 17001, 17000);
            var sj = HubFiles.ReadJson(sp).Value;
            ctx.Check(sj.GetProperty("x_future").GetRawText() == fut && HubFiles.Bool(sj, "x_flag") && J.N(sj.GetProperty("assign"), "B") == 17002,
                      "StateStore: 저장(새 배정) 뒤 모르는 키 x_future(원문)·x_flag 보존");
            string sp2 = Path.Combine(dir, "plain.json");
            new StateStore(sp2).GetOrAssignPort("A", 17001, 17000);
            ctx.Check(File.ReadAllText(sp2) == "{\n  \"assign\": {\n    \"A\": 17001\n  },\n  \"released\": [],\n  \"boards\": {}\n}\n", "모르는 키가 없으면 Phase A 와 같은 바이트");
            string ip = Path.Combine(dir, "install.json");
            File.WriteAllText(ip, "{\"version\":\"1\",\"x_future\":" + fut + ",\"state\":\"installed\",\"skills\":[]}");
            var ii = InstallInfo.Load(ip);
            ii.Version = "2";
            ii.Save(ip);
            var ij = HubFiles.ReadJson(ip).Value;
            ctx.Check(HubFiles.Str(ij, "version") == "2" && ij.GetProperty("x_future").GetRawText() == fut && ij.GetProperty("skills").GetRawText() == "[]", "InstallInfo: 저장 뒤 모르는 키 원문·순서 보존");

            // 실제 exe
            var r = InstallRig.Create(ctx, "root", 17960, 17962);
            string vB = r.Run(Upd.VB, "version").Out.Trim();
            string cabB = r.Cable + "B";
            r.Write(Path.Combine("UserProfile", "uart_logs", "hub", "state.json"),
                    "{\n  \"assign\": {\n    " + Json.Str(r.Cable) + ": 17962\n  },\n  \"released\": [],\n  \"boards\": {},\n  \"x_future_state\": " + fut + "\n}\n");
            Upd.InstallAndWait(ctx, r, Dev, null, 1, "vA 설치");
            string hub = Upd.HubDir(r);
            Upd.Ls(ctx, r, "vA", r.Cable);
            const string futI = "[1, {\"a\": \"한\"}]";
            File.WriteAllText(r.InstallJson, HubFiles.ToJObj(r.InstallInfoJson().Value).Raw("x_future_install", futI).ToString() + "\n");
            string prefs = Path.Combine(r.InstallDir, "prefs.json"), icons = Path.Combine(r.InstallDir, "icons.json");
            File.WriteAllText(prefs, HubFiles.ToJObj(HubFiles.ReadJson(prefs).Value).Raw("x_future_prefs", "\"p\"").ToString() + "\n");
            File.WriteAllText(icons, "{\"icons\": [], \"x_future_icons\": 1}\n");
            string shaP = Upd.Sha(prefs), shaI = Upd.Sha(icons);
            r.Write("sim_spec.json", "{\"boards\":[" + Upd.Board(r.Cable, r.Com, r.Host) + "," + Upd.Board(cabB, "COM963", "SIMRT963") + "]}");

            var u = r.Run(Upd.VB, "install");
            ctx.Check(u.Code == 0, "vB 업데이트 exit 0");
            Upd.Ls(ctx, r, "vB(보드 2개)", r.Cable, cabB);
            var st = HubFiles.ReadJson(Path.Combine(hub, "state.json")).Value;
            ctx.Check(st.GetProperty("x_future_state").GetRawText() == fut && J.N(st.GetProperty("assign"), cabB) == 17963, "state.json: vB 코어가 새 케이블 배정을 저장한 뒤에도 x_future_state 원문 보존");
            var iv = r.InstallInfoJson().Value;
            ctx.Check(HubFiles.Str(iv, "version") == vB && iv.GetProperty("x_future_install").GetRawText() == futI, "install.json: 업데이트(version=" + vB + ") 뒤 x_future_install 원문 보존");
            ctx.Check(Upd.Sha(prefs) == shaP && Upd.Sha(icons) == shaI, "prefs.json·icons.json: 업데이트가 바이트 그대로 둠(모르는 키 포함)");

            // C-14 Repair 저장
            ctx.WaitUntil(() => Upd.Count(Upd.HubLog(hub), "[sup] 설치 상태 일치") >= 2, 20000, "업데이트 뒤 C-14 판정 끝");
            Upd.SetInstall(r, "version", "19990101000000000");
            var (tk, b0) = Upd.KillSup(ctx, r, hub, "Repair 유도");
            Upd.WaitRespawn(ctx, hub, b0, 8000, "코어가 감독 재기동");
            ctx.WaitUntil(() => Upd.HubLog(hub).Contains("Repair 끝 rc=0"), 15000, "새 감독 C-14: version 어긋남 → Repair");
            iv = r.InstallInfoJson().Value;
            ctx.Check(HubFiles.Str(iv, "version") == vB && iv.GetProperty("x_future_install").GetRawText() == futI && Upd.Count(Upd.HubLog(hub), "Repair 시작") == 1,
                      "Repair 저장 뒤 install.json version=" + vB + "·x_future_install 원문 보존(Repair 1회)");
            r.CheckNoProcs("정리");
            real.Verify(ctx, r);
        }
    }
}
