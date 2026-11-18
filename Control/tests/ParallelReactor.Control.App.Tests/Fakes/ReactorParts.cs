using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Tests.Fakes;

/// <summary>
/// 反应釜零件的测试宿主（融合版 F2 删掉了 pc-v3 的反应釜页 ReactorPageVm，零件留给 F3 / F6）：同一组零件
/// （釜体 / 属性 <see cref="ReactorVesselVm"/>、曲线 <see cref="ReactorCurvesVm"/>、程序 <see cref="ReactorProgramVm"/>、未安装 <see cref="ReactorUnVm"/>），
/// 刷新方式同旧页面的 Refresh（各零件 Refresh(DeviceVm)），外壳数据变化时跟着刷新。页面本身的东西（状态条、页面拥有的报警）随页面删掉了。
/// </summary>
public sealed class ReactorParts
{
    public ReactorParts(ShellVm shell, int k, int n)
    {
        Shell = shell;
        K = k;
        N = n;
        Vessel = new ReactorVesselVm(shell, k, n);
        Curves = new ReactorCurvesVm(k, n);
        Program = new ReactorProgramVm(k, n);
        Un = new ReactorUnVm(shell, k, n);
        shell.Fleet.Changed += (_, _) => Refresh();
        Refresh();
    }

    public ShellVm Shell { get; }
    public int K { get; }
    public int N { get; }
    public ReactorVesselVm Vessel { get; }
    public ReactorCurvesVm Curves { get; }
    public ReactorProgramVm Program { get; }
    public ReactorUnVm Un { get; }
    public DeviceVm? D => Shell.Fleet.Device(K);

    public void Refresh()
    {
        var d = D;
        Vessel.Refresh(d);
        Curves.Refresh(d);
        Program.Refresh(d);
        Un.Refresh(d);
    }

    /// <summary>新外壳 + 演示数据（冻结），主页选中这一路，零件刷新一次。</summary>
    public static (ShellVm Shell, ReactorParts Page) Open(int k, int n, string query = "freeze=1", int width = 1920, int height = 1080)
    {
        var p = DemoParams.Parse(query);
        if (!p.Freeze) p = p with { Freeze = true };
        var s = new ShellVm(FleetVm.CreateDemo(p));
        s.Toasts.Frozen = true;
        s.Layout.Update(new Avalonia.Size(width, height));
        s.ApplyDemoParams(p);
        if (s.Fleet.Device(k) is { } d && d.Reactor(n) != null) s.OpenChannel(k, n);
        return (s, new ReactorParts(s, k, n));
    }
}
