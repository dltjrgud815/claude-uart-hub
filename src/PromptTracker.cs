// PromptTracker(계획 §3.2 rev4): 케이블별 입력 쪽 관측 + 소유권 상태기계.
// 입력은 네 가지뿐이다: RX(청크 바이트 + 도착 시각) · TX(출처 + 송신 직전 링 위치) · 시간 진행(Advance) · run 등록(BeginRun/EndRun).
// 시간은 모두 호출자가 준 ms 다(실시간 = 링 청크 도착 시각·IClock.MonoMs, 재생 = 재구성 시각). 그래서 재생과 실시간이 같은 코드로 돈다.
// 모든 판정은 정규화(NUL·ESC[K·SGR 제거, \r 도 줄 시작) 뒤의 문자열로 한다.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace UartHub
{
    public enum PromptState { AtPrompt, HumanTyping, Fullscreen, AtUboot, Booting, Unknown, Busy }
    public enum CandidateVerdict { Pending, Confirmed, Unconfirmed, Foreign }
    public enum RestoreSource { CoreRestart, Attach, Grab }

    /// <summary>프롬프트 후보 한 건(정규화된 RX 에서 prompt_re / uboot_prompt_re 매치).</summary>
    public sealed class PromptCandidate
    {
        public long Start, End;            // 원시 바이트 절대 offset [Start, End)
        public string Host;                // 셸 프롬프트의 호스트명(U-Boot 는 null)
        public bool UBoot;
        public long ArrivalMs;             // 매치 끝 바이트가 든 청크의 도착 시각
        public CandidateVerdict Verdict;
        /// <summary>확정: "open"(T_open 열린 줄) | "e-marker". 미확정: "followed"(바로 이어 바이트) | "tx"(열린 줄 전에 송신). Foreign: "foreign".</summary>
        public string By;
        public long DecidedMs;
        internal long Seq;
        public override string ToString() => Start + "-" + End + " " + (UBoot ? "uboot" : Host) + " " + Verdict.ToString().ToLowerInvariant() + "(" + By + ")";
    }

    public sealed class StateChange
    {
        public long Ms;
        public PromptState State;
        public string Why;
        public override string ToString() => Ms + " " + PromptTracker.Name(State) + (Why == null ? "" : " (" + Why + ")");
    }

    /// <summary>run 봉인 마커(§3.4) 관측. Kind 'B' 또는 'E'. Start/End 는 정규화 전 원시 절대 offset.</summary>
    public sealed class RunMarker
    {
        public string RunId;
        public char Kind;
        public long Start, End;
        public int Rc;
        public long Ms;
    }

    public sealed class TrackerSnapshot
    {
        public PromptState State;
        public string StateName => PromptTracker.Name(State);
        /// <summary>101 응답용(§3.2): partial_line / partial_line_orphaned / fullscreen / busy_since / booting / unknown / serialized. AT_PROMPT·AT_UBOOT 는 null.</summary>
        public string BlockedReason;
        public long SinceMs;
        public bool HumanTyping, Orphaned, RunActive, Unknown, ProbeNeedsUserOk, KernelPhase, ScreenSinceNewline;
        /// <summary>이 세션(연결·복원·새 세션 시작) 뒤 재부팅을 봤는가. false 면 셸 줄에 무엇이 남아 있는지 모른다(자동 깨우기는 줄을 지운 뒤 CR).</summary>
        public bool BootSeen;
        public string RunId, Board;
        public RestoreSource? RestoredFrom;
        public long LastRxMs, LastHumanTxMs, ProcessedPos;
        /// <summary>마지막 커널 줄(printk 타임스탬프·부팅 매치) 시각(부록 C.13, 없으면 long.MinValue).</summary>
        public long LastKernelMs = long.MinValue;
        public int Candidates, Foreign, BootMatches, IgnoredBootMatches, InferredInputs;
        public string[] LastLines;
        /// <summary>사람(telnet) 클라이언트 수. -1 = 아직 모름(telnet 층이 알려 준 적 없음).</summary>
        public int HumanClients;
        /// <summary>HUMAN_TYPING 일 때 현재 줄에서 마지막 프롬프트 후보 뒤의 글자(정규화 뒤). --clear-line 기록용(§8 E5).</summary>
        public string PartialText;
    }

    /// <summary>재생 입력 한 건(상태 복원·골든). IsTx 면 Source/Data 가 송신, 아니면 Abs 위치의 RX 바이트.</summary>
    public sealed class ReplayItem
    {
        public bool IsTx;
        public long Abs;
        public byte[] Data;
        public long Ms;
        public TxSource Source;
        public int LogLine;                // 구 브리지 .log 줄 번호(골든 기대값 대조용, 없으면 0)
        public override string ToString() => (IsTx ? "TX " + Source : "RX") + " @" + Abs + " t=" + Ms + " n=" + (Data?.Length ?? 0) + (LogLine > 0 ? " log:" + LogLine : "");
    }

    public sealed class PromptTracker
    {
        const int Lookback = 256;          // 청크 경계를 넘는 매치를 위해 직전 문자열을 다시 본다
        const int MaxLine = 16384;
        const int MaxHistory = 4096;
        const int MaxCandidates = 4096;
        static readonly Regex KernelTs = new Regex(@"^\[\s*\d+\.\d+\]", RegexOptions.Compiled);
        /// <summary>줄 중간의 printk 타임스탬프(커널 형식 [%5lu.%06lu]). 사람 입력 에코 뒤에 붙은 printk 를 가르는 데만 쓴다(partial).</summary>
        static readonly Regex KernelTsAny = new Regex(@"\[\s*\d+\.\d{6}\]", RegexOptions.Compiled);
        static readonly Regex CprReply = new Regex(@"\x1b\[\d+;\d+R", RegexOptions.Compiled);
        /// <summary>줄 어디에 있든 printk 타임스탬프 값(부록 C.13 — 재부팅 판정·커널 줄 무음). 깨진 글자 뒤에 붙어도("abce[    0.314164]") 잡는다.</summary>
        static readonly Regex KernelTsValue = new Regex(@"\[\s*(\d+\.\d{6})\]", RegexOptions.Compiled);
        /// <summary>B 마커 뒤(명령이 시작된 뒤) 로더 표시를 재부팅으로 볼 앞 RX 무음(실보드 리셋 앞 무음 0.625초 이상 — 덤프된 캡처 로그와 가름).</summary>
        const long LoaderQuietMs = 500;

        readonly object sync = new object();
        readonly Regex promptRe, ubootRe, bootRe, loaderRe;
        readonly bool rebootDetect;        // 부록 C.13 ①(config auto_nudge): run 구간 안의 실제 재부팅을 알아본다. false 면 지금 동작 그대로
        readonly int tOpenMs, quietMs;
        readonly long idleMs;
        readonly double restoreWindowS;

        // ---- 정규화 파서 / 현재 줄
        int escState;                      // 0 일반, 1 ESC 받음, 2 CSI 수집
        readonly StringBuilder csi = new StringBuilder();
        long escAbs;
        readonly StringBuilder line = new StringBuilder();
        readonly List<long> lineAbs = new List<long>();
        int candFrom, bootFrom, markFrom, scanBase;
        bool kernelChecked;
        int lastCandEndIdx = -1;           // 현재 줄의 마지막 후보 끝 인덱스
        bool lastCandForeign;
        readonly List<int> eraseAt = new List<int>();   // 현재 줄에서 지운 ESC[K 의 자리(line 인덱스) — partial 재구성용(지운 줄 끝 지우기)
        string carry = "";                 // printk·줄 접힘으로 앞 줄에 남은 사람 입력 에코(HUMAN_TYPING 동안, partial = carry + 현재 줄)
        bool carryOpen;                    // carry 가 프롬프트 줄에서 시작했다(그 뒤 printk 로 끊긴 비프롬프트 줄의 앞부분만 이어 붙인다)
        int openConfirmedEndIdx = -1;      // 현재 줄에서 열린 줄로 확정된 후보의 끝 인덱스(복원 추론용)
        int eEndIdx = -1;                  // 현재 줄에서 E 마커가 끝난 인덱스(그 뒤 후보만 E 규칙 대상)
        readonly Queue<string> lastLines = new Queue<string>();

        // ---- 진행
        long processed;                    // 처리한 RX 절대 offset 끝
        long curMs = long.MinValue;
        long chunkMs;
        long seq;

        // ---- 사실(seq 로 순서를 비교한다)
        PromptCandidate pending;
        long lastTxSeq, lastHumanTxSeq;
        long lastHumanTxMs = long.MinValue;
        bool humanTyping, typingFs, orphaned;
        bool typingAtPrompt;               // 이번 HUMAN_TYPING 이 셸·U-Boot 프롬프트 줄에서 시작됐다(실행 중 프로그램·전체화면 입력이 아님)
        bool lineStartsWithPrompt;         // 현재 줄(마지막 \r·\n 뒤)이 이 보드의 셸·U-Boot 프롬프트로 시작한다(줄 편집기 다시 그리기 포함, foreign 제외)
        long lastScreenSeq;                // 마지막 화면 주소 지정 CSI(ESC[…H/f, ESC[…r, ESC[?…h 대체 화면) — 전체화면 프로그램. ESC[J·ESC[nA 같은 줄 편집 다시 그리기는 아님
        int humanClients = -1;
        long lastCsiSeq;
        bool csiSinceNewline;
        long lastConfirmSeq;
        int lastConfirmKind;               // 0 없음, 1 셸, 2 U-Boot
        bool bootActive, kernelPhase, bootSinceBoard;
        long lastBootSeq;
        bool unknown = true, probeNeedsUserOk;
        RestoreSource? restoredFrom;
        string board;
        bool runActive, runWindow, runB, runE, runSealed;
        long runBeginSeq;
        string runId;
        Regex runBPartial, runBFinal, runEPartial, runEFinal;
        long lastRxMs = long.MinValue;
        long rxGapMs = long.MaxValue;      // 이번 청크 앞 RX 무음(ms)
        long lastKernelMs = long.MinValue; // 마지막 커널 줄(printk 타임스탬프·부팅 매치)을 본 시각 — 자동 깨우기의 "커널 줄 무음"(C.13 ②)
        double lastKts = -1;               // run 구간 밖에서 마지막으로 본 커널 타임스탬프(이 부팅) — run 중 값이 크게 줄면 재부팅
        bool restoreMode;
        bool replugBlock;                  // 이 세션(ResetFresh/Restore 뒤)에서 BOOTING·FULLSCREEN 을 봤다 — 재연결 줄 지우기 해제(§3.3)
        long sessionSeq;                   // 마지막으로 받아들인 복원 세션(attach 순번). 더 오래된 세션의 늦은 복원은 거부(processed 되감김 방지)
        long sessionMark;                  // 이 세션 복원·새 시작을 마친 시점의 seq — 그 뒤 새 RX 로 생긴 부팅·커서 CSI 만 줄 지우기를 막는다

        // ---- 기록
        readonly List<PromptCandidate> cands = new List<PromptCandidate>();
        readonly List<StateChange> history = new List<StateChange>();
        PromptState state = PromptState.Unknown;
        long stateSince;
        int foreignCount, bootMatches, ignoredBoot, inferred, generation;
        long terminalReplies;

        // ---- 링 위치가 아직 처리 전인 TX/run 통지(링 순서대로 적용)
        sealed class Deferred { public long RxPos; public long Ms; public Action Apply; }
        readonly List<Deferred> deferred = new List<Deferred>();
        readonly List<Action> notices = new List<Action>();

        /// <summary>RX 에서 run 마커를 봤다(B/E). 추적기 잠금 밖에서 호출된다.</summary>
        public event Action<RunMarker> RunMarkerSeen;
        /// <summary>프롬프트 후보가 확정됐다. 추적기 잠금 밖에서 호출된다.</summary>
        public event Action<PromptCandidate> PromptConfirmed;

        /// <summary>구독자 예외를 남길 허브 로그(허브가 설정, 골든·단독 사용은 null). 구독자마다 따로 부르므로 한 구독자의 예외가 다른 구독자를 막지 않는다.</summary>
        public HubLog Log { get; set; }

        public PromptTracker(Config cfg, string board = null, int? tOpenMs = null)
        {
            promptRe = new Regex(cfg.PromptRe, RegexOptions.Compiled);
            ubootRe = new Regex(cfg.UbootPromptRe, RegexOptions.Compiled);
            bootRe = new Regex(cfg.BootRe, RegexOptions.Compiled);
            loaderRe = string.IsNullOrEmpty(cfg.LoaderMarkerRe) ? null : new Regex(cfg.LoaderMarkerRe, RegexOptions.Compiled);   // 없음 = 로더 표시 없는 보드
            rebootDetect = cfg.AutoNudge;
            this.tOpenMs = tOpenMs ?? cfg.EffectiveTOpenMs();
            quietMs = cfg.QuietMs;
            idleMs = (long)(cfg.IdleS * 1000);
            restoreWindowS = cfg.RestoreWindowS;
            this.board = board;
        }

        public int TOpenMs => tOpenMs;

        public static string Name(PromptState s) => s switch
        {
            PromptState.AtPrompt => "AT_PROMPT",
            PromptState.HumanTyping => "HUMAN_TYPING",
            PromptState.Fullscreen => "FULLSCREEN",
            PromptState.AtUboot => "AT_UBOOT",
            PromptState.Booting => "BOOTING",
            PromptState.Unknown => "UNKNOWN",
            _ => "BUSY",
        };

        public static bool TryParseName(string s, out PromptState st)
        {
            foreach (PromptState x in Enum.GetValues(typeof(PromptState)))
                if (Name(x) == s) { st = x; return true; }
            st = PromptState.Busy;
            return false;
        }

        // ================================================================== 공개 입력
        /// <summary>
        /// 새 세션(복원할 것이 없음): UNKNOWN 으로 abs 부터 관측을 시작한다. session(attach 순번, 0 = 검사 안 함)이 이미 받아들인 세션보다
        /// 오래됐으면(멈추라는 요청을 받은 앞 세션 펌프의 늦은 복원) 아무것도 바꾸지 않고 false.
        /// </summary>
        public bool ResetFresh(long abs, long nowMs, long session = 0)
        {
            lock (sync)
            {
                if (!AcceptSession(session)) return false;
                ResetFacts();
                ResetLine(false);
                processed = abs;
                curMs = nowMs;
                lastRxMs = nowMs;                  // probe 무음 조건은 관측을 시작한 때부터 잰다
                deferred.RemoveAll(d => d.RxPos < abs);
                generation++;
                replugBlock = false;
                sessionMark = seq;
                UpdateState(nowMs, "fresh");
            }
            Fire();
            return true;
        }

        bool AcceptSession(long session)
        {
            if (session <= 0) return true;
            if (session < sessionSeq) return false;
            sessionSeq = session;
            return true;
        }

        /// <summary>
        /// 재연결 줄 지우기를 막을지(§3.3): session(attach 순번)의 복원이 이미 적용됐고, 그 뒤 BOOTING·FULLSCREEN 을 봤거나 지금 그렇다 —
        /// 옛 줄 버퍼가 사라졌거나(재부팅) 셸 줄이 아니다. 그 세션 복원 전이면(prepare 중: 새 세션 RX 를 아직 안 봄, 앞 세션 사실만 남음) 막지 않는다.
        /// Cable 송신 잠금 안에서 불린다(Transmitted 와 같은 잠금 순서: 송신 → 추적기).
        /// </summary>
        /// <summary>
        /// 재연결 줄 지우기 예약 조건(§3.3): 지금 셸·U-Boot 줄 편집기가 입력을 받는 중인가 — AT_PROMPT·AT_UBOOT, 또는 프롬프트에서 시작한 HUMAN_TYPING
        /// (반쯤 친 글자·방향키 히스토리 탐색·orphaned 포함, 실보드 11-03 재시험: 히스토리를 위아래로 훑다 빈 줄에서 뽑음 → HUMAN_TYPING).
        /// 실행 중 프로그램에 친 입력(BUSY 에서 시작)·FULLSCREEN 은 아니다.
        /// </summary>
        public bool ShellLineEditing
        {
            get
            {
                lock (sync)
                {
                    var st = Compute();
                    if (st == PromptState.AtPrompt || st == PromptState.AtUboot) return true;
                    // 사람 입력 중: 화면 프로그램(vi 등 — 마지막 줄이 프롬프트처럼 보여도)이 아니고, 프롬프트 줄에서 시작했거나 지금 줄이 프롬프트 줄
                    // (재연결 뒤 첫 키·^U 뒤 다시 그린 줄에서 이어 친 경우, 80열 접힘·printk 로 넘어온 줄 carryOpen 포함)
                    return st == PromptState.HumanTyping && !ScreenProgramActive() && (typingAtPrompt || lineStartsWithPrompt || carryOpen);
                }
            }
        }

        public bool ReplugClearBlockedFor(long session)
        {
            lock (sync) return session > 0 && sessionSeq == session && (replugBlock || NewBlockCond());
        }

        /// <summary>RX 청크. abs = 첫 바이트의 절대 offset, arrivalMs = 도착 시각.</summary>
        public void OnRx(long abs, byte[] data, int off, int len, long arrivalMs)
        {
            lock (sync) RxCore(abs, data, off, len, arrivalMs);
            Fire();
        }

        public void OnRx(long abs, byte[] data, long arrivalMs) => OnRx(abs, data, 0, data.Length, arrivalMs);

        /// <summary>
        /// TX 관측. rxPos = 송신 직전 링 End. 아직 그 위치까지 RX 를 처리하지 못했으면 보류했다가 링 순서대로 적용한다.
        /// TerminalReply(CPR 응답 등)는 판정에서 제외한다. Human 출처 안의 CPR 응답 시퀀스도 사람 입력으로 세지 않는다.
        /// Cable.Write 의 TX 잠금 안에서 불리므로 여기서는 구독자 통지를 하지 않는다(다음 OnRx/Advance 가 그 스레드에서 통지).
        /// </summary>
        public void OnTx(TxSource src, byte[] data, long ms, long rxPos)
        {
            lock (sync)
            {
                if (src == TxSource.TerminalReply) { terminalReplies++; return; }
                var copy = (byte[])data.Clone();
                if (rxPos > processed) deferred.Add(new Deferred { RxPos = rxPos, Ms = ms, Apply = () => ApplyTx(src, copy, ms) });
                else ApplyTx(src, copy, ms);
            }
        }

        /// <summary>시간 진행(새 바이트 없이 now 가 됨). 열린 줄 T_open 확정이 여기서 일어난다.</summary>
        public void Advance(long nowMs)
        {
            lock (sync) AdvanceCore(nowMs);
            Fire();
        }

        /// <summary>
        /// "실행 중 Claude·spool 명령 있음" 플래그(§3.2 AT_PROMPT 조건 2, §3.3 직렬화). 송신 직전에 부른다(rxPos = 그때의 링 End).
        /// sealedRun=true(봉인 run, §3.4): E 마커까지 BOOTING 매치를 무시하고(run 출력 구간 = 이 호출부터 E 까지, 에코된 명령 줄 포함),
        /// E 마커 직후 첫 후보를 열린 줄 조건 없이 확정하며, 그 확정으로 플래그를 푼다.
        /// sealedRun=false(.tx 일반 줄처럼 마커 없는 명령): BOOTING 매치는 그대로 보고, 이 호출 뒤 첫 확정 프롬프트로 플래그를 푼다.
        /// </summary>
        public void BeginRun(string id, long rxPos, long ms, bool sealedRun = true)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("run id 비어 있음");
            lock (sync)
            {
                // 플래그는 즉시(다음 요청이 바로 직렬화를 본다). 구간 시작·해제 기준은 송신 위치에서(그 전에 도착한 바이트와 섞이지 않게).
                runActive = true;
                runId = id;
                runSealed = sealedRun;
                runB = false; runE = false; runWindow = false;
                runBeginSeq = long.MaxValue;
                string ide = Regex.Escape(id);
                runBPartial = new Regex("__UH_B_" + ide + "(?=[^A-Za-z0-9_])");
                runBFinal = new Regex("__UH_B_" + ide + "(?![A-Za-z0-9_])");
                runEPartial = new Regex("__UH_E_" + ide + @"_(\d+)(?=\D)");
                runEFinal = new Regex("__UH_E_" + ide + @"_(\d+)(?!\d)");
                if (rxPos > processed) deferred.Add(new Deferred { RxPos = rxPos, Ms = ms, Apply = () => StartRun(id) });
                else StartRun(id);
                UpdateState(ms, "run begin " + id);
            }
            Fire();
        }

        /// <summary>run 강제 종료(stranded·timeout·interrupted 처리 뒤 Arbiter 가 호출). id 가 null 이면 현재 run.</summary>
        public void EndRun(string id, long ms)
        {
            lock (sync)
            {
                if (id == null || id == runId)
                {
                    ClearRun();
                    UpdateState(ms, "run end");
                }
            }
            Fire();
        }

        /// <summary>추적기가 아직 보드명을 모르면 기록 코어(state.json)가 아는 이름으로 시작한다.</summary>
        public void SetBoardIfUnknown(string name)
        {
            lock (sync) { if (board == null && !string.IsNullOrEmpty(name)) board = name; }
        }

        public long ProcessedPos { get { lock (sync) return processed; } }

        /// <summary>사람(telnet) 클라이언트 수. 0 이 되면 반쯤 친 줄은 partial_line_orphaned 가 된다.</summary>
        public void SetHumanClientCount(int n)
        {
            lock (sync)
            {
                humanClients = n;
                if (n == 0 && humanTyping) orphaned = true;
            }
        }

        /// <summary>케이블 분리: 보드 상태를 알 수 없게 된다.</summary>
        public void MarkDetached(long ms)
        {
            lock (sync)
            {
                if (pending != null) Resolve(pending, CandidateVerdict.Unconfirmed, "detach", ms);
                unknown = true;
                lastConfirmSeq = 0; lastConfirmKind = 0;
                ClearRun();
                UpdateState(ms, "detach");
                replugBlock = false;               // 분리 뒤(확정 지움으로 생긴 FULLSCREEN 조건 포함)는 다음 세션 판단에 쓰지 않는다
            }
            Fire();
        }

        // ================================================================== 조회
        public TrackerSnapshot Snapshot()
        {
            lock (sync)
            {
                var st = Compute();
                return new TrackerSnapshot
                {
                    State = st, BlockedReason = Reason(st), SinceMs = stateSince,
                    HumanTyping = humanTyping, Orphaned = orphaned || (humanTyping && humanClients == 0),
                    RunActive = runActive, RunId = runId, Unknown = unknown, ProbeNeedsUserOk = probeNeedsUserOk,
                    KernelPhase = kernelPhase, ScreenSinceNewline = csiSinceNewline, Board = board, RestoredFrom = restoredFrom,
                    LastRxMs = lastRxMs, LastHumanTxMs = lastHumanTxMs, ProcessedPos = processed, LastKernelMs = lastKernelMs,
                    Candidates = cands.Count, Foreign = foreignCount, BootMatches = bootMatches, IgnoredBootMatches = ignoredBoot,
                    InferredInputs = inferred, LastLines = lastLines.ToArray(), BootSeen = lastBootSeq > sessionMark,
                    HumanClients = humanClients, PartialText = humanTyping ? PartialTextLocked() : null,
                };
            }
        }

        /// <summary>
        /// partial(101 응답·--clear-line 기록): 사람이 친 줄을 터미널이 보이는 대로 재구성한다 = carry(앞 줄에서 넘어온 몫) + 현재 줄.
        /// 현재 줄은 마지막 후보(프롬프트) 뒤부터 줄 중간 printk 타임스탬프 앞까지를 RenderEcho 로 그린다(\b·ESC[J/K·ESC[nC/D 반영).
        /// </summary>
        string PartialTextLocked()
        {
            int from = InputFrom();
            var m = KernelTsAny.Match(line.ToString(), from);
            return carry + RenderEcho(from, m.Success ? m.Index : line.Length);
        }

        int InputFrom() => lastCandEndIdx >= 0 && lastCandEndIdx <= line.Length && !lastCandForeign ? lastCandEndIdx : 0;

        /// <summary>
        /// line[from, to) 를 줄 편집 에코로 해석해 화면에 남는 글자를 돌려준다. 글자는 커서 자리에 덮어쓰고, \b·ESC[nD 는 커서 왼쪽, ESC[nC 는 오른쪽,
        /// ESC[J·ESC[K(정규화에서 지운 자리 eraseAt 포함)는 커서부터 끝까지 지운다. 그 밖의 제어 문자·CSI 는 무시한다.
        /// 커서 뒤에 공백만 남으면(\b 공백 \b 지우기) 잘라 낸다.
        /// </summary>
        string RenderEcho(int from, int to)
        {
            var buf = new List<char>();
            int cur = 0, e = 0;
            while (e < eraseAt.Count && eraseAt[e] < from) e++;
            for (int i = from; i < to; i++)
            {
                while (e < eraseAt.Count && eraseAt[e] <= i) { if (cur < buf.Count) buf.RemoveRange(cur, buf.Count - cur); e++; }
                char ch = line[i];
                if (ch == '\b') { if (cur > 0) cur--; continue; }
                if (ch == '\x1b')
                {
                    if (i + 1 >= to || line[i + 1] != '[') continue;
                    int j = i + 2;
                    while (j < to && line[j] >= 0x20 && line[j] <= 0x3f) j++;
                    if (j >= to) { i = to; break; }
                    string prm = line.ToString(i + 2, j - i - 2);
                    int n = int.TryParse(prm, NumberStyles.None, CultureInfo.InvariantCulture, out int v) && v > 0 ? v : 1;
                    switch (line[j])
                    {
                        case 'J': case 'K': if ((prm.Length == 0 || prm == "0") && cur < buf.Count) buf.RemoveRange(cur, buf.Count - cur); break;
                        case 'D': cur = Math.Max(0, cur - n); break;
                        case 'C': cur = Math.Min(buf.Count, cur + n); break;
                    }
                    i = j;
                    continue;
                }
                if (ch < 0x20 || ch == 0x7f) continue;
                if (cur < buf.Count) buf[cur] = ch; else buf.Add(ch);
                cur++;
            }
            while (e < eraseAt.Count && eraseAt[e] <= to) { if (eraseAt[e] >= from && cur < buf.Count) buf.RemoveRange(cur, buf.Count - cur); e++; }
            int k = cur;
            while (k < buf.Count && buf[k] == ' ') k++;
            if (k == buf.Count && cur < buf.Count) buf.RemoveRange(cur, buf.Count - cur);
            return new string(buf.ToArray());
        }

        public PromptState State { get { lock (sync) return Compute(); } }

        public List<PromptCandidate> Candidates() { lock (sync) return new List<PromptCandidate>(cands); }

        public List<StateChange> History() { lock (sync) return new List<StateChange>(history); }

        public long TerminalReplies { get { lock (sync) return terminalReplies; } }

        /// <summary>ResetFresh·Restore 횟수(attach 마다 1 증가). 시험·진단용.</summary>
        public int Generation { get { lock (sync) return generation; } }

        /// <summary>
        /// probe(CR 1회) 허용 판정(§3.3 표, 부록 A S0-9). 조건: 사람 유휴 ≥ idle_s, RX 무음 ≥ quiet_ms, 마지막 개행 이후 커서 CSI 없음,
        /// 그리고 상태가 UNKNOWN(attach·grab 출처면 userOk 필요) 이거나 BOOTING 이면서 커널 타임스탬프 줄 이후.
        /// </summary>
        /// <summary>
        /// 부록 C.13 ②③ 자동 깨우기·wait --nudge(auto_nudge): probe 와 같되 RX 전체 무음 대신 커널 줄(printk 타임스탬프·부팅 매치) 무음 quiet_ms —
        /// 앱 상태 줄이 계속 흘러도 깨운다. 사람 유휴(humanIdleMs 와 idle_s 중 긴 것)·사람 줄 없음·화면 모드 아님. UNKNOWN(사용자 확인이 필요 없을 때)
        /// 또는 커널 단계 이후 BOOTING 에서만(커널 전·U-Boot autoboot 에선 절대 안 됨).
        /// </summary>
        public bool CanNudge(long nowMs, long humanIdleMs, out string why)
        {
            lock (sync)
            {
                var st = Compute();
                if (lastHumanTxMs != long.MinValue && nowMs - lastHumanTxMs < Math.Max(idleMs, humanIdleMs)) { why = "human_active"; return false; }
                if (humanTyping) { why = "partial_line"; return false; }
                if (csiSinceNewline || FullscreenCond()) { why = "screen_mode"; return false; }
                if (lastKernelMs != long.MinValue && nowMs - lastKernelMs < quietMs) { why = "kernel_not_quiet"; return false; }
                if (st == PromptState.Unknown)
                {
                    if (probeNeedsUserOk) { why = "user_ok_required"; return false; }
                    why = null;
                    return true;
                }
                if (st == PromptState.Booting)
                {
                    if (!kernelPhase) { why = "boot_before_kernel"; return false; }
                    why = null;
                    return true;
                }
                why = "state_" + Name(st).ToLowerInvariant();
                return false;
            }
        }

        public bool CanProbe(long nowMs, bool userOk, out string why)
        {
            lock (sync)
            {
                var st = Compute();
                if (lastHumanTxMs != long.MinValue && nowMs - lastHumanTxMs < idleMs) { why = "human_active"; return false; }
                if (lastRxMs != long.MinValue && nowMs - lastRxMs < quietMs) { why = "rx_not_quiet"; return false; }
                if (csiSinceNewline) { why = "screen_mode"; return false; }
                if (st == PromptState.Unknown)
                {
                    if (probeNeedsUserOk && !userOk) { why = "user_ok_required"; return false; }
                    why = null;
                    return true;
                }
                if (st == PromptState.Booting)
                {
                    if (!kernelPhase) { why = "boot_before_kernel"; return false; }
                    why = null;
                    return true;
                }
                why = "state_" + Name(st).ToLowerInvariant();
                return false;
            }
        }

        // ================================================================== 상태 복원
        /// <summary>
        /// 상태 복원(§3.2): .raw 끝을 도착 시각과 함께 재생하고 출처별 규칙을 적용한 뒤 resumeAbs(링 위치)·nowMs 로 이어 간다.
        /// - 코어 재시작이고 공백 ≤ restore_window_s: 전체 복원.
        /// - 코어 재시작이고 공백 초과: 부정 상태(partial_line, FULLSCREEN)만, 나머지는 UNKNOWN(probe 허용).
        /// - attach·grab: 부정 상태만, 나머지는 UNKNOWN(probe 는 user-ok 필요).
        /// 재생 중에는 사람 입력이 기록돼 있지 않으므로, 열린 줄로 확정된 프롬프트 뒤에 같은 줄로 커널 로그가 아닌 글자가 붙고
        /// 줄이 끝나면 사람이 명령을 친 것으로 추론한다(BUSY 쪽으로만 기운다). 끝에 개행 없는 에코가 남으면 partial_line_orphaned.
        /// </summary>
        public bool Restore(IEnumerable<ReplayItem> items, RestoreSource src, double gapS, long resumeAbs, long nowMs, long session = 0)
        {
            lock (sync)
            {
                if (!AcceptSession(session)) return false;
                var keep = deferred.FindAll(d => d.RxPos >= resumeAbs);
                deferred.Clear();
                ResetFacts();
                ResetLine(false);
                curMs = long.MinValue;             // 재생 시각(파일 기준)은 실시간 시계와 기준이 다르다: 시간 하한을 비운다
                restoreMode = true;
                bool first = true;
                long last = long.MinValue;
                foreach (var it in items)
                {
                    if (first)
                    {
                        first = false;
                        processed = it.Abs;
                        if (it.Abs > 0) line.Append('\u0001');      // 잘린 줄 앞: ^ 앵커가 잘린 지점에서 맞지 않게
                        if (it.Abs > 0) lineAbs.Add(it.Abs);
                    }
                    if (it.IsTx)
                    {
                        AdvanceCore(it.Ms);
                        ApplyTx(it.Source, it.Data ?? new byte[0], it.Ms);
                    }
                    else RxCore(it.Abs, it.Data, 0, it.Data.Length, it.Ms);
                    if (it.Ms > last) last = it.Ms;
                }
                if (last != long.MinValue) AdvanceCore(last + Math.Max(tOpenMs, (long)(Math.Max(0, gapS) * 1000)));   // 끝 = 열린 줄
                bool partial = PartialLineAtEnd();
                bool full = src == RestoreSource.CoreRestart && gapS <= restoreWindowS;
                if (!full)
                {
                    bool fs = FullscreenCond();
                    if (!fs) lastCsiSeq = 0;
                    if (!ScreenProgramActive()) lastScreenSeq = 0;             // 재생 구간에서 이미 끝난 화면 프로그램(vi 종료 뒤 프롬프트 확정)은 남기지 않는다
                    lastConfirmSeq = 0; lastConfirmKind = 0;
                    bootActive = false; kernelPhase = false;
                    unknown = true;
                    probeNeedsUserOk = src != RestoreSource.CoreRestart;
                }
                if (partial) { humanTyping = true; orphaned = true; typingFs = false; typingAtPrompt = true; }   // 끝 줄이 프롬프트 후보 뒤 에코: 프롬프트 줄 편집
                ClearRun();
                restoreMode = false;
                restoredFrom = src;
                pending = null;
                processed = resumeAbs;
                curMs = nowMs;
                lastRxMs = nowMs;                  // 끊긴 동안의 RX 는 알 수 없다: 무음은 지금부터 잰다
                lastHumanTxMs = long.MinValue;
                deferred.AddRange(keep);
                generation++;
                replugBlock = false;
                sessionMark = seq;                 // 복원된 부정 상태(예: 셸 줄 다시 그리기의 ESC[J 로 생긴 FULLSCREEN 조건)는 줄 지우기를 막지 않는다
                UpdateState(nowMs, "restore:" + src.ToString().ToLowerInvariant() + (full ? ":full" : ":negative"));
            }
            Fire();
            return true;
        }

        // ================================================================== 내부: 사실 초기화
        void ResetFacts()
        {
            pending = null;
            lastTxSeq = 0; lastHumanTxSeq = 0; lastHumanTxMs = long.MinValue;
            humanTyping = false; typingFs = false; orphaned = false; typingAtPrompt = false;
            lastCsiSeq = 0; csiSinceNewline = false; lastScreenSeq = 0;
            lastConfirmSeq = 0; lastConfirmKind = 0;
            bootActive = false; kernelPhase = false; bootSinceBoard = false; lastBootSeq = 0;
            unknown = true; probeNeedsUserOk = false; restoredFrom = null;
            ClearRun();
            lastRxMs = long.MinValue;
            carry = ""; carryOpen = false;
        }

        void ResetLine(bool keepLast)
        {
            escState = 0; csi.Clear();
            line.Clear(); lineAbs.Clear();
            candFrom = bootFrom = markFrom = scanBase = 0;
            kernelChecked = false;
            lastCandEndIdx = -1; lastCandForeign = false; openConfirmedEndIdx = -1; eEndIdx = -1;
            eraseAt.Clear();
            lineStartsWithPrompt = false;
            if (!keepLast) lastLines.Clear();
        }

        // ================================================================== 내부: RX
        void RxCore(long abs, byte[] data, int off, int len, long arrivalMs)
        {
            if (len <= 0) return;
            if (abs + len <= processed) return;                     // 이미 처리
            if (abs < processed) { int skip = (int)(processed - abs); off += skip; len -= skip; abs = processed; }
            if (abs > processed)                                    // 링에서 잃은 구간: 줄 상태를 버리고 모름으로
            {
                if (pending != null) Resolve(pending, CandidateVerdict.Unconfirmed, "lost", arrivalMs);
                ResetLine(true);
                carry = ""; carryOpen = false;
                unknown = true;
                processed = abs;
            }
            ApplyDue();
            AdvanceCore(arrivalMs);
            long t = Math.Max(arrivalMs, curMs);
            chunkMs = t;
            rxGapMs = lastRxMs == long.MinValue ? long.MaxValue : t - lastRxMs;
            lastRxMs = t;
            int i = 0;
            while (i < len)
            {
                long due = long.MaxValue;
                foreach (var d in deferred) if (d.RxPos < due) due = d.RxPos;
                int n = due > processed ? (int)Math.Min(len - i, due - processed) : 0;
                if (n > 0)
                {
                    ProcessBytes(data, off + i, n, abs + i);
                    processed += n;
                    i += n;
                }
                if (i < len || due <= processed)
                {
                    ScanLine(false);
                    ApplyDue();
                    chunkMs = t;
                }
            }
            ScanLine(false);
            UpdateState(t, null);
        }

        void ApplyDue()
        {
            while (true)
            {
                Deferred first = null;
                foreach (var d in deferred) if (d.RxPos <= processed && (first == null || d.RxPos < first.RxPos)) first = d;
                if (first == null) return;
                deferred.Remove(first);
                AdvanceCore(first.Ms);
                first.Apply();
            }
        }

        void ProcessBytes(byte[] d, int off, int n, long abs0)
        {
            for (int k = 0; k < n; k++)
            {
                byte b = d[off + k];
                long abs = abs0 + k;
                bool again = true;
                while (again)
                {
                    again = false;
                    switch (escState)
                    {
                        case 0:
                            if (b == 0x00) break;                                   // NUL 제거
                            if (b == 0x1b) { escState = 1; escAbs = abs; break; }
                            if (b == (byte)'\r' || b == (byte)'\n') { LineBreak(); break; }
                            AppendChar((char)b, abs);
                            break;
                        case 1:
                            if (b == (byte)'[') { escState = 2; csi.Clear(); break; }
                            escState = 0;
                            AppendChar('\x1b', escAbs);                             // CSI 아닌 ESC: 내용으로 남긴다
                            again = true;
                            break;
                        default:
                            if (b >= 0x20 && b <= 0x3f && csi.Length < 32) { csi.Append((char)b); break; }
                            escState = 0;
                            if (b >= 0x40 && b <= 0x7e)
                            {
                                char f = (char)b;
                                string prm = csi.ToString();
                                if (f == 'K' && prm.Length == 0) { eraseAt.Add(line.Length); break; }   // ESC[K 제거(자리만 기억: partial 재구성)
                                if (f == 'm' && IsSgr(prm)) break;                  // SGR 제거
                                if (f == 'H' || f == 'J' || f == 'r')               // 커서 주소 지정 CSI(§3.2 FULLSCREEN)
                                {
                                    lastCsiSeq = ++seq;
                                    csiSinceNewline = true;
                                }
                                if (f == 'H' || f == 'f' || f == 'r' || (f == 'h' && prm.Length > 0 && prm[0] == '?'))
                                    lastScreenSeq = ++seq;                          // 진짜 화면 프로그램(vi·top·less) 표시 — 재연결 줄 지우기 판단(§3.3)
                                AppendChar('\x1b', escAbs);
                                AppendChar('[', escAbs);
                                foreach (char c in prm) AppendChar(c, escAbs);
                                AppendChar(f, abs);
                                break;
                            }
                            AppendChar('\x1b', escAbs);                             // 깨진 CSI: 글자로 남기고 이 바이트는 다시 본다
                            AppendChar('[', escAbs);
                            foreach (char c in csi.ToString()) AppendChar(c, escAbs);
                            again = true;
                            break;
                    }
                }
            }
        }

        static bool IsSgr(string prm)
        {
            foreach (char c in prm) if (!(c >= '0' && c <= '9') && c != ';') return false;
            return true;
        }

        void AppendChar(char c, long abs)
        {
            if (pending != null) Resolve(pending, CandidateVerdict.Unconfirmed, "followed", chunkMs);
            line.Append(c);
            lineAbs.Add(abs);
            if (line.Length > MaxLine) TrimLine();
        }

        /// <summary>너무 긴 줄: 앞을 버리고 맨 앞에 \u0001 을 둬 줄 시작 앵커가 잘린 자리에서 맞지 않게 한다.</summary>
        void TrimLine()
        {
            ScanLine(false);
            int keepN = 512;
            int cut = line.Length - keepN;
            line.Remove(0, cut);
            lineAbs.RemoveRange(0, cut);
            line.Insert(0, '\u0001');
            lineAbs.Insert(0, lineAbs.Count > 0 ? lineAbs[0] : processed);
            candFrom = Math.Max(0, candFrom - cut + 1);
            bootFrom = Math.Max(0, bootFrom - cut + 1);
            markFrom = Math.Max(0, markFrom - cut + 1);
            scanBase = line.Length;
            kernelChecked = true;
            lastCandEndIdx = lastCandEndIdx - cut + 1;
            if (lastCandEndIdx < 1) lastCandEndIdx = -1;
            openConfirmedEndIdx = -1;
            for (int i = 0; i < eraseAt.Count; i++) eraseAt[i] = eraseAt[i] - cut + 1;
            eraseAt.RemoveAll(x => x < 1);
        }

        void LineBreak()
        {
            if (pending != null) Resolve(pending, CandidateVerdict.Unconfirmed, "followed", chunkMs);
            ScanLine(true);
            if (humanTyping) CarryInput();
            if (restoreMode && openConfirmedEndIdx >= 0 && openConfirmedEndIdx < line.Length)
            {
                // 복원 재생: 열린 줄로 확정된 프롬프트 뒤에 같은 줄로 글자가 붙고 줄이 끝났다 → 사람 명령(에코)로 추론
                string rest = line.ToString(openConfirmedEndIdx, line.Length - openConfirmedEndIdx);
                if (!KernelTs.IsMatch(rest))
                {
                    inferred++;
                    ApplyTx(TxSource.Human, new[] { (byte)'\r' }, chunkMs);
                }
            }
            if (line.Length > 0)
            {
                lastLines.Enqueue(line.ToString());
                while (lastLines.Count > 3) lastLines.Dequeue();
            }
            ResetLine(true);
            csiSinceNewline = false;
        }

        /// <summary>
        /// 사람이 입력 중(Enter 전)에 줄이 끝남: 다음 줄로 넘어가도 partial 이 사람이 친 글자를 잃지 않게 carry 에 옮긴다(실보드 11-03:
        /// 'cat /pro' 에코 뒤에 printk 가 붙고 개행 → 현재 줄이 비어 partial 이 "" 로 나감).
        ///  - 줄 중간에 printk 타임스탬프가 있으면 그 앞까지가 사람 입력 에코다: 프롬프트 줄이면 carry = 그것, 아니면(앞서 printk 로 넘어온 뒤 친 글자) carry 에 덧붙인다.
        ///  - 타임스탬프가 없는 프롬프트 줄(80열 접힘·Tab 다시 그리기의 \r)은 carry = 그 줄 입력. 다시 그리면 새 후보가 carry 를 비운다.
        ///  - 타임스탬프 없는 비프롬프트 줄(명령 출력·Tab 후보 목록·앱 로그)은 덧붙이지 않고, 비어 있지 않으면 carryOpen 을 끈다 —
        ///    그 뒤 printk 가 줄 중간에 든 출력 줄(logread 의 "… kernel: [ 350.421137] …" 등)의 앞부분이 입력으로 붙지 않게(type-ahead 중 포함).
        ///    덧붙이기는 carryOpen(carry 가 프롬프트 줄에서 시작)일 때만 한다. 빈 줄(printk 뒤 \r\n)은 끄지 않는다.
        /// 한계: 타임스탬프 없는 앱 로그가 입력 에코 뒤에 붙으면 가를 수 없다(그 줄은 통째로 입력으로 남는다).
        /// </summary>
        void CarryInput()
        {
            bool candLine = lastCandEndIdx >= 0 && lastCandEndIdx <= line.Length && !lastCandForeign;
            int from = InputFrom();
            var m = KernelTsAny.Match(line.ToString(), from);
            if (candLine)
            {
                carry = RenderEcho(from, m.Success ? m.Index : line.Length);
                carryOpen = true;
            }
            else if (m.Success)
            {
                if (carryOpen) carry += RenderEcho(from, m.Index);      // printk 로 넘어온 뒤 이어 친 글자가 또 printk 로 끊김
            }
            else if (RenderEcho(from, line.Length).Length > 0) carryOpen = false;   // 타임스탬프 없는 출력 줄: 이 뒤 printk 줄 앞부분은 입력이 아니다
            if (carry.Length > MaxLine) carry = carry.Substring(carry.Length - MaxLine);
        }

        // ================================================================== 내부: 줄 스캔(마커 → 커널 타임스탬프 → 부팅 → 후보)
        void ScanLine(bool final)
        {
            if (line.Length == 0) return;
            string s = line.ToString();
            int lb = Math.Max(0, scanBase - Lookback);
            if (runId != null && runSealed && !runE) ScanMarkers(s, final, lb);
            if (!kernelChecked)
            {
                if (KernelTs.IsMatch(s)) { kernelChecked = true; if (bootActive) kernelPhase = true; }
                else if (final || s.Length > 40) kernelChecked = true;
            }
            if (rebootDetect) RebootCheck(s);
            for (var m = bootRe.Match(s, Math.Min(Math.Max(bootFrom, lb), s.Length)); m.Success; m = m.NextMatch())
            {
                bootFrom = m.Index + Math.Max(1, m.Length);
                OnBootMatch(m.Value);
            }
            int from = Math.Min(Math.Max(candFrom, lb), s.Length);
            var pm = promptRe.Match(s, from);
            var um = ubootRe.Match(s, from);
            while (pm.Success || um.Success)
            {
                bool useU = um.Success && (!pm.Success || um.Index < pm.Index);
                var m = useU ? um : pm;
                if (m.Length > 0 && m.Index >= candFrom) OnCandidate(m, useU, s, final);
                candFrom = Math.Max(candFrom, m.Index + Math.Max(1, m.Length));
                if (useU) um = um.NextMatch(); else pm = pm.NextMatch();
                while (pm.Success && pm.Index < candFrom) pm = pm.NextMatch();
                while (um.Success && um.Index < candFrom) um = um.NextMatch();
            }
            scanBase = s.Length;
        }

        void ScanMarkers(string s, bool final, int lb)
        {
            var bre = final ? runBFinal : runBPartial;
            var ere = final ? runEFinal : runEPartial;
            int from = Math.Min(Math.Max(markFrom, lb), s.Length);
            if (!runB)
            {
                var b = bre.Match(s, from);
                if (b.Success)
                {
                    runB = true;
                    markFrom = b.Index + b.Length;
                    var mk = new RunMarker { RunId = runId, Kind = 'B', Start = lineAbs[b.Index], End = lineAbs[b.Index + b.Length - 1] + 1, Ms = chunkMs };
                    notices.Add(() => Each(RunMarkerSeen, mk));
                    from = markFrom;
                }
            }
            var e = ere.Match(s, from);
            if (e.Success)
            {
                runE = true;
                runWindow = false;                                                   // run 출력 구간 끝
                markFrom = e.Index + e.Length;
                eEndIdx = markFrom;
                int.TryParse(e.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rc);
                var mk = new RunMarker { RunId = runId, Kind = 'E', Rc = rc, Start = lineAbs[e.Index], End = lineAbs[e.Index + e.Length - 1] + 1, Ms = chunkMs };
                notices.Add(() => Each(RunMarkerSeen, mk));
            }
        }

        /// <summary>
        /// 부록 C.13 ①: run 구간(송신 위치 ~ E) 안에서 실제 재부팅을 알아본다(그 밖의 줄은 커널 시각만 기억). 명령 에코 줄(__UH_ 마커가 든 줄)은 보지 않는다.
        ///   B 전(명령이 아직 시작 안 함 — 출력이 있을 수 없음): 로더 표시, 또는 커널 시각이 run 밖에서 본 마지막 값보다 2초 넘게 작음(모르면 5초 미만 — 새 커널).
        ///   B 뒤(명령 출력 중 — dmesg·cat 일 수 있음): 앞 RX 무음 0.5초 이상 뒤의 로더 표시만.
        /// 재부팅이면 run 구간을 닫고(그 뒤 부팅 매치를 그대로 본다) 'R' 마커를 알리고 부팅으로 넘어간다. 잠금(runActive)은 Arbiter 가 다음 프롬프트 확정 때 푼다.
        /// </summary>
        void RebootCheck(string s)
        {
            var km = KernelTsValue.Match(s);
            double kv = -1;
            bool hasKts = km.Success && double.TryParse(km.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out kv);
            if (hasKts) lastKernelMs = chunkMs;
            if (!(runWindow && runSealed && !runE && runId != null))
            {
                if (hasKts) lastKts = kv;
                return;
            }
            if (s.IndexOf("__UH_", StringComparison.Ordinal) >= 0) return;
            bool loader = loaderRe != null && loaderRe.IsMatch(s) && (!runB || rxGapMs >= LoaderQuietMs);
            bool drop = !runB && hasKts && (lastKts >= 0 ? kv + 2 < lastKts : kv < 5);
            if (!loader && !drop) return;
            runWindow = false;
            var mk = new RunMarker { RunId = runId, Kind = 'R', Start = lineAbs.Count > 0 ? lineAbs[0] : processed, End = lineAbs.Count > 0 ? lineAbs[lineAbs.Count - 1] + 1 : processed, Ms = chunkMs };
            notices.Add(() => Each(RunMarkerSeen, mk));
            bootMatches++;
            lastBootSeq = ++seq;
            bootActive = true;
            unknown = false;
            probeNeedsUserOk = false;
            bootSinceBoard = true;
            kernelPhase = drop;
            lastKts = -1;
            lastKernelMs = chunkMs;
            UpdateState(chunkMs, "boot(run 중 재부팅): " + (drop ? "커널 시각 " + kv.ToString("0.000000", CultureInfo.InvariantCulture) : "로더 표시"));
        }

        void OnBootMatch(string m)
        {
            if (runWindow) { ignoredBoot++; return; }                                // Claude run 출력 안의 매치(dmesg 등)는 무시
            lastKts = -1;
            lastKernelMs = chunkMs;
            bootMatches++;
            lastBootSeq = ++seq;
            bootActive = true;
            unknown = false;
            probeNeedsUserOk = false;
            bootSinceBoard = true;
            kernelPhase = KernelTs.IsMatch(m);
            UpdateState(chunkMs, "boot:" + m);
        }

        void OnCandidate(Match m, bool uboot, string s, bool final)
        {
            var c = new PromptCandidate
            {
                Start = lineAbs[m.Index],
                End = lineAbs[m.Index + m.Length - 1] + 1,
                UBoot = uboot,
                Host = uboot ? null : (m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value : m.Value),
                ArrivalMs = chunkMs,
                Verdict = CandidateVerdict.Pending,
                Seq = ++seq,
            };
            lastCandEndIdx = m.Index + m.Length;
            lastCandForeign = false;
            AddCand(c);
            bool atStart = m.Index == 0 || (m.Index == 1 && s[0] == '\u0001');
            if (uboot && atStart) lineStartsWithPrompt = true;
            if (!uboot)
            {
                // 현재 보드명과 다른 호스트명 + 그 사이 부팅 경계 없음 → foreign(중첩 셸·로그 데이터): 확정하지 않고 보드명도 바꾸지 않는다
                if (board != null && c.Host != board && !bootSinceBoard)
                {
                    foreignCount++;
                    lastCandForeign = true;
                    Resolve(c, CandidateVerdict.Foreign, "foreign", chunkMs);
                    return;
                }
                carry = ""; carryOpen = false;                                                          // 새 프롬프트(다시 그리기 포함): 입력은 이 줄에서 다시 시작
                if (atStart) lineStartsWithPrompt = true;
                if (runE && m.Index >= eEndIdx) { Confirm(c, "e-marker", chunkMs); return; }   // 자기 E 마커 직후 첫 후보
            }
            bool followed = final || m.Index + m.Length < s.Length;
            if (followed) Resolve(c, CandidateVerdict.Unconfirmed, "followed", chunkMs);
            else pending = c;
        }

        void AddCand(PromptCandidate c)
        {
            cands.Add(c);
            if (cands.Count > MaxCandidates) cands.RemoveRange(0, cands.Count - MaxCandidates);
        }

        void Resolve(PromptCandidate c, CandidateVerdict v, string by, long ms)
        {
            c.Verdict = v;
            c.By = by;
            c.DecidedMs = ms;
            if (ReferenceEquals(pending, c)) pending = null;
        }

        void Confirm(PromptCandidate c, string by, long ms)
        {
            Resolve(c, CandidateVerdict.Confirmed, by, ms);
            lastConfirmSeq = ++seq;
            lastConfirmKind = c.UBoot ? 2 : 1;
            if (!c.UBoot) board = c.Host;                                            // 보드명은 확정된 프롬프트로만 정한다(부팅 경계 뒤 교체 포함)
            unknown = false;
            probeNeedsUserOk = false;
            bootActive = false;                                                      // BOOTING 은 프롬프트 확정으로 끝난다
            kernelPhase = false;
            bootSinceBoard = false;
            csiSinceNewline = false;                                                 // 커서 CSI 뒤 프롬프트가 확정됐으면 화면 모드는 끝났다
            if (by == "open")
            {
                openConfirmedEndIdx = line.Length;                                   // 열린 줄 확정 후보는 항상 현재 줄 끝에 있다
                // HUMAN_TYPING 해제(RX): FULLSCREEN 에서 보낸 사람 바이트 뒤, 빈 열린 줄 프롬프트 확정
                if (humanTyping && typingFs && c.Seq > lastHumanTxSeq) { humanTyping = false; typingFs = false; orphaned = false; }
            }
            if (!c.UBoot && runActive && (runSealed ? runE : c.Seq > runBeginSeq))                   // 직렬화 해제
                ClearRun();
            notices.Add(() => Each(PromptConfirmed, c));
            UpdateState(ms, "confirm:" + by);
        }

        // ================================================================== 내부: TX·시간·run
        void ApplyTx(TxSource src, byte[] data, long ms)
        {
            if (src == TxSource.TerminalReply) { terminalReplies++; return; }
            AdvanceCore(ms);
            byte[] d = data;
            if (src == TxSource.Human && Array.IndexOf(data, (byte)0x1b) >= 0)
            {
                string s = Encoding.Latin1.GetString(data);
                string stripped = CprReply.Replace(s, "");
                if (stripped.Length != s.Length) { terminalReplies++; d = Encoding.Latin1.GetBytes(stripped); }
                if (d.Length == 0) return;                                            // CPR 응답뿐: 사람 입력 아님
            }
            if (pending != null) Resolve(pending, CandidateVerdict.Unconfirmed, "tx", ms);   // 열린 줄이 되기 전에 입력이 왔다
            bool fsAtSend = FullscreenCond();
            var stAtSend = Compute();                                                 // 이 TX 직전 상태(사람 입력이 프롬프트 줄 편집인지 가린다)
            // 프롬프트 줄 맥락: 프롬프트 상태였거나, 이미 프롬프트 줄 편집 중이었거나(같은 쓰기의 ^U·재연결 줄 지우기 뒤 이어 친 키 포함),
            // 지금 줄이 프롬프트로 시작한다(줄 편집기 다시 그리기). 실행 중 프로그램 출력 줄·화면 프로그램은 아님.
            bool promptCtx = !ScreenProgramActive() && (stAtSend == PromptState.AtPrompt || stAtSend == PromptState.AtUboot
                             || (humanTyping && typingAtPrompt) || lineStartsWithPrompt);
            lastTxSeq = ++seq;
            bool enter = false, cancel = false;
            foreach (byte b in d)
            {
                if (b == (byte)'\r' || b == (byte)'\n') enter = true;
                else if (b == 0x03 || b == 0x15) cancel = true;
                if (src != TxSource.Human)
                {
                    // --clear-line(§3.3, E5): Claude 의 ^U 는 사람이 반쯤 친 줄을 지운다 → HUMAN_TYPING 해제(새 프롬프트 확정 전까지 BUSY)
                    if (b == 0x15 && humanTyping) { humanTyping = false; typingFs = false; orphaned = false; carry = ""; carryOpen = false; }
                    continue;
                }
                if (b == (byte)'\r' || b == (byte)'\n' || b == 0x03 || b == 0x15)
                {
                    humanTyping = false; typingFs = false; carry = ""; carryOpen = false;
                    if (b == (byte)'\r' || b == (byte)'\n') bootActive = false;          // BOOTING 은 사람 CR 로도 끝난다(→BUSY)
                }
                else
                {
                    if (!humanTyping)
                    {
                        humanTyping = true; typingFs = fsAtSend; carry = ""; carryOpen = false;
                        typingAtPrompt = promptCtx;
                    }
                    else if (fsAtSend) typingFs = true;
                }
            }
            if (src == TxSource.Human)
            {
                lastHumanTxSeq = lastTxSeq;
                lastHumanTxMs = ms;
                orphaned = false;
            }
            // probe 의 ^U(줄 지우기)만으로는 상태를 안다고 볼 수 없다: UNKNOWN 은 CR(probe 완료 쪽)·사람 ^C/^U·프롬프트 확정으로만 푼다
            if (enter || (cancel && src != TxSource.Probe)) { unknown = false; probeNeedsUserOk = false; }
            UpdateState(ms, "tx:" + src.ToString().ToLowerInvariant());
        }

        void ClearRun() { runActive = false; runWindow = false; runB = false; runE = false; runId = null; }

        void StartRun(string id)
        {
            if (!runActive || runId != id) return;          // 송신 위치에 닿기 전에 EndRun 됐다
            runBeginSeq = ++seq;
            runWindow = runSealed;                          // 봉인 run 출력 구간(BOOTING 매치 무시) 시작
            markFrom = line.Length;
        }

        void AdvanceCore(long ms)
        {
            if (ms > curMs) curMs = ms;
            if (pending != null && ms - pending.ArrivalMs >= tOpenMs)
                Confirm(pending, "open", pending.ArrivalMs + tOpenMs);
        }

        bool PartialLineAtEnd()
        {
            if (lastCandEndIdx < 0 || lastCandForeign || lastCandEndIdx > line.Length) return false;
            string rest = line.ToString(lastCandEndIdx, line.Length - lastCandEndIdx);
            return rest.Length > 0 && !KernelTs.IsMatch(rest);
        }

        // ================================================================== 내부: 상태
        /// <summary>FULLSCREEN 조건(§3.2): 마지막 TX 이후 커서 CSI(H/J/r)를 받았고 그 뒤 확정 프롬프트가 없다.</summary>
        bool FullscreenCond() => lastCsiSeq > lastTxSeq && lastCsiSeq > lastConfirmSeq;

        /// <summary>우선순위: HUMAN_TYPING > FULLSCREEN > AT_UBOOT > BOOTING > AT_PROMPT > UNKNOWN > BUSY.</summary>
        PromptState Compute()
        {
            if (humanTyping) return PromptState.HumanTyping;
            if (FullscreenCond()) return PromptState.Fullscreen;
            if (lastConfirmKind == 2 && lastConfirmSeq > lastTxSeq && lastConfirmSeq > lastBootSeq && !bootActive) return PromptState.AtUboot;
            if (bootActive) return PromptState.Booting;
            // AT_PROMPT 4조건 AND: ①사람 입력 중 아님(위) ②실행 중 run 없음 ③마지막 TX 이후 확정, 그 뒤 TX 0 ④그 뒤 커서 CSI 없음
            if (!runActive && lastConfirmKind == 1 && lastConfirmSeq > lastTxSeq && lastConfirmSeq > lastBootSeq && !(lastCsiSeq > lastConfirmSeq))
                return PromptState.AtPrompt;
            if (unknown) return PromptState.Unknown;
            return PromptState.Busy;
        }

        string Reason(PromptState st) => st switch
        {
            PromptState.HumanTyping => (orphaned || humanClients == 0) ? "partial_line_orphaned" : "partial_line",
            PromptState.Fullscreen => "fullscreen",
            PromptState.Booting => "booting",
            PromptState.Unknown => "unknown",
            PromptState.Busy => runActive ? "serialized" : "busy_since",
            _ => null,
        };

        /// <summary>마지막 확정 프롬프트 뒤 화면 주소 지정 CSI(ESC[H·ESC[r·대체 화면)가 있었다 — 화면 프로그램이 터미널을 잡고 있다.</summary>
        bool ScreenProgramActive() => lastScreenSeq > lastConfirmSeq;

        /// <summary>이 세션 복원 뒤 새 RX 로 BOOTING(부팅 매치) 또는 FULLSCREEN(커서 CSI)이 됐다 — 재연결 줄 지우기 해제 조건.</summary>
        bool NewBlockCond() => (bootActive && lastBootSeq > sessionMark) || (FullscreenCond() && lastCsiSeq > sessionMark);

        void UpdateState(long ms, string why)
        {
            var st = Compute();
            if (!restoreMode && NewBlockCond()) replugBlock = true;
            if (st == state && history.Count > 0) return;
            state = st;
            stateSince = ms;
            history.Add(new StateChange { Ms = ms, State = st, Why = why });
            if (history.Count > MaxHistory) history.RemoveRange(0, history.Count - MaxHistory);
        }

        void Fire()
        {
            List<Action> todo;
            lock (sync)
            {
                if (notices.Count == 0) return;
                todo = new List<Action>(notices);
                notices.Clear();
            }
            foreach (var a in todo)
            {
                try { a(); } catch (Exception e) { Fault("notice", e); }          // 구독자 예외는 추적을 멈추지 않는다(Each 가 먼저 잡는다)
            }
        }

        void Each<T>(Action<T> ev, T arg)
        {
            if (ev == null) return;
            foreach (Action<T> one in ev.GetInvocationList())
            {
                try { one(arg); }
                catch (Exception e) { Fault((one.Method.DeclaringType?.Name ?? "?") + "." + one.Method.Name, e); }
            }
        }

        /// <summary>구독자 예외 기록(P1 침묵 실패 금지). 같은 구독자·예외는 10초에 한 줄.</summary>
        void Fault(string who, Exception e)
            => Log?.WriteThrottled("tracker-sub|" + board + "|" + who + "|" + e.GetType().Name,
                                   "PromptTracker 구독자 예외 " + (board ?? "") + " (" + who + "): " + e.GetType().Name + ": " + e.Message);
    }
}
