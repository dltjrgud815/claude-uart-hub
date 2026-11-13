// 미연결 UART 정리(트레이 설정 ▸ 미연결 UART 정리…): 지금 PC 에 꽂혀 있지 않은데 state.json 에 포트 번호가 남은 케이블(예: 일련번호 없는 4채널 어댑터를
//   다른 USB 자리에 꽂아 생긴 새 id 의 옛 기록)을 보여 주고, 사람이 고른 것만 코어의 cable forget 으로 지운다. CLI 는 C-4 로 cable 을 막으므로 Claude 는 부르지 못한다.
//   COM 은 이번 코어 실행에서 본 것, 없으면 사건 기록 jsonl 끝의 마지막 연결(attach) 기록에서.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace UartHub
{
    /// <summary>기억된 미연결 UART 한 줄(트레이 창).</summary>
    public sealed class RememberedCable
    {
        public string Id;
        public int Port;
        public string Board, Com;
    }

    public static class CableCleanup
    {
        /// <summary>코어의 cable remembered. 허브가 꺼져 있거나 답이 없으면 null + error.</summary>
        public static List<RememberedCable> List(int controlPort, string tokenPath, string eventsJsonlDir, out string error)
        {
            error = null;
            string l = ControlClient.CallWithTokenFile(controlPort, tokenPath, 5000, "[\"cable\",\"remembered\"]");
            if (l == null) { error = L.T("허브가 꺼져 있어 기록을 읽지 못했습니다.", "The hub is not running, so the records could not be read."); return null; }
            try
            {
                using var d = JsonDocument.Parse(l);
                var o = d.RootElement;
                if (HubFiles.Str(o, "status") != "ok" || !o.TryGetProperty("cables", out var cs) || cs.ValueKind != JsonValueKind.Array)
                { error = L.T("허브가 기록을 주지 않았습니다(" + (HubFiles.Str(o, "message") ?? HubFiles.Str(o, "status") ?? "?") + ").", "The hub returned no records (" + (HubFiles.Str(o, "message") ?? HubFiles.Str(o, "status") ?? "?") + ")."); return null; }
                return cs.EnumerateArray().Select(c => new RememberedCable
                {
                    Id = HubFiles.Str(c, "cable"), Port = (int)HubFiles.Long(c, "port"), Board = HubFiles.Str(c, "board"),
                    Com = HubFiles.Str(c, "com") ?? LastCom(eventsJsonlDir, (int)HubFiles.Long(c, "port")),
                }).Where(r => r.Id != null).ToList();
            }
            catch (JsonException e) { error = L.T("허브의 답을 읽지 못했습니다(" + e.Message + ").", "Could not read the hub's reply (" + e.Message + ")."); return null; }
        }

        /// <summary>고른 케이블마다 코어의 cable forget. 돌려주는 값 = 지운 수, 실패가 있으면 error(첫 이유).</summary>
        public static int Forget(int controlPort, string tokenPath, IEnumerable<string> ids, out string error)
        {
            error = null;
            int n = 0;
            foreach (var id in ids ?? Enumerable.Empty<string>())
            {
                string l = ControlClient.CallWithTokenFile(controlPort, tokenPath, 5000, "[\"cable\",\"forget\"," + Json.Str(id) + "]");
                string st = null, msg = null;
                try { if (l != null) { using var d = JsonDocument.Parse(l); st = HubFiles.Str(d.RootElement, "status"); msg = HubFiles.Str(d.RootElement, "message"); } }
                catch (JsonException) { }
                if (st == "ok") n++;
                else error ??= l == null ? L.T("허브가 꺼져 있어 기록을 지우지 못했습니다.", "The hub is not running, so the records could not be deleted.") : L.T("기록을 지우지 못했습니다(" + (msg ?? st ?? "?") + ").", "Could not delete the records (" + (msg ?? st ?? "?") + ").");
            }
            return n;
        }

        /// <summary>사건 기록 &lt;port&gt;.jsonl 끝 256KB 에서 마지막 연결 기록의 COM(없으면 null).</summary>
        public static string LastCom(string dir, int port)
        {
            if (string.IsNullOrEmpty(dir) || port <= 0) return null;
            try
            {
                string f = EventLog.FileOf(dir, port);
                if (!File.Exists(f)) return null;
                using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                long start = Math.Max(0, fs.Length - 262144);
                fs.Position = start;
                var buf = new byte[fs.Length - start];
                int got = 0;
                while (got < buf.Length) { int k = fs.Read(buf, got, buf.Length - got); if (k <= 0) break; got += k; }
                var lines = Encoding.UTF8.GetString(buf, 0, got).Split('\n');
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    var r = EventRec.Parse(lines[i]);
                    if (r != null && r.Kind == "attach" && r.Com != null) return r.Com;
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            return null;
        }
    }
}
