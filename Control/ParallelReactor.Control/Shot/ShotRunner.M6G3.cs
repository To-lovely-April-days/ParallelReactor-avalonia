#if DEBUG
using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Views.Shell;

namespace ParallelReactor.Control.Shot;

public static partial class ShotRunner
{
    /// <summary>M6 第 3 组的截图准备（Setup 以「m6g3-」开头）：演示数据重置之后、外框重置之前。做了事返回 true。</summary>
    private static bool M6G3Pre(Window window, ShellVm vm, ShotPlan.Spec spec) => M6G3Known(spec.Setup ?? "");

    /// <summary>
    /// M6 第 3 组的截图准备：路由打开、页面重置之后，冻结之前（见 ShotPlan.M6G3.cs）。先排一遍版（检查器刚换了内容）再找控件。
    /// </summary>
    private static void M6G3Post(Window window, ShellVm vm, ShotPlan.Spec spec)
    {
        if (!M6G3Known(spec.Setup ?? "")) return;
        window.UpdateLayout();
        var insp = window.GetVisualDescendants().OfType<InspectorView>().First();
        switch (spec.Setup)
        {
            case "m6g3-tip-runall":
            {
                // 样稿 js：mouseover「#pr-insp [data-reason]」（第一个带原因的是「全部运行」）后等 450 ms
                var runAll = insp.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "全部运行" && b.IsEffectivelyVisible);
                ToolTip.SetIsOpen(runAll, true);
                // 拍完这张要关掉：下一张截图的检查器还是同一个按钮（换了设备、换了提示），提示会一直开着 —— 下一张截图重置外框（ResetView）时关
                void Close(object? s, EventArgs e)
                {
                    vm.LayoutReset -= Close;
                    ToolTip.SetIsOpen(runAll, false);
                }
                vm.LayoutReset += Close;
                break;
            }
            case "m6g3-toast-tones":
                // 样稿 js：PR.toast(…, 'ok' / 'info' / 'err')
                vm.Toast("1号机 · RV1 设定温度 120 ℃", ToastKind.Ok);
                vm.Toast("2号机 · RV4 未在运行 · 已发送停止", ToastKind.Info);
                vm.Toast("急停已发送到 1 台：1号机 · 复位需在触屏操作", ToastKind.Err);
                break;
            case "m6g3-clamp-sp":
            {
                // 样稿 js：input[name=sp] 设 999 再失焦（focusout → clampNum）
                var sp = insp.GetVisualDescendants().OfType<NumBox>().First(n => n.Label == "设定温度" && n.IsEffectivelyVisible);
                var input = sp.GetVisualDescendants().OfType<TextBox>().First();
                input.Focus(NavigationMethod.Tab);
                input.Text = "999";
                window.FocusManager?.ClearFocus();   // 失焦（同样稿 blur：之后没有焦点环）
                break;
            }
        }
    }

    /// <summary>这一组认得的准备名（Setup 以「m6g3-」开头、只在 Post 里做事的也要列上）。</summary>
    private static bool M6G3Known(string setup) => setup is "m6g3-tip-runall" or "m6g3-toast-tones" or "m6g3-clamp-sp";
}
#endif
