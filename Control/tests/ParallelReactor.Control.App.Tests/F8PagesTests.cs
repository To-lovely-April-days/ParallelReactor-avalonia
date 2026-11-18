using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using ParallelReactor.Client;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.History;
using ParallelReactor.Control.Core.Unattended;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Pages.Home;
using ParallelReactor.Control.Pages.Perf;
using ParallelReactor.Control.Pages.Programs;
using ParallelReactor.Control.Pages.Records;
using ParallelReactor.Control.Pages.Settings;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Tests.Fakes;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 融合版 F8：设置页（各节、本机设置的保存与生效、设备参数只读、PID 写入、吸气量计算、导出配置）、无人值守（防睡眠 / 报警声音接到外壳、补拉）、
/// 性能测试页（演示状态、没有测试计划、泄漏测试、阈值）、记录页「再做一次」接到程序页 / 性能测试页、共用搅拌开关在本地只读时禁用、深蓝条的落点。
/// 数据 = 融合版演示数据（fusion=1，2026-11-15 14:30:07）；补拉用假连接（LiveFleet）。
/// </summary>
public class F8PagesTests
{
    private sealed class FakeInhibitor : ISleepInhibitor
    {
        public bool Holding;
        public string Name => "假的";
        public string? LastError => null;
        public bool Acquire() { Holding = true; return true; }
        public void Release() => Holding = false;
    }

    private sealed class FakeSound : IAlarmSound
    {
        public int Plays;
        public void Play() => Plays++;
    }

    private static ShellVm Shell(string query = "page=set&dev=1", ISleepInhibitor? sleep = null, IAlarmSound? sound = null, int w = 1920, int h = 1080)
    {
        var p = DemoParams.Parse("fusion=1&freeze=1");
        var s = new ShellVm(FleetVm.CreateDemo(p), sleep: sleep, sound: sound);
        s.Toasts.Frozen = true;
        s.Layout.Update(new Size(w, h));
        s.ApplyDemoParams(DemoParams.Parse("apop=0&" + query + "&" + p));
        return s;
    }

    private static SettingsPageVm Set(ShellVm s) => Assert.IsType<SettingsPageVm>(s.Page);
    private static PerfPageVm Perf(ShellVm s) => Assert.IsType<PerfPageVm>(s.Page);

    // ———————————————————— 设置页 ————————————————————

    [Fact]
    public void Settings_Sections_FromParams_AndNav()
    {
        var s = Shell("page=set&dev=1&set=night");
        var v = Set(s);
        Assert.True(v.IsNight);
        Assert.Equal(("无人值守", "本机"), (v.Title, v.Sub));
        Assert.Equal("night", v.Nav.Single(n => n.IsOn).Key);
        Assert.Contains(v.Nav, n => n.IsGroup && n.Label == "1号机 参数");
        Assert.Equal(new[] { "lim", "comm", "store" }, v.Nav.Where(n => n.ReadOnly).Select(n => n.Key).ToArray());
        v.Section = "upt";
        Assert.Equal(("吸气量计算", "1号机 · HT-PR8"), (v.Title, v.Sub));
        v.Section = "about";
        Assert.False(v.HasSub);
        v.Section = "nope";
        Assert.True(v.IsGen);
        // 截图前还原
        v.Section = "pid";
        v.ResetView();
        Assert.True(v.IsGen);
    }

    [Fact]
    public void Strip_SettingsAboutHelp_AndNetBanner_LandOnTheirSections()
    {
        var s = Shell("page=main&dev=1");
        s.SettingsCommand.Execute(null);
        Assert.Equal(AppPage.Settings, s.Nav.Page);
        Assert.True(Set(s).IsGen);
        s.AboutCommand.Execute(null);
        Assert.True(Set(s).IsAbout);
        Assert.Contains(Set(s).AboutRows, r => r.Label == "本软件");
        Assert.Contains(Set(s).Help, h => h.Title == "停止");
        s.Go(AppPage.Home);
        s.HelpCommand.Execute(null);
        Assert.True(Set(s).IsAbout);
        var b = new NetBannerVm(s);
        b.SettingsCommand.Execute(null);
        Assert.True(Set(s).IsNight);
    }

    [Fact]
    public void Night_PreventSleep_IsReal_AndTheStatusBarFollowsIt()
    {
        var os = new FakeInhibitor();
        var s = Shell("page=set&dev=1&set=night", sleep: os);
        var v = Set(s);
        Assert.True(os.Holding);                          // 1号机在跑：申请了
        Assert.True(s.Sleep.Held);
        Assert.Contains(s.Status.Extra, x => x.Text == StatusBarVm.SleepText);
        Assert.Equal("现在：已阻止睡眠（假的）", v.SleepState);
        v.PreventSleep = false;                            // 改了马上生效（样稿 SETP）
        Assert.False(os.Holding);
        Assert.DoesNotContain(s.Status.Extra, x => x.Text == StatusBarVm.SleepText);
        Assert.Equal("已关闭 · 电脑按系统设置睡眠", v.SleepState);
        Assert.True(s.Prefs.Dirty);
        Assert.Null(s.Ribbon.Groups.SelectMany(g => g.Tools).First(t => t.Label == "保存").Reason);
        s.Dispose();
        Assert.False(os.Holding);
    }

    [Fact]
    public void Night_LostTable_FromOnRemoteLost_ReadOnly()
    {
        var v = Set(Shell("page=set&dev=1&set=night"));
        Assert.Equal(new[] { ("1号机", "保持运行", true), ("2号机", "保持运行", true), ("3号机", "停止本机启动的加热", false) },
            v.Lost.Select(r => (r.Alias, r.Mode, r.Recommended)).ToArray());
        Assert.Equal("只停本机下发后启动的加热，其它照常", v.Lost[2].Note);
        Assert.Equal("自动连接，接上正在记录的会话", v.AfterRestart);
        v.AutoConnect = false;
        Assert.Equal("不自动连接（「常规 › 启动时自动连接」关着）", v.AfterRestart);
    }

    [Fact]
    public void Backfill_Setting_ReachesTheFleet_AndTheBanner()
    {
        var s = Shell("page=set&dev=1&set=night");
        var v = Set(s);
        Assert.True(s.Fleet.BackfillEnabled);
        v.Backfill = false;
        Assert.False(s.Fleet.BackfillEnabled);
        Assert.Contains("恢复后从最新数据接着显示", NetBannerText.LostBody("keep", null, 2, s.Prefs.Current.Backfill));
        Assert.Equal("已关闭 · 重连后从最新数据接着显示，断开那一段在曲线上空着", v.BackfillState);
    }

    [Fact]
    public void AlarmSound_PopToggle_And_SettingsAreOneSetting_AndItBeeps()
    {
        var snd = new FakeSound();
        var s = Shell("page=main&dev=1", sound: snd);
        Assert.Equal(1, snd.Plays);                       // 演示数据有 2 条未确认的报警级报警：马上响一次
        s.RefreshChrome();
        Assert.Equal(1, snd.Plays);                       // 30 s 之内不再响
        s.AlarmPop.SoundOn = false;                       // 弹窗上的开关：直接保存
        Assert.False(s.Prefs.Current.AlarmSound);
        Assert.False(s.Prefs.Saved.AlarmSound);
        Assert.False(s.Prefs.Dirty);
        s.Go(AppPage.Settings);
        Assert.False(Set(s).AlarmSound);
        Set(s).AlarmSound = true;
        Assert.True(s.AlarmPop.SoundOn);
        // 全部确认：不再响
        foreach (var d in s.Fleet.Devices) foreach (var a in d.Alarms.ToList()) a.AckCommand.Execute(null);
        s.Fleet.RefreshNow();
        int n = snd.Plays;
        s.RefreshChrome();
        Assert.Equal(n, snd.Plays);
    }

    [Fact]
    public void Prefs_SaveAndRestore_RoundTripThroughDevicesJson()
    {
        var cfg = ControlConfig.InMemory();
        cfg.Prefs = new PcPrefs(DataDir: "D:\\Data", AutoConnect: false, AlarmSound: false, PreventSleep: true, Backfill: false);
        var json = cfg.ToJson();
        using var doc = JsonDocument.Parse(json);
        var back = ControlConfig.ReadPrefs(doc.RootElement.GetProperty("pc"));
        Assert.Equal(cfg.Prefs, back);
        // 容错：压力单位只认 MPa / bar / psi（不分大小写，规范成界面写法），类型不对的用默认
        using var bad = JsonDocument.Parse("{\"pressureUnit\":\"kPa\",\"alarmSound\":\"x\",\"preventSleep\":false}");
        Assert.Equal(PcPrefs.Default with { PreventSleep = false }, ControlConfig.ReadPrefs(bad.RootElement));
        using var bar = JsonDocument.Parse("{\"pressureUnit\":\"bar\"}");
        Assert.Equal("bar", ControlConfig.ReadPrefs(bar.RootElement).PressureUnit);
        using var psi = JsonDocument.Parse("{\"pressureUnit\":\"PSI\"}");
        Assert.Equal("psi", ControlConfig.ReadPrefs(psi.RootElement).PressureUnit);

        var s = Shell();
        var v = Set(s);
        v.PreventSleep = false;
        Assert.True(v.IsDirty);
        s.Ribbon.Groups.SelectMany(g => g.Tools).First(t => t.Label == "保存").Command.Execute(null);
        Assert.False(v.IsDirty);
        Assert.Equal("没有未保存的修改", s.Ribbon.Groups.SelectMany(g => g.Tools).First(t => t.Label == "保存").Reason);
        Assert.Contains(s.Toasts.Items, t => t.Text.StartsWith("本机设置已保存", StringComparison.Ordinal));
        s.Ribbon.Groups.SelectMany(g => g.Tools).First(t => t.Label == "恢复默认").Command.Execute(null);
        Assert.True(s.Prefs.Current.PreventSleep);
        Assert.True(v.IsDirty);
    }

    [Fact]
    public void Gen_UnitHasThreeOptions_DataDirEmptyMeansDefault_DemoClientNameCannotChange()
    {
        var s = Shell();
        var v = Set(s);
        Assert.Equal("MPa", v.UnitKey);
        // 压力单位：MPa / bar / psi 三种都能选（切换的效果见 PressureUnitTests）
        Assert.Equal(new[] { "MPa", "bar", "psi" }, v.UnitOptions.Select(o => (string?)o.Value));
        Assert.All(v.UnitOptions, o => Assert.Null(o.Reason));
        Assert.Equal("", v.DataDirText);
        v.DataDirText = "/tmp/pr-data";
        Assert.Equal("/tmp/pr-data", s.Prefs.Current.DataDir);
        Assert.Equal("/tmp/pr-data", DataDirs.Configured);
        v.DataDirText = "";
        v.ClientNameText = "PC-别的";
        Assert.True(v.IsDirty);
        v.Save();
        Assert.Contains(s.Toasts.Items, t => t.Text == "演示数据不能改本机客户端名");
        Assert.Equal(s.Fleet.ClientName, v.ClientNameText);
    }

    [Fact]
    public void Conn_Table_TokenReveal_AutoConnect()
    {
        var v = Set(Shell("page=set&dev=1&set=conn"));
        Assert.Equal(3, v.Devices.Count);
        var r = v.Devices[0];
        Assert.Equal(("1号机", "HT-PR8", "192.168.1.21", "8080", SettingsTexts.TokenMask, "在线"), (r.Alias, r.Model, r.Host, r.Port, r.Token, r.State));
        Assert.Equal("3 台 · 全部在线", v.ConnSummary);
        r.EyeCommand.Execute(null);
        Assert.NotEqual(SettingsTexts.TokenMask, v.Devices[0].Token);
        Assert.Equal("隐藏令牌", v.Devices[0].EyeTip);
        bool before = v.Devices[1].AutoOn;
        v.Devices[1].AutoCommand.Execute(null);
        Assert.Equal(!before, v.Devices[1].AutoOn);
    }

    [Fact]
    public async Task Conn_TestAll_And_ExportConfig_HasNoTokens()
    {
        var s = Shell("page=set&dev=1&set=conn");
        var v = Set(s);
        await v.TestAllAsync();
        Assert.Contains(s.Toasts.Items, t => t.Text == "测试连接：3 台全部连通");
        var dir = Path.Combine(Path.GetTempPath(), "pr-f8-export-" + Guid.NewGuid().ToString("N")[..8]);
        var old = SettingsPageVm.ExportDir;
        SettingsPageVm.ExportDir = () => dir;
        try
        {
            var path = v.ExportConfig()!;
            var text = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(text);
            Assert.Equal(3, doc.RootElement.GetProperty("devices").GetArrayLength());
            Assert.False(text.Contains("token", StringComparison.OrdinalIgnoreCase));
            Assert.True(doc.RootElement.GetProperty("pc").GetProperty("preventSleep").GetBoolean());
            // 导入：本机设置读进来（不保存）
            File.WriteAllText(path, text.Replace("\"backfill\": true", "\"backfill\": false", StringComparison.Ordinal));
            await v.ImportConfigAsync(path);
            Assert.False(s.Prefs.Current.Backfill);
            Assert.True(s.Prefs.Dirty);
        }
        finally
        {
            SettingsPageVm.ExportDir = old;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Lim_ReadOnlyGroups_FromTheDeviceSettings()
    {
        var v = Set(Shell("page=set&dev=1&set=lim"));
        Assert.Null(v.NoSettings);
        Assert.Equal("设备参数只能在1号机触屏上修改 · 这里只读", v.LockText);
        Assert.Equal(new[] { "可设置参数范围", "报警阈值", "泄漏测试", "定值加热防超调", "搅拌电机 · 压力变送器" }, v.LimGroups.Select(g => g.Title).ToArray());
        SetRoRow Row(string l) => v.LimGroups.SelectMany(g => g.Rows).Single(r => r.Label == l);
        Assert.Equal(("450", "rpm", "· 釜里的转速"), (Row("搅拌转速上限").Value, Row("搅拌转速上限").Unit, Row("搅拌转速上限").Note));
        Assert.Equal("3.50 MPa（508 psi）", Row("超压报警").Value);
        Assert.Equal(("2.0", "psi/h"), (Row("泄漏率阈值").Value, Row("泄漏率阈值").Unit));
        Assert.Equal("（设备暂未使用）", Row("升温超时报警").Note);
        Assert.Equal("1.00 MPa（145 psi）", Row("测试压力").Value);
    }

    [Fact]
    public void Pid_ReadsBandPid_LocalIsLocked()
    {
        // 温控 / PID 现在读 GET /pid（演示：样稿 PIDS），自整定见 PidPageTests
        var s = Shell("page=set&dev=1&set=pid");
        var v = Set(s).Pid;
        Assert.Null(v.LockText);
        Assert.Equal(new[] { "低温档", "中温档", "高温档" }, v.Bands.Select(b => b.Name));
        Assert.Equal(1, v.Band);
        var r1 = v.Rows[0];
        Assert.Equal(("RV1", "13.0", "190", "32", true), (r1.Rv, r1.P, r1.I, r1.D, r1.CanEdit));
        Assert.Equal(("停用", false), (v.Rows.Single(r => r.N == 7).Status, v.Rows.Single(r => r.N == 7).CanEdit));
        s.SelectDevice(2);                                          // 2号机本地只读
        Assert.Equal("2号机为本地只读 · 申请控制权后可以改 PID 和自整定", v.LockText);
        Assert.All(v.Rows, r => Assert.False(r.CanEdit));
    }

    [Fact]
    public void Upt_VolumesFromSettings_SolutionFromTheSnapshot_TouchscreenCalibration()
    {
        var s = Shell("page=set&dev=1&set=upt");
        var v = Set(s);
        var r1 = v.UptRows[0];
        Assert.Equal(("RV1", "10", "0.7", "10.7", "5.0", "5.7", "± 0.002", "2026-11-10 已标定", true), (r1.Rv, r1.Vessel, r1.Line, r1.Eff, r1.Sol, r1.Gas, r1.Res, r1.Cal, r1.ShowTouch));
        Assert.Equal(("4.8", "6.0"), (v.UptRows[2].Gas, v.UptRows[2].Sol));     // RV3：10.8 − 6
        var r7 = v.UptRows.Single(r => r.Rv == "RV7");
        Assert.Equal(("—", "—", "停用", false), (r7.Sol, r7.Gas, r7.Cal, r7.ShowTouch));
        Assert.Equal("± 0.003", v.UptRows.Single(r => r.Rv == "RV8").Res);
        Assert.Equal(0.0023, SettingsTexts.ResolutionMmol(5.7), 4);
        s.SelectDevice(3);
        var r15 = v.UptRows.Single(r => r.Rv == "RV15");
        Assert.Equal("停用", r15.Cal);
        var r14 = v.UptRows.Single(r => r.Rv == "RV14");
        Assert.Equal("2026-10-28 已标定", r14.Cal);
        Assert.Contains("釜 25 mL", v.UptFormula);
    }

    // ———————————————————— 性能测试页 ————————————————————

    [Fact]
    public void Perf_Demo_LooksLikeTheMock_P1ManualEntry()
    {
        var s = Shell("page=test&dev=1");
        var p = Perf(s);
        Assert.True(p.PlanOk);
        PtItemVm It(string id) => p.Groups.SelectMany(g => g.Items).Single(i => i.Id == id);
        Assert.Equal(("done", "skip", "cur", "todo", "off"), (It("R0").State, It("F6").State, It("P1").State, It("P2").State, It("LT1").State));
        Assert.False(It("P1").CanCheck);
        Assert.Equal("0 准备", p.Groups[0].Title);
        Assert.StartsWith("已选 21 项 · 约 ", p.SelText);
        Assert.Equal(new[] { 1, 2, 3, 4 }, p.Chans.Where(c => c.IsOn).Select(c => c.N).ToArray());
        Assert.Equal(("会话 PT-20261115-1305", "运行中", "11 / 21 项"), (p.RunSub, p.RunTag, p.DoneText));
        Assert.Equal(("P1", "人工录入", 4), (p.CurId, p.CurKind, p.Entries.Count));
        Assert.Equal(("RV1", "本机 0.997", "1.003"), (p.Entries[0].Label, p.Entries[0].Sub, p.Entries[0].Text));
        Assert.Equal("先填完 4 路读数", p.ConfirmReason);
        p.Entries[2].Text = "1.004";
        p.Entries[3].Text = "1.010";
        Assert.Null(p.ConfirmReason);
        p.ConfirmCommand.Execute(null);
        Assert.Contains(s.Toasts.Items, t => t.Text.Contains("进入第 5 点", StringComparison.Ordinal));
        Assert.Equal(5, p.Kpis.Count);
        Assert.Equal(("搅拌（整台）", "302", "rpm"), (p.Kpis[0].K, p.Kpis[0].V, p.Kpis[0].U));
        Assert.Contains(p.Results, r => r.Setting == "450 rpm");
        Assert.DoesNotContain(p.Results, r => r.Setting.Contains("1000", StringComparison.Ordinal) || r.Setting.Contains("1500", StringComparison.Ordinal));
        var tools = s.Ribbon.Groups.SelectMany(g => g.Tools).ToList();
        Assert.Equal(new[] { "暂停", "跳过本步", "中止", "生成报告", "打开文件夹" }, tools.Select(t => t.Label).ToArray());
    }

    [Fact]
    public void Perf_OtherModels_HaveNoPlan_LeakTestStillWorks()
    {
        var s = Shell("page=test&dev=2");
        var p = Perf(s);
        Assert.False(p.PlanOk);
        Assert.Equal("2号机（HT-PR4）没有对应的测试计划", p.NoPlanTitle);
        Assert.Equal("测试计划只适用 HT-PR8 · 2号机 是 HT-PR4", s.Ribbon.Groups.SelectMany(g => g.Tools).First().Reason);
        p.ModeKey = "leak";
        Assert.Equal(new[] { "开始泄漏测试", "中止", "导出结果" }, s.Ribbon.Groups.SelectMany(g => g.Tools).Select(t => t.Label).ToArray());
        Assert.Equal("2号机为本地只读 · 先申请控制权", s.Ribbon.Groups.SelectMany(g => g.Tools).First(t => t.Label == "开始泄漏测试").Reason);
        Assert.Equal(4, p.LeakRows.Count);
    }

    [Fact]
    public void Leak_ParamsFromSettings_ThresholdIsTheDeviceLeakRate()
    {
        var s = Shell("page=test&dev=1&ptm=leak");
        var p = Perf(s);
        Assert.True(p.IsLeak);
        Assert.Equal(("测试压力", "泄漏率阈值", "2.0"), (p.LeakParams[0].Label, p.LeakParams[3].Label, p.LeakParams[3].Value));
        Assert.Equal(4, p.LeakPhases.Count);
        Assert.All(p.LeakPhases, ph => Assert.True(ph.Done));
        Assert.Equal("最近一次 · 11-10 16:00", p.LeakResSub);
        var rv3 = p.LeakRows.Single(r => r.Rv == "RV3");
        Assert.Equal(("2.60", "2.0", false, "不通过"), (rv3.Rate, rv3.Limit, rv3.Ok, rv3.Verdict));
        Assert.Equal("未测（停用）", p.LeakRows.Single(r => r.Rv == "RV7").Verdict);
        Assert.Equal("RV3 泄漏率超过阈值 · 检查釜盖密封和接头后重测", p.LeakWarn);
        // 通道：1号机此刻只有 RV5 空闲（其余在跑 / 报警 / 停用）
        Assert.Equal(new[] { 5 }, p.LeakChans.Where(c => c.IsOn).Select(c => c.N).ToArray());
        Assert.Equal("RV1正在运行 · 不能做泄漏测试", p.LeakChans[0].Reason);
        // 记录页的判据跟着同一个设置
        s.Go(AppPage.Records);
        Assert.Equal(2.0, Assert.IsType<RecordsPageVm>(s.Page).HoldLimitOf(1));
        // 汇总 JSON（HMI leak/<runId>-summary.json，字段名不分大小写）
        using var doc = JsonDocument.Parse("{\"runId\":\"leak-20261118-101500\",\"start\":\"2026-11-18T10:15:00\",\"channels\":[{\"Rv\":2,\"Status\":\"bad\",\"JudgedDropPsiPerH\":3.1,\"StartPsig\":145.2,\"EndPsig\":143.6},{\"Rv\":1,\"Status\":\"\",\"JudgedDropPsiPerH\":0.4}]}");
        var sum = PerfTexts.ParseLeak(doc.RootElement)!;
        Assert.Equal(("leak-20261118-101500", 2), (sum.RunId, sum.Channels.Count));
        Assert.Equal(1, sum.Channels[0].Rv);
        Assert.True(PerfTexts.Verdict(sum.Channels[0], 2.0));
        Assert.False(PerfTexts.Verdict(sum.Channels[1], 2.0));
        Assert.Equal(143.6, sum.Channels[1].EndPsi);
    }

    [Fact]
    public void Leak_ExportResults_Csv()
    {
        var s = Shell("page=test&dev=1&ptm=leak");
        var p = Perf(s);
        var dir = Path.Combine(Path.GetTempPath(), "pr-f8-leak-" + Guid.NewGuid().ToString("N")[..8]);
        var old = PerfPageVm.ExportDir;
        PerfPageVm.ExportDir = () => dir;
        try
        {
            var path = p.ExportLeak()!;
            var lines = File.ReadAllLines(path);
            Assert.Equal("通道,起始 psi,结束 psi,泄漏率 psi/h,阈值 psi/h,结论", lines[0].TrimStart('\uFEFF'));
            Assert.Contains("RV3,145.3,144.0,2.60,2.0,不通过", lines);
        }
        finally
        {
            PerfPageVm.ExportDir = old;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ———————————————————— 再做一次（记录页 → 程序页 / 性能测试页） ————————————————————

    [Fact]
    public void Redo_Program_LoadsTheProgram_AndPreselectsIdleChannels()
    {
        var s = Shell("page=rec&dev=1&rc=PT-20261114-0915");
        var r = Assert.IsType<RecordsPageVm>(s.Page);
        r.RedoCommand.Execute(null);
        var pg = Assert.IsType<ProgramPageVm>(s.Page);
        Assert.Equal(("one", "A"), (pg.ModeKey, pg.SelId));
        Assert.Equal(new[] { 5 }, pg.One.SelectedChannels.ToArray());
        Assert.Contains(s.Toasts.Items, t => t.Text.StartsWith("已载入「平行合成程序 A」· 预选 RV5", StringComparison.Ordinal));
    }

    [Fact]
    public void Redo_Leak_OpensTheLeakTest_WithTheSessionsIdleChannels()
    {
        var s = Shell("page=rec&dev=0&rc=LK-20261115-0912");
        Assert.IsType<RecordsPageVm>(s.Page).RedoCommand.Execute(null);
        var p = Perf(s);
        Assert.True(p.IsLeak);
        Assert.Equal(3, s.Nav.DeviceK);
        var on = p.LeakChans.Where(c => c.IsOn).Select(c => c.N).ToArray();
        Assert.NotEmpty(on);
        Assert.All(on, n => Assert.Equal("", PerfPageVm.LeakBusy(s.Fleet.Device(3)!.Reactor(n)!)));
        Assert.Contains(s.Toasts.Items, t => t.Text.StartsWith("已打开泄漏测试 · 3号机", StringComparison.Ordinal));
    }

    // ———————————————————— 共用搅拌：本地只读时灰掉 ————————————————————

    [Fact]
    public void SharedStirrer_IsDisabled_WhenLocalReadOnly()
    {
        var s = Shell("page=main&dev=2");
        var hp = Assert.IsType<HomePageVm>(s.Page);
        Assert.Equal("2号机为本地只读 · 先申请控制权", hp.Res.ToggleReason);
        hp.Res.StirToggleCommand.Execute(null);
        Assert.True(s.Fleet.Device(2)!.Stir!.On);                // 没有停
        s.SelectDevice(1);
        s.Fleet.RefreshNow();
        Assert.Null(hp.Res.ToggleReason);
    }

    // ———————————————————— 补拉（真机路径，假连接） ————————————————————

    [Fact]
    public async Task Live_Reconnect_BackfillsTheGap_AndTheBannerReportsTheRows()
    {
        var t0 = DateTime.UtcNow.AddMinutes(-3);
        t0 = new DateTime(t0.Ticks - t0.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        string Csv()
        {
            var sb = new System.Text.StringBuilder("时间,RV1_PV(℃),RV1_压力(MPa),RV1_吸气量(mmol)\n");
            for (int sec = 5; sec <= 120; sec++)
                sb.Append(new DateTimeOffset(t0.AddSeconds(sec)).ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", System.Globalization.CultureInfo.InvariantCulture)).Append(",61,0.5,1\n");
            return sb.ToString();
        }
        var (f, d, fake) = await LiveFleet.OneAsync(h => h.On("GET", @"/telemetry\?.*", _ => ScriptedHttpHandler.Json(200, Csv())));
        var live = (LiveDeviceSource)d.Source;
        fake.RaiseTelemetry(LiveFrames.Frame(t0, LiveFrames.Ch(1, 60, 80, 30, 0, 50, "heating"), LiveFrames.Ch(2), LiveFrames.Ch(3), LiveFrames.Ch(4)));
        f.RefreshNow();
        fake.RaiseConnection(ConnectionState.Reconnecting);
        f.RefreshNow();
        Assert.True(d.IsOffline);
        fake.RaiseConnection(ConnectionState.Connected);
        f.RefreshNow();
        Assert.NotNull(d.NetBack);
        for (int i = 0; i < 300 && live.Backfill is not { Pending: false }; i++) await Task.Delay(10);
        Assert.Null(live.Backfill!.Error);
        Assert.Equal(116, live.Backfill.Rows);
        f.RefreshNow();
        Assert.Equal((116, false), (d.NetBack!.Rows, d.NetBack.Pending));
        var h = live.History.Channel(1)!;
        Assert.True(h.Count >= 12);                                          // 120 s 补回来 ≈ 12 格
        Assert.Contains(Enumerable.Range(0, h.Count), i => h[i].TempC == 61);
        Assert.Contains(fake.Http.Calls, c => c.Path.StartsWith("/telemetry?from=", StringComparison.Ordinal));
        await f.DisposeAsync();
    }

    [Fact]
    public async Task Live_Reconnect_NoBackfill_WhenTheSettingIsOff()
    {
        var (f, d, fake) = await LiveFleet.OneAsync(h => h.On("GET", @"/telemetry\?.*", _ => ScriptedHttpHandler.Json(200, "时间\n")), before: x => x.BackfillEnabled = false);
        var live = (LiveDeviceSource)d.Source;
        fake.RaiseTelemetry(LiveFrames.Frame(DateTime.UtcNow.AddSeconds(-20), LiveFrames.Ch(1), LiveFrames.Ch(2), LiveFrames.Ch(3), LiveFrames.Ch(4)));
        f.RefreshNow();
        fake.RaiseConnection(ConnectionState.Reconnecting);
        f.RefreshNow();
        fake.RaiseConnection(ConnectionState.Connected);
        f.RefreshNow();
        Assert.Null(live.Backfill);
        Assert.Equal(0, fake.Http.Count("GET", @"/telemetry\?.*"));
        Assert.Null(d.NetBack!.Rows);
        await f.DisposeAsync();
    }
}
