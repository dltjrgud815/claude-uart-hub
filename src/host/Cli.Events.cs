// `uart events`·`uart aging`(계획 X6·부록 C.5) — 코어·보드에 아무것도 보내지 않고 <log_root>\events\<port>.jsonl 을 읽기만 한다.
//   events [<c>] [--since <ISO|12h|30m|45s|2d>] [--kind k1,k2] [--follow [--max N]] [--json]
//     --kind: panic,oops,bug,oom,app_error,boot,confirm,missed,attach,detach 와 boot:cmd|soft|hard(그 부팅만).
//     suspect:"cmd_output" 이 붙은 사건 = 명령 출력 속 줄(옛 로그일 수 있음, 부록 C.6·C.7) — 다른 사건과 똑같이 내보낸다(거르지 않음).
//     boot 의 cause = 리셋 직전 panic/oops 줄(suspect 의 확인), confirm = suspect panic/oops 뒤 30초 무응답(멈춤 의심).
//     --json: {status, exit, events_dir, ports, since, kinds, count, events:[사건 줄 그대로]}(2000건 넘으면 마지막 2000건 + truncated·total).
//     --follow: 머리 줄 {status:"ok", exit:0, follow:true, ts, events_dir, ports, since, kinds} 뒤 새 사건마다 사건 JSON 한 줄. --since 를 주면 그때부터의 지난 사건을 먼저.
//               ts = 기다리기 시작한 시각(사건 없이 끝나 다시 걸 때 --since 로 쓴다).
//               허브가 다시 시작돼도 파일을 계속 읽는다. 끝: --max N 개를 내면 exit 0, 표준 출력을 읽는 쪽이 닫히면(| head·Monitor 끝) exit 141,
//               래퍼 TERM → --parent-pipe EOF(103).
//   aging <c> [--since …] [--json](= session summary <c> …, 부록 C.12) — 기간 요약(EventLog.Aging): 부팅 cmd/soft/hard 수·간격, 종류별 수·첫/마지막 시각, 앞뒤 로그 목록, 기록 공백(gaps·covered_s).
//   <c>: 포트 번호 · COMx · alias · 보드명 — 허브 없이 사건 기록(attach 의 com)·state.json·config.json 으로 푼다(없으면 102, 여럿이면 105 + candidates).
//   log_root 는 코어·감독과 같은 규칙(EventLog.ConfigOf). 경로 필드(context·log·events_dir·events_file)는 다른 명령처럼 UART_PATH_STYLE(wsl·mixed)로 바꾼다.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace UartHub
{
    public static partial class Cli
    {
        const int MaxEventsOut = 2000;
        static readonly string[] EventsFlags = { "--follow" };
        static readonly string[] EventsValued = { "--kind", "--max" };

        static int EventsCmd(string cmd, ControlServer.ParsedArgs pa)
        {
            bool aging = cmd == "aging";
            string usage = aging ? L.T("사용: session summary <port|COMx|보드> [--since <ISO|12h|30m>] [--json] (이전 이름 aging)",
                                       "Usage: session summary <port|COMx|board> [--since <ISO|12h|30m>] [--json] (old name: aging)")
                                 : L.T("사용: events [<port|COMx|보드>] [--since <ISO|12h|30m>] [--kind k1,k2] [--follow [--max N]] [--json]",
                                       "Usage: events [<port|COMx|board>] [--since <ISO|12h|30m>] [--kind k1,k2] [--follow [--max N]] [--json]");
            if (pa == null) throw QuitTool(106, usage);
            bool follow = pa.Flags.Contains("--follow");
            var okFlags = aging ? new[] { "--json" } : new[] { "--json", "--follow" };
            var okVals = aging ? new[] { "--since" } : new[] { "--since", "--kind", "--max" };
            var other = pa.Flags.Where(f => !okFlags.Contains(f)).Concat(pa.Values.Keys.Where(k => !okVals.Contains(k))).ToList();
            if (other.Count > 0) throw QuitTool(106, L.T(cmd + ": 쓸 수 없는 옵션 " + string.Join(" ", other) + " — " + usage, cmd + ": option not allowed " + string.Join(" ", other) + " — " + usage));
            if (pa.Pos.Count > 1 || (aging && pa.Pos.Count != 1)) throw QuitTool(106, usage);
            int max = 0;
            if (pa.Values.TryGetValue("--max", out var mv))
            {
                if (!follow) throw QuitTool(106, L.T("events: --max 는 --follow 와 함께만", "events: --max only with --follow"));
                if (!int.TryParse(mv, NumberStyles.None, CultureInfo.InvariantCulture, out max) || max < 1) throw QuitTool(106, L.T("events: --max 는 1 이상 정수: '" + mv + "'", "events: --max must be an integer of 1 or more: '" + mv + "'"));
            }
            var now = DateTimeOffset.Now;
            DateTimeOffset? since = null;
            string sinceText = pa.Values.TryGetValue("--since", out var sv) ? sv : null;
            if (sinceText != null)
            {
                if (!EventLog.TryParseSince(sinceText, now, out var s))
                    throw QuitTool(106, L.T(cmd + ": --since 는 ISO 시각(2026-11-05T08:00) 또는 12h·30m·45s·2d: '" + sinceText + "'",
                                            cmd + ": --since takes an ISO time (2026-11-05T08:00) or 12h, 30m, 45s, 2d: '" + sinceText + "'"));
                since = s;
            }
            HashSet<string> kinds = null;
            if (pa.Values.TryGetValue("--kind", out var kv))
            {
                kinds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var k in kv.Split(',').Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0))
                {
                    if (Array.IndexOf(EventKinds.All, k) < 0 && !(k.StartsWith("boot:", StringComparison.Ordinal) && new[] { "cmd", "soft", "hard" }.Contains(k.Substring(5))))
                        throw QuitTool(106, L.T("events: --kind 는 " + string.Join(",", EventKinds.All) + "(boot:cmd|soft|hard 도 됨): '" + k + "'",
                                                "events: --kind takes " + string.Join(",", EventKinds.All) + " (boot:cmd|soft|hard too): '" + k + "'"));
                    kinds.Add(k);
                }
                if (kinds.Count == 0) throw QuitTool(106, L.T("events: --kind 가 비었음", "events: --kind is empty"));
            }

            var cfg = EventLog.ConfigOf(hubDir);
            string dir = cfg.EventsJsonlDir;                                                  // 부록 C.11: log_layout 2 = <허브 폴더>\events
            int? port = null;
            if (pa.Pos.Count == 1)
            {
                int p = EventLog.ResolveTarget(hubDir, dir, cfg, pa.Pos[0], out int code, out string why, out var cands);
                if (p == 0) throw TargetFail(code, why, cands);
                port = p;
            }
            bool Pass(EventRec e) => (!port.HasValue || e.Port == port.Value) && (!since.HasValue || e.Ts >= since.Value) && e.KindIn(kinds);
            string SinceIso() => since.HasValue ? since.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture) : null;

            // ---- aging
            if (aging)
            {
                // 그 포트에 진행 중인 에이징 세션이 있고 --since 가 없으면 세션 시작부터("지금까지 결과"가 기본). 세션 정보도 함께 돌려준다.
                AgingSession sess = null;
                try { sess = new SessionStore(hubDir, null).Get(port.Value); } catch (Exception) { }
                string scope = since.HasValue ? "since" : "all";
                if (sess != null && !since.HasValue) { since = new DateTimeOffset(sess.Started); scope = "session"; }
                var all = EventLog.Read(dir, new[] { port.Value });
                var evs = all.Where(Pass).ToList();
                // --since 앞의 마지막 연결 기록이 분리면 기간은 분리된 채 시작한다(B8)
                bool detachedAtStart = since.HasValue && all.LastOrDefault(e => e.Ts < since.Value && (e.Kind == "attach" || e.Kind == "detach"))?.Kind == "detach";
                var o = EventLog.Aging(port.Value, evs, since, now, EventLog.FileOf(dir, port.Value), detachedAtStart);
                if (!File.Exists(EventLog.FileOf(dir, port.Value)))
                    o.Set("note", L.T("사건 기록 없음 — 이 포트를 허브가 기록한 적이 없거나 아직 사건이 없음", "No event record — the hub has never recorded this port, or there are no events yet"));
                o.Set("scope", scope);
                if (sess != null)
                    o.Set("session", new JObj().Set("name", sess.Name).Set("started", AgingSession.Iso(sess.Started)).Set("folder", sess.Folder)
                                               .Set("console_log", new JObj().Set("path", sess.ConsolePath)));       // 세션 언어의 이름(콘솔.log | console.log)
                // 부팅 판정 근거: 허브는 로더 표시·boot_re 로만 부팅을 센다. 로더 표시가 다르거나 커널이 quiet 인 보드는 재부팅해도 0 으로 남는다.
                var prof = ProductProfile.Current;
                o.Set("boot_detection", new JObj().Set("loader_marker_re", string.IsNullOrEmpty(cfg.LoaderMarkerRe) ? null : cfg.LoaderMarkerRe).Set("boot_re", cfg.BootRe)
                                                  .Set("profile", prof.IsGeneric ? null : prof.Name));
                if (EventLog.NoBootsHint(evs.Any(e => e.Kind == "boot"), since ?? evs.Select(e => (DateTimeOffset?)e.Ts).FirstOrDefault(), now))
                    o.Set("hint", "no_boots_seen").Set("hint_text", L.T("이 기간에 부팅을 하나도 알아보지 못했습니다. 보드가 실제로 재부팅했다면(on/off 에이징 등) 허브가 이 보드의 부팅 표시를 모르는 것입니다 — "
                                                     + "콘솔 로그에서 부팅 줄을 확인하고, 제품 프로필이나 config 의 loader_marker_re·boot_re 를 그 보드에 맞추세요(boot_detection 이 지금 쓰는 패턴).",
                                                     "No boot was recognized in this period. If the board really rebooted (on/off aging, etc.), the hub does not know this board's boot marker — "
                                                     + "check the boot lines in the console log and set loader_marker_re and boot_re in the product profile or config to match the board (boot_detection shows the patterns in use)."));
                if (jsonMode) { OutLine(Styled(o.ToString())); return 0; }
                PrintAging(o.ToString());
                return 0;
            }

            // ---- events(한 번)
            var portsJson = port.HasValue ? new[] { (long)port.Value } : EventLog.Ports(dir).Select(x => (long)x).ToArray();
            if (!follow)
            {
                var evs = EventLog.Read(dir, port.HasValue ? new[] { port.Value } : null).Where(Pass).ToList();
                int total = evs.Count;
                if (evs.Count > MaxEventsOut) evs = evs.Skip(evs.Count - MaxEventsOut).ToList();
                if (jsonMode)
                {
                    var o = new JObj().Set("status", "ok").Set("exit", 0L).Set("events_dir", dir).Set("ports", portsJson).Set("since", SinceIso())
                        .Set("kinds", kinds?.OrderBy(x => x, StringComparer.Ordinal)).Set("count", (long)evs.Count);
                    if (total > evs.Count) o.Set("truncated", true).Set("total", (long)total);
                    o.Raw("events", "[" + string.Join(",", evs.Select(e => e.Raw)) + "]");
                    OutLine(Styled(o.ToString()));
                    return 0;
                }
                if (evs.Count == 0) OutLine(L.T("사건 없음 (" + StylePath(dir) + (port.HasValue ? ", 포트 " + port.Value : "") + (since.HasValue ? ", " + SinceIso() + " 부터" : "") + ")",
                                                "No events (" + StylePath(dir) + (port.HasValue ? ", port " + port.Value : "") + (since.HasValue ? ", since " + SinceIso() : "") + ")"));
                if (total > evs.Count) OutLine(L.T("(사건 " + total + "건 중 마지막 " + evs.Count + "건)", "(last " + evs.Count + " of " + total + " events)"));
                foreach (var e in evs) PrintEvent(e);
                return 0;
            }

            // ---- events --follow: 파일을 이어 읽는다(코어 재시작과 무관)
            // 표준 출력은 핸들 그대로의 FileStream 으로 쓴다 — 콘솔 스트림(Console.OpenStandardOutput)은 읽는 쪽이 닫힌 파이프 쓰기를 조용히 삼켜 끝을 알 수 없다
            var raw = RawStdout();
            if (raw != null) stdout = raw;
            var tail = new EventTail(dir, port.HasValue ? new[] { port.Value } : null, since.HasValue);
            if (jsonMode)
                OutLine(Styled(new JObj().Set("status", "ok").Set("exit", 0L).Set("follow", true).Set("ts", EventLog.Ts(DateTime.Now)).Set("events_dir", dir).Set("ports", portsJson)
                    .Set("since", SinceIso()).Set("kinds", kinds?.OrderBy(x => x, StringComparer.Ordinal)).ToString()));
            else OutLine(L.T("사건 기다리는 중 — " + StylePath(dir) + (port.HasValue ? " 포트 " + port.Value : " 모든 포트") + (max > 0 ? " (" + max + "건 뒤 끝)" : " (끝내려면 Ctrl-C)"),
                             "Waiting for events — " + StylePath(dir) + (port.HasValue ? " port " + port.Value : " all ports") + (max > 0 ? " (stops after " + max + ")" : " (Ctrl-C to stop)")));
            if (stdoutBroken) return 141;
            int sent = 0;
            while (true)
            {
                foreach (var e in tail.Poll())
                {
                    if (!Pass(e)) continue;
                    if (jsonMode) OutLine(Styled(e.Raw)); else PrintEvent(e);
                    if (stdoutBroken) return 141;                                     // 읽는 쪽이 닫힘(SIGPIPE 와 같은 141)
                    if (max > 0 && ++sent >= max) return 0;
                }
                Thread.Sleep(500);
            }
        }

        // ================================================================== 에이징 세션(부록 C.12)
        static readonly string[] AllValued = { "--timeout", "--idle", "--req-id", "--pattern", "--since", "--tail", "--kind", "--max" };

        /// <summary>session summary &lt;c&gt; … → aging &lt;c&gt; …(옵션 자리는 그대로). 아니면 argv 그대로.</summary>
        static List<string> SummaryAlias(List<string> argv)
        {
            int cmdAt = -1, verbAt = -1;
            bool rest = false;
            for (int i = 0; i < argv.Count; i++)
            {
                string x = argv[i];
                if (!rest && x == "--") { rest = true; continue; }
                if (!rest && x.StartsWith("--", StringComparison.Ordinal)) { if (Array.IndexOf(AllValued, x) >= 0) i++; continue; }
                if (cmdAt < 0) { if (!Eq(x, "session")) return argv; cmdAt = i; continue; }
                verbAt = i;
                break;
            }
            if (cmdAt < 0 || verbAt < 0 || !Eq(argv[verbAt], "summary")) return argv;
            var l = argv.ToList();
            l[verbAt] = "aging";
            l.RemoveAt(cmdAt);
            return l;
        }

        /// <summary>session 응답(비JSON): start·stop 은 한국어 안내, status 는 세션마다 한 줄.</summary>
        static void PrintSession(JsonElement r)
        {
            string S(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind != JsonValueKind.Null ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()) : null;
            double D(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : -1;
            string action = S(r, "action");
            if (action == "start")
            {
                OutLine(L.T("에이징을 시작했습니다: '" + S(r, "name") + "' · 포트 " + S(r, "port") + (S(r, "com") != null ? " (" + S(r, "com") + ")" : ""),
                            "Aging started: '" + S(r, "name") + "' · port " + S(r, "port") + (S(r, "com") != null ? " (" + S(r, "com") + ")" : "")));
                OutLine(L.T("폴더: ", "Folder: ") + StylePath(S(r, "folder")));
                return;
            }
            if (action == "stop")
            {
                OutLine(L.T("에이징을 끝냈습니다. " + (S(r, "summary") != null ? "요약을 저장했습니다: " + StylePath(S(r, "summary")) : "요약을 저장하지 못했습니다(" + S(r, "summary_error") + ")"),
                            "Aging ended. " + (S(r, "summary") != null ? "Summary saved: " + StylePath(S(r, "summary")) : "Could not save the summary (" + S(r, "summary_error") + ")")));
                OutLine("");
                foreach (var l in (S(r, "summary_text") ?? "").Replace("\r\n", "\n").TrimEnd('\n').Split('\n')) OutLine(l);
                return;
            }
            if (!r.TryGetProperty("sessions", out var ss) || ss.ValueKind != JsonValueKind.Array || ss.GetArrayLength() == 0)
            {
                OutLine(L.T("에이징 중인 포트 없음", "No port is aging"));
                return;
            }
            foreach (var x in ss.EnumerateArray())
            {
                double el = Math.Max(0, D(x, "elapsed_s"));
                double age = D(x, "last_rx_age_s");
                var ev = x.GetProperty("events");
                var kinds = ev.GetProperty("by_kind").EnumerateObject().Select(k => AgingText.KindName(k.Name) + " " + k.Value.GetRawText()).ToList();
                var cl = x.GetProperty("console_log");
                int gaps = x.TryGetProperty("gaps", out var g) && g.ValueKind == JsonValueKind.Array ? g.GetArrayLength() : 0;
                string susp = S(ev, "suspect"), lines = long.Parse(S(cl, "lines") ?? "0", CultureInfo.InvariantCulture).ToString("N0", CultureInfo.InvariantCulture);
                string conName = Path.GetFileName(S(cl, "path") ?? "");                                   // 세션 언어의 실제 이름(콘솔.log·console.log)
                if (conName.Length == 0) conName = AgingSession.ConsoleNameFor(null);
                OutLine(S(x, "port") + " " + (S(x, "com") ?? "-") + " " + (S(x, "board_now") ?? S(x, "board") ?? "-") + L.T(" · 에이징 중 '", " · aging '") + S(x, "name") + "' · " + AgingText.Duration(el)
                        + " · " + AgingText.RxTail(age >= 0 ? age : (double?)null, el) + (S(x, "connected") == "false" ? L.T(" · 연결 안 됨", " · not connected") : "")
                        + L.T(" · 이벤트 " + S(ev, "total") + "건", " · events " + S(ev, "total")) + (kinds.Count > 0 ? "(" + string.Join(", ", kinds) + ")" : "")
                        + (susp != null && susp != "0" ? L.T(" + 의심 " + susp + "건", " + " + susp + " suspect") : "")
                        + L.T(" · " + conName + " " + lines + "줄", " · " + conName + " " + lines + " lines")
                        + (gaps > 0 ? L.T(" · 기록 끊김 " + gaps + "번", " · " + gaps + " recording gap(s)") : "") + " · " + StylePath(S(x, "folder")));
            }
        }

        [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int nStdHandle);

        /// <summary>표준 출력 핸들 위의 버퍼 없는 FileStream(파이프가 닫히면 쓰기가 IOException). 핸들이 없거나 열 수 없으면 null(지금 스트림 그대로).</summary>
        static Stream RawStdout()
        {
            try
            {
                IntPtr h = GetStdHandle(-11);
                if (h == IntPtr.Zero || h == new IntPtr(-1)) return null;
                return new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(h, false), FileAccess.Write, 1);
            }
            catch (Exception) { return null; }
        }

        /// <summary>대상 해석 실패: 102(없음)·105(여럿 — candidates 포트). --json 이면 응답 형식 한 줄.</summary>
        static Exception TargetFail(int code, string why, List<int> cands)
        {
            if (jsonMode)
            {
                var o = new JObj().Set("status", Meaning[code]).Set("exit", (long)code).Set("error", why);
                if (cands.Count > 0) o.Set("candidates", cands.Select(c => new JObj().Set("port", (long)c)));
                OutLine(o.ToString());
            }
            else ErrLine("uart: E" + code + " " + Meaning[code] + " " + why);
            return new Quit(code);
        }

        /// <summary>JSON 한 줄의 경로 필드를 UART_PATH_STYLE(wsl·mixed)에 맞춘다(그 밖이면 그대로).</summary>
        static string Styled(string json)
        {
            string style = Environment.GetEnvironmentVariable("UART_PATH_STYLE");
            if (style != "wsl" && style != "mixed") return json;
            using var d = JsonDocument.Parse(json);
            return Rewrite(d.RootElement, style, WslMount());
        }

        static string StylePath(string p)
        {
            string style = Environment.GetEnvironmentVariable("UART_PATH_STYLE");
            return style == "wsl" || style == "mixed" ? ConvertPath(p, style, WslMount()) : p;
        }

        static string WslMount()
        {
            string mount = Environment.GetEnvironmentVariable("UART_WSL_MOUNT");
            return string.IsNullOrEmpty(mount) ? "/mnt/" : mount.TrimEnd('/') + "/";
        }

        static void PrintEvent(EventRec e)
        {
            string what = (e.Kind == "boot" ? "boot(" + (e.Class ?? "?") + ")" : e.Kind == "confirm" ? "confirm(" + (e.Of ?? "?") + ")" : e.Kind) + (e.Suspect != null ? "?" : "");
            OutLine(e.Ts.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + e.Port + " " + (e.Board ?? "-") + "  " + what.PadRight(10)
                    + L.T(" 부팅#", " boot#") + e.BootSeq + "  " + e.Line);
            if (e.Suspect != null) OutLine(L.T("    명령 출력 속 줄(옛 로그일 수 있음 — 뒤따르는 리셋이 cause 로 확인하거나 앞뒤 로그로 판단)",
                                               "    Line inside command output (may be an old log — a following reset confirms it as cause, or judge from the context log)"));
            if (e.CauseKind != null) OutLine(L.T("    원인(리셋 직전): ", "    Cause (just before reset): ") + e.CauseKind + " " + (e.CauseTs ?? "") + " " + e.CauseLine);
            if (e.Kind == "confirm")
                OutLine("    " + EventKinds.Label("confirm", null, null, e.Of) + L.T(" — 그 뒤 " + (e.SilenceS?.ToString("0", CultureInfo.InvariantCulture) ?? "?") + "초 동안 수신 없음",
                                                                                " — nothing received for " + (e.SilenceS?.ToString("0", CultureInfo.InvariantCulture) ?? "?") + " s after it"));
            if (e.Context != null) OutLine(L.T("    앞뒤 로그: ", "    Context log: ") + StylePath(e.Context));
            else if (e.ContextSkipped != null) OutLine(L.T("    앞뒤 로그 없음(예산: " + e.ContextSkipped + ")", "    No context log (budget: " + e.ContextSkipped + ")"));
        }

        static void PrintAging(string json)
        {
            using var d = JsonDocument.Parse(json);
            var o = d.RootElement;
            string S(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind != JsonValueKind.Null ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()) : null;
            string T(string iso) => iso != null && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "-";
            string se = S(o, "suspect_events");
            OutLine(L.T("포트 ", "Port ") + S(o, "port") + (S(o, "board") != null ? " (" + S(o, "board") + ")" : "") + " · " + (S(o, "since") != null ? T(S(o, "since")) : L.T("처음", "start")) + " ~ " + T(S(o, "until"))
                    + L.T(" · 사건 " + S(o, "events") + "건", " · " + S(o, "events") + " events")
                    + (se != null && se != "0" ? L.T(" (+ 명령 출력 속 " + se + "건)", " (+ " + se + " inside command output)") : "") + " · " + StylePath(S(o, "events_file")));
            if (S(o, "note") != null) OutLine(S(o, "note"));
            var gaps = o.GetProperty("gaps");
            OutLine(L.T("기록한 시간 " + S(o, "covered_s") + "초 / 기간 " + S(o, "period_s") + "초" + (gaps.GetArrayLength() > 0 ? " (기록 공백 " + gaps.GetArrayLength() + "번)" : ""),
                        "Recorded " + S(o, "covered_s") + " s / period " + S(o, "period_s") + " s" + (gaps.GetArrayLength() > 0 ? " (" + gaps.GetArrayLength() + " recording gap(s))" : "")));
            var b = o.GetProperty("boots");
            string wc = S(b, "with_cause"), bs = S(b, "suspect");
            OutLine(L.T("부팅 " + S(b, "total") + "회: cmd " + S(b, "cmd") + " · soft " + S(b, "soft") + " · hard " + S(b, "hard") + " (예상 못 한 재부팅 " + S(o, "unexpected_reboots") + ")"
                        + (wc != null && wc != "0" ? " · panic/oops 뒤 리셋 " + wc + "번" : "")
                        + (bs != null && bs != "0" ? " · 명령 출력 속 부팅 줄 " + bs + "번" : ""),
                        "Boots " + S(b, "total") + ": cmd " + S(b, "cmd") + " · soft " + S(b, "soft") + " · hard " + S(b, "hard") + " (unexpected reboots: " + S(o, "unexpected_reboots") + ")"
                        + (wc != null && wc != "0" ? " · resets after panic/oops: " + wc : "")
                        + (bs != null && bs != "0" ? " · boot lines inside command output: " + bs : "")));
            var iv = o.GetProperty("boot_interval_s");
            if (iv.ValueKind == JsonValueKind.Object)
                OutLine(L.T("부팅 간격(초): 최소 " + S(iv, "min") + " · 중간 " + S(iv, "median") + " · 최대 " + S(iv, "max") + " (" + S(iv, "count") + "개"
                            + (S(iv, "excluded_gap") != "0" ? ", 기록 공백 걸친 " + S(iv, "excluded_gap") + "개 뺌" : "") + ")",
                            "Boot interval (s): min " + S(iv, "min") + " · median " + S(iv, "median") + " · max " + S(iv, "max") + " (" + S(iv, "count")
                            + (S(iv, "excluded_gap") != "0" ? ", " + S(iv, "excluded_gap") + " across recording gaps left out" : "") + ")"));
            foreach (var k in o.GetProperty("kinds").EnumerateObject())
            {
                string ks = S(k.Value, "suspect");
                OutLine("  " + k.Name.PadRight(10) + S(k.Value, "count").PadLeft(5) + L.T("건  첫 ", "  first ") + T(S(k.Value, "first")) + L.T("  마지막 ", "  last ") + T(S(k.Value, "last"))
                        + (S(k.Value, "no_context") != "0" ? L.T("  (앞뒤 로그 없음 " + S(k.Value, "no_context") + ")", "  (no context log: " + S(k.Value, "no_context") + ")") : "")
                        + (ks != null && ks != "0" ? L.T("  (+ 명령 출력 속 " + ks + ")", "  (+ " + ks + " inside command output)") : ""));
            }
            var cs = o.GetProperty("contexts");
            OutLine(L.T("앞뒤 로그 " + cs.GetArrayLength() + "개", "Context logs: " + cs.GetArrayLength()) + (cs.GetArrayLength() > 0 ? ":" : ""));
            foreach (var c in cs.EnumerateArray()) OutLine("  " + T(S(c, "ts")) + " " + (S(c, "kind") + (S(c, "suspect") != null ? "?" : "")).PadRight(10) + StylePath(S(c, "context")));
        }
    }
}
