// 재연결 줄 지우기(계획 §3.3, 실보드 재시험 11-03): FTDI 케이블을 뽑으면 보드 콘솔 RX 가 흔들려 프레이밍 오류·break 바이트(약 6개)가
// ash 줄 버퍼에 '??????' 로 남는다(보드 콘솔 -inpck -ignpar -ignbrk). 다음 Enter 에서 ash 가 glob 으로 실행한다(/ 에서는 chroot).
// 허브는 detach 때 셸 프롬프트였던 케이블의 다음 attach 세션에서 보드에 처음 닿는 입력 앞에 ^E^U 를 붙인다.
// 기대값은 계획 §3.3 규칙에서 손으로 정한다. 잡음은 FakeBoard.InjectNoise(허브를 거치지 않는 보드 tty 입력, 분리 중이면 에코는 버려짐) + 커서 왼쪽.
// 포트: 제어 17163~17168·17170·17172~17180, 케이블 17540(replug-clear 의 telnet).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace UartHub.Testing
{
    internal static class ReplugClearScenarios
    {
        /// <summary>실보드 잡음 흉내: 프레이밍 오류 바이트 6개가 '??????' 로 + 커서 왼쪽 2칸(ESC[D) — ^U 만이면 커서 뒤 '??' 가 남는다.</summary>
        static readonly byte[] Noise = Encoding.Latin1.GetBytes("??????\x1b[D\x1b[D");

        static List<string> TxHex(HubRig r, int from, TxSource src) => r.Tx.Skip(from).Where(x => x.Source == src).Select(x => TnClient.Hex(x.Data)).ToList();
        static string Hx(string latin1) => TnClient.Hex(Encoding.Latin1.GetBytes(latin1));
        static bool ClearLogged(HubRig r) => r.LogText().Contains(Cable.ReplugClearEvent);

        /// <summary>분리 → (잡음) → 다시 꽂기 → 추적기 복원까지. 다시 꽂은 뒤 세션이 예약됐는지 돌려준다.</summary>
        static bool Replug(ScenarioContext ctx, HubRig r, string cab, string com, byte[] noise)
        {
            var prov = (FakePortProvider)r.Prov;
            long seq0 = r.Cab.AttachSeq;
            int gen0 = r.T.Generation;
            prov.Unplug(cab, new IOException("시험 탈착"));
            ctx.WaitUntil(() => !r.Cab.IsAttached, 3000, "케이블 분리");
            if (noise != null) r.Board.InjectNoise(noise);
            Thread.Sleep(150);
            prov.Plug(r.Board, cab, com);
            ctx.WaitUntil(() => r.Cab.IsAttached && r.Cab.AttachSeq == seq0 + 1 && r.T.Generation > gen0, 5000, "다시 꽂기(" + com + ") + 추적기 복원");
            return r.Cab.ReplugClearPending(r.Cab.AttachSeq);
        }

        static void NotExecuted(ScenarioContext ctx, HubRig r, long from, string what)
        {
            string t = r.RingText(from);
            ctx.Check(!t.Contains("not found") && !t.Contains("??????"), what + ": 잡음 줄 실행 안 됨 (" + BoardConsole.Visible(t) + ")");
        }

        // ================================================================== replug-clear
        [Scenario("replug-clear", "재연결 줄 지우기: 프롬프트에서 분리 중 잡음('??????'+커서 왼쪽) → 다시 꽂기 → telnet 사람 Enter → 보드 수신 05 15 0D(같은 사람 쓰기), 잡음 실행 안 됨·프롬프트, .log 에 >>> [hub-clear], 두 번째 입력은 접두 없음. 예약 없이 다시 분리하면 예약이 다음 세션으로 넘어감", Order = 650, TimeoutS = 90)]
        public static void ReplugClear(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIMRC1" }, "SIMCABRC1", "COM180", 17163, c => c.BasePort = 17540);
            ctx.Check(r.T.State == PromptState.AtPrompt, "전제: AT_PROMPT");
            ctx.WaitUntil(() => r.Hub.Telnet.StateOf(r.Cab) == "listening", 3000, "telnet listen");
            using var tn = TelnetScenarios.Human(r, r.Cab.TcpPort, 1);
            ctx.Check(Replug(ctx, r, "SIMCABRC1", "COM181", Noise), "분리 때 AT_PROMPT → 다시 꽂은 세션에 ^E^U 예약");
            ctx.WaitUntil(() => TnClient.Contains(tn.All(), TelnetScenarios.B(TelnetScenarios.AttachBanner("COM181"))), 3000, "telnet 재연결 배너");
            long at = r.Cab.AttachRxPos;
            int tx0 = r.Tx.Count;
            long rec0 = r.Board.TotalReceived;
            tn.Send("\r");
            r.WaitState(PromptState.AtPrompt, 3000, "사람 Enter → 프롬프트");
            var h = TxHex(r, tx0, TxSource.Human);
            ctx.Check(h.SequenceEqual(new[] { "05 15 0D" }) && r.Board.TotalReceived - rec0 == 3, "보드 수신 = 05 15 0D (사람 쓰기 한 번에 ^E^U 접두) (" + string.Join(" | ", h) + ")");
            NotExecuted(ctx, r, at, "Enter");
            ctx.Check(r.RingText(at).Contains("\r\nroot@SIMRC1:"), "Enter 뒤 새 프롬프트");
            ctx.Check(!r.Cab.ReplugClearPending(r.Cab.AttachSeq), "예약 풀림");
            ctx.Check(r.LogText().Contains(Cable.ReplugClearEvent + " (human)"), ".log 에 '" + Cable.ReplugClearEvent + " (human)'");
            tx0 = r.Tx.Count;
            long ring1 = r.Ring.End;
            r.IdleWait();
            tn.Send("echo rc1\r");
            ctx.WaitUntil(() => r.RingText(ring1).Contains("\r\nrc1\r\n"), 3000, "두 번째 입력 실행");
            ctx.Check(TxHex(r, tx0, TxSource.Human).SequenceEqual(new[] { Hx("echo rc1\r") }), "두 번째 입력은 접두 없음");
            r.WaitState(PromptState.AtPrompt, 3000, "rc1 뒤");

            // ---- 다시 꽂은 뒤 아무 입력 없이 또 분리(상태는 UNKNOWN): 잡음이 그대로라 예약을 다음 세션으로 넘긴다
            ctx.Check(Replug(ctx, r, "SIMCABRC1", "COM182", Noise), "프롬프트에서 분리 → 예약");
            ctx.Check(r.T.State == PromptState.Unknown, "입력 없이 UNKNOWN");
            ctx.Check(Replug(ctx, r, "SIMCABRC1", "COM183", Noise), "쓰이지 않은 예약은 다음 세션으로(UNKNOWN 에서 분리해도)");
            at = r.Cab.AttachRxPos;
            tx0 = r.Tx.Count;
            tn.Send("\r");
            r.WaitState(PromptState.AtPrompt, 3000, "사람 Enter → 프롬프트");
            ctx.Check(TxHex(r, tx0, TxSource.Human).SequenceEqual(new[] { "05 15 0D" }), "넘겨받은 예약으로 05 15 0D");
            NotExecuted(ctx, r, at, "잡음 두 번 쌓인 줄");
        }

        // ================================================================== replug-clear-fullscreen
        [Scenario("replug-clear-fullscreen", "재연결 줄 지우기: 분리 전 FULLSCREEN(vi) → 예약 없음, 다시 꽂은 뒤 사람 입력(ESC :q!) 에 접두 없음", Order = 651, TimeoutS = 60)]
        public static void ReplugClearFullscreen(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIMRC2" }, "SIMCABRC2", "COM184", 17164);
            r.Human("vi /tmp/rc2.txt\r");
            r.WaitState(PromptState.Fullscreen, 3000, "vi");
            Thread.Sleep(200);
            ctx.Check(!Replug(ctx, r, "SIMCABRC2", "COM185", null), "분리 때 FULLSCREEN → 예약 없음");
            int tx0 = r.Tx.Count;
            r.Human("\x1b"); Thread.Sleep(200); r.Human(":q!\r");
            r.WaitState(PromptState.AtPrompt, 3000, "vi 종료");
            ctx.Check(TxHex(r, tx0, TxSource.Human).SequenceEqual(new[] { "1B", Hx(":q!\r") }) && !ClearLogged(r), "사람 입력 그대로(1B | :q!\\r), .log 에 hub-clear 없음");
        }

        // ================================================================== replug-clear-unknown
        [Scenario("replug-clear-unknown", "재연결 줄 지우기: 분리 전 UNKNOWN(프롬프트 확정 없음 — 셸이 아닌 장치와 같은 경우) → 예약 없음, 다시 꽂은 뒤 입력에 접두 없음", Order = 652, TimeoutS = 60)]
        public static void ReplugClearUnknown(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIMRC3" }, "SIMCABRC3", "COM186", 17165, toPrompt: false);
            ctx.Check(r.T.State == PromptState.Unknown, "전제: UNKNOWN");
            ctx.Check(!Replug(ctx, r, "SIMCABRC3", "COM187", null), "분리 때 UNKNOWN → 예약 없음");
            int tx0 = r.Tx.Count;
            r.Human("\r");
            r.WaitState(PromptState.AtPrompt, 3000, "Enter → 프롬프트");
            ctx.Check(TxHex(r, tx0, TxSource.Human).SequenceEqual(new[] { "0D" }) && !ClearLogged(r), "사람 Enter 그대로(0D), .log 에 hub-clear 없음");
        }

        // ================================================================== replug-clear-probe
        [Scenario("replug-clear-probe", "재연결 줄 지우기: 다시 꽂은 뒤 첫 송신이 probe → probe 가 ^E^U CR 을 보내며 예약을 씀(접두 겹침 없음), 그 뒤 사람 입력은 접두 없음, 잡음 실행 안 됨. ^E 만 보내고 멈춘 probe(deadline·사람 입력)는 예약을 쓰지 않음 → 다음 사람 입력에 ^E^U", Order = 653, TimeoutS = 90)]
        public static void ReplugClearProbe(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIMRC4" }, "SIMCABRC4", "COM188", 17166);
            ctx.Check(Replug(ctx, r, "SIMCABRC4", "COM189", Noise), "예약");
            long at = r.Cab.AttachRxPos;
            Thread.Sleep(r.Cfg.QuietMs + 100);
            int tx0 = r.Tx.Count;
            var p = r.Call("probe", r.P, "--user-ok");
            ctx.Check(J.S(p, "status") == "ok" && J.S(p, "state") == "AT_PROMPT", "probe --user-ok → AT_PROMPT (" + J.Show(p) + ")");
            ctx.Check(TxHex(r, tx0, TxSource.Probe).SequenceEqual(new[] { "05", "15", "0D" }) && r.Tx.Count - tx0 == 3, "probe 송신 05 | 15 | 0D 그대로(접두 겹침 없음)");
            ctx.Check(!r.Cab.ReplugClearPending(r.Cab.AttachSeq) && !ClearLogged(r), "probe 가 예약을 씀(hub-clear 줄 없음)");
            NotExecuted(ctx, r, at, "probe");
            r.IdleWait();
            tx0 = r.Tx.Count;
            r.Human("echo rc4\r");
            r.WaitState(PromptState.AtPrompt, 3000, "echo rc4");
            ctx.Check(TxHex(r, tx0, TxSource.Human).SequenceEqual(new[] { Hx("echo rc4\r") }), "그 뒤 사람 입력은 접두 없음");

            // ---- ^E 만 보내고 멈춘 probe: deadline(^E 쓰기 직전 훅이 deadline 을 넘김) → 예약 남음 → 사람 Enter 에 ^E^U
            var arb = r.Hub.Arbiter;
            ctx.Defer(() => arb.TestBeforeWrite = null);
            ctx.Check(Replug(ctx, r, "SIMCABRC4", "COM198", Noise), "예약(②)");
            at = r.Cab.AttachRxPos;
            Thread.Sleep(r.Cfg.QuietMs + 100);
            int armedD = 1;
            arb.TestBeforeWrite = (c, q, n) => { if (q.Kind == TxKind.Probe && n == 0 && Interlocked.Exchange(ref armedD, 0) == 1) Thread.Sleep(900); };
            tx0 = r.Tx.Count;
            var pd = r.Call("probe", r.P, "--user-ok", "--timeout", "0.6");
            arb.TestBeforeWrite = null;
            ctx.Check(J.N(pd, "exit") == 100 && J.N(pd, "bytes") == 1 && TxHex(r, tx0, TxSource.Probe).SequenceEqual(new[] { "05" }), "② ^E 뒤 deadline → 100, probe 송신 05 뿐 (" + J.Show(pd) + ")");
            ctx.Check(r.Cab.ReplugClearPending(r.Cab.AttachSeq), "② ^U 를 못 보낸 probe 는 예약을 쓰지 않음");
            tx0 = r.Tx.Count;
            r.Human("\r");
            r.WaitState(PromptState.AtPrompt, 3000, "② 사람 Enter → 프롬프트");
            ctx.Check(TxHex(r, tx0, TxSource.Human).SequenceEqual(new[] { "05 15 0D" }), "② 사람 Enter 앞에 ^E^U");
            NotExecuted(ctx, r, at, "②");

            // ---- ^E 만 보내고 사람 입력으로 멈춘 probe → 그 사람 입력 앞에 ^E^U
            r.IdleWait();
            ctx.Check(Replug(ctx, r, "SIMCABRC4", "COM199", Noise), "예약(③)");
            at = r.Cab.AttachRxPos;
            Thread.Sleep(r.Cfg.QuietMs + 100);
            int armedH = 1;
            arb.TestBeforeWrite = (c, q, n) => { if (q.Kind == TxKind.Probe && n == 1 && Interlocked.Exchange(ref armedH, 0) == 1) r.Human("\r"); };
            tx0 = r.Tx.Count;
            var ph = r.Call("probe", r.P, "--user-ok");
            arb.TestBeforeWrite = null;
            ctx.Check(J.N(ph, "exit") == 101 && J.S(ph, "blocked_reason") == "human_active" && J.N(ph, "bytes") == 1, "③ ^E 뒤 사람 입력 → 101 human_active, bytes 1 (" + J.Show(ph) + ")");
            ctx.Check(TxHex(r, tx0, TxSource.Human).SequenceEqual(new[] { "05 15 0D" }), "③ 끼어든 사람 Enter 앞에 ^E^U(예약이 남아 있었음)");
            r.WaitState(PromptState.AtPrompt, 3000, "③ 프롬프트");
            NotExecuted(ctx, r, at, "③");
        }

        // ================================================================== replug-clear-boot
        [Scenario("replug-clear-boot", "재연결 줄 지우기: 다시 꽂은 뒤 보드가 재부팅(BOOTING·커널) → 예약 해제(옛 줄 버퍼 없음), 부팅 뒤 사람 Enter 에 접두 없음", Order = 654, TimeoutS = 60)]
        public static void ReplugClearBoot(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIMRC5" }, "SIMCABRC5", "COM190", 17167);
            ctx.Check(Replug(ctx, r, "SIMCABRC5", "COM191", null), "예약");
            r.Board.Reset();
            ctx.WaitUntil(() => r.Board.Phase == "postboot" && r.T.Snapshot().KernelPhase, 8000, "다시 꽂은 뒤 재부팅 → 커널 뒤 BOOTING");
            ctx.Check(r.T.ReplugClearBlockedFor(r.Cab.AttachSeq), "추적기: 이 세션 복원 뒤 부팅을 봐서 줄 지우기 막음");
            int tx0 = r.Tx.Count;
            r.Human("\r");
            r.WaitState(PromptState.AtPrompt, 3000, "부팅 뒤 Enter → 프롬프트");
            ctx.Check(TxHex(r, tx0, TxSource.Human).SequenceEqual(new[] { "0D" }) && !ClearLogged(r), "사람 Enter 그대로(0D), .log 에 hub-clear 없음");
            ctx.Check(!r.Cab.ReplugClearPending(r.Cab.AttachSeq) && LogFiles.ReadShared(r.Hub.Log.Path).Contains("replug clear 해제"), "예약 해제(hub.log)");

            // ---- 예약된 세션에서 재부팅만 보고(입력 없음) 다시 분리 → 막힌 예약은 다음 세션으로 넘기지 않음(줄 버퍼는 이미 깨끗)
            r.IdleWait();
            ctx.Check(Replug(ctx, r, "SIMCABRC5", "COM292", null), "프롬프트에서 분리 → 예약");
            r.Board.Reset();
            ctx.WaitUntil(() => r.Board.Phase == "postboot" && r.T.Snapshot().KernelPhase, 8000, "재부팅 → 커널 뒤 BOOTING");
            ctx.Check(r.Cab.ReplugClearPending(r.Cab.AttachSeq) && r.T.ReplugClearBlockedFor(r.Cab.AttachSeq), "예약은 남았지만 막힘(입력 없음)");
            ctx.Check(!Replug(ctx, r, "SIMCABRC5", "COM293", null), "BOOTING 에서 분리 + 막힌 예약 → 다음 세션 예약 없음");
            tx0 = r.Tx.Count;
            r.Human("\r");
            r.WaitState(PromptState.AtPrompt, 3000, "Enter → 프롬프트");
            ctx.Check(TxHex(r, tx0, TxSource.Human).SequenceEqual(new[] { "0D" }), "사람 Enter 그대로(0D)");
        }

        // ================================================================== replug-clear-spool
        [Scenario("replug-clear-spool", "재연결 줄 지우기: 다시 꽂은 뒤 첫 입력이 .tx spool 줄 → 같은 spool 쓰기 앞에 ^E^U, 잡음 실행 안 됨, .log 에 >>> [hub-clear] … (spool)", Order = 655, TimeoutS = 60)]
        public static void ReplugClearSpool(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIMRC6" }, "SIMCABRC6", "COM192", 17168, c => c.SpoolPollMs = 50);
            ctx.Check(Replug(ctx, r, "SIMCABRC6", "COM193", Noise), "예약");
            long at = r.Cab.AttachRxPos;
            r.Board.Emit("\r\nroot@SIMRC6:/$ ");                                        // spool 일반 줄은 AT_PROMPT 가 필요: 보드가 스스로 프롬프트를 찍은 경우
            r.WaitState(PromptState.AtPrompt, 3000, "보드 프롬프트 → AT_PROMPT");
            r.IdleWait();
            int tx0 = r.Tx.Count;
            File.AppendAllText(Path.Combine(r.Hub.Spool.LiveDir, r.P + ".tx"), "echo spool_rc6\n");
            ctx.WaitUntil(() => r.RingText(at).Contains("\r\nspool_rc6\r\n"), 5000, ".tx 줄 실행");
            var sp = TxHex(r, tx0, TxSource.Spool);
            ctx.Check(sp.Count == 2 && sp[0] == Hx("\u0005\u0015echo spool_rc6") && sp[1] == "0D", "spool 쓰기 = 05 15 + 'echo spool_rc6' (첫 쓰기에 접두) | 0D(줄 끝, 접두 없음) (" + string.Join(" | ", sp) + ")");
            NotExecuted(ctx, r, at, ".tx");
            ctx.Check(r.LogText().Contains(Cable.ReplugClearEvent + " (spool)") && !r.Cab.ReplugClearPending(r.Cab.AttachSeq), ".log 에 hub-clear (spool), 예약 풀림");
        }

        // ================================================================== replug-clear-early
        [Scenario("replug-clear-early", "재연결 줄 지우기: 앞 세션이 재부팅(BOOTING)·화면 지우기 CSI(ESC[H ESC[J)를 본 뒤 프롬프트에서 분리 → 다시 꽂고 추적기 복원(prepare)이 끝나기 전에 사람 Enter → 앞 세션 사실로 막지 않고 05 15 0D, 잡음 실행 안 됨", Order = 657, TimeoutS = 90)]
        public static void ReplugClearEarly(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIMRC9" }, "SIMCABRC9", "COM294", 17172);
            r.Board.Reset();
            ctx.WaitUntil(() => r.Board.Phase == "postboot" && r.T.Snapshot().KernelPhase, 8000, "앞 세션: 재부팅 → BOOTING");
            r.Human("\r");
            r.WaitState(PromptState.AtPrompt, 3000, "부팅 뒤 Enter → 프롬프트");
            r.IdleWait();
            r.Board.Emit("\x1b[H\x1b[Jroot@SIMRC9:/$ ");                                // 화면 지우기 뒤 프롬프트(clear·^L 출력과 같은 꼴): 마지막 TX 뒤 커서 CSI
            ctx.WaitUntil(() => r.RingText().EndsWith("\x1b[H\x1b[Jroot@SIMRC9:/$ ", StringComparison.Ordinal), 2000, "화면 지우기 CSI + 프롬프트 수신");
            r.WaitState(PromptState.AtPrompt, 3000, "CSI 뒤 프롬프트 확정");
            Thread.Sleep(100);
            var pin = new ManualResetEventSlim(false);
            var pgo = new ManualResetEventSlim(false);
            ctx.Defer(() => { r.Hub.TestBeforePrepare = null; pgo.Set(); });
            r.Hub.TestBeforePrepare = c => { pin.Set(); pgo.Wait(10000); };
            var prov = (FakePortProvider)r.Prov;
            prov.Unplug("SIMCABRC9", new IOException("시험 탈착"));
            ctx.WaitUntil(() => !r.Cab.IsAttached, 3000, "케이블 분리");
            r.Board.InjectNoise(Noise);
            Thread.Sleep(150);
            prov.Plug(r.Board, "SIMCABRC9", "COM295");
            ctx.WaitUntil(() => pin.IsSet && r.Cab.IsAttached, 5000, "다시 꽂기 — 추적기 복원 직전에서 멈춤");
            r.Hub.TestBeforePrepare = null;
            ctx.Check(r.Cab.ReplugClearPending(r.Cab.AttachSeq), "예약됨");
            long at = r.Cab.AttachRxPos;
            int tx0 = r.Tx.Count;
            r.Human("\r");                                                              // 복원 전 Enter
            ctx.Check(TxHex(r, tx0, TxSource.Human).SequenceEqual(new[] { "05 15 0D" }), "복원 전 Enter 에도 05 15 0D (" + string.Join(" | ", TxHex(r, tx0, TxSource.Human)) + ")");
            pgo.Set();
            ctx.WaitUntil(() => r.RingText(at).Contains("\r\nroot@SIMRC9:"), 3000, "새 프롬프트");
            r.WaitState(PromptState.AtPrompt, 5000, "복원 뒤 프롬프트 확정");
            NotExecuted(ctx, r, at, "복원 전 Enter");
        }

        // ================================================================== replug-clear-races
        [Scenario("replug-clear-races", "재연결 줄 지우기 예약이 통지 순서 경합에서 사라지지 않음: ① attach(s+1) 통지보다 그 세션 detach 가 먼저 처리 → 앞 세션 예약 유지 → attach(s+2) 에 예약 ② attach 처리기가 펌프 등록·예약을 마친 직후 detach → 예약이 다음 세션으로 넘어감 (늦은 detach 는 stale-detach)", Order = 658, TimeoutS = 90)]
        public static void ReplugClearRaces(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIMRCR" }, "SIMCABRCR", "COM296", 17173);
            var prov = (FakePortProvider)r.Prov;
            var cm = r.Hub.Cables;
            ctx.Defer(() => { cm.TestBeforeAttachFire = null; r.Hub.TestAfterAttachRegistered = null; });
            void Unplug(string what) { prov.Unplug("SIMCABRCR", new IOException("시험 탈착 " + what)); ctx.WaitUntil(() => !r.Cab.IsAttached, 3000, what + " 분리"); }
            void EnterPrefixed(string what)
            {
                Thread.Sleep(150);
                long at = r.Cab.AttachRxPos;
                int tx0 = r.Tx.Count;
                r.Human("\r");
                r.WaitState(PromptState.AtPrompt, 3000, what + ": Enter → 프롬프트");
                ctx.Check(TxHex(r, tx0, TxSource.Human).SequenceEqual(new[] { "05 15 0D" }), what + ": 사람 Enter 앞에 ^E^U (" + string.Join(" | ", TxHex(r, tx0, TxSource.Human)) + ")");
                NotExecuted(ctx, r, at, what);
            }

            // ---- ① 프롬프트에서 분리(예약) → attach(s+1) 통지 지연 → 그 세션 분리가 먼저 → 통지 풀림(무시됨) → attach(s+2)
            Unplug("①-s");
            r.Board.InjectNoise(Noise);
            var e1 = new ManualResetEventSlim(false);
            var g1 = new ManualResetEventSlim(false);
            ctx.Defer(() => g1.Set());
            cm.TestBeforeAttachFire = c => { e1.Set(); g1.Wait(10000); };
            long s1 = r.Cab.AttachSeq + 1;
            prov.Plug(r.Board, "SIMCABRCR", "COM297");
            ctx.WaitUntil(() => e1.IsSet && r.Cab.AttachSeq == s1, 3000, "① attach(seq " + s1 + ") 통지 직전에서 멈춤");
            cm.TestBeforeAttachFire = null;
            Unplug("①-s+1");
            Thread.Sleep(200);
            g1.Set();
            ctx.WaitUntil(() => LogFiles.ReadShared(r.Hub.Log.Path).Contains("늦은 attach 이벤트 무시 SIMCABRCR seq=" + s1), 3000, "① attach(seq " + s1 + ") 늦은 통지 무시");
            prov.Plug(r.Board, "SIMCABRCR", "COM298");
            ctx.WaitUntil(() => r.Cab.IsAttached && r.Cab.AttachSeq == s1 + 1 && r.T.Generation >= 2, 5000, "① attach(seq " + (s1 + 1) + ")");
            ctx.Check(r.Cab.ReplugClearPending(r.Cab.AttachSeq), "① 반영 못 한 세션의 detach 가 앞 세션 예약을 지우지 않음 → seq " + (s1 + 1) + " 에 예약");
            EnterPrefixed("①");

            // ---- ② attach 처리기가 등록·예약을 마친 직후 detach(펌프 시작 전) → 예약이 다음 세션으로
            r.IdleWait();
            Unplug("②-s");
            r.Board.InjectNoise(Noise);
            var e2 = new ManualResetEventSlim(false);
            var g2 = new ManualResetEventSlim(false);
            ctx.Defer(() => g2.Set());
            r.Hub.TestAfterAttachRegistered = c => { e2.Set(); g2.Wait(10000); };
            long s2 = r.Cab.AttachSeq + 1;
            prov.Plug(r.Board, "SIMCABRCR", "COM299");
            ctx.WaitUntil(() => e2.IsSet && r.Cab.AttachSeq == s2, 3000, "② attach(seq " + s2 + ") 처리기: 등록·예약 직후에서 멈춤");
            r.Hub.TestAfterAttachRegistered = null;
            ctx.Check(r.Cab.ReplugClearPending(s2), "② 펌프 등록과 같은 잠금 안에서 예약됨");
            Unplug("②-s+1");
            Thread.Sleep(200);
            g2.Set();
            int g0 = r.T.Generation;
            prov.Plug(r.Board, "SIMCABRCR", "COM300");
            ctx.WaitUntil(() => r.Cab.IsAttached && r.Cab.AttachSeq == s2 + 1 && r.T.Generation > g0, 5000, "② attach(seq " + (s2 + 1) + ")");
            ctx.Check(r.Cab.ReplugClearPending(r.Cab.AttachSeq), "② 쓰이지 않은 예약이 다음 세션으로 넘어감");
            EnterPrefixed("②");
        }

        // ================================================================== replug-clear-off
        [Scenario("replug-clear-off", "재연결 줄 지우기: config cables.<id>.replug_clear=false 인 케이블은 프롬프트에서 분리해도 예약 없음(셸이 아닌 장치로 옮겨 꽂는 케이블용)", Order = 659, TimeoutS = 60)]
        public static void ReplugClearOff(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIMRC8" }, "SIMCABRC8", "COM301", 17174,
                                 c => c.Cables["SIMCABRC8"] = new CableConfig { ReplugClear = false });
            ctx.Check(r.T.State == PromptState.AtPrompt && !r.Cab.ReplugClearEnabled, "전제: AT_PROMPT, replug_clear=false");
            ctx.Check(!Replug(ctx, r, "SIMCABRC8", "COM302", null), "예약 없음");
            int tx0 = r.Tx.Count;
            r.Human("\r");
            r.WaitState(PromptState.AtPrompt, 3000, "Enter → 프롬프트");
            ctx.Check(TxHex(r, tx0, TxSource.Human).SequenceEqual(new[] { "0D" }) && !ClearLogged(r), "사람 Enter 그대로(0D)");
        }

        // ================================================================== replug-clear-history
        [Scenario("replug-clear-history", "재연결 줄 지우기(실보드 11-03 재시험 첫 실패): 프롬프트에서 방향키로 히스토리를 위아래로 훑다 빈 줄(ESC[J BEL)에서 Enter 없이 분리 → 상태 HUMAN_TYPING(프롬프트에서 시작)이라도 예약 → 다시 꽂고 Enter → 05 15 0D, 잡음 실행 안 됨", Order = 660, TimeoutS = 60)]
        public static void ReplugClearHistory(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIMRCH" }, "SIMCABRCH", "COM303", 17175);
            foreach (var cmd in new[] { "echo h1", "echo h2" })
            {
                r.Human(cmd + "\r");
                r.WaitState(PromptState.AtPrompt, 3000, cmd);
            }
            r.IdleWait();
            long ring0 = r.Ring.End;
            foreach (var k in new[] { "\x1b[A", "\x1b[A", "\x1b[B", "\x1b[B", "\x1b[B" })      // 위 2번(echo h2 → echo h1), 아래 2번(빈 줄), 한 번 더(BEL)
            {
                r.Human(k);
                Thread.Sleep(120);
            }
            ctx.WaitUntil(() => r.RingText(ring0).EndsWith("\rroot@SIMRCH:/$ \x1b[J\x07", StringComparison.Ordinal), 2000,
                          "보드 히스토리 다시 그리기: … echo h1 → 빈 줄 ESC[J → BEL (" + BoardConsole.Visible(r.RingText(ring0)) + ")");
            ctx.Check(r.T.State == PromptState.HumanTyping && r.T.ShellLineEditing, "분리 직전 HUMAN_TYPING — 프롬프트에서 시작한 줄 편집 (" + r.T.Snapshot().StateName + ")");
            ctx.Check(Replug(ctx, r, "SIMCABRCH", "COM304", Noise), "HUMAN_TYPING(프롬프트 줄 편집)에서 분리 → 예약");
            long at = r.Cab.AttachRxPos;
            int tx0 = r.Tx.Count;
            r.Human("\r");
            r.WaitState(PromptState.AtPrompt, 3000, "다시 꽂고 Enter → 프롬프트");
            ctx.Check(TxHex(r, tx0, TxSource.Human).SequenceEqual(new[] { "05 15 0D" }), "보드 수신 = 05 15 0D");
            NotExecuted(ctx, r, at, "히스토리 탐색 뒤 분리");
            ctx.Check(r.RingText(at).Contains("\r\nroot@SIMRCH:"), "새 프롬프트");

            // ---- 실보드 첫 실패 직전 .raw 끝(7001_EVB-A1_152008.raw offset 2149~, 히스토리 다시 그리기로 끝남) 재생: 허브 hub.log 와 같은
            //      HUMAN_TYPING(partial_line_orphaned) 으로 복원되고, 그 상태는 프롬프트 줄 편집이며, 복원된 ESC[J 는 줄 지우기를 막지 않는다
            var tr = new PromptTracker(ctx.MakeConfig(), "EVB-A1");
            var raw = System.Text.Encoding.Latin1.GetBytes("root@EVB-A1:/$ printf '__U''H_B_%s\\n' u2d4f5; sh -c 'stty -a -F /dev/console 2>/\u000d\u000d\u000adev/null | tr \" \" \"\\n\" | grep -E \"^-?(ignpar|ignbrk|inpck)$\" | tr \"\\n\" \" \"; echo\u000d\u000d\u000a; cat /proc/uptime; grep -c . /.ash_history; grep -v __U /.ash_history | tail -2\u000d\u000d\u000a | od -An -tx1' </dev/null; printf '__U''H_E_%s_%s\\n' u2d4f5 $?\u001b[J\u001b[3A\u000droot@EVB-A1:/$ ls\u001b[J\u000droot@EVB-A1:/$ \u001b[J\u0007");
            ctx.Check(tr.Restore(new List<ReplayItem> { new ReplayItem { Abs = 2149, Data = raw, Ms = 47_560 } }, RestoreSource.Attach, 0, 2149 + raw.Length, 1_000_000, 2), "실보드 .raw 끝 재생");
            var sn = tr.Snapshot();
            ctx.Check(sn.State == PromptState.HumanTyping && sn.BlockedReason == "partial_line_orphaned", "실보드와 같은 복원: HUMAN_TYPING (partial_line_orphaned) (" + sn.StateName + "/" + sn.BlockedReason + ")");
            ctx.Check(tr.ShellLineEditing && !tr.ReplugClearBlockedFor(2), "그 상태는 프롬프트 줄 편집(다시 뽑으면 예약), 복원된 ESC[J 로 줄 지우기를 막지 않음");
        }

        // ================================================================== replug-clear-partial
        [Scenario("replug-clear-partial", "재연결 줄 지우기: 'cat /pro' 를 반쯤 친 채 분리 → 예약 → 다시 꽂은 뒤 첫 입력 'echo rcp' 앞에 ^E^U — 반쯤 친 글자도 잡음과 함께 지워짐(의도), 보드는 echo rcp 만 실행", Order = 661, TimeoutS = 60)]
        public static void ReplugClearPartial(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIMRCP" }, "SIMCABRCP", "COM305", 17176);
            r.Human("cat /pro");
            r.WaitState(PromptState.HumanTyping, 2000, "반쯤 친 'cat /pro'");
            ctx.Check(r.T.ShellLineEditing, "프롬프트 줄 편집");
            ctx.Check(Replug(ctx, r, "SIMCABRCP", "COM306", Noise), "반쯤 친 줄에서 분리 → 예약");
            long at = r.Cab.AttachRxPos;
            int tx0 = r.Tx.Count;
            r.Human("echo rcp\r");
            ctx.WaitUntil(() => r.RingText(at).Contains("\r\nrcp\r\n"), 3000, "'echo rcp' 실행");
            ctx.Check(TxHex(r, tx0, TxSource.Human).SequenceEqual(new[] { Hx("\u0005\u0015echo rcp\r") }), "첫 입력 = 05 15 + 'echo rcp\\r'");
            string after = r.RingText(at);
            ctx.Check(!after.Contains("cat:") && !after.Contains("not found") && !after.Contains("/proecho"), "반쯤 친 'cat /pro' 와 잡음이 지워져 'echo rcp' 만 실행 (" + BoardConsole.Visible(after) + ")");
            r.WaitState(PromptState.AtPrompt, 3000, "rcp 뒤");
        }

        // ================================================================== replug-clear-busy
        [Scenario("replug-clear-busy", "재연결 줄 지우기: 실행 중 프로그램(cat)에 사람이 친 입력(BUSY 에서 시작한 HUMAN_TYPING)에서 분리 → 예약 없음(셸 줄이 아님 — 잡음은 cooked tty 버퍼로 감, 한계: 다시 꽂은 뒤 Ctrl-C 권장), 다시 꽂은 뒤 ^C 에 접두 없음", Order = 662, TimeoutS = 60)]
        public static void ReplugClearBusy(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIMRCB" }, "SIMCABRCB", "COM307", 17177);
            long ring0 = r.Ring.End;
            r.Human("cat\r");
            r.WaitState(PromptState.Busy, 2000, "cat 실행 중(stdin 대기)");
            ctx.WaitUntil(() => r.RingText(ring0).Contains("cat\r\n"), 2000, "'cat' 에코");
            Thread.Sleep(200);
            r.Human("abc");
            r.WaitState(PromptState.HumanTyping, 2000, "실행 중 프로그램에 'abc' 입력");
            ctx.Check(!r.T.ShellLineEditing, "BUSY 에서 시작한 입력은 프롬프트 줄 편집이 아님");
            ctx.Check(!Replug(ctx, r, "SIMCABRCB", "COM308", null), "예약 없음");
            int tx0 = r.Tx.Count;
            r.Human("\x03");
            ctx.WaitUntil(() => r.RingText(r.Cab.AttachRxPos).Contains("root@SIMRCB:"), 3000, "^C → cat 종료 → 프롬프트");
            ctx.Check(TxHex(r, tx0, TxSource.Human).SequenceEqual(new[] { "03" }) && !ClearLogged(r), "^C 그대로(03), .log 에 hub-clear 없음");
        }

        // ================================================================== replug-clear-replay
        [Scenario("replug-clear-replay", "재연결 줄 지우기(실보드 11-03 재시험 둘째 실패) 재생: 실보드 .raw 의 줄 편집기 다시 그리기(7001_EVB-A1_174220·185504, 보드는 입력 무시 Silent) + 합성 사람 TX — 히스토리 탐색 + 'cat ' 백스페이스 뒤 분리 → 예약, 다시 꽂고 첫 입력(위 화살표)에 ^E^U, 다시 히스토리 탐색 + 'cat /pro'(오타·백스페이스) 뒤 분리 → 예약(실보드에서는 빠졌음), 다시 꽂고 Enter → 05 15 0D", Order = 663, TimeoutS = 90)]
        public static void ReplugClearReplay(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "EVB-A1" }, "SIMCABRRP", "COM309", 17178, toPrompt: false);
            r.Board.Silent = true;                                                       // 보드 에코 대신 실보드 .raw 바이트를 흘린다
            string Hs = "\u000droot@EVB-A1:/$ printf '__U''H_B_%s\\n' u4d6a1; sh -c 'stty -a -F /dev/console 2>/\u000d\u000d\u000adev/null | tr \" \" \"\\n\" | grep -E \"^-?(ignpar|inpck)$\" | tr \"\\n\" \" \"; grep -c . /\u000d\u000d\u000a.ash_history; cat /proc/uptime' </dev/null; printf '__U''H_E_%s_%s\\n' u4d6a1 $?";                                                           // 히스토리 항목 다시 그리기(실보드 그대로, 80열 접힘 포함)
            const string Us = "\rroot@EVB-A1:/$ \u001b[J";                            // ^E^U 뒤 busybox 다시 그리기
            const string Ds = "\u001b[J\u001b[2A\rroot@EVB-A1:/$ \u001b[J";           // 아래 화살표 → 빈 줄
            const string Qs = "\u001b[J\u001b[2A\rroot@EVB-A1:/$ ????\u001b[J";        // 더 오래된 항목('????' = 이전 탈착 잡음)
            const string UP = "\u001b[A", DOWN = "\u001b[B", BS = "\u007f", BSE = "\b\u001b[J";
            int n = 0;
            void Step(string tx, string rx)
            {
                if (tx != null) r.Human(tx);
                if (rx == null) return;
                long want = r.Ring.End + rx.Length;
                r.Board.EmitBytes(Encoding.Latin1.GetBytes(rx));
                n++;
                ctx.WaitUntil(() => r.Ring.End >= want && r.T.ProcessedPos >= want, 2000, "재생 " + n + ": " + BoardConsole.Visible(rx.Length > 24 ? rx.Substring(0, 24) + "…" : rx));
                Thread.Sleep(15);
            }
            void Type(string text, string echo = null) { foreach (char ch in text) Step(ch.ToString(), echo ?? ch.ToString()); }

            // ---- 세션 1(174220): 확정 프롬프트 → 위·아래로 히스토리 → 'cat ' → 백스페이스 4번 → 분리
            Step(null, "\r\nroot@EVB-A1:/$ ");
            r.WaitState(PromptState.AtPrompt, 3000, "확정 프롬프트");
            r.IdleWait();
            Step(UP, Hs); Step(DOWN, Ds); Step(UP, Hs); Step(UP, Qs); Step(DOWN, Hs); Step(DOWN, Ds);
            Type("cat ");
            for (int i = 0; i < 4; i++) Step(BS, BSE);
            ctx.Check(r.T.State == PromptState.HumanTyping && r.T.ShellLineEditing, "세션 1 끝: HUMAN_TYPING, 프롬프트 줄 편집");
            ctx.Check(Replug(ctx, r, "SIMCABRRP", "COM310", null), "첫 분리 → 예약");
            var s2 = r.T.Snapshot();
            ctx.Check(s2.State == PromptState.HumanTyping && s2.BlockedReason == "partial_line_orphaned", "실보드와 같은 복원: HUMAN_TYPING (partial_line_orphaned) (" + s2.StateName + ")");

            // ---- 세션 2(185504): 첫 입력 위 화살표(^E^U 접두) → 다시 그리기 → 히스토리 탐색 → 'cat [rp' ⌫⌫⌫ '/r' ⌫ 'pro' → 분리
            int tx0 = r.Tx.Count;
            Step(UP, Us + Hs);
            ctx.Check(TxHex(r, tx0, TxSource.Human).SequenceEqual(new[] { "05 15 1B 5B 41" }), "다시 꽂은 뒤 첫 입력(위 화살표) = 05 15 1B 5B 41");
            Step(DOWN, Ds); Step(DOWN, "\a"); Step(UP, Hs); Step(DOWN, Ds); Step(DOWN, "\a"); Step(UP, Hs); Step(UP, Qs); Step(DOWN, Hs); Step(DOWN, Ds);
            Type("cat [rp");
            for (int i = 0; i < 3; i++) Step(BS, BSE);
            Type("/r");
            Step(BS, BSE);
            Type("pro");
            var s3 = r.T.Snapshot();
            ctx.Check(s3.State == PromptState.HumanTyping && s3.PartialText == "cat /pro", "세션 2 끝: HUMAN_TYPING, partial 'cat /pro' (" + s3.StateName + ", '" + s3.PartialText + "')");
            ctx.Check(r.T.ShellLineEditing, "세션 2 끝도 프롬프트 줄 편집(지금 줄이 프롬프트로 시작 — 첫 키에 ^E^U 가 붙었어도)");
            ctx.Check(Replug(ctx, r, "SIMCABRRP", "COM311", null), "둘째 분리 → 예약(실보드에서는 빠졌던 것)");
            tx0 = r.Tx.Count;
            Step("\r", null);
            ctx.Check(TxHex(r, tx0, TxSource.Human).SequenceEqual(new[] { "05 15 0D" }), "다시 꽂고 Enter = 05 15 0D");
        }

        // ================================================================== replug-clear-chain
        [Scenario("replug-clear-chain", "재연결 줄 지우기 연쇄(FakeBoard): 프롬프트에서 분리(잡음) → 다시 꽂고 첫 키 위 화살표(^E^U 접두) → 히스토리 탐색 → 'cat /pro'(오타·백스페이스) 반쯤 → 분리(잡음) → 예약 → 다시 꽂고 Enter → 05 15 0D, 잡음·반쯤 친 줄 실행 안 됨", Order = 664, TimeoutS = 90)]
        public static void ReplugClearChain(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIMRCC" }, "SIMCABRCC", "COM312", 17179);
            foreach (var cmd in new[] { "echo c1", "echo c2" })
            {
                r.Human(cmd + "\r");
                r.WaitState(PromptState.AtPrompt, 3000, cmd);
            }
            r.IdleWait();
            ctx.Check(Replug(ctx, r, "SIMCABRCC", "COM314", Noise), "프롬프트에서 분리 → 예약");
            int tx0 = r.Tx.Count;
            long ring0 = r.Ring.End;
            r.Human("\x1b[A");
            ctx.WaitUntil(() => r.RingText(ring0).Contains("\rroot@SIMRCC:/$ echo c2\x1b[J"), 2000, "첫 키 위 화살표 → ^E^U 로 잡음 지움 + 히스토리 'echo c2'");
            ctx.Check(TxHex(r, tx0, TxSource.Human).SequenceEqual(new[] { "05 15 1B 5B 41" }), "첫 키 = 05 15 1B 5B 41");
            foreach (var k in new[] { "\x1b[B", "\x1b[A", "\x1b[A", "\x1b[B", "\x1b[B" }) { r.Human(k); Thread.Sleep(120); }
            foreach (var k in new[] { "c", "a", "t", " ", "[", "r", "p", "\x7f", "\x7f", "\x7f", "/", "r", "\x7f", "p", "r", "o" }) { r.Human(k); Thread.Sleep(40); }
            ctx.WaitUntil(() => r.T.Snapshot().PartialText == "cat /pro", 2000, "partial 'cat /pro'");
            ctx.Check(r.T.State == PromptState.HumanTyping && r.T.ShellLineEditing, "프롬프트 줄 편집 중");
            ctx.Check(Replug(ctx, r, "SIMCABRCC", "COM315", Noise), "둘째 분리 → 예약");
            long at = r.Cab.AttachRxPos;
            tx0 = r.Tx.Count;
            r.Human("\r");
            r.WaitState(PromptState.AtPrompt, 3000, "Enter → 프롬프트");
            ctx.Check(TxHex(r, tx0, TxSource.Human).SequenceEqual(new[] { "05 15 0D" }), "Enter = 05 15 0D");
            string after = r.RingText(at);
            ctx.Check(!after.Contains("cat:") && !after.Contains("not found") && !after.Contains("??????"), "잡음·반쯤 친 'cat /pro' 실행 안 됨 (" + BoardConsole.Visible(after) + ")");
        }

        // ================================================================== replug-clear-vi-prompt
        [Scenario("replug-clear-vi-prompt", "재연결 줄 지우기: vi(화면 주소 지정 CSI) 안에서 입력 중, 화면 마지막 줄이 프롬프트처럼 보여도('\\r root@…:/$ ' 뒤 커서 이동) 분리 → 예약 없음, 뒤 입력에 접두 없음", Order = 665, TimeoutS = 60)]
        public static void ReplugClearViPrompt(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIMRCV" }, "SIMCABRCV", "COM316", 17180);
            r.Human("vi /tmp/rcv.txt\r");
            r.WaitState(PromptState.Fullscreen, 3000, "vi");
            Thread.Sleep(200);
            r.Human("i"); Thread.Sleep(100); r.Human("hello"); Thread.Sleep(150);
            long ring0 = r.Ring.End;
            r.Board.Emit("\x1b[24;1H\rroot@SIMRCV:/$ \x1b[1;6H");                   // 화면 마지막 줄에 프롬프트처럼 보이는 글 + 커서를 편집 위치로
            ctx.WaitUntil(() => r.T.ProcessedPos >= ring0 + 26, 2000, "vi 화면 줄 수신");
            Thread.Sleep(150);
            ctx.Check(r.T.State == PromptState.HumanTyping && !r.T.ShellLineEditing, "vi 안 입력: 마지막 줄이 프롬프트처럼 보여도 프롬프트 줄 편집 아님 (" + r.T.Snapshot().StateName + ")");
            ctx.Check(!Replug(ctx, r, "SIMCABRCV", "COM317", null), "예약 없음");
            int tx0 = r.Tx.Count;
            r.Human("\x1b"); Thread.Sleep(200); r.Human(":q!\r");
            r.WaitState(PromptState.AtPrompt, 3000, "vi 종료");
            ctx.Check(TxHex(r, tx0, TxSource.Human).SequenceEqual(new[] { "1B", Hx(":q!\r") }) && !ClearLogged(r), "접두 없음");
        }

        // ================================================================== replug-clear-restart
        [Scenario("replug-clear-restart", "재연결 줄 지우기: 코어 재시작(케이블은 안 뽑음) 뒤 첫 attach → 예약 없음, 사람 입력에 접두 없음", Order = 656, TimeoutS = 60)]
        public static void ReplugClearRestart(ScenarioContext ctx)
        {
            var r1 = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIMRC7" }, "SIMCABRC7", "COM194", 17170);
            ctx.Check(r1.T.State == PromptState.AtPrompt, "전제: 코어 1 AT_PROMPT");
            var board = r1.Board;
            var cfg = r1.Cfg;
            Thread.Sleep(150);
            r1.Hub.Stop();
            var prov = new FakePortProvider();
            prov.Plug(board, "SIMCABRC7", "COM194");
            var r2 = HubRig.Boot(ctx, cfg, board, prov, "SIMCABRC7", false);
            ctx.Check(!r2.Cab.ReplugClearPending(r2.Cab.AttachSeq), "코어 재시작 뒤 첫 attach → 예약 없음");
            int tx0 = r2.Tx.Count;
            r2.Human("\r");
            r2.WaitState(PromptState.AtPrompt, 3000, "Enter → 프롬프트");
            ctx.Check(TxHex(r2, tx0, TxSource.Human).SequenceEqual(new[] { "0D" }) && !ClearLogged(r2), "사람 Enter 그대로(0D), .log 에 hub-clear 없음");
        }
    }
}
