#if DEBUG
using System;
using Avalonia;
using Avalonia.Media;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Views.Pages.Overview;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// M6 总览读数「-0.001」（6 个字符，样稿的 data-len 阶梯没有这一档：紧凑卡 18 px）：单位放不放得下按文字小数的前进宽度判断（同 WebKit），
/// 不按取整后的框宽 —— 1366 的紧凑卡内容宽 92.5：61.89 + 3 + 27.14 = 92.03 放得下（样稿写「-0.001 MPa」），按框宽 62 + 3 + 28 = 93 以前把单位挤掉了。
/// 真字体（无头 Avalonia：Inter + 苹方，同截图）。
/// </summary>
[Collection(M5ReviewHeadless.Collection)]
public sealed class M6OverviewReadTests
{
    [Fact]
    public void ReadFit_UsesFractionalAdvances() =>
        // 样稿 1366 紧凑卡（WebKit 量的）：放得下；差 0.5 px 放不下
        Assert.Equal((true, false), OvFit.Read(92.5, 61.89, 27.14, 0, true, false));

    [Fact]
    public void NegativeThousandth_KeepsItsUnit_WhenTheAdvancesFit() => M5ReviewHeadless.Run(() =>
    {
        (OvReadRow Row, PrText Value, PrText Unit) Make()
        {
            var value = new PrText { Text = "-0.001", FontWeight = FontWeight.SemiBold };
            value.Classes.Add("num");
            var unit = new PrText { Text = "MPa", FontSize = 13 };
            var row = new OvReadRow { ValueSize = 18, ValueLineHeight = 22, Height = 22 };
            row.Children.Add(value);
            row.Children.Add(unit);
            row.Children.Add(new PrText { Text = "" });
            return (row, value, unit);
        }

        var (probe, pv, pu) = Make();
        var win = new Avalonia.Controls.Window { Width = 400, Height = 100, Content = new Avalonia.Controls.StackPanel { Children = { probe } } };
        win.Show();
        try
        {
            probe.Measure(new Size(400, 22));
            double nat = pv.NaturalWidth + OvReadRow.Gap + pu.NaturalWidth;
            double box = pv.DesiredSize.Width + OvReadRow.Gap + pu.DesiredSize.Width;
            string facts = $"「-0.001」{pv.NaturalWidth:0.##}（框 {pv.DesiredSize.Width}）+ 3 +「MPa」{pu.NaturalWidth:0.##}（框 {pu.DesiredSize.Width}）= {nat:0.##}（框 {box}）";
            Assert.True(box - nat > 0.2, "这一例要落在小数宽度与取整宽度之间：" + facts);

            // 放得下（小数）、按框宽放不下：单位照写（以前不写）
            double fits = nat + Math.Min(0.3, (box - nat) / 2);
            probe.Measure(new Size(fits, 22));
            Assert.True(probe.Chosen.Unit, $"宽 {fits:0.##}：单位应该写（{facts}）");
            Assert.Equal(18, probe.Chosen.Size);

            // 小数也放不下：不写（同样稿 flex-wrap 换到看不见的第二行），数值不缩
            probe.Measure(new Size(nat - 0.3, 22));
            Assert.False(probe.Chosen.Unit, $"宽 {nat - 0.3:0.##}：单位放不下（{facts}）");
            Assert.Equal(18, probe.Chosen.Size);
        }
        finally { win.Close(); }
    });
}
#endif
