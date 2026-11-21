using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages.Settings.Pid;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Pages.Settings;

/// <summary>
/// 设置页（融合版 F8，样稿 PAGES.set + setBody）：左「设置」导航 240（本机：常规 / 设备连接 / 无人值守 · 「1号机 参数」：参数与报警 / 温控 PID / 通讯与模块 / 数据存储 /
/// 吸气量计算 · 关于），右边是这一节的内容（灰带「常规 · 本机」/「吸气量计算 · 1号机 · HT-PR8」）。
/// <list type="bullet">
/// <item><b>本机</b>（<see cref="PrefsStore"/>）：改了马上生效（防睡眠、声音、补拉，同样稿 SETP），工具条「保存」写进 devices.json（"pc" 一节 + 本机客户端名）。</item>
/// <item><b>设备参数</b>跟着设备列表切换，只读（只能在触屏改，「触屏操作」）；温控 PID 读 GET /pid、手改即写入（SetPidAsync，要控制权），可以按档自整定（Pid/PidSectionVm）。</item>
/// <item>演示数据：设备设置用样稿的数值（<see cref="DemoSettings"/>），不写文件。</item>
/// </list>
/// 地址参数：<c>set=gen|conn|night|lim|pid|comm|store|upt|about</c>（同样稿）。
/// </summary>
public sealed partial class SettingsPageVm : PageVm
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly ShellVm _shell;
    private readonly RibbonTool _save, _reset, _export, _import;
    private readonly List<RibbonGroup> _tools;
    private readonly HashSet<int> _revealed = new();
    private bool _syncing;

    public SettingsPageVm(ShellVm shell) : base(AppPage.Settings)
    {
        _shell = shell;
        _save = new RibbonTool("save", "保存", Save, tip: "保存本机设置");
        _reset = new RibbonTool("reset", "恢复默认", RestoreDefaults, tip: "本机设置恢复为默认值（保存后写进文件；设备列表不变）");
        _export = new RibbonTool("download", "导出配置", () => ExportConfig(), tip: "导出本机配置（不含令牌）");
        _import = new RibbonTool("upload", "导入配置", () => _ = ImportConfigAsync(), tip: "从导出的配置文件读入本机设置（保存后生效到文件）");
        _tools = new List<RibbonGroup> { new("设置", new[] { _save, _reset }), new("配置", new[] { _export, _import }) };

        foreach (var (k, l, ro) in SettingsTexts.Local) _navLocal.Add(new SetNavVm(k, l, ro, Pick));
        foreach (var (k, l, ro) in SettingsTexts.Device) _navDevice.Add(new SetNavVm(k, l, ro, Pick));
        _navAbout = new SetNavVm("about", "关于", false, Pick);
        _devGroup = SetNavVm.Group("设备参数");
        Nav.Add(SetNavVm.Group("本机"));
        foreach (var n in _navLocal) Nav.Add(n);
        Nav.Add(_devGroup);
        foreach (var n in _navDevice) Nav.Add(n);
        Nav.Add(SetNavVm.Sep());
        Nav.Add(_navAbout);

        AddDeviceCommand = new RelayCommand(() => _shell.OpenDialog("add"));
        TestAllCommand = new RelayCommand(() => _ = TestAllAsync());
        BrowseCommand = new RelayCommand(() => _ = BrowseAsync());
        // 压力单位：MPa / bar / psi（同触屏的三种）；改了马上全界面换算（不等「保存」，同其它本机设置）
        UnitOptions = Core.Format.PressureUnits.All.Select(u => new SegOption { Value = Core.Format.PressureUnits.Text(u), Label = Core.Format.PressureUnits.Text(u) }).ToArray();
        _shell.Prefs.Changed += (_, _) => SyncPrefs();
        Pid = new PidSectionVm(shell, () => Dev, () => ReferenceEquals(_shell.Page, this) && Section == "pid");
        SyncPrefs();
        ApplySection();
    }

    public override IReadOnlyList<RibbonGroup> Tools => _tools;

    private readonly List<SetNavVm> _navLocal = new(), _navDevice = new();
    private readonly SetNavVm _navAbout, _devGroup;

    /// <summary>左侧导航（组标题、各节、分隔）。</summary>
    public ObservableCollection<SetNavVm> Nav { get; } = new();

    // ———————————————————— 节 ————————————————————

    /// <summary>当前这一节（样稿 SETP.sec）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGen), nameof(IsConn), nameof(IsNight), nameof(IsLim), nameof(IsPid), nameof(IsComm), nameof(IsStore), nameof(IsUpt), nameof(IsAbout), nameof(IsDeviceSection))]
    private string _section = "gen";

    partial void OnSectionChanged(string value)
    {
        if (!SettingsTexts.IsKnown(value)) { Section = "gen"; return; }
        ApplySection();
    }

    public bool IsGen => Section == "gen";
    public bool IsConn => Section == "conn";
    public bool IsNight => Section == "night";
    public bool IsLim => Section == "lim";
    public bool IsPid => Section == "pid";
    public bool IsComm => Section == "comm";
    public bool IsStore => Section == "store";
    public bool IsUpt => Section == "upt";
    public bool IsAbout => Section == "about";
    public bool IsDeviceSection => SettingsTexts.IsDeviceSection(Section);

    /// <summary>灰带标题（这一节的名字）与副标题（本机 / 1号机 · HT-PR8 / 关于没有）。</summary>
    [ObservableProperty] private string _title = "常规";
    [ObservableProperty] private string _sub = "本机";
    [ObservableProperty] private bool _hasSub = true;

    private void Pick(string key) => Section = key;

    private void ApplySection()
    {
        foreach (var n in Nav) if (n.IsItem) n.IsOn = n.Key == Section;
        Title = SettingsTexts.Title(Section);
        Refresh(_shell.Fleet);
    }

    /// <summary>参数跟着的设备（工具条的设备下拉；设置页的范围是「一台」）。</summary>
    public DeviceVm? Dev => _shell.Nav.DeviceK > 0 ? _shell.Fleet.Device(_shell.Nav.DeviceK) : _shell.Fleet.Devices.FirstOrDefault();

    // ———————————————————— 本机 · 常规 ————————————————————

    [ObservableProperty] private string _clientNameText = "";
    [ObservableProperty] private string _dataDirText = "";
    [ObservableProperty] private string _dataDirHint = "";
    /// <summary>数据目录框的水印（空着 = 默认目录）。</summary>
    public string DataDirWatermark => "默认：文稿/ParallelReactor";
    [ObservableProperty] private string _unitKey = "MPa";
    [ObservableProperty] private bool _autoConnect = true;
    [ObservableProperty] private bool _alarmSound = true;
    public IReadOnlyList<SegOption> UnitOptions { get; }
    public ICommand BrowseCommand { get; }

    /// <summary>选文件夹（视图给：系统的文件夹对话框；截图 / 测试没有时只提示）。</summary>
    public Func<Task<string?>>? PickFolder { get; set; }
    /// <summary>选一个配置文件读入（视图给）。</summary>
    public Func<Task<string?>>? OpenFile { get; set; }

    partial void OnClientNameTextChanged(string value) => RefreshDirty();
    partial void OnUnitKeyChanged(string value) { if (!_syncing) SetPrefs(p => p with { PressureUnit = Core.Format.PressureUnits.Normalize(value) }); }
    partial void OnDataDirTextChanged(string value) { if (!_syncing) SetPrefs(p => p with { DataDir = (value ?? "").Trim() }); }
    partial void OnAutoConnectChanged(bool value) { if (!_syncing) SetPrefs(p => p with { AutoConnect = value }); }
    partial void OnAlarmSoundChanged(bool value) { if (!_syncing) SetPrefs(p => p with { AlarmSound = value }); }

    // ———————————————————— 本机 · 无人值守 ————————————————————

    [ObservableProperty] private bool _preventSleep = true;
    [ObservableProperty] private bool _backfill = true;
    [ObservableProperty] private string _sleepState = "";
    [ObservableProperty] private string _backfillState = "";
    [ObservableProperty] private string _afterRestart = "";
    public string ReconnectText => SettingsTexts.ReconnectText;
    public string ReconnectNote => SettingsTexts.ReconnectNote;
    public ObservableCollection<SetLostRow> Lost { get; } = new();

    partial void OnPreventSleepChanged(bool value) { if (!_syncing) SetPrefs(p => p with { PreventSleep = value }); }
    partial void OnBackfillChanged(bool value) { if (!_syncing) SetPrefs(p => p with { Backfill = value }); }

    private void SetPrefs(Func<PcPrefs, PcPrefs> change) => _shell.Prefs.Set(change(_shell.Prefs.Current));

    /// <summary>本机设置变了（这里、报警弹窗的声音开关、恢复默认、导入）：开关和文字跟着。</summary>
    private void SyncPrefs()
    {
        var p = _shell.Prefs.Current;
        _syncing = true;
        try
        {
            // 空 = 默认目录（框里空着，水印写「默认：文稿/ParallelReactor」—— 不把带用户名的完整路径摆在界面上）
            if ((DataDirText ?? "").Trim() != p.DataDir) DataDirText = p.DataDir;
            UnitKey = p.PressureUnit;
            AutoConnect = p.AutoConnect;
            AlarmSound = p.AlarmSound;
            PreventSleep = p.PreventSleep;
            Backfill = p.Backfill;
        }
        finally { _syncing = false; }
        DataDirHint = DataDirs.FromEnvironment != null
            ? "现在用环境变量 PR_CONTROL_DATA 指定的目录 · 这里的设置在不设它时生效"
            : "下载的会话数据、报告、导出、实验方案、性能测试的输出都放在这里 · 空着 = 文稿/ParallelReactor";
        AfterRestart = SettingsTexts.AfterRestart(p.AutoConnect);
        RefreshDirty();
        RefreshNight();
    }

    private void RefreshDirty()
    {
        bool dirty = _shell.Prefs.Dirty || ClientDirty;
        _save.Reason = dirty ? null : "没有未保存的修改";
        _save.Tip = dirty ? "保存本机设置" : "没有未保存的修改";
    }

    private bool ClientDirty => ClientNameText.Trim().Length > 0 && ControlConfig.NormalizeClientName(ClientNameText) != _shell.Fleet.ClientName;

    /// <summary>有没保存的改动（本机设置或客户端名）。</summary>
    public bool IsDirty => _shell.Prefs.Dirty || ClientDirty;

    // ———————————————————— 本机 · 设备连接 ————————————————————

    public ObservableCollection<SetDevRowVm> Devices { get; } = new();
    [ObservableProperty] private string _connSummary = "";
    [ObservableProperty] private bool _testing;
    public ICommand AddDeviceCommand { get; }
    public ICommand TestAllCommand { get; }

    private void RefreshConn()
    {
        var f = _shell.Fleet;
        var ds = f.Devices;
        while (Devices.Count > ds.Count) Devices.RemoveAt(Devices.Count - 1);
        for (int i = 0; i < ds.Count; i++)
        {
            var d = ds[i];
            if (i >= Devices.Count || Devices[i].K != d.K)
            {
                var row = new SetDevRowVm(d.K, ToggleEye, k => _shell.OpenDialog("edit", k.ToString(Inv)), SetAuto);
                if (i < Devices.Count) Devices[i] = row; else Devices.Add(row);
            }
            var r = Devices[i];
            var st = f.SettingsOf(d.K);
            r.Alias = d.Alias;
            r.Model = d.ModelLabel;
            r.Host = d.Host.Length > 0 ? d.Host : st?.Host ?? "";
            r.Port = (d.Port > 0 ? d.Port : st?.Port ?? 0).ToString(Inv);
            r.Revealed = _revealed.Contains(d.K);
            r.Token = r.Revealed ? (st?.Token is { Length: > 0 } t ? t : "（没有保存令牌）") : SettingsTexts.TokenMask;
            r.EyeTip = r.Revealed ? "隐藏令牌" : "显示令牌";
            r.AutoOn = f.AutoConnectOf(d.K);
            r.AutoReason = f.AutoConnectReason(d.K) is { Length: > 0 } why && !r.AutoOn ? why : null;
            (r.State, r.StateTone, r.StateDot) = d.Conn switch
            {
                ConnState.Online => ("在线", BadgeTone.Ok, true),
                ConnState.Connecting => ("正在连接…", BadgeTone.None, false),
                ConnState.Offline => ("连接中断", BadgeTone.Err, false),
                _ => ("已断开", BadgeTone.None, false),
            };
        }
        int on = ds.Count(d => d.Conn == ConnState.Online);
        ConnSummary = ds.Count.ToString(Inv) + " 台 · " + (ds.Count == 0 ? "还没有设备" : on == ds.Count ? "全部在线" : on.ToString(Inv) + " 台在线");
    }

    private void ToggleEye(SetDevRowVm r)
    {
        if (!_revealed.Remove(r.K)) _revealed.Add(r.K);
        RefreshConn();
    }

    private void SetAuto(int k, bool on)
    {
        var why = _shell.Fleet.SetAutoConnect(k, on);
        if (why.Length > 0) _shell.Toast(why, ToastKind.Warn);
        RefreshConn();
    }

    /// <summary>「测试连接」：每台按设备管理里的地址 / 令牌测一次（GET /health + /device；演示直接成功），汇总成一条提示。</summary>
    public async Task TestAllAsync()
    {
        if (Testing) return;
        var f = _shell.Fleet;
        if (f.Devices.Count == 0) { _shell.Toast("还没有设备 · 先「添加设备」", ToastKind.Info); return; }
        Testing = true;
        try
        {
            var bad = new List<string>();
            foreach (var d in f.Devices.ToList())
            {
                var st = f.SettingsOf(d.K);
                if (st == null) continue;
                var r = await f.TestConnectionAsync(st.Host, st.Port, st.Token, st.ModelKey).ConfigureAwait(true);
                if (!r.Ok) bad.Add(d.Alias + "：" + r.Text);
            }
            int n = f.Devices.Count;
            _shell.Toast(bad.Count == 0 ? "测试连接：" + n.ToString(Inv) + " 台全部连通" : "测试连接：" + (n - bad.Count).ToString(Inv) + "/" + n.ToString(Inv) + " 台连通 · " + string.Join("；", bad),
                bad.Count == 0 ? ToastKind.Ok : ToastKind.Warn);
        }
        finally { Testing = false; }
    }

    private async Task BrowseAsync()
    {
        if (PickFolder == null) { _shell.Toast("选择文件夹：直接在框里填路径也可以", ToastKind.Info); return; }
        var dir = await PickFolder().ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(dir)) DataDirText = dir;
    }

    // ———————————————————— 无人值守 ————————————————————

    private void RefreshNight()
    {
        var sl = _shell.Sleep;
        SleepState = SettingsTexts.SleepState(_shell.Prefs.Current.PreventSleep, sl.Wanted, sl.Held, sl.Mechanism, sl.Error);
        BackfillState = !_shell.Prefs.Current.Backfill ? "已关闭 · 重连后从最新数据接着显示，断开那一段在曲线上空着"
            : _shell.Fleet.IsDemo ? "演示数据没有补拉（断线横条照样稿写）"
            : LastBackfillText() ?? "最近没有断线";
        var ds = _shell.Fleet.Devices;
        var rows = ds.Select(d =>
        {
            var m = d.Source.Plant.OnRemoteLost;
            return new SetLostRow(d.Alias, SettingsTexts.OnLost(m), m == "keep", SettingsTexts.OnLostNote(m));
        }).ToList();
        if (Lost.Count != rows.Count || !Lost.SequenceEqual(rows))
        {
            Lost.Clear();
            foreach (var r in rows) Lost.Add(r);
        }
    }

    /// <summary>最近一次补拉（实时设备）：「1号机 14:31 补拉了 146 条」/「正在补拉…」/「补拉失败（…）」。</summary>
    private string? LastBackfillText()
    {
        var all = _shell.Fleet.Devices.Select(d => (d, d.Source as LiveDeviceSource)).Where(x => x.Item2?.Backfill != null)
            .OrderByDescending(x => x.Item2!.Backfill!.ToUtc).ToList();
        if (all.Count == 0) return null;
        var (dv, live) = all[0];
        var b = live!.Backfill!;
        var at = b.ToUtc.ToLocalTime().ToString("HH:mm", Inv);
        return b.Pending ? dv.Alias + " 正在补拉 " + at + " 之前断开的那一段…"
            : b.Error is { } e ? dv.Alias + " " + at + " 补拉失败（" + e + "）"
            : dv.Alias + " " + at + " 重连后补拉了 " + (b.Rows ?? 0).ToString(Inv) + " 条";
    }

    // ———————————————————— 工具条 ————————————————————

    /// <summary>「保存」：本机客户端名（实时）+ 本机设置写进 devices.json。</summary>
    public void Save()
    {
        string? warn = null;
        if (ClientDirty)
        {
            var err = _shell.Fleet.SetClientName(ClientNameText);
            if (err.Length > 0) { warn = err; ClientNameText = _shell.Fleet.ClientName; }
        }
        bool wrote = _shell.Prefs.Save();
        RefreshDirty();
        if (warn != null) _shell.Toast(warn, ToastKind.Warn);
        _shell.Toast(wrote || !_shell.Fleet.IsDemo && _shell.Prefs.CanWrite ? "本机设置已保存" : "本机设置已保存（演示数据：只在本次运行里生效，不写文件）", ToastKind.Ok);
    }

    private void RestoreDefaults()
    {
        _shell.Prefs.Set(PcPrefs.Default);
        _shell.Toast("本机设置已恢复默认 · 点「保存」写进文件", ToastKind.Info);
    }

    /// <summary>导出目录（测试换成临时目录）：数据目录 / export。</summary>
    public static Func<string> ExportDir { get; set; } = () => Path.Combine(DataDirs.Root, "export");

    /// <summary>「导出配置」：本机客户端名、本机设置、设备列表（别名、地址、端口、自动连接；<b>不含令牌</b>）→ 数据目录/export/pc-config-时刻.json。</summary>
    public string? ExportConfig()
    {
        try
        {
            var dir = ExportDir();
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "pc-config-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", Inv) + ".json");
            File.WriteAllText(path, ConfigJson(), new UTF8Encoding(false));
            _shell.Toast("已导出本机配置（不含令牌）→ " + path, ToastKind.Ok);
            return path;
        }
        catch (Exception ex)
        {
            _shell.Toast("导出失败：" + ex.Message, ToastKind.Err);
            return null;
        }
    }

    /// <summary>导出的内容（同 devices.json 的形状，没有令牌、没有设备 id）。</summary>
    public string ConfigJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            var p = _shell.Prefs.Current;
            w.WriteStartObject();
            w.WriteNumber("version", ControlConfig.CurrentVersion);
            w.WriteString("clientName", _shell.Fleet.ClientName);
            w.WriteStartObject("pc");
            w.WriteString("dataDir", p.DataDir);
            w.WriteString("pressureUnit", p.PressureUnit);
            w.WriteBoolean("autoConnect", p.AutoConnect);
            w.WriteBoolean("alarmSound", p.AlarmSound);
            w.WriteBoolean("preventSleep", p.PreventSleep);
            w.WriteBoolean("backfill", p.Backfill);
            w.WriteEndObject();
            w.WriteStartArray("devices");
            foreach (var d in _shell.Fleet.Devices)
            {
                var st = _shell.Fleet.SettingsOf(d.K);
                w.WriteStartObject();
                w.WriteString("alias", d.Alias);
                w.WriteString("host", st?.Host ?? d.Host);
                w.WriteNumber("port", st?.Port ?? d.Port);
                w.WriteBoolean("autoConnect", st?.AutoConnect ?? false);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    /// <summary>「导入配置」：读一份导出的配置，套用其中的本机设置和客户端名（不保存；设备列表不导入 —— 令牌不在文件里，设备请在「设备连接」里添加）。</summary>
    public async Task ImportConfigAsync(string? path = null)
    {
        path ??= OpenFile != null ? await OpenFile().ConfigureAwait(true) : null;
        if (path == null) { if (OpenFile == null) _shell.Toast("导入配置：选一个「导出配置」得到的 .json 文件", ToastKind.Info); return; }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), ControlConfig.TolerantJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("pc", out var pc) && pc.ValueKind == JsonValueKind.Object) _shell.Prefs.Set(ControlConfig.ReadPrefs(pc));
            if (root.TryGetProperty("clientName", out var cn) && cn.ValueKind == JsonValueKind.String && ControlConfig.NormalizeClientName(cn.GetString()) is { Length: > 0 } name)
                ClientNameText = name;
            RefreshDirty();
            _shell.Toast("已读入本机设置 · 点「保存」写进文件（设备列表不导入：令牌不在文件里）", ToastKind.Info);
        }
        catch (Exception ex) { _shell.Toast("导入失败：" + ex.Message, ToastKind.Err); }
    }

    // ———————————————————— 刷新 ————————————————————

    public override void Refresh(IFleetView fleet)
    {
        var d = Dev;
        _devGroup.Label = (d?.Alias ?? "设备") + " 参数";
        HasSub = Section != "about";
        Sub = IsDeviceSection ? (d == null ? "没有设备" : d.Alias + " · " + d.ModelLabel) : "本机";
        if (string.IsNullOrEmpty(ClientNameText) || !ClientDirty && ClientNameText != _shell.Fleet.ClientName) ClientNameText = _shell.Fleet.ClientName;
        RefreshDirty();
        if (Section == "pid") RefreshLock(d);
        Pid.Refresh();   // 整定的开始 / 结束每台都要看（不在这一节时也看），只在显示时才画
        switch (Section)
        {
            case "conn": RefreshConn(); break;
            case "night": RefreshNight(); break;
            case "lim": RefreshLim(d); break;
            case "comm": RefreshComm(d); break;
            case "store": RefreshStore(d); break;
            case "upt": RefreshUpt(d); break;
            case "about": RefreshAbout(d); break;
        }
    }

    public override void ApplyParams(Func<string, string?> param)
    {
        if (param("set") is { } s && SettingsTexts.IsKnown(s)) Section = s;
        if (Section == "pid") Pid.ApplyParams(param);
    }

    public override void ResetView()
    {
        _revealed.Clear();
        Pid.Reset();
        Section = "gen";
        ClientNameText = _shell.Fleet.ClientName;
        if (_shell.Prefs.Dirty) _shell.Prefs.Revert();
    }
}
