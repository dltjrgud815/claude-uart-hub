// 케이블(=FTDI 하드웨어 ID) 하나의 기록 코어 묶음: 고정 TCP 포트, 링 버퍼, 메모리 큐, 디스크 writer, 현재 IPort.
using System;
using System.Threading;

namespace UartHub
{
    /// <summary>Pending(C-3): 처음 보는 케이블을 ask_new_cables 로 확인 대기 중(포트 0·writer 없음·COM 미개방, 영속하지 않음).</summary>
    public enum CableStatus { Detached, Attached, Released, Ignored, HeldByOther, OpenFailed, Stopped, Pending }

    /// <summary>
    /// TX 출처(§3.2 PromptTracker 입력). 허브가 COM 의 유일한 writer 이므로 모든 송신에 출처가 붙는다.
    /// TerminalReply: 터미널이 보낸 CPR 응답(ESC[r;cR) 등 사람 입력이 아닌 프로토콜 바이트(보드로는 전달, 판정에서는 제외).
    /// telnet IAC 협상·NOP 은 Telnet 층에서 소비되어 COM 에 도달하지 않는다.
    /// </summary>
    public enum TxSource { Human, Claude, Spool, Probe, TerminalReply }

    /// <summary>송신 관측 한 건. RxPos = 송신 직전 링 End(이 값보다 앞의 RX 바이트는 송신 전에 도착).</summary>
    public sealed class TxRecord
    {
        public TxSource Source;
        public byte[] Data;
        public long MonoMs;
        public DateTime Wall;
        public long RxPos;
    }

    public sealed class Cable
    {
        internal readonly object Sync = new object();
        readonly object txLock = new object();
        readonly IClock clock;
        readonly HubLog hlog;
        long rxBytes, txBytes;
        long humanTxSeq, lastHumanTxMs = long.MinValue;
        long replugClearSeq;                 // >0 = 이 attach 세션(AttachSeq)의 첫 입력 앞에 ^E^U (txLock)
        static readonly byte[] ClearPrefix = { 0x05, 0x15 };

        /// <summary>재연결 줄 지우기(§3.3)를 실제로 보낼 때 .log 에 남기는 이벤트 줄 접두. 상태 복원 재생은 이 줄을 ^E^U 송신으로 본다.</summary>
        public const string ReplugClearEvent = ">>> [hub-clear] ^E^U before first input after replug";

        public string Id { get; }
        /// <summary>고정 TCP 포트(state.json). ignore·Pending 케이블은 0.</summary>
        public int TcpPort { get; }
        public string Alias { get; }
        public string Mode { get; }
        /// <summary>보오(config cables.&lt;id&gt;.baud, 기본 115200). /connect-uart COMx &lt;baud&gt; 가 바꾸면(CableManager.SetBaud) 다음 attach 부터 그 값으로 연다.</summary>
        public int Baud { get; internal set; }
        public string Com { get; internal set; }
        /// <summary>장치 이름(WMI Win32_PnPEntity.Name, 예: "USB Serial Port (COM5)"). 스캔마다 열거 값으로 갱신. 없으면 null.</summary>
        public string Description { get; internal set; }
        public CableStatus Status { get; internal set; } = CableStatus.Detached;
        public string LastError { get; internal set; }
        public DateTime? AttachedAt { get; internal set; }

        /// <summary>RX 링(구독 전용). ignore 케이블은 null.</summary>
        public RingBuffer Ring { get; }
        public DiskWriter Writer { get; }
        internal ChunkQueue Queue { get; }
        internal IPort Port;
        internal PortReader Reader;
        internal int MissingScans;

        /// <summary>attach 순번(1부터). DiskWriter 가 같은 순번의 attach 를 처리하면 AttachedSeq 가 따라온다.</summary>
        public long AttachSeq { get; internal set; }
        /// <summary>attach 시점(리더 시작 전)의 링 End. 서비스 층 구독은 여기서 시작하면 attach 뒤 바이트를 빠짐없이 본다.</summary>
        public long AttachRxPos { get; internal set; }

        /// <summary>
        /// 송신 관측(§3.2). 바이트를 포트에 쓰기 직전, TX 잠금 안에서 송신 순서대로 호출된다(보드 응답보다 먼저 관측되도록).
        /// 처리기는 짧아야 하고 Cable.Write 를 다시 부르면 안 된다. 처리기 예외는 송신을 막지 않고 hub.log 에 남는다(같은 처리기·예외는 10초에 한 줄).
        /// </summary>
        public event Action<TxRecord> Transmitted;

        /// <summary>
        /// 사람 TX 순번(P2): Human 출처 송신마다 TX 잠금 안에서 1 증가한다. Arbiter 는 게이트 판정 때 이 값을 잡아 두고
        /// TryWriteIf 로 "그 뒤 사람 입력이 없을 때만" 쓴다(판정과 쓰기 사이·조각 사이 경합 창을 닫는다).
        /// </summary>
        public long HumanTxSeq => Interlocked.Read(ref humanTxSeq);
        /// <summary>마지막 사람 TX 시각(MonoMs, 송신 시점). 추적기 반영(링 순서 보류)보다 앞설 수 있다. 없으면 long.MinValue.</summary>
        public long LastHumanTxMs => Interlocked.Read(ref lastHumanTxMs);

        /// <summary>
        /// 재연결 줄 지우기(§3.3, 실보드 11-03): 케이블을 뽑을 때 보드 RX 가 흔들려 프레이밍 오류·break 바이트(약 6개)가 ash 줄 버퍼에 '??????' 로
        /// 남고, 다음 Enter 에서 glob 으로 실행된다(/ 에서는 chroot). Hub 가 detach 때 셸 프롬프트(AT_PROMPT·AT_UBOOT)였던 케이블의 다음 attach
        /// 세션에 이것을 예약하면, 그 세션에서 보드에 처음 닿는 입력(사람 telnet·.tx spool·Claude send/run)의 같은 조건부 쓰기 앞에 ^E(줄 끝)^U(지우기)를
        /// 붙이고 예약을 푼다. probe 는 스스로 ^E^U 를 보내므로 붙이지 않고 예약만 푼다. TerminalReply 는 건드리지 않는다.
        /// </summary>
        public void ArmReplugClear(long attachSeq) { lock (txLock) replugClearSeq = attachSeq; }

        /// <summary>attachSeq 세션의 예약이 아직 남았는가(보내지도 풀리지도 않음).</summary>
        /// 읽기는 잠금 없이(Interlocked.Read) 한다: Hub.OnCableDetached 가 svc 잠금 안에서 부르므로, 분리 순간 진행 중인 송신(p.Write, WriteTimeout 2초)이
        /// txLock 을 쥐고 있어도 전역 svc 가 막히지 않게(쓰기는 그대로 txLock 안). 읽은 값이 한 순간 낡아도 최악은 ^E^U 한 번 더·덜이다.
        public bool ReplugClearPending(long attachSeq) { long s = Interlocked.Read(ref replugClearSeq); return s > 0 && s == attachSeq; }

        /// <summary>
        /// 예약을 막는 조건(서비스 층이 설정, 인자 = 이 attach 세션 순번 — 추적기가 그 세션 복원 뒤 부팅·FULLSCREEN 을 봤다: 옛 줄 버퍼가 없거나 셸 줄이 아니다).
        /// 참이면 보내지 않고 예약을 푼다. 그 세션 복원 전이면(추적기가 새 세션 RX 를 아직 안 봄) 막지 않아야 한다.
        /// 송신 잠금 안에서 불리므로 짧아야 하고 Cable.Write 를 다시 부르면 안 된다(Transmitted 처리기와 같은 규칙).
        /// </summary>
        public Func<long, bool> ReplugClearBlocked { get; set; }

        /// <summary>config cables.&lt;id&gt;.replug_clear(기본 true). false 면 이 케이블은 재연결 줄 지우기를 예약하지 않는다(셸이 아닌 장치로 옮겨 꽂는 케이블 등).</summary>
        public bool ReplugClearEnabled { get; }

        internal Cable(string id, int tcpPort, CableConfig cc, Config cfg, IClock clock, HubLog hlog, StateStore state, long epoch)
        {
            Id = id; TcpPort = tcpPort; Alias = cc.Alias; Mode = cc.Mode; Baud = cc.Baud; ReplugClearEnabled = cc.ReplugClear;
            this.clock = clock;
            this.hlog = hlog;
            if (tcpPort > 0)
            {
                Ring = new RingBuffer(cfg.RingBytes, cfg.RingChunks, epoch);
                Queue = new ChunkQueue(cfg.QueueBytes);
                Writer = new DiskWriter(this, cfg, clock, hlog, state, Queue);
            }
        }

        public string StatusText => Status switch
        {
            CableStatus.Attached => "attached",
            CableStatus.Detached => "detached",
            CableStatus.Released => "released",
            CableStatus.Ignored => "ignored",
            CableStatus.HeldByOther => "held_by_other",
            CableStatus.OpenFailed => "open_failed",
            CableStatus.Stopped => "stopped",
            CableStatus.Pending => "pending",
            _ => Status.ToString().ToLowerInvariant()
        };

        public string Board => Writer?.Board;
        public long RxBytes => Interlocked.Read(ref rxBytes);
        public long TxBytes => Interlocked.Read(ref txBytes);
        public long QueueBytes => Queue?.Bytes ?? 0;
        public long QueueDroppedBytes => Queue?.DroppedTotal ?? 0;
        public bool IsAttached => Status == CableStatus.Attached;

        internal void AddRx(int n) => Interlocked.Add(ref rxBytes, n);

        /// <summary>
        /// 로그 이벤트 줄(예: "*** gap 12 (hub down) ***", ">>> [claude r1] cmd"). DiskWriter 가 순서대로 기록한다.
        /// 분리 중이면 기록할 파일이 없어 버려진다.
        /// </summary>
        public void PostEvent(string text)
        {
            Queue?.EnqueueControl(new QItem { Kind = QKind.Event, Text = text, Mono = clock.MonoMs, Wall = clock.Now });
        }

        /// <summary>
        /// COM 송신(허브가 유일한 writer, TX 단일 경로). 출처는 PromptTracker 판정에 쓰인다. 분리 중이면 false.
        /// 관측(Transmitted)은 바이트를 내보내기 **직전**에 알린다: 보드 응답(에코·프롬프트)은 port.Write 안에서 이미 생길 수 있으므로,
        /// 송신 뒤에 알리면 송신 스레드가 잠깐 멈춘 사이 추적기가 응답을 먼저 처리해 TX 를 응답 뒤에 놓는다(프롬프트 확정이 뒤집혀 BUSY 고착).
        /// 쓰기가 실패하면 장치가 사라진 것이라 곧 detach 로 추적기 상태가 초기화된다.
        /// </summary>
        public bool Write(byte[] data, int offset, int count, TxSource source) => WriteCore(data, offset, count, source, -1, null);

        public bool Write(byte[] data, TxSource source) => Write(data, 0, data.Length, source);

        /// <summary>
        /// 조건부 송신(P2): TX 잠금 안에서 HumanTxSeq == expectedHumanSeq 일 때만 쓴다(아니면 아무것도 쓰지 않고 false).
        /// 사람 송신도 같은 잠금 안에서 순번을 올리므로 비교와 쓰기 사이에 사람 바이트가 끼어들 수 없다.
        /// eventBefore 는 쓰기가 확정된 뒤·바이트 직전에 로그 이벤트로 넣는다(보내지 않은 요청의 "&gt;&gt;&gt;" 줄이 남지 않게).
        /// false 의 원인은 호출자가 HumanTxSeq 를 다시 읽어 가린다(달라졌으면 사람 입력, 같으면 분리·포트 오류).
        /// </summary>
        public bool TryWriteIf(byte[] data, int offset, int count, TxSource source, long expectedHumanSeq, string eventBefore = null)
            => WriteCore(data, offset, count, source, expectedHumanSeq, eventBefore);

        public bool TryWriteIf(byte[] data, TxSource source, long expectedHumanSeq, string eventBefore = null)
            => WriteCore(data, 0, data.Length, source, expectedHumanSeq, eventBefore);

        bool WriteCore(byte[] data, int offset, int count, TxSource source, long expectedHumanSeq, string eventBefore)
        {
            lock (txLock)
            {
                var p = Port;
                if (p == null || Status != CableStatus.Attached) return false;
                if (expectedHumanSeq >= 0 && Interlocked.Read(ref humanTxSeq) != expectedHumanSeq) return false;
                bool clear = false, probeKill = false;
                if (replugClearSeq > 0 && count > 0 && source != TxSource.TerminalReply)
                {
                    if (replugClearSeq != AttachSeq) replugClearSeq = 0;                              // 지난 세션의 예약
                    else if (source == TxSource.Probe)
                        probeKill = Array.IndexOf(data, (byte)0x15, offset, count) >= 0;              // probe 는 스스로 ^E^U: ^U 를 실제로 보낸 쓰기에서만 예약을 쓴다(^E 뒤 중단이면 남김)
                    else if (ClearBlocked(AttachSeq)) { replugClearSeq = 0; hlog?.Write(L.T("replug clear 해제 " + Id + " — 재연결 뒤 부팅·전체화면(보내지 않음)", "replug clear cancelled " + Id + " — boot or full-screen after reconnect (not sent)")); }
                    else clear = true;
                }
                if (clear)
                {
                    PostEvent(ReplugClearEvent + " (" + source.ToString().ToLowerInvariant() + ")");
                    var buf = new byte[ClearPrefix.Length + count];
                    Buffer.BlockCopy(ClearPrefix, 0, buf, 0, ClearPrefix.Length);
                    Buffer.BlockCopy(data, offset, buf, ClearPrefix.Length, count);
                    data = buf; offset = 0; count = buf.Length;             // 사람 출처면 접두도 그 사람 쓰기의 일부(같은 TX 관측·같은 조건부 쓰기)
                }
                if (eventBefore != null) PostEvent(eventBefore);
                long rxPos = Ring?.End ?? 0;
                long ms = clock.MonoMs;
                if (source == TxSource.Human && count > 0)
                {
                    Interlocked.Exchange(ref lastHumanTxMs, ms);       // 순번보다 먼저: 순번을 읽은 쪽은 그만큼 새 시각을 본다
                    Interlocked.Increment(ref humanTxSeq);
                }
                var h = Transmitted;
                if (h != null && count > 0)
                {
                    var copy = new byte[count];
                    Buffer.BlockCopy(data, offset, copy, 0, count);
                    var rec = new TxRecord { Source = source, Data = copy, MonoMs = ms, Wall = clock.Now, RxPos = rxPos };
                    foreach (Action<TxRecord> one in h.GetInvocationList())
                    {
                        try { one(rec); }
                        catch (Exception e)
                        {
                            // 관측 실패는 송신을 막지 않는다. 다만 침묵하지 않는다(P1): 같은 처리기·예외는 10초에 한 줄.
                            string who = (one.Method.DeclaringType?.Name ?? "?") + "." + one.Method.Name;
                            hlog?.WriteThrottled("tx-handler|" + Id + "|" + who + "|" + e.GetType().Name,
                                                 L.T("Transmitted 처리기 예외 " + Id + " (" + who + "): " + e.GetType().Name + ": " + e.Message, "Transmitted handler exception " + Id + " (" + who + "): " + e.GetType().Name + ": " + e.Message));
                        }
                    }
                }
                try
                {
                    p.Write(data, offset, count);
                    Interlocked.Add(ref txBytes, count);
                    if (clear || probeKill) replugClearSeq = 0;
                }
                catch (Exception e)
                {
                    LastError = "tx: " + e.GetType().Name + ": " + e.Message;
                    return false;
                }
                return true;
            }
        }

        bool ClearBlocked(long seq)
        {
            try { return ReplugClearBlocked?.Invoke(seq) == true; }
            catch (Exception e) { hlog?.WriteThrottled("replug-clear|" + Id, L.T("ReplugClearBlocked 예외 " + Id + ": " + e.GetType().Name + ": " + e.Message, "ReplugClearBlocked exception " + Id + ": " + e.GetType().Name + ": " + e.Message)); return true; }
        }

        /// <summary>
        /// break 송신. expectedHumanSeq ≥ 0 이면 TryWriteIf 와 같은 조건부(사람 입력이 끼었으면 보내지 않고 false).
        /// human=true(telnet IAC BRK)면 사람 입력으로 보고 HumanTxSeq·LastHumanTxMs 를 올린다 — 직후 Claude 송신이 끼지 않게.
        /// </summary>
        public bool SendBreak(int ms, long expectedHumanSeq = -1, string eventBefore = null, bool human = false)
        {
            lock (txLock)
            {
                var p = Port;
                if (p == null || Status != CableStatus.Attached) return false;
                if (expectedHumanSeq >= 0 && Interlocked.Read(ref humanTxSeq) != expectedHumanSeq) return false;
                if (eventBefore != null) PostEvent(eventBefore);
                if (human)
                {
                    Interlocked.Exchange(ref lastHumanTxMs, clock.MonoMs);   // WriteCore 와 같은 순서: 시각 먼저, 순번 나중
                    Interlocked.Increment(ref humanTxSeq);
                }
                try { p.SendBreak(ms); return true; }
                catch (Exception e) { LastError = "break: " + e.GetType().Name + ": " + e.Message; return false; }
            }
        }

        /// <summary>큐가 비고 writer 가 flush 까지 마칠 때까지 대기(시험·종료용).</summary>
        public bool WaitDrained(int timeoutMs)
        {
            if (Writer == null) return true;
            long deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                if (Writer.IsIdle) return true;
                Thread.Sleep(5);
            }
            return Writer.IsIdle;
        }

        public override string ToString() => Id + " :" + TcpPort + " " + Com + " " + StatusText + " board=" + (Board ?? "unknown");
    }
}
