// 제품 프로필(UartHub.profile.json, ProductProfile) 시나리오: 일반 기본값 · 파싱·검증 오류 · 찾는 순서(환경변수·exe 옆) ·
// 우선순위(일반 < 프로필 < config.json) · events.patterns off 가 프로필 패턴에도 쓰임 · app_codes · 로더 표시 없는 보드 · hub status 의 profile.
// 하네스는 시험용 가상 프로필(tests/profile.test.json)을 UART_HUB_PROFILE 로 준다 — 여기서는 Use() 로 잠시 바꿔 본다(시나리오는 차례로 돈다).
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace UartHub.Testing
{
    internal static class ProfileScenarios
    {
        const string Sample = @"{
  // 주석·끝 쉼표 허용(JSONC)
  ""name"": ""evb-x"",
  ""loader_marker_re"": ""XLD>T"",
  ""boot_re"": ""reboot: Restarting system|XLD>T|^\\[\\s+0\\.0\\d+\\]"",
  ""uboot_prompt_re"": ""^evx: "",
  ""event_patterns"": { ""app_error"": [""XFAULT\\((?!OK\\))"", ""X_ABORT reason=""], ""panic"": [""SIM PANIC X""], },
  ""app_codes"": [ { ""re"": ""XFAULT\\([^)\\s]*\\)"" }, { ""re"": ""(?i)x_wdt"", ""code"": ""WDT"" } ],
}";

        static string Expect(Func<ProductProfile> f)
        {
            try { f(); return null; }
            catch (ProductProfile.ProfileException e) { return e.Message; }
        }

        [Scenario("profile", "제품 프로필: 일반 기본값(로더 표시 없음·U-Boot '=> '·app_error 없음) · 파싱(JSONC)·검증 오류(알 수 없는 키·종류·정규식·app_codes) · 깨진 파일은 일반 기본값+error · " +
                             "찾는 순서(UART_HUB_PROFILE 경로·빈 값 = 없음) · 일반 < 프로필 < config.json · off 가 프로필 패턴에 쓰임 · app_codes(code·맞은 글자·순서) · 로더 표시 없는 PromptTracker · hub status profile",
                  Order = 6, TimeoutS = 30)]
        public static void Profile(ScenarioContext ctx)
        {
            // ---- 일반 기본값
            var g = ProductProfile.Generic;
            ctx.Check(g.IsGeneric && g.LoaderMarkerRe == "" && g.BootRe == ProductProfile.GenericBootRe && g.UbootPromptRe == "^=> "
                      && g.EventDefaults["app_error"].Length == 0 && g.EventDefaults["panic"].SequenceEqual(EventKinds.GenericDefaults["panic"]) && g.AppCodes.Count == 0
                      && g.StatusJson() == null && g.AppCode("XFAULT(E1)") == null,
                      "일반 기본값: 로더 표시 없음·boot_re 일반·U-Boot '=> '·app_error 0개·app_codes 0개·status null");
            using (ProductProfile.Use(null))
            {
                var c = new Config();
                ctx.Check(c.LoaderMarkerRe == "" && c.UbootPromptRe == "^=> " && c.BootRe == ProductProfile.GenericBootRe && c.Validate().Count == 0
                          && EventPatterns.For(c, "CABG").List.All(x => x.Kind != "app_error"),
                          "프로필 없음: Config 기본값이 일반 값, 검증 통과(빈 loader_marker_re 허용), app_error 패턴 0개");
                // 로더 표시 없는 보드: PromptTracker 가 만들어지고 커널 첫 줄(boot_re)로 BOOTING
                var t = new PromptTracker(c, "SIMG");
                t.OnRx(0, Encoding.ASCII.GetBytes("\r\nroot@SIMG:/$ "), 0);
                t.Advance(t.TOpenMs + 1);
                var before = t.State;
                t.OnRx(15, Encoding.ASCII.GetBytes("\r\n[    0.012345] Booting\r\n"), t.TOpenMs + 10);
                ctx.Check(before == PromptState.AtPrompt && t.State == PromptState.Booting, "로더 표시 없는 보드: 프롬프트 → 커널 첫 줄로 BOOTING (" + before + " → " + t.State + ")");
            }

            // ---- 파싱
            var p = ProductProfile.Parse(Sample);
            ctx.Check(p.Name == "evb-x" && p.LoaderMarkerRe == "XLD>T" && p.UbootPromptRe == "^evx: " && p.BootRe.Contains("XLD>T") && p.Error == null && !p.IsGeneric
                      && p.EventDefaults["app_error"].SequenceEqual(new[] { @"XFAULT\((?!OK\))", "X_ABORT reason=" })
                      && p.EventDefaults["panic"].SequenceEqual(EventKinds.GenericDefaults["panic"].Concat(new[] { "SIM PANIC X" }))
                      && p.EventDefaults["oops"].SequenceEqual(EventKinds.GenericDefaults["oops"]) && p.AppCodes.Count == 2,
                      "파싱: 값·event_patterns 는 일반 기본 뒤에 더함(panic 1개 추가, oops 그대로)·app_codes 2개");
            ctx.Check(p.AppCode("[ 1.0] XFAULT(E7) at boot") == "XFAULT(E7)" && p.AppCode("Panic: X_WDT fired") == "WDT" && p.AppCode("XFAULT(E1) x_wdt") == "XFAULT(E1)"
                      && p.AppCode("nothing") == null,
                      "app_codes: code 없으면 맞은 글자, 있으면 code(대소문자 무시 정규식), 앞 규칙 먼저, 안 맞으면 null");

            // ---- 검증 오류(모두 모아 한 번에)
            string e1 = Expect(() => ProductProfile.Parse("{\"nam\": 1, \"event_patterns\": {\"warn\": [\"x\"], \"oops\": [\"(\"]}, \"boot_re\": \"\", \"app_codes\": [{\"re\": \"a\", \"cod\": \"B\"}, {\"code\": \"C\"}]}"));
            ctx.Check(e1 != null && e1.Contains("알 수 없는 키 'nam'") && e1.Contains("알 수 없는 종류 'warn'") && e1.Contains("event_patterns.oops: 정규식 오류")
                      && e1.Contains("boot_re: 비어 있지 않은") && e1.Contains("app_codes[0]: 알 수 없거나 잘못된 키 'cod'") && e1.Contains("app_codes[1].re: 비어 있음"),
                      "검증: 알 수 없는 키·종류·정규식 오류·빈 boot_re·app_codes 키 오류를 한 번에: " + e1);
            ctx.Check(Expect(() => ProductProfile.Parse("[1]")) == "최상위가 객체가 아님" && (Expect(() => ProductProfile.Parse("{ x")) ?? "").StartsWith("JSON 파싱 실패", StringComparison.Ordinal),
                      "검증: 최상위 배열·깨진 JSON");

            // ---- 파일·찾는 순서
            string dir = Path.Combine(ctx.Dir, "profile");
            Directory.CreateDirectory(dir);
            string good = Path.Combine(dir, "good.json"), bad = Path.Combine(dir, "bad.json");
            File.WriteAllText(good, "﻿" + Sample, new UTF8Encoding(false));
            File.WriteAllText(bad, "{ \"loader_marker_re\": \"(\" }", new UTF8Encoding(false));
            var lb = ProductProfile.Load(bad);
            ctx.Check(lb.Error != null && lb.Error.Contains("loader_marker_re: 정규식 오류") && lb.Path == bad && lb.LoaderMarkerRe == "" && lb.EventDefaults["app_error"].Length == 0
                      && lb.StatusJson() != null && lb.StatusJson().ToString().Contains("\"error\":"),
                      "깨진 프로필: 일반 기본값 + error·path, status 에 error");
            var lg = ProductProfile.Load(good);
            ctx.Check(lg.Error == null && lg.Name == "evb-x" && lg.StatusJson().ToString() == "{\"name\":\"evb-x\",\"path\":" + Json.Str(good) + ",\"error\":null}",
                      "BOM 붙은 파일 읽기, status {name, path, error:null}: " + lg.StatusJson());
            string old = Environment.GetEnvironmentVariable(ProductProfile.EnvVar);
            try
            {
                Environment.SetEnvironmentVariable(ProductProfile.EnvVar, good);
                var d1 = ProductProfile.Discover();
                Environment.SetEnvironmentVariable(ProductProfile.EnvVar, Path.Combine(dir, "없음.json"));
                var d2 = ProductProfile.Discover();
                ctx.Check(d1.Name == "evb-x" && d1.Path == good && d2.Error != null && d2.Error.StartsWith("FileNotFoundException", StringComparison.Ordinal),
                          "찾는 순서: UART_HUB_PROFILE 경로를 읽음, 없는 파일이면 일반 기본값 + error");
            }
            finally { Environment.SetEnvironmentVariable(ProductProfile.EnvVar, old); }
            ctx.Check(ProductProfile.Current.Name == "sim-evb" || Environment.GetEnvironmentVariable(ProductProfile.EnvVar) == null,
                      "하네스의 시험 프로필(sim-evb)이 이 프로세스의 Current: " + ProductProfile.Current.Name);

            // ---- 우선순위와 off
            using (ProductProfile.Use(p))
            {
                var c = new Config();
                ctx.Check(c.LoaderMarkerRe == "XLD>T" && c.UbootPromptRe == "^evx: " && ProductProfile.Current == p, "프로필: Config 기본값이 프로필 값");
                var o = Config.Parse("{ \"loader_marker_re\": \"\", \"uboot_prompt_re\": \"^cfg> \", \"events\": { \"patterns\": { \"app_error\": { \"off\": [\"X_ABORT reason=\"], \"add\": [\"CFG ERR\"] } } } }");
                var ap = EventPatterns.For(o, "CABP").List.Where(x => x.Kind == "app_error").Select(x => x.Text).ToArray();
                ctx.Check(o.LoaderMarkerRe == "" && o.UbootPromptRe == "^cfg> " && o.BootRe == p.BootRe && o.Validate().Count == 0
                          && ap.SequenceEqual(new[] { @"XFAULT\((?!OK\))", "CFG ERR" }),
                          "config.json 이 이김(로더 표시 끔·U-Boot 프롬프트), 안 준 boot_re 는 프로필 값, off 가 프로필 패턴을 끔: " + string.Join(" · ", ap));
                var bad2 = Config.Parse("{ \"events\": { \"patterns\": { \"app_error\": { \"off\": [\"NOT A DEFAULT\"] } } } }").Validate();
                ctx.Check(bad2.Count == 1 && bad2[0].Contains("기본 패턴에 없음") && bad2[0].Contains("X_ABORT reason="), "off 검증: 기본(일반+프로필)에 없는 글자는 오류, 목록에 프로필 패턴: " + string.Join(" | ", bad2));
            }
            ctx.Check(ProductProfile.Current != p, "Use() 끝나면 원래 프로필로 돌아감");
        }
    }
}
