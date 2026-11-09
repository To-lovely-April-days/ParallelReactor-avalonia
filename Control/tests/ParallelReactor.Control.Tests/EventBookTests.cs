using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using ParallelReactor.Api;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Events;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Services;
using Xunit;
using static ParallelReactor.Control.Tests.PageVectorData;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// Core.Events.EventBook / EventTexts：
/// 真机 —— Samples/live-events_afterSeq_0_limit_20.json（GET /events 第一页，more = true）+ ws-event.jsonl（WS 增量）：级别（op. / control. / session. 算操作）、
/// 反应釜号（标题开头的 RVn）、去重、续取序号、纪元变化、容量；
/// 演示 —— 与样稿在 WebKit 里渲染的设备页「最近事件」逐行对照（MockVectors/PageMockVectors.cs 的 td-* / tt-*：三个筛选的计数、列表、时间、反应釜列、去掉 RVn 的文字、悬停全文）。
/// </summary>
public class EventBookTests
{
    private static EventsPageDto FirstPage() => ApiJson.Deserialize<EventsPageDto>(Samples.Read("live-events_afterSeq_0_limit_20.json"))!;

    private static EventMsg[] WsEvents() => Samples.Lines("ws-event.jsonl").Select(l => ApiJson.Deserialize<EventMsg>(l)!).ToArray();

    // ———————————————————— 真机 ————————————————————

    [Fact]
    public void Live_FirstPage_NormalisesAndContinues()
    {
        var page = FirstPage();
        Assert.Equal(55, page.EventSeqHead);
        Assert.Equal(0, EventBook.InitialAfterSeq(page.EventSeqHead));       // 头 55：从头取
        Assert.Equal(55, EventBook.InitialAfterSeq(255));                     // 最近 200 条
        var book = new EventBook("dev-1");
        Assert.True(book.Load(page));
        Assert.Equal(20, book.Items.Count);
        Assert.Equal(1, book.SeqEpoch);
        Assert.Equal(20, book.NextAfterSeq);                                  // 下一页 afterSeq
        Assert.True(book.More);
        // 最新在前
        Assert.Equal(Enumerable.Range(1, 20).Reverse().Select(i => "1:" + i), book.Items.Select(e => e.Key));
        var e2 = book.Items.Single(e => e.Seq == 2);
        Assert.Equal((EventLevel.Err, (int?)null, "模拟模式 · PR_SIM=1：全部模块为内存模拟，读数非真实设备，急停 / 超温切断不作用于任何硬件"), (e2.Level, e2.N, e2.Text));
        Assert.Equal("整机", e2.RvText);
        Assert.Equal(EventLevel.Warn, book.Items.Single(e => e.Seq == 4).Level);   // api.generic warn → 提醒
        Assert.Equal(EventLevel.Op, book.Items.Single(e => e.Seq == 6).Level);     // op.heat.stop（level warn）→ 操作
        Assert.Equal(EventLevel.Op, book.Items.Single(e => e.Seq == 9).Level);     // control.requested
        Assert.Equal(EventLevel.Op, book.Items.Single(e => e.Seq == 19).Level);    // session.begin
        Assert.Equal(EventLevel.Info, book.Items.Single(e => e.Seq == 1).Level);
        var e17 = book.Items.Single(e => e.Seq == 17);                            // 「RV2 程序下发」：标题开头的 RV2
        Assert.Equal(2, e17.N);
        Assert.Equal("RV2", e17.RvText);
        Assert.Equal("程序下发 · 共 2 步，开始执行 · 来源 远程（PC-测试工位1）", e17.ShortText);
        Assert.Equal("program.generic", e17.Kind);
        Assert.Equal(DateTime.Parse("2026-11-07T07:14:40.956889Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal).ToLocalTime(), e17.At);
        var c = book.Counts;
        Assert.Equal(20, c.All);
        Assert.Equal(book.Items.Count(e => e.Level is EventLevel.Warn or EventLevel.Err), c.Alarm);
        Assert.Equal(book.Items.Count(e => e.Level == EventLevel.Op), c.Op);
        Assert.Equal(new[] { 4, 2 }, book.Filter(EventFilter.Alarm).Select(e => (int)e.Seq).ToArray());
        Assert.False(book.Load(page));                                         // 同一页再来一次：去重，没变
    }

    [Fact]
    public void Live_WsEvents_AppendAndDedupe()
    {
        var book = new EventBook();
        book.Load(FirstPage());
        int v = book.Version;
        var ws = WsEvents();
        Assert.Equal(6, ws.Length);
        foreach (var m in ws) Assert.True(book.Add(m));
        Assert.Equal(v + ws.Length, book.Version);
        Assert.Equal(26, book.Items.Count);
        Assert.Equal("1:37", book.Items[0].Key);                               // 最新在前
        Assert.Equal(37, book.NextAfterSeq);
        var heat = book.Items.Single(e => e.Seq == 33);                         // 远程命令 heat.start · 409（level warn，kind op.）→ 操作
        Assert.Equal(EventLevel.Op, heat.Level);
        Assert.Equal("PC-测试工位1", heat.Client);
        Assert.False(book.Add(ws[0]));                                         // 重复推送：去重
        // REST 页里带着已经从 WS 收到的事件（断线重同步）：只并入没有的
        var record = new EventRecord(38, 1, ws[^1].Ts.AddSeconds(1), "", "info", "op.heat.start", 1, "remote", "接口", "PC-测试工位1", "x", "远程命令 heat.start · 202 accepted", "");
        var resync = new EventsPageDto(1, 38, 36, 2, false, new[] { new EventRecord(37, 1, ws[^1].Ts, "", ws[^1].Level, ws[^1].Kind, ws[^1].Channel, ws[^1].Source, ws[^1].Origin, ws[^1].Client, ws[^1].OpId, ws[^1].Title, ws[^1].Message), record });
        Assert.True(book.Load(resync));
        Assert.Equal(27, book.Items.Count);
        Assert.Equal(38, book.NextAfterSeq);
        Assert.False(book.More);
        Assert.Equal(1, book.Items[0].N);                                       // channel = 1
        Assert.Equal("远程命令 heat.start · 202 accepted", book.Items[0].Text);  // 说明为空：只有标题
    }

    [Fact]
    public void Live_EpochChange_RestartsSequence()
    {
        var book = new EventBook();
        book.Load(FirstPage());
        var t = DateTime.UtcNow;
        Assert.True(book.Add(new EventMsg("event", 3, 2, t, "", "info", "system.boot", null, "system", "系统", null, null, "程序启动", "")));
        Assert.Equal(2, book.SeqEpoch);
        Assert.Equal(3, book.NextAfterSeq);                                    // 新纪元从头计
        Assert.Contains(book.Items, e => e.Key == "1:3");                      // 旧纪元的同号事件还在（身份带纪元）
        Assert.Contains(book.Items, e => e.Key == "2:3");
        // 空页、服务端的头在前面（中间那些已不在环里）：跳到头
        Assert.False(book.Load(new EventsPageDto(2, 50, 3, 0, false, Array.Empty<EventRecord>())));
        Assert.Equal(50, book.NextAfterSeq);
        Assert.True(book.Clear());
        Assert.Empty(book.Items);
        Assert.Null(book.SeqEpoch);
        Assert.Equal(0, book.NextAfterSeq);
    }

    [Fact]
    public void Capacity_KeepsNewest()
    {
        var book = new EventBook(capacity: 5);
        var t0 = new DateTime(2026, 11, 7, 7, 0, 0, DateTimeKind.Utc);
        for (int i = 1; i <= 8; i++)
            book.Add(new EventRecord(i, 1, t0.AddSeconds(i), "", "info", "event.generic", null, "system", "系统", null, null, "e" + i, ""));
        Assert.Equal(new long[] { 8, 7, 6, 5, 4 }, book.Items.Select(e => e.Seq).ToArray());
        // 比簿里最旧的还旧：并入后立刻被挤掉，内容不变
        Assert.False(book.Add(new EventRecord(2, 1, t0.AddSeconds(2), "", "info", "event.generic", null, "system", "系统", null, null, "e2", "")));
        Assert.Equal(8, book.NextAfterSeq);
    }

    [Fact]
    public void ShortText_StripsLeadingRv_NotPartOfLongerNumber()
    {
        var at = DateTime.Now;
        Assert.Equal("进入第 2 步 充压", new EventEntry("e", at, EventLevel.Info, 4, "RV4 进入第 2 步 充压").ShortText);
        Assert.Equal("RV11 超压", new EventEntry("e", at, EventLevel.Err, 1, "RV11 超压").ShortText);   // 不截成「1 超压」
        Assert.Equal("超压", new EventEntry("e", at, EventLevel.Err, 11, "RV11超压").ShortText);
        Assert.Equal("已连接", new EventEntry("e", at, EventLevel.Info, null, "已连接").ShortText);
        Assert.Equal("alert", new EventEntry("e", at, EventLevel.Err, null, "x").IconName);
        Assert.Equal("user", new EventEntry("e", at, EventLevel.Op, null, "x").IconName);
        Assert.Equal("", new EventEntry("e", at, EventLevel.Info, null, "x").IconName);
    }

    [Fact]
    public void Texts()
    {
        var c = new EventCounts(13, 1, 6);
        Assert.Equal("全部 13", EventTexts.SegLabel(EventFilter.All, c));
        Assert.Equal("报警/提醒 1", EventTexts.SegLabel(EventFilter.Alarm, c));
        Assert.Equal("操作 6", EventTexts.SegLabel(EventFilter.Op, c));
        Assert.Equal("只看报警与提醒", EventTexts.SegTip(EventFilter.Alarm));
        Assert.Equal("没有报警或提醒事件", EventTexts.Empty(EventFilter.Alarm));
        Assert.Equal("没有操作事件", EventTexts.Empty(EventFilter.Op));
        Assert.Equal("没有事件", EventTexts.Empty(EventFilter.All));
        Assert.Equal("13 条", EventTexts.CountText(13));
        Assert.Equal(6, c.Of(EventFilter.Op));
    }

    // ———————————————————— 演示：与样稿在 WebKit 里渲染的「最近事件」对照 ————————————————————

    public static IEnumerable<object[]> DeviceTexts() => Names("texts").Where(o => !((string)o[0]).StartsWith("to-", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(DeviceTexts))]
    public void Demo_EventList_MatchesMock(string name)
    {
        var v = Get("texts", name);
        var p = v.GetProperty("probe");
        int k = int.Parse(Regex.Match(v.GetProperty("route").GetString()!, @"/device/(\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
        var d = Fleet(v).Device(k)!;
        var book = new EventBook(d.Id);
        book.Sync(d.Events);
        var counts = book.Counts;
        foreach (var (key, f) in new[] { ("all", EventFilter.All), ("alarm", EventFilter.Alarm), ("op", EventFilter.Op) })
        {
            var ev = p.GetProperty("ev").GetProperty(key);
            var segs = ev.GetProperty("segs").EnumerateArray().ToArray();
            Assert.Equal(new[] { "all", "alarm", "op" }, segs.Select(s => s[0].GetString()).ToArray());
            Assert.Equal(EventTexts.SegLabel(EventFilter.All, counts), segs[0][1].GetString());
            Assert.Equal(EventTexts.SegLabel(EventFilter.Alarm, counts), segs[1][1].GetString());
            Assert.Equal(EventTexts.SegLabel(EventFilter.Op, counts), segs[2][1].GetString());
            var list = book.Filter(f);
            var rows = ev.GetProperty("evs").EnumerateArray().ToArray();
            Assert.Equal(rows.Length, list.Count);
            for (int i = 0; i < rows.Length; i++)
            {
                var e = list[i];
                var r = rows[i];
                string lv = e.Level switch { EventLevel.Err => "err", EventLevel.Warn => "warn", EventLevel.Op => "op", _ => "info" };
                Assert.Equal(r[0].GetString(), "dve-" + e.Key);
                Assert.Equal(r[1].GetString(), "is-" + lv);
                Assert.Equal(r[2].GetString(), e.AtText);
                Assert.Equal(r[3].GetString(), e.RvText);
                Assert.Equal(r[4].GetBoolean(), e.IsDevice);
                Assert.Equal(Norm(r[5].GetString()), e.ShortText);
                Assert.Equal(r[6].GetString(), e.Text);
            }
            Assert.Equal(S(ev.GetProperty("empty")), list.Count == 0 ? EventTexts.Empty(f) : null);
        }
    }

    [Fact]
    public void Demo_SyncIsInPlace_AndFollowsActions()
    {
        var f = new DemoFleet(DemoParams.Parse("freeze=1"));
        var d = f.Device(1)!;
        var book = new EventBook();
        Assert.True(book.Sync(d.Events));
        var items = book.Items;
        Assert.False(book.Sync(d.Events));
        Assert.Same(items, book.Items);
        f.Act(d, Core.Guard.DeviceAction.AlarmAck, new Core.Guard.ActionPayload(Id: d.Alarms[0].Id));
        Assert.True(book.Sync(d.Events));
        Assert.Equal(d.Events[0].Id, book.Items[0].Key);
        Assert.Equal(EventLevel.Op, book.Items[0].Level);
    }
}
