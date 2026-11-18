#if DEBUG
namespace ParallelReactor.Control.Shot;

/// <summary>
/// 压力显示单位（设置 → 常规「压力单位」）的截图：地址参数 punit=bar|psi（同设置页切换；不给 = MPa，每张截图前改回 MPa）。
/// 样稿没有这些状态（只有 MPa），没有参照图，只审计；核对 psi 多出来的位数在表格 / 卡片 / 坐标轴里放得下。
/// </summary>
public static partial class ShotPlan
{
    private static readonly Spec[] PartUnit =
    {
        new("unit-home-bar", "main", "dev=1&apop=0&punit=bar", Both),
        new("unit-home-psi", "main", "dev=1&apop=0&punit=psi", Both),
        new("unit-home-ovp-psi", "main", "dev=1&apop=0&ovm=P&punit=psi", Both),
        new("unit-home-apop-psi", "main", "dev=1&punit=psi", Both),
        new("unit-home-gas-psi", "main", "dev=1&apop=0&mv=gas&punit=psi", Both),
        new("unit-home-vessel-psi", "main", "dev=1&apop=0&mv=vessel&punit=psi", Both),
        new("unit-home-bar16-psi", "main", "dev=3&apop=0&mv=bar&punit=psi", Both),
        new("unit-home-gas16-psi", "main", "dev=3&apop=0&mv=gas&punit=psi", Both),
        new("unit-home-vessel16-psi", "main", "dev=3&apop=0&mv=vessel&punit=psi", Both),
        new("unit-home-run16-psi", "main", "dev=3&apop=0&punit=psi", Both),
        new("unit-prog-mx-bar", "prog", "dev=1&apop=0&pgm=mx&punit=bar", Both),
        new("unit-prog-mx-psi", "prog", "dev=1&apop=0&pgm=mx&punit=psi", Both),
        new("unit-prog-one-mpa", "prog", "dev=1&apop=0&pgm=one&pgstep=2", Both),
        new("unit-prog-one-psi", "prog", "dev=1&apop=0&pgm=one&pgstep=2&punit=psi", Both),
        new("unit-prog-one-bar", "prog", "dev=1&apop=0&pgm=one&pgstep=2&punit=bar", Both),
        new("unit-curve-bar", "curve", "dev=1&apop=0&punit=bar", Both),
        new("unit-curve-psi", "curve", "dev=1&apop=0&punit=psi", Both),
        new("unit-alarm-psi", "alarm", "dev=1&apop=0&punit=psi", Both),
        new("unit-set-psi", "set", "dev=1&apop=0&punit=psi", Both),
        new("unit-set-lim-psi", "set", "dev=1&apop=0&set=lim&punit=psi", Both),
        new("unit-rec-hold-bar", "rec", "dev=0&apop=0&rc=LK-20261115-0912&punit=bar", Both),
        new("unit-test-psi", "test", "dev=1&apop=0&punit=psi", Both),
    };
}
#endif
