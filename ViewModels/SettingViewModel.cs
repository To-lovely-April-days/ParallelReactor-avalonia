using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ParallelReactor.ViewModels;

/// <summary>系统设置界面（SETTING Tab）：左侧菜单 + 右侧配置分页。</summary>
public partial class SettingViewModel : ViewModelBase
{
    public ObservableCollection<SetPage> Pages { get; } = new();
    [ObservableProperty] private SetPage? _selected;

    private readonly MainViewModel _main;
    private SetRow? _ipRow;     // 「本机 IP 地址」行，运行时刷新
    private int _netTick;

    // 运行时刷新的行：通讯页 4 个模块状态、关于页运行时长、数据页磁盘空间
    private SetRow _commTemp = null!, _commPress = null!, _commIo = null!, _commStir = null!;
    private readonly SetRow _uptimeRow = SetRow.Val("本次运行时长", "0 分钟");
    private SetRow _diskRow = null!;
    private static readonly System.Diagnostics.Stopwatch Uptime = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>刷新通讯页模块状态 + 运行时长（由时钟 tick 每秒调一次，只改文本，开销很小）。</summary>
    public void RefreshCommStatus()
    {
        SetComm(_commTemp, Services.HardwareOptions.UseRealTemp, _main.TempOnline);
        SetComm(_commPress, Services.HardwareOptions.UseRealAnalog, _main.PressOnline);
        SetComm(_commIo, Services.HardwareOptions.UseRealIo, _main.IoOnline);
        SetComm(_commStir, Services.HardwareOptions.UseRealStir, _main.StirOnline);
        var up = Uptime.Elapsed;
        _uptimeRow.ValText = up.TotalHours >= 1 ? $"{(int)up.TotalHours} 小时 {up.Minutes} 分钟" : $"{up.Minutes} 分钟";
    }

    private static void SetComm(SetRow row, bool configured, bool online)
    {
        row.ChipText = !configured ? "未配置" : online ? "在线" : "离线";
        row.ChipWarn = !configured || !online;
    }

    private static string Port(string model, string? port, int baud)
        => string.IsNullOrWhiteSpace(port) ? $"{model} · 未配置串口" : $"{model} · {port} · {baud} bps";

    /// <summary>程序集版本（csproj 未设 Version 时为 1.0.0）。</summary>
    private static string AppVersion()
    {
        var v = typeof(SettingViewModel).Assembly.GetName().Version;
        return v == null ? "—" : $"v{v.Major}.{v.Minor}.{v.Build}";
    }

    /// <summary>存档所在磁盘的可用 / 总容量。</summary>
    private static string DiskFreeText()
    {
        try
        {
            var root = Path.GetPathRoot(Services.SettingsStore.FilePath);
            if (string.IsNullOrEmpty(root)) return "—";
            var d = new DriveInfo(root);
            return $"{d.AvailableFreeSpace / 1e9:0.0} GB 可用 / 共 {d.TotalSize / 1e9:0.0} GB";
        }
        catch { return "—"; }
    }

    public SettingViewModel(MainViewModel main)
    {
        _main = main;
        Seed();
        Selected = Pages.Count > 0 ? Pages[0] : null;
        RefreshNetworkInfo(force: true);   // 进入即显示当前 IP

        // 加载关于页的二维码图片
        foreach (var p in Pages)
            foreach (var s in p.Sections)
                if (s.IsAbout && !string.IsNullOrEmpty(s.QrUrl))
                    LoadQr(s);
    }

    /// <summary>退出整个程序（弹确认框，确认后关闭应用）。</summary>
    [RelayCommand]
    private void ExitApp() => _main.OpenExitConfirm();

    // ===== 温控 / PID（绑定到集中页）=====
    public Services.TempService Temp => _main.Temp;

    /// <summary>温度档位列表（界面顶部档位选择）。</summary>
    public System.Collections.Generic.List<Services.TempBand> TempBands => _main.Temp.Bands;

    /// <summary>当前选中的档位下标。切换时把界面 PID 存回旧档、载入新档。</summary>
    [ObservableProperty] private int _tempBandIndex;

    partial void OnTempBandIndexChanged(int value) => _main.Temp.SelectBand(value);

    partial void OnSelectedChanged(SetPage? value)
    {
        // 维护各页 IsActive：右侧所有页常驻、只有选中页可见（切换=toggle，不再重建整列）
        foreach (var p in Pages) p.IsActive = ReferenceEquals(p, value);
    }

    [RelayCommand]
    private void SelectTempBand(string idx)
    {
        if (int.TryParse(idx, out var n)) TempBandIndex = n;
    }

    /// <summary>改「低温/中温」分界（=低温档上限=中温档下限）。</summary>
    [RelayCommand]
    private void EditBound1()
        => _main.Keyboard.OpenNumeric("低温 / 中温 分界", TempBands[0].Hi, "℃", 10, TempBands[1].Hi - 10,
            v => { TempBands[0].Hi = v; TempBands[1].Lo = v; _main.PersistSoon(); });

    /// <summary>改「中温/高温」分界（=中温档上限=高温档下限）。</summary>
    [RelayCommand]
    private void EditBound2()
        => _main.Keyboard.OpenNumeric("中温 / 高温 分界", TempBands[1].Hi, "℃", TempBands[0].Hi + 10, 390,
            v => { TempBands[1].Hi = v; TempBands[2].Lo = v; _main.PersistSoon(); });

    /// <summary>自整定按钮（再次点击=取消并停止输出）。SP 未设时先弹键盘设整定目标温度——
    /// AI-8 整定是在 SP 附近振荡，SP=0 时加热不会开、仪表会永远卡在「整定中」。</summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task Autotune(Services.TempChannel ch)
    {
        if (ch.Autotuning)
        {
            await _main.Temp.CancelAutotuneAsync(ch.Id);
            _main.Toast("ok", $"{ch.Name} 已取消自整定并停止输出");
            return;
        }

        // 自整定会让仪表全功率加热振荡：急停锁定 / 温控未配置或离线时不允许发起
        if (_main.HeatBlockReason() is { } why) { _main.Toast("err", why); return; }
        _main.Keyboard.OpenNumeric($"{ch.Name} 整定目标温度（整定在此温度附近振荡）", 150, "℃", 40, _main.Settings.TMaxSp,
                v => _ = StartTuneWithSpAsync(ch, v));
    }

    /// <summary>一键把启用回路数 Ctn 设为 8（红色徽章点击触发）。</summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task FixCtn()
    {
        var err = await _main.Temp.FixCtnAsync();
        _main.Toast(err == null ? "ok" : "err", err ?? "已把启用回路数 Ctn 设为 8，全部 8 通道受控");
    }

    private async System.Threading.Tasks.Task StartTuneWithSpAsync(Services.TempChannel ch, double sp)
    {
        try { await _main.Temp.SetSetpointAsync(ch.Id, sp); }
        catch { _main.Toast("err", "SP 下发失败，未启动整定"); return; }
        var err = await _main.Temp.AutotuneAsync(ch.Id);
        _main.Toast(err == null ? "ok" : "err",
            err == null ? $"{ch.Name} 已设目标 {sp:0}℃ 并启动自整定" : err);
    }

    [RelayCommand]
    private void EditTempP(Services.TempChannel ch)
        => _main.Keyboard.OpenNumeric($"{ch.Name} 比例带 P", ch.P, "", 0, 3200,
            v => _ = _main.Temp.SetPidAsync(ch.Id, v, ch.I, ch.D));

    [RelayCommand]
    private void EditTempI(Services.TempChannel ch)
        => _main.Keyboard.OpenNumeric($"{ch.Name} 积分时间 I", ch.I, "s", 0, 3200,
            v => _ = _main.Temp.SetPidAsync(ch.Id, ch.P, v, ch.D));

    [RelayCommand]
    private void EditTempD(Services.TempChannel ch)
        => _main.Keyboard.OpenNumeric($"{ch.Name} 微分时间 D", ch.D, "s", 0, 327,
            v => _ = _main.Temp.SetPidAsync(ch.Id, ch.P, ch.I, v));

    private volatile bool _netBusy;   // 防止上一次网卡枚举没回来又发起新的

    /// <summary>刷新「本机 IP 地址」（WiFi 优先）。由时钟 tick 每 5s 调一次；force=true 立即刷新。
    /// <para>
    /// 关键：网卡枚举(GetAllNetworkInterfaces/GetIPProperties)在 Linux+WiFi 上可能同步阻塞数秒，
    /// 绝不能在 UI 线程上跑——否则每 5s 就把界面冻住 2-3 秒（点击、菜单高亮全部卡住）。
    /// 放到后台线程执行，取到结果再回主线程写值。
    /// </para></summary>
    public void RefreshNetworkInfo(bool force = false)
    {
        if (_ipRow == null) return;
        if (!force && _netTick++ % 5 != 0) return;   // 1s 一次 tick，节流到约 5s 查一次
        if (_netBusy) return;
        _netBusy = true;
        Task.Run(() =>
        {
            string ip;
            try { ip = GetLocalIPv4() ?? "未连接"; }
            catch { ip = "未连接"; }
            Dispatcher.UIThread.Post(() =>
            {
                if (_ipRow != null) _ipRow.ValText = ip;
                _netBusy = false;
            });
        });
    }

    /// <summary>取当前在用网卡的 IPv4：优先无线网卡(WiFi)，否则取其它已连接网卡（排除回环/虚拟网卡）。</summary>
    private static string? GetLocalIPv4()
    {
        try
        {
            string? wifi = null, other = null;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var name = ni.Name.ToLowerInvariant();
                if (name.StartsWith("docker") || name.StartsWith("veth") ||
                    name.StartsWith("br-") || name.StartsWith("virbr") || name.StartsWith("lo")) continue;

                string? v4 = null;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                    { v4 = ua.Address.ToString(); break; }
                if (v4 == null) continue;

                if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) { wifi = v4; break; }
                other ??= v4;
            }
            return wifi ?? other;
        }
        catch { return null; }
    }

    /// <summary>加载二维码：avares:// 或相对路径走本地打包资源，http(s) 走网络。</summary>
    private static void LoadQr(SetSection s)
    {
        var src = s.QrUrl;
        if (string.IsNullOrEmpty(src)) return;

        if (src.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            _ = LoadHttpAsync(s);
            return;
        }

        try
        {
            var uri = src.StartsWith("avares://", StringComparison.OrdinalIgnoreCase)
                ? new Uri(src)
                : new Uri($"avares://ParallelReactor/{src.TrimStart('/')}");
            using var stream = AssetLoader.Open(uri);
            s.QrImage = new Bitmap(stream);
        }
        catch
        {
            // 资源缺失时留空，不影响其余内容
        }
    }

    private static async Task LoadHttpAsync(SetSection s)
    {
        try
        {
            using var http = new HttpClient();
            var bytes = await http.GetByteArrayAsync(s.QrUrl);
            using var ms = new MemoryStream(bytes);
            var bmp = new Bitmap(ms);
            Dispatcher.UIThread.Post(() => s.QrImage = bmp);
        }
        catch
        {
            // 离线或地址无效时留空
        }
    }

    private static Geometry G(string d) => Geometry.Parse(d);

    /// <summary>压力单位选择行：切换 psi / bar 时更新全局单位（内部仍存 psi，仅换算显示）。</summary>
    private SetRow PressureUnitRow()
    {
        var row = SetRow.Sel("压力单位", Models.Units.Pressure, new[] { "MPa", "bar", "psi" }, "Pressure Unit");
        row.OnSelChanged = Models.Units.SetPressure;   // MainViewModel 订阅 Units.Changed 后刷新界面
        return row;
    }

    /// <summary>构造一行可编辑数值：双向回写全局设置，点数值弹键盘。</summary>
    private SetRow Num(string label, double value, string unit, double min, double max, double step, Action<double> apply, string? sub = null)
    {
        var row = SetRow.Num(label, value, unit, min, max, step, sub);
        row.Apply = apply;
        row.EditRequest = () => _main.Keyboard.OpenNumeric(label, row.NumValue, unit, min, max, v => row.NumValue = v);
        return row;
    }

    private void Seed()
    {
        Pages.Add(new SetPage
        {
            Id = "general", Name = "通用", Title = "通用", Sub = "界面、语言、单位",
            Icon = G("M12 1v6 M12 17v6 M4.22 4.22l4.24 4.24 M15.54 15.54l4.24 4.24 M1 12h6 M17 12h6 M4.22 19.78l4.24-4.24 M15.54 8.46l4.24-4.24 M15 12a3 3 0 1 1-6 0 3 3 0 1 1 6 0"),
            Sections =
            {
                SetSection.Card(
                    SetRow.Sel("语言", "中文（简体）", new[] { "中文（简体）", "English" }, "Language"),
                    PressureUnitRow(),
                    SetRow.Sel("温度单位", "°C", new[] { "°C", "°F" }),
                    SetRow.Val("主题", "深色（默认）")),
                SetSection.Card(
                    SetRow.Toggle("运行前自动检查", "每次启动前弹出 9 项检查清单", true)),
            },
        });

        Pages.Add(new SetPage
        {
            Id = "params", Name = "参数与报警", Title = "参数与报警", Sub = "可设置参数范围与报警阈值",
            Icon = G("M4 21v-7 M4 10V3 M12 21v-9 M12 8V3 M20 21v-5 M20 12V3 M2 14h4 M10 8h4 M18 16h4"),
            Sections =
            {
                SetSection.Card("可设置参数范围",
                    Num("温度 SP 上限", _main.Settings.TMaxSp, "°C", 50, 300, 5, v => _main.Settings.TMaxSp = v),
                    Num("压力 SP 上限", _main.Settings.PMaxSp, "psi", 100, 1000, 10, v => _main.Settings.PMaxSp = v),
                    Num("搅拌转速上限", _main.Settings.RpmMax, "rpm", 500, 3000, 100, v => _main.Settings.RpmMax = v),
                    Num("溶液体积上限", _main.Settings.VolMax, "mL", 5, 200, 5, v => _main.Settings.VolMax = v)),
                SetSection.Card("报警阈值",
                    Num("超压报警", _main.Settings.OverPressure, "psi", 100, 1000, 5, v => _main.Settings.OverPressure = v, "压力 ≥ 此值触发硬报警"),
                    Num("超温报警", _main.Settings.OverTemp, "°C", 50, 400, 5, v => _main.Settings.OverTemp = v, "温度 ≥ 此值触发报警"),
                    Num("升温超时报警", _main.Settings.HeatTimeout, "分钟", 5, 60, 1, v => _main.Settings.HeatTimeout = v, "升温超过此时长未到 SP 时提示"),
                    Num("压力偏离 SP 报警", _main.Settings.PressDeviation, "psi", 1, 50, 1, v => _main.Settings.PressDeviation = v, "稳压后偏离超过此值时提示"),
                    Num("泄漏率阈值", _main.Settings.LeakRate, "psi/hr", 0.5, 10, 0.5, v => _main.Settings.LeakRate = v, "泄漏测试判定不通过的阈值"),
                    Num("泄漏测试提醒周期", _main.Settings.LeakReminderDays, "天", 1, 30, 1, v => _main.Settings.LeakReminderDays = v, "超过此天数未测试则提醒")),
                SetSection.Card("泄漏测试（惰性气体低压保压）",
                    Num("测试压力", _main.Settings.LeakTestPsi, "psi", 10, 150, 5, v => _main.Settings.LeakTestPsi = v,
                        "低压即可检出密封问题；须低于超压报警值的 90%，惰性气体减压阀调到略高于此值"),
                    Num("平衡时间", _main.Settings.LeakSettleMin, "分钟", 1, 30, 1, v => _main.Settings.LeakSettleMin = v,
                        "充压后静置，等气体温度回稳再开始测量"),
                    Num("测量时间", _main.Settings.LeakMeasureMin, "分钟", 5, 120, 5, v => _main.Settings.LeakMeasureMin = v,
                        "越长越能分辨小泄漏；压降速率按温度补偿后的最小二乘拟合计算")),
                SetSection.Card("定值加热防超调（先升到 目标−Δ 判稳，再慢推到目标）",
                    Num("预留温差 Δ", _main.Settings.ApproachDelta, "°C", 0, 30, 0.5, v => _main.Settings.ApproachDelta = v,
                        "须大于实测最大冲温（建议取 1.5 倍）；0 = 关闭逼近、直接写目标"),
                    Num("快段速率", _main.Settings.ApproachFastRate, "°C/min", 0.1, 10, 0.1, v => _main.Settings.ApproachFastRate = v,
                        "离目标大于末段范围时使用；PV 领先时 SP 直接跟随，不暂停"),
                    Num("末段范围", _main.Settings.ApproachFinalZone, "°C", 0.3, 5, 0.1, v => _main.Settings.ApproachFinalZone = v,
                        "只有最后这段慢推，PV 偏高才暂停；建议略大于实测冲温"),
                    Num("末段速率", _main.Settings.ApproachRate, "°C/min", 0.02, 2, 0.02, v => _main.Settings.ApproachRate = v,
                        "防超调的关键：越慢越稳、越省不了时间"),
                    Num("判稳范围", _main.Settings.ApproachSettleBand, "±°C", 0.1, 2, 0.1, v => _main.Settings.ApproachSettleBand = v,
                        "PV 到达 中间SP−此值 后开始判峰；持续落在 ±此值 内也可开始逼近"),
                    Num("判稳时长", _main.Settings.ApproachSettleSec, "s", 10, 900, 10, v => _main.Settings.ApproachSettleSec = v,
                        "兜底条件：PV 过峰（不再上升）会提前开始逼近，不必等满此时长")),
                SetSection.Card("搅拌电机电流（雷赛 DM2C · 即时写入驱动器并存 EEPROM）",
                    Num("峰值电流", _main.Settings.StirCurrent, "A", 0.3, 3.2, 0.1,
                        v => _ = _main.ApplyStirCurrentAsync(v),
                        "按电机铭牌额定电流设：带负载卡顿调大、发烫调小（驱动器硬限 0.3–3.2A）"),
                    Num("待机电流", _main.Settings.StirStandbyPct, "%", 0, 100, 5,
                        v => _ = _main.ApplyStirStandbyPctAsync((int)v),
                        "停转后自动降到此百分比，减少电机发热"),
                    Num("加减速时间", _main.Settings.StirRampMs, "ms/1000rpm", 50, 10000, 50,
                        v => _main.ApplyStirRampMs(v),
                        "起停平缓度：越大越缓。值小起停顿挫/像“起飞”，值大更柔和（1000≈300rpm 用 0.3s 升起）"),
                    Num("细分", _main.Settings.StirMicrostep, "脉冲/转", 200, 51200, 200,
                        v => _ = _main.ApplyStirMicrostepAsync(v),
                        "指令脉冲数/转：越高低速越平顺、越能压共振。常用 10000 / 20000 / 25600")),
                SetSection.Card("压力变送器（8AI）",
                    Num("变送器满量程", _main.Settings.PressFullScaleMPa, "MPa", 0.1, 60, 0.1,
                        v => _main.ApplyPressFullScaleMPa(v),
                        "必须与变送器铭牌量程一致（如 0–10MPa 填 10），否则压力读数按比例偏")),
            },
        });

        Pages.Add(new SetPage
        {
            Id = "temppid", Name = "温控 / PID", Title = "温控 / PID", Sub = "8 通道独立 PID · 自整定（宇电 AI-8 1 拖 8）",
            Icon = G("M14 14.76V3.5a2.5 2.5 0 0 0-5 0v11.26a4.5 4.5 0 1 0 5 0z"),
            Sections = { },   // 自定义渲染：见 SettingView 的温控块
        });

        Pages.Add(new SetPage
        {
            Id = "channels", Name = "通道与气路", Title = "通道与气路", Sub = "8 路 RV · 3 路气源 + 1 路 Vent",
            Icon = G("M3 3h7v7H3z M14 3h7v7h-7z M3 14h7v7H3z M14 14h7v7h-7z"),
            Sections =
            {
                SetSection.Card("气源与排空（气源压力由各路减压阀手动设定，软件不测量）",
                    SetRow.Val("惰性气体", "IO16R 线圈 8"),
                    SetRow.Val("气体 A", "IO16R 线圈 9"),
                    SetRow.Val("气体 B", "IO16R 线圈 10"),
                    SetRow.Val("排空 / 泄压", "IO16R 线圈 11")),
                SetSection.Card("反应釜",
                    SetRow.Val("进气阀 SV1–SV8", "IO16R 线圈 0–7"),
                    SetRow.Val("压力变送器", $"8AI 通道 1–8 · 满量程 {_main.Settings.PressFullScaleMPa:0.###} MPa", "量程在「参数与报警 → 压力变送器」修改")),
            },
        });

        _ipRow = SetRow.Val("本机 IP 地址", "获取中…", "WiFi / 以太网当前地址");
        _commTemp = SetRow.Chip("温控", "", sub: Port("宇电 AI-8", Services.HardwareOptions.TempPort, Services.HardwareOptions.TempBaud));
        _commPress = SetRow.Chip("压力采集", "", sub: Port("艾莫迅 JY-MODBUS-8AI", Services.HardwareOptions.AnalogPort, Services.HardwareOptions.AnalogBaud));
        _commIo = SetRow.Chip("阀门", "", sub: Port("艾莫迅 JY-MODBUS-IO16R", Services.HardwareOptions.IoPort, Services.HardwareOptions.IoBaud));
        _commStir = SetRow.Chip("搅拌驱动", "", sub: Port("雷赛 DM2C-RS432", Services.HardwareOptions.StirPort, Services.HardwareOptions.StirBaud));
        RefreshCommStatus();
        Pages.Add(new SetPage
        {
            Id = "comm", Name = "通讯设置", Title = "通讯设置", Sub = "4 路独立 RS485 · Modbus RTU",
            Icon = G("M5 12.55a11 11 0 0 1 14 0 M1.42 9a16 16 0 0 1 21.16 0 M8.53 16.11a6 6 0 0 1 6.95 0 M12 20h.01"),
            Sections =
            {
                SetSection.Card("网络",
                    _ipRow,
                    SetRow.Val("主机名", Environment.MachineName)),
                SetSection.Card("模块（实时状态）",
                    _commTemp, _commPress, _commIo, _commStir,
                    SetRow.Val("轮询周期", "1.2 s"),
                    SetRow.Val("离线判定", "连续 3 次通讯失败")),
                SetSection.Note("串口在 run.sh 中用环境变量配置（PR_TEMP_PORT / PR_PRESS_PORT / PR_IO_PORT / PR_STIR_PORT）。" +
                                "未配置或离线的模块读数显示「—」，依赖它的操作会被拒绝。"),
            },
        });

        Pages.Add(new SetPage
        {
            Id = "user", Name = "用户与权限", Title = "用户与权限", Sub = "管理员可控制阀门、改高级参数、停用通道",
            Icon = G("M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2 M16 7a4 4 0 1 1-8 0 4 4 0 1 1 8 0"),
            Sections =
            {
                SetSection.Card(
                    SetRow.Val("当前模式", "单用户 · 管理员", "账户登录与权限分级尚未启用，所有操作均以管理员身份进行")),
            },
        });

        _diskRow = SetRow.Val("磁盘可用空间", DiskFreeText());
        Pages.Add(new SetPage
        {
            Id = "data", Name = "数据存储", Title = "数据存储", Sub = "本地设置存档",
            Icon = G("M21 5a9 3 0 1 1-18 0 9 3 0 1 1 18 0 M3 5v14a9 3 0 0 0 18 0V5 M3 12a9 3 0 0 0 18 0"),
            Sections =
            {
                SetSection.Card(
                    SetRow.Val("设置与 PID 存档", Services.SettingsStore.FilePath, "全局设置、报警阈值、温区边界与 8 通道 PID"),
                    _diskRow),
                SetSection.Note("运行记录、曲线导出与外部数据库对接尚未实现。"),
            },
        });

        Pages.Add(new SetPage
        {
            Id = "about", Name = "关于", Title = "关于本系统", Sub = "霍桐仪器多通道平行反应仪控制系统",
            Icon = G("M22 12a10 10 0 1 1-20 0 10 10 0 1 1 20 0 M12 16v-4 M12 8h.01"),
            Sections =
            {
                SetSection.Card(
                    SetRow.Val("硬件型号", "HT-PR8"),
                    SetRow.Val("软件版本", AppVersion()),
                    _uptimeRow),
                SetSection.About(
                    "关于霍桐仪器",
                    "上海霍桐实验仪器有限公司成立于 2015 年，是一家专注于反应设备及成套化工装配产线研发、制造与服务的高新技术企业。主营实验室及工业级反应设备、定制化化工成套装备产线，产品涵盖高压、高温、耐腐蚀等特种反应设备，广泛应用于医药合成、化工工艺开发、新材料研制等场景。",
                    "© 2026 上海霍桐实验仪器有限公司 · Huotong Instruments",
                    "avares://ParallelReactor/Assets/qr.png",
                    new ContactItem("电话", "021-6723-0701"),
                    new ContactItem("邮箱", "Htlab@htlab.com"),
                    new ContactItem("官网", "www.htlab.cn"),
                    new ContactItem("地址", "上海市闵行区东川路 3966 号 3 幢 2 楼")),
            },
        });
    }
}

/// <summary>一个设置分页。</summary>
public partial class SetPage : ObservableObject
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public string Sub { get; set; } = "";
    public Geometry Icon { get; set; } = Geometry.Parse("M6 6h12v12H6z");
    public List<SetSection> Sections { get; } = new();

    /// <summary>是否为当前选中页。右侧「每页建一次、切换只 toggle 可见」用它控制 IsVisible。</summary>
    [ObservableProperty] private bool _isActive;
}

/// <summary>设置页内的一个区块：卡片 / 提示 / 关于公司。</summary>
public partial class SetSection : ObservableObject
{
    public string? Header { get; set; }
    public string Kind { get; set; } = "card";          // card | note | about
    public List<SetRow> Rows { get; } = new();
    public string Text { get; set; } = "";              // note

    // 关于 / 公司
    public string AboutTitle { get; set; } = "";
    public string Intro { get; set; } = "";
    public List<ContactItem> Contacts { get; } = new();
    public string QrUrl { get; set; } = "";
    public string QrCaption { get; set; } = "扫码了解更多";
    public string AboutCopyright { get; set; } = "";

    private Bitmap? _qrImage;
    public Bitmap? QrImage
    {
        get => _qrImage;
        set => SetProperty(ref _qrImage, value);
    }

    public bool HasHeader => !string.IsNullOrEmpty(Header);
    public bool IsCard => Kind == "card";
    public bool IsNote => Kind == "note";
    public bool IsAbout => Kind == "about";

    public static SetSection Card(params SetRow[] rows)
    {
        var s = new SetSection { Kind = "card" };
        s.Rows.AddRange(rows);
        return s;
    }
    public static SetSection Card(string header, params SetRow[] rows)
    {
        var s = Card(rows);
        s.Header = header;
        return s;
    }
    public static SetSection Note(string text) => new() { Kind = "note", Text = text };

    public static SetSection About(string title, string intro, string copyright, string qrUrl, params ContactItem[] contacts)
    {
        var s = new SetSection { Kind = "about", AboutTitle = title, Intro = intro, AboutCopyright = copyright, QrUrl = qrUrl };
        s.Contacts.AddRange(contacts);
        return s;
    }
}

/// <summary>关于页的一条联系方式。</summary>
public class ContactItem
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
    public ContactItem(string label, string value) { Label = label; Value = value; }
}

/// <summary>设置卡片里的一行：值 / 徽标 / 开关。</summary>
public partial class SetRow : ObservableObject
{
    public string Label { get; set; } = "";
    public string? Sub { get; set; }
    public bool LabelBold { get; set; }
    public string Kind { get; set; } = "val";           // val | chip | toggle | num
    private string _valText = "";
    public string ValText { get => _valText; set => SetProperty(ref _valText, value); }   // 可运行时刷新（如 IP）

    private string _chipText = "";
    public string ChipText { get => _chipText; set => SetProperty(ref _chipText, value); }   // 可运行时刷新（如模块在线状态）

    private bool _chipWarn;
    public bool ChipWarn
    {
        get => _chipWarn;
        set
        {
            if (!SetProperty(ref _chipWarn, value)) return;
            OnPropertyChanged(nameof(ChipBg));
            OnPropertyChanged(nameof(ChipBorder));
            OnPropertyChanged(nameof(ChipFg));
        }
    }
    public string? Trail { get; set; }
    public string Unit { get; set; } = "";
    public double Min { get; set; }
    public double Max { get; set; } = 9999;
    public double Step { get; set; } = 1;
    public string[] Options { get; set; } = System.Array.Empty<string>();
    [ObservableProperty] private bool _on;

    private string _selValue = "";
    public string SelValue { get => _selValue; set { if (SetProperty(ref _selValue, value)) OnSelChanged?.Invoke(value); } }

    /// <summary>下拉选项变化时回调（如切换压力单位）。</summary>
    public Action<string>? OnSelChanged;

    /// <summary>数值变化时回写全局设置。</summary>
    public Action<double>? Apply;
    /// <summary>点击数值时打开键盘。</summary>
    public Action? EditRequest;

    private double _numValue;
    public double NumValue
    {
        get => _numValue;
        set { if (SetProperty(ref _numValue, value)) { OnPropertyChanged(nameof(NumText)); Apply?.Invoke(value); } }
    }

    [RelayCommand] private void Flip() => On = !On;
    [RelayCommand] private void Inc() => NumValue = Math.Min(Max, NumValue + Step);
    [RelayCommand] private void Dec() => NumValue = Math.Max(Min, NumValue - Step);
    [RelayCommand] private void Edit() => EditRequest?.Invoke();

    private static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));

    public bool IsVal => Kind == "val";
    public bool IsChip => Kind == "chip";
    public bool IsToggle => Kind == "toggle";
    public bool IsNum => Kind == "num";
    public bool IsSel => Kind == "sel";

    // 仅当本行为该类型时返回自身，否则 null。供 ContentControl 惰性实例化贵控件
    // （下拉框/开关/步进器）：非该类型的行 Content=null → 模板不构建，切换设置分类时
    // 少建大量隐藏控件（尤其每行原本都白建一个 ComboBox），消除左菜单点击卡顿。
    public object? ToggleSelf => IsToggle ? this : null;
    public object? NumSelf => IsNum ? this : null;
    public object? SelSelf => IsSel ? this : null;
    public string NumText => Unit.Length > 0 ? $"{NumValue:0.#} {Unit}" : $"{NumValue:0.#}";
    public bool HasSub => !string.IsNullOrEmpty(Sub);
    public bool HasTrail => !string.IsNullOrEmpty(Trail);

    public IBrush LabelBrush => LabelBold ? B("#17171c") : B("#54545e");
    public FontWeight LabelWeight => LabelBold ? FontWeight.Bold : FontWeight.Normal;

    public IBrush ChipBg => ChipWarn ? B("#24F0A830") : B("#2484CC16");
    public IBrush ChipBorder => ChipWarn ? B("#4DF0A830") : B("#4D84CC16");
    public IBrush ChipFg => ChipWarn ? B("#f0a830") : B("#a3e635");

    public static SetRow Val(string label, string val, string? sub = null)
        => new() { Kind = "val", Label = label, ValText = val, Sub = sub };
    public static SetRow Chip(string label, string chip, bool warn = false, string? trail = null, string? sub = null, bool labelBold = false)
        => new() { Kind = "chip", Label = label, ChipText = chip, ChipWarn = warn, Trail = trail, Sub = sub, LabelBold = labelBold };
    public static SetRow Toggle(string label, string? sub, bool on)
        => new() { Kind = "toggle", Label = label, Sub = sub, On = on };
    public static SetRow Num(string label, double value, string unit, double min, double max, double step, string? sub = null)
        => new() { Kind = "num", Label = label, NumValue = value, Unit = unit, Min = min, Max = max, Step = step, Sub = sub };
    public static SetRow Sel(string label, string value, string[] options, string? sub = null)
        => new() { Kind = "sel", Label = label, SelValue = value, Options = options, Sub = sub };
}
