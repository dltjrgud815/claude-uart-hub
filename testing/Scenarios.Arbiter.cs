// US-004 시나리오: Arbiter·run 봉인·ControlServer·uart.ps1 (계획 §4 S6 표 1, 2, 3, 4, 5, 6, 7, 9, 16, 19, 21, 23, 29, 30, 31, 32).
// 기대값은 계획 §3.3·§3.4·§3.6 규칙에서 손으로 정한다. "Claude 0바이트"는 FakeBoard 수신 바이트 − 사람 송신 바이트로 센다.
// spool(.tx) 경로는 US-006, push 청크는 Phase B — 여기서는 같은 게이트(Arbiter.Submit, Gate.Prompt/Token, Source.Spool)로 진입점만 확인한다.
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
    internal static class ArbiterScenarios
    {
        static void Ok(ScenarioContext ctx, JsonElement e, string what)
            => ctx.Check(J.S(e, "status") == "ok", what + " → status ok (" + J.Show(e) + ")");

        static void Code(ScenarioContext ctx, JsonElement e, long exit, string status, string what)
            => ctx.Check(J.N(e, "exit") == exit && J.S(e, "status") == status, what + " → " + exit + " " + status + " (" + J.Show(e) + ")");

        static long Received(HubRig r) => r.Board.TotalReceived;

        static int Count(string s, string sub)
        {
            int n = 0;
            for (int i = s.IndexOf(sub, StringComparison.Ordinal); i >= 0; i = s.IndexOf(sub, i + sub.Length, StringComparison.Ordinal)) n++;
            return n;
        }

        // ================================================================== 1
        [Scenario("1", "run 출력·rc(0/1/42), printk kernel 태그(ANSI 접두 포함), CRLF 정규화 + uart.ps1 프로세스(--json 필드, 비JSON stderr 'uart: E10x', 종료코드 = 보드 rc, -B64Req)", Order = 400, TimeoutS = 180)]
        public static void S1(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIM1" }, "SIMCAB01", "COM101", 17101);
            var a = r.Call("run", r.P, "echo hi; false", "--json");
            Ok(ctx, a, "run 'echo hi; false'");
            ctx.Check(J.N(a, "rc") == 1 && J.N(a, "exit") == 1 && J.Seq(J.A(a, "out"), "hi") && J.B(a, "trailing_newline") == true && J.A(a, "kernel").Length == 0,
                      "out=[\"hi\"], rc=1, exit=1, trailing_newline=true, kernel=[]");
            foreach (var f in new[] { "status", "rc", "out", "trailing_newline", "cable", "com", "port", "board", "rx_offset", "raw_offset", "req_id", "run_id" })
                if (!J.Has(a, f)) ctx.Fail("응답 필드 없음: " + f + " (" + J.Show(a) + ")");
            ctx.Check(J.S(a, "cable") == "SIMCAB01" && J.S(a, "com") == "COM101" && J.S(a, "board") == "SIM1" && J.N(a, "port") == r.Cab.TcpPort
                      && Regex.IsMatch(J.S(a, "rx_offset"), @"^\d+:\d+$"), "공통 필드 cable/com/board/port/rx_offset(epoch:offset)");

            a = r.Call("run", r.P, "true");
            ctx.Check(J.N(a, "rc") == 0 && J.A(a, "out").Length == 0 && J.B(a, "trailing_newline") == true, "run 'true' → rc 0, out=[] (" + J.Show(a) + ")");
            a = r.Call("run", r.P, "exit 42");
            ctx.Check(J.S(a, "status") == "ok" && J.N(a, "rc") == 42 && J.N(a, "exit") == 42, "run 'exit 42' → rc 42 = exit 42 (봉인 하위 셸 안의 exit)");

            // printk: /dev/kmsg <3> (console_loglevel 4 → 콘솔 출력)
            a = r.Call("run", r.P, "echo a; echo '<3>UHTEST' > /dev/kmsg; sleep 0.2; echo b");
            var o = J.A(a, "out");
            ctx.Check(o.Length == 3 && o[0] == "a" && Regex.IsMatch(o[1], @"^\[\s*\d+\.\d+\] UHTEST$") && o[2] == "b" && J.Seq(J.A(a, "kernel"), "1"),
                      "printk 줄만 kernel 태그: out=" + string.Join("|", o) + " kernel=" + string.Join(",", J.A(a, "kernel")));

            // ANSI 접두 printk 를 run 도중 주입(실보드 컬러 printk 형태): 태그는 ANSI 접두를 허용하고 out 에서는 ANSI 를 뺀다
            JsonElement ansi = default;
            var th = new Thread(() => ansi = r.Call("run", r.P, "echo x; sleep 0.8; echo y"));
            long ring0 = r.Ring.End;
            th.Start();
            ctx.WaitUntil(() => r.RingText(ring0).Contains("\r\nx\r\n"), 5000, "run 출력 x 도착");
            r.Board.EmitPrintk("UHANSI", ansi: true);
            th.Join(10000);
            o = J.A(ansi, "out");
            ctx.Check(r.RingText(ring0).Contains("\x1b[1;33m["), "원시 RX 에 ANSI 접두 printk(ESC[1;33m[) 가 실제로 있음");
            ctx.Check(o.Length == 3 && o[0] == "x" && Regex.IsMatch(o[1], @"^\[\s*\d+\.\d+\] UHANSI$") && o[2] == "y" && J.Seq(J.A(ansi, "kernel"), "1"),
                      "ANSI 접두 printk → kernel 태그, out 에는 ANSI 없음: " + string.Join("|", o));
            ctx.Check(r.RingText(ring0).Contains("x\r\n") && o.All(l => l.IndexOf('\r') < 0 && l.IndexOf('\x1b') < 0), "CRLF→LF: 원시는 \\r\\n, out 줄에는 CR·ESC 없음");

            // ---- uart.ps1 (pwsh 프로세스): --json 필드, 종료코드 = 보드 rc
            var u = r.Uart(null, "run", r.P, "exit 42", "--json");
            ctx.Check(u.Code == 42, "uart.ps1 run 'exit 42' --json → 종료코드 42");
            ctx.Check(u.Out.EndsWith("\n") && u.Out.IndexOf('\r') < 0 && u.Out.TrimEnd('\n').Split('\n').Length == 1, "--json 출력은 LF 로 끝나는 한 줄(CR 없음)");
            using (var doc = JsonDocument.Parse(u.Out))
            {
                var e = doc.RootElement;
                foreach (var f in new[] { "status", "rc", "out", "trailing_newline", "cable", "com", "board", "rx_offset" })
                    if (!J.Has(e, f)) ctx.Fail("uart.ps1 --json 필드 없음: " + f);
                ctx.Check(J.S(e, "status") == "ok" && J.N(e, "rc") == 42 && J.A(e, "out").Length == 0 && J.S(e, "board") == "SIM1",
                          "uart.ps1 --json: status ok, rc 42, out [], board SIM1");
            }
            u = r.Uart(null, "run", r.P, "echo 한글 hi; exit 7");
            ctx.Check(u.Code == 7 && u.Out == "한글 hi\n" && u.Err.Length == 0, "비JSON run: stdout 은 출력 줄(UTF-8, LF), 종료코드 7");
            u = r.Uart(null, "run", "17999", "echo x");
            ctx.Check(u.Code == 102 && u.Err.StartsWith("uart: E102 no_cable", StringComparison.Ordinal) && u.Out.Length == 0, "없는 대상 → 종료코드 102 + stderr 'uart: E102 no_cable'");
            u = r.Uart(null, "run", r.P, "echo a\nb");
            ctx.Check(u.Code == 106 && u.Err.StartsWith("uart: E106 rejected", StringComparison.Ordinal), "LF 든 cmd → 106 + stderr 'uart: E106'");
            u = r.Uart(new Dictionary<string, string> { ["UART_HUB_CONTROL_PORT"] = "17198" }, "ls");
            ctx.Check(u.Code == 103 && u.Err.StartsWith("uart: E103 hub_down", StringComparison.Ordinal), "허브 미응답 포트 → 103 + stderr 'uart: E103'");

            // -B64Req: 따옴표·$·\·'-'로 시작하는 인자·한글이 그대로 전달된다
            string[] argv = { "run", r.P, "echo 'a\"b$c' -x 한글; exit 42", "--timeout", "8", "--json" };
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("[" + string.Join(",", argv.Select(Json.Str)) + "]"));
            u = r.Uart(null, "-B64Req", b64);
            ctx.Check(u.Code == 42, "-B64Req run → 종료코드 42");
            using (var doc = JsonDocument.Parse(u.Out))
                ctx.Check(J.Seq(J.A(doc.RootElement, "out"), "a\"b$c -x 한글") && J.N(doc.RootElement, "rc") == 42, "-B64Req: 인자 그대로 전달(out=" + string.Join("|", J.A(doc.RootElement, "out")) + ")");
            u = r.Uart(null, "status", r.P);
            ctx.Check(u.Code == 0 && u.Out.Contains("state=AT_PROMPT\n") && u.Out.IndexOf('\r') < 0, "비JSON status: key=value 줄(LF), state=AT_PROMPT");
        }

        // ================================================================== 2
        [Scenario("2", "봉인: 'ls # x', 'sleep 1 &', 따옴표 불균형, 'exit 3', 'cat'(stdin) → 모두 E 마커·rc 회수, 이후 AT_PROMPT 복귀", Order = 410, TimeoutS = 90)]
        public static void S2(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIM2" }, "SIMCAB02", "COM102", 17102);
            var cases = new (string Cmd, long Rc)[] { ("ls # x", 0), ("sleep 1 &", 0), ("echo 'abc", 2), ("exit 3", 3), ("cat", 0) };
            foreach (var (cmd, rc) in cases)
            {
                var sw = Stopwatch.StartNew();
                var a = r.Call("run", r.P, cmd, "--timeout", "8");
                ctx.Check(J.S(a, "status") == "ok" && J.N(a, "rc") == rc, "run '" + cmd + "' → E 마커 회수, rc " + rc + " (" + sw.ElapsedMilliseconds + "ms, " + J.Show(a) + ")");
                if (cmd == "cat") ctx.Check(sw.ElapsedMilliseconds < 4000, "cat 은 </dev/null 로 즉시 EOF(대기 없음)");
                if (cmd == "echo 'abc") ctx.Check(J.A(a, "out").Any(l => l.Contains("syntax error")), "따옴표 불균형 → 하위 셸 안의 syntax error");
                r.WaitState(PromptState.AtPrompt, 3000, "'" + cmd + "' 뒤");
            }
            ctx.Check(r.T.Candidates().Count(c => c.By == "e-marker") >= cases.Length, "각 run 뒤 프롬프트는 E 마커 직후 후보로 확정");
            var last = r.Call("run", r.P, "echo still-ok");
            ctx.Check(J.Seq(J.A(last, "out"), "still-ok"), "마지막 확인 run 정상(콘솔 오염 없음)");
        }

        // ================================================================== 3
        [Scenario("3", "--inline: exit·;·PS1 은 106, 'cd /tmp' 는 다음 run 에서 유지(격리 run 의 cd 는 유지 안 됨) + 106 거부(제어문자·이스케이프 뒤 900B)", Order = 420, TimeoutS = 90)]
        public static void S3(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIM3" }, "SIMCAB03", "COM103", 17103);
            long rec0 = Received(r);
            foreach (var (cmd, inl) in new[] { ("exit", true), ("exit 0", true), ("export PS1='x$ '", true), ("unset PS2", true), ("export PROMPT_COMMAND=x", true),
                                                ("cd /tmp; ls", true), ("cd /tmp && ls", true), ("cd $(pwd)", true), ("ls", true),
                                                ("echo a\nb", false), ("echo a\rb", false), ("echo a\tb", false), ("echo " + new string('x', 896), false),
                                                ("echo " + new string('\'', 300), false) })
            {
                var a = inl ? r.Call("run", r.P, cmd, "--inline") : r.Call("run", r.P, cmd);
                Code(ctx, a, 106, "rejected", (inl ? "--inline " : "") + BoardConsole.Visible(cmd.Length > 30 ? cmd.Substring(0, 30) + "…(" + cmd.Length + ")" : cmd));
            }
            Code(ctx, r.Call("run", r.P, "echo x", "--req-id", "bad id\n"), 106, "rejected", "--req-id 에 공백·LF");
            Code(ctx, r.Call("send", r.P, "echo a\nb"), 106, "rejected", "send 원문에 LF");
            ctx.Check(Received(r) == rec0, "거부된 요청은 보드에 0바이트");
            var ok900 = r.Call("run", r.P, "echo " + new string('x', 895));
            ctx.Check(J.S(ok900, "status") == "ok" && J.Seq(J.A(ok900, "out"), new string('x', 895)), "이스케이프 뒤 정확히 900B 는 허용");

            Ok(ctx, r.Call("run", r.P, "cd /tmp", "--inline"), "--inline 'cd /tmp'");
            ctx.Check(J.Seq(J.A(r.Call("run", r.P, "pwd"), "out"), "/tmp"), "다음 run(격리) 의 pwd = /tmp (inline cd 가 셸에 남음)");
            ctx.Check(J.Seq(J.A(r.Call("run", r.P, "cd /; pwd"), "out"), "/"), "격리 run 안의 cd / → /");
            ctx.Check(J.Seq(J.A(r.Call("run", r.P, "pwd"), "out"), "/tmp"), "격리 run 의 cd 는 셸에 남지 않음(여전히 /tmp)");
            Ok(ctx, r.Call("run", r.P, "export UH3=42", "--inline"), "--inline export");
            ctx.Check(J.Seq(J.A(r.Call("run", r.P, "echo $UH3"), "out"), "42"), "inline export 가 다음 run 에 보임");
            Ok(ctx, r.Call("run", r.P, "cd /", "--inline"), "--inline 'cd /'(되돌림)");
            ctx.Check(r.LogText().Contains("] (inline) cd /tmp"), ".log 에 '>>> [claude …] (inline) cd /tmp' 기록");
        }

        // ================================================================== 4
        [Scenario("4", "사람이 반쯤 친 줄 → 101 partial_line, Claude 0바이트, 사람이 이어 친 명령 그대로 실행. 클라이언트 0 → partial_line_orphaned, --clear-line(^U + !!! [claude-clear] 기록)", Order = 430, TimeoutS = 90)]
        public static void S4(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIM4" }, "SIMCAB04", "COM104", 17104);
            long rec0 = Received(r), h0 = r.HumanBytes;
            r.Human("cat /pro");
            r.WaitState(PromptState.HumanTyping, 2000, "사람 'cat /pro'");
            var a = r.Call("run", r.P, "echo hi", "--timeout", "1");
            Code(ctx, a, 101, "busy", "반쯤 친 줄에 run");
            ctx.Check(J.S(a, "blocked_reason") == "partial_line" && J.S(a, "partial") == "cat /pro" && J.A(a, "last_lines") != null, "blocked_reason partial_line, partial='cat /pro', last_lines 포함");
            Code(ctx, r.Call("send", r.P, "{ENTER}", "--timeout", "0.5"), 101, "busy", "반쯤 친 줄에 send {ENTER}");
            Code(ctx, r.Call("run", r.P, "echo hi", "--clear-line", "--timeout", "0.6"), 101, "busy", "사람 클라이언트 수 모름(-1, telnet 전)·orphaned 아님 → --clear-line 도 대기");
            ctx.Check(Received(r) - rec0 == r.HumanBytes - h0, "보드 수신 == 사람 송신(" + (r.HumanBytes - h0) + "B), Claude 0바이트");
            long ringA = r.Ring.End;
            r.Human("c/version\r");
            ctx.WaitUntil(() => r.RingText(ringA).Contains("Linux version 4.19.91"), 3000, "사람이 이어 친 'cat /proc/version' 이 그대로 실행됨");
            r.WaitState(PromptState.AtPrompt, 3000, "명령 끝");

            // ---- 클라이언트가 떠남 → orphaned
            r.Human("ls -l");
            r.WaitState(PromptState.HumanTyping, 2000, "사람 'ls -l'");
            r.T.SetHumanClientCount(0);
            var st = r.Call("status", r.P);
            ctx.Check(J.S(st, "blocked_reason") == "partial_line_orphaned" && J.N(st, "human_clients") == 0, "status: partial_line_orphaned, human_clients 0");
            long rec1 = Received(r), h1 = r.HumanBytes;
            Code(ctx, r.Call("run", r.P, "echo hi", "--timeout", "0.8"), 101, "busy", "orphaned + --clear-line 없음");
            r.T.SetHumanClientCount(1);
            var nc = r.Call("run", r.P, "echo no", "--clear-line", "--timeout", "0.8");
            ctx.Check(J.N(nc, "exit") == 101, "사람 클라이언트 1개 → --clear-line 도 지우지 않음(101 " + J.S(nc, "blocked_reason") + ")");
            ctx.Check(Received(r) - rec1 == r.HumanBytes - h1, "지우기 전까지 Claude 0바이트");
            r.T.SetHumanClientCount(0);
            var c = r.Call("run", r.P, "echo cleared4", "--clear-line", "--timeout", "5");
            ctx.Check(J.S(c, "status") == "ok" && J.Seq(J.A(c, "out"), "cleared4") && J.S(c, "cleared") == "ls -l", "--clear-line: ^U 로 'ls -l' 지운 뒤 run 정상 (" + J.Show(c) + ")");
            var clr = r.Tx.Where(x => x.Source == TxSource.Claude && x.Data.Length == 1 && x.Data[0] == 0x15).ToList();
            ctx.Check(clr.Count == 1, "Claude ^U 정확히 1회");
            ctx.Check(r.LogText().Contains("!!! [claude-clear] ls -l"), ".log 에 지우기 전 내용 '!!! [claude-clear] ls -l'");
            r.WaitState(PromptState.AtPrompt, 3000, "clear-line run 뒤");
        }

        // ================================================================== 5
        [Scenario("5", "vi(FULLSCREEN): run·send·.tx 일반 줄 진입점(Gate.Prompt, Source.Spool)이 모두 101, vi 버퍼에 0바이트·보드 수신 0바이트, vi 종료 뒤 run 정상", Order = 440, TimeoutS = 90)]
        public static void S5(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIM5" }, "SIMCAB05", "COM105", 17105);
            r.Human("vi /tmp/v5.txt\r");
            r.WaitState(PromptState.Fullscreen, 3000, "vi");
            ctx.WaitUntil(() => r.Board.Phase == "vi", 2000, "보드 vi 모드");
            Thread.Sleep(200);
            long rec0 = Received(r);
            int vi0 = r.Board.ViInsertedBytes;
            var a = r.Call("run", r.P, "echo x", "--timeout", "1");
            ctx.Check(J.N(a, "exit") == 101 && J.S(a, "blocked_reason") == "fullscreen", "vi 중 run → 101 fullscreen");
            ctx.Check(J.S(r.Call("send", r.P, "hello5", "--timeout", "0.8"), "blocked_reason") == "fullscreen", "vi 중 send → 101 fullscreen");
            ctx.Check(J.N(r.Call("send", r.P, "{NONL}i", "--timeout", "0.8"), "exit") == 101, "vi 중 send {NONL}i → 101");
            ctx.Check(J.N(r.Call("run", r.P, "echo x", "--force", "--timeout", "0.5", "--idle", "100"), "exit") == 101, "--force 도 사람 유휴(--idle 100s) 미충족이면 101");
            // .tx 일반 줄(US-006 이 쓸 진입점): Gate.Prompt + Source.Spool
            var sp = r.Hub.Arbiter.Submit(r.Cab, new TxRequest { Kind = TxKind.Send, Gate = TxGate.Prompt, Source = TxSource.Spool, Cmd = "echo tx5", TimeoutS = 0.8 }, out _);
            ctx.Check(sp.Wait(5000) && sp.Exit == 101, ".tx 일반 줄 진입점(Gate.Prompt) → 101");
            ctx.Check(Received(r) == rec0 && r.Board.ViInsertedBytes == vi0, "vi 버퍼 0바이트(삽입 " + (r.Board.ViInsertedBytes - vi0) + "), 보드 수신 0바이트(" + (Received(r) - rec0) + ")");
            string log = r.LogText();
            ctx.Check(log.Contains("!!! [claude ") && log.Contains("dropped (fullscreen)") && log.Contains("!!! [tx ") , ".log 에 dropped(fullscreen) 기록(claude·tx)");
            r.Human("\x1b"); Thread.Sleep(200);
            r.Human(":q!\r");
            r.WaitState(PromptState.AtPrompt, 3000, "사람 :q!");
            r.IdleWait();
            ctx.Check(J.Seq(J.A(r.Call("run", r.P, "echo after5"), "out"), "after5"), "vi 종료 뒤 run 정상");
        }

        // ================================================================== 6
        [Scenario("6", "send/토큰 줄: ESC 뒤 150ms 간격(ESC+명령), BOOTING 중 {NONL}{ESC} 송신 → U-Boot(AT_UBOOT), AT_UBOOT 에서 {ENTER}·send 허용·run 106, 커널 뒤 BOOTING 에서 {ENTER} → 프롬프트", Order = 450, TimeoutS = 120)]
        public static void S6(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIM6", UbootWindowMs = 3000 }, "SIMCAB06", "COM106", 17106);
            // ---- ESC 간격: ESC 뒤 150ms 를 두지 않으면 busybox read_key 가 ESC+'e' 를 시퀀스로 삼켜 'cho' 가 실행된다
            long ring0 = r.Ring.End;
            int tx0 = r.Tx.Count;
            var g = r.Call("send", r.P, "{ESC}echo gap6");
            Ok(ctx, g, "send '{ESC}echo gap6'");
            var sent = r.Tx.Skip(tx0).Where(x => x.Source == TxSource.Claude).ToList();
            ctx.Check(sent.Count >= 2 && sent[0].Data.Length == 1 && sent[0].Data[0] == 0x1b && sent[1].MonoMs - sent[0].MonoMs >= r.Cfg.EscGapMs - 2,
                      "ESC 단독 송신 뒤 " + (sent.Count >= 2 ? sent[1].MonoMs - sent[0].MonoMs : -1) + "ms 뒤 다음 바이트(≥" + r.Cfg.EscGapMs + "ms)");
            ctx.WaitUntil(() => r.RingText(ring0).Contains("\r\ngap6\r\n"), 3000, "ESC 는 단독 키로 처리되고 'echo gap6' 이 실행됨");
            ctx.Check(!r.RingText(ring0).Contains("cho: not found"), "'cho: not found' 없음(ESC 가 'e' 를 삼키지 않음)");
            r.WaitState(PromptState.AtPrompt, 3000, "gap6 뒤");
            r.IdleWait();

            // ---- reboot → BOOTING 중 {NONL}{ESC} (README:51 U-Boot 진입 용법)
            var reb = r.Call("reboot", r.P);
            Ok(ctx, reb, "reboot");
            string R = J.S(reb, "rx_offset");
            var sendStates = new List<string>();
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 8000 && r.T.State != PromptState.AtUboot)
            {
                if (r.T.State == PromptState.Booting)
                {
                    var s = r.Call("send", r.P, "{NONL}{ESC}", "--timeout", "1");
                    if (J.S(s, "status") == "ok") sendStates.Add(J.S(s, "state"));
                    Thread.Sleep(250);                                  // T_open 이 지나 'evb: ' 가 확정될 때까지 다음 키를 보내지 않는다
                }
                else Thread.Sleep(20);
            }
            ctx.Check(sendStates.Contains("BOOTING"), "BOOTING 중 {NONL}{ESC} 송신 ok (" + string.Join(",", sendStates) + ")");
            r.WaitState(PromptState.AtUboot, 3000, "ESC 로 autoboot 중단 → 'evb: '");
            ctx.Check(r.RingText().Contains("evb: "), "보드 U-Boot 프롬프트 'evb: '");
            Code(ctx, r.Call("run", r.P, "echo x"), 106, "rejected", "AT_UBOOT 에서 run");
            var en = r.Call("send", r.P, "{ENTER}");
            ctx.Check(J.S(en, "status") == "ok" && J.S(en, "state") == "AT_UBOOT", "AT_UBOOT 에서 토큰 단독 줄 {ENTER} 송신");
            r.WaitState(PromptState.AtUboot, 3000, "{ENTER} 뒤 'evb: ' 재확정");
            long ringP = r.Ring.End;
            Ok(ctx, r.Call("send", r.P, "printenv"), "AT_UBOOT 에서 send printenv");
            ctx.WaitUntil(() => r.RingText(ringP).Contains("bootcmd=") && r.T.State == PromptState.AtUboot, 3000, "printenv 출력 + 'evb: ' 재확정");
            Ok(ctx, r.Call("send", r.P, "boot"), "send boot");
            var w = r.Call("wait", r.P, "--since", R, "--pattern", @"^\[\s+0\.0\d+\]", "--timeout", "5");
            Ok(ctx, w, "wait --since <reboot rx_offset> 커널 타임스탬프");
            ctx.WaitUntil(() => r.Board.Phase == "postboot" && r.T.Snapshot().KernelPhase, 5000, "커널 뒤 BOOTING(프롬프트 없음)");
            var e2 = r.Call("send", r.P, "{ENTER}", "--timeout", "3");
            ctx.Check(J.S(e2, "status") == "ok" && J.S(e2, "state") == "BOOTING", "커널 뒤 BOOTING 에서 {ENTER} 송신");
            r.WaitState(PromptState.AtPrompt, 3000, "{ENTER} → Linux 프롬프트");
        }

        // ================================================================== 7
        [Scenario("7", "--nudge: LDR>T~커널 타임스탬프 구간(무음 >2초여도) 0바이트, 커널 뒤 RX 무음 2초 뒤에만 CR → 프롬프트 매치", Order = 460, TimeoutS = 120)]
        public static void S7(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIM7", RtosToKernelMs = 2600 }, "SIMCAB07", "COM107", 17107, c => c.QuietMs = 2000);
            var reb = r.Call("reboot", r.P);
            string R = J.S(reb, "rx_offset");
            RingBuffer.TryParse(R, out _, out long rAbs);
            var w = r.Call("wait", r.P, "--since", R, "--pattern", "^root@SIM7:", "--nudge", "--timeout", "25");
            Ok(ctx, w, "wait --nudge");
            var o = J.A(w, "out");
            ctx.Check(o.Any(l => l.Contains("reboot")) && o.Any(l => l.Contains("LDR>T")) && o.Any(l => Regex.IsMatch(l, @"^\[\s+0\.0")) && o.Last().StartsWith("root@SIM7:"),
                      "반환 구간에 reboot 에코·LDR>T·커널 타임스탬프·프롬프트");
            string text = r.RingText(rAbs);
            long baseAbs = Math.Max(rAbs, r.Ring.Start);
            int iNdd = text.IndexOf("LDR>T", StringComparison.Ordinal), iRt = text.IndexOf("> ", iNdd, StringComparison.Ordinal);
            int iK = Regex.Match(text, @"\[\s+0\.0\d+\]").Index;
            r.Ring.TryGetArrival(baseAbs + iRt, out long tRt, out _);
            r.Ring.TryGetArrival(baseAbs + iK, out long tK, out _);
            ctx.Check(tK - tRt >= 2000, "전제: RTOS '> ' 뒤 커널까지 RX 무음 " + (tK - tRt) + "ms ≥ quiet 2000ms");
            var after = r.Tx.Where(x => x.Source != TxSource.Human && x.RxPos >= rAbs && Encoding.ASCII.GetString(x.Data) != "reboot\r").ToList();
            var probes = after.Where(x => x.Source == TxSource.Probe).ToList();
            string pseq = string.Join(" ", probes.Select(x => TnClient.Hex(x.Data)));
            ctx.Check(after.Count == probes.Count && probes.Count >= 3 && probes.Count % 3 == 0 && Regex.IsMatch(pseq, "^(05 15 0D)( 05 15 0D)*$"),
                      "reboot 뒤 Claude 송신은 nudge(^E → ^U → CR) 뿐 (" + pseq + ")");
            ctx.Check(probes.All(p => p.MonoMs > tK && p.RxPos > baseAbs + iK), "LDR>T~커널 구간 0바이트(첫 nudge 는 커널 줄 뒤 " + (probes[0].MonoMs - tK) + "ms)");
            r.Ring.TryGetArrival(probes[0].RxPos - 1, out long tLast, out _);
            ctx.Check(probes[0].MonoMs - tLast >= 2000 - 5, "첫 nudge(^E) 직전 RX 무음 " + (probes[0].MonoMs - tLast) + "ms ≥ 2000ms");
            ctx.Check(J.N(w, "nudges") >= 1, "응답 nudges=" + J.N(w, "nudges"));
            ctx.Check(r.LogText().Contains(">>> [claude-probe]"), ".log 에 '>>> [claude-probe]'");
        }

        // ================================================================== 9
        [Scenario("9", "요청 수명: 제어 연결 끊김(소켓 close·uart.ps1 프로세스 kill) → 대기 요청 미전송, deadline 지난 요청 미전송, 같은 --req-id 재요청 → 이전 결과(재실행 없음)", Order = 470, TimeoutS = 150)]
        public static void S9(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIM9" }, "SIMCAB09", "COM109", 17109);
            // (a) 소켓 close
            r.Human("sleep 2\r");
            r.WaitState(PromptState.Busy, 2000, "사람 sleep 2");
            long rec0 = Received(r), h0 = r.HumanBytes;
            using (var c = new CtlClient(r.Ctl))
            {
                ctx.Check(c.Hello(r.Token), "hello");
                c.Request("run", r.P, "echo killed9", "--timeout", "10");
                ctx.WaitUntil(() => r.Hub.Arbiter.Pending(r.Cab) == 1, 3000, "요청이 큐에서 대기");
            }
            ctx.WaitUntil(() => r.Hub.Arbiter.Pending(r.Cab) == 0, 3000, "연결 끊김 → 요청 취소");
            r.WaitState(PromptState.AtPrompt, 4000, "sleep 끝");
            Thread.Sleep(800);
            ctx.Check(Received(r) - rec0 == r.HumanBytes - h0 && !r.RingText().Contains("killed9"), "취소된 요청은 보드 0바이트(sleep 끝난 뒤에도)");
            ctx.Check(File.ReadAllText(r.Hub.Log.Path).Contains("미전송"), "hub.log 에 취소(미전송) 기록");

            // (b) uart.ps1 프로세스 kill
            r.Human("sleep 3\r");
            r.WaitState(PromptState.Busy, 2000, "사람 sleep 3");
            rec0 = Received(r); h0 = r.HumanBytes;
            var p = r.StartUart(null, "run", r.P, "echo killed9b", "--timeout", "20");
            ctx.WaitUntil(() => r.Hub.Arbiter.Pending(r.Cab) == 1, 10000, "uart.ps1 요청이 큐에서 대기");
            p.Kill(true);
            p.WaitForExit(5000);
            ctx.WaitUntil(() => r.Hub.Arbiter.Pending(r.Cab) == 0, 3000, "프로세스 kill → 요청 취소");
            r.WaitState(PromptState.AtPrompt, 5000, "sleep 끝");
            Thread.Sleep(800);
            ctx.Check(Received(r) - rec0 == r.HumanBytes - h0 && !r.RingText().Contains("killed9b"), "kill 된 클라이언트의 요청은 보드 0바이트");

            // (c) deadline
            r.Human("sleep 2\r");
            r.WaitState(PromptState.Busy, 2000, "사람 sleep 2");
            rec0 = Received(r); h0 = r.HumanBytes;
            var sw = Stopwatch.StartNew();
            var d = r.Call("run", r.P, "echo late9", "--timeout", "0.5");
            ctx.Check(J.N(d, "exit") == 101 && J.S(d, "blocked_reason") == "busy_since" && sw.ElapsedMilliseconds < 1500, "deadline 0.5초 → 101 busy_since (" + sw.ElapsedMilliseconds + "ms)");
            r.WaitState(PromptState.AtPrompt, 4000, "sleep 끝");
            Thread.Sleep(800);
            ctx.Check(Received(r) - rec0 == r.HumanBytes - h0 && !r.RingText().Contains("late9"), "deadline 지난 요청은 AT_PROMPT 가 돼도 미전송");
            ctx.Check(r.LogText().Contains("dropped (busy_since)"), ".log 에 dropped(busy_since)");

            // (d) --req-id 멱등
            r.IdleWait();
            long b0 = Count(r.RingText(), "__UH_B_");
            var x1 = r.Call("run", r.P, "echo once9", "--req-id", "rid9");
            var x2 = r.Call("run", r.P, "echo once9", "--req-id", "rid9");
            ctx.Check(J.Seq(J.A(x1, "out"), "once9") && J.S(x1, "req_id") == "rid9", "첫 요청 실행");
            ctx.Check(J.B(x2, "replayed") == true && J.Seq(J.A(x2, "out"), "once9") && J.S(x2, "run_id") == J.S(x1, "run_id") && J.S(x2, "raw_offset") == J.S(x1, "raw_offset"),
                      "같은 req-id → 이전 결과(run_id·raw_offset 동일, replayed)");
            // 실행 중 같은 req-id 가 오면 그 실행을 기다려 같은 결과
            JsonElement y1 = default;
            var th = new Thread(() => y1 = r.Call("run", r.P, "sleep 0.6; echo twice9", "--req-id", "rid9b"));
            th.Start();
            Thread.Sleep(250);
            var y2 = r.Call("run", r.P, "sleep 0.6; echo twice9", "--req-id", "rid9b");
            th.Join(10000);
            ctx.Check(J.Seq(J.A(y1, "out"), "twice9") && J.Seq(J.A(y2, "out"), "twice9") && J.S(y1, "run_id") == J.S(y2, "run_id"), "진행 중 같은 req-id → 같은 실행의 결과");
            string ring = r.RingText();
            ctx.Check(Count(ring, "__UH_B_") - b0 == 2 && Count(ring, "\r\nonce9\r\n") == 1 && Count(ring, "\r\ntwice9\r\n") == 1, "보드에서는 각 req-id 가 1번만 실행됨");
        }

        // ================================================================== 16
        [Scenario("16", "wait: --since 소급 매치, timeout 100, since_expired(epoch·링 밖), reboot → wait --since 에 reboot 에코·LDR>T·커널 타임스탬프·프롬프트 포함", Order = 480, TimeoutS = 120)]
        public static void S16(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIM16" }, "SIMCAB16", "COM116", 17116);
            var m = r.Call("run", r.P, "echo mark16");
            string R0 = J.S(m, "rx_offset");
            var w = r.Call("wait", r.P, "--since", R0, "--pattern", "^mark16$", "--timeout", "2");
            ctx.Check(J.S(w, "status") == "ok" && J.S(w, "match") == "mark16", "이미 지나간 출력을 --since 로 소급 매치");
            var sw = Stopwatch.StartNew();
            var t = r.Call("wait", r.P, "--pattern", "never16", "--timeout", "0.5");
            Code(ctx, t, 100, "timeout", "나오지 않는 패턴");
            ctx.Check(sw.ElapsedMilliseconds >= 450, "timeout 은 0.5초 뒤(" + sw.ElapsedMilliseconds + "ms)");
            var u = r.Uart(null, "wait", r.P, "--pattern", "never16", "--timeout", "0.3");
            ctx.Check(u.Code == 100 && u.Err.StartsWith("uart: E100 timeout", StringComparison.Ordinal), "uart.ps1 wait timeout → 100 + stderr 'uart: E100'");
            Code(ctx, r.Call("wait", r.P, "--since", "1:0", "--pattern", "x", "--timeout", "0.3"), 106, "since_expired", "다른 epoch 의 --since");
            // 링(시험 설정 1MB)을 넘겨 since 0 을 덮어쓴다
            long before = r.Ring.End;
            var big = string.Concat(Enumerable.Repeat(new string('z', 126) + "\r\n", 9000));
            r.Board.Emit(big);
            ctx.WaitUntil(() => r.Ring.End >= before + big.Length, 5000, "1.1MB 출력 수신");
            ctx.Check(r.Ring.Start > 0, "링 시작이 0 을 지남(" + r.Ring.Start + ")");
            Code(ctx, r.Call("wait", r.P, "--since", r.Ring.Format(0), "--pattern", "x", "--timeout", "0.3"), 106, "since_expired", "링 밖의 --since");
            Code(ctx, r.Call("wait", r.P, "--since", r.Ring.Format(r.Ring.End + 1000), "--pattern", "x", "--timeout", "0.3"), 106, "rejected", "미래 위치 --since");
            r.WaitState(PromptState.AtPrompt, 3000, "대량 출력 뒤(직전 프롬프트 확정 유지)");
            r.IdleWait();

            var reb = r.Call("reboot", r.P);
            Ok(ctx, reb, "reboot (송신 직전 rx_offset 반환)");
            Thread.Sleep(300);
            var wb = r.Call("wait", r.P, "--since", J.S(reb, "rx_offset"), "--pattern", "^root@SIM16:", "--nudge", "--timeout", "20");
            Ok(ctx, wb, "reboot → wait --since --nudge");
            var o = J.A(wb, "out");
            ctx.Check(o[0] == "reboot" && o.Any(l => l.Contains("LDR>T")) && o.Any(l => Regex.IsMatch(l, @"^\[\s+0\.0")) && o.Last().StartsWith("root@SIM16:"),
                      "반환 구간: reboot 에코(첫 줄)·LDR>T·커널 타임스탬프·프롬프트 (실보드는 'Booting Linux' 를 찍지 않음 — 부록 A S0-5)");
            ctx.Check(o.Any(l => l.Contains("Restarting system")), "reboot: Restarting system 포함");
        }

        // ================================================================== 19
        [Scenario("19", "같은 보드명 2대 → 105 + 후보 목록(port·com·alias), 0바이트. 포트·COM·alias 로는 해석됨, 없는 이름은 102", Order = 490, TimeoutS = 90)]
        public static void S19(ScenarioContext ctx)
        {
            var cfg = HubRig.MakeConfig(ctx, 17119, c => c.Cables["SIMCAB19A"] = new CableConfig { Alias = "twin-a" });
            var b1 = new FakeBoard(new FakeBoardOptions { Hostname = "TWIN" });
            var b2 = new FakeBoard(new FakeBoardOptions { Hostname = "TWIN" });
            ctx.Defer(b1.PowerOff); ctx.Defer(b2.PowerOff);
            var prov = new FakePortProvider();
            prov.Plug(b1, "SIMCAB19A", "COM119");
            prov.Plug(b2, "SIMCAB19B", "COM120");
            var r = HubRig.Boot(ctx, cfg, b1, prov, "SIMCAB19A", true);
            Cable cb = null;
            ctx.WaitUntil(() => { cb = r.Hub.Cables.Find("SIMCAB19B"); return cb != null && cb.IsAttached && r.Hub.Tracker(cb)?.Generation > 0; }, 5000, "두 번째 케이블");
            cb.Write(new[] { (byte)'\r' }, TxSource.Human);
            ctx.WaitUntil(() => r.Cab.Board == "TWIN" && cb.Board == "TWIN", 3000, "두 케이블 모두 보드명 TWIN");
            long r1 = b1.TotalReceived, r2 = b2.TotalReceived;
            var a = r.Call("status", "TWIN");
            Code(ctx, a, 105, "ambiguous", "status TWIN");
            var cand = a.GetProperty("candidates").EnumerateArray().ToList();
            ctx.Check(cand.Count == 2 && cand.Any(x => J.S(x, "com") == "COM119" && J.S(x, "alias") == "twin-a") && cand.Any(x => J.S(x, "com") == "COM120" && J.N(x, "port") == cb.TcpPort),
                      "후보 2개(port·com·alias·board)");
            Code(ctx, r.Call("run", "TWIN", "echo x"), 105, "ambiguous", "run TWIN");
            ctx.Check(b1.TotalReceived == r1 && b2.TotalReceived == r2, "105 → 두 보드 모두 0바이트");
            ctx.Check(J.S(r.Call("status", "twin-a"), "cable") == "SIMCAB19A", "alias twin-a → A");
            ctx.Check(J.S(r.Call("status", "com120"), "cable") == "SIMCAB19B", "COM120(대소문자 무시) → B");
            ctx.Check(J.S(r.Call("status", cb.TcpPort.ToString(CultureInfo.InvariantCulture)), "cable") == "SIMCAB19B", "포트 번호 → B");
            Code(ctx, r.Call("status", "NOPE19"), 102, "no_cable", "없는 이름");
            var ls = r.Call("ls");
            ctx.Check(ls.GetProperty("cables").GetArrayLength() == 2, "ls 케이블 2개");
            var u = r.Uart(null, "run", "TWIN", "echo x");
            ctx.Check(u.Code == 105 && u.Err.StartsWith("uart: E105 ambiguous", StringComparison.Ordinal) && u.Err.Contains("COM119") && u.Err.Contains("COM120"),
                      "uart.ps1 비JSON: 105 + stderr 'uart: E105' + 후보");
        }

        // ================================================================== 21
        /// <summary>
        /// 허브가 아닌 상대(같은 포트를 쓰는 다른 프로그램, 실보드 시험의 wizmchecksvc) 흉내: 접속마다 script 를 돌리고 받은 바이트를 모두 기록한다.
        /// script 가 끝나면 EOF 까지 계속 받는다(클라이언트가 그 뒤에 무엇을 보냈는지도 남는다).
        /// </summary>
        sealed class FakeCtlServer : IDisposable
        {
            readonly System.Net.Sockets.TcpListener l;
            readonly Thread th;
            readonly MemoryStream got = new MemoryStream();
            volatile bool stop;
            int done;

            public FakeCtlServer(int port, Action<Stream, Func<string>> script)
            {
                l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
                l.Start();
                th = new Thread(() =>
                {
                    while (!stop)
                    {
                        System.Net.Sockets.TcpClient c;
                        try { c = l.AcceptTcpClient(); } catch (Exception) { return; }
                        using (c)
                        {
                            c.ReceiveTimeout = 8000;
                            var ns = c.GetStream();
                            string ReadLine()
                            {
                                var b = new List<byte>();
                                while (true)
                                {
                                    int x;
                                    try { x = ns.ReadByte(); } catch (Exception) { return null; }
                                    if (x < 0) return null;
                                    lock (got) got.WriteByte((byte)x);
                                    if (x == '\n') return Encoding.UTF8.GetString(b.ToArray()).TrimEnd('\r');
                                    b.Add((byte)x);
                                }
                            }
                            try { script(ns, ReadLine); } catch (Exception) { }
                            var buf = new byte[4096];
                            try { int n; while ((n = ns.Read(buf, 0, buf.Length)) > 0) lock (got) got.Write(buf, 0, n); } catch (Exception) { }
                        }
                        Interlocked.Increment(ref done);
                    }
                }) { IsBackground = true, Name = "FakeCtl" };
                th.Start();
            }

            /// <summary>끝난 접속 수(클라이언트가 닫을 때까지 받은 뒤 1 증가).</summary>
            public int Done => Volatile.Read(ref done);
            public string Got() { lock (got) return Encoding.UTF8.GetString(got.ToArray()); }
            public static void W(Stream s, string t) { var b = Encoding.UTF8.GetBytes(t); s.Write(b, 0, b.Length); s.Flush(); }
            public void Dispose() { stop = true; try { l.Stop(); } catch (Exception) { } th.Join(3000); }
        }

        [Scenario("21", "제어 포트 인사(§3.6): 허브 인사 {uart_hub,nonce} 뒤 hello=HMAC(token,c|nonce)+cnonce, proof=HMAC(token,s|cnonce). 틀린 hello·토큰 원문·반대 방향 접두·이전 nonce 재사용·cnonce 없음·HTTP 첫 줄·hello 없는 요청 → 응답 없이 끊김. 허브가 아닌 상대(무응답·HTTP·JSON 비인사·잘못된 proof·비JSON 응답) → uart.ps1 103, 토큰 원문 미전송, 예외 없음", Order = 500, TimeoutS = 150)]
        public static void S21(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIM21" }, "SIMCAB21", "COM121", 17121, toPrompt: false);
            void Closed(Func<string, string> send, string what)
            {
                using var c = new CtlClient(r.Ctl);
                string n = c.Greeting();
                var sw = Stopwatch.StartNew();
                c.SendRaw(send(n));
                ctx.Check(c.ClosedByServer(3000) && sw.ElapsedMilliseconds < 1500, what + " → 응답 없이 끊김(" + sw.ElapsedMilliseconds + "ms)");
            }
            string Hello(string mac, string cnonce) => "{\"hello\":" + Json.Str(mac) + (cnonce == null ? "" : ",\"cnonce\":" + Json.Str(cnonce)) + "}\n";
            string cn0 = CtlClient.NewNonce();
            using (var c = new CtlClient(r.Ctl))
            {
                string n = c.Greeting();
                ctx.Check(Regex.IsMatch(n, "^[0-9a-f]{32}$"), "접속 즉시 허브 인사 {\"uart_hub\":1,\"nonce\":<32자 hex>} (" + n + ")");
                using var c2 = new CtlClient(r.Ctl);
                ctx.Check(c2.Greeting() != n, "접속마다 nonce 가 다름");
            }
            Closed(n => Hello("wrong-token-0000000000", cn0), "틀린 hello");
            Closed(n => Hello(r.Token, cn0), "토큰 원문 hello(이전 형식)");
            Closed(n => Hello(CtlClient.Mac(r.Token, "s|" + n), cn0), "반대 방향 접두 HMAC(token, s|nonce) — 반사 불가");
            Closed(n => Hello(CtlClient.Mac(r.Token, n), cn0), "접두 없는 HMAC(token, nonce)");
            Closed(n => Hello(CtlClient.Mac(r.Token, "c|" + n), null), "cnonce 없음");
            Closed(n => Hello(CtlClient.Mac(r.Token, "c|" + n), "xyz"), "cnonce 형식 아님");
            string old;
            using (var c1 = new CtlClient(r.Ctl)) old = c1.Greeting();
            Closed(n => Hello(CtlClient.Mac(r.Token, "c|" + old), cn0), "다른 연결의 nonce 로 만든 hello(재사용)");
            Closed(n => "GET / HTTP/1.1\r\nHost: 127.0.0.1:" + r.Ctl + "\r\n\r\n", "HTTP GET");
            Closed(n => "POST /run HTTP/1.1\r\nContent-Type: application/json\r\nContent-Length: 15\r\n\r\n{\"argv\":[\"ls\"]}", "HTTP POST(본문 JSON)");
            Closed(n => "OPTIONS * HTTP/1.1", "개행 없는 HTTP OPTIONS(줄 끝을 기다리지 않음)");
            Closed(n => "{\"argv\":[\"ls\"]}\n", "hello 없이 요청");
            using (var c = new CtlClient(r.Ctl))
            {
                string n = c.Greeting();
                string cn = CtlClient.NewNonce();
                c.SendRaw(Hello(CtlClient.Mac(r.Token, "c|" + n), cn));
                using var h = JsonDocument.Parse(c.ReadLine(5000) ?? "{}");
                ctx.Check(J.S(h.RootElement, "hello") == "ok" && J.S(h.RootElement, "proof") == CtlClient.Mac(r.Token, "s|" + cn),
                          "올바른 hello → hello ok + proof = HMAC(token, s|cnonce) (" + J.Show(h.RootElement) + ")");
                ctx.Check(!J.Show(h.RootElement).Contains(r.Token), "hello 응답에 토큰 원문 없음");
                c.Request("ls");
                var ls = c.Response(5000);
                ctx.Check(J.S(ls, "status") == "ok" && ls.GetProperty("cables").GetArrayLength() == 1, "hello 뒤 ls");
                c.Request("hub", "status");
                var hs = c.Response(5000);
                ctx.Check(J.S(hs, "status") == "ok" && J.N(hs, "control_port") == r.Ctl, "같은 연결에서 hub status");
                c.Request("hub", "stop");
                Code(ctx, c.Response(5000), 106, "unsupported", "hub stop(감독 연결 전 골격)");
            }
            string bad = Path.Combine(ctx.Dir, "badhub");
            Directory.CreateDirectory(bad);
            File.WriteAllText(Path.Combine(bad, "token"), "ffffffffffffffffffffffffffffffff");
            var u = r.Uart(new Dictionary<string, string> { ["UART_HUB_DIR"] = bad }, "ls");
            ctx.Check(u.Code == 103 && u.Err.StartsWith("uart: E103 hub_down", StringComparison.Ordinal), "uart.ps1 토큰 불일치 → 103 + stderr 'uart: E103'");
            u = r.Uart(new Dictionary<string, string> { ["UART_HUB_DIR"] = bad }, "ls", "--json");
            using (var doc = JsonDocument.Parse(u.Out))
                ctx.Check(u.Code == 103 && J.S(doc.RootElement, "status") == "hub_down" && J.N(doc.RootElement, "exit") == 103, "--json 모드 도구 오류는 JSON 한 줄(status hub_down, exit 103)");
            ctx.Check(r.Board.TotalReceived == r.HumanBytes, "거부된 연결로는 보드 0바이트");

            // ---- 허브가 아닌 상대(포트를 다른 프로그램이 씀): uart.ps1 은 인사를 확인한 뒤에만 보내고, 아니면 103 + 원인 안내. 예외 문구 없음
            int fport = 17180;                                     // 가짜 상대마다 새 포트(17180~)
            void Foreign(string what, Action<Stream, Func<string>> script, string expectErr, bool nothingSent)
            {
                int port = fport++;
                var fenv = new Dictionary<string, string> { ["UART_HUB_CONTROL_PORT"] = port.ToString(CultureInfo.InvariantCulture) };
                using var f = new FakeCtlServer(port, script);
                var sw = Stopwatch.StartNew();
                var x = r.Uart(fenv, "ls");
                ctx.WaitUntil(() => f.Done >= 1, 10000, what + ": 가짜 서버 접속 종료");
                string got = f.Got();
                ctx.Check(x.Code == 103 && x.Err.StartsWith("uart: E103 hub_down", StringComparison.Ordinal) && x.Err.Contains(expectErr),
                          what + " → 103 + '" + expectErr + "' (" + sw.ElapsedMilliseconds + "ms, err=" + x.Err.Trim() + ")");
                ctx.Check(!x.Err.Contains("Exception") && !x.Err.Contains("ConvertFrom-Json") && !x.Err.Contains("CategoryInfo") && x.Out.Length == 0, what + ": PowerShell 예외 문구 없음, stdout 비어 있음");
                ctx.Check(!got.Contains(r.Token), what + ": 상대가 받은 바이트에 토큰 원문 없음(" + got.Length + "B)");
                if (nothingSent) ctx.Check(got.Length == 0, what + ": 허브 인사가 아니므로 아무것도 보내지 않음");
                var j = r.Uart(fenv, "ls", "--json");
                ctx.WaitUntil(() => f.Done >= 2, 10000, what + ": (--json) 가짜 서버 접속 종료");
                JsonElement je = default;
                bool parsed = false;
                try { using var d = JsonDocument.Parse(j.Out); je = d.RootElement.Clone(); parsed = true; } catch (JsonException) { }
                ctx.Check(j.Code == 103 && parsed && J.S(je, "status") == "hub_down" && (J.S(je, "error") ?? "").Contains(expectErr),
                          what + " --json → JSON 한 줄 status hub_down (" + j.Out.Trim() + ")");
            }
            const string notHub = "의 상대가 허브가 아님";
            Foreign("무응답 상대(인사 없음, 2초)", (s, rl) => Thread.Sleep(4000), notHub, true);
            Foreign("HTTP 응답을 보내는 상대", (s, rl) => FakeCtlServer.W(s, "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\n\r\n"), notHub, true);
            Foreign("인사가 아닌 JSON 줄", (s, rl) => FakeCtlServer.W(s, "{\"hello\":\"ok\",\"nonce\":\"" + new string('a', 32) + "\"}\n"), notHub, true);
            Foreign("이진 쓰레기", (s, rl) => FakeCtlServer.W(s, "\u0001\u0002\u00ff\u00fe garbage\n"), notHub, true);
            string fn = CtlClient.NewNonce();
            string helloLine = null;
            Foreign("인사는 같지만 proof 가 틀린 상대", (s, rl) =>
            {
                FakeCtlServer.W(s, "{\"uart_hub\":1,\"nonce\":\"" + fn + "\"}\n");
                helloLine = rl();
                FakeCtlServer.W(s, "{\"hello\":\"ok\",\"proof\":\"" + new string('0', 64) + "\"}\n");
            }, "proof", false);
            ctx.Check(helloLine != null && helloLine.Contains("\"hello\":\"" + CtlClient.Mac(r.Token, "c|" + fn) + "\"") && helloLine.Contains("\"cnonce\":\""),
                      "가짜 상대가 받은 hello = HMAC(token, c|nonce) + cnonce (" + helloLine + ")");
            // proof 를 맞출 수 있는(토큰을 아는) 상대가 비JSON 응답을 주면: PowerShell 예외 없이 103(결함 2)
            foreach (var reply in new[] { "this is not json", "[1,2,3]", "{\"status\":\"ok\"}" })
            {
                Foreign("proof 는 맞고 응답이 '" + reply + "'", (s, rl) =>
                {
                    FakeCtlServer.W(s, "{\"uart_hub\":1,\"nonce\":\"" + fn + "\"}\n");
                    string hl = rl();
                    using var d = JsonDocument.Parse(hl);
                    FakeCtlServer.W(s, "{\"hello\":\"ok\",\"proof\":\"" + CtlClient.Mac(r.Token, "s|" + J.S(d.RootElement, "cnonce")) + "\"}\n");
                    rl();
                    FakeCtlServer.W(s, reply + "\n");
                }, "허브 응답을 해석할 수 없음", false);
            }
        }

        // ================================================================== control-hello
        [Scenario("control-hello", "제어 포트 hello 방어: hello 전 연결은 동시에 16개까지(17번째는 인사 없이 끊김), 인사→hello 절대 기한 5초(바이트를 0.7초마다 흘려도 연장 안 됨), hub.log 에 사유별 한 줄(토큰 없음), 그 뒤 정상 hello", Order = 502, TimeoutS = 90)]
        public static void ControlHello(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIMCH" }, "SIMCABCH", "COM175", 17161, toPrompt: false);
            bool CallOk()
            {
                try { return J.S(r.Call("ls"), "status") == "ok"; }
                catch (ScenarioFailure) { return false; }
            }
            // ---- 1. hello 전 연결 상한
            var idle = new List<CtlClient>();
            try
            {
                for (int i = 0; i < 16; i++) { var c = new CtlClient(r.Ctl); idle.Add(c); c.Greeting(); }
                ctx.Log("ok: hello 전 연결 16개 — 모두 인사 받음");
                using (var c17 = new CtlClient(r.Ctl))
                {
                    var sw = Stopwatch.StartNew();
                    string l = c17.ReadLine(3000);
                    ctx.Check(l == null && sw.ElapsedMilliseconds < 1500, "17번째 hello 전 연결 → 인사 없이 바로 끊김(" + sw.ElapsedMilliseconds + "ms, " + (l ?? "EOF") + ")");
                }
            }
            finally { foreach (var c in idle) c.Dispose(); }
            ctx.WaitUntil(CallOk, 5000, "hello 전 연결이 끊긴 뒤 정상 hello·ls");

            // ---- 2. 절대 기한: '{' 뒤 0.7초마다 한 바이트(읽기마다의 시간 제한 5초로는 끝없이 연장되던 경우)
            using (var c = new CtlClient(r.Ctl))
            {
                c.Greeting();
                var sw = Stopwatch.StartNew();
                var drip = new Thread(() =>
                {
                    try { c.SendRaw("{"); while (sw.ElapsedMilliseconds < 12000) { Thread.Sleep(700); c.SendRaw("\""); } }
                    catch (Exception) { }
                }) { IsBackground = true, Name = "drip" };
                drip.Start();
                bool closed = c.ClosedByServer(12000);
                long ms = sw.ElapsedMilliseconds;
                ctx.Check(closed && ms >= 4000 && ms < 7500, "0.7초마다 흘려도 인사 뒤 5초 기한에 응답 없이 끊김(" + ms + "ms)");
                drip.Join(3000);
            }
            ctx.Check(CallOk(), "기한 초과 연결 뒤에도 정상 hello·ls");
            string hl = LogFiles.ReadShared(r.Hub.Log.Path);
            ctx.Check(hl.Contains("control hello 실패") && hl.Contains("16개 초과") && hl.Contains("hello 기한 5000ms 초과"), "hub.log: 상한 초과·기한 초과 사유 기록");
            ctx.Check(!hl.Contains(r.Token), "hub.log 에 토큰 없음");
            ctx.Check(r.Board.TotalReceived == 0, "보드 0바이트");
        }

        // ================================================================== uart-cli-port
        [Scenario("uart-cli-port", "uart.ps1 제어 포트 결정: UART_HUB_CONTROL_PORT 없으면 살아 있는 코어의 core.pid(시작 시각 일치) → config.json(// /* */ 주석 속 control_port 무시, 문자열 속 // 는 그대로) → 7200. 값 오류(문자·범위 밖·Int32 넘침) 106, config.json·token 을 읽지 못하면 103 — PowerShell 예외 없음", Order = 504, TimeoutS = 120)]
        public static void UartCliPort(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIMUP" }, "SIMCABUP", "COM176", 17162, toPrompt: false);
            string hubDir = r.Cfg.EffectiveHubDir;
            string cfgPath = Path.Combine(hubDir, "config.json"), pidPath = Path.Combine(hubDir, "core.pid"), tokenPath = Path.Combine(hubDir, "token");
            ctx.Defer(() => { try { File.Delete(cfgPath); File.Delete(pidPath); } catch (Exception) { } });
            var noEnv = new Dictionary<string, string> { ["UART_HUB_CONTROL_PORT"] = null };
            void Ok((int Code, string Out, string Err) u, string what)
            {
                bool ok = false;
                try { using var d = JsonDocument.Parse(u.Out); ok = u.Code == 0 && J.S(d.RootElement, "status") == "ok"; } catch (JsonException) { }
                ctx.Check(ok, what + " → ls ok (rc=" + u.Code + " " + u.Err.Trim() + ")");
            }
            void Fail((int Code, string Out, string Err) u, int code, string text, string what)
                => ctx.Check(u.Code == code && u.Err.StartsWith("uart: E" + code, StringComparison.Ordinal) && u.Err.Contains(text) && !u.Err.Contains("Exception") && !u.Err.Contains("CategoryInfo"),
                             what + " → " + code + " '" + text + "' (rc=" + u.Code + " err=" + u.Err.Trim() + ")");

            // ---- 1. jsonc: 주석 속 control_port 는 무시, 문자열 속 // 는 주석 아님
            File.WriteAllText(cfgPath, "// \"control_port\": 17997,\n/* \"control_port\": 17998,\n */\n{ \"log_root\": \"C:\\\\x // not a comment\", \"control_port\": "
                                       + r.Ctl + " /* 끝 주석 */ }\n");
            Ok(r.Uart(noEnv, "ls", "--json"), "config.json 의 주석 속 control_port(17997·17998)를 건너뛰고 실제 값 " + r.Ctl);

            // ---- 2. core.pid(살아 있는 코어) 가 config.json 보다 먼저 — 감독이 config.last-good.json 으로 띄운 경우
            File.WriteAllText(cfgPath, "{ \"control_port\": 17996 }");
            using (var me = Process.GetCurrentProcess())
            {
                long ticks = me.StartTime.ToUniversalTime().Ticks;
                File.WriteAllText(pidPath, "{\"pid\":" + me.Id + ",\"start_ticks\":" + ticks + ",\"control_port\":" + r.Ctl + ",\"control\":\"listening\"}");
                Ok(r.Uart(noEnv, "ls", "--json"), "core.pid(살아 있는 pid·시작 시각 일치)의 control_port " + r.Ctl + " 가 config.json(17996)보다 먼저");
                File.WriteAllText(pidPath, "{\"pid\":" + me.Id + ",\"start_ticks\":" + (ticks - 1) + ",\"control_port\":" + r.Ctl + "}");
            }
            Fail(r.Uart(noEnv, "ls"), 103, "127.0.0.1:17996", "낡은 core.pid(시작 시각 다름)는 무시 → config.json 의 17996(아무도 안 들음)");
            File.Delete(pidPath);

            // ---- 3. 값 오류 → 106
            Fail(r.Uart(new Dictionary<string, string> { ["UART_HUB_CONTROL_PORT"] = "abc" }, "ls"), 106, "UART_HUB_CONTROL_PORT 값이 잘못됨", "UART_HUB_CONTROL_PORT=abc");
            Fail(r.Uart(new Dictionary<string, string> { ["UART_HUB_CONTROL_PORT"] = "70000" }, "ls"), 106, "UART_HUB_CONTROL_PORT 값이 잘못됨", "UART_HUB_CONTROL_PORT=70000(범위 밖)");
            File.WriteAllText(cfgPath, "{ \"control_port\": 99999999999 }");
            Fail(r.Uart(noEnv, "ls"), 106, "config.json 의 control_port 값이 잘못됨", "config.json control_port 99999999999(Int32 넘침)");
            var j = r.Uart(noEnv, "ls", "--json");
            using (var d = JsonDocument.Parse(j.Out))
                ctx.Check(j.Code == 106 && J.S(d.RootElement, "status") == "rejected", "--json 이면 JSON 한 줄 status rejected (" + j.Out.Trim() + ")");

            // ---- 4. 읽기 실패 → 103
            File.WriteAllText(cfgPath, "{ \"control_port\": " + r.Ctl + " }");
            using (new FileStream(cfgPath, FileMode.Open, FileAccess.Read, FileShare.None))
                Fail(r.Uart(noEnv, "ls"), 103, "config.json 을 읽지 못함", "config.json 이 잠김");
            using (new FileStream(tokenPath, FileMode.Open, FileAccess.Read, FileShare.None))
                Fail(r.Uart(null, "ls"), 103, "토큰 파일을 읽지 못함", "token 이 잠김");
            Ok(r.Uart(noEnv, "ls", "--json"), "잠금 해제 뒤 config.json 으로 정상");
            ctx.Check(r.Board.TotalReceived == 0, "보드 0바이트");
        }

        // ================================================================== 23
        [Scenario("23", "개행 없는 출력: printf abc / echo -n x → out=[\"abc\"]·trailing_newline:false·rc 0, 이후 AT_PROMPT", Order = 510, TimeoutS = 60)]
        public static void S23(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIM23" }, "SIMCAB23", "COM123", 17123);
            foreach (var (cmd, outs, nl) in new[] { ("printf abc", new[] { "abc" }, false), ("echo -n x", new[] { "x" }, false),
                                                     ("printf 'a\\nb'", new[] { "a", "b" }, false), ("echo y", new[] { "y" }, true) })
            {
                var a = r.Call("run", r.P, cmd);
                ctx.Check(J.S(a, "status") == "ok" && J.N(a, "rc") == 0 && J.Seq(J.A(a, "out"), outs) && J.B(a, "trailing_newline") == nl,
                          "'" + cmd + "' → out=" + string.Join("|", outs) + " trailing_newline=" + nl + " (" + J.Show(a) + ")");
                r.WaitState(PromptState.AtPrompt, 3000, "'" + cmd + "' 뒤");
            }
            ctx.Check(r.RingText().Contains("abc__UH_E_"), "원시 RX: E 마커가 'abc' 바로 뒤(줄 시작 아님)에 붙음");
        }

        // ================================================================== 29
        [Scenario("29", "코어 재시작: 프롬프트 보드 → .raw 재생으로 AT_PROMPT 복원·run 성공(probe 0), 공백>60초(복원 불가) → probe 1회 후 run 성공, vi 화면에서 재시작 → probe 0바이트", Order = 520, TimeoutS = 120)]
        public static void S29(ScenarioContext ctx)
        {
            const string cab = "SIMCAB29";
            var r1 = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIM29" }, cab, "COM129", 17129);
            var board = r1.Board;
            var cfg = r1.Cfg;
            ctx.Check(J.Seq(J.A(r1.Call("run", r1.P, "echo r1"), "out"), "r1"), "코어 1: run");
            r1.WaitState(PromptState.AtPrompt, 3000, "코어 1 프롬프트");
            Thread.Sleep(150);
            int port = r1.Cab.TcpPort;
            r1.Hub.Stop();

            HubRig Restart(string com, bool oldAlive)
            {
                if (oldAlive)
                    File.WriteAllText(Path.Combine(cfg.EffectiveHubDir, "alive", port.ToString(CultureInfo.InvariantCulture)),
                                      DateTime.Now.AddSeconds(-120).ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture));
                var prov = new FakePortProvider();
                prov.Plug(board, cab, com);
                return HubRig.Boot(ctx, cfg, board, prov, cab, false);
            }

            // ---- 공백 ≤60초: 전체 복원
            var r2 = Restart("COM130", false);
            var s2 = r2.T.Snapshot();
            ctx.Check(s2.RestoredFrom == RestoreSource.CoreRestart && s2.State == PromptState.AtPrompt, "코어 2: .raw 재생으로 AT_PROMPT 복원 (" + s2.StateName + "), 같은 제어 포트 재bind");
            long rec = board.TotalReceived;
            var a = r2.Call("run", r2.P, "echo r2");
            ctx.Check(J.Seq(J.A(a, "out"), "r2"), "코어 2: 복원 상태에서 바로 run 성공");
            ctx.Check(r2.Tx.All(x => x.Source == TxSource.Claude) && board.TotalReceived - rec == r2.ClaudeBytes && !r2.Tx.Any(x => x.Source == TxSource.Probe), "probe 0회, 보드 수신 = run 한 줄");
            r2.WaitState(PromptState.AtPrompt, 3000, "코어 2 프롬프트");
            Thread.Sleep(150);
            r2.Hub.Stop();

            // ---- 공백 >60초: 부정 상태만 복원 → UNKNOWN → probe 1회 → run
            var r3 = Restart("COM131", true);
            ctx.Check(r3.T.State == PromptState.Unknown && !r3.T.Snapshot().ProbeNeedsUserOk, "코어 3(공백 120초): UNKNOWN, probe 는 user-ok 불필요");
            var b = r3.Call("run", r3.P, "echo r3", "--timeout", "0.8");
            ctx.Check(J.N(b, "exit") == 101 && J.S(b, "blocked_reason") == "unknown", "UNKNOWN 에서 run → 101 unknown");
            Thread.Sleep(cfg.QuietMs + 100);
            var pr = r3.Call("probe", r3.P);
            ctx.Check(J.S(pr, "status") == "ok" && J.B(pr, "prompt") == true && J.S(pr, "state") == "AT_PROMPT", "probe → CR 1회 → 프롬프트 확정 AT_PROMPT (" + J.Show(pr) + ")");
            r3.IdleWait();
            ctx.Check(J.Seq(J.A(r3.Call("run", r3.P, "echo r3"), "out"), "r3"), "probe 뒤 run 성공");
            var pt = r3.Tx.Where(x => x.Source == TxSource.Probe).Select(x => TnClient.Hex(x.Data)).ToList();
            ctx.Check(pt.Count == 3 && pt[0] == "05" && pt[1] == "15" && pt[2] == "0D", "probe 정확히 1회(송신 ^E → ^U → CR: " + string.Join(" | ", pt) + ")");

            // ---- vi 화면에서 재시작 → FULLSCREEN 유지, probe 0바이트
            r3.Human("vi /tmp/v29.txt\r");
            r3.WaitState(PromptState.Fullscreen, 3000, "vi");
            Thread.Sleep(200);
            r3.Hub.Stop();
            var r4 = Restart("COM132", true);
            ctx.Check(r4.T.State == PromptState.Fullscreen, "코어 4(vi 화면, 공백 120초): FULLSCREEN(부정 상태) 복원 (" + r4.T.Snapshot().StateName + ")");
            Thread.Sleep(cfg.QuietMs + 100);
            rec = board.TotalReceived;
            int vi0 = board.ViInsertedBytes;
            var p1 = r4.Call("probe", r4.P);
            var p2 = r4.Call("probe", r4.P, "--user-ok");
            ctx.Check(J.N(p1, "exit") == 101 && J.N(p2, "exit") == 101, "probe·probe --user-ok → 101 (" + J.S(p1, "blocked_reason") + "/" + J.S(p2, "blocked_reason") + ")");
            ctx.Check(J.N(r4.Call("run", r4.P, "echo x", "--timeout", "0.5"), "exit") == 101, "run → 101");
            ctx.Check(board.TotalReceived == rec && board.ViInsertedBytes == vi0, "vi 에 0바이트");
            r4.Human("\x1b"); Thread.Sleep(200); r4.Human(":q!\r");
            r4.WaitState(PromptState.AtPrompt, 3000, "사람이 vi 종료");
        }

        // ================================================================== 30
        [Scenario("30", "stranded(거짓 AT_PROMPT 주입, B 없음) → Ctrl-C 0바이트·잠금 유지·late start 기록, interrupted(run 중 사람 ^C), timeout(B 있고 E 없음 → ^C 1회 cleanup / --no-interrupt)", Order = 530, TimeoutS = 150)]
        public static void S30(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIM30" }, "SIMCAB30", "COM130", 17130);
            int CtrlC() => r.Tx.Count(x => x.Source != TxSource.Human && x.Data.Contains((byte)0x03));

            // ---- stranded: 사람 명령 실행 중에 프롬프트 모양 텍스트(열린 줄) → 거짓 AT_PROMPT
            r.Human("sleep 4\r");
            r.WaitState(PromptState.Busy, 2000, "사람 sleep 4");
            Thread.Sleep(150);
            r.Board.Emit("root@SIM30:/$ ");
            r.WaitState(PromptState.AtPrompt, 2000, "거짓 프롬프트 확정(주입)");
            r.IdleWait();
            int c0 = CtrlC();
            var sw = Stopwatch.StartNew();
            var s = r.Call("run", r.P, "echo s30", "--timeout", "2", "--req-id", "st30");
            ctx.Check(J.S(s, "status") == "stranded" && J.N(s, "exit") == 100 && J.IsNull(s, "rc") && J.B(s, "released") == false && sw.ElapsedMilliseconds >= 1900,
                      "deadline 까지 B 없음 → stranded 확정, exit 100 (" + sw.ElapsedMilliseconds + "ms, " + J.Show(s) + ")");
            ctx.Check(CtrlC() == c0, "stranded: Ctrl-C 0바이트");
            var st = r.Call("status", r.P);
            ctx.Check(J.B(st, "run_active") == true && J.S(st, "blocked_reason") == "serialized", "직렬화 잠금 유지(serialized)");
            Code(ctx, r.Call("run", r.P, "echo blocked30", "--timeout", "0.5"), 101, "busy", "잠금 중 다음 run");
            ctx.WaitUntil(() => r.LogText().Contains("*** late start st30 ***"), 6000, "sleep 끝 → tty 에 남은 줄이 실행 → '*** late start st30 ***'");
            r.WaitState(PromptState.AtPrompt, 3000, "늦은 E + 프롬프트 → 잠금 해제");
            ctx.Check(r.RingText().Contains("__UH_E_" + J.S(s, "run_id") + "_0") && r.RingText().Contains("\r\ns30\r\n"), "늦게 실행된 run 의 출력·E 마커가 RX 에 있음(자동 재시도 없음: 1회)");
            ctx.Check(Count(r.RingText(), "\r\ns30\r\n") == 1, "s30 실행 1회");
            r.IdleWait();
            ctx.Check(J.Seq(J.A(r.Call("run", r.P, "echo after30"), "out"), "after30"), "해제 뒤 run 정상");

            // ---- stranded 뒤 사람 ^C: tty 에 남은 줄은 버려지고(canonical ^C), 프롬프트 확정으로 잠금 해제(late start 없음)
            r.Human("sleep 4\r");
            r.WaitState(PromptState.Busy, 2000, "사람 sleep 4");
            Thread.Sleep(150);
            r.Board.Emit("root@SIM30:/$ ");
            r.WaitState(PromptState.AtPrompt, 2000, "거짓 프롬프트 확정(주입)");
            r.IdleWait();
            var s2 = r.Call("run", r.P, "echo s30b", "--timeout", "1.6", "--req-id", "st30b");
            ctx.Check(J.S(s2, "status") == "stranded" && CtrlC() == c0, "두 번째 stranded, Ctrl-C 0바이트");
            r.Human("\x03");
            r.WaitState(PromptState.AtPrompt, 3000, "사람 ^C 뒤 프롬프트 확정 → 잠금 해제");
            Thread.Sleep(300);
            ctx.Check(!r.LogText().Contains("late start st30b") && !r.RingText().Contains("\r\ns30b\r\n"), "버려진 줄은 실행되지 않음(late start 없음)");
            ctx.Check(File.ReadAllText(r.Hub.Log.Path).Contains("직렬화 잠금 해제 st30b"), "hub.log 에 잠금 해제 기록");
            r.IdleWait();

            // ---- interrupted: run 중 사람 ^C
            JsonElement it = default;
            var th = new Thread(() => it = r.Call("run", r.P, "sleep 3", "--timeout", "10"));
            long ring0 = r.Ring.End;
            th.Start();
            ctx.WaitUntil(() => r.RingText(ring0).Contains("__UH_B_"), 3000, "run B 마커");
            Thread.Sleep(200);
            int c1 = CtrlC();
            r.Human("\x03");
            th.Join(10000);
            ctx.Check(J.S(it, "status") == "interrupted" && J.N(it, "exit") == 101 && J.IsNull(it, "rc"), "B 뒤 E 없이 프롬프트 확정 → interrupted (" + J.Show(it) + ")");
            ctx.Check(CtrlC() == c1, "interrupted: Claude ^C 0회");
            r.WaitState(PromptState.AtPrompt, 3000, "^C 뒤 프롬프트");
            r.IdleWait();

            // ---- timeout: B 있고 E 없음, 사람 입력 없음 → ^C 1회 + 프롬프트 복귀 확인
            var t = r.Call("run", r.P, "sleep 5", "--timeout", "1");
            ctx.Check(J.S(t, "status") == "timeout" && J.N(t, "exit") == 100 && J.S(t, "cleanup") == "ctrl_c", "timeout → cleanup ctrl_c (" + J.Show(t) + ")");
            ctx.Check(CtrlC() == c1 + 1, "Claude ^C 정확히 1회");
            r.WaitState(PromptState.AtPrompt, 3000, "^C 정리 뒤 프롬프트");
            r.IdleWait();
            ctx.Check(J.Seq(J.A(r.Call("run", r.P, "echo ok30"), "out"), "ok30"), "정리 뒤 run 정상");
            var n = r.Call("run", r.P, "sleep 2; echo late30", "--timeout", "0.7", "--no-interrupt");
            ctx.Check(J.S(n, "status") == "timeout" && J.S(n, "cleanup") == "disabled" && CtrlC() == c1 + 1, "--no-interrupt: timeout, ^C 없음");
            ctx.Check(J.S(r.Call("status", r.P), "blocked_reason") == "serialized", "E 전까지 잠금 유지");
            r.WaitState(PromptState.AtPrompt, 4000, "늦은 E + 프롬프트 → 해제");
            ctx.Check(r.RingText().Contains("\r\nlate30\r\n"), "명령은 끝까지 실행됨");
        }

        // ================================================================== 31
        [Scenario("31", "토큰 줄({ENTER}·{ESC}·{NONL}x)과 send 가 vi(FULLSCREEN)·cat >(BUSY)에 0바이트, .tx 토큰 줄 진입점(Gate.Token, Source.Spool)도 101", Order = 540, TimeoutS = 90)]
        public static void S31(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIM31" }, "SIMCAB31", "COM131", 17131);
            var raws = new[] { "{ENTER}", "{ESC}", "{NONL}x", "hello31" };
            void AllBlocked(string reason, string where)
            {
                long rec = Received(r), h = r.HumanBytes;
                foreach (var raw in raws)
                {
                    var a = r.Call("send", r.P, raw, "--timeout", "0.6");
                    ctx.Check(J.N(a, "exit") == 101 && J.S(a, "blocked_reason") == reason, where + ": send '" + raw + "' → 101 " + reason);
                }
                var sp = r.Hub.Arbiter.Submit(r.Cab, new TxRequest { Kind = TxKind.Send, Gate = TxGate.Token, Source = TxSource.Spool, Cmd = "{NONL}{ESC}", TimeoutS = 0.6 }, out _);
                ctx.Check(sp.Wait(5000) && sp.Exit == 101, where + ": .tx 토큰 줄 진입점 → 101");
                ctx.Check(Received(r) - rec == r.HumanBytes - h, where + ": 보드 수신 == 사람 송신(Claude 0바이트)");
            }
            r.Human("vi /tmp/v31.txt\r");
            r.WaitState(PromptState.Fullscreen, 3000, "vi");
            ctx.WaitUntil(() => r.Board.Phase == "vi", 2000, "보드 vi 모드");
            Thread.Sleep(200);
            int vi0 = r.Board.ViInsertedBytes;
            AllBlocked("fullscreen", "vi");
            ctx.Check(r.Board.ViInsertedBytes == vi0, "vi 버퍼 0바이트");
            r.Human("\x1b"); Thread.Sleep(200); r.Human(":q!\r");
            r.WaitState(PromptState.AtPrompt, 3000, "vi 종료");

            r.Human("cat > /tmp/c31.txt\r");
            r.WaitState(PromptState.Busy, 2000, "cat > (stdin 대기)");
            Thread.Sleep(200);
            AllBlocked("busy_since", "cat >");
            long ringD = r.Ring.End;
            r.Human("\x04");
            ctx.WaitUntil(() => r.RingText(ringD).Contains("root@SIM31:"), 3000, "^D 로 cat 종료 → 셸 프롬프트");
            ctx.Check(r.T.State == PromptState.HumanTyping, "^D 는 HUMAN_TYPING 해제 키가 아님(§3.2: CR·^C·^U 만) → 사람 Enter 까지 Claude 대기");
            r.Human("\r");
            r.WaitState(PromptState.AtPrompt, 3000, "사람 Enter");
            var f = r.Board.FsRead("/tmp/c31.txt");
            ctx.Check(f != null && f.Length == 0, "cat > 파일에 Claude 바이트 0 (" + (f?.Length ?? -1) + "B)");
        }

        // ================================================================== 32
        [Scenario("32", "직렬화: run 2개 동시 요청 → 첫 run 의 E 마커와 프롬프트 확정 뒤에 둘째 송신, out[] 섞임 없음", Order = 550, TimeoutS = 60)]
        public static void S32(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIM32" }, "SIMCAB32", "COM132", 17132);
            JsonElement ra = default, rb = default;
            var ta = new Thread(() => ra = r.Call("run", r.P, "sleep 1; echo one", "--timeout", "10"));
            var tb = new Thread(() => rb = r.Call("run", r.P, "echo two", "--timeout", "10"));
            ta.Start();
            Thread.Sleep(100);
            tb.Start();
            ta.Join(15000); tb.Join(15000);
            ctx.Check(J.Seq(J.A(ra, "out"), "one") && J.Seq(J.A(rb, "out"), "two"), "out 섞임 없음: A=[one], B=[two]");
            string idA = J.S(ra, "run_id"), idB = J.S(rb, "run_id");
            var sends = r.Tx.Where(x => x.Source == TxSource.Claude && x.Data.Length > 20).ToList();
            var txA = sends.Single(x => Encoding.ASCII.GetString(x.Data).Contains(" " + idA + ";"));
            var txB = sends.Single(x => Encoding.ASCII.GetString(x.Data).Contains(" " + idB + ";"));
            string text = r.RingText();
            long eA = r.Ring.Start + text.IndexOf("__UH_E_" + idA + "_0", StringComparison.Ordinal);
            var pA = r.T.Candidates().First(c => c.By == "e-marker" && c.Start > eA);
            ctx.Check(txA.MonoMs < txB.MonoMs && txB.RxPos >= pA.End, "둘째 송신(링 위치 " + txB.RxPos + ")은 첫 run 의 E(" + eA + ")·확정 프롬프트(" + pA.End + ") 뒤");
            ctx.Check(txB.MonoMs >= pA.DecidedMs, "둘째 송신 시각 ≥ 첫 run 뒤 프롬프트 확정 시각");
            long bB = r.Ring.Start + text.IndexOf("__UH_B_" + idB, StringComparison.Ordinal);
            ctx.Check(bB > eA, "B 마커 순서: A 의 E 뒤에 B 의 B");

            // 앞 run 이 길어도 뒤 요청의 응답은 제 deadline 에 나간다(101 serialized), 그리고 보내지 않는다
            r.WaitState(PromptState.AtPrompt, 3000, "A·B 뒤");
            r.IdleWait();
            JsonElement rc = default;
            var tc = new Thread(() => rc = r.Call("run", r.P, "sleep 2; echo three", "--timeout", "10"));
            tc.Start();
            Thread.Sleep(200);
            long rec0 = Received(r);
            var sw = Stopwatch.StartNew();
            var d = r.Call("run", r.P, "echo four", "--timeout", "0.5");
            long ms = sw.ElapsedMilliseconds;
            ctx.Check(J.N(d, "exit") == 101 && J.S(d, "blocked_reason") == "serialized" && ms < 1200, "실행 중 run 뒤의 0.5초 요청 → " + ms + "ms 에 101 serialized");
            tc.Join(15000);
            ctx.Check(J.Seq(J.A(rc, "out"), "three") && Received(r) == rec0 && !r.RingText().Contains("\r\nfour\r\n"), "앞 run 정상 완료, 만료된 요청은 미전송");
        }
    }
}

namespace UartHub.Testing
{
    /// <summary>
    /// 송신 스레드가 port.Write 를 마친 뒤 오래 멈추는 상황(스레드 선점)을 결정적으로 만드는 포트 래퍼.
    /// 보드는 Write 안에서 이미 입력을 받았으므로 응답(에코·프롬프트)은 이 지연 동안 링·추적기로 흘러간다.
    /// </summary>
    internal sealed class SlowWritePort : IPort
    {
        readonly FakeBoard b;
        readonly int delayMs;
        public SlowWritePort(FakeBoard b, int delayMs) { this.b = b; this.delayMs = delayMs; }
        public string Name => b.Name;
        public bool IsOpen => b.IsOpen;
        public void Open() => b.Open();
        public int Read(byte[] buffer, int offset, int count) => b.Read(buffer, offset, count);
        public void Write(byte[] buffer, int offset, int count) { b.Write(buffer, offset, count); Thread.Sleep(delayMs); }
        public void SendBreak(int ms) => b.SendBreak(ms);
        public void Close() => b.Close();
        public void Dispose() => b.Dispose();
    }

    internal sealed class SlowWriteProvider : IPortProvider
    {
        readonly FakeBoard b;
        readonly string cableId, com;
        readonly int delayMs;
        public SlowWriteProvider(FakeBoard b, string cableId, string com, int delayMs) { this.b = b; this.cableId = cableId; this.com = com; this.delayMs = delayMs; b.SetComName(com); }
        public IReadOnlyList<PortInfo> Scan() => new[] { new PortInfo { CableId = cableId, Com = com, Description = "SlowWrite " + b.Hostname, Provider = this } };
        public IPort Create(PortInfo info, int baud, Config cfg) => new SlowWritePort(b, delayMs);
    }

    internal static class TxOrderScenario
    {
        // ================================================================== tx-order (시뮬 5 간헐 실패의 결정적 재현)
        [Scenario("tx-order", "TX 관측 순서: port.Write 뒤 송신 스레드가 120ms 멈춰도(보드 응답·프롬프트 확정이 먼저 처리됨) 추적기는 TX 를 응답 앞에 둔다 → Enter·vi :q!·연속 run 뒤 AT_PROMPT", Order = 560, TimeoutS = 90)]
        public static void TxOrder(ScenarioContext ctx)
        {
            var cfg = HubRig.MakeConfig(ctx, 17133);
            var b = new FakeBoard(new FakeBoardOptions { Hostname = "SIMTX" });
            ctx.Defer(b.PowerOff);
            var r = HubRig.Boot(ctx, cfg, b, new SlowWriteProvider(b, "SIMCABTX", "COM133", 120), "SIMCABTX", false);
            ctx.Check(r.T.TOpenMs < 120, "전제: 지연 120ms > T_open " + r.T.TOpenMs + "ms(지연 동안 프롬프트가 열린 줄로 확정될 수 있음)");
            r.Human("\r");
            r.WaitState(PromptState.AtPrompt, 3000, "Enter → 프롬프트(송신 스레드 지연 중 확정)");
            r.Human("echo hi\r");
            r.WaitState(PromptState.AtPrompt, 3000, "echo hi → 프롬프트");
            r.Human("vi /tmp/tx.txt\r");
            r.WaitState(PromptState.Fullscreen, 3000, "vi");
            Thread.Sleep(200);
            r.Human("\x1b");
            Thread.Sleep(200);
            r.Human(":q!\r");
            r.WaitState(PromptState.AtPrompt, 3000, "한 번에 보낸 ':q!\\r' 뒤 vi 종료 프롬프트");
            r.IdleWait();
            for (int i = 0; i < 3; i++)
            {
                var a = r.Call("run", r.P, "echo tx" + i, "--timeout", "5");
                ctx.Check(J.Seq(J.A(a, "out"), "tx" + i), "연속 run " + i + " 정상(직전 run 의 TX 관측이 프롬프트 뒤로 밀리지 않음)");
            }
            r.WaitState(PromptState.AtPrompt, 3000, "run 뒤");
        }
    }
}
