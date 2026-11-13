// 감독 트레이(계획 T3 UI 쪽, A2-8, UI_COPY.md "트레이"·"풍선"). sup 의 트레이 경로에서만 쓴다(cli·core·open 은 WinForms 를 로드하지 않는다). 시험 모드(C-13)는 트레이를 띄우지 않는다.
// 감독 로직은 WinForms Timer(UI 스레드)에서 Tick() 으로 돈다. 클릭 처리기도 UI 스레드다. 코어에 보내는 케이블 판정만 백그라운드 작업이고, 결과는 Timer 가 받는다.
// 아이콘: 실행 tray_run.ico(주황 별) · 에이징 기록 중 tray_rec.ico(주황 별 + 흰 테두리 초록 점) · 정지 tray_stop.ico(회색) · 경고(계속 꺼짐·설정 파일 오류) tray_warn.ico(빨간 점).
//   우선순위 경고 > 에이징 중 > 실행 > 정지(Supervisor.IconKindOf). 툴팁 = Supervisor.TrayTitle("UART 허브 · 연결 3개 · 에이징 중 1개").
// 메뉴(정확히 이 순서, TrayMenu — 모양은 MenuKit.cs: Claude 데스크톱 앱 트레이 메뉴 계열, Windows 앱 테마를 따름):
//   UART 허브(제목 줄, 별) | 상태 줄("연결 3개"·"연결 없음"·"정지됨"·"다시 켜는 중"·"계속 꺼짐 · 로그 확인") · 연결된 포트 줄 3개까지("EVB-A1 · COM3 · 7001", 주황 점) · "외 N개"
//     · 에이징 세션 줄 3개까지("에이징 중 · 이름 · 2시간 13분 · 마지막 출력 3초 전" + 포트 줄과 같은 그린 점 — 초록, 10분 넘게 출력 없으면 "출력 없음 12분"·빨간 점)
//   | COM 포트 ▸ · 로그 폴더 열기 · 에이징 폴더 열기 | 설정 ▸(터미널 프로그램 ▸ · 언어 ▸ · 미연결 UART 정리… · 제거…) | 시작 · 정지 · 허브 끄기
//   제목·상태·포트 줄은 회색 정보 줄(누를 수 없음). 메뉴 항목에는 괄호 설명을 달지 않는다 — 설명·경로는 툴팁(ToolTipText).
//   COM 포트 ▸  on_demand(X5 부록 B.4): 연결됨 "COM3 · EVB-A1 (7001)"(주황 점) ▸ 터미널 열기(붙어 있을 때) · 끊기(= /disconnect-uart) / "COM8 · 연결 안 됨" ▸ 연결(= /connect-uart — 같은 흐름·확인 창·터미널)
//               on_demand 아님: 확인 필요 ▸ 답하기… / 제외됨 ▸ 제외 풀기 / 그 밖 ▸ 터미널 열기 · 제외. config.json mode:"ignore" → "설정 파일에서 제외"(하위 없음).
//               붙은 포트 ▸ 에이징 시작…(AgingForm 이름 창) / 세션 중 "… · 에이징 중" ▸ 에이징 끝내기(부록 C.12 — 코어 session start·stop, COM 을 열고 닫지 않음).
//   터미널 프로그램 ▸  자동(TeraTerm → PuTTY → MobaXterm — 툴팁) · 찾은 터미널(경로는 툴팁) · 찾아보기…(ttermpro*·putty*·MobaXterm* 만) → prefs.json. 지금 고른 것 = 글자색 점.
//   언어 ▸  자동 · 한국어 · English(언어 이름은 늘 그 언어로) → Supervisor.RequestLanguage(= uart hub language: config.json "language" 글자 편집 C-12, 자동 = 키 지움)
//           → ApplyLanguage(감독은 바로 바꾸고 코어는 다시 띄움 —
//           모든 프로세스가 같은 언어) → 메뉴 고정 글을 다시 씀(TrayMenu.ApplyText). 점 = config.json 의 값. 코어를 다시 띄우면 열린 터미널이 끊기므로 먼저 확인 창.
//   로그 폴더 열기 = sup.LogRoot(기준 폴더, 예 D:\uart_logs) · 에이징 폴더 열기 = sup.EventsDir(log_layout 3 = D:\uart_logs\에이징 — 세션 폴더들). 누를 때만 explorer 로 연다.
//   미연결 UART 정리… = CleanupForm(꽂혀 있지 않은 UART 의 포트 번호 기록, 체크한 것만 지움). 목록 읽기·지우기는 백그라운드(Job), 결과는 풍선 "UART 기록 N개를 지웠습니다."
//   시작·정지 = 해당하는 것만 보인다(둘 다 해당하면 둘 다). 제거… = `UartHub.exe uninstall`(확인 창). 허브 끄기 = 감독 요청 shutdown(C-5): 코어·감독을 모두 내려 exe 를 덮어쓸 수 있게 한다.
//   자동 실행은 메뉴에 없다(방법은 문서·처음 시작 창의 "자세히"에만, 2026-11-06 C.11).
// 풍선(제목 "UART 허브"): 케이블 붙음("EVB-A1 (7002) 연결했습니다. 누르면 터미널이 열립니다." → 클릭 = AllowSetForegroundWindow 뒤 `open <포트> --from balloon`),
//       새 케이블("COM9: 보드 케이블인가요? …" → 클릭 = 그 케이블의 질문 창), 계속 꺼짐, 감독 알림(C-14·이미 실행 중·F1), 아이콘 이름 충돌,
//       에이징 이벤트(X6, 감독이 포트당 1분에 1번으로 묶음 → 클릭 = 그 이벤트 txt 를 기본 편집기로 연다. 사람이 누를 때만 연다),
//       에이징 시작·끝("에이징을 끝냈습니다. 요약을 저장했습니다." → 클릭 = 세션 폴더).
// 첫 연결 질문(FirstConnectForm): pending 케이블마다 하나(비모달·맨 위·포커스 안 뺏음). 닫으면 무응답 — 이번 감독 실행에서는 다시 자동으로 띄우지 않고
//   상태 줄 "확인 필요 n"·COM 포트 [답하기…]로 남는다. on_demand 면 묻지 않는다(sup.PendingCables 가 빔).
// /connect-uart 의 확인 창·안내 창(ConnectForms.WinConnectUi)은 감독의 connect 흐름(백그라운드)이 UI 스레드에 띄운다.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UartHub.App
{
    static class TrayApp
    {
        /// <param name="hubDir">config.json 이 있는 허브 폴더(설정 ▸ 언어가 쓴다).</param>
        public static void Run(Supervisor sup, int tickMs, string hubDir)
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // UI 스레드 예외는 대화상자 대신 hub.log 로(창을 띄우지 않는다)
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => { try { sup.LogError(e.Exception); } catch (Exception) { } };
            new Tray(sup, tickMs, hubDir).Run();
        }

        /// <summary>켜지 못함 알림(uart_hub.ps1:47-71 의 DLL 로드 실패 풍선 대체): 경고 아이콘 + 풍선 30초. 글 = "UART 허브를 켜지 못했습니다." + msg 첫 줄.</summary>
        public static void ShowFatal(string msg)
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Icon icon;
                try { icon = TrayIcons.Load("warn"); } catch (Exception) { icon = SystemIcons.Error; }
                using var ni = new NotifyIcon { Icon = icon, Text = L.T("UART 허브 · 켜지 못함", "UART Hub · could not start"), Visible = true };
                string first = (msg ?? "").Split('\n')[0].Trim();
                ni.ShowBalloonTip(30000, L.T("UART 허브", "UART Hub"), L.T("UART 허브를 켜지 못했습니다.", "UART Hub could not start.") + (first.Length > 0 ? "\n" + first : ""), ToolTipIcon.Error);
                Thread.Sleep(30000);
                ni.Visible = false;
            }
            catch (Exception) { }
        }
    }

    /// <summary>트레이 아이콘 셋(실행·정지·경고). 크기는 알림 영역 작은 아이콘(DPI 에 맞춤).</summary>
    static class TrayIcons
    {
        public static Icon Load(string kind) => Theme.Icon(FileOf(kind), SystemInformation.SmallIconSize);

        /// <summary>종류 → 아이콘 파일: run 주황 별 · rec 주황 별 + 초록 기록 점(에이징 중) · warn 빨간 점 · 그 밖 회색.</summary>
        public static string FileOf(string kind) => kind switch { "run" => "tray_run.ico", "rec" => "tray_rec.ico", "warn" => "tray_warn.ico", _ => "tray_stop.ico" };
    }

    /// <summary>트레이 메뉴 동작(트레이가 채움 — render-ui 의 모형은 비워 둔다).</summary>
    sealed class TrayActions
    {
        public Action<string, string> Connect;          // (verb connect|disconnect, com)
        public Action<int> OpenTerminal;                // 포트
        public Action<string, string, string> Cable;    // (verb exclude|include, id, com)
        public Action<string> Ask;                      // 케이블 id(첫 연결 질문 창)
        public Action<string, string> SetTerminal;      // (auto|teraterm|putty|mobaxterm, 경로)
        public Action<string> SetLanguage;              // auto|ko|en(config.json "language")
        public Action Browse;
        public Action<CableRow> AgingStart;             // 에이징 시작…(이름 창, 부록 C.12)
        public Action<CableRow> AgingStop;              // 에이징 끝내기
    }

    /// <summary>
    /// 트레이 메뉴(UI_COPY.md "메뉴"): 제목 → ─ → 상태·연결된 포트(정보 줄) → ─ → COM 포트 ▸ → 로그 폴더 열기 → 에이징 폴더 열기 → ─ → 설정 ▸ → ─ → 시작/정지 → 허브 끄기.
    /// 하위 메뉴는 열 때 FillComs·FillTerminals 로 다시 채운다(트레이·render-ui 가 같은 코드를 쓴다). 모양은 MenuStyle(MenuKit.cs).
    /// </summary>
    sealed class TrayMenu : IDisposable
    {
        /// <summary>포트 줄은 이만큼까지, 나머지는 "외 N개".</summary>
        const int MaxPorts = 3;
        public readonly MenuStyle Style;
        public readonly ContextMenuStrip Menu = new MenuRoot();
        public readonly MenuRow Title = new MenuRow("", RowKind.Title);
        public readonly MenuRow Status = new MenuRow("", RowKind.Info);
        readonly MenuRow[] ports = Enumerable.Range(0, MaxPorts).Select(_ => new MenuRow("", RowKind.Port)).ToArray();
        readonly MenuRow more = new MenuRow("", RowKind.Info) { Indent = true };
        public readonly MenuRow Coms = new MenuRow(""), Logs = new MenuRow(""), Events = new MenuRow("");      // 글은 ApplyText
        /// <summary>에이징 세션 줄은 이만큼까지(정보 줄 "에이징 중 · 이름 · 경과 · 마지막 출력", 점은 MenuRow.DotColor).</summary>
        const int MaxSessions = 3;
        readonly MenuRow[] sessions = Enumerable.Range(0, MaxSessions).Select(_ => new MenuRow("", RowKind.Port)).ToArray();   // 포트 줄과 같은 점(색만 초록·빨강)
        public readonly MenuRow Settings = new MenuRow(""), Terms = new MenuRow(""), Lang = new MenuRow(""), Cleanup = new MenuRow(""), Uninstall = new MenuRow("");
        public readonly MenuRow Start = new MenuRow(""), Stop = new MenuRow(""), Quit = new MenuRow("");
        string shown;

        /// <summary>
        /// 고정 글(메뉴 이름·툴팁): 만들 때와 언어를 바꾼 뒤(Supervisor.ApplyLanguage) 부른다 — L.T 는 부를 때 언어를 고르므로 만들어 둔 글은 다시 써야 한다.
        /// 상태 줄은 다음 SetStatus 가, 하위 메뉴(COM 포트·터미널 프로그램·언어)는 열 때마다 다시 채운다.
        /// </summary>
        public void ApplyText()
        {
            Title.Text = L.T("UART 허브", "UART Hub");
            Title.AccessibleName = L.T("UART 허브", "UART Hub");
            Coms.Text = L.T("COM 포트", "COM ports");
            Logs.Text = L.T("로그 폴더 열기", "Open log folder");
            Events.Text = L.T("에이징 폴더 열기", "Open aging folder");
            Events.ToolTipText = L.T("에이징 세션 폴더(콘솔 로그, 이벤트, 요약)가 모인 곳을 엽니다.", "Opens the folder that holds the aging run folders (console log, events, summary).");
            Settings.Text = L.T("설정", "Settings");
            Terms.Text = L.T("터미널 프로그램", "Terminal program");
            Lang.Text = L.T("언어", "Language");
            Cleanup.Text = L.T("미연결 UART 정리…", "Clean up disconnected UARTs…");
            Cleanup.ToolTipText = L.T("지금 PC에 꽂혀 있지 않은 UART의 포트 번호 기록을 골라 지웁니다.", "Pick UARTs that are not plugged into this PC and delete their port number records.");
            Uninstall.Text = L.T("제거…", "Remove…");
            Uninstall.ToolTipText = L.T("바탕화면 보드 아이콘, Claude 연결 정보, 등록 정보를 지웁니다. exe와 로그는 남습니다.", "Removes the desktop board icons, the Claude connection info and the registration. The exe and the logs stay.");
            Start.Text = L.T("시작", "Start");
            Start.ToolTipText = L.T("허브를 다시 켭니다.", "Starts the hub again.");
            Stop.Text = L.T("정지", "Stop");
            Stop.ToolTipText = L.T("정지하면 허브가 모든 COM 포트를 놓습니다.", "When stopped, the hub releases every COM port.");
            Quit.Text = L.T("허브 끄기", "Quit hub");
            Quit.ToolTipText = L.T("허브와 트레이 아이콘을 모두 끕니다. exe를 바꿀 때 씁니다.", "Quits the hub and its tray icon. Use this before replacing the exe.");
            shown = null;                                                    // 상태 줄·"외 N개"도 다음 SetStatus 에 다시
        }

        /// <param name="zoom">render-ui 의 화면 배율 흉내(1.5 = 150%). 트레이는 1.</param>
        public TrayMenu(float zoom = 1f)
        {
            Style = new MenuStyle(zoom);
            Style.Attach(Menu, root: true);
            Title.Font = Style.Bold;
            ApplyText();
            Menu.Items.Add(Title);
            Menu.Items.Add(new MenuSep());
            Menu.Items.Add(Status);
            foreach (var p in ports) Menu.Items.Add(p);
            Menu.Items.Add(more);
            foreach (var r in sessions) Menu.Items.Add(r);
            Menu.Items.Add(new MenuSep());
            Menu.Items.Add(Coms);
            Menu.Items.Add(Logs);
            Menu.Items.Add(Events);
            Menu.Items.Add(new MenuSep());
            Menu.Items.Add(Settings);
            Menu.Items.Add(new MenuSep());
            foreach (var mi in new[] { Start, Stop, Quit }) Menu.Items.Add(mi);
            Settings.DropDownItems.Add(Terms);
            Settings.DropDownItems.Add(Lang);
            Settings.DropDownItems.Add(Cleanup);
            Settings.DropDownItems.Add(new MenuSep());
            Settings.DropDownItems.Add(Uninstall);
            foreach (var dd in new[] { Coms, Settings, Terms, Lang }) Style.Attach(dd.DropDown);
            Coms.DropDownItems.Add(new MenuRow(L.T("허브가 꺼져 있습니다", "The hub is off")) { Enabled = false });
            Terms.DropDownItems.Add(new MenuRow(L.T("찾는 중", "Searching")) { Enabled = false });
            FillLanguages(null, null);
            SetStatus(Supervisor.TitleOf("starting", false, false, 0, 0, 0), new CableRow[0]);
        }

        /// <summary>
        /// 상태 줄 + 연결된 포트 줄. title = Supervisor.TrayTitle("UART 허브 · 연결 3개 · 확인 필요 1") — 앞의 "UART 허브 · "를 떼고 메뉴용으로 다듬는다.
        /// 바뀐 것이 없으면 아무것도 하지 않는다(트레이가 1초마다 부른다).
        /// </summary>
        public void SetStatus(string title, IEnumerable<CableRow> cables)
        {
            var on = (cables ?? new CableRow[0]).Where(c => c.Status == "attached" || c.Status == "bind_failed")
                                                .OrderBy(c => c.Port == 0 ? 1 : 0).ThenBy(c => c.Port).ThenBy(c => c.Com, StringComparer.OrdinalIgnoreCase).ToList();
            var lines = on.Take(MaxPorts).Select(PortLine).ToList();
            var agingRows = (cables ?? new CableRow[0]).Where(c => c.Aging).OrderBy(c => c.Port).Take(MaxSessions).ToList();
            var aging = agingRows.Select(c => c.AgingLine + (c.AgingStale ? "\u0001" : "")).ToList();   // 키에 점 색(출력 없음)도 넣는다
            string status = StatusOf(title), key = status + "\n" + string.Join("\n", lines) + "\n" + on.Count.ToString(CultureInfo.InvariantCulture) + "\n" + string.Join("\n", aging);
            if (key == shown) return;
            shown = key;
            Status.Text = status;
            for (int i = 0; i < MaxPorts; i++)
            {
                ports[i].Text = i < lines.Count ? lines[i] : "";
                ports[i].Available = i < lines.Count;
            }
            more.Text = L.T("외 " + (on.Count - MaxPorts).ToString(CultureInfo.InvariantCulture) + "개", (on.Count - MaxPorts).ToString(CultureInfo.InvariantCulture) + " more");
            more.Available = on.Count > MaxPorts;
            for (int i = 0; i < MaxSessions; i++)
            {
                sessions[i].Text = i < aging.Count ? agingRows[i].AgingLine : "";
                bool stale = i < aging.Count && agingRows[i].AgingStale;
                sessions[i].DotColor = stale ? (Func<Palette, Color>)(p => p.Bad) : p => p.Rec;   // 기록 중 초록 · 10분 넘게 출력 없음 빨강(글은 그대로)
                sessions[i].Available = i < aging.Count;
            }
        }

        /// <summary>트레이 제목 → 메뉴 상태 줄: 앞의 "UART 허브 · "를 떼고, 긴 경고 두 개는 메뉴 길이로 줄인다(툴팁은 원래 글 그대로).</summary>
        internal static string StatusOf(string title)
        {
            string s = title ?? "";
            string head = L.T("UART 허브 · ", "UART Hub · ");                // Supervisor.TitleOf 와 같은 글(언어마다)
            if (s.StartsWith(head, StringComparison.Ordinal)) s = s.Substring(head.Length);
            return s.Replace(L.T("계속 꺼짐, 로그 확인 필요", "Keeps stopping, check the log"), L.T("계속 꺼짐 · 로그 확인", "Keeps stopping · check the log"))
                    .Replace(L.T("설정 파일 오류 (마지막 정상 설정으로 동작 중)", "Config file error (using the last good config)"), L.T("설정 파일 오류 · 마지막 정상 설정 사용 중", "Config file error · using the last good config"));
        }

        /// <summary>"EVB-A1 · COM3 · 7001" — 모르는 칸은 뺀다.</summary>
        static string PortLine(CableRow r)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(r.Board)) parts.Add(r.Board.Trim());
            if (!string.IsNullOrEmpty(r.Com)) parts.Add(r.Com);
            if (r.Port > 0) parts.Add(Port(r.Port));
            return string.Join(" · ", parts);
        }

        /// <summary>시작·정지: 해당하는 것만 보인다(정지 상태 = 시작만, 실행 중 = 정지만, 다시 켜는 중·계속 꺼짐 = 둘 다).</summary>
        public void SetRunState(bool stopped, string state)
        {
            bool canStart = stopped || state != "running", canStop = !stopped;
            Start.Available = canStart; Start.Enabled = canStart;
            Stop.Available = canStop; Stop.Enabled = canStop;
        }

        public void SetPaths(string logRoot, string eventsDir)
        {
            Logs.ToolTipText = logRoot;
            Events.ToolTipText = eventsDir;
        }

        static void Clear(ToolStripItemCollection items)
        {
            foreach (ToolStripItem it in items.Cast<ToolStripItem>().ToList()) { items.Remove(it); it.Dispose(); }
        }

        static MenuRow Item(string text, string tip, Action click)
        {
            var it = new MenuRow(text) { ToolTipText = tip };
            if (click != null) it.Click += (s, e) => click();
            return it;
        }

        static string Port(int p) => p.ToString(CultureInfo.InvariantCulture);
        /// <summary>"EVB-A1 (7001)", 보드명을 모르면 "포트 7001".</summary>
        static string Name(CableRow r) => !string.IsNullOrWhiteSpace(r.Board) ? r.Board.Trim() + " (" + Port(r.Port) + ")" : r.Port > 0 ? L.T("포트 " + Port(r.Port), "Port " + Port(r.Port)) : null;

        static string StatusText(string st) => st switch
        {
            "held_by_other" => L.T("다른 프로그램이 사용 중", "In use by another program"),
            "open_failed" => L.T("열지 못함", "Could not open"),
            "stopped" => L.T("정지됨", "Stopped"),
            "bind_failed" => L.T("연결됨, telnet 포트 문제", "Connected, telnet port problem"),
            "detached" => L.T("빠짐", "Unplugged"),
            _ => st,
        };

        /// <summary>COM 포트 ▸ 채우기(열 때마다). rows = 감독의 마지막 ls --detail.</summary>
        public void FillComs(IReadOnlyList<CableRow> all, bool onDemand, bool running, ICollection<string> conflicts, TrayActions a)
        {
            var items = Coms.DropDownItems;
            Clear(items);
            var rows = all.Where(c => c.Status != "detached").OrderBy(c => c.Port == 0 ? 1 : 0).ThenBy(c => c.Port).ThenBy(c => c.Com, StringComparer.OrdinalIgnoreCase).ToList();
            if (rows.Count == 0)
            {
                items.Add(new MenuRow(running ? L.T("꽂힌 COM 포트 없음", "No COM ports plugged in") : L.T("허브가 꺼져 있습니다", "The hub is off")) { Enabled = false });
                return;
            }
            foreach (var r in rows)
            {
                string id = r.Cable, com = r.Com, name = Name(r);
                int port = r.Port;
                bool on = r.Status == "attached" || r.Status == "bind_failed";
                var it = new MenuRow(null) { AccentMark = true };
                var tip = new List<string>();
                if (!string.IsNullOrEmpty(r.Description)) tip.Add(r.Description);
                tip.Add(L.T("일련번호 ", "Serial number ") + id + (r.Baud > 0 ? " · " + r.Baud.ToString(CultureInfo.InvariantCulture) + "bps" : ""));
                if (r.ModeExplicit == "auto") tip.Add("config.json mode: auto");
                string head = com ?? "COM?";
                if (r.ModeExplicit == "ignore")
                {
                    it.Text = head + L.T(" · 설정 파일에서 제외", " · Excluded in config file");
                    tip.Add(L.T("config.json의 mode가 ignore입니다. 허브가 이 COM 포트를 쓰지 않습니다.", "mode is ignore in config.json. The hub does not use this COM port."));
                }
                else if (onDemand)
                {
                    // X5: 연결됨(판정 board·제외 아님 — 열려 있거나 다시 여는 중) [터미널 열기][끊기] / 연결 안 됨(pending·released) [연결]
                    bool connected = r.Status != "pending" && r.Status != "released";
                    if (connected)
                    {
                        it.Text = head + " · " + (on ? name ?? L.T("연결됨", "Connected") : (name != null ? name + " · " : "") + StatusText(r.Status));
                        it.Checked = on;
                        if (r.Status == "attached" && port > 0) it.DropDownItems.Add(Item(L.T("터미널 열기", "Open terminal"), L.T("포트 " + Port(port) + " 터미널을 엽니다.", "Opens the terminal for port " + Port(port) + "."), () => a.OpenTerminal?.Invoke(port)));
                        AddAging(it, r, a);
                        it.DropDownItems.Add(Item(L.T("끊기", "Disconnect"), L.T("허브가 이 COM 포트를 닫습니다. 다른 프로그램이 쓸 수 있게 됩니다. /disconnect-uart와 같습니다.", "The hub closes this COM port so other programs can use it. Same as /disconnect-uart."), () => a.Connect?.Invoke("disconnect", com)));
                    }
                    else
                    {
                        it.Text = head + L.T(" · 연결 안 됨", " · Not connected");
                        it.DropDownItems.Add(Item(L.T("연결", "Connect"), L.T("다른 프로그램이 쓰고 있으면 확인 창을 띄웁니다. /connect-uart와 같습니다.", "If another program is using it, asks you first. Same as /connect-uart."), () => a.Connect?.Invoke("connect", com)));
                    }
                }
                else if (r.Status == "pending")
                {
                    it.Text = head + L.T(" · 확인 필요", " · Needs attention");
                    it.DropDownItems.Add(Item(L.T("답하기…", "Answer…"), L.T("보드 케이블인지 묻는 창을 엽니다.", "Opens the dialog that asks whether this is a board cable."), () => a.Ask?.Invoke(id)));
                }
                else if (r.Status == "released")
                {
                    it.Text = head + (name != null ? " · " + name : "") + L.T(" · 제외됨", " · Excluded");
                    it.DropDownItems.Add(Item(L.T("제외 풀기", "Include again"), L.T("허브가 다시 이 COM 포트를 열어 기록합니다.", "The hub opens and logs this COM port again."), () => a.Cable?.Invoke("include", id, com)));
                }
                else
                {
                    it.Text = head + " · " + (on ? name ?? L.T("연결됨", "Connected") : (name != null ? name + " · " : "") + StatusText(r.Status));
                    it.Checked = on;
                    if (r.Status == "attached" && port > 0) it.DropDownItems.Add(Item(L.T("터미널 열기", "Open terminal"), L.T("포트 " + Port(port) + " 터미널을 엽니다.", "Opens the terminal for port " + Port(port) + "."), () => a.OpenTerminal?.Invoke(port)));
                    AddAging(it, r, a);
                    it.DropDownItems.Add(Item(L.T("제외", "Exclude"), L.T("허브가 이 COM 포트를 닫고 쓰지 않습니다.", "The hub closes this COM port and stops using it."), () => a.Cable?.Invoke("exclude", id, com)));
                }
                if (r.Aging) { it.Text += L.T(" · 에이징 중", " · aging"); tip.Add(L.T("에이징 중: " + r.SessionName, "Aging: " + r.SessionName)); }
                if (r.IconName != null && conflicts.Contains(r.IconName)) { it.Text += L.T(" · 아이콘 이름 충돌", " · icon name conflict"); tip.Add(L.T("바탕화면에 같은 이름의 다른 바로 가기가 있어 아이콘을 만들지 못했습니다.", "Could not create the desktop icon: another shortcut with the same name is there.")); }
                it.ToolTipText = string.Join("\n", tip);
                Style.Attach(it.DropDown);
                items.Add(it);
            }
        }

        /// <summary>
        /// COM ▸ 의 에이징 항목(부록 C.12): 세션 중이면 "에이징 끝내기", 아니면 붙은 포트에만 "에이징 시작…". 세션 중인데 케이블이 빠져 있어도 끝낼 수 있다.
        /// </summary>
        static void AddAging(MenuRow it, CableRow r, TrayActions a)
        {
            if (r.Aging) it.DropDownItems.Add(Item(L.T("에이징 끝내기", "End aging"), L.T("콘솔 로그와 이벤트 기록을 멈추고 요약을 저장합니다.", "Stops recording the console log and events, and saves the summary."), () => a.AgingStop?.Invoke(r)));
            else if (r.Status == "attached" && r.Port > 0) it.DropDownItems.Add(Item(L.T("에이징 시작…", "Start aging…"), L.T("이 포트의 콘솔 로그와 이벤트를 이름을 붙인 폴더에 남깁니다.", "Saves this port's console log and events in a folder named after the run."), () => a.AgingStart?.Invoke(r)));
        }

        /// <summary>터미널 프로그램 ▸ 채우기: 자동 · 찾은 터미널(종류, 경로) · 찾아보기….</summary>
        public void FillTerminals(string current, IReadOnlyList<(string Kind, string Path)> found, TrayActions a)
        {
            var items = Terms.DropDownItems;
            Clear(items);
            items.Add(new MenuRow(L.T("자동", "Auto")) { Checked = current == "auto", ToolTipText = L.T("TeraTerm, PuTTY, MobaXterm 순서로 찾아 씁니다.", "Uses the first one found: TeraTerm, PuTTY, then MobaXterm.") }.With(it => it.Click += (s, e) => a.SetTerminal?.Invoke("auto", null)));
            foreach (var (k, p) in found)
                items.Add(new MenuRow(Terminals.Label(k)) { Checked = current == k, ToolTipText = p }.With(it => it.Click += (s, e) => a.SetTerminal?.Invoke(k, p)));
            if (found.Count == 0) items.Add(new MenuRow(L.T("찾은 터미널 없음", "No terminal found")) { Enabled = false });
            items.Add(new MenuSep());
            items.Add(new MenuRow(L.T("찾아보기…", "Browse…")) { ToolTipText = L.T("TeraTerm(ttermpro.exe), PuTTY(putty.exe), MobaXterm만 고를 수 있습니다.", "Only TeraTerm (ttermpro.exe), PuTTY (putty.exe) and MobaXterm can be chosen.") }.With(it => it.Click += (s, e) => a.Browse?.Invoke()));
        }

        /// <summary>언어 ▸ 채우기: 자동 · 한국어 · English. current = config.json "language"(auto|ko|en), 그 줄에 점.</summary>
        public void FillLanguages(string current, TrayActions a)
        {
            var items = Lang.DropDownItems;
            Clear(items);
            foreach (var (v, text) in new[] { (L.Auto, L.T("자동", "Auto")), (L.Ko, "한국어"), (L.En, "English") })   // 언어 이름은 그 언어로
                items.Add(new MenuRow(text) { Checked = current == v }.With(it => it.Click += (s, e) => a?.SetLanguage?.Invoke(v)));
            items[0].ToolTipText = L.T("Windows 표시 언어가 한국어면 한국어, 아니면 영어로 보입니다.", "Korean if the Windows display language is Korean, otherwise English.");
        }

        public void Dispose()
        {
            Menu.Dispose();
            Style.Dispose();
        }
    }

    static class ToolStripExt
    {
        public static T With<T>(this T item, Action<T> a) { a(item); return item; }
    }

    sealed class Tray
    {
        readonly Supervisor sup;
        readonly int tickMs;
        readonly string hubDir;
        readonly NotifyIcon ni = new NotifyIcon();
        readonly TrayMenu tm = new TrayMenu();
        readonly TrayActions actions = new TrayActions();
        readonly Dictionary<string, Icon> icons = new Dictionary<string, Icon>();
        string iconKind;

        // 풍선 클릭 대상(마지막 풍선만 — NotifyIcon 에 대기열이 없다, §2.3 d)
        int balloonPort;
        string balloonAskId, balloonOpen;
        // 첫 연결 질문 창(케이블 ID → 창), 자동으로 한 번 띄운 케이블
        readonly Dictionary<string, FirstConnectForm> forms = new Dictionary<string, FirstConnectForm>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> asked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> conflictShown = new HashSet<string>(StringComparer.Ordinal);
        // 미연결 UART 정리 창(하나만) · 목록을 읽는 중
        CleanupForm cleanup;
        bool cleanupLoading;
        // 백그라운드 작업(코어 cable 명령) → Timer 가 끝난 것의 후속을 UI 스레드에서 부른다
        readonly List<(Task<string> T, Action<string> Done)> jobs = new List<(Task<string>, Action<string>)>();

        public Tray(Supervisor sup, int tickMs, string hubDir) { this.sup = sup; this.tickMs = tickMs; this.hubDir = hubDir; }

        Icon IconFor(string kind)
        {
            if (icons.TryGetValue(kind, out var i)) return i;
            try { i = TrayIcons.Load(kind); }
            catch (Exception) { i = kind == "warn" ? SystemIcons.Warning : SystemIcons.Application; }
            return icons[kind] = i;
        }

        void SetIcon(string kind)
        {
            if (kind == iconKind) return;
            iconKind = kind;
            ni.Icon = IconFor(kind);
        }

        public void Run()
        {
            sup.ConnectUi = new WinConnectUi(System.Threading.SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext());
            actions.Connect = (verb, com) => Safe(() => ConnectAction(verb, com));
            actions.OpenTerminal = port => Safe(() => OpenTerminal(port));
            actions.Cable = (verb, id, com) => Safe(() => CableAction(verb, id, com));
            actions.Ask = id => Safe(() => { asked.Add(id); ShowForm(id, true); });
            actions.SetTerminal = (k, p) => Safe(() => SavePrefs(k, p));
            actions.SetLanguage = v => Safe(() => SetLanguage(v));
            actions.Browse = () => Safe(Browse);
            actions.AgingStart = r => Safe(() => AgingStart(r));
            actions.AgingStop = r => Safe(() => AgingStop(r));
            tm.Start.Click += (s, e) => Safe(() => sup.RequestStart("tray"));
            tm.Stop.Click += (s, e) => Safe(() => sup.RequestStop("tray"));
            tm.Logs.Click += (s, e) => Explore(sup.LogRoot, false);
            // 지금 언어의 에이징 루트가 없고 다른 언어 루트만 있으면 그것을 연다(언어를 바꾼 뒤 — 지난 세션은 옛 루트에 있다). 메뉴를 열 때 한 번 정해 툴팁과 클릭이 같은 곳
            string AgingFolder() => (sup.EventsDirs ?? new string[0]).FirstOrDefault(System.IO.Directory.Exists) ?? sup.EventsDir;
            string agingFolder = null;
            tm.Events.Click += (s, e) => Explore(agingFolder ?? AgingFolder(), true);
            tm.Cleanup.Click += (s, e) => Safe(OpenCleanup);
            tm.Uninstall.Click += (s, e) => Safe(() =>
            {
                SystemAccess.AllowForeground();                           // 제거 확인 창이 앞으로 올 수 있게
                SystemAccess.StartDetached(Environment.ProcessPath, new[] { "uninstall" });
            });
            tm.Quit.Click += (s, e) => Safe(() => sup.RequestShutdown("tray"));
            tm.Coms.DropDownOpening += (s, e) => Safe(() => tm.FillComs(sup.Cables, sup.OnDemand, sup.State == "running", new HashSet<string>(sup.IconConflicts, StringComparer.Ordinal), actions));
            tm.Terms.DropDownOpening += (s, e) => Safe(BuildTerminals);
            tm.Lang.DropDownOpening += (s, e) => Safe(() => tm.FillLanguages(ConfiguredLanguage(), actions));
            tm.Menu.Opening += (s, e) => Safe(() => { agingFolder = AgingFolder(); tm.SetPaths(sup.LogRoot, agingFolder); tm.SetStatus(sup.TrayTitle, sup.Cables); });
            ni.BalloonTipClicked += (s, e) => Safe(OnBalloonClicked);
            ni.ContextMenuStrip = tm.Menu;
            SetIcon(sup.TrayIconKind);
            ni.Text = L.T("UART 허브", "UART Hub");
            ni.Visible = true;

            string lastTitle = "";
            bool warnedLoop = false;
            int trayTick = 0;
            var timer = new System.Windows.Forms.Timer { Interval = tickMs };
            timer.Tick += (s, e) =>
            {
                try { sup.Tick(); } catch (Exception x) { try { sup.LogError(x); } catch (Exception) { } }
                if (sup.QuitRequested) { timer.Stop(); Application.ExitThread(); return; }
                Safe(FinishJobs);
                trayTick++;
                if (trayTick % Math.Max(1, 1000 / tickMs) != 0) return;
                Safe(() =>
                {
                    string title = sup.TrayTitle;
                    if (title != lastTitle)
                    {
                        lastTitle = title;
                        ni.Text = title.Length > 63 ? title.Substring(0, 63) : title;
                    }
                    tm.SetStatus(title, sup.Cables);                       // 바뀐 것이 없으면 그대로(열린 메뉴도 따라 바뀐다)
                    SetIcon(sup.TrayIconKind);
                    if (sup.CrashLoop && !warnedLoop)
                    {
                        warnedLoop = true;
                        Balloon(L.T("허브가 계속 꺼지고 있습니다. 로그 폴더의 hub.log를 담당자에게 보내 주세요.", "The hub keeps stopping. Please send hub.log from the log folder to whoever looks after the hub."), ToolTipIcon.Warning);
                    }
                    else if (!sup.CrashLoop) warnedLoop = false;
                    foreach (var notice in sup.TakeNotices())    // C-14 결과·설치기 멈춤 경고·이미 실행 중·F1(점유 프로그램)
                        Balloon(notice, ToolTipIcon.Info);
                    foreach (var ev in sup.TakeEventNotices())   // X6 에이징 이벤트(감독이 포트당 1분에 1번으로 묶음)
                        Balloon(ev.Text, ToolTipIcon.Warning, open: ev.Open ?? sup.EventsDir);
                    tm.SetRunState(sup.Stopped, sup.State);
                    foreach (var r in sup.TakeAttached())
                        Balloon(L.T(r.IconName + " 연결했습니다. 누르면 터미널이 열립니다.", "Connected " + r.IconName + ". Click to open the terminal."), ToolTipIcon.Info, port: r.Port);
                    AskPending();
                    foreach (var c in sup.IconConflicts)
                        if (conflictShown.Add(c))
                            Balloon(L.T("바탕화면에 같은 이름의 바로 가기('" + c + "')가 있어 아이콘을 만들지 못했습니다. 그 파일의 이름을 바꾸거나 지워 주세요.", "Could not create the desktop icon: a shortcut named '" + c + "' is already there. Rename or delete that file."), ToolTipIcon.Warning);
                });
            };
            timer.Start();
            Application.Run();
            timer.Dispose();
            foreach (var f in forms.Values.ToList()) { try { f.Close(); } catch (Exception) { } }
            try { cleanup?.CloseQuietly(); } catch (Exception) { }
            ni.Visible = false;
            ni.Dispose();
            tm.Dispose();
            foreach (var i in icons.Values) i.Dispose();
        }

        void Safe(Action a)
        {
            try { a(); } catch (Exception x) { try { sup.LogError(x); } catch (Exception) { } }
        }

        static void Explore(string dir, bool create)
        {
            try
            {
                if (create) System.IO.Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { dir } })?.Dispose();
            }
            catch (Exception) { }
        }

        void OpenTerminal(int port)
        {
            if (port <= 0) return;
            SystemAccess.AllowForeground();                               // 띄울 open 이 터미널 창을 앞으로 가져올 수 있게
            SystemAccess.StartDetached(Environment.ProcessPath, new[] { "open", port.ToString(CultureInfo.InvariantCulture) });
        }

        void Balloon(string text, ToolTipIcon icon, int port = 0, string askId = null, string open = null)
        {
            balloonPort = port;
            balloonAskId = askId;
            balloonOpen = open;
            ni.ShowBalloonTip(10000, L.T("UART 허브", "UART Hub"), text, icon);
        }

        void OnBalloonClicked()
        {
            if (balloonAskId != null) { ShowForm(balloonAskId, true); return; }
            if (balloonOpen != null) { OpenEventFile(balloonOpen, sup.EventsDirs ?? new[] { sup.EventsDir }); return; }
            if (balloonPort <= 0) return;
            SystemAccess.AllowForeground();                               // 띄울 open 이 터미널 창을 앞으로 가져올 수 있게
            SystemAccess.StartDetached(Environment.ProcessPath, new[] { "open", balloonPort.ToString(CultureInfo.InvariantCulture), "--from", "balloon" });
        }

        /// <summary>
        /// 에이징 이벤트 풍선 클릭(X6): 이벤트 폴더(sup.EventsDirs — 두 언어의 에이징 루트) 아래의 이벤트 txt 만 기본 편집기로, 그 폴더 자체·그 아래 폴더면 explorer 로
        /// (L9 — 그 밖의 경로는 열지 않는다). 다른 언어로 시작한 세션의 풍선도 열린다. 사람이 누를 때만 불린다.
        /// </summary>
        static void OpenEventFile(string path, IEnumerable<string> eventsDirs)
        {
            try
            {
                string full = System.IO.Path.GetFullPath(path);
                foreach (var dir in eventsDirs)
                {
                    if (string.IsNullOrEmpty(dir)) continue;
                    string root = System.IO.Path.GetFullPath(dir).TrimEnd('\\') + "\\";
                    if (string.Equals(full.TrimEnd('\\') + "\\", root, StringComparison.OrdinalIgnoreCase)
                        || (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && System.IO.Directory.Exists(full)))     // 이벤트 폴더 자체 또는 그 아래 폴더(에이징 세션 폴더)
                    {
                        if (System.IO.Directory.Exists(full)) Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { full } })?.Dispose();
                        return;
                    }
                    if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !full.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(full)) continue;
                    Process.Start(new ProcessStartInfo(full) { UseShellExecute = true })?.Dispose();
                    return;
                }
            }
            catch (Exception) { }
        }

        // ================================================================== 첫 연결 질문
        /// <summary>새 pending 케이블마다 질문 창 + 풍선(한 번). 판정이 끝난(pending 아님으로 보이는) 케이블의 창은 닫는다.</summary>
        void AskPending()
        {
            var pend = sup.PendingCables;
            foreach (var r in pend)
            {
                if (r.Cable == null || asked.Contains(r.Cable)) continue;
                asked.Add(r.Cable);
                ShowForm(r.Cable, false);
                Balloon(L.T((r.Com ?? r.Cable) + ": 보드 케이블인가요? 누르면 답하는 창이 열립니다.", (r.Com ?? r.Cable) + ": Is this a board cable? Click to answer."), ToolTipIcon.Info, askId: r.Cable);
            }
            foreach (var kv in forms.ToList())
            {
                var row = sup.Cables.FirstOrDefault(c => string.Equals(c.Cable, kv.Key, StringComparison.OrdinalIgnoreCase));
                if (row != null && row.Status != "pending") kv.Value.CloseQuietly();      // 다른 곳에서 정해짐(목록에서 잠깐 사라진 것은 그대로 둔다)
            }
        }

        void ShowForm(string id, bool activate)
        {
            if (forms.TryGetValue(id, out var f)) { if (activate) f.Activate(); return; }
            var row = sup.Cables.FirstOrDefault(c => string.Equals(c.Cable, id, StringComparison.OrdinalIgnoreCase));
            if (row == null || row.Status != "pending") return;
            f = new FirstConnectForm(row, forms.Count, Decide);
            f.FormClosed += (s, e) => forms.Remove(id);
            forms[id] = f;
            f.Show();
            if (activate) f.Activate();
        }

        /// <summary>FirstConnectForm 의 답: 코어에 cable decide 를 보내고(백그라운드) 결과를 창에 돌려준다.</summary>
        void Decide(string id, bool board, Action<string> done)
            => Job(() => sup.CableCommand("decide", id, board ? "board" : "ignore"), done);

        // ================================================================== COM 포트 ▸ 동작
        /// <summary>트레이 [연결]·[끊기](X5): 감독의 connect·disconnect 흐름(확인 창 포함)을 백그라운드로 돌리고 결과를 풍선으로.</summary>
        void ConnectAction(string verb, string com)
        {
            if (string.IsNullOrEmpty(com)) return;
            var flow = sup.NewConnectFlow();                              // UI(= 감독) 스레드에서 코어 포트·pid·설정을 잡아 둔다 — 흐름은 백그라운드
            Job(() => (verb == "connect" ? flow.Connect(com, "tray") : flow.Disconnect(com, "tray")).ToString(), line =>
            {
                string status = null, err = null, board = null;
                long port = 0;
                try
                {
                    using var d = JsonDocument.Parse(line ?? "");
                    var r = d.RootElement;
                    status = HubFiles.Str(r, "status"); err = HubFiles.Str(r, "error"); board = HubFiles.Str(r, "board"); port = HubFiles.Long(r, "port");
                }
                catch (JsonException) { }
                string name = (board ?? com) + (port > 0 ? " (" + port + ")" : "");
                if (status == "ok") Balloon(verb == "connect" ? L.T(com + " 연결했습니다: " + name, "Connected " + com + ": " + name) : L.T(com + " 연결을 끊었습니다. 이제 다른 프로그램에서 쓸 수 있습니다.", "Disconnected " + com + ". Other programs can use it now."), ToolTipIcon.Info);
                else if (status == "cancelled") Balloon(L.T(com + " 연결을 취소했습니다. 바뀐 것은 없습니다.", com + ": cancelled. Nothing was changed."), ToolTipIcon.Info);
                else Balloon(L.T(com + " " + (verb == "connect" ? "연결" : "끊기") + " 실패: " + (err ?? status ?? "허브가 응답하지 않습니다."),
                                 (verb == "connect" ? "Could not connect " : "Could not disconnect ") + com + ": " + (err ?? status ?? "The hub is not responding.")), ToolTipIcon.Warning);
            });
        }

        // ================================================================== 에이징 세션(부록 C.12)
        AgingForm agingForm;

        /// <summary>에이징 시작…: 이름 창(하나만) → [시작] = 백그라운드 코어 session start → 풍선.</summary>
        void AgingStart(CableRow r)
        {
            if (agingForm != null && !agingForm.IsDisposed) { agingForm.Activate(); return; }
            int port = r.Port;
            string who = r.IconName;
            var now = DateTime.Now;
            agingForm = new AgingForm(who, AgingNames.DefaultName(r.Board, r.Com, port, now), sup.EventsDir, now, name =>
                Job(() => sup.SessionCommand("start", port.ToString(CultureInfo.InvariantCulture), name), line =>
                {
                    var (code, err) = Parse(line);
                    string folder = Field(line, "folder");
                    if (code == 0) Balloon(L.T("에이징을 시작했습니다. " + Josa.EulReul(who) + " 끝낼 때까지 기록합니다. 누르면 폴더가 열립니다.", "Aging started. Recording " + who + " until you end it. Click to open the folder."), ToolTipIcon.Info, open: folder);
                    else Balloon(L.T("에이징을 시작하지 못했습니다. " + (line == null ? "허브가 응답하지 않습니다." : err), "Could not start aging. " + (line == null ? "The hub is not responding." : err)), ToolTipIcon.Warning);
                }));
            agingForm.FormClosed += (s, e) => agingForm = null;
            SystemAccess.AllowForeground();
            agingForm.Show();
            agingForm.Activate();
        }

        /// <summary>에이징 끝내기: 백그라운드 코어 session stop(요약을 쓰고 답함) → 풍선, 누르면 세션 폴더.</summary>
        void AgingStop(CableRow r)
        {
            int port = r.Port;
            Job(() => sup.SessionCommand("stop", port.ToString(CultureInfo.InvariantCulture)), line =>
            {
                var (code, err) = Parse(line);
                if (code == 0) Balloon(L.T("에이징을 끝냈습니다. 요약을 저장했습니다. 누르면 폴더가 열립니다.", "Aging ended. The summary is saved. Click to open the folder."), ToolTipIcon.Info, open: Field(line, "folder") ?? r.SessionFolder);
                else Balloon(L.T("에이징을 끝내지 못했습니다. " + (line == null ? "허브가 응답하지 않습니다." : err), "Could not end aging. " + (line == null ? "The hub is not responding." : err)), ToolTipIcon.Warning);
            });
        }

        /// <summary>코어 응답 줄의 문자열 필드(없거나 깨지면 null).</summary>
        static string Field(string line, string key)
        {
            if (line == null) return null;
            try
            {
                using var d = System.Text.Json.JsonDocument.Parse(line);
                return d.RootElement.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
            }
            catch (System.Text.Json.JsonException) { return null; }
        }

        void CableAction(string verb, string id, string com)
        {
            string who = com ?? id;
            Job(() => sup.CableCommand(verb, id), line =>
            {
                var (code, err) = Parse(line);
                if (code == 0) Balloon(verb == "exclude" ? L.T(Josa.EulReul(who) + " 제외했습니다. 허브가 이 COM 포트를 쓰지 않습니다.", "Excluded " + who + ". The hub no longer uses this COM port.")
                                                         : L.T(who + " 제외를 풀었습니다. 허브가 다시 기록합니다.", "Included " + who + " again. The hub logs it again."), ToolTipIcon.Info);
                else Balloon(L.T(who + " " + (verb == "exclude" ? "제외" : "제외 풀기") + " 실패: " + (line == null ? "허브가 응답하지 않습니다." : err),
                                 (verb == "exclude" ? "Could not exclude " + who : "Could not include " + who + " again") + ": " + (line == null ? "The hub is not responding." : err)), ToolTipIcon.Warning);
            });
        }

        // ================================================================== 설정 ▸ 미연결 UART 정리…
        /// <summary>목록을 백그라운드로 읽고(코어 호출 5초 안) 창을 띄운다. 이미 떠 있으면 앞으로.</summary>
        void OpenCleanup()
        {
            if (cleanup != null && !cleanup.IsDisposed) { cleanup.Activate(); return; }
            if (cleanupLoading) return;
            cleanupLoading = true;
            List<RememberedCable> rows = null;
            string err = null;
            Job(() => { rows = sup.RememberedCables(out err); return ""; }, _ =>
            {
                cleanupLoading = false;
                cleanup = new CleanupForm(rows, rows == null ? err ?? L.T("허브가 응답하지 않습니다.", "The hub is not responding.") : null, RemoveCables);
                cleanup.FormClosed += (s, e) => cleanup = null;
                cleanup.Show();
                cleanup.Activate();
            });
        }

        /// <summary>창의 [기록 지우기]: 백그라운드로 지우고 창을 닫은 뒤 풍선.</summary>
        void RemoveCables(List<string> ids, Action done)
        {
            int n = 0;
            string err = null;
            Job(() => { n = sup.ForgetCables(ids, out err); return ""; }, line =>
            {
                done?.Invoke();
                if (line == null) err ??= L.T("허브가 응답하지 않습니다.", "The hub is not responding.");
                string count = n.ToString(CultureInfo.InvariantCulture);
                string records = count + (n == 1 ? " UART record" : " UART records");
                if (err == null) Balloon(L.T("UART 기록 " + count + "개를 지웠습니다.", "Deleted " + records + "."), ToolTipIcon.Info);
                else if (n > 0) Balloon(L.T("UART 기록 " + count + "개를 지웠습니다. 나머지는 지우지 못했습니다: " + err, "Deleted " + records + ". Could not delete the rest: " + err), ToolTipIcon.Warning);
                else Balloon(L.T("UART 기록을 지우지 못했습니다: " + err, "Could not delete the UART records: " + err), ToolTipIcon.Warning);
            });
        }

        // ================================================================== 터미널 프로그램 ▸
        void BuildTerminals()
        {
            var prefs = TermPrefs.Load();
            var det = Terminals.DetectAll();
            var found = new List<(string, string)>();
            foreach (var k in Terminals.Kinds)
            {
                string path = prefs.Paths.TryGetValue(k, out var pp) && SystemAccess.FileExists(pp) ? pp : det.FirstOrDefault(x => x.Kind == k)?.Path;
                if (path != null) found.Add((k, path));
            }
            tm.FillTerminals(prefs.Terminal, found, actions);
        }

        void Browse()
        {
            using var dlg = new OpenFileDialog
            {
                Title = L.T("터미널 프로그램 선택(TeraTerm·PuTTY·MobaXterm)", "Choose a terminal program (TeraTerm, PuTTY, MobaXterm)"), Filter = L.T("프로그램 (*.exe)|*.exe", "Programs (*.exe)|*.exe"), CheckFileExists = true,
                InitialDirectory = SystemAccess.ProgramFiles,
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            string kind = Terminals.KindFromFileName(dlg.FileName);
            if (kind == null)
            {
                MessageBox.Show(L.T("지원하지 않는 터미널입니다: " + System.IO.Path.GetFileName(dlg.FileName) + "\n\nTeraTerm(ttermpro.exe), PuTTY(putty.exe), MobaXterm(MobaXterm*.exe)만 쓸 수 있습니다.",
                                    "This terminal is not supported: " + System.IO.Path.GetFileName(dlg.FileName) + "\n\nOnly TeraTerm (ttermpro.exe), PuTTY (putty.exe) and MobaXterm (MobaXterm*.exe) can be used."),
                                L.T("UART 허브", "UART Hub"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            SavePrefs(kind, dlg.FileName);
        }

        void SavePrefs(string terminal, string path)
        {
            var p = TermPrefs.Load();
            p.Terminal = terminal;
            if (path != null) p.Paths[terminal] = path;
            p.Save();
            Balloon(L.T("터미널 프로그램: " + (terminal == "auto" ? "자동" : Terminals.Label(terminal)), "Terminal program: " + (terminal == "auto" ? "Auto" : Terminals.Label(terminal))), ToolTipIcon.Info);
        }

        // ================================================================== 설정 ▸ 언어 ▸
        /// <summary>config.json "language"(auto|ko|en). 없거나 읽지 못하면 auto.</summary>
        string ConfiguredLanguage() => L.SettingIn(HubFiles.ConfigPath(hubDir));

        /// <summary>
        /// 언어 ▸ 고르기: uart hub language 와 같은 Supervisor.RequestLanguage(config.json "language" 글자 편집 — auto 는 키를 지움 → ApplyLanguage) — 감독은 바로 그 언어로,
        /// 코어는 다시 띄운다(열린 터미널이 끊기므로 그때만 먼저 확인 창, [취소]면 아무것도 쓰지 않음). 뒤에 메뉴 고정 글을 다시 쓴다.
        /// 이미 config 와 같은 값이고 화면 언어도 같으면 아무것도 하지 않는다.
        /// </summary>
        void SetLanguage(string v)
        {
            string old = ConfiguredLanguage(), before = L.Current, after = L.ResolveFor(v);   // after = 새 설정으로 정해질 화면 언어(환경변수·Windows 언어 포함, 상태는 그대로)
            if (v == old && after == before) return;
            if (after != before && sup.State == "running" && !sup.Stopped)
            {
                var t = new UiText
                {
                    Head = L.T("언어를 바꾸면 허브를 다시 켭니다.", "Changing the language restarts the hub."),
                    Body = L.T("열린 터미널의 연결이 끊깁니다. 바탕화면 보드 아이콘으로 다시 열 수 있습니다. 에이징 기록은 이어집니다.",
                               "Open terminals lose their connection. You can reopen them from the desktop board icons. Aging runs keep recording."),
                    Ok = L.T("바꾸기", "Change"),
                };
                bool go;
                using (var w = new ConfirmWindow(t)) go = w.ShowDialog() == DialogResult.OK;
                if (!go) return;
            }
            string r = sup.RequestLanguage(v, "tray").ToString();
            if (Field(r, "status") != "ok")
            {
                Balloon(L.T("언어 설정을 저장하지 못했습니다. config.json을 확인해 주세요.", "Could not save the language setting. Check config.json."), ToolTipIcon.Warning);
                return;
            }
            string action = Field(r, "action");                                // unchanged · restarting · applied
            tm.ApplyText();
            tm.SetStatus(sup.TrayTitle, sup.Cables);
            if (action == "restarting") Balloon(L.T("언어를 바꿨습니다. 허브를 다시 켜는 중입니다.", "Language changed. The hub is restarting."), ToolTipIcon.Info);
            else if (action == "applied") Balloon(L.T("언어를 바꿨습니다.", "Language changed."), ToolTipIcon.Info);
            else Balloon(L.T("언어 설정을 저장했습니다.", "Language setting saved."), ToolTipIcon.Info);
        }

        // ================================================================== 백그라운드 작업
        void Job(Func<string> work, Action<string> done)
        {
            lock (jobs) jobs.Add((Task.Run(work), done));
        }

        void FinishJobs()
        {
            List<(Task<string> T, Action<string> Done)> ready;
            lock (jobs)
            {
                ready = jobs.Where(j => j.T.IsCompleted).ToList();
                foreach (var j in ready) jobs.Remove(j);
            }
            foreach (var j in ready) Safe(() => j.Done(j.T.IsCompletedSuccessfully ? j.T.Result : null));
        }

        /// <summary>코어 응답 줄 → (exit, error). 응답 없음·깨짐은 (-1, null).</summary>
        internal static (long Code, string Error) Parse(string line)
        {
            if (line == null) return (-1, null);
            try
            {
                using var d = JsonDocument.Parse(line);
                var r = d.RootElement;
                long code = r.TryGetProperty("exit", out var x) && x.ValueKind == JsonValueKind.Number ? x.GetInt64() : -1;
                string err = r.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                return (code, err);
            }
            catch (JsonException) { return (-1, null); }
        }
    }
}
