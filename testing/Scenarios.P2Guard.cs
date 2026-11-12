// US-010 시나리오(Step 7 검증 권고): P2 경합 창(조각 사이·판정과 쓰기 사이 사람 입력), P1 침묵 실패(처리기 예외·writer 오류),
// 늦은 detach 이벤트. 기대값은 계획 §2 P1·P2, §3.3, §3.4 에서 손으로 정한다. "Claude 0바이트"는 TX 관측(출처 ≠ Human)과
// FakeBoard 수신 바이트(TotalReceived − 사람 송신)로 센다.
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace UartHub.Testing
{
    internal static class P2GuardScenarios
    {
        static bool IsEsc(TxRecord x) => x.Source != TxSource.Human && x.Data.Length == 1 && x.Data[0] == 0x1b;

        static string HubLogText(HubRig r)
        {
            string p = r.Hub.Log.Path;
            return File.Exists(p) ? LogFiles.ReadShared(p) : "";
        }

        /// <summary>사람이 반쯤 친 줄을 ^C 로 버리고 프롬프트·유휴로 되돌린다.</summary>
        static void BackToPrompt(HubRig r, string what)
        {
            r.Human("\x03");
            r.WaitState(PromptState.AtPrompt, 3000, what);
            r.IdleWait();
        }

        // ================================================================== p2-gap
        [Scenario("p2-gap", "조각 송신(ESC 뒤 esc_gap_ms) 중 사람 입력: {ESC} 뒤 조각 사이에 사람 바이트 → Claude·spool 의 나머지 조각 0바이트, status interrupted(101), .log 에 !!! [claude|tx <id>] interrupted (human input)", Order = 570, TimeoutS = 90)]
        public static void P2Gap(ScenarioContext ctx)
        {
            // ESC 간격을 600ms 로 늘려 시험 스레드가 간격 안에 확실히 끼어들게 한다(판정 규칙은 같다).
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIMGAP" }, "SIMCABGP", "COM151", 17151, c => c.EscGapMs = 600);

            void Case(string what, Func<(int Exit, JsonElement? Res, TxRequest Req)> send, string tag, string marker)
            {
                int tx0 = r.Tx.Count;
                long rec0 = r.Board.TotalReceived, h0 = r.HumanBytes;
                long ring0 = r.Ring.End;
                (int Exit, JsonElement? Res, TxRequest Req) res = default;
                var th = new Thread(() => res = send()) { IsBackground = true };
                th.Start();
                ctx.WaitUntil(() => r.Tx.Skip(tx0).Any(IsEsc), 5000, what + ": 첫 조각(ESC) 송신");
                r.Human("q");                                                    // ESC 뒤 간격 안의 사람 키
                ctx.Check(th.Join(15000), what + ": 요청 완료");
                Thread.Sleep(r.Cfg.EscGapMs + 200);                              // 늦은 조각이 있다면 이 사이에 나간다
                var after = r.Tx.Skip(tx0).ToList();
                int iEsc = after.FindIndex(IsEsc);
                int iHum = after.FindIndex(x => x.Source == TxSource.Human);
                long claudeAfter = after.Skip(iEsc + 1).Where(x => x.Source != TxSource.Human).Sum(x => (long)x.Data.Length);
                ctx.Check(iEsc >= 0 && iHum > iEsc, what + ": 순서 ESC(Claude) → 사람 'q'");
                ctx.Check(claudeAfter == 0, what + ": 사람 입력 뒤 Claude 조각 " + claudeAfter + "B (기대 0)");
                long boardGot = r.Board.TotalReceived - rec0, human = r.HumanBytes - h0;
                ctx.Check(boardGot == 1 + human, what + ": 보드 수신 " + boardGot + "B = ESC 1 + 사람 " + human);
                ctx.Check(!r.RingText(ring0).Contains(marker), what + ": 보드에 '" + marker + "' 없음");
                string id = res.Req?.ReqId ?? (res.Res.HasValue ? J.S(res.Res.Value, "req_id") : null);
                ctx.Check(res.Exit == 101 && (res.Res.HasValue ? J.S(res.Res.Value, "status") == "interrupted" : res.Req.Result.ToString().Contains("\"status\":\"interrupted\"")),
                          what + ": exit 101 interrupted (" + (res.Res.HasValue ? J.Show(res.Res.Value) : res.Req?.Result?.ToString()) + ")");
                string line = "!!! [" + tag + " " + id + "] interrupted (human input)";
                ctx.WaitUntil(() => r.LogText().Contains(line), 3000, what + ": .log 에 '" + line + "'");
                BackToPrompt(r, what + " 뒤 ^C → 프롬프트");
            }

            Case("send", () =>
            {
                var e = r.Call("send", r.P, "{ESC}echo gapz", "--timeout", "10");
                return ((int)J.N(e, "exit"), e, null);
            }, "claude", "gapz");

            Case("spool", () =>
            {
                var q = r.Hub.Arbiter.Submit(r.Cab, new TxRequest { Kind = TxKind.Send, Gate = TxGate.Token, Source = TxSource.Spool, Cmd = "{ESC}echo gapy", TimeoutS = 10 }, out _);
                q.Wait(15000);
                return (q.Exit, null, q);
            }, "tx", "gapy");

            // 사람 입력이 없으면 같은 조각 송신은 끝까지 간다(회귀 없음)
            long ringN = r.Ring.End;
            var ok = r.Call("send", r.P, "{ESC}echo gapok", "--timeout", "10");
            ctx.Check(J.S(ok, "status") == "ok", "사람 입력 없는 {ESC} 조각 송신 ok (" + J.Show(ok) + ")");
            ctx.WaitUntil(() => r.RingText(ringN).Contains("\r\ngapok\r\n"), 3000, "'echo gapok' 실행");
        }

        // ================================================================== p2-atomic
        [Scenario("p2-atomic", "판정과 쓰기 사이 사람 입력(훅으로 결정적 재현): probe → 101·0바이트, run → 송신 안 함(.log 에 >>> 없음)·재대기 뒤 101, spool → 사람 CR 뒤 유휴를 다시 채우고 송신(재대기). 조건부 쓰기(TX 잠금 안 사람 순번 비교)", Order = 575, TimeoutS = 90)]
        public static void P2Atomic(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIMAT" }, "SIMCABAT", "COM152", 17152, null, false);
            var arb = r.Hub.Arbiter;
            ctx.Defer(() => arb.TestBeforeWrite = null);
            string inject = null;
            int armed = 0;
            arb.TestBeforeWrite = (c, q, sent) =>
            {
                if (sent == 0 && Interlocked.Exchange(ref armed, 0) == 1) r.Human(inject);     // 게이트 판정 뒤, 첫 바이트 쓰기 직전
            };
            void Arm(string s) { inject = s; Interlocked.Exchange(ref armed, 1); }

            // ---- probe(UNKNOWN): 판정 뒤 사람 'z' → probe 는 기다리지 않으므로 101, CR 0바이트
            ctx.Check(r.T.State == PromptState.Unknown, "전제: 새 허브 UNKNOWN (" + r.T.Snapshot().StateName + ")");
            Thread.Sleep(r.Cfg.QuietMs + 100);
            long rec0 = r.Board.TotalReceived, h0 = r.HumanBytes, cl0 = r.ClaudeBytes;
            Arm("z");
            var p = r.Call("probe", r.P);
            ctx.Check(Volatile.Read(ref armed) == 0, "훅이 probe 판정 뒤 사람 'z' 를 끼워 넣음");
            ctx.Check(J.N(p, "exit") == 101, "probe → 101 (" + J.Show(p) + ")");
            ctx.Check(r.ClaudeBytes == cl0 && r.Board.TotalReceived - rec0 == r.HumanBytes - h0, "probe CR 0바이트(보드 수신 = 사람 " + (r.HumanBytes - h0) + "B)");
            BackToPrompt(r, "^C → 프롬프트");

            // ---- run: 판정 뒤 사람 'z' → 봉인 줄 미전송, 재대기(HUMAN_TYPING) → deadline 101
            rec0 = r.Board.TotalReceived; h0 = r.HumanBytes; cl0 = r.ClaudeBytes;
            long ring0 = r.Ring.End;
            Arm("z");
            var a = r.Call("run", r.P, "echo atomic1", "--timeout", "1.5");
            ctx.Check(Volatile.Read(ref armed) == 0, "훅이 run 판정 뒤 사람 'z' 를 끼워 넣음");
            ctx.Check(J.N(a, "exit") == 101 && J.S(a, "blocked_reason") == "partial_line", "run → 재대기 뒤 101 partial_line (" + J.Show(a) + ")");
            ctx.Check(r.ClaudeBytes == cl0 && r.Board.TotalReceived - rec0 == r.HumanBytes - h0, "run 봉인 줄 0바이트(보드 수신 = 사람 " + (r.HumanBytes - h0) + "B)");
            ctx.Check(!r.RingText(ring0).Contains("atomic1"), "보드에 'atomic1' 없음");
            string id = J.S(a, "req_id");
            string log = r.LogText();
            ctx.Check(!log.Contains(">>> [claude " + id + "]"), ".log 에 보내지 않은 '>>> [claude " + id + "]' 줄 없음");
            ctx.Check(log.Contains("!!! [claude " + id + "] dropped (partial_line)"), ".log 에 '!!! [claude " + id + "] dropped (partial_line)'");
            BackToPrompt(r, "^C → 프롬프트");

            // ---- spool: 판정 뒤 사람 CR → 미전송 → 프롬프트·사람 유휴를 다시 채운 뒤 송신(재대기)
            int tx0 = r.Tx.Count;
            ring0 = r.Ring.End;
            Arm("\r");
            var sq = arb.Submit(r.Cab, new TxRequest { Kind = TxKind.Send, Gate = TxGate.Token, Source = TxSource.Spool, Cmd = "echo atomic3", TimeoutS = 5 }, out _);
            ctx.Check(sq.Wait(8000) && sq.Exit == 0, "spool → 재대기 뒤 ok (" + sq.Result + ")");
            var tx = r.Tx.Skip(tx0).ToList();
            var cr = tx.FirstOrDefault(x => x.Source == TxSource.Human);
            var sp = tx.FirstOrDefault(x => x.Source == TxSource.Spool);
            ctx.Check(cr != null && sp != null && tx.IndexOf(cr) < tx.IndexOf(sp), "순서: 사람 CR → spool 줄");
            long gap = sp.MonoMs - cr.MonoMs;
            ctx.Check(gap >= (long)(r.Cfg.IdleS * 1000) - 2, "사람 CR 뒤 " + gap + "ms 뒤 송신(≥ idle " + (long)(r.Cfg.IdleS * 1000) + "ms, 재판정)");
            ctx.WaitUntil(() => r.RingText(ring0).Contains("\r\natomic3\r\n"), 3000, "'echo atomic3' 실행");
            r.WaitState(PromptState.AtPrompt, 3000, "spool 뒤");
            r.IdleWait();

            // ---- 훅 없이 run 정상(회귀 없음)
            arb.TestBeforeWrite = null;
            ctx.Check(J.Seq(J.A(r.Call("run", r.P, "echo atomic4"), "out"), "atomic4"), "훅 해제 뒤 run 정상");
        }

        // ================================================================== silent-fail
        [Scenario("silent-fail", "침묵 실패 없음(P1): Cable.Transmitted 처리기 예외·PromptTracker 구독자 예외 → hub.log 기록(송신·추적은 계속), DiskWriter 오류 주입 → 새 파일 첫머리에 *** writer error … ***", Order = 580, TimeoutS = 90)]
        public static void SilentFail(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIMSF" }, "SIMCABSF", "COM153", 17153);

            // ---- Transmitted 처리기 예외
            Action<TxRecord> badTx = rec => { if (rec.Source == TxSource.Human) throw new InvalidOperationException("시험 주입 tx 처리기 예외"); };
            r.Cab.Transmitted += badTx;
            long ring0 = r.Ring.End;
            r.Human("echo sf1\r");
            ctx.WaitUntil(() => r.RingText(ring0).Contains("\r\nsf1\r\n"), 3000, "처리기 예외에도 사람 송신은 보드 도착(sf1)");
            r.Cab.Transmitted -= badTx;
            ctx.WaitUntil(() => HubLogText(r).Contains("시험 주입 tx 처리기 예외"), 3000, "hub.log 에 Transmitted 처리기 예외 기록");
            r.WaitState(PromptState.AtPrompt, 3000, "sf1 뒤");

            // ---- PromptTracker 구독자 예외(PromptConfirmed)
            Action<PromptCandidate> badP = c => throw new InvalidOperationException("시험 주입 tracker 구독자 예외");
            r.T.PromptConfirmed += badP;
            r.Human("echo sf2\r");
            r.WaitState(PromptState.AtPrompt, 3000, "구독자 예외에도 추적 계속(sf2 뒤)");
            ctx.WaitUntil(() => HubLogText(r).Contains("시험 주입 tracker 구독자 예외"), 3000, "hub.log 에 PromptTracker 구독자 예외 기록");
            r.T.PromptConfirmed -= badP;
            r.IdleWait();
            ctx.Check(J.Seq(J.A(r.Call("run", r.P, "echo sf3"), "out"), "sf3"), "구독자 예외 뒤 run 정상(Arbiter 구독자는 계속 통지받음)");
            r.WaitState(PromptState.AtPrompt, 3000, "sf3 뒤");

            // ---- DiskWriter 오류 → 남은 청크 손실을 새 파일에 표시
            r.Hub.WaitDrained(3000);
            string firstLog = r.Cab.Writer.CurrentLogPath;
            r.Cab.Writer.TestFailNextData();
            r.Human("echo sf4\r");
            r.WaitState(PromptState.AtPrompt, 3000, "sf4 뒤");
            ctx.WaitUntil(() => HubLogText(r).Contains("시험 주입 writer 오류"), 3000, "hub.log 에 writer 오류 기록");
            r.Human("echo sf5\r");
            r.WaitState(PromptState.AtPrompt, 3000, "sf5 뒤");
            r.Hub.WaitDrained(3000);
            var logs = LogFiles.All(r.Cfg.LogRoot).Where(x => x.EndsWith(".log", StringComparison.Ordinal)).Select(x => Path.Combine(r.Cfg.LogRoot, x)).ToList();
            var withErr = logs.Where(f => LogFiles.ReadShared(f).Contains("*** writer error")).ToList();
            ctx.Check(withErr.Count == 1, ".log 중 '*** writer error' 가 있는 파일 1개 (" + withErr.Count + "/" + logs.Count + ")");
            string t = LogFiles.ReadShared(withErr[0]);
            ctx.Check(!string.Equals(Path.GetFullPath(withErr[0]), Path.GetFullPath(firstLog), StringComparison.OrdinalIgnoreCase), "오류 표시는 새 파일(" + Path.GetFileName(withErr[0]) + ")");
            int iErr = t.IndexOf("*** writer error", StringComparison.Ordinal), iSf5 = t.IndexOf("] sf5\r\n", StringComparison.Ordinal);
            ctx.Check(iErr >= 0 && iSf5 > iErr, "새 파일: writer error 줄 → 그 뒤 sf5 기록");
            string errLine = t.Substring(iErr, t.IndexOf('\n', iErr) - iErr).TrimEnd('\r');
            ctx.Check(errLine.Contains("IOException") && errLine.Contains("시험 주입 writer 오류") && errLine.EndsWith("***", StringComparison.Ordinal), "표시 줄: " + errLine);
        }

        // ================================================================== stale-detach
        [Scenario("stale-detach", "늦은 detach 이벤트: detach 통지가 지연되는 사이 빠르게 재attach → 이전 세션(AttachSeq) detach 가 늦게 와도 새 추적기 펌프·telnet endpoint 유지. 협상된 telnet 클라이언트: 재연결 배너 뒤 분리 배너 없음, 그 연결로 친 명령이 보드에 감, hub.log 에 telnet 늦은 detach 무시", Order = 585, TimeoutS = 90)]
        public static void StaleDetach(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIMSD" }, "SIMCABSD", "COM154", 17154);
            var prov = (FakePortProvider)r.Prov;
            var cm = r.Hub.Cables;
            ctx.WaitUntil(() => r.Hub.Telnet.StateOf(r.Cab) == "listening", 3000, "telnet listen");
            using var tn = TelnetScenarios.Human(r, r.Cab.TcpPort, 1);
            var entered = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            ctx.Defer(() => { cm.TestBeforeDetachFire = null; release.Set(); });
            long seqAtHook = -1;
            cm.TestBeforeDetachFire = c => { seqAtHook = c.AttachSeq; entered.Set(); release.Wait(10000); };

            long seq0 = r.Cab.AttachSeq;
            int gen0 = r.T.Generation;
            prov.Unplug("SIMCABSD", new IOException("시험 탈착"));
            ctx.WaitUntil(() => entered.IsSet, 3000, "detach 처리 뒤 통지 직전에서 멈춤(seq " + seq0 + ")");
            ctx.Check(seqAtHook == seq0, "멈춘 detach 의 세션 AttachSeq=" + seqAtHook);
            cm.TestBeforeDetachFire = null;
            prov.Plug(r.Board, "SIMCABSD", "COM155");
            ctx.WaitUntil(() => r.Cab.IsAttached && r.Cab.AttachSeq == seq0 + 1, 3000, "빠른 재attach(AttachSeq " + seq0 + " → " + (seq0 + 1) + ")");
            ctx.WaitUntil(() => TnClient.Contains(tn.All(), TelnetScenarios.B(TelnetScenarios.AttachBanner("COM155"))), 3000, "telnet 클라이언트에 재연결 배너(COM155)");
            ctx.WaitUntil(() => r.T.Generation > gen0, 3000, "새 추적기 펌프 준비(복원)");
            release.Set();                                                     // 이전 세션의 detach 이벤트가 이제야 통지된다
            ctx.WaitUntil(() => HubLogText(r).Contains("늦은 detach 이벤트 무시 SIMCABSD seq=" + seq0), 3000, "늦은 detach 통지가 처리됨(허브 쪽 기록)");
            Thread.Sleep(300);

            var got = tn.All();
            int iAtt = IndexOf(got, TelnetScenarios.B(TelnetScenarios.AttachBanner("COM155")));
            ctx.Check(iAtt >= 0 && IndexOf(got, TelnetScenarios.B(TelnetScenarios.DetachBanner("COM154")), iAtt) < 0 && IndexOf(got, TelnetScenarios.B(TelnetScenarios.DetachBanner("COM155")), iAtt) < 0,
                      "재연결 배너 뒤에 분리 배너 없음(늦은 detach 가 telnet 을 분리 상태로 되돌리지 않음)");
            ctx.Check(!tn.ClosedByPeer && r.Cab.IsAttached && r.Hub.Telnet.StateOf(r.Cab) == "listening", "늦은 detach 뒤 telnet 연결·endpoint 유지 (" + r.Hub.Telnet.StateOf(r.Cab) + ")");
            long ring0 = r.Ring.End;
            int txH = r.Tx.Count;
            tn.Send("echo sd1\r");                                                 // 협상된 telnet 클라이언트로 입력 — 분리 상태면 버려진다
            ctx.WaitUntil(() => r.RingText(ring0).Contains("\r\nsd1\r\n"), 3000, "telnet 으로 친 'echo sd1' 이 보드에서 실행됨");
            // 재연결 줄 지우기: 앞 세션 detach 가 늦어도(재attach 처리기가 앞 세션 상태 AT_PROMPT 를 보고) 예약된다 → 첫 입력 앞 ^E^U
            ctx.Check(r.Tx.Skip(txH).Where(x => x.Source == TxSource.Human).SelectMany(x => x.Data).SequenceEqual(TelnetScenarios.B("\u0005\u0015echo sd1\r")),
                      "보드 수신 = ^E^U(늦은 detach 여도 재연결 줄 지우기 예약) + telnet 사람 입력 'echo sd1\\r'");
            ctx.Check(!TnClient.Contains(tn.All(), TelnetScenarios.B(TelnetScenarios.DroppedBanner("COM155"))), "입력 버림 안내 없음");
            ctx.Check(HubLogText(r).Contains("telnet: 늦은 detach 이벤트 무시 SIMCABSD seq=" + seq0), "hub.log 에 telnet 늦은 detach 무시(seq " + seq0 + ")");
            r.WaitState(PromptState.AtPrompt, 3000, "늦은 detach 뒤 추적기 펌프가 RX 를 계속 처리");
            r.IdleWait();
            ctx.Check(J.Seq(J.A(r.Call("run", r.P, "echo sd2", "--timeout", "5"), "out"), "sd2"), "늦은 detach 뒤 run 정상");
        }

        static int IndexOf(byte[] hay, byte[] needle, int from = 0)
        {
            for (int i = from; i + needle.Length <= hay.Length; i++)
                if (hay.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
            return -1;
        }

        // ================================================================== stale-attach
        [Scenario("stale-attach", "늦은 attach 이벤트(telnet): ① attach 통지가 늦는 동안 새 세션 바이트가 먼저 링에 와도 재연결 배너가 그 바이트 앞(WaitAttachApplied) ② 같은 세션의 detach 가 attach 통지보다 먼저 처리되면 그 attach 는 무시(서버 단 detachedSeq) ③ 서버 단 검사를 지난 뒤 endpoint 반영 전에 detach 가 오면 무시(endpoint detachSeen) — ②③ 은 배너 없음·입력 계속 버림 ④ 재attach 통지가 아직 반영되지 않은 사이 앞 세션의 늦은 detach → 무시(live != s): 새 세션 바이트 뒤 분리 배너 없음·입력 정상", Order = 586, TimeoutS = 120)]
        public static void StaleAttach(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIMSA" }, "SIMCABSA", "COM170", 17160);
            var prov = (FakePortProvider)r.Prov;
            var cm = r.Hub.Cables;
            var tel = r.Hub.Telnet;
            ctx.Defer(() => { cm.TestBeforeAttachFire = null; cm.TestBeforeDetachFire = null; tel.TestBeforeEndpointAttach = null; tel.AttachNoticeWaitMs = 1000; });
            tel.AttachNoticeWaitMs = 5000;                                     // ①④ 는 attach 통지를 일부러 늦춘다: 바쁜 PC 에서도 fanout 이 기다림을 포기하지 않게
            ctx.WaitUntil(() => tel.StateOf(r.Cab) == "listening", 3000, "telnet listen");
            using var tn = TelnetScenarios.Human(r, r.Cab.TcpPort, 1);
            byte[] Bn(string s) => TelnetScenarios.B(s);
            void Unplug(string com)
            {
                prov.Unplug("SIMCABSA", new IOException("시험 탈착"));
                ctx.WaitUntil(() => !r.Cab.IsAttached, 3000, "분리(" + com + ")");
                ctx.WaitUntil(() => TnClient.Contains(tn.All(), Bn(TelnetScenarios.DetachBanner(com))), 3000, "분리 배너(" + com + ")");
            }
            // 분리 중 입력이 보드에 가지 않는지(안내는 detach 한 번에 1회라 이미 받았을 수 있다)
            void InputDropped(string what)
            {
                long rec = r.Board.TotalReceived;
                int tx = r.Tx.Count;
                tn.Send("echo no_" + what + "\r");
                Thread.Sleep(300);
                ctx.Check(r.Board.TotalReceived == rec && r.Tx.Count == tx, what + ": telnet 입력은 여전히 버림(보드 0바이트)");
            }

            // ---- ① attach 통지 지연 중 새 세션 바이트 → 배너가 앞
            Unplug("COM170");
            var entered = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            ctx.Defer(() => release.Set());
            cm.TestBeforeAttachFire = c => { entered.Set(); release.Wait(10000); };
            prov.Plug(r.Board, "SIMCABSA", "COM171");
            ctx.WaitUntil(() => entered.IsSet && r.Cab.IsAttached, 3000, "① 재attach(리더 시작) 뒤 attach 통지 직전에서 멈춤");
            cm.TestBeforeAttachFire = null;
            long ring1 = r.Ring.End;
            r.Board.EmitAppLog("early_sa1");
            ctx.WaitUntil(() => r.RingText(ring1).Contains("early_sa1"), 2000, "① 새 세션 바이트가 링에 먼저 옴");
            Thread.Sleep(300);
            ctx.Check(!TnClient.Contains(tn.All(), Bn("early_sa1")) && !TnClient.Contains(tn.All(), Bn(TelnetScenarios.AttachBanner("COM171"))),
                      "① attach 통지 전: 새 세션 바이트를 미룸(배너·바이트 모두 아직 안 감)");
            release.Set();
            ctx.WaitUntil(() => TnClient.Contains(tn.All(), Bn("early_sa1")), 3000, "① 통지 반영 뒤 바이트 전달");
            var got = tn.All();
            int iA = IndexOf(got, Bn(TelnetScenarios.AttachBanner("COM171"))), iE = IndexOf(got, Bn("early_sa1"));
            ctx.Check(iA >= 0 && iA < iE, "① 재연결 배너(" + iA + ")가 새 세션 첫 바이트(" + iE + ") 앞");
            ctx.WaitUntil(() => r.T.Generation >= 2, 3000, "① 추적기 복원");

            // ---- ② 같은 세션 detach 가 attach 통지보다 먼저 → 서버 단 detachedSeq 로 무시
            Unplug("COM171");
            var entered2 = new ManualResetEventSlim(false);
            var release2 = new ManualResetEventSlim(false);
            ctx.Defer(() => release2.Set());
            cm.TestBeforeAttachFire = c => { entered2.Set(); release2.Wait(10000); };
            long seq2 = r.Cab.AttachSeq + 1;
            prov.Plug(r.Board, "SIMCABSA", "COM172");
            ctx.WaitUntil(() => entered2.IsSet && r.Cab.AttachSeq == seq2, 3000, "② attach(seq " + seq2 + ") 통지 직전에서 멈춤");
            cm.TestBeforeAttachFire = null;
            prov.Unplug("SIMCABSA", new IOException("시험 탈착 ②"));
            ctx.WaitUntil(() => !r.Cab.IsAttached, 3000, "② 같은 세션 분리(detach 통지가 먼저 처리됨)");
            Thread.Sleep(200);
            release2.Set();
            ctx.WaitUntil(() => Regex.IsMatch(HubLogText(r), "(?m)telnet: 늦은 attach 이벤트 무시 SIMCABSA seq=" + seq2 + "$"), 3000, "② hub.log 에 telnet 늦은 attach 무시 — 서버 단 형식(뒤에 사유 괄호 없음)");
            Thread.Sleep(200);
            ctx.Check(!TnClient.Contains(tn.All(), Bn(TelnetScenarios.AttachBanner("COM172"))) && !tn.ClosedByPeer, "② 재연결 배너 없음, 연결 유지");
            InputDropped("sa2");

            // ---- ③ 서버 단 검사를 지난 뒤 endpoint 반영 전에 같은 세션 detach → endpoint detachSeen 으로 무시
            var entered3 = new ManualResetEventSlim(false);
            var release3 = new ManualResetEventSlim(false);
            ctx.Defer(() => release3.Set());
            tel.TestBeforeEndpointAttach = c => { entered3.Set(); release3.Wait(10000); };
            long seq3 = r.Cab.AttachSeq + 1;
            prov.Plug(r.Board, "SIMCABSA", "COM173");
            ctx.WaitUntil(() => entered3.IsSet && r.Cab.AttachSeq == seq3, 3000, "③ attach(seq " + seq3 + ") 가 서버 단 검사를 지나 endpoint 반영 직전에서 멈춤");
            tel.TestBeforeEndpointAttach = null;
            prov.Unplug("SIMCABSA", new IOException("시험 탈착 ③"));
            ctx.WaitUntil(() => !r.Cab.IsAttached, 3000, "③ 같은 세션 분리");
            Thread.Sleep(200);
            release3.Set();
            Thread.Sleep(500);
            ctx.Check(!TnClient.Contains(tn.All(), Bn(TelnetScenarios.AttachBanner("COM173"))) && !tn.ClosedByPeer, "③ 재연결 배너 없음, 연결 유지");
            ctx.Check(HubLogText(r).Contains("telnet: 늦은 attach 이벤트 무시 SIMCABSA seq=" + seq3 + " ("), "③ hub.log 에 telnet 늦은 attach 무시(endpoint 단)");
            InputDropped("sa3");

            // ---- 정상 재연결
            prov.Plug(r.Board, "SIMCABSA", "COM174");
            ctx.WaitUntil(() => TnClient.Contains(tn.All(), Bn(TelnetScenarios.AttachBanner("COM174"))), 3000, "정상 재연결 배너(COM174)");
            ctx.WaitUntil(() => r.T.Snapshot().RestoredFrom == RestoreSource.Attach && r.T.Generation >= 3, 3000, "추적기 복원");
            long ring4 = r.Ring.End;
            tn.Send("echo sa4\r");
            ctx.WaitUntil(() => r.RingText(ring4).Contains("\r\nsa4\r\n"), 3000, "재연결 뒤 telnet 입력 정상");
            r.WaitState(PromptState.AtPrompt, 3000, "sa4 뒤");

            // ---- ④ 앞 세션(seq s)의 detach 통지가 늦는 사이 재attach(s+1) — 그 attach 통지도 아직 반영 전. 늦은 detach 를 받으면 live(s+1) != s 라 무시해야 한다
            var entered4 = new ManualResetEventSlim(false);
            var release4 = new ManualResetEventSlim(false);
            var entered5 = new ManualResetEventSlim(false);
            var release5 = new ManualResetEventSlim(false);
            ctx.Defer(() => { release4.Set(); release5.Set(); });
            long s4 = r.Cab.AttachSeq;
            int m4 = (int)tn.Count;
            cm.TestBeforeDetachFire = c => { entered4.Set(); release4.Wait(10000); };
            prov.Unplug("SIMCABSA", new IOException("시험 탈착 ④"));
            ctx.WaitUntil(() => entered4.IsSet && !r.Cab.IsAttached, 3000, "④ detach(seq " + s4 + ") 통지 직전에서 멈춤");
            cm.TestBeforeDetachFire = null;
            cm.TestBeforeAttachFire = c => { entered5.Set(); release5.Wait(10000); };
            prov.Plug(r.Board, "SIMCABSA", "COM175");
            ctx.WaitUntil(() => entered5.IsSet && r.Cab.IsAttached && r.Cab.AttachSeq == s4 + 1, 3000, "④ 재attach(seq " + (s4 + 1) + ") 통지 직전에서 멈춤");
            cm.TestBeforeAttachFire = null;
            long ring5 = r.Ring.End;
            r.Board.EmitAppLog("new_sa5");
            ctx.WaitUntil(() => r.RingText(ring5).Contains("new_sa5"), 2000, "④ 새 세션 바이트가 링에 옴");
            release4.Set();                                                    // 늦은 detach(s) — 재attach(s+1) 통지는 아직
            ctx.WaitUntil(() => HubLogText(r).Contains("telnet: 늦은 detach 이벤트 무시 SIMCABSA seq=" + s4 + " (현재 세션 " + (s4 + 1) + ")"), 3000, "④ telnet 늦은 detach 무시(현재 세션 " + (s4 + 1) + ")");
            release5.Set();
            ctx.WaitUntil(() => TnClient.Contains(tn.Since(m4), Bn("new_sa5")), 6000, "④ 재attach 반영 뒤 새 세션 바이트 전달");
            Thread.Sleep(300);
            var g4 = tn.Since(m4);
            int iA4 = IndexOf(g4, Bn(TelnetScenarios.AttachBanner("COM175"))), iN4 = IndexOf(g4, Bn("new_sa5"));
            ctx.Check(iA4 >= 0 && iA4 < iN4, "④ 재연결 배너가 새 세션 바이트 앞");
            ctx.Check(IndexOf(g4, Bn(TelnetScenarios.DetachBanner("COM174"))) < 0 && IndexOf(g4, Bn(TelnetScenarios.DetachBanner("COM175"))) < 0, "④ 새 세션 바이트 뒤(그리고 이 구간 어디에도) 분리 배너 없음");
            long ring6 = r.Ring.End;
            tn.Send("echo sa6\r");
            ctx.WaitUntil(() => r.RingText(ring6).Contains("\r\nsa6\r\n"), 3000, "④ telnet 입력 버려지지 않음(보드에서 실행)");
            ctx.Check(!r.RingText().Contains("no_sa"), "분리 중 친 입력은 끝까지 보드에 가지 않음");
        }

        // ================================================================== pump-handoff
        [Scenario("pump-handoff", "추적기 펌프 인계: ① 추적기는 더 오래된 세션 순번의 늦은 복원(ResetFresh/Restore)을 거부(processed 되감김 없음) ② 앞 펌프를 기다리는 새 펌프는 Stop 에 곧 끝남(detach 처리기가 2초 막히지 않음), 앞 펌프가 끝나기 전에는 복원하지 않음", Order = 588, TimeoutS = 60)]
        public static void PumpHandoff(ScenarioContext ctx)
        {
            // ① 세션 순번 검사(단독 추적기)
            var t = new PromptTracker(ctx.MakeConfig(), "SIMPH");
            ctx.Check(t.ResetFresh(1000, 1_000_000, 3), "세션 3 새 시작");
            t.OnRx(1000, Encoding.ASCII.GetBytes("\r\nroot@SIMPH:/$ "), 1_000_010);
            long p0 = t.ProcessedPos;
            var old = new[] { new ReplayItem { Abs = 0, Data = Encoding.ASCII.GetBytes("old\r\n"), Ms = 10 } };
            ctx.Check(!t.Restore(old, RestoreSource.Attach, 0, 500, 1_000_020, 2) && !t.ResetFresh(500, 1_000_020, 2) && t.ProcessedPos == p0,
                      "세션 2(앞 세션 펌프)의 늦은 Restore·ResetFresh 거부 — processed 그대로 " + p0);
            ctx.Check(t.Restore(old, RestoreSource.Attach, 0, p0, 1_000_030, 4) && t.ProcessedPos == p0, "더 새 세션 4 는 받아들임");

            // ② 앞 펌프(prepare 에서 멈춤)를 기다리는 새 펌프의 Stop
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIMPH" }, "SIMCABPH", "COM197", 17171, toPrompt: false);
            var aIn = new ManualResetEventSlim(false);
            var aGo = new ManualResetEventSlim(false);
            ctx.Defer(() => aGo.Set());
            var ta = new PromptTracker(r.Cfg, "SIMPH");
            var a = new TrackerPump(ta, r.Cab, r.Hub.Clock, r.Hub.Log, () => { aIn.Set(); aGo.Wait(10000); return r.Cab.Ring.End; });
            a.Start();
            ctx.WaitUntil(() => aIn.IsSet, 2000, "앞 펌프가 prepare 에서 멈춤");
            int bPrepared = 0;
            var b = new TrackerPump(ta, r.Cab, r.Hub.Clock, r.Hub.Log, () => { Interlocked.Exchange(ref bPrepared, 1); return r.Cab.Ring.End; }) { After = a };
            b.Start();
            Thread.Sleep(200);
            ctx.Check(Volatile.Read(ref bPrepared) == 0, "새 펌프는 앞 펌프가 끝나기 전에는 복원(prepare)하지 않음");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            b.Stop();
            ctx.Check(sw.ElapsedMilliseconds < 500 && b.Join(0), "앞 펌프를 기다리던 새 펌프의 Stop 이 곧 끝남(" + sw.ElapsedMilliseconds + "ms)");
            ctx.Check(Volatile.Read(ref bPrepared) == 0, "멈춘 새 펌프는 복원하지 않음");
            a.RequestStop();
            aGo.Set();
            ctx.Check(a.Join(3000), "앞 펌프 종료");
        }

        // ================================================================== stale-first-attach
        [Scenario("stale-first-attach", "케이블의 첫 attach 통지가 늦는 사이 같은 세션 detach 가 먼저 처리됨 → 서버 단 detachedSeq 로 endpoint 를 만들지 않음(listen 안 함, closed). 다음 attach 에서 listen·attached, telnet 입력 정상", Order = 587, TimeoutS = 60)]
        public static void StaleFirstAttach(ScenarioContext ctx)
        {
            var cfg = HubRig.MakeConfig(ctx, 17169, c => c.BasePort = 17560);
            var b = new FakeBoard(new FakeBoardOptions { Hostname = "SIMFA" });
            ctx.Defer(b.PowerOff);
            var prov = new FakePortProvider();
            var hub = new Hub(cfg, null, new IPortProvider[] { prov });
            hub.Start();
            ctx.Defer(hub.Stop);
            var entered = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            ctx.Defer(() => { hub.Cables.TestBeforeAttachFire = null; release.Set(); });
            hub.Cables.TestBeforeAttachFire = c => { entered.Set(); release.Wait(10000); };
            prov.Plug(b, "SIMCABFA", "COM195");
            Cable cab = null;
            ctx.WaitUntil(() => entered.IsSet && (cab = hub.Cables.Find("SIMCABFA")) != null && cab.IsAttached, 5000, "첫 attach(seq 1) 통지 직전에서 멈춤");
            hub.Cables.TestBeforeAttachFire = null;
            prov.Unplug("SIMCABFA", new IOException("시험 탈착"));
            ctx.WaitUntil(() => !cab.IsAttached, 3000, "같은 세션 분리(detach 통지가 먼저 처리됨)");
            Thread.Sleep(200);
            release.Set();
            ctx.WaitUntil(() => Regex.IsMatch(LogFiles.ReadShared(hub.Log.Path), "(?m)telnet: 늦은 attach 이벤트 무시 SIMCABFA seq=1$"), 3000, "hub.log 에 telnet 늦은 attach 무시 — 서버 단 형식");
            Thread.Sleep(200);
            ctx.Check(hub.Telnet.StateOf(cab) == "closed", "endpoint 를 만들지 않음(telnet " + hub.Telnet.StateOf(cab) + ")");
            bool refused;
            try { using var tc = new TcpClient(); tc.Connect(IPAddress.Loopback, cab.TcpPort); refused = false; }
            catch (SocketException) { refused = true; }
            ctx.Check(refused, "127.0.0.1:" + cab.TcpPort + " listen 안 함(접속 거부)");

            prov.Plug(b, "SIMCABFA", "COM196");
            ctx.WaitUntil(() => cab.IsAttached && cab.AttachSeq == 2 && hub.Telnet.StateOf(cab) == "listening", 5000, "다음 attach(seq 2) → listening");
            PromptTracker t = null;
            ctx.WaitUntil(() => (t = hub.Tracker(cab)) != null && t.Generation > 0, 3000, "추적기 준비");
            using var tn = TnClient.Connect(cab.TcpPort);
            ctx.Check(tn.WaitCount(TelnetScenarios.Greeting.Length, 2000), "접속 → 협상 바이트");
            tn.Send(TelnetScenarios.TeraTermReply);
            ctx.WaitUntil(() => t.Snapshot().HumanClients == 1, 3000, "협상 → human_clients 1");
            long ring0 = cab.Ring.End;
            tn.Send("echo fa1\r");
            ctx.WaitUntil(() =>
            {
                var buf = new byte[cab.Ring.End - ring0];
                int n = cab.Ring.Read(ring0, buf, 0, buf.Length);
                return n > 0 && Encoding.Latin1.GetString(buf, 0, n).Contains("\r\nfa1\r\n");
            }, 3000, "telnet 입력이 보드에서 실행됨");
        }
    }
}
