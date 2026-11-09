using Avalonia.Data.Converters;

namespace ParallelReactor.Control.Theme;

/// <summary>
/// 视图里给绑定文字加断行保护（<see cref="UiText"/>）：视图模型只给原文（测试里没有字体系统也能跑），显示时在绑定上转换 ——
/// <see cref="Keep"/>（报警 / 说明句子：中文词不从中间断开，只在空格、「 · 」后断）、<see cref="KeepRange"/>（再加「RV1–RV4」不断开）、
/// <see cref="NoBreak"/>（整段不断开，如急停红框里的「1号机触屏」）。
/// </summary>
public static class TextConverters
{
    public static readonly IValueConverter Keep = new FuncValueConverter<string?, string>(UiText.Keep);
    public static readonly IValueConverter KeepRange = new FuncValueConverter<string?, string>(UiText.KeepRange);
    public static readonly IValueConverter NoBreak = new FuncValueConverter<string?, string>(UiText.NoBreak);
}
