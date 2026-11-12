// US-107 시나리오(계획 T3 코어 쪽, C-3·C-4): 첫 연결 판정(pending·cable decide), 트레이 제외·해제(cable exclude/include), 설정 키(ask_new_cables·sup_watch_s·ModeSet).
// "COM 을 한 번도 열지 않음"은 공급자 Create 횟수로 센다(CableManager 는 Create 직후에만 Open 한다). 스캔 수는 공급자 Scan 호출 수.
// 포트: 제어 17181~17182, 케이블 17600·17620(base_port) — 기존 시나리오와 겹치지 않는다.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace UartHub.Testing
{
    /// <summary>FakePortProvider 를 감싸 케이블별 Create(= COM 열기 시도) 횟수와 Scan 횟수를 센다.</summary>
    internal sealed class CountingProvider : IPortProvider
    {
        readonly FakePortProvider inner;
        readonly ConcurrentDictionary<string, int> creates = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        long scans;

        public CountingProvider(FakePortProvider inner) { this.inner = inner; }
        public FakePortProvider Inner => inner;
        public long Scans => Interlocked.Read(ref scans);
        public int Creates(string cableId) => creates.TryGetValue(cableId, out int n) ? n : 0;

        public IReadOnlyList<PortInfo> Scan()
        {
            Interlocked.Increment(ref scans);
            return inner.Scan().Select(p => new PortInfo { CableId = p.CableId, Com = p.Com, Description = p.Description, Provider = this }).ToList();
        }

        public IPort Create(PortInfo info, int baud, Config cfg)
        {
            creates.AddOrUpdate(info.CableId, 1, (_, n) => n + 1);
            return inner.Create(info, baud, cfg);
        }
    }

    internal static class CableScenarios
    {
        static Hub Boot(ScenarioContext ctx, Config cfg, IPortProvider prov, string what)
        {
            var hub = new Hub(cfg, null, new[] { prov });
            hub.Start();
            ctx.Defer(hub.Stop);
            ctx.Check(hub.Control.Listening, what + ": 제어 포트 127.0.0.1:" + cfg.ControlPort + " listen" + (hub.Control.BindError == null ? "" : " (" + hub.Control.BindError + ")"));
            return hub;
        }

        static JsonElement Ctl(Hub hub, params string[] argv)
            => CtlClient.Call(hub.Config.ControlPort, File.ReadAllText(hub.Control.TokenPath).Trim(), 15000, argv);

        static JsonElement LsOf(JsonElement ls, string id)
        {
            foreach (var c in ls.GetProperty("cables").EnumerateArray())
                if (J.S(c, "cable") == id) return c;
            throw new ScenarioFailure("ls 에 " + id + " 없음: " + J.Show(ls));
        }

        static CableStatus? St(Hub hub, string id) => hub.Cables.Find(id)?.Status;

        /// <summary>state.json 의 (assign 포트 또는 -1, released 여부).</summary>
        static (long Port, bool Released) StateOf(Config cfg, string id)
        {
            using var d = JsonDocument.Parse(File.ReadAllText(Path.Combine(cfg.EffectiveHubDir, "state.json")));
            long p = d.RootElement.GetProperty("assign").TryGetProperty(id, out var v) ? v.GetInt64() : -1;
            bool rel = d.RootElement.GetProperty("released").EnumerateArray().Any(x => x.GetString() == id);
            return (p, rel);
        }

        static int Count(string hay, string needle)
        {
            int n = 0;
            for (int i = hay.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = hay.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
            return n;
        }

        static void Code(ScenarioContext ctx, JsonElement e, long exit, string status, string what, string blocked = null, string errHas = null)
        {
            bool ok = J.N(e, "exit") == exit && J.S(e, "status") == status && (blocked == null || J.S(e, "blocked_reason") == blocked)
                      && (errHas == null || (J.S(e, "error") ?? "").Contains(errHas));
            ctx.Check(ok, what + " → " + exit + " " + status + (blocked != null ? " blocked_reason=" + blocked : "") + " (" + J.Show(e) + ")");
        }

        // ================================================================== first-connect
        [Scenario("first-connect", "첫 연결 판정(C-3): ask_new_cables=true 에서 처음 보는 케이블은 pending(COM 한 번도 안 엶·다른 프로그램이 열 수 있음·ls pending·대상 명령 102 pending), 예 → 2스캔 안에 attached·포트 배정, 아니오 → COM 미개방·포트 배정(소모)·released, 코어 재시작 뒤 답 유지·무응답은 다시 pending, alias 만 있는 항목은 pending, mode:auto 명시는 묻지 않고 붙지만 제외하면 released 가 이김, mode:ignore 명시는 cable decide/exclude/include 에 106, 기존 assign 은 보드, cli 는 cable 을 보내지 않고 106", Order = 760, TimeoutS = 120)]
        public static void FirstConnect(ScenarioContext ctx)
        {
            var parsed = Config.Parse(@"{ ""ask_new_cables"": true,
                ""cables"": { ""SIMFCAL"": { ""alias"": ""fc-alias"" }, ""SIMFCAU"": { ""mode"": ""auto"" }, ""SIMFCIG"": { ""mode"": ""ignore"" } } }");
            var cfg = HubRig.MakeConfig(ctx, 17181, c => { c.BasePort = 17600; c.AskNewCables = parsed.AskNewCables; c.Cables = parsed.Cables; });
            ctx.Check(cfg.AskNewCables && !cfg.CableFor("SIMFCAL").ModeSet && cfg.CableFor("SIMFCAU").ModeSet && cfg.CableFor("SIMFCIG").Mode == "ignore",
                      "전제: ask_new_cables true, SIMFCAL alias 만(모드 명시 아님), SIMFCAU mode auto 명시, SIMFCIG mode ignore 명시");
            Directory.CreateDirectory(cfg.EffectiveHubDir);
            File.WriteAllText(Path.Combine(cfg.EffectiveHubDir, "state.json"), "{\"assign\":{\"SIMFCAS\":17609},\"released\":[],\"boards\":{}}");
            // Y=예, N=아니오, U=무응답, AL=alias 만, AU=mode auto 명시, IG=mode ignore 명시, AS=기존 assign
            var plan = new[] { ("SIMFCY", "COM170"), ("SIMFCN", "COM171"), ("SIMFCU", "COM172"), ("SIMFCAL", "COM173"), ("SIMFCAU", "COM174"), ("SIMFCIG", "COM175"), ("SIMFCAS", "COM176") };
            var inner = new FakePortProvider();
            var boards = new Dictionary<string, FakeBoard>();
            foreach (var (id, com) in plan)
            {
                var b = new FakeBoard(new FakeBoardOptions { Hostname = id });
                ctx.Defer(b.PowerOff);
                boards[id] = b;
                inner.Plug(b, id, com);
            }
            var prov = new CountingProvider(inner);
            var hub = Boot(ctx, cfg, prov, "코어 1");
            ctx.WaitUntil(() => St(hub, "SIMFCY") == CableStatus.Pending && St(hub, "SIMFCN") == CableStatus.Pending && St(hub, "SIMFCU") == CableStatus.Pending
                                && St(hub, "SIMFCAL") == CableStatus.Pending && St(hub, "SIMFCAU") == CableStatus.Attached && St(hub, "SIMFCIG") == CableStatus.Ignored
                                && St(hub, "SIMFCAS") == CableStatus.Attached, 5000,
                          "판정: 처음 보는 Y·N·U·alias 만 AL → pending, mode auto 명시 AU → attached(묻지 않음), mode ignore 명시 IG → ignored, 기존 assign AS → attached");
            Thread.Sleep(300);                                                   // 스캔 6회
            var neverOpened = new[] { "SIMFCY", "SIMFCN", "SIMFCU", "SIMFCAL", "SIMFCIG" };
            ctx.Check(neverOpened.All(id => prov.Creates(id) == 0 && !boards[id].IsOpen && boards[id].TotalReceived == 0),
                      "pending·ignored 케이블: COM 열기 시도 0회·보드 수신 0바이트 (스캔 " + prov.Scans + "회 동안)");
            ctx.Check(prov.Creates("SIMFCAU") == 1 && prov.Creates("SIMFCAS") == 1, "mode auto 명시·기존 assign 은 바로 COM 을 엶(Create 1회)");
            var cY0 = hub.Cables.Find("SIMFCY");
            ctx.Check(cY0.TcpPort == 0 && cY0.Writer == null && cY0.Ring == null && StateOf(cfg, "SIMFCY").Port == -1 && StateOf(cfg, "SIMFCU").Port == -1,
                      "pending 객체: 포트 0·writer·링 없음, state.json 에 배정 없음(비영속)");
            ctx.Check(hub.Cables.Find("SIMFCAS").TcpPort == 17609, "기존 assign 포트 17609 그대로");

            // ---- ls / ls --detail
            var ls = Ctl(hub, "ls");
            var eY = LsOf(ls, "SIMFCY");
            ctx.Check(J.S(eY, "status") == "pending" && J.N(eY, "port") == 0 && J.S(eY, "com") == "COM170" && J.IsNull(eY, "board"), "ls: SIMFCY status pending, port 0, com COM170 (" + J.Show(eY) + ")");
            ctx.Check(!J.Has(eY, "description") && !J.Has(eY, "mode_explicit"), "기본 ls 의 케이블 필드는 Phase A 그대로(description·mode_explicit 없음)");
            var lsd = Ctl(hub, "ls", "--detail");
            var dY = LsOf(lsd, "SIMFCY");
            var dAL = LsOf(lsd, "SIMFCAL");
            var dAU = LsOf(lsd, "SIMFCAU");
            var dIG = LsOf(lsd, "SIMFCIG");
            ctx.Check(J.S(dY, "status") == "pending" && J.S(dY, "description") == "FakeBoard SIMFCY" && J.IsNull(dY, "mode_explicit"),
                      "ls --detail: SIMFCY description(장치 이름) 'FakeBoard SIMFCY', mode_explicit null (" + J.Show(dY) + ")");
            ctx.Check(J.S(dAL, "status") == "pending" && J.S(dAL, "alias") == "fc-alias" && J.IsNull(dAL, "mode_explicit"), "ls --detail: alias 만 있는 SIMFCAL 은 pending, mode_explicit null");
            ctx.Check(J.S(dAU, "status") == "attached" && J.S(dAU, "mode_explicit") == "auto", "ls --detail: SIMFCAU attached, mode_explicit auto");
            ctx.Check(J.S(dIG, "status") == "ignored" && J.S(dIG, "mode_explicit") == "ignore", "ls --detail: SIMFCIG ignored, mode_explicit ignore");

            // ---- 대상 명령 → 102 + blocked_reason
            Code(ctx, Ctl(hub, "run", "COM170", "echo fc", "--timeout", "2"), 102, "no_cable", "pending 케이블에 run(COM170)", "pending");
            Code(ctx, Ctl(hub, "status", "fc-alias"), 102, "no_cable", "pending 케이블에 status(alias fc-alias)", "pending");
            Code(ctx, Ctl(hub, "send", "COM175", "x"), 102, "no_cable", "ignored 케이블에 send(COM175)", "ignored");
            ctx.Check(boards["SIMFCY"].TotalReceived == 0 && boards["SIMFCIG"].TotalReceived == 0, "102 뒤에도 보드 수신 0바이트");
            string hl = LogFiles.ReadShared(hub.Log.Path);
            ctx.Check(Count(hl, "cable pending SIMFCY COM170") == 1 && Count(hl, "cable pending SIMFCAU") == 0 && Count(hl, "cable pending SIMFCAS") == 0,
                      "hub.log: 'cable pending SIMFCY COM170' 1줄, 명시 auto·기존 assign 은 pending 줄 없음");

            // ---- 확인 대기 중에는 다른 프로그램(TeraTerm 시리얼 등)이 COM 을 열 수 있다
            using (var other = new BoardConsole(boards["SIMFCU"]))
            {
                other.Send("\r");
                other.WaitFor(@"root@SIMFCU:", 0, 3000);
                ctx.Check(prov.Creates("SIMFCU") == 0, "pending 중 다른 프로그램이 COM172 를 열어 보드 프롬프트를 봄(허브는 열지 않음)");
            }

            // ---- cli 는 cable 을 보내지 않는다(클라이언트 쪽 제한)
            var rig = new HubRig { Ctx = ctx, Cfg = cfg, Ctl = cfg.ControlPort };
            var u = rig.Uart(null, "cable", "decide", "SIMFCY", "board", "--json");
            using (var ud = JsonDocument.Parse(u.Out))
                ctx.Check(u.Code == 106 && J.S(ud.RootElement, "status") == "rejected" && (J.S(ud.RootElement, "error") ?? "").Contains("트레이에서만"),
                          "cli cable decide → 106 rejected '트레이에서만'");
            Thread.Sleep(150);
            ctx.Check(St(hub, "SIMFCY") == CableStatus.Pending && Count(LogFiles.ReadShared(hub.Log.Path), "cable decide") == 0, "cli 의 cable 은 코어에 가지 않음(여전히 pending, hub.log 에 decide 없음)");

            // ---- mode ignore 명시 → 106, 그 밖의 거부
            Code(ctx, Ctl(hub, "cable", "exclude", "SIMFCIG"), 106, "rejected", "mode ignore 명시 케이블에 cable exclude", null, "config.json 이 제외함");
            Code(ctx, Ctl(hub, "cable", "include", "SIMFCIG"), 106, "rejected", "mode ignore 명시 케이블에 cable include", null, "config.json 이 제외함");
            Code(ctx, Ctl(hub, "cable", "decide", "SIMFCIG", "board"), 106, "rejected", "mode ignore 명시 케이블에 cable decide", null, "config.json 이 제외함");
            ctx.Check(St(hub, "SIMFCIG") == CableStatus.Ignored && prov.Creates("SIMFCIG") == 0 && !StateOf(cfg, "SIMFCIG").Released, "mode ignore 명시 케이블은 그대로 ignored, COM 미개방, state.json 무변경");
            Code(ctx, Ctl(hub, "cable", "exclude", "SIMFCU"), 106, "rejected", "pending 케이블에 cable exclude(답은 decide 로)", null, "decide");
            Code(ctx, Ctl(hub, "cable", "decide", "SIMFCAS", "board"), 106, "rejected", "이미 붙은 케이블에 cable decide", null, "pending");
            Code(ctx, Ctl(hub, "cable", "decide", "SIMFCY", "maybe"), 106, "rejected", "cable decide 답은 board|ignore");
            Code(ctx, Ctl(hub, "cable", "decide", "NOSUCHCAB", "board"), 102, "no_cable", "모르는 케이블 ID");

            // ---- ① 예 → 2스캔 안에 attached·포트 배정
            long s0 = prov.Scans;
            var dy = Ctl(hub, "cable", "decide", "SIMFCY", "board");
            Code(ctx, dy, 0, "ok", "cable decide SIMFCY board");
            long portY = J.N(dy, "port");
            long atScan = -1;
            ctx.WaitUntil(() => { var c = hub.Cables.Find("SIMFCY"); if (c != null && c.IsAttached) { atScan = prov.Scans; return true; } return false; }, 3000, "예 → SIMFCY attached");
            var cY = hub.Cables.Find("SIMFCY");
            ctx.Check(atScan - s0 <= 2, "예 → " + (atScan - s0) + "스캔 안에 attached(≤ 2)");
            ctx.Check(portY >= 17600 && cY.TcpPort == portY && !ReferenceEquals(cY, cY0) && cY.Writer != null && StateOf(cfg, "SIMFCY") == (portY, false),
                      "예 → 포트 " + portY + " 배정(state.json assign 영속, released 아님), 새 케이블 객체(writer 있음)");
            ctx.WaitUntil(() => hub.Telnet.StateOf(cY) == "listening", 3000, "예 → telnet :" + portY + " listening");

            // ---- ② 아니오 → COM 미개방·포트 배정(소모)·released
            var dn = Ctl(hub, "cable", "decide", "SIMFCN", "ignore");
            Code(ctx, dn, 0, "ok", "cable decide SIMFCN ignore");
            long portN = J.N(dn, "port");
            ctx.WaitUntil(() => St(hub, "SIMFCN") == CableStatus.Released, 3000, "아니오 → SIMFCN released");
            Thread.Sleep(300);
            var cN = hub.Cables.Find("SIMFCN");
            ctx.Check(prov.Creates("SIMFCN") == 0 && !boards["SIMFCN"].IsOpen && boards["SIMFCN"].TotalReceived == 0, "아니오 → COM171 열기 시도 0회(스캔 6회 뒤), 보드 수신 0바이트");
            ctx.Check(portN >= 17600 && portN != portY && cN.TcpPort == portN && cN.Writer != null && cN.Ring != null && StateOf(cfg, "SIMFCN") == (portN, true),
                      "아니오 → 포트 " + portN + " 배정(소모)·released 영속 — Phase A released 케이블 모양(포트·writer·링)");
            var lN = LsOf(Ctl(hub, "ls"), "SIMFCN");
            ctx.Check(J.S(lN, "status") == "released" && J.N(lN, "port") == portN, "ls: SIMFCN status released, port " + portN);
            Code(ctx, Ctl(hub, "run", portN.ToString(), "echo fcn", "--timeout", "2"), 102, "no_cable", "released 케이블에 run(포트 " + portN + ")", "released");
            using (var other = new BoardConsole(boards["SIMFCN"]))
            {
                other.Send("\r");
                other.WaitFor(@"root@SIMFCN:", 0, 3000);
            }
            ctx.Check(prov.Creates("SIMFCN") == 0, "아니오 뒤 다른 프로그램이 COM171 을 엶(허브는 열지 않음)");
            Code(ctx, Ctl(hub, "cable", "decide", "SIMFCY", "board"), 106, "rejected", "답한 케이블에 다시 decide", null, "pending");

            // ---- mode auto 명시: 제외하면 released 가 이김
            var cAU = hub.Cables.Find("SIMFCAU");
            int portAU = cAU.TcpPort;
            Code(ctx, Ctl(hub, "cable", "exclude", "SIMFCAU"), 0, "ok", "mode auto 명시 케이블에 cable exclude");
            int crAU = prov.Creates("SIMFCAU");
            Thread.Sleep(300);
            ctx.Check(St(hub, "SIMFCAU") == CableStatus.Released && !boards["SIMFCAU"].IsOpen && prov.Creates("SIMFCAU") == crAU && StateOf(cfg, "SIMFCAU") == (portAU, true),
                      "mode auto 명시라도 제외(released)가 이김: 스캔 6회 동안 released·COM 닫힘·다시 안 엶");

            // ---- 코어 재시작: 답 유지, 무응답은 다시 pending
            var before = new[] { "SIMFCN", "SIMFCU", "SIMFCAL", "SIMFCIG", "SIMFCAU" }.ToDictionary(x => x, prov.Creates);   // 답 "예"인 Y·기존 assign AS 는 다시 연다
            hub.Stop();
            var hub2 = Boot(ctx, cfg, prov, "코어 2(재시작)");
            ctx.WaitUntil(() => St(hub2, "SIMFCY") == CableStatus.Attached && St(hub2, "SIMFCN") == CableStatus.Released && St(hub2, "SIMFCU") == CableStatus.Pending
                                && St(hub2, "SIMFCAL") == CableStatus.Pending && St(hub2, "SIMFCAU") == CableStatus.Released && St(hub2, "SIMFCIG") == CableStatus.Ignored
                                && St(hub2, "SIMFCAS") == CableStatus.Attached, 5000,
                          "재시작 뒤: 예 → attached, 아니오 → released, 무응답 U·alias AL → 다시 pending, 제외한 auto 명시 → released, ignore 명시 → ignored, 기존 assign → attached");
            Thread.Sleep(300);
            ctx.Check(hub2.Cables.Find("SIMFCY").TcpPort == portY && hub2.Cables.Find("SIMFCN").TcpPort == portN && hub2.Cables.Find("SIMFCAU").TcpPort == portAU
                      && hub2.Cables.Find("SIMFCAS").TcpPort == 17609 && hub2.Cables.Find("SIMFCU").TcpPort == 0,
                      "재시작 뒤 포트 그대로(Y " + portY + ", N " + portN + ", AU " + portAU + ", AS 17609), U 는 포트 0");
            ctx.Check(before.All(kv => prov.Creates(kv.Key) == kv.Value) && before.Keys.All(k => !boards[k].IsOpen),
                      "재시작 뒤 N·U·AL·IG·AU 의 COM 열기 시도 0회(스캔 6회 동안) (" + string.Join(", ", before.Select(kv => kv.Key + " " + kv.Value + "→" + prov.Creates(kv.Key) + (boards[kv.Key].IsOpen ? " open" : ""))) + ")");
            hl = LogFiles.ReadShared(hub2.Log.Path);
            ctx.Check(Count(hl, "cable pending SIMFCU ") == 2 && Count(hl, "cable pending SIMFCAL ") == 2 && Count(hl, "cable pending SIMFCY ") == 1 && Count(hl, "cable pending SIMFCN ") == 1,
                      "hub.log: 무응답 U·AL 은 코어마다 다시 pending(2줄), 답한 Y·N 은 다시 묻지 않음(1줄)");
            ctx.Check(StateOf(cfg, "SIMFCU").Port == -1 && !StateOf(cfg, "SIMFCU").Released, "pending 은 state.json 에 남지 않음");
            var l2 = Ctl(hub2, "ls");
            ctx.Check(J.S(LsOf(l2, "SIMFCU"), "status") == "pending" && J.S(LsOf(l2, "SIMFCN"), "status") == "released" && J.S(LsOf(l2, "SIMFCY"), "status") == "attached",
                      "재시작 뒤 ls: U pending, N released, Y attached");
        }

        // ================================================================== cable-exclude
        [Scenario("cable-exclude", "트레이 제외·해제(C-3 ③④): attached → cable exclude: COM 닫힘·telnet 분리 배너(연결 유지)·released(배정 유지, 대상 명령 102 released), include → 같은 포트로 재attach(재연결 배너·run), 다시 제외 → 코어 재시작 뒤에도 released(COM 안 엶) → include → 같은 포트로 재attach", Order = 761, TimeoutS = 120)]
        public static void CableExclude(ScenarioContext ctx)
        {
            const string Id = "SIMCABCX";
            var cfg = HubRig.MakeConfig(ctx, 17182, c => c.BasePort = 17620);
            var board = new FakeBoard(new FakeBoardOptions { Hostname = "SIMCX" });
            ctx.Defer(board.PowerOff);
            var inner = new FakePortProvider();
            inner.Plug(board, Id, "COM180");
            var prov = new CountingProvider(inner);
            var r = HubRig.Boot(ctx, cfg, board, prov, Id, true);
            int port = r.Cab.TcpPort;
            var cab = r.Cab;
            ctx.WaitUntil(() => r.Hub.Telnet.StateOf(cab) == "listening", 3000, "telnet 127.0.0.1:" + port + " listening");
            using var a = TelnetScenarios.Human(r, port, 1);
            Thread.Sleep(300);
            int m0 = (int)a.Count;

            // ---- ③ 제외
            long seq0 = cab.AttachSeq;
            var x = r.Call("cable", "exclude", Id);
            Code(ctx, x, 0, "ok", "cable exclude " + Id);
            ctx.Check(J.S(x, "cable_status") == "released" && J.N(x, "port") == port, "응답: cable_status released, port " + port);
            ctx.Check(!board.IsOpen && cab.Status == CableStatus.Released && !cab.IsAttached, "exclude → COM 닫힘(보드 쪽 open false), status released");
            ctx.WaitUntil(() => TnClient.Contains(a.Since(m0), TelnetScenarios.B(TelnetScenarios.DetachBanner("COM180"))), 3000, "telnet 분리 배너");
            int cr = prov.Creates(Id);
            Thread.Sleep(300);
            ctx.Check(prov.Creates(Id) == cr && !board.IsOpen && cab.Status == CableStatus.Released && cab.AttachSeq == seq0, "제외 뒤 스캔 6회 동안 COM 다시 안 엶(released 유지)");
            ctx.Check(!a.ClosedByPeer && r.Hub.Telnet.StateOf(cab) == "listening", "telnet 연결·listener 유지(평소 분리와 같음)");
            ctx.Check(StateOf(cfg, Id) == (port, true), "state.json: released 영속, assign 포트 " + port + " 유지");
            var lx = LsOf(r.Call("ls"), Id);
            ctx.Check(J.S(lx, "status") == "released" && J.N(lx, "port") == port, "ls: status released, port " + port);
            long rec = board.TotalReceived;
            Code(ctx, r.Call("run", r.P, "echo cx", "--timeout", "2"), 102, "no_cable", "released 케이블에 run", "released");
            Code(ctx, r.Call("cable", "exclude", Id), 0, "ok", "이미 제외된 케이블에 다시 exclude(그대로)");
            ctx.Check(board.TotalReceived == rec, "보드 수신 그대로(0바이트)");
            string hl = LogFiles.ReadShared(r.Hub.Log.Path);
            ctx.Check(hl.Contains("cable exclude " + Id) && hl.Contains("detach " + Id + " COM180: excluded"), "hub.log: 'cable exclude', 'detach … excluded'");

            // ---- ④ 해제(같은 코어) → 같은 포트로 재attach
            var inc = r.Call("cable", "include", Id);
            Code(ctx, inc, 0, "ok", "cable include " + Id);
            ctx.WaitUntil(() => cab.IsAttached && cab.AttachSeq == seq0 + 1, 3000, "include → 재attach");
            ctx.Check(ReferenceEquals(r.Hub.Cables.Find(Id), cab) && cab.TcpPort == port && StateOf(cfg, Id) == (port, false), "같은 케이블·같은 포트 " + port + ", released 지움");
            ctx.WaitUntil(() => TnClient.Contains(a.Since(m0), TelnetScenarios.B(TelnetScenarios.AttachBanner("COM180"))), 3000, "telnet 재연결 배너(같은 연결)");
            ctx.WaitUntil(() => r.T.Generation >= 2, 3000, "추적기 복원");
            r.Human("\r");
            r.WaitState(PromptState.AtPrompt, 3000, "Enter → 프롬프트");
            r.IdleWait();
            ctx.Check(J.Seq(J.A(r.Call("run", r.P, "echo cx_back", "--timeout", "5"), "out"), "cx_back"), "재attach 뒤 run 정상");

            // ---- 다시 제외 → 코어 재시작 뒤에도 released
            Code(ctx, r.Call("cable", "exclude", Id), 0, "ok", "다시 cable exclude");
            ctx.Check(!board.IsOpen && cab.Status == CableStatus.Released, "COM 닫힘, released");
            a.Dispose();
            r.Hub.Stop();
            int crBefore = prov.Creates(Id);
            var hub2 = Boot(ctx, cfg, prov, "코어 2(재시작)");
            ctx.WaitUntil(() => St(hub2, Id) == CableStatus.Released, 3000, "재시작 뒤 released");
            Thread.Sleep(300);
            var c2 = hub2.Cables.Find(Id);
            ctx.Check(prov.Creates(Id) == crBefore && !board.IsOpen && c2.Status == CableStatus.Released && c2.TcpPort == port,
                      "재시작 뒤에도 released: 스캔 6회 동안 COM 열기 시도 0회, 포트 " + port + " 그대로");
            var l2 = LsOf(Ctl(hub2, "ls"), Id);
            ctx.Check(J.S(l2, "status") == "released" && J.N(l2, "port") == port, "재시작 뒤 ls: released, port " + port);

            // ---- 해제 → 같은 포트로 재attach(새 코어)
            Code(ctx, Ctl(hub2, "cable", "include", Id), 0, "ok", "재시작 뒤 cable include");
            ctx.WaitUntil(() => c2.IsAttached, 3000, "include → attached");
            ctx.Check(ReferenceEquals(hub2.Cables.Find(Id), c2) && c2.TcpPort == port && StateOf(cfg, Id) == (port, false), "같은 포트 " + port + " 로 재attach, released 지움");
            ctx.WaitUntil(() => hub2.Telnet.StateOf(c2) == "listening", 3000, "telnet :" + port + " listening");
            var r2 = new HubRig { Ctx = ctx, Hub = hub2, Board = board, Prov = prov, Cfg = cfg, Ctl = cfg.ControlPort, Cab = c2, P = port.ToString(),
                                  Token = File.ReadAllText(hub2.Control.TokenPath).Trim() };
            ctx.WaitUntil(() => { r2.T = hub2.Tracker(c2); return r2.T != null && r2.T.Generation > 0; }, 3000, "추적기 준비");
            r2.Human("\r");
            r2.WaitState(PromptState.AtPrompt, 3000, "Enter → 프롬프트");
            r2.IdleWait();
            ctx.Check(J.Seq(J.A(r2.Call("run", r2.P, "echo cx_again", "--timeout", "5"), "out"), "cx_again"), "재시작·해제 뒤 run 정상");
        }

        // ================================================================== config-validate-t3
        [Scenario("config-validate-t3", "config-validate 추가분(T3): ask_new_cables(기본 false, true/false 만), sup_watch_s(기본 30, 양수), cables.<id> 의 ModeSet(mode 키가 있을 때만 — alias 만이면 아님), 설치기 기본 config {\"ask_new_cables\": true} 통과. + C.13: auto_nudge 키, U-Boot autoboot 중 CR 0바이트, 사람이 친 줄이 있으면 CR 0바이트, 부팅당 3번 상한, auto_nudge:false 옛 동작", Order = 762, TimeoutS = 150)]
        public static void ConfigValidateT3(ScenarioContext ctx)
        {
            string Errs(string json)
            {
                try { return string.Join(" | ", Config.Parse(json).Validate()); }
                catch (ConfigException e) { return string.Join(" | ", e.Errors); }
            }
            var def = Config.Parse("{}");
            ctx.Check(!def.AskNewCables && def.SupWatchS == 30, "키를 안 쓴 기본값: ask_new_cables false(Phase A 동작), sup_watch_s 30");
            ctx.Check(!Config.Default().AskNewCables, "코드 기본값 ask_new_cables false(시험 설정 헬퍼 입력 그대로)");
            var seed = Config.Parse(@"{""ask_new_cables"": true}");
            ctx.Check(seed.AskNewCables && seed.Validate().Count == 0, "설치기 기본 config {\"ask_new_cables\": true} 통과·반영");
            ctx.Check(!Config.Parse(@"{""ask_new_cables"": false}").AskNewCables, "ask_new_cables false 반영");
            ctx.Check(Errs(@"{""ask_new_cables"": ""yes""}").Contains("ask_new_cables 는 true/false") && Errs(@"{""ask_new_cables"": 1}").Contains("ask_new_cables 는 true/false"),
                      "ask_new_cables 문자열·숫자 거부");
            ctx.Check(Config.Parse(@"{""sup_watch_s"": 5}").SupWatchS == 5 && Errs(@"{""sup_watch_s"": 0}").Contains("sup_watch_s"), "sup_watch_s 5 반영, 0 거부");
            var m = Config.Parse(@"{ ""cables"": { ""AL"": { ""alias"": ""a"" }, ""AU"": { ""mode"": ""auto"" }, ""IG"": { ""alias"": ""r"", ""mode"": ""ignore"" }, ""BD"": { ""baud"": 9600 } } }");
            ctx.Check(m.Validate().Count == 0, "cables 예시 통과");
            ctx.Check(!m.CableFor("AL").ModeSet && m.CableFor("AL").Mode == "auto", "alias 만: ModeSet false(Mode 는 기본 auto)");
            ctx.Check(m.CableFor("AU").ModeSet && m.CableFor("AU").Mode == "auto", "mode auto 명시: ModeSet true");
            ctx.Check(m.CableFor("IG").ModeSet && m.CableFor("IG").Mode == "ignore" && m.CableFor("IG").Alias == "r", "mode ignore 명시: ModeSet true, alias 도 반영");
            ctx.Check(!m.CableFor("BD").ModeSet && m.CableFor("BD").Baud == 9600 && !m.CableFor("none").ModeSet, "baud 만·없는 케이블: ModeSet false");
            ctx.Check(Errs(@"{ ""cables"": { ""X"": { ""mode"": ""Ignore"" } } }").Contains("mode 는 auto|ignore"), "mode 는 소문자 auto|ignore 만");
            NudgeScenarios.PartB(ctx);                 // 부록 C.13 ⑶⑷⑸⑹
        }
    }
}
