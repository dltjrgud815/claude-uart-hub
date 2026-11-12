// 제품 프로필: 보드·펌웨어마다 다른 "기본값"(로더 표시·부팅 경계·U-Boot 프롬프트·앱 오류 패턴·앱 오류 코드 이름)을 코드 밖 파일로 둔다.
//
//   찾는 순서: 환경변수 UART_HUB_PROFILE(파일 경로 — 빈 값이면 프로필 없음, 개발·시험용)
//            → exe 옆 UartHub.profile.json → 없으면 일반 Linux 기본값(Generic).
//   우선순위: 일반 기본값 < 프로필 < config.json(같은 키를 주면 config 가 이긴다). events.patterns 의 off 는 프로필 패턴에도 쓸 수 있다.
//   깨진 프로필: 일반 기본값으로 돌고 Error 에 이유를 남긴다(hub status --json 의 profile.error, hub.log).
//
// 파일 형식(JSONC — 주석·끝 쉼표 허용):
//   {
//     "name": "evb-a",                                   // 표시용
//     "loader_marker_re": "LDR>T",                       // 로더 표시(줄 어디에 있든 실제 리셋). 빈 문자열 = 없음
//     "boot_re": "reboot: Restarting system|LDR>T|^\\[\\s+0\\.0\\d+\\]",
//     "uboot_prompt_re": "^evb: ",
//     "event_patterns": { "app_error": ["APPFAULT\\((?!(?i:BA\\)\\(000f\\)))"] },   // 종류마다 일반 기본 패턴에 더한다
//     "app_codes": [ { "re": "APPFAULT\\([^)\\s]*\\)" }, { "re": "(?i)app_wdt", "code": "WDT" } ]  // 앱 오류 txt 이름의 코드
//   }
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UartHub
{
    public sealed class ProductProfile
    {
        public const string FileName = "UartHub.profile.json";
        public const string EnvVar = "UART_HUB_PROFILE";
        public const string GenericBootRe = @"reboot: Restarting system|^\[\s+0\.0\d+\]";
        public const string GenericUbootPromptRe = "^=> ";

        /// <summary>앱 오류 코드 규칙: Re 가 맞으면 Code(없으면 맞은 글자 그대로).</summary>
        public sealed class AppCodeRule
        {
            public string Text;
            public Regex Re;
            public string Code;
        }

        public string Name { get; private set; }
        /// <summary>읽은 파일(프로필이 없으면 null).</summary>
        public string Path { get; private set; }
        /// <summary>읽기·검증 실패 이유(있으면 이 프로필은 일반 기본값으로 동작).</summary>
        public string Error { get; private set; }
        public string LoaderMarkerRe { get; private set; } = "";
        public string BootRe { get; private set; } = GenericBootRe;
        public string UbootPromptRe { get; private set; } = GenericUbootPromptRe;
        public IReadOnlyDictionary<string, string[]> EventDefaults { get; private set; } = EventKinds.GenericDefaults;
        public IReadOnlyList<AppCodeRule> AppCodes { get; private set; } = Array.Empty<AppCodeRule>();

        public bool IsGeneric => Path == null && Name == null;

        public static readonly ProductProfile Generic = new ProductProfile();

        static readonly object gate = new object();
        static ProductProfile current;

        /// <summary>이 프로세스의 프로필(처음 부를 때 한 번 찾는다).</summary>
        public static ProductProfile Current { get { lock (gate) return current ??= Discover(); } }

        /// <summary>시험용: 이 프로필로 바꾸고 Dispose 때 이전 것으로 되돌린다(null = 일반 기본값).</summary>
        public static IDisposable Use(ProductProfile p)
        {
            lock (gate)
            {
                var old = current;
                current = p ?? Generic;
                return new Restore(old);
            }
        }

        sealed class Restore : IDisposable
        {
            readonly ProductProfile old;
            bool done;
            public Restore(ProductProfile old) { this.old = old; }
            public void Dispose() { lock (gate) { if (done) return; done = true; current = old; } }
        }

        public static ProductProfile Discover()
        {
            string env = Environment.GetEnvironmentVariable(EnvVar);
            if (env != null) return env.Length == 0 ? Generic : Load(env);
            string p;
            try { p = System.IO.Path.Combine(AppContext.BaseDirectory, FileName); }
            catch (Exception) { return Generic; }
            return File.Exists(p) ? Load(p) : Generic;
        }

        public static ProductProfile Load(string path)
        {
            try
            {
                var r = Parse(File.ReadAllText(path));
                r.Path = path;
                return r;
            }
            catch (ProfileException e) { return new ProductProfile { Path = path, Error = e.Message }; }
            catch (Exception e) { return new ProductProfile { Path = path, Error = e.GetType().Name + ": " + e.Message }; }
        }

        public sealed class ProfileException : Exception
        {
            public ProfileException(string m) : base(m) { }
        }

        static readonly JsonDocumentOptions Jsonc = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

        /// <summary>프로필 JSON 을 읽는다. 오류가 하나라도 있으면 ProfileException(오류를 '; ' 로 이음).</summary>
        public static ProductProfile Parse(string json)
        {
            var errs = new List<string>();
            var r = new ProductProfile();
            JsonDocument doc;
            try { doc = JsonDocument.Parse((json ?? "").TrimStart('﻿'), Jsonc); }
            catch (JsonException e) { throw new ProfileException("JSON 파싱 실패: " + e.Message); }
            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new ProfileException("최상위가 객체가 아님");
                var added = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                var codes = new List<AppCodeRule>();
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    switch (p.Name)
                    {
                        case "name":
                            if (p.Value.ValueKind == JsonValueKind.String) r.Name = p.Value.GetString();
                            else errs.Add("name: 문자열이어야 함");
                            break;
                        case "loader_marker_re":
                            if (p.Value.ValueKind != JsonValueKind.String) { errs.Add("loader_marker_re: 문자열이어야 함"); break; }
                            r.LoaderMarkerRe = p.Value.GetString();
                            if (r.LoaderMarkerRe.Length > 0) CheckRe(errs, "loader_marker_re", r.LoaderMarkerRe);
                            break;
                        case "boot_re":
                        case "uboot_prompt_re":
                            if (p.Value.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(p.Value.GetString())) { errs.Add(p.Name + ": 비어 있지 않은 문자열이어야 함"); break; }
                            CheckRe(errs, p.Name, p.Value.GetString());
                            if (p.Name == "boot_re") r.BootRe = p.Value.GetString(); else r.UbootPromptRe = p.Value.GetString();
                            break;
                        case "event_patterns":
                            if (p.Value.ValueKind != JsonValueKind.Object) { errs.Add("event_patterns: 객체여야 함"); break; }
                            foreach (var k in p.Value.EnumerateObject())
                            {
                                if (!EventKinds.IsError(k.Name)) { errs.Add("event_patterns: 알 수 없는 종류 '" + k.Name + "'(" + string.Join("|", EventKinds.Errors) + ")"); continue; }
                                if (k.Value.ValueKind != JsonValueKind.Array) { errs.Add("event_patterns." + k.Name + ": 배열이어야 함"); continue; }
                                foreach (var x in k.Value.EnumerateArray())
                                {
                                    if (x.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(x.GetString())) { errs.Add("event_patterns." + k.Name + ": 비어 있지 않은 문자열만"); continue; }
                                    if (!CheckRe(errs, "event_patterns." + k.Name, x.GetString())) continue;
                                    if (!added.TryGetValue(k.Name, out var l)) added[k.Name] = l = new List<string>();
                                    l.Add(x.GetString());
                                }
                            }
                            break;
                        case "app_codes":
                            if (p.Value.ValueKind != JsonValueKind.Array) { errs.Add("app_codes: 배열이어야 함"); break; }
                            int i = 0;
                            foreach (var x in p.Value.EnumerateArray())
                            {
                                string w = "app_codes[" + i++ + "]";
                                if (x.ValueKind != JsonValueKind.Object) { errs.Add(w + ": 객체여야 함"); continue; }
                                string re = null, code = null;
                                bool ok = true;
                                foreach (var f in x.EnumerateObject())
                                {
                                    if (f.Name == "re" && f.Value.ValueKind == JsonValueKind.String) re = f.Value.GetString();
                                    else if (f.Name == "code" && f.Value.ValueKind == JsonValueKind.String && f.Value.GetString().Length > 0) code = f.Value.GetString();
                                    else { errs.Add(w + ": 알 수 없거나 잘못된 키 '" + f.Name + "'(re: 문자열, code: 비어 있지 않은 문자열)"); ok = false; }
                                }
                                if (string.IsNullOrEmpty(re)) { errs.Add(w + ".re: 비어 있음"); continue; }
                                if (!CheckRe(errs, w + ".re", re) || !ok) continue;
                                codes.Add(new AppCodeRule { Text = re, Code = code, Re = new Regex(re, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)) });
                            }
                            break;
                        default:
                            errs.Add("알 수 없는 키 '" + p.Name + "'(name|loader_marker_re|boot_re|uboot_prompt_re|event_patterns|app_codes)");
                            break;
                    }
                }
                if (errs.Count > 0) throw new ProfileException(string.Join("; ", errs));
                var merged = new Dictionary<string, string[]>(StringComparer.Ordinal);
                foreach (var kv in EventKinds.GenericDefaults)
                    merged[kv.Key] = added.TryGetValue(kv.Key, out var l) ? kv.Value.Concat(l.Where(x => Array.IndexOf(kv.Value, x) < 0)).Distinct().ToArray() : kv.Value;
                r.EventDefaults = merged;
                r.AppCodes = codes;
                if (r.Name == null) r.Name = "";
            }
            return r;
        }

        static bool CheckRe(List<string> errs, string where, string re)
        {
            try { _ = new Regex(re, RegexOptions.CultureInvariant); return true; }
            catch (ArgumentException e) { errs.Add(where + ": 정규식 오류: " + e.Message); return false; }
        }

        /// <summary>앱 오류 줄의 코드(규칙 중 처음 맞은 것). 없으면 null.</summary>
        public string AppCode(string line)
        {
            foreach (var r in AppCodes)
            {
                try { var m = r.Re.Match(line ?? ""); if (m.Success) return r.Code ?? m.Value; }
                catch (RegexMatchTimeoutException) { }
            }
            return null;
        }

        /// <summary>hub status 의 profile 필드: 프로필이 없으면 null.</summary>
        public JObj StatusJson()
        {
            if (IsGeneric) return null;
            var o = new JObj().Set("name", Name ?? "").Set("path", Path);
            return Error == null ? o.Null("error") : o.Set("error", Error);
        }
    }
}
