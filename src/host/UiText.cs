// 사람에게 보이는 창의 글(UI_COPY.md, 2026-11-06). 설치기(Installer)·connect 흐름(ConnectFlow)이 만들고 창(app/)이 그린다.
//   Head(굵은 첫 줄 = 결론) · Body(본문) · Bullets(요약 줄) · Note(작은 회색 줄) · Details("자세히 ▸" 안, 고정폭 — 경로·pid 같은 기술 정보).
//   창이 없는 경로(시험 모드 AutoInstallUi·TestConnectUi)는 Flat() 한 줄로 기록하고, setup --yes(YesUi)는 Tech(예전 기술 문구)를 그대로 쓴다.
// Josa: 변수 뒤 조사(을/를·이/가·은/는·으로/로·과/와)를 마지막 글자의 발음으로 고른다. 한글은 받침, 숫자는 읽는 소리(일·칠·팔 = ㄹ, 십·백·천·만),
//   라틴 문자는 대문자 줄임말이면 글자 이름(L·R = ㄹ, M·N = 받침), 소문자 낱말이면 끝소리(l = ㄹ, m·n·ng = 받침). 닫는 괄호·따옴표·공백은 건너뛴다.
using System;
using System.Collections.Generic;
using System.Linq;

namespace UartHub
{
    public sealed class UiText
    {
        public string Title = L.T("UART 허브", "UART Hub");
        /// <summary>굵은 첫 줄(결론). null 이면 예전 방식 창(본문만).</summary>
        public string Head;
        /// <summary>본문(\n 으로 문단).</summary>
        public string Body;
        public List<string> Bullets = new List<string>();
        /// <summary>작은 회색 줄(프로그램 이름·pid 등).</summary>
        public string Note;
        /// <summary>"자세히 ▸" 안(고정폭). 비면 펼치기 없음.</summary>
        public List<string> Details = new List<string>();
        /// <summary>확인 창의 진행 버튼. 진행 창·안내 창은 쓰지 않는다.</summary>
        public string Ok;
        public string Cancel = L.T("취소", "Cancel");
        /// <summary>Enter 가 진행 버튼인가. 아니면 Enter·Esc 모두 [취소](되돌릴 수 없거나 끊기는 동작).</summary>
        public bool OkIsDefault;
        /// <summary>오류 창의 [로그 폴더 열기] 대상(로그 파일 경로).</summary>
        public string LogPath;
        /// <summary>오류 표시(머리 글을 붉게).</summary>
        public bool Error;
        /// <summary>예전 방식 문구(FromMessage) — 창 없는 기록은 이 글을 바꾸지 않고 쓴다.</summary>
        public string Raw;
        /// <summary>창 없는 setup --yes 의 표준 출력·install.log 에 쓰는 기술 문구(Claude 용 — 화면 문구와 따로 둔다).</summary>
        public string Tech;

        /// <summary>예전 방식 확인 창(제목 + 줄 목록): 줄을 요약 줄로 보인다.</summary>
        public static UiText FromLines(string title, string ok, IReadOnlyList<string> lines)
            => new UiText { Title = title, Ok = ok, OkIsDefault = true, Bullets = (lines ?? new string[0]).ToList() };

        /// <summary>예전 방식 완료·오류 문구: 첫 줄 = 머리, 나머지 = 본문. 기록은 원문 그대로(Raw).</summary>
        public static UiText FromMessage(string message, bool error = false)
        {
            string m = message ?? "";
            int nl = m.IndexOf('\n');
            return new UiText { Head = nl < 0 ? m : m.Substring(0, nl), Body = nl < 0 ? null : m.Substring(nl + 1), Raw = m, Error = error };
        }

        /// <summary>한 줄 기록: 머리 / 본문 / 요약 줄(· 로) / 작은 줄. 줄바꿈은 공백.</summary>
        public string Flat(string sep = " / ")
        {
            var parts = new List<string>();
            void Add(string s) { if (!string.IsNullOrWhiteSpace(s)) parts.Add(s.Replace("\r", "").Replace("\n", " ").Trim()); }
            Add(Head);
            Add(Body);
            if (Bullets.Count > 0) Add(string.Join(" · ", Bullets));
            Add(Note);
            return string.Join(sep, parts);
        }

        /// <summary>창 없는 기록에 쓰는 글: 예전 문구면 그대로, 아니면 Flat().</summary>
        public string LogText => Raw ?? Flat();
    }

    /// <summary>변수 뒤 조사(UI_COPY.md 원칙: `COM8 을` 금지 → `COM8을`). 반환 = 낱말 + 조사.</summary>
    public static class Josa
    {
        public static string EulReul(string w) => w + (Final(w) == 0 ? "를" : "을");
        public static string IGa(string w) => w + (Final(w) == 0 ? "가" : "이");
        public static string EunNeun(string w) => w + (Final(w) == 0 ? "는" : "은");
        public static string GwaWa(string w) => w + (Final(w) == 0 ? "와" : "과");
        /// <summary>으로/로: 받침 없음·ㄹ 받침이면 "로".</summary>
        public static string EuroRo(string w) { int f = Final(w); return w + (f == 0 || f == Rieul ? "로" : "으로"); }

        const int Rieul = 8;

        /// <summary>마지막 소리의 받침: 0 = 없음, 8 = ㄹ, 그 밖 = 다른 받침(한글 종성 번호).</summary>
        public static int Final(string w)
        {
            if (string.IsNullOrEmpty(w)) return 0;
            int i = w.Length - 1;
            while (i >= 0 && !char.IsLetterOrDigit(w[i])) i--;            // 닫는 괄호·따옴표·마침표·공백
            if (i < 0) return 0;
            char c = w[i];
            if (c >= 0xAC00 && c <= 0xD7A3) return (c - 0xAC00) % 28;
            if (c >= '0' && c <= '9') return Digits(w, i);
            if (c < 128 && char.IsLetter(c))
            {
                int s = i;
                while (s > 0 && w[s - 1] < 128 && char.IsLetter(w[s - 1])) s--;
                string word = w.Substring(s, i - s + 1);
                if (char.IsUpper(c)) return Letter(c);                       // 줄임말·끝 대문자(PuTTY, EVB-A1, WSL): 글자 이름
                string lw = word.ToLowerInvariant();
                if (lw.EndsWith("l", StringComparison.Ordinal)) return Rieul;
                if (lw.EndsWith("ng", StringComparison.Ordinal)) return 21;   // ㅇ
                if (lw.EndsWith("m", StringComparison.Ordinal)) return 16;    // ㅁ
                if (lw.EndsWith("n", StringComparison.Ordinal)) return 4;     // ㄴ
                return 0;
            }
            return 0;
        }

        /// <summary>대문자 글자 이름의 받침: L 엘·R 알 = ㄹ, M 엠 = ㅁ, N 엔 = ㄴ, 그 밖 없음.</summary>
        static int Letter(char c) => char.ToUpperInvariant(c) switch { 'L' => Rieul, 'R' => Rieul, 'M' => 16, 'N' => 4, _ => 0 };

        /// <summary>숫자(끝 i): 끝자리 읽는 소리. 0 으로 끝나면 자릿수 이름(십 ㅂ·백 ㄱ·천 ㄴ·만 ㄴ), 숫자 전체가 0 이면 영(ㅇ).</summary>
        static int Digits(string w, int i)
        {
            int s = i;
            while (s > 0 && w[s - 1] >= '0' && w[s - 1] <= '9') s--;
            string n = w.Substring(s, i - s + 1).TrimStart('0');
            if (n.Length == 0) return 21;                                      // 영
            char last = n[n.Length - 1];
            if (last != '0') return last switch { '1' => Rieul, '7' => Rieul, '8' => Rieul, '3' => 16, '6' => 1, _ => 0 };   // 일·칠·팔, 삼, 육
            int z = n.Length - n.TrimEnd('0').Length;
            return z == 1 ? 17 : z == 2 ? 1 : 4;                               // 십(ㅂ) · 백(ㄱ) · 천·만·십만…(ㄴ)
        }
    }
}
