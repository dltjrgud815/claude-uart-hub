// 허브 상태(state.json). 코어 전용 writer(§3.8): 케이블→TCP 포트 배정, released 목록, 마지막 보드명.
// C-12: 모르는 최상위 키는 읽을 때 원문(순서 포함)을 보관했다가 저장할 때 아는 키 뒤에 그대로 다시 쓴다(새 판이 더한 키가 되돌림 뒤에도 남는다).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace UartHub
{
    public sealed class StateStore
    {
        readonly string path;
        readonly object sync = new object();
        readonly Dictionary<string, int> assign = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> released = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> boards = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly SortedSet<int> retired = new SortedSet<int>();          // 미연결 UART 정리로 지운 케이블의 번호 — 다시 주지 않는다(옛 즐겨찾기가 다른 케이블을 가리키지 않게)
        readonly List<KeyValuePair<string, string>> unknown = new List<KeyValuePair<string, string>>();
        static readonly string[] Known = { "assign", "released", "boards", "retired_ports" };

        /// <summary>로드 중 문제(깨진 파일 등). 깨진 파일은 .bad 로 보존하고 빈 상태로 시작한다.</summary>
        public string LoadWarning { get; private set; }

        public StateStore(string path)
        {
            this.path = path;
            Load();
        }

        void Load()
        {
            if (!File.Exists(path)) return;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                if (root.TryGetProperty("assign", out var a))
                    foreach (var p in a.EnumerateObject()) assign[p.Name] = p.Value.GetInt32();
                if (root.TryGetProperty("released", out var r))
                    foreach (var e in r.EnumerateArray()) released.Add(e.GetString());
                if (root.TryGetProperty("boards", out var b))
                    foreach (var p in b.EnumerateObject()) boards[p.Name] = p.Value.GetString();
                if (root.TryGetProperty("retired_ports", out var rp))
                    foreach (var e in rp.EnumerateArray()) retired.Add(e.GetInt32());
                foreach (var p in root.EnumerateObject())
                    if (Array.IndexOf(Known, p.Name) < 0) unknown.Add(new KeyValuePair<string, string>(p.Name, p.Value.GetRawText()));
            }
            catch (Exception e)
            {
                LoadWarning = "state.json 읽기 실패(" + e.Message + ") → .bad 로 보존";
                try { File.Copy(path, path + ".bad", true); } catch (Exception) { }
                assign.Clear(); released.Clear(); boards.Clear(); retired.Clear(); unknown.Clear();
            }
        }

        /// <summary>배정이 있으면 그 포트, 없으면 base_port부터 빈 포트를 배정하고 즉시 영속한다(정리로 지운 번호 retired_ports 는 건너뛴다 — 번호를 다시 쓰지 않음).</summary>
        public int GetOrAssignPort(string cableId, int basePort, int controlPort)
        {
            lock (sync)
            {
                if (assign.TryGetValue(cableId, out int p)) return p;
                var used = new HashSet<int>(assign.Values);
                used.UnionWith(retired);
                int port = basePort;
                while (used.Contains(port) || port == controlPort) port++;
                assign[cableId] = port;
                SaveLocked();
                return port;
            }
        }

        /// <summary>포트 배정이 있는가(C-3 "물어본 적이 있는가"의 한 축 — 첫 연결 "예" 또는 Phase A 의 붙임).</summary>
        public bool HasAssign(string cableId) { lock (sync) return assign.ContainsKey(cableId); }

        public bool IsReleased(string cableId) { lock (sync) return released.Contains(cableId); }

        public void SetReleased(string cableId, bool value)
        {
            lock (sync)
            {
                bool changed = value ? released.Add(cableId) : released.Remove(cableId);
                if (changed) SaveLocked();
            }
        }

        public string GetBoard(string cableId) { lock (sync) return boards.TryGetValue(cableId, out var b) ? b : null; }

        /// <summary>기억하는 케이블(포트 배정이 있는 것): id → (포트, 마지막 보드).</summary>
        public List<(string Id, int Port, string Board)> Remembered()
        {
            lock (sync) return assign.OrderBy(k => k.Value).Select(k => (k.Key, k.Value, boards.TryGetValue(k.Key, out var b) ? b : null)).ToList();
        }

        /// <summary>
        /// 미연결 UART 정리(트레이): 케이블의 assign·released·boards 를 지우고 그 번호를 retired_ports 에 남겨 즉시 영속한다(모르는 키는 그대로, C-12).
        /// 돌려주는 값 = 지운 포트(배정이 없었으면 -1 — released·boards 만 있었어도 지운다). 아무 기록도 없으면 -1 이고 쓰지 않는다.
        /// </summary>
        public int Forget(string cableId)
        {
            lock (sync)
            {
                int port = assign.TryGetValue(cableId, out int p) ? p : -1;
                bool changed = assign.Remove(cableId) | released.Remove(cableId) | boards.Remove(cableId);
                if (port > 0) retired.Add(port);
                if (changed) SaveLocked();
                return port;
            }
        }

        public void SetBoard(string cableId, string board)
        {
            lock (sync)
            {
                if (boards.TryGetValue(cableId, out var old) && old == board) return;
                boards[cableId] = board;
                SaveLocked();
            }
        }

        void SaveLocked()
        {
            var sb = new StringBuilder();
            sb.Append("{\n  \"assign\": {");
            sb.Append(string.Join(",", assign.OrderBy(k => k.Value).Select(k => "\n    " + Json.Str(k.Key) + ": " + k.Value)));
            sb.Append(assign.Count > 0 ? "\n  },\n" : "},\n");
            sb.Append("  \"released\": [").Append(string.Join(", ", released.OrderBy(x => x).Select(Json.Str))).Append("],\n");
            sb.Append("  \"boards\": {");
            sb.Append(string.Join(",", boards.OrderBy(k => k.Key).Select(k => "\n    " + Json.Str(k.Key) + ": " + Json.Str(k.Value))));
            sb.Append(boards.Count > 0 ? "\n  }" : "}");
            if (retired.Count > 0) sb.Append(",\n  \"retired_ports\": [").Append(string.Join(", ", retired)).Append("]");
            foreach (var u in unknown) sb.Append(",\n  ").Append(Json.Str(u.Key)).Append(": ").Append(u.Value);
            sb.Append("\n}\n");
            AtomicFile.WriteAllText(path, sb.ToString());
        }
    }
}
