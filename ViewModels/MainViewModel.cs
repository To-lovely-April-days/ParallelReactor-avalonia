using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Commands;
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
    internal readonly Services.StirController _stir;

    // 温控：一台 AI-8（1 拖 8，RV1-8），8 通道独立 PID + 自整定。未配置串口时离线。
    private readonly Hardware.IModbusMaster _tempBus = Services.HardwareOptions.CreateTempBus();
    public Services.TempService Temp { get; }

    // 压力：艾莫迅 8AI（8 路 PT）。未配置串口时离线，压力显示「—」。
    private readonly Hardware.IModbusMaster _pressBus = Services.HardwareOptions.CreateAnalogBus();
    internal readonly Services.PressureService _press;

    // 阀门：艾莫迅 IO16R（线圈驱动电磁阀）。未配置串口时阀门操作一律拒绝（界面不改阀态）。
    private readonly Hardware.IModbusMaster _ioBus = Services.HardwareOptions.CreateIoBus();
    internal readonly Services.ValveService _valves;
    internal static bool RealIo => Services.HardwareOptions.UseRealIo;

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
        if (c is { } code) { _stirAlarmCode = code; StirFaultText = StirFaultName(code); }
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
    [WriteEntry(nameof(OperatorCommands.TryClearStirFault))]
    private Task ClearStirFault() => LocalAsync(Ops.TryClearStirFault(Src));

    // 设置页的搅拌电机电流 / 待机 / 加减速 / 细分、变送器满量程：写入逻辑在 OperatorCommands.Settings（TrySetSetting）。

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

    /// <summary>程序之间的搅拌引用计数（全机一台搅拌器；最后一个释放者才停，见 <see cref="Models.StirShare"/>）。操作员手动开停不经这里。</summary>
    internal readonly StirShare StirShare = new();

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
    internal void ApplyStir()
    {
        foreach (var c in Reactors)
            c.Rpm = (StirOn && c.IsRunning) ? StirRpm : 0;
        RefreshSchematic();
        if (Drawer.IsOpen) Drawer.RaiseAll();
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
    // Toast 信号：控制循环早于 View 订阅就开始运行（C1）。没有订阅者时先暂存（最多 20 条），
    // 第一个订阅者挂上时依次补发，避免开机 / View 卸载（MainView 卸载时退订）期间的提示丢失。
    // 开机遮罩期间到达的提示由 MainView 自己留到遮罩收起后再显示。
    private Action<string, string>? _toastHandlers;
    private readonly Queue<(string kind, string msg)> _toastBacklog = new();
    private const int ToastBacklogMax = 20;
    public event Action<string, string>? ToastRequested
    {
        add
        {
            bool first = _toastHandlers == null;
            _toastHandlers += value;
            if (!first || value == null) return;
            while (_toastBacklog.Count > 0)
            {
                var (k, m) = _toastBacklog.Dequeue();
                value(k, m);
            }
        }
        remove => _toastHandlers -= value;
    }
    // Tab 切换信号
    public event Action<string>? TabSwitched;

    private static readonly string[] Wk = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };

    /// <summary>模拟模式（PR_SIM=1）：顶栏常驻标识。</summary>
    public bool IsSim => Services.HardwareOptions.Sim;

    public MainViewModel()
    {
        // 数据记录最先建：之后所有事件（含 AlarmViewModel 的每条记录）都会落盘
        // 事件存储（带 seq 的 JSONL + 内存环，对外接口 /events 与 WS 重同步的来源）挂在 DataLogger.Event 后面：三个事件入口都经它
        var dataDir = Services.DataLogger.DefaultDirFor(Services.HardwareOptions.Sim);
        Logger = new Services.DataLogger(dataDir, OnLoggerError, new Services.EventStore(dataDir, OnLoggerError));
        // systemd 停服务（SIGTERM）/ 其他非「退出程序」路径：进程退出前尽力写完队列
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Logger.Stop(TimeSpan.FromSeconds(2));
        // 急停锁存：数据目录下 estop.latch（模拟 estop-sim.latch，PR_DATA_DIR 下真机 / 模拟共用目录时也互不影响）
        _estopLatch = new Services.EstopLatch(Services.EstopLatch.PathFor(Logger.Dir, Services.HardwareOptions.Sim));

        // 搅拌驱动器（DM2C，站地址来自 HardwareOptions）。未配置串口时离线，电流显示「—」。
        _stir = new Services.StirController(
            new Hardware.StepperDriver(_bus, Services.HardwareOptions.StirSlave),
            new Hardware.StepperDriver(Hardware.ModbusMasterExtensions.AsUrgent(_bus), Services.HardwareOptions.StirSlave));
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
        Alarm = new AlarmViewModel(Logger) { Main = this };
        Setting = new SettingViewModel(this);
        Leak = new LeakViewModel(this);
        RecipePicker = new RecipeViewModel(this);
        Ops = new OperatorCommands(this, Arbiter);
        Keyboard.ResultSink = Local;   // 键盘确认回调返回 OpResult → 统一转 Toast
        InitControl();                 // 远程控制租约：顶栏状态 / 申请弹窗订阅（MainViewModel.Control.cs）
        InitSessions();                // 会话 manifest：开机把上次未结束的会话标为 interrupted（MainViewModel.Logging.cs）

        // 压力单位切换（psi/bar）：刷新各釜压力显示 + 气路图（内部仍以 psi 存储/判定）
        Models.Units.Changed += OnUnitsChanged;


        // 开机提示哪些模块没配串口（延后到 View 订阅 Toast 之后再发）。
        var off = new List<string>();
        if (!Services.HardwareOptions.UseRealStir) off.Add("搅拌");
        if (!Services.HardwareOptions.UseRealTemp) off.Add("温控");
        if (!Services.HardwareOptions.UseRealAnalog) off.Add("压力");
        if (!RealIo) off.Add("阀门");
        var (k, m) = Services.HardwareOptions.Sim
            ? ("err", "模拟模式（PR_SIM=1）· 温控 / 压力 / 阀门 / 搅拌均为内存模拟，读数非真实设备，不控制任何硬件")
            : off.Count == 0
            ? ("ok", "4 路串口均已配置 · 正在连接各模块")
            : ("warn", $"未配置串口：{string.Join(" / ", off)} · 按离线处理（读数显示「—」，相关操作不可用）");
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Toast(k, m));
        Alarm.Log("info", "系统", "程序启动",
            $"数据目录 {Logger.Dir}" + (off.Count > 0 ? $" · 未配置串口：{string.Join(" / ", off)}" : ""));
        if (Services.HardwareOptions.Sim)
            Alarm.Log("err", "系统", "模拟模式", "PR_SIM=1：全部模块为内存模拟，读数非真实设备，急停 / 超温切断不作用于任何硬件");
        if (Services.HardwareOptions.SimRefused)
        {
            Alarm.Log("err", "系统", "已拒绝模拟模式", "PR_SIM=1 与真机串口（PR_*_PORT）同时设置：按真机串口运行，请从启动脚本中删除 PR_SIM");
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Toast("err", "PR_SIM=1 与真机串口同时设置 · 已忽略模拟，按真机运行"));
        }

        // 上次急停未复位（锁存文件仍在）：开机直接进入急停锁定，窗口起来后下发一次停止（C23）。
        // 必须在读回阀态之前：锁定中不把旧阀态回填界面
        RestoreEstopLatch();

        // 开机初始态：不自动开搅拌、8 釜全部停用；温 / 压由轮询填充（模块离线时显示「—」），
        // 阀门从 IO16R 读回真实状态（读不到时界面按全关显示并提示）。
        if (RealIo) _ = SyncValvesFromHardware();
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
    /// <param name="id">要启动的通道（1 起）；给出时该通道超温切断未解除则拒绝。</param>
    public string? HeatBlockReason(int? id = null) => HeatBlock(id)?.Message;

    /// <summary>同 <see cref="HeatBlockReason"/>，带 §11 错误码（命令层用，见 <see cref="HeatGuard"/>）。</summary>
    public (string Code, string Message)? HeatBlock(int? id = null) => HeatGuard.Check(new HeatGuardInput
    {
        Id = id,
        EstopLocked = EstopLocked,
        TempConfigured = Services.HardwareOptions.UseRealTemp,
        TempOnline = Temp.Online,
        OverTempTripped = id is { } n && IsOverTempTripped(n),
        OverPressureTripped = id is { } m && IsOverPressureTripped(m),
        StopHeatOnOverPressure = Settings.StopHeatOnOverPressure,
        OverTemp = Settings.OverTemp,
        OverPressure = Settings.OverPressure,
    });

    /// <summary>停止单通道加热（At=4）并按结果提示：写入失败时明确告知，不能让界面显示「已停止」而加热器仍在工作。
    /// 程序引擎 / Tick 等非命令路径用；命令层用 <see cref="StopHeatCoreAsync"/>（不弹 Toast，结果由返回值表达）。</summary>
    public async System.Threading.Tasks.Task StopHeatAsync(int id, string okMsg)
        => Show(await StopHeatCoreAsync(id, okMsg));

    /// <summary>停止单通道加热（At=4），不弹 Toast：成功为 Done(okMsg, warn)，失败为 HARDWARE_WRITE_FAILED 并记事件。</summary>
    internal async Task<OpResult> StopHeatCoreAsync(int id, string okMsg)
    {
        if (!Services.HardwareOptions.UseRealTemp || await Temp.StopChannelAsync(id))
            return OpResult.Done(okMsg, "warn");
        Alarm.Log("err", $"RV{id}", "停止加热未确认", "AI-8 通讯失败，加热输出可能仍在工作");
        return OpResult.Fail(OpCodes.HardwareWriteFailed,
            $"RV{id} 停止加热未确认（温控通讯失败）· 请按「急停」或到设备现场确认", "err",
            new { device = "temp", channel = id });
    }

    /// <summary>关闭单个 RV 进气阀并按结果提示（程序引擎收尾用；命令层用 <see cref="CloseReactorValveCoreAsync"/>）。</summary>
    public async System.Threading.Tasks.Task CloseReactorValveAsync(int id)
        => Show(await CloseReactorValveCoreAsync(id));

    /// <summary>关闭单个 RV 进气阀（停止 / 停用通道时）：写成功才改显示，不弹 Toast；失败为 HARDWARE_WRITE_FAILED 并记事件。</summary>
    internal async Task<OpResult> CloseReactorValveCoreAsync(int id)
    {
        if (!RealIo || FindReactor(id) is not { } c) return OpResult.Silent;
        try { await _valves.SetReactorValveAsync(id, false); }
        catch
        {
            Alarm.Log("err", $"RV{id}", $"SV{id} 关闭失败", "IO16R 通讯异常，阀门可能仍开启");
            return OpResult.Fail(OpCodes.HardwareWriteFailed, $"SV{id} 关闭失败（IO16R 通讯）· 阀门可能仍开启", "err",
                new { device = "io", valve = $"SV{id}" });
        }
        if (c.Valve) LogFileEvent("info", "阀门", $"SV{id} 关", "停止 / 停用通道时关闭进气阀");
        c.Valve = false;
        RefreshSchematic();
        Drawer.RaiseAll();
        return OpResult.Silent;
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

    internal async System.Threading.Tasks.Task StartBandHeatAsync(int id, double sp)
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
    public void Toast(string kind, string msg)
    {
        var h = _toastHandlers;
        if (h != null) { h(kind, msg); return; }
        if (_toastBacklog.Count < ToastBacklogMax) _toastBacklog.Enqueue((kind, msg));   // 保留最早的（开机提示最重要）
    }

    // ============ 命令层（C25）：触屏入口统一经 Ops.Try*，结果转成 Toast ============

    /// <summary>控制权仲裁（§5.1）。默认本地控制；切换由 <see cref="ControlLease"/>（远程申请 / 触屏允许 / 切回本地 / 心跳）驱动。</summary>
    public ControlArbiter Arbiter { get; } = new();

    /// <summary>远程控制租约状态机（§5.3，纯逻辑）：接口层申请 / 心跳 / 释放，触屏经 Ops.TryGrantControl / TryDenyControl / TryTakeBackControl。</summary>
    public ControlLease ControlLease { get; private set; } = null!;

    /// <summary>操作员命令层：触屏与将来的远程接口共用的唯一写入口。</summary>
    public OperatorCommands Ops { get; }

    /// <summary>控制循环（由 App 创建并赋值；退出前停止）。</summary>
    internal Services.ControlLoop? Loop { get; set; }

    /// <summary>停止对外接口（由 App 在启动 Kestrel 后赋值；退出前调用，Kestrel 未启用时为 null）。</summary>
    internal Action? ApiStop { get; set; }

    /// <summary>触屏入口的命令来源。</summary>
    internal static CommandSource Src => CommandSource.Local;

    /// <summary>
    /// 触屏入口统一调用：同步完成的结果当场 Toast（时序与今天相同），等待硬件的在完成后 Toast。
    /// 尾部抛出的异常转成 INTERNAL（记事件 + Toast），不留未观察的异常。
    /// </summary>
    internal void Local(Task<OpResult> t)
    {
        if (t.IsCompleted) Show(Unwrap(t));
        else _ = ShowWhenDoneAsync(t);
    }

    /// <summary>同 <see cref="Local"/>，并返回同步部分是否被接受（被拒时 false，供需要回滚控件的入口使用）。</summary>
    internal bool LocalOk(Task<OpResult> t)
    {
        if (!t.IsCompleted) { _ = ShowWhenDoneAsync(t); return true; }
        var r = Unwrap(t);
        Show(r);
        return r.Ok;
    }

    /// <summary>只读预检（<c>Ops.Check*</c>）的结果：被拒时提示并返回 false。</summary>
    internal bool LocalOk(OpResult r)
    {
        Show(r);
        return r.Ok;
    }

    /// <summary>
    /// 同 <see cref="Local"/>，并返回一个「提示完成」的 Task（不会出错）：给 async RelayCommand 用，
    /// 让按钮在硬件返回前保持禁用（AsyncRelayCommand 的防连点，与今天一致）。
    /// </summary>
    internal Task LocalAsync(Task<OpResult> t)
    {
        if (!t.IsCompleted) return ShowWhenDoneAsync(t);
        Show(Unwrap(t));
        return Task.CompletedTask;
    }

    private OpResult Unwrap(Task<OpResult> t)
        => t.IsCompletedSuccessfully ? t.Result : InternalError(t.Exception);

    private async Task ShowWhenDoneAsync(Task<OpResult> t)
    {
        OpResult r;
        try { r = await t; }
        catch (Exception ex) { r = InternalError(ex); }
        Show(r);
    }

    private OpResult InternalError(Exception? ex)
    {
        var r = OpResult.InternalOf(ex);
        Alarm.Log("err", "系统", "命令异常", r.Message["命令执行异常 · ".Length..]);
        return r;
    }

    private void Show(OpResult r)
    {
        if (!r.Quiet && r.Message.Length > 0) Toast(r.Kind, r.Message);
        foreach (var f in r.Followups) Show(f);   // 分步完成的命令：后续各步的提示按顺序补上
    }

    // ============ 阀门手动控制：气路图回调（守卫与写入在 Ops.TrySet*，只允许本地）============
    [WriteEntry(nameof(OperatorCommands.TrySetReactorValve))]
    public void ValveClicked(int id) { if (FindReactor(id) is { } c) Local(Ops.TrySetReactorValve(Src, id, !c.Valve)); }

    [WriteEntry(nameof(OperatorCommands.TrySetGasInlet))]
    public void GasClicked(int index)
    {
        if (index >= 0 && index < GasInlets.Count) Local(Ops.TrySetGasInlet(Src, index, !GasInlets[index].On));
    }

    [WriteEntry(nameof(OperatorCommands.TrySetVent))]
    public void VentClicked() => Local(Ops.TrySetVent(Src, !VentOn));

    /// <summary>开机从 IO16R 读回 12 路阀门实际状态，回填界面，使显示与硬件一致。读不到时界面保持全关并提示。</summary>
    private async System.Threading.Tasks.Task SyncValvesFromHardware()
    {
        try
        {
            var open = await _valves.ReadOpenStatesAsync();
            if (Estopped)
            {
                // 开机恢复急停锁定：读回的是全关帧之前的旧阀态，不回填界面（急停下发全关确认后界面显示全关）
                var was = Enumerable.Range(0, 12).Where(i => open[i]).Select(ValveName).ToList();
                LogFileEvent("info", "阀门", "开机读回阀态（急停锁定中，不回填）", was.Count == 0 ? "全关" : "开启：" + string.Join("、", was));
                return;
            }
            for (int i = 0; i < Reactors.Count && i < 8; i++) Reactors[i].Valve = open[i];
            for (int i = 0; i < GasInlets.Count && i < 3; i++) GasInlets[i].On = open[8 + i];
            VentOn = open[11];
            var on = Enumerable.Range(0, 12).Where(i => open[i]).Select(ValveName).ToList();
            LogFileEvent("info", "阀门", "开机读回阀态", on.Count == 0 ? "全关" : "开启：" + string.Join("、", on));
            RefreshSchematic();
            if (open.Any(o => o)) Toast("warn", "开机读回：有阀门处于开启状态（见气路图）");
        }
        catch { Toast("err", "开机读取阀门状态失败（IO16R 通讯）· 气路图按全关显示，实际状态未知"); }
    }

    [ViewEntry("打开通道抽屉")]
    public void OpenDrawer(int id) => Drawer.Open(id);

    // ============ 工具栏命令 ============
    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TrySetStir))]
    private void ToggleStir() => Local(Ops.TrySetStir(Src, !StirOn));

    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TrySetStirRpm))]
    private void EditStirRpm()
        => Keyboard.OpenNumeric("搅拌转速（全局共用）", StirRpm, "rpm", 0, Settings.RpmMax,
            v => Ops.TrySetStirRpm(Src, (int)v));

    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TrySetManualValveMode))]
    private void ToggleManual() => Local(Ops.TrySetManualValveMode(Src, !ManualValve));

    /// <summary>
    /// 全部停止：中止任何气路占用者（含泄漏测试 / 程序的吹扫、升压、稳压），
    /// 中止程序，停全部通道加热并全关阀。与泄漏测试面板 / 程序的「中止」不同，它不区分占用者（C8）。
    /// </summary>
    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TryStopAll))]
    private Task StopAll() => LocalAsync(Ops.TryStopAll(Src));

    [RelayCommand]
    [ViewEntry("打开预运行检查框（先经 Ops.CheckRun 预检；确认后由 ConfirmRun 经 TryRun 执行）")]
    private void RunAll()
    {
        if (!LocalOk(Ops.CheckRun(Src))) return;
        BuildPreRun();
        IsPreRunOpen = true;
    }

    [RelayCommand]
    [ViewEntry("关闭预运行检查框")]
    private void CancelPreRun() => IsPreRunOpen = false;

    // ============ 退出程序 ============
    /// <summary>打开退出确认弹窗（由设置页的「退出程序」触发）。</summary>
    public void OpenExitConfirm() => IsExitConfirmOpen = true;

    [RelayCommand]
    [ViewEntry("关闭退出确认框")]
    private void CancelExit() => IsExitConfirmOpen = false;

    /// <summary>持久化快照：全局设置 + 档位边界 + 8×3 档 PID。</summary>
    internal Services.SettingsStore.PersistedState BuildPersistState() => new()
    {
        Settings = Settings,
        BandBounds = new[] { Temp.Bands[0].Hi, Temp.Bands[1].Hi },
        BandPids = Temp.ExportBandPids(),
    };

    /// <summary>防抖持久化（整定完成/手改 PID/切档/改档位边界时调用）。</summary>
    public void PersistSoon() => Services.SettingsStore.SaveSoon(BuildPersistState);

    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TryExitApp))]
    private Task ConfirmExit()
    {
        IsExitConfirmOpen = false;
        return LocalAsync(Ops.TryExitApp(Src));
    }

    /// <summary>退出进程（命令层 TryExitApp 收尾完成后调用）：先停控制循环，再按生命周期退出。</summary>
    internal void ShutdownApp()
    {
        try { ApiStop?.Invoke(); } catch { /* 接口停止失败不阻塞退出 */ }
        Loop?.Stop();   // 退出前停控制循环（等待期间 Tick 照常运行，与以前一致）
        if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown(0);   // 退出码 0：配合 systemd Restart=on-failure，正常退出不被重启
        else
            Environment.Exit(0);
    }

    /// <summary>预运行检查框「确认」：经 TryRun 重判全部守卫并开始运行。预运行检查未通过时保留对话框（与今天一致）。</summary>
    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TryRun))]
    private void ConfirmRun()
    {
        var t = Ops.TryRun(Src);
        if (!(t.IsCompletedSuccessfully && t.Result is { Ok: false, Code: OpCodes.PreflightFailed })) IsPreRunOpen = false;
        Local(t);
    }

    /// <summary>开始运行筛选用的通道快照（见 <see cref="RunSelection"/>）。</summary>
    internal RunChannel RunChannelOf(Reactor r)
        => new(r.Id, r.State, IsOverTempTripped(r.Id), IsOverPressureTripped(r.Id), Program.IsProgramOwned(r.Id));

    /// <summary>按当前设备状态生成预运行检查项并写入界面（预运行确认框）。只在本地流程里调用。</summary>
    internal void BuildPreRun() => BuildPreRunAndGet();

    internal PreRunResult BuildPreRunAndGet()
    {
        var r = ComputePreRun();
        PreRunChecks.Clear();
        foreach (var c in r.Checks) PreRunChecks.Add(c);
        PreRunCanRun = r.CanRun;
        PreRunSummary = r.Summary;
        return r;
    }

    /// <summary>
    /// 预运行检查的纯计算：只读 VM 状态，不改任何界面绑定的集合 / 属性。
    /// 本地确认框经 <see cref="BuildPreRun"/> 写入界面；远程 GET /preflight（UI 线程上）直接用本方法，不会闪动操作员正打开的确认框。
    /// </summary>
    internal PreRunResult ComputePreRun()
    {
        var checks = new List<PreRunCheck>();

        var hw = Services.HardwareOptions.UseRealTemp;   // 仅为缩短下面的判断

        // 急停（软件急停；硬件急停回路未接入软件，无法读取）
        checks.Add(EstopLocked
            ? PreRunCheck.Err("急停", "急停已触发，必须先复位才能运行")
            : PreRunCheck.Ok("急停", "软件急停未触发"));

        // 温控：开始运行要下发加热，温控必须在线
        checks.Add(!hw
            ? PreRunCheck.Err("温控", "温控未配置串口（PR_TEMP_PORT），无法加热")
            : !Temp.Online
                ? PreRunCheck.Err("温控", "温控离线（AI-8 通讯失败），请检查接线与串口")
                : PreRunCheck.Ok("温控", "AI-8 在线"));

        // 压力：没有压力读数就无法做超压监视，不允许运行
        var hot = Reactors.Where(r => r.State != ReactorState.Idle && r.P >= Settings.OverPressure).ToList();
        checks.Add(!Services.HardwareOptions.UseRealAnalog
            ? PreRunCheck.Err("压力 / 超压监视", "压力采集未配置串口（PR_PRESS_PORT），无法监视超压")
            : !_press.Online
                ? PreRunCheck.Err("压力 / 超压监视", "压力采集离线（8AI 通讯失败），无法监视超压")
                : hot.Count > 0
                    ? PreRunCheck.Err("压力 / 超压监视", $"{string.Join("、", hot.Select(r => "RV" + r.Id))} 压力不低于超压报警值，需先泄压处理")
                    : PreRunCheck.Ok("压力 / 超压监视", $"8 路在线 · 均低于超压报警值 {Units.FormatP(Settings.OverPressure)}"));

        // 阀门：本轮运行不使用气路；只核对排空阀与手动模式
        checks.Add(!RealIo
            ? PreRunCheck.Warn("阀门", "阀门未配置串口（PR_IO_PORT），无法确认阀门状态")
            : !_valves.Online
                ? PreRunCheck.Warn("阀门", "阀门模块离线，无法确认阀门状态")
                : VentOn
                    ? PreRunCheck.Err("阀门", "排空阀处于开启状态，运行前必须关闭")
                    : PreRunCheck.Ok("阀门", "IO16R 在线 · 排空阀已关闭"));

        // 气路：泄漏测试进行中不允许开始运行（C6）；程序的气路步骤占用只提示（本轮运行不使用气路，程序通道不参与本次运行）
        checks.Add(GasBusy && !GasOwnedBy(GasOwner.Program)
            ? PreRunCheck.Err("气路", $"{GasTask}进行中，请等待完成或先停止")
            : GasBusy
                ? PreRunCheck.Warn("气路", $"{GasTask}进行中（程序通道由程序控制，本次运行不使用气路）")
                : PreRunCheck.Ok("气路", "气路空闲"));

        // 控制模式
        checks.Add(ManualValve
            ? PreRunCheck.Warn("控制模式", "手动阀控仍开启，自动运行时建议关闭")
            : PreRunCheck.Ok("控制模式", "自动模式，手动阀控已关闭"));

        // 搅拌器（全局共用）：开始运行会自动开启搅拌
        checks.Add(!Services.HardwareOptions.UseRealStir
            ? PreRunCheck.Warn("搅拌器", "搅拌未配置串口（PR_STIR_PORT），本次运行不搅拌")
            : !_stir.Online
                ? PreRunCheck.Warn("搅拌器", "搅拌驱动器离线，开始运行后可能无法搅拌")
                : PreRunCheck.Ok("搅拌器", $"在线 · 开始后以 {StirRpm} rpm 运转（全部反应釜共用）"));

        // 报警 / 超温 / 超压未解除的通道：阻塞运行（C19，任何原因、任何通道，含停用通道）
        var chans = Reactors.Select(RunChannelOf).ToList();
        var blocking = RunSelection.Blocking(chans);
        checks.Add(blocking.Count > 0
            ? PreRunCheck.Err("报警通道", $"{string.Join("、", blocking.Select(id => "RV" + id))} 存在报警或超温/超压未解除的通道，请先处理并停止 / 复位")
            : PreRunCheck.Ok("报警通道", "无报警或超温/超压未解除的通道"));

        // 正在执行程序的通道由程序控制，不参与本次运行（C20）
        if (Program.RunningIds is { Count: > 0 } prvs)
        {
            string names = string.Join("、", prvs.Select(x => "RV" + x));
            checks.Add(PreRunCheck.Warn("程序通道",
                $"{names} 正在执行程序，不参与本次运行 · 注意搅拌器全机共用：开始运行会以 {StirRpm} rpm 开启搅拌（{names} 也会被搅拌），程序的搅拌步骤结束 / 中止时也会停掉搅拌"));
        }

        // 参与运行的通道：只有已启用 / 已结束的通道会转入升温（已在运行的通道不重复下发）
        int active = RunSelection.ToHeat(chans).Count;
        checks.Add(active > 0
            ? PreRunCheck.Ok("运行通道", $"{active} / 8 个通道将参与本次运行")
            : PreRunCheck.Err("运行通道", "没有可开始的通道（已启用 / 已结束），请先启用至少一个反应釜"));

        int ok = checks.Count(c => c.Kind == "ok");
        int warn = checks.Count(c => c.Kind == "warn");
        int err = checks.Count(c => c.Kind == "err");
        string summary = err == 0
            ? $"检查通过 · {ok} 项正常" + (warn > 0 ? $" · {warn} 项提示" : "")
            : $"{err} 项异常需处理 · {warn} 项提示 · {ok} 项正常";
        return new PreRunResult(checks, err == 0, summary);
    }

    /// <summary>底栏「← 退出」：未启用登录，按退出程序处理（弹确认框）。</summary>
    [RelayCommand]
    [ViewEntry("打开退出确认框")]
    private void Logout() => OpenExitConfirm();

    [RelayCommand]
    [ViewEntry("切换页签")]
    private void SwitchTabCmd(string id) => SwitchTab(id);

    [ViewEntry("切换页签（预热 / 曲线跳转）")]
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
        Services.HardwareOptions.SimBus?.SimulateStep();   // 仅模拟模式（PR_SIM=1）：推进模拟 PV / 压力
        _ = Temp.PollAsync();    // 温控轮询：PV / 输出 / 自整定状态
        _ = _press.PollAsync(skipIfBusy: true);  // 压力轮询（8AI）；泄漏测试的即时读取还没回来时跳过本轮

        // 搅拌电机故障/母线电压轮询（约每 3 秒，只在配置了串口时跑）
        if (Services.HardwareOptions.UseRealStir && _stirFaultTick++ % 3 == 0)
            _ = RefreshStirFaultAsync();

        bool realT = Temp.Online;
        bool realP = _press.Online;
        TrackModules();   // 模块掉线 / 恢复：只在跳变时记事件

        foreach (var c in Reactors)
        {
            // 温度 = AI-8 实测 PV，压力 = 8AI 实测值；模块离线时读数标记无效（显示「—」），不参与报警判定
            c.TOnline = realT;
            c.POnline = realP;
            if (realT) c.T = Temp.Channels[c.Id - 1].Pv;
            if (realP) c.P = _press.Pressures[c.Id - 1];

            // 超温切断对所有通道生效（含停用 / 已结束 / 自整定中）：加热器可能仍在出力，不能只看运行中的通道
            CheckOverTemp(c, realT);
            CheckOverPressure(c, realP);   // 超压联锁对所有通道生效：关阀 + 中止气路 + 停加热 + 置报警（见 CheckOverPressure）

            if (c.State is ReactorState.Idle or ReactorState.Done) continue;

            // 运行中通道的超温报警判定（阈值来自全局设置；超压报警由 CheckOverPressure 置位，含停用 / 已结束通道）
            if (realT && c.T >= Settings.OverTemp)
            {
                c.State = ReactorState.Alarm;
                c.OverTemp = true;   // 状态文字区分超温 / 超压（OverTemp 离开报警态时自动清除）
            }
        }
        FlushOverPressureValves();   // 本 Tick 的超压关阀合并成一帧下发

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
        LogTelemetry(realT, realP);   // 一行运行记录（只入队，后台写盘）
        EstopRetryTick();   // 急停锁定中：重发未确认的停止指令
    }

    public void ClockTick()
    {
        var now = DateTime.Now;   // 系统时间（板子需联网对时或手动设置 RTC）
        ClockTime = now.ToString("HH:mm:ss");
        ClockDate = now.ToString("yyyy-MM-dd") + " " + Wk[(int)now.DayOfWeek];
        Setting.RefreshNetworkInfo();   // 周期刷新设置页的本机 IP（内部已节流约 5s）
        Setting.RefreshCommStatus();    // 通讯页模块在线状态 + 运行时长
        ControlClockTick();             // 远程控制申请弹窗的倒计时
    }
}

/// <summary>预运行检查的一项（正常 ok / 提示 warn / 异常 err，含主题配色与图标）。</summary>
/// <summary>预运行检查的计算结果（<see cref="MainViewModel.ComputePreRun"/>）。</summary>
public sealed record PreRunResult(List<PreRunCheck> Checks, bool CanRun, string Summary);

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
