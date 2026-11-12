#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Views.Shell;

namespace ParallelReactor.Control.Shot;

/// <summary>
/// 截图与顺序无关（M5 复核）：截图计划里每张都应该像新打开的窗口，跟前一张是什么无关。按截图的同一条路（<see cref="ShotRunner.Show"/>）连着显示两个场景，核对：
/// <list type="bullet">
/// <item>总览 / 设备页卡片的程序没了（ov-trend-sel2 → ov-devsmix：同一张卡片从有程序变成「无程序」）：步骤条没有段（以前旧段留着、画在「无程序」上）。</item>
/// <item>检查器的「最近事件」是这一份演示数据的（演示数据重建后事件 Id 'e13' 会重复，以前留着上一份的文字）。</item>
/// <item>资源管理器换了选中对象（复核的反序截图 rv-estop311 → rv-off41 → rv-noprog313r）：选中的行整行可见，视口里的行与连着显示两次同一个场景相同
/// （以前留着上一个场景的滚动位置，选中的行可能不滚进来）。</item>
/// </list>
/// </summary>
public static partial class ShellChecks
{
    private static async Task OrderChecks(Window window, ShellVm vm, string dir, int width, Action<bool, string> Check, Action<string> Info, List<string> lines)
    {
        var p0 = vm.Fleet.Params ?? DemoParams.Default;
        var size = new PixelSize((int)Math.Round(window.ClientSize.Width), (int)Math.Round(window.ClientSize.Height));
        async Task Show(string name)
        {
            var spec = ShotPlan.All.First(s => s.Name == name);
            ShotRunner.Show(window, spec, size);
            await Settle();
            await Settle();
        }

        // ---------------- 卡片的程序没了：步骤条没有旧段 ----------------
        await Show("ov-trend-sel2");
        var before = window.GetVisualDescendants().OfType<StepBar>().Count(b => b.Segments.Count > 0);
        await Show("ov-devsmix");
        var bars = window.GetVisualDescendants().OfType<StepBar>().Where(b => b.DataContext is OverviewCardVm).ToList();
        var none = bars.Where(b => b.DataContext is OverviewCardVm { HasProgram: false }).ToList();
        var stale = none.Where(b => b.Segments.Count > 0).ToList();
        Check(before > 0 && none.Count > 0 && stale.Count == 0,
            $"ov-trend-sel2 → ov-devsmix：「无程序」卡片的步骤条没有段（{none.Count} 张无程序，{stale.Count} 张留着旧段；上一场景有段的步骤条 {before} 条）");
        var withProg = bars.Where(b => b.DataContext is OverviewCardVm { HasProgram: true, Segments: { } s } && s.Count != b.Segments.Count).ToList();
        Check(withProg.Count == 0, $"有程序的卡片：步骤条的段数与卡片的一致（{withProg.Count} 条不一致）");

        // ---------------- 检查器「最近事件」是这一份数据的 ----------------
        if (vm.Inspector.Body is DeviceInspectorVm di && vm.Fleet.Device(di.D.K) is { } dev)
        {
            var want = dev.Source.Events.Take(5).Select(e => Fmt.Time(e.At) + " " + e.Text).ToList();
            var have = di.Events.Select(e => e.AtText + " " + e.Text).ToList();
            Check(want.Count > 0 && want.SequenceEqual(have),
                $"ov-trend-sel2 → ov-devsmix：检查器「最近事件」是这一份数据的（{string.Join(" | ", have.Take(2))}；应为 {string.Join(" | ", want.Take(2))}）");
        }
        else Check(false, "ov-devsmix：检查器是设备检查器");

        // ---------------- 资源管理器：换了选中对象，选中行可见、滚动位置与新打开相同 ----------------
        if (width >= 1600)
        {
            var explorer = Find<ExplorerView>(window, _ => true);
            var tree = Find<ListBox>(explorer, l => l.Name == "Tree");
            // 看得见的结果：视口里的行（键 + 离视口顶的位置）和选中行的位置。树是虚拟化的：视口以外的行没排、按估计的行高算，
            // 滚动偏移本身跟之前排过哪些行有关（浮层滚动条平时不画），所以比的是视口里画出来的东西
            (string Rows, double Top, double Bottom, double View, bool Found, double Offset) Probe()
            {
                var sv = tree.FindDescendantOfType<ScrollViewer>();
                var row = vm.Explorer.SelectedRow;
                int i = row == null ? -1 : vm.Explorer.Rows.IndexOf(row);
                if (sv == null || i < 0 || tree.ContainerFromIndex(i) is not Avalonia.Controls.Control c || c.TranslatePoint(default, sv) is not { } p)
                    return ("", 0, 0, 0, false, sv?.Offset.Y ?? -1);
                double view = sv.Viewport.Height;
                var shown = tree.GetVisualDescendants().OfType<ListBoxItem>()
                    .Select(li => (Li: li, P: li.TranslatePoint(default, sv)))
                    .Where(x => x.P is { } q && x.Li.IsVisible && q.Y < view && q.Y + x.Li.Bounds.Height > 0 && x.Li.DataContext is XRow)
                    .OrderBy(x => x.P!.Value.Y)
                    .Select(x => ((XRow)x.Li.DataContext!).Key + "@" + x.P!.Value.Y.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
                return (string.Join(" ", shown), p.Y, p.Y + c.Bounds.Height, view, true, sv.Offset.Y);
            }
            // 复核的反序截图：rv-estop311 → rv-off41 → rv-noprog313r（每一张视口里的行都与连着显示两次同一个场景相同）
            string prev = "rv-estop311";
            await Show(prev);
            foreach (var name in new[] { "rv-off41", "rv-noprog313r" })
            {
                await Show(name);
                var seq = Probe();
                await Show(name);
                var again = Probe();
                Info($"资源管理器：{prev} → {name}：选中行 {seq.Top:0.#}–{seq.Bottom:0.#} / 视口 {seq.View:0.#}（偏移 {seq.Offset:0.#}，再显示一次 {again.Offset:0.#}）；视口里 {seq.Rows}");
                Check(seq.Found && seq.Bottom - seq.Top >= 20 && seq.Top >= -0.5 && seq.Bottom <= seq.View - ExplorerView.FadeH + 0.5,
                    $"{prev} → {name}：选中的行排好了、整行可见、不在底部渐隐里（{seq.Top:0.#}–{seq.Bottom:0.#} / {seq.View:0.#}）");
                Check(seq.Found && again.Found && seq.Rows == again.Rows,
                    $"{prev} → {name}：视口里的行与连着显示两次 {name} 相同" + (seq.Rows == again.Rows ? "" : "（" + seq.Rows + " ≠ " + again.Rows + "）"));
                prev = name;
            }
        }
        else Info("资源管理器顺序核对：窄于 1600 时资源管理器收起，跳过");

        vm.Fleet.ResetDemo(p0);
        vm.ApplyDemoParams(p0);
        vm.ResetView();
        await Settle();
    }
}
#endif
