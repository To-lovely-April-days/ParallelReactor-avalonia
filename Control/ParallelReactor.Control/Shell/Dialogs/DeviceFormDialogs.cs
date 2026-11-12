using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;

namespace ParallelReactor.Control.Shell.Dialogs;

/// <summary>
/// 别名输入（样稿 aliasField：添加 / 编辑 / 重命名共用）：输入时就地校验（唯一、最长 8 个汉字或 16 个字母，Core 的 ActionGuard.AliasError），
/// 右侧计数「5/16」（超过时红字 600），下面是说明或错误（⚠ + 红字 600），输入框出错时红框。
/// </summary>
public sealed partial class AliasFieldVm : ObservableObject
{
    private readonly Func<string, string> _check;

    public AliasFieldVm(string alias, Func<string, string> check)
    {
        _check = check;
        _alias = alias;
        Validate();
    }

    [ObservableProperty] private string _alias;
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _count = "";
    [ObservableProperty] private bool _countOver;
    /// <summary>说明或错误那一行。</summary>
    [ObservableProperty] private string _hint = "";

    public const string HintText = "最多 8 个汉字或 16 个字母 · 页签、列表、时间轴都靠别名区分设备";

    partial void OnAliasChanged(string value) => Validate();

    /// <summary>重新校验（别的设备改名 / 增删之后也要）。</summary>
    public void Validate()
    {
        Error = _check(Alias ?? "");
        HasError = Error.Length > 0;
        int units = DialogText.AliasUnits(Alias);
        Count = units + "/" + ActionGuard.AliasMax;
        CountOver = units > ActionGuard.AliasMax;
        Hint = HasError ? Error : HintText;
    }
}

/// <summary>型号下拉的一项（演示：样稿 PR.models.keys → 「HT-PR8 · 8 路」）。</summary>
public sealed record ModelChoice(string Key, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// 添加设备 / 编辑设备的同一张表单（样稿 dialogs.js formBody，两列：地址 · 端口；访问令牌（整行，可显示）；别名（整行，计数 + 校验）；型号 · 启动时 自动连接）。
/// 与样稿的差异（都写在这里）：
/// <list type="bullet">
/// <item>令牌下面去掉了「在设备触屏「设置 → 远程接口」查看」：HMI 只存令牌的哈希，触屏上看不到令牌（PORT-PLAN §5 M3）。</item>
/// <item>令牌下面加「记住令牌」（D7：只有勾了才存；样稿没有这个字段）。没勾时「自动连接」禁用并写明原因（D7：自动连接要求存了令牌）。</item>
/// <item>真机：型号不能选（从 /device 读），显示「—」，「测试连接」成功后显示读到的型号；演示同样稿（型号（原型演示）下拉）。</item>
/// </list>
/// </summary>
public abstract partial class DeviceFormVm : DialogVm
{
    protected readonly ShellVm Shell;

    protected DeviceFormVm(ShellVm shell, string host, string port, string token, string alias, bool remember, bool auto, Func<string, string> aliasCheck)
    {
        Shell = shell;
        _host = host;
        _port = port;
        _token = token;
        _rememberToken = remember;
        _autoConnect = auto;
        Alias = new AliasFieldVm(alias, aliasCheck);
        Alias.PropertyChanged += (_, _) => Validate();
        EyeCommand = new RelayCommand(() => ShowToken = !ShowToken);
        Width = 660;
        Body = this;
    }

    public bool IsDemo => Shell.Fleet.IsDemo;
    public AliasFieldVm Alias { get; }
    public ICommand EyeCommand { get; }

    [ObservableProperty] private string _host;
    [ObservableProperty] private string _port;
    [ObservableProperty] private string _token;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(EyeIcon), nameof(EyeTip), nameof(TokenChar))] private bool _showToken;
    [ObservableProperty] private bool _rememberToken;
    [ObservableProperty] private bool _autoConnect;
    /// <summary>「自动连接」为什么不能勾（D7：没勾「记住令牌」）；null = 能勾。</summary>
    [ObservableProperty] private string? _autoReason;
    /// <summary>「保存 / 添加并连接」为什么不能点（样稿 reason：别名错误、请填写地址；真机另有端口 / 令牌）。</summary>
    [ObservableProperty] private string? _submitReason;

    public string EyeIcon => ShowToken ? "eyeoff" : "eye";
    public string EyeTip => ShowToken ? "隐藏令牌" : "显示令牌";
    public char TokenChar => ShowToken ? '\0' : '•';   // 同浏览器的密码框（U+2022）

    /// <summary>「记住令牌」的说明（D7，样稿没有）：落盘方式按系统写明。</summary>
    public string RememberHint => TokenProtector.CurrentMode == TokenStoreMode.Dpapi ? "记住令牌（Windows 加密保存在本机）" : "记住令牌（明文保存在本机，仅本用户可读）";

    // 型号（演示：下拉；编辑 / 真机：只读）
    public abstract string ModelLabel { get; }
    public abstract string ModelHint { get; }
    public virtual bool ModelSelectable => false;
    public IReadOnlyList<ModelChoice> Models { get; protected init; } = Array.Empty<ModelChoice>();
    [ObservableProperty] private ModelChoice? _model;
    [ObservableProperty] private string _modelText = "—";

    /// <summary>表单下面的中性说明（添加设备：本机客户端名 · 示例型号）；没有为 null。</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasNote))] private IReadOnlyList<TextSeg>? _note;
    public bool HasNote => Note != null;

    /// <summary>表单下面的 warn 说明（编辑：地址改了且在线时「…会断开并按新地址重新连接…」）；没有为空串。</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasWarnNote))] private string _warnNote = "";
    public bool HasWarnNote => WarnNote.Length > 0;

    partial void OnModelChanged(ModelChoice? value) => OnModelPicked();

    /// <summary>选了别的型号（演示的「型号（原型演示）」下拉）。</summary>
    protected virtual void OnModelPicked() { }

    partial void OnHostChanged(string value) => Validate();
    partial void OnPortChanged(string value) => Validate();
    partial void OnTokenChanged(string value) => Validate();
    partial void OnRememberTokenChanged(bool value) => Validate();

    /// <summary>端口（无效时 null）。</summary>
    protected int? PortValue => int.TryParse((Port ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p is > 0 and <= 65535 ? p : null;

    /// <summary>重新算「提交」按钮的原因、「自动连接」能不能勾、表单下方的提示。</summary>
    protected virtual void Validate()
    {
        // D7：自动连接要求存了令牌（演示同样稿，不限制）
        AutoReason = IsDemo || RememberToken ? null : Services.DeviceRegistry.AutoConnectNeedsToken;
        if (AutoReason != null && AutoConnect) AutoConnect = false;
        string err = Alias.Error.Length > 0 ? Alias.Error : string.IsNullOrWhiteSpace(Host) ? "请填写地址" : "";
        // 真机连不上的输入先拦住（样稿只有前两条；这两句沿用旧「连接」对话框的文字）
        if (err.Length == 0 && !IsDemo && PortValue == null) err = "端口无效";
        if (err.Length == 0 && !IsDemo && RequiresToken && string.IsNullOrWhiteSpace(Token)) err = "请输入访问令牌";
        SubmitReason = FootButton.Nz(err);
        OnValidated();
    }

    /// <summary>真机提交时要有令牌（添加：要；编辑：留空 = 不改令牌）。</summary>
    protected virtual bool RequiresToken => true;

    protected virtual void OnValidated() { }
}

/// <summary>「测试连接」那一块（样稿 .pr-foot-left：按钮 + 结果「✓ 连接成功 · 读取到 HT-PR8」）。</summary>
public sealed partial class TestFootVm : ObservableObject
{
    public TestFootVm(FootButton button) => Button = button;

    public FootButton Button { get; }
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _hasText;
    [ObservableProperty] private bool _isOk;
    [ObservableProperty] private bool _isErr;
    [ObservableProperty] private bool _isBusy;

    public void Set(string text, bool ok, bool err, bool busy)
    {
        Text = text;
        HasText = text.Length > 0;
        IsOk = ok;
        IsErr = err;
        IsBusy = busy;
    }
}

/// <summary>
/// 添加设备（样稿 dialogs.js 'add'，final-dlg-add）：「测试连接」= 真机 GET /health（不带令牌）再 GET /device（验证令牌）、演示同样稿直接「连接成功」；
/// 「添加并连接」= 登记（devices.json）+ 开始连接，选中新设备，提示「已添加 4号机 · 正在连接 192.168.1.50:8080」。
/// 默认别名 = 第一个没用过的「k号机」。演示的默认值同样稿（192.168.1.50:8080、示例令牌、自动连接、HT-PR8）；真机地址和令牌留空。
/// 底栏说明：本机客户端名全局一个（「在 设置 中修改」：设置页在第二阶段，样稿原话）。
/// </summary>
public sealed partial class AddDeviceDialogVm : DeviceFormVm
{
    private CancellationTokenSource? _testCts;
    private bool _closed;

    public AddDeviceDialogVm(ShellVm shell)
        : base(shell,
            shell.Fleet.IsDemo ? "192.168.1.50" : "", "8080", shell.Fleet.IsDemo ? "ht-7f3c-pr8-b210" : "",
            shell.Fleet.FreeAlias(), remember: shell.Fleet.IsDemo, auto: shell.Fleet.IsDemo,
            a => shell.Fleet.AliasError(a, null))
    {
        Name = "add";
        Title = "添加设备";
        Sub = "同时连多台：每台一个接口地址、一个访问令牌、一个控制权";
        Icon = "plus";
        var models = shell.Fleet.DemoData?.Models ?? ModelProfiles.Embedded;
        Models = models.All.Select(m => new ModelChoice(m.Key, m.Label + " · " + m.ChannelCount + " 路")).ToList();
        Model = Models.FirstOrDefault(m => m.Key == "PR8") ?? Models.FirstOrDefault();
        // 再点一次「测试连接」= 重新测（TestAsync 先取消上一次）：允许并发执行，测的时候按钮不灰（AsyncRelayCommand 默认执行期间是没有原因的禁用，M6）
        Test = new TestFootVm(new FootButton("测试连接", new AsyncRelayCommand(TestAsync, AsyncRelayCommandOptions.AllowConcurrentExecutions), icon: "link"));
        FooterLeft = Test;
        CancelButton = new FootButton("取消", new RelayCommand(() => shell.Dialogs.Close(this)));
        AddButton = new FootButton("添加并连接", new RelayCommand(Add), FootKind.Primary, "plus", large: true);
        Footer = new FootBar(CancelButton, AddButton);
        // 样稿：本机客户端名全局一个：<b>PC-测试工位1</b>（在 设置 中修改）。示例型号 · 参数为假设。
        // 真机：设置页在第二阶段（占位），改名的入口是「设备 › 本机客户端名…」—— 括号里照实写（M3 复核）；演示照样稿
        Note = new[]
        {
            new TextSeg("本机客户端名全局一个："), new TextSeg(shell.Fleet.ClientName, Bold: true),
            new TextSeg((shell.Fleet.IsDemo ? "（在 设置 中修改）。" : "（在「设备 › " + ClientNameDialogVm.MenuLabel + "」中修改）。") + DialogText.SampleNote + "。"),
        };
        Validate();
    }

    public TestFootVm Test { get; }
    public FootButton CancelButton { get; }
    public FootButton AddButton { get; }

    public override string ModelLabel => IsDemo ? "型号（原型演示）" : "型号";
    public override string ModelHint => "真机连接后从设备 /device 读取";
    public override bool ModelSelectable => IsDemo;

    protected override void OnValidated()
    {
        if (AddButton != null) AddButton.Reason = SubmitReason;
    }

    protected override void OnModelPicked()
    {
        // 演示：换了型号，之前「连接成功 · 读取到 …」的结果作废（同样稿：结果写的是选中的型号）
        if (Test != null && Test.IsOk && IsDemo) Test.Set("", false, false, false);
    }

    private async Task TestAsync()
    {
        _testCts?.Cancel();
        var cts = _testCts = new CancellationTokenSource();
        Test.Set(IsDemo ? "" : "正在连接…", false, false, !IsDemo);
        ConnectionTestResult r;
        // 被下一次「测试连接」或关对话框取消的那一次：结果作废、不抛（ConnectionTester 照样抛出取消；不接住的话 AsyncRelayCommand 会把它抛回 UI 线程）
        try { r = await Shell.Fleet.TestConnectionAsync(Host, PortValue ?? 0, Token, Model?.Key, cts.Token).ConfigureAwait(true); }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { return; }
        if (cts.IsCancellationRequested || _closed) return;
        Test.Set(r.Text, r.Ok, !r.Ok, false);
        if (r.Ok && !IsDemo && r.Model is { } m) ModelText = m.Label + " · " + m.ChannelCount + " 路";
    }

    private void Add()
    {
        Validate();
        if (SubmitReason != null) return;
        var n = new NewDevice(Host.Trim(), PortValue ?? ControlConfig.DefaultPort, Token, Alias.Alias, AutoConnect, RememberToken, Model?.Key);
        var d = Shell.Fleet.AddDevice(n);
        if (d == null) { Alias.Validate(); Validate(); return; }
        Shell.Dialogs.Close(this);
        Shell.Select(d.K);
        Shell.Toast("已添加 " + d.Alias + " · 正在连接 " + d.Host + ":" + d.Port, ToastKind.Ok);
    }

    public override void OnClosed()
    {
        _closed = true;
        _testCts?.Cancel();
    }
}

/// <summary>
/// 编辑设置（样稿 dialogs.js 'edit'）：同一张表单，预填这台的地址、端口、令牌、别名、自动连接（+ 记住令牌）；型号只读（「连接后从设备 /device 读取，不能修改」）。
/// 地址或端口改了且在线时提示「地址或端口改变后会断开并按新地址重新连接；本机的控制权会先释放。」（warn）。保存 = 样稿 editDevice：
/// 真机先释放控制权 / 取消申请再按新地址重连（LiveDeviceSource.UpdateConfigAsync）；令牌留空 = 不改。提示「已保存 2号机 的设置」。
/// </summary>
public sealed partial class EditDeviceDialogVm : DeviceFormVm
{
    private readonly int _k;
    private readonly DeviceSettings _orig;
    private bool _closed, _saving;

    private EditDeviceDialogVm(ShellVm shell, int k, DeviceSettings s)
        : base(shell, s.Host, s.Port.ToString(CultureInfo.InvariantCulture), s.Token, s.Alias, s.RememberToken, s.AutoConnect,
            a => shell.Fleet.AliasError(a, k))
    {
        _k = k;
        _orig = s;
        Name = "edit";
        Arg = DialogText.K(k);
        Icon = "edit";
        ModelText = s.ModelText;
        CancelButton = new FootButton("取消", new RelayCommand(() => shell.Dialogs.Close(this)));
        // 保存期间按钮不灰（M6）：正在保存时再点一次不重复执行（_saving）
        SaveButton = new FootButton("保存", new AsyncRelayCommand(SaveAsync, AsyncRelayCommandOptions.AllowConcurrentExecutions), FootKind.Primary, "check", large: true);
        Footer = new FootBar(CancelButton, SaveButton);
        Validate();
        Refresh();
    }

    /// <summary>arg = 设备位置 k；设备不在时不打开。</summary>
    public static EditDeviceDialogVm? Create(ShellVm shell, string? arg) =>
        DialogText.ParseK(arg) is int k && shell.Fleet.SettingsOf(k) is { } s ? new EditDeviceDialogVm(shell, k, s) : null;

    public int K => _k;
    public FootButton CancelButton { get; }
    public FootButton SaveButton { get; }

    public override string ModelLabel => "型号";
    public override string ModelHint => "连接后从设备 /device 读取，不能修改";

    /// <summary>
    /// 编辑时令牌留空 = 不改（样稿 if (o.token) d.token = o.token）。例外（真机，M3 复核）：勾了「记住令牌」而本机还没有这台的令牌 —— 不填就存成
    /// 「记住了、却没有令牌」，下次启动只会提示「记住的令牌读不出来」：这时要先填令牌（同添加设备的「请输入访问令牌」）。
    /// </summary>
    protected override bool RequiresToken => RememberToken && _orig != null && string.IsNullOrEmpty(_orig.Token);

    protected override void OnValidated()
    {
        if (SaveButton != null) SaveButton.Reason = SubmitReason;
        UpdateMoved();
    }

    private bool Moved => !string.Equals((Host ?? "").Trim(), _orig.Host, StringComparison.Ordinal) || (Port ?? "").Trim() != _orig.Port.ToString(CultureInfo.InvariantCulture);

    /// <summary>换了令牌（真机：同样会释放控制权、断开、用新令牌重新连接 —— LiveDeviceSource.UpdateConfigAsync）。</summary>
    private bool TokenChanged => !IsDemo && !string.IsNullOrWhiteSpace(Token) && Token.Trim() != (_orig.Token ?? "");

    private void UpdateMoved()
    {
        var d = Shell.Fleet.Device(_k);
        if (d == null) return;
        // 样稿 moved && online 时的 warn 说明；真机换了令牌同样会断开重连（M3 复核：说明里写上，句式同样稿）
        string lease = d.LeaseStatus is not (LeaseStatus.Local or LeaseStatus.Other) ? "；本机的控制权会先释放。" : "。";
        WarnNote = !DialogText.Online(d) ? ""
            : Moved ? "地址或端口改变后会断开并按新地址重新连接" + lease
            : TokenChanged ? "令牌改变后会断开并用新令牌重新连接" + lease
            : "";
    }

    /// <summary>样稿 live: false：不改用户正在填的内容；设备没了时关闭，标题跟着别名。</summary>
    public override void Refresh()
    {
        if (_closed) return;
        var d = Shell.Fleet.Device(_k);
        if (d == null) { Shell.Dialogs.Close(this); return; }
        Title = "编辑 " + d.Alias;
        Sub = d.ModelLabel + " · SN " + (string.IsNullOrEmpty(d.Sn) ? Core.Format.Fmt.Dash : d.Sn);   // 从未连上、没记下 SN：「—」
    }

    private async Task SaveAsync()
    {
        if (_saving || _closed) return;
        Validate();
        if (SubmitReason != null) return;
        var d = Shell.Fleet.Device(_k);
        if (d == null) return;
        _saving = true;
        try
        {
            // 样稿：地址变了且本机持有 / 申请着控制权 → 先释放 / 取消（quiet）。真机的数据源换地址时自己先释放；演示在这里做
            if (Moved && IsDemo && d.LeaseStatus is LeaseStatus.Requested or LeaseStatus.Remote or LeaseStatus.Lost)
                await d.Actions.RunAsync(d.LeaseStatus == LeaseStatus.Requested ? DeviceAction.LeaseCancel : DeviceAction.LeaseRelease, quiet: true).ConfigureAwait(true);
            var token = string.IsNullOrWhiteSpace(Token) || Token == _orig.Token ? null : Token.Trim();
            // 真机：存好就关（background）—— 释放控制权、断开、按新设置的第一次连接在后台做，结果照常提示（M3 复核：以前对话框要等最长约 13 s）
            var r = await Shell.Fleet.EditDeviceAsync(_k, new DeviceSettingsEdit(Alias.Alias, Host.Trim(), PortValue ?? _orig.Port, token, RememberToken, AutoConnect),
                background: true).ConfigureAwait(true);
            if (r.Length > 0) { Shell.Toast(r, ToastKind.Warn); return; }
            Shell.Dialogs.Close(this);
            Shell.Toast("已保存 " + (Shell.Fleet.Device(_k)?.Alias ?? Alias.Alias) + " 的设置", ToastKind.Ok);
        }
        finally { _saving = false; }
    }

    public override void OnClosed() => _closed = true;
}
