using System;
using System.Collections.Generic;
using System.Linq;

namespace ParallelReactor.Control.Core.Layout;

/// <summary>反应釜页的面板：程序、釜体（窄版式里是「釜体 / 属性查看器」页签面板）、属性查看器（只有宽版式有单独的面板）、曲线。</summary>
public enum ReactorPanel { Program, Vessel, Props, Curves }

/// <summary>
/// 反应釜页面板网格的结果：宽版式（三列）/ 页签版式、程序栏每行 2 张（<see cref="Prog2"/>）、列宽、行高、各面板相对网格的矩形（关掉的、版式里没有的面板不在里面）。
/// 面板之间 1 px（网格底色 --line-strong 透出来就是分隔线）。放大的面板（is-max）盖住整个网格，界面自己处理。
/// </summary>
public sealed record ReactorPageGrid(bool Wide, bool Prog2, IReadOnlyList<double> Columns, IReadOnlyList<double> Rows, IReadOnlyDictionary<ReactorPanel, FlowRect> Panels)
{
    /// <summary>面板的矩形；关掉 / 没有为 null。</summary>
    public FlowRect? this[ReactorPanel p] => Panels.TryGetValue(p, out var r) ? r : null;
}

/// <summary>
/// 反应釜页的面板网格（样稿 js/pages/reactor.js WIDE_MIN / PROG2_MIN 与 css/reactor.css .reactor-grid 的网格规则，按 CSS Grid 的算法算列宽；
/// ReactorPageLayoutTests 用样稿在 WebKit 里量到的面板矩形核对，含关掉面板后的补位）。
/// <list type="bullet">
/// <item>宽版式（网格宽 ≥ 1800，支持的屏幕上不出现）：程序 minmax(558, 34%) ｜ 釜体 minmax(724, 1fr) ｜ 属性查看器 minmax(516, 30%)；行 1.04 : 1；
///   「程序 | 釜体 | 属性」/「程序 | 曲线 | 曲线」。多出来的宽度先平分给程序和属性栏（到 34 % / 30 % 为止），再给釜体。</item>
/// <item>页签版式：程序 clamp(320, 36%, 400)（每行 1 张）或宽 ≥ 1100 时 minmax(558, 46%)（每行 2 张）｜「釜体 / 属性查看器」页签 + 曲线；行 1.08 : 1。</item>
/// <item>关掉面板：其余面板补位（「视图 › 重置布局」恢复），规则与样稿的 :has 选择器相同（关两块的规则优先于关一块的，同级时后写的优先）；
///   关掉「程序」时右边一栏铺满整个宽度。</item>
/// </list>
/// 输入是网格本身的宽高（反应釜页宽 / 高减去顶部状态条）。只看父元素给的尺寸（R4）。
/// </summary>
public static class ReactorPageLayout
{
    /// <summary>宽版式的最小网格宽（558 + 724 + 516 + 2）；程序栏每行 2 张的最小宽。</summary>
    public const double WideMin = 1800, Prog2Min = 1100, Gap = 1;
    /// <summary>上下两行的比例：宽版式 1.04 : 1，页签版式 1.08 : 1（1366 × 768 也能放下两行属性卡片）。</summary>
    public const double WideRowRatio = 1.04, NarrowRowRatio = 1.08;

    public static bool IsWide(double width) => width >= WideMin;

    public static bool IsProg2(double width) => !IsWide(width) && width >= Prog2Min;

    private const string P = "prog", V = "vessel", S = "props", C = "curves";

    private static readonly (ReactorPanel[] When, string[][] Areas)[] WideRules =
    {
        (new[] { ReactorPanel.Vessel }, new[] { new[] { P, S, S }, new[] { P, C, C } }),
        (new[] { ReactorPanel.Props }, new[] { new[] { P, V, V }, new[] { P, C, C } }),
        (new[] { ReactorPanel.Vessel, ReactorPanel.Props }, new[] { new[] { P, C, C }, new[] { P, C, C } }),
        (new[] { ReactorPanel.Curves }, new[] { new[] { P, V, S }, new[] { P, V, S } }),
        (new[] { ReactorPanel.Program }, new[] { new[] { V, V, S }, new[] { C, C, C } }),
        (new[] { ReactorPanel.Props, ReactorPanel.Curves }, new[] { new[] { P, V, V }, new[] { P, V, V } }),
        (new[] { ReactorPanel.Vessel, ReactorPanel.Curves }, new[] { new[] { P, S, S }, new[] { P, S, S } }),
        (new[] { ReactorPanel.Program, ReactorPanel.Curves }, new[] { new[] { V, V, S }, new[] { V, V, S } }),
        (new[] { ReactorPanel.Program, ReactorPanel.Props }, new[] { new[] { V, V, V }, new[] { C, C, C } }),
        (new[] { ReactorPanel.Program, ReactorPanel.Vessel }, new[] { new[] { S, S, S }, new[] { C, C, C } }),
    };

    private static readonly (ReactorPanel[] When, string[][] Areas)[] NarrowRules =
    {
        (new[] { ReactorPanel.Vessel }, new[] { new[] { P, C }, new[] { P, C } }),
        (new[] { ReactorPanel.Curves }, new[] { new[] { P, V }, new[] { P, V } }),
        (new[] { ReactorPanel.Program }, new[] { new[] { V, V }, new[] { C, C } }),
        (new[] { ReactorPanel.Program, ReactorPanel.Curves }, new[] { new[] { V, V }, new[] { V, V } }),
        (new[] { ReactorPanel.Program, ReactorPanel.Vessel }, new[] { new[] { C, C }, new[] { C, C } }),
        (new[] { ReactorPanel.Vessel, ReactorPanel.Curves }, new[] { new[] { P, P }, new[] { P, P } }),
    };

    /// <summary>
    /// 算网格。<paramref name="width"/> / <paramref name="height"/> = 网格的宽高；<paramref name="closed"/> = 关掉的面板（页签版式里的 Props 不算面板，忽略）。
    /// </summary>
    public static ReactorPageGrid Compute(double width, double height, IEnumerable<ReactorPanel>? closed = null)
    {
        bool wide = IsWide(width), prog2 = IsProg2(width);
        var shut = new HashSet<ReactorPanel>(closed ?? Array.Empty<ReactorPanel>());
        if (!wide) shut.Remove(ReactorPanel.Props);
        double[] cols = wide ? WideColumns(width) : NarrowColumns(width, prog2);
        double ratio = wide ? WideRowRatio : NarrowRowRatio;
        double avail = Math.Max(0, height - Gap);
        double[] rows = { avail * ratio / (ratio + 1), avail / (ratio + 1) };

        var areas = wide ? new[] { new[] { P, V, S }, new[] { P, C, C } } : new[] { new[] { P, V }, new[] { P, C } };
        int best = -1;
        var rules = wide ? WideRules : NarrowRules;
        for (int i = 0; i < rules.Length; i++)
        {
            if (!rules[i].When.All(shut.Contains)) continue;
            if (best < 0 || rules[i].When.Length >= rules[best].When.Length) best = i;
        }
        if (best >= 0) areas = rules[best].Areas;

        var panels = new Dictionary<ReactorPanel, FlowRect>();
        foreach (var (panel, key) in new[] { (ReactorPanel.Program, P), (ReactorPanel.Vessel, V), (ReactorPanel.Props, S), (ReactorPanel.Curves, C) })
        {
            if (shut.Contains(panel) || (!wide && panel == ReactorPanel.Props)) continue;
            int r0 = int.MaxValue, r1 = -1, c0 = int.MaxValue, c1 = -1;
            for (int r = 0; r < areas.Length; r++)
                for (int c = 0; c < areas[r].Length; c++)
                    if (areas[r][c] == key) { r0 = Math.Min(r0, r); r1 = Math.Max(r1, r); c0 = Math.Min(c0, c); c1 = Math.Max(c1, c); }
            if (r1 < 0) continue;
            double x = cols.Take(c0).Sum() + c0 * Gap, w = cols.Skip(c0).Take(c1 - c0 + 1).Sum() + (c1 - c0) * Gap;
            double y = rows.Take(r0).Sum() + r0 * Gap, h = rows.Skip(r0).Take(r1 - r0 + 1).Sum() + (r1 - r0) * Gap;
            panels[panel] = new FlowRect(x, y, w, h);
        }
        return new ReactorPageGrid(wide, prog2, Array.AsReadOnly(cols), Array.AsReadOnly(rows), panels);
    }

    /// <summary>
    /// 宽版式三列（CSS Grid）：起始 558 / 724 / 516；剩余宽度平分给程序栏（上限 34 %）和属性栏（上限 30 %），一栏到上限就停、剩下的继续给另一栏；
    /// 最后还剩的给釜体栏（1fr）。
    /// </summary>
    private static double[] WideColumns(double w)
    {
        double[] b = { 558, 724, 516 };
        double[] lim = { Math.Max(558, 0.34 * w), double.PositiveInfinity, Math.Max(516, 0.30 * w) };
        double free = w - 2 * Gap - b.Sum();
        var grow = new List<int> { 0, 2 };
        while (free > 1e-9 && grow.Count > 0)
        {
            double share = free / grow.Count;
            double head = grow.Min(i => lim[i] - b[i]);
            if (head >= share)
            {
                foreach (int i in grow) b[i] += share;
                free = 0;
                break;
            }
            foreach (int i in grow) b[i] += head;
            free -= head * grow.Count;
            grow.RemoveAll(i => lim[i] - b[i] <= 1e-9);
        }
        if (free > 0) b[1] += free;
        return b;
    }

    /// <summary>页签版式两列：程序 clamp(320, 36%, 400) 或 minmax(558, 46%)（每行 2 张）｜其余（1fr）。</summary>
    private static double[] NarrowColumns(double w, bool prog2)
    {
        double c0 = prog2 ? Math.Min(Math.Max(558, 0.46 * w), Math.Max(558, w - Gap)) : Math.Clamp(0.36 * w, 320, 400);
        return new[] { c0, Math.Max(0, w - Gap - c0) };
    }
}
