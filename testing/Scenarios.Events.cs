// US-113(계획 X6 부록 C — 에이징 사건 감지) 시나리오: events-units · events-hub · events-cli. 앱 오류·로더 표시 패턴은 시험용 가상 프로필(tests/profile.test.json).
//   events-units    : 프로세스·허브 없이(하네스 안) EventScanner 에 줄을 직접 넣는다(시각도 직접 — 결정적). 종류별 기본 패턴(새 Oops·BUG·rcu·hung task·
//                     app_error 의 EVB-A1 표시·000f 대소문자 무시) · panic 묶음 · config events(patterns·enabled·max_mb·케이블별)·검증·C-12 ·
//                     boot cmd/soft/hard — 리셋 순간의 되돌아보기(명령 자리의 reboot 만·reset 은 U-Boot 프롬프트에서만·수신 에코는 송신 CR 2초 안의 그 명령 에코만·
//                     cmd 창 60초를 Restarting 이 이음·직전 20초 panic/oops = hard+cause·직전 10초 Restarting = soft·그 밖 hard+silence_s) ·
//                     판정(부록 C.7: 확실한 재출력 <N>[ts]·에코는 0, 명령 출력 구간 안 사건 줄은 모두 suspect, 덤프 속 프롬프트는 구간을 닫지 않음, 로더 표시는 줄 어디에 있든 리셋,
//                     구간 10초 무음 닫힘·송신 바이트로 다시 열림, 구간 안 로더 표시는 앞 무음 0.5초, 구간 밖 커널 시각 거르기(앞섬·단조 — 절전 뒤 실시간), 로더 없는 보드는
//                     앞 무음 1초, suspect panic 뒤 30초 무응답 = confirm) · 검증자 탐침 R1~R9·N1~N11·S1·FG·MORE·TAILF·STALL ·
//                     앞뒤 로그 줄 수·30초·부팅 뒤 새 묶음·soft/hard 부팅 앞 줄 · 예산(부팅마다 — 같은 종류 rate·포트 hourly·폴더 disk, 첫 panic·soft/hard 부팅 면제, hub.log 경고) ·
//                     쓰기 실패 재시도·대기열·버림 기록 · missed · attach·detach(공백·부팅 시각 지움) · 재시작 뒤 이어 붙임·끊긴 마지막 줄 · 서로게이트 자르기·깨진 줄 ·
//                     EventTail · 풍선 묶음·한 틱 합치기(suspect 는 안 알림, cause·confirm 은 알림) · aging(허브 꺼짐·분리 공백·간격 제외·suspect 따로) · 대상 해석.
//   (events-fixtures — 실보드 로그 재생은 비공개 internal/testing/Scenarios.Fixtures.cs, 사내 제품 프로필로 판정)
//   events-hub      : 허브(하네스 안 Hub + FakeBoard)의 실제 링 추적: attach 기록, panic → jsonl·앞뒤 로그, Claude reboot → cmd, 사람이 친 reboot → cmd,
//                     Restarting 뒤 리셋 → soft(앞 줄 파일), 리셋 → hard, 추적 멈춤 중 링 덮어씀 → missed, 케이블 뽑음·다시 꽂음 → detach·attach(reattach),
//                     허브 정지('# 끝 — 허브 정지')·다시 기동 → 이어 붙음, events.enabled:false.
//   events-cli      : 실제 UartHub.exe 감독(--no-tray --sim, C-13 격리)·코어 안 FakeBoard(주입 emit·reset): --follow(머리 줄·사건 줄·--max·--kind boot:hard·
//                     표준 출력 닫힘 → 141·표준 입력 EOF), 감독 풍선, events·aging(COMx·보드명 대상), 경로 표기, 인자 오류, 코어 명령은 --follow·--kind 를 받지 않음(106).
//   포트: events-hub 제어 18620·케이블 18630(+ 꺼짐 시험 18625·18635), events-cli 제어 18640·케이블 18650. units 는 소켓을 열지 않는다.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace UartHub.Testing
{
    public static partial class EventScenarios
    {
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        static readonly DateTime T0 = new DateTime(2026, 11, 5, 17, 0, 0, DateTimeKind.Local);

        // ================================================================== 단위 도구
        /// <summary>판정기 하나 + 시각(초) 기준으로 줄을 넣는 도구. 끝나면 닫는다(jsonl 스트림).</summary>
        sealed class Feeder
        {
            public EventScanner S;
            public string Dir;
            public int Port;
            long n;
            public Feeder(ScenarioContext ctx, string dir, int port, Config cfg = null, string cable = "CABU", HubLog log = null, EventsBudget budget = null, string board = "SIMEV")
            {
                Dir = dir; Port = port;
                cfg ??= Config.Default();
                S = new EventScanner(dir, port, cable, () => board, () => @"C:\logs\20261105\" + port + "_SIMEV_170000.log", EventPatterns.For(cfg, cable), cfg, log, budget);
                var s = S;
                ctx.Defer(() => s.Close("시험 끝"));
            }
            public void L(double t, string text, bool partial = false)
            {
                var segs = EventLines.Segments(Encoding.UTF8.GetBytes(text));
                S.OnLine(string.Concat(segs), segs, T0.AddSeconds(t), (long)(t * 1000), "1:" + (n++).ToString(CultureInfo.InvariantCulture), partial);
            }
            public void Tx(double t, string text, bool edited = false) => S.OnTxLine(text, (long)(t * 1000), edited);
            /// <summary>CR 없는 송신 바이트(페이저 키 등).</summary>
            public void Key(double t) => S.OnTxBytes((long)(t * 1000));
            public void Ticks(double a, double b) { for (double t = a; t <= b; t += 0.2) Tick(t); }
            public void Tick(double t) => S.Tick(T0.AddSeconds(t), (long)(t * 1000));
            public List<EventRec> Evs() => EventLog.Read(Dir, new[] { Port });
            /// <summary>사건 종류 순서(연결·분리 빼고, '?' = suspect — 명령 출력 속 줄).</summary>
            public string Kinds() => string.Join(",", Evs().Where(e => e.Kind != "attach" && e.Kind != "detach").Select(e => (e.Kind == "boot" ? "boot:" + e.Class : e.Kind) + (e.Suspect != null ? "?" : "")));
            /// <summary>검증자 탐침과 같은 표기: boot:class(+cause 종류) · confirm:of · 종류, '?' = suspect, [rate] 등 = 앞뒤 로그를 줄인 이유.</summary>
            public string K() => string.Join(",", Evs().Where(e => e.Kind != "attach" && e.Kind != "detach")
                .Select(e => (e.Kind == "boot" ? "boot:" + e.Class + (e.CauseKind != null ? "+" + e.CauseKind : "") : e.Kind == "confirm" ? "confirm:" + e.Of : e.Kind)
                             + (e.Suspect != null ? "?" : "") + (e.ContextSkipped != null ? "[" + e.ContextSkipped + "]" : "")));
        }

        /// <summary>앞뒤 로그 줄(쓰는 중인 파일도 — 공유 읽기).</summary>
        static string[] Lines(string path) => LogFiles.ReadShared(path).Split(new[] { "\r\n" }, StringSplitOptions.None).Where(l => l.Length > 0).ToArray();

        static string[] JsonlLines(string path) => LogFiles.ReadShared(path).Split('\n').Where(l => l.Trim().Length > 0).ToArray();

        /// <summary>판정기가 열어 둔 파일에 덧붙이기(공유 쓰기 — 다른 프로그램이 쓴 것처럼).</summary>
        static void AppendShared(string path, byte[] b)
        {
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            fs.Write(b, 0, b.Length);
        }

        static Config MergeCfg() { var c = Config.Default(); c.BootMergeMs = 1500; return c; }

        static HubLog TempLog(ScenarioContext ctx, string name) => new HubLog(Path.Combine(ctx.Dir, name), SystemClock.Instance, "core");
        static string ReadLog(ScenarioContext ctx, string name) => File.Exists(Path.Combine(ctx.Dir, name)) ? LogFiles.ReadShared(Path.Combine(ctx.Dir, name)) : "";

        // ================================================================== events-units
        [Scenario("events-units", "X6 단위(프로세스 없음): 기본 패턴(panic·oops 3종·'Oops#1' 아님·BUG 낱말 앞만·rcu·hung task·oom·app_error 6종·APPFAULT(BA)(000f) 대소문자 무시 제외) · panic 묶음·sysrq-c 한 파일 · config events(patterns·enabled·max_mb·케이블별·검증 오류·C-12) · boot cmd/soft/hard 를 리셋 순간 되돌아보기로(명령 자리 reboot·echo/grep reboot 은 아님·busybox reboot·reset 은 U-Boot 에서만·수신 에코는 송신 CR 2초 안 그 명령 에코만·Restarting 이 cmd 창을 이음·LDR>T 뒤 커널 줄 15초 병합·LDR>T 1초 넘으면 별개·EVB-A1 프롬프트 재출력·로더 없는 부팅·silence_s) · 판정(C.7: <N>[ts]·에코 사건 0·구간 안 사건 줄은 모두 suspect·덤프 속 프롬프트는 구간을 닫지 않음·로더 표시는 언제나 리셋(깨진 줄 끝도)·리셋의 cause·구간 밖 커널 시각 거르기·로더 없는 보드는 앞 무음 1초·suspect panic 뒤 30초 무응답 confirm·프롬프트가 오면 취소) · 구간 10초 무음 닫힘(송신 바이트로 다시 열림)·구간 안 로더 표시는 앞 무음 0.5초·구간 밖 커널 시계 단조 검사(절전 뒤 실시간) · 일으킨 충돌·리셋 명령은 구간 밖(W7)·유휴 프롬프트 뒤 열린 채 끊긴 LDR>T 도 리셋(BLK-1)·soft 는 로더 앞 2초 안 마지막 커널 줄 Restarting(BLK-2)·페이저에서 읽는 중엔 confirm 없음(W1)·붙은 뒤 단조 검사는 두 줄이 맞을 때(W3) · 검증자 탐침 R1~R9·N1~N11·S1·FG·MORE·TAILF·STALL·Q1~Q14 · 앞뒤 로그 200/100줄·30초·부팅 뒤 새 묶음·soft/hard 부팅 앞 줄 · 예산 부팅마다 rate·hourly·disk(첫 panic·soft/hard 부팅 면제·경고·skipped_before·'# 끝' 생략 수) · 쓰기 실패 재시도·대기열·버림 기록 · missed · attach·detach(공백 30초 넘으면 부팅 시각 지움) · 재시작 이어 붙임·끊긴 마지막 줄·NUL · 서로게이트 자르기·깨진 줄 건너뜀 · EventTail · 풍선(대기 2초·1분 창·외 N건·한 틱 합침·rate·suspect 는 안 알림·'panic 후 리셋'·'멈춤 의심') · aging(허브 꺼짐·분리 공백·--since 앞 분리·covered_s·간격 제외·suspect 따로·with_cause) · 대상 해석(포트·COMx·alias·보드·102·105)",
                  Order = 1300, TimeoutS = 90)]
        public static void Units(ScenarioContext ctx)
        {
            string dir = Path.Combine(ctx.Dir, "logs", "events");
            Directory.CreateDirectory(dir);

            // ---- panic 묶음: 끝 줄('---[ end Kernel panic …')도 같은 사건
            var a = new Feeder(ctx, dir, 18601);
            for (int i = 0; i < 5; i++) a.L(i * 0.1, "[  100.00" + i + "000] normal " + i);
            a.L(1.0, "[  120.000000] Kernel panic - not syncing: sysrq triggered crash");
            a.L(1.01, "[  120.000100] CPU: 0 PID: 1 Comm: sh Not tainted 4.19.91 #1");
            a.L(1.02, "[  120.000200] Call trace:");
            a.L(1.03, "[  120.000300]  dump_backtrace+0x0/0x1a0");
            a.L(1.04, "[  120.000400]  panic+0x150/0x2f8");
            a.L(1.05, "[  120.001000] ---[ end Kernel panic - not syncing: sysrq triggered crash ]---");
            var ea = a.Evs();
            ctx.Check(ea.Count == 1 && ea[0].Kind == "panic" && ea[0].Context != null && File.Exists(ea[0].Context) && a.S.OpenContexts == 1,
                      "panic 묶음: 'Kernel panic' 과 '---[ end Kernel panic' 이 한 사건(사건 " + ea.Count + "개, 앞뒤 로그 1개)");
            a.Tick(32);
            var la = Lines(ea[0].Context);
            ctx.Check(la[0].StartsWith("# UART 허브 사건 기록 — panic", StringComparison.Ordinal) && la.Count(l => l.StartsWith(">>> ", StringComparison.Ordinal)) == 2
                      && la.Count(l => l.StartsWith("    [", StringComparison.Ordinal)) == 5 + 4 && la.Last().StartsWith("# 끝 — 30초 지남 (뒤 5줄, 사건 줄 2개: panic)", StringComparison.Ordinal)
                      && Path.GetFileName(ea[0].Context) == "18601_SIMEV_170001_panic.txt" && Path.GetFileName(Path.GetDirectoryName(ea[0].Context)) == "20261105",
                      "panic 앞뒤 로그: 머리말·'>>>' 2줄·앞 5줄+뒤 4줄·'# 끝 — 30초 지남'·이름 events\\20261105\\18601_SIMEV_170001_panic.txt (" + la.Last() + ")");
            var j0 = JsonDocument.Parse(ea[0].Raw).RootElement;
            string[] fields = { "ts", "port", "cable", "board", "kind", "line", "log", "log_line", "context", "boot_seq", "since_boot_s" };
            ctx.Check(fields.All(f => j0.TryGetProperty(f, out _)) && !j0.TryGetProperty("class", out _) && J.N(j0, "port") == 18601 && J.S(j0, "cable") == "CABU"
                      && J.S(j0, "board") == "SIMEV" && J.S(j0, "log_line") == "1:5" && J.IsNull(j0, "since_boot_s") && Regex.IsMatch(J.S(j0, "ts"), @"^2026-11-05T17:00:01\.000[+-]\d\d:\d\d$"),
                      "jsonl 필드 {ts, port, cable, board, kind, line, log, log_line, context, boot_seq, since_boot_s}(class 는 boot 만): " + ea[0].Raw);

            // ---- 종류별 기본 패턴(30초 넘게 떨어뜨림 = 각각 새 사건)
            var c = new Feeder(ctx, dir, 18603);
            string[] lines =
            {
                "[  200.1] Unable to handle kernel NULL pointer dereference at virtual address 0000000000000000",   // oops
                "[  200.2] Internal error: Oops: 96000045 [#1] PREEMPT SMP",                                       // oops
                "[  240.000000] Oops: 0000 [#2] PREEMPT SMP",                                                       // oops(줄 처음)
                "Oops#1 Part1",                                                                                     // 아님(pstore 머리말)
                "[  300.0] BUG: scheduling while atomic: swapper/0/0/0x00000002",                                    // bug
                "[  300.1] DEBUG: not a bug",                                                                         // 아님(낱말 안의 BUG)
                "[  302.0] kernel BUG at mm/slub.c:3912!",                                                            // bug
                "[  303.0] WARNING: CPU: 1 PID: 22 at kernel/workqueue.c:1442 __queue_work+0x2c0/0x4d8",              // bug
                "[  304.0] rcu: INFO: rcu_preempt self-detected stall on CPU",                                        // bug
                "[  304.5] INFO: rcu_sched self-detected stall on CPU",                                               // bug(옛 형식)
                "[  305.0] watchdog: BUG: soft lockup - CPU#0 stuck for 22s! [kworker/0:1:33]",                       // bug
                "[  306.0] INFO: task myrecord:812 blocked for more than 120 seconds.",                               // bug
                "[  306.1] \"echo 0 > /proc/sys/kernel/hung_task_timeout_secs\" disables this message.",              // 아님
                "[  350.0] myapp invoked oom-killer: gfp_mask=0x6000c0(GFP_KERNEL), order=3",                        // oom
                "[2026/11/05-08:00:00] APPFAULT(ED)", "[2026/11/05-08:01:00] APPFAULT(0045)", "[2026/11/05-08:02:00] APPFAULT(BA)(000b)",   // app_error
                "[2026/11/05-08:03:00] APPFAULT(BA)(000f)", "[2026/11/05-08:03:01] APPFAULT(ba)(000F)",             // 아님(SIGTERM 정상 reboot, 대소문자 무시)
                "venc: Encoder Error 67 ch1", "AP-ERROR(0006)", "ENC-ERROR(0045)",                                   // app_error
                "[ 1086.898286] Panic call, app_wdt_external_set ### 0, force (APP-WATCHDOG-ENABLE)",               // app_error(EVB-A1)
                "[ 1501.456881] APP_ABORT reason=TEST_ALIVE",                                                       // app_error(EVB-A1)
            };
            for (int i = 0; i < lines.Length; i++) c.L(i * 40, lines[i]);                   // 커널 시각은 늘어나게(구간 밖 커널 시계 단조 검사)
            string want = "oops,oops,oops,bug,bug,bug,bug,bug,bug,bug,oom,app_error,app_error,app_error,app_error,app_error,app_error,app_error,app_error";
            ctx.Check(c.Kinds() == want && !c.Evs().Any(x => x.Line.Contains("000f") || x.Line.Contains("000F") || x.Line.Contains("DEBUG") || x.Line.Contains("Part1") || x.Line.Contains("disables")),
                      "기본 패턴: oops 3·bug 7(BUG·kernel BUG·WARNING·rcu 두 형식·soft lockup·task blocked)·oom·app_error 8(APPFAULT·Encoder·BB/ENC·Panic call·APP_ABORT), 'Oops#1 Part1'·'DEBUG:'·hung_task 안내 줄·APPFAULT(BA)(000f)/(ba)(000F) 는 아님 (" + c.Kinds() + ")");
            var b = new Feeder(ctx, dir, 18602);
            b.L(0, "[  200.000000] sysrq: SysRq : Trigger a crash");
            b.L(0.1, "[  200.100000] Unable to handle kernel NULL pointer dereference at virtual address 0000000000000000");
            b.L(0.12, "[  200.120000] Internal error: Oops: 96000045 [#1] PREEMPT SMP");
            b.L(0.2, "[  200.200000] Kernel panic - not syncing: Fatal exception");
            var eb = b.Evs();
            ctx.Check(b.Kinds() == "oops,panic" && eb[0].Context == eb[1].Context, "sysrq-c: oops 뒤 panic = 같은 앞뒤 로그 1개·사건 2(종류마다)");

            // ---- config: events.patterns 끄기·더하기, cables.<id>.events, enabled, max_mb
            var cfg = Config.Parse("{ \"events\": { \"enabled\": true, \"max_mb\": 512, \"patterns\": { \"app_error\": { \"off\": [\"Encoder Error\"], \"add\": [\"MYAPP FATAL\"] }, \"bug\": { \"off\": true } } },\n"
                                   + "  \"cables\": { \"CABU\": { \"alias\": \"eva\", \"events\": { \"patterns\": { \"panic\": { \"add\": [\"SIM PANIC\"] } } } }, \"CABX\": { \"events\": { \"enabled\": false } } } }");
            ctx.Check(cfg.Validate().Count == 0 && cfg.Events.Patterns["bug"].OffAll && cfg.Events.MaxMb == 512 && cfg.Events.Enabled && cfg.Cables["CABX"].Events.Enabled == false
                      && cfg.Cables["CABU"].Events.Patterns["panic"].Add.Single() == "SIM PANIC" && Config.Default().Events.Enabled && Config.Default().Events.MaxMb == 2048,
                      "config events(enabled·max_mb·patterns)·cables.<id>.events(patterns·enabled) 읽음, 기본 enabled true·max_mb 2048");
            var f = new Feeder(ctx, dir, 18606, cfg);
            f.L(0, "venc: Encoder Error 67"); f.L(40, "MYAPP FATAL: code 3"); f.L(80, "BUG: x"); f.L(120, "SIM PANIC now"); f.L(160, "APPFAULT(ED)");
            ctx.Check(f.Kinds() == "app_error,panic,app_error" && f.Evs()[0].Line.StartsWith("MYAPP", StringComparison.Ordinal),
                      "패턴 조정: 끈 'Encoder Error'·bug 전부는 사건 아님, 더한 'MYAPP FATAL'·케이블 'SIM PANIC' (" + f.Kinds() + ")");
            var f2 = new Feeder(ctx, dir, 18616, cfg, "OTHER");
            f2.L(0, "SIM PANIC now");
            ctx.Check(f2.Evs().Count == 0, "케이블별 패턴은 다른 케이블에 적용 안 됨");
            string Errs(string json) { try { var x = Config.Parse(json); return string.Join(" | ", x.Validate()); } catch (ConfigException ce) { return string.Join(" | ", ce.Errors); } }
            string e1 = Errs("{\"events\": {\"patterns\": {\"warn\": {\"add\": [\"x\"]}}}}"), e2 = Errs("{\"events\": {\"patterns\": {\"oom\": {\"add\": [\"(\"]}}}}"),
                   e3 = Errs("{\"events\": {\"patterns\": {\"bug\": {\"off\": [\"BUG:\"]}}}}"), e4 = Errs("{\"events\": {\"pattern\": {}}}"),
                   e5 = Errs("{\"cables\": {\"A\": {\"events\": {\"patterns\": {\"oops\": {\"addd\": []}}}}}}"), e6 = Errs("{\"events\": {\"enabled\": 1, \"max_mb\": -5}}"),
                   e7 = Errs("{\"cables\": {\"A\": {\"events\": {\"max_mb\": 10}}}}");
            ctx.Check(e1.Contains("알 수 없는 종류 'warn'") && e2.Contains("events.patterns.oom.add 정규식 오류") && e3.Contains("기본 패턴에 없음 'BUG:'") && e4.Contains("알 수 없는 키 'pattern'")
                      && e5.Contains("cables.A.events.patterns.oops: 알 수 없는 키 'addd'") && e6.Contains("events.enabled 는 true/false") && e6.Contains("events.max_mb 는 양수")
                      && e7.Contains("cables.A.events: 알 수 없는 키 'max_mb'"),
                      "검증 오류: 모르는 종류·정규식·기본에 없는 off·모르는 키·enabled 타입·max_mb 음수·케이블 max_mb — " + string.Join(" / ", e1, e2, e3, e4, e5, e6, e7));
            string withEv = "{\n  \"cables\": {\"CABU\": {\"alias\": \"eva\", \"events\": {\"patterns\": {\"panic\": {\"add\": [\"SIM PANIC\"]}}}}},\n  \"events\": {\"enabled\": true, \"patterns\": {\"bug\": {\"off\": true}}}\n}\n";
            string nb = Config.WithCableBaud(withEv, "CABU", 9600, out string why);
            ctx.Check(nb != null && nb.Contains("\"events\": {\"patterns\": {\"panic\": {\"add\": [\"SIM PANIC\"]}}}") && nb.Contains("\"events\": {\"enabled\": true, \"patterns\": {\"bug\": {\"off\": true}}}")
                      && Config.Parse(nb).CableFor("CABU").Baud == 9600, "C-12: cable baud 저장(WithCableBaud)이 cables.<id>.events·최상위 events 원문을 그대로 둠" + (nb == null ? " — " + why : ""));

            // ---- boot cmd / soft / hard(커널 시각은 그 부팅의 시계와 맞게 — 명령 출력 구간 안의 줄은 시계로 판정한다, 부록 C.6)
            const string Pr = "root@SIMEV:~# ";
            var g = new Feeder(ctx, dir, 18607, MergeCfg());
            g.L(0, "[ 5000.000000] evb_sim: periodic tick");
            g.Tx(1, "reboot");                                                              // ① 송신 reboot(붙은 뒤 가동 시간 모름 → Restarting 은 suspect 지만 reboot 명령이 죽음 신호)
            g.L(1.05, "reboot");
            g.L(1.2, "[ 5001.200000] reboot: Restarting system");
            g.L(2.0, "LDR>T###%%> [    0.062740] unable to find tag: 0x30444957");                // 실제 리셋은 앞 무음 0.625초 이상(구간 안 로더 표시는 0.5초 이상일 때만 실시간)
            g.L(2.01, "[    0.066655] unable to find tag: 0x31444957");
            g.L(2.1, "[    1.204511] ubi0: attached mtd5");
            g.L(100, "[   99.000000] reboot: Restarting system");                            // ② Restarting → soft
            g.L(100.1, "LDR>T###%%");
            g.L(201, "LDR>T###%%");                                                         // ③ → hard
            g.Tx(300, "reboot"); g.L(300.05, "reboot");                                     // ④ cmd 61초 뒤 → soft(구간 안 Restarting 은 커널 시계 160초와 맞음 = 실시간)
            g.L(361, "[  160.000000] reboot: Restarting system");
            g.L(361.1, "LDR>T###%%");
            g.L(400, Pr, true);                                                             // ⑤ 프롬프트 tail flush 뒤 'reboot' 에코 + 2초 안 송신 CR(화살표로 다시 부름 — 송신 줄에 ESC)
            g.Tx(400.9, "", true);
            g.L(401, "reboot");
            g.L(402, "LDR>T###%%");
            g.L(500, Pr, true);                                                             // ⑥ 송신 CR 없이 수신 'reboot' 만 → 명령 아님(L2) → hard
            g.L(500.5, "reboot");
            g.L(501, "LDR>T###%%");
            g.L(600, "root@EVB-A1:/$ [End] /etc/init.d/K00_Sys");                           // ⑦ EVB-A1 프롬프트 재출력은 명령 아님 → soft
            g.L(600.5, "[   99.000000] reboot: Restarting system");
            g.L(601, "LDR>T###%%");
            g.Tx(700.5, "printf '__U''H_B_%s\\n' r9; sh -c 'reboot -f' </dev/null; printf '__U''H_E_%s_%s\\n' r9 $?");   // ⑧ Claude run → cmd
            g.L(701, "LDR>T###%%");
            g.L(799, Pr, true);
            g.Tx(800, "echo reboot"); g.L(800.05, "echo reboot"); g.L(800.06, "reboot"); g.L(800.1, Pr, true);              // ⑨ 명령 자리 아님 → hard
            g.Tx(800.5, "ps | grep reboot"); g.L(800.55, "ps | grep reboot"); g.L(800.6, "  812 root      2436 S    grep reboot"); g.L(800.7, Pr, true);
            g.L(801, "LDR>T###%%");
            g.Tx(900, "busybox reboot");                                                    // ⑩ busybox reboot → cmd
            g.L(901, "LDR>T###%%");
            g.L(1000, Pr, true); g.Tx(1000.5, "reset"); g.L(1000.55, "reset"); g.L(1000.7, Pr, true);   // ⑪ 셸의 reset 은 명령 아님 → hard
            g.L(1001, "LDR>T###%%");
            g.L(1100, "evb: ", true); g.Tx(1100.5, "reset"); g.L(1100.55, "reset");         // ⑫ U-Boot reset → cmd
            g.L(1101, "resetting ...");
            g.L(1101.5, "LDR>T###%%");
            g.Tx(1200, "reboot"); g.L(1200.05, "reboot");                                   // ⑬ 느린 종료: Restarting(59초)이 창을 이음 → 62초 뒤 부팅도 cmd
            g.L(1259, "[  157.500000] reboot: Restarting system");
            g.L(1262, "LDR>T###%%");
            g.L(1300, "LDR>T###%%");                                                        // ⑭ LDR>T 20초 뒤 커널 첫 줄 = 새 부팅(병합은 15초까지)
            g.L(1320, "[    0.050000] x");
            g.L(1400, "[   70.000000] app tick");
            g.L(1500, "[    0.000000] Booting Linux on physical CPU 0x0");                    // ⑮ 로더 표시 없는 보드: 커널 첫 줄 → hard
            g.L(1500.01, "[    0.010000] Linux version 4.19.91");
            g.L(1600, "LDR>T###%%"); g.L(1601.2, "LDR>T###%%");                             // ⑯ 부팅 반복: LDR>T 마다 리셋(병합 창 1.5초 안이어도)
            var eg = g.Evs().Where(x => x.Kind == "boot").ToList();
            string wantBoots = "boot:cmd,boot:soft,boot:hard,boot:soft,boot:cmd,boot:hard,boot:soft,boot:cmd,boot:hard,boot:cmd,boot:hard,boot:cmd,boot:cmd,boot:hard,boot:hard,boot:hard,boot:hard,boot:hard";
            ctx.Check(g.Kinds() == wantBoots,
                      "boot 분류: ①cmd ②soft ③hard ④cmd 61초 뒤 soft ⑤에코+송신 CR cmd ⑥송신 CR 없음 hard ⑦EVB-A1 재출력 soft ⑧sh -c 'reboot -f' cmd ⑨echo/grep reboot hard ⑩busybox reboot cmd ⑪셸 reset hard ⑫U-Boot reset cmd ⑬Restarting 이 창을 이음 cmd ⑭LDR>T 20초 뒤 커널 줄 새 부팅 ⑮커널 첫 줄 hard ⑯4초 반복 LDR>T 둘 다 (" + g.Kinds() + ")");
            var softHard = eg.Where(x => x.Class != "cmd").ToList();
            ctx.Check(eg.Select(x => x.BootSeq).SequenceEqual(Enumerable.Range(1, eg.Count).Select(x => (long)x)) && eg.Where(x => x.Class == "cmd").All(x => x.Context == null)
                      && softHard.All(x => x.Context != null && File.Exists(x.Context) && Path.GetFileName(x.Context).Contains("_boot") && x.ContextSkipped == null),
                      "boot: boot_seq 1~" + eg.Count + ", cmd 는 앞뒤 로그 없음, soft·hard " + softHard.Count + "개 모두 앞 줄 파일(…_boot.txt — rate 예산에서 뺌, D6)");
            var bl0 = Lines(softHard[0].Context);
            ctx.Check(bl0.Any(l => l.StartsWith(">>> ", StringComparison.Ordinal) && l.EndsWith("LDR>T###%%", StringComparison.Ordinal)) && bl0.Any(l => l.Contains("reboot: Restarting system"))
                      && bl0.Last().StartsWith("# 끝 — 부팅 soft 앞 줄(", StringComparison.Ordinal), "soft 부팅 앞 줄 파일: 직전 줄(Restarting)·'>>>' 부팅 줄·'# 끝 — 부팅 soft 앞 줄'");
            ctx.Check(eg[1].SinceBootS == 0 && g.Evs().First(x => x.Kind == "boot").SinceBootS == 0 && eg[2].SilenceS == 100.9 && eg[1].SilenceS == null,
                      "since_boot_s: 부팅 줄은 0(단조 시각 기준, D11) · 원인 없는 hard 는 리셋 전 무음 silence_s(③ 100.9초), soft 는 없음");

            // ---- 판정(부록 C.7): 확실한 재출력·에코는 판정하지 않음, 명령 출력 구간 안 사건 줄은 모두 suspect(커널 시각으로 받아들이지 않음),
            //      로더 표시는 언제나 실시간 리셋(구간을 닫음), 재부팅 분류는 리셋 순간의 되돌아보기로만(cause·soft·silence_s)
            var h = new Feeder(ctx, dir, 18608);
            h.L(0, "LDR>T###%%");                                                           // 부팅 #1
            h.L(1, "[    0.900000] Booting Linux on physical CPU 0x0");
            h.L(20, "root@EVB-A1:/$ ", true);
            h.Tx(30, "grep -c 'Restarting system' $C");                                     // 에코 안의 'Restarting system'
            h.L(30.05, "root@EVB-A1:/$ grep -c 'Restarting system' $C");
            h.L(30.1, "0");
            bool region1 = h.S.InRegion;
            h.L(30.2, "root@EVB-A1:/$ ", true);
            bool closed1 = !h.S.InRegion;
            h.Tx(40, "cat /proc/kmsg; grep -n 'Kernel panic' $D");                           // 확실한 재출력: <N>[ts](grep -n 줄번호·파일 이름이 앞에 붙어도)
            h.L(40.05, "root@EVB-A1:/$ cat /proc/kmsg; grep -n 'Kernel panic' $D");
            h.L(40.1, "<0>[   40.000000] Kernel panic - not syncing: kmsg copy");
            h.L(40.2, "623:<0>[   72.843857] Kernel panic - not syncing: dumptest");
            h.L(40.3, "/mnt/mmc/evb_debug/pstore/recent_0/dmesg-ramoops-0:7:<4>[   12.000000] Internal error: Oops: 5 [#1] PREEMPT SMP");
            h.L(40.4, "<6>[    0.000000] Booting Linux on physical CPU 0x0");
            h.L(40.5, "root@EVB-A1:/$ ", true);
            int afterReplay = h.Evs().Count;
            h.Tx(150, "dmesg");                                                             // 로그 보기 출력 = suspect, 덤프 속 프롬프트(개행 따라옴)는 구간을 닫지 않음(D7)
            h.L(150.05, "root@EVB-A1:/$ dmesg");
            h.L(150.1, "[    0.000000] Booting Linux on physical CPU 0x0");
            h.L(150.11, "[   12.000000] Internal error: Oops: 5 [#1] PREEMPT SMP");
            h.L(150.115, "root@EVB-A1:/$ reboot");
            h.L(150.12, "[   88.605106] reboot: Restarting system");
            bool inDump = h.S.InRegion, pendAfterDump = h.S.PendingConfirm;
            h.L(150.2, "root@EVB-A1:/$ ", true);                                            // 셸이 살아 있음 → 덤프 속 Oops 는 확인 대기에서 빠짐
            bool pendCleared = !h.S.PendingConfirm;
            h.Tx(160, "dmesg -w");                                                          // 구간 안 지금 시각의 panic 도 suspect → 뒤따른 리셋이 cause 로 확인
            h.L(160.05, "root@EVB-A1:/$ dmesg -w");
            h.L(160.1, "[  100.000000] old line");
            h.L(170, "[  170.000000] Kernel panic - not syncing: live during dmesg -w");
            h.L(171, "LDR>T###%%");
            bool regionAfterBoot = h.S.InRegion;
            h.L(180, "root@EVB-A1:/$ ", true);
            h.Tx(181, "cat /mnt/mmc/app.log");                                              // 커널 시각 없는 앱 오류 = suspect
            h.L(181.05, "root@EVB-A1:/$ cat /mnt/mmc/app.log");
            h.L(181.1, "APP_ABORT reason=TEST_ALIVE");
            h.L(183, "[    1.354766] - e?LDR>T###%%");                                     // 깨진 줄 끝의 로더 표시도 리셋(B5) — 구간 안이어도
            h.L(184.5, "LDR>T###%%");                                                       // 1.5초 뒤 또 = 별개 리셋(B10)
            var eh = h.Evs().Where(x => x.Kind != "attach").ToList();
            ctx.Check(region1 && closed1 && afterReplay == 1 && inDump && pendAfterDump && pendCleared && !regionAfterBoot
                      && h.K() == "boot:hard,boot:hard?,oops?,panic?,boot:hard+panic,app_error?,boot:hard,boot:hard",
                      "판정(C.7): 에코·<N>[ts](kmsg·grep -n·파일:줄:) 사건 0, dmesg 출력의 옛 [0.0]·Oops = suspect(덤프 속 프롬프트+reboot·Restarting 은 구간을 닫지 않음·soft 로 안 감), "
                      + "dmesg -w 의 지금 시각 panic 도 suspect → 리셋이 cause 로 확인(hard+panic), 앱 로그의 APP_ABORT = suspect, 깨진 줄 끝 LDR>T·1.5초 뒤 LDR>T 도 리셋 (" + h.K() + ")");
            var hb = eh.Where(x => x.Kind == "boot" && x.Suspect == null).ToList();
            ctx.Check(eh.Where(x => x.Suspect != null).All(x => x.Suspect == "cmd_output" && !EventKinds.Notifiable(x.Kind, x.Class, x.ContextSkipped, x.Suspect) && x.Context != null)
                      && hb.Select(x => x.BootSeq).SequenceEqual(new long[] { 1, 2, 3, 4 }) && hb[1].CauseKind == "panic" && hb[1].CauseLine.Contains("live during dmesg -w")
                      && hb[1].CauseTs != null && hb[1].SilenceS == null && hb[2].SilenceS == 1.9 && hb[3].SilenceS == 1.5
                      && EventKinds.Notifiable(hb[1].Kind, hb[1].Class, hb[1].ContextSkipped, hb[1].Suspect, hb[1].CauseKind) && EventKinds.Label("boot", "hard", hb[1].CauseKind) == "panic 후 리셋(hard)"
                      && EventBalloons.What("boot", "hard", hb[1].CauseKind) == "panic 뒤 재부팅" && !EventKinds.Notifiable(hb[2].Kind, hb[2].Class, hb[2].ContextSkipped, hb[2].Suspect, hb[2].CauseKind),
                      "suspect 사건: cmd_output·풍선 없음·앞뒤 로그 있음·부팅 상태 안 바꿈(실시간 부팅 #1~4), 리셋의 cause{kind,line,ts}·'panic 뒤 재부팅' 풍선, cause 없는 hard 는 silence_s(1.9·1.5초)·풍선 없음(기록만)");

            // ---- 구간 밖 커널 시각 거르기(로더 뒤 경과 +5초 넘게 앞섬·120초 넘게 뒤짐 = suspect, 실시간으로 받아들이지는 않음) · 로더 없는 보드 · confirm
            var kf = new Feeder(ctx, dir, 18672);
            kf.L(0, "LDR>T###%%");
            kf.L(30, "[   30.000000] tick");
            kf.L(60, "[ 4000.000000] Kernel panic - not syncing: printed by a boot script");      // 로더 뒤 60초에 4000초 = 앞섬
            kf.L(300, "[   10.000000] Internal error: Oops: old copy");                          // 이 부팅 실시간 최대 30초보다 2초 넘게 작음 = 뒤로 감
            kf.L(400, "[  399.000000] BUG: live one");
            var nl = new Feeder(ctx, dir, 18673);                                                  // 로더 표시 없는 보드: 커널 첫 줄 = 부팅, 구간 안이면 앞 무음 1초 이상일 때만
            nl.L(0, "[    0.000000] Booting Linux on physical CPU 0x0");
            nl.L(50, "root@SIMEV:~# ", true);
            nl.Tx(60, "tail -f /var/log/messages"); nl.L(60.05, "root@SIMEV:~# tail -f /var/log/messages"); nl.L(60.1, "Nov  5 17:16:00 app: hello");
            nl.L(70, "[    0.000000] Booting Linux on physical CPU 0x0");                         // 10초 무음 뒤 = 실시간 리셋
            nl.L(80, "root@SIMEV:~# ", true);
            nl.Tx(90, "dmesg"); nl.L(90.05, "root@SIMEV:~# dmesg"); nl.L(90.1, "[    0.000000] Booting Linux on physical CPU 0x0");   // 바로 앞 수신 = suspect
            var cf = new Feeder(ctx, dir, 18674);                                                  // suspect panic 뒤 30초 무응답 → confirm, 프롬프트가 오면 취소
            cf.L(0, "LDR>T###%%"); cf.L(10, "root@EVB-A1:/$ ", true);
            cf.Tx(20, "./crash_test.sh"); cf.L(20.05, "root@EVB-A1:/$ ./crash_test.sh");
            cf.L(20.1, "[   20.100000] Kernel panic - not syncing: sysrq triggered crash");
            cf.Tick(40); int beforeQuiet = cf.Evs().Count(x => x.Kind == "confirm");
            cf.Tick(50.2);
            cf.L(100, "root@EVB-A1:/$ ", true);
            cf.Tx(110, "cat /sys/fs/pstore/console-ramoops-0"); cf.L(110.05, "root@EVB-A1:/$ cat /sys/fs/pstore/console-ramoops-0");
            cf.L(110.1, "[   88.000000] Kernel panic - not syncing: old"); cf.L(110.2, "root@EVB-A1:/$ ", true);
            cf.Tick(200);
            var ce = cf.Evs().Where(x => x.Kind == "confirm").ToList();
            ctx.Check(kf.K() == "boot:hard,panic?,oops?,bug" && nl.K() == "boot:hard,boot:hard,boot:hard?" && beforeQuiet == 0 && ce.Count == 1 && ce[0].Of == "panic"
                      && ce[0].Line.Contains("sysrq triggered crash") && ce[0].Context != null && ce[0].SilenceS >= 30 && EventKinds.Notifiable("confirm", null, null, null)
                      && EventKinds.Label("confirm", null, null, "panic") == "panic 뒤 응답 없음(멈춤 의심)" && EventBalloons.What("confirm", null, null, "panic") == "panic 뒤 응답 없음.",
                      "구간 밖 거르기: 로더 뒤 60초의 [4000](앞섬)·실시간 최대 30초 뒤의 [10](커널 시계가 뒤로) = suspect, [399] = 실시간(" + kf.K() + ") · 로더 없는 보드: 커널 첫 줄 = 부팅, 구간 안 무음 10초 뒤 = 실시간·바로 앞 수신 = suspect("
                      + nl.K() + ") · suspect panic 뒤 30초 무응답 → confirm(of panic·앞뒤 로그·풍선 '멈춤 의심'), 덤프 뒤 프롬프트가 오면 confirm 없음");

            // ---- 검증자 탐침(scratchpad vprobe R1~R9·N1~N11 이식): 진짜 리셋·panic 빠짐 0, 확실한 재출력·에코 0, 오판은 suspect 로만(N2b 는 아래 주석)
            const string PR = "root@EVB-A1:/$ ";
            string RunR(int port, Action<Feeder> body) { var rf = new Feeder(ctx, dir, port); body(rf); return rf.K(); }
            void Att(Feeder f) => f.S.OnAttach("start", "COM9", null, null, T0, 0, "1:0");
            var rr = new List<(string Name, string Got, string Want)>
            {
                ("R1 dmesg|more 75초 뒤 진짜 panic", RunR(18660, f => {
                    f.L(0, "LDR>T###%%"); f.L(1, "[    0.012345] Booting Linux on physical CPU 0x0");
                    f.L(10, PR, true); f.L(995, "[  994.000000] live msg"); f.L(999, PR, true);
                    f.Tx(1000, "dmesg | more"); f.L(1000.05, PR + "dmesg | more");
                    f.L(1000.1, "[    0.000000] Booting Linux on physical CPU 0x0"); f.L(1000.1, "[    0.000000] Linux version 4.19.148");
                    f.L(1000.2, "--More-- ", true); f.Tick(1061); f.L(1075, PR, true);
                    f.L(1200, "[ 1199.000000] Kernel panic - not syncing: REAL LIVE PANIC"); f.L(1200, "[ 1199.001000] Rebooting in 10 seconds.."); f.L(1211, "LDR>T###%%"); }),
                 "boot:hard,boot:hard?,panic,boot:hard+panic"),
                ("R1b dmesg -w 90초 뒤 진짜 oops", RunR(18661, f => {
                    f.L(0, "LDR>T###%%"); f.L(1, "[    0.012345] Booting Linux on physical CPU 0x0"); f.L(10, PR, true); f.L(500, "[  500.000000] live msg"); f.L(999, PR, true);
                    f.Tx(1000, "dmesg -w"); f.L(1000.05, PR + "dmesg -w"); f.L(1000.1, "[    0.000000] Booting Linux on physical CPU 0x0"); f.L(1000.2, "[  500.000000] live msg");
                    f.Tick(1061); f.Tick(1090); f.L(1091, "^C"); f.L(1091.5, PR, true);
                    f.L(1300, "[ 1299.000000] Unable to handle kernel NULL pointer dereference at virtual address 0000000000000008"); f.L(1300, "[ 1299.000100] Internal error: Oops: 96000005 [#1] PREEMPT SMP"); }),
                 "boot:hard,boot:hard?,oops"),
                ("R2 붙은 직후 더 길었던 옛 부팅 ramoops cat → 조용한 리셋", RunR(18662, f => {
                    Att(f); f.L(1, "[ 1000.000000] live msg after attach"); f.L(2, PR, true);
                    f.Tx(10, "cat /sys/fs/pstore/console-ramoops-0"); f.L(10.05, PR + "cat /sys/fs/pstore/console-ramoops-0");
                    f.L(10.1, "[    5.000000] old boot early"); f.L(10.1, "[ 2999.000000] Kernel panic - not syncing: OLD PANIC");
                    f.L(10.1, "[ 3000.000000] reboot: Restarting system"); f.L(10.6, PR, true); f.L(40, "LDR>T###%%"); }),
                 "panic?,boot:hard"),
                ("R2b 가동 시각 근처의 옛 줄(ramoops cat)", RunR(18663, f => {
                    f.L(0, "LDR>T###%%"); f.L(1, "[    0.012345] Booting Linux"); f.L(40, "[   39.000000] live msg"); f.L(55, PR, true);
                    f.Tx(60, "cat /sys/fs/pstore/console-ramoops-0"); f.L(60.05, PR + "cat /sys/fs/pstore/console-ramoops-0");
                    f.L(60.1, "[   30.000000] old boot line"); f.L(60.1, "[   65.000000] Kernel panic - not syncing: OLD PANIC"); f.L(60.1, "[   66.000000] reboot: Restarting system");
                    f.L(60.6, PR, true); f.L(90, "LDR>T###%%"); }),
                 "boot:hard,panic?,boot:hard"),
                ("R3 붙은 직후 cat /proc/kmsg 중 진짜 panic+리셋(지난 줄 뒤 15초 무음 = 구간 닫힘 → 실시간)", RunR(18664, f => {
                    Att(f); f.L(1, PR, true); f.Tx(5, "cat /proc/kmsg"); f.L(5.05, PR + "cat /proc/kmsg"); f.L(5.1, "<6>[  100.000000] old kmsg");
                    f.L(20, "[  220.000000] Kernel panic - not syncing: REAL PANIC ON CONSOLE"); f.L(20, "<0>[  220.000000] Kernel panic - not syncing: REAL PANIC ON CONSOLE");
                    f.L(20.1, "[  220.010000] Rebooting in 10 seconds.."); f.L(31, "LDR>T###%%"); f.L(32, "[    0.012345] Booting Linux"); f.L(38, PR, true); }),
                 "panic,boot:hard+panic"),
                ("R4 tail -f 중 조용한 리셋", RunR(18665, f => {
                    f.L(0, "LDR>T###%%"); f.L(1, "[    0.012345] Booting Linux"); f.L(500, "[  499.000000] live"); f.L(999, PR, true);
                    f.Tx(1000, "tail -f /var/log/messages"); f.L(1000.05, PR + "tail -f /var/log/messages"); f.L(1000.1, "Nov  5 17:16:00 EVB-A1 user.info app: hello");
                    f.L(1020, "LDR>T###%%"); f.L(1021, "[    0.012345] Booting Linux"); f.L(1026, PR, true); }),
                 "boot:hard,boot:hard"),
                ("R5 rate 로 줄인 bug 묶음 10초 뒤 panic", RunR(18666, f => {
                    f.L(0, "LDR>T###%%");
                    for (int i = 0; i < 4; i++) f.L(100 + i * 63, "[  " + (100 + i * 63) + ".000000] rcu: INFO: rcu_preempt detected stalls on CPUs/tasks:");
                    f.L(299, "[  299.000000] Kernel panic - not syncing: REAL PANIC AFTER STALL"); }),
                 "boot:hard,bug,bug,bug,bug[rate],panic"),
                ("R6 부팅마다 panic 하나(300초마다 5번)", RunR(18667, f => {
                    for (int b5 = 0; b5 < 5; b5++) { double t0 = b5 * 300; f.L(t0, "LDR>T###%%"); f.L(t0 + 1, "[    0.012345] Booting Linux"); f.L(t0 + 290, "[  289.000000] Kernel panic - not syncing: crash #" + b5); } }),
                 "boot:hard,panic," + string.Join(",", Enumerable.Repeat("boot:hard+panic,panic", 4))),
                ("R7 명령 글자와 같은 kmsg 쓰기(에코)", RunR(18668, f => {
                    f.L(0, "LDR>T###%%"); f.L(1, "[    0.012345] Booting Linux"); f.L(50, PR, true);
                    f.Tx(60, "echo 'Out of memory: Kill' > /dev/kmsg"); f.L(60.05, PR + "echo 'Out of memory: Kill' > /dev/kmsg"); f.L(60.1, "Out of memory: Kill"); }),
                 "boot:hard"),
                ("R8 프롬프트+reboot 줄이 든 .log cat 뒤 조용한 리셋", RunR(18669, f => {
                    f.L(0, "LDR>T###%%"); f.L(1, "[    0.012345] Booting Linux"); f.L(50, PR, true);
                    f.Tx(60, "cat /mnt/mmc/uart_capture.log"); f.L(60.05, PR + "cat /mnt/mmc/uart_capture.log");
                    f.L(60.1, "[  300.000000] Kernel panic - not syncing: old"); f.L(60.1, PR + "reboot"); f.L(60.1, "[   30.000000] Kernel panic - not syncing: old2");
                    f.L(60.6, PR, true); f.L(80, "LDR>T###%%"); }),
                 "boot:hard,panic?,boot:hard"),
                ("R9 빈 Enter 뒤 진짜 panic(구간 안 → suspect, 리셋이 cause 로 확인)", RunR(18670, f => {
                    f.L(0, "LDR>T###%%"); f.L(1, "[    0.012345] Booting Linux"); f.L(60, "[   59.000000] live"); f.L(61, PR, true);
                    f.Tx(70, ""); f.L(70.01, PR, false); f.L(70.05, "[   69.050000] Kernel panic - not syncing: real"); f.L(76, "LDR>T###%%"); }),
                 "boot:hard,panic?,boot:hard+panic"),
                ("N1 화살표 회상·1초 쉬고 Enter, 옛 ramoops 덤프, 조용한 리셋", RunR(18675, f => {
                    f.L(0, "LDR>T###%%"); f.L(1, "[    0.012345] Booting Linux"); f.L(30, "[   29.000000] live"); f.L(40, PR, true);
                    f.L(59, PR + "cat /sys/fs/pstore/console-ramoops-0", true); f.Tx(60, "", true); f.L(60.01, "");
                    f.L(60.1, "[  400.000000] Kernel panic - not syncing: OLD PANIC FROM PREV BOOT"); f.L(60.1, "[  401.000000] reboot: Restarting system");
                    f.L(60.6, PR, true); f.L(90, "LDR>T###%%"); }),
                 "boot:hard,panic?,boot:hard"),
                ("N1c Ctrl-P 회상(송신 글자 없음·ESC 없음)", RunR(18676, f => {
                    f.L(0, "LDR>T###%%"); f.L(1, "[    0.012345] Booting Linux"); f.L(30, "[   29.000000] live"); f.L(40, PR, true);
                    f.Tx(60, ""); f.L(60.01, PR + "cat /sys/fs/pstore/console-ramoops-0");
                    f.L(60.1, "[  400.000000] Kernel panic - not syncing: OLD PANIC FROM PREV BOOT"); f.L(60.1, "[  401.000000] reboot: Restarting system");
                    f.L(60.6, PR, true); f.L(90, "LDR>T###%%"); }),
                 "boot:hard,panic?,boot:hard"),
                ("N2 재기준 사슬: 가동 60초에 옛 줄 50~63(panic 62.8·Restarting 63)", RunR(18677, f => {
                    f.L(0, "LDR>T###%%"); f.L(1, "[    0.012345] Booting Linux"); f.L(59, "[   58.900000] live"); f.L(59.5, PR, true);
                    f.Tx(60, "cat /sys/fs/pstore/console-ramoops-0"); f.L(60.02, PR + "cat /sys/fs/pstore/console-ramoops-0");
                    foreach (var x in new double[] { 50, 55, 57.5, 59.8, 61.2, 62.0 }) f.L(60.05, "[   " + x.ToString("0.000000", CultureInfo.InvariantCulture) + "] old line");
                    f.L(60.06, "[   62.800000] Kernel panic - not syncing: OLD PANIC"); f.L(60.06, "[   63.000000] reboot: Restarting system");
                    f.L(60.6, PR, true); f.L(90, "LDR>T###%%"); }),
                 "boot:hard,panic?,boot:hard"),
                ("N3 'cat console-ramoops-0; reboot'(옛 [0.064] 먼저) 뒤 진짜 reboot", RunR(18678, f => {
                    f.L(0, "LDR>T###%%"); f.L(1, "[    0.012345] Booting Linux"); f.L(300, "[  299.000000] live"); f.L(310, PR, true);
                    f.Tx(320, "cat /sys/fs/pstore/console-ramoops-0; reboot"); f.L(320.02, PR + "cat /sys/fs/pstore/console-ramoops-0; reboot");
                    f.L(320.05, "[    0.064356] unable to find tag: 0x30444957"); f.L(320.05, "[    7.976583] [sie_isr] 3 sts 80020880");
                    f.L(320.06, "[  501.456881] Kernel panic - not syncing: OLD"); f.L(320.06, "[  505.965870] reboot: Restarting system");
                    f.L(321.0, "[  320.500000] reboot: Restarting system"); f.L(324, "LDR>T###%%"); }),
                 "boot:hard,boot:cmd?,panic?,boot:cmd"),
                ("N4 에코 2.5초 늦음 뒤 덤프", RunR(18679, f => {
                    f.L(0, "LDR>T###%%"); f.L(1, "[    0.012345] Booting Linux"); f.L(300, "[  299.000000] live"); f.L(310, PR, true);
                    f.Tx(320, "cat /sys/fs/pstore/console-ramoops-0"); f.L(322.6, PR + "cat /sys/fs/pstore/console-ramoops-0");
                    f.L(322.7, "[  501.456881] Kernel panic - not syncing: OLD"); f.L(322.7, "[  505.965870] reboot: Restarting system");
                    f.L(323.2, PR, true); f.L(350, "LDR>T###%%"); }),
                 "boot:hard,panic?,boot:hard"),
                ("N5 전경 루프·로그인 프롬프트(prompt_re 아님) 뒤 진짜 panic+리셋", RunR(18680, f => {
                    f.L(0, "LDR>T###%%"); f.L(1, "[    0.012345] Booting Linux"); f.L(100, "[   99.000000] live"); f.L(110, PR, true);
                    f.Tx(120, "./aging_loop.sh"); f.L(120.02, PR + "./aging_loop.sh"); f.L(125, "loop 1 ok");
                    f.L(200, "LDR>T###%%"); f.L(200.3, "[    0.065213] unable to find tag"); f.L(205, "[    5.000000] boot line"); f.L(210, "EVB-A1 login: ", true);
                    f.L(400, "[  200.000000] Kernel panic - not syncing: REAL PANIC IN BOOT 2"); f.L(400.1, "[  200.100000] Rebooting in 10 seconds..");
                    f.L(406, "LDR>T###%%"); f.L(406.3, "[    0.065213] unable to find tag"); f.L(416, "EVB-A1 login: ", true);
                    f.L(700, "APPFAULT(ED)(0045) app died"); f.L(800, "LDR>T###%%"); }),
                 "boot:hard,boot:hard,panic,boot:hard+panic,app_error,boot:hard"),
                ("N7 붙은 직후 sysrq 로 일으킨 panic(일으킨 충돌 = 구간 밖, W7)", RunR(18681, f => {
                    Att(f); f.L(1, "[ 5000.000000] periodic live kernel msg"); f.L(30, "[ 5029.000000] periodic live kernel msg"); f.L(31, PR, true);
                    f.Tx(40, "echo c > /proc/sysrq-trigger"); f.L(40.02, PR + "echo c > /proc/sysrq-trigger");
                    f.L(40.1, "[ 5039.100000] sysrq: Trigger a crash"); f.L(40.1, "[ 5039.100100] Kernel panic - not syncing: sysrq triggered crash");
                    f.L(40.2, "[ 5039.200000] Rebooting in 10 seconds.."); f.L(46, "LDR>T###%%"); f.L(46.3, "[    0.065213] unable to find tag"); f.L(51, PR, true); }),
                 "panic,boot:hard+panic"),
                ("N8 logread -f 를 둔 채 조용한 리셋 2번(지난 줄 뒤 무음 10초 → 새 줄은 실시간)", RunR(18682, f => {
                    f.L(0, "LDR>T###%%"); f.L(1, "[    0.012345] Booting Linux"); f.L(60, "[   59.000000] live"); f.L(61, PR, true);
                    f.Tx(70, "logread -f"); f.L(70.02, PR + "logread -f"); f.L(70.1, "Nov  5 17:01:10 EVB-A1 user.info app: hello");
                    f.L(1000, "APPFAULT(ED)(0045) app died");
                    f.L(2000, "LDR>T###%%"); f.L(2000.3, "[    0.065213] unable to find tag"); f.L(2005, PR, true);
                    f.L(2100, "APPFAULT(ED)(0045) app died again"); f.L(5600, "LDR>T###%%"); f.L(5600.3, "[    0.065213] unable to find tag"); f.L(5605, PR, true); }),
                 "boot:hard,app_error,boot:hard,app_error,boot:hard"),
                ("N10 구간 밖 옛 줄 하나 뒤 sysrq 로 일으킨 panic(W7)", RunR(18683, f => {
                    f.L(0, "LDR>T###%%"); f.L(1, "[    0.012345] Booting Linux"); f.L(60, "[   59.000000] live"); f.L(61, PR, true);
                    f.L(70, "[ 4000.000000] some script printed an old kernel line"); f.L(71, PR, true);
                    f.Tx(80, "echo c > /proc/sysrq-trigger"); f.L(80.02, PR + "echo c > /proc/sysrq-trigger");
                    f.L(80.1, "[   79.100000] sysrq: Trigger a crash"); f.L(80.1, "[   79.100100] Kernel panic - not syncing: sysrq triggered crash");
                    f.L(80.2, "[   79.200000] Rebooting in 10 seconds.."); f.L(86, "LDR>T###%%"); }),
                 "boot:hard,panic,boot:hard+panic"),
                ("N11 LDR>T 1.5초 간격 두 번(로더 이중 리셋 — 둘 다 셈)", RunR(18684, f => {
                    f.L(0, "LDR>T###%%"); f.L(1.5, "LDR>T###%%"); f.L(1.8, "[    0.065213] unable to find tag"); f.L(6, PR, true); }),
                 "boot:hard,boot:hard"),
                // N2b: 보드에 저장된 UART 캡처 로그(LDR>T 포함)를 cat — 구간 안 로더 표시는 앞 무음 0.5초 이상일 때만 실시간(실제 리셋 225번은 모두 0.625초 이상)
                ("N2b 캡처 로그 cat 속 LDR>T(앞 무음 없음 → suspect 부팅)", RunR(18685, f => {
                    f.L(0, "LDR>T###%%"); f.L(1, "[    0.012345] Booting Linux"); f.L(59, "[   58.900000] live"); f.L(59.5, PR, true);
                    f.Tx(60, "cat /mnt/mmc/capture.log"); f.L(60.02, PR + "cat /mnt/mmc/capture.log");
                    foreach (var x in new double[] { 57.5, 59.8, 61.2, 62.0 }) f.L(60.05, "[   " + x.ToString("0.000000", CultureInfo.InvariantCulture) + "] old line");
                    f.L(60.06, "[   62.800000] Kernel panic - not syncing: OLD PANIC"); f.L(60.06, "[   62.900000] Rebooting in 10 seconds..");
                    f.L(60.07, "LDR>T###%%"); f.L(60.07, "> [    0.065213] unable to find tag: 0x30444957"); f.L(60.08, "[    5.000000] old boot2 line");
                    f.L(60.08, "[   20.000000] Unable to handle kernel NULL pointer dereference at virtual address 0000000000000008");
                    f.L(60.09, "[   21.000000] reboot: Restarting system"); f.L(60.6, PR, true); f.L(120, "LDR>T###%%"); }),
                 "boot:hard,panic?,boot:hard?,oops?,boot:hard"),
                // ---- C.7 뒤속: 구간 10초 무음 닫힘(송신 바이트로 다시 열림)·커널 시계 단조 검사(절전·깨어남)
                ("S1 절전 1시간 뒤 깨어남: 커널 시각이 경과보다 훨씬 작아도 실시간(뒤로 가지 않으면)", RunR(18686, f => {
                    f.L(0, "LDR>T###%%"); f.L(1, "[    0.900000] Booting Linux"); f.L(30, "[   29.000000] live"); f.L(31, PR, true);
                    f.L(3631, "[   30.500000] PM: resume devices took 0.100 seconds"); f.L(3640, "[   39.500000] Kernel panic - not syncing: after resume");
                    f.L(3650, "LDR>T###%%"); f.L(3651, "[    0.900000] Booting Linux"); f.L(3700, "[   49.000000] live");
                    f.L(7300, "[   50.000000] rcu: INFO: rcu_preempt detected stalls on CPUs/tasks:"); }),
                 "boot:hard,panic,boot:hard+panic,bug"),
                ("FG 전경 루프 5분 뒤 진짜 panic(10초 무음 뒤 구간 닫힘)", RunR(18687, f => {
                    f.L(0, "LDR>T###%%"); f.L(1, "[    0.900000] Booting Linux"); f.L(10, PR, true);
                    f.Tx(20, "./aging_loop.sh"); f.L(20.05, PR + "./aging_loop.sh"); f.L(20.1, "loop start"); f.L(80, "loop 1 ok"); f.L(140, "loop 2 ok");
                    f.L(320, "[  320.000000] Kernel panic - not syncing: real during loop"); f.L(330, "LDR>T###%%"); }),
                 "boot:hard,panic,boot:hard+panic"),
                ("MORE dmesg|more 30초 쉼·스페이스·q: 옛 쪽은 suspect, 쉬는 동안의 실시간 BUG 는 실시간", RunR(18688, f => {
                    f.L(0, "LDR>T###%%"); f.L(1, "[    0.900000] Booting Linux"); f.L(100, "[   99.000000] live"); f.L(101, PR, true);
                    f.Tx(110, "dmesg | more"); f.L(110.05, PR + "dmesg | more");
                    f.L(110.1, "[    0.000000] Booting Linux on physical CPU 0x0"); f.L(110.1, "[   12.000000] Internal error: Oops: old page1"); f.L(110.2, "--More-- ", true);
                    f.L(130, "[  129.000000] BUG: live during pager pause");
                    f.Key(140); f.L(140.05, "[   50.000000] Kernel panic - not syncing: old page2"); f.L(140.1, "--More-- ", true);
                    f.Key(170); f.L(170.05, PR, true); f.Tick(250); }),
                 "boot:hard,boot:hard?,oops?,bug,panic?"),
                ("TAILF tail -f 의 지난 줄은 suspect, 그 뒤 붙는 줄은 실시간", RunR(18689, f => {
                    f.L(0, "LDR>T###%%"); f.L(1, "[    0.900000] Booting Linux"); f.L(10, PR, true);
                    f.Tx(200, "tail -f /var/log/messages"); f.L(200.05, PR + "tail -f /var/log/messages");
                    f.L(200.1, "Nov  5 17:00:00 app: APP_ABORT reason=OLD"); f.L(200.1, "Nov  5 17:00:01 app: APPFAULT(ED)");
                    f.L(260, "Nov  5 17:04:20 app: APP_ABORT reason=NEW"); }),
                 "boot:hard,app_error?,app_error"),
                ("STALL 멈춘 보드에 친 명령(에코 없음) 뒤 rcu stall 은 실시간(EVB-B2 rcu)", RunR(18690, f => {
                    f.L(0, "LDR>T###%%"); f.L(26, "[   26.000000] rcu: INFO: rcu_preempt detected stalls on CPUs/tasks:");
                    f.Tx(40, "echo ===DIAG_BEGIN===; dmesg | tail");
                    f.L(89, "[   89.000000] rcu: INFO: rcu_preempt detected stalls on CPUs/tasks:"); f.L(152, "[  152.000000] rcu: INFO: rcu_preempt detected stalls on CPUs/tasks:"); }),
                 "boot:hard,bug,bug,bug"),
                // ---- 최종 검증 탐침(scratchpad vprobe3 Q1~Q14 이식)
                ("Q14 EVB-B2 유휴 프롬프트 뒤 조용한 리셋: 'LDR>T###%%> ' 가 열린 채 끊김(로더 0.5초 멈춤, BLK-1)", RunR(18691, f => {
                    const string CP = "root@EVB-B2:/$ ";
                    f.L(0, "LDR>T###%%> [    0.329104] "); f.L(0.6, "[    0.329104]  compile 12:02:08 Sep 16 2026 (kernel)"); f.L(10, CP, true);
                    f.L(30, "[   29.000000] live"); f.L(31, CP, true);
                    f.L(100.4, "LDR>T###%%> ", true); f.L(100.5, "[    0.329104] "); f.L(100.51, "[    0.329104]  compile 12:02:08 Sep 16 2026 (kernel)"); f.L(104.7, "EVB-B2 Linux shell..."); f.L(110, CP, true); }),
                 "boot:hard,boot:hard"),
                ("Q14c EVB-A1 유휴 프롬프트 뒤 'LDR>T###%%' 열린 채 끊김", RunR(18692, f => {
                    f.L(0, "LDR>T###%%> [    0.065213] unable to find tag"); f.L(10, PR, true); f.L(30, "[   29.000000] live"); f.L(31, PR, true);
                    f.L(100.4, "LDR>T###%%", true); f.L(100.5, "> [    0.065213] unable to find tag: 0x30444957"); f.L(100.51, "[    0.069133] unable to find tag: 0x31444957"); f.L(105, PR, true); }),
                 "boot:hard,boot:hard"),
                ("Q1 tail -3 ramoops(옛 Restarting) → 프롬프트 → 5초 뒤 'echo b > sysrq'(BLK-2)", RunR(18693, f => {
                    f.L(0, "LDR>T###%%"); f.L(0.25, "> [    0.065213] unable to find tag: 0x30444957"); f.L(4.5, PR, true); f.L(40, "[   39.000000] live"); f.L(45, PR, true);
                    f.Tx(48, "tail -3 /sys/fs/pstore/console-ramoops-0"); f.L(48.05, PR + "tail -3 /sys/fs/pstore/console-ramoops-0");
                    f.L(48.1, "[    1.347113] ubi: mtd8 is already attached to ubi1"); f.L(48.1, "[   47.665628] reboot: Restarting system"); f.L(48.5, PR, true);
                    f.Tx(53, "echo b > /proc/sysrq-trigger"); f.L(53.05, "echo b >[   52.530984] sysrq: Resetting"); f.L(53.74, "LDR>T###%%"); }),
                 "boot:hard,boot:hard"),
                ("Q1b 한 줄 'tail -3 ramoops; echo b > sysrq'(BLK-2)", RunR(18694, f => {
                    f.L(0, "LDR>T###%%"); f.L(4.5, PR, true); f.L(40, "[   39.000000] live"); f.L(45, PR, true);
                    f.Tx(48, "tail -3 /sys/fs/pstore/console-ramoops-0; echo b > /proc/sysrq-trigger"); f.L(48.05, PR + "tail -3 /sys/fs/pstore/console-ramoops-0; echo b > /proc/sysrq-trigger");
                    f.L(48.1, "[   47.665628] reboot: Restarting system"); f.L(48.12, "[   47.700000] sysrq: Resetting"); f.L(48.8, "LDR>T###%%"); }),
                 "boot:hard,boot:hard"),
                ("Q1c cat ramoops(옛 Restarting) → 프롬프트 → 6초 뒤 와치독 리셋(BLK-2)", RunR(18695, f => {
                    f.L(0, "LDR>T###%%"); f.L(4.5, PR, true); f.L(40, "[   39.000000] live"); f.L(45, PR, true);
                    f.Tx(48, "cat /sys/fs/pstore/console-ramoops-0"); f.L(48.05, PR + "cat /sys/fs/pstore/console-ramoops-0");
                    f.L(48.1, "[  505.965870] reboot: Restarting system"); f.L(48.5, PR, true); f.L(54.5, "LDR>T###%%"); }),
                 "boot:hard,boot:hard"),
                ("Q1d cat ramoops(옛 panic) 뒤 앱이 reboot(실시간 Restarting 0.7초 뒤 리셋 = soft)", RunR(18696, f => {
                    f.L(0, "LDR>T###%%"); f.L(4.5, PR, true); f.L(40, "[   39.000000] live"); f.L(45, PR, true);
                    f.Tx(48, "cat /sys/fs/pstore/console-ramoops-0"); f.L(48.05, PR + "cat /sys/fs/pstore/console-ramoops-0");
                    f.L(48.1, "[  505.965870] Kernel panic - not syncing: OLD"); f.L(48.5, PR, true); f.L(55, "[   54.000000] reboot: Restarting system"); f.L(55.7, "LDR>T###%%"); }),
                 "boot:hard,panic?,boot:soft"),
                ("Q2 more console-ramoops 의 옛 panic, 40초 읽다 q — confirm 없음(W1)", RunR(18697, f => {
                    f.L(0, "LDR>T###%%"); f.L(4.5, PR, true); f.L(40, "[   39.000000] live"); f.L(45, PR, true);
                    f.Tx(50, "more /sys/fs/pstore/console-ramoops-0"); f.L(50.05, PR + "more /sys/fs/pstore/console-ramoops-0");
                    f.L(50.1, "[  505.900000] Kernel panic - not syncing: OLD PANIC"); f.L(50.1, "[  505.965870] Rebooting in 10 seconds.."); f.L(50.2, "--More--(80%)", true);
                    f.Ticks(50.4, 95); f.Key(95); f.L(95.1, PR, true); }),
                 "boot:hard,panic?"),
                ("Q3 친 reboot, Restarting → LDR>T 0.45초(빠른 로더, reboot = 일으킨 리셋 W7)", RunR(18698, f => {
                    f.L(0, "LDR>T###%%"); f.L(4.5, PR, true); f.L(300, "[  299.000000] live"); f.L(310, PR, true);
                    f.Tx(320, "reboot"); f.L(320.02, PR + "reboot"); f.L(321, "[  320.900000] reboot: Restarting system"); f.L(321.45, "LDR>T###%%"); f.L(321.7, "> [    0.065213] unable to find tag"); f.L(326, PR, true);
                    f.L(400, "[   78.000000] Kernel panic - not syncing: REAL"); f.L(400.1, "[   78.100000] Rebooting in 10 seconds.."); f.L(406, "LDR>T###%%"); }),
                 "boot:hard,boot:cmd,panic,boot:hard+panic"),
                ("Q4 붙기 전 시작된 덤프 중에 붙음, 뒤 진짜 panic(단조 검사는 두 줄이 5초 넘게 맞을 때 켬, W3)", RunR(18699, f => {
                    Att(f); f.L(0.1, "[ 3000.000000] old line from a dump in progress"); f.L(0.1, "[ 3001.000000] old line 2"); f.L(0.5, PR, true);
                    f.L(30, "[  800.000000] real live line"); f.L(60, "[  830.000000] Kernel panic - not syncing: REAL"); f.L(60.1, "[  830.100000] Rebooting in 10 seconds.."); f.L(66, "LDR>T###%%"); }),
                 "panic,boot:hard+panic"),
                ("Q5 dmesg|more 15초 읽고 스페이스 → 둘째 쪽 옛 oops", RunR(18700, f => {
                    f.L(0, "LDR>T###%%"); f.L(4.5, PR, true); f.L(500, "[  499.000000] live"); f.L(505, PR, true);
                    f.Tx(510, "dmesg | more"); f.L(510.05, PR + "dmesg | more"); f.L(510.1, "[    1.000000] old line"); f.L(510.2, "--More--", true);
                    f.Ticks(510.4, 526); f.Key(526); f.L(526.05, "[  300.000000] Unable to handle kernel paging request at virtual address 0000dead"); f.L(526.1, "--More--", true);
                    f.Ticks(526.3, 530); f.Key(530); f.L(530.1, PR, true); }),
                 "boot:hard,oops?"),
                ("Q12 provoke-crash 로 일으킨 치명적이지 않은 oops(panic_on_oops=0, W7)", RunR(18701, f => {
                    f.L(0, "LDR>T###%%"); f.L(4.5, PR, true); f.L(100, "[   99.000000] live"); f.L(105, PR, true);
                    f.Tx(110, "echo EXCEPTION > /sys/kernel/debug/provoke-crash/DIRECT"); f.L(110.02, PR + "echo EXCEPTION > /sys/kernel/debug/provoke-crash/DIRECT");
                    f.L(110.1, "[  109.100000] Unable to handle kernel NULL pointer dereference at virtual address 0000000000000000"); f.L(110.1, "[  109.100100] Internal error: Oops: 96000045 [#1] PREEMPT SMP");
                    f.L(110.2, "Segmentation fault"); f.L(110.6, PR, true); f.Ticks(110.8, 160); }),
                 "boot:hard,oops"),
                // Q7: 'more' 로 보는 보드에 저장된 UART 캡처 — 페이저 쉼 뒤 쪽 첫 줄 LDR>T 는 앞 무음이 있어 실시간 리셋으로 친다(알려진 한계 W5)
                ("Q7 more capture.log 의 쪽 첫 LDR>T(알려진 한계 W5)", RunR(18702, f => {
                    f.L(0, "LDR>T###%%"); f.L(4.5, PR, true); f.L(500, "[  499.000000] live"); f.L(505, PR, true);
                    f.Tx(510, "more /mnt/mmc/capture.log"); f.L(510.05, PR + "more /mnt/mmc/capture.log"); f.L(510.1, "[   55.000000] Kernel panic - not syncing: OLD"); f.L(510.1, "[   55.100000] Rebooting in 10 seconds.."); f.L(510.2, "--More--", true);
                    f.Key(512); f.L(512.05, "LDR>T###%%"); f.L(512.05, "> [    0.065213] unable to find tag"); f.L(512.06, "[    3.000000] old boot2"); f.L(512.06, "[    4.000000] Kernel panic - not syncing: OLD2"); f.L(512.07, "--More--", true);
                    f.Key(515); f.L(515.05, "LDR>T###%%"); f.L(515.06, "--More--", true); f.Key(518); f.L(518.1, PR, true); }),
                 "boot:hard,panic?,boot:hard+panic,panic,boot:hard+panic"),
            };
            foreach (var x in rr) ctx.Check(x.Got == x.Want, x.Name + ": " + x.Got + (x.Got == x.Want ? "" : " (기대 " + x.Want + ")"));
            var r5 = EventLog.Read(dir, new[] { 18666 }).Last();
            var r6 = EventLog.Read(dir, new[] { 18667 }).Where(x => x.Kind != "attach").ToList();
            ctx.Check(r5.Kind == "panic" && r5.Context != null && r5.ContextSkipped == null && EventKinds.Notifiable(r5.Kind, null, r5.ContextSkipped, r5.Suspect)
                      && r6.All(x => x.Context != null && EventKinds.Notifiable(x.Kind, x.Class, x.ContextSkipped, x.Suspect, x.CauseKind) == (x.Kind != "boot" || x.CauseKind == "panic" || x.CauseKind == "oops")),
                      "R5: rate 로 파일 없이 묶던 bug 에 들어온 panic 은 그 skipped 를 물려받지 않음(앞뒤 로그·풍선, D5) · R6: 부팅마다 첫 panic·hard 부팅 앞 줄은 rate 예산에서 뺌(D6 — 원인 없는 부팅은 풍선 없이 기록만)");


            // ---- 앞뒤 로그 줄 수·30초·부팅 뒤 새 묶음
            var k = new Feeder(ctx, dir, 18610);
            for (int i = 0; i < 300; i++) k.L(i * 0.01, "line " + i);
            k.L(3.0, "Kernel panic - not syncing: count");
            for (int i = 0; i < 150; i++) k.L(3.01 + i * 0.01, "after " + i);
            var lk = Lines(k.Evs()[0].Context);
            var beforeL = lk.TakeWhile(l => !l.StartsWith(">>> ", StringComparison.Ordinal)).Where(l => l.StartsWith("    [", StringComparison.Ordinal)).ToList();
            var afterL = lk.SkipWhile(l => !l.StartsWith(">>> ", StringComparison.Ordinal)).Skip(1).Where(l => l.StartsWith("    [", StringComparison.Ordinal)).ToList();
            ctx.Check(beforeL.Count == 200 && beforeL[0].EndsWith("line 100", StringComparison.Ordinal) && afterL.Count == 100 && afterL.Last().EndsWith("after 99", StringComparison.Ordinal)
                      && lk.Last().StartsWith("# 끝 — 뒤 100줄 채움", StringComparison.Ordinal), "앞뒤 로그: 앞 200줄·사건 1줄·뒤 100줄·'# 끝 — 뒤 100줄 채움'");
            var m = new Feeder(ctx, dir, 18611);
            m.L(0, "Kernel panic - not syncing: cut");
            for (int i = 1; i <= 10; i++) m.L(i, "tail " + i);
            m.Tick(29.9);
            bool openAt29 = m.S.OpenContexts == 1;
            m.Tick(30.1);
            m.L(31, "late line");
            var lm = Lines(m.Evs()[0].Context);
            ctx.Check(openAt29 && m.S.OpenContexts == 0 && lm.Last().StartsWith("# 끝 — 30초 지남 (뒤 10줄", StringComparison.Ordinal) && !lm.Any(l => l.Contains("late line")), "30초 끊기");
            var q = new Feeder(ctx, dir, 18612, MergeCfg());
            q.L(0, "[  10.000000] Kernel panic - not syncing: before reset");
            q.L(5, "LDR>T###%%");
            q.L(8, "[    2.000000] Kernel panic - not syncing: in new boot");
            var eq = q.Evs();
            ctx.Check(q.Kinds() == "panic,boot:hard,panic" && eq[0].Context != eq[2].Context && Lines(eq[0].Context).Any(l => l.Contains("LDR>T")),
                      "panic → 리셋(hard) → 새 부팅의 panic = 새 사건·새 앞뒤 로그, 첫 파일은 부팅 줄까지 뒤 줄로");

            // ---- 예산(H4): 종류 rate·포트 hourly·폴더 disk
            var bud = new Feeder(ctx, dir, 18619, log: TempLog(ctx, "budget_hub.log"));
            bud.S.FreeFilesPerKindHour = 2; bud.S.KindRateS = 300; bud.S.MaxFilesPerPortHour = 4;
            for (int i = 0; i < 6; i++) bud.L(i * 40, "[  1.0] BUG: repeat " + i);               // 0·40 파일, 80~200 rate, (40+300=340 뒤) 파일
            bud.L(350, "[  1.0] BUG: repeat 6");
            bud.L(400, "[  1.0] Kernel panic - not syncing: p1");                                // 포트 4번째 파일
            bud.L(440, "Out of memory: Kill process 1");                                         // 포트 1시간 4개 넘음 → hourly
            var eb2 = bud.Evs();
            var bugs = eb2.Where(x => x.Kind == "bug").ToList();
            string skips = string.Join(",", eb2.Select(x => x.Context != null ? "f" : x.ContextSkipped));
            var l6 = Lines(bugs[6].Context);
            string hl = ReadLog(ctx, "budget_hub.log");
            ctx.Check(skips == "f,f,rate,rate,rate,rate,f,f,hourly" && bugs[6].Raw.Contains("\"skipped_before\":4") && l6[3].Contains("이 파일 전 같은 종류 4건은 앞뒤 로그 없이 jsonl 에만")
                      && InstallRig.Count(hl, "앞뒤 로그 줄임(bug) — 같은 부팅에서 같은 종류가 잦음") == 1 && InstallRig.Count(hl, "앞뒤 로그 줄임(oom) — 이 포트 앞뒤 로그가 1시간에 4개를 넘음") == 1,
                      "예산: 같은 부팅 같은 종류 2개 뒤 300초에 1개(rate 4건은 jsonl 에만·context_skipped)·다음 파일에 skipped_before 4·머리말, 부팅의 첫 panic 은 rate·hourly 면제, 포트 1시간 4개 넘으면 hourly, 걸릴 때 hub.log 경고 1줄 (" + skips + ")");
            bud.Tick(500);
            ctx.Check(Lines(bugs[6].Context).Last().Contains("이 파일 전 앞뒤 로그 생략 4건"), "'# 끝' 줄에 생략 수");
            var bdir = Path.Combine(ctx.Dir, "logs", "events_disk");
            Directory.CreateDirectory(bdir);
            File.WriteAllBytes(Path.Combine(bdir, "filler.bin"), new byte[5000]);
            var dk = new Feeder(ctx, bdir, 18620, log: TempLog(ctx, "disk_hub.log"), budget: new EventsBudget(bdir, 4096));
            dk.L(0, "Kernel panic - not syncing: disk full");
            var ed = dk.Evs();
            ctx.Check(ed.Count == 1 && ed[0].Context == null && ed[0].ContextSkipped == "disk" && ReadLog(ctx, "disk_hub.log").Contains("events 폴더가 0MB 를 넘음: 앞뒤 로그를 더 만들지 않음(지우지 않음, jsonl 에는 계속 기록)")
                      && File.Exists(Path.Combine(bdir, "filler.bin")), "예산 disk: 폴더가 max 를 넘으면 앞뒤 로그 없이 jsonl 만(context_skipped disk)·경고·아무것도 지우지 않음");

            // ---- 쓰기 실패(H2): 잠깐 막힘 → 다시 시도로 씀, 오래 막힘 → 대기열 → 풀리면 Tick 에 씀, 끝까지 막힘 → 버리고 hub.log 에 내용
            var wdir = Path.Combine(ctx.Dir, "logs", "events_w");
            Directory.CreateDirectory(wdir);
            string blk = Path.Combine(wdir, "18621.jsonl");
            Directory.CreateDirectory(blk);                                                 // 그 이름의 폴더 = 열 수 없음(UnauthorizedAccess)
            var w = new Feeder(ctx, wdir, 18621, log: TempLog(ctx, "write_hub.log"));
            var unblock = new Thread(() => { Thread.Sleep(300); Directory.Delete(blk); }) { IsBackground = true };
            unblock.Start();
            var sw = Stopwatch.StartNew();
            w.L(0, "Kernel panic - not syncing: transient");
            long ms1 = sw.ElapsedMilliseconds;
            bool w1 = w.Evs().Count == 1;
            // 두 번째 포트: 오래 막힘
            string blk2 = Path.Combine(wdir, "18622.jsonl");
            Directory.CreateDirectory(blk2);
            var w2 = new Feeder(ctx, wdir, 18622, log: TempLog(ctx, "write_hub.log"));
            sw.Restart();
            w2.L(0, "Kernel panic - not syncing: long block");
            long ms2 = sw.ElapsedMilliseconds;
            w2.L(1, "[  1.0] BUG: second while blocked");
            bool pending = !File.Exists(blk2) || Directory.Exists(blk2);
            Directory.Delete(blk2);
            w2.Tick(2);
            var ew2 = w2.Evs();
            string blk3 = Path.Combine(wdir, "18623.jsonl");
            Directory.CreateDirectory(blk3);
            var w3 = new EventScanner(wdir, 18623, "CABU", () => "SIMEV", () => null, EventPatterns.For(Config.Default(), "CABU"), Config.Default(), TempLog(ctx, "write_hub.log"));
            w3.OnLine("Kernel panic - not syncing: never written", new[] { "Kernel panic - not syncing: never written" }, T0, 0, "1:0");
            w3.Close("시험 끝");
            string wl = ReadLog(ctx, "write_hub.log");
            ctx.Check(w1 && ms1 >= 250 && ms1 < 2500 && ms2 >= 1900 && ms2 < 4000 && pending && ew2.Select(x => x.Kind).SequenceEqual(new[] { "panic", "bug" })
                      && wl.Contains("사건 기록 :18622 쓰기 실패") && wl.Contains("사건 기록 :18622 쓰기 회복") && wl.Contains("사건 기록 :18623 버림(닫을 때까지 쓰지 못함")
                      && wl.Contains("never written"),
                      "쓰기 실패: 0.3초 막힘 → 다시 시도로 씀(" + ms1 + "ms), 계속 막힘 → 2초 뒤 대기열(" + ms2 + "ms)·다음 줄도 대기 → 풀리면 Tick 에 순서대로, 끝까지 못 쓰면 hub.log '버림' + 사건 내용");

            // ---- missed
            var r = new Feeder(ctx, dir, 18613);
            r.L(0, "Kernel panic - not syncing: before loss");
            r.S.OnLost(123456, T0.AddSeconds(1), 1000, "1:999");
            var er = r.Evs();
            ctx.Check(r.Kinds() == "panic,missed" && er[1].Skipped == 123456 && er[1].Context == null && Lines(er[0].Context).Any(l => l.Contains("링 버퍼 놓침 123456바이트")), "missed");

            // ---- 재시작 뒤 이어 붙임·끊긴 마지막 줄(M3)·attach 공백(M4)
            var s1 = new Feeder(ctx, dir, 18614, MergeCfg());
            s1.L(0, "LDR>T###%%"); s1.L(10, "LDR>T###%%");
            s1.S.Close("허브 정지");
            AppendShared(s1.S.JsonlPath, Encoding.ASCII.GetBytes("{\"ts\":\"2026-11-05T17:00:11.000+09:00\",\"port\":18614,\"ki\0\0\0"));   // 전원 차단으로 끊긴 줄
            var s2 = new Feeder(ctx, dir, 18614, MergeCfg());
            s2.S.OnAttach("start", "COM5", 15.0, new DateTimeOffset(T0.AddSeconds(10)), T0.AddSeconds(25), 25000, "1:0");
            s2.L(25, "Kernel panic - not syncing: after restart");
            s2.S.OnDetach(null, T0.AddSeconds(30), 30000, "1:1");                           // 분리 기록(D8)
            s2.S.OnAttach("reattach", "COM5", 45.0, new DateTimeOffset(T0.AddSeconds(30)), T0.AddSeconds(75), 75000, "1:1");
            s2.L(76, "APPFAULT(ED)");
            var es = s2.Evs();
            var att = es.Where(x => x.Kind == "attach").ToList();
            ctx.Check(es.Count == 7 && es[3].Kind == "panic" && es[3].BootSeq == 2 && es[3].SinceBootS == 15 && att.Count == 2 && att[0].GapS == 15 && att[0].Com == "COM5" && att[1].Reason == "reattach"
                      && es[4].Kind == "detach" && es[6].Kind == "app_error" && es[6].SinceBootS == null && JsonlLines(s2.S.JsonlPath).Length == 8,
                      "재시작 뒤: 끊긴 마지막 줄(NUL) 뒤에 개행을 넣고 이어 붙임, boot_seq·since_boot_s 이어감, detach·attach(com·gap_s·reason), 45초 공백 뒤엔 부팅 시각 모름(since_boot_s null)");
            var s3 = new Feeder(ctx, dir, 18614, MergeCfg());
            ctx.Check(s3.S.LastRecordKind == "app_error" && s3.S.LastRecordTs == new DateTimeOffset(T0.AddSeconds(76)), "다음 판정기: 마지막 기록 종류·시각을 이어 받음(분리로 끝났으면 허브 시작 공백을 그 시각부터)");
            s3.S.Close("시험 끝");

            // ---- 서로게이트 자르기·깨진 줄(H5)·EventTail
            string sur = new string('a', 499) + "\U0001F600" + "zz";
            string tr = EventScanner.Trunc(sur, 500);
            ctx.Check(tr.Length == 500 && !char.IsHighSurrogate(tr[498]) && tr.EndsWith("…", StringComparison.Ordinal) && EventRec.Parse("{\"ts\":\"2026-11-05T17:00:00\",\"kind\":\"panic\",\"line\":\"x\\ud83d\"}") == null
                      && EventRec.Parse("\0\0{\"ts\":\"2026-11-05T17:00:00\",\"kind\":\"oom\"}")?.Kind == "oom",
                      "500자 자르기가 서로게이트 짝을 가르지 않음, 홀로 남은 서로게이트 줄은 null(예외 없음), 앞 NUL 은 무시");
            var tail = new EventTail(dir, new[] { 18614 }, false);
            ctx.Check(tail.Poll().Count == 0, "EventTail(끝부터): 이미 있는 사건은 주지 않음");
            AppendShared(s2.S.JsonlPath, Encoding.UTF8.GetBytes("{\"ts\":\"2026-11-05T18:00:00.000+09:00\",\"port\":18614,\"kind\":\"bug\",\"line\":\"bad \\ud83d\"}\n"));
            AppendShared(s2.S.JsonlPath, Encoding.UTF8.GetBytes("{\"ts\":\"2026-11-05T18:00:01.000+09:00\",\"port\":18614,\"kind\":\"oom\""));
            var tp = tail.Poll();
            AppendShared(s2.S.JsonlPath, Encoding.UTF8.GetBytes("}\n"));
            var tp2 = tail.Poll();
            ctx.Check(tp.Count == 0 && tp2.Count == 1 && tp2[0].Kind == "oom" && EventLog.Read(dir, new[] { 18614 }).Count(x => x.Kind == "oom") == 1,
                      "EventTail: 읽지 못하는 줄은 건너뛰고 넘어감, 개행 없는 마지막 줄은 다음 Poll 에");

            // ---- 풍선 묶음
            EventRec Ev(int port, string kind, string cls = null, string context = null, double t = 0, string skipped = null, string suspect = null, string cause = null, string cctx = null)
                => new EventRec { Port = port, Kind = kind, Class = cls, Board = "SIMEV", Context = context, ContextSkipped = skipped, Suspect = suspect, CauseKind = cause, CauseContext = cctx, Ts = new DateTimeOffset(T0.AddSeconds(t)) };
            var bl = new EventBalloons { WindowS = 60, SettleS = 2 };
            var now = T0;
            // 알림(2026-11-06): panic·oops·app_error·confirm·panic/oops 가 cause 인 부팅만 — 원인 없는 soft·hard 부팅·bug·oom·missed 는 기록만
            bl.Add(Ev(7001, "oops", null, @"C:\e\a.txt"), now); bl.Add(Ev(7001, "panic", null, @"C:\e\a.txt", 0.2), now);
            bl.Add(Ev(7002, "boot", "hard", @"C:\e\b.txt", 0.5, cause: "panic", cctx: @"C:\e\p.txt"), now.AddSeconds(0.5)); bl.Add(Ev(7002, "boot", "cmd", null, 0.6), now.AddSeconds(0.6));
            bl.Add(Ev(7003, "bug", null, null, 0.7, "rate"), now.AddSeconds(0.7)); bl.Add(Ev(7003, "attach"), now.AddSeconds(0.7));
            bl.Add(Ev(7003, "panic", null, @"C:\e\s.txt", 0.8, null, "cmd_output"), now.AddSeconds(0.8)); bl.Add(Ev(7003, "detach"), now.AddSeconds(0.8));
            bl.Add(Ev(7004, "boot", "hard", @"C:\e\h.txt", 0.8), now.AddSeconds(0.8)); bl.Add(Ev(7004, "boot", "soft", @"C:\e\s2.txt", 0.9), now.AddSeconds(0.9));
            bl.Add(Ev(7004, "oom", null, @"C:\e\o.txt", 0.9), now.AddSeconds(0.9)); bl.Add(Ev(7004, "bug", null, @"C:\e\g.txt", 0.9), now.AddSeconds(0.9)); bl.Add(Ev(7004, "missed", null, null, 0.9), now.AddSeconds(0.9));
            var d1 = bl.Due(now.AddSeconds(1)); var d2 = bl.Due(now.AddSeconds(2.6));
            var mg = EventBalloons.Merge(d2, " | ");
            ctx.Check(d1.Count == 0 && d2.Count == 2 && mg.Text == "SIMEV (7001): Kernel panic, 17:00:00. (외 1건) | SIMEV (7002): panic 뒤 재부팅, 17:00:00. 누르면 SIMEV (7001)의 앞뒤 로그가 열립니다."
                      && mg.Open == @"C:\e\a.txt" && mg.Count == 3 && d2[1].Text == "SIMEV (7002): panic 뒤 재부팅, 17:00:00. 누르면 앞뒤 로그가 열립니다." && d2[1].Open == @"C:\e\p.txt",
                      "풍선: 2초 대기 뒤, 같은 틱의 두 포트는 한 풍선(가장 심한 panic 먼저·그 앞뒤 로그)·'panic 뒤 재부팅'(누르면 그 panic 의 앞뒤 로그), cmd 부팅·rate 로 줄인 같은 종류·attach·detach·suspect(명령 출력 속 panic)·"
                      + "원인 없는 hard·soft 부팅·oom·bug·missed 는 안 알림 (" + mg?.Text + ")");
            bl.Add(Ev(7001, "app_error", null, @"C:\e\c.txt", 10), now.AddSeconds(10)); bl.Add(Ev(7001, "oops", null, @"C:\e\c.txt", 20), now.AddSeconds(20)); bl.Add(Ev(7001, "bug", null, @"C:\e\c.txt", 21), now.AddSeconds(21));
            var d4 = bl.Due(now.AddSeconds(61)); var d5 = bl.Due(now.AddSeconds(62.6));
            ctx.Check(d4.Count == 0 && d5.Count == 1 && d5[0].Text == "SIMEV (7001): 커널 Oops, 17:00:20. 누르면 앞뒤 로그가 열립니다. (외 1건)", "풍선: 1분 창 안의 사건은 창 끝에 하나 '(외 1건)' — bug 는 세지 않음 (" + (d5.Count > 0 ? d5[0].Text : "") + ")");

            // ---- aging 요약(공백·간격 제외)
            var ag = new Feeder(ctx, dir, 18615, MergeCfg());
            ag.L(0, "LDR>T###%%");                                                           // hard
            ag.L(50, "Kernel panic - not syncing: a");
            ag.Tx(99, "reboot"); ag.L(100, "LDR>T###%%");                                    // cmd
            ag.L(399, "reboot: Restarting system"); ag.L(400, "LDR>T###%%");                 // soft
            ag.L(450, "invoked oom-killer: gfp_mask=0x0");
            ag.S.OnAttach("start", "COM9", 200.0, null, T0.AddSeconds(650), 650000, "1:9");  // 450~650 기록 공백(허브 꺼짐)
            ag.Tx(699, "reboot"); ag.L(700, "LDR>T###%%");                                   // cmd(간격 300 은 공백을 걸침 → 뺌)
            ag.Tx(799, "reboot"); ag.L(800, "LDR>T###%%");                                   // cmd
            ag.S.OnDetach(null, T0.AddSeconds(820), 820000, "1:10");                          // 820~850 분리(케이블 붙은 상태로 센다, D8)
            ag.S.OnAttach("reattach", "COM9", 30.0, new DateTimeOffset(T0.AddSeconds(820)), T0.AddSeconds(850), 850000, "1:11");
            ag.Tx(860, "cat /mnt/mmc/old.log"); ag.L(860.05, "cat /mnt/mmc/old.log");
            ag.L(860.1, "[    5.000000] Kernel panic - not syncing: old");                    // 명령 출력 속 옛 panic = suspect(따로 셈)
            ag.S.Close("시험 끝");
            var all = EventLog.Read(dir, new[] { 18615 });
            var aj = JsonDocument.Parse(EventLog.Aging(18615, all, null, new DateTimeOffset(T0.AddSeconds(900)), "x").ToString()).RootElement;
            var bo = aj.GetProperty("boots"); var iv = aj.GetProperty("boot_interval_s"); var kd = aj.GetProperty("kinds");
            ctx.Check(J.N(bo, "total") == 5 && J.N(bo, "cmd") == 3 && J.N(bo, "soft") == 1 && J.N(bo, "hard") == 1 && J.N(aj, "unexpected_reboots") == 2
                      && J.N(iv, "count") == 3 && iv.GetProperty("min").GetDouble() == 100 && iv.GetProperty("median").GetDouble() == 100 && iv.GetProperty("max").GetDouble() == 300 && J.N(iv, "excluded_gap") == 1
                      && aj.GetProperty("period_s").GetDouble() == 900 && aj.GetProperty("covered_s").GetDouble() == 670 && aj.GetProperty("gaps").GetArrayLength() == 2
                      && J.N(kd.GetProperty("panic"), "count") == 1 && J.N(kd.GetProperty("panic"), "suspect") == 1 && J.N(kd.GetProperty("oom"), "count") == 1 && J.IsNull(kd.GetProperty("bug"), "first")
                      && J.N(aj, "events") == 7 && J.N(aj, "suspect_events") == 1 && J.N(bo, "suspect") == 0
                      && aj.GetProperty("contexts").EnumerateArray().All(x => J.B(x, "exists") == true) && aj.GetProperty("contexts").EnumerateArray().Any(x => J.S(x, "kind") == "boot:soft"),
                      "aging: 부팅 5(cmd 3·soft 1·hard 1)·간격 100·100·300(공백 걸친 1개 뺌)·기간 900 중 기록 670초(허브 꺼짐 200·분리 30)·panic 1(+ suspect 1 따로)·oom 1·앞뒤 로그(부팅 앞 줄 포함) — " + J.Show(aj));
            var open1 = all.Concat(new[] { new EventRec { Port = 18615, Kind = "detach", Ts = new DateTimeOffset(T0.AddSeconds(880)) } }).ToList();
            var aj2 = JsonDocument.Parse(EventLog.Aging(18615, open1, null, new DateTimeOffset(T0.AddSeconds(900)), "x").ToString()).RootElement;
            ctx.Check(aj2.GetProperty("covered_s").GetDouble() == 650 && aj2.GetProperty("gaps").GetArrayLength() == 3, "aging: 끝까지 다시 붙지 않은 분리는 그때부터 until 까지 공백(covered 650)");
            ctx.Check(EventLog.TryParseSince("12h", new DateTimeOffset(T0), out var s12) && s12 == new DateTimeOffset(T0.AddHours(-12))
                      && EventLog.TryParseSince("2026-11-05T08:00", new DateTimeOffset(T0), out var siso) && siso.LocalDateTime == new DateTime(2026, 11, 5, 8, 0, 0) && !EventLog.TryParseSince("yesterday", new DateTimeOffset(T0), out _)
                      && all.Count(x => x.KindIn(new HashSet<string> { "boot:cmd" })) == 3, "--since 12h·ISO·잘못된 값, --kind boot:cmd");

            // ---- 대상 해석(L4, 허브 없이)
            var hub = Path.Combine(ctx.Dir, "rhub"); var rdir = Path.Combine(ctx.Dir, "rlogs", "events");
            Directory.CreateDirectory(hub); Directory.CreateDirectory(rdir);
            File.WriteAllText(Path.Combine(hub, "state.json"), "{\"assign\":{\"A1\":7001,\"B2\":7002},\"released\":[],\"boards\":{\"A1\":\"EVB-A1\",\"B2\":\"EVB-A1\"}}", Utf8);
            var rcfg = Config.Parse("{\"cables\": {\"A1\": {\"alias\": \"front\"}}}");
            var ra = new EventScanner(rdir, 7001, "A1", () => "EVB-A1", () => null, EventPatterns.For(rcfg, "A1"), rcfg, null);
            ra.OnAttach("start", "COM5", null, null, T0, 0, "1:0"); ra.Close("시험 끝");
            int R(string t, out int code, out List<int> cands) => EventLog.ResolveTarget(hub, rdir, rcfg, t, out code, out _, out cands);
            int p1 = R("7002", out _, out _), p2 = R("com5", out _, out _), p3 = R("front", out _, out _), p4 = R("EVB-A1", out int c4, out var cd4), p5 = R("NOPE", out int c5, out _);
            ctx.Check(p1 == 7002 && p2 == 7001 && p3 == 7001 && p4 == 0 && c4 == 105 && cd4.SequenceEqual(new[] { 7001, 7002 }) && p5 == 0 && c5 == 102,
                      "대상 해석: 포트 → 그대로, COMx → 사건 기록의 attach com, alias(config), 보드명이 둘이면 105 [7001,7002], 없으면 102");
        }

        // ================================================================== events-hub
        [Scenario("events-hub", "X6 허브 안 실제 링 추적(Hub + FakeBoard): attach 기록(start·com), panic 줄 → jsonl·앞뒤 로그(>>> 2줄·보드 이름), Claude reboot → cmd, 사람이 친 reboot → cmd, Restarting 뒤 리셋 → soft(앞 줄 파일), 리셋 → hard, 추적 멈춤 중 링 덮어씀 → missed 뒤 계속, 허브 정지 → 열린 앞뒤 로그 '# 끝 — 허브 정지'(Join), 다시 기동 → 같은 jsonl 에 이어 붙음(attach 공백·boot_seq 이어감), events.enabled:false 면 아무것도 쓰지 않음. + C.12 log_layout 3 에이징 세션: 세션 밖 감지 없음, session start·status·stop, 콘솔.log·이벤트 txt·요약.txt, 코어 재시작 뒤 이어 감, 연결 안 된 포트 거부. + C.13 재부팅 뒤 자동 깨우기: run 송신 직후 리셋 → rebooted·잠금 해제·CR 1회·다음 run, 앱 줄이 흘러도 깨움, booting 중 run 대기 실행, 사람 줄 입력 중 101·0바이트",
                  Order = 1301, TimeoutS = 240)]
        public static void InHub(ScenarioContext ctx)
        {
            // LoaderDelayMs 700: 실보드처럼 리셋 앞 무음(실측 0.625초 이상) — 명령 출력 구간 안의 로더 표시는 앞 무음 0.5초 이상일 때만 실시간
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIMEVH", LoaderDelayMs = 700 }, "SIMCABEV", "COM186", 18620, c => { c.BasePort = 18630; c.RingMb = 0.25; c.BootMergeMs = 1500; });
            string dir = r.Hub.Control.Events.Dir;
            int port = r.Cab.TcpPort;
            ctx.WaitUntil(() => r.Hub.Control.Events.ScannerOf(r.Cab) != null, 5000, "사건 감지: 붙은 케이블의 추적 스레드·판정기 준비");
            List<EventRec> Evs() => EventLog.Read(dir, new[] { port });
            void Wait(Func<List<EventRec>, bool> cond, string what, int ms = 15000)
            {
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < ms) { if (cond(Evs())) { ctx.Log("ok: " + what + " (" + sw.ElapsedMilliseconds + "ms)"); return; } Thread.Sleep(20); }
                ctx.Fail("시간 초과(" + ms + "ms): " + what + " — 사건: " + string.Join(" | ", Evs().Select(x => x.Raw)));
            }
            void ToPrompt(string what)
            {
                Thread.Sleep(1700);                                                              // 부팅 병합 창(1.5초)을 넘긴다
                r.Human("\r");
                r.WaitState(PromptState.AtPrompt, 5000, what + ": Enter → 프롬프트");
                r.IdleWait();
            }
            Wait(l => l.Any(x => x.Kind == "attach"), "attach 기록");
            var at0 = Evs().First(x => x.Kind == "attach");
            ctx.Check(at0.Reason == "start" && at0.Com == "COM186" && at0.GapS == null && dir == Path.Combine(r.Cfg.LogRoot, "events"),
                      "attach 기록: reason start·com COM186·gap_s null(그 전 기록 없음), 폴더 <log_root>\\events");

            // ① panic
            r.Board.EmitPrintk("Kernel panic - not syncing: sim panic");
            r.Board.EmitPrintk("Call trace:");
            r.Board.Emit(" dump_backtrace+0x0/0x1a0\r\n panic+0x150/0x2f8\r\n");
            r.Board.EmitPrintk("---[ end Kernel panic - not syncing: sim panic ]---");
            Wait(l => l.Count(x => x.Kind == "panic") == 1, "panic 사건 1(끝 줄 묶음)");
            Thread.Sleep(300);
            var p = Evs().Single(x => x.Kind == "panic");
            ctx.Check(File.Exists(p.Context) && Path.GetFileName(p.Context).StartsWith(port + "_SIMEVH_", StringComparison.Ordinal) && Lines(p.Context).Count(l => l.StartsWith(">>> ", StringComparison.Ordinal)) == 2
                      && p.Log != null && File.Exists(p.Log) && Regex.IsMatch(p.LogLine ?? "", @"^\d{13}:\d+$"), "panic: jsonl + 앞뒤 로그('>>>' 2줄), log = 지금 .log, log_line = epoch:offset");

            // ② Claude reboot → cmd · ③ 사람 reboot → cmd · ④ Restarting 뒤 리셋 → soft · ⑤ 리셋 → hard
            ToPrompt("②");
            ctx.Check(J.S(r.Call("reboot", r.P, "--json"), "status") == "ok", "Claude reboot 송신");
            Wait(l => l.Count(x => x.Kind == "boot") == 1, "Claude reboot 뒤 부팅");
            ToPrompt("③");
            r.Human("reboot\r");
            Wait(l => l.Count(x => x.Kind == "boot") == 2, "사람 reboot 뒤 부팅");
            ToPrompt("④");
            r.Board.EmitPrintk("reboot: Restarting system");
            Thread.Sleep(50);
            r.Board.Reset();
            Wait(l => l.Count(x => x.Kind == "boot") == 3, "Restarting 뒤 리셋 부팅");
            ToPrompt("⑤");
            r.Board.Reset();
            Wait(l => l.Count(x => x.Kind == "boot") == 4, "리셋 부팅");
            var boots = Evs().Where(x => x.Kind == "boot").ToList();
            ctx.Check(string.Join(",", boots.Select(x => x.Class)) == "cmd,cmd,soft,hard" && boots.Select(x => x.BootSeq).SequenceEqual(new long[] { 1, 2, 3, 4 })
                      && boots[2].Context != null && boots[3].Context != null && boots[0].Context == null && Lines(boots[2].Context).Any(l => l.Contains("reboot: Restarting system")),
                      "부팅: Claude reboot cmd · 사람 reboot cmd · Restarting 뒤 리셋 soft · 리셋 hard, boot_seq 1~4, soft·hard 는 앞 줄 파일 (" + string.Join(",", boots.Select(x => x.Class)) + ")");

            // ⑥ 추적 멈춤 중 링 덮어씀 → missed
            ToPrompt("⑥");
            ctx.Check(r.Hub.Control.Events.TestPause(r.Cab, true), "사건 추적 스레드 멈춤(시험 훅)");
            long end0 = r.Ring.End;
            var flood = new StringBuilder();
            for (int i = 0; i < 6000; i++) flood.Append("flood line ").Append(i.ToString("D6", CultureInfo.InvariantCulture)).Append(" ........................................\r\n");
            r.Board.Emit(flood.ToString());
            ctx.WaitUntil(() => r.Ring.End - end0 >= flood.Length, 15000, "링에 " + flood.Length + "B(링 " + r.Cfg.RingBytes + "B 보다 큼) 들어옴");
            r.Hub.Control.Events.TestPause(r.Cab, false);
            Wait(l => l.Any(x => x.Kind == "missed"), "missed 사건");
            r.Board.EmitAppLog("APPFAULT(ED)");
            Wait(l => l.Any(x => x.Kind == "app_error"), "놓친 뒤에도 판정 계속(app_error)");
            ctx.Check(Evs().First(x => x.Kind == "missed").Skipped > 0, "missed: skipped > 0");

            // ⑥' 케이블 뽑음 → detach(링 끝까지 읽은 뒤) · 다시 꽂음 → attach reattach(분리 동안 공백)
            var fprov = (FakePortProvider)r.Prov;
            fprov.Unplug("SIMCABEV");
            Wait(l => l.Any(x => x.Kind == "detach"), "케이블 뽑음 → detach 기록");
            Thread.Sleep(1200);
            fprov.Plug(r.Board, "SIMCABEV", "COM186");
            Wait(l => l.Count(x => x.Kind == "attach") == 2, "다시 꽂음 → attach(reattach)");
            var re1 = Evs().Last(x => x.Kind == "attach");
            var de1 = Evs().Last(x => x.Kind == "detach");
            ctx.Check(re1.Reason == "reattach" && re1.GapS >= 1.0 && re1.Ts > de1.Ts && Evs().FindIndex(x => x.Kind == "detach") < Evs().FindIndex(x => x.Kind == "attach" && x.Reason == "reattach"),
                      "detach → attach(reattach, gap_s " + re1.GapS + "초) 순서 — aging 은 그 사이를 기록 공백으로 센다(D8)");

            // ⑦ 허브 정지 → 열린 앞뒤 로그 닫힘(Join) → 다시 기동 → 이어 붙음
            r.Board.EmitPrintk("BUG: before stop");
            Wait(l => l.Any(x => x.Kind == "bug"), "정지 직전 bug 사건(앞뒤 로그 열림)");
            var bugCtx = Evs().First(x => x.Kind == "bug").Context;
            int linesBefore = JsonlLines(EventLog.FileOf(dir, port)).Length;
            string first = JsonlLines(EventLog.FileOf(dir, port))[0];
            r.Hub.Stop();
            ctx.Check(r.Hub.Control.Events.Join(3000) && Lines(bugCtx).Last().StartsWith("# 끝 — 허브 정지", StringComparison.Ordinal), "허브 정지 → 추적 스레드 Join, 열린 앞뒤 로그 마지막 줄 '# 끝 — 허브 정지'");
            var r2 = HubRig.Boot(ctx, r.Cfg, r.Board, r.Prov, "SIMCABEV", true);
            Thread.Sleep(300);
            r2.Board.Reset();
            Wait(l => l.Count(x => x.Kind == "boot") == 5, "다시 기동한 허브에서 부팅 사건");
            var all = JsonlLines(EventLog.FileOf(dir, port));
            var last = Evs().Last(x => x.Kind == "boot");
            var at1 = Evs().Where(x => x.Kind == "attach").ToList();
            ctx.Check(all.Length > linesBefore && all[0] == first && last.BootSeq == 5 && last.Class == "hard" && at1.Count == 3 && at1[2].Reason == "start" && at1[2].GapS.HasValue && at1[2].GapS < 30,
                      "허브 재시작 뒤: 같은 파일에 이어 붙음(" + linesBefore + " → " + all.Length + "줄), 두 번째 attach(gap_s " + at1.Last().GapS + "), boot_seq 4 다음 5");
            ctx.Check(!LogFiles.ReadShared(Path.Combine(r.Cfg.EffectiveHubDir, "hub.log")).Contains("사건 감지"), "hub.log 에 사건 감지 줄 0(시작은 남기지 않고 실패만 — 실패 없음)");

            // ⑧ events.enabled:false
            r2.Hub.Stop();
            var off = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIMEVO" }, "SIMCABEVO", "COM187", 18625, c => { c.BasePort = 18635; c.Events.Enabled = false; });
            off.Board.EmitPrintk("Kernel panic - not syncing: disabled");
            Thread.Sleep(1000);
            ctx.Check(off.Hub.Control.Events.ScannerOf(off.Cab) == null && !File.Exists(EventLog.FileOf(off.Hub.Control.Events.Dir, off.Cab.TcpPort)),
                      "events.enabled:false → 추적 없음·" + off.Cab.TcpPort + ".jsonl 없음");
            off.Hub.Stop();
            AgingScenarios.InHub(ctx);
            NudgeScenarios.PartA(ctx);                 // 부록 C.13 ⑴⑵⑺: 재부팅으로 사라진 run 잠금 해제·자동 깨우기(앱 줄이 흘러도)·booting 중 run 대기 실행                 // 부록 C.12: log_layout 3 에이징 세션(세션 밖 감지 없음·start·status·stop·세션 폴더·코어 재시작 뒤 이어 감·연결 안 됨 거부)
        }

        // ================================================================== events-cli
        /// <summary>cli --parent-pipe events … 를 띄워 표준 출력 줄을 모은다. 표준 입력을 닫으면 CLI 가 끝난다(래퍼가 죽은 것과 같음).</summary>
        sealed class FollowProc
        {
            public Process P;
            readonly List<string> lines = new List<string>();
            public List<string> Lines { get { lock (lines) return lines.ToList(); } }

            public static FollowProc Start(ScenarioContext ctx, InstallRig r, string hub, bool read, params string[] a)
            {
                var psi = new ProcessStartInfo(TestEnv.UartHubExe)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8, WorkingDirectory = Path.GetDirectoryName(TestEnv.UartHubExe),
                };
                psi.ArgumentList.Add("cli"); psi.ArgumentList.Add("--parent-pipe");
                foreach (var x in a) psi.ArgumentList.Add(x);
                foreach (var k in new[] { "UART_HUB_CONTROL_PORT", "UART_HUB_TEST_FAIL", "UART_HUB_TEST_ANSWER", "UART_HUB_TEST_DELAY", "UART_PATH_STYLE" }) psi.Environment.Remove(k);
                psi.Environment["UART_HUB_TEST_ROOT"] = r.Root;
                psi.Environment["UART_HUB_DIR"] = hub;
                var f = new FollowProc();
                f.P = Process.Start(psi);
                if (read)
                {
                    f.P.OutputDataReceived += (s, e) => { if (e.Data != null) lock (f.lines) f.lines.Add(e.Data); };
                    f.P.BeginOutputReadLine();
                }
                f.P.StandardError.ReadToEndAsync();
                ctx.Defer(() => { try { if (!f.P.HasExited) { f.P.Kill(); f.P.WaitForExit(5000); } } catch (Exception) { } });
                return f;
            }

            /// <summary>머리 줄 한 줄을 직접 읽는다(read=false 로 띄운 것).</summary>
            public string ReadHead() => P.StandardOutput.ReadLine();
        }

        static string B64(string s) => Convert.ToBase64String(Utf8.GetBytes(s));

        /// <summary>
        /// 알림 종류(2026-11-06, 프로세스 없음): config events.notify(기본 panic·oops·app_error·confirm, 검증·C-12), 원인 없는 hard 부팅은 풍선 없음·panic 은 풍선,
        /// panic 이 cause 인 부팅은 알림, notify 를 바꾸면 그대로. myapp 의 'APP_EVT_ID_UI_OOM_ERROR' 는 app_error(알림), 커널 oom-killer 는 oom(기록만).
        /// </summary>
        /// <summary>
        /// 부록 C.11 단위(프로세스 없음): log_layout 2 경로(콘솔로그·에이징이벤트·허브 폴더 events, 키 없으면 그대로)·WithLogLayout(글자 편집)·숨김(.raw·.idx·live, .log 아님, 열린 스트림 그대로)·
        /// 로그 옮기기(같은 볼륨 이름 바꾸기, 대상에 있으면 건너뜀, 잠긴 파일은 건너뛰고 나머지 진행, 두 번째는 할 일 없음)·에이징 이벤트 txt(이름·머리·결과 줄·더 심한 종류로 이름 바뀜 + context_was·
        /// _의심·겹치면 _2·앱오류 코드·연결·분리·조용한 종류는 txt 없음)·jsonl 읽기의 이름 바뀜 적용. 돌려주는 값 = CLI 시험용 허브 폴더(&lt;기준&gt;\hub, jsonl 은 그 events).
        /// </summary>
        static string C11Units(ScenarioContext ctx)
        {
            // ---- 경로
            var c1 = Config.Parse("{\"log_root\": \"D:\\\\L\"}"); c1.ResolveLogRoot(@"D:\L\hub");
            var c2 = Config.Parse("{\"log_root\": \"D:\\\\L\", \"log_layout\": 2}"); c2.HubDir = @"D:\L\hub"; c2.ResolveLogRoot(@"D:\L\hub"); c2.ResolveLogRoot(@"D:\L\hub");
            var c3 = Config.Parse("{\"log_layout\": 2}"); c3.HubDir = @"E:\X\hub"; c3.ResolveLogRoot(@"E:\X\hub");
            ctx.Check(c1.LogLayout == 1 && c1.LogRoot == @"D:\L" && c1.BaseRoot == @"D:\L" && c1.EventsJsonlDir == @"D:\L\events" && c1.EventsTxtDir == @"D:\L\events" && c1.EffectiveHubDir == @"D:\L\hub"
                      && c2.LogRoot == @"D:\L\콘솔로그" && c2.BaseRoot == @"D:\L" && c2.EventsJsonlDir == @"D:\L\hub\events" && c2.EventsTxtDir == @"D:\L\에이징이벤트"
                      && c3.LogRoot == @"E:\X\콘솔로그" && c3.EventsTxtDir == @"E:\X\에이징이벤트" && c3.EffectiveHubDir == @"E:\X\hub"
                      && Config.Parse("{\"log_layout\": 4}").Validate().Any(e => e.Contains("log_layout")) && Config.Parse("{\"log_layout\": 3}").Validate().Count == 0,
                      "C.11 경로: 키 없음 = 그대로(log_root·events), log_layout 2 = 콘솔로그·에이징이벤트·허브 폴더\\events(두 번 풀어도 같음), 3 은 C.12 세션 배치, 4 는 설정 오류");
            string t = "// 주석\r\n{\r\n  \"on_demand\": true, // 켬\r\n  \"cables\": {}\r\n}\r\n";
            string tn = Config.WithLogLayout(t, out _);
            string up = Config.WithLogLayout("// 옛 설정\r\n{\"log_layout\": 2, // 보기 쉬운 배치\r\n \"on_demand\": true}\r\n", out _);
            ctx.Check(tn == "// 주석\r\n{\r\n  \"log_layout\": 3,\r\n  \"on_demand\": true, // 켬\r\n  \"cables\": {}\r\n}\r\n" && Config.WithLogLayout(tn, out string tw) == null && tw.Contains("이미 있음")
                      && Config.WithLogLayout("{\"log_layout\": 1}", out _) == null && Config.Parse(tn).LogLayout == 3 && Config.Parse(Installer.PortableDefaultConfig).LogLayout == 3
                      && up == "// 옛 설정\r\n{\"log_layout\": 3, // 보기 쉬운 배치\r\n \"on_demand\": true}\r\n",
                      "WithLogLayout(C.12): 주석·CRLF·다른 키 그대로 \"log_layout\": 3 을 끼움, 2 는 그 글자만 3 으로, 3·1 은 그대로, 휴대용 새 config 는 3");

            // ---- 숨김
            string con = Path.Combine(ctx.Dir, "c11hide", Config.ConsoleDirName), today = Path.Combine(con, DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(today); Directory.CreateDirectory(Path.Combine(con, "live"));
            foreach (var e in new[] { ".raw", ".idx", ".log" }) File.WriteAllText(Path.Combine(today, "18001_B_1" + e), "x");
            bool H(string p) => (File.GetAttributes(p) & FileAttributes.Hidden) != 0;
            int f1, h1, f2, h2;
            using (var open = new FileStream(Path.Combine(today, "18001_B_2.raw"), FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete))
            {
                open.WriteByte(1); open.Flush();
                f1 = LogHider.Sweep(con, DateTime.Now, out h1);
                open.WriteByte(2); open.Flush();                                                // 숨긴 뒤에도 열린 스트림에 그대로 쓴다
                f2 = LogHider.Sweep(con, DateTime.Now, out h2);
            }
            ctx.Check(f1 == 0 && h1 == 4 && f2 == 0 && h2 == 0 && H(Path.Combine(today, "18001_B_1.raw")) && H(Path.Combine(today, "18001_B_1.idx")) && H(Path.Combine(today, "18001_B_2.raw"))
                      && H(Path.Combine(con, "live")) && !H(Path.Combine(today, "18001_B_1.log")) && new FileInfo(Path.Combine(today, "18001_B_2.raw")).Length == 2,
                      "숨김: .raw·.idx·콘솔로그\\live 에 숨김(" + h1 + "개), .log 는 아님, 쓰는 중인 스트림 그대로(2바이트), 두 번째 훑기는 바꿀 것 없음");

            // ---- 옮기기
            string b = Path.Combine(ctx.Dir, "c11mig"), hub = Path.Combine(b, "hub");
            foreach (var d in new[] { "20261105", "20261106", "live", @"events\20261106", "hub" }) Directory.CreateDirectory(Path.Combine(b, d));
            foreach (var e in new[] { ".log", ".raw", ".idx" }) File.WriteAllText(Path.Combine(b, "20261105", "7001_X_101010" + e), "a");
            File.WriteAllText(Path.Combine(b, "20261106", "7001_X_090000.log"), "b");
            File.WriteAllText(Path.Combine(b, "live", "7001.status"), "s");
            File.WriteAllText(Path.Combine(b, "events", "7001.jsonl"), "{}\n");
            File.WriteAllText(Path.Combine(b, "events", "7002.jsonl"), "{}\n");
            File.WriteAllText(Path.Combine(b, "events", "20261106", "7001_X_101010_panic.txt"), "p");
            File.WriteAllText(Path.Combine(b, "events", "20261106", "7001_X_101011_boot.txt"), "q");
            Directory.CreateDirectory(Path.Combine(b, Config.ConsoleDirName, "20261106"));            // 대상에 이미 있음 → 건너뜀
            string plan = LogMigration.Summary(LogMigration.Plan(b, hub));
            var ml = new List<string>();
            (int Moved, int Skipped) m1;
            using (new FileStream(Path.Combine(b, "events", "7002.jsonl"), FileMode.Open, FileAccess.Read, FileShare.Read))   // 잠김(지우기 공유 없음) → 이름 바꾸기 실패 → 건너뜀
                m1 = LogMigration.Run(b, hub, ml.Add);
            var ml2 = new List<string>();
            var m2 = LogMigration.Run(b, hub, ml2.Add);
            ctx.Check(plan != null && plan.Contains("날짜 폴더 2개") && plan.Contains("jsonl 2개") && plan.Contains("txt 2개")
                      && m1.Moved == 5 && m1.Skipped == 2 && File.Exists(Path.Combine(b, Config.ConsoleDirName, "20261105", "7001_X_101010.log")) && File.Exists(Path.Combine(b, Config.ConsoleDirName, "live", "7001.status"))
                      && File.Exists(Path.Combine(hub, "events", "7001.jsonl")) && File.Exists(Path.Combine(b, Config.EventsTxtDirName, "20261106", "7001_X_101010_panic.txt"))
                      && File.Exists(Path.Combine(b, Config.EventsTxtDirName, "20261106", "7001_X_101011_boot.txt")) && Directory.Exists(Path.Combine(b, "20261106"))
                      && !Directory.Exists(Path.Combine(b, "events", "20261106"))
                      && H(Path.Combine(b, Config.ConsoleDirName, "20261105", "7001_X_101010.raw")) && !H(Path.Combine(b, Config.ConsoleDirName, "20261105", "7001_X_101010.log"))
                      && ml.Any(l => l.Contains("옮기지 않음(대상에 이미 있음)") && l.Contains("20261106")) && ml.Any(l => l.Contains("옮기지 않음(") && l.Contains("7002.jsonl"))
                      && m2.Moved == 1 && m2.Skipped == 1 && File.Exists(Path.Combine(hub, "events", "7002.jsonl")) && !Directory.Exists(Path.Combine(b, "events")),
                      "옮기기: 날짜 폴더·live·jsonl·txt 이름 바꾸기(" + m1.Moved + "), 대상에 있는 날짜 폴더·잠긴 jsonl 은 건너뛰고 install.log 에 적음(" + m1.Skipped + "), 빈 옛 폴더 지움·.raw 숨김, "
                      + "두 번째 = 잠김 풀린 jsonl 만 옮기고 대상에 있는 것은 그대로(" + m2.Moved + "/" + m2.Skipped + ") | " + string.Join(" / ", ml.Take(3)));

            // ---- 에이징 이벤트 txt
            string ev = Path.Combine(ctx.Dir, "c11ev"), jd = Path.Combine(ev, "hub", "events"), td = Path.Combine(ev, Config.EventsTxtDirName);
            var cfg = Config.Default();
            DateTime w = new DateTime(2026, 11, 6, 17, 38, 50, 0);
            long m = 5_000_000;
            EventScanner Sc(int port, string board) => new EventScanner(jd, port, "CAB" + port, () => board, () => @"D:\L\콘솔로그\20261106\" + port + "_x.log", EventPatterns.For(cfg, "CAB" + port), cfg, null, null, td);
            void L(EventScanner sc, string s, double dt = 0.05) { m += (long)(dt * 1000); w = w.AddMilliseconds(dt * 1000); sc.OnLine(s, new[] { s }, w, m, "0"); }
            void Tk(EventScanner sc, double dt) { m += (long)(dt * 1000); w = w.AddMilliseconds(dt * 1000); sc.Tick(w, m); }
            string Day = Path.Combine(td, "20261106");
            // A: Oops → 같은 묶음에 panic(이름·머리가 커널패닉으로) → 하드 리셋(결과: 재부팅)
            var a = Sc(18701, "EVB-A1");
            a.OnAttach("start", "COM3", null, null, w, m, "0");
            L(a, "[   47.100000] Unable to handle kernel NULL pointer dereference at virtual address 0000000000000000");
            string oopsPath = Path.Combine(Day, "173850_EVB-A1(18701)_Oops.txt");
            bool oopsFirst = File.Exists(oopsPath);
            L(a, "[   47.110000] Internal error: Oops: 96000005 [#1] PREEMPT SMP");
            L(a, "[   47.200000] Kernel panic - not syncing: Fatal exception");
            L(a, "[   47.210000] ---[ end Kernel panic - not syncing: Fatal exception ]---");
            L(a, "LDR>T", 3);
            L(a, "[    0.000000] Booting Linux on physical CPU 0x0");
            Tk(a, 31);
            string pa = Path.Combine(Day, "173850_EVB-A1(18701)_커널패닉.txt");
            string[] Lines(string f) => File.Exists(f) ? (HubFiles.ReadShared(f) ?? "").Replace("\r\n", "\n").Split('\n') : new string[0];
            var al = Lines(pa);
            var aj = EventLog.Read(jd, new[] { 18701 });
            var araw = (HubFiles.ReadShared(Path.Combine(jd, "18701.jsonl")) ?? "").Split('\n').Select(EventRec.Parse).Where(x => x != null).ToList();
            ctx.Check(oopsFirst && !File.Exists(oopsPath) && al.Length > 8 && al[0] == "이벤트 종류: Kernel panic" && al[1] == "보드: EVB-A1 (포트 18701 · COM3)" && al[2].StartsWith("시각: 2026-11-06 17:38:50", StringComparison.Ordinal)
                      && al[3] == "원인 줄: [   47.200000] Kernel panic - not syncing: Fatal exception" && al[4] == @"콘솔 로그 파일: D:\L\콘솔로그\20261106\18701_x.log"
                      && al[5] == "----- 앞 200줄 -----" && al.Contains("----- 이후 -----") && al.Any(l => l.StartsWith(">>> ", StringComparison.Ordinal) && l.Contains("Unable to handle kernel"))
                      && al.Any(l => l.Contains("LDR>T")) && al.Last(l => l.Length > 0).StartsWith("결과: 17:38:5", StringComparison.Ordinal) && al.Last(l => l.Length > 0).EndsWith("에 재부팅했습니다(하드 리셋).", StringComparison.Ordinal)
                      && araw.Any(x => x.Kind == "panic" && x.Context == pa && x.ContextWas == oopsPath) && araw.Any(x => x.Kind == "oops" && x.Context == oopsPath)
                      && aj.Where(x => x.Kind == "oops" || x.Kind == "panic").All(x => x.Context == pa) && aj.Single(x => x.Kind == "oops").Raw.Contains(Json.Str(pa).Trim('"'))
                      && aj.Any(x => x.Kind == "boot" && x.Class == "hard" && x.CauseKind != null && x.Context == pa && x.CauseContext == pa)
                      && aj.Any(x => x.Kind == "attach") && Directory.GetFiles(Day, "*(18701)*").Length == 1,
                      "txt: Oops 로 열린 묶음에 panic 이 합류 → 173850_EVB-A1(18701)_커널패닉.txt 하나(옛 _Oops 지움, panic 기록 context_was), 머리 5줄·앞 200줄·이후·결과 '재부팅(하드 리셋)', "
                      + "jsonl 읽기는 앞 기록의 context 도 새 이름으로, 원인 부팅의 context = 그 txt | " + string.Join(" / ", al.Take(5)) + " … " + al.LastOrDefault(l => l.Length > 0));
            // B: 명령 출력 속 panic(suspect) → _의심, 같은 초 두 번째 묶음 → _2, 30초 무음 → confirm → 결과: 응답 없음
            var bs = Sc(18702, "EVB-A1");
            w = new DateTime(2026, 11, 6, 18, 0, 0, 0);
            bs.OnTxLine("cat /mnt/sd/old.log", m);
            L(bs, "[   10.000000] Kernel panic - not syncing: old one");
            for (int i = 0; i < 100; i++) L(bs, "filler " + i, 0.001);
            L(bs, "[   11.000000] Kernel panic - not syncing: old two", 0.001);
            Tk(bs, 31); Tk(bs, 31); Tk(bs, 31);
            string b1 = Path.Combine(Day, "180000_EVB-A1(18702)_커널패닉_의심.txt"), b2 = Path.Combine(Day, "180000_EVB-A1(18702)_커널패닉_의심_2.txt");
            var bl = Lines(b1);
            var bl2 = Lines(b2);
            ctx.Check(bl.Length > 5 && bl[0] == "이벤트 종류: Kernel panic (의심)" && bl.Any(l => l.StartsWith("의심 이유: ", StringComparison.Ordinal))
                      && bl.Last(l => l.Length > 0).StartsWith("결과: 응답 없음.", StringComparison.Ordinal) && bl2.Length > 5 && bl2.Last(l => l.Length > 0).StartsWith("결과: 응답 없음.", StringComparison.Ordinal)
                      && EventLog.Read(jd, new[] { 18702 }).Any(x => x.Kind == "confirm" && x.Context == b1),
                      "txt: 명령 출력 속 panic = _의심(머리에 이유), 같은 초 두 번째 묶음 = _의심_2, 뒤 무음 → confirm·결과 '응답 없음' | " + string.Join(" / ", Directory.GetFiles(Day, "*(18702)*").Select(Path.GetFileName)));
            // C: 앱 오류 코드·보드 이름의 공백 → '_', 조용한 종류(oom·bug)·부팅·연결·분리는 txt 없음, 결과 창 지나 출력이 이어지면 '계속 동작'
            var cs = Sc(18703, "EVB E5");
            w = new DateTime(2026, 11, 6, 19, 0, 0, 0);
            cs.OnAttach("start", "COM9", null, null, w, m, "0");
            L(cs, "APPFAULT(ED)(0003) myapp restart");
            L(cs, "[2026/11/06-19:00:31] app Ui msg : APP_EVT_ID_UI_OOM_ERROR", 31);
            L(cs, "[ 3000.000000] app: Panic call, app_wdt_external_set", 31);
            L(cs, "[ 3031.000000] myapp invoked oom-killer: gfp_mask=0x6000c0(GFP_KERNEL), order=3", 31);
            L(cs, "[ 3062.000000] BUG: scheduling while atomic: swapper/0/0/0x00000002", 31);
            L(cs, "LDR>T", 31);
            for (int i = 0; i < 4; i++) { L(cs, "tick " + i, 20); Tk(cs, 1); }
            cs.OnDetach("unplug", w, m, "0");
            var cf = Directory.GetFiles(Day, "*(18703)*").Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var cl = cf.Count > 0 ? Lines(Path.Combine(Day, cf[0])) : new string[0];
            var cj = EventLog.Read(jd, new[] { 18703 });
            ctx.Check(cf.SequenceEqual(new[] { "190000_EVB_E5(18703)_앱오류_APPFAULT(ED).txt", "190031_EVB_E5(18703)_앱오류_OOM.txt", "190102_EVB_E5(18703)_앱오류_WDT.txt" })
                      && cl.Length > 5 && cl[0] == "이벤트 종류: 앱 오류 APPFAULT(ED)" && cl[1] == "보드: EVB E5 (포트 18703 · COM9)" && cl.Last(l => l.Length > 0).StartsWith("결과: 계속 동작.", StringComparison.Ordinal)
                      && cj.Where(x => x.Kind == "oom" || x.Kind == "bug" || x.Kind == "boot" || x.Kind == "attach" || x.Kind == "detach").All(x => x.Context == null && x.ContextSkipped == null)
                      && cj.Count(x => x.Kind == "oom" || x.Kind == "bug") == 2 && cj.Any(x => x.Kind == "boot") && cj.Any(x => x.Kind == "detach"),
                      "txt: 앱오류_APPFAULT(ED)·앱오류_OOM(APP_EVT_ID_UI_OOM_ERROR)·앱오류_WDT(app_wdt), 보드 공백 → '_', oom·bug·부팅·연결·분리는 jsonl 에만(context 없음), 결과 '계속 동작' | "
                      + string.Join(" / ", cf) + " | " + cl.LastOrDefault(l => l.Length > 0));
            foreach (var sc in new[] { a, bs, cs }) sc.Close("시험 끝");
            // CLI 시험용 허브 폴더: <기준>\hub\config.json(log_layout 2) + jsonl 은 그 events(위 jd)
            File.WriteAllText(Path.Combine(ev, "hub", "config.json"), "{\"log_layout\": 2}\n");
            return Path.Combine(ev, "hub");
        }

        static void NotifyUnits(ScenarioContext ctx)
        {
            var pat = EventPatterns.For(Config.Default(), "CABN");
            string appOom = pat.Match(new[] { "[2026/11/06-09:12:01] app Ui msg : APP_EVT_ID_UI_OOM_ERROR" }, null), kOom = pat.Match(new[] { "[  350.0] myapp invoked oom-killer: gfp_mask=0x6000c0(GFP_KERNEL), order=3" }, null);
            ctx.Check(appOom == "app_error" && EventKinds.Notifiable(appOom, null) && kOom == "oom" && !EventKinds.Notifiable(kOom, null),
                      "기본 패턴: 'app Ui msg : APP_EVT_ID_UI_OOM_ERROR' → app_error(알림), 커널 oom-killer → oom(기록만) (" + appOom + "/" + kOom + ")");
            var dflt = Config.Default().Events.Notify;
            ctx.Check(dflt.SequenceEqual(new[] { "panic", "oops", "app_error", "confirm" }) && !EventKinds.Notifiable("boot", "hard") && !EventKinds.Notifiable("boot", "soft")
                      && !EventKinds.Notifiable("boot", "cmd") && EventKinds.Notifiable("boot", "hard", null, null, "panic") && EventKinds.Notifiable("boot", "hard", null, null, "oops")
                      && !EventKinds.Notifiable("bug", null) && !EventKinds.Notifiable("oom", null) && !EventKinds.Notifiable("missed", null) && EventKinds.Notifiable("confirm", null)
                      && !EventKinds.Notifiable("panic", null, "rate") && !EventKinds.Notifiable("panic", null, null, "cmd_output"),
                      "기본 notify = panic·oops·app_error·confirm: 원인 없는 soft·hard·cmd 부팅·bug·oom·missed 는 알리지 않음, panic/oops 원인 부팅은 알림, rate·suspect 는 그대로 안 알림");
            var c = Config.Parse("{\"events\": {\"notify\": [\"panic\", \"boot:hard\", \"panic\"]}}");
            var n = c.Events.Notify;
            ctx.Check(c.Validate().Count == 0 && n.SequenceEqual(new[] { "panic", "boot:hard" }) && EventKinds.Notifiable("boot", "hard", null, null, null, n)
                      && !EventKinds.Notifiable("boot", "soft", null, null, null, n) && !EventKinds.Notifiable("app_error", null, null, null, null, n)
                      && Config.Parse("{\"events\": {\"notify\": []}}").Events.Notify.Count == 0,
                      "events.notify 읽음(중복 하나로): [panic, boot:hard] → hard 부팅 알림·soft·app_error 안 알림, [] = 풍선 없음");
            string Errs(string json) { try { var x = Config.Parse(json); return string.Join(" | ", x.Validate()); } catch (ConfigException ce) { return string.Join(" | ", ce.Errors); } }
            string e1 = Errs("{\"events\": {\"notify\": [\"reboot\"]}}"), e2 = Errs("{\"events\": {\"notify\": \"panic\"}}"), e3 = Errs("{\"cables\": {\"A\": {\"events\": {\"notify\": []}}}}");
            ctx.Check(e1.Contains("events.notify: 알 수 없는 종류 'reboot'") && e2.Contains("events.notify 는 종류 문자열 배열") && e3.Contains("cables.A.events: 알 수 없는 키 'notify'"),
                      "events.notify 검증: 모르는 종류·배열 아님·케이블별 notify 거부 — " + string.Join(" / ", e1, e2, e3));
            string withN = "{\n  \"cables\": {\"CABN\": {\"alias\": \"eva\"}},\n  \"events\": {\"notify\": [\"panic\", \"boot:hard\"], \"x_future\": 1}\n}\n";
            string nb = Config.WithCableBaud(withN, "CABN", 9600, out string why);
            ctx.Check(nb != null && nb.Contains("\"events\": {\"notify\": [\"panic\", \"boot:hard\"], \"x_future\": 1}"), "C-12: cable baud 저장이 events.notify·모르는 키 원문을 그대로 둠" + (nb == null ? " — " + why : ""));
            EventRec Ev(int port, string kind, string cls, string cause = null, double t = 0)
                => new EventRec { Port = port, Kind = kind, Class = cls, Board = "SIMEV", CauseKind = cause, Context = @"C:\e\" + port + ".txt", Ts = new DateTimeOffset(T0.AddSeconds(t)) };
            var bl = new EventBalloons { WindowS = 60, SettleS = 2 };
            bl.Add(Ev(7101, "boot", "hard"), T0); bl.Add(Ev(7102, "panic", null), T0);
            var due = bl.Due(T0.AddSeconds(3));
            var custom = new EventBalloons { WindowS = 60, SettleS = 2, Notify = n };
            custom.Add(Ev(7103, "boot", "hard", null, 1), T0);
            var cd = custom.Due(T0.AddSeconds(3));
            ctx.Check(due.Count == 1 && due[0].Port == 7102 && due[0].Text == "SIMEV (7102): Kernel panic, 17:00:00. 누르면 앞뒤 로그가 열립니다."
                      && cd.Count == 1 && cd[0].Text == "SIMEV (7103): 예상 못 한 재부팅(하드), 17:00:01. 누르면 앞뒤 로그가 열립니다.",
                      "풍선: 원인 없는 hard 부팅은 없음·panic 은 있음(기본) · notify 에 boot:hard 를 넣으면 '예상 못 한 재부팅(하드)' (" + string.Join(" | ", due.Concat(cd).Select(b => b.Text)) + ")");
        }

        [Scenario("events-cli", "X6 실제 exe(감독 --no-tray --sim, C-13 격리, 코어 안 FakeBoard 주입 emit·reset): cli events <port> --follow --json = 머리 줄 뒤 새 사건마다 JSON 한 줄, 표준 입력 EOF 에 끝남 · --max 1 → 한 줄 뒤 exit 0 · --kind boot:soft,boot:hard 는 그 부팅만 · 표준 출력을 닫으면 다음 쓰기에 exit 141 · 감독 풍선(같은 묶음 1개 '외 1건', 창 끝 1개, hard 재부팅) · events --json(attach 포함 count)·--kind·--since·전체 포트·비JSON · aging --json·비JSON · 대상 COMx·보드명(허브 없이) · UART_PATH_STYLE wsl·mixed · 인자 오류 106·없는 대상 102 · 코어 명령(run·ls)은 --follow·--kind 를 받지 않음(106) · 읽기만(tx_bytes 그대로). 실제 시스템 무변경. + C.12: cli session start·status(--json·한 줄)·summary(= aging)·stop, 연결 안 된 COM 거부 102",
                  Order = 1302, TimeoutS = 240)]
        public static void CliFollow(ScenarioContext ctx)
        {
            var real = RealSystem.Capture(ctx);
            NotifyUnits(ctx);
            string c11Hub = C11Units(ctx);
            // C.11: 트레이 메뉴 글 — 단일 파일 exe(압축 없음, IL 문자열은 UTF-16)에 "자동 실행 설정"·"에이징 사건 열기"가 없고 새 메뉴 글이 있다
            byte[] exeBytes = File.ReadAllBytes(TestEnv.UartHubExe);
            bool InExe(string t) => exeBytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(t).AsSpan()) >= 0;
            ctx.Check(!InExe("자동 실행 설정") && !InExe("에이징 사건 열기") && !InExe("에이징 이벤트 열기") && InExe("에이징 폴더 열기") && InExe("미연결 UART 정리"),
                      "트레이 메뉴: '자동 실행 설정…' 항목 없음(exe 문자열 0), '에이징 이벤트 열기' → '에이징 폴더 열기'(C.12), 설정 ▸ '미연결 UART 정리…' 있음");
            var g = IconRig.Create(ctx, "evcli", 18640, 18650, "{\"cable\":\"SIMCABEC\",\"com\":\"COM640\",\"hostname\":\"SIMEVC\"}");   // 주기 printk 없음(probe 조용함 조건)
            var r = g.R;
            Upd.Start(r, TestEnv.UartHubExe, new[] { "sup", "--no-tray", "--hub-dir", g.Hub, "--sim", g.Spec, "--sim-dll", TestEnv.TestsDll, "--tick-ms", "100" },
                      Upd.Env("UART_HUB_TEST_EVENTS", "6,1"));
            g.WaitCore("감독 → 코어");
            ctx.WaitUntil(() => g.StatusOf("SIMCABEC") == "attached", 20000, "SIMCABEC attached");
            const int Port = 18650;
            string Ps = Port.ToString(CultureInfo.InvariantCulture);
            string evDir = Path.Combine(r.Root, "events");
            (int Code, JsonElement J, string Out, string Err) Cli(Dictionary<string, string> env, params string[] a)
            {
                var e = new Dictionary<string, string> { ["UART_HUB_DIR"] = g.Hub };
                if (env != null) foreach (var kv in env) e[kv.Key] = kv.Value;
                var o = r.Run(TestEnv.UartHubExe, new[] { "cli" }.Concat(a).ToArray(), e, 60000);
                return (o.Code, Upd.Parse(o.Out), o.Out.Trim(), o.Err.Trim());
            }
            long Tx() { foreach (var c in g.Ctl_("ls").GetProperty("cables").EnumerateArray()) if (J.S(c, "cable") == "SIMCABEC") return J.N(c, "tx_bytes"); return -1; }
            string Board() { foreach (var c in g.Ctl_("ls").GetProperty("cables").EnumerateArray()) if (J.S(c, "cable") == "SIMCABEC") return J.S(c, "board"); return null; }
            // 보드 이름(프롬프트)을 허브가 알게: probe --user-ok(Enter) — 이 뒤로는 보드로 보내지 않는다
            for (int i = 0; i < 20 && Board() != "SIMEVC"; i++) { g.Ctl_("probe", Ps, "--user-ok"); Thread.Sleep(400); }
            ctx.WaitUntil(() => Board() == "SIMEVC", 5000, "보드 이름 SIMEVC(probe --user-ok 뒤 프롬프트)");
            Thread.Sleep(500);
            long tx0 = Tx();

            // ① --follow 시작 → 머리 줄 · 다른 --follow(--kind boot:soft,boot:hard --max 1)
            var fol = FollowProc.Start(ctx, r, g.Hub, true, "events", Ps, "--follow", "--json");
            var folBoot = FollowProc.Start(ctx, r, g.Hub, true, "events", Ps, "--follow", "--json", "--kind", "boot:soft,boot:hard", "--max", "1");
            var folMax = FollowProc.Start(ctx, r, g.Hub, true, "events", Ps, "--follow", "--json", "--max", "1");
            var folPipe = FollowProc.Start(ctx, r, g.Hub, false, "events", Ps, "--follow", "--json");
            ctx.WaitUntil(() => fol.Lines.Count >= 1 && folBoot.Lines.Count >= 1 && folMax.Lines.Count >= 1, 10000, "--follow 머리 줄(셋)");
            string pipeHead = folPipe.ReadHead();
            folPipe.P.StandardOutput.Close();                                                  // 읽는 쪽이 닫힘(| head 가 끝난 것처럼)
            var head = JsonDocument.Parse(fol.Lines[0]).RootElement;
            ctx.Check(J.S(head, "status") == "ok" && J.B(head, "follow") == true && J.S(head, "events_dir") == evDir && J.A(head, "ports").SequenceEqual(new[] { Ps }) && pipeHead != null && pipeHead.Contains("\"follow\":true")
                      && DateTimeOffset.TryParse(J.S(head, "ts"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var hts) && Math.Abs((DateTimeOffset.Now - hts).TotalSeconds) < 60,
                      "--follow 머리 줄 {status ok, follow true, ts(기다리기 시작한 시각 — 다시 걸 때 --since), events_dir, ports [18650]}: " + fol.Lines[0]);

            // ② panic+oom 한 덩어리 → follow 2줄, 풍선 1개(oom 은 기록만 — 알림은 panic 하나), --max 1 은 한 줄 뒤 exit 0, 읽는 쪽 닫힘 → exit 141
            var sincePanic = System.Diagnostics.Stopwatch.StartNew();
            g.Inject("emit SIMCABEC " + B64("[  123.000000] Kernel panic - not syncing: cli test\r\n[  123.000100] Call trace:\r\n[  123.000200] sh invoked oom-killer: gfp_mask=0x0\r\n[  123.000300] ---[ end Kernel panic - not syncing: cli test ]---\r\n"), "panic·oom 줄");
            ctx.WaitUntil(() => fol.Lines.Count(l => l.Contains("\"kind\":\"panic\"") || l.Contains("\"kind\":\"oom\"")) == 2, 15000, "--follow: panic·oom 사건 줄 2");
            ctx.Check(folMax.P.WaitForExit(10000) && folMax.P.ExitCode == 0 && folMax.Lines.Count == 2 && folMax.Lines[1].Contains("\"kind\":\"panic\""), "--follow --max 1: 머리 줄 + 사건 1줄 뒤 exit 0");
            ctx.Check(folPipe.P.WaitForExit(10000) && folPipe.P.ExitCode == 141, "--follow: 표준 출력을 읽는 쪽이 닫히면 다음 쓰기에서 exit 141 (rc=" + (folPipe.P.HasExited ? folPipe.P.ExitCode.ToString(CultureInfo.InvariantCulture) : "?") + ")");
            var fp = JsonDocument.Parse(fol.Lines.First(l => l.Contains("\"kind\":\"panic\""))).RootElement;
            string ctxPath = J.S(fp, "context");
            ctx.Check(ctxPath != null && File.Exists(ctxPath) && ctxPath.StartsWith(evDir + "\\", StringComparison.OrdinalIgnoreCase) && J.N(fp, "port") == Port && J.S(fp, "board") == "SIMEVC",
                      "--follow 사건 줄: context = 시험 루트 안 앞뒤 로그(있음)·port·board(" + ctxPath + ")");
            string b1 = "풍선: SIMEVC (" + Port + "): Kernel panic, ";
            ctx.WaitUntil(() => g.Count(b1) == 1, 10000, "감독 풍선 '" + b1 + "…'");
            ctx.Check(Regex.IsMatch(g.Log(), Regex.Escape(b1) + @"\d\d:\d\d:\d\d\. 누르면 앞뒤 로그가 열립니다\. \(누르면 " + Regex.Escape(ctxPath) + @"\)") && !g.Log().Contains("건 묶음]"),
                      "풍선 1(UI_COPY.md): 'SIMEVC (18650): Kernel panic, hh:mm:ss. 누르면 앞뒤 로그가 열립니다.' — 같은 묶음의 oom 은 기록만(외 N건에 세지 않음), 누르면 그 앞뒤 로그");

            // ③ 창(6초) 안의 app_error·bug → 창 끝에 1개
            g.Inject("emit SIMCABEC " + B64("[2026/11/05-08:00:00] APPFAULT(ED)\r\n"), "app_error 줄");
            g.Inject("emit SIMCABEC " + B64("[  124.000000] WARNING: CPU: 0 PID: 9 at x.c:1 f+0x1/0x2\r\n"), "bug 줄");
            ctx.WaitUntil(() => fol.Lines.Any(l => l.Contains("\"kind\":\"app_error\"")) && fol.Lines.Any(l => l.Contains("\"kind\":\"bug\"")), 15000, "--follow: app_error·bug 사건 줄");
            string b2 = "풍선: SIMEVC (" + Port + "): 앱 오류, ";
            int firstTwo = g.Count("풍선: SIMEVC (" + Port + ")");
            ctx.WaitUntil(() => g.Count(b2) == 1, 15000, "창 끝 풍선 '" + b2 + "…'");
            ctx.Check(firstTwo == 1 && Regex.IsMatch(g.Log(), Regex.Escape(b2) + @"\d\d:\d\d:\d\d\. 누르면 앞뒤 로그가 열립니다\.") && !g.Log().Contains("커널 BUG·경고, "),
                      "풍선 2: 창 안의 사건은 그동안 풍선 없이 모았다가 창 끝에 1개 — app_error 만(bug 는 기록만)");
            ctx.Check(!folBoot.P.HasExited && folBoot.Lines.Count == 1, "--kind boot:soft,boot:hard: panic·oom·app_error·bug 는 내지 않음");

            // ④ 리셋 → boot hard → follow 줄·--kind boot:hard 는 그 한 줄 뒤 끝. 원인(cause) 없는 hard 리셋은 풍선 없이 기록만(2026-11-06 — 전원 on/off 에이징의 매 회차 리셋)
            //    cause 창(직전 20초 panic/oops)을 벗어나게 ②의 panic 뒤 22초를 채운 뒤 리셋한다
            int waitCause = 22000 - (int)sincePanic.ElapsedMilliseconds;
            if (waitCause > 0) Thread.Sleep(waitCause);
            int balloonsBeforeReset = g.Count("풍선: ");
            g.Inject("reset SIMCABEC", "와치독식 리셋");
            ctx.WaitUntil(() => fol.Lines.Any(l => l.Contains("\"kind\":\"boot\"") && l.Contains("\"class\":\"hard\"")), 15000, "--follow: boot hard 사건 줄");
            ctx.Check(folBoot.P.WaitForExit(10000) && folBoot.P.ExitCode == 0 && folBoot.Lines.Count == 2 && folBoot.Lines[1].Contains("\"class\":\"hard\""), "--kind boot:soft,boot:hard --max 1: hard 부팅 한 줄 뒤 exit 0");
            var hardLine = EventRec.Parse(fol.Lines.First(l => l.Contains("\"kind\":\"boot\"") && l.Contains("\"class\":\"hard\"")));
            Thread.Sleep(8500);                                                                // 창 6초 + 대기 1초 + 감독 1초 주기가 지나도록
            ctx.Check(hardLine.CauseKind == null && hardLine.Context != null && g.Count("풍선: ") == balloonsBeforeReset && !g.Log().Contains("예상 못 한 재부팅"),
                      "원인 없는 hard 리셋(panic 22초 뒤): jsonl·--follow·앞뒤 로그에는 남고 풍선은 없음(②의 panic 은 풍선 1) — 풍선 " + balloonsBeforeReset + " → " + g.Count("풍선: "));
            int nFollow = fol.Lines.Count;
            fol.P.StandardInput.Close();
            ctx.Check(fol.P.WaitForExit(5000), "--follow: 표준 입력 EOF(래퍼가 끝남) → CLI 끝남 rc=" + (fol.P.HasExited ? fol.P.ExitCode.ToString(CultureInfo.InvariantCulture) : "?"));
            ctx.Check(nFollow == 6 && fol.Lines.Skip(1).All(l => EventRec.Parse(l) != null), "--follow 출력 = 머리 1 + 사건 5(panic·oom·app_error·bug·boot), 줄마다 사건 JSON");

            // ⑤ events·aging(한 번) — 대상 포트·COMx·보드명
            var ev = Cli(null, "events", Ps, "--json");
            var evs = ev.J.GetProperty("events").EnumerateArray().Select(x => EventRec.Parse(x.GetRawText())).ToList();
            ctx.Check(ev.Code == 0 && J.N(ev.J, "count") == 6 && string.Join(",", evs.Select(x => x.Kind)) == "attach,panic,oom,app_error,bug,boot" && evs.Where(x => x.Context != null).All(x => File.Exists(x.Context))
                      && evs[0].Com == "COM640" && evs[0].Reason == "start",
                      "events --json: count 6(attach·panic·oom·app_error·bug·boot) — attach(com COM640·start), 앞뒤 로그 모두 있음 (" + string.Join(",", evs.Select(x => x.Kind)) + ")");
            var ek = Cli(null, "events", Ps, "--json", "--kind", "panic,boot:hard");
            var es = Cli(null, "events", Ps, "--json", "--since", "2099-01-01T00:00");
            var eh = Cli(null, "events", Ps, "--json", "--since", "1h");
            var eall = Cli(null, "events", "--json");
            var ecom = Cli(null, "events", "COM640", "--json", "--kind", "panic");
            var eboard = Cli(null, "aging", "SIMEVC", "--json");
            var enope = Cli(null, "events", "NOPE", "--json");
            ctx.Check(J.N(ek.J, "count") == 2 && J.N(es.J, "count") == 0 && J.N(eh.J, "count") == 6 && J.A(eall.J, "ports").Contains(Ps) && J.N(eall.J, "count") == 6
                      && ecom.Code == 0 && J.N(ecom.J, "count") == 1 && eboard.Code == 0 && J.N(eboard.J, "port") == Port && enope.Code == 102 && J.S(enope.J, "status") == "no_cable",
                      "events --kind panic,boot:hard → 2 · --since 미래 → 0 · --since 1h → 6 · 포트 없이 = 모든 포트 · 대상 COM640·보드명 SIMEVC → 18650(허브에 묻지 않음) · 없는 대상 102");
            var en = Cli(null, "events", Ps);
            ctx.Check(en.Code == 0 && en.Out.Contains(" 18650 SIMEVC  panic ") && en.Out.Contains("    앞뒤 로그: " + ctxPath) && en.Out.Contains("boot(hard)"), "events(비JSON): 사건마다 한 줄 + '앞뒤 로그:' 경로");
            var ag = Cli(null, "aging", Ps, "--json");
            var bo = ag.J.GetProperty("boots"); var kd = ag.J.GetProperty("kinds");
            ctx.Check(ag.Code == 0 && J.N(bo, "total") == 1 && J.N(bo, "hard") == 1 && J.N(ag.J, "unexpected_reboots") == 1 && J.N(kd.GetProperty("panic"), "count") == 1
                      && J.N(kd.GetProperty("oom"), "count") == 1 && J.N(kd.GetProperty("app_error"), "count") == 1 && J.N(kd.GetProperty("bug"), "count") == 1 && J.N(ag.J, "events") == 5
                      && ag.J.GetProperty("contexts").EnumerateArray().Any(x => J.S(x, "context") == ctxPath && J.B(x, "exists") == true)
                      && ag.J.GetProperty("contexts").EnumerateArray().Any(x => J.S(x, "kind") == "boot:hard") && J.S(ag.J, "events_file") == Path.Combine(evDir, "18650.jsonl")
                      && ag.J.TryGetProperty("covered_s", out _),
                      "aging --json: 부팅 1(hard)·panic/oom/app_error/bug 각 1·앞뒤 로그(panic·hard 부팅 앞 줄)·events_file·covered_s");
            var agn = Cli(null, "aging", Ps);
            ctx.Check(agn.Code == 0 && agn.Out.Contains("부팅 1회: cmd 0 · soft 0 · hard 1 (예상 못 한 재부팅 1)") && agn.Out.Contains("앞뒤 로그 ") && agn.Out.Contains("기록한 시간 "), "aging(비JSON): 부팅 줄·기록한 시간·종류 표·앞뒤 로그 목록");

            // ⑥ 경로 표기(래퍼가 정하는 UART_PATH_STYLE)
            var ew = Cli(Upd.Env("UART_PATH_STYLE", "wsl", "UART_WSL_MOUNT", "/mnt/"), "events", Ps, "--json", "--kind", "panic");
            var am = Cli(Upd.Env("UART_PATH_STYLE", "mixed"), "aging", Ps, "--json");
            string drive = ctxPath.Substring(0, 1);
            string wslCtx = J.S(ew.J.GetProperty("events")[0], "context"), mixCtx = J.S(am.J.GetProperty("contexts")[0], "context");
            ctx.Check(wslCtx == "/mnt/" + drive.ToLowerInvariant() + "/" + ctxPath.Substring(3).Replace('\\', '/') && J.S(ew.J, "events_dir").StartsWith("/mnt/", StringComparison.Ordinal)
                      && mixCtx != null && Regex.IsMatch(mixCtx, "^[A-Za-z]:/") && !mixCtx.Contains('\\') && J.S(am.J, "events_file").StartsWith(drive + ":/", StringComparison.Ordinal),
                      "경로 표기: wsl → " + wslCtx + " · mixed → " + mixCtx);

            // ⑦ 인자 오류 106 · 코어 명령은 --follow·--kind 를 받지 않음 · 읽기만
            var x2 = Cli(null, "aging", "--json"); var x3 = Cli(null, "events", Ps, "--kind", "nope", "--json");
            var x4 = Cli(null, "events", Ps, "--since", "yesterday", "--json"); var x5 = Cli(null, "aging", Ps, "--follow", "--json");
            var x6 = Cli(null, "events", Ps, "--max", "1", "--json"); var x7 = Cli(null, "events", Ps, "--follow", "--max", "0", "--json");
            ctx.Check(new[] { x2, x3, x4, x5, x6, x7 }.All(x => x.Code == 106 && J.S(x.J, "status") == "rejected"),
                      "인자 오류 106: aging 대상 없음 · 모르는 --kind · 잘못된 --since · aging --follow · --follow 없는 --max · --max 0 (" + string.Join(",", new[] { x2, x3, x4, x5, x6, x7 }.Select(x => x.Code)) + ")");
            var y1 = Cli(null, "run", Ps, "echo x", "--follow", "--json"); var y2 = Cli(null, "ls", "--kind", "panic", "--json");
            ctx.Check(y1.Code == 106 && (J.S(y1.J, "error") ?? "").Contains("--follow") && y2.Code == 106 && (J.S(y2.J, "error") ?? "").Contains("--kind"),
                      "코어 명령은 --follow·--kind 를 받지 않음: run … --follow → 106 '" + J.S(y1.J, "error") + "', ls --kind → 106(Phase A 계약 그대로)");
            // C.11: log_layout 2 허브 폴더의 jsonl(<허브 폴더>\events)을 uart events·aging 이 허브 없이 읽음
            var h2 = new Dictionary<string, string> { ["UART_HUB_DIR"] = c11Hub };
            var e2 = Cli(h2, "events", "18701", "--json"); var a2 = Cli(h2, "aging", "18701", "--json");
            var e2k = e2.Code == 0 ? e2.J.GetProperty("events").EnumerateArray().Select(x => J.S(x, "kind")).ToList() : new List<string>();
            ctx.Check(e2.Code == 0 && e2k.Contains("panic") && e2k.Contains("oops") && e2k.Contains("boot") && a2.Code == 0 && J.N(a2.J.GetProperty("boots"), "total") >= 1
                      && a2.J.GetProperty("contexts").EnumerateArray().Any(c => (J.S(c, "context") ?? "").EndsWith("_커널패닉.txt", StringComparison.Ordinal) && c.GetProperty("exists").GetBoolean())
                      && (J.S(a2.J, "events_file") ?? "").Contains(@"\hub\events\", StringComparison.OrdinalIgnoreCase),
                      "log_layout 2: uart events·aging 이 <허브 폴더>\\events\\18701.jsonl 을 읽음(events " + e2.Code + " [" + string.Join(",", e2k) + "], aging " + a2.Code + " " + J.S(a2.J, "events_file") + ")");
            AgingScenarios.Cli(ctx, (env, a) => Cli(env, a), Ps, t => g.Inject("emit SIMCABEC " + B64(t), "에이징 세션 줄"));   // 부록 C.12: cli session start·status·summary·stop
            ctx.Check(Tx() == tx0, "events·aging·--follow·session 은 보드로 보내지 않음: 케이블 tx_bytes 그대로(" + tx0 + ")");
            ctx.Check(!g.Log().Contains("사건 감지") && !g.Log().Contains("사건 알림 읽기 실패"), "hub.log 에 사건 감지·알림 오류 0");
            g.Violations();
            r.CheckNoProcs("정리");
            real.Verify(ctx, r);
        }
    }
}
