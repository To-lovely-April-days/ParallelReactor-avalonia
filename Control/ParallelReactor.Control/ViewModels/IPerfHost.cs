using ParallelReactor.Client;

namespace ParallelReactor.Control.ViewModels;

/// <summary>
/// 性能测试视图模型（<see cref="PerfViewModel"/>）的宿主（PORT-PLAN §5 M2「PerfViewModel 改为依赖 IPerfHost，接口只有 Connection、ClientName、IsVisible、Toast 四个成员」）。
/// 实现：Pages/PerfPageVm（新外壳里的性能测试页）；测试可以给假的宿主。
/// </summary>
public interface IPerfHost
{
    /// <summary>要测试的设备连接（没有连上为 null）。</summary>
    IReactorConnection? Connection { get; }

    /// <summary>本机客户端名（报告里的操作员）。</summary>
    string ClientName { get; }

    /// <summary>性能测试页是当前页（不在看时、又没在测试，就不更新曲线）。</summary>
    bool IsVisible { get; }

    /// <summary>提示（新外壳的 toast）。</summary>
    void Toast(string text);
}
