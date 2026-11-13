// 영어판(i18n — 언어 정하기·에이징 이름) 시나리오. 하네스(run_sim*.ps1)는 UART_HUB_LANG=ko 로 돈다(기존 시험 = 한국어 문구).
// 여기서는 환경변수·config·L.Use 로 잠시 언어를 바꿔 보고, 끝나면(LangKeep.Dispose = finally) 환경변수·L 을 원래대로 되돌린다 — 시나리오는 한 프로세스에서 차례로 돈다.
//   lang-resolve      언어 정하기: 환경변수 > config "language" > auto(Windows 표시 언어), ConfigureFromFile, L.T·L.Use, config 검증(auto|ko|en)
//   lang-aging-names  세션 언어(Lang)·console.log|콘솔.log·sessions.json "lang"(없으면 ko)·에이징/임시 기록 뿌리(두 언어)·SpoolDirFor(있는 폴더 먼저)
//   lang-event-text   이벤트 txt(이름·머리·결과 줄)·요약 글이 세션 언어를 따름(지금 언어와 무관) — EventScanner·AgingSummary 직접
//   lang-hub-switch   Hub + FakeBoard: ko 세션이 영어로 다시 켠 코어에서도 한국어 이름·글 그대로, 새 en 세션은 <기준>\aging\ 에 영어로
//   lang-e2e          실제 exe: UART_HUB_LANG=en 감독·코어(config 는 ko) → aging\<이름>\console.log·events\·summary.txt, 영어 CLI·풍선·hub.log, CLI 환경변수 ko 는 한국어
//   lang-no-hangul    L.Use("en") 에서 글을 만드는 곳 표본(트레이 제목·요약·이벤트 이름·config 오류·기본 이름…)에 한글 없음
//   lang-config-edit  Config.WithLanguage 글자 편집(바꾸기·끼우기·auto = 키 지움·CRLF·BOM·주석·끝 쉼표·중복 키) — 결과가 읽히고 다른 키는 그대로
//   lang-switch-e2e   실제 exe, UART_HUB_LANG 없이(config ko): uart hub language en → 코어 재시작(사유 language) → 영어 CLI·hub.log, 다시 en = unchanged, auto = 키 지움
// 포트: lang-hub-switch 제어 18720·케이블 18730, lang-e2e 제어 18740·케이블 18750(+ 기록 없는 포트 18759 는 CLI 가 파일만 읽음), lang-switch-e2e 제어 18760·케이블 18770.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace UartHub.Testing
{
    internal static class LangScenarios
    {
        /// <summary>한글(음절·자모). 영어판 글에 이것이 있으면 번역이 새는 것.</summary>
        static readonly Regex HangulRe = new Regex(@"[\uAC00-\uD7A3\u3131-\u318E]", RegexOptions.CultureInvariant);
        /// <summary>hub.log 의 시험 도구(SimCore) 줄 "[시각] [태그] sim…" — 제품 문구가 아니다.</summary>
        static readonly Regex SimLine = new Regex(@"^\[[^\]]*\] \[[^\]]*\] sim[: ]", RegexOptions.CultureInvariant);
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        static bool HasHangul(string s) => s != null && HangulRe.IsMatch(s);

        /// <summary>경로처럼 데이터로 들어간 글(시험 폴더·한국어로 먼저 생긴 임시기록 폴더)은 빼고 본다 — 문구가 아니다.</summary>
        static string Strip(string s, params string[] paths)
        {
            s ??= "";
            foreach (var p in paths.Where(x => !string.IsNullOrEmpty(x)).OrderByDescending(x => x.Length)) s = s.Replace(p, "<path>");
            return s;
        }

        /// <summary>한글이 든 첫 줄(실패 메시지용).</summary>
        static string FirstHangulLine(string s) => (s ?? "").Replace("\r\n", "\n").Split('\n').FirstOrDefault(HasHangul) ?? "";

        static string Read(string p) => File.Exists(p) ? (HubFiles.ReadShared(p) ?? "") : "";
        static List<string> Lines(string p) => Read(p).Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0).ToList();
        static List<string> FilesIn(string dir) => Directory.Exists(dir) ? Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToList() : new List<string>();
        static string B64(string s) => Convert.ToBase64String(Utf8.GetBytes(s));
        static long N(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object ? J.N(e, k) : long.MinValue;

        /// <summary>
        /// 이 프로세스의 언어 상태를 저장했다가 Dispose(= finally)에서 되돌린다: 환경변수 UART_HUB_LANG(하네스는 ko) → config 값 auto → 원래 언어 고정.
        /// 시나리오가 실패로 끝나도 다음 시나리오(한국어 문구 기대)가 영어를 보지 않게 한다.
        /// </summary>
        sealed class LangKeep : IDisposable
        {
            readonly string env = Environment.GetEnvironmentVariable(L.EnvVar), cur = L.Current;
            public void Dispose()
            {
                Environment.SetEnvironmentVariable(L.EnvVar, env);
                L.Configure(null);
                L.Use(cur);
            }
        }

        /// <summary>요약 시험용 사건 기록(AgingScenarios.Units 와 같은 모양): 부팅 cmd·hard(panic 원인)·soft, panic(txt), 분리 5분, 허브 다시 시작 2분, 의심 앱 오류, oom.</summary>
        static List<EventRec> SummaryRecs(DateTime st, string panicTxt)
        {
            string Rec(int min, string kind, string extra) => "{\"ts\":\"" + EventLog.Ts(st.AddMinutes(min)) + "\",\"port\":7001,\"board\":\"EVB-A1\",\"kind\":\"" + kind + "\",\"boot_seq\":1" + extra + "}";
            return new[]
            {
                Rec(0, "attach", ",\"reason\":\"session\",\"com\":\"COM3\",\"gap_s\":null"), Rec(10, "boot", ",\"class\":\"cmd\",\"line\":\"LDR>T\""),
                Rec(20, "panic", ",\"line\":\"Kernel panic - not syncing: x\",\"context\":" + Json.Str(panicTxt)),
                Rec(21, "boot", ",\"class\":\"hard\",\"line\":\"LDR>T\",\"cause\":{\"kind\":\"panic\",\"line\":\"Kernel panic\"}"), Rec(30, "boot", ",\"class\":\"soft\",\"line\":\"LDR>T\""),
                Rec(40, "detach", ",\"reason\":null"), Rec(45, "attach", ",\"reason\":\"reattach\",\"com\":\"COM3\",\"gap_s\":300"),
                Rec(60, "attach", ",\"reason\":\"start\",\"com\":\"COM3\",\"gap_s\":120"), Rec(70, "app_error", ",\"line\":\"APPFAULT(ED)\",\"suspect\":\"cmd_output\""),
                Rec(75, "oom", ",\"line\":\"invoked oom-killer\""),
            }.Select(EventRec.Parse).ToList();
        }

        // ================================================================== lang-resolve
        [Scenario("lang-resolve", "영어판 언어 정하기(계획 §1): 환경변수 UART_HUB_LANG(공백·대소문자 무시, ko|en 만) > config \"language\" > auto(Windows 표시 언어) · ConfigureFromFile(없음·깨짐 = auto, 환경변수가 이김) · " +
                                  "L.T 는 지금 언어 쪽 · L.Use 고정·풀기 · config language auto|ko|en 통과·그 밖은 검증 오류(ko·en 문구)·문자열 아니면 Parse 오류. 끝나면 환경변수·L 원래대로",
                  Order = 1500, TimeoutS = 30)]
        public static void Resolve(ScenarioContext ctx)
        {
            using var keep = new LangKeep();
            string auto = L.WindowsIsKorean() ? L.Ko : L.En;
            string With(string env, string cfg) { Environment.SetEnvironmentVariable(L.EnvVar, env); L.Configure(cfg); return L.Current; }

            // ---- 환경변수 > config
            string[] e1 = { With("en", "ko"), With("ko", "en"), With(" EN ", "ko"), With("Ko", "auto"), With("en", null) };
            ctx.Check(e1.SequenceEqual(new[] { "en", "ko", "en", "ko", "en" }),
                      "환경변수가 config 를 이김: (env en, cfg ko)=en · (ko, en)=ko · (' EN ', ko)=en · (Ko, auto)=ko · (en, 없음)=en: " + string.Join(",", e1));

            // ---- config > auto(환경변수 없음·빈 값·ko|en 아님)
            string[] e2 = { With(null, "en"), With(null, "ko"), With(null, " En "), With("fr", "en"), With("", "ko") };
            ctx.Check(e2.SequenceEqual(new[] { "en", "ko", "en", "en", "ko" }),
                      "환경변수 없거나 ko|en 아님 → config 가 auto 를 이김: (없음, en)=en · (없음, ko)=ko · (없음, ' En ')=en · (fr, en)=en · ('', ko)=ko: " + string.Join(",", e2));
            string[] e3 = { With(null, "auto"), With(null, null), With(null, ""), With(null, "fr"), With("de", null) };
            ctx.Check(e3.All(x => x == auto),
                      "환경변수·config 모두 auto·없음·모르는 값 → Windows 표시 언어(" + auto + ", WindowsIsKorean=" + L.WindowsIsKorean() + "): " + string.Join(",", e3));

            // ---- config.json 파일에서
            string d = Path.Combine(ctx.Dir, "cfg");
            Directory.CreateDirectory(d);
            string fEn = Path.Combine(d, "en.json"), fKo = Path.Combine(d, "ko.json"), fBad = Path.Combine(d, "bad.json"), fNone = Path.Combine(d, "none.json");
            File.WriteAllText(fEn, "{\n  // 주석·끝 쉼표 허용\n  \"language\": \"en\",\n}\n", Utf8);
            File.WriteAllText(fKo, "{\"language\": \"ko\"}", Utf8);
            File.WriteAllText(fBad, "{ \"language\": ", Utf8);
            string FromFile(string env, string path) { Environment.SetEnvironmentVariable(L.EnvVar, env); L.ConfigureFromFile(path); return L.Current; }
            string[] f = { FromFile(null, fEn), FromFile(null, fKo), FromFile(null, fNone), FromFile(null, fBad), FromFile(null, null), FromFile("ko", fEn), FromFile("en", fKo) };
            ctx.Check(f.SequenceEqual(new[] { "en", "ko", auto, auto, auto, "ko", "en" }),
                      "ConfigureFromFile: en.json(JSONC)=en · ko.json=ko · 없는 파일·깨진 JSON·null = auto(" + auto + ") · 환경변수 ko|en 이 파일을 이김: " + string.Join(",", f));

            // ---- L.T·L.Use
            Environment.SetEnvironmentVariable(L.EnvVar, null);
            L.Configure("ko");
            L.Use("en");
            bool en = L.T("한국어", "English") == "English" && L.IsEn && !L.IsKo && L.Current == "en";
            L.Use("ko");
            bool ko = L.T("한국어", "English") == "한국어" && L.IsKo && !L.IsEn && L.Current == "ko";
            L.Use("EN");
            bool upper = L.Current == "en";
            L.Use(null);
            string released = L.Current;
            L.Use("en");
            L.Use("xx");
            string bogus = L.Current;
            ctx.Check(en && ko && upper && released == "ko" && bogus == "ko",
                      "L.T: en 이면 영어 쪽·ko 면 한국어 쪽, L.Use(\"EN\") 도 en, L.Use(null)·모르는 값 = 고정을 풀고 config(ko)로 다시 정함 (" + released + "/" + bogus + ")");
            ctx.Check(L.IsValidSetting("auto") && L.IsValidSetting("ko") && L.IsValidSetting("en") && !L.IsValidSetting("fr") && !L.IsValidSetting(null),
                      "IsValidSetting: auto·ko·en 만");

            // ---- Config "language"
            L.Use("ko");
            var cDef = Config.Default();
            var cs = new[] { "auto", "ko", "en" }.Select(v => Config.Parse("{\"language\": \"" + v + "\"}")).ToList();
            ctx.Check(cDef.Language == "auto" && cDef.Validate().Count == 0 && cs.Select(c => c.Language).SequenceEqual(new[] { "auto", "ko", "en" }) && cs.All(c => c.Validate().Count == 0),
                      "config language: 기본 auto, auto·ko·en 을 읽고 검증 통과");
            var bad = Config.Parse("{\"language\": \"fr\"}");
            var badKo = bad.Validate();
            L.Use("en");
            var badEn = bad.Validate();
            ctx.Check(bad.Language == "fr" && badKo.Count == 1 && badKo[0] == "language 는 auto·ko·en: fr" && badEn.Count == 1 && badEn[0] == "language must be auto, ko or en: fr",
                      "config language 'fr' → 검증 오류 하나(ko '" + string.Join(" | ", badKo) + "' · en '" + string.Join(" | ", badEn) + "')");
            string numErr = null;
            try { Config.Parse("{\"language\": 1}"); }
            catch (ConfigException x) { numErr = string.Join(" | ", x.Errors); }
            ctx.Check(numErr != null && numErr.StartsWith("language: ", StringComparison.Ordinal), "config language 가 문자열이 아니면 Parse 오류(language: …): " + numErr);
        }

        // ================================================================== lang-aging-names
        [Scenario("lang-aging-names", "영어판 에이징 이름(계획 §4): en 에서 만든 세션 = Lang en·console.log·events\\·summary.txt(지금 언어가 바뀌어도) · ko 세션은 콘솔.log·이벤트\\·요약.txt · ToJson \"lang\"·FromJson 그대로 · " +
                                      "\"lang\" 없는 옛 기록·모르는 값 = ko · sessions.json 을 다른 언어 코어가 읽어도 그대로 · log_layout 3 뿌리: en = <기준>\\aging·<허브>\\spool(두 언어 목록, 지금 언어 먼저) · " +
                                      "SpoolDirFor 는 있는 폴더(임시기록 → spool 순), 없으면 지금 언어",
                  Order = 1501, TimeoutS = 30)]
        public static void AgingNamesScenario(ScenarioContext ctx)
        {
            using var keep = new LangKeep();
            var t = new DateTime(2026, 11, 6, 18, 0, 0);

            // ---- 세션 언어 = 만들 때의 언어
            L.Use("en");
            var en = new AgingSession { Port = 18711, Cable = "CABA", Com = "COM3", Board = "EVB-A1", Name = "night", Folder = @"D:\L\aging\night_20261106-1800", Started = t };
            L.Use("ko");
            var ko = new AgingSession { Port = 18712, Cable = "CABB", Com = "COM4", Board = "EVB-B1", Name = "야간", Folder = @"D:\L\에이징\야간_20261106-1800", Started = t };
            ctx.Check(en.Lang == "en" && en.ConsoleName == "console.log" && en.EventsDirName == "events" && en.SummaryName == "summary.txt"
                      && en.ConsolePath == @"D:\L\aging\night_20261106-1800\console.log" && en.EventsDir == @"D:\L\aging\night_20261106-1800\events"
                      && en.SummaryPath == @"D:\L\aging\night_20261106-1800\summary.txt",
                      "en 에서 만든 세션: Lang en · console.log · events\\ · summary.txt (지금 언어를 ko 로 바꾼 뒤에 봄): " + en.ConsolePath + " · " + en.SummaryPath);
            L.Use("en");
            ctx.Check(ko.Lang == "ko" && ko.ConsoleName == "콘솔.log" && ko.EventsDirName == "이벤트" && ko.SummaryName == "요약.txt" && ko.SummaryPath == @"D:\L\에이징\야간_20261106-1800\요약.txt",
                      "ko 에서 만든 세션: Lang ko · 콘솔.log · 이벤트\\ · 요약.txt (지금 언어를 en 으로 바꾼 뒤에 봄)");
            ctx.Check(AgingSession.ConsoleNameFor("en") == "console.log" && AgingSession.ConsoleNameFor("ko") == "콘솔.log" && AgingSession.ConsoleNameFor(null) == "console.log"
                      && AgingSession.EventsDirNameFor("en") == "events" && AgingSession.EventsDirNameFor("ko") == "이벤트"
                      && AgingSession.SummaryNameFor("en") == "summary.txt" && AgingSession.SummaryNameFor("ko") == "요약.txt" && AgingSession.SummaryNameFor(null) == "summary.txt",
                      "언어별 이름 표: console.log|콘솔.log · events|이벤트 · summary.txt|요약.txt, lang null = 지금 언어(en)");

            // ---- ToJson·FromJson
            AgingSession FromJson(string json) { using var doc = JsonDocument.Parse(json); return AgingSession.FromJson(doc.RootElement); }
            string ej = en.ToJson().ToString();
            var en2 = FromJson(ej);
            var ko2 = FromJson(ko.ToJson().ToString());
            ctx.Check(ej.Contains("\"lang\":\"en\"") && ko.ToJson().ToString().Contains("\"lang\":\"ko\"") && en2 != null && en2.Lang == "en" && en2.ConsoleName == "console.log" && en2.Started == t
                      && ko2 != null && ko2.Lang == "ko" && ko2.SummaryName == "요약.txt",
                      "ToJson 에 \"lang\"(en·ko), FromJson 이 그 언어로 읽음(지금 언어 en): " + ej);
            string old = "{\"port\":18713,\"cable\":\"CABC\",\"com\":\"COM5\",\"board\":\"EVB-C1\",\"name\":\"옛 세션\",\"folder\":" + Json.Str(@"D:\L\에이징\옛 세션_20261106-1800")
                         + ",\"started\":" + Json.Str(AgingSession.Iso(t)) + "}";
            var old2 = FromJson(old);
            var odd = FromJson(old.Substring(0, old.Length - 1) + ",\"lang\":\"fr\"}");
            ctx.Check(old2 != null && old2.Lang == "ko" && old2.ConsoleName == "콘솔.log" && old2.EventsDirName == "이벤트" && old2.SummaryName == "요약.txt"
                      && old2.ConsolePath == @"D:\L\에이징\옛 세션_20261106-1800\콘솔.log" && odd != null && odd.Lang == "ko",
                      "\"lang\" 없는 옛 기록(지금 언어 en) → Lang ko·한국어 이름, 모르는 값 'fr' → ko");

            // ---- sessions.json: 다른 언어 코어가 읽어도 세션마다 자기 언어
            string hub = Path.Combine(ctx.Dir, "store");
            Directory.CreateDirectory(hub);
            var store = new SessionStore(hub, null);
            bool add1 = store.Add(en), add2 = store.Add(ko);
            string sj = Read(Path.Combine(hub, "sessions.json"));
            L.Use("en");
            var back = new SessionStore(hub, null).All();
            L.Use("ko");
            var back2 = new SessionStore(hub, null).All();
            ctx.Check(add1 && add2 && Regex.IsMatch(sj, "\"lang\"\\s*:\\s*\"en\"") && Regex.IsMatch(sj, "\"lang\"\\s*:\\s*\"ko\"") && back.Count == 2
                      && back[0].Port == 18711 && back[0].Lang == "en" && back[0].ConsolePath == en.ConsolePath && back[1].Port == 18712 && back[1].Lang == "ko" && back[1].EventsDir == ko.EventsDir
                      && back2.Count == 2 && back2[0].Lang == "en" && back2[0].SummaryName == "summary.txt" && back2[1].Lang == "ko" && back2[1].SummaryName == "요약.txt",
                      "sessions.json 에 세션마다 lang, en 코어·ko 코어 어느 쪽이 읽어도 en 세션은 console.log·summary.txt, ko 세션은 콘솔.log·이벤트\\·요약.txt");

            // ---- log_layout 3 뿌리(두 언어)
            string b = Path.Combine(ctx.Dir, "roots"), h = Path.Combine(b, "hub");
            Config Mk(string hubDir) { var c = Config.Parse("{\"log_root\": " + Json.Str(b) + ", \"log_layout\": 3}"); c.HubDir = hubDir; c.ResolveLogRoot(hubDir); return c; }
            string A(string s) => Path.Combine(b, s);
            L.Use("en");
            var ce = Mk(h);
            ctx.Check(ce.AgingRoot == A("aging") && ce.AgingRoots.SequenceEqual(new[] { A("aging"), A("에이징") }) && ce.EventsTxtDir == A("aging") && ce.EventsTxtDirs.SequenceEqual(ce.AgingRoots)
                      && ce.LogRoot == Path.Combine(h, "spool") && ce.SpoolDirs.SequenceEqual(new[] { Path.Combine(h, "spool"), Path.Combine(h, "임시기록") })
                      && Config.AgingDirName == "aging" && Config.AgingDirNames.SequenceEqual(new[] { "aging", "에이징" }) && Config.SpoolDirName == "spool" && Config.SpoolDirNames.SequenceEqual(new[] { "spool", "임시기록" }),
                      "en: 새 세션 뿌리 <기준>\\aging, AgingRoots [aging, 에이징](지금 언어 먼저), 이벤트 txt 뿌리도 둘 다, 임시 기록 <허브>\\spool, SpoolDirs [spool, 임시기록]: " + string.Join(" · ", ce.AgingRoots));
            L.Use("ko");
            var ck = Mk(h);
            ctx.Check(ck.AgingRoot == A("에이징") && ck.AgingRoots.SequenceEqual(new[] { A("에이징"), A("aging") }) && ck.LogRoot == Path.Combine(h, "임시기록")
                      && ck.SpoolDirs.SequenceEqual(new[] { Path.Combine(h, "임시기록"), Path.Combine(h, "spool") }) && Config.AgingDirNames.SequenceEqual(new[] { "에이징", "aging" }),
                      "ko: 새 세션 뿌리 <기준>\\에이징, AgingRoots [에이징, aging], 임시 기록 <허브>\\임시기록, SpoolDirs [임시기록, spool]");

            // ---- SpoolDirFor: 이미 있는 폴더 먼저(임시기록 → spool), 둘 다 없으면 지금 언어
            string sh = Path.Combine(ctx.Dir, "spoolfor"), sKo = Path.Combine(sh, "임시기록"), sEn = Path.Combine(sh, "spool");
            Directory.CreateDirectory(sh);
            L.Use("en"); string s1 = Config.SpoolDirFor(sh);
            L.Use("ko"); string s2 = Config.SpoolDirFor(sh);
            Directory.CreateDirectory(sKo);
            L.Use("en"); string s3 = Config.SpoolDirFor(sh);
            var c3 = Mk(sh);
            Directory.Delete(sKo);
            Directory.CreateDirectory(sEn);
            L.Use("ko"); string s4 = Config.SpoolDirFor(sh);
            Directory.CreateDirectory(sKo);
            string s5 = Config.SpoolDirFor(sh);
            L.Use("en"); string s6 = Config.SpoolDirFor(sh);
            ctx.Check(s1 == sEn && s2 == sKo && s3 == sKo && c3.LogRoot == sKo && c3.SpoolDirs.SequenceEqual(new[] { sKo, sEn }) && s4 == sEn && s5 == sKo && s6 == sKo,
                      "SpoolDirFor: 없음 → 지금 언어(en spool·ko 임시기록) · 임시기록만 있음 → en 에서도 임시기록(ResolveLogRoot 도, SpoolDirs [임시기록, spool]) · spool 만 있음 → ko 에서도 spool · 둘 다 → 임시기록 (" +
                      string.Join(", ", new[] { s1, s2, s3, s4, s5, s6 }.Select(Path.GetFileName)) + ")");
        }

        // ================================================================== lang-event-text
        const string EvProfile = @"{
  ""name"": ""lang-sim"",
  ""loader_marker_re"": ""LDR>T"",
  ""boot_re"": ""reboot: Restarting system|LDR>T|^\\[\\s+0\\.0\\d+\\]"",
  ""uboot_prompt_re"": ""^evb: "",
  ""event_patterns"": { ""app_error"": [""APPFAULT\\(""] },
  ""app_codes"": [ { ""re"": ""APPFAULT\\([^)\\s]*\\)"" } ]
}";

        [Scenario("lang-event-text", "영어판 이벤트 txt·요약(계획 §4, 판정기·요약 직접): en 세션 판정기(지금 언어 ko) → <HHMMSS>_kernel-panic.txt·_suspect·_app-error_<코드>, 머리(Event·Board·Time·Cause line·Console log file·" +
                                     "----- 200 lines before -----·----- after -----)·결과 줄(rebooted … (hard reset)·no response·kept running) 영어·한글 없음 · ko 세션 판정기(지금 언어 en) → _커널패닉·_의심·_앱오류_<코드>, 한국어 머리·결과 · " +
                                     "요약 글은 세션 언어(en 세션 = Aging summary … 한글 없음, \"lang\" 없는 옛 ko 세션 = 에이징 요약 …)",
                  Order = 1502, TimeoutS = 30)]
        public static void EventText(ScenarioContext ctx)
        {
            using var keep = new LangKeep();
            using var prof = ProductProfile.Use(ProductProfile.Parse(EvProfile));      // 로더 표시 LDR>T·앱 오류 APPFAULT( — 하네스 프로필과 무관하게
            var cfg = Config.Default();
            string root = Path.Combine(ctx.Dir, "evtxt"), jd = Path.Combine(root, "hub", "events");
            DateTime w = default;
            long m = 5_000_000;
            EventScanner Sc(string dir, int port, string lang)
            {
                Directory.CreateDirectory(dir);
                return new EventScanner(jd, port, "CAB" + port, () => "EVB-A1", () => @"D:\L\hub\spool\20261106\" + port + "_x.log", EventPatterns.For(cfg, "CAB" + port), cfg, null, null, dir, true, lang);
            }
            void Ln(EventScanner sc, string s, double dt = 0.05) { m += (long)(dt * 1000); w = w.AddMilliseconds(dt * 1000); sc.OnLine(s, new[] { s }, w, m, "0"); }
            void Tk(EventScanner sc, double dt) { m += (long)(dt * 1000); w = w.AddMilliseconds(dt * 1000); sc.Tick(w, m); }
            string Last(List<string> l) => l.LastOrDefault() ?? "";
            var t0 = new DateTime(2026, 11, 6, 18, 0, 0);
            var all = new List<EventScanner>();
            EventScanner New(string dir, int port, string lang) { var sc = Sc(dir, port, lang); all.Add(sc); w = t0; return sc; }
            void Panic(EventScanner sc) { Ln(sc, "[   47.200000] Kernel panic - not syncing: Fatal exception"); Ln(sc, "[   47.210000] ---[ end Kernel panic - not syncing: Fatal exception ]---"); }
            void Suspect(EventScanner sc) { sc.OnTxLine("cat /mnt/sd/old.log", m); Ln(sc, "[   10.000000] Kernel panic - not syncing: old one"); Tk(sc, 31); Tk(sc, 31); Tk(sc, 31); }
            void AppErr(EventScanner sc) { Ln(sc, "APPFAULT(ED)(0003) myapp restart"); for (int i = 0; i < 4; i++) { Ln(sc, "tick " + i, 20); Tk(sc, 1); } }

            // ---- en 세션 판정기, 지금 언어 ko: txt 이름·글은 판정기 언어(en)
            L.Use("ko");
            string dA = Path.Combine(root, "aging", "a_20261106-1800", "events"), dB = Path.Combine(root, "aging", "b_20261106-1800", "events"), dC = Path.Combine(root, "aging", "c_20261106-1800", "events");
            var a = New(dA, 18711, "en");
            a.OnAttach("session", "COM3", null, null, w, m, "0");
            Panic(a);
            Ln(a, "LDR>T", 3);
            Ln(a, "[    0.000000] Booting Linux on physical CPU 0x0");
            Tk(a, 31);
            var bsc = New(dB, 18712, "en");
            Suspect(bsc);
            var csc = New(dC, 18713, "en");
            csc.OnAttach("session", "COM9", null, null, w, m, "0");
            AppErr(csc);

            // ---- ko 세션 판정기, 지금 언어 en: txt 이름·글은 판정기 언어(ko)
            L.Use("en");
            string dD = Path.Combine(root, "에이징", "d_20261106-1800", "이벤트"), dE = Path.Combine(root, "에이징", "e_20261106-1800", "이벤트"), dF = Path.Combine(root, "에이징", "f_20261106-1800", "이벤트");
            var dsc = New(dD, 18714, "ko");
            dsc.OnAttach("session", "COM3", null, null, w, m, "0");
            Panic(dsc);
            Tk(dsc, 31); Tk(dsc, 31);
            var esc = New(dE, 18715, "ko");
            esc.OnAttach("session", "COM9", null, null, w, m, "0");
            AppErr(esc);
            var fsc = New(dF, 18716, "ko");
            Suspect(fsc);
            foreach (var sc in all) sc.Close("test end");

            // ---- en: 이름
            var fa = FilesIn(dA); var fb = FilesIn(dB); var fc = FilesIn(dC);
            ctx.Check(a.Lang == "en" && fa.SequenceEqual(new[] { "180000_kernel-panic.txt" }) && fb.SequenceEqual(new[] { "180000_kernel-panic_suspect.txt" })
                      && fc.SequenceEqual(new[] { "180000_app-error_APPFAULT(ED).txt" }),
                      "en 세션 txt 이름(지금 언어 ko): <HHMMSS>_kernel-panic · _kernel-panic_suspect · _app-error_<코드> (" + string.Join(" / ", fa.Concat(fb).Concat(fc)) + ")");
            // ---- en: 머리·결과 줄
            var la = fa.Count == 1 ? Lines(Path.Combine(dA, fa[0])) : new List<string>();
            ctx.Check(la.Count > 8 && la[0] == "Event: Kernel panic" && la[1] == "Board: EVB-A1 (port 18711 · COM3)" && la[2].StartsWith("Time: 2026-11-06 18:00:00", StringComparison.Ordinal)
                      && la[3] == "Cause line: [   47.200000] Kernel panic - not syncing: Fatal exception" && la[4] == @"Console log file: D:\L\hub\spool\20261106\18711_x.log"
                      && la[5] == "----- 200 lines before -----" && la.Contains("----- after -----") && la.Any(l => l.StartsWith(">>> ", StringComparison.Ordinal) && l.Contains("Kernel panic"))
                      && Last(la).StartsWith("Result: rebooted at 18:00:0", StringComparison.Ordinal) && Last(la).EndsWith(" (hard reset).", StringComparison.Ordinal),
                      "en txt(panic → 하드 리셋): 머리 Event·Board(port · COM)·Time·Cause line·Console log file·'----- 200 lines before -----'·'----- after -----', 끝 'Result: rebooted at … (hard reset).' | "
                      + string.Join(" / ", la.Take(6)) + " … " + Last(la));
            var lb = fb.Count == 1 ? Lines(Path.Combine(dB, fb[0])) : new List<string>();
            var lc = fc.Count == 1 ? Lines(Path.Combine(dC, fc[0])) : new List<string>();
            ctx.Check(lb.Count > 5 && lb[0] == "Event: Kernel panic (suspect)" && lb.Any(l => l.StartsWith("Why suspect: ", StringComparison.Ordinal)) && Last(lb).StartsWith("Result: no response. No output for ", StringComparison.Ordinal)
                      && Last(lb).EndsWith(" s; the board may have hung.", StringComparison.Ordinal)
                      && lc.Count > 5 && lc[0] == "Event: App error APPFAULT(ED)" && lc[1] == "Board: EVB-A1 (port 18713 · COM9)" && Last(lc) == "Result: kept running. No reboot within 60 s.",
                      "en txt: 의심 panic = 'Event: Kernel panic (suspect)'·'Why suspect: …'·'Result: no response. …', 앱 오류 = 'Event: App error APPFAULT(ED)'·'Result: kept running. No reboot within 60 s.' | "
                      + Last(lb) + " | " + Last(lc));
            string enAll = string.Join("\n", la.Concat(lb).Concat(lc));
            ctx.Check(!HasHangul(enAll) && !fa.Concat(fb).Concat(fc).Any(HasHangul), "en txt 이름·글에 한글 없음(지금 언어 ko 인데도)" + (HasHangul(enAll) ? ": " + FirstHangulLine(enAll) : ""));

            // ---- ko: 이름·머리·결과(지금 언어 en)
            var fd = FilesIn(dD); var fe = FilesIn(dE); var ff = FilesIn(dF);
            ctx.Check(dsc.Lang == "ko" && fd.SequenceEqual(new[] { "180000_커널패닉.txt" }) && fe.SequenceEqual(new[] { "180000_앱오류_APPFAULT(ED).txt" }) && ff.SequenceEqual(new[] { "180000_커널패닉_의심.txt" }),
                      "ko 세션 txt 이름(지금 언어 en): _커널패닉 · _앱오류_<코드> · _커널패닉_의심 (" + string.Join(" / ", fd.Concat(fe).Concat(ff)) + ")");
            var ld = fd.Count == 1 ? Lines(Path.Combine(dD, fd[0])) : new List<string>();
            var le = fe.Count == 1 ? Lines(Path.Combine(dE, fe[0])) : new List<string>();
            var lf = ff.Count == 1 ? Lines(Path.Combine(dF, ff[0])) : new List<string>();
            ctx.Check(ld.Count > 8 && ld[0] == "이벤트 종류: Kernel panic" && ld[1] == "보드: EVB-A1 (포트 18714 · COM3)" && ld[2].StartsWith("시각: 2026-11-06 18:00:00", StringComparison.Ordinal)
                      && ld[3].StartsWith("원인 줄: ", StringComparison.Ordinal) && ld[4].StartsWith("콘솔 로그 파일: ", StringComparison.Ordinal) && ld[5] == "----- 앞 200줄 -----" && ld.Contains("----- 이후 -----")
                      && Last(ld).StartsWith("결과: 응답 없음.", StringComparison.Ordinal)
                      && le.Count > 5 && le[0] == "이벤트 종류: 앱 오류 APPFAULT(ED)" && Last(le).StartsWith("결과: 계속 동작.", StringComparison.Ordinal)
                      && lf.Count > 5 && lf[0] == "이벤트 종류: Kernel panic (의심)" && lf.Any(l => l.StartsWith("의심 이유: ", StringComparison.Ordinal)) && Last(lf).StartsWith("결과: 응답 없음.", StringComparison.Ordinal)
                      && !ld.Concat(le).Concat(lf).Any(l => l.StartsWith("Event: ", StringComparison.Ordinal) || l.StartsWith("Result: ", StringComparison.Ordinal)),
                      "ko txt(지금 언어 en): 머리 '이벤트 종류·보드(포트)·시각·원인 줄·콘솔 로그 파일·----- 앞 200줄 -----·----- 이후 -----', 결과 '응답 없음'·'계속 동작', 의심 이유 — 영어 줄 없음 | "
                      + Last(ld) + " | " + Last(le));

            // ---- 요약 글 = 세션 언어
            var st = new DateTime(2026, 11, 6, 9, 0, 0);
            L.Use("en");
            var se = new AgingSession { Port = 7001, Com = "COM3", Board = "EVB-A1", Name = "night", Folder = @"D:\L\aging\night_20261106-0900", Started = st };
            L.Use("ko");
            string txt = AgingSummary.Text(se, SummaryRecs(st, @"D:\L\aging\night_20261106-0900\events\092000_kernel-panic.txt"), st.AddMinutes(133));
            ctx.Check(se.Lang == "en" && txt.StartsWith("Aging summary\r\nName: night\r\nBoard: EVB-A1 (port 7001 · COM3)\r\nPeriod: ", StringComparison.Ordinal) && txt.Contains("(2 h 13 min)")
                      && txt.Contains("Recorded time: 2 h 6 min (2 recording gap(s))") && txt.Contains(@"Console log: D:\L\aging\night_20261106-0900\console.log (0 lines)")
                      && txt.Contains("Boots: 3 total · command (cmd) 1 · soft 1 · hard 1 (unexpected reboots: ") && txt.Contains("Events: 2 (+ 1 suspect)")
                      && txt.Contains(@"kernel panic  Kernel panic - not syncing: x  → events\092000_kernel-panic.txt") && txt.Contains("Reboot after panic")
                      && txt.Contains("  OOM  invoked oom-killer") && txt.Contains("app error (suspect)  APPFAULT(ED)") && txt.Contains("Recording gaps: 2\r\n")
                      && txt.Contains("(5 min, cable disconnected)") && txt.Contains("(2 min, hub restarted)"),
                      "en 세션 요약(지금 언어 ko): Aging summary·Name·Board(port)·Period·Recorded time·Console log(console.log)·Boots·Events(+ suspect)·kernel panic → events\\…·Reboot after panic·Recording gaps(cable disconnected·hub restarted)");
            ctx.Check(!HasHangul(txt), "en 세션 요약 글에 한글 없음" + (HasHangul(txt) ? ": " + FirstHangulLine(txt) : ""));
            ctx.Log("en 요약 예시:");
            foreach (var l in txt.Replace("\r\n", "\n").TrimEnd('\n').Split('\n')) ctx.Log("    | " + l);
            L.Use("en");
            AgingSession so;
            using (var doc = JsonDocument.Parse("{\"port\":7001,\"cable\":\"CABA\",\"com\":\"COM3\",\"board\":\"EVB-A1\",\"name\":\"야간\",\"folder\":" + Json.Str(@"D:\L\에이징\야간_20261106-0900")
                                                + ",\"started\":" + Json.Str(AgingSession.Iso(st)) + "}"))
                so = AgingSession.FromJson(doc.RootElement);
            string kt = AgingSummary.Text(so, SummaryRecs(st, @"D:\L\에이징\야간_20261106-0900\이벤트\092000_커널패닉.txt"), st.AddMinutes(133));
            ctx.Check(so.Lang == "ko" && kt.StartsWith("에이징 요약\r\n이름: 야간\r\n보드: EVB-A1 (포트 7001 · COM3)\r\n기간: ", StringComparison.Ordinal) && kt.Contains("(2시간 13분)")
                      && kt.Contains("기록한 시간: 2시간 6분 (기록 끊김 2번)") && kt.Contains(@"콘솔 로그: D:\L\에이징\야간_20261106-0900\콘솔.log (0줄)")
                      && kt.Contains("부팅: 모두 3번 · 명령(cmd) 1 · 소프트(soft) 1 · 하드(hard) 1") && kt.Contains("이벤트: 2건 (+ 의심 1건)")
                      && kt.Contains(@"커널 패닉  Kernel panic - not syncing: x  → 이벤트\092000_커널패닉.txt") && kt.Contains("panic 뒤 재부팅") && kt.Contains("앱 오류 (의심)")
                      && kt.Contains("기록 끊긴 구간: 2번") && kt.Contains("(5분, 케이블 분리)") && kt.Contains("(2분, 허브 다시 시작)")
                      && !kt.Contains("Aging summary") && !kt.Contains("Recording gaps") && !kt.Contains(" min"),
                      "\"lang\" 없는 옛 ko 세션 요약(지금 언어 en): 한국어 그대로(에이징 요약·이름·보드(포트)·콘솔.log·부팅·이벤트(+ 의심)·커널 패닉 → 이벤트\\…·기록 끊긴 구간), 영어 섞이지 않음");
        }

        // ================================================================== lang-hub-switch
        [Scenario("lang-hub-switch", "언어를 바꿔도 세션은 자기 언어(계획 §4, Hub + FakeBoard, log_layout 3): ko 허브에서 시작한 세션(<기준>\\에이징\\야간_…·콘솔.log·이벤트\\, sessions.json lang ko)이 " +
                                     "영어로 다시 켠 코어에서도 Lang ko 로 이어짐 → _커널패닉.txt·한국어 머리·콘솔.log '기록 끊김' 표시·요약.txt 한국어(hub.log 는 지금 언어 en) · " +
                                     "같은 코어의 새 en 세션 = <기준>\\aging\\night_…\\(console.log·events\\), _kernel-panic.txt·영어 머리·console.log 'Aging started/ended'·summary.txt 영어·한글 없음",
                  Order = 1503, TimeoutS = 120)]
        public static void HubSwitch(ScenarioContext ctx)
        {
            using var keep = new LangKeep();
            L.Use("ko");
            string root = Path.Combine(ctx.Dir, "hubsw"), logs = Path.Combine(root, "logs");
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIMLANG", LoaderDelayMs = 700 }, "SIMCABLS", "COM721", 18720, c =>
            {
                c.BasePort = 18730; c.RingMb = 0.25; c.BootMergeMs = 1500;
                c.LogRoot = logs; c.HubDir = Path.Combine(root, "hub"); c.LogLayout = 3;
                c.ResolveLogRoot(c.HubDir);
            });
            int port = r.Cab.TcpPort;
            string spool = r.Cfg.LogRoot, hubDir = r.Cfg.EffectiveHubDir;
            string HubLog() => Read(Path.Combine(hubDir, "hub.log"));
            string SessionsJson() => Read(Path.Combine(hubDir, "sessions.json"));

            // ① ko 허브에서 세션 시작
            var s1 = r.Call("session", "start", r.P, "야간", "--json");
            string kf = J.S(s1, "folder") ?? "";
            ctx.Check(J.S(s1, "status") == "ok" && Path.GetDirectoryName(kf) == Path.Combine(logs, "에이징") && Regex.IsMatch(Path.GetFileName(kf), @"^야간_\d{8}-\d{4}$")
                      && File.Exists(Path.Combine(kf, "콘솔.log")) && Directory.Exists(Path.Combine(kf, "이벤트")) && Regex.IsMatch(SessionsJson(), "\"lang\"\\s*:\\s*\"ko\"")
                      && spool == Path.Combine(root, "hub", "임시기록"),
                      "ko 허브: 세션 폴더 <기준>\\에이징\\야간_<시각>\\(콘솔.log·이벤트\\), sessions.json lang ko, 임시 기록 <허브>\\임시기록: " + kf);
            r.Board.Emit("before switch line\r\n");
            Thread.Sleep(500);

            // ② 영어로 다시 켬(언어를 바꾸면 감독이 코어를 다시 띄우는 것과 같음)
            r.Hub.Stop();
            ctx.Check(r.Hub.Control.Events.Join(3000), "ko 허브 정지 → 추적 스레드 끝(세션은 sessions.json 에 남음)");
            L.Use("en");
            Thread.Sleep(1200);
            var r2 = HubRig.Boot(ctx, r.Cfg, r.Board, r.Prov, "SIMCABLS", true);
            var det2 = r2.Hub.Control.Events;
            ctx.WaitUntil(() => det2.SessionOf(r2.Cab) != null && det2.ScannerOf(r2.Cab) != null, 5000, "영어로 다시 켠 코어가 ko 세션을 이어 따름");
            var ks = det2.SessionOf(r2.Cab);
            ctx.Check(ks.Lang == "ko" && ks.Folder == kf && ks.ConsoleName == "콘솔.log" && det2.ScannerOf(r2.Cab).Lang == "ko",
                      "다시 켠 코어(지금 언어 en): 세션 Lang ko·같은 폴더·콘솔.log, 판정기 언어 ko");

            // ③ ko 세션 안 panic → 한국어 txt, 콘솔.log 에 한국어 '기록 끊김'
            string kev = Path.Combine(kf, "이벤트");
            r2.Board.EmitPrintk("Kernel panic - not syncing: after switch");
            ctx.WaitUntil(() => FilesIn(kev).Count >= 1, 15000, "ko 세션 이벤트 txt(지금 언어 en)");
            Thread.Sleep(500);
            var kfiles = FilesIn(kev);
            string ktxt = kfiles.Count == 1 ? Read(Path.Combine(kev, kfiles[0])) : "";
            var kcon = Lines(Path.Combine(kf, "콘솔.log"));
            ctx.Check(kfiles.Count == 1 && Regex.IsMatch(kfiles[0], @"^\d{6}_커널패닉\.txt$") && ktxt.StartsWith("이벤트 종류: Kernel panic\r\n보드: ", StringComparison.Ordinal)
                      && ktxt.Contains("\r\n----- 이후 -----\r\n") && !ktxt.Contains("Event: ") && !ktxt.Contains("----- after -----") && !File.Exists(Path.Combine(kf, "console.log")) && !Directory.Exists(Path.Combine(kf, "events")),
                      "ko 세션(지금 언어 en) panic → 이벤트\\<HHMMSS>_커널패닉.txt·한국어 머리, console.log·events\\ 는 만들지 않음 (" + string.Join(",", kfiles) + ")");
            ctx.Check(kcon.Count >= 3 && kcon[0].Contains("*** 에이징 시작 '야간'") && kcon.Any(l => l.Contains("*** 기록 끊김 ") && l.Contains("허브가 다시 켜졌거나")) && kcon.Any(l => l.Contains("after switch"))
                      && !kcon.Any(l => l.Contains("*** Recording gap") || l.Contains("*** Aging ")),
                      "ko 세션 콘솔.log: 다시 켠 뒤 '*** 기록 끊김 … 허브가 다시 켜졌거나 …' 한국어 표시(영어 표시 없음), 새 줄 이어 씀");

            // ④ ko 세션 끝 → 요약.txt 한국어, hub.log 는 지금 언어(en)
            var stop1 = r2.Call("session", "stop", r2.P, "--json");
            string ksum = Path.Combine(kf, "요약.txt"), ktext = Read(ksum);
            ctx.Check(J.S(stop1, "status") == "ok" && J.S(stop1, "summary") == ksum && J.S(stop1, "summary_text") == ktext && !File.Exists(Path.Combine(kf, "summary.txt"))
                      && ktext.StartsWith("에이징 요약\r\n이름: 야간\r\n", StringComparison.Ordinal) && ktext.Contains("커널 패닉  ") && ktext.Contains("→ 이벤트\\") && ktext.Contains("기록 끊긴 구간: 1번")
                      && ktext.Contains("허브 다시 시작") && !ktext.Contains("Aging summary") && Lines(Path.Combine(kf, "콘솔.log")).LastOrDefault()?.EndsWith("*** 에이징 끝 ***", StringComparison.Ordinal) == true,
                      "ko 세션 끝(지금 언어 en): 요약.txt(summary.txt 아님) 한국어 — 이름·커널 패닉 → 이벤트\\…·기록 끊긴 구간 1번(허브 다시 시작), 콘솔.log 끝 '*** 에이징 끝 ***'");
            string hl = HubLog();
            ctx.Check(hl.Contains("에이징 시작 :" + port + " '야간'") && hl.Contains("Aging ended :" + port + " '야간'"),
                      "hub.log 는 그때의 지금 언어: 시작(ko 허브) '에이징 시작 :" + port + "', 끝(en 허브) 'Aging ended :" + port + "'");

            // ⑤ 같은 en 코어에서 새 세션 → <기준>\aging\ 에 영어 이름
            Thread.Sleep(1500);                                                                   // 앞 세션의 사건이 새 세션 기간(시작 1초 전부터)에 들지 않게
            var s2 = r2.Call("session", "start", r2.P, "night", "--json");
            string ef = J.S(s2, "folder") ?? "";
            ctx.Check(J.S(s2, "status") == "ok" && Path.GetDirectoryName(ef) == Path.Combine(logs, "aging") && Regex.IsMatch(Path.GetFileName(ef), @"^night_\d{8}-\d{4}$")
                      && File.Exists(Path.Combine(ef, "console.log")) && Directory.Exists(Path.Combine(ef, "events")) && !File.Exists(Path.Combine(ef, "콘솔.log")) && !Directory.Exists(Path.Combine(ef, "이벤트"))
                      && det2.SessionOf(r2.Cab)?.Lang == "en" && Regex.IsMatch(SessionsJson(), "\"lang\"\\s*:\\s*\"en\"") && r2.Cfg.LogRoot == spool,
                      "en 코어의 새 세션: <기준>\\aging\\night_<시각>\\(console.log·events\\), Lang en(sessions.json), 임시 기록 폴더는 그대로(" + Path.GetFileName(spool) + "): " + ef);
            string eev = Path.Combine(ef, "events");
            r2.Board.Emit("aging line en\r\n");
            r2.Board.EmitPrintk("Kernel panic - not syncing: english session");
            ctx.WaitUntil(() => FilesIn(eev).Count >= 1, 15000, "en 세션 이벤트 txt");
            Thread.Sleep(500);
            var efiles = FilesIn(eev);
            string etxt = efiles.Count == 1 ? Read(Path.Combine(eev, efiles[0])) : "";
            var econ = Lines(Path.Combine(ef, "console.log"));
            ctx.Check(efiles.Count == 1 && Regex.IsMatch(efiles[0], @"^\d{6}_kernel-panic\.txt$") && etxt.StartsWith("Event: Kernel panic\r\nBoard: ", StringComparison.Ordinal)
                      && etxt.Contains("\r\nCause line: ") && etxt.Contains("english session") && etxt.Contains("\r\nConsole log file: ") && etxt.Contains("\r\n----- 200 lines before -----\r\n")
                      && etxt.Contains("\r\n----- after -----\r\n") && !HasHangul(Strip(etxt, spool, ctx.Dir)),
                      "en 세션 panic → events\\<HHMMSS>_kernel-panic.txt·영어 머리, (경로 밖) 한글 없음 (" + string.Join(",", efiles) + ")" + (HasHangul(Strip(etxt, spool, ctx.Dir)) ? ": " + FirstHangulLine(Strip(etxt, spool, ctx.Dir)) : ""));
            ctx.Check(econ.Count >= 2 && econ[0].Contains("*** Aging started 'night'") && econ.Any(l => l.EndsWith("] aging line en", StringComparison.Ordinal)) && !HasHangul(Strip(string.Join("\n", econ), ctx.Dir)),
                      "en 세션 console.log: '*** Aging started 'night' …', RX 줄, 한글 없음: " + (econ.Count > 0 ? econ[0] : "(없음)"));

            // ⑥ en 세션 끝 → summary.txt 영어
            var stop2 = r2.Call("session", "stop", r2.P, "--json");
            string esum = Path.Combine(ef, "summary.txt"), etext = Read(esum);
            string eplain = Strip(etext, spool, ctx.Dir);
            ctx.Check(J.S(stop2, "status") == "ok" && J.S(stop2, "summary") == esum && J.S(stop2, "summary_text") == etext && !File.Exists(Path.Combine(ef, "요약.txt"))
                      && etext.StartsWith("Aging summary\r\nName: night\r\nBoard: ", StringComparison.Ordinal) && etext.Contains("\r\nEvents: 1") && etext.Contains("kernel panic  ")
                      && etext.Contains("→ events\\") && etext.Contains("Recording gaps: none") && Lines(Path.Combine(ef, "console.log")).LastOrDefault()?.EndsWith("*** Aging ended ***", StringComparison.Ordinal) == true,
                      "en 세션 끝: summary.txt(요약.txt 아님) — Aging summary·Name·Events 1·kernel panic → events\\…·Recording gaps: none, console.log 끝 '*** Aging ended ***'");
            ctx.Check(!HasHangul(eplain), "en 세션 summary.txt 에 (경로 밖) 한글 없음" + (HasHangul(eplain) ? ": " + FirstHangulLine(eplain) : ""));
            ctx.Log("summary.txt 예시(" + esum + "):");
            foreach (var l in etext.Replace("\r\n", "\n").TrimEnd('\n').Split('\n')) ctx.Log("    | " + l);
            r2.Hub.Stop();
        }

        // ================================================================== lang-e2e
        [Scenario("lang-e2e", "영어판 실제 exe(감독 --no-tray --sim, C-13 격리): 감독을 UART_HUB_LANG=en 으로(config language 는 ko — 환경변수가 이김, 코어는 물려받음) → hub.log 'Supervisor started'·'Core started' · " +
                              "cli(UART_HUB_LANG=en) session status/start/stop 영어 · 연결 안 된 COM·이미 에이징 중 거부 문구(코어) 영어 · 세션 = <기준>\\aging\\night run_<시각>\\console.log·events\\ · panic → _kernel-panic.txt·" +
                              "풍선 'Balloon: … Kernel panic, …'·한 줄 status · session summary 의 note·hint_text 영어 · summary.txt 영어 · <허브>\\spool(임시기록 없음) · hub.log(sim 줄 빼고) 한글 없음 · " +
                              "같은 허브에 cli 환경변수 ko·빈 값(config ko) → 한국어. 실제 시스템 무변경",
                  Order = 1504, TimeoutS = 180)]
        public static void EndToEnd(ScenarioContext ctx)
        {
            var real = RealSystem.Capture(ctx);
            const string Cab = "SIMCABLE", Com = "COM741";
            const int Ctl = 18740, Port = 18750;
            string Ps = Port.ToString(CultureInfo.InvariantCulture);
            var g = IconRig.Create(ctx, "lange2e", Ctl, Port, "{\"cable\":\"" + Cab + "\",\"com\":\"" + Com + "\",\"hostname\":\"SIMLANGE\"}", null, ", \"log_layout\": 3, \"language\": \"ko\"");
            var r = g.R;
            Upd.Start(r, TestEnv.UartHubExe, new[] { "sup", "--no-tray", "--hub-dir", g.Hub, "--sim", g.Spec, "--sim-dll", TestEnv.TestsDll, "--tick-ms", "100" },
                      Upd.Env(L.EnvVar, "en", "UART_HUB_TEST_EVENTS", "6,1"));
            g.WaitCore("UART_HUB_LANG=en 감독 → 코어");
            ctx.WaitUntil(() => g.StatusOf(Cab) == "attached", 20000, Cab + " attached");
            (int Code, JsonElement J, string Out, string Err) Cli(string lang, params string[] a)
            {
                var e = new Dictionary<string, string> { ["UART_HUB_DIR"] = g.Hub, [L.EnvVar] = lang };
                var o = r.Run(TestEnv.UartHubExe, new[] { "cli" }.Concat(a).ToArray(), e, 60000);
                return (o.Code, Upd.Parse(o.Out), o.Out.Trim(), o.Err.Trim());
            }
            string exeDir = Path.GetDirectoryName(TestEnv.UartHubExe), dllDir = Path.GetDirectoryName(TestEnv.TestsDll);
            string Plain(string s) => Strip(s, r.Root, exeDir, dllDir);
            string agingRoot = Path.Combine(r.Root, "aging");

            // ① 감독·코어 언어 = 환경변수(en) — config 는 ko
            string log0 = g.Log();
            ctx.Check(log0.Contains("Supervisor started pid=") && log0.Contains("Core started pid=") && !log0.Contains("감독 시작 pid=") && !log0.Contains("코어 시작 pid="),
                      "UART_HUB_LANG=en 감독(config language ko 보다 환경변수가 이김): hub.log 'Supervisor started'·'Core started'");

            // ② CLI 영어 · 코어 거부 문구 영어
            var st0 = Cli("en", "session", "status");
            var bad = Cli("en", "session", "start", "COM999", "--json");
            string badErr = Upd.S(bad.J, "error") ?? "";
            ctx.Check(st0.Code == 0 && st0.Out == "No port is aging" && bad.Code == 102 && badErr.StartsWith("Aging not started — 'COM999' is not connected. Connect it first with /connect-uart COM999", StringComparison.Ordinal)
                      && !HasHangul(badErr),
                      "cli(en) session status → 'No port is aging', 연결 안 된 COM → 102 영어 안내(코어 문구 — 코어도 en): " + badErr);

            // ③ 세션 시작 → <기준>\aging\night run_<시각>\(console.log·events\)
            var st = Cli("en", "session", "start", Ps, "night run");
            ctx.Check(st.Code == 0 && st.Out.StartsWith("Aging started: 'night run' · port " + Ps + " (" + Com + ")", StringComparison.Ordinal) && st.Out.Contains("Folder: ") && !HasHangul(Plain(st.Out)),
                      "cli(en) session start <port> <이름> → 'Aging started: …'·'Folder: …' (경로 밖 한글 없음): " + st.Out.Replace("\r", "").Replace("\n", " / "));
            var sj = Cli("en", "session", "status", "--json");
            var x = N(sj.J, "count") == 1 ? sj.J.GetProperty("sessions")[0] : default;
            string folder = Upd.S(x, "folder") ?? "";
            string cl = x.ValueKind == JsonValueKind.Object && x.TryGetProperty("console_log", out var clv) ? Upd.S(clv, "path") : null;
            ctx.Check(sj.Code == 0 && Upd.S(sj.J, "aging_root") == agingRoot && Path.GetDirectoryName(folder) == agingRoot && Regex.IsMatch(Path.GetFileName(folder), @"^night run_\d{8}-\d{4}$")
                      && cl == Path.Combine(folder, "console.log") && File.Exists(cl) && Directory.Exists(Path.Combine(folder, "events"))
                      && !File.Exists(Path.Combine(folder, "콘솔.log")) && !Directory.Exists(Path.Combine(folder, "이벤트")) && !Directory.Exists(Path.Combine(r.Root, "에이징"))
                      && Regex.IsMatch(Read(Path.Combine(g.Hub, "sessions.json")), "\"lang\"\\s*:\\s*\"en\""),
                      "en 코어의 세션: aging_root <기준>\\aging, 폴더 aging\\night run_<시각>\\(console.log·events\\), 에이징\\·콘솔.log 없음, sessions.json lang en: " + folder);
            var dup = Cli("en", "session", "start", Ps, "--json");
            string dupErr = Upd.S(dup.J, "error") ?? "";
            ctx.Check(dup.Code == 106 && dupErr.Contains("Port " + Ps + " is already aging ('night run')") && !HasHangul(dupErr), "같은 포트 두 번째 start → 106 영어 거부(코어 문구): " + dupErr);

            // ④ panic → events\<HHMMSS>_kernel-panic.txt·console.log·풍선(감독)
            string evDir = Path.Combine(folder, "events");
            g.Inject("emit " + Cab + " " + B64("aging line one\r\n[  123.000000] Kernel panic - not syncing: lang e2e\r\n"), "panic 줄");
            ctx.WaitUntil(() => FilesIn(evDir).Count >= 1, 15000, "en 세션 이벤트 txt");
            Thread.Sleep(500);
            var evFiles = FilesIn(evDir);
            string txtPath = evFiles.Count == 1 ? Path.Combine(evDir, evFiles[0]) : "";
            string txt = Read(txtPath);
            ctx.Check(evFiles.Count == 1 && Regex.IsMatch(evFiles[0], @"^\d{6}_kernel-panic\.txt$") && txt.StartsWith("Event: Kernel panic\r\nBoard: ", StringComparison.Ordinal)
                      && txt.Contains("\r\nCause line: ") && txt.Contains("Kernel panic - not syncing: lang e2e") && txt.Contains("\r\n----- after -----\r\n") && !HasHangul(Plain(txt)),
                      "panic → events\\<HHMMSS>_kernel-panic.txt·영어 머리, (경로 밖) 한글 없음 (" + string.Join(",", evFiles) + ")" + (HasHangul(Plain(txt)) ? ": " + FirstHangulLine(Plain(txt)) : ""));
            var con = Lines(Path.Combine(folder, "console.log"));
            ctx.Check(con.Count >= 3 && con[0].Contains("*** Aging started 'night run'") && con.Any(l => l.EndsWith("] aging line one", StringComparison.Ordinal)) && !HasHangul(Plain(string.Join("\n", con))),
                      "console.log: '*** Aging started 'night run' …', RX 줄, 한글 없음: " + (con.Count > 0 ? con[0] : "(없음)"));
            ctx.WaitUntil(() => g.Log().Contains("Balloon: "), 15000, "감독 풍선 줄(영어 'Balloon: ')");
            string balloon = g.Log().Replace("\r\n", "\n").Split('\n').FirstOrDefault(l => l.Contains("Balloon: ")) ?? "";
            ctx.Check(balloon.Contains(": Kernel panic, ") && balloon.Contains("Click to open the context log.") && balloon.Contains("(click opens " + txtPath + ")") && !HasHangul(Plain(balloon)) && !g.Log().Contains("풍선: "),
                      "감독 풍선(en): '… Kernel panic, hh:mm:ss. Click to open the context log. (click opens <txt>)', '풍선:' 줄 없음: " + Plain(balloon));
            ctx.Check(Directory.Exists(Path.Combine(g.Hub, "spool")) && !Directory.Exists(Path.Combine(g.Hub, "임시기록")), "en 코어의 숨은 임시 기록 = <허브>\\spool(임시기록 없음)");

            // ⑤ 한 줄 status · summary(비JSON) · 기록 없는 포트의 note·hint_text
            var one = Cli("en", "session", "status");
            ctx.Check(one.Code == 0 && one.Out.StartsWith(Ps + " " + Com + " ", StringComparison.Ordinal) && one.Out.Contains(" · aging 'night run' · ") && one.Out.Contains(" · events 1(kernel panic 1)")
                      && Regex.IsMatch(one.Out, @" · console\.log [\d,]+ lines · ") && !HasHangul(Plain(one.Out)),
                      "cli(en) session status 한 줄: '<port> <COM> <보드> · aging 'night run' · … · events 1(kernel panic 1) · console.log N lines · <폴더>': " + Plain(one.Out));
            var sumNow = Cli("en", "session", "summary", Ps, "--since", "1h");
            ctx.Check(sumNow.Code == 0 && sumNow.Out.StartsWith("Port " + Ps, StringComparison.Ordinal) && sumNow.Out.Contains("Context logs: ") && sumNow.Out.Contains("Boots ") && !HasHangul(Plain(sumNow.Out)),
                      "cli(en) session summary <port> --since 1h(비JSON): 'Port …'·'Boots …'·'Context logs: …', 한글 없음" + (HasHangul(Plain(sumNow.Out)) ? ": " + FirstHangulLine(Plain(sumNow.Out)) : ""));
            var hint = Cli("en", "session", "summary", "18759", "--since", "2h", "--json");
            string hintText = Upd.S(hint.J, "hint_text") ?? "", note = Upd.S(hint.J, "note") ?? "";
            ctx.Check(hint.Code == 0 && Upd.S(hint.J, "hint") == "no_boots_seen" && hintText.StartsWith("No boot was recognized in this period.", StringComparison.Ordinal) && note.StartsWith("No event record", StringComparison.Ordinal)
                      && !HasHangul(hintText) && !HasHangul(note),
                      "cli(en) 기록 없는 포트 2시간 summary: hint no_boots_seen·hint_text 영어·note 영어: " + hintText.Substring(0, Math.Min(80, hintText.Length)) + " | " + note);

            // ⑥ 세션 끝 → summary.txt 영어
            var stop = Cli("en", "session", "stop", Ps);
            string sum = Path.Combine(folder, "summary.txt"), sumText = Read(sum);
            ctx.Check(stop.Code == 0 && stop.Out.StartsWith("Aging ended. Summary saved: ", StringComparison.Ordinal) && stop.Out.Contains("Aging summary") && stop.Out.Contains("Name: night run")
                      && File.Exists(sum) && !File.Exists(Path.Combine(folder, "요약.txt")) && sumText.StartsWith("Aging summary\r\nName: night run\r\n", StringComparison.Ordinal)
                      && sumText.Contains("kernel panic  ") && sumText.Contains("→ events\\") && Lines(Path.Combine(folder, "console.log")).LastOrDefault()?.EndsWith("*** Aging ended ***", StringComparison.Ordinal) == true,
                      "cli(en) session stop → 'Aging ended. Summary saved: …' + 요약 글, aging\\night run_…\\summary.txt(요약.txt 아님)·console.log 끝 '*** Aging ended ***'");
            ctx.Check(!HasHangul(Plain(sumText)) && !HasHangul(Plain(stop.Out)),
                      "summary.txt·stop 출력에 (경로 밖) 한글 없음" + (HasHangul(Plain(sumText)) ? ": " + FirstHangulLine(Plain(sumText)) : HasHangul(Plain(stop.Out)) ? ": " + FirstHangulLine(Plain(stop.Out)) : ""));
            ctx.Log("summary.txt 예시:");
            foreach (var l in sumText.Replace("\r\n", "\n").TrimEnd('\n').Split('\n')) ctx.Log("    | " + Plain(l));

            // ⑦ hub.log: en 감독·코어·CLI 가 쓴 줄에 한글 없음(시험 도구 sim 줄 빼고, 경로 빼고) — 한국어 CLI 를 부르기 전에 본다
            var leaks = g.Log().Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0 && !SimLine.IsMatch(l)).Select(Plain).Where(HasHangul).ToList();
            ctx.Check(leaks.Count == 0, "hub.log(en 감독·코어) 에 한글 없음(sim 줄·경로 제외)" + (leaks.Count > 0 ? " — " + leaks.Count + "줄: " + string.Join(" ⏎ ", leaks.Take(6)) : ""));

            // ⑧ 같은 허브, CLI 프로세스마다 언어: 환경변수 ko → 한국어, 빈 값 → config.json language ko → 한국어, en → 영어
            var k1 = Cli("ko", "session", "status");
            var k2 = Cli("", "session", "status");
            var e1 = Cli("en", "session", "status");
            ctx.Check(k1.Out == "에이징 중인 포트 없음" && k2.Out == "에이징 중인 포트 없음" && e1.Out == "No port is aging",
                      "CLI 언어는 프로세스마다: UART_HUB_LANG=ko → '" + k1.Out + "', 빈 값(config ko) → '" + k2.Out + "', en → '" + e1.Out + "'");

            g.Violations();
            ctx.Check(!g.Log().Contains("Supervisor error"), "hub.log 'Supervisor error'(영어판 감독 오류 줄) 0");
            r.CheckNoProcs("정리");
            real.Verify(ctx, r);
        }

        // ================================================================== lang-no-hangul
        [Scenario("lang-no-hangul", "영어판 글 표본에 한글 없음(L.Use(\"en\"), 계획 §2): 트레이 제목(Supervisor.TitleOf 모든 상태) · 에이징 정보 줄·경과·마지막 출력·터미널 제목 꼬리·종류 이름·기록 끊김 이유 · " +
                                    "AgingSummary.Text · EventKinds.Label·풍선 What · config 검증·파싱 오류 · 제품 프로필 오류 · AgingNames.DefaultName · 폴더·파일 이름 · UiText 기본값. + 용어집 대표 문구 그대로",
                  Order = 1505, TimeoutS = 30)]
        public static void NoHangul(ScenarioContext ctx)
        {
            using var keep = new LangKeep();
            L.Use("en");
            var t = new DateTime(2026, 11, 6, 15, 30, 0);
            var groups = new List<(string Group, List<(string What, string Text)> Items)>();
            List<(string What, string Text)> G(string name) { var l = new List<(string, string)>(); groups.Add((name, l)); return l; }

            // ---- 트레이 제목
            var tray = G("트레이 제목");
            var states = new (string State, bool Crash, bool Warn, int Conn, int Pend, int Conf, int Aging)[]
            {
                ("running", false, false, 3, 0, 0, 1), ("running", false, false, 0, 0, 0, 0), ("running", false, false, 1, 2, 1, 0), ("stopped", false, false, 0, 0, 0, 0),
                ("exited", false, false, 0, 1, 0, 0), ("backoff", false, false, 0, 0, 0, 0), ("starting", false, false, 0, 0, 0, 0), ("waiting_core", false, false, 0, 0, 0, 0),
                ("running", true, false, 3, 0, 0, 1), ("running", false, true, 3, 0, 2, 1),
            };
            foreach (var s in states)
                tray.Add(("TitleOf(" + s.State + (s.Crash ? ",crash" : "") + (s.Warn ? ",cfgwarn" : "") + ")", Supervisor.TitleOf(s.State, s.Crash, s.Warn, s.Conn, s.Pend, s.Conf, s.Aging)));

            // ---- 에이징 글
            var aging = G("에이징 글");
            foreach (var sec in new double[] { 0, 45, 600, 7980, 90000 }) aging.Add(("Duration(" + sec + ")", AgingText.Duration(sec)));
            aging.Add(("RxTail(3,100)", AgingText.RxTail(3, 100))); aging.Add(("RxTail(725,8000)", AgingText.RxTail(725, 8000)));
            aging.Add(("RxTail(null,30)", AgingText.RxTail(null, 30))); aging.Add(("RxTail(null,700)", AgingText.RxTail(null, 700)));
            aging.Add(("MenuLine", AgingText.MenuLine("night", 7980, 3))); aging.Add(("TitleSuffix", AgingText.TitleSuffix));
            aging.Add(("CableRow.AgingLine", new CableRow { Port = 7001, Board = "EVB-A1", Com = "COM3", Status = "attached", SessionName = "night", SessionElapsedS = 7980, SessionLastRxAgeS = 725 }.AgingLine));
            foreach (var k in new[] { "panic", "oops", "bug", "oom", "app_error", "confirm", "missed" }) aging.Add(("KindName(" + k + ")", AgingText.KindName(k)));
            foreach (var why in new[] { "start", "reattach", "detached", null }) aging.Add(("GapWhy(" + (why ?? "null") + ")", AgingSummary.GapWhy(why)));
            aging.Add(("DefaultName(-,-,7001)", AgingNames.DefaultName(null, null, 7001, t))); aging.Add(("DefaultName(-,COM3,7001)", AgingNames.DefaultName(null, "COM3", 7001, t)));
            var se = new AgingSession { Port = 7001, Com = "COM3", Board = "EVB-A1", Name = "night", Folder = @"D:\L\aging\night_20261106-0900", Started = t };
            aging.Add(("AgingSummary.Text", AgingSummary.Text(se, SummaryRecs(t, @"D:\L\aging\night_20261106-0900\events\153000_kernel-panic.txt"), t.AddMinutes(133))));
            aging.Add(("AgingSummary.Text(이벤트 없음)", AgingSummary.Text(se, new List<EventRec>(), t.AddMinutes(5))));

            // ---- 이벤트 이름
            var ev = G("이벤트 이름");
            foreach (var k in EventKinds.All) ev.Add(("Label(" + k + ")", EventKinds.Label(k, k == "boot" ? "hard" : null)));
            ev.Add(("Label(boot,cmd)", EventKinds.Label("boot", "cmd"))); ev.Add(("Label(boot,soft)", EventKinds.Label("boot", "soft")));
            ev.Add(("Label(boot,hard,panic)", EventKinds.Label("boot", "hard", "panic"))); ev.Add(("Label(confirm,of oops)", EventKinds.Label("confirm", null, null, "oops")));
            ev.Add(("What(boot,hard,panic)", EventBalloons.What("boot", "hard", "panic"))); ev.Add(("What(boot,soft)", EventBalloons.What("boot", "soft")));
            ev.Add(("What(confirm,app_error)", EventBalloons.What("confirm", null, null, "app_error"))); ev.Add(("What(app_error)", EventBalloons.What("app_error", null)));

            // ---- config 오류
            List<string> Errs(string json) { try { return Config.Parse(json).Validate(); } catch (ConfigException e) { return e.Errors.ToList(); } }
            var cfg = G("config 오류");
            var v1 = Errs("{\"language\": \"fr\", \"log_layout\": 4, \"base_port\": 80, \"control_port\": 70000, \"ring_chunks\": 1, \"read_buffer_bytes\": 1, \"t_open_ms\": -1, \"restore_tail_bytes\": 1, "
                          + "\"backoff_s\": [0], \"spool_keep_hours\": 0, \"boot_re\": \"\", \"cables\": {\"A\": {\"mode\": \"x\", \"baud\": 0}}, \"events\": {\"patterns\": {\"panic\": {\"off\": [\"NOT A DEFAULT\"]}}}}");
            var v2 = Errs("{\"base_port\": 7001, \"control_port\": 7050, \"backoff_s\": [], \"log_root\": \"\"}");
            var v3 = Errs("{\"bogus\": 1, \"ask_new_cables\": 3, \"auto_nudge\": \"x\", \"on_demand\": 1, \"log_layout\": 1.5, \"cables\": {\"A\": {\"zz\": 1, \"replug_clear\": 2}}, "
                          + "\"events\": {\"enabled\": 1, \"max_mb\": -1, \"notify\": \"panic\", \"zz\": 1, \"patterns\": {\"nope\": {}, \"panic\": {\"add\": \"x\", \"off\": 3, \"qq\": 1}}}}");
            var v4 = Errs("[1]");
            foreach (var (name, list) in new[] { ("검증", v1), ("검증2", v2), ("파싱", v3), ("최상위", v4) })
                for (int i = 0; i < list.Count; i++) cfg.Add((name + "[" + i + "]", list[i]));
            string pe = null, pe2 = null;
            try { ProductProfile.Parse("{\"nam\": 1, \"event_patterns\": {\"warn\": [\"x\"]}, \"boot_re\": \"\", \"app_codes\": [{\"re\": \"a\", \"cod\": \"B\"}, {\"code\": \"C\"}]}"); }
            catch (ProductProfile.ProfileException e) { pe = e.Message; }
            try { ProductProfile.Parse("[1]"); }
            catch (ProductProfile.ProfileException e) { pe2 = e.Message; }
            cfg.Add(("ProductProfile 오류", pe)); cfg.Add(("ProductProfile 최상위", pe2));

            // ---- 이름·UiText
            var names = G("이름·UiText");
            names.Add(("Config.AgingDirName", Config.AgingDirName)); names.Add(("Config.SpoolDirName", Config.SpoolDirName));
            names.Add(("ConsoleNameFor(null)", AgingSession.ConsoleNameFor(null))); names.Add(("EventsDirNameFor(null)", AgingSession.EventsDirNameFor(null)));
            names.Add(("SummaryNameFor(null)", AgingSession.SummaryNameFor(null)));
            var ui = new UiText();
            names.Add(("UiText.Title", ui.Title)); names.Add(("UiText.Cancel", ui.Cancel));

            // ---- 한글 검사(모두 먼저 적고 묶음마다 판정)
            foreach (var (grp, items) in groups)
                foreach (var it in items)
                    if (it.Text == null || HasHangul(it.Text)) ctx.Log("한글(" + grp + "): " + it.What + " → " + (it.Text == null ? "null" : FirstHangulLine(it.Text)));
            ctx.Check(v1.Count >= 10 && v2.Count >= 3 && v3.Count >= 10 && v4.Count == 1 && pe != null && pe2 != null,
                      "config·프로필 오류 표본이 실제로 나옴(검증 " + v1.Count + "·" + v2.Count + ", 파싱 " + v3.Count + ", 최상위 " + v4.Count + ", 프로필 " + (pe != null) + "/" + (pe2 != null) + ")");
            foreach (var (grp, items) in groups)
            {
                var bad = items.Where(it => it.Text == null || HasHangul(it.Text)).ToList();
                ctx.Check(bad.Count == 0, "en: " + grp + " " + items.Count + "개에 한글 없음" + (bad.Count > 0 ? " — " + string.Join(" | ", bad.Take(6).Select(b => b.What + " → " + (b.Text == null ? "null" : FirstHangulLine(b.Text)))) : ""));
            }

            // ---- 용어집 대표 문구(계획 §3)
            ctx.Check(Supervisor.TitleOf("running", false, false, 3, 0, 0, 1) == "UART Hub · 3 connected · Aging 1" && Supervisor.TitleOf("running", false, false, 0, 0, 0) == "UART Hub · No connections"
                      && Supervisor.TitleOf("running", false, false, 1, 2, 0) == "UART Hub · 1 connected · Needs attention 2" && Supervisor.TitleOf("stopped", false, false, 0, 0, 0) == "UART Hub · Stopped"
                      && Supervisor.TitleOf("backoff", false, false, 0, 0, 0) == "UART Hub · Restarting" && Supervisor.TitleOf("starting", false, false, 0, 0, 0) == "UART Hub · Starting"
                      && Supervisor.TitleOf("running", true, false, 3, 0, 0, 1) == "UART Hub · Keeps stopping, check the log",
                      "트레이 제목(en): 'UART Hub · 3 connected · Aging 1' · 'No connections' · 'Needs attention 2' · 'Stopped' · 'Restarting' · 'Starting' · 계속 꺼짐: " + Supervisor.TitleOf("running", false, false, 1, 2, 0));
            ctx.Check(AgingText.Duration(7980) == "2 h 13 min" && AgingText.Duration(45) == "45 s" && AgingText.Duration(90000) == "1 d 1 h" && AgingText.RxTail(3, 100) == "Last output 3 s ago"
                      && AgingText.RxTail(725, 8000) == "No output 12 min" && AgingText.RxTail(null, 30) == "No output yet" && AgingText.MenuLine("night", 7980, 3) == "Aging · night · 2 h 13 min · Last output 3 s ago"
                      && AgingText.TitleSuffix == " · aging" && AgingNames.DefaultName(null, null, 7001, t) == "port7001_20261106-1530" && AgingText.KindName("panic") == "kernel panic" && AgingText.KindName("app_error") == "app error",
                      "에이징 글(en): '2 h 13 min'·'45 s'·'1 d 1 h'·'Last output 3 s ago'·'No output 12 min'(용어집 '출력 없음 N분')·'No output yet'·정보 줄·' · aging'·기본 이름 port7001_…·kernel panic·app error: " + AgingText.MenuLine("night", 7980, 3));
            ctx.Check(EventKinds.Label("app_error", null) == "App error" && EventKinds.Label("boot", "cmd") == "Reboot (command)" && EventKinds.Label("boot", "hard") == "Unexpected reboot (hard)"
                      && EventKinds.Label("confirm", null) == "No response after panic (suspected hang)" && AgingSummary.GapWhy("reattach") == "cable disconnected" && AgingSummary.GapWhy("start") == "hub restarted"
                      && new UiText().Title == "UART Hub" && new UiText().Cancel == "Cancel" && v4[0] == "top level is not an object",
                      "이벤트·끊김·창 기본값(en): App error · Reboot (command) · Unexpected reboot (hard) · No response after panic (suspected hang) · cable disconnected · hub restarted · UART Hub · Cancel");
        }

        // ================================================================== lang-config-edit
        [Scenario("lang-config-edit", "언어 바꾸기의 config.json 글자 편집(Config.WithLanguage — 트레이 설정 ▸ 언어·uart hub language 가 같이 씀): 있는 키 값만 바꿈 · {}·보통 config 에 끼움 · " +
                                      "CRLF·BOM·여는 중괄호 뒤 주석·끝 쉼표 그대로 · 중복 키·객체 값·배열·모르는 값 = null · auto = 키 지움(가운데·마지막·하나뿐·한 줄), 키 없으면 텍스트 그대로, " +
                                      "같은 줄 주석이 붙으면 \"auto\" 를 쓰고 이유 · 결과는 Config.Parse 로 읽히고 다른 최상위 키는 그대로",
                  Order = 1506, TimeoutS = 30)]
        public static void ConfigEdit(ScenarioContext ctx)
        {
            using var keep = new LangKeep();
            L.Use("ko");
            var jo = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            List<string> Others(string t)
            {
                using var d = JsonDocument.Parse(t.TrimStart('\uFEFF'), jo);
                return d.RootElement.EnumerateObject().Where(p => p.Name != "language").Select(p => p.Name + "=" + p.Value.GetRawText()).ToList();
            }
            string Esc(string t) => t == null ? "null" : t.Replace("\uFEFF", "<BOM>").Replace("\r", "\\r").Replace("\n", "\\n");
            // 기대 텍스트와 바이트 그대로 같고, Config.Parse 로 읽히며(언어 = 기대값, 키가 없으면 기본 auto), 다른 최상위 키가 원문과 같아야 한다
            void Edit(string what, string text, string lang, string expect, string expectLang, bool expectWhy = false)
            {
                string n = Config.WithLanguage(text, lang, out string why);
                bool parses = false, same = false;
                string got = null;
                if (n != null)
                    try
                    {
                        got = Config.Parse(n.TrimStart('\uFEFF')).Language;
                        parses = true;
                        same = string.IsNullOrWhiteSpace(text) || Others(text).SequenceEqual(Others(n));
                    }
                    catch (Exception e) { got = e.GetType().Name + ": " + e.Message; }
                ctx.Check(n == expect && parses && got == expectLang && same && (why != null) == expectWhy,
                          what + " [" + lang + "]: " + Esc(text) + " → " + Esc(n) + " (language " + got + ", 다른 키 그대로 " + same + (why != null ? ", 이유 '" + why + "'" : "") + ")"
                          + (n != expect ? " — 기대 " + Esc(expect) : ""));
            }
            void Bad(string what, string text, string lang)
            {
                string n = Config.WithLanguage(text, lang, out string why);
                ctx.Check(n == null && !string.IsNullOrEmpty(why), what + " [" + lang + "] → null + 이유: " + Esc(text) + " (" + why + ")");
            }

            // ---- ko·en: 바꾸기·끼우기
            Edit("있는 키 값만 바꿈", "{\"language\": \"ko\", \"base_port\": 17001}", "en", "{\"language\": \"en\", \"base_port\": 17001}", "en");
            Edit("같은 값이면 텍스트 그대로", "{\"language\": \"en\"}", "EN", "{\"language\": \"en\"}", "en");
            Edit("{} 에 끼움", "{}", "en", "{\"language\": \"en\"}", "en");
            Edit("보통 config 에 끼움(여는 중괄호 뒤 새 줄)", "{\n  \"base_port\": 17001,\n  \"log_layout\": 3\n}\n", "ko", "{\n  \"language\": \"ko\",\n  \"base_port\": 17001,\n  \"log_layout\": 3\n}\n", "ko");
            Edit("파일 없음(null) → 키 하나", null, "en", "{\n  \"language\": \"en\"\n}\n", "en");
            Edit("숫자 값도 바꿈(설정 오류를 고침)", "{\"language\": 1, \"base_port\": 17001}", "ko", "{\"language\": \"ko\", \"base_port\": 17001}", "ko");
            string nest = Config.WithLanguage("{\"x\": {\"language\": \"ko\"}}", "en", out _);
            ctx.Check(nest == "{ \"language\": \"en\",\"x\": {\"language\": \"ko\"}}", "최상위만 봄(중첩된 \"language\" 는 그대로, 최상위에 끼움): " + Esc(nest));
            // ---- 줄 끝·BOM·주석·끝 쉼표
            Edit("CRLF 바꾸기", "{\r\n  \"language\": \"en\",\r\n  \"base_port\": 17001\r\n}\r\n", "ko", "{\r\n  \"language\": \"ko\",\r\n  \"base_port\": 17001\r\n}\r\n", "ko");
            Edit("CRLF 끼움", "{\r\n  \"base_port\": 17001\r\n}\r\n", "en", "{\r\n  \"language\": \"en\",\r\n  \"base_port\": 17001\r\n}\r\n", "en");
            Edit("BOM 그대로(끼움)", "\uFEFF{\"base_port\": 17001}", "en", "\uFEFF{ \"language\": \"en\",\"base_port\": 17001}", "en");
            Edit("여는 중괄호 뒤 주석 그대로", "{ // hub settings\n  \"base_port\": 17001\n}\n", "en", "{ \"language\": \"en\", // hub settings\n  \"base_port\": 17001\n}\n", "en");
            Edit("끝 쉼표 그대로(바꾸기)", "{\"base_port\": 17001, \"language\": \"ko\",}", "en", "{\"base_port\": 17001, \"language\": \"en\",}", "en");
            // ---- 이상한 것 = null
            Bad("중복 키", "{\"language\": \"ko\", \"language\": \"en\"}", "en");
            Bad("중복 키(auto)", "{\"language\": \"ko\", \"base_port\": 17001, \"language\": \"en\"}", "auto");
            Bad("객체 값", "{\"language\": {}}", "en");
            Bad("최상위 배열", "[1]", "en");
            Bad("깨진 JSON", "{\"language\": ", "en");
            Bad("모르는 값", "{}", "fr");
            // ---- auto = 키 지움
            Edit("auto: 가운데 줄 지움", "{\n  \"language\": \"en\",\n  \"base_port\": 17001\n}\n", "auto", "{\n  \"base_port\": 17001\n}\n", "auto");
            Edit("auto: 마지막 항목(앞 쉼표째)", "{\n  \"base_port\": 17001,\n  \"language\": \"en\"\n}\n", "auto", "{\n  \"base_port\": 17001\n}\n", "auto");
            Edit("auto: 하나뿐인 항목", "{\"language\": \"en\"}", "auto", "{}", "auto");
            Edit("auto: 한 줄 첫 항목", "{\"language\": \"en\", \"base_port\": 17001}", "auto", "{\"base_port\": 17001}", "auto");
            Edit("auto: CRLF 줄째로", "{\r\n  \"language\": \"en\",\r\n  \"base_port\": 17001\r\n}\r\n", "auto", "{\r\n  \"base_port\": 17001\r\n}\r\n", "auto");
            Edit("auto: BOM 그대로", "\uFEFF{\"language\": \"en\", \"base_port\": 17001}", "auto", "\uFEFF{\"base_port\": 17001}", "auto");
            Edit("auto: 끝 쉼표", "{\"base_port\": 17001, \"language\": \"ko\",}", "auto", "{\"base_port\": 17001, }", "auto");
            Edit("auto: 앞 줄 주석은 남김", "{\n  // 언어\n  \"language\": \"en\",\n  \"base_port\": 17001\n}\n", "auto", "{\n  // 언어\n  \"base_port\": 17001\n}\n", "auto");
            Edit("auto: 같은 줄 주석 → \"auto\" 를 씀(이유)", "{\n  \"language\": \"en\", // English\n  \"base_port\": 17001\n}\n", "auto",
                 "{\n  \"language\": \"auto\", // English\n  \"base_port\": 17001\n}\n", "auto", expectWhy: true);
            Edit("auto: 마지막 항목 뒤 주석 → \"auto\"(이유)", "{\n  \"base_port\": 17001,\n  \"language\": \"en\" // English\n}\n", "auto",
                 "{\n  \"base_port\": 17001,\n  \"language\": \"auto\" // English\n}\n", "auto", expectWhy: true);
            string noKey = "{\n  \"base_port\": 17001 // 주석\n}\n";
            string same = Config.WithLanguage(noKey, "auto", out string sameWhy);
            ctx.Check(ReferenceEquals(same, noKey) && sameWhy == null, "auto: 키가 없으면 받은 텍스트 그대로(이유 없음): " + Esc(same));
            string none = Config.WithLanguage(null, "auto", out string noneWhy);
            ctx.Check(none == "" && noneWhy == null, "auto: 파일 없음(null) → \"\"(쓸 것 없음): " + Esc(none));
        }

        // ================================================================== lang-switch-e2e
        [Scenario("lang-switch-e2e", "uart hub language(실제 exe, 감독 --no-tray --sim, C-13 격리, 감독·코어·CLI 모두 UART_HUB_LANG 없음 = config.json \"language\" 만): config ko 로 기동(hub.log 한국어) → " +
                                     "hub language en --json = action restarting·setting en·language en, config.json 은 그 값만 바뀜(last-good 도), 코어 재시작 reason=language·케이블 로그 *** hub restart (language) *** · " +
                                     "뒤 CLI 영어('No port is aging')·새 코어 이후 hub.log(sim 줄·경로 빼고) 한글 없음 · 다시 en(비JSON) = 'Hub language unchanged: English (setting en)'·코어 그대로 · " +
                                     "auto = config.json·last-good 에서 키 지움(다른 글자 그대로), action 은 Windows 표시 언어에 따라 · fr·값 없음 = 106. 실제 시스템 무변경",
                  Order = 1507, TimeoutS = 180)]
        public static void SwitchE2e(ScenarioContext ctx)
        {
            using var keep = new LangKeep();
            Environment.SetEnvironmentVariable(L.EnvVar, null);          // 이 프로세스가 띄우는 감독·코어·CLI 모두 환경변수 없이(LangKeep 이 되돌림)
            var real = RealSystem.Capture(ctx);
            const string Cab = "SIMCABLE", Com = "COM761";
            const int Ctl = 18760, Port = 18770;
            var g = IconRig.Create(ctx, "langsw", Ctl, Port, "{\"cable\":\"" + Cab + "\",\"com\":\"" + Com + "\",\"hostname\":\"SIMLANGSW\"}", null, ", \"log_layout\": 3, \"language\": \"ko\"");
            var r = g.R;
            string cfgPath = Path.Combine(g.Hub, "config.json"), lgPath = Path.Combine(g.Hub, "config.last-good.json"), cfg0 = Read(cfgPath);
            Upd.Start(r, TestEnv.UartHubExe, new[] { "sup", "--no-tray", "--hub-dir", g.Hub, "--sim", g.Spec, "--sim-dll", TestEnv.TestsDll, "--tick-ms", "100" }, null);
            int c1 = g.WaitCore("환경변수 없는 감독(config ko) → 코어");
            ctx.WaitUntil(() => g.StatusOf(Cab) == "attached", 20000, Cab + " attached");
            (int Code, JsonElement J, string Out, string Err) Cli(params string[] a)
            {
                var o = r.Run(TestEnv.UartHubExe, new[] { "cli" }.Concat(a).ToArray(), new Dictionary<string, string> { ["UART_HUB_DIR"] = g.Hub }, 60000);
                return (o.Code, Upd.Parse(o.Out), o.Out.Trim(), o.Err.Trim());
            }
            int NewCore(int old, string what) { int p = 0; ctx.WaitUntil(() => (p = g.CorePid()) > 0 && p != old, 20000, what); return p; }
            string exeDir = Path.GetDirectoryName(TestEnv.UartHubExe), dllDir = Path.GetDirectoryName(TestEnv.TestsDll);
            string Plain(string t) => Strip(t, Path.Combine(g.Hub, "임시기록"), r.Root, exeDir, dllDir);   // ko 로 기동할 때 생긴 숨은 임시 기록 폴더는 언어를 바꿔도 그대로 쓴다(경로 = 데이터)
            bool CableLogHas(string what) => Directory.GetFiles(r.Root, "*.log", SearchOption.AllDirectories).Any(f => !f.EndsWith("hub.log", StringComparison.OrdinalIgnoreCase) && Read(f).Contains(what));

            // ① 기동: 환경변수 없음 → config.json "language": "ko"
            string log0 = g.Log();
            var k0 = Cli("session", "status");
            ctx.Check(log0.Contains("감독 시작 pid=") && log0.Contains("코어 시작 pid=") && k0.Code == 0 && k0.Out == "에이징 중인 포트 없음",
                      "환경변수 없음 + config ko: hub.log '감독 시작'·'코어 시작', CLI session status '" + k0.Out + "'");

            // ② hub language en --json → 코어 재시작(사유 language)
            var en = Cli("hub", "language", "en", "--json");
            ctx.Check(en.Code == 0 && Upd.S(en.J, "status") == "ok" && Upd.S(en.J, "action") == "restarting" && Upd.S(en.J, "setting") == "en" && Upd.S(en.J, "language") == "en",
                      "uart hub language en --json → ok·action restarting·setting en·language en: " + en.Out);
            string cfg1 = Read(cfgPath);
            ctx.Check(cfg1 == cfg0.Replace("\"language\": \"ko\"", "\"language\": \"en\""), "config.json 은 \"language\" 값만 ko → en(다른 글자 그대로): " + cfg1.Trim());
            int c2 = NewCore(c1, "language 로 다시 띄운 코어");
            ctx.WaitUntil(() => Regex.IsMatch(g.Log(), @"\[sup\] Core started pid=" + c2 + @" .* reason=language"), 10000, "hub.log '[sup] Core started pid=" + c2 + " … reason=language'");
            string log1 = g.Log();
            ctx.Check(Regex.IsMatch(log1, @"language ko -> en \((core|cli)\)") && log1.Contains("core.stop, language)") && Read(lgPath).Contains("\"language\": \"en\""),
                      "hub.log 'language ko -> en (core)'·'(core.stop, language)' · config.last-good.json 도 en");
            ctx.WaitUntil(() => g.StatusOf(Cab) == "attached", 20000, "다시 띄운 코어에서 " + Cab + " attached");
            ctx.WaitUntil(() => CableLogHas("*** hub restart (language) ***"), 10000, "케이블 로그 '*** hub restart (language) ***'");

            // ③ 뒤로는 영어: CLI(환경변수 없음 = config en) · 새 코어 이후 hub.log
            var e1 = Cli("session", "status");
            ctx.Check(e1.Code == 0 && e1.Out == "No port is aging", "바꾼 뒤 CLI(환경변수 없음, config en) session status → '" + e1.Out + "'");
            var all = g.Log().Replace("\r\n", "\n").Split('\n').ToList();
            int from = all.FindIndex(l => l.Contains("Core started pid=" + c2 + " "));
            var after = all.Skip(Math.Max(0, from)).Where(l => l.Length > 0 && !SimLine.IsMatch(l)).Select(Plain).ToList();
            var leaks = after.Where(HasHangul).ToList();
            ctx.Check(from >= 0 && after.Count >= 2 && leaks.Count == 0,
                      "새 코어 기동 뒤 hub.log " + after.Count + "줄(sim 줄·경로 제외)에 한글 없음" + (leaks.Count > 0 ? " — " + string.Join(" ⏎ ", leaks.Take(6)) : ""));

            // ④ 다시 en(비JSON) → unchanged, 코어 그대로
            var again = Cli("hub", "language", "en");
            ctx.Check(again.Code == 0 && again.Out == "Hub language unchanged: English (setting en)" && g.CorePid() == c2 && Read(cfgPath) == cfg1,
                      "다시 hub language en → '" + again.Out + "', 코어 pid 그대로·config.json 그대로");

            // ⑤ 값 오류 → 106(보내지 않음)
            var fr = Cli("hub", "language", "fr");
            var no = Cli("hub", "language", "--json");
            ctx.Check(fr.Code == 106 && fr.Err.StartsWith("uart: E106 rejected ", StringComparison.Ordinal) && no.Code == 106 && Upd.S(no.J, "status") == "rejected" && Read(cfgPath) == cfg1,
                      "hub language fr → 106 '" + fr.Err + "' · 값 없음 --json → 106 rejected, config.json 그대로");

            // ⑥ auto → 키 지움(config.json·last-good), action 은 Windows 표시 언어에 따라
            string autoLang = L.WindowsIsKorean() ? L.Ko : L.En;
            var au = Cli("hub", "language", "auto", "--json");
            string cfg2 = Read(cfgPath);
            ctx.Check(au.Code == 0 && Upd.S(au.J, "setting") == "auto" && Upd.S(au.J, "language") == autoLang && Upd.S(au.J, "action") == (autoLang == L.En ? "unchanged" : "restarting")
                      && Upd.S(au.J, "note") == null && cfg2 == cfg0.Replace(", \"language\": \"ko\"", "") && !cfg2.Contains("language") && !Read(lgPath).Contains("language"),
                      "hub language auto → setting auto·language " + autoLang + "(Windows 표시 언어)·action " + Upd.S(au.J, "action") + ", config.json·last-good 에 language 키 없음(다른 글자 그대로): " + cfg2.Trim());
            if (autoLang == L.Ko)
            {
                int c3 = NewCore(c2, "auto(Windows 한국어)로 다시 띄운 코어");
                ctx.WaitUntil(() => Regex.IsMatch(g.Log(), @"\[sup\] 코어 시작 pid=" + c3 + @" .* reason=language"), 10000, "hub.log '[sup] 코어 시작 pid=" + c3 + " … reason=language'");
            }

            g.Violations();
            ctx.Check(!g.Log().Contains("Supervisor error"), "hub.log 'Supervisor error'(영어판 감독 오류 줄) 0");
            r.CheckNoProcs("정리");
            real.Verify(ctx, r);
        }
    }
}
