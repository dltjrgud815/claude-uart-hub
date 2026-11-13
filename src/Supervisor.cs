// 감독(계획 §3.8). `UartHub.exe sup`(app/Program.cs)이 이 클래스를 만들고, 그 스레드에서 Tick() 을 주기 호출한다
// (트레이 = WinForms Timer, --no-tray = 루프). 감독 로직은 모두 이 스레드에서 돈다. 백그라운드 작업은 ping·ls 소켓 호출(C# Task)뿐이다.
//
// 상태: running(코어 감시) · backoff(재시작 대기) · stopped(control.json stopped — 코어 미기동, COM 미점유)
//       · exited(exit 0 인데 stopped 아님 — 재시작 안 함, hub start 로 재개) · waiting_core(코어 mutex 점유자를 아직 입양 못 함)
// 코어 기동: config.json 검증(Config.ValidateFile = 코어와 같은 파서) → 실패면 config.last-good.json + 경고 → 같은 exe 의 `core`(창 없음, Job Object 없음, C-10).
// 입양: 코어 mutex 가 잡혀 있고 core.pid 의 PID·시작 시각이 실제 프로세스와 같으면 새로 띄우지 않고 그 프로세스를 감시한다.
// 재시작: exit≠0 → 지수 백오프(backoff_s, 마지막 값 상한), backoff_reset_s 정상 운용이면 초기화, crash_loop_count 회 연속
//        crash_loop_window_s 안에 죽으면 크래시 루프 경고. 다음 코어는 --restart-reason "exit=N[ hang=…]" 으로 떠서
//        케이블 로그에 *** hub restart (…) *** 와 *** gap 초 (hub down) ***(alive 하트비트 기준)를 쓴다.
// hang: alive\<port>(이 코어가 쓴 것)나 ControlServer ping(hello + hub status)이 hang_s 넘게 끊기면 kill 후 재시작.
// 감독 요청 shutdown(C-5, 설치기·업데이트·제거 전용): control.json 의 stopped 는 그대로 두고 코어를 정상 종료(exit 0)시킨 뒤 감독도 끝낸다.
// C-14(운영 모드만): installer mutex 가 있으면 1초마다 다시 보며 기다린다(Repair 안 함, 상한 10분 — 넘으면 경고 1회 뒤 60초마다). 사라지면 install.json 을
//   다시 판정해 version 이 다르거나 state=installing 이면 Installer.Repair(= install --repair 코드)를 백그라운드로 한 번 돈다. 맞으면 아무것도 하지 않는다.
// 케이블 목록(T3): 트레이나 아이콘(--icons-dir·운영 바탕화면)이 있으면 1초마다 코어에 `ls --detail` → 구조화된 CableRow 목록. 첫 스냅샷(코어가 바뀌면 다시 첫 스냅샷)
//   뒤의 attached 전이는 hub.log "케이블 붙음" + 트레이 풍선 대기열, pending 목록은 sup.json "pending"·트레이 "확인 필요 n".
//   아이콘(DesktopIcons)은 새 스냅샷마다 맞춘다. 정지(stop·control.json stopped)면 전부 지우고, shutdown·quit 이면 그대로 둔다(C-5).
// X5(부록 B): 감독 요청 connect [COMx]·disconnect COMx(args 배열, CLI 가 씀)는 ConnectFlow 를 백그라운드로 돌리고 끝나면 응답을 쓴다(확인 창·점유 세션 끊기·터미널).
//   on_demand(config.json)면 pending 케이블은 묻지 않는다(PendingCables·트레이 제목·sup.json pending 이 빔). F1(on_demand 일 때만): 케이블이 held_by_other 로
//   바뀌면 알려진 시리얼 도구만 열어 점유자 이름을 찾고 풍선 "COMx 사용 중: <이름>. 허브가 열지 못했습니다. 다시 연결하려면 /connect-uart COMx"
//   (끝내지 않음, connect 진행 중인 COM 은 제외, 같은 케이블은 10분에 한 번).
// X6(부록 C.4): 1초마다 <log_root>\events\*.jsonl 의 새 줄(코어 EventScanner 가 쓴 사건)을 이어 읽어(EventTail — 감독 기동 뒤 사건만) 트레이 풍선으로.
//   알리는 종류는 config events.notify(기본 panic·oops·app_error·confirm + panic·oops 가 cause 인 부팅) — 재부팅·bug·oom·missed 는 기록만(2026-11-06).
//   포트당 1분에 1번(EventBalloons — 묶인 수 "외 N건", 같은 틱의 여러 포트는 한 풍선), 누르면 그 사건의 앞뒤 로그. 코어 → 감독 경로는 파일뿐이라 코어를 막지 않는다.
//   시험 모드는 트레이가 없으므로 hub.log "풍선: …" 줄로 센다(창 1분·묶음 대기 2초는 UART_HUB_TEST_EVENTS="창,대기"초로 축소).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UartHub
{
    public sealed class SupervisorOptions
    {
        /// <summary>config.json·control.json·core.pid·alive\·hub.log 가 있는 폴더. 필수(C-2: 고정 기본값 없음 — sup 이 --hub-dir·UART_HUB_DIR·install.json 순으로 정한다).</summary>
        public string HubDir;
        /// <summary>코어로 띄울 exe(= 감독 자신, C-10). null 이면 Environment.ProcessPath.</summary>
        public string Exe;
        /// <summary>mutex 이름 기본값은 SystemAccess(C-13): 실제 Local\uart_hub_*, 시험 모드 Local\uart_hub_test_&lt;root 해시&gt;_*.</summary>
        public string SupMutex = SystemAccess.MutexName("sup");
        public string CoreMutex = SystemAccess.MutexName("core");
        /// <summary>설치기가 교체 공백 동안 쥐는 mutex(C-8). 운영 모드 코어의 감독 감시(C-11)에 넘긴다.</summary>
        public string UpdateMutex = SystemAccess.MutexName("update");
        /// <summary>설치기·제거기가 살아 있는 동안 존재하는 mutex(C-8·C-14). 운영 모드 감독은 이것이 사라진 뒤에만 install.json 일치를 판정한다.</summary>
        public string InstallerMutex = SystemAccess.MutexName("installer");
        /// <summary>시험: 코어 `--sim &lt;FakeBoard 사양 JSON&gt;`(코어 안 FakeBoard). 이때 config.json 이 없으면 기본값으로 띄우지 않는다.</summary>
        public string Sim;
        /// <summary>시험: --sim 과 함께 코어에 넘기는 UartHub.Tests.dll 경로(운영에서는 tests DLL 을 로드하지 않는다).</summary>
        public string SimDll;
        /// <summary>바탕화면 아이콘 폴더(T3). null 이면 아이콘을 다루지 않는다.</summary>
        public string IconsDir;
        /// <summary>운영 모드(C-11): 허브 폴더를 install.json 에서 얻었다 → 코어에 --watch-sup 을 넘긴다.</summary>
        public bool Production;
        /// <summary>트레이용: 실행 중인 코어에 ls --detail 을 주기적으로 보내 Cables 를 채운다(IconsDir 가 있어도 폴링한다).</summary>
        public bool PollCables;
    }

    /// <summary>감독이 본 케이블 한 줄(ls --detail, T3). Status: attached·detached·released·pending·ignored·held_by_other·open_failed·bind_failed·stopped.</summary>
    public sealed class CableRow
    {
        public int Port;
        public long Queue;
        /// <summary>이 attach 뒤 받은 바이트(F2, ls --detail 의 rx_since_attach). 붙어 있지 않으면 -1.</summary>
        public long RxSinceAttach = -1;
        /// <summary>보오(ls 의 baud, config cables.&lt;id&gt;.baud — 기본 115200).</summary>
        public int Baud;
        public string Cable, Com, Board, Status, State, Alias, Description, ModeExplicit;
        /// <summary>에이징 세션(부록 C.12, ls --detail 의 session): 이름·폴더(없으면 null = 세션 아님)·경과(초)·마지막 출력 뒤(초, 아직 없으면 null).</summary>
        public string SessionName, SessionFolder;
        public double SessionElapsedS;
        public double? SessionLastRxAgeS;
        public bool Aging => SessionName != null;

        public static CableRow From(JsonElement c)
        {
            var r = new CableRow
            {
                Port = (int)HubFiles.Long(c, "port"), Cable = HubFiles.Str(c, "cable"), Com = HubFiles.Str(c, "com"), Board = HubFiles.Str(c, "board"),
                Status = HubFiles.Str(c, "status"), State = HubFiles.Str(c, "state"), Alias = HubFiles.Str(c, "alias"), Queue = HubFiles.Long(c, "queue"),
                Description = HubFiles.Str(c, "description"), ModeExplicit = HubFiles.Str(c, "mode_explicit"), RxSinceAttach = HubFiles.Long(c, "rx_since_attach", -1),
                Baud = (int)HubFiles.Long(c, "baud"),
            };
            if (c.TryGetProperty("session", out var s) && s.ValueKind == JsonValueKind.Object && HubFiles.Str(s, "name") is string n)
            {
                r.SessionName = n; r.SessionFolder = HubFiles.Str(s, "folder");
                r.SessionElapsedS = s.TryGetProperty("elapsed_s", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 0;
                r.SessionLastRxAgeS = s.TryGetProperty("last_rx_age_s", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetDouble() : (double?)null;
            }
            return r;
        }

        /// <summary>트레이 정보 줄(세션 중일 때만): "에이징 중 · &lt;이름&gt; · 2시간 13분 · 마지막 출력 3초 전"(10분 넘게 출력 없으면 "출력 없음 12분"). 앞의 점은 메뉴가 그린다.</summary>
        public string AgingLine => Aging ? AgingText.MenuLine(SessionName, SessionElapsedS, SessionLastRxAgeS) : null;
        /// <summary>10분 넘게 출력이 없음(아직 받은 것이 없으면 세션 경과로) — 메뉴 점을 경고색으로.</summary>
        public bool AgingStale => Aging && AgingText.Stale(SessionLastRxAgeS, SessionElapsedS);

        /// <summary>아이콘·풍선 이름(터미널 창 제목은 Terminals.Title) "&lt;보드|COMx&gt; (&lt;포트&gt;)".</summary>
        public string IconName => DesktopIcons.NameOf(DesktopIcons.LabelOf(Board, Com), Port);
    }

    public sealed class Supervisor
    {
        const double StopGraceS = 30;          // core.stop 뒤 이 시간 안에 안 끝나면 kill
        const double StateWriteS = 10;         // sup.json 주기 갱신(상태가 바뀌면 바로 쓴다)
        const double StaleRequestS = 30;       // 요청자는 8~10초만 기다린다 — 이보다 오래된 요청(죽은 CLI 의 잔재)은 실행하지 않는다
        const double LsIntervalS = 1;

        readonly SupervisorOptions opt;
        readonly HubLog log;
        readonly string hubDir;
        readonly int myPid;
        readonly long myStartTicks;
        readonly DateTime myStart;

        Config cfg;                            // 생성자에서 언어를 정한 뒤 만든다(Config 가 ProductProfile 을 처음 읽으면 프로필 오류 글이 그때 언어로 굳는다)
        string cfgPath, configWarning, lastLoggedWarning;

        // 코어
        Process core;
        bool adopted;
        int corePid;
        DateTime coreStartUtc;
        string coreVersion, coreConfig, coreControl;
        int controlPort;

        // 상태
        string state = "starting";
        int restarts, backoffIdx, quickDeaths;
        bool crashLoop, stopRequested, startAfterStop, shutdownRequested;
        string pendingRestart;                 // 코어가 exit 0 으로 끝나면 이 사유로 다시 띄운다: "upgrade"(hub upgrade) · "language"(ApplyLanguage) · null
        DateTime stopRequestedUtc, nextStartUtc, lastTickUtc, hangBaselineUtc, lastStateWriteUtc, lastRespCleanupUtc, lastWaitCheckUtc;
        string nextReason;
        int? lastExit;
        DateTime? lastExitAt;
        string lastExitReason;
        bool dirty = true;

        // ping·ls(C# Task — PS 와 무관)
        Task<bool> pingTask;
        DateTime lastPingOkUtc, lastPingAttemptUtc;
        Task<List<CableRow>> lsTask;
        int lsCorePid;
        DateTime lastLsUtc;

        // 케이블 목록(T3)·아이콘
        List<CableRow> cables = new List<CableRow>();
        Dictionary<string, string> prevStatus = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool haveSnapshot;
        int snapshotCorePid;
        readonly Queue<CableRow> attachEvents = new Queue<CableRow>();
        string pendingKey = "", iconKey = "";
        DesktopIcons icons;
        TermTitler titles;                     // 허브가 띄운 터미널 창 제목 맞추기(TermTitles.cs)
        bool iconsCleared;
        DateTime lastIconTickUtc;

        // 트레이 풍선 대기열(C-14 결과·설치기 멈춤·이미 실행 중·F1) — 트레이 Timer 가 꺼낸다
        readonly System.Collections.Concurrent.ConcurrentQueue<string> notices = new System.Collections.Concurrent.ConcurrentQueue<string>();
        // X6: 사건 풍선(글 + 누르면 열 파일) — 트레이 Timer 가 꺼낸다
        readonly System.Collections.Concurrent.ConcurrentQueue<(string Text, string Open)> eventNotices = new System.Collections.Concurrent.ConcurrentQueue<(string, string)>();
        EventTail evTail;
        readonly EventBalloons balloons = new EventBalloons
        {
            WindowS = SystemAccess.TestNumber("UART_HUB_TEST_EVENTS", 0, 60),
            SettleS = SystemAccess.TestNumber("UART_HUB_TEST_EVENTS", 1, 2),
        };
        DateTime lastEventsUtc;
        // F1: held_by_other 로 알린 케이블(상태가 바뀌면 지워 다음에 다시 알린다)
        readonly HashSet<string> heldNotified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        TestConnectUi testUi;

        // C-14 install.json 일치 점검
        bool installCheckDone, installWaitLogged, installWaitWarned;
        DateTime installWaitStartUtc, lastInstallCheckUtc;
        Task<int> repairTask;

        public Supervisor(SupervisorOptions o)
        {
            if (string.IsNullOrEmpty(o.HubDir)) throw new ArgumentException(L.T("HubDir 필요", "HubDir is required"));
            opt = o;
            if (string.IsNullOrEmpty(opt.Exe)) opt.Exe = Environment.ProcessPath;
            hubDir = o.HubDir;
            Directory.CreateDirectory(hubDir);
            Directory.CreateDirectory(HubFiles.ReqDir(hubDir));
            Directory.CreateDirectory(HubFiles.RespDir(hubDir));
            L.ConfigureFromFile(HubFiles.ConfigPath(hubDir));                 // 첫 hub.log 줄부터 config.json "language"(PickConfig 도 같은 파일·같은 키로 다시 정함)
            cfg = Config.Default();
            log = new HubLog(Path.Combine(hubDir, "hub.log"), SystemClock.Instance, "sup");
            using var me = Process.GetCurrentProcess();
            myPid = me.Id;
            myStartTicks = HubFiles.StartTicks(me);
            myStart = me.StartTime;
        }

        public string State => state;
        public bool CrashLoop => crashLoop;
        public bool Stopped => HubFiles.IsStopped(hubDir);
        public bool QuitRequested { get; private set; }
        public string ConfigWarning => configWarning;
        /// <summary>기준 로그 폴더(트레이 "로그 폴더 열기") — Config.ResolveLogRoot 로 푼 log_root(log_layout 2 면 그 아래 콘솔로그·에이징이벤트·hub).</summary>
        public string LogRoot => Paths.BaseRoot;
        /// <summary>감독이 마지막으로 읽은 설정을 코어·CLI 와 같은 규칙으로 푼 것(경로만 쓴다).</summary>
        Config Paths
        {
            get
            {
                var c = cfg;
                if (c.BaseRoot == null) { c.HubDir = hubDir; c.ResolveLogRoot(hubDir); }
                return c;
            }
        }
        /// <summary>마지막 ls --detail 스냅샷(감독 스레드에서만 바뀐다 — 트레이 Timer 도 같은 스레드).</summary>
        public IReadOnlyList<CableRow> Cables => cables;
        /// <summary>첫 연결 질문 대상(트레이 FirstConnectForm·제목 "확인 필요 n"·sup.json pending). on_demand 면 묻지 않으므로 빈 배열(X5 부록 B.4).</summary>
        public CableRow[] PendingCables => OnDemand ? new CableRow[0] : cables.Where(c => c.Status == "pending").ToArray();
        /// <summary>config.json on_demand(필요할 때만 연결). 감독이 마지막으로 읽은 설정 — 코어와 같은 파일.</summary>
        public bool OnDemand => cfg.OnDemand;
        /// <summary>아이콘 이름 충돌(남의 같은 이름 파일 때문에 만들지 못한 아이콘). 아이콘을 다루지 않으면 빈 배열.</summary>
        public string[] IconConflicts => icons?.Conflicts ?? new string[0];
        /// <summary>첫 스냅샷 뒤 attached 로 바뀐 케이블(트레이 풍선용). 꺼내면 비운다.</summary>
        public CableRow[] TakeAttached()
        {
            var a = attachEvents.ToArray();
            attachEvents.Clear();
            return a;
        }
        /// <summary>트레이 풍선 대기열(C-14 결과·설치기 멈춤 경고·이미 실행 중·F1). 꺼내면 비운다.</summary>
        public string[] TakeNotices()
        {
            var l = new List<string>();
            while (notices.TryDequeue(out var n)) l.Add(n);
            return l.ToArray();
        }
        /// <summary>사건 풍선(X6): (글, 누르면 열 파일 — 앞뒤 로그, 없으면 null). 꺼내면 비운다.</summary>
        public (string Text, string Open)[] TakeEventNotices()
        {
            var l = new List<(string, string)>();
            while (eventNotices.TryDequeue(out var n)) l.Add(n);
            return l.ToArray();
        }
        /// <summary>
        /// 사람이 보는 에이징 폴더(트레이 "에이징 폴더 열기"·풍선 클릭은 이 아래 .txt·폴더만): log_layout 3 = &lt;기준&gt;\에이징(세션 폴더들),
        /// log_layout 2 = &lt;기준&gt;\에이징이벤트, 아니면 &lt;log_root&gt;\events.
        /// </summary>
        public string EventsDir => Paths.EventsTxtDir;
        /// <summary>이벤트 txt 가 있을 수 있는 폴더 전부(layout 3 = 두 언어의 에이징 루트 — 지금 언어 먼저). 풍선 클릭 경로 확인용.</summary>
        public string[] EventsDirs => Paths.EventsTxtDirs;
        /// <summary>사건 jsonl 폴더(내부 기록 — 풍선이 이어 읽음).</summary>
        public string EventsJsonlDir => Paths.EventsJsonlDir;
        /// <summary>미연결 UART 정리(트레이 설정 ▸ 미연결 UART 정리…): 꽂혀 있지 않은 기억된 케이블. 허브가 꺼졌거나 답이 없으면 null + error. 코어 호출(5초 안).</summary>
        public List<RememberedCable> RememberedCables(out string error)
            => CableCleanup.List(controlPort, Path.Combine(hubDir, "token"), EventsJsonlDir, out error);

        /// <summary>고른 케이블의 포트 번호 기록을 지운다(코어 cable forget, 꽂혀 있는 것은 코어가 거절). 돌려주는 값 = 지운 수, 실패가 있으면 error. 풍선은 부르는 쪽(트레이)이.</summary>
        public int ForgetCables(IEnumerable<string> ids, out string error)
        {
            var l = ids?.ToList() ?? new List<string>();
            int n = CableCleanup.Forget(controlPort, Path.Combine(hubDir, "token"), l, out error);
            log.Write(L.T("미연결 UART 정리: " + n + "/" + l.Count + "개 지움" + (error != null ? " — " + error : ""), "Clean up disconnected UARTs: deleted " + n + "/" + l.Count + (error != null ? " — " + error : "")));
            return n;
        }

        /// <summary>이 감독이 돈 Repair 횟수(C-14).</summary>
        public int RepairCount { get; private set; }

        public void LogError(Exception e) => log.Write(L.T("감독 오류: " + e.GetType().Name + ": " + e.Message, "Supervisor error: " + e.GetType().Name + ": " + e.Message));

        // ================================================================== 시작
        /// <summary>기동 직후 1회: 기존 코어 입양 → 정지 상태 확인 → 코어 기동.</summary>
        public void Init()
        {
            log.Write(L.T("감독 시작 pid=" + myPid + " v=" + Hub.Version + " hub_dir=" + hubDir + (opt.Sim != null ? " sim=" + opt.Sim : "") + (opt.Production ? " (운영 모드)" : ""), "Supervisor started pid=" + myPid + " v=" + Hub.Version + " hub_dir=" + hubDir + (opt.Sim != null ? " sim=" + opt.Sim : "") + (opt.Production ? " (production mode)" : "")));
            // 설치기가 살아 있으면 UartHub.old.exe 는 그 설치기의 되돌림 백업이다 — 지우지 않는다(설치기 5 단계·C-14 Repair·다음 감독 기동이 지운다)
            if (CoreMain.MutexExists(opt.InstallerMutex)) log.Write(L.T("설치기 실행 중 — 옛 이미지(UartHub.old*.exe)는 지우지 않음(되돌림 백업)", "Installer is running — old images (UartHub.old*.exe) are kept (rollback backup)"));
            else CleanOldImages();
            if (!string.IsNullOrEmpty(opt.IconsDir)) InitIcons();
            var now = DateTime.UtcNow;
            lastTickUtc = now;
            hangBaselineUtc = now;
            PickConfig(false);                              // 시간 상수(hang_s·backoff_s …)
            bool stopped = HubFiles.IsStopped(hubDir);
            var leftover = HubFiles.ReadJson(HubFiles.CorePid(hubDir));
            if (TryAdopt())
            {
                if (stopped)
                {
                    log.Write(L.T("control.json stopped 인데 코어가 살아 있음 → 정상 종료 요청", "control.json says stopped but the core is alive → asking it to exit cleanly"));
                    SendCoreStop("stop");
                }
                WriteState(true);
                return;
            }
            if (CoreMutexHeld())
            {
                EnterWaiting(L.T("코어 mutex 가 잡혀 있으나 core.pid 와 맞는 프로세스가 없음 — 입양 대기", "Core mutex is held but no process matches core.pid — waiting to adopt"));
                WriteState(true);
                return;
            }
            if (stopped)
            {
                state = "stopped";
                log.Write(L.T("정지 상태(control.json stopped) — 코어를 띄우지 않음(COM 미점유)", "Stopped (control.json stopped) — core not started (COM ports not held)"));
                WriteState(true);
                return;
            }
            // 이전 코어가 정리 없이 사라졌으면(core.pid 잔재) 재시작으로 기록한다(공백 = 이전 코어의 마지막 하트비트부터)
            string reason = leftover.HasValue && !HubFiles.IsAlive(HubFiles.Long(leftover.Value, "pid"), HubFiles.Long(leftover.Value, "start_ticks")) ? "exit=unknown" : null;
            StartCore(reason);
            WriteState(true);
        }

        /// <summary>
        /// 아이콘 준비(T3): 대상 = 설치된 UartHub.exe(install.json 의 exe, 없으면 설치 위치). 설치하지 않은 개발 실행이면 이 exe.
        /// icons.json 의 남은 항목·폴더의 자기 아이콘을 읽어 두고, 정리는 첫 스냅샷(또는 정지 상태면 첫 틱)에서 한다.
        /// </summary>
        void InitIcons()
        {
            try
            {
                var info = InstallInfo.Load(InstallInfo.DefaultPath);
                string target = info != null && !string.IsNullOrEmpty(info.Exe) ? info.Exe : SystemAccess.InstalledExe;
                if (!SystemAccess.FileExists(target)) target = opt.Exe;
                icons = new DesktopIcons(opt.IconsDir, target, info?.InstallId, log);
                icons.Startup();
            }
            catch (Exception e)
            {
                log.Write(L.T("아이콘 준비 실패 — 아이콘을 다루지 않음: " + e.GetType().Name + ": " + e.Message, "Icon setup failed — icons are not managed: " + e.GetType().Name + ": " + e.Message));
                icons = null;
            }
        }

        // ================================================================== 틱
        public void Tick()
        {
            var now = DateTime.UtcNow;
            double tickGap = (now - lastTickUtc).TotalSeconds;
            if (lastTickUtc != default && tickGap > Math.Max(5, cfg.HangS / 4))
            {
                // 감독 자신이 멈춰 있었다(절전·재개 등): 하트비트·ping 은 코어 잘못이 아니므로 hang 판정 기준을 지금으로 옮긴다
                hangBaselineUtc = now;
                log.Write(L.T("감독 틱 공백 " + tickGap.ToString("0.0", CultureInfo.InvariantCulture) + "초(절전·재개 추정) → hang 판정 기준 재설정", "Supervisor tick gap " + tickGap.ToString("0.0", CultureInfo.InvariantCulture) + "s (sleep/resume?) → hang baseline reset"));
            }
            lastTickUtc = now;
            Guard(ProcessRequests);
            switch (state)
            {
                case "running": Guard(() => TickRunning(now)); break;
                case "backoff":
                    if (now >= nextStartUtc) Guard(() => StartCore(nextReason));
                    break;
                case "waiting_core":
                    if ((now - lastWaitCheckUtc).TotalSeconds >= 1)
                    {
                        lastWaitCheckUtc = now;
                        Guard(() =>
                        {
                            if (TryAdopt()) return;
                            if (!CoreMutexHeld()) StartCore(nextReason ?? "exit=unknown");
                        });
                    }
                    break;
            }
            if (opt.PollCables || icons != null) Guard(() => PollLs(now));
            TickEvents(now);
            if (icons != null) Guard(() => TickIcons(now));
            if (opt.Production) Guard(() => CheckInstall(now));
            if (shutdownRequested && core == null && !QuitRequested)
            {
                QuitRequested = true;
                log.Write(L.T("종료 요청 처리: 코어 없음 → 감독 끝냄", "Shutdown request: no core → supervisor exits"));
            }
            WriteState(false);
        }

        // ================================================================== C-14 install.json 일치 점검
        static double InstallerWaitLimitS => SystemAccess.IsTest ? SystemAccess.TestNumber("UART_HUB_TEST_C14", 0, 30) : 600;
        static double InstallerRecheckS => SystemAccess.IsTest ? SystemAccess.TestNumber("UART_HUB_TEST_C14", 1, 1) : 60;

        void CheckInstall(DateTime now)
        {
            if (repairTask != null && repairTask.IsCompleted)
            {
                int rc = repairTask.IsCompletedSuccessfully ? repairTask.Result : -1;
                log.Write(L.T("Repair 끝 rc=" + rc + (repairTask.IsFaulted ? " 오류: " + repairTask.Exception?.GetBaseException().Message : "") + " (install.log)", "Repair done rc=" + rc + (repairTask.IsFaulted ? " error: " + repairTask.Exception?.GetBaseException().Message : "") + " (install.log)"));
                Notice(rc == 0 ? L.T("허브 설정을 복구했습니다.", "Hub setup repaired.") : L.T("허브 설정을 복구하지 못했습니다. 받은 exe를 다시 실행해 주세요.", "Could not repair the hub setup. Run the downloaded exe again."));
                repairTask = null;
            }
            if (installCheckDone) return;
            double interval = installWaitWarned ? InstallerRecheckS : 1;
            if (lastInstallCheckUtc != default && (now - lastInstallCheckUtc).TotalSeconds < interval) return;
            lastInstallCheckUtc = now;
            if (CoreMain.MutexExists(opt.InstallerMutex))
            {
                if (!installWaitLogged)
                {
                    installWaitLogged = true;
                    installWaitStartUtc = now;
                    log.Write(L.T("설치기 마무리 대기(" + opt.InstallerMutex + " 있음 — Repair 하지 않음)", "Waiting for the installer to finish (" + opt.InstallerMutex + " exists — no Repair)"));
                }
                else if (!installWaitWarned && (now - installWaitStartUtc).TotalSeconds >= InstallerWaitLimitS)
                {
                    installWaitWarned = true;
                    log.Write(L.T("경고: 설치기가 " + InstallerWaitLimitS.ToString("0", CultureInfo.InvariantCulture) + "초 안에 끝나지 않음 — Repair 하지 않고 "
                              + InstallerRecheckS.ToString("0", CultureInfo.InvariantCulture) + "초마다 다시 확인",
                              "Warning: the installer did not finish within " + InstallerWaitLimitS.ToString("0", CultureInfo.InvariantCulture) + "s — no Repair, checking again every "
                              + InstallerRecheckS.ToString("0", CultureInfo.InvariantCulture) + "s"));
                    Notice(L.T("등록이 끝나지 않았습니다. 열린 UART 허브 창을 확인하거나 exe를 다시 실행해 주세요.", "Setup did not finish. Check the open UART Hub window or run the exe again."));
                }
                return;
            }
            installCheckDone = true;
            if (installWaitLogged) log.Write(L.T("설치기 끝남(" + (now - installWaitStartUtc).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "초 대기) → 설치 상태 다시 판정", "Installer finished (waited " + (now - installWaitStartUtc).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s) → checking the install state again"));
            var info = InstallInfo.Load(InstallInfo.DefaultPath);
            string why = info == null ? null
                       : info.Version != Hub.Version ? L.T("install.json version=" + (info.Version ?? "(없음)") + " ≠ " + Hub.Version, "install.json version=" + (info.Version ?? "(none)") + " ≠ " + Hub.Version)
                       : info.State == "installing" ? L.T("install.json state=installing(설치기가 마무리 전에 끝남)", "install.json state=installing (the installer ended before finishing)") : null;
            if (info == null) { log.Write(L.T("install.json 없음 — 설치 상태 점검 건너뜀", "No install.json — install check skipped")); return; }
            if (why == null) { log.Write(L.T("설치 상태 일치(v" + info.Version + ", state=" + info.State + ") — Repair 안 함", "Install state matches (v" + info.Version + ", state=" + info.State + ") — no Repair")); return; }
            RepairCount++;
            log.Write(L.T("설치 상태 불일치(" + why + ") → Repair 시작(백그라운드, update mutex 없음·sup 기동 없음)", "Install state mismatch (" + why + ") → starting Repair (background, no update mutex, no sup start)"));
            repairTask = Task.Run(() => Installer.Repair(new InstallLog { Echo = false }, "sup"));
        }

        /// <summary>
        /// X6 사건 풍선: 1초마다 사건 jsonl 의 새 줄 → 포트별 묶기 → 때가 된 풍선을 hub.log "풍선: …" 와 트레이 대기열로. 처음 보는 폴더는 지금 끝부터
        /// (감독 기동 전 사건은 알리지 않는다). 오류는 감독 오류로 올리지 않고 10초에 한 줄만 남긴다(감독 틱을 막지 않는다).
        /// </summary>
        void TickEvents(DateTime now)
        {
            if (lastEventsUtc != default && (now - lastEventsUtc).TotalSeconds < 1) return;
            lastEventsUtc = now;
            try
            {
                string dir = EventsJsonlDir;
                if (evTail == null || !string.Equals(evTail.Dir, dir, StringComparison.OrdinalIgnoreCase)) { evTail = new EventTail(dir, null, false); return; }
                var local = now.ToLocalTime();
                balloons.Notify = cfg.Events?.Notify;                    // config events.notify(다시 읽으면 바로)
                foreach (var e in evTail.Poll()) balloons.Add(e, local);
                var due = balloons.Due(local);
                if (due.Count > 0)
                {
                    // 같은 틱의 여러 포트는 풍선 하나(L8): 트레이는 줄바꿈, hub.log 는 ' | '
                    var b = EventBalloons.Merge(due, " | ");
                    log.Write(L.T("풍선: " + b.Text + (b.Count > 1 ? " [" + b.Count + "건 묶음]" : "") + (b.Open != null ? " (누르면 " + b.Open + ")" : ""), "Balloon: " + b.Text + (b.Count > 1 ? " [" + b.Count + " merged]" : "") + (b.Open != null ? " (click opens " + b.Open + ")" : "")));
                    var t = EventBalloons.Merge(due, "\n");
                    if (eventNotices.Count < 32) eventNotices.Enqueue((t.Text.Length > 250 ? t.Text.Substring(0, 249) + "…" : t.Text, t.Open));
                }
            }
            catch (Exception e) { log.WriteThrottled("sup-events", L.T("사건 알림 읽기 실패: " + e.GetType().Name + ": " + e.Message, "Event notice read failed: " + e.GetType().Name + ": " + e.Message)); }
        }

        /// <summary>트레이 풍선 한 번(트레이가 꺼낸다). 시험 모드는 트레이가 없으므로 hub.log 의 "풍선:" 줄로 센다. 어느 스레드에서 불러도 된다.</summary>
        void Notice(string text)
        {
            if (notices.Count < 32) notices.Enqueue(text);
            log.Write(L.T("풍선: " + text, "Balloon: " + text));
        }

        /// <summary>교체(설치기 File.Replace)로 남은 옛 이미지 UartHub.old*.exe 를 지운다. 실행 중이면 지워지지 않으므로 다음 기동 때 다시 시도한다(K8a).</summary>
        void CleanOldImages()
        {
            try
            {
                string exe = opt.Exe;
                if (string.IsNullOrEmpty(exe) || !string.Equals(Path.GetFileName(exe), "UartHub.exe", StringComparison.OrdinalIgnoreCase)) return;
                // 휴대용(X4): 사용자 폴더의 exe 는 설치기가 교체한 것이 아니다 — 설치 폴더에서만 옛 이미지를 지운다
                if (!SystemAccess.SamePath(Path.GetDirectoryName(exe), SystemAccess.InstallDir)) return;
                foreach (var f in Directory.GetFiles(Path.GetDirectoryName(exe), "UartHub.old*.exe"))
                {
                    try { File.Delete(f); log.Write(L.T("옛 이미지 삭제: " + Path.GetFileName(f), "Deleted old image: " + Path.GetFileName(f))); }
                    catch (Exception e) { log.Write(L.T("옛 이미지 삭제 실패(실행 중일 수 있음 — 다음 기동 때 다시): " + Path.GetFileName(f) + ": " + e.Message, "Could not delete old image (may be running — will retry at next start): " + Path.GetFileName(f) + ": " + e.Message)); }
                }
            }
            catch (Exception e) { LogError(e); }
        }

        void Guard(Action a)
        {
            try { a(); }
            catch (Exception e) { LogError(e); dirty = true; }
        }

        void TickRunning(DateTime now)
        {
            bool exited;
            try { exited = core.HasExited; } catch (Exception) { exited = true; }
            if (exited) { OnCoreExit(ExitCodeOf(core), null); return; }
            RefreshCoreRecord();
            if (stopRequested && (now - stopRequestedUtc).TotalSeconds > StopGraceS)
            {
                log.Write(L.T("정상 종료 요청 뒤 " + StopGraceS + "초 안에 끝나지 않음 → 코어 pid=" + corePid + " kill", "Core did not exit within " + StopGraceS + "s of the stop request → kill core pid=" + corePid));
                Kill();
                OnCoreExit(ExitCodeOf(core), null);
                return;
            }
            if (!stopRequested)
            {
                string hang = CheckHang(now, out string kind);
                if (hang != null)
                {
                    log.Write(L.T("hang 감지(" + hang + ") → 코어 pid=" + corePid + " kill", "Hang detected (" + hang + ") → kill core pid=" + corePid));
                    Kill();
                    OnCoreExit(ExitCodeOf(core), kind);
                    return;
                }
            }
            double up = (now - coreStartUtc).TotalSeconds;
            if (up >= cfg.BackoffResetS && (backoffIdx > 0 || quickDeaths > 0 || crashLoop))
            {
                log.Write(L.T("코어가 " + up.ToString("0", CultureInfo.InvariantCulture) + "초 정상 운용 → 백오프·크래시 루프 초기화", "Core ran normally for " + up.ToString("0", CultureInfo.InvariantCulture) + "s → backoff and crash loop reset"));
                backoffIdx = 0; quickDeaths = 0; crashLoop = false;
                dirty = true;
            }
        }

        void Kill()
        {
            try { core.Kill(); } catch (Exception e) { log.Write(L.T("kill 실패: " + e.Message, "kill failed: " + e.Message)); }
            try { core.WaitForExit(5000); } catch (Exception) { }
        }

        static int? ExitCodeOf(Process p)
        {
            try { return p.HasExited ? p.ExitCode : (int?)null; } catch (Exception) { return null; }
        }

        // ================================================================== 코어 기동·입양
        void StartCore(string reason)
        {
            var now = DateTime.UtcNow;
            if (shutdownRequested) return;
            if (HubFiles.IsStopped(hubDir)) { state = "stopped"; dirty = true; return; }
            if (CoreMutexHeld())
            {
                if (!TryAdopt()) { nextReason = reason; EnterWaiting(L.T("코어 mutex 가 잡혀 있음 — 새 코어를 띄우지 않고 입양 대기", "Core mutex is held — not starting a new core, waiting to adopt")); }
                return;
            }
            var picked = PickConfig(true);
            if (picked == null)
            {
                ScheduleRetry(reason, L.T("유효한 설정 없음(config.json·config.last-good.json)", "No valid config (config.json, config.last-good.json)"));
                return;
            }
            string ver = Hub.Version;                                       // 코어 = 같은 exe 이미지(C-10): 버전이 섞일 수 없다
            HubFiles.TryDelete(HubFiles.CoreStop(hubDir));
            var psi = new ProcessStartInfo(opt.Exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,          // 창 없음. 감독의 표준 핸들은 상속되지 않게 sup 이 기동할 때 막아 둔다
                WorkingDirectory = Path.GetDirectoryName(opt.Exe),
            };
            psi.ArgumentList.Add("core");
            psi.ArgumentList.Add("--hub-dir"); psi.ArgumentList.Add(hubDir);
            psi.ArgumentList.Add("--core-mutex"); psi.ArgumentList.Add(opt.CoreMutex);
            if (picked != "") { psi.ArgumentList.Add("--config"); psi.ArgumentList.Add(picked); }
            if (!string.IsNullOrEmpty(reason)) { psi.ArgumentList.Add("--restart-reason"); psi.ArgumentList.Add(reason); }
            if (!string.IsNullOrEmpty(opt.Sim))
            {
                psi.ArgumentList.Add("--sim"); psi.ArgumentList.Add(opt.Sim);
                if (!string.IsNullOrEmpty(opt.SimDll)) { psi.ArgumentList.Add("--sim-dll"); psi.ArgumentList.Add(opt.SimDll); }
            }
            if (opt.Production)
            {
                psi.ArgumentList.Add("--watch-sup"); psi.ArgumentList.Add(opt.SupMutex);
                psi.ArgumentList.Add("--update-mutex"); psi.ArgumentList.Add(opt.UpdateMutex);
            }
            Process p;
            try { p = Process.Start(psi); }
            catch (Exception e)
            {
                ScheduleRetry(reason, L.T("코어 프로세스 시작 실패: " + e.Message, "Core process failed to start: " + e.Message));
                return;
            }
            string oldVer = coreVersion;
            core = p; adopted = false; corePid = p.Id;
            try { coreStartUtc = p.StartTime.ToUniversalTime(); } catch (Exception) { coreStartUtc = now; }
            coreVersion = ver; coreConfig = picked == "" ? null : picked; coreControl = null; controlPort = cfg.ControlPort;
            BeginWatch(now);
            if (!string.IsNullOrEmpty(reason)) restarts++;
            state = "running";
            dirty = true;
            log.Write(L.T("코어 시작 pid=" + corePid + " v=" + ver + " config=" + (coreConfig ?? "(기본값)") + " reason=" + (reason ?? "-"), "Core started pid=" + corePid + " v=" + ver + " config=" + (coreConfig ?? "(defaults)") + " reason=" + (reason ?? "-")));
            var prof = ProductProfile.Current;
            if (!prof.IsGeneric) log.Write(L.T("제품 프로필 " + (prof.Error == null ? "'" + prof.Name + "' " + prof.Path : "읽기 실패(일반 기본값으로 동작) " + prof.Path + ": " + prof.Error), "Product profile " + (prof.Error == null ? "'" + prof.Name + "' " + prof.Path : "read failed (running with generic defaults) " + prof.Path + ": " + prof.Error)));
            if (reason == "upgrade") log.Write(L.T("upgrade: 코어 재시작 v" + (oldVer ?? "?") + " → v" + ver, "upgrade: core restart v" + (oldVer ?? "?") + " → v" + ver));
        }

        void BeginWatch(DateTime now)
        {
            lastPingOkUtc = now; lastPingAttemptUtc = default; pingTask = null;
            hangBaselineUtc = now;
            stopRequested = false; startAfterStop = false;
            nextReason = null;
        }

        void ScheduleRetry(string reason, string why)
        {
            int delay = NextDelay();
            nextStartUtc = DateTime.UtcNow.AddSeconds(delay);
            nextReason = reason;
            state = "backoff";
            dirty = true;
            log.Write(L.T(why + " → " + delay + "초 뒤 다시 시도", why + " → retrying in " + delay + "s"));
        }

        int NextDelay()
        {
            var b = cfg.BackoffS;
            int d = b[Math.Min(backoffIdx, b.Length - 1)];
            backoffIdx++;
            return d;
        }

        void EnterWaiting(string why)
        {
            if (state != "waiting_core") log.Write(why);
            state = "waiting_core";
            lastWaitCheckUtc = DateTime.UtcNow;
            dirty = true;
        }

        /// <summary>코어 mutex 가 잡혀 있고 core.pid 의 PID·시작 시각이 살아 있는 프로세스와 같으면 입양한다.</summary>
        bool TryAdopt()
        {
            if (!CoreMutexHeld()) return false;
            var j = HubFiles.ReadJson(HubFiles.CorePid(hubDir));
            if (!j.HasValue) return false;
            long pid = HubFiles.Long(j.Value, "pid"), ticks = HubFiles.Long(j.Value, "start_ticks");
            Process p;
            try { p = Process.GetProcessById((int)pid); }
            catch (Exception) { return false; }
            try
            {
                if (p.HasExited || HubFiles.StartTicks(p) != ticks) { p.Dispose(); return false; }
                _ = p.SafeHandle;                     // 핸들을 지금 열어 둔다(종료 뒤에도 ExitCode 를 읽기 위해)
            }
            catch (Exception) { p.Dispose(); return false; }
            core = p; adopted = true; corePid = (int)pid;
            coreStartUtc = new DateTime(ticks, DateTimeKind.Utc);
            coreVersion = HubFiles.Str(j.Value, "version");
            coreConfig = HubFiles.Str(j.Value, "config");
            coreControl = HubFiles.Str(j.Value, "control");
            controlPort = (int)HubFiles.Long(j.Value, "control_port", cfg.ControlPort);
            BeginWatch(DateTime.UtcNow);
            state = "running";
            dirty = true;
            log.Write(L.T("기존 코어 입양 pid=" + corePid + " v=" + coreVersion + " started=" + HubFiles.Str(j.Value, "started") + " (새 코어를 띄우지 않음)", "Adopted existing core pid=" + corePid + " v=" + coreVersion + " started=" + HubFiles.Str(j.Value, "started") + " (no new core started)"));
            return true;
        }

        bool CoreMutexHeld()
        {
            try
            {
                using var m = new Mutex(false, opt.CoreMutex);
                bool got;
                try { got = m.WaitOne(0); }
                catch (AbandonedMutexException) { got = true; }
                if (got) { m.ReleaseMutex(); return false; }
                return true;
            }
            catch (Exception e)
            {
                log.Write(L.T("코어 mutex 확인 실패: " + e.Message, "Core mutex check failed: " + e.Message));
                return true;
            }
        }

        /// <summary>core.pid 가 지금 코어의 것이면 제어 포트 상태를 갱신한다(bind 실패면 ping 판정 생략).</summary>
        void RefreshCoreRecord()
        {
            if (coreControl == "listening" || (coreControl != null && coreControl.StartsWith("bind_failed", StringComparison.Ordinal))) return;
            var j = HubFiles.ReadJson(HubFiles.CorePid(hubDir));
            if (!j.HasValue || HubFiles.Long(j.Value, "pid") != corePid) return;
            string c = HubFiles.Str(j.Value, "control");
            if (c == coreControl) return;
            coreControl = c;
            controlPort = (int)HubFiles.Long(j.Value, "control_port", controlPort);
            dirty = true;
            if (c != null && c.StartsWith("bind_failed", StringComparison.Ordinal))
                log.Write(L.T("코어 제어 포트 " + c + " — ping hang 판정은 건너뛰고 하트비트만 본다", "Core control port " + c + " — skipping the ping hang check, watching heartbeats only"));
        }

        /// <summary>
        /// 코어에 넘길 설정 파일. config.json 이 검증(코어와 같은 파서)을 통과하면 그것(+ last-good 갱신),
        /// 실패하면 config.last-good.json + 경고, 둘 다 안 되면 null. 파일이 없으면 ""(코어 기본값) — 단 시험(-Sim)은 null.
        /// </summary>
        string PickConfig(bool forStart)
        {
            string main = HubFiles.ConfigPath(hubDir), lg = HubFiles.LastGood(hubDir);
            L.ConfigureFromFile(main);                      // 언어는 고른 파일과 상관없이 config.json 의 "language"(검증에 실패해 last-good 으로 떠도 — 코어·CLI 와 같은 곳)
            if (!File.Exists(main))
            {
                if (opt.Sim != null) { SetWarning(L.T("config.json 없음(시험 모드는 기본값으로 띄우지 않음)", "No config.json (test mode does not start with defaults)")); return null; }
                SetWarning(null);
                cfg = Config.Default(); cfgPath = "";
                _ = Paths;
                if (forStart) log.Write(L.T("config.json 없음 → 기본값으로 기동", "No config.json → starting with defaults"));
                return "";
            }
            var errs = Config.ValidateFile(main);
            if (errs.Count == 0)
            {
                try
                {
                    cfg = Config.Load(main); cfgPath = main;
                    _ = Paths;
                    SetWarning(null);
                    UpdateLastGood(main);
                    return main;
                }
                catch (Exception e) { errs = new List<string> { e.Message }; }
            }
            SetWarning(L.T("config.json 검증 실패: " + string.Join("; ", errs.Take(5)), "config.json validation failed: " + string.Join("; ", errs.Take(5))));
            if (File.Exists(lg) && Config.ValidateFile(lg).Count == 0)
            {
                try
                {
                    cfg = Config.Load(lg); cfgPath = lg;
                    _ = Paths;
                    if (forStart) log.Write(L.T(configWarning + " → config.last-good.json 으로 기동", configWarning + " → starting with config.last-good.json"));
                    return lg;
                }
                catch (Exception) { }
            }
            if (forStart) log.Write(L.T(configWarning + " → config.last-good.json 도 없거나 무효", configWarning + " → config.last-good.json is also missing or invalid"));
            return null;
        }

        void SetWarning(string w)
        {
            if (w == configWarning) return;
            configWarning = w;
            dirty = true;
            if (w != null && w != lastLoggedWarning) { lastLoggedWarning = w; log.Write(L.T("경고: " + w, "Warning: " + w)); }
            if (w == null) lastLoggedWarning = null;
        }

        void UpdateLastGood(string main)
        {
            string lg = HubFiles.LastGood(hubDir);
            string t = HubFiles.ReadShared(main);
            if (t == null || t == HubFiles.ReadShared(lg)) return;
            if (HubFiles.TryWriteAtomic(lg, t)) log.Write(L.T("config.last-good.json 갱신", "config.last-good.json updated"));
        }

        // ================================================================== 종료 처리
        void OnCoreExit(int? code, string hangKind)
        {
            var now = DateTime.UtcNow;
            double life = (now - coreStartUtc).TotalSeconds;
            string codeS = code.HasValue ? code.Value.ToString(CultureInfo.InvariantCulture) : "?";
            log.Write(L.T("코어 종료 pid=" + corePid + " exit=" + codeS + " 수명=" + life.ToString("0.0", CultureInfo.InvariantCulture) + "s"
                      + (hangKind != null ? " hang=" + hangKind : "") + (adopted ? " (입양)" : ""),
                      "Core exited pid=" + corePid + " exit=" + codeS + " lifetime=" + life.ToString("0.0", CultureInfo.InvariantCulture) + "s"
                      + (hangKind != null ? " hang=" + hangKind : "") + (adopted ? " (adopted)" : "")));
            lastExit = code; lastExitAt = DateTime.Now;
            lastExitReason = "exit=" + codeS + (hangKind != null ? " hang=" + hangKind : "");
            try { core.Dispose(); } catch (Exception) { }
            core = null; pingTask = null; adopted = false;
            string restartAs = pendingRestart;
            bool startAgain = startAfterStop;
            pendingRestart = null; stopRequested = false; startAfterStop = false;
            dirty = true;

            if (shutdownRequested)
            {
                state = "exited";
                QuitRequested = true;
                log.Write(L.T("종료 요청 처리: 코어 종료(exit=" + codeS + ") → 감독 끝냄(control.json 그대로)", "Shutdown request: core exited (exit=" + codeS + ") → supervisor exits (control.json unchanged)"));
                return;
            }
            if (HubFiles.IsStopped(hubDir))
            {
                state = "stopped";
                log.Write(L.T("정지 상태 — 재시작하지 않음(COM 해제)", "Stopped — not restarting (COM ports released)"));
                return;
            }
            if (code == 0)
            {
                if (restartAs != null) { StartCore(restartAs); return; }
                if (startAgain) { StartCore(null); return; }
                state = "exited";
                log.Write(L.T("exit 0(정상 종료) — 재시작하지 않음(hub start 로 재개)", "exit 0 (clean exit) — not restarting (resume with hub start)"));
                return;
            }
            if (code == CoreHost.CoreBusyExitCode)
            {
                nextReason = null;
                EnterWaiting(L.T("코어가 mutex 점유로 끝남(다른 코어 실행 중) → 입양 대기", "Core exited because the mutex is held (another core is running) → waiting to adopt"));
                return;
            }
            if (life >= cfg.BackoffResetS) backoffIdx = 0;
            quickDeaths = life < cfg.CrashLoopWindowS ? quickDeaths + 1 : 0;
            if (quickDeaths >= cfg.CrashLoopCount && !crashLoop)
            {
                crashLoop = true;
                log.Write(L.T("크래시 루프 경고: 코어가 " + quickDeaths + "회 연속 기동 뒤 " + cfg.CrashLoopWindowS.ToString(CultureInfo.InvariantCulture)
                          + "초 안에 죽음(마지막 exit=" + codeS + ") — 백오프로 계속 재시도, tray 경고",
                          "Crash loop warning: the core died within " + cfg.CrashLoopWindowS.ToString(CultureInfo.InvariantCulture) + "s of starting, " + quickDeaths
                          + " times in a row (last exit=" + codeS + ") — still retrying with backoff, tray warning"));
            }
            int step = backoffIdx;
            int delay = NextDelay();
            nextStartUtc = now.AddSeconds(delay);
            nextReason = lastExitReason;
            state = "backoff";
            log.Write(L.T("재시작 대기 " + delay + "초 (백오프 " + step + "단계, " + nextReason + ")", "Restart in " + delay + "s (backoff step " + step + ", " + nextReason + ")"));
        }

        // ================================================================== hang
        string CheckHang(DateTime now, out string kind)
        {
            kind = null;
            double hangS = cfg.HangS;
            bool pingOn = coreControl == null || !coreControl.StartsWith("bind_failed", StringComparison.Ordinal);
            if (pingOn)
            {
                if (pingTask != null && pingTask.IsCompleted)
                {
                    if (pingTask.Status == TaskStatus.RanToCompletion && pingTask.Result) lastPingOkUtc = now;
                    pingTask = null;
                }
                if (pingTask == null && (now - lastPingAttemptUtc).TotalSeconds >= cfg.PingIntervalS)
                {
                    lastPingAttemptUtc = now;
                    int port = controlPort;
                    string tok = Path.Combine(hubDir, "token");
                    int to = (int)Math.Clamp(hangS * 500, 500, 5000);
                    pingTask = Task.Run(() => Ping(port, tok, to));
                }
                var okRef = lastPingOkUtc > hangBaselineUtc ? lastPingOkUtc : hangBaselineUtc;
                double s = (now - okRef).TotalSeconds;
                if (s > hangS) { kind = "ping"; return L.T("ping 응답 없음 " + s.ToString("0.0", CultureInfo.InvariantCulture) + "s > hang_s " + hangS.ToString(CultureInfo.InvariantCulture), "no ping reply " + s.ToString("0.0", CultureInfo.InvariantCulture) + "s > hang_s " + hangS.ToString(CultureInfo.InvariantCulture)); }
            }
            string dir = HubFiles.AliveDir(hubDir);
            if (!Directory.Exists(dir)) return null;
            var mine = coreStartUtc.ToLocalTime().AddSeconds(-1);
            var baseLocal = hangBaselineUtc.ToLocalTime();
            var nowLocal = now.ToLocalTime();
            foreach (var f in Directory.GetFiles(dir))
            {
                string name = Path.GetFileName(f);
                if (!int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) continue;
                string t = HubFiles.ReadShared(f);
                if (t == null || !DateTime.TryParseExact(t.Trim(), "yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) continue;
                if (at < mine) continue;                          // 이 코어가 쓴 적 없는 포트(이전 코어의 잔재)
                var refT = at > baseLocal ? at : baseLocal;
                double s = (nowLocal - refT).TotalSeconds;
                if (s > hangS) { kind = "heartbeat"; return L.T("하트비트 alive\\" + name + " " + s.ToString("0.0", CultureInfo.InvariantCulture) + "s 정지 > hang_s " + hangS.ToString(CultureInfo.InvariantCulture), "heartbeat alive\\" + name + " stalled " + s.ToString("0.0", CultureInfo.InvariantCulture) + "s > hang_s " + hangS.ToString(CultureInfo.InvariantCulture)); }
            }
            return null;
        }

        static bool Ping(int port, string tokenPath, int timeoutMs)
        {
            string l = ControlClient.CallWithTokenFile(port, tokenPath, timeoutMs, "[\"hub\",\"status\"]");
            return l != null && l.Contains("\"status\":\"ok\"");
        }

        // ================================================================== 요청(스풀·트레이)
        void ProcessRequests()
        {
            var now = DateTime.UtcNow;
            string reqDir = HubFiles.ReqDir(hubDir), respDir = HubFiles.RespDir(hubDir);
            if (Directory.Exists(reqDir))
            {
                var files = Directory.GetFiles(reqDir).Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => { try { return File.GetLastWriteTimeUtc(f); } catch (Exception) { return DateTime.MaxValue; } }).ToList();
                foreach (var f in files)
                {
                    string text = HubFiles.ReadShared(f);
                    double age;
                    try { age = (now - File.GetLastWriteTimeUtc(f)).TotalSeconds; } catch (Exception) { age = 0; }
                    try { File.Delete(f); } catch (Exception) { continue; }
                    string id = Path.GetFileNameWithoutExtension(f), verb = null, from = "?";
                    var args = new List<string>();
                    if (age > StaleRequestS)
                    {
                        log.Write(L.T("오래된 요청 " + id + " (" + age.ToString("0", CultureInfo.InvariantCulture) + "초 전) — 실행하지 않고 버림", "Stale request " + id + " (" + age.ToString("0", CultureInfo.InvariantCulture) + "s old) — dropped without running"));
                        continue;
                    }
                    try
                    {
                        using var d = JsonDocument.Parse(text ?? "");
                        verb = HubFiles.Str(d.RootElement, "verb");
                        from = HubFiles.Str(d.RootElement, "from") ?? "?";
                        if (d.RootElement.TryGetProperty("args", out var av) && av.ValueKind == JsonValueKind.Array)
                            foreach (var x in av.EnumerateArray()) if (x.ValueKind == JsonValueKind.String) args.Add(x.GetString());
                    }
                    catch (JsonException) { }
                    string respPath = Path.Combine(respDir, id + ".json");
                    if (verb == "connect" || verb == "disconnect") { StartConnectRequest(verb, from, args, id, respPath); continue; }   // 오래 걸림: 끝나면 응답(X5)
                    JObj r;
                    try { r = Handle(verb, from, args); }
                    catch (Exception e) { LogError(e); r = new JObj().Set("status", "hub_down").Set("exit", 103L).Set("error", L.T("감독 내부 오류: " + e.Message, "Supervisor internal error: " + e.Message)); }
                    HubFiles.TryWriteAtomic(respPath, r.ToString());
                }
            }
            if ((now - lastRespCleanupUtc).TotalSeconds >= 10 && Directory.Exists(respDir))
            {
                lastRespCleanupUtc = now;
                foreach (var f in Directory.GetFiles(respDir))
                    try { if ((now - File.GetLastWriteTimeUtc(f)).TotalSeconds > 60) File.Delete(f); } catch (Exception) { }
                if (Directory.Exists(reqDir))
                    foreach (var f in Directory.GetFiles(reqDir, "*.cancel"))                // connect 가 끝난 뒤 늦게 온 취소 표시(X5)
                        try { if ((now - File.GetLastWriteTimeUtc(f)).TotalSeconds > 600) File.Delete(f); } catch (Exception) { }
            }
        }

        JObj Handle(string verb, string from, List<string> args)
        {
            switch (verb)
            {
                case "status": return new JObj().Set("status", "ok").Set("exit", 0L).Set("supervisor", StatusJson());
                case "stop": return RequestStop(from);
                case "start": return RequestStart(from);
                case "upgrade": return RequestUpgrade(from);
                case "language": return RequestLanguage(args.Count == 1 ? args[0] : null, from);
                case "shutdown": return RequestShutdown(from);
                case "quit":
                    QuitRequested = true;
                    log.Write(L.T("감독 끝내기 요청(" + from + ") — 코어는 그대로 둔다", "Supervisor quit request (" + from + ") — the core keeps running"));
                    return Ok("quit");
                case "notice_running":                       // 휴대용 실행(X4): 같은 exe 를 다시 더블클릭 → 트레이 풍선만
                    Notice(L.T("UART 허브가 이미 켜져 있습니다. 작업 표시줄 오른쪽의 아이콘을 쓰세요(안 보이면 ^ 안).", "UART Hub is already running. Use its icon at the right end of the taskbar (look under ^ if hidden)."));
                    return Ok("notice");
                default: return new JObj().Set("status", "rejected").Set("exit", 106L).Set("error", L.T("감독 요청은 status|stop|start|upgrade|shutdown|quit|notice_running|connect|disconnect: " + (verb ?? "(없음)"), "Supervisor requests are status|stop|start|upgrade|shutdown|quit|notice_running|connect|disconnect: " + (verb ?? "(none)")));
            }
        }

        JObj Ok(string action)
        {
            return new JObj().Set("status", "ok").Set("exit", 0L).Set("action", action).Set("stopped", HubFiles.IsStopped(hubDir))
                .Set("state", state).Set("core_pid", core != null ? (long)corePid : 0L);
        }

        bool WriteControl(bool stopped, string by)
        {
            var o = new JObj().Set("stopped", stopped).Set("by", by).Set("at", HubFiles.Iso(DateTime.Now));
            if (HubFiles.TryWriteAtomic(HubFiles.Control(hubDir), o.ToString() + "\n")) { dirty = true; return true; }
            log.Write(L.T("control.json 쓰기 실패", "control.json write failed"));
            return false;
        }

        void SendCoreStop(string reason)
        {
            var o = new JObj().Set("pid", (long)corePid).Set("reason", reason).Set("at", HubFiles.Iso(DateTime.Now));
            HubFiles.TryWriteAtomic(HubFiles.CoreStop(hubDir), o.ToString());
            stopRequested = true;
            stopRequestedUtc = DateTime.UtcNow;
            dirty = true;
            log.Write(L.T("코어 pid=" + corePid + " 정상 종료 요청(core.stop, " + reason + ")", "Asked core pid=" + corePid + " to exit cleanly (core.stop, " + reason + ")"));
        }

        /// <summary>tray "종료"·uart hub stop: control.json stopped=true 를 **먼저** 쓰고, 코어가 있으면 정상 종료(exit 0)를 요청한다.</summary>
        public JObj RequestStop(string by)
        {
            if (!WriteControl(true, by)) return new JObj().Set("status", "rejected").Set("exit", 106L).Set("error", L.T("control.json 쓰기 실패 — 코어를 멈추지 않음", "control.json write failed — core not stopped"));
            log.Write(L.T("정지 요청(" + by + ") → control.json stopped=true", "Stop request (" + by + ") → control.json stopped=true"));
            startAfterStop = false; pendingRestart = null;
            if (icons != null) { Guard(() => icons.RemoveAll(L.T("정지 요청(" + by + ")", "stop request (" + by + ")"))); iconsCleared = true; }
            if (state == "running" && core != null)
            {
                if (!stopRequested) SendCoreStop("stop");
                return Ok("stopping");
            }
            state = "stopped";
            dirty = true;
            return Ok("stopped");
        }

        /// <summary>tray "시작"·uart hub start: stopped 해제, 백오프·크래시 루프 초기화, 코어가 없으면 바로 띄운다.</summary>
        public JObj RequestStart(string by)
        {
            if (HubFiles.IsStopped(hubDir))
            {
                if (!WriteControl(false, by)) return new JObj().Set("status", "rejected").Set("exit", 106L).Set("error", L.T("control.json 쓰기 실패", "control.json write failed"));
                log.Write(L.T("시작 요청(" + by + ") → control.json stopped=false", "Start request (" + by + ") → control.json stopped=false"));
            }
            backoffIdx = 0; quickDeaths = 0; crashLoop = false;
            dirty = true;
            if (state == "running" && core != null)
            {
                if (stopRequested) { startAfterStop = true; return Ok("restart_after_stop"); }
                return Ok("already_running");
            }
            if (state == "waiting_core") return Ok("waiting_core");
            StartCore(null);
            return Ok(state == "running" ? "started" : "start_failed");
        }

        /// <summary>hub upgrade(C-10): 코어를 정상 종료시키고 같은 exe 로 다시 띄운다. 로그에 *** hub restart (upgrade) ***. 실제 버전 교체는 설치기만 한다.</summary>
        public JObj RequestUpgrade(string by)
        {
            if (HubFiles.IsStopped(hubDir)) return new JObj().Set("status", "rejected").Set("exit", 106L).Set("error", L.T("정지 상태 — hub start 먼저", "Hub is stopped — run hub start first"));
            string to = Hub.Version;
            log.Write(L.T("업그레이드 요청(" + by + ") v" + (coreVersion ?? "?") + " → v" + (to ?? "?"), "Upgrade request (" + by + ") v" + (coreVersion ?? "?") + " → v" + (to ?? "?")));
            if (state == "running" && core != null)
            {
                pendingRestart = "upgrade";
                if (!stopRequested) SendCoreStop("upgrade");
                return Ok("upgrading").Set("to_version", to);
            }
            StartCore("upgrade");
            return Ok(state == "running" ? "started" : "start_failed").Set("to_version", to);
        }

        /// <summary>
        /// 감독 요청 language(uart hub language auto|ko|en · 트레이 설정 ▸ 언어 — 감독 스레드): config.json 의 "language" 를 글자로 고치고(Config.WithLanguage —
        /// auto 는 키를 지움, 주석·순서·CRLF·BOM 그대로, 원자 쓰기) ApplyLanguage. 값이 틀리거나 config.json 을 읽거나 고치거나 쓰지 못하면 106 rejected(아무것도 바꾸지 않음).
        /// 응답 = ApplyLanguage 의 것(action unchanged · restarting · applied) + setting(auto|ko|en) · language(감독의 새 언어) · 깨끗이 지우지 못해 "auto" 를 썼으면 note.
        /// </summary>
        public JObj RequestLanguage(string value, string by)
        {
            string v = value?.Trim().ToLowerInvariant(), path = HubFiles.ConfigPath(hubDir), text = null, why = null;
            JObj Rejected(string e) { log.Write(L.T("언어 요청(" + by + ") " + (value ?? "-") + " 거부: " + e, "Language request (" + by + ") " + (value ?? "-") + " rejected: " + e)); return new JObj().Set("status", "rejected").Set("exit", 106L).Set("error", e); }
            if (!L.IsValidSetting(v)) return Rejected(L.T("언어는 auto|ko|en: " + (value ?? "(없음)"), "the language must be auto|ko|en: " + (value ?? "(none)")));
            if (File.Exists(path))
            {
                // BOM 을 지키려고 바이트로 읽는다(HubFiles.ReadShared 는 BOM 을 떼고 읽는다)
                try { using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); using var ms = new MemoryStream(); fs.CopyTo(ms); text = new UTF8Encoding(false).GetString(ms.ToArray()); }
                catch (Exception e) { return Rejected(L.T("config.json 을 읽지 못함: " + e.Message, "could not read config.json: " + e.Message)); }
            }
            string n = Config.WithLanguage(text, v, out why);
            if (n == null) return Rejected(L.T("config.json 을 고치지 못함: " + why, "could not edit config.json: " + why));
            if (n != (text ?? ""))
            {
                if (!HubFiles.TryWriteAtomic(path, n)) return Rejected(L.T("config.json 쓰기 실패", "config.json write failed"));
                log.Write("config.json language → " + v + " (" + by + ")" + (why != null ? " — " + why : ""));
            }
            var r = ApplyLanguage(by).Set("setting", v).Set("language", L.Current);
            if (why != null) r.Set("note", why);
            return r;
        }

        /// <summary>
        /// 언어 바꾸기(RequestLanguage 가 config.json "language" 를 쓴 뒤 감독 스레드에서 부른다): config 를 다시 읽어 감독 언어를 바로 바꾸고
        /// (PickConfig → L.ConfigureFromFile — 트레이 제목·풍선·hub.log 는 다음 글부터), 코어는 hub upgrade 와 같은 길로 정상 종료 뒤 같은 exe 로 다시 띄운다
        /// (케이블 로그 *** hub restart (language) ***, 이미 다른 사유(upgrade)로 다시 띄우는 중이면 그 사유 그대로). 언어가 그대로면 코어는 두고 "unchanged".
        /// 코어가 없으면(정지·백오프) 다음 기동 때 새 config 로 뜬다. 응답 action: unchanged · restarting · applied. 트레이 메뉴 글은 부른 쪽이 다시 그린다.
        /// </summary>
        public JObj ApplyLanguage(string by)
        {
            string before = L.Current;
            PickConfig(false);
            log.Write("language " + before + " -> " + L.Current + " (" + by + ")");
            if (L.Current == before) return Ok("unchanged");                // 코어도 같은 규칙(환경변수 → config → Windows)으로 정하므로 다시 띄울 일 없음
            if (state == "running" && core != null && !HubFiles.IsStopped(hubDir))
            {
                pendingRestart ??= "language";
                if (!stopRequested) SendCoreStop("language");
                return Ok("restarting");
            }
            return Ok("applied");
        }

        /// <summary>
        /// 감독 요청 shutdown(C-5, 설치기·업데이트·제거 전용): control.json 의 stopped 를 건드리지 않고 코어를 정상 종료(exit 0)시킨 뒤 감독도 끝낸다.
        /// 다시 설치해도 정지 상태가 남지 않는다. 아이콘은 지우지 않는다(새 감독이 이어받음).
        /// </summary>
        public JObj RequestShutdown(string by)
        {
            log.Write(L.T("종료 요청(" + by + ") — control.json 은 그대로, 코어 정상 종료 뒤 감독도 끝냄", "Shutdown request (" + by + ") — control.json unchanged; the supervisor exits after the core exits cleanly"));
            shutdownRequested = true;
            startAfterStop = false; pendingRestart = null;
            dirty = true;
            if (state == "running" && core != null)
            {
                if (!stopRequested) SendCoreStop("shutdown");
                return Ok("shutting_down");
            }
            QuitRequested = true;
            return Ok("shutdown");
        }

        // ================================================================== 상태
        public JObj StatusJson()
        {
            var now = DateTime.UtcNow;
            var o = new JObj().Set("pid", (long)myPid).Set("start_ticks", myStartTicks).Set("started", HubFiles.Iso(myStart))
                .Set("version", Hub.Version).Set("exe", opt.Exe).Set("state", state).Set("stopped", HubFiles.IsStopped(hubDir));
            if (core != null)
                o.Set("core_pid", (long)corePid).Set("core_started", HubFiles.Iso(coreStartUtc.ToLocalTime())).Set("core_version", coreVersion)
                 .Set("core_config", coreConfig).Set("core_control", coreControl).Set("adopted", adopted);
            else o.Null("core_pid").Null("core_started").Null("core_version").Null("core_config").Null("core_control").Set("adopted", false);
            o.Set("restarts", (long)restarts);
            if (lastExit.HasValue) o.Set("last_exit", (long)lastExit.Value); else o.Null("last_exit");
            o.Set("last_exit_at", lastExitAt.HasValue ? HubFiles.Iso(lastExitAt.Value) : null).Set("last_exit_reason", lastExitReason);
            o.Set("backoff_step", (long)backoffIdx).Set("quick_deaths", (long)quickDeaths).Set("crash_loop", crashLoop);
            if (state == "backoff") o.Set("next_start_in_s", Math.Max(0, Math.Round((nextStartUtc - now).TotalSeconds, 1))); else o.Null("next_start_in_s");
            o.Set("config", cfgPath == "" ? L.T("(기본값)", "(defaults)") : cfgPath).Set("config_warning", configWarning)
             .Set("stop_requested", stopRequested).Set("hub_dir", hubDir);
            o.Set("pending", PendingCables.Select(c => new JObj().Set("cable", c.Cable).Set("com", c.Com).Set("description", c.Description)));
            o.Set("on_demand", OnDemand);
            if (icons != null) o.Set("icons_dir", icons.Dir).Set("icon_count", (long)icons.Count).Set("icon_conflicts", icons.Conflicts);
            o.Set("updated", HubFiles.Iso(DateTime.Now));
            return o;
        }

        void WriteState(bool force)
        {
            var now = DateTime.UtcNow;
            if (!force && !dirty && (now - lastStateWriteUtc).TotalSeconds < StateWriteS) return;
            if (HubFiles.TryWriteAtomic(HubFiles.SupState(hubDir), StatusJson().ToString(), 3))
            {
                dirty = false;
                lastStateWriteUtc = now;
            }
        }

        /// <summary>감독 끝: 상태를 마지막으로 기록(코어는 건드리지 않는다 — 다음 감독이 입양).</summary>
        public void Shutdown()
        {
            WriteState(true);
            log.Write(L.T("감독 종료 pid=" + myPid + (core != null ? " (코어 pid=" + corePid + " 는 계속 실행)" : ""), "Supervisor exiting pid=" + myPid + (core != null ? " (core pid=" + corePid + " keeps running)" : "")));
        }

        // ================================================================== 트레이
        /// <summary>트레이 한 줄 요약(툴팁·메뉴 상태 줄, UI_COPY.md "트레이"). NotifyIcon.Text 는 짧아야 한다.</summary>
        public string TrayTitle => TitleOf(state, crashLoop, configWarning != null, ConnectedCount, PendingCables.Length, IconConflicts.Length, AgingCount);

        /// <summary>에이징 세션 중인 포트 수(마지막 ls --detail, 부록 C.12).</summary>
        public int AgingCount => cables.Count(c => c.Aging);

        /// <summary>열려 있는 케이블 수(attached·bind_failed — connect 의 "연결됨"과 같은 기준).</summary>
        public int ConnectedCount => cables.Count(c => c.Status == "attached" || c.Status == "bind_failed");

        /// <summary>트레이 아이콘: "warn"(계속 꺼짐·설정 파일 오류) · "rec"(에이징 기록 중 — 세션 하나 이상) · "run"(실행 중) · "stop"(그 밖).</summary>
        public string TrayIconKind => IconKindOf(crashLoop || configWarning != null, state == "running" && AgingCount > 0, state == "running");

        /// <summary>아이콘 우선순위: 경고 &gt; 에이징 기록 중 &gt; 실행 중 &gt; 정지.</summary>
        public static string IconKindOf(bool warn, bool aging, bool running) => warn ? "warn" : aging ? "rec" : running ? "run" : "stop";

        /// <summary>
        /// 트레이 제목: 계속 꺼짐 &gt; 설정 파일 오류 &gt; 상태("연결 3개"·"연결 없음"·"정지됨"·"다시 켜는 중"·"켜는 중"). 확인 필요(on_demand 아님)·아이콘 이름 충돌은 뒤에 붙인다.
        /// </summary>
        public static string TitleOf(string state, bool crashLoop, bool configWarning, int connected, int pending, int conflicts, int aging = 0)
        {
            string s = crashLoop ? L.T("계속 꺼짐, 로그 확인 필요", "Keeps stopping, check the log")
                     : configWarning ? L.T("설정 파일 오류 (마지막 정상 설정으로 동작 중)", "Config file error (using the last good config)")
                     : state switch
                     {
                         "running" => connected > 0 ? L.T("연결 " + connected.ToString(CultureInfo.InvariantCulture) + "개", connected.ToString(CultureInfo.InvariantCulture) + " connected") : L.T("연결 없음", "No connections"),
                         "stopped" or "exited" => L.T("정지됨", "Stopped"),
                         "backoff" => L.T("다시 켜는 중", "Restarting"),
                         "starting" or "waiting_core" => L.T("켜는 중", "Starting"),
                         _ => state,
                     };
            if (aging > 0 && state == "running" && !crashLoop && !configWarning) s += L.T(" · 에이징 중 " + aging.ToString(CultureInfo.InvariantCulture) + "개", " · Aging " + aging.ToString(CultureInfo.InvariantCulture));
            if (pending > 0) s += L.T(" · 확인 필요 " + pending.ToString(CultureInfo.InvariantCulture), " · Needs attention " + pending.ToString(CultureInfo.InvariantCulture));
            if (conflicts > 0) s += L.T(" · 아이콘 이름 충돌 " + conflicts.ToString(CultureInfo.InvariantCulture), " · Icon name conflicts " + conflicts.ToString(CultureInfo.InvariantCulture));
            return L.T("UART 허브 · " + s, "UART Hub · " + s);
        }

        void PollLs(DateTime now)
        {
            if (lsTask != null && lsTask.IsCompleted)
            {
                var rows = lsTask.Status == TaskStatus.RanToCompletion ? lsTask.Result : null;
                int pid = lsCorePid;
                lsTask = null;
                if (rows != null && state == "running" && core != null && corePid == pid) ApplyLs(rows);
            }
            if (state != "running")
            {
                if (cables.Count > 0) { cables = new List<CableRow>(); PendingChanged(); }
                return;
            }
            if (lsTask != null || (now - lastLsUtc).TotalSeconds < LsIntervalS) return;
            lastLsUtc = now;
            int port = controlPort;
            lsCorePid = corePid;
            string tok = Path.Combine(hubDir, "token");
            lsTask = Task.Run(() => LsRows(port, tok));
        }

        /// <summary>
        /// 새 스냅샷: attached 전이(첫 스냅샷 뒤 — 코어가 바뀌면 그 코어의 첫 스냅샷은 기준선)·pending 목록·아이콘 맞추기·띄운 터미널 창 제목 맞추기.
        /// 정지·shutdown·quit 중이면 아이콘은 건드리지 않는다(정지는 RemoveAll 이 이미 지움).
        /// </summary>
        void ApplyLs(List<CableRow> rows)
        {
            bool baseline = !haveSnapshot || snapshotCorePid != corePid;
            foreach (var r in rows)
            {
                prevStatus.TryGetValue(r.Cable ?? "", out var was);
                if (!baseline && r.Status == "attached" && was != "attached")
                {
                    log.Write(L.T("케이블 붙음 :" + r.Port + " " + r.Com + " " + (r.Board ?? "-") + " (" + r.Cable + ")", "Cable attached :" + r.Port + " " + r.Com + " " + (r.Board ?? "-") + " (" + r.Cable + ")"));
                    if (attachEvents.Count < 32) attachEvents.Enqueue(r);
                }
                if (r.Status == "pending" && was != "pending")
                    log.Write((OnDemand ? L.T("연결 대기(on_demand — /connect-uart 로 연결, 묻지 않음) ", "Waiting to connect (on_demand — connect with /connect-uart, no prompt) ") : L.T("확인 필요(첫 연결) ", "Needs attention (first connection) ")) + r.Com + " " + r.Cable + (r.Description != null ? " (" + r.Description + ")" : ""));
                HeldNotice(r);
            }
            prevStatus = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows) if (r.Cable != null) prevStatus[r.Cable] = r.Status;
            haveSnapshot = true;
            snapshotCorePid = corePid;
            cables = rows;
            PendingChanged();
            if (icons != null && !shutdownRequested && !QuitRequested && !HubFiles.IsStopped(hubDir))
            {
                icons.Reconcile(rows);
                string ck = string.Join("|", icons.Conflicts) + "#" + icons.Count;
                if (ck != iconKey) { iconKey = ck; dirty = true; }
            }
            if (!shutdownRequested && !QuitRequested)
            {
                titles ??= new TermTitler(hubDir, s => log.Write(s), SystemTermWindows.Instance);
                titles.Apply(rows, DateTime.UtcNow);
            }
        }

        void PendingChanged()
        {
            string k = string.Join(",", cables.Where(c => c.Status == "pending").Select(c => c.Cable));
            if (k == pendingKey) return;
            pendingKey = k;
            dirty = true;
        }

        /// <summary>정지 상태(control.json stopped)면 아이콘을 한 번 모두 지운다(기동 때 남은 아이콘 포함). shutdown·quit 중에는 건드리지 않는다.</summary>
        void TickIcons(DateTime now)
        {
            if ((now - lastIconTickUtc).TotalSeconds < 1) return;
            lastIconTickUtc = now;
            if (shutdownRequested || QuitRequested) return;
            if (!HubFiles.IsStopped(hubDir)) { iconsCleared = false; return; }
            if (iconsCleared) return;
            iconsCleared = true;
            icons.RemoveAll(L.T("정지 상태", "stopped"));
            dirty = true;
        }

        static List<CableRow> LsRows(int port, string tokenPath)
        {
            string l = ControlClient.CallWithTokenFile(port, tokenPath, 2000, "[\"ls\",\"--detail\"]");
            if (l == null) return null;
            try
            {
                using var d = JsonDocument.Parse(l);
                if (!d.RootElement.TryGetProperty("cables", out var cs) || cs.ValueKind != JsonValueKind.Array) return null;
                return cs.EnumerateArray().Select(CableRow.From).ToList();
            }
            catch (JsonException) { return null; }
        }

        /// <summary>
        /// 트레이의 케이블 판정(C-4): 코어에 `cable &lt;argv…&gt;` 를 보낸다. 블로킹(최대 5초) — 트레이는 백그라운드 작업에서 부른다.
        /// 응답 줄(JSON), 코어에 닿지 못하면 null. hub.log 에 한 줄.
        /// </summary>
        /// <summary>
        /// 트레이의 에이징 시작·끝(부록 C.12): 코어에 `session &lt;argv…&gt;` 를 보낸다. 블로킹(최대 10초 — 끝내기는 요약을 쓰고 답한다). 응답 줄, 닿지 못하면 null. hub.log 에 한 줄.
        /// </summary>
        public string SessionCommand(params string[] argv)
        {
            int port = controlPort;
            string tok = Path.Combine(hubDir, "token");
            string json = "[" + string.Join(",", new[] { "session" }.Concat(argv).Select(Json.Str)) + "]";
            string l = ControlClient.CallWithTokenFile(port, tok, 10000, json);
            log.Write(L.T("트레이 session ", "Tray session ") + string.Join(" ", argv) + " → " + (l == null ? L.T("코어 응답 없음", "no core reply") : l.Length > 200 ? l.Substring(0, 200) + "…" : l));
            return l;
        }

        public string CableCommand(params string[] argv)
        {
            int port = controlPort;
            string tok = Path.Combine(hubDir, "token");
            string json = "[" + string.Join(",", new[] { "cable" }.Concat(argv).Select(Json.Str)) + "]";
            string l = ControlClient.CallWithTokenFile(port, tok, 5000, json);
            log.Write(L.T("트레이 cable ", "Tray cable ") + string.Join(" ", argv) + " → " + (l == null ? L.T("코어 응답 없음", "no core reply") : l.Length > 200 ? l.Substring(0, 200) + "…" : l));
            return l;
        }

        // ================================================================== X5 /connect-uart · /disconnect-uart(부록 B)
        /// <summary>확인 창(트레이가 설정 — WinForms). null 이면 시험 모드는 자동 응답(TestConnectUi), 실제 모드는 창 없음(점유자가 있으면 106).</summary>
        public IConnectUi ConnectUi { get; set; }

        /// <summary>
        /// 이 감독 스레드(트레이 UI 스레드·--no-tray 루프)에서 부른다: 코어 제어 포트·코어 pid·스캔 주기·holder_scan_skip 을 지금 값으로 잡아 둔 흐름.
        /// 흐름 자체는 백그라운드에서 돌려도 된다(감독 필드를 다시 읽지 않는다). requesterGone = CLI 요청의 &lt;id&gt;.cancel(트레이는 null).
        /// </summary>
        public ConnectFlow NewConnectFlow(Func<bool> requesterGone = null)
        {
            IConnectUi ui = ConnectUi;
            if (ui == null && SystemAccess.IsTest) ui = LazyInitializer.EnsureInitialized(ref testUi, () => new TestConnectUi(s => log.Write(s)));
            return new ConnectFlow(hubDir, controlPort, core != null ? corePid : 0, cfg.ScanIntervalMs, cfg.HolderScanSkip, s => log.Write(s), ui, requesterGone);
        }

        /// <summary>감독 요청 connect·disconnect: 흐름은 받을 때(감독 스레드) 만들고 백그라운드로 돌려, 끝나면 sup\resp\&lt;id&gt;.json(요청자는 길게 기다린다).</summary>
        void StartConnectRequest(string verb, string from, List<string> args, string id, string respPath)
        {
            log.Write(L.T("감독 요청 " + verb + " " + string.Join(" ", args) + " (" + from + ")", "Supervisor request " + verb + " " + string.Join(" ", args) + " (" + from + ")"));
            string cancel = Path.Combine(HubFiles.ReqDir(hubDir), id + ".cancel");          // CLI 가 시간 초과·종료 때 쓴다(*.json 이 아니라 요청으로 읽히지 않음)
            var flow = NewConnectFlow(() => File.Exists(cancel));
            Task.Run(() =>
            {
                JObj r;
                try
                {
                    int baud = 0;
                    if (args.Count > (verb == "connect" ? 2 : 1)) r = new JObj().Set("status", "rejected").Set("exit", 106L).Set("error", L.T(verb + ": 인자가 많음(" + string.Join(" ", args) + ")", verb + ": too many arguments (" + string.Join(" ", args) + ")"));
                    else if (args.Count == 2 && (!int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out baud) || !Config.IsStandardBaud(baud)))
                        r = new JObj().Set("status", "rejected").Set("exit", 106L).Set("error", L.T("baud 는 " + Config.StandardBaudText + " 중 하나: '" + args[1] + "'", "baud must be one of " + Config.StandardBaudText + ": '" + args[1] + "'"));
                    else if (verb == "connect") r = args.Count == 0 ? flow.Table() : flow.Connect(args[0], from, baud);
                    else r = args.Count == 1 ? flow.Disconnect(args[0], from) : new JObj().Set("status", "rejected").Set("exit", 106L).Set("error", L.T("disconnect: COMx 가 필요함", "disconnect: COMx is required"));
                }
                catch (Exception e) { LogError(e); r = new JObj().Set("status", "hub_down").Set("exit", 103L).Set("error", L.T("감독 내부 오류: " + e.GetType().Name + ": " + e.Message, "Supervisor internal error: " + e.GetType().Name + ": " + e.Message)); }
                if (File.Exists(cancel)) { HubFiles.TryDelete(cancel); log.Write(L.T("감독 요청 " + id + ": 요청자가 떠남 — 응답은 쓰지 않음", "Supervisor request " + id + ": the requester left — no reply written")); return; }
                HubFiles.TryWriteAtomic(respPath, r.ToString());
            });
        }

        const double HeldNoticeMinS = 600;                       // F1: 같은 케이블은 10분에 한 번만 다시 찾고 알린다(붙었다 떨어졌다 해도)
        readonly Dictionary<string, DateTime> heldNoticeAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// F1(부록 B.5, on_demand 일 때만): 연결됐던 케이블이 held_by_other 로 바뀌면 한 번 — 알려진 시리얼 도구만 열어(후보 검사, 전체 검사 안 함) 점유자 이름을 찾고 풍선.
        /// 끝내지 않는다. connect 진행 중인 COM 은 알리지 않는다. 같은 케이블은 10분 안에 다시 찾지도 알리지도 않는다. 감독 스레드에서 부른다.
        /// </summary>
        void HeldNotice(CableRow r)
        {
            if (r.Cable == null || !OnDemand) return;
            if (r.Status != "held_by_other") { heldNotified.Remove(r.Cable); return; }
            if (r.Com == null || ConnectFlow.IsActive(r.Com) || heldNotified.Contains(r.Cable)) return;
            var now = DateTime.UtcNow;
            if (heldNoticeAt.TryGetValue(r.Cable, out var at) && (now - at).TotalSeconds < HeldNoticeMinS) { heldNotified.Add(r.Cable); return; }
            heldNotified.Add(r.Cable);
            heldNoticeAt[r.Cable] = now;
            string com = r.Com;
            int cp = core != null ? corePid : 0;
            var skip = cfg.HolderScanSkip;
            Task.Run(() =>
            {
                string who = L.T("다른 프로그램", "another program"), name = L.T("다른 프로그램", "another program");
                string mode = "?";
                try
                {
                    var sc = ComHolders.Scan(new[] { com }, new[] { Environment.ProcessId, cp }.Where(p => p > 0).ToArray(), HolderScanMode.Candidates, skip);
                    mode = sc.Mode + " " + sc.Ms + "ms";
                    var h = sc.Of(com).FirstOrDefault();
                    if (h != null) { who = h.Label + "(pid " + h.Pid + ")"; name = h.Label; }
                }
                catch (Exception e) { log.Write(L.T("F1 점유자 찾기 실패 " + com + ": " + e.GetType().Name + ": " + e.Message, "F1 holder lookup failed " + com + ": " + e.GetType().Name + ": " + e.Message)); }
                if (ConnectFlow.IsActive(com)) return;
                log.Write(L.T("F1 " + com + " held_by_other — " + who + " 사용 중(끝내지 않음, 찾기 " + mode + ")", "F1 " + com + " held_by_other — in use by " + who + " (not closed, lookup " + mode + ")"));
                Notice(L.T(com + " 사용 중: " + name + ". 허브가 열지 못했습니다. 다시 연결하려면 /connect-uart " + com, com + " is in use by " + name + ". The hub could not open it. To connect again: /connect-uart " + com));
            });
        }
    }
}
