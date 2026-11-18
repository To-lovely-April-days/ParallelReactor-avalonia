using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages;

namespace ParallelReactor.Control.Shell;

/// <summary>
/// 检查器（PORT-PLAN §2.1「检查器」、§5 M2「InspectorVm：分 DeviceInspectorVm 和 ReactorInspectorVm」；样稿 shell.js renderInspector）。
/// 显示外框的选中对象（<see cref="SelectionVm"/>）：标题栏「检查器 /别名/RVn」+ 打开（设备页 / 反应釜页）；正文 <see cref="Body"/> 是
/// <see cref="DeviceInspectorVm"/>、<see cref="ReactorInspectorVm"/>、<see cref="UninstalledInspectorVm"/> 之一（没有设备时 null = 空状态）。
/// 正文视图模型按对象缓存（再次选中同一个对象时是同一个实例），每次刷新就地更新；换对象时 <see cref="BodyKey"/> 变（视图把滚动回到顶部，
/// 同样稿 data-key 换了整块重画）。检查器对象不是当前页签的对象时（例如在 1号机 · RV1 页签上单击了 3号机 RV11）顶部有提示条
/// 「检查器：3号机 · RV11（不是当前页签） 回到 1号机 · RV1」，避免对错的设备操作。
/// 同一条报警屏幕上只有一个「确认」：当前页面声明自己有确认按钮的报警（<see cref="PageVm.OwnedAlarms"/>，样稿 ownsAlarms）在这里不再放按钮。
/// </summary>
public sealed partial class InspectorVm : ObservableObject
{
    private readonly ShellVm _shell;
    private readonly Dictionary<int, DeviceInspectorVm> _devices = new();
    private readonly Dictionary<(int K, int N), ReactorInspectorVm> _reactors = new();
    private readonly Dictionary<(int K, int N), UninstalledInspectorVm> _uninstalled = new();
    private (int K, int N) _back;

    public InspectorVm(ShellVm shell)
    {
        _shell = shell;
        OpenCommand = new RelayCommand(() =>
        {
            // 「在主页查看」：主页选中这台设备（和这一路）
            if (shell.SelectedDevice is not { } d) return;
            shell.Go(AppPage.Home);
            shell.Select(d.K, shell.SelectedRv);
        });
        BackCommand = new RelayCommand(() => { if (_back.K > 0) shell.Select(_back.K, _back.N > 0 ? _back.N : null); });
    }

    public ICommand OpenCommand { get; }
    /// <summary>提示条的「回到 …」：选中当前页签的对象。</summary>
    public ICommand BackCommand { get; }

    // —— 标题栏 ——
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private string _pathAlias = "";
    [ObservableProperty] private string _pathRv = "";
    [ObservableProperty] private string _pathTip = "";
    [ObservableProperty] private string _openTip = "";

    // —— 正文 ——
    /// <summary>DeviceInspectorVm / ReactorInspectorVm / UninstalledInspectorVm；没有设备时 null（空状态「没有选中对象」）。</summary>
    [ObservableProperty] private object? _body;
    /// <summary>正文对象的键（「1-d」「1-3」）：变了视图把滚动回到顶部。</summary>
    [ObservableProperty] private string _bodyKey = "";
    [ObservableProperty] private bool _isEmpty = true;
    /// <summary>检查器 300 px（窗口窄于 1920，样稿 .insp-n）：数值框的标签放到控件上方、控制权卡不写「控制权」。</summary>
    [ObservableProperty] private bool _narrow;

    // —— 「不是当前页签」提示条 ——
    [ObservableProperty] private bool _hasOther;
    [ObservableProperty] private string _otherName = "";
    [ObservableProperty] private string _otherBack = "";
    /// <summary>
    /// 提示条的那一句「检查器：<b>3号机 · RV11</b>（不是当前页签）」：一段文字（同样稿一个 span，名字加粗）—— 放不下时整句换到图标下面、句子里面再折行
    /// （M6：以前拆成三块分别换行，1366 下两行，样稿是三行）。
    /// </summary>
    [ObservableProperty] private IReadOnlyList<TextSeg> _otherSegments = Array.Empty<TextSeg>();

    /// <summary>「设置」链接（共用转速）：选中设备后请视图把对应的输入框滚到可见处并聚焦（参数 = 输入的名字，「rpm」）。</summary>
    public event EventHandler<string>? FocusRequested;

    internal void RequestFocus(string name) => FocusRequested?.Invoke(this, name);

    /// <summary>
    /// 页面上的对象（融合版：检查器的对象就是路由选中的设备 / 通道，不会出现 pc-v3 那种「不是当前页签」—— 一律 null，提示条不出现）。
    /// </summary>
    private (int K, int N)? PageObj() => null;

    internal void Refresh()
    {
        var d = _shell.SelectedDevice;
        int? n = _shell.SelectedRv;
        var r = n is int nn ? d?.Reactor(nn) : null;
        Narrow = false;   // 融合版的检查器停靠 340 / 浮出 400，都不窄（pc-v3 的 300 才窄）

        HasSelection = d != null;
        PathAlias = d == null ? "" : "/" + d.Alias;
        PathRv = r != null ? "/RV" + r.N : "";
        PathTip = PathAlias + PathRv;
        OpenTip = r != null ? "打开反应釜页" : "打开设备页";
        Prune();

        if (d == null)
        {
            Body = null;
            BodyKey = "none";
            IsEmpty = true;
            HasOther = false;
            return;
        }
        IsEmpty = false;

        var page = PageObj();
        bool other = page != null && (page.Value.K != d.K || (page.Value.N > 0 && page.Value.N != (r?.N ?? 0)));
        HasOther = other;
        if (other)
        {
            var pd = _shell.Fleet.Device(page!.Value.K)!;
            OtherName = d.Alias + (r != null ? " · RV" + r.N : "");
            OtherBack = "回到 " + pd.Alias + (page.Value.N > 0 ? " · RV" + page.Value.N : "");
            if (OtherSegments.Count != 3 || OtherSegments[1].Text != OtherName)
                OtherSegments = new[] { new TextSeg("检查器："), new TextSeg(OtherName, Bold: true), new TextSeg("（不是当前页签）") };
            _back = (page.Value.K, page.Value.N);
        }

        var owned = _shell.Page?.OwnedAlarms ?? PageVm.NoOwnedAlarms;
        var ctx = new InspCtx(_shell, d, d.Facts, owned, _shell.Nav.Page == AppPage.Home && _shell.Nav.DeviceK > 0 ? _shell.Nav.DeviceK : null, Narrow, _shell.Fleet.Now);
        // 设备页打开着这台设备、页面上有「报警与事件」：检查器不再放报警 / 事件列表（样稿 inspDevice 的 o.page === 'device'）
        bool showLog = !(!other && _shell.Nav.DeviceK == d.K && _shell.Page is { } pg && pg.ShowsAlarmLog);
        string key;
        object body;
        if (r != null && !r.Installed)
        {
            key = d.K.ToString(CultureInfo.InvariantCulture) + "-u" + r.N.ToString(CultureInfo.InvariantCulture);
            if (!_uninstalled.TryGetValue((d.K, r.N), out var u) || !ReferenceEquals(u.D, d)) _uninstalled[(d.K, r.N)] = u = new UninstalledInspectorVm(_shell, d, r.N);
            if (key != BodyKey) u.Device.ResetInputs();
            u.Refresh(ctx, showLog: true);   // 样稿 inspUninstalled → inspDevice(d, { noHead })：报警 / 事件照常列出
            body = u;
        }
        else if (r != null)
        {
            key = d.K.ToString(CultureInfo.InvariantCulture) + "-" + r.N.ToString(CultureInfo.InvariantCulture);
            if (!_reactors.TryGetValue((d.K, r.N), out var rv) || !ReferenceEquals(rv.R, r)) _reactors[(d.K, r.N)] = rv = new ReactorInspectorVm(_shell, d, r);
            if (key != BodyKey) rv.ResetInputs();
            // 打开的页签就是这个反应釜：报警条（含唯一的「确认」）在页面顶部，页面声明了的报警不再列在标题下面
            bool pageOwnsTop = false;
            rv.Refresh(ctx, pageOwnsTop);
            body = rv;
        }
        else
        {
            key = d.K.ToString(CultureInfo.InvariantCulture) + "-d";
            if (!_devices.TryGetValue(d.K, out var dv) || !ReferenceEquals(dv.D, d)) _devices[d.K] = dv = new DeviceInspectorVm(_shell, d);
            if (key != BodyKey) dv.ResetInputs();
            dv.Refresh(ctx, showLog);
            body = dv;
        }
        Body = body;
        BodyKey = key;
    }

    /// <summary>设备没了的缓存去掉（设备组合重建时 DeviceVm 按位置保留，所以一般不会去掉）。</summary>
    private void Prune()
    {
        var fleet = _shell.Fleet;
        foreach (var k in _devices.Keys.Where(k => fleet.Device(k) == null).ToList()) _devices.Remove(k);
        foreach (var k in _reactors.Keys.Where(k => fleet.Device(k.K)?.Reactor(k.N) == null).ToList()) _reactors.Remove(k);
        foreach (var k in _uninstalled.Keys.Where(k => fleet.Device(k.K)?.Reactor(k.N) == null).ToList()) _uninstalled.Remove(k);
    }
}
