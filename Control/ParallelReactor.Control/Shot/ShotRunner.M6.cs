#if DEBUG
using Avalonia.Controls;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Shot;

/// <summary>
/// M6 各组的截图准备（ShotPlan.Spec.Setup 以「m6g&lt;组号&gt;-」开头；每组一个文件 ShotRunner.M6G1.cs … M6G5.cs，互不冲突）：
/// Pre 在演示数据重置、地址参数套用之后，外框重置（ResetView）之前；Post 在路由打开、页面重置之后，冻结（Frozen.Apply）之前。
/// 每组只处理自己前缀的名字，自己前缀下不认识的名字抛异常。
/// </summary>
public static partial class ShotRunner
{
    private static void M6Pre(Window window, ShellVm vm, ShotPlan.Spec spec)
    {
        bool handled = M6G1Pre(window, vm, spec) | M6G2Pre(window, vm, spec) | M6G3Pre(window, vm, spec) | M6G4Pre(window, vm, spec) | M6G5Pre(window, vm, spec);
        bool known = handled || M6Known(spec.Setup!);
        if (!known) throw new System.NotSupportedException($"截图准备「{spec.Setup}」没有哪一组认（ShotRunner.M6G*.cs）");
    }

    private static void M6Post(Window window, ShellVm vm, ShotPlan.Spec spec)
    {
        M6G1Post(window, vm, spec);
        M6G2Post(window, vm, spec);
        M6G3Post(window, vm, spec);
        M6G4Post(window, vm, spec);
        M6G5Post(window, vm, spec);
    }

    /// <summary>只在 Post 里做事的准备名也要有组认领（各组的 M6G*Known）。</summary>
    private static bool M6Known(string setup) => M6G1Known(setup) || M6G2Known(setup) || M6G3Known(setup) || M6G4Known(setup) || M6G5Known(setup);
}
#endif
