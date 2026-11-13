// 설치·업데이트·제거·복구(계획 §4 T2 "첫 설치 1~8"·"업데이트 0~5"·"되돌림"·"제거", C-8·C-13·C-14). install.ps1 의 토큰·ACL·사전 점검·바로가기 규칙을 C# 로 옮겼다.
//   (인자 없음)                    휴대용 실행(X4, 2026-11-05 — 백신 행동 탐지 PDM 이 설치기의 자기 복사→복사본 실행→시작프로그램 등록을 드로퍼로 봄):
//                                  exe 를 그 자리에서 쓴다. 자기 복사·다른 위치 실행·바로가기(시작프로그램·시작 메뉴)·작업 스케줄러 없음.
//                                  처음 한 번만 확인 창(로그 위치·skills·자동 실행 안내) → 허브 폴더·토큰·config → install.json(exe=이 exe, portable:true) → sup → skills.
//                                  다음부터는 창 없이 install.json 의 exe·version 만 맞추고(바뀌었으면 바탕화면 아이콘 대상·skills 도) sup 을 띄운다.
//                                  감독이 이미 있으면: 같은 exe = 트레이 풍선 "이미 실행 중"만, 다른 exe(새 판) = 확인 창 [예] → shutdown(C-5) → 새 exe 의 sup.
//                                  설치 방식(portable 아님) install.json 이 있으면 먼저 제거하라고 안내하고 멈춘다.
//   UartHub.exe install            첫 설치(명시적 하위 명령으로만). 설치본(install.json)이 있으면 업데이트(같은 버전 = 복구, 더 오래된 판은 확인 창에 표시)
//   UartHub.exe install --dry-run  모든 단계를 출력만 한다(파일·mutex·바로가기·작업 무변경). 사전 점검은 실제로 읽는다(WMI·wsl -l -q·schtasks /query)
//   UartHub.exe install --repair [--quiet]  확인 창 없이 멱등 재적용(install.json·시작프로그램·시작 메뉴·옛 이미지 정리). update mutex 를 잡지 않고 sup 도 띄우지 않는다
//                                  (부르는 쪽 — 되돌림 중인 설치기·감독의 C-14 — 이 이미 쥐고 있거나 직접 띄운다)
//   UartHub.exe uninstall [--dry-run]  install.json 이 portable 이면 아이콘·skills(표시가 맞는 것)·install.json·icons.json 만 정리(exe·로그·폴더는 남김)
//   UartHub.exe uninstall --yes    확인 창 없이 같은 범위로 제거(X7 — Claude 가 INSTALL_UART_HUB.md 를 따라 부름)
//   UartHub.exe setup [--yes] [--json] [--no-start]   X7(부록 D, 2026-11-06 — 배포 폴더 + INSTALL_UART_HUB.md 를 Claude 가 따라 등록, 사람의 더블클릭·확인 창 없음):
//                                  --yes 없으면 계획만(바꾸지 않음): exe·로그 위치·허브 폴더·skills 넣을 곳(Windows + .claude 있는 WSL 배포판, 읽기 탐침)·실행 중 허브(버전·exe)·
//                                  옛 설치 방식 잔재·MotW(Zone.Identifier — 보고만, 지우지 않음)·할 일. --yes = 휴대용 첫 실행과 같은 등록을 창 없이(install.json portable·exe=이 exe,
//                                  허브 폴더·토큰, config on_demand 보장, skills 3개). 다른 exe(또는 다른 판)의 허브가 돌면 휴대용 교체 경로(update mutex → shutdown → 새 sup)로 바꾼다
//                                  (포트·설정·로그 유지). 같은 exe·같은 판으로 다시 = 바꾸는 것 없음. 옛 설치 방식 잔재·다른 설치기·등록 안 된 감독 = 무변경 중단(exit 3).
//                                  --no-start 가 아니면 감독 기동 → 제어 포트 응답(hub status, 최대 30초). 결과는 표준 출력(--json = JSON 한 줄, 아니면 한국어 줄)과
//                                  %LOCALAPPDATA%\uart_hub\setup_last.json(같은 JSON — Git Bash 에서 WinExe 출력이 사라지는 경우 대비. 계획만일 때도 이 파일만 쓴다).
// 업데이트: 0 잔재 점검(UartHub.exe 없음·new·old·bad) → 1 확인 창 "vA → vB" · 공백 전(update mutex, UartHub.new.exe 스트림 복사·version --json 검증)
//   → 2 공백 시작(감독 shutdown → 무응답이면 core.stop → 그래도 안 되면 검증 PID kill) → 3 File.Replace(재시도 20×250ms, 1176·1177 처리)
//   → 4 update mutex 닫기 → 새 sup → 건강 확인(합격선 20초·중단 임계 45초, supervisor.version == core_version == vB) → 5 공백 뒤(멱등 재실행·install.json version·old 삭제).
//   4 실패 → 되돌림(bad 로 빼고 old 를 되돌린 뒤 옛 exe 의 install --repair --quiet → 옛 sup). 5 실패는 경고만(감독 C-14 가 마저 맞춤).
// Claude skills(T4, SkillsDeployer): 첫 설치 7 단계(건강 확인 뒤)·업데이트 5 단계(공백 뒤 — 배포판 탐침이 COM 공백에 들어가지 않음)·Repair 에서 넣거나 갱신하고
//   결과를 install.json skills[] 와 설치 요약에 남긴다. 제거 8 단계는 .uart_hub 의 install_id 가 같은 곳만 지운다. dry-run 은 배포판을 깨우지 않는다(이름만).
// 시험 주입(C-13 에서만): UART_HUB_TEST_FAIL = health · shutdown · core.stop · replace · replace-1176[-once] · replace-1177[-once] · sup-start · rollback-rename · old-sup-start
//   (core-crash 는 코어가 읽는다), UART_HUB_TEST_DELAY = finalize · pre-replace · after-1177, UART_HUB_TEST_HEALTH_S = 중단 임계(초).
// mutex(C-8): installer mutex 는 프로세스 시작 직후 잡아 끝날 때까지 쥔다(이미 있으면 기존 설치 창을 앞으로 가져오고 exit 0).
//   update mutex 는 exe 복사부터 감독 기동 직전까지(첫 설치·업데이트 공백), 되돌림은 다시 잡는다, 제거는 처음부터 끝까지. 놓을 때는 ReleaseMutex 뒤 핸들째 닫는다.
// 시스템 접근은 모두 SystemAccess(C-13)를 거친다 — 이 파일에는 Environment.GetFolderPath·Process.Start·File.* 직접 호출이 없다.
// 종료코드: 0 성공·중복 실행 · 1 실패(정리·되돌림함) · 2 취소 · 3 사전 점검 중단 · 106 인자 오류
//   setup·uninstall --yes: 0 성공(계획만·등록·교체·맞춤·바꾼 것 없음 / 제거함) · 1 실패 · 3 중단(옛 잔재·다른 설치기 실행 중·등록 안 된 감독 / 등록되어 있지 않음) · 106 인자 오류.
//   setup 계획만(--yes 없음)도 실제 실행이 막힐 상황이면 3.
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

namespace UartHub
{
    /// <summary>
    /// 설치기 화면(app/InstallForm.cs). 확인 창은 한 번뿐이다. 진행 창은 단계(Steps·Stage)를 보이고, install.log 줄(Detail)은 "자세히" 안에만 둔다(UI_COPY.md).
    /// 글은 UiText(머리·본문·요약 줄·자세히). 예전 방식(제목 + 줄 목록, 한 덩어리 문구) 호출은 InstallUiCompat 가 UiText 로 바꾼다.
    /// </summary>
    public interface IInstallUi
    {
        /// <summary>확인 창. true = 진행.</summary>
        bool Confirm(UiText t);
        /// <summary>진행 창의 머리 글과 단계 이름(없으면 진행 줄 하나를 그대로 보인다).</summary>
        void Steps(string head, params string[] names);
        /// <summary>지금 단계(앞 단계는 끝남).</summary>
        void Stage(int index);
        /// <summary>진행 줄(단계가 없을 때만 화면에 — 확인 전에는 쌓기만 한다).</summary>
        void Progress(string line);
        /// <summary>install.log 한 줄("자세히" 안).</summary>
        void Detail(string line);
        /// <summary>마지막 표시: ok 면 autoCloseMs 뒤 저절로 닫힘, 아니면 오류 창(사용자가 닫을 때까지). 닫힐 때까지 돌아오지 않는다.</summary>
        void Finish(bool ok, UiText t, int autoCloseMs);
    }

    /// <summary>예전 방식 호출(제목 + 줄 목록 · 한 덩어리 문구)을 UiText 로. 창 없는 기록은 예전 글 그대로 남는다(Raw).</summary>
    public static class InstallUiCompat
    {
        public static bool Confirm(this IInstallUi ui, string title, string okText, IReadOnlyList<string> lines) => ui.Confirm(UiText.FromLines(title, okText, lines));
        public static void Finish(this IInstallUi ui, bool ok, string message, int autoCloseMs) => ui.Finish(ok, UiText.FromMessage(message, !ok), autoCloseMs);
    }

    /// <summary>
    /// 휴대용 창의 글(UI_COPY.md "처음 시작"·"켜는 중 → 끝"·"새 버전으로 바꾸기" + 같은 원칙의 제거 창). Session 과 render-ui(app/RenderUi.cs)가 같은 글을 쓴다.
    /// 경로·버전 같은 기술 정보는 Details("자세히 ▸")로 내린다.
    /// </summary>
    public static class InstallCopy
    {
        public static string[] StepsFirst => new[] { L.T("허브 켜기", "Start the hub"), L.T("Claude 연결 정보 넣기", "Add Claude connection info"), L.T("확인", "Check") };
        public static string[] StepsReplace => new[] { L.T("켜진 허브 끄기", "Stop the running hub"), L.T("새 버전 켜기", "Start the new version"), L.T("Claude 연결 정보 넣기", "Add Claude connection info") };
        public static string[] StepsUninstall => new[] { L.T("허브 끄기", "Stop the hub"), L.T("아이콘과 Claude 연결 정보 지우기", "Remove icons and Claude connection info"), L.T("등록 정보 지우기", "Remove registration info") };
        public static string FirstHead => L.T("UART 허브를 켜는 중입니다.", "Starting UART Hub."); public static string ReplaceHead => L.T("새 버전으로 바꾸는 중입니다.", "Switching to the new version."); public static string UninstallHead => L.T("UART 허브를 제거하는 중입니다.", "Removing UART Hub.");
        public static string AutoRunHow => L.T("Win+R → shell:startup → exe를 오른쪽 버튼으로 끌어 '바로 가기 만들기'", "Win+R → shell:startup → right-drag the exe there and choose 'Create shortcuts here'");

        /// <summary>Claude 연결 정보(skills)를 넣을 곳: "Windows, WSL Ubuntu-22.04".</summary>
        public static string SkillsPlaces(bool windows, IEnumerable<string> distros)
        {
            var p = new List<string>();
            if (windows) p.Add("Windows");
            var d = (distros ?? new string[0]).ToList();
            if (d.Count > 0) p.Add("WSL " + string.Join(", ", d));
            return p.Count > 0 ? string.Join(", ", p) : L.T("찾지 못했습니다. Claude Code를 설치한 뒤 exe를 다시 실행하면 넣습니다.", "None found. Install Claude Code, then run the exe again to add them.");
        }

        public static UiText FirstRun(string logRoot, string places, string exe, string hubDir, string instJson) => new UiText
        {
            Head = L.T("UART 허브를 시작할까요?", "Start UART Hub?"),
            Body = L.T("보드 UART를 터미널과 Claude가 같이 쓰게 해 주는 프로그램입니다. 설치하지 않고 이 폴더에서 실행합니다.", "This program lets a terminal and Claude share the board UART. It is not installed; it runs from this folder."),
            Bullets = { L.T("로그 저장 위치: " + logRoot, "Log location: " + logRoot), L.T("Claude 연결 정보를 넣을 곳: " + places, "Where Claude connection info goes: " + places), L.T("COM 포트는 '/connect-uart COM3'처럼 연결할 때만 엽니다.", "COM ports are opened only when you connect, as in '/connect-uart COM3'.") },
            Details = { L.T("exe 경로: " + exe, "exe path: " + exe), L.T("허브 폴더: " + hubDir, "Hub folder: " + hubDir), L.T("설정 파일: " + instJson, "Settings file: " + instJson), L.T("자동 실행: " + AutoRunHow, "Auto start: " + AutoRunHow) },
            Ok = L.T("시작", "Start"), OkIsDefault = true,
        };

        public static UiText Ready(string logRoot, string skills) => new UiText
        {
            Head = L.T("준비됐습니다.", "Ready."),
            Body = L.T("트레이에 UART 허브 아이콘이 생겼습니다.\n아이콘이 안 보이면 작업 표시줄 오른쪽 ^ 안에 있습니다.", "The UART Hub icon is now in the tray.\nIf you do not see it, look under the ^ at the right end of the taskbar."),
            Details = { L.T("로그 저장 위치: " + logRoot, "Log location: " + logRoot), L.T("Claude 연결 정보: " + (skills ?? "-"), "Claude connection info: " + (skills ?? "-")), L.T("자동 실행: " + AutoRunHow, "Auto start: " + AutoRunHow) },
        };

        /// <summary>켜지 못함(오류 창): 로그 파일을 담당자에게 · [로그 폴더 열기]. tech = 예전 기술 문구(setup --yes 출력은 그대로).</summary>
        public static UiText Fail(string why, string logPath, string tech) => new UiText
        {
            Head = L.T("허브를 켜지 못했습니다.", "Could not start the hub."),
            Body = L.T("로그 파일을 담당자에게 보내 주세요:\n" + (logPath ?? "(로그 파일 없음)"), "Please send the log file to the person in charge:\n" + (logPath ?? "(no log file)")),
            Details = { L.T("이유: " + why, "Reason: " + why) },
            LogPath = logPath, Error = true, Tech = tech,
        };

        /// <summary>새 버전으로 바꾸기 확인 창(Enter·Esc = 취소 — 열린 터미널이 끊긴다).</summary>
        public static UiText Replace(string runExe, string runVer, string newVer, string exe) => new UiText
        {
            Head = L.T("새 버전으로 바꿀까요?", "Switch to the new version?"),
            Body = L.T("지금 켜진 허브" + (runVer != null ? "(" + runVer + ")" : "") + "를 끄고 이\u00A0버전(" + newVer + ")으로 다시 켭니다. 몇 초 멈추는 동안 열린 터미널은 연결이 끊깁니다.\n"
                                      + "포트 번호, 연결해 둔 COM, 설정, 로그는 그대로입니다.", "The hub that is running now" + (runVer != null ? " (" + runVer + ")" : "") + " will be stopped and started again as this version (" + newVer + "). Open terminals lose their connection during the few seconds it pauses.\n"
                   + "Port numbers, connected COM ports, settings and logs stay as they are."),
            Details = { L.T("지금 허브: " + (runExe ?? "(exe 경로 모름)"), "Running hub: " + (runExe ?? "(exe path unknown)")), L.T("새 exe: " + exe, "New exe: " + exe) },
            Ok = L.T("바꾸기", "Switch"),
        };

        public static UiText Replaced(string ver, string exe, string skills) => new UiText
        {
            Head = L.T("새 버전으로 바꿨습니다.", "Switched to the new version."),
            Body = L.T("끊긴 터미널은 바탕화면 보드 아이콘이나 트레이 메뉴에서 다시 열어 주세요.", "Reopen any disconnected terminals from a desktop board icon or the tray menu."),
            Details = { L.T("버전: " + ver, "Version: " + ver), L.T("exe 경로: " + exe, "exe path: " + exe), L.T("Claude 연결 정보: " + (skills ?? "그대로"), "Claude connection info: " + (skills ?? "unchanged")) },
        };

        /// <summary>휴대용 제거 확인 창(Enter·Esc = 취소).</summary>
        public static UiText Uninstall(string exe, string logRoot, string hubDir, string instJson) => new UiText
        {
            Title = Installer.UninstallTitle,
            Head = L.T("UART 허브를 제거할까요?", "Remove UART Hub?"),
            Body = L.T("허브를 끄고 바탕화면 보드 아이콘, Claude 연결 정보, 등록 정보를 지웁니다. 열린 터미널은 연결이 끊깁니다.", "This stops the hub and removes the desktop board icons, Claude connection info, and registration info. Open terminals will be disconnected."),
            Bullets = { L.T("exe와 로그는 남습니다. 필요 없으면 직접 지워 주세요.", "The exe and logs stay. Delete them yourself if you do not need them."), L.T("자동 실행 바로 가기를 만들었다면 그것도 직접 지워 주세요(Win+R → shell:startup).", "If you made an auto-start shortcut, delete it yourself too (Win+R → shell:startup).") },
            Details = { L.T("exe 경로: " + exe, "exe path: " + exe), L.T("로그 위치: " + (logRoot ?? "(모름)"), "Log location: " + (logRoot ?? "(unknown)")), L.T("허브 폴더: " + (hubDir ?? "(모름)"), "Hub folder: " + (hubDir ?? "(unknown)")), L.T("설정 파일: " + instJson, "Settings file: " + instJson) },
            Ok = L.T("제거", "Remove"),
        };

        public static UiText Uninstalled(string exe, string logs) => new UiText
        {
            Head = L.T("UART 허브를 제거했습니다.", "UART Hub was removed."),
            Body = L.T("exe와 로그는 남아 있습니다. 필요 없으면 직접 지워 주세요.", "The exe and logs are still there. Delete them yourself if you do not need them."),
            Details = { L.T("exe 경로: " + exe, "exe path: " + exe), L.T("로그 위치: " + logs, "Log location: " + logs) },
        };

        public static UiText UninstallFail(IReadOnlyList<string> failed, string logPath)
        {
            var t = new UiText
            {
                Head = L.T("제거를 다 하지 못했습니다.", "Removal did not finish."),
                Body = L.T("로그 파일을 담당자에게 보내 주세요:\n" + (logPath ?? "(로그 파일 없음)"), "Please send the log file to the person in charge:\n" + (logPath ?? "(no log file)")),
                LogPath = logPath, Error = true,
                Tech = L.T("UART 허브 제거를 다 하지 못했습니다: " + string.Join(" · ", failed) + "\n로그 파일: " + (logPath ?? "(없음)"), "UART Hub removal did not finish: " + string.Join(" · ", failed) + "\nLog file: " + (logPath ?? "(none)")),
            };
            t.Details.AddRange(failed.Select(f => L.T("실패: " + f, "Failed: " + f)));
            return t;
        }
    }

    /// <summary>창 없는 화면: 시험 모드(C-13)의 자동 응답과 --quiet. 확인·완료·오류를 install.log 에 "[ui] …"로 남긴다(새 문구는 머리 / 본문 / 요약 줄 한 줄로).</summary>
    public sealed class AutoInstallUi : IInstallUi
    {
        readonly InstallLog log;
        readonly bool answer;
        public AutoInstallUi(InstallLog log, bool answer) { this.log = log; this.answer = answer; }
        public bool Confirm(UiText t)
        {
            int n = t.Bullets.Count + t.Details.Count;
            log.Line(L.T("[ui] 확인 창(자동 응답=" + (answer ? t.Ok : t.Cancel) + "): " + t.Title + (t.Head != null ? " · " + t.Flat() : "") + " · " + n + "줄", "[ui] confirm window (auto answer=" + (answer ? t.Ok : t.Cancel) + "): " + t.Title + (t.Head != null ? " · " + t.Flat() : "") + " · " + n + " lines"));
            return answer;
        }
        public void Steps(string head, params string[] names) => log.Line(L.T("[ui] 진행 창: " + head + " 단계 " + string.Join(" → ", names), "[ui] progress window: " + head + " steps " + string.Join(" → ", names)));
        public void Stage(int index) { }
        public void Progress(string line) { }
        public void Detail(string line) { }
        public void Finish(bool ok, UiText t, int autoCloseMs)
            => log.Line(L.T("[ui] " + (ok ? "완료 창(" + (autoCloseMs / 1000) + "초 뒤 닫힘): " : "오류 창: ") + t.LogText + (t.LogPath != null && t.Raw == null ? " [로그 폴더 열기]" : ""), "[ui] " + (ok ? "done window (closes in " + (autoCloseMs / 1000) + " s): " : "error window: ") + t.LogText + (t.LogPath != null && t.Raw == null ? " [Open log folder]" : "")));
    }

    /// <summary>--yes(X7): 창 없이 진행 — 확인은 그 자리에서 '예'(등록·제거 요청 자체가 동의), 실패 안내는 로그 줄(표준 출력)로만. 글은 기술 문구(Tech) 그대로.</summary>
    public sealed class YesUi : IInstallUi
    {
        readonly InstallLog log;
        public YesUi(InstallLog log) { this.log = log; }
        public bool Confirm(UiText t)
        {
            log.Line(L.T("  --yes: 확인 창 없이 진행(" + t.Ok + ")", "  --yes: continuing without a confirm window (" + t.Ok + ")"));
            return true;
        }
        public void Steps(string head, params string[] names) { }
        public void Stage(int index) { }
        public void Progress(string line) { }
        public void Detail(string line) { }
        public void Finish(bool ok, UiText t, int autoCloseMs)
        {
            if (!ok) log.Line(L.T("  오류: " + (t.Tech ?? t.LogText).Replace("\n", " "), "  Error: " + (t.Tech ?? t.LogText).Replace("\n", " ")));
        }
    }

    /// <summary>install.log: 표준 출력에도 같은 줄을 쓴다(파일 줄에는 시각). 파일이 정해지기 전 줄은 모아 두었다가 Attach 때 쓴다.</summary>
    public sealed class InstallLog
    {
        readonly object l = new object();
        readonly List<string> pending = new List<string>();
        string path;
        Stream so;
        public bool Echo = true;
        /// <summary>줄마다 함께 부른다(진행 창의 "자세히" — 창 스레드로 넘기기만 해야 한다).</summary>
        public Action<string> Tap;
        public string Path => path;

        public void Line(string s)
        {
            try { Tap?.Invoke(s); } catch (Exception) { }
            lock (l)
            {
                if (Echo)
                {
                    try
                    {
                        so ??= Console.OpenStandardOutput();
                        var b = new UTF8Encoding(false).GetBytes(s + "\n");
                        so.Write(b, 0, b.Length);
                        so.Flush();
                    }
                    catch (Exception) { }
                }
                string fl = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "] " + s + "\n";
                if (path == null) { pending.Add(fl); return; }
                try { SystemAccess.AppendText(path, fl); } catch (Exception) { }
            }
        }

        /// <summary>이 파일에 이어 쓴다(모아 둔 줄을 먼저).</summary>
        public void Attach(string p)
        {
            lock (l)
            {
                path = p;
                if (pending.Count == 0) return;
                try { SystemAccess.AppendText(p, string.Concat(pending)); pending.Clear(); } catch (Exception) { }
            }
        }

        /// <summary>지금까지의 파일을 p 로 복사하고 이후 p 에 쓴다(설치 폴더를 지우기 전).</summary>
        public void MoveTo(string p)
        {
            lock (l)
            {
                if (path != null && SystemAccess.FileExists(path)) SystemAccess.CopyFile(path, p, true);
                path = p;
                if (pending.Count > 0) { SystemAccess.AppendText(p, string.Concat(pending)); pending.Clear(); }
            }
        }
    }

    public static class Installer
    {
        public const int RcOk = 0, RcFailed = 1, RcCancelled = 2, RcBlocked = 3, RcArgs = 106;
        /// <summary>건강 확인 합격선(측정값, §5 A1-2 첫 설치·A1-4 업데이트)과 실패 처리를 발동하는 중단 임계(T2).</summary>
        public const double HealthPassS = 10, HealthPassUpdateS = 20, HealthAbortS = 45;
        /// <summary>setup(X7): 감독을 띄운 뒤 제어 포트가 응답할 때까지 기다리는 상한(부록 D.3).</summary>
        public const double SetupHealthS = 30;
        /// <summary>setup(X7) 전체 시간 예산(초): WSL 배포판 탐침·skills 배치는 이 안에서만(넘으면 남은 배포판은 failed "budget"). 시험은 UART_HUB_TEST_SETUP_BUDGET_S.</summary>
        public const double SetupBudgetS = 120;
        /// <summary>setup 결과 파일 이름(%LOCALAPPDATA%\uart_hub\ — Git Bash 에서 WinExe 표준 출력이 사라지는 경우 대비).</summary>
        public const string SetupLastName = "setup_last.json";
        /// <summary>교체 재시도(K8a: 공유 위반 20×250ms).</summary>
        const int ReplaceTries = 20, ReplaceWaitMs = 250;
        public const string StartupLnk = "UART 허브.lnk", MenuFolder = "UART 허브", MenuMain = "UART 허브.lnk", MenuUninstall = "UART 허브 제거.lnk";
        static readonly (string Ko, string En) InstallTitles = ("UART 허브 설치", "UART Hub setup"), UninstallTitles = ("UART 허브 제거", "UART Hub removal"), PortableTitles = ("UART 허브", "UART Hub");
        public static string InstallTitle => L.T(InstallTitles.Ko, InstallTitles.En); public static string UninstallTitle => L.T(UninstallTitles.Ko, UninstallTitles.En); public static string PortableTitle => L.T(PortableTitles.Ko, PortableTitles.En);
        /// <summary>설치기가 config.json 이 없을 때만 만드는 내용(§2.3 n).</summary>
        public const string DefaultConfig = "{\"ask_new_cables\": true}\n";
        /// <summary>휴대용 첫 실행이 config.json 이 없을 때 만드는 내용(X5 부록 B.4 — 필요할 때만 연결).</summary>
        public const string PortableDefaultConfig = "{\"on_demand\": true, \"log_layout\": 3}\n";

        static string InstJson => Path.Combine(SystemAccess.InstallDir, "install.json");
        static string InstLogPath => Path.Combine(SystemAccess.InstallDir, "install.log");
        static string StartupLnkPath => Path.Combine(SystemAccess.Startup, StartupLnk);
        static string MenuDir => Path.Combine(SystemAccess.StartMenuPrograms, MenuFolder);

        /// <summary>허브 폴더의 config.json 이 이미 있으면 거기서 언어("language")를 정한다(없으면 환경변수 → Windows 언어 그대로).</summary>
        static void LoadLang(string hubDir)
        {
            if (string.IsNullOrEmpty(hubDir)) return;
            string p = Path.Combine(hubDir, "config.json");
            if (SystemAccess.FileExists(p)) L.ConfigureFromFile(p);
        }

        /// <summary>
        /// 하위 명령 install·uninstall, 그리고 인자 없는 휴대용 실행(sub = "portable"). gui = (창 제목, 작업) → 종료코드: 확인 창·진행 창을 띄우고
        /// 작업을 백그라운드 스레드에서 돌린다(app/InstallForm.cs — Confirm·Finish 를 부르지 않으면 창은 보이지 않는다).
        /// 시험 모드(C-13)·--dry-run·--repair 는 창을 띄우지 않는다.
        /// </summary>
        public static int Main(string sub, string[] args, Func<string, Func<IInstallUi, int>, int> gui)
        {
            try { LoadLang((SystemAccess.FileExists(InstJson) ? InstallInfo.Load(InstJson) : null)?.HubDir); }   // 첫 글(인자 오류·창 제목·로그)부터 기존 허브의 언어
            catch (TestIsolationViolation) { throw; }
            catch (Exception) { }
            bool dry = false, repair = false, quiet = false, yes = false, json = false, noStart = false, allowDown = false;
            foreach (var a in args)
            {
                switch (a)
                {
                    case "--dry-run": dry = true; break;
                    case "--repair": repair = true; break;
                    case "--quiet": quiet = true; break;
                    case "--yes": yes = true; break;
                    case "--json": json = true; break;
                    case "--no-start": noStart = true; break;
                    case "--allow-downgrade": allowDown = true; break;
                    default: ErrLine(L.T(sub + ": 알 수 없는 인자 " + a + " — setup [--yes] [--json] [--no-start] [--allow-downgrade] | install [--dry-run | --repair [--quiet]] | uninstall [--dry-run | --yes]", sub + ": unknown argument " + a + " — setup [--yes] [--json] [--no-start] [--allow-downgrade] | install [--dry-run | --repair [--quiet]] | uninstall [--dry-run | --yes]")); return RcArgs;
                }
            }
            if ((repair && (dry || sub != "install")) || (quiet && !repair)) { ErrLine(L.T(sub + ": 인자 조합 오류 — --repair 는 install 에만, --quiet 는 --repair 와만", sub + ": invalid argument combination — --repair only with install, --quiet only with --repair")); return RcArgs; }
            if ((yes && sub != "setup" && sub != "uninstall") || (yes && dry) || (sub == "setup" && dry) || ((json || noStart || allowDown) && sub != "setup"))
            {
                ErrLine(L.T(sub + ": 인자 조합 오류 — --yes 는 setup·uninstall 에만(--dry-run 과 함께 못 씀, setup 은 --yes 없으면 계획만), --json·--no-start·--allow-downgrade 는 setup 에만", sub + ": invalid argument combination — --yes only with setup/uninstall (not with --dry-run; setup without --yes only plans), --json/--no-start/--allow-downgrade only with setup"));
                return RcArgs;
            }
            NativeHandles.DontInheritStd();                  // 띄우는 감독이 호출자(시험 하네스·WSL)의 파이프를 물고 남지 않게
            var log = new InstallLog { Echo = !json };
            SystemAccess.ExternalLog = log.Line;
            if (repair) return Repair(log, "cli");
            // setup(X7): 창을 띄우지 않는다. --yes 없으면 계획만(dry). installer mutex 는 Setup 이 --yes 일 때 잡는다
            if (sub == "setup") return new Session(sub, !yes, log, yes ? new YesUi(log) : null) { Json = json, NoStart = noStart, AllowDowngrade = allowDown }.Run();
            if (dry) return new Session(sub, true, log, null).Run();

            // ---- installer mutex: 프로세스 시작 직후부터 끝날 때까지(C-8)
            string mname = SystemAccess.MutexName("installer");
            var mtx = new Mutex(false, mname);
            bool own;
            try { own = mtx.WaitOne(0); } catch (AbandonedMutexException) { own = true; }
            if (!own)
            {
                if (yes)
                {
                    // --yes(X7): 창을 건드리지 않고 중단으로 알린다(Claude 가 성공으로 읽지 않게)
                    log.Line(L.T("# 다른 설치기·제거기가 이미 실행 중(" + mname + ") — 끝난 뒤 다시 실행하세요(exit 3)", "# Another installer/uninstaller is already running (" + mname + ") — run again after it finishes (exit 3)"));
                    mtx.Dispose();
                    return RcBlocked;
                }
                // 먼저 뜬 설치기의 창 제목은 그 프로세스의 언어 — 두 언어 제목 모두로 찾는다
                bool front = new[] { InstallTitles, UninstallTitles, PortableTitles }.SelectMany(t => new[] { t.Ko, t.En }).Any(SystemAccess.BringWindowToFront);
                log.Line(L.T("# 다른 설치기가 이미 실행 중(" + mname + ") — " + (front ? "그 창을 앞으로 가져옴" : "앞으로 가져올 창 없음") + ", 이 실행은 끝냄(exit 0)", "# Another installer is already running (" + mname + ") — " + (front ? "brought its window to the front" : "no window to bring forward") + ", ending this run (exit 0)"));
                mtx.Dispose();
                return RcOk;
            }
            try
            {
                int Work(IInstallUi ui) => new Session(sub, false, log, ui).Run();
                if (yes) return Work(new YesUi(log));                       // uninstall --yes(X7): 시험 모드에서도 같은 길
                if (SystemAccess.IsTest)
                {
                    bool answer = !string.Equals(Environment.GetEnvironmentVariable("UART_HUB_TEST_ANSWER"), "cancel", StringComparison.OrdinalIgnoreCase);
                    return Work(new AutoInstallUi(log, answer));
                }
                return gui(sub == "uninstall" ? UninstallTitle : sub == "portable" ? PortableTitle : InstallTitle, Work);
            }
            finally
            {
                try { mtx.ReleaseMutex(); } catch (Exception) { }
                mtx.Dispose();
            }
        }

        /// <summary>표준 출력 한 줄(setup --json 의 JSON — 로그 줄은 섞지 않는다).</summary>
        static void OutLine(string s)
        {
            try
            {
                var o = Console.OpenStandardOutput();
                var b = new UTF8Encoding(false).GetBytes(s + "\n");
                o.Write(b, 0, b.Length);
                o.Flush();
            }
            catch (Exception) { }
        }

        static void ErrLine(string s)
        {
            try
            {
                using var e = Console.OpenStandardError();
                var b = new UTF8Encoding(false).GetBytes(s + "\n");
                e.Write(b, 0, b.Length);
            }
            catch (Exception) { }
        }

        // ================================================================== 복구(--repair, 감독의 C-14)
        /// <summary>
        /// 확인 창 없이 멱등 재적용: install.json(version=이 판·exe·state=installed, 모르는 키 보존 C-12), 시작프로그램·시작 메뉴 바로가기,
        /// 옛 이미지(UartHub.old*.exe — 실행 중이면 다음 감독 기동 때). update mutex 를 잡지 않고 sup 을 띄우지 않는다. skills 표시도 맞춘다(기록된 곳 + 새로 생긴 곳 — SkillsDeployer).
        /// </summary>
        public static int Repair(InstallLog log, string from)
        {
            string dir = SystemAccess.InstallDir, exe = SystemAccess.InstalledExe;
            var info = SystemAccess.FileExists(InstJson) ? InstallInfo.Load(InstJson) : null;
            if (from != "sup") LoadLang(info?.HubDir);       // 감독 안(백그라운드 스레드)에서는 언어를 건드리지 않는다 — 감독 언어는 감독이 정한다
            // 휴대용(X4): 기록은 허브 폴더에(%LOCALAPPDATA%\uart_hub 에는 install.json 등 설정 파일만), 바로가기·설치본·옛 이미지는 다루지 않는다
            if (info != null && info.Portable && !string.IsNullOrEmpty(info.HubDir) && SystemAccess.DirExists(info.HubDir)) log.Attach(Path.Combine(info.HubDir, "install.log"));
            else if (SystemAccess.DirExists(dir)) log.Attach(InstLogPath);
            log.Line("== Repair(from=" + from + ", v=" + Hub.Version + ", pid=" + Environment.ProcessId + ")");
            if (info == null) { log.Line(L.T("  install.json 없음 — 고칠 설치본이 없음", "  no install.json — nothing to repair")); return RcFailed; }
            if (info.Portable) exe = Environment.ProcessPath;
            else if (!SystemAccess.FileExists(exe)) { log.Line(L.T("  " + exe + " 없음 — 받은 exe 를 다시 실행해야 함", "  " + exe + " is missing — run the downloaded exe again")); return RcFailed; }
            string id = info.InstallId ?? Guid.NewGuid().ToString("N");
            if (from == "sup" && CoreMain.MutexExists(SystemAccess.MutexName("installer")))
            {
                // C-14 경합(X7): 감독은 installer mutex 가 없을 때 판정했지만 그 뒤 setup·설치기가 시작됐다 — 그쪽이 install.json·skills 를 쓴다
                log.Line(L.T("  설치기(setup·설치)가 그사이 시작됨 — Repair 그만둠(그 설치기가 install.json 을 씀)", "  an installer (setup/install) started in the meantime — stopping Repair (that installer writes install.json)"));
                return RcOk;
            }
            if (info.Portable)
            {
                log.Line(L.T("  휴대용(portable): install.json exe·version 만 맞춤(바로가기·설치본 없음) — exe=" + exe, "  portable: only aligning install.json exe/version (no shortcuts or installed copy) — exe=" + exe));
                info.Version = Hub.Version; info.Exe = exe; info.InstallId = id; info.State = "installed";
                SystemAccess.Guard(InstJson, "write");
                if (!info.Save(InstJson)) { log.Line(L.T("  install.json 쓰기 실패", "  failed to write install.json")); return RcFailed; }
                log.Line("  install.json version=" + info.Version + " state=installed portable=true");
            }
            else
            {
                EnsureLnk(log, StartupLnkPath, exe, "sup", L.T("UART 허브 감독(로그온 때 자동 실행)", "UART Hub supervisor (auto start at logon)"), id);
                EnsureLnk(log, Path.Combine(MenuDir, MenuMain), exe, "sup", L.T("UART 허브 감독 시작", "Start UART Hub supervisor"), id);
                EnsureLnk(log, Path.Combine(MenuDir, MenuUninstall), exe, "uninstall", L.T("UART 허브 제거", "Remove UART Hub"), id);
                info.Version = Hub.Version; info.Exe = exe; info.InstallId = id; info.State = "installed";
                SystemAccess.Guard(InstJson, "write");
                if (!info.Save(InstJson)) { log.Line(L.T("  install.json 쓰기 실패", "  failed to write install.json")); return RcFailed; }
                log.Line("  install.json version=" + info.Version + " state=installed");
                DeleteOldImages(log, dir);
            }
            log.Line(L.T("  skills 표시 맞추기(기록된 곳 + 새로 생긴 곳)", "  aligning skills markers (recorded places + new ones)"));
            try
            {
                var placed = SkillsDeployer.Deploy(id, Hub.Version, exe, null, SkillsDeployer.Recorded(InstJson), log.Line);
                info.SetRaw("skills", SkillsDeployer.ToJson(placed));
                if (!info.Save(InstJson)) log.Line(L.T("  install.json skills[] 쓰기 실패(경고)", "  failed to write install.json skills[] (warning)"));
                log.Line("  skills: " + SkillsDeployer.Summary(placed));
            }
            catch (TestIsolationViolation) { throw; }
            catch (Exception e) { log.Line(L.T("  경고: skills 표시 맞추기 실패(" + e.GetType().Name + ": " + e.Message + ")", "  warning: failed to align skills markers (" + e.GetType().Name + ": " + e.Message + ")")); }
            log.Line(L.T("  Repair 끝", "  Repair done"));
            return RcOk;
        }

        // ================================================================== 바로가기 공용
        /// <summary>자기 것(대상·인자 일치 또는 [uart_hub:…] 표시)이거나 없으면 만든다. 남의 같은 이름 파일은 건드리지 않는다(P3).</summary>
        static bool EnsureLnk(InstallLog log, string path, string exe, string args, string desc, string installId)
        {
            if (SystemAccess.FileExists(path))
            {
                var cur = ShellLink.Read(path);
                bool ours = cur != null && (ShellLink.IsOurs(cur, exe, args) || (cur.Description ?? "").Contains(ShellLink.MarkerPrefix));
                if (!ours) { log.Line(L.T("  남의 바로가기라 건드리지 않음: " + path + (cur != null ? " (대상 " + cur.Target + ")" : " (읽지 못함)"), "  not our shortcut, leaving it alone: " + path + (cur != null ? " (target " + cur.Target + ")" : " (could not read)"))); return false; }
            }
            ShellLink.Save(path, new ShellLinkInfo
            {
                Target = exe, Arguments = args, WorkingDirectory = Path.GetDirectoryName(exe),
                Description = desc + " " + ShellLink.Marker(installId), IconPath = exe, IconIndex = 0,
            });
            return true;
        }

        static bool IsOurLnk(string path, string exe, string args)
        {
            var cur = ShellLink.Read(path);
            return cur != null && (ShellLink.IsOurs(cur, exe, args) || (SystemAccess.SamePath(cur.Target, exe) && (cur.Description ?? "").Contains(ShellLink.MarkerPrefix)));
        }

        /// <summary>교체 백업 UartHub.old*.exe 를 지운다. 실행 중인 이미지(긴 cli 등)는 지워지지 않으므로 다음 감독 기동(CleanOldImages) 때로 넘긴다(K8a).</summary>
        static void DeleteOldImages(InstallLog log, string dir)
        {
            foreach (var f in SystemAccess.ListFiles(dir, "UartHub.old*.exe"))
            {
                try { SystemAccess.DeleteFile(f); log.Line(L.T("  옛 이미지 삭제 " + Path.GetFileName(f), "  deleted old image " + Path.GetFileName(f))); }
                catch (TestIsolationViolation) { throw; }
                catch (Exception e) { log.Line(L.T("  옛 이미지 " + Path.GetFileName(f) + " 잠겨 있음(실행 중) — 다음 감독 기동 때 지움: " + e.GetType().Name, "  old image " + Path.GetFileName(f) + " is locked (running) — will be removed at the next supervisor start: " + e.GetType().Name)); }
            }
        }

        /// <summary>공유 위반·접근 거부(백신·색인기가 막 만든 파일을 잠깐 잡음 — US-108 게이트에서 제거 시험이 시작 메뉴 .lnk 삭제로 1회 실패)에 대비한 재시도.</summary>
        static void Retry(Action a, int n = 20, int ms = 250)
        {
            for (int i = 0; ; i++)
            {
                try { a(); return; }
                catch (TestIsolationViolation) { throw; }
                catch (Exception) when (i < n - 1) { Thread.Sleep(ms); }
            }
        }

        // ================================================================== 한 번의 설치·제거
        sealed class Session
        {
            readonly string sub;
            readonly bool dry;
            readonly InstallLog log;
            readonly IInstallUi ui;
            readonly string self = Environment.ProcessPath;
            readonly string instDir = SystemAccess.InstallDir;
            /// <summary>허브 exe: 설치 방식은 설치본, 휴대용(X4)은 그 exe(실행 = 이 exe, 제거 = install.json 의 exe).</summary>
            string instExe = SystemAccess.InstalledExe;
            readonly string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string NewExe => Path.Combine(instDir, "UartHub.new.exe");

            // 사전 점검 결과
            readonly List<string> blockers = new List<string>(), warnings = new List<string>();
            InstallInfo existing, info;
            // 업데이트: 설치된 판·받은 판, 교체 백업(되돌림에 씀), 0 단계 잔재 정리 안내
            string vA, vB = Hub.Version, oldBackup;
            readonly List<string> residue = new List<string>();  // 코드: A·B·C·D·exe-missing(언어와 무관한 비교용)
            static string ResText(string c) => c switch { "A" => L.T("잔재 A", "residue A"), "B" => L.T("잔재 B", "residue B"), "C" => L.T("잔재 C", "residue C"), "D" => L.T("잔재 D", "residue D"), _ => L.T("exe 없음", "exe missing") };
            static string ResJoin(IEnumerable<string> r) => string.Join("·", r.Distinct().Select(ResText));
            string logRoot, logRootWhy, hubDir, installId;
            List<string> distros = new List<string>();
            SkillsDeployer.Distros wsl;
            string wslNote, winClaude, psTaskNote, cfgNote, cfgContent, skillsSummary;
            bool deletePsTask, cfgExists;
            string[] terms = new string[3];
            static readonly string[] TermNames = { "teraterm", "putty", "mobaxterm" };

            Mutex update;
            readonly List<(int Pid, long St)> started = new List<(int, long)>();

            /// <summary>setup(X7): 결과를 JSON 한 줄로(표준 출력은 그 줄만) · 감독을 띄우지 않음 · 더 오래된 판으로 되돌리기 허용.</summary>
            public bool Json, NoStart, AllowDowngrade;

            public Session(string sub, bool dry, InstallLog log, IInstallUi ui)
            {
                this.sub = sub; this.dry = dry; this.log = log; this.ui = ui;
                if (ui != null) log.Tap = ui.Detail;                       // install.log 줄 → 진행 창 "자세히"(창 없는 화면은 버림)
            }

            string TokenPath => Path.Combine(hubDir, "token");
            string CfgPath => Path.Combine(hubDir, "config.json");
            string PrefsPath => Path.Combine(instDir, "prefs.json");

            public int Run()
            {
                // 작업 폴더를 TEMP 로: 시작 메뉴 바로가기는 작업 폴더가 설치 폴더라, 그대로면 제거·실패 정리 때 그 폴더를 지울 수 없다
                if (!dry) try { SystemAccess.SetCurrentDirectory(SystemAccess.Temp); } catch (TestIsolationViolation) { throw; } catch (Exception) { }
                try { return sub == "uninstall" ? Uninstall() : sub == "portable" ? Portable() : sub == "setup" ? Setup() : Install(); }
                catch (TestIsolationViolation e) { log.Line("  " + e.Message); return RcFailed; }
                finally
                {
                    ReleaseUpdate();
                    SystemAccess.ExternalLog = null;
                }
            }

            // -------------------------------------------------------------- 출력
            void Step(string s)
            {
                log.Line("");
                log.Line("== " + s);
                ui?.Progress(s);
            }

            /// <summary>실제 동작. dry-run 이면 설명만 찍는다.</summary>
            void Act(string desc, Action a)
            {
                if (dry) { log.Line("  [dry-run] " + desc); return; }
                log.Line("  " + desc);
                a();
            }

            void Header(string kind)
            {
                log.Line(L.T("# UART 허브 " + kind + " v" + Hub.Version + (dry ? " (dry-run — 시스템을 바꾸지 않음)" : ""), "# UART Hub " + kind + " v" + Hub.Version + (dry ? " (dry-run — system is not changed)" : "")));
                log.Line(L.T("# exe=" + self + " pid=" + Environment.ProcessId + " 사용자=" + Environment.UserName, "# exe=" + self + " pid=" + Environment.ProcessId + " user=" + Environment.UserName));
                if (SystemAccess.IsTest) log.Line(L.T("# 시험 모드(C-13) root=" + SystemAccess.TestRoot, "# test mode (C-13) root=" + SystemAccess.TestRoot));
                log.Line(L.T("# 설치 위치=" + instDir, "# install location=" + instDir));
            }

            // -------------------------------------------------------------- mutex
            void AcquireUpdate()
            {
                if (update != null) return;
                var m = new Mutex(false, SystemAccess.MutexName("update"));
                bool own;
                try { own = m.WaitOne(5000); } catch (AbandonedMutexException) { own = true; }
                if (!own) { m.Dispose(); throw new IOException(L.T("update mutex 를 5초 안에 얻지 못함 — 다른 설치기·업데이트가 실행 중", "could not get the update mutex within 5 s — another installer/update is running")); }
                update = m;
            }

            /// <summary>ReleaseMutex 뒤 핸들째 닫는다(쥐고 있으면 확인자가 계속 "업데이트 중"으로 본다, C-8).</summary>
            void ReleaseUpdate()
            {
                if (update == null) return;
                try { update.ReleaseMutex(); } catch (Exception) { }
                update.Dispose();
                update = null;
            }

            // ============================================================== 설치
            int Install()
            {
                Header(L.T("설치", "setup"));
                Step(L.T("0. 이전 제거 잔재(%TEMP%\\uart_hub_removed_*.exe)", "0. Leftovers from a previous removal (%TEMP%\\uart_hub_removed_*.exe)"));
                var removed = SystemAccess.ListFiles(SystemAccess.Temp, "uart_hub_removed_*.exe");
                if (removed.Length == 0) log.Line(L.T("  없음", "  none"));
                foreach (var f in removed)
                    Act(L.T("지움 " + f, "delete " + f), () => { try { SystemAccess.DeleteFile(f); } catch (TestIsolationViolation) { throw; } catch (Exception e) { log.Line(L.T("    실패(다음 설치 때 다시): " + e.Message, "    failed (will retry at the next setup): " + e.Message)); } });
                if (SystemAccess.FileExists(InstJson)) Residue();

                Step(L.T("1. 사전 점검", "1. Pre-checks"));
                Precheck();
                foreach (var w in warnings) log.Line(L.T("  경고: " + w, "  warning: " + w));
                int stop = blockers.Count > 0 ? RcBlocked : RcOk;
                foreach (var b in blockers) log.Line(L.T("  중단: " + b, "  blocked: " + b));
                if (stop != RcOk)
                {
                    if (!dry)
                    {
                        ui?.Finish(false, string.Join("\n", blockers), 0);
                        return stop;
                    }
                    log.Line(L.T("  [dry-run] 실제 실행이면 여기서 중단(exit " + stop + ") — 아래는 막는 것이 없을 때의 단계", "  [dry-run] a real run would stop here (exit " + stop + ") — the steps below are what would run if nothing blocked it"));
                }
                else log.Line(L.T("  사전 점검 통과", "  pre-checks passed"));
                if (existing != null) return Update(stop);

                Step(L.T("2. 확인 창(1회)", "2. Confirm window (once)"));
                var lines = Summary();
                foreach (var l in lines) log.Line("  | " + l);
                if (!dry && !ui.Confirm(InstallTitle, L.T("설치", "Install"), lines))
                {
                    log.Line(L.T("  취소 — 아무것도 바꾸지 않음(exit 2)", "  cancelled — nothing was changed (exit 2)"));
                    return RcCancelled;
                }
                if (dry) log.Line(L.T("  [dry-run] 확인 창을 띄우지 않음", "  [dry-run] not showing the confirm window"));

                try
                {
                    Step(L.T("3. update mutex → exe 복사·검증", "3. update mutex → copy and verify exe"));
                    Act(L.T("update mutex 획득(" + SystemAccess.MutexName("update") + ")", "acquire update mutex (" + SystemAccess.MutexName("update") + ")"), AcquireUpdate);
                    Act(L.T("설치 폴더 " + instDir, "install folder " + instDir), () => SystemAccess.CreateDir(instDir));
                    Act("install.log → " + InstLogPath, () => log.Attach(InstLogPath));
                    Act(L.T("스트림 복사(MotW 없는 새 파일) " + self + " → " + NewExe, "stream copy (new file without MotW) " + self + " → " + NewExe), () => SystemAccess.CopyStream(self, NewExe));
                    Act(L.T(Path.GetFileName(NewExe) + " version --json = 자기 버전(" + Hub.Version + ")·Core MVID 확인", "check " + Path.GetFileName(NewExe) + " version --json = own version (" + Hub.Version + ") and Core MVID"), () => VerifyCopy(NewExe));
                    Act(L.T("이름 바꾸기 " + Path.GetFileName(NewExe) + " → " + instExe + " (재시도 20×250ms)", "rename " + Path.GetFileName(NewExe) + " → " + instExe + " (retry 20×250ms)"), () => Retry(() => SystemAccess.MoveFile(NewExe, instExe, true)));

                    Step(L.T("4. 로그·허브 폴더, 토큰, config.json", "4. Log and hub folders, token, config.json"));
                    foreach (var d in new[] { hubDir, Path.Combine(logRoot, "live") })
                    {
                        if (SystemAccess.DirExists(d)) log.Line(L.T("  " + d + " 이미 있음", "  " + d + " already exists"));
                        else Act(L.T("폴더 " + d, "folder " + d), () => SystemAccess.CreateDir(d));
                    }
                    bool haveTok = TokenFile.IsValid(TokenPath);
                    Act(L.T("토큰 " + TokenPath + (haveTok ? " 이미 있음 → 유지" : " 새로 만듦(32자 hex)") + " + ACL(상속 끊기, 현재 사용자만 FullControl)", "token " + TokenPath + (haveTok ? " already exists → kept" : " created (32 hex chars)") + " + ACL (inheritance removed, FullControl for the current user only)"), () =>
                    {
                        TokenFile.Ensure(TokenPath);
                        log.Line("    ACL: " + TokenFile.Describe(TokenPath));
                    });
                    if (cfgExists) log.Line(L.T("  config.json 이미 있음 — 건드리지 않음(" + cfgNote + ")", "  config.json already exists — left alone (" + cfgNote + ")"));
                    else Act(L.T("config.json 새로 만듦(" + cfgNote + "): " + cfgContent.Trim().Replace("\n", " "), "create config.json (" + cfgNote + "): " + cfgContent.Trim().Replace("\n", " ")), () => SystemAccess.WriteText(CfgPath, cfgContent));

                    Step(L.T("5. install.json(installing)·바로가기·prefs.json·이전 PS 작업", "5. install.json (installing), shortcuts, prefs.json, old PS task"));
                    installId = Guid.NewGuid().ToString("N");
                    Act("install.json state=installing (install_id=" + installId + ", hub_dir=" + hubDir + ", log_root=" + logRoot + ")", () => WriteInstallJson("installing"));
                    Act(L.T("시작프로그램 " + StartupLnkPath + " → \"" + instExe + "\" sup", "startup " + StartupLnkPath + " → \"" + instExe + "\" sup"), () => EnsureLnk(log, StartupLnkPath, instExe, "sup", L.T("UART 허브 감독(로그온 때 자동 실행)", "UART Hub supervisor (auto start at logon)"), installId));
                    Act(L.T("시작 메뉴 " + Path.Combine(MenuDir, MenuMain) + " → sup", "Start menu " + Path.Combine(MenuDir, MenuMain) + " → sup"), () => EnsureLnk(log, Path.Combine(MenuDir, MenuMain), instExe, "sup", L.T("UART 허브 감독 시작", "Start UART Hub supervisor"), installId));
                    Act(L.T("시작 메뉴 " + Path.Combine(MenuDir, MenuUninstall) + " → uninstall", "Start menu " + Path.Combine(MenuDir, MenuUninstall) + " → uninstall"), () => EnsureLnk(log, Path.Combine(MenuDir, MenuUninstall), instExe, "uninstall", L.T("UART 허브 제거", "Remove UART Hub"), installId));
                    if (SystemAccess.FileExists(PrefsPath)) log.Line(L.T("  prefs.json 이미 있음 — 그대로", "  prefs.json already exists — kept as is"));
                    else Act(L.T("prefs.json(터미널 auto, 찾은 것: " + TermText() + ")", "prefs.json (terminal auto, found: " + TermText() + ")"), WritePrefs);
                    if (deletePsTask) Act(L.T("이전 PS 작업 \\uart_hub 삭제(동작이 uart_hub.ps1)", "delete old PS task \\uart_hub (its action is uart_hub.ps1)"), DeletePsTask);
                    else log.Line(L.T("  이전 PS 작업 \\uart_hub: " + psTaskNote, "  old PS task \\uart_hub: " + psTaskNote));

                    Step(L.T("6. update mutex 닫기 → 감독 기동 → 건강 확인", "6. close update mutex → start supervisor → health check"));
                    Act(L.T("update mutex 닫기(ReleaseMutex + 핸들 닫기)", "close update mutex (ReleaseMutex + close handle)"), ReleaseUpdate);
                    Act(L.T("\"" + instExe + "\" sup 분리 기동", "start \"" + instExe + "\" sup detached"), () => started.Add(SystemAccess.StartDetached(instExe, new[] { "sup" })));
                    bool healthy = true;
                    Act(L.T("건강 확인(sup.json state=running · core.pid control=listening, 합격선 " + HealthPassS + "초 · 중단 임계 " + HealthAbortS + "초)", "health check (sup.json state=running, core.pid control=listening; pass within " + HealthPassS + " s, abort threshold " + HealthAbortS + " s)"), () => healthy = Health());
                    if (!healthy) return FirstFail(L.T("건강 확인 실패", "health check failed"));

                    Step(L.T("7. 마무리", "7. Finish"));
                    if (!dry) SystemAccess.TestDelay("finalize");                // finalize-race: 감독은 이 동안 C-14 에서 기다리기만 한다
                    Act(L.T("install.json state=installed 확정", "finalize install.json state=installed"), () => WriteInstallJson("installed"));
                    if (dry) log.Line(L.T("  [dry-run] Claude skills 배치(~/.claude 가 있는 곳만, 배포판마다 탐침 30초 — dry-run 은 배포판을 깨우지 않음) — 후보: " + SkillsText(), "  [dry-run] deploy Claude skills (only where ~/.claude exists; 30 s probe per distro — dry-run does not wake distros) — candidates: " + SkillsText()));
                    else DeploySkills(null);
                    log.Line(L.T("  " + (dry ? "[dry-run] " : "") + "진행 창 \"완료\" 5초 뒤 저절로 닫힘. installer mutex 는 이 프로세스가 끝날 때 닫힘(감독의 C-14 는 그때까지 기다림)", "  " + (dry ? "[dry-run] " : "") + "the progress window shows \"Done\" and closes by itself after 5 s. The installer mutex closes when this process ends (the supervisor's C-14 waits until then)"));
                    if (dry)
                    {
                        Step(L.T("8. (건강 확인 실패 때만) 첫 설치 실패 정리", "8. (only if the health check failed) clean up a failed first install"));
                        log.Line(L.T("  [dry-run] update mutex 재획득 → 감독·코어 shutdown → core.stop → 검증 PID kill → 시작프로그램·시작 메뉴 바로가기 삭제", "  [dry-run] re-acquire update mutex → shut down supervisor and core → core.stop → kill verified PID → delete startup and Start menu shortcuts"));
                        log.Line(L.T("  [dry-run] install.log → " + Path.Combine(hubDir, "install_failed_<시각>.log") + " → 설치 폴더 " + instDir + " 삭제", "  [dry-run] install.log → " + Path.Combine(hubDir, "install_failed_<time>.log") + " → delete install folder " + instDir));
                        log.Line(L.T("  [dry-run] 남김: 허브 폴더 " + hubDir + "(token·config.json·hub.log), 로그 폴더 " + logRoot + " → 오류 창(로그 파일 경로)", "  [dry-run] kept: hub folder " + hubDir + " (token, config.json, hub.log), log folder " + logRoot + " → error window (log file path)"));
                        log.Line("");
                        log.Line(L.T("# dry-run 끝 — 시스템을 바꾸지 않았습니다." + (stop != RcOk ? " 사전 점검이 실제 실행을 막습니다(exit " + stop + ")." : ""), "# dry-run done — the system was not changed." + (stop != RcOk ? " The pre-checks would block a real run (exit " + stop + ")." : "")));
                        return stop;
                    }
                    log.Line("");
                    log.Line(L.T("# 설치 완료 v" + Hub.Version, "# setup done v" + Hub.Version));
                    ui.Finish(true, L.T("UART 허브를 설치했습니다. 트레이(숨겨진 아이콘 ^)에 UART 허브 아이콘이 있습니다.\n로그 위치: " + logRoot + "\nClaude skills: " + skillsSummary, "UART Hub is installed. Its icon is in the tray (under the hidden icons ^).\nLog location: " + logRoot + "\nClaude skills: " + skillsSummary), 5000);
                    return RcOk;
                }
                catch (TestIsolationViolation) { throw; }
                catch (Exception e)
                {
                    if (dry) { log.Line(L.T("  [dry-run] 오류: " + e.GetType().Name + ": " + e.Message, "  [dry-run] error: " + e.GetType().Name + ": " + e.Message)); return RcFailed; }
                    log.Line(L.T("  오류: " + e.GetType().Name + ": " + e.Message, "  error: " + e.GetType().Name + ": " + e.Message));
                    return FirstFail(e.GetType().Name + ": " + e.Message);
                }
            }

            // ============================================================== 업데이트(설치본 있음, T2 "업데이트 0~5")
            /// <summary>받은 판(vB)과 설치된 판(vA) 비교. 버전은 17자리 빌드 시각이라 길이가 같으면 문자열 순서 = 시간 순서.</summary>
            int CompareVersions()
            {
                if (vA == null || vA.Length != vB.Length || !vA.All(char.IsDigit) || !vB.All(char.IsDigit)) return string.Equals(vA, vB, StringComparison.Ordinal) ? 0 : 1;
                return Math.Sign(string.CompareOrdinal(vB, vA));
            }

            string UpdateKind()
            {
                if (!SystemAccess.FileExists(instExe)) return L.T("복구(UartHub.exe 없음 — 새로 놓음)", "repair (UartHub.exe is missing — placing a new one)");
                int c = CompareVersions();
                return c == 0 ? L.T("같은 버전 — 복구(다시 설치)", "same version — repair (reinstall)") : c > 0 ? L.T("업데이트", "update") : L.T("더 오래된 판으로 바꿈(주의: 설치된 판이 더 새것)", "switch to an older version (note: the installed one is newer)");
            }

            /// <summary>진행 창 안내 한 줄(시험 모드는 install.log 의 "[진행 창]" 줄로 본다).</summary>
            void Notice(string s)
            {
                log.Line(L.T("  [진행 창] " + s, "  [progress window] " + s));
                ui?.Progress(s);
            }

            /// <summary>
            /// 0 단계 잔재 점검(install.json 이 있을 때 항상, 확인 창 전): 이전 설치·업데이트가 공백 중에 끊긴 흔적을 매트릭스 규칙으로 먼저 정리한다.
            ///   A: UartHub.exe 없음 + old → old 를 되돌리고 new 를 지움 · B: UartHub.exe + new → new 를 지움 · C: UartHub.exe + old → 감독의 C-14 몫(여기선 기록만,
            ///   이번 업데이트가 다시 덮음) · D: bad → 지움 · UartHub.exe·old·new 모두 없음 → 같은 버전 업데이트(복구) 경로로 새로 놓음.
            /// </summary>
            void Residue()
            {
                Step(L.T("0a. 이전 업데이트 잔재(UartHub.exe 없음 · UartHub.new/old*/bad*.exe)", "0a. Leftovers from a previous update (UartHub.exe missing · UartHub.new/old*/bad*.exe)"));
                if (!dry) log.Attach(InstLogPath);
                bool exe = SystemAccess.FileExists(instExe), nw = SystemAccess.FileExists(NewExe);
                var olds = SystemAccess.ListFiles(instDir, "UartHub.old*.exe");
                var bads = SystemAccess.ListFiles(instDir, "UartHub.bad*.exe");
                if (exe && !nw && olds.Length == 0 && bads.Length == 0) { log.Line(L.T("  없음", "  none")); return; }
                if (!exe && olds.Length > 0)
                {
                    // 가장 최근 백업: UartHub.old.<n>.exe 는 old.exe 가 잠겨 있을 때만 생기므로 n 이 큰 쪽이 나중 것
                    string pick = olds.OrderByDescending(OldRank).First();
                    residue.Add("A");
                    Act(L.T("잔재 A(공백 중 끊김: UartHub.exe 없음 + " + Path.GetFileName(pick) + ") → UartHub.exe 로 되돌림", "residue A (interrupted during the gap: UartHub.exe missing + " + Path.GetFileName(pick) + ") → restore as UartHub.exe"), () => Retry(() => SystemAccess.MoveFile(pick, instExe, false)));
                }
                else if (exe && nw) residue.Add("B");
                if (nw) Act(L.T("잔재 " + (exe ? "B" : "A") + ": " + Path.GetFileName(NewExe) + " 지움(교체 전에 끊긴 새 exe)", "residue " + (exe ? "B" : "A") + ": delete " + Path.GetFileName(NewExe) + " (new exe cut off before the swap)"), () => SystemAccess.DeleteFile(NewExe));
                if (exe && !nw && olds.Length > 0)
                {
                    residue.Add("C");
                    log.Line(L.T("  잔재 C(교체 뒤 마무리 전에 끊김: UartHub.exe + " + string.Join(", ", olds.Select(Path.GetFileName)) + ") — 감독의 C-14 가 맞추며, 이번 실행이 다시 덮는다", "  residue C (interrupted after the swap, before finishing: UartHub.exe + " + string.Join(", ", olds.Select(Path.GetFileName)) + ") — the supervisor's C-14 aligns it, and this run overwrites it again"));
                }
                foreach (var b in bads)
                {
                    residue.Add("D");
                    Act(L.T("잔재 D(되돌림이 남긴 " + Path.GetFileName(b) + ") 지움", "residue D (left by a rollback: " + Path.GetFileName(b) + ") delete"), () =>
                    {
                        try { SystemAccess.DeleteFile(b); }
                        catch (TestIsolationViolation) { throw; }
                        catch (Exception e) { log.Line(L.T("    지우지 못함(실행 중일 수 있음 — 다음 설치 때 다시): " + e.GetType().Name, "    could not delete (may be running — will retry at the next setup): " + e.GetType().Name)); }
                    });
                }
                if (!exe && olds.Length == 0)
                {
                    residue.Add("exe-missing");
                    log.Line(L.T("  UartHub.exe 없음(old·new 도 없음 — 사용자가 지운 경우 등) → 같은 버전 업데이트(복구) 경로로 새로 놓음. 포트·설정·로그는 그대로", "  UartHub.exe is missing (no old or new either — e.g. the user deleted it) → placing a new one via the same-version update (repair) path. Ports, settings and logs are kept"));
                }
                if (residue.Any(x => x == "A" || x == "B")) Notice(L.T("이전 업데이트가 끝나지 않아 먼저 복구함(" + ResJoin(residue) + ")", "The previous update had not finished, so it was repaired first (" + ResJoin(residue) + ")"));
                else if (residue.Contains("exe-missing")) Notice(L.T("복구: UartHub.exe 가 없어 새로 놓음", "Repair: UartHub.exe was missing, so a new one was placed"));
            }

            static int OldRank(string f)
            {
                var m = Regex.Match(Path.GetFileName(f), @"^UartHub\.old\.(\d+)\.exe$", RegexOptions.IgnoreCase);
                return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            }

            /// <summary>base.exe 가 없으면 그것, 있으면 base.&lt;n&gt;.exe 중 없는 첫 이름(잠긴 옛 이미지를 피한다).</summary>
            string FreeName(string stem)
            {
                string p = Path.Combine(instDir, stem + ".exe");
                if (!SystemAccess.FileExists(p)) return p;
                try { SystemAccess.DeleteFile(p); return p; }
                catch (TestIsolationViolation) { throw; }
                catch (Exception) { }
                for (int n = 1; ; n++)
                {
                    p = Path.Combine(instDir, stem + "." + n.ToString(CultureInfo.InvariantCulture) + ".exe");
                    if (!SystemAccess.FileExists(p)) return p;
                }
            }

            int Update(int stop)
            {
                string kind = UpdateKind();
                bool same = CompareVersions() == 0;
                info = existing;
                installId = existing.InstallId ?? Guid.NewGuid().ToString("N");

                Step(L.T("U1. 확인 창(1회): v" + vA + " → v" + vB + " — " + kind, "U1. Confirm window (once): v" + vA + " → v" + vB + " — " + kind));
                var lines = new List<string>
                {
                    L.T("설치된 판 v" + vA + " → 받은 판 v" + vB + " (" + kind + ")", "Installed v" + vA + " → received v" + vB + " (" + kind + ")"),
                    L.T("업데이트하는 동안 허브가 잠깐(수 초) 멈춥니다 — 열린 터미널 창(TeraTerm 등)은 연결이 끊겨 닫히고, Claude 의 긴 명령은 hub_down(103)으로 끝납니다", "The hub stops for a few seconds during the update — open terminal windows (TeraTerm etc.) lose their connection and close, and Claude's long commands end with hub_down(103)"),
                    L.T("포트 배정·설정(config.json)·제외한 케이블·토큰·로그는 그대로 둡니다", "Port assignments, settings (config.json), excluded cables, token and logs are kept as they are"),
                    L.T("설치 위치: " + instDir, "Install location: " + instDir),
                    L.T("로그 위치: " + logRoot + "  (" + logRootWhy + ")", "Log location: " + logRoot + "  (" + logRootWhy + ")"),
                    L.T("Claude skills 갱신(설치 뒤): " + SkillsText(), "Claude skills update (after install): " + SkillsText()),
                };
                if (CompareVersions() < 0) lines.Insert(1, L.T("주의: 받은 판 v" + vB + " 가 설치된 판 v" + vA + " 보다 오래되었습니다(되돌리기). 계속하면 옛 판으로 바꿉니다", "Note: the received version v" + vB + " is older than the installed v" + vA + " (downgrade). Continuing will switch to the older version"));
                if (residue.Count > 0) lines.Add(L.T("이전 업데이트 잔재를 먼저 정리했습니다: " + ResJoin(residue), "Leftovers from a previous update were cleaned up first: " + ResJoin(residue)));
                foreach (var w in warnings) lines.Add(L.T("경고: " + w, "Warning: " + w));
                foreach (var l in lines) log.Line("  | " + l);
                if (!dry && !ui.Confirm(InstallTitle + " — v" + vA + " → v" + vB, L.T(same ? "복구" : "업데이트", same ? "Repair" : "Update"), lines))
                {
                    log.Line(L.T("  취소 — 교체하지 않음(exit 2)", "  cancelled — not replacing (exit 2)"));
                    return RcCancelled;
                }
                if (dry) log.Line(L.T("  [dry-run] 확인 창을 띄우지 않음", "  [dry-run] not showing the confirm window"));
                if (!dry && log.Path == null) log.Attach(InstLogPath);

                // ---- 공백 전: 실패해도 허브는 그대로(vA)
                Step(L.T("U1. 공백 전: update mutex → 새 exe 복사·검증(백신의 첫 검사도 여기서)", "U1. Before the gap: update mutex → copy and verify the new exe (the antivirus first scan also happens here)"));
                try
                {
                    Act(L.T("update mutex 획득(" + SystemAccess.MutexName("update") + ") — 이후 감독 기동·C-9·C-11 자가 복구 없음", "acquire update mutex (" + SystemAccess.MutexName("update") + ") — no supervisor start or C-9/C-11 self-repair from here"), AcquireUpdate);
                    Act(L.T("설치 폴더 " + instDir, "install folder " + instDir), () => SystemAccess.CreateDir(instDir));
                    Act(L.T("스트림 복사(MotW 없는 새 파일) " + self + " → " + NewExe, "stream copy (new file without MotW) " + self + " → " + NewExe), () => SystemAccess.CopyStream(self, NewExe));
                    Act(L.T(Path.GetFileName(NewExe) + " version --json = 자기 버전(" + vB + ")·Core MVID 확인", "check " + Path.GetFileName(NewExe) + " version --json = own version (" + vB + ") and Core MVID"), () => VerifyCopy(NewExe));
                }
                catch (TestIsolationViolation) { throw; }
                catch (Exception e)
                {
                    log.Line(L.T("  오류: " + e.GetType().Name + ": " + e.Message, "  error: " + e.GetType().Name + ": " + e.Message));
                    try { SystemAccess.DeleteFile(NewExe); } catch (TestIsolationViolation) { throw; } catch (Exception) { }
                    ReleaseUpdate();
                    log.Line(L.T("# 업데이트 실패(공백 전 — 허브는 그대로 v" + vA + ", exit 1)", "# update failed (before the gap — hub unchanged at v" + vA + ", exit 1)"));
                    ui.Finish(false, L.T("업데이트를 시작하지 못했습니다(허브는 그대로 동작합니다).\n로그 파일: " + log.Path + "\n" + e.Message, "Could not start the update (the hub keeps running as before).\nLog file: " + log.Path + "\n" + e.Message), 0);
                    return RcFailed;
                }
                if (dry)
                {
                    Step(L.T("U2. 공백 시작: 허브 정지", "U2. Gap starts: stop the hub"));
                    log.Line(L.T("  [dry-run] 감독에 shutdown(C-5, control.json 그대로) → 감독·코어 종료 ≤20초 → 무응답이면 core.stop(*** hub stop ***) 10초 → 그래도 안 되면 검증 PID kill", "  [dry-run] shutdown to the supervisor (C-5, control.json unchanged) → supervisor and core exit within 20 s → if no response, core.stop (*** hub stop ***) for 10 s → if still alive, kill the verified PID"));
                    Step(L.T("U3. 교체", "U3. Swap"));
                    log.Line(L.T("  [dry-run] File.Replace(" + NewExe + " → " + instExe + ", 백업 UartHub.old.exe) 재시도 " + ReplaceTries + "×" + ReplaceWaitMs + "ms, 1176 = 둘 다 제자리 → 재시도, 1177 = old 를 즉시 되돌린 뒤 재시도", "  [dry-run] File.Replace(" + NewExe + " → " + instExe + ", backup UartHub.old.exe) retry " + ReplaceTries + "×" + ReplaceWaitMs + "ms, 1176 = both still in place → retry, 1177 = restore old immediately, then retry"));
                    Step(L.T("U4. update mutex 닫기 → 새 감독 기동 → 건강 확인(공백 끝)", "U4. Close update mutex → start the new supervisor → health check (gap ends)"));
                    log.Line(L.T("  [dry-run] \"" + instExe + "\" sup 분리 기동 → sup.json running·core.pid listening·hub status 의 supervisor.version == core_version == v" + vB + " (합격선 " + HealthPassUpdateS + "초 · 중단 임계 " + HealthAbortS + "초)", "  [dry-run] start \"" + instExe + "\" sup detached → sup.json running, core.pid listening, hub status supervisor.version == core_version == v" + vB + " (pass within " + HealthPassUpdateS + " s, abort threshold " + HealthAbortS + " s)"));
                    Step(L.T("U5. 공백 뒤: 마무리(멱등)", "U5. After the gap: finish (idempotent)"));
                    log.Line(L.T("  [dry-run] 첫 설치 4~5 단계 재실행(토큰·config·state 는 그대로) → install.json version=" + vB + " → Claude skills 갱신(기록된 곳 + 새로 생긴 곳, 배포판마다 탐침 30초 — 공백 밖) → UartHub.old.exe 삭제(잠겨 있으면 다음 감독 기동 때)", "  [dry-run] re-run first-install steps 4-5 (token, config, state unchanged) → install.json version=" + vB + " → update Claude skills (recorded places + new ones, 30 s probe per distro — outside the gap) → delete UartHub.old.exe (at the next supervisor start if locked)"));
                    Step(L.T("R. (U4 실패 때만) 되돌림", "R. (only if U4 failed) rollback"));
                    log.Line(L.T("  [dry-run] update mutex 재획득 → 새 감독·코어 정지 → UartHub.exe → UartHub.bad.exe, UartHub.old.exe → UartHub.exe → 옛 exe install --repair --quiet → update mutex 닫기 → 옛 sup → 오류 창", "  [dry-run] re-acquire update mutex → stop the new supervisor and core → UartHub.exe → UartHub.bad.exe, UartHub.old.exe → UartHub.exe → old exe install --repair --quiet → close update mutex → old sup → error window"));
                    log.Line("");
                    log.Line(L.T("# dry-run 끝 — 시스템을 바꾸지 않았습니다." + (stop != RcOk ? " 사전 점검이 실제 실행을 막습니다(exit " + stop + ")." : ""), "# dry-run done — the system was not changed." + (stop != RcOk ? " The pre-checks would block a real run (exit " + stop + ")." : "")));
                    return stop;
                }

                // ---- 공백
                var gap = Stopwatch.StartNew();
                Step(L.T("U2. 공백 시작: 허브 정지(감독 shutdown → core.stop → 검증 PID kill)", "U2. Gap starts: stop the hub (supervisor shutdown → core.stop → kill verified PID)"));
                StopHub("update");
                SystemAccess.TestDelay("pre-replace");

                Step(L.T("U3. 교체: File.Replace(" + Path.GetFileName(NewExe) + " → UartHub.exe, 백업 UartHub.old.exe) 재시도 " + ReplaceTries + "×" + ReplaceWaitMs + "ms", "U3. Swap: File.Replace(" + Path.GetFileName(NewExe) + " → UartHub.exe, backup UartHub.old.exe) retry " + ReplaceTries + "×" + ReplaceWaitMs + "ms"));
                string rr = ReplaceExe();
                if (rr == "restore-failed")
                {
                    log.Line(L.T("  되돌림 이름 바꾸기 실패 — UartHub.exe 없음, " + Path.GetFileName(oldBackup ?? "UartHub.old.exe") + "(v" + vA + ")·" + Path.GetFileName(NewExe) + " 가 남음", "  rollback rename failed — UartHub.exe is missing; " + Path.GetFileName(oldBackup ?? "UartHub.old.exe") + " (v" + vA + ") and " + Path.GetFileName(NewExe) + " remain"));
                    ReleaseUpdate();
                    return Fail(L.T("업데이트하지 못했고 이전 판으로 되돌리지도 못했습니다(허브 꺼짐).\n받은 exe 를 다시 실행하면 복구됩니다.", "The update failed and the previous version could not be restored (hub is off).\nRun the downloaded exe again to repair."));
                }
                if (rr != null) return KeepOld(rr);

                Step(L.T("U4. update mutex 닫기 → 새 감독 기동 → 건강 확인(공백 끝)", "U4. Close update mutex → start the new supervisor → health check (gap ends)"));
                Act(L.T("update mutex 닫기(ReleaseMutex + 핸들 닫기)", "close update mutex (ReleaseMutex + close handle)"), ReleaseUpdate);
                try
                {
                    if (SystemAccess.TestFail("sup-start")) throw new IOException("시험 주입(UART_HUB_TEST_FAIL=sup-start): 새 감독 기동 실패");
                    Act(L.T("\"" + instExe + "\" sup 분리 기동", "start \"" + instExe + "\" sup detached"), () => started.Add(SystemAccess.StartDetached(instExe, new[] { "sup" })));
                }
                catch (TestIsolationViolation) { throw; }
                catch (Exception e)
                {
                    log.Line(L.T("  새 감독 기동 실패: " + e.Message, "  failed to start the new supervisor: " + e.Message));
                    return Rollback(L.T("새 감독 기동 실패", "failed to start the new supervisor"));
                }
                bool healthy = false;
                Act(L.T("건강 확인(sup.json running · core.pid listening · supervisor.version == core_version == v" + vB + ", 합격선 " + HealthPassUpdateS + "초 · 중단 임계 " + HealthAbortS + "초)", "health check (sup.json running, core.pid listening, supervisor.version == core_version == v" + vB + "; pass within " + HealthPassUpdateS + " s, abort threshold " + HealthAbortS + " s)"),
                    () => healthy = Health(vB, HealthPassUpdateS, true));
                if (!healthy) return Rollback(L.T("건강 확인 실패", "health check failed"));
                log.Line(L.T("  공백(정지 시작 → 건강 확인) " + gap.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "초", "  gap (stop start → health check) " + gap.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s"));

                // ---- 공백 뒤: 실패는 경고만(새 감독의 C-14 가 마저 맞춘다)
                Step(L.T("U5. 공백 뒤: 마무리(첫 설치 4~5 단계 멱등 재실행·install.json version·옛 이미지)", "U5. After the gap: finish (idempotent re-run of first-install steps 4-5, install.json version, old image)"));
                SystemAccess.TestDelay("finalize");
                try { FinishUpdate(); }
                catch (TestIsolationViolation) { throw; }
                catch (Exception e) { log.Line(L.T("  경고: 5 단계 실패(" + e.GetType().Name + ": " + e.Message + ") — 새 감독이 설치기 종료 뒤 C-14 로 마저 맞춤", "  warning: step 5 failed (" + e.GetType().Name + ": " + e.Message + ") — the new supervisor finishes it via C-14 after the installer exits")); }
                log.Line("");
                log.Line(L.T("# 업데이트 완료 v" + vA + " → v" + vB, "# update done v" + vA + " → v" + vB));
                ui.Finish(true, L.T("UART 허브를 v" + vB + " 로 " + (same ? "다시 설치(복구)" : "업데이트") + "했습니다.\n로그 위치: " + logRoot
                                + (skillsSummary != null ? "\nClaude skills: " + skillsSummary : ""), "UART Hub was " + (same ? "reinstalled (repaired)" : "updated") + " to v" + vB + ".\nLog location: " + logRoot + (skillsSummary != null ? "\nClaude skills: " + skillsSummary : "")), 5000);
                return RcOk;
            }

            /// <summary>업데이트 5 단계: 첫 설치 4~5 단계를 멱등하게 다시(토큰·config·state·prefs 는 있으면 그대로), install.json version, 옛 이미지 정리.</summary>
            void FinishUpdate()
            {
                foreach (var d in new[] { hubDir, Path.Combine(logRoot, "live") })
                    if (!SystemAccess.DirExists(d)) Act(L.T("폴더 " + d, "folder " + d), () => SystemAccess.CreateDir(d));
                bool made = TokenFile.Ensure(TokenPath);
                log.Line(L.T("  토큰 " + (made ? "새로 만듦" : "그대로") + " · ACL: " + TokenFile.Describe(TokenPath), "  token " + (made ? "created" : "unchanged") + " · ACL: " + TokenFile.Describe(TokenPath)));
                if (cfgExists) log.Line(L.T("  config.json 그대로", "  config.json unchanged"));
                else Act(L.T("config.json 새로 만듦(" + cfgNote + ")", "create config.json (" + cfgNote + ")"), () => SystemAccess.WriteText(CfgPath, cfgContent));
                Act(L.T("install.json version=" + vB + " state=installed (모르는 키 보존)", "install.json version=" + vB + " state=installed (unknown keys preserved)"), () => WriteInstallJson("installed"));
                EnsureLnk(log, StartupLnkPath, instExe, "sup", L.T("UART 허브 감독(로그온 때 자동 실행)", "UART Hub supervisor (auto start at logon)"), installId);
                EnsureLnk(log, Path.Combine(MenuDir, MenuMain), instExe, "sup", L.T("UART 허브 감독 시작", "Start UART Hub supervisor"), installId);
                EnsureLnk(log, Path.Combine(MenuDir, MenuUninstall), instExe, "uninstall", L.T("UART 허브 제거", "Remove UART Hub"), installId);
                log.Line(L.T("  시작프로그램·시작 메뉴 바로가기 확인", "  checked startup and Start menu shortcuts"));
                if (!SystemAccess.FileExists(PrefsPath)) Act(L.T("prefs.json(터미널 auto)", "prefs.json (terminal auto)"), WritePrefs);
                else log.Line(L.T("  prefs.json 그대로", "  prefs.json unchanged"));
                if (deletePsTask) Act(L.T("이전 PS 작업 \\uart_hub 삭제(동작이 uart_hub.ps1)", "delete old PS task \\uart_hub (its action is uart_hub.ps1)"), DeletePsTask);
                DeploySkills(SkillsDeployer.Recorded(InstJson));
                DeleteOldImages(log, instDir);
            }

            /// <summary>
            /// File.Replace + 재시도. null = 교체됨(oldBackup = 백업 경로, UartHub.exe 가 없던 복구 경로면 null), "exhausted" = 재시도 소진(UartHub.exe = vA 그대로),
            /// "restore-failed" = 1177 상태(UartHub.exe 없음)에서 old 를 되돌리지 못함.
            /// </summary>
            string ReplaceExe()
            {
                string backup = FreeName("UartHub.old");
                if (!backup.EndsWith("UartHub.old.exe", StringComparison.OrdinalIgnoreCase)) log.Line(L.T("  UartHub.old.exe 가 잠겨 있음(실행 중인 옛 이미지) → 백업 이름 " + Path.GetFileName(backup), "  UartHub.old.exe is locked (old image still running) → backup name " + Path.GetFileName(backup)));
                for (int i = 1; i <= ReplaceTries; i++)
                {
                    try
                    {
                        if (!SystemAccess.FileExists(instExe))
                        {
                            SystemAccess.MoveFile(NewExe, instExe, false);
                            oldBackup = null;
                            log.Line(L.T("  UartHub.exe 없음 → 이름 바꾸기 " + Path.GetFileName(NewExe) + " → UartHub.exe (되돌릴 백업 없음)", "  UartHub.exe is missing → rename " + Path.GetFileName(NewExe) + " → UartHub.exe (no backup to restore)"));
                            return null;
                        }
                        InjectReplace(i, backup);
                        SystemAccess.ReplaceFile(NewExe, instExe, backup);
                        oldBackup = backup;
                        log.Line(L.T("  교체 OK(" + i + "번째 시도): UartHub.exe = v" + vB + ", 백업 " + Path.GetFileName(backup) + " = v" + vA, "  swap OK (attempt " + i + "): UartHub.exe = v" + vB + ", backup " + Path.GetFileName(backup) + " = v" + vA));
                        return null;
                    }
                    catch (TestIsolationViolation) { throw; }
                    catch (Exception e)
                    {
                        log.Line(L.T("  교체 실패 " + i + "/" + ReplaceTries + ": " + e.GetType().Name + "(" + (e.HResult & 0xFFFF) + ") " + e.Message, "  swap failed " + i + "/" + ReplaceTries + ": " + e.GetType().Name + "(" + (e.HResult & 0xFFFF) + ") " + e.Message));
                        if (!SystemAccess.FileExists(instExe) && SystemAccess.FileExists(backup))
                        {
                            // 1177: 원본은 백업 이름으로만 남고 UartHub.exe 가 없다(잔재 A 상태) — 즉시 되돌린 뒤 재시도
                            oldBackup = backup;
                            SystemAccess.TestDelay("after-1177");
                            try
                            {
                                Retry(() =>
                                {
                                    if (SystemAccess.TestFail("rollback-rename")) throw new IOException("시험 주입(UART_HUB_TEST_FAIL=rollback-rename): 이름 바꾸기 실패");
                                    SystemAccess.MoveFile(backup, instExe, false);
                                });
                                log.Line(L.T("    " + Path.GetFileName(backup) + " → UartHub.exe 즉시 되돌림(1177 상태 해소)", "    " + Path.GetFileName(backup) + " → UartHub.exe restored immediately (clears the 1177 state)"));
                            }
                            catch (TestIsolationViolation) { throw; }
                            catch (Exception e2) { log.Line(L.T("    되돌리지 못함: " + e2.Message, "    could not restore: " + e2.Message)); return "restore-failed"; }
                        }
                        if (i < ReplaceTries) Thread.Sleep(ReplaceWaitMs);
                    }
                }
                return "exhausted";
            }

            /// <summary>시험 주입(C-13): replace = 공유 위반 · replace-1176 = 두 파일 제자리 · replace-1177 = 원본만 백업 이름으로 옮겨진 상태. -once 는 첫 시도만.</summary>
            void InjectReplace(int attempt, string backup)
            {
                bool first = attempt == 1;
                if (SystemAccess.TestFail("replace")) throw new IOException("시험 주입(replace): 공유 위반", unchecked((int)0x80070020));
                if (SystemAccess.TestFail("replace-1176") || (first && SystemAccess.TestFail("replace-1176-once")))
                    throw new IOException("시험 주입: ReplaceFile ERROR_UNABLE_TO_MOVE_REPLACEMENT", unchecked((int)0x80070498));
                if (SystemAccess.TestFail("replace-1177") || (first && SystemAccess.TestFail("replace-1177-once")))
                {
                    SystemAccess.MoveFile(instExe, backup, false);
                    throw new IOException("시험 주입: ReplaceFile ERROR_UNABLE_TO_MOVE_REPLACEMENT_2", unchecked((int)0x80070499));
                }
            }

            /// <summary>교체하지 못함(재시도 소진): UartHub.exe 는 vA 그대로 → new 를 지우고 vA 감독을 다시 띄운다.</summary>
            int KeepOld(string why)
            {
                log.Line("");
                log.Line(L.T("== R. 교체하지 못함(" + why + ") → v" + vA + " 그대로 다시 기동", "== R. could not swap (" + why + ") → starting v" + vA + " again as it was"));
                try { SystemAccess.DeleteFile(NewExe); log.Line(L.T("  " + Path.GetFileName(NewExe) + " 지움", "  deleted " + Path.GetFileName(NewExe))); }
                catch (TestIsolationViolation) { throw; }
                catch (Exception e) { log.Line(L.T("  " + Path.GetFileName(NewExe) + " 지우지 못함(다음 설치기가 잔재 B 로 지움): " + e.Message, "  could not delete " + Path.GetFileName(NewExe) + " (the next installer removes it as residue B): " + e.Message)); }
                return StartOld(L.T("업데이트하지 못했습니다(파일 교체 실패) — 이전 판 v" + vA + " 로 계속 동작합니다.", "The update failed (file swap failed) — continuing with the previous version v" + vA + "."));
            }

            /// <summary>
            /// 되돌림(업데이트 4 의 중단 임계 초과·새 감독 기동 실패 때만): update mutex 재획득 → 새 감독·코어 정지 → UartHub.exe → UartHub.bad.exe,
            /// 백업 → UartHub.exe → 옛 exe install --repair --quiet → update mutex 닫기 → 옛 sup → 오류 창.
            /// </summary>
            int Rollback(string why)
            {
                log.Line("");
                log.Line(L.T("== R. 되돌림: " + why + " → v" + vA, "== R. rollback: " + why + " → v" + vA));
                try { AcquireUpdate(); log.Line(L.T("  update mutex 재획득", "  re-acquired update mutex")); }
                catch (TestIsolationViolation) { throw; }
                catch (Exception e) { log.Line(L.T("  update mutex 재획득 실패(계속): " + e.Message, "  failed to re-acquire update mutex (continuing): " + e.Message)); }
                StopHub("rollback");
                if (oldBackup == null || !SystemAccess.FileExists(oldBackup))
                {
                    log.Line(L.T("  되돌릴 이전 판 없음(업데이트 전에 UartHub.exe 가 없었음)", "  no previous version to restore (UartHub.exe was missing before the update)"));
                    ReleaseUpdate();
                    return Fail(L.T("업데이트하지 못했습니다(되돌릴 이전 판 없음 — 허브 꺼짐).\n받은 exe 를 다시 실행하세요.", "The update failed (no previous version to restore — hub is off).\nRun the downloaded exe again."));
                }
                string bad = FreeName("UartHub.bad");
                try
                {
                    Retry(() => SystemAccess.MoveFile(instExe, bad, false));
                    log.Line("  UartHub.exe(v" + vB + ") → " + Path.GetFileName(bad));
                    Retry(() =>
                    {
                        if (SystemAccess.TestFail("rollback-rename")) throw new IOException("시험 주입(UART_HUB_TEST_FAIL=rollback-rename): 이름 바꾸기 실패");
                        SystemAccess.MoveFile(oldBackup, instExe, false);
                    });
                    log.Line("  " + Path.GetFileName(oldBackup) + "(v" + vA + ") → UartHub.exe");
                }
                catch (TestIsolationViolation) { throw; }
                catch (Exception e)
                {
                    log.Line(L.T("  되돌림 이름 바꾸기 실패: " + e.Message + " — " + string.Join("·", SystemAccess.ListFiles(instDir, "UartHub*.exe").Select(Path.GetFileName)) + " 가 남음, UartHub.exe 없음", "  rollback rename failed: " + e.Message + " — " + string.Join("·", SystemAccess.ListFiles(instDir, "UartHub*.exe").Select(Path.GetFileName)) + " remain, UartHub.exe is missing"));
                    ReleaseUpdate();
                    return Fail(L.T("업데이트하지 못했고 이전 판으로 되돌리지도 못했습니다(허브 꺼짐).\n받은 exe 를 다시 실행하면 복구됩니다.", "The update failed and the previous version could not be restored (hub is off).\nRun the downloaded exe again to repair."));
                }
                var r = SystemAccess.RunCapture(instExe, new[] { "install", "--repair", "--quiet" }, 60000);
                log.Line(L.T("  옛 exe install --repair --quiet → rc=" + r.Code + (r.Code != 0 ? " " + Trunc(r.OutText + r.Err, 300) : ""), "  old exe install --repair --quiet → rc=" + r.Code + (r.Code != 0 ? " " + Trunc(r.OutText + r.Err, 300) : "")));
                return StartOld(L.T("업데이트하지 못해 이전 판 v" + vA + " 로 되돌렸습니다(" + why + ").", "The update failed, so v" + vA + " was restored (" + why + ")."));
            }

            /// <summary>update mutex 닫기 → 옛 sup(주입 환경변수 없이) → 건강 확인 → 오류 창. 언제나 exit 1.</summary>
            int StartOld(string message)
            {
                ReleaseUpdate();
                log.Line(L.T("  update mutex 닫기", "  closed update mutex"));
                try
                {
                    if (SystemAccess.TestFail("old-sup-start")) throw new IOException("시험 주입(UART_HUB_TEST_FAIL=old-sup-start): 옛 감독 기동 실패");
                    started.Add(SystemAccess.StartDetached(instExe, new[] { "sup" }, true));
                    log.Line(L.T("  옛 감독(v" + vA + ") 분리 기동", "  starting the old supervisor (v" + vA + ") detached"));
                }
                catch (TestIsolationViolation) { throw; }
                catch (Exception e)
                {
                    log.Line(L.T("  옛 감독 기동 실패: " + e.Message + " — 허브 꺼짐(v" + vA + " 파일은 제자리)", "  failed to start the old supervisor: " + e.Message + " — hub is off (v" + vA + " files are in place)"));
                    return Fail(L.T(message + "\n이전 판의 허브도 다시 켜지 못했습니다(허브 꺼짐). 받은 exe 를 다시 실행하세요.", message + "\nThe previous version of the hub could not be started again either (hub is off). Run the downloaded exe again."));
                }
                bool ok = Health(vA, HealthPassUpdateS, false);
                return Fail(L.T(message + (ok ? "" : "\n(이전 판의 건강 확인도 실패 — hub.log 를 확인하세요)") + "\n받은 exe 를 다시 실행하면 복구됩니다.", message + (ok ? "" : "\n(the health check of the previous version failed too — check hub.log)") + "\nRun the downloaded exe again to repair."));
            }

            int Fail(string message)
            {
                log.Line(L.T("# 업데이트 실패(exit 1) — 로그 " + log.Path, "# update failed (exit 1) — log " + log.Path));
                ui.Finish(false, L.T(message + "\n로그 파일: " + log.Path, message + "\nLog file: " + log.Path), 0);
                return RcFailed;
            }

            // -------------------------------------------------------------- 1. 사전 점검
            void Precheck()
            {
                // '관리자 권한으로 실행'(UAC 켜짐 + 올린 토큰)만 막는다. UAC 가 꺼진 PC(EnableLUA=0)는 관리자 계정의 모든 프로세스가 관리자 토큰이라
                // 일반 권한으로 다시 실행할 방법이 없다 — 시작프로그램·감독도 같은 토큰으로 돌므로 경고만 하고 진행한다.
                var elev = SystemAccess.ElevationState;
                log.Line("  elevated=" + (elev == SystemAccess.Elevation.Normal ? "false" : elev == SystemAccess.Elevation.Elevated ? "true" : "true(uac_off)"));
                if (elev == SystemAccess.Elevation.Elevated) blockers.Add(L.T("관리자 권한으로 실행됨 — 일반 권한으로 다시 실행하세요(설치는 현재 사용자 폴더에만 씁니다, UAC 필요 없음)", "running as administrator — run again without elevation (setup writes only to the current user's folders; no UAC needed)"));
                else if (elev == SystemAccess.Elevation.UacOff) warnings.Add(L.T("이 PC 는 UAC 가 꺼져 있어(EnableLUA=0) 모든 프로그램이 관리자 토큰으로 돕니다 — 그대로 설치합니다(감독·시작프로그램도 같은 토큰)", "UAC is off on this PC (EnableLUA=0), so every program runs with an administrator token — installing as is (the supervisor and startup entry use the same token)"));
                if (SystemAccess.SamePath(self, instExe)) blockers.Add(L.T("설치 위치의 exe 로 install 을 실행함 — 받은 exe 를 실행하세요", "install was run from the exe in the install location — run the downloaded exe instead"));

                // PS 판·구 브리지(install.ps1:299-302 확장, WMI 명령줄). 자동 kill 하지 않는다.
                var how = new Dictionary<string, string>
                {
                    ["uart_hub.ps1"] = L.T("PS 판 감독 — 트레이 아이콘의 '감독 끝내기'(또는 <허브 폴더>\\sup\\req 에 quit 요청)", "PS-version supervisor — use 'End supervisor' on its tray icon (or write a quit request to <hub folder>\\sup\\req)"),
                    ["hub_core.ps1"] = L.T("PS 판 코어 — 그 PS 판의 `uart hub stop --json`(UART_HUB_DIR 를 그 허브 폴더로) 뒤 감독 끝내기", "PS-version core — run that PS version's `uart hub stop --json` (with UART_HUB_DIR set to its hub folder), then end the supervisor"),
                    ["uart_bridge.ps1"] = L.T("구 브리지 — 그 브리지의 `bridge.sh stop`", "old bridge — run that bridge's `bridge.sh stop`"),
                };
                var ps = SystemAccess.ScriptHosts();
                int found = 0;
                foreach (var p in ps)
                {
                    string cmd = p.CommandLine ?? "";
                    foreach (var kv in how)
                    {
                        if (cmd.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        found++;
                        blockers.Add(L.T("PowerShell 판 프로세스 실행 중: pid " + p.Pid + " (" + p.Name + ", " + kv.Key + ") — 정지 방법: " + kv.Value + ". 자동으로 끄지 않습니다. 끈 뒤 다시 실행하세요", "a PowerShell-version process is running: pid " + p.Pid + " (" + p.Name + ", " + kv.Key + ") — how to stop it: " + kv.Value + ". It is not stopped automatically. Stop it, then run again"));
                        break;
                    }
                }
                log.Line(L.T("  PowerShell 판·구 브리지 프로세스: " + (found == 0 ? "없음" : found + "개"), "  PowerShell-version and old bridge processes: " + (found == 0 ? "none" : found.ToString())));

                // 기존 설치본(업데이트 경로)
                existing = SystemAccess.FileExists(InstJson) ? InstallInfo.Load(InstJson) : null; LoadLang(existing?.HubDir);
                if (existing != null)
                {
                    vA = existing.Version ?? "?";
                    log.Line(L.T("  기존 설치본: " + InstJson + " v" + vA + " state=" + (existing.State ?? "?") + " → " + UpdateKind(), "  existing installation: " + InstJson + " v" + vA + " state=" + (existing.State ?? "?") + " → " + UpdateKind()));
                }
                else log.Line(L.T("  기존 설치본: 없음 → 첫 설치", "  existing installation: none → first install"));
                if (existing == null && CoreMain.MutexExists(SystemAccess.MutexName("sup")))
                    blockers.Add(L.T("다른 UART 허브 감독이 이미 실행 중(" + SystemAccess.MutexName("sup") + ") — 그 감독을 끝낸 뒤 다시 실행하세요", "another UART Hub supervisor is already running (" + SystemAccess.MutexName("sup") + ") — end it, then run again"));
                if (CoreMain.MutexExists(SystemAccess.MutexName("update")))
                    blockers.Add(L.T("다른 설치기·업데이트가 교체 중(" + SystemAccess.MutexName("update") + ")", "another installer/update is swapping files (" + SystemAccess.MutexName("update") + ")"));

                // 로그 위치(§2.3 j)
                DecideLogRoot();
                log.Line(L.T("  로그 위치: " + logRoot + " (" + logRootWhy + ")", "  log location: " + logRoot + " (" + logRootWhy + ")"));
                log.Line(L.T("  허브 폴더: " + hubDir, "  hub folder: " + hubDir));
                if (HubFiles.IsStopped(hubDir)) warnings.Add(L.T("허브 폴더의 control.json 이 stopped:true — 감독이 코어를 띄우지 않습니다(트레이 '시작'으로 재개)", "control.json in the hub folder has stopped:true — the supervisor will not start the core (resume with 'Start' in the tray)"));

                CheckConfig();
                CheckPorts();
                CheckSkillsTargets();

                // 이전 PS 작업 \uart_hub(install.ps1 이 등록) — 동작이 uart_hub.ps1 일 때만 지운다(P3)
                PsTaskAndTerminals();
            }

            /// <summary>config.json(§2.3 n, 시험은 seed_config.json — C-13): 있으면 그대로(새 케이블 질문 표시만), 없으면 만들 내용.</summary>
            void CheckConfig()
            {
                cfgExists = SystemAccess.FileExists(CfgPath);
                if (cfgExists)
                {
                    bool ask = false;
                    try { ask = Config.Load(SystemAccess.Guard(CfgPath, "read")).AskNewCables; } catch (Exception) { }
                    cfgNote = L.T("새 케이블 질문: " + (ask ? "켜짐" : "꺼짐") + "(기존 config.json)", "new-cable prompt: " + (ask ? "on" : "off") + " (existing config.json)");
                }
                else if (SystemAccess.IsTest)
                {
                    cfgContent = SystemAccess.ReadText(SystemAccess.TestFile("seed_config.json"));
                    if (cfgContent == null) throw SystemAccess.Violation("시험 모드인데 seed_config.json 없음 — 기본 config 는 실제 포트(7200·7001~)와 실제 FTDI 열거를 씀");
                    cfgNote = L.T("새 케이블 질문: " + (cfgContent.Contains("\"ask_new_cables\": true") || cfgContent.Contains("\"ask_new_cables\":true") ? "켜짐" : "꺼짐") + "(시험: seed_config.json)", "new-cable prompt: " + (cfgContent.Contains("\"ask_new_cables\": true") || cfgContent.Contains("\"ask_new_cables\":true") ? "on" : "off") + " (test: seed_config.json)");
                }
                else if (sub == "portable" || sub == "setup") { cfgContent = PortableDefaultConfig; cfgNote = L.T("연결: 필요할 때만(/connect-uart COMx — 꽂아도 잡지 않음, 새 config.json)", "connect: only when needed (/connect-uart COMx — plugging in does not connect, new config.json)"); }
                else { cfgContent = DefaultConfig; cfgNote = L.T("새 케이블 질문: 켜짐(새 config.json)", "new-cable prompt: on (new config.json)"); }
                log.Line(L.T("  config.json: " + (cfgExists ? "있음" : "없음 → 새로 만듦") + " · " + cfgNote, "  config.json: " + (cfgExists ? "exists" : "missing → will be created") + " · " + cfgNote));
            }

            /// <summary>skills 를 넣을 후보: WSL 배포판 이름만 + Windows %USERPROFILE%\.claude 유무.</summary>
            void CheckSkillsTargets()
            {
                // WSL 배포판 이름만(§2.3 g — 깨우는 탐침은 설치·건강 확인 뒤 SkillsDeployer 가). 이 결과를 skills 배치가 다시 쓴다(-l -q 를 두 번 기다리지 않게)
                wsl = SkillsDeployer.ListDistros();
                distros = wsl.Names;
                wslNote = wsl.Note;
                log.Line(L.T("  WSL 배포판: " + wslNote, "  WSL distros: " + wslNote));
                string wc = Path.Combine(SystemAccess.UserProfile, ".claude");
                winClaude = SystemAccess.DirExists(wc) ? Path.Combine(wc, "skills", "uart") : null;
                log.Line(L.T("  Windows Claude(" + wc + "): " + (winClaude != null ? "있음" : "없음"), "  Windows Claude (" + wc + "): " + (winClaude != null ? "found" : "not found")));
            }

            void PsTaskAndTerminals()
            {
                var q = SystemAccess.Schtasks(new[] { "/query", "/tn", "\\uart_hub", "/xml" }, 10000);
                if (q.Code == 0)
                {
                    string x = q.OutText;
                    string act = string.Join(" ", Regex.Matches(x, @"<(Command|Arguments)>(.*?)</\1>", RegexOptions.Singleline).Select(m => m.Groups[2].Value));
                    deletePsTask = act.IndexOf("uart_hub.ps1", StringComparison.OrdinalIgnoreCase) >= 0;
                    psTaskNote = L.T(deletePsTask ? "있음(동작 = uart_hub.ps1) → 삭제" : "있음(동작이 uart_hub.ps1 아님: " + Trunc(act, 120) + ") → 그대로 둠", deletePsTask ? "exists (action = uart_hub.ps1) → will be deleted" : "exists (action is not uart_hub.ps1: " + Trunc(act, 120) + ") → left alone");
                }
                else psTaskNote = L.T("없음", "none");
                log.Line(L.T("  이전 PS 작업 \\uart_hub: " + psTaskNote, "  old PS task \\uart_hub: " + psTaskNote));

                // 터미널(경로 후보만 — 정밀화는 T3)
                var cands = new[]
                {
                    new[] { Path.Combine(SystemAccess.ProgramFilesX86, "teraterm5", "ttermpro.exe"), Path.Combine(SystemAccess.ProgramFilesX86, "teraterm", "ttermpro.exe"),
                            Path.Combine(SystemAccess.ProgramFiles, "teraterm5", "ttermpro.exe"), Path.Combine(SystemAccess.ProgramFiles, "teraterm", "ttermpro.exe") },
                    new[] { Path.Combine(SystemAccess.ProgramFiles, "PuTTY", "putty.exe"), Path.Combine(SystemAccess.ProgramFilesX86, "PuTTY", "putty.exe") },
                    new[] { Path.Combine(SystemAccess.ProgramFilesX86, "Mobatek", "MobaXterm", "MobaXterm.exe"), Path.Combine(SystemAccess.ProgramFiles, "Mobatek", "MobaXterm", "MobaXterm.exe") },
                };
                for (int i = 0; i < 3; i++) terms[i] = cands[i].FirstOrDefault(SystemAccess.FileExists);
                log.Line(L.T("  터미널: " + TermText(), "  terminal: " + TermText()));
            }

            void DecideLogRoot()
            {
                if (existing != null && !string.IsNullOrEmpty(existing.HubDir))
                {
                    hubDir = existing.HubDir;
                    logRoot = !string.IsNullOrEmpty(existing.LogRoot) ? existing.LogRoot : Path.GetDirectoryName(hubDir.TrimEnd('\\'));
                    logRootWhy = L.T("이전 설치(install.json)의 허브 폴더", "hub folder of the previous install (install.json)");
                    return;
                }
                var d = SystemAccess.Drive("D");
                string dLogs = Path.Combine(d.Root, "uart_logs"), uLogs = Path.Combine(SystemAccess.UserProfile, "uart_logs");
                if (d.Exists && d.Ready && SystemAccess.FileExists(Path.Combine(dLogs, "hub", "token")))
                { logRoot = dLogs; LoadLang(Path.Combine(dLogs, "hub")); logRootWhy = L.T("이전 허브 폴더 재사용(D:\\uart_logs\\hub\\token 있음)", "reusing the previous hub folder (D:\\uart_logs\\hub\\token exists)"); }
                else if (SystemAccess.FileExists(Path.Combine(uLogs, "hub", "token")))
                { logRoot = uLogs; LoadLang(Path.Combine(uLogs, "hub")); logRootWhy = L.T("이전 허브 폴더 재사용(%USERPROFILE%\\uart_logs\\hub\\token 있음)", "reusing the previous hub folder (%USERPROFILE%\\uart_logs\\hub\\token exists)"); }
                else if (d.Type == "Fixed" && d.Ready)
                { logRoot = dLogs; logRootWhy = L.T("D: 가 로컬 고정 디스크", "D: is a local fixed disk"); }
                else
                {
                    string why = !d.Exists ? L.T("없음", "missing") : d.Type == "Removable" ? L.T("이동식", "removable") : d.Type == "Network" ? L.T("네트워크", "network") : !d.Ready ? L.T("준비 안 됨", "not ready") : d.Type;
                    logRoot = uLogs; logRootWhy = L.T("D: " + why + " → 사용자 폴더", "D: " + why + " → user folder");
                }
                hubDir = Path.Combine(logRoot, "hub");
            }

            /// <summary>skipOurs(X7 setup): UartHub 자신(실행 중인 허브 — 바꾸거나 그대로 둘 것)이 쥔 포트는 경고하지 않는다.</summary>
            void CheckPorts(bool skipOurs = false)
            {
                int ctl = 7200, bas = 7001;
                string src = cfgExists ? CfgPath : SystemAccess.IsTest ? SystemAccess.TestFile("seed_config.json") : null;
                if (src != null)
                {
                    try { var c = Config.Load(SystemAccess.Guard(src, "read")); ctl = c.ControlPort; bas = c.BasePort; }
                    catch (Exception) { }
                }
                var ports = new List<int> { ctl };
                for (int i = 0; i < 10; i++) ports.Add(bas + i);
                var owners = SystemAccess.TcpListeners(ports);
                if (skipOurs) owners = owners.Where(o => !(o.Name ?? "").StartsWith("UartHub", StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var o in owners)
                    warnings.Add(L.T("포트 " + o.Port + " 를 다른 프로그램이 쓰는 중(pid " + o.Pid + " " + (o.Name ?? "?") + ") — 허브가 이 포트를 못 쓸 수 있음(config.json 의 control_port·base_port 로 바꿀 수 있음)", "port " + o.Port + " is in use by another program (pid " + o.Pid + " " + (o.Name ?? "?") + ") — the hub may not be able to use it (change control_port/base_port in config.json)"));
                log.Line(L.T("  포트 " + ctl + "·" + bas + "~" + (bas + 9) + ": " + (owners.Count == 0 ? "사용 중 없음" : owners.Count + "개 사용 중(경고만)"), "  ports " + ctl + ", " + bas + "-" + (bas + 9) + ": " + (owners.Count == 0 ? "none in use" : owners.Count + " in use (warning only)")));
            }

            string TermText()
            {
                var l = Enumerable.Range(0, 3).Where(i => terms[i] != null).Select(i => TermNames[i] + "=" + terms[i]).ToList();
                return l.Count == 0 ? L.T("없음", "none") : string.Join(", ", l);
            }

            string SkillsText()
                => L.T("Windows " + (winClaude ?? "(없음 — %USERPROFILE%\\.claude 없음)") + " · WSL " + (distros.Count > 0 ? string.Join(", ", distros) : "(" + wslNote + ")"), "Windows " + (winClaude ?? "(none — no %USERPROFILE%\\.claude)") + " · WSL " + (distros.Count > 0 ? string.Join(", ", distros) : "(" + wslNote + ")"));

            List<string> Summary()
            {
                var l = new List<string>
                {
                    L.T("설치 위치: " + instDir, "Install location: " + instDir),
                    L.T("로그 위치: " + logRoot + "  (" + logRootWhy + ")", "Log location: " + logRoot + "  (" + logRootWhy + ")"),
                    L.T("허브 폴더: " + hubDir, "Hub folder: " + hubDir),
                    L.T("자동 실행: 로그온 때 시작 — " + StartupLnkPath, "Auto start: at logon — " + StartupLnkPath),
                    L.T("시작 메뉴: " + MenuDir + "\\{" + MenuMain + ", " + MenuUninstall + "}", "Start menu: " + MenuDir + "\\{" + MenuMain + ", " + MenuUninstall + "}"),
                    cfgNote,
                    L.T("Claude skills 를 넣을 곳(설치 뒤): " + SkillsText(), "Where Claude skills go (after install): " + SkillsText()),
                    L.T("바탕화면 아이콘: 보드 케이블을 꽂으면 '<보드> (<포트>)' 아이콘이 생깁니다(더블클릭 = 터미널)", "Desktop icons: plugging in a board cable creates a '<board> (<port>)' icon (double-click = terminal)"),
                    L.T("이전 PS 작업 \\uart_hub: " + psTaskNote, "Old PS task \\uart_hub: " + psTaskNote),
                };
                foreach (var w in warnings) l.Add(L.T("경고: " + w, "Warning: " + w));
                return l;
            }

            // -------------------------------------------------------------- 3~7 보조
            /// <summary>
            /// Claude skills 넣기·갱신(첫 설치 7·업데이트 5 — 건강 확인 뒤). 결과를 install.json skills[]·진행 창·설치 요약에. 실패는 경고만(설치는 계속).
            /// recorded = install.json 에 기록된 곳(업데이트 — 새로 생긴 곳은 사전 점검의 배포판 목록으로).
            /// </summary>
            void DeploySkills(List<SkillsDeployer.Place> recorded, IList<SkillsDeployer.Place> plan = null, DateTime? deadlineUtc = null)
            {
                log.Line(L.T("  Claude skills 배치(~/.claude 가 있는 곳만 — Windows " + Path.Combine(SystemAccess.UserProfile, ".claude") + " · WSL 배포판마다 탐침 30초)", "  deploying Claude skills (only where ~/.claude exists — Windows " + Path.Combine(SystemAccess.UserProfile, ".claude") + " · 30 s probe per WSL distro)"));
                ui?.Progress(L.T("Claude skills 배치 중…", "Deploying Claude skills…"));
                try
                {
                    var placed = SkillsDeployer.Deploy(installId, Hub.Version, instExe, wsl, recorded, log.Line, plan, deadlineUtc);
                    skillsPlaced = placed;
                    skillsSummary = SkillsDeployer.Summary(placed);
                    info.SetRaw("skills", SkillsDeployer.ToJson(placed));
                    SystemAccess.Guard(InstJson, "write");
                    if (!info.Save(InstJson)) log.Line(L.T("  경고: install.json skills[] 쓰기 실패", "  warning: failed to write install.json skills[]"));
                }
                catch (TestIsolationViolation) { throw; }
                catch (Exception e) { skillsSummary = L.T("실패(" + e.Message + ")", "failed (" + e.Message + ")"); log.Line(L.T("  경고: skills 배치 실패(" + e.GetType().Name + ": " + e.Message + ") — 설치는 계속", "  warning: skills deployment failed (" + e.GetType().Name + ": " + e.Message + ") — continuing setup")); }
                log.Line("  skills: " + skillsSummary);
                ui?.Progress("Claude skills: " + skillsSummary);
            }

            void VerifyCopy(string exe)
            {
                var r = SystemAccess.RunCapture(exe, new[] { "version", "--json" }, 30000);
                if (r.Code != 0) throw new IOException(L.T("복사본 version --json 실패 rc=" + r.Code + " " + Trunc(r.Err, 200), "copy version --json failed rc=" + r.Code + " " + Trunc(r.Err, 200)));
                string v = null, m = null;
                try
                {
                    using var d = JsonDocument.Parse(r.OutText.Trim());
                    v = HubFiles.Str(d.RootElement, "version"); m = HubFiles.Str(d.RootElement, "core_mvid");
                }
                catch (JsonException) { }
                string myM = typeof(Hub).Module.ModuleVersionId.ToString();
                if (v != Hub.Version || m != myM) throw new IOException(L.T("복사본이 다름: version=" + v + " core_mvid=" + m + " (기대 " + Hub.Version + " · " + myM + ")", "the copy differs: version=" + v + " core_mvid=" + m + " (expected " + Hub.Version + " · " + myM + ")"));
                log.Line(L.T("    version=" + v + " core_mvid=" + m + " — 일치", "    version=" + v + " core_mvid=" + m + " — match"));
            }

            void WriteInstallJson(string state)
            {
                info ??= new InstallInfo().SetRaw("skills", "[]");
                info.Version = Hub.Version; info.InstallId = installId; info.Exe = instExe; info.HubDir = hubDir; info.LogRoot = logRoot;
                info.IconsDir = SystemAccess.Desktop; info.State = state;
                info.Portable = sub == "portable" || sub == "setup";
                SystemAccess.Guard(InstJson, "write");
                if (!info.Save(InstJson)) throw new IOException(L.T("install.json 쓰기 실패", "failed to write install.json"));
            }

            void WritePrefs()
            {
                var o = new JObj().Set("terminal", "auto");
                for (int i = 0; i < 3; i++) o.Set(TermNames[i], terms[i]);
                SystemAccess.WriteText(PrefsPath, o.ToString() + "\n");
            }

            void DeletePsTask()
            {
                var r = SystemAccess.Schtasks(new[] { "/delete", "/tn", "\\uart_hub", "/f" }, 10000);
                if (r.Code != 0) log.Line(L.T("    삭제 실패 rc=" + r.Code + " " + Trunc(r.Err, 200) + " — 계속 진행(작업은 pwsh 판 감독을 띄우려 하지만 사전 점검처럼 공존하지 않게 사용법에 안내)", "    delete failed rc=" + r.Code + " " + Trunc(r.Err, 200) + " — continuing (the task would start a pwsh-version supervisor, but the usage notes keep the two from coexisting, as the pre-check does)"));
            }

            /// <summary>
            /// 건강 확인: sup.json running·core.pid listening(첫 설치). expect 가 있으면(업데이트·되돌림) 두 파일의 version 이 expect 이고
            /// `cli hub status --json` 의 version == supervisor.version == supervisor.core_version == expect 일 때까지. 중단 임계 45초(시험 UART_HUB_TEST_HEALTH_S).
            /// inject = 시험 주입 health 를 적용할지(새 판 확인에만). abortS = 중단 임계(setup 은 30초, X7).
            /// </summary>
            bool Health(string expect = null, double pass = HealthPassS, bool inject = true, double abortS = HealthAbortS)
            {
                var sw = Stopwatch.StartNew();
                double abort = SystemAccess.TestNumber("UART_HUB_TEST_HEALTH_S", 0, abortS);
                string why = null;
                bool stoppedLogged = false;
                while (sw.Elapsed.TotalSeconds < abort)
                {
                    if (HealthOk(out why, expect, out bool stopped))
                    {
                        if (stopped && !stoppedLogged) { log.Line(L.T("    허브는 정지 상태(control.json stopped) — 감독만 확인", "    hub is stopped (control.json stopped) — checking the supervisor only")); stoppedLogged = true; }
                        if (expect == null || stopped || CliVersions(expect, out why))
                        {
                            double s = sw.Elapsed.TotalSeconds;
                            log.Line(L.T("    건강 확인 OK " + s.ToString("0.0", CultureInfo.InvariantCulture) + "초 (합격선 " + pass + "초" + (s > pass ? " 초과 — 경고" : " 이내") + ")", "    health check OK " + s.ToString("0.0", CultureInfo.InvariantCulture) + " s (pass within " + pass + " s" + (s > pass ? " — exceeded, warning" : " — within") + ")"));
                            if (inject && SystemAccess.TestFail("health")) { log.Line(L.T("    시험 주입(UART_HUB_TEST_FAIL=health): 건강 확인 실패로 처리", "    test injection (UART_HUB_TEST_FAIL=health): treated as health check failure")); return false; }
                            return true;
                        }
                        Thread.Sleep(400);
                        continue;
                    }
                    Thread.Sleep(100);
                }
                log.Line(L.T("    건강 확인 실패: " + abort.ToString("0", CultureInfo.InvariantCulture) + "초 안에 준비되지 않음(" + why + ")", "    health check failed: not ready within " + abort.ToString("0", CultureInfo.InvariantCulture) + " s (" + why + ")"));
                return false;
            }

            bool HealthOk(out string why, string expect, out bool stopped)
            {
                stopped = false;
                var s = HubFiles.ReadJson(HubFiles.SupState(hubDir));
                if (!s.HasValue) { why = L.T("sup.json 없음", "sup.json missing"); return false; }
                string st = HubFiles.Str(s.Value, "state");
                if (!HubFiles.IsAlive(HubFiles.Long(s.Value, "pid"), HubFiles.Long(s.Value, "start_ticks"))) { why = L.T("sup.json 의 감독이 살아 있지 않음", "the supervisor in sup.json is not alive"); return false; }
                if (expect != null && HubFiles.Str(s.Value, "version") != expect) { why = "sup.json version=" + HubFiles.Str(s.Value, "version"); return false; }
                if (st == "stopped" && HubFiles.IsStopped(hubDir)) { why = null; stopped = true; return true; }
                if (st != "running") { why = "sup.json state=" + st; return false; }
                var c = HubFiles.ReadJson(HubFiles.CorePid(hubDir));
                if (!c.HasValue) { why = L.T("core.pid 없음", "core.pid missing"); return false; }
                if (HubFiles.Str(c.Value, "control") != "listening") { why = "core.pid control=" + HubFiles.Str(c.Value, "control"); return false; }
                if (!HubFiles.IsAlive(HubFiles.Long(c.Value, "pid"), HubFiles.Long(c.Value, "start_ticks"))) { why = L.T("core.pid 의 코어가 살아 있지 않음", "the core in core.pid is not alive"); return false; }
                if (expect != null && HubFiles.Str(c.Value, "version") != expect) { why = "core.pid version=" + HubFiles.Str(c.Value, "version"); return false; }
                why = null;
                return true;
            }

            /// <summary>설치본 `cli hub status --json`: version(코어) == supervisor.version == supervisor.core_version == expect.</summary>
            bool CliVersions(string expect, out string why)
            {
                var r = SystemAccess.RunCapture(instExe, new[] { "cli", "hub", "status", "--json" }, 15000);
                string core = null, sv = null, svCore = null;
                try
                {
                    using var d = JsonDocument.Parse(r.OutText.Trim());
                    core = HubFiles.Str(d.RootElement, "version");
                    if (d.RootElement.TryGetProperty("supervisor", out var s) && s.ValueKind == JsonValueKind.Object) { sv = HubFiles.Str(s, "version"); svCore = HubFiles.Str(s, "core_version"); }
                }
                catch (JsonException) { }
                why = "hub status rc=" + r.Code + " version=" + core + " supervisor.version=" + sv + " supervisor.core_version=" + svCore;
                bool ok = r.Code == 0 && core == expect && sv == expect && svCore == expect;
                if (ok) log.Line(L.T("    " + why + " — 모두 v" + expect, "    " + why + " — all v" + expect));
                return ok;
            }

            // -------------------------------------------------------------- 8. 첫 설치 실패 정리
            int FirstFail(string why)
            {
                log.Line("");
                log.Line(L.T("== 8. 첫 설치 실패 정리: " + why, "== 8. first-install failure cleanup: " + why));
                try { AcquireUpdate(); log.Line(L.T("  update mutex 재획득", "  re-acquired update mutex")); } catch (Exception e) { log.Line(L.T("  update mutex 재획득 실패(계속): " + e.Message, "  failed to re-acquire update mutex (continuing): " + e.Message)); }
                StopHub("install-first-fail");
                RemoveStartShortcuts();
                string failed = Path.Combine(hubDir, "install_failed_" + ts + ".log");
                try
                {
                    SystemAccess.CreateDir(hubDir);
                    log.MoveTo(failed);
                    log.Line("  install.log → " + failed);
                }
                catch (TestIsolationViolation) { throw; }
                catch (Exception e) { log.Line(L.T("  install.log 복사 실패: " + e.Message, "  failed to copy install.log: " + e.Message)); failed = log.Path ?? L.T("(없음)", "(none)"); }
                try
                {
                    Retry(() => SystemAccess.DeleteDir(instDir));
                    log.Line(L.T("  설치 폴더 삭제 " + instDir, "  deleted install folder " + instDir));
                }
                catch (TestIsolationViolation) { throw; }
                catch (Exception e) { log.Line(L.T("  설치 폴더 삭제 실패: " + e.Message, "  failed to delete install folder: " + e.Message)); }
                log.Line(L.T("  남김: 허브 폴더 " + hubDir + "(token·config.json·hub.log), 로그 폴더 " + logRoot + " — 다음 설치가 다시 씀", "  kept: hub folder " + hubDir + " (token, config.json, hub.log), log folder " + logRoot + " — the next setup reuses them"));
                log.Line(L.T("  skills: 아직 배치 전 — 할 일 없음", "  skills: not deployed yet — nothing to do"));
                ReleaseUpdate();
                log.Line(L.T("# 설치 실패(exit 1) — 로그 " + failed, "# setup failed (exit 1) — log " + failed));
                ui?.Finish(false, L.T("설치하지 못했습니다. 로그 파일: " + failed + "\n이 파일을 담당자에게 보내 주세요.", "Setup failed. Log file: " + failed + "\nPlease send this file to the person in charge."), 0);
                return RcFailed;
            }

            /// <summary>감독·코어를 내린다: 감독에 shutdown(C-5) → 20초 → 코어에 core.stop → 10초 → 검증 PID kill(install.ps1:85-92 규칙).</summary>
            void StopHub(string from)
            {
                if (string.IsNullOrEmpty(hubDir)) { log.Line(L.T("  허브 폴더 모름 — 정지 건너뜀", "  hub folder unknown — skipping the stop")); return; }
                var sup = Rec(HubFiles.SupState(hubDir));
                var core = Rec(HubFiles.CorePid(hubDir));
                var mine = started.Where(p => HubFiles.IsAlive(p.Pid, p.St)).ToList();
                bool supAlive = HubFiles.IsAlive(sup.Pid, sup.St) || mine.Count > 0;
                log.Line(L.T("  감독 " + (supAlive ? "pid=" + (sup.Pid > 0 ? sup.Pid : mine[0].Pid) : "없음") + " · 코어 " + (HubFiles.IsAlive(core.Pid, core.St) ? "pid=" + core.Pid : "없음"), "  supervisor " + (supAlive ? "pid=" + (sup.Pid > 0 ? sup.Pid : mine[0].Pid) : "none") + " · core " + (HubFiles.IsAlive(core.Pid, core.St) ? "pid=" + core.Pid : "none")));
                if (supAlive)
                {
                    if (SystemAccess.TestFail("shutdown")) log.Line(L.T("  감독 요청 shutdown → 무응답(시험 주입 UART_HUB_TEST_FAIL=shutdown — 요청을 처리하지 않는 감독으로 둠)", "  shutdown request to the supervisor → no response (test injection UART_HUB_TEST_FAIL=shutdown — leaving the supervisor as one that ignores requests)"));
                    else
                    {
                        var r = SupervisorChannel.Request(hubDir, "shutdown", from, 10000);
                        log.Line(L.T("  감독 요청 shutdown → " + r, "  shutdown request to the supervisor → " + r));
                        WaitGone(new[] { sup, core }.Concat(mine), 20000);
                    }
                }
                core = Rec(HubFiles.CorePid(hubDir)).Pid > 0 ? Rec(HubFiles.CorePid(hubDir)) : core;
                if (HubFiles.IsAlive(core.Pid, core.St))
                {
                    if (SystemAccess.TestFail("core.stop")) log.Line(L.T("  대체 경로: core.stop(pid=" + core.Pid + ") → 무응답(시험 주입 UART_HUB_TEST_FAIL=core.stop — 쓰지 않음)", "  fallback: core.stop (pid=" + core.Pid + ") → no response (test injection UART_HUB_TEST_FAIL=core.stop — not used)"));
                    else
                    {
                        var o = new JObj().Set("pid", (long)core.Pid).Set("reason", from).Set("at", HubFiles.Iso(DateTime.Now));
                        SystemAccess.WriteText(HubFiles.CoreStop(hubDir), o.ToString());
                        log.Line(L.T("  대체 경로: 감독이 내리지 못함 → core.stop(pid=" + core.Pid + ") 정상 종료 10초 대기", "  fallback: the supervisor could not stop it → core.stop (pid=" + core.Pid + "), waiting 10 s for a clean exit"));
                        WaitGone(new[] { core }, 10000);
                    }
                }
                // 남은 것은 검증 PID 만 kill. 감독 먼저(살아 있는 감독이 코어를 다시 띄우지 못하게 — 코어의 C-11 은 update mutex 때문에 감독을 띄우지 않는다)
                foreach (var p in new[] { sup }.Concat(mine).Concat(new[] { core }).Distinct())
                    if (HubFiles.IsAlive(p.Pid, p.St))
                    {
                        bool killed = SystemAccess.KillVerified(p.Pid, p.St);      // 한 번만(L.T 인자 안에서 부르면 두 번 죽이고 영어는 늘 "failed")
                        log.Line(L.T("  대체 경로: 검증 PID kill pid=" + p.Pid + " → " + (killed ? "끝남" : "실패"), "  fallback: kill verified PID pid=" + p.Pid + " → " + (killed ? "ended" : "failed")));
                    }
                bool any = new[] { core, sup }.Concat(mine).Any(p => HubFiles.IsAlive(p.Pid, p.St));
                log.Line(L.T("  허브 정지 " + (any ? "실패 — 남은 프로세스 있음" : "완료"), "  hub stop " + (any ? "failed — processes remain" : "done")));
            }

            static (int Pid, long St) Rec(string file)
            {
                var j = HubFiles.ReadJson(file);
                return j.HasValue ? ((int)HubFiles.Long(j.Value, "pid"), HubFiles.Long(j.Value, "start_ticks")) : (0, 0);
            }

            static void WaitGone(IEnumerable<(int Pid, long St)> ps, int ms)
            {
                var l = ps.Where(p => p.Pid > 0).ToList();
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < ms && l.Any(p => HubFiles.IsAlive(p.Pid, p.St))) Thread.Sleep(100);
            }

            /// <summary>시작프로그램 바로가기·시작 메뉴 폴더(자기 것만).</summary>
            void RemoveStartShortcuts()
            {
                if (SystemAccess.FileExists(StartupLnkPath))
                {
                    if (IsOurLnk(StartupLnkPath, instExe, "sup")) { Retry(() => SystemAccess.DeleteFile(StartupLnkPath)); log.Line(L.T("  삭제 " + StartupLnkPath, "  deleted " + StartupLnkPath)); }
                    else log.Line(L.T("  남의 바로가기라 남김: " + StartupLnkPath, "  not our shortcut, kept: " + StartupLnkPath));
                }
                RemoveMenu();
            }

            void RemoveMenu()
            {
                foreach (var (n, a) in new[] { (MenuMain, "sup"), (MenuUninstall, "uninstall") })
                {
                    string p = Path.Combine(MenuDir, n);
                    if (!SystemAccess.FileExists(p)) continue;
                    if (IsOurLnk(p, instExe, a)) { Retry(() => SystemAccess.DeleteFile(p)); log.Line(L.T("  삭제 " + p, "  deleted " + p)); }
                    else log.Line(L.T("  남의 바로가기라 남김: " + p, "  not our shortcut, kept: " + p));
                }
                if (SystemAccess.DirExists(MenuDir) && SystemAccess.ListFiles(MenuDir, "*").Length == 0 && SystemAccess.ListDirs(MenuDir).Length == 0)
                {
                    Retry(() => SystemAccess.DeleteDir(MenuDir));
                    log.Line(L.T("  삭제 " + MenuDir, "  deleted " + MenuDir));
                }
            }

            // ============================================================== 휴대용 실행(인자 없음, X4)
            /// <summary>
            /// 인자 없는 실행: 이 exe 를 그 자리에서 허브로 쓴다(자기 복사·다른 위치 실행·바로가기·작업 스케줄러 없음).
            /// 처음(portable install.json 없음) = 확인 창 1회 → PortableFirst. 감독이 있으면 같은 exe = 풍선만, 다른 exe = 확인 창 → 교체. 그 밖 = 창 없이 시작.
            /// </summary>
            int Portable()
            {
                instExe = self;
                log.Line(L.T("# UART 허브 시작(휴대용) v" + Hub.Version, "# UART Hub start (portable) v" + Hub.Version));
                log.Line(L.T("# exe=" + self + " pid=" + Environment.ProcessId + " 사용자=" + Environment.UserName, "# exe=" + self + " pid=" + Environment.ProcessId + " user=" + Environment.UserName));
                if (SystemAccess.IsTest) log.Line(L.T("# 시험 모드(C-13) root=" + SystemAccess.TestRoot, "# test mode (C-13) root=" + SystemAccess.TestRoot));
                log.Line(L.T("# 설정 파일=" + InstJson + " (실행 파일·바로가기는 두지 않음)", "# settings file=" + InstJson + " (no executable or shortcuts are placed)"));
                Step(L.T("1. 상태", "1. State"));
                existing = SystemAccess.FileExists(InstJson) ? InstallInfo.Load(InstJson) : null; LoadLang(existing?.HubDir);
                if (existing != null && !existing.Portable)
                    return Blocked(L.T("이전 방식으로 설치된 UART 허브가 있습니다(" + instDir + ", install.json state=" + (existing.State ?? "?") + ").\n"
                                   + "먼저 제거하세요: 시작 메뉴 'UART 허브 제거' 또는 " + SystemAccess.InstalledExe + " uninstall — 로그·설정은 남습니다. 그 뒤 이 exe 를 다시 실행하세요.\n"
                                   + "(설치가 중간에 끊겨 제거되지 않으면 " + instDir + " 폴더와 시작 메뉴 'UART 허브' 폴더를 직접 지우세요)", "A UART Hub installed the old way exists (" + instDir + ", install.json state=" + (existing.State ?? "?") + ").\n"
                                   + "Remove it first: Start menu 'Remove UART Hub' or " + SystemAccess.InstalledExe + " uninstall — logs and settings are kept. Then run this exe again.\n"
                                   + "(If setup was interrupted and removal does not work, delete the " + instDir + " folder and the Start menu 'UART Hub' folder yourself)"));
                if (CoreMain.MutexExists(SystemAccess.MutexName("update"))) return Blocked(L.T("다른 설치기·업데이트가 교체 중(" + SystemAccess.MutexName("update") + ") — 끝난 뒤 다시 실행하세요", "another installer/update is swapping files (" + SystemAccess.MutexName("update") + ") — run again after it finishes"));
                bool first = existing == null || existing.State != "installed" || string.IsNullOrEmpty(existing.HubDir);
                if (first) existing = null;
                DecideLogRoot();
                bool supUp = CoreMain.MutexExists(SystemAccess.MutexName("sup"));
                string runExe = null, runVer = null;
                if (supUp) RunningSup(out runExe, out runVer);
                log.Line(L.T("  install.json: " + (first ? "없음(처음 실행)" : "v" + existing.Version + " exe=" + existing.Exe), "  install.json: " + (first ? "none (first run)" : "v" + existing.Version + " exe=" + existing.Exe)));
                log.Line(L.T("  로그 위치: " + logRoot + " (" + logRootWhy + ")", "  log location: " + logRoot + " (" + logRootWhy + ")"));
                log.Line(L.T("  허브 폴더: " + hubDir, "  hub folder: " + hubDir));
                log.Line(L.T("  감독: " + (supUp ? "실행 중 exe=" + (runExe ?? "(모름)") + " v" + (runVer ?? "?") : "없음"), "  supervisor: " + (supUp ? "running exe=" + (runExe ?? "(unknown)") + " v" + (runVer ?? "?") : "none")));
                if (first)
                {
                    if (supUp) return Blocked(L.T("다른 UART 허브 감독이 이미 실행 중(" + SystemAccess.MutexName("sup") + ", exe=" + (runExe ?? "모름") + ") — 그 허브를 끈 뒤 다시 실행하세요", "another UART Hub supervisor is already running (" + SystemAccess.MutexName("sup") + ", exe=" + (runExe ?? "unknown") + ") — stop that hub, then run again"));
                    return PortableFirst();
                }
                if (!supUp && Older(Hub.Version, existing.Version) && !string.IsNullOrEmpty(existing.Exe) && !SystemAccess.SamePath(existing.Exe, self) && SystemAccess.FileExists(existing.Exe))
                    return StartRegistered();
                if (supUp && runExe != null && SystemAccess.SamePath(runExe, self))
                {
                    var r = SupervisorChannel.Request(hubDir, "notice_running", "portable", 5000);
                    log.Line(L.T("  이미 실행 중(같은 exe) — 트레이 풍선으로 알림: " + r, "  already running (same exe) — notified with a tray balloon: " + r));
                    log.Line(L.T("# 이미 실행 중 — 바꾼 것 없음", "# already running — nothing changed"));
                    return RcOk;
                }
                return PortableStart(supUp, runExe, runVer);
            }

            /// <summary>
            /// X7: 등록된 exe 가 이 exe 보다 새 판이고 허브가 꺼져 있으면(예: 예전 exe 를 가리키는 shell:startup 바로 가기로 로그온) 등록을 옛 판으로 바꾸지 않고
            /// 등록된 exe 로 감독을 띄운다. 창은 실패 때만(오류 창).
            /// </summary>
            int StartRegistered()
            {
                info = existing;
                installId = existing.InstallId;
                instExe = existing.Exe;
                log.Line(L.T("  이 exe(v" + Hub.Version + ")가 등록된 exe(" + existing.Exe + " v" + existing.Version + ")보다 오래됨 — 등록은 그대로 두고 등록된 exe 로 켬(옛 바로 가기로 판이 되돌아가지 않게)", "  this exe (v" + Hub.Version + ") is older than the registered exe (" + existing.Exe + " v" + existing.Version + ") — leaving the registration alone and starting the registered exe (so an old shortcut cannot roll the version back)"));
                try
                {
                    if (SystemAccess.DirExists(hubDir)) log.Attach(Path.Combine(hubDir, "install.log"));
                    Step(L.T("2. 등록된 exe 로 감독 기동 → 건강 확인", "2. Start the supervisor from the registered exe → health check"));
                    Act(L.T("\"" + existing.Exe + "\" sup 분리 기동(등록된 새 판)", "start \"" + existing.Exe + "\" sup detached (registered newer version)"), () => started.Add(SystemAccess.StartDetached(existing.Exe, new[] { "sup", "--icons-dir", SupIconsDir }, false, new[] { "UART_HUB_DIR" })));
                    if (!Health(existing.Version))
                    {
                        log.Line(L.T("# 시작 실패(exit 1) — 로그 " + log.Path, "# start failed (exit 1) — log " + log.Path));
                        ui.Finish(false, FailText(L.T("등록된 exe " + existing.Exe + " 의 건강 확인 실패", "health check of the registered exe " + existing.Exe + " failed"), L.T("UART 허브를 시작하지 못했습니다(등록된 exe " + existing.Exe + " 의 건강 확인 실패). 로그 파일: " + log.Path, "Could not start UART Hub (health check of the registered exe " + existing.Exe + " failed). Log file: " + log.Path)), 0);
                        return RcFailed;
                    }
                }
                catch (TestIsolationViolation) { throw; }
                catch (Exception e)
                {
                    log.Line(L.T("  오류: " + e.GetType().Name + ": " + e.Message, "  error: " + e.GetType().Name + ": " + e.Message));
                    ui.Finish(false, FailText(e.GetType().Name + ": " + e.Message, L.T("UART 허브를 시작하지 못했습니다: " + e.Message + "\n로그 파일: " + (log.Path ?? "(없음)"), "Could not start UART Hub: " + e.Message + "\nLog file: " + (log.Path ?? "(none)"))), 0);
                    return RcFailed;
                }
                log.Line("");
                log.Line(L.T("# 시작 완료(휴대용) — 등록된 새 판 v" + existing.Version + " 로 켬(이 exe v" + Hub.Version + " 은 쓰지 않음, 등록 그대로)", "# start done (portable) — started the registered newer version v" + existing.Version + " (this exe v" + Hub.Version + " is not used; registration unchanged)"));
                return RcOk;
            }

            int Blocked(string why)
            {
                log.Line(L.T("  중단: " + why.Replace("\n", " "), "  blocked: " + why.Replace("\n", " ")));
                ui?.Finish(false, why, 0);
                return RcBlocked;
            }

            /// <summary>실행 중인 감독의 exe·버전(sup.json — pid·시작 시각이 살아 있을 때만, exe 가 없으면 그 프로세스의 이미지 경로).</summary>
            void RunningSup(out string exe, out string ver)
            {
                exe = ver = null;
                var j = HubFiles.ReadJson(HubFiles.SupState(hubDir));
                if (!j.HasValue) return;
                long pid = HubFiles.Long(j.Value, "pid"), st = HubFiles.Long(j.Value, "start_ticks");
                if (!HubFiles.IsAlive(pid, st)) return;
                ver = HubFiles.Str(j.Value, "version");
                exe = HubFiles.Str(j.Value, "exe");
                if (string.IsNullOrEmpty(exe))
                    try { using var p = Process.GetProcessById((int)pid); exe = p.MainModule?.FileName; } catch (Exception) { exe = null; }
            }

            string SupIconsDir => info?.IconsDir ?? existing?.IconsDir ?? SystemAccess.Desktop;

            /// <summary>이 exe 의 운영 감독(install.json 으로 허브 폴더를 정함 — 부르는 셸의 UART_HUB_DIR 은 물려주지 않는다, X7).</summary>
            void StartSup() => started.Add(SystemAccess.StartDetached(self, new[] { "sup", "--icons-dir", SupIconsDir }, false, new[] { "UART_HUB_DIR" }));

            /// <summary>
            /// 허브 폴더·토큰·config.json 이 없으면 만든다(있으면 그대로) → config 에 on_demand·log_layout 3 보장(C-12 글자 편집, 2 는 3 으로) →
            /// log_layout 2·3 이면 옛 배치(Phase A)의 로그를 옮기고(LogMigration, 허브가 꺼진 채 update mutex 를 쥔 동안 — 2 → 3 때 콘솔로그·에이징이벤트는 옛 기록으로 그대로) →
            /// 콘솔 로그의 live 폴더(layout 3 = &lt;허브 폴더&gt;\임시기록\live).
            /// </summary>
            void EnsureHubFiles()
            {
                if (SystemAccess.DirExists(hubDir)) log.Line(L.T("  " + hubDir + " 이미 있음", "  " + hubDir + " already exists"));
                else Act(L.T("폴더 " + hubDir, "folder " + hubDir), () => SystemAccess.CreateDir(hubDir));
                bool haveTok = TokenFile.IsValid(TokenPath);
                Act(L.T("토큰 " + TokenPath + (haveTok ? " 이미 있음 → 유지" : " 새로 만듦(32자 hex) + ACL(상속 끊기, 현재 사용자만 FullControl)"), "token " + TokenPath + (haveTok ? " already exists → kept" : " created (32 hex chars) + ACL (inheritance removed, FullControl for the current user only)")), () => TokenFile.Ensure(TokenPath));
                if (cfgExists) { log.Line(L.T("  config.json 이미 있음(" + cfgNote + ")", "  config.json already exists (" + cfgNote + ")")); EnsureOnDemand(); EnsureLogLayout(); }
                else
                {
                    string c = Config.WithLogLayout(cfgContent, out _) ?? cfgContent;     // 시험 seed 에도(휴대용 기본값에는 이미 있음)
                    Act(L.T("config.json 새로 만듦(" + cfgNote + "): " + c.Trim().Replace("\n", " "), "create config.json (" + cfgNote + "): " + c.Trim().Replace("\n", " ")), () => SystemAccess.WriteText(CfgPath, c));
                }
                var rc = ResolvedConfig();
                if (rc.LogLayout >= 2)
                {
                    if (CoreMain.MutexExists(SystemAccess.MutexName("core"))) log.Line(L.T("  로그 폴더 정리: 코어가 아직 실행 중이라 옮기지 않음(다음 시작 때 다시)", "  log folder cleanup: the core is still running, so nothing was moved (will retry at the next start)"));
                    else LogMigration.Run(rc.BaseRoot, hubDir, log.Line);
                }
                string live = Path.Combine(rc.LogRoot, "live");
                if (SystemAccess.DirExists(live)) log.Line(L.T("  " + live + " 이미 있음", "  " + live + " already exists"));
                else Act(L.T("폴더 " + live, "folder " + live), () => SystemAccess.CreateDir(live));
            }

            /// <summary>
            /// 부록 C.11·C.12: 이미 있는 config.json 에 "log_layout" 키가 없으면 "log_layout": 3 을 더하고, 2 면 3 으로 바꾼다 — EnsureOnDemand 와 같은 글자 편집(C-12).
            /// 1 은 사용자 선택으로 보고 그대로 둔다.
            /// </summary>
            void EnsureLogLayout()
            {
                string text = SystemAccess.ReadText(CfgPath);
                string n = Config.WithLogLayout(text, out string why);
                if (n == null) { log.Line("  config.json log_layout: " + why); return; }
                Act(L.T("config.json 에 \"log_layout\": " + Config.CurrentLogLayout + " 보장(에이징 세션 — 다른 키·주석 그대로, " + (text ?? "").Length + "→" + n.Length + "자)", "ensure \"log_layout\": " + Config.CurrentLogLayout + " in config.json (aging runs — other keys and comments untouched, " + (text ?? "").Length + "→" + n.Length + " chars)"), () => SystemAccess.WriteText(CfgPath, n));
            }

            /// <summary>코어·CLI 와 같은 규칙으로 푼 설정(Config.ResolveLogRoot — 기준 폴더·콘솔 로그·사건 폴더). 읽지 못하면 기본값(log_root = install.json 의 기준).</summary>
            Config ResolvedConfig()
            {
                Config c = null;
                try { string t = SystemAccess.ReadText(CfgPath); if (t != null) c = Config.Parse(t.TrimStart('\uFEFF')); }
                catch (ConfigException) { }
                c ??= Config.Default();
                if (string.IsNullOrEmpty(c.LogRoot)) c.LogRoot = logRoot;
                c.HubDir = hubDir;
                c.ResolveLogRoot(hubDir);
                return c;
            }

            /// <summary>
            /// 휴대용 시작(X5 부록 B.4): 이미 있는 config.json 에 "on_demand" 키가 없으면 "on_demand": true 를 더한다 — 다른 키·주석·순서는 그대로(C-12,
            /// Config.WithOnDemand 가 글자로 끼워 넣고 다시 읽어 확인). 키가 있으면(false 포함) 사용자 선택으로 보고 그대로 둔다.
            /// </summary>
            void EnsureOnDemand()
            {
                string text = SystemAccess.ReadText(CfgPath);
                string n = Config.WithOnDemand(text, out string why);
                if (n == null) { log.Line("  config.json on_demand: " + why); return; }
                Act(L.T("config.json 에 \"on_demand\": true 더함(필요할 때만 연결 — 다른 키·주석 그대로, " + (text ?? "").Length + "→" + n.Length + "자)", "add \"on_demand\": true to config.json (connect only when needed — other keys and comments untouched, " + (text ?? "").Length + "→" + n.Length + " chars)"), () => SystemAccess.WriteText(CfgPath, n));
            }

            /// <summary>처음 실행: 확인 창 1회 → 허브 폴더·토큰·config → install.json(portable) → sup → 건강 확인 → skills → 완료 창(5초).</summary>
            int PortableFirst()
            {
                CheckConfig();
                CheckSkillsTargets();
                Step(L.T("2. 확인 창(처음 한 번)", "2. Confirm window (first time only)"));
                var lines = new List<string>
                {
                    L.T("이 exe 를 지금 있는 곳에서 그대로 씁니다(설치·복사 없음): " + self, "This exe is used right where it is (no install or copy): " + self),
                    L.T("로그 위치: " + logRoot + "  (" + logRootWhy + ")", "Log location: " + logRoot + "  (" + logRootWhy + ")"),
                    L.T("허브 폴더: " + hubDir, "Hub folder: " + hubDir),
                    cfgNote,
                    L.T("Claude skills 를 넣을 곳: " + SkillsText(), "Where Claude skills go: " + SkillsText()),
                    L.T("설정 파일: " + InstJson + " (실행 파일은 두지 않음)", "Settings file: " + InstJson + " (no executable is placed)"),
                    L.T("자동 실행은 직접: exe 를 오른쪽 버튼으로 끌어 shell:startup 폴더(Win+R → shell:startup)에 '바로 가기 만들기'(트레이 → 시작프로그램 폴더 열기)", "Auto start is up to you: right-drag the exe into the shell:startup folder (Win+R → shell:startup) and choose 'Create shortcuts here' (tray → open startup folder)"),
                    L.T("바탕화면 아이콘: 보드 케이블을 꽂으면 '<보드> (<포트>)' 아이콘이 생깁니다(더블클릭 = 터미널)", "Desktop icons: plugging in a board cable creates a '<board> (<port>)' icon (double-click = terminal)"),
                    L.T("끄기·업데이트: 트레이 아이콘 → 종료(허브 끄기) 뒤 exe 를 바꾸고 다시 더블클릭", "Stop / update: tray icon → Quit hub, then replace the exe and double-click it again"),
                };
                foreach (var l in lines) log.Line("  | " + l);
                if (!ui.Confirm(FirstRunText())) { log.Line(L.T("  취소 — 아무것도 바꾸지 않음(exit 2)", "  cancelled — nothing was changed (exit 2)")); return RcCancelled; }
                ui.Steps(InstallCopy.FirstHead, InstallCopy.StepsFirst);
                try
                {
                    ui.Stage(0);
                    Step(L.T("3. 허브 폴더·토큰·config.json", "3. Hub folder, token, config.json"));
                    EnsureHubFiles();
                    log.Attach(Path.Combine(hubDir, "install.log"));
                    Step("4. install.json(portable)");
                    installId = Guid.NewGuid().ToString("N");
                    Act("install.json state=installed portable=true (exe=" + self + ", install_id=" + installId + ", hub_dir=" + hubDir + ")", () => WriteInstallJson("installed"));
                    Step(L.T("5. 감독 기동 → 건강 확인", "5. Start the supervisor → health check"));
                    Act(L.T("\"" + self + "\" sup 분리 기동(이 exe 그대로)", "start \"" + self + "\" sup detached (this exe as is)"), StartSup);
                    if (!Health()) return PortableFirstFail(L.T("건강 확인 실패", "health check failed"));
                    ui.Stage(1);
                    Step("6. Claude skills");
                    DeploySkills(null);
                    ui.Stage(2);
                }
                catch (TestIsolationViolation) { throw; }
                catch (Exception e)
                {
                    log.Line(L.T("  오류: " + e.GetType().Name + ": " + e.Message, "  error: " + e.GetType().Name + ": " + e.Message));
                    return PortableFirstFail(e.GetType().Name + ": " + e.Message);
                }
                log.Line("");
                log.Line(L.T("# 시작 완료(휴대용) v" + Hub.Version, "# start done (portable) v" + Hub.Version));
                ui.Finish(true, ReadyText(), 5000);
                return RcOk;
            }

            // ---- 화면 글(InstallCopy, UI_COPY.md). install.log 의 "  | " 줄은 기술 기록으로 그대로 둔다.
            UiText FirstRunText() => InstallCopy.FirstRun(logRoot, InstallCopy.SkillsPlaces(winClaude != null, distros), self, hubDir, InstJson);
            UiText ReadyText() => InstallCopy.Ready(logRoot, skillsSummary);
            UiText FailText(string why, string tech) => InstallCopy.Fail(why, log.Path, tech);

            /// <summary>처음 실행 실패: 띄운 감독·코어를 내리고 install.json 을 지운다(다음 실행이 다시 처음 실행). 허브 폴더·로그는 남긴다.</summary>
            int PortableFirstFail(string why)
            {
                log.Line("");
                log.Line(L.T("== 처음 실행 실패 정리: " + why, "== first-run failure cleanup: " + why));
                try { AcquireUpdate(); } catch (Exception e) { log.Line(L.T("  update mutex 재획득 실패(계속): " + e.Message, "  failed to re-acquire update mutex (continuing): " + e.Message)); }
                StopHub("portable-first-fail");
                try { if (SystemAccess.FileExists(InstJson)) { SystemAccess.DeleteFile(InstJson); log.Line(L.T("  install.json 삭제", "  deleted install.json")); } }
                catch (TestIsolationViolation) { throw; }
                catch (Exception e) { log.Line(L.T("  install.json 삭제 실패: " + e.Message, "  failed to delete install.json: " + e.Message)); }
                ReleaseUpdate();
                log.Line(L.T("# 시작 실패(exit 1) — 로그 " + log.Path, "# start failed (exit 1) — log " + log.Path));
                ui.Finish(false, FailText(why, L.T("UART 허브를 시작하지 못했습니다. 로그 파일: " + (log.Path ?? "(없음)") + "\n이 파일을 담당자에게 보내 주세요.", "Could not start UART Hub. Log file: " + (log.Path ?? "(none)") + "\nPlease send this file to the person in charge.")), 0);
                return RcFailed;
            }

            /// <summary>
            /// 두 번째 실행부터: 다른 exe 의 감독이 있으면 확인 창 [예] → update mutex → shutdown(C-5)·core.stop·검증 PID kill. install.json exe·version 을 이 exe 로
            /// (바뀌었으면 옛 exe 를 가리키는 아이콘 대상도) → sup → 건강 확인 → 바뀌었으면 skills 갱신. 교체가 아니면 창을 띄우지 않는다(실패만 오류 창).
            /// </summary>
            int PortableStart(bool replace, string runExe, string runVer)
            {
                info = existing;
                installId = existing.InstallId ?? Guid.NewGuid().ToString("N");
                if (replace)
                {
                    Step(L.T("2. 확인 창: 기존 허브를 끄고 새 exe 로 바꿀까요?", "2. Confirm window: stop the existing hub and switch to the new exe?"));
                    var lines = new List<string>
                    {
                        L.T("실행 중인 허브: " + (runExe ?? "(exe 경로 모름)") + " v" + (runVer ?? "?"), "Running hub: " + (runExe ?? "(exe path unknown)") + " v" + (runVer ?? "?")),
                        L.T("새 exe: " + self + " v" + Hub.Version, "New exe: " + self + " v" + Hub.Version),
                        L.T("기존 허브(감독·코어)를 끄고 이 exe 로 다시 켭니다 — 잠깐(수 초) 멈추며 열린 터미널 창은 연결이 끊깁니다", "The existing hub (supervisor and core) is stopped and started again from this exe — it pauses for a few seconds and open terminal windows lose their connection"),
                        L.T("포트 배정·설정(config.json)·제외한 케이블·토큰·로그는 그대로 둡니다", "Port assignments, settings (config.json), excluded cables, token and logs are kept as they are"),
                    };
                    foreach (var l in lines) log.Line("  | " + l);
                    if (!ui.Confirm(ReplaceText(runExe, runVer))) { log.Line(L.T("  취소 — 기존 허브 그대로(exit 2)", "  cancelled — existing hub unchanged (exit 2)")); return RcCancelled; }
                    ui.Steps(InstallCopy.ReplaceHead, InstallCopy.StepsReplace);
                    ui.Stage(0);
                }
                CheckConfig();
                try
                {
                    Step(L.T("3. " + (replace ? "기존 허브 끄기(shutdown) → " : "") + "install.json·아이콘", "3. " + (replace ? "stop the existing hub (shutdown) → " : "") + "install.json and icons"));
                    AcquireUpdate();                                  // 공백 동안 C-9·C-11 이 옛 감독을 다시 띄우지 않게
                    if (replace) StopHub("portable");
                    bool exeChanged = !SystemAccess.SamePath(existing.Exe, self), verChanged = existing.Version != Hub.Version;
                    if (exeChanged && !string.IsNullOrEmpty(existing.Exe)) RetargetIcons(existing.Exe, self);
                    if (exeChanged || verChanged) Act(L.T("install.json exe=" + self + " version=" + Hub.Version + " (이전 exe=" + existing.Exe + " v" + existing.Version + ")", "install.json exe=" + self + " version=" + Hub.Version + " (previous exe=" + existing.Exe + " v" + existing.Version + ")"), () => WriteInstallJson("installed"));
                    else log.Line(L.T("  install.json 그대로(exe·version 같음)", "  install.json unchanged (same exe and version)"));
                    EnsureHubFiles();
                    log.Attach(Path.Combine(hubDir, "install.log"));
                    ReleaseUpdate();
                    if (replace) ui.Stage(1);
                    Step(L.T("4. 감독 기동 → 건강 확인", "4. Start the supervisor → health check"));
                    Act(L.T("\"" + self + "\" sup 분리 기동(이 exe 그대로)", "start \"" + self + "\" sup detached (this exe as is)"), StartSup);
                    if (!(replace ? Health(Hub.Version, HealthPassUpdateS, true) : Health()))
                    {
                        log.Line(L.T("# 시작 실패(exit 1) — 로그 " + log.Path, "# start failed (exit 1) — log " + log.Path));
                        ui.Finish(false, FailText(L.T("건강 확인 실패", "health check failed"), L.T("UART 허브를 시작하지 못했습니다(건강 확인 실패). 로그 파일: " + log.Path, "Could not start UART Hub (health check failed). Log file: " + log.Path)), 0);
                        return RcFailed;
                    }
                    if (replace) ui.Stage(2);
                    Step("5. Claude skills");
                    if (exeChanged || verChanged) DeploySkills(SkillsDeployer.Recorded(InstJson));
                    else log.Line(L.T("  skills 그대로(exe·version 같음)", "  skills unchanged (same exe and version)"));
                }
                catch (TestIsolationViolation) { throw; }
                catch (Exception e)
                {
                    ReleaseUpdate();
                    log.Line(L.T("  오류: " + e.GetType().Name + ": " + e.Message, "  error: " + e.GetType().Name + ": " + e.Message));
                    ui.Finish(false, FailText(e.GetType().Name + ": " + e.Message, L.T("UART 허브를 시작하지 못했습니다: " + e.Message + "\n로그 파일: " + (log.Path ?? "(없음)"), "Could not start UART Hub: " + e.Message + "\nLog file: " + (log.Path ?? "(none)"))), 0);
                    return RcFailed;
                }
                log.Line("");
                log.Line(L.T("# 시작 완료(휴대용) v" + Hub.Version + (replace ? " — 기존 허브(" + (runExe ?? "?") + ")를 이 exe 로 바꿈" : ""), "# start done (portable) v" + Hub.Version + (replace ? " — replaced the existing hub (" + (runExe ?? "?") + ") with this exe" : "")));
                if (replace) ui.Finish(true, InstallCopy.Replaced(Hub.Version, self, skillsSummary), 5000);
                return RcOk;
            }

            UiText ReplaceText(string runExe, string runVer) => InstallCopy.Replace(runExe, runVer, Hub.Version, self);

            /// <summary>휴대용 제거: 확인 창 → update mutex → 허브 정지 → 자기 아이콘·icons.json → skills(표시가 맞는 것) → install.json. exe·로그·폴더는 남긴다(자기 이미지 이동 없음).</summary>
            int PortableUninstall()
            {
                instExe = existing.Exe ?? self;
                hubDir = existing.HubDir; logRoot = existing.LogRoot; installId = existing.InstallId;
                log.Line("  install.json: v" + existing.Version + " portable=true exe=" + instExe + " install_id=" + installId);
                log.Line(L.T("  허브 폴더: " + (hubDir ?? "(모름)") + " · 로그 위치: " + (logRoot ?? "(모름)"), "  hub folder: " + (hubDir ?? "(unknown)") + " · log location: " + (logRoot ?? "(unknown)")));
                Step(L.T("2. 확인 창(1회)", "2. Confirm window (once)"));
                var lines = new List<string>
                {
                    L.T("지울 것: 바탕화면 허브 아이콘(자기 것만), 넣은 Claude skills(표시가 맞을 때만), 설정 파일 " + InstJson, "To delete: desktop hub icons (our own only), Claude skills we placed (only if the marker matches), settings file " + InstJson),
                    L.T("남길 것: exe " + instExe + ", 로그·설정 " + (logRoot ?? "(모름)") + " (허브 폴더 " + (hubDir ?? "(모름)") + ")", "To keep: exe " + instExe + ", logs and settings " + (logRoot ?? "(unknown)") + " (hub folder " + (hubDir ?? "(unknown)") + ")"),
                    L.T("허브를 끕니다(열린 터미널 연결이 끊깁니다)", "The hub is stopped (open terminals lose their connection)"),
                    L.T("자동 실행 바로 가기를 만들었다면 직접 지우세요(Win+R → shell:startup)", "If you made an auto-start shortcut, delete it yourself (Win+R → shell:startup)"),
                };
                foreach (var l in lines) log.Line("  | " + l);
                if (!dry && !ui.Confirm(InstallCopy.Uninstall(instExe, logRoot, hubDir, InstJson))) { log.Line(L.T("  취소 — 아무것도 바꾸지 않음(exit 2)", "  cancelled — nothing was changed (exit 2)")); return RcCancelled; }
                if (dry) log.Line(L.T("  [dry-run] 확인 창을 띄우지 않음", "  [dry-run] not showing the confirm window"));
                if (!dry && !string.IsNullOrEmpty(hubDir) && SystemAccess.DirExists(hubDir)) log.Attach(Path.Combine(hubDir, "uninstall_" + ts + ".log"));
                if (!dry) { ui.Steps(InstallCopy.UninstallHead, InstallCopy.StepsUninstall); ui.Stage(0); }

                // 단계마다 오류를 잡고 다음 단계를 계속한다(X7) — 하나가 실패해도 나머지는 지우고, 끝에 실패 요약과 exit 1
                var failed = new List<string>();
                void Try(string what, Action a)
                {
                    try { a(); }
                    catch (TestIsolationViolation) { throw; }
                    catch (Exception e) { failed.Add(what + "(" + e.GetType().Name + ": " + e.Message + ")"); log.Line(L.T("  오류: " + what + " — " + e.GetType().Name + ": " + e.Message + " (다음 단계는 계속)", "  error: " + what + " — " + e.GetType().Name + ": " + e.Message + " (continuing with the next step)")); }
                }
                Step(L.T("3. update mutex 획득(제거 중 C-9·C-11 이 감독을 다시 띄우지 않게)", "3. Acquire update mutex (so C-9/C-11 do not restart the supervisor during removal)"));
                Try("update mutex", () => Act(L.T("update mutex 획득(" + SystemAccess.MutexName("update") + ")", "acquire update mutex (" + SystemAccess.MutexName("update") + ")"), AcquireUpdate));
                Step(L.T("4. 허브 정지(감독 shutdown → core.stop → 검증 PID kill)", "4. Stop the hub (supervisor shutdown → core.stop → kill verified PID)"));
                if (string.IsNullOrEmpty(hubDir)) log.Line(L.T("  허브 폴더 모름 — 건너뜀", "  hub folder unknown — skipped"));
                else Try(L.T("허브 정지", "stop hub"), () => Act(L.T("감독에 shutdown(C-5, control.json stopped 는 건드리지 않음) — 응답 없으면 core.stop·검증 PID kill", "shutdown to the supervisor (C-5; control.json stopped is left alone) — if no response, core.stop and kill verified PID"), () => StopHub("uninstall")));
                if (!dry) ui.Stage(1);
                Step(L.T("5. 바탕화면 아이콘 삭제(대상 = " + instExe + " 이고 인자 open <포트> 인 것만, E1)", "5. Delete desktop icons (only those whose target = " + instExe + " with the argument open <port>, E1)"));
                Try(L.T("바탕화면 아이콘", "desktop icons"), RemoveIcons);
                string iconsJson = Path.Combine(instDir, "icons.json");
                Try("icons.json", () => { if (SystemAccess.FileExists(iconsJson)) Act(L.T("icons.json 삭제", "delete icons.json"), () => Retry(() => SystemAccess.DeleteFile(iconsJson))); });
                Step(L.T("6. skills(.uart_hub 의 install_id 가 같을 때만)", "6. skills (only when install_id in .uart_hub matches)"));
                var recorded = SkillsDeployer.Recorded(InstJson);
                Try("skills", () =>
                {
                    if (dry) log.Line(L.T("  [dry-run] 후보: Windows ", "  [dry-run] candidates: Windows ") + Path.Combine(SystemAccess.UserProfile, ".claude", "skills", "uart") + " · WSL "
                                      + (recorded.Any(x => x.Env == "wsl" && x.Placed) ? string.Join(", ", recorded.Where(x => x.Env == "wsl" && x.Placed).Select(x => x.Distro)) : L.T("(기록 없음)", "(no record)"))
                                      + L.T(" — .uart_hub 의 install_id=" + (installId ?? "(모름)") + " 일 때만 지움", " — deleted only if install_id in .uart_hub is " + (installId ?? "(unknown)")));
                    else
                    {
                        var rm = SkillsDeployer.Remove(installId, recorded, log.Line);
                        log.Line("  skills: " + SkillsDeployer.Summary(rm));
                        if (rm.Any(x => x.Result == "failed")) throw new IOException(SkillsDeployer.Summary(rm.Where(x => x.Result == "failed")));
                    }
                });
                Step(L.T("7. 옛 설치 방식 잔재(자기 것만 — 표시 있는 시작프로그램·시작 메뉴 바로가기, 설치 폴더의 UartHub*.exe)", "7. Leftovers of the old install method (our own only — marked startup and Start menu shortcuts, UartHub*.exe in the install folder)"));
                Try(L.T("옛 설치 잔재", "old install leftovers"), RemoveLegacyRemnants);
                if (!dry) ui.Stage(2);
                Step(L.T("8. install.json 삭제", "8. Delete install.json"));
                Try("install.json", () => Act(L.T("삭제 " + InstJson, "delete " + InstJson), () => Retry(() => SystemAccess.DeleteFile(InstJson))));
                Step(L.T("9. 남김", "9. Kept"));
                log.Line(L.T("  exe " + instExe + ", 로그 " + (logRoot ?? "(모름)") + ", 허브 폴더 " + (hubDir ?? "(모름)") + ", " + instDir + " 폴더(설정 파일 prefs.json 등). 자기 이미지는 옮기지 않음", "  exe " + instExe + ", logs " + (logRoot ?? "(unknown)") + ", hub folder " + (hubDir ?? "(unknown)") + ", folder " + instDir + " (settings such as prefs.json). The running image is not moved"));
                if (!dry) ReleaseUpdate();
                log.Line("");
                if (dry) { log.Line(L.T("# dry-run 끝 — 시스템을 바꾸지 않았습니다.", "# dry-run done — the system was not changed.")); return RcOk; }
                if (failed.Count > 0)
                {
                    log.Line(L.T("# 제거 일부 실패(exit 1): " + string.Join(" · ", failed), "# removal partly failed (exit 1): " + string.Join(" · ", failed)));
                    ui.Finish(false, InstallCopy.UninstallFail(failed, log.Path), 0);
                    return RcFailed;
                }
                log.Line(L.T("# 제거 완료(휴대용)", "# removal done (portable)"));
                ui.Finish(true, InstallCopy.Uninstalled(instExe, logRoot ?? hubDir), 5000);
                return RcOk;
            }

            // ============================================================== setup(X7, 부록 D.3 — 창 없는 휴대용 등록)
            string zoneId, runExeS, runVerS, location, downgradeFrom;
            int runPidS;
            bool sFirst, sSupUp, sReplace, sSameRunning, sExeChanged, sVerChanged, sNeedSkills, sStarted, sStopped;
            bool? sHealthOk, hubRunningAfter;
            double sHealthS;
            string onDemandAdd, layoutAdd;
            DateTime setupDeadline;
            readonly List<string> legacy = new List<string>(), legacyHints = new List<string>(), actions = new List<string>();
            readonly List<(string Path, string Target)> autostart = new List<(string, string)>();
            List<SkillsDeployer.Place> skillPlan, skillsPlaced;

            string SetupLast => Path.Combine(instDir, SetupLastName);

            /// <summary>X7: 17자리 빌드 시각 버전 a 가 b 보다 오래됐나(형식이 다르면 판단하지 않음 = false).</summary>
            static bool Older(string a, string b)
                => a != null && b != null && a.Length == b.Length && a.All(char.IsDigit) && b.All(char.IsDigit) && string.CompareOrdinal(a, b) < 0;

            /// <summary>
            /// setup: 결과 파일에 "running" 을 먼저 남김 → 1 상태(읽기만) → 2 할 일 → (--yes 일 때만) 3~ 실행 → 결과(표준 출력 + setup_last.json). 창은 띄우지 않는다.
            /// --yes 는 installer mutex 를 끝까지 쥔다(C-8 — 띄운 감독의 C-14 는 이 프로세스가 끝난 뒤 install.json 을 판정). 예상 못 한 예외도 결과 failed 로 남긴다.
            /// </summary>
            int Setup()
            {
                instExe = self;
                setupDeadline = DateTime.UtcNow.AddSeconds(SystemAccess.TestNumber("UART_HUB_TEST_SETUP_BUDGET_S", 0, SetupBudgetS));
                log.Line(L.T("# UART 허브 setup v" + Hub.Version + (dry ? " — 계획만(아무것도 바꾸지 않음 — 실행은 setup --yes)" : " --yes" + (NoStart ? " --no-start" : "") + (AllowDowngrade ? " --allow-downgrade" : "")), "# UART Hub setup v" + Hub.Version + (dry ? " — plan only (nothing is changed — run setup --yes to apply)" : " --yes" + (NoStart ? " --no-start" : "") + (AllowDowngrade ? " --allow-downgrade" : ""))));
                log.Line(L.T("# exe=" + self + " pid=" + Environment.ProcessId + " 사용자=" + Environment.UserName, "# exe=" + self + " pid=" + Environment.ProcessId + " user=" + Environment.UserName));
                if (SystemAccess.IsTest) log.Line(L.T("# 시험 모드(C-13) root=" + SystemAccess.TestRoot, "# test mode (C-13) root=" + SystemAccess.TestRoot));
                log.Line(L.T("# 설정 파일=" + InstJson + " · 결과 파일=" + SetupLast + " (실행 파일·바로가기는 두지 않음)", "# settings file=" + InstJson + " · result file=" + SetupLast + " (no executable or shortcuts are placed)"));
                WriteLast(new JObj().Set("command", "setup").Set("mode", dry ? "plan" : "apply").Set("result", "running").Null("rc")
                                    .Set("message", L.T("setup 실행 중(또는 중간에 끊김)", "setup is running (or was interrupted)")).Set("version", Hub.Version).Set("exe", self).Set("at", HubFiles.Iso(DateTime.Now)).ToString());
                Mutex inst = null;
                try
                {
                    string mname = SystemAccess.MutexName("installer");
                    if (dry ? CoreMain.MutexExists(mname) : !TryOwn(mname, out inst))
                        return SetupEnd(RcBlocked, "blocked", L.T("다른 설치기·제거기(UART 허브 창)가 실행 중(" + mname + ") — 그 창을 닫은 뒤 다시 실행하세요", "another installer/uninstaller (UART Hub window) is running (" + mname + ") — close that window, then run again"));
                    try { return SetupRun(); }
                    catch (TestIsolationViolation) { throw; }
                    catch (Exception e)
                    {
                        log.Line(L.T("  내부 오류: " + e.GetType().Name + ": " + e.Message, "  internal error: " + e.GetType().Name + ": " + e.Message));
                        return SetupEnd(RcFailed, "failed", L.T("내부 오류(" + e.GetType().Name + ": " + e.Message + ") — 로그 " + (log.Path ?? "(없음)"), "internal error (" + e.GetType().Name + ": " + e.Message + ") — log " + (log.Path ?? "(none)")));
                    }
                }
                finally
                {
                    if (inst != null) { try { inst.ReleaseMutex(); } catch (Exception) { } inst.Dispose(); }
                }
            }

            static bool TryOwn(string name, out Mutex m)
            {
                m = new Mutex(false, name);
                bool own;
                try { own = m.WaitOne(0); } catch (AbandonedMutexException) { own = true; }
                if (!own) { m.Dispose(); m = null; }
                return own;
            }

            int SetupRun()
            {
                Step(L.T("1. 상태(읽기만)", "1. State (read only)"));
                existing = SystemAccess.FileExists(InstJson) ? InstallInfo.Load(InstJson) : null; LoadLang(existing?.HubDir);
                string selfForCheck = SystemAccess.IsTest && SystemAccess.FileExists(SystemAccess.TestFile("fake_self_path.txt"))
                                      ? SystemAccess.ReadText(SystemAccess.TestFile("fake_self_path.txt")).Trim() : self;     // 시험(C-13): 위치 검사만 이 경로로
                location = SystemAccess.ExeLocationProblem(selfForCheck);
                zoneId = SystemAccess.ZoneId(self);
                log.Line(L.T("  exe: " + self + " v" + Hub.Version + (location == null ? " (로컬 고정 디스크)" : " — 위치 문제: " + location), "  exe: " + self + " v" + Hub.Version + (location == null ? " (local fixed disk)" : " — location problem: " + location)));
                log.Line("  MotW(Zone.Identifier): " + (zoneId == null ? L.T("없음", "none") : zoneId == "unknown" ? L.T("확인 못 함(대체 데이터 스트림이 없는 파일 시스템 등)", "could not check (e.g. a file system without alternate data streams)")
                         : L.T("있음(ZoneId=" + zoneId + (zoneId == "3" ? " — 인터넷에서 받은 파일" : "") + ") — 지우지 않음(사용자가 exe 속성 → '차단 해제')", "present (ZoneId=" + zoneId + (zoneId == "3" ? " — downloaded from the internet" : "") + ") — not removed (the user can use exe Properties → 'Unblock')")));
                Legacy();
                log.Line(L.T("  옛 설치 방식 잔재: " + (legacy.Count == 0 ? "없음" : string.Join(" · ", legacy)), "  leftovers of the old install method: " + (legacy.Count == 0 ? "none" : string.Join(" · ", legacy))));
                sFirst = existing == null || !existing.Portable || existing.State != "installed" || string.IsNullOrEmpty(existing.HubDir);
                log.Line(L.T("  등록(install.json): ", "  registration (install.json): ") + (existing == null ? L.T("없음(처음 등록)", "none (first registration)")
                         : "v" + existing.Version + " exe=" + existing.Exe + " portable=" + (existing.Portable ? "true" : "false") + " state=" + (existing.State ?? "?")
                           + (sFirst ? L.T(" → 처음 등록으로 다시 씀", " → rewritten as a first registration") : SystemAccess.SamePath(existing.Exe, self) ? L.T(" (이 exe)", " (this exe)") : L.T(" (다른 exe)", " (other exe)"))));
                if (sFirst && existing != null && existing.Portable) existing = null;           // 끝나지 않은 휴대용 기록은 처음 등록으로(옛 설치 방식 기록은 허브 폴더 표시에만 씀)
                DecideLogRoot();
                sStopped = HubFiles.IsStopped(hubDir);
                log.Line(L.T("  로그 위치: " + logRoot + " (" + logRootWhy + ")", "  log location: " + logRoot + " (" + logRootWhy + ")"));
                log.Line(L.T("  허브 폴더: " + hubDir + (sStopped ? " — control.json stopped(트레이 '정지' 상태: 허브가 COM 을 잡지 않음)" : ""), "  hub folder: " + hubDir + (sStopped ? " — control.json stopped (tray 'Stop' state: the hub does not hold any COM port)" : "")));
                sSupUp = CoreMain.MutexExists(SystemAccess.MutexName("sup"));
                if (sSupUp)
                {
                    RunningSup(out runExeS, out runVerS);
                    var sr = Rec(HubFiles.SupState(hubDir));
                    runPidS = HubFiles.IsAlive(sr.Pid, sr.St) ? sr.Pid : 0;
                }
                bool unidentified = sSupUp && (runPidS == 0 || string.IsNullOrEmpty(runExeS));
                log.Line(L.T("  실행 중 허브: ", "  running hub: ") + (!sSupUp ? L.T("없음", "none") : unidentified ? L.T("감독 mutex 는 있는데 sup.json 으로 확인되지 않음(누구인지 모름)", "the supervisor mutex exists but sup.json does not identify it (owner unknown)")
                         : "pid " + runPidS + " v" + (runVerS ?? "?") + " exe=" + runExeS + (SystemAccess.SamePath(runExeS, self) ? L.T(" (이 exe)", " (this exe)") : L.T(" (다른 exe)", " (other exe)"))));
                string newest = new[] { sFirst ? null : existing?.Version, sSupUp ? runVerS : null }.Where(v => Older(Hub.Version, v)).OrderByDescending(v => v, StringComparer.Ordinal).FirstOrDefault();
                downgradeFrom = newest;
                if (newest != null) log.Line(L.T("  되돌리기: 이 exe v" + Hub.Version + " 가 " + (sSupUp && runVerS == newest ? "실행 중" : "등록된") + " 판 v" + newest + " 보다 오래됨" + (AllowDowngrade ? " — --allow-downgrade 로 허용" : ""), "  downgrade: this exe v" + Hub.Version + " is older than the " + (sSupUp && runVerS == newest ? "running" : "registered") + " version v" + newest + (AllowDowngrade ? " — allowed by --allow-downgrade" : "")));
                CheckConfig();
                if (cfgExists)
                {
                    onDemandAdd = Config.WithOnDemand(SystemAccess.ReadText(CfgPath), out string odWhy);
                    log.Line("  config.json on_demand: " + (onDemandAdd != null ? L.T("없음 → \"on_demand\": true 를 더함", "missing → adding \"on_demand\": true") : odWhy));
                    layoutAdd = Config.WithLogLayout(SystemAccess.ReadText(CfgPath), out string lyWhy);
                    log.Line("  config.json log_layout: " + (layoutAdd != null ? L.T("\"log_layout\": " + Config.CurrentLogLayout + " 로 맞춤(없으면 더하고 2 면 바꿈)", "set to \"log_layout\": " + Config.CurrentLogLayout + " (added if missing, changed if 2)") : lyWhy));
                }
                CheckPorts(true);
                var task = SystemAccess.Schtasks(new[] { "/query", "/tn", "\\uart_hub", "/xml" }, 10000);
                if (task.Code == 0) warnings.Add(L.T("PowerShell 판이 등록한 작업 스케줄러 작업 \\uart_hub 가 남아 있음 — 로그온 때 PS 판 허브를 띄워 COM 을 다툴 수 있음(사용자가 작업 스케줄러에서 지우기 — setup 은 건드리지 않음)", "the Task Scheduler task \\uart_hub registered by the PowerShell version is still there — it can start the PS-version hub at logon and compete for COM ports (the user should delete it in Task Scheduler — setup does not touch it)"));
                string envDir = Environment.GetEnvironmentVariable("UART_HUB_DIR");
                if (!string.IsNullOrEmpty(envDir))
                    warnings.Add(L.T("환경변수 UART_HUB_DIR=" + envDir + " 가 설정됨 — 그 셸의 uart·허브 명령은 등록된 허브 폴더 대신 그 폴더를 씀(사용자가 지우기 — setup 이 띄우는 감독은 물려받지 않음)", "environment variable UART_HUB_DIR=" + envDir + " is set — uart and hub commands in that shell use that folder instead of the registered hub folder (the user should unset it — the supervisor setup starts does not inherit it)"));
                foreach (var w in warnings) log.Line(L.T("  경고: " + w, "  warning: " + w));
                foreach (var f in SystemAccess.ListFiles(SystemAccess.Startup, "*.lnk"))
                {
                    var i = ShellLink.Read(f);
                    if (i?.Target != null && Path.GetFileName(i.Target).StartsWith("UartHub", StringComparison.OrdinalIgnoreCase)) autostart.Add((f, i.Target));
                }
                log.Line(L.T("  자동 실행(shell:startup): ", "  auto start (shell:startup): ") + (autostart.Count == 0 ? L.T("없음(사용자가 직접 만듦)", "none (the user creates it)")
                         : string.Join(" · ", autostart.Select(a => a.Path + " → " + a.Target + (SystemAccess.SamePath(a.Target, self) ? L.T(" (이 exe)", " (this exe)") : L.T(" (다른 exe)", " (other exe)"))))));
                CheckSkillsTargets();
                // 계획만: 켜진 배포판만 탐침(꺼진 것은 unknown — 깨우지 않음). --yes: 모두 탐침하고 그 결과를 배치가 다시 쓴다. 둘 다 setup 시간 예산 안에서만
                skillPlan = SkillsDeployer.Plan(sFirst ? null : existing.InstallId, Hub.Version, self, wsl, log.Line, setupDeadline, dry ? SkillsDeployer.ListDistros(true) : null);

                string blocked = legacy.Count > 0
                        ? L.T("옛 설치 방식(설치기)의 잔재가 있습니다: " + string.Join(" · ", legacy) + " — 먼저 제거해야 합니다(이 exe 로 uninstall --yes)", "leftovers of the old install method (installer) exist: " + string.Join(" · ", legacy) + " — remove them first (uninstall --yes with this exe)")
                    : location != null ? L.T("이 exe 가 " + location + " 에 있습니다 — 휴대용 허브는 이 exe 를 계속 그 자리에서 실행하므로 로컬 고정 디스크(예: C:\\Tools\\)에 두고 그 경로로 다시 실행하세요", "this exe is on " + location + " — the portable hub keeps running this exe from where it is, so put it on a local fixed disk (e.g. C:\\Tools\\) and run it from there")
                    : CoreMain.MutexExists(SystemAccess.MutexName("update")) ? L.T("다른 설치기·업데이트가 교체 중(" + SystemAccess.MutexName("update") + ") — 끝난 뒤 다시 실행하세요", "another installer/update is swapping files (" + SystemAccess.MutexName("update") + ") — run again after it finishes")
                    : unidentified ? L.T("UART 허브 감독이 실행 중인데 누구인지 확인할 수 없습니다(sup.json 없음·오래됨, " + hubDir + ") — 트레이 아이콘 → 종료(허브 끄기) 뒤 다시 실행하세요", "a UART Hub supervisor is running but cannot be identified (sup.json missing or stale, " + hubDir + ") — use tray icon → Quit hub, then run again")
                    : sFirst && sSupUp ? L.T("등록되지 않은 UART 허브 감독이 이미 실행 중(exe=" + runExeS + ") — 트레이 → 종료(허브 끄기) 뒤 다시 실행하세요", "an unregistered UART Hub supervisor is already running (exe=" + runExeS + ") — use tray → Quit hub, then run again")
                    : downgradeFrom != null && !AllowDowngrade ? L.T("되돌리기: 이 exe v" + Hub.Version + " 가 지금 판 v" + downgradeFrom + " 보다 오래됐습니다 — 의도한 되돌리기면 --allow-downgrade 를 붙여 다시 실행하세요", "downgrade: this exe v" + Hub.Version + " is older than the current version v" + downgradeFrom + " — if the downgrade is intended, run again with --allow-downgrade")
                    : null;
                sReplace = !sFirst && sSupUp && (!SystemAccess.SamePath(runExeS, self) || runVerS != Hub.Version);
                sSameRunning = !sFirst && sSupUp && !sReplace;
                sExeChanged = sFirst || !SystemAccess.SamePath(existing.Exe, self);
                sVerChanged = sFirst || existing.Version != Hub.Version;
                sNeedSkills = sFirst || sExeChanged || sVerChanged || skillPlan.Any(p => p.Result == "install" || p.Result == "update");
                bool start = !NoStart && !sSameRunning;

                Step(L.T("2. 할 일", "2. To do"));
                if (blocked == null)
                {
                    if (sReplace) actions.Add(L.T("실행 중 허브(pid " + runPidS + " v" + (runVerS ?? "?") + " " + runExeS + ") 끄기 — 감독 shutdown(C-5), 포트·설정·로그 그대로"
                                              + (start ? " → 이 exe 로 다시 켬(실패하면 이전 허브로 되돌림)" : "(--no-start: 다시 켜지 않음)"), "Stop the running hub (pid " + runPidS + " v" + (runVerS ?? "?") + " " + runExeS + ") — supervisor shutdown (C-5), ports, settings and logs unchanged"
                                              + (start ? " → start it again from this exe (roll back to the previous hub on failure)" : " (--no-start: not started again)")));
                    if (!SystemAccess.DirExists(hubDir)) actions.Add(L.T("허브 폴더 만들기 " + hubDir, "create hub folder " + hubDir));
                    var rc = ResolvedConfig();
                    bool layout2 = !cfgExists || layoutAdd != null || rc.LogLayout >= 2;
                    bool layout3 = !cfgExists || layoutAdd != null || rc.LogLayout >= 3;
                    string live = layout3 ? Path.Combine(Config.SpoolDirFor(hubDir), "live") : layout2 ? Path.Combine(rc.BaseRoot, Config.ConsoleDirName, "live") : Path.Combine(rc.LogRoot, "live");
                    if (!TokenFile.IsValid(TokenPath)) actions.Add(L.T("토큰 만들기 " + TokenPath + "(32자 hex, 현재 사용자만 읽음)", "create token " + TokenPath + " (32 hex chars, readable by the current user only)"));
                    if (!cfgExists) actions.Add(L.T("config.json 새로 만들기(" + cfgNote + ")", "create config.json (" + cfgNote + ")"));
                    else if (onDemandAdd != null) actions.Add(L.T("config.json 에 \"on_demand\": true 더하기(필요할 때만 연결 — 다른 키·주석 그대로)", "add \"on_demand\": true to config.json (connect only when needed — other keys and comments untouched)"));
                    if (cfgExists && layoutAdd != null) actions.Add(L.T("config.json 에 \"log_layout\": " + Config.CurrentLogLayout + " 보장(에이징 세션 — 콘솔 기록은 숨김 " + Config.SpoolDirName + "에 24시간, 세션 폴더 " + Config.AgingDirName + " — 다른 키·주석 그대로)", "ensure \"log_layout\": " + Config.CurrentLogLayout + " in config.json (aging runs — console records go to the hidden " + Config.SpoolDirName + " for 24 hours, session folder " + Config.AgingDirName + " — other keys and comments untouched)"));
                    if (layout2 && LogMigration.Summary(LogMigration.Plan(rc.BaseRoot, hubDir)) is string mig) actions.Add(mig + (sSameRunning ? L.T(" — 허브가 실행 중이라 이번에는 하지 않음(끄고 다시 시작할 때)", " — not done this time because the hub is running (done when it is stopped and started again)") : ""));
                    if (!SystemAccess.DirExists(live) && !SystemAccess.DirExists(Path.Combine(rc.BaseRoot, "live"))) actions.Add(L.T("폴더 만들기 " + live, "create folder " + live));
                    if (sFirst) actions.Add(L.T("install.json 쓰기(portable:true, exe=" + self + ", state=installed)", "write install.json (portable:true, exe=" + self + ", state=installed)"));
                    else if (sExeChanged || sVerChanged) actions.Add(L.T("install.json exe·version 바꾸기(이전 " + existing.Exe + " v" + existing.Version + " → 이 exe v" + Hub.Version + ")", "change install.json exe/version (previous " + existing.Exe + " v" + existing.Version + " → this exe v" + Hub.Version + ")"));
                    if (!sFirst && sExeChanged) actions.Add(L.T("옛 exe 를 가리키는 바탕화면 허브 아이콘의 대상을 이 exe 로(있으면)", "retarget desktop hub icons that point to the old exe to this exe (if any)"));
                    if (start) actions.Add(L.T("감독 기동(이 exe 그대로) → 제어 포트 응답 확인(최대 " + SetupHealthS + "초)", "start the supervisor (this exe as is) → check the control port responds (up to " + SetupHealthS + " s)"));
                    if (sNeedSkills) actions.Add(L.T("Claude skills(uart·connect-uart·disconnect-uart) 넣기·갱신: " + SkillsDeployer.Summary(skillPlan), "add/update Claude skills (uart, connect-uart, disconnect-uart): " + SkillsDeployer.Summary(skillPlan)));
                    foreach (var a in actions) log.Line("  - " + a);
                    if (actions.Count == 0) log.Line(L.T("  없음 — 이미 이 exe(v" + Hub.Version + ")로 등록되어 " + (sSupUp ? "실행 중" : "있음(--no-start: 띄우지 않음)"), "  none — already registered with this exe (v" + Hub.Version + ") and " + (sSupUp ? "running" : "present (--no-start: not started)")));
                }
                else log.Line(L.T("  중단: " + blocked, "  blocked: " + blocked));

                if (blocked != null) return SetupEnd(RcBlocked, "blocked", blocked);
                if (dry) return SetupEnd(RcOk, "planned", actions.Count == 0 ? L.T("할 일 없음 — 이미 이 exe 로 등록되어 있습니다(아무것도 바꾸지 않았습니다)", "nothing to do — already registered with this exe (nothing was changed)")
                                                                            : L.T("아무것도 바꾸지 않았습니다. 이대로 하려면 setup --yes" + (downgradeFrom != null ? " --allow-downgrade" : ""), "Nothing was changed. To apply this, run setup --yes" + (downgradeFrom != null ? " --allow-downgrade" : "")));
                if (actions.Count == 0)
                    return SetupEnd(RcOk, "unchanged", L.T("바꾼 것 없음 — 이미 이 exe(v" + Hub.Version + ")로 등록되어 " + (sSupUp ? "실행 중입니다" : "있습니다(--no-start: 띄우지 않음)"), "nothing changed — already registered with this exe (v" + Hub.Version + ") and " + (sSupUp ? "running" : "present (--no-start: not started)")));
                return sFirst ? SetupFirst(start) : SetupUpdate(start);
            }

            /// <summary>옛 설치 방식(설치기 install)이 만든 자기 바로가기인가: 설명의 [uart_hub:…] 표시나 대상 = 설치본(%LOCALAPPDATA%\uart_hub\UartHub.exe).</summary>
            static bool IsLegacyLnk(string p)
            {
                var i = ShellLink.Read(p);
                return i != null && ((i.Description ?? "").Contains(ShellLink.MarkerPrefix) || SystemAccess.SamePath(i.Target, SystemAccess.InstalledExe));
            }

            /// <summary>
            /// 옛 설치 방식(설치기 install) 잔재: portable 아닌 install.json · 설치 폴더의 UartHub*.exe · 시작프로그램 'UART 허브.lnk' ·
            /// 시작 메뉴 'UART 허브\{UART 허브, UART 허브 제거}.lnk'(자기 것만). 잔재마다 다음 할 일(legacyHints)도 만든다.
            /// </summary>
            void Legacy()
            {
                if (existing != null && !existing.Portable)
                {
                    legacy.Add(L.T("install.json(portable 아님, state=" + (existing.State ?? "?") + ") " + InstJson, "install.json (not portable, state=" + (existing.State ?? "?") + ") " + InstJson));
                    legacyHints.Add(L.T("예전 설치기 등록(install.json): 이 exe 의 uninstall --yes 가 설치 폴더·시작프로그램·시작 메뉴 바로가기·skills 를 지움(로그·설정은 남김)", "registration by the old installer (install.json): uninstall --yes of this exe removes the install folder, startup and Start menu shortcuts, and skills (logs and settings are kept)"));
                }
                foreach (var f in SystemAccess.ListFiles(instDir, "UartHub*.exe"))
                    if (!SystemAccess.SamePath(f, self))
                    {
                        legacy.Add(L.T("설치본 " + f, "installed copy " + f));
                        legacyHints.Add(L.T("설치 폴더의 exe(" + Path.GetFileName(f) + "): uninstall --yes 가 지움 — 실행 중이면 지워지지 않으니 그 허브를 트레이 → 종료(허브 끄기) 한 뒤", "exe in the install folder (" + Path.GetFileName(f) + "): removed by uninstall --yes — it cannot be deleted while running, so first use tray → Quit hub on that hub"));
                    }
                foreach (var p in new[] { StartupLnkPath, Path.Combine(MenuDir, MenuMain), Path.Combine(MenuDir, MenuUninstall) })
                    if (SystemAccess.FileExists(p) && IsLegacyLnk(p))
                    {
                        legacy.Add(L.T("바로가기 " + p, "shortcut " + p));
                        legacyHints.Add(L.T("예전 설치기가 만든 바로가기(" + Path.GetFileName(p) + "): uninstall --yes 가 지움", "shortcut made by the old installer (" + Path.GetFileName(p) + "): removed by uninstall --yes"));
                    }
            }

            /// <summary>휴대용 제거가 함께 치우는 옛 설치 방식 잔재(자기 것만, X7 — setup 이 잔재로 막힌 뒤 uninstall --yes 로 풀리게): Legacy() 와 같은 기준.</summary>
            void RemoveLegacyRemnants()
            {
                int n = 0;
                foreach (var p in new[] { StartupLnkPath, Path.Combine(MenuDir, MenuMain), Path.Combine(MenuDir, MenuUninstall) })
                    if (SystemAccess.FileExists(p) && IsLegacyLnk(p)) { n++; Act(L.T("삭제 " + p, "delete " + p), () => Retry(() => SystemAccess.DeleteFile(p))); }
                if (SystemAccess.DirExists(MenuDir) && SystemAccess.ListFiles(MenuDir, "*").Length == 0 && SystemAccess.ListDirs(MenuDir).Length == 0)
                    Act(L.T("삭제 " + MenuDir + "(빈 폴더)", "delete " + MenuDir + " (empty folder)"), () => Retry(() => SystemAccess.DeleteDir(MenuDir)));
                var locked = new List<string>();
                foreach (var f in SystemAccess.ListFiles(instDir, "UartHub*.exe"))
                {
                    if (SystemAccess.SamePath(f, self) || SystemAccess.SamePath(f, instExe)) continue;
                    n++;
                    Act(L.T("삭제 " + f, "delete " + f), () =>
                    {
                        try { SystemAccess.DeleteFile(f); }
                        catch (TestIsolationViolation) { throw; }
                        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { locked.Add(Path.GetFileName(f)); log.Line(L.T("    지우지 못함(실행 중일 수 있음): " + e.GetType().Name, "    could not delete (may be running): " + e.GetType().Name)); }
                    });
                }
                if (n == 0) log.Line(L.T("  없음", "  none"));
                if (locked.Count > 0) throw new IOException(L.T("실행 중이라 지우지 못한 설치본: " + string.Join(", ", locked) + " — 그 허브를 끈 뒤 다시 uninstall --yes", "installed copies that could not be deleted because they are running: " + string.Join(", ", locked) + " — stop that hub, then run uninstall --yes again"));
            }

            /// <summary>감독을 띄운 뒤 건강 확인(sup.json·core.pid + hub status 로 제어 포트 응답, version == 이 판) — 상한 SetupHealthS.</summary>
            bool SetupHealth(double pass)
            {
                var sw = Stopwatch.StartNew();
                bool ok = Health(Hub.Version, pass, true, SetupHealthS);
                sStarted = true;
                sHealthOk = ok;
                sHealthS = sw.Elapsed.TotalSeconds;
                return ok;
            }

            /// <summary>처음 등록(휴대용 첫 실행 PortableFirst 와 같은 일, 확인·완료 창 없음): 허브 폴더·토큰·config → install.json(portable) → sup → 건강 확인 → skills.</summary>
            int SetupFirst(bool start)
            {
                string fail = null;
                try
                {
                    Step(L.T("3. 허브 폴더·토큰·config.json", "3. Hub folder, token, config.json"));
                    EnsureHubFiles();
                    log.Attach(Path.Combine(hubDir, "install.log"));
                    Step("4. install.json(portable)");
                    installId = Guid.NewGuid().ToString("N");
                    Act("install.json state=installed portable=true (exe=" + self + ", install_id=" + installId + ", hub_dir=" + hubDir + ")", () => WriteInstallJson("installed"));
                    Step(L.T("5. 감독 기동 → 제어 포트 응답(최대 " + SetupHealthS + "초)", "5. Start the supervisor → control port responds (up to " + SetupHealthS + " s)"));
                    if (start)
                    {
                        Act(L.T("\"" + self + "\" sup 분리 기동(이 exe 그대로)", "start \"" + self + "\" sup detached (this exe as is)"), StartSup);
                        if (!SetupHealth(HealthPassS)) fail = L.T("감독을 띄웠지만 " + SetupHealthS + "초 안에 제어 포트가 응답하지 않음", "the supervisor was started but the control port did not respond within " + SetupHealthS + " s");
                    }
                    else log.Line(L.T("  --no-start: 감독을 띄우지 않음", "  --no-start: not starting the supervisor"));
                    if (fail == null)
                    {
                        Step("6. Claude skills");
                        DeploySkills(null, skillPlan, setupDeadline);
                    }
                }
                catch (TestIsolationViolation) { throw; }
                catch (Exception e)
                {
                    log.Line(L.T("  오류: " + e.GetType().Name + ": " + e.Message, "  error: " + e.GetType().Name + ": " + e.Message));
                    fail = e.GetType().Name + ": " + e.Message;
                }
                if (fail != null)
                {
                    PortableFirstFail(fail);                                  // 띄운 허브를 내리고 install.json 을 지운다(처음 등록 전 상태 — 허브 폴더·토큰·로그는 남김)
                    hubRunningAfter = false;
                    return SetupEnd(RcFailed, "rolled_back", L.T("처음 등록 실패(" + fail + ") → 되돌림: 띄운 허브를 내리고 install.json 을 지웠습니다(허브 폴더·로그는 남김, 로그 " + (log.Path ?? "(없음)") + ")", "first registration failed (" + fail + ") → rolled back: stopped the hub that was started and deleted install.json (hub folder and logs kept, log " + (log.Path ?? "(none)") + ")"));
                }
                return SetupEnd(RcOk, "registered", L.T("등록했습니다 — 이 exe 를 지금 자리에서 씁니다(복사·시작프로그램 등록 없음)" + (start ? ", 허브 실행 중" : " — --no-start: 허브는 띄우지 않음"), "registered — this exe is used right where it is (no copy, no startup entry)" + (start ? ", hub is running" : " — --no-start: the hub was not started")));
            }

            /// <summary>
            /// 등록이 있을 때(휴대용 다음 실행 PortableStart 와 같은 길, 확인 창 없음): update mutex → (다른 exe·다른 판이 돌면) shutdown(C-5)·core.stop·검증 PID kill →
            /// 아이콘 대상·install.json exe·version → 허브 파일(on_demand 보장) → update mutex 닫기 → sup → 건강 확인 → (바뀐 곳이 있으면) skills.
            /// 끈 뒤·바꾼 뒤 실패하면 SetupRollback(이전 exe·판으로 install.json·아이콘을 되돌리고, 돌던 허브였으면 그 exe 로 다시 켬).
            /// </summary>
            int SetupUpdate(bool start)
            {
                info = existing;
                installId = existing.InstallId ?? Guid.NewGuid().ToString("N");
                string prevExe = existing.Exe, prevVer = existing.Version;
                bool onlyStart = actions.Count == 1 && start, stopped = false, retargeted = false, wrote = false;
                string fail = null;
                try
                {
                    Step(L.T("3. " + (sReplace ? "기존 허브 끄기(shutdown) → " : "") + "install.json·아이콘·허브 파일", "3. " + (sReplace ? "stop the existing hub (shutdown) → " : "") + "install.json, icons and hub files"));
                    AcquireUpdate();                                  // 공백 동안 C-9·C-11 이 옛 감독을 다시 띄우지 않게(휴대용 교체 경로와 같음)
                    if (sReplace) { StopHub("setup"); stopped = true; }
                    if (sExeChanged && !string.IsNullOrEmpty(prevExe)) { retargeted = true; RetargetIcons(prevExe, self); }
                    if (sExeChanged || sVerChanged) { wrote = true; Act(L.T("install.json exe=" + self + " version=" + Hub.Version + " (이전 exe=" + prevExe + " v" + prevVer + ")", "install.json exe=" + self + " version=" + Hub.Version + " (previous exe=" + prevExe + " v" + prevVer + ")"), () => WriteInstallJson("installed")); }
                    else log.Line(L.T("  install.json 그대로(exe·version 같음)", "  install.json unchanged (same exe and version)"));
                    EnsureHubFiles();
                    log.Attach(Path.Combine(hubDir, "install.log"));
                    ReleaseUpdate();
                    Step(L.T("4. 감독 기동 → 제어 포트 응답(최대 " + SetupHealthS + "초)", "4. Start the supervisor → control port responds (up to " + SetupHealthS + " s)"));
                    if (start)
                    {
                        Act(L.T("\"" + self + "\" sup 분리 기동(이 exe 그대로)", "start \"" + self + "\" sup detached (this exe as is)"), StartSup);
                        if (!SetupHealth(sReplace ? HealthPassUpdateS : HealthPassS)) fail = L.T("감독을 띄웠지만 " + SetupHealthS + "초 안에 제어 포트가 응답하지 않음", "the supervisor was started but the control port did not respond within " + SetupHealthS + " s");
                    }
                    else log.Line(L.T("  " + (NoStart ? "--no-start: 감독을 띄우지 않음" : "감독 이미 실행 중(이 exe) — 그대로"), "  " + (NoStart ? "--no-start: not starting the supervisor" : "supervisor already running (this exe) — left as is")));
                    if (fail == null)
                    {
                        Step("5. Claude skills");
                        if (sNeedSkills) DeploySkills(SkillsDeployer.Recorded(InstJson), skillPlan, setupDeadline);
                        else log.Line(L.T("  skills 그대로(모두 같은 표시)", "  skills unchanged (all markers match)"));
                    }
                }
                catch (TestIsolationViolation) { throw; }
                catch (Exception e)
                {
                    log.Line(L.T("  오류: " + e.GetType().Name + ": " + e.Message, "  error: " + e.GetType().Name + ": " + e.Message));
                    fail = e.GetType().Name + ": " + e.Message;
                }
                if (fail != null) return SetupRollback(fail, prevExe, prevVer, stopped, retargeted, wrote);
                if (sReplace)
                    return SetupEnd(RcOk, "replaced", L.T("기존 허브(" + runExeS + " v" + (runVerS ?? "?") + ")를 끄고 이 exe(v" + Hub.Version + ")로 바꿨습니다 — 포트·설정·로그는 그대로"
                                                      + (start ? "" : "(--no-start: 새 허브는 띄우지 않음)"), "Stopped the existing hub (" + runExeS + " v" + (runVerS ?? "?") + ") and switched to this exe (v" + Hub.Version + ") — ports, settings and logs unchanged"
                                                      + (start ? "" : " (--no-start: the new hub was not started)")));
                return SetupEnd(RcOk, "updated", onlyStart ? L.T("이미 이 exe 로 등록되어 있어 허브만 켰습니다", "already registered with this exe, so only the hub was started") : L.T("등록을 이 exe(v" + Hub.Version + ")에 맞췄습니다", "aligned the registration with this exe (v" + Hub.Version + ")"));
            }

            /// <summary>
            /// 등록 맞추기 실패의 되돌림(H1): update mutex → 띄운 새 허브 정지 → install.json exe·version 을 이전 값으로·아이콘 대상을 이전 exe 로 → update mutex 닫기 →
            /// 돌던 허브를 껐었으면 그 exe 로 감독을 다시 띄우고 건강 확인(그 판). 결과 rolled_back(1) — 바꾼 것이 없었으면 failed(1).
            /// </summary>
            int SetupRollback(string why, string prevExe, string prevVer, bool stopped, bool retargeted, bool wrote)
            {
                log.Line("");
                log.Line(L.T("== R. 되돌림: " + why, "== R. rollback: " + why));
                try { AcquireUpdate(); } catch (TestIsolationViolation) { throw; } catch (Exception e) { log.Line(L.T("  update mutex 재획득 실패(계속): " + e.Message, "  failed to re-acquire update mutex (continuing): " + e.Message)); }
                var undone = new List<string>();
                try
                {
                    if (started.Count > 0) { StopHub("setup-rollback"); undone.Add(L.T("띄운 새 허브 정지", "stopped the new hub that was started")); }
                    if (wrote)
                    {
                        info.Exe = prevExe; info.Version = prevVer;
                        SystemAccess.Guard(InstJson, "write");
                        if (!info.Save(InstJson)) throw new IOException(L.T("install.json 되돌리기 쓰기 실패", "failed to write the install.json rollback"));
                        log.Line(L.T("  install.json exe=" + prevExe + " version=" + prevVer + " 로 되돌림", "  install.json restored to exe=" + prevExe + " version=" + prevVer));
                        undone.Add(L.T("install.json = 이전 exe·판", "install.json = previous exe and version"));
                    }
                    if (retargeted && !string.IsNullOrEmpty(prevExe)) { RetargetIcons(self, prevExe); undone.Add(L.T("아이콘 대상 = 이전 exe", "icon targets = previous exe")); }
                }
                catch (TestIsolationViolation) { throw; }
                catch (Exception e) { log.Line(L.T("  되돌림 중 오류: " + e.GetType().Name + ": " + e.Message, "  error during rollback: " + e.GetType().Name + ": " + e.Message)); undone.Add(L.T("일부 되돌리지 못함(" + e.Message + ")", "some parts could not be rolled back (" + e.Message + ")")); }
                ReleaseUpdate();
                bool oldOk = false;
                string oldExe = stopped ? runExeS : null;
                if (oldExe != null)
                {
                    try
                    {
                        instExe = oldExe;                                          // 건강 확인의 hub status 를 그 exe 로
                        started.Add(SystemAccess.StartDetached(oldExe, new[] { "sup", "--icons-dir", SupIconsDir }, true, new[] { "UART_HUB_DIR" }));
                        log.Line(L.T("  이전 허브 감독(" + oldExe + " v" + runVerS + ") 다시 기동", "  starting the previous hub supervisor (" + oldExe + " v" + runVerS + ") again"));
                        oldOk = Health(runVerS, HealthPassUpdateS, false, SetupHealthS);
                        undone.Add(L.T("이전 허브 " + (oldOk ? "다시 켬" : "다시 켜지 못함"), "previous hub " + (oldOk ? "started again" : "could not be started again")));
                    }
                    catch (TestIsolationViolation) { throw; }
                    catch (Exception e) { log.Line(L.T("  이전 허브 기동 실패: " + e.Message, "  failed to start the previous hub: " + e.Message)); undone.Add(L.T("이전 허브 다시 켜지 못함", "previous hub could not be started again")); }
                    finally { instExe = self; }
                }
                hubRunningAfter = oldOk;
                rollbackOldExe = oldExe;
                if (!wrote && !retargeted && oldExe == null)
                    return SetupEnd(RcFailed, "failed", L.T("실패(" + why + ")" + (undone.Count > 0 ? " — " + string.Join(", ", undone) : "") + " — 로그 " + (log.Path ?? "(없음)"), "failed (" + why + ")" + (undone.Count > 0 ? " — " + string.Join(", ", undone) : "") + " — log " + (log.Path ?? "(none)")));
                return SetupEnd(RcFailed, "rolled_back", L.T("이 exe 로 바꾸지 못해(" + why + ") 이전 상태로 되돌렸습니다: " + string.Join(", ", undone) + " — 로그 " + (log.Path ?? "(없음)"), "could not switch to this exe (" + why + "), so the previous state was restored: " + string.Join(", ", undone) + " — log " + (log.Path ?? "(none)")));
            }

            string rollbackOldExe;

            static string ResultText(string r) => r switch
            {
                "planned" => L.T("계획만(바꾸지 않음)", "plan only (nothing changed)"),
                "registered" => L.T("등록함", "registered"),
                "replaced" => L.T("바꿈(기존 허브 → 이 exe)", "switched (existing hub → this exe)"),
                "updated" => L.T("맞춤", "aligned"),
                "unchanged" => L.T("바꾼 것 없음", "nothing changed"),
                "blocked" => L.T("중단", "blocked"),
                "rolled_back" => L.T("실패 → 되돌림", "failed → rolled back"),
                _ => L.T("실패", "failed"),
            };

            /// <summary>결과 파일(%LOCALAPPDATA%\uart_hub\setup_last.json)을 쓴다. 실패는 로그 경고만.</summary>
            void WriteLast(string text)
            {
                try
                {
                    SystemAccess.CreateDir(instDir);
                    SystemAccess.WriteText(SetupLast, text + "\n");
                }
                catch (TestIsolationViolation) { throw; }
                catch (Exception e) { log.Line(L.T("  경고: " + SetupLast + " 쓰기 실패(" + e.GetType().Name + ": " + e.Message + ")", "  warning: failed to write " + SetupLast + " (" + e.GetType().Name + ": " + e.Message + ")")); }
            }

            /// <summary>결과: 한국어 요약 줄(로그·표준 출력) + JSON(setup_last.json, --json 이면 표준 출력에 그 한 줄만).</summary>
            int SetupEnd(int rc, string result, string message)
            {
                bool running = hubRunningAfter ?? (sStarted ? sHealthOk == true : sSameRunning);
                bool done = rc == RcOk && result != "planned";
                var next = new List<string>();
                if (result == "blocked" && legacy.Count > 0)
                {
                    next.Add(L.T("사용자에게 알리고 허락을 받은 뒤: \"" + self + "\" uninstall --yes → 다시 setup(계획부터). 그 뒤에도 같은 잔재로 막히면 멈추고 보고", "After telling the user and getting permission: \"" + self + "\" uninstall --yes → run setup again (plan first). If it is still blocked by the same leftovers, stop and report"));
                    next.AddRange(legacyHints);
                }
                else if (result == "blocked" && downgradeFrom != null && !AllowDowngrade)
                    next.Add(L.T("사용자에게 한 번 묻기: \"v" + downgradeFrom + " → v" + Hub.Version + " 되돌리기인가요?\" — 예면 setup --yes --allow-downgrade", "Ask the user once: \"Is this a downgrade v" + downgradeFrom + " → v" + Hub.Version + "?\" — if yes, setup --yes --allow-downgrade"));
                if (result == "planned" && rc == RcOk && actions.Count > 0) next.Add(L.T("이대로 등록하려면 같은 exe 로 setup --yes" + (downgradeFrom != null ? " --allow-downgrade" : ""), "To register as planned, run setup --yes with the same exe" + (downgradeFrom != null ? " --allow-downgrade" : "")));
                if (result == "rolled_back" && rollbackOldExe != null && hubRunningAfter != true)
                    next.Add(L.T("이전 허브가 다시 켜지지 않았습니다 — 이전 exe(" + rollbackOldExe + ")를 더블클릭하면 이전 판으로 켜집니다", "The previous hub did not start again — double-click the previous exe (" + rollbackOldExe + ") to start the previous version"));
                if (result == "rolled_back" || result == "failed") next.Add(L.T("log(install.log)와 이 결과 파일을 담당자에게 보내기 — 다시 실행하지 않음", "Send the log (install.log) and this result file to the person in charge — do not run again"));
                if (done && running)
                {
                    next.Add(L.T("사용자에게: Claude Code 에 /connect-uart 를 직접 입력(인자 없이 = 꽂힌 COM 표, /connect-uart COMx = 연결). 슬래시 명령 목록에 없으면 Claude Code 세션을 새로 시작", "To the user: type /connect-uart in Claude Code yourself (no argument = table of plugged-in COM ports, /connect-uart COMx = connect). If it is not in the slash command list, start a new Claude Code session"));
                }
                if (sStopped) next.Add(L.T("허브가 정지 상태(control.json stopped)입니다 — 사용자가 트레이 아이콘 → 시작 을 눌러야 보드를 기록합니다", "The hub is stopped (control.json stopped) — the user must press tray icon → Start before the board is recorded"));
                if (zoneId != null && zoneId != "unknown")
                    next.Add(L.T("exe 에 MotW(인터넷에서 받은 표시, ZoneId=" + zoneId + ")가 있습니다 — 바탕화면 허브 아이콘·자동 실행 바로 가기를 열 때 보안 경고가 뜨면 사용자가 exe 오른쪽 버튼 → 속성 → '차단 해제'(setup 은 지우지 않음)", "The exe has MotW (marked as downloaded from the internet, ZoneId=" + zoneId + ") — if a security warning appears when opening the desktop hub icon or the auto-start shortcut, the user should right-click the exe → Properties → 'Unblock' (setup does not remove it)"));
                if (rc == RcOk)
                {
                    if (autostart.Count == 0) next.Add(L.T("로그온 때 자동 실행은 사용자가 직접: Win+R → shell:startup → exe 를 오른쪽 버튼으로 끌어 놓고 '바로 가기 만들기'", "Auto start at logon is up to the user: Win+R → shell:startup → right-drag the exe there and choose 'Create shortcuts here'"));
                    else if (autostart.Any(a => !SystemAccess.SamePath(a.Target, self)))
                        next.Add(L.T("shell:startup 의 바로 가기가 다른 exe 를 가리킵니다(" + string.Join(", ", autostart.Where(a => !SystemAccess.SamePath(a.Target, self)).Select(a => a.Target)) + ") — 사용자가 지우고 이 exe 로 다시 만들기", "A shortcut in shell:startup points to another exe (" + string.Join(", ", autostart.Where(a => !SystemAccess.SamePath(a.Target, self)).Select(a => a.Target)) + ") — the user should delete it and recreate it for this exe"));
                }

                var cur = SystemAccess.FileExists(InstJson) ? InstallInfo.Load(InstJson) : null;
                long port = -1;
                if (running)
                    try { port = Config.Load(SystemAccess.Guard(CfgPath, "read")).ControlPort; } catch (TestIsolationViolation) { throw; } catch (Exception) { }
                var o = new JObj()
                    .Set("command", "setup").Set("mode", dry ? "plan" : "apply").Set("result", result).Set("rc", (long)rc).Set("message", message)
                    .Set("version", Hub.Version).Set("exe", self).Set("location", location)
                    .Raw("motw", new JObj().Raw("present", zoneId == "unknown" ? "null" : zoneId != null ? "true" : "false").Set("zone_id", zoneId).ToString())
                    .Set("legacy", legacy)
                    .Set("install", cur == null ? null : new JObj().Set("version", cur.Version).Set("exe", cur.Exe).Set("portable", cur.Portable).Set("state", cur.State).Set("install_id", cur.InstallId))
                    .Set("running", !sSupUp ? null : new JObj().Raw("pid", runPidS > 0 ? runPidS.ToString(CultureInfo.InvariantCulture) : "null").Set("exe", runExeS).Set("version", runVerS)
                                                        .Set("same_exe", runExeS != null && SystemAccess.SamePath(runExeS, self)))
                    .Raw("downgrade", downgradeFrom == null ? "null" : new JObj().Set("from", downgradeFrom).Set("to", Hub.Version).Set("allowed", AllowDowngrade).ToString())
                    .Set("log_root", logRoot).Set("log_root_why", logRootWhy).Set("hub_dir", hubDir).Set("config", cfgNote).Set("stopped", sStopped)
                    .Set("warnings", warnings)
                    .Set("autostart", autostart.Select(a => new JObj().Set("path", a.Path).Set("target", a.Target).Set("this_exe", SystemAccess.SamePath(a.Target, self))))
                    .Set("skills", (skillsPlaced ?? skillPlan ?? new List<SkillsDeployer.Place>()).Select(p => p.ToJObj()))
                    .Set("actions", actions)
                    .Set("started", sStarted)
                    .Set("health", sHealthOk == null ? null : new JObj().Set("ok", sHealthOk == true).Set("seconds", Math.Round(sHealthS, 1)))
                    .Set("hub_running", done || result == "rolled_back" ? running : result != "failed" && sSupUp)
                    .Raw("control_port", port > 0 ? port.ToString(CultureInfo.InvariantCulture) : "null")
                    .Set("next", next)
                    .Set("log", log.Path)
                    .Set("setup_last", SetupLast)
                    .Set("at", HubFiles.Iso(DateTime.Now));
                string text = o.ToString();
                WriteLast(text);
                log.Line("");
                log.Line(L.T("# 결과: " + ResultText(result) + " (exit " + rc + ") — " + message, "# result: " + ResultText(result) + " (exit " + rc + ") — " + message));
                foreach (var n in next) log.Line(L.T("#   다음: " + n, "#   next: " + n));
                log.Line(L.T("# 결과 파일: " + SetupLast, "# result file: " + SetupLast));
                if (Json) OutLine(text);
                return rc;
            }

            // ============================================================== 제거
            int Uninstall()
            {
                Header(L.T("제거", "removal"));
                Step(L.T("1. 설치 상태", "1. Install state"));
                log.Line("  elevated=" + SystemAccess.ElevationState.ToString().ToLowerInvariant());
                existing = SystemAccess.FileExists(InstJson) ? InstallInfo.Load(InstJson) : null; LoadLang(existing?.HubDir);
                if (existing != null && existing.Portable) return PortableUninstall();
                // 휴대용 제거 뒤 남는 설정·결과 파일(prefs.json·setup_last.json)만 있는 폴더는 "설치되어 있지 않음"(X7 — uninstall --yes 두 번째는 exit 3)
                bool instLeft = SystemAccess.DirExists(instDir) && (SystemAccess.ListDirs(instDir).Length > 0
                                || SystemAccess.ListFiles(instDir, "*").Any(f => !new[] { "prefs.json", SetupLastName }.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)));
                bool any = existing != null || instLeft || SystemAccess.FileExists(StartupLnkPath) || SystemAccess.DirExists(MenuDir);
                if (!any)
                {
                    log.Line(L.T("  설치되어 있지 않음(" + instDir + " 에 install.json·설치본 없음, 바로가기 없음)", "  not installed (no install.json or installed copy in " + instDir + ", no shortcuts)"));
                    if (!dry) ui?.Finish(false, L.T("UART 허브가 설치되어 있지 않습니다.", "UART Hub is not installed."), 0);
                    return dry ? RcOk : RcBlocked;
                }
                hubDir = existing?.HubDir; logRoot = existing?.LogRoot; installId = existing?.InstallId;
                log.Line(L.T("  install.json: " + (existing != null ? "v" + existing.Version + " state=" + existing.State + " install_id=" + installId : "없음"), "  install.json: " + (existing != null ? "v" + existing.Version + " state=" + existing.State + " install_id=" + installId : "none")));
                log.Line(L.T("  허브 폴더: " + (hubDir ?? "(모름)") + " · 로그 위치: " + (logRoot ?? "(모름)"), "  hub folder: " + (hubDir ?? "(unknown)") + " · log location: " + (logRoot ?? "(unknown)")));

                Step(L.T("2. 확인 창(1회)", "2. Confirm window (once)"));
                var lines = new List<string>
                {
                    L.T("지울 것: 설치 폴더 " + instDir + ", 시작프로그램·시작 메뉴 바로가기, 바탕화면 허브 아이콘(자기 것만), 넣은 skills(표시가 맞을 때만)", "To delete: install folder " + instDir + ", startup and Start menu shortcuts, desktop hub icons (our own only), skills we placed (only if the marker matches)"),
                    L.T("남길 것: 로그·설정 " + (logRoot ?? "(모름)") + " (허브 폴더 " + (hubDir ?? "(모름)") + ")", "To keep: logs and settings " + (logRoot ?? "(unknown)") + " (hub folder " + (hubDir ?? "(unknown)") + ")"),
                    L.T("허브를 정지합니다(열린 터미널 연결이 끊깁니다). 정지 상태(stopped)는 남기지 않습니다 — 다시 설치하면 바로 기록합니다", "The hub is stopped (open terminals lose their connection). The stopped state is not kept — after a reinstall it records right away"),
                };
                foreach (var l in lines) log.Line("  | " + l);
                if (!dry && !ui.Confirm(UninstallTitle, L.T("제거", "Remove"), lines)) { log.Line(L.T("  취소 — 아무것도 바꾸지 않음(exit 2)", "  cancelled — nothing was changed (exit 2)")); return RcCancelled; }
                if (dry) log.Line(L.T("  [dry-run] 확인 창을 띄우지 않음", "  [dry-run] not showing the confirm window"));
                if (!dry && SystemAccess.DirExists(instDir)) log.Attach(InstLogPath);

                Step(L.T("3. update mutex 획득(제거 중 C-9·C-11 이 감독을 다시 띄우지 않게)", "3. Acquire update mutex (so C-9/C-11 do not restart the supervisor during removal)"));
                Act(L.T("update mutex 획득(" + SystemAccess.MutexName("update") + ")", "acquire update mutex (" + SystemAccess.MutexName("update") + ")"), AcquireUpdate);

                Step(L.T("4. 시작프로그램 바로가기 삭제", "4. Delete the startup shortcut"));
                if (!SystemAccess.FileExists(StartupLnkPath)) log.Line(L.T("  없음", "  none"));
                else if (IsOurLnk(StartupLnkPath, instExe, "sup")) Act(L.T("삭제 " + StartupLnkPath, "delete " + StartupLnkPath), () => Retry(() => SystemAccess.DeleteFile(StartupLnkPath)));
                else log.Line(L.T("  남의 바로가기라 남김: " + StartupLnkPath, "  not our shortcut, kept: " + StartupLnkPath));

                Step(L.T("5. 허브 정지(감독 shutdown → core.stop → 검증 PID kill)", "5. Stop the hub (supervisor shutdown → core.stop → kill verified PID)"));
                if (string.IsNullOrEmpty(hubDir)) log.Line(L.T("  허브 폴더 모름 — 건너뜀", "  hub folder unknown — skipped"));
                else Act(L.T("감독에 shutdown(C-5, control.json stopped 는 건드리지 않음) — 응답 없으면 core.stop·검증 PID kill", "shutdown to the supervisor (C-5; control.json stopped is left alone) — if no response, core.stop and kill verified PID"), () => StopHub("uninstall"));

                Step(L.T("6. 바탕화면 아이콘 삭제(대상 = 설치한 UartHub.exe 이고 인자 open <포트> 인 것만, E1)", "6. Delete desktop icons (only those whose target = the installed UartHub.exe with the argument open <port>, E1)"));
                RemoveIcons();

                Step(L.T("7. 시작 메뉴 폴더 삭제", "7. Delete the Start menu folder"));
                if (!SystemAccess.DirExists(MenuDir)) log.Line(L.T("  없음", "  none"));
                else Act(L.T("자기 바로가기·빈 폴더 삭제 " + MenuDir, "delete our shortcuts and the empty folder " + MenuDir), RemoveMenu);

                Step(L.T("8. skills(.uart_hub 의 install_id 가 같을 때만)", "8. skills (only when install_id in .uart_hub matches)"));
                var recorded = SkillsDeployer.Recorded(InstJson);
                if (dry)
                    log.Line(L.T("  [dry-run] 후보: Windows ", "  [dry-run] candidates: Windows ") + Path.Combine(SystemAccess.UserProfile, ".claude", "skills", "uart") + " · WSL "
                             + (recorded.Any(x => x.Env == "wsl" && x.Placed) ? string.Join(", ", recorded.Where(x => x.Env == "wsl" && x.Placed).Select(x => x.Distro)) : L.T("(기록 없음)", "(no record)"))
                             + L.T(" — .uart_hub 의 install_id=" + (installId ?? "(모름)") + " 일 때만 지움", " — deleted only if install_id in .uart_hub is " + (installId ?? "(unknown)")));
                else
                {
                    var removed = SkillsDeployer.Remove(installId, recorded, log.Line);
                    log.Line("  skills: " + SkillsDeployer.Summary(removed));
                }

                Step(L.T("9. 자기 이미지 옮기기 → 설치 폴더 삭제", "9. Move the running image → delete the install folder"));
                if (SystemAccess.Under(self, instDir))
                {
                    string dst = Path.Combine(SystemAccess.Temp, "uart_hub_removed_" + ts + ".exe");
                    Act(L.T("실행 중인 자기 이미지 " + self + " → " + dst + " (다음 설치가 지움)", "running image " + self + " → " + dst + " (the next setup deletes it)"), () =>
                    {
                        try { Retry(() => SystemAccess.MoveFile(self, dst, true), 8); }
                        catch (TestIsolationViolation) { throw; }
                        catch (Exception e) { log.Line(L.T("    옮기지 못함(설치 폴더에 exe 가 남을 수 있음): " + e.Message, "    could not move it (the exe may stay in the install folder): " + e.Message)); }
                    });
                }
                else log.Line(L.T("  자기 이미지는 설치 폴더 밖(" + self + ") — 옮길 것 없음", "  the running image is outside the install folder (" + self + ") — nothing to move"));
                if (!string.IsNullOrEmpty(hubDir) && SystemAccess.DirExists(hubDir))
                {
                    string keep = Path.Combine(hubDir, "uninstall_" + ts + ".log");
                    Act(L.T("제거 기록 → " + keep, "removal record → " + keep), () => log.MoveTo(keep));
                }
                if (SystemAccess.DirExists(instDir))
                    Act(L.T("설치 폴더 삭제 " + instDir, "delete install folder " + instDir), () =>
                    {
                        // Directory.Delete(재귀)는 다른 프로그램(백신·색인기)이 잠깐 연 파일을 '삭제 대기'로만 두면 폴더 RemoveDirectory 의
                        // ERROR_DIR_NOT_EMPTY 를 예외 없이 넘긴다 → 폴더가 남았으면 실패로 보고 다시 시도한다
                        try
                        {
                            Retry(() =>
                            {
                                SystemAccess.DeleteDir(instDir);
                                if (SystemAccess.DirExists(instDir)) throw new IOException(L.T("설치 폴더가 아직 남음(다른 프로그램이 파일을 잡고 있음)", "the install folder still exists (another program holds a file)"));
                            });
                        }
                        catch (TestIsolationViolation) { throw; }
                        catch (Exception e) { log.Line(L.T("    삭제 실패: " + e.Message, "    delete failed: " + e.Message)); }
                    });
                else log.Line(L.T("  설치 폴더 없음", "  no install folder"));

                Step(L.T("10. 남김", "10. Kept"));
                log.Line(L.T("  로그 " + (logRoot ?? "(모름)") + ", 허브 폴더 " + (hubDir ?? "(모름)") + "(config.json·token·state.json·hub.log). control.json 은 그대로(stopped 로 바꾸지 않음)", "  logs " + (logRoot ?? "(unknown)") + ", hub folder " + (hubDir ?? "(unknown)") + " (config.json, token, state.json, hub.log). control.json is unchanged (not set to stopped)"));
                if (!dry) ReleaseUpdate();
                log.Line("");
                if (dry) { log.Line(L.T("# dry-run 끝 — 시스템을 바꾸지 않았습니다.", "# dry-run done — the system was not changed.")); return RcOk; }
                bool left = SystemAccess.DirExists(instDir);
                log.Line(L.T("# 제거 " + (left ? "일부 실패 — 설치 폴더가 남음" : "완료"), "# removal " + (left ? "partly failed — the install folder remains" : "done")));
                ui.Finish(!left, left ? L.T("제거를 마치지 못했습니다(설치 폴더가 남음): " + instDir, "Removal did not finish (the install folder remains): " + instDir) : L.T("UART 허브를 제거했습니다. 로그·설정은 " + (logRoot ?? hubDir) + " 에 남아 있습니다.", "UART Hub was removed. Logs and settings remain in " + (logRoot ?? hubDir) + "."), 5000);
                return left ? RcFailed : RcOk;
            }

            /// <summary>아이콘 후보: icons.json 의 path(앞 fromList 개) + 바탕화면 *.lnk(중복 포함 — 쓰는 쪽이 Distinct).</summary>
            List<string> IconCandidates(out int fromList)
            {
                var cand = new List<string>();
                string icons = Path.Combine(instDir, "icons.json");
                string t = SystemAccess.ReadText(icons);
                if (!string.IsNullOrWhiteSpace(t))
                {
                    try
                    {
                        using var d = JsonDocument.Parse(t);
                        void Walk(JsonElement e)
                        {
                            if (e.ValueKind == JsonValueKind.Object)
                            {
                                if (e.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String) cand.Add(p.GetString());
                                foreach (var c in e.EnumerateObject()) if (c.Value.ValueKind == JsonValueKind.Object || c.Value.ValueKind == JsonValueKind.Array) Walk(c.Value);
                            }
                            else if (e.ValueKind == JsonValueKind.Array) foreach (var c in e.EnumerateArray()) Walk(c);
                        }
                        Walk(d.RootElement);
                    }
                    catch (JsonException) { log.Line(L.T("  icons.json 깨짐 — 바탕화면 검사만", "  icons.json is corrupt — checking the desktop only")); }
                }
                fromList = cand.Count;
                cand.AddRange(SystemAccess.ListFiles(SystemAccess.Desktop, "*.lnk"));
                return cand;
            }

            /// <summary>icons.json 항목 + 바탕화면 *.lnk 중 대상 = 설치한 exe · 인자 open &lt;포트&gt; 인 것만 지운다(E1 + 보조 판정). 남의 파일은 남긴다.</summary>
            void RemoveIcons()
            {
                var cand = IconCandidates(out int fromList);
                int n = 0;
                foreach (var p in cand.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    bool listed = cand.IndexOf(p) < fromList;
                    if (!SystemAccess.FileExists(p)) continue;
                    var i = ShellLink.Read(p);
                    if (ShellLink.IsOurs(i, instExe, "open ", true)) { n++; Act(L.T("삭제 " + p, "delete " + p), () => Retry(() => SystemAccess.DeleteFile(p))); }
                    else if (listed) log.Line(L.T("  icons.json 에 있으나 자기 것이 아님(대상 " + (i?.Target ?? "?") + ") — 남김: " + p, "  listed in icons.json but not ours (target " + (i?.Target ?? "?") + ") — kept: " + p));
                }
                log.Line(L.T("  자기 아이콘 " + n + "개" + (fromList > 0 ? " (icons.json " + fromList + "개 확인)" : ""), "  our icons: " + n + (fromList > 0 ? " (" + fromList + " confirmed in icons.json)" : "")));
            }

            /// <summary>휴대용(X4): exe 경로가 바뀌면 옛 exe 를 가리키는 자기 아이콘(인자 open &lt;포트&gt;)의 대상만 새 exe 로 바꾼다 — 새 감독이 이어받는다(E1 소유 판정).</summary>
            void RetargetIcons(string oldExe, string newExe)
            {
                int n = 0;
                foreach (var p in IconCandidates(out _).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!SystemAccess.FileExists(p)) continue;
                    var i = ShellLink.Read(p);
                    if (!ShellLink.IsOurs(i, oldExe, "open ", true)) continue;
                    n++;
                    Act(L.T("아이콘 대상 바꿈 " + p + " → " + newExe, "retarget icon " + p + " → " + newExe), () =>
                    {
                        i.Target = newExe; i.IconPath = newExe; i.IconIndex = 0; i.WorkingDirectory = Path.GetDirectoryName(newExe);
                        Retry(() => ShellLink.Save(p, i));
                    });
                }
                log.Line(L.T("  옛 exe 를 가리키던 자기 아이콘 " + n + "개", "  our icons that pointed to the old exe: " + n));
            }

            static string Trunc(string s, int n) => s == null ? "" : s.Length > n ? s.Substring(0, n) + "…" : s;
        }
    }
}
