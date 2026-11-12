// 부록 C.13 재부팅 뒤 자동 프롬프트 깨우기 시험 — 새 시나리오를 등록하지 않고(--part 나눔이 바뀌지 않게) 기존 시나리오가 부른다:
//   PartA ← events-hub(2/3): ⑴ run 송신 직후 리셋(사람 입력 없음) → stranded·rebooted:true·started:false 바로, 허브 CR 1회로 프롬프트 → 잠금 해제 → 다음 run 성공
//                            ⑵ 앱 줄이 0.5초마다 흐르는 보드(RX 무음 없음)에서도 깨움 ⑺ booting 중 들어온 run 은 기다렸다 실행, 사람이 줄을 치는 중이면 101·CR 0바이트
//   PartB ← config-validate-t3(3/3): ⑶ U-Boot autoboot 카운트다운 중 CR 0바이트(autoboot 그대로) ⑷ 부팅 뒤 사람이 줄을 치다 멈추면 CR 0바이트
//                            ⑸ 응답 없는 보드에 부팅당 3번까지 ⑻ 처음 연결의 UNKNOWN 은 ^E^U CR(치다 만 줄을 실행하지 않음) ⑹ auto_nudge:false 면 옛 동작(CR 0·stranded 에 rebooted 없음·잠금 유지) · config 키 검증
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace UartHub.Testing
{
    public static class NudgeScenarios
    {
        static HubRig Rig(ScenarioContext ctx, string host, string cab, string com, int ctl, int basePort, bool auto, Action<FakeBoardOptions> opt = null)
        {
            var o = new FakeBoardOptions { Hostname = host, LoaderDelayMs = 700 };
            opt?.Invoke(o);
            return HubRig.Start(ctx, o, cab, com, ctl, c => { c.BasePort = basePort; c.AutoNudge = auto; c.AutoNudgeGapS = 1; c.QuietMs = 600; c.BootMergeMs = 1500; });
        }

        /// <summary>허브가 보낸 깨우기 CR(Probe 출처 0D 한 바이트) 수, ring 위치 from 뒤.</summary>
        static int Nudges(HubRig r, long from) => r.Tx.Count(x => x.Source == TxSource.Probe && x.RxPos >= from && x.Data.Length == 1 && x.Data[0] == 0x0d);

        // ================================================================== PartA(events-hub)
        public static void PartA(ScenarioContext ctx)
        {
            var r = Rig(ctx, "SIMNDG", "SIMCABNA", "COM190", 18646, 18656, true);
            var arb = r.Hub.Arbiter;
            ctx.Defer(() => arb.TestBeforeWrite = null);

            // ⑴ run 송신 직전(게이트 판정 뒤) 보드 리셋 → 명령 소멸
            long from = r.Ring.End;
            arb.TestBeforeWrite = (c, q, n) => { if (q.Kind == TxKind.Run && q.Cmd == "echo gone" && n == 0) { r.Board.Reset(); Thread.Sleep(30); } };
            var sw = Stopwatch.StartNew();
            var g = r.Call("run", r.P, "echo gone", "--timeout", "20");
            long ms = sw.ElapsedMilliseconds;
            arb.TestBeforeWrite = null;
            ctx.Check(J.S(g, "status") == "stranded" && J.N(g, "exit") == 100 && J.B(g, "rebooted") == true && J.B(g, "started") == false && J.B(g, "released") == false && ms < 8000,
                      "⑴ run 송신 직후 리셋 → --timeout(20초)을 기다리지 않고 stranded·rebooted:true·started:false (" + ms + "ms): " + J.Show(g));
            r.WaitState(PromptState.AtPrompt, 15000, "⑴ 사람 입력 없이 허브 CR 로 프롬프트 → 재부팅 소멸 잠금 해제");
            string hl = LogFiles.ReadShared(System.IO.Path.Combine(r.Cfg.EffectiveHubDir, "hub.log")) ?? "";
            ctx.Check(Nudges(r, from) == 1 && !r.Tx.Any(x => x.Source == TxSource.Human && x.RxPos >= from) && hl.Contains("(재부팅으로 명령 소멸")
                      && r.LogText().Contains(">>> [hub-nudge] CR 1/3"),
                      "⑴ 깨우기 CR 1회(사람 입력은 시작 Enter 1번뿐)·hub.log '직렬화 잠금 해제 … (재부팅으로 명령 소멸 …)'·.log '>>> [hub-nudge] CR 1/3'");
            r.IdleWait();
            var ok = r.Call("run", r.P, "echo after-reboot", "--timeout", "10");
            ctx.Check(J.S(ok, "status") == "ok" && J.Seq(J.A(ok, "out"), "after-reboot"), "⑴ 잠금이 풀려 다음 run 성공: " + J.Show(ok));

            // ⑺ booting 중 들어온 run: 101 을 주지 않고 기다렸다가(허브가 깨움) 실행
            long from7 = r.Ring.End;
            r.Board.Reset();
            r.WaitState(PromptState.Booting, 5000, "⑺ 리셋 → BOOTING");
            var d = r.Call("run", r.P, "echo during-boot", "--timeout", "20");
            ctx.Check(J.S(d, "status") == "ok" && J.Seq(J.A(d, "out"), "during-boot") && Nudges(r, from7) == 1,
                      "⑺ BOOTING 중 run → 기다렸다가 깨우기 CR 1회 뒤 실행: " + J.Show(d));

            // ⑵ 앱 줄이 0.5초마다 흐르는 보드(RX 무음 2초가 없음 — 옛 probe 조건은 영영 거짓): 커널 줄 무음으로 깨운다
            long from2 = r.Ring.End;
            var stop = new ManualResetEventSlim(false);
            var app = new Thread(() =>
            {
                int i = 0;
                while (!stop.Wait(500)) r.Board.EmitAppLog("00000" + (176000 + i++) + " : [vsink_00] 30.01 fps, 022.0 Mbps");
            }) { IsBackground = true };
            ctx.Defer(() => stop.Set());
            r.Board.Reset();
            r.WaitState(PromptState.Booting, 5000, "⑵ 리셋 → BOOTING");
            app.Start();
            var a2 = r.Call("run", r.P, "echo with-app-lines", "--timeout", "20");
            stop.Set();
            app.Join(2000);
            ctx.Check(J.S(a2, "status") == "ok" && J.Seq(J.A(a2, "out").Where(l => !l.Contains("[vsink_00]")).ToArray(), "with-app-lines") && Nudges(r, from2) >= 1 && Nudges(r, from2) <= 3,
                      "⑵ 앱 줄이 0.5초마다 흘러도 깨우기 CR(" + Nudges(r, from2) + "회 — 프롬프트 뒤 50ms 안에 앱 줄이 붙으면 한 번 더) → run 성공: " + J.Show(a2));

            // 사람이 줄을 치는 중이면 run 은 그 --timeout 뒤 101 partial_line, Claude·깨우기 0바이트(사람 우선)
            r.IdleWait();
            long fromP = r.Ring.End;
            long claude0 = r.ClaudeBytes;
            r.Human("cat /pro");
            r.WaitState(PromptState.HumanTyping, 3000, "사람 'cat /pro'");
            var p = r.Call("run", r.P, "echo x", "--timeout", "1");
            ctx.Check(J.N(p, "exit") == 101 && J.S(p, "blocked_reason") == "partial_line" && r.ClaudeBytes == claude0 && Nudges(r, fromP) == 0,
                      "사람이 줄 입력 중 run → 101 partial_line, Claude·깨우기 0바이트");
            r.Human("\u0015");
            r.Hub.Stop();
        }

        // ================================================================== PartB(config-validate-t3)
        public static void PartB(ScenarioContext ctx)
        {
            // config 키
            var c0 = Config.Default();
            ctx.Check(c0.AutoNudge && c0.AutoNudgeGapS == 10 && !Config.Parse("{\"auto_nudge\": false}").AutoNudge
                      && Config.Parse("{\"auto_nudge_gap_s\": 0}").Validate().Any(e => e.Contains("auto_nudge_gap_s")), "auto_nudge(기본 true)·auto_nudge_gap_s(기본 10, 양수)");
            bool bad = false;
            try { Config.Parse("{\"auto_nudge\": 1}"); } catch (ConfigException e) { bad = e.Errors.Any(x => x.Contains("auto_nudge")); }
            ctx.Check(bad, "auto_nudge 가 true/false 가 아니면 설정 오류");

            // ① 실보드 모양(7001 EVB-D4 11-06 17:35:54): 이 프로필이 모르는 다른 로더 표시("LDX>T")라 부팅 매치가 없고, 송신 뒤 커널 줄이 깨진 글자 뒤에 붙어 옴("abce[    0.314164]")
            (List<RunMarker> Marks, PromptState St) Feed(bool auto, params string[] parts)
            {
                var cfg = Config.Default();
                cfg.AutoNudge = auto;
                var t = new PromptTracker(cfg, "EVB-D4");
                var marks = new List<RunMarker>();
                t.RunMarkerSeen += m => marks.Add(m);
                t.ResetFresh(0, 0);
                long abs = 0, ms = 0;
                foreach (var part in parts)
                {
                    if (part.StartsWith("@run ", StringComparison.Ordinal)) { t.BeginRun(part.Substring(5), abs, ms, true); continue; }
                    var b = Encoding.UTF8.GetBytes(part);
                    ms += 100;
                    t.OnRx(abs, b, ms);
                    abs += b.Length;
                }
                t.Advance(ms + 1000);
                return (marks, t.State);
            }
            var f1 = Feed(true, "[ 6470.123456] [WD] tick\r\n00000176463 : [vsink_00] 30.01 fps\r\nLDX>T>boot ok\r\n", "@run u1", "2acor1await>CPU freq = 999> ", "abce[    0.314164] comm pwm driver probe\r\n");
            var f2 = Feed(true, "[ 6470.123456] [WD] tick\r\n", "@run u2", "__UH_B_u2\r\n", "[    0.314164] old line from dmesg\r\nLDR>T old capture\r\n");
            var f3 = Feed(true, "[ 6470.123456] [WD] tick\r\n", "@run u3", "printf __UH_B_u3; grep 'LDR>T' x\r\n");
            var f4 = Feed(false, "[ 6470.123456] [WD] tick\r\n", "@run u4", "abce[    0.314164] comm pwm driver probe\r\nLDR>T###\r\n");
            ctx.Check(f1.Marks.Count(m => m.Kind == 'R') == 1 && f1.St == PromptState.Booting && f2.Marks.All(m => m.Kind != 'R') && f3.Marks.All(m => m.Kind != 'R') && f4.Marks.All(m => m.Kind != 'R'),
                      "① 재부팅 판정(실보드 모양): 깨진 로더 뒤 'abce[    0.314164]'(run 밖 마지막 6470초보다 작음) → R·BOOTING / B 뒤 dmesg·캡처 속 옛 줄(앞 무음 없음) → 아님 / 명령 에코(__UH_) 속 LDR>T → 아님 / auto_nudge:false → 아님 ("
                      + f1.St + "," + f2.Marks.Count + "," + f3.Marks.Count + "," + f4.Marks.Count + ")");

            // ⑶ U-Boot autoboot 카운트다운(3초) 중에는 CR 0바이트 — autoboot 를 멈추지 않는다
            var r = Rig(ctx, "SIMNDU", "SIMCABNU", "COM191", 18647, 18657, true, o => o.UbootWindowMs = 3000);
            long from = r.Ring.End;
            r.Board.Reset();
            ctx.WaitUntil(() => r.RingText(from).Contains("Hit any key to stop autoboot"), 5000, "⑶ U-Boot 카운트다운 시작");
            var q = r.Call("run", r.P, "echo after-uboot", "--timeout", "20");
            string txt = r.RingText(from);
            int iHit = txt.IndexOf("Hit any key", StringComparison.Ordinal), iK = txt.IndexOf("[    0.0", StringComparison.Ordinal);
            r.Ring.TryGetArrival(Math.Max(from, r.Ring.Start) + iK, out long tK, out _);
            var early = r.Tx.Where(x => x.Source != TxSource.Human && x.RxPos >= from && x.MonoMs < tK).ToList();
            ctx.Check(J.S(q, "status") == "ok" && J.Seq(J.A(q, "out"), "after-uboot") && iHit >= 0 && iK > iHit && early.Count == 0 && !txt.Contains("evb: ") && Nudges(r, from) == 1,
                      "⑶ autoboot 카운트다운 동안 허브 송신 0바이트(U-Boot 프롬프트로 빠지지 않음), 커널 뒤 CR 1회 → run 성공 (" + early.Count + ")");

            // ⑷ 부팅 뒤 사람이 줄을 치다 멈춤(프롬프트 없이 'ab') → Claude 가 기다려도 CR 0바이트(친 줄이 실행되지 않게)
            long from4 = r.Ring.End;
            r.Board.Reset();
            r.WaitState(PromptState.Booting, 5000, "⑷ 리셋 → BOOTING");
            ctx.WaitUntil(() => r.RingText(from4).Contains("[START APP]"), 5000, "⑷ 부팅 끝(프롬프트 없음)");
            r.Human("ab");
            var h = r.Call("run", r.P, "echo y", "--timeout", "3");
            ctx.Check(J.N(h, "exit") == 101 && Nudges(r, from4) == 0, "⑷ 사람이 프롬프트 없이 'ab' 를 치고 멈춤 → run 101, 깨우기 CR 0바이트: " + J.Show(h));
            r.Human("\u0015\r");                                                                // 사람이 지우고 Enter → 프롬프트
            r.WaitState(PromptState.AtPrompt, 5000, "⑷ 사람 ^U·Enter → 프롬프트");
            r.Hub.Stop();

            // ⑸ CR 에 답하지 않는 보드: 부팅당 3번까지(1초 간격으로 줄인 설정)
            var s = Rig(ctx, "SIMNDS", "SIMCABNS", "COM192", 18648, 18658, true);
            long from5 = s.Ring.End;
            s.Board.Reset();
            s.WaitState(PromptState.Booting, 5000, "⑸ 리셋 → BOOTING");
            ctx.WaitUntil(() => s.RingText(from5).Contains("[START APP]"), 5000, "⑸ 부팅 끝");
            s.Board.Silent = true;
            var x5 = s.Call("run", s.P, "echo z", "--timeout", "6");
            Thread.Sleep(2500);
            int n5 = Nudges(s, from5);
            ctx.Check(J.N(x5, "exit") == 101 && J.S(x5, "blocked_reason") == "booting" && n5 == 3, "⑸ 응답 없는 보드: 깨우기 CR 부팅당 3번에서 멈춤(" + n5 + "번), run 101 booting");
            s.Board.Silent = false;
            s.Hub.Stop();

            // ⑻ 처음 연결하는 케이블(복원할 기록 없음 → UNKNOWN, user-ok 불필요)의 셸 줄에 다른 터미널에서 치다 만 명령이 남아 있으면:
            //    Claude run 의 자동 깨우기는 맨 CR 이 아니라 ^E ^U CR — 그 줄을 지우고 새 프롬프트에서 run 실행(남은 명령은 실행되지 않음).
            var b8 = new FakeBoard(new FakeBoardOptions { Hostname = "SIMNDL", LoaderDelayMs = 700 });
            ctx.Defer(b8.PowerOff);
            b8.InjectNoise(Encoding.ASCII.GetBytes("echo LEFTOVER_NUDGE"));        // 포트가 아직 안 열려 에코는 버려짐 — 허브는 모름
            var p8 = new FakePortProvider();
            p8.Plug(b8, "SIMCABNL", "COM194");
            var l8 = HubRig.Boot(ctx, HubRig.MakeConfig(ctx, 18533, c => { c.BasePort = 18534; c.AutoNudge = true; c.AutoNudgeGapS = 1; c.QuietMs = 600; }),
                                 b8, p8, "SIMCABNL", false);
            var sn8 = l8.T.Snapshot();
            ctx.Check(sn8.State == PromptState.Unknown && !sn8.BootSeen && !sn8.ProbeNeedsUserOk, "⑻ 처음 연결: UNKNOWN·이 세션 재부팅 없음·user-ok 불필요(자동 깨우기 대상)");
            long from8 = l8.Ring.End;
            var x8 = l8.Call("run", l8.P, "echo nudge-ok", "--timeout", "10");
            string t8 = l8.RingText(from8);
            var tx8 = l8.Tx.Where(x => x.Source == TxSource.Probe && x.RxPos >= from8).SelectMany(x => x.Data).ToArray();
            ctx.Check(J.S(x8, "status") == "ok" && x8.GetProperty("out").EnumerateArray().Any(o => o.GetString() == "nudge-ok")
                      && tx8.Length == 3 && tx8[0] == 0x05 && tx8[1] == 0x15 && tx8[2] == 0x0d,
                      "⑻ 자동 깨우기 = ^E ^U CR(" + BitConverter.ToString(tx8) + ") → 프롬프트 → run 실행: " + J.Show(x8));
            ctx.Check(b8.LastKilled == "echo LEFTOVER_NUDGE" && !System.Text.RegularExpressions.Regex.IsMatch(t8, @"^LEFTOVER_NUDGE\r?$", System.Text.RegularExpressions.RegexOptions.Multiline),
                      "⑻ 치다 만 'echo LEFTOVER_NUDGE' 는 ^U 로 지워지고(LastKilled='" + b8.LastKilled + "') 실행되지 않음(출력 줄 없음)");
            l8.Hub.Stop();

            // ⑹ auto_nudge:false = 옛 동작: 송신 직후 리셋 → --timeout 까지 기다려 stranded(rebooted 없음), CR 0바이트, 잠금은 사람 Enter 까지
            var o6 = Rig(ctx, "SIMNDO", "SIMCABNO", "COM193", 18649, 18659, false);
            var arb = o6.Hub.Arbiter;
            ctx.Defer(() => arb.TestBeforeWrite = null);
            long from6 = o6.Ring.End;
            arb.TestBeforeWrite = (c, q6, n) => { if (q6.Kind == TxKind.Run && q6.Cmd == "echo gone6" && n == 0) { o6.Board.Reset(); Thread.Sleep(30); } };
            var sw = Stopwatch.StartNew();
            var g6 = o6.Call("run", o6.P, "echo gone6", "--timeout", "4");
            long ms6 = sw.ElapsedMilliseconds;
            arb.TestBeforeWrite = null;
            Thread.Sleep(1500);
            var b6 = o6.Call("run", o6.P, "echo blocked", "--timeout", "1");
            ctx.Check(J.S(g6, "status") == "stranded" && !J.Has(g6, "rebooted") && ms6 >= 3500 && o6.Tx.Count(x => x.Source == TxSource.Probe && x.RxPos >= from6) == 0
                      && J.N(b6, "exit") == 101 && (J.S(b6, "blocked_reason") == "booting" || J.S(b6, "blocked_reason") == "serialized"),
                      "⑹ auto_nudge:false: --timeout 까지 기다린 stranded(rebooted 없음, " + ms6 + "ms)·허브 CR 0바이트·다음 run 101(" + J.S(b6, "blocked_reason") + ")");
            o6.Human("\r");
            o6.WaitState(PromptState.AtPrompt, 5000, "⑹ 사람 Enter → 프롬프트(잠금 해제)");
            o6.IdleWait();
            ctx.Check(J.S(o6.Call("run", o6.P, "echo freed", "--timeout", "5"), "status") == "ok", "⑹ 사람 Enter 뒤 run 성공(옛 해제 경로)");
            o6.Hub.Stop();
        }
    }
}
