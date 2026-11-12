// US-005 시나리오: TelnetServer (계획 §4 S6 표 10, 12, 22). 기대 바이트는 계획 §3.5·RFC 854/855 와 S0-3 실측(TeraTerm 응답)에서 손으로 정한다.
// 시험 클라이언트는 TcpClient 로 서버가 보낸 바이트를 그대로 모아 검사한다(telnet 해석 없음).
// 포트: 케이블 17210·17220·17230·17500·17520(base_port), 제어 17140~17142·17155·17156 — 기존 시나리오(17000~17002, 17101~17133)와 겹치지 않는다.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace UartHub.Testing
{
    /// <summary>시험용 raw TCP 클라이언트: 받은 바이트를 전부 모은다(읽지 않는 모드도 있다).</summary>
    internal sealed class TnClient : IDisposable
    {
        readonly TcpClient tc;
        readonly MemoryStream got = new MemoryStream();
        readonly Thread th;
        volatile bool closedByPeer, disposed;
        long count;

        TnClient(TcpClient tc, bool read)
        {
            this.tc = tc;
            if (!read) return;
            th = new Thread(() =>
            {
                var buf = new byte[65536];
                try
                {
                    while (true)
                    {
                        int n = tc.Client.Receive(buf);
                        if (n <= 0) break;
                        lock (got) { got.Write(buf, 0, n); count += n; }
                    }
                }
                catch (Exception) { }
                if (!disposed) closedByPeer = true;
            }) { IsBackground = true, Name = "TnClient" };
            th.Start();
        }

        /// <param name="read">false 면 읽지 않는다(느린 클라이언트). recvBuf 로 수신 버퍼를 줄인다.</param>
        public static TnClient Connect(int port, bool read = true, int recvBuf = 0)
        {
            var tc = new TcpClient(AddressFamily.InterNetwork);
            if (recvBuf > 0) tc.ReceiveBufferSize = recvBuf;
            tc.NoDelay = true;
            tc.Connect(IPAddress.Loopback, port);
            return new TnClient(tc, read);
        }

        public long Count { get { lock (got) return count; } }
        public bool ClosedByPeer => closedByPeer;
        public byte[] All() { lock (got) return got.ToArray(); }
        public byte[] Since(int mark) { var a = All(); return a.Skip(mark).ToArray(); }
        public void Send(params byte[] b) => tc.Client.Send(b);
        public void Send(string latin1) => Send(Encoding.Latin1.GetBytes(latin1));

        public bool WaitCount(long n, int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms) { if (Count >= n) return true; Thread.Sleep(5); }
            return Count >= n;
        }

        public bool WaitClosed(int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms) { if (closedByPeer) return true; Thread.Sleep(5); }
            return closedByPeer;
        }

        /// <summary>읽지 않던 클라이언트: 서버가 끊었는지 확인(버퍼에 남은 것을 비우며 0/RST 를 기다림).</summary>
        public bool DrainUntilClosed(int ms)
        {
            var sw = Stopwatch.StartNew();
            var buf = new byte[65536];
            try
            {
                tc.Client.ReceiveTimeout = 200;
                while (sw.ElapsedMilliseconds < ms)
                {
                    int n;
                    try { n = tc.Client.Receive(buf); }
                    catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut) { return false; }
                    if (n <= 0) return true;
                }
            }
            catch (Exception) { return true; }                // RST
            return false;
        }

        public void Dispose()
        {
            disposed = true;
            try { tc.Client.Shutdown(SocketShutdown.Both); } catch (Exception) { }
            try { tc.Close(); } catch (Exception) { }
            th?.Join(1000);
        }

        public static string Hex(IEnumerable<byte> b) => string.Join(" ", b.Select(x => x.ToString("X2")));
        public static bool Contains(byte[] hay, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= hay.Length; i++)
                if (hay.AsSpan(i, needle.Length).SequenceEqual(needle)) return true;
            return false;
        }
    }

    internal static class TelnetScenarios
    {
        const byte IAC = 0xFF, DONT = 0xFE, DO = 0xFD, WONT = 0xFC, WILL = 0xFB, SB = 0xFA, IP = 0xF4, BRK = 0xF3, NOP = 0xF1, SE = 0xF0;
        // 계획 §3.5: WILL ECHO, WILL SGA, DO SGA, WILL BINARY, DO BINARY (옵션 번호 ECHO=1, SGA=3, BINARY=0)
        internal static readonly byte[] Greeting = { IAC, WILL, 1, IAC, WILL, 3, IAC, DO, 3, IAC, WILL, 0, IAC, DO, 0 };
        // 부록 A S0-3: TeraTerm 응답 DO ECHO, DO SGA, WILL SGA, DO BINARY, WILL BINARY
        internal static readonly byte[] TeraTermReply = { IAC, DO, 1, IAC, DO, 3, IAC, WILL, 3, IAC, DO, 0, IAC, WILL, 0 };

        internal static byte[] B(string latin1) => Encoding.Latin1.GetBytes(latin1);
        internal static byte[] Cat(params byte[][] a) => a.SelectMany(x => x).ToArray();

        /// <summary>txFrom 이후 출처가 src 인 송신 바이트(순서대로 이어 붙임).</summary>
        static byte[] TxBytes(HubRig r, int txFrom, TxSource src)
            => r.Tx.Skip(txFrom).Where(x => x.Source == src).SelectMany(x => x.Data).ToArray();

        static JsonElement LsCable(HubRig r)
        {
            var ls = r.Call("ls");
            return ls.GetProperty("cables").EnumerateArray().First(x => J.S(x, "cable") == r.Cab.Id);
        }

        // ================================================================== 10
        [Scenario("10", "Telnet: 협상 바이트(WILL ECHO·WILL SGA·DO SGA·WILL/DO BINARY), 협상 전 입력 폐기, IAC SB 파싱·미지원 옵션 WONT/DONT, CR LF·CR NUL→CR(조각 경계 포함), 0xFF 이스케이프(양방향), IAC IP→0x03, IAC BRK→break, IAC NOP·CPR 응답은 사람 입력 아님, HTTP 첫 줄 끊김, 2초 무협상 raw 허용, human_clients", Order = 600, TimeoutS = 120)]
        public static void S10(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIM10" }, "SIMCAB10", "COM110", 17140, c => c.BasePort = 17210);
            int port = r.Cab.TcpPort;
            ctx.Check(port == 17210, "케이블 TCP 포트 17210");
            ctx.WaitUntil(() => r.Hub.Telnet.StateOf(r.Cab) == "listening", 3000, "telnet 127.0.0.1:" + port + " listening");
            ctx.Check(J.S(LsCable(r), "telnet") == "listening" && J.S(LsCable(r), "status") == "attached", "ls: status attached, telnet listening");

            // ---- 1. 접속 즉시 협상 바이트, 협상 응답 전 입력은 버림
            using var a = TnClient.Connect(port);
            ctx.Check(a.WaitCount(Greeting.Length, 2000) && a.All().Take(Greeting.Length).SequenceEqual(Greeting),
                      "접속 즉시 협상 " + TnClient.Hex(Greeting) + " (받음 " + TnClient.Hex(a.All().Take(20)) + ")");
            int tx0 = r.Tx.Count;
            long ring0 = r.Ring.End;
            a.Send("echo pre10\r");
            Thread.Sleep(400);
            ctx.Check(TxBytes(r, tx0, TxSource.Human).Length == 0 && !r.RingText(ring0).Contains("pre10"), "협상 응답 전 입력 'echo pre10' → 보드 0바이트");
            ctx.Check(r.T.Snapshot().HumanClients == -1 && r.Hub.Telnet.ClientCount(r.Cab) == 1, "협상 전 클라이언트는 사람 클라이언트로 세지 않음(human_clients -1 그대로), 연결 1");

            // ---- 2. TeraTerm 응답(S0-3) → 입력 허용, 우리가 요청한 옵션의 응답에는 다시 답하지 않음(루프 없음)
            a.Send(TeraTermReply);
            ctx.WaitUntil(() => r.T.Snapshot().HumanClients == 1, 2000, "협상 응답 → human_clients 1");
            Thread.Sleep(200);
            ctx.Check(a.Count == Greeting.Length, "요청한 옵션의 응답(DO ECHO·DO SGA·WILL SGA·DO BINARY·WILL BINARY)에는 추가 협상 없음 (받은 " + a.Count + "B)");
            // 미지원 옵션: WILL NAWS(31) → DONT 31, DO TTYPE(24) → WONT 24, 이미 켠 옵션 DONT ECHO → WONT ECHO
            int m0 = (int)a.Count;
            a.Send(IAC, WILL, 31, IAC, DO, 24);
            ctx.Check(a.WaitCount(m0 + 6, 2000) && a.Since(m0).SequenceEqual(new byte[] { IAC, DONT, 31, IAC, WONT, 24 }),
                      "미지원 옵션 WILL NAWS → DONT NAWS, DO TTYPE → WONT TTYPE (" + TnClient.Hex(a.Since(m0)) + ")");

            // ---- 3. IAC SB … IAC SE(안의 IAC IAC 포함)는 보드에 가지 않고, CR LF → CR
            tx0 = r.Tx.Count; ring0 = r.Ring.End;
            a.Send(IAC, SB, 31, 0, 80, 0, 24, IAC, SE);                                        // NAWS 80x24
            a.Send(Cat(new byte[] { IAC, SB, 24, 0 }, B("xt"), new byte[] { IAC, IAC }, B("m"), new byte[] { IAC, SE }, B("echo sb10\r\n")));
            ctx.WaitUntil(() => r.RingText(ring0).Contains("\r\nsb10\r\n"), 3000, "SB 뒤 'echo sb10' 실행");
            r.WaitState(PromptState.AtPrompt, 3000, "sb10 뒤");
            var h = TxBytes(r, tx0, TxSource.Human);
            ctx.Check(h.SequenceEqual(B("echo sb10\r")), "보드 수신 = 'echo sb10\\r' (SB 바이트 0, CR LF→CR): " + BoardConsole.Visible(Encoding.Latin1.GetString(h)));

            // CR NUL → CR, CR 이 조각 끝·LF 가 다음 조각 처음이어도 CR 하나(상태 유지 파서)
            tx0 = r.Tx.Count; ring0 = r.Ring.End;
            a.Send("echo nul10\r\0");
            ctx.WaitUntil(() => r.RingText(ring0).Contains("\r\nnul10\r\n"), 3000, "'echo nul10' 실행");
            r.WaitState(PromptState.AtPrompt, 3000, "nul10 뒤");
            a.Send("echo split10\r");
            Thread.Sleep(150);
            a.Send("\n");
            ctx.WaitUntil(() => r.RingText(ring0).Contains("\r\nsplit10\r\n"), 3000, "'echo split10' 실행");
            Thread.Sleep(300);
            h = TxBytes(r, tx0, TxSource.Human);
            ctx.Check(h.SequenceEqual(B("echo nul10\recho split10\r")), "CR NUL → CR, 조각 경계의 CR|LF → CR (보드 수신 " + BoardConsole.Visible(Encoding.Latin1.GetString(h)) + ")");
            r.WaitState(PromptState.AtPrompt, 3000, "split10 뒤");

            // ---- 4. 0xFF: 클라이언트 IAC IAC → 보드 0xFF 1바이트, 보드 출력 0xFF → 클라이언트 FF FF
            tx0 = r.Tx.Count; ring0 = r.Ring.End;
            int m1 = (int)a.Count;
            a.Send(Cat(B("echo y"), new byte[] { IAC, IAC }, B("z")));
            ctx.WaitUntil(() => r.RingText(ring0).Contains("y\xFFz"), 3000, "보드 에코 'y\\xFFz'(0xFF 1바이트)");
            h = TxBytes(r, tx0, TxSource.Human);
            ctx.Check(h.SequenceEqual(B("echo y\xFFz")), "보드 수신 'echo y' FF 'z' (IAC IAC → 0xFF 하나): " + TnClient.Hex(h));
            ctx.WaitUntil(() => TnClient.Contains(a.Since(m1), new byte[] { (byte)'y', 0xFF, 0xFF, (byte)'z' }), 3000, "클라이언트는 에코의 0xFF 를 FF FF 로 받음");
            a.Send("\x15");                                                                    // ^U 로 줄 지움
            Thread.Sleep(100);
            int txE = r.Tx.Count;
            a.Send("\r");
            ctx.WaitUntil(() => TxBytes(r, txE, TxSource.Human).SequenceEqual(B("\r")), 2000, "Enter 송신(^U 다시 그리기 프롬프트로 이미 AT_PROMPT 일 수 있어 송신을 먼저 확인)");
            r.WaitState(PromptState.AtPrompt, 3000, "^U·Enter 뒤");

            // ---- 5. IAC IP → 0x03(^C)
            ring0 = r.Ring.End;
            tx0 = r.Tx.Count;                         // 송신 전에 잡는다(추적기 처리기가 HubRig 기록보다 먼저 돌아 상태가 먼저 바뀔 수 있다)
            a.Send("sleep 30\r");
            r.WaitState(PromptState.Busy, 3000, "sleep 30");
            ctx.WaitUntil(() => r.RingText(ring0).Contains("sleep 30\r\n"), 2000, "'sleep 30' 에코");
            a.Send(IAC, IP);
            r.WaitState(PromptState.AtPrompt, 3000, "IAC IP → sleep 중단");
            h = TxBytes(r, tx0, TxSource.Human);
            ctx.Check(h.SequenceEqual(B("sleep 30\r\x03")), "IAC IP → 보드 수신 0x03 ('sleep 30\\r' 뒤 " + TnClient.Hex(h.Skip(9)) + ")");

            // ---- 6. IAC BRK → serial break
            long br0 = r.Board.BreakCount;
            tx0 = r.Tx.Count;
            a.Send(IAC, BRK);
            ctx.WaitUntil(() => r.Board.BreakCount == br0 + 1, 2000, "IAC BRK → 보드 break 1회");
            ctx.Check(r.Tx.Count == tx0, "break 는 데이터 바이트가 아님");

            // ---- 7. IAC NOP·CPR 응답은 사람 입력이 아님
            r.IdleWait();
            var sn0 = r.T.Snapshot();
            tx0 = r.Tx.Count;
            a.Send(IAC, NOP);
            a.Send("\x1b[24;80R");
            Thread.Sleep(300);
            var nt = r.Tx.Skip(tx0).ToList();
            ctx.Check(nt.Count == 1 && nt[0].Source == TxSource.TerminalReply && nt[0].Data.SequenceEqual(B("\x1b[24;80R")),
                      "IAC NOP 은 COM 에 가지 않고, CPR 응답 ESC[24;80R 은 TerminalReply 로 전달 (" + string.Join(",", nt.Select(x => x.Source + ":" + TnClient.Hex(x.Data))) + ")");
            var sn1 = r.T.Snapshot();
            ctx.Check(sn1.LastHumanTxMs == sn0.LastHumanTxMs && sn1.State == PromptState.AtPrompt, "사람 입력 시각 그대로, AT_PROMPT 유지 (" + sn1.StateName + ")");
            var run = r.Call("run", r.P, "echo after10", "--timeout", "3");
            ctx.Check(J.Seq(J.A(run, "out"), "after10"), "NOP·CPR 직후에도 사람 유휴로 보고 run 성공 (" + J.Show(run) + ")");

            // ---- 8. HTTP 첫 줄 → 즉시 끊김(첫 바이트들이 조각으로 와도)
            long hb = TxBytes(r, 0, TxSource.Human).Length;
            foreach (var req in new[] { "GET / HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n", "POST /x HTTP/1.1\r\n\r\nls\r", "PUT / HTTP/1.1\r\n", "OPTIONS * HTTP/1.1\r\n", "HEAD / HTTP/1.1\r\n" })
            {
                using var w = TnClient.Connect(port);
                w.WaitCount(Greeting.Length, 2000);
                w.Send(req);
                ctx.Check(w.WaitClosed(2000), "'" + req.Substring(0, req.IndexOf(' ') + 1) + "' 첫 줄 → 서버가 즉시 끊음");
            }
            using (var w = TnClient.Connect(port))
            {
                w.WaitCount(Greeting.Length, 2000);
                w.Send("PO");
                Thread.Sleep(150);
                ctx.Check(!w.ClosedByPeer, "'PO' 만으로는 판정 보류(연결 유지)");
                w.Send("ST / HTTP/1.1\r\n");
                ctx.Check(w.WaitClosed(2000), "'PO'+'ST ' 조각 → 끊김");
            }
            ctx.Check(TxBytes(r, 0, TxSource.Human).Length == hb && !r.RingText().Contains("HTTP/1.1"), "HTTP 클라이언트 바이트는 보드에 0");
            ctx.Check(r.T.Snapshot().HumanClients == 1, "HTTP 클라이언트는 사람 클라이언트 수에 들어가지 않음");

            // ---- 9. 협상 무응답 2초 → raw 클라이언트(nc): 그 전 입력은 버리고 그 뒤는 허용, 출력 0xFF 는 이스케이프하지 않음
            ring0 = r.Ring.End;
            tx0 = r.Tx.Count;
            using var raw = TnClient.Connect(port);
            raw.Send("echo early10\r");
            Thread.Sleep(r.Cfg.TelnetNegotiationMs + 300);
            ctx.WaitUntil(() => r.T.Snapshot().HumanClients == 2, 2000, "2초 무협상 → raw 클라이언트 입력 허용, human_clients 2");
            int m2 = (int)raw.Count;
            raw.Send("echo raw10\r");
            ctx.WaitUntil(() => r.RingText(ring0).Contains("\r\nraw10\r\n"), 3000, "raw 클라이언트 'echo raw10' 실행");
            ctx.Check(!r.RingText(ring0).Contains("early10") && TxBytes(r, tx0, TxSource.Human).SequenceEqual(B("echo raw10\r")), "협상 대기 중 'echo early10' 은 버려짐");
            ctx.WaitUntil(() => TnClient.Contains(raw.Since(m2), B("\r\nraw10\r\n")) && TnClient.Contains(a.All(), B("\r\nraw10\r\n")), 2000, "출력은 두 클라이언트 모두에게(fanout, 소켓 전달은 비동기)");
            r.WaitState(PromptState.AtPrompt, 3000, "raw10 뒤");
            int m3 = (int)raw.Count;
            r.Board.EmitBytes(new byte[] { (byte)'q', 0xFF, (byte)'r', 0x0d, 0x0a });
            ctx.WaitUntil(() => TnClient.Contains(raw.Since(m3), new byte[] { (byte)'q', 0xFF, (byte)'r' }), 2000, "raw 클라이언트는 0xFF 를 그대로 받음");

            // ---- 10. 클라이언트가 떠나면 human_clients 감소
            raw.Dispose();
            ctx.WaitUntil(() => r.T.Snapshot().HumanClients == 1, 2000, "raw 끊김 → human_clients 1");
            a.Dispose();
            ctx.WaitUntil(() => r.T.Snapshot().HumanClients == 0 && r.Hub.Telnet.ClientCount(r.Cab) == 0, 2000, "모두 끊김 → human_clients 0");
            var st = r.Call("status", r.P);
            ctx.Check(J.N(st, "human_clients") == 0 && J.S(st, "telnet") == "listening" && J.N(st, "telnet_clients") == 0, "status: human_clients 0, telnet listening");
        }

        // ================================================================== 12
        [Scenario("12", "느린 telnet 클라이언트: 읽지 않는 클라이언트만 송신 큐 1MB 초과로 끊기고(*** telnet client … dropped *** 기록), 읽는 클라이언트는 빠짐없이 받음, 기록 코어 무중단(.raw 합계 == FakeBoard 송신)", Order = 610, TimeoutS = 120)]
        public static void S12(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIM12", MaxChunk = 4096 }, "SIMCAB12", "COM112", 17141,
                                 c => { c.BasePort = 17220; c.RingMb = 8; });
            int port = r.Cab.TcpPort;
            ctx.Check(r.Cfg.ClientQueueBytes == 1 << 20, "클라이언트 송신 큐 상한 = 기본 1MB");
            ctx.WaitUntil(() => r.Hub.Telnet.StateOf(r.Cab) == "listening", 3000, "telnet listening");
            using var fast = TnClient.Connect(port);
            fast.WaitCount(Greeting.Length, 2000);
            fast.Send(TeraTermReply);
            using var slow = TnClient.Connect(port, read: false, recvBuf: 4096);
            slow.Send(TeraTermReply);
            ctx.WaitUntil(() => r.Hub.Telnet.ClientCount(r.Cab) == 2 && r.T.Snapshot().HumanClients == 2, 3000, "클라이언트 2개(빠름·읽지 않음)");
            long fast0 = fast.Count;
            long ring0 = r.Ring.End;

            // 4MB: 64바이트 줄(0xFF 없음)을 32KB 묶음으로 10ms 마다(빠른 클라이언트가 따라올 속도, 느린 쪽은 큐 1MB + 소켓 버퍼를 넘김)
            var line = Encoding.ASCII.GetBytes(new string('x', 52) + " slow-test\r\n");
            var block = Enumerable.Range(0, 32768 / line.Length).SelectMany(_ => line).ToArray();
            long emitted = 0;
            var sw = Stopwatch.StartNew();
            while (emitted < 4L << 20)
            {
                r.Board.EmitBytes(block);
                emitted += block.Length;
                Thread.Sleep(10);
            }
            ctx.Log("방출 " + emitted + "B, " + sw.ElapsedMilliseconds + "ms");
            ctx.WaitUntil(() => r.Board.PendingBytes == 0 && r.Ring.End - ring0 >= emitted, 15000, "허브가 보드 출력을 모두 읽음(PortReader 무정지)");
            ctx.WaitUntil(() => r.Hub.Telnet.ClientCount(r.Cab) == 1, 10000, "읽지 않는 클라이언트만 끊김(남은 연결 1)");
            ctx.WaitUntil(() => fast.Count - fast0 >= emitted, 15000, "읽는 클라이언트가 " + emitted + "B 를 모두 받음");
            ctx.Check(fast.Count - fast0 == r.Ring.End - ring0 && !fast.ClosedByPeer, "빠른 클라이언트 수신 = 링 증가분(" + (r.Ring.End - ring0) + "B), 연결 유지");
            var fb = fast.All().Skip((int)fast0).ToArray();
            ctx.Check(fb.Take(line.Length).SequenceEqual(line) && fb.Skip(fb.Length - line.Length).SequenceEqual(line), "받은 바이트 처음·끝이 보드 출력과 같음");
            ctx.Check(slow.DrainUntilClosed(5000), "느린 클라이언트 소켓은 서버가 닫음");
            ctx.Check(r.T.Snapshot().HumanClients == 1, "human_clients 1");
            r.Hub.WaitDrained(10000);
            long raw = LogFiles.RawTotal(r.Cfg.LogRoot), sent = r.Board.TotalSent;
            ctx.Check(raw == sent, ".raw 합계 " + raw + " == FakeBoard 송신 " + sent + " (기록 코어 막힘 없음)");
            string log = r.LogText();
            ctx.Check(log.Contains("*** telnet client ") && log.Contains("dropped (send queue > 1048576 bytes) ***"), ".log 에 *** telnet client … dropped (send queue > 1048576 bytes) ***");
            ctx.Check(LogFiles.ReadShared(r.Hub.Log.Path).Contains("끊김: send queue > 1048576 bytes"), "hub.log 에 끊은 이유");
            int lines = log.Split('\n').Count(x => x.EndsWith("dropped (send queue > 1048576 bytes) ***\r") || x.EndsWith("dropped (send queue > 1048576 bytes) ***"));
            ctx.Check(lines == 1, "끊김 기록 1건(빠른 클라이언트는 끊기지 않음): " + lines);
        }

        // ================================================================== 22
        [Scenario("22", "bind 실패: 케이블 TCP 포트를 다른 소켓이 선점 → ls·status 에 bind_failed(telnet_error), 기록·run 은 계속, 선점이 풀리면 재시도로 listening·attached 복귀", Order = 620, TimeoutS = 60)]
        public static void S22(ScenarioContext ctx)
        {
            const int port = 17230;
            var squat = new TcpListener(IPAddress.Loopback, port);
            squat.Start();
            bool squatting = true;
            ctx.Defer(() => { if (squatting) squat.Stop(); });
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIM22" }, "SIMCAB22", "COM122", 17142, c => { c.BasePort = port; c.TelnetBindRetryMs = 300; });
            ctx.Check(r.Cab.TcpPort == port, "케이블 TCP 포트 " + port + "(선점됨)");
            ctx.WaitUntil(() => r.Hub.Telnet.StateOf(r.Cab) == "bind_failed", 3000, "telnet bind_failed");
            var e = LsCable(r);
            ctx.Check(J.S(e, "status") == "bind_failed" && J.S(e, "telnet") == "bind_failed" && !string.IsNullOrEmpty(J.S(e, "telnet_error")),
                      "ls: status bind_failed, telnet_error (" + J.Show(e) + ")");
            ctx.Check(J.S(r.Call("status", r.P), "cable_status") == "bind_failed", "status: cable_status bind_failed");
            ctx.Check(LogFiles.ReadShared(r.Hub.Log.Path).Contains("telnet bind 실패 127.0.0.1:" + port), "hub.log 에 bind 실패");

            // 기록·run 계속
            var a = r.Call("run", r.P, "echo bf22");
            ctx.Check(J.Seq(J.A(a, "out"), "bf22"), "bind_failed 중 run 정상 (" + J.Show(a) + ")");
            r.Board.EmitAppLog("app log 22");
            ctx.WaitUntil(() => r.LogText().Contains("app log 22"), 3000, "보드 출력이 .log 에 기록됨");
            r.Hub.WaitDrained(3000);
            ctx.Check(LogFiles.RawTotal(r.Cfg.LogRoot) == r.Board.TotalSent, ".raw 합계 == FakeBoard 송신 (" + r.Board.TotalSent + ")");
            ctx.Check(r.Cab.IsAttached, "케이블은 attached(COM 보유)");

            // 선점 해제 → 재시도로 복귀
            squat.Stop();
            squatting = false;
            ctx.WaitUntil(() => r.Hub.Telnet.StateOf(r.Cab) == "listening", 5000, "선점 해제 → 재시도 bind → listening");
            e = LsCable(r);
            ctx.Check(J.S(e, "status") == "attached" && J.S(e, "telnet") == "listening" && !J.Has(e, "telnet_error"), "ls: attached, listening (" + J.Show(e) + ")");
            using var c = TnClient.Connect(port);
            ctx.Check(c.WaitCount(Greeting.Length, 2000) && c.All().Take(Greeting.Length).SequenceEqual(Greeting), "복귀 뒤 접속 → 협상 바이트");
        }

        // ------------------------------------------------------------------ 케이블 분리·재연결(실보드 11-03: 분리 때 연결을 닫아 TeraTerm 창이 닫힘)
        internal static string DetachBanner(string com) => "\r\n*** uart-hub: cable detached (" + com + ") - replug to resume ***\r\n";
        internal static string AttachBanner(string com) => "\r\n*** uart-hub: cable attached (" + com + ") ***\r\n";
        internal static string DroppedBanner(string com) => "\r\n*** uart-hub: cable detached (" + com + ") - input discarded ***\r\n";

        internal static int Count(byte[] hay, byte[] needle)
        {
            int n = 0;
            for (int i = 0; i + needle.Length <= hay.Length; i++)
                if (hay.AsSpan(i, needle.Length).SequenceEqual(needle)) { n++; i += needle.Length - 1; }
            return n;
        }

        /// <summary>TeraTerm 처럼 협상 응답까지 한 클라이언트(입력 허용 = human_clients 증가 확인).</summary>
        internal static TnClient Human(HubRig r, int port, int humans)
        {
            var c = TnClient.Connect(port);
            r.Ctx.Check(c.WaitCount(Greeting.Length, 2000) && c.All().Take(Greeting.Length).SequenceEqual(Greeting), "접속 → 협상 바이트");
            c.Send(TeraTermReply);
            r.Ctx.WaitUntil(() => r.T.Snapshot().HumanClients == humans, 3000, "협상 응답 → human_clients " + humans);
            return c;
        }

        // ================================================================== telnet-replug
        [Scenario("telnet-replug", "케이블 분리→재연결: telnet 연결·listener 유지(닫지 않음), 분리/재연결 배너 바이트(ASCII), 재연결 뒤 같은 연결로 입력·출력 재개, 클라이언트 수신 = 링 바이트 + 배너(중복·누락 없음), 배너는 링·.raw·.log·추적기에 없음", Order = 630, TimeoutS = 90)]
        public static void TelnetReplug(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIMTR" }, "SIMCABTR", "COM160", 17155, c => c.BasePort = 17500);
            int port = r.Cab.TcpPort;
            var prov = (FakePortProvider)r.Prov;
            ctx.WaitUntil(() => r.Hub.Telnet.StateOf(r.Cab) == "listening", 3000, "telnet 127.0.0.1:" + port + " listening");
            using var a = Human(r, port, 1);
            Thread.Sleep(300);                                                     // 링 조용: 이 뒤 클라이언트 수신 = 링 증가분
            int m0 = (int)a.Count;
            long ringStart = r.Ring.End;
            r.Human("echo before_tr\r");
            r.WaitState(PromptState.AtPrompt, 3000, "echo before_tr");
            ctx.WaitUntil(() => TnClient.Contains(a.Since(m0), B("\r\nbefore_tr\r\n")), 2000, "분리 전 출력 수신");

            long seq0 = r.Cab.AttachSeq;
            prov.Unplug("SIMCABTR", new IOException("시험 탈착"));
            ctx.WaitUntil(() => !r.Cab.IsAttached, 3000, "케이블 분리");
            long ringAtDetach = r.Ring.End;
            ctx.WaitUntil(() => TnClient.Contains(a.Since(m0), B(DetachBanner("COM160"))), 3000, "분리 배너 '" + BoardConsole.Visible(DetachBanner("COM160")) + "'");
            Thread.Sleep(300);
            ctx.Check(!a.ClosedByPeer && r.Hub.Telnet.ClientCount(r.Cab) == 1 && r.Hub.Telnet.StateOf(r.Cab) == "listening", "분리 뒤에도 telnet 연결·listener 유지(클라이언트 1, listening)");
            var e = LsCable(r);
            ctx.Check(J.S(e, "status") == "detached" && J.S(e, "telnet") == "listening", "ls: status detached, telnet listening (" + J.Show(e) + ")");
            ctx.Check(r.T.Snapshot().HumanClients == 1, "human_clients 1 유지");

            prov.Plug(r.Board, "SIMCABTR", "COM161");
            ctx.WaitUntil(() => r.Cab.IsAttached && r.Cab.AttachSeq == seq0 + 1, 5000, "재연결(COM161)");
            ctx.Check(r.Cab.AttachRxPos == ringAtDetach, "분리 중 RX 없음: 재연결 위치 = 분리 위치 " + ringAtDetach);
            ctx.WaitUntil(() => TnClient.Contains(a.Since(m0), B(AttachBanner("COM161"))), 3000, "재연결 배너 '" + BoardConsole.Visible(AttachBanner("COM161")) + "'");
            ctx.WaitUntil(() => r.T.Generation >= 2, 3000, "추적기 복원");
            int txH = r.Tx.Count;
            a.Send("echo after_tr\r");                                               // 같은 telnet 연결로 입력 재개
            ctx.WaitUntil(() => r.RingText(ringAtDetach).Contains("\r\nafter_tr\r\n"), 3000, "재연결 뒤 같은 연결의 입력이 보드에 감");
            ctx.Check(TxBytes(r, txH, TxSource.Human).SequenceEqual(B("\u0005\u0015echo after_tr\r")), "보드 수신 = ^E^U(재연결 줄 지우기, 분리 때 AT_PROMPT) + 'echo after_tr\\r'(사람) (" + TnClient.Hex(TxBytes(r, txH, TxSource.Human)) + ")");
            r.WaitState(PromptState.AtPrompt, 3000, "after_tr 뒤 프롬프트 확정(앱 로그가 T_open 안에 붙지 않게)");
            r.Board.EmitAppLog("app after replug");
            ctx.WaitUntil(() => r.RingText(ringAtDetach).Contains("app after replug\r\n"), 3000, "재연결 뒤 보드 출력");
            Thread.Sleep(400);
            var got = a.Since(m0);
            var ring = B(r.RingText(ringStart));
            int cut = (int)(ringAtDetach - ringStart);
            var want = Cat(ring.Take(cut).ToArray(), B(DetachBanner("COM160")), B(AttachBanner("COM161")), ring.Skip(cut).ToArray());
            ctx.Check(got.SequenceEqual(want), "클라이언트 수신(" + got.Length + "B) = 링[분리 전] + 분리 배너 + 재연결 배너 + 링[재연결 뒤] (" + want.Length + "B) — 중복·누락 없음"
                      + (got.SequenceEqual(want) ? "" : "\n      got =" + BoardConsole.Visible(Encoding.Latin1.GetString(got)) + "\n      want=" + BoardConsole.Visible(Encoding.Latin1.GetString(want))));
            ctx.Check(!r.RingText().Contains("uart-hub"), "배너는 링에 없음");
            r.Hub.WaitDrained(3000);
            ctx.Check(!r.LogText().Contains("uart-hub") && LogFiles.RawTotal(r.Cfg.LogRoot) == r.Board.TotalSent, "배너는 .log·.raw 에 없음(.raw 합계 == 보드 송신 " + r.Board.TotalSent + ")");
            ctx.Check(!r.T.Snapshot().LastLines.Any(l => l.Contains("uart-hub")) && r.T.State == PromptState.AtPrompt, "추적기는 배너를 보지 않음, AT_PROMPT");
            string hl = LogFiles.ReadShared(r.Hub.Log.Path);
            ctx.Check(hl.Contains("telnet :" + port + " 케이블 분리") && hl.Contains("telnet :" + port + " 케이블 재연결") && !hl.Contains("telnet 종료"), "hub.log: 분리·재연결 기록, telnet 종료 없음");
            r.IdleWait();
            ctx.Check(J.Seq(J.A(r.Call("run", r.P, "echo run_tr", "--timeout", "5"), "out"), "run_tr"), "재연결 뒤 run 정상");
        }

        // ================================================================== telnet-detached-io
        [Scenario("telnet-detached-io", "분리 중 telnet: 입력(글자·IAC IP·IAC BRK)은 버리고 큐에 쌓지 않음(보드 0바이트, 재연결 뒤에도 안 나감), 안내 배너는 클라이언트당 분리 한 번에 1회, 분리 중 접속도 받아 분리 배너, 재연결 배너는 모든 클라이언트에, 두 번째 분리에서 안내 다시 1회", Order = 640, TimeoutS = 90)]
        public static void TelnetDetachedIo(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIMTD" }, "SIMCABTD", "COM162", 17156, c => c.BasePort = 17520);
            int port = r.Cab.TcpPort;
            var prov = (FakePortProvider)r.Prov;
            ctx.WaitUntil(() => r.Hub.Telnet.StateOf(r.Cab) == "listening", 3000, "telnet listening");
            using var a = Human(r, port, 1);
            prov.Unplug("SIMCABTD", new IOException("시험 탈착"));
            ctx.WaitUntil(() => TnClient.Contains(a.All(), B(DetachBanner("COM162"))), 3000, "분리 배너");
            long rec0 = r.Board.TotalReceived, br0 = r.Board.BreakCount;
            int tx0 = r.Tx.Count;
            int m1 = (int)a.Count;
            a.Send("echo dropped_td1\r");
            ctx.WaitUntil(() => TnClient.Contains(a.Since(m1), B(DroppedBanner("COM162"))), 2000, "분리 중 입력 → 안내 '" + BoardConsole.Visible(DroppedBanner("COM162")) + "'");
            a.Send("echo dropped_td2\r");
            a.Send(IAC, IP);
            a.Send(IAC, BRK);
            Thread.Sleep(400);
            ctx.Check(Count(a.Since(m1), B(DroppedBanner("COM162"))) == 1, "안내는 이 분리에서 1회 (" + Count(a.Since(m1), B(DroppedBanner("COM162"))) + ")");
            ctx.Check(r.Board.TotalReceived == rec0 && r.Tx.Count == tx0 && r.Board.BreakCount == br0, "분리 중 입력은 COM 송신 0(글자·IP·BRK)");

            // 분리 중 접속: 받아 들이고 협상 바이트 뒤 분리 배너
            using var b = TnClient.Connect(port);
            var wantB = Cat(Greeting, B(DetachBanner("COM162")));
            ctx.Check(b.WaitCount(wantB.Length, 3000) && b.All().SequenceEqual(wantB), "분리 중 접속 → 협상 바이트 + 분리 배너 (" + BoardConsole.Visible(Encoding.Latin1.GetString(b.All())) + ")");
            b.Send(TeraTermReply);
            ctx.WaitUntil(() => r.T.Snapshot().HumanClients == 2, 3000, "분리 중 접속한 클라이언트도 협상 → human_clients 2");
            int mb = (int)b.Count;
            b.Send("x");
            ctx.WaitUntil(() => TnClient.Contains(b.Since(mb), B(DroppedBanner("COM162"))), 2000, "두 번째 클라이언트 입력 → 그 클라이언트에 안내 1회");
            Thread.Sleep(200);
            ctx.Check(Count(a.Since(m1), B(DroppedBanner("COM162"))) == 1, "다른 클라이언트의 안내는 첫 클라이언트에 가지 않음");

            // 재연결: 두 클라이언트 모두 재연결 배너, 버린 입력은 나가지 않음
            long seq0 = r.Cab.AttachSeq;
            prov.Plug(r.Board, "SIMCABTD", "COM163");
            ctx.WaitUntil(() => r.Cab.IsAttached && r.Cab.AttachSeq == seq0 + 1, 5000, "재연결(COM163)");
            ctx.WaitUntil(() => TnClient.Contains(a.All(), B(AttachBanner("COM163"))) && TnClient.Contains(b.All(), B(AttachBanner("COM163"))), 3000, "두 클라이언트 모두 재연결 배너");
            ctx.WaitUntil(() => r.T.Generation >= 2, 3000, "추적기 복원");
            long ringR = r.Ring.End;
            a.Send("echo ok_td\r");
            ctx.WaitUntil(() => r.RingText(ringR).Contains("\r\nok_td\r\n"), 3000, "재연결 뒤 입력 정상");
            Thread.Sleep(300);
            ctx.Check(r.Board.TotalReceived - rec0 == 2 + B("echo ok_td\r").Length && !r.RingText(ringR).Contains("dropped_td"), "분리 중 친 입력은 재연결 뒤에도 보드에 가지 않음(보드 수신 = 재연결 줄 지우기 ^E^U + 'echo ok_td\\r' 만)");
            ctx.WaitUntil(() => TnClient.Contains(b.All(), B("\r\nok_td\r\n")), 2000, "출력은 두 클라이언트 모두에게");

            // 두 번째 분리: 안내가 다시 1회
            r.WaitState(PromptState.AtPrompt, 3000, "ok_td 뒤");
            int m2 = (int)a.Count;
            prov.Unplug("SIMCABTD", new IOException("시험 탈착 2"));
            ctx.WaitUntil(() => TnClient.Contains(a.Since(m2), B(DetachBanner("COM163"))), 3000, "두 번째 분리 배너(COM163)");
            a.Send("y");
            a.Send("z");
            ctx.WaitUntil(() => TnClient.Contains(a.Since(m2), B(DroppedBanner("COM163"))), 2000, "두 번째 분리 중 입력 → 안내");
            Thread.Sleep(300);
            ctx.Check(Count(a.Since(m2), B(DroppedBanner("COM163"))) == 1 && !a.ClosedByPeer && !b.ClosedByPeer, "두 번째 분리에서도 안내 1회, 연결 유지");
            ctx.Check(!r.RingText().Contains("uart-hub") && !r.T.Snapshot().LastLines.Any(l => l.Contains("uart-hub")), "배너·안내는 링·추적기에 없음");
        }
    }
}
