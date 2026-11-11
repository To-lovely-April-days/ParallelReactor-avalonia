using System;
using System.Linq;
using ParallelReactor.Api;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;
using Xunit;
using static ParallelReactor.Control.Tests.LiveFrames;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// Core.Programs.ReactorPageText（反应釜页没有程序时 / 未安装的位置的文字，逐字照样稿 reactor.js noProgHTML / unPanel）与
/// ProgramPanelText.HmiStatus（真机的程序对不上台账时的状态行，R5）。
/// </summary>
public class ReactorPageTextTests
{
    private static readonly ModelProfile Pr84 = ModelProfiles.Embedded.Get("PR8-4")!;
    private static readonly ModelProfile Pr16 = ModelProfiles.Embedded.Get("PR16")!;

    [Fact]
    public void NoProgram_Texts()
    {
        Assert.Equal("RV13 没有程序", ReactorPageText.NoProgramTitle(13));
        Assert.Equal("从3号机（HT-PR16）的程序库选一个下发：", ReactorPageText.LibraryLead("3号机", Pr16.Label));
        Assert.Equal("5号机尚未连接 · 连上后这里显示程序库", ReactorPageText.NeverConnectedLead("5号机"));
        Assert.Equal("预览：筛选-80 ℃", ReactorPageText.PreviewTitle("筛选-80 ℃"));
        Assert.Equal("只读 · 下发后在这里运行", ReactorPageText.PreviewSub);
        var fleet = new DemoFleet(DemoParams.Default with { Freeze = true });
        var lib = fleet.Device(3)!.Programs;
        Assert.Equal("80 ℃ · 1.2 MPa N₂ · 反应 90 min · 4 步", ReactorPageText.LibraryNote(lib[0]));
        // 真机的草稿没有说明：只写步数
        Assert.Equal("4 步", ReactorPageText.LibraryNote(lib[0] with { Note = "" }));
        // 预览：每一步都是「未到」
        var prev = ReactorPageText.Preview(lib[0]);
        Assert.Equal((ProgramStatus.Loaded, -1, "prev-" + lib[0].Id), (prev.Status, prev.StepIndex, prev.Id));
        Assert.All(Enumerable.Range(0, prev.Steps.Count), i => Assert.Equal(SegmentState.Next, Progress.StepState(prev, i)));
    }

    [Fact]
    public void Uninstalled_Texts()
    {
        Assert.Equal("1号机 · RV5 未安装", ReactorPageText.UninstalledTitle("1号机", 5));
        Assert.Equal("RV5–RV8", ReactorPageText.UninstalledRuns(Pr84));
        Assert.Equal("1号机（HT-PR8 · 现场）只装了 RV1–RV4，RV5–RV8 未安装。这个位置没有釜体，也没有温度、压力、加热和搅拌测点，不能下发程序。",
            ReactorPageText.UninstalledText("1号机", Pr84));
        Assert.Equal(new[] { ("型号", "HT-PR8 · 现场"), ("通道", "8 路 · 已装 RV1–RV4"), ("釜", "50 mL 釜 · 溶液 ≤ 20 mL"), ("说明", "示例型号 · 参数为假设") },
            ReactorPageText.UninstalledKv(Pr84).ToArray());
        Assert.Equal("1号机 的位置 · 8 路", ReactorPageText.SlotsTitle("1号机", Pr84));
        Assert.Equal("打开1号机设备页", ReactorPageText.OpenDevice("1号机"));
        Assert.Equal("RV5 · 空位", ReactorPageText.EmptySlot(5));
        Assert.Equal("打开 1号机 · RV1 · 反应中", ReactorPageText.SlotTip("1号机", 1, true, "反应中"));
        Assert.Equal("RV6 未安装", ReactorPageText.SlotTip("1号机", 6, false, "未安装"));
        Assert.Equal("打开 1号机 · RV2", ReactorPageText.ChipTip("1号机", 2));
    }

    [Fact]
    public void Degraded_Text()
    {
        Assert.Equal("这个程序不是本机下发的 · 设备上的程序内容读不到（HMI 没有程序读取接口），步骤卡片与剩余时间不显示", ReactorPageText.DegradedText(null));
        Assert.StartsWith("步骤数不同（设备 5 步 · 本机下发 4 步） · ", ReactorPageText.DegradedText("步骤数不同（设备 5 步 · 本机下发 4 步）"), StringComparison.Ordinal);
    }

    [Fact]
    public void HmiStatus_EachState()
    {
        var t = new DateTime(2026, 11, 8, 2, 0, 0, DateTimeKind.Utc);
        ProgramStatusLine S(string state, int step = 1, int count = 5, string msg = "", bool online = true) =>
            ProgramPanelText.HmiStatus(Prog(step, count, state, t, ByMe, msg), online, "数据停在 10:11:40");
        var run = S("running", msg: " 02/05 升压 · 充压 1.2 / 3.5 MPa ");
        Assert.Equal((StateKey.React, "运行中", "第 2/5 步 · 02/05 升压 · 充压 1.2 / 3.5 MPa", "运行中 · 2/5"), (run.Key, run.Word, run.Full, run.Short));
        Assert.Equal(("第 2/5 步", "运行中 · 第 2/5 步"), (S("running").Full, S("running").Title));
        Assert.Equal((StateKey.Wait, "等待气路", "2/5"), (S("waitingGas").Key, S("waitingGas").Word, S("waitingGas").ShortText));
        Assert.Equal((StateKey.Done, "已结束", "共 5 步"), (S("completed").Key, S("completed").Word, S("completed").Full));
        Assert.Equal((StateKey.Stopped, "已中止", "第 2/5 步"), (S("aborted").Key, S("aborted").Word, S("aborted").Full));
        Assert.Equal((StateKey.Stopped, "报警中止"), (S("failed").Key, S("failed").Word));
        var off = S("running", online: false);
        Assert.Equal((StateKey.Offline, "离线", "停在第 2/5 步 · 数据停在 10:11:40", "离线 · 停在 2/5"), (off.Key, off.Word, off.Full, off.Short));
        // 准备中（第 −1 步）按第 1 步
        Assert.Equal("第 1/5 步", S("preparing", step: -1).Full);
    }
}
