using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ParallelReactor.Api;

namespace ParallelReactor.Control.Core.Models;

/// <summary>型号表文件格式错误（字段缺失、类型不对、取值越界、base 引用不存在 / 成环）。</summary>
public sealed class ModelFileException : FormatException
{
    public ModelFileException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// 型号表（样稿 js/models.js 的移植，PORT-PLAN §3.2 第 1 条）。内置表是嵌入的 Models/models.json；
/// 可在配置目录放一份同格式的 <see cref="OverrideFileName"/>：同键的条目整条替换、新键追加到后面，deviceModels 按键合并。
/// 覆盖文件读不了或有错时整份忽略、用内置表，原因放在 <see cref="LoadError"/>（界面提示用），绝不抛出。
/// <para><b>表内参数全部是样稿的假设值</b>（HT-PR4 / HT-PR8 / HT-PR8 · 现场 / HT-PR16，示例型号 · 参数为假设），等用户提供真实规格
/// （通道数、釜容积与溶液上限、量程、搅拌方式、气路名称、有无冷却、各模块数量）后只改 models.json。</para>
/// </summary>
public sealed class ModelProfiles
{
    /// <summary>配置目录里覆盖内置表的文件名。</summary>
    public const string OverrideFileName = "models.json";

    /// <summary>型号参数旁边的提示（样稿 PR.models.SAMPLE_NOTE）。</summary>
    public const string SampleNote = "示例型号 · 参数为假设";

    /// <summary>通道数上限（型号表校验用）。</summary>
    public const int MaxChannels = 64;

    private const string ResourceName = "ParallelReactor.Control.Core.Models.models.json";

    private static readonly JsonDocumentOptions DocOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly Lazy<ModelProfiles> EmbeddedTable = new(() => FromJson(ReadEmbedded(), null, "内置型号表"));

    private readonly ModelProfile[] _all;
    private readonly Dictionary<string, ModelProfile> _byKey;
    private readonly Dictionary<string, string> _deviceModels;

    private ModelProfiles(ModelProfile[] all, Dictionary<string, string> deviceModels, string? fallbackKey, string source, string? loadError)
    {
        _all = all;
        _byKey = all.ToDictionary(p => p.Key, StringComparer.Ordinal);
        _deviceModels = deviceModels;
        FallbackKey = fallbackKey != null && _byKey.ContainsKey(fallbackKey) ? fallbackKey : all[0].Key;
        Source = source;
        LoadError = loadError;
    }

    /// <summary>内置型号表。</summary>
    public static ModelProfiles Embedded => EmbeddedTable.Value;

    /// <summary>全部型号，按文件顺序（演示条 / 添加设备里的顺序：PR4、PR8、PR8-4、PR16）。</summary>
    public IReadOnlyList<ModelProfile> All => _all;

    public IReadOnlyList<string> Keys => _all.Select(p => p.Key).ToArray();

    /// <summary>/device 报的型号认不出时用的表内键。</summary>
    public string FallbackKey { get; }

    /// <summary>"内置型号表" 或覆盖文件的路径。</summary>
    public string Source { get; }

    /// <summary>覆盖文件有错被忽略时的原因；没有覆盖文件或读取成功为 null。</summary>
    public string? LoadError { get; }

    /// <summary>按表内键取型号；没有返回 null。</summary>
    public ModelProfile? Get(string? key) => key != null && _byKey.TryGetValue(key, out var p) ? p : null;

    /// <summary>/device 的 model（如 "ParallelReactor-8"）对应的表内键；没有配置返回 null。</summary>
    public string? KeyForDeviceModel(string? deviceModel)
    {
        if (string.IsNullOrEmpty(deviceModel)) return null;
        if (_deviceModels.TryGetValue(deviceModel, out var k)) return k;
        foreach (var kv in _deviceModels) if (string.Equals(kv.Key, deviceModel, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    /// <summary>
    /// 由真机的 GET /device（必有）和 GET /settings（可选）推出这台设备的型号（PORT-PLAN §3.2 第 1 条）：
    /// <list type="number">
    /// <item>型号：deviceModels 里配置的键（"ParallelReactor-8" → PR8）；认不出时找通道数相同的表内型号（非变体优先），再不行用 FallbackKey。</item>
    /// <item>通道数取 /device 的 channelCount；已安装通道取 channels[].installed（没有时取 installedChannels）。装机不全 → 「HT-PR8 · 现场」。</item>
    /// <item>量程：/settings 的 limits.tempSpMaxC、limits.rpmMax、limits.volumeMaxMl（溶液上限）、pressure.fullScaleMPa 覆盖表内假设值；
    ///   没有 /settings 时压力满量程取 /device 的 hardware.pressure.fullScaleMPa。</item>
    /// <item>接口里没有的（釜容积、气路名称、搅拌方式、冷却、模块名称与数量）仍取表内值，所以 Hypothetical 不变。</item>
    /// </list>
    /// 通道数与表内不同时卡片网格按通道数选（≤ 4 wide4，≤ 8 row8，其余 grid16）。
    /// </summary>
    public ModelProfile Resolve(DeviceInfoDto info, SettingsDto? settings = null)
    {
        if (info is null) throw new ArgumentNullException(nameof(info));
        var overrides = new List<string>();
        int count = info.ChannelCount is >= 1 and <= MaxChannels ? info.ChannelCount : 0;
        var baseProfile = Get(KeyForDeviceModel(info.Model))
            ?? (count > 0 ? _all.Where(p => p.ChannelCount == count).OrderBy(p => p.Variant.Length > 0 ? 1 : 0).FirstOrDefault() : null)
            ?? Get(FallbackKey)!;
        var s = baseProfile.Spec;
        if (count == 0) count = s.ChannelCount;
        if (count != s.ChannelCount) overrides.Add("channelCount");

        int[] installed = info.Channels is { Length: > 0 }
            ? info.Channels.Where(c => c.Installed).Select(c => c.Id).ToArray()
            : info.InstalledChannels ?? Array.Empty<int>();
        installed = installed.Where(n => n >= 1 && n <= count).Distinct().OrderBy(n => n).ToArray();
        if (installed.Length == 0) installed = s.Installed.Where(n => n <= count).ToArray();
        if (!installed.SequenceEqual(s.Installed)) overrides.Add("installed");

        bool full = installed.Length == count;
        string variant = full ? "" : "现场";
        string label = full ? (s.Variant.Length == 0 ? s.Label : s.Name) : s.Name + " · 现场";

        double temp = Pick(settings?.Limits?.TempSpMaxC, s.TempMaxC, "tempMaxC", overrides);
        double press = Pick(settings?.Pressure?.FullScaleMPa ?? info.Hardware?.Pressure?.FullScaleMPa, s.PressureMaxMPa, "pressureMaxMPa", overrides);
        double rpm = Pick(settings?.Limits?.RpmMax, s.RpmMax, "rpmMax", overrides);
        double fill = Pick(settings?.Limits?.VolumeMaxMl, s.MaxFillMl, "maxFillMl", overrides);

        var spec = s with
        {
            ChannelCount = count, Installed = installed, Label = label, Variant = variant,
            TempMaxC = temp, PressureMaxMPa = press, RpmMax = Math.Max(rpm, s.RpmMin), MaxFillMl = fill,
            Layout = count == s.ChannelCount ? s.Layout : LayoutFor(count),
        };
        return new ModelProfile(spec, baseProfile.BaseKey, info.Model, overrides);
    }

    /// <summary>按通道数选卡片网格。</summary>
    public static LayoutKind LayoutFor(int channelCount) => channelCount <= 4 ? LayoutKind.Wide4 : channelCount <= 8 ? LayoutKind.Row8 : LayoutKind.Grid16;

    /// <summary>读内置表，再叠加覆盖文件（路径为空或文件不存在 = 只用内置表）。不抛出：覆盖文件有错时用内置表并给出 <see cref="LoadError"/>。</summary>
    public static ModelProfiles Load(string? overridePath)
    {
        if (string.IsNullOrWhiteSpace(overridePath) || !File.Exists(overridePath)) return Embedded;
        try
        {
            return FromJson(ReadEmbedded(), File.ReadAllText(overridePath), overridePath);
        }
        catch (Exception ex)   // 配置文件：任何错误都退回内置表，不让程序起不来
        {
            string why = ex is ModelFileException ? ex.Message : ex.GetType().Name + "：" + ex.Message;
            var e = Embedded;
            return new ModelProfiles(e._all, e._deviceModels, e.FallbackKey, e.Source, overridePath + " 无效，已用内置型号表：" + why);
        }
    }

    /// <summary>只解析一份完整的型号表（不叠加内置表；测试 / 工具用）。格式错误抛 <see cref="ModelFileException"/>。</summary>
    public static ModelProfiles Parse(string json, string source = "models.json")
    {
        try { return FromJson(json, null, source); }
        catch (JsonException ex) { throw new ModelFileException(source + "：JSON 格式错误：" + ex.Message, ex); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { throw new ModelFileException(source + "：" + ex.Message, ex); }
    }

    private static double Pick(double? live, double table, string name, List<string> overrides)
    {
        if (live is double v && double.IsFinite(v) && v > 0)
        {
            if (v != table) overrides.Add(name);
            return v;
        }
        return table;
    }

    private static string ReadEmbedded()
    {
        using var stream = typeof(ModelProfiles).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("缺少嵌入资源 " + ResourceName);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // ———————————————————— 解析 ————————————————————

    private static ModelProfiles FromJson(string baseJson, string? overlayJson, string source)
    {
        var raws = new List<(string Key, JsonObject Node)>();
        var deviceModels = new Dictionary<string, string>(StringComparer.Ordinal);
        string? fallback = null;
        ReadFile(baseJson, overlayJson == null ? source : "内置型号表", raws, deviceModels, ref fallback);
        if (overlayJson != null) ReadFile(overlayJson, source, raws, deviceModels, ref fallback);
        if (raws.Count == 0) throw new ModelFileException(source + "：profiles 为空");

        var rawByKey = raws.ToDictionary(r => r.Key, r => r.Node, StringComparer.Ordinal);
        var resolved = new Dictionary<string, (JsonObject Node, string BaseKey)>(StringComparer.Ordinal);
        var list = new List<ModelProfile>();
        foreach (var (key, _) in raws)
        {
            var (node, baseKey) = ResolveBase(key, rawByKey, resolved, new HashSet<string>(StringComparer.Ordinal), source);
            list.Add(new ModelProfile(ToSpec(node, source + " · " + key), baseKey));
        }
        foreach (var kv in deviceModels)
            if (!rawByKey.ContainsKey(kv.Value)) throw new ModelFileException(source + "：deviceModels." + kv.Key + " 指向不存在的型号 " + kv.Value);
        if (fallback != null && !rawByKey.ContainsKey(fallback)) throw new ModelFileException(source + "：fallbackModel 指向不存在的型号 " + fallback);
        return new ModelProfiles(list.ToArray(), deviceModels, fallback, source, null);
    }

    /// <summary>读一份文件：profiles 按键放进 raws（同键整条替换、保持原位置，新键追加），deviceModels 按键合并。</summary>
    private static void ReadFile(string json, string source, List<(string Key, JsonObject Node)> raws, Dictionary<string, string> deviceModels, ref string? fallback)
    {
        var root = JsonNode.Parse(json, null, DocOptions) as JsonObject ?? throw new ModelFileException(source + "：根节点必须是对象");
        if (root["profiles"] is not JsonArray arr) throw new ModelFileException(source + "：缺少 profiles 数组");
        foreach (var item in arr)
        {
            if (item is not JsonObject o) throw new ModelFileException(source + "：profiles 里的每一项都必须是对象");
            string key = ReadString(o, "key", source + " · profiles[]");
            if (key.Length == 0) throw new ModelFileException(source + "：profiles[].key 不能为空");
            int i = raws.FindIndex(r => r.Key == key);
            if (i >= 0) raws[i] = (key, o); else raws.Add((key, o));
        }
        if (root["deviceModels"] is JsonObject dm)
            foreach (var kv in dm) deviceModels[kv.Key] = kv.Value is JsonValue v && v.TryGetValue(out string? k) && !string.IsNullOrEmpty(k)
                ? k : throw new ModelFileException(source + "：deviceModels." + kv.Key + " 必须是型号键");
        if (root["fallbackModel"] is JsonValue fb) fallback = fb.TryGetValue(out string? f) ? f : throw new ModelFileException(source + "：fallbackModel 必须是字符串");
    }

    /// <summary>"base"：先取基础条目（递归展开），再用本条目的字段逐个覆盖（浅合并，同样稿 Object.assign({}, PR8, {...})）。</summary>
    private static (JsonObject Node, string BaseKey) ResolveBase(string key, Dictionary<string, JsonObject> raw, Dictionary<string, (JsonObject, string)> done,
        HashSet<string> visiting, string source)
    {
        if (done.TryGetValue(key, out var hit)) return hit;
        if (!visiting.Add(key)) throw new ModelFileException(source + "：型号 " + key + " 的 base 成环");
        var node = raw[key];
        (JsonObject, string) result;
        if (node["base"] is JsonNode b)
        {
            string baseKey = b is JsonValue bv && bv.TryGetValue(out string? bk) ? bk : throw new ModelFileException(source + "：" + key + ".base 必须是字符串");
            if (baseKey == key || !raw.ContainsKey(baseKey)) throw new ModelFileException(source + "：" + key + ".base 指向不存在的型号 " + baseKey);
            var (parent, root) = ResolveBase(baseKey, raw, done, visiting, source);
            var merged = (JsonObject)parent.DeepClone();
            foreach (var kv in node)
            {
                if (kv.Key == "base") continue;
                merged[kv.Key] = kv.Value?.DeepClone();
            }
            result = (merged, root);
        }
        else result = (node, key);
        visiting.Remove(key);
        done[key] = result;
        return result;
    }

    private static ProfileSpec ToSpec(JsonObject o, string ctx)
    {
        string key = ReadString(o, "key", ctx);
        string name = ReadString(o, "name", ctx);
        int count = (int)ReadNumber(o, "channelCount", ctx, 1, MaxChannels, integer: true);
        int[] installed = o["installed"] is JsonArray ia
            ? ia.Select((x, i) => (int)AsNumber(x, ctx + ".installed[" + i + "]", 1, count, integer: true)).ToArray()
            : Enumerable.Range(1, count).ToArray();
        if (installed.Length == 0) throw new ModelFileException(ctx + ".installed：至少要有一个已安装通道");

        var vessel = ReadObject(o, "vessel", ctx);
        var limits = ReadObject(o, "limits", ctx);
        double rpmMin = ReadNumber(limits, "rpmMin", ctx + ".limits", 0, 100000);
        int? pDec = limits["pressureDecimals"] is null ? null : (int)ReadNumber(limits, "pressureDecimals", ctx + ".limits", 0, 4, integer: true);

        string stir = ReadString(o, "stirMode", ctx);
        var stirMode = stir switch
        {
            "shared" => StirMode.Shared,
            "independent" => StirMode.Independent,
            _ => throw new ModelFileException(ctx + ".stirMode：只能是 shared / independent，实际 " + stir),
        };
        string layout = ReadString(o, "layout", ctx);
        var layoutKind = layout switch
        {
            "wide4" => LayoutKind.Wide4,
            "row8" => LayoutKind.Row8,
            "grid16" => LayoutKind.Grid16,
            _ => throw new ModelFileException(ctx + ".layout：只能是 wide4 / row8 / grid16，实际 " + layout),
        };

        var gas = o["gasInlets"] is JsonArray ga
            ? ga.Select((x, i) => ReadGas(x, ctx + ".gasInlets[" + i + "]")).ToArray()
            : throw new ModelFileException(ctx + "：缺少 gasInlets 数组");
        GasLine? vent = o["vent"] is null ? null : ReadGas(o["vent"], ctx + ".vent");
        var comm = o["comm"] is JsonArray ca
            ? ca.Select((x, i) =>
            {
                string c = ctx + ".comm[" + i + "]";
                var co = x as JsonObject ?? throw new ModelFileException(c + " 必须是对象");
                return new CommModule(ReadString(co, "key", c), ReadString(co, "name", c), (int)ReadNumber(co, "count", c, 0, 64, integer: true));
            }).ToArray()
            : throw new ModelFileException(ctx + "：缺少 comm 数组");

        return new ProfileSpec(
            Key: key, Name: name, Label: ReadString(o, "label", ctx, name), Variant: ReadString(o, "variant", ctx, ""),
            ChannelCount: count, Installed: installed,
            VolumeMl: ReadNumber(vessel, "volumeMl", ctx + ".vessel", 0.001, 1e6), MaxFillMl: ReadNumber(vessel, "maxFillMl", ctx + ".vessel", 0, 1e6),
            VesselKind: ReadString(vessel, "kind", ctx + ".vessel"), VesselNote: ReadString(vessel, "note", ctx + ".vessel", ""),
            TempMaxC: ReadNumber(limits, "tempMaxC", ctx + ".limits", 1, 2000), PressureMaxMPa: ReadNumber(limits, "pressureMaxMPa", ctx + ".limits", 0.001, 1000),
            RpmMin: rpmMin, RpmMax: ReadNumber(limits, "rpmMax", ctx + ".limits", rpmMin, 100000), PressureDecimals: pDec,
            StirMode: stirMode, StirKind: ReadString(o, "stirKind", ctx), StirNote: ReadString(o, "stirNote", ctx, ""),
            GasInlets: gas, Vent: vent, HasCooling: ReadBool(o, "hasCooling", ctx, false),
            Layout: layoutKind, Comm: comm, Note: ReadString(o, "note", ctx, ""), Hypothetical: ReadBool(o, "hypothetical", ctx, true));
    }

    private static GasLine ReadGas(JsonNode? x, string ctx)
    {
        var g = x as JsonObject ?? throw new ModelFileException(ctx + " 必须是对象");
        string? hmi = g["hmi"] is null ? null : ReadString(g, "hmi", ctx);
        return new GasLine(ReadString(g, "key", ctx), ReadString(g, "name", ctx), hmi);
    }

    private static JsonObject ReadObject(JsonObject o, string name, string ctx) =>
        o[name] as JsonObject ?? throw new ModelFileException(ctx + "：缺少对象 " + name);

    private static string ReadString(JsonObject o, string name, string ctx, string? fallback = null)
    {
        var n = o[name];
        if (n is null) return fallback ?? throw new ModelFileException(ctx + "：缺少 " + name);
        return n is JsonValue v && v.TryGetValue(out string? s) ? s : throw new ModelFileException(ctx + "." + name + " 必须是字符串");
    }

    private static bool ReadBool(JsonObject o, string name, string ctx, bool fallback)
    {
        var n = o[name];
        if (n is null) return fallback;
        return n is JsonValue v && v.TryGetValue(out bool b) ? b : throw new ModelFileException(ctx + "." + name + " 必须是 true / false");
    }

    private static double ReadNumber(JsonObject o, string name, string ctx, double min, double max, bool integer = false) =>
        AsNumber(o[name] ?? throw new ModelFileException(ctx + "：缺少 " + name), ctx + "." + name, min, max, integer);

    private static double AsNumber(JsonNode? n, string ctx, double min, double max, bool integer)
    {
        if (n is not JsonValue v || !v.TryGetValue(out double d) || !double.IsFinite(d)) throw new ModelFileException(ctx + " 必须是数字");
        if (integer && d != Math.Floor(d)) throw new ModelFileException(ctx + " 必须是整数，实际 " + d);
        if (d < min || d > max) throw new ModelFileException(ctx + " 超出范围 " + min + "–" + max + "，实际 " + d);
        return d;
    }
}
