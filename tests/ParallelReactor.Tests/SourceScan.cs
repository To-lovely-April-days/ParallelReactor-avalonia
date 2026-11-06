using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ParallelReactor.Tests;

/// <summary>源码中的一个类型成员（方法 / 属性 / 字段 / 表达式体成员）。文本均为「掩码后」的代码（见 <see cref="SourceScan.Mask"/>）。</summary>
public sealed record SourceMember(string File, string Type, string Name, int Line, string Header, string Body)
{
    /// <summary>
    /// 白名单 / 待迁移清单用的键：「文件名:成员名」；成员属于嵌套类型（类型名 ≠ 文件主名）时为「文件名:类型.成员名」。
    /// </summary>
    public string Key
    {
        get
        {
            var file = System.IO.Path.GetFileName(File);
            var stem = file.Split('.')[0];
            return Type == stem || Type.Length == 0 ? $"{file}:{Name}" : $"{file}:{Type}.{Name}";
        }
    }
    public override string ToString() => $"{File}:{Line} {Type}.{Name}";
}

/// <summary>
/// 架构测试用的简易 C# 源码扫描（测试工程不引用主工程，无法用反射加载 ViewModel 类型）。
/// 先把注释、字符串、字符字面量的内容替换成空格（保留长度与换行；插值字符串的 {…} 洞保留为代码），
/// 再按括号深度把类型体切成成员。约定：入口方法体保持简短（一两行转发），复杂逻辑放进命令层。
/// </summary>
public static class SourceScan
{
    public static string RepoRoot { get; } = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ParallelReactor.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("找不到仓库根目录（ParallelReactor.csproj）");
    }

    public static IEnumerable<string> Files(string relDir, string pattern, bool recursive = false)
        => Directory.EnumerateFiles(Path.Combine(RepoRoot, relDir), pattern,
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.Ordinal);

    public static string Rel(string path) => Path.GetRelativePath(RepoRoot, path).Replace('\\', '/');

    // ============ 掩码：注释 / 字符串 / 字符内容 → 空格 ============

    public static string Mask(string src)
    {
        var sb = new StringBuilder(src);
        int i = 0;
        MaskCode(src, sb, ref i, stopAtBrace: false);
        return sb.ToString();
    }

    private static void Blank(StringBuilder sb, int from, int to)
    {
        for (int k = from; k < to && k < sb.Length; k++)
            if (sb[k] != '\n' && sb[k] != '\r') sb[k] = ' ';
    }

    /// <summary>扫描代码直到文件结束（或 stopAtBrace 时遇到与起点配对的 '}'，用于插值洞）。</summary>
    private static void MaskCode(string s, StringBuilder sb, ref int i, bool stopAtBrace)
    {
        int depth = 0;
        while (i < s.Length)
        {
            char c = s[i];
            char n = i + 1 < s.Length ? s[i + 1] : '\0';
            if (c == '/' && n == '/')
            {
                int e = s.IndexOf('\n', i);
                if (e < 0) e = s.Length;
                Blank(sb, i, e); i = e; continue;
            }
            if (c == '/' && n == '*')
            {
                int e = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                e = e < 0 ? s.Length : e + 2;
                Blank(sb, i, e); i = e; continue;
            }
            if (c == '\'')
            {
                int st = i++;
                while (i < s.Length && s[i] != '\'') { if (s[i] == '\\') i++; i++; }
                i++;
                Blank(sb, st + 1, i - 1); continue;
            }
            if (c == '"' || ((c == '$' || c == '@') && (n == '"' || ((n == '$' || n == '@') && i + 2 < s.Length && s[i + 2] == '"'))))
            {
                MaskString(s, sb, ref i); continue;
            }
            if (stopAtBrace)
            {
                if (c == '{') depth++;
                else if (c == '}')
                {
                    if (depth == 0) return;   // 插值洞结束（调用方跳过这个 '}'）
                    depth--;
                }
            }
            i++;
        }
    }

    private static void MaskString(string s, StringBuilder sb, ref int i)
    {
        bool interp = false, verbatim = false;
        while (s[i] != '"') { if (s[i] == '$') interp = true; if (s[i] == '@') verbatim = true; i++; }
        // 原始字符串字面量 """…"""（本仓库未使用，按普通字符串近似处理）
        i++;   // 开头的引号
        int segStart = i;
        while (i < s.Length)
        {
            char c = s[i];
            if (!verbatim && c == '\\') { i += 2; continue; }
            if (verbatim && c == '"' && i + 1 < s.Length && s[i + 1] == '"') { i += 2; continue; }
            if (c == '"') { Blank(sb, segStart, i); i++; return; }
            if (interp && c == '{')
            {
                if (i + 1 < s.Length && s[i + 1] == '{') { i += 2; continue; }
                Blank(sb, segStart, i);
                i++;   // 洞的 '{' 本身也不计入代码括号：洞内按代码扫描到配对的 '}'
                sb[i - 1] = ' ';
                MaskCode(s, sb, ref i, stopAtBrace: true);
                if (i < s.Length) { sb[i] = ' '; i++; }
                segStart = i;
                continue;
            }
            if (interp && c == '}' && i + 1 < s.Length && s[i + 1] == '}') { i += 2; continue; }
            if (!verbatim && c == '\n') { Blank(sb, segStart, i); return; }   // 防御：未闭合
            i++;
        }
        Blank(sb, segStart, s.Length);
    }

    // ============ 成员切分 ============

    private static readonly Regex TypeKeyword = new(@"\b(class|struct|record|interface|namespace)\b", RegexOptions.Compiled);
    private static readonly Regex EnumKeyword = new(@"\benum\b", RegexOptions.Compiled);
    private static readonly Regex Ident = new(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

    public static List<SourceMember> Members(string path)
    {
        var raw = File.ReadAllText(path);
        var code = Mask(raw);
        var list = new List<SourceMember>();
        ScanTypeBody(path, code, 0, code.Length, "", list);
        return list;
    }

    private static int LineOf(string s, int pos)
    {
        int line = 1;
        for (int k = 0; k < pos && k < s.Length; k++) if (s[k] == '\n') line++;
        return line;
    }

    /// <summary>找到与 s[open]（'{' / '(' / '['）配对的位置。</summary>
    private static int Match(string s, int open)
    {
        char o = s[open], c = o switch { '{' => '}', '(' => ')', '[' => ']', _ => throw new ArgumentException() };
        int d = 0;
        for (int k = open; k < s.Length; k++)
        {
            if (s[k] == o) d++;
            else if (s[k] == c && --d == 0) return k;
        }
        return s.Length - 1;
    }

    private static void ScanTypeBody(string file, string s, int from, int to, string typeName, List<SourceMember> list)
    {
        int segStart = from;
        int k = from;
        while (k < to)
        {
            char c = s[k];
            if (c == '(' || c == '[') { k = Match(s, k) + 1; continue; }
            if (c == ';')
            {
                AddMember(file, s, segStart, k, k + 1, typeName, list, expressionBodied: true);
                segStart = ++k; continue;
            }
            if (c == '}') { segStart = ++k; continue; }   // 防御（不应出现）
            if (c == '{')
            {
                string header = s[segStart..k];
                if (HasTopLevelAssign(header))
                {
                    // 表达式体成员 / 带初始化器的字段：一直到顶层的 ';'
                    int e = k;
                    while (e < to && s[e] != ';')
                    {
                        if (s[e] is '{' or '(' or '[') e = Match(s, e);
                        e++;
                    }
                    AddMember(file, s, segStart, k, Math.Min(e + 1, to), typeName, list, expressionBodied: true);
                    segStart = k = Math.Min(e + 1, to);
                    continue;
                }
                int close = Match(s, k);
                var stripped = StripAttributes(header);
                if (TypeKeyword.IsMatch(stripped))
                {
                    var m = Regex.Match(stripped, @"\b(?:class|struct|record|interface|namespace)\s+([A-Za-z_][\w\.]*)");
                    ScanTypeBody(file, s, k + 1, close, m.Success ? m.Groups[1].Value : typeName, list);
                }
                else if (!EnumKeyword.IsMatch(stripped))
                {
                    AddMember(file, s, segStart, k, close + 1, typeName, list, expressionBodied: false);
                }
                segStart = k = close + 1;
                continue;
            }
            k++;
        }
    }

    /// <summary>头部在括号外含 '=' 或 '=>'（表达式体 / 初始化器）。</summary>
    private static bool HasTopLevelAssign(string header)
    {
        int d = 0;
        for (int k = 0; k < header.Length; k++)
        {
            char c = header[k];
            if (c is '(' or '[' or '<') d++;
            else if (c is ')' or ']' or '>') { if (c == '>' && k > 0 && header[k - 1] == '=') continue; d = Math.Max(0, d - 1); }
            else if (c == '=' && d == 0)
            {
                char p = k > 0 ? header[k - 1] : ' ';
                char n = k + 1 < header.Length ? header[k + 1] : ' ';
                if (p is '=' or '!' or '<' or '>') continue;
                if (n == '=') { k++; continue; }
                return true;
            }
        }
        return false;
    }

    private static string StripAttributes(string header)
    {
        var sb = new StringBuilder(header);
        for (int k = 0; k < header.Length; k++)
            if (header[k] == '[')
            {
                int e = Match(header, k);
                for (int j = k; j <= e && j < sb.Length; j++) if (sb[j] != '\n') sb[j] = ' ';
                k = e;
            }
        return sb.ToString();
    }

    private static void AddMember(string file, string s, int segStart, int bodyStart, int end, string typeName,
                                  List<SourceMember> list, bool expressionBodied)
    {
        // 头部 = 从上一个成员结束到成员体开始（含特性与注释掩码）
        int headerEnd = bodyStart;
        string full = s[segStart..Math.Min(end, s.Length)];
        string header = s[segStart..headerEnd];
        if (expressionBodied)
        {
            // 表达式体：头部截到第一个顶层 "=>" / "="
            int arrow = IndexOfTopLevelAssign(full);
            if (arrow >= 0) header = full[..arrow];
            else header = full;
        }
        var stripped = StripAttributes(header);
        if (string.IsNullOrWhiteSpace(stripped)) return;
        if (Regex.IsMatch(stripped, @"^\s*(using|namespace)\b")) return;
        string? name = MemberName(stripped);
        if (name == null) return;
        int nameOffset = segStart + Math.Max(0, header.LastIndexOf(name, StringComparison.Ordinal));
        string body = full[(header.Length)..];
        list.Add(new SourceMember(file, typeName, name, LineOf(s, nameOffset), header, body));
    }

    private static int IndexOfTopLevelAssign(string text)
    {
        int d = 0;
        for (int k = 0; k < text.Length; k++)
        {
            char c = text[k];
            if (c is '(' or '[' or '{') d++;
            else if (c is ')' or ']' or '}') d = Math.Max(0, d - 1);
            else if (c == '=' && d == 0)
            {
                char p = k > 0 ? text[k - 1] : ' ';
                char n = k + 1 < text.Length ? text[k + 1] : ' ';
                if (p is '!' or '<' or '>' or '=') continue;
                if (n == '=') { k++; continue; }
                return k;
            }
        }
        return -1;
    }

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "public", "private", "protected", "internal", "static", "readonly", "async", "override", "virtual",
        "sealed", "partial", "new", "extern", "unsafe", "const", "event", "abstract", "volatile", "void",
        "get", "set", "init", "where", "operator", "implicit", "explicit", "this", "required",
    };

    /// <summary>方法 = '(' 前的标识符；属性 / 字段 = 最后一个标识符。</summary>
    private static string? MemberName(string header)
    {
        int d = 0, paren = -1;
        for (int k = 0; k < header.Length; k++)
        {
            char c = header[k];
            if (c == '<') d++;
            else if (c == '>') d = Math.Max(0, d - 1);
            else if (c == '(' && d == 0) { paren = k; break; }
        }
        string before = paren >= 0 ? header[..paren] : header;
        before = Regex.Replace(before, @"<[^<>]*>", " ");   // 去掉泛型参数（一层足够）
        before = Regex.Replace(before, @"<[^<>]*>", " ");
        var ids = Ident.Matches(before).Select(m => m.Value).Where(v => !Keywords.Contains(v)).ToList();
        return ids.Count == 0 ? null : ids[^1];
    }

    // ============ 辅助 ============

    /// <summary>把匹配 callStart（须以 '(' 结尾）的每个调用的实参整段换成空格（保留长度）。</summary>
    public static string BlankCallArgs(string code, string callStart)
    {
        var sb = new StringBuilder(code);
        foreach (Match x in Regex.Matches(code, callStart))
        {
            int open = x.Index + x.Length - 1;
            if (code[open] != '(') continue;
            int close = Match(code, open);
            for (int k = open + 1; k < close; k++) if (sb[k] != '\n') sb[k] = ' ';
        }
        return sb.ToString();
    }

    /// <summary>头部去掉特性后的文本（判断修饰符用）。</summary>
    public static string StripAttributesPublic(string header) => StripAttributes(header);

    /// <summary>成员是方法（去掉特性后的头部在泛型括号外有 '('）。属性 / 字段 / 事件为 false。</summary>
    public static bool IsMethod(SourceMember m)
    {
        var h = StripAttributes(m.Header);
        int d = 0;
        foreach (char c in h)
        {
            if (c == '<') d++;
            else if (c == '>') d = Math.Max(0, d - 1);
            else if (c == '(' && d == 0) return true;
        }
        return false;
    }

    /// <summary>成员体的「第一条语句」：去掉开头的 '{' 和空白后的文本。</summary>
    public static string FirstStatement(SourceMember m)
    {
        var b = m.Body.TrimStart();
        if (b.StartsWith("{")) b = b[1..].TrimStart();
        return b;
    }

    /// <summary>特性列表里是否有某个特性（支持 [A, B] 合并写法与命名空间前缀）。</summary>
    public static bool HasAttribute(SourceMember m, string name)
        => Regex.IsMatch(m.Header, $@"[\[,]\s*(?:[\w\.]+\.)?{name}(?:Attribute)?\s*[\(\],]");

    /// <summary>[WriteEntry(nameof(OperatorCommands.TryX), …)] 里登记的 Try* 名。</summary>
    public static IReadOnlyList<string> WriteEntryCommands(SourceMember m)
    {
        var at = Regex.Match(m.Header, @"[\[,]\s*(?:[\w\.]+\.)?WriteEntry(?:Attribute)?\s*\(");
        if (!at.Success) return Array.Empty<string>();
        int open = at.Index + at.Length - 1;
        int close = Match(m.Header, open);
        var args = m.Header[open..(close + 1)];
        return Regex.Matches(args, @"nameof\s*\(\s*(?:[\w\.]+\.)?OperatorCommands\.(Try\w+)\s*\)")
            .Select(x => x.Groups[1].Value).ToList();
    }
}
