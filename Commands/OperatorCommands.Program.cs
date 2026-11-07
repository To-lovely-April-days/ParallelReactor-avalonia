using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ParallelReactor.Models;

namespace ParallelReactor.Commands;

/// <summary>程序：草稿编辑闸门、曲线应用、复制到其他通道、整体替换（远程）、下发运行、中止。</summary>
public sealed partial class OperatorCommands
{
    /// <summary>
    /// 程序草稿是否可编辑（与 ProgramViewModel.IsLocked 同一判定）：该通道的程序执行中，或该通道真正在运行（升温 / 反应 / 升压 / 超压）时锁定为只读。
    /// 其他通道的程序不影响本通道编辑（多程序并行）。文字同界面的锁定提示；Quiet：今天只是按钮被禁用，没有 Toast。
    /// </summary>
    private OpResult? EditLocked(Reactor c)
    {
        if (_m.Program.IsProgramOwned(c.Id))
            return OpResult.Fail(OpCodes.ChannelProgramOwned, "程序执行中 · 已锁定为只读", "warn") with { Quiet = true };
        if (c.IsWorking)
            return OpResult.Fail(OpCodes.ChannelBusy, "该通道正在运行 · 程序已锁定为只读", "warn") with { Quiet = true };
        return null;
    }

    /// <summary>
    /// 草稿编辑闸门（§5.7）：仲裁（Control）后重新判断可编辑，通过才在同一次调用里执行 VM 传入的修改。
    /// 触屏的加步骤 / 拖入 / 重排 / 上移下移删除 / 字段键盘 / 字段下拉 / 升温方式都经这里——远程控制中一律被拒（P1 验收项）。
    /// </summary>
    public Task<OpResult> TryEditProgram(CommandSource src, int rv, Func<OpResult> mutate)
    {
        if (Admit(src, ActionClass.Control) is { } deny) return deny.AsTask();
        if (ChannelOr(rv, out var c) is { } nf) return nf.AsTask();
        if (EditLocked(c) is { } locked) return locked.AsTask();
        return mutate().AsTask();
    }

    /// <summary>
    /// 整体替换某通道的程序草稿（远程 <c>PUT /channels/{id}/program</c>；本期只实现方法）。
    /// 可编辑检查 + 全局限值（<see cref="ProgramLimits.Validate"/>）→ PARAM_OUT_OF_RANGE。revision 留到下一期。
    /// </summary>
    public Task<OpResult> TryReplaceProgram(CommandSource src, int rv, IReadOnlyList<StepSnapshot> steps, long? revision = null)
    {
        if (Admit(src, ActionClass.Control) is { } deny) return deny.AsTask();
        if (ChannelOr(rv, out var c) is { } nf) return nf.AsTask();
        if (EditLocked(c) is { } locked) return locked.AsTask();
        var errors = ProgramLimits.Validate(steps, _m.Program.Limits);
        if (errors.Count > 0)
            return OpResult.Fail(OpCodes.ParamOutOfRange,
                errors.Count == 1 ? errors[0] : $"{errors[0]}（另有 {errors.Count - 1} 项超限）", "err", new { errors }).AsTask();

        var list = _m.Program.StepsOf(rv);
        list.Clear();
        foreach (var s in steps) list.Add(ProgramStep.FromSnapshot(s));
        for (int i = 0; i < list.Count; i++) list[i].Ordinal = i + 1;
        if (_m.Program.ChannelOf(rv) is { } ch) ch.IsModified = true;
        _m.Program.OnStepsReplaced(rv);
        return OpResult.Done($"RV{rv} 程序已更新（{list.Count} 步）", "ok", new { rv, steps = list.Count }).AsTask();
    }

    /// <summary>
    /// 把曲线编辑器里的段写回某通道第 stepIndex 步（升温步骤，切成曲线模式）。allChannels = 「全部运行中」范围：
    /// 同序号也是升温步骤的其他通道一并改（跳过已停用和正在执行程序的通道，原逻辑）。
    /// 段温度按全局限值检查（C4）：超限「曲线未应用 · …」err，不改任何通道。
    /// 严格守卫：目标通道正在运行时跳过并写入 Warnings。
    /// </summary>
    public Task<OpResult> TryApplyCurve(CommandSource src, int rv, int stepIndex, IReadOnlyList<HeatSeg> segs, bool allChannels)
    {
        if (Admit(src, ActionClass.Control) is { } deny) return deny.AsTask();
        if (ChannelOr(rv, out var c) is { } nf) return nf.AsTask();
        if (EditLocked(c) is { } locked) return locked.AsTask();
        var steps = _m.Program.StepsOf(rv);
        if (stepIndex < 0 || stepIndex >= steps.Count || steps[stepIndex].Type != StepType.Heat)
            return OpResult.Fail(OpCodes.NotFound, $"RV{rv} 第 {stepIndex + 1} 步不存在或不是升温步骤").AsTask();
        if (Invalid(ValueRules.ProfileSegCount(segs.Count)) is { } cnt) return cnt.AsTask();
        var segSnap = StepSnapshot.Create(StepType.Heat, 0, "升温", "",
            new Dictionary<string, object> { ["mode"] = "curve" }, segs.Select(g => (g.Temp, g.Rate, g.Hold)));
        if (ProgramLimits.CheckStep(segSnap, _m.Program.Limits) is { } bad)
            return OpResult.Fail(OpCodes.ParamOutOfRange, $"曲线未应用 · {bad}").AsTask();

        CopyCurve(steps[stepIndex], segs);
        var applied = new List<int> { rv };
        var warnings = new List<string>();
        if (allChannels)
        {
            foreach (var ch in _m.Program.Channels)
            {
                var r = ch.Reactor;
                if (r.IsIdle || r.Id == rv || _m.Program.IsProgramOwned(r.Id)) continue;   // 程序执行中的通道不改
                if (CommandPolicy.Strict(src) && r.IsWorking) { warnings.Add($"RV{r.Id} 正在运行 · 已跳过"); continue; }
                var list = _m.Program.StepsOf(r.Id);
                if (stepIndex < list.Count && list[stepIndex].Type == StepType.Heat)
                {
                    CopyCurve(list[stepIndex], segs);
                    ch.IsModified = true;
                    applied.Add(r.Id);
                }
            }
        }
        if (_m.Program.ChannelOf(rv) is { } cur) cur.IsModified = true;
        string msg = allChannels ? $"已应用 {segs.Count} 段曲线到全部运行中通道" : $"已应用 {segs.Count} 段曲线到 RV{rv}";
        return OpResult.Done(msg, "ok", new { channels = applied }).WithWarnings(warnings.ToArray()).AsTask();
    }

    private static void CopyCurve(ProgramStep dst, IReadOnlyList<HeatSeg> segs)
    {
        dst.Segments.Clear();
        foreach (var s in segs) dst.Segments.Add(s.Clone());
        dst.SetStr("mode", "curve");
        dst.RaiseChanged();
    }

    /// <summary>
    /// 把 fromRv 的程序复制到 to 中的通道（覆盖）。跳过正在执行程序的通道（原逻辑）；没有目标时「请先选择目标通道」warn。
    /// 源通道须可编辑（同界面锁定）。严格守卫：目标通道已停用或正在运行时跳过并写入 Warnings。
    /// </summary>
    public Task<OpResult> TryCopyProgram(CommandSource src, int fromRv, IReadOnlyList<int> to)
    {
        if (Admit(src, ActionClass.Control) is { } deny) return deny.AsTask();
        if (ChannelOr(fromRv, out var c) is { } nf) return nf.AsTask();
        if (EditLocked(c) is { } locked) return locked.AsTask();
        var targets = new List<int>();
        var warnings = new List<string>();
        foreach (int id in to.Distinct())
        {
            if (id == fromRv) continue;
            if (ChannelOr(id, out var r) is { } tnf) return tnf.AsTask();
            if (_m.Program.IsProgramOwned(id)) continue;
            if (CommandPolicy.Strict(src) && (r.IsIdle || r.IsWorking))
            {
                warnings.Add($"RV{id} {(r.IsIdle ? "已停用" : "正在运行")} · 已跳过");
                continue;
            }
            targets.Add(id);
        }
        if (targets.Count == 0)
            return OpResult.Fail(OpCodes.BadRequest, "请先选择目标通道", "warn").WithWarnings(warnings.ToArray()).AsTask();

        var srcSteps = _m.Program.StepsOf(fromRv);
        foreach (int id in targets)
        {
            var list = _m.Program.StepsOf(id);
            list.Clear();
            foreach (var s in srcSteps) list.Add(s.Clone());
            for (int i = 0; i < list.Count; i++) list[i].Ordinal = i + 1;
            if (_m.Program.ChannelOf(id) is { } ch) ch.IsModified = false;
        }
        return OpResult.Done($"已将 RV{fromRv} 的 {srcSteps.Count} 步程序应用到 {targets.Count} 个通道", "ok",
            new { from = fromRv, to = targets }).WithWarnings(warnings.ToArray()).AsTask();
    }

    /// <summary>下发被守卫拦下：Toast 一闪而过容易漏看，同步写一条事件记录（报警 / 事件页可查）。</summary>
    private OpResult RunBlocked((string Code, string Message) why)
    {
        _m.Alarm.Log("warn", "系统", "程序下发被拒绝", why.Message);
        return OpResult.Fail(why.Code, $"下发被拒绝 · {why.Message}");
    }

    /// <summary>
    /// 打开「下发并运行」确认框之前的预检（只读，不改程序状态）：仲裁 + 下发守卫（<see cref="ProgramRunGuard"/>）。
    /// 被拒时「下发被拒绝 · …」err，并记一条「程序下发被拒绝」事件（与今天一致）。rv = 0 表示未选中通道。
    /// </summary>
    public OpResult CheckProgramRun(CommandSource src, int rv, IReadOnlyList<StepSnapshot> snap)
    {
        if (Admit(src, ActionClass.Control) is { } deny) return deny;
        return _m.Program.RunGuard(rv, snap) is { } why ? RunBlocked(why) : OpResult.Silent;
    }

    /// <summary>
    /// 下发并运行一份冻结的程序副本（C3）：按此刻实时状态把全部守卫重判一次（确认期间状态可能变了），通过才执行。
    /// 执行流程（ExecuteAsync）在后台进行，步骤进度与结果由运行监视 / 事件通知，所以返回 Accepted、不提示。
    /// </summary>
    public Task<OpResult> TryRunProgram(CommandSource src, int rv, IReadOnlyList<StepSnapshot> snap)
    {
        if (Admit(src, ActionClass.Control) is { } deny) return deny.AsTask();
        if (_m.Program.RunGuard(rv, snap) is { } why) return RunBlocked(why).AsTask();
        // startedBy：触屏 "local"；远程带客户端名（快照 channels[i].program.startedBy）
        _m.Program.StartRun(rv, snap, src.IsRemote ? src.Label : "local");
        return OpResult.Accepted("", "ok", new { rv, steps = snap.Count }).AsTask();
    }

    /// <summary>
    /// 中止某通道的程序（幂等：该通道没有程序在执行时 Silent）。只中止该通道，其他通道的程序继续。
    /// 程序引擎随后停加热、关它自己的阀并给出结果提示（事件，不是本命令的返回）。
    /// 搅拌在运行、且没有其他程序持有搅拌请求时 Warnings 带「中止程序将停止全局搅拌」（H5）。触屏不提示（与今天一致）。
    /// </summary>
    public Task<OpResult> TryAbortProgram(CommandSource src, int rv)
    {
        if (Admit(src, ActionClass.Stop) is { } deny) return deny.AsTask();
        if (!_m.Program.IsProgramOwned(rv)) return OpResult.Silent.AsTask();
        bool stir = _m.StirOn && _m.StirShare.Holders.All(h => h == rv);
        _m.Program.AbortProgram(rv);
        var r = OpResult.Done($"RV{rv} 程序已中止 · 正在安全停止", "warn", new { rv }) with { Quiet = true };
        return (stir ? r.WithWarnings("中止程序将停止全局搅拌") : r).AsTask();
    }
}
