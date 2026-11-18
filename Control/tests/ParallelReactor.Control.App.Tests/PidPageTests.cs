using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Pages.Settings;
using ParallelReactor.Control.Pages.Settings.Pid;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Shell.Dialogs;
using ParallelReactor.Control.Tests.Fakes;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 设置 → 温控 / PID 的自整定页（PidSectionVm）：档位卡片、表（样稿 PIDS、状态）、「开始自整定」的禁用原因、开始 = 每路 StartAutotuneAsync(n, tempC, band)、
/// 整定中（曲线、振荡步骤、状态）、结束 = 重读 GET /pid 给出新旧对比、取消先确认、手改写入、换档清掉结果。
/// 演示数据（fusion=1，冻结）+ 真机（假连接：GET /pid、POST / DELETE /channels/{n}/autotune 的调用记录）。
/// </summary>
public class PidPageTests
{
    private static ShellVm Shell(string query = "page=set&dev=1&set=pid", int w = 1920, int h = 1080)
    {
        var p = DemoParams.Parse("fusion=1&freeze=1");
        var s = new ShellVm(FleetVm.CreateDemo(p));
        s.Toasts.Frozen = true;
        s.Layout.Update(new Size(w, h));
        s.ApplyDemoParams(DemoParams.Parse("apop=0&" + query + "&" + p));
        return s;
    }

    private static PidSectionVm Pid(ShellVm s) => Assert.IsType<SettingsPageVm>(s.Page).Pid;

    // ———————————————————— 演示：页面 ————————————————————

    [Fact]
    public void Ready_BandCards_Table_Form_LikeTheMock()
    {
        var s = Shell();
        var v = Pid(s);
        Assert.Null(v.LockText);
        Assert.True(v.HasData);
        Assert.Equal(new[] { "低温档", "中温档", "高温档" }, v.Bands.Select(b => b.Name));
        Assert.Equal(new[] { "0 – 100 °C", "100 – 200 °C", "200 – 400 °C" }, v.Bands.Select(b => b.Range));
        Assert.Equal(new[] { "5/7 路已整定", "7 路已整定", "超过本机上限 200 °C" }, v.Bands.Select(b => b.Status));
        Assert.Equal(new[] { false, true, false }, v.Bands.Select(b => b.IsOn));
        Assert.False(v.Bands[2].Usable);
        var r1 = v.Rows[0];
        Assert.Equal(("RV1", "13.0", "190", "32", "150 °C", "11-10", "运行中 · 不能整定", false), (r1.Rv, r1.P, r1.I, r1.D, r1.TunedSp, r1.TunedAt, r1.Status, r1.CanCheck));
        var r5 = v.Rows.Single(r => r.N == 5);
        Assert.Equal(("15.1", "218", "33", "已整定", true, true), (r5.P, r5.I, r5.D, r5.Status, r5.CanCheck, r5.CanEdit));
        var r7 = v.Rows.Single(r => r.N == 7);
        Assert.Equal(("停用", false, "—"), (r7.Status, r7.HasValues, r7.TunedSp));
        Assert.Equal("表里是中温档（100–200 °C）每一路的 PID；加热时按设定温度所在的档自动套用。手改 P / I / D 立即写入并存档。", v.TableNote);
        Assert.True(v.ShowForm);
        Assert.False(v.ShowMonitor);
        Assert.Equal(("自整定 · 中温档", "150", "本档 100 – 200 °C", "完成后自动存入中温档，通道停止加热"), (v.FormTitle, v.SpText, v.SpRange, v.AfterText));
        // 低温档：RV3 / RV6 没整定过（出厂值、未整定标签）；默认整定温度 80
        v.Bands[0].PickCommand.Execute(null);
        Assert.Equal((0, "80", "本档 40 – 100 °C"), (v.Band, v.SpText, v.SpRange));
        var low3 = v.Rows.Single(r => r.N == 3);
        Assert.Equal(("运行中 · 不能整定", "—", "—", "12.0"), (low3.Status, low3.TunedSp, low3.TunedAt, low3.P));   // 运行中优先于「未整定」（同样稿）
        // 空闲又没整定过的通道：琥珀「未整定」（3号机 RV12 已结束，低温档没整定过）
        s.SelectDevice(3);
        var r12 = v.Rows.Single(r => r.N == 12);
        Assert.Equal(("未整定", PidTone.Warn, "12.0"), (r12.Status, r12.Tone, r12.P));
        s.SelectDevice(1);
        v.Bands[1].PickCommand.Execute(null);
        // 超过上限的档点不了
        v.Bands[2].PickCommand.Execute(null);
        Assert.Equal(1, v.Band);
        Assert.Contains(s.Toasts.Items, t => t.Text.StartsWith("超过本机温度上限 200 °C", StringComparison.Ordinal));
    }

    [Fact]
    public void Start_DisabledReasons()
    {
        var s = Shell();
        var v = Pid(s);
        Assert.Equal(("开始自整定（0 路）", "先勾选通道"), (v.StartLabel, v.StartReason));
        Assert.Equal("在左边表格里勾选（运行中的通道不能整定）", v.ChipHint);
        v.Rows.Single(r => r.N == 1).Checked = true;                              // 运行中的不能勾
        Assert.Empty(v.Chips);
        v.Rows.Single(r => r.N == 5).Checked = true;
        Assert.Equal(("开始自整定（1 路）", (string?)null), (v.StartLabel, v.StartReason));
        Assert.Equal(new[] { "RV5" }, v.Chips.Select(c => c.Rv));
        v.SpText = "250";
        Assert.Equal("整定温度不在本档区间内（100 – 200 °C）", v.StartReason);
        Assert.True(v.SpBad);
        v.SpText = "200";
        Assert.Equal("整定温度须低于 200 °C（200 °C 属于下一档）", v.StartReason);
        v.SpText = "";
        Assert.Equal("先填整定温度", v.StartReason);
        v.SpText = "160";
        Assert.Null(v.StartReason);
        Assert.False(v.SpBad);
        // 2号机本地只读：说明条、勾不了、开始带原因
        s.SelectDevice(2);
        Assert.Equal("2号机为本地只读 · 申请控制权后可以改 PID 和自整定", v.LockText);
        Assert.All(v.Rows, r => Assert.False(r.CanCheck));
        Assert.All(v.Rows, r => Assert.False(r.CanEdit));
        v.Select(1);
        Assert.Equal("先申请控制权", v.StartReason);
    }

    [Fact]
    public async Task Start_TunesInDemo_ThenMonitorShowsTheRun()
    {
        var s = Shell("page=set&dev=1&set=pid&pidsel=5");
        var v = Pid(s);
        Assert.Null(v.StartReason);
        await v.StartAsync();
        Assert.Contains(s.Toasts.Items, t => t.Text == "开始自整定：1号机 RV5 · 中温档 · 150 °C");
        Assert.True(v.ShowMonitor);
        Assert.False(v.ShowForm);
        Assert.Equal(("正在自整定", "中温档 · 整定温度 150 °C · RV5 · 已用 0 min"), (v.MonTitle, v.MonSub));
        var r5 = v.Rows.Single(r => r.N == 5);
        Assert.Equal(("整定中 · 第 1/3 次振荡", PidTone.Brand, true, false), (r5.Status, r5.Tone, r5.Checked, r5.CanEdit));
        var demo = (Services.DemoDeviceSource)s.Fleet.Device(1)!.Source;
        Assert.True(demo.Fleet.Autotuning(demo.Device, 5));
    }

    [Fact]
    public void Tuning_FromUrl_LikeTheMock()
    {
        var s = Shell("page=set&dev=1&set=pid&pidsel=5&tune=run");
        var v = Pid(s);
        Assert.True(v.ShowMonitor);
        Assert.Equal(("正在自整定", "中温档 · 整定温度 150 °C · RV5 · 已用 26 min"), (v.MonTitle, v.MonSub));
        Assert.Equal("整定中 · 第 2/3 次振荡", v.Rows.Single(r => r.N == 5).Status);
        Assert.Equal(new[] { (true, false), (false, true), (false, false) }, v.Steps.Select(x => (x.Done, x.Cur)));
        var c = v.Chart!;
        Assert.Equal((150d, 40d), (c.Sp, c.Total));
        Assert.InRange(c.Now!.Value, 25.9, 26.1);
        var ln = c.Lines.Single();
        Assert.Equal(5, ln.N);
        Assert.True(ln.M.Length > 150);
        Assert.InRange(ln.T[0], 27, 30);                                            // 从约 28 °C 升起
        Assert.Contains(ln.O, o => o == 0);
        Assert.True(v.CanCancel);
    }

    [Fact]
    public void Done_FromUrl_ReReadsPid_AndShowsTheDiff_UntilBandChanges()
    {
        var s = Shell("page=set&dev=1&set=pid&pidsel=5&tune=done");
        var v = Pid(s);
        Assert.True(v.ShowMonitor);
        Assert.True(v.MonDone);
        Assert.Equal(("整定完成", "中温档 · 整定温度 150 °C · RV5 · 已用 36 min"), (v.MonTitle, v.MonSub));
        Assert.Null(v.Chart!.Now);
        var res = Assert.Single(v.Results);
        Assert.Equal("P 15.1 → 14.0 · I 218 → 182 s · D 33 → 28 s · 已存入中温档", res.Text);
        Assert.Equal("已存入中温档", res.Saved);
        Assert.True(v.HasOkNote);
        Assert.Equal("新 PID 已写入仪表并存入中温档；以后这一档温度区间（100–200 °C）里加热都用它。整定后通道已停止加热，正在自然降温。", v.OkNote);
        var r5 = v.Rows.Single(r => r.N == 5);
        Assert.Equal(("14.0", "182", "28", "150 °C", "11-15", "整定完成", PidTone.Run), (r5.P, r5.I, r5.D, r5.TunedSp, r5.TunedAt, r5.Status, r5.Tone));
        Assert.False(r5.CanEdit);
        Assert.DoesNotContain(s.Toasts.Items, t => t.Text.Contains("自整定完成"));   // 地址参数摆出来的结果不弹提示（截图同样稿）
        // 刷新照旧留着；换档 → 结果没了，回到表单
        s.Fleet.RefreshNow();
        Assert.True(v.MonDone);
        v.Bands[0].PickCommand.Execute(null);
        Assert.False(v.ShowMonitor);
        Assert.True(v.ShowForm);
        v.Bands[1].PickCommand.Execute(null);
        Assert.Equal("已整定", v.Rows.Single(r => r.N == 5).Status);
    }

    [Fact]
    public void Done_LeavingTheSection_DropsTheResult()
    {
        var s = Shell("page=set&dev=1&set=pid&tune=done");
        var v = Pid(s);
        Assert.True(v.MonDone);
        var sp = Assert.IsType<SettingsPageVm>(s.Page);
        sp.Section = "lim";
        sp.Section = "pid";
        Assert.False(v.ShowMonitor);
        Assert.Equal("14.0", v.Rows.Single(r => r.N == 5).P);
    }

    [Fact]
    public async Task Cancel_AsksToConfirm_ThenStopsWithoutChangingPid()
    {
        var s = Shell("page=set&dev=1&set=pid&tune=run");
        var v = Pid(s);
        v.CancelCommand.Execute(null);
        var dlg = Assert.IsType<PidCancelDialogVm>(s.Dialogs.Current);
        Assert.Equal("取消 1号机 的自整定？", dlg.Title);
        Assert.Equal("RV5 的自整定（中温档 · 150 °C）会中止：通道停止加热，自然降温。", dlg.Text);
        var demo = (Services.DemoDeviceSource)s.Fleet.Device(1)!.Source;
        Assert.True(demo.Fleet.Autotuning(demo.Device, 5));                     // 只是问，还没停
        Assert.True(v.ShowMonitor);
        // 「继续整定」= 关掉
        var foot = (FootBar)dlg.Footer!;
        foot.Buttons[0].Command.Execute(null);
        Assert.Null(s.Dialogs.Current);
        Assert.True(demo.Fleet.Autotuning(demo.Device, 5));
        // 再来一次，确认
        v.CancelCommand.Execute(null);
        dlg = Assert.IsType<PidCancelDialogVm>(s.Dialogs.Current);
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)dlg.GoButton.Command).ExecuteAsync(null);
        Assert.False(demo.Fleet.Autotuning(demo.Device, 5));
        Assert.False(v.ShowMonitor);
        Assert.Contains(s.Toasts.Items, t => t.Text == "已取消自整定 · 1号机 RV5 停止加热，PID 未改");
        Assert.Equal(("15.1", "已整定"), (v.Rows.Single(r => r.N == 5).P, v.Rows.Single(r => r.N == 5).Status));
    }

    [Fact]
    public async Task ManualEdit_WritesTheBand_AndRereads()
    {
        var s = Shell();
        var v = Pid(s);
        var r5 = v.Rows.Single(r => r.N == 5);
        r5.P = "abc";
        Assert.Contains(s.Toasts.Items, t => t.Text == "RV5：P 须大于 0，I、D（s）不能小于 0");
        Assert.Equal("15.1", r5.P);                                                 // 恢复
        r5.I = "200";
        await Task.Yield();
        Assert.Contains(s.Toasts.Items, t => t.Text == "1号机 · 已写入 RV5 中温档 PID（P 15.1 · I 200 s · D 33 s）");
        Assert.Equal("200", r5.I);
        Assert.Equal("已整定 · 手改过", r5.Status);
        var d1 = s.Fleet.Device(1)!;
        Assert.Equal(200, v.DataOf(d1)!.Channels[4].Bands[1].I);
    }

    [Fact]
    public void Narrow_HidesDateColumn()
    {
        var s = Shell(w: 1366, h: 768);
        Assert.True(Pid(s).IsNarrow);
        var w = Shell();
        Assert.False(Pid(w).IsNarrow);
    }

    // ———————————————————— 真机（假连接）————————————————————

    private static PidDto LivePid(Func<int, bool> tuning, double sp = 150, int band = 1, PidBandValueSnap? rv1Mid = null, PidBandValueSnap? rv2Mid = null)
    {
        var bands = new[] { new PidBandSnap(0, "低温档", 0, 100), new PidBandSnap(1, "中温档", 100, 200), new PidBandSnap(2, "高温档", 200, 400) };
        PidBandValueSnap Def(int b) => new(b, 40 + 20 * b, 100 + 25 * b, 15 + 5 * b, null, null, null);
        PidChannelSnap Ch(int id) => new(id, tuning(id), tuning(id) ? band : null, tuning(id) ? sp : null,
            Enumerable.Range(0, 3).Select(b => b == 1 && id == 1 && rv1Mid != null ? rv1Mid : b == 1 && id == 2 && rv2Mid != null ? rv2Mid : Def(b)).ToArray());
        return new PidDto(1, DateTime.UtcNow, bands, Enumerable.Range(1, 8).Select(Ch).ToArray());
    }

    private static TelemetrySnapshot Frame(TelemetrySnapshot f0, Func<int, bool> tuning) =>
        f0 with { Ts = DateTime.UtcNow, Channels = f0.Channels.Select(c => c with { Autotuning = tuning(c.Id) }).ToArray() };

    private static async Task Until(Func<bool> cond, Action tick)
    {
        for (int i = 0; i < 300 && !cond(); i++) { tick(); await Task.Delay(10); }
        Assert.True(cond());
    }

    [Fact]
    public async Task Live_Start_CallsAutotunePerChannelWithBand_Finish_ReReadsPid_AndShowsDiff()
    {
        var pid = LivePid(_ => false);
        var (f, d, fake) = await LiveFleet.OneAsync(h =>
        {
            FakeReactorConnection.ScriptGrantedLease(h);
            h.On("GET", "/pid", _ => ScriptedHttpHandler.Ok(pid));
            h.On("POST", @"/channels/\d+/autotune", c => ScriptedHttpHandler.Command(c, true, new { channel = 1, sp = 150, band = 1 }));
        });
        Assert.True(await fake.Lease.AcquireAsync(CancellationToken.None));
        f.RefreshNow();
        var s = new ShellVm(f);
        s.Toasts.Frozen = true;
        s.SelectDevice(d.K);
        s.OpenSettings("pid");
        var v = Assert.IsType<SettingsPageVm>(s.Page).Pid;
        await Until(() => v.HasData, () => s.Page!.Refresh(f));
        Assert.Equal(1, fake.Http.Count("GET", "/pid"));
        Assert.Null(v.LockText);
        // RV1 就绪、RV2 已结束：都能整定；两路一起
        v.Select(1);
        v.Select(2);
        v.SpText = "160";
        Assert.Null(v.StartReason);
        await v.StartAsync();
        var calls = fake.Http.Of("POST", @"/channels/\d+/autotune").OrderBy(c => c.Path).ToList();
        Assert.Equal(new[] { "/channels/1/autotune", "/channels/2/autotune" }, calls.Select(c => c.Path));
        foreach (var c in calls)
        {
            using var doc = JsonDocument.Parse(c.Body);
            Assert.Equal(160, doc.RootElement.GetProperty("tempC").GetDouble());
            Assert.Equal(1, doc.RootElement.GetProperty("band").GetInt32());
            Assert.Equal("L-test", c.Lease);                                       // 控制类：带租约
        }
        Assert.True(v.ShowMonitor);
        Assert.Equal("中温档 · 整定温度 160 °C · RV1、RV2 · 已用 0 min", v.MonSub);
        // 设备开始整定（遥测 autotuning = true），GET /pid 也写着在整定
        pid = LivePid(n => n is 1 or 2, 160);
        var f0 = ApiJson.Deserialize<TelemetrySnapshot>(Samples.Read("live-snapshot.json"))!;
        fake.RaiseTelemetry(Frame(f0, n => n is 1 or 2));
        f.RefreshNow();
        s.Page!.Refresh(f);
        Assert.Equal("整定中 · 第 1/3 次振荡", v.Rows.Single(r => r.N == 1).Status);
        // RV1 结束（RV2 还在整定）：重读 PID，RV1 给出结果；监视还在「正在自整定」
        var at = DateTime.UtcNow;
        pid = LivePid(n => n == 2, 160, rv1Mid: new PidBandValueSnap(1, 55.5, 110, 18, 160, at, null));
        int reads = fake.Http.Count("GET", "/pid");
        fake.RaiseTelemetry(Frame(f0, n => n == 2));
        f.RefreshNow();
        await Until(() => v.Results.Count == 1, () => s.Page!.Refresh(f));
        Assert.True(fake.Http.Count("GET", "/pid") > reads);
        Assert.Equal("P 60.0 → 55.5 · I 125 → 110 s · D 20 → 18 s · 已存入中温档", v.Results[0].Text);
        Assert.False(v.MonDone);
        Assert.Equal(("整定完成", "55.5"), (v.Rows.Single(r => r.N == 1).Status, v.Rows.Single(r => r.N == 1).P));
        // RV2 被急停打断（设备没有存入新 PID）：结束，给出「没有完成」
        pid = LivePid(_ => false, 160, rv1Mid: new PidBandValueSnap(1, 55.5, 110, 18, 160, at, null));
        fake.RaiseTelemetry(Frame(f0, _ => false));
        f.RefreshNow();
        await Until(() => v.MonDone && v.Results.Count == 2, () => s.Page!.Refresh(f));
        Assert.Equal("整定完成", v.MonTitle);
        Assert.True(v.Results[0].Ok);
        Assert.True(v.Results[1].Failed);
        Assert.Equal(Core.Pid.PidTexts.NotSaved, v.Results[1].Fail);
        Assert.Equal("整定没有完成", v.Rows.Single(r => r.N == 2).Status);
        Assert.Contains(s.Toasts.Items, t => t.Text == "1号机 RV1、RV2 自整定结束 · 有通道没有存入新 PID");
        await f.DisposeAsync();
    }

    [Fact]
    public async Task Live_Cancel_SendsDeletePerChannel_NoLeaseNeeded()
    {
        var pid = LivePid(n => n == 1, 150);
        var (f, d, fake) = await LiveFleet.OneAsync(h =>
        {
            h.On("GET", "/pid", _ => ScriptedHttpHandler.Ok(pid));
            h.On("DELETE", @"/channels/\d+/autotune", c => ScriptedHttpHandler.Command(c, false, new { ok = true }));
        });
        var f0 = ApiJson.Deserialize<TelemetrySnapshot>(Samples.Read("live-snapshot.json"))!;
        fake.RaiseTelemetry(Frame(f0, n => n == 1));      // 触屏上发起的整定：页面打开时就在整定
        f.RefreshNow();
        var s = new ShellVm(f);
        s.Toasts.Frozen = true;
        s.SelectDevice(d.K);
        s.OpenSettings("pid");
        var v = Assert.IsType<SettingsPageVm>(s.Page).Pid;
        await Until(() => v.ShowMonitor, () => s.Page!.Refresh(f));
        Assert.Equal("1号机为本地只读 · 申请控制权后可以改 PID 和自整定", v.LockText);
        Assert.StartsWith("中温档 · 整定温度 150 °C · RV1", v.MonSub);
        Assert.True(v.CanCancel);                                                  // 停止类：不要控制权
        v.CancelCommand.Execute(null);
        var dlg = Assert.IsType<PidCancelDialogVm>(s.Dialogs.Current);
        Assert.Equal(0, fake.Http.Count("DELETE", @"/channels/\d+/autotune"));
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)dlg.GoButton.Command).ExecuteAsync(null);
        Assert.Equal(1, fake.Http.Count("DELETE", "/channels/1/autotune"));
        Assert.False(v.ShowMonitor);
        await f.DisposeAsync();
    }
}
