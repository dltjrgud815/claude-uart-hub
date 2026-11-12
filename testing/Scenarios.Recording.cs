// 기록 코어 시나리오(US-002): 14 전환, 15 재연결·고정 포트, idx-gap, overflow.
// 기대값은 규칙(계획 §3.1)에서 손으로 계산한다. 구현 코드로 기대값을 만들지 않는다.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UartHub.Testing
{
    internal static class RecordingScenarios
    {
        // ------------------------------------------------------------------ 도우미
        static Hub StartHub(ScenarioContext ctx, Config cfg, IClock clock, FakePortProvider prov)
        {
            var hub = new Hub(cfg, clock, new IPortProvider[] { prov });
            hub.Start();
            ctx.Defer(hub.Stop);
            return hub;
        }

        static Cable WaitAttached(ScenarioContext ctx, Hub hub, string id, string com = null, int timeoutMs = 5000)
        {
            Cable c = null;
            ctx.WaitUntil(() => { c = hub.Cables.Find(id); return c != null && c.IsAttached && (com == null || c.Com == com); },
                          timeoutMs, id + " attached" + (com != null ? " @" + com : ""));
            return c;
        }

        static void Send(ScenarioContext ctx, Cable c, string s)
        {
            if (!c.Write(Encoding.UTF8.GetBytes(s), TxSource.Human)) ctx.Fail("Cable.Write 실패: " + c.LastError);
        }

        /// <summary>보드가 낸 바이트를 허브가 모두 읽고 writer 가 flush 까지 마칠 때까지.</summary>
        static void Settle(ScenarioContext ctx, Cable c, FakeBoard b, string what, int timeoutMs = 5000)
        {
            ctx.WaitUntil(() => b.PendingBytes == 0 && c.RxBytes == b.TotalSent && c.WaitDrained(0), timeoutMs, what);
        }

        static string Lines(string tag, int n, int len)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < n; i++)
            {
                string head = tag + i.ToString("D6", CultureInfo.InvariantCulture) + " ";
                sb.Append(head).Append('x', Math.Max(0, len - head.Length - 2)).Append("\r\n");
            }
            return sb.ToString();
        }

        static List<string> BasesIn(string dir)
        {
            if (!Directory.Exists(dir)) return new List<string>();
            return Directory.GetFiles(dir, "*.log").Select(f => Path.GetFileNameWithoutExtension(f)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        }

        static string ReadLog(string dir, string bas) => LogFiles.ReadShared(Path.Combine(dir, bas + ".log"));

        static List<JsonElement> ReadIdx(string dir, string bas)
        {
            var list = new List<JsonElement>();
            foreach (var l in LogFiles.ReadShared(Path.Combine(dir, bas + ".idx")).Split('\n'))
                if (l.Trim().Length > 0) list.Add(JsonDocument.Parse(l).RootElement.Clone());
            return list;
        }

        static string[] LogLines(string text) => text.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);

        // ================================================================== 14
        [Scenario("14", "전환: 자정(주입 시각)·rotate(축소)·보드명 최초 감지 rename·보드 교체 → 파일명 규칙, .raw 합계 == 송신 합계", Order = 140, TimeoutS = 90)]
        public static void S14(ScenarioContext ctx)
        {
            var clock = new ShiftClock(new DateTime(2026, 10, 29, 23, 59, 50));
            var cfg = ctx.MakeConfig();
            cfg.RotateMb = 64.0 / 1024;                       // 64KB
            long limit = cfg.RotateBytes;
            var b = new FakeBoard(new FakeBoardOptions { Hostname = "SIMA", MaxChunk = 1024 });
            ctx.Defer(b.PowerOff);
            var prov = new FakePortProvider();
            prov.Plug(b, "SIM14", "COM91");
            var hub = StartHub(ctx, cfg, clock, prov);
            var cab = WaitAttached(ctx, hub, "SIM14");
            ctx.Check(cab.TcpPort == 17001, "TCP 포트 17001 배정");

            string day1 = Path.Combine(cfg.LogRoot, "20261029");
            string day2 = Path.Combine(cfg.LogRoot, "20261030");
            ctx.WaitUntil(() => BasesIn(day1).Count == 1 && Regex.IsMatch(BasesIn(day1)[0], @"^17001_unknown_2359\d\d$"), 3000, "attach 직후 20261029\\17001_unknown_HHMMSS");
            string stamp = BasesIn(day1)[0].Substring("17001_unknown_".Length);

            // ---- 1) 보드명 최초 감지(60초 이내) → 열린 파일 rename
            Send(ctx, cab, "\r");
            ctx.WaitUntil(() => cab.Board == "SIMA", 3000, "프롬프트에서 보드명 SIMA 감지");
            Settle(ctx, cab, b, "프롬프트 수신·기록");
            ctx.WaitUntil(() => BasesIn(day1).SequenceEqual(new[] { "17001_SIMA_" + stamp }), 3000, "17001_unknown_" + stamp + " → 17001_SIMA_" + stamp + " rename");
            ctx.Check(new[] { ".raw", ".log", ".idx" }.All(e => File.Exists(Path.Combine(day1, "17001_SIMA_" + stamp + e))) && Directory.GetFiles(day1).Length == 3,
                      ".raw/.log/.idx 세 파일 모두 rename, unknown 잔존 없음");
            ctx.Check(ReadIdx(day1, "17001_SIMA_" + stamp).Any(j => j.GetProperty("k").GetString() == "rename"), ".idx 에 rename 기록");
            ctx.Check(ReadLog(day1, "17001_SIMA_" + stamp).Contains("*** attach COM91 cable=SIM14 port=17001 baud=115200 ***"), "첫 파일에 *** attach *** 줄");

            // ---- 2) 크기 전환(rotate 64KB) 2회
            for (int k = 0; k < 2; k++)
            {
                clock.Jump(TimeSpan.FromSeconds(1));
                b.Emit(Lines("R" + k + "-", 700, 100));
                Settle(ctx, cab, b, "70KB 출력 " + (k + 1) + " 기록");
            }
            var d1 = BasesIn(day1);
            ctx.Check(d1.Count == 3 && d1.All(x => Regex.IsMatch(x, @"^17001_SIMA_2359\d\d(_\d+)?$")), "크기 전환 2회 → SIMA 파일 3개: " + string.Join(", ", d1));
            for (int i = 0; i < 2; i++)
            {
                var lines = LogLines(ReadLog(day1, d1[i]));
                long raw = new FileInfo(Path.Combine(day1, d1[i] + ".raw")).Length;
                long log = new FileInfo(Path.Combine(day1, d1[i] + ".log")).Length;
                ctx.Check(lines.Last().Contains("*** rotate (size) -> " + d1[i + 1] + " ***"), d1[i] + " 끝 줄 *** rotate (size) -> " + d1[i + 1] + " ***");
                ctx.Check(Math.Max(raw, log) >= limit && Math.Max(raw, log) < limit + 1024, d1[i] + " 크기 " + Math.Max(raw, log) + " ∈ [rotate, rotate+1KB)");
            }
            ctx.Check(LogLines(ReadLog(day1, d1[1]))[0].Contains("*** rotate (size) <- " + d1[0] + " ***"), "새 파일 첫 줄 *** rotate (size) <- 이전 ***");

            // ---- 3) 자정 전환(주입 시각), 경과초 연속
            double lastDay1 = LogLines(ReadLog(day1, d1[2])).Select(LogFiles.Elapsed).Where(x => x.HasValue).Last().Value;
            clock.JumpTo(new DateTime(2026, 10, 30, 0, 0, 1));
            b.Emit(Lines("M-", 10, 50));
            Settle(ctx, cab, b, "자정 뒤 출력 기록");
            var d2 = BasesIn(day2);
            ctx.Check(d2.Count == 1 && Regex.IsMatch(d2[0], @"^17001_SIMA_0000\d\d$"), "자정 → 20261030\\17001_SIMA_0000xx: " + string.Join(", ", d2));
            ctx.Check(LogLines(ReadLog(day1, d1[2])).Last().Contains("*** rotate (midnight) -> " + d2[0] + " ***"), "전날 마지막 파일 끝 *** rotate (midnight) ***");
            var firstDay2 = LogLines(ReadLog(day2, d2[0]));
            double firstEl = LogFiles.Elapsed(firstDay2[0]).Value;
            ctx.Check(lastDay1 >= 2.0 && firstEl > lastDay1, "경과초는 attach 기준으로 이어짐(파일 전환 후 0 으로 돌아가지 않음: " + lastDay1.ToString("F3") + " → " + firstEl.ToString("F3") + ")");
            ctx.Check(firstDay2[0].StartsWith("[2026-10-30 00:00:0", StringComparison.Ordinal), "새 날짜 첫 줄 시각 = 주입 시각");

            // ---- 4) 보드 교체(재부팅 후 다른 호스트명) → 다음 줄부터 새 파일, 부팅 경계 .idx
            b.Options.NextHostname = "SIMB";
            Send(ctx, cab, "reboot\r");
            ctx.WaitUntil(() => b.Phase == "postboot", 5000, "보드 재부팅 완료(프롬프트 없음)");
            Settle(ctx, cab, b, "부팅 로그 기록");
            Send(ctx, cab, "\r");
            ctx.WaitUntil(() => cab.Board == "SIMB", 3000, "새 보드명 SIMB 감지");
            Send(ctx, cab, "echo after-swap\r");
            ctx.WaitUntil(() => BasesIn(day2).Any(x => x.StartsWith("17001_SIMB_", StringComparison.Ordinal)), 3000, "17001_SIMB_ 새 파일 생성");
            Settle(ctx, cab, b, "교체 뒤 출력 기록");
            d2 = BasesIn(day2);
            string simb = d2.Single(x => x.StartsWith("17001_SIMB_", StringComparison.Ordinal));
            string simaDay2 = d2.Single(x => x.StartsWith("17001_SIMA_", StringComparison.Ordinal));
            ctx.Check(Regex.IsMatch(simb, @"^17001_SIMB_0000\d\d(_\d+)?$"), "보드 교체 파일명 " + simb);
            ctx.Check(LogLines(ReadLog(day2, simb)).Any(l => l.EndsWith("] after-swap", StringComparison.Ordinal)), "교체 뒤 출력이 SIMB 파일에 기록");
            ctx.Check(ReadLog(day2, simaDay2).Contains("*** rotate (board) -> " + simb + " ***"), "SIMA 파일에 *** rotate (board) -> SIMB ***");
            var boots = ReadIdx(day2, simaDay2).Where(j => j.GetProperty("k").GetString() == "boot").ToList();
            ctx.Check(boots.Count == 1, "재부팅 1회 → .idx boot 기록 1건(같은 부팅의 LDR>T·[0.0x] 병합): " + boots.Count);
            ctx.Check(boots[0].GetProperty("m").GetString().Contains("reboot: Restarting system") && boots[0].GetProperty("board").GetString() == "SIMA",
                      ".idx boot: m='reboot: Restarting system', board=SIMA");
            long bootOff = boots[0].GetProperty("off").GetInt64();
            var raw2 = File.ReadAllBytes(Path.Combine(day2, simaDay2 + ".raw"));
            string atOff = Encoding.ASCII.GetString(raw2, (int)bootOff, Math.Min(60, raw2.Length - (int)bootOff));
            ctx.Check(Regex.IsMatch(atOff, @"^\[\s*\d+\.\d{6}\] reboot: Restarting system"), ".idx boot offset 이 .raw 의 해당 줄 시작을 가리킴");
            var simbOpen = ReadIdx(day2, simb).First();
            ctx.Check(simbOpen.GetProperty("k").GetString() == "open" && simbOpen.GetProperty("board").GetString() == "SIMB" && simbOpen.GetProperty("reason").GetString() == "board",
                      "SIMB .idx open 기록(board=SIMB, reason=board)");

            // ---- 5) 정지 → 바이트 무손실·파일명 규칙
            hub.Stop();
            long rawTotal = LogFiles.RawTotal(cfg.LogRoot);
            ctx.Check(rawTotal == b.TotalSent, ".raw 합계 " + rawTotal + " == FakeBoard 송신 " + b.TotalSent);
            var all = LogFiles.All(cfg.LogRoot);
            ctx.Check(all.All(f => LogFiles.NameRe.IsMatch(Path.GetFileName(f))), "모든 파일명이 <포트>_<보드|unknown>_<HHMMSS>[_n].{log,raw,idx}: " + all.Count + "개");
            ctx.Check(LogLines(ReadLog(day2, simb)).Last().Contains("*** hub stop ***"), "마지막 파일 끝 *** hub stop ***");
            ctx.Check(File.ReadAllText(Path.Combine(cfg.EffectiveHubDir, "state.json")).Contains("\"SIM14\": \"SIMB\""), "state.json boards 에 마지막 보드명");
        }

        // ================================================================== 15
        [Scenario("15", "detach→attach 재연결, *** detach/attach *** 기록, 같은 케이블 ID 에 다른 COM 이어도 같은 TCP 포트(state.json)", Order = 150, TimeoutS = 60)]
        public static void S15(ScenarioContext ctx)
        {
            ctx.Check(WmiPortProvider.ParseCableId(@"FTDIBUS\VID_0403+PID_6001+FT1ABCDE\0000") == "FT1ABCDE"
                      && WmiPortProvider.ParseCableId(@"FTDIBUS\VID_0403+PID_6011+6&1A2B3C4D&0&3\0000") == "6&1A2B3C4D&0&3",
                      "WMI DeviceID → 케이블 ID 파싱(단채널 iSerial, 4채널 인스턴스 ID)");

            var cfg = ctx.MakeConfig();
            var a = new FakeBoard(new FakeBoardOptions { Hostname = "SIM15A" });
            var bb = new FakeBoard(new FakeBoardOptions { Hostname = "SIM15B" });
            ctx.Defer(a.PowerOff);
            ctx.Defer(bb.PowerOff);
            var prov = new FakePortProvider();
            prov.Plug(a, "SIMCAB15A", "COM93");
            var hub = StartHub(ctx, cfg, null, prov);
            var ca = WaitAttached(ctx, hub, "SIMCAB15A", "COM93");
            ctx.Check(ca.TcpPort == 17001, "첫 케이블 → :17001");
            Send(ctx, ca, "\r");
            ctx.WaitUntil(() => ca.Board == "SIM15A", 3000, "보드명 SIM15A");
            prov.Plug(bb, "SIMCAB15B", "COM97");
            var cb = WaitAttached(ctx, hub, "SIMCAB15B", "COM97");
            ctx.Check(cb.TcpPort == 17002, "둘째 케이블 → :17002");

            // ---- 탈착 3회: 예외 타입과 무관하게 detach(S0-6), 매번 다른 COM 으로 재연결해도 같은 포트
            var excs = new Exception[] { new OperationCanceledException("The I/O operation has been aborted"), new IOException("device gone"), new InvalidOperationException("odd state") };
            string[] coms = { "COM94", "COM90", "COM99" };
            string prevCom = "COM93";
            for (int i = 0; i < excs.Length; i++)
            {
                prov.Unplug("SIMCAB15A", excs[i]);
                ctx.WaitUntil(() => ca.Status == CableStatus.Detached, 3000, "탈착(" + excs[i].GetType().Name + ") → detached");
                ctx.WaitUntil(() => LogFiles.AllLogText(cfg.LogRoot).Contains("*** detach " + prevCom + " (" + excs[i].GetType().Name + ") ***"), 3000,
                              "*** detach " + prevCom + " (" + excs[i].GetType().Name + ") *** 기록");
                prov.Plug(a, "SIMCAB15A", coms[i]);
                var again = WaitAttached(ctx, hub, "SIMCAB15A", coms[i]);
                ctx.Check(ReferenceEquals(again, ca) && again.TcpPort == 17001, "재연결 @" + coms[i] + " → 같은 케이블, 같은 :17001");
                ctx.WaitUntil(() => LogFiles.AllLogText(cfg.LogRoot).Contains("*** attach " + coms[i] + " cable=SIMCAB15A port=17001 baud=115200 ***"), 3000,
                              "*** attach " + coms[i] + " *** 기록");
                Send(ctx, ca, "echo back-" + i + "\r");
                ctx.WaitUntil(() => LogFiles.AllLogText(cfg.LogRoot).Contains("] back-" + i + "\r\n"), 3000, "재연결 뒤 RX 기록(back-" + i + ")");
                prevCom = coms[i];
            }
            ctx.Check(cb.IsAttached && cb.Com == "COM97", "다른 케이블은 계속 attached");
            Send(ctx, cb, "echo b-alive\r");
            ctx.WaitUntil(() => LogFiles.AllLogText(cfg.LogRoot).Contains("] b-alive\r\n"), 3000, "다른 케이블 기록 무중단");

            string statePath = Path.Combine(cfg.EffectiveHubDir, "state.json");
            using (var doc = JsonDocument.Parse(File.ReadAllText(statePath)))
            {
                var asg = doc.RootElement.GetProperty("assign");
                ctx.Check(asg.GetProperty("SIMCAB15A").GetInt32() == 17001 && asg.GetProperty("SIMCAB15B").GetInt32() == 17002, "state.json assign: SIMCAB15A=17001, SIMCAB15B=17002");
            }
            ctx.Check(File.Exists(Path.Combine(cfg.EffectiveHubDir, "alive", "17001")), "하트비트 alive\\17001");
            hub.Stop();

            // ---- 코어 재시작: 순서를 바꿔 꽂고 COM 도 바꿔도 포트 유지, 보드명은 state 에서 이어받음
            var prov2 = new FakePortProvider();
            prov2.Plug(bb, "SIMCAB15B", "COM98");
            var hub2 = StartHub(ctx, cfg, null, prov2);
            var cb2 = WaitAttached(ctx, hub2, "SIMCAB15B", "COM98");
            prov2.Plug(a, "SIMCAB15A", "COM95");
            var ca2 = WaitAttached(ctx, hub2, "SIMCAB15A", "COM95");
            ctx.Check(cb2.TcpPort == 17002 && ca2.TcpPort == 17001, "코어 재시작 + 순서·COM 변경 → 포트 유지(A=17001, B=17002)");
            ctx.WaitUntil(() => ca2.Writer.CurrentLogPath != null && Path.GetFileName(ca2.Writer.CurrentLogPath).StartsWith("17001_SIM15A_", StringComparison.Ordinal), 3000,
                          "재시작 뒤 새 파일이 state 의 보드명(17001_SIM15A_)으로 생성");
            hub2.Stop();

            // ---- 케이블별 무손실
            var all = LogFiles.All(cfg.LogRoot).Where(f => f.EndsWith(".raw", StringComparison.Ordinal)).ToList();
            long rawA = all.Where(f => Path.GetFileName(f).StartsWith("17001_", StringComparison.Ordinal)).Sum(f => new FileInfo(Path.Combine(cfg.LogRoot, f)).Length);
            long rawB = all.Where(f => Path.GetFileName(f).StartsWith("17002_", StringComparison.Ordinal)).Sum(f => new FileInfo(Path.Combine(cfg.LogRoot, f)).Length);
            ctx.Check(rawA == a.TotalSent && rawB == bb.TotalSent, ".raw 합계 == 송신: A " + rawA + "/" + a.TotalSent + ", B " + rawB + "/" + bb.TotalSent);
        }

        // ================================================================== idx-gap
        [Scenario("idx-gap", ".idx 희소 간격: RX 간격 ≥20ms 만 (offset, gap_ms) 기록, 20ms 미만은 기록 안 함", Order = 160, TimeoutS = 60)]
        public static void IdxGap(ScenarioContext ctx)
        {
            var clock = new ManualClock(new DateTime(2026, 10, 29, 12, 0, 0));
            var cfg = ctx.MakeConfig();
            ctx.Check(cfg.GapMs == 20, "gap_ms 기본 20");
            var b = new FakeBoard(new FakeBoardOptions { Hostname = "SIMG", MaxChunk = 64 });
            ctx.Defer(b.PowerOff);
            var prov = new FakePortProvider();
            prov.Plug(b, "SIMGAP", "COM92");
            var hub = StartHub(ctx, cfg, clock, prov);
            var cab = WaitAttached(ctx, hub, "SIMGAP");

            // (앞 RX 이후 진행할 ms, 바이트 수). 첫 항목은 attach 기준 간격 0.
            var plan = new[] { (0, 100), (5, 30), (19, 40), (20, 50), (250, 10), (1, 70), (21, 5), (19, 64), (0, 7) };
            var expected = new List<string>();
            var arrivals = new List<(long off, long mono)>();
            var sent = new MemoryStream();
            long off = 0;
            int idx = 0;
            foreach (var (adv, len) in plan)
            {
                clock.Advance(adv);
                if (adv >= 20) expected.Add(off + ":" + adv);
                arrivals.Add((off, clock.MonoMs));
                string tag = "G" + idx;
                idx++;
                var chunk = Encoding.ASCII.GetBytes(tag + new string('x', len - 2 - tag.Length) + "\r\n");
                sent.Write(chunk, 0, chunk.Length);
                b.EmitBytes(chunk);
                ctx.WaitUntil(() => b.PendingBytes == 0 && cab.RxBytes == b.TotalSent, 2000, "청크 " + idx + " 수신(+" + adv + "ms, " + len + "B)");
                off += len;
            }
            Settle(ctx, cab, b, "기록 완료");

            // 링 버퍼 구독 API(이후 wait --since·telnet·PromptTracker 용): 바이트·도착 시각·epoch:offset
            var ring = cab.Ring;
            var cursor = ring.OpenCursor(0);
            var got = new MemoryStream();
            RingRead rr;
            while ((rr = cursor.Next(0)).Status == RingReadStatus.Data) got.Write(rr.Data, 0, rr.Data.Length);
            ctx.Check(rr.Status == RingReadStatus.Timeout && got.ToArray().SequenceEqual(sent.ToArray()) && cursor.Position == ring.End,
                      "링 커서로 읽은 바이트 == 보낸 바이트(" + got.Length + "B), 끝에서 Timeout");
            ctx.Check(arrivals.All(x => ring.TryGetArrival(x.off, out var mono, out _) && mono == x.mono), "링 도착 시각(주입 시계) == 각 청크 송신 시각");
            ctx.Check(ring.Format(170) == ring.Epoch + ":170" && RingBuffer.TryParse(ring.Format(170), out var ep, out var o2) && ep == ring.Epoch && o2 == 170,
                      "절대 offset 표기 epoch:offset 왕복");
            hub.Stop();

            string day = Path.Combine(cfg.LogRoot, "20261029");
            var bas = BasesIn(day).Single();
            var gaps = ReadIdx(day, bas).Where(j => j.GetProperty("k").GetString() == "gap")
                                        .Select(j => j.GetProperty("off").GetInt64() + ":" + j.GetProperty("ms").GetInt64()).ToList();
            ctx.Log("기대 gap(off:ms) = " + string.Join(", ", expected) + " / 실제 = " + string.Join(", ", gaps));
            ctx.Check(gaps.SequenceEqual(expected), ".idx gap 기록 == 기대(≥20ms 만, 정확한 offset)");
            ctx.Check(new FileInfo(Path.Combine(day, bas + ".raw")).Length == off && off == b.TotalSent, ".raw 크기 == 송신 합계 " + off);
        }

        // ================================================================== overflow
        [Scenario("overflow", "writer 인위 정지 → 큐 상한(축소) 초과분 drop + *** overflow dropped N bytes ***, PortReader 는 멈추지 않음", Order = 170, TimeoutS = 60)]
        public static void Overflow(ScenarioContext ctx)
        {
            var cfg = ctx.MakeConfig();
            cfg.QueueMb = 64.0 / 1024;                         // 64KB
            cfg.RingMb = 0.25;                                 // 256KB: 512KB 를 넣으면 앞부분이 덮어써진다
            var b = new FakeBoard(new FakeBoardOptions { Hostname = "SIMO", MaxChunk = 4096 });
            ctx.Defer(b.PowerOff);
            var prov = new FakePortProvider();
            prov.Plug(b, "SIMOVF", "COM96");
            var hub = StartHub(ctx, cfg, null, prov);
            var cab = WaitAttached(ctx, hub, "SIMOVF");
            Send(ctx, cab, "echo before-overflow\r");
            Settle(ctx, cab, b, "정지 전 출력 기록");

            ctx.Check(cab.Writer.TestPause(2000), "DiskWriter 인위 정지");
            long writtenAtPause = cab.Writer.WrittenRawBytes;
            b.Emit(Lines("O-", 8000, 64));                     // 512,000B ≫ 64KB
            ctx.WaitUntil(() => b.PendingBytes == 0 && cab.RxBytes == b.TotalSent, 10000, "writer 정지 중에도 PortReader 가 끝까지 읽음(RxBytes == 송신)");
            ctx.Check(cab.Ring.End == b.TotalSent, "링 버퍼에도 전부 적재(End == 송신)");
            var slow = cab.Ring.OpenCursor(0).Next(0);
            ctx.Check(slow.Status == RingReadStatus.Lost && slow.Skipped == cab.Ring.Start && cab.Ring.Start == b.TotalSent - 262144,
                      "느린 구독자(offset 0)는 Lost(" + slow.Skipped + "B 건너뜀)로 알림, 링은 최근 256KB 유지");
            ctx.Check(cab.QueueBytes <= cfg.QueueBytes, "큐 바이트 " + cab.QueueBytes + " ≤ 상한 " + cfg.QueueBytes);
            ctx.Check(cab.Writer.WrittenRawBytes == writtenAtPause, "정지 중 디스크 기록 0바이트");
            long dropped = cab.QueueDroppedBytes;
            ctx.Check(dropped > 0 && dropped >= 512000 - cfg.QueueBytes, "drop 바이트 " + dropped);

            cab.Writer.TestResume();
            b.EmitAppLog("AFTER-OVERFLOW");
            Settle(ctx, cab, b, "재개 뒤 기록");
            hub.Stop();

            string log = LogFiles.AllLogText(cfg.LogRoot);
            var ms = Regex.Matches(log, @"\*\*\* overflow dropped (\d+) bytes \*\*\*");
            long logged = ms.Cast<Match>().Sum(m => long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            ctx.Check(ms.Count >= 1 && logged == dropped, "*** overflow dropped N bytes *** 기록, N 합 " + logged + " == drop " + dropped);
            long raw = LogFiles.RawTotal(cfg.LogRoot);
            ctx.Check(raw + dropped == b.TotalSent, ".raw " + raw + " + drop " + dropped + " == 송신 " + b.TotalSent);
            ctx.Check(log.Contains("] AFTER-OVERFLOW\r\n") && log.Contains("] before-overflow\r\n"), "정지 전후 줄 기록 유지");
            string day = Path.GetDirectoryName(Path.Combine(cfg.LogRoot, LogFiles.All(cfg.LogRoot).First()));
            long idxDrop = BasesIn(day).SelectMany(x => ReadIdx(day, x)).Where(j => j.GetProperty("k").GetString() == "drop").Sum(j => j.GetProperty("n").GetInt64());
            ctx.Check(idxDrop == dropped, ".idx drop n 합 == drop");
        }
    }
}
