using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace ParallelReactor.Control.Controls.Pr;

/// <summary>徽标色调（样稿 ui.badge 的 tone）：None 中性灰；Ok 绿点；Warn 白底描边 + ⚠；Err 红；Info 强调色浅底；Mute 灰。</summary>
public enum BadgeTone { None, Ok, Warn, Err, Info, Mute }

/// <summary>
/// 徽标（样稿 ui.badge / .pr-badge）：高 26、圆角 4、14 px 500；前面是 8 px 点、或 15 px 图标、或（warn）⚠。
/// 全部中性：报警 = 红点，提醒 = 描边 ⚠，运行 = 浅绿点。外观在 Styles/Theme/Badges.axaml（伪类 :ok :warn :err :info :mute，
/// 派生的控制权 / 连接徽标另有自己的伪类）。
/// </summary>
public class Badge : TemplatedControl
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<Badge, string?>(nameof(Text));
    public static readonly StyledProperty<BadgeTone> ToneProperty = AvaloniaProperty.Register<Badge, BadgeTone>(nameof(Tone));
    public static readonly StyledProperty<string?> IconProperty = AvaloniaProperty.Register<Badge, string?>(nameof(Icon));
    public static readonly StyledProperty<bool> ShowDotProperty = AvaloniaProperty.Register<Badge, bool>(nameof(ShowDot), true);

    /// <summary>实际显示的图标（Icon、派生类的默认图标，或 warn 色调的 ⚠）；null = 没有图标。</summary>
    public static readonly DirectProperty<Badge, string?> ShownIconProperty =
        AvaloniaProperty.RegisterDirect<Badge, string?>(nameof(ShownIcon), o => o.ShownIcon);
    /// <summary>实际显示的文字（Text，空时用派生类的默认文字）。</summary>
    public static readonly DirectProperty<Badge, string?> ShownTextProperty =
        AvaloniaProperty.RegisterDirect<Badge, string?>(nameof(ShownText), o => o.ShownText);

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public BadgeTone Tone { get => GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
    /// <summary>图标名（有图标时不画点）。</summary>
    public string? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    /// <summary>false = 不画点（样稿 { dot: false }）。</summary>
    public bool ShowDot { get => GetValue(ShowDotProperty); set => SetValue(ShowDotProperty, value); }

    private string? _shownIcon, _shownText;
    public string? ShownIcon { get => _shownIcon; private set => SetAndRaise(ShownIconProperty, ref _shownIcon, value); }
    public string? ShownText { get => _shownText; private set => SetAndRaise(ShownTextProperty, ref _shownText, value); }

    public Badge() => Update();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ToneProperty || change.Property == IconProperty || change.Property == ShowDotProperty || change.Property == TextProperty) Update();
    }

    /// <summary>派生类（LeaseBadge / ConnBadge）的默认图标。</summary>
    protected virtual string? DefaultIcon => null;
    /// <summary>派生类的默认文字（Text 为空时）。</summary>
    protected virtual string? DefaultText => null;

    protected void Update()
    {
        var tone = Tone;
        PseudoClasses.Set(":ok", tone == BadgeTone.Ok);
        PseudoClasses.Set(":warn", tone == BadgeTone.Warn);
        PseudoClasses.Set(":err", tone == BadgeTone.Err);
        PseudoClasses.Set(":info", tone == BadgeTone.Info);
        PseudoClasses.Set(":mute", tone == BadgeTone.Mute);
        var icon = Icon ?? DefaultIcon ?? (tone == BadgeTone.Warn && ShowDot ? "warn" : null);
        ShownIcon = icon;
        ShownText = string.IsNullOrEmpty(Text) ? DefaultText : Text;
        PseudoClasses.Set(":icon", icon != null);
        PseudoClasses.Set(":dot", icon == null && ShowDot);
    }
}

/// <summary>
/// 控制权状态（样稿 PR.sim.leaseInfo 的 state）+ 真机的第 5 种 <see cref="Other"/>：被其他客户端持有（PORT-PLAN §3.5 / R8；演示数据不会出现）。
/// 前 4 种的顺序与 Core 的 LeaseStatus 相同。
/// </summary>
public enum LeaseState { Local, Requested, Remote, Lost, Other }

/// <summary>
/// 控制权徽标（样稿 ui.leaseBadge / .pr-lease--*，brief §5.5）：远程控制中 = a-soft + 钥匙；本地 · 只读 = 灰 + 锁；
/// 申请中 = 白底紫框 + 时钟（倒计时写在文字里）；心跳中断 / 宽限 = 红 + ⚠。Text 为空时用默认文字。
/// 第 5 种「其他客户端 X」（M3，样稿没有）：灰底 + 人形图标 + t-2 字 —— 中性（不是本机的控制权，不用强调色；也不是故障，不用红）。
/// </summary>
public sealed class LeaseBadge : Badge
{
    public static readonly StyledProperty<LeaseState> StateProperty = AvaloniaProperty.Register<LeaseBadge, LeaseState>(nameof(State));

    public LeaseState State { get => GetValue(StateProperty); set => SetValue(StateProperty, value); }

    public LeaseBadge() => UpdateState();

    protected override string? DefaultIcon => State switch { LeaseState.Remote => "key", LeaseState.Requested => "clock", LeaseState.Lost => "alert", LeaseState.Other => "user", _ => "lock" };
    protected override string? DefaultText => TextOf(State);

    /// <summary>没有倒计时时的文字。</summary>
    public static string TextOf(LeaseState s) => s switch { LeaseState.Remote => "远程控制中", LeaseState.Requested => "申请中", LeaseState.Lost => "控制权宽限", LeaseState.Other => "其他客户端", _ => "本地 · 只读" };

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StateProperty) UpdateState();
    }

    private void UpdateState()
    {
        var s = State;
        PseudoClasses.Set(":remote", s == LeaseState.Remote);
        PseudoClasses.Set(":local", s == LeaseState.Local);
        PseudoClasses.Set(":requested", s == LeaseState.Requested);
        PseudoClasses.Set(":lost", s == LeaseState.Lost);
        PseudoClasses.Set(":other", s == LeaseState.Other);
        Update();
    }
}

/// <summary>连接状态（样稿 PR.sim.connInfo 的 state）。</summary>
public enum ConnState { Online, Connecting, Offline, Disconnected }

/// <summary>
/// 连接徽标（样稿 ui.connBadge / .pr-conn--*）：已连接 / 已断开 灰；正在连接 白底描边；连接中断 红（断线属于报警色）。
/// </summary>
public sealed class ConnBadge : Badge
{
    public static readonly StyledProperty<ConnState> StateProperty = AvaloniaProperty.Register<ConnBadge, ConnState>(nameof(State));

    public ConnState State { get => GetValue(StateProperty); set => SetValue(StateProperty, value); }

    public ConnBadge() => UpdateState();

    protected override string? DefaultIcon => State switch { ConnState.Connecting => "refresh", ConnState.Offline => "wifioff", ConnState.Disconnected => "unplug", _ => "link" };
    protected override string? DefaultText => TextOf(State);

    public static string TextOf(ConnState s) => s switch { ConnState.Connecting => "正在连接…", ConnState.Offline => "连接中断", ConnState.Disconnected => "已断开", _ => "已连接" };

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StateProperty) UpdateState();
    }

    private void UpdateState()
    {
        var s = State;
        PseudoClasses.Set(":online", s == ConnState.Online);
        PseudoClasses.Set(":connecting", s == ConnState.Connecting);
        PseudoClasses.Set(":offline", s == ConnState.Offline);
        PseudoClasses.Set(":disconnected", s == ConnState.Disconnected);
        Update();
    }
}
