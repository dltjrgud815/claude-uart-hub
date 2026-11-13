// 부록 C.11 숨김 속성(log_layout 2): 사람이 열 일 없는 콘솔 로그 짝 파일(.raw·.idx)과 콘솔로그\live 폴더에 FILE_ATTRIBUTE_HIDDEN 을 붙인다.
//   속성만 바꾼다 — 동결 DiskWriter·SpoolWatcher 는 그대로이고, 속성은 열린 핸들과 상관없어 쓰는 중인 FileStream 을 깨지 않는다.
//   코어가 케이블이 붙은 뒤(새 파일이 생길 몇 초 뒤 한 번)와 1분마다 오늘·어제 날짜 폴더를 훑는다(자정·크기 전환으로 생긴 새 파일도 다음 차례에).
//   .log 파일과 hub 폴더는 숨기지 않는다. 백신 잠금·권한 오류는 건너뛰고 다음 차례에 다시 한다(hub.log 에 10분에 한 줄).
using System;
using System.Globalization;
using System.IO;
using System.Threading;

namespace UartHub
{
    public sealed class LogHider : IDisposable
    {
        public const int SweepMs = 60_000, AttachDelayMs = 3_000;
        static readonly string[] Patterns = { "*.raw", "*.idx" };

        readonly string root;
        readonly HubLog log;
        readonly Timer timer;
        int busy;

        LogHider(string consoleRoot, HubLog log)
        {
            root = consoleRoot; this.log = log;
            timer = new Timer(_ => Run(), null, AttachDelayMs, SweepMs);
        }

        /// <summary>log_layout 2 면 숨김 훑기를 시작한다(케이블이 붙으면 몇 초 뒤 한 번 더). 아니면 null(Phase A 배치는 그대로).</summary>
        public static LogHider Start(Hub hub)
        {
            var cfg = hub.Config;
            if (cfg.LogLayout < 2 || string.IsNullOrEmpty(cfg.LogRoot)) return null;
            var h = new LogHider(cfg.LogRoot, hub.Log);
            hub.Cables.CableAttached += (c, seq) => h.Kick();
            return h;
        }

        /// <summary>케이블이 붙음: DiskWriter 가 새 파일을 만들 시간을 두고 한 번 훑는다(그 뒤는 1분 주기 그대로).</summary>
        public void Kick()
        {
            try { timer.Change(AttachDelayMs, SweepMs); } catch (ObjectDisposedException) { }
        }

        void Run()
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            try
            {
                int failed = Sweep(root, DateTime.Now, out int hidden);
                if (failed > 0) log?.WriteThrottled("log-hide", L.T("숨김 속성: " + failed + "개 바꾸지 못함(백신 잠금·권한 — 다음 차례에 다시)", "hidden attribute: could not set on " + failed + " item(s) (antivirus lock or permission — retrying next round)"));
            }
            catch (Exception e) { log?.WriteThrottled("log-hide", L.T("숨김 속성 훑기 실패: " + e.GetType().Name + ": " + e.Message, "hidden attribute sweep failed: " + e.GetType().Name + ": " + e.Message)); }
            finally { Volatile.Write(ref busy, 0); }
        }

        /// <summary>콘솔로그\live 와 오늘·어제 날짜 폴더의 .raw·.idx 에 숨김(이미 숨김이면 그대로). 돌려주는 값 = 바꾸지 못한 수, hidden = 이번에 숨긴 수.</summary>
        public static int Sweep(string consoleRoot, DateTime now, out int hidden)
        {
            hidden = 0;
            int failed = 0;
            Hide(Path.Combine(consoleRoot, "live"), ref hidden, ref failed);
            foreach (var d in new[] { now, now.AddDays(-1) })
                HideFiles(Path.Combine(consoleRoot, d.ToString("yyyyMMdd", CultureInfo.InvariantCulture)), ref hidden, ref failed);
            return failed;
        }

        /// <summary>모든 날짜 폴더(이름 8자리 숫자)와 live — 옮기기(LogMigration) 뒤 한 번. 돌려주는 값 = 바꾸지 못한 수.</summary>
        public static int SweepAll(string consoleRoot, out int hidden)
        {
            hidden = 0;
            int failed = 0;
            if (!Directory.Exists(consoleRoot)) return 0;
            Hide(Path.Combine(consoleRoot, "live"), ref hidden, ref failed);
            foreach (var d in Directory.GetDirectories(consoleRoot))
            {
                string n = Path.GetFileName(d);
                if (n.Length == 8 && long.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out _)) HideFiles(d, ref hidden, ref failed);
            }
            return failed;
        }

        static void HideFiles(string dir, ref int hidden, ref int failed)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var pat in Patterns)
            {
                string[] files;
                try { files = Directory.GetFiles(dir, pat); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { failed++; continue; }
                foreach (var f in files) Hide(f, ref hidden, ref failed);
            }
        }

        /// <summary>파일·폴더 하나에 숨김 속성(없으면 건너뜀). 실패(백신 잠금·권한)는 failed 로만 센다.</summary>
        static void Hide(string path, ref int hidden, ref int failed)
        {
            try
            {
                if (!File.Exists(path) && !Directory.Exists(path)) return;
                var a = File.GetAttributes(path);
                if ((a & FileAttributes.Hidden) != 0) return;
                File.SetAttributes(path, (a & ~FileAttributes.Directory) | FileAttributes.Hidden);
                hidden++;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { failed++; }
        }

        public void Dispose()
        {
            try { timer.Dispose(); } catch (Exception) { }
        }
    }
}
