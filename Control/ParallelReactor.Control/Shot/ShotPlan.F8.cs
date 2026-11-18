#if DEBUG
using Avalonia;

namespace ParallelReactor.Control.Shot;

/// <summary>
/// 融合版 F8「设置」「性能测试」页的截图（参照图同名，tools/snap-fusion-refs.sh 的 F8 一组；page-set / page-test 在 Fusion 里，是默认状态：常规 / 摸底测试）。
/// 地址参数：set=gen|conn|night|lim|pid|comm|store|upt|about（设置页这一节，同样稿）、ptm=perf|leak（性能测试的模式）。
/// </summary>
public static partial class ShotPlan
{
    private static readonly Spec[] PartF8 =
    {
        new("page-set-night", "set", "dev=1&apop=0&set=night", Both),
        new("page-set-upt", "set", "dev=1&apop=0&set=upt", Both),
        new("page-set-conn", "set", "dev=1&apop=0&set=conn", Both),
        new("page-set-lim", "set", "dev=1&apop=0&set=lim", Both),
        new("page-set-comm", "set", "dev=1&apop=0&set=comm", Both),
        new("page-set-store", "set", "dev=1&apop=0&set=store", Both),
        new("page-set-about", "set", "dev=1&apop=0&set=about", Both),
        new("page-set-upt-dev3", "set", "dev=3&apop=0&set=upt", Both),
        new("page-set-dev2", "set", "dev=2&apop=0&set=night", Both),
        new("page-test-leak", "test", "dev=1&apop=0&ptm=leak", Both),
        new("page-test-dev2", "test", "dev=2&apop=0", Both),
    };
}
#endif
