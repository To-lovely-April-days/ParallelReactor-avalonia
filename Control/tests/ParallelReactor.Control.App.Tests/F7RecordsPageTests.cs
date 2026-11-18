using System;
using System.IO;
using System.Linq;
using Avalonia;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Records;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages.Records;
using ParallelReactor.Control.Shell;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 融合版 F7 记录页（RecordsPageVm / RecordsStore）：会话列表（分组、按设备、搜索、近 7 天）、进行中的会话用历史缓存算对比（与样稿 page-rec 的数字一致）、
/// 历史会话 / 保压会话、选中通道、地址参数、工具条原因、「再做一次」的交接、标记与事件、数据文件、CSV 缓存的读取。
/// 数据 = 融合版演示数据（fusion=1，2026-11-15 14:30:07）。
/// </summary>
public class F7RecordsPageTests
{
    private static ShellVm Shell(string query = "dev=1", int w = 1920, int h = 1080)
    {
        var p = DemoParams.Parse("fusion=1&freeze=1");
        var s = new ShellVm(FleetVm.CreateDemo(p));
        s.Toasts.Frozen = true;
        s.Layout.Update(new Size(w, h));
        s.ApplyDemoParams(DemoParams.Parse("page=rec&apop=0&" + query + "&" + p));
        return s;
    }

    private static RecordsPageVm Rec(ShellVm s) => Assert.IsType<RecordsPageVm>(s.Page);

    [Fact]
    public void List_GroupsByDay_FilteredByDevice_DefaultSelectsNewest()
    {
        var s = Shell();
        var r = Rec(s);
        Assert.Equal("1号机 · 4 条", r.ListSub);
        Assert.Equal(new[] { "今天", "昨天", "更早" }, r.Items.Where(i => i.IsGroup).Select(i => i.Group).ToArray());
        var first = r.Items.First(i => !i.IsGroup);
        Assert.Equal(("平行合成 · 批次 7", "进行中", true, "1号机 · 平行合成程序 A · 7 路", "11-15 13:05 · 1 h 25 min", " · PT-20261115-1305", true),
            (first.Name, first.Tag, first.TagDot, first.Line2, first.Line3, first.IdText, first.IsOn));
        Assert.Equal("PT-20261115-1305", r.SelId);

        s.ApplyDemoParams(DemoParams.Parse("page=rec&apop=0&dev=0&fusion=1&freeze=1"));
        Assert.Equal("全部设备 · 7 条", r.ListSub);
        Assert.Equal(7, r.Items.Count(i => !i.IsGroup));
        Assert.Contains(r.Items, i => i.Line2 == "3号机 · 泄漏测试 · 16 路");
        // 搜索（名字 / 会话号 / 程序）
        r.Query = "筛选";
        Assert.Equal(new[] { "PT-20261113-1402", "PT-20261112-0930" }, r.Items.Where(i => !i.IsGroup).Select(i => i.Id).ToArray());
        r.Query = "没有这个";
        Assert.True(r.ListEmpty);
        Assert.Equal("没有找到「没有这个」", r.ListStatus);
        Assert.False(r.HasSel);
        r.Query = "";
        r.Range = "7d";
        Assert.Contains(r.Items, i => i.Id == "PT-20261110-1600");   // 11-10 是 5 天前：在近 7 天里
        r.Range = "custom";   // 还没做：提示后回到「全部」
        Assert.Equal("all", r.Range);
    }

    [Fact]
    public void LiveSession_FromHistory_MatchesTheMockNumbers()
    {
        var r = Rec(Shell());
        var c = r.Compare!;
        Assert.True(c.Live);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 8 }, c.Rows.Select(x => x.N).ToArray());
        Assert.Equal(new[] { 5 }, c.Skipped);
        Assert.Equal(("平均吸气", "14.4", "按 6 路 · 当前值"), (r.Kpis[0].Key, r.Kpis[0].Value, r.Kpis[0].Sub));
        Assert.Equal("6.7", r.Kpis[1].Value);
        Assert.Equal(("46.3", RecKpiTone.Warn, "大于 10 %，各路差异明显"), (r.Kpis[2].Value, r.Kpis[2].Tone, r.Kpis[2].Sub));
        Assert.Equal(("21.3", "RV6"), (r.Kpis[3].Value, r.Kpis[3].Sub));
        Assert.Equal(("2.1", "RV2 · 仍在反应"), (r.Kpis[4].Value, r.Kpis[4].Sub));
        Assert.Equal(("2", RecKpiTone.Err), (r.Kpis[5].Value, r.Kpis[5].Tone));
        RecRowVm R(int n) => r.Rows.Single(x => x.N == n);
        Assert.Equal(("18.4", "+27%", "0.64", "未到", false, "反应中"), (R(1).GText, R(1).RelText, R(1).RateText, R(1).T90None, R(1).HasT90, R(1).Result));
        Assert.Equal(("2.1", "−85%", "升温中"), (R(2).GText, R(2).RelText, R(2).Result));
        Assert.Equal(("已暂停", true), (R(4).Result, R(4).IsErr));
        Assert.Equal((false, "已就绪 · 不计入统计"), (R(5).Used, R(5).OffText));
        Assert.True(R(8).HasT90);                     // 降温段：终值已定
        Assert.InRange(int.Parse(R(8).T90Text), 41, 42);
        Assert.Equal("降温中", R(8).Result);
        Assert.Equal(("14.4", "平均 · SD 6.7 · RSD 46.3 % · 6 路", "（RV5 未运行，不计入）"), (r.FootMean, r.FootLine, r.FootSkip));
        // 曲线：从会话开始，到现在 85 min；6 条线 + 平均线
        Assert.Equal(("吸气量", 0.0, 6), (r.Chart!.Title, r.Chart.M0, r.Chart.Lines.Count));
        Assert.InRange(r.Chart.Span, 85, 85.2);
        Assert.NotEmpty(r.Chart.Mean);
        Assert.Equal(new DateTime(2026, 11, 15, 13, 5, 0), r.Chart.Start);
        Assert.Equal("1号机 · 平行合成程序 A · 张工 · 11-15 13:05 起 · 1 h 25 min", r.SesMeta);
        Assert.Null(r.CurvesReason);
    }

    [Fact]
    public void Events_MarksAndDeviceEventsInWindow_Sorted()
    {
        var r = Rec(Shell());
        Assert.Equal("9", r.EventCount);
        Assert.Equal(("13:05", "会话", "会话 PT-20261115-1305 开始", RecEventKind.Info), (r.Events[0].Time, r.Events[0].Src, r.Events[0].Text, r.Events[0].Kind));
        Assert.Contains(r.Events, e => e.Src == "RV3" && e.Text == "标记 · RV3 加料" && e.Kind == RecEventKind.Mark);
        Assert.Contains(r.Events, e => e.Src == "全部" && e.Text == "标记 · 换气源 · 气体 A");
        Assert.Contains(r.Events, e => e.Src == "RV4" && e.Text == "标记 · RV4 超压" && e.Kind == RecEventKind.MarkErr);   // 报警时刻的标记
        Assert.Contains(r.Events, e => e.Src == "RV2" && e.Kind == RecEventKind.Warn);
        Assert.Equal(r.Events.Select(e => e.Time).OrderBy(t => t, StringComparer.Ordinal), r.Events.Select(e => e.Time));
        // 数据文件（演示：下载带原因）
        Assert.Equal("3", r.FileCount);
        Assert.Equal(("telemetry-20261115.csv", "11.8 MB（写入中）", "演示数据：没有文件可下载"), (r.Files[0].File, r.Files[0].Size, r.Files[0].Reason));
    }

    [Fact]
    public void HistoricalSession_ByParam_ChannelSelectToggles_ModesChangeChart()
    {
        var s = Shell("dev=1&rc=PT-20261114-0915&rcch=6&rcm=T");
        var r = Rec(s);
        Assert.Equal(("PT-20261114-0915", 6), (r.SelId, r.Ch));
        Assert.Equal(("已结束", BadgeToneNone()), (r.SesTag, r.SesTone));
        Assert.Equal("历史会话的完整曲线：下载数据后查看（第二阶段）", r.CurvesReason);
        Assert.Equal(("17.4", "2.7", "15.2"), (r.Kpis[0].Value, r.Kpis[1].Value, r.Kpis[2].Value));
        Assert.Equal(("温度", 6), (r.Chart!.Title, r.Chart.Selected));
        Assert.Empty(r.Chart.Mean);                     // 平均线只在吸气量模式
        Assert.True(r.Rows.Single(x => x.N == 6).IsOn);
        r.Rows.Single(x => x.N == 6).PickCommand.Execute(null);
        Assert.Null(r.Ch);
        Assert.False(r.Rows.Single(x => x.N == 6).IsOn);
        r.Rows.Single(x => x.N == 3).PickCommand.Execute(null);
        Assert.Equal(3, r.Chart!.Selected);
        r.ModeKey = "G";
        Assert.Equal("吸气量", r.Chart!.Title);
        Assert.NotEmpty(r.Chart.Mean);
        Assert.Equal("2", r.EventCount);
        // 换一条会话：选中的通道清掉
        r.Items.First(i => i.Id == "PT-20261112-0930").PickCommand.Execute(null);
        Assert.Null(r.Ch);
        Assert.Equal(("完成 · 有提醒", "RV3 升温超时（提醒）", true), (r.SesTag, r.Note, r.HasNote));
        Assert.True(r.Rows.Single(x => x.N == 3).IsWarn);
        Assert.Equal("有提醒", r.Rows.Single(x => x.N == 3).Result);
    }

    private static Controls.Pr.BadgeTone BadgeToneNone() => Controls.Pr.BadgeTone.None;

    [Fact]
    public void HoldSession_ComparesDrop_FiveKpis_ChartFromMinuteEight()
    {
        var r = Rec(Shell("dev=0&rc=LK-20261115-0912"));
        Assert.True(r.IsHold);
        Assert.Equal(("各路压降对比", "再做一次泄漏测试"), (r.CmpTitle, r.RedoLabel));
        Assert.Equal(5, r.Kpis.Count);
        Assert.Equal(("判定", "16/16", "路通过", RecKpiTone.Ok), (r.Kpis[0].Key, r.Kpis[0].Value, r.Kpis[0].Unit, r.Kpis[0].Tone));
        Assert.Equal(("0.35", "惰性气体 · 约 50 psi"), (r.Kpis[1].Value, r.Kpis[1].Sub));
        Assert.Equal(("≤ 2.0", "第 10–40 min 压降"), (r.Kpis[2].Value, r.Kpis[2].Sub));
        Assert.Equal(("42 min"), r.Kpis[4].Value);
        Assert.Equal(16, r.Rows.Count);
        Assert.All(r.Rows, x => Assert.True(x.ShowHold && x.Ok && x.Result == "通过"));
        Assert.EndsWith(" kPa/h", r.Rows[0].KpaText);
        Assert.Equal(("压力", 8.0, true), (r.Chart!.Title, r.Chart.M0, r.Chart.Hold));
        Assert.True(r.Chart.Lines.Single(l => l.N == 9).Dashed);
        Assert.False(r.ShowMean);
        Assert.Equal("4", r.FileCount);
        Assert.True(r.EventsEmpty);
    }

    [Fact]
    public void Tools_Reasons()
    {
        var s = Shell();
        var r = Rec(s);
        RibbonTool T(string label) => r.Tools.SelectMany(g => g.Tools).Single(t => t.Label == label);
        Assert.Equal("1号机已有进行中的会话", T("新建会话").Reason);
        Assert.Null(T("结束会话").Reason);           // 本机创建、进行中
        Assert.Null(T("打标记").Reason);
        Assert.Null(T("下载数据").Reason);
        s.ApplyDemoParams(DemoParams.Parse("page=rec&apop=0&dev=0&fusion=1&freeze=1"));
        Assert.Equal("先在左侧设备列表选一台设备", T("新建会话").Reason);
        r.Items.First(i => i.Id == "PT-20261115-1250").PickCommand.Execute(null);
        Assert.Equal("只有创建者（触屏）或设备触屏能结束", T("结束会话").Reason);
        r.Items.First(i => i.Id == "PT-20261114-0915").PickCommand.Execute(null);
        Assert.Equal("所选会话已结束", T("结束会话").Reason);
        Assert.Equal("所选会话已结束", T("打标记").Reason);
    }

    [Fact]
    public void Redo_Program_PreselectsIdleChannels_AndNavigates()
    {
        var s = Shell("dev=1&rc=PT-20261114-0915");
        var r = Rec(s);
        Assert.Null(r.RedoReason);
        var req = r.BuildRedo()!;
        Assert.Equal((RedoKind.Program, 1, "平行合成程序 A", "A", AppPage.Program), (req.Kind, req.DeviceK, req.ProgramName, req.ProgramId, req.Page));
        // 1号机此刻：RV1/2/3/6/8 在跑、RV4 报警、RV7 停用 → 只有 RV5 空闲
        Assert.Equal(new[] { 5 }, req.Channels);
        Assert.Equal(new[] { 1, 2, 3, 4, 6, 7, 8 }, req.Busy);
        RedoRequest? got = null;
        r.RedoRequested += (_, e) => got = e;
        r.RedoCommand.Execute(null);
        Assert.Same(r.LastRedo, got);
        Assert.Equal((AppPage.Program, 1), (s.Nav.Page, s.Nav.DeviceK));
        Assert.Contains(s.Toasts.Items, t => t.Text.Contains("平行合成程序 A") && t.Text.Contains("RV5"));
    }

    [Fact]
    public void Redo_Leak_GoesToPerf_ProgramNotInLibrary_HasReason()
    {
        var s = Shell("dev=0&rc=LK-20261115-0912");
        var r = Rec(s);
        var req = r.BuildRedo()!;
        Assert.Equal((RedoKind.LeakTest, 3, (string?)null, AppPage.Perf), (req.Kind, req.DeviceK, req.ProgramName, req.Page));
        r.RedoCommand.Execute(null);
        Assert.Equal((AppPage.Perf, 3), (s.Nav.Page, s.Nav.DeviceK));
        // 2号机的进行中会话：程序「高压加氢-150」在 2号机的程序库里
        var s2 = Shell("dev=0&rc=PT-20261115-1250");
        Assert.Null(Rec(s2).RedoReason);
    }

    [Fact]
    public void NarrowShort_LayoutSizes_HideIdAndSkip()
    {
        var r = Rec(Shell("dev=1", 1366, 768));
        Assert.Equal((true, true), (r.IsNarrow, r.IsShort));
        Assert.Equal((300.0, 168.0), (r.ListW.Value, r.BottomH.Value));
        Assert.Equal((62.0, 8.0), (r.Cols.C0.Value, r.Cols.Pad.Left));
        Assert.Equal("", r.Items.First(i => !i.IsGroup).IdText);
        Assert.Equal("", r.FootSkip);
    }

    [Fact]
    public void Store_ParsesCachedCsv_ForEndedSession_AndCachePathUnderDataDir()
    {
        var s = Shell();
        var r = Rec(s);
        var dev = s.Fleet.Device(1)!;
        var sess = DemoRecords.Sessions(_ => true).Single(x => x.Id == "PT-20261114-0915");
        var root = Path.Combine(Path.GetTempPath(), "pr-f7-" + Guid.NewGuid().ToString("N"));
        try
        {
            r.Store.CacheRoot = root;
            var path = r.Store.CachePath(dev, sess);
            Assert.StartsWith(root, path);
            Assert.EndsWith("PT-20261114-0915.csv", path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, RecExport.SeriesCsv(sess, DemoRecords.Series(sess)!), new System.Text.UTF8Encoding(true));
            var set = RecordsStore.ParseFile(sess, path);
            Assert.Equal(8, set.Channels.Count);
            Assert.Equal(245, set.Span);
            var c = RecCompute.Compare(sess, set);
            Assert.Equal("17.4", RecTexts.F1(c.Stats.Mean));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

#if DEBUG   // 截图计划（Shot/*）只在 Debug 里编译
    [Fact]
    public void ShotPlan_HasF7Entries()
    {
        foreach (var n in new[] { "page-rec", "rec-all", "rec-hist", "rec-ch", "rec-ovt", "rec-ovp", "rec-files", "rec-hold", "rec-tight", "rec-int", "rec-warn" })
            Assert.NotNull(Shot.ShotPlan.Find(n));
    }
#endif
    [Fact]
    public void Store_FromDto_MapsManifest_StateCreatorLeakInterrupted()
    {
        var s = Shell();
        var r = Rec(s);
        var dev = s.Fleet.Device(1)!;
        var t0 = new DateTime(2026, 11, 14, 1, 15, 0, DateTimeKind.Utc);
        ParallelReactor.Services.SessionManifest M(string state, string source, string? client, int leaks = 0, DateTime? end = null) =>
            new("PT-X-20261114-091500", "PT-X", "批次 6", t0, end, state, source, client, false, 1, null, "sessions/PT-X.csv",
                new[] { new ParallelReactor.Services.SessionMarker(1, t0.AddMinutes(5), 2, "RV1 加料", source, client) },
                Array.Empty<ParallelReactor.Services.SessionProgramRun>(),
                Enumerable.Range(0, leaks).Select(i => new ParallelReactor.Services.SessionLeakTest("leak-" + i, t0)).ToArray());
        var item = new ParallelReactor.Api.SessionInfoDto("PT-X", t0, "PT-X.csv", 100, t0, t0.AddHours(1), "PT-X-20261114-091500");
        ParallelReactor.Api.SessionDetailDto D(ParallelReactor.Services.SessionManifest m) => new(false, m.Id, m.TestId, m.File, m, new[] { "sessions/PT-X-20261114-091500.csv" });

        var ended = r.Store.FromDto(dev, item, D(M("ended", "remote", "PC-测试工位1", end: t0.AddMinutes(245))), active: false);
        Assert.Equal(("PT-X-20261114-091500", "批次 6", "PC-测试工位1", RecState.Ended, RecHold.None, 1), (ended.Id, ended.Name, ended.By, ended.State, ended.Hold, ended.Marks));
        Assert.Equal(t0.ToLocalTime(), ended.Start);
        Assert.Equal(TimeSpan.FromMinutes(245), ended.End!.Value - ended.Start);
        Assert.Equal(8, ended.Channels.Count);
        var leak = r.Store.FromDto(dev, item, D(M("ended", "local", null, leaks: 1, end: t0.AddMinutes(42))), active: false);
        Assert.Equal(("触屏", RecHold.Leak), (leak.By, leak.Hold));
        // manifest 还写着 active、设备上已经不是当前会话 → 中断，数据到列表里的 To
        var lost = r.Store.FromDto(dev, item, D(M("active", "remote", "PC-测试工位1")), active: false);
        Assert.Equal(RecState.Interrupted, lost.State);
        Assert.Equal(t0.AddHours(1).ToLocalTime(), lost.End);
        Assert.StartsWith("会话中断 · 数据保留到", lost.Note);
        // 没有 manifest 的旧会话
        var bare = r.Store.FromDto(dev, item, null, active: true);
        Assert.Equal(("PT-X", RecState.Active, "—"), (bare.Name, bare.State, bare.By));
    }
}

