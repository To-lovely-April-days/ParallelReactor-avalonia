using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.Programs.Editor;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages.Programs;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;
using ParallelReactor.Models;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 融合版 F6 程序页：默认条件矩阵（H1 模板 + 演示值）、单路程序（锁住在跑的程序、编辑、保存、复制）、下发并运行（溶液体积 → 程序 → 运行）、
/// 预运行检查对话框、程序库筛选、地址参数（pgm / pgbank / pgvol）、本机存储（library.json 迁移 drafts.json、experiments.json）。
/// 数据 = 融合版演示数据（1号机 RV1–4、6、8 在跑，RV5 空闲，RV7 停用；远程控制）。
/// </summary>
public class F6ProgramTests
{
    private static ShellVm Shell(string query = "")
    {
        var p = DemoParams.Parse("fusion=1&freeze=1");
        var s = new ShellVm(FleetVm.CreateDemo(p));
        s.Toasts.Frozen = true;
        s.Layout.Update(new Size(1920, 1080));
        s.ApplyDemoParams(DemoParams.Parse("fusion=1&freeze=1&page=prog&dev=1&apop=0" + (query.Length > 0 ? "&" + query : "")));
        return s;
    }

    private static ProgramPageVm Page(ShellVm s) => Assert.IsType<ProgramPageVm>(s.Page);

    private static string LastToast(ShellVm s) => s.Toasts.Items.LastOrDefault()?.Text ?? "";

    [Fact]
    public void Default_IsMatrixWithMockTemplate()
    {
        var pg = Page(Shell());
        Assert.True(pg.IsMatrix);
        Assert.Equal("H1", pg.SelId);
        Assert.Equal("模板「催化加氢筛选 · 120 ℃」 · 9 步", pg.Mx.TemplateSub);
        Assert.Equal(8, pg.Mx.Columns.Count);
        Assert.Equal("7", pg.Mx.IncText);
        Assert.Equal(new[] { "第 06 步目标压力：RV4 4.00 MPa · 超出 HT-PR8 范围 0.06 – 3.14 MPa", "RV1–RV4、RV6、RV8 正在运行，最晚约 15:50 结束 · 结束后才能下发，可以先保存" },
            pg.Mx.Issues.Select(i => i.Msg));
        Assert.Equal(pg.Mx.Issues[0].Msg, pg.Mx.RunReason);
        Assert.Equal("RV1 · 2 h 45 min", pg.Mx.Shortest);
        Assert.Equal("RV8 · 3 h 12 min", pg.Mx.Longest);
        var col1 = pg.Mx.Columns[0];
        Assert.Equal(("运行中", " · 15:22"), (col1.Status, col1.Time));
        Assert.Equal("已停用", pg.Mx.Columns[6].Status);
        Assert.False(pg.Mx.Columns[6].Included);
        // 行：样品、体积、9 个步骤标题，每路的 3 行（升温目标、充压目标、反应最长时间）+ 共用 12 行
        Assert.Equal(2 + 9 + 3 + 12, pg.Mx.Rows.Count);
        // 搅拌转速是共用的一行：写明为什么不能按路变化
        var rpm = pg.Mx.Rows.OfType<MxSharedRow>().Single(r => r.Spec.Key == "rpm");
        Assert.Equal(("一台电机带全部釜 · 转速不能按路变化", "f-link", "400"), (rpm.Why, rpm.WhyIcon, rpm.Text));
        Assert.DoesNotContain(pg.Mx.Rows.OfType<MxPerRow>(), r => r.Spec.Key == "rpm");
        var temp = pg.Mx.Rows.OfType<MxPerRow>().First(r => r.Spec.Key == "temp");
        Assert.Equal(new[] { "100", "110", "120", "130", "100", "110", "120", "130" }, temp.Cells.Select(c => c.Text));
        Assert.True(temp.Cells[0].Chg);
        Assert.False(temp.Cells[6].Enabled);
        var pres = pg.Mx.Rows.OfType<MxPerRow>().First(r => r.Spec.Key == "pres");
        Assert.True(pres.Cells[3].Bad);
        Assert.Equal("2 h 45 min", pg.Mx.Foot[0].Text);
        Assert.Equal("—", pg.Mx.Foot[6].Text);
        var stir = pg.Mx.Rows.OfType<MxSharedRow>().First(r => r.Spec.Key == "rpm");
        Assert.Equal(("一台电机带全部釜 · 转速不能按路变化", "f-link", "400"), (stir.Why, stir.WhyIcon, stir.Text));
        Assert.Contains("矩阵", string.Join(",", pg.Tools.Select(t => t.Title)) + "矩阵");
        Assert.Equal(new[] { "方案", "文件", "运行" }, pg.Tools.Select(t => t.Title));
    }

    [Fact]
    public void Matrix_EditCellsAndVolume()
    {
        var s = Shell();
        var pg = Page(s);
        var pres = pg.Mx.Rows.OfType<MxPerRow>().First(r => r.Spec.Key == "pres");
        pres.Cells[3].Text = "3.00";
        Assert.DoesNotContain(pg.Mx.Issues, i => i.IsNo);
        var vol = pg.Mx.Rows.OfType<MxVolRow>().Single();
        vol.Cells[4].Text = "";
        Assert.Contains(pg.Mx.Issues, i => i.Msg == "溶液体积：RV5 未填 · 算不出吸气量");
        Assert.True(vol.Cells[4].Bad);
        vol.Cells[4].Text = "6.25";
        Assert.Equal("6.3", vol.Cells[4].Text);
        Assert.Contains("气相体积约", vol.Cells[4].Tip);
        // 不参加 RV1：它的格变灰、计数变化
        pg.Mx.Columns[0].Included = false;
        Assert.Equal("6", pg.Mx.IncText);
        Assert.False(pres.Cells[0].Enabled);
        // 等差
        var temp = pg.Mx.Rows.OfType<MxPerRow>().First(r => r.Spec.Key == "temp");
        temp.RampCommand.Execute(null);
        Assert.StartsWith("目标温度：从 110 到 130 °C 等差填充 6 路", LastToast(s));
    }

    [Fact]
    public void Matrix_Params_VolBadAndBank()
    {
        var pg = Page(Shell("pgm=mx&pgvol=5:"));
        Assert.Contains(pg.Mx.Issues, i => i.Msg == "溶液体积：RV5 未填 · 算不出吸气量");
        var s3 = Shell("pgm=mx&pgbank=2");
        s3.ApplyDemoParams(DemoParams.Parse("fusion=1&freeze=1&page=prog&dev=3&apop=0&pgm=mx&pgbank=2"));
        var p3 = Page(s3);
        Assert.True(p3.Mx.IsMany);
        Assert.Equal("RV9", p3.Mx.Columns[0].Rv);
        Assert.Equal("C", p3.SelId);
        Assert.Equal("14", p3.Mx.IncText);
        Assert.Contains(p3.Mx.Issues, i => i.Msg == "3号机为本地只读 · 在工具条「申请控制」后才能下发");
    }

    [Fact]
    public async Task Matrix_SendOnlyIdleChannel()
    {
        var s = Shell();
        var pg = Page(s);
        foreach (var c in pg.Mx.Columns.Where(c => c.N != 5)) c.Included = false;
        Assert.Null(pg.Mx.RunReason);
        await pg.Mx.SendAsync();
        var d = s.Fleet.Device(1)!;
        var r5 = d.Reactor(5)!;
        Assert.Equal("催化加氢筛选 · 120 ℃", r5.Program!.Name);
        Assert.Equal(ProgramStatus.Running, r5.Program.Status);
        Assert.Equal(100, r5.Program.Record!.Steps[4].Sp);      // RV5 的升温目标 = 矩阵里的 100
        Assert.Equal(1, pg.Experiments.RunCount);
        Assert.StartsWith("已按每路条件下发 1 个程序到 1号机", LastToast(s));
        Assert.Contains("运行 1 路", pg.Lib.First(x => x.Id == "H1").TagText);
    }

    [Fact]
    public async Task One_LockedProgram_SendToRv5()
    {
        var s = Shell("pgm=one");
        var pg = Page(s);
        Assert.True(pg.IsOne);
        Assert.Equal("A", pg.SelId);
        var one = pg.One;
        Assert.Equal("共 6 步 · 约 1 h 56 min", one.TotalText);
        Assert.True(one.Locked);
        Assert.Equal("正在 1号机 RV1、RV2、RV3、RV4、RV6、RV8 运行 · 运行中的程序不能直接修改", one.LockText);
        Assert.Equal("第 03 步 · 反应", one.ParamSub);
        Assert.Equal(new[] { "time", "minTime", "mmolOn", "stallOn" }, one.Fields.Select(f => f.Spec.Key));   // visibleWhen：吸气量 / 吸气停止关着，不显示它们的参数
        Assert.True(one.Fields.All(f => f.Locked));
        Assert.Equal(new[] { "51 min", "3 min", "≤ 45 min", "约 15 min", "2 min", "立即" }, one.Steps.Select(x => x.Dur));
        Assert.Equal(new[] { false, false, false, false, false, true }, one.Steps.Select(x => x.IsEnd));
        Assert.False(one.Migrated);
        Assert.False(one.HasHints);
        Assert.Equal("运行 6 路", pg.Lib.First(x => x.Id == "A").TagText);
        Assert.Equal("1 处待改", pg.Lib.First(x => x.Id == "OX").TagText);
        var chip5 = one.Chips.First(c => c.N == 5);
        Assert.True(chip5.IsChecked);
        Assert.Equal("已就绪", chip5.Status);
        Assert.True(one.Chips.First(c => c.N == 1).Disabled);
        Assert.Equal("停用", one.Chips.First(c => c.N == 7).Status);
        Assert.Null(one.RunReason);
        one.VolText = "";
        Assert.Equal("溶液体积未填 · 算不出吸气量", one.RunReason);
        one.VolText = "6.2";
        await one.SendAsync();
        var r5 = s.Fleet.Device(1)!.Reactor(5)!;
        Assert.Equal("平行合成程序 A", r5.Program!.Name);
        Assert.Equal(ProgramStatus.Running, r5.Program.Status);
        Assert.Equal(6.2, r5.SolutionMl);
        Assert.Equal("已下发「平行合成程序 A」到 1号机 RV5 · 开始运行", LastToast(s));
        Assert.Equal("运行 7 路", pg.Lib.First(x => x.Id == "A").TagText);
        Assert.Equal("先在右下选择目标通道", one.RunReason);
    }

    [Fact]
    public void One_EditDuplicateSave()
    {
        var s = Shell("pgm=one");
        var pg = Page(s);
        // 在跑的程序不能加步骤；复制后可以
        pg.One.AddTypes.First(a => a.Type == StepType.Stir).Command.Execute(null);
        Assert.Equal(6, pg.Sel!.Steps.Count);
        pg.One.DupCommand.Execute(null);
        Assert.Equal("平行合成程序 A（副本）", pg.Sel!.Name);
        Assert.False(pg.One.Locked);
        Assert.Equal("平行合成程序 A（副本） · 未保存", pg.One.ProgName);
        pg.One.AddTypes.First(a => a.Type == StepType.Stir).Command.Execute(null);
        Assert.Equal(7, pg.Sel.Steps.Count);
        Assert.Equal(StepType.Stir, pg.Sel.Steps[3].Type);
        Assert.Equal(3, pg.One.StepIndex);
        var rpm = pg.One.Fields.First(f => f.Spec.Key == "rpm");
        Assert.Equal("0 – 450", rpm.Range);
        rpm.Text = "500";
        Assert.True(rpm.Bad);
        Assert.Equal("1 处待改", pg.One.IssueTag);
        Assert.StartsWith("先改掉待改项", pg.One.RunReason);
        rpm.Text = "300";
        Assert.Equal("校验通过", pg.One.IssueTag);
        pg.One.UpCommand.Execute(null);
        Assert.Equal(StepType.Stir, pg.Sel.Steps[2].Type);
        pg.One.DeleteCommand.Execute(null);
        Assert.Equal(6, pg.Sel.Steps.Count);
        // 压力字段写约合 psi
        pg.One.Steps[1].PickCommand.Execute(null);
        Assert.Equal("0.06 – 3.14（≈ 363 psi）", pg.One.Fields.First(f => f.Spec.Key == "pres").Range);
        // 保存（演示不写文件）：草稿标记去掉、修改人 = 本机客户端名
        var save = pg.Tools.SelectMany(g => g.Tools).First(t => t.Label == "保存");
        Assert.Null(save.Reason);
        save.Command.Execute(null);
        Assert.False(pg.IsDirty(pg.SelId!));
        Assert.False(pg.Sel.Draft);
        Assert.Equal("没有未保存的修改", save.Reason);
    }

    /// <summary>程序步骤 v0.8：「结束」固定在最后（不能删 / 挪，添加的步骤插在它前面）；添加列表 8 种；参数按 visibleWhen 显示。</summary>
    [Fact]
    public void One_V08_EndPinned_AddBeforeEnd_VisibleWhen()
    {
        var s = Shell("pgm=one&pgp=H1");
        var pg = Page(s);
        var one = pg.One;
        Assert.Equal("H1", pg.SelId);
        Assert.False(one.Locked);
        Assert.Equal(new[] { "气密检查", "置换", "搅拌", "升温", "充压", "保持", "反应", "放空" }, one.AddTypes.Select(a => a.Label));
        Assert.Equal("共 9 步 · 约 2 h 51 min", one.TotalText);
        one.Steps[8].PickCommand.Execute(null);
        Assert.True(one.IsEndStep);
        Assert.Empty(one.Fields);
        Assert.StartsWith("固定的最后一步", one.Note);
        Assert.Equal(("「结束」固定在最后，不能删、不能挪", "「结束」固定在最后，不能删、不能挪", "「结束」固定在最后，不能删、不能挪"), (one.UpReason, one.DownReason, one.DeleteReason));
        one.DeleteCommand.Execute(null);
        Assert.Equal(9, pg.Sel!.Steps.Count);
        // 选着结束时添加：插在结束之前
        one.AddTypes.First(a => a.Type == StepType.Hold).Command.Execute(null);
        Assert.Equal((10, StepType.Hold, StepType.End, 8), (pg.Sel.Steps.Count, pg.Sel.Steps[8].Type, pg.Sel.Steps[^1].Type, one.StepIndex));
        Assert.Equal("「结束」固定在最后，不能挪到它后面", one.DownReason);
        Assert.Equal(new[] { "until", "time" }, one.Fields.Select(f => f.Spec.Key));
        var until = one.Fields.First(f => f.Spec.Key == "until");
        until.Selected = until.Options.First(o => o.Code == "tempBelow");
        Assert.Equal(new[] { "until", "temp", "maxWait", "onTimeout" }, one.Fields.Select(f => f.Spec.Key));
        Assert.Equal("约 15 min", one.Steps[8].Dur);
        // 反应：吸气停止的参数跟着开关
        one.Steps[6].PickCommand.Execute(null);
        Assert.Equal(new[] { "time", "minTime", "mmolOn", "stallOn", "stallRate", "stallMin" }, one.Fields.Select(f => f.Spec.Key));
        Assert.Equal("mmol/min", one.Fields.First(f => f.Spec.Key == "stallRate").Unit);
        Assert.Equal(156, one.Fields.First(f => f.Spec.Key == "stallRate").InputWidth);   // 单位长的输入框加宽
        s.Dispose();
    }

    /// <summary>旧格式转换来的程序（演示的 OX）：顶上提示 + 收紧说明、待改项、提示；没改也能保存（按新格式写回，提示条消失）。</summary>
    [Fact]
    public void One_MigratedProgram_NoticeIssuesHints_Save()
    {
        var s = Shell("pgm=one&pgp=OX");
        var pg = Page(s);
        var one = pg.One;
        Assert.True(one.Migrated);
        Assert.Contains("先吹总管 12 次 超出新范围，已改为 10 次", one.MigNote);
        Assert.Equal("1 处待改", one.IssueTag);                                    // 充压 3.40 MPa 超出上限
        Assert.Contains("程序里没有放空 · 结束时釜里可能带压", one.Hints);
        Assert.True(one.HasHints);
        var save = pg.Tools.SelectMany(g => g.Tools).First(t => t.Label == "保存");
        Assert.Null(save.Reason);
        save.Command.Execute(null);
        Assert.False(pg.Sel!.Migrated);
        Assert.False(one.Migrated);
        Assert.Contains("已按新步骤格式保存", LastToast(s));
        // 预运行检查：程序待改 = 不通过；没有放空 = 提醒
        var d = Shell("pgm=one&pgp=OX&dialog=pgcheck");
        var dlg = Assert.IsType<PreflightDialogVm>(d.Dialogs.Current);
        Assert.Equal(CheckKind.No, dlg.Rows.First(r => r.Title == "程序").Kind);
        var vent = dlg.Rows.First(r => r.Title == "结束时压力");
        Assert.Equal((CheckKind.Warn, "程序里没有放空 · 结束时釜里可能带压"), (vent.Kind, vent.Detail));
        s.Dispose();
        d.Dispose();
    }

    [Fact]
    public void NewProgram_IsEmpty_RedoNotesMigration()
    {
        var s = Shell("pgm=one");
        var pg = Page(s);
        pg.Tools.SelectMany(g => g.Tools).First(t => t.Label == "新建程序").Command.Execute(null);
        Assert.Equal("未命名程序", pg.Sel!.Name);
        // 用户 2026-11-19：新建程序是空的 —— 只有固定在最后的「结束」（模板在程序库里另有）
        Assert.Equal(new[] { StepType.End }, pg.Sel.Steps.Select(x => x.Type));
        // 记录页「再做一次」载入的程序是旧格式转换来的：提示先检查
        Assert.True(pg.AcceptRedo(new ParallelReactor.Control.Pages.Records.RedoRequest(ParallelReactor.Control.Pages.Records.RedoKind.Program, 1, "S1", "低温氧化 · 60 ℃", "OX", new[] { 5 }, Array.Empty<int>()), out var toast));
        Assert.Contains("旧格式、已按新步骤转换，请先检查", toast);
        Assert.True(pg.One.Migrated);
        s.Dispose();
    }

    /// <summary>v0.8 的 PUT 响应：result.steps 是 HMI 转换后的步骤数组（台账记它）；较早的 HMI 给步数（数字）→ 不用。</summary>
    [Fact]
    public void PutResult_ConvertedSteps()
    {
        using var v08 = System.Text.Json.JsonDocument.Parse("{\"steps\":[{\"type\":\"hold\",\"params\":{\"until\":\"time\",\"time\":5},\"name\":\"保持\",\"detail\":\"时间 5 min\"},{\"type\":\"end\",\"params\":{}}],\"migrated\":true}");
        var d = ParallelReactor.Control.Services.LiveDeviceSource.ConvertedSteps(v08.RootElement, "x")!;
        Assert.Equal("x", d.Name);
        Assert.Equal(new[] { "hold", "end" }, d.Steps.Select(x => x.Type));
        Assert.Equal(5, d.Steps[0].Params["time"].GetDouble());
        using var old = System.Text.Json.JsonDocument.Parse("{\"steps\":4}");
        Assert.Null(ParallelReactor.Control.Services.LiveDeviceSource.ConvertedSteps(old.RootElement, "x"));
        Assert.Null(ParallelReactor.Control.Services.LiveDeviceSource.ConvertedSteps(null, "x"));
    }

    [Fact]
    public void Library_FilterAndScope()
    {
        var pg = Page(Shell("pgm=one"));
        Assert.Equal("5 个", pg.LibCount);
        pg.LibScope = "all";
        Assert.Equal("7 个", pg.LibCount);
        pg.LibQuery = "加氢";
        Assert.Equal(new[] { "H1", "B" }, pg.Lib.Select(x => x.Id));
        pg.LibQuery = "不存在";
        Assert.True(pg.LibEmpty);
    }

    [Fact]
    public void ModeSwitch_ChangesTools()
    {
        var pg = Page(Shell("pgm=one"));
        Assert.Equal(new[] { "程序", "文件", "运行" }, pg.Tools.Select(t => t.Title));
        pg.ModeKey = "mx";
        Assert.True(pg.IsMatrix);
        Assert.Equal("H1", pg.SelId);
        Assert.Equal(new[] { "方案", "文件", "运行" }, pg.Tools.Select(t => t.Title));
    }

    [Fact]
    public void CheckDialog_BothModes()
    {
        var s = Shell("pgm=one&dialog=pgcheck");
        var dlg = Assert.IsType<PreflightDialogVm>(s.Dialogs.Current);
        Assert.Equal("预运行检查", dlg.Title);
        Assert.Equal(new[] { "急停", "控制模式", "温控", "阀门", "气路", "搅拌器", "压力 / 超压监视", "报警通道", "程序", "目标通道", "溶液体积" }, dlg.Rows.Select(r => r.Title));
        Assert.Equal("全部通过 · 可以下发", dlg.Summary);
        Assert.Null(dlg.GoButton.Reason);
        Assert.Equal("RV4 报警中 · 已排除在目标外", dlg.Rows.First(r => r.Title == "报警通道").Detail);
        Assert.Equal("所选各路 5.0 mL · 吸气量由设备按压降计算", dlg.Rows.Last().Detail);

        var m = Shell("pgm=mx&dialog=pgcheck");
        var md = Assert.IsType<PreflightDialogVm>(m.Dialogs.Current);
        Assert.Equal("预运行检查 · 条件矩阵", md.Title);
        Assert.Equal(new[] { "急停", "控制模式", "模板", "参加通道", "每路参数", "溶液体积", "气路", "搅拌", "会话" }, md.Rows.Select(r => r.Title));
        Assert.Equal(CheckKind.No, md.Rows[3].Kind);
        Assert.Equal("RV1、RV2、RV3、RV4、RV6、RV8 正在运行", md.Rows[3].Detail);
        Assert.Equal("1 处超出范围", md.Rows[4].Detail);
        Assert.Equal("气体 A · 7 路依次升压，最后一路晚约 18 min", md.Rows[6].Detail);
        Assert.Equal("下发后开始「催化剂 × 温度筛选 · 批次 8」", md.Rows[8].Detail);
        Assert.Equal("有未通过的项，处理后再下发", md.GoButton.Reason);
    }

    // ———————————————————— 本机存储 ————————————————————

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "pr-f6-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void LibraryStore_MigratesDrafts_ThenSaves()
    {
        if (ControlConfig.WritesDisabledByEnvironment) return;
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "drafts.json"),
                "{ \"3\": { \"name\": \"加氢测试-03\", \"program\": " + LegacyDocs.Four("x").ToJson() + " } }");
            var st = new ProgramLibraryStore(demo: false, firstModel: "PR8", dir: dir);
            Assert.Equal(1, st.Migrated);
            var p = Assert.Single(st.Programs);
            Assert.Equal("RV3 草稿 · 加氢测试-03", p.Name);
            Assert.True(p.Draft);
            // 旧草稿读进来就按 STEPS-SPEC §3 转换（结束 · 排空 · 先降温至 30 → 保持 · 温度 ≤ 30 → 放空 → 结束）
            Assert.True(p.Migrated);
            Assert.Equal(new[] { StepType.Heat, StepType.Press, StepType.React, StepType.Hold, StepType.Vent, StepType.End }, p.Steps.Select(x => x.Type));
            Assert.Equal(Math.Round(150 / 145.0377, 2), p.Steps[1].N("pres"));
            Assert.Null(st.Save());
            var again = new ProgramLibraryStore(demo: false, firstModel: "PR8", dir: dir);
            Assert.Equal(0, again.Migrated);
            Assert.Equal("RV3 草稿 · 加氢测试-03", Assert.Single(again.Programs).Name);
            Assert.False(again.Programs[0].Migrated);                                       // 保存时已按新格式写盘
            Assert.DoesNotContain("\"endBy\"", File.ReadAllText(Path.Combine(dir, ProgramLibraryStore.FileName)));
            // 坏文件：不覆盖，从示例开始并说明
            File.WriteAllText(Path.Combine(dir, ProgramLibraryStore.FileName), "{ bad");
            var bad = new ProgramLibraryStore(demo: false, firstModel: "PR8", dir: dir);
            Assert.NotNull(bad.LoadError);
            Assert.Equal(new[] { "加氢筛选", "只做气密", "惰性气氛加热" }, bad.Programs.Select(x => x.Name));   // 设计稿的三个推荐模板
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ExperimentStore_SavesPlansAndRuns()
    {
        if (ControlConfig.WritesDisabledByEnvironment) return;
        var dir = TempDir();
        try
        {
            var chs = Enumerable.Range(1, 8).Select(n => new MxChannel(n, n == 7, false, null, 5)).ToArray();
            var tpl = DemoPrograms.Library().First(p => p.Id == "H1");
            var m = MatrixPlan.Build(tpl, 1, chs, "批次 9");
            m.Samples[2] = "Pd/C 5%";
            var id = Guid.NewGuid();
            var st = new ExperimentStore(memoryOnly: false, dir: dir);
            Assert.Null(st.AddRun(id, "1号机", m, new[] { 1, 2 }, new[] { 2 }, new DateTime(2026, 11, 15, 14, 31, 0)));
            var re = new ExperimentStore(memoryOnly: false, dir: dir);
            Assert.Equal(1, re.RunCount);
            var back = re.Plan(id, tpl, 1, chs)!;
            Assert.Equal("批次 9", back.Name);
            Assert.Equal("Pd/C 5%", back.Samples[2]);
            Assert.Null(re.Plan(Guid.NewGuid(), tpl, 1, chs));
            var json = File.ReadAllText(Path.Combine(dir, ExperimentStore.FileName));
            Assert.Contains("\"sent\":[2]", json.Replace(" ", "").Replace("\n", "").Replace("\r", ""));
        }
        finally { Directory.Delete(dir, true); }
    }
}
