// 화면·파일·메시지 언어(한국어 ko / 영어 en). 2026-11-13 사용자 결정: 앱 전부에 영어판, 고르는 법 = Windows 표시 언어 + 설정.
//   정하는 순서: 환경변수 UART_HUB_LANG(ko|en — 시험·개발용) → config.json "language"(auto|ko|en, 기본 auto)
//                → auto = Windows 표시 언어(GetUserDefaultUILanguage)가 한국어면 ko, 아니면 en.
//   exe 는 InvariantGlobalization 이라 CultureInfo 로는 Windows 언어를 알 수 없다 → kernel32 를 직접 묻는다.
//   문구는 그 자리에 L.T("한국어", "English") 로 둘 다 쓴다(키 표 없음). 한국어 원문은 바이트 그대로 둔다 —
//   시뮬 시험은 UART_HUB_LANG=ko 로 돌아 한국어 문구를 그대로 확인하므로, ko 결과가 한 글자라도 바뀌면 시험이 잡는다.
//   프로세스(sup·core·cli·open·install)마다 시작할 때 <허브 폴더>\config.json 의 "language" 키만 읽어(ConfigureFromFile) 정한다 —
//   Config 를 만들기 전에(Config 가 ProductProfile 을 처음 읽으면 프로필 오류 글이 그때 언어로 굳는다), config 가 검증에 실패해
//   감독이 config.last-good.json 으로 돌 때도 같은 파일·같은 키(모든 프로세스가 한 곳에서 언어를 얻는다).
//   config 를 읽기 전(또는 못 읽을 때)에는 환경변수 → Windows 언어로 정한다.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace UartHub
{
    public static class L
    {
        public const string EnvVar = "UART_HUB_LANG";
        public const string Auto = "auto", Ko = "ko", En = "en";

        static volatile string cur;               // "ko" | "en" (null = 아직 안 정함)
        static volatile string configured = Auto; // config.json "language"

        /// <summary>지금 언어: "ko" 또는 "en".</summary>
        public static string Current
        {
            get { var c = cur; if (c == null) { c = Resolve(configured); cur = c; } return c; }
        }
        public static bool IsEn => Current == En;
        public static bool IsKo => Current == Ko;

        /// <summary>그 자리 번역: 지금 언어가 영어면 en, 아니면 ko.</summary>
        public static string T(string ko, string en) => IsEn ? en : ko;

        /// <summary>config.json "language" 값으로 다시 정한다(null·빈 값 = auto). 환경변수가 있으면 환경변수가 이긴다.</summary>
        public static void Configure(string configuredLanguage)
        {
            configured = Norm(configuredLanguage) ?? Auto;
            cur = Resolve(configured);
        }

        /// <summary>
        /// config.json 파일에서 최상위 "language" 키만 읽어 정한다(Config.Parse 를 부르지 않는다 — 다른 키가 틀려도 이 키는 살리고,
        /// Config 를 만들며 ProductProfile 을 언어보다 먼저 읽지 않게). 파일이 없거나 깨졌으면 auto(환경변수 → Windows 언어).
        /// </summary>
        public static void ConfigureFromFile(string configPath) => Configure(RawSetting(configPath));

        /// <summary>config.json 의 언어 설정 "auto"|"ko"|"en"(대소문자·공백 무시). 키가 없거나 모르는 값·파일 없음·깨짐 = "auto". 상태는 바꾸지 않는다.</summary>
        public static string SettingIn(string configPath)
        {
            var n = Norm(RawSetting(configPath));
            return IsValidSetting(n) ? n : Auto;
        }

        /// <summary>그 설정(auto|ko|en, 그 밖·null = auto)으로 정하면 될 언어 "ko"|"en"(환경변수가 이김). 지금 언어는 바꾸지 않는다.</summary>
        public static string ResolveFor(string setting) => Resolve(Norm(setting) ?? Auto);

        /// <summary>파일의 최상위 "language" 문자열 그대로(없거나 못 읽으면 null).</summary>
        static string RawSetting(string configPath)
        {
            try
            {
                if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath)) return null;
                string text = HubFiles.ReadShared(configPath);
                return text == null ? null : LanguageKeyOnly(text);
            }
            catch (Exception) { return null; }
        }

        /// <summary>JSONC 에서 최상위 "language" 문자열만 읽는다(없거나 못 읽으면 null).</summary>
        static string LanguageKeyOnly(string text)
        {
            try
            {
                using var doc = JsonDocument.Parse(text.TrimStart('\uFEFF'), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("language", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>시험용: 언어를 강제로 고정한다("ko"|"en"). null 이면 고정을 풀고 다시 계산한다.</summary>
        public static void Use(string lang)
        {
            var n = Norm(lang);
            cur = n == En ? En : n == Ko ? Ko : null;
        }

        /// <summary>config.json "language" 로 쓸 수 있는 값인가.</summary>
        public static bool IsValidSetting(string s) => s == Auto || s == Ko || s == En;

        static string Norm(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().ToLowerInvariant();

        static string Resolve(string configuredLanguage)
        {
            var env = Norm(Environment.GetEnvironmentVariable(EnvVar));
            if (env == Ko || env == En) return env;
            var c = Norm(configuredLanguage);
            if (c == Ko || c == En) return c;
            return WindowsIsKorean() ? Ko : En;
        }

        [DllImport("kernel32.dll")] static extern ushort GetUserDefaultUILanguage();

        /// <summary>Windows 표시 언어가 한국어인가(LANGID 주 언어 0x12 = LANG_KOREAN). Windows 가 아니거나 실패하면 false.</summary>
        public static bool WindowsIsKorean()
        {
            try { return OperatingSystem.IsWindows() && (GetUserDefaultUILanguage() & 0x3ff) == 0x12; }
            catch (Exception) { return false; }
        }
    }
}
