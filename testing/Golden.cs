// 골든 재생(계획 §3.2 "도착 시각", §4 S6 "골든 기대값 작성 규칙").
// tests/golden/<name>.raw  : 원본 .raw 의 바이트 발췌(가공 없음)
// tests/golden/<name>.log  : 같은 구간의 원본 .log 줄(가공 없음, 구 브리지 형식)
// tests/golden/<name>.expect : 사람이 원시 오프셋을 보고 손으로 쓴 기대값(구현 코드로 만들지 않는다)
//
// 구 브리지 파일은 .idx 가 없으므로 .log 줄 타임스탬프로 도착 시각을 재구성한다(uart_bridge.ps1 동작 근거).
//  - 같은 .log 줄의 바이트는 그 줄 타임스탬프에 동시 도착(브리지는 '\n' 을 읽은 순간 줄을 쓴다).
//  - 개행 없이 끝난 줄(tail flush)은 브리지가 RX 무음 400ms 뒤에야 쓰므로 도착 = 타임스탬프 − 400ms(앞 줄보다 빠르지는 않게).
//    이 줄 뒤의 바이트는 그 400ms 무음 뒤에 왔다. .raw 끝은 열린 줄이다.
//  - "[ts] >>> X" 줄은 .tx 송신(출처 Spool)이다. 송신 바이트는 토큰을 바꾸고 CR 을 붙인다({NONL} 은 로그에 남지 않아 구분 불가, 판정 영향 없음).
//  - "*** …" / "!!! …" 줄은 RX 가 아니다. 빈 줄 "" 은 개행 하나로만 생긴다(브리지는 빈 tail 을 쓰지 않는다) — 그래서 앞 줄이
//    개행으로 끝난 것처럼 보여도 다음 빈 줄 몫의 개행이 없으면 앞 줄은 tail flush 였다.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace UartHub.Testing
{
    public static class BridgeLog
    {
        static readonly Regex TsRe = new Regex(@"^\[(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3}) \+\s*(-?\d+\.\d{3})\] (.*)$", RegexOptions.Singleline);
        static readonly Regex TokRe = new Regex(@"\{(ESC|ENTER|TAB|SPACE|BREAK|CTRL-[A-Z]|HEX:[0-9A-Fa-f]{2,})\}");

        sealed class LogLine { public int No; public long Ms; public string Text; }

        /// <summary>raw = 발췌 바이트(절대 offset baseOff 부터), logText = 같은 구간 .log 줄들(firstLineNo 부터).</summary>
        public static List<ReplayItem> Load(byte[] raw, long baseOff, string logText, int firstLineNo)
        {
            // 브리지와 같은 방식: 한 디코더로 UTF-8 → 문자, NUL 은 버림. 문자마다 [시작, 끝) 바이트.
            var dec = new UTF8Encoding(false, false).GetDecoder();
            var chars = new StringBuilder();
            var cStart = new List<int>();
            var cEnd = new List<int>();
            var one = new byte[1];
            var outc = new char[4];
            int seqStart = 0;
            for (int i = 0; i < raw.Length; i++)
            {
                one[0] = raw[i];
                int n = dec.GetChars(one, 0, 1, outc, 0, false);
                for (int k = 0; k < n; k++)
                {
                    if (outc[k] == '\0') continue;
                    chars.Append(outc[k]); cStart.Add(seqStart); cEnd.Add(i + 1);
                }
                if (n > 0) seqStart = i + 1;
            }
            string text = chars.ToString();

            var lines = new List<LogLine>();
            var split = logText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            for (int i = 0; i < split.Length; i++)
            {
                var m = TsRe.Match(split[i]);
                if (!m.Success) continue;
                lines.Add(new LogLine
                {
                    No = firstLineNo + i,
                    Ms = (long)Math.Round(double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 1000),
                    Text = m.Groups[3].Value,
                });
            }

            var items = new List<ReplayItem>();
            int pos = 0;                 // 문자 위치
            int bytePos = 0;             // 다음 RX 구간 시작 바이트
            long prevArrival = long.MinValue;
            bool IsEventText(string t) => t.StartsWith(">>> ", StringComparison.Ordinal) || t.StartsWith("*** ", StringComparison.Ordinal) || t.StartsWith("!!! ", StringComparison.Ordinal);
            bool At(int p, string t) => string.CompareOrdinal(text, p, t, 0, t.Length) == 0 && p + t.Length <= text.Length;
            int SkipNewline(int p)       // \r* \n 이면 그 뒤 위치, 아니면 -1
            {
                int q = p;
                while (q < text.Length && text[q] == '\r') q++;
                return q < text.Length && text[q] == '\n' ? q + 1 : -1;
            }

            for (int li = 0; li < lines.Count; li++)
            {
                var L = lines[li];
                if (IsEventText(L.Text) && !(At(pos, L.Text) && L.Text.Length > 0))
                {
                    if (L.Text.StartsWith(">>> ", StringComparison.Ordinal))
                        items.Add(new ReplayItem { IsTx = true, Abs = baseOff + bytePos, Ms = L.Ms, Source = TxSource.Spool, Data = TxBytes(L.Text.Substring(4)), LogLine = L.No });
                    continue;
                }
                if (!At(pos, L.Text))
                    throw new InvalidDataException("golden .log 줄 " + L.No + " 이 .raw 와 맞지 않음(문자 위치 " + pos + "): " + Show(L.Text));
                int p2 = pos + L.Text.Length;
                int nxt = SkipNewline(p2);
                bool complete = nxt >= 0;
                if (complete && L.Text.Length > 0)
                {
                    // 되짚기: 다음 RX 줄이 빈 줄인데 그 몫의 개행이 없으면 이 줄은 tail flush 였다
                    var next = lines.Skip(li + 1).FirstOrDefault(x => !(IsEventText(x.Text) && !At(nxt, x.Text)));
                    if (next != null && next.Text.Length == 0 && SkipNewline(nxt) < 0) complete = false;
                }
                if (!complete)
                {
                    if (L.Text.Length == 0) throw new InvalidDataException("golden .log 줄 " + L.No + ": 빈 tail flush 는 있을 수 없음");
                    nxt = p2;
                }
                int endByte = nxt == 0 ? 0 : cEnd[nxt - 1];
                if (li == lines.Count - 1 || !lines.Skip(li + 1).Any(x => !IsEventText(x.Text) || At(nxt, x.Text))) endByte = raw.Length;  // 마지막 RX 줄: 남은 바이트(NUL 등)까지
                long arrival = complete ? L.Ms : L.Ms - 400;
                if (arrival < prevArrival) arrival = prevArrival;
                prevArrival = arrival;
                if (endByte > bytePos)
                {
                    var d = new byte[endByte - bytePos];
                    Buffer.BlockCopy(raw, bytePos, d, 0, d.Length);
                    items.Add(new ReplayItem { Abs = baseOff + bytePos, Data = d, Ms = arrival, LogLine = L.No });
                    bytePos = endByte;
                }
                pos = nxt;
            }
            if (bytePos != raw.Length) throw new InvalidDataException("golden .log 가 .raw 끝까지 덮지 못함: " + bytePos + "/" + raw.Length);
            return items;
        }

        /// <summary>.tx 줄 → 송신 바이트(uart_bridge.ps1 Get-TxOps 와 같은 토큰) + 줄끝 CR.</summary>
        public static byte[] TxBytes(string line)
        {
            var o = new List<byte>();
            int p = 0;
            foreach (Match m in TokRe.Matches(line))
            {
                o.AddRange(Encoding.UTF8.GetBytes(line.Substring(p, m.Index - p)));
                string t = m.Groups[1].Value;
                if (t == "ESC") o.Add(0x1b);
                else if (t == "ENTER") o.Add(0x0d);
                else if (t == "TAB") o.Add(0x09);
                else if (t == "SPACE") o.Add(0x20);
                else if (t.StartsWith("CTRL-", StringComparison.Ordinal)) o.Add((byte)(t[5] - 64));
                else if (t.StartsWith("HEX:", StringComparison.Ordinal)) for (int i = 4; i + 1 < t.Length; i += 2) o.Add(Convert.ToByte(t.Substring(i, 2), 16));
                p = m.Index + m.Length;
            }
            o.AddRange(Encoding.UTF8.GetBytes(line.Substring(p)));
            o.Add(0x0d);
            return o.ToArray();
        }

        static string Show(string s) => BoardConsole.Visible(s.Length > 80 ? s.Substring(0, 80) + "…" : s);
    }

    /// <summary>.expect 파일(사람이 쓴 기대값). 한 줄에 하나, '#' 뒤는 근거 주석.</summary>
    public sealed class GoldenExpect
    {
        public sealed class Point
        {
            public string Kind;          // "cand" | "state" | "probe"
            public string Where;         // "@<off>" | "tx<line>" | "end" (cand 는 offset)
            public long Off = -1;
            public int TxLine = -1;
            public string Value;         // 판정·상태명·yes/no
            public string Note;
            public int FileLine;
            public override string ToString() => Kind + " " + Where + " " + Value;
        }

        public string Name, Source, Mode = "bridge", Board;
        public long RangeStart = -1, RangeEnd = -1;
        public int LogFirst = -1, LogLast = -1;
        public readonly List<Point> Points = new List<Point>();

        public static GoldenExpect Parse(string path)
        {
            var e = new GoldenExpect { Name = Path.GetFileNameWithoutExtension(path) };
            var lines = File.ReadAllLines(path, new UTF8Encoding(false));
            for (int i = 0; i < lines.Length; i++)
            {
                string l = lines[i];
                string note = null;
                int h = l.IndexOf('#');
                if (h >= 0) { note = l.Substring(h + 1).Trim(); l = l.Substring(0, h); }
                var f = l.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (f.Length == 0) continue;
                string Err(string why) => e.Name + ".expect:" + (i + 1) + " " + why;
                switch (f[0])
                {
                    case "source": e.Source = f[1]; break;
                    case "range": e.RangeStart = long.Parse(f[1], CultureInfo.InvariantCulture); e.RangeEnd = long.Parse(f[2], CultureInfo.InvariantCulture); break;
                    case "log_lines": e.LogFirst = int.Parse(f[1], CultureInfo.InvariantCulture); e.LogLast = int.Parse(f[2], CultureInfo.InvariantCulture); break;
                    case "mode": e.Mode = f[1]; break;
                    case "board": e.Board = f[1]; break;
                    case "cand":
                        if (f.Length != 3) throw new FormatException(Err("cand <off> <confirmed|unconfirmed|foreign>"));
                        e.Points.Add(new Point { Kind = "cand", Where = f[1], Off = long.Parse(f[1], CultureInfo.InvariantCulture), Value = f[2], Note = note, FileLine = i + 1 });
                        break;
                    case "state":
                    case "probe":
                        if (f.Length != 3) throw new FormatException(Err(f[0] + " @<off>|tx<line>|end <값>"));
                        var p = new Point { Kind = f[0], Where = f[1], Value = f[2], Note = note, FileLine = i + 1 };
                        if (f[1].StartsWith("@", StringComparison.Ordinal)) p.Off = long.Parse(f[1].Substring(1), CultureInfo.InvariantCulture);
                        else if (f[1].StartsWith("tx", StringComparison.Ordinal)) p.TxLine = int.Parse(f[1].Substring(2), CultureInfo.InvariantCulture);
                        else if (f[1] != "end") throw new FormatException(Err("위치는 @<off>|tx<line>|end"));
                        if (p.Kind == "state" && !PromptTracker.TryParseName(p.Value, out _)) throw new FormatException(Err("상태명 오류: " + p.Value));
                        if (p.Kind == "probe" && p.Value != "yes" && p.Value != "no") throw new FormatException(Err("probe 값은 yes|no"));
                        e.Points.Add(p);
                        break;
                    default: throw new FormatException(Err("알 수 없는 지시어 " + f[0]));
                }
            }
            if (e.Points.Count == 0) throw new FormatException(e.Name + ".expect: 기대 지점 없음");
            foreach (var p in e.Points) if (string.IsNullOrEmpty(p.Note)) throw new FormatException(e.Name + ".expect:" + p.FileLine + " 근거 주석(#) 없음");
            return e;
        }
    }

    /// <summary>골든 재생 결과(시나리오가 이어서 쓸 수 있게 추적기·마지막 시각·위치를 돌려준다).</summary>
    public sealed class GoldenRun
    {
        public PromptTracker Tracker;
        public GoldenExpect Expect;
        public long LastMs, EndAbs;
        public int Checked;
    }

    public static class Golden
    {
        public static string Dir
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("UART_HUB_GOLDEN");
                if (!string.IsNullOrEmpty(env)) return env;
                string bin = Path.GetDirectoryName(typeof(Golden).Assembly.Location);
                return Path.GetFullPath(Path.Combine(bin, "..", "tests", "golden"));
            }
        }

        /// <summary>
        /// 골든 하나를 재생하고 .expect 의 모든 지점을 ctx.Check 로 대조한다. endAdvance=false 면 끝에서 시간을 진행하지 않는다
        /// (시나리오가 이어서 합성 입력을 넣을 때). "end" 지점은 endAdvance=true 일 때만 본다.
        /// </summary>
        public static GoldenRun Run(ScenarioContext ctx, string name, bool endAdvance = true)
        {
            string dir = Dir;
            var exp = GoldenExpect.Parse(Path.Combine(dir, name + ".expect"));
            byte[] raw = File.ReadAllBytes(Path.Combine(dir, name + ".raw"));
            ctx.Check(exp.RangeEnd - exp.RangeStart == raw.Length, "golden " + name + ": .raw 길이 " + raw.Length + " == range [" + exp.RangeStart + ", " + exp.RangeEnd + ")");
            var cfg = Config.Default();
            var t = new PromptTracker(cfg, exp.Board);
            var run = new GoldenRun { Tracker = t, Expect = exp };
            if (exp.Mode == "cat") RunCat(ctx, exp, raw, run);
            else
            {
                string logText = File.ReadAllText(Path.Combine(dir, name + ".log"), new UTF8Encoding(false));
                var items = BridgeLog.Load(raw, exp.RangeStart, logText, exp.LogFirst);
                RunItems(ctx, exp, items, run, endAdvance, null);
            }
            ctx.Log("golden " + name + ": 기대 지점 " + run.Checked + "개 일치");
            return run;
        }

        static void RunItems(ScenarioContext ctx, GoldenExpect exp, List<ReplayItem> items, GoldenRun run, bool endAdvance, Func<long, long> mapSrcToAbs)
        {
            var t = run.Tracker;
            var atOff = exp.Points.Where(p => p.Kind != "cand" && p.Off >= 0).GroupBy(p => mapSrcToAbs == null ? p.Off : mapSrcToAbs(p.Off)).ToDictionary(g => g.Key, g => g.ToList());
            var atTx = exp.Points.Where(p => p.TxLine >= 0).GroupBy(p => p.TxLine).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var p in exp.Points.Where(p => p.TxLine >= 0))
                if (!items.Any(x => x.IsTx && x.LogLine == p.TxLine)) ctx.Fail(exp.Name + ".expect:" + p.FileLine + " tx" + p.TxLine + " 에 해당하는 송신 줄 없음");
            foreach (var k in atOff.Keys)
                if (!items.Any(x => !x.IsTx && k >= x.Abs && k < x.Abs + x.Data.Length)) ctx.Fail(exp.Name + " @" + k + " 가 재생 범위 밖");

            bool started = false;
            long lastMs = 0;
            foreach (var it in items)
            {
                if (!started) { t.ResetFresh(it.Abs, it.Ms); started = true; }
                if (it.IsTx)
                {
                    if (atTx.TryGetValue(it.LogLine, out var ps)) { t.Advance(it.Ms); CheckAll(ctx, exp, ps, t, it.Ms); }
                    t.OnTx(it.Source, it.Data, it.Ms, it.Abs);
                    lastMs = Math.Max(lastMs, it.Ms);
                    continue;
                }
                long a = it.Abs, endA = it.Abs + it.Data.Length;
                var cuts = atOff.Keys.Where(k => k > a && k < endA).OrderBy(k => k).ToList();
                cuts.Add(endA);
                long p0 = a;
                foreach (long cut in cuts)
                {
                    if (atOff.TryGetValue(p0, out var ps)) { t.Advance(it.Ms); CheckAll(ctx, exp, ps, t, it.Ms); }
                    t.OnRx(p0, it.Data, (int)(p0 - a), (int)(cut - p0), it.Ms);
                    p0 = cut;
                }
                lastMs = Math.Max(lastMs, it.Ms);
                run.EndAbs = endA;
            }
            run.LastMs = lastMs;
            if (endAdvance)
            {
                long endMs = lastMs + 600000;          // .raw 끝 = 열린 줄
                t.Advance(endMs);
                CheckAll(ctx, exp, exp.Points.Where(p => p.Where == "end").ToList(), t, endMs);
                run.LastMs = endMs;
            }
            CheckCands(ctx, exp, t, mapSrcToAbs, run);
            run.Checked += exp.Points.Count(p => p.Kind != "cand" && (endAdvance || p.Where != "end"));
        }

        static void CheckAll(ScenarioContext ctx, GoldenExpect exp, List<GoldenExpect.Point> ps, PromptTracker t, long ms)
        {
            foreach (var p in ps)
            {
                if (p.Kind == "state")
                {
                    var sn = t.Snapshot();
                    ctx.Check(sn.StateName == p.Value, exp.Name + " " + p.Where + " 상태 " + p.Value + (sn.StateName == p.Value ? "" : " (실제 " + sn.StateName + ")") + " — " + p.Note);
                }
                else if (p.Kind == "probe")
                {
                    bool ok = t.CanProbe(ms, false, out string why);
                    ctx.Check(ok == (p.Value == "yes"), exp.Name + " " + p.Where + " probe " + p.Value + (ok == (p.Value == "yes") ? "" : " (실제 " + (ok ? "yes" : "no:" + why) + ")") + " — " + p.Note);
                }
            }
        }

        static void CheckCands(ScenarioContext ctx, GoldenExpect exp, PromptTracker t, Func<long, long> mapSrcToAbs, GoldenRun run)
        {
            var expC = exp.Points.Where(p => p.Kind == "cand").ToList();
            var got = t.Candidates();
            Func<long, long> toSrc = mapSrcToAbs == null ? (x => x) : MapBack(exp, mapSrcToAbs);
            var gotSrc = got.Select(c => new { c, src = toSrc(c.Start) }).Where(x => x.src >= exp.RangeStart && x.src < exp.RangeEnd).ToList();
            string gotList = string.Join(", ", gotSrc.Select(x => x.src + ":" + x.c.Verdict.ToString().ToLowerInvariant() + "(" + x.c.By + ")"));
            ctx.Check(gotSrc.Count == expC.Count && expC.All(p => gotSrc.Any(x => x.src == p.Off)),
                      exp.Name + " 후보 위치 집합 == 기대 {" + string.Join(", ", expC.Select(p => p.Off)) + "} (실제 {" + gotList + "})");
            foreach (var p in expC)
            {
                var g = gotSrc.First(x => x.src == p.Off).c;
                string v = g.Verdict.ToString().ToLowerInvariant();
                ctx.Check(v == p.Value, exp.Name + " 후보 " + p.Off + " " + p.Value + (v == p.Value ? "" : " (실제 " + v + "/" + g.By + ")") + " — " + p.Note);
                run.Checked++;
            }
        }

        static Func<long, long> MapBack(GoldenExpect exp, Func<long, long> mapSrcToAbs)
        {
            var back = new Dictionary<long, long>();
            for (long s = exp.RangeStart; s < exp.RangeEnd; s++) { long a = mapSrcToAbs(s); if (a >= 0 && !back.ContainsKey(a)) back[a] = s; }
            return a => back.TryGetValue(a, out var s) ? s : -1;
        }

        /// <summary>
        /// cat 재생: 저장 로그 발췌를 보드에서 `cat` 한 것처럼 감싼다. 합성 부분(근거: busybox 셸·tty ONLCR·FTDI 115200 연속 출력):
        /// 열린 프롬프트(t=0) → 사람 TX "cat /tmp/saved.log\r"(t=1000) → 에코(t=1001) → 발췌 바이트(\n → \r\n)를 62B 씩 5ms 간격
        /// → 다음 패킷에 진짜 프롬프트. 기대값의 offset 은 원본 발췌 기준이다.
        /// </summary>
        static void RunCat(ScenarioContext ctx, GoldenExpect exp, byte[] raw, GoldenRun run)
        {
            string prompt = "root@" + exp.Board + ":/$ ";
            var stream = new List<byte>();
            var srcOf = new List<long>();
            void Put(byte[] b, long src) { foreach (var x in b) { stream.Add(x); srcOf.Add(src); } }
            var items = new List<ReplayItem>();
            Put(Encoding.ASCII.GetBytes(prompt), -1);
            items.Add(new ReplayItem { Abs = 0, Data = Encoding.ASCII.GetBytes(prompt), Ms = 0 });
            byte[] cmd = Encoding.ASCII.GetBytes("cat /tmp/saved.log\r");
            items.Add(new ReplayItem { IsTx = true, Abs = stream.Count, Data = cmd, Ms = 1000, Source = TxSource.Human });
            byte[] echo = Encoding.ASCII.GetBytes("cat /tmp/saved.log\r\n");
            items.Add(new ReplayItem { Abs = stream.Count, Data = echo, Ms = 1001 });
            Put(echo, -1);
            int bodyStart = stream.Count;
            for (int i = 0; i < raw.Length; i++)
            {
                if (raw[i] == (byte)'\n') { stream.Add((byte)'\r'); srcOf.Add(-1); }   // tty ONLCR
                stream.Add(raw[i]); srcOf.Add(exp.RangeStart + i);
            }
            Put(Encoding.ASCII.GetBytes(prompt), -2);
            long ms = 1002;
            for (int p = bodyStart; p < stream.Count; p += 62)
            {
                int n = Math.Min(62, stream.Count - p);
                items.Add(new ReplayItem { Abs = p, Data = stream.GetRange(p, n).ToArray(), Ms = ms });
                ms += 5;
            }
            // 끝 프롬프트가 앞 패킷과 같은 덩어리에 붙지 않게 마지막 패킷 경계를 프롬프트 시작에 맞춘다
            int promptAt = stream.Count - prompt.Length;
            var fixedItems = new List<ReplayItem>();
            foreach (var it in items)
            {
                if (it.IsTx || it.Abs + it.Data.Length <= promptAt || it.Abs >= promptAt || it.Abs < bodyStart) { fixedItems.Add(it); continue; }
                int k = (int)(promptAt - it.Abs);
                fixedItems.Add(new ReplayItem { Abs = it.Abs, Data = it.Data.Take(k).ToArray(), Ms = it.Ms });
                fixedItems.Add(new ReplayItem { Abs = promptAt, Data = it.Data.Skip(k).ToArray(), Ms = it.Ms + 5 });
            }
            var srcArr = srcOf.ToArray();
            Func<long, long> map = s => { for (int i = 0; i < srcArr.Length; i++) if (srcArr[i] == s) return i; return -1; };
            RunItems(ctx, exp, fixedItems, run, true, map);
            var final = run.Tracker.Candidates().LastOrDefault();
            ctx.Check(final != null && final.Start == promptAt && final.Verdict == CandidateVerdict.Confirmed,
                      exp.Name + ": cat 이 끝난 뒤 진짜 프롬프트(합성 " + promptAt + ")는 열린 줄로 확정");
            var hist = run.Tracker.History();
            long catTx = 1000, promptMs = fixedItems.Last().Ms;
            ctx.Check(!hist.Any(h => h.State == PromptState.AtPrompt && h.Ms > catTx && h.Ms < promptMs),
                      exp.Name + ": cat TX(1000ms) ~ 진짜 프롬프트 도착(" + promptMs + "ms) 사이 AT_PROMPT 0회 → Claude 송신 0");
        }
    }
}
