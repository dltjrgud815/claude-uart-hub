// US-004 시험 도구: 제어 포트 클라이언트(uart.ps1 과 같은 프로토콜), FakeBoard 허브 리그, uart.ps1 프로세스 실행기, JSON 조회 도우미.
// "Claude 0바이트"는 FakeBoard 수신 바이트(TotalReceived)에서 사람 송신 바이트를 빼서 센다(Arbiter 의 실제 송신).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace UartHub.Testing
{
    /// <summary>
    /// 제어 포트 시험 클라이언트(uart.ps1 과 같은 인사, §3.6): 허브 인사 {"uart_hub":1,"nonce"} → hello(HMAC) + cnonce → proof 확인 → 요청 한 줄 → 응답 한 줄.
    /// </summary>
    public sealed class CtlClient : IDisposable
    {
        readonly TcpClient tcp;
        readonly NetworkStream ns;
        readonly List<byte> acc = new List<byte>();

        public CtlClient(int port)
        {
            tcp = new TcpClient { NoDelay = true };
            tcp.Connect(IPAddress.Loopback, port);
            ns = tcp.GetStream();
        }

        public void SendRaw(string s)
        {
            var b = Encoding.UTF8.GetBytes(s);
            ns.Write(b, 0, b.Length);
            ns.Flush();
        }

        static bool IsTimeout(IOException e) => e.InnerException is SocketException se && se.SocketErrorCode == SocketError.TimedOut;

        /// <summary>한 줄(\n 까지). 서버가 끊으면 null, 시간 초과면 ScenarioFailure.</summary>
        public string ReadLine(int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            var buf = new byte[4096];
            while (true)
            {
                int nl = acc.IndexOf((byte)'\n');
                if (nl >= 0)
                {
                    string line = Encoding.UTF8.GetString(acc.Take(nl).ToArray()).TrimEnd('\r');
                    acc.RemoveRange(0, nl + 1);
                    return line;
                }
                long left = timeoutMs - sw.ElapsedMilliseconds;
                if (left <= 0) throw new ScenarioFailure("제어 응답 대기 초과(" + timeoutMs + "ms)");
                tcp.ReceiveTimeout = (int)left;
                int n;
                try { n = ns.Read(buf, 0, buf.Length); }
                catch (IOException e) when (IsTimeout(e)) { throw new ScenarioFailure("제어 응답 대기 초과(" + timeoutMs + "ms)"); }
                catch (IOException) { return null; }
                if (n <= 0) return null;
                acc.AddRange(buf.Take(n));
            }
        }

        /// <summary>허브 인사의 nonce(아직 안 읽었으면 null).</summary>
        public string Nonce { get; private set; }

        /// <summary>허브 인사 한 줄을 읽고 nonce 를 돌려준다(이미 읽었으면 그 값). 인사 형식이 아니면 ScenarioFailure.</summary>
        public string Greeting(int timeoutMs = 5000)
        {
            if (Nonce != null) return Nonce;
            string l = ReadLine(timeoutMs);
            if (l == null) throw new ScenarioFailure("허브 인사 없이 연결이 닫힘");
            using var d = JsonDocument.Parse(l);
            if (J.N(d.RootElement, "uart_hub") != 1 || string.IsNullOrEmpty(J.S(d.RootElement, "nonce"))) throw new ScenarioFailure("허브 인사 형식 아님: " + l);
            return Nonce = J.S(d.RootElement, "nonce");
        }

        /// <summary>hex(HMAC-SHA256(token, msg)) — uart.ps1·Supervisor 와 같은 계산(소문자 hex).</summary>
        public static string Mac(string token, string msg)
            => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(msg))).ToLowerInvariant();

        public static string NewNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        /// <summary>인사 → hello = HMAC(token, "c|"+nonce) + cnonce → 응답 hello ok 이고 proof = HMAC(token, "s|"+cnonce) 이면 true.</summary>
        public bool Hello(string token)
        {
            string n = Greeting();
            string cn = NewNonce();
            SendRaw("{\"hello\":" + Json.Str(Mac(token, "c|" + n)) + ",\"cnonce\":" + Json.Str(cn) + "}\n");
            string l = ReadLine(5000);
            if (l == null || !l.Contains("\"hello\":\"ok\"")) return false;
            using var d = JsonDocument.Parse(l);
            return J.S(d.RootElement, "proof") == Mac(token, "s|" + cn);
        }

        public void Request(params string[] argv) => SendRaw("{\"argv\":[" + string.Join(",", argv.Select(Json.Str)) + "]}\n");

        public JsonElement Response(int timeoutMs)
        {
            string l = ReadLine(timeoutMs);
            if (l == null) throw new ScenarioFailure("허브가 응답 없이 연결을 닫음");
            using var d = JsonDocument.Parse(l);
            return d.RootElement.Clone();
        }

        /// <summary>timeoutMs 안에 서버가 연결을 끊었는가(EOF·RST). 데이터가 오거나 시간이 지나면 false.</summary>
        public bool ClosedByServer(int timeoutMs)
        {
            tcp.ReceiveTimeout = timeoutMs;
            try { return ns.Read(new byte[256], 0, 256) <= 0; }
            catch (IOException e) { return !IsTimeout(e); }
        }

        public static JsonElement Call(int port, string token, int timeoutMs, params string[] argv)
        {
            using var c = new CtlClient(port);
            if (!c.Hello(token)) throw new ScenarioFailure("hello 실패");
            c.Request(argv);
            return c.Response(timeoutMs);
        }

        public void Dispose() { try { tcp.Close(); } catch (Exception) { } }
    }

    /// <summary>FakeBoard 하나 + 허브(제어 포트 포함). 시험 설정: idle_s 0.3, quiet_ms 600, default_timeout_s 10.</summary>
    internal sealed class HubRig
    {
        public ScenarioContext Ctx;
        public Hub Hub;
        public Cable Cab;
        public FakeBoard Board;
        public IPortProvider Prov;
        public PromptTracker T;
        public Config Cfg;
        public int Ctl;
        public string Token, P;
        public long HumanBytes;
        readonly List<TxRecord> tx = new List<TxRecord>();

        public List<TxRecord> Tx { get { lock (tx) return tx.ToList(); } }
        public long ClaudeBytes => Tx.Where(x => x.Source != TxSource.Human).Sum(x => (long)x.Data.Length);
        public RingBuffer Ring => Cab.Ring;

        public static Config MakeConfig(ScenarioContext ctx, int ctl, Action<Config> tweak = null)
        {
            var cfg = ctx.MakeConfig();
            cfg.ControlPort = ctl;
            cfg.IdleS = 0.3;
            cfg.QuietMs = 600;
            cfg.DefaultTimeoutS = 10;
            tweak?.Invoke(cfg);
            return cfg;
        }

        public static HubRig Start(ScenarioContext ctx, FakeBoardOptions opt, string cableId, string com, int ctl, Action<Config> tweak = null, bool toPrompt = true)
        {
            var cfg = MakeConfig(ctx, ctl, tweak);
            var b = new FakeBoard(opt);
            ctx.Defer(b.PowerOff);
            var prov = new FakePortProvider();
            prov.Plug(b, cableId, com);
            return Boot(ctx, cfg, b, prov, cableId, toPrompt);
        }

        /// <summary>이미 있는 보드·공급자로 허브(코어)를 띄운다(코어 재시작 시험은 같은 cfg 로 다시 부른다).</summary>
        public static HubRig Boot(ScenarioContext ctx, Config cfg, FakeBoard b, IPortProvider prov, string cableId, bool toPrompt)
        {
            var hub = new Hub(cfg, null, new IPortProvider[] { prov });
            hub.Start();
            ctx.Defer(hub.Stop);
            var r = new HubRig { Ctx = ctx, Hub = hub, Board = b, Prov = prov, Cfg = cfg, Ctl = cfg.ControlPort };
            ctx.WaitUntil(() => { r.Cab = hub.Cables.Find(cableId); return r.Cab != null && r.Cab.IsAttached; }, 5000, cableId + " attached");
            r.Cab.Transmitted += rec => { lock (r.tx) r.tx.Add(rec); };
            ctx.WaitUntil(() => { r.T = hub.Tracker(r.Cab); return r.T != null && r.T.Generation > 0; }, 5000, "PromptTracker 준비");
            ctx.Check(hub.Control.Listening, "제어 포트 127.0.0.1:" + cfg.ControlPort + " listen" + (hub.Control.BindError == null ? "" : " (" + hub.Control.BindError + ")"));
            r.Token = File.ReadAllText(hub.Control.TokenPath).Trim();
            r.P = r.Cab.TcpPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (toPrompt)
            {
                r.Human("\r");
                r.WaitState(PromptState.AtPrompt, 3000, "Enter → 프롬프트");
                r.IdleWait();
            }
            return r;
        }

        public void Human(string s)
        {
            var d = Encoding.UTF8.GetBytes(s);
            if (!Cab.Write(d, TxSource.Human)) Ctx.Fail("사람 송신 실패: " + Cab.LastError);
            HumanBytes += d.Length;
        }

        /// <summary>사람 유휴 조건(idle_s)이 찰 때까지.</summary>
        public void IdleWait() => Thread.Sleep((int)(Cfg.IdleS * 1000) + 60);

        public void WaitState(PromptState st, int ms, string what)
        {
            string label = what + " → " + PromptTracker.Name(st);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                if (T.State == st) { Ctx.Log("ok: " + label + " (" + sw.ElapsedMilliseconds + "ms)"); return; }
                Thread.Sleep(5);
            }
            if (T.State == st) { Ctx.Log("ok: " + label); return; }
            Ctx.Fail("시간 초과(" + ms + "ms): " + label + "\n" + Diag());
        }

        /// <summary>실패 진단: 보드 Phase, 추적기 스냅샷·후보·상태 이력, 최근 TX(출처·바이트·링 위치).</summary>
        public string Diag()
        {
            var sn = T.Snapshot();
            var sb = new StringBuilder();
            sb.Append("      board.phase=").Append(Board.Phase).Append(" state=").Append(sn.StateName).Append(" blocked=").Append(sn.BlockedReason)
              .Append(" run_active=").Append(sn.RunActive).Append(" human_typing=").Append(sn.HumanTyping).Append(" orphaned=").Append(sn.Orphaned)
              .Append(" processed=").Append(sn.ProcessedPos).Append(" ring.end=").Append(Ring.End).Append('\n');
            sb.Append("      last_lines=").Append(string.Join(" | ", sn.LastLines.Select(BoardConsole.Visible))).Append('\n');
            foreach (var c in T.Candidates().TakeLast(4)) sb.Append("      cand ").Append(c).Append(" arr=").Append(c.ArrivalMs).Append(" decided=").Append(c.DecidedMs).Append('\n');
            foreach (var h in T.History().TakeLast(10)) sb.Append("      hist ").Append(h).Append('\n');
            foreach (var x in Tx.TakeLast(6))
                sb.Append("      tx ").Append(x.Source).Append(" '").Append(BoardConsole.Visible(Encoding.Latin1.GetString(x.Data))).Append("' ms=").Append(x.MonoMs).Append(" rxpos=").Append(x.RxPos).Append('\n');
            return sb.ToString().TrimEnd('\n');
        }

        public JsonElement Call(params string[] argv) => CtlClient.Call(Ctl, Token, 90000, argv);

        /// <summary>링 전체(남아 있는 범위)를 Latin1 문자열로. 바이트 offset = Ring.Start + 문자 인덱스.</summary>
        public string RingText(long from = 0)
        {
            long start = Math.Max(from, Ring.Start);
            var buf = new byte[Ring.End - start];
            int n = Ring.Read(start, buf, 0, buf.Length);
            return n <= 0 ? "" : Encoding.Latin1.GetString(buf, 0, n);
        }

        public string LogText()
        {
            Hub.WaitDrained(3000);
            return LogFiles.AllLogText(Cfg.LogRoot);
        }

        // ------------------------------------------------------------------ uart.ps1 → UartHub.exe cli(계획 T1 H2)
        public Process StartUart(IDictionary<string, string> env, params string[] args)
        {
            if (!File.Exists(TestEnv.UartHubExe)) Ctx.Fail("UartHub.exe 없음: " + TestEnv.UartHubExe);
            var psi = new ProcessStartInfo(TestEnv.UartHubExe)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
            };
            psi.ArgumentList.Add("cli");
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["UART_HUB_DIR"] = Cfg.EffectiveHubDir;
            psi.Environment["UART_HUB_CONTROL_PORT"] = Ctl.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (env != null) foreach (var kv in env) { if (kv.Value == null) psi.Environment.Remove(kv.Key); else psi.Environment[kv.Key] = kv.Value; }   // null = 변수 없앰
            return Process.Start(psi);
        }

        /// <summary>UartHub.exe cli 를 프로세스로 실행하고 (종료코드, stdout, stderr).</summary>
        public (int Code, string Out, string Err) Uart(IDictionary<string, string> env, params string[] args)
        {
            using var p = StartUart(env, args);
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(90000)) { try { p.Kill(true); } catch (Exception) { } Ctx.Fail("uart.ps1 시간 초과: " + string.Join(" ", args)); }
            p.WaitForExit();
            var res = (p.ExitCode, so.Result, se.Result);
            Ctx.Log("uart.ps1 " + string.Join(" ", args.Select(a => a.Length > 40 ? a.Substring(0, 40) + "…" : a)) + " → rc=" + res.ExitCode
                    + " out=" + BoardConsole.Visible(Trunc(res.Item2)) + (res.Item3.Length > 0 ? " err=" + BoardConsole.Visible(Trunc(res.Item3)) : ""));
            return res;
        }

        static string Trunc(string s) => s.Length > 400 ? s.Substring(0, 400) + "…" : s;
    }

    internal static class J
    {
        public static string S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        public static long N(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : long.MinValue;
        public static bool? B(JsonElement e, string k)
            => e.TryGetProperty(k, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : (bool?)null;
        public static bool Has(JsonElement e, string k) => e.TryGetProperty(k, out _);
        public static bool IsNull(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Null;
        public static string[] A(JsonElement e, string k)
            => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array
               ? v.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText()).ToArray() : null;
        public static string Show(JsonElement e)
        {
            string s = e.GetRawText();
            return s.Length > 600 ? s.Substring(0, 600) + "…" : s;
        }
        public static bool Seq(string[] a, params string[] b) => a != null && a.SequenceEqual(b);
    }
}
