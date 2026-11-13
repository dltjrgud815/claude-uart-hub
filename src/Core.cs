// UART 허브 공통 기반: 시계·포트 추상화·원자적 파일 쓰기·JSON 이스케이프·허브 진단 로그.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace UartHub
{
    /// <summary>시각 공급자. 판정은 모두 이 시계로 한다(테스트는 ManualClock/ShiftClock 주입).</summary>
    public interface IClock
    {
        /// <summary>벽시계(로컬). 파일명·줄 타임스탬프·자정 전환에 쓴다.</summary>
        DateTime Now { get; }
        /// <summary>단조 증가 ms. 경과초·RX 간격·tail flush 판정에 쓴다.</summary>
        long MonoMs { get; }
    }

    public sealed class SystemClock : IClock
    {
        public static readonly SystemClock Instance = new SystemClock();
        static readonly Stopwatch Sw = Stopwatch.StartNew();
        public DateTime Now => DateTime.Now;
        public long MonoMs => Sw.ElapsedMilliseconds;
    }

    /// <summary>
    /// 시리얼 포트 추상화. 실제는 SerialPortAdapter, 시뮬은 FakeBoard.
    /// Read: 타임아웃이면 0, 장치가 사라지면 예외(타입 무관 → detach, S0-6).
    /// </summary>
    public interface IPort : IDisposable
    {
        string Name { get; }
        bool IsOpen { get; }
        void Open();
        int Read(byte[] buffer, int offset, int count);
        void Write(byte[] buffer, int offset, int count);
        void SendBreak(int ms);
        void Close();
    }

    /// <summary>열거 결과 한 건: 케이블 ID ↔ 현재 COM 이름.</summary>
    public sealed class PortInfo
    {
        public string CableId;
        public string Com;
        public string Description;
        public IPortProvider Provider;
        public override string ToString() => CableId + "@" + Com;
    }

    /// <summary>IPort 공급자. 실제는 WmiPortProvider(VID_0403 2초 폴링), 시뮬은 FakePortProvider.</summary>
    public interface IPortProvider
    {
        IReadOnlyList<PortInfo> Scan();
        IPort Create(PortInfo info, int baud, Config cfg);
    }

    public static class AtomicFile
    {
        /// <summary>임시 파일에 쓴 뒤 rename으로 교체한다(§3.8 파일별 단일 writer + 원자 교체).</summary>
        public static void WriteAllText(string path, string text)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
                         + "_" + Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture);
            try
            {
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                File.Move(tmp, path, true);
            }
            catch (Exception)
            {
                // 쓰기·교체 실패(디스크 가득, 대상을 다른 프로세스가 공유 없이 열고 있음 등): 임시 파일을 남기지 않는다. 호출자가 다음 주기에 다시 쓴다.
                try { File.Delete(tmp); } catch (Exception) { }
                throw;
            }
        }
    }

    public static class Json
    {
        /// <summary>JSON 문자열 리터럴(따옴표 포함). 비ASCII는 \u+16진 4자리로 이스케이프한다.</summary>
        public static string Str(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c > 0x7e) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        public static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>순서를 지키는 JSON 객체 작성기(제어 응답 한 줄). 같은 키를 다시 넣으면 값을 바꾼다. 값은 이미 직렬화된 JSON 조각이다.</summary>
    public sealed class JObj
    {
        readonly List<KeyValuePair<string, string>> f = new List<KeyValuePair<string, string>>();

        public JObj Raw(string k, string json)
        {
            int i = f.FindIndex(x => x.Key == k);
            var kv = new KeyValuePair<string, string>(k, json);
            if (i >= 0) f[i] = kv; else f.Add(kv);
            return this;
        }
        public JObj Set(string k, string v) => Raw(k, Json.Str(v));
        public JObj Set(string k, long v) => Raw(k, v.ToString(CultureInfo.InvariantCulture));
        public JObj Set(string k, double v) => Raw(k, Json.Num(v));
        public JObj Set(string k, bool v) => Raw(k, v ? "true" : "false");
        public JObj Set(string k, JObj v) => Raw(k, v == null ? "null" : v.ToString());
        public JObj Set(string k, IEnumerable<string> v) => Raw(k, v == null ? "null" : "[" + string.Join(",", System.Linq.Enumerable.Select(v, Json.Str)) + "]");
        public JObj Set(string k, IEnumerable<long> v) => Raw(k, "[" + string.Join(",", System.Linq.Enumerable.Select(v, x => x.ToString(CultureInfo.InvariantCulture))) + "]");
        public JObj Set(string k, IEnumerable<JObj> v) => Raw(k, "[" + string.Join(",", System.Linq.Enumerable.Select(v, x => x.ToString())) + "]");
        public JObj Null(string k) => Raw(k, "null");

        public override string ToString()
        {
            var sb = new StringBuilder("{");
            for (int i = 0; i < f.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Json.Str(f[i].Key)).Append(':').Append(f[i].Value);
            }
            return sb.Append('}').ToString();
        }
    }

    /// <summary>코어 진단 로그(hub_dir\hub.log). 감독도 같은 파일에 append 하므로 ReadWrite 공유로 연다.</summary>
    public sealed class HubLog
    {
        readonly string path;
        readonly IClock clock;
        readonly string tag;
        readonly object sync = new object();
        /// <param name="tag">줄 머리 출처 표시: 코어 "core", 감독 "sup".</param>
        public HubLog(string path, IClock clock, string tag = "core") { this.path = path; this.clock = clock; this.tag = tag; }
        public string Path => path;

        public void Write(string msg)
        {
            string line = "[" + clock.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "] [" + tag + "] " + msg + "\n";
            lock (sync)
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    byte[] b = Encoding.UTF8.GetBytes(line);
                    fs.Write(b, 0, b.Length);
                }
                catch (Exception) { /* 진단 로그 실패는 기록 코어를 멈추지 않는다 */ }
            }
        }

        readonly Dictionary<string, (long Ms, int Skipped)> throttle = new Dictionary<string, (long, int)>(StringComparer.Ordinal);

        /// <summary>
        /// 같은 key 는 intervalMs 에 한 줄만 쓴다(반복 예외가 hub.log 를 채우거나 호출 스레드를 붙잡지 않게). 생략한 수는 다음 줄에 붙인다.
        /// </summary>
        public void WriteThrottled(string key, string msg, int intervalMs = 10000)
        {
            int skipped;
            lock (throttle)
            {
                long now = Environment.TickCount64;
                if (throttle.TryGetValue(key, out var t) && now - t.Ms < intervalMs) { throttle[key] = (t.Ms, t.Skipped + 1); return; }
                skipped = t.Skipped;
                if (throttle.Count > 256) throttle.Clear();
                throttle[key] = (now, 0);
            }
            Write(skipped > 0 ? L.T(msg + " (그 사이 같은 오류 " + skipped + "건 생략)", msg + " (" + skipped + " identical errors skipped meanwhile)") : msg);
        }
    }

    public static class Names
    {
        /// <summary>파일명에 쓸 수 없는 문자를 '-'로 바꾼다(보드명·라벨용).</summary>
        public static string SafeLabel(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unknown";
            var bad = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append(Array.IndexOf(bad, c) >= 0 || c == '&' || c == ' ' ? '-' : c);
            return sb.ToString();
        }
    }
}
