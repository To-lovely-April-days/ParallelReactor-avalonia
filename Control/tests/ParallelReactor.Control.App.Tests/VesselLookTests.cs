using System;
using System.Linq;
using ParallelReactor.Control.Controls.Vessel;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Core.Vessel;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Theme;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 釜体的外观规则（Controls/Vessel/VesselLook、VesselLabels、VesselMotion；样稿 ui.js vesselLook / jacketStyle / fitReactors、base.css 的 rv-lt85 / rv-lt72 与动画关键帧），
/// 以及 ReactorVm 给釜体的三个输入（Cooling、Stirring、VesselLevel）。不画图（渲染与样稿的逐格对照见 Shot/VesselSheet）。
/// </summary>
public class VesselLookTests
{
    [Fact]
    public void Running_Heating_Reacting()
    {
        var L = VesselLook.Of(StateKey.React, heating: true, cooling: false, stirring: true, level: 0.4);
        Assert.True(L.Heating);
        Assert.True(L.Spin);
        Assert.True(L.Bubbles);
        Assert.False(L.Hatch);
        Assert.Equal(JacketStyle.Heating, L.Jacket);
        Assert.Equal(Palette.VLineColor, L.Line);
        Assert.Equal(Palette.VDetailColor, L.Detail);
        Assert.Equal(Palette.VLiqRunColor, L.Liquid);
        Assert.Equal(Palette.VLiqRunLineColor, L.LiquidLine);
        Assert.Equal(134, L.LevelY);
    }

    [Fact]
    public void Cooling_WinsOverHeating_ForTheJacket()
    {
        Assert.Equal(JacketStyle.Cooling, VesselLook.Of(StateKey.Cool, true, true, true, 0.4).Jacket);
        Assert.Equal(JacketStyle.Idle, VesselLook.Of(StateKey.Idle, false, false, false, 0.4).Jacket);
        Assert.False(VesselLook.Of(StateKey.Heat, true, false, false, 0.4).Bubbles);   // 气泡只在反应中
    }

    [Fact]
    public void Alarm_PaleRedLiquid_AlarmOutline()
    {
        var L = VesselLook.Of(StateKey.Alarm, true, false, true, 0.5);
        Assert.True(L.Alarm);
        Assert.Equal(Palette.VLineAlarmColor, L.Line);
        Assert.Equal(Palette.VLiqAlarmColor, L.Liquid);
        Assert.Equal(Palette.VLineAlarmColor, L.LiquidLine);
        Assert.True(L.Spin);   // 报警也占用通道（PR.ST.RUNNING）
    }

    [Fact]
    public void Offline_GreyHatch_NoHeatCoolSpinBubbles()
    {
        var L = VesselLook.Of(StateKey.Offline, heating: true, cooling: true, stirring: true, level: 0.4);
        Assert.True(L.Offline);
        Assert.True(L.Hatch);
        Assert.False(L.Heating);
        Assert.False(L.Cooling);
        Assert.False(L.Spin);
        Assert.False(L.Bubbles);
        Assert.Equal(Palette.VDetailColor, L.Line);
        Assert.Equal(Palette.VDetailColor, L.LiquidLine);
        Assert.Equal(JacketStyle.Idle, L.Jacket);
    }

    [Fact]
    public void Uninstalled_DashedOutlineOnly()
    {
        var L = VesselLook.Of(StateKey.Uninstalled, true, true, true, double.NaN);
        Assert.True(L.Uninstalled);
        Assert.Null(L.Liquid);
        Assert.Equal(Palette.LineColor, L.Line);
        Assert.Equal(Palette.LineColor, L.Detail);
        Assert.False(L.Heating || L.Cooling || L.Spin || L.Bubbles);
        Assert.Equal(VesselLabels.None, VesselLabels.Of(L, 2, simple: false));
    }

    [Theory]
    [InlineData(StateKey.Idle)]
    [InlineData(StateKey.Ready)]
    [InlineData(StateKey.Done)]
    [InlineData(StateKey.Stopped)]
    public void Neutral_GreyLiquid_NoSpinEvenIfStirring(StateKey key)
    {
        var L = VesselLook.Of(key, false, false, true, 0.4);
        Assert.False(L.Spin);   // 只有运行类状态下桨才动
        Assert.Equal(Palette.VLiqColor, L.Liquid);
        Assert.Equal(Palette.VDetailColor, L.LiquidLine);
        Assert.Equal(Palette.VLineColor, L.Line);
    }

    [Fact]
    public void Colours_AreAllPaletteTokens()
    {
        foreach (var key in StateKeys.All)
        {
            var L = VesselLook.Of(key, true, true, true, 0.4);
            foreach (var c in new[] { L.Line, L.Detail, L.LiquidLine }.Concat(L.Liquid is { } lc ? new[] { lc } : Array.Empty<Avalonia.Media.Color>()))
                Assert.Contains(Palette.Tokens, t => t.Color == c);
        }
    }

    // ---------------- 角标（fitReactors：13.5 / 14 px，k < 0.85 不写 OFF，k < 0.72 都不写） ----------------

    [Fact]
    public void Labels_Thresholds()
    {
        var idle = VesselLook.Of(StateKey.Idle, false, false, false, 0.4);
        var heat = VesselLook.Of(StateKey.Heat, true, false, true, 0.4);
        Assert.Equal(VesselLabels.None, VesselLabels.Of(idle, 0.719, false));
        Assert.Equal(VesselLabels.None, VesselLabels.Of(heat, 0.719, false));             // 火焰也不画
        Assert.Equal(new VesselLabels(false, false, true, true, false), VesselLabels.Of(idle, 0.72, false));
        Assert.Equal(new VesselLabels(false, false, true, true, false), VesselLabels.Of(idle, 0.849, false));
        Assert.Equal(new VesselLabels(false, true, true, true, false), VesselLabels.Of(idle, 0.85, false));
        Assert.Equal(new VesselLabels(true, false, true, true, true), VesselLabels.Of(heat, 0.72, false));
        Assert.Equal(new VesselLabels(true, false, true, true, true), VesselLabels.Of(heat, 1.2, false));
    }

    [Fact]
    public void Labels_OfflineNoHt_SimpleNone_Switches()
    {
        var off = VesselLook.Of(StateKey.Offline, true, false, false, 0.4);
        Assert.Equal(new VesselLabels(false, false, true, true, false), VesselLabels.Of(off, 1, false));   // 断线：不写 HT / OFF，也不画火焰
        var heat = VesselLook.Of(StateKey.Heat, true, false, false, 0.4);
        Assert.Equal(VesselLabels.None, VesselLabels.Of(heat, 1, simple: true));
        Assert.Equal(new VesselLabels(false, false, false, false, true), VesselLabels.Of(heat, 1, false, showLabels: false));   // 火焰是图形
        Assert.Equal(new VesselLabels(true, false, false, false, true), VesselLabels.Of(heat, 1, false, showTags: false));     // 反应釜页：TT / PT 在标注框里
        Assert.Equal(VesselLabels.None, VesselLabels.Of(heat, 0, false));
    }

    [Fact]
    public void Labels_NeverBelow13px()
    {
        Assert.Equal(13.5, VesselLabels.Px(VesselSize.Card));
        Assert.Equal(13.5, VesselLabels.Px(VesselSize.Compact));
        Assert.Equal(14, VesselLabels.Px(VesselSize.Large));
        Assert.True(VesselLabels.Px(VesselSize.Card) >= 13);
    }

    // ---------------- 动画关键帧 ----------------

    [Fact]
    public void Motion_Paddle_Linear_1To025To1()
    {
        Assert.Equal(1, VesselMotion.PaddleScale(0), 9);
        Assert.Equal(0.625, VesselMotion.PaddleScale(0.4), 9);
        Assert.Equal(0.25, VesselMotion.PaddleScale(0.8), 9);
        Assert.Equal(0.625, VesselMotion.PaddleScale(1.2), 9);
        Assert.Equal(1, VesselMotion.PaddleScale(1.6), 9);
    }

    [Fact]
    public void Motion_Bubbles_EaseIn_WithDelays()
    {
        Assert.Equal((0d, 1d), VesselMotion.Bubble(0.5, 1));   // 还没到 0.8 s 的延迟：静止
        Assert.Equal((0d, 1d), VesselMotion.Bubble(1.0, 2));
        var (dy, op) = VesselMotion.Bubble(0, 0);
        Assert.Equal(0, dy, 9);
        Assert.Equal(0.95, op, 9);
        (dy, op) = VesselMotion.Bubble(1.2, 0);                // 一半时间：ease-in 0.3154
        Assert.Equal(-44 * 0.31535681257253945, dy, 4);
        Assert.Equal(0.95 * (1 - 0.31535681257253945), op, 4);
        (dy, _) = VesselMotion.Bubble(0.8 + 1.2, 1);           // 第二个气泡晚 0.8 s
        Assert.Equal(-44 * 0.31535681257253945, dy, 4);
    }

    [Fact]
    public void Motion_Pulse_EaseOut()
    {
        var (s, op) = VesselMotion.Pulse(0);
        Assert.Equal(0.7, s, 9);
        Assert.Equal(0.9, op, 9);
        (s, op) = VesselMotion.Pulse(0.6);
        Assert.Equal(0.7 + 1.2 * 0.6846431874274606, s, 4);
        Assert.Equal(0.9 * (1 - 0.6846431874274606), op, 4);
        Assert.Equal((0.7, 0.9), VesselMotion.PulseStill);
        Assert.Equal(0, VesselMotion.Ease(0, 0.42, 0, 1, 1));
        Assert.Equal(1, VesselMotion.Ease(1, 0.42, 0, 1, 1));
    }

    // ---------------- ReactorVm → 釜体的输入 ----------------

    private static ReactorVm Project(DemoFleet fleet, int k, int n)
    {
        var d = fleet.Device(k)!;
        var src = new DemoDeviceSource(fleet, d);
        var vm = new ReactorVm(null, n);
        vm.Apply(src.Read(n), d.Model, d.Online, d.Conn.EverOnline, d.Alias, d.DataText, extras: src.Extras(n));
        _ = src.DisposeAsync();
        return vm;
    }

    [Fact]
    public void ReactorVm_DemoInputs_SameAsMockVesselLook()
    {
        var fleet = new DemoFleet(DemoParams.Default with { Freeze = true });
        var d1 = fleet.Device(1)!;
        // 1号机 RV1 反应中：加热、共用搅拌在转、体积 / 釜容积
        var r11 = Project(fleet, 1, 1);
        var ch11 = d1.Channel(1)!;
        Assert.Equal(StateKey.React, r11.Key);
        Assert.Equal(ch11.Heating, r11.Heating);
        Assert.Equal(d1.RpmOf(ch11) > 0, r11.Stirring);
        Assert.Equal(ch11.VolumeMl / d1.Model.Vessel.VolumeMl, r11.VesselLevel, 9);
        Assert.False(r11.Cooling);
        // 2号机 RV3 降温中：冷却
        var r23 = Project(fleet, 2, 3);
        Assert.Equal(StateKey.Cool, r23.Key);
        Assert.True(r23.Cooling);
        // 1号机 RV5 未安装：没有液位
        var r15 = Project(fleet, 1, 5);
        Assert.Equal(StateKey.Uninstalled, r15.Key);
        Assert.True(double.IsNaN(r15.VesselLevel));
        Assert.False(r15.Cooling || r15.Stirring);
    }

    [Fact]
    public void ReactorVm_LiveInputs_NoVolume_HalfOfMaxFill()
    {
        // 真机没有体积和冷却：液位 = 溶液上限的一半 / 釜容积（PR8：10 / 50）；搅拌看共用搅拌的设定值
        var pr8 = ModelProfiles.Embedded.Get("PR8")!;
        var r = new ReactorVm(null, 1);
        var rd = ChannelProjection.FromFrame(null, 1, online: true, everOnline: true);
        r.Apply(rd, pr8, true, true, "1号机", "");
        Assert.False(r.Cooling);
        Assert.False(r.Stirring);
        Assert.Equal(0.2, r.VesselLevel, 9);
        Assert.Equal(156, VesselAnchors.LevelY(r.VesselLevel));
    }
}
