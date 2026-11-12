// US-108 시나리오(계획 T3 감독·UI 쪽): 바탕화면 아이콘 맞추기(icons-reconcile·icons-timing), 터미널 탐지·인자(terminal-args), open 의 기존 창 앞으로·C-9(open-front).
// 모두 실제 UartHub.exe 를 UART_HUB_TEST_ROOT(C-13) 아래에서 띄운다 — 바탕화면·시작 메뉴·LocalAppData 는 시험 루트 안 가짜, 터미널 실행·창 앞으로·안내 창·SHChangeNotify 는
// 시험 모드라 아무것도 띄우지 않고 "[ext:fake]" 줄(hub.log)·<root>\fake_launch.log 로만 남는다. 프로세스 목록·App Paths·PATH 도 fake_*.
//   감독: sup --no-tray --hub-dir <root>\hub --icons-dir <임시 바탕화면> --sim <사양> --sim-dll <Tests.dll>(코어 안 FakeBoard, plug/unplug 주입은 SimCore).
//   포트(제어/케이블): icons-reconcile 17409/17410~17413(+OneDrive 17420/17425·17421/17426), icons-timing 17430/17435,
//         terminal-args 17440(허브 없음, 제어 17439 = listen 없음), open-front 17444~17449(허브 없음, 제어 17443) + C-9 설치 17490/17495.
//         run_sim_wsl 의 17400·17450 과 겹치지 않는다.
// 끝마다 "실제 시스템 무변경"(RealSystem: 실제 Startup·바탕화면 *.lnk·skills·시작 메뉴 UART 허브·%LOCALAPPDATA%\uart_hub·schtasks) + TestIsolationViolation 0.
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

namespace UartHub.Testing
{
    /// <summary>아이콘 시나리오 리그: InstallRig(C-13 루트) + 허브 폴더·FakeBoard 사양·가짜 설치본(아이콘 대상)·감독 기동.</summary>
    internal sealed class IconRig
    {
        public ScenarioContext Ctx;
        public InstallRig R;
        public string Hub, Spec, Exe;
        public int Ctl, Base;
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        public static string Np => Path.Combine(Environment.SystemDirectory, "notepad.exe");

        public string Desk => R.Desktop;
        public string StartMenuHub => Path.Combine(R.StartMenu, "UART 허브");
        public string IconsJson => Path.Combine(R.InstallDir, "icons.json");

        /// <param name="cfgExtra">config.json 에 덧붙일 키(앞에 쉼표 포함). fast=false 면 운영 기본 주기(scan_interval_ms 등 기본값).</param>
        public static IconRig Create(ScenarioContext ctx, string sub, int ctl, int basePort, string boards, string state = null, string cfgExtra = "", bool fast = true)
        {
            var r = InstallRig.Create(ctx, sub, ctl, basePort, sim: false);
            var g = new IconRig { Ctx = ctx, R = r, Ctl = ctl, Base = basePort, Hub = Path.Combine(r.Root, "hub"), Spec = Path.Combine(r.Root, "icons_spec.json") };
            Directory.CreateDirectory(g.Hub);
            string tune = fast ? ", \"scan_interval_ms\": 200, \"read_timeout_ms\": 50, \"writer_wait_ms\": 20, \"ring_mb\": 1, \"queue_mb\": 16, \"heartbeat_s\": 0.5, \"hang_s\": 8, \"ping_interval_s\": 0.5, \"backoff_s\": [1, 2, 4], \"idle_s\": 0.3, \"quiet_ms\": 600" : "";
            File.WriteAllText(Path.Combine(g.Hub, "config.json"), "{\"base_port\": " + basePort + ", \"control_port\": " + ctl + ", \"vids\": []" + tune + cfgExtra + "}\n", Utf8);
            if (state != null) File.WriteAllText(Path.Combine(g.Hub, "state.json"), state, Utf8);
            File.WriteAllText(g.Spec, "{\"boards\":[" + boards + "]}", Utf8);
            // 아이콘 대상 = 설치된 UartHub.exe(가짜 파일 — 아이콘 시험은 실행하지 않는다), install_id = IDNEW
            Directory.CreateDirectory(r.InstallDir);
            g.Exe = r.InstalledExe;
            File.WriteAllBytes(g.Exe, Encoding.ASCII.GetBytes("MZ dummy installed image for icon tests"));
            File.WriteAllText(r.InstallJson, "{\"version\":\"0\",\"install_id\":\"IDNEW\",\"exe\":" + Json.Str(g.Exe) + ",\"state\":\"installed\"}\n", Utf8);
            return g;
        }

        public static string Board(string cable, string com, string host, bool plugged = true)
            => "{\"cable\":" + Json.Str(cable) + ",\"com\":" + Json.Str(com) + ",\"hostname\":" + Json.Str(host) + ",\"printk_ms\":300" + (plugged ? "" : ",\"plugged\":false") + "}";

        public Process StartSup(string iconsDir = null, bool fastTick = true)
        {
            var a = new List<string> { "sup", "--no-tray", "--hub-dir", Hub, "--icons-dir", iconsDir ?? Desk, "--sim", Spec, "--sim-dll", TestEnv.TestsDll };
            if (fastTick) { a.Add("--tick-ms"); a.Add("100"); }
            return Upd.Start(R, TestEnv.UartHubExe, a.ToArray(), null).P;
        }

        public string Log() => HubFiles.ReadShared(Path.Combine(Hub, "hub.log")) ?? "";
        public int Count(string what) => InstallRig.Count(Log(), what);

        /// <summary>살아 있고 제어 포트가 listening 인 코어의 PID(아니면 0).</summary>
        public int CorePid()
        {
            var j = HubFiles.ReadJson(HubFiles.CorePid(Hub));
            if (!j.HasValue || HubFiles.Str(j.Value, "control") != "listening") return 0;
            long pid = HubFiles.Long(j.Value, "pid");
            return HubFiles.IsAlive(pid, HubFiles.Long(j.Value, "start_ticks")) ? (int)pid : 0;
        }

        public int WaitCore(string what, int ms = 20000)
        {
            int pid = 0;
            Ctx.WaitUntil(() => (pid = CorePid()) > 0, ms, what + ": 코어 제어 포트 listening");
            return pid;
        }

        /// <summary>코어 안 FakeBoard 주입(SimCore): sim\inject.&lt;pid&gt; → ack.</summary>
        public void Inject(string cmd, string what)
        {
            int pid = CorePid();
            Ctx.Check(pid > 0, what + ": 코어 있음");
            string dir = Path.Combine(Hub, "sim"), ack = Path.Combine(dir, "ack." + pid);
            HubFiles.TryDelete(ack);
            File.WriteAllText(Path.Combine(dir, "inject." + pid), cmd, Utf8);
            Ctx.WaitUntil(() => File.Exists(ack) && (HubFiles.ReadShared(ack) ?? "").Trim() == cmd, 5000, what + ": 주입 '" + cmd + "' 처리");
        }

        public string Token => File.ReadAllText(Path.Combine(Hub, "token")).Trim();
        public JsonElement Ctl_(params string[] argv) => CtlClient.Call(Ctl, Token, 15000, argv);

        /// <summary>ls --detail 의 그 케이블 status(목록에 없으면 null — decide 직후 1스캔 동안 사라진다).</summary>
        public string StatusOf(string cable)
        {
            foreach (var c in Ctl_("ls", "--detail").GetProperty("cables").EnumerateArray())
                if (J.S(c, "cable") == cable) return J.S(c, "status");
            return null;
        }

        public JsonElement Sup() => HubFiles.ReadJson(HubFiles.SupState(Hub)) ?? default;

        /// <summary>sup.json 의 문자열 배열 키(파일·키가 없으면 null).</summary>
        public string[] SupArr(string k)
        {
            var s = Sup();
            return s.ValueKind == JsonValueKind.Object && s.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText()).ToArray() : null;
        }

        /// <summary>감독 요청 스풀(sup\req → sup\resp).</summary>
        public JsonElement SupReq(string verb)
        {
            string id = "icons-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string req = Path.Combine(HubFiles.ReqDir(Hub), id + ".json"), resp = Path.Combine(HubFiles.RespDir(Hub), id + ".json");
            File.WriteAllText(req + ".tmp", new JObj().Set("id", id).Set("verb", verb).Set("from", "test").ToString(), Utf8);
            File.Move(req + ".tmp", req, true);
            Ctx.WaitUntil(() => File.Exists(resp), 10000, "감독 요청 " + verb + " 응답");
            Thread.Sleep(50);
            return LifeRig.Parse(HubFiles.ReadShared(resp));
        }

        public string[] Lnks(string dir) => Directory.Exists(dir) ? Directory.GetFiles(dir, "*.lnk").Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToArray() : new string[0];
        public string Show(string dir) => "[" + string.Join(", ", Lnks(dir)) + "]";
        public bool Has(string name, string dir = null) => File.Exists(Path.Combine(dir ?? Desk, name));

        /// <summary>자기 아이콘인가: 대상 = 설치본, 인자 = open &lt;p&gt;, 설명에 [uart_hub:IDNEW].</summary>
        public bool OwnIcon(string name, int port, string dir = null)
        {
            Thread.Sleep(100);                                  // 기다리는 동안 .lnk 를 계속 열고 있지 않게(감독의 옮기기·덮어쓰기와 겨루지 않음)
            var i = ShellLink.Read(Path.Combine(dir ?? Desk, name));
            return i != null && SystemAccess.SamePath(i.Target, Exe) && i.Arguments == "open " + port && (i.Description ?? "").Contains("[uart_hub:IDNEW]");
        }

        public bool Foreign(string name) { var i = ShellLink.Read(Path.Combine(Desk, name)); return i != null && SystemAccess.SamePath(i.Target, Np); }

        public void Decoy(string name, string args = "x") => ShellLink.Save(Path.Combine(Desk, name), new ShellLinkInfo { Target = Np, Arguments = args, Description = "남의 바로가기" });

        public void OwnFile(string name, int port, string id)
            => ShellLink.Save(Path.Combine(Desk, name), new ShellLinkInfo { Target = Exe, Arguments = "open " + port, Description = "UART 허브 " + Path.GetFileNameWithoutExtension(name) + " " + ShellLink.Marker(id) });

        public List<int> Kind(string sub) => Upd.Kind(R, sub);

        /// <summary>이 시험 루트의 감독·코어를 kill(InstallRig.KillAll — WMI 명령줄에 이 루트가 들어간 UartHub.exe 만, 감독 먼저). 그 사이 스스로 끝난 것은 건너뛴다.</summary>
        public void KillAll(string what)
        {
            var before = R.Procs();
            R.KillAll();
            Ctx.WaitUntil(() => Kind("sup").Count == 0 && Kind("core").Count == 0, 10000, what + ": 감독·코어 0(kill 대상 " + before.Count + "개, 명령줄로 이 시험 루트 확인)");
        }

        public void Violations()
        {
            string vf = Path.Combine(R.Root, "isolation_violations.log");
            int n = File.Exists(vf) ? File.ReadAllLines(vf).Count(l => l.Trim().Length > 0) : 0;
            Ctx.Check(n == 0 && !Log().Contains("TestIsolationViolation"), "TestIsolationViolation 0(루트 " + Path.GetFileName(R.Root) + ")");
            var errs = Log().Split('\n').Where(l => l.Contains("감독 오류")).ToList();
            Ctx.Check(errs.Count == 0, "hub.log 감독 오류 0(루트 " + Path.GetFileName(R.Root) + ")" + (errs.Count > 0 ? ": " + errs[0] : ""));
        }
    }

    public static class IconScenarios
    {
        static string Dev => TestEnv.UartHubExe;

        /// <summary>icons.json 의 포트(정렬). 없거나 깨졌으면 빈 배열.</summary>
        static long[] IconPorts(IconRig g)
        {
            var j = HubFiles.ReadJson(g.IconsJson);
            return j.HasValue && j.Value.TryGetProperty("icons", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Select(x => J.N(x, "port")).OrderBy(x => x).ToArray() : new long[0];
        }

        // ================================================================== icons-reconcile
        [Scenario("icons-reconcile", "바탕화면 아이콘 맞추기(T3, E1, C-5, A2-3·A2-7): attached 케이블만 '<보드|COMx> (<포트>)' → 설치본 open <p>·[uart_hub:id], 보드명 나오면 이름 바꿈, 분리 삭제, 남의 같은 이름 파일 보존(충돌 표시·지우면 만듦)·남의 미끼 보존, install_id 다른 자기 아이콘(보조 판정)·icons.json 남은 항목 정리, 첫 연결 아니오 → 아이콘 없음·예 → 아이콘, quit·shutdown 유지, 비정상 종료 뒤 자기 것만 현재 상태로(icons.json 없이도), stop 전부 삭제·정지 중 안 만듦·start 로 복귀, icons.json 모르는 키 보존, SHChangeNotify, OneDrive 바탕화면이면 시작 메뉴 'UART 허브'. 실제 시스템 무변경",
                  Order = 770, TimeoutS = 420)]
        public static void IconsReconcile(ScenarioContext ctx)
        {
            var real = RealSystem.Capture(ctx);
            string boards = IconRig.Board("SIMICA", "COM410", "ICONA") + "," + IconRig.Board("SIMICB", "COM411", "ICONB") + ","
                          + IconRig.Board("SIMICC", "COM412", "ICONC") + "," + IconRig.Board("SIMICD", "COM413", "ICOND");
            var g = IconRig.Create(ctx, "main", 17409, 17410, boards, "{\"assign\":{\"SIMICA\":17410,\"SIMICB\":17411},\"released\":[],\"boards\":{}}",
                                   ", \"ask_new_cables\": true");
            // 미리 놓은 것: 남의 미끼(관계없는 이름)·사용자 바로가기·B 가 원할 이름의 남의 파일(충돌)·install_id 가 다른 자기 아이콘(붙지 않은 포트)·icons.json(없는 파일 + 모르는 키)
            g.Decoy("EVB-A1 (7002).lnk");
            g.Decoy("메모장.lnk", "");
            g.Decoy("COM411 (17411).lnk");
            g.OwnFile("OLDBOARD (17419).lnk", 17419, "IDOLD");
            File.WriteAllText(g.IconsJson, "{\"icons\":[{\"port\":17418,\"path\":" + Json.Str(Path.Combine(g.Desk, "GONE (17418).lnk")) + "}],\"x_future_icons\":{\"k\":[1,\"한\"]}}\n");

            g.StartSup();
            g.WaitCore("감독 1");
            ctx.WaitUntil(() => g.OwnIcon("COM410 (17410).lnk", 17410), 8000,
                          "attached A → 'COM410 (17410).lnk'(보드명 아직 없음 → COM): 대상 = 설치된 UartHub.exe, 인자 = open 17410, 설명 [uart_hub:IDNEW]");
            ctx.Check(!Directory.GetFiles(g.R.InstallDir, "*.tmp.lnk").Any() && !Directory.GetFiles(g.Desk, "*.tmp*").Any(), "바로가기는 임시 파일 → 옮기기(쓰다 만 .lnk·임시 파일 남지 않음)");
            ctx.WaitUntil(() => !g.Has("OLDBOARD (17419).lnk"), 5000, "install_id 가 다른 자기 아이콘(대상·open <p> 일치 = 보조 판정)·붙지 않은 포트 → 정리(삭제)");
            ctx.Check(g.Foreign("EVB-A1 (7002).lnk") && g.Foreign("메모장.lnk"), "남의 미끼 'EVB-A1 (7002).lnk'·사용자 바로가기 그대로");
            ctx.WaitUntil(() => g.Log().Contains("아이콘 이름 충돌: " + Path.Combine(g.Desk, "COM411 (17411).lnk")), 5000, "B 가 원하는 이름에 남의 파일 → 만들지 않고 hub.log '아이콘 이름 충돌'");
            ctx.Check(g.Foreign("COM411 (17411).lnk"), "충돌한 남의 파일 'COM411 (17411).lnk' 은 덮어쓰지 않음(대상 notepad 그대로)");
            ctx.WaitUntil(() => g.SupArr("icon_conflicts")?.Contains("COM411 (17411)") == true, 12000,
                          "sup.json icon_conflicts = ['COM411 (17411)'](트레이 '아이콘 이름 충돌' 표시 근거)");
            var sj = g.Sup();
            var pend = sj.GetProperty("pending").EnumerateArray().Select(x => J.S(x, "cable")).OrderBy(x => x).ToArray();
            ctx.Check(J.Seq(pend, "SIMICC", "SIMICD"), "sup.json pending = 처음 보는 C·D (ask_new_cables) — " + string.Join(",", pend));
            ctx.Check(!g.Lnks(g.Desk).Any(n => n.Contains("COM412") || n.Contains("COM413")), "pending 케이블은 아이콘 없음 " + g.Show(g.Desk));
            ctx.WaitUntil(() => IconPorts(g).SequenceEqual(new long[] { 17410 }), 5000, "icons.json icons = [17410]");
            var ij = HubFiles.ReadJson(g.IconsJson).Value;
            ctx.Check(ij.GetProperty("x_future_icons").GetRawText() == "{\"k\":[1,\"한\"]}",
                      "icons.json: icons = [17410](없는 파일 항목 GONE 은 버림), 모르는 키 x_future_icons 원문 보존(C-12)");
            ctx.Check(g.Count("아이콘 만듦: COM410 (17410).lnk") == 1 && Regex.IsMatch(g.Log(), @"아이콘 만듦: COM410 \(17410\)\.lnk → open 17410 \(SHChangeNotify fake\)")
                      && Regex.IsMatch(g.Log(), @"아이콘 지움\(분리\): OLDBOARD \(17419\)\.lnk \(SHChangeNotify fake\)"), "바꿀 때마다 SHChangeNotify(시험 모드 = fake)");

            // 첫 연결: C 아니오 → 아이콘 없음(released), D 예 → 아이콘
            var rc = g.Ctl_("cable", "decide", "SIMICC", "ignore");
            ctx.Check(J.N(rc, "exit") == 0, "cable decide SIMICC ignore → 0 (" + J.Show(rc) + ")");
            var rd = g.Ctl_("cable", "decide", "SIMICD", "board");
            ctx.Check(J.N(rd, "exit") == 0, "cable decide SIMICD board → 0");
            long pC = J.N(rc, "port"), pD = J.N(rd, "port");
            ctx.WaitUntil(() => g.OwnIcon("COM413 (" + pD + ").lnk", (int)pD), 6000, "'예'(D) → attached → 아이콘 'COM413 (" + pD + ").lnk'");
            ctx.WaitUntil(() => g.StatusOf("SIMICC") == "released", 5000, "'아니오'(C) → released(포트 " + pC + " 배정·COM 미개방)");
            Thread.Sleep(2500);
            ctx.Check(!g.Lnks(g.Desk).Any(n => n.Contains("COM412") || n.Contains("(" + pC + ")")), "첫 연결 '아니오' → 아이콘 없음(2.5초 더 기다림) " + g.Show(g.Desk));

            // 보드명 → 이름 바꿈
            using (var tn = TnClient.Connect(17410))
            {
                ctx.Check(tn.WaitCount(TelnetScenarios.Greeting.Length, 3000), "telnet :17410 협상 바이트");
                tn.Send(TelnetScenarios.TeraTermReply);
                Thread.Sleep(300);
                tn.Send("\r");
                ctx.WaitUntil(() => g.OwnIcon("ICONA (17410).lnk", 17410) && !g.Has("COM410 (17410).lnk"), 6000, "보드 프롬프트(사람 Enter) → 'COM410 (17410)' 이 'ICONA (17410)' 으로 이름 바뀜");
            }
            ctx.WaitUntil(() => { Thread.Sleep(100); return (ShellLink.Read(Path.Combine(g.Desk, "ICONA (17410).lnk"))?.Description ?? "").Contains("ICONA (17410)"); }, 3000, "바뀐 아이콘 설명도 새 이름");
            ctx.Check(!g.Log().Contains("감독 오류"), "hub.log 에 감독 오류 없음(아이콘 쓰기 거부 등)");
            ctx.Check(g.Log().Contains("아이콘 이름 바꿈: COM410 (17410).lnk → ICONA (17410).lnk (SHChangeNotify fake)"), "바뀐 아이콘도 자기 것(open 17410), SHChangeNotify");

            // 충돌 해소: 사용자가 남의 파일을 지우면 그 이름으로 만든다
            File.Delete(Path.Combine(g.Desk, "COM411 (17411).lnk"));
            ctx.WaitUntil(() => g.Has("COM411 (17411).lnk") && g.OwnIcon("COM411 (17411).lnk", 17411), 5000, "충돌 파일을 지우면 'COM411 (17411).lnk' 를 자기 것으로 만듦");
            ctx.WaitUntil(() => g.SupArr("icon_conflicts")?.Length == 0, 12000, "icon_conflicts 비움");

            // 분리 → 삭제, 다시 꽂음 → 다시 만듦(보드명 기억)
            g.Inject("unplug SIMICA", "A 뽑기");
            ctx.WaitUntil(() => !g.Has("ICONA (17410).lnk"), 5000, "뽑은 A 의 아이콘 삭제");
            g.Inject("plug SIMICA", "A 다시 꽂기");
            ctx.WaitUntil(() => g.OwnIcon("ICONA (17410).lnk", 17410), 6000, "다시 꽂은 A → 'ICONA (17410).lnk'(state.json 보드명)");
            string[] mine = { "ICONA (17410).lnk", "COM411 (17411).lnk", "COM413 (" + pD + ").lnk" };
            ctx.Check(mine.All(n => g.Has(n)) && g.Lnks(g.Desk).Length == 5, "바탕화면 = 자기 3 + 남의 2 " + g.Show(g.Desk));

            // quit: 감독만 끝냄 → 아이콘 유지, 새 감독이 이어받음(중복·변경 없음)
            var q = g.SupReq("quit");
            ctx.Check(J.S(q, "status") == "ok", "감독 요청 quit → ok");
            ctx.WaitUntil(() => g.Kind("sup").Count == 0, 8000, "quit: 감독 끝남");
            ctx.Check(g.Kind("core").Count == 1 && mine.All(n => g.Has(n)) && g.Count("아이콘 전부 지움") == 0, "quit: 코어 유지·아이콘 그대로");
            int made = g.Count("아이콘 만듦"), gone = g.Count("아이콘 지움");
            g.StartSup();
            ctx.WaitUntil(() => g.Count("기존 코어 입양") >= 1, 8000, "새 감독이 코어 입양");
            ctx.WaitUntil(() => g.Count("아이콘: 기동 정리 대상") >= 2, 5000, "새 감독 기동 정리");
            Thread.Sleep(3000);
            ctx.Check(mine.All(n => g.Has(n)) && g.Lnks(g.Desk).Length == 5 && g.Count("아이콘 만듦") == made && g.Count("아이콘 지움") == gone,
                      "quit 뒤 새 감독: 아이콘 이어받음(새로 만들거나 지운 것 없음) " + g.Show(g.Desk));

            // shutdown: 코어 정상 종료 + 감독 끝 → 아이콘 유지
            var sd = g.SupReq("shutdown");
            ctx.Check(J.S(sd, "status") == "ok", "감독 요청 shutdown → ok");
            ctx.WaitUntil(() => g.Kind("sup").Count == 0 && g.Kind("core").Count == 0, 15000, "shutdown: 감독·코어 끝남");
            ctx.Check(mine.All(n => g.Has(n)) && !HubFiles.IsStopped(g.Hub), "shutdown: 아이콘 그대로·control.json stopped 아님(C-5)");

            // 비정상 종료 뒤: B 를 뽑은 상태로 다시 기동, icons.json 없이(보조 판정), 같은 포트의 자기 것 중복 하나 + 남의 미끼
            g.StartSup();
            g.WaitCore("감독 2");
            Thread.Sleep(2000);
            g.KillAll("비정상 종료(감독·코어 kill)");
            File.WriteAllText(g.Spec, "{\"boards\":[" + IconRig.Board("SIMICA", "COM410", "ICONA") + "," + IconRig.Board("SIMICB", "COM411", "ICONB", plugged: false) + ","
                                       + IconRig.Board("SIMICC", "COM412", "ICONC") + "," + IconRig.Board("SIMICD", "COM413", "ICOND") + "]}");
            File.Delete(g.IconsJson);
            g.OwnFile("dup A (17410).lnk", 17410, "IDNEW");
            g.StartSup();
            g.WaitCore("감독 3");
            ctx.WaitUntil(() => !g.Has("COM411 (17411).lnk") && !g.Has("dup A (17410).lnk"), 8000, "재기동 정리: 뽑힌 B 아이콘·같은 포트 중복 아이콘 삭제(icons.json 없이 대상·인자로 판정)");
            ctx.WaitUntil(() => g.Has("ICONA (17410).lnk") && g.Has("COM413 (" + pD + ").lnk"), 5000, "붙은 A·D 아이콘은 남음");
            ctx.Check(g.Foreign("EVB-A1 (7002).lnk") && g.Foreign("메모장.lnk") && g.Lnks(g.Desk).Length == 4, "남의 미끼·사용자 바로가기 그대로 " + g.Show(g.Desk));
            ctx.WaitUntil(() => IconPorts(g).SequenceEqual(new[] { 17410L, pD }), 5000, "icons.json 다시 씀: [17410, " + pD + "]");

            // stop: 자기 아이콘 전부 삭제, 정지 중에는 만들지 않음, start 로 복귀
            var st = g.SupReq("stop");
            ctx.Check(J.S(st, "status") == "ok", "감독 요청 stop → ok");
            ctx.WaitUntil(() => g.Lnks(g.Desk).Length == 2, 5000, "stop → 자기 아이콘 전부 삭제(남의 2개만) " + g.Show(g.Desk));
            ctx.WaitUntil(() => g.Kind("core").Count == 0, 15000, "stop: 코어 끝남");
            Thread.Sleep(2500);
            ctx.Check(g.Lnks(g.Desk).Length == 2 && g.Foreign("EVB-A1 (7002).lnk") && g.Foreign("메모장.lnk"), "정지 중: 아이콘을 만들지 않음, 남의 것 그대로");
            var sa = g.SupReq("start");
            ctx.Check(J.S(sa, "status") == "ok", "감독 요청 start → ok");
            ctx.WaitUntil(() => g.Has("ICONA (17410).lnk") && g.Has("COM413 (" + pD + ").lnk"), 10000, "start → 아이콘 다시 만듦");
            ctx.WaitUntil(() => g.SupArr("icon_conflicts")?.Length == 0, 12000, "icon_conflicts 없음");
            g.KillAll("정리");
            g.Violations();

            // OneDrive 바탕화면 → 시작 메뉴 'UART 허브'
            var od = IconRig.Create(ctx, "onedrive", 17420, 17425, IconRig.Board("SIMICO", "COM425", "ICONO"), "{\"assign\":{\"SIMICO\":17425},\"released\":[],\"boards\":{}}");
            string odDesk = Path.Combine(od.R.UserProfile, "OneDrive - Contoso", "Desktop");
            Directory.CreateDirectory(odDesk);
            od.StartSup(odDesk);
            od.WaitCore("OneDrive 감독");
            ctx.WaitUntil(() => od.OwnIcon("COM425 (17425).lnk", 17425, od.StartMenuHub), 8000, "OneDrive 바탕화면(경로) → 시작 메뉴 'UART 허브\\COM425 (17425).lnk'(자기 것)");
            ctx.Check(od.Lnks(odDesk).Length == 0 && od.Lnks(od.Desk).Length == 0
                      && od.Log().Contains("OneDrive 바탕화면(경로에 OneDrive 폴더) → 시작 메뉴"), "OneDrive 바탕화면·로컬 바탕화면에는 아이콘 없음, hub.log 에 이유");
            var os = od.SupReq("stop");
            ctx.WaitUntil(() => od.Lnks(od.StartMenuHub).Length == 0, 5000, "OneDrive(시작 메뉴) 아이콘도 stop 에 삭제");
            od.KillAll("OneDrive 정리");
            // 주입(fake_onedrive): 일반 바탕화면 경로라도 OneDrive 로 판정
            var od2 = IconRig.Create(ctx, "onedrive2", 17421, 17426, IconRig.Board("SIMICP", "COM426", "ICONP"), "{\"assign\":{\"SIMICP\":17426},\"released\":[],\"boards\":{}}");
            File.WriteAllText(Path.Combine(od2.R.Root, "fake_onedrive"), "1");
            od2.StartSup();
            od2.WaitCore("OneDrive 주입 감독");
            ctx.WaitUntil(() => od2.Has("COM426 (17426).lnk", od2.StartMenuHub), 8000, "fake_onedrive 주입 → 시작 메뉴 'UART 허브\\COM426 (17426).lnk'");
            ctx.Check(od2.Lnks(od2.Desk).Length == 0, "주입: 바탕화면에는 아이콘 없음");
            od2.KillAll("OneDrive 주입 정리");
            od.Violations(); od2.Violations();
            real.Verify(ctx, g.R, od.R, od2.R);
        }

        // ================================================================== icons-timing
        [Scenario("icons-timing", "아이콘 5초(A2-2·A2-3): 운영 기본 주기(scan_interval 2초 기본·감독 ls 1초·틱 200ms)로 sup --no-tray --icons-dir, SimCore plug/unplug 주입 3회 — 꽂은 뒤 아이콘 ≤5초, 뽑은 뒤 삭제 ≤5초, 붙음 전이 hub.log '케이블 붙음'(첫 스냅샷 뒤만). 실제 시스템 무변경",
                  Order = 771, TimeoutS = 180)]
        public static void IconsTiming(ScenarioContext ctx)
        {
            var real = RealSystem.Capture(ctx);
            var g = IconRig.Create(ctx, "root", 17430, 17435, IconRig.Board("SIMTIM", "COM435", "TIMB", plugged: false),
                                   "{\"assign\":{\"SIMTIM\":17435},\"released\":[],\"boards\":{}}", fast: false);
            g.StartSup(fastTick: false);
            g.WaitCore("감독");
            ctx.WaitUntil(() => { var s = g.Sup(); return s.ValueKind == JsonValueKind.Object && J.S(s, "state") == "running"; }, 10000, "감독 running");
            Thread.Sleep(2500);                                                           // 첫 스냅샷(기준선, 케이블 없음)
            ctx.Check(g.Lnks(g.Desk).Length == 0 && g.Count("케이블 붙음") == 0, "꽂기 전: 아이콘 0·붙음 전이 0(첫 스냅샷은 기준선)");
            const string icon = "COM435 (17435).lnk";
            var plugMs = new List<long>();
            var unplugMs = new List<long>();
            string iconLoc = null;
            for (int i = 1; i <= 3; i++)
            {
                var sw = Stopwatch.StartNew();
                g.Inject("plug SIMTIM", "꽂기 " + i);
                ctx.WaitUntil(() => g.Has(icon), 5000, "꽂기 " + i + " → 아이콘 ≤5초");
                plugMs.Add(sw.ElapsedMilliseconds);
                if (i == 1) { Thread.Sleep(150); var li = ShellLink.Read(Path.Combine(g.Desk, icon)); iconLoc = li == null ? "(읽지 못함)" : li.IconPath + "," + li.IconIndex; }
                Thread.Sleep(1200);
                sw.Restart();
                g.Inject("unplug SIMTIM", "뽑기 " + i);
                ctx.WaitUntil(() => !g.Has(icon), 5000, "뽑기 " + i + " → 삭제 ≤5초");
                unplugMs.Add(sw.ElapsedMilliseconds);
                Thread.Sleep(1200);
            }
            ctx.Check(plugMs.Max() <= 5000 && unplugMs.Max() <= 5000,
                      "운영 기본 주기 실측: 꽂기→아이콘 " + string.Join("/", plugMs) + "ms(최대 " + plugMs.Max() + "), 뽑기→삭제 " + string.Join("/", unplugMs) + "ms(최대 " + unplugMs.Max() + ") — 모두 ≤5000ms");
            ctx.Check(g.Count("케이블 붙음 :17435 COM435") == 3, "hub.log '케이블 붙음' 3회(꽂을 때마다 — 트레이 풍선 근거)");
            ctx.Check(iconLoc != null && iconLoc.EndsWith(",0", StringComparison.Ordinal) && SystemAccess.SamePath(iconLoc.Substring(0, iconLoc.Length - 2), g.Exe),
                      "보드 아이콘 모양 = exe 아이콘(IconLocation exe,0 — UI_COPY.md): " + iconLoc?.Replace(g.R.Root, "<root>"));
            g.KillAll("정리");
            g.Violations();
            real.Verify(ctx, g.R);
        }

        // ================================================================== terminal-args
        sealed class TermRoot
        {
            public InstallRig R;
            public string Hub;
            public string P(params string[] s) => Path.Combine(new[] { R.Root }.Concat(s).ToArray());
            public string TT => P("ProgramFilesX86", "teraterm5", "ttermpro.exe");
            public string PT => P("ProgramFiles", "PuTTY", "putty.exe");
            public string MX => P("ProgramFilesX86", "Mobatek", "MobaXterm", "MobaXterm.exe");
            public void Touch(string f) { Directory.CreateDirectory(Path.GetDirectoryName(f)); File.WriteAllText(f, "fake"); }
            public void Gone(string f) { if (File.Exists(f)) File.Delete(f); }
            public void Prefs(string json) { Directory.CreateDirectory(R.InstallDir); File.WriteAllText(Path.Combine(R.InstallDir, "prefs.json"), json); }

            public static TermRoot Create(ScenarioContext ctx, string sub, int ctl)
            {
                var t = new TermRoot { R = InstallRig.Create(ctx, sub, ctl, ctl + 1, sim: false) };
                t.Hub = t.P("hub");
                Directory.CreateDirectory(t.Hub);
                // 허브 없음: 제어 포트는 listen 없는 17xxx(7200 을 건드리지 않게), 토큰 없음 → 코어 미응답
                File.WriteAllText(Path.Combine(t.Hub, "config.json"), "{\"control_port\": " + ctl + ", \"base_port\": " + (ctl + 1) + ", \"vids\": []}\n");
                return t;
            }

            /// <summary>open &lt;p&gt;(UART_HUB_DIR = 이 허브 폴더, 개발 경로 — C-9 없음). (rc, 결과 줄).</summary>
            public (int Code, string Line) Open(int port, params string[] extra)
            {
                var o = R.Run(TestEnv.UartHubExe, new[] { "open", port.ToString(CultureInfo.InvariantCulture) }.Concat(extra).ToArray(), Upd.Env("UART_HUB_DIR", Hub));
                return (o.Code, o.Out.Trim());
            }

            public string Log() => HubFiles.ReadShared(Path.Combine(Hub, "hub.log")) ?? "";
            public int Launches() => File.Exists(P("fake_launch.log")) ? File.ReadAllLines(P("fake_launch.log")).Length : 0;
        }

        [Scenario("terminal-args", "터미널(§2.3 f): 창 제목 '<보드> · <COM> · <p>'(모르는 칸은 뺌), 인자 문자열 3종(TeraTerm /W=\"<제목>\"·PuTTY -telnet -P·MobaXterm -newtab \"telnet …\")·PuTTY 창 제목, 찾아보기 판정(ttermpro*·putty*·MobaXterm* .exe 만), open 의 탐지 우선순위 TeraTerm → PuTTY → MobaXterm(기본 경로·App Paths·PATH), prefs.json 지정·경로·없으면 자동, 제목(코어 없으면 icons.json label → 없으면 포트만), 셋 다 없으면 안내 창(exit 3), 제목이 바뀐 창도 접속 대상으로 앞으로, 띄운 터미널 창 제목 바꾸기(TermTitler, 가짜 창 목록). 시험 모드라 아무것도 띄우지 않음. 실제 시스템 무변경",
                  Order = 772, TimeoutS = 180)]
        public static void TerminalArgs(ScenarioContext ctx)
        {
            var real = RealSystem.Capture(ctx);
            // 순수 함수(시스템 접근 없음)
            // 창 제목(트레이 포트 줄과 같은 모양, 2026-11-06): 이름 앎 · 이름 모름 · COM 도 모름
            ctx.Check(Terminals.Title("EVB-D4", "COM3", 7001) == "EVB-D4 · COM3 · 7001" && Terminals.Title(null, "COM3", 7001) == "COM3 · 7001"
                      && Terminals.Title(" ", null, 7001) == "7001" && Terminals.Title("EVB-A1", null, 7001) == "EVB-A1 · 7001",
                      "창 제목: 'EVB-D4 · COM3 · 7001' · 'COM3 · 7001' · '7001'(모르는 칸은 뺌)");
            ctx.Check(Terminals.Args("teraterm", 7002, Terminals.Title("EVB-A1", "COM3", 7002)) == "127.0.0.1:7002 /nossh /T=1 /W=\"EVB-A1 · COM3 · 7002\"",
                      "TeraTerm 인자: 127.0.0.1:7002 /nossh /T=1 /W=\"EVB-A1 · COM3 · 7002\"");
            ctx.Check(Terminals.Args("putty", 7002, "x") == "-telnet 127.0.0.1 -P 7002" && Terminals.WindowTitle("putty", Terminals.Title("EVB-A1", "COM3", 7002)) == "EVB-A1 · COM3 · 7002 - PuTTY"
                      && Terminals.WindowTitle("teraterm", "COM3 · 7002") == "COM3 · 7002", "PuTTY 인자: -telnet 127.0.0.1 -P 7002, 창 제목 'EVB-A1 · COM3 · 7002 - PuTTY'(TeraTerm 은 제목 그대로)");
            ctx.Check(Terminals.Args("mobaxterm", 7002, "x") == "-newtab \"telnet 127.0.0.1 7002\"", "MobaXterm 인자: -newtab \"telnet 127.0.0.1 7002\"");
            ctx.Check(Terminals.Title("a\"b", "COM3", 7003) == "a'b · COM3 · 7003" && Terminals.Title("a\tb", null, 7003) == "a_b · 7003",
                      "이름의 큰따옴표는 ' 로(/W=\"…\" 보호), 제어 문자는 _");
            // 이미 열린 창은 접속 대상으로 찾는다(제목이 바뀌어도, 옛 제목과 같은 다른 창은 아님)
            var moved = new List<ProcRow>
            {
                new ProcRow { Pid = 61, Name = "ttermpro.exe", CommandLine = "ttermpro.exe 127.0.0.1:7011 /nossh /T=1 /W=\"COM3 · 7001\"", Title = "COM3 · 7001" },
                new ProcRow { Pid = 62, Name = "ttermpro.exe", CommandLine = "ttermpro.exe 127.0.0.1:7001 /nossh /T=1 /W=\"COM3 · 7001\"", Title = "EVB-D4 · COM3 · 7001" },
                new ProcRow { Pid = 63, Name = "putty.exe", CommandLine = "putty.exe -telnet 127.0.0.1 -P 7003", Title = "EVB-A1 · COM5 · 7003 - PuTTY" },
            };
            ctx.Check(Terminals.FindOpen("teraterm", 7001, moved, out _)?.Pid == 62 && Terminals.FindOpen("putty", 7003, moved, out _)?.Pid == 63
                      && Terminals.FindOpen("teraterm", 7002, moved, out _) == null,
                      "제목이 바뀐 창도 접속 대상(127.0.0.1:<p>·-P <p>)으로 찾음, 옛 제목 'COM3 · 7001' 인 7011 창(pid 61)은 아님");
            var browse = new (string F, string K)[]
            {
                (@"C:\Program Files (x86)\teraterm5\ttermpro.exe", "teraterm"), ("TTERMPRO.EXE", "teraterm"), (@"D:\tools\putty.exe", "putty"), ("PUTTY_0.83.exe", "putty"),
                (@"C:\x\MobaXterm_Personal_25.2.exe", "mobaxterm"), ("mobaxterm.exe", "mobaxterm"), ("kitty.exe", null), ("notepad.exe", null),
                (@"C:\x\putty.ini", null), (@"C:\x\ttermpro", null), ("", null),
            };
            var bad = browse.Where(b => Terminals.KindFromFileName(b.F) != b.K).Select(b => b.F + "→" + (Terminals.KindFromFileName(b.F) ?? "null")).ToList();
            ctx.Check(bad.Count == 0, "찾아보기 판정: ttermpro*·putty*·MobaXterm*(.exe)만, 그 밖(kitty·notepad·.ini·확장자 없음)은 거부" + (bad.Count > 0 ? " — 틀림: " + string.Join(", ", bad) : ""));

            // open 으로 탐지(시험 루트의 가짜 터미널 파일 — 실행하지 않음)
            var t = TermRoot.Create(ctx, "root", 17439);
            const int port = 17440;
            t.Prefs("{\"terminal\":\"auto\"}\n");
            Directory.CreateDirectory(t.R.InstallDir);
            File.WriteAllText(Path.Combine(t.R.InstallDir, "icons.json"), "{\"icons\":[{\"port\":17440,\"path\":\"x\",\"name\":\"EVB-A1 (17440)\",\"label\":\"EVB-A1\"}]}\n");
            t.Touch(t.TT); t.Touch(t.PT); t.Touch(t.MX);
            var o = t.Open(port);
            ctx.Check(o.Code == 0 && o.Line == "open 17440 → launch teraterm pid=0 " + t.TT + " 127.0.0.1:17440 /nossh /T=1 /W=\"EVB-A1 · 17440\" (기본 경로)",
                      "셋 다 있음 → TeraTerm(우선순위 1), 제목 = icons.json label(코어 없음 → COM 칸 모름): " + o.Line.Replace(t.R.Root, "<root>"));
            ctx.Check(t.Log().Contains("[open] open 17440 → launch teraterm") && t.Log().Contains("[ext:fake] 실행 " + t.TT) && t.Launches() == 1,
                      "hub.log [open] 줄·[ext:fake] 실행(띄우지 않음)·fake_launch.log 1줄");
            ctx.Check(t.Log().Contains("코어 미응답 — 감독을 띄우지 않음(UART_HUB_DIR 로 허브 폴더를 정함"), "UART_HUB_DIR(개발 경로)에서는 C-9 자가 복구 없음");
            t.Gone(t.TT);
            o = t.Open(port);
            ctx.Check(o.Code == 0 && o.Line == "open 17440 → launch putty pid=0 " + t.PT + " -telnet 127.0.0.1 -P 17440 (기본 경로)"
                      && t.Log().Contains("[ext:fake] SetWindowText pid=0 'EVB-A1 · 17440 - PuTTY'"), "TeraTerm 없음 → PuTTY + 창 제목 SetWindowText(시험 = fake)");
            t.Gone(t.PT);
            o = t.Open(port);
            ctx.Check(o.Code == 0 && o.Line == "open 17440 → launch mobaxterm pid=0 " + t.MX + " -newtab \"telnet 127.0.0.1 17440\" (기본 경로)", "PuTTY 도 없음 → MobaXterm");
            t.Gone(t.MX);
            File.Delete(Path.Combine(t.R.InstallDir, "icons.json"));
            o = t.Open(port);
            ctx.Check(o.Code == 3 && o.Line == "open 17440 → 터미널 없음(안내 창)" && t.Log().Contains("[ui:fake] 안내 창: UART 허브 — 터미널(TeraTerm·PuTTY·MobaXterm)을 찾지 못했습니다.")
                      && t.Log().Contains("127.0.0.1:17440 로 telnet 접속하세요") && t.Launches() == 3, "셋 다 없음 → 안내 창(시험 = 줄만)·exit 3·실행 없음");
            // App Paths(PuTTY) + PATH(TeraTerm): TeraTerm 이 우선
            string apPutty = t.P("apps", "PuTTY", "putty.exe"), pathTt = t.P("bin", "ttermpro.exe");
            t.Touch(apPutty);
            File.WriteAllText(t.P("fake_app_paths.json"), "{\"putty.exe\":" + Json.Str(apPutty) + "}");
            o = t.Open(port);
            ctx.Check(o.Code == 0 && o.Line == "open 17440 → launch putty pid=0 " + apPutty + " -telnet 127.0.0.1 -P 17440 (App Paths)", "App Paths 의 PuTTY 를 찾음");
            t.Touch(pathTt);
            File.WriteAllText(t.P("fake_path.txt"), t.P("nowhere") + "\n" + t.P("bin") + "\n");
            o = t.Open(port);
            ctx.Check(o.Code == 0 && o.Line == "open 17440 → launch teraterm pid=0 " + pathTt + " 127.0.0.1:17440 /nossh /T=1 /W=\"17440\" (PATH)",
                      "PATH 의 TeraTerm 이 App Paths 의 PuTTY 보다 우선(종류 우선순위), 이름·COM 모름 → 제목 '17440'");
            // prefs.json 지정
            t.Touch(t.TT); t.Touch(t.PT); t.Touch(t.MX);
            t.Prefs("{\"terminal\":\"mobaxterm\",\"x_future_prefs\":1}\n");
            o = t.Open(port);
            ctx.Check(o.Code == 0 && o.Line.StartsWith("open 17440 → launch mobaxterm pid=0 " + t.MX, StringComparison.Ordinal), "prefs terminal=mobaxterm → 셋 다 있어도 MobaXterm");
            string custom = t.P("custom", "PUTTY_0.83.exe");
            t.Touch(custom);
            t.Prefs("{\"terminal\":\"putty\",\"putty\":" + Json.Str(custom) + "}\n");
            o = t.Open(port);
            ctx.Check(o.Code == 0 && o.Line == "open 17440 → launch putty pid=0 " + custom + " -telnet 127.0.0.1 -P 17440 (prefs.json)", "prefs terminal=putty + 경로 → 그 경로");
            t.Gone(custom); t.Gone(t.PT); t.Gone(apPutty);
            o = t.Open(port);
            ctx.Check(o.Code == 0 && o.Line.StartsWith("open 17440 → launch teraterm", StringComparison.Ordinal) && t.Log().Contains("prefs.json 의 PuTTY 를 찾지 못함 → 자동 선택"),
                      "지정한 PuTTY 가 없으면 자동(TeraTerm) + hub.log 이유");
            var bad106 = t.R.Run(TestEnv.UartHubExe, new[] { "open", "abc" }, Upd.Env("UART_HUB_DIR", t.Hub));
            ctx.Check(bad106.Code == 106, "open 인자 오류 → 106");
            ctx.Check(t.Launches() == 8, "가짜 실행 기록 8회(실제 터미널 실행 0 — 시험 모드)");
            // 코어 없음 + icons.json label 이 COMn → COM 칸
            t.Prefs("{\"terminal\":\"auto\"}\n");
            File.WriteAllText(Path.Combine(t.R.InstallDir, "icons.json"), "{\"icons\":[{\"port\":17440,\"path\":\"x\",\"name\":\"COM7 (17440)\",\"label\":\"COM7\"}]}\n");
            o = t.Open(port);
            ctx.Check(o.Code == 0 && o.Line.StartsWith("open 17440 → launch teraterm pid=0 ", StringComparison.Ordinal) && o.Line.Contains(" /W=\"COM7 · 17440\" ") && t.Launches() == 9,
                      "코어 없음·icons.json label 'COM7' → 제목 'COM7 · 17440': " + o.Line.Replace(t.R.Root, "<root>"));
            ctx.Check(!Directory.Exists(Path.Combine(t.Hub, "terminals")), "시험 모드 실행(pid 0)은 터미널 기록을 남기지 않음");
            // 제목이 바뀐 창도 앞으로(명령줄의 접속 대상으로 찾음): 같은 포트 창의 제목은 새 제목, 띄울 때 /W= 는 옛 제목. 옛 제목만 같은 다른 포트 창은 아님
            File.WriteAllText(t.P("fake_procs.json"), "[" +
                "{\"pid\":6161,\"name\":\"ttermpro.exe\",\"cmd\":\"ttermpro.exe 127.0.0.1:17441 /nossh /T=1 /W=\\\"COM7 · 17440\\\"\",\"title\":\"COM7 · 17440\"}," +
                "{\"pid\":6262,\"name\":\"ttermpro.exe\",\"cmd\":\"ttermpro.exe 127.0.0.1:17440 /nossh /T=1 /W=\\\"COM7 · 17440\\\"\",\"title\":\"EVB-D4 · COM7 · 17440\"}]");
            o = t.Open(port);
            ctx.Check(o.Code == 0 && o.Line == "open 17440 → front pid=6262 (teraterm)" && t.Launches() == 9,
                      "제목이 'EVB-D4 · COM7 · 17440' 로 바뀐 창 → 앞으로(front pid=6262), 옛 제목의 17441 창(pid 6161)은 아님: " + o.Line);
            File.Delete(t.P("fake_procs.json"));
            TermTitlerChecks(ctx, t.P("titles"));
            string vf = t.P("isolation_violations.log");
            ctx.Check(!File.Exists(vf) && !t.Log().Contains("TestIsolationViolation"), "TestIsolationViolation 0");
            real.Verify(ctx, t.R);
        }

        /// <summary>가짜 창 목록(TermTitler 이음매): pid → (시작 시각, 창들). 실제 창·프로세스는 보지 않는다.</summary>
        sealed class FakeWins : ITermWindows
        {
            public readonly Dictionary<int, long> Procs = new Dictionary<int, long>();
            public readonly Dictionary<IntPtr, (int Pid, string Title)> Wins = new Dictionary<IntPtr, (int, string)>();
            public readonly List<string> Sets = new List<string>();
            public void Add(int pid, long ticks, int h, string title) { Procs[pid] = ticks; Wins[(IntPtr)h] = (pid, title); }
            public void Retitle(int h, string title) => Wins[(IntPtr)h] = (Wins[(IntPtr)h].Pid, title);
            public bool Alive(int pid, long startTicks) => Procs.TryGetValue(pid, out var t) && t == startTicks;
            public List<(IntPtr Handle, string Title)> Windows(int pid) => Wins.Where(w => w.Value.Pid == pid).Select(w => (w.Key, w.Value.Title)).ToList();
            public bool SetTitle(IntPtr h, string title)
            {
                Sets.Add((long)h + "=" + title);
                Wins[h] = (Wins[h].Pid, title);
                return true;
            }
        }

        static CableRow TRow(int port, string board, string com) => new CableRow { Port = port, Board = board, Com = com, Status = "attached" };

        /// <summary>창 제목 바꾸기(TermTitler)를 가짜 창 목록으로: 보드명·COM 이 바뀌면 바로, 그대로면 1분에 한 번만 다시, 허브가 그 포트로 띄운 창만, 프로세스가 없거나 pid 재사용이면 기록을 지움.</summary>
        static void TermTitlerChecks(ScenarioContext ctx, string hub)
        {
            Directory.CreateDirectory(hub);
            var fw = new FakeWins();
            fw.Add(101, 1001, 11, "COM3 · 7001");                         // TeraTerm 7001
            fw.Add(102, 1002, 12, "COM4 · 7002");                         // TeraTerm 7002
            fw.Add(103, 1003, 13, "COM5 · 7003 - PuTTY");                 // PuTTY 7003
            fw.Add(104, 1004, 14, "127.0.0.1 - PuTTY (inactive)");        // 끊긴 PuTTY 7001
            fw.Add(202, 2002, 21, "COM3 · 7001");                         // 허브가 띄우지 않은 창(제목만 같음)
            ctx.Check(TermRecords.Add(hub, 7001, 101, 1001, "teraterm", "COM3 · 7001") && TermRecords.Add(hub, 7002, 102, 1002, "teraterm", "COM4 · 7002")
                      && TermRecords.Add(hub, 7003, 103, 1003, "putty", "COM5 · 7003 - PuTTY") && TermRecords.Add(hub, 7001, 104, 1004, "putty", "x - PuTTY")
                      && !TermRecords.Add(hub, 7001, 0, 0, "teraterm", "x") && !TermRecords.Add(hub, 7001, 105, 1, "mobaxterm", "x")
                      && TermRecords.Load(hub).Count == 4, "터미널 기록 4건(<허브>\\terminals\\<pid>.json), pid 0·MobaXterm 은 남기지 않음");
            var lines = new List<string>();
            var tt = new TermTitler(hub, lines.Add, fw);
            var t0 = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
            var rows0 = new List<CableRow> { TRow(7001, null, "COM3"), TRow(7002, null, "COM4"), TRow(7003, null, "COM5") };
            tt.Apply(rows0, t0);
            ctx.Check(fw.Sets.Count == 0 && lines.Count == 0 && tt.Pids.OrderBy(x => x).SequenceEqual(new[] { 101, 102, 103, 104 }), "이름 모름 그대로 → 바꾸지 않음, 지켜보는 pid 101~104");

            var rows1 = new List<CableRow> { TRow(7001, "EVB-D4", "COM3"), TRow(7002, null, "COM9"), TRow(7003, "EVB-A1", "COM5") };
            tt.Apply(rows1, t0.AddSeconds(1));
            ctx.Check(fw.Sets.SequenceEqual(new[] { "11=EVB-D4 · COM3 · 7001", "12=COM9 · 7002", "13=EVB-A1 · COM5 · 7003 - PuTTY" }),
                      "보드명이 잡힘(7001·7003)·COM 바뀜(7002) → 그 pid 창만 바로 바꿈(PuTTY 는 ' - PuTTY'): " + string.Join(", ", fw.Sets));
            ctx.Check(fw.Wins[(IntPtr)21].Title == "COM3 · 7001" && fw.Wins[(IntPtr)14].Title == "127.0.0.1 - PuTTY (inactive)",
                      "허브가 띄우지 않은 창(제목이 같아도)·끊긴 PuTTY((inactive))는 건드리지 않음");
            ctx.Check(lines.Count == 3 && lines[0] == "터미널 제목 바꿈 pid=101 \"COM3 · 7001\" → \"EVB-D4 · COM3 · 7001\"",
                      "hub.log 한 줄씩: " + (lines.Count > 0 ? lines[0] : "(없음)"));

            int n = fw.Sets.Count;
            tt.Apply(rows1, t0.AddSeconds(2));
            fw.Retitle(11, "Tera Term - [disconnected] VT");               // TeraTerm 이 스스로 제목을 되돌림
            tt.Apply(rows1, t0.AddSeconds(30));
            ctx.Check(fw.Sets.Count == n, "그대로면 1분 안에는 다시 쓰지 않음(TeraTerm 이 제목을 되돌려도)");
            tt.Apply(rows1, t0.AddSeconds(62));
            ctx.Check(fw.Sets.Count == n + 1 && fw.Sets[n] == "11=EVB-D4 · COM3 · 7001", "1분 뒤 다른 제목 → 한 번 다시 씀");
            fw.Retitle(11, "Tera Term - [disconnected] VT");
            tt.Apply(rows1, t0.AddSeconds(63));
            tt.Apply(rows1, t0.AddSeconds(100));
            ctx.Check(fw.Sets.Count == n + 1, "다시 쓴 뒤에도 1분에 한 번까지");
            fw.Retitle(11, "EVB-D4 · COM3 · 7001 - 127.0.0.1:7001 VT");
            tt.Apply(rows1, t0.AddSeconds(200));
            ctx.Check(fw.Sets.Count == n + 1, "기대 제목으로 시작하는 제목(TeraTerm 꼬리)은 같다고 봄");

            fw.Procs.Remove(102);                                           // 7002 터미널 닫힘
            fw.Procs[103] = 9999;                                           // 7003 pid 재사용(시작 시각 다름)
            var rows2 = new List<CableRow> { TRow(7001, "EVB-D4", "COM3"), TRow(7002, "B", "COM9"), TRow(7003, "C", "COM5") };
            n = fw.Sets.Count;
            tt.Apply(rows2, t0.AddSeconds(201));
            ctx.Check(fw.Sets.Count == n && !File.Exists(Path.Combine(TermRecords.Dir(hub), "102.json")) && !File.Exists(Path.Combine(TermRecords.Dir(hub), "103.json"))
                      && tt.Pids.OrderBy(x => x).SequenceEqual(new[] { 101, 104 }), "프로세스 없음·pid 재사용(시작 시각 다름) → 기록을 지우고 그 창은 건드리지 않음");
            var tt2 = new TermTitler(hub, lines.Add, fw);                   // 감독 재시작: 남은 기록을 다시 읽음
            tt2.Apply(new List<CableRow> { TRow(7001, "EVB-D4-2", "COM3") }, t0.AddSeconds(300));
            ctx.Check(fw.Sets.Count == n + 1 && fw.Sets[n] == "11=EVB-D4-2 · COM3 · 7001" && fw.Wins[(IntPtr)21].Title == "COM3 · 7001",
                      "감독이 다시 떠도 남은 기록(pid+시작 시각)으로 이어서 바꿈");
        }

        // ================================================================== open-front
        [Scenario("open-front", "open 의 기존 창(§2.3 f, A2-4·A2-5): 가짜 프로세스 목록으로 TeraTerm 같은 포트 → 앞으로(실행 없음)·포트 경계(1744≠17445)·전면 거부 → FlashWindowEx, PuTTY -P <p> → 앞으로·(inactive) 끊긴 창 → 새로, MobaXterm 은 늘 새 탭, --from balloon 표시. C-9: 설치본(운영 경로)에서 코어·감독 모두 없음 → open 이 감독을 띄우고 코어를 기다린 뒤 실행(이름 = ls), 감독 있음·update mutex·정지 → 띄우지 않음. 실제 시스템 무변경",
                  Order = 773, TimeoutS = 300)]
        public static void OpenFront(ScenarioContext ctx)
        {
            var real = RealSystem.Capture(ctx);
            var t = TermRoot.Create(ctx, "front", 17443);
            t.Touch(t.TT); t.Touch(t.PT); t.Touch(t.MX);
            t.Prefs("{\"terminal\":\"teraterm\"}\n");
            File.WriteAllText(t.P("fake_procs.json"), "[" +
                "{\"pid\":4242,\"name\":\"ttermpro.exe\",\"cmd\":\"\\\"C:\\\\tt\\\\ttermpro.exe\\\" 127.0.0.1:17445 /nossh /T=1 /W=\\\"EVB-A1 (17445)\\\"\",\"title\":\"EVB-A1 (17445) - Tera Term VT\"}," +
                "{\"pid\":4243,\"name\":\"ttermpro.exe\",\"cmd\":\"ttermpro.exe 127.0.0.1:17446 /nossh\",\"title\":\"x\",\"fg\":false}," +
                "{\"pid\":5151,\"name\":\"putty.exe\",\"cmd\":\"putty.exe -telnet 127.0.0.1 -P 17447\",\"title\":\"EVB-A1 (17447) - PuTTY\"}," +
                "{\"pid\":5152,\"name\":\"putty.exe\",\"cmd\":\"putty.exe -telnet 127.0.0.1 -P 17448\",\"title\":\"127.0.0.1 - PuTTY (inactive)\"}," +
                "{\"pid\":6161,\"name\":\"MobaXterm.exe\",\"cmd\":\"MobaXterm.exe -newtab \\\"telnet 127.0.0.1 17449\\\"\",\"title\":\"MobaXterm\"}]");
            var o = t.Open(17445);
            ctx.Check(o.Code == 0 && o.Line == "open 17445 → front pid=4242 (teraterm)" && t.Launches() == 0, "TeraTerm 같은 포트 창 있음 → 앞으로(front pid=4242), 새로 실행 안 함");
            ctx.Check(t.Log().Contains("[ext:fake] 창 앞으로 pid=4242 → front — 시험 모드라 창을 건드리지 않음"), "창 앞으로는 시험 모드 fake(화면 무변경)");
            o = t.Open(17445, "--from", "balloon");
            ctx.Check(o.Code == 0 && t.Log().Contains("[open] open 17445 (from balloon) → front pid=4242 (teraterm)"), "풍선 경로: hub.log 'open 17445 (from balloon) → front pid=4242'");
            o = t.Open(1744);
            ctx.Check(o.Code == 0 && o.Line.StartsWith("open 1744 → launch teraterm", StringComparison.Ordinal) && t.Launches() == 1, "포트 경계: 1744 는 127.0.0.1:17445 창과 다름 → 새로 실행");
            o = t.Open(17446);
            ctx.Check(o.Code == 0 && o.Line == "open 17446 → front pid=4243 (teraterm, 전면 거부 → FlashWindowEx)" && t.Launches() == 1, "전면 거부(fg:false) → FlashWindowEx, 새로 실행 안 함");
            t.Prefs("{\"terminal\":\"putty\"}\n");
            o = t.Open(17447);
            ctx.Check(o.Code == 0 && o.Line == "open 17447 → front pid=5151 (putty)" && t.Launches() == 1, "PuTTY -P 17447 창 → 앞으로");
            o = t.Open(17448);
            ctx.Check(o.Code == 0 && o.Line.StartsWith("open 17448 → launch putty pid=0", StringComparison.Ordinal) && t.Launches() == 2
                      && t.Log().Contains("끊긴 PuTTY 창 pid=5152 '127.0.0.1 - PuTTY (inactive)' — 새로 엶"), "끊긴 PuTTY((inactive)) → 새로 실행");
            t.Prefs("{\"terminal\":\"mobaxterm\"}\n");
            o = t.Open(17449);
            var o2 = t.Open(17449);
            ctx.Check(o.Code == 0 && o2.Code == 0 && o.Line.StartsWith("open 17449 → launch mobaxterm", StringComparison.Ordinal) && o2.Line.StartsWith("open 17449 → launch mobaxterm", StringComparison.Ordinal)
                      && t.Launches() == 4, "MobaXterm: 같은 포트 프로세스가 있어도 열 때마다 새 탭(실행 2회)");

            // C-9: 설치본(운영 경로, UART_HUB_DIR 없음)
            var r = InstallRig.Create(ctx, "c9", 17490, 17495);
            string tt = Path.Combine(r.Root, "ProgramFilesX86", "teraterm5", "ttermpro.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(tt));
            File.WriteAllText(tt, "fake");
            Upd.InstallAndWait(ctx, r, Dev, null, 1, "설치");
            string hub = Upd.HubDir(r);
            string HubLog() => Upd.HubLog(hub);
            Upd.Ls(ctx, r, "설치 뒤", r.Cable);
            ctx.Check((HubFiles.ReadShared(Path.Combine(r.InstallDir, "prefs.json")) ?? "").Contains("ttermpro.exe"), "설치기 prefs.json 에 가짜 TeraTerm");
            int sp = Upd.Kind(r, "sup").Single(), cp = Upd.Kind(r, "core").Single();
            Upd.KillBoth(ctx, r, sp, cp);
            var c = r.Run(r.InstalledExe, "open", "17495");
            ctx.Check(c.Code == 0 && Regex.IsMatch(HubLog(), @"\[open\] open 17495: 자가 복구\(C-9\) — 코어 미응답·감독 없음 → 감독 다시 띄움 pid=\d+"),
                      "코어·감독 모두 없음 → open 이 감독을 띄움(C-9, hub.log)");
            ctx.Check(HubLog().Contains("[open] open 17495: 코어 응답(") && c.Out.Trim() == "open 17495 → launch teraterm pid=0 " + tt + " 127.0.0.1:17495 /nossh /T=1 /W=\"" + r.Com + " · 17495\" (기본 경로)",
                      "코어를 기다린 뒤 실행, 이름 = ls 의 COM(보드명 전): " + c.Out.Trim().Replace(r.Root, "<root>"));
            ctx.WaitUntil(() => Upd.Kind(r, "sup").Count == 1 && Upd.Kind(r, "core").Count == 1, 10000, "감독 1·코어 1");
            int heal = Upd.Count(HubLog(), "자가 복구(C-9)");
            c = r.Run(r.InstalledExe, "open", "17495");
            ctx.Check(c.Code == 0 && Upd.Count(HubLog(), "자가 복구(C-9)") == heal && Upd.Kind(r, "sup").Count == 1, "허브 실행 중 → 감독을 더 띄우지 않음");
            // 감독 있음·코어 없음(백오프 흉내: 코어만 kill → 감독이 다시 띄우기 전) → 띄우지 않음
            sp = Upd.Kind(r, "sup").Single(); cp = Upd.Kind(r, "core").Single();
            Upd.KillBoth(ctx, r, sp, cp);
            using (var upd = new Mutex(false, SystemAccess.MutexNameFor(r.Root, "update")))
            {
                c = r.Run(r.InstalledExe, "open", "17495");
                ctx.Check(c.Code == 0 && HubLog().Contains("[open] open 17495: 코어 미응답 — 감독을 띄우지 않음(업데이트 중(update mutex))"), "update mutex 있음 → 감독을 띄우지 않음(터미널은 엶)");
                Thread.Sleep(2500);
                ctx.Check(Upd.Kind(r, "sup").Count == 0 && Upd.Kind(r, "core").Count == 0, "update mutex 동안 감독·코어 0");
            }
            File.WriteAllText(Path.Combine(hub, "control.json"), "{\"stopped\":true,\"by\":\"test\"}\n");
            c = r.Run(r.InstalledExe, "open", "17495");
            ctx.Check(c.Code == 0 && HubLog().Contains("[open] open 17495: 코어 미응답 — 감독을 띄우지 않음(정지 상태(control.json stopped))"), "정지 상태 → 감독을 띄우지 않음");
            Thread.Sleep(2500);
            ctx.Check(Upd.Kind(r, "sup").Count == 0 && Upd.Count(HubLog(), "자가 복구(C-9)") == heal, "정지 상태: 감독 0, 자가 복구 1회 그대로");
            File.WriteAllText(Path.Combine(hub, "control.json"), "{\"stopped\":false,\"by\":\"test\"}\n");
            ctx.Check(!HubLog().Contains("TestIsolationViolation") && !File.Exists(Path.Combine(r.Root, "isolation_violations.log"))
                      && !File.Exists(t.P("isolation_violations.log")), "TestIsolationViolation 0");
            r.CheckNoProcs("정리");
            real.Verify(ctx, t.R, r);
        }
    }
}
