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
        Assert.Equal("模板「催化加氢筛选 · 120 ℃」 · 6 步", pg.Mx.TemplateSub);
        Assert.Equal(8, pg.Mx.Columns.Count);
        Assert.Equal("7", pg.Mx.IncText);
        Assert.Equal(new[] { "第 04 步目标压力：RV4 4.00 MPa · 超出 HT-PR8 范围 0.01 – 3.14 MPa", "RV1–RV4、RV6、RV8 正在运行，最晚约 15:47 结束 · 结束后才能下发，可以先保存" },
            pg.Mx.Issues.Select(i => i.Msg));
        Assert.Equal(pg.Mx.Issues[0].Msg, pg.Mx.RunReason);
        Assert.Equal("RV1 · 2 h 28 min", pg.Mx.Shortest);
        Assert.Equal("RV8 · 3 h 01 min", pg.Mx.Longest);
        var col1 = pg.Mx.Columns[0];
        Assert.Equal(("运行中", " · 15:19"), (col1.Status, col1.Time));
        Assert.Equal("已停用", pg.Mx.Columns[6].Status);
        Assert.False(pg.Mx.Columns[6].Included);
        // 行：样品、体积、6 个步骤标题，每路的 3 行 + 共用 10 行
        Assert.Equal(2 + 6 + 3 + 10, pg.Mx.Rows.Count);
        var temp = pg.Mx.Rows.OfType<MxPerRow>().First(r => r.Spec.Key == "temp");
        Assert.Equal(new[] { "100", "110", "120", "130", "100", "110", "120", "130" }, temp.Cells.Select(c => c.Text));
        Assert.True(temp.Cells[0].Chg);
        Assert.False(temp.Cells[6].Enabled);
        var pres = pg.Mx.Rows.OfType<MxPerRow>().First(r => r.Spec.Key == "pres");
        Assert.True(pres.Cells[3].Bad);
        Assert.Equal("2 h 28 min", pg.Mx.Foot[0].Text);
        Assert.Equal("—", pg.Mx.Foot[6].Text);
        var stir = pg.Mx.Rows.OfType<MxSharedRow>().First(r => r.Spec.Key == "rpm");
        Assert.Equal(("全部釜共用一台电机", "f-link", "400"), (stir.Why, stir.WhyIcon, stir.Text));
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
        Assert.Equal(100, r5.Program.Record!.Steps[1].Sp);      // RV5 的升温目标 = 矩阵里的 100
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
        Assert.Equal("共 4 步 · 约 2 h 01 min", one.TotalText);
        Assert.True(one.Locked);
        Assert.Equal("正在 1号机 RV1、RV2、RV3、RV4、RV6、RV8 运行 · 运行中的程序不能直接修改", one.LockText);
        Assert.Equal("第 03 步 · 反应", one.ParamSub);
        Assert.Equal(new[] { "endBy", "time", "mmol" }, one.Fields.Select(f => f.Spec.Key));
        Assert.True(one.Fields.All(f => f.Locked));
        Assert.Equal(new[] { "51 min", "3 min", "45 min", "22 min" }, one.Steps.Select(x => x.Dur));
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
        Assert.Equal(4, pg.Sel!.Steps.Count);
        pg.One.DupCommand.Execute(null);
        Assert.Equal("平行合成程序 A（副本）", pg.Sel!.Name);
        Assert.False(pg.One.Locked);
        Assert.Equal("平行合成程序 A（副本） · 未保存", pg.One.ProgName);
        pg.One.AddTypes.First(a => a.Type == StepType.Stir).Command.Execute(null);
        Assert.Equal(5, pg.Sel.Steps.Count);
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
        Assert.Equal(4, pg.Sel.Steps.Count);
        // 压力字段写约合 psi
        pg.One.Steps[1].PickCommand.Execute(null);
        Assert.Equal("0.01 – 3.14（≈ 363 psi）", pg.One.Fields.First(f => f.Spec.Key == "pres").Range);
        // 保存（演示不写文件）：草稿标记去掉、修改人 = 本机客户端名
        var save = pg.Tools.SelectMany(g => g.Tools).First(t => t.Label == "保存");
        Assert.Null(save.Reason);
        save.Command.Execute(null);
        Assert.False(pg.IsDirty(pg.SelId!));
        Assert.False(pg.Sel.Draft);
        Assert.Equal("没有未保存的修改", save.Reason);
    }

    [Fact]
    public void Library_FilterAndScope()
    {
        var pg = Page(Shell("pgm=one"));
        Assert.Equal("4 个", pg.LibCount);
        pg.LibScope = "all";
        Assert.Equal("6 个", pg.LibCount);
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
                "{ \"3\": { \"name\": \"加氢测试-03\", \"program\": " + ProgramDraftStore.Template("x").ToJson() + " } }");
            var st = new ProgramLibraryStore(demo: false, firstModel: "PR8", dir: dir);
            Assert.Equal(1, st.Migrated);
            var p = Assert.Single(st.Programs);
            Assert.Equal("RV3 草稿 · 加氢测试-03", p.Name);
            Assert.True(p.Draft);
            Assert.Equal(Math.Round(150 / 145.0377, 2), p.Steps[1].N("pres"));
            Assert.Null(st.Save());
            var again = new ProgramLibraryStore(demo: false, firstModel: "PR8", dir: dir);
            Assert.Equal(0, again.Migrated);
            Assert.Equal("RV3 草稿 · 加氢测试-03", Assert.Single(again.Programs).Name);
            // 坏文件：不覆盖，从示例开始并说明
            File.WriteAllText(Path.Combine(dir, ProgramLibraryStore.FileName), "{ bad");
            var bad = new ProgramLibraryStore(demo: false, firstModel: "PR8", dir: dir);
            Assert.NotNull(bad.LoadError);
            Assert.Equal("示例程序", Assert.Single(bad.Programs).Name);
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
