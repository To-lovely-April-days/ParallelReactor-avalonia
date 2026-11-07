using System.Threading.Tasks;
using Avalonia.Threading;
using ParallelReactor.Models;
using ParallelReactor.ViewModels;

namespace ParallelReactor.Commands;

/// <summary>
/// 操作员命令层（C25）：触屏与将来的远程接口共用的唯一写入口。按领域拆成多个 partial 文件。
/// <para>每个 <c>Try*</c> 都是「同步头 + 异步尾」写法（设计稿 §2.6，由架构测试检查）：</para>
/// <list type="number">
/// <item>方法本身不是 async，返回 <c>Task&lt;OpResult&gt;</c>；</item>
/// <item>第一条语句是仲裁 <c>if (Admit(src, ActionClass.…) is { } deny) return deny.AsTask();</c>；</item>
/// <item>守卫、校验和第一次状态修改在同一次 UI 线程调用里完成；</item>
/// <item>需要等硬件的部分放在私有的 <c>*TailAsync</c> 里。</item>
/// </list>
/// <para><c>Try*</c> 内禁止调用 Toast：结果一律通过返回值表达，触屏端由 <see cref="MainViewModel.Local"/> 转成 Toast。</para>
/// </summary>
public sealed partial class OperatorCommands
{
    private readonly MainViewModel _m;

    public OperatorCommands(MainViewModel main, ControlArbiter arbiter)
    {
        _m = main;
        Arbiter = arbiter;
    }

    public ControlArbiter Arbiter { get; }

    /// <summary>
    /// 每个 Try* 的第一条语句：断言在 UI 线程，再做控制权仲裁。null = 放行。
    /// 远程控制中触屏被拒（MODE_REMOTE）按 warn 提示（§5.1），其余拒绝按 err。
    /// </summary>
    private OpResult? Admit(CommandSource src, ActionClass cls, Exposure exp = Exposure.Remote)
    {
        Dispatcher.UIThread.VerifyAccess();
        var d = Arbiter.Check(cls, src, exp);
        return d.Allowed ? null : OpResult.Fail(d.Code!, d.Message!, d.Code == OpCodes.ModeRemote ? "warn" : "err");
    }

    // ============ 公共守卫辅助 ============

    /// <summary>通道号 → 通道。不在 1..8 时返回 CHANNEL_NOT_FOUND。</summary>
    private OpResult? ChannelOr(int id, out Reactor c)
    {
        c = _m.FindReactor(id)!;
        return c != null ? null : OpResult.Fail(OpCodes.ChannelNotFound, $"通道 RV{id} 不存在（有效范围 RV1–RV8）");
    }

    /// <summary>
    /// 该通道正在执行程序（C20）：温度 / 加热 / 启用类操作由程序控制，拒绝（停止 / 停用仍允许，见 C9）。
    /// 文字与级别与原 DrawerViewModel.OwnedByProgram 一致。
    /// </summary>
    private OpResult? ProgramOwned(Reactor c)
        => !_m.Program.IsProgramOwned(c.Id) ? null
           : OpResult.Fail(OpCodes.ChannelProgramOwned, $"RV{c.Id} 正在执行程序 · 请先中止程序", "warn");

    /// <summary>急停锁定（含快速路径已锁、UI 流程尚未跑到）时拒绝启动类操作。msg 为各入口今天的原文。</summary>
    private OpResult? EstopOr(string msg)
        => _m.EstopLocked ? OpResult.Fail(OpCodes.EstopActive, msg) : null;

    /// <summary>启动加热前的公共检查（<see cref="HeatGuard"/>，带错误码）。</summary>
    private OpResult? HeatBlocked(int? id)
        => _m.HeatBlock(id) is { } b ? OpResult.Fail(b.Code, b.Message) : null;

    /// <summary>开始气路序列前的公共检查（<see cref="GasGuard"/>，带错误码）。</summary>
    private OpResult? GasBlocked(bool needPressure = true, int? selfId = null)
        => _m.GasBlock(needPressure, selfId) is { } b ? OpResult.Fail(b.Code, b.Message) : null;

    /// <summary>数值越界 → PARAM_OUT_OF_RANGE（detail 带 min / max / value）。</summary>
    private static OpResult? Invalid(RuleViolation? v)
        => v == null ? null : OpResult.Fail(v.Code, v.Message, "err", v.Detail);

    /// <summary>通道 / 程序的 startedBy 标签（§5.4 / §6.3）：触屏 "local"，远程「远程（客户端名）」——与 ProgramRun.StartedBy 同一规则。</summary>
    internal static string StartedByOf(CommandSource src) => src.IsRemote ? src.Label : "local";
}
