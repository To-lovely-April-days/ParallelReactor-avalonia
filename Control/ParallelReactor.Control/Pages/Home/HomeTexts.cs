using System.Collections.Generic;
using Avalonia.Data.Converters;

namespace ParallelReactor.Control.Pages.Home;

/// <summary>主页（融合版 F3）的固定文字：逐字同样稿 main-fusion.html。</summary>
public static class HomeTexts
{
    /// <summary>吸气量的计算说明（样稿 UPT_TIP）：运行表「吸气 mmol」表头、曲线「设备按压降计算」的悬停提示。</summary>
    public const string UptakeTip = "吸气量由设备（HMI）计算：按每秒的压力和温度，n = ΔP · V气 / (R · T)；V气 = 有效容积（标定）− 溶液体积；稳压补气、开阀补进去的气不计入。程序升压完成后开始计量，结束步骤停止";

    /// <summary>「未算」的提示（样稿：没填溶液体积或管路未标定）。</summary>
    public const string NotComputedTip = "没填溶液体积或管路未标定，算不出吸气量";

    /// <summary>报警的处理建议（样稿 AL_TIP，按报警名）。</summary>
    public static readonly IReadOnlyDictionary<string, string> AlarmTips = new Dictionary<string, string>
    {
        ["压力超上限"] = "检查是否过量进气或升温导致压力上升；压力回落到上限以下后报警自动解除。解除后通道不会自动恢复，需要人工停止或复位。",
        ["升温偏慢"] = "检查加热套是否贴合、设定的升温速率是否超出加热能力。",
        ["压力采集模块离线"] = "检查 8AI 模块供电和 RS485 接线；恢复后读数自动继续。",
        ["超温 · 切断加热"] = "确认热电偶读数；冷却后在控制抽屉里重新启动加热。",
        ["搅拌电机故障"] = "驱动器报警需在设备旁检查原因后清除，电脑端不能远程清除。",
        ["升温超时"] = "仅提醒，程序继续。",
    };

    /// <summary>断线时设备怎么做（样稿 ONLOST_T；真机 GET /control 的 onRemoteLost）。</summary>
    public static string OnLostText(string? code) => code switch
    {
        "keep" => "保持运行",
        "stopHeat" => "停止本机启动的加热",
        "estop" => "急停",
        null or "" => "按设备设置",
        _ => code,
    };
}

/// <summary>主页视图的小转换器（运行表的格子样式）。</summary>
public static class HomeConverters
{
    public static readonly IValueConverter IsRunDot = new FuncValueConverter<RunDot, bool>(d => d == RunDot.Run);
    public static readonly IValueConverter IsErrDot = new FuncValueConverter<RunDot, bool>(d => d == RunDot.Err);
    public static readonly IValueConverter IsLeftValue = new FuncValueConverter<LeftKind, bool>(k => k == LeftKind.Value);
    public static readonly IValueConverter IsLeftPaused = new FuncValueConverter<LeftKind, bool>(k => k == LeftKind.Paused);
    public static readonly IValueConverter IsLeftNone = new FuncValueConverter<LeftKind, bool>(k => k is LeftKind.None or LeftKind.Ended);
    public static readonly IValueConverter Is1 = new FuncValueConverter<int, bool>(v => v == 1);
    public static readonly IValueConverter IsNot1 = new FuncValueConverter<int, bool>(v => v != 1);
    public static readonly IValueConverter GasTip = new FuncValueConverter<int, string?>(v => v == 2 ? HomeTexts.NotComputedTip : null);
}
