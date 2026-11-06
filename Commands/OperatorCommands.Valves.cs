using System;
using System.Threading.Tasks;
using ParallelReactor.Models;
using ParallelReactor.ViewModels;

namespace ParallelReactor.Commands;

/// <summary>
/// 手动阀控（全部只允许在设备触屏上进行，附录 B：维护操作，手动阀不经 ValveFrame.IsSafe）。
/// 方向由调用方给出：开为 Control、关为 Stop（排空：开会释放釜内气体、改变过程状态，按 Control；关按 Stop）。
/// 守卫与文字沿用原 MainViewModel.TryValve / ToggleGas / ToggleVent / ToggleManual。
/// </summary>
public sealed partial class OperatorCommands
{
    /// <summary>开 / 关「手动阀控」模式。急停、气路序列进行中时两个方向都拒绝（与今天一致）。</summary>
    public Task<OpResult> TrySetManualValveMode(CommandSource src, bool on)
    {
        if (Admit(src, on ? ActionClass.Control : ActionClass.Stop, Exposure.LocalOnly) is { } deny) return deny.AsTask();
        if (_m.GasLockedForManual() is { } locked) return locked.AsTask();
        if (!_m.IsAdmin) return OpResult.Fail(OpCodes.ForbiddenRole, "手动阀控需要管理员权限").AsTask();
        if (on && !_m.ManualValve && !MainViewModel.RealIo)
            return OpResult.Fail(OpCodes.HwNotConfigured, "阀门模块未配置串口（PR_IO_PORT），无法手动阀控").AsTask();
        _m.ManualValve = on;
        _m.RefreshSchematic();   // 开关画在气路图里，立即重绘
        return (on
            ? OpResult.Done("手动阀控已开启 · 点击阀门可手动操作", "warn", new { on })
            : OpResult.Done("手动阀控已关闭", "ok", new { on })).AsTask();
    }

    /// <summary>手动开 / 关某通道进气阀 SVn（气路图 / 抽屉）。开阀时超压联锁未解除、加压 / 反应 / 报警中拒绝。</summary>
    public Task<OpResult> TrySetReactorValve(CommandSource src, int id, bool open)
    {
        if (Admit(src, open ? ActionClass.Control : ActionClass.Stop, Exposure.LocalOnly) is { } deny) return deny.AsTask();
        if (ChannelOr(id, out var c) is { } nf) return nf.AsTask();
        if (_m.GasLockedForManual() is { } locked) return locked.AsTask();
        if (!_m.IsAdmin) return OpResult.Fail(OpCodes.ForbiddenRole, "手动阀控需要管理员权限").AsTask();
        if (!_m.ManualValve)
            return OpResult.Fail(OpCodes.ChannelStateInvalid, "请先打开 RV1 左侧的「手动阀控」开关", "warn").AsTask();
        if (open && _m.IsOverPressureTripped(id))
            return OpResult.Fail(OpCodes.ChannelAlarm, $"RV{id} 超压联锁未解除 · 禁止打开 SV{id}").AsTask();
        if (open && c.State is ReactorState.Pressing or ReactorState.React or ReactorState.Alarm)
            return OpResult.Fail(OpCodes.ChannelBusy, $"加压/反应中禁止手动开阀（RV{id}）").AsTask();
        return ManualValveTailAsync($"SV{id}", open, () => _m._valves.SetReactorValveAsync(id, open), () =>
        {
            c.Valve = open;
            _m.Drawer.RaiseAll();
        });
    }

    /// <summary>手动开 / 关某路气源进气阀（index 0..2）。开阀时有通道超压联锁未解除则拒绝。</summary>
    public Task<OpResult> TrySetGasInlet(CommandSource src, int index, bool open)
    {
        if (Admit(src, open ? ActionClass.Control : ActionClass.Stop, Exposure.LocalOnly) is { } deny) return deny.AsTask();
        if (_m.GasLockedForManual() is { } locked) return locked.AsTask();
        if (!_m.IsAdmin) return OpResult.Fail(OpCodes.ForbiddenRole, "需要管理员权限").AsTask();
        if (!_m.ManualValve) return OpResult.Fail(OpCodes.ChannelStateInvalid, "请先开启「手动阀控」", "warn").AsTask();
        if (index < 0 || index >= _m.GasInlets.Count)
            return OpResult.Fail(OpCodes.NotFound, $"气源 {index + 1} 不存在（有效范围 1–{_m.GasInlets.Count}）").AsTask();
        var g = _m.GasInlets[index];
        if (open && _m.AnyOverPressureTripped)
            return OpResult.Fail(OpCodes.ChannelAlarm, "有通道超压联锁未解除 · 禁止打开气源").AsTask();
        return ManualValveTailAsync($"{g.Label} 进气阀", open, () => _m._valves.SetGasAsync(index, open), () => g.On = open);
    }

    /// <summary>手动开 / 关排空阀（开为 warn 提示）。</summary>
    public Task<OpResult> TrySetVent(CommandSource src, bool open)
    {
        if (Admit(src, open ? ActionClass.Control : ActionClass.Stop, Exposure.LocalOnly) is { } deny) return deny.AsTask();
        if (_m.GasLockedForManual() is { } locked) return locked.AsTask();
        if (!_m.IsAdmin) return OpResult.Fail(OpCodes.ForbiddenRole, "需要管理员权限").AsTask();
        if (!_m.ManualValve) return OpResult.Fail(OpCodes.ChannelStateInvalid, "请先开启「手动阀控」", "warn").AsTask();
        return ManualValveTailAsync("排空阀", open, () => _m._valves.SetVentAsync(open), () => _m.VentOn = open, openKind: "warn");
    }

    private bool _manualValveBusy;   // 上一次手动阀写入还没回来时不接受新操作，避免写入顺序错乱

    /// <summary>
    /// 手动阀操作的公共尾部（原 MainViewModel.ManualValveAsync）：先写线圈，写成功后才改界面（界面只显示确认过的阀态）。
    /// 阀门未配置串口时直接拒绝；写入期间按下急停，写完后立即补关一次。
    /// 未配置 / 忙两种拒绝在第一个 await 之前同步返回（触屏当场提示）。
    /// </summary>
    private async Task<OpResult> ManualValveTailAsync(string name, bool open, Func<Task> write, Action show, string openKind = "ok")
    {
        if (!MainViewModel.RealIo)
            return OpResult.Fail(OpCodes.HwNotConfigured, "阀门模块未配置串口（PR_IO_PORT），无法操作");
        if (_manualValveBusy) return OpResult.Fail(OpCodes.ValveBusy, "上一个阀门操作还在进行，请稍候", "warn");
        _manualValveBusy = true;
        try
        {
            try { await write(); }
            catch (Services.ValveLockedException)
            {
                return OpResult.Fail(OpCodes.EstopActive, "急停锁定中 · 拒绝打开阀门，请先复位");
            }
            catch (Exception ex)
            {
                _m.Alarm.Log("err", "阀门", $"手动{(open ? "打开" : "关闭")} {name} 失败", "IO16R 通讯异常 · 阀门状态未改变");
                return OpResult.Fail(OpCodes.HardwareWriteFailed, $"{name}{(open ? "打开" : "关闭")}失败（IO16R 通讯）· 阀门状态未改变",
                    "err", new { device = "io", valve = name, error = ex.Message });
            }
            _m.Alarm.Log(open ? "warn" : "info", "阀门", $"手动{(open ? "打开" : "关闭")} {name}", "手动阀控 · 写入已确认");
            if (_m.Estopped)
            {
                // 写入途中按了急停：这一帧可能落在急停的全关之后，再关一次
                await _m.CloseAllValvesAsync();
                return OpResult.Silent;
            }
            show();
            _m.RefreshSchematic();
            return OpResult.Done($"{name} {(open ? "已开" : "已关")}", open ? openKind : "ok", new { valve = name, open });
        }
        finally { _manualValveBusy = false; }
    }
}
