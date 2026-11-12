// Arbiter(계획 §3.3): 케이블별 TX 단일 큐. 사람(telnet)은 큐를 거치지 않고 Cable.Write(Human)로 바로 나가며(최우선),
// 큐의 Claude·spool 요청은 게이트(상태 + 사람 유휴)가 열릴 때만 나간다. 케이블당 레인 스레드 하나가 요청을 하나씩 실행한다.
// run 봉인(§3.4): printf B; sh -c '…' </dev/null; printf E $? — 마커는 추적기가 RX 에서 위치와 무관하게 찾고(RunMarkerSeen),
// 프롬프트 확정(PromptConfirmed)과 함께 ok / interrupted / stranded / timeout 을 가른다. 직렬화 잠금은 추적기의 run 플래그다
// (E 뒤 프롬프트 확정으로 추적기가 스스로 풀고, stranded·timeout 잔여 잠금은 늦은 E 또는 사람 ^C/CR 뒤 프롬프트 확정으로 푼다).
// 요청 수명: 절대 deadline(지나면 절대 보내지 않음), 송신 전 취소(Cancel 성공 = 미전송 보장), --req-id 멱등.
// 잠금 순서: 요청 잠금 → Cable TX 잠금 → 추적기 잠금(TX 잠금 안의 로그 이벤트는 큐 잠금, 말단). 레인 잠금을 쥔 채로 Cable.Write 를 부르지 않는다
// (사람 송신 스레드가 TX 잠금 안에서 Transmitted 처리기를 부르므로). Transmitted 처리기는 잠금 없이 카운터만 올린다.
// P2 경합 창: 게이트 판정 때 Cable.HumanTxSeq 를 잡아 두고(사람 유휴도 Cable.LastHumanTxMs 로 함께 본다), 모든 Claude·spool·probe 바이트는
// Cable.TryWriteIf(…, 그 순번) 로만 쓴다. 비교와 쓰기가 TX 잠금 안에서 원자적이라 판정 뒤·조각 사이의 사람 입력에 섞이지 않는다.
// 아무것도 못 보냈으면 요청은 다시 대기(게이트 재판정, probe 는 101), 일부를 보낸 뒤면 나머지를 버리고 interrupted(101).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace UartHub
{
    /// <summary>송신 게이트(§3.3 표).</summary>
    public enum TxGate
    {
        /// <summary>AT_PROMPT + 사람 유휴: run·reboot·push 청크·.tx 일반 줄.</summary>
        Prompt,
        /// <summary>AT_PROMPT·AT_UBOOT·BOOTING + 사람 유휴 + 실행 중 명령 없음: send·.tx 토큰 단독 줄·{NONL} 줄.</summary>
        Token,
        /// <summary>상태 무관, 사람 유휴만: --force(로그 [claude-force]).</summary>
        Force,
        /// <summary>probe·nudge: PromptTracker.CanProbe. 기다리지 않는다(차례가 왔을 때 판정, 불가면 101).</summary>
        Probe,
    }

    public enum TxKind { Run, Send, Probe, Reboot }

    /// <summary>중재 요청 한 건. 제출 전에 채우고 Arbiter.Submit 에 넘긴다. 결과는 Wait 뒤 Result/Exit.</summary>
    public sealed class TxRequest
    {
        public TxKind Kind;
        public TxGate Gate;
        /// <summary>클라이언트 --req-id(멱등키). null 이면 내부 id 를 쓴다.</summary>
        public string ClientReqId;
        /// <summary>run: 명령 / send: 원문(.tx 토큰 문법).</summary>
        public string Cmd;
        public bool Inline, NoInterrupt, ClearLine, UserOk;
        public TxSource Source = TxSource.Claude;
        /// <summary>요청 수명(초). 0 이하 → default_timeout_s.</summary>
        public double TimeoutS;
        /// <summary>사람 유휴 조건(초). 음수 → idle_s.</summary>
        public double IdleS = -1;
        /// <summary>내부 요청(nudge)은 멱등 저장소·로그 태그에서 구분한다.</summary>
        public bool Internal;
        /// <summary>probe 게이트를 자동 깨우기 조건(커널 줄 무음, 부록 C.13 ③)으로 본다 — wait --nudge(auto_nudge 일 때).</summary>
        public bool Nudge;

        public string ReqId { get; internal set; }
        public string MarkerId { get; internal set; }
        internal byte[] SealedLine;
        internal string CableId;
        internal long DeadlineMs, IdleMs;
        internal long BootWaitSinceMs = long.MinValue;  // C.13 ④: booting·unknown·재부팅 잠금 때문에 기다리기 시작한 때(최대 30초)
        internal bool ClearDone;
        internal string ClearedText;
        /// <summary>게이트 판정 때 잡은 Cable.HumanTxSeq. 이 요청의 바이트는 이 값이 그대로일 때만 나간다.</summary>
        internal long HumanSeq;

        readonly object l = new object();
        int phase;                                   // 0 대기, 1 송신 시작, 2 완료, 3 취소
        bool cancelAsked;                            // 송신 시작 뒤 취소 요청(재대기로 돌아가면 그때 취소한다)
        readonly ManualResetEventSlim done = new ManualResetEventSlim(false);

        public JObj Result { get; private set; }
        public int Exit { get; private set; }
        public bool IsDone => done.IsSet;
        public bool Cancelled { get { lock (l) return phase == 3; } }

        public bool Wait(int ms) => done.Wait(ms);

        /// <summary>송신 전이면 취소하고 true(= 이 요청의 바이트는 COM 에 나가지 않았고 나가지 않는다).</summary>
        public bool Cancel()
        {
            lock (l)
            {
                if (phase == 1) cancelAsked = true;
                if (phase != 0) return false;
                phase = 3;
                Result = new JObj().Set("status", "cancelled").Set("exit", 103L).Set("req_id", ReqId);
                Exit = 103;
            }
            done.Set();
            return true;
        }

        internal bool TryBeginSend()
        {
            lock (l)
            {
                if (phase != 0) return false;
                phase = 1;
                return true;
            }
        }

        /// <summary>
        /// 송신 시작을 되돌린다(조건부 쓰기가 첫 바이트부터 막혀 아무것도 보내지 않음 → 다시 대기). 그 사이 취소 요청이 왔으면
        /// 지금 취소로 끝내고 false(미전송 보장은 그대로).
        /// </summary>
        internal bool UndoBeginSend()
        {
            lock (l)
            {
                if (phase != 1) return false;
                if (!cancelAsked) { phase = 0; return true; }
                phase = 3;
                Result = new JObj().Set("status", "cancelled").Set("exit", 103L).Set("req_id", ReqId);
                Exit = 103;
            }
            done.Set();
            return false;
        }

        /// <summary>송신 전 상태에서만 a 를 실행한다(--clear-line 의 ^U 처럼 본 송신 전 바이트). 취소와 배타적.</summary>
        internal bool WhilePending(Action a)
        {
            lock (l)
            {
                if (phase != 0) return false;
                a();
                return true;
            }
        }

        internal void Complete(JObj r, int exit)
        {
            lock (l)
            {
                if (phase == 2 || phase == 3) return;
                phase = 2;
                Result = r;
                Exit = exit;
            }
            done.Set();
        }
    }

    public sealed class Arbiter
    {
        const int CleanupWaitMs = 3000;              // timeout 정리 ^C 뒤 프롬프트 복귀 확인 창
        const int ProbeWaitMs = 3000;                // probe CR 뒤 프롬프트 확정 대기(상태 보고용)
        const int ProbeKillGapMs = 30;               // probe 바이트(^E·^U·CR) 사이(셸이 줄 끝 이동·지우기를 먼저 처리하게)
        static readonly byte[] ProbeBytes = { 0x05, 0x15, (byte)'\r' };   // ^E(줄 끝) → ^U(지우기) → CR
        const int MaxCmdBytes = 900;                 // 이스케이프 뒤 상한(busybox 1.36.1 줄 편집 실측 867B 동작)
        const int ReqStoreMax = 512;
        static readonly Regex InlineHead = new Regex(@"^(cd|export|unset|umask|alias)(\s|$)", RegexOptions.CultureInvariant);
        static readonly Regex InlinePromptVar = new Regex(@"^(export|unset)\s.*\b(PS1|PS2|PROMPT_COMMAND)\b", RegexOptions.CultureInvariant);

        readonly Hub hub;
        readonly object sync = new object();
        readonly Dictionary<string, Lane> lanes = new Dictionary<string, Lane>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, TxRequest> byReq = new Dictionary<string, TxRequest>(StringComparer.Ordinal);
        readonly Queue<string> reqOrder = new Queue<string>();
        readonly Random rnd = new Random();
        long counter;
        bool stopped;

        public Arbiter(Hub hub) { this.hub = hub; }

        /// <summary>시험 훅: 게이트 판정 뒤 Claude·spool·probe 바이트를 쓰기 직전(인자: 케이블, 요청, 이 요청이 이미 보낸 바이트 수). 운영에서는 null.</summary>
        public Action<Cable, TxRequest, int> TestBeforeWrite;

        internal long Now => hub.Clock.MonoMs;
        Config Cfg => hub.Config;

        /// <summary>
        /// 요청 제출. 같은 --req-id 가 이미 있으면(취소된 것 제외) 그 요청을 돌려주고 replay=true(재송신 없음).
        /// 검증 실패(106)·케이블 없음(102)은 즉시 완료된 요청으로 돌려준다.
        /// </summary>
        public TxRequest Submit(Cable cable, TxRequest r, out bool replay)
        {
            replay = false;
            Lane lane;
            lock (sync)
            {
                if (r.ClientReqId != null && byReq.TryGetValue(r.ClientReqId, out var prev))
                {
                    if (!prev.Cancelled) { replay = true; return prev; }
                    byReq.Remove(r.ClientReqId);
                }
                counter++;
                r.MarkerId = "u" + counter.ToString(CultureInfo.InvariantCulture) + rnd.Next(0x1000, 0xffff).ToString("x", CultureInfo.InvariantCulture);
                r.ReqId = r.ClientReqId ?? r.MarkerId;
                r.CableId = cable?.Id;
                long now = Now;
                r.DeadlineMs = now + (long)((r.TimeoutS > 0 ? r.TimeoutS : Cfg.DefaultTimeoutS) * 1000);
                r.IdleMs = (long)((r.IdleS >= 0 ? r.IdleS : Cfg.IdleS) * 1000);
                if (r.ClientReqId != null)
                {
                    byReq[r.ClientReqId] = r;
                    reqOrder.Enqueue(r.ClientReqId);
                    while (reqOrder.Count > ReqStoreMax)
                    {
                        string old = reqOrder.Dequeue();
                        if (byReq.TryGetValue(old, out var o) && o.IsDone) byReq.Remove(old);
                    }
                }
                if (stopped) { r.Complete(Error(cable, 103, "hub_down", "허브 정지 중"), 103); return r; }
                if (r.Kind == TxKind.Run && !PrepareRun(r, out string why))
                {
                    r.Complete(Error(cable, 106, "rejected", why).Set("req_id", r.ReqId), 106);
                    return r;
                }
                if (cable == null || cable.Ring == null || !cable.IsAttached || hub.Tracker(cable) == null)
                {
                    r.Complete(Error(cable, 102, "no_cable", "케이블이 연결돼 있지 않음(" + (cable?.StatusText ?? "없음") + ")").Set("req_id", r.ReqId), 102);
                    return r;
                }
                if (!lanes.TryGetValue(cable.Id, out lane) || lane.Cable != cable)
                {
                    lane?.Stop(false);
                    lane = new Lane(this, cable);
                    lanes[cable.Id] = lane;
                    lane.Start();
                }
            }
            lane.Enqueue(r);
            return r;
        }

        /// <summary>
        /// deadline 이 지났는데 아직 큐에 있는 요청을 지금 101 로 끝낸다(레인이 긴 run 을 실행 중이어도 응답은 제 deadline 에 나가게).
        /// 이미 송신을 시작했거나 끝난 요청이면 false. 큐에서 빠진 요청은 다시 송신될 수 없다.
        /// </summary>
        public bool ExpireIfWaiting(TxRequest r)
        {
            Lane lane;
            lock (sync) { if (r.CableId == null || !lanes.TryGetValue(r.CableId, out lane)) return false; }
            return lane.ExpireIfWaiting(r);
        }

        /// <summary>케이블 큐에서 아직 차례를 기다리는 요청 수(tray·ls 의 "Claude 대기 n").</summary>
        public int Pending(Cable c)
        {
            lock (sync) return c != null && lanes.TryGetValue(c.Id, out var l) ? l.Count : 0;
        }

        public void Stop()
        {
            List<Lane> ls;
            lock (sync)
            {
                stopped = true;
                ls = lanes.Values.ToList();
                lanes.Clear();
            }
            foreach (var l in ls) l.Stop(true);
        }

        // ------------------------------------------------------------------ run 검증·봉인(§3.4)
        /// <summary>CR·LF·제어문자, --inline 화이트리스트, 이스케이프 뒤 900B 를 검사하고 봉인된 한 줄(+CR)을 만든다.</summary>
        internal static bool PrepareRun(TxRequest r, out string why)
        {
            string cmd = r.Cmd;
            if (string.IsNullOrWhiteSpace(cmd)) { why = "빈 명령"; return false; }
            foreach (char ch in cmd)
                if (ch < 0x20 || ch == 0x7f) { why = "제어문자(CR·LF 포함)가 든 명령: 0x" + ((int)ch).ToString("x2", CultureInfo.InvariantCulture); return false; }
            if (r.Inline)
            {
                string c = cmd.Trim();
                if (!InlineHead.IsMatch(c)) { why = "--inline 은 cd/export/unset/umask/alias 단일 명령만 허용"; return false; }
                if (c.IndexOfAny(new[] { ';', '&', '|', '`' }) >= 0 || c.Contains("$(")) { why = "--inline 에 ; & | ` $( 금지"; return false; }
                if (InlinePromptVar.IsMatch(c))
                { why = "--inline 으로 PS1/PS2/PROMPT_COMMAND 변경 금지(프롬프트 판정 기준)"; return false; }
            }
            string esc = cmd.Replace("'", "'\\''");
            int n = Encoding.UTF8.GetByteCount(esc);
            if (n > MaxCmdBytes) { why = "이스케이프 뒤 " + n + "B > " + MaxCmdBytes + "B"; return false; }
            string id = r.MarkerId;
            string line = "printf '__U''H_B_%s\\n' " + id + "; "
                          + (r.Inline ? "eval '" : "sh -c '") + esc + "' </dev/null; "
                          + "printf '__U''H_E_%s_%s\\n' " + id + " $?\r";
            r.SealedLine = Encoding.UTF8.GetBytes(line);
            why = null;
            return true;
        }

        // ------------------------------------------------------------------ 응답
        internal JObj Resp(string status, int exit, Cable c, string rxOffset)
        {
            var o = new JObj().Set("status", status).Set("exit", (long)exit);
            AddCable(o, c, rxOffset);
            return o;
        }

        internal JObj Error(Cable c, int exit, string status, string msg)
        {
            var o = Resp(status, exit, c, c?.Ring != null ? c.Ring.Format(c.Ring.End) : null);
            return o.Set("error", msg);
        }

        /// <summary>모든 응답 공통 필드(§3.4 끝): cable, com, port, board, rx_offset.</summary>
        internal void AddCable(JObj o, Cable c, string rxOffset)
        {
            if (c == null) { o.Null("cable").Null("com").Null("port").Null("board").Null("rx_offset"); return; }
            string board = c.Board ?? hub.Tracker(c)?.Snapshot().Board;
            o.Set("cable", c.Id).Set("com", c.Com).Set("port", (long)c.TcpPort).Set("board", board);
            if (rxOffset == null) o.Null("rx_offset"); else o.Set("rx_offset", rxOffset);
        }

        // ================================================================== 레인(케이블 하나)
        sealed class RunWatch
        {
            public string Id, ReqId;
            public RunMarker B, E;
            public PromptCandidate PromptAfterB;
            public RunMarker Reboot;                      // 부록 C.13 ①: run 구간 안 실제 재부팅(명령 소멸) — 다음 프롬프트 확정 때 사람 입력 없이 잠금 해제
            public bool Released;
            public long Rx0, SendMs;
            public long CutSeqAtSend = long.MaxValue;     // 송신 전에는 어떤 사람 입력도 "송신 뒤"로 세지 않는다
        }

        enum Verdict { Wait, Go, Clear, Reject }

        sealed class Lane
        {
            public readonly Cable Cable;
            readonly Arbiter a;
            readonly object ls = new object();
            readonly List<TxRequest> q = new List<TxRequest>();
            readonly List<RunWatch> lingering = new List<RunWatch>();
            Thread th;
            volatile bool stop;
            PromptTracker tracker;
            RunWatch current;
            long humanCutSeq, promptSeq;
            long nextTxMs;                                 // 이 시각(MonoMs) 전에는 쓰지 않는다(ESC 간격)
            // 부록 C.13 ② 자동 깨우기: 마지막으로 booting·unknown·serialized 로 101 을 받은 Claude 요청 시각, 부팅마다 보낸 수, 마지막 CR 시각
            long lastClaudeBusyMs = long.MinValue, lastNudgeMs = long.MinValue, lastNudgeCheckMs = long.MinValue;
            int nudgeBoot = -1, nudgeCount;
            volatile bool humanLineOpen;                   // 사람이 Enter·^C·^U 없이 글자를 친 채(사람 줄 있음 — 깨우지 않는다)

            public Lane(Arbiter a, Cable c)
            {
                this.a = a;
                Cable = c;
                Cable.Transmitted += OnTransmitted;
            }

            public int Count { get { lock (ls) return q.Count(x => !x.IsDone); } }

            public void Start()
            {
                th = new Thread(Loop) { IsBackground = true, Name = "Arbiter-" + Cable.Id };
                th.Start();
            }

            public void Enqueue(TxRequest r)
            {
                lock (ls) { q.Add(r); Monitor.PulseAll(ls); }
            }

            public bool ExpireIfWaiting(TxRequest r)
            {
                lock (ls)
                {
                    if (!q.Remove(r)) return false;
                    Expire(r, EnsureTracker()?.Snapshot(), a.Now);
                    return true;
                }
            }

            public void Stop(bool join)
            {
                stop = true;
                lock (ls) Monitor.PulseAll(ls);
                if (join && th != null && th != Thread.CurrentThread) th.Join(3000);
                Cable.Transmitted -= OnTransmitted;
                if (tracker != null) { tracker.RunMarkerSeen -= OnMarker; tracker.PromptConfirmed -= OnPrompt; }
                List<TxRequest> left;
                lock (ls) { left = q.ToList(); q.Clear(); }
                foreach (var r in left) r.Complete(a.Error(Cable, 103, "hub_down", "허브 정지 중").Set("req_id", r.ReqId), 103);
            }

            PromptTracker EnsureTracker()
            {
                var t = a.hub.Tracker(Cable);
                if (t != null && !ReferenceEquals(t, tracker))
                {
                    if (tracker != null) { tracker.RunMarkerSeen -= OnMarker; tracker.PromptConfirmed -= OnPrompt; }
                    tracker = t;
                    t.RunMarkerSeen += OnMarker;
                    t.PromptConfirmed += OnPrompt;
                }
                return tracker;
            }

            // ---------------------------------------------------------- 관측 처리기
            void OnTransmitted(TxRecord rec)
            {
                if (rec.Source != TxSource.Human) return;
                bool cut = false;
                foreach (byte b in rec.Data)
                {
                    if (b == 0x0d || b == 0x0a || b == 0x03) { cut = true; humanLineOpen = false; }
                    else if (b == 0x15) humanLineOpen = false;                       // ^U: 친 줄을 지움
                    else if (b >= 0x20 && b != 0x7f) humanLineOpen = true;          // C.13 ②: 사람이 Enter 없이 친 글자가 있다(깨우기 CR 이 그 줄을 실행하지 않게)
                }
                if (cut) Interlocked.Increment(ref humanCutSeq);
            }

            void OnMarker(RunMarker m)
            {
                lock (ls)
                {
                    var w = current;
                    if (w != null && m.RunId == w.Id)
                    {
                        if (m.Kind == 'B' && w.B == null) w.B = m;
                        else if (m.Kind == 'E' && w.E == null) w.E = m;
                        else if (m.Kind == 'R' && w.Reboot == null) w.Reboot = m;
                        Monitor.PulseAll(ls);
                        return;
                    }
                    w = lingering.Find(x => x.Id == m.RunId);
                    if (w == null) return;
                    if (m.Kind == 'R') { w.Reboot ??= m; Cable.PostEvent("*** 재부팅으로 명령 소멸 " + w.ReqId + " — 다음 프롬프트에서 잠금 해제 ***"); }
                    else if (m.Kind == 'B' && w.B == null)
                    {
                        w.B = m;
                        Cable.PostEvent("*** late start " + w.ReqId + " ***");
                    }
                    else if (m.Kind == 'E')
                    {
                        w.E = m;
                        lingering.Remove(w);                 // 추적기가 E 뒤 프롬프트 확정으로 잠금을 푼다
                    }
                }
            }

            void OnPrompt(PromptCandidate c)
            {
                List<RunWatch> release = null;
                lock (ls)
                {
                    promptSeq++;
                    long cut = Interlocked.Read(ref humanCutSeq);
                    var w = current;
                    if (w != null && w.E == null && !c.UBoot && c.Start >= w.Rx0)
                    {
                        if (w.B != null && w.PromptAfterB == null && c.Start >= w.B.End) w.PromptAfterB = c;
                        else if (w.B == null && cut > w.CutSeqAtSend) w.Released = true;
                    }
                    foreach (var x in lingering.ToList())
                        if (x.E == null && !c.UBoot && c.Start >= x.Rx0 && (cut > x.CutSeqAtSend || (x.Reboot != null && c.Start >= x.Reboot.End)))
                        { lingering.Remove(x); (release ??= new List<RunWatch>()).Add(x); }
                    Monitor.PulseAll(ls);
                }
                if (release == null) return;
                foreach (var x in release)
                {
                    tracker?.EndRun(x.Id, a.Now);
                    a.hub.Log.Write("직렬화 잠금 해제 " + x.ReqId + (x.Reboot != null && !(Interlocked.Read(ref humanCutSeq) > x.CutSeqAtSend)
                                    ? " (재부팅으로 명령 소멸 — 재부팅 뒤 프롬프트 확정)" : " (사람 ^C/CR 뒤 프롬프트 확정)"));
                }
            }

            // ---------------------------------------------------------- 큐
            void Loop()
            {
                while (!stop)
                {
                    TxRequest go = null, clear = null;
                    try
                    {
                        AutoNudge();
                        lock (ls)
                        {
                            if (q.Count == 0) { Monitor.Wait(ls, 50); continue; }
                            var t = EnsureTracker();
                            long now = a.Now;
                            // 사람 순번을 스냅샷보다 먼저 잡는다: 이 뒤의 사람 입력은 조건부 쓰기가 막고, 앞의 입력은 LastHumanTxMs(유휴)에 보인다.
                            long hseq = Cable.HumanTxSeq;
                            long hms = Cable.LastHumanTxMs;
                            var sn = t?.Snapshot();
                            for (int i = 0; i < q.Count && go == null && clear == null; i++)
                            {
                                var r = q[i];
                                if (r.IsDone) { q.RemoveAt(i--); continue; }
                                if (!Cable.IsAttached || t == null)
                                {
                                    q.RemoveAt(i--);
                                    r.Complete(a.Error(Cable, 102, "no_cable", "케이블 분리됨").Set("req_id", r.ReqId), 102);
                                    continue;
                                }
                                if (now >= r.DeadlineMs) { q.RemoveAt(i--); Expire(r, sn, now); continue; }
                                if (now < nextTxMs) break;            // ESC 간격(부록 A S0-10): 그동안은 아무것도 보내지 않는다
                                var v = Check(r, t, sn, now, hms, out int code, out string why);
                                if (v == Verdict.Wait && BootCapped(r, sn, now)) { q.RemoveAt(i--); Expire(r, sn, now); continue; }
                                if (v == Verdict.Go) { go = r; r.HumanSeq = hseq; q.RemoveAt(i); }
                                else if (v == Verdict.Clear) { clear = r; r.HumanSeq = hseq; }
                                else if (v == Verdict.Reject)
                                {
                                    q.RemoveAt(i--);
                                    var res = code == 101 ? Busy(r, sn, why) : a.Error(Cable, code, code == 106 ? "rejected" : "busy", why).Set("req_id", r.ReqId);
                                    r.Complete(res, code);
                                }
                            }
                            if (go == null && clear == null) { Monitor.Wait(ls, 10); continue; }
                        }
                        if (clear != null) DoClear(clear);
                        else Execute(go);
                    }
                    catch (Exception e)
                    {
                        a.hub.Log.Write("arbiter " + Cable.Id + " 오류: " + e.GetType().Name + ": " + e.Message);
                        (go ?? clear)?.Complete(a.Error(Cable, 103, "hub_down", "허브 내부 오류: " + e.Message), 103);
                        Thread.Sleep(20);
                    }
                }
            }

            // ---------------------------------------------------------- 부록 C.13 자동 깨우기(auto_nudge)
            const long BootWaitCapMs = 30000, ClaudeWaitMs = 60000;
            const int MaxNudgesPerBoot = 3;

            /// <summary>
            /// ④: Claude run 이 booting·unknown·재부팅 잠금(serialized) 때문에 30초 넘게 기다렸으면 그 --timeout 전이라도 101 로 끝낸다(깨워도 프롬프트가 오지 않음).
            /// 다른 이유(사람 입력·화면 모드·명령 실행 중)로 기다리면 지금처럼 --timeout 까지. 레인 잠금 안.
            /// </summary>
            bool BootCapped(TxRequest r, TrackerSnapshot sn, long now)
            {
                if (!a.Cfg.AutoNudge || r.Internal || r.Kind != TxKind.Run || sn == null) return false;
                bool boot = sn.State == PromptState.Booting || sn.State == PromptState.Unknown
                            || (sn.State == PromptState.Busy && sn.RunActive && lingering.Any(x => x.Reboot != null));
                if (!boot) { r.BootWaitSinceMs = long.MinValue; return false; }
                if (r.BootWaitSinceMs == long.MinValue) { r.BootWaitSinceMs = now; return false; }
                return now - r.BootWaitSinceMs > BootWaitCapMs;
            }

            /// <summary>
            /// ②: Claude 가 기다리는 중(재부팅으로 명령이 사라진 잠금 run, 큐의 Claude 요청, 60초 안에 booting·unknown·serialized 로 101 을 받은 Claude 요청)이고
            /// 추적기가 깨워도 된다고 하면(CanNudge — UNKNOWN 또는 커널 단계 이후 BOOTING, 커널 줄 무음, 사람 유휴·줄 없음·화면 모드 아님) CR 1회.
            /// run 실행 중엔 보내지 않는다. auto_nudge_gap_s(기본 10초)에 1번, 부팅마다 3번까지. 사람이 그 사이 입력하면 보내지 않는다(조건부 쓰기). .log 에 '>>> [hub-nudge]'.
            /// </summary>
            void AutoNudge()
            {
                if (!a.Cfg.AutoNudge || !Cable.IsAttached || humanLineOpen) return;
                long now = a.Now;
                if (lastNudgeCheckMs != long.MinValue && now - lastNudgeCheckMs < 200) return;
                lastNudgeCheckMs = now;
                PromptTracker t;
                lock (ls)
                {
                    t = EnsureTracker();
                    if (t == null || current != null) return;
                    bool waiting = lingering.Any(x => x.Reboot != null && x.E == null)
                                   || q.Any(x => !x.IsDone && !x.Internal && x.Source == TxSource.Claude && x.Kind != TxKind.Probe)
                                   || (lastClaudeBusyMs != long.MinValue && now - lastClaudeBusyMs <= ClaudeWaitMs);
                    if (!waiting) return;
                }
                if (!t.CanNudge(now, (long)(a.Cfg.IdleS * 1000), out _)) return;
                var sn = t.Snapshot();
                if (sn.BootMatches != nudgeBoot) { nudgeBoot = sn.BootMatches; nudgeCount = 0; }
                if (nudgeCount >= MaxNudgesPerBoot) return;
                if (lastNudgeMs != long.MinValue && now - lastNudgeMs < (long)(a.Cfg.AutoNudgeGapS * 1000)) return;
                long hseq = Cable.HumanTxSeq;
                WaitGap();
                nudgeCount++;
                lastNudgeMs = a.Now;
                string n = nudgeCount + "/" + MaxNudgesPerBoot;
                if (sn.State == PromptState.Unknown && !sn.BootSeen)
                {
                    // 연결·복원 뒤 재부팅을 못 본 UNKNOWN: 셸 줄에 다른 터미널에서 치다 만 글자가 남아 있을 수 있다 — probe 와 같이 ^E ^U 로 지운 뒤 CR
                    // (맨 CR 이면 그 줄을 실행한다). 바이트마다 조건부 쓰기: 사이에 사람이 치면 나머지(CR 포함)는 보내지 않는다.
                    bool first = true;
                    foreach (byte b in ProbeBytes)
                    {
                        if (!first) Thread.Sleep(ProbeKillGapMs);
                        if (!Cable.TryWriteIf(new[] { b }, TxSource.Probe, hseq,
                                              first ? ">>> [hub-nudge] ^E^U CR " + n + " (연결 뒤 상태 모름 — 남은 줄을 지우고 깨우기, " + sn.StateName + ")" : null)) break;
                        first = false;
                    }
                }
                else Cable.TryWriteIf(new byte[] { 0x0d }, TxSource.Probe, hseq, ">>> [hub-nudge] CR " + n + " (재부팅 뒤 프롬프트 깨우기, " + sn.StateName + ")");
            }

            /// <summary>§3.3 게이트 판정. 레인 잠금 안(쓰기 없음). 사람 유휴는 추적기 반영분과 Cable 의 송신 시각 중 늦은 쪽으로 본다.</summary>
            Verdict Check(TxRequest r, PromptTracker t, TrackerSnapshot sn, long now, long cableHumanMs, out int code, out string why)
            {
                code = 0; why = null;
                long lastHuman = Math.Max(sn.LastHumanTxMs, cableHumanMs);
                bool idle = lastHuman == long.MinValue || now - lastHuman >= r.IdleMs;
                if (r.Kind == TxKind.Run && r.Gate != TxGate.Force && sn.State == PromptState.AtUboot)
                { code = 106; why = "AT_UBOOT 에서는 run 불가(send 사용)"; return Verdict.Reject; }
                switch (r.Gate)
                {
                    case TxGate.Probe:
                        if (!idle) { why = "human_active"; code = 101; return Verdict.Reject; }
                        if (r.Nudge ? t.CanNudge(now, r.IdleMs, out why) : t.CanProbe(now, r.UserOk, out why)) return Verdict.Go;
                        code = 101;
                        return Verdict.Reject;
                    case TxGate.Force:
                        if (idle) return Verdict.Go;
                        break;
                    case TxGate.Prompt:
                        if (idle && sn.State == PromptState.AtPrompt) return Verdict.Go;
                        break;
                    case TxGate.Token:
                        if (idle && !sn.RunActive && (sn.State == PromptState.AtPrompt || sn.State == PromptState.AtUboot || sn.State == PromptState.Booting))
                            return Verdict.Go;
                        break;
                }
                // --clear-line(§3.3, E5): 사람 클라이언트 0개 + 반쯤 친 줄이 남음 + 사람 유휴 → 먼저 ^U
                if (r.ClearLine && !r.ClearDone && idle && sn.State == PromptState.HumanTyping && sn.BlockedReason == "partial_line_orphaned" && sn.HumanClients <= 0)
                    return Verdict.Clear;
                return Verdict.Wait;
            }

            string BlockedReason(TxRequest r, TrackerSnapshot sn, long now)
            {
                if (sn == null) return "unknown";
                bool idle = sn.LastHumanTxMs == long.MinValue || now - sn.LastHumanTxMs >= r.IdleMs;
                if (r.Gate == TxGate.Token && sn.RunActive) return "serialized";
                if (r.Gate == TxGate.Token && (sn.State == PromptState.AtUboot || sn.State == PromptState.Booting) && !idle) return "human_idle";
                return sn.BlockedReason ?? (idle ? "busy_since" : "human_idle");
            }

            JObj Busy(TxRequest r, TrackerSnapshot sn, string reason)
            {
                var o = a.Error(Cable, 101, "busy", "비프롬프트 상태이거나 사용자 사용 중").Set("req_id", r.ReqId).Set("blocked_reason", reason);
                if (sn != null)
                {
                    o.Set("state", sn.StateName).Set("since_ms", Math.Max(0, a.Now - sn.SinceMs)).Set("last_lines", sn.LastLines.Select(RxText.FromLatin1));
                    if (sn.PartialText != null) o.Set("partial", RxText.FromLatin1(sn.PartialText));
                }
                return o;
            }

            /// <summary>deadline 까지 게이트가 열리지 않음 → 보내지 않고 101(dropped).</summary>
            void Expire(TxRequest r, TrackerSnapshot sn, long now)
            {
                string reason = BlockedReason(r, sn, now);
                if (!r.Internal && (reason == "booting" || reason == "unknown" || reason == "serialized")) lastClaudeBusyMs = now;   // C.13 ②: 60초 동안 깨우기 대상
                if (!r.Internal) Cable.PostEvent("!!! [" + Tag(r) + " " + r.ReqId + "] dropped (" + reason + ")");
                r.Complete(Busy(r, sn, reason), 101);
            }

            static string Tag(TxRequest r) => r.Source == TxSource.Spool ? "tx" : r.Gate == TxGate.Force ? "claude-force" : "claude";

            // ---------------------------------------------------------- 송신
            void WaitGap()
            {
                while (true)
                {
                    long left = Interlocked.Read(ref nextTxMs) - a.Now;
                    if (left <= 0) return;
                    Thread.Sleep((int)Math.Min(left, 20));
                }
            }

            enum Put { Sent, Human, NoCable }

            /// <summary>조건부 쓰기 한 번(게이트 순번 r.HumanSeq). 실패 원인: 순번이 바뀌었으면 사람 입력, 아니면 분리·포트 오류.</summary>
            Put Write1(TxRequest r, byte[] data, int off, int n, TxSource src, int sentBefore, string ev)
            {
                a.TestBeforeWrite?.Invoke(Cable, r, sentBefore);
                if (Cable.TryWriteIf(data, off, n, src, r.HumanSeq, ev)) return Put.Sent;
                return Cable.HumanTxSeq != r.HumanSeq ? Put.Human : Put.NoCable;
            }

            /// <summary>
            /// ESC 뒤마다 esc_gap_ms 간격을 두고 쓴다(busybox read_key 가 ESC 시퀀스로 묶지 않게, 부록 A S0-10).
            /// 조각마다 조건부 쓰기: 간격 동안 사람이 치면 남은 조각은 나가지 않는다. ev(로그 줄)는 첫 조각과 함께 한 번 나가고 null 이 된다.
            /// </summary>
            Put WriteGapped(TxRequest r, byte[] data, TxSource src, ref int sent, ref string ev)
            {
                int pos = 0;
                while (pos < data.Length)
                {
                    int esc = Array.IndexOf(data, (byte)0x1b, pos);
                    int end = esc < 0 ? data.Length : esc + 1;
                    WaitGap();
                    var p = Write1(r, data, pos, end - pos, src, sent, ev);
                    if (p != Put.Sent) return p;
                    ev = null;
                    sent += end - pos;
                    if (esc >= 0) Interlocked.Exchange(ref nextTxMs, a.Now + a.Cfg.EscGapMs);
                    pos = end;
                }
                return Put.Sent;
            }

            /// <summary>아무것도 보내지 못함(판정 뒤 사람 입력) → 큐 앞에 되돌려 게이트를 다시 본다. 그 사이 취소됐으면 103 으로 끝난다.</summary>
            void Requeue(TxRequest r)
            {
                if (!r.UndoBeginSend()) return;
                a.hub.Log.Write("송신 직전 사람 입력 → 재대기 " + Cable.Id + " " + Tag(r) + " " + r.ReqId);
                bool late;
                lock (ls)
                {
                    late = stop;
                    if (!late) { q.Insert(0, r); Monitor.PulseAll(ls); }
                }
                if (late) r.Complete(a.Error(Cable, 103, "hub_down", "허브 정지 중").Set("req_id", r.ReqId), 103);
            }

            /// <summary>일부 조각을 보낸 뒤 사람 입력 → 나머지를 버리고 interrupted(101).</summary>
            void Interrupted(TxRequest r, long rx0, int sent, string state)
            {
                Cable.PostEvent("!!! [" + Tag(r) + " " + r.ReqId + "] interrupted (human input)");
                var o = a.Resp("interrupted", 101, Cable, Cable.Ring.Format(rx0)).Set("req_id", r.ReqId).Set("bytes", (long)sent).Set("state", state)
                         .Set("blocked_reason", "human_input").Set("error", "조각 송신 중 사람 입력 — 나머지 조각은 보내지 않음");
                r.Complete(o, 101);
            }

            void NoCable(TxRequest r) => r.Complete(a.Error(Cable, 102, "no_cable", "송신 실패: " + Cable.LastError).Set("req_id", r.ReqId), 102);

            void DoClear(TxRequest r)
            {
                var sn = tracker.Snapshot();
                string text = RxText.FromLatin1(sn.PartialText ?? "");
                bool sent = false;
                bool did = r.WhilePending(() =>
                {
                    WaitGap();
                    sent = Write1(r, new byte[] { 0x15 }, 0, 1, TxSource.Claude, 0, "!!! [claude-clear] " + text) == Put.Sent;
                });
                if (did && !sent && Cable.HumanTxSeq != r.HumanSeq) return;      // 사람이 다시 침 → 지우지 않음, 다음 판정에서 다시 본다
                r.ClearDone = true;
                if (did && sent) r.ClearedText = text;
            }

            void Execute(TxRequest r)
            {
                if (a.Now >= r.DeadlineMs) { Expire(r, tracker?.Snapshot(), a.Now); return; }
                if (!r.TryBeginSend()) return;                 // 취소됨(미전송)
                bool requeue = false;
                switch (r.Kind)
                {
                    case TxKind.Run: requeue = ExecuteRun(r); break;
                    case TxKind.Send: requeue = ExecuteSend(r); break;
                    case TxKind.Probe: ExecuteProbe(r); break;
                    case TxKind.Reboot: requeue = ExecuteReboot(r); break;
                }
                if (requeue) Requeue(r);
            }

            /// <returns>true = 아무것도 보내지 않음(재대기).</returns>
            bool ExecuteSend(TxRequest r)
            {
                var tl = TxLine.Parse(r.Cmd);
                var t = tracker;
                string state = t.Snapshot().StateName;
                string ev = r.Source == TxSource.Spool ? ">>> [tx] " + r.Cmd : ">>> [" + Tag(r) + " " + r.ReqId + "] " + r.Cmd;
                WaitGap();
                long rx0 = Cable.Ring.End;
                int sent = 0;
                var res = Put.Sent;
                foreach (var op in tl.Ops)
                {
                    if (op.Break)
                    {
                        WaitGap();
                        a.TestBeforeWrite?.Invoke(Cable, r, sent);
                        if (Cable.SendBreak(300, r.HumanSeq, ev)) { ev = null; continue; }
                        res = Cable.HumanTxSeq != r.HumanSeq ? Put.Human : Put.NoCable;
                        break;
                    }
                    res = WriteGapped(r, op.Bytes, r.Source, ref sent, ref ev);
                    if (res != Put.Sent) break;
                }
                if (res == Put.Sent && !tl.NoNewline) res = WriteGapped(r, new[] { (byte)'\r' }, r.Source, ref sent, ref ev);
                if (res == Put.Human)
                {
                    if (ev != null) return true;               // 첫 조각도 못 보냄 → 재대기
                    Interrupted(r, rx0, sent, state);
                    return false;
                }
                if (res == Put.NoCable) { NoCable(r); return false; }
                if (ev != null) Cable.PostEvent(ev);           // 보낼 바이트가 없는 줄({NONL} 단독 등)도 기록은 남긴다
                var o = a.Resp("ok", 0, Cable, Cable.Ring.Format(rx0)).Set("req_id", r.ReqId).Set("bytes", (long)sent).Set("state", state);
                if (r.ClearedText != null) o.Set("cleared", r.ClearedText);
                r.Complete(o, 0);
                return false;
            }

            bool ExecuteReboot(TxRequest r)
            {
                string ev = ">>> [" + Tag(r) + " " + r.ReqId + "] reboot";
                WaitGap();
                long rx0 = Cable.Ring.End;                     // 송신 직전(§3.7): 이 offset 부터 reboot 에코가 들어온다
                int sent = 0;
                var res = WriteGapped(r, Encoding.ASCII.GetBytes("reboot\r"), r.Source, ref sent, ref ev);
                if (res == Put.Human) return true;             // 한 조각: 못 보냈으면 아무것도 안 나감
                if (res == Put.NoCable) { NoCable(r); return false; }
                r.Complete(a.Resp("ok", 0, Cable, Cable.Ring.Format(rx0)).Set("req_id", r.ReqId), 0);
                return false;
            }

            /// <summary>
            /// probe 는 기다리지 않는다(§3.3): 판정 뒤 사람 입력으로 못 보내면 101.
            /// 보내는 것: ^E(0x05, 줄 끝으로) → ^U(0x15, 커서 앞 줄 지우기) → CR, 바이트 사이 ProbeKillGapMs. busybox ash lineedit·U-Boot cread 모두
            /// ^E 는 줄 끝, ^U 는 지우기다(ash 의 ^U 는 커서 앞만 지우므로 ^E 로 커서를 먼저 끝에 둔다). 케이블 탈착 잡음 등으로 셸 줄 버퍼에
            /// 보이지 않는 글자가 남아 있어도(커서가 옮겨졌어도) CR 이 그 줄을 실행하지 않게 한다(실보드 11-03: 재연결 뒤 CR 만 보낸 probe 가
            /// 숨은 줄을 실행해 busybox chroot 사용법이 찍힘). 바이트마다 조건부 쓰기(P2): 사이에 사람이 치면 나머지(CR 포함)는 보내지 않고
            /// 101 human_active(bytes = 보낸 수). 바이트 사이에 deadline 이 지나도 나머지는 보내지 않는다(100 timeout).
            /// </summary>
            void ExecuteProbe(TxRequest r)
            {
                var t = tracker;
                string ev = ">>> [claude-probe] " + r.ReqId;
                WaitGap();
                long rx0 = Cable.Ring.End;
                long seq0;
                lock (ls) seq0 = promptSeq;
                int sent = 0;
                var res = Put.Sent;
                bool late = false;
                foreach (byte b in ProbeBytes)
                {
                    if (sent > 0)
                    {
                        Thread.Sleep(ProbeKillGapMs);
                        if (a.Now >= r.DeadlineMs) { late = true; break; }
                    }
                    res = WriteGapped(r, new[] { b }, TxSource.Probe, ref sent, ref ev);
                    if (res != Put.Sent) break;
                }
                if (res == Put.Human) { r.Complete(Busy(r, t.Snapshot(), "human_active").Set("bytes", (long)sent), 101); return; }
                if (res == Put.NoCable) { NoCable(r); return; }
                if (late)
                {
                    r.Complete(a.Error(Cable, 100, "timeout", "probe deadline 지남 — 앞 바이트만 보내고 CR 은 보내지 않음").Set("req_id", r.ReqId).Set("bytes", (long)sent), 100);
                    return;
                }
                long until = Math.Min(r.DeadlineMs, a.Now + ProbeWaitMs);
                bool prompt;
                lock (ls)
                {
                    while (promptSeq == seq0 && a.Now < until && !stop) Monitor.Wait(ls, 20);
                    prompt = promptSeq != seq0;
                }
                r.Complete(a.Resp("ok", 0, Cable, Cable.Ring.Format(rx0)).Set("req_id", r.ReqId).Set("prompt", prompt).Set("state", t.Snapshot().StateName), 0);
            }

            bool ExecuteRun(TxRequest r)
            {
                var t = tracker;
                var ring = Cable.Ring;
                var w = new RunWatch { Id = r.MarkerId, ReqId = r.ReqId };
                lock (ls) current = w;
                WaitGap();
                long rx0 = ring.End;
                w.Rx0 = rx0;
                w.CutSeqAtSend = Interlocked.Read(ref humanCutSeq);
                t.BeginRun(w.Id, rx0, a.Now, true);
                var put = Write1(r, r.SealedLine, 0, r.SealedLine.Length, r.Source, 0,
                                 ">>> [" + Tag(r) + " " + r.ReqId + "] " + (r.Inline ? "(inline) " : "") + r.Cmd);
                w.SendMs = a.Now;
                if (put != Put.Sent)
                {
                    lock (ls) current = null;
                    t.EndRun(w.Id, a.Now);
                    if (put == Put.Human) return true;         // 봉인 줄은 한 번에 나간다: 못 보냈으면 0바이트 → 재대기
                    NoCable(r);
                    return false;
                }
                lock (ls) lingering.Clear();                   // 새 run 은 남은 잠금을 대신한다(--force 경로에서만 가능)
                var res = a.Resp("ok", 0, Cable, ring.Format(rx0)).Set("req_id", r.ReqId).Set("run_id", w.Id);
                if (r.ClearedText != null) res.Set("cleared", r.ClearedText);
                lock (ls)
                {
                    while (!(w.E != null || (w.B != null && w.PromptAfterB != null) || (w.B == null && w.Released) || w.Reboot != null)
                           && a.Now < r.DeadlineMs && Cable.IsAttached && !stop)
                        Monitor.Wait(ls, 20);
                }
                if (w.E == null && w.Reboot != null && !(w.B != null && w.PromptAfterB != null))
                {
                    // 부록 C.13 ①: 송신 뒤 보드가 재부팅 — 명령은 사라졌다. 바로 끝내고 잠금은 재부팅 뒤 첫 프롬프트 확정 때 사람 입력 없이 푼다(OnPrompt).
                    bool started = w.B != null;
                    lock (ls) { current = null; lingering.Add(w); }
                    if (started) res.Set("b_ms", Math.Max(0, w.B.Ms - w.SendMs));
                    Output(res, ring, w.B?.End ?? rx0, Math.Max(w.B?.End ?? rx0, w.Reboot.Start));
                    Cable.PostEvent("!!! [" + Tag(r) + " " + r.ReqId + "] 재부팅으로 명령 소멸(" + (a.Now - w.SendMs) + "ms, " + (started ? "명령이 시작된 뒤" : "명령이 시작되기 전") + ")");
                    res.Set("status", "stranded").Set("exit", 100L).Null("rc").Set("released", false).Set("rebooted", true).Set("started", started);
                    r.Complete(res, 100);
                    return false;
                }
                if (w.B != null)
                {
                    long bms = w.B.Ms - w.SendMs;
                    res.Set("b_ms", Math.Max(0, bms));
                    if (bms > a.Cfg.StrandedMs) res.Set("late_b", true);       // 잠정 stranded 였다가 deadline 전에 B 가 옴(정상 처리)
                }

                if (w.E != null)
                {
                    lock (ls) current = null;
                    Output(res, ring, w.B?.End ?? rx0, w.E.Start);
                    res.Set("rc", (long)w.E.Rc).Set("exit", (long)w.E.Rc);
                    r.Complete(res, w.E.Rc);
                    return false;
                }
                if (w.B != null && w.PromptAfterB != null)
                {
                    // B 는 받았고 E 없이 새 프롬프트 확정 → interrupted(사람 ^C, busybox ash 는 남은 목록을 버린다)
                    lock (ls) current = null;
                    t.EndRun(w.Id, a.Now);
                    Output(res, ring, w.B.End, w.PromptAfterB.Start);
                    res.Set("status", "interrupted").Set("exit", 101L).Null("rc");
                    r.Complete(res, 101);
                    return false;
                }
                if (stop || !Cable.IsAttached)
                {
                    lock (ls) current = null;
                    t.EndRun(w.Id, a.Now);
                    int code = stop ? 103 : 102;
                    res.Set("status", stop ? "hub_down" : "no_cable").Set("exit", (long)code).Null("rc");
                    r.Complete(res, code);
                    return false;
                }
                if (w.B == null)
                {
                    // stranded: B 없음. Ctrl-C 금지·자동 재시도 금지. 잠금 유지(늦은 E 또는 사람 ^C/CR 뒤 프롬프트 확정으로 해제).
                    bool released = w.Released;
                    lock (ls)
                    {
                        current = null;
                        if (!released) lingering.Add(w);
                    }
                    if (released) t.EndRun(w.Id, a.Now);
                    Cable.PostEvent("!!! [" + Tag(r) + " " + r.ReqId + "] stranded (" + (a.Now - w.SendMs) + "ms 동안 B 마커 없음"
                                    + (released ? ", 사람 입력 뒤 프롬프트로 해제" : ", 줄이 tty 에 남았을 수 있음") + ")");
                    res.Set("status", "stranded").Set("exit", 100L).Null("rc").Set("released", released);
                    r.Complete(res, 100);
                    return false;
                }

                // timeout: B 는 받았는데 deadline 까지 E 없음
                long outEnd = ring.End;
                string cleanup;
                var sn = t.Snapshot();
                bool humanAfter = Interlocked.Read(ref humanCutSeq) > w.CutSeqAtSend || (sn.LastHumanTxMs != long.MinValue && sn.LastHumanTxMs >= w.SendMs);
                if (r.NoInterrupt) cleanup = "disabled";
                else if (humanAfter) cleanup = "skipped_human_input";
                else
                {
                    WaitGap();
                    // 조건부(게이트 순번): 판정 뒤 사람 입력이 한 바이트라도 있었으면 ^C 를 보내지 않는다(위 검사와 이 쓰기 사이의 경합도 닫힘)
                    if (!Cable.TryWriteIf(new byte[] { 0x03 }, TxSource.Claude, r.HumanSeq, ">>> [" + Tag(r) + " " + r.ReqId + "] ^C (timeout)")
                        && Cable.HumanTxSeq != r.HumanSeq)
                        cleanup = "skipped_human_input";
                    else
                    {
                        long until = a.Now + CleanupWaitMs;
                        lock (ls)
                        {
                            while (w.PromptAfterB == null && w.E == null && a.Now < until && Cable.IsAttached && !stop) Monitor.Wait(ls, 20);
                        }
                        cleanup = w.E != null ? "e_arrived" : w.PromptAfterB != null ? "ctrl_c" : "ctrl_c_no_prompt";
                    }
                }
                lock (ls)
                {
                    current = null;
                    if (w.E == null && cleanup != "ctrl_c") lingering.Add(w);
                }
                if (cleanup == "ctrl_c") t.EndRun(w.Id, a.Now);
                Output(res, ring, w.B.End, outEnd);
                res.Set("status", "timeout").Set("exit", 100L).Null("rc").Set("cleanup", cleanup);
                r.Complete(res, 100);
                return false;
            }

            /// <summary>out[]·kernel·trailing_newline·raw_offset 채우기. from 바로 뒤의 줄바꿈(B 마커 줄 끝)은 출력이 아니다.</summary>
            void Output(JObj res, RingBuffer ring, long from, long to)
            {
                bool truncated = false;
                if (from < ring.Start) { from = ring.Start; truncated = true; }
                if (to < from) to = from;
                var buf = new byte[to - from];
                int n = ring.Read(from, buf, 0, buf.Length);
                if (n < 0) { n = 0; truncated = true; }
                int skip = 0;
                while (skip < n && (buf[skip] == (byte)'\r' || buf[skip] == 0)) skip++;
                if (skip < n && buf[skip] == (byte)'\n') skip++; else skip = 0;
                var o = RxText.Lines(buf, skip, n - skip);
                res.Set("out", o.Lines).Set("kernel", o.Kernel).Set("trailing_newline", o.TrailingNewline)
                   .Set("raw_offset", ring.Format(from + skip)).Set("raw_end", ring.Format(from + n));
                if (truncated) res.Set("truncated", true);
            }
        }
    }
}
