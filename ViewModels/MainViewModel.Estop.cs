using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Models;
using ParallelReactor.Services;

namespace ParallelReactor.ViewModels;

/// <summary>
/// 软件急停：一键并行下发 停加热（AI-8 Srun=9655 + 8 路 At=4）/ 停搅拌（DM2C 减速停车）/ 全关 12 路阀门（IO16R），
/// 同时中止气路自动序列、停止曲线与逼近、各通道转为已结束。之后锁定：拒绝一切启动类操作，
/// 未确认的部分每 3 个 Tick 自动重发，直到操作员确认复位。复位只解除锁定，设备保持停止。
/// <para>这是通讯层面的急停，不能替代设备上的硬件急停回路（软件读不到硬件急停的状态）。</para>
/// </summary>
public partial class MainViewModel
{
    [ObservableProperty] private bool _isEstopPanelOpen;
    [ObservableProperty] private bool _isEstopResetConfirmOpen;
    [ObservableProperty] private string _estopTimeText = "";
    [ObservableProperty] private string _estopResetWarn = "";

    public EstopRow EstopHeat { get; } = new("加热", "AI-8 全局停止 Srun=9655 + 8 路 At=4", "已停止");
    public EstopRow EstopStir { get; } = new("搅拌", "DM2C 减速停车", "已停止");
    public EstopRow EstopValves { get; } = new("阀门", "IO16R 12 路全关（SV1–8 · 三路气源 · 排空）", "已全关");
    private IEnumerable<EstopRow> EstopRows => new[] { EstopHeat, EstopStir, EstopValves };

    public string EstopButtonText => Estopped ? "急停中" : "急停";
    public bool HasEstopResetWarn => EstopResetWarn.Length > 0;
    public string EstopSummary
    {
        get
        {
            var bad = EstopRows.Where(r => r.Part is EstopPart.Failed).Select(r => r.Name).ToList();
            if (EstopRows.Any(r => r.Part == EstopPart.Pending)) return "正在下发停止指令…";
            return bad.Count == 0
                ? "停止指令已全部确认 · 设备保持停止，复位前不能重新运行"
                : $"{string.Join("、", bad)}停止未确认（通讯失败）· 约每 4 秒自动重发 · 必要时使用设备硬件急停";
        }
    }

    private int _estopInFlight;   // 正在进行的急停下发（触发 / 补发 / 重发）
    private int _estopRetryTick;

    partial void OnEstoppedChanged(bool value)
    {
        // 服务层兜底锁：界面守卫之外，已在途的启动流程也会在下一步写入前被拦下
        Temp.Locked = value;
        _stir.Locked = value;
        OnPropertyChanged(nameof(EstopButtonText));
    }

    partial void OnEstopResetWarnChanged(string value) => OnPropertyChanged(nameof(HasEstopResetWarn));

    /// <summary>工具栏「急停」：任何时候按下都会（重新）下发全部停止指令并打开急停面板。不需要确认。</summary>
    [RelayCommand]
    private void Estop() => _ = TriggerEstopAsync();

    private async Task TriggerEstopAsync()
    {
        bool first = !Estopped;
        Estopped = true;   // 先锁：OnEstoppedChanged 同步锁住温控 / 搅拌服务
        IsEstopPanelOpen = true;
        IsEstopResetConfirmOpen = false;
        if (first) EstopTimeText = $"触发时间 {DateTime.Now:HH:mm:ss}";
        Alarm.Log("err", "系统", first ? "急停按下 · 停止指令下发" : "急停再次按下 · 重新下发",
            "加热 / 搅拌 / 阀门三路并行下发，各部分回应时刻见事件日志");

        // —— 界面侧立即停：气路序列、程序执行、曲线、逼近、通道状态、搅拌开关、手动阀控、各确认弹窗 ——
        bool hadGas = GasBusy;
        AbortGas();
        Program.AbortProgram();   // 程序执行中也中止（非气路步骤如等待/反应倒计时，靠程序自己的取消令牌）
        foreach (var c in Reactors)
        {
            if (c.Profile.Running) c.Profile.Stop();
            c.Approach.Stop();
            if (c.State != ReactorState.Idle) c.State = ReactorState.Done;
            c.Rpm = 0;
        }
        StirOn = false;        // Estopped 已为 true：setter 不另发停止帧，由下面统一下发并等结果
        ManualValve = false;
        IsPreRunOpen = false;
        Program.IsRunConfirmOpen = false;
        Keyboard.IsOpen = false;   // 关掉键盘且不执行其回调
        UpdateRunCount();
        RefreshSchematic();
        Drawer.RaiseAll();

        // —— 硬件侧：三条独立总线并行下发 ——
        foreach (var r in EstopRows) r.Part = EstopPart.Pending;
        RaiseEstop();
        _estopInFlight++;
        try
        {
            var res = await EmergencyStop.RunAsync(
                HeatStopTimed("首轮"),
                TimedEstopOp("搅拌", "首轮", StirStopOp()),
                TimedEstopOp("阀门", "首轮", ValveCloseOp()));
            SetEstopParts(res);
            if (first) LogEstopTriggered(res);

            // 补发一轮：按下急停时可能有一帧启动 / 开阀写入已在串口队列里，会落在停止帧之后。
            // 等气路序列收尾（它的 finally 也会全关阀）并留出在途事务的时间，再完整下发一次。
            var sw = Stopwatch.StartNew();
            while (Estopped && (hadGas && GasBusy || sw.ElapsedMilliseconds < 1500) && sw.ElapsedMilliseconds < 4000)
                await Task.Delay(100);
            // 等待期间已复位：不再补发，避免把复位后刚启动的加热 / 搅拌又停掉
            if (!Estopped) return;
            SetEstopParts(await EmergencyStop.RunAsync(
                HeatStopTimed("补发"),
                TimedEstopOp("搅拌", "补发", StirStopOp()),
                TimedEstopOp("阀门", "补发", ValveCloseOp())));
        }
        finally { _estopInFlight--; }
    }

    /// <summary>急停锁定期间由 Tick 调用：每 3 个 Tick 重发一次未确认的部分（已确认的不重复写，避免磨损仪表 EEPROM）。</summary>
    private void EstopRetryTick()
    {
        if (!Estopped || _estopInFlight > 0 || ++_estopRetryTick % 3 != 0) return;
        if (EstopRows.Any(r => r.Part == EstopPart.Failed)) _ = RetryEstopAsync();
    }

    private async Task RetryEstopAsync()
    {
        _estopInFlight++;
        try
        {
            // 三路各自独立重发（任务一创建就已开始，三条总线并行）
            var h = EstopHeat.Part == EstopPart.Failed ? EmergencyStop.PartAsync(HeatStopTimed("自动重发")) : null;
            var s = EstopStir.Part == EstopPart.Failed ? EmergencyStop.PartAsync(TimedEstopOp("搅拌", "自动重发", StirStopOp())) : null;
            var v = EstopValves.Part == EstopPart.Failed ? EmergencyStop.PartAsync(TimedEstopOp("阀门", "自动重发", ValveCloseOp())) : null;
            if (h != null) SetRetried(EstopHeat, await h);
            if (s != null) SetRetried(EstopStir, await s);
            if (v != null) SetRetried(EstopValves, await v);
            RaiseEstop();
        }
        finally { _estopInFlight--; }
    }

    private void SetRetried(EstopRow row, EstopPart part)
    {
        if (!Estopped) return;   // 重发期间已复位：不再改面板
        row.Part = part;
        if (part == EstopPart.Ok)
            Alarm.Log("info", "系统", $"急停 · {row.Name}{row.OkText}", "自动重发后确认");
    }

    private void SetEstopParts(EstopResult r)
    {
        if (!Estopped) return;
        EstopHeat.Part = r.Heat;
        EstopStir.Part = r.Stir;
        EstopValves.Part = r.Valves;
        RaiseEstop();
    }

    private void RaiseEstop() => OnPropertyChanged(nameof(EstopSummary));

    private void LogEstopTriggered(EstopResult r)
    {
        string detail = string.Join(" · ", EstopRows.Select(x => $"{x.Name} {x.StatusText}"));
        Alarm.Log("err", "系统", "急停触发", detail);
        Toast("err", r.AllConfirmed
            ? "急停已触发 · 加热 / 搅拌已停止，阀门已全关 · 复位后才能重新运行"
            : "急停已触发 · 部分停止未确认，正在自动重发 · 必要时使用设备硬件急停");
    }

    // 未配置串口的模块软件从未控制过，记为「未配置」，不算失败
    private Func<Task<bool>>? StirStopOp() => HardwareOptions.UseRealStir ? _stir.StopAsync : null;

    /// <summary>加热停止 + 计时事件；事件里的 Srun / At=4 两步结果取自<b>本次</b>调用（多轮并发时不会串到别的轮次）。</summary>
    private Func<Task<bool>>? HeatStopTimed(string round)
    {
        if (!HardwareOptions.UseRealTemp) return null;
        (bool Gate, bool Each)? d = null;
        return TimedEstopOp("加热", round, async () =>
        {
            var r = await Temp.EmergencyStopDetailedAsync();
            d = (r.Gate, r.Each);
            return r.Ok;
        }, () => d is { } x
            ? $" · Srun=9655 {(x.Gate ? "成功" : "失败")} · 8 路 At=4 {(x.Each ? "全部成功" : "有失败")}"
            : " · 未完成（异常）");
    }

    private Func<Task<bool>>? ValveCloseOp() => RealIo ? () => CloseAllValvesAsync(report: false) : null;

    /// <summary>急停面板「收起」：锁定保持，工具栏按钮显示「急停中」，再按会重新下发并打开面板。</summary>
    [RelayCommand]
    private void CloseEstopPanel() => IsEstopPanelOpen = false;

    /// <summary>急停面板「复位」：弹确认框；有停止未确认时在确认框里提醒先到现场确认。</summary>
    [RelayCommand]
    private void EstopReset()
    {
        var bad = EstopRows.Where(r => r.Part is EstopPart.Failed or EstopPart.Pending).Select(r => r.Name).ToList();
        EstopResetWarn = bad.Count == 0
            ? ""
            : $"注意：{string.Join("、", bad)}的停止指令尚未确认（通讯失败）。复位前请到设备现场确认已停止，必要时切断电源。";
        IsEstopResetConfirmOpen = true;
    }

    [RelayCommand]
    private void CancelEstopReset() => IsEstopResetConfirmOpen = false;

    [RelayCommand]
    private void ConfirmEstopReset()
    {
        IsEstopResetConfirmOpen = false;
        IsEstopPanelOpen = false;
        bool unconfirmed = HasEstopResetWarn;
        Estopped = false;   // 只解除锁定：设备保持停止，需要时重新开始运行
        Alarm.Log("warn", "系统", "急停已复位",
            "操作员确认复位 · 设备保持停止" + (unconfirmed ? " · 复位时仍有停止指令未确认" : ""));
        Toast("ok", "急停已复位 · 设备保持停止，需要时重新开始运行");
    }
}

/// <summary>急停面板的一行：某一部分的停止指令与下发结果。</summary>
public partial class EstopRow : ObservableObject
{
    public string Name { get; }
    public string Detail { get; }
    public string OkText { get; }
    [ObservableProperty] private EstopPart _part = EstopPart.Pending;

    public EstopRow(string name, string detail, string okText)
    {
        Name = name;
        Detail = detail;
        OkText = okText;
    }

    partial void OnPartChanged(EstopPart value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
    }

    public string StatusText => Part switch
    {
        EstopPart.Pending => "下发中…",
        EstopPart.Ok => OkText,
        EstopPart.Failed => "未确认 · 重发中",
        _ => "未配置串口",
    };

    public IBrush StatusBrush => new SolidColorBrush(Color.Parse(Part switch
    {
        EstopPart.Ok => "#5fae14",
        EstopPart.Failed => "#e0394c",
        EstopPart.Pending => "#c9820f",
        _ => "#7d8694",
    }));
}
