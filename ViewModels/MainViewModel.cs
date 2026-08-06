using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Models;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

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
    private readonly Hardware.IModbusMaster _bus = Services.HardwareOptions.CreateStirBus();
    private readonly Services.StirController _stir;

    // 温控：两台 AI-8（A 站 RV1-4、B 站 RV5-8），8 通道独立 PID + 自整定。挂在温控总线上（真机/mock）。
    private readonly Hardware.IModbusMaster _tempBus = Services.HardwareOptions.CreateTempBus();
    public Services.TempService Temp { get; }

    // 压力：艾莫迅 8AI（8 路 PT）。仅在配置了真实串口时启用真机采集，否则用首页 Random 模拟。
    private readonly Hardware.IModbusMaster _pressBus = Services.HardwareOptions.CreateAnalogBus();
    private readonly Services.PressureService _press;

    // 阀门：艾莫迅 IO16R（线圈驱动电磁阀）。配置真实串口后，界面阀门操作会写线圈。
    private readonly Hardware.IModbusMaster _ioBus = Services.HardwareOptions.CreateIoBus();
    private readonly Services.ValveService _valves;
    private static bool RealIo => Services.HardwareOptions.UseRealIo;

    [ObservableProperty] private bool _stirOn = true;
    [ObservableProperty] private int _stirRpm = 200;

    /// <summary>搅拌电机当前运行电流（A）——即驱动器设定的峰值电流（步进恒流，无实时负载电流反馈）。</summary>
    [ObservableProperty] private double _stirMotorCurrent;
    public string StirMotorCurrentText => $"{StirMotorCurrent:0.0} A";
    partial void OnStirMotorCurrentChanged(double value) => OnPropertyChanged(nameof(StirMotorCurrentText));

    /// <summary>搅拌电机故障文本（空=无故障）。由 Tick 周期轮询驱动器报警码 0x2203。</summary>
    [ObservableProperty] private string _stirFaultText = "";
    public bool StirHasFault => !string.IsNullOrEmpty(StirFaultText);
    partial void OnStirFaultTextChanged(string value) => OnPropertyChanged(nameof(StirHasFault));

    /// <summary>雷赛 DM2C 报警码 → 中文（见手册 §5.4.2）。</summary>
    private static string StirFaultName(ushort code) => code switch
    {
        0 => "",
        0x01 => "过流",
        0x02 => "过压",
        0x40 => "电流采样故障",
        0x80 => "锁轴/缺相（堵转）",
        0x100 => "参数自整定故障",
        0x200 => "EEPROM 故障",
        _ => $"故障 0x{code:X}"
    };

    /// <summary>从驱动器读回当前设定电流并刷新显示（通讯失败/为 0 时保留原值）。</summary>
    public async System.Threading.Tasks.Task RefreshStirCurrentAsync()
    {
        var a = await _stir.ReadCurrentAsync();
        if (a is { } v && v > 0) StirMotorCurrent = v;
    }

    /// <summary>驱动器母线电压（V）。带载时若周期性塌陷 → 供电容量不足（转速波动的电源侧证据）。</summary>
    [ObservableProperty] private double _stirBusVoltage;
    public string StirBusVoltageText => StirBusVoltage > 0 ? $" · 母线 {StirBusVoltage:0.0} V" : "";
    partial void OnStirBusVoltageChanged(double value) => OnPropertyChanged(nameof(StirBusVoltageText));

    /// <summary>轮询搅拌电机报警码 + 母线电压并刷新显示（在 Tick 里按节流调用）。</summary>
    public async System.Threading.Tasks.Task RefreshStirFaultAsync()
    {
        var c = await _stir.ReadAlarmAsync();
        if (c is { } code) StirFaultText = StirFaultName(code);
        var v = await _stir.ReadBusVoltageAsync();
        if (v is { } bv) StirBusVoltage = bv;
    }

    /// <summary>开机从驱动器回填当前细分到设置显示（读不到则保留默认）。</summary>
    private async System.Threading.Tasks.Task InitStirMicrostepAsync()
    {
        var m = await _stir.ReadMicrostepAsync();
        if (m is { } n && n >= 200) Settings.StirMicrostep = n;
    }

    /// <summary>一键清除搅拌电机报警。</summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task ClearStirFault()
    {
        await _stir.ClearAlarmAsync();
        await RefreshStirFaultAsync();
        Toast(StirHasFault ? "warn" : "ok", StirHasFault ? $"报警仍在：{StirFaultText}（过流需排查后才能清）" : "已清除搅拌电机报警");
    }

    /// <summary>设置细分（指令脉冲数/转），写入 DM2C 并存 EEPROM；运行中改则重新触发使其生效。</summary>
    public async System.Threading.Tasks.Task ApplyStirMicrostepAsync(double ppr)
    {
        Settings.StirMicrostep = ppr;
        var v = await _stir.SetMicrostepAsync((int)ppr);
        if (v is { } n)
        {
            if (StirOn) _ = _stir.SetRpmAsync(StirRpm);   // 让新细分立即生效
            Toast("ok", $"细分已设为 {n} 脉冲/转 并保存");
        }
    }

    public string StirStateText => StirOn ? "运行中" : "已停止";

    /// <summary>是否有需要播放的动画（气路流动 / 桨叶旋转）。空闲时为 false，让动画停下省 CPU。</summary>
    public bool AnimActive =>
        Reactors.Any(r => r.Valve && (r.State == ReactorState.React || r.State == ReactorState.Pressing))
        || (StirOn && Reactors.Any(r => r.IsRunning));

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

    /// <summary>设置搅拌电机峰值电流（A），写入 DM2C 并存 EEPROM；反馈实际生效值（驱动器硬夹 0.3–3.2A）。</summary>
    public async System.Threading.Tasks.Task ApplyStirCurrentAsync(double amps)
    {
        Settings.StirCurrent = amps;
        var a = await _stir.SetCurrentAsync(amps);
        if (a is { } v) { StirMotorCurrent = v; Toast("ok", $"搅拌电机峰值电流已设为 {v:0.0} A 并保存"); }
    }

    /// <summary>设置搅拌待机电流百分比（0–100），写入 DM2C 并存 EEPROM。</summary>
    public async System.Threading.Tasks.Task ApplyStirStandbyPctAsync(int pct)
    {
        Settings.StirStandbyPct = pct;
        var p = await _stir.SetStandbyPctAsync(pct);
        if (p is { } v) Toast("ok", $"搅拌待机电流已设为 {v}% 并保存");
    }

    /// <summary>设置搅拌起停平缓度（加减速时间 ms/1000rpm）：运行中改则立即以新斜率重新下发。</summary>
    public void ApplyStirRampMs(double ms)
    {
        Settings.StirRampMs = ms;
        _stir.RampMs = (int)ms;
        if (StirOn) _ = _stir.SetRpmAsync(StirRpm);   // 重新触发速度路径，让新加减速立即生效
        Toast("ok", $"搅拌加减速时间已设为 {ms:0} ms/1000rpm");
    }

    /// <summary>设置压力变送器满量程（MPa）：即时生效到 8AI 换算，压力读数随之修正。</summary>
    public void ApplyPressFullScaleMPa(double mpa)
    {
        Settings.PressFullScaleMPa = mpa;
        _press.FullScalePsi = Models.Units.MpaToPsi(mpa);
        RefreshSchematic();
        Toast("ok", $"压力变送器满量程已设为 {mpa:0.###} MPa");
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
            new Hardware.StepperDriver(_bus, Services.HardwareOptions.StirSlave));
        _stir.CommError += msg => Toast("err", $"搅拌通讯失败 · {msg}");
        _stir.RampMs = (int)Settings.StirRampMs;    // 起停平缓度（加减速时间）
        StirMotorCurrent = Settings.StirCurrent;   // 先用设置值占位，随后从驱动器读回真实设定
        _ = RefreshStirCurrentAsync();
        _ = RefreshStirFaultAsync();
        _ = InitStirMicrostepAsync();

        // 温控服务（一台 AI-8，1 拖 8）。开机读一次各通道 PID 填充界面。
        Temp = new Services.TempService(_tempBus,
            Services.HardwareOptions.TempSlave, Services.HardwareOptions.TempDpt);
        _ = Temp.InitAsync();

        // 压力服务（8AI）。用当前内部 psi 满程回填设置页的 MPa 量程显示值。
        _press = new Services.PressureService(_pressBus, Services.HardwareOptions.AnalogSlave,
            Services.HardwareOptions.PressFullScale, Services.HardwareOptions.Press4to20);
        Settings.PressFullScaleMPa = System.Math.Round(Models.Units.PsiToMpa(_press.FullScalePsi), 3);

        // 阀门服务（IO16R）。真机的状态读回放到 SeedData 之后（见下方真机初始态）。
        _valves = new Services.ValveService(_ioBus, Services.HardwareOptions.IoSlave, Services.HardwareOptions.IoEnergizeOpens);

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

        // 压力单位切换（psi/bar）：刷新各釜压力显示 + 气路图（内部仍以 psi 存储/判定）
        Models.Units.Changed += OnUnitsChanged;

        // 真机模式：开机进入"实时查询"初始态 —— 不自动开搅拌、所有釜置为空闲（温/压/阀门由轮询与读回填充），
        // 不沿用演示用的"全部运行"假数据。
        if (Services.HardwareOptions.AnyReal)
        {
            StirOn = false;
            foreach (var c in Reactors)
            {
                c.State = ReactorState.Idle;
                c.Rpm = 0;
                c.T = 0; c.P = 0; c.Gas = 0;   // 清演示假值；由轮询填充真实温压（没读到则显示 0）
            }
            UpdateRunCount();
            if (RealIo) _ = SyncValvesFromHardware();   // 读回真实阀门状态，覆盖空闲默认
        }

        // 演示模式：初始 StirOn=true（字段初始化）不触发属性回调，这里显式启动搅拌。真机模式上面已置 false。
        if (StirOn) _ = _stir.StartAsync(StirRpm);

        // 提示当前搅拌运行在真机还是模拟（开机一次）。延后到 View 订阅 Toast 之后再发。
        var (k, m) = Services.HardwareOptions.UseRealStir
            ? ("ok", $"搅拌已连接真机 · {Services.HardwareOptions.StirPort} @ {Services.HardwareOptions.StirBaud} · 站{Services.HardwareOptions.StirSlave}")
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

    /// <summary>安全写温度 SP 到 AI-8（定值编辑与曲线引擎共用）。返回是否写成功，失败由调用方决定重试。</summary>
    public async System.Threading.Tasks.Task<bool> WriteTempSpSafeAsync(int ch, double sp)
    {
        try { await Temp.SetSetpointAsync(ch, sp); return true; }
        catch { return false; /* 串口异常：曲线引擎靠"成功才标记"机制在下个周期重试 */ }
    }

    /// <summary>曲线引擎的一次 SP 下发：仅在写成功后标记去抖状态，失败下个周期重试。</summary>
    private async System.Threading.Tasks.Task WriteCurveSpAsync(Models.Reactor c, double target)
    {
        if (await WriteTempSpSafeAsync(c.Id, target))
            c.Profile.MarkWritten(target);
    }

    /// <summary>压力单位切换后：刷新每个釜的压力显示绑定 + 气路图读数。</summary>
    private void OnUnitsChanged()
    {
        foreach (var r in Reactors) r.RefreshUnits();
        RefreshSchematic();
    }
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
        if (RealIo) _ = _valves.SetReactorValveAsync(id, c.Valve);   // 写线圈
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
        if (RealIo) _ = _valves.SetGasAsync(index, g.On);
        RefreshSchematic();
        Toast(g.On ? "ok" : "warn", $"{g.Label} 进气阀 {(g.On ? "已开" : "已关")}");
    }

    public void ToggleVent()
    {
        if (!IsAdmin) { Toast("err", "需要管理员权限"); return; }
        if (!ManualValve) { Toast("warn", "请先开启「手动阀控」"); return; }
        VentOn = !VentOn;
        if (RealIo) _ = _valves.SetVentAsync(VentOn);
        RefreshSchematic();
        Toast(VentOn ? "warn" : "ok", $"排空阀 {(VentOn ? "已开" : "已关")}");
    }

    /// <summary>开机从 IO16R 读回 12 路阀门实际状态，回填界面，使显示与硬件一致。</summary>
    private async System.Threading.Tasks.Task SyncValvesFromHardware()
    {
        try
        {
            var open = await _valves.ReadOpenStatesAsync();
            for (int i = 0; i < Reactors.Count && i < 8; i++) Reactors[i].Valve = open[i];
            for (int i = 0; i < GasInlets.Count && i < 3; i++) GasInlets[i].On = open[8 + i];
            VentOn = open[11];
            RefreshSchematic();
        }
        catch { /* 读取失败忽略 */ }
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
            if (RealIo) _ = _valves.SetReactorValveAsync(c.Id, false);   // 安全：停止即关进气阀
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
            desktop.Shutdown(0);   // 退出码 0：配合 systemd Restart=on-failure，正常退出不被重启
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

        // 自动套档：按各釜目标温度选用对应档位的 PID，连同 SP 一起下发到温控器
        foreach (var c in Reactors.Where(r => r.State != ReactorState.Idle))
            _ = Temp.ApplyBandForAsync(c.Id, c.TSp);

        RefreshSchematic();
        Drawer.RaiseAll();
        UpdateRunCount();
        Toast("ok", $"预运行检查通过 · 已开始运行（{Reactors.Count(r => r.State != ReactorState.Idle)} 个通道）· PID 已按温区自动套档");
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
    private int _stirFaultTick;

    public void Tick()
    {
        _ = Temp.PollAsync();   // 温控轮询：PV / 输出 / 自整定状态（mock 模式下推进 PV 漂移）

        // 搅拌电机故障/母线电压轮询（约每 3 秒，只在真机上跑）
        if (Services.HardwareOptions.UseRealStir && _stirFaultTick++ % 3 == 0)
            _ = RefreshStirFaultAsync();

        bool realT = Services.HardwareOptions.UseRealTemp;
        bool realP = Services.HardwareOptions.UseRealAnalog;
        if (realP) _ = _press.PollAsync();   // 压力轮询（8AI）

        foreach (var c in Reactors)
        {
            // 真机：温度用 AI-8 实测 PV、压力用 8AI 实测值（覆盖模拟）
            if (realT) c.T = Temp.Channels[c.Id - 1].Pv;
            if (realP) c.P = _press.Pressures[c.Id - 1];

            if (c.State is ReactorState.Idle or ReactorState.Done) continue;
            if (!realT) c.T += (Random.Shared.NextDouble() - 0.5) * 0.3;
            if (c.State == ReactorState.React)
            {
                if (!realP) c.P += (Random.Shared.NextDouble() - 0.5) * 1.2;
                c.Gas += Random.Shared.NextDouble() * 0.02;
            }
            else if (c.State == ReactorState.Pressing)
            {
                if (!realP) c.P = Math.Min(c.PSp, c.P + Random.Shared.NextDouble() * 2);
            }
            else if (c.State == ReactorState.Heating)
            {
                if (!realT) c.T = Math.Min(c.TSp, c.T + Random.Shared.NextDouble() * 0.4);
            }

            // 超压 / 超温报警判定（阈值来自全局设置）
            if (c.P >= Settings.OverPressure || c.T >= Settings.OverTemp)
                c.State = ReactorState.Alarm;
        }

        // —— 曲线升温引擎：AI-8 无原生多段程序，由上位机按分段线性插值周期下发 SP ——
        foreach (var c in Reactors)
        {
            if (c.SpMode != "curve" || !c.Profile.Running) continue;

            // 超温/超压报警：自动停止曲线，不再继续抬 SP（仪表保持最后成功下发的给定值）
            if (c.State == ReactorState.Alarm)
            {
                c.Profile.Stop();
                Toast("err", $"RV{c.Id} 报警，曲线升温已自动停止");
                continue;
            }

            double target = c.Profile.CurrentTarget();
            c.TSp = target;                       // 界面上的目标温度跟随曲线当前值
            if (c.Profile.ShouldWrite(target))
                _ = WriteCurveSpAsync(c, target);
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
        "warn" => new SolidColorBrush(Color.Parse("#c9820f")),
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
