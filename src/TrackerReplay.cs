// PromptTracker 재생 입력 만들기(상태 복원, §3.2) + 실시간 구동(링 구독 펌프).
// 허브 파일(.raw + .idx)에서 도착 시각을 재구성한다: DiskWriter 는 RX 간격이 gap_ms(20ms) 이상일 때만
// {"k":"gap","off":<raw offset>,"ms":<간격>,"el":<attach 기준 도착 초>} 를 남긴다. 그래서
//  - gap 레코드가 없는 청크 경계 = 간격 < 20ms(< T_open): 같은 구간으로 묶어 동시 도착으로 본다.
//  - 구간 i 의 마지막 청크 도착 = 다음 gap 의 el×1000 − ms(정확), 마지막 구간은 그 구간 첫 도착(el×1000).
// {"k":"event","off","el","text"} 중 ">>> [" 로 시작하는 송신 기록(claude/tx/probe)은 그 offset 에 TX 로 끼운다.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace UartHub
{
    public static class HubLogReplay
    {
        sealed class Gap { public long Off, Ms; public double El; }
        sealed class Ev { public long Off; public double El; public string Text; }

        /// <summary>.raw 의 마지막 maxBytes 와 .idx 로 재생 입력을 만든다. Abs 는 .raw 파일 offset.</summary>
        public static List<ReplayItem> LoadTail(string rawPath, string idxPath, int maxBytes)
        {
            byte[] raw;
            long start;
            using (var fs = new FileStream(rawPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long len = fs.Length;
                start = Math.Max(0, len - maxBytes);
                fs.Position = start;
                raw = new byte[len - start];
                int got = 0;
                while (got < raw.Length)
                {
                    int n = fs.Read(raw, got, raw.Length - got);
                    if (n <= 0) break;
                    got += n;
                }
                if (got < raw.Length) Array.Resize(ref raw, got);
            }
            long end = start + raw.Length;

            var gaps = new List<Gap>();
            var evs = new List<Ev>();
            double openEl = 0;
            if (idxPath != null && File.Exists(idxPath))
            {
                string text;
                using (var fs = new FileStream(idxPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(fs, new UTF8Encoding(false)))
                    text = r.ReadToEnd();
                foreach (var l in text.Split('\n'))
                {
                    if (l.Trim().Length == 0) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(l);
                        var o = doc.RootElement;
                        string k = o.GetProperty("k").GetString();
                        if (k == "open" && o.TryGetProperty("el", out var oe)) openEl = ParseEl(oe);
                        else if (k == "gap") gaps.Add(new Gap { Off = o.GetProperty("off").GetInt64(), Ms = o.GetProperty("ms").GetInt64(), El = ParseEl(o.GetProperty("el")) });
                        else if (k == "event" && o.TryGetProperty("text", out var tx) && o.TryGetProperty("off", out var eo))
                        {
                            string t = tx.GetString() ?? "";
                            if (t.StartsWith(">>> [", StringComparison.Ordinal))
                                evs.Add(new Ev { Off = eo.GetInt64(), El = o.TryGetProperty("el", out var ee) ? ParseEl(ee) : 0, Text = t });
                        }
                    }
                    catch (Exception) { /* 쓰는 중인 마지막 줄 등은 건너뛴다 */ }
                }
            }
            gaps.Sort((a, b) => a.Off.CompareTo(b.Off));

            // 구간 경계와 각 구간 도착 시각
            var bounds = new List<long> { 0 };
            var firstEl = new List<double> { openEl };
            foreach (var g in gaps)
            {
                if (g.Off == bounds[bounds.Count - 1]) firstEl[firstEl.Count - 1] = g.El;
                else { bounds.Add(g.Off); firstEl.Add(g.El); }
            }
            long ArrivalOfSegment(int i)
            {
                if (i + 1 < bounds.Count)
                {
                    var next = gaps.First(g => g.Off == bounds[i + 1]);
                    return (long)Math.Round(next.El * 1000) - next.Ms;
                }
                return (long)Math.Round(firstEl[i] * 1000);
            }

            var items = new List<ReplayItem>();
            var cuts = new SortedSet<long>(bounds.Where(b => b > start && b < end));
            foreach (var e in evs) if (e.Off > start && e.Off < end) cuts.Add(e.Off);
            long pos = start;
            var evQ = new Queue<Ev>(evs.Where(e => e.Off >= start && e.Off <= end).OrderBy(e => e.Off));
            void FlushEvents(long upTo)
            {
                while (evQ.Count > 0 && evQ.Peek().Off <= upTo)
                {
                    var e = evQ.Dequeue();
                    // 재연결 줄 지우기 줄은 명령이 아니라 ^E^U 두 바이트다(뒤따른 입력은 기록되지 않는다)
                    byte[] d = e.Text.StartsWith(Cable.ReplugClearEvent, StringComparison.Ordinal) ? new byte[] { 0x05, 0x15 } : Encoding.UTF8.GetBytes(e.Text + "\r");
                    items.Add(new ReplayItem { IsTx = true, Abs = e.Off, Ms = (long)Math.Round(e.El * 1000), Source = SourceOf(e.Text), Data = d });
                }
            }
            foreach (long cut in cuts.Concat(new[] { end }))
            {
                FlushEvents(pos);
                if (cut <= pos) continue;
                int seg = bounds.FindLastIndex(b => b <= pos);
                long ms = ArrivalOfSegment(Math.Max(0, seg));
                var d = new byte[cut - pos];
                Buffer.BlockCopy(raw, (int)(pos - start), d, 0, d.Length);
                items.Add(new ReplayItem { Abs = pos, Data = d, Ms = ms });
                pos = cut;
            }
            FlushEvents(end);
            return items;
        }

        static double ParseEl(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetDouble()
            : double.Parse(e.GetString() ?? "0", NumberStyles.Float, CultureInfo.InvariantCulture);

        static TxSource SourceOf(string text)
        {
            if (text.StartsWith(">>> [tx]", StringComparison.Ordinal)) return TxSource.Spool;
            if (text.StartsWith(">>> [claude-probe]", StringComparison.Ordinal)) return TxSource.Probe;
            return TxSource.Claude;
        }

        /// <summary>log_root 아래 날짜 폴더에서 TCP 포트의 가장 최근 .raw(exclude 제외). 없으면 null.</summary>
        public static string FindLatestRaw(string logRoot, int port, string exclude)
        {
            if (!Directory.Exists(logRoot)) return null;
            string prefix = port.ToString(CultureInfo.InvariantCulture) + "_";
            string ex = exclude == null ? null : Path.GetFullPath(exclude);
            FileInfo best = null;
            foreach (var dir in Directory.GetDirectories(logRoot).Where(d => Path.GetFileName(d).Length == 8 && Path.GetFileName(d).All(char.IsDigit))
                                         .OrderByDescending(d => d, StringComparer.Ordinal).Take(8))
            {
                foreach (var f in Directory.GetFiles(dir, prefix + "*.raw"))
                {
                    if (ex != null && string.Equals(Path.GetFullPath(f), ex, StringComparison.OrdinalIgnoreCase)) continue;
                    var fi = new FileInfo(f);
                    if (best == null || fi.LastWriteTimeUtc > best.LastWriteTimeUtc) best = fi;
                }
            }
            return best?.FullName;
        }
    }

    /// <summary>
    /// 실시간 구동: 케이블 링을 구독해 청크(도착 시각 포함)를 PromptTracker 에 넣고, 새 바이트가 없으면 시간을 진행시킨다.
    /// 시작할 때 prepare(상태 복원 등)를 먼저 실행하고 그 반환 위치부터 읽는다. 예외는 허브 로그로 격리한다.
    /// </summary>
    public sealed class TrackerPump
    {
        const int PollMs = 10;
        readonly PromptTracker tracker;
        readonly Cable cable;
        readonly IClock clock;
        readonly HubLog hlog;
        readonly Func<long> prepare;
        Thread thread;
        volatile bool stop;

        /// <summary>이 펌프가 맡은 attach 세션(Cable.AttachSeq). 늦게 온 attach/detach 통지를 가리는 데 쓴다.</summary>
        public long AttachSeq { get; set; }

        /// <summary>
        /// 앞 세션의 펌프(늦은 detach 로 아직 돌던 것). 이 펌프는 시작하자마자(자기 스레드에서) 그 펌프가 끝나기를 기다린 뒤 복원한다 —
        /// attach 통지 처리기가 Join 으로 막히지 않게(같은 통지를 받는 telnet 재연결 배너가 늦지 않게) 기다림을 펌프 스레드로 옮긴 것이다.
        /// </summary>
        public TrackerPump After { get; set; }

        public TrackerPump(PromptTracker tracker, Cable cable, IClock clock, HubLog hlog, Func<long> prepare)
        {
            this.tracker = tracker; this.cable = cable; this.clock = clock; this.hlog = hlog; this.prepare = prepare;
        }

        public void Start()
        {
            thread = new Thread(Run) { IsBackground = true, Name = "TrackerPump-" + cable.Id };
            thread.Start();
        }

        public void Stop()
        {
            stop = true;
            if (thread != null && thread != Thread.CurrentThread) thread.Join(2000);
        }

        /// <summary>멈추라고만 알린다(기다리지 않음). 끝났는지는 Join 으로 본다.</summary>
        public void RequestStop() { stop = true; }

        /// <summary>멈추라는 요청을 받았는가(prepare 가 복원 적용 직전에 본다).</summary>
        public bool StopRequested => stop;

        public bool Join(int ms) => thread == null || thread == Thread.CurrentThread || thread.Join(ms);

        void Run()
        {
            var prev = After;
            After = null;
            if (prev != null)
            {
                // 앞 펌프를 기다리는 동안에도 자기 stop 을 본다: detach 처리기의 Stop(Join) 이 이 기다림에 막히지 않게
                long until = Environment.TickCount64 + 5000;
                while (!prev.Join(20))
                {
                    if (stop) return;
                    if (Environment.TickCount64 >= until) { hlog?.Write(L.T("tracker " + cable.Id + " 앞 세션 펌프 종료 대기 5초 초과 — 복원을 계속", "tracker " + cable.Id + " previous session pump did not exit within 5 s — continuing the restore")); break; }
                }
            }
            if (stop) return;
            long pos;
            try { pos = prepare(); }
            catch (Exception e)
            {
                hlog?.Write(L.T("tracker prepare 실패 " + cable.Id + ": " + e.GetType().Name + ": " + e.Message, "tracker prepare failed " + cable.Id + ": " + e.GetType().Name + ": " + e.Message));
                pos = cable.AttachRxPos;
                tracker.ResetFresh(pos, clock.MonoMs, AttachSeq);
            }
            var ring = cable.Ring;
            var cur = ring.OpenCursor(pos);
            while (!stop)
            {
                try
                {
                    var r = cur.Next(PollMs);
                    switch (r.Status)
                    {
                        case RingReadStatus.Data:
                            tracker.OnRx(r.Offset, r.Data, 0, r.Data.Length, r.MonoMs >= 0 ? r.MonoMs : clock.MonoMs);
                            break;
                        case RingReadStatus.Timeout:
                            if (ring.End <= cur.Position) tracker.Advance(clock.MonoMs);
                            break;
                        case RingReadStatus.Lost:
                            hlog?.Write(L.T("tracker " + cable.Id + " 링 손실 " + r.Skipped + "B → UNKNOWN", "tracker " + cable.Id + " ring loss " + r.Skipped + "B → UNKNOWN"));
                            break;          // 다음 Data 의 offset 이 앞서 있으면 추적기가 줄 상태를 버리고 UNKNOWN 으로 간다
                        case RingReadStatus.Closed:
                            return;
                    }
                }
                catch (Exception e)
                {
                    hlog?.Write(L.T("tracker " + cable.Id + " 오류: " + e.GetType().Name + ": " + e.Message, "tracker " + cable.Id + " error: " + e.GetType().Name + ": " + e.Message));
                    Thread.Sleep(50);
                }
            }
        }
    }
}
