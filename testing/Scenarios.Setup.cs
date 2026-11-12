// X7(레드팀식 배포, 2026-11-06 — 계획 부록 D) 시나리오: 배포 폴더의 exe 를 Claude 가 INSTALL_UART_HUB.md 대로 부르는 setup·uninstall --yes.
//   C-13 격리 아래 실제 UartHub.exe 를 시험 루트의 "받은 폴더"에 두고(MotW = Zone.Identifier 를 붙여 인터넷에서 받은 것처럼) 하위 명령으로 띄운다. 창 0 = 출력에 "[ui]" 없음.
//   시험 config 는 실제 휴대용 첫 config 처럼 "on_demand": true 이고, FakeBoard 케이블은 state.json 에 포트를 미리 배정해(쓰던 케이블) 붙는다.
//   setup-portable : 옛 설치 방식 잔재(install.json portable 아님·설치본 exe·시작프로그램 바로가기) → 계획·--yes 모두 exit 3·무변경 → 이 exe 의 uninstall --yes 로 잔재 정리 /
//                    계획만(한국어·--json): 시험 루트 무변경(결과 파일 setup_last.json 만), MotW·skills 넣을 곳(.claude 있는 WSL 배포판만)·할 일 보고 /
//                    --yes --json 첫 등록(창 0, install.json portable·exe = 그 exe, 허브 폴더·토큰·config, 감독·코어 = 그 exe, 제어 포트 응답, skills Windows·WSL, MotW 그대로) /
//                    같은 exe 로 다시 --yes = 바꾼 것 없음(install.json·config·token·install.log·skills·감독 PID 그대로, 풍선 0) / uninstall --yes(창 0, 휴대용 제거와 같은 범위) /
//                    --yes --no-start 등록(프로세스 0). setup_last.json = --json 표준 출력(JSON 한 줄).
//   setup-replace  : A(ToolsA, vA)를 setup --yes 로 등록·실행 중 → B(ToolsB, vB) 계획(실행 중 A·교체 할 일, 무변경) → B setup --yes = 확인 창 없이 shutdown(C-5)으로 A 를 내리고 B,
//                    hub status 모두 vB, install.json exe = B·version = vB·install_id 그대로, 포트·token·config·state(assign·released)·로그 파일 그대로, 아이콘 대상 A → B, skills = B.
//   리뷰 반영(2026-11-06): setup-portable 에 installer·update·sup mutex 막힘(누구인지 모르는 감독)·UNC·이동식 위치 막힘·더블클릭 중복, 계획의 켜진 배포판만 탐침(unknown)·
//                    포트·작업 스케줄러·UART_HUB_DIR 경고·시간 예산(budget), 처음 등록 건강 확인 실패 → rolled_back, 정지(stopped) 보고, 등록 없는 uninstall --yes = 3,
//                    휴대용 등록 + 옛 잔재 → uninstall --yes 가 잔재까지 치움. setup-replace 에 교체 건강 확인 실패 → rolled_back(이전 허브 다시 켬), 되돌리기 막힘·--allow-downgrade,
//                    오래된 exe 더블클릭(옛 자동 실행 바로 가기) → 등록된 새 판으로 켬, --no-start 교체.
//   포트: setup-portable 18800/18810, setup-replace 18820/18830.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace UartHub.Testing
{
    public static class SetupScenarios
    {
        static string Dev => TestEnv.UartHubExe;

        static InstallRig Rig(ScenarioContext ctx, int ctl)
        {
            var r = InstallRig.Create(ctx, "root", ctl, ctl + 10);
            r.Write("fake_drives.json", "{\"D\":{\"type\":\"Removable\",\"ready\":true}}");
            r.Write("seed_config.json", Seed(r));
            r.Write(Path.Combine("UserProfile", "uart_logs", "hub", "state.json"),
                    "{\n  \"assign\": {\n    " + Json.Str(r.Cable) + ": " + r.Base + "\n  },\n  \"released\": [],\n  \"boards\": {}\n}\n");
            r.FakeWsl("Ubuntu-Fake\nDebian-Fake\n");
            Directory.CreateDirectory(Path.Combine(r.Root, "fake_wsl", "home", "Ubuntu-Fake", ".claude"));      // Debian-Fake 는 .claude 없음
            Directory.CreateDirectory(Path.Combine(r.UserProfile, ".claude"));
            return r;
        }

        /// <summary>시험 seed 에 "on_demand": true(실제 휴대용 첫 config 와 같은 연결 방식).</summary>
        static string Seed(InstallRig r) => r.SeedConfig().Replace("{\n", "{\n  \"on_demand\": true,\n");

        /// <summary>exe 를 시험 루트의 받은 폴더(rel\UartHub.exe)에 놓는다. motw 면 인터넷에서 받은 표시(Zone.Identifier, ZoneId=3)를 붙인다.</summary>
        static string Place(InstallRig r, string rel, string src, bool motw)
        {
            string p = Path.Combine(r.Root, rel, "UartHub.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.Copy(src, p, true);
            if (motw) File.WriteAllText(p + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=https://example.invalid/\r\n");
            return p;
        }

        static string Last(InstallRig r) => Path.Combine(r.InstallDir, "setup_last.json");
        static string WslSkill(InstallRig r, string distro, string rel) => Path.Combine(r.Root, "fake_wsl", "home", distro, ".claude", "skills", rel);
        static string Rel(InstallRig r, string p) => PortableScenarios.Rel(r, p);

        /// <summary>스냅숏 차이(결과 파일·그 폴더 항목은 뺌 — setup 은 계획만일 때도 setup_last.json 은 쓴다).</summary>
        static string DiffExcept(SortedDictionary<string, string> a, SortedDictionary<string, string> b, params string[] skip)
        {
            foreach (var k in skip) { a.Remove(k); b.Remove(k); }
            return InstallRig.Diff(a, b);
        }

        /// <summary>--json 출력: 표준 출력이 JSON 한 줄뿐이고 setup_last.json 과 같은 글자.</summary>
        static JsonElement JsonOut(ScenarioContext ctx, InstallRig r, (int Code, string Out, string Err, long Ms) e, string what)
        {
            string o = (e.Out ?? "").Trim();
            string f = File.Exists(Last(r)) ? File.ReadAllText(Last(r)).Trim() : "(없음)";
            var j = Upd.Parse(o);
            ctx.Check(j.ValueKind == JsonValueKind.Object && o.Split('\n').Length == 1 && o == f && !o.Contains("[ui]"),
                      what + ": --json 표준 출력 = JSON 한 줄 = setup_last.json, 창 0" + (o == f ? "" : " — 출력 " + Upd.Tail(o) + " / 파일 " + Upd.Tail(f)));
            return j;
        }

        static readonly string[] NoArgs = new string[0];

        // 작업 개체(Job) — Claude Code·터미널이 셸을 묶는 방식 흉내: KILL_ON_JOB_CLOSE(닫으면 안의 프로세스를 모두 끝냄) + BREAKAWAY_OK(자식이 CREATE_BREAKAWAY_FROM_JOB 로 나갈 수 있음)
        [StructLayout(LayoutKind.Sequential)]
        struct JobLimit
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinWs, MaxWs;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
            public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObjectW(IntPtr sa, string name);
        [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int cls, ref JobLimit info, int len);
        [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr proc);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        /// <summary>exe 를 KILL_ON_JOB_CLOSE·BREAKAWAY_OK 작업 안에서 실행하고, 끝나면 작업을 닫는다(안에 남은 프로세스는 끝남). (rc, 표준 출력, 작업에 넣었나, ms)</summary>
        static (int Code, string Out, bool Assigned, long Ms) RunInJob(InstallRig r, string exe, params string[] args)
        {
            IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
            var li = new JobLimit { LimitFlags = 0x2000 | 0x800 };
            bool set = SetInformationJobObject(job, 9, ref li, Marshal.SizeOf<JobLimit>());
            var sw = Stopwatch.StartNew();
            var (p, output) = Upd.Start(r, exe, args, null);
            bool assigned = set && AssignProcessToJobObject(job, p.Handle);       // setup 은 감독을 띄우기 전에 상태를 읽느라 수백 ms — 그 전에 들어간다
            if (!p.WaitForExit(120000)) r.Ctx.Fail("시간 초과: 작업 안 " + string.Join(" ", args));
            string o = output();
            int code = p.ExitCode;
            CloseHandle(job);
            return (code, o, assigned, sw.ElapsedMilliseconds);
        }

        /// <summary>setup 이 막혀야 하는 상황: 계획(--json)·--yes --json 모두 exit 3 blocked, message 에 why, 시험 루트 무변경(결과 파일만).</summary>
        static void Blocked(ScenarioContext ctx, InstallRig r, string exe, string why, string what, Dictionary<string, string> env = null)
        {
            var snap = InstallRig.Snapshot(r.Root);
            var a = r.Run(exe, new[] { "setup", "--json" }, env);
            var ja = JsonOut(ctx, r, a, what + " 계획");
            var b = r.Run(exe, new[] { "setup", "--yes", "--json" }, env);
            var jb = JsonOut(ctx, r, b, what + " --yes");
            string d = DiffExcept(snap, InstallRig.Snapshot(r.Root), Last(r), r.InstallDir);
            ctx.Check(a.Code == 3 && b.Code == 3 && J.S(ja, "result") == "blocked" && J.S(jb, "result") == "blocked" && (J.S(jb, "message") ?? "").Contains(why) && (J.A(jb, "actions")?.Length ?? -1) == 0 && d == "",
                      what + " → 계획·--yes 모두 exit 3 blocked('" + why + "')·시험 루트 무변경 (rc " + a.Code + "/" + b.Code + ")" + (d == "" ? "" : ": " + d) + (b.Code != 3 ? " — " + Upd.Tail(b.Out) : ""));
        }

        static string Skills(JsonElement j)
            => j.TryGetProperty("skills", out var a) && a.ValueKind == JsonValueKind.Array
               ? string.Join(", ", a.EnumerateArray().Select(x => (J.S(x, "env") == "wsl" ? J.S(x, "distro") : "windows") + "=" + J.S(x, "result"))) : "(없음)";

        /// <summary>설치 흔적 없음: 시작프로그램·시작 메뉴 파일 0, %LOCALAPPDATA%\uart_hub 는 설정·결과 파일만, 시험 루트의 UartHub*.exe 는 놓아 둔 것뿐, 작업 스케줄러 0.</summary>
        static void NoInstallArtifacts(ScenarioContext ctx, InstallRig r, string what, params string[] placed)
        {
            var lnks = Directory.Exists(r.StartMenu) ? Directory.GetFiles(r.StartMenu, "*", SearchOption.AllDirectories) : new string[0];
            ctx.Check(lnks.Length == 0, what + ": 시작프로그램·시작 메뉴에 파일 0(" + string.Join(",", lnks.Select(Path.GetFileName)) + ")");
            var la = Directory.Exists(r.InstallDir) ? Directory.GetFiles(r.InstallDir, "*", SearchOption.AllDirectories).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToArray() : new string[0];
            ctx.Check(la.All(f => f == "install.json" || f == "icons.json" || f == "prefs.json" || f == "setup_last.json"),
                      what + ": %LOCALAPPDATA%\\uart_hub = 설정·결과 파일만, 실행 파일·.lnk 0 (" + (la.Length == 0 ? "비어 있음/없음" : string.Join(",", la)) + ")");
            var extra = Directory.GetFiles(r.Root, "UartHub*.exe", SearchOption.AllDirectories).Where(e => !placed.Any(p => SystemAccess.SamePath(p, e))).ToList();
            ctx.Check(extra.Count == 0, what + ": 시험 루트의 UartHub*.exe = 놓아 둔 " + placed.Length + "개뿐(자기 복사 0)" + (extra.Count > 0 ? " — 더 있음: " + string.Join(",", extra.Select(x => Rel(r, x))) : ""));
            ctx.Check(!Regex.IsMatch(r.AllText(), @"\[ext(:fake)?\] schtasks(\.exe)? .*/(create|delete|change)", RegexOptions.IgnoreCase), what + ": 작업 스케줄러 쓰기 0(가짜 포함 — setup 은 /query 로 읽기만)");
        }

        // ================================================================== setup-portable
        [Scenario("setup-portable", "setup(X7 부록 D.3, C-13, 받은 폴더의 실제 exe + MotW): 옛 설치 방식 잔재(install.json portable 아님·설치본·시작프로그램 바로가기) → 계획·--yes 모두 exit 3 blocked·시험 루트 무변경, 같은 exe 의 uninstall --yes 로 잔재 정리. installer mutex(더블클릭은 exit 0)·update mutex·누구인지 모르는 감독·UNC·이동식 위치 → 계획·--yes 모두 exit 3·무변경. 계획만(한국어·--json, 켜진 배포판만 탐침 — 꺼진 것 unknown, 포트·PS 작업·UART_HUB_DIR 경고, 시간 예산 넘으면 budget) = 시험 루트 무변경(setup_last.json 만), MotW ZoneId=3·로그 위치·허브 폴더·skills 넣을 곳(Windows·.claude 있는 WSL 배포판만)·할 일 보고. --yes --json 첫 등록: 창 0, install.json portable·exe = 그 exe, 토큰·config(on_demand), 감독·코어 = 그 exe, 제어 포트 응답, C-14 일치, FakeBoard attached·아이콘, skills Windows·WSL(connect-uart·disconnect-uart 포함), MotW·exe 바이트 그대로. 처음 등록 건강 확인 실패 → rolled_back(허브 내림·install.json 없음). 같은 exe 로 다시 --yes = 바꾼 것 없음(install.json·config·token·install.log·skills·감독 PID 그대로, 풍선 0). 정지 상태 = stopped true·'트레이 → 시작'. uninstall --yes = 창 0·휴대용 제거와 같은 범위(exe·로그 남김), 다시 = exit 3. --yes --no-start 등록 = 프로세스 0. 휴대용 등록 + 옛 잔재 → 막힘·잔재별 안내 → uninstall --yes 가 잔재까지 치움. --json 표준 출력 = setup_last.json. 시작프로그램·시작 메뉴 0·자기 복사 0·작업 스케줄러 0. 실제 시스템 무변경",
                  Order = 1400, TimeoutS = 240)]
        public static void Portable(ScenarioContext ctx)
        {
            var real = RealSystem.Capture(ctx);
            var r = Rig(ctx, 18800);
            string exe = Place(r, Path.Combine("Downloads", "UartHub_pkg"), Dev, true);
            string sha0 = Upd.Sha(exe);
            string hub0 = Path.Combine(r.UserProfile, "uart_logs", "hub"), expRoot = Path.Combine(r.UserProfile, "uart_logs");
            ctx.Check(r.AdsExists(exe, "Zone.Identifier"), "준비: 받은 exe 에 MotW(Zone.Identifier)");

            // ⓪ 옛 설치 방식 잔재 → 계획·--yes 모두 중단(무변경)
            r.Write(Path.Combine("LocalAppData", "uart_hub", "install.json"),
                    "{\"version\":\"20261101000000000\",\"install_id\":\"0123456789abcdef0123456789abcdef\",\"exe\":" + Json.Str(r.InstalledExe) + ",\"hub_dir\":" + Json.Str(hub0)
                    + ",\"log_root\":" + Json.Str(expRoot) + ",\"state\":\"installed\",\"skills\":[]}\n");
            r.Write(Path.Combine("LocalAppData", "uart_hub", "UartHub.exe"), "MZ 옛 설치본 자리\n");
            Directory.CreateDirectory(Path.GetDirectoryName(r.StartupLnk));
            ShellLink.Save(r.StartupLnk, new ShellLinkInfo { Target = r.InstalledExe, Arguments = "sup", Description = "UART 허브 감독(로그온 때 자동 실행) " + ShellLink.Marker("0123456789abcdef0123456789abcdef") });
            var snap = InstallRig.Snapshot(r.Root);
            var b1 = r.Run(exe, "setup", "--json");
            var jb = JsonOut(ctx, r, b1, "옛 잔재 계획");
            ctx.Check(b1.Code == 3 && J.S(jb, "result") == "blocked" && J.S(jb, "mode") == "plan" && (J.A(jb, "legacy")?.Length ?? 0) == 3 && (J.A(jb, "actions")?.Length ?? -1) == 0,
                      "옛 잔재(install.json portable 아님·설치본 exe·시작프로그램 바로가기) → 계획도 exit 3 blocked·legacy 3개·할 일 0: " + string.Join(" | ", J.A(jb, "legacy") ?? new string[0]).Replace(r.Root, "<root>"));
            var b2 = r.Run(exe, "setup", "--yes");
            ctx.Check(b2.Code == 3 && b2.Out.Contains("# 결과: 중단 (exit 3)") && b2.Out.Contains("uninstall --yes") && !b2.Out.Contains("[ui]"),
                      "옛 잔재 → setup --yes 도 exit 3 중단(창 0, 다음: 사용자 허락 뒤 uninstall --yes) (rc=" + b2.Code + ")" + (b2.Code != 3 ? " — " + Upd.Tail(b2.Out) : ""));
            string d0 = DiffExcept(snap, InstallRig.Snapshot(r.Root), Last(r));
            ctx.Check(d0 == "", "옛 잔재 중단: 시험 루트 무변경(setup_last.json 말고)" + (d0 == "" ? "" : ": " + d0));
            var lu = r.Run(exe, "uninstall", "--yes");
            ctx.Check(lu.Code == 0 && lu.Out.Contains("--yes: 확인 창 없이 진행(제거)") && !lu.Out.Contains("[ui]") && !Directory.Exists(r.InstallDir) && !File.Exists(r.StartupLnk),
                      "옛 잔재를 이 exe 의 uninstall --yes 로 정리(창 0): 설치 폴더·시작프로그램 바로가기 삭제 (rc=" + lu.Code + ")" + (lu.Code != 0 ? " — " + Upd.Tail(lu.Out) : ""));

            // ⓪-2 막힘(무변경): installer mutex(다른 설치기·확인 창) · update mutex · 누구인지 모르는 감독(sup mutex 만) · UNC·이동식 위치
            using (new Mutex(true, SystemAccess.MutexNameFor(r.Root, "installer")))
            {
                Blocked(ctx, r, exe, "다른 설치기·제거기(UART 허브 창)가 실행 중", "installer mutex 있음(다른 설치기·확인 창)");
                var dc = r.Run(exe, NoArgs);
                ctx.Check(dc.Code == 0 && dc.Out.Contains("다른 설치기가 이미 실행 중") && !dc.Out.Contains("[ui]") && !File.Exists(r.InstallJson),
                          "installer mutex 를 쥔 동안 더블클릭 → exit 0 '다른 설치기가 이미 실행 중'(창·변경 0)");
            }
            using (new Mutex(true, SystemAccess.MutexNameFor(r.Root, "update")))
                Blocked(ctx, r, exe, "다른 설치기·업데이트가 교체 중", "update mutex 있음");
            using (new Mutex(true, SystemAccess.MutexNameFor(r.Root, "sup")))
                Blocked(ctx, r, exe, "누구인지 확인할 수 없습니다", "sup mutex 만 있고 sup.json 없음(누구인지 모르는 감독)");
            r.Write("fake_self_path.txt", @"\\wsl.localhost\Ubuntu\home\u\UartHub_pkg\UartHub.exe");
            Blocked(ctx, r, exe, "네트워크·UNC 경로", "exe 위치 = \\\\wsl.localhost\\…(UNC)");
            File.Delete(Path.Combine(r.Root, "fake_self_path.txt"));
            r.Write("fake_drives.json", "{\"D\":{\"type\":\"Removable\",\"ready\":true},\"C\":{\"type\":\"Removable\",\"ready\":true}}");
            Blocked(ctx, r, exe, "이동식 디스크(C:)", "exe 위치 = 이동식 디스크");
            r.Write("fake_drives.json", "{\"D\":{\"type\":\"Removable\",\"ready\":true}}");

            // ① 계획만: 한국어·--json, 시험 루트 무변경(결과 파일만). 켜진 배포판만 탐침(Debian-Fake 꺼짐 → unknown), 포트·PS 작업·UART_HUB_DIR 경고
            r.Write(Path.Combine("fake_wsl", "l_running.txt"), "Ubuntu-Fake\n");
            r.Write("fake_ports.json", "[{\"port\":" + r.Ctl + ",\"pid\":4242,\"name\":\"node.exe\"},{\"port\":" + r.Base + ",\"pid\":4243,\"name\":\"UartHub.exe\"}]");
            r.FakeTask("uart_hub", "pwsh.exe", "-File C:\\old\\uart_hub.ps1");
            snap = InstallRig.Snapshot(r.Root);
            var p1 = r.Run(exe, "setup");
            ctx.Check(p1.Code == 0 && p1.Out.Contains("— 계획만(아무것도 바꾸지 않음") && p1.Out.Contains("# 결과: 계획만(바꾸지 않음) (exit 0)") && !p1.Out.Contains("[ui]"),
                      "setup(계획만, 한국어) → exit 0, 창 0 (" + p1.Ms + "ms)" + (p1.Code != 0 ? " — " + Upd.Tail(p1.Out) : ""));
            ctx.Check(p1.Out.Contains("  MotW(Zone.Identifier): 있음(ZoneId=3") && p1.Out.Contains("  로그 위치: " + expRoot + " (D: 이동식") && p1.Out.Contains("  허브 폴더: " + hub0)
                      && p1.Out.Contains("  실행 중 허브: 없음") && p1.Out.Contains("  옛 설치 방식 잔재: 없음") && p1.Out.Contains("  등록(install.json): 없음(처음 등록)") && p1.Out.Contains("  exe: " + exe),
                      "계획 보고: exe·MotW(ZoneId=3, 지우지 않음)·로그 위치·허브 폴더·실행 중 허브 없음·옛 잔재 없음·처음 등록");
            ctx.Check(p1.Out.Contains("  skills Windows: 넣을 예정") && p1.Out.Contains("  skills WSL Ubuntu-Fake: 넣을 예정") && p1.Out.Contains("  skills WSL Debian-Fake: 확인 안 함(꺼진 배포판"),
                      "skills 넣을 곳: Windows·켜진 WSL 배포판(Ubuntu-Fake, .claude 있음) — 꺼진 배포판(Debian-Fake)은 계획에서 깨우지 않음(확인 안 함)");
            ctx.Check(p1.Out.Contains("  - install.json 쓰기(portable:true, exe=" + exe) && p1.Out.Contains("  - 감독 기동(이 exe 그대로)") && p1.Out.Contains("  - 토큰 만들기")
                      && p1.Out.Contains("#   다음: 이대로 등록하려면 같은 exe 로 setup --yes") && p1.Out.Contains("#   다음: exe 에 MotW"),
                      "할 일(install.json·토큰·감독 기동·skills)과 다음 안내(setup --yes·MotW 차단 해제는 사용자)");
            var p2 = r.Run(exe, new[] { "setup", "--json" }, Upd.Env("UART_HUB_DIR", @"C:\elsewhere\hub"));
            var jp = JsonOut(ctx, r, p2, "계획 --json");
            var mw = jp.TryGetProperty("motw", out var m0) ? m0 : default;
            ctx.Check(p2.Code == 0 && J.S(jp, "mode") == "plan" && J.S(jp, "result") == "planned" && SystemAccess.SamePath(J.S(jp, "exe"), exe) && SystemAccess.SamePath(J.S(jp, "hub_dir"), hub0)
                      && mw.ValueKind == JsonValueKind.Object && J.B(mw, "present") == true && J.S(mw, "zone_id") == "3" && J.IsNull(jp, "running") && J.IsNull(jp, "install")
                      && (J.A(jp, "legacy")?.Length ?? -1) == 0 && J.B(jp, "started") == false && (J.A(jp, "actions")?.Length ?? 0) >= 5
                      && Skills(jp) == "windows=install, Ubuntu-Fake=install, Debian-Fake=unknown",
                      "계획 --json: mode plan·planned·exe·hub_dir·motw ZoneId 3·running null·install null·legacy 0·started false·할 일 " + (J.A(jp, "actions")?.Length ?? 0) + "개·skills " + Skills(jp));
            var warn = J.A(jp, "warnings") ?? new string[0];
            ctx.Check(warn.Any(w => w.Contains("포트 " + r.Ctl) && w.Contains("node.exe")) && !warn.Any(w => w.Contains("포트 " + r.Base)) && warn.Any(w => w.Contains("\\uart_hub"))
                      && warn.Any(w => w.Contains("UART_HUB_DIR=")) && J.B(jp, "stopped") == false && J.IsNull(jp, "downgrade"),
                      "계획 경고: 다른 프로그램이 쥔 제어 포트 " + r.Ctl + "(UartHub 자신이 쥔 " + r.Base + " 은 경고 안 함)·PS 판 작업 \\uart_hub·UART_HUB_DIR, stopped false, downgrade null (" + warn.Length + "개)");
            string d1 = DiffExcept(snap, InstallRig.Snapshot(r.Root), Last(r), r.InstallDir);
            ctx.Check(d1 == "", "계획만: 시험 루트 무변경(결과 파일 setup_last.json 과 그 폴더 말고 — 허브 폴더·skills·WSL 홈 그대로)" + (d1 == "" ? "" : ": " + d1));
            File.Delete(Path.Combine(r.Root, "fake_ports.json"));
            Directory.Delete(Path.Combine(r.Root, "fake_tasks"), true);
            var bg = r.Run(exe, new[] { "setup", "--json" }, Upd.Env("UART_HUB_TEST_SETUP_BUDGET_S", "0.2"));
            var jbg = JsonOut(ctx, r, bg, "시간 예산");
            var ub = jbg.TryGetProperty("skills", out var sb) ? sb.EnumerateArray().FirstOrDefault(x => J.S(x, "distro") == "Ubuntu-Fake") : default;
            ctx.Check(bg.Code == 0 && ub.ValueKind == JsonValueKind.Object && J.S(ub, "result") == "failed" && (J.S(ub, "note") ?? "").StartsWith("budget", StringComparison.Ordinal),
                      "setup 시간 예산(시험 0.2초)을 넘으면 남은 배포판은 탐침하지 않고 failed 'budget…': " + Skills(jbg));

            // ①-2 처음 등록의 건강 확인 실패(시험 주입 health) → 되돌림(rolled_back): 띄운 허브를 내리고 install.json 지움, skills 넣기 전
            var hf = r.Run(exe, new[] { "setup", "--yes", "--json" }, Upd.Env("UART_HUB_TEST_FAIL", "health"));
            var jh = JsonOut(ctx, r, hf, "처음 등록 건강 확인 실패");
            ctx.WaitUntil(() => r.Procs().Count == 0, 20000, "처음 등록 실패 정리 뒤 이 시험 루트의 UartHub.exe 0");
            ctx.Check(hf.Code == 1 && J.S(jh, "result") == "rolled_back" && J.B(jh, "hub_running") == false && J.B(jh, "started") == true && !File.Exists(r.InstallJson)
                      && !File.Exists(PortableScenarios.WinSkill(r, "uart")) && (J.A(jh, "next") ?? new string[0]).Any(x => x.Contains("다시 실행하지 않음")),
                      "처음 등록 건강 확인 실패 → exit 1 rolled_back(허브 내림·install.json 없음·skills 안 넣음·다음 = 담당자, 다시 실행하지 않음)" + (hf.Code != 1 ? " — " + Upd.Tail(hf.Out) : ""));

            // ② --yes --json 첫 등록 — 작업 개체(KILL_ON_JOB_CLOSE) 안에서 실행하고 작업을 닫아도 띄운 감독·코어가 남는지(CREATE_BREAKAWAY_FROM_JOB)
            var yj = RunInJob(r, exe, "setup", "--yes", "--json");
            var y = (yj.Code, yj.Out, "", yj.Ms);
            var jy = JsonOut(ctx, r, y, "첫 등록");
            Thread.Sleep(1000);
            ctx.Check(yj.Assigned && PortableScenarios.Sups(r).Count == 1 && r.Procs().Any(x => x.Cmd.Contains(" core ")),
                      "작업 개체(KILL_ON_JOB_CLOSE·BREAKAWAY_OK) 안에서 setup --yes → 작업을 닫은 뒤에도 감독·코어 살아 있음(작업 이탈 기동)");
            var hy = jy.TryGetProperty("health", out var h0) ? h0 : default;
            ctx.Check(y.Code == 0 && J.S(jy, "result") == "registered" && J.S(jy, "mode") == "apply" && J.B(jy, "started") == true && hy.ValueKind == JsonValueKind.Object && J.B(hy, "ok") == true
                      && J.B(jy, "hub_running") == true && J.N(jy, "control_port") == r.Ctl,
                      "setup --yes --json → exit 0 registered·started·제어 포트 " + r.Ctl + " 응답(" + y.Ms + "ms)" + (y.Code != 0 ? " — " + Upd.Tail(y.Out) : ""));
            var iv = PortableScenarios.Info(r);
            string hub = PortableScenarios.HubOf(r), id = HubFiles.Str(iv, "install_id");
            ctx.Check(iv.ValueKind == JsonValueKind.Object && SystemAccess.SamePath(HubFiles.Str(iv, "exe"), exe) && HubFiles.Str(iv, "state") == "installed" && J.B(iv, "portable") == true
                      && HubFiles.Str(iv, "version") == Hub.Version && SystemAccess.SamePath(hub, hub0) && Regex.IsMatch(id ?? "", "^[0-9a-f]{32}$")
                      && iv.TryGetProperty("skills", out var sk) && sk.ValueKind == JsonValueKind.Array && sk.GetArrayLength() == 3,
                      "install.json: exe = 그 exe(복사 없음) · state=installed · portable:true · version · hub_dir · install_id · skills[Windows·Ubuntu-Fake·Debian-Fake]");
            string cfgPath = Path.Combine(hub, "config.json"), tokPath = Path.Combine(hub, "token"), ilog = Path.Combine(hub, "install.log");
            ctx.Check(TokenFile.IsValid(tokPath) && File.ReadAllText(cfgPath) == Config.WithLogLayout(Seed(r), out _) && Directory.Exists(Path.Combine(hub, Config.SpoolDirName, "live"))
                      && !Directory.Exists(Path.Combine(expRoot, "live")) && File.Exists(ilog),
                      "허브 폴더: 토큰(32 hex)·config.json(on_demand, 시험 seed + \"log_layout\": 3 — C.12)·임시기록\\live·install.log");
            string ilt = HubFiles.ReadShared(ilog) ?? "";
            ctx.Check(Regex.IsMatch(ilt, @"sup --icons-dir .* → pid=\d+ \(분리 기동\)") && !ilt.Contains("작업 이탈 못 함"), "install.log: 감독은 작업 이탈로 분리 기동(대체 경로 없음)");
            var sj = HubFiles.ReadJson(HubFiles.SupState(hub));
            ctx.Check(sj.HasValue && HubFiles.Str(sj.Value, "state") == "running" && SystemAccess.SamePath(HubFiles.Str(sj.Value, "exe"), exe), "sup.json running · exe = 그 exe");
            PortableScenarios.ProcsFrom(ctx, r, exe, "첫 등록");
            ctx.WaitUntil(() => Upd.HubLog(hub).Contains("[sup] 설치 상태 일치"), 20000, "감독 C-14 '설치 상태 일치'(setup 이 installer mutex 를 놓은 뒤)");
            ctx.Check(Upd.Count(Upd.HubLog(hub), "Repair 시작") == 0, "C-14 Repair 0");
            PortableScenarios.WaitLs(ctx, r, exe, "첫 등록");
            PortableScenarios.WaitIcon(ctx, r, exe, "첫 등록");
            string wrap = File.Exists(PortableScenarios.WinSkill(r, "uart")) ? File.ReadAllText(PortableScenarios.WinSkill(r, "uart")) : "";
            string mk = File.Exists(PortableScenarios.WinSkill(r, ".uart_hub")) ? File.ReadAllText(PortableScenarios.WinSkill(r, ".uart_hub")) : "";
            string ww = File.Exists(WslSkill(r, "Ubuntu-Fake", "uart\\uart")) ? File.ReadAllText(WslSkill(r, "Ubuntu-Fake", "uart\\uart")) : "";
            ctx.Check(wrap.Contains("UART_HUB_EXE_DEFAULT='" + exe + "'") && mk.Contains(id) && mk.Contains(Hub.Version)
                      && File.Exists(Path.Combine(r.UserProfile, ".claude", "skills", "connect-uart", "SKILL.md")) && File.Exists(Path.Combine(r.UserProfile, ".claude", "skills", "disconnect-uart", "SKILL.md")),
                      "skills(Windows): uart 래퍼 exe 줄 = 그 exe·.uart_hub = install_id·버전, connect-uart·disconnect-uart");
            ctx.Check(ww.Contains("UART_HUB_EXE_DEFAULT='" + exe + "'") && File.Exists(WslSkill(r, "Ubuntu-Fake", "connect-uart\\SKILL.md")) && File.Exists(WslSkill(r, "Ubuntu-Fake", "disconnect-uart\\SKILL.md"))
                      && !Directory.Exists(Path.Combine(r.Root, "fake_wsl", "home", "Debian-Fake")),
                      "skills(WSL): .claude 있는 Ubuntu-Fake 에 uart·connect-uart·disconnect-uart, .claude 없는 Debian-Fake 는 건드리지 않음");
            ctx.Check(r.AdsExists(exe, "Zone.Identifier") && Upd.Sha(exe) == sha0, "MotW 그대로(보고만 — 지우지 않음)·exe 바이트 그대로(" + sha0 + ")");
            NoInstallArtifacts(ctx, r, "첫 등록", exe);

            // ③ 같은 exe 로 다시 --yes → 바꾼 것 없음
            string ij0 = File.ReadAllText(r.InstallJson), cfg0 = File.ReadAllText(cfgPath), tok0 = File.ReadAllText(tokPath), il0 = HubFiles.ReadShared(ilog);
            var sk0 = InstallRig.Snapshot(Path.Combine(r.UserProfile, ".claude"), Path.Combine(r.Root, "fake_wsl"));
            var ps0 = r.Procs().Select(p => p.Pid).OrderBy(x => x).ToList();
            int balloons = Upd.Count(Upd.HubLog(hub), "풍선:");
            var a = r.Run(exe, "setup", "--yes");
            ctx.Check(a.Code == 0 && a.Out.Contains("# 결과: 바꾼 것 없음 (exit 0)") && a.Out.Contains("  실행 중 허브: pid " + PortableScenarios.Sups(r).First().Pid) && a.Out.Contains("(이 exe)")
                      && a.Out.Contains("  skills Windows: 그대로(같은 표시)") && a.Out.Contains("  skills WSL Ubuntu-Fake: 그대로(같은 표시)") && !a.Out.Contains("[ui]"),
                      "같은 exe 로 다시 setup --yes → exit 0 '바꾼 것 없음'(실행 중 = 이 exe, skills 같은 표시), 창 0 (" + a.Ms + "ms)" + (a.Code != 0 ? " — " + Upd.Tail(a.Out) : ""));
            string dk = InstallRig.Diff(sk0, InstallRig.Snapshot(Path.Combine(r.UserProfile, ".claude"), Path.Combine(r.Root, "fake_wsl")));
            ctx.Check(File.ReadAllText(r.InstallJson) == ij0 && File.ReadAllText(cfgPath) == cfg0 && File.ReadAllText(tokPath) == tok0 && HubFiles.ReadShared(ilog) == il0 && dk == "",
                      "멱등: install.json·config.json·token·install.log 바이트 그대로, skills(Windows·WSL) 무변경" + (dk == "" ? "" : ": " + dk));
            ctx.Check(r.Procs().Select(p => p.Pid).OrderBy(x => x).SequenceEqual(ps0) && Upd.Count(Upd.HubLog(hub), "풍선:") == balloons,
                      "멱등: 감독·코어 PID 그대로(" + string.Join(",", ps0) + "), 풍선 0(이미 실행 중 알림도 없음)");
            var ja = Upd.Parse(File.ReadAllText(Last(r)));
            ctx.Check(J.S(ja, "result") == "unchanged" && J.S(ja, "mode") == "apply" && (J.A(ja, "actions")?.Length ?? -1) == 0 && J.B(ja, "started") == false && J.B(ja, "hub_running") == true,
                      "setup_last.json: unchanged·할 일 0·started false·hub_running true");

            // ③-2 정지(control.json stopped) 보고
            var hs = r.Run(exe, "cli", "hub", "stop", "--json");
            var ps = r.Run(exe, "setup", "--json");
            var jps = JsonOut(ctx, r, ps, "정지 상태 계획");
            ctx.Check(hs.Code == 0 && ps.Code == 0 && J.B(jps, "stopped") == true && (J.A(jps, "next") ?? new string[0]).Any(x => x.Contains("트레이 아이콘 → 시작")) && ps.Out.Length > 0,
                      "hub stop 뒤 계획: stopped true·다음 '트레이 아이콘 → 시작'");
            var hst = r.Run(exe, "cli", "hub", "start", "--json");
            ctx.Check(hst.Code == 0, "hub start → 0");
            PortableScenarios.WaitIcon(ctx, r, exe, "다시 시작");

            // ④ uninstall --yes: 창 없이 휴대용 제거와 같은 범위
            string icon = PortableScenarios.Icon(r, exe, r.Base);
            var u = r.Run(exe, "uninstall", "--yes");
            ctx.Check(u.Code == 0 && u.Out.Contains("# 제거 완료(휴대용)") && u.Out.Contains("--yes: 확인 창 없이 진행(제거)") && !u.Out.Contains("[ui]") && u.Out.Contains("감독 요청 shutdown") && u.Out.Contains("허브 정지 완료"),
                      "uninstall --yes → exit 0, 확인 창 없이 shutdown(C-5)으로 허브 정지, 창 0 (" + u.Ms + "ms)" + (u.Code != 0 ? " — " + Upd.Tail(u.Out) : ""));
            ctx.WaitUntil(() => r.Procs().Count == 0, 10000, "제거 뒤 이 시험 루트의 UartHub.exe 0");
            ctx.Check(!File.Exists(r.InstallJson) && icon != null && !File.Exists(icon) && !File.Exists(PortableScenarios.WinSkill(r, "uart"))
                      && !Directory.Exists(Path.Combine(r.UserProfile, ".claude", "skills", "connect-uart")) && !File.Exists(WslSkill(r, "Ubuntu-Fake", "uart\\uart"))
                      && !File.Exists(WslSkill(r, "Ubuntu-Fake", "connect-uart\\SKILL.md")),
                      "install.json·바탕화면 허브 아이콘·skills(Windows·WSL, connect-uart 포함) 삭제");
            ctx.Check(File.Exists(exe) && Upd.Sha(exe) == sha0 && r.AdsExists(exe, "Zone.Identifier") && TokenFile.IsValid(tokPath) && File.Exists(cfgPath)
                      && Directory.GetFiles(hub, "uninstall_*.log").Length >= 1 && !HubFiles.IsStopped(hub),
                      "exe(바이트·MotW 그대로)·로그·허브 폴더(token·config·uninstall_<시각>.log) 남김, control.json stopped 아님");
            var u2 = r.Run(exe, "uninstall", "--yes");
            ctx.Check(u2.Code == 3 && u2.Out.Contains("설치되어 있지 않음") && !u2.Out.Contains("[ui]") && File.Exists(Last(r)),
                      "등록이 없을 때(%LOCALAPPDATA%\\uart_hub 에 setup_last.json 만) uninstall --yes → exit 3 '설치되어 있지 않음', 결과 파일 그대로 (rc=" + u2.Code + ")");

            // ⑤ --yes --no-start: 등록만(프로세스 0)
            var n = r.Run(exe, "setup", "--yes", "--no-start", "--json");
            var jn = JsonOut(ctx, r, n, "--no-start");
            ctx.Check(n.Code == 0 && J.S(jn, "result") == "registered" && J.B(jn, "started") == false && J.IsNull(jn, "health") && J.B(jn, "hub_running") == false
                      && r.Procs().Count == 0 && File.Exists(r.InstallJson) && File.Exists(PortableScenarios.WinSkill(r, "uart")),
                      "setup --yes --no-start → registered·started false·프로세스 0, install.json·skills 있음 (" + n.Ms + "ms)" + (n.Code != 0 ? " — " + Upd.Tail(n.Out) : ""));

            // ⑥ 휴대용 등록 + 옛 설치 방식 잔재 → setup 막힘(잔재별 안내) → uninstall --yes(휴대용 제거)가 잔재까지 치움 → setup 막힘 없음
            File.WriteAllText(r.InstalledExe, "MZ 옛 설치본 자리\n");
            Directory.CreateDirectory(Path.GetDirectoryName(r.StartupLnk));
            ShellLink.Save(r.StartupLnk, new ShellLinkInfo { Target = r.InstalledExe, Arguments = "sup", Description = "UART 허브 감독(로그온 때 자동 실행) " + ShellLink.Marker("fedcba9876543210fedcba9876543210") });
            var lb = r.Run(exe, "setup", "--json");
            var jlb = JsonOut(ctx, r, lb, "휴대용 + 옛 잔재");
            var nx = J.A(jlb, "next") ?? new string[0];
            ctx.Check(lb.Code == 3 && J.S(jlb, "result") == "blocked" && (J.A(jlb, "legacy")?.Length ?? 0) == 2 && nx.Any(x => x.Contains("설치 폴더의 exe")) && nx.Any(x => x.Contains("바로가기"))
                      && nx.Any(x => x.Contains("그 뒤에도 같은 잔재로 막히면 멈추고 보고")),
                      "휴대용 등록 + 옛 잔재(설치본·표시 바로가기) → blocked, 잔재별 다음 안내·'그래도 막히면 멈추고 보고'");
            var lu2 = r.Run(exe, "uninstall", "--yes");
            ctx.Check(lu2.Code == 0 && lu2.Out.Contains("# 제거 완료(휴대용)") && !File.Exists(r.StartupLnk) && !File.Exists(r.InstalledExe) && !File.Exists(r.InstallJson) && !lu2.Out.Contains("[ui]"),
                      "휴대용 uninstall --yes 가 옛 잔재(표시 바로가기·설치본 exe)까지 치움 (rc=" + lu2.Code + ")" + (lu2.Code != 0 ? " — " + Upd.Tail(lu2.Out) : ""));
            var la = r.Run(exe, "setup", "--json");
            var jla = JsonOut(ctx, r, la, "잔재 정리 뒤");
            ctx.Check(la.Code == 0 && J.S(jla, "result") == "planned" && (J.A(jla, "legacy")?.Length ?? -1) == 0, "잔재 정리 뒤 setup 계획 = 막힘 없음(planned)");

            NoInstallArtifacts(ctx, r, "setup", exe);
            r.CheckNoProcs("정리");
            real.Verify(ctx, r);
        }

        // ================================================================== setup-replace
        [Scenario("setup-replace", "setup 교체(X7 부록 D.3, C-13): A(ToolsA, vA)를 setup --yes 로 등록·실행 중 B(ToolsB, vB) 계획 = 실행 중 A(exe·버전)·교체 할 일, 무변경(A 그대로). B setup --yes 의 건강 확인 실패 → rolled_back(B 내림·install.json·아이콘 = A·A 감독 다시 켬, skills 그대로). B setup --yes = 확인 창 없이 update mutex → shutdown(C-5)으로 A 를 내리고(검증 PID kill 없음) B 의 sup, hub status version == supervisor.version == core_version == vB, install.json exe = B·version = vB·install_id 그대로, 포트·token·config·state(assign·released) 그대로, 로그 파일 그대로(log_root 같음, 이전 파일 남음), 바탕화면 아이콘 대상 A → B(같은 파일), skills 래퍼 = B(Windows·WSL)·.uart_hub vB. 오래된 A 의 setup --yes = exit 3 downgrade(무변경), 허브 끈 뒤 A 를 인자 없이 = 등록된 B 로 켬(등록 그대로), --yes --no-start --allow-downgrade = B 를 끄고 A 로 등록만. 시작프로그램·시작 메뉴 0·자기 복사 0. 실제 시스템 무변경",
                  Order = 1401, TimeoutS = 300)]
        public static void Replace(ScenarioContext ctx)
        {
            var real = RealSystem.Capture(ctx);
            var r = Rig(ctx, 18820);
            ctx.Check(File.Exists(Upd.VB), "두 번째 빌드 있음: " + Upd.VB);
            string exeA = Place(r, Path.Combine("ToolsA", "UartHub"), Dev, false), exeB = Place(r, Path.Combine("ToolsB", "UartHub"), Upd.VB, false);
            string vA = Hub.Version, vB = r.Run(exeB, "version").Out.Trim();
            ctx.Check(vB.Length == 17 && vB != vA, "vB = 버전 문자열만 다른 빌드(vA " + vA + " → vB " + vB + ")");

            // A 등록
            var a = r.Run(exeA, "setup", "--yes", "--json");
            var ja = JsonOut(ctx, r, a, "A 등록");
            ctx.Check(a.Code == 0 && J.S(ja, "result") == "registered" && J.B(ja, "started") == true, "A setup --yes → registered (" + a.Ms + "ms)" + (a.Code != 0 ? " — " + Upd.Tail(a.Out) : ""));
            string hub = PortableScenarios.HubOf(r), id = HubFiles.Str(PortableScenarios.Info(r), "install_id"), logRoot = Upd.LogRoot(r);
            ctx.WaitUntil(() => Upd.HubLog(hub).Contains("[sup] 설치 상태 일치"), 20000, "A: 감독 C-14 '설치 상태 일치'");
            PortableScenarios.WaitLs(ctx, r, exeA, "A");
            string iconA = PortableScenarios.WaitIcon(ctx, r, exeA, "A");
            var supA = PortableScenarios.Sups(r).First();
            string keep0 = Upd.Keep(hub), cfg0 = File.ReadAllText(Path.Combine(hub, "config.json"));
            var logs0 = LogFiles.All(Path.Combine(hub, Config.SpoolDirName));                   // C.12: log_layout 3 = <허브 폴더>\임시기록
            ctx.Check(keep0.Contains("assign={" + r.Cable + "=" + r.Base + "}") && logs0.Any(x => x.EndsWith(".log", StringComparison.Ordinal)), "A: 포트 배정 " + r.Cable + "=" + r.Base + "·케이블 로그 " + logs0.Count + "개");

            // B 계획만: 실행 중 A·교체 할 일, 무변경
            var snap = InstallRig.Snapshot(r.InstallDir, Path.Combine(r.UserProfile, ".claude"), r.Desktop, Path.Combine(r.Root, "fake_wsl"));
            var p = r.Run(exeB, "setup", "--json");
            var jp = JsonOut(ctx, r, p, "B 계획");
            var run = jp.TryGetProperty("running", out var rn) ? rn : default;
            ctx.Check(p.Code == 0 && J.S(jp, "result") == "planned" && run.ValueKind == JsonValueKind.Object && SystemAccess.SamePath(J.S(run, "exe"), exeA) && J.S(run, "version") == vA
                      && J.B(run, "same_exe") == false && J.N(run, "pid") == supA.Pid && (J.A(jp, "actions") ?? new string[0]).Any(x => x.StartsWith("실행 중 허브(pid " + supA.Pid, StringComparison.Ordinal) && x.Contains("이 exe 로 다시 켬")),
                      "B 계획: 실행 중 허브 = A(pid·exe·vA, 다른 exe)·할 일 '실행 중 허브 끄기 → 이 exe 로 다시 켬'");
            string d0 = DiffExcept(snap, InstallRig.Snapshot(r.InstallDir, Path.Combine(r.UserProfile, ".claude"), r.Desktop, Path.Combine(r.Root, "fake_wsl")),
                                   Last(r), Path.Combine(r.InstallDir, "icons.json"));            // icons.json 은 돌고 있는 A 감독의 것
            ctx.Check(d0 == "" && PortableScenarios.Sups(r).Select(x => x.Pid).SequenceEqual(new[] { supA.Pid }), "B 계획만: A 의 감독 그대로·install.json·skills·바탕화면 무변경" + (d0 == "" ? "" : ": " + d0));

            // B --yes 인데 새 감독의 건강 확인 실패(시험 주입 health) → 되돌림(H1): B 를 내리고 install.json·아이콘을 A 로, A 의 감독을 다시 켬
            var hfail = r.Run(exeB, new[] { "setup", "--yes", "--json" }, Upd.Env("UART_HUB_TEST_FAIL", "health"));
            var jf = JsonOut(ctx, r, hfail, "교체 건강 확인 실패");
            ctx.Check(hfail.Code == 1 && J.S(jf, "result") == "rolled_back" && J.B(jf, "hub_running") == true && (J.S(jf, "message") ?? "").Contains("이전 허브 다시 켬"),
                      "B 교체의 건강 확인 실패 → exit 1 rolled_back, 이전 허브(A) 다시 켬 (" + hfail.Ms + "ms)" + (hfail.Code != 1 ? " — " + Upd.Tail(hfail.Out) : ""));
            PortableScenarios.ProcsFrom(ctx, r, exeA, "되돌림 뒤");
            var ivr = PortableScenarios.Info(r);
            ctx.Check(SystemAccess.SamePath(HubFiles.Str(ivr, "exe"), exeA) && HubFiles.Str(ivr, "version") == vA && HubFiles.Str(ivr, "install_id") == id
                      && SystemAccess.SamePath(PortableScenarios.Icon(r, exeA, r.Base), iconA) && PortableScenarios.Icon(r, exeB, r.Base) == null
                      && File.ReadAllText(PortableScenarios.WinSkill(r, "uart")).Contains("UART_HUB_EXE_DEFAULT='" + exeA + "'") && Upd.Keep(hub) == keep0,
                      "되돌림: install.json exe = A·vA·install_id 그대로, 아이콘 대상 A, skills 는 A 그대로(교체 실패 전에는 안 넣음), 포트·token·config 그대로");
            var stA = r.Run(exeA, "cli", "hub", "status", "--json");
            var jstA = Upd.Parse(stA.Out);
            ctx.Check(stA.Code == 0 && J.S(jstA, "version") == vA, "되돌림 뒤 hub status version = vA");
            PortableScenarios.WaitLs(ctx, r, exeA, "되돌림 뒤");
            supA = PortableScenarios.Sups(r).First();

            // B --yes: 확인 창 없이 A 를 내리고 B
            var u = r.Run(exeB, "setup", "--yes");
            ctx.Check(u.Code == 0 && u.Out.Contains("# 결과: 바꿈(기존 허브 → 이 exe) (exit 0)") && !u.Out.Contains("[ui]"),
                      "B setup --yes → exit 0 '바꿈', 확인 창 0 (" + u.Ms + "ms)" + (u.Code != 0 ? " — " + Upd.Tail(u.Out) : ""));
            ctx.Check(u.Out.Contains("감독 요청 shutdown") && u.Out.Contains("허브 정지 완료") && !u.Out.Contains("검증 PID kill pid"), "A: 감독 요청 shutdown(C-5)으로 정상 종료(검증 PID kill 없음)");
            ctx.Check(u.Out.Contains("모두 v" + vB), "제어 포트 응답: hub status version == supervisor.version == core_version == vB");
            PortableScenarios.ProcsFrom(ctx, r, exeB, "교체 뒤");
            var iv = PortableScenarios.Info(r);
            ctx.Check(SystemAccess.SamePath(HubFiles.Str(iv, "exe"), exeB) && HubFiles.Str(iv, "version") == vB && J.B(iv, "portable") == true && HubFiles.Str(iv, "install_id") == id
                      && SystemAccess.SamePath(HubFiles.Str(iv, "hub_dir"), hub) && SystemAccess.SamePath(HubFiles.Str(iv, "log_root"), logRoot),
                      "install.json: exe = B · version = vB · portable · install_id·hub_dir·log_root 그대로");
            ctx.Check(!HubFiles.IsStopped(hub), "control.json stopped 아님");
            ctx.Check(u.Out.Contains("아이콘 대상 바꿈") && PortableScenarios.Icon(r, exeA, r.Base) == null && SystemAccess.SamePath(PortableScenarios.Icon(r, exeB, r.Base), iconA),
                      "바탕화면 아이콘: 같은 파일의 대상이 A → B");
            PortableScenarios.WaitLs(ctx, r, exeB, "B");
            string keep1 = Upd.Keep(hub);
            ctx.Check(keep1 == keep0 && File.ReadAllText(Path.Combine(hub, "config.json")) == cfg0, "포트·설정 유지: token·config(바이트)·state assign·released 그대로 (" + keep1 + ")");
            var logs1 = LogFiles.All(Path.Combine(hub, Config.SpoolDirName));
            ctx.Check(logs0.All(x => logs1.Contains(x)), "로그 유지: A 때의 로그 파일 " + logs0.Count + "개 모두 남음(지금 " + logs1.Count + "개)");
            string wrap = File.ReadAllText(PortableScenarios.WinSkill(r, "uart")), mk = File.ReadAllText(PortableScenarios.WinSkill(r, ".uart_hub"));
            string ww = File.ReadAllText(WslSkill(r, "Ubuntu-Fake", "uart\\uart"));
            ctx.Check(wrap.Contains("UART_HUB_EXE_DEFAULT='" + exeB + "'") && mk.Contains(vB) && mk.Contains(id) && ww.Contains("UART_HUB_EXE_DEFAULT='" + exeB + "'"),
                      "skills: 래퍼 exe 줄 = B(Windows·WSL), .uart_hub version vB·install_id 그대로");
            var jl = Upd.Parse(File.ReadAllText(Last(r)));
            ctx.Check(J.S(jl, "result") == "replaced" && SystemAccess.SamePath(J.S(jl, "exe"), exeB) && J.B(jl, "started") == true && J.B(jl, "hub_running") == true,
                      "setup_last.json: replaced·exe = B·started·hub_running");

            // 되돌리기 막힘: 실행 중·등록된 B(vB)보다 오래된 A(vA)의 setup --yes → exit 3·무변경, downgrade{from vB, to vA, allowed false}
            string ijB = File.ReadAllText(r.InstallJson);
            var supB = PortableScenarios.Sups(r).Select(x => x.Pid).ToList();
            var dg = r.Run(exeA, "setup", "--yes", "--json");
            var jd = JsonOut(ctx, r, dg, "되돌리기 막힘");
            var dgo = jd.TryGetProperty("downgrade", out var dd) ? dd : default;
            ctx.Check(dg.Code == 3 && J.S(jd, "result") == "blocked" && dgo.ValueKind == JsonValueKind.Object && J.S(dgo, "from") == vB && J.S(dgo, "to") == vA && J.B(dgo, "allowed") == false
                      && (J.A(jd, "next") ?? new string[0]).Any(x => x.Contains("--allow-downgrade")) && File.ReadAllText(r.InstallJson) == ijB
                      && PortableScenarios.Sups(r).Select(x => x.Pid).SequenceEqual(supB),
                      "더 오래된 A 의 setup --yes → exit 3 blocked(downgrade vB→vA, allowed false, 다음 = 사용자에게 묻고 --allow-downgrade)·install.json·B 감독 그대로");

            // 옛 exe 더블클릭(예: 옛 exe 를 가리키는 shell:startup 바로 가기로 로그온) → 등록을 바꾸지 않고 등록된 새 판(B)으로 켬
            SupervisorChannel.Request(hub, "shutdown", "tray", 10000);
            ctx.WaitUntil(() => r.Procs().Count == 0, 20000, "종료(허브 끄기): 감독·코어 0");
            var old = r.Run(exeA, NoArgs);
            ctx.Check(old.Code == 0 && !old.Out.Contains("[ui]") && old.Out.Contains("등록은 그대로 두고 등록된 exe 로 켬") && File.ReadAllText(r.InstallJson) == ijB,
                      "옛 A 를 인자 없이 실행 → exit 0, 창 0, 등록된 B 로 켬·install.json 그대로 (" + old.Ms + "ms)" + (old.Code != 0 ? " — " + Upd.Tail(old.Out) : ""));
            PortableScenarios.ProcsFrom(ctx, r, exeB, "옛 exe 실행 뒤");

            // --allow-downgrade + --no-start 교체: 돌던 B 를 끄고 A 로 등록만(띄우지 않음)
            var nd = r.Run(exeA, "setup", "--yes", "--no-start", "--allow-downgrade", "--json");
            var jn = JsonOut(ctx, r, nd, "되돌리기 --no-start");
            ctx.WaitUntil(() => r.Procs().Count == 0, 20000, "--no-start 교체 뒤 감독·코어 0");
            var dn = jn.TryGetProperty("downgrade", out var dd2) ? dd2 : default;
            var ivn = PortableScenarios.Info(r);
            ctx.Check(nd.Code == 0 && J.S(jn, "result") == "replaced" && J.B(jn, "started") == false && J.B(jn, "hub_running") == false && dn.ValueKind == JsonValueKind.Object && J.B(dn, "allowed") == true
                      && SystemAccess.SamePath(HubFiles.Str(ivn, "exe"), exeA) && HubFiles.Str(ivn, "version") == vA && SystemAccess.SamePath(PortableScenarios.Icon(r, exeA, r.Base), iconA)
                      && File.ReadAllText(PortableScenarios.WinSkill(r, "uart")).Contains("UART_HUB_EXE_DEFAULT='" + exeA + "'"),
                      "setup --yes --no-start --allow-downgrade → replaced·started false·프로세스 0, install.json·아이콘·skills = A(vA)" + (nd.Code != 0 ? " — " + Upd.Tail(nd.Out) : ""));

            NoInstallArtifacts(ctx, r, "교체", exeA, exeB);
            r.CheckNoProcs("정리");
            real.Verify(ctx, r);
        }
    }
}
