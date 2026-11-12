// 제어 포트 클라이언트(계획 §3.6 인사). 감독(ping·트레이 ls, Supervisor.cs)과 CLI(src/host/Cli.cs)가 같이 쓴다.
// 시험의 CtlClient(testing/HubRig.cs)는 독립 구현으로 남긴다(같은 코드로 같은 코드를 시험하지 않게).
//   1. 허브 인사 {"uart_hub":1,"nonce":"<32~128자 hex>"} 를 바이트 단위로 \n 까지(greetMs, 4096B 상한) 읽는다. 아니면 NotHub — 아무것도 보내지 않는다.
//   2. hello = hex(HMAC-SHA256(token, "c|"+nonce)) + cnonce → 허브 proof = hex(HMAC-SHA256(token, "s|"+cnonce)) 확인. 토큰 원문은 보내지 않는다.
//   3. 요청 한 줄 → 응답 한 줄(replyMs, -1 = 무한).
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace UartHub
{
    public static class ControlClient
    {
        public enum Result { Ok, ConnectFailed, NotHub, HelloRejected, ProofMismatch, Failed }

        static readonly Regex NonceRe = new Regex("^[0-9a-f]{32,128}$", RegexOptions.CultureInvariant);

        public static string Mac(byte[] key, string msg) => Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(msg))).ToLowerInvariant();

        /// <summary>
        /// 한 번 호출한다. reply = 응답 줄(연결이 응답 없이 닫혔으면 null), error = Failed 일 때의 예외 메시지.
        /// </summary>
        public static Result Call(int port, byte[] key, string requestLine, int connectMs, int greetMs, int helloMs, int replyMs,
                                  out string reply, out string error)
        {
            reply = null; error = null;
            using var tcp = new TcpClient { NoDelay = true };
            try
            {
                if (!tcp.ConnectAsync(IPAddress.Loopback, port).Wait(connectMs)) return Result.ConnectFailed;
            }
            catch (Exception) { return Result.ConnectFailed; }
            try
            {
                var ns = tcp.GetStream();
                string nonce = HubNonce(ReadFirstLine(ns, greetMs));
                if (nonce == null) return Result.NotHub;
                var enc = new UTF8Encoding(false);
                var w = new StreamWriter(ns, enc) { NewLine = "\n", AutoFlush = true };
                var r = new StreamReader(ns, enc);
                string cnonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                ns.ReadTimeout = helloMs;
                w.WriteLine("{\"hello\":" + Json.Str(Mac(key, "c|" + nonce)) + ",\"cnonce\":" + Json.Str(cnonce) + "}");
                string hello = r.ReadLine();
                if (string.IsNullOrEmpty(hello)) return Result.HelloRejected;
                if (!ProofOk(hello, Mac(key, "s|" + cnonce))) return Result.ProofMismatch;
                ns.ReadTimeout = replyMs < 0 ? Timeout.Infinite : replyMs;
                w.WriteLine(requestLine);
                reply = r.ReadLine();
                return Result.Ok;
            }
            catch (Exception e)
            {
                error = e.Message;
                return Result.Failed;
            }
        }

        /// <summary>감독용: 토큰 파일을 읽어 한 번 호출, 성공한 응답 줄(실패면 null). argvJson = JSON 문자열 배열.</summary>
        public static string CallWithTokenFile(int port, string tokenPath, int timeoutMs, string argvJson)
        {
            byte[] key;
            try { key = Encoding.UTF8.GetBytes(File.ReadAllText(tokenPath).Trim()); }
            catch (Exception) { return null; }
            var res = Call(port, key, "{\"argv\":" + argvJson + "}", timeoutMs, timeoutMs, timeoutMs, timeoutMs, out string reply, out _);
            return res == Result.Ok ? reply : null;
        }

        /// <summary>첫 줄을 바이트 단위로 \n 까지 읽는다(전체 ms 제한, 4096B 상한). 실패·시간 초과·EOF 면 null. 뒤 바이트를 미리 가져가지 않는다.</summary>
        static string ReadFirstLine(NetworkStream ns, int ms)
        {
            var sw = Stopwatch.StartNew();
            var buf = new MemoryStream();
            var one = new byte[1];
            while (buf.Length < 4096)
            {
                long left = ms - sw.ElapsedMilliseconds;
                if (left <= 0) return null;
                ns.ReadTimeout = (int)Math.Max(1, left);
                int n;
                try { n = ns.Read(one, 0, 1); }
                catch (Exception) { return null; }
                if (n <= 0) return null;
                if (one[0] == 10) return Encoding.UTF8.GetString(buf.ToArray()).TrimEnd('\r');
                buf.WriteByte(one[0]);
            }
            return null;
        }

        /// <summary>허브 인사면 nonce, 아니면 null.</summary>
        static string HubNonce(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            try
            {
                using var d = JsonDocument.Parse(line);
                var o = d.RootElement;
                if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty("uart_hub", out var hv)) return null;
                bool one = (hv.ValueKind == JsonValueKind.Number && hv.GetRawText() == "1") || (hv.ValueKind == JsonValueKind.String && hv.GetString() == "1");
                if (!one || !o.TryGetProperty("nonce", out var nv) || nv.ValueKind != JsonValueKind.String) return null;
                string n = nv.GetString();
                return n != null && NonceRe.IsMatch(n) ? n : null;
            }
            catch (JsonException) { return null; }
        }

        static bool ProofOk(string helloLine, string want)
        {
            try
            {
                using var d = JsonDocument.Parse(helloLine);
                var o = d.RootElement;
                if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty("hello", out var hv) || hv.ValueKind != JsonValueKind.String || hv.GetString() != "ok"
                    || !o.TryGetProperty("proof", out var pv) || pv.ValueKind != JsonValueKind.String) return false;
                byte[] got = Encoding.UTF8.GetBytes(pv.GetString() ?? ""), exp = Encoding.UTF8.GetBytes(want);
                return got.Length == exp.Length && CryptographicOperations.FixedTimeEquals(got, exp);
            }
            catch (JsonException) { return false; }
        }
    }
}
