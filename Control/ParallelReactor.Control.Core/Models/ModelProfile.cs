using System;
using System.Collections.Generic;
using System.Linq;
using ParallelReactor.Api;
using ParallelReactor.Control.Core.Format;

namespace ParallelReactor.Control.Core.Models;

/// <summary>搅拌方式：全釜共用一个搅拌电机（PR8 / PR16）/ 每釜独立（PR4）。</summary>
public enum StirMode { Shared, Independent }

/// <summary>总览 / 设备页的反应釜卡片网格，由型号决定：wide4 一排 4 张宽卡、row8 一排 8 张、grid16 两排 × 8。</summary>
public enum LayoutKind { Wide4, Row8, Grid16 }

/// <summary>一路气（进气或排空）。Key 是表内键（程序步骤里引用）；HmiCode 是 HMI 线上的代码（GasSnap.Inlets[].Id、/program-schema 的 gas 选项），没有就为 null。</summary>
public sealed record GasLine(string Key, string Name, string? HmiCode = null);

/// <summary>
/// 一种通讯模块（设备信息面板「模块通讯」）。Key：temp / press / stir / valve，分别对应遥测 CommSnap.Temp / Pressure / Stir / Io。
/// </summary>
public sealed record CommModule(string Key, string Name, int Count)
{
    /// <summary>这一类模块在遥测里是否在线；Key 不认识或没有遥测时为 null。</summary>
    public bool? OnlineIn(CommSnap? comm) => comm is null ? null : Key switch
    {
        "temp" => comm.Temp,
        "press" => comm.Pressure,
        "stir" => comm.Stir,
        "valve" => comm.Io,
        _ => null,
    };
}

/// <summary>釜：容积、溶液上限、显示名（'50 mL 釜'）、说明（'溶液 ≤ 20 mL'）。</summary>
public sealed record VesselSpec(double VolumeMl, double MaxFillMl, string Kind, string Note);

/// <summary>量程：温度上限、压力上限（MPa）、转速范围、压力显示小数位（≥ 5 MPa 用 2 位，否则 3 位，可在型号表里显式指定）。</summary>
public sealed record ModelLimits(double TempMaxC, double PressureMaxMPa, double RpmMin, double RpmMax, int PressureDecimals);

/// <summary>一个通道位：通道号 + 是否安装。</summary>
public readonly record struct ChannelSlot(int N, bool Installed);

/// <summary>连续的通道号段 [From, To]（「RV5–RV8 未安装」合并成一行）。</summary>
public readonly record struct ChannelRun(int From, int To);

/// <summary>
/// 一种设备型号（样稿 js/models.js 的 Profile，API.md §4）。卡片网格、通道列表、量程校验、压力小数位、步骤文字都按它来，
/// 不得在界面里写死 8 路。<b>表内 HT-PR4 / HT-PR8 / HT-PR8 · 现场 / HT-PR16 都是样稿的假设参数</b>（<see cref="Hypothetical"/>，
/// 显示 <see cref="ModelProfiles.SampleNote"/>），等用户给出真实规格后只改 models.json。真机由 <see cref="ModelProfiles.Resolve"/> 推出，
/// 通道数、已安装通道和量程取自设备。
/// </summary>
public sealed class ModelProfile
{
    private readonly ProfileSpec _spec;
    private readonly HashSet<int> _installed;

    internal ModelProfile(ProfileSpec spec, string baseKey, string? deviceModel = null, IReadOnlyList<string>? liveOverrides = null)
    {
        // 内置表的实例全程序共用：对外的列表一律只读包装（不能强转回数组改内容）
        _spec = spec;
        BaseKey = baseKey;
        DeviceModel = deviceModel;
        LiveOverrides = Array.AsReadOnly((liveOverrides ?? Array.Empty<string>()).ToArray());
        Installed = Array.AsReadOnly(spec.Installed.Where(n => n >= 1 && n <= spec.ChannelCount).Distinct().OrderBy(n => n).ToArray());
        _installed = new HashSet<int>(Installed);
        int dec = spec.PressureDecimals ?? Units.PressureDecimals(spec.PressureMaxMPa);
        Limits = new ModelLimits(spec.TempMaxC, spec.PressureMaxMPa, spec.RpmMin, spec.RpmMax, dec);
        Vessel = new VesselSpec(spec.VolumeMl, spec.MaxFillMl, Render(spec.VesselKind), Render(spec.VesselNote));
        StirNote = Render(spec.StirNote);
        GasInlets = Array.AsReadOnly(spec.GasInlets.ToArray());
        Comm = Array.AsReadOnly(spec.Comm.ToArray());
        Channels = Array.AsReadOnly(Enumerable.Range(1, spec.ChannelCount).Select(n => new ChannelSlot(n, _installed.Contains(n))).ToArray());
        var runs = new List<ChannelRun>();
        for (int n = 1; n <= spec.ChannelCount; n++)
        {
            if (_installed.Contains(n)) continue;
            if (runs.Count > 0 && runs[^1].To == n - 1) runs[^1] = runs[^1] with { To = n };
            else runs.Add(new ChannelRun(n, n));
        }
        UninstalledRuns = runs.AsReadOnly();
        GasKeys = Array.AsReadOnly(spec.GasInlets.Select(g => g.Key).Concat(spec.Vent is null ? Array.Empty<string>() : new[] { spec.Vent.Key }).ToArray());
    }

    internal ProfileSpec Spec => _spec;

    /// <summary>表内键：PR4 / PR8 / PR8-4 / PR16。</summary>
    public string Key => _spec.Key;
    /// <summary>继承链的根（程序库按它分组：PR8-4 → PR8）。</summary>
    public string BaseKey { get; }
    /// <summary>型号名：'HT-PR8'。</summary>
    public string Name => _spec.Name;
    /// <summary>显示名：'HT-PR8 · 现场'。</summary>
    public string Label => _spec.Label;
    /// <summary>变体：'' 或 '现场'（装机不全）。</summary>
    public string Variant => _spec.Variant;
    public int ChannelCount => _spec.ChannelCount;
    /// <summary>已安装通道（升序，均在 1..ChannelCount 内）。</summary>
    public IReadOnlyList<int> Installed { get; }
    public VesselSpec Vessel { get; }
    public ModelLimits Limits { get; }
    public StirMode StirMode => _spec.StirMode;
    /// <summary>'磁力搅拌' / '顶置机械搅拌'。</summary>
    public string StirKind => _spec.StirKind;
    /// <summary>'全釜共用磁力搅拌 0–1500 rpm'（随转速上限变化）。</summary>
    public string StirNote { get; }
    public IReadOnlyList<GasLine> GasInlets { get; }
    public GasLine? Vent => _spec.Vent;
    /// <summary>有冷却水夹套（可「降温中」）。PR8 没有。</summary>
    public bool HasCooling => _spec.HasCooling;
    public LayoutKind Layout => _spec.Layout;
    public IReadOnlyList<CommModule> Comm { get; }
    /// <summary>备注：'当前在开发的机型'。</summary>
    public string Note => _spec.Note;
    /// <summary>参数是假设值（示例型号 · 参数为假设）。用户给出真实规格后在 models.json 里改为 false。</summary>
    public bool Hypothetical => _spec.Hypothetical;
    /// <summary>由 /device 推出时为 DeviceInfoDto.Model（如 "ParallelReactor-8"）；表内型号为 null。</summary>
    public string? DeviceModel { get; }
    /// <summary>由设备覆盖了表内值的字段：channelCount / installed / tempMaxC / pressureMaxMPa / rpmMax / maxFillMl。</summary>
    public IReadOnlyList<string> LiveOverrides { get; }

    /// <summary>压力显示小数位（= Limits.PressureDecimals）。</summary>
    public int PressureDecimals => Limits.PressureDecimals;
    /// <summary>每个通道位（含未安装）：所有反应釜列表都按它生成。</summary>
    public IReadOnlyList<ChannelSlot> Channels { get; }
    /// <summary>未安装通道的连续段：[[5, 8]]。</summary>
    public IReadOnlyList<ChannelRun> UninstalledRuns { get; }
    /// <summary>气路键：进气 + 排空。</summary>
    public IReadOnlyList<string> GasKeys { get; }

    public bool IsInstalled(int n) => _installed.Contains(n);

    /// <summary>已安装通道的范围文字：'RV1–RV4'。</summary>
    public string InstalledText => RangeText(Installed, "RV");

    /// <summary>步骤卡片的完整提示：'搅拌电机（全局 RV1–RV8 共用）' / '搅拌电机（本釜）'。</summary>
    public string StirLabel => StirMode == StirMode.Shared ? "搅拌电机（全局 RV1–RV" + ChannelCount + " 共用）" : "搅拌电机（本釜）";

    /// <summary>步骤卡片显示的短名：'搅拌电机（全局）' / '搅拌电机（本釜）'。</summary>
    public string StirShort => StirMode == StirMode.Shared ? "搅拌电机（全局）" : "搅拌电机（本釜）";

    /// <summary>量程文字：'≤ 200 ℃ · ≤ 4.14 MPa'（数字按 JS 的 String(number)）。</summary>
    public string RangeNote => "≤ " + JsNumber.ToString(Limits.TempMaxC) + " ℃ · ≤ " + JsNumber.ToString(Limits.PressureMaxMPa) + " MPa";

    /// <summary>
    /// 气路名称：表内键 → 名称（排空也算）。样稿之外多一层：键对不上时再按 HMI 代码找（真机 GasSnap 的 "A" → '气体 A'）。都找不到返回原样。
    /// </summary>
    public string GasName(string? key)
    {
        if (key is null) return "";
        foreach (var g in GasInlets) if (g.Key == key) return g.Name;
        if (Vent is { } v && v.Key == key) return v.Name;
        foreach (var g in GasInlets) if (g.HmiCode == key) return g.Name;
        if (Vent is { HmiCode: { } vc } && vc == key) return Vent.Name;
        return key;
    }

    /// <summary>按 HMI 代码找进气路（GasSnap.Inlets[].Id）。</summary>
    public GasLine? GasByHmi(string? code)
    {
        if (code is null) return null;
        foreach (var g in GasInlets) if (g.HmiCode == code) return g;
        return null;
    }

    /// <summary>通道号列表 → 区间文字（样稿 PR.models.rangeText）：[1,2,3,4] → 'RV1–RV4'，[1,2,5] → 'RV1–RV2、RV5'，空 → '—'。prefix 为 null 时用 'RV'。</summary>
    public static string RangeText(IEnumerable<int>? list, string? prefix = null)
    {
        string p = prefix ?? "RV";
        var ns = (list ?? Array.Empty<int>()).Distinct().OrderBy(n => n).ToArray();
        if (ns.Length == 0) return Fmt.Dash;
        var runs = new List<(int A, int B)>();
        foreach (int n in ns)
        {
            if (runs.Count > 0 && n == runs[^1].B + 1) runs[^1] = (runs[^1].A, n);
            else runs.Add((n, n));
        }
        return string.Join("、", runs.Select(r => r.A == r.B ? p + r.A : p + r.A + "–" + p + r.B));
    }

    public override string ToString() => Label;

    /// <summary>文字模板：{tempMaxC} {pressureMaxMPa} {rpmMin} {rpmMax} {volumeMl} {maxFillMl} {channelCount}（数字按 JS 的 String(number)）。</summary>
    private string Render(string template)
    {
        if (template.IndexOf('{') < 0) return template;
        return template
            .Replace("{tempMaxC}", JsNumber.ToString(_spec.TempMaxC))
            .Replace("{pressureMaxMPa}", JsNumber.ToString(_spec.PressureMaxMPa))
            .Replace("{rpmMin}", JsNumber.ToString(_spec.RpmMin))
            .Replace("{rpmMax}", JsNumber.ToString(_spec.RpmMax))
            .Replace("{volumeMl}", JsNumber.ToString(_spec.VolumeMl))
            .Replace("{maxFillMl}", JsNumber.ToString(_spec.MaxFillMl))
            .Replace("{channelCount}", _spec.ChannelCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}

/// <summary>型号表里的一条（模板文字未展开、量程未覆盖）。<see cref="ModelProfiles.Resolve"/> 用 with 改出真机的那一份。</summary>
internal sealed record ProfileSpec(
    string Key, string Name, string Label, string Variant, int ChannelCount, int[] Installed,
    double VolumeMl, double MaxFillMl, string VesselKind, string VesselNote,
    double TempMaxC, double PressureMaxMPa, double RpmMin, double RpmMax, int? PressureDecimals,
    StirMode StirMode, string StirKind, string StirNote, GasLine[] GasInlets, GasLine? Vent, bool HasCooling,
    LayoutKind Layout, CommModule[] Comm, string Note, bool Hypothetical);
