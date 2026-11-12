// 에이징 세션(부록 C.12): 사용자가 일부러 시작한 동안만 포트의 콘솔 줄·에이징 이벤트를 사람이 보는 폴더에 남긴다.
//   세션 폴더 <기준>\에이징\<이름>_<yyyyMMdd-HHmm>\ = 콘솔.log(세션 동안 RX 줄 — DiskWriter .log 와 같은 줄머리, 사건 추적 스레드가 쓴다) ·
//   이벤트\<HHMMSS>_<종류>.txt(부록 C.11 형식) · 끝낼 때 요약.txt.
//   세션 상태는 <허브 폴더>\sessions.json 에 남아 허브·코어가 다시 켜져도 이어 간다(코어 SessionStore 하나만 쓴다).
//   세션은 COM 을 열고 닫지 않는다(시작은 연결된 케이블에만 — 아니면 거부, /connect-uart 안내).
//   여기에는 모양·이름·글(트레이·CLI·요약 공통)과 저장소만 있다. 줄 쓰기는 EventDetector(Events.cs), 명령은 ControlServer 가 한다.
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
    /// <summary>에이징 세션 하나(포트별). 저장 필드 + 코어 안에서만 쓰는 실행 중 값(콘솔.log 크기·줄 수·마지막 수신 시각).</summary>
    public sealed class AgingSession
    {
        public const string ConsoleName = "콘솔.log", EventsDirName = "이벤트", SummaryName = "요약.txt";

        public int Port;
        public string Cable, Com, Board, Name, Folder;
        /// <summary>시작 시각(현지).</summary>
        public DateTime Started;

        // ---- 실행 중 값(코어의 추적 스레드가 바꾸고 제어 스레드가 읽는다)
        long consoleBytes, consoleLines, lastRxTicks;
        public long ConsoleBytes => Interlocked.Read(ref consoleBytes);
        public long ConsoleLines => Interlocked.Read(ref consoleLines);
        /// <summary>세션 동안(이 코어 실행에서) 마지막으로 RX 를 받은 시각. 아직 없으면 null.</summary>
        public DateTime? LastRx { get { long t = Interlocked.Read(ref lastRxTicks); return t == 0 ? (DateTime?)null : new DateTime(t); } }
        public void SetConsole(long bytes, long lines) { Interlocked.Exchange(ref consoleBytes, bytes); Interlocked.Exchange(ref consoleLines, lines); }
        public void AddConsole(long bytes, long lines) { Interlocked.Add(ref consoleBytes, bytes); Interlocked.Add(ref consoleLines, lines); }
        public void NoteRx(DateTime wall) => Interlocked.Exchange(ref lastRxTicks, wall.Ticks);

        /// <summary>콘솔.log 크기·줄 수: 추적 스레드가 연 뒤면 그 값, 아직이면(케이블이 없음 등) 파일을 직접 잰다(없으면 0).</summary>
        public void ConsoleStats(out long bytes, out long lines)
        {
            bytes = ConsoleBytes; lines = ConsoleLines;
            if (bytes > 0) return;
            try
            {
                var fi = new FileInfo(ConsolePath);
                if (!fi.Exists) return;
                bytes = fi.Length;
                using var r = new FileStream(ConsolePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buf = new byte[1 << 16];
                int k;
                while ((k = r.Read(buf, 0, buf.Length)) > 0) for (int i = 0; i < k; i++) if (buf[i] == (byte)'\n') lines++;
            }
            catch (Exception) { }
        }

        public string ConsolePath => Path.Combine(Folder ?? "", ConsoleName);
        public string EventsDir => Path.Combine(Folder ?? "", EventsDirName);
        public string SummaryPath => Path.Combine(Folder ?? "", SummaryName);

        public static string Iso(DateTime t) => new DateTimeOffset(t).ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture);

        public JObj ToJson() => new JObj().Set("port", (long)Port).Set("cable", Cable).Set("com", Com).Set("board", Board).Set("name", Name).Set("folder", Folder).Set("started", Iso(Started));

        public static AgingSession FromJson(JsonElement e)
        {
            string st = HubFiles.Str(e, "started");
            if (!DateTimeOffset.TryParse(st ?? "", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t)) return null;
            var s = new AgingSession
            {
                Port = (int)HubFiles.Long(e, "port"), Cable = HubFiles.Str(e, "cable"), Com = HubFiles.Str(e, "com"), Board = HubFiles.Str(e, "board"),
                Name = HubFiles.Str(e, "name"), Folder = HubFiles.Str(e, "folder"), Started = t.LocalDateTime,
            };
            return s.Port > 0 && !string.IsNullOrEmpty(s.Folder) && !string.IsNullOrEmpty(s.Name) ? s : null;
        }
    }

    /// <summary>세션 이름·폴더 이름(윈도 파일 이름 규칙).</summary>
    public static class AgingNames
    {
        public const int MaxName = 60;
        static readonly Regex Reserved = new Regex(@"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\..*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// 사용자가 준 이름 → 폴더에 쓸 수 있는 이름: 파일 이름에 못 쓰는 글자·제어 문자는 '_', 앞뒤 공백·점은 지우고 잇단 '_' 는 하나로, 60자까지,
        /// 윈도 예약 이름(CON·NUL·COM1 …)은 앞에 '_'. 다 지워져 비면 null(부르는 쪽이 기본 이름을 쓴다).
        /// </summary>
        public static string Sanitize(string name)
        {
            if (name == null) return null;
            var bad = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (char ch in name.Normalize(NormalizationForm.FormC))
                sb.Append(Array.IndexOf(bad, ch) >= 0 || "\\/:*?\"<>|".IndexOf(ch) >= 0 || char.IsControl(ch) ? '_' : ch);
            string s = Regex.Replace(sb.ToString(), @"\s+", " ");
            s = Regex.Replace(s, "_{2,}", "_").Trim().Trim('.', ' ').Trim();
            if (s.Length > MaxName)
            {
                int k = MaxName;
                if (char.IsHighSurrogate(s[k - 1])) k--;
                s = s.Substring(0, k).TrimEnd('.', ' ');
            }
            if (s.Length == 0 || s.All(c => c == '_')) return null;
            if (Reserved.IsMatch(s)) s = "_" + s;
            return s;
        }

        public static string Stamp(DateTime t) => t.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);

        /// <summary>트레이 이름 창·CLI 의 기본 이름 "&lt;보드&gt;_&lt;yyyyMMdd-HHmm&gt;"(보드를 모르면 COM, 그것도 없으면 포트).</summary>
        public static string DefaultName(string board, string com, int port, DateTime t)
            => Sanitize((!string.IsNullOrWhiteSpace(board) ? board : !string.IsNullOrWhiteSpace(com) ? com : "포트" + port.ToString(CultureInfo.InvariantCulture)) + "_" + Stamp(t))
               ?? "에이징_" + Stamp(t);

        /// <summary>폴더 이름 "&lt;이름&gt;_&lt;yyyyMMdd-HHmm&gt;" — 이름이 이미 그 시각으로 끝나면(기본 이름) 한 번만.</summary>
        public static string FolderName(string name, DateTime started)
        {
            string st = Stamp(started);
            return name.EndsWith("_" + st, StringComparison.Ordinal) ? name : name + "_" + st;
        }
    }

    /// <summary>세션 글(트레이 정보 줄·CLI 한 줄·요약 — UI_COPY.md "에이징 세션").</summary>
    public static class AgingText
    {
        /// <summary>이 시간(초)보다 오래 RX 가 없으면 "출력 없음 N분"(보드·케이블이 멈췄는지 보라는 표시).</summary>
        public const double NoOutputS = 600;

        /// <summary>"2시간 13분" · "45분" · "30초"(1분 미만) · "3일 4시간".</summary>
        public static string Duration(double s)
        {
            if (double.IsNaN(s) || s < 0) s = 0;
            long t = (long)Math.Floor(s);
            if (t < 60) return t.ToString(CultureInfo.InvariantCulture) + "초";
            long m = t / 60, h = m / 60, d = h / 24;
            if (d > 0) return d.ToString(CultureInfo.InvariantCulture) + "일 " + (h % 24).ToString(CultureInfo.InvariantCulture) + "시간";
            if (h > 0) return h.ToString(CultureInfo.InvariantCulture) + "시간 " + (m % 60).ToString(CultureInfo.InvariantCulture) + "분";
            return m.ToString(CultureInfo.InvariantCulture) + "분";
        }

        /// <summary>
        /// 마지막 출력: "마지막 출력 3초 전" · "마지막 출력 4분 전" · 10분 넘게 없으면 "출력 없음 12분".
        /// 아직 받은 것이 없으면(lastRxAgeS null) 세션 경과로: 10분 안이면 "아직 출력 없음", 넘으면 "출력 없음 N분".
        /// </summary>
        public static string RxTail(double? lastRxAgeS, double elapsedS)
        {
            if (!lastRxAgeS.HasValue) return elapsedS > NoOutputS ? "출력 없음 " + Duration(elapsedS) : "아직 출력 없음";
            double a = Math.Max(0, lastRxAgeS.Value);
            if (a > NoOutputS) return "출력 없음 " + Duration(a);
            return "마지막 출력 " + Duration(a) + " 전";
        }

        /// <summary>트레이 메뉴 정보 줄 "에이징 중 · &lt;이름&gt; · 2시간 13분 · 마지막 출력 3초 전"(앞의 점은 메뉴가 그린다 — 기록 중 초록, Stale 이면 빨강).</summary>
        public static string MenuLine(string name, double elapsedS, double? lastRxAgeS)
            => "에이징 중 · " + name + " · " + Duration(elapsedS) + " · " + RxTail(lastRxAgeS, elapsedS);

        /// <summary>RxTail 이 "출력 없음 N분" 인 상태(10분 넘게 출력 없음, 아직 받은 것이 없으면 세션 경과로).</summary>
        public static bool Stale(double? lastRxAgeS, double elapsedS) => (lastRxAgeS ?? elapsedS) > NoOutputS;

        /// <summary>터미널 창 제목 끝에 붙는 글(세션 중인 포트).</summary>
        public const string TitleSuffix = " · 에이징 중";

        /// <summary>종류 이름(요약·CLI): panic → 커널 패닉 …</summary>
        public static string KindName(string kind) => kind switch
        {
            "panic" => "커널 패닉", "oops" => "Oops", "bug" => "BUG", "oom" => "OOM", "app_error" => "앱 오류", "confirm" => "패닉 뒤 응답 없음", "missed" => "기록 놓침",
            _ => kind,
        };
    }

    /// <summary>
    /// 세션 저장소(코어 하나만 쓴다): &lt;허브 폴더&gt;\sessions.json {"sessions":[{port, cable, com, board, name, folder, started}]}.
    /// 만들 때 읽고(허브 재시작 뒤 이어 감), 바뀔 때마다 원자 교체로 쓴다. 스레드 안전.
    /// </summary>
    public sealed class SessionStore
    {
        readonly object l = new object();
        readonly Dictionary<int, AgingSession> map = new Dictionary<int, AgingSession>();
        readonly HubLog log;
        public string Path { get; }

        public SessionStore(string hubDir, HubLog log)
        {
            this.log = log;
            Path = System.IO.Path.Combine(hubDir ?? "", "sessions.json");
            var j = HubFiles.ReadJson(Path);
            if (j.HasValue && j.Value.ValueKind == JsonValueKind.Object && j.Value.TryGetProperty("sessions", out var a) && a.ValueKind == JsonValueKind.Array)
                foreach (var e in a.EnumerateArray())
                {
                    var s = e.ValueKind == JsonValueKind.Object ? AgingSession.FromJson(e) : null;
                    if (s != null) map[s.Port] = s;
                }
        }

        public AgingSession Get(int port) { lock (l) return map.TryGetValue(port, out var s) ? s : null; }
        public List<AgingSession> All() { lock (l) return map.Values.OrderBy(s => s.Port).ToList(); }
        public int Count { get { lock (l) return map.Count; } }

        /// <summary>새 세션을 더한다(그 포트에 이미 있으면 false). 저장 실패는 예외(부르는 쪽이 거절로 바꾼다).</summary>
        public bool Add(AgingSession s)
        {
            lock (l)
            {
                if (map.ContainsKey(s.Port)) return false;
                map[s.Port] = s;
                try { Save(); }
                catch (Exception) { map.Remove(s.Port); throw; }
                return true;
            }
        }

        /// <summary>세션을 뺀다(없으면 null). 저장 실패는 hub.log 에만 — 세션은 끝난 것으로 본다.</summary>
        public AgingSession Remove(int port)
        {
            lock (l)
            {
                if (!map.TryGetValue(port, out var s)) return null;
                map.Remove(port);
                try { Save(); }
                catch (Exception e) { log?.Write("sessions.json 쓰기 실패(" + Path + "): " + e.Message); }
                return s;
            }
        }

        void Save() => AtomicFile.WriteAllText(Path, new JObj().Set("sessions", map.Values.OrderBy(s => s.Port).Select(s => s.ToJson())).ToString() + "\n");
    }

    /// <summary>요약.txt(끝낼 때) — 기간·부팅 수 cmd/soft/hard·이벤트 목록·기록 끊긴 구간. 사건 jsonl(그 포트, 세션 기간)을 읽어 만든다.</summary>
    public static class AgingSummary
    {
        static string T(DateTimeOffset t) => t.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        static string Hm(DateTimeOffset t) => t.LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        /// <summary>세션 기간의 사건(시각 순). jsonlDir = cfg.EventsJsonlDir.</summary>
        public static List<EventRec> Events(string jsonlDir, AgingSession s, DateTime until)
            => EventLog.Read(jsonlDir, new[] { s.Port }).Where(e => e.Ts.LocalDateTime >= s.Started.AddSeconds(-1) && e.Ts.LocalDateTime <= until.AddSeconds(1)).ToList();

        /// <summary>이벤트 목록에 올리는 사건: 커널·앱 오류·confirm·missed, 원인이 있는 부팅(목록에만 — 이벤트 수에는 넣지 않는다, 원인 사건이 이미 셈).</summary>
        public static bool Listed(EventRec e) => EventKinds.IsError(e.Kind) || e.Kind == "confirm" || e.Kind == "missed" || (e.Kind == "boot" && e.CauseKind != null);

        /// <summary>세션 기간의 기록 끊김(분리 ~ 다시 연결, 허브가 꺼져 있던 동안) — 세션 시작 앞부분은 잘라 낸다.</summary>
        public static List<(DateTimeOffset From, DateTimeOffset To, string Why)> Gaps(AgingSession s, List<EventRec> evs, DateTime until)
        {
            var since = new DateTimeOffset(s.Started);
            var ag = EventLog.Aging(s.Port, evs, since, new DateTimeOffset(until), null);
            using var d = JsonDocument.Parse(ag.ToString());
            var gaps = new List<(DateTimeOffset From, DateTimeOffset To, string Why)>();
            foreach (var g in d.RootElement.GetProperty("gaps").EnumerateArray())
            {
                DateTimeOffset.TryParse(g.GetProperty("from").GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var gf);
                DateTimeOffset.TryParse(g.GetProperty("to").GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var gt);
                if (gf < since) gf = since;
                if (gt <= gf) continue;                                                       // 세션 전의 공백(허브 시작 기록이 그 전부터 센 것)
                gaps.Add((gf, gt, g.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null));
            }
            return gaps;
        }

        public static string Text(AgingSession s, List<EventRec> evs, DateTime until)
        {
            var since = new DateTimeOffset(s.Started);
            var ag = EventLog.Aging(s.Port, evs, since, new DateTimeOffset(until), null);
            using var d = JsonDocument.Parse(ag.ToString());
            var o = d.RootElement;
            string N(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetRawText() : "0";
            double period = (until - s.Started).TotalSeconds, covered;
            var sb = new StringBuilder();
            sb.Append("에이징 요약\r\n");
            sb.Append("이름: ").Append(s.Name).Append("\r\n");
            sb.Append("보드: ").Append(string.IsNullOrWhiteSpace(s.Board) ? "알 수 없음" : s.Board).Append(" (포트 ").Append(s.Port.ToString(CultureInfo.InvariantCulture))
              .Append(s.Com != null ? " · " + s.Com : "").Append(")\r\n");
            sb.Append("기간: ").Append(T(since)).Append(" ~ ").Append(T(new DateTimeOffset(until))).Append(" (").Append(AgingText.Duration(period)).Append(")\r\n");
            var gaps = Gaps(s, evs, until);
            covered = Math.Max(0, period - gaps.Sum(g => (g.To - g.From).TotalSeconds));
            sb.Append("기록한 시간: ").Append(AgingText.Duration(covered)).Append(gaps.Count > 0 ? " (기록 끊김 " + gaps.Count + "번)" : "").Append("\r\n");
            sb.Append("콘솔 로그: ").Append(s.ConsolePath).Append(" (").Append(s.ConsoleLines.ToString("N0", CultureInfo.InvariantCulture)).Append("줄)\r\n");
            sb.Append("\r\n");
            var b = o.GetProperty("boots");
            sb.Append("부팅: 모두 ").Append(N(b, "total")).Append("번 · 명령(cmd) ").Append(N(b, "cmd")).Append(" · 소프트(soft) ").Append(N(b, "soft"))
              .Append(" · 하드(hard) ").Append(N(b, "hard")).Append(" (예상 못 한 재부팅 ").Append(N(o, "unexpected_reboots")).Append("번)\r\n");
            if (N(b, "suspect") != "0") sb.Append("  명령 출력 속 부팅 줄 ").Append(N(b, "suspect")).Append("번은 세지 않았습니다(옛 로그일 수 있음).\r\n");
            var iv = o.GetProperty("boot_interval_s");
            if (iv.ValueKind == JsonValueKind.Object)
                sb.Append("부팅 간격: 최소 ").Append(AgingText.Duration(iv.GetProperty("min").GetDouble())).Append(" · 중간 ").Append(AgingText.Duration(iv.GetProperty("median").GetDouble()))
                  .Append(" · 최대 ").Append(AgingText.Duration(iv.GetProperty("max").GetDouble())).Append("\r\n");
            sb.Append("\r\n");
            var listed = evs.Where(Listed).ToList();
            sb.Append("이벤트: ").Append(listed.Count(e => e.Suspect == null && e.Kind != "boot")).Append("건")
              .Append(listed.Any(e => e.Suspect != null && e.Kind != "boot") ? " (+ 의심 " + listed.Count(e => e.Suspect != null && e.Kind != "boot") + "건)" : "").Append("\r\n");
            if (listed.Count == 0) sb.Append("  없음\r\n");
            foreach (var e in listed)
            {
                string what = e.Kind == "boot" ? EventBalloons.What("boot", e.Class, e.CauseKind) : AgingText.KindName(e.Kind);
                sb.Append("  ").Append(Hm(e.Ts)).Append("  ").Append(what).Append(e.Suspect != null ? " (의심)" : "").Append("  ").Append(EventScanner.Trunc(e.Line, 120));
                string ctx = e.Kind == "boot" ? e.CauseContext ?? e.Context : e.Context;
                if (ctx != null) sb.Append("  → ").Append(ctx.StartsWith(s.Folder + "\\", StringComparison.OrdinalIgnoreCase) ? ctx.Substring(s.Folder.Length + 1) : ctx);
                sb.Append("\r\n");
            }
            sb.Append("\r\n");
            sb.Append("기록 끊긴 구간: ").Append(gaps.Count == 0 ? "없음" : gaps.Count + "번").Append("\r\n");
            foreach (var g in gaps)
                sb.Append("  ").Append(T(g.From)).Append(" ~ ").Append(T(g.To)).Append(" (").Append(AgingText.Duration((g.To - g.From).TotalSeconds)).Append(", ").Append(GapWhy(g.Why)).Append(")\r\n");
            return sb.ToString();
        }

        public static string GapWhy(string reason) => reason switch
        {
            "start" => "허브 다시 시작",
            "reattach" => "케이블 분리",
            "detached" => "케이블 분리, 다시 연결 안 됨",
            _ => "기록 없음",
        };
    }
}
