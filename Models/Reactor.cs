using CommunityToolkit.Mvvm.ComponentModel;

namespace ParallelReactor.Models;

/// <summary>
/// 单个反应釜（RV）的数据模型，对应 HTML 中 RVs 数组的每一项。
/// 任意属性变化会触发 PropertyChanged，气路图与抽屉据此重绘。
/// </summary>
public partial class Reactor : ObservableObject
{
    public int Id { get; init; }

    [ObservableProperty] private ReactorState _state;

    // —— 测量值 PV ——
    [ObservableProperty] private double _t;      // 温度 °C
    [ObservableProperty] private double _p;      // 压力 psi
    [ObservableProperty] private double _gas;    // 气体消耗 mmol
    [ObservableProperty] private int _rpm;       // 当前转速

    // —— 设定值 SP ——
    [ObservableProperty] private double _tSp;
    [ObservableProperty] private double _pSp;
    [ObservableProperty] private int _rpmSp;

    // —— 反应配置 ——
    [ObservableProperty] private int _vol;       // 溶液体积 mL
    [ObservableProperty] private string _blade = "桨式";
    [ObservableProperty] private string _end = "密封";
    [ObservableProperty] private string _run = "02:00:00";

    // —— 阀门 / IO ——
    [ObservableProperty] private bool _valve;

    /// <summary>报警是超温切断引起的（State=Alarm 期间有效；离开报警态时自动清除）。只影响状态文字「超温」/「超压」。</summary>
    [ObservableProperty] private bool _overTemp;
    partial void OnOverTempChanged(bool value) => OnPropertyChanged(nameof(StateZh));

    // —— 已应用配方（标签）——
    private string _appliedRecipe = "";
    public string AppliedRecipe
    {
        get => _appliedRecipe;
        set { if (SetProperty(ref _appliedRecipe, value)) OnPropertyChanged(nameof(HasRecipe)); }
    }
    public bool HasRecipe => !string.IsNullOrEmpty(AppliedRecipe);

    public PidParams Pid { get; } = new();

    // —— 温度设定方式：fixed=定值升温（直接写 SP），curve=曲线升温（16 段程序，上位机下发）——
    [ObservableProperty] private string _spMode = "fixed";

    /// <summary>16 段曲线升温程序（curve 模式下由 MainViewModel.Tick 驱动下发）。</summary>
    public TempProfile Profile { get; } = new();

    /// <summary>定值加热防超调逼近（fixed 模式启动加热后由 MainViewModel.Tick 驱动下发）。</summary>
    public HeatApproach Approach { get; } = new();

    // —— 读数是否有效（MainViewModel.Tick 按温控 / 压力模块在线状态刷新）：无效时显示「—」，不显示旧值或 0 ——
    [ObservableProperty] private bool _tOnline;
    [ObservableProperty] private bool _pOnline;

    /// <summary>气体消耗是否为实测值（反应步骤按 ΔP 计算后才置 true；之前一律显示「—」）。</summary>
    [ObservableProperty] private bool _gasValid;

    public string TText => TOnline ? $"{T:0.0}" : "—";
    public string GasText => GasValid ? $"{Gas:0.00}" : "—";

    partial void OnTChanged(double value) => OnPropertyChanged(nameof(TText));
    partial void OnTOnlineChanged(bool value) => OnPropertyChanged(nameof(TText));
    partial void OnGasChanged(double value) => OnPropertyChanged(nameof(GasText));
    partial void OnGasValidChanged(bool value) => OnPropertyChanged(nameof(GasText));

    // —— 压力显示（按全局单位换算；内部 P 恒为 psi，不影响报警/SP 判定）——
    public double PShown => Units.P(P);
    public string PText => POnline ? Units.PValue(P) : "—";   // 按单位取合适小数位（MPa 三位 / bar 两位 / psi 一位）
    public string PUnit => Units.PLabel;

    /// <summary>P 变化时刷新换算后的显示值。</summary>
    partial void OnPChanged(double value)
    {
        OnPropertyChanged(nameof(PShown));
        OnPropertyChanged(nameof(PText));
    }
    partial void OnPOnlineChanged(bool value) => OnPropertyChanged(nameof(PText));

    /// <summary>全局单位切换后调用，刷新压力显示（值 + 单位标签）。</summary>
    public void RefreshUnits()
    {
        OnPropertyChanged(nameof(PShown));
        OnPropertyChanged(nameof(PText));
        OnPropertyChanged(nameof(PUnit));
    }

    /// <summary>状态中文名，对应 HTML stZh 映射</summary>
    public string StateZh => State switch
    {
        ReactorState.React => "反应中",
        ReactorState.Pressing => "升压中",
        ReactorState.Heating => "升温中",
        ReactorState.Alarm => OverTemp ? "超温" : "超压",
        ReactorState.Done => "已结束",
        ReactorState.Ready => "已启用",
        ReactorState.Wait => "等待中",
        ReactorState.Stir => "搅拌中",
        ReactorState.Cooling => "降温中",
        _ => "停用"
    };

    public bool IsIdle => State == ReactorState.Idle;
    public bool IsDone => State == ReactorState.Done;
    public bool IsRunning => State is ReactorState.React or ReactorState.Heating
        or ReactorState.Pressing or ReactorState.Alarm or ReactorState.Ready
        or ReactorState.Wait or ReactorState.Stir or ReactorState.Cooling;
    /// <summary>真正在干活（升温/反应/升压/超压/等待/搅拌/降温），区别于仅「已启用」：程序编辑与下发据此锁定。</summary>
    public bool IsWorking => State is ReactorState.React or ReactorState.Heating
        or ReactorState.Pressing or ReactorState.Alarm
        or ReactorState.Wait or ReactorState.Stir or ReactorState.Cooling;

    /// <summary>状态变化时同步刷新派生属性</summary>
    partial void OnStateChanged(ReactorState value)
    {
        if (value != ReactorState.Alarm) OverTemp = false;
        OnPropertyChanged(nameof(StateZh));
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsWorking));
    }
}
