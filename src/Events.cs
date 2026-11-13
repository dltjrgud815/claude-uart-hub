// 에이징 사건 감지(계획 X6·부록 C.2·C.3). 동결 파일(Hub·RingBuffer·DiskWriter·PromptTracker …)은 건드리지 않는다:
//   ControlServer 가 EventDetector 를 만들고, EventDetector 는 CableManager.CableAttached·CableDetached 와 Cable.Transmitted 만 구독한다.
//   케이블(객체)마다 추적 스레드 하나가 링 버퍼를 자기 커서로 따라 읽고(ControlServer wait 와 같은 방식) 줄을 나눠 EventScanner 에 넣는다.
//   EventScanner(포트 하나): C.2 기본 패턴(panic·oops·bug·oom = 일반 Linux, app_error 는 제품 프로필 — config events.patterns·cables.<id>.events 로 끄고 더함),
//   부팅 분류(boot_re: cmd = 허브·사람이 보낸 reboot 명령 줄 뒤 60초 안(그 안의 Restarting 이 창을 잇는다) / soft = 명령 없이 'reboot: Restarting system' 뒤 /
//   hard = 그 밖), 한 사건의 연속 줄 묶기(같은 부팅·30초·앞뒤 로그를 모으는 동안 = 파일 하나), 앞뒤 로그(앞 200줄 + 뒤 100줄 또는 30초, soft·hard 부팅은 앞 200줄).
//   기록: <log_root>\events\<port>.jsonl 한 줄 = 한 사건 {ts, port, cable, board, kind, class(boot), line, log, log_line(rx_offset), context, boot_seq, since_boot_s},
//         연결 기록 kind:"attach"(허브 시작·다시 연결 — 그 전 기록 공백 gap_s), 앞뒤 로그 <log_root>\events\<yyyyMMdd>\<port>_<board>_<HHMMSS>_<kind>.txt.
//   log_layout 2(부록 C.11): jsonl 은 <허브 폴더>\events\<port>.jsonl(내부 기록), 사람이 보는 에이징 이벤트 txt 는 알림 종류(config events.notify — 기본 panic·oops·app_error)
//         묶음만 <기준>\에이징이벤트\<yyyyMMdd>\<HHMMSS>_<보드>(<포트>)_<종류>[_의심].txt(종류 = 커널패닉·Oops·앱오류_<코드>, 묶음에서 가장 심한 것 — 더 심한 종류가
//         합류하면 이름·머리를 바꿔 다시 쓰고 그 기록에 context_was). 본문 = 머리(이벤트 종류·보드·시각·원인 줄·콘솔 로그 파일) → 앞 200줄 → 이후 → 결과(재부팅 시각·응답 없음·계속 동작).
//         그 밖(부팅·bug·oom·missed·연결·분리)은 jsonl 에만.
//   판정(부록 C.7 — 받아들이기 없이 거르기만): 로더 표시(loader_marker_re — 제품 프로필·config, 없으면 판정 안 함)는 줄 어디에 있든(앞에 커널 시각·깨진 글자가 있어도) 실시간 리셋 —
//     로더 줄은 커널 링·pstore 에 없어 dmesg·cat 으로 다시 찍히지 않는다(구간 안이면 앞 RX 무음 0.5초 이상일 때만 — 덤프된 캡처 로그). 1초 넘게 떨어지면 별개 부팅.
//     명령 출력 구간 = 송신 CR(빈 Enter·회상·편집 모두) ~ 진짜 프롬프트(prompt_re 가 개행 없이 열린 채 RX 300ms 무음)·실시간 부팅·분리(굳게 닫힘),
//     또는 RX·TX 10초 무음(무르게 닫힘 — 그 뒤 송신 바이트 하나면 다시 열림, 페이저 키 등). 구간 안의 커널·앱 사건 줄은 모두 suspect:"cmd_output".
//     일부러 일으키는 리셋·충돌 명령(reboot, echo … > /proc/sysrq-trigger, provoke-crash, lkdtm — 덤프하지 않는 곁 명령과만 섞인 줄)은 구간을 열지 않는다(W7).
//     로더 표시·커널 시각 줄은 에코·치는 중으로 보지 않는다(BLK-1), 치는 중은 사람이 보낸 글자와 맞을 때만.
//     구간 밖은 실시간 — 단 커널 시각이 (이 실행에서 본 지금 부팅의 로더 뒤 경과 + 5초)보다 크거나, 이 부팅 구간 밖 실시간 줄의 최대 커널 시각보다 2초 넘게 작으면
//     suspect(커널 시계는 절전 중 멈추되 뒤로 가지 않는다). 로더 표시 없는 보드의 커널 부팅 줄은 구간 밖이면 부팅, 구간 안이면 앞 RX 무음 1초 이상일 때만.
//     재부팅 분류는 실시간 부팅 순간의 되돌아보기로만: cmd(명령 자리 reboot 60초 안, 실시간 Restarting 이 이음) → hard + cause(직전 20초 panic/oops 줄, suspect 포함)
//     → soft(로더 표시 바로 앞 커널 줄이 2초 안의 Restarting, 진짜 프롬프트 뒤 아님 — BLK-2) → hard + silence_s.
//     suspect panic/oops 는 60초 안 실시간 부팅이 cause 로 확인하고, 부팅 없이 RX 30초 무음이면 confirm(페이저에서 읽는 중·그 뒤 키를 쳤으면 아님, W1).
//     버리는 것은 확실한 재출력(<N>[ts] — 앞에 'grep -n' 줄번호·파일 이름이 붙어도)과 명령 에코뿐. 기록 공백: kind "detach"(분리) → "attach"(다시 연결·허브 시작, gap_s).
//   앞뒤 로그 예산: 한 부팅 안 같은 종류는 1시간에 3개 뒤로는 10분에 1개, 포트마다 1시간 30개(부팅마다 첫 실시간 panic·oops 와 soft·hard 부팅 앞 줄은 면제),
//   events 폴더가 events.max_mb(기본 2048MB)를 넘으면 더 만들지 않는다
//   (지우지 않음 — jsonl 에는 계속 쓰고 context_skipped 로 이유를 남긴다, 예산이 걸릴 때 hub.log 경고 한 줄).
//   파일 쓰기: 포트마다 jsonl 스트림 하나를 열어 두고 줄마다 flush. 백신·공유 위반(IOException·UnauthorizedAccessException)은 2초까지 늘려 가며 다시 시도하고
//   그래도 안 되면 대기열(상한)에 두었다가 주기마다 다시 쓴다. 끝내 버리는 줄은 hub.log 에 그 내용과 함께(빼지 않고) 남긴다. 보드로는 아무것도 보내지 않는다.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace UartHub
{
    /// <summary>사건 종류·기본 패턴(부록 C.2). Errors 순서 = 판정 순서(한 줄이 여러 종류에 맞으면 앞의 것).</summary>
    public static class EventKinds
    {
        public static readonly string[] Errors = { "panic", "oops", "bug", "oom", "app_error" };
        public static readonly string[] All = { "panic", "oops", "bug", "oom", "app_error", "boot", "confirm", "missed", "attach", "detach" };

        /// <summary>일반 Linux 커널 기본 패턴. 앱 오류(app_error)는 보드·펌웨어마다 달라 기본이 없다 — 제품 프로필(ProductProfile)이 더한다.</summary>
        public static readonly IReadOnlyDictionary<string, string[]> GenericDefaults = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["panic"] = new[] { @"Kernel panic - not syncing" },
            // 'Oops#1 Part1'(pstore 머리말)은 사건 아님
            ["oops"] = new[] { @"Unable to handle kernel", @"Internal error: Oops", @"^\s*(?:\[\s*\d+\.\d+\]\s*)?Oops(?![#])" },
            ["bug"] = new[] { @"(?<![A-Za-z])BUG: ", @"kernel BUG at", @"WARNING: CPU:", @"rcu: INFO: .*stall", @"INFO: rcu_\w+ self-detected stall", @"soft lockup",
                              @"INFO: task .* blocked for more than" },
            ["oom"] = new[] { @"Out of memory: Kill", @"invoked oom-killer" },
            ["app_error"] = Array.Empty<string>(),
        };

        /// <summary>기본 패턴 = 일반 기본 + 제품 프로필이 더한 것(config events.patterns 의 off 는 둘 다에 쓴다).</summary>
        public static IReadOnlyDictionary<string, string[]> Defaults => ProductProfile.Current.EventDefaults;

        public static bool IsError(string kind) => Array.IndexOf(Errors, kind) >= 0;

        /// <summary>
        /// 알리는 종류 기본값(config events.notify, 2026-11-06 사용자 결정): panic·oops·app_error·confirm(panic 뒤 응답 없음). 재부팅은 기록만 한다 —
        /// 전원 on/off 에이징에서는 매 회차가 hard 리셋이라 풍선·Claude 감시가 넘친다. bug·oom·missed 도 기록만.
        /// </summary>
        public static readonly string[] NotifyDefault = { "panic", "oops", "app_error", "confirm" };

        /// <summary>events.notify·CLI --kind 에 쓸 수 있는 값: All 의 종류 + boot:cmd|soft|hard.</summary>
        public static bool IsKindFilter(string k)
            => k != null && (Array.IndexOf(All, k) >= 0 || (k.StartsWith("boot:", StringComparison.Ordinal) && Array.IndexOf(new[] { "cmd", "soft", "hard" }, k.Substring(5)) >= 0));

        /// <summary>
        /// 알릴 사건인가(트레이 풍선): 종류가 notify(기본 NotifyDefault)에 있으면. 부팅은 notify 에 "boot"·"boot:&lt;class&gt;" 가 있을 때만 —
        /// 단 cause 가 panic·oops 인 부팅("panic 뒤 재부팅")은 그 panic 의 확인이라 notify 에 그 종류가 있으면 알린다(기본값으로 알림).
        /// 같은 종류가 잦아 앞뒤 로그를 줄인 사건(context_skipped "rate")은 알리지 않는다 — 처음 몇 번과 10분마다의 것은 알린다.
        /// 명령 출력 속 줄일 수 있는 사건(suspect "cmd_output", 부록 C.6)도 알리지 않는다(기록·--follow 에는 남는다) — 그 확인(리셋의 cause·confirm)은 알린다.
        /// </summary>
        public static bool Notifiable(string kind, string cls, string contextSkipped = null, string suspect = null, string causeKind = null, IEnumerable<string> notify = null)
        {
            if (suspect != null || contextSkipped == "rate" || kind == null) return false;
            var n = notify ?? NotifyDefault;
            if (kind == "boot")
                return n.Contains("boot") || (cls != null && n.Contains("boot:" + cls)) || ((causeKind == "panic" || causeKind == "oops") && n.Contains(causeKind));
            return n.Contains(kind);
        }

        /// <summary>풍선 머리말 순위(클수록 앞). causeKind = 부팅의 cause(panic/oops 뒤 리셋 — 그 종류 바로 뒤).</summary>
        public static int Severity(string kind, string cls, string causeKind = null) => kind switch
        {
            "panic" => 90, "oops" => 80, "bug" => 70, "oom" => 60, "app_error" => 50, "confirm" => 85,
            "boot" => causeKind != null ? Severity(causeKind, null) - 1 : cls == "hard" ? 40 : cls == "soft" ? 30 : 0,
            "missed" => 10, _ => 0,
        };

        /// <summary>
        /// 사람이 읽는 이름. causeKind = 부팅의 cause 종류("panic 후 리셋"), of = confirm 이 확인한 종류("panic 뒤 응답 없음").
        /// lang null = 지금 언어(이벤트 txt 머리는 세션 언어).
        /// </summary>
        public static string Label(string kind, string cls, string causeKind = null, string of = null, string lang = null) => kind switch
        {
            "panic" => "Kernel panic", "oops" => AgingLang.T(lang, "커널 Oops", "Kernel Oops"), "bug" => AgingLang.T(lang, "커널 BUG·경고", "Kernel BUG/warning"),
            "oom" => AgingLang.T(lang, "메모리 부족(OOM)", "Out of memory (OOM)"), "app_error" => AgingLang.T(lang, "앱 오류", "App error"),
            "boot" => causeKind != null ? AgingLang.T(lang, causeKind + " 후 리셋(" + cls + ")", "Reset after " + causeKind + " (" + cls + ")")
                    : cls == "cmd" ? AgingLang.T(lang, "재부팅(명령)", "Reboot (command)") : AgingLang.T(lang, "예상 못 한 재부팅(" + cls + ")", "Unexpected reboot (" + cls + ")"),
            "confirm" => AgingLang.T(lang, (of ?? "panic") + " 뒤 응답 없음(멈춤 의심)", "No response after " + (of ?? "panic") + " (suspected hang)"),
            "missed" => AgingLang.T(lang, "로그 놓침(검사 못 한 구간)", "Log missed (span not checked)"), "attach" => AgingLang.T(lang, "연결", "Connected"),
            "detach" => AgingLang.T(lang, "분리", "Disconnected"), _ => kind,
        };
    }

    /// <summary>케이블 하나의 사건 패턴(기본 − 끈 것 + 더한 것: 최상위 events 다음 cables.&lt;id&gt;.events). 종류 순서대로 판정한다.</summary>
    public sealed class EventPatterns
    {
        static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(200);
        static readonly Dictionary<string, Regex> DefaultRe = new Dictionary<string, Regex>(StringComparer.Ordinal);
        public readonly List<(string Kind, string Text, Regex Re)> List = new List<(string, string, Regex)>();

        /// <summary>기본 패턴: 둘러보기(lookaround)가 없으면 NonBacktracking(되돌아가기 폭주 없음), 있으면 Compiled.</summary>
        static Regex Default(string text)
        {
            lock (DefaultRe)
            {
                if (DefaultRe.TryGetValue(text, out var r)) return r;
                try { r = new Regex(text, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, MatchTimeout); }
                catch (NotSupportedException) { r = new Regex(text, RegexOptions.Compiled | RegexOptions.CultureInvariant, MatchTimeout); }
                return DefaultRe[text] = r;
            }
        }

        public static EventPatterns For(Config cfg, string cableId)
        {
            var p = new EventPatterns();
            var cc = cfg.CableFor(cableId).Events;
            foreach (var kind in EventKinds.Errors)
            {
                EventRule g = null, c = null;
                cfg.Events?.Patterns.TryGetValue(kind, out g);
                cc?.Patterns.TryGetValue(kind, out c);
                bool Off(EventRule r, string d) => r != null && (r.OffAll || r.Off.Contains(d));
                foreach (var d in EventKinds.Defaults[kind])
                    if (!Off(g, d) && !Off(c, d)) p.List.Add((kind, d, Default(d)));
                foreach (var a in (g?.Add ?? new List<string>()).Concat(c?.Add ?? new List<string>()))
                    p.List.Add((kind, a, new Regex(a, RegexOptions.CultureInvariant, MatchTimeout)));
            }
            return p;
        }

        /// <summary>줄(\r 로 나눈 구간들)에 맞는 첫 종류. 정규식 시간 초과는 맞지 않음으로 보고 onTimeout(패턴 글자)을 부른다.</summary>
        public string Match(IReadOnlyList<string> segs, Action<string> onTimeout)
        {
            foreach (var (kind, text, re) in List)
                foreach (var s in segs)
                {
                    try { if (re.IsMatch(s)) return kind; }
                    catch (RegexMatchTimeoutException) { onTimeout?.Invoke(text); }
                }
            return null;
        }
    }

    /// <summary>
    /// events 폴더 크기 예산(H4): 시작 때 폴더 전체 크기를 세고 쓰는 만큼 더한다. 넘으면 앞뒤 로그를 더 만들지 않는다(지우지 않음).
    /// 케이블 판정기들이 같이 쓴다(잠금).
    /// </summary>
    public sealed class EventsBudget
    {
        readonly object l = new object();
        long bytes;
        public long MaxBytes { get; }

        public EventsBudget(string dir, long maxBytes) : this(maxBytes, dir) { }

        /// <summary>여러 폴더(log_layout 2 = jsonl 폴더 + 에이징이벤트)의 크기를 더한다. null·같은 폴더는 한 번만.</summary>
        public EventsBudget(long maxBytes, params string[] dirs)
        {
            MaxBytes = maxBytes;
            foreach (var dir in dirs.Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                try
                {
                    if (Directory.Exists(dir))
                        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                            try { bytes += new FileInfo(f).Length; } catch (Exception) { }
                }
                catch (Exception) { }
        }

        public long Bytes { get { lock (l) return bytes; } }
        public bool Full { get { lock (l) return MaxBytes > 0 && bytes >= MaxBytes; } }
        public void Add(long n) { lock (l) bytes += n; }
    }

    /// <summary>
    /// 덧붙여 쓰는 파일 하나(H2): 스트림을 열어 두고 줄마다 flush. 실패(백신·공유 위반)면 닫고, 처음 실패한 쓰기는 2초까지 늘려 가며 다시 시도,
    /// 그 뒤는 대기열(상한)에 두고 Retry(주기)마다 한 번씩. 대기열이 넘치거나 Close 에서도 못 쓴 줄은 버리고 hub.log 에 남긴다(perLine 이면 줄마다 내용 그대로, 아니면 묶어서).
    /// </summary>
    public sealed class SafeFile
    {
        const int MaxPending = 2000;
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        readonly HubLog hlog;
        readonly EventsBudget budget;
        readonly bool perLine;
        readonly string what;
        FileStream fs;
        readonly Queue<byte[]> q = new Queue<byte[]>();
        bool failing;
        int dropped;
        string firstDrop;

        public string Path { get; }
        /// <summary>버린 줄 수.</summary>
        public int Dropped => dropped;
        public bool Pending => q.Count > 0;

        public SafeFile(string path, string what, HubLog hlog, EventsBudget budget, bool perLine)
        {
            Path = path; this.what = what; this.hlog = hlog; this.budget = budget; this.perLine = perLine;
        }

        public void Write(string text)
        {
            if (q.Count >= MaxPending) Drop(q.Dequeue(), L.T("대기열 " + MaxPending + "줄 넘침", "queue over " + MaxPending + " lines"));
            q.Enqueue(Utf8.GetBytes(text));
            Flush(failing ? 0 : 2000);
        }

        /// <summary>다시 쓸 것이라 버림(log_layout 2 txt 이름 바꾸기): 대기열을 비우고(hub.log 에 남기지 않음) 닫는다.</summary>
        public void Abandon()
        {
            q.Clear();
            try { fs?.Dispose(); } catch (Exception) { }
            fs = null;
        }

        /// <summary>대기열이 있으면 한 번 더 시도(기다리지 않음).</summary>
        public void Retry() { if (q.Count > 0) Flush(0); }

        /// <summary>끝: budgetMs 동안 마저 쓰고, 남은 줄은 버리고 남긴다.</summary>
        public void Close(int budgetMs)
        {
            Flush(budgetMs);
            while (q.Count > 0) Drop(q.Dequeue(), L.T("닫을 때까지 쓰지 못함", "not written before close"));
            if (!perLine && dropped > 0)
                hlog?.Write(L.T(what + " " + dropped + "줄 버림(" + Path + ") — 첫 줄: " + firstDrop, what + ": dropped " + dropped + " line(s) (" + Path + ") — first line: " + firstDrop));
            try { fs?.Dispose(); } catch (Exception) { }
            fs = null;
        }

        void Flush(int budgetMs)
        {
            long deadline = Environment.TickCount64 + budgetMs;
            int delay = 20;
            while (q.Count > 0)
            {
                try
                {
                    if (fs == null)
                    {
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                        fs = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    }
                    var b = q.Peek();
                    fs.Write(b, 0, b.Length);
                    fs.Flush();
                    q.Dequeue();
                    budget?.Add(b.Length);
                    if (failing) { failing = false; hlog?.Write(L.T(what + " 쓰기 회복(" + Path + ")", what + ": write recovered (" + Path + ")")); }
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    try { fs?.Dispose(); } catch (Exception) { }
                    fs = null;
                    if (!failing)
                    {
                        failing = true;
                        hlog?.WriteThrottled("safefile|" + Path, L.T(what + " 쓰기 실패(" + Path + "): " + e.GetType().Name + ": " + e.Message + " — 다시 시도(대기열 " + q.Count + "줄)",
                                                                     what + ": write failed (" + Path + "): " + e.GetType().Name + ": " + e.Message + " — retrying (queue " + q.Count + " lines)"));
                    }
                    long left = deadline - Environment.TickCount64;
                    if (left <= 0) return;
                    Thread.Sleep((int)Math.Min(delay, left));
                    delay = Math.Min(delay * 2, 400);
                }
            }
        }

        void Drop(byte[] b, string why)
        {
            dropped++;
            string t = Utf8.GetString(b).TrimEnd('\r', '\n');
            if (perLine) hlog?.Write(L.T(what + " 버림(" + why + ", " + Path + "): " + t, what + ": dropped (" + why + ", " + Path + "): " + t));
            else firstDrop ??= t.Length > 300 ? t.Substring(0, 300) + "…" : t;
        }
    }

    /// <summary>
    /// 포트 하나의 사건 판정·기록. 줄은 추적 스레드(EventDetector)나 시험이 넣고, 시각(wall·mono)도 넣는 쪽이 준다(시험은 결정적).
    /// 한 스레드에서만 부른다(잠금 없음). 파일 쓰기 실패는 SafeFile 이 다시 시도하고 hub.log 에 남긴다(기록 코어·다른 판정을 멈추지 않는다).
    /// 판정(부록 C.7): 로더 표시(loader_marker_re — 제품 프로필·config, 없으면 판정 안 함)는 줄 어디에 있든 실시간 리셋. 명령 출력 구간(송신 CR ~ 진짜 프롬프트·실시간 부팅·분리) 안의
    /// 커널·앱 사건 줄은 모두 suspect:"cmd_output"(커널 시각으로 실시간이라 받아들이지 않는다). 구간 밖은 실시간 — 단 이 실행에서 지금 부팅의 로더 표시를 봤으면
    /// 커널 시각이 로더 뒤 경과와 크게 어긋나는 줄은 suspect(거르기만). 재부팅 분류는 실시간 부팅 순간의 되돌아보기로만(부팅 사이에 쌓는 상태 없음).
    /// suspect 는 부팅 상태를 바꾸지 않고 풍선을 띄우지 않는다 — suspect panic/oops 는 뒤따르는 실시간 리셋(cause)이나 30초 무응답(confirm)이 확인한다.
    /// </summary>
    public sealed class EventScanner
    {
        public const int BeforeLines = 200, AfterLines = 100;
        public const double AfterS = 30, CmdWindowS = 60;
        const double EchoS = 2, KernelFirstS = 15, GapResetS = 30;
        const double MarkerMergeS = 1, QuietBootS = 1, MarkerQuietS = 0.5, SoftCloseS = 10, CauseS = 20, SoftS = 2, ConfirmBootS = 60, ConfirmQuietS = 30, KtsAheadS = 5, KtsBackS = 2,
                     ArmSpanS = 5, ArmSlackS = 2;
        const int MaxLineChars = 500;
        static readonly Regex RestartRe = new Regex(@"reboot: Restarting system", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex KtsRe = new Regex(@"^\[\s*(\d+\.\d+)\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        // 확실한 재출력: 시리얼 콘솔은 printk 수준 접두 <N> 를 찍지 않는다 — dmesg-ramoops·/proc/kmsg·grep -n 출력(앞에 '줄번호:'·'파일:' 가 붙을 수 있음)
        static readonly Regex LevelPrefixRe = new Regex(@"(?:^|:)(?:\d+[:-])?<[0-7]>\[\s*\d+\.\d+\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        // 송신 줄의 reboot: 명령 자리(줄 처음·; & | ( ` $( 뒤·sh -c '…'·eval '…')에서만. busybox·sudo·경로 앞붙이 허용, 'echo reboot'·'grep reboot' 은 아님
        static readonly Regex TxCmdRe = new Regex(@"(?:^|[;&|(`]|\$\(|\b(?:sh|bash|ash)\s+-c\s+|\beval\s+)\s*['""]?\s*(?:sudo\s+)?(?:busybox\s+)?(?:/[\w./-]*/)?reboot(?=$|[\s;&|)'""`])",
                                                  RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex ResetRe = new Regex(@"^\s*reset\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        // 수신 에코(프롬프트 뒤 명령): reboot, U-Boot 프롬프트면 reset 도
        static readonly Regex RxRebootRe = new Regex(@"^\s*(?:sudo\s+)?(?:busybox\s+)?(?:[\w./-]*/)?reboot(?:\s|$)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        // 일부러 일으키는 리셋·충돌 명령(W7): 그 출력은 덤프가 아니라 지금 일어나는 일 — 명령 출력 구간을 열지 않는다(구간 밖으로 판정)
        static readonly Regex TriggerSegRe = new Regex(@"^(?:sudo\s+)?(?:busybox\s+)?(?:/[\w./-]*/)?reboot(?:\s+-\w+)*$|>\s*/proc/sysrq-trigger\b|/sys/kernel/debug/provoke-crash/|\blkdtm\b|>\s*/proc/\S*provoke",
                                                        RegexOptions.Compiled | RegexOptions.CultureInvariant);
        // 덤프하지 않는 곁 명령(sync·echo·printf·date·sleep·mount·cd·true) — 이것과 위 명령만으로 된 줄이 trigger 줄
        static readonly Regex BenignSegRe = new Regex(@"^(?:sync|true|date(?:\s.*)?|sleep\s+[\d.]+|echo(?:\s.*)?|printf(?:\s.*)?|mount(?:\s.*)?|cd(?:\s.*)?)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex ShCRe = new Regex(@"^(?:sh|bash|ash)\s+-c\s+(['""])(.*)\1(?:\s*<\s*\S+)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        readonly string dir;
        readonly string txtDir;               // log_layout 2: 에이징 이벤트 txt 뿌리(<기준>\에이징이벤트). null = Phase A(앞뒤 로그를 jsonl 옆 <날짜> 폴더에)
        readonly bool flatTxt;                // 에이징 세션(부록 C.12): txtDir = <세션 폴더>\이벤트, 이름 <HHMMSS>_<종류>.txt(날짜 폴더·보드·포트 없음)
        readonly string lang;                 // txt 이름·글의 언어(세션 = 세션 언어, 그 밖 = 만들 때의 지금 언어) — jsonl·hub.log 는 지금 언어(L.T)
        readonly HashSet<string> fileKinds;   // log_layout 2 에서 txt 를 만드는 종류(알림 종류 ∩ 커널·앱 오류)
        string com;                           // 마지막 연결의 COM(txt 머리)
        readonly int port;
        readonly string cable;
        readonly Func<string> board, logPath;
        readonly EventPatterns pats;
        readonly Regex bootRe, loaderRe, shellPromptRe, ubootPromptRe;
        readonly int bootMergeMs;
        readonly HubLog hlog;
        readonly EventsBudget budget;
        readonly SafeFile jsonl;
        readonly List<SafeFile> closing = new List<SafeFile>();

        // 부팅(실시간 부팅만 바꾼다)
        long bootSeq;
        DateTime? bootWall;                   // 지금 부팅의 시작(벽시계, jsonl 에서 이어 받음, 긴 공백 뒤엔 모름)
        long bootMono = long.MinValue;        // 이 실행에서 본 지금 부팅의 시작(단조)
        long loaderMono = long.MinValue;      // 지금 부팅을 연 로더 표시(이 실행에서 봤을 때만) — 구간 밖 커널 시각 거르기의 기준
        long markerMono = long.MinValue;      // 마지막 로더 표시(1초 안의 다음 표시는 같은 부팅)
        double bootMaxKts = -1;               // 이 부팅에서 실시간으로 본 커널 시각 최대값(로더 뒤 커널 첫 줄 병합)
        double lastLiveKts = -1;              // 이 부팅에서 구간 밖 실시간 줄의 가장 큰 커널 시각(커널 시계는 뒤로 가지 않는다 — 절전 뒤에도)
        bool ktsArmed;                        // 단조 검사를 켰나 — 실시간 부팅을 봤거나, 붙은 뒤 구간 밖 두 줄이 5초 넘게 떨어져 서로 맞음(W3)
        (double Kts, long Mono)? armCand;     // 켜기 후보(붙은 뒤 구간 밖 첫 커널 줄)
        bool lastKernelRestart;               // 마지막 커널 줄(커널 시각 또는 Restarting)이 Restarting 이었나 — 진짜 프롬프트 뒤엔 지움(soft 판정, BLK-2)
        long lastKernelMono = long.MinValue;
        bool lastPartialText;                 // 마지막 수신 줄이 커널 줄이 아닌 열린 줄(페이저 --More--·(END)·':' 등 — 사람이 읽는 중, confirm 하지 않음, W1)
        string typedBuf = "";                 // 지금 치는 중인 송신 글자(CR 전) — 사람이 치는 줄의 에코만 typing 으로 본다(BLK-1)
        long lastRxMono = long.MinValue;      // 마지막 수신 줄(무음 길이)
        long lastTxMono = long.MinValue;      // 마지막 송신 바이트(구간 무음 판단)
        // 되돌아보기(실시간 부팅마다 비운다 — 부팅 사이에 쌓는 상태 없음)
        readonly List<long> rebootCmds = new List<long>();              // 명령 자리의 reboot 송신·에코, U-Boot reset
        readonly List<long> restarts = new List<long>();                // 'reboot: Restarting system' 줄(실시간·suspect)
        readonly List<(long Mono, string Kind, string Line, DateTime Wall, string Context, bool Suspect)> errs = new List<(long, string, string, DateTime, string, bool)>();   // panic·oops 줄
        (long Mono, string Kind, string Line, DateTime Wall, string Context)? pending;   // 확인을 기다리는 suspect panic/oops
        // 셸·에코
        long txLineMono = long.MinValue;
        readonly List<(string Text, long Mono)> recentTx = new List<(string, long)>();
        bool lastPromptUboot;
        bool promptPending;                   // 개행 없이 끝난(tail flush) 맨 프롬프트 뒤 — 다음 줄이 명령 에코
        bool typing;                          // 그 뒤 개행 없이 끊긴 줄들(사람이 치는 중)
        bool echoDue;                         // 송신 CR 뒤 아직 그 명령의 에코 줄을 못 봄(송신 CR 하나에 에코 하나)
        bool txEdited;                        // 마지막 송신 줄에 화살표(ESC)·Tab·Ctrl-P/N 이 있었나 — 셸이 다시 그린 명령
        // 명령 출력 구간(C.7) — softClosed = 10초 무음으로 닫힘(그 뒤 송신 바이트 하나면 다시 열림, 페이저 키 등)
        bool region, softClosed;
        long regionBootMono = long.MinValue;  // 이 구간에서 낸 suspect 부팅(같은 출력의 부팅 줄 여러 개를 하나로)

        // 앞뒤 로그·예산
        readonly Queue<(DateTime Wall, string Text)> before = new Queue<(DateTime, string)>();
        readonly List<Ctx> open = new List<Ctx>();
        readonly HashSet<string> usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, List<long>> filesByKind = new Dictionary<string, List<long>>(StringComparer.Ordinal);   // 이 부팅에서
        readonly HashSet<string> exemptUsed = new HashSet<string>(StringComparer.Ordinal);                                   // 이 부팅에서 쓴 면제(첫 panic·oops)
        readonly List<long> filesHour = new List<long>();
        readonly Dictionary<string, int> skippedSince = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly Dictionary<string, string> skipState = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>앞뒤 로그 예산(시험이 줄일 수 있다): 한 부팅 안 같은 종류 1시간 무료 개수·그 뒤 간격(초)·포트 1시간 상한(면제 제외).</summary>
        public int FreeFilesPerKindHour = 3, MaxFilesPerPortHour = 30;
        public double KindRateS = 600;
        /// <summary>log_layout 2 txt 의 결과 판정 창(초): 마지막 사건 줄 뒤 이 안에 실시간 부팅이 오면 재부팅, 아니면 무음 여부로 응답 없음·계속 동작.</summary>
        public double ResultS = 60;
        readonly List<Ctx> waiting = new List<Ctx>();                    // 앞뒤 로그는 닫았고 결과를 기다리는 txt(log_layout 2)

        sealed class Ctx
        {
            public string Path;               // null = 예산 때문에 파일 없이 묶기만(context_skipped)
            public SafeFile File;
            public string Skipped;
            public int After, Marked, SkippedBefore;
            public long StartMono, BootSeq;
            public readonly List<string> Kinds = new List<string>();
            // log_layout 2 txt: 묶음의 대표(가장 심한) 종류·의심 여부·원인 줄, 다시 쓰기용 앞 줄·이후 줄, 마지막 사건 줄 시각, 결과 줄
            public string Kind, Line;
            public bool Suspect;
            public DateTime Wall, NameWall;
            public double? SinceBoot;
            public string LogPath, BeforeText;
            public StringBuilder AfterText;
            public long LastMono;
            public string Result;
        }

        /// <param name="board">지금 보드명(없으면 null). <paramref name="logPath"/> 지금 기록 중인 .log(없으면 null). budget = 폴더 크기 예산(없으면 무제한).</param>
        /// <param name="txtDir">log_layout 2 의 에이징 이벤트 txt 뿌리(null = Phase A 배치 — 앞뒤 로그를 eventsDir\&lt;날짜&gt; 에).</param>
        /// <param name="flatTxt">에이징 세션의 이벤트 폴더(txtDir = &lt;세션 폴더&gt;\이벤트): 이름 &lt;HHMMSS&gt;_&lt;종류&gt;[_의심].txt, 날짜 폴더 없음(부록 C.12).</param>
        /// <param name="lang">txt 이름·글의 언어("ko"|"en" — 세션이면 세션 언어). null = 지금 언어(만들 때 정해 바꾸지 않는다).</param>
        public EventScanner(string eventsDir, int port, string cable, Func<string> board, Func<string> logPath, EventPatterns pats, Config cfg, HubLog hlog, EventsBudget budget = null, string txtDir = null, bool flatTxt = false,
                            string lang = null)
        {
            dir = eventsDir; this.txtDir = txtDir; this.flatTxt = flatTxt && txtDir != null; this.port = port;
            this.lang = lang == L.En || lang == L.Ko ? lang : L.Current;
            fileKinds = new HashSet<string>((cfg.Events?.Notify ?? (IEnumerable<string>)EventKinds.NotifyDefault).Where(EventKinds.IsError), StringComparer.Ordinal); this.cable = cable; this.board = board ?? (() => null); this.logPath = logPath ?? (() => null);
            this.pats = pats; this.hlog = hlog; this.budget = budget;
            bootRe = new Regex(cfg.BootRe, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
            loaderRe = string.IsNullOrEmpty(cfg.LoaderMarkerRe) ? null : new Regex(cfg.LoaderMarkerRe, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));   // 없음 = 로더 표시 없는 보드
            shellPromptRe = string.IsNullOrEmpty(cfg.PromptRe) ? null : new Regex(cfg.PromptRe, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
            ubootPromptRe = string.IsNullOrEmpty(cfg.UbootPromptRe) ? null : new Regex(cfg.UbootPromptRe, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
            bootMergeMs = cfg.BootMergeMs;
            jsonl = new SafeFile(JsonlPath, L.T("사건 기록 :" + port, "event record :" + port), hlog, budget, true);
            LoadState();
        }

        public string JsonlPath => System.IO.Path.Combine(dir, port.ToString(CultureInfo.InvariantCulture) + ".jsonl");
        /// <summary>txt 이름·글의 언어("ko"|"en").</summary>
        public string Lang => lang;
        /// <summary>txt 에 쓰는 글(이 판정기의 언어 — 세션이면 세션 언어).</summary>
        string Tx(string ko, string en) => AgingLang.T(lang, ko, en);
        public long BootSeq => bootSeq;
        /// <summary>jsonl 의 마지막 기록 시각·종류(이 판정기를 만들 때 읽은 것, 없으면 null).</summary>
        public DateTimeOffset? LastRecordTs { get; private set; }
        public string LastRecordKind { get; private set; }
        /// <summary>이 판정기가 쓴 기록 수(시험·진단용).</summary>
        public int Emitted { get; private set; }
        /// <summary>앞뒤 로그를 아직 모으는 묶음 수(파일 없이 묶기만 하는 것 포함).</summary>
        public int OpenContexts => open.Count;
        /// <summary>명령 출력 구간인가.</summary>
        public bool InRegion => region;
        /// <summary>확인을 기다리는 suspect panic/oops 가 있나.</summary>
        public bool PendingConfirm => pending.HasValue;

        // ================================================================== 입력
        /// <summary>
        /// 송신 줄(사람·Claude·.tx 가 보낸 CR 까지의 글자 — 빈 Enter·화살표로 부른 줄도). 명령 출력 구간을 연다(C.7: CR 이면 언제나).
        /// 명령 자리의 reboot(또는 U-Boot 프롬프트의 reset)이면 되돌아보기에 남긴다. edited = 그 줄에 화살표(ESC)·Tab·Ctrl-P/N 이 있었음.
        /// </summary>
        public void OnTxLine(string text, long mono, bool edited = false)
        {
            text ??= "";
            SoftCheck(mono);
            lastTxMono = mono;
            softClosed = false;
            txLineMono = mono;
            recentTx.Add((text, mono));
            while (recentTx.Count > 16) recentTx.RemoveAt(0);
            echoDue = true;
            txEdited = edited;
            typedBuf = "";
            if (TxCmdRe.IsMatch(text) || (lastPromptUboot && ResetRe.IsMatch(text))) Note(rebootCmds, mono);
            if (IsTrigger(text)) { region = softClosed = false; return; }                 // 일부러 일으키는 리셋·충돌: 출력은 지금 일 — 구간 밖으로 판정(W7)
            if (!region) { region = true; regionBootMono = long.MinValue; }
        }

        /// <summary>
        /// 리셋·충돌을 일으키는 명령 줄인가(W7): reboot, echo … &gt; /proc/sysrq-trigger, /sys/kernel/debug/provoke-crash/*, lkdtm, /proc/…provoke… 가 있고
        /// 나머지는 덤프하지 않는 곁 명령(sync·echo·printf·date·sleep·mount·cd·true, sh -c '…' 안도)뿐인 줄. cat·dmesg·tail 이 끼면 아니다.
        /// </summary>
        static bool IsTrigger(string text)
        {
            bool any = false;
            bool Walk(string t)
            {
                foreach (var raw in Regex.Split(t, @"\s*(?:;|&&|\|\||\|)\s*"))
                {
                    string seg = Regex.Replace(raw.Trim(), @"\s+\d?>\s*/dev/null\s*$", "").Trim();
                    if (seg.Length == 0) continue;
                    var sh = ShCRe.Match(seg);
                    if (sh.Success) { if (!Walk(sh.Groups[2].Value)) return false; continue; }
                    if (TriggerSegRe.IsMatch(seg)) { any = true; continue; }
                    if (!BenignSegRe.IsMatch(seg)) return false;
                }
                return true;
            }
            return Walk(text ?? "") && any;
        }

        /// <summary>
        /// 송신 바이트(CR 없는 글자·페이저 키 포함, 송신 줄과 따로 — 추적 스레드는 송신 기록마다 부른다). 10초 무음으로 닫힌 구간(soft)은 다시 연다 —
        /// 'dmesg | more' 에서 스페이스·q 를 누르면 다음 쪽의 옛 줄도 구간 안. 진짜 프롬프트·실시간 부팅·분리로 닫힌 구간은 열지 않는다(CR 이 연다).
        /// </summary>
        public void OnTxBytes(long mono, string typed = null)
        {
            SoftCheck(mono);
            lastTxMono = mono;
            if (typed != null) typedBuf = typed;
            if (softClosed) { softClosed = false; region = true; regionBootMono = long.MinValue; }
        }

        /// <summary>구간이 열린 채 수신·송신 모두 10초 무음이면 닫는다(soft — 전경 프로그램·멈춘 보드·follow 명령의 지난 줄 뒤).</summary>
        void SoftCheck(long mono)
        {
            long last = Math.Max(lastRxMono, lastTxMono);
            if (region && last != long.MinValue && mono - last >= SoftCloseS * 1000) { region = false; softClosed = true; }
        }

        /// <summary>
        /// 연결(허브 시작·케이블 다시 연결) 기록(M4): kind "attach" 한 줄 — com, reason, gap_s(그 전 기록 공백, 모르면 null)·gap_from.
        /// 공백이 30초를 넘으면 그 사이 재부팅했을 수 있으므로 부팅 시작 시각을 모름으로 둔다. 셸 상태·되돌아보기는 지운다.
        /// </summary>
        public void OnAttach(string reason, string com, double? gapS, DateTimeOffset? gapFrom, DateTime wall, long mono, string rxOff)
        {
            CloseExpired(mono);
            if (gapS.HasValue && gapS.Value > GapResetS) { bootWall = null; bootMono = loaderMono = long.MinValue; bootMaxKts = lastLiveKts = -1; }
            ktsArmed = false; armCand = null;                                                // 붙은 뒤엔 단조 검사를 바로 켜지 않는다(W3)
            ResetLookBack();
            ResetShell();
            lastRxMono = lastTxMono = long.MinValue;
            if (com != null) this.com = com;
            string line = L.T((reason == "reattach" ? "케이블 다시 연결" : reason == "session" ? "에이징 세션 시작" : reason == "session_end" ? "에이징 세션 끝" : "허브가 이 케이블 기록 시작") + " " + (com ?? "")
                              + (reason == "session" || reason == "session_end" ? "" : gapS.HasValue ? " — 그 전 기록 공백 " + gapS.Value.ToString("0.0", CultureInfo.InvariantCulture) + "초" : " — 그 전 기록 없음"),
                              (reason == "reattach" ? "Cable reconnected" : reason == "session" ? "Aging started" : reason == "session_end" ? "Aging ended" : "Hub started recording this cable") + " " + (com ?? "")
                              + (reason == "session" || reason == "session_end" ? "" : gapS.HasValue ? " — gap before: " + gapS.Value.ToString("0.0", CultureInfo.InvariantCulture) + " s" : " — no earlier record"));
            Emit("attach", null, line, wall, mono, rxOff, null, false, o =>
            {
                o.Set("com", com).Set("reason", reason);
                if (gapS.HasValue) o.Set("gap_s", Math.Round(gapS.Value, 1)); else o.Null("gap_s");
                if (gapFrom.HasValue) o.Set("gap_from", gapFrom.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture)); else o.Null("gap_from");
            });
        }

        /// <summary>분리(케이블 뽑힘·제외·보오 바꿈) 기록 kind "detach" — 다시 붙거나 허브가 다시 시작될 때까지가 기록 공백(D8). 구간을 닫는다.</summary>
        public void OnDetach(string reason, DateTime wall, long mono, string rxOff)
        {
            CloseExpired(mono);
            ResetShell();
            pending = null;
            Settle(c => c.Result ??= Tx("결과: 케이블이 분리되어 확인하지 못했습니다.", "Result: not checked, the cable was disconnected."));
            Emit("detach", null, L.T("케이블 분리 — 이 뒤 다시 붙을 때까지 기록 없음" + (reason != null ? " (" + reason + ")" : ""),
                                     "Cable disconnected — no record until it is connected again" + (reason != null ? " (" + reason + ")" : "")), wall, mono, rxOff, null, false, o => o.Set("reason", reason));
        }

        void ResetShell()
        {
            region = softClosed = typing = promptPending = echoDue = false;
        }

        void ResetLookBack()
        {
            rebootCmds.Clear(); restarts.Clear(); errs.Clear(); pending = null;
        }

        static void Note(List<long> l, long mono)
        {
            l.Add(mono);
            l.RemoveAll(t => mono - t > 2 * CmdWindowS * 1000);
        }

        /// <summary>
        /// 수신 줄 하나. segs = \r 로 나눈 구간(ANSI·NUL 을 지운 UTF-8, 빈 구간 없음), display = 구간을 이은 글자.
        /// partial = 개행 없이 끝난 줄(tail flush — RX 가 조용해 열린 채 보낸 줄). rxOff = 줄 시작의 epoch:offset.
        /// </summary>
        public void OnLine(string display, IReadOnlyList<string> segs, DateTime wall, long mono, string rxOff, bool partial = false)
        {
            CloseExpired(mono);
            string first = segs.Count > 0 ? segs[0] : "";
            double? kts = null;
            var km = KtsRe.Match(first);
            if (km.Success && double.TryParse(km.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double kv)) kts = kv;
            SoftCheck(mono);
            long prevRx = lastRxMono;
            lastRxMono = mono;

            bool prompt = PromptRemainder(display, out string after, out bool uboot);
            bool wasPending = promptPending, wasTyping = typing;
            bool txCr = txLineMono != long.MinValue && mono >= txLineMono && mono - txLineMono <= EchoS * 1000;
            // 로더 표시·커널 시각 줄은 에코·치는 중으로 보지 않는다(BLK-1: 유휴 프롬프트 뒤 열린 채 끊긴 '<로더 표시>###%%> ' 가 typing 으로 버려졌다)
            bool kernelish = kts.HasValue || AnyMatch(segs, loaderRe);
            bool echo = !kernelish && IsEcho(display, prompt, after, mono, partial && (wasPending || wasTyping));
            // 화살표·Tab·Ctrl-P 가 든 송신 CR 2초 안의 첫 명령 줄(프롬프트 + 글자, 또는 열린 프롬프트 다음 줄) = 셸이 다시 그린 그 명령의 에코.
            // 송신 CR 하나에 에코 하나: 그 뒤 출력 속 '프롬프트 + reboot' 줄은 에코가 아니다(R8)
            bool recalled = !echo && !kernelish && echoDue && txEdited && txCr && !partial && (prompt ? after.Trim().Length > 0 : wasPending && display.Trim().Length > 0);
            if (recalled) echo = true;
            bool cmdEcho = echo && echoDue && txCr && (prompt || wasPending);
            if (cmdEcho) echoDue = false;
            bool realPrompt = prompt && partial;                                              // 개행 없이 열린 채 조용한 프롬프트 = 셸이 입력을 기다림
            bool replay = !echo && segs.Any(s => LevelPrefixRe.IsMatch(s));                    // 확실한 재출력 — 판정하지 않음
            bool judged = !echo && !replay;
            bool marker = judged && AnyMatch(segs, loaderRe);                                  // 로더 표시: 줄 어디에 있든(앞에 커널 시각·깨진 글자가 있어도, B5)
            bool restart = judged && !marker && AnyMatch(segs, RestartRe);
            bool kboot = judged && !marker && !restart && AnyMatch(segs, bootRe);              // 커널 부팅 줄(로더 표시 없는 보드의 부팅 경계)
            // 앞 무음 = 마지막 수신 줄 뒤(송신은 세지 않는다 — 멈춘 보드에 사람이 키를 누르는 동안 난 진짜 리셋, 실보드 로그 두 건)
            double quietS = prevRx == long.MinValue ? double.MaxValue : (mono - prevRx) / 1000.0;
            // 로더 표시: 구간 밖이면 언제나 실시간, 구간 안이면 앞 수신 무음 0.5초 이상일 때만(실제 리셋 225번 모두 0.625초 이상 — 덤프된 캡처 로그 속 로더 표시는 suspect, N2b)
            bool live = judged && (marker ? !region || quietS >= MarkerQuietS
                                          : realPrompt || (region ? kboot && quietS >= QuietBootS : !KtsOff(kts, mono, kboot)));
            bool suspect = judged && !live;
            if (realPrompt)                                                                   // 셸이 살아 있음 = 앞의 suspect panic·덤프 속 Restarting 은 리셋 원인이 아니다
            {
                region = softClosed = false; lastPromptUboot = uboot; pending = null;
                errs.RemoveAll(e => e.Suspect);
                restarts.Clear(); lastKernelRestart = false;
            }
            else if (prompt && !echo && !region) lastPromptUboot = uboot;

            string kind = null;
            if (judged)
            {
                if (restart && live) Note(restarts, mono);                                    // 실시간 Restarting 만(BLK-2)
                if (marker && live)
                {
                    if (markerMono == long.MinValue || mono - markerMono > MarkerMergeS * 1000) StartBoot(display, wall, mono, rxOff, true, prevRx);
                    markerMono = mono;
                }
                else if (kboot && live && IsNewBoot(kts, mono)) StartBoot(display, wall, mono, rxOff, false, prevRx);
                else if ((marker || kboot) && suspect && (regionBootMono == long.MinValue || mono - regionBootMono >= bootMergeMs)) SuspectBoot(display, wall, mono, rxOff);
                if (live && kts.HasValue && !marker)
                {
                    if (kts.Value > bootMaxKts) bootMaxKts = kts.Value;
                    if (!region) Arm(kts.Value, mono);
                }
                if ((kts.HasValue || restart) && !marker) { lastKernelRestart = restart; lastKernelMono = mono; }
                kind = pats.Match(segs, PatternTimeout);
            }
            lastPartialText = partial && !kernelish && !realPrompt;

            // ---- 앞뒤 로그: 이미 열린 묶음에 뒤 줄로(사건 줄은 >>>), 새 사건이면 새 묶음(예산 안이면 파일).
            // 같은 부팅·30초 안의 같은 종류(실시간·suspect 따로)는 이어진 줄로 묶어 사건 한 번 — suspect 사건이 실시간 사건을 삼키지 않게 둘을 나눈다.
            Ctx target = kind != null ? open.LastOrDefault(x => x.BootSeq == bootSeq) : null;
            foreach (var c in open) { WriteLine(c, kind != null, wall, display); c.After++; }
            string ctxPath = null;
            if (kind != null)
            {
                string key = suspect ? kind + "(suspect)" : kind;
                bool exempt = live && (kind == "panic" || kind == "oops") && !exemptUsed.Contains(kind);
                if (target != null)
                {
                    target.Marked++;
                    target.LastMono = mono;
                    ctxPath = target.Path;
                    if (!target.Kinds.Contains(key))
                    {
                        if (exempt) exemptUsed.Add(kind);
                        target.Kinds.Add(key);
                        string skip = null, was = null;
                        bool promoted = target.Path == null && (txtDir == null || fileKinds.Contains(kind))
                                        && Promote(target, kind, suspect, display, wall, mono, rxOff, exempt, out skip);   // D5: 묶음의 skipped 를 물려받지 않음
                        if (!promoted && txtDir != null && target.Path != null && fileKinds.Contains(kind) && Worse(kind, suspect, target)) was = Upgrade(target, kind, suspect, display, wall, mono);
                        ctxPath = target.Path;
                        EmitError(kind, display, wall, mono, rxOff, target.Path, skip, promoted ? target.SkippedBefore : 0, suspect, was);
                    }
                }
                else
                {
                    if (exempt) exemptUsed.Add(kind);
                    var c = NewCtx(key, kind, suspect, display, wall, mono, rxOff, true, exempt);
                    ctxPath = c.Path;
                    EmitError(kind, display, wall, mono, rxOff, c.Path, c.Skipped, c.SkippedBefore, suspect);
                }
                if (kind == "panic" || kind == "oops")
                {
                    errs.Add((mono, kind, display, wall, ctxPath, suspect));
                    errs.RemoveAll(e => mono - e.Mono > ConfirmBootS * 1000);
                    if (suspect && !pending.HasValue) pending = (mono, kind, display, wall, ctxPath);
                }
            }
            foreach (var c in open.Where(x => x.After >= AfterLines).ToList()) CloseCtx(c, Tx("뒤 " + AfterLines + "줄 채움", AfterLines + " lines after collected"));

            before.Enqueue((wall, display));
            while (before.Count > BeforeLines) before.Dequeue();

            // ---- 수신 에코로 reboot 명령(화살표로 다시 부른 명령·Tab 완성 — 송신 줄에 글자가 없음): 송신 CR 2초 안의 그 명령 에코 줄만(L2·R8)
            if (cmdEcho && ((prompt && CmdRx(after, uboot)) || (!prompt && CmdRx(display, lastPromptUboot)))) Note(rebootCmds, mono);
            promptPending = partial && prompt && after.Trim().Length == 0;
            typing = partial && !prompt && (wasPending || wasTyping);
        }

        /// <summary>링 버퍼를 놓침(Lost): missed 사건 한 줄, 열린 앞뒤 로그에 표시.</summary>
        public void OnLost(long skipped, DateTime wall, long mono, string rxOff)
        {
            CloseExpired(mono);
            string text = Tx("*** 링 버퍼 놓침 " + skipped.ToString(CultureInfo.InvariantCulture) + "바이트 — 이 사이 줄은 검사·기록하지 못함 ***",
                             "*** Ring buffer missed " + skipped.ToString(CultureInfo.InvariantCulture) + " bytes — lines in this span were not checked or recorded ***");
            foreach (var c in open) Append(c, "    " + Stamp(wall) + text + "\r\n");
            promptPending = typing = false;
            Emit("missed", null, Tx("링 버퍼를 놓쳐 " + skipped.ToString(CultureInfo.InvariantCulture) + "바이트를 검사하지 못함",
                                    "Ring buffer missed: " + skipped.ToString(CultureInfo.InvariantCulture) + " bytes not checked"), wall, mono, rxOff, null, false, o => o.Set("skipped", skipped));
        }

        /// <summary>시간만 흐름(줄 없음): 30초 지난 앞뒤 로그를 닫고, suspect panic 뒤 30초 무응답이면 confirm, 못 쓴 줄을 다시 쓴다.</summary>
        public void Tick(DateTime wall, long mono)
        {
            CloseExpired(mono);
            if (pending.HasValue && lastRxMono != long.MinValue && mono - lastRxMono >= ConfirmQuietS * 1000 && mono - pending.Value.Mono >= ConfirmQuietS * 1000
                && !lastPartialText && !(lastTxMono != long.MinValue && lastTxMono > pending.Value.Mono))   // 페이저에서 읽는 중·그 뒤 키를 친 경우는 멈춤 아님(W1)
            {
                var p = pending.Value;
                pending = null;
                double quiet = Math.Round((mono - lastRxMono) / 1000.0, 1);
                Emit("confirm", null, p.Line, wall, mono, null, p.Context, false, o => o.Set("of", p.Kind).Set("suspect_ts", EventLog.Ts(p.Wall)).Set("silence_s", quiet));
                if (p.Context != null)
                    Settle(c => { if (c.Result == null && string.Equals(c.Path, p.Context, StringComparison.OrdinalIgnoreCase)) c.Result = Tx("결과: 응답 없음. " + quiet.ToString("0", CultureInfo.InvariantCulture) + "초 동안 출력이 없어 보드가 멈췄을 수 있습니다.",
                                                                                                                                   "Result: no response. No output for " + quiet.ToString("0", CultureInfo.InvariantCulture) + " s; the board may have hung."); });
            }
            if (txtDir != null)                                                                  // 결과 창이 지난 txt: 무음이면 응답 없음, 아니면 계속 동작
                Settle(c =>
                {
                    if (c.Result != null || open.Contains(c) || mono - c.LastMono < ResultS * 1000) return;
                    if (pending.HasValue && string.Equals(pending.Value.Context, c.Path, StringComparison.OrdinalIgnoreCase)) return;   // confirm 이 판정한다
                    double quietS = lastRxMono == long.MinValue ? double.MaxValue : (mono - lastRxMono) / 1000.0;
                    c.Result = c.Kind == "panic" && quietS >= ConfirmQuietS                       // panic 뒤 조용함 = 멈춤(Oops·앱 오류 뒤 조용한 프롬프트는 정상일 수 있다)
                        ? Tx("결과: 응답 없음. " + Math.Min(quietS, 9999).ToString("0", CultureInfo.InvariantCulture) + "초 동안 출력이 없어 보드가 멈췄을 수 있습니다.",
                             "Result: no response. No output for " + Math.Min(quietS, 9999).ToString("0", CultureInfo.InvariantCulture) + " s; the board may have hung.")
                        : Tx("결과: 계속 동작. " + ResultS.ToString("0", CultureInfo.InvariantCulture) + "초 안에 재부팅이 없었습니다.",
                             "Result: kept running. No reboot within " + ResultS.ToString("0", CultureInfo.InvariantCulture) + " s.");
                });
            jsonl.Retry();
            foreach (var c in open) c.File?.Retry();
            foreach (var f in closing.ToList())
            {
                f.Retry();
                if (!f.Pending) { f.Close(0); closing.Remove(f); }
            }
        }

        /// <summary>끝(허브 정지 등): 열린 앞뒤 로그를 모두 닫고 남은 쓰기를 2초까지 마저 한다(못 쓴 줄은 hub.log 에).</summary>
        public void Close(string why)
        {
            Settle(c => c.Result ??= Tx("결과: 기록이 끝나 확인하지 못했습니다(" + why + ").", "Result: not checked, recording ended (" + why + ")."));
            foreach (var c in open.ToList()) CloseCtx(c, why);
            foreach (var f in closing.ToList()) f.Close(1000);
            closing.Clear();
            jsonl.Close(2000);
        }

        // ================================================================== 판정 도우미
        /// <summary>
        /// 구간 밖 커널 시각 거르기(C.7 4 — 실시간을 suspect 로만 바꾼다, 부팅 스크립트가 pstore 를 찍는 등):
        /// 앞으로 — 이 실행에서 지금 부팅의 로더 표시를 봤고 커널 시각이 로더 뒤 경과 + 5초보다 큼(절전 뒤엔 커널 시각이 벽시계보다 늦으므로 안전).
        /// 뒤로 — 이 부팅에서 구간 밖 실시간 줄의 가장 큰 커널 시각보다 2초 넘게 작음(커널 시계는 절전 중 멈추되 뒤로 가지 않는다 — S1 절전·깨어남 반복에도 실시간 유지).
        /// 로더 표시를 못 본 부팅의 커널 부팅 줄(로더 없는 보드의 새 커널)은 뒤로 검사를 하지 않는다.
        /// </summary>
        bool KtsOff(double? kts, long mono, bool kboot)
        {
            if (!kts.HasValue) return false;
            if (loaderMono != long.MinValue && kts.Value > (mono - loaderMono) / 1000.0 + KtsAheadS) return true;
            return ktsArmed && lastLiveKts >= 0 && kts.Value < lastLiveKts - KtsBackS && !(kboot && loaderMono == long.MinValue);
        }

        /// <summary>
        /// 구간 밖 실시간 커널 줄로 단조 기준을 올린다. 붙은 뒤(실시간 부팅 전)엔 두 줄이 5초 넘게 떨어져 커널 시각 차 ≈ 경과(±2초)일 때 켠다 —
        /// 붙자마자 본 줄이 덤프 속 옛 줄이어도 기준이 굳지 않게(W3).
        /// </summary>
        void Arm(double kts, long mono)
        {
            if (ktsArmed) { if (kts > lastLiveKts) lastLiveKts = kts; return; }
            if (armCand.HasValue && mono - armCand.Value.Mono >= ArmSpanS * 1000)
            {
                double dk = kts - armCand.Value.Kts, dm = (mono - armCand.Value.Mono) / 1000.0;
                if (Math.Abs(dk - dm) <= ArmSlackS) { ktsArmed = true; lastLiveKts = kts; return; }
                armCand = (kts, mono);
            }
            else if (!armCand.HasValue) armCand = (kts, mono);
        }

        bool IsNewBoot(double? kts, long mono)
        {
            if (bootMono == long.MinValue) return true;
            if (mono - bootMono < bootMergeMs) return false;                                         // 같은 부팅의 경계 매치(첫 매치 기준 — DiskWriter 와 같은 창)
            if (kts.HasValue && bootMaxKts < 1.0 && mono - bootMono < KernelFirstS * 1000) return false; // 로더 표시 뒤 15초 안 새 커널의 첫 1초 줄들
            return true;
        }

        bool CmdRx(string text, bool uboot)
            => text != null && (RxRebootRe.IsMatch(text) || (uboot && ResetRe.IsMatch(text)));

        /// <summary>
        /// 실시간 부팅 순간의 되돌아보기(C.7 5): cmd = 명령 자리 reboot 60초 안(그 안의 Restarting 이 창을 이음) · hard + cause = 직전 20초 안 panic/oops 줄 ·
        /// soft = 직전 10초 안 Restarting · 그 밖 hard(silence_s). cmd 가 아니고 cause 가 없으면 60초 안의 suspect panic/oops 를 cause 로(확인).
        /// </summary>
        string LookBack(long mono, long prevRx, out (long Mono, string Kind, string Line, DateTime Wall, string Context)? cause, out double? silence)
        {
            cause = null; silence = null;
            long W(double s) => (long)(s * 1000);
            bool cmd = rebootCmds.Any(t => t <= mono && mono - t <= W(CmdWindowS))
                       || rebootCmds.Any(t => restarts.Any(r => r >= t && r - t <= W(CmdWindowS) && r <= mono && mono - r <= W(CmdWindowS)));
            if (cmd) return "cmd";
            var e = errs.LastOrDefault(x => x.Mono <= mono && mono - x.Mono <= W(CauseS));
            string cls;
            if (e.Kind != null) { cause = (e.Mono, e.Kind, e.Line, e.Wall, e.Context); cls = "hard"; }
            else if (lastKernelRestart && lastKernelMono != long.MinValue && mono - lastKernelMono <= W(SoftS)) cls = "soft";   // Restarting 이 로더 표시 바로 앞 커널 줄(2초 안, 프롬프트 뒤 아님, BLK-2)
            else
            {
                cls = "hard";
                if (prevRx != long.MinValue) silence = Math.Round((mono - prevRx) / 1000.0, 1);
            }
            if (cause == null && pending.HasValue && mono - pending.Value.Mono <= W(ConfirmBootS)) cause = pending;
            return cls;
        }

        void StartBoot(string display, DateTime wall, long mono, string rxOff, bool loader, long prevRx)
        {
            string cls = LookBack(mono, prevRx, out var cause, out double? silence);
            bootSeq++;
            bootWall = wall;
            bootMono = mono;
            loaderMono = loader ? mono : long.MinValue;
            bootMaxKts = -1;
            lastLiveKts = -1;
            ktsArmed = true; armCand = null;                                                 // 새 부팅을 봤으니 커널 시계를 처음부터 안다
            lastKernelRestart = false; lastKernelMono = long.MinValue;
            filesByKind.Clear();                                                                 // 예산은 부팅마다(C.6)
            exemptUsed.Clear();
            ResetLookBack();
            ResetShell();                                                                        // 새 부팅이 그 전 명령의 출력을 끝낸다(명령을 돌리던 셸이 없다, B7)
            Ctx bc = null;
            if (cls != "cmd" && txtDir == null) bc = BootCtx(cls, display, wall, mono, rxOff, true);
            if (txtDir != null)                                                                  // 결과 창 안의 txt = 이 부팅으로 끝남
            {
                string t = wall.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                Settle(c =>
                {
                    if (c.Result != null || c.BootSeq >= bootSeq || mono - c.LastMono > ResultS * 1000) return;
                    c.Result = Tx("결과: " + t + "에 재부팅했습니다(" + BootText(cls) + ")."
                                  + (c.Suspect && (c.Kind == "panic" || c.Kind == "oops") ? " 이 재부팅으로 실제로 일어난 일로 확인되었습니다." : ""),
                                  "Result: rebooted at " + t + " (" + BootText(cls) + ")."
                                  + (c.Suspect && (c.Kind == "panic" || c.Kind == "oops") ? " This reboot confirms it really happened." : ""));
                });
            }
            string bctx = bc?.Path ?? (txtDir != null ? cause?.Context : null);                 // log_layout 2: panic·oops 뒤 부팅은 그 txt
            Emit("boot", cls, display, wall, mono, rxOff, bctx, false, o =>
            {
                if (bc?.Skipped != null) o.Set("context_skipped", bc.Skipped);
                if (bc != null && bc.SkippedBefore > 0) o.Set("skipped_before", (long)bc.SkippedBefore);
                if (cause.HasValue)
                    o.Set("cause", new JObj().Set("kind", cause.Value.Kind).Set("line", Trunc(cause.Value.Line, MaxLineChars)).Set("ts", EventLog.Ts(cause.Value.Wall)).Set("context", cause.Value.Context));
                if (silence.HasValue) o.Set("silence_s", silence.Value);
            });
        }

        /// <summary>명령 출력 구간 안의 커널 부팅 줄(바로 앞 무음 1초 미만): suspect 부팅 한 줄 — 부팅 상태·boot_seq 를 바꾸지 않음(옛 로그의 부팅 줄일 수도).</summary>
        void SuspectBoot(string display, DateTime wall, long mono, string rxOff)
        {
            regionBootMono = mono;
            if (AnyMatch(new[] { display }, loaderRe)) lastLiveKts = -1;                     // 짧은 무음의 로더 표시 = 진짜 리셋일 수도 — 단조 기준을 비운다(W2)
            string cls = LookBack(mono, long.MinValue, out _, out _);
            Ctx bc = cls != "cmd" && txtDir == null ? BootCtx(cls, display, wall, mono, rxOff, false) : null;
            Emit("boot", cls, display, wall, mono, rxOff, bc?.Path, true, o => { if (bc?.Skipped != null) o.Set("context_skipped", bc.Skipped); });
        }

        void PatternTimeout(string pattern)
            => hlog?.WriteThrottled("events-re|" + port + "|" + pattern, L.T("사건 감지 :" + port + " 패턴 시간 초과(200ms, 맞지 않음으로 봄): " + pattern,
                                                                                  "event detection :" + port + " pattern timed out (200 ms, treated as no match): " + pattern));

        /// <summary>
        /// 에코인가(판정하지 않음): 프롬프트 + 최근 2초 송신 줄 안의 글자, 그 송신 줄의 일부(3글자 이상 — 80열 접힘), 사람이 치는 중인 줄(typingPartial).
        /// 맨 프롬프트(뒤 글자 없음)는 에코가 아니다(명령이 끝났다는 표시).
        /// </summary>
        bool IsEcho(string display, bool prompt, string after, long mono, bool typingPartial)
        {
            var recent = recentTx.Where(t => mono - t.Mono <= EchoS * 1000 && t.Text.Trim().Length > 0).Select(t => t.Text).ToList();
            if (typingPartial && !prompt)                                                    // 치는 중: 사람이 보낸 글자의 에코일 때만(BLK-1)
            {
                string tp = display.Trim();
                return tp.Length > 0 && (typedBuf.Contains(tp, StringComparison.Ordinal) || recent.Any(t => t.Contains(tp, StringComparison.Ordinal)));
            }
            if (recent.Count == 0) return false;
            if (prompt)
            {
                string r = after.Trim();
                return r.Length > 0 && recent.Any(t => t.Contains(r, StringComparison.Ordinal));
            }
            string s = display.Trim();
            return s.Length >= 3 && recent.Any(t => t.Contains(s, StringComparison.Ordinal));
        }

        // ================================================================== 앞뒤 로그
        void CloseExpired(long mono)
        {
            foreach (var c in open.Where(x => mono - x.StartMono >= AfterS * 1000).ToList()) CloseCtx(c, Tx(AfterS.ToString(CultureInfo.InvariantCulture) + "초 지남", AfterS.ToString(CultureInfo.InvariantCulture) + " s passed"));
        }

        /// <summary>
        /// 앞뒤 로그 예산(H4·C.6). null = 만들어도 됨, 아니면 이유. 면제(부팅마다 첫 실시간 panic·oops, soft·hard 부팅 앞 줄)는 디스크 상한만 본다.
        /// rate 는 한 부팅 안 같은 종류의 반복에만: 1시간에 FreeFilesPerKindHour 개 뒤로는 KindRateS 에 1개. hourly 는 포트 1시간 상한(면제 제외).
        /// </summary>
        string Budget(string kind, long mono, bool exempt)
        {
            if (budget != null && budget.Full) return "disk";
            if (exempt) return null;
            filesHour.RemoveAll(t => mono - t > 3600_000);
            if (!filesByKind.TryGetValue(kind, out var l)) filesByKind[kind] = l = new List<long>();
            l.RemoveAll(t => mono - t > 3600_000);
            if (filesHour.Count >= MaxFilesPerPortHour) return "hourly";
            if (l.Count >= FreeFilesPerKindHour && mono - l[l.Count - 1] < KindRateS * 1000) return "rate";
            return null;
        }

        void Warn(string kind, string why)
        {
            if (skipState.TryGetValue(kind, out var st) && st == why) return;
            skipState[kind] = why;
            hlog?.Write(L.T("사건 감지 :" + port + " 앞뒤 로그 줄임(" + kind + ") — " + (why == "disk"
                ? "events 폴더가 " + (budget.MaxBytes / (1024 * 1024)) + "MB 를 넘음: 앞뒤 로그를 더 만들지 않음(지우지 않음, jsonl 에는 계속 기록)"
                : why == "hourly" ? "이 포트 앞뒤 로그가 1시간에 " + MaxFilesPerPortHour + "개를 넘음(jsonl 에는 모두 기록)"
                : "같은 부팅에서 같은 종류가 잦음: 1시간에 " + FreeFilesPerKindHour + "개 뒤로는 " + (KindRateS / 60).ToString("0", CultureInfo.InvariantCulture) + "분에 1개만(jsonl 에는 모두 기록)"),
                "event detection :" + port + " fewer context logs (" + kind + ") — " + (why == "disk"
                ? "events folder is over " + (budget.MaxBytes / (1024 * 1024)) + " MB: no more context logs (nothing deleted, jsonl keeps recording)"
                : why == "hourly" ? "this port made over " + MaxFilesPerPortHour + " context logs in 1 hour (jsonl records all)"
                : "same kind repeats in one boot: after " + FreeFilesPerKindHour + " per hour, only 1 every " + (KindRateS / 60).ToString("0", CultureInfo.InvariantCulture) + " min (jsonl records all)")));
        }

        /// <summary>새 묶음. 예산 밖이면 파일 없이 묶기만 하고(같은 사건의 연속 줄이 새 사건이 되지 않게) 이유를 남긴다.</summary>
        Ctx NewCtx(string key, string kind, bool suspect, string display, DateTime wall, long mono, string rxOff, bool collect, bool exempt)
        {
            var c = new Ctx { StartMono = mono, BootSeq = bootSeq, Marked = 1, LastMono = mono };
            c.Kinds.Add(key);
            if (txtDir != null && !fileKinds.Contains(kind)) { if (collect) open.Add(c); return c; }   // log_layout 2: 알리지 않는 종류는 txt 없이 묶기만(jsonl 에만)
            string why = Budget(kind, mono, exempt);
            if (why != null)
            {
                c.Skipped = why;
                skippedSince.TryGetValue(kind, out int sk);
                skippedSince[kind] = sk + 1;
                Warn(kind, why);
                if (collect) open.Add(c);
                return c;
            }
            OpenFile(c, kind, suspect, display, wall, mono, rxOff, collect);
            if (collect) open.Add(c);
            return c;
        }

        /// <summary>파일 없이 묶던 묶음에 새 종류가 들어옴(D5): 그 종류의 예산(면제 포함)으로 다시 판단해 되면 지금 파일을 만든다(앞 200줄 + 그 줄).</summary>
        bool Promote(Ctx c, string kind, bool suspect, string display, DateTime wall, long mono, string rxOff, bool exempt, out string skip)
        {
            skip = Budget(kind, mono, exempt);
            if (skip != null) { skippedSince.TryGetValue(kind, out int sk); skippedSince[kind] = sk + 1; Warn(kind, skip); return false; }
            c.Skipped = null;
            OpenFile(c, kind, suspect, display, wall, mono, rxOff, true);
            return true;
        }

        void OpenFile(Ctx c, string kind, bool suspect, string display, DateTime wall, long mono, string rxOff, bool collect)
        {
            skipState.Remove(kind);
            skippedSince.TryGetValue(kind, out int skipped);
            c.SkippedBefore = skipped;
            skippedSince[kind] = 0;
            filesHour.Add(mono);
            if (!filesByKind.TryGetValue(kind, out var l)) filesByKind[kind] = l = new List<long>();
            l.Add(mono);
            if (txtDir != null) { OpenTxt(c, kind, suspect, display, wall, mono); return; }
            string day = System.IO.Path.Combine(dir, wall.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            string name = port.ToString(CultureInfo.InvariantCulture) + "_" + Names.SafeLabel(board()) + "_" + wall.ToString("HHmmss", CultureInfo.InvariantCulture) + "_" + kind;
            string p = null;
            for (int n = 1; n < 1000; n++)
            {
                p = System.IO.Path.Combine(day, name + (n > 1 ? "_" + n.ToString(CultureInfo.InvariantCulture) : "") + ".txt");
                if (!usedNames.Contains(p) && !File.Exists(p)) break;
            }
            usedNames.Add(p);
            c.Path = p;
            c.File = new SafeFile(p, L.T("앞뒤 로그 :" + port, "context log :" + port), hlog, budget, false);
            string since = SinceBoot(wall, mono) is double sb ? Tx(" (부팅 뒤 " + sb.ToString("0.0", CultureInfo.InvariantCulture) + "초)", " (" + sb.ToString("0.0", CultureInfo.InvariantCulture) + " s after boot)") : "";
            var sbd = new StringBuilder();
            sbd.Append(Tx("# UART 허브 사건 기록 — ", "# UART Hub event record — ")).Append(kind).Append(" (").Append(EventKinds.Label(kind, null, lang: lang)).Append(")\r\n");
            sbd.Append(Tx("# 시각 ", "# Time ")).Append(wall.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(Tx(" · 포트 ", " · port ")).Append(port).Append(Tx(" · 케이블 ", " · cable ")).Append(cable)
               .Append(Tx(" · 보드 ", " · board ")).Append(board() ?? "unknown").Append(Tx(" · 부팅 #", " · boot #")).Append(bootSeq).Append(since).Append("\r\n");
            sbd.Append(Tx("# 원본 로그 ", "# Source log ")).Append(logPath() ?? Tx("(없음)", "(none)")).Append(" · rx_offset ").Append(rxOff).Append("\r\n");
            if (skipped > 0) sbd.Append(Tx("# 이 파일 전 같은 종류 " + skipped + "건은 앞뒤 로그 없이 jsonl 에만(예산)\r\n",
                                            "# " + skipped + " earlier event(s) of this kind are in jsonl only, without a context log (budget)\r\n"));
            sbd.Append(collect
                ? Tx("# 앞 " + BeforeLines + "줄(있는 만큼) · '>>>' = 사건 줄 · 뒤 " + AfterLines + "줄 또는 " + AfterS.ToString(CultureInfo.InvariantCulture) + "초 — 다 모을 때까지 이 파일에 이어 쓰고 끝에 '# 끝' 줄을 남긴다\r\n",
                     "# " + BeforeLines + " lines before (as many as there are) · '>>>' = event line · " + AfterLines + " lines or " + AfterS.ToString(CultureInfo.InvariantCulture) + " s after — appended until done, then an '# end' line\r\n")
                : Tx("# 앞 " + BeforeLines + "줄(있는 만큼) · '>>>' = 부팅 줄(그 전 부팅의 마지막 줄들이 증거)\r\n",
                     "# " + BeforeLines + " lines before (as many as there are) · '>>>' = boot line (the last lines of the previous boot are the evidence)\r\n"));
            foreach (var b in before) sbd.Append("    ").Append(Stamp(b.Wall)).Append(b.Text).Append("\r\n");
            sbd.Append(">>> ").Append(Stamp(wall)).Append(display).Append("\r\n");
            c.File.Write(sbd.ToString());
        }

        /// <summary>soft·hard 부팅의 앞 줄(L14): 직전 200줄 + 부팅 줄, 바로 닫는다(실시간 부팅은 rate 면제 — 디스크 상한만).</summary>
        Ctx BootCtx(string cls, string display, DateTime wall, long mono, string rxOff, bool exempt)
        {
            var c = NewCtx("boot", "boot", false, display, wall, mono, rxOff, false, exempt);
            if (c.File != null) { c.File.Write(Tx("# 끝 — 부팅 " + cls + " 앞 줄(" + before.Count + "줄)", "# end — lines before boot " + cls + " (" + before.Count + " lines)") + Skips(c) + "\r\n"); Retire(c.File); }
            return c;
        }

        string Skips(Ctx c) => c.SkippedBefore > 0 ? Tx(", 이 파일 전 앞뒤 로그 생략 " + c.SkippedBefore + "건", ", " + c.SkippedBefore + " earlier context log(s) skipped") : "";

        void WriteLine(Ctx c, bool mark, DateTime wall, string display) => Append(c, (mark ? ">>> " : "    ") + Stamp(wall) + display + "\r\n");

        /// <summary>열린 묶음의 파일에 이어 쓴다(log_layout 2 는 다시 쓰기용으로 기억도 한다).</summary>
        void Append(Ctx c, string text)
        {
            if (c.File == null) return;
            c.AfterText?.Append(text);
            c.File.Write(text);
        }

        void CloseCtx(Ctx c, string why)
        {
            open.Remove(c);
            if (c.File == null) return;
            if (txtDir != null)                                                                  // log_layout 2: 끝 줄은 결과 — 아직 모르면 결과 창이 지날 때까지 기다린다
            {
                if (c.Result != null) c.File.Write("\r\n" + c.Result + "\r\n");
                else waiting.Add(c);
                Retire(c.File);
                return;
            }
            c.File.Write(Tx("# 끝 — " + why + " (뒤 " + c.After + "줄, 사건 줄 " + c.Marked + "개: " + string.Join(", ", c.Kinds) + Skips(c) + ")\r\n",
                            "# end — " + why + " (" + c.After + " lines after, " + c.Marked + " event line(s): " + string.Join(", ", c.Kinds) + Skips(c) + ")\r\n"));
            Retire(c.File);
        }

        /// <summary>다 쓴 파일: 대기열이 비었으면 닫고, 아니면 다 쓸 때까지 Tick 마다 다시 쓴다.</summary>
        void Retire(SafeFile f)
        {
            if (f.Pending) closing.Add(f); else f.Close(0);
        }

        // ================================================================== 에이징 이벤트 txt(log_layout 2, 부록 C.11)
        /// <summary>열린 묶음·결과를 기다리는 txt 에 결과를 정한다(decide 가 Result 를 채움). 닫힌 것은 바로 끝 줄을 쓰고 목록에서 뺀다(열린 것은 닫을 때 쓴다).</summary>
        void Settle(Action<Ctx> decide)
        {
            if (txtDir == null) return;
            foreach (var c in open) if (c.Path != null) decide(c);
            foreach (var c in waiting.ToList())
            {
                decide(c);
                if (c.Result == null) continue;
                waiting.Remove(c);
                var f = new SafeFile(c.Path, L.T("에이징 이벤트 :" + port, "aging event :" + port), hlog, budget, false);
                f.Write("\r\n" + c.Result + "\r\n");
                Retire(f);
            }
        }

        string BootText(string cls) => cls == "hard" ? Tx("하드 리셋", "hard reset") : cls == "soft" ? Tx("소프트 재부팅", "soft reboot") : cls == "cmd" ? Tx("명령으로 재부팅", "reboot by command") : cls;

        /// <summary>새 종류가 묶음의 대표보다 심한가(panic &gt; oops &gt; app_error), 같은 종류면 실시간이 의심을 이긴다.</summary>
        static bool Worse(string kind, bool suspect, Ctx c)
        {
            int a = EventKinds.Severity(kind, null), b = EventKinds.Severity(c.Kind, null);
            return a > b || (a == b && c.Suspect && !suspect);
        }

        /// <summary>
        /// txt 파일 이름의 종류(이 판정기의 언어 — 계획 §4): 커널패닉 · Oops · 앱오류_&lt;코드 또는 첫 낱말&gt;(프로필 app_codes 의 코드 …) · BUG · OOM
        /// ↔ kernel-panic · oops · app-error_&lt;code&gt; · bug · oom.
        /// </summary>
        string TxtKind(string kind, string line) => kind switch
        {
            "panic" => Tx("커널패닉", "kernel-panic"),
            "oops" => Tx("Oops", "oops"),
            "bug" => Tx("BUG", "bug"),
            "oom" => Tx("OOM", "oom"),
            "app_error" => Tx("앱오류_", "app-error_") + AppCode(line),
            _ => kind,
        };

        /// <summary>앱 오류 코드: 제품 프로필 app_codes 중 처음 맞은 규칙(code 또는 맞은 글자), 없으면 맞은 패턴 글자의 첫 낱말.</summary>
        public string AppCode(string line)
        {
            line ??= "";
            string code = ProductProfile.Current.AppCode(line);
            if (!string.IsNullOrEmpty(code)) return code;
            string hit = null;
            foreach (var p in pats.List)
            {
                if (p.Kind != "app_error") continue;
                try { var pm = p.Re.Match(line); if (pm.Success && pm.Value.Trim().Length > 0) { hit = pm.Value; break; } }
                catch (RegexMatchTimeoutException) { }
            }
            string w = (hit ?? line).Trim().Split(new[] { ' ', '\t', ':', '=', ',' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return string.IsNullOrEmpty(w) ? Tx("알수없음", "unknown") : w.Length > 40 ? w.Substring(0, 40) : w;
        }

        /// <summary>파일 이름에 못 쓰는 글자(와 공백)는 '_'.</summary>
        public static string FileSafe(string s)
        {
            var bad = System.IO.Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s) sb.Append(Array.IndexOf(bad, ch) >= 0 || ch == '\\' || ch == '/' || ch == ':' || ch == '*' || ch == '?' || ch == '"' || ch == '<' || ch == '>' || ch == '|' || char.IsWhiteSpace(ch) || char.IsControl(ch) ? '_' : ch);
            return sb.ToString();
        }

        /// <summary>&lt;txt 뿌리&gt;\&lt;yyyyMMdd&gt;\&lt;HHMMSS&gt;_&lt;보드&gt;(&lt;포트&gt;)_&lt;종류&gt;[_의심].txt, 겹치면 _2, _3 …</summary>
        string TxtPath(Ctx c)
        {
            string day = flatTxt ? txtDir : System.IO.Path.Combine(txtDir, c.NameWall.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            string name = flatTxt
                ? FileSafe(c.NameWall.ToString("HHmmss", CultureInfo.InvariantCulture) + "_" + TxtKind(c.Kind, c.Line) + (c.Suspect ? Tx("_의심", "_suspect") : ""))
                : FileSafe(c.NameWall.ToString("HHmmss", CultureInfo.InvariantCulture) + "_" + (string.IsNullOrWhiteSpace(board()) ? "unknown" : board().Trim())
                                   + "(" + port.ToString(CultureInfo.InvariantCulture) + ")_" + TxtKind(c.Kind, c.Line) + (c.Suspect ? Tx("_의심", "_suspect") : ""));
            string p = null;
            for (int n = 1; n < 1000; n++)
            {
                p = System.IO.Path.Combine(day, name + (n > 1 ? "_" + n.ToString(CultureInfo.InvariantCulture) : "") + ".txt");
                if (!usedNames.Contains(p) && !File.Exists(p)) break;
            }
            usedNames.Add(p);
            return p;
        }

        /// <summary>머리(사람용): 이벤트 종류·보드(포트·COM)·시각(부팅 후 초)·원인 줄·콘솔 로그 파일, 의심이면 그 이유 → '----- 앞 200줄 -----'.</summary>
        string TxtHead(Ctx c)
        {
            var sb = new StringBuilder();
            string what = EventKinds.Label(c.Kind, null, lang: lang) + (c.Kind == "app_error" ? " " + AppCode(c.Line) : "");
            sb.Append(Tx("이벤트 종류: ", "Event: ")).Append(what).Append(c.Suspect ? Tx(" (의심)", " (suspect)") : "").Append("\r\n");
            sb.Append(Tx("보드: ", "Board: ")).Append(string.IsNullOrWhiteSpace(board()) ? Tx("알 수 없음", "unknown") : board()).Append(Tx(" (포트 ", " (port ")).Append(port.ToString(CultureInfo.InvariantCulture))
              .Append(com != null ? " · " + com : "").Append(")\r\n");
            sb.Append(Tx("시각: ", "Time: ")).Append(c.Wall.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
              .Append(c.SinceBoot is double sb2 ? Tx(" (부팅 후 " + sb2.ToString("0.0", CultureInfo.InvariantCulture) + "초)", " (" + sb2.ToString("0.0", CultureInfo.InvariantCulture) + " s after boot)") : "").Append("\r\n");
            sb.Append(Tx("원인 줄: ", "Cause line: ")).Append(Trunc(c.Line, MaxLineChars)).Append("\r\n");
            sb.Append(Tx("콘솔 로그 파일: ", "Console log file: ")).Append(c.LogPath ?? Tx("(없음)", "(none)")).Append("\r\n");
            if (c.Suspect)
                sb.Append(Tx("의심 이유: 사람이나 Claude가 친 명령의 출력 속에 있던 줄입니다. 옛 로그를 다시 보여 준 것일 수 있어 알림을 띄우지 않았습니다. 결과 줄에서 실제로 재부팅했는지 확인하세요.\r\n",
                             "Why suspect: this line was inside the output of a command typed by someone or by Claude. It may be an old log shown again, so no alert was shown. Check the result line to see whether the board really rebooted.\r\n"));
            if (c.SkippedBefore > 0) sb.Append(Tx("참고: 이 파일 전 같은 종류 " + c.SkippedBefore + "건은 앞뒤 로그 없이 기록만 했습니다(용량·빈도 제한).\r\n",
                                                  "Note: " + c.SkippedBefore + " earlier event(s) of this kind were recorded without a context log (size and rate limits).\r\n"));
            sb.Append(Tx("----- 앞 " + BeforeLines + "줄 -----\r\n", "----- " + BeforeLines + " lines before -----\r\n"));
            return sb.ToString();
        }

        string AfterHead => Tx("----- 이후 -----\r\n", "----- after -----\r\n");

        /// <summary>새 txt: 머리 + 앞 200줄 + '----- 이후 -----' + 사건 줄. 뒤 줄은 Append 로 이어 쓰고, 끝 줄(결과)은 닫을 때·결과가 정해질 때.</summary>
        void OpenTxt(Ctx c, string kind, bool suspect, string display, DateTime wall, long mono)
        {
            c.Kind = kind; c.Suspect = suspect; c.Line = display; c.Wall = c.NameWall = wall; c.SinceBoot = SinceBoot(wall, mono); c.LogPath = logPath();
            var bt = new StringBuilder();
            foreach (var b in before) bt.Append("    ").Append(Stamp(b.Wall)).Append(b.Text).Append("\r\n");
            c.BeforeText = bt.ToString();
            c.AfterText = new StringBuilder(">>> " + Stamp(wall) + display + "\r\n");
            c.Path = TxtPath(c);
            c.File = new SafeFile(c.Path, L.T("에이징 이벤트 :" + port, "aging event :" + port), hlog, budget, false);
            c.File.Write(TxtHead(c) + c.BeforeText + AfterHead + c.AfterText);
        }

        /// <summary>
        /// 더 심한 종류(또는 같은 종류의 실시간 줄)가 묶음에 합류: 대표 종류·원인 줄을 바꾸고 새 이름으로 다시 쓴 뒤 옛 파일을 지운다(이름 시각은 그대로).
        /// 돌려주는 값 = 옛 경로(그 기록의 context_was). 쓰는 동안 줄인 이후 줄은 기억해 둔 것으로 다시 쓴다.
        /// </summary>
        string Upgrade(Ctx c, string kind, bool suspect, string display, DateTime wall, long mono)
        {
            string old = c.Path;
            c.Kind = kind; c.Suspect = c.Suspect && suspect; c.Line = display; c.Wall = wall; c.SinceBoot = SinceBoot(wall, mono);
            c.File.Abandon();
            c.Path = TxtPath(c);
            c.File = new SafeFile(c.Path, L.T("에이징 이벤트 :" + port, "aging event :" + port), hlog, budget, false);
            c.File.Write(TxtHead(c) + c.BeforeText + AfterHead + c.AfterText);
            try { File.Delete(old); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            { hlog?.WriteThrottled("events-rename|" + port, L.T("사건 감지 :" + port + " 옛 이름 txt 를 지우지 못함(" + old + "): " + e.Message, "event detection :" + port + " could not delete the old txt name (" + old + "): " + e.Message)); }
            usedNames.Remove(old);
            for (int i = 0; i < errs.Count; i++)                                                 // 리셋 원인·확인 대기가 옛 경로를 들고 있지 않게
                if (string.Equals(errs[i].Context, old, StringComparison.OrdinalIgnoreCase)) errs[i] = (errs[i].Mono, errs[i].Kind, errs[i].Line, errs[i].Wall, c.Path, errs[i].Suspect);
            if (pending.HasValue && string.Equals(pending.Value.Context, old, StringComparison.OrdinalIgnoreCase))
                pending = (pending.Value.Mono, pending.Value.Kind, pending.Value.Line, pending.Value.Wall, c.Path);
            return old;
        }

        static string Stamp(DateTime wall) => "[" + wall.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "] ";

        // ================================================================== 기록
        void EmitError(string kind, string display, DateTime wall, long mono, string rxOff, string context, string skipped, int skippedBefore, bool suspect, string contextWas = null)
            => Emit(kind, null, display, wall, mono, rxOff, context, suspect, o =>
            {
                if (skipped != null) o.Set("context_skipped", skipped);
                if (skippedBefore > 0) o.Set("skipped_before", (long)skippedBefore);
                if (contextWas != null) o.Set("context_was", contextWas);                     // 더 심한 종류가 합류해 txt 이름이 바뀜(앞 기록의 context = 이 경로)
            });

        /// <summary>글자 수 자르기(서로게이트 짝을 가르지 않음 — 홀로 남은 서로게이트는 JSON 을 읽는 쪽을 깨뜨린다, H5).</summary>
        public static string Trunc(string s, int n)
        {
            if (s == null) return "";
            if (s.Length <= n) return s;
            int k = n;
            if (k > 0 && char.IsHighSurrogate(s[k - 1])) k--;
            return s.Substring(0, k) + "…";
        }

        /// <summary>부팅 뒤 초: 이 실행에서 본 부팅이면 단조 시간(D11), 아니면 벽시계(jsonl 에서 이어 받은 부팅 시각), 모르면 null.</summary>
        double? SinceBoot(DateTime wall, long mono)
        {
            if (bootMono != long.MinValue) return Math.Round(Math.Max(0, (mono - bootMono) / 1000.0), 1);
            if (bootWall.HasValue) return Math.Round(Math.Max(0, (wall - bootWall.Value).TotalSeconds), 1);
            return null;
        }

        void Emit(string kind, string cls, string line, DateTime wall, long mono, string rxOff, string context, bool suspect, Action<JObj> extra)
        {
            var o = new JObj().Set("ts", EventLog.Ts(wall)).Set("port", (long)port).Set("cable", cable).Set("board", board()).Set("kind", kind);
            if (cls != null) o.Set("class", cls);
            o.Set("line", Trunc(line, MaxLineChars)).Set("log", logPath()).Set("log_line", rxOff).Set("context", context).Set("boot_seq", bootSeq);
            if (SinceBoot(wall, mono) is double sb) o.Set("since_boot_s", sb); else o.Null("since_boot_s");
            if (suspect) o.Set("suspect", "cmd_output");
            extra?.Invoke(o);
            jsonl.Write(o + "\n");
            Emitted++;
        }

        /// <summary>
        /// jsonl 끝에서 boot_seq·지금 부팅의 시작 시각(ts − since_boot_s)·마지막 기록 시각·종류를 이어 받는다(suspect 기록은 부팅 시각에 쓰지 않는다).
        /// 마지막 줄이 개행 없이 끊겼으면(전원 차단 등) 다음 기록이 그 뒤에 붙지 않게 개행을 먼저 쓴다(M3).
        /// </summary>
        void LoadState()
        {
            try
            {
                if (!File.Exists(JsonlPath)) return;
                byte[] buf;
                using (var fs = new FileStream(JsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long start = Math.Max(0, fs.Length - 65536);
                    fs.Position = start;
                    buf = new byte[fs.Length - start];
                    int got = 0;
                    while (got < buf.Length) { int k = fs.Read(buf, got, buf.Length - got); if (k <= 0) break; got += k; }
                    if (got < buf.Length) Array.Resize(ref buf, got);
                }
                if (buf.Length > 0 && buf[buf.Length - 1] != (byte)'\n') jsonl.Write("\n");
                var lines = Encoding.UTF8.GetString(buf).Split('\n');
                bool haveLast = false;
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    var r = EventRec.Parse(lines[i]);
                    if (r == null) continue;
                    if (!haveLast) { haveLast = true; bootSeq = r.BootSeq; LastRecordTs = r.Ts; LastRecordKind = r.Kind; }
                    if (r.Suspect != null) continue;
                    if (r.SinceBootS.HasValue) bootWall = r.Ts.LocalDateTime.AddSeconds(-r.SinceBootS.Value);
                    return;
                }
            }
            catch (Exception e) { hlog?.WriteThrottled("events-load|" + port, L.T("사건 감지 :" + port + " 이전 사건 기록 읽기 실패: " + e.GetType().Name + ": " + e.Message,
                                                                                   "event detection :" + port + " failed to read earlier event records: " + e.GetType().Name + ": " + e.Message)); }
        }

        // ================================================================== 도우미
        /// <summary>구간 중 처음 맞은 글자(없으면 null).</summary>
        static string FirstMatch(IReadOnlyList<string> segs, Regex re)
        {
            foreach (var s in segs)
            {
                try { var m = re.Match(s); if (m.Success) return m.Value; }
                catch (RegexMatchTimeoutException) { }
            }
            return null;
        }

        static bool AnyMatch(IReadOnlyList<string> segs, Regex re)
        {
            if (re == null) return false;                                                    // 로더 표시가 없는 보드(loader_marker_re 빈 값)
            foreach (var s in segs)
            {
                try { if (re.IsMatch(s)) return true; }
                catch (RegexMatchTimeoutException) { }
            }
            return false;
        }

        /// <summary>줄에 셸·U-Boot 프롬프트가 있으면 true, 마지막 프롬프트 뒤 글자, 그 프롬프트가 U-Boot 인지.</summary>
        bool PromptRemainder(string display, out string after, out bool uboot)
        {
            after = null; uboot = false;
            int best = -1;
            foreach (var (re, ub) in new[] { (shellPromptRe, false), (ubootPromptRe, true) })
            {
                if (re == null) continue;
                try
                {
                    var ms = re.Matches(display);
                    if (ms.Count == 0) continue;
                    var m = ms[ms.Count - 1];
                    if (m.Index + m.Length > best) { best = m.Index + m.Length; uboot = ub; }
                }
                catch (RegexMatchTimeoutException) { }
            }
            if (best < 0) return false;
            after = display.Substring(best);
            return true;
        }
    }

    /// <summary>
    /// 허브의 사건 감지(ControlServer 가 만든다). 붙은 케이블(링이 있는 객체)마다 추적 스레드 하나: 링을 자기 커서로 읽어 줄을 만들고 EventScanner 에 넣는다.
    /// 처리기(attach·detach·송신 관측)는 짧고 예외를 밖으로 내지 않는다. 링이 닫히면(허브 정지) 그 스레드는 열린 앞뒤 로그를 닫고 끝난다(Join 으로 기다릴 수 있다).
    /// config events.enabled:false 면 판정하지 않는다(케이블별 cables.&lt;id&gt;.events.enabled:false 는 그 케이블만).
    /// 에이징 세션(부록 C.12, SessionStore): 세션 중인 포트는 판정기가 앞뒤 로그 txt 를 세션 폴더 이벤트\ 에 쓰고, 추적 스레드가 같은 줄을 세션 폴더 콘솔.log 에도 쓴다.
    /// log_layout 3 이면 세션 밖 포트는 판정하지 않는다(jsonl·풍선 없음 — 추적 스레드는 세션을 기다리며 링만 따라간다). layout 1·2 는 세션 밖에서도 지금처럼 판정한다.
    /// 세션이 바뀌면(시작·끝) 추적 스레드가 다음 차례(200ms 안)에 판정기를 닫고 새로 만든다 — 판정기는 그 스레드만 만지기 때문이다.
    /// </summary>
    public sealed class EventDetector
    {
        readonly Hub hub;
        readonly Dictionary<Cable, Follower> followers = new Dictionary<Cable, Follower>();
        readonly List<Thread> threads = new List<Thread>();
        readonly Dictionary<int, DateTime> lastAlive = new Dictionary<int, DateTime>();          // 이전 코어의 마지막 하트비트(허브 시작 공백)
        readonly Dictionary<Cable, DateTime> detachedAt = new Dictionary<Cable, DateTime>();
        EventsBudget budget;

        public EventDetector(Hub hub, SessionStore sessions = null)
        {
            this.hub = hub;
            Sessions = sessions;
            if (!hub.Config.Events.Enabled && !Gated) return;
            ReadAlive();
            hub.Cables.CableAttached += OnAttached;
            hub.Cables.CableDetached += OnDetached;
        }

        /// <summary>에이징 세션 저장소(없으면 세션 없음).</summary>
        public SessionStore Sessions { get; }
        /// <summary>log_layout 3: 세션 중인 포트만 판정한다(부록 C.12).</summary>
        public bool Gated => hub.Config.LogLayout >= 3;

        /// <summary>사건 jsonl 폴더: &lt;log_root&gt;\events, log_layout 2·3 = &lt;허브 폴더&gt;\events(부록 C.11).</summary>
        public string Dir => hub.Config.EventsJsonlDir;
        /// <summary>log_layout 2 의 에이징 이벤트 txt 뿌리(&lt;기준&gt;\에이징이벤트, layout 3 은 &lt;기준&gt;\에이징 — 세션 밖엔 쓰지 않음), 아니면 null.</summary>
        public string TxtDir => hub.Config.LogLayout >= 2 ? hub.Config.EventsTxtDir : null;

        /// <summary>허브 시작 전(Hub 생성 중) alive\&lt;port&gt; 를 읽어 둔다 — 새 코어의 DiskWriter 가 덮어쓰기 전.</summary>
        void ReadAlive()
        {
            try
            {
                string d = Path.Combine(hub.Config.EffectiveHubDir, "alive");
                if (!Directory.Exists(d)) return;
                foreach (var f in Directory.GetFiles(d))
                    if (int.TryParse(Path.GetFileName(f), NumberStyles.Integer, CultureInfo.InvariantCulture, out int p)
                        && DateTime.TryParseExact((HubFiles.ReadShared(f) ?? "").Trim(), "yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
                        lastAlive[p] = t;
            }
            catch (Exception) { }
        }

        void OnAttached(Cable c, long seq)
        {
            try
            {
                bool detect = hub.Config.Events.Enabled && hub.Config.CableFor(c.Id).Events?.Enabled != false;
                if (c.Ring == null || c.TcpPort <= 0 || (!detect && !Gated)) return;
                Follower f;
                bool fresh = false;
                DateTime? det = null;
                lock (followers)
                {
                    if (!followers.TryGetValue(c, out f)) { f = new Follower(this, c, detect); followers[c] = f; fresh = true; }
                    if (detachedAt.TryGetValue(c, out var dt)) { det = dt; detachedAt.Remove(c); }
                }
                if (fresh) f.Start(); else f.SessionBreak(c.AttachRxPos, c.Com, det, hub.Clock.Now);
            }
            catch (Exception e) { hub.Log.WriteThrottled("events-attach|" + c.Id, L.T("사건 감지 시작 실패 " + c.Id + ": " + e.GetType().Name + ": " + e.Message, "event detection failed to start " + c.Id + ": " + e.GetType().Name + ": " + e.Message)); }
        }

        void OnDetached(Cable c, long seq)
        {
            try
            {
                Follower f;
                DateTime now = hub.Clock.Now;
                lock (followers) { detachedAt[c] = now; followers.TryGetValue(c, out f); }
                f?.Detached(now, hub.Clock.MonoMs, c.Ring?.End ?? 0);
            }
            catch (Exception) { }
        }

        /// <summary>허브 정지 뒤: 추적 스레드들이 앞뒤 로그를 닫고('# 끝') 남은 쓰기를 마칠 때까지 ms 까지 기다린다(L12).</summary>
        public bool Join(int ms)
        {
            Thread[] ts;
            lock (threads) ts = threads.ToArray();
            long deadline = Environment.TickCount64 + ms;
            foreach (var t in ts)
            {
                long left = deadline - Environment.TickCount64;
                if (left <= 0 || !t.Join((int)left)) return false;
            }
            return true;
        }

        /// <summary>시험 훅: 그 케이블의 추적 스레드를 멈추거나(링을 읽지 않음 — 놓침 재현) 다시 돌린다. 멈춤을 스레드가 확인했으면 true.</summary>
        public bool TestPause(Cable c, bool pause, int waitMs = 3000)
        {
            Follower f;
            lock (followers) followers.TryGetValue(c, out f);
            if (f == null) return false;
            f.Paused = pause;
            if (!pause) return true;
            long deadline = Environment.TickCount64 + waitMs;
            while (!f.PausedAck && Environment.TickCount64 < deadline) Thread.Sleep(5);
            return f.PausedAck;
        }

        /// <summary>시험·진단: 그 케이블의 판정기(추적 스레드가 쓰는 중 — 읽기만).</summary>
        public EventScanner ScannerOf(Cable c)
        {
            lock (followers) return followers.TryGetValue(c, out var f) ? f.Scanner : null;
        }

        /// <summary>그 케이블의 추적 스레드가 지금 따르는 세션(없으면 null).</summary>
        public AgingSession SessionOf(Cable c)
        {
            lock (followers) return followers.TryGetValue(c, out var f) ? f.Current : null;
        }

        /// <summary>
        /// 세션 시작·끝 뒤: 그 포트의 추적 스레드가 세션을 따르기 시작했거나(want = s) 놓았을 때(want = null)까지 ms 기다린다(판정기·콘솔.log 를 닫고 남은 쓰기까지).
        /// 끝(want = null)은 추적 스레드가 없으면 바로 true, 시작은 따르는 스레드가 없으면(케이블이 없음·감지 끔) ms 뒤 false(= 아직 기록하지 않음).
        /// </summary>
        public bool WaitSession(int port, AgingSession want, int ms)
        {
            long deadline = Environment.TickCount64 + ms;
            while (true)
            {
                List<Follower> fs;
                lock (followers) fs = followers.Values.Where(f => f.Port == port && f.Alive).ToList();
                if ((want == null || fs.Count > 0) && fs.All(f => ReferenceEquals(f.Current, want) && f.Synced)) return true;
                if (Environment.TickCount64 >= deadline) return false;
                Thread.Sleep(20);
            }
        }

        EventsBudget Budget()
        {
            // log_layout 3: 두 언어의 에이징 뿌리를 모두 센다(언어를 바꿔도 옛 세션 폴더가 예산에 남는다)
            lock (followers) return budget ??= new EventsBudget((long)(hub.Config.Events.MaxMb * 1024 * 1024),
                                                                new[] { Dir }.Concat(hub.Config.LogLayout >= 2 ? hub.Config.EventsTxtDirs : new string[0]).ToArray());
        }

        /// <summary>세션 콘솔.log(추적 스레드 하나만 쓴다): DiskWriter .log 와 같은 줄머리 "[yyyy-MM-dd HH:mm:ss.fff +   경과초] 글" — 경과 = 세션 시작부터.</summary>
        sealed class ConsoleOut
        {
            readonly AgingSession s;
            readonly HubLog log;
            FileStream fs;
            bool dirty;

            /// <summary>콘솔.log 에 쓰는 표시 글(세션 언어).</summary>
            public string T(string ko, string en) => AgingLang.T(s.Lang, ko, en);

            public ConsoleOut(AgingSession s, HubLog log, DateTime now)
            {
                this.s = s; this.log = log;
                try
                {
                    Directory.CreateDirectory(s.Folder);
                    Directory.CreateDirectory(s.EventsDir);
                    var fi = new FileInfo(s.ConsolePath);
                    bool resume = fi.Exists && fi.Length > 0;
                    DateTime last = resume ? fi.LastWriteTime : now;
                    long lines = resume ? CountLines(s.ConsolePath) : 0;
                    fs = new FileStream(s.ConsolePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    s.SetConsole(fs.Length, lines);
                    if (resume)
                        Marker(now, T("기록 끊김 " + last.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " ~ " + now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                                      + " (" + AgingText.Duration((now - last).TotalSeconds, s.Lang) + ") — 허브가 다시 켜졌거나 케이블이 다시 연결됨",
                                      "Recording gap " + last.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " ~ " + now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                                      + " (" + AgingText.Duration((now - last).TotalSeconds, s.Lang) + ") — the hub restarted or the cable was reconnected"));
                    else Marker(now, T("에이징 시작 '" + s.Name + "' · " + (s.Board ?? "보드 모름") + " (포트 " + s.Port.ToString(CultureInfo.InvariantCulture) + (s.Com != null ? " · " + s.Com : "") + ")",
                                       "Aging started '" + s.Name + "' · " + (s.Board ?? "board unknown") + " (port " + s.Port.ToString(CultureInfo.InvariantCulture) + (s.Com != null ? " · " + s.Com : "") + ")"));
                    Flush();
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    fs = null;
                    log?.WriteThrottled("aging-console|" + s.Port, L.T("에이징 콘솔.log 열기 실패 :" + s.Port + " (" + s.ConsolePath + "): " + e.Message, "aging console log open failed :" + s.Port + " (" + s.ConsolePath + "): " + e.Message));
                }
            }

            static long CountLines(string path)
            {
                long n = 0;
                using var r = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buf = new byte[1 << 16];
                int k;
                while ((k = r.Read(buf, 0, buf.Length)) > 0) for (int i = 0; i < k; i++) if (buf[i] == (byte)'\n') n++;
                return n;
            }

            public void Line(DateTime wall, string text)
            {
                double el = Math.Max(0, (wall - s.Started).TotalSeconds);
                Write("[" + wall.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " +" + el.ToString("F3", CultureInfo.InvariantCulture).PadLeft(9) + "] " + text + "\r\n");
            }

            /// <summary>기록 표시 줄 "*** … ***"(DiskWriter 의 rotate 표시와 같은 모양).</summary>
            public void Marker(DateTime wall, string text) => Line(wall, "*** " + text + " ***");

            void Write(string line)
            {
                if (fs == null) return;
                try
                {
                    byte[] b = Encoding.UTF8.GetBytes(line);
                    fs.Write(b, 0, b.Length);
                    s.AddConsole(b.Length, 1);
                    dirty = true;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                { log?.WriteThrottled("aging-console|" + s.Port, L.T("에이징 콘솔.log 쓰기 실패 :" + s.Port + ": " + e.Message, "aging console log write failed :" + s.Port + ": " + e.Message)); }
            }

            public void Flush()
            {
                if (fs == null || !dirty) return;
                try { fs.Flush(); dirty = false; }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                { log?.WriteThrottled("aging-console|" + s.Port, L.T("에이징 콘솔.log 쓰기 실패 :" + s.Port + ": " + e.Message, "aging console log write failed :" + s.Port + ": " + e.Message)); }
            }

            public void Close()
            {
                Flush();
                try { fs?.Dispose(); } catch (Exception) { }
                fs = null;
            }
        }

        sealed class Follower
        {
            const int MaxLine = 8192;
            readonly EventDetector d;
            readonly Cable c;
            readonly Hub hub;
            readonly RingBuffer ring;
            readonly bool detect;                       // 이 케이블을 판정하나(events.enabled — 끄면 세션 콘솔.log 만)
            /// <summary>판정기 — 추적 스레드가 만든다(정규식 컴파일·jsonl 끝 읽기를 attach 처리기 밖에서). 판정하지 않는 동안은 null.</summary>
            public volatile EventScanner Scanner;
            public volatile bool Paused, PausedAck;
            /// <summary>지금 따르는 에이징 세션(없으면 null) · 처음 맞추기를 마쳤나 · 스레드가 살아 있나.</summary>
            volatile AgingSession cur;
            public AgingSession Current => cur;
            public volatile bool Synced, Alive = true;
            public int Port => c.TcpPort;
            ConsoleOut console;
            long breakAt = -1;
            readonly object brLock = new object();
            (string Com, DateTime? DetachedAt, DateTime Wall) brInfo;
            (DateTime Wall, long Mono, long At)? detach;    // 분리: 링이 그 위치(분리 때 끝)까지 읽히면 분리 기록(D8)
            readonly DateTime startWall;
            readonly string startCom;

            // 송신 줄 조립(TX 잠금 안에서 불린다 — 짧게)
            readonly object txLock = new object();
            readonly List<byte> tx = new List<byte>();
            int txEsc;                                  // 0 일반, 1 ESC 뒤, 2 CSI/SS3 안
            bool txEdited;                              // 이 송신 줄에 ESC(화살표)·Tab 이 있었음
            // 송신 줄(Text != null)과 송신 바이트 표시(Text == null, Typed = 그때 치던 글자)를 순서대로. 줄은 따로 256개까지, 바이트 표시는 잇단 것을 하나로(W9 — 긴 붙여넣기가 reboot 줄을 밀어내지 않게)
            readonly LinkedList<(long RxPos, long Mono, string Text, bool Edited, string Typed)> txq = new LinkedList<(long, long, string, bool, string)>();
            int txLines;

            // 수신 줄 조립(추적 스레드)
            readonly List<byte> line = new List<byte>(256);
            long lineStart = -1, lastDataMono;
            DateTime lastWall;
            long lastMono;

            /// <summary>attach 처리기 안(스캔 스레드)에서 불린다 — 송신 관측 구독만 하고 무거운 일은 추적 스레드로(뒤 처리기·telnet 배너를 늦추지 않게).</summary>
            public Follower(EventDetector d, Cable c, bool detect)
            {
                this.d = d; this.c = c; this.detect = detect; hub = d.hub; ring = c.Ring;
                startWall = hub.Clock.Now; startCom = c.Com;
                c.Transmitted += OnTx;                        // attach 직후 보낸 reboot 도 놓치지 않게 먼저 구독(줄은 판정기가 생길 때까지 큐에)
            }

            public void Start()
            {
                long from = c.AttachRxPos;
                var t = new Thread(() => Run(from)) { IsBackground = true, Name = "Events-" + c.Id };
                lock (d.threads) { d.threads.RemoveAll(x => !x.IsAlive); d.threads.Add(t); }
                t.Start();
            }

            /// <summary>분리 처리기(스캔 스레드): 분리 때 링 끝까지 읽은 뒤 분리 기록을 남기게 한다.</summary>
            public void Detached(DateTime wall, long mono, long at)
            {
                lock (brLock) detach = (wall, mono, at);
            }

            /// <summary>같은 케이블 객체의 다음 attach: 그 위치에 오면 열린 줄을 끊고 연결 기록(다시 연결 — 분리 동안의 공백)을 남긴다.</summary>
            public void SessionBreak(long pos, string com, DateTime? detachedAt, DateTime wall)
            {
                lock (brLock) { brInfo = (com, detachedAt, wall); breakAt = pos; }
            }

            void OnTx(TxRecord rec)
            {
                if (rec.Source == TxSource.Probe || rec.Source == TxSource.TerminalReply || rec.Data == null) return;
                lock (txLock)
                {
                    foreach (byte b in rec.Data)
                    {
                        if (txEsc == 1) { txEsc = b == (byte)'[' || b == (byte)'O' ? 2 : 0; continue; }
                        if (txEsc == 2) { if (b >= 0x40 && b <= 0x7e) txEsc = 0; continue; }
                        if (b == 0x0d || b == 0x0a)
                        {
                            if (txLines < 256) { txq.AddLast((rec.RxPos, rec.MonoMs, Encoding.UTF8.GetString(tx.ToArray()), txEdited, null)); txLines++; }
                            tx.Clear();
                            txEdited = false;
                        }
                        else if (b == 0x1b) { txEsc = 1; txEdited = true; }
                        else if (b == 0x08 || b == 0x7f) { if (tx.Count > 0) tx.RemoveAt(tx.Count - 1); }
                        else if (b == 0x15 || b == 0x03) { tx.Clear(); txEdited = false; }
                        else if (b == 0x10 || b == 0x0e) txEdited = true;                       // Ctrl-P/N: 셸 기록 회상
                        else if ((b >= 0x20 || b == 0x09) && tx.Count < 4096) { tx.Add(b); if (b == 0x09) txEdited = true; }
                    }
                    // 송신 바이트 표시(구간 다시 열기 — 페이저 키 등, 치는 중인 글자): 바로 앞이 바이트 표시면 그것을 고쳐 하나로
                    if (rec.Data.Length > 0)
                    {
                        var mark = (rec.RxPos, rec.MonoMs, (string)null, false, Encoding.UTF8.GetString(tx.ToArray()));
                        if (txq.Last != null && txq.Last.Value.Text == null) txq.Last.Value = (txq.Last.Value.RxPos, rec.MonoMs, null, false, mark.Item5);
                        else txq.AddLast(mark);
                    }
                }
            }

            /// <summary>RxPos ≤ upTo 인 송신 줄(그 RX 바이트보다 먼저 보낸 것)을 판정기에 넣는다(판정하지 않는 동안은 버린다 — 큐가 쌓이지 않게).</summary>
            void DrainTx(long upTo)
            {
                while (true)
                {
                    (long RxPos, long Mono, string Text, bool Edited, string Typed) t;
                    lock (txLock)
                    {
                        if (txq.First == null || txq.First.Value.RxPos > upTo) return;
                        t = txq.First.Value;
                        txq.RemoveFirst();
                        if (t.Text != null) txLines--;
                    }
                    var sc = Scanner;
                    if (sc == null) continue;
                    if (t.Text == null) sc.OnTxBytes(t.Mono, t.Typed);
                    else sc.OnTxLine(t.Text, t.Mono, t.Edited);
                }
            }

            /// <summary>분리 위치까지 읽었으면(또는 다시 연결 위치에 왔으면): 열린 줄을 끊고 분리 기록.</summary>
            void CheckDetach(long pos, bool force)
            {
                (DateTime Wall, long Mono, long At)? dt;
                lock (brLock) dt = detach;
                if (!dt.HasValue || (!force && pos < dt.Value.At)) return;
                lock (brLock) { if (detach.Equals(dt)) detach = null; }
                if (line.Count > 0) Complete(true);
                console?.Marker(dt.Value.Wall, console.T("케이블 분리 — 다시 연결될 때까지 기록 없음", "Cable disconnected — no record until it is reconnected"));
                console?.Flush();
                Scanner?.OnDetach(null, dt.Value.Wall, dt.Value.Mono, ring.Format(Math.Min(pos, dt.Value.At)));
            }

            /// <summary>다시 연결 위치에 왔으면: 열린 줄을 끊고 연결 기록.</summary>
            void CheckBreak(long pos)
            {
                long br;
                (string Com, DateTime? DetachedAt, DateTime Wall) info;
                lock (brLock) { br = breakAt; info = brInfo; }
                if (br < 0 || pos < br) return;
                lock (brLock) { if (breakAt == br) breakAt = -1; }
                CheckDetach(pos, true);
                if (line.Count > 0) Complete(true);
                double? gap = info.DetachedAt.HasValue ? Math.Max(0, (info.Wall - info.DetachedAt.Value).TotalSeconds) : (double?)null;
                console?.Marker(info.Wall, console.T("케이블 다시 연결 " + (info.Com ?? "") + (gap.HasValue ? " — 기록 끊김 " + AgingText.Duration(gap.Value, cur?.Lang) : ""),
                                                     "Cable reconnected " + (info.Com ?? "") + (gap.HasValue ? " — recording gap " + AgingText.Duration(gap.Value, cur?.Lang) : "")));
                Scanner?.OnAttach("reattach", info.Com, gap, info.DetachedAt.HasValue ? new DateTimeOffset(info.DetachedAt.Value) : (DateTimeOffset?)null,
                                  info.Wall, hub.Clock.MonoMs, ring.Format(br));
            }

            /// <summary>
            /// 세션 맞추기(추적 스레드, 매 차례): 저장소의 그 포트 세션이 지금 따르는 것과 다르면 옛 세션의 콘솔.log·판정기를 닫고 새 것을 연다.
            /// 판정기가 필요한 때 = 판정 켬 그리고 (세션 중 또는 layout 1·2). 처음(허브 시작·새 케이블 객체)이면 기록 공백(허브 시작 공백)과 함께 attach "start".
            /// </summary>
            void Sync(long pos, bool first)
            {
                var want = d.Sessions?.Get(c.TcpPort);
                if (!first && ReferenceEquals(want, cur)) return;
                bool hadSession = cur != null;
                if (cur != null)
                {
                    console?.Marker(hub.Clock.Now, console.T("에이징 끝", "Aging ended"));
                    console?.Close();
                    console = null;
                    cur = null;
                }
                if (!first && Scanner != null && (hadSession || want != null))
                {
                    Scanner.Close(hadSession ? AgingLang.T(Scanner.Lang, "에이징 끝", "aging ended") : AgingLang.T(Scanner.Lang, "에이징 시작", "aging started"));
                    Scanner = null;
                }
                if (want != null) { console = new ConsoleOut(want, hub.Log, hub.Clock.Now); cur = want; }
                bool need = detect && (want != null || !d.Gated);
                if (need && Scanner == null)
                {
                    try
                    {
                        // 시작은 hub.log 에 남기지 않는다(실패만) — 기존 시나리오가 hub.log 줄 번호를 기대값에 쓴다(Phase A ok: 불변)
                        var sc = new EventScanner(d.Dir, c.TcpPort, c.Id, () => c.Board ?? hub.State.GetBoard(c.Id), () => c.Writer?.CurrentLogPath,
                                                  EventPatterns.For(hub.Config, c.Id), hub.Config, hub.Log, d.Budget(), want != null ? want.EventsDir : d.Gated ? null : d.TxtDir, want != null,
                                                  want?.Lang);
                        if (first)
                        {
                            // 허브 시작 공백: 마지막 기록이 분리면 그때부터(분리 동안에도 alive 하트비트는 돈다, D8), 아니면 이전 코어의 마지막 하트비트와 마지막 기록 중 늦은 것부터
                            DateTime? prev = sc.LastRecordTs?.LocalDateTime;
                            if (sc.LastRecordKind != "detach")
                                lock (d.followers) if (d.lastAlive.TryGetValue(c.TcpPort, out var al) && (!prev.HasValue || al > prev.Value)) prev = al;
                            double? gap = prev.HasValue ? Math.Max(0, (startWall - prev.Value).TotalSeconds) : (double?)null;
                            sc.OnAttach("start", startCom, gap, prev.HasValue ? new DateTimeOffset(prev.Value) : (DateTimeOffset?)null, startWall, hub.Clock.MonoMs, ring.Format(pos));
                        }
                        else sc.OnAttach(want != null ? "session" : "session_end", c.Com, null, null, hub.Clock.Now, hub.Clock.MonoMs, ring.Format(pos));
                        Scanner = sc;
                    }
                    catch (Exception e) { hub.Log.WriteThrottled("events-start|" + c.Id, L.T("사건 감지 시작 실패 " + c.Id + ": " + e.GetType().Name + ": " + e.Message, "event detection failed to start " + c.Id + ": " + e.GetType().Name + ": " + e.Message)); }
                }
                Synced = true;
            }

            void Run(long from)
            {
                try { Sync(from, true); }
                catch (Exception e) { hub.Log.WriteThrottled("events-run|" + c.Id, L.T("사건 감지 오류 " + c.Id + ": " + e.GetType().Name + ": " + e.Message, "event detection error " + c.Id + ": " + e.GetType().Name + ": " + e.Message)); }
                var rc = ring.OpenCursor(from);
                long lastTick = 0;
                while (true)
                {
                    try
                    {
                        if (Paused) { PausedAck = true; Thread.Sleep(20); continue; }
                        PausedAck = false;
                        Sync(rc.Position, false);
                        var rd = rc.Next(200);
                        long now = hub.Clock.MonoMs;
                        switch (rd.Status)
                        {
                            case RingReadStatus.Data:
                                DrainTx(rd.Offset);
                                CheckDetach(rd.Offset, false);
                                CheckBreak(rd.Offset);
                                Feed(rd.Data, rd.Offset, rd.MonoMs >= 0 ? rd.Wall : hub.Clock.Now, rd.MonoMs >= 0 ? rd.MonoMs : now);
                                break;
                            case RingReadStatus.Lost:
                                line.Clear(); lineStart = -1;
                                console?.Marker(hub.Clock.Now, console.T("기록 놓침 " + rd.Skipped.ToString(CultureInfo.InvariantCulture) + "바이트(허브가 따라 읽기 전에 버퍼가 넘침)",
                                                                         "Missed " + rd.Skipped.ToString(CultureInfo.InvariantCulture) + " bytes (the buffer overflowed before the hub read it)"));
                                Scanner?.OnLost(rd.Skipped, hub.Clock.Now, now, ring.Format(rd.Offset));
                                break;
                            case RingReadStatus.Timeout:
                                DrainTx(rc.Position);
                                // 진짜 프롬프트 = 개행 없이 열린 채 RX 가 300ms 이상 조용(C.6)
                                if (line.Count > 0 && now - lastDataMono >= Math.Max(300, hub.Config.TailFlushMs)) Complete(true);
                                CheckDetach(rc.Position, false);
                                CheckBreak(rc.Position);
                                console?.Flush();
                                break;
                            case RingReadStatus.Closed:
                                DrainTx(long.MaxValue);
                                CheckDetach(long.MaxValue, true);
                                if (line.Count > 0) Complete(true);
                                Scanner?.Close(AgingLang.T(Scanner?.Lang, "허브 정지", "hub stopped"));
                                console?.Close();                                     // 세션은 sessions.json 에 남는다(다시 켜지면 이어 감)
                                Alive = false;
                                lock (d.followers) d.followers.Remove(c);
                                c.Transmitted -= OnTx;
                                return;
                        }
                        if (now - lastTick >= 200) { lastTick = now; Scanner?.Tick(hub.Clock.Now, now); console?.Flush(); }
                    }
                    catch (Exception e)
                    {
                        hub.Log.WriteThrottled("events-run|" + c.Id, L.T("사건 감지 오류 " + c.Id + ": " + e.GetType().Name + ": " + e.Message, "event detection error " + c.Id + ": " + e.GetType().Name + ": " + e.Message));
                        line.Clear(); lineStart = -1;
                        Thread.Sleep(100);
                    }
                }
            }

            void Feed(byte[] data, long offset, DateTime wall, long mono)
            {
                lastWall = wall; lastMono = mono; lastDataMono = mono;
                cur?.NoteRx(wall);
                for (int i = 0; i < data.Length; i++)
                {
                    if (lineStart < 0) lineStart = offset + i;
                    byte b = data[i];
                    if (b == (byte)'\n') { Complete(false); continue; }
                    line.Add(b);
                    if (line.Count >= MaxLine) Complete(true);
                }
            }

            /// <summary>모은 줄을 판정기에 넣는다(\r 로 나눈 구간마다 ANSI·NUL·CR 을 지운 UTF-8). 세션 중이면 콘솔.log 에도(DiskWriter 처럼 NUL 만 빼고 끝 CR 을 지운 글).</summary>
            void Complete(bool partial)
            {
                var arr = line.ToArray();
                string rxOff = ring.Format(lineStart < 0 ? 0 : lineStart);
                line.Clear();
                lineStart = -1;
                DateTime wall = lastWall == default ? hub.Clock.Now : lastWall;
                if (console != null) console.Line(wall, Encoding.UTF8.GetString(arr).Replace("\0", "").TrimEnd('\r'));
                var sc = Scanner;
                if (sc == null) return;
                var segs = EventLines.Segments(arr);
                sc.OnLine(string.Concat(segs), segs, wall, lastMono, rxOff, partial);
            }
        }
    }

    /// <summary>수신 줄 바이트 → 판정 구간(시험의 실보드 로그 재생도 같은 규칙을 쓴다).</summary>
    public static class EventLines
    {
        /// <summary>\r 로 나눈 구간마다 ANSI(CSI·OSC·ESC+1)·NUL·CR 을 지우고 UTF-8 로 읽는다. 빈 구간은 뺀다.</summary>
        public static List<string> Segments(byte[] arr)
        {
            var segs = new List<string>();
            int st = 0;
            for (int i = 0; i <= arr.Length; i++)
            {
                if (i < arr.Length && arr[i] != (byte)'\r') continue;
                if (i > st)
                {
                    var b = RxText.StripAnsi(arr, st, i - st);
                    if (b.Length > 0) segs.Add(Encoding.UTF8.GetString(b));
                }
                st = i + 1;
            }
            return segs;
        }
    }
}
