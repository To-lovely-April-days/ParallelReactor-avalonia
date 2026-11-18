#if DEBUG
using Avalonia;

namespace ParallelReactor.Control.Shot;

/// <summary>融合版 F5（报警页 / 曲线页）的截图：参照图同名（tools/snap-fusion-refs.sh 的「F5」一段）。page-alarm / page-curve 本身在 <see cref="ShotPlan"/> 的 Fusion 里。</summary>
public static partial class ShotPlan
{
    private static readonly Spec[] StageF5 =
    {
        // 报警页：全部设备（多一列「设备」）· 选中已解除的报警（RV6 超温，样稿 H2）· 筛选「未确认」
        new Spec("page-alarm-all", "alarm", "dev=0&apop=0", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }),
        new Spec("page-alarm-sel", "alarm", "dev=1&apop=0&alsel=h2", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }),
        new Spec("page-alarm-ua", "alarm", "dev=1&apop=0&als=ua", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }),
        // 曲线页：1 h + 气体消耗（三张图）· 只选 4 路（末端标签）+ 设定值 · 十字光标 13:53
        new Spec("page-curve-h1", "curve", "dev=1&apop=0&cvr=h1&cvv=TPG", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }),
        new Spec("page-curve-sel", "curve", "dev=1&apop=0&cvsel=1,2,3,4&cvsp=1", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }),
        new Spec("page-curve-cur", "curve", "dev=1&apop=0&cvcur=13:53", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }),
    };
}
#endif
