// /connect-uart · /disconnect-uart 흐름(계획 X5 부록 B.3, US-112). 감독(트레이 프로세스) 안에서 돈다 — CLI(`cli connect|disconnect`)는 감독 요청 스풀로
// 넘기고 응답을 찍을 뿐이며, 트레이 '케이블 설정' [연결]·[끊기]도 같은 흐름을 부른다. 상태 변경은 코어의 cable decide|include|exclude|baud(C-3·C-4)로만 한다.
//   connect COMx [baud]:
//     1. 코어 ls --detail(코어가 막 뜨는 중이면 최대 10초). 정지 상태면 103 stopped. COMx 가 없거나 분리됐으면 102 no_cable + 꽂힌 COM 목록. config ignore 면 106.
//        같은 COM 의 행이 여럿이면 분리되지 않은 행을 고른다.
//     2. 이미 attached(또는 bind_failed — 연결됨, telnet 포트 문제는 note)면 6 으로(보오를 바꾸면 4 의 보오 단계만).
//     3. 다른 프로그램이 쥐고 있으면(ComHolders — 알려진 시리얼 도구 먼저, 못 찾으면 같은 세션 전체: 사람이 부른 요청이라서):
//        - 점유자 하나이고 허용 목록(MoTTY·ttermpro·putty·kitty): 맨 앞 확인 창 [끊고 연결]/[취소] → [끊고 연결]이면 ComHolders.Terminate(시작 시각·이미지·세션·
//          아직 그 COM 을 쥠을 다시 본 뒤에만) → 그 프로세스가 COM 을 놓을 때까지 최대 5초. 다시 확인이 어긋나면 끝내지 않고, 다시 찾아 점유자가 없으면 계속·있으면 106.
//        - 그 밖(MobaXterm 본체·모르는 프로그램·여럿): 끝내지 않는다. 안내 창 [취소] + 2초마다 그 프로세스(와 후보)만 다시 확인, 최대 120초.
//        - [취소] → 106 status "cancelled", 시간 초과 → 100 "timeout" — 둘 다 아무것도 바꾸지 않는다.
//     4. 상태 변경: baud 를 주면(표준 값만, 아니면 106 + 허용 목록) cable baud <id> <rate>(메모리 + config.json cables.<id>.baud, 붙어 있으면 닫았다가 새 보오로
//        다시 엶 — .log "*** baud A -> B ***"). pending → cable decide <id> board(포트 배정), released → cable include <id>. 그 뒤 attached 를 최대 10초 기다린다.
//        held_by_other 는 COM 이 풀린 뒤 scan_interval_ms + 500ms 가 지나야 확정으로 본다(코어 스캔 전의 낡은 상태를 점유로 오인하지 않게).
//        점유자를 못 찾았는데 열리지 않으면(관리자 권한 프로그램 등) 안내 창 + 2초마다 확인(120초). 그 안내 창 취소·시간 초과, attach 시간 초과는 되돌림:
//        pending·released 였으면 cable exclude(끊김 = 연결 안 됨, 처음 보던 케이블이면 포트 번호는 배정된 채 남는다), 보오를 바꿨으면 원래 보오.
//        세션을 끝낸 뒤의 응답에는(취소·시간 초과 포함) 언제나 holder_closed 가 있다.
//     5. 터미널: open <포트> 와 같은 경로(OpenCmd.OpenTerminal — TeraTerm → PuTTY → MobaXterm, 이미 열린 창이면 앞으로). 안내 창은 띄우지 않는다.
//        띄운 터미널 pid 에만 AllowSetForegroundWindow(모르면 부르지 않음).
//     6. 2초 기다려 rx_since_attach(F2). 0 이면 hint no_output + "보드 출력이 아직 없습니다 …".
//     응답 {status, exit, com, port, baud, board, state, cable, rx_bytes, holder_closed:{name,pid,label,parent}|null, terminal:"launched"|"focused"|"none", …}.
//   connect(인자 없음) = 꽂힌 COM 마다 연결됨(포트·보드·보오) / 연결 안 됨 / <프로그램> 사용 중 표.
//   disconnect COMx = cable exclude(COM 닫음, 포트 유지, 바탕화면 아이콘은 다음 맞추기에서 사라짐). 이미 끊김·연결된 적 없음이면 ok + already.
//   확인 창: 트레이는 WinForms(app/ConnectForms.cs), 시험 모드(C-13)는 TestConnectUi(창 없음, <root>\fake_ui.json 자동 응답). 창을 띄울 곳이 없으면
//   (--no-tray 실제 모드) 점유자가 있을 때 106 — 확인 없이 끝내는 길은 없다.
//   요청자가 떠나면(CLI 의 <req>.cancel — 시간 초과·호출 쪽 종료) 확인 창·안내 창을 닫고 취소한다(requester_gone). 전체는 160초 안(CLI 는 175초 기다림).
//   시간(시험 모드만 바꿈): UART_HUB_TEST_CONNECT="확인창초,안내최대초,안내간격초,attach대기초,rx대기초"(기본 60,120,2,10,2).
//   동시에 하나만(다른 connect·disconnect 가 진행 중이면 106).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace UartHub
{
    /// <summary>connect 흐름의 창(트레이: WinForms, 시험: 자동 응답). 흐름의 백그라운드 스레드에서 불린다. 글은 UiText(UI_COPY.md "COM 연결 확인·안내").</summary>
    public interface IConnectUi
    {
        /// <summary>맨 앞 확인 창([t.Ok] [취소]). true = [t.Ok], false = [취소]·닫기·timeoutMs 초과·abort()(요청자가 떠남 — 창을 닫는다).</summary>
        bool Confirm(UiText t, int timeoutMs, Func<bool> abort);
        /// <summary>맨 앞 안내 창([취소] 하나 + 남은 시간, maxMs 부터 줄어듦). 흐름이 끝나면 Close. 사용자가 [취소]·닫기를 누르면 Cancelled.</summary>
        IConnectGuide Guide(UiText t, int maxMs);
    }

    public interface IConnectGuide
    {
        bool Cancelled { get; }
        void Close();
    }

    /// <summary>
    /// 시험 모드(C-13) 자동 응답: 창을 띄우지 않고 hub.log 에 "[ui:fake] …" 한 줄. 창마다 &lt;root&gt;\fake_ui.json 을 다시 읽는다
    /// {"confirm":"yes|cancel|none","confirm_delay_ms":N,"guide":"wait|cancel","guide_cancel_ms":N}(없으면 confirm yes·guide wait).
    /// </summary>
    public sealed class TestConnectUi : IConnectUi
    {
        readonly Action<string> log;
        public TestConnectUi(Action<string> log) { this.log = log; }

        static JsonElement? Spec() => HubFiles.ReadJson(SystemAccess.TestFile("fake_ui.json"));

        public bool Confirm(UiText t, int timeoutMs, Func<bool> abort)
        {
            var s = Spec();
            string ans = s.HasValue ? HubFiles.Str(s.Value, "confirm") ?? "yes" : "yes";
            int delay = s.HasValue ? (int)HubFiles.Long(s.Value, "confirm_delay_ms") : 0;
            string yes = t.Ok;
            log(L.T("[ui:fake] 확인 창: " + t.Title + " — " + t.Flat() + " [" + yes + "][" + t.Cancel + "]" + (t.OkIsDefault ? "" : " 기본=" + t.Cancel)
                + " (자동 응답=" + (ans == "yes" ? yes : ans == "none" ? "없음" : "취소") + (delay > 0 ? ", " + delay + "ms 뒤" : "") + ")",
                "[ui:fake] confirm window: " + t.Title + " — " + t.Flat() + " [" + yes + "][" + t.Cancel + "]" + (t.OkIsDefault ? "" : " default=" + t.Cancel)
                + " (auto reply=" + (ans == "yes" ? yes : ans == "none" ? "none" : "cancel") + (delay > 0 ? ", after " + delay + "ms" : "") + ")"));
            int wait = ans == "none" ? timeoutMs : Math.Min(delay, timeoutMs);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < wait)
            {
                if (abort != null && abort()) { log(L.T("[ui:fake] 확인 창 닫음(요청자가 떠남)", "[ui:fake] confirm window closed (requester left)")); return false; }
                Thread.Sleep(50);
            }
            if (ans == "none") { log(L.T("[ui:fake] 확인 창 시간 초과(" + timeoutMs + "ms) → 취소로 봄", "[ui:fake] confirm window timed out (" + timeoutMs + "ms) → treated as cancel")); return false; }
            return ans == "yes";
        }

        public IConnectGuide Guide(UiText t, int maxMs)
        {
            var s = Spec();
            string ans = s.HasValue ? HubFiles.Str(s.Value, "guide") ?? "wait" : "wait";
            long ms = s.HasValue ? HubFiles.Long(s.Value, "guide_cancel_ms", 500) : 500;
            log(L.T("[ui:fake] 안내 창: " + t.Title + " — " + t.Flat() + " / 남은 시간 " + ConnectFlow.Clock(maxMs) + " [" + t.Cancel + "] (자동 응답=" + (ans == "cancel" ? "취소(" + ms + "ms 뒤)" : "기다림") + ")",
                "[ui:fake] guide window: " + t.Title + " — " + t.Flat() + " / time left " + ConnectFlow.Clock(maxMs) + " [" + t.Cancel + "] (auto reply=" + (ans == "cancel" ? "cancel (after " + ms + "ms)" : "wait") + ")"));
            return new G(ans == "cancel" ? ms : -1, log);
        }

        sealed class G : IConnectGuide
        {
            readonly Stopwatch sw = Stopwatch.StartNew();
            readonly long cancelMs;
            readonly Action<string> log;
            public G(long cancelMs, Action<string> log) { this.cancelMs = cancelMs; this.log = log; }
            public bool Cancelled => cancelMs >= 0 && sw.ElapsedMilliseconds >= cancelMs;
            public void Close() => log(L.T("[ui:fake] 안내 창 닫음", "[ui:fake] guide window closed"));
        }
    }

    public sealed class ConnectFlow
    {
        /// <summary>창 제목 "UART 허브 · COMx 연결"(UI_COPY.md).</summary>
        public static string Title(string com) => L.T("UART 허브 · " + com + " 연결", "UART Hub · " + com + " connection");
        /// <summary>남은 시간 표시 "m:ss".</summary>
        public static string Clock(long ms)
        {
            long s = Math.Max(0, (ms + 999) / 1000);
            return (s / 60).ToString(CultureInfo.InvariantCulture) + ":" + (s % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 확인 창 글(허용 목록의 점유자 하나 — 끝낼 수 있음): "COM8 사용 중: MobaXterm 세션" · 세션을 끊고 연결할까요? · 작은 줄 "motty.exe · pid 88688".
        /// Enter·Esc = [취소](OkIsDefault false — 세션을 끝내는 쪽은 기본이 아니다).
        /// </summary>
        public static UiText ConfirmText(string com, ComHolder h)
        {
            bool motty = string.Equals(h.Name, "MoTTY.exe", StringComparison.OrdinalIgnoreCase);
            return new UiText
            {
                Title = Title(com),
                Head = L.T(com + " 사용 중: " + h.Label, com + " in use: " + h.Label),
                Body = L.T((motty ? "이 세션을 끊고" : h.Label + " 창을 닫고") + ", 허브가 " + Josa.EulReul(com) + " 열어 터미널과 Claude가 같이 쓰게 할까요?"
                       + (motty ? " MobaXterm의 다른 탭은 그대로입니다." : ""),
                       (motty ? "Disconnect this session" : "Close the " + h.Label + " window") + " and let the hub open " + com + " so the terminal and Claude can share it?"
                       + (motty ? " Other MobaXterm tabs stay as they are." : "")),
                Note = HolderNote(h),
                Ok = L.T("끊고 연결", "Disconnect and connect"), OkIsDefault = false,
            };
        }

        /// <summary>안내 창 글(끝낼 수 없는 프로그램·모르는 점유자): "COM8 사용 중: &lt;이름&gt;" · 그 프로그램에서 끊어 주세요 · 남은 시간(창이 센다).</summary>
        public static UiText GuideText(string com, ComHolder h)
        {
            return new UiText
            {
                Title = Title(com),
                Head = L.T(com + " 사용 중: " + (h?.Label ?? "다른 프로그램"), com + " in use: " + (h?.Label ?? "another program")),
                Body = L.T("그 프로그램에서 " + com + " 연결을 끊어 주세요. 끊으면 바로 이어서 연결합니다."
                       + (h == null ? "\n어느 프로그램인지 찾지 못했습니다. 관리자 권한으로 실행한 프로그램일 수 있습니다." : ""),
                       "Please disconnect " + com + " in that program. The hub will connect right after you do."
                       + (h == null ? "\nCould not tell which program it is. It may be a program running as administrator." : "")),
                Note = h != null ? HolderNote(h) : null,
            };
        }

        static string HolderNote(ComHolder h) => (h.Name ?? "?").ToLowerInvariant() + " · pid " + h.Pid.ToString(CultureInfo.InvariantCulture);
        static int running;                                                        // connect·disconnect 는 동시에 하나만
        static readonly HashSet<string> active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        readonly string hubDir;
        readonly int controlPort, corePid, scanIntervalMs;
        readonly List<string> skip;
        readonly Action<string> log;
        readonly IConnectUi ui;
        readonly Func<bool> gone;
        readonly Stopwatch flow = new Stopwatch();

        static double T(int i, double dflt) => SystemAccess.TestNumber("UART_HUB_TEST_CONNECT", i, dflt);
        static int ConfirmMs => (int)(T(0, 60) * 1000);
        static int GuideMaxMs => (int)(T(1, 120) * 1000);
        static int GuideEveryMs => (int)(T(2, 2) * 1000);
        static int AttachMs => (int)(T(3, 10) * 1000);
        static int RxWaitMs => (int)(T(4, 2) * 1000);
        const int ReleaseMs = 5000;
        /// <summary>흐름 전체 상한(CLI 는 요청을 쓴 뒤 175초 기다린다 — 터미널·rx 몫 15초를 남긴다).</summary>
        const int BudgetMs = 160000, TailMs = 15000;

        /// <param name="controlPort">코어 제어 포트(요청을 받을 때 감독 스레드에서 읽은 값).</param>
        /// <param name="corePid">허브 코어 pid(점유자에서 뺀다, 0 = 없음).</param>
        /// <param name="scanIntervalMs">코어 스캔 주기(config scan_interval_ms) — held_by_other 확정 대기에 쓴다.</param>
        /// <param name="skip">config.json holder_scan_skip.</param>
        /// <param name="requesterGone">요청자가 떠났는가(CLI 의 &lt;req&gt;.cancel). null = 트레이(늘 false).</param>
        public ConnectFlow(string hubDir, int controlPort, int corePid, int scanIntervalMs, IEnumerable<string> skip, Action<string> log, IConnectUi ui, Func<bool> requesterGone = null)
        {
            this.hubDir = hubDir; this.controlPort = controlPort; this.corePid = corePid; this.scanIntervalMs = Math.Max(50, scanIntervalMs);
            this.skip = (skip ?? new string[0]).ToList(); this.log = log; this.ui = ui; gone = requesterGone ?? (() => false);
        }

        /// <summary>그 COM 의 connect 가 진행 중인가(F1 풍선을 잠시 막는다).</summary>
        public static bool IsActive(string com) { lock (active) return com != null && active.Contains(com); }

        // ================================================================== 진입점
        public JObj Connect(string com, string from, int baud = 0)
        {
            if (baud != 0 && !Config.IsStandardBaud(baud)) return Err(106, "rejected", L.T("baud 는 " + Config.StandardBaudText + " 중 하나: " + baud, "baud must be one of " + Config.StandardBaudText + ": " + baud)).Set("com", com).Set("allowed_bauds", Config.StandardBauds.Select(x => (long)x));
            return Exclusive(com, c => DoConnect(c, from, baud));
        }
        public JObj Disconnect(string com, string from) => Exclusive(com, c => DoDisconnect(c, from));

        JObj Exclusive(string com, Func<string, JObj> work)
        {
            com = (com ?? "").Trim().ToUpperInvariant();
            if (!System.Text.RegularExpressions.Regex.IsMatch(com, @"^COM\d+$")) return Err(106, "rejected", L.T("COM 이름이 아님: '" + com + "' (예: COM8)", "Not a COM name: '" + com + "' (e.g. COM8)"));
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0) return Err(106, "rejected", L.T("다른 연결·끊기 작업이 진행 중 — 끝난 뒤 다시", "Another connect or disconnect is in progress — try again after it finishes")).Set("com", com);
            lock (active) active.Add(com);
            flow.Restart();
            try { return work(com); }
            finally
            {
                lock (active) active.Remove(com);
                Interlocked.Exchange(ref running, 0);
            }
        }

        static JObj Err(long code, string status, string msg) => new JObj().Set("status", status).Set("exit", code).Set("error", msg);

        int[] Exclude() => new[] { Environment.ProcessId, corePid }.Where(p => p > 0).ToArray();

        /// <summary>흐름 상한 안에서 쓸 수 있는 대기(ms): want 와 (남은 시간 − 뒤 단계 몫) 중 작은 값, 최소 1초.</summary>
        int Cap(int want) => (int)Math.Max(1000, Math.Min(want, BudgetMs - TailMs - flow.ElapsedMilliseconds));

        static bool IsOn(CableRow r) => r != null && (r.Status == "attached" || r.Status == "bind_failed");

        /// <summary>COM 이 같은 행이 여럿이면(뽑았다 다른 케이블이 같은 COM) 분리되지 않은 행.</summary>
        static CableRow Pick(IEnumerable<CableRow> rows, string com)
            => rows?.Where(r => string.Equals(r.Com, com, StringComparison.OrdinalIgnoreCase)).OrderBy(r => r.Status == "detached" ? 1 : 0).FirstOrDefault();

        // ================================================================== 코어
        string Call(params string[] argv)
            => ControlClient.CallWithTokenFile(controlPort, Path.Combine(hubDir, "token"), 5000, "[" + string.Join(",", argv.Select(Json.Str)) + "]");

        List<CableRow> Ls()
        {
            string l = Call("ls", "--detail");
            if (l == null) return null;
            try
            {
                using var d = JsonDocument.Parse(l);
                return d.RootElement.TryGetProperty("cables", out var cs) && cs.ValueKind == JsonValueKind.Array ? cs.EnumerateArray().Select(CableRow.From).ToList() : null;
            }
            catch (JsonException) { return null; }
        }

        /// <summary>코어가 응답할 때까지 최대 ms(감독이 막 띄운 코어).</summary>
        List<CableRow> LsWait(int ms)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                var r = Ls();
                if (r != null || sw.ElapsedMilliseconds >= ms) return r;
                Thread.Sleep(300);
            }
        }

        CableRow Row(string com) => Pick(Ls(), com);

        /// <summary>코어 cable 명령. (exit, 오류 문구). 코어에 닿지 못하면 (-1, …).</summary>
        (long Code, string Error) Cable(params string[] argv)
        {
            string l = Call(new[] { "cable" }.Concat(argv).ToArray());
            log("connect: cable " + string.Join(" ", argv) + " → " + (l == null ? L.T("코어 응답 없음", "no core reply") : l.Length > 200 ? l.Substring(0, 200) + "…" : l));
            if (l == null) return (-1, L.T("허브 코어 응답 없음", "No reply from the hub core"));
            try
            {
                using var d = JsonDocument.Parse(l);
                return (HubFiles.Long(d.RootElement, "exit", -1), HubFiles.Str(d.RootElement, "error"));
            }
            catch (JsonException) { return (-1, L.T("코어 응답을 해석할 수 없음", "Cannot parse the core reply")); }
        }

        JObj NoHub()
        {
            if (HubFiles.IsStopped(hubDir)) return Err(103, "hub_down", L.T("허브 정지됨(트레이 → 시작 또는 'uart hub start' 뒤 다시)", "Hub stopped (tray → Start, or run 'uart hub start', then try again)")).Set("stopped", true);
            return Err(103, "hub_down", L.T("허브 코어가 응답하지 않음(재시작 중일 수 있음 — 잠시 뒤 다시)", "The hub core is not responding (it may be restarting — try again shortly)"));
        }

        static IEnumerable<JObj> ComList(IEnumerable<CableRow> rows)
            => rows.Where(r => r.Com != null && r.Status != "detached").Select(r => new JObj().Set("com", r.Com).Set("status", r.Status).Set("port", (long)r.Port).Set("board", r.Board));

        // ================================================================== connect
        JObj DoConnect(string com, string from, int baud)
        {
            log(L.T("connect " + com + (baud > 0 ? " " + baud : "") + " 시작(" + from + ")", "connect " + com + (baud > 0 ? " " + baud : "") + " started (" + from + ")"));
            ComHolder closed = null;
            CableRow row = null;
            JObj Fin(JObj r)
            {
                r.Set("com", com);
                if (row?.Cable != null) r.Set("cable", row.Cable);
                if (closed != null) r.Set("holder_closed", closed.ToJObj());     // 세션을 끝냈으면 어떤 결과든 알린다
                string s = r.ToString();
                log(L.T("connect " + com + " 끝 → ", "connect " + com + " done → ") + (s.Length > 300 ? s.Substring(0, 300) + "…" : s));
                return r;
            }

            var rows = LsWait(10000);
            if (rows == null) return Fin(NoHub());
            row = Pick(rows, com);
            if (row != null && row.Status == "detached")
            {
                Thread.Sleep(2500);                                           // 해제(include) 직후 한 스캔 동안의 detached 와 뽑힌 케이블을 가린다
                rows = Ls() ?? rows;
                row = Pick(rows, com);
            }
            if (row == null || row.Status == "detached")
                return Fin(Err(102, "no_cable", L.T(com + (row == null ? " 이(가) 허브가 보는 케이블 목록에 없음" : " 케이블이 분리됨") + "(꽂혀 있는지·FTDI 케이블인지 확인)", com + (row == null ? " is not in the cable list the hub sees" : " cable was unplugged") + " (check that it is plugged in and is an FTDI cable)")).Set("coms", ComList(rows)));
            if (row.ModeExplicit == "ignore")
                return Fin(Err(106, "rejected", L.T("config.json 이 제외함(cables." + row.Cable + ".mode = ignore) — 연결할 수 없음", "Excluded by config.json (cables." + row.Cable + ".mode = ignore) — cannot connect")));
            bool already = IsOn(row);
            int oldBaud = row.Baud;
            bool rebaud = baud > 0 && baud != row.Baud;
            string before = row.Status;
            if (!already)
            {
                var stop = ClearHolders(com, ref closed);
                if (stop != null) return Fin(stop);
            }
            if (rebaud)
            {
                // 점유자를 비운 뒤(취소면 여기까지 오지 않음): 메모리 설정·config.json 저장, 붙어 있으면 닫았다가 새 보오로 다시 엶
                var (c, e) = Cable("baud", row.Cable, baud.ToString(CultureInfo.InvariantCulture));
                if (c != 0) return Fin(Err(c < 0 ? 103 : c, c < 0 ? "hub_down" : "rejected", L.T("보오 바꾸기 실패(cable baud): " + e, "Changing the baud rate failed (cable baud): " + e)));
            }
            if (!already || rebaud)
            {
                bool decided = false;
                if (before == "pending")
                {
                    var (c, e) = Cable("decide", row.Cable, "board");
                    if (c != 0) { Revert(row.Cable, false, rebaud, oldBaud, com); return Fin(Err(c < 0 ? 103 : c, c < 0 ? "hub_down" : "rejected", L.T("연결 실패(cable decide): " + e, "Connect failed (cable decide): " + e))); }
                    decided = true;
                }
                else if (before == "released")
                {
                    var (c, e) = Cable("include", row.Cable);
                    if (c != 0) { Revert(row.Cable, false, rebaud, oldBaud, com); return Fin(Err(c < 0 ? 103 : c, c < 0 ? "hub_down" : "rejected", L.T("연결 실패(cable include): " + e, "Connect failed (cable include): " + e))); }
                    decided = true;
                }
                var got = WaitAttached(com, out var last);
                if (got == null && last?.Status == "held_by_other")
                {
                    log(L.T("connect " + com + ": 점유자를 찾지 못했는데 열리지 않음(held_by_other) → 안내 창", "connect " + com + ": no holder found but the port will not open (held_by_other) → guide window"));
                    var stop2 = GuideWait(com, null, () => IsOn(Row(com)));
                    if (stop2 != null) { Revert(row.Cable, decided, rebaud, oldBaud, com); return Fin(stop2.Set("reverted", true)); }
                    got = Row(com);
                }
                if (!IsOn(got))
                {
                    Revert(row.Cable, decided, rebaud, oldBaud, com);
                    return Fin(Err(100, "timeout", L.T(com + " 을 " + (AttachMs / 1000) + "초 안에 열지 못함(" + (last?.Status ?? "목록에 없음") + ") — 연결 전 상태로 되돌림", "Could not open " + com + " within " + (AttachMs / 1000) + " s (" + (last?.Status ?? "not in the list") + ") — reverted to the state before connecting")).Set("reverted", true));
                }
                row = got;
            }

            // 터미널(open <포트> 와 같은 경로). 띄운 터미널에만 전면 권한(확인 창을 누른 뒤라면 감독에 전면 권한이 있다)
            string term = OpenCmd.OpenTerminal(hubDir, row.Port, row.Board, row.Com, false, s => log("connect " + com + ": " + s), s => log(L.T("connect " + com + ": 터미널 " + s, "connect " + com + ": terminal " + s)),
                                               pid => { if (pid > 0) SystemAccess.AllowForeground(pid); });
            Thread.Sleep(RxWaitMs);
            var now = Pick(Ls(), com) ?? row;
            long rx = now.RxSinceAttach < 0 ? 0 : now.RxSinceAttach;
            var o = new JObj().Set("status", "ok").Set("exit", 0L).Set("com", com).Set("port", (long)now.Port).Set("baud", (long)now.Baud).Set("board", now.Board).Set("state", now.State)
                .Set("cable", now.Cable).Set("rx_bytes", rx).Set("holder_closed", closed?.ToJObj()).Set("terminal", term);
            if (rebaud) o.Set("old_baud", (long)oldBaud);
            if (already) o.Set("already", true);
            if (now.Status == "bind_failed") o.Set("note", L.T("연결됨 — 그러나 telnet 포트 " + now.Port + " 를 열지 못함(bind_failed, 다른 프로그램이 그 포트를 씀) — 터미널이 붙지 않을 수 있음", "Connected — but telnet port " + now.Port + " could not be opened (bind_failed, another program is using it) — the terminal may not attach"));
            if (term == "none") o.Set("terminal_note", L.T("터미널(TeraTerm·PuTTY·MobaXterm)을 찾지 못함 — 127.0.0.1:" + now.Port + " 로 telnet 접속", "No terminal program (TeraTerm, PuTTY, MobaXterm) found — connect with telnet to 127.0.0.1:" + now.Port));
            if (rx == 0) o.Set("hint", "no_output").Set("message", L.T("보드 출력이 아직 없습니다 — 전원·배선 확인 뒤 터미널에서 Enter", "No board output yet — check power and wiring, then press Enter in the terminal"));
            return Fin(o);
        }

        /// <summary>연결하다 멈춘 것을 되돌린다: 이번에 decide·include 했으면 exclude(끊김), 보오를 바꿨으면 원래 보오.</summary>
        void Revert(string cable, bool decided, bool rebaud, int oldBaud, string com)
        {
            if (decided)
            {
                var (c, _) = Cable("exclude", cable);
                log(L.T("connect " + com + ": 되돌림 cable exclude → " + (c == 0 ? "끊김(released)" : "실패 exit=" + c), "connect " + com + ": revert cable exclude → " + (c == 0 ? "released" : "failed exit=" + c)));
            }
            if (rebaud)
            {
                var (c, _) = Cable("baud", cable, oldBaud.ToString(CultureInfo.InvariantCulture));
                log(L.T("connect " + com + ": 되돌림 cable baud " + oldBaud + " → " + (c == 0 ? "ok" : "실패 exit=" + c), "connect " + com + ": revert cable baud " + oldBaud + " → " + (c == 0 ? "ok" : "failed exit=" + c)));
            }
        }

        JObj Cancelled(ComHolder h)
        {
            bool g = gone();
            var r = Err(106, "cancelled", g ? L.T("요청자가 떠남(CLI 시간 초과·종료) — 창을 닫고 아무것도 바꾸지 않음", "The requester left (CLI timeout or exit) — window closed, nothing changed") : L.T("사용자가 취소 — 아무것도 바꾸지 않음", "Cancelled by the user — nothing changed")).Set("holder", h?.ToJObj());
            return g ? r.Set("requester_gone", true) : r;
        }

        /// <summary>
        /// 점유자 처리. null = COM 이 비었음(계속), 아니면 끝낼 응답(취소·시간 초과·거부). closed = 끝낸 세션(없으면 그대로).
        /// </summary>
        JObj ClearHolders(string com, ref ComHolder closed)
        {
            var ex = Exclude();
            var scan = ComHolders.Scan(new[] { com }, ex, HolderScanMode.FullIfNone, skip);
            var hs = scan.Of(com);
            log(L.T("connect " + com + ": 점유자 " + (hs.Count == 0 ? "없음" : string.Join(", ", hs.Select(h => h.ToString()))) + " (찾기 " + scan.Mode + ", " + scan.Ms + "ms" + (scan.Note != null ? ", " + scan.Note : "") + ")",
                "connect " + com + ": holder " + (hs.Count == 0 ? "none" : string.Join(", ", hs.Select(h => h.ToString()))) + " (scan " + scan.Mode + ", " + scan.Ms + "ms" + (scan.Note != null ? ", " + scan.Note : "") + ")"));
            if (hs.Count == 0) return null;
            if (hs.Count == 1 && hs[0].Allowed)
            {
                var h = hs[0];
                if (ui == null) return Err(106, "rejected", L.T(com + " 을 " + h + " 이 쓰고 있음 — 확인 창을 띄울 수 없는 감독(트레이 없음)이라 끝내지 않음. 그 세션을 직접 끊은 뒤 다시", h + " is using " + com + " — this supervisor cannot show a confirm window (no tray), so it was not closed. Disconnect that session yourself, then try again")).Set("holder", h.ToJObj());
                bool yes = ui.Confirm(ConfirmText(com, h), Cap(ConfirmMs), gone);
                log(L.T("connect " + com + ": 확인 창 → " + (yes ? "끊고 연결" : gone() ? "닫음(요청자가 떠남)" : "취소"), "connect " + com + ": confirm window → " + (yes ? "Disconnect and connect" : gone() ? "closed (requester left)" : "cancel")));
                if (!yes || gone()) return Cancelled(h);
                string why = ComHolders.Terminate(h, ex);
                if (why != null)
                {
                    log(L.T("connect " + com + ": 끝내지 않음 — " + why, "connect " + com + ": not terminated — " + why));
                    var again = ComHolders.Find(com, ex, HolderScanMode.FullIfNone, skip);
                    if (again.Count == 0) { log(L.T("connect " + com + ": 다시 찾음 → 점유자 없음(스스로 놓음) — 계속", "connect " + com + ": rescanned → no holder (it let go on its own) — continuing")); return null; }
                    bool same = again.Any(x => x.Pid == h.Pid && x.StartTicks == h.StartTicks && string.Equals(x.Name, h.Name, StringComparison.OrdinalIgnoreCase));
                    return Err(106, "rejected", L.T((same ? h + " 을 끝내지 못함(" + why + ")" : "확인하는 사이 " + com + " 을 쥔 프로그램이 바뀜(" + why + ")")
                                                + " — 아무것도 끝내지 않음. 그 프로그램에서 직접 끊거나 다시 /connect-uart " + com,
                                                (same ? "Could not close " + h + " (" + why + ")" : "The program holding " + com + " changed while confirming (" + why + ")")
                                                + " — nothing was closed. Disconnect from that program yourself, or run /connect-uart " + com + " again")).Set("holder", again[0].ToJObj());
                }
                closed = h;
                log(L.T("connect " + com + ": " + h + " 끝냄(TerminateProcess)", "connect " + com + ": " + h + " terminated (TerminateProcess)"));
                var sw = Stopwatch.StartNew();
                while (ComHolders.StillHolds(h, ex, skip))
                {
                    if (sw.ElapsedMilliseconds >= ReleaseMs) return Err(100, "timeout", L.T(h + " 을 끝냈지만 " + com + " 이 " + (ReleaseMs / 1000) + "초 안에 풀리지 않음", "Closed " + h + " but " + com + " was not released within " + (ReleaseMs / 1000) + " s"));
                    Thread.Sleep(200);
                }
                return null;
            }
            var first = hs[0];
            return GuideWait(com, first, () => !ComHolders.StillHolds(first, ex, skip) && ComHolders.Find(com, ex, HolderScanMode.Candidates, skip).Count == 0);
        }

        /// <summary>안내 창 + GuideEveryMs 마다 released() 확인, 최대 GuideMaxMs(흐름 상한 안). null = 풀림(계속), 아니면 취소·요청자 떠남(106)·시간 초과(100)·창 없음(106).</summary>
        JObj GuideWait(string com, ComHolder h, Func<bool> released)
        {
            string who = h != null ? h.Label + L.T("(pid ", " (pid ") + h.Pid + ")" : L.T("다른 프로그램", "another program");
            if (ui == null) return Err(106, "rejected", L.T(com + " 을 " + who + " 이 쓰고 있음 — 안내 창을 띄울 수 없는 감독(트레이 없음). 그 프로그램에서 연결을 끊은 뒤 다시", who + " is using " + com + " — this supervisor cannot show a guide window (no tray). Disconnect from that program, then try again")).Set("holder", h?.ToJObj());
            int max = Cap(GuideMaxMs);
            var g = ui.Guide(GuideText(com, h), max);
            var sw = Stopwatch.StartNew();
            long nextCheck = 0;
            try
            {
                while (true)
                {
                    if (g.Cancelled || gone()) { log(L.T("connect " + com + ": 안내 창 → " + (gone() ? "닫음(요청자가 떠남)" : "취소"), "connect " + com + ": guide window → " + (gone() ? "closed (requester left)" : "cancel"))); return Cancelled(h); }
                    if (sw.ElapsedMilliseconds >= nextCheck)
                    {
                        if (released()) { log(L.T("connect " + com + ": " + who + " 이 놓음(" + sw.ElapsedMilliseconds + "ms) — 계속", "connect " + com + ": " + who + " let go (" + sw.ElapsedMilliseconds + "ms) — continuing")); return null; }
                        nextCheck = sw.ElapsedMilliseconds + GuideEveryMs;
                    }
                    if (sw.ElapsedMilliseconds >= max)
                    {
                        log(L.T("connect " + com + ": 안내 창 시간 초과(" + (max / 1000) + "초)", "connect " + com + ": guide window timed out (" + (max / 1000) + " s)"));
                        return Err(100, "timeout", L.T(com + " 을 " + who + " 이 " + (max / 1000) + "초 동안 놓지 않음 — 아무것도 바꾸지 않음", who + " did not release " + com + " for " + (max / 1000) + " s — nothing changed")).Set("holder", h?.ToJObj());
                    }
                    Thread.Sleep(100);
                }
            }
            finally { g.Close(); }
        }

        /// <summary>
        /// attached(또는 bind_failed)가 될 때까지 최대 AttachMs. held_by_other 는 이 대기를 시작한 뒤(= COM 이 풀리고 상태를 바꾼 뒤) scan_interval_ms + 500ms 가
        /// 지나야 확정으로 본다 — 그 전의 held_by_other 는 코어가 아직 다시 스캔하지 않은 낡은 상태일 수 있다. last = 마지막 행.
        /// </summary>
        CableRow WaitAttached(string com, out CableRow last)
        {
            last = null;
            int settle = scanIntervalMs + 500, max = Cap(AttachMs);
            var sw = Stopwatch.StartNew();
            while (true)
            {
                var r = Row(com);
                if (r != null) last = r;
                if (IsOn(r)) return r;
                if (r != null && r.Status == "held_by_other" && sw.ElapsedMilliseconds >= settle) return null;
                if (sw.ElapsedMilliseconds >= max) return null;
                Thread.Sleep(200);
            }
        }

        // ================================================================== disconnect
        JObj DoDisconnect(string com, string from)
        {
            log(L.T("disconnect " + com + " 시작(" + from + ")", "disconnect " + com + " started (" + from + ")"));
            var rows = LsWait(5000);
            JObj Done(JObj r) { string s = r.ToString(); log(L.T("disconnect " + com + " 끝 → ", "disconnect " + com + " done → ") + (s.Length > 300 ? s.Substring(0, 300) + "…" : s)); return r; }
            if (rows == null) return Done(NoHub().Set("com", com));
            var row = Pick(rows, com);
            if (row == null) return Done(Err(102, "no_cable", L.T(com + " 이(가) 허브가 보는 케이블 목록에 없음", com + " is not in the cable list the hub sees")).Set("com", com).Set("coms", ComList(rows)));
            if (row.ModeExplicit == "ignore") return Done(Err(106, "rejected", L.T("config.json 이 제외함(mode: ignore) — 허브가 이미 쓰지 않음", "Excluded by config.json (mode: ignore) — the hub is already not using it")).Set("com", com).Set("cable", row.Cable));
            var o = new JObj().Set("status", "ok").Set("exit", 0L).Set("com", com).Set("port", (long)row.Port).Set("board", row.Board).Set("cable", row.Cable);
            if (row.Status == "released" || row.Status == "pending")
                return Done(o.Set("cable_status", row.Status).Set("already", true).Set("message", row.Status == "pending" ? L.T("연결된 적 없음 — 허브가 이 COM 을 열지 않음", "Never connected — the hub does not open this COM port") : L.T("이미 끊김", "Already disconnected")));
            var (c, e) = Cable("exclude", row.Cable);
            if (c != 0) return Done(Err(c < 0 ? 103 : c, c < 0 ? "hub_down" : "rejected", L.T("끊기 실패(cable exclude): " + e, "Disconnect failed (cable exclude): " + e)).Set("com", com).Set("cable", row.Cable));
            return Done(o.Set("cable_status", "released").Set("message", L.T("허브가 " + com + " 을 닫음(포트 " + row.Port + " 유지) — 다른 프로그램이 쓸 수 있음", "The hub closed " + com + " (port " + row.Port + " kept) — other programs can use it now")));
        }

        // ================================================================== 표(인자 없는 connect)
        public JObj Table()
        {
            var rows = LsWait(5000);
            if (rows == null) return NoHub();
            var show = rows.Where(x => x.Com != null).GroupBy(x => x.Com, StringComparer.OrdinalIgnoreCase).Select(g => Pick(g, g.Key)).Where(x => x.Status != "detached").ToList();
            var open = show.Where(r => !IsOn(r)).Select(r => r.Com).ToList();
            var scan = ComHolders.Scan(open, Exclude(), HolderScanMode.FullIfNone, skip);
            var list = new List<JObj>();
            foreach (var r in show.OrderBy(x => x.Com, StringComparer.OrdinalIgnoreCase))
            {
                var h = IsOn(r) ? null : scan.Of(r.Com).FirstOrDefault();
                string st = r.ModeExplicit == "ignore" ? "ignored" : IsOn(r) ? "connected" : h != null || r.Status == "held_by_other" ? "in_use" : "not_connected";
                string text = L.T(st == "connected" ? "연결됨 " + r.Port + (r.Board != null ? " " + r.Board : "") + (r.Status == "bind_failed" ? "(telnet 포트 문제)" : "")
                            : st == "in_use" ? (h != null ? h.Label + "(pid " + h.Pid + ")" : "다른 프로그램") + " 사용 중"
                            : st == "ignored" ? "설정 파일이 제외함" : "연결 안 됨",
                            st == "connected" ? "Connected " + r.Port + (r.Board != null ? " " + r.Board : "") + (r.Status == "bind_failed" ? " (telnet port problem)" : "")
                            : st == "in_use" ? "In use by " + (h != null ? h.Label + " (pid " + h.Pid + ")" : "another program")
                            : st == "ignored" ? "Excluded by the config file" : "Not connected") + " · " + r.Baud.ToString(CultureInfo.InvariantCulture);
                list.Add(new JObj().Set("com", r.Com).Set("connected", st == "connected").Set("state", st).Set("text", text).Set("port", (long)r.Port).Set("baud", (long)r.Baud).Set("board", r.Board)
                                   .Set("cable", r.Cable).Set("hub_status", r.Status).Set("holder", h?.ToJObj()));
            }
            var o = new JObj().Set("status", "ok").Set("exit", 0L).Set("cables", list).Set("holder_scan", scan.Mode);
            if (scan.Note != null) o.Set("holder_note", scan.Note);
            log(L.T("connect(표) " + list.Count + "개, 점유자 찾기 " + scan.Mode + " " + scan.Ms + "ms", "connect (table) " + list.Count + " row(s), holder scan " + scan.Mode + " " + scan.Ms + "ms"));
            return o;
        }
    }
}
