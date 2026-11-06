using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using ParallelReactor.Models;

namespace ParallelReactor.Commands;

/// <summary>温控 / PID：PID（显式档位）、自整定、Ctn、档位分界。</summary>
public sealed partial class OperatorCommands
{
    /// <summary>
    /// 写某通道某档的 PID（C11：显式档位）。触屏传入界面显示档 <c>Temp.CurrentBand</c>。
    /// 只有该档正是仪表当前生效档时才立即写仪表，否则只存档、切到该档时生效（Data.live 区分）。
    /// 成功按字段记「设置 · RVn P/I/D 旧 → 新」，写仪表失败记「未生效」。结果 Quiet：触屏今天就没有 Toast。
    /// </summary>
    public Task<OpResult> TrySetPid(CommandSource src, int ch, int band, double p, double i, double d)
    {
        if (Admit(src, ActionClass.Control) is { } deny) return deny.AsTask();
        if (ChannelOr(ch, out _) is { } nf) return nf.AsTask();
        if (Invalid(ValueRules.Pid(band, p, i, d, _m.Temp.Bands.Count)) is { } bad) return bad.AsTask();
        var (op, oi, od) = _m.Temp.GetPid(ch, band);   // 同步头取旧值：键盘确认那一刻的值
        return SetPidTailAsync(ch, band, (op, oi, od), (p, i, d));
    }

    private async Task<OpResult> SetPidTailAsync(int ch, int band,
        (double p, double i, double d) old, (double p, double i, double d) now)
    {
        string name = $"RV{ch}";
        var changes = new List<(string key, string oldV, string newV)>();
        void Diff(string key, double o, double n, string unit)
        {
            if (Fmt(o, unit) != Fmt(n, unit)) changes.Add((key, Fmt(o, unit), Fmt(n, unit)));
        }
        Diff("P", old.p, now.p, "");
        Diff("I", old.i, now.i, "s");
        Diff("D", old.d, now.d, "s");

        bool live;
        try { live = await _m.Temp.SetPidAsync(ch, band, now.p, now.i, now.d); }
        catch (Exception ex)
        {
            if (changes.Count == 0) changes.Add(("PID", "", ""));
            foreach (var (key, o, n) in changes)
                _m.Alarm.Log("err", "设置", $"修改 · {name} {key} 失败", $"{o} → {n} · AI-8 写入失败，未生效");
            return OpResult.Fail(OpCodes.HardwareWriteFailed, $"{name} PID 写入失败（AI-8 通讯）· 未生效", "err",
                new { device = "temp", channel = ch, band, error = ex.Message }) with { Quiet = true };
        }
        foreach (var (key, o, n) in changes) _m.LogSetting($"{name} {key}", o, n);
        return OpResult.Done(live ? $"{name} PID 已写入仪表" : $"{name} PID 已保存 · 切到该档时生效",
                             "ok", new { live, band }) with { Quiet = true };
    }

    // ============ 自整定 ============

    /// <summary>
    /// 打开整定目标温度键盘之前的预检（只读，不改状态）：仲裁 + 启动加热公共检查（急停 / 温控未配置或离线 / 超温超压未解除）。
    /// 文字与今天打开键盘前的 HeatBlockReason 提示一致（err）。
    /// </summary>
    public OpResult CheckAutotune(CommandSource src, int ch)
    {
        if (Admit(src, ActionClass.Control) is { } deny) return deny;
        if (ChannelOr(ch, out _) is { } nf) return nf;
        return HeatBlocked(ch) ?? OpResult.Silent;
    }

    /// <summary>
    /// 先把 SP 写成整定目标温度，再启动 AI-8 自整定（仪表在 SP 附近全功率振荡）。
    /// 确认时重新判断启动加热公共检查（K3）；目标温度 40..TMaxSp。严格守卫：通道须为 已停用 / 已启用 / 已结束，且不在整定中。
    /// 成功「RVn 已设目标 x℃ 并启动自整定」ok；SP 下发失败「SP 下发失败，未启动整定」err；仪表拒绝时原样提示 err。
    /// </summary>
    public Task<OpResult> TryStartAutotune(CommandSource src, int ch, double tempC)
    {
        if (Admit(src, ActionClass.Control) is { } deny) return deny.AsTask();
        if (ChannelOr(ch, out var c) is { } nf) return nf.AsTask();
        if (HeatBlocked(ch) is { } hb) return hb.AsTask();
        if (Invalid(ValueRules.AutotuneSp(tempC, _m.Settings.TMaxSp)) is { } bad) return bad.AsTask();
        if (CommandPolicy.Strict(src))
        {
            if (_m.Temp.Channels[ch - 1].Autotuning)
                return OpResult.Fail(OpCodes.AutotuneActive, $"RV{ch} 正在自整定 · 请先取消").AsTask();
            if (c.State is not (ReactorState.Idle or ReactorState.Ready or ReactorState.Done))
                return OpResult.Fail(OpCodes.ChannelStateInvalid, $"RV{ch} 正在运行，不能自整定").AsTask();
        }
        return StartAutotuneTailAsync(ch, tempC);
    }

    private async Task<OpResult> StartAutotuneTailAsync(int ch, double sp)
    {
        string name = _m.Temp.Channels[ch - 1].Name;
        try { await _m.Temp.SetSetpointAsync(ch, sp); }
        catch (Exception ex)
        {
            return OpResult.Fail(OpCodes.HardwareWriteFailed, "SP 下发失败，未启动整定", "err",
                new { device = "temp", channel = ch, error = ex.Message });
        }
        var err = await _m.Temp.AutotuneAsync(ch);
        _m.Alarm.Log(err == null ? "info" : "warn", name, err == null ? "启动自整定" : "自整定未启动",
            err == null ? $"整定目标 {Fmt(sp, "℃")}" : err);
        return err == null
            ? OpResult.Done($"{name} 已设目标 {sp:0}℃ 并启动自整定", "ok", new { channel = ch, sp })
            : OpResult.Fail(OpCodes.HardwareWriteFailed, err, "err", new { device = "temp", channel = ch });
    }

    /// <summary>取消自整定并停止该通道输出（At=4）。记「取消自整定」事件；提示「RVn 已取消自整定并停止输出」ok。</summary>
    public Task<OpResult> TryCancelAutotune(CommandSource src, int ch)
    {
        if (Admit(src, ActionClass.Stop) is { } deny) return deny.AsTask();
        if (ChannelOr(ch, out _) is { } nf) return nf.AsTask();
        return CancelAutotuneTailAsync(ch);
    }

    private async Task<OpResult> CancelAutotuneTailAsync(int ch)
    {
        string name = _m.Temp.Channels[ch - 1].Name;
        await _m.Temp.CancelAutotuneAsync(ch);
        _m.Alarm.Log("info", name, "取消自整定", "At=4 停止输出");
        return OpResult.Done($"{name} 已取消自整定并停止输出");
    }

    // ============ Ctn / 档位分界（只允许在设备触屏上进行，附录 B）============

    /// <summary>一键把启用回路数 Ctn 设为 8（红色徽章）。成功 ok，失败把仪表返回的原因 err 提示。</summary>
    public Task<OpResult> TryFixCtn(CommandSource src)
    {
        if (Admit(src, ActionClass.Control, Exposure.LocalOnly) is { } deny) return deny.AsTask();
        return FixCtnTailAsync();
    }

    private async Task<OpResult> FixCtnTailAsync()
    {
        var err = await _m.Temp.FixCtnAsync();
        return err == null
            ? OpResult.Done("已把启用回路数 Ctn 设为 8，全部 8 通道受控")
            : OpResult.Fail(OpCodes.HardwareWriteFailed, err);
    }

    /// <summary>
    /// 改温度档位分界：which = 1 为「低温 / 中温」（低温档上限 = 中温档下限），2 为「中温 / 高温」。
    /// 范围同键盘（分界 1：10..中温上限−10；分界 2：低温上限+10..390）。记设置事件并落盘；没有 Toast（与今天一致）。
    /// </summary>
    public Task<OpResult> TrySetBandBound(CommandSource src, int which, double value)
    {
        if (Admit(src, ActionClass.Control, Exposure.LocalOnly) is { } deny) return deny.AsTask();
        var b = _m.Temp.Bands;
        if (Invalid(ValueRules.BandBound(which, value, b[0].Hi, b[1].Hi)) is { } bad) return bad.AsTask();
        int lo = which - 1;   // 分界 1：Bands[0].Hi / Bands[1].Lo；分界 2：Bands[1].Hi / Bands[2].Lo
        string key = which == 1 ? "低温 / 中温 分界" : "中温 / 高温 分界";
        _m.LogSetting(key, Fmt(b[lo].Hi, "℃"), Fmt(value, "℃"));
        b[lo].Hi = value;
        b[lo + 1].Lo = value;
        _m.PersistSoon();
        return (OpResult.Silent with { Data = new { which, value } }).AsTask();
    }

    /// <summary>设置事件里的数值格式（与 SettingViewModel.Fmt 一致）。</summary>
    private static string Fmt(double v, string unit)
    {
        string n = v.ToString("0.###", CultureInfo.InvariantCulture);
        return unit.Length > 0 ? $"{n} {unit}" : n;
    }
}
