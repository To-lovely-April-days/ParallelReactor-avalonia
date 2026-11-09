using Avalonia.Data.Converters;
using Avalonia.Media;
using ParallelReactor.Control.Core.State;

namespace ParallelReactor.Control.Theme;

/// <summary>
/// 状态键 → 样式的绑定转换（视图模型只给 Core 的 <see cref="StateKey"/>，颜色 / 字重在视图里按 <see cref="StateStyles"/> 取）：
/// 状态字颜色（报警 r-ink、运行 t-2、其它 t-3）与字重（报警 600），读数颜色（离线 t-3）。
/// </summary>
public static class StateConverters
{
    /// <summary>状态字颜色（样稿 .pr-stword：var(--st-fg)）。</summary>
    public static readonly IValueConverter WordBrush = new FuncValueConverter<StateKey, IBrush>(k => StateStyles.Get(k).Word);

    /// <summary>状态字字重（报警 600，其余 400）。</summary>
    public static readonly IValueConverter WordWeight = new FuncValueConverter<StateKey, FontWeight>(k => StateStyles.Get(k).WordWeight);

    /// <summary>读数颜色（卡片 / 树：离线 t-3，其余 t-1）。</summary>
    public static readonly IValueConverter ReadingBrush = new FuncValueConverter<StateKey, IBrush>(k => StateStyles.Get(k).Reading);
}
