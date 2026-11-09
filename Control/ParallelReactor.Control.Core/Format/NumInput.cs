using System;
using System.Globalization;
using System.Text;

namespace ParallelReactor.Control.Core.Format;

/// <summary>
/// 数值输入的解析与限幅（App 的 NumBox 用；界面无关，可单测）。设定值输入框最终会下发给 HMI，所以只接受有限值：
/// 「NaN」「Infinity」「∞」「1e400」（溢出成 ∞）一律算无效 —— 输入框恢复原值，不把它们当成数。
/// 样稿 ui.numInput / shell.js clampNum：parseFloat 不是有限值时不限幅、不改值。
/// </summary>
public static class NumInput
{
    /// <summary>
    /// 解析输入：去掉首尾空白；全角数字 / 小数点 / 正负号（中文输入法常见）和减号「−」换成半角；按不变区域性解析
    /// （小数点只认「.」，「12,5」无效）。只有有限值返回 true。
    /// </summary>
    public static bool TryParse(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = Normalize(text.Trim());
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v)) return false;
        value = v;
        return true;
    }

    /// <summary>全角 ０–９ ． ＋ －、减号 U+2212 → 半角；其它字符原样（交给解析判无效）。</summary>
    public static string Normalize(string s)
    {
        StringBuilder? sb = null;
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            char r = c switch
            {
                >= '０' and <= '９' => (char)('0' + (c - '０')),
                '．' => '.',
                '＋' => '+',
                '－' or '−' => '-',
                _ => c,
            };
            if (r != c) { sb ??= new StringBuilder(s); sb[i] = r; }
        }
        return sb?.ToString() ?? s;
    }

    /// <summary>
    /// 限幅（min / max 为 NaN = 不限）后按小数位取整（远离零，同 NumBox 的显示）；取整不会越过上下限
    /// （上限 199.95、1 位小数 → 199.9，不是 200.0）。v 不是有限值（NaN / ±∞）时返回 fallback —— 调用方传「原来的值」，坏值不会写进设定值。
    /// </summary>
    public static double Clamp(double v, double min, double max, int decimals, double fallback)
    {
        if (!double.IsFinite(v)) return fallback;
        if (!double.IsNaN(min)) v = Math.Max(min, v);
        if (!double.IsNaN(max)) v = Math.Min(max, v);
        var d = Math.Clamp(decimals, 0, 15);
        var r = Math.Round(v, d, MidpointRounding.AwayFromZero);
        var p = Math.Pow(10, d);
        if (double.IsFinite(max) && r > max) r = Math.Floor(max * p) / p;
        if (double.IsFinite(min) && r < min) r = Math.Ceiling(min * p) / p;
        return r;
    }

    /// <summary>步进的起点：当前值是有限值就用它，否则用下限（有限时），再否则 0。</summary>
    public static double StepBase(double? current, double min) =>
        current is double d && double.IsFinite(d) ? d : double.IsFinite(min) ? min : 0;
}
