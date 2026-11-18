#if DEBUG
using Avalonia;

namespace ParallelReactor.Control.Shot;

public static partial class ShotPlan
{
    /// <summary>
    /// 融合版 F4（主页其余）：通道面板的气路图 / 卡片 / 棒图（地址参数 mv=gas|vessel|bar，同样稿），1 / 2 / 3 号机（8 / 4 / 16 路）各一张；
    /// 总览（不选设备）沿用 F2 的 home-overview。参照图同名（tools/snap-fusion-refs.sh 的 F4 一段）。样稿的 home-maint（维护模式）电脑端没有接口，不拍。
    /// 用方法而不是静态字段：分文件的静态字段初始化顺序不定（All 在 ShotPlan.cs 里用到它）。
    /// </summary>
    private static Spec[] F4Specs()
    {
        PixelSize[] both = { new(1920, 1080), new(1366, 768) };
        return new[]
        {
            new Spec("home-gas", "main", "dev=1&apop=0&mv=gas", both),
            new Spec("home-vessel", "main", "dev=1&apop=0&mv=vessel", both),
            new Spec("home-bar", "main", "dev=1&apop=0&mv=bar", both),
            new Spec("home-gas-dev2", "main", "dev=2&apop=0&mv=gas", both),
            new Spec("home-gas-dev3", "main", "dev=3&apop=0&mv=gas", both),
            new Spec("home-vessel-dev2", "main", "dev=2&apop=0&mv=vessel", both),
            new Spec("home-vessel-dev3", "main", "dev=3&apop=0&mv=vessel", both),
            new Spec("home-bar-dev2", "main", "dev=2&apop=0&mv=bar", both),
            new Spec("home-bar-dev3", "main", "dev=3&apop=0&mv=bar", both),
        };
    }
}
#endif
