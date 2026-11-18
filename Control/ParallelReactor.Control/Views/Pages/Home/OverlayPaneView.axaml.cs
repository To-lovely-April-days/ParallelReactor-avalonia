using Avalonia;
using Avalonia.Controls;

namespace ParallelReactor.Control.Views.Pages.Home;

/// <summary>
/// 曲线面板（见 OverlayPaneView.axaml）。灰带放不下时先不写说明「8 路叠加 · … · 设备按压降计算」，再放不下就不放芯片（样稿 @container pcv：≤ 600 不放芯片）。
/// </summary>
public partial class OverlayPaneView : UserControl
{
    public OverlayPaneView()
    {
        InitializeComponent();
        Head.SizeChanged += (_, _) => Fit();
        Chips.SizeChanged += (_, _) => Fit();
    }

    private bool _fitting;
    private double _optW, _chipsW;

    private void Fit()
    {
        if (_fitting) return;
        _fitting = true;
        try
        {
            double avail = Head.Bounds.Width - Seg.Bounds.Width - 10;
            var inf = new Size(double.PositiveInfinity, double.PositiveInfinity);
            Chips.Measure(inf);
            Opt.Measure(inf);
            // 隐藏时量出来是 0：记住上一次看得见时的宽度
            if (Chips.DesiredSize.Width > 0) _chipsW = Chips.DesiredSize.Width;
            if (Opt.DesiredSize.Width > 0) _optW = Opt.DesiredSize.Width;
            bool showChips = avail >= _chipsW + 8;
            bool showOpt = avail >= _optW + (showChips ? _chipsW + 16 : 0);
            Chips.Classes.Set("off", !showChips);
            Opt.Classes.Set("off", !showOpt);
        }
        finally { _fitting = false; }
    }
}
