// ControlServer(계획 §3.6·§3.7): 127.0.0.1:control_port, JSON lines.
//   인사(토큰 원문은 오가지 않는다, 토큰 = <hub_dir>\token):
//     ① 서버가 접속 즉시 {"uart_hub":1,"nonce":"<32자 hex>"} 한 줄 — 클라이언트는 이 줄로 상대가 허브인지 먼저 확인한다
//        (같은 포트를 다른 프로그램이 쓰면 아무것도 보내지 않고 103).
//     ② 클라이언트 {"hello":"<hex HMAC-SHA256(token, "c|"+nonce)>","cnonce":"<hex>"} — 고정 시간 비교. 첫 바이트가 '{' 가 아니거나(HTTP 등)
//        hello 가 틀리거나 cnonce 형식이 아니면 응답 없이 즉시 끊는다. 인사→hello 는 절대 기한 5초(조금씩 흘려도 연장 없음),
//        hello 전 연결은 동시에 16개까지(넘으면 인사 없이 끊음). 실패는 hub.log 에 사유별 10초에 한 줄(토큰·hello 값은 남기지 않음).
//        기한 타이머와 hello 성공이 겹치면 CompareExchange 로 한쪽만 이긴다. 남은 위험(계획 R56): 같은 PC 의 다른 프로세스가 5초마다
//        hello 전 연결 16개를 다시 열면 감독 ping 이 계속 실패해 코어가 재시작될 수 있다(기록 코어는 계속 돈다).
//     ③ 서버 {"hello":"ok","proof":"<hex HMAC-SHA256(token, "s|"+cnonce)>","version","pid"} — 클라이언트가 proof 로 허브를 확인한다.
//     두 방향의 HMAC 입력 접두("c|"·"s|")가 달라 한쪽 값을 반대 방향 답으로 되돌려 쓸 수 없다.
//   그 뒤 요청 한 줄 {"argv":[...]} → 응답 한 줄(JSON 객체, status·exit + 공통 cable/com/port/board/rx_offset).
//   argv 해석은 여기서만 한다(uart.ps1 은 argv 를 그대로 넘기고 응답을 출력할 뿐 — WSL·Windows 결과가 같아지는 지점, P4).
// 대상 해석 순서(§3.6): 정수(TCP 포트) → COM\d+ → alias → 보드명. 2개 이상이면 105 + 후보 목록.
// 연결이 끊기면(클라이언트 kill) 아직 보내지 않은 요청은 취소한다(Arbiter: Cancel 성공 = 미전송 보장).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace UartHub
{
    public sealed class ControlServer
    {
        const int HelloTimeoutMs = 5000;            // 인사 → hello 까지 절대 기한(바이트를 조금씩 흘려도 연장되지 않음)
        const int MaxHelloLine = 4096;
        const int MaxUnauthenticated = 16;           // 동시에 hello 전인 연결 상한(넘으면 인사 없이 바로 끊음)
        const int MaxLine = 1 << 20;
        const int MaxOutChars = 1 << 20;             // wait/log 응답 텍스트 상한(넘으면 앞을 자르고 truncated)
        static readonly HashSet<string> Flags = new HashSet<string>(StringComparer.Ordinal)
            { "--json", "--inline", "--no-interrupt", "--force", "--nudge", "--user-ok", "--clear-line", "--detail" };
        static readonly HashSet<string> Valued = new HashSet<string>(StringComparer.Ordinal)
            { "--timeout", "--idle", "--req-id", "--pattern", "--since", "--tail" };
        static readonly Regex ComRe = new Regex(@"^COM\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex ReqIdRe = new Regex(@"^[A-Za-z0-9._:-]{1,64}$", RegexOptions.CultureInvariant);

        readonly Hub hub;
        readonly object sync = new object();
        readonly List<TcpClient> clients = new List<TcpClient>();
        TcpListener listener;
        Thread acceptThread;
        volatile bool stopping;
        byte[] token;
        readonly DateTime started = DateTime.Now;
        int unauthenticated;                         // hello 전 연결 수(Interlocked)

        public ControlServer(Hub hub)
        {
            this.hub = hub;
            Sessions = new SessionStore(hub.Config.EffectiveHubDir, hub.Log);   // 부록 C.12: <허브 폴더>\sessions.json(허브 재시작 뒤 이어 감)
            Events = new EventDetector(hub, Sessions);   // X6 부록 C: CableAttached·Transmitted 만 구독(동결 파일 무변경). 링이 닫히면(허브 정지) 스스로 끝난다
        }

        /// <summary>에이징 세션(부록 C.12). session start|stop|status 가 바꾸고 읽는다.</summary>
        public SessionStore Sessions { get; }

        /// <summary>에이징 사건 감지(X6 부록 C.2·C.3). 읽기는 CLI(uart events·aging)가 &lt;log_root&gt;\events 파일에서 직접 한다.</summary>
        public EventDetector Events { get; }

        /// <summary>bind 실패 사유(성공이면 null). 실패해도 기록 코어는 계속 돈다(P1).</summary>
        public string BindError { get; private set; }
        public bool Listening => listener != null && BindError == null;
        public int Port => hub.Config.ControlPort;
        public string TokenPath => Path.Combine(hub.Config.EffectiveHubDir, "token");

        /// <summary>hub stop/start/upgrade/language 처리기(감독 연결 지점, CoreHost 가 설정: 감독 요청 스풀로 전달 — 동사, 그 뒤 인자). null 이면 106 unsupported.</summary>
        public Func<string, string[], JObj> HubCommandHandler { get; set; }

        /// <summary>hub status 의 supervisor 필드(감독 상태 파일 sup.json + 생존 확인, CoreHost 가 설정). null 이면 supervisor:null.</summary>
        public Func<JObj> SupervisorStatus { get; set; }

        public void Start()
        {
            try { token = Encoding.UTF8.GetBytes(LoadOrCreateToken()); }
            catch (Exception e)
            {
                // 남이 읽을 수 있는 토큰은 만들지 않는다: 제어 포트를 열지 않고(기록 코어는 계속, P1) 사유를 남긴다.
                BindError = "token: " + e.GetType().Name + ": " + e.Message;
                hub.Log.Write(L.T("control 기동 실패 — 토큰을 현재 사용자 전용 ACL 로 만들지 못함(" + TokenPath + "): " + e.GetType().Name + ": " + e.Message, "control did not start — could not create the token with a current-user-only ACL (" + TokenPath + "): " + e.GetType().Name + ": " + e.Message));
                return;
            }
            try
            {
                var l = new TcpListener(IPAddress.Loopback, Port) { ExclusiveAddressUse = true };
                l.Start();
                listener = l;
            }
            catch (SocketException e)
            {
                BindError = e.SocketErrorCode + ": " + e.Message;
                hub.Log.Write(L.T("control bind 실패 127.0.0.1:" + Port + " " + BindError, "control bind failed 127.0.0.1:" + Port + " " + BindError));
                return;
            }
            hub.Log.Write("control 127.0.0.1:" + Port + " listen");
            acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "Control-accept" };
            acceptThread.Start();
        }

        public void Stop()
        {
            stopping = true;
            try { listener?.Stop(); } catch (Exception) { }
            List<TcpClient> cs;
            lock (sync) { cs = clients.ToList(); clients.Clear(); }
            foreach (var c in cs) { try { c.Close(); } catch (Exception) { } }
            acceptThread?.Join(2000);
        }

        string LoadOrCreateToken()
        {
            try
            {
                if (File.Exists(TokenPath))
                {
                    string t = File.ReadAllText(TokenPath).Trim();
                    if (t.Length >= 16) return t;
                }
            }
            catch (Exception e) { hub.Log.Write(L.T("token 읽기 실패: " + e.Message, "token read failed: " + e.Message)); }
            string n = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            WritePrivate(TokenPath, n);
            hub.Log.Write(L.T("token 생성 " + TokenPath + " (상속 끊음, 현재 사용자만)", "token created " + TokenPath + " (inheritance removed, current user only)"));
            return n;
        }

        /// <summary>
        /// 토큰 파일을 install 과 같은 ACL(icacls /inheritance:r /grant:r &lt;SID&gt;:(F))로 만든다. 임시 파일을 처음부터 그 ACL 로 생성해
        /// 쓴 뒤 rename 한다(같은 볼륨 rename 은 보안 설명자를 그대로 옮기므로 내용이 상속 ACL 로 노출되는 순간이 없다). 실패하면 예외.
        /// </summary>
        static void WritePrivate(string path, string text)
        {
            var me = WindowsIdentity.GetCurrent().User;
            var sec = new FileSecurity();
            sec.SetAccessRuleProtection(true, false);
            sec.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl, AccessControlType.Allow));
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            try
            {
                if (File.Exists(tmp)) File.Delete(tmp);         // 남은 임시 파일은 ACL 이 다를 수 있다: 새로 만들어야 sec 가 적용된다
                using (var fs = new FileInfo(tmp).Create(FileMode.CreateNew, FileSystemRights.Write | FileSystemRights.Read, FileShare.None, 4096, FileOptions.None, sec))
                {
                    byte[] b = new UTF8Encoding(false).GetBytes(text);
                    fs.Write(b, 0, b.Length);
                }
                File.Move(tmp, path, true);
            }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { } }
        }

        void AcceptLoop()
        {
            while (!stopping)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); }
                catch (Exception) { if (stopping) return; Thread.Sleep(50); continue; }
                lock (sync) clients.Add(c);
                new Thread(() => Serve(c)) { IsBackground = true, Name = "Control-client" }.Start();
            }
        }

        // ================================================================== 연결
        sealed class LineReader
        {
            readonly Stream s;
            readonly byte[] buf = new byte[8192];
            int pos, len;
            public LineReader(Stream s) { this.s = s; }

            public int PeekByte()
            {
                if (pos < len) return buf[pos];
                len = s.Read(buf, 0, buf.Length);
                pos = 0;
                return len <= 0 ? -1 : buf[0];
            }

            /// <summary>\n 까지(\r 제거). EOF 면 null. max 초과면 IOException.</summary>
            public string ReadLine(int max)
            {
                var ms = new MemoryStream();
                while (true)
                {
                    if (pos >= len)
                    {
                        len = s.Read(buf, 0, buf.Length);
                        pos = 0;
                        if (len <= 0) return ms.Length > 0 ? Decode(ms) : null;
                    }
                    int nl = Array.IndexOf(buf, (byte)'\n', pos, len - pos);
                    int end = nl < 0 ? len : nl;
                    ms.Write(buf, pos, end - pos);
                    if (ms.Length > max) throw new IOException(L.T("줄이 너무 김", "line too long"));
                    pos = nl < 0 ? len : nl + 1;
                    if (nl >= 0) return Decode(ms);
                }
            }

            static string Decode(MemoryStream ms) => Encoding.UTF8.GetString(ms.ToArray()).TrimEnd('\r');
        }

        void Serve(TcpClient c)
        {
            int authed = 0;                           // 0 = hello 전, 1 = hello 통과(본 스레드가 이김), 2 = 기한 초과(타이머가 이김) — CompareExchange 로 한쪽만
            bool counted = false;
            string why = null;                        // hello 전 끊김 사유(hub.log 한 줄, 토큰·hello 값은 남기지 않음)
            Timer deadline = null;
            string peer = "?";
            try
            {
                using (c)
                {
                    var sock = c.Client;
                    peer = sock.RemoteEndPoint?.ToString() ?? "?";
                    if (Interlocked.Increment(ref unauthenticated) > MaxUnauthenticated)
                    {
                        Interlocked.Decrement(ref unauthenticated);
                        why = L.T("hello 전 연결 " + MaxUnauthenticated + "개 초과 — 인사 없이 끊음", "more than " + MaxUnauthenticated + " connections before hello — closed without greeting");
                        return;
                    }
                    counted = true;
                    // 인사 → hello 전체에 절대 기한: 읽기마다의 ReceiveTimeout 은 조금씩 흘려 보내는 상대에게 계속 연장되므로 기한이 되면 소켓을 닫는다
                    deadline = new Timer(_ => { if (Interlocked.CompareExchange(ref authed, 2, 0) == 0) { try { sock.Close(); } catch (Exception) { } } }, null, HelloTimeoutMs, Timeout.Infinite);
                    var ns = c.GetStream();
                    c.ReceiveTimeout = HelloTimeoutMs;
                    var rd = new LineReader(ns);
                    string nonce = Hex(RandomNumberGenerator.GetBytes(16));
                    Write(ns, new JObj().Set("uart_hub", 1L).Set("nonce", nonce).ToString());
                    // 첫 바이트가 '{' 가 아니면(GET/POST… 등 HTTP, 아무 텍스트) 줄 끝을 기다리지 않고 끊는다(§3.6)
                    int first = rd.PeekByte();
                    if (first != '{') { why = first < 0 ? L.T("hello 없이 끊김", "closed without hello") : L.T("첫 바이트가 '{' 아님(HTTP 등)", "first byte is not '{' (HTTP etc.)"); return; }
                    string hello = rd.ReadLine(MaxHelloLine);
                    string cnonce = HelloOk(hello, nonce);
                    if (cnonce == null) { why = hello == null ? L.T("hello 없이 끊김", "closed without hello") : L.T("hello 불일치(토큰이 다르거나 형식 오류)", "hello mismatch (wrong token or bad format)"); return; }
                    // 기한과 정확히 겹치면 한쪽만 이긴다: 타이머가 먼저면(소켓을 닫음) proof 를 보내지 않고 기한 초과로 끝낸다
                    if (Interlocked.CompareExchange(ref authed, 1, 0) != 0) { why = L.T("hello 기한 " + HelloTimeoutMs + "ms 초과 또는 수신 오류", "hello deadline " + HelloTimeoutMs + "ms passed or receive error"); return; }
                    deadline.Dispose();
                    deadline = null;
                    Interlocked.Decrement(ref unauthenticated);
                    counted = false;
                    c.ReceiveTimeout = 0;
                    Write(ns, new JObj().Set("hello", "ok").Set("proof", Mac("s|" + cnonce)).Set("version", Hub.Version).Set("pid", (long)Environment.ProcessId).ToString());
                    while (!stopping)
                    {
                        string line = rd.ReadLine(MaxLine);
                        if (line == null) return;
                        if (line.Length == 0) continue;
                        JObj resp;
                        try { resp = Handle(line, sock); }
                        catch (Exception e)
                        {
                            hub.Log.Write(L.T("control 처리 오류: " + e.GetType().Name + ": " + e.Message, "control handler error: " + e.GetType().Name + ": " + e.Message));
                            resp = hub.Arbiter.Error(null, 103, "hub_down", L.T("허브 내부 오류: " + e.GetType().Name + ": " + e.Message, "Hub internal error: " + e.GetType().Name + ": " + e.Message));
                        }
                        if (resp == null) return;               // 클라이언트가 떠났다
                        Write(ns, resp.ToString());
                    }
                }
            }
            catch (Exception e)
            {
                /* 연결 오류는 그 연결만 끝낸다 */
                if (Volatile.Read(ref authed) != 1 && why == null)
                    why = e is IOException || e is ObjectDisposedException || e is SocketException ? L.T("hello 기한 " + HelloTimeoutMs + "ms 초과 또는 수신 오류", "hello deadline " + HelloTimeoutMs + "ms passed or receive error") : e.GetType().Name;
            }
            finally
            {
                deadline?.Dispose();
                if (counted) Interlocked.Decrement(ref unauthenticated);
                lock (sync) clients.Remove(c);
                if (why != null && !stopping) hub.Log.WriteThrottled("control-hello|" + why, L.T("control hello 실패 " + peer + ": " + why + " — 응답 없이 끊음", "control hello failed " + peer + ": " + why + " — closed without reply"));
            }
        }

        static readonly Regex NonceRe = new Regex(@"^[0-9a-f]{32,128}$", RegexOptions.CultureInvariant);

        static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

        /// <summary>hex(HMAC-SHA256(token, msg)). msg 접두로 방향을 나눈다("c|" 클라이언트 → 서버, "s|" 서버 → 클라이언트).</summary>
        string Mac(string msg) => Hex(HMACSHA256.HashData(token, Encoding.UTF8.GetBytes(msg)));

        /// <summary>hello 검증: hello == hex(HMAC(token, "c|"+nonce)) 이고 cnonce 가 hex 면 cnonce, 아니면 null(응답 없이 끊는다).</summary>
        string HelloOk(string line, string nonce)
        {
            if (line == null) return null;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
                if (!doc.RootElement.TryGetProperty("hello", out var h) || h.ValueKind != JsonValueKind.String) return null;
                if (!doc.RootElement.TryGetProperty("cnonce", out var cn) || cn.ValueKind != JsonValueKind.String) return null;
                string cnonce = cn.GetString() ?? "";
                byte[] got = Encoding.UTF8.GetBytes(h.GetString() ?? "");
                byte[] want = Encoding.UTF8.GetBytes(Mac("c|" + nonce));
                bool ok = got.Length == want.Length && CryptographicOperations.FixedTimeEquals(got, want);
                return ok && NonceRe.IsMatch(cnonce) ? cnonce : null;
            }
            catch (JsonException) { return null; }
        }

        static void Write(Stream s, string json)
        {
            byte[] b = Encoding.UTF8.GetBytes(json + "\n");
            s.Write(b, 0, b.Length);
            s.Flush();
        }

        /// <summary>상대가 연결을 닫았는가(읽을 것이 없는데 읽기 가능 = EOF).</summary>
        static bool Gone(Socket s)
        {
            try { return s.Poll(0, SelectMode.SelectRead) && s.Available == 0; }
            catch (Exception) { return true; }
        }

        // ================================================================== 요청
        sealed class Args
        {
            public string Cmd;
            public readonly List<string> Pos = new List<string>();
            public readonly Dictionary<string, string> Val = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly HashSet<string> Flag = new HashSet<string>(StringComparer.Ordinal);
            public bool Has(string f) => Flag.Contains(f);
            public string Get(string k) => Val.TryGetValue(k, out var v) ? v : null;
        }

        /// <summary>argv 해석 결과(CLI 공용): 명령·위치 인자·플래그·값 옵션.</summary>
        public sealed class ParsedArgs
        {
            public string Cmd;
            public List<string> Pos;
            public HashSet<string> Flags;
            public Dictionary<string, string> Values;
        }

        /// <summary>
        /// 이 서버와 똑같은 규칙(플래그·값 옵션·"--"·"--k=v")으로 argv 를 해석한다 — CLI 가 C-4(cable 차단)·hub·connect 판정에 쓴다
        /// (CLI 와 서버가 명령을 다르게 읽어 `--json cable …`·`-- cable …` 이 빠져나가지 않게). 해석 오류면 null(서버도 같은 오류로 106).
        /// </summary>
        public static ParsedArgs ParseArgs(IEnumerable<string> argv, out string err) => ParseArgs(argv, out err, null, null);

        /// <summary>
        /// 같은 규칙 + 이 호출에만 더 받는 플래그·값 옵션(CLI 의 events·aging 이 --follow·--kind·--max 를 쓴다, X6). 코어 명령은 이것들을 받지 않는다 —
        /// `run … --follow` 는 Phase A 처럼 106(알 수 없는 옵션).
        /// </summary>
        public static ParsedArgs ParseArgs(IEnumerable<string> argv, out string err, ICollection<string> extraFlags, ICollection<string> extraValued)
        {
            var a = Parse(argv.ToList(), out err, extraFlags, extraValued);
            return a == null ? null : new ParsedArgs { Cmd = a.Cmd, Pos = a.Pos.ToList(), Flags = new HashSet<string>(a.Flag, StringComparer.Ordinal), Values = new Dictionary<string, string>(a.Val, StringComparer.Ordinal) };
        }

        static Args Parse(List<string> argv, out string err, ICollection<string> extraFlags = null, ICollection<string> extraValued = null)
        {
            var a = new Args();
            err = null;
            bool optsDone = false;
            string waiting = null;                    // 값을 기다리는 옵션: 다음 인자는 '-' 로 시작해도 값이다
            foreach (string s in argv)
            {
                if (waiting != null) { a.Val[waiting] = s; waiting = null; continue; }
                if (!optsDone && s == "--") { optsDone = true; continue; }
                if (!optsDone && s.StartsWith("--", StringComparison.Ordinal) && s.Length > 2)
                {
                    string k = s, v = null;
                    int eq = s.IndexOf('=');
                    if (eq > 0) { k = s.Substring(0, eq); v = s.Substring(eq + 1); }
                    if ((Flags.Contains(k) || (extraFlags?.Contains(k) ?? false)) && v == null) { a.Flag.Add(k); continue; }
                    if (Valued.Contains(k) || (extraValued?.Contains(k) ?? false)) { if (v != null) a.Val[k] = v; else waiting = k; continue; }
                    err = L.T("알 수 없는 옵션 " + k, "unknown option " + k);
                    return null;
                }
                if (a.Cmd == null) a.Cmd = s; else a.Pos.Add(s);
            }
            if (waiting != null) { err = L.T(waiting + " 에 값이 없음", waiting + " has no value"); return null; }
            return a;
        }

        JObj Handle(string line, Socket sock)
        {
            var argv = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("argv", out var av) || av.ValueKind != JsonValueKind.Array) return Reject(null, L.T("요청에 argv 배열이 없음", "request has no argv array"));
                foreach (var e in av.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.String) return Reject(null, L.T("argv 는 문자열 배열", "argv must be an array of strings"));
                    argv.Add(e.GetString());
                }
            }
            catch (JsonException e) { return Reject(null, L.T("JSON 파싱 실패: " + e.Message, "JSON parse failed: " + e.Message)); }

            var a = Parse(argv, out string perr);
            if (a == null) return Reject(null, perr);
            if (!TryNum(a, "--timeout", 0.001, 86400, out double timeoutS, out string nerr) || !TryNum(a, "--idle", 0, 86400, out double idleS, out nerr)
                || !TryNum(a, "--tail", 1, 100000, out double tail, out nerr))
                return Reject(null, nerr);

            switch (a.Cmd)
            {
                case null: return Reject(null, L.T("명령 없음 (ls|status|run|send|probe|wait|reboot|log|logpath|hub)", "no command (ls|status|run|send|probe|wait|reboot|log|logpath|hub)"));
                case "ls": return Ls(a.Has("--detail"));
                case "hub": return HubCmd(a);
                case "cable": return CableCmd(a);
                case "events":
                case "aging":
                    return Reject(null, L.T(a.Cmd + " 은 CLI(uart " + a.Cmd + ")가 사건 기록 파일(<log_root>\\events)을 직접 읽는다 — 코어 명령이 아님", a.Cmd + " is handled by the CLI (uart " + a.Cmd + "), which reads the event files (<log_root>\\events) directly — not a core command"));
                case "session": return SessionCmd(a);
            }

            if (a.Pos.Count == 0) return Reject(null, L.T(a.Cmd + ": 대상 <c> 가 필요함", a.Cmd + ": target <c> is required"));
            var c = Resolve(a.Pos[0], out JObj rerr);
            if (c == null) return rerr;
            int need = a.Cmd == "run" || a.Cmd == "send" ? 2 : 1;
            if (a.Pos.Count != need) return Reject(c, L.T(a.Cmd + ": 인자 수가 " + need + "개여야 함(명령은 따옴표로 한 인자)", a.Cmd + ": needs exactly " + need + " argument(s) (quote the command as one argument)"));

            switch (a.Cmd)
            {
                case "status": return Status(c);
                case "logpath": return LogPath(c);
                case "log": return Log(c, a, a.Val.ContainsKey("--tail") ? (int)tail : 50);
                case "wait": return Wait(c, a, timeoutS, sock);
                case "run":
                case "send":
                case "probe":
                case "reboot":
                    var r = new TxRequest
                    {
                        Kind = a.Cmd switch { "run" => TxKind.Run, "send" => TxKind.Send, "probe" => TxKind.Probe, _ => TxKind.Reboot },
                        Cmd = need == 2 ? a.Pos[1] : null,
                        ClientReqId = a.Get("--req-id"),
                        Inline = a.Has("--inline"),
                        NoInterrupt = a.Has("--no-interrupt"),
                        ClearLine = a.Has("--clear-line"),
                        UserOk = a.Has("--user-ok"),
                        TimeoutS = timeoutS,
                        IdleS = a.Val.ContainsKey("--idle") ? idleS : -1,
                    };
                    r.Gate = r.Kind == TxKind.Probe ? TxGate.Probe : a.Has("--force") ? TxGate.Force : r.Kind == TxKind.Send ? TxGate.Token : TxGate.Prompt;
                    if (r.Inline && r.Kind != TxKind.Run) return Reject(c, L.T("--inline 은 run 전용", "--inline is for run only"));
                    if (r.ClientReqId != null && !ReqIdRe.IsMatch(r.ClientReqId)) return Reject(c, L.T("--req-id 는 [A-Za-z0-9._:-] 1~64자", "--req-id must be 1-64 characters of [A-Za-z0-9._:-]"));
                    if (r.Kind == TxKind.Send && r.Cmd.IndexOfAny(new[] { '\r', '\n' }) >= 0) return Reject(c, L.T("send 원문에 CR·LF 금지(한 줄, 개행은 {ENTER})", "send text must not contain CR or LF (one line; use {ENTER} for a newline)"));
                    return Submit(c, r, sock);
                default:
                    return Reject(c, L.T("알 수 없는 명령 '" + a.Cmd + "'", "unknown command '" + a.Cmd + "'"));
            }
        }

        static bool TryNum(Args a, string k, double min, double max, out double v, out string err)
        {
            v = 0; err = null;
            string s = a.Get(k);
            if (s == null) return true;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) || v < min || v > max)
            { err = L.T(k + " 값이 잘못됨: " + s, k + " has a bad value: " + s); return false; }
            return true;
        }

        JObj Reject(Cable c, string msg) => hub.Arbiter.Error(c, 106, "rejected", msg);

        JObj Submit(Cable c, TxRequest r, Socket sock)
        {
            var req = hub.Arbiter.Submit(c, r, out bool replay);
            while (!req.Wait(50))
            {
                if (Gone(sock))
                {
                    if (req.Cancel()) hub.Log.Write(L.T("control 연결 끊김 → 요청 취소(미전송) " + req.ReqId, "control connection lost → request cancelled (not sent) " + req.ReqId));
                    return null;
                }
                if (hub.Clock.MonoMs >= req.DeadlineMs) hub.Arbiter.ExpireIfWaiting(req);
            }
            var res = replay ? Copy(req.Result).Set("replayed", true) : req.Result;
            // F2: run·send·reboot 의 101 이유가 "보드가 아무것도 보내지 않음"일 수 있다(Arbiter 는 동결 — 여기서 덧붙임). probe 의 101(user_ok_required 등)은
            // 그 자체가 "상태 모름 → 사용자 확인" 안내라 덧붙이지 않는다(Phase A 응답 그대로).
            if (r.Kind != TxKind.Probe && NoOutput(c) && ExitOf(res) == 101)
                res = AddRxHint(replay ? res : Copy(res), c);
            return res;
        }

        // ================================================================== F2(X5 부록 B.5): attach 뒤 받은 바이트
        static string NoOutputText => L.T("보드에서 받은 데이터가 없습니다(연결 뒤 0바이트)", "No data received from the board (0 bytes since connecting)");

        /// <summary>붙어 있는 케이블이 이 attach 뒤 받은 바이트(링 End − attach 때 End). 붙어 있지 않으면 -1.</summary>
        static long RxSinceAttach(Cable c)
        {
            var ring = c.Ring;
            if (ring == null || !c.IsAttached) return -1;
            return Math.Max(0, ring.End - c.AttachRxPos);
        }

        static bool NoOutput(Cable c) => RxSinceAttach(c) == 0;

        /// <summary>rx_since_attach(붙어 있을 때) + 0 이면 hint:"no_output"·hint_text. 응답 o 를 바꿔 돌려준다.</summary>
        static JObj AddRxHint(JObj o, Cable c)
        {
            long n = RxSinceAttach(c);
            if (n < 0) return o;
            o.Set("rx_since_attach", n);
            if (n == 0) o.Set("hint", "no_output").Set("hint_text", NoOutputText);
            return o;
        }

        static long ExitOf(JObj o)
        {
            try
            {
                using var d = JsonDocument.Parse(o.ToString());
                return d.RootElement.TryGetProperty("exit", out var x) && x.ValueKind == JsonValueKind.Number ? x.GetInt64() : -1;
            }
            catch (JsonException) { return -1; }
        }

        static JObj Copy(JObj o)
        {
            using var doc = JsonDocument.Parse(o.ToString());
            return HubFiles.ToJObj(doc.RootElement);
        }

        // ================================================================== 에이징 세션(부록 C.12)
        static string SessionUsage => L.T("사용: session start <port|COMx> [이름] · session stop <port|COMx> · session status [--json]", "Usage: session start <port|COMx> [name] · session stop <port|COMx> · session status [--json]");

        /// <summary>
        /// session start|stop|status. 세션은 COM 을 열고 닫지 않는다 — 시작은 지금 연결된 케이블에만(아니면 102 + /connect-uart 안내).
        /// start: 이름(없으면 &lt;보드&gt;_&lt;yyyyMMdd-HHmm&gt;)을 윈도 파일 이름 규칙으로 다듬고 &lt;기준&gt;\에이징\&lt;이름&gt;_&lt;yyyyMMdd-HHmm&gt;\ 를 만든 뒤 sessions.json 에 남긴다.
        /// stop: 세션을 빼고 추적 스레드가 콘솔.log·판정기를 닫을 때까지 기다린 뒤 요약.txt 를 쓴다. status: 세션마다 콘솔.log 크기·줄 수·마지막 출력·이벤트 수·기록 끊김.
        /// </summary>
        JObj SessionCmd(Args a)
        {
            string verb = a.Pos.Count > 0 ? a.Pos[0].ToLowerInvariant() : null;
            DateTime now = hub.Clock.Now;
            switch (verb)
            {
                case "start":
                {
                    if (a.Pos.Count < 2 || a.Pos.Count > 3) return Reject(null, SessionUsage);
                    string t = a.Pos[1];
                    var c = Resolve(t, out JObj rerr);
                    if (c == null && ExitOf(rerr) != 102) return rerr;
                    if (c == null || !c.IsAttached) return NotConnected(c, t);
                    int port = c.TcpPort;
                    if (Sessions.Get(port) is AgingSession ex)
                        return hub.Arbiter.Error(c, 106, "rejected", L.T("포트 " + port + " 은(는) 이미 에이징 중('" + ex.Name + "') — 끝내려면 session stop " + port, "Port " + port + " is already aging ('" + ex.Name + "') — to end it: session stop " + port)).Set("folder", ex.Folder);
                    string board = BoardOf(c);
                    string given = a.Pos.Count == 3 ? a.Pos[2] : null;
                    string name = given != null ? AgingNames.Sanitize(given) : AgingNames.DefaultName(board, c.Com, port, now);
                    if (name == null) return Reject(c, L.T("이름에 쓸 수 있는 글자가 없음: '" + given + "' — 다른 이름을 주거나 이름 없이(기본 이름 <보드>_<시각>)", "The name has no usable characters: '" + given + "' — give another name or none (default name <board>_<time>)"));
                    string root = hub.Config.AgingRoot, folder = null;
                    for (int n = 1; n < 1000; n++)
                    {
                        folder = Path.Combine(root, AgingNames.FolderName(name, now) + (n > 1 ? "_" + n.ToString(CultureInfo.InvariantCulture) : ""));
                        if (!Directory.Exists(folder) && !File.Exists(folder)) break;
                    }
                    var s = new AgingSession { Port = port, Cable = c.Id, Com = c.Com, Board = board, Name = name, Folder = folder, Started = now };
                    try
                    {
                        Directory.CreateDirectory(folder);
                        Directory.CreateDirectory(s.EventsDir);
                        if (!Sessions.Add(s)) return Reject(c, L.T("포트 " + port + " 은(는) 이미 에이징 중", "Port " + port + " is already aging"));
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException)
                    {
                        return Reject(c, L.T("에이징 폴더를 만들지 못함(" + folder + "): " + e.Message, "Could not create the aging folder (" + folder + "): " + e.Message));
                    }
                    hub.Log.Write(L.T("에이징 시작 :" + port + " '" + name + "' " + folder, "Aging started :" + port + " '" + name + "' " + folder));
                    bool synced = Events.WaitSession(port, s, 3000);
                    var o = new JObj().Set("status", "ok").Set("exit", 0L).Set("action", "start");
                    foreach (var kv in Fields(SessionBrief(s, now))) o.Raw(kv.Key, kv.Value);
                    if (given != null && name != given.Trim()) o.Set("name_given", given);
                    o.Set("detect", hub.Config.Events.Enabled).Set("recording", synced);
                    return o;
                }
                case "stop":
                {
                    if (a.Pos.Count != 2) return Reject(null, SessionUsage);
                    int port = SessionPort(a.Pos[1]);
                    var s = port > 0 ? Sessions.Remove(port) : null;
                    if (s == null)
                        return hub.Arbiter.Error(null, 106, "rejected", L.T("'" + a.Pos[1] + "' 은(는) 에이징 중이 아님(session status 로 확인)", "'" + a.Pos[1] + "' is not aging (check with session status)")).Set("aging", false);
                    Events.WaitSession(port, null, 5000);
                    DateTime until = hub.Clock.Now;
                    var evs = AgingSummary.Events(hub.Config.EventsJsonlDir, s, until);
                    string text = AgingSummary.Text(s, evs, until);
                    string err = null;
                    try { File.WriteAllText(s.SummaryPath, text, new UTF8Encoding(false)); }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { err = e.Message; }
                    hub.Log.Write(L.T("에이징 끝 :" + port + " '" + s.Name + "' " + AgingText.Duration((until - s.Started).TotalSeconds) + (err != null ? " — 요약.txt 쓰기 실패: " + err : ""), "Aging ended :" + port + " '" + s.Name + "' " + AgingText.Duration((until - s.Started).TotalSeconds) + (err != null ? " — could not write " + Path.GetFileName(s.SummaryPath) + ": " + err : "")));
                    var o = new JObj().Set("status", "ok").Set("exit", 0L).Set("action", "stop");
                    foreach (var kv in Fields(SessionDetail(s, until, evs))) o.Raw(kv.Key, kv.Value);
                    o.Set("ended", AgingSession.Iso(until)).Set("summary", err == null ? s.SummaryPath : null).Set("summary_text", text);
                    if (err != null) o.Set("summary_error", err);
                    return o;
                }
                case "status":
                {
                    if (a.Pos.Count != 1) return Reject(null, SessionUsage);
                    var list = Sessions.All();
                    return new JObj().Set("status", "ok").Set("exit", 0L).Set("aging_root", hub.Config.AgingRoot).Set("aging_roots", hub.Config.AgingRoots).Set("count", (long)list.Count)
                        .Set("sessions", list.Select(s => SessionDetail(s, now, AgingSummary.Events(hub.Config.EventsJsonlDir, s, now))));
                }
                case "summary":
                    return Reject(null, L.T("session summary 는 CLI(uart session summary)가 사건 기록 파일을 직접 읽는다 — 코어 명령이 아님", "session summary is handled by the CLI (uart session summary), which reads the event files directly — not a core command"));
                default:
                    return Reject(null, SessionUsage);
            }
        }

        /// <summary>연결 안 된 대상의 세션 시작 거부(102): 먼저 /connect-uart 로 연결해야 한다(Claude 는 스스로 연결하지 않음).</summary>
        JObj NotConnected(Cable c, string target)
        {
            string com = c?.Com ?? (ComRe.IsMatch(target) ? target.ToUpperInvariant()
                       : int.TryParse(target, NumberStyles.None, CultureInfo.InvariantCulture, out int p) ? hub.Cables.Snapshot().FirstOrDefault(x => x.TcpPort == p)?.Com : null);
            return hub.Arbiter.Error(c, 102, "no_cable", L.T("에이징을 시작하지 않음 — '" + target + "' 이(가) 연결되어 있지 않음. 먼저 /connect-uart " + (com ?? "COMx")
                                     + " 로 연결해 주세요(Claude 는 스스로 연결하지 않음)",
                                     "Aging not started — '" + target + "' is not connected. Connect it first with /connect-uart " + (com ?? "COMx")
                                     + " (Claude does not connect on its own)")).Set("blocked_reason", "not_connected");
        }

        /// <summary>세션 대상 → 포트: 정수 · 세션의 COM · 세션 이름 · 케이블 해석(alias·보드명). 못 찾으면 0.</summary>
        int SessionPort(string target)
        {
            target = (target ?? "").Trim();
            if (int.TryParse(target, NumberStyles.None, CultureInfo.InvariantCulture, out int p)) return p;
            var all = Sessions.All();
            var hit = all.Where(s => string.Equals(s.Com, target, StringComparison.OrdinalIgnoreCase) || string.Equals(s.Name, target, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hit.Count == 1) return hit[0].Port;
            var c = hub.Cables.Snapshot().Where(x => string.Equals(x.Com, target, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Alias, target, StringComparison.OrdinalIgnoreCase)
                                                   || string.Equals(BoardOf(x), target, StringComparison.OrdinalIgnoreCase)).ToList();
            return c.Count == 1 ? c[0].TcpPort : 0;
        }

        /// <summary>세션 짧은 정보(ls --detail 의 케이블 session·start 응답): 이름·폴더·시작·경과·마지막 출력.</summary>
        JObj SessionBrief(AgingSession s, DateTime now)
        {
            var o = new JObj().Set("port", (long)s.Port).Set("com", s.Com).Set("board", s.Board).Set("name", s.Name).Set("folder", s.Folder)
                .Set("started", AgingSession.Iso(s.Started)).Set("elapsed_s", Math.Round(Math.Max(0, (now - s.Started).TotalSeconds), 1));
            if (s.LastRx is DateTime lr) o.Set("last_rx_age_s", Math.Round(Math.Max(0, (now - lr).TotalSeconds), 1)); else o.Null("last_rx_age_s");
            return o;
        }

        /// <summary>세션 자세한 정보(status·stop): 짧은 정보 + 연결 여부 + 콘솔.log(경로·크기·줄 수) + 이벤트 수(종류별, 의심 따로) + 부팅 수 + 기록 끊김.</summary>
        JObj SessionDetail(AgingSession s, DateTime now, List<EventRec> evs)
        {
            var o = SessionBrief(s, now);
            var cab = hub.Cables.Snapshot().FirstOrDefault(x => x.TcpPort == s.Port);
            o.Set("connected", cab != null && cab.IsAttached).Set("cable", s.Cable);
            if (cab != null && BoardOf(cab) is string b && b != s.Board) o.Set("board_now", b);
            s.ConsoleStats(out long bytes, out long lines);
            o.Set("console_log", new JObj().Set("path", s.ConsolePath).Set("bytes", bytes).Set("lines", lines)).Set("events_dir", s.EventsDir);
            var listed = evs.Where(e => AgingSummary.Listed(e) && e.Kind != "boot").ToList();       // 원인 있는 부팅은 원인 사건이 이미 셈
            var kinds = new JObj();
            foreach (var k in listed.Where(e => e.Suspect == null).GroupBy(e => e.Kind).OrderBy(g => g.Key, StringComparer.Ordinal))
                kinds.Set(k.Key, (long)k.Count());
            o.Set("events", new JObj().Set("total", (long)listed.Count(e => e.Suspect == null)).Set("suspect", (long)listed.Count(e => e.Suspect != null)).Set("by_kind", kinds));
            var boots = evs.Where(e => e.Kind == "boot" && e.Suspect == null).ToList();
            o.Set("boots", new JObj().Set("total", (long)boots.Count).Set("cmd", (long)boots.Count(x => x.Class == "cmd")).Set("soft", (long)boots.Count(x => x.Class == "soft"))
                                     .Set("hard", (long)boots.Count(x => x.Class == "hard")));
            o.Set("gaps", AgingSummary.Gaps(s, evs, now).Select(g => new JObj().Set("from", AgingSession.Iso(g.From.LocalDateTime)).Set("to", AgingSession.Iso(g.To.LocalDateTime))
                                                                        .Set("s", Math.Round((g.To - g.From).TotalSeconds, 1)).Set("reason", g.Why)));
            return o;
        }

        // ================================================================== 대상 해석
        Cable Resolve(string target, out JObj err)
        {
            err = null;
            var all = hub.Cables.Snapshot();
            List<Cable> hit;
            if (int.TryParse(target, NumberStyles.None, CultureInfo.InvariantCulture, out int port))
                hit = all.Where(x => x.TcpPort == port && port > 0).ToList();
            else if (ComRe.IsMatch(target))
                hit = all.Where(x => string.Equals(x.Com, target, StringComparison.OrdinalIgnoreCase)).ToList();
            else
            {
                hit = all.Where(x => string.Equals(x.Alias, target, StringComparison.OrdinalIgnoreCase)).ToList();
                if (hit.Count == 0) hit = all.Where(x => string.Equals(BoardOf(x), target, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            if (hit.Count == 1)
            {
                // C-3: 확인 대기·제외·설정 제외 케이블은 COM 을 열지 않았다 → 102 + blocked_reason(SKILL 은 대신 답하지 않고 사용자에게 알린다)
                var one = hit[0];
                string why = one.Status switch { CableStatus.Pending => "pending", CableStatus.Released => "released", CableStatus.Ignored => "ignored", _ => null };
                if (why == null) return one;
                string how = why == "ignored" ? L.T("config.json 이 제외함(mode: ignore)", "excluded by config.json (mode: ignore)")
                           : hub.Config.OnDemand ? L.T("연결 안 됨 — 사용자가 /connect-uart " + (one.Com ?? "COMx") + " 로 연결해야 함(Claude 는 스스로 연결하지 않음)", "not connected — the user must connect it with /connect-uart " + (one.Com ?? "COMx") + " (Claude does not connect on its own)")
                           : why == "pending" ? L.T("새 케이블 확인 창에 답하거나 트레이 '케이블 설정'에서 정해야 함", "answer the new cable prompt or decide in the tray 'Cable settings'")
                           : L.T("트레이 '케이블 설정'에서 제외됨([해제]로 다시 붙음)", "excluded in the tray 'Cable settings' ([Include again] reattaches it)");
                err = hub.Arbiter.Error(one, 102, "no_cable", L.T("대상 '" + target + "' 케이블을 쓰지 않음(" + why + ") — " + how, "Target '" + target + "' cable is not in use (" + why + ") — " + how)).Set("blocked_reason", why);
                return null;
            }
            if (hit.Count == 0)
            {
                err = hub.Arbiter.Error(null, 102, "no_cable", L.T("대상 '" + target + "' 에 맞는 케이블 없음", "No cable matches target '" + target + "'"));
                return null;
            }
            err = hub.Arbiter.Error(null, 105, "ambiguous", L.T("대상 '" + target + "' 이(가) " + hit.Count + "개에 맞음 — 포트 번호로 지정", "Target '" + target + "' matches " + hit.Count + " cables — use the port number"))
                .Set("candidates", hit.Select(x => new JObj().Set("port", (long)x.TcpPort).Set("com", x.Com).Set("alias", x.Alias).Set("board", BoardOf(x)).Set("cable", x.Id)));
            return null;
        }

        string BoardOf(Cable c) => c.Board ?? hub.Tracker(c)?.Snapshot().Board;

        // ================================================================== 조회 명령
        /// <param name="detail">ls --detail·status: description(장치 이름)·mode_explicit(config.json 의 cables.&lt;id&gt;.mode 가 있으면 그 값, 없으면 null)을 더한다.</param>
        JObj CableInfo(Cable c, bool detail)
        {
            var o = new JObj();
            hub.Arbiter.AddCable(o, c, c.Ring != null ? c.Ring.Format(c.Ring.End) : null);
            o.Set("alias", c.Alias).Set("status", hub.CableStatusText(c)).Set("baud", (long)c.Baud);
            o.Set("telnet", hub.Telnet.StateOf(c)).Set("telnet_clients", (long)hub.Telnet.ClientCount(c));
            if (hub.Telnet.BindError(c) is string be) o.Set("telnet_error", be);
            var t = hub.Tracker(c);
            if (t != null)
            {
                var sn = t.Snapshot();
                o.Set("state", sn.StateName).Set("blocked_reason", sn.BlockedReason);
            }
            else o.Null("state").Null("blocked_reason");
            o.Set("queue", (long)hub.Arbiter.Pending(c)).Set("rx_bytes", c.RxBytes).Set("tx_bytes", c.TxBytes);
            if (detail && Sessions.Get(c.TcpPort) is AgingSession ss)                // 부록 C.12: 트레이 정보 줄·아이콘·터미널 제목(감독이 1초마다 읽음 — 가벼운 값만)
                o.Set("session", SessionBrief(ss, hub.Clock.Now));
            if (detail)
            {
                var cc = hub.Config.CableFor(c.Id);
                o.Set("description", c.Description);
                if (cc.ModeSet) o.Set("mode_explicit", cc.Mode); else o.Null("mode_explicit");
                AddRxHint(o, c);                                            // F2: ls --detail·status 에 rx_since_attach(+0 이면 hint)
            }
            return o;
        }

        /// <param name="detail">--detail: 케이블마다 description·mode_explicit(트레이·감독용). 기본 ls 의 케이블 필드는 Phase A 그대로.</param>
        JObj Ls(bool detail)
        {
            var o = new JObj().Set("status", "ok").Set("exit", 0L);
            o.Set("hub", new JObj().Set("version", Hub.Version).Set("pid", (long)Environment.ProcessId).Set("control_port", (long)Port));
            return o.Set("cables", hub.Cables.Snapshot().Select(c => CableInfo(c, detail)));
        }

        JObj Status(Cable c)
        {
            var o = new JObj().Set("status", "ok").Set("exit", 0L);
            foreach (var kv in Fields(CableInfo(c, true))) o.Raw(kv.Key, kv.Value);
            o.Raw("status", Json.Str("ok"));
            o.Set("cable_status", hub.CableStatusText(c));
            var t = hub.Tracker(c);
            if (t != null)
            {
                var sn = t.Snapshot();
                o.Set("since_ms", Math.Max(0, hub.Clock.MonoMs - sn.SinceMs)).Set("run_active", sn.RunActive).Set("run_id", sn.RunId)
                 .Set("human_typing", sn.HumanTyping).Set("orphaned", sn.Orphaned).Set("human_clients", (long)sn.HumanClients)
                 .Set("unknown", sn.Unknown).Set("probe_needs_user_ok", sn.ProbeNeedsUserOk).Set("kernel_phase", sn.KernelPhase)
                 .Set("restored_from", sn.RestoredFrom?.ToString().ToLowerInvariant()).Set("last_lines", sn.LastLines.Select(RxText.FromLatin1));
            }
            return o.Set("log", c.Writer?.CurrentLogPath);
        }

        static IEnumerable<KeyValuePair<string, string>> Fields(JObj o)
        {
            using var doc = JsonDocument.Parse(o.ToString());
            return doc.RootElement.EnumerateObject().Select(p => new KeyValuePair<string, string>(p.Name, p.Value.GetRawText())).ToList();
        }

        JObj LogPath(Cable c)
        {
            var o = hub.Arbiter.Resp("ok", 0, c, c.Ring?.Format(c.Ring.End));
            return o.Set("log", c.Writer?.CurrentLogPath).Set("raw", c.Writer?.CurrentRawPath).Set("idx", c.Writer?.CurrentIdxPath);
        }

        JObj Log(Cable c, Args a, int tail)
        {
            var ring = c.Ring;
            if (ring == null) return hub.Arbiter.Error(c, 102, "no_cable", L.T("기록하지 않는 케이블(" + c.StatusText + ")", "cable is not being recorded (" + c.StatusText + ")"));
            string since = a.Get("--since");
            if (since != null)
            {
                var e = SinceStart(c, since, out long from);
                if (e != null) return e;
                long to = ring.End;
                var buf = new byte[to - from];
                int n = ring.Read(from, buf, 0, buf.Length);
                if (n < 0) return SinceExpired(c, since);
                int skip = Math.Max(0, n - MaxOutChars);
                var lines = RxText.Lines(buf, skip, n - skip);
                var o = hub.Arbiter.Resp("ok", 0, c, ring.Format(from + n)).Set("out", lines.Lines).Set("kernel", lines.Kernel).Set("trailing_newline", lines.TrailingNewline);
                if (skip > 0) o.Set("truncated", true);
                return o;
            }
            string path = c.Writer?.CurrentLogPath;
            var outL = new List<string>();
            if (path != null && File.Exists(path))
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                long start = Math.Max(0, fs.Length - Math.Min(MaxOutChars, (long)tail * 1024 + 4096));
                fs.Position = start;
                var b = new byte[fs.Length - start];
                int got = 0;
                while (got < b.Length) { int k = fs.Read(b, got, b.Length - got); if (k <= 0) break; got += k; }
                var all = Encoding.UTF8.GetString(b, 0, got).Split('\n').Select(x => x.TrimEnd('\r')).ToList();
                if (start > 0 && all.Count > 0) all.RemoveAt(0);                 // 잘린 첫 줄
                if (all.Count > 0 && all[all.Count - 1].Length == 0) all.RemoveAt(all.Count - 1);
                outL = all.Skip(Math.Max(0, all.Count - tail)).ToList();
            }
            return hub.Arbiter.Resp("ok", 0, c, ring.Format(ring.End)).Set("log", path).Set("out", outL);
        }

        /// <summary>
        /// C-4 케이블 판정(트레이 전용 — cli 는 보내지 않는다, 클라이언트 쪽 제한): cable decide &lt;id&gt; board|ignore · cable exclude &lt;id&gt; · cable include &lt;id&gt;
        /// · cable baud &lt;id&gt; &lt;rate&gt;(X5 /connect-uart COMx &lt;baud&gt; — 감독의 connect 흐름이 보낸다).
        /// &lt;id&gt; 는 케이블 ID(이번 코어 실행에서 본 것). config.json 의 mode: ignore 케이블은 106.
        /// </summary>
        JObj CableCmd(Args a)
        {
            const string Usage = "cable decide <id> board|ignore | cable exclude <id> | cable include <id> | cable baud <id> <rate> | cable remembered | cable forget <id>";
            string verb = a.Pos.Count > 0 ? a.Pos[0] : null;
            if (verb == "remembered" || verb == "forget") return ForgetCmd(verb, a, Usage);
            if (verb != "decide" && verb != "exclude" && verb != "include" && verb != "baud") return Reject(null, Usage);
            if (a.Pos.Count != (verb == "decide" || verb == "baud" ? 3 : 2)) return Reject(null, Usage);
            var c = hub.Cables.Find(a.Pos[1]);
            if (c == null) return hub.Arbiter.Error(null, 102, "no_cable", L.T("케이블 '" + a.Pos[1] + "' 없음(이번 허브 실행에서 보이지 않음)", "No cable '" + a.Pos[1] + "' (not seen in this hub run)"));
            if (c.Mode == "ignore") return Reject(c, L.T("config.json 이 제외함(cables." + c.Id + ".mode = ignore) — 트레이에서 바꿀 수 없음", "excluded by config.json (cables." + c.Id + ".mode = ignore) — cannot be changed from the tray"));
            bool pending = c.TcpPort == 0;
            if (verb == "baud") return BaudCmd(c, a.Pos[2]);
            if (verb == "decide")
            {
                string ans = a.Pos[2];
                if (ans != "board" && ans != "ignore") return Reject(c, Usage);
                if (!pending || !hub.Cables.Decide(c, ans == "board", out int port))
                    return Reject(c, L.T("확인 대기(pending) 케이블이 아님(" + c.StatusText + ") — cable exclude|include 를 쓴다", "not a pending cable (" + c.StatusText + ") — use cable exclude|include"));
                return hub.Arbiter.Resp("ok", 0, c, null).Set("port", (long)port).Set("decision", ans);   // 다음 스캔이 새 객체로 붙이거나(board) released 로 만든다
            }
            if (pending) return Reject(c, L.T("확인 대기(pending) 케이블 — cable decide " + c.Id + " board|ignore 로 답한다", "pending cable — answer with cable decide " + c.Id + " board|ignore"));
            bool ok = verb == "exclude" ? hub.Cables.Exclude(c) : hub.Cables.Include(c);
            if (!ok) return Reject(c, L.T(verb + " 할 수 없는 케이블(" + c.StatusText + ")", "cannot " + verb + " this cable (" + c.StatusText + ")"));
            return hub.Arbiter.Resp("ok", 0, c, c.Ring?.Format(c.Ring.End)).Set("cable_status", c.StatusText);
        }

        /// <summary>
        /// cable baud &lt;id&gt; &lt;rate&gt;: 표준 보오만(Config.StandardBauds, 아니면 106). 메모리 설정·케이블 객체를 바꾸고(붙어 있으면 닫았다가 다음 스캔에 새 보오로 다시 엶 —
        /// CableManager.SetBaud) &lt;hub_dir&gt;\config.json 의 cables.&lt;id&gt;.baud 에 저장한다(Config.WithCableBaud — 다른 키 그대로, C-12). 저장 실패는 persisted:false
        /// + persist_error(이번 실행에는 적용됨). 응답 baud·old_baud·reopened·persisted.
        /// </summary>
        JObj BaudCmd(Cable c, string rateText)
        {
            if (!int.TryParse(rateText, NumberStyles.None, CultureInfo.InvariantCulture, out int rate) || !Config.IsStandardBaud(rate))
                return Reject(c, L.T("baud 는 " + Config.StandardBaudText + " 중 하나: '" + rateText + "'", "baud must be one of " + Config.StandardBaudText + ": '" + rateText + "'"));
            bool changed = hub.Cables.SetBaud(c.Id, rate, out int old, out bool reopened);
            // 메모리 값이 이미 같아도 파일이 다르면(사람이 고친 뒤 등) 파일을 맞춘다 — 글자로 고친 결과가 지금 파일과 같으면 쓰지 않는다
            string err = null;
            bool persisted = false, wrote = false;
            string path = HubFiles.ConfigPath(hub.Config.EffectiveHubDir);
            try
            {
                string text = File.Exists(path) ? HubFiles.ReadShared(path) : null;
                if (File.Exists(path) && text == null) err = L.T("config.json 을 읽지 못함", "could not read config.json");
                else
                {
                    string n = Config.WithCableBaud(text, c.Id, rate, out string why);
                    if (n == null) err = why;
                    else if (n == text) persisted = true;
                    else if (HubFiles.TryWriteAtomic(path, n)) persisted = wrote = true;
                    else err = L.T("config.json 쓰기 실패", "config.json write failed");
                }
            }
            catch (Exception e) { err = e.GetType().Name + ": " + e.Message; }
            if (changed || wrote || err != null)
                hub.Log.Write(L.T("cable baud " + c.Id + " " + old + " -> " + rate + (wrote ? " — config.json cables." + c.Id + ".baud 저장" : persisted ? " — config.json 이미 같음" : " — config.json 저장 못 함(" + err + "), 이번 실행에만"), "cable baud " + c.Id + " " + old + " -> " + rate + (wrote ? " — saved to config.json cables." + c.Id + ".baud" : persisted ? " — config.json already the same" : " — could not save to config.json (" + err + "), this run only")));
            var o = hub.Arbiter.Resp("ok", 0, c, c.Ring?.Format(c.Ring.End)).Set("baud", (long)rate).Set("old_baud", (long)old).Set("reopened", reopened).Set("persisted", persisted);
            if (err != null) o.Set("persist_error", err);
            return o;
        }

        /// <summary>
        /// 미연결 UART 정리(트레이 전용): cable remembered = 꽂혀 있지 않은 기억된 케이블 목록 [{cable, port, board, com}],
        /// cable forget &lt;id&gt; = 그 기록을 state.json 에서 지움(꽂혀 있으면 106, 아무것도 바꾸지 않음).
        /// </summary>
        JObj ForgetCmd(string verb, Args a, string usage)
        {
            if (verb == "remembered")
            {
                if (a.Pos.Count != 1) return Reject(null, usage);
                return new JObj().Set("status", "ok").Set("exit", 0L)
                    .Set("cables", hub.Cables.Remembered().Select(r => new JObj().Set("cable", r.Id).Set("port", (long)r.Port).Set("board", r.Board).Set("com", r.Com)));
            }
            if (a.Pos.Count != 2) return Reject(null, usage);
            if (!hub.Cables.Forget(a.Pos[1], out int port, out string why, out bool noRecord))
                return noRecord ? hub.Arbiter.Error(null, 102, "no_cable", L.T("케이블 '" + a.Pos[1] + "' 의 기록 없음", "No record of cable '" + a.Pos[1] + "'"))
                                          : Reject(null, L.T("케이블 '" + a.Pos[1] + "' 은 " + why + " — 꽂혀 있는 UART 는 정리하지 않음", "Cable '" + a.Pos[1] + "' is " + why + " — plugged-in UARTs are not cleaned up"));
            return new JObj().Set("status", "ok").Set("exit", 0L).Set("cable", a.Pos[1]).Set("port", (long)port);
        }

        JObj HubCmd(Args a)
        {
            string verb = a.Pos.Count > 0 ? a.Pos[0] : null;
            if (verb == "status")
            {
                var o = new JObj().Set("status", "ok").Set("exit", 0L).Set("version", Hub.Version).Set("pid", (long)Environment.ProcessId)
                    .Set("control_port", (long)Port).Set("uptime_s", Math.Round((DateTime.Now - started).TotalSeconds, 1))
                    .Set("cables", (long)hub.Cables.Snapshot().Count).Set("log_root", hub.Config.LogRoot);
                if (!string.IsNullOrEmpty(hub.RestartReason)) o.Set("restart_reason", hub.RestartReason);
                o.Set("profile", ProductProfile.Current.StatusJson());                       // 제품 프로필(없으면 null, 깨졌으면 error)
                JObj sv = null;
                try { sv = SupervisorStatus?.Invoke(); }
                catch (Exception e) { hub.Log.Write(L.T("감독 상태 읽기 실패: " + e.GetType().Name + ": " + e.Message, "Supervisor status read failed: " + e.GetType().Name + ": " + e.Message)); }
                return sv == null ? o.Null("supervisor") : o.Set("supervisor", sv);
            }
            if (verb == "stop" || verb == "start" || verb == "upgrade" || verb == "language")
            {
                if (verb == "language" && a.Pos.Count != 2) return Reject(null, "hub language auto|ko|en");
                var h = HubCommandHandler;
                if (h != null) return h(verb, a.Pos.Skip(1).ToArray());
                return new JObj().Set("status", "unsupported").Set("exit", 106L).Set("error", L.T("hub " + verb + " 는 감독(uart_hub.ps1)이 처리한다 — 이 코어는 감독과 연결되지 않음", "hub " + verb + " is handled by the supervisor (uart_hub.ps1) — this core is not connected to a supervisor"));
            }
            return Reject(null, "hub status|stop|start|upgrade|language");
        }

        // ================================================================== wait
        JObj SinceStart(Cable c, string since, out long from)
        {
            from = 0;
            var ring = c.Ring;
            if (!RingBuffer.TryParse(since, out long ep, out long off)) return Reject(c, L.T("--since 형식은 epoch:offset", "--since format is epoch:offset"));
            if (ep != ring.Epoch || off < ring.Start) return SinceExpired(c, since);
            if (off > ring.End) return Reject(c, L.T("--since 가 현재 위치(" + ring.Format(ring.End) + ")보다 뒤", "--since is past the current position (" + ring.Format(ring.End) + ")"));
            from = off;
            return null;
        }

        JObj SinceExpired(Cable c, string since)
            => hub.Arbiter.Error(c, 106, "since_expired", L.T("--since " + since + " 는 링 버퍼 범위(" + c.Ring.Format(c.Ring.Start) + "~) 밖이거나 다른 허브 실행(epoch)의 값", "--since " + since + " is outside the ring buffer (" + c.Ring.Format(c.Ring.Start) + "~) or from another hub run (epoch)"));

        JObj Wait(Cable c, Args a, double timeoutS, Socket sock)
        {
            var ring = c.Ring;
            if (ring == null) return hub.Arbiter.Error(c, 102, "no_cable", L.T("기록하지 않는 케이블(" + c.StatusText + ")", "cable is not being recorded (" + c.StatusText + ")"));
            string pat = a.Get("--pattern");
            if (string.IsNullOrEmpty(pat)) return Reject(c, L.T("wait: --pattern 필요", "wait: --pattern is required"));
            Regex re;
            try { re = new Regex(pat, RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)); }
            catch (ArgumentException e) { return Reject(c, L.T("정규식 오류: " + e.Message, "regex error: " + e.Message)); }
            long from = ring.End;
            string since = a.Get("--since");
            if (since != null)
            {
                var e = SinceStart(c, since, out from);
                if (e != null) return e;
            }
            double waitS = timeoutS > 0 ? timeoutS : hub.Config.DefaultTimeoutS;
            long deadline = hub.Clock.MonoMs + (long)(waitS * 1000);
            bool nudge = a.Has("--nudge");
            var cur = ring.OpenCursor(from);
            var norm = new RxText.Normalizer();
            var sb = new StringBuilder();
            int scanned = 0, nudges = 0;
            TxRequest nudgeReq = null;
            while (true)
            {
                for (int k = 0; ; k++)
                {
                    var rd = cur.Next(k == 0 ? 50 : 0);
                    if (rd.Status == RingReadStatus.Lost) return SinceExpired(c, since ?? ring.Format(from));
                    if (rd.Status != RingReadStatus.Data) break;
                    norm.Feed(rd.Data, 0, rd.Data.Length, sb);
                    if (sb.Length - scanned > (1 << 20)) break;
                }
                if (sb.Length > scanned)
                {
                    string text = sb.ToString();
                    Match m;
                    try { m = re.Match(text, Math.Max(0, scanned - 65536)); }
                    catch (RegexMatchTimeoutException) { return Reject(c, L.T("정규식 시간 초과(2초)", "regex timed out (2 s)")); }
                    scanned = text.Length;
                    if (m.Success)
                    {
                        int lineEnd = text.IndexOf('\n', m.Index + m.Length);
                        string upto = text.Substring(0, lineEnd < 0 ? text.Length : lineEnd);
                        bool trunc = upto.Length > MaxOutChars;
                        if (trunc) upto = upto.Substring(upto.Length - MaxOutChars);
                        var o = hub.Arbiter.Resp("ok", 0, c, ring.Format(cur.Position)).Set("match", m.Value).Set("out", upto.Split('\n')).Set("nudges", (long)nudges);
                        if (trunc) o.Set("truncated", true);
                        return o;
                    }
                }
                long now = hub.Clock.MonoMs;
                if (now >= deadline)
                {
                    var lines = sb.ToString().Split('\n').Where(x => x.Trim().Length > 0).ToList();
                    return hub.Arbiter.Error(c, 100, "timeout", L.T("패턴 /" + pat + "/ 이 " + waitS + "초 안에 나오지 않음", "pattern /" + pat + "/ did not appear within " + waitS + " s"))
                        .Set("rx_offset", ring.Format(cur.Position)).Set("last_lines", lines.Skip(Math.Max(0, lines.Count - 3))).Set("nudges", (long)nudges);
                }
                if (Gone(sock)) return null;
                if (nudge && nudges < 3 && (nudgeReq == null || nudgeReq.IsDone))
                {
                    // --nudge(§3.3 probe 와 같은 규칙): UNKNOWN 또는 커널 타임스탬프 이후 BOOTING + RX 무음 + 사람 유휴일 때만 CR 1회.
                    // 부록 C.13 ③(auto_nudge): RX 전체 무음 대신 커널 줄 무음 — 앱 상태 줄이 계속 흐르는 보드도 깨운다(auto_nudge:false 면 지금 그대로)
                    var t = hub.Tracker(c);
                    bool relaxed = hub.Config.AutoNudge;
                    if (t != null && (relaxed ? t.CanNudge(now, (long)(hub.Config.IdleS * 1000), out _) : t.CanProbe(now, false, out _)))
                    {
                        nudgeReq = hub.Arbiter.Submit(c, new TxRequest { Kind = TxKind.Probe, Gate = TxGate.Probe, Internal = true, TimeoutS = 5, Nudge = relaxed }, out _);
                        nudges++;
                    }
                }
            }
        }
    }
}
