using System.ComponentModel;
using ParallelReactor.Control.Core.Format;

namespace ParallelReactor.Control.Theme;

/// <summary>
/// XAML 里写死的压力单位文字（表头「压力 MPa」、单位小字「MPa」、棒图灰带「°C / MPa」）的绑定源：
/// <c>Text="{Binding Header, Source={x:Static th:PressureText.Instance}}"</c>。设置 → 常规改了压力单位（<see cref="PressureDisplay.Changed"/>）时全部跟着变。
/// 视图模型里算出来的文字不走这里，直接用 <see cref="PressureDisplay"/>。
/// </summary>
public sealed class PressureText : INotifyPropertyChanged
{
    public static PressureText Instance { get; } = new();

    private PressureText() => PressureDisplay.Changed += () => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>'MPa' / 'bar' / 'psi'。</summary>
    public string Unit => PressureDisplay.Unit;

    /// <summary>表头：'压力 MPa'。</summary>
    public string Header => PressureDisplay.Label("压力");

    /// <summary>棒图 16 路的灰带：'°C / MPa'。</summary>
    public string TempPress => "°C / " + PressureDisplay.Unit;

    /// <summary>压力分辨率（吸气量分辨率按它估算）：'0.001 MPa' / '0.01 bar' / '0.145 psi'。</summary>
    public static string Resolution => PressureDisplay.Primary switch
    {
        PressureUnit.Bar => "0.01 bar",
        PressureUnit.Psi => "0.145 psi",
        _ => "0.001 MPa",
    };

    /// <summary>设置 · 吸气量计算的说明行（分辨率按设置的单位写）。</summary>
    public string UptNote => "标定在设备触屏「容积标定」里做（空釜一遍 + 加 5 mL 水一遍），这里只显示结果 · 分辨率按压力分辨率 " + Resolution
        + "、25 °C 估算 · 换釜、换管路后要重新标定 · 起算点是各路升压完成的时刻，8 路分别对齐";

    /// <summary>棒图灰带的悬停说明。</summary>
    public string BarUnitsTip => "16 路棒图：温度单位 °C、压力单位 " + PressureDisplay.Unit + "（每根条下不再写单位）";
}
