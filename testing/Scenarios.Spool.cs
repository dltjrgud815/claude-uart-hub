// US-006 시나리오: SpoolWatcher (계획 §4 S6 표 13, 34, spool-tokens, 시뮬 6·31 의 spool 변형, .status·released/ignored 미접촉).
// 기대 바이트는 구 브리지 uart_bridge.ps1:73-96 Get-TxOps 를 읽고 손으로 적는다(구현 TxLine 으로 만들지 않는다).
// 포트: 케이블 17240~17290(base_port), 제어 17143~17148 — 기존 시나리오와 US-005 시나리오(17140~17142, 17210~17230)와 겹치지 않는다.
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
    internal static class SpoolScenarios
    {
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        /// <summary>SpoolWatcher.LineDone 기록(줄, 결과 JSON).</summary>
        sealed class Done
        {
            readonly List<(string Line, JsonElement Res)> items = new List<(string, JsonElement)>();
            public Done(HubRig r) { r.Hub.Spool.LineDone += (c, line, res) => { var e = JsonDocument.Parse(res.ToString()).RootElement.Clone(); lock (items) items.Add((line, e)); }; }
            public int Count { get { lock (items) return items.Count; } }
            public List<(string Line, JsonElement Res)> All { get { lock (items) return items.ToList(); } }
            public (string Line, JsonElement Res) Last(string line) { lock (items) return items.Last(x => x.Line == line); }
            public bool Has(string line) { lock (items) return items.Any(x => x.Line == line); }
        }

        /// <summary>writer 흉내: 한 번 열어 text 를 쓰고 닫는다. 다른 writer·허브가 막고 있으면(공유 위반) 잠깐 뒤 다시.</summary>
        static int Append(string path, string text, FileShare share = FileShare.Read)
        {
            var b = Utf8.GetBytes(text);
            for (int retry = 0; ; retry++)
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, share);
                    fs.Write(b, 0, b.Length);
                    return retry;
                }
                catch (IOException) { Thread.Sleep(1); }
            }
        }

        static long Received(HubRig r) => r.Board.TotalReceived;
        static List<TxRecord> SpoolTx(HubRig r, int from) => r.Tx.Skip(from).Where(x => x.Source == TxSource.Spool).ToList();
        static int Count(string s, string sub)
        {
            int n = 0;
            for (int i = s.IndexOf(sub, StringComparison.Ordinal); i >= 0; i = s.IndexOf(sub, i + sub.Length, StringComparison.Ordinal)) n++;
            return n;
        }

        static List<string> Leftovers(string live)
            => Directory.GetFiles(live).Select(Path.GetFileName).Where(f => f.EndsWith(".tx", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".taking", StringComparison.OrdinalIgnoreCase)).ToList();

        /// <summary>1000줄 검증 공통: LineDone 1000(전부 ok), .log 의 >>> [tx] 줄이 1000개·중복 없음·writer 별 번호 오름차순, 보드가 모두 실행, dropped 0.</summary>
        static void VerifyThousand(ScenarioContext ctx, HubRig r, Done done, int done0, long ring0, int tx0, string tag, int digits, int writers, int perWriter, string what)
        {
            int total = writers * perWriter;
            var sw = Stopwatch.StartNew();
            ctx.WaitUntil(() => done.Count - done0 >= total, 200000, what + ": " + total + "줄 처리 완료");
            ctx.Log("배출 " + sw.ElapsedMilliseconds + "ms (첫 줄 이후 전체)");
            var res = done.All.Skip(done0).ToList();
            var bad = res.Where(x => J.S(x.Res, "status") != "ok").ToList();
            ctx.Check(bad.Count == 0, what + ": " + total + "줄 모두 송신 ok (실패 " + bad.Count + (bad.Count > 0 ? ": " + bad[0].Line + " " + J.Show(bad[0].Res) : "") + ")");
            string log = r.LogText();
            string core = tag + @"(\d)_(\d{" + digits.ToString(CultureInfo.InvariantCulture) + "})";
            var lineRe = new Regex("^echo " + core + "$");
            var sent = Regex.Matches(log, @">>> \[tx\] (echo " + core + @")\r\n").Select(m => m.Groups[1].Value).ToList();
            ctx.Check(sent.Count == total && sent.Distinct().Count() == total, what + ": .log '>>> [tx]' " + sent.Count + "줄, 서로 다름 " + sent.Distinct().Count() + " (기대 " + total + ")");
            for (int w = 0; w < writers; w++)
            {
                var seq = sent.Select(x => lineRe.Match(x)).Where(m => m.Groups[1].Value == w.ToString(CultureInfo.InvariantCulture)).Select(m => int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)).ToList();
                ctx.Check(seq.Count == perWriter && seq.SequenceEqual(Enumerable.Range(1, perWriter)), what + ": writer " + w + " 순서 보존(1~" + perWriter + ", " + seq.Count + "줄)");
            }
            ctx.Check(!log.Contains("!!! [tx "), what + ": dropped 0");
            var spool = SpoolTx(r, tx0);
            long expect = sent.Sum(x => (long)Utf8.GetByteCount(x) + 1);
            ctx.Check(spool.Sum(x => (long)x.Data.Length) == expect, what + ": 보드로 나간 spool 바이트 " + spool.Sum(x => (long)x.Data.Length) + " == 줄+CR 합계 " + expect);
            r.WaitState(PromptState.AtPrompt, 3000, what + " 끝");
            var outs = Regex.Matches(r.RingText(ring0), @"\r\n" + core + @"\r\n").Count;
            ctx.Check(outs == total, what + ": 보드가 echo " + total + "개를 실행(출력 " + outs + ")");
        }

        // ================================================================== 13
        [Scenario("13", ".tx 동시 append(NTFS) 1000줄: writer 4개(Delete 공유 없음·있음, 핸들 붙잡기, 포트·COM 두 이름)가 묶음으로 쓰는 동안 허브가 소비 → 유실 0·writer 별 순서 보존·전부 보드 도착. 늦은 writer(rename 뒤 같은 핸들로 쓰기) 보존. TTL 초과 → !!! [tx <id>] dropped(vi 중·오래된 .tx), 보드 0바이트, 파일은 소비됨", Order = 700, TimeoutS = 300)]
        public static void S13(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIM13" }, "SIMCAB13", "COM113", 17143,
                                 c => { c.BasePort = 17240; c.TxTtlS = 600; c.TOpenMs = 15; c.SpoolPollMs = 50; });
            var sp = r.Hub.Spool;
            string live = sp.LiveDir;
            ctx.Check(live == Path.Combine(r.Cfg.LogRoot, "live") && Directory.Exists(live), "live 폴더 = <log_root>\\live");
            string pPort = Path.Combine(live, "17240.tx"), pCom = Path.Combine(live, "COM113.tx");
            var done = new Done(r);

            // ---- 1000줄 동시 append: writer 4개가 25줄씩 동시에 쓰고 함께 쉬기를 10번(쉬는 동안 허브가 rename·소비 → 다음 묶음과 겹침)
            int done0 = done.Count, tx0 = r.Tx.Count;
            long ring0 = r.Ring.End, files0 = sp.FilesTaken;
            int[] retries = new int[4];
            var ths = new Thread[4];
            const int Bursts = 10, PerBurst = 25;
            using var barrier = new Barrier(4);
            int pauseMs = r.Cfg.SpoolQuietMs + 250;
            for (int w = 0; w < 4; w++)
            {
                int k = w;
                ths[w] = new Thread(() =>
                {
                    string path = k % 2 == 0 ? pPort : pCom;
                    string L(int i) => "echo w" + k + "_" + i.ToString("D4", CultureInfo.InvariantCulture) + "\n";
                    for (int bn = 0; bn < Bursts; bn++)
                    {
                        int first = bn * PerBurst + 1, last = first + PerBurst - 1;
                        if (k == 2)
                        {
                            // 핸들을 붙잡고 5줄씩(그 사이 허브가 rename 하면 .taking 에 이어 쓴다 = 늦은 writer)
                            for (int i = first; i <= last;)
                            {
                                try
                                {
                                    using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                                    for (int j = 0; j < 5 && i <= last; j++, i++)
                                    {
                                        var b = Utf8.GetBytes(L(i));
                                        fs.Write(b, 0, b.Length);
                                        fs.Flush();
                                        Thread.Sleep(2);
                                    }
                                }
                                catch (IOException) { retries[k]++; Thread.Sleep(1); }
                            }
                        }
                        else
                        {
                            var share = k == 1 ? FileShare.ReadWrite | FileShare.Delete : FileShare.Read;
                            for (int i = first; i <= last; i++) retries[k] += Append(path, L(i), share);
                        }
                        barrier.SignalAndWait();
                        Thread.Sleep(pauseMs);
                    }
                }) { IsBackground = true, Name = "Writer13-" + w };
            }
            var sw = Stopwatch.StartNew();
            foreach (var t in ths) t.Start();
            foreach (var t in ths) t.Join();
            long filesDuring = sp.FilesTaken - files0;
            ctx.Log("append 1000줄 " + sw.ElapsedMilliseconds + "ms(묶음 " + Bursts + "개, 쉬기 " + pauseMs + "ms), writer 재시도 " + string.Join("/", retries) + ", append 중 소비한 파일 " + filesDuring);
            ctx.Check(filesDuring >= Bursts, "append 가 이어지는 동안 소비(.taking " + filesDuring + "개 ≥ 묶음 " + Bursts + ")");
            VerifyThousand(ctx, r, done, done0, ring0, tx0, "w", 4, 4, 250, "NTFS 1000줄");
            ctx.Log("spool 카운터: files=" + sp.FilesTaken + " lines=" + sp.LinesRead + " rename_busy=" + sp.RenameBusy + " late_writer_waits=" + sp.LateWriterWaits);
            ctx.Check(sp.LinesRead == 1000, "spool 이 읽은 줄 = 1000");
            ctx.WaitUntil(() => Leftovers(live).Count == 0, 2000, "남은 .tx·.taking 없음");

            // ---- 늦은 writer: 연 핸들로 1줄 → 허브가 rename → 같은 핸들로 1줄 더 → 닫기 → 두 줄 모두 순서대로
            r.IdleWait();
            using (var hold = new FileStream(pPort, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                var a = Utf8.GetBytes("echo late13_a\n");
                hold.Write(a, 0, a.Length);
                hold.Flush();
                ctx.WaitUntil(() => !File.Exists(pPort) && Directory.GetFiles(live, "17240.tx.*.taking").Length == 1, 3000, "열려 있는 .tx 를 허브가 .taking 으로 rename");
                long w0 = sp.LateWriterWaits;
                Thread.Sleep(300);
                var b = Utf8.GetBytes("echo late13_b\n");
                hold.Write(b, 0, b.Length);
                hold.Flush();
                ctx.Check(sp.LateWriterWaits > w0 && !done.Has("echo late13_a"), "writer 가 닫기 전에는 읽지 않음(FileShare.None 재시도 " + (sp.LateWriterWaits - w0) + "회)");
            }
            ctx.WaitUntil(() => done.Has("echo late13_b"), 5000, "닫은 뒤 두 줄 송신");
            var lates = done.All.Where(x => x.Line.StartsWith("echo late13_", StringComparison.Ordinal)).Select(x => x.Line + ":" + J.S(x.Res, "status")).ToList();
            ctx.Check(lates.SequenceEqual(new[] { "echo late13_a:ok", "echo late13_b:ok" }), "rename 뒤에 쓴 줄까지 보존, 순서대로 ok (" + string.Join(",", lates) + ")");
            r.WaitState(PromptState.AtPrompt, 3000, "late13 뒤");

            // ---- TTL 초과: vi(FULLSCREEN) 중 일반 줄·토큰 줄 → 보내지 않고 dropped
            r.Human("vi /tmp/t13.txt\r");
            r.WaitState(PromptState.Fullscreen, 3000, "vi");
            ctx.WaitUntil(() => r.Board.Phase == "vi", 2000, "보드 vi 모드");
            Thread.Sleep(200);
            r.Cfg.TxTtlS = 1.0;
            long rec0 = Received(r), h0 = r.HumanBytes;
            int vi0 = r.Board.ViInsertedBytes;
            int d0 = Count(r.LogText(), "dropped (fullscreen)");
            var ttlSw = Stopwatch.StartNew();
            Append(pPort, "echo ttl13\n{ENTER}\n");
            ctx.WaitUntil(() => done.Has("{ENTER}"), 6000, "TTL 1초 뒤 두 줄 처리 끝");
            ctx.Check(ttlSw.ElapsedMilliseconds >= 900, "TTL(1초) 전에는 dropped 하지 않음 (" + ttlSw.ElapsedMilliseconds + "ms)");
            var t1 = done.Last("echo ttl13").Res; var t2 = done.Last("{ENTER}").Res;
            ctx.Check(J.N(t1, "exit") == 101 && J.N(t2, "exit") == 101 && J.S(t1, "blocked_reason") == "fullscreen" && J.S(t2, "blocked_reason") == "fullscreen",
                      "일반 줄·토큰 줄 모두 101 fullscreen (" + J.Show(t1) + " / " + J.Show(t2) + ")");
            string log = r.LogText();
            ctx.Check(Count(log, "dropped (fullscreen)") - d0 == 2 && Regex.IsMatch(log, @"!!! \[tx \S+\] dropped \(fullscreen\)"), ".log 에 '!!! [tx <id>] dropped (fullscreen)' 2줄");
            ctx.Check(Received(r) - rec0 == r.HumanBytes - h0 && r.Board.ViInsertedBytes == vi0, "보드 수신 0바이트, vi 버퍼 0바이트");
            r.Human("\x1b"); Thread.Sleep(200); r.Human(":q!\r");
            r.WaitState(PromptState.AtPrompt, 3000, "vi 종료");
            r.IdleWait();

            // ---- 오래된 .tx(마지막 쓰기 10분 전, 허브 정지·분리 중 쌓인 줄 흉내): 게이트가 열려 있어도 보내지 않음
            r.Cfg.TxTtlS = 60;
            string tmp = Path.Combine(live, "old13.tmp");
            File.WriteAllText(tmp, "echo old13\n");
            File.SetLastWriteTimeUtc(tmp, DateTime.UtcNow.AddMinutes(-10));
            rec0 = Received(r);
            File.Move(tmp, pCom);
            ctx.WaitUntil(() => done.Has("echo old13"), 3000, "오래된 .tx 소비");
            var o = done.Last("echo old13").Res;
            ctx.Check(J.N(o, "exit") == 101 && J.S(o, "blocked_reason") == "ttl", "마지막 쓰기 10분 전 줄 → 101 ttl (" + J.Show(o) + ")");
            Thread.Sleep(200);
            ctx.Check(Received(r) == rec0 && Regex.IsMatch(r.LogText(), @"!!! \[tx t\d+\] dropped \(ttl\)"), "보드 0바이트, '!!! [tx t<n>] dropped (ttl)'");
            ctx.WaitUntil(() => Leftovers(live).Count == 0, 2000, "남은 .tx·.taking 없음");
        }

        // ================================================================== 34
        /// <summary>WSL 배포판에서 bash 스크립트를 실행(stdin 으로 전달, 경로는 WSLENV /p 로 /mnt/… 변환 — 따옴표·역슬래시 재해석 없음).</summary>
        static (int Code, string Out, string Err, long Ms) WslBash(ScenarioContext ctx, string script, IDictionary<string, string> winPaths)
        {
            var psi = new ProcessStartInfo("wsl.exe")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = Utf8, StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8,
            };
            string distro = Environment.GetEnvironmentVariable("WSL_DISTRO_NAME");
            if (!string.IsNullOrEmpty(distro)) { psi.ArgumentList.Add("-d"); psi.ArgumentList.Add(distro); }
            psi.ArgumentList.Add("-e"); psi.ArgumentList.Add("bash"); psi.ArgumentList.Add("-s");
            string wslenv = Environment.GetEnvironmentVariable("WSLENV");
            foreach (var kv in winPaths)
            {
                psi.Environment[kv.Key] = kv.Value;
                wslenv = (string.IsNullOrEmpty(wslenv) ? "" : wslenv + ":") + kv.Key + "/p";
            }
            psi.Environment["WSLENV"] = wslenv;
            var sw = Stopwatch.StartNew();
            Process p = null;
            try { p = Process.Start(psi); }
            catch (Exception e) { ctx.Fail("wsl.exe 실행 실패: " + e.GetType().Name + ": " + e.Message); }
            using (p)
            {
                var so = p.StandardOutput.ReadToEndAsync();
                var se = p.StandardError.ReadToEndAsync();
                p.StandardInput.Write(script);
                p.StandardInput.Close();
                if (!p.WaitForExit(120000)) { try { p.Kill(true); } catch (Exception) { } ctx.Fail("wsl.exe bash 시간 초과"); }
                p.WaitForExit();
                var r = (p.ExitCode, so.Result.Trim(), se.Result.Trim(), sw.ElapsedMilliseconds);
                ctx.Log("wsl.exe " + (distro ?? "(기본 배포판)") + " rc=" + r.ExitCode + " " + r.ElapsedMilliseconds + "ms out=" + r.Item2.Replace("\n", " | ")
                        + (r.Item3.Length > 0 ? " err=" + (r.Item3.Length > 300 ? r.Item3.Substring(0, 300) + "…" : r.Item3).Replace("\n", " | ") : ""));
                return r;
            }
        }

        const string WslWriter =
            "set -u\n" +
            "d=\"$UH34_LIVE\"\n" +
            "case \"$d\" in /mnt/*) ;; *) echo \"BADPATH $d\"; exit 3;; esac\n" +
            "w() { for i in $(seq -w 1 \"$3\"); do echo \"echo v$2_$i\" >> \"$1\" || echo \"ERR $2 $i\"; done; }\n";

        [Scenario("34", "13번 변형(WSL DrvFs): bash 2개 프로세스가 /mnt/…/live 의 <포트>.tx·<COMx>.tx 에 '>>' 로 동시 append 1000줄 → writer 오류 0·유실 0·writer 별 순서·전부 보드 도착. 같은 파일 동시 writer: 허브는 writer open 을 깨지 않고(ENOENT 0) 읽은 줄을 빠짐없이 순서대로 보냄(DrvFs 자체 유실은 대조군과 함께 기록)", Order = 710, TimeoutS = 300)]
        public static void S34(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIM34" }, "SIMCAB34", "COM134", 17144,
                                 c => { c.BasePort = 17250; c.TxTtlS = 600; c.TOpenMs = 15; c.SpoolPollMs = 50; });
            var sp = r.Hub.Spool;
            string live = sp.LiveDir;
            var done = new Done(r);
            var env = new Dictionary<string, string> { ["UH34_LIVE"] = live };

            // ---- 1. 동시 append 1000줄: 프로세스 2개가 같은 케이블의 두 이름(<포트>.tx·<COMx>.tx)에 동시에
            int done0 = done.Count, tx0 = r.Tx.Count;
            long ring0 = r.Ring.End, read0 = sp.LinesRead;
            var w1 = WslBash(ctx, WslWriter +
                "stat -f -c 'FS %T' \"$d\"\n" +
                "w \"$d/17250.tx\" 0 500 & w \"$d/COM134.tx\" 1 500 & wait\n" +
                "echo \"DONE $d\"\n", env);
            ctx.Check(w1.Code == 0 && w1.Out.Contains("DONE /mnt/"), "WSL bash 2개 프로세스가 /mnt/… live 에 >> 동시 append 완료 (rc " + w1.Code + ")");
            ctx.Check(!w1.Out.Contains("ERR ") && w1.Err.Length == 0, "writer 쪽 오류 0 (ENOENT 등 append 실패 없음)");
            VerifyThousand(ctx, r, done, done0, ring0, tx0, "v", 3, 2, 500, "WSL DrvFs 동시 1000줄");
            ctx.Check(sp.LinesRead - read0 == 1000, "spool 이 읽은 줄 = 1000");
            ctx.WaitUntil(() => Leftovers(live).Count == 0, 2000, "남은 .tx·.taking 없음");
            ctx.Log("spool 카운터: files=" + sp.FilesTaken + " lines=" + sp.LinesRead + " rename_busy=" + sp.RenameBusy + " late_writer_waits=" + sp.LateWriterWaits);

            // ---- 2. 같은 파일에 동시 writer 2개(250줄씩): DrvFs 의 `>>` 는 프로세스 간 append 가 원자적이지 않아 허브 없이도 줄이 겹쳐 쓰인다(대조군).
            //         허브가 지킬 것: writer open 을 깨지 않음(ENOENT 0), 파일에 남은 줄 = 허브가 읽은 줄 = 보낸 줄(유실·중복 0), writer 별 순서.
            r.WaitState(PromptState.AtPrompt, 3000, "같은 파일 시험 전");
            string ctl = Path.Combine(ctx.Dir, "ctl34");
            Directory.CreateDirectory(ctl);
            int n0 = done.Count;
            long readS = sp.LinesRead;
            var w2 = WslBash(ctx, WslWriter +
                "c=\"$UH34_CTL\"\n" +
                "w \"$d/17250.tx\" 2 250 & w \"$d/17250.tx\" 3 250 & wait\n" +
                "echo DONE2\n" +
                "w \"$c/ctl.tx\" 2 250 & w \"$c/ctl.tx\" 3 250 & wait\n" +
                "echo \"CTRL $(grep -c '^echo v' \"$c/ctl.tx\")\"\n",
                new Dictionary<string, string> { ["UH34_LIVE"] = live, ["UH34_CTL"] = ctl });
            ctx.Check(w2.Code == 0 && w2.Out.Contains("DONE2"), "WSL bash 같은 파일 동시 writer 완료");
            ctx.Check(!w2.Out.Contains("ERR ") && w2.Err.Length == 0, "같은 파일 동시 writer 중에도 writer 쪽 오류 0 (허브 rename 이 open 을 깨지 않음)");
            ctx.WaitUntil(() => Leftovers(live).Count == 0 && sp.LinesRead > readS && done.Count - n0 == sp.LinesRead - readS, 60000, "허브가 같은 파일을 소비하고 읽은 줄을 모두 처리");
            Thread.Sleep(500);
            long got = sp.LinesRead - readS;
            var res = done.All.Skip(n0).ToList();
            ctx.Check(res.Count == got && res.All(x => J.S(x.Res, "status") == "ok") && res.Select(x => x.Line).Distinct().Count() == res.Count,
                      "읽은 " + got + "줄 = 보낸 줄(모두 ok, 중복 0)");
            foreach (var k in new[] { "2", "3" })
            {
                var seq = res.Select(x => Regex.Match(x.Line, @"^echo v(\d)_(\d{3})$")).Where(m => m.Success && m.Groups[1].Value == k).Select(m => int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)).ToList();
                ctx.Check(seq.Zip(seq.Skip(1), (a, b) => a < b).All(x => x), "writer " + k + " 순서 보존(받은 " + seq.Count + "줄 증가 순)");
            }
            string ctlLine = w2.Out.Split('\n').FirstOrDefault(x => x.StartsWith("CTRL ", StringComparison.Ordinal)) ?? "CTRL ?";
            ctx.Log("참고(DrvFs 한계): 같은 파일 동시 writer 2개 × 250줄 → 허브가 받은 줄 " + got + "/500, 허브 없는 대조군 " + ctlLine.Substring(5) + "/500 (ext4 에서는 500/500)");
            r.WaitState(PromptState.AtPrompt, 3000, "끝");
        }

        // ================================================================== spool-tokens
        [Scenario("spool-tokens", ".tx 토큰 문법 = 구 브리지 Get-TxOps(uart_bridge.ps1:73-96) 바이트: {ESC}{ENTER}{TAB}{SPACE}{CTRL-C}{HEX:0d0a}{BREAK}, 줄 앞 {NONL}(대소문자 무시), 소문자·미완성 토큰은 글자 그대로, HEX 홀수 니블 버림, UTF-8, ESC 뒤 150ms, >>> [tx] 원문 기록", Order = 720, TimeoutS = 120)]
        public static void SpoolTokens(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIMTK" }, "SIMCABTK", "COM135", 17145, c => { c.BasePort = 17260; c.SpoolPollMs = 50; });
            string tx = Path.Combine(r.Hub.Spool.LiveDir, "COM135.tx");
            var done = new Done(r);
            byte[] H(string hex) => hex.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => byte.Parse(x, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
            byte[] A(string s) => Utf8.GetBytes(s);
            // (줄, 기대 바이트(구 브리지: ops 를 차례로 + 끝에 EOL CR, {NONL} 이면 CR 없음), break 수, 토큰 줄 여부, 뒤에 사람 Enter 필요)
            var cases = new List<(string Line, byte[] Bytes, int Breaks, bool Nonl)>
            {
                ("{ESC}{ENTER}{TAB}{SPACE}{CTRL-C}{HEX:0d0a}{BREAK}", H("1B 0D 09 20 03 0D 0A 0D"), 1, false),
                ("echo {esc}{Esc}{ENTER", A("echo {esc}{Esc}{ENTER\r"), 0, false),
                ("{HEX:4142434}", H("41 42 43 0D"), 0, false),
                ("echo 한글{TAB}x", H("65 63 68 6F 20 ED 95 9C EA B8 80 09 78 0D"), 0, false),
                ("{CTRL-Z}{CTRL-A}{HEX:ff}", H("1A 01 FF 0D"), 0, false),
                ("{NONL}echo nonl6", A("echo nonl6"), 0, true),
                ("{nonl}{ENTER}", H("0D"), 0, false),
                ("{NONL}{ESC}", H("1B"), 0, true),
                ("{NONL}", new byte[0], 0, false),
            };
            foreach (var (line, bytes, breaks, nonl) in cases)
            {
                int tx0 = r.Tx.Count, n0 = done.Count;
                long br0 = r.Board.BreakCount;
                Append(tx, line + "\n");
                ctx.WaitUntil(() => done.Count > n0, 5000, "'" + line + "' 처리");
                var res = done.Last(line).Res;
                ctx.Check(J.S(res, "status") == "ok", "'" + line + "' 송신 ok (" + J.Show(res) + ")");
                Thread.Sleep(100);
                var recs = SpoolTx(r, tx0);
                var got = recs.SelectMany(x => x.Data).ToArray();
                ctx.Check(got.SequenceEqual(bytes) && r.Board.BreakCount - br0 == breaks,
                          "'" + line + "' → " + TnClient.Hex(got) + (breaks > 0 ? " + break " + (r.Board.BreakCount - br0) : "") + " (기대 " + TnClient.Hex(bytes) + (breaks > 0 ? " + break " + breaks : "") + ")");
                if (line.StartsWith("{ESC}{ENTER}", StringComparison.Ordinal))
                {
                    ctx.Check(recs.Count == 3 && recs[0].Data.SequenceEqual(H("1B")) && recs[1].Data.SequenceEqual(H("0D 09 20 03 0D 0A")) && recs[2].Data.SequenceEqual(H("0D")),
                              "ESC 단독 → (150ms) → break 앞 묶음 → break → EOL 순서 (" + string.Join(" | ", recs.Select(x => TnClient.Hex(x.Data))) + ")");
                    ctx.Check(recs[1].MonoMs - recs[0].MonoMs >= r.Cfg.EscGapMs - 2, "ESC 뒤 " + (recs[1].MonoMs - recs[0].MonoMs) + "ms 간격(≥" + r.Cfg.EscGapMs + ")");
                }
                if (nonl)
                {
                    long ringN = r.Ring.End;
                    r.Human("\r");
                    if (line == "{NONL}echo nonl6") ctx.WaitUntil(() => r.RingText(ringN).Contains("\r\nnonl6\r\n"), 3000, "{NONL} 로 친 'echo nonl6' 이 사람 Enter 로 실행됨");
                    r.WaitState(PromptState.AtPrompt, 3000, "사람 Enter 뒤");
                    r.IdleWait();
                }
                else r.WaitState(PromptState.AtPrompt, 3000, "'" + line + "' 뒤");
            }
            string log = r.LogText();
            foreach (var c in cases) ctx.Check(log.Contains(">>> [tx] " + c.Line + "\r\n"), ".log 에 '>>> [tx] " + c.Line + "'");
            ctx.Check(r.RingText().Contains("\r\n{esc}{Esc}{ENTER\r\n"), "소문자·미완성 토큰은 글자 그대로 셸에 감(echo 출력 '{esc}{Esc}{ENTER')");
        }

        // ================================================================== 6-spool
        [Scenario("6-spool", "시뮬 6 의 spool 변형: BOOTING 중 .tx 의 {NONL}{ESC} 송신 → U-Boot(AT_UBOOT), AT_UBOOT 에서 .tx {ENTER}·{NONL}…{ENTER} 송신, 일반 줄은 TTL 뒤 dropped(0바이트), 커널 뒤 BOOTING 에서 .tx {ENTER} → 프롬프트", Order = 730, TimeoutS = 120)]
        public static void S6Spool(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIM6S", UbootWindowMs = 3000 }, "SIMCAB6S", "COM136", 17146, c => { c.BasePort = 17270; c.SpoolPollMs = 50; });
            string tx = Path.Combine(r.Hub.Spool.LiveDir, r.P + ".tx");
            var done = new Done(r);
            var reb = r.Call("reboot", r.P);
            ctx.Check(J.S(reb, "status") == "ok", "reboot (" + J.Show(reb) + ")");
            var sw = Stopwatch.StartNew();
            int k = done.Count;
            while (sw.ElapsedMilliseconds < 8000 && r.T.State != PromptState.AtUboot)
            {
                if (r.T.State == PromptState.Booting)
                {
                    Append(tx, "{NONL}{ESC}\n");
                    k++;
                    ctx.WaitUntil(() => done.Count >= k, 3000, "BOOTING 중 .tx {NONL}{ESC} 처리");
                    Thread.Sleep(250);
                }
                else Thread.Sleep(20);
            }
            var escs = done.All.Where(x => x.Line == "{NONL}{ESC}").Select(x => J.S(x.Res, "status") + "/" + J.S(x.Res, "state")).ToList();
            ctx.Check(escs.Contains("ok/BOOTING"), "BOOTING 중 .tx {NONL}{ESC} 송신 ok (" + string.Join(",", escs) + ")");
            r.WaitState(PromptState.AtUboot, 3000, ".tx ESC 로 autoboot 중단 → 'evb: '");
            ctx.Check(r.LogText().Contains(">>> [tx] {NONL}{ESC}"), ".log 에 '>>> [tx] {NONL}{ESC}'");

            void Line(string line, string state)
            {
                int n0 = done.Count;
                Append(tx, line + "\n");
                ctx.WaitUntil(() => done.Count > n0, 5000, "'" + line + "' 처리");
                var res = done.Last(line).Res;
                ctx.Check(J.S(res, "status") == "ok" && J.S(res, "state") == state, state + " 에서 .tx '" + line + "' 송신 ok (" + J.Show(res) + ")");
            }
            Line("{ENTER}", "AT_UBOOT");
            r.WaitState(PromptState.AtUboot, 3000, "{ENTER} 뒤 'evb: ' 재확정");

            // 일반 줄은 AT_PROMPT 전용 → AT_UBOOT 에서는 TTL 뒤 dropped, 보드 0바이트
            r.Cfg.TxTtlS = 1.5;
            long rec0 = r.Board.TotalReceived;
            int n1 = done.Count;
            Append(tx, "printenv\n");
            ctx.WaitUntil(() => done.Count > n1, 5000, "AT_UBOOT 에서 일반 줄 'printenv' 처리");
            var pr = done.Last("printenv").Res;
            ctx.Check(J.N(pr, "exit") == 101, "AT_UBOOT 에서 .tx 일반 줄 → 101 (" + J.Show(pr) + ")");
            ctx.Check(r.Board.TotalReceived == rec0 && Regex.IsMatch(r.LogText(), @"!!! \[tx \S+\] dropped \("), "보드 0바이트, '!!! [tx <id>] dropped' 기록");
            r.Cfg.TxTtlS = 60;

            long ringP = r.Ring.End;
            Line("{NONL}printenv{ENTER}", "AT_UBOOT");
            ctx.WaitUntil(() => r.RingText(ringP).Contains("bootcmd=") && r.T.State == PromptState.AtUboot, 3000, "{NONL} 줄 printenv 출력 + 'evb: ' 재확정");
            Line("{NONL}boot{ENTER}", "AT_UBOOT");
            ctx.WaitUntil(() => r.Board.Phase == "postboot" && r.T.Snapshot().KernelPhase, 5000, "커널 뒤 BOOTING(프롬프트 없음)");
            Line("{ENTER}", "BOOTING");
            r.WaitState(PromptState.AtPrompt, 3000, ".tx {ENTER} → Linux 프롬프트");
        }

        // ================================================================== 31-spool
        [Scenario("31-spool", "시뮬 31 의 spool 변형: vi(FULLSCREEN)·cat >(BUSY) 중 .tx 토큰 줄({ENTER}·{ESC}·{NONL}x)·일반 줄 → 보드 0바이트(vi 버퍼·cat 파일 0), TTL 뒤 줄마다 !!! [tx <id>] dropped (fullscreen|busy_since), 파일은 소비됨", Order = 740, TimeoutS = 90)]
        public static void S31Spool(ScenarioContext ctx)
        {
            var r = HubRig.Start(ctx, new FakeBoardOptions { Hostname = "SIM31S" }, "SIMCAB31S", "COM137", 17147, c => { c.BasePort = 17280; c.SpoolPollMs = 50; c.TxTtlS = 1.0; });
            string live = r.Hub.Spool.LiveDir;
            var done = new Done(r);
            var lines = new[] { "{ENTER}", "{ESC}", "{NONL}x", "hello31" };
            void AllDropped(string reason, string where, string file)
            {
                long rec = Received(r), h = r.HumanBytes;
                int n0 = done.Count, d0 = Count(r.LogText(), "dropped (" + reason + ")");
                Append(Path.Combine(live, file), string.Join("\n", lines) + "\n");
                ctx.WaitUntil(() => done.Count >= n0 + lines.Length, 8000, where + ": .tx " + lines.Length + "줄 처리");
                var res = done.All.Skip(n0).ToList();
                ctx.Check(res.Select(x => x.Line).SequenceEqual(lines) && res.All(x => J.N(x.Res, "exit") == 101 && J.S(x.Res, "blocked_reason") == reason),
                          where + ": 줄마다 101 " + reason + " (" + string.Join(", ", res.Select(x => x.Line + "=" + J.N(x.Res, "exit") + "/" + J.S(x.Res, "blocked_reason"))) + ")");
                ctx.Check(Count(r.LogText(), "dropped (" + reason + ")") - d0 == lines.Length, where + ": .log 에 '!!! [tx <id>] dropped (" + reason + ")' " + lines.Length + "줄");
                ctx.Check(Received(r) - rec == r.HumanBytes - h, where + ": 보드 수신 == 사람 송신(spool 0바이트)");
                ctx.Check(Leftovers(live).Count == 0, where + ": " + file + " 소비됨");
            }
            r.Human("vi /tmp/v31s.txt\r");
            r.WaitState(PromptState.Fullscreen, 3000, "vi");
            ctx.WaitUntil(() => r.Board.Phase == "vi", 2000, "보드 vi 모드");
            Thread.Sleep(200);
            int vi0 = r.Board.ViInsertedBytes;
            AllDropped("fullscreen", "vi", r.P + ".tx");
            ctx.Check(r.Board.ViInsertedBytes == vi0, "vi 버퍼 0바이트");
            r.Human("\x1b"); Thread.Sleep(200); r.Human(":q!\r");
            r.WaitState(PromptState.AtPrompt, 3000, "vi 종료");

            r.Human("cat > /tmp/c31s.txt\r");
            r.WaitState(PromptState.Busy, 2000, "cat > (stdin 대기)");
            Thread.Sleep(200);
            AllDropped("busy_since", "cat >", "COM137.tx");
            long ringD = r.Ring.End;
            r.Human("\x04");
            ctx.WaitUntil(() => r.RingText(ringD).Contains("root@SIM31S:"), 3000, "^D 로 cat 종료");
            r.Human("\r");
            r.WaitState(PromptState.AtPrompt, 3000, "사람 Enter");
            var f = r.Board.FsRead("/tmp/c31s.txt");
            ctx.Check(f != null && f.Length == 0, "cat > 파일에 spool 바이트 0 (" + (f?.Length ?? -1) + "B)");
        }

        // ================================================================== spool-status
        [Scenario("spool-status", ".status: <TCP포트>.status·<COMx>.status 원자 교체 주기 기록(구 브리지 필드 + tcp_port·board·state·attached), released·ignored 케이블과 모르는 이름의 .tx·.status 는 건드리지 않음", Order = 750, TimeoutS = 60)]
        public static void SpoolStatus(ScenarioContext ctx)
        {
            var cfg = HubRig.MakeConfig(ctx, 17148, c =>
            {
                c.BasePort = 17290; c.SpoolPollMs = 50; c.StatusIntervalMs = 200;
                c.Cables["SIMCABIG"] = new CableConfig { Mode = "ignore" };
            });
            Directory.CreateDirectory(cfg.EffectiveHubDir);
            File.WriteAllText(Path.Combine(cfg.EffectiveHubDir, "state.json"), "{\"assign\":{},\"released\":[\"SIMCABRL\"],\"boards\":{}}");
            string live = Path.Combine(cfg.LogRoot, "live");
            Directory.CreateDirectory(live);
            // 허브가 뜨기 전부터 있는 파일(구 브리지·다른 도구 것): ignored(COM151)·released(COM152)·모르는 COM·이름
            var keep = new Dictionary<string, string>
            {
                ["COM151.tx"] = "echo ignored\n", ["COM151.status"] = "{\"old\":\"bridge\"}",
                ["COM152.tx"] = "echo released\n", ["COM152.status"] = "{\"old\":\"bridge\"}",
                ["COM199.tx"] = "echo unknown\n", ["foo.tx"] = "echo foo\n",
            };
            foreach (var kv in keep) File.WriteAllText(Path.Combine(live, kv.Key), kv.Value);
            var main = new FakeBoard(new FakeBoardOptions { Hostname = "SIMST" });
            var ig = new FakeBoard(new FakeBoardOptions { Hostname = "SIMIG" });
            var rl = new FakeBoard(new FakeBoardOptions { Hostname = "SIMRL" });
            foreach (var b in new[] { main, ig, rl }) ctx.Defer(b.PowerOff);
            var prov = new FakePortProvider();
            prov.Plug(main, "SIMCABST", "COM150");
            prov.Plug(ig, "SIMCABIG", "COM151");
            prov.Plug(rl, "SIMCABRL", "COM152");
            var r = HubRig.Boot(ctx, cfg, main, prov, "SIMCABST", true);
            var cig = r.Hub.Cables.Find("SIMCABIG");
            var crl = r.Hub.Cables.Find("SIMCABRL");
            ctx.WaitUntil(() => cig?.Status == CableStatus.Ignored && crl?.Status == CableStatus.Released, 3000, "ignored·released 케이블 인식");
            ctx.Check(crl.TcpPort > 0 && crl.TcpPort != r.Cab.TcpPort, "released 케이블 포트 " + crl.TcpPort);
            string relPortTx = Path.Combine(live, crl.TcpPort.ToString(CultureInfo.InvariantCulture) + ".tx");
            File.WriteAllText(relPortTx, "echo released-port\n");
            Thread.Sleep(1000);                                     // spool 20주기·status 5주기

            foreach (var kv in keep)
                ctx.Check(File.Exists(Path.Combine(live, kv.Key)) && File.ReadAllText(Path.Combine(live, kv.Key)) == kv.Value, "건드리지 않음: " + kv.Key);
            ctx.Check(File.Exists(relPortTx), "건드리지 않음: released 케이블의 " + Path.GetFileName(relPortTx));
            ctx.Check(!File.Exists(Path.Combine(live, crl.TcpPort.ToString(CultureInfo.InvariantCulture) + ".status")), "released 케이블 .status 안 씀");
            ctx.Check(rl.TotalReceived == 0 && ig.TotalReceived == 0, "released·ignored 보드 수신 0바이트");
            ctx.Check(!Directory.GetFiles(live).Any(f => f.EndsWith(".taking", StringComparison.OrdinalIgnoreCase)), ".taking 없음(rename 도 안 함)");

            string pS = Path.Combine(live, r.P + ".status"), cS = Path.Combine(live, "COM150.status");
            ctx.Check(File.Exists(pS) && File.Exists(cS), "<포트>.status·COM150.status 생성");
            var t0 = File.GetLastWriteTimeUtc(pS);
            Thread.Sleep(600);
            ctx.Check(File.GetLastWriteTimeUtc(pS) > t0, "주기 갱신(mtime 증가)");
            string js = File.ReadAllText(pS);
            using var doc = JsonDocument.Parse(js);
            var e = doc.RootElement;
            var names = e.EnumerateObject().Select(x => x.Name).ToList();
            var need = new[] { "port", "baud", "pid", "log", "raw", "started", "uptime_s", "rx_bytes", "idle_s", "tcp_port", "board", "state", "attached" };
            ctx.Check(need.All(names.Contains) && names.Take(9).SequenceEqual(need.Take(9)), "필드: 구 브리지 순서 그대로 + 허브 필드 (" + string.Join(",", names) + ")");
            ctx.Check(J.S(e, "port") == "COM150" && J.N(e, "baud") == 115200 && J.N(e, "pid") == Environment.ProcessId && J.N(e, "tcp_port") == r.Cab.TcpPort
                      && J.S(e, "board") == "SIMST" && J.S(e, "state") == "AT_PROMPT" && J.B(e, "attached") == true, "값: port COM150, baud, pid, tcp_port, board SIMST, state AT_PROMPT, attached (" + js + ")");
            ctx.Check(J.S(e, "log") == r.Cab.Writer.CurrentLogPath && J.S(e, "raw") == r.Cab.Writer.CurrentRawPath && Regex.IsMatch(J.S(e, "log"), @"^[A-Za-z]:\\.*\.log$"),
                      "log·raw = 현재 파일 Windows 경로 (" + J.S(e, "log") + ")");
            ctx.Check(Regex.IsMatch(J.S(e, "started") ?? "", @"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d$") && e.GetProperty("uptime_s").GetDouble() >= 1 && e.GetProperty("idle_s").GetDouble() >= 0
                      && J.N(e, "rx_bytes") > 0, "started(s 형식)·uptime_s·idle_s·rx_bytes");
            ctx.Check(File.ReadAllText(cS) is string cj && JsonDocument.Parse(cj).RootElement.GetProperty("tcp_port").GetInt32() == r.Cab.TcpPort, "COM150.status 도 같은 내용");
            r.Hub.Stop();
            ctx.Check(!Directory.GetFiles(live).Any(f => Path.GetFileName(f).Contains(".tmp")), "허브 정지 뒤 임시 파일 남지 않음(원자 교체)");
        }
    }
}
