// FakeBoard 의 미니 busybox ash: 따옴표·변수·$?·$(..)·; && || | & 리다이렉션·{ } ( ) 를 해석하고 applet 을 흉내낸다.
// 목적은 허브 시험(run 봉인, push, 프롬프트 판정)에 필요한 동작의 충실한 재현이지 완전한 셸이 아니다.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace UartHub.Testing
{
    // ====================================================================== 렉서
    internal sealed class ShSeg { public string Text; public bool Quoted; public int Kind; }     // Kind 0=literal 1=var 2=cmdsub
    internal sealed class ShWord
    {
        public readonly List<ShSeg> Segs = new List<ShSeg>();
        public bool IsLiteral(string s) => Segs.Count == 1 && Segs[0].Kind == 0 && !Segs[0].Quoted && Segs[0].Text == s;
        public override string ToString() => string.Concat(Segs.Select(x => x.Kind == 0 ? x.Text : "$" + x.Text));
    }
    internal sealed class ShTok
    {
        public bool IsOp; public string Op; public int Fd = -1; public ShWord Word;
        public override string ToString() => IsOp ? (Op == "\n" ? "newline" : Op) : Word.ToString();
    }
    internal sealed class ShIncomplete : Exception { public ShIncomplete(string m) : base(m) { } }
    internal sealed class ShSyntaxError : Exception { public ShSyntaxError(string m) : base(m) { } }

    internal static class ShLexer
    {
        public static List<ShTok> Lex(string s)
        {
            var toks = new List<ShTok>();
            ShWord cur = null;
            int i = 0, n = s.Length;

            void Lit(string t, bool q)
            {
                cur ??= new ShWord();
                var last = cur.Segs.Count > 0 ? cur.Segs[cur.Segs.Count - 1] : null;
                if (last != null && last.Kind == 0 && last.Quoted == q) last.Text += t;
                else cur.Segs.Add(new ShSeg { Text = t, Quoted = q });
            }
            void End() { if (cur != null) { toks.Add(new ShTok { Word = cur }); cur = null; } }
            int Dollar(int k, bool q)
            {
                cur ??= new ShWord();
                if (k + 1 >= n) { Lit("$", q); return k + 1; }
                char e = s[k + 1];
                if (e == '{')
                {
                    int j = s.IndexOf('}', k + 2);
                    if (j < 0) throw new ShIncomplete("missing }");
                    cur.Segs.Add(new ShSeg { Kind = 1, Text = s.Substring(k + 2, j - k - 2), Quoted = q });
                    return j + 1;
                }
                if ("?$!#@*-0123456789".IndexOf(e) >= 0) { cur.Segs.Add(new ShSeg { Kind = 1, Text = e.ToString(), Quoted = q }); return k + 2; }
                if (char.IsLetter(e) || e == '_')
                {
                    int j = k + 1;
                    while (j < n && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
                    cur.Segs.Add(new ShSeg { Kind = 1, Text = s.Substring(k + 1, j - k - 1), Quoted = q });
                    return j;
                }
                if (e == '(')
                {
                    int depth = 1, j = k + 2;
                    while (j < n && depth > 0)
                    {
                        if (s[j] == '\'') { int c = s.IndexOf('\'', j + 1); if (c < 0) throw new ShIncomplete("unterminated quoted string"); j = c; }
                        else if (s[j] == '(') depth++;
                        else if (s[j] == ')') depth--;
                        j++;
                    }
                    if (depth > 0) throw new ShIncomplete("missing )");
                    cur.Segs.Add(new ShSeg { Kind = 2, Text = s.Substring(k + 2, j - 1 - (k + 2)), Quoted = q });
                    return j;
                }
                Lit("$", q);
                return k + 1;
            }

            while (i < n)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r') { End(); i++; continue; }
                if (c == '\n') { End(); toks.Add(new ShTok { IsOp = true, Op = "\n" }); i++; continue; }
                if (c == '#' && cur == null) { while (i < n && s[i] != '\n') i++; continue; }
                if (c == '\'')
                {
                    int j = s.IndexOf('\'', i + 1);
                    if (j < 0) throw new ShIncomplete("unterminated quoted string");
                    Lit(s.Substring(i + 1, j - i - 1), true);
                    i = j + 1;
                    continue;
                }
                if (c == '"')
                {
                    cur ??= new ShWord();
                    int before = cur.Segs.Count;
                    var sb = new StringBuilder();
                    int k = i + 1;
                    while (true)
                    {
                        if (k >= n) throw new ShIncomplete("unterminated quoted string");
                        char d = s[k];
                        if (d == '"') break;
                        if (d == '\\' && k + 1 < n && "$`\"\\\n".IndexOf(s[k + 1]) >= 0)
                        {
                            if (s[k + 1] != '\n') sb.Append(s[k + 1]);
                            k += 2;
                            continue;
                        }
                        if (d == '$')
                        {
                            if (sb.Length > 0) { Lit(sb.ToString(), true); sb.Clear(); }
                            k = Dollar(k, true);
                            continue;
                        }
                        sb.Append(d);
                        k++;
                    }
                    if (sb.Length > 0 || cur.Segs.Count == before) Lit(sb.ToString(), true);
                    i = k + 1;
                    continue;
                }
                if (c == '\\')
                {
                    if (i + 1 >= n) throw new ShIncomplete("trailing backslash");
                    if (s[i + 1] == '\n') { i += 2; continue; }
                    Lit(s[i + 1].ToString(), true);
                    i += 2;
                    continue;
                }
                if (c == '$') { i = Dollar(i, false); continue; }
                if ("|&;<>()".IndexOf(c) >= 0)
                {
                    int fd = -1;
                    if ((c == '<' || c == '>') && cur != null && cur.Segs.Count == 1 && cur.Segs[0].Kind == 0 && !cur.Segs[0].Quoted
                        && cur.Segs[0].Text.Length > 0 && cur.Segs[0].Text.All(char.IsDigit))
                    {
                        fd = int.Parse(cur.Segs[0].Text, CultureInfo.InvariantCulture);
                        cur = null;
                    }
                    End();
                    string two = i + 1 < n ? s.Substring(i, 2) : null;
                    string op = two == "&&" || two == "||" || two == ">>" || two == ">&" || two == "<&" ? two : c.ToString();
                    i += op.Length;
                    toks.Add(new ShTok { IsOp = true, Op = op, Fd = fd });
                    continue;
                }
                Lit(c.ToString(), false);
                i++;
            }
            End();
            return toks;
        }
    }

    // ====================================================================== 파서
    internal abstract class ShNode { }
    internal sealed class ShList : ShNode { public readonly List<KeyValuePair<ShNode, bool>> Items = new List<KeyValuePair<ShNode, bool>>(); }
    internal sealed class ShAndOr : ShNode { public readonly List<ShNode> Parts = new List<ShNode>(); public readonly List<string> Ops = new List<string>(); }
    internal sealed class ShPipe : ShNode { public readonly List<ShNode> Cmds = new List<ShNode>(); public bool Bang; }
    internal sealed class ShRedir { public int Fd; public string Op; public ShWord Target; }
    internal sealed class ShSimple : ShNode
    {
        public readonly List<KeyValuePair<string, ShWord>> Assigns = new List<KeyValuePair<string, ShWord>>();
        public readonly List<ShWord> Words = new List<ShWord>();
        public readonly List<ShRedir> Redirs = new List<ShRedir>();
    }
    internal sealed class ShGroup : ShNode { public ShList Body; public bool Subshell; public readonly List<ShRedir> Redirs = new List<ShRedir>(); }

    internal sealed class ShParser
    {
        readonly List<ShTok> t;
        int p;
        ShParser(List<ShTok> toks) { t = toks; }

        public static ShList Parse(string src)
        {
            var ps = new ShParser(ShLexer.Lex(src));
            var l = ps.ParseList(null, null);
            if (ps.Peek != null) throw Syntax(ps.Peek);
            return l;
        }

        public static bool IsIncomplete(string src)
        {
            try { Parse(src); return false; }
            catch (ShIncomplete) { return true; }
            catch (ShSyntaxError) { return false; }
        }

        ShTok Peek => p < t.Count ? t[p] : null;
        bool IsOp(string op) { var k = Peek; return k != null && k.IsOp && k.Op == op; }
        bool IsWordLit(string s) { var k = Peek; return k != null && !k.IsOp && k.Word.IsLiteral(s); }
        void SkipNewlines() { while (IsOp("\n")) p++; }
        static ShSyntaxError Syntax(ShTok k) => new ShSyntaxError("unexpected \"" + (k?.ToString() ?? "end of file") + "\"");
        static bool IsRedir(string op) => op == "<" || op == ">" || op == ">>" || op == ">&" || op == "<&";

        ShList ParseList(string stopOp, string stopWord)
        {
            var l = new ShList();
            while (true)
            {
                SkipNewlines();
                if (Peek == null) break;
                if (stopOp != null && IsOp(stopOp)) break;
                if (stopWord != null && IsWordLit(stopWord)) break;
                var node = ParseAndOr();
                bool bg = false;
                if (IsOp("&")) { bg = true; p++; }
                else if (IsOp(";") || IsOp("\n")) p++;
                else if (Peek != null && !(stopOp != null && IsOp(stopOp)) && !(stopWord != null && IsWordLit(stopWord))) throw Syntax(Peek);
                l.Items.Add(new KeyValuePair<ShNode, bool>(node, bg));
            }
            return l;
        }

        ShNode ParseAndOr()
        {
            var a = new ShAndOr();
            a.Parts.Add(ParsePipe());
            while (IsOp("&&") || IsOp("||"))
            {
                a.Ops.Add(Peek.Op);
                p++;
                SkipNewlines();
                if (Peek == null) throw new ShIncomplete("and-or");
                a.Parts.Add(ParsePipe());
            }
            return a.Parts.Count == 1 ? a.Parts[0] : a;
        }

        ShNode ParsePipe()
        {
            var pp = new ShPipe();
            if (IsWordLit("!")) { pp.Bang = true; p++; }
            pp.Cmds.Add(ParseCommand());
            while (IsOp("|"))
            {
                p++;
                SkipNewlines();
                if (Peek == null) throw new ShIncomplete("pipe");
                pp.Cmds.Add(ParseCommand());
            }
            return pp.Cmds.Count == 1 && !pp.Bang ? pp.Cmds[0] : pp;
        }

        ShNode ParseCommand()
        {
            if (Peek == null) throw new ShIncomplete("command");
            if (IsOp("("))
            {
                p++;
                var body = ParseList(")", null);
                if (Peek == null) throw new ShIncomplete("(");
                if (!IsOp(")") || body.Items.Count == 0) throw Syntax(Peek);
                p++;
                var g = new ShGroup { Body = body, Subshell = true };
                ParseRedirs(g.Redirs);
                return g;
            }
            if (IsWordLit("{"))
            {
                p++;
                var body = ParseList(null, "}");
                if (Peek == null) throw new ShIncomplete("{");
                if (body.Items.Count == 0) throw Syntax(Peek);
                p++;
                var g = new ShGroup { Body = body };
                ParseRedirs(g.Redirs);
                return g;
            }
            var sc = new ShSimple();
            while (Peek != null)
            {
                var k = Peek;
                if (k.IsOp)
                {
                    if (!IsRedir(k.Op)) break;
                    sc.Redirs.Add(ParseOneRedir());
                    continue;
                }
                if (sc.Words.Count == 0 && TryAssign(k.Word, out var name, out var val))
                {
                    sc.Assigns.Add(new KeyValuePair<string, ShWord>(name, val));
                    p++;
                    continue;
                }
                sc.Words.Add(k.Word);
                p++;
            }
            if (sc.Words.Count == 0 && sc.Assigns.Count == 0 && sc.Redirs.Count == 0) throw Syntax(Peek);
            return sc;
        }

        void ParseRedirs(List<ShRedir> rs)
        {
            while (Peek != null && Peek.IsOp && IsRedir(Peek.Op)) rs.Add(ParseOneRedir());
        }

        ShRedir ParseOneRedir()
        {
            var k = Peek;
            p++;
            if (Peek == null) throw new ShIncomplete("redirection");
            if (Peek.IsOp) throw Syntax(Peek);
            var r = new ShRedir { Fd = k.Fd >= 0 ? k.Fd : (k.Op[0] == '<' ? 0 : 1), Op = k.Op, Target = Peek.Word };
            p++;
            return r;
        }

        static bool TryAssign(ShWord w, out string name, out ShWord val)
        {
            name = null; val = null;
            if (w.Segs.Count == 0 || w.Segs[0].Kind != 0 || w.Segs[0].Quoted) return false;
            string first = w.Segs[0].Text;
            int eq = first.IndexOf('=');
            if (eq <= 0 || !Regex.IsMatch(first.Substring(0, eq), "^[A-Za-z_][A-Za-z0-9_]*$")) return false;
            name = first.Substring(0, eq);
            val = new ShWord();
            if (eq + 1 < first.Length) val.Segs.Add(new ShSeg { Text = first.Substring(eq + 1) });
            for (int i = 1; i < w.Segs.Count; i++) val.Segs.Add(w.Segs[i]);
            return true;
        }
    }

    // ====================================================================== 입출력
    internal abstract class ShIn
    {
        public virtual bool IsTty => false;
        public abstract string ReadLine();     // '\n' 포함, EOF 면 null
        public virtual byte[] ReadAll()
        {
            var ms = new MemoryStream();
            string l;
            while ((l = ReadLine()) != null) { var b = Encoding.UTF8.GetBytes(l); ms.Write(b, 0, b.Length); }
            return ms.ToArray();
        }
    }
    internal sealed class TtyIn : ShIn
    {
        readonly FakeBoard b;
        public TtyIn(FakeBoard b) { this.b = b; }
        public override bool IsTty => true;
        public override string ReadLine() => b.TtyReadLine();
    }
    internal sealed class BytesIn : ShIn
    {
        readonly byte[] data; int pos;
        public BytesIn(byte[] d) { data = d ?? new byte[0]; }
        public override string ReadLine()
        {
            if (pos >= data.Length) return null;
            int nl = Array.IndexOf(data, (byte)'\n', pos);
            int end = nl < 0 ? data.Length : nl + 1;
            string s = Encoding.UTF8.GetString(data, pos, end - pos);
            pos = end;
            return s;
        }
        public override byte[] ReadAll() { var r = data.Skip(pos).ToArray(); pos = data.Length; return r; }
    }
    internal sealed class NullIn : ShIn { public override string ReadLine() => null; public override byte[] ReadAll() => new byte[0]; }

    internal abstract class ShOut
    {
        public virtual bool IsTty => false;
        public abstract void Write(byte[] b);
        public void Write(string s) { if (!string.IsNullOrEmpty(s)) Write(Encoding.UTF8.GetBytes(s)); }
        public virtual void Close() { }
    }
    internal sealed class TtyOut : ShOut
    {
        readonly FakeBoard b;
        public TtyOut(FakeBoard b) { this.b = b; }
        public override bool IsTty => true;
        public override void Write(byte[] d)                // ONLCR: \n → \r\n
        {
            var o = new List<byte>(d.Length + 8);
            foreach (byte x in d) { if (x == (byte)'\n') o.Add((byte)'\r'); o.Add(x); }
            b.EmitBytes(o.ToArray());
        }
    }
    internal sealed class BufOut : ShOut
    {
        readonly MemoryStream ms = new MemoryStream();
        public override void Write(byte[] d) => ms.Write(d, 0, d.Length);
        public byte[] ToArray() => ms.ToArray();
    }
    internal sealed class NullOut : ShOut { public override void Write(byte[] d) { } }
    internal sealed class FileOut : ShOut
    {
        readonly FakeBoard b; readonly string path;
        public FileOut(FakeBoard b, string path, bool append) { this.b = b; this.path = path; if (!append) b.FsWrite(path, new byte[0], false); }
        public override void Write(byte[] d) => b.FsWrite(path, d, true);
    }
    internal sealed class KmsgOut : ShOut
    {
        readonly FakeBoard b; readonly StringBuilder sb = new StringBuilder();
        public KmsgOut(FakeBoard b) { this.b = b; }
        public override void Write(byte[] d)
        {
            sb.Append(Encoding.UTF8.GetString(d));
            int nl;
            while ((nl = sb.ToString().IndexOf('\n')) >= 0) { b.KmsgWrite(sb.ToString(0, nl)); sb.Remove(0, nl + 1); }
        }
        public override void Close() { if (sb.Length > 0) { b.KmsgWrite(sb.ToString()); sb.Clear(); } }
    }
    internal sealed class ShIo
    {
        public ShIn In; public ShOut Out, Err;
        public ShIo Copy() => new ShIo { In = In, Out = Out, Err = Err };
    }
    internal sealed class RedirError : Exception { public RedirError(string m) : base(m) { } }

    // ====================================================================== 셸
    internal sealed class FakeShell
    {
        static int pidSeq = 300;
        static readonly string[] Builtins = { ":", "true", "false", "echo", "printf", "cd", "pwd", "export", "unset", "umask", "alias", "eval", "exit",
                                              "test", "[", "command", "set", "kill", "read", "logout" };
        static readonly Dictionary<string, string> AppletPaths = new Dictionary<string, string>
        {
            ["cat"] = "/bin/cat", ["sleep"] = "/bin/sleep", ["uudecode"] = "/usr/bin/uudecode", ["md5sum"] = "/usr/bin/md5sum", ["rm"] = "/bin/rm",
            ["ls"] = "/bin/ls", ["mkdir"] = "/bin/mkdir", ["touch"] = "/bin/touch", ["uname"] = "/bin/uname", ["hostname"] = "/bin/hostname",
            ["date"] = "/bin/date", ["dmesg"] = "/bin/dmesg", ["grep"] = "/bin/grep", ["head"] = "/usr/bin/head", ["tail"] = "/usr/bin/tail",
            ["wc"] = "/usr/bin/wc", ["xxd"] = "/usr/bin/xxd", ["stty"] = "/bin/stty", ["busybox"] = "/bin/busybox", ["vi"] = "/bin/vi",
            ["top"] = "/usr/bin/top", ["reboot"] = "/sbin/reboot", ["sync"] = "/bin/sync", ["sh"] = "/bin/sh", ["which"] = "/usr/bin/which",
            ["echo"] = "/bin/echo", ["printf"] = "/usr/bin/printf", ["true"] = "/bin/true", ["false"] = "/bin/false", ["test"] = "/usr/bin/test",
            ["pwd"] = "/bin/pwd", ["kill"] = "/bin/kill",
        };

        public readonly FakeBoard B;
        public string Argv0;
        public bool Interactive;
        public bool Background;
        public string Cwd = "/";
        public Dictionary<string, string> Vars;
        public string[] Pos = new string[0];
        public int Last;
        public readonly int Pid;
        int lastBg;
        string umask = "0022";

        public FakeShell(FakeBoard b, string argv0, bool interactive)
        {
            B = b; Argv0 = argv0; Interactive = interactive;
            Pid = Interlocked.Increment(ref pidSeq);
            Vars = new Dictionary<string, string>
            {
                ["HOME"] = "/root", ["PATH"] = "/sbin:/usr/sbin:/bin:/usr/bin", ["PS1"] = "\\u@\\h:\\w$ ", ["PS2"] = "> ",
                ["USER"] = "root", ["LOGNAME"] = "root", ["SHELL"] = "/bin/sh", ["TERM"] = "vt102",
            };
        }

        public FakeShell Child(string argv0, bool interactive)
        {
            return new FakeShell(B, argv0, interactive) { Cwd = Cwd, Vars = new Dictionary<string, string>(Vars), Background = Background, umask = umask };
        }

        public string GetVar(string n, string def = null) => Vars.TryGetValue(n, out var v) ? v : def;

        public string ExpandPs1()
        {
            string ps1 = GetVar("PS1", "$ ");
            var sb = new StringBuilder();
            for (int i = 0; i < ps1.Length; i++)
            {
                if (ps1[i] != '\\' || i + 1 >= ps1.Length) { sb.Append(ps1[i]); continue; }
                char c = ps1[++i];
                switch (c)
                {
                    case 'u': sb.Append("root"); break;
                    case 'h': sb.Append(B.Hostname.Split('.')[0]); break;
                    case 'H': sb.Append(B.Hostname); break;
                    case 'w': sb.Append(Cwd == GetVar("HOME") ? "~" : Cwd); break;
                    case 'W': sb.Append(Cwd == "/" ? "/" : Cwd.Substring(Cwd.LastIndexOf('/') + 1)); break;
                    case '$': sb.Append('#'); break;
                    case '\\': sb.Append('\\'); break;
                    default: sb.Append('\\').Append(c); break;
                }
            }
            return sb.ToString();
        }

        ShIo TtyIo() => new ShIo { In = new TtyIn(B), Out = new TtyOut(B), Err = new TtyOut(B) };

        public void RunInteractiveText(string text) { Last = RunText(text, TtyIo()); }

        public int RunText(string text, ShIo io)
        {
            ShList l;
            try { l = ShParser.Parse(text); }
            catch (ShIncomplete e)
            {
                io.Err.Write(Argv0 + ": syntax error: " + (e.Message == "unterminated quoted string" ? "unterminated quoted string" : "unexpected end of file") + "\n");
                return Last = 2;
            }
            catch (ShSyntaxError e) { io.Err.Write(Argv0 + ": syntax error: " + e.Message + "\n"); return Last = 2; }
            return RunList(l, io);
        }

        int RunList(ShList l, ShIo io)
        {
            int rc = Last;
            foreach (var it in l.Items)
            {
                if (it.Value) { StartBackground(it.Key); rc = 0; }
                else rc = Exec(it.Key, io);
                Last = rc;
            }
            return rc;
        }

        int Exec(ShNode n, ShIo io)
        {
            switch (n)
            {
                case ShList l: return RunList(l, io);
                case ShAndOr a:
                    {
                        int rc = Exec(a.Parts[0], io);
                        Last = rc;
                        for (int i = 0; i < a.Ops.Count; i++)
                        {
                            if ((a.Ops[i] == "&&" && rc == 0) || (a.Ops[i] == "||" && rc != 0)) { rc = Exec(a.Parts[i + 1], io); Last = rc; }
                        }
                        return rc;
                    }
                case ShPipe pp: return RunPipe(pp, io);
                case ShGroup g: return RunGroup(g, io);
                case ShSimple s: return RunSimple(s, io);
            }
            return 0;
        }

        int RunPipe(ShPipe pp, ShIo io)
        {
            ShIn input = io.In;
            int rc = 0;
            for (int i = 0; i < pp.Cmds.Count; i++)
            {
                bool last = i == pp.Cmds.Count - 1;
                var buf = last ? null : new BufOut();
                var sub = new ShIo { In = input, Out = last ? io.Out : buf, Err = io.Err };
                if (pp.Cmds.Count > 1)
                {
                    try { rc = Exec(pp.Cmds[i], sub); }
                    catch (FakeBoard.ExitShellException e) { rc = e.Code; }
                }
                else rc = Exec(pp.Cmds[i], sub);
                if (!last) input = new BytesIn(buf.ToArray());
            }
            if (pp.Bang) rc = rc == 0 ? 1 : 0;
            return rc;
        }

        int RunGroup(ShGroup g, ShIo io)
        {
            ShIo io2;
            try { io2 = ApplyRedirs(g.Redirs, io); }
            catch (RedirError e) { io.Err.Write(e.Message); return 1; }
            try
            {
                if (!g.Subshell) return RunList(g.Body, io2);
                var child = Child(Argv0, false);
                try { return child.RunList(g.Body, io2); }
                catch (FakeBoard.ExitShellException e) { return e.Code; }
            }
            finally { CloseRedirs(io2, io); }
        }

        int RunSimple(ShSimple s, ShIo io)
        {
            if (!Background) B.CheckSigint();
            var argv = new List<string>();
            foreach (var w in s.Words) ExpandWord(w, argv, io);
            foreach (var a in s.Assigns) Vars[a.Key] = ExpandSingle(a.Value, io);
            ShIo io2;
            try { io2 = ApplyRedirs(s.Redirs, io); }
            catch (RedirError e) { io.Err.Write(e.Message); return 1; }
            try { return argv.Count == 0 ? 0 : RunCommand(argv, io2); }
            finally { CloseRedirs(io2, io); }
        }

        void StartBackground(ShNode n)
        {
            var child = Child(Argv0, false);
            child.Background = true;
            lastBg = child.Pid;
            var o = new TtyOut(B);
            var th = new Thread(() =>
            {
                try { child.Exec(n, new ShIo { In = new NullIn(), Out = o, Err = o }); }
                catch (Exception) { }
            }) { IsBackground = true, Name = "FakeBoard-bg" };
            th.Start();
        }

        // ---------------------------------------------------------------- 리다이렉션
        ShIo ApplyRedirs(List<ShRedir> rs, ShIo io)
        {
            if (rs.Count == 0) return io;
            var r2 = io.Copy();
            foreach (var r in rs)
            {
                string target = ExpandSingle(r.Target, io);
                switch (r.Op)
                {
                    case "<":
                        if (target == "/dev/null") r2.In = new NullIn();
                        else
                        {
                            var data = B.FsRead(B.Resolve(Cwd, target));
                            if (data == null) throw new RedirError(Argv0 + ": can't open '" + target + "': No such file or directory\n");
                            r2.In = new BytesIn(data);
                        }
                        break;
                    case ">":
                    case ">>":
                        {
                            var o = OpenOut(target, r.Op == ">>");
                            if (r.Fd == 2) r2.Err = o; else r2.Out = o;
                            break;
                        }
                    case ">&":
                        if (r.Fd == 2 && target == "1") r2.Err = r2.Out;
                        else if (r.Fd == 1 && target == "2") r2.Out = r2.Err;
                        break;
                }
            }
            return r2;
        }

        ShOut OpenOut(string target, bool append)
        {
            switch (target)
            {
                case "/dev/null": return new NullOut();
                case "/dev/kmsg": return new KmsgOut(B);
                case "/dev/console":
                case "/dev/tty":
                case "/dev/ttyS0": return new TtyOut(B);
            }
            string path = B.Resolve(Cwd, target);
            string parent = path.Substring(0, Math.Max(1, path.LastIndexOf('/')));
            if (!B.FsDirExists(parent) || B.FsDirExists(path)) throw new RedirError(Argv0 + ": can't create " + target + ": " + (B.FsDirExists(path) ? "Is a directory" : "nonexistent directory") + "\n");
            return new FileOut(B, path, append);
        }

        static void CloseRedirs(ShIo io2, ShIo io)
        {
            if (ReferenceEquals(io2, io)) return;
            if (!ReferenceEquals(io2.Out, io.Out)) io2.Out.Close();
            if (!ReferenceEquals(io2.Err, io.Err) && !ReferenceEquals(io2.Err, io2.Out)) io2.Err.Close();
        }

        // ---------------------------------------------------------------- 확장
        string VarValue(string name)
        {
            switch (name)
            {
                case "?": return Last.ToString(CultureInfo.InvariantCulture);
                case "$": return Pid.ToString(CultureInfo.InvariantCulture);
                case "!": return lastBg.ToString(CultureInfo.InvariantCulture);
                case "#": return Pos.Length.ToString(CultureInfo.InvariantCulture);
                case "0": return Argv0;
                case "@":
                case "*": return string.Join(" ", Pos);
            }
            if (name.Length == 1 && char.IsDigit(name[0])) { int k = name[0] - '0'; return k >= 1 && k <= Pos.Length ? Pos[k - 1] : ""; }
            return Vars.TryGetValue(name, out var v) ? v : "";
        }

        string CmdSub(string text, ShIo io)
        {
            var child = Child(Argv0, false);
            var buf = new BufOut();
            try { child.RunText(text, new ShIo { In = new NullIn(), Out = buf, Err = io.Err }); }
            catch (FakeBoard.ExitShellException) { }
            return Encoding.UTF8.GetString(buf.ToArray()).TrimEnd('\n');
        }

        void ExpandWord(ShWord w, List<string> fields, ShIo io)
        {
            var cur = new StringBuilder();
            bool have = false;
            foreach (var seg in w.Segs)
            {
                string val = seg.Kind == 0 ? seg.Text : seg.Kind == 1 ? VarValue(seg.Text) : CmdSub(seg.Text, io);
                if (seg.Kind == 0 || seg.Quoted) { cur.Append(val); have = true; continue; }
                var parts = val.Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < parts.Length; i++)
                {
                    if (i > 0) { fields.Add(cur.ToString()); cur.Clear(); }
                    cur.Append(parts[i]);
                    have = true;
                }
            }
            if (have) fields.Add(cur.ToString());
        }

        string ExpandSingle(ShWord w, ShIo io)
        {
            var sb = new StringBuilder();
            foreach (var seg in w.Segs) sb.Append(seg.Kind == 0 ? seg.Text : seg.Kind == 1 ? VarValue(seg.Text) : CmdSub(seg.Text, io));
            return sb.ToString();
        }

        // ---------------------------------------------------------------- 명령
        int NotFound(string name, ShIo io) { io.Err.Write(Argv0 + ": " + name + ": not found\n"); return 127; }

        int RunCommand(List<string> argv, ShIo io)
        {
            string name = argv[0];
            if (name.Contains('/'))
            {
                string bn = name.Substring(name.LastIndexOf('/') + 1);
                if (AppletPaths.TryGetValue(bn, out var p) && p == B.Resolve(Cwd, name)) name = bn;
                else return NotFound(name, io);
            }
            var a = argv.Skip(1).ToList();
            switch (name)
            {
                case ":": case "true": case "sync": case "set": case "kill": return 0;
                case "false": return 1;
                case "echo": return Echo(a, io);
                case "printf": return Printf(a, io);
                case "cd": return Cd(a, io);
                case "pwd": io.Out.Write(Cwd + "\n"); return 0;
                case "export":
                    foreach (var x in a) { int eq = x.IndexOf('='); if (eq > 0) Vars[x.Substring(0, eq)] = x.Substring(eq + 1); else if (!Vars.ContainsKey(x)) Vars[x] = ""; }
                    if (a.Count == 0) foreach (var kv in Vars.OrderBy(k => k.Key)) io.Out.Write("export " + kv.Key + "='" + kv.Value + "'\n");
                    return 0;
                case "unset": foreach (var x in a) Vars.Remove(x); return 0;
                case "umask": if (a.Count == 0) io.Out.Write(umask + "\n"); else umask = a[0]; return 0;
                case "alias": return 0;
                case "eval": return RunText(string.Join(" ", a), io);
                case "exit":
                case "logout":
                    {
                        int code = Last;
                        if (a.Count > 0 && !int.TryParse(a[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out code))
                        { io.Err.Write(Argv0 + ": exit: Illegal number: " + a[0] + "\n"); code = 2; }
                        throw new FakeBoard.ExitShellException(code & 255);
                    }
                case "test": return Test(a, io);
                case "[":
                    if (a.Count == 0 || a[a.Count - 1] != "]") { io.Err.Write(Argv0 + ": [: missing ]\n"); return 2; }
                    return Test(a.Take(a.Count - 1).ToList(), io);
                case "command":
                    if (a.Count > 0 && a[0] == "-v")
                    {
                        int rc = 0;
                        foreach (var x in a.Skip(1))
                        {
                            if (Builtins.Contains(x)) io.Out.Write(x + "\n");
                            else if (AppletPaths.TryGetValue(x, out var p)) io.Out.Write(p + "\n");
                            else rc = 1;
                        }
                        return rc;
                    }
                    return a.Count == 0 ? 0 : RunCommand(a, io);
                case "which":
                    {
                        int rc = 0;
                        foreach (var x in a) { if (AppletPaths.TryGetValue(x, out var p)) io.Out.Write(p + "\n"); else rc = 1; }
                        return rc;
                    }
                case "read":
                    {
                        string l = io.In.ReadLine();
                        if (l == null) return 1;
                        if (a.Count > 0) Vars[a[0]] = l.TrimEnd('\n');
                        return 0;
                    }
                case "sh": return Sh(a, io);
                case "sleep": return SleepCmd(a, io);
                case "cat": return Cat(a, io);
                case "uudecode": return Uudecode(a, io);
                case "md5sum": return Md5(a, io);
                case "rm":
                    {
                        bool force = a.Any(x => x.StartsWith("-") && x.Contains('f'));
                        int rc = 0;
                        foreach (var x in a.Where(x => !x.StartsWith("-")))
                            if (!B.FsRemove(B.Resolve(Cwd, x)) && !force) { io.Err.Write("rm: can't remove '" + x + "': No such file or directory\n"); rc = 1; }
                        return rc;
                    }
                case "ls": return Ls(a, io);
                case "mkdir": foreach (var x in a.Where(x => !x.StartsWith("-"))) B.FsMkdir(B.Resolve(Cwd, x)); return 0;
                case "touch": foreach (var x in a) { var pth = B.Resolve(Cwd, x); if (B.FsRead(pth) == null) B.FsWrite(pth, new byte[0], false); } return 0;
                case "uname": return Uname(a, io);
                case "hostname": io.Out.Write(B.Hostname + "\n"); return 0;
                case "date": io.Out.Write(new DateTime(1970, 1, 1).AddSeconds(B.Uptime).ToString("ddd MMM d HH:mm:ss 'UTC' yyyy", CultureInfo.InvariantCulture) + "\n"); return 0;
                case "dmesg": foreach (var l in B.KmsgSnapshot()) io.Out.Write(l + "\n"); return 0;
                case "grep": return Grep(a, io);
                case "head": return HeadTail(a, io, true);
                case "tail": return HeadTail(a, io, false);
                case "wc": return Wc(a, io);
                case "xxd": return Xxd(a, io);
                case "stty": if (a.Count > 0 && a[0] == "size") io.Out.Write("24 80\n"); return 0;
                case "busybox":
                    if (a.Count > 0) return RunCommand(a, io);
                    io.Out.Write("BusyBox v1.36.1 (2026-08-05 16:09:38 KST) multi-call binary.\nBusyBox is copyrighted by many authors between 1998-2015.\n");
                    return 0;
                case "vi": return B.RunVi(this, a.Count > 0 ? B.Resolve(Cwd, a[a.Count - 1]) : null);
                case "top": return B.RunTop();
                case "reboot":
                    if (Background) { B.Reset(); return 0; }
                    throw new FakeBoard.RebootException();
            }
            return NotFound(name, io);
        }

        int Echo(List<string> a, ShIo io)
        {
            int i = 0; bool nl = true, esc = false;
            while (i < a.Count && a[i].Length > 1 && a[i][0] == '-' && a[i].Skip(1).All(ch => "neE".IndexOf(ch) >= 0))
            {
                foreach (char ch in a[i].Skip(1)) { if (ch == 'n') nl = false; else if (ch == 'e') esc = true; else esc = false; }
                i++;
            }
            string s = string.Join(" ", a.Skip(i));
            if (esc) s = Unescape(s, out _);
            io.Out.Write(s + (nl ? "\n" : ""));
            return 0;
        }

        /// <summary>printf 이스케이프(\n \t \\ \r \a \b \f \v \0NNN \xHH). \c 를 만나면 stop.</summary>
        static string Unescape(string s, out bool stop)
        {
            stop = false;
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
                char c = s[++i];
                switch (c)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'a': sb.Append('\a'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'v': sb.Append('\v'); break;
                    case 'e': sb.Append('\x1b'); break;
                    case '\\': sb.Append('\\'); break;
                    case 'c': stop = true; return sb.ToString();
                    case '0':
                        {
                            int v = 0, k = 0;
                            while (k < 3 && i + 1 < s.Length && s[i + 1] >= '0' && s[i + 1] <= '7') { v = v * 8 + (s[++i] - '0'); k++; }
                            sb.Append((char)v);
                            break;
                        }
                    case 'x':
                        {
                            int v = 0, k = 0;
                            while (k < 2 && i + 1 < s.Length && Uri.IsHexDigit(s[i + 1])) { v = v * 16 + Convert.ToInt32(s[++i].ToString(), 16); k++; }
                            if (k == 0) sb.Append("\\x"); else sb.Append((char)v);
                            break;
                        }
                    default: sb.Append('\\').Append(c); break;
                }
            }
            return sb.ToString();
        }

        int Printf(List<string> a, ShIo io)
        {
            if (a.Count == 0) { io.Err.Write("printf: usage: printf FORMAT [ARGUMENT...]\n"); return 2; }
            string fmt = a[0];
            var args = a.Skip(1).ToList();
            int ai = 0, rc = 0;
            var sb = new StringBuilder();
            bool stop = false;
            do
            {
                int used = 0;
                for (int i = 0; i < fmt.Length && !stop; i++)
                {
                    char c = fmt[i];
                    if (c == '\\')
                    {
                        int j = i + 1;
                        if (j < fmt.Length && fmt[j] == '0') { j++; int k = 0; while (k < 3 && j < fmt.Length && fmt[j] >= '0' && fmt[j] <= '7') { j++; k++; } }
                        else if (j < fmt.Length && fmt[j] == 'x') { j++; int k = 0; while (k < 2 && j < fmt.Length && Uri.IsHexDigit(fmt[j])) { j++; k++; } }
                        else j = Math.Min(fmt.Length, j + 1);
                        sb.Append(Unescape(fmt.Substring(i, j - i), out stop));
                        i = j - 1;
                        continue;
                    }
                    if (c != '%') { sb.Append(c); continue; }
                    if (i + 1 < fmt.Length && fmt[i + 1] == '%') { sb.Append('%'); i++; continue; }
                    var m = Regex.Match(fmt.Substring(i), @"^%([-+ 0#]*)(\*|\d+)?(?:\.(\*|\d+))?([sdiuxXocbfeEgG])");
                    if (!m.Success) { sb.Append(c); continue; }
                    i += m.Length - 1;
                    string flags = m.Groups[1].Value;
                    string ws = m.Groups[2].Value == "*" ? (ai < args.Count ? args[ai++] : "0") : m.Groups[2].Value;
                    string ps = m.Groups[3].Value == "*" ? (ai < args.Count ? args[ai++] : "0") : m.Groups[3].Value;
                    char conv = m.Groups[4].Value[0];
                    string arg = ai < args.Count ? args[ai++] : null;
                    if (arg != null) used++;
                    string body;
                    switch (conv)
                    {
                        case 's': body = arg ?? ""; if (ps.Length > 0) body = body.Substring(0, Math.Min(body.Length, int.Parse(ps, CultureInfo.InvariantCulture))); break;
                        case 'b': body = Unescape(arg ?? "", out stop); break;
                        case 'c': body = string.IsNullOrEmpty(arg) ? "" : arg.Substring(0, 1); break;
                        case 'f': case 'e': case 'E': case 'g': case 'G':
                            {
                                double.TryParse(arg ?? "0", NumberStyles.Float, CultureInfo.InvariantCulture, out var dv);
                                body = dv.ToString((conv == 'f' ? "F" : conv.ToString()) + (ps.Length > 0 ? ps : "6"), CultureInfo.InvariantCulture);
                                break;
                            }
                        default:
                            {
                                long v = 0;
                                string sarg = (arg ?? "0").Trim();
                                if (sarg.Length > 1 && (sarg[0] == '\'' || sarg[0] == '"')) v = sarg[1];
                                else if (!long.TryParse(sarg, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v))
                                { io.Err.Write("printf: invalid number '" + sarg + "'\n"); rc = 1; v = 0; }
                                body = conv == 'x' ? v.ToString("x", CultureInfo.InvariantCulture) : conv == 'X' ? v.ToString("X", CultureInfo.InvariantCulture)
                                     : conv == 'o' ? Convert.ToString(v, 8) : v.ToString(CultureInfo.InvariantCulture);
                                break;
                            }
                    }
                    int width = ws.Length > 0 ? int.Parse(ws, CultureInfo.InvariantCulture) : 0;
                    if (body.Length < width)
                        body = flags.Contains('-') ? body.PadRight(width) : (flags.Contains('0') && conv != 's' ? body.PadLeft(width, '0') : body.PadLeft(width));
                    sb.Append(body);
                }
                if (used == 0) break;
            } while (ai < args.Count && !stop);
            io.Out.Write(sb.ToString());
            return rc;
        }

        int Cd(List<string> a, ShIo io)
        {
            string target = a.Count == 0 ? GetVar("HOME", "/") : a[0];
            string p = B.Resolve(Cwd, target);
            if (!B.FsDirExists(p)) { io.Err.Write(Argv0 + ": cd: can't cd to " + target + ": No such file or directory\n"); return 2; }
            Cwd = p;
            Vars["PWD"] = p;
            return 0;
        }

        int Test(List<string> a, ShIo io)
        {
            bool neg = false;
            if (a.Count > 0 && a[0] == "!") { neg = true; a = a.Skip(1).ToList(); }
            bool r;
            if (a.Count == 0) r = false;
            else if (a.Count == 1) r = a[0].Length > 0;
            else if (a.Count == 2)
            {
                string p = B.Resolve(Cwd, a[1]);
                switch (a[0])
                {
                    case "-z": r = a[1].Length == 0; break;
                    case "-n": r = a[1].Length > 0; break;
                    case "-e": r = B.FsRead(p) != null || B.FsDirExists(p); break;
                    case "-f": r = B.FsRead(p) != null; break;
                    case "-d": r = B.FsDirExists(p); break;
                    case "-s": r = (B.FsRead(p)?.Length ?? 0) > 0; break;
                    default: io.Err.Write(Argv0 + ": test: " + a[0] + ": unknown operand\n"); return 2;
                }
            }
            else
            {
                string x = a[0], op = a[1], y = a[2];
                long.TryParse(x, out var xi); long.TryParse(y, out var yi);
                switch (op)
                {
                    case "=": case "==": r = x == y; break;
                    case "!=": r = x != y; break;
                    case "-eq": r = xi == yi; break;
                    case "-ne": r = xi != yi; break;
                    case "-lt": r = xi < yi; break;
                    case "-le": r = xi <= yi; break;
                    case "-gt": r = xi > yi; break;
                    case "-ge": r = xi >= yi; break;
                    default: io.Err.Write(Argv0 + ": test: syntax error\n"); return 2;
                }
            }
            return (r ^ neg) ? 0 : 1;
        }

        int Sh(List<string> a, ShIo io)
        {
            if (a.Count > 0 && a[0] == "-c")
            {
                if (a.Count < 2) { io.Err.Write("sh: -c requires an argument\n"); return 2; }
                var child = Child(a.Count > 2 ? a[2] : "sh", false);
                child.Pos = a.Skip(3).ToArray();
                try { return child.RunText(a[1], io); }
                catch (FakeBoard.ExitShellException e) { return e.Code; }
            }
            if (a.Count > 0)
            {
                var data = B.FsRead(B.Resolve(Cwd, a[0]));
                if (data == null) { io.Err.Write("sh: can't open '" + a[0] + "': No such file or directory\n"); return 2; }
                var child = Child("sh", false);
                child.Pos = a.Skip(1).ToArray();
                try { return child.RunText(Encoding.UTF8.GetString(data), io); }
                catch (FakeBoard.ExitShellException e) { return e.Code; }
            }
            if (io.In.IsTty && !Background) return B.NestedInteractive(Child("sh", true));
            var script = Child("sh", false);
            try { return script.RunText(Encoding.UTF8.GetString(io.In.ReadAll()), io); }
            catch (FakeBoard.ExitShellException e) { return e.Code; }
        }

        int SleepCmd(List<string> a, ShIo io)
        {
            if (a.Count == 0 || !double.TryParse(a[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var sec) || sec < 0)
            { io.Err.Write("sleep: invalid number '" + (a.Count > 0 ? a[0] : "") + "'\n"); return 1; }
            double ms = sec * 1000 * B.Options.SleepScale;
            if (Background) B.BackgroundSleep(ms);
            else B.Sleep(ms, true);
            return 0;
        }

        int Cat(List<string> a, ShIo io)
        {
            var files = a.Where(x => x != "-u").ToList();
            if (files.Count == 0) files.Add("-");
            int rc = 0;
            foreach (var f in files)
            {
                if (f == "-")
                {
                    if (io.In.IsTty) { string l; while ((l = io.In.ReadLine()) != null) io.Out.Write(l); }
                    else io.Out.Write(io.In.ReadAll());
                    continue;
                }
                var d = B.FsRead(B.Resolve(Cwd, f));
                if (d == null) { io.Err.Write("cat: can't open '" + f + "': No such file or directory\n"); rc = 1; continue; }
                io.Out.Write(d);
            }
            return rc;
        }

        byte[] InputOf(List<string> files, ShIo io, out string err)
        {
            err = null;
            if (files.Count == 0 || files[0] == "-") return io.In.ReadAll();
            var d = B.FsRead(B.Resolve(Cwd, files[0]));
            if (d == null) err = files[0] + ": No such file or directory";
            return d;
        }

        int Uudecode(List<string> a, ShIo io)
        {
            string outName = null;
            var rest = new List<string>();
            for (int i = 0; i < a.Count; i++) { if (a[i] == "-o" && i + 1 < a.Count) outName = a[++i]; else rest.Add(a[i]); }
            var input = InputOf(rest, io, out var err);
            if (input == null) { io.Err.Write("uudecode: " + err + "\n"); return 1; }
            var lines = Encoding.UTF8.GetString(input).Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            int bi = lines.FindIndex(l => l.StartsWith("begin-base64 ", StringComparison.Ordinal) || l.StartsWith("begin ", StringComparison.Ordinal));
            if (bi < 0) { io.Err.Write("uudecode: no 'begin' line\n"); return 1; }
            var hdr = lines[bi].Split(new[] { ' ' }, 3);
            outName ??= hdr.Length >= 3 ? hdr[2] : "-";
            byte[] data;
            if (hdr[0] == "begin-base64")
            {
                var sb = new StringBuilder();
                int i = bi + 1;
                for (; i < lines.Count && lines[i] != "===="; i++) sb.Append(lines[i].Trim());
                if (i >= lines.Count) { io.Err.Write("uudecode: short file\n"); return 1; }
                try { data = Convert.FromBase64String(sb.ToString()); }
                catch (FormatException) { io.Err.Write("uudecode: invalid input\n"); return 1; }
            }
            else
            {
                var ms = new MemoryStream();
                int i = bi + 1;
                for (; i < lines.Count && lines[i] != "end"; i++)
                {
                    string l = lines[i];
                    if (l.Length == 0) continue;
                    int len = (l[0] - 32) & 63;
                    var bytes = new List<byte>();
                    for (int k = 1; k + 3 < l.Length + 3 && bytes.Count < len; k += 4)
                    {
                        int C(int x) => x < l.Length ? (l[x] - 32) & 63 : 0;
                        int v = (C(k) << 18) | (C(k + 1) << 12) | (C(k + 2) << 6) | C(k + 3);
                        bytes.Add((byte)(v >> 16)); bytes.Add((byte)(v >> 8)); bytes.Add((byte)v);
                    }
                    ms.Write(bytes.Take(len).ToArray(), 0, len);
                }
                if (i >= lines.Count) { io.Err.Write("uudecode: short file\n"); return 1; }
                data = ms.ToArray();
            }
            if (outName == "-" || outName == "/dev/stdout") { io.Out.Write(data); return 0; }
            string path = B.Resolve(Cwd, outName);
            if (!B.FsWrite(path, data, false)) { io.Err.Write("uudecode: can't create '" + outName + "'\n"); return 1; }
            return 0;
        }

        int Md5(List<string> a, ShIo io)
        {
            var files = a.Where(x => !x.StartsWith("-") || x == "-").ToList();
            if (files.Count == 0) files.Add("-");
            int rc = 0;
            foreach (var f in files)
            {
                byte[] d = f == "-" ? io.In.ReadAll() : B.FsRead(B.Resolve(Cwd, f));
                if (d == null) { io.Err.Write("md5sum: can't open '" + f + "': No such file or directory\n"); rc = 1; continue; }
                using var md5 = MD5.Create();
                io.Out.Write(string.Concat(md5.ComputeHash(d).Select(x => x.ToString("x2", CultureInfo.InvariantCulture))) + "  " + f + "\n");
            }
            return rc;
        }

        int Ls(List<string> a, ShIo io)
        {
            var targets = a.Where(x => !x.StartsWith("-")).ToList();
            if (targets.Count == 0) targets.Add(".");
            int rc = 0;
            foreach (var t in targets)
            {
                string p = B.Resolve(Cwd, t);
                if (B.FsDirExists(p)) { var names = B.FsList(p); if (names.Count > 0) io.Out.Write(string.Join("  ", names) + "\n"); }
                else if (B.FsRead(p) != null) io.Out.Write(t + "\n");
                else { io.Err.Write("ls: " + t + ": No such file or directory\n"); rc = 1; }
            }
            return rc;
        }

        int Uname(List<string> a, ShIo io)
        {
            string f = a.Count > 0 ? a[0] : "-s";
            string s = f == "-a" ? "Linux " + B.Hostname + " 4.19.91 #1 SMP PREEMPT Mon Oct 12 10:52:46 KST 2026 aarch64 GNU/Linux"
                     : f == "-r" ? "4.19.91" : f == "-n" ? B.Hostname : f == "-m" ? "aarch64" : "Linux";
            io.Out.Write(s + "\n");
            return 0;
        }

        int Grep(List<string> a, ShIo io)
        {
            bool inv = false, icase = false, count = false, quiet = false;
            var rest = new List<string>();
            foreach (var x in a)
            {
                if (x.Length > 1 && x[0] == '-' && rest.Count == 0)
                {
                    foreach (char c in x.Skip(1)) { if (c == 'v') inv = true; else if (c == 'i') icase = true; else if (c == 'c') count = true; else if (c == 'q') quiet = true; }
                    continue;
                }
                rest.Add(x);
            }
            if (rest.Count == 0) { io.Err.Write("grep: usage\n"); return 2; }
            Regex re;
            try { re = new Regex(rest[0], icase ? RegexOptions.IgnoreCase : RegexOptions.None); }
            catch (ArgumentException) { io.Err.Write("grep: bad regex '" + rest[0] + "'\n"); return 2; }
            var input = InputOf(rest.Skip(1).ToList(), io, out var err);
            if (input == null) { io.Err.Write("grep: " + err + "\n"); return 2; }
            int n = 0;
            foreach (var l in SplitLines(input))
            {
                if (re.IsMatch(l) == inv) continue;
                n++;
                if (!count && !quiet) io.Out.Write(l + "\n");
            }
            if (count) io.Out.Write(n + "\n");
            return n > 0 ? 0 : 1;
        }

        static List<string> SplitLines(byte[] d)
        {
            var s = Encoding.UTF8.GetString(d);
            var ls = s.Split('\n').ToList();
            if (ls.Count > 0 && ls[ls.Count - 1].Length == 0) ls.RemoveAt(ls.Count - 1);
            return ls;
        }

        int HeadTail(List<string> a, ShIo io, bool head)
        {
            int n = 10;
            var rest = new List<string>();
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i] == "-n" && i + 1 < a.Count) int.TryParse(a[++i], out n);
                else if (a[i].Length > 1 && a[i][0] == '-' && int.TryParse(a[i].Substring(1), out var k)) n = k;
                else rest.Add(a[i]);
            }
            var input = InputOf(rest, io, out var err);
            if (input == null) { io.Err.Write((head ? "head: " : "tail: ") + err + "\n"); return 1; }
            var ls = SplitLines(input);
            var sel = head ? ls.Take(n) : ls.Skip(Math.Max(0, ls.Count - n));
            foreach (var l in sel) io.Out.Write(l + "\n");
            return 0;
        }

        int Wc(List<string> a, ShIo io)
        {
            var flags = string.Concat(a.Where(x => x.StartsWith("-")).Select(x => x.Substring(1)));
            var rest = a.Where(x => !x.StartsWith("-")).ToList();
            var input = InputOf(rest, io, out var err);
            if (input == null) { io.Err.Write("wc: " + err + "\n"); return 1; }
            string s = Encoding.UTF8.GetString(input);
            long lines = s.Count(c => c == '\n'), words = s.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length, bytes = input.Length;
            var parts = new List<string>();
            if (flags.Length == 0 || flags.Contains('l')) parts.Add(lines.ToString(CultureInfo.InvariantCulture));
            if (flags.Length == 0 || flags.Contains('w')) parts.Add(words.ToString(CultureInfo.InvariantCulture));
            if (flags.Length == 0 || flags.Contains('c')) parts.Add(bytes.ToString(CultureInfo.InvariantCulture));
            io.Out.Write(string.Join(" ", parts.Select(x => x.PadLeft(7))) + (rest.Count > 0 ? " " + rest[0] : "") + "\n");
            return 0;
        }

        int Xxd(List<string> a, ShIo io)
        {
            bool rev = a.Contains("-r"), plain = a.Contains("-p") || a.Contains("-ps");
            var rest = a.Where(x => !x.StartsWith("-")).ToList();
            var input = InputOf(rest, io, out var err);
            if (input == null) { io.Err.Write("xxd: " + err + "\n"); return 2; }
            if (rev)
            {
                var hex = new string(Encoding.ASCII.GetString(input).Where(Uri.IsHexDigit).ToArray());
                var o = new byte[hex.Length / 2];
                for (int i = 0; i < o.Length; i++) o[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
                io.Out.Write(o);
                return 0;
            }
            var sb = new StringBuilder();
            if (plain)
            {
                for (int i = 0; i < input.Length; i++) { sb.Append(input[i].ToString("x2", CultureInfo.InvariantCulture)); if (i % 30 == 29) sb.Append('\n'); }
                if (input.Length % 30 != 0) sb.Append('\n');
            }
            else
            {
                for (int off = 0; off < input.Length; off += 16)
                {
                    sb.Append(off.ToString("x8", CultureInfo.InvariantCulture)).Append(": ");
                    for (int i = 0; i < 16; i++)
                    {
                        sb.Append(off + i < input.Length ? input[off + i].ToString("x2", CultureInfo.InvariantCulture) : "  ");
                        if (i % 2 == 1) sb.Append(' ');
                    }
                    sb.Append(' ');
                    for (int i = off; i < Math.Min(off + 16, input.Length); i++) sb.Append(input[i] >= 0x20 && input[i] < 0x7f ? (char)input[i] : '.');
                    sb.Append('\n');
                }
            }
            io.Out.Write(sb.ToString());
            return 0;
        }
    }

    // ====================================================================== 파일시스템(메모리)
    public sealed partial class FakeBoard
    {
        readonly object fsLock = new object();
        readonly Dictionary<string, byte[]> files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        readonly HashSet<string> dirs = new HashSet<string>(StringComparer.Ordinal);

        void InitFs()
        {
            foreach (var d in new[] { "/", "/bin", "/sbin", "/usr", "/usr/bin", "/usr/sbin", "/etc", "/tmp", "/proc", "/proc/sys", "/proc/sys/kernel",
                                      "/dev", "/mnt", "/mnt/mmc", "/root", "/var", "/var/log", "/lib" })
                dirs.Add(d);
            files["/proc/sys/kernel/printk"] = Encoding.ASCII.GetBytes("4\t4\t1\t7\n");
            files["/proc/version"] = Encoding.ASCII.GetBytes("Linux version 4.19.91 (sim) #1 SMP PREEMPT\n");
            files["/etc/profile"] = Encoding.ASCII.GetBytes("PS1='\\u@\\h:\\w$ '\nexport PS1\n");
            lock (kmsg)
            {
                kmsg.Add(KTime(0) + " Booting Linux on physical CPU 0x0000000000 [0x410fd034]");
                kmsg.Add(KTime(0) + " Linux version 4.19.91 (sim)");
            }
        }

        public static string NormalizePath(string cwd, string p)
        {
            string full = p.StartsWith("/", StringComparison.Ordinal) ? p : (cwd.TrimEnd('/') + "/" + p);
            var stack = new List<string>();
            foreach (var part in full.Split('/'))
            {
                if (part.Length == 0 || part == ".") continue;
                if (part == "..") { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); continue; }
                stack.Add(part);
            }
            return "/" + string.Join("/", stack);
        }

        internal string Resolve(string cwd, string p) => NormalizePath(cwd, p);

        /// <summary>시험용: 보드 파일 내용(없으면 null).</summary>
        public byte[] GetFile(string path) => FsRead(NormalizePath("/", path));
        public void PutFile(string path, byte[] data) => FsWrite(NormalizePath("/", path), data, false);

        internal byte[] FsRead(string path)
        {
            if (path == "/etc/hostname") return Encoding.ASCII.GetBytes(Hostname + "\n");
            lock (fsLock) return files.TryGetValue(path, out var d) ? (byte[])d.Clone() : null;
        }

        internal bool FsWrite(string path, byte[] data, bool append)
        {
            lock (fsLock)
            {
                string parent = path.Substring(0, Math.Max(1, path.LastIndexOf('/')));
                if (!dirs.Contains(parent) || dirs.Contains(path)) return false;
                if (append && files.TryGetValue(path, out var old))
                {
                    var n = new byte[old.Length + data.Length];
                    Buffer.BlockCopy(old, 0, n, 0, old.Length);
                    Buffer.BlockCopy(data, 0, n, old.Length, data.Length);
                    files[path] = n;
                }
                else files[path] = (byte[])data.Clone();
                return true;
            }
        }

        internal bool FsRemove(string path) { lock (fsLock) return files.Remove(path); }
        internal bool FsDirExists(string path) { lock (fsLock) return dirs.Contains(path); }
        internal void FsMkdir(string path) { lock (fsLock) { string acc = ""; foreach (var part in path.Split('/').Where(x => x.Length > 0)) { acc += "/" + part; dirs.Add(acc); } } }

        internal List<string> FsList(string dir)
        {
            string pre = dir == "/" ? "/" : dir + "/";
            lock (fsLock)
            {
                return files.Keys.Concat(dirs).Where(k => k != dir && k.StartsWith(pre, StringComparison.Ordinal) && k.IndexOf('/', pre.Length) < 0)
                            .Select(k => k.Substring(pre.Length)).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            }
        }

        void ClearTmp()
        {
            lock (fsLock) foreach (var k in files.Keys.Where(k => k.StartsWith("/tmp/", StringComparison.Ordinal)).ToList()) files.Remove(k);
        }
    }
}
