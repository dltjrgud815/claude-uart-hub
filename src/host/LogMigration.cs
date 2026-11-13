// 부록 C.11 로그 폴더 옮기기(log_layout 2 로 바꿀 때 한 번): 허브가 꺼진 휴대용 첫 시작·교체·setup 때 설치기(EnsureHubFiles)가 update mutex 를 쥔 채 부른다.
//   <기준>\YYYYMMDD\        → <기준>\콘솔로그\YYYYMMDD\
//   <기준>\live\            → <기준>\콘솔로그\live\
//   <기준>\events\*.jsonl   → <허브 폴더>\events\
//   <기준>\events\<날짜>\*.txt → <기준>\에이징이벤트\<날짜>\
//   같은 볼륨 안 이름 바꾸기만(SystemAccess.Rename — 복사 뒤 지우기 없음). 대상에 이미 있거나 실패한 항목은 그대로 두고 install.log 에 한 줄씩 적는다.
//   여러 번 불러도 같다(옮길 것이 없으면 아무것도 하지 않는다). 다 옮긴 옛 events\<날짜>\·events\ 가 비면 지우고, 옮긴 날짜 폴더의 .raw·.idx·live 에 숨김 속성.
//   코어가 돌고 있으면(코어 mutex) 열린 파일을 옮기게 되므로 하지 않는다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace UartHub
{
    public static class LogMigration
    {
        public sealed class Item
        {
            public string From, To;
            public bool Dir;
        }

        static readonly Regex DateRe = new Regex(@"^\d{8}$", RegexOptions.CultureInvariant);

        /// <summary>옮길 것(읽기만). baseRoot = 기준 폴더(log_root), hubDir = 허브 폴더.</summary>
        public static List<Item> Plan(string baseRoot, string hubDir)
        {
            var l = new List<Item>();
            if (string.IsNullOrEmpty(baseRoot) || !SystemAccess.DirExists(baseRoot)) return l;
            string con = Path.Combine(baseRoot, Config.ConsoleDirName), evOld = Path.Combine(baseRoot, "events");
            string txt = Path.Combine(baseRoot, Config.EventsTxtDirName), jdir = Path.Combine(hubDir, "events");
            foreach (var d in SystemAccess.ListDirs(baseRoot).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                string n = Path.GetFileName(d);
                if (DateRe.IsMatch(n)) l.Add(new Item { From = d, To = Path.Combine(con, n), Dir = true });
                else if (string.Equals(n, "live", StringComparison.OrdinalIgnoreCase)) l.Add(new Item { From = d, To = Path.Combine(con, "live"), Dir = true });
            }
            if (SystemAccess.DirExists(evOld) && !SystemAccess.SamePath(evOld, jdir))
            {
                foreach (var f in SystemAccess.ListFiles(evOld, "*.jsonl").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    l.Add(new Item { From = f, To = Path.Combine(jdir, Path.GetFileName(f)) });
                foreach (var d in SystemAccess.ListDirs(evOld).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    if (!DateRe.IsMatch(Path.GetFileName(d))) continue;
                    foreach (var f in SystemAccess.ListFiles(d, "*.txt").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                        l.Add(new Item { From = f, To = Path.Combine(txt, Path.GetFileName(d), Path.GetFileName(f)) });
                }
            }
            return l;
        }

        /// <summary>계획 보고(setup 계획만)의 한 줄: 옮길 날짜 폴더·live·jsonl·txt 수. 옮길 것이 없으면 null.</summary>
        public static string Summary(List<Item> plan)
        {
            if (plan == null || plan.Count == 0) return null;
            int days = plan.Count(i => i.Dir && DateRe.IsMatch(Path.GetFileName(i.From))), live = plan.Count(i => i.Dir && !DateRe.IsMatch(Path.GetFileName(i.From)));
            int jsonl = plan.Count(i => !i.Dir && i.From.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)), txt = plan.Count(i => !i.Dir) - jsonl;
            var parts = new List<string>();
            if (days > 0) parts.Add(L.T("날짜 폴더 " + days + "개 → " + Config.ConsoleDirName + "\\", days + " date folder(s) → " + Config.ConsoleDirName + "\\"));
            if (live > 0) parts.Add("live → " + Config.ConsoleDirName + "\\live");
            if (jsonl > 0) parts.Add(L.T("사건 기록 jsonl " + jsonl + "개 → 허브 폴더\\events", jsonl + " event record jsonl file(s) → hub folder\\events"));
            if (txt > 0) parts.Add(L.T("앞뒤 로그 txt " + txt + "개 → " + Config.EventsTxtDirName + "\\<날짜>", txt + " before/after log txt file(s) → " + Config.EventsTxtDirName + "\\<date>"));
            return L.T("로그 폴더 정리(같은 드라이브 안 이름 바꾸기만, 대상에 이미 있으면 그대로): ", "Log folder cleanup (rename within the same drive only; left as is if the target already exists): ") + string.Join(" · ", parts);
        }

        /// <summary>옮기기. log = install.log 한 줄. 돌려주는 값 = (옮김, 건너뜀). 시험 격리 위반은 그대로 던진다.</summary>
        public static (int Moved, int Skipped) Run(string baseRoot, string hubDir, Action<string> log)
        {
            int moved = 0, skipped = 0;
            var plan = Plan(baseRoot, hubDir);
            foreach (var it in plan)
            {
                string why = null;
                try
                {
                    if (SystemAccess.DirExists(it.To) || SystemAccess.FileExists(it.To)) why = L.T("대상에 이미 있음", "target already exists");
                    else
                    {
                        SystemAccess.CreateDir(Path.GetDirectoryName(it.To));
                        SystemAccess.Rename(it.From, it.To);
                    }
                }
                catch (TestIsolationViolation) { throw; }
                catch (Exception e) { why = e.GetType().Name + ": " + e.Message; }
                if (why == null) moved++; else skipped++;
                log(L.T("  로그 폴더 " + (why == null ? "옮김 " : "옮기지 않음(" + why + ") ") + it.From + " → " + it.To, "  Log folder " + (why == null ? "moved " : "not moved (" + why + ") ") + it.From + " → " + it.To));
            }
            if (plan.Count > 0)
            {
                string evOld = Path.Combine(baseRoot, "events");
                try
                {
                    foreach (var d in SystemAccess.ListDirs(evOld)) if (DateRe.IsMatch(Path.GetFileName(d))) SystemAccess.DeleteEmptyDir(d);
                    if (SystemAccess.DeleteEmptyDir(evOld)) log(L.T("  빈 옛 폴더 지움 " + evOld, "  Removed empty old folder " + evOld));
                }
                catch (TestIsolationViolation) { throw; }
                catch (Exception e) { log(L.T("  옛 events 폴더 정리 못 함(그대로 둠): " + e.GetType().Name + ": " + e.Message, "  Could not clean up old events folder (left as is): " + e.GetType().Name + ": " + e.Message)); }
                string con = SystemAccess.Guard(Path.Combine(baseRoot, Config.ConsoleDirName), "write");
                int failed = LogHider.SweepAll(con, out int hidden);
                log(L.T("  숨김 속성(.raw·.idx·live): " + hidden + "개" + (failed > 0 ? ", 바꾸지 못함 " + failed + "개(코어가 1분마다 다시)" : ""), "  Hidden attribute (.raw/.idx/live): " + hidden + (failed > 0 ? ", could not set " + failed + " (the core retries every minute)" : "")));
                log(L.T("  로그 폴더 정리: 옮김 " + moved + "개, 그대로 둠 " + skipped + "개", "  Log folder cleanup: moved " + moved + ", left " + skipped));
            }
            return (moved, skipped);
        }
    }
}
