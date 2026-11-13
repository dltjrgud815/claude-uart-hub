// 허브 설정(config.json). 사용자 전용 파일이며 코어와 감독이 같은 파서(Config.Validate)를 쓴다(§3.8).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UartHub
{
    public sealed class ConfigException : Exception
    {
        public IReadOnlyList<string> Errors { get; }
        public ConfigException(IReadOnlyList<string> errors) : base("config invalid: " + string.Join("; ", errors)) { Errors = errors; }
    }

    public sealed class CableConfig
    {
        public string Alias;
        public string Mode = "auto";      // auto | ignore
        public bool ModeSet;              // config.json 에 "mode" 키가 실제로 있음(C-3: alias 만 적은 항목은 모드 명시가 아님)
        public int Baud = 115200;
        public bool ReplugClear = true;   // 재연결 줄 지우기(§3.3). 셸이 아닌 장치로 옮겨 꽂는 케이블은 false
        public EventsConfig Events;       // 케이블별 사건 패턴 덧붙임(X6 부록 C.2, cables.<id>.events — 최상위 events 와 같은 모양). 없으면 null
    }

    /// <summary>사건 종류 하나의 패턴 조정(X6 부록 C.2): off = true(그 종류 기본 패턴 전부 끔) 또는 끌 기본 패턴 문자열 목록, add = 더할 정규식.</summary>
    public sealed class EventRule
    {
        public bool OffAll;
        public List<string> Off = new List<string>();
        public List<string> Add = new List<string>();
    }

    /// <summary>
    /// config events(최상위·cables.&lt;id&gt;.events): {"enabled": true|false, "patterns": {"&lt;종류&gt;": {"add": [정규식…], "off": true | [기본 패턴…]}}}
    /// + 최상위만 "max_mb"(events 폴더 크기 상한 — 넘으면 앞뒤 로그를 더 만들지 않음, 기본 2048)과
    /// "notify"(트레이 풍선을 띄울 종류, 기본 ["panic","oops","app_error","confirm"] — 재부팅은 기록만, 2026-11-06. panic·oops 가 cause 인 부팅은 그 종류로 알림).
    /// </summary>
    public sealed class EventsConfig
    {
        public bool Enabled = true;
        public double MaxMb = 2048;
        public List<string> Notify = new List<string>(EventKinds.NotifyDefault);
        public Dictionary<string, EventRule> Patterns = new Dictionary<string, EventRule>(StringComparer.Ordinal);
    }

    /// <summary>
    /// 모든 시간 상수는 여기서 주입한다. 테스트는 축소 값을 쓴다.
    /// *_mb 는 소수 허용(테스트에서 64KB = 0.0625).
    /// </summary>
    public sealed class Config
    {
        // ---- 경로·포트 (§3.9) ----
        public string LogRoot;                      // null → 허브 폴더의 부모(C-2, ResolveLogRoot). 고정 기본값(D:\) 없음. log_layout 2 면 ResolveLogRoot 뒤 <BaseRoot>\콘솔로그
        public bool LogRootSet;                     // config.json 에 log_root 가 명시됨
        public int LogLayout = 1;                   // 부록 C.11: 2 = 보기 쉬운 배치(콘솔로그·에이징이벤트·hub\events). 3 = 에이징 세션(부록 C.12 — 콘솔 기록은 <허브 폴더>\임시기록). 없음/1 = Phase A 배치 그대로
        public string BaseRoot;                     // ResolveLogRoot 가 정함: 설치 때 정한 log_root(기준 폴더, 트레이 "로그 폴더 열기"). 그 전엔 null
        public string HubDir;                       // null → <log_root>\hub
        public int BasePort = 7001;
        public string Language = L.Auto;              // "language": auto|ko|en — 화면·파일·메시지 언어(src/Lang.cs, 2026-11-13)
        public int ControlPort = 7200;               // 7000 은 다른 상주 서비스(wizmchecksvc 등)가 쓰는 PC 가 있어 피한다(실보드 시험 11-03)
        public List<string> Vids = new List<string> { "VID_0403" };
        public Dictionary<string, CableConfig> Cables = new Dictionary<string, CableConfig>(StringComparer.OrdinalIgnoreCase);

        // ---- 크기 ----
        public double RingMb = 32;
        public int RingChunks = 65536;              // 링 도착시각 메타 슬롯 수(최근 청크만 필요: 62B 패킷 기준 약 4MB 분량)
        public double RotateMb = 64;
        public double QueueMb = 256;                // 메모리 큐 상한(§3.1)
        public int ReadBufferBytes = 1 << 20;       // SerialPort.ReadBufferSize
        public int CompressAfterDays = 7;

        // ---- 정규식 ----
        public string PromptRe = @"root@([^:\s]+):\S*[#$] ";            // §3.2: 줄 시작이 아니어도 후보(정규화 뒤 \r 은 줄 시작)
        // 아래 셋의 기본값은 제품 프로필(UartHub.profile.json, ProductProfile)이 정한다 — 프로필이 없으면 일반 Linux 값.
        public string UbootPromptRe = ProductProfile.Current.UbootPromptRe;
        public string BootRe = ProductProfile.Current.BootRe;                // 부록 A
        public string LoaderMarkerRe = ProductProfile.Current.LoaderMarkerRe;  // 사건 감지(부록 C.7): 로더 표시 — 줄 어디에 있든 실시간 리셋(커널 링·pstore 에 안 들어가 다시 찍히지 않음). 빈 값 = 없음
        public string BoardRe = @"root@([^:\s]+):\S*[#$] ";                          // 기록 코어 최소 매처

        // ---- 기록 코어 시간 ----
        public int ScanIntervalMs = 2000;           // WMI 폴링 주기
        public int ReadTimeoutMs = 200;
        public int WriterWaitMs = 50;               // writer 큐 대기 상한(tail flush·하트비트 판정 주기)
        public int TailFlushMs = 400;               // 개행 없는 꼬리 기록(구 브리지 규칙)
        public int GapMs = 20;                      // .idx 희소 간격 기록 임계값
        public double RenameWindowS = 60;           // 보드명 최초 감지 rename 허용 창
        public int BootMergeMs = 5000;              // 같은 부팅의 경계 매치 병합 창(첫 매치 기준)
        public double HeartbeatS = 10;              // alive/<port> 갱신 주기

        // ---- 서비스 층 시간 ----
        public int TOpenMs = 0;                     // 0 → max(50, 2×LatencyTimer+20)
        public double IdleS = 3;
        public int QuietMs = 2000;                  // probe/nudge RX 무음 조건
        public double DefaultTimeoutS = 30;
        public double PushTimeoutS = 120;
        public double TxTtlS = 60;
        public int StrandedMs = 1500;
        public int EscGapMs = 150;
        public int TelnetNegotiationMs = 2000;
        public int TelnetBindRetryMs = 5000;        // telnet bind 실패(bind_failed) 뒤 재시도 주기
        public double ClientQueueMb = 1;
        public int SpoolPollMs = 200;               // live\*.tx 감시 주기
        public int SpoolQuietMs = 300;              // .tx 가 이만큼 조용해야 rename(WSL DrvFs `>>` 의 open 과 겹치면 ENOENT, SpoolWatcher 주석)
        public int StatusIntervalMs = 2000;         // live\<port|COMx>.status 기록 주기(구 브리지 2초)
        public double HangS = 60;                   // 감독 hang 판정: alive 하트비트·ControlServer ping 이 이만큼 끊기면 kill 후 재시작(§3.8)
        public double PingIntervalS = 10;           // 감독 → ControlServer ping(hello + hub status) 주기
        public double RestoreWindowS = 60;
        public int RestoreTailBytes = 65536;        // 상태 복원 재생 길이(.raw 끝)
        public int[] BackoffS = { 5, 10, 20, 40, 60 };
        public double BackoffResetS = 600;          // 코어가 이만큼 살아 있으면 백오프·크래시 루프 초기화(10분 정상 운용)
        public int CrashLoopCount = 3;              // 연속 crash_loop_count 회 …
        public double CrashLoopWindowS = 60;        // … 각각 기동 뒤 이 시간 안에 죽으면 크래시 루프 경고(§3.8)
        public double SupWatchS = 30;               // 운영 모드 코어의 감독 생존 확인 주기(C-11)

        // ---- 사람 접속 ----
        public bool AskNewCables;                   // 처음 보는 케이블을 잡기 전에 물을지(§2.3 n). 코드 기본값 false = Phase A 동작
        public List<string> HolderScanSkip = new List<string>();   // 점유자 찾기(X5)가 열지 않을 프로세스 이름(끝 * = 앞부분 일치) — 사내 보안·DRM 에이전트 등
        public bool AutoNudge = true;               // 부록 C.13: 재부팅으로 사라진 run 의 잠금 풀기 + Claude 가 기다릴 때 재부팅 뒤 CR 로 프롬프트 깨우기. false = C.13 전 동작 그대로
        public double AutoNudgeGapS = 10;           // 자동 깨우기 CR 사이 최소 간격(부팅마다 3번까지)
        public bool OnDemand;                       // 필요할 때만 연결(X5 부록 B.4): 판정 없는 케이블은 Pending(COM 미개방)이고 첫 연결 창·풍선 없음 — /connect-uart 로만 붙는다. 없음/false = 지금 동작

        // ---- 임시 기록 보관(log_layout 3, 부록 C.12): 닫힌 파일 중 이 시간이 지난 것·합계 상한을 넘는 오래된 것을 지운다 ----
        public double SpoolKeepHours = 24;
        public double SpoolMaxMb = 2048;

        // ---- 에이징 사건 감지(X6 부록 C.2) ----
        public EventsConfig Events = new EventsConfig();   // events.patterns: 기본 패턴 끄기·더하기(모든 케이블). 케이블별은 cables.<id>.events

        public string EffectiveHubDir => !string.IsNullOrEmpty(HubDir) ? HubDir : string.IsNullOrEmpty(BaseRoot ?? LogRoot) ? null : Path.Combine(BaseRoot ?? LogRoot, "hub");

        /// <summary>log_layout 2 의 폴더 이름(부록 C.11): 콘솔 로그(동결 DiskWriter·SpoolWatcher 가 cfg.LogRoot 로 씀)·사람이 보는 에이징 이벤트 txt.</summary>
        public const string ConsoleDirName = "콘솔로그", EventsTxtDirName = "에이징이벤트";
        /// <summary>
        /// log_layout 3 의 폴더 이름(부록 C.12) — 언어마다(계획 §4): 숨김 임시 기록(&lt;허브 폴더&gt;\임시기록 | spool = cfg.LogRoot)·에이징 세션 폴더의 뿌리(&lt;기준&gt;\에이징 | aging).
        /// lang null = 지금 언어.
        /// </summary>
        public static string SpoolDirNameFor(string lang) => AgingLang.T(lang, "임시기록", "spool");
        public static string AgingDirNameFor(string lang) => AgingLang.T(lang, "에이징", "aging");
        /// <summary>지금 언어의 이름(새로 만들 때). 이미 있는 폴더는 SpoolDirFor·AgingRoots 로 찾는다.</summary>
        public static string SpoolDirName => SpoolDirNameFor(null);
        public static string AgingDirName => AgingDirNameFor(null);
        /// <summary>두 언어의 이름(지금 언어 먼저) — 지우기·숨기기·목록·보호는 둘 다 안다(언어를 바꿔도 옛 폴더가 남아 있다).</summary>
        public static string[] SpoolDirNames => new[] { SpoolDirName, SpoolDirNameFor(L.IsEn ? L.Ko : L.En) };
        public static string[] AgingDirNames => new[] { AgingDirName, AgingDirNameFor(L.IsEn ? L.Ko : L.En) };

        /// <summary>
        /// 허브 폴더의 임시 기록 폴더: 이미 있는 것(임시기록 → spool 순)을 그대로 쓰고, 둘 다 없으면 지금 언어 이름.
        /// 한 번 생기면 언어를 바꿔도 같은 폴더 — 동결 DiskWriter·SpoolWatcher·CLI(logpath·log)·설치기가 프로세스마다 언어가 달라도 같은 곳을 본다.
        /// </summary>
        public static string SpoolDirFor(string hubDir)
        {
            foreach (var lang in new[] { L.Ko, L.En })
            {
                string d = Path.Combine(hubDir ?? "", SpoolDirNameFor(lang));
                try { if (Directory.Exists(d)) return d; } catch (Exception) { }
            }
            return Path.Combine(hubDir ?? "", SpoolDirName);
        }

        /// <summary>새 에이징 세션 폴더의 뿌리(&lt;기준 폴더&gt;\에이징 | aging — 지금 언어, 부록 C.12).</summary>
        public string AgingRoot => Path.Combine(BaseRoot ?? LogRoot ?? "", AgingDirName);
        /// <summary>두 언어의 에이징 뿌리(지금 언어 먼저) — 목록·열기 허용·보호·예산은 둘 다 본다.</summary>
        public string[] AgingRoots => AgingDirNames.Select(n => Path.Combine(BaseRoot ?? LogRoot ?? "", n)).ToArray();

        /// <summary>
        /// log_layout 3 의 임시 기록 폴더들: LogRoot 먼저, 그 옆의 다른 언어 이름 폴더(언어를 바꾸기 전에 생긴 옛 폴더 — 있을 때만 숨기고 정리한다).
        /// 그 밖의 배치면 LogRoot 하나.
        /// </summary>
        public string[] SpoolDirs
        {
            get
            {
                if (string.IsNullOrEmpty(LogRoot)) return new string[0];
                var l = new List<string> { LogRoot };
                string trimmed = LogRoot.TrimEnd('\\', '/'), leaf = Path.GetFileName(trimmed), parent = Path.GetDirectoryName(trimmed);
                if (LogLayout >= 3 && !string.IsNullOrEmpty(parent) && SpoolDirNames.Contains(leaf, StringComparer.OrdinalIgnoreCase))
                    foreach (var n in SpoolDirNames) if (!string.Equals(n, leaf, StringComparison.OrdinalIgnoreCase)) l.Add(Path.Combine(parent, n));
                return l.ToArray();
            }
        }

        /// <summary>사건 jsonl 폴더(내부 기록, Claude·aging 용): layout 2 = &lt;허브 폴더&gt;\events, 아니면 &lt;log_root&gt;\events.</summary>
        public string EventsJsonlDir => LogLayout >= 2 ? Path.Combine(EffectiveHubDir ?? "", "events") : Path.Combine(LogRoot ?? "", "events");

        /// <summary>
        /// 사람이 보는 에이징 이벤트 txt 의 뿌리: layout 3 = &lt;기준 폴더&gt;\에이징(세션 폴더마다 이벤트\), layout 2 = &lt;기준 폴더&gt;\에이징이벤트(아래 &lt;yyyyMMdd&gt;),
        /// 아니면 jsonl 과 같은 &lt;log_root&gt;\events.
        /// </summary>
        public string EventsTxtDir => LogLayout >= 3 ? AgingRoot : LogLayout >= 2 ? Path.Combine(BaseRoot ?? LogRoot ?? "", EventsTxtDirName) : Path.Combine(LogRoot ?? "", "events");
        /// <summary>사람이 보는 이벤트 txt 가 있을 수 있는 뿌리 모두: layout 3 = 두 언어의 에이징 뿌리(지금 언어 먼저), 아니면 EventsTxtDir 하나.</summary>
        public string[] EventsTxtDirs => LogLayout >= 3 ? AgingRoots : new[] { EventsTxtDir };
        public long RingBytes => MbToBytes(RingMb);
        public long RotateBytes => MbToBytes(RotateMb);
        public long QueueBytes => MbToBytes(QueueMb);
        public long ClientQueueBytes => MbToBytes(ClientQueueMb);

        static long MbToBytes(double mb) => (long)Math.Round(mb * 1024 * 1024);

        public static Config Default() => new Config();

        /// <summary>
        /// 경로를 푸는 한 곳(코어·감독·CLI 가 허브 폴더를 정한 뒤 호출, 여러 번 불러도 같음). C-2: log_root 가 명시되지 않았으면 허브 폴더의 부모.
        /// 그 값이 BaseRoot(기준 폴더)이고, log_layout 2 면 LogRoot = BaseRoot\콘솔로그(부록 C.11), log_layout 3 이면 LogRoot = &lt;허브 폴더&gt;\임시기록(부록 C.12 —
        /// 동결 DiskWriter·SpoolWatcher 는 그대로 이 폴더에 쓴다), 아니면 LogRoot = BaseRoot.
        /// </summary>
        public void ResolveLogRoot(string hubDir)
        {
            if (BaseRoot != null) return;
            if (string.IsNullOrEmpty(LogRoot) && !string.IsNullOrEmpty(hubDir)) LogRoot = Path.GetDirectoryName(Path.GetFullPath(hubDir).TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(LogRoot)) return;
            BaseRoot = LogRoot;
            if (LogLayout >= 3)
            {
                string hd = !string.IsNullOrEmpty(HubDir) ? HubDir : !string.IsNullOrEmpty(hubDir) ? hubDir : Path.Combine(BaseRoot, "hub");
                LogRoot = SpoolDirFor(hd);
            }
            else if (LogLayout >= 2) LogRoot = Path.Combine(BaseRoot, ConsoleDirName);
        }

        public CableConfig CableFor(string cableId)
        {
            if (cableId != null && Cables.TryGetValue(cableId, out var cc)) return cc;
            return new CableConfig();
        }

        /// <summary>T_open = max(50, 2 × FTDI LatencyTimer + 20) (§3.2). 설정값이 있으면 그것.</summary>
        public int EffectiveTOpenMs(int latencyTimerMs = 16) => TOpenMs > 0 ? TOpenMs : Math.Max(50, 2 * latencyTimerMs + 20);

        // ------------------------------------------------------------------ 파싱
        public static Config Load(string path)
        {
            var cfg = Parse(File.ReadAllText(path));
            cfg.ValidateOrThrow();
            return cfg;
        }

        /// <summary>감독이 코어 기동 전에 호출: 오류 목록(비면 정상).</summary>
        public static List<string> ValidateFile(string path)
        {
            try { return Parse(File.ReadAllText(path)).Validate(); }
            catch (ConfigException e) { return new List<string>(e.Errors); }
            catch (Exception e) { return new List<string> { e.GetType().Name + ": " + e.Message }; }
        }

        public static Config Parse(string json)
        {
            var errs = new List<string>();
            var cfg = new Config();
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            }
            catch (JsonException e) { throw new ConfigException(new[] { L.T("JSON 파싱 실패: " + e.Message, "JSON parse failed: " + e.Message) }); }
            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new ConfigException(new[] { L.T("최상위가 객체가 아님", "top level is not an object") });
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    try { cfg.Apply(p, errs); }
                    catch (Exception e) when (e is InvalidOperationException || e is FormatException)
                    {
                        errs.Add(L.T(p.Name + ": 형식 오류(" + e.Message + ")", p.Name + ": bad format (" + e.Message + ")"));
                    }
                }
            }
            if (errs.Count > 0) throw new ConfigException(errs);
            return cfg;
        }

        void Apply(JsonProperty p, List<string> errs)
        {
            var v = p.Value;
            switch (p.Name)
            {
                case "log_root": LogRoot = v.GetString(); LogRootSet = true; break;
                case "hub_dir": HubDir = v.GetString(); break;
                case "log_layout": LogLayout = Int(v); break;
                case "base_port": BasePort = Int(v); break;
                case "control_port": ControlPort = Int(v); break;
                case "language": Language = v.GetString()?.Trim().ToLowerInvariant(); break;   // "EN"·" en " 도 받는다(L 과 같은 규칙)
                case "vids":
                    Vids = new List<string>();
                    foreach (var e in v.EnumerateArray()) Vids.Add(e.GetString());
                    break;
                case "cables":
                    Cables = new Dictionary<string, CableConfig>(StringComparer.OrdinalIgnoreCase);
                    foreach (var c in v.EnumerateObject())
                    {
                        var cc = new CableConfig();
                        foreach (var f in c.Value.EnumerateObject())
                        {
                            switch (f.Name)
                            {
                                case "alias": cc.Alias = f.Value.GetString(); break;
                                case "mode": cc.Mode = f.Value.GetString(); cc.ModeSet = true; break;
                                case "baud": cc.Baud = Int(f.Value); break;
                                case "replug_clear":
                                    if (f.Value.ValueKind == JsonValueKind.True || f.Value.ValueKind == JsonValueKind.False) cc.ReplugClear = f.Value.GetBoolean();
                                    else errs.Add(L.T("cables." + c.Name + ".replug_clear 는 true/false", "cables." + c.Name + ".replug_clear must be true/false"));
                                    break;
                                case "events": cc.Events = ParseEvents(f.Value, "cables." + c.Name + ".events", errs, false); break;
                                default: errs.Add(L.T("cables." + c.Name + ": 알 수 없는 키 '" + f.Name + "'", "cables." + c.Name + ": unknown key '" + f.Name + "'")); break;
                            }
                        }
                        Cables[c.Name] = cc;
                    }
                    break;
                case "ring_mb": RingMb = v.GetDouble(); break;
                case "ring_chunks": RingChunks = Int(v); break;
                case "rotate_mb": RotateMb = v.GetDouble(); break;
                case "queue_mb": QueueMb = v.GetDouble(); break;
                case "read_buffer_bytes": ReadBufferBytes = Int(v); break;
                case "compress_after_days": CompressAfterDays = Int(v); break;
                case "spool_keep_hours": SpoolKeepHours = v.GetDouble(); break;
                case "spool_max_mb": SpoolMaxMb = v.GetDouble(); break;
                case "prompt_re": PromptRe = v.GetString(); break;
                case "uboot_prompt_re": UbootPromptRe = v.GetString(); break;
                case "boot_re": BootRe = v.GetString(); break;
                case "loader_marker_re": LoaderMarkerRe = v.GetString(); break;
                case "board_re": BoardRe = v.GetString(); break;
                case "scan_interval_ms": ScanIntervalMs = Int(v); break;
                case "read_timeout_ms": ReadTimeoutMs = Int(v); break;
                case "writer_wait_ms": WriterWaitMs = Int(v); break;
                case "tail_flush_ms": TailFlushMs = Int(v); break;
                case "gap_ms": GapMs = Int(v); break;
                case "rename_window_s": RenameWindowS = v.GetDouble(); break;
                case "boot_merge_ms": BootMergeMs = Int(v); break;
                case "heartbeat_s": HeartbeatS = v.GetDouble(); break;
                case "t_open_ms": TOpenMs = Int(v); break;
                case "idle_s": IdleS = v.GetDouble(); break;
                case "quiet_ms": QuietMs = Int(v); break;
                case "default_timeout_s": DefaultTimeoutS = v.GetDouble(); break;
                case "push_timeout_s": PushTimeoutS = v.GetDouble(); break;
                case "tx_ttl_s": TxTtlS = v.GetDouble(); break;
                case "stranded_ms": StrandedMs = Int(v); break;
                case "esc_gap_ms": EscGapMs = Int(v); break;
                case "telnet_negotiation_ms": TelnetNegotiationMs = Int(v); break;
                case "telnet_bind_retry_ms": TelnetBindRetryMs = Int(v); break;
                case "spool_poll_ms": SpoolPollMs = Int(v); break;
                case "spool_quiet_ms": SpoolQuietMs = Int(v); break;
                case "status_interval_ms": StatusIntervalMs = Int(v); break;
                case "client_queue_mb": ClientQueueMb = v.GetDouble(); break;
                case "hang_s": HangS = v.GetDouble(); break;
                case "ping_interval_s": PingIntervalS = v.GetDouble(); break;
                case "restore_window_s": RestoreWindowS = v.GetDouble(); break;
                case "restore_tail_bytes": RestoreTailBytes = Int(v); break;
                case "backoff_s":
                    var l = new List<int>();
                    foreach (var e in v.EnumerateArray()) l.Add(Int(e));
                    BackoffS = l.ToArray();
                    break;
                case "backoff_reset_s": BackoffResetS = v.GetDouble(); break;
                case "crash_loop_count": CrashLoopCount = Int(v); break;
                case "crash_loop_window_s": CrashLoopWindowS = v.GetDouble(); break;
                case "sup_watch_s": SupWatchS = v.GetDouble(); break;
                case "ask_new_cables":
                    if (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) AskNewCables = v.GetBoolean();
                    else errs.Add(L.T("ask_new_cables 는 true/false", "ask_new_cables must be true/false"));
                    break;
                case "holder_scan_skip":
                    HolderScanSkip = new List<string>();
                    foreach (var e in v.EnumerateArray()) HolderScanSkip.Add(e.GetString());
                    break;
                case "auto_nudge":
                    if (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) AutoNudge = v.GetBoolean();
                    else errs.Add(L.T("auto_nudge 는 true/false", "auto_nudge must be true/false"));
                    break;
                case "auto_nudge_gap_s": AutoNudgeGapS = v.GetDouble(); break;
                case "on_demand":
                    if (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) OnDemand = v.GetBoolean();
                    else errs.Add(L.T("on_demand 는 true/false", "on_demand must be true/false"));
                    break;
                case "events": Events = ParseEvents(v, "events", errs, true) ?? new EventsConfig(); break;
                default: errs.Add(L.T("알 수 없는 키 '" + p.Name + "'", "unknown key '" + p.Name + "'")); break;
            }
        }

        /// <summary>
        /// events 객체(X6 부록 C.2): {"patterns": {"panic"|"oops"|"bug"|"oom"|"app_error": {"add": [정규식…], "off": true | [기본 패턴 문자열…]}}}.
        /// 모양 오류(모르는 키·종류, 타입)는 여기서, 정규식·off 문자열 확인은 Validate 에서. 오류면 errs 에 더하고 읽은 만큼 돌려준다.
        /// </summary>
        static EventsConfig ParseEvents(JsonElement v, string where, List<string> errs, bool top)
        {
            var ec = new EventsConfig();
            if (v.ValueKind != JsonValueKind.Object) { errs.Add(L.T(where + " 는 객체({\"patterns\": {…}})", where + " must be an object ({\"patterns\": {…}})")); return ec; }
            foreach (var p in v.EnumerateObject())
            {
                if (p.Name == "enabled")
                {
                    if (p.Value.ValueKind == JsonValueKind.True || p.Value.ValueKind == JsonValueKind.False) ec.Enabled = p.Value.GetBoolean();
                    else errs.Add(L.T(where + ".enabled 는 true/false", where + ".enabled must be true/false"));
                    continue;
                }
                if (top && p.Name == "max_mb")
                {
                    if (p.Value.ValueKind == JsonValueKind.Number && p.Value.GetDouble() > 0) ec.MaxMb = p.Value.GetDouble();
                    else errs.Add(L.T(where + ".max_mb 는 양수(MB)", where + ".max_mb must be a positive number (MB)"));
                    continue;
                }
                if (top && p.Name == "notify")
                {
                    // 알릴 종류 배열(빈 배열 = 풍선 없음). 값은 CLI --kind 와 같은 이름(boot:soft·boot:hard 처럼 부팅 class 도)
                    if (p.Value.ValueKind != JsonValueKind.Array) { errs.Add(L.T(where + ".notify 는 종류 문자열 배열(예: [\"panic\",\"oops\",\"app_error\",\"confirm\"])", where + ".notify must be an array of kind strings (e.g. [\"panic\",\"oops\",\"app_error\",\"confirm\"])")); continue; }
                    var n = new List<string>();
                    foreach (var e in p.Value.EnumerateArray())
                    {
                        if (e.ValueKind == JsonValueKind.String && EventKinds.IsKindFilter(e.GetString())) { if (!n.Contains(e.GetString())) n.Add(e.GetString()); }
                        else errs.Add(L.T(where + ".notify: 알 수 없는 종류 " + (e.ValueKind == JsonValueKind.String ? "'" + e.GetString() + "'" : e.ValueKind.ToString())
                                          + "(" + string.Join("|", EventKinds.All) + "|boot:cmd|boot:soft|boot:hard)",
                                          where + ".notify: unknown kind " + (e.ValueKind == JsonValueKind.String ? "'" + e.GetString() + "'" : e.ValueKind.ToString())
                                          + " (" + string.Join("|", EventKinds.All) + "|boot:cmd|boot:soft|boot:hard)"));
                    }
                    ec.Notify = n;
                    continue;
                }
                if (p.Name != "patterns") { errs.Add(L.T(where + ": 알 수 없는 키 '" + p.Name + "'(" + (top ? "enabled|max_mb|notify|patterns" : "enabled|patterns") + ")",
                                                          where + ": unknown key '" + p.Name + "' (" + (top ? "enabled|max_mb|notify|patterns" : "enabled|patterns") + ")")); continue; }
                if (p.Value.ValueKind != JsonValueKind.Object) { errs.Add(L.T(where + ".patterns 는 객체(종류 → {add, off})", where + ".patterns must be an object (kind → {add, off})")); continue; }
                foreach (var k in p.Value.EnumerateObject())
                {
                    string w = where + ".patterns." + k.Name;
                    if (Array.IndexOf(EventKinds.Errors, k.Name) < 0) { errs.Add(L.T(where + ".patterns: 알 수 없는 종류 '" + k.Name + "'(" + string.Join("|", EventKinds.Errors) + ")", where + ".patterns: unknown kind '" + k.Name + "' (" + string.Join("|", EventKinds.Errors) + ")")); continue; }
                    if (k.Value.ValueKind != JsonValueKind.Object) { errs.Add(L.T(w + " 는 객체({\"add\": […], \"off\": true | […]})", w + " must be an object ({\"add\": […], \"off\": true | […]})")); continue; }
                    var r = new EventRule();
                    foreach (var f in k.Value.EnumerateObject())
                    {
                        if (f.Name == "add")
                        {
                            if (f.Value.ValueKind != JsonValueKind.Array) { errs.Add(L.T(w + ".add 는 정규식 문자열 배열", w + ".add must be an array of regex strings")); continue; }
                            foreach (var e in f.Value.EnumerateArray())
                                if (e.ValueKind == JsonValueKind.String) r.Add.Add(e.GetString()); else errs.Add(L.T(w + ".add 는 정규식 문자열 배열", w + ".add must be an array of regex strings"));
                        }
                        else if (f.Name == "off")
                        {
                            if (f.Value.ValueKind == JsonValueKind.True) r.OffAll = true;
                            else if (f.Value.ValueKind == JsonValueKind.False) r.OffAll = false;
                            else if (f.Value.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var e in f.Value.EnumerateArray())
                                    if (e.ValueKind == JsonValueKind.String) r.Off.Add(e.GetString()); else errs.Add(L.T(w + ".off 는 true 또는 기본 패턴 문자열 배열", w + ".off must be true or an array of default pattern strings"));
                            }
                            else errs.Add(L.T(w + ".off 는 true 또는 기본 패턴 문자열 배열", w + ".off must be true or an array of default pattern strings"));
                        }
                        else errs.Add(L.T(w + ": 알 수 없는 키 '" + f.Name + "'(add|off)", w + ": unknown key '" + f.Name + "' (add|off)"));
                    }
                    ec.Patterns[k.Name] = r;
                }
            }
            return ec;
        }

        /// <summary>events 의 정규식(add)·끌 기본 패턴(off) 확인.</summary>
        static void ValidateEvents(List<string> e, EventsConfig ec, string where)
        {
            if (ec == null) return;
            foreach (var kv in ec.Patterns)
            {
                string w = where + ".patterns." + kv.Key;
                foreach (var a in kv.Value.Add) Re(e, w + ".add", a);
                foreach (var o in kv.Value.Off)
                    if (!EventKinds.Defaults.TryGetValue(kv.Key, out var d) || Array.IndexOf(d, o) < 0)
                        e.Add(L.T(w + ".off: 기본 패턴에 없음 '" + o + "' (기본: " + string.Join(" · ", EventKinds.Defaults[kv.Key]) + ")",
                                  w + ".off: not a default pattern '" + o + "' (defaults: " + string.Join(" · ", EventKinds.Defaults[kv.Key]) + ")"));
            }
        }

        static int Int(JsonElement v)
        {
            double d = v.GetDouble();
            if (d != Math.Floor(d) || d > int.MaxValue || d < int.MinValue) throw new FormatException(L.T("정수가 아님: " + d.ToString(CultureInfo.InvariantCulture), "not an integer: " + d.ToString(CultureInfo.InvariantCulture)));
            return (int)d;
        }

        // ------------------------------------------------------------------ 검증
        public List<string> Validate()
        {
            var e = new List<string>();
            if (LogRootSet && string.IsNullOrWhiteSpace(LogRoot)) e.Add(L.T("log_root 비어 있음", "log_root is empty"));
            if (LogLayout < 1 || LogLayout > 3) e.Add(L.T("log_layout 은 1·2·3: " + LogLayout, "log_layout must be 1, 2 or 3: " + LogLayout));
            Pos(e, "spool_keep_hours", SpoolKeepHours); Pos(e, "spool_max_mb", SpoolMaxMb); Pos(e, "auto_nudge_gap_s", AutoNudgeGapS);
            if (BasePort < 1024 || BasePort > 65000) e.Add(L.T("base_port 범위(1024~65000) 밖: " + BasePort, "base_port out of range (1024~65000): " + BasePort));
            if (ControlPort < 1024 || ControlPort > 65535) e.Add(L.T("control_port 범위 밖: " + ControlPort, "control_port out of range: " + ControlPort));
            if (!L.IsValidSetting(Language)) e.Add(L.T("language 는 auto·ko·en: " + Language, "language must be auto, ko or en: " + Language));
            if (ControlPort >= BasePort && ControlPort < BasePort + 100) e.Add(L.T("control_port 가 케이블 포트 범위(base_port~+99)와 겹침", "control_port overlaps the cable port range (base_port~+99)"));
            Pos(e, "ring_mb", RingMb); Pos(e, "rotate_mb", RotateMb); Pos(e, "queue_mb", QueueMb); Pos(e, "client_queue_mb", ClientQueueMb);
            if (RingChunks < 16) e.Add(L.T("ring_chunks 너무 작음", "ring_chunks too small"));
            if (ReadBufferBytes < 4096) e.Add(L.T("read_buffer_bytes 너무 작음", "read_buffer_bytes too small"));
            Pos(e, "scan_interval_ms", ScanIntervalMs); Pos(e, "read_timeout_ms", ReadTimeoutMs); Pos(e, "writer_wait_ms", WriterWaitMs);
            Pos(e, "tail_flush_ms", TailFlushMs); Pos(e, "gap_ms", GapMs); Pos(e, "rename_window_s", RenameWindowS);
            Pos(e, "boot_merge_ms", BootMergeMs); Pos(e, "heartbeat_s", HeartbeatS);
            Pos(e, "idle_s", IdleS); Pos(e, "quiet_ms", QuietMs); Pos(e, "default_timeout_s", DefaultTimeoutS); Pos(e, "push_timeout_s", PushTimeoutS);
            Pos(e, "tx_ttl_s", TxTtlS); Pos(e, "stranded_ms", StrandedMs); Pos(e, "esc_gap_ms", EscGapMs); Pos(e, "telnet_negotiation_ms", TelnetNegotiationMs);
            Pos(e, "telnet_bind_retry_ms", TelnetBindRetryMs); Pos(e, "spool_poll_ms", SpoolPollMs); Pos(e, "spool_quiet_ms", SpoolQuietMs); Pos(e, "status_interval_ms", StatusIntervalMs);
            Pos(e, "hang_s", HangS); Pos(e, "ping_interval_s", PingIntervalS); Pos(e, "restore_window_s", RestoreWindowS); Pos(e, "backoff_reset_s", BackoffResetS);
            Pos(e, "crash_loop_count", CrashLoopCount); Pos(e, "crash_loop_window_s", CrashLoopWindowS); Pos(e, "sup_watch_s", SupWatchS);
            if (TOpenMs < 0) e.Add(L.T("t_open_ms 음수", "t_open_ms is negative"));
            if (RestoreTailBytes < 1024) e.Add(L.T("restore_tail_bytes 너무 작음", "restore_tail_bytes too small"));
            if (CompressAfterDays < 1) e.Add("compress_after_days < 1");
            if (BackoffS == null || BackoffS.Length == 0) e.Add(L.T("backoff_s 비어 있음", "backoff_s is empty"));
            else foreach (int b in BackoffS) if (b <= 0) { e.Add(L.T("backoff_s 값은 양수", "backoff_s values must be positive")); break; }
            Re(e, "prompt_re", PromptRe); Re(e, "uboot_prompt_re", UbootPromptRe); Re(e, "boot_re", BootRe); if (!string.IsNullOrEmpty(LoaderMarkerRe)) Re(e, "loader_marker_re", LoaderMarkerRe); Re(e, "board_re", BoardRe);
            if (Vids == null) e.Add("vids null");
            foreach (var kv in Cables)
            {
                if (kv.Value.Mode != "auto" && kv.Value.Mode != "ignore") e.Add(L.T("cables." + kv.Key + ".mode 는 auto|ignore: " + kv.Value.Mode, "cables." + kv.Key + ".mode must be auto|ignore: " + kv.Value.Mode));
                if (kv.Value.Baud <= 0) e.Add(L.T("cables." + kv.Key + ".baud 는 양수", "cables." + kv.Key + ".baud must be positive"));
                ValidateEvents(e, kv.Value.Events, "cables." + kv.Key + ".events");
            }
            ValidateEvents(e, Events, "events");
            return e;
        }

        public void ValidateOrThrow()
        {
            var e = Validate();
            if (e.Count > 0) throw new ConfigException(e);
        }

        // ------------------------------------------------------------------ on_demand 더하기(X5 부록 B.4, C-12)
        static readonly JsonDocumentOptions JsoncOptions = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

        /// <summary>
        /// 휴대용 시작 때 기존 config.json 에 "on_demand" 키가 없으면 <c>"on_demand": true</c> 를 여는 중괄호 바로 뒤에 글자로 끼워 넣은 새 텍스트를 돌려준다
        /// (다른 키·주석·순서·줄 끝 그대로). 키가 이미 있거나(true·false 모두), JSON 이 아니거나, 넣은 뒤 다시 읽어 다른 키가 하나라도 달라지면 null 과 이유.
        /// </summary>
        public static string WithOnDemand(string text, out string why) => WithTopKey(text, "on_demand", "true", out why);

        /// <summary>새 등록·setup·휴대용 시작이 config.json 에 보장하는 log_layout(부록 C.12 = 3).</summary>
        public const int CurrentLogLayout = 3;

        /// <summary>
        /// 부록 C.11·C.12: config.json 에 "log_layout" 키가 없으면 <c>"log_layout": 3</c> 을 WithOnDemand 와 같은 방식으로 끼우고, 값이 2 면 그 글자만 3 으로 바꾼다
        /// (C-12 글자 편집 — 주석·순서·줄 끝 그대로, 다시 읽어 다른 키가 그대로인지 확인). 1 은 사용자 선택으로 보고 그대로 둔다(null + 이유).
        /// </summary>
        public static string WithLogLayout(string text, out string why)
        {
            string raw = CurrentLogLayout.ToString(CultureInfo.InvariantCulture);
            string n = WithTopKey(text, "log_layout", raw, out why);
            if (n != null || text == null) return n;
            List<KeyValuePair<string, string>> before;
            string cur;
            try
            {
                using var doc = JsonDocument.Parse(text.TrimStart('\uFEFF'), JsoncOptions);
                if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("log_layout", out var lv)) return null;
                cur = lv.GetRawText();
                before = doc.RootElement.EnumerateObject().Where(p => p.Name != "log_layout").Select(p => new KeyValuePair<string, string>(p.Name, p.Value.GetRawText())).ToList();
            }
            catch (JsonException) { return null; }
            if (cur != "2") { why = L.T("log_layout 이미 있음(" + cur + ") — 그대로 둠", "log_layout already set (" + cur + ") — left as is"); return null; }
            var ms = Regex.Matches(text, @"(""log_layout""\s*:\s*)2(?=\s*[,}\r\n/])", RegexOptions.CultureInvariant);
            if (ms.Count != 1) { why = L.T("log_layout 2 를 글자로 찾지 못함 — 그대로 둠", "could not find log_layout 2 in the text — left as is"); return null; }
            var m = ms[0];
            n = text.Substring(0, m.Index) + m.Groups[1].Value + raw + text.Substring(m.Index + m.Length);
            try
            {
                using var doc = JsonDocument.Parse(n.TrimStart('\uFEFF'), JsoncOptions);
                var after = doc.RootElement.EnumerateObject().Where(p => p.Name != "log_layout").Select(p => new KeyValuePair<string, string>(p.Name, p.Value.GetRawText())).ToList();
                bool same = after.Count == before.Count && after.Zip(before, (a, b) => a.Key == b.Key && a.Value == b.Value).All(x => x);
                if (!same || doc.RootElement.GetProperty("log_layout").GetRawText() != raw) { why = L.T("바꾼 뒤 검증 실패(다른 키가 달라짐) — 그대로 둠", "check after the change failed (another key changed) — left as is"); return null; }
            }
            catch (JsonException e) { why = L.T("바꾼 뒤 파싱 실패 — 그대로 둠(" + e.Message + ")", "parse after the change failed — left as is (" + e.Message + ")"); return null; }
            why = null;
            return n;
        }

        /// <summary>최상위에 key 가 없으면 <c>"key": raw</c> 를 여는 중괄호 바로 뒤에 글자로 끼운다(WithOnDemand·WithLogLayout). 다시 읽어 그 값과 다른 키가 그대로인지 확인.</summary>
        static string WithTopKey(string text, string key, string raw, out string why)
        {
            why = null;
            if (text == null) { why = L.T("읽지 못함 — 그대로 둠", "could not read — left as is"); return null; }
            List<KeyValuePair<string, string>> before;
            static string NoBom(string t) => t.Length > 0 && t[0] == '\uFEFF' ? t.Substring(1) : t;   // 문자열 파서는 BOM 을 받지 않는다(출력에는 그대로 둔다)
            try
            {
                using var doc = JsonDocument.Parse(NoBom(text), JsoncOptions);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) { why = L.T("최상위가 객체가 아님 — 그대로 둠", "top level is not an object — left as is"); return null; }
                if (doc.RootElement.TryGetProperty(key, out var od)) { why = L.T(key + " 이미 있음(" + od.GetRawText() + ") — 그대로 둠", key + " already set (" + od.GetRawText() + ") — left as is"); return null; }
                before = new List<KeyValuePair<string, string>>();
                foreach (var p in doc.RootElement.EnumerateObject()) before.Add(new KeyValuePair<string, string>(p.Name, p.Value.GetRawText()));
            }
            catch (JsonException e) { why = L.T("JSON 파싱 실패 — 그대로 둠(" + e.Message + ")", "JSON parse failed — left as is (" + e.Message + ")"); return null; }
            int i = OpeningBrace(text);
            if (i < 0) { why = L.T("여는 중괄호를 찾지 못함 — 그대로 둠", "could not find the opening brace — left as is"); return null; }
            string nl = i + 1 < text.Length && text[i + 1] == '\r' ? "\r\n" : "\n";
            bool lineAfter = i + 1 < text.Length && (text[i + 1] == '\n' || text[i + 1] == '\r');
            string kv = Json.Str(key) + ": " + raw;
            string ins = before.Count == 0 ? kv : lineAfter ? nl + "  " + kv + "," : " " + kv + ",";
            string n = text.Substring(0, i + 1) + ins + text.Substring(i + 1);
            try
            {
                using var doc = JsonDocument.Parse(NoBom(n), JsoncOptions);
                var after = new List<KeyValuePair<string, string>>();
                bool on = false;
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (p.Name == key) { on = p.Value.GetRawText() == raw; continue; }
                    after.Add(new KeyValuePair<string, string>(p.Name, p.Value.GetRawText()));
                }
                bool same = after.Count == before.Count;
                for (int k = 0; same && k < after.Count; k++) same = after[k].Key == before[k].Key && after[k].Value == before[k].Value;
                if (!on || !same) { why = L.T("넣은 뒤 검증 실패(다른 키가 달라짐) — 그대로 둠", "check after the insert failed (another key changed) — left as is"); return null; }
            }
            catch (JsonException e) { why = L.T("넣은 뒤 파싱 실패 — 그대로 둠(" + e.Message + ")", "parse after the insert failed — left as is (" + e.Message + ")"); return null; }
            return n;
        }

        /// <summary>최상위 여는 중괄호 위치(BOM·공백·// 줄 주석·/* */ 블록 주석 건너뜀). 그 밖의 글자가 먼저 나오면 -1.</summary>
        static int OpeningBrace(string t)
        {
            int i = 0;
            while (i < t.Length)
            {
                char c = t[i];
                if (c == '\uFEFF' || char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '/' && i + 1 < t.Length && t[i + 1] == '/') { int e = t.IndexOf('\n', i); if (e < 0) return -1; i = e + 1; continue; }
                if (c == '/' && i + 1 < t.Length && t[i + 1] == '*') { int e = t.IndexOf("*/", i + 2, StringComparison.Ordinal); if (e < 0) return -1; i = e + 2; continue; }
                return c == '{' ? i : -1;
            }
            return -1;
        }

        // ------------------------------------------------------------------ 언어(트레이 설정 ▸ 언어 · uart hub language → Supervisor.RequestLanguage)
        /// <summary>
        /// config.json 텍스트의 최상위 "language" 를 lang(auto|ko|en)으로 글자 편집한 새 텍스트(C-12 — 주석·키 순서·CRLF·BOM 그대로, WithCableBaud 와 같은 방식).
        /// ko·en: 키가 있으면 그 값만 바꾸고, 없으면 여는 중괄호 바로 뒤에 끼운다. 텍스트가 비었으면(파일 없음 = null) {"language": …} 하나.
        /// auto: 키가 있으면 지운다(혼자 있는 줄은 줄째로, 아니면 항목과 그 쉼표) — 기본 설정의 config.json 은 "language" 를 모르는 옛 exe 도 읽게.
        ///   키가 없으면 텍스트 그대로. 주석이 붙어 있어 깨끗이 지울 수 없으면 "auto" 를 써 넣고 why 에 그렇다고 적는다.
        /// lang 이 auto|ko|en 이 아니거나, "language" 가 둘 이상·객체·배열이거나, JSON 이 아니거나, 다시 읽어 다른 최상위 값이 하나라도 달라지면 null 과 이유.
        /// 바꿀 것이 없으면 받은 텍스트 그대로(null 이면 "").
        /// </summary>
        public static string WithLanguage(string text, string lang, out string why)
        {
            why = null;
            string v = lang?.Trim().ToLowerInvariant();
            if (!L.IsValidSetting(v)) { why = L.T("언어는 auto·ko·en 중 하나: " + lang, "the language must be auto, ko or en: " + lang); return null; }
            text ??= "";
            bool bom = text.Length > 0 && text[0] == '﻿';
            string body = bom ? text.Substring(1) : text;
            if (string.IsNullOrWhiteSpace(body)) return v == L.Auto ? text : (bom ? "﻿" : "") + "{\n  \"language\": \"" + v + "\"\n}\n";
            var u8 = new System.Text.UTF8Encoding(false);
            byte[] u = u8.GetBytes(body);
            int open, keyAt = -1, valAt = -1, valEnd = -1;
            bool empty = true;
            try
            {
                var rd = new Utf8JsonReader(u, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (!rd.Read() || rd.TokenType != JsonTokenType.StartObject) { why = L.T("최상위가 객체가 아님 — 그대로 둠", "top level is not an object — left as is"); return null; }
                open = (int)rd.TokenStartIndex;
                while (rd.Read() && rd.TokenType == JsonTokenType.PropertyName)  // 값은 Skip 하므로 최상위 키만 본다
                {
                    empty = false;
                    bool mine = rd.ValueTextEquals("language");
                    int k = (int)rd.TokenStartIndex;
                    rd.Read();
                    if (!mine) { rd.Skip(); continue; }
                    if (keyAt >= 0) { why = L.T("\"language\" 키가 둘 이상 — 그대로 둠", "\"language\" appears more than once — left as is"); return null; }
                    if (rd.TokenType == JsonTokenType.StartObject || rd.TokenType == JsonTokenType.StartArray) { why = L.T("\"language\" 가 문자열이 아님 — 그대로 둠", "\"language\" is not a string — left as is"); return null; }
                    keyAt = k; valAt = (int)rd.TokenStartIndex; valEnd = (int)rd.BytesConsumed;
                }
            }
            catch (JsonException e) { why = L.T("JSON 파싱 실패 — 그대로 둠(" + e.Message + ")", "JSON parse failed — left as is (" + e.Message + ")"); return null; }
            if (keyAt < 0 && v == L.Auto) return text;                          // auto 이고 키 없음: 그대로

            string Splice(int at, int cut, string ins)
            {
                byte[] add = u8.GetBytes(ins);
                var nb = new byte[u.Length - cut + add.Length];
                Buffer.BlockCopy(u, 0, nb, 0, at);
                Buffer.BlockCopy(add, 0, nb, at, add.Length);
                Buffer.BlockCopy(u, at + cut, nb, at + add.Length, u.Length - at - cut);
                return u8.GetString(nb);
            }
            static bool Ws(byte b) => b == (byte)' ' || b == (byte)'\t';
            static bool Nl(byte b) => b == (byte)'\r' || b == (byte)'\n';
            // auto 로 지울 글자 범위 [from, to). 주석이 붙어 있는 등 깨끗이 지울 수 없으면 (-1, -1)
            (int From, int To) Removal()
            {
                for (int x = keyAt; x < valAt; x++) if (u[x] == (byte)'/') return (-1, -1);          // 키와 값 사이 주석
                int i = valEnd;
                while (i < u.Length && Ws(u[i])) i++;
                if (i < u.Length && u[i] == (byte)',')
                {
                    int j = i + 1;
                    while (j < u.Length && Ws(u[j])) j++;
                    if (j >= u.Length || u[j] == (byte)'/') return (-1, -1);                          // 쉼표 뒤 같은 줄 주석
                    if (!Nl(u[j])) return (keyAt, j);                                                   // 같은 줄에 다음 항목(또는 끝 쉼표 뒤 }): 항목·쉼표·뒤 공백
                    int s = keyAt;
                    while (s > 0 && Ws(u[s - 1])) s--;
                    if (s == 0 || Nl(u[s - 1])) return (s, j + (u[j] == (byte)'\r' && j + 1 < u.Length && u[j + 1] == (byte)'\n' ? 2 : 1));   // 혼자 있는 줄: 줄째로
                    return (s, j);                                                                      // 앞에 다른 것이 있는 줄: 앞 공백부터 쉼표 뒤 공백까지(줄 끝은 남김)
                }
                int p = i;
                while (p < u.Length && (Ws(u[p]) || Nl(u[p]))) p++;
                if (p >= u.Length || u[p] != (byte)'}') return (-1, -1);                                // 값 뒤에 주석 등
                int r = keyAt;
                while (r > 0 && (Ws(u[r - 1]) || Nl(u[r - 1]))) r--;
                if (r > 0 && u[r - 1] == (byte)',') return (r - 1, valEnd);                             // 마지막 항목: 앞 쉼표부터
                if (r > 0 && u[r - 1] == (byte)'{') return (r, valEnd);                                 // 하나뿐인 항목
                return (-1, -1);                                                                        // 앞에 주석
            }

            string n, written = v;                                                                      // written = 결과의 "language"(null = 키 없음)
            if (keyAt < 0)
            {
                int at = open + 1;
                bool cr = at < u.Length && u[at] == (byte)'\r', lf = cr || (at < u.Length && u[at] == (byte)'\n');
                string kv = "\"language\": \"" + v + "\"";
                n = Splice(at, 0, empty ? kv : lf ? (cr ? "\r\n" : "\n") + "  " + kv + "," : " " + kv + ",");
            }
            else if (v != L.Auto) n = Splice(valAt, valEnd - valAt, "\"" + v + "\"");
            else
            {
                var cut = Removal();
                if (cut.From >= 0) { n = Splice(cut.From, cut.To - cut.From, ""); written = null; }
                else
                {
                    n = Splice(valAt, valEnd - valAt, "\"" + L.Auto + "\"");
                    why = L.T("\"language\" 에 주석이 붙어 있어 지우지 않고 \"auto\" 로 씀", "a comment is attached to \"language\", so it was set to \"auto\" instead of removed");
                }
            }
            try
            {
                using var a = JsonDocument.Parse(body, JsoncOptions);
                using var b = JsonDocument.Parse(n, JsoncOptions);
                static IEnumerable<string> Others(JsonElement r) => r.EnumerateObject().Where(p => p.Name != "language").Select(p => p.Name + "\u0000" + p.Value.GetRawText());
                bool same = b.RootElement.ValueKind == JsonValueKind.Object && Others(a.RootElement).SequenceEqual(Others(b.RootElement));
                var mine = same ? b.RootElement.EnumerateObject().Where(p => p.Name == "language").ToList() : null;
                bool ok = same && (written == null ? mine.Count == 0 : mine.Count == 1 && mine[0].Value.ValueKind == JsonValueKind.String && mine[0].Value.GetString() == written);
                if (!ok) { why = L.T("고친 뒤 검증 실패(다른 키가 달라짐) — 그대로 둠", "check after the edit failed (another key changed) — left as is"); return null; }
            }
            catch (JsonException e) { why = L.T("고친 뒤 파싱 실패 — 그대로 둠(" + e.Message + ")", "parse after the edit failed — left as is (" + e.Message + ")"); return null; }
            return (bom ? "﻿" : "") + n;
        }

        // ------------------------------------------------------------------ 케이블 보오(X5 /connect-uart COMx <baud>)
        /// <summary>connect 가 받는 표준 보오.</summary>
        public static readonly int[] StandardBauds = { 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600 };
        public static bool IsStandardBaud(int b) => Array.IndexOf(StandardBauds, b) >= 0;
        public static string StandardBaudText => string.Join(", ", StandardBauds);

        /// <summary>
        /// config.json 텍스트에 cables.&lt;id&gt;.baud = baud 를 넣은 새 텍스트(코어의 cable baud 가 쓴다). WithOnDemand 처럼 글자로 고친다 — 주석·CRLF·BOM·키 순서 그대로(C-12):
        /// 그 케이블에 baud 가 있으면 그 값만 바꾸고, 없으면 그 케이블 객체의 여는 중괄호 뒤에, 케이블 항목이 없으면 cables 의 여는 중괄호 뒤에,
        /// cables 가 없으면 최상위 여는 중괄호 뒤에 끼운다. id 는 대소문자를 가리지 않는다(Config.Cables 와 같음). cables(또는 그 케이블)가 객체가 아니거나,
        /// JSON 이 아니거나, 다시 읽어 다른 값이 하나라도 달라지면 null 과 이유. text 가 비었으면 {"cables": …} 하나만 만든다.
        /// </summary>
        public static string WithCableBaud(string text, string cableId, int baud, out string why)
        {
            why = null;
            string b = baud.ToString(CultureInfo.InvariantCulture);
            string entry = Json.Str(cableId) + ": {\"baud\": " + b + "}";
            if (string.IsNullOrWhiteSpace(text)) return "{\n  \"cables\": {" + entry + "}\n}\n";
            bool bom = text[0] == '\uFEFF';
            string body = bom ? text.Substring(1) : text;
            var u8 = new System.Text.UTF8Encoding(false);
            byte[] u = u8.GetBytes(body);
            long topOpen = -1, cablesOpen = -1, cableOpen = -1, baudStart = -1, baudEnd = -1;
            bool topEmpty = true, cablesEmpty = true, cableEmpty = true;
            string cablesKind = null;
            try
            {
                var rd = new Utf8JsonReader(u, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (!rd.Read() || rd.TokenType != JsonTokenType.StartObject) { why = L.T("최상위가 객체가 아님 — 그대로 둠", "top level is not an object — left as is"); return null; }
                topOpen = rd.TokenStartIndex;
                while (rd.Read() && !(rd.TokenType == JsonTokenType.EndObject && rd.CurrentDepth == 0))
                {
                    if (rd.TokenType != JsonTokenType.PropertyName || rd.CurrentDepth != 1) continue;
                    topEmpty = false;
                    string pn = rd.GetString();
                    rd.Read();
                    if (pn != "cables" || cablesOpen >= 0 || cablesKind != null) { rd.Skip(); continue; }
                    if (rd.TokenType != JsonTokenType.StartObject) { cablesKind = rd.TokenType.ToString(); rd.Skip(); continue; }
                    cablesOpen = rd.TokenStartIndex;
                    while (rd.Read() && !(rd.TokenType == JsonTokenType.EndObject && rd.CurrentDepth == 1))
                    {
                        if (rd.TokenType != JsonTokenType.PropertyName || rd.CurrentDepth != 2) continue;
                        cablesEmpty = false;
                        string cn = rd.GetString();
                        rd.Read();
                        if (cableOpen >= 0 || !string.Equals(cn, cableId, StringComparison.OrdinalIgnoreCase)) { rd.Skip(); continue; }
                        if (rd.TokenType != JsonTokenType.StartObject) { why = L.T("cables." + cn + " 가 객체가 아님 — 그대로 둠", "cables." + cn + " is not an object — left as is"); return null; }
                        cableOpen = rd.TokenStartIndex;
                        while (rd.Read() && !(rd.TokenType == JsonTokenType.EndObject && rd.CurrentDepth == 2))
                        {
                            if (rd.TokenType != JsonTokenType.PropertyName || rd.CurrentDepth != 3) continue;
                            cableEmpty = false;
                            string fn = rd.GetString();
                            rd.Read();
                            if (fn != "baud" || baudStart >= 0) { rd.Skip(); continue; }
                            if (rd.TokenType == JsonTokenType.StartObject || rd.TokenType == JsonTokenType.StartArray) { why = L.T("cables." + cn + ".baud 가 숫자가 아님 — 그대로 둠", "cables." + cn + ".baud is not a number — left as is"); return null; }
                            baudStart = rd.TokenStartIndex;
                            baudEnd = baudStart + (rd.HasValueSequence ? rd.ValueSequence.Length : rd.ValueSpan.Length) + (rd.TokenType == JsonTokenType.String ? 2 : 0);
                        }
                    }
                }
            }
            catch (JsonException e) { why = L.T("JSON 파싱 실패 — 그대로 둠(" + e.Message + ")", "JSON parse failed — left as is (" + e.Message + ")"); return null; }
            if (cablesOpen < 0 && cablesKind != null) { why = L.T("cables 가 객체가 아님(" + cablesKind + ") — 그대로 둠", "cables is not an object (" + cablesKind + ") — left as is"); return null; }
            int at;
            string ins;
            int cut = 0;
            if (baudStart >= 0) { at = (int)baudStart; cut = (int)(baudEnd - baudStart); ins = b; }
            else if (cableOpen >= 0) { at = (int)cableOpen + 1; ins = cableEmpty ? "\"baud\": " + b : "\"baud\": " + b + ", "; }
            else if (cablesOpen >= 0) { at = (int)cablesOpen + 1; ins = cablesEmpty ? entry : entry + ", "; }
            else
            {
                at = (int)topOpen + 1;
                bool cr = at < u.Length && u[at] == (byte)'\r', lf = at < u.Length && (u[at] == (byte)'\n' || cr);
                string obj = "\"cables\": {" + entry + "}";
                ins = topEmpty ? obj : lf ? (cr ? "\r\n" : "\n") + "  " + obj + "," : " " + obj + ",";
            }
            byte[] add = u8.GetBytes(ins);
            var nb = new byte[u.Length - cut + add.Length];
            Buffer.BlockCopy(u, 0, nb, 0, at);
            Buffer.BlockCopy(add, 0, nb, at, add.Length);
            Buffer.BlockCopy(u, at + cut, nb, at + add.Length, u.Length - at - cut);
            string n = (bom ? "\uFEFF" : "") + u8.GetString(nb);
            if (!SameExceptBaud(body, n.TrimStart('\uFEFF'), cableId, baud)) { why = L.T("넣은 뒤 검증 실패(다른 값이 달라짐) — 그대로 둠", "check after the insert failed (another value changed) — left as is"); return null; }
            try
            {
                var cfg = Parse(n.TrimStart('\uFEFF'));
                if (cfg.CableFor(cableId).Baud != baud) { why = L.T("넣은 뒤 검증 실패(baud 가 다름) — 그대로 둠", "check after the insert failed (baud differs) — left as is"); return null; }
            }
            catch (ConfigException e)
            {
                // 원래도 허브 파서가 거부하던 설정이면(알 수 없는 키 등) 그 오류는 그대로 둔다 — baud 를 넣은 것 때문에 생긴 오류만 막는다
                bool before = true;
                try { Parse(body); } catch (ConfigException) { before = false; }
                if (before) { why = L.T("넣은 뒤 설정 오류(" + string.Join("; ", e.Errors) + ") — 그대로 둠", "config error after the insert (" + string.Join("; ", e.Errors) + ") — left as is"); return null; }
            }
            return n;
        }

        /// <summary>a·b 를 다시 읽어 cables.&lt;id&gt;.baud 만 다른지(b 의 그 값 = baud) 본다 — 최상위 키·다른 케이블·그 케이블의 다른 키는 원문 그대로여야 한다.</summary>
        static bool SameExceptBaud(string a, string b, string cableId, int baud)
        {
            try
            {
                using var da = JsonDocument.Parse(a, JsoncOptions);
                using var db = JsonDocument.Parse(b, JsoncOptions);
                static List<KeyValuePair<string, JsonElement>> Props(JsonElement e) => e.ValueKind == JsonValueKind.Object ? e.EnumerateObject().Select(p => new KeyValuePair<string, JsonElement>(p.Name, p.Value)).ToList() : new List<KeyValuePair<string, JsonElement>>();
                var ta = Props(da.RootElement).Where(p => p.Key != "cables").Select(p => p.Key + "=" + p.Value.GetRawText());
                var tb = Props(db.RootElement).Where(p => p.Key != "cables").Select(p => p.Key + "=" + p.Value.GetRawText());
                if (!ta.SequenceEqual(tb)) return false;
                var ca = da.RootElement.TryGetProperty("cables", out var x) ? Props(x) : new List<KeyValuePair<string, JsonElement>>();
                var cb = Props(db.RootElement.GetProperty("cables"));
                bool Target(string k) => string.Equals(k, cableId, StringComparison.OrdinalIgnoreCase);
                if (!ca.Where(p => !Target(p.Key)).Select(p => p.Key + "=" + p.Value.GetRawText()).SequenceEqual(cb.Where(p => !Target(p.Key)).Select(p => p.Key + "=" + p.Value.GetRawText()))) return false;
                var oa = ca.Where(p => Target(p.Key)).Select(p => Props(p.Value)).FirstOrDefault() ?? new List<KeyValuePair<string, JsonElement>>();
                var ob = cb.Where(p => Target(p.Key)).Select(p => Props(p.Value)).FirstOrDefault();
                if (ob == null) return false;
                if (!oa.Where(p => p.Key != "baud").Select(p => p.Key + "=" + p.Value.GetRawText()).SequenceEqual(ob.Where(p => p.Key != "baud").Select(p => p.Key + "=" + p.Value.GetRawText()))) return false;
                var nbv = ob.FirstOrDefault(p => p.Key == "baud");
                return nbv.Key != null && nbv.Value.ValueKind == JsonValueKind.Number && nbv.Value.GetInt64() == baud;
            }
            catch (Exception) { return false; }
        }

        static void Pos(List<string> e, string name, double v) { if (!(v > 0)) e.Add(L.T(name + " 는 양수여야 함: " + v.ToString(CultureInfo.InvariantCulture), name + " must be positive: " + v.ToString(CultureInfo.InvariantCulture))); }

        static void Re(List<string> e, string name, string re)
        {
            if (string.IsNullOrEmpty(re)) { e.Add(L.T(name + " 비어 있음", name + " is empty")); return; }
            try { _ = new Regex(re); } catch (ArgumentException x) { e.Add(L.T(name + " 정규식 오류: " + x.Message, name + " regex error: " + x.Message)); }
        }
    }
}
