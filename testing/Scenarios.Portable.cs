// X4(휴대용 전환, 2026-11-05 — 백신 행동 탐지가 설치기의 자기 복사→복사본 실행→시작프로그램 등록을 드로퍼로 봄) 시나리오.
//   인자 없는 실행 = 그 자리에서 허브. C-13 격리 아래 실제 UartHub.exe 를 시험 루트의 "사용자 폴더"(Tools…\UartHub.exe)에 두고 인자 없이 띄운다.
//   포트: portable-first-run 18400/18410, portable-second-run 18420/18430, portable-new-version 18440/18450, portable-uninstall 18460/18470.
//   공통 단언(NoInstallArtifacts): 시험 루트의 시작프로그램·시작 메뉴에 .lnk 0, %LOCALAPPDATA%\uart_hub 는 설정 파일만(install.json·icons.json·prefs.json —
//   실행 파일·.lnk 0), 시험 루트 안 UartHub*.exe 는 시나리오가 놓은 것뿐(자기 복사 0), 작업 스케줄러 호출 0. 끝에 실제 시스템 무변경(RealSystem).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UartHub.Testing
{
    public static class PortableScenarios
    {
        static string Dev => TestEnv.UartHubExe;
        static readonly string[] NoArgs = new string[0];

        /// <summary>exe 를 시험 루트의 사용자 폴더(rel\UartHub.exe)에 놓는다.</summary>
        static string Place(InstallRig r, string rel, string src)
        {
            string p = Path.Combine(r.Root, rel, "UartHub.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.Copy(src, p, true);
            return p;
        }

        static InstallRig Rig(ScenarioContext ctx, int ctl)
        {
            var r = InstallRig.Create(ctx, "root", ctl, ctl + 10);
            r.Write("fake_drives.json", "{\"D\":{\"type\":\"Removable\",\"ready\":true}}");
            r.FakeWsl("Ubuntu-Fake\n");
            Directory.CreateDirectory(Path.Combine(r.UserProfile, ".claude"));
            return r;
        }

        internal static string Rel(InstallRig r, string p) => p.Replace(r.Root + "\\", "<root>\\");

        internal static JsonElement Info(InstallRig r) => r.InstallInfoJson() ?? default;
        internal static string HubOf(InstallRig r) => HubFiles.Str(Info(r), "hub_dir");
        internal static string WinSkill(InstallRig r, string f) => Path.Combine(r.UserProfile, ".claude", "skills", "uart", f);

        /// <summary>처음 실행([시작] 자동 응답) → exit 0 + C-14 '설치 상태 일치'.</summary>
        static void FirstStart(ScenarioContext ctx, InstallRig r, string exe, string what)
        {
            var e = r.Run(exe, NoArgs);
            ctx.Check(e.Code == 0 && e.Out.Contains("# 시작 완료(휴대용)") && InstallRig.Count(e.Out, "[ui] 확인 창(자동 응답=시작)") == 1,
                      what + ": 인자 없이 실행 → 확인 창 1회([시작]) → exit 0 (" + e.Ms + "ms)" + (e.Code != 0 ? " — " + Upd.Tail(e.Out) : ""));
            string hub = HubOf(r);
            ctx.WaitUntil(() => Upd.HubLog(hub).Contains("[sup] 설치 상태 일치"), 20000, what + ": 감독 C-14 '설치 상태 일치'");
        }

        /// <summary>바탕화면에 대상 = exe, 인자 open &lt;port&gt; 인 아이콘(경로). 없으면 null.</summary>
        internal static string Icon(InstallRig r, string exe, int port)
        {
            if (!Directory.Exists(r.Desktop)) return null;
            foreach (var f in Directory.GetFiles(r.Desktop, "*.lnk"))
            {
                var i = ShellLink.Read(f);
                if (i != null && SystemAccess.SamePath(i.Target, exe) && (i.Arguments ?? "").Trim() == "open " + port) return f;
            }
            return null;
        }

        internal static string WaitIcon(ScenarioContext ctx, InstallRig r, string exe, string what)
        {
            string got = null;
            ctx.WaitUntil(() => (got = Icon(r, exe, r.Base)) != null, 15000, what + ": 바탕화면 아이콘(대상 = " + Rel(r, exe) + ", open " + r.Base + ")");
            return got;
        }

        internal static void WaitLs(ScenarioContext ctx, InstallRig r, string exe, string what)
        {
            ctx.WaitUntil(() =>
            {
                var o = r.Run(exe, "cli", "ls", "--json");
                if (o.Code != 0) return false;
                var j = Upd.Parse(o.Out);
                return j.ValueKind == JsonValueKind.Object && j.GetProperty("cables").EnumerateArray().Any(c => J.S(c, "cable") == r.Cable && J.S(c, "status") == "attached");
            }, 15000, what + ": " + Rel(r, exe) + " cli ls → FakeBoard " + r.Cable + " attached");
        }

        internal static List<(int Pid, string Cmd)> Sups(InstallRig r) => r.Procs().Where(p => Regex.IsMatch(p.Cmd, @"""?\s+sup(\s|$)")).ToList();

        /// <summary>실행 중인 이 시험 루트의 감독·코어가 모두 exe 의 것인가(명령줄).</summary>
        internal static void ProcsFrom(ScenarioContext ctx, InstallRig r, string exe, string what)
        {
            var ps = r.Procs();
            bool all = ps.Count >= 2 && ps.All(p => p.Cmd.IndexOf(exe, StringComparison.OrdinalIgnoreCase) >= 0);
            ctx.Check(all && ps.Any(p => Regex.IsMatch(p.Cmd, @"\s+sup(\s|$)")) && ps.Any(p => p.Cmd.Contains(" core ")),
                      what + ": 감독·코어 = " + Rel(r, exe) + " (" + ps.Count + "개: " + string.Join(" | ", ps.Select(p => p.Cmd.Replace(r.Root, "<root>"))) + ")");
        }

        /// <summary>설치 흔적 없음: 시작프로그램·시작 메뉴 .lnk 0, %LOCALAPPDATA%\uart_hub 는 설정 파일만, 시험 루트 안 UartHub*.exe = placed, 작업 스케줄러 0.</summary>
        static void NoInstallArtifacts(ScenarioContext ctx, InstallRig r, string what, params string[] placed)
        {
            var lnks = Directory.Exists(r.StartMenu) ? Directory.GetFiles(r.StartMenu, "*", SearchOption.AllDirectories) : new string[0];
            ctx.Check(lnks.Length == 0, what + ": 시작프로그램·시작 메뉴에 파일 0(" + string.Join(",", lnks.Select(Path.GetFileName)) + ")");
            var la = Directory.Exists(r.InstallDir) ? Directory.GetFiles(r.InstallDir, "*", SearchOption.AllDirectories).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToArray() : new string[0];
            ctx.Check(la.All(f => f == "install.json" || f == "icons.json" || f == "prefs.json"),
                      what + ": %LOCALAPPDATA%\\uart_hub = 설정 파일만, 실행 파일·.lnk 0 (" + (la.Length == 0 ? "비어 있음/없음" : string.Join(",", la)) + ")");
            var extra = Directory.GetFiles(r.Root, "UartHub*.exe", SearchOption.AllDirectories).Where(e => !placed.Any(p => SystemAccess.SamePath(p, e))).ToList();
            ctx.Check(extra.Count == 0, what + ": 시험 루트의 UartHub*.exe = 놓아 둔 " + placed.Length + "개뿐(자기 복사 0)" + (extra.Count > 0 ? " — 더 있음: " + string.Join(",", extra.Select(x => x.Replace(r.Root, "<root>"))) : ""));
            ctx.Check(!Regex.IsMatch(r.AllText(), @"\[ext(:fake)?\] schtasks"), what + ": 작업 스케줄러 호출 0(가짜 포함)");
        }

        // ================================================================== portable-first-run
        [Scenario("portable-first-run", "휴대용 처음 실행(X4, C-13 격리, 사용자 폴더의 실제 exe 를 인자 없이): 예전 설치 방식 install.json 이 있으면 제거 안내·중단(exit 3, 무변경). 취소 → exit 2·시험 루트 무변경. [시작] → 확인 창 1회(로그 위치·skills 넣을 곳·자동 실행 shell:startup 직접 안내), 허브 폴더·토큰·config(seed)·install.json(exe = 그 exe·state=installed·portable:true·skills[]), 그 exe 의 감독·코어(운영 모드·C-11 감시)·C-14 일치·Repair 0, FakeBoard attached·바탕화면 아이콘(대상 = 그 exe)·skills(래퍼 exe 줄 = 그 exe). 시작프로그램·시작 메뉴 .lnk 0, %LOCALAPPDATA%\\uart_hub 는 설정 파일만, 자기 복사 0, 작업 스케줄러 0, exe 바이트 그대로. 실제 시스템 무변경",
                  Order = 1100, TimeoutS = 180)]
        public static void FirstRun(ScenarioContext ctx)
        {
            var real = RealSystem.Capture(ctx);
            var r = Rig(ctx, 18400);
            string exe = Place(r, Path.Combine("Tools", "UartHub"), Dev);
            string sha0 = Upd.Sha(exe);

            // ⓪ 예전 설치 방식(portable 아님) install.json → 안내·중단
            r.Write(Path.Combine("LocalAppData", "uart_hub", "install.json"), "{\"version\":\"1\",\"state\":\"installing\",\"hub_dir\":\"x\",\"exe\":\"x\"}\n");
            var snap = InstallRig.Snapshot(r.Root);
            var b = r.Run(exe, NoArgs);
            ctx.Check(b.Code == 3 && b.Out.Contains("이전 방식으로 설치된 UART 허브가 있습니다") && b.Out.Contains("[ui] 오류 창") && !b.Out.Contains("[ui] 확인 창"),
                      "예전 설치 방식 install.json → 제거 안내(오류 창)·중단 exit 3 (rc=" + b.Code + ")");
            string db = InstallRig.Diff(snap, InstallRig.Snapshot(r.Root));
            ctx.Check(db == "", "예전 설치본 중단: 시험 루트 무변경" + (db == "" ? "" : ": " + db));
            Directory.Delete(r.InstallDir, true);

            // ① 취소 → 무변경
            snap = InstallRig.Snapshot(r.Root);
            var c = r.Run(exe, NoArgs, Upd.Env("UART_HUB_TEST_ANSWER", "cancel"));
            ctx.Check(c.Code == 2 && InstallRig.Count(c.Out, "[ui] 확인 창(자동 응답=취소)") == 1 && c.Out.Contains("취소 — 아무것도 바꾸지 않음"), "취소 → exit 2 (확인 창 1회, 자동 응답=취소)");
            string d0 = InstallRig.Diff(snap, InstallRig.Snapshot(r.Root));
            ctx.Check(d0 == "", "취소: 시험 루트 무변경(허브 폴더·install.json 없음)" + (d0 == "" ? "" : ": " + d0));

            // ② [시작]
            var e = r.Run(exe, NoArgs);
            ctx.Check(e.Code == 0 && e.Out.Contains("# 시작 완료(휴대용)"), "[시작] → exit 0 (" + e.Ms + "ms)" + (e.Code != 0 ? " — " + Upd.Tail(e.Out) : ""));
            ctx.Check(InstallRig.Count(e.Out, "[ui] 확인 창(자동 응답=시작)") == 1 && InstallRig.Count(e.Out, "[ui] 확인 창") == 1 && e.Out.Contains("[ui] 완료 창(5초 뒤 닫힘)"),
                      "확인 창 1회(자동 응답=시작) + 완료 창(5초 뒤 닫힘)");
            string expRoot = Path.Combine(r.UserProfile, "uart_logs");
            // 화면 글(UI_COPY.md "처음 시작"·"켜는 중 → 끝"): 창에는 머리·본문·요약 3줄(기술 정보는 자세히), install.log 의 "  | " 줄은 기술 기록 그대로
            string uiConfirm = e.Out.Split('\n').FirstOrDefault(l => l.Contains("[ui] 확인 창(자동 응답=시작)")) ?? "";
            string uiDone = e.Out.Split('\n').FirstOrDefault(l => l.Contains("[ui] 완료 창(")) ?? "";
            ctx.Check(uiConfirm.Contains(": UART 허브 · UART 허브를 시작할까요? / 보드 UART를 터미널과 Claude가 같이 쓰게 해 주는 프로그램입니다. 설치하지 않고 이 폴더에서 실행합니다. / 로그 저장 위치: " + expRoot
                                         + " · Claude 연결 정보를 넣을 곳: Windows, WSL Ubuntu-Fake · COM 포트는 '/connect-uart COM3'처럼 연결할 때만 엽니다. · 7줄")
                      && e.Out.Contains("[ui] 진행 창: UART 허브를 켜는 중입니다. 단계 허브 켜기 → Claude 연결 정보 넣기 → 확인")
                      && uiDone.Contains("준비됐습니다. / 트레이에 UART 허브 아이콘이 생겼습니다. 아이콘이 안 보이면 작업 표시줄 오른쪽 ^ 안에 있습니다.")
                      && !uiConfirm.Contains("—") && !uiDone.Contains("—") && !uiConfirm.Contains(exe),
                      "창 글: '처음 시작'(제목 UART 허브·첫 줄·본문·요약 3줄, exe 경로는 자세히로) · 진행 창 단계 3개 · 완료 '준비됐습니다.' (긴 줄표 없음) — " + uiConfirm.Trim() + " || " + uiDone.Trim());
            ctx.Check(e.Out.Contains("  | 로그 위치: " + expRoot + "  (D: 이동식") && e.Out.Contains("  | Claude skills 를 넣을 곳: Windows " + WinSkill(r, "").TrimEnd('\\'))
                      && e.Out.Contains("Ubuntu-Fake") && Regex.IsMatch(e.Out, @"\| 자동 실행은 직접: .*shell:startup") && e.Out.Contains("(설치·복사 없음): " + exe),
                      "확인 창 내용: 로그 위치(D: 이동식 → %USERPROFILE%\\uart_logs)·skills 넣을 곳(Windows·WSL)·자동 실행은 직접(shell:startup)·이 exe 그대로");
            var iv = Info(r);
            string hub = HubOf(r), id = HubFiles.Str(iv, "install_id");
            ctx.Check(iv.ValueKind == JsonValueKind.Object && SystemAccess.SamePath(HubFiles.Str(iv, "exe"), exe) && HubFiles.Str(iv, "state") == "installed" && J.B(iv, "portable") == true
                      && HubFiles.Str(iv, "version") == Hub.Version && SystemAccess.SamePath(hub, Path.Combine(expRoot, "hub")) && Regex.IsMatch(id ?? "", "^[0-9a-f]{32}$")
                      && iv.TryGetProperty("skills", out var sk) && sk.ValueKind == JsonValueKind.Array && sk.GetArrayLength() == 2,
                      "install.json: exe = 그 exe · state=installed · portable:true · version · hub_dir · install_id · skills[Windows·WSL]");
            ctx.Check(TokenFile.IsValid(Path.Combine(hub, "token")) && File.ReadAllText(Path.Combine(hub, "config.json")) == Config.WithLogLayout(r.SeedConfig(), out _)
                      && Directory.Exists(Path.Combine(hub, Config.SpoolDirName, "live")) && File.Exists(Path.Combine(hub, "install.log")),
                      "허브 폴더: 토큰(32 hex)·config.json(시험 seed + \"log_layout\": 3 — C.12)·임시기록\\live·install.log(기록은 허브 폴더에)");
            var sj = HubFiles.ReadJson(HubFiles.SupState(hub));
            ctx.Check(sj.HasValue && HubFiles.Str(sj.Value, "state") == "running" && SystemAccess.SamePath(HubFiles.Str(sj.Value, "exe"), exe), "sup.json running · exe = 그 exe");
            ProcsFrom(ctx, r, exe, "처음 실행");
            string hl = Upd.HubLog(hub);
            ctx.Check(hl.Contains("(운영 모드)") && hl.Contains("[core] 감독 감시 시작") && Upd.Count(hl, "Repair 시작") == 0, "운영 모드 감독(install.json) → 코어 C-11 감시, C-14 Repair 0");
            WaitLs(ctx, r, exe, "처음 실행");
            WaitIcon(ctx, r, exe, "처음 실행");
            string wrap = File.Exists(WinSkill(r, "uart")) ? File.ReadAllText(WinSkill(r, "uart")) : "";
            string mk = File.Exists(WinSkill(r, ".uart_hub")) ? File.ReadAllText(WinSkill(r, ".uart_hub")) : "";
            ctx.Check(wrap.Contains("UART_HUB_EXE_DEFAULT='" + exe + "'") && mk.Contains(id) && mk.Contains(Hub.Version), "skills(Windows): 래퍼 exe 줄 = 그 exe, .uart_hub = install_id·버전");
            NoInstallArtifacts(ctx, r, "처음 실행", exe);
            ctx.Check(Upd.Sha(exe) == sha0, "exe 바이트 그대로(" + sha0 + ")");
            r.CheckNoProcs("정리");
            real.Verify(ctx, r);
        }

        // ================================================================== portable-second-run
        [Scenario("portable-second-run", "휴대용 다음 실행(X4, C-13): 같은 exe 다시 더블클릭 → 창 없이 '이미 실행 중'(트레이 풍선 1회, 감독 PID·install.json 그대로). 트레이 종료(감독 요청 shutdown, C-5) → 감독·코어 0·control.json stopped 아님. 다시 더블클릭 → 창 없이 시작(install.json 바이트·skills 그대로). C-9: 감독·코어 kill → cli ls 103 restarted:true → install.json 의 exe(그 휴대용 경로)로 감독 재기동. C-11: 감독만 kill → 코어가 그 exe 로 sup respawn. 시작프로그램·시작 메뉴 .lnk 0·%LOCALAPPDATA%\\uart_hub 설정 파일만·자기 복사 0. 실제 시스템 무변경",
                  Order = 1101, TimeoutS = 240)]
        public static void SecondRun(ScenarioContext ctx)
        {
            var real = RealSystem.Capture(ctx);
            var r = Rig(ctx, 18420);
            string exe = Place(r, Path.Combine("Tools", "UartHub"), Dev);
            FirstStart(ctx, r, exe, "처음 실행");
            string hub = HubOf(r);
            string ij0 = File.ReadAllText(r.InstallJson);
            var s0 = Sups(r);

            // ① 같은 exe → 이미 실행 중
            var a = r.Run(exe, NoArgs);
            ctx.Check(a.Code == 0 && a.Out.Contains("이미 실행 중(같은 exe)") && !a.Out.Contains("[ui]"), "같은 exe 다시 실행 → exit 0 '이미 실행 중', 창 0 (" + a.Ms + "ms)");
            ctx.WaitUntil(() => Upd.HubLog(hub).Contains("풍선: UART 허브가 이미 켜져 있습니다. 작업 표시줄 오른쪽의 아이콘을 쓰세요(안 보이면 ^ 안)."), 5000, "트레이 풍선 '이미 켜져 있습니다'(시험 모드는 hub.log 풍선 줄)");
            var s1 = Sups(r);
            ctx.Check(s0.Count == 1 && s1.Count == 1 && s0[0].Pid == s1[0].Pid && File.ReadAllText(r.InstallJson) == ij0, "감독 1개·PID 그대로(" + s1.FirstOrDefault().Pid + ")·install.json 바이트 그대로");

            // ② 트레이 종료(허브 끄기) = 감독 요청 shutdown
            var sd = SupervisorChannel.Request(hub, "shutdown", "tray", 10000);
            ctx.Log("shutdown → " + sd);
            ctx.WaitUntil(() => r.Procs().Count == 0, 20000, "종료(허브 끄기): 감독·코어 모두 끝남(exe 를 덮어쓸 수 있음)");
            ctx.Check(!HubFiles.IsStopped(hub), "종료: control.json stopped 아님(다음 더블클릭에 바로 기록)");

            // ③ 다시 더블클릭 → 창 없이 시작
            var b = r.Run(exe, NoArgs);
            ctx.Check(b.Code == 0 && b.Out.Contains("# 시작 완료(휴대용)") && !b.Out.Contains("[ui]") && b.Out.Contains("install.json 그대로") && b.Out.Contains("skills 그대로"),
                      "다시 더블클릭 → 창 없이 시작(install.json·skills 그대로) exit 0 (" + b.Ms + "ms)" + (b.Code != 0 ? " — " + Upd.Tail(b.Out) : ""));
            ctx.Check(File.ReadAllText(r.InstallJson) == ij0, "install.json 바이트 그대로");
            ProcsFrom(ctx, r, exe, "다시 시작");
            WaitLs(ctx, r, exe, "다시 시작");

            // ④ C-9: 감독·코어 모두 kill → cli 가 install.json 의 exe 로 감독을 다시 띄움
            r.KillAll();                                            // 명령줄에 이 시험 루트가 든 UartHub.exe 만(감독 먼저)
            ctx.Check(r.Procs().Count == 0, "C-9 준비: 감독·코어 kill(이 시험 루트 것만)");
            var o = r.Run(exe, "cli", "ls", "--json");
            ctx.Check(o.Code == 103 && Upd.True(Upd.Parse(o.Out), "restarted"), "C-9: 감독·코어 없음 → cli ls 103 restarted:true");
            ctx.WaitUntil(() => Sups(r).Count == 1 && r.Procs().Any(p => p.Cmd.Contains(" core ")), 15000, "C-9: 감독·코어 다시 뜸");
            ProcsFrom(ctx, r, exe, "C-9 재기동");
            WaitLs(ctx, r, exe, "C-9 뒤");

            // ⑤ C-11: 감독만 kill → 코어가 그 exe 로 sup respawn
            int before = Upd.Count(Upd.HubLog(hub), "sup respawn");
            var sup = Sups(r).First();
            Upd.KillPid(ctx, r, sup.Pid, "C-11 감독");
            ctx.WaitUntil(() => Upd.Count(Upd.HubLog(hub), "sup respawn") > before && Sups(r).Count == 1, 15000, "C-11: 코어가 감독을 다시 띄움([core] sup respawn)");
            string resp = Regex.Matches(Upd.HubLog(hub), @"sup respawn pid=\d+ exe=(.+)").Cast<Match>().Last().Groups[1].Value.Trim();
            ctx.Check(resp.StartsWith(exe, StringComparison.OrdinalIgnoreCase), "C-11: respawn exe = install.json 의 휴대용 exe(" + resp.Replace(r.Root, "<root>") + ")");
            ProcsFrom(ctx, r, exe, "C-11 재기동");

            NoInstallArtifacts(ctx, r, "다음 실행", exe);
            r.CheckNoProcs("정리");
            real.Verify(ctx, r);
        }

        // ================================================================== portable-new-version
        [Scenario("portable-new-version", "휴대용 새 판(X4, C-13): A(vA, ToolsA) 실행 중 다른 경로의 B(vB, ToolsB) 더블클릭 → 확인 창 '기존 허브를 끄고 새 exe 로 바꿀까요?'. 취소 → A 그대로. [예] → shutdown(C-5)으로 A 의 감독·코어를 내리고 B 의 sup, hub status version == supervisor.version == core_version == vB, install.json exe = B·version = vB, 바탕화면 아이콘 대상 A → B(같은 파일), skills 래퍼 exe = B·.uart_hub vB. 트레이 종료 → 같은 경로 exe 덮어쓰기 → 더블클릭 → 창 없이 그 판(skills 갱신). 시작프로그램·시작 메뉴 .lnk 0·%LOCALAPPDATA%\\uart_hub 설정 파일만·자기 복사 0. 실제 시스템 무변경",
                  Order = 1102, TimeoutS = 300)]
        public static void NewVersion(ScenarioContext ctx)
        {
            var real = RealSystem.Capture(ctx);
            var r = Rig(ctx, 18440);
            ctx.Check(File.Exists(Upd.VB), "두 번째 빌드 있음: " + Upd.VB);
            string exeA = Place(r, Path.Combine("ToolsA", "UartHub"), Dev), exeB = Place(r, Path.Combine("ToolsB", "UartHub"), Upd.VB);
            string vA = Hub.Version, vB = r.Run(exeB, "version").Out.Trim();
            ctx.Check(vB.Length == 17 && vB != vA, "vB = 버전 문자열만 다른 빌드(vA " + vA + " → vB " + vB + ")");
            FirstStart(ctx, r, exeA, "A 처음 실행");
            string hub = HubOf(r), id = HubFiles.Str(Info(r), "install_id");
            WaitLs(ctx, r, exeA, "A");
            string iconA = WaitIcon(ctx, r, exeA, "A");
            var supA = Sups(r).First();

            // ① B — 취소 → A 그대로
            var c = r.Run(exeB, NoArgs, Upd.Env("UART_HUB_TEST_ANSWER", "cancel"));
            ctx.Check(c.Code == 2 && c.Out.Contains("[ui] 확인 창(자동 응답=취소): UART 허브 · 새 버전으로 바꿀까요? / 지금 켜진 허브(" + vA + ")를 끄고 이\u00A0버전(" + vB + ")으로 다시 켭니다.")
                      && c.Out.Contains("기존 허브를 끄고 새 exe 로 바꿀까요?"), "B 더블클릭 → 교체 확인 창 '새 버전으로 바꿀까요?'(두 버전) → 취소 exit 2");
            ctx.Check(Sups(r).Select(p => p.Pid).SequenceEqual(new[] { supA.Pid }) && SystemAccess.SamePath(HubFiles.Str(Info(r), "exe"), exeA), "취소: A 의 감독 그대로·install.json exe = A");

            // ② B — [예] → A 를 내리고 B
            var u = r.Run(exeB, NoArgs);
            ctx.Check(u.Code == 0 && InstallRig.Count(u.Out, "[ui] 확인 창(자동 응답=바꾸기)") == 1 && u.Out.Contains("# 시작 완료(휴대용) v" + vB) && u.Out.Contains("[ui] 완료 창(5초 뒤 닫힘): 새 버전으로 바꿨습니다.")
                      && u.Out.Contains("[ui] 진행 창: 새 버전으로 바꾸는 중입니다. 단계 켜진 허브 끄기 → 새 버전 켜기 → Claude 연결 정보 넣기"),
                      "[바꾸기] → exit 0, 진행 창 단계 3개, 완료 창 '새 버전으로 바꿨습니다.' (" + u.Ms + "ms)" + (u.Code != 0 ? " — " + Upd.Tail(u.Out) : ""));
            ctx.Check(u.Out.Contains("감독 요청 shutdown") && u.Out.Contains("허브 정지 완료") && !u.Out.Contains("검증 PID kill pid"), "A: 감독 요청 shutdown(C-5)으로 정상 종료(검증 PID kill 없음)");
            ctx.Check(u.Out.Contains("모두 v" + vB), "건강 확인: hub status version == supervisor.version == core_version == vB");
            ProcsFrom(ctx, r, exeB, "교체 뒤");
            var iv = Info(r);
            ctx.Check(SystemAccess.SamePath(HubFiles.Str(iv, "exe"), exeB) && HubFiles.Str(iv, "version") == vB && J.B(iv, "portable") == true && HubFiles.Str(iv, "install_id") == id,
                      "install.json: exe = B · version = vB · portable · install_id 그대로");
            ctx.Check(!HubFiles.IsStopped(hub), "control.json stopped 아님");
            ctx.Check(u.Out.Contains("아이콘 대상 바꿈") && Icon(r, exeA, r.Base) == null && SystemAccess.SamePath(Icon(r, exeB, r.Base), iconA),
                      "바탕화면 아이콘: 같은 파일의 대상이 A → B");
            var lb = ShellLink.Read(iconA);
            ctx.Check(lb != null && SystemAccess.SamePath(lb.IconPath, exeB) && lb.IconIndex == 0, "바탕화면 아이콘 모양도 B 의 exe 아이콘(IconLocation " + lb?.IconPath?.Replace(r.Root, "<root>") + "," + lb?.IconIndex + ")");
            WaitLs(ctx, r, exeB, "B");
            ctx.WaitUntil(() => { var s = HubFiles.ReadJson(HubFiles.SupState(hub)); return s.HasValue && HubFiles.Long(s.Value, "icon_count") == 1 && Upd.HubLog(hub).Contains("기동 정리 대상 — icons.json 1개"); }, 10000,
                          "새 감독이 아이콘을 이어받음(icons.json 1개·icon_count 1)");
            ctx.Check(Directory.GetFiles(r.Desktop, "*.lnk").Length == 1, "바탕화면 아이콘 1개(중복·충돌 없음)");
            string wrap = File.ReadAllText(WinSkill(r, "uart")), mk = File.ReadAllText(WinSkill(r, ".uart_hub"));
            ctx.Check(wrap.Contains("UART_HUB_EXE_DEFAULT='" + exeB + "'") && mk.Contains(vB) && mk.Contains(id), "skills: 래퍼 exe 줄 = B, .uart_hub version vB·install_id 그대로");

            // ③ 트레이 종료 → 같은 경로 exe 덮어쓰기 → 더블클릭(창 없음)
            SupervisorChannel.Request(hub, "shutdown", "tray", 10000);
            ctx.WaitUntil(() => r.Procs().Count == 0, 20000, "종료(허브 끄기): 감독·코어 0");
            for (int i = 0; ; i++)
            {
                try { File.Copy(Dev, exeB, true); break; }
                catch (IOException) when (i < 40) { System.Threading.Thread.Sleep(250); }    // 끝난 프로세스의 이미지 잠금이 풀릴 때까지
            }
            var w = r.Run(exeB, NoArgs);
            ctx.Check(w.Code == 0 && !w.Out.Contains("[ui]") && w.Out.Contains("# 시작 완료(휴대용) v" + vA), "덮어쓴 exe 더블클릭 → 창 없이 시작 v" + vA + " (" + w.Ms + "ms)" + (w.Code != 0 ? " — " + Upd.Tail(w.Out) : ""));
            iv = Info(r);
            ctx.Check(SystemAccess.SamePath(HubFiles.Str(iv, "exe"), exeB) && HubFiles.Str(iv, "version") == vA && File.ReadAllText(WinSkill(r, ".uart_hub")).Contains(vA),
                      "같은 경로 판 바꿈: install.json exe = B·version = vA, skills .uart_hub = vA");
            ProcsFrom(ctx, r, exeB, "덮어쓴 뒤");
            WaitLs(ctx, r, exeB, "덮어쓴 뒤");

            NoInstallArtifacts(ctx, r, "새 판", exeA, exeB);
            r.CheckNoProcs("정리");
            real.Verify(ctx, r);
        }

        // ================================================================== portable-uninstall
        [Scenario("portable-uninstall", "휴대용 제거(X4, C-13): uninstall --dry-run 은 출력만(바탕화면·설정·skills 무변경, 허브 계속). uninstall → 확인 창 1회 → shutdown 으로 허브 정지(프로세스 0) → 자기 아이콘만 삭제(사용자 바로가기 보존)·icons.json 삭제 → skills(표시가 맞는 것) 삭제 → install.json 삭제. exe(바이트 그대로)·로그·허브 폴더(token·config·uninstall_<시각>.log)·%LOCALAPPDATA%\\uart_hub 폴더(prefs.json)는 남김, 자기 이미지 이동 0(%TEMP%\\uart_hub_removed_* 없음), control.json stopped 아님. 다시 더블클릭 → 처음 실행 확인 창. 실제 시스템 무변경",
                  Order = 1103, TimeoutS = 180)]
        public static void Uninstall(ScenarioContext ctx)
        {
            var real = RealSystem.Capture(ctx);
            var r = Rig(ctx, 18460);
            string exe = Place(r, Path.Combine("Tools", "UartHub"), Dev);
            string sha0 = Upd.Sha(exe);
            FirstStart(ctx, r, exe, "처음 실행");
            string hub = HubOf(r);
            WaitLs(ctx, r, exe, "처음 실행");
            string icon = WaitIcon(ctx, r, exe, "처음 실행");
            string mine = Path.Combine(r.Desktop, "내 메모.lnk");
            ShellLink.Save(mine, new ShellLinkInfo { Target = Path.Combine(r.Root, "notes.txt"), Arguments = "", Description = "사용자 바로가기" });
            File.WriteAllText(Path.Combine(r.InstallDir, "prefs.json"), "{\"terminal\":\"auto\"}\n");
            ctx.Check(File.Exists(WinSkill(r, "uart")), "skills 있음(제거 전)");

            // ① dry-run
            var snap = InstallRig.Snapshot(r.Desktop, r.InstallDir, Path.Combine(r.UserProfile, ".claude"));
            var dr = r.Run(exe, "uninstall", "--dry-run");
            ctx.Check(dr.Code == 0 && dr.Out.Contains("# dry-run 끝") && !dr.Out.Contains("[ui]") && dr.Out.Contains("[dry-run] 삭제 " + icon) && dr.Out.Contains("[dry-run] 삭제 " + r.InstallJson),
                      "uninstall --dry-run: exit 0, 아이콘·install.json 삭제 예정만 출력, 창 0");
            string d0 = InstallRig.Diff(snap, InstallRig.Snapshot(r.Desktop, r.InstallDir, Path.Combine(r.UserProfile, ".claude")));
            ctx.Check(d0 == "" && Sups(r).Count == 1, "dry-run: 바탕화면·%LOCALAPPDATA%\\uart_hub·.claude 무변경, 허브 계속 실행" + (d0 == "" ? "" : ": " + d0));

            // ② 제거
            var u = r.Run(exe, "uninstall");
            ctx.Check(u.Code == 0 && InstallRig.Count(u.Out, "[ui] 확인 창(자동 응답=제거): UART 허브 제거 · UART 허브를 제거할까요?") == 1 && u.Out.Contains("# 제거 완료(휴대용)")
                      && u.Out.Contains("[ui] 완료 창(5초 뒤 닫힘): UART 허브를 제거했습니다. / exe와 로그는 남아 있습니다."),
                      "uninstall → 확인 창 1회 → exit 0 (" + u.Ms + "ms)" + (u.Code != 0 ? " — " + Upd.Tail(u.Out) : ""));
            ctx.Check(u.Out.Contains("감독 요청 shutdown") && u.Out.Contains("허브 정지 완료"), "허브 정지: 감독 요청 shutdown(C-5)");
            ctx.WaitUntil(() => r.Procs().Count == 0, 10000, "제거 뒤 이 시험 루트의 UartHub.exe 0");
            ctx.Check(!File.Exists(icon) && File.Exists(mine), "자기 아이콘만 삭제(사용자 바로가기 '내 메모.lnk' 보존)");
            ctx.Check(!File.Exists(r.InstallJson) && !File.Exists(Path.Combine(r.InstallDir, "icons.json")) && File.Exists(Path.Combine(r.InstallDir, "prefs.json")) && Directory.Exists(r.InstallDir),
                      "install.json·icons.json 삭제, %LOCALAPPDATA%\\uart_hub 폴더·prefs.json 남김(폴더 삭제 없음)");
            ctx.Check(!File.Exists(WinSkill(r, "uart")) && !File.Exists(WinSkill(r, ".uart_hub")), "skills(표시 install_id 일치) 삭제");
            ctx.Check(File.Exists(exe) && Upd.Sha(exe) == sha0 && !Directory.GetFiles(r.Temp, "uart_hub_removed_*").Any(), "exe 남음(바이트 그대로)·자기 이미지 이동 0");
            ctx.Check(TokenFile.IsValid(Path.Combine(hub, "token")) && File.Exists(Path.Combine(hub, "config.json")) && Directory.GetFiles(hub, "uninstall_*.log").Length == 1 && !HubFiles.IsStopped(hub),
                      "허브 폴더 남김(token·config·uninstall_<시각>.log), control.json stopped 아님");

            // ③ 다시 더블클릭 → 처음 실행
            var again = r.Run(exe, NoArgs, Upd.Env("UART_HUB_TEST_ANSWER", "cancel"));
            ctx.Check(again.Code == 2 && again.Out.Contains("[ui] 확인 창(자동 응답=취소)") && again.Out.Contains("(설치·복사 없음): " + exe), "제거 뒤 다시 더블클릭 → 처음 실행 확인 창(취소 exit 2)");

            NoInstallArtifacts(ctx, r, "제거", exe);
            r.CheckNoProcs("정리");
            real.Verify(ctx, r);
        }
    }
}
