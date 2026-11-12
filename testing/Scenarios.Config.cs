// config.json 파서·검증(Config.Validate). 감독도 코어 기동 전에 같은 파서를 쓴다(§3.8).
using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;

namespace UartHub.Testing
{
    internal static class ConfigScenarios
    {
        [Scenario("config-validate", "config.json(jsonc) 파싱·검증: 계획 §3.9 예시 통과, 오타 키·잘못된 정규식·포트 범위·mode 거부. 코어가 새로 만든 토큰 ACL = 상속 없음·현재 사용자만", Order = 5, TimeoutS = 20)]
        public static void ConfigValidate(ScenarioContext ctx)
        {
            // 계획 §3.9 예시(주석 포함)를 그대로 + 부록 A 의 boot_re
            const string example = @"// config.json — 사용자가 편집
{ ""log_root"": ""D:\\uart_logs"", ""base_port"": 7001, ""control_port"": 7200,
  ""idle_s"": 3, ""default_timeout_s"": 30, ""ring_mb"": 32, ""rotate_mb"": 64, ""compress_after_days"": 7,
  ""prompt_re"": ""^root@([^:\\s]+):\\S*[#$] "", ""uboot_prompt_re"": ""^evb: "",
  ""boot_re"": ""reboot: Restarting system|LDR>T|^\\[\\s+0\\.0\\d+\\]"", ""vids"": [""VID_0403""],
  ""cables"": { ""FT1ABCDE"": { ""alias"": ""single"", ""mode"": ""auto"", ""baud"": 115200 },
              ""radar-cable-id"": { ""mode"": ""ignore"" } } }";
            var cfg = Config.Parse(example);
            ctx.Check(cfg.Validate().Count == 0, "계획 §3.9 예시 통과");
            var def = Config.Parse("{}");
            ctx.Check(def.ControlPort == 7200 && def.BasePort == 7001 && def.Validate().Count == 0, "키를 안 쓴 기본값: control_port 7200(11-03, 7000 은 다른 서비스와 충돌), base_port 7001 (" + def.ControlPort + ")");
            ctx.Check(cfg.LogRoot == @"D:\uart_logs" && cfg.BasePort == 7001 && cfg.CableFor("FT1ABCDE").Alias == "single"
                      && cfg.CableFor("radar-cable-id").Mode == "ignore" && cfg.CableFor("other").Baud == 115200, "값 반영(케이블 예외·기본값)");
            ctx.Check(cfg.RotateBytes == 64L * 1024 * 1024 && cfg.QueueBytes == 256L * 1024 * 1024 && cfg.EffectiveTOpenMs() == 52, "rotate 64MB, 큐 256MB, T_open=max(50,2×16+20)=52");

            string Errs(string json)
            {
                try { return string.Join(" | ", Config.Parse(json).Validate()); }
                catch (ConfigException e) { return string.Join(" | ", e.Errors); }
            }
            ctx.Check(Errs(@"{ ""rotate_mbb"": 1 }").Contains("알 수 없는 키 'rotate_mbb'"), "오타 키 거부");
            ctx.Check(Errs(@"{ ""boot_re"": ""(unclosed"" }").Contains("boot_re 정규식 오류"), "잘못된 정규식 거부");
            ctx.Check(Errs(@"{ ""base_port"": 80 }").Contains("base_port"), "base_port 범위 밖 거부");
            ctx.Check(Errs(@"{ ""control_port"": 7005 }").Contains("control_port"), "control_port 가 케이블 포트 범위와 겹치면 거부");
            ctx.Check(Errs(@"{ ""cables"": { ""X"": { ""mode"": ""grab"" } } }").Contains("mode"), "mode 는 auto|ignore");
            ctx.Check(Errs(@"{ ""tail_flush_ms"": 0 }").Contains("tail_flush_ms"), "시간 상수 0 거부");
            ctx.Check(Errs("{ broken").Contains("JSON 파싱 실패"), "깨진 JSON 거부");
            var rc = Config.Parse(@"{ ""cables"": { ""MCU"": { ""replug_clear"": false } } }");
            ctx.Check(!rc.CableFor("MCU").ReplugClear && rc.CableFor("other").ReplugClear && def.CableFor("x").ReplugClear, "cables.<id>.replug_clear: false 반영, 기본 true");
            ctx.Check(Errs(@"{ ""cables"": { ""X"": { ""replug_clear"": 1 } } }").Contains("replug_clear"), "replug_clear 는 true/false");

            string path = Path.Combine(ctx.Dir, "config.json");
            File.WriteAllText(path, example);
            ctx.Check(Config.ValidateFile(path).Count == 0 && Config.Load(path).BasePort == 7001, "ValidateFile/Load(감독용 진입점)");
            File.WriteAllText(path, @"{ ""queue_mb"": -1 }");
            ctx.Check(Config.ValidateFile(path).Any(x => x.Contains("queue_mb")), "ValidateFile 이 오류 목록 반환");

            // ---- 코어가 새로 만든 토큰: 상속 끊김 + 현재 사용자만(install 의 icacls /inheritance:r /grant:r <SID>:(F) 와 같은 결과)
            var hc = ctx.MakeConfig();
            hc.ControlPort = 17157;
            var hub = new Hub(hc, null, Array.Empty<IPortProvider>());
            ctx.Defer(hub.Stop);
            ctx.Check(!File.Exists(Path.Combine(hc.EffectiveHubDir, "token")), "전제: 토큰 없음");
            hub.Start();
            string tp = hub.Control.TokenPath;
            ctx.Check(File.Exists(tp) && File.ReadAllText(tp).Trim().Length == 32, "코어가 토큰 생성(32자)");
            var acl = new FileInfo(tp).GetAccessControl();
            var me = WindowsIdentity.GetCurrent().User;
            var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
            string show = string.Join(", ", rules.Select(x => x.IdentityReference + ":" + x.AccessControlType + ":" + x.FileSystemRights + (x.IsInherited ? "(상속)" : "")));
            ctx.Check(acl.AreAccessRulesProtected, "토큰 ACL 상속 끊김 (" + show + ")");
            ctx.Check(rules.Count > 0 && rules.All(x => !x.IsInherited && x.IdentityReference.Equals(me) && x.AccessControlType == AccessControlType.Allow),
                      "토큰 ACL 은 현재 사용자 " + me + " 만 (" + show + ")");
            hub.Stop();
            ctx.Check(File.ReadAllText(tp).Trim().Length == 32, "정지 뒤 토큰 유지");
        }
    }
}
