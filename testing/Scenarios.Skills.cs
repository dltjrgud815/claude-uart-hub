// US-109(계획 T4 Claude 연동, AC-3·NF-3) 시나리오: cli-b64nul · skills-render · skills-deploy · install-update-skills.
//   cli-b64nul 은 LifeRig(감독 --no-tray --sim) 위에서 UartHub.exe cli 를 직접 부르고, 나머지는 InstallRig(C-13 격리: 가짜 USERPROFILE·가짜 WSL 실행기
//   <root>\fake_wsl\ — 배포판 홈 fake_wsl\home\<d>, 탐침 지연 <d>.delay_ms, 쓴 파일·모드 ops.log) 아래에서 실제 exe 로 설치·업데이트·제거한다.
//   끝마다 "실제 시스템 무변경"(RealSystem — 실제 %USERPROFILE%\.claude\skills 포함) + 실제 wsl.exe 호출 0 + TestIsolationViolation 0.
//   실제 WSL ~/.claude/skills/uart 는 tests/run_sim.sh 가 실행 전후 해시로 따로 본다.
// 포트: cli-b64nul 18100/18110 · skills-render 18120/18122 · skills-deploy 18130/18132·18140/18142·18144/18146 · install-update-skills 18150/18160
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace UartHub.Testing
{
    public static class SkillsScenarios
    {
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        static string Dev => TestEnv.UartHubExe;

        /// <summary>printf '%s\0' "$@" | base64 | tr '+/' '-_' | tr -d '=' 와 같은 토큰.</summary>
        static string B64Nul(params string[] argv)
            => Convert.ToBase64String(Utf8.GetBytes(string.Concat(argv.Select(a => a + "\0")))).Replace('+', '-').Replace('/', '_').TrimEnd('=');

        static string B64Req(params string[] argv) => Convert.ToBase64String(Utf8.GetBytes("[" + string.Join(",", argv.Select(Json.Str)) + "]"));

        /// <summary>UartHub.exe cli(UART_HUB_DIR = 허브 폴더). holdStdin 이면 표준 입력을 파이프로 쥐고 있다가 closeAfterMs 뒤 닫는다(--parent-pipe 시험).</summary>
        static (int Code, string Out, string Err, long Ms, long AfterCloseMs) Cli(LifeRig r, string[] args, bool redirectStdin = false, int closeAfterMs = -1, int timeoutMs = 60000)
        {
            var psi = new ProcessStartInfo(Dev)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = redirectStdin,
                StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8,
            };
            psi.ArgumentList.Add("cli");
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["UART_HUB_DIR"] = r.HubDir;
            psi.Environment["UART_HUB_CONTROL_PORT"] = r.Ctl.ToString(CultureInfo.InvariantCulture);
            psi.Environment.Remove("UART_PATH_STYLE");
            var sw = Stopwatch.StartNew();
            using var p = Process.Start(psi);
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();
            long closedAt = -1;
            if (redirectStdin)
            {
                if (closeAfterMs >= 0)
                {
                    if (closeAfterMs > 0 && p.WaitForExit(closeAfterMs)) { }
                    closedAt = sw.ElapsedMilliseconds;
                    try { p.StandardInput.Close(); } catch (Exception) { }
                }
            }
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch (Exception) { } r.Ctx.Fail("cli 시간 초과: " + string.Join(" ", args)); }
            p.WaitForExit();
            long ms = sw.ElapsedMilliseconds;
            if (redirectStdin && closeAfterMs < 0) { try { p.StandardInput.Close(); } catch (Exception) { } }
            so.Wait(5000); se.Wait(5000);
            var res = (p.ExitCode, so.Result, se.Result, ms, closedAt < 0 ? -1 : ms - closedAt);
            r.Ctx.Log("cli " + string.Join(" ", args.Select(a => a.Length > 60 ? a.Substring(0, 60) + "…" : a)) + " → rc=" + res.ExitCode + " (" + ms + "ms) out=" + Trunc(res.Item2.Trim()) + (res.Item3.Trim().Length > 0 ? " err=" + Trunc(res.Item3.Trim()) : ""));
            return res;
        }

        static string Trunc(string s) => s.Length > 300 ? s.Substring(0, 300) + "…" : s;

        static JsonElement P(string s) { try { return LifeRig.Parse(s.Trim()); } catch (Exception) { return default; } }

        // ================================================================== cli-b64nul
        [Scenario("cli-b64nul", "cli --b64nul 경계(§2.3 l): 빈 토큰 = 인자 0개 → 106 '명령 없음' · NUL 하나 = 빈 문자열 1개('' 명령 — -B64Req [\"\"] 와 같은 응답, 0개와 다름) · 끝이 NUL 아닌 토큰 → 106 · base64url 아닌 문자 → 106 · 한글·큰/작은따옴표·역슬래시·$·'/'로 시작하는 인자 보존(대상 문자열이 오류 문구에 그대로) · 같은 argv 의 -B64Req 와 같은 응답(rc·status·out) · 표준 base64(패딩)도 받음 · --parent-pipe: 표준 입력을 쥐고 있으면 정상, 닫히면 ≤2초 안에 103(대기 중이던 wait 취소)",
                  Order = 980, TimeoutS = 180)]
        public static void CliB64Nul(ScenarioContext ctx)
        {
            var r = LifeRig.Create(ctx, "b64", 18100, 18110, "SIMB64CAB", "COM918", "SIMB64");
            // LifeRig 기본 사양은 printk_ms:300(RX 가 조용해지지 않아 probe 가 rx_not_quiet) — run 을 쓰는 이 시나리오는 printk 없는 보드로
            File.WriteAllText(r.SpecPath, "{\"boards\":[{\"cable\":\"SIMB64CAB\",\"com\":\"COM918\",\"hostname\":\"SIMB64\"}]}");
            r.StartSup();
            r.WaitCore(0, 30000, "코어 기동");
            r.WaitAttached("FakeBoard");
            string port = "18110";
            string state = null;
            ctx.WaitUntil(() =>
            {
                Cli(r, new[] { "probe", port, "--user-ok", "--json" });
                Thread.Sleep(300);
                state = J.S(P(Cli(r, new[] { "status", port, "--json" }).Out), "state");
                return state == "AT_PROMPT";
            }, 20000, "probe --user-ok → AT_PROMPT");

            // ① 빈 토큰 = 인자 0개
            var a0 = Cli(r, new[] { "--b64nul", "" });
            ctx.Check(a0.Code == 106 && a0.Err.Contains("명령 없음"), "빈 토큰(인자 0개) → 106 '명령 없음' (" + a0.Err.Trim() + ")");
            // ② NUL 하나 = 빈 문자열 1개
            ctx.Check(B64Nul("") == "AA", "B64Nul(\"\") = AA (NUL 1바이트)");
            var a1 = Cli(r, new[] { "--b64nul", "AA" });
            var a1r = Cli(r, new[] { "-B64Req", B64Req("") });
            ctx.Check(a1.Code == 106 && a1.Err.Contains(": 대상 <c> 가 필요함") && !a1.Err.Contains("명령 없음") && a1.Code == a1r.Code && a1.Err == a1r.Err && a1.Out == a1r.Out,
                      "NUL 하나 → 빈 문자열 1개('' 명령: '대상 <c> 가 필요함'), -B64Req [\"\"] 와 같은 응답, 인자 0개와 다름");
            // ③ 끝이 NUL 이 아닌 토큰
            string noNul = Convert.ToBase64String(Utf8.GetBytes("ls\0--json")).Replace('+', '-').Replace('/', '_').TrimEnd('=');
            var a2 = Cli(r, new[] { "--b64nul", noNul });
            ctx.Check(a2.Code == 106 && a2.Err.Contains("NUL 로 끝나야"), "끝이 NUL 이 아닌 토큰 → 106 (" + a2.Err.Trim() + ")");
            // ④ base64url 이 아닌 문자
            var a3 = Cli(r, new[] { "--b64nul", "bHM!AC0t" });
            ctx.Check(a3.Code == 106 && a3.Err.Contains("--b64nul 해석 실패"), "base64url 이 아닌 문자('!') → 106 (" + a3.Err.Trim() + ")");
            // ⑤ 특수 문자·'/'로 시작하는 대상 → 오류 문구에 그대로, -B64Req 와 같은 응답
            string odd = "/c/한글 \"dq\" 'sq' \\back $X -n";
            var b = Cli(r, new[] { "--b64nul", B64Nul("status", odd, "--json") });
            var br = Cli(r, new[] { "-B64Req", B64Req("status", odd, "--json") });
            var bj = P(b.Out);
            ctx.Check(b.Code == 102 && J.S(bj, "status") == "no_cable" && (J.S(bj, "error") ?? "").Contains("'" + odd + "'"),
                      "한글·따옴표·역슬래시·$·'/'로 시작하는 인자 보존: status '" + odd + "' → 102, 오류 문구에 그대로");
            ctx.Check(b.Code == br.Code && b.Out == br.Out && b.Err == br.Err, "같은 argv 의 -B64Req 와 같은 응답(rc·stdout·stderr 바이트 같음)");
            // ⑥ run: 보드 출력·rc 가 -B64Req 와 같음
            string cmd = "echo '한글 \"q\"' /abs \\$HOME; exit 3";
            var c = Cli(r, new[] { "--b64nul", B64Nul("run", port, cmd, "--json") });
            var cr = Cli(r, new[] { "-B64Req", B64Req("run", port, cmd, "--json") });
            var cj = P(c.Out); var crj = P(cr.Out);
            string outB = cj.ValueKind == JsonValueKind.Object && cj.TryGetProperty("out", out var o1) ? o1.GetRawText() : null;
            string outR = crj.ValueKind == JsonValueKind.Object && crj.TryGetProperty("out", out var o2) ? o2.GetRawText() : null;
            ctx.Check(c.Code == 3 && J.S(cj, "status") == "ok" && J.N(cj, "rc") == 3 && outB != null && JsonSerializer.Deserialize<string[]>(outB).SequenceEqual(new[] { "한글 \"q\" /abs $HOME" }),
                      "--b64nul run → rc 3, out [한글 \"q\" /abs $HOME]");
            ctx.Check(cr.Code == c.Code && J.S(crj, "status") == J.S(cj, "status") && J.N(crj, "rc") == J.N(cj, "rc") && outR == outB, "같은 argv 의 -B64Req run 과 rc·status·out 같음");
            // ⑦ 표준 base64(+/ 와 = 패딩)도 받음
            string std = Convert.ToBase64String(Utf8.GetBytes("ls\0--json\0"));
            ctx.Check(std.EndsWith("=", StringComparison.Ordinal), "표준 base64 토큰은 = 패딩으로 끝남(" + std + ")");
            var d = Cli(r, new[] { "--b64nul", std });
            ctx.Check(d.Code == 0 && J.S(P(d.Out), "status") == "ok", "표준 base64(패딩 있음) 토큰도 받음 → ls rc 0");

            // ⑧ --parent-pipe
            var e1 = Cli(r, new[] { "--parent-pipe", "--b64nul", B64Nul("ls", "--json") }, redirectStdin: true);
            ctx.Check(e1.Code == 0 && J.S(P(e1.Out), "status") == "ok", "--parent-pipe + 표준 입력을 쥐고 있음 → ls 정상(rc 0)");
            string wait = B64Nul("wait", port, "--pattern", "NEVER_MATCH_US109", "--timeout", "30", "--json");
            var e2 = Cli(r, new[] { "--parent-pipe", "--b64nul", wait }, redirectStdin: true, closeAfterMs: 1500);
            ctx.Check(e2.Code == 103 && e2.Err.Contains("--parent-pipe 표준 입력 EOF") && e2.AfterCloseMs >= 0 && e2.AfterCloseMs <= 2000,
                      "--parent-pipe: wait(30초) 중 표준 입력을 닫음 → " + e2.AfterCloseMs + "ms 뒤 103(≤2000ms), stderr '호출 쪽이 끝남'");
            var e3 = Cli(r, new[] { "status", port, "--json" });
            ctx.Check(e3.Code == 0 && J.S(P(e3.Out), "state") == "AT_PROMPT", "취소 뒤에도 보드는 AT_PROMPT(대기 요청이 남지 않음)");
            r.KillAll();
            r.CheckNoLeftovers();
        }

        // ================================================================== skills-render
        static readonly string[] Forbidden = { "/home/", "claude/uart", "D:\\", "/mnt/d" };

        static int Cr(byte[] b) => b.Count(x => x == (byte)'\r');

        [Scenario("skills-render", "skills 렌더링(A3-2, C-13 격리 실제 설치): SKILL.md 에 /home/·claude/uart·D:\\·/mnt/d 0건, CR 0·BOM 없음, frontmatter name/description(allowed-tools 없음), 필수 내용(종료코드 표·사람 우선 101·pending/released 102·restarted 5초 1회·reboot→wait --since·세 장면·경로 표기·금지 stty/vi). 래퍼 uart: CR 0, shebang, exe 경로(설치본) 한 줄, .uart_hub = install.json 의 install_id·버전. 가짜 WSL 배포판에 같은 바이트(uart 755). 실제 시스템 무변경",
                  Order = 981, TimeoutS = 240)]
        public static void SkillsRender(ScenarioContext ctx)
        {
            var real = RealSystem.Capture(ctx);
            var r = InstallRig.Create(ctx, "root", 18120, 18122);
            Directory.CreateDirectory(Path.Combine(r.UserProfile, ".claude"));
            r.FakeWsl("Ubuntu-R1\n");
            Directory.CreateDirectory(Path.Combine(r.Root, "fake_wsl", "home", "Ubuntu-R1", ".claude"));
            var e = r.Run(Dev, "install");
            ctx.Check(e.Code == 0 && e.Out.Contains("# 설치 완료"), "설치 exit 0 (" + e.Ms + "ms)" + (e.Code != 0 ? " — " + Upd.Tail(e.Out) : ""));
            var iv = r.InstallInfoJson().Value;
            string id = HubFiles.Str(iv, "install_id");

            string win = Path.Combine(r.UserProfile, ".claude", "skills", "uart");
            byte[] skill = File.ReadAllBytes(Path.Combine(win, "SKILL.md")), uart = File.ReadAllBytes(Path.Combine(win, "uart")), mark = File.ReadAllBytes(Path.Combine(win, ".uart_hub"));
            string st = Utf8.GetString(skill), ut = Utf8.GetString(uart);
            var hits = Forbidden.Select(f => f + "=" + Regex.Matches(st, Regex.Escape(f), RegexOptions.IgnoreCase).Count).ToList();
            ctx.Check(Forbidden.All(f => st.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0), "SKILL.md 금지 문자열 0건(" + string.Join(" ", hits) + ")");
            ctx.Check(Cr(skill) == 0 && !(skill.Length >= 3 && skill[0] == 0xEF && skill[1] == 0xBB && skill[2] == 0xBF), "SKILL.md CR 0바이트·BOM 없음(" + skill.Length + "B)");
            var fm = Regex.Match(st, @"\A---\n(.*?)\n---\n", RegexOptions.Singleline);
            ctx.Check(fm.Success && Regex.IsMatch(fm.Groups[1].Value, @"^name: uart$", RegexOptions.Multiline) && Regex.IsMatch(fm.Groups[1].Value, @"^description: .*(UART|콘솔).*$", RegexOptions.Multiline)
                      && !fm.Groups[1].Value.Contains("allowed-tools"), "frontmatter: name: uart · description(UART 콘솔) · allowed-tools 없음(Q3 — 기본은 허용 1회)");
            var need = new (string What, string Text)[]
            {
                ("CLI 위치", "~/.claude/skills/uart/uart"), ("종료코드 표", "| `hub_down` | 103 |"), ("사람 우선 101", "101(`busy`)이면 대신 보내지 않는다"),
                ("pending·released 안내", "새 케이블 확인 창"), ("트레이 케이블 설정", "케이블 설정"), ("restarted 5초 1회", "5초 기다린 뒤 같은 명령을 한 번만"),
                ("업데이트 중 103 보고", "업데이트 중"), ("reboot→wait --since", "wait 7002 --since"), ("장면 ① uname", "uname -a"), ("장면 ③ 사람 입력 중", "partial_line"),
                ("WSL 경로", "/mnt/<드라이브 소문자>/"), ("Git Bash 경로", "<드라이브>:/"), ("금지 stty", "`stty`"), ("금지 vi run", "`vi`"), ("spool 대신 send", "`send` 를 쓴다"),
            };
            var miss = need.Where(n => !st.Contains(n.Text)).Select(n => n.What).ToList();
            ctx.Check(miss.Count == 0, "SKILL.md 필수 내용 " + need.Length + "개" + (miss.Count > 0 ? " — 빠짐: " + string.Join(", ", miss) : ""));

            var lines = ut.Split('\n');
            string expLine = "UART_HUB_EXE_DEFAULT='" + r.InstalledExe + "'";
            ctx.Check(Cr(uart) == 0, "래퍼 uart CR 0바이트(" + uart.Length + "B)");
            ctx.Check(lines[0] == "#!/usr/bin/env bash", "래퍼 shebang #!/usr/bin/env bash");
            ctx.Check(lines.Count(l => l.StartsWith("UART_HUB_EXE_DEFAULT=", StringComparison.Ordinal)) == 1 && lines.Count(l => l == expLine) == 1
                      && lines.Count(l => l.Contains(r.InstalledExe)) == 1, "래퍼에 exe 경로 한 줄: " + expLine.Replace(r.Root, "<root>"));
            ctx.Check(ut.Contains("cli --parent-pipe --b64nul") && ut.Contains("trap 'exit 143' TERM"), "래퍼: cli --parent-pipe --b64nul + TERM trap(호출 쪽 생존)");
            var mj = P(Utf8.GetString(mark));
            ctx.Check(Cr(mark) == 0 && J.S(mj, "install_id") == id && J.S(mj, "version") == Hub.Version && J.S(mj, "env") == "windows" && SystemAccess.SamePath(J.S(mj, "exe"), r.InstalledExe),
                      ".uart_hub = install_id(install.json 과 같음)·version " + Hub.Version + "·env windows·exe, CR 0");

            string wh = Path.Combine(r.Root, "fake_wsl", "home", "Ubuntu-R1", ".claude", "skills", "uart");
            ctx.Check(File.Exists(Path.Combine(wh, "SKILL.md")) && File.ReadAllBytes(Path.Combine(wh, "SKILL.md")).SequenceEqual(skill)
                      && File.ReadAllBytes(Path.Combine(wh, "uart")).SequenceEqual(uart), "가짜 WSL 배포판 Ubuntu-R1: SKILL.md·uart 가 Windows 와 같은 바이트");
            var wm = P(File.ReadAllText(Path.Combine(wh, ".uart_hub")));
            ctx.Check(J.S(wm, "install_id") == id && J.S(wm, "version") == Hub.Version && J.S(wm, "env") == "wsl", "WSL .uart_hub: 같은 install_id·version, env wsl");
            string ops = File.ReadAllText(Path.Combine(r.Root, "fake_wsl", "ops.log"));
            ctx.Check(ops.Contains("Ubuntu-R1 write uart 755 " + uart.Length) && ops.Contains("Ubuntu-R1 write SKILL.md 644 " + skill.Length) && ops.Contains("Ubuntu-R1 write .uart_hub 644"),
                      "WSL 쓰기: 표준 입력으로 세 파일, uart 755·나머지 644(가짜 실행기 ops.log)");
            r.CheckNoProcs("정리");
            real.Verify(ctx, r);
        }

        // ================================================================== skills-deploy
        static string Home(InstallRig r, string distro) => Path.Combine(r.Root, "fake_wsl", "home", distro);
        static string WslSkill(InstallRig r, string distro) => Path.Combine(Home(r, distro), ".claude", "skills", "uart");

        static List<JsonElement> SkillsOf(InstallRig r)
        {
            var iv = r.InstallInfoJson().Value;
            return iv.TryGetProperty("skills", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(x => x.Clone()).ToList() : new List<JsonElement>();
        }

        static string Rec(List<JsonElement> l, string env, string distro = null)
        {
            var e = l.FirstOrDefault(x => J.S(x, "env") == env && (distro == null || J.S(x, "distro") == distro));
            return e.ValueKind == JsonValueKind.Object ? J.S(e, "result") + "/" + (J.S(e, "version") ?? "-") : "(없음)";
        }

        static string MarkVer(string dir) => File.Exists(Path.Combine(dir, ".uart_hub")) ? J.S(P(File.ReadAllText(Path.Combine(dir, ".uart_hub"))), "version") : "(없음)";

        [Scenario("skills-deploy", "skills 배치(§2.3 g, A3-1, C-13: 가짜 USERPROFILE + 가짜 WSL 실행기): 첫 설치 — Windows·.claude 있는 배포판에 넣기(uart 755), .claude 없는 배포판·Windows 는 건드리지 않음, 표시 없는 기존 폴더는 건너뜀+경고(내용 그대로), docker-desktop* 탐침 안 함, install.json skills[]. 업데이트(vB) — 기록된 곳 갱신(표시 버전 vB) + 새로 생긴 배포판에 넣기. 제거 — .uart_hub install_id 가 같은 곳만 지움(다른 설치 표시·사용자 폴더 남김). -l -q 무응답(설치 대기 흉내) → 10초 뒤 'WSL 없음', 탐침 0회. 실제 시스템 무변경",
                  Order = 982, TimeoutS = 420)]
        public static void SkillsDeploy(ScenarioContext ctx)
        {
            var real = RealSystem.Capture(ctx);
            string vA = Hub.Version;

            // ---------------------------------------------------------------- A: 넣기 → 업데이트 → 제거
            var a = InstallRig.Create(ctx, "a", 18130, 18132);
            string vB = a.Run(Upd.VB, "version").Out.Trim();
            ctx.Check(vB.Length == 17 && vB != vA, "두 번째 빌드 vB = " + vB);
            Directory.CreateDirectory(Path.Combine(a.UserProfile, ".claude"));
            a.FakeWsl("Alpha\nBeta\nGamma\ndocker-desktop\ndocker-desktop-data\n");
            Directory.CreateDirectory(Path.Combine(Home(a, "Alpha"), ".claude"));
            Directory.CreateDirectory(Home(a, "Beta"));
            Directory.CreateDirectory(WslSkill(a, "Gamma"));
            File.WriteAllText(Path.Combine(WslSkill(a, "Gamma"), "SKILL.md"), "user skill\n");
            string winA = Path.Combine(a.UserProfile, ".claude", "skills", "uart");

            var i1 = a.Run(Dev, "install");
            ctx.Check(i1.Code == 0 && i1.Out.Contains("# 설치 완료"), "A 첫 설치 exit 0 (" + i1.Ms + "ms)" + (i1.Code != 0 ? " — " + Upd.Tail(i1.Out) : ""));
            ctx.Check(i1.Out.Contains("skills Windows: 넣음 — " + winA) && MarkVer(winA) == vA && File.Exists(Path.Combine(winA, "uart")) && File.Exists(Path.Combine(winA, "SKILL.md")),
                      "Windows %USERPROFILE%\\.claude 있음 → skills\\uart 에 넣음(SKILL.md·uart·.uart_hub v" + vA + ")");
            ctx.Check(i1.Out.Contains("skills WSL Alpha: 넣음") && MarkVer(WslSkill(a, "Alpha")) == vA && File.ReadAllText(Path.Combine(a.Root, "fake_wsl", "ops.log")).Contains("Alpha write uart 755"),
                      "WSL Alpha(.claude 있음) → 넣음, uart 755");
            ctx.Check(i1.Out.Contains("skills WSL Beta: 건너뜀(.claude 없음)") && !Directory.Exists(Path.Combine(Home(a, "Beta"), ".claude")), "WSL Beta(.claude 없음) → 건너뜀, .claude 를 만들지 않음");
            ctx.Check(i1.Out.Contains("skills WSL Gamma: 건너뜀(표시(.uart_hub) 없는 기존 폴더") && i1.Out.Contains("경고: WSL Gamma") && File.ReadAllText(Path.Combine(WslSkill(a, "Gamma"), "SKILL.md")) == "user skill\n"
                      && !File.Exists(Path.Combine(WslSkill(a, "Gamma"), "uart")) && !File.Exists(Path.Combine(WslSkill(a, "Gamma"), ".uart_hub")),
                      "WSL Gamma(표시 없는 기존 skills/uart — 사용자가 만든 것) → 건너뜀+경고, 내용 그대로");
            ctx.Check(!Regex.IsMatch(i1.Out, @"wsl\.exe -d docker-desktop") && i1.Out.Contains("WSL 배포판: Alpha, Beta, Gamma"), "docker-desktop* 은 목록에서 빼고 탐침하지 않음");
            ctx.Check(InstallRig.Count(i1.Out, "[ext:fake] wsl.exe -l -q") == 1, "-l -q 는 사전 점검 1회(skills 배치가 결과를 다시 씀)");
            var s1 = SkillsOf(a);
            ctx.Check(s1.Count == 4 && Rec(s1, "windows") == "installed/" + vA && Rec(s1, "wsl", "Alpha") == "installed/" + vA && Rec(s1, "wsl", "Beta") == "skipped/-" && Rec(s1, "wsl", "Gamma") == "skipped/-",
                      "install.json skills[]: Windows·Alpha installed v" + vA + ", Beta·Gamma skipped (" + string.Join(" ", s1.Select(x => x.GetRawText())).Replace(a.Root, "<root>") + ")");
            ctx.Check(i1.Out.Contains("[ui] 완료 창") && i1.Out.Contains("Claude skills: Windows 넣음, WSL Alpha 넣음, WSL Beta 건너뜀"), "설치 요약(완료 창)에 skills 결과");
            ctx.WaitUntil(() => Upd.Count(Upd.HubLog(Upd.HubDir(a)), "[sup] 설치 상태 일치") >= 1, 20000, "A 첫 설치 뒤 C-14 '설치 상태 일치'");

            // 업데이트(vB): 새 배포판 Delta(.claude 있음) — 기록된 곳(Windows·Alpha) 갱신 + 새로 생긴 곳(Delta) 넣기
            a.FakeWsl("Alpha\nBeta\nGamma\nDelta\n");
            Directory.CreateDirectory(Path.Combine(Home(a, "Delta"), ".claude"));
            var u = a.Run(Upd.VB, "install");
            ctx.Check(u.Code == 0 && u.Out.Contains("# 업데이트 완료"), "A 업데이트 v" + vA + " → v" + vB + " exit 0 (" + u.Ms + "ms)" + (u.Code != 0 ? " — " + Upd.Tail(u.Out) : ""));
            int iU5 = u.Out.IndexOf("== U5.", StringComparison.Ordinal), iSk = u.Out.IndexOf("skills Windows:", StringComparison.Ordinal);
            ctx.Check(iU5 >= 0 && iSk > iU5, "skills 갱신은 U5(공백 뒤)에서");
            ctx.Check(u.Out.Contains("skills Windows: 갱신") && MarkVer(winA) == vB, "Windows: 갱신(.uart_hub 버전 v" + vB + ")");
            ctx.Check(u.Out.Contains("skills WSL Alpha: 갱신") && MarkVer(WslSkill(a, "Alpha")) == vB, "기록된 곳 Alpha: 갱신(.uart_hub 버전 v" + vB + ")");
            ctx.Check(u.Out.Contains("skills WSL Delta: 넣음") && MarkVer(WslSkill(a, "Delta")) == vB, "새로 생긴 곳 Delta: 넣음(v" + vB + ")");
            ctx.Check(File.ReadAllText(Path.Combine(WslSkill(a, "Gamma"), "SKILL.md")) == "user skill\n" && !Directory.Exists(Path.Combine(Home(a, "Beta"), ".claude")), "업데이트 뒤에도 Gamma(사용자 폴더)·Beta(.claude 없음) 그대로");
            var s2 = SkillsOf(a);
            ctx.Check(Rec(s2, "windows") == "updated/" + vB && Rec(s2, "wsl", "Alpha") == "updated/" + vB && Rec(s2, "wsl", "Delta") == "installed/" + vB, "install.json skills[]: Windows·Alpha updated, Delta installed (v" + vB + ")");

            // 제거: Delta 의 표시를 다른 설치 것으로 바꿔 둠 → 표시가 같은 곳(Windows·Alpha)만 지움
            string other = new string('f', 32);
            string dm = Path.Combine(WslSkill(a, "Delta"), ".uart_hub");
            File.WriteAllText(dm, File.ReadAllText(dm).Replace(HubFiles.Str(a.InstallInfoJson().Value, "install_id"), other));
            var un = a.Run(a.InstalledExe, "uninstall");
            ctx.Check(un.Code == 0 && un.Out.Contains("# 제거 완료"), "A 제거 exit 0 (" + un.Ms + "ms)" + (un.Code != 0 ? " — " + Upd.Tail(un.Out) : ""));
            ctx.Check(un.Out.Contains("skills Windows: 지움") && !Directory.Exists(winA) && Directory.Exists(Path.Combine(a.UserProfile, ".claude", "skills")), "제거: Windows skills\\uart 지움(.claude\\skills 는 남김)");
            ctx.Check(un.Out.Contains("skills WSL Alpha: 지움") && !Directory.Exists(WslSkill(a, "Alpha")), "제거: Alpha(표시 install_id 같음) 지움");
            ctx.Check(un.Out.Contains("skills WSL Delta: 건너뜀(다른 설치의 표시") && File.Exists(Path.Combine(WslSkill(a, "Delta"), "uart")), "제거: Delta(다른 설치의 표시) 남김");
            ctx.Check(File.ReadAllText(Path.Combine(WslSkill(a, "Gamma"), "SKILL.md")) == "user skill\n" && !Regex.IsMatch(un.Out, @"wsl\.exe -d (Beta|Gamma) "),
                      "제거: 기록상 넣지 않은 Beta·Gamma 는 탐침도 하지 않음, Gamma 내용 그대로");
            a.CheckNoProcs("A 정리");

            // ---------------------------------------------------------------- B: -l -q 무응답 → 10초 뒤 WSL 없음
            var b = InstallRig.Create(ctx, "nowsl", 18140, 18142);
            b.FakeWsl("Never\n");
            b.Write(Path.Combine("fake_wsl", "l_q.delay_ms"), "600000");
            var ib = b.Run(Dev, "install");
            ctx.Check(ib.Code == 0 && ib.Out.Contains("# 설치 완료"), "B 설치 exit 0 (" + ib.Ms + "ms)" + (ib.Code != 0 ? " — " + Upd.Tail(ib.Out) : ""));
            var lm = Regex.Match(ib.Out, @"\[ext:fake\] wsl\.exe -l -q → rc=-1 \(시간 초과\) (\d+)ms");
            long lq = lm.Success ? long.Parse(lm.Groups[1].Value, CultureInfo.InvariantCulture) : -1;
            ctx.Check(lm.Success && lq >= 9500 && lq <= 12000 && ib.Out.Contains("WSL 배포판: WSL 없음(rc=-1, 10초 응답 없음)"), "-l -q 무응답 → " + lq + "ms 에 끊고 'WSL 없음'(10초 제한)");
            ctx.Check(InstallRig.Count(ib.Out, "[ext:fake] wsl.exe -l -q") == 1 && !ib.Out.Contains("wsl.exe -d "), "-l -q 1회(다시 기다리지 않음), 배포판 탐침 0회");
            ctx.Check(ib.Out.Contains("skills Windows: 건너뜀(.claude 없음)") && !Directory.Exists(Path.Combine(b.UserProfile, ".claude")), "Windows .claude 없음 → 건너뜀, .claude 를 만들지 않음");
            var sb = SkillsOf(b);
            ctx.Check(sb.Count == 1 && Rec(sb, "windows") == "skipped/-", "install.json skills[] = [Windows skipped]");
            b.CheckNoProcs("B 정리");

            // ---------------------------------------------------------------- C: Windows 에 사용자가 만든 skills/uart
            var c = InstallRig.Create(ctx, "foreign", 18144, 18146);
            string winC = Path.Combine(c.UserProfile, ".claude", "skills", "uart");
            Directory.CreateDirectory(winC);
            File.WriteAllText(Path.Combine(winC, "SKILL.md"), "my own\n");
            var ic = c.Run(Dev, "install");
            ctx.Check(ic.Code == 0 && ic.Out.Contains("skills Windows: 건너뜀(표시(.uart_hub) 없는 기존 폴더") && ic.Out.Contains("경고: " + winC) && ic.Out.Contains("WSL 배포판: WSL 없음"),
                      "C: Windows 표시 없는 skills\\uart → 건너뜀+경고(WSL 없음은 바로 판정)");
            ctx.Check(File.ReadAllText(Path.Combine(winC, "SKILL.md")) == "my own\n" && Directory.GetFileSystemEntries(winC).Length == 1, "C: 사용자 폴더 내용 그대로(파일 1개)");
            var uc = c.Run(c.InstalledExe, "uninstall");
            ctx.Check(uc.Code == 0 && uc.Out.Contains("skills Windows: 건너뜀(표시(.uart_hub) 없음") && File.ReadAllText(Path.Combine(winC, "SKILL.md")) == "my own\n", "C 제거: 표시 없는 폴더는 남김");
            c.CheckNoProcs("C 정리");
            real.Verify(ctx, a, b, c);
        }

        // ================================================================== install-update-skills
        [Scenario("install-update-skills", "업데이트 공백과 skills(NF-3·A1-4, US-106 이관): 가짜 WSL 배포판 2개(.claude 있음) 탐침에 각 20초 지연을 주입해도 케이블 로그 *** hub stop *** → *** hub start *** COM 공백 ≤15초 — 배포판 탐침은 U5(공백 뒤)에서, 설치기 전체는 지연만큼 길어짐(≥40초). 두 배포판·Windows 의 .uart_hub 버전 = vB, hub status version 모두 vB, 정상 경로 Repair 0, 감독은 설치기 끝까지 기다림. 실제 시스템 무변경",
                  Order = 983, TimeoutS = 360)]
        public static void InstallUpdateSkills(ScenarioContext ctx)
        {
            var real = RealSystem.Capture(ctx);
            var r = InstallRig.Create(ctx, "root", 18150, 18160);
            string vA = Hub.Version;
            string vB = r.Run(Upd.VB, "version").Out.Trim();
            Directory.CreateDirectory(Path.Combine(r.UserProfile, ".claude"));
            r.FakeWsl("Slow-1\nSlow-2\n");
            foreach (var d in new[] { "Slow-1", "Slow-2" }) Directory.CreateDirectory(Path.Combine(Home(r, d), ".claude"));
            var c14 = Upd.Env("UART_HUB_TEST_C14", "180,1");           // 감독의 설치기 대기 상한(시험 기본 30초)을 지연(40초+)보다 길게 — 운영 값은 600초
            Upd.InstallAndWait(ctx, r, Dev, c14, 1, "vA 첫 설치(지연 없음)");
            string hub = Upd.HubDir(r), logRoot = Upd.LogRoot(r);
            Upd.Ls(ctx, r, "vA", r.Cable);
            Upd.Status(ctx, r, vA, "vA");

            foreach (var d in new[] { "Slow-1", "Slow-2" }) r.Write(Path.Combine("fake_wsl", d + ".delay_ms"), "20000");
            var u = r.Run(Upd.VB, new[] { "install" }, c14, 180000);
            ctx.Check(u.Code == 0 && u.Out.Contains("v" + vA + " → v" + vB + " — 업데이트") && u.Out.Contains("# 업데이트 완료"),
                      "업데이트 v" + vA + " → v" + vB + " exit 0 (" + u.Ms + "ms)" + (u.Code != 0 ? " — " + Upd.Tail(u.Out) : ""));
            var pm = Regex.Matches(u.Out, @"skills WSL (Slow-\d): 갱신 — \S+ \((\d+)ms\)");
            var probeMs = pm.Cast<Match>().ToDictionary(m => m.Groups[1].Value, m => long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
            ctx.Check(probeMs.Count == 2 && probeMs.Values.All(x => x >= 20000 && x < 30000), "배포판 탐침 2개 각 20초 지연 아래 갱신(" + string.Join(", ", probeMs.Select(kv => kv.Key + " " + kv.Value + "ms")) + ")");
            int iGap = u.Out.IndexOf("공백(정지 시작 → 건강 확인)", StringComparison.Ordinal), iU5 = u.Out.IndexOf("== U5.", StringComparison.Ordinal),
                iP = u.Out.IndexOf("wsl.exe -d Slow-1", StringComparison.Ordinal);
            ctx.Check(iGap >= 0 && iU5 > iGap && iP > iU5, "배포판 탐침은 공백이 끝난 뒤(U4 건강 확인 → U5) — install.log 순서");
            ctx.Check(u.Ms >= 40000, "설치기 전체 " + u.Ms + "ms ≥ 40초(지연은 실제로 들어감)");
            double gap = Upd.Gap(ctx, logRoot, r.Base, vB, "업데이트", out _);
            ctx.Check(gap <= 15, "COM 공백 " + gap.ToString("0.0", CultureInfo.InvariantCulture) + "초 ≤ 15초(배포판 탐침 2 × 20초 지연 아래)");
            var gm = Regex.Match(u.Out, @"공백\(정지 시작 → 건강 확인\) (\d+\.\d)초");
            ctx.Log("설치기 기록 공백(정지 시작 → 건강 확인) " + (gm.Success ? gm.Groups[1].Value : "?") + "초");
            Upd.Status(ctx, r, vB, "업데이트 뒤");
            string win = Path.Combine(r.UserProfile, ".claude", "skills", "uart");
            ctx.Check(MarkVer(win) == vB && MarkVer(WslSkill(r, "Slow-1")) == vB && MarkVer(WslSkill(r, "Slow-2")) == vB, "Windows·Slow-1·Slow-2 의 .uart_hub 버전 = v" + vB);
            ctx.WaitUntil(() => Upd.Count(Upd.HubLog(hub), "[sup] 설치 상태 일치") >= 2, 20000, "업데이트 뒤 새 감독 C-14 '설치 상태 일치'(설치기가 끝난 뒤)");
            string hl = Upd.HubLog(hub);
            ctx.Check(Upd.Count(hl, "Repair 시작") == 0 && !Upd.InstLog(r).Contains("== Repair(") && !hl.Contains("설치기가 180초 안에 끝나지 않음"), "정상 경로 Repair 0회, 감독은 설치기 마무리(skills) 끝까지 기다림");
            r.CheckNoProcs("정리");
            real.Verify(ctx, r);
        }
    }
}
