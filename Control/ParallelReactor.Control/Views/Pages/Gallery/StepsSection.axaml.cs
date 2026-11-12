using System.Collections.ObjectModel;
using Avalonia.Controls;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Layout;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Views.Pages.Reactor;

namespace ParallelReactor.Control.Views.Pages.Gallery;

/// <summary>
/// 陈列页「步骤卡片」分区（MOCK placeholder.js 的 ui.stepFlow(d1, c11, c11.program, { perRow: flowPer(w, n) })）：数据 = 样稿冻结时的演示数据
/// （新建一份冻结的 Core DemoFleet，同「釜体」「曲线」分区）—— 1号机 RV1「加氢-标准」，第 3 步当前「当前 · 剩 42 min」；卡片与反应釜页共用（StepCardView），
/// 条件框是普通的（样稿 ui.stepCond：不按卡宽改写标签、没有进度线）。
/// 每行张数：陈列区宽 + 两层内边距与边框（样稿 flowPer 的 w 是 .gallery 的宽 = 这一块的宽 + 82）。
/// </summary>
public partial class StepsSection : UserControl
{
    /// <summary>这一块的宽到样稿 flowPer 的 w：.gallery-in 左右 24 + 分区边框 2 + 分区内边距 32 + 画布边框 2（再加 22 的余量在 GalleryPerRow 里扣）。</summary>
    public const double ToGalleryWidth = 2 * 24 + 2 + 2 * 16;

    private readonly ObservableCollection<StepCardVm> _steps = new();

    public StepsSection()
    {
        InitializeComponent();
        var fleet = new DemoFleet(DemoParams.Default with { Freeze = true });
        var d1 = fleet.Device(1);
        var c11 = d1?.Channel(1);
        if (d1 != null && c11?.Program is { } p)
        {
            var pr = d1.Progress(c11);
            for (int i = 0; i < p.Steps.Count; i++)
            {
                var vm = new StepCardVm(i);
                vm.Apply(p, d1.Model, c11.N, d1.Alias, pr, online: true, preview: false, plain: true);
                _steps.Add(vm);
            }
        }
        Flow.Items = _steps;
        Probe.WidthChanged += w => Flow.GalleryPer = StepFlowLayout.GalleryPerRow(w + ToGalleryWidth, _steps.Count);
    }

    /// <summary>步骤卡片画布（核对用）。</summary>
    public StepFlowPanel FlowControl => Flow;
}
