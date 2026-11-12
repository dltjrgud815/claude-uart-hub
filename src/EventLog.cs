// 사건 기록 읽기(계획 X6·부록 C.4·C.5): jsonl 한 줄(EventRec), CLI 의 events·aging(EventLog), 이어 읽기(EventTail — CLI --follow·감독 풍선),
// 트레이 풍선 묶기(EventBalloons — 포트당 1분에 1번, 묶인 수 표시, 한 번에 여러 포트면 한 풍선), 대상 해석(포트·COMx·alias·보드명 — 허브 없이).
// 모두 읽기만 한다(쓰는 쪽은 코어의 EventScanner 하나뿐).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UartHub
{
    /// <summary>사건 한 줄(&lt;log_root&gt;\events\&lt;port&gt;.jsonl).</summary>
    public sealed class EventRec
    {
        public DateTimeOffset Ts;
        public int Port;
        public string Cable, Board, Kind, Class, Line, Log, LogLine, Context, ContextSkipped, Com, Reason;
        /// <summary>log_layout 2: 더 심한 종류가 합류해 txt 이름이 바뀐 기록의 옛 경로(앞 기록들의 context). 없으면 null.</summary>
        public string ContextWas;
        /// <summary>명령 출력 속 줄일 수 있음("cmd_output", 부록 C.6) — 실시간 사건이면 null.</summary>
        public string Suspect;
        /// <summary>부팅의 원인(C.7 — 직전 panic/oops 줄): 종류·줄·시각·앞뒤 로그. confirm 의 of = 확인한 종류.</summary>
        public string CauseKind, CauseLine, CauseTs, CauseContext, Of;
        /// <summary>hard 부팅 직전 수신 무음(초), confirm 의 무응답(초).</summary>
        public double? SilenceS;
        public long BootSeq;
        public double? SinceBootS, GapS;
        public long? Skipped;
        /// <summary>원문 JSON 줄(끝 개행 없음).</summary>
        public string Raw;

        /// <summary>JSON 객체 한 줄 → 사건. ts·kind 가 없거나 JSON 이 아니거나 읽다 실패하면(홀로 남은 서로게이트 등) null — 예외를 내지 않는다(H5).</summary>
        public static EventRec Parse(string line)
        {
            if (line == null) return null;
            line = line.Trim().Trim('\0').Trim();                                   // 전원 차단으로 생긴 NUL 채움(M3)
            if (line.Length == 0 || line[0] != '{') return null;
            try
            {
                using var d = JsonDocument.Parse(line);
                var o = d.RootElement;
                if (o.ValueKind != JsonValueKind.Object) return null;
                string ts = HubFiles.Str(o, "ts"), kind = HubFiles.Str(o, "kind");
                if (ts == null || kind == null || !DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t)) return null;
                var r = new EventRec
                {
                    Ts = t, Port = (int)HubFiles.Long(o, "port"), Cable = HubFiles.Str(o, "cable"), Board = HubFiles.Str(o, "board"), Kind = kind,
                    Class = HubFiles.Str(o, "class"), Line = HubFiles.Str(o, "line"), Log = HubFiles.Str(o, "log"), LogLine = HubFiles.Str(o, "log_line"),
                    Context = HubFiles.Str(o, "context"), ContextSkipped = HubFiles.Str(o, "context_skipped"), Com = HubFiles.Str(o, "com"), Reason = HubFiles.Str(o, "reason"),
                    Suspect = HubFiles.Str(o, "suspect"),
                    BootSeq = HubFiles.Long(o, "boot_seq"), Raw = line,
                };
                if (o.TryGetProperty("since_boot_s", out var sb) && sb.ValueKind == JsonValueKind.Number) r.SinceBootS = sb.GetDouble();
                if (o.TryGetProperty("gap_s", out var gs) && gs.ValueKind == JsonValueKind.Number) r.GapS = gs.GetDouble();
                if (o.TryGetProperty("skipped", out var sk) && sk.ValueKind == JsonValueKind.Number) r.Skipped = sk.GetInt64();
                if (o.TryGetProperty("silence_s", out var si) && si.ValueKind == JsonValueKind.Number) r.SilenceS = si.GetDouble();
                r.Of = HubFiles.Str(o, "of");
                r.ContextWas = HubFiles.Str(o, "context_was");
                if (o.TryGetProperty("cause", out var ca) && ca.ValueKind == JsonValueKind.Object)
                {
                    r.CauseKind = HubFiles.Str(ca, "kind"); r.CauseLine = HubFiles.Str(ca, "line"); r.CauseTs = HubFiles.Str(ca, "ts"); r.CauseContext = HubFiles.Str(ca, "context");
                }
                return r;
            }
            catch (Exception) { return null; }
        }

        /// <summary>종류 거르기 이름: 그 종류, boot 는 "boot"·"boot:cmd|soft|hard" 둘 다.</summary>
        public bool KindIn(ICollection<string> kinds) => kinds == null || kinds.Contains(Kind) || (Kind == "boot" && Class != null && kinds.Contains("boot:" + Class));
    }

    public static class EventLog
    {
        public static string Dir(string logRoot) => Path.Combine(logRoot ?? "", "events");

        /// <summary>사건 시각 문자열(현지 시각 + 오프셋, 밀리초).</summary>
        public static string Ts(DateTime wall) => new DateTimeOffset(wall).ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
        static string Iso(DateTimeOffset t) => t.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture);

        /// <summary>
        /// CLI 가 보는 설정(C-2 — 코어·감독과 같은 규칙): 허브 폴더의 config.json → (검증 실패면) config.last-good.json → 기본값. log_root 가 없으면 허브 폴더의 부모.
        /// </summary>
        public static Config ConfigOf(string hubDir)
        {
            foreach (var p in new[] { HubFiles.ConfigPath(hubDir), HubFiles.LastGood(hubDir) })
            {
                string t = File.Exists(p) ? HubFiles.ReadShared(p) : null;
                if (t == null) continue;
                try
                {
                    var c = Config.Parse(t.TrimStart('﻿'));
                    if (c.Validate().Count > 0) continue;
                    c.HubDir = hubDir;                                                        // 코어와 같음(CoreMain 이 --hub-dir 로 고정)
                    c.ResolveLogRoot(hubDir);
                    return c;
                }
                catch (ConfigException) { }
            }
            var d = Config.Default();
            d.HubDir = hubDir;
            d.ResolveLogRoot(hubDir);
            return d;
        }

        public static string LogRootOf(string hubDir) => ConfigOf(hubDir).LogRoot;

        static readonly Regex RelRe = new Regex(@"^\s*(\d+(?:\.\d+)?)\s*([smhd])\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>--since: 12h · 30m · 45s · 2d(지금부터 거꾸로) 또는 ISO 시각(오프셋 없으면 현지 시각).</summary>
        public static bool TryParseSince(string s, DateTimeOffset now, out DateTimeOffset since)
        {
            since = default;
            if (string.IsNullOrWhiteSpace(s)) return false;
            var m = RelRe.Match(s);
            if (m.Success)
            {
                double v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                double sec = v * (char.ToLowerInvariant(m.Groups[2].Value[0]) switch { 's' => 1, 'm' => 60, 'h' => 3600, _ => 86400 });
                since = now.AddSeconds(-sec);
                return true;
            }
            return DateTimeOffset.TryParse(s.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out since);
        }

        /// <summary>폴더의 &lt;port&gt;.jsonl 포트 번호(정렬).</summary>
        public static List<int> Ports(string dir)
        {
            var l = new List<int>();
            if (!Directory.Exists(dir)) return l;
            foreach (var f in Directory.GetFiles(dir, "*.jsonl"))
                if (int.TryParse(Path.GetFileNameWithoutExtension(f), NumberStyles.None, CultureInfo.InvariantCulture, out int p) && p > 0) l.Add(p);
            l.Sort();
            return l;
        }

        public static string FileOf(string dir, int port) => Path.Combine(dir, port.ToString(CultureInfo.InvariantCulture) + ".jsonl");

        /// <summary>포트들(null = 폴더의 전부)의 사건 전부(시각 순). 깨진 줄·쓰는 중인 마지막 줄(개행 없음)은 건너뛴다.</summary>
        public static List<EventRec> Read(string dir, IEnumerable<int> ports)
        {
            var all = new List<EventRec>();
            foreach (int p in ports ?? Ports(dir))
            {
                string f = FileOf(dir, p);
                string t = File.Exists(f) ? HubFiles.ReadShared(f) : null;
                if (t == null) continue;
                int end = t.LastIndexOf('\n');
                if (end < 0) continue;
                foreach (var l in t.Substring(0, end).Split('\n'))
                {
                    var r = EventRec.Parse(l);
                    if (r != null) all.Add(r);
                }
            }
            var sorted = all.OrderBy(r => r.Ts).ThenBy(r => r.Port).ToList();
            ApplyRenames(sorted, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            return sorted;
        }

        /// <summary>
        /// txt 이름 바뀜(context_was → context)을 앞 기록들의 context·cause.context 에 적용한다(원문 JSON 의 그 경로 글자도). renames 는 이어 읽기가 이어 쓴다.
        /// 이름이 여러 번 바뀌면 끝 이름까지 따라간다.
        /// </summary>
        public static void ApplyRenames(List<EventRec> evs, Dictionary<string, string> renames)
        {
            foreach (var e in evs) if (e.ContextWas != null && e.Context != null && !string.Equals(e.ContextWas, e.Context, StringComparison.OrdinalIgnoreCase)) renames[e.ContextWas] = e.Context;
            if (renames.Count == 0) return;
            string Final(string p)
            {
                for (int i = 0; p != null && i < 8 && renames.TryGetValue(p, out var n); i++) p = n;
                return p;
            }
            foreach (var e in evs)
            {
                foreach (var (get, set) in new (Func<string>, Action<string>)[] { (() => e.Context, v => e.Context = v), (() => e.CauseContext, v => e.CauseContext = v) })
                {
                    string cur = get(), fin = Final(cur);
                    if (cur == null || fin == cur) continue;
                    set(fin);
                    e.Raw = e.Raw.Replace(Json.Str(cur), Json.Str(fin));
                }
            }
        }

        /// <summary>
        /// 대상 → 포트(허브 없이, L4): 정수 → 그 포트, COMx → 사건 기록의 마지막 연결(attach) 기록의 com, 그다음 config cables.&lt;id&gt;.alias,
        /// 보드명(state.json boards·사건 기록의 board). 하나면 그 포트, 0개면 102, 여럿이면 105 + 후보.
        /// </summary>
        public static int ResolveTarget(string hubDir, string dir, Config cfg, string target, out int code, out string why, out List<int> candidates)
        {
            code = 0; why = null; candidates = new List<int>();
            target = (target ?? "").Trim();
            if (int.TryParse(target, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 1 && n <= 65535) return n;
            var st = HubFiles.ReadJson(Path.Combine(hubDir, "state.json"));
            var assign = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var boards = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (st.HasValue)
            {
                if (st.Value.TryGetProperty("assign", out var a) && a.ValueKind == JsonValueKind.Object)
                    foreach (var p in a.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.Number) assign[p.Name] = p.Value.GetInt32();
                if (st.Value.TryGetProperty("boards", out var b) && b.ValueKind == JsonValueKind.Object)
                    foreach (var p in b.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.String) boards[p.Name] = p.Value.GetString();
            }
            // 포트마다 마지막 com·보드(사건 기록)
            var lastCom = new Dictionary<int, string>();
            var lastBoard = new Dictionary<int, string>();
            foreach (var e in Read(dir, null))
            {
                if (e.Kind == "attach" && e.Com != null) lastCom[e.Port] = e.Com;
                if (e.Board != null) lastBoard[e.Port] = e.Board;
            }
            HashSet<int> hit;
            if (Regex.IsMatch(target, @"^COM\d+$", RegexOptions.IgnoreCase))
                hit = new HashSet<int>(lastCom.Where(kv => string.Equals(kv.Value, target, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key));
            else
            {
                hit = new HashSet<int>(cfg.Cables.Where(kv => string.Equals(kv.Value.Alias, target, StringComparison.OrdinalIgnoreCase) && assign.ContainsKey(kv.Key)).Select(kv => assign[kv.Key]));
                if (hit.Count == 0)
                {
                    foreach (var kv in boards) if (string.Equals(kv.Value, target, StringComparison.OrdinalIgnoreCase) && assign.TryGetValue(kv.Key, out int p)) hit.Add(p);
                    foreach (var kv in lastBoard) if (string.Equals(kv.Value, target, StringComparison.OrdinalIgnoreCase)) hit.Add(kv.Key);
                }
            }
            candidates = hit.OrderBy(x => x).ToList();
            if (candidates.Count == 1) return candidates[0];
            code = candidates.Count == 0 ? 102 : 105;
            why = candidates.Count == 0
                ? "대상 '" + target + "' 에 맞는 포트 없음(사건 기록·state.json·config.json 에서 찾음) — 포트 번호(예: 7001)로 지정"
                : "대상 '" + target + "' 이(가) 포트 " + string.Join(",", candidates) + " 에 맞음 — 포트 번호로 지정";
            return 0;
        }

        /// <summary>
        /// 에이징 요약(부록 C.5 aging): 부팅 수(cmd/soft/hard)·부팅 간격(초) 최소·중간·최대(기록 공백을 걸친 간격은 뺌), 종류별 수와 첫·마지막 시각,
        /// 앞뒤 로그 목록(같은 파일은 한 번), 기록한 시간(covered_s — 기간에서 기록 공백 gaps 를 뺀 것, M4). evs 는 그 포트·기간으로 이미 거른 것(시각 순).
        /// 기록 공백 = 케이블이 붙어 있지 않던 시간: 분리(detach) ~ 다음 연결(attach), 분리 기록 없이 온 연결은 그 gap_s(허브가 꺼져 있던 동안, D8).
        /// 수는 실시간 사건만 센다 — 명령 출력 속 사건(suspect)은 kinds.&lt;종류&gt;.suspect·boots.suspect 에 따로(부록 C.6).
        /// </summary>
        /// <summary>요약의 '부팅 0' 안내(hint no_boots_seen): 기간이 30분 이상인데 부팅을 하나도 못 봤으면 — 재부팅하는데 허브가 그 보드의 부팅 표시를 모를 수 있다.</summary>
        public static bool NoBootsHint(bool anyBoot, DateTimeOffset? from, DateTimeOffset now) => !anyBoot && from.HasValue && (now - from.Value).TotalMinutes >= 30;

        public static JObj Aging(int port, List<EventRec> evs, DateTimeOffset? since, DateTimeOffset until, string eventsFile, bool detachedAtStart = false)
        {
            var o = new JObj().Set("status", "ok").Set("exit", 0L).Set("port", (long)port).Set("board", evs.LastOrDefault(e => e.Board != null)?.Board)
                .Set("events_file", eventsFile).Set("since", since.HasValue ? Iso(since.Value) : null).Set("until", Iso(until))
                .Set("first_event", evs.Count > 0 ? Iso(evs[0].Ts) : null).Set("last_event", evs.Count > 0 ? Iso(evs[evs.Count - 1].Ts) : null)
                .Set("events", (long)evs.Count(e => e.Kind != "attach" && e.Kind != "detach" && e.Suspect == null))
                .Set("suspect_events", (long)evs.Count(e => e.Suspect != null));
            // ---- 기록 공백: 분리 ~ 다음 연결, 분리 기록 없는 연결은 [ts − gap_s, ts], 끝까지 다시 붙지 않은 분리는 ~ until
            DateTimeOffset start = since ?? (evs.Count > 0 ? evs[0].Ts : until);
            var gaps = new List<(DateTimeOffset From, DateTimeOffset To, string Reason)>();
            DateTimeOffset? off = detachedAtStart ? start : (DateTimeOffset?)null;                 // --since 앞에서 분리된 채(B8)
            foreach (var e in evs)
            {
                if (e.Kind == "detach") { off ??= e.Ts; continue; }
                if (e.Kind != "attach") continue;
                if (off.HasValue) { gaps.Add((off.Value, e.Ts, e.Reason)); off = null; }
                else if (e.GapS.HasValue && e.GapS.Value > 0) gaps.Add((e.Ts.AddSeconds(-e.GapS.Value), e.Ts, e.Reason));
            }
            if (off.HasValue && until > off.Value) gaps.Add((off.Value, until, "detached"));
            double gapSum = 0;
            foreach (var g in gaps)
            {
                var f = g.From < start ? start : g.From;
                var t = g.To > until ? until : g.To;
                if (t > f) gapSum += (t - f).TotalSeconds;
            }
            double span = Math.Max(0, (until - start).TotalSeconds);
            o.Set("period_s", Math.Round(span, 1)).Set("covered_s", Math.Round(Math.Max(0, span - gapSum), 1))
             .Set("gaps", gaps.Select(g => new JObj().Set("from", Iso(g.From)).Set("to", Iso(g.To)).Set("s", Math.Round((g.To - g.From).TotalSeconds, 1)).Set("reason", g.Reason)));
            // ---- 부팅(실시간만, suspect 는 따로 센다)
            var boots = evs.Where(e => e.Kind == "boot" && e.Suspect == null).ToList();
            long Cls(string c) => boots.Count(b => b.Class == c);
            o.Set("boots", new JObj().Set("total", (long)boots.Count).Set("cmd", Cls("cmd")).Set("soft", Cls("soft")).Set("hard", Cls("hard"))
                                     .Set("with_cause", (long)boots.Count(b => b.CauseKind != null))
                                     .Set("suspect", (long)evs.Count(e => e.Kind == "boot" && e.Suspect != null)));
            o.Set("unexpected_reboots", Cls("soft") + Cls("hard"));
            var iv = new List<double>();
            int spanned = 0;
            for (int i = 1; i < boots.Count; i++)
            {
                var a = boots[i - 1].Ts; var b = boots[i].Ts;
                if (gaps.Any(g => g.To > a && g.From < b)) { spanned++; continue; }      // 기록 공백을 걸친 간격(그 사이 부팅을 못 봤을 수 있음, L10)
                iv.Add(Math.Round((b - a).TotalSeconds, 1));
            }
            if (iv.Count == 0) o.Null("boot_interval_s");
            else
            {
                var s = iv.OrderBy(x => x).ToList();
                double med = s.Count % 2 == 1 ? s[s.Count / 2] : Math.Round((s[s.Count / 2 - 1] + s[s.Count / 2]) / 2, 1);
                o.Set("boot_interval_s", new JObj().Set("count", (long)s.Count).Set("min", s[0]).Set("median", med).Set("max", s[s.Count - 1]).Set("excluded_gap", (long)spanned));
            }
            var kinds = new JObj();
            foreach (var k in EventKinds.Errors.Concat(new[] { "confirm", "missed" }))
            {
                var l = evs.Where(e => e.Kind == k && e.Suspect == null).ToList();
                var ko = new JObj().Set("count", (long)l.Count);
                if (l.Count == 0) ko.Null("first").Null("last");
                else ko.Set("first", Iso(l[0].Ts)).Set("last", Iso(l[l.Count - 1].Ts));
                ko.Set("no_context", (long)l.Count(e => e.ContextSkipped != null)).Set("suspect", (long)evs.Count(e => e.Kind == k && e.Suspect != null));
                kinds.Set(k, ko);
            }
            o.Set("kinds", kinds);
            var ctx = new List<JObj>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in evs.Where(e => e.Context != null))
            {
                if (!seen.Add(e.Context)) continue;
                ctx.Add(new JObj().Set("ts", Iso(e.Ts)).Set("kind", e.Kind == "boot" ? "boot:" + e.Class : e.Kind)
                    .Set("kinds", evs.Where(x => string.Equals(x.Context, e.Context, StringComparison.OrdinalIgnoreCase)).Select(x => x.Kind == "boot" ? "boot:" + x.Class : x.Kind).Distinct())
                    .Set("boot_seq", e.BootSeq).Set("line", e.Line).Set("context", e.Context).Set("exists", File.Exists(e.Context)).Set("suspect", e.Suspect));
            }
            return o.Set("contexts", ctx);
        }
    }

    /// <summary>
    /// 사건 jsonl 이어 읽기(CLI events --follow·감독 풍선). 포트들(null = 폴더의 전부, 새로 생긴 파일도)을 파일 끝 위치로 기억하고 Poll 마다 새 줄만 준다.
    /// fromStart=false 면 만들 때의 끝부터(그 전 사건은 주지 않음). 끝 위치를 못 잰 파일은 0 부터 읽지 않고 다음 Poll 에 다시 잰다(L6).
    /// 크기가 그대로면 열지 않는다(M9). 파일이 줄면(지웠다 다시 생김) 처음부터. 개행 없는 마지막 줄은 다음 Poll 로 미루고, 읽지 못한 줄은 건너뛴다(H5).
    /// </summary>
    public sealed class EventTail
    {
        readonly int[] ports;
        readonly Dictionary<string, long> pos = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> noBase = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Dir { get; }

        public EventTail(string dir, IEnumerable<int> ports, bool fromStart)
        {
            Dir = dir; this.ports = ports?.ToArray();
            if (!fromStart)
                foreach (var f in Files())
                {
                    try { pos[f] = File.Exists(f) ? new FileInfo(f).Length : 0; }
                    catch (Exception) { noBase.Add(f); }
                }
        }

        IEnumerable<string> Files() => (ports ?? EventLog.Ports(Dir).ToArray()).Select(p => EventLog.FileOf(Dir, p));

        public List<EventRec> Poll()
        {
            var got = new List<EventRec>();
            foreach (var f in Files())
            {
                try
                {
                    if (noBase.Contains(f))
                    {
                        pos[f] = File.Exists(f) ? new FileInfo(f).Length : 0;               // 끝 위치를 이제 잼 — 그 전 사건은 주지 않는다
                        noBase.Remove(f);
                        continue;
                    }
                    var fi = new FileInfo(f);
                    if (!fi.Exists) continue;
                    pos.TryGetValue(f, out long p);
                    long len = fi.Length;
                    if (len == p) continue;
                    if (len < p) p = 0;
                    using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    fs.Position = p;
                    var buf = new byte[Math.Max(0, fs.Length - p)];
                    int n = 0;
                    while (n < buf.Length) { int k = fs.Read(buf, n, buf.Length - n); if (k <= 0) break; n += k; }
                    int end = n > 0 ? Array.LastIndexOf(buf, (byte)'\n', n - 1) : -1;
                    if (end < 0) { pos[f] = p; continue; }
                    pos[f] = p + end + 1;
                    foreach (var l in Encoding.UTF8.GetString(buf, 0, end).Split('\n'))
                    {
                        var r = EventRec.Parse(l);
                        if (r != null) got.Add(r);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            var sorted = got.OrderBy(r => r.Ts).ThenBy(r => r.Port).ToList();
            EventLog.ApplyRenames(sorted, renames);
            return sorted;
        }
    }

    /// <summary>풍선 하나: 글, 누르면 열 파일(앞뒤 로그 — 없으면 null), 묶인 사건 수. Name = "보드 (포트)", Core = 클릭 안내·묶인 수를 뺀 한 문장(여러 포트를 한 풍선으로 묶을 때 쓴다).</summary>
    public sealed class Balloon
    {
        public int Port;
        public string Text, Open, Name, Core;
        public int Count, Severity;
    }

    /// <summary>
    /// 트레이 풍선 묶기(부록 C.4): 포트마다 첫 사건 뒤 SettleS(같은 사건 묶음의 다른 줄 — oops 뒤 panic 등 — 를 함께 받으려고) 기다렸다가 하나,
    /// 그 뒤 WindowS(1분) 안의 사건은 모아 두었다가 창이 지나면 하나("… 외 N건"). 머리말은 가장 심한 종류, 누르면 그 사건의 앞뒤 로그.
    /// 알리는 것은 Notify(config events.notify — 기본 panic·oops·app_error·confirm, panic·oops 가 cause 인 부팅)뿐. 재부팅·bug·oom·missed 는 기록만(2026-11-06).
    /// 알리지 않는 것: 잦아서 앞뒤 로그를 줄인 같은 종류(context_skipped rate)·명령 출력 속 suspect. 시계는 부르는 쪽이 준다(시험은 결정적).
    /// </summary>
    public sealed class EventBalloons
    {
        public double WindowS = 60, SettleS = 2;
        /// <summary>알릴 종류(config events.notify). null = 기본(EventKinds.NotifyDefault). 감독이 설정을 다시 읽으면 바꾼다.</summary>
        public IReadOnlyCollection<string> Notify;

        sealed class PortQ
        {
            public DateTime? Last;
            public DateTime First;
            public readonly List<EventRec> Items = new List<EventRec>();
        }

        readonly Dictionary<int, PortQ> q = new Dictionary<int, PortQ>();

        public void Add(EventRec e, DateTime now)
        {
            if (e == null) return;
            if (e.ContextWas != null && e.Context != null)                                    // txt 이름이 바뀜: 기다리는 풍선이 옛 경로를 열지 않게
                foreach (var it in q.Values.SelectMany(x => x.Items))
                {
                    if (string.Equals(it.Context, e.ContextWas, StringComparison.OrdinalIgnoreCase)) it.Context = e.Context;
                    if (string.Equals(it.CauseContext, e.ContextWas, StringComparison.OrdinalIgnoreCase)) it.CauseContext = e.Context;
                }
            if (!EventKinds.Notifiable(e.Kind, e.Class, e.ContextSkipped, e.Suspect, e.CauseKind, Notify)) return;
            if (!q.TryGetValue(e.Port, out var p)) q[e.Port] = p = new PortQ();
            if (p.Items.Count == 0) p.First = now;
            if (p.Items.Count < 1000) p.Items.Add(e);
        }

        public List<Balloon> Due(DateTime now)
        {
            var l = new List<Balloon>();
            foreach (var kv in q.OrderBy(x => x.Key))
            {
                var p = kv.Value;
                if (p.Items.Count == 0) continue;
                var at = p.First.AddSeconds(SettleS);
                if (p.Last.HasValue && p.Last.Value.AddSeconds(WindowS) > at) at = p.Last.Value.AddSeconds(WindowS);
                if (now < at) continue;
                l.Add(Make(kv.Key, p.Items));
                p.Items.Clear();
                p.Last = now;
            }
            return l;
        }

        /// <summary>
        /// 같은 틱에 때가 된 풍선들 → 하나(L8, NotifyIcon 은 마지막 풍선만 보인다). 가장 심한 것이 먼저·그 앞뒤 로그를 연다. 포트마다 한 문장을 sep 로 잇고,
        /// 누르면 열리는 앞뒤 로그는 끝에 한 번 "누르면 &lt;보드 (포트)&gt;의 앞뒤 로그가 열립니다."(sep 가 줄바꿈이면 새 줄).
        /// </summary>
        public static Balloon Merge(List<Balloon> bs, string sep)
        {
            if (bs == null || bs.Count == 0) return null;
            if (bs.Count == 1) return bs[0];
            var o = bs.OrderByDescending(b => b.Severity).ThenBy(b => b.Port).ToList();
            var first = o.FirstOrDefault(b => b.Open != null);
            string text = string.Join(sep, o.Select(b => b.Core + More(b.Count)))
                          + (first != null ? (sep.Contains('\n') ? "\n" : " ") + "누르면 " + first.Name + "의 앞뒤 로그가 열립니다." : "");
            return new Balloon { Port = o[0].Port, Text = text, Open = first?.Open, Name = o[0].Name, Core = o[0].Core, Count = o.Sum(b => b.Count), Severity = o[0].Severity };
        }

        static string More(int count) => count > 1 ? " (외 " + (count - 1).ToString(CultureInfo.InvariantCulture) + "건)" : "";

        /// <summary>
        /// 풍선 글(UI_COPY.md "풍선"): "EVB-A1 (7001): Kernel panic, 17:38:51." · "… panic 뒤 재부팅, 17:38:53." · "… 예상 못 한 재부팅(하드), 17:40:05." ·
        /// "… panic 뒤 응답 없음. 보드가 멈췄을 수 있습니다." 앞뒤 로그가 있으면 " 누르면 앞뒤 로그가 열립니다.", 묶였으면 " (외 N건)".
        /// </summary>
        Balloon Make(int port, List<EventRec> items)
        {
            var head = items.OrderByDescending(e => EventKinds.Severity(e.Kind, e.Class, e.CauseKind)).ThenBy(e => e.Ts).First();
            string open = head.CauseContext ?? head.Context ?? items.FirstOrDefault(e => e.Context != null)?.Context;
            string board = head.Board ?? items.LastOrDefault(e => e.Board != null)?.Board;
            string com = head.Com ?? items.LastOrDefault(e => e.Com != null)?.Com;
            string name = DesktopIcons.NameOf(DesktopIcons.LabelOf(board, com), port);
            string core = name + ": " + What(head.Kind, head.Class, head.CauseKind, head.Of)
                          + (head.Kind == "confirm" ? " 보드가 멈췄을 수 있습니다." : ", " + head.Ts.LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + ".");
            string text = core + (open != null ? " 누르면 앞뒤 로그가 열립니다." : "") + More(items.Count);
            return new Balloon { Port = port, Text = text, Open = open, Name = name, Core = core, Count = items.Count, Severity = EventKinds.Severity(head.Kind, head.Class, head.CauseKind) };
        }

        /// <summary>풍선의 사건 이름(사람용 — CLI·앞뒤 로그 머리말의 EventKinds.Label 과 따로 둔다). confirm 은 마침표까지("panic 뒤 응답 없음.").</summary>
        public static string What(string kind, string cls, string causeKind = null, string of = null) => kind switch
        {
            "boot" when causeKind != null => Short(causeKind) + " 뒤 재부팅",
            "boot" => "예상 못 한 재부팅(" + (cls == "hard" ? "하드" : cls == "soft" ? "소프트" : cls) + ")",
            "confirm" => Short(of ?? "panic") + " 뒤 응답 없음.",
            _ => EventKinds.Label(kind, cls),
        };

        static string Short(string kind) => kind switch { "panic" => "panic", "oops" => "Oops", "bug" => "BUG", "oom" => "OOM", "app_error" => "앱 오류", _ => kind };
    }
}
