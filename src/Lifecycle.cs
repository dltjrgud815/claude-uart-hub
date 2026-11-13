// 감독·코어 수명(계획 §3.8) 중 코어 쪽과 공용 부분: hub_dir 의 수명 파일, 감독 요청 스풀, CoreHost(코어 프로세스 수명).
//
// hub_dir 파일(파일별 단일 writer, 모두 임시 파일 → rename 원자 교체):
//   core.pid      코어 전용  {"pid","start_ticks"(프로세스 시작 UTC ticks),"version","config","control_port","control"}  — 입양 검증용
//   core.stop     감독 전용  {"pid","reason"}  — 그 pid 의 코어에 정상 종료(exit 0) 요청. 코어가 200ms 주기로 확인한다
//   control.json  감독 전용  {"stopped","by","at"}  — 사용자 정지 의도(영속). CLI 는 감독 요청 스풀을 거쳐 바꾼다
//   sup.json      감독 전용  감독 상태(pid·start_ticks·state·restarts·backoff·crash_loop·core_pid …)
//   sup\req\<id>.json → sup\resp\<id>.json   감독 요청 스풀(요청자: 코어 ControlServer 의 hub stop/start/upgrade, 코어가 없을 때 uart.ps1)
//   alive\<port>  코어(DiskWriter) 하트비트, state.json 코어 전용, config.json 사용자 전용, config.last-good.json 감독 전용
// 읽기는 FileShare.ReadWrite|Delete 로 연다(읽는 동안에도 writer 의 rename 교체가 막히지 않게).
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace UartHub
{
    public static class HubFiles
    {
        public static string CorePid(string hubDir) => Path.Combine(hubDir, "core.pid");
        public static string CoreStop(string hubDir) => Path.Combine(hubDir, "core.stop");
        public static string Control(string hubDir) => Path.Combine(hubDir, "control.json");
        public static string SupState(string hubDir) => Path.Combine(hubDir, "sup.json");
        public static string ConfigPath(string hubDir) => Path.Combine(hubDir, "config.json");
        public static string LastGood(string hubDir) => Path.Combine(hubDir, "config.last-good.json");
        public static string ReqDir(string hubDir) => Path.Combine(hubDir, "sup", "req");
        public static string RespDir(string hubDir) => Path.Combine(hubDir, "sup", "resp");
        public static string AliveDir(string hubDir) => Path.Combine(hubDir, "alive");

        /// <summary>파일 전체를 공유 읽기(없거나 실패하면 null).</summary>
        public static string ReadShared(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var r = new StreamReader(fs, new UTF8Encoding(false));
                return r.ReadToEnd();
            }
            catch (Exception) { return null; }
        }

        /// <summary>원자 교체 쓰기. 읽는 쪽이 공유 없이 열고 있으면 rename 이 실패할 수 있어 짧게 재시도한다.</summary>
        public static bool TryWriteAtomic(string path, string text, int attempts = 10)
        {
            for (int i = 0; i < attempts; i++)
            {
                try { AtomicFile.WriteAllText(path, text); return true; }
                catch (Exception) { Thread.Sleep(20); }
            }
            return false;
        }

        public static void TryDelete(string path)
        {
            try { File.Delete(path); } catch (Exception) { }
        }

        /// <summary>JSON 객체 파일 → 복제된 JsonElement(없거나 깨졌으면 null).</summary>
        public static JsonElement? ReadJson(string path)
        {
            string t = ReadShared(path);
            if (string.IsNullOrWhiteSpace(t)) return null;
            try
            {
                using var d = JsonDocument.Parse(t);
                return d.RootElement.ValueKind == JsonValueKind.Object ? d.RootElement.Clone() : (JsonElement?)null;
            }
            catch (JsonException) { return null; }
        }

        public static long Long(JsonElement e, string k, long dflt = 0)
            => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long x) ? x : dflt;

        public static string Str(JsonElement e, string k)
            => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        public static bool Bool(JsonElement e, string k)
            => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;

        /// <summary>프로세스 시작 시각(UTC ticks). PID 재사용을 가려내는 데 쓴다.</summary>
        public static long StartTicks(Process p) => p.StartTime.ToUniversalTime().Ticks;

        /// <summary>pid 가 살아 있고 시작 시각이 같은가(같은 프로세스인가).</summary>
        public static bool IsAlive(long pid, long startTicks)
        {
            if (pid <= 0) return false;
            try
            {
                using var p = Process.GetProcessById((int)pid);
                return !p.HasExited && StartTicks(p) == startTicks;
            }
            catch (Exception) { return false; }
        }

        /// <summary>control.json 의 stopped(없거나 깨졌으면 false).</summary>
        public static bool IsStopped(string hubDir)
        {
            var j = ReadJson(Control(hubDir));
            return j.HasValue && Bool(j.Value, "stopped");
        }

        public static string Iso(DateTime t) => t.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);

        /// <summary>JSON 객체 텍스트 → JObj(필드 순서·원문 값 유지).</summary>
        public static JObj ToJObj(JsonElement e)
        {
            var o = new JObj();
            foreach (var p in e.EnumerateObject()) o.Raw(p.Name, p.Value.GetRawText());
            return o;
        }
    }

    /// <summary>
    /// 감독 요청 스풀(파일). 요청자는 sup\req\&lt;id&gt;.json 을 원자적으로 만들고 sup\resp\&lt;id&gt;.json 을 기다린다.
    /// 감독은 틱마다(트레이 Timer 또는 -NoTray 루프, PS 스레드) 요청을 처리한다. 응답 = 제어 응답과 같은 JSON 객체(status·exit …).
    /// TCP 포트·토큰을 따로 두지 않으려고 파일로 한다(hub_dir 은 사용자 전용 폴더).
    /// </summary>
    public static class SupervisorChannel
    {
        public static JObj Request(string hubDir, string verb, string from, int timeoutMs, string[] args = null)
        {
            string id = from + "-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string req = Path.Combine(HubFiles.ReqDir(hubDir), id + ".json");
            string resp = Path.Combine(HubFiles.RespDir(hubDir), id + ".json");
            var body = new JObj().Set("id", id).Set("verb", verb).Set("from", from).Set("pid", (long)Environment.ProcessId).Set("at", HubFiles.Iso(DateTime.Now));
            if (args != null && args.Length > 0) body.Set("args", args);
            try { AtomicFile.WriteAllText(req, body.ToString()); }
            catch (Exception e) { return Err(103, "hub_down", L.T("감독 요청 파일을 쓰지 못함: " + e.Message, "Could not write the supervisor request file: " + e.Message)); }
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                Thread.Sleep(40);
                string t = HubFiles.ReadShared(resp);
                if (string.IsNullOrWhiteSpace(t)) continue;
                try
                {
                    using var d = JsonDocument.Parse(t);
                    HubFiles.TryDelete(resp);
                    return HubFiles.ToJObj(d.RootElement);
                }
                catch (JsonException) { /* 쓰는 중 — 다음 회차 */ }
            }
            bool taken = !File.Exists(req);
            HubFiles.TryDelete(req);
            return Err(103, "hub_down", taken ? L.T("감독이 요청을 가져갔지만 " + timeoutMs + "ms 안에 응답하지 않음", "The supervisor took the request but did not reply within " + timeoutMs + "ms")
                                              : L.T("감독(uart_hub.ps1) 미응답 — 감독이 실행 중인지 확인", "No reply from the supervisor (uart_hub.ps1) — check that it is running"));
        }

        static JObj Err(long code, string status, string msg) => new JObj().Set("status", status).Set("exit", code).Set("error", msg);

        /// <summary>sup.json + 생존 확인(pid·시작 시각) + control.json 의 stopped. sup.json 이 없으면 null.</summary>
        public static JObj Status(string hubDir)
        {
            var j = HubFiles.ReadJson(HubFiles.SupState(hubDir));
            if (!j.HasValue) return null;
            var o = HubFiles.ToJObj(j.Value);
            o.Set("alive", HubFiles.IsAlive(HubFiles.Long(j.Value, "pid"), HubFiles.Long(j.Value, "start_ticks")));
            o.Set("stopped", HubFiles.IsStopped(hubDir));
            return o;
        }
    }

    /// <summary>
    /// 코어 프로세스 수명(hub_core.ps1 이 쓴다, §3.8):
    /// core.pid 기록(PID + 프로세스 시작 시각), AppDomain.UnhandledException → 큐 flush + 모든 케이블 로그에 *** hub crash *** → exit 70,
    /// ControlServer 의 hub stop/start/upgrade → 감독 요청 스풀, hub status 의 supervisor 필드, core.stop 확인(정상 종료 → exit 0).
    /// 모든 처리기는 C# 이다(C# 스레드에서 PS scriptblock 을 부르지 않는다).
    /// </summary>
    public sealed class CoreHost
    {
        /// <summary>UnhandledException 으로 끝날 때의 종료코드(감독은 0 이 아니면 재시작한다).</summary>
        public const int CrashExitCode = 70;
        /// <summary>기동 실패(DLL·설정·Hub 생성) 종료코드 — hub_core.ps1 이 쓴다.</summary>
        public const int StartFailedExitCode = 2;
        /// <summary>코어 mutex 를 이미 다른 코어가 잡고 있음 — hub_core.ps1 이 쓴다(감독은 재시작 대신 입양을 시도).</summary>
        public const int CoreBusyExitCode = 3;

        readonly Hub hub;
        readonly string hubDir, configPath;
        readonly int pid;
        readonly long startTicks;
        readonly DateTime startLocal;
        int crashing;

        CoreHost(Hub hub, string hubDir, string configPath)
        {
            this.hub = hub; this.hubDir = hubDir; this.configPath = configPath;
            using var me = Process.GetCurrentProcess();
            pid = me.Id;
            startTicks = HubFiles.StartTicks(me);
            startLocal = me.StartTime;
        }

        public static CoreHost Install(Hub hub, string hubDir, string configPath)
        {
            var h = new CoreHost(hub, hubDir, configPath);
            AppDomain.CurrentDomain.UnhandledException += h.OnUnhandled;
            hub.Control.HubCommandHandler = (verb, args) => SupervisorChannel.Request(hubDir, verb, "core", 8000, args);
            hub.Control.SupervisorStatus = () => SupervisorChannel.Status(hubDir);
            h.WritePid("starting");
            return h;
        }

        /// <summary>hub.Start() 뒤: 제어 포트 bind 결과를 core.pid 에 반영(감독은 bind 실패면 ping 판정을 건너뛴다).</summary>
        public void Started()
        {
            var be = hub.Control.BindError;
            WritePid(be == null ? "listening" : "bind_failed: " + be);
        }

        void WritePid(string control)
        {
            var o = new JObj().Set("pid", (long)pid).Set("start_ticks", startTicks).Set("started", HubFiles.Iso(startLocal))
                .Set("version", Hub.Version).Set("config", configPath).Set("control_port", (long)hub.Config.ControlPort).Set("control", control)
                .Set("restart_reason", hub.RestartReason);
            if (!HubFiles.TryWriteAtomic(HubFiles.CorePid(hubDir), o.ToString())) hub.Log.Write(L.T("core.pid 쓰기 실패", "core.pid write failed"));
        }

        /// <summary>감독이 이 코어에 정상 종료를 요청했는가(core.stop 의 pid == 내 pid).</summary>
        public bool StopRequested
        {
            get
            {
                var j = HubFiles.ReadJson(HubFiles.CoreStop(hubDir));
                return j.HasValue && HubFiles.Long(j.Value, "pid") == pid;
            }
        }

        /// <summary>정상 종료 마무리: 내 core.pid·core.stop 만 지운다(남은 core.pid = 비정상 종료 흔적).</summary>
        public void Exit()
        {
            var j = HubFiles.ReadJson(HubFiles.CorePid(hubDir));
            if (j.HasValue && HubFiles.Long(j.Value, "pid") == pid) HubFiles.TryDelete(HubFiles.CorePid(hubDir));
            if (StopRequested) HubFiles.TryDelete(HubFiles.CoreStop(hubDir));
            hub.Log.Write(L.T("코어 정상 종료(exit 0)", "Core exited cleanly (exit 0)"));
        }

        void OnUnhandled(object sender, UnhandledExceptionEventArgs e)
        {
            if (Interlocked.Exchange(ref crashing, 1) != 0) return;         // 처리 중 다른 스레드의 예외는 무시
            var ex = e.ExceptionObject as Exception;
            string what = ex == null ? "unknown" : ex.GetType().Name + ": " + (ex.Message ?? "").Replace('\r', ' ').Replace('\n', ' ');
            if (what.Length > 200) what = what.Substring(0, 200) + "…";
            try { hub.Log.Write("hub crash: " + (ex?.ToString() ?? "unknown")); } catch (Exception) { }
            try { foreach (var c in hub.Cables.Snapshot()) c.PostEvent("*** hub crash (" + what + ") ***"); } catch (Exception) { }
            bool drained = false;
            try { drained = hub.WaitDrained(3000); } catch (Exception) { }
            try { hub.Log.Write(L.T("hub crash: 큐 flush " + (drained ? "완료" : "시간 초과") + " → exit " + CrashExitCode, "hub crash: queue flush " + (drained ? "done" : "timed out") + " → exit " + CrashExitCode)); } catch (Exception) { }
            Environment.Exit(CrashExitCode);   // 런타임 기본 처리(WER 보고)보다 빠르고 종료코드가 정해진다
        }
    }
}
