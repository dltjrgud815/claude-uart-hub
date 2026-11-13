// 케이블 열거·배정·attach/detach(§3, S0-6). IPort 공급자를 주기적으로 스캔해 케이블ID ↔ COM 을 맞추고,
// 케이블ID → TCP 포트는 state.json 으로 영속한다. reader 예외는 타입 무관 detach, 다음 스캔에서 재연결.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace UartHub
{
    public sealed class CableManager
    {
        readonly Config cfg;
        readonly IClock clock;
        readonly StateStore state;
        readonly HubLog hlog;
        readonly List<IPortProvider> providers;
        readonly object sync = new object();
        readonly Dictionary<string, Cable> cables = new Dictionary<string, Cable>(StringComparer.OrdinalIgnoreCase);
        readonly List<Cable> retiredObjs = new List<Cable>();                 // 미연결 UART 정리로 목록에서 뺀 분리된 객체 — 정지 때 writer·링을 마저 끝낸다
        HashSet<string> present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // 마지막 열거에서 보인 케이블(꽂혀 있음)
        readonly ManualResetEventSlim stopEvt = new ManualResetEventSlim(false);
        /// <summary>보오를 바꾼 케이블 → 다음 attach 뒤 .log 에 남길 줄("*** baud 115200 -> 9600 ***"). Pending 객체가 새로 만들어져도 남는다(케이블 ID 기준).</summary>
        readonly Dictionary<string, string> baudNotes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly long epoch;
        Thread scanThread;
        volatile bool stopping;

        /// <summary>
        /// attach 직후(리더 시작 뒤) 호출. 인자 = (케이블, 그 attach 의 AttachSeq). 서비스 층(telnet 등)이 구독한다. C# 스레드에서 호출되므로 PS scriptblock 금지.
        /// attach/detach 통지는 케이블 잠금 밖에서 나가므로(처리기가 무거움) 늦게 도착할 수 있다: 처리기는 순번으로 이미 지난 세션의 통지를 거른다.
        /// </summary>
        public event Action<Cable, long> CableAttached;

        /// <summary>케이블 첫 attach 의 "*** hub start ***" 다음 줄 공급자(DiskWriter.StartPreamble). Start() 전에 설정한다.</summary>
        public Func<int, IReadOnlyList<string>> StartPreamble { get; set; }
        /// <summary>detach 뒤 호출. 인자 = (케이블, 끝난 세션의 AttachSeq). 그 사이 재attach 됐으면 케이블의 현재 AttachSeq 가 더 크다.</summary>
        public event Action<Cable, long> CableDetached;

        public long Epoch => epoch;

        /// <summary>시험 훅: detach 이벤트를 알리기 직전(케이블 잠금 밖, detach 한 스레드). 늦은 detach 경합 재현용. 운영에서는 null.</summary>
        public Action<Cable> TestBeforeDetachFire;
        /// <summary>시험 훅: attach 이벤트를 알리기 직전(케이블 잠금 밖, 스캔 스레드, 리더는 이미 돈다). 늦은 attach 경합 재현용. 운영에서는 null.</summary>
        public Action<Cable> TestBeforeAttachFire;

        public CableManager(Config cfg, IClock clock, StateStore state, HubLog hlog, IEnumerable<IPortProvider> providers)
        {
            this.cfg = cfg; this.clock = clock; this.state = state; this.hlog = hlog;
            this.providers = providers.ToList();
            epoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        public IReadOnlyList<Cable> Snapshot()
        {
            lock (sync) return cables.Values.OrderBy(c => c.TcpPort == 0 ? int.MaxValue : c.TcpPort).ThenBy(c => c.Id).ToList();
        }

        public Cable Find(string cableId)
        {
            lock (sync) return cables.TryGetValue(cableId, out var c) ? c : null;
        }

        public void Start()
        {
            ScanOnce();
            scanThread = new Thread(() =>
            {
                while (!stopping)
                {
                    if (stopEvt.Wait(cfg.ScanIntervalMs)) break;
                    try { ScanOnce(); } catch (Exception e) { hlog.Write(L.T("scan 오류: " + e.GetType().Name + ": " + e.Message, "scan error: " + e.GetType().Name + ": " + e.Message)); }
                }
            }) { IsBackground = true, Name = "CableScan" };
            scanThread.Start();
        }

        public void ScanOnce()
        {
            if (stopping) return;
            var seen = new List<PortInfo>();
            foreach (var p in providers)
            {
                try { seen.AddRange(p.Scan()); }
                catch (Exception e) { hlog.Write(L.T("열거 실패(" + p.GetType().Name + "): " + e.Message, "enumeration failed (" + p.GetType().Name + "): " + e.Message)); }
            }
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var info in seen) if (!string.IsNullOrEmpty(info.CableId)) seenIds.Add(info.CableId);
            lock (sync) present = new HashSet<string>(seenIds, StringComparer.OrdinalIgnoreCase);
            seenIds.Clear();
            foreach (var info in seen)
            {
                if (string.IsNullOrEmpty(info.CableId) || !seenIds.Add(info.CableId)) continue;
                var c = GetOrCreate(info);
                bool attached;
                long seq;
                lock (c.Sync)
                {
                    if (stopping) return;
                    c.MissingScans = 0;
                    if (c.Status == CableStatus.Attached) continue;
                    c.Com = info.Com;
                    c.Description = info.Description;
                    if (c.Mode == "ignore") { c.Status = CableStatus.Ignored; continue; }
                    if (state.IsReleased(c.Id)) { c.Status = CableStatus.Released; continue; }
                    if (c.TcpPort == 0) { c.Status = CableStatus.Pending; continue; }   // C-3: 생성 때 pending 으로 만든 객체(포트 0) — 답(Decide)이 올 때까지 COM 미개방
                    attached = TryAttachLocked(c, info);
                    seq = c.AttachSeq;
                }
                if (attached)
                {
                    TestBeforeAttachFire?.Invoke(c);
                    Fire(CableAttached, c, seq);            // 구독자 처리기는 케이블 잠금 밖에서 부른다(순번으로 늦은 통지를 가린다)
                }
            }
            // 열거에서 사라졌는데 reader 가 예외를 못 받은 경우: 2회 연속이면 강제 detach
            foreach (var c in Snapshot())
            {
                if (c.Status != CableStatus.Attached || seenIds.Contains(c.Id)) continue;
                if (++c.MissingScans >= 2) ForceDetach(c, "enumeration lost");
            }
        }

        Cable GetOrCreate(PortInfo info)
        {
            lock (sync)
            {
                if (cables.TryGetValue(info.CableId, out var c)) return c;
                var cc = cfg.CableFor(info.CableId);
                // C-3: 포트 0·writer 없음은 ignore 와 Pending 뿐. Pending = 물어본 적 없음(assign·released·config mode 명시 모두 없음) + ask_new_cables.
                // X5(부록 B.4): on_demand 도 같은 Pending(포트 0·COM 미개방) — 포트는 /connect-uart 때(cable decide board) 배정한다. 묻는 창·풍선은 감독이 띄우지 않는다.
                // released 는 Phase A 처럼 배정 여부와 상관없이 포트를 받는다(spool-status 전제).
                bool pending = cc.Mode != "ignore" && (cfg.AskNewCables || cfg.OnDemand) && !cc.ModeSet && !state.HasAssign(info.CableId) && !state.IsReleased(info.CableId);
                int port;
                if (cc.Mode == "ignore" || pending) port = 0;
                else port = state.GetOrAssignPort(info.CableId, cfg.BasePort, cfg.ControlPort);
                c = new Cable(info.CableId, port, cc, cfg, clock, hlog, state, epoch) { Com = info.Com, Description = info.Description };
                if (pending) c.Status = CableStatus.Pending;
                cables[info.CableId] = c;
                if (c.Writer != null) c.Writer.StartPreamble = StartPreamble;
                c.Writer?.Start();
                hlog.Write(L.T("케이블 등록 " + info.CableId + " → :" + port + (cc.Mode == "ignore" ? " (ignore)" : pending ? " (pending)" : ""), "Cable registered " + info.CableId + " → :" + port + (cc.Mode == "ignore" ? " (ignore)" : pending ? " (pending)" : "")));
                if (pending) hlog.Write("cable pending " + info.CableId + " " + info.Com + (cfg.OnDemand ? L.T(" — 연결 대기(on_demand: /connect-uart 전까지 COM 을 열지 않음, 창·풍선 없음)", " — waiting to connect (on_demand: COM not opened until /connect-uart, no window or balloon)") : L.T(" — 첫 연결 확인 대기(COM 을 열지 않음)", " — waiting for first-connection answer (COM not opened)")));
                return c;
            }
        }

        bool TryAttachLocked(Cable c, PortInfo info)
        {
            IPort port = null;
            try
            {
                port = info.Provider.Create(info, c.Baud, cfg);
                port.Open();
            }
            catch (Exception e)
            {
                var st = e is UnauthorizedAccessException ? CableStatus.HeldByOther : CableStatus.OpenFailed;
                if (c.Status != st || c.LastError != e.Message) hlog.Write(L.T("열기 실패 " + c.Id + " " + info.Com + ": " + e.GetType().Name + ": " + e.Message, "open failed " + c.Id + " " + info.Com + ": " + e.GetType().Name + ": " + e.Message));
                c.Status = st;
                c.LastError = e.Message;
                try { port?.Dispose(); } catch (Exception) { }
                return false;
            }
            c.Port = port;
            c.Com = info.Com;
            c.AttachSeq++;
            c.AttachRxPos = c.Ring.End;                 // 리더 시작 전: 이후 바이트는 모두 이 attach 의 것
            c.Status = CableStatus.Attached;
            c.AttachedAt = clock.Now;
            c.LastError = null;
            c.Queue.EnqueueControl(new QItem { Kind = QKind.Attach, Com = info.Com, Seq = c.AttachSeq, Mono = clock.MonoMs, Wall = clock.Now });
            string note;
            lock (baudNotes) { if (baudNotes.TryGetValue(c.Id, out note)) baudNotes.Remove(c.Id); }
            if (note != null) c.PostEvent(note);                      // 새 보오로 연 세션의 attach 줄 바로 뒤

            var r = new PortReader(c, port, clock, 65536, OnReaderFailed);
            c.Reader = r;
            r.Start();
            hlog.Write("attach " + c.Id + " " + info.Com + " :" + c.TcpPort);
            return true;
        }

        void OnReaderFailed(PortReader r, Exception ex)
        {
            var c = r.Cable;
            long seq;
            lock (c.Sync)
            {
                if (!ReferenceEquals(c.Reader, r)) return;
                DetachLocked(c, ex.GetType().Name);
                c.LastError = ex.GetType().Name + ": " + ex.Message;
                seq = c.AttachSeq;
            }
            hlog.Write("detach " + c.Id + " " + c.Com + ": " + ex.GetType().Name + ": " + ex.Message);
            TestBeforeDetachFire?.Invoke(c);
            Fire(CableDetached, c, seq);
        }

        void ForceDetach(Cable c, string reason)
        {
            PortReader r;
            long seq;
            lock (c.Sync)
            {
                if (c.Status != CableStatus.Attached) return;
                r = c.Reader;
                r?.RequestStop();
                DetachLocked(c, reason);
                seq = c.AttachSeq;
            }
            r?.Join(2000);
            hlog.Write("detach " + c.Id + " " + c.Com + ": " + reason);
            TestBeforeDetachFire?.Invoke(c);
            Fire(CableDetached, c, seq);
        }

        // ------------------------------------------------------------------ 케이블 판정(C-3, 제어 명령 cable — 쓰기는 코어만)
        /// <summary>
        /// 첫 연결 답(① "예" board / ② "아니오" ignore). 지금 목록의 Pending 객체일 때만: 포트를 배정(영속)하고, ignore 면 released 도 영속한 뒤,
        /// 붙은 적 없는 Pending 객체(writer 없음·AttachSeq 0)를 목록에서 뺀다 — 다음 스캔이 새로 만들어 붙이거나(board) Phase A 의 released 케이블로 만든다(ignore).
        /// Pending 이 아니면(이미 답함·다른 객체) 아무것도 하지 않고 false.
        /// </summary>
        public bool Decide(Cable c, bool board, out int port)
        {
            port = 0;
            lock (sync)
            {
                if (!cables.TryGetValue(c.Id, out var cur) || !ReferenceEquals(cur, c) || c.TcpPort != 0 || c.Mode == "ignore") return false;
                port = state.GetOrAssignPort(c.Id, cfg.BasePort, cfg.ControlPort);
                if (!board) state.SetReleased(c.Id, true);
                cables.Remove(c.Id);
            }
            hlog.Write("cable decide " + c.Id + " " + (board ? "board" : "ignore") + " → :" + port + (board ? "" : L.T(" (released, COM 미개방)", " (released, COM not opened)")));
            return true;
        }

        /// <summary>
        /// ③ 제외: 붙어 있으면 ForceDetach 와 같은 순서(잠금 안에서 reader 정지·DetachLocked → released 영속 → 잠금 밖 join·분리 통지)로 COM 을 닫는다.
        /// 배정은 유지한다. ignore·Pending 객체(포트 0)는 대상이 아니다(false).
        /// </summary>
        public bool Exclude(Cable c)
        {
            if (c.TcpPort <= 0) return false;
            PortReader r = null;
            long seq = 0;
            bool was;
            lock (c.Sync)
            {
                was = c.Status == CableStatus.Attached;
                if (was)
                {
                    r = c.Reader;
                    r?.RequestStop();
                    DetachLocked(c, "excluded");
                    seq = c.AttachSeq;
                }
                state.SetReleased(c.Id, true);
                c.Status = CableStatus.Released;
            }
            if (was)
            {
                r?.Join(2000);
                hlog.Write("detach " + c.Id + " " + c.Com + ": excluded");
                TestBeforeDetachFire?.Invoke(c);
                Fire(CableDetached, c, seq);
            }
            hlog.Write("cable exclude " + c.Id + " :" + c.TcpPort + (was ? L.T(" (COM 닫음)", " (COM closed)") : ""));
            return true;
        }

        /// <summary>④ 해제: released 를 지운다 → 다음 스캔에서 같은 포트로 붙는다. ignore·Pending 객체(포트 0)는 대상이 아니다(false).</summary>
        public bool Include(Cable c)
        {
            if (c.TcpPort <= 0) return false;
            lock (c.Sync)
            {
                state.SetReleased(c.Id, false);
                if (c.Status == CableStatus.Released) c.Status = CableStatus.Detached;
            }
            hlog.Write("cable include " + c.Id + " :" + c.TcpPort);
            return true;
        }

        /// <summary>
        /// 보오 바꾸기(X5 /connect-uart COMx &lt;baud&gt;): 설정(메모리의 cables.&lt;id&gt;.baud — 다음에 만드는 케이블 객체도 이 값)과 지금 객체의 Baud 를 바꾼다.
        /// 붙어 있으면 ForceDetach 와 같은 순서로 COM 을 닫는다(사유 "baud A -> B") — 다음 스캔이 새 보오로 다시 연다(IPort·PortReader 는 그대로, 동결 파일 무변경).
        /// 그 attach 뒤 .log 에 "*** baud A -> B ***". 같은 값이면 아무것도 하지 않고 false. config.json 저장은 호출자(ControlServer)가 한다.
        /// </summary>
        public bool SetBaud(string cableId, int baud, out int old, out bool reopened)
        {
            reopened = false;
            Cable c;
            lock (sync)
            {
                cables.TryGetValue(cableId, out c);
                var cur = cfg.Cables.TryGetValue(cableId, out var cc0) ? cc0 : null;
                old = c?.Baud ?? cur?.Baud ?? new CableConfig().Baud;
                if (old == baud && (cur == null ? baud == new CableConfig().Baud : cur.Baud == baud)) return false;
                // 읽는 쪽(CableFor — 잠금 없이 읽는 곳도 있음)이 반쯤 바뀐 사전을 보지 않게 새 사전으로 바꿔 끼운다
                var d = new Dictionary<string, CableConfig>(cfg.Cables, StringComparer.OrdinalIgnoreCase);
                var ncc = cur ?? new CableConfig();
                ncc.Baud = baud;
                d[cableId] = ncc;
                cfg.Cables = d;
            }
            string text = "baud " + old.ToString(System.Globalization.CultureInfo.InvariantCulture) + " -> " + baud.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (old != baud) lock (baudNotes) baudNotes[cableId] = "*** " + text + " ***";
            if (c == null) { hlog.Write(L.T("cable baud " + cableId + " " + text + " (케이블 객체 없음 — 다음에 만들 때)", "cable baud " + cableId + " " + text + " (no cable object — applied when it is created)")); return true; }
            PortReader r = null;
            long seq = 0;
            lock (c.Sync)
            {
                c.Baud = baud;
                if (c.Status == CableStatus.Attached && old != baud)
                {
                    r = c.Reader;
                    r?.RequestStop();
                    DetachLocked(c, text);
                    seq = c.AttachSeq;
                    reopened = true;
                }
            }
            if (reopened)
            {
                r?.Join(2000);
                hlog.Write("detach " + c.Id + " " + c.Com + ": " + text);
                TestBeforeDetachFire?.Invoke(c);
                Fire(CableDetached, c, seq);
            }
            hlog.Write("cable baud " + c.Id + " " + c.Com + " " + text + (reopened ? L.T(" — COM 닫음, 다음 스캔에서 새 보오로 다시 엶", " — COM closed, reopened at the new baud on the next scan") : ""));
            return true;
        }

        void DetachLocked(Cable c, string reason)
        {
            var p = c.Port;
            c.Port = null;
            c.Reader = null;
            c.Status = CableStatus.Detached;
            try { p?.Close(); } catch (Exception) { }
            try { p?.Dispose(); } catch (Exception) { }
            c.Queue.EnqueueControl(new QItem { Kind = QKind.Detach, Com = c.Com, Text = reason, Mono = clock.MonoMs, Wall = clock.Now });
        }

        /// <summary>모든 reader 정지 → 각 로그에 "*** hub stop ***" → writer 종료까지 대기.</summary>
        public void Stop(string stopLine = "*** hub stop ***")
        {
            stopping = true;
            stopEvt.Set();
            scanThread?.Join(5000);
            var all = Snapshot();
            lock (sync) all = all.Concat(retiredObjs).ToList();
            foreach (var c in all)
            {
                PortReader r;
                lock (c.Sync)
                {
                    r = c.Reader;
                    r?.RequestStop();
                    var p = c.Port;
                    c.Port = null;
                    c.Reader = null;
                    try { p?.Close(); } catch (Exception) { }
                    try { p?.Dispose(); } catch (Exception) { }
                    if (c.Status == CableStatus.Attached) c.Status = CableStatus.Stopped;
                }
                r?.Join(2000);
                c.Queue?.EnqueueControl(new QItem { Kind = QKind.Stop, Text = stopLine, Mono = clock.MonoMs, Wall = clock.Now });
            }
            foreach (var c in all)
            {
                if (c.Writer != null && !c.Writer.Join(10000)) hlog.Write(L.T("writer 종료 대기 초과: " + c.Id, "writer exit wait timed out: " + c.Id));
                c.Ring?.Close();
            }
        }

        /// <summary>지금 꽂혀 있는가: 마지막 열거에 보였거나 객체가 분리(detached)가 아닌 상태(붙음·pending·점유·열기 실패·제외·무시 …).</summary>
        bool PresentLocked(string id, out string status)
        {
            status = null;
            if (cables.TryGetValue(id, out var c) && c.Status != CableStatus.Detached) status = c.StatusText;
            else if (present.Contains(id)) status = "present";
            return status != null;
        }

        /// <summary>미연결 UART 정리 목록: 포트 배정이 있고 지금 꽂혀 있지 않은 케이블(포트·마지막 보드·이번 실행에서 본 COM).</summary>
        public List<(string Id, int Port, string Board, string Com)> Remembered()
        {
            var l = new List<(string, int, string, string)>();
            lock (sync)
                foreach (var r in state.Remembered())
                    if (!PresentLocked(r.Id, out _)) l.Add((r.Id, r.Port, r.Board, cables.TryGetValue(r.Id, out var c) ? c.Com : null));
            return l;
        }

        /// <summary>
        /// 미연결 UART 정리(트레이 전용 — CLI 는 C-4 로 cable 을 막는다): 꽂혀 있지 않은 케이블의 state.json 기록(assign·released·boards)을 지운다.
        /// 꽂혀 있으면(붙음·pending·held_by_other·open_failed·bind_failed·제외 …) false + 이유. 이번 실행에서 분리된 객체는 목록에서 빼 두어
        /// 다시 꽂히면 새 케이블로 만든다(새 번호 — 지운 번호는 retired_ports 로 다시 주지 않음). hub.log 에 한 줄.
        /// </summary>
        public bool Forget(string cableId, out int port, out string why, out bool noRecord)
        {
            port = -1; why = null; noRecord = false;
            lock (sync)
            {
                if (PresentLocked(cableId, out string st)) { why = L.T("지금 꽂혀 있음(" + st + ")", "currently plugged in (" + st + ")"); port = cables.TryGetValue(cableId, out var pc) ? pc.TcpPort : -1; return false; }
                bool known = state.HasAssign(cableId) || state.IsReleased(cableId) || state.GetBoard(cableId) != null || cables.ContainsKey(cableId);
                if (!known) { why = L.T("기록 없음", "no record"); noRecord = true; return false; }
                port = state.Forget(cableId);
                if (cables.TryGetValue(cableId, out var c)) { if (port < 0) port = c.TcpPort; cables.Remove(cableId); retiredObjs.Add(c); }
            }
            hlog.Write(L.T("cable forget " + cableId + " :" + port + " — 미연결 UART 기록 지움(다시 꽂으면 새 번호)", "cable forget " + cableId + " :" + port + " — disconnected UART record deleted (a new number if plugged in again)"));
            return true;
        }

        void Fire(Action<Cable, long> ev, Cable c, long seq)
        {
            if (ev == null) return;
            try { ev(c, seq); } catch (Exception e) { hlog.Write(L.T("이벤트 처리기 예외: " + e.GetType().Name + ": " + e.Message, "event handler exception: " + e.GetType().Name + ": " + e.Message)); }
        }
    }
}
