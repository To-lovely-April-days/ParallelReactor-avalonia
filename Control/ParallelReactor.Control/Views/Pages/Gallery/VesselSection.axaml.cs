using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using ParallelReactor.Control.Controls.Vessel;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;

namespace ParallelReactor.Control.Views.Pages.Gallery;

/// <summary>陈列页「釜体」的一项：说明 + 投影好的反应釜（ReactorVm，同页面上的卡片）。</summary>
public sealed record VesselSample(string Caption, ReactorVm Reactor);

/// <summary>
/// 陈列页「釜体」分区（MOCK placeholder.js 的 vs）。数据是样稿冻结时（freeze=1）的演示数据：新建一份冻结的 Core DemoFleet，
/// 经 DemoDeviceSource → ReactorVm.Apply（与 DeviceVm.Refresh 同一条路）投影出状态、加热 / 冷却 / 搅拌、液位，再绑到 ReactorVessel 上。
/// </summary>
public partial class VesselSection : UserControl
{
    /// <summary>样稿 vs 的七项：(设备号, RV, 说明里写的状态)。</summary>
    private static readonly (int K, int N, string Word)[] Cards =
    {
        (1, 1, "反应中"), (1, 3, "升温中"), (2, 3, "降温中"), (3, 11, "超压报警"), (2, 4, "已结束"), (3, 15, "停用"), (1, 5, "未安装"),
    };

    /// <summary>vesselGlyph 一行的状态（样稿 ['react', 'heat', 'alarm', 'ready', 'done', 'offline', 'uninstalled']）。</summary>
    public static readonly IReadOnlyList<StateKey> GlyphKeys = new[]
    {
        StateKey.React, StateKey.Heat, StateKey.Alarm, StateKey.Ready, StateKey.Done, StateKey.Offline, StateKey.Uninstalled,
    };

    /// <summary>card 釜体的框（.gallery-vbox）与 large 的框（.gallery-v.is-large .gallery-vbox）。</summary>
    public const double CardW = 104, CardH = 150, LargeW = 180, LargeH = 270, GlyphSize = 26;

    private static IReadOnlyList<VesselSample>? _samples;
    private static VesselSample? _large;

    /// <summary>七个 card 釜体（演示数据里有这个反应釜位置的才放）。</summary>
    public static IReadOnlyList<VesselSample> Samples { get { Build(); return _samples!; } }

    /// <summary>large：1号机 RV1。</summary>
    public static VesselSample? Large { get { Build(); return _large; } }

    private static void Build()
    {
        if (_samples != null) return;
        var fleet = new DemoFleet(DemoParams.Default with { Freeze = true });
        var list = new List<VesselSample>();
        foreach (var (k, n, word) in Cards)
            if (fleet.Device(k) is { } d && d.Channel(n) != null)
                list.Add(new VesselSample(d.Alias + " · RV" + n + " · " + word, Project(fleet, d, n)));
        _samples = list;
        _large = fleet.Device(1) is { } d1 && d1.Channel(1) != null ? new VesselSample("large", Project(fleet, d1, 1)) : null;
    }

    /// <summary>一个反应釜的投影（同 DeviceVm.Refresh：数据源的读数 + 演示才有的体积 / 冷却 / 转速）。</summary>
    public static ReactorVm Project(DemoFleet fleet, DemoDevice d, int n)
    {
        var src = new DemoDeviceSource(fleet, d);
        var vm = new ReactorVm(null, n);
        vm.Apply(src.Read(n), d.Model, d.Online, d.Conn.EverOnline, d.Alias, d.DataText, extras: src.Extras(n));
        _ = src.DisposeAsync();
        return vm;
    }

    public VesselSection()
    {
        InitializeComponent();
        foreach (var s in Samples) Row.Children.Add(Figure(Vessel(s.Reactor, VesselSize.Card, CardW, CardH), s.Caption));
        if (Large is { } l) Row.Children.Add(Figure(Vessel(l.Reactor, VesselSize.Large, LargeW, LargeH), l.Caption));
        // .gallery-glyphs：横排、间距 10、上下 8
        var glyphs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 8), HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var k in GlyphKeys) glyphs.Children.Add(new VesselGlyph { Key = k, Size = GlyphSize });
        Row.Children.Add(Figure(glyphs, "vesselGlyph"));
    }

    /// <summary>反应釜 → 釜体（框的宽高同样稿；状态、标志、液位取自 ReactorVm）。</summary>
    public static ReactorVessel Vessel(ReactorVm r, VesselSize size, double w, double h) => new()
    {
        Width = w, Height = h, HorizontalAlignment = HorizontalAlignment.Center,
        Key = r.Key, Heating = r.Heating, Cooling = r.Cooling, Stirring = r.Stirring, Level = r.VesselLevel, Size = size,
    };

    /// <summary>.gallery-v：图 + 8 px 下面的说明（14 px t-3、行高 20、不换行），横向居中。</summary>
    private static Avalonia.Controls.Control Figure(Avalonia.Controls.Control content, string caption)
    {
        var cap = new TextBlock { Text = caption, HorizontalAlignment = HorizontalAlignment.Center };
        // 主题 GalleryCap 在陈列页的资源里（GalleryTheme.axaml）：同 {DynamicResource}，上树后才找得到
        cap.Bind(StyledElement.ThemeProperty, cap.GetResourceObservable("GalleryCap"));
        var fig = new StackPanel { Spacing = 8 };
        fig.Children.Add(content);
        fig.Children.Add(cap);
        return fig;
    }
}
