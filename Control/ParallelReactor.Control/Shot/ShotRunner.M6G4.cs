#if DEBUG
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Shot;

public static partial class ShotRunner
{
    /// <summary>M6 第 4 组的截图准备（Setup 以「m6g4-」开头）：演示数据重置之后、外框重置之前。做了事返回 true。</summary>
    private static bool M6G4Pre(Window window, ShellVm vm, ShotPlan.Spec spec) => false;

    /// <summary>
    /// M6 第 4 组的截图准备：路由打开、页面重置之后，冻结之前。m6g4-maxcurves = 放大反应釜页的「曲线」面板（同样稿 JS 点曲线面板的 □）。
    /// 先排一遍：刚建的页面视图这时还不在可视树里（同 rvmaxprog）。
    /// </summary>
    private static void M6G4Post(Window window, ShellVm vm, ShotPlan.Spec spec)
    {
        if (spec.Setup != "m6g4-maxcurves" || vm.Page is not Pages.ReactorPageVm rp) return;
        window.UpdateLayout();
        var curves = window.GetVisualDescendants().OfType<Views.Pages.Reactor.ReactorCurvesPanel>().FirstOrDefault(p => ReferenceEquals(p.DataContext, rp.Curves))
            ?? throw new System.InvalidOperationException("m6g4-maxcurves：反应釜页上找不到「曲线」面板");
        curves.PanelControl.IsMaximized = true;
    }

    /// <summary>这一组认得的准备名（Setup 以「m6g4-」开头、只在 Post 里做事的也要列上）。</summary>
    private static bool M6G4Known(string setup) => setup == "m6g4-maxcurves";
}
#endif
