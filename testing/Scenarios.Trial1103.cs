// 실보드 시험(2026-11-03, EVB-A1 COM3 :7001) 결함 회귀 시나리오: partial-printk(결함 4), probe-garbage(결함 6).
// 기대값은 실보드 .raw 발췌(7001_EVB-A1_102552.raw, 사람 TX 는 기록되지 않으므로 키 입력은 합성)와 계획 §3.2·§3.3 규칙에서 손으로 정한다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace UartHub.Testing
{
    internal static class Trial1103Scenarios
    {
        // ================================================================== partial-printk
        [Scenario("partial-printk", "partial(101 partial_line): 실보드 .raw 발췌 재생 — Tab 다시 그리기·\\b ESC[J 지우기 뒤 'cat /pro' 에코가 printk 줄로 끊겨도 partial='cat /pro', 이어 친 'c/' → 'cat /proc/', 80열 접힘·\\b ESC[K·\\b 공백 \\b 지우기. 허브(FakeBoard): printk 뒤 run → 101 partial 'cat /pro', 백스페이스 뒤 'cat /proc/'", Order = 590, TimeoutS = 90)]
        public static void PartialPrintk(ScenarioContext ctx)
        {
            // ---- 1. 추적기 단독: 실보드 .raw 바이트(발췌, 후보 목록만 줄임) + 합성 사람 TX(에코 직전 위치)
            var t = new PromptTracker(ctx.MakeConfig(), "EVB-A1");
            long abs = 0, ms = 1_000_000;
            t.ResetFresh(0, ms);
            void Rx(string s) { var b = Encoding.Latin1.GetBytes(s); t.OnRx(abs, b, ms); abs += b.Length; ms += 5; }
            void Tx(string s) { t.OnTx(TxSource.Human, Encoding.Latin1.GetBytes(s), ms, abs); ms += 5; }
            void Type(string s) { foreach (char ch in s) { Tx(ch.ToString()); Rx(ch.ToString()); } }
            void Partial(string want, string what)
            {
                var sn = t.Snapshot();
                ctx.Check(sn.State == PromptState.HumanTyping && sn.BlockedReason == "partial_line" && sn.PartialText == want,
                          what + " → HUMAN_TYPING, partial='" + want + "' (상태 " + sn.StateName + ", partial='" + BoardConsole.Visible(sn.PartialText ?? "(null)") + "')");
            }
            t.SetHumanClientCount(1);
            Rx("\r\nroot@EVB-A1:/$ ");
            ms += 200;
            t.Advance(ms);
            ctx.Check(t.State == PromptState.AtPrompt, "열린 줄 프롬프트 → AT_PROMPT");
            Type("cat /pr");
            Partial("cat /pr", "'cat /pr' 입력");
            Tx("\t");
            Rx("\rroot@EVB-A1:/$ cat /proc/\x1b[J");                                      // .raw: Tab 완성 = \r + 프롬프트 + 줄 다시 그리기
            Partial("cat /proc/", "Tab 완성 다시 그리기");
            Tx("\t");
            Rx("\x07\r\n1/                 258/               513/               kdrv_ipe/\r\n"
               + "257/               512/               kdrv_abc/\r\n\rroot@EVB-A1:/$ cat /proc/\x1b[J");   // 후보 목록(가운데 줄임) + 다시 그리기
            Partial("cat /proc/", "Tab 후보 목록 뒤 다시 그리기");
            for (int i = 0; i < 10; i++) { Tx("\x7f"); Rx("\x08\x1b[J"); }                // .raw: 백스페이스 에코 \b ESC[J × 10
            Partial("", "백스페이스 10번(\\b ESC[J) → 빈 줄");
            Type("cat /pro");
            Partial("cat /pro", "'cat /pro' 다시 입력");
            Rx("[  350.421137] [cmd_det(0x000001c0) data_det(0x000000fc)] [cmd_phase(3) data_phase(9) phase_unit(0)]\r\n");   // .raw 그대로: 에코 뒤에 printk
            Partial("cat /pro", "에코 뒤에 printk 가 붙고 개행(실보드 결함: partial '')");
            Rx("[  399.319526] \x1b[1;37;41m Changed (40) -> (49) \x1b[1;37;40m\r\n");
            Partial("cat /pro", "printk 한 줄 더");
            Type("c/");
            Partial("cat /proc/", "printk 뒤 이어 친 'c/'");
            Rx("[  400.000001] sim printk after c/\r\n");
            Partial("cat /proc/", "이어 친 글자 뒤에 또 printk");
            Tx("v");
            Rx("v");
            Tx("\t");
            Rx("\rroot@EVB-A1:/$ cat /proc/version \x1b[J");                             // printk 로 끊긴 뒤의 Tab 완성: 새 줄에 프롬프트부터 다시 그림
            Partial("cat /proc/version ", "printk 로 끊긴 뒤 Tab 다시 그리기 → 새 후보 기준");
            Tx("\r");
            ctx.Check(t.Snapshot().PartialText == null && t.State == PromptState.Busy, "Enter → partial 없음, BUSY");
            Rx("\r\nLinux version 4.19.148\r\nroot@EVB-A1:/$ ");
            ms += 200;
            t.Advance(ms);
            ctx.Check(t.State == PromptState.AtPrompt, "명령 끝 → AT_PROMPT");
            // type-ahead: 명령 실행 중 'ls' 를 미리 침 → 출력 줄 중간의 printk 타임스탬프(logread 형식) 앞부분이 partial 에 붙지 않음
            Type("logread");
            Tx("\r");
            Rx("\r\n");
            Tx("l"); Tx("s");                                                             // 실행 중이라 에코는 출력 사이에 섞인다
            Rx("lsNov  3 10:32:18 EVB-A1 kern.warn kernel: [  350.421137] [cmd_det(0x000001c0)]\r\n");
            Rx("Nov  3 10:33:07 EVB-A1 kern.warn kernel: [  399.319526] Changed (40) -> (49)\r\n");
            var ta = t.Snapshot();
            ctx.Check(ta.State == PromptState.HumanTyping && !(ta.PartialText ?? "").Contains("Nov") && !(ta.PartialText ?? "").Contains("kernel"),
                      "type-ahead 중 logread 줄(줄 중간 printk 타임스탬프)의 앞부분은 partial 에 안 붙음 ('" + ta.PartialText + "')");
            Rx("root@EVB-A1:/$ ls");                                                      // 명령 끝: 프롬프트 + 미리 친 'ls' 재에코
            Partial("ls", "새 프롬프트 뒤 재에코 'ls'");
            Tx("\x15"); Rx("\r\x1b[Kroot@EVB-A1:/$ \x1b[K");
            ms += 200;
            t.Advance(ms);
            ctx.Check(t.State == PromptState.AtPrompt, "^U 뒤 프롬프트");
            // 80열 접힘(FakeBoard·ash: \r\r\n) — 프롬프트 15열 + 65글자에서 접힘
            string a65 = new string('a', 65);
            Type(a65);
            Rx("\r\r\n");
            Type("bbb");
            Partial(a65 + "bbb", "80열 접힘 뒤 이어 친 글자까지");
            Tx("\x7f"); Rx("\b\x1b[K");                                                   // FakeBoard 식 지우기(ESC[K 는 정규화에서 빠짐)
            Partial(a65 + "bb", "\\b ESC[K 지우기");
            Tx("\x7f"); Rx("\b \b");                                                      // canonical 식 지우기
            Partial(a65 + "b", "\\b 공백 \\b 지우기");
            Tx("\x15"); Rx("\r\x1b[Kroot@EVB-A1:/$ \x1b[K");
            ctx.Check(t.Snapshot().PartialText == null, "^U → 입력 끝(partial 없음)");

            // ---- 2. 허브 + FakeBoard: printk 가 에코를 끊은 뒤 run → 101 partial_line 의 partial
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIMPP" }, "SIMCABPP", "COM157", 17159);
            r.T.SetHumanClientCount(1);
            long ring0 = r.Ring.End;
            r.Human("cat /pro");
            ctx.WaitUntil(() => r.RingText(ring0).EndsWith("cat /pro", StringComparison.Ordinal), 2000, "에코 'cat /pro'");
            r.Board.EmitPrintk("[cmd_det(0x000001c0) data_det(0x000000fc)] sim");
            ctx.WaitUntil(() => r.RingText(ring0).Contains("cat /pro[") && r.RingText(ring0).EndsWith("sim\r\n", StringComparison.Ordinal), 2000, "printk 가 에코 뒤에 붙고 개행");
            Thread.Sleep(100);
            var a = r.Call("run", r.P, "echo hi", "--timeout", "1");
            ctx.Check(J.N(a, "exit") == 101 && J.S(a, "blocked_reason") == "partial_line" && J.S(a, "partial") == "cat /pro",
                      "printk 뒤 run → 101 partial_line, partial='cat /pro' (" + J.Show(a) + ")");
            r.Human("c/xx\x7f\x7f");
            ctx.WaitUntil(() => r.T.Snapshot().PartialText == "cat /proc/", 2000, "'c/xx' + 백스페이스 2번(\\b ESC[K) → partial 'cat /proc/'");
            var b = r.Call("run", r.P, "echo hi", "--timeout", "1");
            ctx.Check(J.N(b, "exit") == 101 && J.S(b, "partial") == "cat /proc/", "run → 101 partial='cat /proc/' (" + J.Show(b) + ")");
            long ring1 = r.Ring.End;
            r.Human("version\r");
            ctx.WaitUntil(() => r.RingText(ring1).Contains("Linux version"), 3000, "보드 줄 버퍼도 'cat /proc/version' — partial 과 같음");
            r.WaitState(PromptState.AtPrompt, 3000, "명령 끝");
            ctx.Check(r.Board.TotalReceived == r.HumanBytes, "Claude 0바이트");
        }

        // ================================================================== probe-garbage
        [Scenario("probe-garbage", "재연결 뒤 probe: 탈착 잡음이 보드 셸 줄 버퍼에 숨은 글자를 남기고 커서까지 옮겨도(허브는 모름) probe --user-ok 는 ^E → ^U → CR 이라 그 줄을 실행하지 않고 프롬프트 확정, 뒤 run 정상. 바이트 사이 사람 입력(^E 뒤·^U 뒤) → 나머지(CR) 미전송, 101 human_active·bytes. 바이트 사이 deadline → 100 timeout·bytes, probe ^U 만으로는 UNKNOWN 유지", Order = 595, TimeoutS = 150)]
        public static void ProbeGarbage(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIMPG" }, "SIMCABPG", "COM158", 17158);
            var prov = (FakePortProvider)r.Prov;
            var arb = r.Hub.Arbiter;
            ctx.Defer(() => arb.TestBeforeWrite = null);
            int com = 159;
            // 분리 → (잡음) → 재연결 → UNKNOWN(--user-ok 필요)·RX 무음까지. 반환 = 재연결 위치
            long Replug(byte[] noise)
            {
                long seq0 = r.Cab.AttachSeq;
                int gen0 = r.T.Generation;
                prov.Unplug("SIMCABPG", new IOException("시험 탈착"));
                ctx.WaitUntil(() => !r.Cab.IsAttached, 3000, "케이블 분리");
                if (noise != null) r.Board.InjectNoise(noise);                         // 분리 중 잡음: 에코는 버려지고 줄 버퍼·커서에만 남는다
                Thread.Sleep(150);
                prov.Plug(r.Board, "SIMCABPG", "COM" + com++);
                ctx.WaitUntil(() => r.Cab.IsAttached && r.Cab.AttachSeq == seq0 + 1 && r.T.Generation > gen0, 5000, "재연결 + 추적기 복원");
                var sn = r.T.Snapshot();
                ctx.Check(sn.State == PromptState.Unknown && sn.ProbeNeedsUserOk, "재연결 뒤 UNKNOWN, probe 는 --user-ok 필요 (" + sn.StateName + ")");
                Thread.Sleep(r.Cfg.QuietMs + 100);
                return r.Cab.AttachRxPos;
            }
            List<string> ProbeTx(int from) => r.Tx.Skip(from).Where(x => x.Source == TxSource.Probe).Select(x => TnClient.Hex(x.Data)).ToList();

            // ---- 1. 숨은 줄 + 커서 왼쪽 2칸(ESC[D ESC[D): ^U 만이면 커서 앞 'chroot_p' 만 지워져 'g9' 가 실행된다 → ^E 로 줄 끝부터
            long attachPos = Replug(Encoding.ASCII.GetBytes("chroot_pg9\x1b[D\x1b[D"));
            var p0 = r.Call("probe", r.P);
            ctx.Check(J.N(p0, "exit") == 101 && J.S(p0, "blocked_reason") == "user_ok_required", "--user-ok 없으면 101 user_ok_required (" + J.Show(p0) + ")");
            int tx0 = r.Tx.Count;
            long rec0 = r.Board.TotalReceived;
            var p = r.Call("probe", r.P, "--user-ok");
            ctx.Check(J.S(p, "status") == "ok" && J.B(p, "prompt") == true && J.S(p, "state") == "AT_PROMPT", "probe --user-ok → 프롬프트 확정 AT_PROMPT (" + J.Show(p) + ")");
            var sent = ProbeTx(tx0);
            ctx.Check(sent.SequenceEqual(new[] { "05", "15", "0D" }) && r.Board.TotalReceived - rec0 == 3 && r.Tx.Count - tx0 == 3, "probe 송신 = ^E(05) → ^U(15) → CR(0D) 3바이트 (" + string.Join(" | ", sent) + ")");
            string after = r.RingText(attachPos);
            ctx.Check(!after.Contains("not found") && !after.Contains("chroot_pg9"), "숨은 줄(커서 옮겨진 'chroot_pg9') 실행 안 됨(재연결 뒤 RX: " + BoardConsole.Visible(after) + ")");
            r.IdleWait();
            var a = r.Call("run", r.P, "echo pg_ok", "--timeout", "5");
            ctx.Check(J.S(a, "status") == "ok" && J.N(a, "rc") == 0 && J.Seq(J.A(a, "out"), "pg_ok"), "probe 뒤 run 정상(줄 버퍼 깨끗) (" + J.Show(a) + ")");
            ctx.Check(r.LogText().Contains(">>> [claude-probe]"), ".log 에 '>>> [claude-probe]'");

            // ---- 2. 바이트 사이 사람 입력: 훅으로 sent==k 직전(판정 뒤, k 번째 바이트 쓰기 직전)에 사람 'z' → 나머지(CR 포함) 미전송
            foreach (int k in new[] { 2, 1 })
            {
                string what = k == 2 ? "^U 와 CR 사이" : "^E 와 ^U 사이";
                Replug(null);
                int armed = 1;
                arb.TestBeforeWrite = (c, q, n) => { if (q.Kind == TxKind.Probe && n == k && Interlocked.Exchange(ref armed, 0) == 1) r.Human("z"); };
                tx0 = r.Tx.Count;
                rec0 = r.Board.TotalReceived;
                long h0 = r.HumanBytes;
                var pi = r.Call("probe", r.P, "--user-ok");
                arb.TestBeforeWrite = null;
                var ps = ProbeTx(tx0);
                ctx.Check(Volatile.Read(ref armed) == 0, what + ": 훅이 사람 'z' 를 끼워 넣음");
                ctx.Check(J.N(pi, "exit") == 101 && J.S(pi, "blocked_reason") == "human_active" && J.N(pi, "bytes") == k,
                          what + " 사람 입력 → 101 human_active, bytes=" + k + " (" + J.Show(pi) + ")");
                ctx.Check(ps.SequenceEqual(new[] { "05", "15" }.Take(k)) && !ps.Contains("0D"), what + ": probe 송신 " + string.Join(" | ", ps) + " — CR 미전송");
                int pre = k == 1 ? 2 : 0;                                                    // ^E 만 보내고 멈춘 probe 는 재연결 줄 지우기 예약을 쓰지 않는다 → 사람 'z' 앞에 ^E^U
                ctx.Check(r.Board.TotalReceived - rec0 == k + pre + (r.HumanBytes - h0), what + ": 보드 수신 = probe " + k + "B + 사람 " + (r.HumanBytes - h0) + "B" + (pre > 0 ? " + 재연결 줄 지우기 2B" : ""));
                ctx.Check(r.Tx.Skip(tx0).Where(x => x.Source == TxSource.Human).Select(x => TnClient.Hex(x.Data)).SequenceEqual(new[] { k == 1 ? "05 15 7A" : "7A" }),
                          what + ": 사람 'z' " + (k == 1 ? "앞에 ^E^U(예약 남아 있음)" : "그대로(probe 의 ^U 가 예약을 씀)"));
                r.WaitState(PromptState.HumanTyping, 2000, what + ": 사람 'z' 입력 중");
                r.Human("\x15\r");                                                         // 사람이 줄을 지우고 Enter
                r.WaitState(PromptState.AtPrompt, 3000, what + ": 사람 ^U·Enter 뒤");
            }
            // ---- 3. 바이트 사이 deadline: ^U 쓰기 직전 훅이 deadline 을 넘기면 ^U 까지만 나가고 CR 은 안 나감 → 100 timeout·bytes 2, probe 의 ^U 만으로는 UNKNOWN 유지
            Replug(null);
            int armedD = 1;
            arb.TestBeforeWrite = (c, q, n) => { if (q.Kind == TxKind.Probe && n == 1 && Interlocked.Exchange(ref armedD, 0) == 1) Thread.Sleep(900); };
            tx0 = r.Tx.Count;
            rec0 = r.Board.TotalReceived;
            var pd = r.Call("probe", r.P, "--user-ok", "--timeout", "0.6");
            arb.TestBeforeWrite = null;
            var pdTx = ProbeTx(tx0);
            ctx.Check(J.N(pd, "exit") == 100 && J.S(pd, "status") == "timeout" && J.N(pd, "bytes") == 2, "바이트 사이 deadline → 100 timeout, bytes=2 (" + J.Show(pd) + ")");
            ctx.Check(pdTx.SequenceEqual(new[] { "05", "15" }) && r.Board.TotalReceived - rec0 == 2, "deadline: probe 송신 05 | 15 — CR 미전송 (" + string.Join(" | ", pdTx) + ")");
            Thread.Sleep(200);
            var sd = r.T.Snapshot();
            ctx.Check(sd.State == PromptState.Unknown && sd.ProbeNeedsUserOk, "probe 의 ^E^U 만으로는 UNKNOWN·user-ok 필요 유지 (" + sd.StateName + ")");
            r.Human("\r");
            r.WaitState(PromptState.AtPrompt, 3000, "사람 Enter → 프롬프트");
            r.IdleWait();
            ctx.Check(J.Seq(J.A(r.Call("run", r.P, "echo pg_end", "--timeout", "5"), "out"), "pg_end"), "끝: run 정상");
        }
    }
}
