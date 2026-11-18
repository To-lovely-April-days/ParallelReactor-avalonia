using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using ParallelReactor.Api;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages.Settings.Pid;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Pages.Settings;

/// <summary>
/// 设置页的「设备参数」几节（融合版 F8，样稿 setBody 的 lim / pid / comm / store / upt）与「关于」。数据：真机 = GET /settings（连上后取一次）、
/// /device/comm（每 5 s）、快照（PV、输出、溶液体积）；演示 = 样稿的数值（<see cref="DemoSettings"/>）。只读的几节写「只能在触屏上改 · 触屏操作」。
/// </summary>
public sealed partial class SettingsPageVm
{
    // ———————————————————— 共用：设备的设置 ————————————————————

    /// <summary>这一台的设置：真机 GET /settings；演示 = 样稿的数值；真机还没取到（没连上过）为 null。</summary>
    public static SettingsDto? SettingsOf(DeviceVm d) => d.Source.Settings ?? (d.Source.IsDemo ? DemoSettings(d) : null);

    /// <summary>「设备参数只能在 1号机触屏上修改 · 这里只读」（样稿 lock 条）。</summary>
    [ObservableProperty] private string _lockText = "";
    /// <summary>真机还没取到设置（没连上过）时的说明；有设置时为 null。</summary>
    [ObservableProperty] private string? _noSettings;

    private void RefreshLock(DeviceVm? d)
    {
        LockText = "设备参数只能在" + (d?.Alias ?? "设备") + "触屏上修改 · 这里只读";
    }

    // ———————————————————— 参数与报警 ————————————————————

    public SetRoGroups LimGroups { get; } = new();

    private void RefreshLim(DeviceVm? d)
    {
        RefreshLock(d);
        var s = d == null ? null : SettingsOf(d);
        NoSettings = d == null ? "没有设备" : s == null ? d.Alias + "还没有连上过 · 连上后读取设备设置" : null;
        var groups = s == null || d == null ? new List<SetRoGroup>() : LimRows(s, d);
        Replace(LimGroups, groups);
    }

    /// <summary>参数与报警的各组（样稿 lim；「设备暂未使用」= 设置里有、HMI 目前没有逻辑用到的 4 个值）。</summary>
    public static List<SetRoGroup> LimRows(SettingsDto s, DeviceVm d)
    {
        var unused = new HashSet<string>(s.Notes?.Unused ?? Array.Empty<string>(), StringComparer.Ordinal);
        string U(string key) => unused.Contains(key) ? "（设备暂未使用）" : "";
        string N(double v, string f = "0.##") => v.ToString(f, Inv);
        var vessel = s.Volume?.VesselVolume ?? d.Model.Vessel.VolumeMl;
        var gear = s.Stir.StirGearRatio > 0 ? s.Stir.StirGearRatio : d.Source.Plant.StirGearRatio ?? 1;
        var leak = s.LeakTest;
        return new List<SetRoGroup>
        {
            new("可设置参数范围", new[]
            {
                new SetRoRow("温度 SP 上限", N(s.Limits.TempSpMaxC, "0.#"), "°C"),
                new SetRoRow("压力 SP 上限", SettingsTexts.PsiM(s.Limits.PressureSpMaxPsi), "", U("pressureSpMaxPsi")),
                new SetRoRow("搅拌转速上限", N(s.Limits.RpmMax, "0"), "rpm", "· 釜里的转速"),
                new SetRoRow("搅拌传动比", gear.ToString("0.000", Inv), "", "· 电机 ÷ 釜"),
                new SetRoRow("溶液体积上限", N(s.Limits.VolumeMaxMl, "0.#"), "mL", "· 釜容积 " + N(vessel, "0.#") + " mL"),
            }),
            new("报警阈值", new[]
            {
                new SetRoRow("超压报警", SettingsTexts.PsiM(s.Alarms.OverPressurePsi)),
                new SetRoRow("超压时停止加热", s.Alarms.StopHeatOnOverPressure ? "开启" : "关闭"),
                new SetRoRow("超温报警", N(s.Alarms.OverTempC, "0.#"), "°C", "· 温度 SP 上限必须低于它"),
                new SetRoRow("升温超时报警", N(s.Alarms.HeatTimeoutMin, "0"), "min", U("heatTimeoutMin")),
                new SetRoRow("压力偏离 SP 报警", SettingsTexts.PsiM(s.Alarms.PressDeviationPsi), "", U("pressDeviationPsi")),
                new SetRoRow("泄漏率阈值", N(leak.RateLimitPsiPerHr, "0.0##"), "psi/h", "· 泄漏测试、记录页保压类都按它判定"),
                new SetRoRow("泄漏测试提醒周期", N(leak.ReminderDays, "0"), "天", U("reminderDays")),
            }),
            new("泄漏测试", new[]
            {
                new SetRoRow("测试压力", SettingsTexts.PsiM(leak.TestPsi), "", "· 必须低于超压报警的 90%"),
                new SetRoRow("平衡时间", N(leak.SettleMin, "0.#"), "min"),
                new SetRoRow("测量时间", N(leak.MeasureMin, "0.#"), "min"),
                new SetRoRow("上一次测试", leak.LastTestUtc is { } lt ? DateTime.SpecifyKind(lt, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm", Inv) : "—"),
            }),
            new("定值加热防超调", new[]
            {
                new SetRoRow("预留温差 Δ", N(s.Approach.DeltaC, "0.#"), "°C"),
                new SetRoRow("快段速率", N(s.Approach.FastRateCPerMin, "0.#"), "°C/min"),
                new SetRoRow("末段范围", N(s.Approach.FinalZoneC, "0.#"), "°C"),
                new SetRoRow("末段速率", N(s.Approach.RateCPerMin, "0.#"), "°C/min"),
                new SetRoRow("判稳范围", "±" + N(s.Approach.SettleBandC, "0.0#"), "°C"),
                new SetRoRow("判稳时长", N(s.Approach.SettleSec / 60, "0.#"), "min"),
            }),
            new("搅拌电机 · 压力变送器", new[]
            {
                new SetRoRow("峰值电流", N(s.Stir.PeakCurrentA, "0.0#"), "A"),
                new SetRoRow("待机电流", N(s.Stir.StandbyPct, "0"), "%"),
                new SetRoRow("加减速时间", N(s.Stir.RampMsPer1000Rpm, "0"), "ms / 1000 rpm（电机）"),
                new SetRoRow("细分", N(s.Stir.Microstep, "0")),
                new SetRoRow("变送器满量程", Core.Format.PressureDisplay.Fixed(s.Pressure.FullScaleMPa, 2), Core.Format.PressureDisplay.Unit),
            }),
        };
    }

    // ———————————————————— 温控 / PID ————————————————————

    /// <summary>
    /// 温控 / PID（自整定，样稿 pidBody）：档位卡片、这一档的 PID 表（手改即写入）、自整定表单 / 整定监视 / 结果。见 <see cref="PidSectionVm"/>。
    /// </summary>
    public PidSectionVm Pid { get; }

    // ———————————————————— 通讯与模块 ————————————————————

    public ObservableCollection<SetRoRow> CommRows { get; } = new();
    public ObservableCollection<SetModuleRow> Modules { get; } = new();

    private void RefreshComm(DeviceVm? d)
    {
        RefreshLock(d);
        NoSettings = d == null ? "没有设备" : null;
        if (d == null) { CommRows.Clear(); Modules.Clear(); return; }
        var comm = d.Source.Comm;
        var host = d.Source is DemoDeviceSource demo ? demo.Device.Plant?.HostName : null;
        double tickMs = comm?.Tick?.IntervalMs is double ms && ms > 0 ? ms : d.Source.IsDemo ? 1200 : 0;
        var rows = new List<SetRoRow>
        {
            new("设备地址", d.Host + ":" + d.Port.ToString(Inv)),
            new("主机名", host ?? (d.Sn.Length > 0 ? "—" : "—"), "", host == null ? "· 接口不提供主机名" : ""),
            new("序列号", d.Sn.Length > 0 ? d.Sn : "—"),
            new("HMI / 接口", d.Source.HmiVersion + " · 接口 " + d.Source.ApiVersion),
            new("轮询周期", tickMs > 0 ? (tickMs / 1000).ToString("0.0##", Inv) : "—", tickMs > 0 ? "s" : ""),
            new("离线判定", "连续 3 次", "", "无应答"),
        };
        Replace(CommRows, rows);
        var mods = new List<SetModuleRow>();
        foreach (var m in d.Source.Modules)
        {
            var snap = m.Key switch
            {
                "temp" => comm?.Temp, "press" => comm?.Pressure, "stir" => comm?.Stir, "valve" => comm?.Io, _ => null,
            };
            mods.Add(new SetModuleRow(m.Name, m.Count > 1 ? "×" + m.Count.ToString(Inv) : "1", m.Online, m.Online ? "在线" : "离线",
                snap != null ? snap.Polls.ToString("N0", Inv) : "—", snap != null ? snap.Fails.ToString("N0", Inv) : "—",
                m.LastOk is { } at ? at.ToString("HH:mm:ss", Inv) : "—"));
        }
        Replace(Modules, mods);
    }

    // ———————————————————— 数据存储 ————————————————————

    public ObservableCollection<SetRoRow> StoreRows { get; } = new();

    private void RefreshStore(DeviceVm? d)
    {
        RefreshLock(d);
        NoSettings = d == null ? "没有设备" : null;
        if (d == null) { StoreRows.Clear(); return; }
        var comm = d.Source.Comm;
        double tickMs = comm?.Tick?.IntervalMs is double ms && ms > 0 ? ms : d.Source.IsDemo ? 1200 : 0;
        var rows = new List<SetRoRow>
        {
            new("设备记录", tickMs > 0 ? (tickMs / 1000).ToString("0.0##", Inv) : "—", tickMs > 0 ? "s / 行" : "", "· 每个采样一行，按日期分文件（telemetry-日期.csv）"),
            new("会话文件", "每个会话一份", "", "· 在「记录」页下载、对比"),
            new("日志丢行", comm?.Logger is { } lg ? lg.DroppedLines.ToString("N0", Inv) : d.Source.IsDemo ? "0" : "—", "", "· 写盘跟不上时丢掉的行数（应为 0）"),
            new("本机数据目录", DataDirs.Root, "", "· 下载的会话数据、报告、导出在这里"),
        };
        Replace(StoreRows, rows);
    }

    // ———————————————————— 吸气量计算 ————————————————————

    public ObservableCollection<SetUptRowVm> UptRows { get; } = new();
    [ObservableProperty] private string _uptFormula = "";
    [ObservableProperty] private string? _noVolume;

    private void RefreshUpt(DeviceVm? d)
    {
        NoSettings = d == null ? "没有设备" : null;
        var v = d?.Source.Plant.Volume;
        NoVolume = d == null ? null : v == null ? (d.IsOffline && !d.EverOnline ? d.Alias + "还没有连上过 · 连上后读取容积设置" : d.Alias + "的 HMI 版本不提供容积设置（接口 v0.6 起有）") : null;
        UptFormula = "n = ΔP · V气 / (R · T) · V气 = 有效容积（标定）− 溶液；没标定时 = 釜 " + (v != null ? v.VesselMl.ToString("0.#", Inv) : "—")
            + " mL − 溶液 + 管路死体积 · 稳压补气时累加每次补气前后的压降";
        if (d == null || v == null) { UptRows.Clear(); return; }
        var rows = new List<SetUptRowVm>();
        foreach (var r in d.Reactors)
        {
            if (!r.Installed) continue;
            bool off = r.Own == StateKey.Idle;
            var u = SettingsTexts.Upt(v, r.N, r.SolutionMl, off);
            string cal; BadgeTone tone; bool tag;
            if (off) { cal = "停用"; tone = BadgeTone.None; tag = false; }
            else if (u.Calibrated) { cal = (u.CalibratedAt is { } at ? at.ToString("yyyy-MM-dd", Inv) + " " : "") + "已标定"; tone = BadgeTone.None; tag = false; }
            else { cal = "未标定 · 用默认管路体积"; tone = BadgeTone.Warn; tag = true; }
            var tip = off ? "停用的通道不计量" : u.GasMl is double g
                ? "RV" + r.N.ToString(Inv) + " 气相 " + g.ToString("0.0", Inv) + " mL = " + (u.Calibrated ? "有效容积 " + SettingsTexts.Ml(u.EffectiveMl) + " − 溶液" : "釜 " + u.VesselMl.ToString("0.#", Inv) + " − 溶液 + 管路 " + u.LineMl.ToString("0.0", Inv))
                : "RV" + r.N.ToString(Inv) + " 没有溶液体积 · 在程序页 / 控制面板填了才能算";
            rows.Add(new SetUptRowVm(r.Rv, u.VesselMl.ToString("0.#", Inv), u.LineMl.ToString("0.0", Inv), SettingsTexts.Ml(u.EffectiveMl), SettingsTexts.Ml(u.SolutionMl),
                SettingsTexts.Ml(u.GasMl), SettingsTexts.Res(u.ResMmol), u.GasMl != null, cal, tone, tag, off, tip));
        }
        Replace(UptRows, rows);
    }

    // ———————————————————— 关于 ————————————————————

    public ObservableCollection<SetRoRow> AboutRows { get; } = new();

    /// <summary>帮助（深蓝条「帮助」也到这里）：几条最常问的。</summary>
    public IReadOnlyList<SetHelpRow> Help { get; } = new[]
    {
        new SetHelpRow("控制权", "改设定、下发程序要先「申请控制」，设备触屏上点允许；停止、急停、确认报警不需要控制权。"),
        new SetHelpRow("停止", "停止一律先确认：通道「停止」、全部停止、急停都会先弹出对话框，写明对气路做什么。"),
        new SetHelpRow("报警", "报警弹窗可以确认、定位、停止该通道；未确认的报警每 30 s 响一次（「常规 › 报警声音」）。"),
        new SetHelpRow("断线", "设备在断线期间照常运行和记录；重连后自动补拉这段数据（「无人值守」）。"),
        new SetHelpRow("设备参数", "报警阈值、容积标定、PC 断开时的动作只能在设备触屏上改，这里只读。"),
    };

    private void RefreshAbout(DeviceVm? d)
    {
        var rows = new List<SetRoRow> { new("本软件", ShellVm.AboutText) };
        foreach (var x in _shell.Fleet.Devices)
            rows.Add(new SetRoRow(x.Alias, x.ModelLabel + " · HMI " + x.Source.HmiVersion + " · 接口 " + x.Source.ApiVersion));
        rows.Add(new SetRoRow("本机", _shell.Fleet.ClientName));
        rows.Add(new SetRoRow("服务热线", "021-67230701"));
        rows.Add(new SetRoRow("许可", "图标 Fluent UI System Icons（MIT）"));
        Replace(AboutRows, rows);
    }

    // ———————————————————— 演示数据 ————————————————————

    /// <summary>
    /// 演示设备的设置（样稿 lim 的数值）：温度 SP 上限 = 型号量程、压力 SP 上限 = 变送器满量程、转速 450（釜）、溶液 8 mL、超压 = 演示的压力报警、超温 220 °C、
    /// 泄漏测试 145 psi / 10 / 30 min、泄漏率阈值 = 演示整机数据（HMI 出厂 2 psi/h）、防超调 5 / 4 / 8 / 1 / ±0.5 / 3 min、搅拌 2.8 A / 50 % / 500 ms / 16、
    /// 温度档位同演示的档位 PID（样稿 PIDB：100 / 200 °C）；容积 = 演示整机数据。
    /// </summary>
    public static SettingsDto DemoSettings(DeviceVm d)
    {
        var m = d.Model;
        var plant = d.Source.Plant;
        double pmaxPsi = Math.Round(m.Limits.PressureMaxMPa * SettingsTexts.PsiPerMPa);
        double plimPsi = Math.Round((plant.OverPressureMPa ?? m.Limits.PressureMaxMPa * 0.85) * SettingsTexts.PsiPerMPa);
        var leak = plant.Leak ?? LeakTestInfo.HmiDefault;
        var vol = plant.Volume;
        return new SettingsDto(
            new LimitsDto(m.Limits.TempMaxC, pmaxPsi, plant.RpmMax ?? 450, m.Vessel.MaxFillMl > 0 ? m.Vessel.MaxFillMl : 8),
            new AlarmSettingsDto(plimPsi, 220, true, 60, 44),
            new LeakTestSettingsDto(leak.TestPsi, leak.SettleMin, leak.MeasureMin, leak.RatePsiH, 30, new DateTime(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc)),
            new ApproachSettingsDto(5, 1, 4, 8, 0.5, 180),
            new StirSettingsDto(2.8, 50, 500, 16, plant.StirGearRatio ?? 1),
            new PressureSettingsDto(m.Limits.PressureMaxMPa),
            Core.Demo.DemoFleet.DemoPidBands.Select(b => new TempBandSnap(b.Index, b.LoC, b.HiC)).ToArray(),
            new SettingsNotesDto(SettingsDto.UnusedKeys),
            1,
            vol == null ? null : new VolumeSettingsDto(vol.VesselMl, vol.LineMl, vol.ManifoldMl, vol.EffectiveMl.ToArray(), vol.CalibratedAt.Select(t => t?.ToUniversalTime()).ToArray()));
    }

    /// <summary>就地替换集合的内容（内容一样时不动，免得每秒重建视图）。</summary>
    private static void Replace<T>(ObservableCollection<T> col, IReadOnlyList<T> want)
    {
        if (col.Count == want.Count && col.SequenceEqual(want, new RecordEq<T>())) return;
        col.Clear();
        foreach (var x in want) col.Add(x);
    }

    /// <summary>记录类型按值比（SetRoGroup 里的数组按项比）。</summary>
    private sealed class RecordEq<T> : IEqualityComparer<T>
    {
        public bool Equals(T? a, T? b) => a is SetRoGroup ga && b is SetRoGroup gb ? ga.Title == gb.Title && ga.Rows.SequenceEqual(gb.Rows) : EqualityComparer<T>.Default.Equals(a, b);
        public int GetHashCode(T obj) => obj?.GetHashCode() ?? 0;
    }
}
