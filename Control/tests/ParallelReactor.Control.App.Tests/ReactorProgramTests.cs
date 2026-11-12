using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Avalonia;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Layout;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Shell;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 反应釜页的「程序」面板与整页（PORT-PLAN §5 M5；样稿 js/pages/reactor.js progPanel / progStatus / stepCard / condHTML / safetyHTML / noProgHTML / alertsHTML / ownsAlarms / unPanel）：
/// <list type="bullet">
/// <item>对照样稿在 WebKit 里实际渲染、探针量出来的结果（Core 的 <see cref="ReactorMockVectors"/>，46 个场景：三个尺寸、各种程序状态、放大一档 / 两档、断线、最坏值）：
///   面包屑全文、状态行（全文 / 窄画布 / 点）、工具（定位当前步、放大 / 缩小能不能点、放大档文字）、每张卡的标题 / 标记 / 状态 / 报警暂停、条件框的值与按卡宽改写后的标签
///   （Core 的 StepFlowLayout.CondFit + 样稿自己量的字宽）、安全联锁框、没有程序的预览。</item>
/// <item>状态条：报警条上唯一的「确认」（页面拥有这条报警 → 检查器不再列它）、已确认、连接中断条 +「立即重连」、首次连接、急停条。</item>
/// <item>没有程序：程序库（本地只读 = 原因写一次；远程控制中 = 每行「下发」）、点一行预览、下发之后变成「已下发 · 待运行」；从未连上的设备只有一句说明。</item>
/// <item>未安装的位置：能打开（样稿 route），整页一块说明（文字逐字照样稿）。放大档：画布太窄时不能放大、面板变窄时回到 100 %。</item>
/// </list>
/// 用真的外壳视图模型 + 演示数据（冻结），不创建窗口。
/// </summary>
public class ReactorProgramTests
{
    private static (ShellVm Shell, ReactorPageVm Page) Open(int k, int n, string query = "freeze=1", int width = 1920, int height = 1080, string? setup = null)
    {
        var p = DemoParams.Parse(query);
        if (!p.Freeze) p = p with { Freeze = true };
        var s = new ShellVm(FleetVm.CreateDemo(p));
        s.Toasts.Frozen = true;
        s.Layout.Update(new Size(width, height));
        if (setup == "adddevice" && s.Fleet.DemoData is { } demo)
        {
            demo.AddDevice(new DemoNewDevice(Host: "192.168.1.50", Port: 8080, Token: "x", Alias: "5号机", AutoConnect: true, ModelKey: "PR8"));
            s.Fleet.RefreshNow();
        }
        s.ApplyDemoParams(p, Route.Reactor(k, n));
        var page = Assert.IsType<ReactorPageVm>(s.Page);
        return (s, page);
    }

    // ———————————————————— 对照样稿 ————————————————————

    public sealed record FlowCase(string Name, int W, int K, int N, string Query, int Zoom, JsonElement Probe)
    {
        public override string ToString() => Name;
    }

    private static readonly Lazy<IReadOnlyList<FlowCase>> Cases = new(() =>
    {
        var list = new List<FlowCase>();
        using var doc = JsonDocument.Parse(ReactorMockVectors.Json);
        foreach (var f in doc.RootElement.GetProperty("flow").EnumerateArray())
        {
            string name = f.GetProperty("name").GetString()!;
            string route = f.GetProperty("route").GetString()!;
            int w = f.GetProperty("w").GetInt32();
            var path = route[..route.IndexOf('?')].Split('/', StringSplitOptions.RemoveEmptyEntries);
            var q = route[(route.IndexOf('?') + 1)..];
            string pre = f.TryGetProperty("pre", out var pe) ? pe.GetString() ?? "" : "";
            int zoom = pre.Contains("__zoom = 2", StringComparison.Ordinal) ? 2 : pre.Contains("__zoom = 1", StringComparison.Ordinal) ? 1 : 0;
            list.Add(new FlowCase(name, w, int.Parse(path[1], CultureInfo.InvariantCulture), int.Parse(path[2], CultureInfo.InvariantCulture),
                string.Join("&", q.Split('&').Where(x => !x.StartsWith("bare=", StringComparison.Ordinal))), zoom, f.GetProperty("probe").Clone()));
        }
        return list;
    });

    public static IEnumerable<object[]> FlowNames() => Cases.Value.Select(c => new object[] { c.Name });

    private static int HeightOf(int w) => w switch { 1920 => 1080, 1600 => 900, _ => 768 };

    /// <summary>
    /// 每个场景：面包屑、状态行、工具、卡片、条件框、安全联锁、预览 —— 与样稿量出来的逐项相同。
    /// 条件框的标签按卡宽改写（结束条件 → 条件 → 只留值），用样稿自己量的字宽（fw：标签 14 /「条件」14 / … / 值 18 / 值 16）跑同一个 Core 的 CondFit。
    /// </summary>
    [Theory]
    [MemberData(nameof(FlowNames))]
    public void ProgramPanel_SameAsMock(string name)
    {
        var c = Cases.Value.Single(x => x.Name == name);
        var (_, page) = Open(c.K, c.N, c.Query, c.W, HeightOf(c.W));
        var vm = page.Program;
        var p = c.Probe;
        var cv = p.GetProperty("cv");
        vm.SetCanvasWidth(cv.GetProperty("ow").GetDouble());
        for (int i = 0; i < c.Zoom; i++) vm.ZoomInCommand.Execute(null);

        // 面包屑全文
        Assert.Equal(p.GetProperty("crumb").GetProperty("title").GetString(), vm.CrumbTip);

        // 状态行：全文 / 窄画布 / 点
        if (p.GetProperty("status").ValueKind == JsonValueKind.Null)
        {
            Assert.False(vm.HasStatus);
            Assert.True(vm.HasNoProgram);
        }
        else
        {
            var st = p.GetProperty("status");
            Assert.True(vm.HasStatus);
            Assert.Equal(st.GetProperty("title").GetString(), vm.StatusTitle);
            Assert.Equal(st.GetProperty("l").GetString(), vm.StatusWord + (vm.StatusFull.Length > 0 ? " · " + vm.StatusFull : ""));
            Assert.Equal(st.GetProperty("s").GetString(), vm.StatusShortWord + (vm.StatusShortWord.Length > 0 && vm.StatusShortText.Length > 0 ? " · " : "") + vm.StatusShortText);
            // 点的状态键（已结束画 ✓，探针量不到键）
            if (st.TryGetProperty("key", out var key)) Assert.Equal(key.GetString(), KeyCode(vm.StatusKey));
            else Assert.Equal(StateKey.Done, vm.StatusKey);
        }

        // 工具：定位当前步（有没有）、放大 / 缩小（能不能点）、放大档文字
        var tools = p.GetProperty("tools").EnumerateArray().Select(t => (Label: t[0].GetString()!, Disabled: t[1].GetBoolean())).ToList();
        if (tools.Count > 0)
        {
            Assert.Equal(tools.Any(t => t.Label == "定位当前步"), vm.CanLocate);
            Assert.Equal(tools.Single(t => t.Label == "放大画布").Disabled, vm.ZoomInReason != null);
            Assert.Equal(tools.Single(t => t.Label == "缩小画布").Disabled, vm.ZoomOutReason != null);
        }
        var zt = p.GetProperty("zoomText");
        Assert.Equal(zt.ValueKind == JsonValueKind.Null ? null : zt.GetString(), vm.ZoomText);

        // 卡片：标题、标记、状态、报警暂停；条件框：值 + 按卡宽改写后的标签与字号
        var flow = p.GetProperty("flow");
        bool preview = flow.GetProperty("preview").GetBoolean();
        var cards = preview ? vm.Preview : vm.Steps;
        var cols = p.GetProperty("cols").EnumerateArray().ToList();
        Assert.Equal(cols.Count, cards.Count);
        double stepW = flow.GetProperty("stepW").GetDouble();
        var fit = StepFlowLayout.Fit(cv.GetProperty("ow").GetDouble(), cards.Count, preview ? 0 : vm.Zoom);
        Assert.Equal(stepW, fit.StepW);
        Assert.Equal(flow.GetProperty("stale").GetBoolean(), vm.Stale && !preview);
        for (int i = 0; i < cols.Count; i++)
        {
            var col = cols[i];
            var card = cards[i];
            Assert.Equal(col.GetProperty("title").GetString(), card.Title);
            Assert.Equal(col.GetProperty("mark").GetString(), card.Mark);
            Assert.Equal(col.GetProperty("state").GetString(), card.State.ToString().ToLowerInvariant());
            Assert.Equal(col.GetProperty("held").GetBoolean(), card.Held);
            Assert.Equal(col.GetProperty("value").GetString(), card.CondValue);
            var fw = col.GetProperty("fw").EnumerateArray().Select(e => e.GetDouble()).ToArray();
            double Measure(string text, double size, int weight) =>
                text == "结束条件" ? fw[0] : text == "条件" ? fw[1] : text == "步骤时间" ? fw[2] : text == card.CondValue ? (size >= 18 ? fw[3] : fw[4]) : throw new InvalidOperationException("没量过的字：" + text);
            var (label, small) = StepFlowLayout.CondFit(card.CondLabel, card.CondValue, stepW, Measure);
            Assert.Equal(col.GetProperty("label").GetString(), label);
            Assert.Equal(col.GetProperty("sm").GetBoolean(), small);
            Assert.Equal(preview, card.Preview);
        }

        // 安全联锁框
        var safe = p.GetProperty("safe");
        if (safe.ValueKind != JsonValueKind.Null)
        {
            Assert.Equal(safe.GetProperty("v").GetString(), vm.SafeValue);
            Assert.Equal(Core.Text.Keep.Plain(safe.GetProperty("note").GetString()), vm.SafeNote);   // 样稿的说明带断行保护（Keep），比原文
            var tone = safe.GetProperty("cls").GetString()!.Split(' ').Last();
            Assert.Equal(tone, "is-" + vm.SafeTone.ToString().ToLowerInvariant());
            Assert.Equal("RV" + c.N + " :", vm.SafeRv);
        }
        else Assert.False(vm.ShowFlow);
    }

    private static string KeyCode(StateKey k) => k switch
    {
        StateKey.React => "react",
        StateKey.Offline => "offline",
        StateKey.Stopped => "stopped",
        StateKey.Done => "done",
        StateKey.Ready => "ready",
        StateKey.Wait => "wait",
        _ => k.ToString().ToLowerInvariant(),
    };

    [Fact]
    public void Vectors_CoverAllScenarios()
    {
        Assert.True(Cases.Value.Count >= 40, "样稿的「程序」画布场景：" + Cases.Value.Count);
        Assert.Contains(Cases.Value, c => c.Zoom == 2);
        Assert.Contains(Cases.Value, c => c.Probe.GetProperty("flow").GetProperty("preview").GetBoolean());
        Assert.Contains(Cases.Value, c => c.Probe.GetProperty("flow").GetProperty("stale").GetBoolean());
    }

    // ———————————————————— 状态行 / 工具 / 卡片（读得懂的几条） ————————————————————

    [Fact]
    public void Running_StatusToolsCardsAndCondition()
    {
        var (_, page) = Open(1, 1);
        var vm = page.Program;
        vm.SetCanvasWidth(564);
        Assert.Equal(("加氢-标准", "1号机 › RV1 › 加氢-标准", "程序 · 加氢-标准"), (vm.ProgName, vm.CrumbTip, vm.PanelName));
        Assert.Equal(("运行中", "第 3/4 步 · 预计 11:09 结束", "", "预计 11:09 结束"), (vm.StatusWord, vm.StatusFull, vm.StatusShortWord, vm.StatusShortText));
        Assert.True(vm.CanLocate);
        Assert.Null(vm.ZoomInReason);
        Assert.Equal("已是 100 %（字号不再缩小）", vm.ZoomOutReason);
        Assert.Equal(2, vm.CurIndex);
        var cur = vm.Steps[2];
        Assert.Equal(("3: 反应", "当前 · 剩 42 min", StepMarkKind.Cur), (cur.Title, cur.Mark, cur.MarkKind));
        Assert.Equal(("步骤时间", "> 72 min"), (cur.CondLabel, cur.CondValue));
        Assert.True(cur.CondRun);
        Assert.InRange(cur.CondFill, 0.04, 1);
        Assert.StartsWith("1号机 · RV1 · 步骤时间 > 72 min · 已进行 ", cur.CondTip, StringComparison.Ordinal);
        Assert.EndsWith(" % · 剩 42 min", cur.CondTip, StringComparison.Ordinal);
        Assert.Equal(("加热器 :", "保持", "设定值", "120.0 ℃"), (cur.Sections[0].Dev, cur.Sections[0].Mode, cur.Sections[0].Label, cur.Sections[0].Value));
        Assert.Equal(("进气阀 SV1 :", "补压保持 · 气体 A", "压力", "0.350 MPa"), (cur.Sections[1].Dev, cur.Sections[1].Mode, cur.Sections[1].Label, cur.Sections[1].Value));
        Assert.Equal("搅拌电机（全局 RV1–RV8 共用）", cur.Sections[2].DevTip);
        var done = vm.Steps[0];
        Assert.Equal(("已完成", StepMarkKind.Done, "check"), (done.Mark, done.MarkKind, done.MarkIcon));
        Assert.Matches(@"^1号机 · RV1 · 结束条件 温度 ≥ 119\.5 ℃ · 用时 \d+ min（\d\d:\d\d–\d\d:\d\d）$", done.CondTip);
        Assert.Equal(0, done.CondFill);
        Assert.Equal(("= 正常", SafetyTone.Ok, "急停未触发 · 量程内 ≤ 4.14 MPa · ≤ 200 ℃"), (vm.SafeValue, vm.SafeTone, vm.SafeNote));
        Assert.Equal("1/1/h2-std/2/Running", vm.ScrollKey);
    }

    [Fact]
    public void Zoom_ReflowsAndClampsToTheCanvas()
    {
        var (_, page) = Open(1, 1);
        var vm = page.Program;
        vm.SetCanvasWidth(564);
        int located = 0;
        vm.LocateRequested += (_, force) => { if (force) located++; };
        vm.ZoomInCommand.Execute(null);
        Assert.Equal((1, "115 %", 1), (vm.Zoom, vm.ZoomText, located));
        Assert.Equal(1, StepFlowLayout.Fit(564, 4, vm.Zoom).Per);           // 放大按放大后的尺寸重排：每行 2 张 → 1 张
        vm.ZoomInCommand.Execute(null);
        Assert.Equal((2, "130 %", "已是最大 130 %"), (vm.Zoom, vm.ZoomText, vm.ZoomInReason));
        vm.ZoomInCommand.Execute(null);                                       // 已是最大：不变
        Assert.Equal(2, vm.Zoom);
        vm.FitCommand.Execute(null);
        Assert.Equal((0, (string?)null, 3), (vm.Zoom, vm.ZoomText, located));
        // 面板很窄：放大后一行放不下一张最窄的卡 —— 不能再放大；已经放大的回到 100 %（样稿 measure）
        vm.ZoomInCommand.Execute(null);
        vm.SetCanvasWidth(300);
        Assert.Equal(0, vm.Zoom);
        Assert.Equal("画布太窄 · 放大后一行放不下一张步骤卡", vm.ZoomInReason);
        vm.LocateCommand.Execute(null);
        Assert.Equal(5, located);
    }

    // ———————————————————— 状态条 · 页面拥有的报警 · 断线说明条 ————————————————————

    [Fact]
    public void AlarmStrip_OwnsTheOnlyAck_InspectorDoesNotRepeatIt()
    {
        var (shell, page) = Open(3, 11, "freeze=1&sel=3/11");
        var a = Assert.Single(page.Alerts);
        Assert.Equal((ReactorAlertTone.Err, "超压报警", "确认", (string?)null), (a.Tone, a.Title, a.ButtonText, a.ButtonReason));
        Assert.Equal("RV11 超压 3.12 MPa ≥ 3.00 MPa · 已关闭 SV11 · 程序在第 2/4 步暂停", a.Text);
        var owned = Assert.Single(page.OwnedAlarms);
        Assert.Equal(ReactorPageVm.StripOwnerName, owned.Value);
        Assert.StartsWith("3:", owned.Key, StringComparison.Ordinal);
        // 检查器（选中的就是这个反应釜）：这条报警不再列出（屏幕上只有一个「确认」）
        var insp = Assert.IsType<ReactorInspectorVm>(shell.Inspector.Body);
        Assert.Empty(insp.TopAlarms);
        Assert.True(page.ShowsReconnect);
        // 确认：报警条写「✓ 已确认」，页面不再拥有它
        a.ButtonCommand.Execute(null);
        shell.Fleet.RefreshNow();
        var acked = Assert.Single(page.Alerts);
        Assert.True(acked.Acked);
        Assert.Null(acked.ButtonText);
        Assert.Empty(page.OwnedAlarms);
    }

    [Fact]
    public void WarnStrip_IsNeutral_AndOwned()
    {
        var (_, page) = Open(1, 3);
        var a = Assert.Single(page.Alerts);
        Assert.Equal((ReactorAlertTone.Warn, "warn", "提醒", "确认"), (a.Tone, a.Icon, a.Title, a.ButtonText));
        Assert.Single(page.OwnedAlarms);
    }

    [Fact]
    public void OfflineBanner_ReconnectOnThePage()
    {
        var (_, page) = Open(4, 1, "freeze=1&devs=4&sel=4/1");
        var off = page.Alerts[0];
        Assert.True(off.IsOff);
        Assert.False(off.HasIcon);
        Assert.Matches(@"^连接中断 · 重连中 \d+ s$", off.ConnText);
        Assert.Equal("读数停在最后时刻 · 数据停在 10:11:40", off.Text);
        Assert.Equal(("立即重连", "refresh", (string?)null), (off.ButtonText, off.ButtonIcon, off.ButtonReason));
        Assert.True(page.ShowsReconnect);
        var vm = page.Program;
        Assert.True(vm.Stale);
        Assert.Equal(("离线", "停在第 3/4 步 · 数据停在 10:11:40"), (vm.StatusWord, vm.StatusFull));
        Assert.Equal("当前（离线）", vm.Steps[2].Mark);
        Assert.False(vm.Steps[2].CondRun);
        Assert.Equal(("= 未知", SafetyTone.Mute), (vm.SafeValue, vm.SafeTone));
    }

    [Fact]
    public void EstopStrip_ThenAlarm_ProgramAborted()
    {
        var (_, page) = Open(3, 11, "freeze=1&estop=3&sel=3/11");
        Assert.Equal(2, page.Alerts.Count);
        Assert.Equal((ReactorAlertTone.Err, "estop"), (page.Alerts[0].Tone, page.Alerts[0].Icon));
        Assert.Equal("加热 / 进气 / 搅拌已切断 · 复位只能在3号机触屏上操作", page.Alerts[0].Text);
        Assert.Null(page.Alerts[0].ButtonText);
        Assert.Equal("超压报警", page.Alerts[1].Title);
        var vm = page.Program;
        Assert.Equal(("已中止", "第 2/4 步"), (vm.StatusWord, vm.StatusFull));
        Assert.Equal(("已中止", StepMarkKind.Abort), (vm.Steps[1].Mark, vm.Steps[1].MarkKind));
        Assert.True(vm.Steps[1].IsAbort);
        Assert.Equal(1, vm.CurIndex);
        Assert.Equal(("= 急停", SafetyTone.Err, "急停已触发 · 加热 / 进气 / 搅拌已切断 · 需在触屏复位"), (vm.SafeValue, vm.SafeTone, vm.SafeNote));
    }

    // ———————————————————— 没有程序 · 从未连上 · 未安装 ————————————————————

    [Fact]
    public void NoProgram_LocalReadOnly_ReasonOnce_PreviewFirst()
    {
        var (_, page) = Open(3, 13, "freeze=1&sel=3/13");
        var vm = page.Program;
        Assert.True(vm.HasNoProgram);
        Assert.False(vm.HasProgram);
        Assert.Equal(("RV13 没有程序", "从3号机（HT-PR16）的程序库选一个下发：", "3号机 › RV13 › 无程序"), (vm.NoProgTitle, vm.NoProgLead, vm.CrumbTip));
        Assert.Equal(new[] { "筛选-80 ℃", "加氢-标准", "吹扫-置换 ×3" }, vm.Library.Select(r => r.Name));
        Assert.Equal("80 ℃ · 1.2 MPa N₂ · 反应 90 min · 4 步", vm.Library[0].Note);
        Assert.All(vm.Library, r => Assert.False(r.ShowLoad));
        Assert.Equal("3号机为本地只读 · 先申请控制权", vm.LibReason);
        Assert.True(vm.Library[0].IsSelected);
        Assert.Equal(("预览：筛选-80 ℃", 4), (vm.PreviewTitle, vm.Preview.Count));
        Assert.All(vm.Preview, c => Assert.Equal(("", SegmentState.Next, true), (c.Mark, c.State, c.Preview)));
        Assert.Equal("进气阀 SV13 :", vm.Preview[1].Sections[1].Dev);
        // 单击一行：预览它
        vm.Library[2].PickCommand.Execute(null);
        Assert.Equal(("预览：吹扫-置换 ×3", 3), (vm.PreviewTitle, vm.Preview.Count));
        Assert.True(vm.Library[2].IsSelected);
        Assert.False(vm.Library[0].IsSelected);
    }

    [Fact]
    public void NoProgram_Remote_LoadPerRow_ThenLoadedAndReady()
    {
        var (shell, page) = Open(3, 13, "freeze=1&sel=3/13&lease=3:remote");
        var vm = page.Program;
        Assert.Null(vm.LibReason);
        Assert.All(vm.Library, r => Assert.True(r.ShowLoad));
        Assert.All(vm.Library, r => Assert.Null(r.LoadReason));
        vm.Library[1].LoadCommand.Execute(null);
        shell.Fleet.RefreshNow();
        Assert.True(vm.HasProgram);
        Assert.False(vm.HasNoProgram);
        Assert.Equal(("加氢-标准", "已下发", "待运行"), (vm.ProgName, vm.StatusWord, vm.StatusFull));
        Assert.False(vm.CanLocate);
        Assert.Equal(-1, vm.CurIndex);
        Assert.All(vm.Steps, s => Assert.Equal("", s.Mark));
    }

    [Fact]
    public void NeverConnected_OnlyALine()
    {
        var (_, page) = Open(4, 1, setup: "adddevice");
        var vm = page.Program;
        Assert.True(vm.HasNoProgram);
        Assert.True(vm.NeverConnected);
        Assert.Equal(("RV1 没有程序", "5号机尚未连接 · 连上后这里显示程序库"), (vm.NoProgTitle, vm.NoProgLead));
        Assert.Empty(vm.Library);
        Assert.False(vm.HasPreview);
        Assert.Equal("首次连接 · 尚未收到数据", Assert.Single(page.Alerts).Text);
        Assert.Null(page.Alerts[0].ButtonText);                              // 首次连接：没有按钮（正在自动连）
    }

    [Fact]
    public void Uninstalled_Routable_QuietExplanation()
    {
        var (shell, page) = Open(1, 5);
        Assert.Equal(Route.Reactor(1, 5), shell.Route);
        Assert.False(page.Installed);
        Assert.Empty(page.OwnedAlarms);
        var u = page.Un;
        Assert.Equal(("1号机 › RV5", "1号机 · RV5 未安装", "RV5 · 空位"), (u.Crumb, u.Title, u.Caption));
        Assert.Equal("1号机（HT-PR8 · 现场）只装了 RV1–RV4，RV5–RV8 未安装。这个位置没有釜体，也没有温度、压力、加热和搅拌测点，不能下发程序。", u.Text);
        Assert.Equal(("HT-PR8 · 现场", "8 路 · 已装 RV1–RV4", "50 mL 釜 · 溶液 ≤ 20 mL", "示例型号 · 参数为假设"), (u.KvModel, u.KvChannels, u.KvVessel, u.KvNote));
        Assert.Equal(("1号机 的位置 · 8 路", "去已安装的反应釜", "打开1号机设备页"), (u.SlotsTitle, u.ChipsTitle, u.OpenText));
        Assert.Equal(8, u.Slots.Count);
        Assert.True(u.Slots[4].IsCur);
        Assert.False(u.Slots[4].Installed);
        Assert.Equal("RV5 未安装", u.Slots[4].Tip);
        Assert.Equal("打开 1号机 · RV1 · 反应中", u.Slots[0].Tip);
        Assert.Equal(new[] { "RV1", "RV2", "RV3", "RV4" }, u.Chips.Select(c => c.Rv));
        Assert.Equal(("反应中", "打开 1号机 · RV1"), (u.Chips[0].Word, u.Chips[0].Tip));
        // 去已安装的反应釜 / 设备页
        u.Chips[2].OpenCommand.Execute(null);
        Assert.Equal(Route.Reactor(1, 3), shell.Route);
        Assert.Contains(shell.Tabs.Tabs, t => t.Route == Route.Reactor(1, 3));
        Assert.DoesNotContain(shell.Tabs.Tabs, t => t.Route == Route.Reactor(1, 5));   // 未安装位置的页签只在它是当前页签时留着
    }

    [Fact]
    public void Uninstalled_OutOfRange_IsNotRoutable()
    {
        var (shell, _) = Open(1, 1);
        shell.Navigate(Route.Reactor(1, 9));
        Assert.Equal(Route.Overview, shell.Route);
    }

    // ———————————————————— 版式：宽版式只在工作区 ≥ 1800 ————————————————————

    [Theory]
    [InlineData(1226, 752, false, true)]     // 1920 × 1080
    [InlineData(1526, 752, false, true)]     // 1920 收起资源管理器
    [InlineData(936, 592, false, false)]     // 1600 × 900
    [InlineData(1002, 460, false, false)]    // 1366 × 768
    [InlineData(2196, 1112, true, false)]    // 2560 × 1440 收起资源管理器
    public void Layout_WideOnlyFrom1800(double w, double h, bool wide, bool prog2)
    {
        var g = ReactorPageLayout.Compute(w, h);
        Assert.Equal((wide, prog2), (g.Wide, g.Prog2));
        Assert.Equal(wide, g[ReactorPanel.Props] != null);
        // 关掉「程序」：右边一栏铺满整个宽度
        var x = ReactorPageLayout.Compute(w, h, new[] { ReactorPanel.Program });
        Assert.Null(x[ReactorPanel.Program]);
        Assert.Equal(0, x[ReactorPanel.Curves]!.Value.X);
        Assert.Equal(w, x[ReactorPanel.Curves]!.Value.W, 3);
    }
}
