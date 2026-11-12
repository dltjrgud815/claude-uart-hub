// 시뮬 하네스: 주입 시계, FakePortProvider, 시나리오 등록·실행, FakeBoard 직접 콘솔, 로그 파일 도우미.
// 시나리오는 [Scenario("id", "설명")] 정적 메서드로 등록한다(이후 스토리는 새 파일에 메서드만 추가).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace UartHub.Testing
{
    // ====================================================================== 시계
    /// <summary>명시적으로 움직일 때만 가는 시계(.idx 간격처럼 결정적 판정용).</summary>
    public sealed class ManualClock : IClock
    {
        readonly object l = new object();
        DateTime now;
        long mono;
        public ManualClock(DateTime start, long startMono = 0) { now = start; mono = startMono; }
        public DateTime Now { get { lock (l) return now; } }
        public long MonoMs { get { lock (l) return mono; } }
        public void Advance(long ms) { lock (l) { now = now.AddMilliseconds(ms); mono += ms; } }
    }

    /// <summary>실시간으로 흐르되 원하는 시각으로 건너뛸 수 있는 시계(자정 전환 시험용). 벽시계·단조시간이 함께 점프한다.</summary>
    public sealed class ShiftClock : IClock
    {
        readonly Stopwatch sw = Stopwatch.StartNew();
        readonly DateTime start;
        long offsetMs;
        public ShiftClock(DateTime start) { this.start = start; }
        public DateTime Now => start.AddTicks(sw.Elapsed.Ticks).AddMilliseconds(Interlocked.Read(ref offsetMs));
        public long MonoMs => sw.ElapsedMilliseconds + Interlocked.Read(ref offsetMs);
        public void Jump(TimeSpan d) { if (d.Ticks > 0) Interlocked.Add(ref offsetMs, (long)d.TotalMilliseconds); }
        public void JumpTo(DateTime t) => Jump(t - Now);
    }

    // ====================================================================== 포트 공급자
    public sealed class FakePortProvider : IPortProvider
    {
        readonly object l = new object();
        readonly List<KeyValuePair<FakeBoard, PortInfo>> plugged = new List<KeyValuePair<FakeBoard, PortInfo>>();

        public void Plug(FakeBoard board, string cableId, string com)
        {
            lock (l)
            {
                plugged.RemoveAll(p => p.Value.CableId == cableId);
                board.SetComName(com);
                board.Replug();
                plugged.Add(new KeyValuePair<FakeBoard, PortInfo>(board, new PortInfo { CableId = cableId, Com = com, Description = "FakeBoard " + board.Hostname, Provider = this }));
            }
        }

        public void Unplug(string cableId, Exception ex = null)
        {
            FakeBoard b = null;
            lock (l)
            {
                var i = plugged.FindIndex(p => p.Value.CableId == cableId);
                if (i >= 0) { b = plugged[i].Key; plugged.RemoveAt(i); }
            }
            b?.Unplug(ex);
        }

        public IReadOnlyList<PortInfo> Scan() { lock (l) return plugged.Select(p => p.Value).ToList(); }

        public IPort Create(PortInfo info, int baud, Config cfg)
        {
            lock (l)
            {
                var p = plugged.FirstOrDefault(x => x.Value.CableId == info.CableId);
                if (p.Key == null) throw new IOException("The port '" + info.Com + "' does not exist.");
                return p.Key;
            }
        }
    }

    // ====================================================================== 시나리오 등록·실행
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class ScenarioAttribute : Attribute
    {
        public string Id { get; }
        public string Description { get; }
        public int Order { get; set; } = 1000;
        public int TimeoutS { get; set; } = 60;
        public ScenarioAttribute(string id, string description) { Id = id; Description = description; }
    }

    public sealed class ScenarioFailure : Exception { public ScenarioFailure(string m) : base(m) { } }

    public sealed class ScenarioContext
    {
        readonly List<string> details = new List<string>();
        public string Id { get; }
        /// <summary>이 시나리오 전용 작업 폴더(%TEMP%\uart_hub_test\&lt;run&gt;\&lt;id&gt;).</summary>
        public string Dir { get; }
        public IReadOnlyList<string> Details { get { lock (details) return details.ToList(); } }
        readonly List<Action> cleanups = new List<Action>();

        public ScenarioContext(string id, string dir) { Id = id; Dir = dir; Directory.CreateDirectory(dir); }

        public void Log(string s) { lock (details) details.Add(s); }
        public void Check(bool cond, string what)
        {
            if (!cond) throw new ScenarioFailure(what);
            Log("ok: " + what);
        }
        public void Fail(string why) => throw new ScenarioFailure(why);
        public void Defer(Action a) { lock (cleanups) cleanups.Add(a); }
        internal void RunCleanups()
        {
            List<Action> cs;
            lock (cleanups) { cs = cleanups.ToList(); cs.Reverse(); cleanups.Clear(); }
            foreach (var a in cs) { try { a(); } catch (Exception) { } }
        }

        public void WaitUntil(Func<bool> cond, int timeoutMs, string what)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (cond()) { Log("ok: " + what + " (" + sw.ElapsedMilliseconds + "ms)"); return; }
                Thread.Sleep(5);
            }
            if (cond()) { Log("ok: " + what); return; }
            throw new ScenarioFailure("시간 초과(" + timeoutMs + "ms): " + what);
        }

        /// <summary>시뮬 기본 설정: 포트 17001~, 제어 17000, 로그 루트는 이 폴더, 실제 FTDI 열거 끔, 시간 상수 축소.</summary>
        public Config MakeConfig()
        {
            var c = Config.Default();
            c.LogRoot = Path.Combine(Dir, "logs");
            c.HubDir = Path.Combine(Dir, "hub");
            c.BasePort = 17001;
            c.ControlPort = 17000;
            c.Vids = new List<string>();
            c.ScanIntervalMs = 50;
            c.ReadTimeoutMs = 50;
            c.WriterWaitMs = 20;
            c.TailFlushMs = 400;
            c.AutoNudge = false;              // 부록 C.13: 기존(Phase A) 시나리오는 C.13 전 계약(auto_nudge:false = 그 동작 그대로)을 본다. C.13 시나리오가 켠다
            c.RingMb = 1;
            c.QueueMb = 16;
            c.HeartbeatS = 0.5;
            return c;
        }
    }

    public sealed class ScenarioResult
    {
        public string Id, Description, Error;
        public bool Pass;
        public double Seconds;
        public IReadOnlyList<string> Details;
    }

    public static class ScenarioRunner
    {
        sealed class Entry { public ScenarioAttribute A; public MethodInfo M; }

        static List<Entry> Discover()
        {
            return typeof(ScenarioRunner).Assembly.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                .Select(m => new Entry { A = m.GetCustomAttribute<ScenarioAttribute>(), M = m })
                .Where(e => e.A != null)
                .OrderBy(e => e.A.Order).ThenBy(e => e.A.Id, StringComparer.Ordinal)
                .ToList();
        }

        public static string[] Ids() => Discover().Select(e => e.A.Id).ToArray();

        /// <summary>ids 가 비면 전체. 각 줄을 바로 out 에 쓰고, FAIL 개수를 돌려준다.</summary>
        public static int Run(string[] ids, string testRoot, TextWriter output)
        {
            var all = Discover();
            var dup = all.GroupBy(e => e.A.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            var results = new List<ScenarioResult>();
            string runDir = Path.Combine(testRoot, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(runDir);
            PruneOldRuns(testRoot, 5);
            output.WriteLine("# test_root=" + runDir);
            output.WriteLine("# registered=" + string.Join(",", all.Select(e => e.A.Id)));
            foreach (var d in dup) { var r = Fail(d, "중복 등록된 시나리오 id"); results.Add(r); Print(output, r); }

            var selected = new List<Entry>();
            if (ids == null || ids.Length == 0) selected.AddRange(all);
            else
            {
                foreach (var id in ids)
                {
                    var e = all.FirstOrDefault(x => x.A.Id == id);
                    if (e == null) { var r = Fail(id, "등록되지 않은 시나리오 (등록: " + string.Join(", ", all.Select(x => x.A.Id)) + ")"); results.Add(r); Print(output, r); }
                    else selected.Add(e);
                }
            }

            foreach (var e in selected)
            {
                var r = RunOne(e, runDir);
                results.Add(r);
                Print(output, r);
            }
            int pass = results.Count(r => r.Pass), fail = results.Count(r => !r.Pass);
            output.WriteLine("합계: " + pass + " PASS, " + fail + " FAIL (총 " + results.Count + ")");
            output.Flush();
            return fail;
        }

        static ScenarioResult Fail(string id, string why) => new ScenarioResult { Id = id, Pass = false, Error = why, Details = new string[0] };

        static ScenarioResult RunOne(Entry e, string runDir)
        {
            var ctx = new ScenarioContext(e.A.Id, Path.Combine(runDir, Regex.Replace(e.A.Id, @"[^\w\-]", "_")));
            var res = new ScenarioResult { Id = e.A.Id, Description = e.A.Description };
            var sw = Stopwatch.StartNew();
            Exception err = null;
            var th = new Thread(() =>
            {
                try { e.M.Invoke(null, new object[] { ctx }); }
                catch (TargetInvocationException tie) { err = tie.InnerException ?? tie; }
                catch (Exception x) { err = x; }
                finally { ctx.RunCleanups(); }
            }) { IsBackground = true, Name = "Scenario-" + e.A.Id };
            th.Start();
            if (!th.Join(e.A.TimeoutS * 1000)) err = new ScenarioFailure("시나리오 시간 초과(" + e.A.TimeoutS + "s)");
            res.Seconds = sw.Elapsed.TotalSeconds;
            res.Details = ctx.Details;
            if (err == null) res.Pass = true;
            else
            {
                res.Pass = false;
                res.Error = err is ScenarioFailure ? err.Message : err.GetType().Name + ": " + err.Message + " @ " + FirstFrame(err);
            }
            return res;
        }

        static string FirstFrame(Exception e)
        {
            var st = e.StackTrace ?? "";
            var l = st.Split('\n').FirstOrDefault(x => x.Contains("UartHub")) ?? st.Split('\n').FirstOrDefault() ?? "";
            return l.Trim();
        }

        static void Print(TextWriter o, ScenarioResult r)
        {
            if (r.Pass) o.WriteLine("[PASS] " + r.Id + " " + r.Description + " (" + r.Seconds.ToString("F1", CultureInfo.InvariantCulture) + "s)");
            else o.WriteLine("[FAIL] " + r.Id + " " + r.Error);
            foreach (var d in r.Details ?? new string[0]) o.WriteLine("    " + d);
            o.Flush();
        }

        static void PruneOldRuns(string root, int keep)
        {
            try
            {
                foreach (var d in new DirectoryInfo(root).GetDirectories().OrderByDescending(x => x.Name).Skip(keep))
                    try { d.Delete(true); } catch (Exception) { }
            }
            catch (Exception) { }
        }
    }

    // ====================================================================== FakeBoard 직접 콘솔
    /// <summary>허브 없이 FakeBoard 를 직접 열어 바이트를 주고받는 시험 도구. 텍스트는 Latin1(바이트=문자)로 본다.</summary>
    public sealed class BoardConsole : IDisposable
    {
        static readonly Encoding L1 = Encoding.Latin1;
        readonly FakeBoard b;
        readonly MemoryStream all = new MemoryStream();
        readonly Thread th;
        volatile bool stop;
        long readBytes;

        public BoardConsole(FakeBoard board)
        {
            b = board;
            b.Open();
            th = new Thread(() =>
            {
                var buf = new byte[4096];
                while (!stop)
                {
                    int n;
                    try { n = b.Read(buf, 0, buf.Length); } catch (Exception) { return; }
                    if (n > 0) lock (all) { all.Write(buf, 0, n); readBytes += n; }
                }
            }) { IsBackground = true, Name = "BoardConsole" };
            th.Start();
        }

        public long ReadBytes { get { lock (all) return readBytes; } }
        public int Mark() { lock (all) return (int)all.Length; }
        public string Text(int from = 0) { lock (all) { var a = all.ToArray(); return L1.GetString(a, from, a.Length - from); } }
        public void Send(string s) { var d = Encoding.UTF8.GetBytes(s); b.Write(d, 0, d.Length); }

        /// <summary>mark 이후 텍스트에서 정규식이 나올 때까지 대기. 매치까지의 텍스트를 돌려준다.</summary>
        public string WaitFor(string regex, int from, int timeoutMs = 3000)
        {
            var re = new Regex(regex, RegexOptions.Singleline);
            var sw = Stopwatch.StartNew();
            while (true)
            {
                string t = Text(from);
                var m = re.Match(t);
                if (m.Success) return t.Substring(0, m.Index + m.Length);
                if (sw.ElapsedMilliseconds > timeoutMs) throw new ScenarioFailure("보드 출력 대기 초과 /" + regex + "/, 받은 꼬리: " + Visible(t.Length > 300 ? t.Substring(t.Length - 300) : t));
                Thread.Sleep(5);
            }
        }

        /// <summary>ms 동안 mark 이후 정규식이 나오지 않음을 확인.</summary>
        public bool StaysAbsent(string regex, int from, int ms)
        {
            Thread.Sleep(ms);
            return !Regex.IsMatch(Text(from), regex, RegexOptions.Singleline);
        }

        public static string Visible(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (c == '\r') sb.Append("\\r");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\x1b') sb.Append("\\e");
                else if (c < 0x20 || c == 0x7f) sb.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public void Dispose()
        {
            stop = true;
            try { b.Close(); } catch (Exception) { }
            th.Join(1000);
        }
    }

    // ====================================================================== 로그 파일 도우미
    public static class LogFiles
    {
        public static readonly Regex NameRe = new Regex(@"^(\d+)_([^_\\/]+)_(\d{6})(?:_(\d+))?\.(log|raw|idx)$");

        /// <summary>log_root 아래 YYYYMMDD 폴더의 모든 로그 파일(상대경로 "YYYYMMDD\name").</summary>
        public static List<string> All(string logRoot)
        {
            if (!Directory.Exists(logRoot)) return new List<string>();
            return Directory.GetDirectories(logRoot).Where(d => Regex.IsMatch(Path.GetFileName(d), @"^\d{8}$"))
                .SelectMany(d => Directory.GetFiles(d).Select(f => Path.GetFileName(d) + "\\" + Path.GetFileName(f)))
                .OrderBy(x => x, StringComparer.Ordinal).ToList();
        }

        public static string ReadShared(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs, new UTF8Encoding(false));
            return r.ReadToEnd();
        }

        public static long RawTotal(string logRoot)
            => All(logRoot).Where(x => x.EndsWith(".raw", StringComparison.Ordinal)).Sum(x => new FileInfo(Path.Combine(logRoot, x)).Length);

        public static string AllLogText(string logRoot)
            => string.Concat(All(logRoot).Where(x => x.EndsWith(".log", StringComparison.Ordinal)).Select(x => ReadShared(Path.Combine(logRoot, x))));

        /// <summary>.log 한 줄의 +경과초.</summary>
        public static double? Elapsed(string line)
        {
            var m = Regex.Match(line, @"^\[\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} \+\s*(-?\d+\.\d{3})\] ");
            return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : (double?)null;
        }
    }
}
