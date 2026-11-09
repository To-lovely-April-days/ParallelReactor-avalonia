#if DEBUG
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ParallelReactor.Control.Behaviors;
using ParallelReactor.Control.Controls;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Core.Text;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Shot;

/// <summary>
/// 主题核对表（PR_CONTROL_DEMO=1 + PR_CONTROL_THEME_SHEET=1，仅 Debug）：陈列页看不到的东西 ——
/// 各按钮 / 切换 / 输入主题的 悬停 · 按下 · 焦点 · 禁用 · 原因 状态（悬停 / 焦点伪类直接设上；按下列设 Button.IsPressed —— 与真实按下走同一条路径，
/// 直接设 :pressed 伪类会被 IsPressed 盖掉、画出来其实是悬停（M1 复核），所以另在行为核对里用真实指针按下核对颜色），
/// 菜单 / 右键菜单 / 弹出框 / 提示框 / 列表行 / 文档页签 / 图标栏 / 对话框外壳，以及 BarFitPanel、FitText、SizeClasses、Keep 断行。截图 theme-sheet-&lt;w&gt;.png + 审计；
/// 再用无头输入核对行为（Pr.Reason 拦住鼠标 / 键盘 / IsDefault / IsCancel / HotKey / UI 自动化、NumBox 限幅与坏值、分段 / 页签 / 芯片、
/// 选项与步骤段就地更新、面板放大关闭…），并把真正弹出的下拉 / 子菜单 / 提示框单独拍下来，结果写 theme-sheet-&lt;w&gt;.checks.txt（每行 PASS / FAIL / INFO）。
/// 整张表冻结（.frozen：没有过渡、进度条不呼吸），截图可复现。
/// </summary>
public static class ThemeSheet
{
    private static ControlTheme T(string key) => (ControlTheme)Application.Current!.FindResource(key)!;

    private static void Pseudo(StyledElement e, params string[] classes)
    {
        foreach (var c in classes) ((IPseudoClasses)e.Classes).Set(c, true);
    }

    /// <summary>按下态：设 Button.IsPressed（私有 setter；按钮自己据此设 :pressed，与真实按下同一条路径，表里没有指针事件所以一直保持）。</summary>
    private static void Press(Button b) =>
        typeof(Button).GetProperty(nameof(Button.IsPressed))!.GetSetMethod(nonPublic: true)!.Invoke(b, new object[] { true });

    private static TextBlock Head(string s) => new() { Text = s, FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Palette.T1Brush, Margin = new Thickness(0, 18, 0, 8) };
    private static TextBlock Cap(string s) => new() { Text = s, FontSize = 14, Foreground = Palette.T3Brush, VerticalAlignment = VerticalAlignment.Center };

    // 行为核对要用到的控件
    private static Button _reasonBtn = null!, _plainBtn = null!;
    private static NumBox _num = null!, _numLocked = null!;
    private static SegControl _seg = null!;
    private static PTabs _ptabs = null!;
    private static Chip _chip = null!, _chipLocked = null!;
    private static PrPanel _panel = null!;
    private static BarFitPanel _fitWide = null!, _fitNarrow = null!;
    private static readonly List<(double W, FitText F)> _fits = new();
    private static Border _scNarrow = null!, _scWide = null!;
    private static ComboBox _combo = null!;
    private static MenuItem _subMenu = null!;
    private static Button _tipBtn = null!, _plainTipBtn = null!;
    private static int _reasonClicks, _plainClicks;

    /// <summary>断行核对用的报警句子（PR.keep 的例子）。</summary>
    public const string AlarmText = "RV11 超压 3.12 MPa ≥ 3.00 MPa · 已关闭 SV11";

    public static Avalonia.Controls.Control Build()
    {
        var root = new StackPanel { Margin = new Thickness(24, 8, 24, 24) };
        root.Styles.Add(new StyleInclude(new Uri("avares://ParallelReactor.Control/")) { Source = new Uri("avares://ParallelReactor.Control/Styles/Frozen.axaml") });
        // SizeClasses 演示的两种外观（只在本表里）
        root.Styles.Add(new Style(x => x.OfType<Border>().Class("sc-narrow")) { Setters = { new Setter(Border.BackgroundProperty, Palette.ASoftBrush) } });
        root.Styles.Add(new Style(x => x.OfType<Border>().Class("sc-wide")) { Setters = { new Setter(Border.BackgroundProperty, Palette.G3Brush) } });
        root.Resources[typeof(ToolTip)] = new ControlTheme(typeof(ToolTip)) { BasedOn = T("PrTip") };

        // ---------------- 按钮状态 ----------------
        root.Children.Add(Head("按钮：默认 · 悬停 · 按下 · 焦点 · 禁用 · 原因"));
        var states = new[] { "默认", "悬停", "按下", "焦点", "禁用", "原因" };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("150," + string.Join(",", states.Select(_ => "Auto"))) };
        int row = 0;
        void AddRow(string theme, string? icon, string? text, string? cls = null)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var cap = Cap(theme + (cls != null ? "." + cls : ""));
            Grid.SetRow(cap, row);
            grid.Children.Add(cap);
            for (int i = 0; i < states.Length; i++)
            {
                var b = new Button { Theme = T(theme), Content = text, Margin = new Thickness(8, 6) };
                if (cls != null) b.Classes.Add(cls);
                if (icon != null) Pr.SetIcon(b, icon);
                if (text == null) ToolTip.SetTip(b, theme);
                switch (i)
                {
                    case 1: Pseudo(b, ":pointerover"); break;
                    case 2: Pseudo(b, ":pointerover"); Press(b); break;
                    case 3: Pseudo(b, ":focus", ":focus-visible"); break;
                    case 4: b.IsEnabled = false; break;
                    case 5: Pr.SetReason(b, "先申请控制权"); break;
                }
                Grid.SetRow(b, row); Grid.SetColumn(b, i + 1);
                grid.Children.Add(b);
            }
            row++;
        }
        AddRow("PrButton", "edit", "编辑程序");
        AddRow("PrPrimary", "key", "申请控制权");
        AddRow("PrPrimary", "stop", "全部停止", "stop");
        AddRow("PrStop", "stop", "停止加热");
        AddRow("PrEstopOutline", "estop", "急停");
        AddRow("PrDanger", "estop", "急停", "estop");
        AddRow("PrGhost", "x", "全不选", "sm");
        AddRow("PrLink", null, "回到 1号机 · RV1");
        AddRow("PrIconButton", "zoomin", null);
        AddRow("PrIconButton", "stop", null, "stop");
        AddRow("PrIconButtonS", "x", null);
        root.Children.Add(grid);

        // ---------------- 切换 ----------------
        root.Children.Add(Head("切换：分段项 · 芯片 · 面板页签 · 复选（默认 / 悬停 / 按下 / 选中 / 焦点 / 原因）"));
        var tg = new WrapPanel();
        foreach (var theme in new[] { "PrSegItem", "PrChip", "PrPTab", "PrCheck" })
        {
            for (int i = 0; i < 6; i++)
            {
                var t = new PrToggle { Theme = T(theme), Content = theme == "PrPTab" ? "报警与事件" : theme == "PrCheck" ? "自动连接" : "RV" + (i + 1), Margin = new Thickness(6), Height = theme == "PrPTab" ? 41 : double.NaN };
                if (theme == "PrSegItem") t.Height = 30;
                switch (i)
                {
                    case 1: Pseudo(t, ":pointerover"); break;
                    case 2: Pseudo(t, ":pointerover"); Press(t); break;
                    case 3: t.IsChecked = true; break;
                    case 4: t.IsChecked = true; Pseudo(t, ":focus", ":focus-visible"); break;
                    case 5: Pr.SetReason(t, "未安装"); break;
                }
                if (theme == "PrPTab" && i == 3) Pr.SetCount(t, "2");
                if (theme == "PrPTab" && i == 4) Pr.SetDot(t, true);
                tg.Children.Add(t);
            }
        }
        root.Children.Add(tg);

        // 文档页签 + 图标栏
        root.Children.Add(Head("文档页签（固定 / 当前 / 悬停 / 红点）· 图标栏（设备 / 当前 / 悬停 / 按下）"));
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Height = 44, Background = Palette.BgHeadBrush };
        var close = new CommunityToolkit.Mvvm.Input.RelayCommand(() => { });
        foreach (var (label, icon, mode) in new[] { ("总览", "overview", 0), ("1号机", "machine", 1), ("1号机 · RV1", "flask", 2), ("2号机", "machine", 3) })
        {
            var t = new ToggleButton { Theme = T("PrDocTab"), Content = label };
            Pr.SetIcon(t, icon);
            if (mode == 0) t.Classes.Add("pinned"); else Pr.SetCloseCommand(t, close);
            if (mode == 1) t.IsChecked = true;
            if (mode == 2) Pseudo(t, ":pointerover");
            if (mode == 3) Pr.SetDot(t, true);
            tabs.Children.Add(t);
        }
        var side = new StackPanel { Width = 64, Background = (IBrush)Application.Current!.FindResource("SideGradientBrush")! };
        foreach (var (label, icon, mode) in new[] { ("设备", "devices", 0), ("总览", "overview", 1), ("程序", "program", 2), ("报警", "bell", 3) })
        {
            var t = new ToggleButton { Theme = T("PrSideItem"), Content = label };
            Pr.SetIcon(t, icon);
            if (mode == 1) t.IsChecked = true;
            if (mode == 2) Pseudo(t, ":pointerover");
            if (mode == 3) { Pseudo(t, ":pointerover"); Press(t); }
            side.Children.Add(t);
        }
        root.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24, Children = { new Border { VerticalAlignment = VerticalAlignment.Top, BorderBrush = Palette.LineStrongBrush, BorderThickness = new Thickness(0, 0, 0, 1), Child = tabs }, side } });

        // ---------------- 输入状态 ----------------
        root.Children.Add(Head("输入：默认 / 悬停 / 聚焦 / 只读 / 禁用 · 数值框聚焦 · 下拉（展开态 / 禁用）"));
        var inputs = new WrapPanel();
        for (int i = 0; i < 5; i++)
        {
            var tb = new TextBox { Theme = T("PrInput"), Text = i == 3 ? "PT-20261108-0930" : "192.168.1.20", Width = 200, Margin = new Thickness(8) };
            if (i == 1) Pseudo(tb, ":pointerover");
            if (i == 2) Pseudo(tb, ":focus-within");
            if (i == 3) tb.IsReadOnly = true;
            if (i == 4) tb.IsEnabled = false;
            inputs.Children.Add(tb);
        }
        var nf = new NumBox { Value = 120, Unit = "℃", Decimals = 1, Width = 200, Margin = new Thickness(8) };
        Pseudo(nf, ":focus-within");
        inputs.Children.Add(nf);
        var c1 = new ComboBox { Theme = T("PrSelect"), Width = 200, Margin = new Thickness(8), Items = { "加氢-标准", "高压加氢-150" } };
        c1.SelectedIndex = 0;
        Pseudo(c1, ":dropdownopen");
        inputs.Children.Add(c1);
        var c2 = new ComboBox { Theme = T("PrSelect"), Width = 200, Margin = new Thickness(8), Items = { "加氢-标准" } };
        c2.SelectedIndex = 0;
        Pr.SetReason(c2, "先申请控制权");
        inputs.Children.Add(c2);
        var c3 = new ComboBox { Theme = T("PrSelect"), Width = 200, Margin = new Thickness(8), PlaceholderText = "选择程序" };
        inputs.Children.Add(c3);
        root.Children.Add(inputs);

        // ---------------- 菜单 / 弹出 / 提示 ----------------
        root.Children.Add(Head("菜单栏 · 下拉面板（标题 / 图标 / 勾选 / 分隔 / 子菜单 / 危险 / 停止 / 原因 / 悬停）· 弹出框 · 提示框"));
        var menu = new Menu { Theme = T("PrMenu"), Background = Palette.BgCardBrush };
        foreach (var h in new[] { "文件", "设备", "视图", "程序", "工具", "窗口", "帮助" }) menu.Items.Add(new MenuItem { Header = h });
        Pseudo((MenuItem)menu.Items[1]!, ":pointerover");
        var flyout = new MenuFlyoutPresenter { Theme = T("PrMenuFlyout"), Width = 280, HorizontalAlignment = HorizontalAlignment.Left };
        flyout.Items.Add(new MenuItem { Header = "1号机", Classes = { "head" } });
        var mOpen = new MenuItem { Header = "打开设备页" }; Pr.SetIcon(mOpen, "open"); flyout.Items.Add(mOpen);
        var mHover = new MenuItem { Header = "新页签打开 RV1" }; Pr.SetIcon(mHover, "flask"); Pseudo(mHover, ":pointerover"); flyout.Items.Add(mHover);
        flyout.Items.Add(new MenuItem { Header = "显示设定值", ToggleType = MenuItemToggleType.CheckBox, IsChecked = true });
        flyout.Items.Add(new MenuItem { Header = "-" });
        _subMenu = new MenuItem { Header = "切换型号", Items = { new MenuItem { Header = "HT-PR8" }, new MenuItem { Header = "HT-PR4" } } };
        Pr.SetIcon(_subMenu, "tag"); flyout.Items.Add(_subMenu);
        var mStop = new MenuItem { Header = "全部停止", Classes = { "stop" } }; Pr.SetIcon(mStop, "stop"); flyout.Items.Add(mStop);
        var mEstop = new MenuItem { Header = "急停 1号机…", Classes = { "danger" } }; Pr.SetIcon(mEstop, "estop"); flyout.Items.Add(mEstop);
        var mReason = new MenuItem { Header = "下发程序" }; Pr.SetIcon(mReason, "send"); Pr.SetReason(mReason, "先申请控制权"); flyout.Items.Add(mReason);
        var ctx = new ContextMenu { Theme = T("PrContextMenu"), Width = 240, HorizontalAlignment = HorizontalAlignment.Left, Items = { new MenuItem { Header = "断开连接…" }, new MenuItem { Header = "立即重连" } } };
        var pop = new FlyoutPresenter { Theme = T("PrPopover"), Content = new TextBlock { Text = "图例：浅绿点 = 运行中", Margin = new Thickness(16, 14), FontSize = 15, Foreground = Palette.T2Brush }, HorizontalAlignment = HorizontalAlignment.Left };
        var tip = new ToolTip { Theme = T("PrTip"), Content = "1号机 · RV1 · 反应中\n119.8 ℃ · 0.349 MPa", HorizontalAlignment = HorizontalAlignment.Left };
        var reasonTip = Pr.ReasonTip("2号机为本地只读 · 先申请控制权");
        reasonTip.HorizontalAlignment = HorizontalAlignment.Left;
        root.Children.Add(new StackPanel { Spacing = 12, Children = { new Border { BorderBrush = Palette.LineMenuBrush, BorderThickness = new Thickness(0, 0, 0, 1), Height = 34, Child = menu } } });
        root.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24, Margin = new Thickness(0, 12, 0, 0), Children = { flyout, new StackPanel { Spacing = 16, Children = { ctx, pop, tip, reasonTip } } } });

        // ---------------- 列表 ----------------
        root.Children.Add(Head("列表：树行（选中 / 悬停 / 焦点）· 表格行（选中 / 悬停）"));
        var tree = new ListBox { Theme = T("PrListBox"), Width = 300, ItemContainerTheme = T("PrTreeRow") };
        foreach (var s in new[] { "RV1 反应中", "RV2 升温中", "RV3 停用", "RV4 已结束" }) tree.Items.Add(new ListBoxItem { Theme = T("PrTreeRow"), Content = s });
        tree.SelectedIndex = 0;
        Pseudo((ListBoxItem)tree.Items[1]!, ":pointerover");
        Pseudo((ListBoxItem)tree.Items[2]!, ":focus-visible");
        var table = new ListBox { Theme = T("PrListBox"), Width = 360 };
        foreach (var s in new[] { "1号机   HT-PR8 · 现场", "2号机   HT-PR4", "3号机   HT-PR16" }) table.Items.Add(new ListBoxItem { Theme = T("PrTableRow"), Padding = new Thickness(12, 0), Content = s });
        table.SelectedIndex = 1;
        Pseudo((ListBoxItem)table.Items[2]!, ":pointerover");
        root.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24, Children = { new Border { Background = Palette.BgSideBrush, Child = tree }, table } });

        // ---------------- 对话框外壳 ----------------
        root.Children.Add(Head("对话框外壳（中性 / 急停）"));
        var dlg1 = new DialogFrame
        {
            Title = "添加设备", Sub = "填写触屏端的地址与令牌", Icon = "plus", Width = 420,
            Content = new TextBlock { Text = "地址、端口、令牌（只在勾选「记住令牌」时保存）。", TextWrapping = TextWrapping.Wrap },
            Footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { new Button { Theme = T("PrButton"), Content = "取消" }, new Button { Theme = T("PrPrimary"), Content = "添加" } } },
        };
        var dlg2 = new DialogFrame
        {
            Title = "急停", Sub = "立即停止所选设备的加热、搅拌与进气", Icon = "estop", IsDanger = true, Width = 420,
            Content = new TextBlock { Text = "急停不需要控制权，连接正常就会发送。", TextWrapping = TextWrapping.Wrap },
            FooterLeft = new TextBlock { Text = "复位在触屏" },
            Footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { new Button { Theme = T("PrButton"), Content = "取消" }, new Button { Theme = T("PrDanger"), Content = "急停" } } },
        };
        Pr.SetIcon((Button)((StackPanel)dlg2.Footer).Children[1], "estop");
        root.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 32, Margin = new Thickness(0, 4, 0, 24), Children = { dlg1, dlg2 } });

        // ---------------- 布局工具 ----------------
        root.Children.Add(Head("BarFitPanel（宽 300 / 窄 90）· FitText（140 / 110 / 80 / 50）· SizeClasses（250 / 350）"));
        StepBar Bar8()
        {
            var b = new StepBar { IsRunning = true };
            for (int i = 0; i < 8; i++) b.Segments.Add(new StepSeg { Label = "S" + i, State = i < 3 ? StepSegState.Done : i == 3 ? StepSegState.Current : StepSegState.Pending, Fraction = 0.5 });
            return b;
        }
        _fitWide = new BarFitPanel { Width = 300, Children = { Bar8(), new TextBlock { Text = "剩 42 min", FontSize = 14, Foreground = Palette.T3Brush } } };
        _fitNarrow = new BarFitPanel { Width = 90, Children = { Bar8(), new TextBlock { Text = "剩 42 min", FontSize = 14, Foreground = Palette.T3Brush } } };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24 };
        tools.Children.Add(new StackPanel { Spacing = 10, Children = { new Border { Background = Palette.BgCardBrush, Padding = new Thickness(6), Child = _fitWide }, new Border { Background = Palette.BgCardBrush, Padding = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Left, Child = _fitNarrow } } });
        var fits = new StackPanel { Spacing = 6 };
        _fits.Clear();
        foreach (var w in new double[] { 140, 110, 80, 50 })
        {
            var f = new FitText { Text = "119.8", Unit = "℃", Sizes = "26,24,22,20", Classes = { "num" } };
            _fits.Add((w, f));
            fits.Children.Add(new Border { Width = w, HorizontalAlignment = HorizontalAlignment.Left, BorderBrush = Palette.LineFaintBrush, BorderThickness = new Thickness(1), Child = f });
        }
        tools.Children.Add(fits);
        _scNarrow = new Border { Width = 250, Height = 40, Child = Cap("w=250") };
        _scWide = new Border { Width = 350, Height = 40, Child = Cap("w=350") };
        SizeClasses.SetRules(_scNarrow, "sc-narrow: w < 300; sc-wide: w >= 300");
        SizeClasses.SetRules(_scWide, "sc-narrow: w < 300; sc-wide: w >= 300");
        tools.Children.Add(new StackPanel { Spacing = 8, Children = { _scNarrow, _scWide } });
        // 分区小标题（右侧徽标）与徽标版状态
        tools.Children.Add(new StackPanel
        {
            Spacing = 8, Width = 300,
            Children =
            {
                new Subhead { Title = "加热", Right = new Badge { Text = "运行 4/4", Tone = BadgeTone.Ok } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { new StateBadge { Key = StateKey.React, Classes = { "badge" } }, new StateBadge { Key = StateKey.Done, Classes = { "badge" } }, new StateBadge { Key = StateKey.Offline, NeverConnected = true, Classes = { "badge" } } } },
            },
        });
        root.Children.Add(tools);

        // ---------------- 断行（Core.Text.Keep）----------------
        root.Children.Add(Head($"断行：原文 / Keep.Web（NBSP）/ UiText.Keep（+ 连接符 U+{(UiText.Joiner.Length > 0 ? ((int)UiText.Joiner[0]).ToString("X4") : "无")}），宽 160 与 240"));
        var brk = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24 };
        foreach (var w in new double[] { 160, 240 })
            foreach (var (name, text) in new[] { ("原文", AlarmText), ("Web", Keep.Web(AlarmText)), ("Apply", UiText.Keep(AlarmText)) })
                brk.Children.Add(new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        Cap(name + " · " + w),
                        new Border { Width = w, BorderBrush = Palette.LineFaintBrush, BorderThickness = new Thickness(1), Child = new TextBlock { Name = "keep-" + name + "-" + w, Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14, Foreground = Palette.T2Brush } },
                    },
                });
        root.Children.Add(brk);

        // ---------------- 行为核对用的控件 ----------------
        root.Children.Add(Head("行为核对（无头鼠标点击）"));
        _reasonBtn = new Button { Theme = T("PrButton"), Content = "启动加热（原因）" };
        Pr.SetIcon(_reasonBtn, "flame");
        Pr.SetReason(_reasonBtn, "先申请控制权");
        _reasonBtn.Click += (_, _) => _reasonClicks++;
        _plainBtn = new Button { Theme = T("PrButton"), Content = "编辑程序" };
        _plainBtn.Click += (_, _) => _plainClicks++;
        _num = new NumBox { Value = 199.5, Minimum = 0, Maximum = 200, Step = 1, Decimals = 1, Unit = "℃", Width = 200 };
        _numLocked = new NumBox { Value = 300, Unit = "rpm", Width = 200 };
        Pr.SetReason(_numLocked, "先申请控制权");
        _seg = new SegControl { Value = "10", Options = { new SegOption { Value = "10", Label = "10" }, new SegOption { Value = "30", Label = "30" }, new SegOption { Value = "60", Label = "60 min" } } };
        _ptabs = new PTabs { Value = "a", Options = { new SegOption { Value = "a", Label = "设备信息" }, new SegOption { Value = "b", Label = "报警与事件" } } };
        _chip = new Chip { Content = "RV2" };
        _chipLocked = new Chip { Content = "RV5" };
        Pr.SetReason(_chipLocked, "未安装");
        _combo = new ComboBox { Theme = T("PrSelect"), Width = 220, Items = { new ComboBoxItem { Theme = T("PrSelectItem"), Content = "加氢-标准" }, new ComboBoxItem { Theme = T("PrSelectItem"), Content = "高压加氢-150" }, new ComboBoxItem { Theme = T("PrSelectItem"), Content = "吹扫" } } };
        _combo.SelectedIndex = 0;
        _plainTipBtn = new Button { Theme = T("PrIconButton") };
        Pr.SetIcon(_plainTipBtn, "info");
        ToolTip.SetTip(_plainTipBtn, "普通文字提示");
        _tipBtn = new Button { Theme = T("PrIconButton") };
        Pr.SetIcon(_tipBtn, "open");
        Pr.SetReason(_tipBtn, "不可用 · 先连接设备");
        var probes = new WrapPanel { Children = { _reasonBtn, _plainBtn, _num, _numLocked, _seg, _ptabs, _chip, _chipLocked, _combo, _tipBtn, _plainTipBtn } };
        foreach (var c in probes.Children) c.Margin = new Thickness(8);
        root.Children.Add(probes);
        _panel = new PrPanel { Title = "属性查看器", Sub = "面板标题栏 42 px", Icon = "tree", Height = 120, Width = 520, HorizontalAlignment = HorizontalAlignment.Left, Content = Cap("内容") };
        root.Children.Add(new Border { BorderBrush = Palette.Line2Brush, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, Child = _panel });

        var outer = new Border { Background = Palette.BgAppBrush, Child = root };
        Frozen.Apply(outer);
        return outer;
    }

    // ---------------- 行为核对 ----------------

    private static Point Center(Visual v, Window w) => v.TranslatePoint(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2), w) ?? default;

    private static void Click(Window w, Visual v)
    {
        var p = Center(v, w);
        w.MouseMove(p);
        w.MouseDown(p, MouseButton.Left);
        w.MouseUp(p, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    public static async Task<(int Fails, List<string> Lines)> CheckAsync(Window window, string dir, int width)
    {
        var lines = new List<string>();
        int fails = 0;
        void Check(bool ok, string what) { lines.Add((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }
        void Info(string s) => lines.Add("INFO " + s);

        // Pr.Reason：吞掉点击、打开原因提示；普通按钮照常 Click
        Click(window, _reasonBtn);
        Check(_reasonClicks == 0, $"Pr.Reason 吞掉点击（Click 次数 {_reasonClicks}）");
        Check(ToolTip.GetIsOpen(_reasonBtn), "Pr.Reason 点击后打开原因提示（M1 没有 toast 宿主）");
        Check(_reasonBtn.Classes.Contains(":reason"), "Pr.Reason 设上 :reason 伪类");
        ToolTip.SetIsOpen(_reasonBtn, false);
        Click(window, _plainBtn);
        Check(_plainClicks == 1, $"普通按钮点击（Click 次数 {_plainClicks}）");

        // NumBox：+ 限幅到 200.0；− 199.0；有原因时不动
        var plus = _num.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PART_Plus");
        var minus = _num.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PART_Minus");
        var input = _num.GetVisualDescendants().OfType<TextBox>().First();
        Click(window, plus);
        Check(_num.Value == 200 && input.Text == "200.0", $"NumBox + 限幅到上限（{_num.Value} / 「{input.Text}」）");
        Click(window, minus);
        Check(_num.Value == 199 && input.Text == "199.0", $"NumBox − 步进 1、1 位小数（{_num.Value} / 「{input.Text}」）");
        var lockedPlus = _numLocked.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PART_Plus");
        Click(window, lockedPlus);
        Check(_numLocked.Value == 300, $"NumBox 有原因时 + 不动（{_numLocked.Value}）");

        // 分段 / 页签 / 芯片
        var segBtns = _seg.GetVisualDescendants().OfType<ToggleButton>().ToList();
        Click(window, segBtns[2]);
        Check(Equals(_seg.Value?.ToString(), "60") && segBtns[2].IsChecked == true && segBtns[0].IsChecked == false, $"SegControl 点第 3 段 → Value {_seg.Value}");
        Click(window, segBtns[2]);
        Check(segBtns[2].IsChecked == true, "SegControl 再点当前段仍保持选中");
        var tabBtns = _ptabs.GetVisualDescendants().OfType<ToggleButton>().ToList();
        Click(window, tabBtns[1]);
        Check(Equals(_ptabs.Value?.ToString(), "b") && tabBtns[1].IsChecked == true, $"PTabs 点第 2 页签 → Value {_ptabs.Value}");
        Click(window, _chip);
        Check(_chip.IsChecked == true, "Chip 点击切换为开");
        Click(window, _chipLocked);
        Check(_chipLocked.IsChecked != true, "Chip 有原因时点击不切换");

        // 面板：放大 / 关闭
        var max = _panel.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PART_Max");
        Click(window, max);
        Check(_panel.IsMaximized && _panel.MaxIcon == "restore", "PrPanel 放大按钮 → IsMaximized、图标换成 restore");
        var close = _panel.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PART_Close");
        Click(window, close);
        Dispatcher.UIThread.RunJobs();
        Check(_panel.IsClosed && !_panel.IsVisible, "PrPanel 关闭 → IsClosed、不可见");

        // BarFitPanel / FitText / SizeClasses
        Check(!_fitWide.IsBarFit && _fitNarrow.IsBarFit, $"BarFitPanel：宽 300 显示文字（{_fitWide.IsBarFit}），窄 90 藏文字（{_fitNarrow.IsBarFit}）");
        var narrowBar = (StepBar)_fitNarrow.Children[0];
        Check(narrowBar.Bounds.Width >= StepBar.MinBarWidth(8) - 0.5, $"窄行里步骤条不窄于 8 段的最小宽度（{narrowBar.Bounds.Width:0.#} ≥ {StepBar.MinBarWidth(8)}）");
        Check(ToolTip.GetTip(_fitNarrow) as string == "剩 42 min", "BarFitPanel 藏文字时整行的提示是全文");
        double prev = double.MaxValue;
        bool mono = true;
        foreach (var (w, f) in _fits)
        {
            Info($"FitText 宽 {w}: 字号 {f.ChosenSize} · 带单位 {f.ChosenWithUnit} · 提示 {ToolTip.GetTip(f) ?? "无"}");
            if (f.ChosenSize > prev) mono = false;
            prev = f.ChosenSize;
        }
        Check(mono && _fits[0].F.ChosenWithUnit && !_fits[^1].F.ChosenWithUnit && ToolTip.GetTip(_fits[^1].F) as string == "119.8 ℃",
            "FitText：越窄字号越小，最窄时去掉单位并给出全文提示");
        Check(_scNarrow.Classes.Contains("sc-narrow") && !_scNarrow.Classes.Contains("sc-wide") && _scWide.Classes.Contains("sc-wide"),
            $"SizeClasses：250 → {string.Join(" ", _scNarrow.Classes)}；350 → {string.Join(" ", _scWide.Classes)}");

        // Keep：单行宽度 Apply == Web（U+2060 零宽）；换行只发生在空格 / 「 · 」之后，「3.12 MPa ≥ 3.00 MPa」「已关闭 SV11」不拆
        var face = new Typeface(UiFonts.Create());
        double Width1(string s)
        {
            using var l = new Avalonia.Media.TextFormatting.TextLayout(s, face, 14, null);
            return l.WidthIncludingTrailingWhitespace;
        }
        double wRaw = Width1(AlarmText), wWeb = Width1(Keep.Web(AlarmText)), wApply = Width1(UiText.Keep(AlarmText));
        Info($"Keep 单行宽：原文 {wRaw:0.0} · Web {wWeb:0.0} · Apply {wApply:0.0}");
        Info($"UiText 连接符：U+{(UiText.Joiner.Length > 0 ? ((int)UiText.Joiner[0]).ToString("X4") : "（无）")}（字体 {UiFonts.Stack()}；U+2060 {(UiText.ProbeJoiner(UiFonts.Create()) == "\u2060" ? "可用" : "会画方块")}）");
        Check(UiText.Joiner.Length > 0, "UiText：当前字体下有可用的连接符（U+2060 或 U+FEFF）");
        Check(Math.Abs(wApply - wWeb) < 0.6, "UiText.Keep 与 Keep.Web 单行宽度相同（连接符不占宽）");
        Check(!HasNotdef(UiText.Keep(AlarmText), face), "UiText.Keep 的文字没有方块（.notdef）");
        foreach (var w in new double[] { 160, 240 })
        {
            foreach (var (name, text) in new[] { ("Web", Keep.Web(AlarmText)), ("Apply", UiText.Keep(AlarmText)) })
            {
                using var l = new Avalonia.Media.TextFormatting.TextLayout(text, face, 14, null, textWrapping: TextWrapping.Wrap, maxWidth: w);
                var parts = l.TextLines.Select(t => UiText.Plain(text.Substring(t.FirstTextSourceIndex, t.Length)).TrimEnd()).ToList();
                Info($"Keep.{name} @ {w}: " + string.Join(" ⏎ ", parts));
                if (name == "Apply")
                {
                    // 「已关闭」「超压」不拆开；240 宽时正好在「 · 」后换行（160 宽时整段「超压 3.12 MPa ≥ 3.00 MPa ·」比行宽还长，只能紧急断开）
                    bool ok = !parts.Any(p => p.EndsWith("已关", StringComparison.Ordinal) || p.EndsWith("已", StringComparison.Ordinal) || p.EndsWith("超", StringComparison.Ordinal))
                              && (w < 200 || (parts.Count == 2 && parts[0].EndsWith("·", StringComparison.Ordinal)));
                    Check(ok, $"UiText.Keep @ {w}：中文词与「已关闭 SV11」不拆开" + (w < 200 ? "" : "，正好在「 · 」后换行"));
                }
            }
        }

        // 真正弹出的东西：下拉、子菜单、原因提示
        _combo.IsDropDownOpen = true;
        await Settle();
        var popup = _combo.GetVisualDescendants().OfType<Popup>().FirstOrDefault();
        Check(popup?.IsOpen == true && popup.Child != null, "PrSelect 下拉打开");
        if (popup?.Child is Avalonia.Controls.Control pc) Save(pc, Path.Combine(dir, $"theme-popup-select-{width}.png"), lines);
        _combo.IsDropDownOpen = false;

        _subMenu.IsSubMenuOpen = true;
        await Settle();
        var subPopup = _subMenu.GetVisualDescendants().OfType<Popup>().FirstOrDefault();
        Check(subPopup?.IsOpen == true, "PrMenuItem 子菜单打开");
        if (subPopup?.Child is Avalonia.Controls.Control sc) Save(sc, Path.Combine(dir, $"theme-popup-submenu-{width}.png"), lines);
        _subMenu.IsSubMenuOpen = false;

        ToolTip.SetIsOpen(_tipBtn, true);
        await Settle();
        var tipCtl = ToolTip.GetTip(_tipBtn) as ToolTip;
        Check(tipCtl != null && tipCtl.IsVisible && tipCtl.Theme == T("PrTip"), "原因提示用 PrTip 主题并已打开");
        if (tipCtl != null) Save(tipCtl, Path.Combine(dir, $"theme-popup-tip-{width}.png"), lines);
        ToolTip.SetIsOpen(_tipBtn, false);

        // 普通字符串提示：页内资源里 {x:Type ToolTip} = PrTip（陈列页同样做法）→ 自动生成的 ToolTip 也用 PrTip
        ToolTip.SetIsOpen(_plainTipBtn, true);
        await Settle();
        var field = typeof(ToolTip).GetField("ToolTipProperty", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var auto = (field?.GetValue(null) as AvaloniaProperty) is { } prop ? _plainTipBtn.GetValue(prop) as ToolTip : null;
        Check(auto != null && auto.Background is ISolidColorBrush bg && bg.Color == Palette.ToastColor,
            $"字符串提示自动套用页内默认 PrTip（底色 {(auto?.Background as ISolidColorBrush)?.Color.ToString() ?? "无"}）");
        if (auto != null) Save(auto, Path.Combine(dir, $"theme-popup-plaintip-{width}.png"), lines);
        ToolTip.SetIsOpen(_plainTipBtn, false);

        await GuardChecksAsync(Check, Info);
        await LiveChecksAsync(Check, Info);
        return (fails, lines);
    }

    /// <summary>
    /// Pr.Reason 的各条执行路径（M1 复核：以前只拦鼠标和 Enter 按下）：键盘 Space / Enter、IsDefault 回车、IsCancel Esc、HotKey、UI 自动化 Invoke / Toggle、
    /// 带原因的开关、菜单项 HotKey、下拉的方向键、输入框打字；清空原因后命令 / 提示 / 无障碍说明原样回来；另核对真实指针按下的颜色（同样稿 CSS）。
    /// 在单独的冻结小窗口里做（不改主题表的截图）。
    /// </summary>
    private static async Task GuardChecksAsync(Action<bool, string> Check, Action<string> Info)
    {
        int cmd = 0, click = 0, cancel = 0, chipCmd = 0, menuCmd = 0, plainCmd = 0;
        var field = new TextBox { Theme = T("PrInput"), Width = 160, Text = "其它输入" };
        var btn = new Button { Theme = T("PrPrimary"), Content = "下发程序", IsDefault = true, HotKey = new KeyGesture(Key.F5), Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => cmd++) };
        btn.Click += (_, _) => click++;
        ToolTip.SetTip(btn, "下发到所选反应釜");
        Pr.SetReason(btn, "先申请控制权");
        var cancelBtn = new Button { Theme = T("PrButton"), Content = "停止", IsCancel = true, Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => cancel++) };
        Pr.SetReason(cancelBtn, "先申请控制权");
        var plain = new Button { Theme = T("PrButton"), Content = "编辑", Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => plainCmd++) };
        var chip = new Chip { Content = "RV5", Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => chipCmd++) };
        Pr.SetReason(chip, "未安装");
        var tog = new PrToggle { Theme = T("PrSegItem"), Content = "60 min", Height = 30 };
        Pr.SetReason(tog, "先申请控制权");
        var combo = new ComboBox { Theme = T("PrSelect"), Width = 200, Items = { "加氢-标准", "高压加氢-150", "吹扫" }, SelectedIndex = 0 };
        Pr.SetReason(combo, "先申请控制权");
        var locked = new TextBox { Theme = T("PrInput"), Width = 160, Text = "120" };
        Pr.SetReason(locked, "先申请控制权");
        var menuItem = new MenuItem { Theme = T("PrMenuItem"), Header = "下发程序…", HotKey = new KeyGesture(Key.F6), Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => menuCmd++) };
        Pr.SetReason(menuItem, "先申请控制权");
        var menuTop = new MenuItem { Theme = T("PrMenuBarItem"), Header = "程序", Items = { menuItem } };
        var menu = new Menu { Theme = T("PrMenu"), Items = { menuTop } };
        var panel = new WrapPanel { Margin = new Thickness(20), Children = { menu, field, btn, cancelBtn, plain, chip, tog, combo, locked } };
        foreach (var c in panel.Children) c.Margin = new Thickness(6);
        var root = new Border { Background = Palette.BgCardBrush, Child = panel };
        root.Styles.Add(new StyleInclude(new Uri("avares://ParallelReactor.Control/")) { Source = new Uri("avares://ParallelReactor.Control/Styles/Frozen.axaml") });
        Frozen.Apply(root);
        var w = new Window { Width = 900, Height = 300, Content = root, FontSize = 15, Foreground = Palette.T2Brush, Background = Palette.BgCardBrush };
        if (Application.Current!.TryFindResource("UiFont", out var f) && f is FontFamily ff) w.FontFamily = ff;
        w.Show();
        await Settle();

        void Keys(Key k, PhysicalKey pk, string? sym = null, RawInputModifiers mods = RawInputModifiers.None)
        {
            w.KeyPress(k, mods, pk, sym);
            w.KeyRelease(k, mods, pk, sym);
            Dispatcher.UIThread.RunJobs();
        }

        btn.Focus(NavigationMethod.Tab);
        Dispatcher.UIThread.RunJobs();
        Keys(Key.Space, PhysicalKey.Space, " ");
        Keys(Key.Enter, PhysicalKey.Enter, "\r");
        Check(cmd == 0 && click == 0, $"Pr.Reason：聚焦后 Space（抬起才执行）/ Enter 不执行（Command {cmd} · Click {click}）");
        field.Focus(NavigationMethod.Tab);
        Dispatcher.UIThread.RunJobs();
        Keys(Key.Enter, PhysicalKey.Enter, "\r");
        Check(cmd == 0 && click == 0, $"Pr.Reason：IsDefault 按钮在别的输入框里回车不执行（Command {cmd} · Click {click}）");
        Keys(Key.Escape, PhysicalKey.Escape);
        Check(cancel == 0, $"Pr.Reason：IsCancel 按钮 Esc 不执行（{cancel}）");
        Keys(Key.F5, PhysicalKey.F5);
        Check(cmd == 0, $"Pr.Reason：HotKey F5 不执行（{cmd}）");
        if (ControlAutomationPeer.CreatePeerForElement(btn) is IInvokeProvider inv) inv.Invoke();
        Dispatcher.UIThread.RunJobs();
        Check(cmd == 0 && click == 0, $"Pr.Reason：UI 自动化 Invoke 不执行（Command {cmd} · Click {click}）");
        Check(AutomationProperties.GetHelpText(btn) == "先申请控制权", $"Pr.Reason：无障碍说明 = 原因（「{AutomationProperties.GetHelpText(btn)}」）");
        Keys(Key.F6, PhysicalKey.F6);
        Check(menuCmd == 0, $"Pr.Reason：菜单项 HotKey F6 不执行（{menuCmd}）");
        chip.Focus(NavigationMethod.Tab);
        Dispatcher.UIThread.RunJobs();
        Keys(Key.Space, PhysicalKey.Space, " ");
        if (ControlAutomationPeer.CreatePeerForElement(chip) is IToggleProvider chipToggle) chipToggle.Toggle();
        if (ControlAutomationPeer.CreatePeerForElement(tog) is IToggleProvider togToggle) togToggle.Toggle();
        Dispatcher.UIThread.RunJobs();
        Check(chip.IsChecked != true && tog.IsChecked != true && chipCmd == 0, $"Pr.Reason：芯片 / PrToggle 的 Space 与 UI 自动化 Toggle 翻不动（{chip.IsChecked} / {tog.IsChecked} · 命令 {chipCmd}）");
        combo.Focus(NavigationMethod.Tab);
        Dispatcher.UIThread.RunJobs();
        Keys(Key.Down, PhysicalKey.ArrowDown);
        Keys(Key.Down, PhysicalKey.ArrowDown, null, RawInputModifiers.Alt);
        Check(combo.SelectedIndex == 0 && !combo.IsDropDownOpen, $"Pr.Reason：下拉的方向键 / Alt+↓ 不改选择、不展开（{combo.SelectedIndex} · {combo.IsDropDownOpen}）");
        combo.IsDropDownOpen = false;
        locked.Focus(NavigationMethod.Tab);
        locked.CaretIndex = locked.Text?.Length ?? 0;
        Dispatcher.UIThread.RunJobs();
        w.KeyTextInput("9");
        Keys(Key.Back, PhysicalKey.Backspace);
        Dispatcher.UIThread.RunJobs();
        Check(locked.Text == "120" && locked.IsReadOnly, $"Pr.Reason：输入框打字 / 退格不改内容（「{locked.Text}」· 只读 {locked.IsReadOnly}）");
        plain.Focus(NavigationMethod.Tab);
        Dispatcher.UIThread.RunJobs();
        Keys(Key.Space, PhysicalKey.Space, " ");
        Check(plainCmd == 1, $"没有原因的按钮 Space 照常执行（{plainCmd}）");

        // 清空原因：命令、提示、无障碍说明原样回来
        Pr.SetReason(btn, null);
        Dispatcher.UIThread.RunJobs();
        if (ControlAutomationPeer.CreatePeerForElement(btn) is IInvokeProvider inv2) inv2.Invoke();
        Dispatcher.UIThread.RunJobs();
        Check(cmd == 1 && click == 1, $"清空原因后命令恢复（Command {cmd} · Click {click}）");
        Check(ToolTip.GetTip(btn) as string == "下发到所选反应釜" && string.IsNullOrEmpty(AutomationProperties.GetHelpText(btn)) && !btn.Classes.Contains(":reason"),
            $"清空原因后提示 / 无障碍说明 / 外观复原（提示「{ToolTip.GetTip(btn)}」）");
        w.Close();

        // 真实指针按下的颜色（冻结、无过渡）：样稿 base.css 的 :active；幽灵按钮按下仍是 bg-press（:hover 在 :active 之后）
        foreach (var (theme, expect) in new (string, Color)[]
        {
            ("PrButton", Palette.ASoftColor), ("PrPrimary", Palette.AInkColor), ("PrStop", Palette.ASoftColor),
            ("PrGhost", Palette.BgPressColor), ("PrIconButton", Palette.LineSoftColor), ("PrLink", Palette.ASoftColor),
        })
        {
            var b = new Button { Theme = T(theme), Content = theme == "PrIconButton" ? null : "按下" };
            Pr.SetIcon(b, theme == "PrLink" ? null : "edit");
            if (theme == "PrIconButton") ToolTip.SetTip(b, "编辑");
            var host = new Border { Background = Palette.BgCardBrush, Padding = new Thickness(30), Child = b };
            host.Styles.Add(new StyleInclude(new Uri("avares://ParallelReactor.Control/")) { Source = new Uri("avares://ParallelReactor.Control/Styles/Frozen.axaml") });
            Frozen.Apply(host);
            var pw = new Window { Width = 300, Height = 120, Content = host, Background = Palette.BgCardBrush };
            pw.Show();
            await Settle();
            var body = b.GetVisualDescendants().OfType<Border>().FirstOrDefault(x => x.Name == "PART_Body");
            var p = Center(b, pw);
            pw.MouseMove(p);
            pw.MouseDown(p, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            var got = (body?.Background as ISolidColorBrush)?.Color;
            Check(b.IsPressed && got == expect, $"真实按下 {theme}：底色 {got} = {expect}");
            pw.MouseUp(p, MouseButton.Left);
            pw.Close();
        }
    }

    /// <summary>就地更新（R10）：选项 / 步骤段改属性时不重建、不丢焦点；FitText 不截半个数；SizeClasses 短暂来回不卡旧类；NumBox 拒绝坏值；无障碍名。</summary>
    private static async Task LiveChecksAsync(Action<bool, string> Check, Action<string> Info)
    {
        var tabs = new PTabs { Value = "a", Options = { new SegOption { Value = "a", Label = "设备信息" }, new SegOption { Value = "b", Label = "报警与事件", Count = "1" } } };
        var bar = new StepBar { ShowLabels = true, Width = 300 };
        for (int i = 0; i < 4; i++) bar.Segments.Add(new StepSeg { Label = "S" + (i + 1), State = i < 1 ? StepSegState.Done : i == 1 ? StepSegState.Current : StepSegState.Pending, Fraction = 0.3 });
        var fitTiny = new FitText { Text = "119.8", Unit = "℃", Classes = { "num" } };
        var fitNarrow = new FitText { Text = "119.8", Unit = "℃", Classes = { "num" } };
        var sc = new Border { Width = 350, Height = 30 };
        SizeClasses.SetRules(sc, "sc-narrow: w < 300; sc-wide: w >= 300");
        var num = new NumBox { Value = 120, Minimum = double.NaN, Maximum = double.NaN, Decimals = 1, Unit = "℃", Width = 200 };
        var iconBtn = new Button { Theme = T("PrIconButton") };
        Pr.SetIcon(iconBtn, "zoomin");
        ToolTip.SetTip(iconBtn, "放大");
        var hidden = new TextBlock { Text = "剩 42 min", FontSize = 14 };
        var fitRow = new BarFitPanel { Width = 90, Children = { Bar(8), hidden } };
        var panel = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 10,
            Children =
            {
                tabs, bar,
                new Border { Width = 12, HorizontalAlignment = HorizontalAlignment.Left, Child = fitTiny },
                new Border { Width = 46, HorizontalAlignment = HorizontalAlignment.Left, Child = fitNarrow },
                sc, num, iconBtn, fitRow,
            },
        };
        var root = new Border { Background = Palette.BgCardBrush, Child = panel };
        Frozen.Apply(root);
        var w = new Window { Width = 600, Height = 520, Content = root, FontSize = 15, Foreground = Palette.T2Brush, Background = Palette.BgCardBrush };
        if (Application.Current!.TryFindResource("UiFont", out var f) && f is FontFamily ff) w.FontFamily = ff;
        w.Show();
        await Settle();

        var before = tabs.GetVisualDescendants().OfType<PrToggle>().ToList();
        before[1].Focus(NavigationMethod.Tab);
        Dispatcher.UIThread.RunJobs();
        tabs.Options[1].Count = "2";
        tabs.Options[1].Dot = true;
        Dispatcher.UIThread.RunJobs();
        tabs.Options[1] = new SegOption { Value = "b", Label = "报警与事件", Count = "3" };
        await Settle();
        var after = tabs.GetVisualDescendants().OfType<PrToggle>().ToList();
        Check(after.Count == 2 && ReferenceEquals(before[1], after[1]) && after[1].IsFocused && Pr.GetCount(after[1]) == "3",
            $"PTabs：改计数 / 红点、替换选项都就地更新（同一个按钮 {ReferenceEquals(before[1], after[1])} · 焦点 {after[1].IsFocused} · 计数 {Pr.GetCount(after[1])}）");

        var labels = bar.GetVisualChildren().OfType<TextBlock>().ToList();
        bar.Segments[1].Fraction = 0.8;
        bar.Segments[1].State = StepSegState.Done;
        bar.Segments[2].State = StepSegState.Current;
        await Settle();
        var labels2 = bar.GetVisualChildren().OfType<TextBlock>().ToList();
        Check(labels.Count == 4 && labels.SequenceEqual(labels2) && labels2[2].FontWeight == FontWeight.SemiBold && labels2[1].FontWeight == FontWeight.Normal,
            $"StepBar：段的进度 / 状态变化就地更新标签（同一批标签 {labels.SequenceEqual(labels2)}）");

        string Shown(FitText ft) => ft.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()?.Text ?? "";
        bool Collapsed(FitText ft) => ft.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()?.TextLayout.TextLines.Any(l => l.HasCollapsed) == true;
        Info($"FitText 宽 12：「{Shown(fitTiny)}」@{fitTiny.ChosenSize} · 宽 46：「{Shown(fitNarrow)}」@{fitNarrow.ChosenSize}");
        Check(fitTiny.ChosenOverflow && Shown(fitTiny) == FitText.Overflow && ToolTip.GetTip(fitTiny) as string == "119.8 ℃",
            "FitText：13 px 也放不下时只显示「…」并给出全文提示（不截成「11…」）");
        Check(Shown(fitNarrow) == "119.8" && !Collapsed(fitNarrow) && fitNarrow.ChosenSize >= FitText.MinSize,
            $"FitText：窄时数值缩到 {fitNarrow.ChosenSize} px，不省略");

        // 同一轮里来回（第二次变化落在 250 ms 的防振荡窗口里）：排完布局后按最终尺寸再判一次
        void Tick() { for (int i = 0; i < 2; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } }
        sc.Width = 250;
        Tick();
        var mid = string.Join(" ", sc.Classes);
        sc.Width = 350;
        Tick();
        Info($"SizeClasses 250 时：{mid}");
        Check(sc.Classes.Contains("sc-wide") && !sc.Classes.Contains("sc-narrow"), $"SizeClasses：350 → 250 → 350 快速来回后是 sc-wide（{string.Join(" ", sc.Classes)}）");

        var input = num.GetVisualDescendants().OfType<TextBox>().First();
        var bad = new List<string>();
        foreach (var s in new[] { "NaN", "Infinity", "1e400", "abc" })
        {
            input.Focus();
            input.Text = s;
            w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
            w.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
            Dispatcher.UIThread.RunJobs();
            if (num.Value != 120 || input.Text != "120.0") bad.Add($"{s}→{num.Value}/{input.Text}");
        }
        Check(bad.Count == 0, "NumBox：NaN / Infinity / 1e400 / abc 不改值、输入框恢复原值" + (bad.Count > 0 ? "：" + string.Join(", ", bad) : ""));

        Check(AutomationProperties.GetName(iconBtn) == "放大", $"纯图标按钮的无障碍名默认取提示文字（「{AutomationProperties.GetName(iconBtn)}」）");

        hidden.Text = "剩 41 min";
        await Settle();
        Check(fitRow.IsBarFit && ToolTip.GetTip(fitRow) as string == "剩 41 min", $"BarFitPanel：藏起来的文字变了，提示跟着变（「{ToolTip.GetTip(fitRow)}」）");
        w.Close();
    }

    private static StepBar Bar(int n)
    {
        var b = new StepBar();
        for (int i = 0; i < n; i++) b.Segments.Add(new StepSeg { State = i < 3 ? StepSegState.Done : StepSegState.Pending });
        return b;
    }

    /// <summary>排版后有没有 .notdef（方块）字形。</summary>
    private static bool HasNotdef(string text, Typeface face)
    {
        using var l = new Avalonia.Media.TextFormatting.TextLayout(text, face, 14, null);
        return l.TextLines.SelectMany(t => t.TextRuns).OfType<Avalonia.Media.TextFormatting.ShapedTextRun>().Any(r => r.ShapedBuffer.Any(g => g.GlyphIndex == 0));
    }

    private static async Task Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            await Task.Delay(120);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Save(Avalonia.Controls.Control c, string path, List<string> lines)
    {
        var size = new PixelSize(Math.Max(1, (int)Math.Ceiling(c.Bounds.Width)), Math.Max(1, (int)Math.Ceiling(c.Bounds.Height)));
        using var rtb = new RenderTargetBitmap(size, new Vector(96, 96));
        rtb.Render(c);
        rtb.Save(path);
        lines.Add($"INFO saved {Path.GetFileName(path)} {size.Width}x{size.Height}");
    }
}
#endif
