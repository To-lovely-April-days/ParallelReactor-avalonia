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
using ParallelReactor.Commands;
using ParallelReactor.Models;

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

    // 写入口一律转给 Ops.Try*（守卫、校验与提示文字在命令层 OperatorCommands.Temp / Settings）。
    private static CommandSource Src => MainViewModel.Src;
    private OperatorCommands Ops => _main.Ops;

    /// <summary>退出整个程序（弹确认框，确认后关闭应用）。</summary>
    [RelayCommand]
    [ViewEntry("只打开退出确认框；确认后经 MainViewModel.ConfirmExit → TryExitApp")]
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
    [ViewEntry("切换界面显示档（附录 B：纯界面状态；C11 之后档位 PID 以 SetBand 存档为准）")]
    private void SelectTempBand(string idx)
    {
        if (int.TryParse(idx, out var n)) TempBandIndex = n;
    }

    /// <summary>改「低温/中温」分界（=低温档上限=中温档下限）。</summary>
    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TrySetBandBound))]
    private void EditBound1()
        => _main.Keyboard.OpenNumeric("低温 / 中温 分界", TempBands[0].Hi, "℃", 10, TempBands[1].Hi - 10,
            v => Ops.TrySetBandBound(Src, 1, v));

    /// <summary>改「中温/高温」分界（=中温档上限=高温档下限）。</summary>
    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TrySetBandBound))]
    private void EditBound2()
        => _main.Keyboard.OpenNumeric("中温 / 高温 分界", TempBands[1].Hi, "℃", TempBands[0].Hi + 10, 390,
            v => Ops.TrySetBandBound(Src, 2, v));

    /// <summary>自整定按钮（再次点击=取消并停止输出）。SP 未设时先弹键盘设整定目标温度——
    /// AI-8 整定是在 SP 附近振荡，SP=0 时加热不会开、仪表会永远卡在「整定中」。
    /// 打开键盘前先预检（急停锁定 / 温控未配置或离线时不允许发起），确认时命令层再判一次（K3）。</summary>
    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TryCancelAutotune), nameof(OperatorCommands.TryStartAutotune))]
    private Task Autotune(Services.TempChannel ch)
    {
        if (ch.Autotuning) return _main.LocalAsync(Ops.TryCancelAutotune(Src, ch.Id));
        int id = ch.Id;
        if (_main.LocalOk(Ops.CheckAutotune(Src, id)))
            _main.Keyboard.OpenNumeric($"{ch.Name} 整定目标温度（整定在此温度附近振荡）", 150, "℃", 40, _main.Settings.TMaxSp,
                v => Ops.TryStartAutotune(Src, id, v));
        return Task.CompletedTask;
    }

    /// <summary>一键把启用回路数 Ctn 设为 8（红色徽章点击触发）。</summary>
    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TryFixCtn))]
    private Task FixCtn() => _main.LocalAsync(Ops.TryFixCtn(Src));

    // PID 手改经命令层（C11：显式档位 = 界面当前显示档）。写入成功记「旧 → 新」，失败记一条未生效；没有 Toast。
    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TrySetPid))]
    private void EditTempP(Services.TempChannel ch)
        => _main.Keyboard.OpenNumeric($"{ch.Name} 比例带 P", ch.P, "", 0, 3200,
            v => Ops.TrySetPid(Src, ch.Id, _main.Temp.CurrentBand, v, ch.I, ch.D));

    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TrySetPid))]
    private void EditTempI(Services.TempChannel ch)
        => _main.Keyboard.OpenNumeric($"{ch.Name} 积分时间 I", ch.I, "s", 0, 3200,
            v => Ops.TrySetPid(Src, ch.Id, _main.Temp.CurrentBand, ch.P, v, ch.D));

    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TrySetPid))]
    private void EditTempD(Services.TempChannel ch)
        => _main.Keyboard.OpenNumeric($"{ch.Name} 微分时间 D", ch.D, "s", 0, 327,
            v => Ops.TrySetPid(Src, ch.Id, _main.Temp.CurrentBand, ch.P, ch.I, v));

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

    /// <summary>压力单位选择行：切换 psi / bar 时更新全局单位（内部仍存 psi，仅换算显示）。被拒时下拉框回到原值。</summary>
    private SetRow PressureUnitRow()
    {
        var row = Wire(SetRow.Sel("压力单位", Models.Units.Pressure, new[] { "MPa", "bar", "psi" }, "Pressure Unit"));
        row.OnSelChanged = u => Ops.TrySetPressureUnit(Src, u);
        return row;
    }

    /// <summary>行的命令结果统一转成 Toast（被拒时行自己回滚控件值）。</summary>
    private SetRow Wire(SetRow row)
    {
        row.Sink = _main.Local;
        return row;
    }

    /// <summary>事件文字里的数值按 InvariantCulture（小数点「.」）。</summary>
    private static string Fmt(double v, string unit)
    {
        string n = v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        return unit.Length > 0 ? $"{n} {unit}" : n;
    }

    /// <summary>+/− 连点后尚未记下的设置修改（停手 1.5s 才记）：退出程序前由 <see cref="FlushPendingSettingLogs"/> 立即记下。</summary>
    private readonly List<(DispatcherTimer Timer, Action Commit)> _settles = new();

    /// <summary>立即记下所有尚在等待的设置修改事件（退出程序前调用）。</summary>
    public void FlushPendingSettingLogs()
    {
        foreach (var (t, commit) in _settles)
        {
            if (!t.IsEnabled) continue;
            t.Stop();
            commit();
        }
    }

    /// <summary>「超压时停止加热」开关：改动立即生效并落盘（安全相关，不等退出）。</summary>
    private SetRow StopHeatOnOverPressureRow()
    {
        var row = Wire(SetRow.Toggle("超压时停止加热", "超压联锁关阀、中止气路的同时下发该通道 At=4（默认开）", _main.Settings.StopHeatOnOverPressure));
        row.ToggleApply = v => Ops.TrySetStopHeatOnOverPressure(Src, v);
        return row;
    }

    /// <summary>
    /// 构造一行可编辑数值（标签 / 单位 / 范围 / 步进 / 说明取自 <see cref="SettingSpecs"/>）：写入经 Ops.TrySetSetting，点数值弹键盘。
    /// 设置修改事件：+/− 连点每步都会写入，停手 1.5s 后才记一条（旧值 → 最终值），避免一步一条（deferLog）。
    /// </summary>
    private SetRow Num(SettingKey key)
    {
        var spec = SettingSpecs.Of(key);
        var row = Wire(SetRow.Num(spec.Label, spec.Get(_main.Settings), spec.Unit, spec.Min, spec.Max, spec.Step, spec.Sub));
        double committed = row.NumValue;
        DispatcherTimer? settle = null;
        void Commit()
        {
            double now = row.NumValue;
            _main.LogSetting(spec.Label, Fmt(committed, spec.Unit), Fmt(now, spec.Unit));
            committed = now;
        }
        row.TryApply = v =>
        {
            var t = Ops.TrySetSetting(Src, key, v, deferLog: true);
            if (t.IsCompleted && !(t.IsCompletedSuccessfully && t.Result.Ok)) return t;   // 被拒：值已回滚，不记事件
            if (settle == null)
            {
                settle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                settle.Tick += (_, _) => { settle.Stop(); Commit(); };
                _settles.Add((settle, Commit));
            }
            settle.Stop();
            settle.Start();
            return t;
        };
        row.EditRequest = () => _main.Keyboard.OpenNumeric(spec.Label, row.NumValue, spec.Unit, spec.Min, spec.Max, row.Submit);
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
                    Num(SettingKey.TMaxSp),
                    Num(SettingKey.PMaxSp),
                    Num(SettingKey.RpmMax),
                    Num(SettingKey.VolMax)),
                SetSection.Card("报警阈值",
                    Num(SettingKey.OverPressure),
                    StopHeatOnOverPressureRow(),
                    Num(SettingKey.OverTemp),
                    Num(SettingKey.HeatTimeout),
                    Num(SettingKey.PressDeviation),
                    Num(SettingKey.LeakRate),
                    Num(SettingKey.LeakReminderDays)),
                SetSection.Card("泄漏测试（惰性气体低压保压）",
                    Num(SettingKey.LeakTestPsi),
                    Num(SettingKey.LeakSettleMin),
                    Num(SettingKey.LeakMeasureMin)),
                SetSection.Card("定值加热防超调（先升到 目标−Δ 判稳，再慢推到目标）",
                    Num(SettingKey.ApproachDelta),
                    Num(SettingKey.ApproachFastRate),
                    Num(SettingKey.ApproachFinalZone),
                    Num(SettingKey.ApproachRate),
                    Num(SettingKey.ApproachSettleBand),
                    Num(SettingKey.ApproachSettleSec)),
                SetSection.Card("搅拌电机电流（雷赛 DM2C · 即时写入驱动器并存 EEPROM）",
                    Num(SettingKey.StirCurrent),
                    Num(SettingKey.StirStandbyPct),
                    Num(SettingKey.StirRampMs),
                    Num(SettingKey.StirMicrostep)),
                SetSection.Card("压力变送器（8AI）",
                    Num(SettingKey.PressFullScaleMPa)),
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
                    SetRow.Val("运行记录目录", _main.Logger.Dir,
                        "telemetry-日期.csv 每 1.2s 一行 · events-日期.csv 报警与事件 · leak/ 泄漏测试逐点数据（环境变量 PR_DATA_DIR 可改）"),
                    _diskRow),
                SetSection.Note("曲线导出与外部数据库对接尚未实现。"),
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
    // ===== 写入：三个回调都返回命令层的 OpResult（编译期约束：只能经 Ops.Try*）=====
    // 同步阶段被拒（例如远程控制中 MODE_REMOTE）时把控件值恢复为旧值并重新通知界面；
    // 异步尾部失败（例如 EEPROM 写失败）只提示、不回滚（今天也是「设置已改、设备没写成功」）。

    /// <summary>命令结果的去向（SettingViewModel 设为 MainViewModel.Local：转成 Toast）。</summary>
    public Action<Task<Commands.OpResult>>? Sink;

    /// <summary>开关切换时写入（为 null 的开关只是展示）。</summary>
    public Func<bool, Task<Commands.OpResult>>? ToggleApply;

    /// <summary>下拉选项变化时写入（如切换压力单位；为 null 的下拉框只是展示）。</summary>
    public Func<string, Task<Commands.OpResult>>? OnSelChanged;

    /// <summary>数值变化时写入（经 Ops.TrySetSetting）。</summary>
    public Func<double, Task<Commands.OpResult>>? TryApply;

    /// <summary>点击数值时打开键盘。</summary>
    public Action? EditRequest;

    private static bool Rejected(Task<Commands.OpResult> t) => t.IsCompleted && !(t.IsCompletedSuccessfully && t.Result.Ok);

    /// <summary>回滚后的通知：立即一次，再延后一拍一次（双向绑定的控件可能在同一轮里把值写回，见设计稿 §9 第 4 条）。</summary>
    private void NotifyReverted(params string[] props)
    {
        foreach (var p in props) OnPropertyChanged(p);
        Dispatcher.UIThread.Post(() => { foreach (var p in props) OnPropertyChanged(p); });
    }

    private bool _on;
    public bool On
    {
        get => _on;
        set
        {
            bool old = _on;
            if (!SetProperty(ref _on, value) || ToggleApply is not { } f) return;
            var t = f(value);
            if (Rejected(t)) { _on = old; NotifyReverted(nameof(On)); }   // 直接写字段：不再触发写入
            Sink?.Invoke(t);
        }
    }

    private string _selValue = "";
    public string SelValue
    {
        get => _selValue;
        set
        {
            string old = _selValue;
            if (!SetProperty(ref _selValue, value) || OnSelChanged is not { } f) return;
            var t = f(value);
            if (Rejected(t)) { _selValue = old; NotifyReverted(nameof(SelValue)); }
            Sink?.Invoke(t);
        }
    }

    private double _numValue;
    public double NumValue
    {
        get => _numValue;
        set
        {
            var t = Write(value);
            if (t != null) Sink?.Invoke(t);
        }
    }

    /// <summary>
    /// 写入一个新数值并返回命令结果。值没变或没有写入回调时返回 null。被同步拒绝时恢复旧值。
    /// </summary>
    private Task<Commands.OpResult>? Write(double value)
    {
        double old = _numValue;
        if (!SetProperty(ref _numValue, value, nameof(NumValue))) return null;
        OnPropertyChanged(nameof(NumText));
        if (TryApply is not { } f) return null;
        var t = f(value);
        if (Rejected(t)) { _numValue = old; NotifyReverted(nameof(NumValue), nameof(NumText)); }
        return t;
    }

    /// <summary>键盘确认回调：同 NumValue 赋值，结果交给键盘统一提示。</summary>
    public Task<Commands.OpResult> Submit(double value) => Write(value) ?? Commands.OpResult.Silent.AsTask();

    [RelayCommand]
    [Commands.ViewEntry("只改开关控件值；写入在 On 的 setter → ToggleApply（经 Ops.Try*）")]
    private void Flip() => On = !On;

    [RelayCommand]
    [Commands.ViewEntry("只改数值控件值；写入在 NumValue 的 setter → TryApply（经 Ops.TrySetSetting）")]
    private void Inc() => NumValue = InRange(NumValue + Step);

    [RelayCommand]
    [Commands.ViewEntry("只改数值控件值；写入在 NumValue 的 setter → TryApply（经 Ops.TrySetSetting）")]
    private void Dec() => NumValue = InRange(NumValue - Step);

    /// <summary>
    /// +/− 的结果夹到 [Min, Max]：当前值本身可能在范围外（设置文件未限幅、开机从硬件回填，例如驱动器细分 60000），
    /// 这时按一下就回到范围边界，而不是算出一个仍越界、被 TrySetSetting 拒绝的值（否则 +/− 永远走不回来）。
    /// </summary>
    private double InRange(double v) => Math.Max(Min, Math.Min(Max, v));

    [RelayCommand]
    [Commands.ViewEntry("只打开键盘；确认回调为 Submit → TryApply（经 Ops.TrySetSetting）")]
    private void Edit() => EditRequest?.Invoke();

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
