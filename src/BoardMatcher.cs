// 기록 코어 자체의 최소 매처(§3.1): 보드명(프롬프트 호스트명)과 부팅 경계.
// PromptTracker 에 의존하지 않는다(P1). 판정 전 NUL·ESC[K·SGR 를 지우고 \r 도 줄 시작으로 본다(§3.2 정규화).
using System;
using System.Text.RegularExpressions;

namespace UartHub
{
    public sealed class BoardMatcher
    {
        static readonly Regex Strip = new Regex(@"\x00|\x1b\[K|\x1b\[[0-9;]*m", RegexOptions.Compiled);
        readonly Regex boardRe;
        readonly Regex bootRe;

        public BoardMatcher(Config cfg)
        {
            boardRe = new Regex(cfg.BoardRe, RegexOptions.Compiled);
            bootRe = new Regex(cfg.BootRe, RegexOptions.Compiled);
        }

        public static string Normalize(string s) => s.IndexOf('\x1b') < 0 && s.IndexOf('\0') < 0 ? s : Strip.Replace(s, "");

        /// <summary>줄(부분 줄 포함)에서 보드명. 여러 개면 마지막 것.</summary>
        public string MatchBoard(string normalized)
        {
            var ms = boardRe.Matches(normalized);
            if (ms.Count == 0) return null;
            var g = ms[ms.Count - 1].Groups;
            return g.Count > 1 && g[1].Success ? g[1].Value : ms[ms.Count - 1].Value;
        }

        /// <summary>부팅 경계 매치 텍스트(없으면 null). \r 로 나뉜 각 구간을 줄 시작으로 본다.</summary>
        public string MatchBoot(string normalized)
        {
            foreach (var seg in normalized.Split('\r'))
            {
                var m = bootRe.Match(seg);
                if (m.Success) return m.Value;
            }
            return null;
        }
    }
}
