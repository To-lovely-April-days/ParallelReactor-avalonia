using System;
using System.IO;
using System.Linq;
using Avalonia;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Programs.Editor;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages.Programs;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;
using ParallelReactor.Models;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 模板库（用户 2026-11-23「按 A 做」，设计稿 research/fusion-port/tpl-design）：「新建程序」开窗口 —— 空白 / 内置模板 / 我的模板，预览步骤，填名字后创建；
/// 「存为模板」把程序存进我的模板；我的模板能改名、删除（内置的不能）；程序库为空时中间给两个入口；右键删除程序（先确认，运行中的不能删）；
/// 本机存储 templates.json；程序库第一次打开不再放内置程序。数据 = 融合版演示数据（我的模板两个：U1 来自 H1、U2 来自 A）。
/// </summary>
public class TemplateLibraryTests
{
    private static ShellVm Shell(string query = "pgm=one")
    {
        var p = DemoParams.Parse("fusion=1&freeze=1");
        var s = new ShellVm(FleetVm.CreateDemo(p));
        s.Toasts.Frozen = true;
        s.Layout.Update(new Size(1920, 1080));
        s.ApplyDemoParams(DemoParams.Parse("fusion=1&freeze=1&page=prog&dev=1&apop=0" + (query.Length > 0 ? "&" + query : "")));
        return s;
    }

    private static ProgramPageVm Page(ShellVm s) => Assert.IsType<ProgramPageVm>(s.Page);
    private static RibbonTool Tool(ProgramPageVm pg, string label) => pg.Tools.SelectMany(g => g.Tools).First(t => t.Label == label);
    private static string LastToast(ShellVm s) => s.Toasts.Items.LastOrDefault()?.Text ?? "";

    private static NewProgramDialogVm OpenNew(ShellVm s)
    {
        Tool(Page(s), "新建程序").Command.Execute(null);
        return Assert.IsType<NewProgramDialogVm>(s.Dialogs.Current);
    }

    [Fact]
    public void NewDialog_ListsBlankBuiltInAndMine_PreviewsSteps()
    {
        var s = Shell();
        var dlg = OpenNew(s);
        Assert.Equal(("新建程序", 1080.0), (dlg.Title, dlg.Width));
        Assert.Equal("1号机 · HT-PR8 · 选一个模板作为起点，或者从空白开始", dlg.Sub);
        Assert.Equal(new[] { "加氢筛选", "只做气密", "惰性气氛加热", "吸气量验证" }, dlg.BuiltIns.Select(x => x.Name));
        Assert.Equal(new[] { "催化加氢 · 120 ℃ 标准流程", "平行合成 · 150 ℃" }, dlg.Mine.Select(x => x.Name));
        Assert.Equal(("4 个", "2 个", false), (dlg.BuiltInCount, dlg.MineCount, dlg.MineEmpty));
        Assert.Equal("9 步 · 气密 → 置换 → 升温 → 充压 → 反应", dlg.BuiltIns[0].Sub);
        Assert.Equal("9 步 · 张工 · 11-12 存入", dlg.Mine[0].Sub);
        // 默认选第一个内置模板：预览 9 步，程序名跟着模板名
        Assert.Same(dlg.BuiltIns[0], dlg.Selected);
        Assert.True(dlg.IsTemplate);
        Assert.False(dlg.IsOwn);
        Assert.False(dlg.ShowManage);
        Assert.Equal(("加氢筛选", "内置模板", "9 步", "适用 HT-PR8"), (dlg.PvTitle, dlg.KindText, dlg.StepsText, dlg.ModelText));
        Assert.StartsWith("约 ", dlg.DurText);
        Assert.Equal(9, dlg.Steps.Count);
        Assert.Equal(("01", "气密检查", "试验压力", "3.00 MPa"), (dlg.Steps[0].No, dlg.Steps[0].Name, dlg.Steps[0].K1, dlg.Steps[0].V1));
        Assert.Equal("结束", dlg.Steps[^1].Name);
        Assert.Equal("加氢筛选", dlg.ProgName);
        Assert.False(dlg.HasBy);
        // 换一个：预览和名字都跟着换（没手改过名字）
        dlg.BuiltIns[3].PickCommand.Execute(null);
        Assert.Equal(("吸气量验证", 6, "吸气量验证"), (dlg.PvTitle, dlg.Steps.Count, dlg.ProgName));
        Assert.True(dlg.BuiltIns[3].IsSelected);
        Assert.False(dlg.BuiltIns[0].IsSelected);
        // 空白
        dlg.Blank.PickCommand.Execute(null);
        Assert.True(dlg.IsBlank);
        Assert.Empty(dlg.Steps);
        Assert.Equal("未命名程序", dlg.ProgName);
        // 我的模板：有「改名」「删除」，下面写谁存的、来自哪个程序
        dlg.Mine[0].PickCommand.Execute(null);
        Assert.True(dlg.IsOwn);
        Assert.True(dlg.ShowManage);
        Assert.Equal(("我的模板", "Pd/C 体系常用的温度和压力"), (dlg.KindText, dlg.PvNote));
        Assert.Equal("张工 · 11-12 存入 · 来自程序「催化加氢筛选 · 120 ℃」", dlg.ByText);
        Assert.Equal("目标温度", dlg.Steps[4].K1);
        Assert.StartsWith("120", dlg.Steps[4].V1);
    }

    [Fact]
    public void Create_FromTemplate_AddsIndependentDraft()
    {
        var s = Shell("pgm=mx");
        var pg = Page(s);
        int before = pg.Store.Programs.Count;
        pg.OpenNew();                                        // 条件矩阵的工具条没有「新建程序」：从程序页直接开
        var dlg = Assert.IsType<NewProgramDialogVm>(s.Dialogs.Current);
        dlg.ProgName = "我的加氢 80";
        dlg.BuiltIns[1].PickCommand.Execute(null);          // 手改过名字之后换模板：名字不再跟着变
        Assert.Equal("我的加氢 80", dlg.ProgName);
        dlg.BuiltIns[0].PickCommand.Execute(null);
        dlg.CreateCommand.Execute(null);
        Assert.Null(s.Dialogs.Current);
        Assert.Equal(before + 1, pg.Store.Programs.Count);
        var p = pg.Sel!;
        Assert.Same(pg.Store.Programs[0], p);
        Assert.Equal(("我的加氢 80", "PR8", true, 9), (p.Name, p.Model, p.Draft, p.Steps.Count));
        Assert.StartsWith("N", p.Id);
        Assert.True(pg.IsDirty(p.Id));
        Assert.True(pg.IsOne);                               // 从条件矩阵切到单路程序去改
        Assert.Equal("已用模板「加氢筛选」新建「我的加氢 80」（9 步）· 检查参数后保存", LastToast(s));
        // 改程序不影响模板
        p.Steps[4].Num["temp"] = 150;
        Assert.Equal(80.0, pg.Templates.For("PR8")[0].Program.Steps[4].N("temp"));
        // 名字为空：不创建，写原因
        var again = OpenNew(s);
        again.ProgName = "  ";
        Assert.Equal("请填程序名称", again.NameError);
        again.CreateCommand.Execute(null);
        Assert.Same(again, s.Dialogs.Current);
        Assert.Equal(before + 1, pg.Store.Programs.Count);
        // 同名的默认名加序号
        var third = OpenNew(s);
        third.Mine[0].PickCommand.Execute(null);
        Assert.Equal("催化加氢 · 120 ℃ 标准流程", third.ProgName);
        third.CreateCommand.Execute(null);
        var fourth = OpenNew(s);
        fourth.Mine[0].PickCommand.Execute(null);
        Assert.Equal("催化加氢 · 120 ℃ 标准流程 2", fourth.ProgName);
    }

    [Fact]
    public void SaveAsTemplate_ThenRenameAndDelete()
    {
        var s = Shell("pgm=one&pgp=IH");
        var pg = Page(s);
        Tool(pg, "存为模板").Command.Execute(null);
        var save = Assert.IsType<SaveTemplateDialogVm>(s.Dialogs.Current);
        Assert.Equal(("存为模板", "把「惰性气氛加热」的步骤和参数存进模板库", "HT-PR8"), (save.Title, save.Sub, save.ModelText));
        Assert.Equal("惰性气氛加热 2", save.TplName);        // 内置模板里已有「惰性气氛加热」：默认名加序号
        save.TplName = "加氢筛选";
        Assert.Equal("已有同名模板 · 换一个名字", save.Error);
        save.SaveCommand.Execute(null);
        Assert.Same(save, s.Dialogs.Current);
        Assert.Equal(2, pg.Templates.Mine.Count);
        save.TplName = "惰性加热 · 标准";
        save.Note = "  常用  ";
        Assert.False(save.HasError);
        save.SaveCommand.Execute(null);
        Assert.Null(s.Dialogs.Current);
        Assert.Equal("已存为模板「惰性加热 · 标准」· 新建程序时可以选它", LastToast(s));
        var t = pg.Templates.Mine[0];
        Assert.Equal(("惰性加热 · 标准", "常用", "惰性气氛加热", 7, false), (t.Name, t.Note, t.From, t.Program.Steps.Count, t.BuiltIn));
        // 新建窗口里能看到它；改名（重名不行）、删除（先确认）
        var dlg = OpenNew(s);
        Assert.Equal("惰性加热 · 标准", dlg.Mine[0].Name);
        dlg.Mine[0].PickCommand.Execute(null);
        dlg.StartRenameCommand.Execute(null);
        Assert.True(dlg.IsRenaming);
        Assert.False(dlg.ShowManage);
        dlg.RenameText = "只做气密";
        Assert.Equal("已有同名模板 · 换一个名字", dlg.RenameError);
        dlg.CommitRenameCommand.Execute(null);
        Assert.True(dlg.IsRenaming);
        dlg.RenameText = "惰性加热 · 120";
        dlg.CommitRenameCommand.Execute(null);
        Assert.False(dlg.IsRenaming);
        Assert.Equal(("惰性加热 · 120", "惰性加热 · 120", "惰性加热 · 120"), (t.Name, dlg.Mine[0].Name, dlg.PvTitle));
        Assert.Equal("惰性加热 · 120", dlg.ProgName);         // 没手改过程序名：跟着模板的新名字
        dlg.StartDeleteCommand.Execute(null);
        Assert.True(dlg.IsDeleting);
        dlg.CancelDeleteCommand.Execute(null);
        Assert.Equal(3, pg.Templates.Mine.Count);
        dlg.StartDeleteCommand.Execute(null);
        dlg.ConfirmDeleteCommand.Execute(null);
        Assert.Equal(2, pg.Templates.Mine.Count);
        Assert.Equal(new[] { "催化加氢 · 120 ℃ 标准流程", "平行合成 · 150 ℃" }, dlg.Mine.Select(x => x.Name));
        Assert.Same(dlg.Mine[0], dlg.Selected);
        Assert.Equal("已删除模板「惰性加热 · 120」· 用它建过的程序不受影响", LastToast(s));
        // 内置模板没有改名 / 删除
        dlg.BuiltIns[0].PickCommand.Execute(null);
        dlg.StartRenameCommand.Execute(null);
        Assert.False(dlg.IsRenaming);
        dlg.ConfirmDeleteCommand.Execute(null);
        Assert.Equal(4, dlg.BuiltIns.Count);
    }

    [Fact]
    public void EmptyLibrary_ShowsTwoEntries()
    {
        var s = Shell("pgm=one&pgempty=1");
        var pg = Page(s);
        Assert.True(pg.NoPrograms);
        Assert.False(pg.ShowOne);
        Assert.False(pg.ShowMatrix);
        Assert.Equal(("0 个", "还没有程序"), (pg.LibCount, pg.LibEmptyText));
        Assert.Null(pg.Sel);
        Assert.Equal("还没有程序 · 先新建一个", Tool(pg, "下发并运行").Reason);
        Assert.Equal("先选一个程序", Tool(pg, "存为模板").Reason);
        Assert.Null(Tool(pg, "新建程序").Reason);
        pg.NewFromTemplateCommand.Execute(null);
        Assert.IsType<NewProgramDialogVm>(s.Dialogs.Current);
        s.Dialogs.Close();
        pg.NewBlankCommand.Execute(null);
        Assert.False(pg.NoPrograms);
        Assert.True(pg.ShowOne);
        Assert.Equal(("未命名程序", 1), (pg.Sel!.Name, pg.Sel.Steps.Count));
        Assert.StartsWith("已新建空程序", LastToast(s));
    }

    [Fact]
    public void DeleteProgram_Confirms_RunningOnesCannot()
    {
        var s = Shell("pgm=one&pgp=IH");
        var pg = Page(s);
        int before = pg.Store.Programs.Count;
        var row = pg.Lib.First(x => x.Id == "IH");
        Assert.True(row.CanDelete);
        pg.LibDeleteCommand.Execute(row);
        var dlg = Assert.IsType<DeleteProgramDialogVm>(s.Dialogs.Current);
        Assert.Equal("删除程序「惰性气氛加热」？", dlg.Title);
        Assert.Equal(before, pg.Store.Programs.Count);       // 还没确认
        dlg.DeleteCommand.Execute(null);
        Assert.Null(s.Dialogs.Current);
        Assert.Equal(before - 1, pg.Store.Programs.Count);
        Assert.Null(pg.Store.Find("IH"));
        Assert.NotNull(pg.Sel);
        Assert.Equal("PR8", pg.Sel!.Model);
        Assert.Equal("已删除程序「惰性气氛加热」", LastToast(s));
        // 正在运行的（平行合成程序 A，1号机 6 路在跑）：右键项灰掉，硬点也不开确认
        var run = pg.Lib.First(x => x.Id == "A");
        Assert.Equal("运行中的程序不能删除", run.DeleteReason);
        pg.LibDeleteCommand.Execute(run);
        Assert.Null(s.Dialogs.Current);
        Assert.NotNull(pg.Store.Find("A"));
        // 右键「复制为新程序」「存为模板」先选中那一行
        pg.LibTemplateCommand.Execute(pg.Lib.First(x => x.Id == "H1"));
        Assert.Equal("H1", pg.SelId);
        Assert.IsType<SaveTemplateDialogVm>(s.Dialogs.Current);
    }

    [Fact]
    public void TemplateStore_RoundTrips_AndLibraryStartsEmpty()
    {
        if (ControlConfig.WritesDisabledByEnvironment) return;
        var dir = Path.Combine(Path.GetTempPath(), "pr-tpl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // 新装的软件：程序库是空的，我的模板也是空的；内置模板按型号给
            Assert.Empty(new ProgramLibraryStore(demo: false, firstModel: "PR8", dir: dir).Programs);
            var st = new ProgramTemplateStore(demo: false, dir: dir);
            Assert.Empty(st.Mine);
            Assert.Null(st.LoadError);
            Assert.Equal(new[] { "T1", "T2", "T3", "T4" }, st.For("PR16").Select(t => t.Id));
            Assert.All(st.For("PR16"), t => Assert.Equal("PR16", t.Model));
            Assert.False(File.Exists(st.FilePath));
            var src = DemoPrograms.Library().First(p => p.Id == "H1");
            var t1 = PcTemplate.FromProgram(src, st.NewId(), "加氢 120", "说明文字", new DateTime(2026, 11, 23, 10, 0, 0), "张工");
            Assert.Null(st.Add(t1));
            Assert.Contains("\"templates\"", File.ReadAllText(st.FilePath));
            var re = new ProgramTemplateStore(demo: false, dir: dir);
            var back = Assert.Single(re.Mine);
            Assert.Equal((t1.Id, "加氢 120", "说明文字", src.Name, "PR8", "张工", 9, false), (back.Id, back.Name, back.Note, back.From, back.Model, back.Program.By, back.Program.Steps.Count, back.BuiltIn));
            Assert.Equal(120.0, back.Program.Steps[4].N("temp"));
            Assert.Equal(3.0, back.Program.Steps[5].N("pres"));
            Assert.Equal(5, re.For("PR8").Count);            // 4 个内置 + 1 个自己的
            Assert.Equal(4, re.For("PR4").Count);            // 别的型号看不到这个模板
            Assert.Null(re.Rename(back, " 加氢 120 标准 "));
            Assert.Equal("加氢 120 标准", new ProgramTemplateStore(false, dir).Mine[0].Name);
            Assert.Null(re.Remove(back));
            Assert.Empty(new ProgramTemplateStore(false, dir).Mine);
            // 坏文件：当作没有，不覆盖，说明原因
            File.WriteAllText(st.FilePath, "{ bad");
            var bad = new ProgramTemplateStore(false, dir);
            Assert.Empty(bad.Mine);
            Assert.NotNull(bad.LoadError);
            Assert.Equal("{ bad", File.ReadAllText(st.FilePath));
            // 演示：两个示例模板，不写文件
            var demoDir = Path.Combine(dir, "demo");
            var demo = new ProgramTemplateStore(demo: true, dir: demoDir);
            Assert.Equal(new[] { "U1", "U2" }, demo.Mine.Select(t => t.Id));
            Assert.Null(demo.Add(t1));
            Assert.False(Directory.Exists(demoDir));
            demo.ResetDemo();
            Assert.Equal(2, demo.Mine.Count);
        }
        finally { Directory.Delete(dir, true); }
    }
}
