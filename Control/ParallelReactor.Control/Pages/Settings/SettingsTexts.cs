using System;
using System.Collections.Generic;
using System.Globalization;
using ParallelReactor.Api;
using ParallelReactor.Control.Services;

namespace ParallelReactor.Control.Pages.Settings;

/// <summary>设置页的文字与小计算（融合版 F8，样稿 setBody）：纯函数，单元测试直接调。</summary>
public static class SettingsTexts
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public const double PsiPerMPa = 145.04;

    /// <summary>导航（样稿 SET_NAV）：键、名字、「只读」标签。</summary>
    public static readonly IReadOnlyList<(string Key, string Label, bool ReadOnly)> Local = new[]
    {
        ("gen", "常规", false), ("conn", "设备连接", false), ("night", "无人值守", false),
    };

    public static readonly IReadOnlyList<(string Key, string Label, bool ReadOnly)> Device = new[]
    {
        ("lim", "参数与报警", true), ("pid", "温控 / PID", false), ("comm", "通讯与模块", true), ("store", "数据存储", true), ("upt", "吸气量计算", false),
    };

    /// <summary>每一节的标题；未知的键 = 常规。</summary>
    public static string Title(string key) => key switch
    {
        "gen" => "常规", "conn" => "设备连接", "night" => "无人值守", "lim" => "参数与报警", "pid" => "温控 / PID",
        "comm" => "通讯与模块", "store" => "数据存储", "upt" => "吸气量计算", "about" => "关于", _ => "常规",
    };

    /// <summary>这一节是设备参数（跟着设备列表切换）。</summary>
    public static bool IsDeviceSection(string key) => key is "lim" or "pid" or "comm" or "store" or "upt";

    /// <summary>认得的键（地址参数 set=…）。</summary>
    public static bool IsKnown(string? key) => key is "gen" or "conn" or "night" or "lim" or "pid" or "comm" or "store" or "upt" or "about";

    /// <summary>
    /// 设备设置里的 psi 值（样稿 psiM）按设置的压力单位写，括号里是设备原来的 psi：「1.00 MPa（145 psi）」/「10.00 bar（145 psi）」；
    /// 单位是 psi 时反过来写「145 psi（1.00 MPa）」。
    /// </summary>
    public static string PsiM(double psi)
    {
        string mpa = (psi / PsiPerMPa).ToString("0.00", Inv) + " MPa", p = Math.Round(psi).ToString(Inv) + " psi";
        return Core.Format.PressureDisplay.Primary switch
        {
            Core.Format.PressureUnit.Bar => (psi / Core.Format.PressureUnits.PsiPerBar).ToString("0.00", Inv) + " bar（" + p + "）",
            Core.Format.PressureUnit.Psi => p + "（" + mpa + "）",
            _ => mpa + "（" + p + "）",
        };
    }

    /// <summary>
    /// 0.001 MPa 压力分辨率对应的吸气量分辨率 mmol（样稿 resMmol：n = ΔP · V / (R · T)，按 25 °C）。<paramref name="gasMl"/> = 气相体积 mL。
    /// </summary>
    public static double ResolutionMmol(double gasMl) => 1000 * gasMl * 1e-6 / (8.314 * 298.15) * 1000;

    /// <summary>PC 断开时设备怎么做（样稿 ONLOST_T）。</summary>
    public static string OnLost(string? mode) => mode switch
    {
        "keep" => "保持运行",
        "stopHeat" => "停止本机启动的加热",
        "estop" => "急停",
        null or "" => "—",
        _ => mode,
    };

    /// <summary>那一行的说明（样稿 night 表的第三列）。</summary>
    public static string OnLostNote(string? mode) => mode switch
    {
        "keep" => "网络抖动不会中断长时间实验；超温、超压联锁在设备本机，断网照常",
        "stopHeat" => "只停本机下发后启动的加热，其它照常",
        "estop" => "断开即急停：全部停止加热、关阀、停搅拌 · 长时间实验慎用",
        null or "" => "设备未连接过 · 连上后读取",
        _ => "",
    };

    /// <summary>「断线自动重连」那一行（Client 的 WebSocket 重连：0.5 s 起翻倍，最长 10 s 一次，一直重试）。</summary>
    public const string ReconnectText = "0.5 s 起，最长每 10 s 一次";
    public const string ReconnectNote = "· 一直重试，不需要人点";

    /// <summary>「本机重启后」那一行：按「启动时自动连接」写。</summary>
    public static string AfterRestart(bool autoConnect) => autoConnect ? "自动连接，接上正在记录的会话" : "不自动连接（「常规 › 启动时自动连接」关着）";

    /// <summary>防睡眠此刻的状态（设置开着时写在开关下面）。</summary>
    public static string SleepState(bool enabled, bool wanted, bool held, string mechanism, string? error)
    {
        if (!enabled) return "已关闭 · 电脑按系统设置睡眠";
        if (held) return "现在：已阻止睡眠（" + mechanism + "）";
        if (wanted) return "现在：没能阻止睡眠" + (error is { Length: > 0 } e ? " · " + e : "");
        return "现在：没有设备在运行，没有阻止";
    }

    /// <summary>温度档位「低温档 ≤ 100 °C」「中温档 100 – 180 °C」「高温档 &gt; 180 °C」（HMI 的 tempBands：三档，分界只能在触屏改）。</summary>
    public static string BandName(int index) => index switch { 0 => "低温档", 1 => "中温档", 2 => "高温档", _ => "档 " + (index + 1).ToString(Inv) };

    public static string BandRange(TempBandSnap b, bool first, bool last) =>
        first ? "≤ " + b.HiC.ToString("0", Inv) + " °C"
        : last ? "> " + b.LoC.ToString("0", Inv) + " °C"
        : b.LoC.ToString("0", Inv) + " – " + b.HiC.ToString("0", Inv) + " °C";

    /// <summary>
    /// 吸气量计算表的一行（样稿 upt）：釜容积、管路死体积（标定过 = 有效容积 − 釜容积；未标定 = 默认管路体积）、有效容积（未标定 null）、溶液、气相体积、分辨率、标定日期。
    /// 停用 / 未安装的通道溶液和气相为 null。
    /// </summary>
    public static UptRow Upt(VolumeInfo v, int n, double? solutionMl, bool off)
    {
        bool cal = v.Calibrated(n);
        double? eff = cal ? v.EffectiveMl[n - 1] : null;
        double line = cal ? eff!.Value - v.VesselMl : v.LineMl;
        double? sol = off ? null : solutionMl;
        double? gas = off ? null : v.GasVolume(n, sol);
        double? res = gas is double g && g > 0 ? ResolutionMmol(g) : null;
        DateTime? at = n >= 1 && n <= v.CalibratedAt.Count ? v.CalibratedAt[n - 1] : null;
        return new UptRow(n, v.VesselMl, line, eff, sol, gas, res, cal, at);
    }

    /// <summary>吸气量计算的一行（mL；分辨率 mmol）。</summary>
    public sealed record UptRow(int N, double VesselMl, double LineMl, double? EffectiveMl, double? SolutionMl, double? GasMl, double? ResMmol, bool Calibrated, DateTime? CalibratedAt);

    /// <summary>「5.7」/「—」。</summary>
    public static string Ml(double? v) => v is double d ? d.ToString("0.0", Inv) : "—";

    /// <summary>「± 0.002」/「—」。</summary>
    public static string Res(double? v) => v is double d ? "± " + d.ToString("0.000", Inv) : "—";

    /// <summary>令牌遮住（样稿 ••••••••）。</summary>
    public const string TokenMask = "••••••••";
}
