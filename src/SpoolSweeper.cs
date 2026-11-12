// 임시 기록 보관(log_layout 3, 부록 C.12): 동결 DiskWriter 가 <허브 폴더>\임시기록\<yyyyMMdd>\ 에 쓰는 .log·.raw·.idx 를 오래 두지 않는다.
//   닫힌 파일 중 spool_keep_hours(기본 24시간)보다 오래된 것을 지우고, 합계가 spool_max_mb(기본 2048MB)를 넘으면 오래된 닫힌 파일부터 지운다.
//   지금 DiskWriter 가 쓰는 파일(케이블마다 .log·.raw·.idx)과 1분 안에 바뀐 파일은 지우지 않는다(전환 직후 스냅샷이 늦은 경우 대비). live\ 는 건드리지 않는다.
//   임시기록 폴더 자체에는 숨김 속성을 붙인다(속성만 — 쓰는 중인 핸들과 상관없음). 비게 된 날짜 폴더는 지운다.
//   코어가 시작 30초 뒤 한 번, 그 뒤 10분마다 훑는다. 잠긴 파일·권한 오류는 건너뛰고 다음 차례에(hub.log 에 10분에 한 줄).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace UartHub
{
    public sealed class SpoolSweeper : IDisposable
    {
        public const int SweepMs = 10 * 60_000, FirstDelayMs = 30_000;
        /// <summary>이 안에 바뀐 파일은 닫혔더라도 지우지 않는다(초).</summary>
        public const double FreshS = 60;

        readonly Hub hub;
        readonly Timer timer;
        int busy;

        SpoolSweeper(Hub hub)
        {
            this.hub = hub;
            timer = new Timer(_ => Run(), null, FirstDelayMs, SweepMs);
        }

        /// <summary>log_layout 3 이면 임시기록 폴더를 숨기고 보관 훑기를 시작한다. 아니면 null.</summary>
        public static SpoolSweeper Start(Hub hub)
        {
            var cfg = hub.Config;
            if (cfg.LogLayout < 3 || string.IsNullOrEmpty(cfg.LogRoot)) return null;
            HideDir(cfg.LogRoot);
            return new SpoolSweeper(hub);
        }

        void Run()
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            try
            {
                var cfg = hub.Config;
                HideDir(cfg.LogRoot);
                int failed = Sweep(cfg.LogRoot, DateTime.Now, cfg.SpoolKeepHours, (long)(cfg.SpoolMaxMb * 1024 * 1024), CurrentFiles(hub), out int deleted, out long freed);
                if (deleted > 0) hub.Log.WriteThrottled("spool-sweep", "임시 기록 정리: " + deleted + "개 지움(" + (freed / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + "MB)");
                if (failed > 0) hub.Log.WriteThrottled("spool-sweep-fail", "임시 기록 정리: " + failed + "개 지우지 못함(잠김·권한 — 다음 차례에 다시)");
            }
            catch (Exception e) { hub.Log.WriteThrottled("spool-sweep-fail", "임시 기록 정리 실패: " + e.GetType().Name + ": " + e.Message); }
            finally { Volatile.Write(ref busy, 0); }
        }

        /// <summary>지금 케이블마다 DiskWriter 가 쓰는 파일(.log·.raw·.idx 전체 경로).</summary>
        public static HashSet<string> CurrentFiles(Hub hub)
        {
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in hub.Cables.Snapshot())
            {
                var w = c.Writer;
                if (w == null) continue;
                foreach (var p in new[] { w.CurrentLogPath, w.CurrentRawPath, w.CurrentIdxPath })
                    if (!string.IsNullOrEmpty(p)) keep.Add(Path.GetFullPath(p));
            }
            return keep;
        }

        /// <summary>
        /// 한 번 훑기(시험이 직접 부른다): root\&lt;yyyyMMdd&gt;\ 의 파일 중 keep 에 없고 FreshS 안에 바뀌지 않은 것 →
        /// keepHours 보다 오래된 것을 지우고, 그래도 합계(남은 모든 파일)가 maxBytes 를 넘으면 오래된 것부터 지운다. 돌려주는 값 = 지우지 못한 수.
        /// </summary>
        public static int Sweep(string root, DateTime now, double keepHours, long maxBytes, ICollection<string> keep, out int deleted, out long freed)
        {
            deleted = 0; freed = 0;
            int failed = 0;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return 0;
            var files = new List<FileInfo>();
            var days = new List<string>();
            foreach (var d in Directory.GetDirectories(root))
            {
                string n = Path.GetFileName(d);
                if (n.Length != 8 || !long.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out _)) continue;
                days.Add(d);
                try { files.AddRange(new DirectoryInfo(d).GetFiles()); } catch (Exception) { failed++; }
            }
            long total = files.Sum(f => f.Length);
            bool Closed(FileInfo f) => !(keep?.Contains(f.FullName) ?? false) && (now - f.LastWriteTime).TotalSeconds >= FreshS;
            var closed = files.Where(Closed).OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.FullName, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var f in closed)
            {
                bool old = (now - f.LastWriteTime).TotalHours > keepHours;
                if (!old && (maxBytes <= 0 || total <= maxBytes)) continue;
                try
                {
                    if ((f.Attributes & FileAttributes.ReadOnly) != 0) f.Attributes &= ~FileAttributes.ReadOnly;
                    long len = f.Length;
                    f.Delete();
                    deleted++; freed += len; total -= len;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { failed++; }
            }
            foreach (var d in days)
            {
                try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); }
                catch (Exception) { }
            }
            return failed;
        }

        /// <summary>폴더에 숨김 속성(이미 있으면 그대로). 실패는 조용히(다음 차례에).</summary>
        public static void HideDir(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var di = new DirectoryInfo(dir);
                if ((di.Attributes & FileAttributes.Hidden) == 0) di.Attributes |= FileAttributes.Hidden;
            }
            catch (Exception) { }
        }

        public void Dispose()
        {
            try { timer.Dispose(); } catch (Exception) { }
        }
    }
}
