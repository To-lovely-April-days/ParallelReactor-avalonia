using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ParallelReactor.Models;

namespace ParallelReactor.Commands;

/// <summary>远程整段替换曲线用的一段：目标温度（℃）与时长（分钟）。</summary>
public readonly record struct ProfileSegmentDto(double TempC, double Minutes);

/// <summary>
/// 通道 / 抽屉。所有方法以显式通道号为参数，不依赖 Drawer.Reactor（H3）。
/// 现有守卫对两种来源都生效（文字、级别与原 DrawerViewModel 一致）；严格守卫只在 <see cref="CommandPolicy.Strict"/> 时生效。
/// </summary>
public sealed partial class OperatorCommands
{
    // ==================== 温度 SP / 体积 ====================

    /// <summary>
    /// 抽屉「温度 SP」的预检（只读）：仲裁 + 程序占用。打开键盘前调用，体验与今天一致；确认时 <see cref="TrySetChannelTemp"/> 会重判（K3）。
    /// </summary>
    public OpResult CheckChannelTemp(CommandSource src, int id)
    {
        if (Admit(src, ActionClass.Control) is { } deny) return deny;
        if (ChannelOr(id, out var c) is { } nf) return nf;
        return ProgramOwned(c) ?? OpResult.Silent;
    }

    /// <summary>
    /// 设定值模式目标温度：加热中交给防超调逼近重新规划（下个 Tick 下发），未加热时直接写 SP（<see cref="ViewModels.MainViewModel.SetFixedTarget"/>）。
    /// 校验 10..TMaxSp（C5）；严格守卫：须低于超温报警值。没有提示（与今天一致）。
    /// </summary>
    public Task<OpResult> TrySetChannelTemp(CommandSource src, int id, double tempC)
    {
        if (Admit(src, ActionClass.Control) is { } deny) return deny.AsTask();
        if (ChannelOr(id, out var c) is { } nf) return nf.AsTask();
        if (ProgramOwned(c) is { } owned) return owned.AsTask();   // 键盘确认时重新判断（K3）
        if (Invalid(ValueRules.TempSp(tempC, _m.Settings.TMaxSp)) is { } bad) return bad.AsTask();
        if (CommandPolicy.Strict(src) && Invalid(ValueRules.StrictTempSp(tempC, _m.Settings.OverTemp)) is { } hot) return hot.AsTask();
        c.TSp = tempC;
        _m.SetFixedTarget(c);
        _m.RefreshSchematic();
        _m.Drawer.RaiseAll();
        return (OpResult.Silent with { Data = new { tempC, approachReplanned = c.Approach.Active } }).AsTask();
    }

    /// <summary>溶液体积（mL）。校验 1..VolMax。没有提示（与今天一致）。</summary>
    public Task<OpResult> TrySetChannelVolume(CommandSource src, int id, int ml)
    {
        if (Admit(src, ActionClass.Control) is { } deny) return deny.AsTask();
        if (ChannelOr(id, out var c) is { } nf) return nf.AsTask();
        if (Invalid(ValueRules.Volume(ml, _m.Settings.VolMax)) is { } bad) return bad.AsTask();
        c.Vol = ml;
        _m.RefreshSchematic();
        _m.Drawer.RaiseAll();
        return (OpResult.Silent with { Data = new { ml } }).AsTask();
    }

    // ==================== 升温方式 / 曲线 ====================

    /// <summary>切换升温方式（fixed / curve）。切走曲线时停曲线，切走定值时停逼近。严格守卫：加热中或曲线运行中拒绝切换。</summary>
    public Task<OpResult> TrySetSpMode(CommandSource src, int id, string mode)
    {
        if (Admit(src, ActionClass.Control) is { } deny) return deny.AsTask();
        if (ChannelOr(id, out var c) is { } nf) return nf.AsTask();
        if (ProgramOwned(c) is { } owned) return owned.AsTask();
        if (mode is not ("fixed" or "curve"))
            return OpResult.Fail(OpCodes.ParamOutOfRange, "升温方式须为 fixed（定值）或 curve（曲线）", "err", new { value = mode }).AsTask();
        if (CommandPolicy.Strict(src) && mode != c.SpMode && (c.State == ReactorState.Heating || c.Profile.Running))
            return OpResult.Fail(OpCodes.ChannelBusy, $"RV{id} 加热 / 曲线运行中 · 请先停止再切换升温方式").AsTask();
        c.SpMode = mode;
        if (mode != "curve" && c.Profile.Running) c.Profile.Stop();
        if (mode != "fixed") c.Approach.Stop();
        _m.Drawer.RaiseAll();
        return (OpResult.Silent with { Data = new { mode } }).AsTask();
    }

    /// <summary>曲线严格守卫：程序占用、曲线运行中不能改段（K4，本地照旧允许）。</summary>
    private OpResult? ProfileLocked(CommandSource src, Reactor c)
    {
        if (!CommandPolicy.Strict(src)) return null;
        if (ProgramOwned(c) is { } owned) return owned;
        return c.Profile.Running ? OpResult.Fail(OpCodes.CurveRunning, $"RV{c.Id} 曲线升温运行中 · 请先停止曲线再修改") : null;
    }

    /// <summary>启用段数（1..16）。</summary>
    public Task<OpResult> TrySetProfileSegCount(CommandSource src, int id, int n)
    {
        if (Admit(src, ActionClass.Control) is { } deny) return deny.AsTask();
        if (ChannelOr(id, out var c) is { } nf) return nf.AsTask();
        if (Invalid(ValueRules.ProfileSegCount(n, TempProfile.MaxSegments)) is { } bad) return bad.AsTask();
        if (ProfileLocked(src, c) is { } locked) return locked.AsTask();
        c.Profile.SegCount = n;
        return (OpResult.Silent with { Data = new { segCount = n } }).AsTask();
    }

    /// <summary>改某段（index 1..16）的目标温度和 / 或时长（给 null 的项不改）。温度 0..TMaxSp，时长 0.1..6000 分钟。</summary>
    public Task<OpResult> TrySetProfileSegment(CommandSource src, int id, int index, double? tempC, double? minutes)
    {
        if (Admit(src, ActionClass.Control) is { } deny) return deny.AsTask();
        if (ChannelOr(id, out var c) is { } nf) return nf.AsTask();
        var seg = c.Profile.Segments.FirstOrDefault(s => s.Index == index);
        if (seg == null)
            return OpResult.Fail(OpCodes.ParamOutOfRange, $"曲线段号须在 1–{TempProfile.MaxSegments} 之间", "err",
                new { min = 1, max = TempProfile.MaxSegments, value = index }).AsTask();
        if (Invalid(ValueRules.ProfileSegment(tempC, minutes, _m.Settings.TMaxSp)) is { } bad) return bad.AsTask();
        if (ProfileLocked(src, c) is { } locked) return locked.AsTask();
        if (CommandPolicy.Strict(src) && tempC is { } t && Invalid(ValueRules.StrictTempSp(t, _m.Settings.OverTemp)) is { } hot)
            return hot.AsTask();
        if (tempC is { } tt) seg.Temp = tt;
        if (minutes is { } mm) seg.Minutes = mm;
        return (OpResult.Silent with { Data = new { index, tempC = seg.Temp, minutes = seg.Minutes } }).AsTask();
    }

    /// <summary>整段替换曲线（远程 PUT …/profile；触屏没有对应入口）。段数 1..16，逐段检查同 <see cref="TrySetProfileSegment"/>。</summary>
    public Task<OpResult> TryReplaceProfile(CommandSource src, int id, IReadOnlyList<ProfileSegmentDto> segs)
    {
        if (Admit(src, ActionClass.Control) is { } deny) return deny.AsTask();
        if (ChannelOr(id, out var c) is { } nf) return nf.AsTask();
        if (Invalid(ValueRules.ProfileSegCount(segs.Count, TempProfile.MaxSegments)) is { } badN) return badN.AsTask();
        for (int i = 0; i < segs.Count; i++)
        {
            var v = ValueRules.ProfileSegment(segs[i].TempC, segs[i].Minutes, _m.Settings.TMaxSp)
                    ?? (CommandPolicy.Strict(src) ? ValueRules.StrictTempSp(segs[i].TempC, _m.Settings.OverTemp) : null);
            if (v != null)
                return OpResult.Fail(v.Code, $"第 {i + 1} 段：{v.Message}", "err",
                    new { index = i + 1, min = v.Min, max = v.Max, value = v.Value }).AsTask();
        }
        if (ProfileLocked(src, c) is { } locked) return locked.AsTask();
        for (int i = 0; i < segs.Count; i++)
        {
            c.Profile.Segments[i].Temp = segs[i].TempC;
            c.Profile.Segments[i].Minutes = segs[i].Minutes;
        }
        c.Profile.SegCount = segs.Count;
        _m.Drawer.RaiseAll();
        return OpResult.Done($"RV{id} 曲线已更新 · 共 {segs.Count} 段", "ok", new { segCount = segs.Count }).AsTask();
    }

    /// <summary>
    /// 启动曲线升温：以当前实测 PV 为段 1 起点，逐周期下发 SP。同时做全链启动（Srun=0 + At=0 + PID 套档，按曲线最高段温度选档）——
    /// 开机 / 停止后通道处于 At=4 停止态，只写 SP 曲线会「空转」。全链启动失败时停曲线并提示（事件式提示，不是本命令的返回）。
    /// </summary>
    public Task<OpResult> TryStartProfile(CommandSource src, int id)
    {
        if (Admit(src, ActionClass.Control) is { } deny) return deny.AsTask();
        if (ChannelOr(id, out var c) is { } nf) return nf.AsTask();
        if (ProgramOwned(c) is { } owned) return owned.AsTask();
        if (HeatBlocked(id) is { } blocked) return blocked.AsTask();
        if (CommandPolicy.Strict(src) && c.State == ReactorState.Alarm)
            return OpResult.Fail(OpCodes.ChannelAlarm, $"RV{id} 报警中 · 请先处理并停止 / 复位").AsTask();
        // PV 无效（温控离线 / 读数为 0）则从室温起步，避免段 1 从 0℃ 开始爬
        double startPv = c.TOnline && c.T > 1 ? c.T : 25;
        double bandTemp = startPv;
        foreach (var s in c.Profile.Segments)
            if (s.Enabled) bandTemp = Math.Max(bandTemp, s.Temp);
        c.Profile.Start(startPv);
        if (c.State is ReactorState.Idle or ReactorState.Done or ReactorState.Ready)
            c.State = ReactorState.Heating;   // 曲线升温也是加热，状态同步为「升温中」
        c.StartedBy = StartedByOf(src);
        // 硬件链放在曲线启动之后：StartChannelAsync 可能同步返回 false（快速路径急停已置服务锁），
        // 失败分支要能停掉已启动的曲线，否则会留下「曲线在跑、仪表不出力」
        _ = StartProfileHwAsync(c, bandTemp, startPv);
        _m.RefreshSchematic();
        _m.Drawer.RaiseAll();
        return OpResult.Accepted($"RV{id} 曲线升温已启动 · 共 {c.Profile.SegCount} 段", "ok",
            new { segCount = c.Profile.SegCount, startPv }).AsTask();
    }

    private async Task StartProfileHwAsync(Reactor c, double bandTemp, double startPv)
    {
        if (await _m.Temp.StartChannelAsync(c.Id, bandTemp, startPv)) return;
        // 全链启动没成功：仪表不出力，曲线引擎空转没有意义，停掉并提示
        if (c.Profile.Running) c.Profile.Stop();
        _m.ReportHeatStartFailed(c.Id);
        _m.Drawer.RaiseAll();
    }

    /// <summary>停止曲线升温（仪表保持最后下发的 SP）。加热仍在按当前给定值运行时附带告警。</summary>
    public Task<OpResult> TryStopProfile(CommandSource src, int id)
    {
        if (Admit(src, ActionClass.Stop) is { } deny) return deny.AsTask();
        if (ChannelOr(id, out var c) is { } nf) return nf.AsTask();
        c.Profile.Stop();
        _m.Drawer.RaiseAll();
        var r = OpResult.Done($"RV{id} 曲线升温已停止 · 保持当前给定值", "warn");
        return (c.State == ReactorState.Heating ? r.WithWarnings($"加热仍在按 {c.TSp:0.#} ℃ 运行") : r).AsTask();
    }

    // ==================== 加热 ====================

    /// <summary>
    /// 启动本通道定值加热（全链：Srun=0 + At=0 + PID 套档 + SP），SP 走防超调逼近。
    /// 带 tempC 时先按 <see cref="TrySetChannelTemp"/> 的规则校验再设定。严格守卫：通道须为 已启用 / 已结束 / 升温中。
    /// </summary>
    public Task<OpResult> TryStartHeat(CommandSource src, int id, double? tempC = null)
    {
        if (Admit(src, ActionClass.Control) is { } deny) return deny.AsTask();
        if (ChannelOr(id, out var c) is { } nf) return nf.AsTask();
        if (ProgramOwned(c) is { } owned) return owned.AsTask();
        if (HeatBlocked(id) is { } blocked) return blocked.AsTask();
        if (tempC is { } t)
        {
            if (Invalid(ValueRules.TempSp(t, _m.Settings.TMaxSp)) is { } bad) return bad.AsTask();
            if (CommandPolicy.Strict(src) && Invalid(ValueRules.StrictTempSp(t, _m.Settings.OverTemp)) is { } hot) return hot.AsTask();
        }
        if ((tempC ?? c.TSp) < 35)
            return OpResult.Fail(OpCodes.ParamOutOfRange, "请先设置目标温度（≥35℃）再启动加热").AsTask();
        if (CommandPolicy.Strict(src) && c.State is not (ReactorState.Ready or ReactorState.Done or ReactorState.Heating))
            return OpResult.Fail(OpCodes.ChannelStateInvalid, $"RV{id} 当前状态「{c.StateZh}」不可启动加热 · 请先启用通道").AsTask();
        if (tempC is { } sp) c.TSp = sp;
        _m.StartFixedHeat(c);
        if (c.State is ReactorState.Idle or ReactorState.Done or ReactorState.Ready)
            c.State = ReactorState.Heating;   // 加热真的开始了才显示「升温中」
        c.StartedBy = StartedByOf(src);   // §5.4：onRemoteLost = stopHeat 只停该租约启动的通道
        _m.RefreshSchematic();
        _m.Drawer.RaiseAll();
        return OpResult.Accepted($"RV{id} 加热已启动 · 目标 {c.TSp:0}℃ · 防超调逼近", "ok", new { tempC = c.TSp }).AsTask();
    }

    /// <summary>
    /// 停止单通道加热（At=4，输出关闭）。只停加热，不改阀门；「升温中」回到「已启用」。
    /// 停止类操作永不拒绝：通道正在执行程序时中止该程序（程序中止会立即下发停加热并完成收尾）。
    /// </summary>
    public Task<OpResult> TryStopHeat(CommandSource src, int id)
    {
        if (Admit(src, ActionClass.Stop) is { } deny) return deny.AsTask();
        if (ChannelOr(id, out var c) is { } nf) return nf.AsTask();
        if (_m.Program.IsProgramOwned(id))
        {
            bool aborted = _m.Program.AbortProgram(id);
            _m.Drawer.RaiseAll();
            return (aborted
                ? OpResult.Done($"RV{id} 停止加热 · 正在执行的程序已中止", "warn", new { programAborted = true })
                : OpResult.Silent).AsTask();
        }
        if (c.Profile.Running) c.Profile.Stop();   // 曲线在跑也一并停，避免引擎继续抬 SP
        c.Approach.Stop();
        if (c.State == ReactorState.Heating) c.State = ReactorState.Ready;
        var tail = _m.StopHeatCoreAsync(id, $"RV{id} 加热已停止（输出关闭）");
        _m.RefreshSchematic();
        _m.Drawer.RaiseAll();
        return tail;
    }

    // ==================== 启用 / 停止 / 停用 ====================

    /// <summary>
    /// 启用通道（状态转「已启用」，开始加热后才转「升温中」）。defaultVolume = true（抽屉「启用通道」）时体积为 0 则补 3 mL。
    /// 现有守卫：程序占用、急停锁定（含快速路径已锁、UI 流程尚未跑到）、气路序列进行中（多程序并行后：另一程序的气路步骤占用不算，泄漏测试才拒绝）。
    /// 严格守卫：报警或超温 / 超压未解除的通道不能启用（K6，本地照旧允许）。
    /// </summary>
    public Task<OpResult> TryEnableChannel(CommandSource src, int id, bool defaultVolume)
    {
        if (Admit(src, ActionClass.Control) is { } deny) return deny.AsTask();
        if (ChannelOr(id, out var c) is { } nf) return nf.AsTask();
        if (ProgramOwned(c) is { } owned) return owned.AsTask();
        if (EstopOr(defaultVolume ? "急停状态下不可启用通道 · 请先复位" : "急停状态下不可启动 · 请先复位") is { } es) return es.AsTask();
        if (_m.GasBusy && !_m.GasOwnedBy(GasOwner.Program))
            return OpResult.Fail(OpCodes.GasBusy, $"{_m.GasTask}进行中 · 请等待完成或先停止", "warn").AsTask();
        if (CommandPolicy.Strict(src) && (c.State == ReactorState.Alarm || _m.IsOverTempTripped(id) || _m.IsOverPressureTripped(id)))
            return OpResult.Fail(OpCodes.ChannelAlarm, $"RV{id} 存在报警或超温 / 超压未解除 · 请先处理").AsTask();
        c.State = ReactorState.Ready;   // 启用 ≠ 升温中：只表示通道参与运行，开始加热后才转 Heating
        c.StartedBy = StartedByOf(src);
        if (defaultVolume && c.Vol == 0) c.Vol = 3;
        c.Rpm = _m.StirOn ? _m.StirRpm : 0;
        _m.RefreshSchematic();
        _m.Drawer.RaiseAll();
        return OpResult.Done($"RV{id} 已启用").AsTask();
    }

    /// <summary>
    /// 停止（disable = false → 已结束）/ 停用（disable = true → 停用）通道（C9）。顺序：
    /// <list type="number">
    /// <item>同步头：置状态、停曲线 / 逼近（界面立即生效）；</item>
    /// <item>该通道正被程序执行或参与气路序列：先按占用者干净地中止那个序列，等它自己的收尾全关阀、释放气路；</item>
    /// <item>停温控输出（At=4）——程序已被中止时由程序收尾停加热，这里不重复下发；</item>
    /// <item>SV 仍开着就关闭（写成功才改显示）。</item>
    /// </list>
    /// 主结果是停加热的结果；关阀失败作为随后的提示（与今天两条提示一致）。被中止的气路序列名写入 Warnings（H9）。
    /// </summary>
    public Task<OpResult> TryStopChannel(CommandSource src, int id, bool disable)
    {
        if (Admit(src, ActionClass.Stop) is { } deny) return deny.AsTask();
        if (ChannelOr(id, out var c) is { } nf) return nf.AsTask();
        string? gas = _m.GasIncludes(id) ? _m.GasTask : null;
        c.State = disable ? ReactorState.Idle : ReactorState.Done;
        c.StartedBy = null;
        c.Rpm = 0;
        if (c.Profile.Running) c.Profile.Stop();
        c.Approach.Stop();
        _m.RefreshSchematic();
        _m.Drawer.RaiseAll();
        return StopChannelTailAsync(c, disable ? "已停用，不参与运行" : "已停止（含加热输出）", gas);
    }

    private async Task<OpResult> StopChannelTailAsync(Reactor c, string what, string? gas)
    {
        bool programAborted = await _m.ReleaseChannelFromGasAsync(c.Id);
        // 停温控输出(At=4)——否则仪表带着旧 SP 继续加热
        var r = programAborted ? OpResult.Silent : await _m.StopHeatCoreAsync(c.Id, $"RV{c.Id} {what}");
        if (c.Valve)
        {
            var v = await _m.CloseReactorValveCoreAsync(c.Id);
            if (!v.Ok) r = r.Then(v).WithWarnings(v.Message);
        }
        _m.RefreshSchematic();
        _m.Drawer.RaiseAll();
        if (r.Ok) r = r with { Data = new { programAborted, gasAborted = gas } };   // 失败时保留 error.detail
        return gas != null ? r.WithWarnings($"已中止气路序列：{gas}") : r;
    }

    // ==================== 其他 ====================

    /// <summary>进样计数清零。</summary>
    public Task<OpResult> TryResetGasCounter(CommandSource src, int id)
    {
        if (Admit(src, ActionClass.Control) is { } deny) return deny.AsTask();
        if (ChannelOr(id, out var c) is { } nf) return nf.AsTask();
        c.Gas = 0;
        _m.RefreshSchematic();
        _m.Drawer.RaiseAll();
        return OpResult.Done($"RV{id} 进样计数已清零，开始累计进气").AsTask();
    }

    /// <summary>清除通道上的配方标签（只允许在设备触屏上进行：配方不对外，附录 B）。</summary>
    public Task<OpResult> TryClearRecipeTag(CommandSource src, int id)
    {
        if (Admit(src, ActionClass.Control, Exposure.LocalOnly) is { } deny) return deny.AsTask();
        if (ChannelOr(id, out var c) is { } nf) return nf.AsTask();
        c.AppliedRecipe = "";
        _m.Drawer.RaiseAll();
        return OpResult.Done($"RV{id} 配方标签已清除").AsTask();
    }

    /// <summary>
    /// 把一个通道的配置（TSp / PSp / RpmSp / 体积 / 桨叶 / 结束条件 / 运行时长）复制到其他通道。
    /// to = null（触屏「复制到其余通道」）：原规则——非停用、且不是正在执行程序的通道（程序的「等待温度 / 压力到位」按通道给定值判断）。
    /// 显式 to：跳过正在执行程序的通道。严格守卫：源通道数值逐项校验，跳过正在干活（IsWorking）的目标并写入 Warnings。
    /// 修改 TSp 后由逼近在下一个 Tick 间接写入（K8）。
    /// </summary>
    public Task<OpResult> TryCopyChannelConfig(CommandSource src, int fromId, IReadOnlyList<int>? to = null)
    {
        if (Admit(src, ActionClass.Control) is { } deny) return deny.AsTask();
        if (ChannelOr(fromId, out var s) is { } nf) return nf.AsTask();
        bool strict = CommandPolicy.Strict(src);
        if (strict && (Invalid(ValueRules.TempSp(s.TSp, _m.Settings.TMaxSp))
                       ?? Invalid(ValueRules.StrictTempSp(s.TSp, _m.Settings.OverTemp))
                       ?? Invalid(ValueRules.Volume(s.Vol, _m.Settings.VolMax))) is { } bad)
            return bad.AsTask();
        IEnumerable<Reactor> targets;
        var warnings = new List<string>();
        if (to == null)
            targets = _m.Reactors.Where(r => r.State != ReactorState.Idle && !_m.Program.IsProgramOwned(r.Id));
        else
        {
            var list = new List<Reactor>();
            foreach (int id in to.Distinct())
            {
                if (id == fromId) continue;
                if (_m.FindReactor(id) is not { } r)
                    return OpResult.Fail(OpCodes.ChannelNotFound, $"通道 RV{id} 不存在（有效范围 RV1–RV8）").AsTask();
                if (_m.Program.IsProgramOwned(r.Id)) { warnings.Add($"RV{id} 正在执行程序 · 已跳过"); continue; }
                list.Add(r);
            }
            targets = list;
        }
        var done = new List<int>();
        foreach (var x in targets.ToList())
        {
            if (strict && x.Id != fromId && x.IsWorking) { warnings.Add($"RV{x.Id} 运行中 · 已跳过"); continue; }
            x.TSp = s.TSp; x.PSp = s.PSp; x.RpmSp = s.RpmSp;
            x.Vol = s.Vol; x.Blade = s.Blade; x.End = s.End; x.Run = s.Run;
            if (x.Id != fromId) done.Add(x.Id);
        }
        _m.RefreshSchematic();
        return OpResult.Done("已复制本配置到其余通道", "ok",
                new { from = fromId, channels = done, approachReplanned = done.Where(i => _m.FindReactor(i)!.Approach.Active).ToList() })
            .WithWarnings([.. warnings]).AsTask();
    }
}
