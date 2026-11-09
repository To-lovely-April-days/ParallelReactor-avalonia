using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;

namespace ParallelReactor.Control.Controls;

/// <summary>
/// 自绘文字（DrawingContext.DrawText / FormattedText / TextLayout）的一段：文字、字号（控件坐标下的 px）、
/// 在控件里占的矩形、颜色。ShotAudit 不读绘图指令，看不到自绘的文字，所以自绘文字的控件要自己报告。
/// </summary>
public readonly record struct AuditTextRun(string Text, double FontSize, Rect Bounds, Color Foreground);

/// <summary>
/// 自绘文字的控件（M4 / M5 的曲线刻度、釜体标注等）实现它：截图审计（Shot/ShotAudit）照样查字号下限（× 外框缩放）、
/// 封闭色板，并把这些文字放进重叠检查。只在截图审计时读取，不影响渲染。
/// </summary>
public interface IAuditText
{
    IEnumerable<AuditTextRun> AuditTexts { get; }
}
