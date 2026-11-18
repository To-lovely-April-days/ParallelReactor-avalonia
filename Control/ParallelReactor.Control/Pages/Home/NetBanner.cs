using System;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Pages.Home;

/// <summary>断线横条的状态（样稿 netBar）：不显示 / 连接中断（琥珀）/ 已重新连接（绿，可关）。</summary>
public enum NetBannerKind { None, Lost, Back }

/// <summary>断线横条的文字（样稿 netBar，融合版 F3）：纯函数，单元测试直接调。</summary>
public static class NetBannerText
{
    /// <summary>时长：「26 s」/「2 min 26 s」/「1 h 05 min」。</summary>
    public static string Gap(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        int s = (int)Math.Round(t.TotalSeconds);
        if (s < 60) return s.ToString(CultureInfo.InvariantCulture) + " s";
        if (s < 3600) return (s / 60).ToString(CultureInfo.InvariantCulture) + " min " + (s % 60).ToString(CultureInfo.InvariantCulture) + " s";
        return (s / 3600).ToString(CultureInfo.InvariantCulture) + " h " + (s % 3600 / 60).ToString("00", CultureInfo.InvariantCulture) + " min";
    }

    public static string LostTitle(string alias, TimeSpan gap) => "与" + alias + "的连接中断 " + Gap(gap);

    /// <summary>
    /// 「设备照常运行（断开时：保持运行）· 下面的数据停在 14:28:41 · 自动重连，已试 3 次 · 恢复后自动补拉这段数据，会话记录不断」。
    /// <paramref name="tries"/> = 已经失败的重连次数；<paramref name="backfill"/> = 恢复后会补拉（否则写「恢复后从最新数据接着显示」）。
    /// </summary>
    public static string LostBody(string? onLost, DateTime? dataAt, int tries, bool backfill)
    {
        var head = onLost == "estop" ? "设备按设置急停" : "设备照常运行";
        var parts = new System.Collections.Generic.List<string>();
        if (dataAt is { } at) parts.Add("下面的数据停在 " + at.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        parts.Add("自动重连" + (tries > 0 ? "，已试 " + tries.ToString(CultureInfo.InvariantCulture) + " 次" : "中"));
        parts.Add(backfill ? "恢复后自动补拉这段数据，会话记录不断" : "恢复后从最新数据接着显示");
        // 全角括号后面直接接「· 」（样稿「（断开时：保持运行）· 下面的数据…」）
        return head + "（断开时：" + HomeTexts.OnLostText(onLost) + "）· " + string.Join(" · ", parts);
    }

    public static string BackTitle(string alias) => "已重新连接" + alias;

    /// <summary>
    /// 「断开 2 min 26 s（14:28:41 – 14:31:07）· 已补拉这段的 146 条数据 · 会话 PT-… 记录完整，期间没有新报警」。
    /// 补拉（融合版 F8）：进行中写「正在补拉这段数据…」，失败写原因，关着 / 没补写「这段数据没有补拉（曲线在这里断开）」。
    /// </summary>
    public static string BackBody(DateTime lostAt, DateTime backAt, int? rows, string? session, int newAlarms, bool pending = false, string? error = null)
    {
        var s = "断开 " + Gap(backAt - lostAt) + "（" + lostAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " – " + backAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "）· ";
        s += pending ? "正在补拉这段数据… · "
            : rows is int r
            ? "已补拉这段的 " + r.ToString(CultureInfo.InvariantCulture) + " 条数据 · " + (session != null ? "会话 " + session + " 记录完整，" : "")
            : error is { Length: > 0 } ? "补拉失败（" + error + "）· 曲线在这里断开 · "
            : "这段数据没有补拉（曲线在这里断开）· ";
        return s + (newAlarms == 0 ? "期间没有新报警" : "期间有 " + newAlarms.ToString(CultureInfo.InvariantCulture) + " 条新报警");
    }

    /// <summary>横条现在该是什么：连上过、现在断着 = 连接中断；断开后又连上、还没关掉 = 已重新连接。</summary>
    public static NetBannerKind KindOf(bool offline, bool everOnline, bool hasBack) =>
        offline && everOnline ? NetBannerKind.Lost : !offline && hasBack ? NetBannerKind.Back : NetBannerKind.None;
}

/// <summary>
/// 通道面板顶部的断线横条（融合版 F3，样稿 netBar）：连接中断（琥珀：断了多久、断开时设备怎么做、数据停在哪、重连次数、「立即重连」「断线设置」）/
/// 已重新连接（绿：断开多久、补拉了多少、会话、期间的报警；×关掉）。状态来自 <see cref="DeviceVm"/> 的连接与 <see cref="DeviceVm.NetBack"/>。
/// </summary>
public sealed partial class NetBannerVm : ObservableObject
{
    private readonly ShellVm _shell;
    private int _k;

    public NetBannerVm(ShellVm shell)
    {
        _shell = shell;
        RetryCommand = new RelayCommand(() => { if (_k > 0) shell.DeviceAct(_k, "reconnect"); });
        SettingsCommand = new RelayCommand(() => shell.OpenSettings("night"));
        DismissCommand = new RelayCommand(() => { if (shell.Fleet.Device(_k) is { } d) { d.DismissNetBack(); Apply(d); } });
    }

    public ICommand RetryCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand DismissCommand { get; }

    [ObservableProperty] private NetBannerKind _kind;
    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private bool _isLost;
    [ObservableProperty] private bool _isBack;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _body = "";

    internal void Apply(DeviceVm d)
    {
        _k = d.K;
        var kind = NetBannerText.KindOf(d.IsOffline, d.EverOnline, d.NetBack != null);
        Kind = kind;
        IsVisible = kind != NetBannerKind.None;
        IsLost = kind == NetBannerKind.Lost;
        IsBack = kind == NetBannerKind.Back;
        var now = _shell.Fleet.Now;
        var c = d.Source.Conn;
        if (kind == NetBannerKind.Lost)
        {
            var lostAt = c.LostAt ?? d.Source.DataAt ?? now;
            Title = NetBannerText.LostTitle(d.Alias, now - lostAt);
            Body = NetBannerText.LostBody(d.Source.Plant.OnRemoteLost, d.Source.DataAt, Math.Max(0, c.Attempts - 1), _shell.Prefs.Current.Backfill);
        }
        else if (kind == NetBannerKind.Back && d.NetBack is { } b)
        {
            Title = NetBannerText.BackTitle(d.Alias);
            int alarms = d.AlarmList.Count(a => a.At >= b.LostAt && a.At <= b.BackAt);
            Body = NetBannerText.BackBody(b.LostAt, b.BackAt, b.Rows, d.SessionId, alarms, b.Pending, b.Error);
        }
    }
}
