using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Fleet;

namespace ParallelReactor.Control.Shell;

/// <summary>资源管理器的一行（扁平列表，PORT-PLAN §5 M2「ExplorerVm：扁平行」）。身份 = <see cref="Key"/>，结构不变时实例不变。</summary>
public abstract partial class XRow : ObservableObject
{
    protected XRow(string key) => Key = key;

    public string Key { get; }
    /// <summary>可以选中（设备头、反应釜行）；其余行（间隔、断线细节、未安装）不可点、键盘跳过。</summary>
    public virtual bool Selectable => false;
    /// <summary>选中：设备头 a-tint 底 + 左侧 3 px a-tab 条（反应釜行同）。</summary>
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// 设备头（样稿 .pr-xhd，高 36 + 28 + 下 4 = 68）：第 1 行 展开箭头 · 设备图标 · 别名 16/600 · 型号 14 t-3（放不下时型号整个让出去）；
/// 第 2 行（从图标下方开始）短控制权徽标（断线时连接徽标）· 报警标记 · 「运行 k/n」（靠右，断线时不画）。
/// <see cref="HasSel"/>：选中的反应釜在收起的设备里 —— 左侧 3 px a-line 条（不铺底色）。
/// </summary>
public sealed partial class XDeviceRow : XRow
{
    public XDeviceRow(DeviceVm d, Action<int> toggle) : base("d" + d.K)
    {
        D = d;
        TwistCommand = new RelayCommand(() => toggle(d.K));
    }

    public DeviceVm D { get; }
    public override bool Selectable => true;
    public ICommand TwistCommand { get; }

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private bool _hasSel;
    [ObservableProperty] private string _twistIcon = "down";
    [ObservableProperty] private string _twistTip = "收起";
    /// <summary>设备头的悬停提示（DeviceVm.HeadTip，选中的反应釜藏在收起的设备里时加一行「选中：RVn」）。</summary>
    [ObservableProperty] private string _tip = "";

    internal void Update(bool open, bool selected, bool hasSel, int? selRv)
    {
        IsOpen = open;
        TwistIcon = open ? "down" : "right";
        TwistTip = open ? "收起" : "展开";
        IsSelected = selected;
        HasSel = hasSel;
        Tip = D.HeadTip + (hasSel && selRv is int n ? "\n选中：RV" + n : "");
    }
}

/// <summary>反应釜行（样稿 .pr-xrow--rv，36 px）：状态点 · RV（37 px，16 路 40 px）· 状态字（不省略）· ℃ 列 73 · MPa 列 84（右对齐，16/600）。</summary>
public sealed class XReactorRow : XRow
{
    public XReactorRow(DeviceVm d, ReactorVm r) : base("r" + d.K + "/" + r.N)
    {
        D = d;
        R = r;
    }

    public DeviceVm D { get; }
    public ReactorVm R { get; }
    public override bool Selectable => true;
}

/// <summary>连续未安装的位置合成一行（样稿「RV5–RV8 未安装」，14 px t-3，不可点）。</summary>
public sealed class XUninstalledRow : XRow
{
    public XUninstalledRow(int k, int from, int to) : base("u" + k + "/" + from)
    {
        Text = (to > from ? "RV" + from + "–RV" + to : "RV" + from) + " 未安装";
        From = from;
        To = to;
    }

    public string Text { get; }
    public int From { get; }
    public int To { get; }
}

/// <summary>断线细节条（样稿 .pr-xoff，30 px，bg-sunken，中性）：连接（wifi-off / 从未连上 link + 文字 + 「数据停在 …」），或控制权宽限（钥匙 + 文字）。</summary>
public sealed class XOffRow : XRow
{
    public XOffRow(DeviceVm d, bool grace) : base((grace ? "g" : "o") + d.K)
    {
        D = d;
        IsGrace = grace;
    }

    public DeviceVm D { get; }
    /// <summary>控制权宽限的那一条（钥匙图标，只有文字）。</summary>
    public bool IsGrace { get; }
    public bool IsConn => !IsGrace;
}

/// <summary>
/// 设备之间的间隔（样稿 .pr-xdev 上 2 / 下 6 + 1 px line-soft 分隔线，.pr-xkids 上下各 2）：合成一行，不可点、不参与悬停。
/// <see cref="LineAt"/> &lt; 0 = 没有分隔线。
/// </summary>
public sealed partial class XGapRow : XRow
{
    public XGapRow(string key) : base(key) { }

    [ObservableProperty] private double _height;
    [ObservableProperty] private double _lineAt = -1;
    [ObservableProperty] private bool _hasLine;

    internal void Set(double height, double lineAt)
    {
        Height = height;
        LineAt = lineAt;
        HasLine = lineAt >= 0;
    }
}

/// <summary>树处理的键（样稿 treeKey 的那几个；视图把 Avalonia 的键换成它，视图模型不碰界面类型）。</summary>
public enum XTreeKey { Down, Up, Home, End, Right, Left, Enter, Space }

/// <summary>
/// 设备资源管理器（PORT-PLAN §2.1「设备资源管理器」、§5 M2；样稿 shell.js renderExplorer / devNodeHTML / rvRowHTML）：
/// 标题栏（设备 · N 台 · 添加 · 状态图例 · 收起 / 浮层时关闭）+ 安静的树。树是<b>扁平行</b>（<see cref="Rows"/>）：设备头、断线细节、反应釜、
/// 合并的「RVa–RVb 未安装」、间隔。结构（设备增删、展开 / 收起、断线细节的出现）变了才增删行，按 <see cref="XRow.Key"/> 保留实例；
/// 读数、状态字等由行引用的 DeviceVm / ReactorVm 就地更新（绑定直接读它们）。
/// <list type="bullet">
/// <item>展开：默认 ≤ 8 路的设备展开、16 路收起（样稿 isXOpen）；展开箭头切换；选中收起的设备里的反应釜时自动展开（样稿 xReveal）。</item>
/// <item>单击选中（设备 / 反应釜，检查器跟着换），双击打开页签（设备页 / 反应釜页；浮层资源管理器随即收起）。</item>
/// <item>键盘（M6，样稿 treeKey / xTabTarget）：树只有一个 Tab 停靠点（<see cref="TabTarget"/>），方向键、Home、End、Enter、空格见 <see cref="TreeKey"/>。</item>
/// </list>
/// </summary>
public sealed partial class ExplorerVm : ObservableObject
{
    private readonly ShellVm _shell;
    private readonly Dictionary<int, bool> _open = new();
    private readonly Dictionary<string, XRow> _rows = new(StringComparer.Ordinal);
    private bool _syncingSelection;

    public ExplorerVm(ShellVm shell)
    {
        _shell = shell;
        AddCommand = new RelayCommand(() => shell.OpenDialog("add"));
        ToggleCommand = new RelayCommand(shell.ToggleExplorer);
    }

    public ICommand AddCommand { get; }
    public ICommand ToggleCommand { get; }

    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private string _closeIcon = "collapse";
    [ObservableProperty] private string _closeTip = "收起资源管理器";
    /// <summary>还没有设备（空状态「还没有设备 · 点「添加」连接第一台设备」）。</summary>
    [ObservableProperty] private bool _isEmpty;

    /// <summary>树的全部行（扁平，按显示顺序）。</summary>
    public ObservableCollection<XRow> Rows { get; } = new();

    /// <summary>列表的选中行（与 ShellVm 的选择双向同步）。</summary>
    [ObservableProperty] private XRow? _selectedRow;

    partial void OnSelectedRowChanged(XRow? value)
    {
        if (_syncingSelection || value == null) return;
        Choose(value);
    }

    /// <summary>选中这一行的设备 / 反应釜（样稿 shell.select）。</summary>
    private void Choose(XRow row)
    {
        switch (row)
        {
            case XDeviceRow d: _shell.Select(d.D.K); break;
            case XReactorRow r: _shell.Select(r.D.K, r.R.N); break;
        }
    }

    /// <summary>
    /// 键盘 Tab 进树时落在哪一行（样稿 xTabTarget，roving tabindex：整棵树只有这一个 Tab 停靠点）：选中的行；选中的反应釜不在树里时它的设备头
    /// （在收起的设备里，同样稿；或者是未安装的位置 —— 样稿这时一行都不给，Tab 进不了树）；没有选中时第一台。
    /// </summary>
    public XRow? TabTarget =>
        SelectedRow ?? Rows.OfType<XDeviceRow>().FirstOrDefault(r => r.D.K == _shell.Selection.K) ?? Rows.OfType<XDeviceRow>().FirstOrDefault();

    /// <summary>
    /// 树的键盘操作（样稿 shell.js treeKey；视图把焦点所在的行和键交过来，返回之后焦点该在的行 —— 不动时就是 <paramref name="at"/>）。
    /// 可见的设备头与反应釜行排成一列（间隔、断线细节、未安装的行不算）：
    /// ↑ ↓ Home End = 上一项 / 下一项 / 第一项 / 最后一项，并选中它（到头了不动，不循环）；
    /// → 收起的设备头 = 展开（选中不变），展开的设备头 = 下一项（它的第一个反应釜）并选中，反应釜行上什么都不做；
    /// ← 展开的设备头 = 收起，收起的设备头上什么都不做，反应釜行 = 回到它的设备头并选中设备；
    /// Enter = 打开页签（设备页 / 反应釜页），浮层资源管理器不收起（样稿只有指针的单击 / 双击收起它）；空格 = 选中。
    /// </summary>
    public XRow TreeKey(XRow at, XTreeKey key)
    {
        var list = Rows.Where(r => r.Selectable).ToList();
        int i = list.IndexOf(at);
        if (i < 0) return at;
        XRow Pick(int j)
        {
            if (j < 0 || j >= list.Count) return at;
            Choose(list[j]);
            return list[j];
        }
        switch (key)
        {
            case XTreeKey.Down: return Pick(i + 1);
            case XTreeKey.Up: return Pick(i - 1);
            case XTreeKey.Home: return Pick(0);
            case XTreeKey.End: return Pick(list.Count - 1);
            case XTreeKey.Right when at is XDeviceRow d:
                if (IsOpen(d.D)) return Pick(i + 1);
                Toggle(d.D.K);
                return at;
            case XTreeKey.Left when at is XDeviceRow d:
                if (IsOpen(d.D)) Toggle(d.D.K);
                return at;
            case XTreeKey.Left when at is XReactorRow r:
                return Pick(list.FindIndex(x => x is XDeviceRow h && ReferenceEquals(h.D, r.D)));
            case XTreeKey.Enter:
                Open(at, keepFloat: true);
                return at;
            case XTreeKey.Space:
                Choose(at);
                return at;
        }
        return at;
    }

    /// <summary>这台设备展开着（默认 ≤ 8 路展开，样稿 isXOpen）。</summary>
    public bool IsOpen(DeviceVm d) => _open.TryGetValue(d.K, out var o) ? o : d.ChannelCount <= 8;

    /// <summary>展开箭头：切换展开 / 收起。</summary>
    public void Toggle(int k)
    {
        var d = _shell.Fleet.Device(k);
        if (d == null) return;
        _open[k] = !IsOpen(d);
        Refresh();
    }

    /// <summary>
    /// 回到刚打开时的样子（ShellVm.ResetView：截图的每个场景、外框检查）：展开状态回到默认（≤ 8 路展开、16 路收起），树滚回顶部（<see cref="ScrollResetRequested"/>）——
    /// 之后选中的行照样按样稿 xReveal 滚到可见处，结果就与新打开的页面相同（复核：以前留着上一个场景的滚动位置，选中的行可能不滚进来）。
    /// </summary>
    internal void ResetView()
    {
        _open.Clear();
        ScrollResetRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>请视图把树滚回顶部（<see cref="ResetView"/>）。</summary>
    public event EventHandler? ScrollResetRequested;

    /// <summary>
    /// 展开状态回到默认并刷新（视图 › 重置布局，样稿 resetLayout 的 xOpen = {}）：不滚动、不跟随 ——
    /// 选中的反应釜在收起的设备里时由设备头标出（HasSel），同样稿。
    /// </summary>
    public void ResetOpen()
    {
        _open.Clear();
        Refresh();
    }

    /// <summary>
    /// 双击：打开设备页 / 反应釜页（ShellVm.Navigate 同时选中它、收起浮层）。<paramref name="keepFloat"/>：浮层资源管理器不收起
    /// （键盘 Enter，样稿 treeKey 只导航；指针的双击照旧收起）。
    /// </summary>
    public void Open(XRow row, bool keepFloat = false)
    {
        switch (row)
        {
            case XDeviceRow d: _shell.Navigate(Route.Device(d.D.K), keepFloat); break;
            case XReactorRow r: _shell.Navigate(Route.Reactor(r.D.K, r.R.N), keepFloat); break;
        }
    }

    /// <summary>选中的反应釜在收起的设备里时展开它（样稿 xReveal）；ShellVm 选中对象后调用。之后视图把选中行滚到可见处（<see cref="RevealRequested"/>）。</summary>
    internal void Reveal(int k, int? n)
    {
        if (n != null && _shell.Fleet.Device(k) is { } d && !IsOpen(d)) _open[k] = true;
        RevealRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>请视图把选中行滚到可见处（样稿 xReveal：上面留 6、下面留 22 —— 树底部有 18 px 渐隐）。只在选中 / 导航时，不在每次刷新时（不打断用户自己的滚动）。</summary>
    public event EventHandler? RevealRequested;

    internal void RequestReveal() => RevealRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>按当前数据、展开状态与选中对象刷新（结构变了才增删行）。</summary>
    internal void Refresh()
    {
        var fleet = _shell.Fleet;
        var devs = fleet.Devices;
        CountText = ShellText.Units(devs.Count);
        bool fl = _shell.Layout.ExplorerFloat;
        CloseIcon = fl ? "x" : "collapse";
        CloseTip = fl ? "关闭" : "收起资源管理器";
        IsEmpty = devs.Count == 0;

        var want = new List<XRow>();
        XGapRow Gap(string key, double h, double line)
        {
            var g = Get(key, () => new XGapRow(key));
            g.Set(h, line);
            return g;
        }
        var sel = _shell.Selection;
        for (int i = 0; i < devs.Count; i++)
        {
            var d = devs[i];
            bool open = IsOpen(d), last = i == devs.Count - 1;
            if (i == 0) want.Add(Gap("s", 2, -1));
            var head = Get("d" + d.K, () => new XDeviceRow(d, Toggle), x => ReferenceEquals(x.D, d));
            head.Update(open, sel.IsDevice(d.K), !open && sel.K == d.K && sel.N != null, sel.K == d.K ? sel.N : null);
            want.Add(head);
            if (d.IsOffline)
            {
                want.Add(Get("o" + d.K, () => new XOffRow(d, false), x => ReferenceEquals(x.D, d)));
                if (d.GraceText != null) want.Add(Get("g" + d.K, () => new XOffRow(d, true), x => ReferenceEquals(x.D, d)));
            }
            double bottom = 6;
            if (open)
            {
                want.Add(Gap("k" + d.K, 2, -1));
                var rs = d.Reactors;
                for (int j = 0; j < rs.Count; j++)
                {
                    var r = rs[j];
                    if (r.Installed)
                    {
                        var row = Get("r" + d.K + "/" + r.N, () => new XReactorRow(d, r), x => ReferenceEquals(x.R, r));
                        row.IsSelected = sel.IsReactor(d.K, r.N);
                        want.Add(row);
                        continue;
                    }
                    int e = j;
                    while (e + 1 < rs.Count && !rs[e + 1].Installed) e++;
                    int from = r.N, to = rs[e].N;
                    var key = "u" + d.K + "/" + from;
                    if (_rows.TryGetValue(key, out var old) && old is XUninstalledRow ur && ur.To != to) _rows.Remove(key);
                    want.Add(Get(key, () => new XUninstalledRow(d.K, from, to)));
                    j = e;
                }
                bottom += 2;
            }
            want.Add(last ? Gap("e" + d.K, bottom, -1) : Gap("e" + d.K, bottom + 1 + 2, bottom));
        }
        Sync(want);

        // 列表的选中行（键盘、无障碍）：选中设备头 / 反应釜行；选中的反应釜在收起的设备里时没有选中行
        _syncingSelection = true;
        try { SelectedRow = want.FirstOrDefault(r => r.IsSelected && r.Selectable); }
        finally { _syncingSelection = false; }
        Refreshed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>刷新完了（行、选中行都是新的）：视图按 <see cref="TabTarget"/> 重设树的 Tab 停靠点。</summary>
    public event EventHandler? Refreshed;

    private T Get<T>(string key, Func<T> make, Func<T, bool>? valid = null) where T : XRow
    {
        if (_rows.TryGetValue(key, out var r) && r is T t && (valid == null || valid(t))) return t;
        t = make();
        _rows[key] = t;
        return t;
    }

    /// <summary>把 Rows 变成 want（同一个实例就移动、没有的插入、多余的去掉）：绑定的容器尽量不重建。</summary>
    private void Sync(List<XRow> want)
    {
        var keep = new HashSet<XRow>(want);
        for (int i = Rows.Count - 1; i >= 0; i--) if (!keep.Contains(Rows[i])) Rows.RemoveAt(i);
        for (int i = 0; i < want.Count; i++)
        {
            var r = want[i];
            if (i < Rows.Count && ReferenceEquals(Rows[i], r)) continue;
            int at = Rows.IndexOf(r);
            if (at >= 0) Rows.Move(at, i); else Rows.Insert(i, r);
        }
        foreach (var k in _rows.Keys.Where(k => !keep.Contains(_rows[k])).ToList()) _rows.Remove(k);
    }
}
