// .tx 토큰 문법(uart_bridge.ps1:73-96 Get-TxOps 이식). `send`(US-004)와 SpoolWatcher(US-006)가 같이 쓴다.
//   {ESC} {ENTER} {TAB} {SPACE} {CTRL-A}~{CTRL-Z} {HEX:0d0a…} {BREAK}, 줄 맨 앞 {NONL} = 개행 없이 전송.
//   토큰은 대문자만(구 브리지 [regex] 그대로), {NONL} 은 대소문자 무시(구 브리지 -match 그대로). HEX 홀수 자리 마지막 니블은 버린다.
// 중재 분류(§3.3): {NONL} 줄 또는 토큰만으로 된 줄 = "토큰 줄"(AT_PROMPT·AT_UBOOT·BOOTING 허용), 그 밖은 일반 줄(AT_PROMPT 만).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace UartHub
{
    /// <summary>송신 단위 하나: 바이트열 또는 serial break(300ms).</summary>
    public sealed class TxOp
    {
        public byte[] Bytes;
        public bool Break;
    }

    public sealed class TxLine
    {
        static readonly Regex Tok = new Regex(@"\{(ESC|ENTER|TAB|SPACE|BREAK|CTRL-[A-Z]|HEX:[0-9A-Fa-f]{2,})\}", RegexOptions.CultureInvariant);
        static readonly Regex NoNl = new Regex(@"^\{NONL\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public readonly List<TxOp> Ops = new List<TxOp>();
        /// <summary>{NONL}: 끝에 CR 을 붙이지 않는다.</summary>
        public bool NoNewline;
        /// <summary>{NONL} 뒤 본문이 토큰만으로 이뤄졌다(일반 글자 0개).</summary>
        public bool TokenOnly;
        /// <summary>§3.3 "토큰 줄"(.tx 토큰 단독 줄 · {NONL} 줄): AT_UBOOT·BOOTING 에서도 보낼 수 있다.</summary>
        public bool IsTokenLine => NoNewline || TokenOnly;

        public static TxLine Parse(string line)
        {
            var t = new TxLine();
            line ??= "";
            if (NoNl.IsMatch(line)) { t.NoNewline = true; line = line.Substring(6); }
            var buf = new List<byte>();
            int pos = 0, literal = 0;
            foreach (Match m in Tok.Matches(line))
            {
                if (m.Index > pos) { buf.AddRange(Encoding.UTF8.GetBytes(line.Substring(pos, m.Index - pos))); literal += m.Index - pos; }
                string tok = m.Groups[1].Value;
                if (tok == "ESC") buf.Add(0x1b);
                else if (tok == "ENTER") buf.Add(0x0d);
                else if (tok == "TAB") buf.Add(0x09);
                else if (tok == "SPACE") buf.Add(0x20);
                else if (tok.StartsWith("CTRL-", StringComparison.Ordinal)) buf.Add((byte)(tok[5] - 64));
                else if (tok.StartsWith("HEX:", StringComparison.Ordinal))
                {
                    string h = tok.Substring(4);
                    for (int i = 0; i + 1 < h.Length; i += 2) buf.Add(byte.Parse(h.Substring(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                }
                else if (tok == "BREAK")
                {
                    if (buf.Count > 0) { t.Ops.Add(new TxOp { Bytes = buf.ToArray() }); buf.Clear(); }
                    t.Ops.Add(new TxOp { Break = true });
                }
                pos = m.Index + m.Length;
            }
            if (pos < line.Length) { buf.AddRange(Encoding.UTF8.GetBytes(line.Substring(pos))); literal += line.Length - pos; }
            if (buf.Count > 0) t.Ops.Add(new TxOp { Bytes = buf.ToArray() });
            t.TokenOnly = literal == 0 && t.Ops.Count > 0;
            return t;
        }
    }
}
