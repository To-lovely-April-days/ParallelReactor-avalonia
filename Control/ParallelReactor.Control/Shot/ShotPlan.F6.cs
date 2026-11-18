#if DEBUG
using Avalonia;

namespace ParallelReactor.Control.Shot;

/// <summary>
/// 融合版 F6 程序页的截图（参照图同名，tools/snap-fusion-refs.sh 的「F6 程序页」一组）：
/// 单路程序（pgm=one）、单路的预运行检查、条件矩阵（pgm=mx；不给 pgm 的 page-prog 也是矩阵，同样稿默认）、矩阵的预运行检查、
/// 3号机 16 路的 RV9–16（pgbank=2）、RV5 溶液体积没填（pgvol=5:）。
/// </summary>
public static partial class ShotPlan
{
    // 属性（不是字段）：分部类跨文件的静态字段初始化顺序不确定，All 初始化时要能拿到
    private static Spec[] F6 => new[]
    {
        new Spec("page-prog-mx", "prog", "dev=1&apop=0&pgm=mx", Both),
        new Spec("page-prog-one", "prog", "dev=1&apop=0&pgm=one", Both),
        new Spec("page-prog-one-check", "prog", "dev=1&apop=0&pgm=one&dialog=pgcheck", Both),
        new Spec("page-prog-mx-check", "prog", "dev=1&apop=0&pgm=mx&dialog=pgcheck", Both),
        new Spec("page-prog-mx-d3", "prog", "dev=3&apop=0&pgm=mx&pgbank=2", Both),
        new Spec("page-prog-mx-volbad", "prog", "dev=1&apop=0&pgm=mx&pgvol=5:", Both),
    };
}
#endif
