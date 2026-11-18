#if DEBUG
namespace ParallelReactor.Control.Shot;

/// <summary>
/// 设置 → 温控 / PID 的自整定（2026-11-18 用户确认的设计；参照图 tools/snap-fusion-refs.sh 的 page-set-pid*）：
/// 中温档勾了 RV5（可以开始）、RV5 整定中（26 min，第 2/3 次振荡）、整定完成（36 min，新旧 PID 对比）。地址参数同样稿：pidsel=5、tune=run|done。
/// </summary>
public static partial class ShotPlan
{
    private static readonly Spec[] PartPid =
    {
        new("page-set-pid", "set", "dev=1&apop=0&set=pid&pidsel=5", Both),
        new("page-set-pid-tuning", "set", "dev=1&apop=0&set=pid&pidsel=5&tune=run", Both),
        new("page-set-pid-done", "set", "dev=1&apop=0&set=pid&pidsel=5&tune=done", Both),
    };
}
#endif
