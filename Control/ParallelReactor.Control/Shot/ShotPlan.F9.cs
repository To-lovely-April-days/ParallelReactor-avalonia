#if DEBUG
namespace ParallelReactor.Control.Shot;

/// <summary>
/// 融合版 F9：其它停止类操作的确认（「停止一律先确认」，样稿没有，外观同 home-stopch）：停止加热、停用通道、关共用搅拌。没有参照图。
/// </summary>
public static partial class ShotPlan
{
    private static readonly Spec[] PartF9Confirm =
    {
        new("home-heatstop", "main", "dev=1&apop=0&dialog=heatstop:1:1", Both),
        new("home-disable", "main", "dev=1&apop=0&dialog=disable:1:1", Both),
        new("home-stiroff", "main", "dev=1&apop=0&dialog=stiroff:1", Both),
    };
}
#endif
