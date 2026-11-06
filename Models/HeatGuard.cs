using System.Globalization;
using ParallelReactor.Commands;

namespace ParallelReactor.Models;

/// <summary>启动加热前公共检查的输入（VM 取实时状态填入）。</summary>
public readonly record struct HeatGuardInput
{
    /// <summary>要启动的通道（1 起）；null = 不针对具体通道（只查全局项）。</summary>
    public int? Id { get; init; }
    public bool EstopLocked { get; init; }
    /// <summary>温控已配置串口（HardwareOptions.UseRealTemp）。</summary>
    public bool TempConfigured { get; init; }
    public bool TempOnline { get; init; }
    public bool OverTempTripped { get; init; }
    public bool OverPressureTripped { get; init; }
    public bool StopHeatOnOverPressure { get; init; }
    /// <summary>超温报警值（℃）。</summary>
    public double OverTemp { get; init; }
    /// <summary>超压报警值（psi）。</summary>
    public double OverPressure { get; init; }
}

/// <summary>
/// 启动加热（定值 / 曲线 / 自整定 / 程序加热步骤）前的公共检查，从 MainViewModel.HeatBlockReason 抽出。
/// 返回 null = 可以启动，否则为（§11 错误码, 与今天逐字相同的中文原因）。判断顺序与原实现一致。
/// </summary>
public static class HeatGuard
{
    public static (string Code, string Message)? Check(in HeatGuardInput s)
    {
        if (s.EstopLocked) return (OpCodes.EstopActive, "急停状态下不可启动加热 · 请先复位");
        if (!s.TempConfigured) return (OpCodes.HwNotConfigured, "温控未配置串口（PR_TEMP_PORT），无法加热");
        if (!s.TempOnline) return (OpCodes.HwOffline, "温控离线（AI-8 通讯失败），无法启动加热");
        if (s.Id is { } n && s.OverTempTripped)
            return (OpCodes.ChannelAlarm,
                $"RV{n} 超温切断未解除（需低于 {(s.OverTemp - OverTempGuard.Hysteresis).ToString("0.#", CultureInfo.InvariantCulture)}℃），不可启动加热");
        if (s.Id is { } m && s.StopHeatOnOverPressure && s.OverPressureTripped)
            return (OpCodes.ChannelAlarm,
                $"RV{m} 超压联锁未解除（需低于 {Units.FormatP(s.OverPressure * OverPressureGuard.RearmRatio)}），不可启动加热");
        return null;
    }
}
