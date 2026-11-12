// 터미널 탐지·실행 인자·이미 열린 창 찾기(계획 §2.3 f, T3). open(OpenCmd)·트레이 '터미널 변경'이 쓴다.
//   우선순위 TeraTerm → PuTTY → MobaXterm. 경로 후보: 기본 설치 경로(install.ps1:93-101 + PuTTY·MobaXterm 기본 경로) → App Paths(읽기만) → PATH.
//   인자(§2.3 f 후보 1 — K5 에서 사용자가 확정):
//     TeraTerm  127.0.0.1:<p> /nossh /T=1 /W="<제목>"
//     PuTTY     -telnet 127.0.0.1 -P <p>            (창이 뜨면 SetWindowText "<제목> - PuTTY")
//     <제목> = 트레이 포트 줄과 같은 "<보드> · <COM> · <p>"(모르는 칸은 뺌). 보드명이 나중에 잡히면 감독이 띄운 창 제목을 바꾼다(TermTitler).
//     MobaXterm -newtab "telnet 127.0.0.1 <p>"      (찾지 않음 — 열 때마다 새 탭)
//   이미 열린 창: TeraTerm 은 명령줄에 127.0.0.1:<p>, PuTTY 는 -P <p>(+127.0.0.1)인 프로세스(접속 대상으로 찾는다 — 창 제목은 바뀌므로 보지 않는다).
//   PuTTY 제목에 (inactive)(끊긴 창)면 새로 연다.
//   찾아보기: 파일 이름 ttermpro*·putty*·MobaXterm*(.exe)만 받는다.
//   prefs.json(%LOCALAPPDATA%\uart_hub, C-12 왕복 보존): {"terminal":"auto|teraterm|putty|mobaxterm","teraterm":경로,"putty":경로,"mobaxterm":경로,…}
// 시스템 접근(파일 존재·레지스트리·PATH·프로세스)은 모두 SystemAccess(C-13). 인자·판정 함수(Args·KindFromFileName·FindOpen)는 시스템에 닿지 않는다.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UartHub
{
    public sealed class TermChoice
    {
        public string Kind, Path, Source;
        public override string ToString() => Terminals.Label(Kind) + " " + Path + " (" + Source + ")";
    }

    /// <summary>prefs.json: 터미널 선택(auto 또는 지정) + 종류별 경로. 모르는 키는 그대로 다시 쓴다(C-12).</summary>
    public sealed class TermPrefs
    {
        public string Terminal = "auto";
        public readonly Dictionary<string, string> Paths = new Dictionary<string, string>(StringComparer.Ordinal);
        JObj raw = new JObj();

        public static string FilePath => System.IO.Path.Combine(SystemAccess.InstallDir, "prefs.json");

        public static TermPrefs Load()
        {
            var p = new TermPrefs();
            string t = SystemAccess.ReadText(FilePath);
            if (string.IsNullOrWhiteSpace(t)) return p;
            try
            {
                using var d = JsonDocument.Parse(t);
                if (d.RootElement.ValueKind != JsonValueKind.Object) return p;
                p.raw = HubFiles.ToJObj(d.RootElement);
                string term = HubFiles.Str(d.RootElement, "terminal");
                if (term == "auto" || Terminals.Kinds.Contains(term)) p.Terminal = term;
                foreach (var k in Terminals.Kinds)
                {
                    string v = HubFiles.Str(d.RootElement, k);
                    if (!string.IsNullOrWhiteSpace(v)) p.Paths[k] = v;
                }
            }
            catch (JsonException) { }
            return p;
        }

        public void Save()
        {
            raw.Set("terminal", Terminal);
            foreach (var k in Terminals.Kinds)
                if (Paths.TryGetValue(k, out var v)) raw.Set(k, v);
            SystemAccess.CreateDir(SystemAccess.InstallDir);
            SystemAccess.WriteText(FilePath, raw.ToString() + "\n");
        }
    }

    public static class Terminals
    {
        /// <summary>종류(우선순위 순).</summary>
        public static readonly string[] Kinds = { "teraterm", "putty", "mobaxterm" };

        public static string ExeName(string kind) => kind switch { "teraterm" => "ttermpro.exe", "putty" => "putty.exe", "mobaxterm" => "MobaXterm.exe", _ => null };
        public static string Label(string kind) => kind switch { "teraterm" => "TeraTerm", "putty" => "PuTTY", "mobaxterm" => "MobaXterm", _ => kind };

        static IEnumerable<string> DefaultPaths(string kind)
        {
            string pf = SystemAccess.ProgramFiles, x86 = SystemAccess.ProgramFilesX86;
            switch (kind)
            {
                case "teraterm":
                    return new[] { Path.Combine(x86, "teraterm5", "ttermpro.exe"), Path.Combine(x86, "teraterm", "ttermpro.exe"),
                                   Path.Combine(pf, "teraterm5", "ttermpro.exe"), Path.Combine(pf, "teraterm", "ttermpro.exe") };
                case "putty":
                    return new[] { Path.Combine(pf, "PuTTY", "putty.exe"), Path.Combine(x86, "PuTTY", "putty.exe") };
                case "mobaxterm":
                    return new[] { Path.Combine(x86, "Mobatek", "MobaXterm", "MobaXterm.exe"), Path.Combine(pf, "Mobatek", "MobaXterm", "MobaXterm.exe") };
                default: return new string[0];
            }
        }

        static bool Exists(string p)
        {
            try { return !string.IsNullOrWhiteSpace(p) && SystemAccess.FileExists(p); }
            catch (TestIsolationViolation) { throw; }
            catch (Exception) { return false; }                     // PATH 의 잘못된 항목 등
        }

        /// <summary>kind 의 exe: 기본 경로 → App Paths → PATH. 없으면 null.</summary>
        public static TermChoice Find(string kind)
        {
            foreach (var p in DefaultPaths(kind))
                if (Exists(p)) return new TermChoice { Kind = kind, Path = p, Source = "기본 경로" };
            string ap = SystemAccess.AppPath(ExeName(kind));
            if (Exists(ap)) return new TermChoice { Kind = kind, Path = ap, Source = "App Paths" };
            foreach (var d in SystemAccess.PathDirs())
            {
                string p;
                try { p = Path.Combine(d, ExeName(kind)); } catch (Exception) { continue; }
                if (Exists(p)) return new TermChoice { Kind = kind, Path = p, Source = "PATH" };
            }
            return null;
        }

        /// <summary>찾은 터미널 전부(우선순위 순).</summary>
        public static List<TermChoice> DetectAll() => Kinds.Select(Find).Where(x => x != null).ToList();

        /// <summary>
        /// prefs → 쓸 터미널. auto: 우선순위로 처음 찾은 것. 지정: prefs 의 그 종류 경로(있으면) → 그 종류 탐지 → 없으면 auto 로 바꿔 고른다(note 에 이유).
        /// 하나도 없으면 null.
        /// </summary>
        public static TermChoice Pick(TermPrefs prefs, out string note)
        {
            note = null;
            if (prefs != null && prefs.Terminal != "auto" && Kinds.Contains(prefs.Terminal))
            {
                string k = prefs.Terminal;
                if (prefs.Paths.TryGetValue(k, out var path) && Exists(path)) return new TermChoice { Kind = k, Path = path, Source = "prefs.json" };
                var f = Find(k);
                if (f != null) return f;
                note = "prefs.json 의 " + Label(k) + " 를 찾지 못함 → 자동 선택";
            }
            foreach (var k in Kinds)
            {
                var f = Find(k);
                if (f != null) return f;
            }
            return null;
        }

        /// <summary>찾아보기로 고른 파일의 종류(파일 이름 ttermpro*·putty*·MobaXterm*, 확장자 .exe). 그 밖은 null(지원하지 않는 터미널).</summary>
        public static string KindFromFileName(string path)
        {
            string f = Path.GetFileName(path ?? "");
            if (!f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
            if (f.StartsWith("ttermpro", StringComparison.OrdinalIgnoreCase)) return "teraterm";
            if (f.StartsWith("putty", StringComparison.OrdinalIgnoreCase)) return "putty";
            if (f.StartsWith("MobaXterm", StringComparison.OrdinalIgnoreCase)) return "mobaxterm";
            return null;
        }

        /// <summary>
        /// 터미널 창 제목(트레이 메뉴의 포트 줄과 같은 모양): "&lt;보드&gt; · &lt;COM&gt; · &lt;p&gt;", 모르는 칸은 뺀다("COM3 · 7001", "7001").
        /// 큰따옴표·제어 문자는 바꾼다(TeraTerm /W="…" 를 깨지 않게). 바탕화면 아이콘 이름("&lt;보드&gt; (&lt;p&gt;)")과는 따로다.
        /// </summary>
        public static string Title(string board, string com, int port)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(board)) parts.Add(board.Trim());
            if (!string.IsNullOrWhiteSpace(com)) parts.Add(com.Trim());
            parts.Add(port.ToString(CultureInfo.InvariantCulture));
            return Regex.Replace(string.Join(" · ", parts).Replace('"', '\''), @"[\x00-\x1f]", "_");
        }

        /// <summary>그 종류 창에 실제로 쓰는 제목. PuTTY 는 " - PuTTY" 를 붙인다(PuTTY 는 명령줄로 제목을 받지 못해 실행 뒤 SetWindowText 로 쓴다).</summary>
        public static string WindowTitle(string kind, string title) => kind == "putty" ? title + " - PuTTY" : title;

        /// <summary>실행 인자(한 문자열, 그대로 CreateProcess 명령줄에 붙는다). title = Title(…) 결과(TeraTerm /W=).</summary>
        public static string Args(string kind, int port, string title)
        {
            string p = port.ToString(CultureInfo.InvariantCulture);
            return kind switch
            {
                "teraterm" => "127.0.0.1:" + p + " /nossh /T=1 /W=\"" + title + "\"",
                "putty" => "-telnet 127.0.0.1 -P " + p,
                "mobaxterm" => "-newtab \"telnet 127.0.0.1 " + p + "\"",
                _ => throw new ArgumentException("터미널 종류: " + kind),
            };
        }

        /// <summary>이미 열린 창을 찾을 프로세스 이름(MobaXterm 은 찾지 않음 — 늘 새 탭).</summary>
        public static string[] ProcessNames(string kind) => kind switch { "teraterm" => new[] { "ttermpro.exe" }, "putty" => new[] { "putty.exe" }, _ => new string[0] };

        /// <summary>
        /// 그 포트로 이미 열린 터미널 프로세스(앞으로 가져올 대상). TeraTerm: 명령줄에 127.0.0.1:&lt;p&gt;. PuTTY: 명령줄에 -P &lt;p&gt;·127.0.0.1, 제목에 (inactive) 면 건너뜀.
        /// MobaXterm: 항상 null. skipped = 건너뛴 이유(끊긴 PuTTY 등, 없으면 null).
        /// </summary>
        public static ProcRow FindOpen(string kind, int port, IEnumerable<ProcRow> procs, out string skipped)
        {
            skipped = null;
            string p = port.ToString(CultureInfo.InvariantCulture);
            Regex re = kind switch
            {
                "teraterm" => new Regex(@"127\.0\.0\.1:" + p + @"(?!\d)"),
                "putty" => new Regex(@"(^|\s)-P\s+" + p + @"(?!\d)"),
                _ => null,
            };
            if (re == null || procs == null) return null;
            var names = ProcessNames(kind);
            foreach (var r in procs)
            {
                if (r == null || !names.Any(n => string.Equals(n, r.Name, StringComparison.OrdinalIgnoreCase))) continue;
                string cmd = r.CommandLine ?? "";
                if (!re.IsMatch(cmd)) continue;
                if (kind == "putty")
                {
                    if (cmd.IndexOf("127.0.0.1", StringComparison.Ordinal) < 0) continue;
                    if ((r.Title ?? "").IndexOf("(inactive)", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        skipped = "끊긴 PuTTY 창 pid=" + r.Pid + " '" + r.Title + "' — 새로 엶";
                        continue;
                    }
                }
                return r;
            }
            return null;
        }
    }
}
