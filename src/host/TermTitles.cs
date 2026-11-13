// 허브가 띄운 터미널 창 제목 맞추기(2026-11-06 요청): 창 제목 = 트레이 포트 줄과 같은 "<보드> · <COM> · <p>"(Terminals.Title).
//   기록: OpenCmd.OpenTerminal(open·/connect-uart)이 TeraTerm·PuTTY 를 띄우면 <허브>\terminals\<pid>.json 에 한 건을 남긴다
//         {"port","pid","start_ticks"(프로세스 시작 UTC ticks, pid 재사용 구분),"kind","title"(띄울 때 쓴 창 제목)}.
//         open 은 따로 뜬 짧은 프로세스라 감독과 파일로 넘긴다(pid 마다 파일 하나라 쓰는 쪽끼리 겹치지 않는다). MobaXterm 은 기록하지 않는다(본체 창을 여러 탭이 같이 씀).
//   감독(TermTitler.Apply, ls 스냅샷마다): 기록을 읽어 pid+시작 시각으로 살아 있는지 보고(없으면 기록을 지움), 그 포트의 보드명·COM 으로 기대 제목을 만든다.
//     기대 제목이 바뀌면 그 pid 의 보이는 최상위 창(주인 창 없는 것)에 바로 SetWindowText, 그대로면 1분에 한 번만 지금 제목을 보고 다르면 다시 쓴다
//     (TeraTerm 이 스스로 제목을 되돌릴 수 있다). 지금 제목이 기대 제목으로 시작하면(TeraTerm 이 뒤에 붙이는 꼬리) 같다고 본다.
//     "(inactive)" 인 창(끊긴 PuTTY)은 건드리지 않는다. open 이 그 표시로 새 창을 연다.
//   허브가 그 포트로 띄운 프로세스의 창만 바꾼다. 실패는 조용히 넘어가고, 바꾼 창마다 hub.log 한 줄: 터미널 제목 바꿈 pid=… "옛 제목" → "새 제목".
//   창 접근은 ITermWindows(실제 = SystemAccess, 시험 = 가짜 창 목록).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace UartHub
{
    /// <summary>창 접근 이음매(시험은 가짜). Alive = pid 가 살아 있고 시작 시각이 같다. Windows = 보이는 최상위 창(주인 창 없음)과 제목.</summary>
    public interface ITermWindows
    {
        bool Alive(int pid, long startTicks);
        List<(IntPtr Handle, string Title)> Windows(int pid);
        bool SetTitle(IntPtr handle, string title);
    }

    public sealed class SystemTermWindows : ITermWindows
    {
        public static readonly SystemTermWindows Instance = new SystemTermWindows();
        public bool Alive(int pid, long startTicks) => SystemAccess.ProcessAlive(pid, startTicks);
        public List<(IntPtr Handle, string Title)> Windows(int pid) => SystemAccess.TopWindows(pid);
        public bool SetTitle(IntPtr handle, string title) => SystemAccess.SetWindowTextTimeout(handle, title);
    }

    public sealed class TermRecord
    {
        public int Port, Pid;
        public long StartTicks;
        public string Kind, Title;
    }

    public static class TermRecords
    {
        public static string Dir(string hubDir) => Path.Combine(hubDir, "terminals");
        static string FileOf(string hubDir, int pid) => Path.Combine(Dir(hubDir), pid.ToString(CultureInfo.InvariantCulture) + ".json");

        /// <summary>띄운 터미널 한 건(pid ≤ 0·종류가 TeraTerm·PuTTY 아님·허브 폴더 없음이면 남기지 않는다). 실패는 false(조용히).</summary>
        public static bool Add(string hubDir, int port, int pid, long startTicks, string kind, string title)
        {
            if (string.IsNullOrEmpty(hubDir) || pid <= 0 || (kind != "teraterm" && kind != "putty")) return false;
            try
            {
                Directory.CreateDirectory(Dir(hubDir));
                var o = new JObj().Set("port", (long)port).Set("pid", (long)pid).Set("start_ticks", startTicks).Set("kind", kind).Set("title", title);
                return HubFiles.TryWriteAtomic(FileOf(hubDir, pid), o.ToString() + "\n");
            }
            catch (Exception) { return false; }
        }

        public static List<TermRecord> Load(string hubDir)
        {
            var l = new List<TermRecord>();
            string[] files;
            try { files = Directory.Exists(Dir(hubDir)) ? Directory.GetFiles(Dir(hubDir), "*.json") : new string[0]; }
            catch (Exception) { return l; }
            foreach (var f in files)
            {
                var j = HubFiles.ReadJson(f);
                if (!j.HasValue) continue;
                var r = new TermRecord
                {
                    Port = (int)HubFiles.Long(j.Value, "port"), Pid = (int)HubFiles.Long(j.Value, "pid"), StartTicks = HubFiles.Long(j.Value, "start_ticks"),
                    Kind = HubFiles.Str(j.Value, "kind"), Title = HubFiles.Str(j.Value, "title"),
                };
                if (r.Pid > 0 && r.Port > 0) l.Add(r);
            }
            return l;
        }

        public static void Remove(string hubDir, int pid) => HubFiles.TryDelete(FileOf(hubDir, pid));
    }

    /// <summary>감독 쪽: 허브가 띄운 터미널의 창 제목을 그 포트의 보드명·COM 에 맞춘다(머리 주석).</summary>
    public sealed class TermTitler
    {
        public const double RecheckS = 60;

        sealed class Tracked
        {
            public TermRecord Rec;
            public string Applied;
            public DateTime LastCheckUtc;
        }

        readonly string hubDir;
        readonly Action<string> log;
        readonly ITermWindows win;
        readonly Dictionary<int, Tracked> tracked = new Dictionary<int, Tracked>();

        public TermTitler(string hubDir, Action<string> log, ITermWindows win)
        {
            this.hubDir = hubDir; this.log = log ?? (_ => { }); this.win = win;
        }

        /// <summary>지금 지켜보는 pid(시험용).</summary>
        public IReadOnlyCollection<int> Pids => tracked.Keys.ToList();

        /// <summary>
        /// 그 창에 쓸 제목: Terminals.Title(보드·COM·포트) + 세션 중이면 " · 에이징 중"(부록 C.12 — 세션이 시작·끝나면 다음 스냅샷에 바뀐다). PuTTY 는 " - PuTTY" 를 끝에.
        /// </summary>
        public static string WantTitle(string kind, CableRow row, int port)
            => Terminals.WindowTitle(kind, Terminals.Title(row.Board, row.Com, port) + (row.Aging ? AgingText.TitleSuffix : ""));

        /// <summary>
        /// 세션 표시 글(AgingText.TitleSuffix)의 두 언어 값 — 지금 언어 먼저. 언어를 바꾸기 전에 다른 언어로 붙인 제목도 알아보고 뗀다(기계 비교용 값이라 L.T 가 아님).
        /// </summary>
        static string[] Suffixes => new[] { AgingText.TitleSuffix, " · 에이징 중", " · aging" }.Distinct(StringComparer.Ordinal).ToArray();

        /// <summary>지금 제목이 원하는 제목으로 시작하지만 그 뒤가 끝난 세션의 " · 에이징 중"(또는 영어 " · aging")이면(세션 끝 — 붙은 글을 떼야 함) true.</summary>
        public static bool StaleSuffix(string cur, string want)
        {
            string w = want.EndsWith(" - PuTTY", StringComparison.Ordinal) ? want.Substring(0, want.Length - 8) : want;
            var sx = Suffixes;
            if (sx.Any(x => w.EndsWith(x, StringComparison.Ordinal))) return false;
            return sx.Any(x => cur.StartsWith(w + x, StringComparison.Ordinal) || (cur.StartsWith(want, StringComparison.Ordinal) && cur.Substring(want.Length).StartsWith(x, StringComparison.Ordinal)));
        }

        public void Apply(IEnumerable<CableRow> rows, DateTime nowUtc)
        {
            try { ApplyCore(rows, nowUtc); }
            catch (Exception) { }                                     // 제목은 덤이라 감독을 흔들지 않는다
        }

        void ApplyCore(IEnumerable<CableRow> rows, DateTime nowUtc)
        {
            var recs = TermRecords.Load(hubDir);
            foreach (var r in recs)
                if (!tracked.TryGetValue(r.Pid, out var t) || t.Rec.StartTicks != r.StartTicks)
                    tracked[r.Pid] = new Tracked { Rec = r, Applied = r.Title ?? "", LastCheckUtc = nowUtc };
            var onDisk = new HashSet<int>(recs.Select(r => r.Pid));
            var byPort = new Dictionary<int, CableRow>();
            foreach (var c in rows ?? Enumerable.Empty<CableRow>()) if (c != null && c.Port > 0) byPort[c.Port] = c;

            foreach (var pid in tracked.Keys.ToList())
            {
                var t = tracked[pid];
                if (!onDisk.Contains(pid)) { tracked.Remove(pid); continue; }          // 기록이 지워짐
                if (!win.Alive(pid, t.Rec.StartTicks))
                {
                    tracked.Remove(pid);
                    TermRecords.Remove(hubDir, pid);
                    continue;
                }
                if (!byPort.TryGetValue(t.Rec.Port, out var row)) continue;            // 포트가 목록에 없으면 그대로 둔다
                string want = WantTitle(t.Rec.Kind, row, t.Rec.Port);
                bool changed = want != t.Applied;
                if (!changed && (nowUtc - t.LastCheckUtc).TotalSeconds < RecheckS) continue;
                t.LastCheckUtc = nowUtc;
                t.Applied = want;
                foreach (var w in win.Windows(pid))
                {
                    string cur = w.Title ?? "";
                    if (cur.IndexOf("(inactive)", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (cur.StartsWith(want, StringComparison.Ordinal) && !StaleSuffix(cur, want)) continue;
                    if (win.SetTitle(w.Handle, want)) log(L.T("터미널 제목 바꿈 pid=" + pid.ToString(CultureInfo.InvariantCulture) + " \"" + cur + "\" → \"" + want + "\"", "Terminal title changed pid=" + pid.ToString(CultureInfo.InvariantCulture) + " \"" + cur + "\" → \"" + want + "\""));
                }
            }
        }
    }
}
