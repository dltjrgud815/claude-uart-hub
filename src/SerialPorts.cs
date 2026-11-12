// 실제 하드웨어 경로: System.IO.Ports.SerialPort(S0-6 탈착 생존 확인) + WMI FTDI 열거.
// 시뮬레이션에서는 쓰지 않는다(vids: [], FakeBoard 만 사용).
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading;

namespace UartHub
{
    public sealed class SerialPortAdapter : IPort
    {
        readonly string com;
        readonly int baud, readTimeoutMs, readBufferBytes;
        SerialPort sp;

        public SerialPortAdapter(string com, int baud, int readTimeoutMs, int readBufferBytes)
        {
            this.com = com; this.baud = baud; this.readTimeoutMs = readTimeoutMs; this.readBufferBytes = readBufferBytes;
        }

        public string Name => com;
        public bool IsOpen => sp != null && sp.IsOpen;

        public void Open()
        {
            var p = new SerialPort(com, baud, Parity.None, 8, StopBits.One)
            {
                Handshake = Handshake.None,
                DtrEnable = false,          // 보드 리셋 방지: DTR/RTS deassert 고정
                RtsEnable = false,
                ReadTimeout = readTimeoutMs,
                WriteTimeout = 2000,
                ReadBufferSize = readBufferBytes,
            };
            p.Open();
            sp = p;
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            var p = sp ?? throw new InvalidOperationException("port closed");
            try { return p.BaseStream.Read(buffer, offset, count); }
            catch (TimeoutException) { return 0; }
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            var p = sp ?? throw new InvalidOperationException("port closed");
            p.BaseStream.Write(buffer, offset, count);
        }

        public void SendBreak(int ms)
        {
            var p = sp ?? throw new InvalidOperationException("port closed");
            p.BreakState = true;
            try { Thread.Sleep(ms); } finally { p.BreakState = false; }
        }

        public void Close()
        {
            var p = sp;
            if (p == null) return;
            try { p.Close(); } catch (Exception) { }
        }

        public void Dispose()
        {
            Close();
            try { sp?.Dispose(); } catch (Exception) { }
            sp = null;
        }
    }

    /// <summary>Win32_PnPEntity 에서 FTDI(VID 필터) 를 찾아 케이블ID ↔ COM 을 만든다.</summary>
    public sealed class WmiPortProvider : IPortProvider
    {
        static readonly Regex ComRe = new Regex(@"\((COM\d+)\)", RegexOptions.Compiled);
        readonly List<string> vids;

        public WmiPortProvider(IEnumerable<string> vids) { this.vids = vids.ToList(); }

        /// <summary>
        /// FTDIBUS\VID_0403+PID_6001+FT1ABCDE\0000 → FT1ABCDE,
        /// FTDIBUS\VID_0403+PID_6011+6&amp;1A2B3C4D&amp;0&amp;3\0000 → 6&amp;1A2B3C4D&amp;0&amp;3
        /// </summary>
        public static string ParseCableId(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return null;
            var seg = deviceId.Split('\\');
            if (seg.Length < 2) return null;
            var parts = seg[1].Split(new[] { '+' }, 3);
            return parts.Length == 3 && parts[2].Length > 0 ? parts[2] : null;
        }

        public IReadOnlyList<PortInfo> Scan()
        {
            var list = new List<PortInfo>();
            if (vids.Count == 0) return list;
            using var s = new ManagementObjectSearcher("SELECT Name, DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE 'FTDIBUS%'");
            using var found = s.Get();
            foreach (ManagementBaseObject o in found)
            {
                using (o)
                {
                    string id = o["DeviceID"] as string;
                    string name = o["Name"] as string;
                    if (id == null || !vids.Any(v => id.IndexOf(v, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                    var m = ComRe.Match(name ?? "");
                    string cable = ParseCableId(id);
                    if (!m.Success || cable == null) continue;
                    list.Add(new PortInfo { CableId = cable, Com = m.Groups[1].Value, Description = name, Provider = this });
                }
            }
            return list;
        }

        public IPort Create(PortInfo info, int baud, Config cfg) => new SerialPortAdapter(info.Com, baud, cfg.ReadTimeoutMs, cfg.ReadBufferBytes);
    }
}
