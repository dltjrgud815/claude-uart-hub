// Claude skills 배치(계획 §2.3 g, T4, AC-3 A3-1). 설치 7·업데이트 5(공백 뒤)·복구(Repair)·제거 때 환경마다 ~/.claude/skills/uart/{SKILL.md, uart, .uart_hub}.
//   원본: UartHub.exe 리소스 skill/SKILL.md·skill/uart(app csproj EmbeddedResource), WSL 쪽 작업 스크립트 skills_wsl.sh(Core 리소스).
//   래퍼의 `UART_HUB_EXE_DEFAULT=''` 한 줄에 설치한 exe 의 Windows 경로를 새긴다. 쓰는 파일은 모두 LF 만(CR 0바이트)·UTF-8 BOM 없음.
//   .uart_hub = {"install_id","version","exe","env"} 한 줄 — 설치기가 넣은 폴더라는 표시. 표시 없이 이미 있는 skills/uart 는 사용자가 만든 것으로 보고 건너뛰고 경고한다.
//   Windows: %USERPROFILE%\.claude 가 있을 때만.
//   WSL: wsl.exe -l -q(10초, 표준 입력 닫음, docker-desktop* 제외)로 배포판 이름 → 배포판마다 wsl.exe -d <d> -e sh -c '<skills_wsl.sh>' uart_hub probe|write|remove
//        (배포판당 30초, 파일은 표준 입력으로 base64 줄, uart 는 chmod 755). ~/.claude 가 없으면 건너뜀.
//   업데이트 = install.json skills[] 에 기록된 곳 + 지금 새로 생긴 곳(목록에 있는 배포판·Windows). 제거 = .uart_hub 의 install_id 가 같을 때만.
//   결과는 install.json 의 skills[]({env, distro, path, result, version, note})와 설치 요약에. 실패는 경고만(설치는 계속).
//   시스템 접근(폴더·파일·wsl.exe)은 모두 SystemAccess(C-13)를 거친다 — 시험 모드에서는 <root>\UserProfile 과 가짜 WSL 실행기(<root>\fake_wsl\).
//   X5(부록 B): uart 와 함께 사용자 전용 slash skill ~/.claude/skills/{connect-uart, disconnect-uart}/{SKILL.md, .uart_hub}(같은 표시)를 넣고 지운다.
//     uart 를 넣거나 갱신한 곳에만 넣는다. 표시 없이 이미 있는 그 폴더(사용자가 만든 것)는 그 폴더만 건너뛴다. 결과는 로그 줄(install.json skills[] 는 uart 기준 그대로).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace UartHub
{
    public static class SkillsDeployer
    {
        public const int ListTimeoutMs = 10000, DistroBudgetMs = 30000;
        public const string MarkerName = ".uart_hub", ResSkill = "skill/SKILL.md", ResWrapper = "skill/uart", ResWslScript = "skills_wsl.sh";
        /// <summary>uart 와 함께 넣는 사용자 전용 slash skill 폴더(X5). 리소스 skill/&lt;이름&gt;/SKILL.md.</summary>
        public static readonly string[] Extras = { "connect-uart", "disconnect-uart" };
        const string ExeLine = "UART_HUB_EXE_DEFAULT=";
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        /// <summary>배치 한 곳. Result: installed · updated · skipped · failed · removed · kept(목록을 못 얻어 기록만 유지) · absent(지울 것 없음).</summary>
        public sealed class Place
        {
            public string Env, Distro, Path, Result, Version, Note;
            /// <summary>지금 이곳에 우리 표시가 있다고 보는가(제거·다음 업데이트의 "기록된 곳").</summary>
            public bool Placed => Result == "installed" || Result == "updated" || Result == "kept";
            public string Label => Env == "windows" ? "Windows" : "WSL " + Distro;
            public string Text => Result switch
            {
                "installed" => "넣음",
                "updated" => "갱신",
                "removed" => "지움",
                "kept" => "기록 유지(확인 못 함)",
                "absent" => "지울 것 없음",
                "install" => "넣을 예정",                                         // 아래 셋은 setup 계획(Plan, X7)
                "update" => "갱신 예정",
                "same" => "그대로(같은 표시)",
                "unknown" => "확인 안 함",
                "skipped" => "건너뜀",
                _ => "실패",
            } + (string.IsNullOrEmpty(Note) ? "" : "(" + Note + ")");

            public JObj ToJObj()
            {
                var o = new JObj().Set("env", Env);
                if (Distro != null) o.Set("distro", Distro);
                o.Set("path", Path).Set("result", Result);
                if (Version != null) o.Set("version", Version);
                if (!string.IsNullOrEmpty(Note)) o.Set("note", Note);
                return o;
            }
        }

        /// <summary>wsl.exe -l -q 결과. Ok=false 면 Note 가 "WSL 없음(…)".</summary>
        public sealed class Distros
        {
            public bool Ok;
            public List<string> Names = new List<string>();
            public string Note;
        }

        public static string Summary(IEnumerable<Place> l)
        {
            var a = l.Select(p => p.Label + " " + p.Text).ToList();
            return a.Count == 0 ? "(대상 없음)" : string.Join(", ", a);
        }

        public static string ToJson(IEnumerable<Place> l) => "[" + string.Join(",", l.Select(p => p.ToJObj().ToString())) + "]";

        // ================================================================== 배포판 목록
        /// <summary>wsl.exe -l -q(UTF-16LE, 10초, 표준 입력 닫음). 종료코드≠0·시간 초과는 "WSL 없음"(WSL 없는 Win11 의 기본 wsl.exe 가 설치를 묻고 기다리는 경우 포함).</summary>
        /// running = 켜진 배포판만(wsl -l --running -q — setup 계획이 꺼진 배포판을 깨우지 않게, X7).
        public static Distros ListDistros(bool running = false)
        {
            var r = SystemAccess.Wsl(running ? new[] { "-l", "--running", "-q" } : new[] { "-l", "-q" }, null, ListTimeoutMs);
            var d = new Distros();
            if (r.Code == 0)
            {
                d.Ok = true;
                d.Names = r.OutText.Replace("\r", "").Split('\n').Select(x => x.Trim())
                          .Where(x => x.Length > 0 && !x.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase)).ToList();
                d.Note = d.Names.Count == 0 ? "배포판 없음" : string.Join(", ", d.Names);
            }
            else d.Note = "WSL 없음(rc=" + r.Code + (r.TimedOut ? ", " + (ListTimeoutMs / 1000) + "초 응답 없음" : "") + ")";
            return d;
        }

        // ================================================================== install.json skills[]
        /// <summary>install.json 의 skills[](없거나 깨졌으면 빈 목록).</summary>
        public static List<Place> Recorded(string installJson)
        {
            var l = new List<Place>();
            if (!SystemAccess.FileExists(installJson)) return l;
            var j = HubFiles.ReadJson(SystemAccess.Guard(installJson, "read"));
            if (!j.HasValue || !j.Value.TryGetProperty("skills", out var a) || a.ValueKind != JsonValueKind.Array) return l;
            foreach (var e in a.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var p = new Place
                {
                    Env = HubFiles.Str(e, "env"), Distro = HubFiles.Str(e, "distro"), Path = HubFiles.Str(e, "path"),
                    Result = HubFiles.Str(e, "result"), Version = HubFiles.Str(e, "version"), Note = HubFiles.Str(e, "note"),
                };
                if (p.Env == "windows" || (p.Env == "wsl" && !string.IsNullOrEmpty(p.Distro))) l.Add(p);
            }
            return l;
        }

        // ================================================================== 원본·렌더링
        static string Resource(Assembly asm, string name)
        {
            using var s = asm?.GetManifestResourceStream(name);
            if (s == null) return null;
            using var r = new StreamReader(s, Utf8);
            return Lf(r.ReadToEnd());
        }

        static string Lf(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n");

        /// <summary>래퍼 원본의 `UART_HUB_EXE_DEFAULT=` 줄(정확히 하나)을 exe 경로로 바꾼다(작은따옴표 인용). LF 만.</summary>
        public static string RenderWrapper(string template, string exe)
        {
            var lines = Lf(template).Split('\n');
            int n = lines.Count(l => l.StartsWith(ExeLine, StringComparison.Ordinal));
            if (n != 1) throw new InvalidDataException("래퍼 원본에 " + ExeLine + " 줄이 " + n + "개(1개여야 함)");
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].StartsWith(ExeLine, StringComparison.Ordinal)) lines[i] = ExeLine + "'" + exe.Replace("'", "'\\''") + "'";
            return string.Join("\n", lines);
        }

        public static string MarkerJson(string installId, string version, string exe, string env)
            => new JObj().Set("install_id", installId).Set("version", version).Set("exe", exe).Set("env", env).ToString() + "\n";

        sealed class Sources { public string Skill, Wrapper, Script; public readonly Dictionary<string, string> Extra = new Dictionary<string, string>(StringComparer.Ordinal); }

        static Sources Load(out string why)
        {
            var s = new Sources
            {
                Skill = Resource(Assembly.GetEntryAssembly(), ResSkill),
                Wrapper = Resource(Assembly.GetEntryAssembly(), ResWrapper),
                Script = Resource(typeof(SkillsDeployer).Assembly, ResWslScript),
            };
            why = s.Skill == null || s.Wrapper == null ? "UartHub.exe 리소스에 " + ResSkill + "·" + ResWrapper + " 없음"
                : s.Script == null ? "리소스에 " + ResWslScript + " 없음" : null;
            foreach (var e in Extras)
            {
                string t = Resource(Assembly.GetEntryAssembly(), "skill/" + e + "/SKILL.md");
                if (t != null) s.Extra[e] = t;                                     // 없으면 그 skill 만 빠진다(uart 는 그대로 — 로그에 경고)
            }
            return why == null ? s : null;
        }

        // ================================================================== setup 계획(X7, 읽기만)
        /// <summary>
        /// setup 계획(부록 D.3): 넣을 곳마다 지금 상태를 읽기만 한다 — Windows 는 %USERPROFILE%\.claude, WSL 은 배포판마다 probe(읽기, 30초).
        /// Result: install(넣을 예정) · update(표시의 install_id·version·exe 가 다름 → 갱신 예정) · same(같은 표시 — 그대로) · skipped(.claude 없음·표시 없는 기존 폴더) ·
        /// failed(탐침 실패, 시간 예산 초과 = Note "budget…") · unknown(running 에 없는 꺼진 배포판 — 깨우지 않음). installId 가 null 이면(처음 등록) 표시가 있는 곳은 모두 update.
        /// deadlineUtc: setup 전체 시간 예산 — 넘으면 남은 배포판은 탐침하지 않는다. running: null 이면 모두 탐침.
        /// </summary>
        public static List<Place> Plan(string installId, string version, string exe, Distros distros, Action<string> log, DateTime deadlineUtc, Distros running = null)
        {
            var res = new List<Place>();
            string claude = Path.Combine(SystemAccess.UserProfile, ".claude"), mk = Path.Combine(WinDir, MarkerName);
            var w = new Place { Env = "windows", Path = WinDir };
            if (!SystemAccess.DirExists(claude)) { w.Result = "skipped"; w.Note = ".claude 없음"; }
            else if (SystemAccess.DirExists(WinDir) && !SystemAccess.FileExists(mk)) { w.Result = "skipped"; w.Note = "표시(.uart_hub) 없는 기존 폴더 — 사용자가 만든 것으로 보고 건드리지 않음"; }
            else w.Result = Judge(SystemAccess.ReadText(mk), installId, version, exe);
            res.Add(w);
            string script = Resource(typeof(SkillsDeployer).Assembly, ResWslScript);
            foreach (var d in distros?.Names ?? new List<string>())
            {
                var p = new Place { Env = "wsl", Distro = d };
                int left = (int)(deadlineUtc - DateTime.UtcNow).TotalMilliseconds;
                if (script == null) { p.Result = "failed"; p.Note = "리소스에 " + ResWslScript + " 없음"; res.Add(p); continue; }
                if (running != null && !running.Names.Contains(d, StringComparer.OrdinalIgnoreCase)) { p.Result = "unknown"; p.Note = "꺼진 배포판 — 계획에서는 깨우지 않음"; res.Add(p); continue; }
                if (left < 1000) { p.Result = "failed"; p.Note = BudgetNote; res.Add(p); continue; }
                var pr = SystemAccess.Wsl(Args(d, script, "probe"), null, Math.Min(DistroBudgetMs, left));
                var kv = Kv(pr.OutText);
                if (pr.Code != 0 || !kv.ContainsKey("home")) { p.Result = "failed"; p.Note = "탐침 " + Fail(pr, DistroBudgetMs); }
                else
                {
                    p.Path = kv["home"].TrimEnd('/') + "/.claude/skills/uart";
                    if (kv.GetValueOrDefault("claude") != "1") { p.Result = "skipped"; p.Note = ".claude 없음"; }
                    else if (kv.GetValueOrDefault("dir") == "1" && !kv.ContainsKey("marker")) { p.Result = "skipped"; p.Note = "표시(.uart_hub) 없는 기존 폴더 — 사용자가 만든 것으로 보고 건드리지 않음"; }
                    else p.Result = Judge(kv.GetValueOrDefault("marker"), installId, version, exe);
                }
                res.Add(p);
            }
            foreach (var p in res) log("  skills " + p.Label + ": " + p.Text + (p.Path != null ? " — " + p.Path : ""));
            return res;
        }

        public const string BudgetNote = "budget — setup 시간 예산을 넘어 탐침하지 않음";

        /// <summary>표시(.uart_hub 한 줄)가 없으면 install, install_id·version·exe 가 모두 같으면 same, 아니면 update.</summary>
        static string Judge(string marker, string installId, string version, string exe)
        {
            if (string.IsNullOrWhiteSpace(marker)) return "install";
            try
            {
                using var d = JsonDocument.Parse(marker);
                var e = d.RootElement;
                return e.ValueKind == JsonValueKind.Object && installId != null && HubFiles.Str(e, "install_id") == installId && HubFiles.Str(e, "version") == version
                       && SystemAccess.SamePath(HubFiles.Str(e, "exe"), exe) ? "same" : "update";
            }
            catch (JsonException) { return "update"; }
        }

        // ================================================================== 넣기·갱신
        /// <summary>
        /// 넣기·갱신. distros = 사전 점검에서 얻은 배포판 목록(null 이면 여기서 -l -q). recorded = install.json 의 skills[](업데이트·복구 — 기록된 곳 + 새로 생긴 곳).
        /// 결과 목록(Windows 1개 + 배포판마다 1개). 실패는 결과에 남기고 예외를 던지지 않는다(TestIsolationViolation 제외).
        /// </summary>
        /// plan(X7 setup): 같은 실행에서 Plan 이 이미 탐침한 결과 — 있으면 배포판을 다시 탐침하지 않는다(skipped·failed·unknown 은 그대로, 나머지는 바로 쓰기).
        /// deadlineUtc: 넘으면 남은 배포판은 쓰지 않고 failed(budget).
        public static List<Place> Deploy(string installId, string version, string exe, Distros distros, IList<Place> recorded, Action<string> log,
                                         IList<Place> plan = null, DateTime? deadlineUtc = null)
        {
            var res = new List<Place>();
            var src = Load(out string why);
            if (src == null)
            {
                log("  경고: skills 원본 없음(" + why + ") — 건너뜀");
                res.Add(new Place { Env = "windows", Path = WinDir, Result = "failed", Note = why });
                return res;
            }
            string uart = RenderWrapper(src.Wrapper, exe);

            foreach (var e in Extras.Where(x => !src.Extra.ContainsKey(x))) log("  경고: skills 원본에 " + e + " 없음 — 그 skill 은 건너뜀");
            var winPlace = DeployWindows(src.Skill, uart, MarkerJson(installId, version, exe, "windows"), version, log);
            res.Add(winPlace);
            if (winPlace.Result == "installed" || winPlace.Result == "updated") DeployWindowsExtras(src, MarkerJson(installId, version, exe, "windows"), log);

            distros ??= ListDistros();
            log("  WSL 배포판: " + distros.Note);
            var names = new List<string>(distros.Names);
            foreach (var p in recorded ?? new List<Place>())
            {
                if (p.Env != "wsl" || names.Contains(p.Distro, StringComparer.OrdinalIgnoreCase) || res.Any(x => x.Env == "wsl" && string.Equals(x.Distro, p.Distro, StringComparison.OrdinalIgnoreCase))) continue;
                Place q = !distros.Ok
                    ? new Place { Env = "wsl", Distro = p.Distro, Path = p.Path, Version = p.Version, Result = p.Placed ? "kept" : p.Result, Note = "배포판 목록을 얻지 못함 — 이전 기록 유지" }
                    : new Place { Env = "wsl", Distro = p.Distro, Path = p.Path, Result = "skipped", Note = "배포판 목록에 없음(지워졌거나 이름이 바뀜)" };
                log("  skills " + q.Label + ": " + q.Text);
                res.Add(q);
            }
            string wslMarker = MarkerJson(installId, version, exe, "wsl");
            var pl = new StringBuilder();
            pl.Append("SKILL.md 644 ").Append(Convert.ToBase64String(Utf8.GetBytes(src.Skill))).Append('\n')
              .Append("uart 755 ").Append(Convert.ToBase64String(Utf8.GetBytes(uart))).Append('\n')
              .Append(".uart_hub 644 ").Append(Convert.ToBase64String(Utf8.GetBytes(wslMarker))).Append('\n');
            foreach (var kv in src.Extra)
                pl.Append(kv.Key).Append("/SKILL.md 644 ").Append(Convert.ToBase64String(Utf8.GetBytes(kv.Value))).Append('\n')
                  .Append(kv.Key).Append("/.uart_hub 644 ").Append(Convert.ToBase64String(Utf8.GetBytes(wslMarker))).Append('\n');
            byte[] payload = Utf8.GetBytes(pl.ToString());
            foreach (var d in names)
            {
                var pp = plan?.FirstOrDefault(x => x.Env == "wsl" && string.Equals(x.Distro, d, StringComparison.OrdinalIgnoreCase));
                int left = deadlineUtc == null ? DistroBudgetMs : (int)(deadlineUtc.Value - DateTime.UtcNow).TotalMilliseconds;
                if (pp != null && (pp.Result == "skipped" || pp.Result == "failed" || pp.Result == "unknown"))
                {
                    var q = new Place { Env = "wsl", Distro = d, Path = pp.Path, Result = pp.Result == "unknown" ? "failed" : pp.Result, Note = pp.Note ?? "계획 탐침 결과" };
                    log("  skills " + q.Label + ": " + q.Text + (q.Path != null ? " — " + q.Path : "") + " (계획 탐침 결과 그대로)");
                    res.Add(q);
                }
                else if (left < 1000)
                {
                    var q = new Place { Env = "wsl", Distro = d, Path = pp?.Path, Result = "failed", Note = BudgetNote };
                    log("  skills " + q.Label + ": " + q.Text);
                    res.Add(q);
                }
                else res.Add(DeployWsl(d, src.Script, payload, version, log, pp?.Path, Math.Min(DistroBudgetMs, left)));
            }
            return res;
        }

        static string WinDir => Path.Combine(SystemAccess.UserProfile, ".claude", "skills", "uart");

        static Place DeployWindows(string skill, string uart, string marker, string version, Action<string> log)
        {
            string claude = Path.Combine(SystemAccess.UserProfile, ".claude"), dir = WinDir, mk = Path.Combine(dir, MarkerName);
            var p = new Place { Env = "windows", Path = dir };
            try
            {
                if (!SystemAccess.DirExists(claude)) { p.Result = "skipped"; p.Note = ".claude 없음"; }
                else if (SystemAccess.DirExists(dir) && !SystemAccess.FileExists(mk))
                {
                    p.Result = "skipped"; p.Note = "표시(.uart_hub) 없는 기존 폴더 — 사용자가 만든 것으로 보고 건드리지 않음";
                    log("  경고: " + dir + " 에 .uart_hub 표시가 없음 — 사용자가 만든 skills 로 보고 건너뜀");
                }
                else
                {
                    bool had = SystemAccess.FileExists(mk);
                    SystemAccess.CreateDir(dir);
                    SystemAccess.WriteText(mk, marker);                                  // 표시 먼저(중간에 끊겨도 다음 실행이 자기 폴더로 알아봄)
                    SystemAccess.WriteText(Path.Combine(dir, "SKILL.md"), skill);
                    SystemAccess.WriteText(Path.Combine(dir, "uart"), uart);
                    p.Result = had ? "updated" : "installed";
                    p.Version = version;
                }
            }
            catch (TestIsolationViolation) { throw; }
            catch (Exception e) { p.Result = "failed"; p.Note = e.GetType().Name + ": " + e.Message; }
            log("  skills " + p.Label + ": " + p.Text + " — " + dir);
            return p;
        }

        static string SkillsRoot => Path.Combine(SystemAccess.UserProfile, ".claude", "skills");

        static string ExtrasText(IEnumerable<string> parts)
        {
            var l = parts.ToList();
            return l.Count == 0 ? null : string.Join(", ", l.Select(p =>
            {
                var kv = p.Split(':');
                string r = kv.Length > 1 ? kv[1] : "";
                return kv[0] + " " + (r == "installed" ? "넣음" : r == "updated" ? "갱신" : r == "skipped" ? "건너뜀(표시 없는 기존 폴더)" : r == "removed" ? "지움" : "실패" + (kv.Length > 2 ? "(" + kv[2] + ")" : ""));
            }));
        }

        /// <summary>Windows 의 connect-uart·disconnect-uart(X5): 폴더마다 표시(.uart_hub) 먼저 → SKILL.md. 표시 없이 이미 있는 폴더는 건너뛴다.</summary>
        static void DeployWindowsExtras(Sources src, string marker, Action<string> log)
        {
            var parts = new List<string>();
            foreach (var kv in src.Extra)
            {
                string dir = Path.Combine(SkillsRoot, kv.Key), mk = Path.Combine(dir, MarkerName);
                try
                {
                    if (SystemAccess.DirExists(dir) && !SystemAccess.FileExists(mk)) { parts.Add(kv.Key + ":skipped"); log("  경고: " + dir + " 에 .uart_hub 표시가 없음 — 사용자가 만든 skill 로 보고 건너뜀"); continue; }
                    bool had = SystemAccess.FileExists(mk);
                    SystemAccess.CreateDir(dir);
                    SystemAccess.WriteText(mk, marker);
                    SystemAccess.WriteText(Path.Combine(dir, "SKILL.md"), kv.Value);
                    parts.Add(kv.Key + ":" + (had ? "updated" : "installed"));
                }
                catch (TestIsolationViolation) { throw; }
                catch (Exception e) { parts.Add(kv.Key + ":failed:" + e.GetType().Name); }
            }
            if (parts.Count > 0) log("  skills Windows: " + ExtrasText(parts) + " — " + SkillsRoot);
        }

        /// <summary>Windows 의 connect-uart·disconnect-uart 지우기: 표시의 install_id 가 같을 때만 두 파일을 지우고 폴더가 비면 폴더도.</summary>
        static void RemoveWindowsExtras(string installId, Action<string> log)
        {
            var parts = new List<string>();
            foreach (var e in Extras)
            {
                string dir = Path.Combine(SkillsRoot, e), mk = Path.Combine(dir, MarkerName);
                try
                {
                    if (!SystemAccess.FileExists(mk)) continue;
                    var j = HubFiles.ReadJson(SystemAccess.Guard(mk, "read"));
                    if (!j.HasValue || HubFiles.Str(j.Value, "install_id") != installId) continue;
                    foreach (var f in new[] { "SKILL.md", MarkerName }) SystemAccess.DeleteFile(Path.Combine(dir, f));
                    if (SystemAccess.ListFiles(dir, "*").Length == 0 && SystemAccess.ListDirs(dir).Length == 0) SystemAccess.DeleteDir(dir);
                    parts.Add(e + ":removed");
                }
                catch (TestIsolationViolation) { throw; }
                catch (Exception x) { parts.Add(e + ":failed:" + x.GetType().Name); }
            }
            if (parts.Count > 0) log("  skills Windows: " + ExtrasText(parts) + " — " + SkillsRoot);
        }

        static string[] Args(string distro, string script, params string[] op)
            => new[] { "-d", distro, "-e", "sh", "-c", script, "uart_hub" }.Concat(op).ToArray();

        static Dictionary<string, string> Kv(string text)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var l in text.Replace("\r", "").Split('\n'))
            {
                int i = l.IndexOf('=');
                if (i > 0 && !d.ContainsKey(l.Substring(0, i))) d[l.Substring(0, i)] = l.Substring(i + 1);
            }
            return d;
        }

        static string Fail(ExtResult r, int budgetMs)
            => r.TimedOut ? (budgetMs / 1000) + "초 안에 응답 없음" : "rc=" + r.Code + (string.IsNullOrWhiteSpace(r.Err) ? "" : " " + Trunc(r.Err.Trim(), 160));

        static string Trunc(string s, int n) => s.Length > n ? s.Substring(0, n) + "…" : s;

        /// <summary>배포판 하나: probe(읽기) → 판정 → write(표준 입력으로 파일). 합쳐 30초 안에.</summary>
        /// probedPath(X7): 같은 실행의 계획이 이미 탐침해 쓸 곳이 맞다고 본 경로 — 있으면 탐침하지 않고 바로 쓴다(쓰기 스크립트가 .claude 없음·표시 없는 폴더를 다시 막는다).
        static Place DeployWsl(string distro, string script, byte[] payload, string version, Action<string> log, string probedPath = null, int budgetMs = DistroBudgetMs)
        {
            var p = new Place { Env = "wsl", Distro = distro };
            var sw = Stopwatch.StartNew();
            int Left() => Math.Max(1000, budgetMs - (int)sw.ElapsedMilliseconds);
            try
            {
                var pr = probedPath != null ? null : SystemAccess.Wsl(Args(distro, script, "probe"), null, budgetMs);
                var kv = pr != null ? Kv(pr.OutText) : new Dictionary<string, string> { ["home"] = probedPath.EndsWith("/.claude/skills/uart", StringComparison.Ordinal) ? probedPath.Substring(0, probedPath.Length - 20) : probedPath, ["claude"] = "1" };
                if (pr != null && (pr.Code != 0 || !kv.ContainsKey("home"))) { p.Result = "failed"; p.Note = "탐침 " + Fail(pr, budgetMs); }
                else
                {
                    p.Path = kv["home"].TrimEnd('/') + "/.claude/skills/uart";
                    if (kv.GetValueOrDefault("claude") != "1") { p.Result = "skipped"; p.Note = ".claude 없음"; }
                    else if (kv.GetValueOrDefault("dir") == "1" && !kv.ContainsKey("marker"))
                    {
                        p.Result = "skipped"; p.Note = "표시(.uart_hub) 없는 기존 폴더 — 사용자가 만든 것으로 보고 건드리지 않음";
                        log("  경고: WSL " + distro + " " + p.Path + " 에 .uart_hub 표시가 없음 — 사용자가 만든 skills 로 보고 건너뜀");
                    }
                    else if (sw.ElapsedMilliseconds >= budgetMs) { p.Result = "failed"; p.Note = "탐침에 " + (budgetMs / 1000) + "초를 다 씀"; }
                    else
                    {
                        int budget = Left();
                        var wr = SystemAccess.Wsl(Args(distro, script, "write"), payload, budget);
                        var w = Kv(wr.OutText);
                        string ok = w.GetValueOrDefault("ok");
                        if (wr.Code == 0 && (ok == "installed" || ok == "updated"))
                        {
                            p.Result = ok; p.Version = version;
                            if (w.TryGetValue("path", out var path) && path.Length > 0) p.Path = path;
                            if (w.TryGetValue("extras", out var ex) && ex.Length > 0) log("  skills WSL " + distro + ": " + ExtrasText(ex.Split(',')));
                        }
                        else if (w.GetValueOrDefault("err") == "foreign") { p.Result = "skipped"; p.Note = "표시 없는 기존 폴더(쓰기 직전 확인)"; }
                        else if (w.GetValueOrDefault("err") == "no_claude") { p.Result = "skipped"; p.Note = ".claude 없음"; }
                        else { p.Result = "failed"; p.Note = "쓰기 " + (w.TryGetValue("err", out var err) ? "err=" + err : Fail(wr, budget)); }
                    }
                }
            }
            catch (TestIsolationViolation) { throw; }
            catch (Exception e) { p.Result = "failed"; p.Note = e.GetType().Name + ": " + e.Message; }
            log("  skills " + p.Label + ": " + p.Text + (p.Path != null ? " — " + p.Path : "") + " (" + sw.ElapsedMilliseconds + "ms)");
            return p;
        }

        // ================================================================== 제거
        /// <summary>
        /// 제거: Windows 기본 위치 + install.json 에 기록된(우리 표시가 있다고 본) WSL 배포판. .uart_hub 의 install_id 가 installId 일 때만 세 파일을 지우고
        /// 폴더가 비면 폴더도 지운다. 표시가 없거나 다른 설치의 표시면 남긴다.
        /// </summary>
        public static List<Place> Remove(string installId, IList<Place> recorded, Action<string> log)
        {
            var res = new List<Place>();
            if (string.IsNullOrEmpty(installId)) { log("  install_id 모름(install.json 없음) — skills 는 지우지 않음"); return res; }
            res.Add(RemoveWindows(installId, log));
            RemoveWindowsExtras(installId, log);
            var src = Load(out string why);
            foreach (var p in (recorded ?? new List<Place>()).Where(x => x.Env == "wsl" && x.Placed)
                                                               .GroupBy(x => x.Distro, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
            {
                var q = new Place { Env = "wsl", Distro = p.Distro, Path = p.Path };
                var sw = Stopwatch.StartNew();
                try
                {
                    if (src == null) { q.Result = "failed"; q.Note = why; }
                    else
                    {
                        var r = SystemAccess.Wsl(Args(p.Distro, src.Script, "remove", installId), null, DistroBudgetMs);
                        var kv = Kv(r.OutText);
                        string ok = kv.GetValueOrDefault("ok"), err = kv.GetValueOrDefault("err");
                        if (kv.TryGetValue("extras_removed", out var xr) && xr.Length > 0) log("  skills WSL " + p.Distro + ": " + ExtrasText(xr.Split(',').Select(x => x + ":removed")));
                        if (r.Code == 0 && (ok == "removed" || ok == "removed_files")) { q.Result = "removed"; q.Note = ok == "removed_files" ? "다른 파일이 있어 폴더는 남김" : null; }
                        else if (r.Code == 0 && ok == "absent") q.Result = "absent";
                        else if (err == "other_install") { q.Result = "skipped"; q.Note = "다른 설치의 표시(install_id 다름) — 남김"; }
                        else { q.Result = "failed"; q.Note = err != null ? "err=" + err : Fail(r, DistroBudgetMs); }
                    }
                }
                catch (TestIsolationViolation) { throw; }
                catch (Exception e) { q.Result = "failed"; q.Note = e.GetType().Name + ": " + e.Message; }
                log("  skills " + q.Label + ": " + q.Text + (q.Path != null ? " — " + q.Path : "") + " (" + sw.ElapsedMilliseconds + "ms)");
                res.Add(q);
            }
            return res;
        }

        static Place RemoveWindows(string installId, Action<string> log)
        {
            string dir = WinDir, mk = Path.Combine(dir, MarkerName);
            var p = new Place { Env = "windows", Path = dir };
            try
            {
                if (!SystemAccess.DirExists(dir)) p.Result = "absent";
                else if (!SystemAccess.FileExists(mk)) { p.Result = "skipped"; p.Note = "표시(.uart_hub) 없음 — 사용자가 만든 것으로 보고 남김"; }
                else
                {
                    string id = null;
                    var j = HubFiles.ReadJson(SystemAccess.Guard(mk, "read"));
                    if (j.HasValue) id = HubFiles.Str(j.Value, "install_id");
                    if (id != installId) { p.Result = "skipped"; p.Note = "다른 설치의 표시(install_id 다름) — 남김"; }
                    else
                    {
                        foreach (var f in new[] { "SKILL.md", "uart", MarkerName }) SystemAccess.DeleteFile(Path.Combine(dir, f));
                        if (SystemAccess.ListFiles(dir, "*").Length == 0 && SystemAccess.ListDirs(dir).Length == 0) { SystemAccess.DeleteDir(dir); p.Result = "removed"; }
                        else { p.Result = "removed"; p.Note = "다른 파일이 있어 폴더는 남김"; }
                    }
                }
            }
            catch (TestIsolationViolation) { throw; }
            catch (Exception e) { p.Result = "failed"; p.Note = e.GetType().Name + ": " + e.Message; }
            log("  skills " + p.Label + ": " + p.Text + " — " + dir);
            return p;
        }
    }
}
