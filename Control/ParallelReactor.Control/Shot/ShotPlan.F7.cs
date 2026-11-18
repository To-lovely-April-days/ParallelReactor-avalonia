#if DEBUG
using Avalonia;

namespace ParallelReactor.Control.Shot;

/// <summary>
/// 融合版 F7「记录」页的截图（参照图 = tools/snap-fusion-refs-f7.sh 按样稿点出来的同名状态；page-rec 在 Fusion 里，是默认状态：1号机进行中的会话）。
/// 地址参数：rc=会话号、rcch=选中的通道、rcm=G|T|P 曲线、rct=fl 数据文件页签（见 RecordsPageVm.ApplyParams）。
/// </summary>
public static partial class ShotPlan
{
    private static readonly Spec[] PartF7 =
    {
        new("rec-all", "rec", "dev=0&apop=0", Both),
        new("rec-hist", "rec", "dev=1&apop=0&rc=PT-20261114-0915", Both),
        new("rec-ch", "rec", "dev=1&apop=0&rc=PT-20261114-0915&rcch=6", Both),
        new("rec-ovt", "rec", "dev=1&apop=0&rcm=T", Both),
        new("rec-ovp", "rec", "dev=1&apop=0&rcm=P", Both),
        new("rec-files", "rec", "dev=1&apop=0&rct=fl", Both),
        new("rec-hold", "rec", "dev=0&apop=0&rc=LK-20261115-0912", Both),
        new("rec-tight", "rec", "dev=1&apop=0&rc=PT-20261110-1600", Both),
        new("rec-int", "rec", "dev=0&apop=0&rc=PT-20261113-1402", Both),
        new("rec-warn", "rec", "dev=1&apop=0&rc=PT-20261112-0930", Both),
    };
}
#endif
