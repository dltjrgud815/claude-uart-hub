// RX 바이트 → 텍스트. 두 가지 규칙만 있다.
//  - run 출력(§3.4): \n 으로 줄을 나누고 CR·NUL·ANSI(CSI, ESC+1글자, OSC)를 지운 UTF-8 줄. 커널 태그는 지우기 전 줄에
//    ^(\x1b\[[0-9;]*m)*\[\s*\d+\.\d+\] 로 판정한다(ANSI 접두 허용). printk 가 줄 중간에 끼면 분리할 수 없다(문서화된 한계).
//  - wait 정규화(§3.2): NUL·ESC[K·SGR 만 지우고 \r 도 줄 시작으로 본다(\r\n·\r → \n). 청크 경계를 넘는 시퀀스를 위해 상태를 가진다.
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace UartHub
{
    public static class RxText
    {
        static readonly Regex KernelTag = new Regex(@"^(\x1b\[[0-9;]*m)*\[\s*\d+\.\d+\]", RegexOptions.CultureInvariant);

        public sealed class OutLines
        {
            public readonly List<string> Lines = new List<string>();
            /// <summary>printk 줄의 Lines 인덱스.</summary>
            public readonly List<long> Kernel = new List<long>();
            /// <summary>마지막 줄이 개행으로 끝났다(출력이 비어도 true). false 면 마지막 줄은 E 마커 앞 부분 줄.</summary>
            public bool TrailingNewline = true;
        }

        /// <summary>run 출력 규칙으로 줄을 만든다(CRLF→LF, ANSI·NUL·CR 제거, 커널 태그).</summary>
        public static OutLines Lines(byte[] raw, int off, int len)
        {
            var r = new OutLines();
            int end = off + len, start = off;
            for (int i = off; i <= end; i++)
            {
                if (i < end && raw[i] != (byte)'\n') continue;
                bool last = i == end;
                if (last && start == end) break;
                string latin = Encoding.Latin1.GetString(raw, start, i - start).Replace("\r", "").Replace("\0", "");
                string text = Encoding.UTF8.GetString(StripAnsi(raw, start, i - start));
                if (last)
                {
                    if (text.Length == 0) break;           // CR·ANSI 뿐인 꼬리는 줄이 아니다
                    r.TrailingNewline = false;
                }
                if (KernelTag.IsMatch(latin)) r.Kernel.Add(r.Lines.Count);
                r.Lines.Add(text);
                start = i + 1;
            }
            return r;
        }

        /// <summary>PromptTracker 의 줄 텍스트(바이트 = Latin1 문자)를 UTF-8 로 다시 읽는다(응답의 last_lines·partial 용).</summary>
        public static string FromLatin1(string s) => s == null ? null : Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(s));

        /// <summary>CR·NUL 과 ESC 시퀀스(CSI, OSC…BEL/ST, ESC+1글자)를 뺀 바이트.</summary>
        public static byte[] StripAnsi(byte[] d, int off, int len)
        {
            var o = new List<byte>(len);
            int end = off + len;
            for (int i = off; i < end; i++)
            {
                byte b = d[i];
                if (b == 0 || b == (byte)'\r') continue;
                if (b != 0x1b) { o.Add(b); continue; }
                if (i + 1 >= end) break;
                byte n = d[i + 1];
                if (n == (byte)'[')
                {
                    int j = i + 2;
                    while (j < end && d[j] >= 0x20 && d[j] <= 0x3f) j++;
                    while (j < end && d[j] >= 0x20 && d[j] <= 0x2f) j++;
                    i = j < end ? j : end;              // 종결 바이트까지 건너뜀
                }
                else if (n == (byte)']')
                {
                    int j = i + 2;
                    while (j < end && d[j] != 0x07 && !(d[j] == 0x1b && j + 1 < end && d[j + 1] == (byte)'\\')) j++;
                    i = j < end && d[j] == 0x1b ? j + 1 : j;
                }
                else i++;                                 // ESC + 1글자
            }
            return o.ToArray();
        }

        /// <summary>wait 정규화(§3.2): NUL·ESC[K·SGR 제거, \r\n·\r → \n. 나머지 CSI 는 남긴다. UTF-8 은 청크 경계를 넘어 이어 디코딩한다.</summary>
        public sealed class Normalizer
        {
            int esc;                                    // 0 일반, 1 ESC, 2 CSI
            readonly List<byte> csi = new List<byte>();
            bool lastCr;
            readonly Decoder dec = new UTF8Encoding(false).GetDecoder();
            readonly List<byte> pend = new List<byte>();

            public void Feed(byte[] d, int off, int n, StringBuilder sb)
            {
                pend.Clear();
                for (int k = off; k < off + n; k++)
                {
                    byte b = d[k];
                    switch (esc)
                    {
                        case 0:
                            if (b == 0) break;
                            if (b == 0x1b) { esc = 1; break; }
                            if (b == (byte)'\r') { if (!lastCr) pend.Add((byte)'\n'); lastCr = true; break; }
                            if (b == (byte)'\n') { if (!lastCr) pend.Add((byte)'\n'); lastCr = false; break; }
                            lastCr = false;
                            pend.Add(b);
                            break;
                        case 1:
                            if (b == (byte)'[') { esc = 2; csi.Clear(); break; }
                            esc = 0;
                            lastCr = false;
                            pend.Add(0x1b); pend.Add(b);
                            break;
                        default:
                            if (b >= 0x20 && b <= 0x3f && csi.Count < 32) { csi.Add(b); break; }
                            esc = 0;
                            bool sgr = b == (byte)'m' && csi.TrueForAll(c => (c >= (byte)'0' && c <= (byte)'9') || c == (byte)';');
                            bool el = b == (byte)'K' && csi.Count == 0;
                            if (sgr || el) break;
                            lastCr = false;
                            pend.Add(0x1b); pend.Add((byte)'['); pend.AddRange(csi); pend.Add(b);
                            break;
                    }
                }
                if (pend.Count == 0) return;
                var bytes = pend.ToArray();
                var chars = new char[dec.GetCharCount(bytes, 0, bytes.Length, false)];
                int cn = dec.GetChars(bytes, 0, bytes.Length, chars, 0, false);
                sb.Append(chars, 0, cn);
            }
        }
    }
}
