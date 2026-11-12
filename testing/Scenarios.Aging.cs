// 에이징 세션(부록 C.12) 시험 — 새 시나리오를 등록하지 않고(--part 나눔이 바뀌지 않게) 기존 시나리오가 부른다:
//   Units  ← logroot-policy(2/3): log_layout 3 경로·임시 기록 보관 정리(닫힌 옛 파일 지움·지금 파일 둠·합계 상한)·이름 다듬기·글(경과·마지막 출력)·
//            트레이 아이콘 우선순위·툴팁·터미널 제목 " · 에이징 중"(가짜 창으로 시작·끝 다시 쓰기)·요약 글
//   InHub  ← events-hub(2/3): Hub + FakeBoard, log_layout 3 — 세션 밖 감지 없음, session start·status·stop(제어 명령), 세션 폴더(콘솔.log 줄머리·이벤트 txt·요약.txt),
//            코어 다시 시작 뒤 이어 감(끊김 표시), 연결 안 된 포트 거부(/connect-uart 안내), 끝낸 뒤 감지 없음
//   Cli    ← events-cli(3/3): 실제 exe 의 cli session start·status(--json·한 줄)·summary(= aging)·stop, 연결 안 된 COM 거부 102
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
    public static class AgingScenarios
    {
        static readonly Regex ConsoleLineRe = new Regex(@"^\[\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} \+\s*\d+\.\d{3}\] ", RegexOptions.CultureInvariant);

        sealed class Wins : ITermWindows
        {
            public readonly Dictionary<IntPtr, (int Pid, string Title)> W = new Dictionary<IntPtr, (int, string)>();
            public bool Alive(int pid, long startTicks) => true;
            public List<(IntPtr Handle, string Title)> Windows(int pid) => W.Where(w => w.Value.Pid == pid).Select(w => (w.Key, w.Value.Title)).ToList();
            public bool SetTitle(IntPtr h, string title) { W[h] = (W[h].Pid, title); return true; }
        }

        static void Touch(string p, string text, DateTime when)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, text);
            File.SetLastWriteTime(p, when);
        }

        // ================================================================== 단위(logroot-policy)
        public static void Units(ScenarioContext ctx)
        {
            // ---- log_layout 3 경로
            var c = Config.Parse("{\"log_root\": \"D:\\\\L\", \"log_layout\": 3, \"spool_keep_hours\": 12, \"spool_max_mb\": 512}");
            c.HubDir = @"D:\L\hub"; c.ResolveLogRoot(@"D:\L\hub"); c.ResolveLogRoot(@"D:\L\hub");
            var c2 = Config.Parse("{\"log_root\": \"D:\\\\L\", \"log_layout\": 3}"); c2.HubDir = @"E:\H"; c2.ResolveLogRoot(@"E:\H");
            ctx.Check(c.LogRoot == @"D:\L\hub\임시기록" && c.BaseRoot == @"D:\L" && c.AgingRoot == @"D:\L\에이징" && c.EventsTxtDir == @"D:\L\에이징" && c.EventsJsonlDir == @"D:\L\hub\events"
                      && c.EffectiveHubDir == @"D:\L\hub" && c.SpoolKeepHours == 12 && c.SpoolMaxMb == 512 && c2.LogRoot == @"E:\H\임시기록" && c2.AgingRoot == @"D:\L\에이징"
                      && Config.Default().SpoolKeepHours == 24 && Config.Default().SpoolMaxMb == 2048 && Config.Parse("{\"spool_keep_hours\": 0}").Validate().Any(e => e.Contains("spool_keep_hours")),
                      "C.12 경로: log_layout 3 = 콘솔 기록 <허브 폴더>\\임시기록(hub_dir 따로면 그 아래), 세션 <기준>\\에이징, jsonl <허브 폴더>\\events, 두 번 풀어도 같음, spool_keep_hours·spool_max_mb(기본 24·2048, 0 은 오류)");

            // ---- 임시 기록 보관 정리
            string sp = Path.Combine(ctx.Dir, "c12spool", "임시기록");
            var now = DateTime.Now;
            string d1 = Path.Combine(sp, now.AddDays(-2).ToString("yyyyMMdd", CultureInfo.InvariantCulture)), d2 = Path.Combine(sp, now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            foreach (var x in new[] { ".log", ".raw", ".idx" }) Touch(Path.Combine(d1, "7001_OLD_101010" + x), "old", now.AddHours(-30));
            Touch(Path.Combine(d1, "7002_CUR_101010.log"), "current but old mtime", now.AddHours(-30));
            Touch(Path.Combine(d2, "7001_NEW_120000.log"), new string('n', 1000), now.AddHours(-2));
            Touch(Path.Combine(d2, "7001_NEW_120000.raw"), new string('r', 3000), now.AddHours(-2));
            Touch(Path.Combine(d2, "7001_MID_110000.log"), new string('m', 500), now.AddHours(-1));
            Touch(Path.Combine(d2, "7001_FRESH_130000.log"), new string('f', 5000), now);
            Touch(Path.Combine(sp, "live", "7001.tx"), "x", now.AddDays(-3));
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(Path.Combine(d1, "7002_CUR_101010.log")) };
            int f1 = SpoolSweeper.Sweep(sp, now, 24, 0, keep, out int del1, out long fr1);
            ctx.Check(f1 == 0 && del1 == 3 && !File.Exists(Path.Combine(d1, "7001_OLD_101010.log")) && !File.Exists(Path.Combine(d1, "7001_OLD_101010.raw"))
                      && File.Exists(Path.Combine(d1, "7002_CUR_101010.log")) && File.Exists(Path.Combine(d2, "7001_NEW_120000.raw")) && File.Exists(Path.Combine(sp, "live", "7001.tx")),
                      "임시 기록 정리: 24시간 넘은 닫힌 파일(.log·.raw·.idx) 3개 지움, 지금 DiskWriter 파일(시각이 오래돼도)·24시간 안 파일·live\\ 는 둠");
            int f2 = SpoolSweeper.Sweep(sp, now, 24, 5600, keep, out int del2, out long fr2);
            ctx.Check(f2 == 0 && del2 == 2 && fr2 == 4000 && !File.Exists(Path.Combine(d2, "7001_NEW_120000.log")) && !File.Exists(Path.Combine(d2, "7001_NEW_120000.raw"))
                      && File.Exists(Path.Combine(d2, "7001_MID_110000.log")) && File.Exists(Path.Combine(d2, "7001_FRESH_130000.log")) && File.Exists(Path.Combine(d1, "7002_CUR_101010.log")),
                      "합계 상한(5600B): 오래된 닫힌 파일부터 상한 아래가 될 때까지 지움(2개, " + fr2 + "B, 그보다 새 닫힌 파일은 둠) — 1분 안에 바뀐 파일·지금 파일은 상한을 넘어도 둠");
            File.Delete(Path.Combine(d1, "7002_CUR_101010.log"));
            SpoolSweeper.Sweep(sp, now, 24, 0, null, out _, out _);
            SpoolSweeper.HideDir(sp);
            ctx.Check(!Directory.Exists(d1) && (new DirectoryInfo(sp).Attributes & FileAttributes.Hidden) != 0, "빈 날짜 폴더 지움, 임시기록 폴더 숨김 속성");

            // ---- 이름 다듬기
            var t = new DateTime(2026, 11, 6, 15, 30, 0);
            string[] got = { AgingNames.Sanitize("  EVB-A1:에이징/1?  "), AgingNames.Sanitize("CON"), AgingNames.Sanitize("com3.log"), AgingNames.Sanitize("..."), AgingNames.Sanitize("a\tb<>c"),
                             AgingNames.Sanitize(new string('가', 80)), AgingNames.Sanitize("  "), AgingNames.Sanitize("전원 on/off 1000회") };
            ctx.Check(got[0] == "EVB-A1_에이징_1_" && got[1] == "_CON" && got[2] == "_com3.log" && got[3] == null && got[4] == "a_b_c" && got[5] == new string('가', 60) && got[6] == null
                      && got[7] == "전원 on_off 1000회"
                      && AgingNames.DefaultName("EVB-A1", "COM3", 7001, t) == "EVB-A1_20261106-1530" && AgingNames.DefaultName(null, "COM3", 7001, t) == "COM3_20261106-1530"
                      && AgingNames.DefaultName(null, null, 7001, t) == "포트7001_20261106-1530" && AgingNames.FolderName("EVB-A1_20261106-1530", t) == "EVB-A1_20261106-1530"
                      && AgingNames.FolderName("야간", t) == "야간_20261106-1530",
                      "이름 다듬기(윈도 파일 이름): 못 쓰는 글자·제어 문자 '_', 앞뒤 공백·점 지움, 예약 이름 앞 '_', 60자, 빈 이름 null · 기본 <보드|COM|포트>_<yyyyMMdd-HHmm> · 폴더 <이름>_<시각>(기본 이름은 한 번만): "
                      + string.Join(" | ", got.Select(x => x ?? "null")));

            // ---- 글(트레이 정보 줄·툴팁·아이콘)
            ctx.Check(AgingText.Duration(7980) == "2시간 13분" && AgingText.Duration(45) == "45초" && AgingText.Duration(600) == "10분" && AgingText.Duration(90000) == "1일 1시간"
                      && AgingText.RxTail(3, 100) == "마지막 출력 3초 전" && AgingText.RxTail(240, 1000) == "마지막 출력 4분 전" && AgingText.RxTail(725, 8000) == "출력 없음 12분"
                      && AgingText.RxTail(null, 30) == "아직 출력 없음" && AgingText.RxTail(null, 700) == "출력 없음 11분"
                      && AgingText.MenuLine("야간", 7980, 3) == "에이징 중 · 야간 · 2시간 13분 · 마지막 출력 3초 전"
                      && !AgingText.Stale(3, 7980) && AgingText.Stale(725, 8000) && !AgingText.Stale(null, 30) && AgingText.Stale(null, 700),
                      "정보 줄: '에이징 중 · 야간 · 2시간 13분 · 마지막 출력 3초 전'(점은 메뉴가 그림), 10분 넘게 출력 없으면 '출력 없음 12분'·점 경고색(Stale)");
            var row = new CableRow { Port = 7001, Board = "EVB-A1", Com = "COM3", Status = "attached", SessionName = "야간", SessionElapsedS = 7980, SessionLastRxAgeS = 725 };
            ctx.Check(Supervisor.IconKindOf(true, true, true) == "warn" && Supervisor.IconKindOf(false, true, true) == "rec" && Supervisor.IconKindOf(false, false, true) == "run"
                      && Supervisor.IconKindOf(false, false, false) == "stop" && Supervisor.IconKindOf(true, false, false) == "warn"
                      && Supervisor.TitleOf("running", false, false, 3, 0, 0, 1) == "UART 허브 · 연결 3개 · 에이징 중 1개" && Supervisor.TitleOf("running", false, false, 3, 0, 0) == "UART 허브 · 연결 3개"
                      && Supervisor.TitleOf("running", true, false, 3, 0, 0, 1) == "UART 허브 · 계속 꺼짐, 로그 확인 필요" && row.AgingLine == "에이징 중 · 야간 · 2시간 13분 · 출력 없음 12분" && row.AgingStale
                      && new CableRow { Port = 7002 }.AgingLine == null && !new CableRow { Port = 7002 }.AgingStale,
                      "아이콘 우선순위 경고 > 에이징 중(rec) > 실행 > 정지, 툴팁 'UART 허브 · 연결 3개 · 에이징 중 1개'(경고 때는 경고만), 정보 줄은 세션 중인 행만");
            using (var d = JsonDocument.Parse("{\"port\":7001,\"cable\":\"X\",\"com\":\"COM3\",\"status\":\"attached\",\"session\":{\"name\":\"야간\",\"folder\":\"D:\\\\L\\\\에이징\\\\야간\",\"elapsed_s\":61.5,\"last_rx_age_s\":null}}"))
            {
                var cr = CableRow.From(d.RootElement);
                ctx.Check(cr.Aging && cr.SessionName == "야간" && cr.SessionFolder == @"D:\L\에이징\야간" && cr.SessionElapsedS == 61.5 && cr.SessionLastRxAgeS == null && cr.AgingLine == "에이징 중 · 야간 · 1분 · 아직 출력 없음" && !cr.AgingStale,
                          "ls --detail 의 session → CableRow(이름·폴더·경과·마지막 출력 없음)");
            }

            // ---- 터미널 제목 " · 에이징 중"(TermTitler, 가짜 창): 세션 시작 → 붙음, 끝 → 뗌(TeraTerm·PuTTY)
            string hub = Path.Combine(ctx.Dir, "c12titles");
            Directory.CreateDirectory(hub);
            var w = new Wins();
            w.W[(IntPtr)31] = (301, "EVB-A1 · COM3 · 7001");
            w.W[(IntPtr)32] = (302, "EVB-A1 · COM3 · 7001 - PuTTY");
            TermRecords.Add(hub, 7001, 301, 3001, "teraterm", "EVB-A1 · COM3 · 7001");
            TermRecords.Add(hub, 7001, 302, 3002, "putty", "EVB-A1 · COM3 · 7001 - PuTTY");
            var lines = new List<string>();
            var tt = new TermTitler(hub, lines.Add, w);
            var t0 = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
            tt.Apply(new[] { new CableRow { Port = 7001, Board = "EVB-A1", Com = "COM3", Status = "attached" } }, t0);
            bool same0 = w.W[(IntPtr)31].Title == "EVB-A1 · COM3 · 7001" && lines.Count == 0;
            tt.Apply(new[] { new CableRow { Port = 7001, Board = "EVB-A1", Com = "COM3", Status = "attached", SessionName = "야간" } }, t0.AddSeconds(1));
            string on1 = w.W[(IntPtr)31].Title, on2 = w.W[(IntPtr)32].Title;
            w.W[(IntPtr)31] = (301, on1 + " - Tera Term VT");                                     // 터미널이 제목 뒤에 제 이름을 붙여도
            tt.Apply(new[] { new CableRow { Port = 7001, Board = "EVB-A1", Com = "COM3", Status = "attached", SessionName = "야간" } }, t0.AddSeconds(2));
            bool steady = w.W[(IntPtr)31].Title == on1 + " - Tera Term VT";
            tt.Apply(new[] { new CableRow { Port = 7001, Board = "EVB-A1", Com = "COM3", Status = "attached" } }, t0.AddSeconds(3));
            ctx.Check(same0 && on1 == "EVB-A1 · COM3 · 7001 · 에이징 중" && on2 == "EVB-A1 · COM3 · 7001 · 에이징 중 - PuTTY" && steady
                      && w.W[(IntPtr)31].Title == "EVB-A1 · COM3 · 7001" && w.W[(IntPtr)32].Title == "EVB-A1 · COM3 · 7001 - PuTTY"
                      && TermTitler.StaleSuffix("EVB-A1 · COM3 · 7001 · 에이징 중", "EVB-A1 · COM3 · 7001") && !TermTitler.StaleSuffix("EVB-A1 · COM3 · 7001 - Tera Term VT", "EVB-A1 · COM3 · 7001"),
                      "터미널 제목: 세션 시작 → '… · 7001 · 에이징 중'(PuTTY '… · 에이징 중 - PuTTY'), 그대로면 다시 쓰지 않음, 세션 끝 → 뗌 (" + string.Join(" / ", lines) + ")");

            // ---- 요약 글(사건 기록으로): 기간·부팅 cmd/soft/hard·이벤트 목록(세션 폴더 기준 경로)·기록 끊김(허브 다시 시작·케이블)
            var st = new DateTime(2026, 11, 6, 9, 0, 0);
            var s = new AgingSession { Port = 7001, Com = "COM3", Board = "EVB-A1", Name = "야간", Folder = @"D:\L\에이징\야간_20261106-0900", Started = st };
            string Rec(int min, string kind, string extra) => "{\"ts\":\"" + EventLog.Ts(st.AddMinutes(min)) + "\",\"port\":7001,\"board\":\"EVB-A1\",\"kind\":\"" + kind + "\",\"boot_seq\":1" + extra + "}";
            var evs = new[]
            {
                Rec(0, "attach", ",\"reason\":\"session\",\"com\":\"COM3\",\"gap_s\":null"), Rec(10, "boot", ",\"class\":\"cmd\",\"line\":\"LDR>T\""),
                Rec(20, "panic", ",\"line\":\"Kernel panic - not syncing: x\",\"context\":\"D:\\\\L\\\\에이징\\\\야간_20261106-0900\\\\이벤트\\\\092000_커널패닉.txt\""),
                Rec(21, "boot", ",\"class\":\"hard\",\"line\":\"LDR>T\",\"cause\":{\"kind\":\"panic\",\"line\":\"Kernel panic\"}"), Rec(30, "boot", ",\"class\":\"soft\",\"line\":\"LDR>T\""),
                Rec(40, "detach", ",\"reason\":null"), Rec(45, "attach", ",\"reason\":\"reattach\",\"com\":\"COM3\",\"gap_s\":300"),
                Rec(60, "attach", ",\"reason\":\"start\",\"com\":\"COM3\",\"gap_s\":120"), Rec(70, "app_error", ",\"line\":\"APPFAULT(ED)\",\"suspect\":\"cmd_output\""),
                Rec(75, "oom", ",\"line\":\"invoked oom-killer\""),
            }.Select(EventRec.Parse).ToList();
            string txt = AgingSummary.Text(s, evs, st.AddMinutes(133));
            ctx.Check(txt.Contains("이름: 야간\r\n") && txt.Contains("보드: EVB-A1 (포트 7001 · COM3)") && txt.Contains("(2시간 13분)") && txt.Contains("부팅: 모두 3번 · 명령(cmd) 1 · 소프트(soft) 1 · 하드(hard) 1")
                      && txt.Contains("이벤트: 2건 (+ 의심 1건)") && txt.Contains("커널 패닉  Kernel panic - not syncing: x  → 이벤트\\092000_커널패닉.txt") && txt.Contains("panic 뒤 재부팅")
                      && txt.Contains("OOM") && txt.Contains("앱 오류 (의심)") && txt.Contains("기록 끊긴 구간: 2번") && txt.Contains("(5분, 케이블 분리)") && txt.Contains("(2분, 허브 다시 시작)")
                      && txt.Contains("기록한 시간: 2시간 6분 (기록 끊김 2번)") && !txt.Contains("—"),
                      "요약 글: 이름·보드·기간·기록한 시간·부팅 cmd/soft/hard·이벤트(의심 따로, 세션 폴더 기준 경로)·기록 끊김(케이블 분리 5분·허브 다시 시작 2분), 긴 줄표 없음");
        }

        // ================================================================== 허브 안(events-hub)
        public static void InHub(ScenarioContext ctx)
        {
            string root = Path.Combine(ctx.Dir, "c12hub");
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIMAGE", LoaderDelayMs = 700 }, "SIMCABAG", "COM188", 18628, c =>
            {
                c.BasePort = 18638; c.RingMb = 0.25; c.BootMergeMs = 1500;
                c.LogRoot = Path.Combine(root, "logs"); c.HubDir = Path.Combine(root, "hub"); c.LogLayout = 3;
                c.ResolveLogRoot(c.HubDir);
            });
            int port = r.Cab.TcpPort;
            var det = r.Hub.Control.Events;
            string jdir = det.Dir;
            List<EventRec> Evs() => EventLog.Read(jdir, new[] { port });
            ctx.Check(r.Cfg.LogRoot == Path.Combine(root, "hub", "임시기록") && (r.Cab.Writer?.CurrentLogPath ?? "").StartsWith(r.Cfg.LogRoot + "\\", StringComparison.OrdinalIgnoreCase)
                      && det.Gated && jdir == Path.Combine(root, "hub", "events"),
                      "log_layout 3: DiskWriter 는 그대로 <허브 폴더>\\임시기록 에 씀(" + r.Cab.Writer?.CurrentLogPath + "), jsonl <허브 폴더>\\events");

            // ① 세션 밖: 감지 없음
            r.Board.EmitPrintk("Kernel panic - not syncing: outside session");
            Thread.Sleep(1500);
            var st0 = r.Call("session", "status", "--json");
            ctx.Check(det.ScannerOf(r.Cab) == null && det.SessionOf(r.Cab) == null && Evs().Count == 0 && !File.Exists(EventLog.FileOf(jdir, port))
                      && J.S(st0, "status") == "ok" && J.N(st0, "count") == 0 && !Directory.Exists(Path.Combine(root, "logs", "에이징")),
                      "세션 밖 panic → 판정기 없음·jsonl 없음·세션 폴더 없음, session status count 0");

            // ② 거부: 없는 COM · 이름에 쓸 글자 없음 · 끝낼 세션 없음
            var bad1 = r.Call("session", "start", "COM999", "--json");
            var bad2 = r.Call("session", "start", r.P, "...", "--json");
            var bad3 = r.Call("session", "stop", r.P, "--json");
            ctx.Check(J.N(bad1, "exit") == 102 && (J.S(bad1, "error") ?? "").Contains("/connect-uart COM999") && J.N(bad2, "exit") == 106 && J.N(bad3, "exit") == 106 && J.B(bad3, "aging") == false,
                      "거부: 연결 안 된 COM → 102 '먼저 /connect-uart COM999', 이름 '...' → 106, 세션 없는 포트 stop → 106 (" + J.S(bad1, "error") + ")");

            // ③ 시작
            var s1 = r.Call("session", "start", r.P, "시험 에이징:1/2", "--json");
            string folder = J.S(s1, "folder");
            string stamp = folder == null ? "" : Path.GetFileName(folder);
            var ss = new SessionStore(r.Cfg.EffectiveHubDir, null).All();
            ctx.Check(J.S(s1, "status") == "ok" && J.S(s1, "name") == "시험 에이징_1_2" && J.S(s1, "name_given") == "시험 에이징:1/2" && J.B(s1, "recording") == true
                      && Regex.IsMatch(stamp, @"^시험 에이징_1_2_\d{8}-\d{4}$") && Path.GetDirectoryName(folder) == Path.Combine(root, "logs", "에이징")
                      && Directory.Exists(Path.Combine(folder, "이벤트")) && File.Exists(Path.Combine(folder, "콘솔.log")) && ss.Count == 1 && ss[0].Port == port && ss[0].Folder == folder
                      && det.SessionOf(r.Cab) != null && det.ScannerOf(r.Cab) != null,
                      "session start: 이름 다듬음('시험 에이징_1_2'), 폴더 <기준>\\에이징\\<이름>_<yyyyMMdd-HHmm>\\(이벤트\\·콘솔.log), sessions.json 에 남음, 추적 스레드가 따름: " + stamp);
            var dup = r.Call("session", "start", r.P, "--json");
            ctx.Check(J.N(dup, "exit") == 106 && (J.S(dup, "error") ?? "").Contains("이미 에이징 중"), "같은 포트 두 번째 start → 106 '이미 에이징 중'");

            // ④ 세션 중: 콘솔.log 줄머리 · panic → jsonl + 이벤트\<HHMMSS>_커널패닉.txt · ls --detail session · status 필드
            string con = Path.Combine(folder, "콘솔.log");
            r.Board.Emit("aging line one\r\n");
            r.Board.EmitPrintk("Kernel panic - not syncing: inside session");
            ctx.WaitUntil(() => Evs().Any(x => x.Kind == "panic"), 15000, "세션 안 panic → jsonl");
            ctx.WaitUntil(() => Directory.GetFiles(Path.Combine(folder, "이벤트")).Length >= 1, 5000, "이벤트 txt");
            Thread.Sleep(500);
            var evFiles = Directory.GetFiles(Path.Combine(folder, "이벤트")).Select(Path.GetFileName).ToList();
            string ConText() => LogFiles.ReadShared(con) ?? "";
            var cl = ConText().Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0).ToList();
            var p1 = Evs().First(x => x.Kind == "panic");
            ctx.Check(evFiles.Count == 1 && Regex.IsMatch(evFiles[0], @"^\d{6}_커널패닉\.txt$") && p1.Context == Path.Combine(folder, "이벤트", evFiles[0])
                      && (LogFiles.ReadShared(p1.Context) ?? "").StartsWith("이벤트 종류: Kernel panic", StringComparison.Ordinal),
                      "세션 이벤트 txt: 이벤트\\<HHMMSS>_커널패닉.txt(C.11 머리), jsonl context = 그 파일 (" + string.Join(",", evFiles) + ")");
            ctx.Check(cl.Count >= 3 && cl.All(l => ConsoleLineRe.IsMatch(l)) && cl[0].Contains("*** 에이징 시작 '시험 에이징_1_2'") && cl.Any(l => l.EndsWith("] aging line one", StringComparison.Ordinal))
                      && cl.Any(l => l.Contains("Kernel panic - not syncing: inside session")) && !cl.Any(l => l.Contains("outside session")),
                      "콘솔.log: 줄마다 DiskWriter 와 같은 줄머리 '[yyyy-MM-dd HH:mm:ss.fff +   경과] ', 세션 시작 표시, 세션 동안 RX 줄만(세션 밖 panic 없음): " + cl.Count + "줄");
            var ls = r.Call("ls", "--detail");
            var lc = ls.GetProperty("cables").EnumerateArray().First(x => J.N(x, "port") == port);
            var ls0 = r.Call("ls");
            bool plainNoSession = !ls0.GetProperty("cables").EnumerateArray().Any(x => x.TryGetProperty("session", out _));
            ctx.Check(lc.TryGetProperty("session", out var lss) && J.S(lss, "name") == "시험 에이징_1_2" && lss.GetProperty("elapsed_s").GetDouble() >= 0
                      && lss.GetProperty("last_rx_age_s").ValueKind == JsonValueKind.Number && lss.GetProperty("last_rx_age_s").GetDouble() < 30 && plainNoSession,
                      "ls --detail 케이블에 session{name, folder, started, elapsed_s, last_rx_age_s}(트레이·제목용), 기본 ls 는 Phase A 그대로");
            var st1 = r.Call("session", "status", "--json");
            var x1 = st1.GetProperty("sessions")[0];
            var c1 = x1.GetProperty("console_log");
            ctx.Check(J.N(st1, "count") == 1 && J.N(x1, "port") == port && J.S(x1, "com") == "COM188" && J.S(x1, "board") == "SIMAGE" && J.S(x1, "name") == "시험 에이징_1_2" && J.S(x1, "folder") == folder
                      && J.S(x1, "started") != null && x1.GetProperty("elapsed_s").GetDouble() > 0 && J.S(c1, "path") == con && J.N(c1, "bytes") == new FileInfo(con).Length && J.N(c1, "lines") == cl.Count
                      && x1.GetProperty("last_rx_age_s").GetDouble() < 30 && J.N(x1.GetProperty("events"), "total") == 1 && J.N(x1.GetProperty("events").GetProperty("by_kind"), "panic") == 1
                      && x1.GetProperty("gaps").GetArrayLength() == 0 && J.B(x1, "connected") == true,
                      "session status --json: port·com·board·name·folder·started·elapsed_s·console_log{path, bytes, lines}·last_rx_age_s·events{total, by_kind}·gaps·connected: " + x1.GetRawText().Substring(0, Math.Min(300, x1.GetRawText().Length)));

            // ⑤ 코어 다시 시작 → 세션 이어 감(콘솔.log 끊김 표시, 이벤트 계속)
            r.Hub.Stop();
            ctx.Check(det.Join(3000), "허브 정지 → 추적 스레드 끝(세션은 sessions.json 에 남음)");
            Thread.Sleep(1200);
            var r2 = HubRig.Boot(ctx, r.Cfg, r.Board, r.Prov, "SIMCABAG", true);
            var det2 = r2.Hub.Control.Events;
            ctx.WaitUntil(() => det2.SessionOf(r2.Cab) != null && det2.ScannerOf(r2.Cab) != null, 5000, "다시 기동한 코어가 세션을 이어 따름");
            r2.Board.Emit("after restart line\r\n");
            r2.Board.EmitAppLog("APPFAULT(ED)");
            ctx.WaitUntil(() => Evs().Any(x => x.Kind == "app_error"), 15000, "재시작 뒤 app_error → jsonl");
            ctx.WaitUntil(() => Directory.GetFiles(Path.Combine(folder, "이벤트")).Length >= 2, 5000, "재시작 뒤 이벤트 txt");
            Thread.Sleep(500);
            var cl2 = ConText().Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0).ToList();
            ctx.Check(cl2.Any(l => l.Contains("*** 기록 끊김 ") && l.Contains("허브가 다시 켜졌거나")) && cl2.Any(l => l.EndsWith("] after restart line", StringComparison.Ordinal))
                      && cl2.Count > cl.Count && cl2.Take(cl.Count).SequenceEqual(cl) && Directory.GetFiles(Path.Combine(folder, "이벤트")).Any(f => f.EndsWith("_앱오류_APPFAULT(ED).txt", StringComparison.Ordinal)),
                      "재시작 뒤: 같은 콘솔.log 에 이어 씀(앞 줄 그대로) + '*** 기록 끊김 … ***', 새 RX 줄·앱오류_APPFAULT(ED).txt");

            // ⑥ 끝내기 → 요약.txt, 그 뒤 감지 없음
            var stop = r2.Call("session", "stop", r2.P, "--json");
            string sum = Path.Combine(folder, "요약.txt");
            string text = File.Exists(sum) ? File.ReadAllText(sum) : "";
            int nEv = Evs().Count;
            r2.Board.EmitPrintk("Kernel panic - not syncing: after stop");
            Thread.Sleep(1500);
            ctx.Check(J.S(stop, "status") == "ok" && J.S(stop, "summary") == sum && J.S(stop, "summary_text") == text && det2.SessionOf(r2.Cab) == null && det2.ScannerOf(r2.Cab) == null
                      && new SessionStore(r.Cfg.EffectiveHubDir, null).Count == 0 && Evs().Count == nEv && Directory.GetFiles(Path.Combine(folder, "이벤트")).Length == 2
                      && !ConText().Contains("after stop") && ConText().TrimEnd().EndsWith("*** 에이징 끝 ***", StringComparison.Ordinal),
                      "session stop: 요약.txt 저장·응답에 summary·summary_text, 세션 빠짐, 끝낸 뒤 panic 은 jsonl·txt·콘솔.log 에 없음, 콘솔.log 끝 '*** 에이징 끝 ***'");
            ctx.Check(text.StartsWith("에이징 요약\r\n이름: 시험 에이징_1_2\r\n보드: SIMAGE (포트 " + port + " · COM188)", StringComparison.Ordinal) && text.Contains("부팅: 모두 ")
                      && text.Contains("이벤트: 2건") && Regex.IsMatch(text, @"\d\d:\d\d:\d\d  커널 패닉  \[\s*[\d.]+\] Kernel panic - not syncing: inside session  → 이벤트\\\d{6}_커널패닉\.txt") && text.Contains("앱 오류  APPFAULT(ED)")
                      && text.Contains("기록 끊긴 구간: 1번") && text.Contains("허브 다시 시작") && text.Contains("→ 이벤트\\") && !text.Contains("—"),
                      "요약.txt: 이름·보드·기간·부팅 수·이벤트 2건(커널 패닉·앱 오류, 세션 폴더 기준 txt)·기록 끊긴 구간 1번(허브 다시 시작)");
            ctx.Log("요약.txt 예시(" + sum + "):");
            foreach (var l in text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n')) ctx.Log("    | " + l);

            // ⑦ 연결 안 된 포트(케이블 뽑힘) → 시작 거부(/connect-uart)
            ((FakePortProvider)r2.Prov).Unplug("SIMCABAG");
            ctx.WaitUntil(() => !r2.Cab.IsAttached, 5000, "케이블 뽑힘");
            var bad4 = r2.Call("session", "start", r2.P, "--json");
            ctx.Check(J.N(bad4, "exit") == 102 && (J.S(bad4, "error") ?? "").Contains("/connect-uart COM188") && J.S(bad4, "blocked_reason") == "not_connected"
                      && new SessionStore(r.Cfg.EffectiveHubDir, null).Count == 0,
                      "연결 안 된 포트 session start → 102 '먼저 /connect-uart COM188 로 연결', 세션 만들지 않음");
            r2.Hub.Stop();
        }

        // ================================================================== 실제 exe CLI(events-cli)
        /// <param name="cli">(환경, 인자) → (exit, JSON, 표준 출력, 표준 오류) — events-cli 의 Cli.</param>
        /// <param name="emit">보드 출력 넣기(코어 안 FakeBoard).</param>
        public static void Cli(ScenarioContext ctx, Func<Dictionary<string, string>, string[], (int Code, JsonElement J, string Out, string Err)> cli, string ps, Action<string> emit)
        {
            var s0 = cli(null, new[] { "session", "status" });
            var bad = cli(null, new[] { "session", "start", "COM999", "--json" });
            ctx.Check(s0.Code == 0 && s0.Out == "에이징 중인 포트 없음" && bad.Code == 102 && (J.S(bad.J, "error") ?? "").Contains("/connect-uart COM999"),
                      "cli session status(없음) → '에이징 중인 포트 없음', 연결 안 된 COM 시작 → 102 /connect-uart 안내");
            var st = cli(null, new[] { "session", "start", ps, "cli 에이징" });
            ctx.Check(st.Code == 0 && st.Out.StartsWith("에이징을 시작했습니다: 'cli 에이징' · 포트 " + ps, StringComparison.Ordinal) && st.Out.Contains("폴더: "),
                      "cli session start <port> <이름> → 한국어 안내 + 폴더: " + st.Out.Replace("\n", " / "));
            emit("cli aging line\r\nAPPFAULT(ED)\r\n");
            Thread.Sleep(3000);
            var sj = cli(null, new[] { "session", "status", "--json" });
            var x = sj.Code == 0 ? sj.J.GetProperty("sessions")[0] : default;
            var one = cli(null, new[] { "session", "status" });
            var wsl = cli(new Dictionary<string, string> { ["UART_PATH_STYLE"] = "wsl" }, new[] { "session", "status", "--json" });
            ctx.Check(sj.Code == 0 && J.N(sj.J, "count") == 1 && J.S(x, "name") == "cli 에이징" && J.N(x.GetProperty("console_log"), "lines") >= 2 && J.N(x.GetProperty("events"), "total") == 1
                      && J.N(x.GetProperty("events").GetProperty("by_kind"), "app_error") == 1 && x.GetProperty("last_rx_age_s").ValueKind == JsonValueKind.Number
                      && one.Code == 0 && Regex.IsMatch(one.Out, "^" + ps + @" COM\d+ \S+ · 에이징 중 'cli 에이징' · \d+초 · 마지막 출력 \d+초 전 · 이벤트 1건\(앱 오류 1\) · 콘솔\.log \d+줄 · ")
                      && (J.S(wsl.J.GetProperty("sessions")[0], "folder") ?? "").StartsWith("/mnt/", StringComparison.Ordinal),
                      "cli session status: --json 필드(이름·콘솔.log 줄·이벤트 by_kind·last_rx_age_s), 한국어 한 줄, UART_PATH_STYLE=wsl 이면 folder 도 /mnt/…: " + one.Out);
            var sum = cli(null, new[] { "session", "summary", ps, "--since", "1h", "--json" });
            var ag = cli(null, new[] { "aging", ps, "--since", "1h", "--json" });
            ctx.Check(sum.Code == 0 && J.S(sum.J, "status") == "ok" && sum.J.TryGetProperty("boots", out _) && J.N(sum.J, "port") == long.Parse(ps, CultureInfo.InvariantCulture)
                      && ag.Code == 0 && J.N(sum.J.GetProperty("kinds").GetProperty("app_error"), "count") == J.N(ag.J.GetProperty("kinds").GetProperty("app_error"), "count"),
                      "cli session summary <port> --since = aging(이전 이름도 그대로)");
            // 진행 중인 세션이면 --since 없이도 그 세션 시작부터(scope session + 세션 정보), --since 를 주면 그 기간(scope since)
            var cur = cli(null, new[] { "session", "summary", ps, "--json" });
            var cs = cur.Code == 0 && cur.J.TryGetProperty("session", out var cse) ? cse : default;
            ctx.Check(cur.Code == 0 && J.S(cur.J, "scope") == "session" && J.S(cs, "name") == "cli 에이징" && J.S(cur.J, "since") == J.S(x, "started")
                      && (J.S(cs.GetProperty("console_log"), "path") ?? "").EndsWith("콘솔.log", StringComparison.Ordinal) && J.S(sum.J, "scope") == "since"
                      && J.N(cur.J.GetProperty("kinds").GetProperty("app_error"), "count") == 1,
                      "session summary <port>(진행 중) → scope session·since = 세션 시작·세션 이름·콘솔.log 경로, --since 를 주면 scope since: " + J.S(cur.J, "since"));
            // 부팅 판정 근거와 '부팅 0' 안내: 30분 넘는 기간에 부팅이 하나도 없으면 hint no_boots_seen(재부팅하는데 허브가 못 알아보는 보드 경고)
            var bd = cur.J.GetProperty("boot_detection");
            var longQ = cli(null, new[] { "session", "summary", ps, "--since", "2h", "--json" });
            bool longBoots = longQ.Code == 0 && J.N(longQ.J.GetProperty("boots"), "total") > 0;
            ctx.Check(J.S(bd, "loader_marker_re") == "LDR>T" && (J.S(bd, "boot_re") ?? "").Contains("LDR>T") && J.S(bd, "profile") == "sim-evb" && !J.Has(cur.J, "hint")
                      && (longBoots ? !J.Has(longQ.J, "hint") : J.S(longQ.J, "hint") == "no_boots_seen"),
                      "boot_detection = 지금 쓰는 로더 표시·boot_re·프로필, 짧은 기간은 안내 없음, 2시간(부팅 " + (longBoots ? "있음 → 안내 없음" : "0 → no_boots_seen") + "): "
                      + J.Show(bd) + " hint=" + J.S(longQ.J, "hint"));
            var t0h = DateTimeOffset.Now;
            ctx.Check(EventLog.NoBootsHint(false, t0h.AddHours(-2), t0h) && !EventLog.NoBootsHint(true, t0h.AddHours(-2), t0h) && !EventLog.NoBootsHint(false, t0h.AddMinutes(-10), t0h)
                      && !EventLog.NoBootsHint(false, null, t0h),
                      "NoBootsHint: 부팅 0·2시간 → 안내, 부팅 있음 → 없음, 10분 → 없음, 기간 모름 → 없음");
            var stop = cli(null, new[] { "session", "stop", ps });
            ctx.Check(stop.Code == 0 && stop.Out.StartsWith("에이징을 끝냈습니다. 요약을 저장했습니다: ", StringComparison.Ordinal) && stop.Out.Contains("에이징 요약\n이름: cli 에이징")
                      && stop.Out.Contains("앱 오류  APPFAULT(ED)"),
                      "cli session stop <port> → 한국어 안내 + 요약 글");
            var again = cli(null, new[] { "session", "stop", ps, "--json" });
            ctx.Check(again.Code == 106 && J.B(again.J, "aging") == false, "끝낸 포트 다시 stop → 106");
        }
    }
}
