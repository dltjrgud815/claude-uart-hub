// SpoolWatcher(계획 §3.3 `.tx` 행, §4 S1, §8 E1): <log_root>\live\ 의 구 브리지 호환 입력·상태 파일.
//   입력: <TCP포트>.tx 와 <COMx>.tx 만 본다(둘 다 같은 케이블). attached 케이블 것만 소비하고, released·ignored·분리 중·모르는 이름은 건드리지 않는다
//         (구 브리지와 경합하지 않게). 소비 = .tx 가 spool_quiet_ms(기본 300ms) 동안 크기·mtime 이 그대로일 때 .tx.<n>.taking 으로
//         rename → FileShare.None 으로 열릴 때까지 재시도(rename 전에 연 writer 의 늦은 쓰기까지 보존) → 읽기 → 닫으면서 삭제(DeleteOnClose).
//         조용할 때만 rename 하는 이유(2026-10-29 실측, WSL2 DrvFs 9p cache=0x5): WSL 쪽 `>>` 의 open 과 Windows 쪽 rename 이 겹치면
//         그 open 이 ENOENT 로 실패해 줄이 writer 쪽에서 사라진다(연속 append 600회 중 3~11회). 조용할 때만 rename 하면 0회.
//         줄은 TxLine 분류대로 Arbiter 에 하나씩 넣는다:
//         토큰 단독 줄·{NONL} 줄 = TxGate.Token, 일반 줄 = TxGate.Prompt, Source=Spool. 케이블당 한 번에 한 줄만 제출해 파일 안 줄 순서를 지킨다.
//         대기 상한 tx_ttl_s(기본 60초)는 .tx 의 마지막 쓰기 시각부터 센다(허브 정지·케이블 분리 중 쌓인 오래된 줄이 나중에 실행되지 않게).
//         넘으면 보내지 않고 `!!! [tx <id>] dropped (<reason>)` 를 남긴다. 송신 기록 `>>> [tx] line` 은 Arbiter 가 남긴다.
//   상태: status_interval_ms(기본 2초)마다 <TCP포트>.status 와(attached 일 때) <COMx>.status 를 원자 교체로 쓴다.
//         구 브리지 필드(port, baud, pid, log, raw, started, uptime_s, rx_bytes, idle_s) + 허브 필드(tcp_port, board, state, attached, cable_status, cable).
//         port 는 구 브리지처럼 COM 이름, log·raw 는 현재 파일의 Windows 경로, state 는 PromptTracker 상태.
// 허브는 WSL 경로를 보지 않는다(E1): WSL 쪽은 /mnt/d/uart_logs/live/ 로 같은 폴더에 쓴다.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace UartHub
{
    public sealed class SpoolWatcher
    {
        static readonly Regex ComRe = new Regex(@"^COM\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex TakingRe = new Regex(@"^(?<name>[^.]+)\.tx\.(?<n>\d+)\.taking$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex LineSplit = new Regex(@"\r?\n", RegexOptions.CultureInvariant);
        const int ExclusiveTries = 5;

        readonly Hub hub;
        readonly object sync = new object();
        readonly Dictionary<string, Feeder> feeders = new Dictionary<string, Feeder>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> statusErr = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, (string Sig, long Since)> quiet = new Dictionary<string, (string, long)>(StringComparer.OrdinalIgnoreCase);
        readonly ManualResetEventSlim stopEvt = new ManualResetEventSlim(false);
        Thread th;
        volatile bool stop;
        long takeSeq, dropSeq, filesTaken, linesRead, renameBusy, lateWriterWaits;
        long lastStatusTick = long.MinValue;

        /// <summary>한 줄의 처리가 끝났다(송신 ok, 또는 dropped). 결과는 Arbiter 응답과 같은 형식. 관측용(처리기는 짧게, 예외는 무시).</summary>
        public event Action<Cable, string, JObj> LineDone;

        public string LiveDir { get; }
        public long FilesTaken => Interlocked.Read(ref filesTaken);
        public long LinesRead => Interlocked.Read(ref linesRead);
        /// <summary>.tx rename 이 writer(Delete 공유 없이 열고 있음) 때문에 다음 주기로 미뤄진 횟수.</summary>
        public long RenameBusy => Interlocked.Read(ref renameBusy);
        /// <summary>rename 뒤 FileShare.None 열기를 기다린 횟수(rename 전에 연 writer 가 아직 쓰는 중).</summary>
        public long LateWriterWaits => Interlocked.Read(ref lateWriterWaits);

        public SpoolWatcher(Hub hub)
        {
            this.hub = hub;
            LiveDir = Path.Combine(hub.Config.LogRoot, "live");
            takeSeq = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();     // 재시작 뒤에도 .taking 번호가 커지게
        }

        public void Start()
        {
            try { Directory.CreateDirectory(LiveDir); }
            catch (Exception e) { hub.Log.Write(L.T("live 폴더 생성 실패 " + LiveDir + ": " + e.Message, "failed to create live folder " + LiveDir + ": " + e.Message)); }
            th = new Thread(Loop) { IsBackground = true, Name = "SpoolWatcher" };
            th.Start();
        }

        public void Stop()
        {
            stop = true;
            stopEvt.Set();
            if (th != null && th != Thread.CurrentThread) th.Join(3000);
            List<Feeder> fs;
            lock (sync) { fs = feeders.Values.ToList(); feeders.Clear(); }
            foreach (var f in fs) f.Stop();
        }

        void Loop()
        {
            while (!stop)
            {
                try { PollOnce(); }
                catch (Exception e) { hub.Log.Write(L.T("spool 오류: " + e.GetType().Name + ": " + e.Message, "spool error: " + e.GetType().Name + ": " + e.Message)); }
                long now = Environment.TickCount64;
                if (lastStatusTick == long.MinValue || now - lastStatusTick >= hub.Config.StatusIntervalMs)
                {
                    lastStatusTick = now;
                    try { WriteStatus(); }
                    catch (Exception e) { hub.Log.Write(L.T("status 기록 오류: " + e.GetType().Name + ": " + e.Message, "status write error: " + e.GetType().Name + ": " + e.Message)); }
                }
                stopEvt.Wait(hub.Config.SpoolPollMs);
            }
        }

        // ================================================================== .tx 소비
        sealed class Group { public readonly List<(long N, string Path)> Takings = new List<(long, string)>(); public string Tx; }

        void PollOnce()
        {
            if (!Directory.Exists(LiveDir)) return;
            var groups = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.GetFiles(LiveDir))
            {
                string fn = Path.GetFileName(f);
                if (fn.EndsWith(".tx", StringComparison.OrdinalIgnoreCase) && fn.Length > 3)
                    Get(groups, fn.Substring(0, fn.Length - 3)).Tx = f;
                else
                {
                    var m = TakingRe.Match(fn);
                    if (m.Success && long.TryParse(m.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long n))
                        Get(groups, m.Groups["name"].Value).Takings.Add((n, f));
                }
            }
            foreach (var kv in groups)
            {
                if (stop) return;
                var cable = Resolve(kv.Key);
                if (cable == null) continue;                 // 모르는 이름·released·ignored·분리 중: 건드리지 않는다
                var g = kv.Value;
                bool blocked = false;
                foreach (var t in g.Takings.OrderBy(x => x.N))   // 이전 .taking(늦은 writer 대기·크래시 잔여)부터 순서대로
                    if (!Consume(t.Path, cable)) { blocked = true; break; }
                if (blocked || g.Tx == null || !Quiet(g.Tx)) continue;
                string taking = g.Tx + "." + Interlocked.Increment(ref takeSeq).ToString(CultureInfo.InvariantCulture) + ".taking";
                try { File.Move(g.Tx, taking); }
                catch (FileNotFoundException) { continue; }
                catch (Exception) { Interlocked.Increment(ref renameBusy); continue; }   // writer 가 Delete 공유 없이 열고 있음 → 다음 주기
                quiet.Remove(g.Tx);
                Consume(taking, cable);                      // 아직 열려 있으면 .taking 으로 남아 다음 주기에 다시 시도
            }
            foreach (var k in quiet.Keys.Where(k => !groups.Values.Any(g => string.Equals(g.Tx, k, StringComparison.OrdinalIgnoreCase))).ToList())
                quiet.Remove(k);                             // 사라진 .tx 의 기록 정리
        }

        /// <summary>
        /// .tx 의 (크기, mtime) 이 spool_quiet_ms 동안 그대로인가. 공유 핸들(ReadWrite|Delete)로 재므로 writer 를 막지 않는다.
        /// 처음 본 파일·바뀐 파일은 false(다음 주기에 다시 본다).
        /// </summary>
        bool Quiet(string path)
        {
            string sig;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                sig = fs.Length.ToString(CultureInfo.InvariantCulture) + "|" + File.GetLastWriteTimeUtc(fs.SafeFileHandle).Ticks.ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception) { return false; }              // 사라졌거나 다른 writer 가 배타로 열고 있음
            long now = Environment.TickCount64;
            if (!quiet.TryGetValue(path, out var q) || q.Sig != sig) { quiet[path] = (sig, now); return false; }
            return now - q.Since >= hub.Config.SpoolQuietMs;
        }

        static Group Get(Dictionary<string, Group> d, string name)
        {
            if (!d.TryGetValue(name, out var g)) d[name] = g = new Group();
            return g;
        }

        /// <summary>이름 → attached 케이블. 정수 = TCP 포트, COM\d+ = 현재 그 COM 에 붙은 케이블. 그 밖·분리·released·ignored 는 null.</summary>
        Cable Resolve(string name)
        {
            var all = hub.Cables.Snapshot();
            Cable c = null;
            if (int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out int port))
                c = all.FirstOrDefault(x => x.TcpPort > 0 && x.TcpPort == port);
            else if (ComRe.IsMatch(name))
                c = all.FirstOrDefault(x => x.IsAttached && string.Equals(x.Com, name, StringComparison.OrdinalIgnoreCase));
            return c != null && c.IsAttached && c.TcpPort > 0 ? c : null;
        }

        /// <summary>.taking 하나를 읽고 지운 뒤 줄을 케이블 큐에 넣는다. 아직 다른 핸들이 열려 있으면 false(다음 주기에 다시).</summary>
        bool Consume(string path, Cable cable)
        {
            FileStream fs = null;
            for (int k = 0; fs == null; k++)
            {
                try { fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 4096, FileOptions.DeleteOnClose); }
                catch (FileNotFoundException) { return true; }
                catch (DirectoryNotFoundException) { return true; }
                catch (IOException)
                {
                    if (k == 0) Interlocked.Increment(ref lateWriterWaits);
                    if (k + 1 >= ExclusiveTries || stop) return false;
                    Thread.Sleep(10);
                }
                catch (UnauthorizedAccessException e)
                {
                    hub.Log.Write(L.T("spool " + Path.GetFileName(path) + " 열기 거부: " + e.Message, "spool " + Path.GetFileName(path) + " open denied: " + e.Message));
                    return false;
                }
            }
            byte[] data;
            DateTime mtime;
            using (fs)
            {
                mtime = File.GetLastWriteTimeUtc(fs.SafeFileHandle);   // 배타 핸들을 쥔 동안: 더 이상의 쓰기는 없다
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                data = ms.ToArray();
            }                                                // 닫으면서 삭제(DeleteOnClose)
            Interlocked.Increment(ref filesTaken);
            int off = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;   // 구 브리지 ReadAllText 처럼 BOM 무시
            string text = new UTF8Encoding(false).GetString(data, off, data.Length - off);
            var lines = LineSplit.Split(text).Where(x => x.Length > 0).ToList();   // 구 브리지: -split "\r?\n", 빈 줄 건너뜀
            if (lines.Count == 0) return true;
            Interlocked.Add(ref linesRead, lines.Count);
            long deadline = hub.Clock.MonoMs + (long)((mtime.AddSeconds(hub.Config.TxTtlS) - DateTime.UtcNow).TotalMilliseconds);
            Feeder f;
            lock (sync)
            {
                if (stop) return true;
                if (!feeders.TryGetValue(cable.Id, out f))
                {
                    f = new Feeder(this, cable);
                    feeders[cable.Id] = f;
                    f.Start();
                }
            }
            f.Add(lines, deadline);
            return true;
        }

        void Done(Cable c, string line, JObj res)
        {
            var h = LineDone;
            if (h == null) return;
            try { h(c, line, res); } catch (Exception) { }
        }

        // ================================================================== 케이블별 순차 제출
        sealed class Feeder
        {
            readonly SpoolWatcher w;
            readonly Cable cable;
            readonly Queue<(string Line, long Deadline)> q = new Queue<(string, long)>();
            Thread th;
            volatile bool stop;

            public Feeder(SpoolWatcher w, Cable c) { this.w = w; cable = c; }

            public void Start()
            {
                th = new Thread(Loop) { IsBackground = true, Name = "Spool-" + cable.Id };
                th.Start();
            }

            public void Add(List<string> lines, long deadline)
            {
                lock (q)
                {
                    foreach (var l in lines) q.Enqueue((l, deadline));
                    Monitor.PulseAll(q);
                }
            }

            public void Stop()
            {
                int left;
                lock (q) { stop = true; left = q.Count; q.Clear(); Monitor.PulseAll(q); }
                if (th != null && th != Thread.CurrentThread) th.Join(3000);
                if (left > 0) w.hub.Log.Write(L.T("spool " + cable.Id + " 허브 정지로 미전송 " + left + "줄 버림", "spool " + cable.Id + " hub stopped: dropped " + left + " unsent line(s)"));
            }

            void Loop()
            {
                while (!stop)
                {
                    (string Line, long Deadline) it;
                    lock (q)
                    {
                        while (q.Count == 0 && !stop) Monitor.Wait(q, 500);
                        if (stop) return;
                        it = q.Dequeue();
                    }
                    try { SendOne(it.Line, it.Deadline); }
                    catch (Exception e) { w.hub.Log.Write(L.T("spool " + cable.Id + " 오류: " + e.GetType().Name + ": " + e.Message, "spool " + cable.Id + " error: " + e.GetType().Name + ": " + e.Message)); }
                }
            }

            /// <summary>한 줄: deadline 까지 게이트가 열리기를 기다려 보내거나 dropped. 케이블이 잠깐 분리되면 deadline 안에서 다시 시도한다.</summary>
            void SendOne(string line, long deadline)
            {
                var hub = w.hub;
                var tl = TxLine.Parse(line);
                while (!stop)
                {
                    long left = deadline - hub.Clock.MonoMs;
                    if (left <= 0) { Drop(line); return; }
                    var r = new TxRequest
                    {
                        Kind = TxKind.Send,
                        Gate = tl.IsTokenLine ? TxGate.Token : TxGate.Prompt,
                        Source = TxSource.Spool,
                        Cmd = line,
                        TimeoutS = left / 1000.0,
                    };
                    var req = hub.Arbiter.Submit(cable, r, out _);
                    while (!req.Wait(50))
                    {
                        if (stop) { req.Cancel(); return; }
                        if (hub.Clock.MonoMs >= req.DeadlineMs) hub.Arbiter.ExpireIfWaiting(req);
                    }
                    if (req.Exit == 102 && hub.Clock.MonoMs < deadline)
                    {
                        Thread.Sleep(200);                    // 케이블 분리: deadline 안에 다시 붙으면 보낸다
                        continue;
                    }
                    if (req.Exit != 0 && req.Exit != 101)
                        hub.Log.Write(L.T("spool " + cable.Id + " 줄 미전송(" + req.Exit + "): " + line, "spool " + cable.Id + " line not sent (" + req.Exit + "): " + line));
                    w.Done(cable, line, req.Result);
                    return;
                }
            }

            /// <summary>제출 전에 대기 상한이 지남(앞 줄 대기·오래된 .tx). 보내지 않고 기록한다. 사유는 지금 막고 있는 상태(없으면 ttl).</summary>
            void Drop(string line)
            {
                var hub = w.hub;
                string id = "t" + Interlocked.Increment(ref w.dropSeq).ToString(CultureInfo.InvariantCulture);
                var sn = hub.Tracker(cable)?.Snapshot();
                string reason = !cable.IsAttached ? "no_cable" : sn?.BlockedReason ?? "ttl";
                cable.PostEvent("!!! [tx " + id + "] dropped (" + reason + ")");
                if (!cable.IsAttached) hub.Log.Write("spool " + cable.Id + " !!! [tx " + id + "] dropped (" + reason + "): " + line);
                var res = hub.Arbiter.Error(cable, 101, "busy", L.T("tx_ttl_s(" + hub.Config.TxTtlS.ToString(CultureInfo.InvariantCulture) + "초) 초과 — 보내지 않음",
                                                                  "tx_ttl_s (" + hub.Config.TxTtlS.ToString(CultureInfo.InvariantCulture) + " s) exceeded — not sent"))
                    .Set("req_id", id).Set("blocked_reason", reason);
                w.Done(cable, line, res);
            }
        }

        // ================================================================== .status
        void WriteStatus()
        {
            if (!Directory.Exists(LiveDir)) return;
            foreach (var c in hub.Cables.Snapshot())
            {
                if (stop) return;
                if (c.TcpPort <= 0 || c.Ring == null) continue;
                if (c.Status == CableStatus.Released || c.Status == CableStatus.Ignored || c.Status == CableStatus.Stopped) continue;
                string json = StatusJson(c).ToString();
                Put(Path.Combine(LiveDir, c.TcpPort.ToString(CultureInfo.InvariantCulture) + ".status"), json);
                if (c.IsAttached && c.Com != null && ComRe.IsMatch(c.Com))
                    Put(Path.Combine(LiveDir, c.Com.ToUpperInvariant() + ".status"), json);
            }
        }

        JObj StatusJson(Cable c)
        {
            var now = hub.Clock.Now;
            long mono = hub.Clock.MonoMs;
            DateTime? started = c.AttachedAt;
            double uptime = started.HasValue ? Math.Max(0, (now - started.Value).TotalSeconds) : 0;
            double idle = uptime;
            long end = c.Ring.End;
            if (end > 0 && c.Ring.TryGetArrival(end - 1, out long last, out _) && last >= 0) idle = Math.Max(0, (mono - last) / 1000.0);
            var t = hub.Tracker(c);
            return new JObj()
                .Set("port", c.Com)
                .Set("baud", (long)c.Baud)
                .Set("pid", (long)Environment.ProcessId)
                .Set("log", c.Writer?.CurrentLogPath)
                .Set("raw", c.Writer?.CurrentRawPath)
                .Set("started", started?.ToString("s", CultureInfo.InvariantCulture))
                .Set("uptime_s", Math.Round(uptime, 1))
                .Set("rx_bytes", c.RxBytes)
                .Set("idle_s", Math.Round(idle, 1))
                .Set("tcp_port", (long)c.TcpPort)
                .Set("board", c.Board ?? t?.Snapshot().Board)
                .Set("state", t?.Snapshot().StateName)
                .Set("attached", c.IsAttached)
                .Set("cable_status", hub.CableStatusText(c))
                .Set("cable", c.Id);
        }

        void Put(string path, string json)
        {
            try
            {
                AtomicFile.WriteAllText(path, json);
                lock (sync) statusErr.Remove(path);
            }
            catch (Exception e)
            {
                bool first;
                lock (sync) { first = !statusErr.ContainsKey(path); statusErr[path] = e.Message; }
                if (first) hub.Log.Write(L.T("status 쓰기 실패 " + path + ": " + e.GetType().Name + ": " + e.Message + " (다음 주기에 다시)", "status write failed " + path + ": " + e.GetType().Name + ": " + e.Message + " (retrying next cycle)"));
            }
        }
    }
}
