using System;
using System.Globalization;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Models;
using Xunit;
using static ParallelReactor.Control.Tests.LiveFrames;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// M5 复核 c2-ui-5：状态行的「预计 HH:MM 结束」= 现在 + 全程剩余，按绝对时刻相加（同样稿 new Date(+now + min * 60000)）——
/// 以前在本地时间上直接加，跨夏令时切换时差 1 小时。用有夏令时的时区（TimeZoneInfo 换算，不靠进程的时区）核对。
/// </summary>
public class M5ReviewEtaTests
{
    private static TimeZoneInfo Berlin()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time"); }
    }

    /// <summary>
    /// 复核的探针：柏林 2026-10-25（回拨）00:30Z = 02:30 CEST，+ 60 min = 01:30Z = 02:30 CET（在本地时间上加是 03:30）；
    /// 2026-03-29（前拨）00:30Z = 01:30 CET，+ 60 min = 01:30Z = 03:30 CEST（在本地时间上加是不存在的 02:30）。
    /// </summary>
    [Theory]
    [InlineData(10, 25, "02:30", "02:30", "03:30")]
    [InlineData(3, 29, "01:30", "03:30", "02:30")]
    public void Eta_IsEpochArithmetic_AcrossDst(int month, int day, string localNow, string right, string wallClockWrong)
    {
        var zone = Berlin();
        var nowUtc = new DateTime(2026, month, day, 0, 30, 0, DateTimeKind.Utc);
        Assert.Equal(localNow, Hm(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone)));
        var eta = ProgramPanelText.EtaAt(nowUtc, 60);
        Assert.Equal((nowUtc.AddMinutes(60), DateTimeKind.Utc), (eta, eta.Kind));
        Assert.Equal(right, Hm(TimeZoneInfo.ConvertTimeFromUtc(eta, zone)));
        Assert.Equal(wallClockWrong, Hm(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone).AddMinutes(60)));   // 以前的写法
        // 本地时间进来（页面的 now 是本地时间，任何进程时区）：同一个绝对时刻
        Assert.Equal(nowUtc.AddMinutes(60), ProgramPanelText.EtaAt(nowUtc.ToLocalTime(), 60));
    }

    /// <summary>状态行用的就是它：「预计 HH:MM 结束」= Fmt.Hm(EtaAt(现在, 全程剩余))。</summary>
    [Fact]
    public void StatusLine_UsesTheEpochEta()
    {
        var d0 = new DateTime(2026, 11, 8, 23, 0, 0, DateTimeKind.Utc);
        var pr8 = ModelProfiles.Embedded.Get("PR8")!;
        var l = new ProgramLedger("dev-1", Me);
        l.AddDownload(1, new ProgramDoc { Name = "加氢测试-01" }
            .Add(StepType.Heat, new { temp = 120, wait = 10 }).Add(StepType.Press, new { gas = "A", pres = 50.76 })
            .Add(StepType.React, new { time = 72 }).Add(StepType.End, new { cool = 50, mode = "vent" }), "b1", d0, TimeSpan.Zero, null, true);
        var f = Frame(d0.AddMinutes(60), Ch(1, temp: 120, psi: 50.8, atMode: 0, program: Prog(2, 4, "running", d0.AddSeconds(30), ByMe, "03/04 反应 · 反应中 · 剩 30 分")));
        l.ObserveFrame(f, "b1");
        var rec = l.ProgramOf(1, f, "b1", pr8)!;
        var now = d0.AddMinutes(60).ToLocalTime();
        var pr = Progress.Of(rec, new ProgressContext(now, Model: pr8, ChannelState: StateKey.React))!;
        var line = ProgramPanelText.Status(rec, pr, true, "", now)!;
        Assert.Equal("第 3/4 步 · 预计 " + Fmt.Hm(ProgramPanelText.EtaAt(now, pr.TotalRemainMin)) + " 结束", line.Full);
    }

    private static string Hm(DateTime t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);
}
