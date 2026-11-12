// US-112(계획 X5 부록 B — 필요할 때만 연결) 시나리오: connect-units · connect-holders · connect-ondemand.
//   connect-units    : 프로세스 없이(하네스 안) Config.WithOnDemand(키 보존·주석·CRLF·이미 있음·깨진 JSON)·on_demand 파싱·휴대용 기본 config·
//                      ComHolders 허용 목록·표시 이름·허용 목록 밖은 끝내지 않음(OS 호출 전에 거부).
//   connect-holders  : 실제 UartHub.exe 감독(--no-tray --sim, C-13 격리)에 cli connect·disconnect. 점유자는 <root>\fake_holders.json(가짜 — 실제 핸들 표·
//                      프로세스를 보지 않음), 끝내기는 가짜(fake_kill.log, 그 COM 이 풀림), 확인 창은 <root>\fake_ui.json 자동 응답(창 없음),
//                      터미널은 가짜 ttermpro.exe(실행하지 않음 — fake_launch.log). 빈 케이블 · 허용 목록 터미널 [끊고 연결] · [취소] · 확인 사이 점유자 바뀜 ·
//                      허용 목록 밖(안내 창 → 놓으면 이어짐) · 시간 초과 · 안내 창 취소 · 표 · disconnect · 다시 connect(같은 포트·열린 창 앞으로) · 102 · CLI 인자 · C-4.
//   connect-ondemand : on_demand Pending(COM 미개방·창·풍선 없음, 포트는 connect 때) · F1(연결됨 케이블 held_by_other → 풍선 1회, 끝내지 않음) ·
//                      보오(첫 연결에 보오·연결된 채 바꾸기 = 닫고 다시 엶 + .log '*** baud A -> B ***'·표준 아님 106·적지 않으면 그대로·config.json 저장) ·
//                      F2(rx_since_attach·hint no_output — connect·status·ls --detail·101, 자동 깨우기는 연결 뒤 ^E^U CR 로 남은 줄을 지움) · 휴대용 시작이 기존 config.json 에 on_demand 를 더함(다른 키·주석 그대로,
//                      두 번째 시작은 그대로) · connect-uart·disconnect-uart skill 배치·제거(Windows·가짜 WSL).
//   포트(제어/케이블): connect-holders 18500/18510~ · connect-ondemand 18520/18530~·(F1 18545) · 휴대용 18560/18570.
//   끝마다 실제 시스템 무변경(RealSystem) + TestIsolationViolation 0 + 감독 오류 0.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace UartHub.Testing
{
    /// <summary>connect 리그: IconRig(감독 --no-tray --sim --icons-dir, C-13 루트) + 가짜 TeraTerm + 점유자·확인 창 자동 응답 파일.</summary>
    internal sealed class ConnectRig
    {
        public IconRig G;
        public ScenarioContext Ctx;
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        public InstallRig R => G.R;
        public string Hub => G.Hub;

        public static ConnectRig Create(ScenarioContext ctx, string sub, int ctl, int basePort, string boards, string state, string cfgExtra)
        {
            var g = IconRig.Create(ctx, sub, ctl, basePort, boards, state, cfgExtra);
            string tt = Path.Combine(g.R.Root, "ProgramFilesX86", "teraterm", "ttermpro.exe");     // 가짜 TeraTerm — 시험 모드는 실행하지 않는다(fake_launch.log)
            Directory.CreateDirectory(Path.GetDirectoryName(tt));
            File.WriteAllBytes(tt, Encoding.ASCII.GetBytes("MZ fake teraterm"));
            return new ConnectRig { G = g, Ctx = ctx };
        }

        /// <summary>FakeBoard 사양 한 줄. printk=false 면 보드가 스스로 아무것도 보내지 않는다(F2).</summary>
        public static string Board(string cable, string com, string host, bool printk = true)
            => "{\"cable\":" + Json.Str(cable) + ",\"com\":" + Json.Str(com) + ",\"hostname\":" + Json.Str(host) + (printk ? ",\"printk_ms\":300" : "") + "}";

        /// <summary>감독: 확인 창 3초·안내 창 최대 4초(0.3초마다)·attach 10초·rx 0.5초(UART_HUB_TEST_CONNECT).</summary>
        public void StartSup()
            => Upd.Start(R, TestEnv.UartHubExe, new[] { "sup", "--no-tray", "--hub-dir", Hub, "--icons-dir", G.Desk, "--sim", G.Spec, "--sim-dll", TestEnv.TestsDll, "--tick-ms", "100" },
                         Upd.Env("UART_HUB_TEST_CONNECT", "3,4,0.3,10,0.5"));

        string F(string name) => Path.Combine(R.Root, name);
        void Atomic(string name, string text) { File.WriteAllText(F(name) + ".tmp", text, Utf8); File.Move(F(name) + ".tmp", F(name), true); }

        /// <summary>점유자 파일: (COM, pid, 이름, 부모, 시작 시각) 목록.</summary>
        public void Holders(params (string Com, int Pid, string Name, string Parent, long St)[] hs)
        {
            var o = new JObj();
            foreach (var g in hs.GroupBy(x => x.Com))
                o.Set(g.Key, g.Select(x => new JObj().Set("pid", (long)x.Pid).Set("name", x.Name).Set("parent", x.Parent).Set("start_ticks", x.St)));
            Atomic("fake_holders.json", o.ToString());
        }

        public string HoldersText => File.Exists(F("fake_holders.json")) ? File.ReadAllText(F("fake_holders.json")) : "";
        public void Ui(string json) => Atomic("fake_ui.json", json);
        public string Kills => File.Exists(F("fake_kill.log")) ? File.ReadAllText(F("fake_kill.log")) : "";
        public string Launches => File.Exists(F("fake_launch.log")) ? File.ReadAllText(F("fake_launch.log")) : "";
        public string Log() => G.Log();

        /// <summary>cli &lt;args&gt;(UART_HUB_DIR = 허브 폴더 — 자가 복구 없음). (rc, JSON, 출력 원문)</summary>
        public (int Code, JsonElement J, string Out) Cli(params string[] a)
        {
            var e = R.Run(TestEnv.UartHubExe, new[] { "cli" }.Concat(a).ToArray(), Upd.Env("UART_HUB_DIR", Hub), 200000);
            return (e.Code, Upd.Parse(e.Out), e.Out.Trim());
        }

        /// <summary>cli --parent-pipe &lt;args&gt; 를 표준 입력을 쥔 채 띄운다(래퍼와 같은 방식). 표준 입력을 닫으면 CLI 는 "호출 쪽이 끝남" 으로 103 — connect 중이면 &lt;id&gt;.cancel.</summary>
        public System.Diagnostics.Process StartCliHeld(params string[] a)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(TestEnv.UartHubExe)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8, WorkingDirectory = Path.GetDirectoryName(TestEnv.UartHubExe),
            };
            psi.ArgumentList.Add("cli"); psi.ArgumentList.Add("--parent-pipe");
            foreach (var x in a) psi.ArgumentList.Add(x);
            foreach (var k in new[] { "UART_HUB_CONTROL_PORT", "UART_HUB_TEST_FAIL", "UART_HUB_TEST_ANSWER", "UART_HUB_TEST_DELAY" }) psi.Environment.Remove(k);
            psi.Environment["UART_HUB_TEST_ROOT"] = R.Root;
            psi.Environment["UART_HUB_DIR"] = Hub;
            var p = System.Diagnostics.Process.Start(psi);
            p.StandardOutput.ReadToEndAsync();
            p.StandardError.ReadToEndAsync();
            Ctx.Defer(() => { try { if (!p.HasExited) { p.Kill(); p.WaitForExit(5000); } } catch (Exception) { } });
            return p;
        }

        public JsonElement Row(string com)
        {
            foreach (var c in G.Ctl_("ls", "--detail").GetProperty("cables").EnumerateArray())
                if (J.S(c, "com") == com) return c.Clone();
            return default;
        }

        public string St(string com) { var r = Row(com); return r.ValueKind == JsonValueKind.Object ? J.S(r, "status") : null; }

        public long Assign(string cable)
        {
            var s = HubFiles.ReadJson(Path.Combine(Hub, "state.json"));
            return s.HasValue && s.Value.TryGetProperty("assign", out var a) && a.TryGetProperty(cable, out var v) ? v.GetInt64() : 0;
        }

        public bool Released(string cable)
        {
            var s = HubFiles.ReadJson(Path.Combine(Hub, "state.json"));
            return s.HasValue && s.Value.TryGetProperty("released", out var a) && a.EnumerateArray().Any(x => x.GetString() == cable);
        }

        public int Count(string what) => InstallRig.Count(Log(), what);
    }

    public static class ConnectScenarios
    {
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        static string Dev => TestEnv.UartHubExe;
        static string Show(JsonElement j) => j.ValueKind == JsonValueKind.Undefined ? "(JSON 아님)" : J.Show(j);

        // ================================================================== connect-units
        [Scenario("connect-units", "X5 단위(프로세스 없음): Config.WithOnDemand — on_demand 없는 config 에 \"on_demand\": true 를 여는 중괄호 뒤에 끼움(다른 키·값·순서·주석·CRLF 그대로, 빼면 원문과 같은 바이트), 빈 객체·한 줄 객체, 이미 있음(true·false)·JSON 아님·배열·주석 안의 중괄호는 그대로(null). Config 파서 on_demand true/false·형식 오류. 휴대용 첫 config = {\"on_demand\": true}. ComHolders 허용 목록(MoTTY·ttermpro·putty·kitty, 대소문자 무시)·표시 이름(MobaXterm 세션 …)·허용 목록 밖(MobaXterm.exe·SecureCRT 등)은 OS 호출 전에 끝내기 거부. 표준 보오 11개·Config.WithCableBaud(그 케이블 보오만, 다른 케이블·키·순서 그대로, cables·항목 없으면 더함)",
                  Order = 1200, TimeoutS = 30)]
        public static void Units(ScenarioContext ctx)
        {
            // ---- WithOnDemand: 여러 줄·주석·CRLF
            string a = "// 사용자 설정\n{\n  \"base_port\": 7001, /* 블록 { 주석 */ \"cables\": {\"A1\": {\"alias\": \"eva\", \"mode\": \"auto\"}},\n  \"vids\": [\"VID_0403\"]\n}\n";
            string na = Config.WithOnDemand(a, out string wa);
            ctx.Check(na != null && wa == null && na == "// 사용자 설정\n{\n  \"on_demand\": true,\n  \"base_port\": 7001, /* 블록 { 주석 */ \"cables\": {\"A1\": {\"alias\": \"eva\", \"mode\": \"auto\"}},\n  \"vids\": [\"VID_0403\"]\n}\n",
                      "여러 줄 config: 여는 중괄호 다음 줄에 \"on_demand\": true, — 앞 주석·블록 주석·다른 키 그대로");
            ctx.Check(na.Replace("\n  \"on_demand\": true,", "") == a, "끼운 줄을 빼면 원문과 같은 바이트(C-12)");
            var ca = Config.Parse(na);
            ctx.Check(ca.OnDemand && ca.BasePort == 7001 && ca.Cables["A1"].Alias == "eva" && ca.Cables["A1"].ModeSet, "끼운 config 를 허브 파서가 읽음: on_demand=true·base_port·cables 그대로");
            string b = "{\r\n  \"control_port\": 7300\r\n}\r\n";
            string nb = Config.WithOnDemand(b, out _);
            ctx.Check(nb == "{\r\n  \"on_demand\": true,\r\n  \"control_port\": 7300\r\n}\r\n", "CRLF config: 같은 줄 끝(\\r\\n)으로 끼움");
            ctx.Check(Config.WithOnDemand("{}", out _) == "{\"on_demand\": true}" && Config.WithOnDemand("\uFEFF{\"ask_new_cables\": true}\n", out _) == "\uFEFF{ \"on_demand\": true,\"ask_new_cables\": true}\n",
                      "빈 객체 {} → {\"on_demand\": true} · 한 줄 객체(BOM 포함) → 같은 줄에 끼움");
            foreach (var (t, why) in new[] { ("{\"on_demand\": false}", "이미 있음(false)"), ("{\"x\":1, \"on_demand\": true}", "이미 있음(true)"), ("{ 깨짐", "JSON 아님"), ("[1,2]", "배열"), ("", "빈 파일") })
            {
                string n = Config.WithOnDemand(t, out string w);
                ctx.Check(n == null && !string.IsNullOrEmpty(w), why + " → 그대로(null, 이유: " + w + ")");
            }
            // ---- 파서·휴대용 기본값
            ctx.Check(Config.Parse("{\"on_demand\": true}").OnDemand && !Config.Parse("{\"on_demand\": false}").OnDemand && !Config.Parse("{}").OnDemand, "on_demand true/false/없음(기본 false = 지금 동작)");
            bool err = false;
            try { Config.Parse("{\"on_demand\": 1}"); } catch (ConfigException e) { err = e.Errors.Any(x => x.Contains("on_demand")); }
            ctx.Check(err, "on_demand 가 true/false 가 아니면 설정 오류");
            ctx.Check(Installer.PortableDefaultConfig == "{\"on_demand\": true, \"log_layout\": 3}\n" && Config.Parse(Installer.PortableDefaultConfig).OnDemand && Installer.DefaultConfig == "{\"ask_new_cables\": true}\n",
                      "휴대용 첫 config = {\"on_demand\": true, \"log_layout\": 3}(C.12) · 예전 설치기(install) 기본값은 그대로");
            // ---- 허용 목록·표시 이름
            ctx.Check(new[] { "MoTTY.exe", "motty.EXE", "ttermpro.exe", "putty.exe", "PUTTY.exe", "kitty.exe" }.All(ComHolders.IsAllowed)
                      && !new[] { "MobaXterm.exe", "MobaXterm_Personal.exe", "SecureCRT.exe", "UartHub.exe", "pwsh.exe", "explorer.exe", null, "" }.Any(ComHolders.IsAllowed),
                      "허용 목록 = MoTTY·ttermpro·putty·kitty(대소문자 무시), MobaXterm 본체·SecureCRT·UartHub·pwsh 등은 밖");
            ctx.Check(ComHolders.Label("MoTTY.exe") == "MobaXterm 세션" && ComHolders.Label("ttermpro.exe") == "TeraTerm" && ComHolders.Label("putty.exe") == "PuTTY"
                      && ComHolders.Label("MobaXterm.exe") == "MobaXterm" && ComHolders.Label(null) == "알 수 없는 프로그램", "표시 이름: MobaXterm 세션·TeraTerm·PuTTY·MobaXterm(본체)");
            // 허용 목록 밖은 OS 를 부르기 전에 거부한다(이 하네스는 시험 모드가 아니어도 안전 — 첫 줄에서 돌아온다)
            string w1 = ComHolders.Terminate(new ComHolder { Com = "COM1", Pid = 999999, Name = "MobaXterm.exe", StartTicks = 1 }, new int[0]);
            string w2 = ComHolders.Terminate(new ComHolder { Com = "COM1", Pid = 999999, Name = "SecureCRT.exe", StartTicks = 1 }, new int[0]);
            string w3 = ComHolders.Terminate(null, new int[0]);
            ctx.Check(w1 != null && w1.Contains("허용 목록 밖") && w2 != null && w2.Contains("허용 목록 밖") && w3 != null, "허용 목록 밖·점유자 없음 → 끝내지 않음(" + w1 + ")");

            // ---- 보오: 표준 값·config 저장(Config.WithCableBaud)
            ctx.Check(Config.StandardBauds.All(Config.IsStandardBaud) && Config.StandardBauds.Length == 11 && !new[] { 0, 300, 12345, 115201, 1000000 }.Any(Config.IsStandardBaud),
                      "표준 보오 11개(1200~921600)만, 그 밖(300·12345·115201 …)은 아님");
            string cb = "{\"base_port\": 7001, \"cables\": {\"A1\": {\"alias\": \"eva\", \"baud\": 115200}, \"B2\": {\"mode\": \"ignore\"}}, \"on_demand\": true}";
            string nb1 = Config.WithCableBaud(cb, "a1", 9600, out string wb1);
            var cb1 = nb1 == null ? null : Config.Parse(nb1);
            ctx.Check(cb1 != null && cb1.CableFor("A1").Baud == 9600 && cb1.CableFor("A1").Alias == "eva" && cb1.CableFor("B2").Mode == "ignore" && cb1.CableFor("B2").ModeSet && cb1.BasePort == 7001 && cb1.OnDemand
                      && nb1.IndexOf("\"base_port\"", StringComparison.Ordinal) < nb1.IndexOf("\"cables\"", StringComparison.Ordinal) && nb1.IndexOf("\"cables\"", StringComparison.Ordinal) < nb1.IndexOf("\"on_demand\"", StringComparison.Ordinal),
                      "있는 케이블(대소문자 무시) 보오만 9600 — alias·다른 케이블(mode ignore)·다른 키·키 순서 그대로: " + (nb1 ?? wb1));
            string nb2 = Config.WithCableBaud("{\"base_port\": 7001}", "C3", 57600, out _);
            string nb3 = Config.WithCableBaud(cb, "Z9", 1200, out _);
            string nb4 = Config.WithCableBaud(null, "Z9", 4800, out _);
            ctx.Check(nb2 != null && Config.Parse(nb2).CableFor("C3").Baud == 57600 && Config.Parse(nb2).BasePort == 7001 && nb3 != null && Config.Parse(nb3).CableFor("Z9").Baud == 1200
                      && Config.Parse(nb3).CableFor("A1").Baud == 115200 && nb4 != null && Config.Parse(nb4).CableFor("Z9").Baud == 4800,
                      "cables 없음 → cables 를 더함 · 없는 케이블 → 그 항목을 더함(다른 케이블 그대로) · 파일 없음 → {cables} 만");
            ctx.Check(Config.WithCableBaud("{ 깨짐", "A1", 9600, out string wb5) == null && wb5 != null && Config.WithCableBaud("[1]", "A1", 9600, out _) == null, "JSON 아님·배열 → 그대로(null)");
            // 글자로 고침: 주석·CRLF·BOM·키 순서 그대로
            string cc0 = "\uFEFF// 머리 주석\r\n{\r\n  \"base_port\": 7001, // 끝 주석\r\n  /* 블록 */ \"cables\": {\r\n    \"A1\": { \"alias\": \"eva\", \"baud\": 115200 } // 케이블\r\n  }\r\n}\r\n";
            string cc1 = Config.WithCableBaud(cc0, "A1", 921600, out string wc1);
            ctx.Check(cc1 == cc0.Replace("\"baud\": 115200", "\"baud\": 921600"), "있는 baud 값만 바꿈 — BOM·머리/끝/블록 주석·CRLF·공백 그대로: " + (cc1 ?? wc1));
            string cc2 = Config.WithCableBaud(cc0, "B2", 4800, out _);
            ctx.Check(cc2 == cc0.Replace("\"cables\": {\r\n", "\"cables\": {\"B2\": {\"baud\": 4800}, \r\n"), "없는 케이블 → cables 의 여는 중괄호 뒤에 그 항목만(나머지 바이트 그대로): " + cc2);
            string cc3 = Config.WithCableBaud("{\"cables\": {\"A1\": {\"alias\": \"x\"}}, \"vids\": []}", "a1", 2400, out _);
            string cc4 = Config.WithCableBaud("{\"cables\": {\"A1\": {}}}", "A1", 2400, out _);
            ctx.Check(cc3 == "{\"cables\": {\"A1\": {\"baud\": 2400, \"alias\": \"x\"}}, \"vids\": []}" && cc4 == "{\"cables\": {\"A1\": {\"baud\": 2400}}}",
                      "baud 없는 케이블 객체 → 그 객체 여는 중괄호 뒤(빈 객체는 쉼표 없이), 대소문자 무시: " + cc3 + " | " + cc4);
            string cc5 = Config.WithCableBaud("// c\n{\n  \"vids\": []\n}\n", "Z", 1200, out _);
            ctx.Check(cc5 == "// c\n{\n  \"cables\": {\"Z\": {\"baud\": 1200}},\n  \"vids\": []\n}\n", "cables 없음(여러 줄) → 다음 줄에 cables 한 줄, 주석 그대로: " + cc5);
            string wn6 = Config.WithCableBaud("{\"cables\": null}", "A1", 9600, out string wc6), wn7 = Config.WithCableBaud("{\"cables\": {\"A1\": 5}}", "A1", 9600, out string wc7);
            ctx.Check(wn6 == null && (wc6 ?? "").Contains("cables 가 객체가 아님") && wn7 == null && (wc7 ?? "").Contains("객체가 아님"),
                      "cables 가 null·그 케이블이 객체가 아님 → 거부(중복 키를 쓰지 않음): " + wc6 + " | " + wc7);
            // 점유자 찾기 범위: 후보(알려진 시리얼 도구)·건너뛰기(백신·보안·DRM·holder_scan_skip)
            ctx.Check(new[] { "MoTTY.exe", "MobaXterm_Personal_23.exe", "SecureCRT.exe", "python3.12.exe", "plink.exe", "Arduino IDE.exe" }.All(ComHolders.IsCandidate)
                      && !new[] { "weirdtool.exe", "chrome.exe", "explorer.exe" }.Any(ComHolders.IsCandidate), "후보 = 허용 목록 + MobaXterm*·SecureCRT·python*·plink·Arduino* …(weirdtool·chrome 은 아님)");
            ctx.Check(new[] { "V3LTray.exe", "AhnLabSafeTransaction.exe", "astxAgent.exe", "npkcmsvc.exe", "nProtectSvc.exe", "NxDrmAgent.exe", "fasoo_agent.exe", "f_ecm.exe", "DSCSLink.exe", "MarkAnyAgent.exe", "avp.exe", "MsMpEng.exe" }
                          .All(x => ComHolders.IsSkipped(x, null)) && ComHolders.IsSkipped("corp_agent.exe", new[] { "corp_*" }) && !ComHolders.IsSkipped("putty.exe", new[] { "corp_*" }),
                      "열지 않음 = V3·Safe Transaction(astx)·nProtect·NX·Fasoo(f_*)·DSCSLink·MarkAny·Kaspersky·Defender + config holder_scan_skip(corp_*)");
        }

        // ================================================================== connect-holders
        /// <summary>
        /// 화면 글 단위 검사(UI_COPY.md, 프로세스 없음): 조사 도우미(Josa — 한글 받침·숫자 읽는 소리·라틴 글자), 트레이 제목(Supervisor.TitleOf),
        /// connect 확인·안내 창 글(ConnectFlow.ConfirmText·GuideText), 휴대용 창 글(InstallCopy)에 긴 줄표(—)·띄운 조사(`COM8 을`)·메모체(~함·~됨) 없음.
        /// </summary>
        static void UiCopyUnits(ScenarioContext ctx)
        {
            string j = string.Join(" ", Josa.EulReul("COM8"), Josa.EulReul("COM3"), Josa.EulReul("COM10"), Josa.EulReul("COM502"), Josa.IGa("MobaXterm"), Josa.IGa("PuTTY"),
                                   Josa.IGa("TeraTerm 세션"), Josa.EuroRo("COM8"), Josa.EuroRo("COM3"), Josa.EuroRo("허브"), Josa.EulReul("EVB-A1 (7001)"), Josa.EulReul("WSL"), Josa.IGa("Claude"), Josa.EulReul("COM0"));
            ctx.Check(j == "COM8을 COM3을 COM10을 COM502를 MobaXterm이 PuTTY가 TeraTerm 세션이 COM8로 COM3으로 허브로 EVB-A1 (7001)을 WSL을 Claude가 COM0을",
                      "Josa: 받침·숫자(팔 ㄹ·삼·십·이·영)·라틴(대문자 글자 이름, 소문자 끝소리)·닫는 괄호 건너뜀 → " + j);
            var titles = new[]
            {
                Supervisor.TitleOf("running", false, false, 3, 0, 0), Supervisor.TitleOf("running", false, false, 0, 0, 0), Supervisor.TitleOf("stopped", false, false, 0, 0, 0),
                Supervisor.TitleOf("backoff", false, false, 0, 0, 0), Supervisor.TitleOf("backoff", true, false, 0, 0, 0), Supervisor.TitleOf("running", false, true, 2, 0, 0),
                Supervisor.TitleOf("running", false, false, 1, 2, 1),
            };
            ctx.Check(titles.SequenceEqual(new[]
            {
                "UART 허브 · 연결 3개", "UART 허브 · 연결 없음", "UART 허브 · 정지됨", "UART 허브 · 다시 켜는 중", "UART 허브 · 계속 꺼짐, 로그 확인 필요",
                "UART 허브 · 설정 파일 오류 (마지막 정상 설정으로 동작 중)", "UART 허브 · 연결 1개 · 확인 필요 2 · 아이콘 이름 충돌 1",
            }) && titles.All(t => t.Length <= 63), "트레이 제목(UI_COPY.md 툴팁·상태 줄, 63자 이내): " + string.Join(" | ", titles));
            var ct = ConnectFlow.ConfirmText("COM8", new ComHolder { Name = "MoTTY.exe", Pid = 88688 });
            var tt = ConnectFlow.ConfirmText("COM3", new ComHolder { Name = "ttermpro.exe", Pid = 77 });
            var gt = ConnectFlow.GuideText("COM8", new ComHolder { Name = "MobaXterm.exe", Pid = 40112 });
            var gn = ConnectFlow.GuideText("COM8", null);
            ctx.Check(ct.Title == "UART 허브 · COM8 연결" && ct.Head == "COM8 사용 중: MobaXterm 세션" && ct.Note == "motty.exe · pid 88688" && ct.Ok == "끊고 연결" && !ct.OkIsDefault
                      && ct.Body == "이 세션을 끊고, 허브가 COM8을 열어 터미널과 Claude가 같이 쓰게 할까요? MobaXterm의 다른 탭은 그대로입니다."
                      && tt.Body.StartsWith("TeraTerm 창을 닫고, 허브가 COM3을 열어", StringComparison.Ordinal) && !tt.Body.Contains("MobaXterm")
                      && gt.Head == "COM8 사용 중: MobaXterm" && gt.Body == "그 프로그램에서 COM8 연결을 끊어 주세요. 끊으면 바로 이어서 연결합니다." && gt.Ok == null
                      && gn.Head == "COM8 사용 중: 다른 프로그램" && gn.Note == null && gn.Body.Contains("관리자 권한") && ConnectFlow.Clock(118000) == "1:58" && ConnectFlow.Clock(120000) == "2:00",
                      "connect 확인 창(Cancel 기본·'motty.exe · pid 88688')·TeraTerm 은 '창을 닫고'·안내 창·점유자 모름·남은 시간 m:ss");
            var copies = new[]
            {
                InstallCopy.FirstRun(@"D:\uart_logs", InstallCopy.SkillsPlaces(true, new[] { "Ubuntu-22.04" }), @"C:\T\UartHub.exe", @"D:\uart_logs\hub", @"C:\x\install.json"),
                InstallCopy.Ready(@"D:\uart_logs", "Windows 넣음"), InstallCopy.Fail("건강 확인 실패", @"D:\uart_logs\hub\install.log", "tech"),
                InstallCopy.Replace(@"C:\A\UartHub.exe", "20261106030213432", "20261106101724547", @"C:\B\UartHub.exe"), InstallCopy.Replaced("1", "x", null),
                InstallCopy.Uninstall("x", "y", "z", "w"), InstallCopy.Uninstalled("x", "y"), ct, tt, gt, gn,
            };
            var bad = copies.Select(t => t.Flat()).Where(t => t.Contains('—') || Regex.IsMatch(t, @"[A-Za-z0-9)] (을|를|이|가|은|는|으로|로)(\s|[.,]|$)") || Regex.IsMatch(t, @"(함|됨)[.)]?(\s|$)")).ToList();
            ctx.Check(bad.Count == 0 && copies[0].Bullets.Contains("Claude 연결 정보를 넣을 곳: Windows, WSL Ubuntu-22.04") && copies[0].Ok == "시작" && copies[0].OkIsDefault
                      && copies[0].Details.Any(x => x.StartsWith("자동 실행: Win+R → shell:startup", StringComparison.Ordinal)) && copies[2].LogPath != null && copies[2].Error && copies[2].Tech == "tech"
                      && copies[3].Head == "새 버전으로 바꿀까요?" && copies[3].Ok == "바꾸기" && !copies[3].OkIsDefault && copies[3].Body.Contains("허브(20261106030213432)를 끄고")
                      && copies[5].Ok == "제거" && !copies[5].OkIsDefault && InstallCopy.SkillsPlaces(false, new string[0]).StartsWith("찾지 못했습니다", StringComparison.Ordinal),
                      "휴대용 창 글(InstallCopy): 긴 줄표·띄운 조사·메모체 없음, 처음 시작 [시작] 기본·바꾸기/제거는 취소 기본, 오류 창 [로그 폴더 열기]·setup 용 기술 문구 따로" + (bad.Count > 0 ? " — " + string.Join(" | ", bad) : ""));
        }

        [Scenario("connect-holders", "X5 connect·disconnect(실제 exe 감독, C-13 격리, 가짜 점유자·가짜 끝내기·창 자동 응답·가짜 터미널): ① 빈 케이블 → 포트 배정·attached·터미널 launched·holder_closed null ② MoTTY(허용 목록) 점유 → 확인 창 [끊고 연결] → 그 세션만 끝냄(가짜)·COM 풀림·attached·holder_closed ③ [취소] → 106 cancelled·상태·점유자 그대로 ④ 확인 창 사이 점유자 pid 바뀜 → 끝내지 않고 106 ⑤ 허용 목록 밖(MobaXterm 본체) → 끝내지 않고 안내 창·놓으면 이어짐 ⑥ 놓지 않음 → 100 timeout·그대로 ⑦ 안내 창 [취소] → 106 ⑧ 인자 없는 connect 표 ⑨ disconnect → released(포트 유지)·두 번째는 already ⑩ 다시 connect → include·같은 포트·열린 TeraTerm 앞으로(focused) ⑪ 없는 COM 102(꽂힌 목록) ⑫ CLI 인자 106·cable 은 계속 106(C-4). 실제 프로세스·창 0, 실제 시스템 무변경",
                  Order = 1201, TimeoutS = 240)]
        public static void Holders(ScenarioContext ctx)
        {
            var real = RealSystem.Capture(ctx);
            UiCopyUnits(ctx);
            string boards = string.Join(",", new[]
            {
                ConnectRig.Board("SIMCNA", "COM501", "CNA"), ConnectRig.Board("SIMCNB", "COM502", "CNB"), ConnectRig.Board("SIMCNC", "COM503", "CNC"),
                ConnectRig.Board("SIMCND", "COM504", "CND"), ConnectRig.Board("SIMCNE", "COM505", "CNE"),
            });
            var c = ConnectRig.Create(ctx, "main", 18500, 18510, boards, null, ", \"on_demand\": true");
            c.Holders(("COM502", 50201, "MoTTY.exe", "MobaXterm.exe", 111), ("COM503", 50301, "MoTTY.exe", "MobaXterm.exe", 222),
                      ("COM504", 50401, "MobaXterm.exe", "explorer.exe", 333), ("COM505", 50501, "SecureCRT.exe", null, 444));
            c.StartSup();
            c.G.WaitCore("감독");
            ctx.WaitUntil(() => new[] { "COM501", "COM502", "COM503", "COM504", "COM505" }.All(x => c.St(x) == "pending"), 10000, "on_demand: 다섯 케이블 모두 pending(COM 미개방)");
            ctx.Check(new[] { "COM501", "COM503" }.All(x => J.N(c.Row(x), "port") == 0) && c.Assign("SIMCNA") == 0, "pending = 포트 0·state.json 배정 없음(포트는 connect 때)");

            // ① 빈 케이블
            var r1 = c.Cli("connect", "COM501", "--json");
            ctx.Check(r1.Code == 0 && J.S(r1.J, "status") == "ok" && J.N(r1.J, "port") == 18510 && J.IsNull(r1.J, "holder_closed") && J.S(r1.J, "terminal") == "launched"
                      && J.S(r1.J, "com") == "COM501" && J.Has(r1.J, "rx_bytes") && J.S(r1.J, "cable") == "SIMCNA",
                      "① 빈 COM501 → ok·포트 18510(connect 때 배정)·holder_closed null·terminal launched·rx_bytes: " + Show(r1.J));
            ctx.Check(c.St("COM501") == "attached" && c.Assign("SIMCNA") == 18510 && c.Launches.Contains("ttermpro.exe | 127.0.0.1:18510 /nossh /T=1 /W=\"COM501 · 18510\""),
                      "① attached·state.json 배정 18510·TeraTerm(가짜) 127.0.0.1:18510 실행 기록");

            // ② 허용 목록 MoTTY → [끊고 연결]
            int conf0 = c.Count("[ui:fake] 확인 창");
            var r2 = c.Cli("connect", "COM502", "--json");
            var hc = r2.J.ValueKind == JsonValueKind.Object && r2.J.TryGetProperty("holder_closed", out var h2) ? h2 : default;
            ctx.Check(r2.Code == 0 && J.S(r2.J, "status") == "ok" && hc.ValueKind == JsonValueKind.Object && J.S(hc, "name") == "MoTTY.exe" && J.N(hc, "pid") == 50201 && J.S(hc, "label") == "MobaXterm 세션"
                      && J.S(r2.J, "terminal") == "launched", "② MoTTY(50201) 점유 → ok·holder_closed {MoTTY.exe, 50201, MobaXterm 세션}: " + Show(r2.J));
            ctx.Check(c.Count("[ui:fake] 확인 창") == conf0 + 1 && c.Log().Contains("[ui:fake] 확인 창: UART 허브 · COM502 연결 — COM502 사용 중: MobaXterm 세션 / 이 세션을 끊고, 허브가 COM502를 열어 터미널과 Claude가 같이 쓰게 할까요? MobaXterm의 다른 탭은 그대로입니다. / motty.exe · pid 50201")
                      && c.Log().Contains("[끊고 연결][취소] 기본=취소 (자동 응답=끊고 연결)"),
                      "② 확인 창 1회(UI_COPY.md): 제목 'UART 허브 · COM502 연결' · 'COM502 사용 중: MobaXterm 세션' · 'COM502를 열어' · 작은 줄 'motty.exe · pid 50201' · [끊고 연결][취소] 기본=취소");
            ctx.Check(c.Kills.Trim() == "50201 MoTTY.exe COM502" && !c.HoldersText.Contains("50201") && c.St("COM502") == "attached",
                      "② 그 세션만 끝냄(가짜 fake_kill.log 1줄)·COM502 풀림 → attached (kills=" + c.Kills.Trim() + ")");

            // ③ [취소] → 아무것도 바꾸지 않음
            c.Ui("{\"confirm\":\"cancel\"}");
            string h3 = c.HoldersText;
            var r3 = c.Cli("connect", "COM503", "--json");
            ctx.Check(r3.Code == 106 && J.S(r3.J, "status") == "cancelled" && r3.J.TryGetProperty("holder", out var hh3) && J.N(hh3, "pid") == 50301, "③ [취소] → 106 cancelled·holder pid 50301: " + Show(r3.J));
            ctx.Check(c.St("COM503") == "pending" && J.N(c.Row("COM503"), "port") == 0 && c.Assign("SIMCNC") == 0 && c.HoldersText == h3 && !c.Kills.Contains("5030"),
                      "③ 상태 그대로(pending·포트 0·배정 없음)·점유자 그대로·끝내기 0");

            // ④ 확인 창이 떠 있는 사이 점유자가 바뀜(pid 50301 → 50302) → 끝내지 않음
            c.Ui("{\"confirm\":\"yes\",\"confirm_delay_ms\":1500}");
            int conf1 = c.Count("[ui:fake] 확인 창");
            var t4 = Task.Run(() => c.Cli("connect", "COM503", "--json"));
            ctx.WaitUntil(() => c.Count("[ui:fake] 확인 창") > conf1, 15000, "④ 확인 창 떠 있음(1.5초 뒤 [끊고 연결])");
            c.Holders(("COM503", 50302, "MoTTY.exe", "MobaXterm.exe", 555), ("COM505", 50501, "SecureCRT.exe", null, 444), ("COM504", 50401, "MobaXterm.exe", "explorer.exe", 333));
            var r4 = t4.Result;
            ctx.Check(r4.Code == 106 && J.S(r4.J, "status") == "rejected" && (J.S(r4.J, "error") ?? "").Contains("바뀜") && r4.J.TryGetProperty("holder", out var hh4) && J.N(hh4, "pid") == 50302,
                      "④ 다시 확인 → pid·시작 시각 다름 → 끝내지 않고 106(새 점유자 50302 알림): " + Show(r4.J));
            ctx.Check(!c.Kills.Contains("5030") && c.HoldersText.Contains("50302") && c.St("COM503") == "pending" && c.Assign("SIMCNC") == 0, "④ 끝내기 0·상태 그대로(pending·배정 없음)");

            // ④-2 요청자가 떠남(래퍼가 끝남 = --parent-pipe 표준 입력 EOF) → CLI 가 <id>.cancel → 감독이 확인 창을 닫고 취소, 응답은 쓰지 않음
            c.Ui("{\"confirm\":\"none\"}");
            int conf2 = c.Count("[ui:fake] 확인 창:");
            var held = c.StartCliHeld("connect", "COM503", "--json");
            ctx.WaitUntil(() => c.Count("[ui:fake] 확인 창:") > conf2, 15000, "④-2 확인 창 떠 있음(자동 응답 없음 — 시험 3초 제한)");
            held.StandardInput.Close();
            ctx.Check(held.WaitForExit(5000) && held.ExitCode == 103, "④-2 호출 쪽이 끝남(표준 입력 EOF) → CLI 103");
            ctx.WaitUntil(() => c.Log().Contains("[ui:fake] 확인 창 닫음(요청자가 떠남)") && c.Log().Contains("요청자가 떠남 — 응답은 쓰지 않음"), 5000,
                          "④-2 감독: 확인 창을 3초 제한 전에 닫음 + 응답 쓰지 않음(<id>.cancel)");
            ctx.Check(!c.Log().Contains("[ui:fake] 확인 창 시간 초과") && !c.Kills.Contains("5030") && c.St("COM503") == "pending" && c.Assign("SIMCNC") == 0
                      && !Directory.GetFiles(HubFiles.ReqDir(c.Hub), "*.cancel").Any(), "④-2 끝내기 0·상태 그대로·취소 표시 정리");

            // ⑤ 허용 목록 밖(MobaXterm 본체) → 끝내지 않고 안내 창, 놓으면 이어짐
            c.Ui("{\"guide\":\"wait\"}");
            int guide0 = c.Count("[ui:fake] 안내 창:");
            var t5 = Task.Run(() => c.Cli("connect", "COM504", "--json"));
            ctx.WaitUntil(() => c.Count("[ui:fake] 안내 창:") > guide0, 15000, "⑤ 안내 창(허용 목록 밖 — 끝내지 않음)");
            ctx.Check(c.Log().Contains("[ui:fake] 안내 창: UART 허브 · COM504 연결 — COM504 사용 중: MobaXterm / 그 프로그램에서 COM504 연결을 끊어 주세요. 끊으면 바로 이어서 연결합니다. / mobaxterm.exe · pid 50401 / 남은 시간 ")
                      && c.Log().Contains("[취소] (자동 응답="), "⑤ 안내 문구(UI_COPY.md) 'COM504 사용 중: MobaXterm' · '그 프로그램에서 COM504 연결을 끊어 주세요. 끊으면 바로 이어서 연결합니다.' · 'mobaxterm.exe · pid 50401' · 남은 시간 · [취소]");
            Thread.Sleep(800);
            c.Holders(("COM503", 50302, "MoTTY.exe", "MobaXterm.exe", 555), ("COM505", 50501, "SecureCRT.exe", null, 444));   // 사용자가 MobaXterm 에서 COM504 를 끊음
            var r5 = t5.Result;
            ctx.Check(r5.Code == 0 && J.S(r5.J, "status") == "ok" && J.IsNull(r5.J, "holder_closed") && c.St("COM504") == "attached" && !c.Kills.Contains("50401"),
                      "⑤ 놓으면 이어짐 → ok·holder_closed null·attached·끝내기 0: " + Show(r5.J));

            // ⑥ 놓지 않음 → 시간 초과(시험 4초), 그대로
            var r6 = c.Cli("connect", "COM505", "--json");
            ctx.Check(r6.Code == 100 && J.S(r6.J, "status") == "timeout" && c.St("COM505") == "pending" && c.Assign("SIMCNE") == 0 && !c.Kills.Contains("50501"),
                      "⑥ SecureCRT 가 놓지 않음 → 100 timeout·pending·배정 없음·끝내기 0: " + Show(r6.J));
            ctx.Check(c.Log().Contains("안내 창 시간 초과(4초)"), "⑥ 안내 창 시간 초과(시험 4초) 기록");

            // ⑦ 안내 창 [취소]
            c.Ui("{\"guide\":\"cancel\",\"guide_cancel_ms\":300}");
            var r7 = c.Cli("connect", "COM505", "--json");
            ctx.Check(r7.Code == 106 && J.S(r7.J, "status") == "cancelled" && c.St("COM505") == "pending" && !c.Kills.Contains("50501"), "⑦ 안내 창 [취소] → 106 cancelled·그대로: " + Show(r7.J));

            // ⑧ 인자 없는 connect = 표
            var r8 = c.Cli("connect", "--json");
            var tab = r8.J.ValueKind == JsonValueKind.Object && r8.J.TryGetProperty("cables", out var cs) ? cs.EnumerateArray().ToDictionary(x => J.S(x, "com"), x => x.Clone()) : new Dictionary<string, JsonElement>();
            ctx.Check(r8.Code == 0 && tab.Count == 5 && J.S(tab["COM501"], "state") == "connected" && J.S(tab["COM502"], "state") == "connected" && J.S(tab["COM504"], "state") == "connected"
                      && J.S(tab["COM503"], "state") == "in_use" && (J.S(tab["COM503"], "text") ?? "").Contains("MobaXterm 세션(pid 50302) 사용 중")
                      && J.S(tab["COM505"], "state") == "in_use" && (J.S(tab["COM505"], "text") ?? "").Contains("SecureCRT"),
                      "⑧ 표: 501·502·504 연결됨, 503 'MobaXterm 세션(pid 50302) 사용 중', 505 SecureCRT 사용 중: " + Show(r8.J));
            var r8t = c.R.Run(Dev, new[] { "cli", "connect" }, Upd.Env("UART_HUB_DIR", c.Hub));
            ctx.Check(r8t.Code == 0 && r8t.Out.Contains("COM501  연결됨 18510") && r8t.Out.Contains("COM503  MobaXterm 세션(pid 50302) 사용 중"), "⑧ 비JSON 표 줄 'COM501  연결됨 18510 …'");

            // ⑨ disconnect
            var r9 = c.Cli("disconnect", "COM501", "--json");
            ctx.Check(r9.Code == 0 && J.S(r9.J, "cable_status") == "released" && J.N(r9.J, "port") == 18510, "⑨ disconnect COM501 → ok·released·포트 18510: " + Show(r9.J));
            ctx.WaitUntil(() => c.St("COM501") == "released", 5000, "⑨ ls COM501 released(COM 닫음)");
            ctx.Check(c.Released("SIMCNA") && c.Assign("SIMCNA") == 18510, "⑨ state.json released 에 SIMCNA·배정 18510 유지");
            var r9b = c.Cli("disconnect", "COM501", "--json");
            ctx.Check(r9b.Code == 0 && Upd.True(r9b.J, "already"), "⑨ 두 번째 disconnect → ok already");

            // ⑩ 다시 connect → include, 같은 포트, 열린 TeraTerm 은 앞으로
            File.WriteAllText(Path.Combine(c.R.Root, "fake_procs.json"), "[{\"pid\":4242,\"name\":\"ttermpro.exe\",\"cmd\":\"ttermpro.exe 127.0.0.1:18510 /nossh /T=1\",\"title\":\"COM501 (18510)\"}]", Utf8);
            var r10 = c.Cli("connect", "COM501", "--json");
            ctx.Check(r10.Code == 0 && J.N(r10.J, "port") == 18510 && J.S(r10.J, "terminal") == "focused" && !c.Released("SIMCNA") && c.St("COM501") == "attached",
                      "⑩ 다시 connect → include·같은 포트 18510·열린 TeraTerm 앞으로(focused): " + Show(r10.J));
            var r10b = c.Cli("connect", "COM501", "--json");
            ctx.Check(r10b.Code == 0 && Upd.True(r10b.J, "already") && J.S(r10b.J, "terminal") == "focused", "⑩ 이미 연결된 COM 다시 connect → already·focused");

            // ⑪ 없는 COM
            var r11 = c.Cli("connect", "COM599", "--json");
            var coms = r11.J.ValueKind == JsonValueKind.Object && r11.J.TryGetProperty("coms", out var cl) ? cl.EnumerateArray().Select(x => J.S(x, "com")).ToList() : new List<string>();
            ctx.Check(r11.Code == 102 && J.S(r11.J, "status") == "no_cable" && coms.Count == 5 && coms.Contains("COM503"), "⑪ 없는 COM599 → 102 no_cable + 꽂힌 COM 목록(5): " + Show(r11.J));

            // ⑫ CLI 인자·C-4
            var b1 = c.Cli("connect", "foo", "--json");
            var b2 = c.Cli("disconnect", "--json");
            var b3 = c.Cli("connect", "COM1", "COM2", "--json");
            var b4 = c.Cli("cable", "decide", "SIMCNE", "board", "--json");
            ctx.Check(b1.Code == 106 && b2.Code == 106 && b3.Code == 106 && b4.Code == 106 && (J.S(b4.J, "error") ?? "").Contains("트레이에서만") && c.St("COM505") == "pending",
                      "⑫ connect foo·disconnect(인자 없음)·COM 두 개 → 106, cable 은 계속 CLI 에서 106(C-4)");
            string cfgBefore = File.ReadAllText(Path.Combine(c.Hub, "config.json"));
            var x1 = c.Cli("--json", "cable", "baud", "SIMCNE", "9600");
            var x2 = c.Cli("--", "cable", "exclude", "SIMCNA");
            var x3 = c.Cli("--timeout", "5", "cable", "include", "SIMCNA", "--json");
            var x4 = c.Cli("--json", "--", "cable", "decide", "SIMCNE", "board");
            ctx.Check(x1.Code == 106 && (J.S(x1.J, "error") ?? "").Contains("트레이에서만") && x2.Code == 106 && x3.Code == 106 && (J.S(x3.J, "error") ?? "").Contains("트레이에서만")
                      && x4.Code == 106 && (J.S(x4.J, "error") ?? "").Contains("트레이에서만"),
                      "⑫ C-4 우회 막힘: '--json cable baud' · '-- cable exclude' · '--timeout 5 cable include' · '--json -- cable decide' → 모두 CLI 에서 106(코어와 같은 해석)");
            ctx.Check(c.St("COM501") == "attached" && c.St("COM505") == "pending" && File.ReadAllText(Path.Combine(c.Hub, "config.json")) == cfgBefore && !c.Log().Contains("cable baud SIMCNE"),
                      "⑫ 코어에 닿지 않음: COM501 attached·COM505 pending·config.json 그대로");
            ctx.Check(c.Log().Contains("[sup] connect COM502: MobaXterm 세션(pid 50201, 부모 MobaXterm.exe) 끝냄(TerminateProcess)") && c.Count("끝냄(TerminateProcess)") == 1, "hub.log: 끝냄은 ② 한 번뿐");
            c.G.Violations();
            c.R.CheckNoProcs("정리");
            real.Verify(ctx, c.R);
        }

        // ================================================================== connect-ondemand
        [Scenario("connect-ondemand", "X5 on_demand·F1·F2·휴대용 config·skills: on_demand 의 처음 보는 케이블 = pending(포트 0·COM 미개방·state 배정 없음, 감독 '확인 필요' 0·풍선 0·sup.json pending []·on_demand true, status 102 '/connect-uart COMx'), connect 때 포트 배정(base 부터). F1: 연결됨(배정 있음) 케이블을 MobaXterm 이 쥠 → held_by_other → 풍선 'COMx 을 MobaXterm(pid) 이 쓰고 있어 열지 못했습니다 — /connect-uart COMx 로 다시 연결' 1회·끝내지 않음, 놓으면 붙음. F2: 0바이트 → connect rx_bytes 0·hint no_output·message, status·ls --detail rx_since_attach 0·hint_text, 101(run) 에도, ls(기본) 는 Phase A 그대로, 바이트가 오면 hint 사라짐. 보오: 첫 연결 COM 9600(config.json cables.<id>.baud 저장·다른 키 그대로·.log '*** baud 115200 -> 9600 ***'), 연결된 채 57600(닫고 다시 엶·같은 포트), 12345 → 106 + 허용 목록·무변경, 보오 없이 connect·끊고 다시 connect 는 그대로, 표에 보오. 휴대용 첫 실행(기존 config.json) → on_demand 를 더함(다른 키·주석 그대로)·감독 on_demand·두 번째 시작은 그대로. connect-uart·disconnect-uart skill(disable-model-invocation: true) Windows·가짜 WSL 배치·제거, uart SKILL.md 규칙(/connect-uart·스스로 connect 금지·2단계 wait). 실제 시스템 무변경",
                  Order = 1202, TimeoutS = 300)]
        public static void OnDemand(ScenarioContext ctx)
        {
            var real = RealSystem.Capture(ctx);
            string boards = ConnectRig.Board("SIMCOA", "COM521", "COA", printk: false) + "," + ConnectRig.Board("SIMCOB", "COM522", "COB")
                          + "," + ConnectRig.Board("SIMCOC", "COM523", "COC", printk: false) + "," + ConnectRig.Board("SIMCOD", "COM524", "COD")
                          + "," + ConnectRig.Board("SIMCOE", "COM525", "COE") + "," + ConnectRig.Board("SIMCOF", "COM526", "COF");
            var c = ConnectRig.Create(ctx, "main", 18520, 18530, boards, "{\"assign\":{\"SIMCOB\":18545,\"SIMCOD\":18546,\"SIMCOE\":18547},\"released\":[],\"boards\":{}}",
                                      ", \"on_demand\": true, \"holder_scan_skip\": [\"skipme*\"]");
            var rest = new[] { ("COM524", 52401, "MoTTY.exe", "MobaXterm.exe", 901L), ("COM525", 52501, "weirdtool.exe", (string)null, 902L), ("COM526", 52601, "skipme.exe", (string)null, 903L) };
            c.Holders(new[] { ("COM522", 52201, "MobaXterm.exe", "explorer.exe", 777L) }.Concat(rest).ToArray());
            c.StartSup();
            c.G.WaitCore("감독");

            // ---- on_demand pending
            ctx.WaitUntil(() => c.St("COM521") == "pending" && c.St("COM523") == "pending", 10000, "on_demand: 처음 보는 COM521·COM523 pending");
            ctx.Check(J.N(c.Row("COM521"), "port") == 0 && c.Assign("SIMCOA") == 0 && c.Assign("SIMCOC") == 0, "pending = 포트 0·state.json 배정 없음");
            ctx.WaitUntil(() => { var s = c.G.Sup(); return s.ValueKind == JsonValueKind.Object && J.B(s, "on_demand") == true; }, 5000, "sup.json on_demand true");
            var sj = c.G.Sup();
            ctx.Check(sj.TryGetProperty("pending", out var pend) && pend.GetArrayLength() == 0, "sup.json pending [](묻는 창 대상 없음)");
            ctx.WaitUntil(() => c.Log().Contains("연결 대기(on_demand — /connect-uart 로 연결, 묻지 않음) COM521"), 5000, "감독 hub.log '연결 대기(on_demand …) COM521'");
            ctx.Check(!c.Log().Contains("확인 필요(첫 연결)") && !Regex.IsMatch(c.Log(), @"풍선: COM52[13]") && c.Log().Contains("cable pending SIMCOA COM521 — 연결 대기(on_demand"),
                      "첫 연결 '확인 필요' 0·COM521/523 풍선 0, 코어 'cable pending … 연결 대기(on_demand …)'");
            var st0 = c.Cli("status", "COM521", "--json");
            ctx.Check(st0.Code == 102 && J.S(st0.J, "blocked_reason") == "pending" && (J.S(st0.J, "error") ?? "").Contains("/connect-uart COM521"),
                      "status COM521 → 102 blocked_reason pending + '/connect-uart COM521': " + Show(st0.J));

            // ---- F1: 배정 있는(연결됨) COM522 를 MobaXterm 이 쥠
            ctx.WaitUntil(() => c.St("COM522") == "held_by_other", 10000, "F1: COM522 held_by_other");
            string f1 = "풍선: COM522 사용 중: MobaXterm. 허브가 열지 못했습니다. 다시 연결하려면 /connect-uart COM522";
            ctx.WaitUntil(() => c.Log().Contains(f1), 10000, "F1 풍선 '" + f1.Substring(4) + "'");
            Thread.Sleep(1500);
            ctx.Check(c.Count(f1) == 1 && c.Kills == "" && c.HoldersText.Contains("52201"), "F1: 풍선 1회(스냅숏마다 되풀이 안 함)·끝내지 않음·점유자 그대로");
            // F1 은 후보(알려진 시리얼 도구)만 연다: 모르는 프로그램(weirdtool)이 쥔 COM525 는 이름 없이 알린다
            string f1b = "풍선: COM525 사용 중: 다른 프로그램. 허브가 열지 못했습니다. 다시 연결하려면 /connect-uart COM525";
            ctx.WaitUntil(() => c.Log().Contains(f1b), 10000, "F1(후보만): 모르는 프로그램이 쥔 COM525 → '다른 프로그램' 풍선(전체 검사 안 함)");
            ctx.Check(c.Log().Contains("F1 COM525 held_by_other — 다른 프로그램 사용 중(끝내지 않음, 찾기 후보 ") && !Regex.IsMatch(c.Log(), @"F1 COM52\d .*찾기 후보 → 전체"),
                      "F1 찾기 범위 = 후보만(hub.log '찾기 후보', '→ 전체' 0)");
            c.Holders(rest);                                                // 사용자가 MobaXterm 에서 COM522 를 끊음
            ctx.WaitUntil(() => c.St("COM522") == "attached", 10000, "F1: 놓으면 허브가 다시 잡음(attached, 포트 18545)");

            // ---- 연결됐던(배정 있음) 케이블을 MoTTY 가 쥠(held_by_other) → connect → [끊고 연결] → 거짓 안내 창 없이 attached
            ctx.WaitUntil(() => c.St("COM524") == "held_by_other", 10000, "COM524(배정 18546) held_by_other — MoTTY 52401");
            int g0 = c.Count("[ui:fake] 안내 창:");
            var rh = c.Cli("connect", "COM524", "--json");
            var hc4 = rh.J.ValueKind == JsonValueKind.Object && rh.J.TryGetProperty("holder_closed", out var hh) ? hh : default;
            ctx.Check(rh.Code == 0 && J.S(rh.J, "status") == "ok" && J.N(rh.J, "port") == 18546 && hc4.ValueKind == JsonValueKind.Object && J.N(hc4, "pid") == 52401
                      && c.St("COM524") == "attached" && c.Kills.Contains("52401 MoTTY.exe COM524"),
                      "COM524 connect → [끊고 연결] → 그 세션만 끝냄 → attached(포트 18546)·holder_closed 52401: " + Show(rh.J));
            ctx.Check(c.Count("[ui:fake] 안내 창:") == g0 && !c.Log().Contains("connect COM524: 점유자를 찾지 못했는데 열리지 않음"),
                      "COM524: 끝낸 직후의 낡은 held_by_other 를 점유로 보지 않음(scan_interval_ms + 500ms 기다림) — 안내 창 0");

            // ---- 표: 모르는 프로그램은 사람이 부른 표에서만 전체 검사로 찾음, holder_scan_skip 은 열지 않음
            var tb = c.Cli("connect", "--json");
            var tbl = tb.J.ValueKind == JsonValueKind.Object ? tb.J.GetProperty("cables").EnumerateArray().ToDictionary(x => J.S(x, "com"), x => x.Clone()) : new Dictionary<string, JsonElement>();
            ctx.Check(tb.Code == 0 && tbl.ContainsKey("COM525") && J.S(tbl["COM525"], "state") == "in_use" && (J.S(tbl["COM525"], "text") ?? "").StartsWith("weirdtool(pid 52501) 사용 중", StringComparison.Ordinal)
                      && (J.S(tb.J, "holder_scan") ?? "").StartsWith("후보 → 전체", StringComparison.Ordinal),
                      "표: COM525 'weirdtool(pid 52501) 사용 중'(후보로 못 찾아 전체 검사 — holder_scan '" + J.S(tb.J, "holder_scan") + "')");
            ctx.Check(tbl.ContainsKey("COM526") && J.S(tbl["COM526"], "state") == "not_connected" && J.IsNull(tbl["COM526"], "holder"),
                      "표: COM526 의 점유자 skipme.exe 는 holder_scan_skip 이라 열지 않음(연결 안 됨으로 보임)");

            // ---- connect 때 포트 배정 + F2(0바이트) + 자동 깨우기(C.13)의 줄 지우기
            // 연결 전: 보드를 응답 없게 만든다(전원·배선 문제 흉내). 치다 만 줄을 지우는 자동 깨우기 안전성은 config-validate-t3 의 ⑻(NudgeScenarios).
            c.G.Inject("silent SIMCOA on", "SIMCOA 응답 없음");
            var r1 = c.Cli("connect", "COM521", "--json");
            ctx.Check(r1.Code == 0 && J.N(r1.J, "port") == 18530 && J.N(r1.J, "rx_bytes") == 0 && J.S(r1.J, "hint") == "no_output" && (J.S(r1.J, "message") ?? "").Contains("보드 출력이 아직 없습니다"),
                      "connect COM521 → 포트 18530(base 부터 — connect 때 배정)·rx_bytes 0·hint no_output·message: " + Show(r1.J));
            ctx.Check(c.Assign("SIMCOA") == 18530 && c.Assign("SIMCOC") == 0, "state.json: 연결한 SIMCOA 만 배정(SIMCOC 는 아직 없음)");
            var s1 = c.G.Ctl_("status", "18530");
            ctx.Check(J.N(s1, "rx_since_attach") == 0 && J.S(s1, "hint") == "no_output" && J.S(s1, "hint_text") == "보드에서 받은 데이터가 없습니다(연결 뒤 0바이트)",
                      "F2 status: rx_since_attach 0·hint no_output·hint_text: " + Show(s1));
            var d1 = c.Row("COM521");
            ctx.Check(J.N(d1, "rx_since_attach") == 0 && J.S(d1, "hint") == "no_output", "F2 ls --detail: rx_since_attach 0·hint no_output");
            var plain = c.G.Ctl_("ls").GetProperty("cables").EnumerateArray().First(x => J.S(x, "com") == "COM521");
            ctx.Check(!J.Has(plain, "rx_since_attach") && !J.Has(plain, "hint"), "F2: 기본 ls 의 케이블 필드는 Phase A 그대로(rx_since_attach·hint 없음)");
            string ConsoleLog() { var lp = c.G.Ctl_("logpath", "18530"); string f = J.S(lp, "log"); return f == null ? "" : HubFiles.ReadShared(f) ?? ""; }
            // ① 응답 없는 보드: run 은 --timeout 안에서 허브가 깨워 보고(연결 뒤 재부팅을 못 본 UNKNOWN 이라 ^E ^U CR), 그래도 0바이트면 101 + hint no_output
            var b1 = c.Cli("run", "18530", "true", "--timeout", "3", "--json");
            ctx.Check(b1.Code == 101 && J.S(b1.J, "blocked_reason") == "busy_since" && J.S(b1.J, "state") == "BUSY" && J.N(b1.J, "rx_since_attach") == 0 && J.S(b1.J, "hint") == "no_output",
                      "F2 101(run, 응답 없는 보드 — 깨운 CR 뒤 응답 없어 BUSY): rx_since_attach 0·hint no_output 덧붙음: " + Show(b1.J));
            string cl1 = ConsoleLog();
            ctx.Check(Regex.Matches(cl1, @">>> \[hub-nudge\] \^E\^U CR 1/3 \(연결 뒤 상태 모름").Count == 1 && !cl1.Contains("[hub-nudge] CR "),
                      "자동 깨우기: 연결 뒤 재부팅을 못 본 UNKNOWN 은 맨 CR 이 아니라 ^E^U CR(줄 지운 뒤 깨움) 1회");
            // ② 보드가 다시 응답: 깨운 CR 에 답이 없어 BUSY 라 허브는 더 깨우지 않는다(probe 도 BUSY 에선 거부) — 안내(hint_text)대로 사람이 터미널에서 Enter
            c.G.Inject("silent SIMCOA off", "SIMCOA 다시 응답");
            using (var tn = TnClient.Connect(18530))
            {
                ctx.Check(tn.WaitCount(TelnetScenarios.Greeting.Length, 3000), "telnet 18530 협상 시작");
                tn.Send(TelnetScenarios.TeraTermReply);                                  // 협상 응답 전 입력은 보드로 가지 않는다(사람 클라이언트 아님)
                Thread.Sleep(400);
                tn.Send("\r\0");
                ctx.WaitUntil(() => { var s = c.G.Ctl_("status", "18530"); return J.N(s, "rx_since_attach") > 0 && !J.Has(s, "hint"); }, 10000, "F2: 사람 Enter 로 보드가 응답하면 rx_since_attach > 0·hint 사라짐");
            }
            var b2 = c.Cli("run", "18530", "echo F2_RUN_OK", "--timeout", "10", "--json");
            ctx.Check(b2.Code == 0 && b2.J.GetProperty("out").EnumerateArray().Any(x => x.GetString() == "F2_RUN_OK"), "사람 Enter 뒤 run 실행: " + Show(b2.J));

            ctx.Check(J.N(r1.J, "baud") == 115200, "connect(보오 없음) → baud 115200(기본 그대로)");

            // ---- 두 번째 연결은 다음 빈 포트 + 첫 연결에 보오
            string cfgPath = Path.Combine(c.Hub, "config.json");
            string cfgA = File.ReadAllText(cfgPath);
            var r2 = c.Cli("connect", "COM523", "9600", "--json");
            ctx.Check(r2.Code == 0 && J.N(r2.J, "port") == 18531 && c.Assign("SIMCOC") == 18531 && J.N(r2.J, "baud") == 9600 && J.N(r2.J, "old_baud") == 115200,
                      "두 번째 connect COM523 9600 → 다음 빈 포트 18531(번호가 덜 띄엄띄엄)·baud 9600(첫 연결에 보오): " + Show(r2.J));
            ctx.Check(J.N(c.Row("COM523"), "baud") == 9600 && Config.Parse(File.ReadAllText(cfgPath)).CableFor("SIMCOC").Baud == 9600, "COM523: ls baud 9600·config.json cables.SIMCOC.baud 9600 저장");
            bool keysKept = true;
            using (var j0 = JsonDocument.Parse(cfgA)) using (var j1 = JsonDocument.Parse(File.ReadAllText(cfgPath)))
                foreach (var pr in j0.RootElement.EnumerateObject())
                    keysKept &= j1.RootElement.TryGetProperty(pr.Name, out var v1) && v1.GetRawText() == pr.Value.GetRawText();
            ctx.Check(keysKept, "config.json: 다른 최상위 키(포트·vids·시간 상수·on_demand) 원문 그대로(C-12)");
            string log523 = J.S(c.G.Ctl_("status", "18531"), "log");
            ctx.WaitUntil(() => log523 != null && File.Exists(log523) && LogFiles.ReadShared(log523).Contains("baud=9600") && LogFiles.ReadShared(log523).Contains("*** baud 115200 -> 9600 ***"), 5000,
                          "COM523 .log: attach 줄 baud=9600 뒤 '*** baud 115200 -> 9600 ***'");

            // ---- 연결된 채 보오 바꾸기 → 닫았다가 새 보오로 다시 엶
            var r3 = c.Cli("connect", "COM521", "57600", "--json");
            ctx.Check(r3.Code == 0 && J.N(r3.J, "baud") == 57600 && J.N(r3.J, "old_baud") == 115200 && Upd.True(r3.J, "already") && J.N(r3.J, "port") == 18530,
                      "연결된 COM521 → connect COM521 57600 → baud 57600(old_baud 115200)·같은 포트: " + Show(r3.J));
            ctx.Check(c.St("COM521") == "attached" && J.N(c.Row("COM521"), "baud") == 57600 && c.Log().Contains("detach SIMCOA COM521: baud 115200 -> 57600")
                      && Config.Parse(File.ReadAllText(cfgPath)).CableFor("SIMCOA").Baud == 57600, "COM521: 닫았다가(hub.log 'detach … baud 115200 -> 57600') 다시 attached·ls baud 57600·config.json 저장");
            string log521 = J.S(c.G.Ctl_("status", "18530"), "log");
            ctx.WaitUntil(() => log521 != null && File.Exists(log521) && LogFiles.ReadShared(log521).Contains("baud=57600") && LogFiles.ReadShared(log521).Contains("*** baud 115200 -> 57600 ***"), 5000,
                          "COM521 새 .log: attach 줄 baud=57600 뒤 '*** baud 115200 -> 57600 ***'");

            // ---- 표준이 아닌 보오 → 106, 아무것도 바꾸지 않음
            string cfgB = File.ReadAllText(cfgPath);
            var r4 = c.Cli("connect", "COM521", "12345", "--json");
            ctx.Check(r4.Code == 106 && (J.S(r4.J, "error") ?? "").Contains("1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600")
                      && File.ReadAllText(cfgPath) == cfgB && J.N(c.Row("COM521"), "baud") == 57600, "connect COM521 12345 → 106 + 쓸 수 있는 보오 목록·config.json·보오 그대로: " + Show(r4.J));

            // ---- 보오를 적지 않으면 그 케이블의 보오 그대로
            var r5 = c.Cli("connect", "COM521", "--json");
            ctx.Check(r5.Code == 0 && J.N(r5.J, "baud") == 57600 && !J.Has(r5.J, "old_baud"), "connect COM521(보오 없음) → baud 57600 그대로");
            var r6 = c.Cli("disconnect", "COM521", "--json");
            ctx.WaitUntil(() => c.St("COM521") == "released", 5000, "disconnect COM521 → released");
            var r7 = c.Cli("connect", "COM521", "--json");
            ctx.Check(r6.Code == 0 && r7.Code == 0 && J.N(r7.J, "baud") == 57600 && J.N(c.Row("COM521"), "baud") == 57600 && File.ReadAllText(cfgPath) == cfgB,
                      "끊었다 다시 connect(보오 없음) → 57600 그대로·config.json 바이트 그대로");
            var r8 = c.Cli("connect", "--json");
            var t521 = r8.J.ValueKind == JsonValueKind.Object ? r8.J.GetProperty("cables").EnumerateArray().FirstOrDefault(x => J.S(x, "com") == "COM521") : default;
            ctx.Check(r8.Code == 0 && t521.ValueKind == JsonValueKind.Object && J.N(t521, "baud") == 57600 && (J.S(t521, "text") ?? "").EndsWith("· 57600", StringComparison.Ordinal),
                      "인자 없는 표: COM521 baud 57600·text '… · 57600': " + (t521.ValueKind == JsonValueKind.Object ? J.Show(t521) : Show(r8.J)));

            // ---- 미연결 UART 정리(트레이 전용 cable forget): 꽂힌 것은 106·무변경, CLI 는 C-4 로 106, 뺀 것은 state.json 에서 지우고 다시 꽂으면 새 번호(옛 번호 재사용 없음)
            string stPath = Path.Combine(c.G.Hub, "state.json");
            string stA = File.ReadAllText(stPath);
            var fp = c.G.Ctl_("cable", "forget", "SIMCOA");
            var fc1 = c.Cli("cable", "forget", "SIMCOA", "--json");
            var fc2 = c.Cli("--json", "cable", "forget", "SIMCOA");
            var fc3 = c.Cli("--", "cable", "remembered");
            ctx.Check(J.N(fp, "exit") == 106 && J.S(fp, "status") == "rejected" && (J.S(fp, "error") ?? J.S(fp, "message") ?? "").Contains("꽂혀 있음") && File.ReadAllText(stPath) == stA
                      && fc1.Code == 106 && fc2.Code == 106 && fc3.Code == 106 && c.Assign("SIMCOA") == 18530,
                      "cable forget: 꽂힌 SIMCOA = 106·state.json 바이트 그대로, CLI cable forget·'--json cable …'·'-- cable …' = 106(C-4, 코어로 안 감) | " + J.Show(fp));
            c.G.Inject("unplug SIMCOA", "SIMCOA 뽑기");
            ctx.WaitUntil(() => c.G.StatusOf("SIMCOA") == "detached", 10000, "SIMCOA detached");
            // 다음 열거에서 빠져야 '꽂혀 있지 않음'(reader 오류로 먼저 detached 가 돼도 열거에 보이는 동안은 꽂힌 것)
            ctx.WaitUntil(() => c.G.Ctl_("cable", "remembered").GetProperty("cables").EnumerateArray().Any(x => J.S(x, "cable") == "SIMCOA"), 10000, "cable remembered 에 SIMCOA");
            var rem = c.G.Ctl_("cable", "remembered");
            var remA = rem.GetProperty("cables").EnumerateArray().Where(x => J.S(x, "cable") == "SIMCOA").ToList();
            var fo = c.G.Ctl_("cable", "forget", "SIMCOA");
            string stB = File.ReadAllText(stPath);
            var stJ = Upd.Parse(stB);
            ctx.Check(remA.Count == 1 && J.N(remA[0], "port") == 18530 && J.S(remA[0], "com") == "COM521" && rem.GetProperty("cables").EnumerateArray().All(x => J.S(x, "cable") != "SIMCOC")
                      && J.S(fo, "status") == "ok" && J.N(fo, "port") == 18530 && c.Assign("SIMCOA") == 0 && !stB.Contains("\"SIMCOA\"")
                      && stJ.GetProperty("retired_ports").EnumerateArray().Any(x => x.GetInt32() == 18530) && c.Log().Contains("cable forget SIMCOA :18530"),
                      "뺀 SIMCOA: cable remembered 에 포트 18530·COM521(꽂힌 SIMCOC 는 없음) → cable forget ok → state.json assign·boards 에서 지움, retired_ports 18530, hub.log 한 줄 | " + stB.Replace("\n", " "));
            c.G.Inject("plug SIMCOA", "SIMCOA 다시 꽂기");
            ctx.WaitUntil(() => c.G.StatusOf("SIMCOA") == "pending", 10000, "다시 꽂은 SIMCOA = pending(on_demand, 새 케이블)");
            var rc = c.Cli("connect", "COM521", "--json");
            int newPort = (int)J.N(rc.J, "port");
            ctx.Check(rc.Code == 0 && newPort > 18531 && newPort != 18530 && c.Assign("SIMCOA") == newPort,
                      "다시 꽂아 연결 → 새 번호 " + newPort + "(18530 재사용 안 함)");
            c.G.Violations();
            c.R.CheckNoProcs("on_demand 정리");

            // ---- 휴대용 시작: 기존 config.json 에 on_demand 더함 + skills
            var m = InstallRig.Create(ctx, "portable", 18560, 18570);
            m.Write("fake_drives.json", "{\"D\":{\"type\":\"Removable\",\"ready\":true}}");
            m.FakeWsl("Ubuntu-Conn\n");
            Directory.CreateDirectory(Path.Combine(m.UserProfile, ".claude"));
            Directory.CreateDirectory(Path.Combine(m.Root, "fake_wsl", "home", "Ubuntu-Conn", ".claude"));
            string hub = Path.Combine(m.UserProfile, "uart_logs", "hub");
            Directory.CreateDirectory(hub);
            string seed = m.SeedConfig();
            string cfg0 = "// 사용자가 고친 설정(주석·키 순서 보존 확인)\n" + seed.Replace("\"vids\": [],", "\"vids\": [], /* 실제 FTDI 열거 끔 */ \"cables\": {\"SIMXYZ\": {\"alias\": \"bench\"}},");
            File.WriteAllText(Path.Combine(hub, "config.json"), cfg0, Utf8);
            string exe = Path.Combine(m.Root, "Tools", "UartHub", "UartHub.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exe));
            File.Copy(Dev, exe, true);
            // C.11: 옛 배치(기준 폴더 바로 아래 날짜 폴더·live·events)를 두고 시작 → log_layout 2 로 옮김
            string lr = Path.GetDirectoryName(hub);
            foreach (var d in new[] { "20261105", "live", @"events\20261105" }) Directory.CreateDirectory(Path.Combine(lr, d));
            File.WriteAllText(Path.Combine(lr, "20261105", "18570_OLD_101010.log"), "old");
            File.WriteAllText(Path.Combine(lr, "20261105", "18570_OLD_101010.raw"), "old");
            File.WriteAllText(Path.Combine(lr, "events", "18570.jsonl"), "");
            File.WriteAllText(Path.Combine(lr, "events", "20261105", "18570_OLD_101011_panic.txt"), "p");
            var e1 = m.Run(exe, new string[0]);
            ctx.Check(e1.Code == 0 && e1.Out.Contains("# 시작 완료(휴대용)") && e1.Out.Contains("config.json 에 \"on_demand\": true 더함") && e1.Out.Contains("config.json 에 \"log_layout\": 3 보장"),
                      "휴대용 처음 실행(기존 config.json) → exit 0·'config.json 에 \"on_demand\": true 더함' (" + e1.Ms + "ms)" + (e1.Code != 0 ? " — " + Upd.Tail(e1.Out) : ""));
            string cfg1 = File.ReadAllText(Path.Combine(hub, "config.json"));
            string con = Path.Combine(lr, Config.ConsoleDirName);
            ctx.Check(File.Exists(Path.Combine(con, "20261105", "18570_OLD_101010.log")) && (File.GetAttributes(Path.Combine(con, "20261105", "18570_OLD_101010.raw")) & FileAttributes.Hidden) != 0
                      && Directory.Exists(Path.Combine(con, "live")) && File.Exists(Path.Combine(hub, "events", "18570.jsonl"))
                      && File.Exists(Path.Combine(lr, Config.EventsTxtDirName, "20261105", "18570_OLD_101011_panic.txt")) && !Directory.Exists(Path.Combine(lr, "events"))
                      && !Directory.Exists(Path.Combine(lr, "20261105")) && e1.Out.Contains("로그 폴더 옮김") && e1.Out.Contains("로그 폴더 정리: 옮김 4개, 그대로 둠 0개"),
                      "휴대용 시작(허브 꺼짐·update mutex): 옛 날짜 폴더·live → 콘솔로그\\, events\\*.jsonl → hub\\events, events\\<날짜>\\*.txt → 에이징이벤트\\<날짜>, .raw 숨김, install.log 에 줄마다");
            ctx.Check(cfg1.StartsWith("// 사용자가 고친 설정", StringComparison.Ordinal) && cfg1.Contains("\"on_demand\": true") && cfg1.Contains("\"log_layout\": 3")
                      && cfg1.Replace("\n  \"log_layout\": 3,", "").Replace("\n  \"on_demand\": true,", "") == cfg0,
                      "config.json: on_demand·log_layout 두 줄만 더함 — 주석·다른 키(cables.SIMXYZ·포트·시간 상수) 바이트 그대로(C-12)");
            ctx.WaitUntil(() => { var s = HubFiles.ReadJson(HubFiles.SupState(hub)); return s.HasValue && J.B(s.Value, "on_demand") == true && HubFiles.Str(s.Value, "state") == "running"; }, 15000,
                          "휴대용 감독 sup.json on_demand true·running");
            ctx.WaitUntil(() =>
            {
                var o = m.Run(exe, "cli", "ls", "--json");
                var j = Upd.Parse(o.Out);
                return o.Code == 0 && j.ValueKind == JsonValueKind.Object && j.GetProperty("cables").EnumerateArray().Any(x => J.S(x, "cable") == m.Cable && J.S(x, "status") == "pending");
            }, 15000, "휴대용: FakeBoard " + m.Cable + " = pending(꽂아도 잡지 않음)");

            // skills: connect-uart·disconnect-uart
            string ws = Path.Combine(m.UserProfile, ".claude", "skills"), wh = Path.Combine(m.Root, "fake_wsl", "home", "Ubuntu-Conn", ".claude", "skills");
            string id = HubFiles.Str(m.InstallInfoJson().Value, "install_id");
            foreach (var sk in new[] { "connect-uart", "disconnect-uart" })
            {
                string t = File.Exists(Path.Combine(ws, sk, "SKILL.md")) ? File.ReadAllText(Path.Combine(ws, sk, "SKILL.md")) : "";
                var fm = Regex.Match(t, @"\A---\n(.*?)\n---\n", RegexOptions.Singleline);
                ctx.Check(fm.Success && Regex.IsMatch(fm.Groups[1].Value, "^name: " + sk + "$", RegexOptions.Multiline) && Regex.IsMatch(fm.Groups[1].Value, "^disable-model-invocation: true$", RegexOptions.Multiline)
                          && t.Contains("~/.claude/skills/uart/uart " + (sk == "connect-uart" ? "connect" : "disconnect") + " $ARGUMENTS --json") && t.Contains("180000") && !t.Contains('\r')
                          && new[] { "/home/", "claude/uart", "D:\\", "/mnt/d" }.All(f => t.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0)
                          && (HubFiles.ReadShared(Path.Combine(ws, sk, ".uart_hub")) ?? "").Contains(id),
                          "Windows skills\\" + sk + ": name·disable-model-invocation: true·'uart " + (sk == "connect-uart" ? "connect" : "disconnect") + " $ARGUMENTS --json'·timeout 180000·CR 0·금지 문자열 0·.uart_hub install_id");
                ctx.Check(File.Exists(Path.Combine(wh, sk, "SKILL.md")) && File.ReadAllText(Path.Combine(wh, sk, "SKILL.md")) == t && File.ReadAllText(Path.Combine(wh, sk, ".uart_hub")).Contains(id),
                          "가짜 WSL Ubuntu-Conn ~/.claude/skills/" + sk + ": 같은 바이트·.uart_hub install_id");
            }
            string us = File.ReadAllText(Path.Combine(ws, "uart", "SKILL.md"));
            ctx.Check(us.Contains("/connect-uart COMx") && us.Contains("`$U connect`·`$U disconnect` 는 **사람 전용**") && us.Contains("--pattern 'Restarting system'")
                      && us.Contains("--pattern '^root@' --nudge") && us.Contains("hint:\"no_output\""), "uart SKILL.md: 연결 안 됨 → 사용자에게 /connect-uart 부탁·스스로 connect 금지·재부팅 2단계 wait(F3)·hint no_output(F2)");

            // 두 번째 시작: 이미 있음 → 그대로
            var sd = SupervisorChannel.Request(hub, "shutdown", "tray", 10000);
            ctx.WaitUntil(() => m.Procs().Count == 0, 20000, "종료(허브 끄기): 감독·코어 0");
            var e2 = m.Run(exe, new string[0]);
            ctx.Check(e2.Code == 0 && e2.Out.Contains("config.json on_demand: on_demand 이미 있음(true) — 그대로 둠") && File.ReadAllText(Path.Combine(hub, "config.json")) == cfg1
                      && e2.Out.Contains("config.json log_layout: log_layout 이미 있음(3) — 그대로 둠") && !e2.Out.Contains("로그 폴더 옮김"),
                      "두 번째 휴대용 시작 → 'on_demand 이미 있음(true) — 그대로 둠'·config.json 바이트 그대로" + (e2.Code != 0 ? " — " + Upd.Tail(e2.Out) : ""));

            // 제거: skills 함께 지움
            var u = m.Run(exe, "uninstall");
            ctx.Check(u.Code == 0 && u.Out.Contains("# 제거 완료(휴대용)"), "uninstall → exit 0" + (u.Code != 0 ? " — " + Upd.Tail(u.Out) : ""));
            ctx.Check(new[] { "uart", "connect-uart", "disconnect-uart" }.All(sk => !Directory.Exists(Path.Combine(ws, sk)) && !Directory.Exists(Path.Combine(wh, sk))),
                      "제거: Windows·가짜 WSL 의 uart·connect-uart·disconnect-uart 폴더 모두 지움(표시 install_id 일치)");
            m.CheckNoProcs("휴대용 정리");
            real.Verify(ctx, c.R, m);
        }
    }
}
