using Avalonia.Data.Converters;
using Avalonia.Media;
using ParallelReactor.Models;
using System;
using System.Globalization;

namespace ParallelReactor.Converters;

/// <summary>取反 bool</summary>
public class InverseBoolConverter : IValueConverter
{
    public static readonly InverseBoolConverter Instance = new();
    public object Convert(object? v, Type t, object? p, CultureInfo c) => v is bool b && !b;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => v is bool b && !b;
}



/// <summary>bool → 平移变换：true=滑入(0)，false=藏到右侧屏外(392)。用于抽屉动画。</summary>
public class BoolToTranslateConverter : IValueConverter
{
    public static readonly BoolToTranslateConverter Instance = new();
    public object Convert(object? v, Type t, object? p, CultureInfo c)
    {
        bool open = v is bool b && b;
        return Avalonia.Media.Transformation.TransformOperations.Parse(
            open ? "translateX(0px)" : "translateX(460px)");
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>判断 string 是否等于参数（用于分段按钮高亮）</summary>
public class EqualsConverter : IValueConverter
{
    public static readonly EqualsConverter Instance = new();
    public object Convert(object? v, Type t, object? p, CultureInfo c)
        => v?.ToString() == p?.ToString();
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}

/// <summary>string 等于参数 → 1.0，否则 0.0（用于各页常驻、靠透明度切换，避免折叠重绘）</summary>
public class EqualsToOpacityConverter : IValueConverter
{
    public static readonly EqualsToOpacityConverter Instance = new();
    public object Convert(object? v, Type t, object? p, CultureInfo c)
        => v?.ToString() == p?.ToString() ? 1.0 : 0.0;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}

/// <summary>bool → 1.0/0.0（用于弹框常驻、靠透明度开关，避免首次打开时重建）</summary>
public class BoolToOpacityConverter : IValueConverter
{
    public static readonly BoolToOpacityConverter Instance = new();
    public object Convert(object? v, Type t, object? p, CultureInfo c)
        => v is true ? 1.0 : 0.0;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}

/// <summary>反应釜状态 → 状态色（chip / 文本）</summary>
public class StateColorConverter : IValueConverter
{
    public static readonly StateColorConverter Instance = new();
    public object Convert(object? v, Type t, object? p, CultureInfo c)
    {
        var st = v is ReactorState s ? s : ReactorState.Idle;
        var color = st switch
        {
            ReactorState.React => Color.Parse("#5fae14"),
            ReactorState.Alarm => Color.Parse("#e0394c"),
            ReactorState.Heating or ReactorState.Pressing => Color.Parse("#c9820f"),
            ReactorState.Done => Color.Parse("#7d8694"),
            _ => Color.Parse("#66666f")
        };
        return new SolidColorBrush(color);
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}

/// <summary>Modbus 寄存器地址格式化（id 从 1 起，地址从 0 起）</summary>
public class RegConverter : IValueConverter
{
    public enum Kind { SpAddr, PvAddr, DoCoil }
    private readonly Kind _kind;
    public RegConverter(Kind k) => _kind = k;

    public static readonly RegConverter SpAddr = new(Kind.SpAddr);
    public static readonly RegConverter PvAddr = new(Kind.PvAddr);
    public static readonly RegConverter DoCoil = new(Kind.DoCoil);

    public object Convert(object? v, Type t, object? p, CultureInfo c)
    {
        int idx = (v is int i ? i : 1) - 1;
        return _kind switch
        {
            Kind.SpAddr => $"0x000{idx}",
            Kind.PvAddr => $"0x060{idx}",
            Kind.DoCoil => $"0x000{idx} (IO16R)",
            _ => ""
        };
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}
