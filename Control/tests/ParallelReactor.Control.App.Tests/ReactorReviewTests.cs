using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Layout;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Views.Pages.Reactor;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// M5 复核的几处小修（纯函数 / 视图模型，不启动 Avalonia）：面板网格取整后的 1 px 分隔线、组件陈列的普通条件框、逐字省略。
/// </summary>
public class ReactorReviewTests
{
    /// <summary>
    /// 面板网格的边按「.5 进位」取整（ReactorGridPanel.Snap）：任何网格宽高下，程序栏与右栏之间、釜体与曲线之间（宽版式还有釜体与属性查看器之间）
    /// 都正好 1 px 的分隔线 —— 银行家舍入在 563.5 / 564.5 这种边上会把分隔线吃掉（复核：窗口宽 1889 → 网格 1225）。
    /// </summary>
    [Fact]
    public void GridSnap_KeepsTheOnePixelDivider_AtEveryWidth()
    {
        var bad = new List<string>();
        foreach (double h in new[] { 600.0, 641.5, 700.25, 752.0, 752.5, 913.5, 1100.0 })
            for (double w = 900; w <= 2600; w += 0.25)
            {
                var g = ReactorPageLayout.Compute(w, h);
                var p = Snap(g[ReactorPanel.Program]!.Value);
                var v = Snap(g[ReactorPanel.Vessel]!.Value);
                var c = Snap(g[ReactorPanel.Curves]!.Value);
                if (v.X - p.Right != 1) bad.Add($"{w}×{h}：程序 | 右栏 {v.X - p.Right} px");
                if (c.Y - v.Bottom != 1) bad.Add($"{w}×{h}：釜体 / 曲线 {c.Y - v.Bottom} px");
                if (g.Wide && g[ReactorPanel.Props] is { } sp && Snap(sp).X - v.Right != 1) bad.Add($"{w}×{h}：釜体 | 属性 {Snap(sp).X - v.Right} px");
                if (p.Right > Math.Floor(w + 0.5) || c.Right > Math.Floor(w + 0.5)) bad.Add($"{w}×{h}：超出网格");
                if (bad.Count > 8) break;
            }
        Assert.True(bad.Count == 0, string.Join("\n", bad));
        Assert.Equal(new Rect(0, 0, 564, 672), ReactorGridPanel.Snap(new Rect(0, 0, 563.5, 671.5)));
        Assert.Equal(565, ReactorGridPanel.Snap(new Rect(564.5, 0, 660.5, 10)).X);
        Assert.Equal(564, ReactorGridPanel.Edge(563.4999999999999));
        Assert.Equal(565, ReactorGridPanel.Edge(564.5000000000001));
    }

    private static Rect Snap(FlowRect r) => ReactorGridPanel.Snap(new Rect(r.X, r.Y, r.W, r.H));

    /// <summary>组件陈列的条件框（样稿 ui.stepCond）一律是普通的浅灰框：当前步 / 报警暂停 / 中止的样式类只在反应釜页上。</summary>
    [Fact]
    public void GalleryCondition_IsAlwaysPlain()
    {
        var model = ModelProfiles.Embedded.Get("PR8")!;
        var p = new ProgramRecord
        {
            Name = "加氢-标准", Status = ProgramStatus.Running, StepIndex = 2,
            Steps = new[] { new ProgramStep(StepKind.Heat) { Sp = 120, EstMin = 10 }, new ProgramStep(StepKind.Press) { Target = 1, Gas = "a", EstMin = 5 },
                new ProgramStep(StepKind.React) { Minutes = 72, End = new StepEnd(EndKind.Time, 72) }, new ProgramStep(StepKind.End) { CoolTo = 50 } },
            StartedAt = DateTime.Now.AddMinutes(-30), StepStartedAt = DateTime.Now.AddMinutes(-10), StepDurS = 72 * 60,
        };
        var page = new StepCardVm(2);
        page.Apply(p, model, 1, "1号机", null, true, preview: false, plain: false);
        Assert.True(page.IsCur);
        Assert.True(page.CondCur);
        var gallery = new StepCardVm(2);
        gallery.Apply(p, model, 1, "1号机", null, true, preview: false, plain: true);
        Assert.True(gallery.IsCur);                      // 卡片照样是当前步（ui.stepCard 有状态）
        Assert.False(gallery.CondCur);                   // 条件框不是
        var held = p with { Status = ProgramStatus.Held };
        var gh = new StepCardVm(2);
        gh.Apply(held, model, 1, "1号机", null, true, preview: false, plain: true);
        Assert.True(gh.Held);
        Assert.False(gh.CondHeld);
        var aborted = p with { Status = ProgramStatus.Aborted };
        var ga = new StepCardVm(2);
        ga.Apply(aborted, model, 1, "1号机", null, true, preview: false, plain: true);
        Assert.True(ga.IsAbort);
        Assert.False(ga.CondAbort);
        var pa = new StepCardVm(2);
        pa.Apply(aborted, model, 1, "1号机", null, true, preview: false, plain: false);
        Assert.True(pa.CondAbort);
    }

    /// <summary>
    /// 逐字省略（属性卡片的路径行，Controls/Pr/TextFit）：最长的前缀 +「…」，中文与西文混排时一个字一个字地量，不会整段丢掉
    /// （Avalonia 11.1 的 CharacterEllipsis 在多段文字上把段宽重复扣掉，复核）；放得下原样；不切开代理对。
    /// </summary>
    [Fact]
    public void TextFit_Prefix_IsCharacterLevel()
    {
        // 假字体：中文 14、其余 8、省略号 14
        static double W(string t) => t.Sum(ch => ch == '…' ? 14 : ch > 0x2E80 ? 14 : 8);
        const string path = "/高压加氢测试一号/RV1/设备/加热器";
        Assert.Equal(path, TextFit.Prefix(path, W(path), W));
        Assert.Equal(path, TextFit.Prefix(path, double.PositiveInfinity, W));
        double w = W("/高压加氢测试一号/RV1/设备/加…");
        Assert.Equal("/高压加氢测试一号/RV1/设备/加…", TextFit.Prefix(path, w, W));
        Assert.Equal("/高压加氢测试一号/RV1/设备/…", TextFit.Prefix(path, w - 1, W));
        Assert.Equal("…", TextFit.Prefix(path, 10, W));
        Assert.Equal("", TextFit.Prefix(null, 100, W));
        // 代理对（𠀋 = U+2000B）不切开
        const string sp = "A𠀋B𠀋C";
        static double M(string t) => t.Sum(ch => char.IsSurrogate(ch) ? 7 : ch == '…' ? 14 : 8);
        Assert.Equal(36, M("A𠀋…"));
        Assert.Equal("A𠀋…", TextFit.Prefix(sp, 36.5, M));
        Assert.Equal("A…", TextFit.Prefix(sp, 35.5, M));   // 放不下整个「𠀋」时不留半个
    }
}
