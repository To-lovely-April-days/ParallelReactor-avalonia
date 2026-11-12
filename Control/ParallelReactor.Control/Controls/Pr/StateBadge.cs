using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Controls.Pr;

/// <summary>
/// 反应釜状态：点 + 字（样稿 ui.keyDot + .pr-stword，陈列页 .gallery-st：间距 7、15 px）。不加底色 / 边框（brief §4.3 不重复编码）。
/// 状态字颜色 / 字重来自 StateStyles（报警 r-ink 600，其它 400），状态字<b>绝不省略</b>（类 stword，ShotAudit 会查）。
/// 类 <c>badge</c>：徽标版（样稿 ui.stateBadge / .pr-badge--st：高 26、14 px、8 px 点、间距 6）。
/// <see cref="Text"/> 覆盖默认状态字（真机的「超温报警」「等待气路（RV3 占用中）」由 Core StateMap 给出）；离线且从未连上时写「未连接」。
/// </summary>
public sealed class StateBadge : TemplatedControl
{
    public static readonly StyledProperty<StateKey> KeyProperty = AvaloniaProperty.Register<StateBadge, StateKey>(nameof(Key));
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<StateBadge, string?>(nameof(Text));
    public static readonly StyledProperty<bool> NeverConnectedProperty = AvaloniaProperty.Register<StateBadge, bool>(nameof(NeverConnected));

    public static readonly DirectProperty<StateBadge, string?> ShownTextProperty =
        AvaloniaProperty.RegisterDirect<StateBadge, string?>(nameof(ShownText), o => o.ShownText);
    public static readonly DirectProperty<StateBadge, IBrush?> WordBrushProperty =
        AvaloniaProperty.RegisterDirect<StateBadge, IBrush?>(nameof(WordBrush), o => o.WordBrush);
    public static readonly DirectProperty<StateBadge, FontWeight> WordWeightProperty =
        AvaloniaProperty.RegisterDirect<StateBadge, FontWeight>(nameof(WordWeight), o => o.WordWeight);

    public StateKey Key { get => GetValue(KeyProperty); set => SetValue(KeyProperty, value); }
    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    /// <summary>设备从未连上（离线时写「未连接」）。</summary>
    public bool NeverConnected { get => GetValue(NeverConnectedProperty); set => SetValue(NeverConnectedProperty, value); }

    private string? _text;
    private IBrush? _brush;
    private FontWeight _weight = FontWeight.Normal;
    public string? ShownText { get => _text; private set => SetAndRaise(ShownTextProperty, ref _text, value); }
    public IBrush? WordBrush { get => _brush; private set => SetAndRaise(WordBrushProperty, ref _brush, value); }
    public FontWeight WordWeight { get => _weight; private set => SetAndRaise(WordWeightProperty, ref _weight, value); }

    public StateBadge() => Update();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KeyProperty || change.Property == TextProperty || change.Property == NeverConnectedProperty) Update();
    }

    private void Update()
    {
        var st = StateStyles.Get(Key);
        ShownText = string.IsNullOrEmpty(Text) ? StateStyles.Word(Key, NeverConnected) : Text;
        WordBrush = st.Word;
        WordWeight = st.WordWeight;
        PseudoClasses.Set(":done", Key == StateKey.Done);
        PseudoClasses.Set(":alarm", Key == StateKey.Alarm);
    }
}

/// <summary>
/// 设备标识（样稿 ui.devTag / .pr-devtag）：别名（600 t-1，太长时省略）+ 型号（14 px t-3），基线对齐、间距 8。
/// 设备没有识别色（brief §2.2）：只靠「别名 + 型号」区分。悬停提示「别名 · 型号」。
/// </summary>
public sealed class DevTag : TemplatedControl
{
    public static readonly StyledProperty<string?> AliasProperty = AvaloniaProperty.Register<DevTag, string?>(nameof(Alias));
    public static readonly StyledProperty<string?> ModelProperty = AvaloniaProperty.Register<DevTag, string?>(nameof(Model));
    public static readonly DirectProperty<DevTag, string?> TipProperty = AvaloniaProperty.RegisterDirect<DevTag, string?>(nameof(Tip), o => o.Tip);

    public string? Alias { get => GetValue(AliasProperty); set => SetValue(AliasProperty, value); }
    /// <summary>型号（null = 只显示别名）。</summary>
    public string? Model { get => GetValue(ModelProperty); set => SetValue(ModelProperty, value); }

    private string? _tip;
    public string? Tip { get => _tip; private set => SetAndRaise(TipProperty, ref _tip, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AliasProperty || change.Property == ModelProperty)
        {
            Tip = string.IsNullOrEmpty(Model) ? Alias : Alias + " · " + Model;
            PseudoClasses.Set(":model", !string.IsNullOrEmpty(Model));
        }
    }
}
