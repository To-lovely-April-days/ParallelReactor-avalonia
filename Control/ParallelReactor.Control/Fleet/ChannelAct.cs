using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ParallelReactor.Control.Core.Activity;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Services;

namespace ParallelReactor.Control.Fleet;

/// <summary>一路此刻被什么占着（运行表 / 卡片 / 棒图的状态字换掉，ACT-SPEC §3.2）。</summary>
public enum ChannelActKind { Leak, Calib, Tune, Queue }

/// <summary>
/// 一路的活动覆盖（ACT-SPEC §3.2「运行表、卡片、棒图」，样稿 act.js axDot / axLeft / axPg / axStW）：
/// 泄漏测试的几路写「泄漏测试」（品牌蓝点）、步骤栏「平衡 剩 05:20」+ 阶段分段条、预计结束 = 测试结束；标定的那一路「容积标定」（等人操作琥珀）；
/// 自整定「自整定中 · 150 °C」；被测试 / 标定挡住的程序「排队等气路」·「排队」。
/// </summary>
public sealed record ChannelAct(ChannelActKind Kind, string Word, string Step, string Sub, IReadOnlyList<RingSeg> Segs, string? End, bool Amber)
{
    /// <summary>卡片 / 棒图的一行：「平衡 · 剩 05:20」「加水 · 等待触屏」「150 °C · 整定中」。</summary>
    public string Line => Sub.Length > 0 ? Step + " · " + Sub : Step;

    /// <summary>
    /// 第 n 路的覆盖；不被占着为 null。<paramref name="queue"/> = 排队等气路的通道（plant.gasQueue）；<paramref name="now"/> = 预计结束的「现在」。
    /// </summary>
    public static ChannelAct? Of(int n, DeviceActivity? a, DeviceMarks m, IReadOnlyList<int>? queue, LeakTestInfo? leak, DateTime now, bool ownRunning)
    {
        double settle = leak?.SettleMin ?? 10, measure = leak?.MeasureMin ?? 30;
        if (a is { Live: true, Kind: ActKind.Leak } && a.Channels.Any(c => c.N == n))
        {
            var segs = ActivityText.Ring(a, settle, measure);
            return new ChannelAct(ChannelActKind.Leak, "泄漏测试", ActivityText.StageName(a),
                a.StageLeftSec is double l ? "剩 " + ActivityText.Mmss(l) : "", segs,
                ActivityText.EndAt(a, now) is { } e ? Fmt.Hm(e) : null, false);
        }
        if (a is { Live: true, Kind: ActKind.Calib } && a.Channel == n)
            return new ChannelAct(ChannelActKind.Calib, "容积标定", ActivityText.StageName(a), a.Waiting ? "等待触屏" : ActivityText.Pct(a).ToString(CultureInfo.InvariantCulture) + " %",
                ActivityText.Ring(a, settle, measure), null, a.Waiting);
        if (m.Autotune.FirstOrDefault(x => x.N == n) is { N: > 0 } t && !ownRunning)
            return new ChannelAct(ChannelActKind.Tune, "自整定中", t.SpC is double sp ? sp.ToString("0", CultureInfo.InvariantCulture) + " °C" : "整定中",
                t.SpC is null ? "" : "整定中", Array.Empty<RingSeg>(), null, false);
        if (m.GasByActivity && queue is { } q && q.Contains(n))
            return new ChannelAct(ChannelActKind.Queue, "排队等气路", "", "", Array.Empty<RingSeg>(), null, false);
        return null;
    }
}
