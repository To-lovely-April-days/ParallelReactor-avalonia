using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ParallelReactor.Control.Core.Models;

namespace ParallelReactor.Control.Core.Demo;

/// <summary>检查器选中的对象（样稿 sel=2 / sel=3/11）：设备号 K（1 起，设备列表里的位置）+ 可选的反应釜号 N。</summary>
public readonly record struct DemoSelection(int K, int? N = null)
{
    public override string ToString() => N is int n ? K.ToString(CultureInfo.InvariantCulture) + "/" + n.ToString(CultureInfo.InvariantCulture) : K.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// 样稿的地址参数（README「常用查询参数」、API.md §2，语法与默认值同 js/core.js PR.route 与 js/sim.js applyParams）：
/// <list type="bullet">
/// <item>devs：'1' / '3' / '4' 或型号列表 'PR8-4,PR4,PR16[,PR8]'（不认识的型号丢掉、最多 8 台；为空或全不认识 = 默认 3 台）。</item>
/// <item>bare=1、freeze=1；res=1920 | 1600 | 1366（其它值 = 窗口 1:1）。</item>
/// <item>sel=&lt;k&gt; 或 &lt;k&gt;/&lt;n&gt;；explorer=0 | 1（其它 = 默认）；tl=0 收起时间轴（给了别的值 = 展开）。</item>
/// <item>lease=&lt;k&gt;:&lt;local|requested|remote|lost&gt;[,…]；alarm=&lt;k&gt;:1（加一条超压报警）| &lt;k&gt;:0（清除）| 其它值（恢复场景默认）。</item>
/// <item>offline=&lt;k&gt;[,…] | none（不给 = 默认：4 号位断线；none / 0 / 空 = 全部在线）；estop=&lt;k&gt;[,…]。</item>
/// <item>stress=1 | 2（压力测试数据）；dialog=name[:arg]；scroll=end（截图把总览滚到底，样稿由截图脚本处理）。</item>
/// </list>
/// 设备键保留原文（'01' 不等于 1 号机，同样稿按字符串比较）。不认识的参数原样留在 <see cref="Extra"/>（例如截图计划的 frame=workspace）。
/// <see cref="ToString"/> 写出规范的查询串（键顺序固定），<c>Parse(p.ToString())</c> 与 p 相等（值相等语义，见 <see cref="Equals(DemoParams?)"/>）。
/// </summary>
public sealed record DemoParams
{
    /// <summary>样稿的设备组合（COMBOS）。</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Combos = new Dictionary<string, IReadOnlyList<string>>
    {
        ["1"] = new[] { "PR8-4" },
        ["3"] = new[] { "PR8-4", "PR4", "PR16" },
        ["4"] = new[] { "PR8-4", "PR4", "PR16", "PR8" },
    };

    /// <summary>固定画面尺寸（样稿 RES）。</summary>
    public static readonly IReadOnlyDictionary<int, (int W, int H)> Resolutions = new Dictionary<int, (int, int)>
    {
        [1920] = (1920, 1080), [1600] = (1600, 900), [1366] = (1366, 768),
    };

    /// <summary>样稿 PR.route.GLOBAL_KEYS（导航时保留的参数）。</summary>
    public static readonly IReadOnlyList<string> GlobalKeys = new[] { "devs", "bare", "freeze", "res", "lease", "offline", "alarm", "estop", "explorer", "tl", "sel", "stress" };

    private static readonly string[] KnownKeys = GlobalKeys.Concat(new[] { "dialog", "scroll" }).ToArray();

    public static readonly DemoParams Default = new();

    /// <summary>设备组合原文（null = 默认 3 台）。</summary>
    public string? Devs { get; init; }
    public bool Bare { get; init; }
    public bool Freeze { get; init; }
    /// <summary>1920 / 1600 / 1366；null = 窗口 1:1。</summary>
    public int? Res { get; init; }
    public DemoSelection? Sel { get; init; }
    /// <summary>资源管理器：true = 打开（≥ 1600 停靠，以下浮层）、false = 收起、null = 默认（≥ 1600 停靠，以下收起）。</summary>
    public bool? Explorer { get; init; }
    /// <summary>时间轴：true = 收起到 40 px 标题（tl=0），false = 展开（给了别的值），null = 没给。</summary>
    public bool? TimelineCollapsed { get; init; }
    /// <summary>每台的控制权覆盖：设备键（原文）→ 值原文（'requested' …；'k' 不带值时为 '1'，按默认处理）。</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Lease { get; init; } = Array.Empty<KeyValuePair<string, string>>();
    /// <summary>断线的设备键；null = 默认（4 号位断线），空表 = 全部在线（none）。</summary>
    public IReadOnlyList<string>? Offline { get; init; }
    /// <summary>每台的报警覆盖：设备键 → '1'（加一条演示超压报警）/ '0'（清除）/ 其它（恢复场景默认）。</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Alarm { get; init; } = Array.Empty<KeyValuePair<string, string>>();
    /// <summary>在设备上被急停的设备键。</summary>
    public IReadOnlyList<string> Estop { get; init; } = Array.Empty<string>();
    /// <summary>0 = 演示数据；1 = 最坏值 + 8 个汉字的别名；2 = 最坏值 + 16 个字母的别名。</summary>
    public int Stress { get; init; }
    /// <summary>对话框原文：'add' / 'lease:2' …（页面参数，导航时丢弃）。</summary>
    public string? Dialog { get; init; }
    /// <summary>滚动位置原文（'end'）。</summary>
    public string? Scroll { get; init; }
    /// <summary>不认识的参数（原样保留，顺序同输入）。</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Extra { get; init; } = Array.Empty<KeyValuePair<string, string>>();

    /// <summary>对话框名（dialog 冒号前）。</summary>
    public string? DialogName => Dialog is null ? null : Dialog.IndexOf(':') is int i and >= 0 ? Dialog[..i] : Dialog;
    /// <summary>对话框参数（冒号后，如设备号）；没有为 null。</summary>
    public string? DialogArg => Dialog is null ? null : Dialog.IndexOf(':') is int i and >= 0 ? Dialog[(i + 1)..] : null;
    /// <summary>scroll=end。</summary>
    public bool ScrollToEnd => Scroll == "end";
    /// <summary>固定画面尺寸；res 没给或不认识为 null。</summary>
    public (int W, int H)? Resolution => Res is int r && Resolutions.TryGetValue(r, out var s) ? s : null;

    /// <summary>
    /// 设备组合 → 每个位置（k = 1, 2, …）的型号键（样稿 parseDevs）：空 = 默认 3 台；单个数字且是已知组合 = 该组合；
    /// 否则按逗号拆开、去掉首尾空白、只留型号表里有的、最多 8 台；一个都没有 = 默认 3 台。
    /// </summary>
    public IReadOnlyList<string> DeviceModels(ModelProfiles? models = null)
    {
        var table = models ?? ModelProfiles.Embedded;
        string s = Devs ?? "";
        if (s.Length == 0) return Combos["3"].ToArray();
        if (s.Length == 1 && s[0] is >= '0' and <= '9' && Combos.TryGetValue(s, out var combo)) return combo.ToArray();
        var list = s.Split(',').Select(x => x.Trim()).Where(x => table.Get(x) != null).Take(8).ToArray();
        return list.Length > 0 ? list : Combos["3"].ToArray();
    }

    /// <summary>某台（k）的控制权覆盖值；没有覆盖返回 null。</summary>
    public string? LeaseFor(int k) => Lookup(Lease, k);

    /// <summary>某台（k）的报警覆盖值；没有覆盖返回 null。</summary>
    public string? AlarmFor(int k) => Lookup(Alarm, k);

    /// <summary>
    /// 某台是否断线：offline 没给时按默认（<paramref name="defaultOffline"/>，样稿 4 号位断线）；给了按列表。
    /// </summary>
    public bool OfflineFor(int k, bool defaultOffline) => Offline is null ? defaultOffline : Offline.Contains(Key(k));

    /// <summary>某台是否被急停。</summary>
    public bool EstopFor(int k) => Estop.Contains(Key(k));

    // ———————————————————— 解析 ————————————————————

    /// <summary>
    /// 解析查询串（'?' 可有可无；同 URLSearchParams：'&amp;' 分隔、第一个 '=' 分键值、'+' 是空格、百分号解码，同名参数后者为准）。
    /// </summary>
    public static DemoParams Parse(string? query)
    {
        var raw = new List<KeyValuePair<string, string>>();
        string q = query ?? "";
        if (q.StartsWith('?')) q = q[1..];
        foreach (var part in q.Split('&'))
        {
            if (part.Length == 0) continue;
            int i = part.IndexOf('=');
            string k = i < 0 ? part : part[..i], v = i < 0 ? "" : part[(i + 1)..];
            raw.Add(new KeyValuePair<string, string>(FormDecode(k), FormDecode(v)));
        }
        return FromPairs(raw);
    }

    /// <summary>从键值对构造（同名参数后者为准，位置取第一次出现）。</summary>
    public static DemoParams FromPairs(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        var map = new List<KeyValuePair<string, string>>();
        foreach (var kv in pairs)
        {
            int i = map.FindIndex(x => x.Key == kv.Key);
            if (i >= 0) map[i] = kv; else map.Add(kv);
        }
        string? Get(string key)
        {
            foreach (var kv in map) if (kv.Key == key) return kv.Value;
            return null;
        }

        int? res = Resolutions.Keys.FirstOrDefault(r => r.ToString(CultureInfo.InvariantCulture) == Get("res")) is int rr and > 0 ? rr : null;
        string? tl = Get("tl");
        string? explorer = Get("explorer");
        return new DemoParams
        {
            Devs = NullIfEmpty(Get("devs")),
            Bare = Get("bare") == "1",
            Freeze = Get("freeze") == "1",
            Res = res,
            Sel = ParseSel(Get("sel")),
            Explorer = explorer == "1" ? true : explorer == "0" ? false : null,
            TimelineCollapsed = tl is null ? null : tl == "0",
            Lease = ParseMap(Get("lease")),
            Offline = ParseSet(Get("offline")),
            Alarm = ParseMap(Get("alarm")),
            Estop = ParseSet(Get("estop")) ?? Array.Empty<string>(),
            Stress = Get("stress") switch { "1" => 1, "2" => 2, _ => 0 },
            Dialog = NullIfEmpty(Get("dialog")),
            Scroll = NullIfEmpty(Get("scroll")),
            Extra = map.Where(kv => !KnownKeys.Contains(kv.Key)).ToArray(),
        };
    }

    /// <summary>
    /// 样稿 parseMap：'2:requested,1:lost' → [(2, requested), (1, lost)]。键去空白后必须全是数字；值去空白，没有冒号时为 '1'；
    /// 'a:b:c' 只取 b。同一键后者为准。
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ParseMap(string? s)
    {
        var list = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrEmpty(s)) return list;
        foreach (var part in s.Split(','))
        {
            var bits = part.Split(':');
            string k = Guard.ActionGuard.JsTrim(bits[0]);
            if (k.Length == 0 || !k.All(c => c is >= '0' and <= '9')) continue;
            string v = bits.Length < 2 ? "1" : Guard.ActionGuard.JsTrim(bits[1]);
            int i = list.FindIndex(x => x.Key == k);
            if (i >= 0) list[i] = new KeyValuePair<string, string>(k, v); else list.Add(new KeyValuePair<string, string>(k, v));
        }
        return list;
    }

    /// <summary>样稿 parseSet：null → null（默认）；'none' / '0' / '' → 空表；否则逗号分隔、去空白后全是数字的键。</summary>
    public static IReadOnlyList<string>? ParseSet(string? s)
    {
        if (s is null) return null;
        if (s is "none" or "0" or "") return Array.Empty<string>();
        var list = new List<string>();
        foreach (var part in s.Split(','))
        {
            string k = Guard.ActionGuard.JsTrim(part);
            if (k.Length > 0 && k.All(c => c is >= '0' and <= '9') && !list.Contains(k)) list.Add(k);
        }
        return list;
    }

    private static DemoSelection? ParseSel(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var bits = s.Split('/');
        if (!int.TryParse(bits[0], NumberStyles.None, CultureInfo.InvariantCulture, out int k)) return null;
        int? n = bits.Length > 1 && int.TryParse(bits[1], NumberStyles.None, CultureInfo.InvariantCulture, out int nn) ? nn : null;
        return new DemoSelection(k, n);
    }

    // ———————————————————— 写出 ————————————————————

    /// <summary>键值对（规范顺序：GLOBAL_KEYS、dialog、scroll、其它；默认值不写）。</summary>
    public IReadOnlyList<KeyValuePair<string, string>> ToPairs()
    {
        var list = new List<KeyValuePair<string, string>>();
        void Add(string k, string? v) { if (!string.IsNullOrEmpty(v)) list.Add(new KeyValuePair<string, string>(k, v)); }
        Add("devs", Devs);
        if (Bare) Add("bare", "1");
        if (Freeze) Add("freeze", "1");
        Add("res", Res?.ToString(CultureInfo.InvariantCulture));
        Add("lease", MapText(Lease));
        if (Offline != null) Add("offline", Offline.Count == 0 ? "none" : string.Join(",", Offline));
        Add("alarm", MapText(Alarm));
        if (Estop.Count > 0) Add("estop", string.Join(",", Estop));
        if (Explorer is bool x) Add("explorer", x ? "1" : "0");
        if (TimelineCollapsed is bool t) Add("tl", t ? "0" : "1");
        Add("sel", Sel?.ToString());
        if (Stress != 0) Add("stress", Stress.ToString(CultureInfo.InvariantCulture));
        Add("dialog", Dialog);
        Add("scroll", Scroll);
        foreach (var kv in Extra) list.Add(kv);
        return list;
    }

    /// <summary>规范的查询串（不带 '?'；编码同样稿 PR.route.build：encodeURIComponent，逗号 / 冒号 / 斜杠不编码）。</summary>
    public override string ToString() => string.Join("&", ToPairs().Select(kv => DemoRoute.Encode(kv.Key) + "=" + DemoRoute.EncodeValue(kv.Value)));

    /// <summary>值相等：规范键值对相同（列表按内容比较）。</summary>
    public bool Equals(DemoParams? o)
    {
        if (o is null) return false;
        if (ReferenceEquals(this, o)) return true;
        return ToPairs().SequenceEqual(o.ToPairs());
    }

    public override int GetHashCode() => ToString().GetHashCode(StringComparison.Ordinal);

    private static string Key(int k) => k.ToString(CultureInfo.InvariantCulture);

    private static string? Lookup(IReadOnlyList<KeyValuePair<string, string>> map, int k)
    {
        string key = Key(k);
        foreach (var kv in map) if (kv.Key == key) return kv.Value;
        return null;
    }

    private static string? MapText(IReadOnlyList<KeyValuePair<string, string>> map) => map.Count == 0 ? null : string.Join(",", map.Select(kv => kv.Key + ":" + kv.Value));

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>application/x-www-form-urlencoded 解码（'+' → 空格，百分号按 UTF-8 解码；坏的百分号序列原样保留）。</summary>
    internal static string FormDecode(string s) => DemoRoute.PercentDecode(s.Replace('+', ' '));
}

/// <summary>
/// 样稿的 hash 路由（js/core.js PR.route）：#/&lt;page&gt;[/&lt;arg1&gt;[/&lt;arg2&gt;]]?&lt;query&gt;。
/// 页面：overview（默认）· device/&lt;k&gt; · reactor/&lt;k&gt;/&lt;n&gt; · placeholder/&lt;name&gt; · gallery。
/// <see cref="ToString"/> 同 PR.route.build（参数用 <see cref="DemoParams"/> 的规范顺序）。
/// </summary>
public sealed record DemoRoute(string Page, IReadOnlyList<string> Args, DemoParams Params)
{
    /// <summary>页签 id（样稿 PR.route.id）：'overview' / 'device/1' / 'reactor/1/3' / 'placeholder/curves'。</summary>
    public string Id => string.Join("/", new[] { Page }.Concat(Args));

    /// <summary>设备页 / 反应釜页的设备号；其它页面或不是数字为 null。</summary>
    public int? DeviceK => (Page is "device" or "reactor") && Args.Count > 0 && int.TryParse(Args[0], NumberStyles.None, CultureInfo.InvariantCulture, out int k) ? k : null;

    /// <summary>反应釜页的反应釜号。</summary>
    public int? ReactorN => Page == "reactor" && Args.Count > 1 && int.TryParse(Args[1], NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : null;

    /// <summary>解析 '#/reactor/1/1?bare=1&amp;freeze=1'（'#' 可有可无；路径段按 decodeURIComponent 解码，坏的保留原文；空页面 = overview）。</summary>
    public static DemoRoute Parse(string? hash)
    {
        string h = hash ?? "";
        if (h.StartsWith('#')) h = h[1..];
        string q = "";
        int qi = h.IndexOf('?');
        if (qi >= 0) { q = h[(qi + 1)..]; h = h[..qi]; }
        var parts = h.Split('/').Where(x => x.Length > 0).Select(PercentDecode).ToArray();
        return new DemoRoute(parts.Length > 0 ? parts[0] : "overview", parts.Skip(1).ToArray(), DemoParams.Parse(q));
    }

    /// <summary>样稿 PR.route.build：'#/' + page + '/' + 参数（encodeURIComponent）+ '?' + 查询串。</summary>
    public override string ToString()
    {
        string qs = Params.ToString();
        return "#/" + Page + string.Concat(Args.Where(a => !string.IsNullOrEmpty(a)).Select(a => "/" + Encode(a))) + (qs.Length > 0 ? "?" + qs : "");
    }

    /// <summary>JS encodeURIComponent：除 A–Z a–z 0–9 - _ . ! ~ * ' ( ) 外都按 UTF-8 百分号编码（大写十六进制）。</summary>
    public static string Encode(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (byte b in Encoding.UTF8.GetBytes(s))
        {
            char c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '!' or '~' or '*' or '\'' or '(' or ')')) sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>查询串的值：encodeURIComponent 后把 %2C %3A %2F 换回 , : /（同样稿 build）。</summary>
    public static string EncodeValue(string s) => Encode(s).Replace("%2C", ",").Replace("%3A", ":").Replace("%2F", "/");

    /// <summary>百分号解码（UTF-8；坏的序列原样保留，同 decodeURIComponent 失败时样稿用原文）。</summary>
    public static string PercentDecode(string s)
    {
        if (s.IndexOf('%') < 0) return s;
        try { return Uri.UnescapeDataString(s); }
        catch (UriFormatException) { return s; }
    }
}
