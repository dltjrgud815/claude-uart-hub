// TelnetServer(계획 §3.5, Phase A — 접속 상태 배너·접속 재생·CPR 단일 전달은 Phase B).
// 케이블마다 127.0.0.1:<TCP포트> 를 첫 attach 때 열고 코어가 끝날 때(hub stop·코어 종료)까지 유지한다. 여러 클라이언트가 같은 콘솔을 본다.
//   케이블 분리·재연결은 연결을 끊지 않는다(TeraTerm 은 끊기면 창을 닫는다, 실보드 11-03). 대신 클라이언트에게만 한 줄 배너(ASCII):
//     분리 "\r\n*** uart-hub: cable detached (COMx) - replug to resume ***\r\n", 재연결 "\r\n*** uart-hub: cable attached (COMx) ***\r\n".
//     배너는 RX 순서 자리에 끼운다(분리 = 그 세션 마지막 바이트 뒤, 재연결 = Cable.AttachRxPos 앞) — 출력 중복·누락 없음.
//     배너는 링·.raw·.log·PromptTracker 에 들어가지 않는다(클라이언트 송신 큐에만).
//   분리 중 접속한 클라이언트는 받아 들이고 분리 배너를 보낸다. 분리 중 입력은 버린다(큐에 쌓지 않음) — 클라이언트마다 detach 한 번에 안내 1회.
//   늦은 attach/detach 통지(케이블 잠금 밖이라 순서가 바뀔 수 있음)는 AttachSeq 와 케이블의 현재 세션(Cable.Sync 안에서 읽음)으로 가린다.
//   RX: 케이블 링을 구독하는 fanout 스레드가 클라이언트별 송신 큐(상한 client_queue_mb, 기본 1MB)에 넣기만 한다.
//       넘치면 그 클라이언트만 끊고 기록한다. 느린 클라이언트가 기록 코어·다른 클라이언트를 막지 않는다(P1). 출력의 0xFF 는 FF FF.
//   TX: 사람 입력은 Cable.Write(…, Human) 한 경로로 바로 나간다(Arbiter 큐를 거치지 않음, §3.3). 터미널 CPR 응답(ESC[r;cR)은 TerminalReply.
//       IAC 협상·NOP·SB 는 여기서 소비되어 COM 에 가지 않는다(사람 입력이 아님, §3.2).
//   협상: 접속 즉시 WILL ECHO, WILL SGA, DO SGA, WILL BINARY, DO BINARY(S0-3 TeraTerm 응답 확인). 협상 응답(WILL/WONT/DO/DONT)
//         전 입력은 버리고, telnet_negotiation_ms(2초) 동안 응답이 없으면 raw 클라이언트(nc 등)로 보고 입력을 허용한다.
//         human_clients(PromptTracker.SetHumanClientCount)는 입력이 허용된 클라이언트 수다.
//   보안: 연결의 첫 바이트가 "GET "/"POST "/"PUT "/"OPTIONS "/"HEAD " 이면 즉시 끊는다(브라우저 cross-protocol, §6).
//   bind 실패(포트 선점 등): 케이블 상태를 bind_failed 로 보이고 telnet_bind_retry_ms 마다 다시 시도한다. 기록은 영향 없음.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace UartHub
{
    public sealed class TelnetServer
    {
        internal const byte IAC = 255, DONT = 254, DO = 253, WONT = 252, WILL = 251, SB = 250, IP = 244, BRK = 243, SE = 240;
        internal const byte OptBinary = 0, OptEcho = 1, OptSga = 3;
        /// <summary>접속 즉시 보내는 협상(§3.5): WILL ECHO, WILL SGA, DO SGA, WILL BINARY, DO BINARY.</summary>
        public static readonly byte[] Greeting = { IAC, WILL, OptEcho, IAC, WILL, OptSga, IAC, DO, OptSga, IAC, WILL, OptBinary, IAC, DO, OptBinary };
        const int BreakMs = 300;                     // .tx {BREAK}·send 와 같은 길이
        const int SendBufferBytes = 65536;
        static readonly string[] HttpHeads = { "GET ", "POST ", "PUT ", "OPTIONS ", "HEAD " };
        static readonly Regex Cpr = new Regex(@"\x1b\[\d+;\d+R", RegexOptions.CultureInvariant);

        readonly Hub hub;
        readonly object sync = new object();
        readonly Dictionary<string, Endpoint> eps = new Dictionary<string, Endpoint>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, long> detachedSeq = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);   // 처리한 detach 의 최대 AttachSeq
        bool stopped;

        /// <summary>시험 훅: attach 통지가 서버 단 늦은 통지 검사를 지난 뒤 endpoint 반영 직전(통지 스레드). 그 사이 detach 경합 재현용. 운영에서는 null.</summary>
        public Action<Cable> TestBeforeEndpointAttach;

        /// <summary>fanout 이 attach 통지 반영을 기다리는 최대 시간(ms, 기본 1초). 시험이 지연 경합을 여유 있게 재현하도록 늘릴 수 있다.</summary>
        public int AttachNoticeWaitMs = 1000;

        public TelnetServer(Hub hub)
        {
            this.hub = hub;
            hub.Cables.CableAttached += OnAttached;
            hub.Cables.CableDetached += OnDetached;
        }

        /// <summary>"listening" / "bind_failed" / "closed"(분리·정지·아직 시도 전).</summary>
        public string StateOf(Cable c)
        {
            var ep = Find(c);
            if (ep == null) return "closed";
            if (ep.BindError != null) return "bind_failed";
            return ep.Listening ? "listening" : "closed";
        }

        public bool IsBindFailed(Cable c) => Find(c)?.BindError != null;
        public string BindError(Cable c) => Find(c)?.BindError;
        /// <summary>연결된 클라이언트 수(협상 중 포함).</summary>
        public int ClientCount(Cable c) => Find(c)?.Count ?? 0;

        Endpoint Find(Cable c)
        {
            if (c == null) return null;
            lock (sync) return eps.TryGetValue(c.Id, out var e) ? e : null;
        }

        /// <summary>
        /// attach 통지(seq = AttachSeq). 첫 attach 면 endpoint 를 만들어 연다(이후 코어 수명 동안 유지), 아니면 재연결 배너.
        /// 이미 detach 를 처리한 세션의 늦은 통지는 무시한다(첫 attach 가 늦은 통지면 열지 않는다 — 다음 attach 에서 연다).
        /// </summary>
        void OnAttached(Cable c, long seq)
        {
            if (c.TcpPort <= 0 || c.Ring == null) return;
            Endpoint ep;
            bool created = false;
            lock (sync)
            {
                if (stopped) return;
                if (detachedSeq.TryGetValue(c.Id, out long ds) && seq <= ds) ep = null;
                else if (!eps.TryGetValue(c.Id, out ep)) { ep = new Endpoint(this, c); eps[c.Id] = ep; created = true; }
            }
            if (ep == null) { hub.Log.Write(L.T("telnet: 늦은 attach 이벤트 무시 " + c.Id + " seq=" + seq, "telnet: late attach event ignored " + c.Id + " seq=" + seq)); return; }
            TestBeforeEndpointAttach?.Invoke(c);
            bool applied = ep.Attached(seq);
            if (!created) return;
            if (!applied)
            {
                lock (sync) { if (eps.TryGetValue(c.Id, out var cur) && ReferenceEquals(cur, ep)) eps.Remove(c.Id); }
                return;
            }
            ep.Start();
        }

        /// <summary>detach 통지(seq = 끝난 세션의 AttachSeq). endpoint·클라이언트는 그대로 두고 분리 배너만 낸다(늦은 통지는 Endpoint 가 가린다).</summary>
        void OnDetached(Cable c, long seq)
        {
            Endpoint ep;
            lock (sync)
            {
                if (!detachedSeq.TryGetValue(c.Id, out long ds) || seq > ds) detachedSeq[c.Id] = seq;
                eps.TryGetValue(c.Id, out ep);
            }
            ep?.Detached(seq);
        }

        public void Stop()
        {
            List<Endpoint> all;
            lock (sync)
            {
                stopped = true;
                all = eps.Values.ToList();
                eps.Clear();
            }
            foreach (var e in all) e.Stop();
        }

        // ================================================================== 케이블 하나
        sealed class Endpoint
        {
            readonly Hub hub;
            readonly TelnetServer srv;
            public readonly Cable Cable;
            readonly object l = new object();
            readonly ManualResetEventSlim stopEvt = new ManualResetEventSlim(false);
            volatile Client[] clients = new Client[0];     // copy-on-write: fanout 은 잠금 없이 읽는다
            TcpListener listener;
            Thread acceptTh, fanTh;
            volatile bool stop;
            int active;
            long seq;

            // ---- 케이블 세션(l 로 보호, attached 는 입력 경로가 잠금 없이 먼저 본다)
            sealed class Mark { public long Pos; public byte[] Banner; public bool Detach; }
            readonly List<Mark> marks = new List<Mark>();   // fanout 이 RX 순서 자리(Pos)에 끼울 배너
            volatile bool attached;
            long attachSeq;                                 // 반영한 attach 세션(Cable.AttachSeq)
            long detachSeen;                                // 받은 detach 통지의 최대 seq(무시한 것 포함) — 그 세션의 늦은 attach 를 거른다
            int detachGen;                                  // detach 마다 1 증가: 분리 중 입력 안내는 클라이언트당 detach 한 번에 1회
            string com;

            public volatile string BindError;
            public volatile bool Listening;
            public int Count => clients.Length;
            Config Cfg => hub.Config;

            public Endpoint(TelnetServer srv, Cable c) { this.srv = srv; hub = srv.hub; Cable = c; }

            static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);
            static byte[] DetachBanner(string com) => Ascii("\r\n*** uart-hub: cable detached (" + com + ") - replug to resume ***\r\n");
            static byte[] AttachBanner(string com) => Ascii("\r\n*** uart-hub: cable attached (" + com + ") ***\r\n");
            static byte[] DroppedBanner(string com) => Ascii("\r\n*** uart-hub: cable detached (" + com + ") - input discarded ***\r\n");

            /// <summary>
            /// attach 반영. 케이블의 현재 세션이 그 seq 일 때만 반영한다(케이블 잠금 안에서 확인 — 그 사이 더 새 세션이 시작된 늦은 통지는 거름,
            /// 그 세션의 통지가 따로 온다. 이미 끝난 세션이면 곧 올 detach 통지가 분리 배너를 낸다).
            /// 첫 반영이면 배너 없음(아직 클라이언트가 없다), 그 뒤로는 Cable.AttachRxPos 자리에 재연결 배너. 반영했으면 true.
            /// </summary>
            public bool Attached(long s)
            {
                bool live;
                long pos;
                string c;
                lock (Cable.Sync) { live = Cable.AttachSeq == s; pos = Cable.AttachRxPos; c = Cable.Com; }
                bool first;
                lock (l)
                {
                    if (stop || s <= attachSeq) return false;
                    if (!live || s <= detachSeen) { hub.Log.Write(L.T("telnet: 늦은 attach 이벤트 무시 " + Cable.Id + " seq=" + s + " (케이블은 이미 더 새 세션이거나 이 세션의 detach 를 받음)", "telnet: late attach event ignored " + Cable.Id + " seq=" + s + " (the cable is already in a newer session or got this session's detach)")); return false; }
                    first = attachSeq == 0;
                    attachSeq = s;
                    attached = true;
                    com = c;
                    if (!first) marks.Add(new Mark { Pos = pos, Banner = AttachBanner(c) });
                }
                if (!first) hub.Log.Write(L.T("telnet :" + Cable.TcpPort + " 케이블 재연결 " + c + " — 클라이언트 " + clients.Length + "개에 재연결 배너, 출력 재개", "telnet :" + Cable.TcpPort + " cable reconnected " + c + " — reconnect banner to " + clients.Length + " client(s), output resumed"));
                return true;
            }

            /// <summary>
            /// detach 반영: 연결은 그대로 두고 그 세션 마지막 RX 뒤 자리에 분리 배너. 더 새 세션을 이미 반영했거나(seq 작음) 케이블이 이미
            /// 재attach 됐으면(그 attach 통지가 재연결 배너를 낸다) 늦은 통지라 무시한다.
            /// </summary>
            public void Detached(long s)
            {
                long live, pos;
                lock (Cable.Sync) { live = Cable.AttachSeq; pos = Cable.Ring.End; }
                lock (l)
                {
                    if (s > detachSeen) detachSeen = s;
                    if (stop || !attached) return;
                    if (s < attachSeq || live != s)
                    {
                        hub.Log.Write(L.T("telnet: 늦은 detach 이벤트 무시 " + Cable.Id + " seq=" + s + " (현재 세션 " + Math.Max(attachSeq, live) + ")", "telnet: late detach event ignored " + Cable.Id + " seq=" + s + " (current session " + Math.Max(attachSeq, live) + ")"));
                        return;
                    }
                    attached = false;
                    detachGen++;
                    marks.Add(new Mark { Pos = pos, Banner = DetachBanner(com), Detach = true });
                }
                hub.Log.Write(L.T("telnet :" + Cable.TcpPort + " 케이블 분리 " + com + " — 연결 유지(클라이언트 " + clients.Length + "개에 분리 배너, 분리 중 입력은 버림)", "telnet :" + Cable.TcpPort + " cable detached " + com + " — connections kept (detach banner to " + clients.Length + " client(s), input discarded while detached)"));
            }

            /// <summary>
            /// 분리 중 입력이면 true(버림, 큐에 쌓지 않음). 이 클라이언트가 이번 detach 에서 처음 친 것이면 안내 배너 1회.
            /// </summary>
            public bool DropInput(Client c, int n)
            {
                if (attached) return false;
                bool notice = false;
                lock (l)
                {
                    if (attached || stop) return !attached;
                    if (c.NoticeGen != detachGen) { c.NoticeGen = detachGen; notice = true; c.EnqueueBanner(DroppedBanner(com)); }
                }
                if (notice) hub.Log.Write(L.T("telnet :" + Cable.TcpPort + " " + c.Name + " 케이블 분리 중 입력 " + n + "B 버림(안내 1회, 이후 이번 분리 동안은 조용히 버림)", "telnet :" + Cable.TcpPort + " " + c.Name + " input while cable detached, " + n + "B discarded (notice once, then silently discarded until reattach)"));
                return true;
            }

            /// <summary>
            /// upTo 이하 자리의 배너 중 가장 앞(같은 자리는 넣은 순서). 없으면 null. 배너를 받을 클라이언트 목록도 같은 잠금 안에서 잡는다:
            /// 그 사이 접속한 클라이언트는 Accept 가 "보낼 분리 배너 없음"을 보고 제 몫의 분리 배너를 받으므로, 두 번 받지 않는다.
            /// </summary>
            Mark TakeMark(long upTo, out Client[] to)
            {
                lock (l)
                {
                    to = clients;
                    int best = -1;
                    for (int i = 0; i < marks.Count; i++)
                        if (marks[i].Pos <= upTo && (best < 0 || marks[i].Pos < marks[best].Pos)) best = i;
                    if (best < 0) return null;
                    var m = marks[best];
                    marks.RemoveAt(best);
                    return m;
                }
            }

            static void Banner(byte[] b, Client[] to)
            {
                foreach (var c in to) c.EnqueueBanner(b);
            }

            long gaveUpSeq;                                 // fanout 전용: attach 통지를 기다리다 포기한 세션

            /// <summary>
            /// 이 청크가 아직 반영하지 않은 attach 세션의 바이트면(리더가 attach 통지보다 먼저 읽음) 통지가 반영될 때까지 잠깐(최대 1초) 미룬다 —
            /// 재연결 배너가 그 세션 첫 바이트 앞에 오게. 통지가 끝내 안 오면(늦은 통지로 걸러짐) 그 세션은 배너 없이 보낸다.
            /// </summary>
            void WaitAttachApplied(long offset)
            {
                int waitMs = srv.AttachNoticeWaitMs;
                long deadline = Environment.TickCount64 + waitMs;
                while (!stop)
                {
                    long liveSeq, livePos, applied;
                    lock (Cable.Sync) { liveSeq = Cable.AttachSeq; livePos = Cable.AttachRxPos; }
                    lock (l) applied = attachSeq;
                    if (liveSeq <= applied || liveSeq <= gaveUpSeq || offset < livePos) return;
                    if (Environment.TickCount64 >= deadline)
                    {
                        gaveUpSeq = liveSeq;
                        hub.Log.Write(L.T("telnet :" + Cable.TcpPort + " attach 통지(seq " + liveSeq + ") " + waitMs + "ms 지연 — 재연결 배너 없이 출력 재개", "telnet :" + Cable.TcpPort + " attach notice (seq " + liveSeq + ") " + waitMs + "ms late — output resumed without a reconnect banner"));
                        return;
                    }
                    Thread.Sleep(5);
                }
            }

            /// <summary>to = 받을 클라이언트(배너와 같은 스냅샷이면 배너 앞뒤 바이트가 같은 클라이언트에 간다). null 이면 지금 목록.</summary>
            void Send(byte[] d, int off, int n, long limit, Client[] to = null)
            {
                if (n <= 0) return;
                byte[] part = off == 0 && n == d.Length ? d : d.AsSpan(off, n).ToArray();
                foreach (var c in to ?? clients)
                    if (!c.EnqueueData(part, limit))
                        Gone(c, "send queue > " + limit + " bytes", true);
            }

            public void Start()
            {
                acceptTh = new Thread(AcceptLoop) { IsBackground = true, Name = "Telnet-accept-" + Cable.TcpPort };
                fanTh = new Thread(FanLoop) { IsBackground = true, Name = "Telnet-fanout-" + Cable.TcpPort };
                fanTh.Start();
                acceptTh.Start();
            }

            public void Stop()
            {
                Client[] cs;
                lock (l)
                {
                    if (stop) return;
                    stop = true;
                    stopEvt.Set();
                    try { listener?.Stop(); } catch (Exception) { }
                    cs = clients;
                }
                foreach (var c in cs) Gone(c, L.T("telnet 종료(허브 정지)", "telnet closed (hub stopping)"));
                if (acceptTh != null && acceptTh != Thread.CurrentThread) acceptTh.Join(1000);
                if (fanTh != null && fanTh != Thread.CurrentThread) fanTh.Join(1000);
                Listening = false;
            }

            void AcceptLoop()
            {
                try { AcceptLoopCore(); }
                catch (Exception e) { hub.Log.Write(L.T("telnet :" + Cable.TcpPort + " accept 스레드 오류(telnet 중단, 기록은 계속): " + e.GetType().Name + ": " + e.Message, "telnet :" + Cable.TcpPort + " accept thread error (telnet stopped, recording continues): " + e.GetType().Name + ": " + e.Message)); }
            }

            void AcceptLoopCore()
            {
                string lastErr = null;
                while (!stop)
                {
                    var ls = new TcpListener(IPAddress.Loopback, Cable.TcpPort) { ExclusiveAddressUse = true };
                    try { ls.Start(); }
                    catch (SocketException e)
                    {
                        try { ls.Stop(); } catch (Exception) { }
                        string err = e.SocketErrorCode + ": " + e.Message;
                        Listening = false;
                        BindError = err;
                        if (err != lastErr)
                            hub.Log.Write(L.T("telnet bind 실패 127.0.0.1:" + Cable.TcpPort + " (" + Cable.Id + ") " + err + " → bind_failed, 기록은 계속, "
                                          + Cfg.TelnetBindRetryMs + "ms 마다 재시도",
                                          "telnet bind failed 127.0.0.1:" + Cable.TcpPort + " (" + Cable.Id + ") " + err + " → bind_failed, recording continues, retry every "
                                          + Cfg.TelnetBindRetryMs + "ms"));
                        lastErr = err;
                        stopEvt.Wait(Cfg.TelnetBindRetryMs);
                        continue;
                    }
                    lock (l)
                    {
                        if (stop) { try { ls.Stop(); } catch (Exception) { } return; }
                        listener = ls;
                    }
                    BindError = null;
                    Listening = true;
                    lastErr = null;
                    hub.Log.Write("telnet 127.0.0.1:" + Cable.TcpPort + " listen (" + Cable.Id + ")");
                    while (!stop)
                    {
                        TcpClient tc;
                        try { tc = ls.AcceptTcpClient(); }
                        catch (Exception e)
                        {
                            if (stop) return;
                            hub.Log.Write(L.T("telnet accept 오류 :" + Cable.TcpPort + " " + e.GetType().Name + ": " + e.Message + " → 다시 listen", "telnet accept error :" + Cable.TcpPort + " " + e.GetType().Name + ": " + e.Message + " → listening again"));
                            try { ls.Stop(); } catch (Exception) { }
                            Listening = false;
                            stopEvt.Wait(200);
                            break;
                        }
                        Accept(tc);
                    }
                }
            }

            void Accept(TcpClient tc)
            {
                Client c;
                try { c = new Client(this, tc, Interlocked.Increment(ref seq)); }
                catch (Exception e)
                {
                    hub.Log.Write(L.T("telnet 접속 준비 실패 :" + Cable.TcpPort + " " + e.GetType().Name + ": " + e.Message, "telnet connection setup failed :" + Cable.TcpPort + " " + e.GetType().Name + ": " + e.Message));
                    try { tc.Close(); } catch (Exception) { }
                    return;
                }
                bool detachedNow;
                lock (l)
                {
                    if (stop) { c.Close(); return; }
                    clients = clients.Append(c).ToArray();     // 협상 바이트는 이미 큐 맨 앞(fanout 보다 먼저 나간다)
                    // 분리 중 접속: 분리 배너(아직 fanout 이 내보내지 않은 분리 배너가 있으면 그것이 이 클라이언트에도 간다)
                    detachedNow = !attached;
                    if (detachedNow && !marks.Any(m => m.Detach)) c.EnqueueBanner(DetachBanner(com));
                }
                hub.Log.Write(L.T("telnet :" + Cable.TcpPort + " 접속 " + c.Name + (detachedNow ? " (케이블 분리 중)" : ""), "telnet :" + Cable.TcpPort + " connected " + c.Name + (detachedNow ? " (cable detached)" : "")));
                c.Start();
            }

            /// <summary>클라이언트 입력이 허용됨(협상 응답 또는 raw 판정) → 사람 클라이언트 수 갱신.</summary>
            public void Activate(Client c, string how)
            {
                lock (l)
                {
                    if (c.Removed || c.Active) return;
                    c.Active = true;
                    active++;
                    hub.Tracker(Cable)?.SetHumanClientCount(active);
                }
                hub.Log.Write(L.T("telnet :" + Cable.TcpPort + " " + c.Name + " 입력 허용(" + how + ")", "telnet :" + Cable.TcpPort + " " + c.Name + " input allowed (" + how + ")"));
            }

            /// <summary>클라이언트 제거(한 번만). 입력이 허용됐던 클라이언트면 사람 클라이언트 수를 줄인다.</summary>
            public void Gone(Client c, string reason, bool cableEvent = false)
            {
                lock (l)
                {
                    if (c.Removed) return;
                    c.Removed = true;
                    clients = clients.Where(x => x != c).ToArray();
                    if (c.Active)
                    {
                        active--;
                        hub.Tracker(Cable)?.SetHumanClientCount(active);
                    }
                }
                c.Close();
                hub.Log.Write(L.T("telnet :" + Cable.TcpPort + " " + c.Name + " 끊김: " + reason, "telnet :" + Cable.TcpPort + " " + c.Name + " disconnected: " + reason));
                if (cableEvent) Cable.PostEvent("*** telnet client " + c.Name + " dropped (" + reason + ") ***");
            }

            /// <summary>
            /// RX fanout: 링 구독 → 클라이언트별 큐에 넣기만 한다(막지 않음). 큐 상한을 넘는 클라이언트만 끊는다.
            /// 분리·재연결 배너는 RX 순서 자리(Mark.Pos)에 끼운다: 그 자리까지의 RX 를 넣은 뒤 배너. 커서는 케이블 수명 동안 하나라 재연결 뒤에도
            /// 이어서(Cable.AttachRxPos 부터) 중복·누락 없이 보낸다.
            /// </summary>
            void FanLoop()
            {
                var ring = Cable.Ring;
                var cur = ring.OpenCursor(ring.End);
                long limit = Cfg.ClientQueueBytes;
                while (!stop)
                {
                    try
                    {
                        for (var m = TakeMark(cur.Position, out var to); m != null; m = TakeMark(cur.Position, out to)) Banner(m.Banner, to);
                        var r = cur.Next(200);
                        switch (r.Status)
                        {
                            case RingReadStatus.Data:
                            {
                                WaitAttachApplied(r.Offset);
                                int done = 0;
                                for (var m = TakeMark(r.Offset + r.Data.Length, out var to); m != null; m = TakeMark(r.Offset + r.Data.Length, out to))
                                {
                                    int cut = (int)Math.Clamp(m.Pos - r.Offset, done, r.Data.Length);
                                    Send(r.Data, done, cut - done, limit, to);           // 배너 앞 바이트도 배너와 같은 스냅샷에(사이에 접속한 클라이언트가 배너 뒤 옛 세션 꼬리를 받지 않게)
                                    done = cut;
                                    Banner(m.Banner, to);
                                }
                                Send(r.Data, done, r.Data.Length - done, limit);
                                break;
                            }
                            case RingReadStatus.Lost:
                                hub.Log.Write(L.T("telnet fanout :" + Cable.TcpPort + " 링 손실 " + r.Skipped + "B", "telnet fanout :" + Cable.TcpPort + " ring loss " + r.Skipped + "B"));
                                break;
                            case RingReadStatus.Closed:
                                return;
                        }
                    }
                    catch (Exception e)
                    {
                        hub.Log.Write(L.T("telnet fanout :" + Cable.TcpPort + " 오류: " + e.GetType().Name + ": " + e.Message, "telnet fanout :" + Cable.TcpPort + " error: " + e.GetType().Name + ": " + e.Message));
                        Thread.Sleep(50);
                    }
                }
            }

            // ============================================================== 클라이언트 하나
            internal sealed class Client
            {
                enum St { Data, Iac, Opt, Sb, SbIac }

                readonly Endpoint ep;
                readonly TcpClient tc;
                readonly Socket sock;
                public readonly string Name;
                readonly object q = new object();
                readonly Queue<(byte[] Data, bool Ctl)> items = new Queue<(byte[], bool)>();
                long queued;
                bool closed;
                Thread rd, wr;

                public bool Active, Removed;               // ep.l 로 보호
                public int NoticeGen;                      // ep.l 로 보호: 분리 중 입력 안내를 보낸 detach 순번
                volatile bool raw;                         // 협상 무응답 → raw: 출력 0xFF 를 이스케이프하지 않는다

                // 입력 파서 상태(읽기 스레드 전용)
                St st = St.Data;
                byte verb;
                bool lastCr, negotiated, inputOk;
                List<byte> head = new List<byte>(8);       // HTTP 첫 줄 판정이 끝나면 null
                long discarded;
                // 옵션 상태: 0 no, 1 yes, 2 우리가 요청하고 응답 대기(Q method 간략판)
                readonly byte[] us = new byte[256], him = new byte[256];

                public Client(Endpoint ep, TcpClient tc, long id)
                {
                    this.ep = ep;
                    this.tc = tc;
                    sock = tc.Client;
                    sock.NoDelay = true;
                    sock.SendBufferSize = SendBufferBytes;
                    Name = (sock.RemoteEndPoint?.ToString() ?? "?") + "#" + id.ToString(CultureInfo.InvariantCulture);
                    us[OptEcho] = us[OptSga] = us[OptBinary] = 2;
                    him[OptSga] = him[OptBinary] = 2;
                    items.Enqueue((Greeting, true));
                }

                public void Start()
                {
                    wr = new Thread(WriteLoop) { IsBackground = true, Name = "Telnet-wr-" + Name };
                    rd = new Thread(ReadLoop) { IsBackground = true, Name = "Telnet-rd-" + Name };
                    wr.Start();
                    rd.Start();
                }

                public void Close()
                {
                    lock (q)
                    {
                        if (closed) return;
                        closed = true;
                        items.Clear();
                        queued = 0;
                        Monitor.PulseAll(q);
                    }
                    try { sock.Shutdown(SocketShutdown.Both); } catch (Exception) { }
                    try { tc.Close(); } catch (Exception) { }
                }

                /// <summary>fanout 스레드에서 호출. 상한을 넘으면 false(호출자가 이 클라이언트만 끊는다). 막지 않는다.</summary>
                public bool EnqueueData(byte[] d, long limit)
                {
                    lock (q)
                    {
                        if (closed) return true;
                        if (queued + d.Length > limit) return false;
                        items.Enqueue((d, false));
                        queued += d.Length;
                        Monitor.Pulse(q);
                        return true;
                    }
                }

                /// <summary>허브 안내 배너(ASCII, 0xFF 없음). 출력 순서대로 나가며 송신 큐 상한에 세지 않는다(작다).</summary>
                public void EnqueueBanner(byte[] b)
                {
                    lock (q)
                    {
                        if (closed) return;
                        items.Enqueue((b, true));
                        Monitor.Pulse(q);
                    }
                }

                void EnqueueCtl(byte v, byte opt)
                {
                    lock (q)
                    {
                        if (closed) return;
                        items.Enqueue((new[] { IAC, v, opt }, true));
                        Monitor.Pulse(q);
                    }
                }

                void WriteLoop()
                {
                    var buf = new List<byte>(65536);
                    try
                    {
                        while (true)
                        {
                            buf.Clear();
                            lock (q)
                            {
                                while (items.Count == 0 && !closed) Monitor.Wait(q);
                                if (closed) return;
                                bool esc = !raw;
                                while (items.Count > 0 && buf.Count < 65536)
                                {
                                    var it = items.Dequeue();
                                    if (!it.Ctl) queued -= it.Data.Length;
                                    if (it.Ctl || !esc) buf.AddRange(it.Data);
                                    else foreach (byte b in it.Data) { buf.Add(b); if (b == IAC) buf.Add(IAC); }
                                }
                            }
                            var arr = buf.ToArray();
                            int off = 0;
                            while (off < arr.Length) off += sock.Send(arr, off, arr.Length - off, SocketFlags.None);
                        }
                    }
                    catch (Exception e) { ep.Gone(this, L.T("송신 실패 " + e.GetType().Name, "send failed " + e.GetType().Name)); }
                }

                void ReadLoop()
                {
                    var buf = new byte[4096];
                    long t0 = Environment.TickCount64;
                    string why = L.T("연결 종료", "connection closed");
                    try
                    {
                        while (!closed && !ep.stop)
                        {
                            if (!negotiated && !raw && Environment.TickCount64 - t0 >= ep.Cfg.TelnetNegotiationMs)
                            {
                                raw = true;
                                inputOk = true;
                                ep.Activate(this, L.T("raw: " + ep.Cfg.TelnetNegotiationMs + "ms 동안 협상 응답 없음" + (discarded > 0 ? ", 그 전 입력 " + discarded + "B 버림" : ""), "raw: no negotiation reply for " + ep.Cfg.TelnetNegotiationMs + "ms" + (discarded > 0 ? ", earlier input " + discarded + "B discarded" : "")));
                            }
                            if (!sock.Poll(50 * 1000, SelectMode.SelectRead)) continue;
                            int n = sock.Receive(buf, 0, buf.Length, SocketFlags.None);
                            if (n <= 0) break;
                            if (!Feed(buf, n)) { why = L.T("HTTP 형태의 첫 줄 → 차단", "HTTP-like first line → blocked"); break; }
                        }
                    }
                    catch (Exception e) { why = L.T("수신 " + e.GetType().Name, "receive " + e.GetType().Name); }
                    ep.Gone(this, why);
                }

                /// <summary>false = HTTP 첫 줄(즉시 끊기).</summary>
                bool Feed(byte[] b, int n)
                {
                    if (head != null)
                    {
                        for (int i = 0; i < n; i++) head.Add(b[i]);
                        int v = HttpVerdict(head);
                        if (v > 0) return false;
                        if (v == 0) return true;                 // 아직 모름: 더 받는다
                        var h = head.ToArray();
                        head = null;
                        Process(h, h.Length);
                        return true;
                    }
                    Process(b, n);
                    return true;
                }

                /// <summary>1 = HTTP 메서드로 시작, 0 = 판정 보류(접두가 같음), -1 = 아님.</summary>
                static int HttpVerdict(List<byte> h)
                {
                    if (h.Count == 0) return 0;
                    if (h[0] == IAC) return -1;
                    bool maybe = false;
                    foreach (var m in HttpHeads)
                    {
                        int k = Math.Min(m.Length, h.Count);
                        bool same = true;
                        for (int i = 0; i < k && same; i++) same = h[i] == (byte)m[i];
                        if (!same) continue;
                        if (h.Count >= m.Length) return 1;
                        maybe = true;
                    }
                    return maybe ? 0 : -1;
                }

                void Process(byte[] b, int n)
                {
                    var data = new List<byte>(n);
                    for (int i = 0; i < n; i++)
                    {
                        byte x = b[i];
                        switch (st)
                        {
                            case St.Data:
                                if (x == IAC) st = St.Iac; else Data(x, data);
                                break;
                            case St.Iac:
                                st = St.Data;
                                switch (x)
                                {
                                    case IAC: Data(0xFF, data); break;
                                    case WILL: case WONT: case DO: case DONT: verb = x; st = St.Opt; break;
                                    case SB: st = St.Sb; break;
                                    case IP: Data(0x03, data); break;          // Interrupt Process → ^C
                                    case BRK:
                                        if (inputOk)
                                        {
                                            Flush(data);
                                            if (!ep.DropInput(this, 1)) ep.Cable.SendBreak(BreakMs, human: true);
                                        }
                                        break;
                                    default: break;                            // NOP, DM, AYT, GA, SE …: 무시(사람 입력 아님)
                                }
                                break;
                            case St.Opt:
                                st = St.Data;
                                Negotiate(verb, x);
                                break;
                            case St.Sb:
                                if (x == IAC) st = St.SbIac;
                                break;
                            case St.SbIac:
                                st = x == SE ? St.Data : St.Sb;                 // IAC IAC = SB 안의 0xFF
                                break;
                        }
                    }
                    Flush(data);
                }

                /// <summary>데이터 바이트 하나: CR LF / CR NUL → CR, 입력 허용 전이면 버린다.</summary>
                void Data(byte x, List<byte> data)
                {
                    if (lastCr && (x == 0x0A || x == 0x00)) { lastCr = false; return; }
                    lastCr = x == 0x0D;
                    if (inputOk) data.Add(x); else discarded++;
                }

                /// <summary>모은 입력을 COM 으로: CPR 응답은 TerminalReply, 나머지는 Human(§3.2). 케이블 분리 중이면 버린다(큐에 쌓지 않음).</summary>
                void Flush(List<byte> data)
                {
                    if (data.Count == 0) return;
                    var arr = data.ToArray();
                    data.Clear();
                    if (ep.DropInput(this, arr.Length)) return;
                    string s = Encoding.Latin1.GetString(arr);
                    int pos = 0;
                    foreach (Match m in Cpr.Matches(s))
                    {
                        if (m.Index > pos) ep.Cable.Write(arr, pos, m.Index - pos, TxSource.Human);
                        ep.Cable.Write(arr, m.Index, m.Length, TxSource.TerminalReply);
                        pos = m.Index + m.Length;
                    }
                    if (pos < arr.Length) ep.Cable.Write(arr, pos, arr.Length - pos, TxSource.Human);
                }

                void Negotiate(byte v, byte opt)
                {
                    if (!negotiated)
                    {
                        negotiated = true;
                        if (!raw)
                        {
                            inputOk = true;
                            ep.Activate(this, L.T("telnet 협상 응답" + (discarded > 0 ? ", 그 전 입력 " + discarded + "B 버림" : ""), "telnet negotiation reply" + (discarded > 0 ? ", earlier input " + discarded + "B discarded" : "")));
                        }
                    }
                    bool local = opt == OptEcho || opt == OptSga || opt == OptBinary;   // 우리가 켜 줄 수 있는 옵션(WILL)
                    bool remote = opt == OptSga || opt == OptBinary;                    // 상대에게 켜 달라는 옵션(DO)
                    switch (v)
                    {
                        case DO:
                            if (us[opt] == 1) break;
                            if (us[opt] == 2) { us[opt] = 1; break; }
                            if (local) { us[opt] = 1; EnqueueCtl(WILL, opt); }
                            else EnqueueCtl(WONT, opt);
                            break;
                        case DONT:
                            if (us[opt] == 1) EnqueueCtl(WONT, opt);
                            us[opt] = 0;
                            break;
                        case WILL:
                            if (him[opt] == 1) break;
                            if (him[opt] == 2) { him[opt] = 1; break; }
                            if (remote) { him[opt] = 1; EnqueueCtl(DO, opt); }
                            else EnqueueCtl(DONT, opt);
                            break;
                        case WONT:
                            if (him[opt] == 1) EnqueueCtl(DONT, opt);
                            him[opt] = 0;
                            break;
                    }
                }
            }
        }
    }
}
