// `UartHub.exe cli` — UART 허브 CLI(계획 §3.6·§3.7, uart.ps1 이식, 계약 C-6·C-9).
//   인자 입력 세 가지:
//     cli -B64Req <base64(UTF-8 JSON 문자열 배열)>    Phase A WSL 래퍼 형식(시나리오 1). 따옴표·\·$·'-'로 시작하는 인자·한글이 다시 해석되지 않는다.
//     cli --b64nul <base64url(인자마다 NUL 로 끝나는 UTF-8)>  새 래퍼 형식(§2.3 l): printf '%s\0' "$@" | base64 → '+/'→'-_', '=' 제거. NUL 은 bash 인자에 들어갈 수 없어 손실이 없다.
//     cli <명령> <대상> [인자] [--옵션]                 일반 인자.
//   맨 앞 --parent-pipe(K4 후속): 표준 입력을 호출 쪽이 쥔 파이프로 보고, EOF(호출 쪽이 죽음·TERM)면 바로 끝낸다 → 제어 연결이 닫혀
//     코어가 대기 요청을 취소한다(Arbiter Cancel = 미전송). WinExe 에는 SIGTERM 이 전달되지 않기 때문이다(부록 A K4).
//   명령: ls | status <c> | run <c> "<cmd>" | send <c> "<raw>" | probe <c> | wait <c> --pattern RE | reboot <c>
//         | log <c> [--tail N | --since OFF] | logpath <c> | hub status|stop|start|upgrade | hub language auto|ko|en | connect [COMx [baud]] | disconnect COMx
//         | events [<port>] [--since …] [--kind …] [--follow] · aging <port> [--since …](X6 — 코어에 묻지 않고 <log_root>\events 를 읽기만, Cli.Events.cs)
//         | session start <c> [이름] · session stop <c> · session status [--json](부록 C.12 — 코어가 세션을 바꾸고 읽는다. COM 을 열고 닫지 않아 Claude 가 불러도 된다)
//         | session summary <c> [--since …](= aging, 이전 이름 aging 도 그대로)
//   hub 명령: 코어(제어 포트)가 감독 요청 스풀로 넘긴다. 코어에 닿지 못하면 이 CLI 가 직접 <허브 폴더>\sup\req 에 요청을 쓰고 감독의 응답을 기다린다.
//   connect·disconnect(X5 부록 B): 사람이 부르는 명령(/connect-uart·/disconnect-uart skill). 감독 요청 스풀 {"verb":"connect","args":["COMx"]}로 감독
//     (트레이 프로세스 — 확인 창·점유 세션 끊기·터미널)에 넘기고 응답(최대 175초)을 찍는다. 감독이 없으면 C-9 조건에서 창 없이 sup 을 띄우고 기다린다.
//     `cable` 은 지금처럼 106(C-4 — 클라이언트 쪽 제한).
//   argv 해석은 허브(ControlServer)가 한다. CLI 는 argv 를 그대로 넘기고 응답을 출력할 뿐이다(WSL·Windows 결과 동일).
//   제어 연결: ControlClient(허브 인사 2초 → hello HMAC → proof 확인). 응답이 JSON 객체(status·exit)가 아니면 103.
//   출력(C-6): 표준 출력·오류에 UTF-8(BOM 없음)·LF 로 직접 쓴다. --json 이면 응답 줄을 그대로(경로 변환이 필요할 때만 다시 직렬화).
//   종료코드: run 은 보드 rc(0~255). 도구 오류는 100 timeout · 101 busy · 102 no_cable · 103 hub_down · 104 md5_mismatch · 105 ambiguous · 106 rejected.
//            비JSON 모드의 도구 오류는 stderr 에 'uart: E10x <의미> …'. 103 은 코어에 닿지 못했을 때만(C-9).
//   허브 위치(C-2): UART_HUB_DIR → %LOCALAPPDATA%\uart_hub\install.json 의 hub_dir → 없으면 103 설치되지 않음.
//   자가 복구(C-9): 코어에 닿지 못했고, UART_HUB_DIR 이 없고(install.json 으로 허브 폴더를 정함), install.json 이 state:"installed" 이며
//            감독·update mutex 가 모두 없고(C-8 존재 확인), 정지(control.json stopped) 상태가 아니면 install.json 의 exe 로 sup 을 분리 기동하고
//            103 + restarted:true(대기 시간 계약 불변 — SKILL 은 5초 뒤 한 번 다시 시도). 정지 상태면 103 stopped:true 이고 띄우지 않는다.
//            제어 포트: UART_HUB_CONTROL_PORT → 살아 있는 코어의 core.pid control_port → config.json(주석 제외) control_port → 7200.
//   경로 표기(C-6, §2.3 m): UART_PATH_STYLE=win(기본, D:\…) | wsl(<UART_WSL_MOUNT 기본 /mnt/>d/…) | mixed(D:/…).
//            대상 필드: log raw idx path log_root hub_dir config core_config(중첩 객체 포함). out·last_lines 같은 보드 출력은 바꾸지 않는다.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace UartHub
{
    public static partial class Cli
    {
        static readonly Dictionary<int, string> Meaning = new Dictionary<int, string>
        {
            [100] = "timeout", [101] = "busy", [102] = "no_cable", [103] = "hub_down", [104] = "md5_mismatch", [105] = "ambiguous", [106] = "rejected",
        };
        static readonly string[] PathKeys = { "log", "raw", "idx", "path", "log_root", "hub_dir", "config", "core_config", "context", "events_dir", "events_file", "folder", "summary", "aging_root", "aging_roots" };
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        static readonly UTF8Encoding Utf8Strict = new UTF8Encoding(false, true);
        static readonly object outLock = new object();

        static bool jsonMode, fromInstall;
        static string hubDir;

        sealed class Quit : Exception { public readonly int Code; public Quit(int c) { Code = c; } }

        public static int Run(string[] args)
        {
            try { return Main(args); }
            catch (Quit q) { return q.Code; }
        }

        // ================================================================== 출력
        static void Write(Stream s, string text)
        {
            byte[] b = Utf8.GetBytes(text);
            lock (outLock)
            {
                try { s.Write(b, 0, b.Length); s.Flush(); }
                catch (Exception) { if (ReferenceEquals(s, stdout)) stdoutBroken = true; /* 호출 쪽이 파이프를 닫음 — 종료코드로만 전달(events --follow 는 이것을 보고 끝낸다) */ }
            }
        }
        /// <summary>표준 출력 쓰기가 실패했다(읽는 쪽이 닫음 — `| head`, Monitor 종료).</summary>
        static volatile bool stdoutBroken;
        static Stream stdout, stderr;
        static void OutLine(string s) => Write(stdout ??= Console.OpenStandardOutput(), s + "\n");
        static void ErrLine(string s) => Write(stderr ??= Console.OpenStandardError(), s + "\n");

        static bool HubStopped() => hubDir != null && HubFiles.IsStopped(hubDir);

        /// <summary>도구 오류로 끝낸다(uart.ps1 Quit-Tool). 103 이고 control.json stopped 면 'hub start' 안내를 붙인다.</summary>
        static Exception QuitTool(int code, string msg, bool restarted = false)
        {
            bool stopped = code == 103 && HubStopped();
            if (stopped) msg = L.T("허브 정지됨(hub stopped — 'uart hub start' 로 재개). ", "Hub stopped (hub stopped — resume with 'uart hub start'). ") + msg;
            if (jsonMode)
            {
                var o = new JObj().Set("status", Meaning[code]).Set("exit", (long)code).Set("error", msg);
                if (stopped) o.Set("stopped", true);
                if (restarted) o.Set("restarted", true);
                OutLine(o.ToString());
            }
            else ErrLine("uart: E" + code + " " + Meaning[code] + " " + msg + (restarted ? " (restarted)" : ""));
            return new Quit(code);
        }

        /// <summary>
        /// 코어에 닿지 못했을 때(C-9): 자가 복구 조건이 맞으면 감독을 분리 기동하고 103 + restarted:true, 아니면 그냥 103(정지면 stopped:true).
        /// 조건이 맞지 않는 이유(감독이 살아 있음·update 중·설치 중 등)는 Phase A 대로 103 만 준다.
        /// </summary>
        static Exception HubDown(string msg)
        {
            if (!fromInstall || HubStopped()) return QuitTool(103, msg);
            if (CoreMain.MutexExists(SystemAccess.MutexName("sup")) || CoreMain.MutexExists(SystemAccess.MutexName("update"))) return QuitTool(103, msg);
            string exe = CoreMain.RespawnExe(out string why);
            if (exe == null) return QuitTool(103, msg + L.T(" — 감독을 다시 띄우지 않음(" + why + ")", " — not restarting the supervisor (" + why + ")"));
            try
            {
                NativeHandles.DontInheritStd();                          // 띄운 감독이 호출 쪽(WSL·Claude)의 파이프를 물고 남지 않게
                var p = SystemAccess.StartDetached(exe, new[] { "sup" });
                return QuitTool(103, msg + L.T(" — 감독이 없어 다시 띄움(pid " + p.Pid + ", 5초 뒤 다시 시도)", " — no supervisor, restarted it (pid " + p.Pid + ", retry in 5 s)"), true);
            }
            catch (TestIsolationViolation) { throw; }
            catch (Exception e) { return QuitTool(103, msg + L.T(" — 감독 기동 실패: " + e.Message, " — failed to start the supervisor: " + e.Message)); }
        }

        // ================================================================== 본체
        static int Main(string[] args)
        {
            int skip = 0;
            {
                string hd0 = null;
                try { hd0 = InstallInfo.ResolveHubDir(out _); } catch (Exception) { }
                if (!string.IsNullOrEmpty(hd0)) L.ConfigureFromFile(HubFiles.ConfigPath(hd0)); else L.Configure(null);
            }
            if (args.Length > 0 && args[0] == "--parent-pipe") { WatchParent(); skip = 1; }
            var a = args.Skip(skip).ToArray();

            List<string> argv;
            if (a.Length >= 1 && a[0] == "-B64Req")
            {
                if (a.Length != 2) throw QuitTool(106, L.T("-B64Req 뒤에는 base64 인자 하나만", "-B64Req takes exactly one base64 argument"));
                argv = DecodeB64Req(a[1]);
            }
            else if (a.Length >= 1 && a[0] == "--b64nul")
            {
                if (a.Length != 2) throw QuitTool(106, L.T("--b64nul 뒤에는 base64url 인자 하나만", "--b64nul takes exactly one base64url argument"));
                argv = DecodeB64Nul(a[1]);
            }
            else argv = a.ToList();
            foreach (var x in argv) { if (x == "--") break; if (x == "--json") jsonMode = true; }
            // 명령은 코어(ControlServer)와 같은 규칙으로 읽는다 — `--json cable …`·`-- cable …`·`--timeout 5 cable …` 도 cable 이다(C-4 우회 방지).
            // 해석 오류(모르는 옵션 등)는 코어도 같은 오류로 106 을 주므로 그대로 넘긴다.
            // X6: events·aging 만 CLI 의 옵션 --follow·--kind·--max 를 받는다 — 코어 명령은 받지 않는다(`run … --follow` 는 Phase A 처럼 106, L3)
            argv = SummaryAlias(argv);                // 부록 C.12: session summary <c> … = aging <c> …
            var pe = ControlServer.ParseArgs(argv, out _, EventsFlags, EventsValued);
            var pa = pe != null && (Eq(pe.Cmd, "events") || Eq(pe.Cmd, "aging")) ? pe : ControlServer.ParseArgs(argv, out _);
            string cmd = pa?.Cmd;
            // C-4: 케이블 판정은 사람이 트레이에서만 한다(Claude 가 대신 답하지 않게 — 클라이언트 쪽 제한, 코어로 보내지 않음)
            if (Eq(cmd, "cable")) throw QuitTool(106, L.T("cable 은 트레이에서만(새 케이블 확인 창·트레이 '케이블 설정') — CLI 는 케이블 판정을 보내지 않음", "cable is tray-only (new-cable prompt window, tray 'cable settings') — the CLI does not send cable decisions"));
            string hubVerb = null;
            string[] hubArgs = null;
            if (Eq(cmd, "hub") && pa.Pos.Count >= 1 && new[] { "status", "stop", "start", "upgrade", "language" }.Any(v => Eq(pa.Pos[0], v))) hubVerb = pa.Pos[0];
            if (Eq(hubVerb, "language"))
            {
                // 언어 바꾸기: 감독이 config.json "language" 를 고치고(auto = 키 지움) 언어가 바뀌면 코어를 다시 띄운다(Supervisor.RequestLanguage)
                string lv = pa.Pos.Count == 2 ? pa.Pos[1].Trim().ToLowerInvariant() : null;
                if (!L.IsValidSetting(lv)) throw QuitTool(106, L.T("사용: hub language auto|ko|en [--json]", "Usage: hub language auto|ko|en [--json]"));
                hubVerb = "language";
                hubArgs = new[] { lv };
            }

            // ---- 허브 위치·제어 포트·토큰
            hubDir = InstallInfo.ResolveHubDir(out fromInstall);
            if (string.IsNullOrEmpty(hubDir))
                throw QuitTool(103, L.T("허브 폴더를 찾지 못함 — 설치되지 않음(UART_HUB_DIR 없음, " + InstallInfo.DefaultPath + " 의 hub_dir 없음)", "Hub folder not found — not installed (UART_HUB_DIR not set, no hub_dir in " + InstallInfo.DefaultPath + ")"));
            // X6: 사건 기록 읽기 — 코어·보드에 아무것도 보내지 않는다(허브가 꺼져 있어도 지난 기록을 읽는다)
            if (Eq(cmd, "events") || Eq(cmd, "aging")) return EventsCmd(cmd.ToLowerInvariant(), pa);
            string line = null;
            // connect·disconnect 는 감독에게(코어 제어 포트·토큰을 쓰지 않음 — 응답은 아래 공통 해석·출력으로)
            bool connectCmd = Eq(cmd, "connect") || Eq(cmd, "disconnect");
            if (connectCmd) line = ConnectLine(cmd.ToLowerInvariant(), pa);
            int port = connectCmd ? 0 : ControlPort();
            string tokenPath = Path.Combine(hubDir, "token");
            if (!connectCmd && !File.Exists(tokenPath))
            {
                if (hubVerb != null) line = SupervisorLine(hubVerb, L.T("토큰 파일 없음(" + tokenPath + ")", "Token file missing (" + tokenPath + ")"), hubArgs);
                else throw QuitTool(103, L.T("토큰 파일 없음(" + tokenPath + ") — 허브가 설치·실행 중인지 확인", "Token file missing (" + tokenPath + ") — check that the hub is installed and running"));
            }

            // ---- 요청(hub 명령은 코어가 없으면 감독에게 직접)
            string notHub = L.T("127.0.0.1:" + port + " 의 상대가 허브가 아님(다른 프로그램이 포트를 쓰는 중일 수 있음 — hub.log 의 control bind 실패·config.json control_port 확인)", "The peer at 127.0.0.1:" + port + " is not the hub (another program may be using the port — check the control bind failure in hub.log and control_port in config.json)");
            if (line == null)
            {
                string tokenText = HubFiles.ReadShared(tokenPath);
                if (tokenText == null) throw QuitTool(103, L.T("토큰 파일을 읽지 못함(" + tokenPath + ") — 권한·잠금 확인", "Could not read the token file (" + tokenPath + ") — check permissions and locks"));
                byte[] key = Encoding.UTF8.GetBytes(tokenText.Trim());
                string req = "{\"argv\":[" + string.Join(",", argv.Select(Json.Str)) + "]}";
                var res = ControlClient.Call(port, key, req, 3000, 2000, 10000, -1, out string reply, out string err);
                switch (res)
                {
                    case ControlClient.Result.ConnectFailed:
                        if (hubVerb != null && (Eq(hubVerb, "stop") || !SelfHealDue())) line = SupervisorLine(hubVerb, L.T("허브 코어 미응답(127.0.0.1:" + port + ")", "Hub core not responding (127.0.0.1:" + port + ")"), hubArgs);
                        else throw HubDown(L.T("허브 미응답(127.0.0.1:" + port + ")", "Hub not responding (127.0.0.1:" + port + ")"));
                        break;
                    case ControlClient.Result.NotHub:
                        if (hubVerb != null) line = SupervisorLine(hubVerb, notHub, hubArgs);
                        else throw QuitTool(103, notHub);
                        break;
                    case ControlClient.Result.HelloRejected: throw QuitTool(103, L.T("hello 거부(토큰 불일치?)", "hello rejected (token mismatch?)"));
                    case ControlClient.Result.ProofMismatch: throw QuitTool(103, L.T("허브 증명(proof) 불일치 — 토큰이 다르거나 상대가 허브가 아님. 요청은 보내지 않음", "Hub proof mismatch — the token differs or the peer is not the hub. The request was not sent"));
                    case ControlClient.Result.Failed: throw QuitTool(103, L.T("허브 통신 실패: " + err, "Hub communication failed: " + err));
                    default: line = reply; break;
                }
            }
            if (string.IsNullOrEmpty(line)) throw QuitTool(103, L.T("허브가 응답 없이 연결을 닫음", "The hub closed the connection without replying"));

            // 응답이 JSON 객체가 아니거나 status·exit 가 없으면(허브가 아닌 상대, 깨진 응답) 103
            JsonDocument doc = null;
            try { doc = JsonDocument.Parse(line); } catch (JsonException) { }
            JsonElement resp = doc != null ? doc.RootElement : default;
            int code = 0;
            bool okShape = doc != null && resp.ValueKind == JsonValueKind.Object && resp.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String
                           && resp.TryGetProperty("exit", out var ex) && ExitCode(ex, out code);
            if (!okShape)
            {
                string show = line.Length > 80 ? line.Substring(0, 80) + "…" : line;
                throw QuitTool(103, L.T("허브 응답을 해석할 수 없음(JSON 객체·status·exit 아님): ", "Cannot parse the hub reply (not a JSON object with status and exit): ") + Regex.Replace(show, "[\\x00-\\x1f]", "?"));
            }
            string style = Environment.GetEnvironmentVariable("UART_PATH_STYLE");
            if (style == "wsl" || style == "mixed")
            {
                string mount = Environment.GetEnvironmentVariable("UART_WSL_MOUNT");
                mount = string.IsNullOrEmpty(mount) ? "/mnt/" : mount.TrimEnd('/') + "/";
                line = Rewrite(resp, style, mount);
                doc.Dispose();
                doc = JsonDocument.Parse(line);
                resp = doc.RootElement;
            }
            string status = resp.GetProperty("status").GetString();
            if (Eq(hubVerb, "language") && status == "ok") L.ConfigureFromFile(HubFiles.ConfigPath(hubDir));   // 출력은 바뀐 언어로(환경변수가 있으면 그것)

            // ---- 출력
            if (jsonMode)
            {
                OutLine(line);
                return code;
            }
            bool toolError = code >= 100 && code <= 106 && !(Eq(cmd, "run") && status == "ok");
            if (toolError)
            {
                if (Eq(cmd, "run") || Eq(cmd, "log")) OutLines(Prop(resp, "out"));
                var detail = new List<string>();
                if (Truthy(Prop(resp, "error"))) detail.Add(PsStr(Prop(resp, "error")));
                if (Truthy(Prop(resp, "blocked_reason"))) detail.Add("blocked_reason=" + PsStr(Prop(resp, "blocked_reason")));
                if (Truthy(Prop(resp, "cleanup"))) detail.Add("cleanup=" + PsStr(Prop(resp, "cleanup")));
                if (status != Meaning[code]) detail.Insert(0, "status=" + status);
                ErrLine("uart: E" + code + " " + Meaning[code] + (detail.Count > 0 ? " " + string.Join(" ", detail) : ""));
                var ll = Prop(resp, "last_lines");
                if (Truthy(ll)) foreach (var l in Items(ll)) ErrLine("  | " + PsStr(l));
                var cand = Prop(resp, "candidates");
                if (Truthy(cand))
                    foreach (var c in Items(cand))
                        ErrLine(L.T("  후보 port=", "  candidate port=") + PsStr(Prop(c, "port")) + " com=" + PsStr(Prop(c, "com")) + " alias=" + PsStr(Prop(c, "alias")) + " board=" + PsStr(Prop(c, "board")));
                return code;
            }

            if (connectCmd) PrintConnect(cmd, resp);
            else if (Eq(cmd, "session")) PrintSession(resp);
            else if (Eq(cmd, "run") || Eq(cmd, "wait") || Eq(cmd, "log")) OutLines(Prop(resp, "out"));
            else if (Eq(cmd, "logpath")) OutLine(PsStr(Prop(resp, "log")));
            else if (Eq(cmd, "send") || Eq(cmd, "probe") || Eq(cmd, "reboot")) OutLine(PsStr(Prop(resp, "rx_offset")));
            else if (Eq(hubVerb, "language"))
            {
                string hl = PsStr(Prop(resp, "language")), act = PsStr(Prop(resp, "action")), set = PsStr(Prop(resp, "setting"));
                string name = hl == L.En ? L.T("영어", "English") : hl == L.Ko ? L.T("한국어", "Korean") : hl;
                OutLine(act == "unchanged" ? L.T("허브 언어 그대로: " + name + "(설정 " + set + ")", "Hub language unchanged: " + name + " (setting " + set + ")")
                        : act == "restarting" ? L.T("허브 언어를 " + name + "로 바꿨습니다(설정 " + set + "). 허브를 다시 켜는 중입니다 — 열린 터미널은 끊깁니다.", "Hub language changed to " + name + " (setting " + set + "). The hub is restarting — open terminals are disconnected.")
                        : L.T("허브 언어를 " + name + "로 바꿨습니다(설정 " + set + ").", "Hub language changed to " + name + " (setting " + set + ")."));
                if (Truthy(Prop(resp, "note"))) OutLine(PsStr(Prop(resp, "note")));
            }
            else if (Eq(cmd, "hub"))
            {
                // 한 단계 펼침: supervisor.state=running …
                foreach (var p in resp.EnumerateObject())
                {
                    if (Eq(p.Name, "status") || Eq(p.Name, "exit")) continue;
                    if (p.Value.ValueKind == JsonValueKind.Object)
                        foreach (var q in p.Value.EnumerateObject()) OutLine(p.Name + "." + q.Name + "=" + PsStr(q.Value));
                    else OutLine(p.Name + "=" + PsStr(p.Value));
                }
            }
            else if (Eq(cmd, "ls"))
            {
                foreach (var c in Items(Prop(resp, "cables")))
                {
                    string Or(string k) { var v = Prop(c, k); return Truthy(v) ? PsStr(v) : "-"; }
                    OutLine(PsStr(Prop(c, "port")) + " " + PsStr(Prop(c, "com")) + " " + PsStr(Prop(c, "status")) + " " + Or("board") + " " + Or("state") + " " + Or("alias") + " " + PsStr(Prop(c, "cable")));
                }
            }
            else
            {
                foreach (var p in resp.EnumerateObject())
                {
                    if (Eq(p.Name, "status") || Eq(p.Name, "exit")) continue;
                    string v = p.Value.ValueKind == JsonValueKind.Array ? string.Join(" | ", p.Value.EnumerateArray().Select(PsStr)) : PsStr(p.Value);
                    OutLine(p.Name + "=" + v);
                }
            }
            return code;
        }

        // PowerShell 의 -eq·switch·-in 은 대소문자를 가리지 않았다(uart.ps1 과 같은 판정)
        static bool Eq(string a, string b) => a != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        // ================================================================== 인자 입력
        static List<string> DecodeB64Req(string b64)
        {
            JsonDocument d;
            try { d = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64))); }
            catch (Exception e) when (e is FormatException || e is JsonException) { throw QuitTool(106, L.T("-B64Req 해석 실패: " + e.Message, "Failed to parse -B64Req: " + e.Message)); }
            using (d)
            {
                if (d.RootElement.ValueKind != JsonValueKind.Array) throw QuitTool(106, L.T("-B64Req 는 JSON 문자열 배열이어야 함", "-B64Req must be a JSON array of strings"));
                var l = new List<string>();
                foreach (var x in d.RootElement.EnumerateArray())
                {
                    if (x.ValueKind != JsonValueKind.String) throw QuitTool(106, L.T("-B64Req 는 JSON 문자열 배열이어야 함", "-B64Req must be a JSON array of strings"));
                    l.Add(x.GetString());
                }
                return l;
            }
        }

        /// <summary>--b64nul: base64url(패딩 없음, 표준 base64 도 받음) → UTF-8 바이트 → 인자마다 NUL 로 끝남.</summary>
        static List<string> DecodeB64Nul(string tok)
        {
            byte[] b;
            try
            {
                string s = tok.Replace('-', '+').Replace('_', '/');
                s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
                b = Convert.FromBase64String(s);
            }
            catch (FormatException e) { throw QuitTool(106, L.T("--b64nul 해석 실패: " + e.Message, "Failed to parse --b64nul: " + e.Message)); }
            if (b.Length > 0 && b[b.Length - 1] != 0) throw QuitTool(106, L.T("--b64nul 은 인자마다 NUL 로 끝나야 함(printf '%s\\0' \"$@\")", "--b64nul requires each argument to end with NUL (printf '%s\\0' \"$@\")"));
            string text;
            try { text = Utf8Strict.GetString(b); }
            catch (ArgumentException) { throw QuitTool(106, L.T("--b64nul 인자가 UTF-8 이 아님", "--b64nul arguments are not UTF-8")); }
            var l = text.Split('\0').ToList();
            l.RemoveAt(l.Count - 1);                                   // 마지막 NUL 뒤의 빈 조각
            return l;
        }

        // ================================================================== 허브 위치·포트
        /// <summary>제어 포트: ① UART_HUB_CONTROL_PORT ② 살아 있는 코어의 core.pid control_port ③ config.json(jsonc — 주석을 지운 뒤) ④ 7200. 값 오류 106, 읽기 실패 103.</summary>
        static int ControlPort()
        {
            string env = Environment.GetEnvironmentVariable("UART_HUB_CONTROL_PORT");
            if (!string.IsNullOrEmpty(env)) return PortValue(env, "UART_HUB_CONTROL_PORT");
            int port = 0;
            var cp = HubFiles.ReadJson(HubFiles.CorePid(hubDir));
            if (cp.HasValue)
            {
                long cport = HubFiles.Long(cp.Value, "control_port");
                if (cport >= 1 && cport <= 65535 && HubFiles.IsAlive(HubFiles.Long(cp.Value, "pid"), HubFiles.Long(cp.Value, "start_ticks"))) port = (int)cport;
            }
            if (port <= 0)
            {
                string cfgPath = HubFiles.ConfigPath(hubDir);
                if (File.Exists(cfgPath))
                {
                    string t = HubFiles.ReadShared(cfgPath);
                    if (t == null) throw QuitTool(103, L.T("config.json 을 읽지 못함(" + cfgPath + ")", "Could not read config.json (" + cfgPath + ")"));
                    // 문자열은 그대로 두고 // 줄 주석과 /* */ 블록 주석만 지운다(허브 파서는 주석을 건너뛴다: 주석 속 control_port 를 잡지 않게)
                    t = Regex.Replace(t, "(\"(?:[^\"\\\\]|\\\\.)*\")|//[^\\r\\n]*|/\\*[\\s\\S]*?\\*/", m => m.Groups[1].Success ? m.Value : " ");
                    var m2 = Regex.Match(t, "\"control_port\"\\s*:\\s*([^,}\\s]+)");
                    if (m2.Success) port = PortValue(m2.Groups[1].Value, L.T("config.json 의 control_port", "control_port in config.json"));
                }
            }
            return port > 0 ? port : 7200;
        }

        static int PortValue(string text, string what)
        {
            if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int p) || p < 1 || p > 65535)
                throw QuitTool(106, L.T(what + " 값이 잘못됨: '" + text + "' (1~65535 정수)", what + " is invalid: '" + text + "' (integer 1-65535)"));
            return p;
        }

        /// <summary>hub 명령이 감독에게 직접 가기 전: 감독이 없어 자가 복구할 상황이면 스풀에 써 봐야 10초 기다릴 뿐이다.</summary>
        static bool SelfHealDue()
            => fromInstall && !HubStopped() && !CoreMain.MutexExists(SystemAccess.MutexName("sup")) && !CoreMain.MutexExists(SystemAccess.MutexName("update"))
               && CoreMain.RespawnExe(out _) != null;

        // ================================================================== 감독 요청 스풀(코어 없이)
        static string SupervisorLine(string verb, string why, string[] args = null)
        {
            string reqDir = HubFiles.ReqDir(hubDir), respDir = HubFiles.RespDir(hubDir);
            if (!Directory.Exists(reqDir)) throw QuitTool(103, why + L.T(" — 감독 요청 폴더 없음(" + reqDir + ", 감독이 한 번도 실행되지 않음)", " — supervisor request folder missing (" + reqDir + ", the supervisor has never run)"));
            int pid = Environment.ProcessId;
            string id = "cli-" + pid + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string req = Path.Combine(reqDir, id + ".json"), tmp = req + ".tmp" + pid;
            var body = new JObj().Set("id", id).Set("verb", verb).Set("from", "cli").Set("pid", (long)pid);
            if (args != null) body.Set("args", args);
            File.WriteAllText(tmp, body.ToString(), Utf8);
            File.Move(tmp, req, true);
            string resp = Path.Combine(respDir, id + ".json");
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 10000)
            {
                Thread.Sleep(50);
                string t = HubFiles.ReadShared(resp);
                if (string.IsNullOrEmpty(t)) continue;
                try { using var d = JsonDocument.Parse(t); }
                catch (JsonException) { continue; }
                HubFiles.TryDelete(resp);
                return t.Trim();
            }
            HubFiles.TryDelete(req);
            throw QuitTool(103, why + L.T(" — 감독(UartHub.exe sup)도 10초 안에 응답하지 않음", " — the supervisor (UartHub.exe sup) did not respond within 10 s either"));
        }

        // ================================================================== connect·disconnect(X5 부록 B)
        const int ConnectPickupMs = 8000, ConnectReplyMs = 175000;
        /// <summary>진행 중인 connect 요청의 &lt;id&gt;.cancel 경로 — 기다리다 그만두면(시간 초과·--parent-pipe EOF) 이 파일을 써서 감독이 창을 닫고 취소하게 한다.</summary>
        static volatile string connectCancel;

        static void CancelConnect()
        {
            string c = connectCancel;
            if (c == null) return;
            try { File.WriteAllText(c, "requester gone pid=" + Environment.ProcessId + "\n", Utf8); } catch (Exception) { }
        }

        /// <summary>
        /// connect [COMx [baud]] · disconnect COMx → 감독 요청 스풀(args) → 응답 줄. 인자 오류 106(baud 는 Config.StandardBauds 만 — 허용 목록을 알려 줌). 감독이 없으면 C-9 조건(설치 기록·정지 아님·update 아님)에서
        /// 창 없이 sup 을 띄우고 최대 15초 기다린다. 감독이 요청을 8초 안에 가져가지 않거나 175초 안에 답하지 않으면 103.
        /// </summary>
        static string ConnectLine(string verb, ControlServer.ParsedArgs pa)
        {
            var pos = pa.Pos;
            var other = pa.Flags.Where(f => f != "--json").Concat(pa.Values.Keys).ToList();
            if (other.Count > 0) throw QuitTool(106, verb + L.T(": 쓸 수 없는 옵션 " + string.Join(" ", other) + " — connect [COMx [baud]] | disconnect COMx [--json]", ": unsupported option " + string.Join(" ", other) + " — connect [COMx [baud]] | disconnect COMx [--json]"));
            if (pos.Count > (verb == "connect" ? 2 : 1) || (verb == "disconnect" && pos.Count != 1)) throw QuitTool(106, L.T("사용: connect [COMx [baud]] | disconnect COMx [--json]", "Usage: connect [COMx [baud]] | disconnect COMx [--json]"));
            string com = pos.Count >= 1 ? pos[0].Trim().ToUpperInvariant() : null;
            if (com != null && !Regex.IsMatch(com, @"^COM\d+$")) throw QuitTool(106, verb + L.T(": COM 이름이 아님 '" + pos[0] + "' (예: COM8)", ": not a COM name '" + pos[0] + "' (e.g. COM8)"));
            string baud = null;
            if (pos.Count == 2)
            {
                if (!int.TryParse(pos[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int b) || !Config.IsStandardBaud(b))
                    throw QuitTool(106, L.T("baud 는 " + Config.StandardBaudText + " 중 하나: '" + pos[1] + "'", "baud must be one of " + Config.StandardBaudText + ": '" + pos[1] + "'"));
                baud = b.ToString(CultureInfo.InvariantCulture);
            }
            EnsureSupervisor();
            string reqDir = HubFiles.ReqDir(hubDir), respDir = HubFiles.RespDir(hubDir);
            int pid = Environment.ProcessId;
            string id = "cli-" + pid + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string req = Path.Combine(reqDir, id + ".json"), tmp = req + ".tmp" + pid, resp = Path.Combine(respDir, id + ".json");
            var body = new JObj().Set("id", id).Set("verb", verb).Set("from", "cli").Set("pid", (long)pid)
                .Set("args", com == null ? new string[0] : baud == null ? new[] { com } : new[] { com, baud });
            File.WriteAllText(tmp, body.ToString(), Utf8);
            connectCancel = Path.Combine(reqDir, id + ".cancel");
            File.Move(tmp, req, true);
            var sw = Stopwatch.StartNew();
            bool taken = false;
            while (true)
            {
                Thread.Sleep(50);
                string t = HubFiles.ReadShared(resp);
                if (!string.IsNullOrEmpty(t))
                {
                    try { using var d = JsonDocument.Parse(t); }
                    catch (JsonException) { continue; }
                    HubFiles.TryDelete(resp);
                    connectCancel = null;                                            // 답을 받음 — 이 뒤의 종료는 취소가 아니다
                    return t.Trim();
                }
                if (!taken && !File.Exists(req)) taken = true;
                if (!taken && sw.ElapsedMilliseconds >= ConnectPickupMs)
                {
                    HubFiles.TryDelete(req);
                    throw QuitTool(103, L.T("감독(트레이)이 " + (ConnectPickupMs / 1000) + "초 안에 요청을 가져가지 않음 — 트레이의 UART 허브 아이콘을 확인", "The supervisor (tray) did not pick up the request within " + (ConnectPickupMs / 1000) + " s — check the UART Hub icon in the tray"));
                }
                if (sw.ElapsedMilliseconds >= ConnectReplyMs)
                {
                    CancelConnect();                                                 // 감독이 확인 창·안내 창을 닫고 취소하게
                    throw QuitTool(103, L.T("감독이 " + (ConnectReplyMs / 1000) + "초 안에 " + verb + " 결과를 주지 않음 — 취소를 요청함(떠 있던 창은 닫힘)", "The supervisor gave no " + verb + " result within " + (ConnectReplyMs / 1000) + " s — cancellation requested (any open window is closed)"));
                }
            }
        }

        /// <summary>감독이 살아 있게(C-9 와 같은 조건으로 창 없이 sup 을 띄움). 안 되면 103.</summary>
        static void EnsureSupervisor()
        {
            bool Alive()
            {
                var j = HubFiles.ReadJson(HubFiles.SupState(hubDir));                   // sup.json 의 pid·시작 시각이 살아 있는 프로세스인가
                return j.HasValue && HubFiles.IsAlive(HubFiles.Long(j.Value, "pid"), HubFiles.Long(j.Value, "start_ticks")) && Directory.Exists(HubFiles.ReqDir(hubDir));
            }
            if (Alive()) return;
            if (HubStopped()) throw QuitTool(103, L.T("감독(트레이)이 실행 중이 아님", "The supervisor (tray) is not running"));
            if (!fromInstall) throw QuitTool(103, L.T("감독(트레이)이 실행 중이 아님 — UartHub.exe 를 실행하세요(UART_HUB_DIR 로 정한 허브 폴더라 다시 띄우지 않음)", "The supervisor (tray) is not running — run UartHub.exe (the hub folder was set by UART_HUB_DIR, so it is not restarted)"));
            if (CoreMain.MutexExists(SystemAccess.MutexName("update"))) throw QuitTool(103, L.T("업데이트 중(update mutex) — 끝난 뒤 다시", "Update in progress (update mutex) — try again after it finishes"));
            bool restarted = false;
            if (!CoreMain.MutexExists(SystemAccess.MutexName("sup")))
            {
                string exe = CoreMain.RespawnExe(out string why);
                if (exe == null) throw QuitTool(103, L.T("감독(트레이)이 실행 중이 아님 — 다시 띄우지 않음(" + why + ")", "The supervisor (tray) is not running — not restarting (" + why + ")"));
                NativeHandles.DontInheritStd();
                SystemAccess.StartDetached(exe, new[] { "sup" });
                restarted = true;
            }
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 15000)
            {
                Thread.Sleep(200);
                if (Alive()) return;
            }
            throw QuitTool(103, L.T("감독(트레이)이 15초 안에 뜨지 않음", "The supervisor (tray) did not start within 15 s"), restarted);
        }

        /// <summary>connect·disconnect 의 비JSON 출력(성공일 때 — 도구 오류는 공통 경로).</summary>
        static void PrintConnect(string cmd, JsonElement resp)
        {
            string S(JsonElement e, string k) { var v = Prop(e, k); return Truthy(v) ? PsStr(v) : null; }
            if (Eq(cmd, "disconnect"))
            {
                OutLine(S(resp, "com") + " " + (S(resp, "message") ?? L.T("끊음", "disconnected")));
                return;
            }
            if (Prop(resp, "cables").ValueKind == JsonValueKind.Array)
            {
                foreach (var c in Items(Prop(resp, "cables"))) OutLine((S(c, "com") ?? "?") + "  " + (S(c, "text") ?? S(c, "state") ?? ""));
                return;
            }
            var hc = Prop(resp, "holder_closed");
            OutLine(L.T(S(resp, "com") + " 연결됨 — 포트 " + S(resp, "port") + (S(resp, "board") != null ? " (" + S(resp, "board") + ")" : "")
                    + " · " + S(resp, "baud") + "bps" + (S(resp, "old_baud") != null ? "(" + S(resp, "old_baud") + "에서 바꿈)" : "")
                    + " · 터미널 " + (S(resp, "terminal") ?? "none") + " · 받은 바이트 " + (S(resp, "rx_bytes") ?? "0"),
                    S(resp, "com") + " connected — port " + S(resp, "port") + (S(resp, "board") != null ? " (" + S(resp, "board") + ")" : "")
                    + " · " + S(resp, "baud") + "bps" + (S(resp, "old_baud") != null ? " (changed from " + S(resp, "old_baud") + ")" : "")
                    + " · terminal " + (S(resp, "terminal") ?? "none") + " · bytes received " + (S(resp, "rx_bytes") ?? "0")));
            if (hc.ValueKind == JsonValueKind.Object) OutLine(L.T("끊은 세션: " + S(hc, "label") + "(pid " + S(hc, "pid") + ")", "Disconnected session: " + S(hc, "label") + " (pid " + S(hc, "pid") + ")"));
            if (S(resp, "terminal_note") != null) OutLine(S(resp, "terminal_note"));
            if (S(resp, "message") != null) OutLine(S(resp, "message"));
        }

        // ================================================================== K4 후속: 호출 쪽 생존(--parent-pipe)
        /// <summary>표준 입력 EOF(호출 쪽이 쥔 파이프가 닫힘) → 바로 끝낸다. 제어 연결이 닫혀 코어가 대기 요청을 취소한다.</summary>
        static void WatchParent()
        {
            var t = new Thread(() =>
            {
                try
                {
                    using var s = Console.OpenStandardInput();
                    var buf = new byte[256];
                    while (s.Read(buf, 0, buf.Length) > 0) { }
                }
                catch (Exception) { }
                CancelConnect();                                                     // connect 중이었으면 감독의 창도 닫게
                ErrLine(L.T("uart: E103 hub_down 호출 쪽이 끝남(--parent-pipe 표준 입력 EOF) — 요청을 취소하고 끝냄", "uart: E103 hub_down the caller exited (--parent-pipe stdin EOF) — request cancelled, exiting"));
                Environment.Exit(103);
            }) { IsBackground = true, Name = "ParentPipe" };
            t.Start();
        }

        // ================================================================== 응답 해석·출력 도우미
        static bool ExitCode(JsonElement e, out int code)
        {
            code = 0;
            string s = e.ValueKind == JsonValueKind.Number ? e.GetRawText() : e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            return s != null && Regex.IsMatch(s, "^\\d{1,3}$") && int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out code);
        }

        static JsonElement Prop(JsonElement o, string k)
            => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var v) ? v : default;

        static IEnumerable<JsonElement> Items(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Array) return e.EnumerateArray().ToList();
            if (e.ValueKind == JsonValueKind.Undefined || e.ValueKind == JsonValueKind.Null) return Array.Empty<JsonElement>();
            return new[] { e };
        }

        static void OutLines(JsonElement e) { foreach (var l in Items(e)) OutLine(PsStr(l)); }

        /// <summary>PowerShell 의 if(값) 판정(uart.ps1 과 같은 출력 분기).</summary>
        static bool Truthy(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.String: return e.GetString().Length > 0;
                case JsonValueKind.Number: return e.GetRawText().Trim('-', '0', '.').Length > 0;
                case JsonValueKind.True: return true;
                case JsonValueKind.Array: return e.GetArrayLength() > 0;
                case JsonValueKind.Object: return true;
                default: return false;
            }
        }

        /// <summary>PowerShell 의 [string] 변환(uart.ps1 의 비JSON 출력과 같게): null → "", true → "True", 배열 → 공백 연결, 객체 → @{k=v; …}.</summary>
        static string PsStr(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.String: return e.GetString();
                case JsonValueKind.Number: return e.GetRawText();
                case JsonValueKind.True: return "True";
                case JsonValueKind.False: return "False";
                case JsonValueKind.Array: return string.Join(" ", e.EnumerateArray().Select(PsStr));
                case JsonValueKind.Object: return "@{" + string.Join("; ", e.EnumerateObject().Select(p => p.Name + "=" + PsStr(p.Value))) + "}";
                default: return "";
            }
        }

        /// <summary>'X:\a\b' → wsl(&lt;mount&gt;x/a/b)·mixed(X:/a/b). 드라이브 경로가 아니면 그대로.</summary>
        static string ConvertPath(string s, string style, string mount)
        {
            var m = s == null ? Match.Empty : Regex.Match(s, "^([A-Za-z]):[\\\\/](.*)$", RegexOptions.Singleline);
            if (!m.Success) return s;
            return style == "wsl" ? mount + m.Groups[1].Value.ToLowerInvariant() + "/" + m.Groups[2].Value.Replace('\\', '/')
                                  : m.Groups[1].Value + ":/" + m.Groups[2].Value.Replace('\\', '/');
        }

        /// <summary>경로 필드의 'X:\a\b' 를 wsl(<mount>x/a/b)·mixed(X:/a/b) 로 바꿔 다시 직렬화한다(비ASCII 는 \u 이스케이프, Json.Str).</summary>
        static string Rewrite(JsonElement e, string style, string mount)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    return "{" + string.Join(",", e.EnumerateObject().Select(p =>
                    {
                        string v;
                        if (p.Value.ValueKind == JsonValueKind.String)
                        {
                            string s = p.Value.GetString();
                            if (Array.IndexOf(PathKeys, p.Name) >= 0) s = ConvertPath(s, style, mount);
                            v = Json.Str(s);
                        }
                        else if (p.Value.ValueKind == JsonValueKind.Array && Array.IndexOf(PathKeys, p.Name) >= 0)      // 경로 목록(aging_roots)
                            v = "[" + string.Join(",", p.Value.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? Json.Str(ConvertPath(x.GetString(), style, mount)) : Rewrite(x, style, mount))) + "]";
                        else v = Rewrite(p.Value, style, mount);
                        return Json.Str(p.Name) + ":" + v;
                    })) + "}";
                case JsonValueKind.Array:
                    return "[" + string.Join(",", e.EnumerateArray().Select(x => Rewrite(x, style, mount))) + "]";
                case JsonValueKind.String:
                    return Json.Str(e.GetString());
                default:
                    return e.GetRawText();
            }
        }
    }
}
