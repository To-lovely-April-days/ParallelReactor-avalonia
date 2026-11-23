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
        // 模板库（设计稿 research/fusion-port/tpl-design 方向 A）：新建程序窗口（选内置模板 / 空白 / 我的模板）、存为模板、程序库为空、删除程序
        new Spec("tpl-new", "prog", "dev=1&apop=0&pgm=one&pgp=H1&dialog=pgnew:T1", Both),
        new Spec("tpl-new-blank", "prog", "dev=1&apop=0&pgm=one&pgp=H1&dialog=pgnew:blank", Both),
        new Spec("tpl-new-mine", "prog", "dev=1&apop=0&pgm=one&pgp=H1&dialog=pgnew:U1", Both),
        new Spec("tpl-save", "prog", "dev=1&apop=0&pgm=one&pgp=H1&dialog=tplsave", Both),
        new Spec("tpl-empty", "prog", "dev=1&apop=0&pgm=one&pgempty=1", Both),
        new Spec("tpl-delete", "prog", "dev=1&apop=0&pgm=one&pgp=IH&dialog=pgdel", Both),
    };
}
#endif
