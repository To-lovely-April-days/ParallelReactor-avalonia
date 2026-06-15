using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Models;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ParallelReactor.Service;

namespace ParallelReactor.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    public ObservableCollection<Reactor> Reactors { get; } = new();
    public ObservableCollection<GasInlet> GasInlets { get; } = new();
    public List<Recipe> Recipes { get; } = new();

    // ============ 顶栏通知铃铛数据 ============
    public ObservableCollection<Notification> Notifications { get; } = new();

    public int NotifCount => Notifications.Count;
    public bool HasNotif => Notifications.Count > 0;
    public bool HasAlarm => Notifications.Any(n => n.Kind == "alarm");
    public string NotifCountText => NotifCount.ToString();
    public string NotifSummary => $"{NotifCount} 条未处理";
    public string NotifTotalText => $"共 {NotifCount} 条";

    [ObservableProperty] private bool _ventOn;
    [ObservableProperty] private bool _manualValve;
    [ObservableProperty] private bool _isAdmin = true;
    [ObservableProperty] private bool _estopped;

    // ============ 全局搅拌（共用一台搅拌器：8 釜同开同关、同一转速）============
    // 通过 StirController 驱动雷赛 DM2C 步进驱动器。总线由 HardwareOptions 决定：
    // 配置了串口(PR_STIR_PORT) → 走真实 ModbusRtuMaster；否则用内存 mock 演示。
    private readonly Hardware.IModbusMaster _bus = HardwareOptions.CreateStirBus();
    private readonly Services.StirController _stir;

    [ObservableProperty] private bool _stirOn = true;
    [ObservableProperty] private int _stirRpm = 600;

    public string StirStateText => StirOn ? "运行中" : "已停止";

    // ============ 预运行检查 ============
    [ObservableProperty] private bool _isPreRunOpen;
    [ObservableProperty] private string _preRunSummary = "";
    [ObservableProperty] private bool _preRunCanRun;
    public ObservableCollection<PreRunCheck> PreRunChecks { get; } = new();

    // ============ 退出程序确认 ============
    [ObservableProperty] private bool _isExitConfirmOpen;

    partial void OnStirOnChanged(bool value)
    {
        OnPropertyChanged(nameof(StirStateText));
        ApplyStir();
        if (value) _ = _stir.StartAsync(StirRpm);
        else _stir.Stop();
    }

    partial void OnStirRpmChanged(int value)
    {
        ApplyStir();
        _ = _stir.SetRpmAsync(value);
    }

    /// <summary>把全局搅拌状态下发到所有运行中的反应釜（驱动桨叶动画）。</summary>
    private void ApplyStir()
    {
        foreach (var c in Reactors)
            c.Rpm = (StirOn && c.IsRunning) ? StirRpm : 0;
        RefreshSchematic();
        if (Drawer.IsOpen) Drawer.RaiseAll();
    }

    [ObservableProperty] private string _runCountText = "6 / 8";
    [ObservableProperty] private string _clockTime = "14:33:00";
    [ObservableProperty] private string _clockDate = "2026-06-02 周二";

    [ObservableProperty] private string _activeTab = "home";

    public DrawerViewModel Drawer { get; }
    public KeyboardViewModel Keyboard { get; } = new();
    public AppSettings Settings { get; } = new();
    public ProgramViewModel Program { get; }
    public GraphViewModel Graph { get; }
    public DataViewModel Data { get; }
    public AlarmViewModel Alarm { get; }
    public SettingViewModel Setting { get; }
    public LeakViewModel Leak { get; }
    public RecipeViewModel RecipePicker { get; }

    // 气路图重绘信号（View 订阅后调用 InvalidateVisual）
    public event Action? SchematicInvalidated;
    // Toast 信号
    public event Action<string, string>? ToastRequested;
    // Tab 切换信号
    public event Action<string>? TabSwitched;

    private DateTime _clock = new(2026, 6, 2, 14, 33, 0);
    private static readonly string[] Wk = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };

    public MainViewModel()
    {
        // 搅拌驱动器（DM2C，站地址来自 HardwareOptions）。mock 与真机行为一致，仅总线实现不同。
        _stir = new Services.StirController(
            new Hardware.StepperDriver(_bus, HardwareOptions.StirSlave));
        _stir.FaultRaised += code =>
        {
            StirOn = false;
            Toast("err", $"搅拌驱动器报警（0x{code:X4}）· 已停止搅拌");
        };
        _stir.CommError += msg => Toast("err", $"搅拌通讯失败 · {msg}");

        Drawer = new DrawerViewModel(this);
        SeedData();
        // 抽屉默认选中 RV1（关闭时滑出屏外不可见）。避免 Reactor 为 null 时
        // 抽屉子树里的 {Binding Reactor.*} 每个 tick 刷一批绑定告警。
        Drawer.Reactor ??= Reactors.FirstOrDefault();
        UpdateRunCount();
        Program = new ProgramViewModel(this);
        Graph = new GraphViewModel(this);
        Data = new DataViewModel(this);
        Alarm = new AlarmViewModel();
        Setting = new SettingViewModel(this);
        Leak = new LeakViewModel(this);
        RecipePicker = new RecipeViewModel(this);

        // 初始 StirOn=true 由字段初始化设置，不会触发属性回调，这里显式启动搅拌。
        if (StirOn) _ = _stir.StartAsync(StirRpm);

        // 提示当前搅拌运行在真机还是模拟（开机一次）。延后到 View 订阅 Toast 之后再发。
        var (k, m) = HardwareOptions.UseRealStir
            ? ("ok", $"搅拌已连接真机 · {HardwareOptions.StirPort} @ {HardwareOptions.StirBaud} · 站{HardwareOptions.StirSlave}")
            : ("warn", "搅拌运行于模拟模式 · 未配置串口（设 PR_STIR_PORT 连真机）");
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Toast(k, m));
    }

    private void SeedData()
    {
        Reactors.Add(new Reactor { Id = 1, State = ReactorState.React, T = 150.4, TSp = 150, P = 325.6, PSp = 320, Gas = 13.00, Rpm = 600, RpmSp = 600, Valve = true, Vol = 3, Blade = "桨式", End = "密封" });
        Reactors.Add(new Reactor { Id = 2, State = ReactorState.React, T = 150.0, TSp = 150, P = 317.3, PSp = 320, Gas = 12.45, Rpm = 600, RpmSp = 600, Valve = true, Vol = 3, Blade = "桨式", End = "密封" });
        Reactors.Add(new Reactor { Id = 3, State = ReactorState.Pressing, T = 148.0, TSp = 150, P = 269.8, PSp = 320, Gas = 0, Rpm = 600, RpmSp = 600, Valve = true, Vol = 3, Blade = "桨式", End = "吹扫" });
        Reactors.Add(new Reactor { Id = 4, State = ReactorState.Heating, T = 117.8, TSp = 150, P = 199.2, PSp = 320, Gas = 0, Rpm = 600, RpmSp = 600, Valve = false, Vol = 3, Blade = "锚式", End = "密封" });
        Reactors.Add(new Reactor { Id = 5, State = ReactorState.Alarm, T = 151.8, TSp = 150, P = 511.8, PSp = 320, Gas = 14.90, Rpm = 600, RpmSp = 600, Valve = true, Vol = 3, Blade = "桨式", End = "密封" });
        Reactors.Add(new Reactor { Id = 6, State = ReactorState.React, T = 149.4, TSp = 150, P = 317.1, PSp = 320, Gas = 12.71, Rpm = 600, RpmSp = 600, Valve = true, Vol = 3, Blade = "桨式", End = "密封" });
        Reactors.Add(new Reactor { Id = 7, State = ReactorState.Done, T = 42.0, TSp = 0, P = 14.7, PSp = 0, Gas = 13.50, Rpm = 0, RpmSp = 600, Valve = false, Vol = 3, Blade = "桨式", End = "密封" });
        Reactors.Add(new Reactor { Id = 8, State = ReactorState.Idle, T = 0, TSp = 0, P = 0, PSp = 0, Gas = 0, Rpm = 0, RpmSp = 600, Valve = false, Vol = 0, Blade = "桨式", End = "密封" });

        GasInlets.Add(new GasInlet { Label = "惰性气体", On = true, P = 518 });
        GasInlets.Add(new GasInlet { Label = "气体 A", On = true, P = 520 });
        GasInlets.Add(new GasInlet { Label = "气体 B", On = false, P = 515 });

        Recipes.Add(new Recipe { Id = "hydro-pdc", Name = "加氢 · Pd/C", Sub = "苯甲酸酯加氢 · 标准条件", Tag = "催化筛选", TSp = 40, PSp = 50, RpmSp = 1000, Vol = 4, Blade = "桨式", End = "密封", Run = "02:00:00" });
        Recipes.Add(new Recipe { Id = "hydro-rh", Name = "加氢 · Rh 均相", Sub = "温和条件 · 均相催化", Tag = "催化筛选", TSp = 60, PSp = 200, RpmSp = 800, Vol = 3, Blade = "桨式", End = "密封", Run = "04:00:00" });
        Recipes.Add(new Recipe { Id = "olefin-poly", Name = "烯烃聚合", Sub = "Ziegler-Natta · 高温高压", Tag = "聚合", TSp = 80, PSp = 350, RpmSp = 1000, Vol = 4, Blade = "锚式", End = "吹扫", Run = "01:30:00" });
        Recipes.Add(new Recipe { Id = "co-carbo", Name = "CO 羰基化", Sub = "低压 CO · 长反应时间", Tag = "有机", TSp = 120, PSp = 150, RpmSp = 600, Vol = 3, Blade = "桨式", End = "淬灭", Run = "06:00:00" });
        Recipes.Add(new Recipe { Id = "leak-test", Name = "空白泄漏测试", Sub = "仅充氮 · 不加热", Tag = "诊断", TSp = 25, PSp = 500, RpmSp = 0, Vol = 0, Blade = "桨式", End = "排空", Run = "00:40:00" });

        Notifications.Add(Notification.Alarm(
            "RV5 超压报警",
            "RV5 压力已达到危险水平，超过 510 psi 阈值。密封圈仅承受 500 psi，继续加压有泄漏与喷射风险。"));
        Notifications.Add(Notification.Advice(
            "RV4 升温过慢",
            "RV4 已升温 14 分钟仍未达到设定值。可能原因：缺少传热液、热电偶接触不良或相邻 RV 温差过大。"));
    }

    public Reactor? FindReactor(int id) => Reactors.FirstOrDefault(r => r.Id == id);

    public void RefreshSchematic() => SchematicInvalidated?.Invoke();
    public void Toast(string kind, string msg) => ToastRequested?.Invoke(kind, msg);

    // ============ 阀门手动控制（带权限/状态守卫）============
    public void TryValve(int id)
    {
        var c = FindReactor(id);
        if (c == null) return;
        if (!IsAdmin) { Toast("err", "手动阀控需要管理员权限"); return; }
        if (!ManualValve) { Toast("warn", "请先在工具栏开启「手动阀控」"); return; }
        if (!c.Valve && c.State is ReactorState.Pressing or ReactorState.React or ReactorState.Alarm)
        {
            Toast("err", $"加压/反应中禁止手动开阀（RV{id}）");
            return;
        }
        c.Valve = !c.Valve;
        RefreshSchematic();
        Drawer.RaiseAll();
        Toast(c.Valve ? "ok" : "warn", $"SV{id} {(c.Valve ? "已开" : "已关")}");
    }

    public void ToggleGas(int index)
    {
        if (!IsAdmin) { Toast("err", "需要管理员权限"); return; }
        if (!ManualValve) { Toast("warn", "请先开启「手动阀控」"); return; }
        var g = GasInlets[index];
        g.On = !g.On;
        RefreshSchematic();
        Toast(g.On ? "ok" : "warn", $"{g.Label} 进气阀 {(g.On ? "已开" : "已关")}");
    }

    public void ToggleVent()
    {
        if (!IsAdmin) { Toast("err", "需要管理员权限"); return; }
        if (!ManualValve) { Toast("warn", "请先开启「手动阀控」"); return; }
        VentOn = !VentOn;
        RefreshSchematic();
        Toast(VentOn ? "warn" : "ok", $"排空阀 {(VentOn ? "已开" : "已关")}");
    }

    public void OpenDrawer(int id) => Drawer.Open(id);

    public void CopyConfigToAll(Reactor src)
    {
        foreach (var x in Reactors.Where(r => r.State != ReactorState.Idle))
        {
            x.TSp = src.TSp; x.PSp = src.PSp; x.RpmSp = src.RpmSp;
            x.Vol = src.Vol; x.Blade = src.Blade; x.End = src.End; x.Run = src.Run;
        }
        RefreshSchematic();
    }

    // ============ 工具栏命令 ============
    [RelayCommand]
    private void ToggleStir()
    {
        StirOn = !StirOn;
        Toast(StirOn ? "ok" : "warn",
            StirOn ? $"搅拌已开启 · 全部反应釜 {StirRpm} rpm" : "搅拌已停止 · 全部反应釜");
    }

    [RelayCommand]
    private void EditStirRpm()
        => Keyboard.OpenNumeric("搅拌转速（全局共用）", StirRpm, "rpm", 0, Settings.RpmMax, v =>
        {
            StirRpm = (int)v;
            if (StirOn) Toast("ok", $"搅拌转速已设为 {StirRpm} rpm · 全部反应釜");
        });

    [RelayCommand]
    private void ToggleManual()
    {
        if (!IsAdmin) { Toast("err", "手动阀控需要管理员权限"); return; }
        ManualValve = !ManualValve;
        Toast(ManualValve ? "warn" : "ok",
            ManualValve ? "手动阀控已开启 · 点击阀门可手动操作" : "手动阀控已关闭");
    }

    [RelayCommand]
    private void StopAll()
    {
        foreach (var c in Reactors.Where(r => r.State != ReactorState.Idle))
        {
            c.State = ReactorState.Done;
            c.Valve = false;
            c.Rpm = 0;
        }
        RefreshSchematic();
        Drawer.RaiseAll();
        UpdateRunCount();
        Toast("warn", "已停止全部通道");
    }

    [RelayCommand]
    private void RunAll()
    {
        if (Estopped) { Toast("err", "急停状态下不可启动 · 请先复位"); return; }
        BuildPreRun();
        IsPreRunOpen = true;
    }

    [RelayCommand]
    private void CancelPreRun() => IsPreRunOpen = false;

    // ============ 退出程序 ============
    /// <summary>打开退出确认弹窗（由设置页的「退出程序」触发）。</summary>
    public void OpenExitConfirm() => IsExitConfirmOpen = true;

    [RelayCommand]
    private void CancelExit() => IsExitConfirmOpen = false;

    [RelayCommand]
    private void ConfirmExit()
    {
        IsExitConfirmOpen = false;
        _stir.Stop();   // 停掉搅拌 JOG 保活，驱动器随之停转，避免退出后电机仍按最后一帧维持
        if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
        else
            Environment.Exit(0);
    }

    [RelayCommand]
    private void ConfirmRun()
    {
        if (!PreRunCanRun) { Toast("err", "存在未处理的异常项，无法开始运行"); return; }
        IsPreRunOpen = false;

        // 重新启动已停止 / 已结束的通道（停用通道不参与）
        foreach (var c in Reactors)
        {
            if (c.State == ReactorState.Done)
            {
                c.State = ReactorState.React;
                c.Valve = true;
            }
        }

        // 开始运行：开启全局搅拌并同步桨叶动画
        if (!StirOn) StirOn = true;   // setter 会触发 ApplyStir
        else ApplyStir();

        RefreshSchematic();
        Drawer.RaiseAll();
        UpdateRunCount();
        Toast("ok", $"预运行检查通过 · 已开始运行（{Reactors.Count(r => r.State != ReactorState.Idle)} 个通道）");
    }

    /// <summary>按当前设备状态生成预运行检查项。</summary>
    private void BuildPreRun()
    {
        PreRunChecks.Clear();

        // 急停回路
        PreRunChecks.Add(Estopped
            ? PreRunCheck.Err("急停回路", "急停已触发，必须先复位才能运行")
            : PreRunCheck.Ok("急停回路", "急停未触发，安全回路正常"));

        // 排空 / 泄压阀
        PreRunChecks.Add(VentOn
            ? PreRunCheck.Err("排空 / 泄压阀", "排空阀处于开启状态，加压前必须关闭")
            : PreRunCheck.Ok("排空 / 泄压阀", "排空阀已关闭"));

        // 控制模式
        PreRunChecks.Add(ManualValve
            ? PreRunCheck.Warn("控制模式", "手动阀控仍开启，自动运行时建议关闭")
            : PreRunCheck.Ok("控制模式", "自动模式，手动阀控已关闭"));

        // 惰性气体
        var inert = GasInlets.FirstOrDefault(g => g.Label.Contains("惰性"));
        PreRunChecks.Add(inert is { On: true }
            ? PreRunCheck.Ok("惰性气体", $"已接通 · {inert.P} psi")
            : PreRunCheck.Warn("惰性气体", "惰性气体未接通，吹扫 / 保护气可能不可用"));

        // 反应釜压力（超压报警）
        var alarm = Reactors.Where(r => r.State == ReactorState.Alarm).ToList();
        PreRunChecks.Add(alarm.Count > 0
            ? PreRunCheck.Err("反应釜压力", $"{string.Join("、", alarm.Select(r => "RV" + r.Id))} 超压，需先泄压处理")
            : PreRunCheck.Ok("反应釜压力", "各通道压力均在安全范围"));

        // 搅拌器（全局共用）
        PreRunChecks.Add(StirOn
            ? PreRunCheck.Ok("搅拌器", $"就绪 · {StirRpm} rpm（全部反应釜共用）")
            : PreRunCheck.Warn("搅拌器", "搅拌当前关闭，请确认本次运行是否需要搅拌"));

        // 参与运行的通道
        int active = Reactors.Count(r => r.State != ReactorState.Idle);
        PreRunChecks.Add(active > 0
            ? PreRunCheck.Ok("运行通道", $"{active} / 8 个通道将参与本次运行")
            : PreRunCheck.Err("运行通道", "没有已启用的通道，请先启用至少一个反应釜"));

        int ok = PreRunChecks.Count(c => c.Kind == "ok");
        int warn = PreRunChecks.Count(c => c.Kind == "warn");
        int err = PreRunChecks.Count(c => c.Kind == "err");
        PreRunCanRun = err == 0;
        PreRunSummary = err == 0
            ? $"检查通过 · {ok} 项正常" + (warn > 0 ? $" · {warn} 项提示" : "")
            : $"{err} 项异常需处理 · {warn} 项提示 · {ok} 项正常";
    }

    [RelayCommand]
    private void Estop() => Toast("err", "（演示）急停确认弹窗 — 后续接入");

    [RelayCommand]
    private void Logout() => Toast("ok", "（演示）退出登录");

    [RelayCommand]
    private void SwitchTabCmd(string id) => SwitchTab(id);

    public void SwitchTab(string id)
    {
        ActiveTab = id;
        TabSwitched?.Invoke(id);
    }

    // —— 工具栏联动：运行中只显示「全部停止」，否则只显示「开始运行」——
    public bool AnyRunning => Reactors.Any(r => r.IsRunning);
    public bool NotRunning => !AnyRunning;

    public void UpdateRunCount()
    {
        RunCountText = $"{Reactors.Count(r => r.State != ReactorState.Idle)} / 8";
        OnPropertyChanged(nameof(AnyRunning));
        OnPropertyChanged(nameof(NotRunning));
    }

    // ============ 实时数据 tick（对应 HTML setInterval）============
    public void Tick()
    {
        foreach (var c in Reactors)
        {
            if (c.State is ReactorState.Idle or ReactorState.Done) continue;
            c.T += (Random.Shared.NextDouble() - 0.5) * 0.3;
            if (c.State == ReactorState.React)
            {
                c.P += (Random.Shared.NextDouble() - 0.5) * 1.2;
                c.Gas += Random.Shared.NextDouble() * 0.02;
            }
            else if (c.State == ReactorState.Pressing)
            {
                c.P = Math.Min(c.PSp, c.P + Random.Shared.NextDouble() * 2);
            }
            else if (c.State == ReactorState.Heating)
            {
                c.T = Math.Min(c.TSp, c.T + Random.Shared.NextDouble() * 0.4);
            }

            // 超压 / 超温报警判定（阈值来自全局设置）
            if (c.P >= Settings.OverPressure || c.T >= Settings.OverTemp)
                c.State = ReactorState.Alarm;
        }
        RefreshSchematic();
        if (Drawer.IsOpen) Drawer.RaiseAll();
        Graph.AppendSample();
    }

    public void ClockTick()
    {
        _clock = _clock.AddSeconds(1);
        ClockTime = _clock.ToString("HH:mm:ss");
        ClockDate = _clock.ToString("yyyy-MM-dd") + " " + Wk[(int)_clock.DayOfWeek];
        Setting.RefreshNetworkInfo();   // 周期刷新设置页的本机 IP（内部已节流约 5s）
    }
}

/// <summary>预运行检查的一项（正常 ok / 提示 warn / 异常 err，含主题配色与图标）。</summary>
public class PreRunCheck
{
    public string Kind { get; init; } = "ok";   // ok | warn | err
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";

    public static PreRunCheck Ok(string t, string d) => new() { Kind = "ok", Title = t, Detail = d };
    public static PreRunCheck Warn(string t, string d) => new() { Kind = "warn", Title = t, Detail = d };
    public static PreRunCheck Err(string t, string d) => new() { Kind = "err", Title = t, Detail = d };

    public IBrush Tint => Kind switch
    {
        "err" => new SolidColorBrush(Color.Parse("#e0394c")),
        "warn" => new SolidColorBrush(Color.Parse("#f0a830")),
        _ => new SolidColorBrush(Color.Parse("#7aa86a")),
    };

    public IBrush TintBg => Kind switch
    {
        "err" => new SolidColorBrush(Color.FromArgb(26, 224, 57, 76)),
        "warn" => new SolidColorBrush(Color.FromArgb(26, 240, 168, 48)),
        _ => new SolidColorBrush(Color.FromArgb(26, 132, 204, 22)),
    };

    public Geometry Icon => Geometry.Parse(Kind switch
    {
        "err" => "M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20z M15 9l-6 6 M9 9l6 6",
        "warn" => "M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20z M12 8v5 M12 16h0.01",
        _ => "M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20z M8 12l3 3 5-6",
    });

    public string StatusText => Kind switch { "err" => "异常", "warn" => "提示", _ => "正常" };
}
