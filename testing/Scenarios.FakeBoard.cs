// fakeboard-selftest: FakeBoard 가 실보드처럼 동작하는지 허브 없이 직접 검증한다.
// 기대값은 실측 바이트열(부록 A, s0_COM3_20261028_171441.raw)과 busybox 동작에서 손으로 적은 것이다.
using System;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace UartHub.Testing
{
    internal static class FakeBoardScenarios
    {
        const string P = "root@SIM:/$ ";

        static string Q(string s) => Regex.Escape(s);

        /// <summary>명령을 보내고 다음 프롬프트(출력 끝)까지 받은 텍스트(에코 포함).</summary>
        static string Cmd(BoardConsole c, string cmd, string prompt = P, int timeoutMs = 3000)
        {
            int m = c.Mark();
            c.Send(cmd + "\r");
            return c.WaitFor(Q(prompt) + "$", m, timeoutMs);
        }

        static string Md5Hex(byte[] d)
        {
            using var md5 = MD5.Create();
            return string.Concat(md5.ComputeHash(d).Select(x => x.ToString("x2")));
        }

        [Scenario("fakeboard-selftest", "FakeBoard 자체 검증(echo/printf/sh -c/exit respawn/vi/top/reboot/uudecode/printk/80열/stdin 대기)", Order = 0, TimeoutS = 120)]
        public static void SelfTest(ScenarioContext ctx)
        {
            var b = new FakeBoard(new FakeBoardOptions { Hostname = "SIM", SleepScale = 0.3 });
            ctx.Defer(b.PowerOff);
            var c = new BoardConsole(b);
            ctx.Defer(c.Dispose);
            long sentByUs = 0;
            void Send(string s) { sentByUs += Encoding.UTF8.GetByteCount(s); c.Send(s); }
            string Run(string cmd, string prompt = P) { sentByUs += Encoding.UTF8.GetByteCount(cmd) + 1; return Cmd(c, cmd, prompt); }

            // ---- 연결 직후: 프롬프트는 이미 찍혀 있었던 상태. Enter → 새 프롬프트
            int m0 = c.Mark();
            Send("\r");
            ctx.Check(c.WaitFor(Q(P) + "$", m0) == "\r\n" + P, "Idle 보드에 CR → \\r\\n + 프롬프트");

            // ---- echo / printf / $?
            ctx.Check(Run("echo hello") == "echo hello\r\nhello\r\n" + P, "echo: 에코 + 출력(ONLCR) + 프롬프트 바이트 일치");
            ctx.Check(Run("printf 'a%sb\\n' X Y").Contains("aXb\r\naYb\r\n"), "printf 형식 재사용(인자 2개)");
            ctx.Check(Run("printf abc") == "printf abc\r\nabc" + P, "printf abc: 개행 없는 출력 뒤에 프롬프트가 바로 붙음");
            ctx.Check(Run("false; echo rc=$?").Contains("\r\nrc=1\r\n"), "false → $?=1");

            // ---- sh -c 하위 셸: exit 는 하위만
            ctx.Check(Run("sh -c 'echo in; exit 3'; echo rc=$?").Contains("\r\nin\r\nrc=3\r\n"), "sh -c 'exit 3' → 하위 셸만 종료, rc=3");
            string seal = Run("printf '__U''H_B_%s\\n' 7; sh -c 'ls # x' </dev/null; printf '__U''H_E_%s_%s\\n' 7 $?");
            ctx.Check(seal.Contains("\r\n__UH_B_7\r\n") && seal.Contains("\r\n__UH_E_7_0\r\n"), "봉인 형식: B/E 마커와 rc 회수(# 주석 포함)");
            ctx.Check(seal.Split(new[] { "\r\n" }, StringSplitOptions.None)[0].IndexOf("__UH_", StringComparison.Ordinal) < 0, "에코된 명령 줄에는 __UH_ 가 연속으로 나타나지 않음");
            ctx.Check(Run("sh -c 'echo \"x' </dev/null; echo rc=$?").Contains("sh: syntax error: unterminated quoted string\r\nrc=2\r\n"), "sh -c 안 따옴표 불균형 → 오류, rc=2");
            ctx.Check(Run("sh -c 'cat' </dev/null; echo rc=$?").Contains("\r\nrc=0\r\n"), "</dev/null 이면 cat 이 즉시 EOF");
            var swBg = Stopwatch.StartNew();
            ctx.Check(Run("sh -c 'sleep 1 &' </dev/null; echo bg=$?").Contains("\r\nbg=0\r\n") && swBg.ElapsedMilliseconds < 250, "sleep 1 & 는 기다리지 않음");

            // ---- 대화형 따옴표 불균형 → PS2
            int m1 = c.Mark();
            Send("echo \"a\r");
            c.WaitFor(Q("\r\n> ") + "$", m1);
            ctx.Log("ok: 대화형 따옴표 불균형 → PS2 '> '");
            Send("b\"\r");
            c.WaitFor(Q("a\r\nb\r\n" + P) + "$", m1);
            ctx.Log("ok: PS2 이어 입력 → 실행");

            // ---- eval: 현재 셸에서 실행(cd 유지)
            ctx.Check(Run("eval 'cd /tmp'; pwd", "root@SIM:/tmp$ ").Contains("\r\n/tmp\r\n"), "eval 'cd /tmp' 가 현재 셸에 남고 프롬프트 \\w 반영");
            Run("cd /");

            // ---- applet 집합: base64 없음, uudecode/md5sum 있음
            ctx.Check(Run("base64").Contains("\r\n-sh: base64: not found\r\n"), "base64 → not found");
            ctx.Check(Run("echo $?").Contains("\r\n127\r\n"), "not found → 127");
            ctx.Check(Run("command -v uudecode md5sum base64; echo rc=$?").Contains("\r\n/usr/bin/uudecode\r\n/usr/bin/md5sum\r\nrc=1\r\n"), "command -v: uudecode·md5sum 있음, base64 없음");
            ctx.Check(Run("printf 'begin-base64 644 -\\naGk=\\n====\\n' | uudecode -o /tmp/uh && cat /tmp/uh").EndsWith("\r\nhi" + P), "S0-4 명령: uudecode begin-base64 → 'hi'");
            ctx.Check(Encoding.ASCII.GetString(b.GetFile("/tmp/uh") ?? new byte[0]) == "hi", "uudecode 결과 파일 바이트 == 'hi'");
            ctx.Check(Run("md5sum /tmp/uh").Contains("\r\n" + Md5Hex(Encoding.ASCII.GetBytes("hi")) + "  /tmp/uh\r\n"), "md5sum 형식·값");

            // ---- push 와 같은 청크 경로(700자 청크 → b64 파일 → uudecode)
            var rnd = new Random(1234);
            var payload = new byte[1500];
            rnd.NextBytes(payload);
            string b64 = Convert.ToBase64String(payload);
            for (int i = 0; i < b64.Length; i += 700)
                Run("printf '%s\\n' '" + b64.Substring(i, Math.Min(700, b64.Length - i)) + "' >> /tmp/.uh_T.b64");
            string pushOut = Run("{ echo 'begin-base64 644 -'; cat /tmp/.uh_T.b64; echo '===='; } | uudecode -o /tmp/p.bin && md5sum /tmp/p.bin; rm -f /tmp/.uh_T.b64");
            ctx.Check(pushOut.Contains(Md5Hex(payload) + "  /tmp/p.bin"), "청크 push 경로: md5 일치");
            ctx.Check((b.GetFile("/tmp/p.bin") ?? new byte[0]).SequenceEqual(payload) && b.GetFile("/tmp/.uh_T.b64") == null, "청크 push 경로: 파일 바이트 일치, 임시 파일 삭제");

            // ---- 80열 접힘: 프롬프트 12자 + 68자에서 \r\r\n
            string longArg = new string('A', 100);
            string wrapped = Run("echo " + longArg);
            ctx.Check(wrapped.StartsWith("echo " + new string('A', 63) + "\r\r\n" + new string('A', 37) + "\r\n" + longArg + "\r\n", StringComparison.Ordinal),
                      "80열에서 \\r\\r\\n 접힘(S0-4 실측 형태)");

            // ---- stdin 읽는 명령은 </dev/null 없으면 대기
            int m2 = c.Mark();
            Send("cat\r");
            c.WaitFor(Q("cat\r\n") + "$", m2);
            ctx.Check(c.StaysAbsent(Q(P), m2, 300), "cat(인자 없음)은 프롬프트로 돌아오지 않고 대기");
            Send("xyz\r");
            c.WaitFor(Q("xyz\r\nxyz\r\n") + "$", m2);
            ctx.Log("ok: 대기 중 입력은 tty 에코 + cat 출력");
            Send("\x04");
            c.WaitFor(Q(P) + "$", m2);
            ctx.Log("ok: ^D 로 cat 종료 → 프롬프트");
            int m3 = c.Mark();
            Send("cat\r");
            c.WaitFor(Q("cat\r\n") + "$", m3);
            Send("\x03");
            ctx.Check(c.WaitFor(Q(P) + "$", m3).EndsWith("^C\r\n" + P), "cat 대기 중 ^C → ^C + 프롬프트");
            ctx.Check(Run("echo $?").Contains("\r\n130\r\n"), "SIGINT 종료 → $?=130");

            // ---- ^C 는 남은 목록을 버린다
            int m4 = c.Mark();
            Send("sleep 5; echo after\r");
            Thread.Sleep(100);
            Send("\x03");
            string intr = c.WaitFor(Q(P) + "$", m4);
            ctx.Check(intr.EndsWith("^C\r\n" + P) && !intr.Contains("\r\nafter\r\n"), "sleep 중 ^C → 목록 중단(after 미출력)");

            // ---- type-ahead: 명령 실행 중 친 글자는 tty 가 에코하고, 새 프롬프트 뒤에 다시 에코된다
            int m5 = c.Mark();
            Send("sleep 1\r");
            Thread.Sleep(60);
            Send("ls");
            string ta = c.WaitFor(Q(P + "ls") + "$", m5, 3000);
            ctx.Check(ta.StartsWith("sleep 1\r\nls", StringComparison.Ordinal), "type-ahead: 실행 중 tty 에코 + 프롬프트 뒤 재에코");
            int m6 = c.Mark();
            Send("\x15");
            c.WaitFor(Q("\r" + P + "\x1b[K") + "$", m6);
            ctx.Log("ok: ^U → 줄 지움(\\r + 프롬프트 + ESC[K)");
            ctx.Check(Cmd(c, "") == "\r\n" + P, "^U 뒤 빈 줄 Enter → 새 프롬프트");
            sentByUs += 1;

            // ---- printk·앱 로그 혼입
            int m7 = c.Mark();
            b.EmitPrintk("hello kmsg", ansi: true);
            c.WaitFor(@"\x1b\[1;33m\[\s*\d+\.\d{6}\] hello kmsg\x1b\[0m\r\n$", m7);
            ctx.Check(c.Text(m7 - P.Length).StartsWith(P + "\x1b[1;33m[", StringComparison.Ordinal), "열린 프롬프트 줄에 ANSI printk 가 붙음");
            b.AppendAfterNextPrompt("[APPLOG] same-ms\r\n");
            int m8 = c.Mark();
            Send("\r");
            c.WaitFor(Q(P + "[APPLOG] same-ms\r\n") + "$", m8);
            ctx.Log("ok: 프롬프트와 앱 로그가 같은 덩어리로 나옴");
            ctx.Check(Run("echo '<3>UHTEST' > /dev/kmsg; echo '<6>quiet' > /dev/kmsg").Contains("] UHTEST\r\n") && !c.Text(m8).Contains("] quiet"),
                      "/dev/kmsg: <3> 만 콘솔 출력(console_loglevel 4)");
            ctx.Check(Run("dmesg | grep quiet").Contains("] quiet\r\n"), "dmesg 에는 <6> 도 남음");

            // ---- vi: 실측 진입 바이트열, :q 종료
            int m9 = c.Mark();
            Send("vi /tmp/v.txt\r");
            c.WaitFor(Q("\x1b[?1049h\x1b[999;999H\x1b[6n\x1b[H\x1b[J"), m9);
            ctx.Log("ok: vi 진입 ESC[?1049h ESC[999;999H ESC[6n ESC[H ESC[J");
            ctx.Check(b.WaitPhase("vi", 1000), "vi 상태");
            Send("\x1b[24;80R");                          // 터미널 CPR 응답 → 시퀀스로 소비
            Thread.Sleep(120);
            Send(":q\r");
            ctx.Check(c.WaitFor(Q("\x1b[?1049l" + P) + "$", m9).Length > 0 && b.ViInsertedBytes == 0, "CPR 응답 소비, :q → ESC[?1049l + 프롬프트, 버퍼 0바이트");

            // ---- vi: ESC 직후 같은 덩어리 키는 무시(S0-10), 간격을 두면 동작
            int m10 = c.Mark();
            Send("vi /tmp/v2.txt\r");
            c.WaitFor(Q("\x1b[H\x1b[J"), m10);
            Send("i");
            Thread.Sleep(80);
            Send("hello");
            ctx.WaitUntil(() => b.ViInsertedBytes == 5, 2000, "vi 삽입 5바이트");
            int m11 = c.Mark();
            Send("\x1b:q!\r");
            ctx.Check(c.StaysAbsent(Q("\x1b[?1049l"), m11, 300) && c.Text(m11).Contains("\a") && b.Phase == "vi", "ESC 와 같은 덩어리의 ':q!' 는 삼켜짐(bell), vi 유지");
            Send("\x1b");
            Thread.Sleep(200);
            Send(":q!\r");
            c.WaitFor(Q("\x1b[?1049l" + P) + "$", m11);
            ctx.Check(b.GetFile("/tmp/v2.txt") == null, "ESC 뒤 간격을 두면 :q! 로 종료, 저장 안 됨");

            // ---- top: CSI 화면, q 로 종료
            int m12 = c.Mark();
            Send("top\r");
            c.WaitFor(Q("\x1b[H\x1b[J") + ".*Mem:", m12);
            ctx.Check(b.WaitPhase("top", 1000), "top 상태(CSI H/J 화면)");
            Send("q");
            ctx.Check(c.WaitFor(Q("\r\n" + P) + "$", m12).Length > 0 && b.Phase == "shell", "top 에서 q → 프롬프트");

            // ---- exit → 로그인 셸 respawn(새 프롬프트, 상태 초기화)
            Run("cd /tmp", "root@SIM:/tmp$ ");
            int m13 = c.Mark();
            Send("exit 5\r");
            ctx.Check(c.WaitFor(Q(P) + "$", m13) == "exit 5\r\n" + P, "exit 5 → respawn: 새 프롬프트(cwd / 로 초기화)");
            ctx.Check(Run("echo $?").Contains("\r\n0\r\n"), "respawn 셸의 $?=0");

            // ---- reboot: 실측 순서, 부팅 뒤 프롬프트 자동 출력 없음, 호스트명 교체
            b.Options.NextHostname = "SIM2";
            int m14 = c.Mark();
            Send("reboot\r");
            string boot = c.WaitFor(@"\[START APP\][^\n]*\r\n", m14, 5000);
            ctx.Check(Regex.IsMatch(boot, @"Requesting system reboot\r\n\[\s*\d+\.\d{6}\] reboot: Restarting system\r\nLDR>T###%%> \[    0\.0\d{5}\] .*\[    0\.0\d{5}\] .*SIM2 Linux shell", RegexOptions.Singleline),
                      "reboot 순서: Restarting system → LDR>T###%%> → 커널 [    0.0x] → init");
            ctx.Check(c.StaysAbsent("root@SIM2", m14, 400), "부팅 뒤 프롬프트 자동 출력 없음(S0-9)");
            int m15 = c.Mark();
            Send("\r");
            c.WaitFor(Q("\r\nroot@SIM2:/$ ") + "$", m15);
            ctx.Check(b.BootCount == 1 && b.Hostname == "SIM2", "Enter 가 오면 새 호스트명 프롬프트");

            // ---- 카운터(무손실 검증용)
            ctx.WaitUntil(() => b.PendingBytes == 0 && c.ReadBytes == b.TotalSent, 2000, "보드 송신 바이트 == 콘솔 수신 바이트(TotalSent)");
            ctx.Check(b.TotalReceived == sentByUs, "보드 수신 바이트 == 콘솔 송신 바이트(" + sentByUs + ")");

            // ---- 탈착: 대기 중 Read 가 OperationCanceledException(S0-6)
            c.Dispose();
            b.Open();
            Exception readEx = null;
            var reader = new Thread(() => { try { var buf = new byte[16]; while (true) b.Read(buf, 0, 16); } catch (Exception e) { readEx = e; } });
            reader.Start();
            Thread.Sleep(50);
            b.Unplug();
            reader.Join(2000);
            ctx.Check(readEx is OperationCanceledException, "Unplug → Read 가 OperationCanceledException(" + readEx?.GetType().Name + ")");
            b.Replug();
            b.Open();
            b.Close();
            ctx.Log("ok: Replug 뒤 다시 Open 가능");

            // ---- U-Boot 창(설정 시) / 창에서 키 없음 → 그대로 커널
            var u = new FakeBoard(new FakeBoardOptions { Hostname = "UB", UbootWindowMs = 400 });
            ctx.Defer(u.PowerOff);
            var uc = new BoardConsole(u);
            ctx.Defer(uc.Dispose);
            int n0 = uc.Mark();
            uc.Send("reboot\r");
            uc.WaitFor("Hit any key to stop autoboot", n0, 3000);
            uc.Send("\x1b");
            uc.WaitFor(Q("evb: ") + "$", n0, 2000);
            int n1 = uc.Mark();
            uc.Send("printenv\r");
            uc.WaitFor("baudrate=115200\r\n.*" + Q("evb: ") + "$", n1);
            uc.Send("boot\r");
            uc.WaitFor(@"> \[    0\.0\d{5}\].*\[START APP\]", n1, 3000);
            ctx.Log("ok: U-Boot 창: 키 → evb: → printenv → boot → 커널");
            int n2 = uc.Mark();
            uc.Send("\r");
            uc.WaitFor(Q("root@UB:/$ ") + "$", n2);
            uc.Send("reboot\r");
            uc.WaitFor(@"Hit any key to stop autoboot.*> \[    0\.0\d{5}\].*\[START APP\]", n2, 3000);
            ctx.Check(!uc.Text(n2).Contains("evb: "), "U-Boot 창에서 키가 없으면 evb: 없이 커널로");

            // ---- 115200 근사 속도 제한(선택 기능)
            var pz = new FakeBoard(new FakeBoardOptions { Hostname = "PACE", PacingBytesPerSec = 11520 });
            ctx.Defer(pz.PowerOff);
            var pc = new BoardConsole(pz);
            ctx.Defer(pc.Dispose);
            var sw = Stopwatch.StartNew();
            pz.Emit(new string('p', 1152));
            ctx.WaitUntil(() => pc.ReadBytes >= 1152, 3000, "pacing 출력 수신");
            ctx.Check(sw.ElapsedMilliseconds >= 70, "11520B/s 에서 1152B 수신에 ≥70ms(" + sw.ElapsedMilliseconds + "ms)");
        }
    }
}
