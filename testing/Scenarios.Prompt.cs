// PromptTracker 시나리오(US-003): 24, 25, 26, 28, boot-detect, tracker-restore.
// 실보드 골든 재생(8, 27, 28-golden, boot-detect-golden)은 비공개 internal/testing 에 있다. 실시간 시나리오의 기대값은 계획 §3.2 규칙에서 손으로 정한다.
// Arbiter(US-004)가 아직 없으므로 "Claude 0바이트"는 "그 구간에 AT_PROMPT 가 한 번도 없었다"(Arbiter 는 AT_PROMPT 에서만 보낸다)로 확인한다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace UartHub.Testing
{
    internal static class PromptScenarios
    {
        // ------------------------------------------------------------------ 도우미
        sealed class Rig
        {
            public Hub Hub;
            public Cable Cab;
            public FakeBoard Board;
            public FakePortProvider Prov;
            public PromptTracker T;
            public long HumanBytes;
        }

        static Rig Start(ScenarioContext ctx, FakeBoardOptions opt, string cableId, string com, Config cfg = null, BoardStart start = BoardStart.Idle)
        {
            cfg ??= ctx.MakeConfig();
            var b = new FakeBoard(opt, start);
            ctx.Defer(b.PowerOff);
            var prov = new FakePortProvider();
            prov.Plug(b, cableId, com);
            var hub = new Hub(cfg, null, new IPortProvider[] { prov });
            hub.Start();
            ctx.Defer(hub.Stop);
            var r = new Rig { Hub = hub, Board = b, Prov = prov };
            ctx.WaitUntil(() => { r.Cab = hub.Cables.Find(cableId); return r.Cab != null && r.Cab.IsAttached; }, 5000, cableId + " attached");
            ctx.WaitUntil(() => { r.T = hub.Tracker(r.Cab); return r.T != null && r.T.Generation > 0; }, 5000, "PromptTracker 준비(복원/새 세션)");
            return r;
        }

        static void Human(ScenarioContext ctx, Rig r, string s)
        {
            var d = Encoding.UTF8.GetBytes(s);
            if (!r.Cab.Write(d, TxSource.Human)) ctx.Fail("Cable.Write 실패: " + r.Cab.LastError);
            r.HumanBytes += d.Length;
        }

        static void WaitState(ScenarioContext ctx, PromptTracker t, PromptState st, int timeoutMs, string what)
            => ctx.WaitUntil(() => t.State == st, timeoutMs, what + " → " + PromptTracker.Name(st) + " (현재 " + PromptTracker.Name(t.State) + ")");

        static void HoldState(ScenarioContext ctx, PromptTracker t, PromptState st, int ms, string what)
        {
            var end = Environment.TickCount64 + ms;
            while (Environment.TickCount64 < end)
            {
                var now = t.State;
                if (now != st) ctx.Fail(what + ": " + ms + "ms 동안 " + PromptTracker.Name(st) + " 유지 기대, " + PromptTracker.Name(now) + " 로 바뀜");
                Thread.Sleep(10);
            }
            ctx.Log("ok: " + what + " (" + ms + "ms 동안 " + PromptTracker.Name(st) + " 유지)");
        }

        static bool NoAtPromptBetween(PromptTracker t, long fromMs, long toMs)
            => !t.History().Any(h => h.State == PromptState.AtPrompt && h.Ms > fromMs && h.Ms < toMs);

        static string Hist(PromptTracker t, long fromMs) => string.Join(" → ", t.History().Where(h => h.Ms >= fromMs).Select(h => PromptTracker.Name(h.State)));

        // ================================================================== 24
        [Scenario("24", "type-ahead: 사람 명령 실행 중 ls 를 CR 없이 입력 → 새 프롬프트 뒤 재에코 → HUMAN_TYPING 유지, Claude 0바이트(AT_PROMPT 없음)", Order = 310, TimeoutS = 60)]
        public static void S24(ScenarioContext ctx)
        {
            var r = Start(ctx, new FakeBoardOptions { Hostname = "SIM24" }, "SIMCAB24", "COM81");
            ctx.Check(r.T.State == PromptState.Unknown, "attach 직후(복원할 파일 없음) UNKNOWN");
            Human(ctx, r, "\r");
            WaitState(ctx, r.T, PromptState.AtPrompt, 3000, "Enter → 열린 줄 프롬프트");

            long t0 = r.Hub.Clock.MonoMs;
            Human(ctx, r, "sleep 1\r");
            ctx.Check(r.T.State == PromptState.Busy && r.T.Snapshot().BlockedReason == "busy_since", "사람 CR → BUSY(blocked_reason busy_since)");
            Thread.Sleep(200);
            Human(ctx, r, "l");
            ctx.Check(r.T.State == PromptState.HumanTyping && r.T.Snapshot().BlockedReason == "partial_line", "명령 실행 중 'l'(CR 아님) → HUMAN_TYPING, partial_line");
            Thread.Sleep(30);
            Human(ctx, r, "s");
            // sleep 이 끝나면 셸이 프롬프트를 찍고 미리 친 'ls' 를 곧바로 다시 에코한다(같은 덩어리)
            ctx.WaitUntil(() => r.T.Candidates().Any(c => c.ArrivalMs > t0 + 900 && c.Verdict != CandidateVerdict.Pending), 4000, "sleep 뒤 새 프롬프트 후보 판정");
            var cand = r.T.Candidates().Last();
            ctx.Check(cand.Verdict == CandidateVerdict.Unconfirmed && cand.By == "followed", "새 프롬프트 뒤 재에코 'ls' 가 바로 이어짐 → 미확정(" + cand + ")");
            HoldState(ctx, r.T, PromptState.HumanTyping, 400, "재에코 뒤에도 HUMAN_TYPING 유지(type-ahead 는 해제하지 않는다)");
            long t1 = r.Hub.Clock.MonoMs;
            ctx.Check(NoAtPromptBetween(r.T, t0, t1), "사람 명령 시작 ~ 지금 AT_PROMPT 0회 → Claude 송신 0바이트 (이력: " + Hist(r.T, t0) + ")");
            ctx.Check(r.Board.TotalReceived == r.HumanBytes, "보드 수신 바이트 == 사람 송신 바이트(" + r.HumanBytes + ")");

            r.T.SetHumanClientCount(0);
            ctx.Check(r.T.Snapshot().BlockedReason == "partial_line_orphaned", "사람 클라이언트 0 → partial_line_orphaned");
            r.T.SetHumanClientCount(1);
            Human(ctx, r, "\x15");                    // ^U: 줄 지움 → 셸이 \r + 프롬프트 + ESC[K 를 다시 찍는다
            WaitState(ctx, r.T, PromptState.AtPrompt, 3000, "사람 ^U → HUMAN_TYPING 해제 + 다시 찍힌 빈 프롬프트 확정");
        }

        // ================================================================== 25
        [Scenario("25", "top 실행 → FULLSCREEN, 사람이 q 로 종료 → 빈 열린 줄 프롬프트로 HUMAN_TYPING 해제 → AT_PROMPT", Order = 320, TimeoutS = 60)]
        public static void S25(ScenarioContext ctx)
        {
            var r = Start(ctx, new FakeBoardOptions { Hostname = "SIM25", TopIntervalMs = 300 }, "SIMCAB25", "COM82");
            Human(ctx, r, "\r");
            WaitState(ctx, r.T, PromptState.AtPrompt, 3000, "Enter → 프롬프트");
            Human(ctx, r, "top\r");
            WaitState(ctx, r.T, PromptState.Fullscreen, 3000, "top 화면(ESC[H ESC[J) → FULLSCREEN");
            HoldState(ctx, r.T, PromptState.Fullscreen, 700, "top 이 다시 그리는 동안 FULLSCREEN 유지(Claude 0바이트)");
            ctx.Check(r.T.Snapshot().BlockedReason == "fullscreen", "blocked_reason fullscreen");
            long tq = r.Hub.Clock.MonoMs;
            Human(ctx, r, "q");
            ctx.Check(r.T.State == PromptState.HumanTyping, "FULLSCREEN 에서 사람 'q' → HUMAN_TYPING");
            WaitState(ctx, r.T, PromptState.AtPrompt, 3000, "top 종료 → 빈 열린 줄 프롬프트 확정 → HUMAN_TYPING 해제");
            var c = r.T.Candidates().Last();
            ctx.Check(c.Verdict == CandidateVerdict.Confirmed && c.By == "open" && c.ArrivalMs >= tq, "해제 근거 후보: q 이후 열린 줄로 확정(" + c + ")");
            var seq = r.T.History().Where(h => h.Ms >= tq).Select(h => h.State).ToList();
            ctx.Check(seq.FirstOrDefault() == PromptState.HumanTyping && seq.LastOrDefault() == PromptState.AtPrompt && !seq.Contains(PromptState.Busy),
                      "q 이후 상태 이력 HUMAN_TYPING → AT_PROMPT (" + Hist(r.T, tq) + ")");
            ctx.Check(r.Board.Phase == "shell", "보드는 셸로 복귀");
        }

        // ================================================================== 26
        [Scenario("26", "출력 데이터 속 프롬프트 모양: 저장 로그를 cat(115200 속도) → 후보 전부 미확정/foreign, cat 동안 AT_PROMPT 0회(Claude 0바이트), 끝난 뒤 AT_PROMPT", Order = 330, TimeoutS = 60)]
        public static void S26(ScenarioContext ctx)
        {
            var r = Start(ctx, new FakeBoardOptions { Hostname = "SIM26", PacingBytesPerSec = 11520 }, "SIMCAB26", "COM83");
            var sb = new StringBuilder();
            for (int i = 0; i < 12; i++)
            {
                sb.Append("root@SIM26:/$ \n");                                          // 개행과 함께 연속 출력되는 프롬프트 모양 줄
                sb.Append("root@SIM26:/$ [  135.576230] \x1b[1;37;41m Changed (35) -> (44) \x1b[1;37;40m\n");
                sb.Append("root@OTHER-BOARD:/tmp# ls -l\n");                            // 다른 호스트명(부팅 경계 없음) → foreign
                sb.Append("\x00\x00\x1b[Kroot@SIM26:/$ echo " + i + "\n");                // NUL·ESC[K 접두
            }
            int lines = 12 * 4;
            r.Board.PutFile("/tmp/saved.log", Encoding.UTF8.GetBytes(sb.ToString()));
            Human(ctx, r, "\r");
            WaitState(ctx, r.T, PromptState.AtPrompt, 3000, "Enter → 프롬프트");

            long pos0 = r.Cab.Ring.End;
            long t0 = r.Hub.Clock.MonoMs;
            Human(ctx, r, "cat /tmp/saved.log\r");
            ctx.WaitUntil(() => r.T.State == PromptState.AtPrompt && r.Board.PendingBytes == 0, 8000, "cat 끝 → 진짜 프롬프트 확정");
            var all = r.T.Candidates().Where(c => c.Start >= pos0).ToList();
            var fin = all.Last();
            var inData = all.Take(all.Count - 1).ToList();
            ctx.Check(fin.Verdict == CandidateVerdict.Confirmed && fin.By == "open" && fin.Host == "SIM26", "마지막 후보 = cat 뒤 진짜 프롬프트, 열린 줄 확정(" + fin + ")");
            ctx.Check(inData.Count == lines, "cat 데이터 속 후보 " + inData.Count + "개 == 프롬프트 모양 줄 " + lines + "개");
            ctx.Check(inData.All(c => c.Verdict == CandidateVerdict.Unconfirmed || c.Verdict == CandidateVerdict.Foreign),
                      "데이터 속 후보 확정 0개(미확정 " + inData.Count(c => c.Verdict == CandidateVerdict.Unconfirmed) + ", foreign " + inData.Count(c => c.Verdict == CandidateVerdict.Foreign) + ")");
            ctx.Check(inData.Count(c => c.Verdict == CandidateVerdict.Foreign) == 12 && inData.Where(c => c.Verdict == CandidateVerdict.Foreign).All(c => c.Host == "OTHER-BOARD"),
                      "root@OTHER-BOARD 줄 12개 → foreign_prompt");
            ctx.Check(r.T.Snapshot().Board == "SIM26" && r.Cab.Board == "SIM26", "foreign 은 보드명을 바꾸지 않음(추적기·기록 코어 모두 SIM26)");
            ctx.Check(NoAtPromptBetween(r.T, t0, fin.ArrivalMs), "cat TX ~ 진짜 프롬프트 도착 사이 AT_PROMPT 0회 → Claude 0바이트 (" + Hist(r.T, t0) + ")");
            ctx.Check(r.Board.TotalReceived == r.HumanBytes, "보드 수신 == 사람 송신(" + r.HumanBytes + "B)");
        }

        // ================================================================== 28
        [Scenario("28", "vi 종료 직후 AT_PROMPT 복귀(고착 없음): FakeBoard vi(ESC[?1049l 직후 프롬프트, CPR 응답은 사람 입력 아님)", Order = 350, TimeoutS = 60)]
        public static void S28(ScenarioContext ctx)
        {
            var r = Start(ctx, new FakeBoardOptions { Hostname = "SIM28" }, "SIMCAB28", "COM84");
            Human(ctx, r, "\r");
            WaitState(ctx, r.T, PromptState.AtPrompt, 3000, "Enter → 프롬프트");
            Human(ctx, r, "vi /tmp/v28.txt\r");
            WaitState(ctx, r.T, PromptState.Fullscreen, 3000, "vi 화면(ESC[?1049h ESC[999;999H ESC[6n ESC[H ESC[J) → FULLSCREEN");
            // 터미널의 CPR 응답: 출처 TerminalReply 로 오든, telnet 층이 사람 바이트로 넘기든 사람 입력으로 세지 않는다
            if (!r.Cab.Write(Encoding.ASCII.GetBytes("\x1b[24;80R"), TxSource.TerminalReply)) ctx.Fail("CPR 송신 실패");
            Thread.Sleep(150);
            Human(ctx, r, "\x1b[24;80R");
            ctx.Check(r.T.State == PromptState.Fullscreen && r.T.TerminalReplies == 2, "CPR 응답 2회(TerminalReply·사람 출처 안 CPR) → HUMAN_TYPING 아님, FULLSCREEN 유지");
            Thread.Sleep(150);
            long tk = r.Hub.Clock.MonoMs;
            Human(ctx, r, ":");
            ctx.Check(r.T.State == PromptState.HumanTyping, "vi 에서 ':' → HUMAN_TYPING");
            Thread.Sleep(60);
            Human(ctx, r, "q");
            Thread.Sleep(60);
            Human(ctx, r, "\r");
            WaitState(ctx, r.T, PromptState.AtPrompt, 3000, ":q Enter → vi 종료(ESC[?1049l + 프롬프트) → AT_PROMPT");
            HoldState(ctx, r.T, PromptState.AtPrompt, 300, "vi 종료 뒤 AT_PROMPT 고착 없이 유지(FULLSCREEN 으로 돌아가지 않음)");
            ctx.Check(r.Board.ViInsertedBytes == 0 && r.Board.Phase == "shell", "vi 버퍼 0바이트, 보드 셸 복귀");
            ctx.Check(r.T.Candidates().Last().ArrivalMs >= tk && r.T.Snapshot().ScreenSinceNewline == false, "확정 프롬프트는 vi 종료 뒤, 화면 모드 플래그 해제");
        }

        // ================================================================== boot-detect
        [Scenario("boot-detect", "BOOTING: FakeBoard reboot→probe→AT_PROMPT, run 출력 안 dmesg 매치 무시, E 마커 직후 후보 즉시 확정", Order = 360, TimeoutS = 90)]
        public static void BootDetect(ScenarioContext ctx)
        {
            // ---- 실시간: FakeBoard reboot (종료 메시지 → reboot: Restarting system → LDR>T###%%> → [    0.06…] → init, 프롬프트 자동 출력 없음)
            var r = Start(ctx, new FakeBoardOptions { Hostname = "SIMBD" }, "SIMCABBD", "COM85");
            Human(ctx, r, "\r");
            WaitState(ctx, r.T, PromptState.AtPrompt, 3000, "Enter → 프롬프트");
            int boots0 = r.T.Snapshot().BootMatches;
            Human(ctx, r, "reboot\r");
            WaitState(ctx, r.T, PromptState.Booting, 3000, "reboot → BOOTING");
            ctx.WaitUntil(() => r.Board.Phase == "postboot", 5000, "보드 부팅 완료(프롬프트 없이 대기, S0-9)");
            var sn = r.T.Snapshot();
            ctx.Check(sn.State == PromptState.Booting && sn.KernelPhase && sn.BootMatches > boots0, "부팅 뒤 프롬프트 없음 → BOOTING 유지, 커널 타임스탬프 줄 이후(" + sn.BootMatches + " 매치)");
            ctx.Check(!r.T.CanProbe(r.Hub.Clock.MonoMs, false, out string why0) && (why0 == "human_active" || why0 == "rx_not_quiet"),
                      "부팅 직후: 사람 유휴 3초·RX 무음 2초 전에는 probe 불가(" + why0 + ")");
            ctx.WaitUntil(() => r.T.CanProbe(r.Hub.Clock.MonoMs, false, out _), 8000, "BOOTING + 커널 타임스탬프 이후 + RX 무음 2초 + 사람 유휴 3초 → probe 허용");
            if (!r.Cab.Write(new[] { (byte)'\r' }, TxSource.Probe)) ctx.Fail("probe 송신 실패");
            WaitState(ctx, r.T, PromptState.AtPrompt, 3000, "probe CR → 새 프롬프트 확정 → BOOTING 끝, AT_PROMPT");
            ctx.Check(r.T.Snapshot().Board == "SIMBD", "보드명 SIMBD");

            // ---- run 출력 구간 안의 dmesg 매치([    0.062740] … 줄)는 무시
            var ring = r.Cab.Ring;
            int boots1 = r.T.Snapshot().BootMatches, ign1 = r.T.Snapshot().IgnoredBootMatches;
            var marks = new List<RunMarker>();
            r.T.RunMarkerSeen += m => { lock (marks) marks.Add(m); };
            long tr = r.Hub.Clock.MonoMs;
            r.T.BeginRun("r1", ring.End, tr);
            ctx.Check(r.T.State == PromptState.Busy && r.T.Snapshot().BlockedReason == "serialized", "run 등록 → AT_PROMPT 조건 2 거짓 → BUSY(serialized)");
            string sealedDmesg = "printf '__U''H_B_%s\\n' r1; sh -c 'dmesg' </dev/null; printf '__U''H_E_%s_%s\\n' r1 $?\r";
            if (!r.Cab.Write(Encoding.ASCII.GetBytes(sealedDmesg), TxSource.Claude)) ctx.Fail("run 송신 실패");
            ctx.WaitUntil(() => { lock (marks) return marks.Any(m => m.Kind == 'E'); }, 5000, "E 마커 관측");
            WaitState(ctx, r.T, PromptState.AtPrompt, 3000, "E 마커 뒤 프롬프트 확정 → 직렬화 해제, AT_PROMPT");
            var s2 = r.T.Snapshot();
            string between = Encoding.Latin1.GetString(ReadRing(ring, 0));
            ctx.Check(between.Contains("[    0.062740]") && between.Contains("__UH_E_r1_0"), "dmesg 출력에 부팅 타임스탬프 줄 [    0.062740] 포함, E 마커 rc 0");
            ctx.Check(s2.BootMatches == boots1 && s2.IgnoredBootMatches > ign1, "run 출력 안 boot_re 매치 " + (s2.IgnoredBootMatches - ign1) + "개 무시, BOOTING 진입 0");
            ctx.Check(!r.T.History().Any(h => h.Ms >= tr && h.State == PromptState.Booting), "run 구간 상태 이력에 BOOTING 없음 (" + Hist(r.T, tr) + ")");
            lock (marks) ctx.Check(marks.Count == 2 && marks[0].Kind == 'B' && marks[1].Kind == 'E' && marks[1].Rc == 0 && !s2.RunActive, "B·E 마커 순서, rc=0, run 해제");

            // ---- E 마커 직후 첫 후보는 열린 줄 조건 없이 확정(프롬프트와 같은 덩어리에 printk 가 붙어도)
            r.Board.AppendAfterNextPrompt("[  200.000000] late printk\r\n");
            r.T.BeginRun("r2", ring.End, r.Hub.Clock.MonoMs);
            string sealedEcho = "printf '__U''H_B_%s\\n' r2; sh -c 'echo hi' </dev/null; printf '__U''H_E_%s_%s\\n' r2 $?\r";
            if (!r.Cab.Write(Encoding.ASCII.GetBytes(sealedEcho), TxSource.Claude)) ctx.Fail("run 송신 실패");
            WaitState(ctx, r.T, PromptState.AtPrompt, 3000, "E 직후 프롬프트(+같은 덩어리 printk) → AT_PROMPT");
            var ec = r.T.Candidates().Last();
            ctx.Check(ec.Verdict == CandidateVerdict.Confirmed && ec.By == "e-marker", "E 마커 직후 후보: 바로 바이트가 이어졌지만 e-marker 규칙으로 확정(" + ec + ")");
            ctx.Check(!r.T.Snapshot().RunActive, "직렬화 해제");

            // ---- 마커 없는 명령(.tx 일반 줄): 플래그만 세우고(BOOTING 매치는 그대로 본다) 다음 확정 프롬프트로 푼다
            r.T.BeginRun("s1", ring.End, r.Hub.Clock.MonoMs, sealedRun: false);
            ctx.Check(r.T.State == PromptState.Busy && r.T.Snapshot().BlockedReason == "serialized", "마커 없는 spool 명령 등록 → BUSY(serialized)");
            if (!r.Cab.Write(Encoding.ASCII.GetBytes("echo spool-line\r"), TxSource.Spool)) ctx.Fail("spool 송신 실패");
            WaitState(ctx, r.T, PromptState.AtPrompt, 3000, "spool 줄 실행 뒤 열린 줄 프롬프트 확정 → 플래그 해제");
            ctx.Check(!r.T.Snapshot().RunActive && r.T.Candidates().Last().By == "open", "마커 없는 명령은 열린 줄 확정으로 해제(e-marker 규칙 아님)");

            r.Hub.WaitDrained(2000);
            int idxBoots = LogFiles.All(r.Hub.Config.LogRoot).Where(f => f.EndsWith(".idx", StringComparison.Ordinal))
                .Sum(f => LogFiles.ReadShared(Path.Combine(r.Hub.Config.LogRoot, f)).Split('\n').Count(l => l.Contains("\"k\":\"boot\"")));
            ctx.Log("참고: 기록 코어 최소 매처의 .idx boot 레코드 " + idxBoots + "개(최소 매처는 run 구간을 모른다 — m 필드로 구분, US-002 그대로 둠)");

            // ---- AT_UBOOT: U-Boot 창에서 (Claude 의 uboot 키 연타를 흉내 낸) 키 → 'evb: ' 확정 → AT_UBOOT, 명령 뒤 다시 확정, boot → BOOTING
            var u = Start(ctx, new FakeBoardOptions { Hostname = "SIMUB", UbootWindowMs = 1500 }, "SIMCABUB", "COM80");
            Human(ctx, u, "\r");
            WaitState(ctx, u.T, PromptState.AtPrompt, 3000, "Enter → 프롬프트");
            Human(ctx, u, "reboot\r");
            ctx.WaitUntil(() => u.Board.Phase == "uboot", 3000, "U-Boot autoboot 창");
            ctx.Check(u.T.State == PromptState.Booting, "LDR>T 뒤 U-Boot 창 → BOOTING");
            if (!u.Cab.Write(new byte[] { 0x1b }, TxSource.Claude)) ctx.Fail("키 송신 실패");
            WaitState(ctx, u.T, PromptState.AtUboot, 3000, "키 → 'evb: ' 열린 줄 확정 → BOOTING 끝, AT_UBOOT");
            ctx.Check(u.T.Snapshot().BlockedReason == null && u.T.Candidates().Last().UBoot, "AT_UBOOT 은 101 사유 없음(run 은 Arbiter 가 106), 후보 종류 U-Boot");
            if (!u.Cab.Write(Encoding.ASCII.GetBytes("printenv\r"), TxSource.Claude)) ctx.Fail("send 실패");
            ctx.WaitUntil(() => u.T.State == PromptState.AtUboot && u.T.Candidates().Count(c => c.UBoot && c.Verdict == CandidateVerdict.Confirmed) >= 2, 3000,
                          "printenv 출력 뒤 'evb: ' 다시 확정 → AT_UBOOT");
            if (!u.Cab.Write(Encoding.ASCII.GetBytes("boot\r"), TxSource.Claude)) ctx.Fail("send 실패");
            WaitState(ctx, u.T, PromptState.Booting, 3000, "boot → 커널 [    0.0x] → BOOTING");
        }

        static byte[] ReadRing(RingBuffer ring, long from)
        {
            long start = Math.Max(from, ring.Start);
            var buf = new byte[ring.End - start];
            int n = ring.Read(start, buf, 0, buf.Length);
            return n < 0 ? new byte[0] : buf.Take(n).ToArray();
        }

        // ================================================================== tracker-restore
        [Scenario("tracker-restore", "상태 복원(.raw 끝 64KB + .idx 도착 시각): 코어 재시작 ≤60초 전체 / >60초 부정 상태만+UNKNOWN(probe 허용) / attach·grab 부정 상태만+UNKNOWN(user-ok 필요), partial_line_orphaned, 사람 명령 추론", Order = 370, TimeoutS = 120)]
        public static void TrackerRestore(ScenarioContext ctx)
        {
            var cfg = ctx.MakeConfig();
            const string cab = "SIMCABRS";
            // ---- A: 프롬프트에 'ls' 를 반쯤 친 채로 코어 정지
            var r1 = Start(ctx, new FakeBoardOptions { Hostname = "SIMRS" }, cab, "COM86", cfg);
            var board = r1.Board;
            Human(ctx, r1, "\r");
            WaitState(ctx, r1.T, PromptState.AtPrompt, 3000, "Enter → 프롬프트");
            Human(ctx, r1, "echo hi\r");
            WaitState(ctx, r1.T, PromptState.AtPrompt, 3000, "echo hi → 프롬프트");
            Thread.Sleep(150);
            Human(ctx, r1, "l"); Thread.Sleep(40); Human(ctx, r1, "s");
            ctx.WaitUntil(() => r1.Cab.RxBytes == board.TotalSent && board.PendingBytes == 0, 2000, "에코 수신");
            Thread.Sleep(100);
            string rawA = r1.Cab.Writer.CurrentRawPath;
            r1.Hub.Stop();

            PromptTracker Offline(string raw, RestoreSource src, double gapS, out List<ReplayItem> items)
            {
                items = HubLogReplay.LoadTail(raw, Path.ChangeExtension(raw, ".idx"), cfg.RestoreTailBytes);
                var t = new PromptTracker(cfg, "SIMRS");
                t.Restore(items, src, gapS, 0, 1_000_000);
                return t;
            }
            var ta = Offline(rawA, RestoreSource.CoreRestart, 5, out var itemsA);
            ctx.Check(itemsA.Count > 1 && itemsA.All(x => !x.IsTx), ".raw/.idx 재생 입력 " + itemsA.Count + "개(.idx gap 으로 나눈 구간, 사람 입력은 기록되지 않음)");
            var sa = ta.Snapshot();
            ctx.Check(sa.State == PromptState.HumanTyping && sa.BlockedReason == "partial_line_orphaned", "코어 재시작 5초: 프롬프트 뒤 개행 없는 에코 'ls' → partial_line_orphaned (" + sa.StateName + "/" + sa.BlockedReason + ")");
            var tb = Offline(rawA, RestoreSource.CoreRestart, 120, out _);
            var sbn = tb.Snapshot();
            ctx.Check(sbn.State == PromptState.HumanTyping && sbn.Orphaned && sbn.Unknown, "코어 재시작 120초: 부정 상태(partial_line) 유지 + 나머지 UNKNOWN");

            // ---- B: 새 코어(같은 로그 루트) → 첫 attach 에서 자동 복원(alive 하트비트 기준 공백 ≤60초 → 전체)
            var prov2 = new FakePortProvider();
            prov2.Plug(board, cab, "COM87");
            var hub2 = new Hub(cfg, null, new IPortProvider[] { prov2 });
            hub2.Start();
            ctx.Defer(hub2.Stop);
            Cable c2 = null;
            ctx.WaitUntil(() => { c2 = hub2.Cables.Find(cab); return c2 != null && c2.IsAttached; }, 5000, "새 코어 attach");
            PromptTracker t2 = null;
            ctx.WaitUntil(() => { t2 = hub2.Tracker(c2); return t2 != null && t2.Generation > 0; }, 5000, "새 코어 추적기 복원 완료");
            var s2 = t2.Snapshot();
            ctx.Check(s2.RestoredFrom == RestoreSource.CoreRestart && s2.State == PromptState.HumanTyping && s2.BlockedReason == "partial_line_orphaned",
                      "허브 자동 복원(코어 재시작): partial_line_orphaned (" + s2.StateName + "/" + s2.BlockedReason + ")");
            var r2 = new Rig { Hub = hub2, Cab = c2, Board = board, Prov = prov2, T = t2 };
            Human(ctx, r2, "\x15");
            WaitState(ctx, t2, PromptState.AtPrompt, 3000, "사람 ^U → 줄 지움 + 빈 프롬프트 확정");

            // ---- C: vi 화면에서 케이블 탈착·재장착 → attach 출처: FULLSCREEN(부정 상태) 복원, probe 는 user-ok 필요
            Human(ctx, r2, "vi /tmp/rs.txt\r");
            WaitState(ctx, t2, PromptState.Fullscreen, 3000, "vi → FULLSCREEN");
            Thread.Sleep(100);
            int g = t2.Generation;
            prov2.Unplug(cab);
            ctx.WaitUntil(() => c2.Status == CableStatus.Detached, 3000, "탈착");
            prov2.Plug(board, cab, "COM88");
            ctx.WaitUntil(() => c2.IsAttached && t2.Generation > g, 5000, "재장착 + 추적기 복원");
            var s3 = t2.Snapshot();
            ctx.Check(s3.RestoredFrom == RestoreSource.Attach && s3.State == PromptState.Fullscreen && s3.ProbeNeedsUserOk && s3.Unknown,
                      "attach 복원: FULLSCREEN 유지(부정 상태), 나머지 UNKNOWN·user-ok 필요 (" + s3.StateName + ")");
            Thread.Sleep(200);
            Human(ctx, r2, "\x1b"); Thread.Sleep(150);
            Human(ctx, r2, ":"); Thread.Sleep(50); Human(ctx, r2, "q"); Thread.Sleep(50); Human(ctx, r2, "!"); Thread.Sleep(50); Human(ctx, r2, "\r");
            WaitState(ctx, t2, PromptState.AtPrompt, 3000, "재장착 뒤 :q! → AT_PROMPT");

            // ---- D: 프롬프트에서 탈착·재장착 → UNKNOWN, probe 는 user-ok 가 있어야만
            Thread.Sleep(100);
            g = t2.Generation;
            prov2.Unplug(cab);
            ctx.WaitUntil(() => c2.Status == CableStatus.Detached, 3000, "탈착");
            prov2.Plug(board, cab, "COM89");
            ctx.WaitUntil(() => c2.IsAttached && t2.Generation > g, 5000, "재장착 + 추적기 복원");
            var s4 = t2.Snapshot();
            long later = hub2.Clock.MonoMs + cfg.QuietMs + 100;
            ctx.Check(s4.State == PromptState.Unknown && s4.BlockedReason == "unknown", "attach 복원: 프롬프트였어도 UNKNOWN (" + s4.StateName + ")");
            ctx.Check(!t2.CanProbe(later, false, out string w1) && w1 == "user_ok_required" && t2.CanProbe(later, true, out _), "attach 출처 UNKNOWN: probe 는 --user-ok 일 때만(" + w1 + ")");
            string rawD = HubLogReplay.FindLatestRaw(cfg.LogRoot, c2.TcpPort, c2.Writer.CurrentRawPath);

            // ---- E: 프롬프트에 머문 파일을 출처별로 복원
            var te = Offline(rawD, RestoreSource.CoreRestart, 5, out _);
            ctx.Check(te.State == PromptState.AtPrompt, "코어 재시작 5초(≤60): 전체 복원 → AT_PROMPT");
            var tf = Offline(rawD, RestoreSource.CoreRestart, 120, out _);
            ctx.Check(tf.State == PromptState.Unknown && tf.CanProbe(1_000_000 + cfg.QuietMs, false, out _), "코어 재시작 120초(>60): UNKNOWN, probe 허용(user-ok 불필요)");
            var tg = Offline(rawD, RestoreSource.Grab, 0, out _);
            ctx.Check(tg.State == PromptState.Unknown && !tg.CanProbe(1_000_000 + cfg.QuietMs, false, out string w2) && w2 == "user_ok_required", "grab: UNKNOWN, probe 는 user-ok 필요");

            // ---- F: 사람이 시작한 긴 명령 도중 코어 정지 → 전체 복원이어도 AT_PROMPT 가 아니다(재생에서 사람 명령을 추론)
            var r3 = new Rig { Hub = hub2, Cab = c2, Board = board, Prov = prov2, T = t2 };
            Human(ctx, r3, "\r");
            WaitState(ctx, t2, PromptState.AtPrompt, 3000, "Enter → 프롬프트");
            Thread.Sleep(150);
            Human(ctx, r3, "sleep 5\r");
            Thread.Sleep(300);
            string rawF = c2.Writer.CurrentRawPath;
            hub2.Stop();
            var th = Offline(rawF, RestoreSource.CoreRestart, 1, out _);
            var sh = th.Snapshot();
            ctx.Check(sh.State == PromptState.Busy && sh.InferredInputs >= 1, "코어 재시작 1초: 'sleep 5' 실행 중 → BUSY(사람 명령 추론 " + sh.InferredInputs + "회), AT_PROMPT 아님 (" + sh.StateName + ")");
        }
    }
}
