using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Models;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

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
    // 配置了串口(PR_STIR_PORT) → 走真实 ModbusRtuMaster；否则离线（事务全部失败）。
    private readonly Hardware.IModbusMaster _bus = Services.HardwareOptions.CreateStirBus();
    private readonly Services.StirController _stir;

    // 温控：一台 AI-8（1 拖 8，RV1-8），8 通道独立 PID + 自整定。未配置串口时离线。
    private readonly Hardware.IModbusMaster _tempBus = Services.HardwareOptions.CreateTempBus();
    public Services.TempService Temp { get; }

    // 压力：艾莫迅 8AI（8 路 PT）。未配置串口时离线，压力显示「—」。
    private readonly Hardware.IModbusMaster _pressBus = Services.HardwareOptions.CreateAnalogBus();
    private readonly Services.PressureService _press;

    // 阀门：艾莫迅 IO16R（线圈驱动电磁阀）。未配置串口时阀门操作一律拒绝（界面不改阀态）。
    private readonly Hardware.IModbusMaster _ioBus = Services.HardwareOptions.CreateIoBus();
    private readonly Services.ValveService _valves;
    private static bool RealIo => Services.HardwareOptions.UseRealIo;

    /// <summary>各模块在线状态（设置页「通讯」显示用）。</summary>
    public bool TempOnline => Temp.Online;
    public bool PressOnline => _press.Online;
    public bool StirOnline => _stir.Online;
    public bool IoOnline => _valves.Online;

    [ObservableProperty] private bool _stirOn;
    [ObservableProperty] private int _stirRpm = 200;

    /// <summary>搅拌电机当前运行电流（A）——即驱动器设定的峰值电流（步进恒流，无实时负载电流反馈）。</summary>
    [ObservableProperty] private double _stirMotorCurrent;
    public string StirMotorCurrentText => StirMotorCurrent > 0 ? $"{StirMotorCurrent:0.0} A" : "—";
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
        if (Estopped) return;   // 急停：停止帧由急停流程统一下发并等待结果；锁定期间不允许启动
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

    [ObservableProperty] private string _runCountText = "0 / 8";
    [ObservableProperty] private string _clockTime = "";
    [ObservableProperty] private string _clockDate = "";

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

    private static readonly string[] Wk = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };

    public MainViewModel()
    {
        // 搅拌驱动器（DM2C，站地址来自 HardwareOptions）。未配置串口时离线，电流显示「—」。
        _stir = new Services.StirController(
            new Hardware.StepperDriver(_bus, Services.HardwareOptions.StirSlave));
        _stir.CommError += msg => Toast("err", $"搅拌通讯失败 · {msg}");
        _stir.RampMs = (int)Settings.StirRampMs;    // 起停平缓度（加减速时间）
        _ = RefreshStirCurrentAsync();             // 电流只显示从驱动器读回的值，读到之前为「—」
        _ = RefreshStirFaultAsync();
        _ = InitStirMicrostepAsync();

        // 温控服务（一台 AI-8，1 拖 8）。
        Temp = new Services.TempService(_tempBus,
            Services.HardwareOptions.TempSlave, Services.HardwareOptions.TempDpt);

        // 本地持久化：恢复全局设置、温度档位边界、8 通道 × 3 档 PID。
        // 有存档时不再用仪表 PID 作种子——仪表里残留的是上次整定值（可能属于高温档），
        // 盲写进开机默认的低温档会串档。
        var persisted = Services.SettingsStore.Load();
        if (persisted != null)
        {
            if (persisted.Settings is { } ps) Services.SettingsStore.CopySettings(ps, Settings);
            if (persisted.BandBounds is { Length: 2 } bb)
            {
                Temp.Bands[0].Hi = bb[0]; Temp.Bands[1].Lo = bb[0];
                Temp.Bands[1].Hi = bb[1]; Temp.Bands[2].Lo = bb[1];
            }
            Temp.ImportBandPids(persisted.BandPids);
        }
        Temp.PidsChanged += PersistSoon;
        _ = Temp.InitAsync(seedFromInstrument: persisted?.BandPids == null);

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

        // 开机初始态：不自动开搅拌、8 釜全部停用；温 / 压由轮询填充（模块离线时显示「—」），
        // 阀门从 IO16R 读回真实状态（读不到时界面按全关显示并提示）。
        if (RealIo) _ = SyncValvesFromHardware();

        // 开机提示哪些模块没配串口（延后到 View 订阅 Toast 之后再发）。
        var off = new List<string>();
        if (!Services.HardwareOptions.UseRealStir) off.Add("搅拌");
        if (!Services.HardwareOptions.UseRealTemp) off.Add("温控");
        if (!Services.HardwareOptions.UseRealAnalog) off.Add("压力");
        if (!RealIo) off.Add("阀门");
        var (k, m) = off.Count == 0
            ? ("ok", "4 路串口均已配置 · 正在连接各模块")
            : ("warn", $"未配置串口：{string.Join(" / ", off)} · 按离线处理（读数显示「—」，相关操作不可用）");
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Toast(k, m));
        ClockTick();
    }

    /// <summary>开机初始数据：8 个停用的反应釜（读数由轮询填充）+ 3 路气源（关）。配方、通知开机均为空。</summary>
    private void SeedData()
    {
        for (int id = 1; id <= 8; id++)
            Reactors.Add(new Reactor { Id = id, State = ReactorState.Idle, RpmSp = StirRpm });

        GasInlets.Add(new GasInlet { Label = "惰性气体" });
        GasInlets.Add(new GasInlet { Label = "气体 A" });
        GasInlets.Add(new GasInlet { Label = "气体 B" });
    }

    public Reactor? FindReactor(int id) => Reactors.FirstOrDefault(r => r.Id == id);

    public void RefreshSchematic() => SchematicInvalidated?.Invoke();

    /// <summary>安全写温度 SP 到 AI-8（定值编辑与曲线引擎共用）。返回是否写成功，失败由调用方决定重试。</summary>
    /// <param name="bandTemp">按哪个温度选 PID 档，默认同 sp（逼近中间 SP 要按最终目标选档）。</param>
    public async System.Threading.Tasks.Task<bool> WriteTempSpSafeAsync(int ch, double sp, double? bandTemp = null)
    {
        // 先按温区切换 PID 档（同档时内部直接返回，不重复写），再写 SP
        await Temp.ApplyPidForBandAsync(ch, bandTemp ?? sp);
        try { await Temp.SetSetpointAsync(ch, sp); return true; }
        catch { return false; /* 串口异常：曲线引擎靠"成功才标记"机制在下个周期重试 */ }
    }

    /// <summary>曲线引擎的一次 SP 下发：仅在写成功后标记去抖状态，失败下个周期重试。</summary>
    private async System.Threading.Tasks.Task WriteCurveSpAsync(Models.Reactor c, double target)
    {
        if (await WriteTempSpSafeAsync(c.Id, target))   // 内部已按 target 套档
            c.Profile.MarkWritten(target);
    }

    // ============ 定值加热防超调逼近（见 Models.HeatApproach）============

    /// <summary>定值模式启动加热：全链启动通道，SP 先写逼近规划的首个给定值（目标−Δ），之后由 Tick 推进。</summary>
    public void StartFixedHeat(Models.Reactor c)
    {
        double pv = c.TOnline && c.T > 1 ? c.T : 25;   // PV 无效（温控离线 / 读数为 0）按室温规划
        var a = c.Approach;
        double sp = a.Start(c.TSp, pv, DateTime.UtcNow, Settings.ApproachParams);
        a.WriteInFlight = true;
        _ = StartFixedHeatAsync(c, sp);
    }

    private async System.Threading.Tasks.Task StartFixedHeatAsync(Models.Reactor c, double sp)
    {
        try
        {
            bool ok = await Temp.StartChannelAsync(c.Id, c.TSp, sp);   // PID 按最终目标选档
            c.Approach.MarkWritten(sp, DateTime.UtcNow);       // SP 通讯失败也会在 30s 强制重写时补发
            if (!ok) ReportHeatStartFailed(c.Id);
        }
        finally { c.Approach.WriteInFlight = false; }
    }

    /// <summary>启动加热前的公共检查：null = 可以启动，否则为拒绝原因。</summary>
    public string? HeatBlockReason()
    {
        if (Estopped) return "急停状态下不可启动加热 · 请先复位";
        if (!Services.HardwareOptions.UseRealTemp) return "温控未配置串口（PR_TEMP_PORT），无法加热";
        if (!Temp.Online) return "温控离线（AI-8 通讯失败），无法启动加热";
        return null;
    }

    /// <summary>停止单通道加热（At=4）并按结果提示：写入失败时明确告知，不能让界面显示「已停止」而加热器仍在工作。</summary>
    public async System.Threading.Tasks.Task StopHeatAsync(int id, string okMsg)
    {
        if (!Services.HardwareOptions.UseRealTemp || await Temp.StopChannelAsync(id))
        {
            Toast("warn", okMsg);
            return;
        }
        Toast("err", $"RV{id} 停止加热未确认（温控通讯失败）· 请按「急停」或到设备现场确认");
        Alarm.Log("err", $"RV{id}", "停止加热未确认", "AI-8 通讯失败，加热输出可能仍在工作");
    }

    /// <summary>关闭单个 RV 进气阀（停止 / 停用通道时）：写成功才改显示，失败提示。</summary>
    public async System.Threading.Tasks.Task CloseReactorValveAsync(int id)
    {
        if (!RealIo || FindReactor(id) is not { } c) return;
        try { await _valves.SetReactorValveAsync(id, false); }
        catch
        {
            Toast("err", $"SV{id} 关闭失败（IO16R 通讯）· 阀门可能仍开启");
            return;
        }
        c.Valve = false;
        RefreshSchematic();
        Drawer.RaiseAll();
    }

    /// <summary>
    /// 启动加热的 Srun=0 / At=0 没写进去：之后的 SP 重写不会让仪表开始出力，必须明确提示，
    /// 否则界面显示「升温中」而加热器根本没工作。急停锁定导致的拒绝不提示（急停面板已说明）。
    /// </summary>
    public void ReportHeatStartFailed(int id)
    {
        if (Estopped) return;
        Toast("err", $"RV{id} 启动加热失败（温控通讯）· 请检查温控后重新启动");
        Alarm.Log("err", $"RV{id}", "启动加热失败", "AI-8 通讯失败，加热输出未启动");
    }

    private async System.Threading.Tasks.Task StartBandHeatAsync(int id, double sp)
    {
        if (!await Temp.ApplyBandForAsync(id, sp)) ReportHeatStartFailed(id);
    }

    /// <summary>定值模式修改目标温度：加热中交给逼近重新规划（下个 Tick 下发）；未加热时照旧直接写 SP。</summary>
    public void SetFixedTarget(Models.Reactor c)
    {
        if (c.Approach.Active) c.Approach.Retarget(c.TSp, c.T, DateTime.UtcNow, Settings.ApproachParams);
        else _ = WriteTempSpSafeAsync(c.Id, c.TSp);
    }

    private async System.Threading.Tasks.Task WriteApproachSpAsync(Models.Reactor c)
    {
        var a = c.Approach;
        double sp = a.SpToWrite;
        a.WriteInFlight = true;
        try
        {
            if (await WriteTempSpSafeAsync(c.Id, sp, a.Target))
                a.MarkWritten(sp, DateTime.UtcNow);
        }
        finally { a.WriteInFlight = false; }
    }

    /// <summary>每个 Tick 推进各通道逼近，并处理放热事件（提示 + 写入报警与事件记录）。</summary>
    private void StepApproaches(bool realT)
    {
        var now = DateTime.UtcNow;
        var ap = Settings.ApproachParams;
        foreach (var c in Reactors)
        {
            var a = c.Approach;
            if (!a.Active) continue;
            if (c.SpMode != "fixed") { a.Stop(); continue; }

            // 超温/超压报警：停止上推（仪表保持最后成功下发的给定值），与曲线引擎一致
            if (c.State == ReactorState.Alarm)
            {
                a.Stop();
                Toast("err", $"RV{c.Id} 报警，防超调逼近已停止 · 保持当前给定值");
                continue;
            }

            // 目标被其他途径改了（配方、复制配置等）：重新规划
            if (Math.Abs(c.TSp - a.Target) > 1e-6) a.Retarget(c.TSp, c.T, now, ap);

            // 温控离线：PV / 输出都是旧值，不能拿来判预热完成或放热，等通讯恢复再推进
            if (!realT) continue;
            double op = Temp.Channels[c.Id - 1].OutputPct;
            switch (a.Step(now, c.T, op, ap))
            {
                case ApproachEvent.PreheatDone:
                    Alarm.Log("info", $"RV{c.Id}", $"预热完成 · 实测冲温 {a.PreheatOvershoot:0.0}℃",
                        $"开始逼近 {a.CurrentSp:0.0} → {a.Target:0.0}℃ · 当前预留温差 Δ={ap.Delta:0.#}℃");
                    break;
                case ApproachEvent.ExoPause:
                    Toast("warn", $"RV{c.Id} 检测到放热（输出≈0 仍在升温）· 暂停上推");
                    Alarm.Log("info", $"RV{c.Id}", "逼近中检测到放热，暂停上推",
                        $"PV {c.T:0.0}℃ / 当前 SP {a.CurrentSp:0.0}℃ / 目标 {a.Target:0.0}℃");
                    break;
                case ApproachEvent.OverLimit:
                    Toast("err", $"RV{c.Id} 放热超出控温能力 · 输出已为 0，温度仍高于目标 +{HeatApproach.OverLimit}℃");
                    Alarm.Log("warn", $"RV{c.Id}", "放热超出控温能力",
                        $"PV {c.T:0.0}℃ / 目标 {a.Target:0.0}℃ · 加热输出已为 0（无冷却，无法压回）");
                    break;
            }

            if (a.ShouldWrite(now)) _ = WriteApproachSpAsync(c);
        }
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
        if (GasLockedForManual()) return;
        if (!IsAdmin) { Toast("err", "手动阀控需要管理员权限"); return; }
        if (!ManualValve) { Toast("warn", "请先打开 RV1 左侧的「手动阀控」开关"); return; }
        if (!c.Valve && c.State is ReactorState.Pressing or ReactorState.React or ReactorState.Alarm)
        {
            Toast("err", $"加压/反应中禁止手动开阀（RV{id}）");
            return;
        }
        bool open = !c.Valve;
        _ = ManualValveAsync($"SV{id}", open, () => _valves.SetReactorValveAsync(id, open), () =>
        {
            c.Valve = open;
            Drawer.RaiseAll();
        });
    }

    public void ToggleGas(int index)
    {
        if (GasLockedForManual()) return;
        if (!IsAdmin) { Toast("err", "需要管理员权限"); return; }
        if (!ManualValve) { Toast("warn", "请先开启「手动阀控」"); return; }
        var g = GasInlets[index];
        bool open = !g.On;
        _ = ManualValveAsync($"{g.Label} 进气阀", open, () => _valves.SetGasAsync(index, open), () => g.On = open);
    }

    public void ToggleVent()
    {
        if (GasLockedForManual()) return;
        if (!IsAdmin) { Toast("err", "需要管理员权限"); return; }
        if (!ManualValve) { Toast("warn", "请先开启「手动阀控」"); return; }
        bool open = !VentOn;
        _ = ManualValveAsync("排空阀", open, () => _valves.SetVentAsync(open), () => VentOn = open, openKind: "warn");
    }

    private bool _manualValveBusy;   // 上一次手动阀写入还没回来时不接受新操作，避免写入顺序错乱

    /// <summary>
    /// 手动阀操作：先写线圈，写成功后才改界面（界面只显示确认过的阀态）。
    /// 阀门未配置串口时直接拒绝；写入期间按下急停，写完后立即补关一次。
    /// </summary>
    private async Task ManualValveAsync(string name, bool open, Func<Task> write, Action show, string openKind = "ok")
    {
        if (!RealIo) { Toast("err", "阀门模块未配置串口（PR_IO_PORT），无法操作"); return; }
        if (_manualValveBusy) { Toast("warn", "上一个阀门操作还在进行，请稍候"); return; }
        _manualValveBusy = true;
        try
        {
            try { await write(); }
            catch { Toast("err", $"{name}{(open ? "打开" : "关闭")}失败（IO16R 通讯）· 阀门状态未改变"); return; }
            if (Estopped)
            {
                // 写入途中按了急停：这一帧可能落在急停的全关之后，再关一次
                await CloseAllValvesAsync();
                return;
            }
            show();
            RefreshSchematic();
            Toast(open ? openKind : "ok", $"{name} {(open ? "已开" : "已关")}");
        }
        finally { _manualValveBusy = false; }
    }

    /// <summary>开机从 IO16R 读回 12 路阀门实际状态，回填界面，使显示与硬件一致。读不到时界面保持全关并提示。</summary>
    private async System.Threading.Tasks.Task SyncValvesFromHardware()
    {
        try
        {
            var open = await _valves.ReadOpenStatesAsync();
            for (int i = 0; i < Reactors.Count && i < 8; i++) Reactors[i].Valve = open[i];
            for (int i = 0; i < GasInlets.Count && i < 3; i++) GasInlets[i].On = open[8 + i];
            VentOn = open[11];
            RefreshSchematic();
            if (open.Any(o => o)) Toast("warn", "开机读回：有阀门处于开启状态（见气路图）");
        }
        catch { Toast("err", "开机读取阀门状态失败（IO16R 通讯）· 气路图按全关显示，实际状态未知"); }
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
        if (!StirOn && Estopped) { Toast("err", "急停状态下不可启动搅拌 · 请先复位"); return; }
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
        if (GasLockedForManual()) return;
        if (!IsAdmin) { Toast("err", "手动阀控需要管理员权限"); return; }
        if (!ManualValve && !RealIo) { Toast("err", "阀门模块未配置串口（PR_IO_PORT），无法手动阀控"); return; }
        ManualValve = !ManualValve;
        RefreshSchematic();   // 开关画在气路图里，立即重绘
        Toast(ManualValve ? "warn" : "ok",
            ManualValve ? "手动阀控已开启 · 点击阀门可手动操作" : "手动阀控已关闭");
    }

    [RelayCommand]
    private async Task StopAll()
    {
        bool gas = GasBusy;
        AbortGas();   // 吹扫 / 泄漏测试：序列收到取消后自己也会全关阀
        Program.AbortProgram();   // 有程序在执行也一并中止（含后台稳压回路）
        foreach (var c in Reactors)
        {
            if (c.Profile.Running) c.Profile.Stop();
            c.Approach.Stop();
            if (c.State != ReactorState.Idle) c.State = ReactorState.Done;
            c.Rpm = 0;
        }
        RefreshSchematic();
        Drawer.RaiseAll();
        UpdateRunCount();

        // 温控与阀门是两条独立总线：并行下发（停全部温控输出 逐路 At=4 + 全关 12 路阀），再按结果提示
        var heat = Services.HardwareOptions.UseRealTemp ? Temp.StopAllChannelsAsync() : Task.FromResult(true);
        var valves = RealIo ? CloseAllValvesAsync() : Task.FromResult(true);   // 失败时 CloseAllValvesAsync 自己提示
        bool heatOk = await heat, valvesOk = await valves;

        if (!heatOk)
        {
            Toast("err", "停止加热输出未确认（AI-8 通讯失败）· 请按「急停」或到设备现场确认");
            Alarm.Log("err", "系统", "全部停止 · 停止加热未确认", "AI-8 通讯失败，加热输出可能仍在工作");
        }
        else if (valvesOk)
            Toast("warn", gas ? "已停止全部通道并中止气路序列 · 阀门全关" : "已停止全部通道（含加热输出）· 阀门全关");
    }

    [RelayCommand]
    private void RunAll()
    {
        if (Estopped) { Toast("err", "急停状态下不可启动 · 请先复位"); return; }
        if (GasBusy) { Toast("warn", $"{GasTask}进行中 · 请等待完成或先停止"); return; }
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

    /// <summary>持久化快照：全局设置 + 档位边界 + 8×3 档 PID。</summary>
    private Services.SettingsStore.PersistedState BuildPersistState() => new()
    {
        Settings = Settings,
        BandBounds = new[] { Temp.Bands[0].Hi, Temp.Bands[1].Hi },
        BandPids = Temp.ExportBandPids(),
    };

    /// <summary>防抖持久化（整定完成/手改 PID/切档/改档位边界时调用）。</summary>
    public void PersistSoon() => Services.SettingsStore.SaveSoon(BuildPersistState);

    [RelayCommand]
    private async Task ConfirmExit()
    {
        IsExitConfirmOpen = false;
        if (GasBusy) await AbortGasAndWaitAsync();   // 吹扫 / 泄漏测试中退出：等序列收尾、全关阀后再退出
        Services.SettingsStore.SaveNow(BuildPersistState());   // 设置页的修改统一在退出时落盘

        // 退出前停搅拌、停全部温控输出、全关阀门（三条总线并行，等写完再退出，避免退出后加热器 / 电机 / 气源仍在工作）
        var stir = _stir.StopAsync();
        var heat = Temp.StopAllChannelsAsync();
        var valves = RealIo ? CloseAllValvesAsync(report: false) : Task.FromResult(true);
        await Task.WhenAll(stir, heat, valves);

        if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown(0);   // 退出码 0：配合 systemd Restart=on-failure，正常退出不被重启
        else
            Environment.Exit(0);
    }

    [RelayCommand]
    private void ConfirmRun()
    {
        if (Estopped) { IsPreRunOpen = false; Toast("err", "急停状态下不可启动 · 请先复位"); return; }
        BuildPreRun();   // 弹窗打开期间模块可能掉线：确认时按最新状态重判
        if (!PreRunCanRun) { Toast("err", "存在未处理的异常项，无法开始运行"); return; }
        IsPreRunOpen = false;

        // 开始运行即下发加热：已结束 / 已启用（待加热）的通道转入「升温中」（停用通道不参与）。
        // 阀门不在这里动：本轮运行不使用气路
        foreach (var c in Reactors)
            if (c.State is ReactorState.Done or ReactorState.Ready) c.State = ReactorState.Heating;

        // 开始运行：开启全局搅拌并同步桨叶动画（搅拌未配置串口时跳过，预运行检查里已提示）
        if (!StirOn && Services.HardwareOptions.UseRealStir) StirOn = true;   // setter 会触发 ApplyStir
        else ApplyStir();

        // 自动套档：按各釜目标温度选用对应档位的 PID，连同 SP 一起下发到温控器。
        // 定值模式走防超调逼近（先写 目标−Δ，之后由 Tick 推进）
        foreach (var c in Reactors.Where(r => r.State != ReactorState.Idle))
        {
            if (c.SpMode == "fixed" && c.TSp >= 35) StartFixedHeat(c);
            else _ = StartBandHeatAsync(c.Id, c.TSp);
        }

        RefreshSchematic();
        Drawer.RaiseAll();
        UpdateRunCount();
        Toast("ok", $"预运行检查通过 · 已开始运行（{Reactors.Count(r => r.State != ReactorState.Idle)} 个通道）· PID 已按温区自动套档");
    }

    /// <summary>按当前设备状态生成预运行检查项。</summary>
    private void BuildPreRun()
    {
        PreRunChecks.Clear();

        var hw = Services.HardwareOptions.UseRealTemp;   // 仅为缩短下面的判断

        // 急停（软件急停；硬件急停回路未接入软件，无法读取）
        PreRunChecks.Add(Estopped
            ? PreRunCheck.Err("急停", "急停已触发，必须先复位才能运行")
            : PreRunCheck.Ok("急停", "软件急停未触发"));

        // 温控：开始运行要下发加热，温控必须在线
        PreRunChecks.Add(!hw
            ? PreRunCheck.Err("温控", "温控未配置串口（PR_TEMP_PORT），无法加热")
            : !Temp.Online
                ? PreRunCheck.Err("温控", "温控离线（AI-8 通讯失败），请检查接线与串口")
                : PreRunCheck.Ok("温控", "AI-8 在线"));

        // 压力：没有压力读数就无法做超压监视，不允许运行
        var hot = Reactors.Where(r => r.State != ReactorState.Idle && r.P >= Settings.OverPressure).ToList();
        PreRunChecks.Add(!Services.HardwareOptions.UseRealAnalog
            ? PreRunCheck.Err("压力 / 超压监视", "压力采集未配置串口（PR_PRESS_PORT），无法监视超压")
            : !_press.Online
                ? PreRunCheck.Err("压力 / 超压监视", "压力采集离线（8AI 通讯失败），无法监视超压")
                : hot.Count > 0
                    ? PreRunCheck.Err("压力 / 超压监视", $"{string.Join("、", hot.Select(r => "RV" + r.Id))} 压力不低于超压报警值，需先泄压处理")
                    : PreRunCheck.Ok("压力 / 超压监视", $"8 路在线 · 均低于超压报警值 {Units.FormatP(Settings.OverPressure)}"));

        // 阀门：本轮运行不使用气路；只核对排空阀与手动模式
        PreRunChecks.Add(!RealIo
            ? PreRunCheck.Warn("阀门", "阀门未配置串口（PR_IO_PORT），无法确认阀门状态")
            : !_valves.Online
                ? PreRunCheck.Warn("阀门", "阀门模块离线，无法确认阀门状态")
                : VentOn
                    ? PreRunCheck.Err("阀门", "排空阀处于开启状态，运行前必须关闭")
                    : PreRunCheck.Ok("阀门", "IO16R 在线 · 排空阀已关闭"));

        // 控制模式
        PreRunChecks.Add(ManualValve
            ? PreRunCheck.Warn("控制模式", "手动阀控仍开启，自动运行时建议关闭")
            : PreRunCheck.Ok("控制模式", "自动模式，手动阀控已关闭"));

        // 搅拌器（全局共用）：开始运行会自动开启搅拌
        PreRunChecks.Add(!Services.HardwareOptions.UseRealStir
            ? PreRunCheck.Warn("搅拌器", "搅拌未配置串口（PR_STIR_PORT），本次运行不搅拌")
            : !_stir.Online
                ? PreRunCheck.Warn("搅拌器", "搅拌驱动器离线，开始运行后可能无法搅拌")
                : PreRunCheck.Ok("搅拌器", $"在线 · 开始后以 {StirRpm} rpm 运转（全部反应釜共用）"));

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

    /// <summary>底栏「← 退出」：未启用登录，按退出程序处理（弹确认框）。</summary>
    [RelayCommand]
    private void Logout() => OpenExitConfirm();

    [RelayCommand]
    private void SwitchTabCmd(string id) => SwitchTab(id);

    public void SwitchTab(string id)
    {
        ActiveTab = id;
        TabSwitched?.Invoke(id);
    }

    // —— 工具栏联动：运行中只显示「全部停止」，否则只显示「开始运行」——
    public bool AnyRunning => Reactors.Any(r => r.IsRunning) || GasBusy;
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
        _ = Temp.PollAsync();    // 温控轮询：PV / 输出 / 自整定状态
        _ = _press.PollAsync();  // 压力轮询（8AI）

        // 搅拌电机故障/母线电压轮询（约每 3 秒，只在配置了串口时跑）
        if (Services.HardwareOptions.UseRealStir && _stirFaultTick++ % 3 == 0)
            _ = RefreshStirFaultAsync();

        bool realT = Temp.Online;
        bool realP = _press.Online;

        foreach (var c in Reactors)
        {
            // 温度 = AI-8 实测 PV，压力 = 8AI 实测值；模块离线时读数标记无效（显示「—」），不参与报警判定
            c.TOnline = realT;
            c.POnline = realP;
            if (realT) c.T = Temp.Channels[c.Id - 1].Pv;
            if (realP) c.P = _press.Pressures[c.Id - 1];

            if (c.State is ReactorState.Idle or ReactorState.Done) continue;

            // 超压 / 超温报警判定（阈值来自全局设置）
            if ((realP && c.P >= Settings.OverPressure) || (realT && c.T >= Settings.OverTemp))
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

        // —— 定值加热防超调逼近 ——
        StepApproaches(realT);

        RefreshSchematic();
        if (Drawer.IsOpen) Drawer.RaiseAll();
        Graph.AppendSample();
        EstopRetryTick();   // 急停锁定中：重发未确认的停止指令
    }

    public void ClockTick()
    {
        var now = DateTime.Now;   // 系统时间（板子需联网对时或手动设置 RTC）
        ClockTime = now.ToString("HH:mm:ss");
        ClockDate = now.ToString("yyyy-MM-dd") + " " + Wk[(int)now.DayOfWeek];
        Setting.RefreshNetworkInfo();   // 周期刷新设置页的本机 IP（内部已节流约 5s）
        Setting.RefreshCommStatus();    // 通讯页模块在线状态 + 运行时长
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
