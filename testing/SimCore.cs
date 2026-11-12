// 코어 프로세스 안 시험 모드(hub_core.ps1 -Sim <사양.json>): 실제 COM·WMI 대신 FakeBoard 를 코어 프로세스 안에 만들고,
// 수명 시나리오(20·33·hang)가 파일로 고장을 주입한다. 이 DLL(UartHub.Tests.*)은 -Sim 일 때만 코어에 로드된다.
//   사양: {"boards":[{"cable":"SIMCAB20","com":"COM320","hostname":"SIM20","printk_ms":300}]}
//   주입: <hub_dir>\sim\inject.<코어 PID> 에 명령 한 줄 → 처리 뒤 sim\ack.<PID> 에 같은 명령
//         pause-writers  모든 DiskWriter 정지(alive 하트비트 멈춤, ping 은 응답)
//         stop-control   ControlServer 정지(ping 실패, 하트비트는 계속)
//         crash          백그라운드 스레드 예외(AppDomain.UnhandledException → *** hub crash *** → exit 70)
//   <hub_dir>\sim\crash_on_start 가 있으면 기동 뒤 그 내용(ms, 기본 300) 만큼 지나 crash(크래시 루프 시험).
//   US-108(icons-timing·icons-reconcile): 사양의 보드에 "plugged": false 면 처음에는 꽂지 않는다. 주입 "plug <cable>"·"unplug <cable>" 로 꽂고 뽑는다.
//   US-112(X5 connect): 시험 모드(UART_HUB_TEST_ROOT)면 <root>\fake_holders.json 에 그 COM 의 점유자가 있는 동안 열기가 UnauthorizedAccessException
//     (= held_by_other, 실제 SerialPort 와 같은 예외). 감독의 가짜 끝내기(ComHolders)가 항목을 지우면 다음 스캔에서 열린다.
//   US-113(X6 사건): 주입 "emit <cable> <base64(UTF-8)>" = 그 보드가 그 바이트를 그대로 출력(panic·앱 오류 줄 흉내), "reset <cable>" = 와치독식 리셋(hard 재부팅).
//   자동 깨우기 F2: "noise <cable> <base64>" = 허브를 거치지 않는 보드 tty 입력(다른 터미널에서 치다 만 글자 흉내, FakeBoard.InjectNoise),
//     "silent <cable> on|off" = 보드가 입력을 무시(응답 없는 보드 흉내, FakeBoard.Silent).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace UartHub.Testing
{
    public static class SimCore
    {
        static FakePortProvider simProv;
        static readonly Dictionary<string, (FakeBoard Board, string Com)> simBoards = new Dictionary<string, (FakeBoard, string)>(StringComparer.OrdinalIgnoreCase);

        public static IPortProvider[] Providers(string specPath)
        {
            var prov = new FakePortProvider();
            using var d = JsonDocument.Parse(File.ReadAllText(specPath));
            foreach (var b in d.RootElement.GetProperty("boards").EnumerateArray())
            {
                var opt = new FakeBoardOptions { Hostname = Str(b, "hostname") ?? "SIM" };
                if (b.TryGetProperty("printk_ms", out var pk)) opt.PrintkIntervalMs = pk.GetInt32();
                var board = new FakeBoard(opt);
                lock (simBoards) simBoards[Str(b, "cable")] = (board, Str(b, "com"));
                if (!(b.TryGetProperty("plugged", out var pl) && pl.ValueKind == JsonValueKind.False)) prov.Plug(board, Str(b, "cable"), Str(b, "com"));
            }
            simProv = prov;
            if (SystemAccess.IsTest) return new IPortProvider[] { new HeldAwareProvider(prov, SystemAccess.TestFile("fake_holders.json")) };
            return new IPortProvider[] { prov };
        }

        /// <summary>시험 점유자(US-112): fake_holders.json 에 COM 이 있으면 그 COM 의 열기가 UnauthorizedAccessException. 열거는 안쪽 공급자 그대로(공급자만 이것으로).</summary>
        sealed class HeldAwareProvider : IPortProvider
        {
            readonly IPortProvider inner;
            readonly string file;
            public HeldAwareProvider(IPortProvider inner, string file) { this.inner = inner; this.file = file; }

            public IReadOnlyList<PortInfo> Scan()
            {
                var l = new List<PortInfo>();
                foreach (var p in inner.Scan()) l.Add(new PortInfo { CableId = p.CableId, Com = p.Com, Description = p.Description, Provider = this });
                return l;
            }

            public IPort Create(PortInfo info, int baud, Config cfg) => Held(info.Com) ? new HeldPort(info.Com) : inner.Create(info, baud, cfg);

            bool Held(string com)
            {
                var j = HubFiles.ReadJson(file);
                return com != null && j.HasValue && j.Value.TryGetProperty(com, out var a) && a.ValueKind == JsonValueKind.Array && a.GetArrayLength() > 0;
            }
        }

        sealed class HeldPort : IPort
        {
            public HeldPort(string com) { Name = com; }
            public string Name { get; }
            public bool IsOpen => false;
            public void Open() => throw new UnauthorizedAccessException("Access to the port '" + Name + "' is denied.");
            public int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("열리지 않은 포트");
            public void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException("열리지 않은 포트");
            public void SendBreak(int ms) { }
            public void Close() { }
            public void Dispose() { }
        }

        /// <summary>주입 plug|unplug &lt;cable&gt;(US-108). 처리했으면 true.</summary>
        static bool PlugCmd(Hub hub, string cmd)
        {
            var parts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || (parts[0] != "plug" && parts[0] != "unplug") || simProv == null) return false;
            (FakeBoard Board, string Com) b;
            lock (simBoards) if (!simBoards.TryGetValue(parts[1], out b)) { hub.Log.Write("sim: 모르는 케이블 " + parts[1]); return true; }
            if (parts[0] == "plug") simProv.Plug(b.Board, parts[1], b.Com);
            else simProv.Unplug(parts[1], new IOException("sim unplug"));
            hub.Log.Write("sim: " + parts[0] + " " + parts[1] + " " + b.Com);
            return true;
        }

        /// <summary>주입 emit &lt;cable&gt; &lt;base64&gt; | reset &lt;cable&gt;(US-113). 처리했으면 true.</summary>
        static bool BoardCmd(Hub hub, string cmd)
        {
            var parts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || Array.IndexOf(new[] { "emit", "reset", "noise", "silent" }, parts[0]) < 0) return false;
            (FakeBoard Board, string Com) b;
            lock (simBoards) if (!simBoards.TryGetValue(parts[1], out b)) { hub.Log.Write("sim: 모르는 케이블 " + parts[1]); return true; }
            if (parts[0] == "reset") { b.Board.Reset(); hub.Log.Write("sim: reset " + parts[1]); return true; }
            if (parts[0] == "silent")
            {
                b.Board.Silent = parts.Length > 2 && parts[2] == "on";
                hub.Log.Write("sim: silent " + parts[1] + " " + (b.Board.Silent ? "on" : "off"));
                return true;
            }
            if (parts.Length != 3) { hub.Log.Write("sim: emit <cable> <base64> 형식 아님"); return true; }
            byte[] data;
            try { data = Convert.FromBase64String(parts[2]); } catch (FormatException) { hub.Log.Write("sim: emit base64 아님"); return true; }
            if (parts[0] == "noise") { b.Board.InjectNoise(data); hub.Log.Write("sim: noise " + parts[1] + " " + data.Length + "B"); return true; }
            b.Board.EmitBytes(data);
            hub.Log.Write("sim: emit " + parts[1] + " " + data.Length + "B");
            return true;
        }

        static string Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        public static void Attach(Hub hub, string specPath)
        {
            string dir = Path.Combine(hub.Config.EffectiveHubDir, "sim");
            Directory.CreateDirectory(dir);
            int pid = Environment.ProcessId;
            hub.Log.Write("sim: 코어 안 FakeBoard 시험 모드 pid=" + pid + " spec=" + specPath);
            string cos = Path.Combine(dir, "crash_on_start");
            if (File.Exists(cos))
            {
                int ms = 300;
                try { int.TryParse(File.ReadAllText(cos).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ms); } catch (Exception) { }
                if (ms <= 0) ms = 300;
                hub.Log.Write("sim: crash_on_start " + ms + "ms");
                new Thread(() => { Thread.Sleep(ms); Crash("sim crash_on_start"); }) { IsBackground = true, Name = "SimCrashOnStart" }.Start();
            }
            new Thread(() => Watch(hub, dir, pid)) { IsBackground = true, Name = "SimInject" }.Start();
        }

        static void Watch(Hub hub, string dir, int pid)
        {
            string f = Path.Combine(dir, "inject." + pid.ToString(CultureInfo.InvariantCulture));
            string ack = Path.Combine(dir, "ack." + pid.ToString(CultureInfo.InvariantCulture));
            while (true)
            {
                Thread.Sleep(50);
                if (!File.Exists(f)) continue;
                string cmd;
                try { cmd = File.ReadAllText(f).Trim(); File.Delete(f); }
                catch (Exception) { continue; }
                hub.Log.Write("sim inject: " + cmd);
                switch (cmd)
                {
                    case "pause-writers":
                        var paused = new List<string>();
                        foreach (var c in hub.Cables.Snapshot())
                            if (c.Writer != null && c.Writer.TestPause(2000)) paused.Add(c.Id);
                        hub.Log.Write("sim: writer 정지 " + string.Join(",", paused));
                        break;
                    case "stop-control":
                        hub.Control.Stop();
                        break;
                    case "crash":
                        Crash("sim crash");
                        break;
                    default:
                        if (!PlugCmd(hub, cmd) && !BoardCmd(hub, cmd)) hub.Log.Write("sim: 모르는 주입 " + cmd);
                        break;
                }
                try { File.WriteAllText(ack, cmd); } catch (Exception) { }
            }
        }

        static void Crash(string msg)
        {
            new Thread(() => throw new InvalidOperationException(msg)) { IsBackground = true, Name = "SimCrash" }.Start();
        }
    }
}
