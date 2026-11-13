// 허브 조립: 설정 검증 → hub_dir(state.json, hub.log) → CableManager(기록 코어) → 서비스 층.
// 서비스 층은 Cables 이벤트와 Cable.Ring 구독·Cable.Transmitted 관측으로만 붙는다(기록 코어는 서비스 층에 의존하지 않는다, P1).
// 현재 서비스 층: PromptTracker(케이블별, attach 마다 상태 복원 후 링 구독), Arbiter(케이블별 TX 큐), ControlServer(127.0.0.1 JSON lines),
// TelnetServer(케이블별 127.0.0.1:<TCP포트>, 첫 attach 때 열고 코어 수명 동안 유지 — 분리·재연결은 클라이언트에 배너만), SpoolWatcher(<log_root>\live\*.tx 소비·*.status 기록).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace UartHub
{
    public sealed class Hub : IDisposable
    {
        bool started, stopped;
        readonly object svc = new object();
        readonly Dictionary<string, PromptTracker> trackers = new Dictionary<string, PromptTracker>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, TrackerPump> pumps = new Dictionary<string, TrackerPump>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> attachedOnce = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, long> detachedSeq = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);   // 처리한 detach 의 최대 AttachSeq
        readonly HashSet<string> clearNext = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // 다음 attach 세션에 재연결 줄 지우기 예약(§3.3)
        readonly Dictionary<int, DateTime> lastAlive;        // 이전 코어의 마지막 하트비트(코어 재시작 공백 판정, §3.2)

        public Config Config { get; }
        public IClock Clock { get; }
        public HubLog Log { get; }
        public StateStore State { get; }
        public CableManager Cables { get; }
        public Arbiter Arbiter { get; }
        public ControlServer Control { get; }
        public TelnetServer Telnet { get; }
        public SpoolWatcher Spool { get; }
        public static string Version => BuildInfo.Version;

        /// <summary>
        /// 감독이 넘긴 코어 재시작 사유(예: "exit=-1", "exit=70", "exit=-1 hang=heartbeat", "upgrade"). null 이면 첫 기동.
        /// Start() 전에 설정한다. 설정돼 있으면 케이블마다 첫 파일의 "*** hub start ***" 다음에
        /// "*** hub restart (사유) ***" 와 "*** gap 초 (hub down) ***"(이전 코어의 alive 하트비트 기준)를 쓴다(§3.8).
        /// </summary>
        public string RestartReason { get; set; }

        /// <param name="providers">null 이면 config.vids 로 WMI 열거(실운용). 시뮬은 FakePortProvider 를 넘기고 vids=[].</param>
        public Hub(Config cfg, IClock clock = null, IEnumerable<IPortProvider> providers = null)
        {
            cfg.ValidateOrThrow();
            Config = cfg;
            Clock = clock ?? SystemClock.Instance;
            Directory.CreateDirectory(cfg.LogRoot);
            Directory.CreateDirectory(cfg.EffectiveHubDir);
            Log = new HubLog(Path.Combine(cfg.EffectiveHubDir, "hub.log"), Clock);
            State = new StateStore(Path.Combine(cfg.EffectiveHubDir, "state.json"));
            if (State.LoadWarning != null) Log.Write(State.LoadWarning);
            var provs = providers?.ToList() ?? new List<IPortProvider>();
            if (providers == null && cfg.Vids.Count > 0) provs.Add(new WmiPortProvider(cfg.Vids));
            Cables = new CableManager(cfg, Clock, State, Log, provs);
            lastAlive = ReadAlive(Path.Combine(cfg.EffectiveHubDir, "alive"));
            Cables.StartPreamble = RestartPreamble;
            Cables.CableAttached += OnCableAttached;
            Cables.CableDetached += OnCableDetached;
            Arbiter = new Arbiter(this);
            Control = new ControlServer(this);
            Telnet = new TelnetServer(this);         // CableAttached 구독 순서: 추적기(위) 다음 → 접속 클라이언트 수를 추적기에 바로 반영
            Spool = new SpoolWatcher(this);
        }

        /// <summary>ls·status·.status 에 보이는 케이블 상태(§3.7). attached 인데 telnet bind 가 실패했으면 bind_failed(기록은 계속).</summary>
        public string CableStatusText(Cable c) => c.IsAttached && Telnet.IsBindFailed(c) ? "bind_failed" : c.StatusText;

        // ------------------------------------------------------------------ PromptTracker
        /// <summary>케이블의 PromptTracker(첫 attach 때 생긴다). 없으면 null.</summary>
        public PromptTracker Tracker(string cableId)
        {
            lock (svc) return trackers.TryGetValue(cableId, out var t) ? t : null;
        }

        public PromptTracker Tracker(Cable c) => Tracker(c.Id);

        /// <summary>
        /// attach 통지(seq = 그 attach 의 AttachSeq). 통지는 케이블 잠금 밖이라 순서가 뒤바뀔 수 있다:
        /// 이미 detach 를 처리한 세션(seq ≤ detachedSeq)이나 더 새 펌프가 있는 경우의 attach 는 무시한다.
        /// </summary>
        void OnCableAttached(Cable c, long seq)
        {
            if (c.TcpPort <= 0 || c.Ring == null) return;
            PromptTracker t;
            TrackerPump old;
            bool first;
            lock (svc)
            {
                if (IsOldAttach(c.Id, seq)) { Log.Write(L.T("늦은 attach 이벤트 무시 " + c.Id + " seq=" + seq, "late attach event ignored " + c.Id + " seq=" + seq)); return; }
                if (!trackers.TryGetValue(c.Id, out t))
                {
                    t = new PromptTracker(Config, c.Board) { Log = Log };
                    var tt = t;
                    c.Transmitted += rec => tt.OnTx(rec.Source, rec.Data, rec.MonoMs, rec.RxPos);
                    c.ReplugClearBlocked = session => tt.ReplugClearBlockedFor(session);
                    trackers[c.Id] = t;
                }
                pumps.TryGetValue(c.Id, out old);
                pumps.Remove(c.Id);
                first = attachedOnce.Add(c.Id);
            }
            old?.RequestStop();                                     // 기다림은 새 펌프 스레드에서(After) — 이 처리기 뒤의 telnet 재연결 배너가 늦지 않게
            var src = first ? RestoreSource.CoreRestart : RestoreSource.Attach;
            TrackerPump p = null;
            p = new TrackerPump(t, c, Clock, Log, () => PrepareTracker(c, t, src, seq, () => p.StopRequested)) { AttachSeq = seq, After = old };
            // 앞 세션의 detach 가 아직 처리되지 않았으면(old 펌프가 남아 있음 — 늦은 detach) 그 detach 는 늦은 통지로 버려지므로 예약 판단을 여기서 한다:
            // 추적기는 아직 앞 세션 상태(새 세션 복원 전)다.
            bool oldArm = old != null && ArmFromSession(c, t, old.AttachSeq);
            bool arm;
            lock (svc)
            {
                if (stopped || IsOldAttach(c.Id, seq)) return;      // 그 사이 이 세션의 detach 가 처리됨
                pumps[c.Id] = p;
                // 재연결 줄 지우기(§3.3): 다시 꽂기(코어 재시작·이 프로세스 첫 attach 가 아님)이고 앞 세션이 셸 프롬프트에서 끝났을 때만.
                // 펌프 등록과 같은 잠금 안에서 예약한다(그 뒤의 detach 는 이 예약을 보고 다음 세션으로 넘긴다). 잠금 순서 svc → 송신(txLock), 반대 경로 없음.
                // telnet 처리기(이 처리기 뒤)가 사람 입력을 받기 전에 예약된다.
                bool want = clearNext.Remove(c.Id) | oldArm;
                arm = !first && want && c.ReplugClearEnabled;
                if (arm) c.ArmReplugClear(seq);
            }
            if (arm) Log.Write(L.T("replug clear 예약 " + c.Id + " seq=" + seq + " — 첫 입력 앞에 ^E^U(탈착 잡음이 셸 줄 버퍼에 남았을 수 있음)", "replug clear armed " + c.Id + " seq=" + seq + " — ^E^U before the first input (unplug noise may be left in the shell line buffer)"));
            TestAfterAttachRegistered?.Invoke(c);
            p.Start();
        }

        /// <summary>
        /// 세션 seq 가 끝날 때 다음 세션에 재연결 줄 지우기를 예약할지(§3.3): 그 순간(추적기 초기화·복원 전) 셸·U-Boot 줄 편집 중이었거나
        /// (AT_PROMPT·AT_UBOOT, 또는 프롬프트에서 시작한 HUMAN_TYPING — 반쯤 친 글자도 ^E^U 로 함께 지워진다: 잡음이 그 뒤에 붙으므로 의도한 것),
        /// 그 세션의 예약이 아직 쓰이지 않았고(잡음이 그대로 남음) 그 세션에서 부팅·전체화면으로 막히지 않았으면.
        /// AT_UBOOT 도 같다: U-Boot readline 에서도 ^U 는 줄 지우기이고 ^E 는 줄 끝(편집 없는 readline 이면 글자로 들어갔다가 ^U 로 함께 지워진다).
        /// </summary>
        static bool ArmFromSession(Cable c, PromptTracker t, long seq)
        {
            if (t != null && t.ShellLineEditing) return true;
            return c.ReplugClearPending(seq) && !(t?.ReplugClearBlockedFor(seq) ?? false);
        }

        /// <summary>시험 훅: attach 처리기가 펌프 등록·예약을 마친 직후(펌프 시작 전, 통지 스레드). 그 직후 detach 경합 재현용. 운영에서는 null.</summary>
        public Action<Cable> TestAfterAttachRegistered;
        /// <summary>시험 훅: 펌프 스레드가 추적기 복원(prepare)을 시작하기 직전. 복원 전에 들어온 입력 재현용. 운영에서는 null.</summary>
        public Action<Cable> TestBeforePrepare;

        bool IsOldAttach(string id, long seq)
            => (detachedSeq.TryGetValue(id, out long ds) && seq <= ds) || (pumps.TryGetValue(id, out var cur) && cur.AttachSeq > seq);

        /// <summary>detach 통지(seq = 끝난 세션의 AttachSeq). 더 새 세션의 펌프가 이미 돌고 있으면 늦은 통지라 무시한다.</summary>
        void OnCableDetached(Cable c, long seq)
        {
            TrackerPump p;
            PromptTracker t;
            long curSeq = 0;
            lock (svc)
            {
                if (!detachedSeq.TryGetValue(c.Id, out long ds) || seq > ds) detachedSeq[c.Id] = seq;
                pumps.TryGetValue(c.Id, out p);
                if (p != null && p.AttachSeq > seq) { curSeq = p.AttachSeq; p = null; t = null; }
                else
                {
                    pumps.Remove(c.Id);
                    trackers.TryGetValue(c.Id, out t);
                    // 재연결 줄 지우기(§3.3): 이 세션의 attach 를 Hub 가 반영했을 때만 다음 세션 예약을 다시 정한다(ArmFromSession).
                    // 반영 못 한 세션(attach 통지보다 detach 가 먼저 옴)은 추적기가 그 세션을 보지 못했으니 앞 세션에서 넘어온 예약을 그대로 둔다.
                    // 펌프 제거와 같은 잠금 안에서 정한다: 그 사이 attach(s+1)(펌프 없음 → clearNext 만 봄)나 detach(s+1) 의 MarkDetached 가 끼지 않게.
                    // svc 안에서 추적기 sync 를 잡고(ShellLineEditing·ReplugClearBlockedFor, 중첩 없이 차례로) 송신 예약은 잠금 없이 읽는다(Cable.ReplugClearPending). 두 잠금 모두 svc 를 다시 잡는 경로 없음.
                    if (p != null && p.AttachSeq == seq)
                    {
                        if (ArmFromSession(c, t, seq)) clearNext.Add(c.Id); else clearNext.Remove(c.Id);
                    }
                }
            }
            if (curSeq > 0) { Log.Write(L.T("늦은 detach 이벤트 무시 " + c.Id + " seq=" + seq + " (현재 세션 " + curSeq + ")", "late detach event ignored " + c.Id + " seq=" + seq + " (current session " + curSeq + ")")); return; }
            p?.Stop();                                              // 앞 세션 펌프를 기다리는 중이어도 곧 끝난다(기다림이 stop 을 본다)
            t?.MarkDetached(Clock.MonoMs);
        }

        /// <summary>
        /// attach 직후(펌프 스레드): writer 가 이 attach 를 처리해 이전 파일을 닫을 때까지 기다린 뒤, 같은 포트의 직전 .raw 끝을 재생해 복원한다.
        /// 첫 attach(이 프로세스) = 코어 재시작 출처(공백은 이전 코어의 alive 하트비트 기준), 그 뒤 attach = 다시 꽂기 출처.
        /// 직전 파일이 없으면 UNKNOWN 으로 시작한다. 반환값 = 링 구독 시작 위치(attach 시점 링 End).
        /// </summary>
        long PrepareTracker(Cable c, PromptTracker t, RestoreSource src, long seq, Func<bool> cancelled)
        {
            TestBeforePrepare?.Invoke(c);
            long resume;
            bool superseded;
            lock (c.Sync) { superseded = c.AttachSeq != seq; resume = c.AttachRxPos; }
            // 멈추라는 요청을 받았거나 더 새 세션이 시작된 펌프는 복원하지 않는다(늦은 복원이 다음 세션의 processed 를 되감지 않게).
            // 확인과 적용 사이의 경합은 추적기의 세션 순번 검사(ResetFresh/Restore session)가 막는다.
            if (superseded || cancelled()) return resume;
            long deadline = Environment.TickCount64 + 3000;
            while (c.Writer.AttachedSeq < seq && Environment.TickCount64 < deadline && !cancelled()) Thread.Sleep(5);
            if (cancelled()) return resume;
            t.SetBoardIfUnknown(c.Board);
            string prev = HubLogReplay.FindLatestRaw(Config.LogRoot, c.TcpPort, c.Writer.CurrentRawPath);
            if (prev == null)
            {
                if (!t.ResetFresh(resume, Clock.MonoMs, seq)) Log.Write(L.T("tracker 새 세션 시작 거부 " + c.Id + " seq=" + seq + " (더 새 세션이 이미 복원됨)", "tracker new session refused " + c.Id + " seq=" + seq + " (a newer session was already restored)"));
                return resume;
            }
            double gapS = 0;
            if (src == RestoreSource.CoreRestart)
            {
                DateTime last;
                bool have;
                lock (svc) have = lastAlive.TryGetValue(c.TcpPort, out last);
                if (!have) last = File.GetLastWriteTime(prev);
                gapS = Math.Max(0, (Clock.Now - last).TotalSeconds);
            }
            var items = HubLogReplay.LoadTail(prev, Path.ChangeExtension(prev, ".idx"), Config.RestoreTailBytes);
            if (cancelled() || !t.Restore(items, src, gapS, resume, Clock.MonoMs, seq))
            {
                Log.Write(L.T("tracker 복원 건너뜀 " + c.Id + " seq=" + seq + " (멈춤 요청 또는 더 새 세션이 이미 복원됨)", "tracker restore skipped " + c.Id + " seq=" + seq + " (stop requested or a newer session was already restored)"));
                return resume;
            }
            var sn = t.Snapshot();
            Log.Write(L.T("tracker 복원 " + c.Id + " src=" + src + " gap=" + gapS.ToString("0.0", CultureInfo.InvariantCulture) + "s from="
                      + Path.GetFileName(prev) + " → " + sn.StateName + (sn.BlockedReason == null ? "" : " (" + sn.BlockedReason + ")"),
                      "tracker restored " + c.Id + " src=" + src + " gap=" + gapS.ToString("0.0", CultureInfo.InvariantCulture) + "s from="
                      + Path.GetFileName(prev) + " → " + sn.StateName + (sn.BlockedReason == null ? "" : " (" + sn.BlockedReason + ")")));
            return resume;
        }

        /// <summary>DiskWriter 첫 attach 머리 줄(writer 스레드). 공백 = 지금 − 이전 코어의 마지막 하트비트(last_alive).</summary>
        IReadOnlyList<string> RestartPreamble(int port)
        {
            string reason = RestartReason;
            if (string.IsNullOrEmpty(reason)) return Array.Empty<string>();
            var l = new List<string> { "*** hub restart (" + reason + ") ***" };
            DateTime last;
            bool have;
            lock (svc) have = lastAlive.TryGetValue(port, out last);
            if (have) l.Add("*** gap " + Math.Max(0, (Clock.Now - last).TotalSeconds).ToString("0.0", CultureInfo.InvariantCulture) + " (hub down) ***");
            return l;
        }

        static Dictionary<int, DateTime> ReadAlive(string dir)
        {
            var d = new Dictionary<int, DateTime>();
            if (!Directory.Exists(dir)) return d;
            foreach (var f in Directory.GetFiles(dir))
            {
                if (!int.TryParse(Path.GetFileName(f), NumberStyles.Integer, CultureInfo.InvariantCulture, out int port)) continue;
                try
                {
                    if (DateTime.TryParseExact(File.ReadAllText(f).Trim(), "yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
                        d[port] = t;
                }
                catch (Exception) { }
            }
            return d;
        }

        public void Start()
        {
            if (started) return;
            started = true;
            Log.Write("hub start v" + Version + " pid=" + Environment.ProcessId + " log_root=" + Config.LogRoot
                      + (string.IsNullOrEmpty(RestartReason) ? "" : " restart=(" + RestartReason + ")"));
            Cables.Start();
            Control.Start();
            Spool.Start();
        }

        public void Stop()
        {
            if (!started || stopped) return;
            List<TrackerPump> ps;
            lock (svc)
            {
                stopped = true;
                ps = pumps.Values.ToList();
                pumps.Clear();
            }
            Control.Stop();
            Spool.Stop();
            Telnet.Stop();
            Arbiter.Stop();
            foreach (var p in ps) p.Stop();
            Cables.Stop();
            Log.Write("hub stop");
        }

        /// <summary>모든 케이블의 큐가 비고 flush 될 때까지 대기.</summary>
        public bool WaitDrained(int timeoutMs)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            foreach (var c in Cables.Snapshot())
            {
                long left = deadline - Environment.TickCount64;
                if (left <= 0 || !c.WaitDrained((int)left)) return false;
            }
            return true;
        }

        public void Dispose() => Stop();
    }
}
