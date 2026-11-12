#if DEBUG
using System;
using System.Collections.Generic;
using Avalonia;

namespace ParallelReactor.Control.Shot;

public static partial class ShotPlan
{
    /// <summary>
    /// M6 第 1 组加的截图（名字不与别的组重复；截图准备 Setup 以「m6g1-」开头，见 ShotRunner.M6G1.cs）：菜单 / 弹出框打开着的整窗截图
    /// （PORT-PLAN §5 M6 验证「菜单展开」；审计把弹出层当单独的一层）—— 菜单栏「设备」「视图」「窗口」、「+」、「›」、总览 «⋯» 与「设备信息」、
    /// 资源管理器 ⓘ 图例、右键菜单（资源管理器的设备行 / 反应釜行、时间轴的行、总览卡片）；键盘：键盘打开的「设备」菜单（第二项画焦点环）、
    /// Tab 到菜单栏的「设备」、Tab 到总览卡片。参照用 snap 现拍样稿同一个地址 + 打开同一个菜单的 js（final-&lt;name&gt;-&lt;w&gt;.png，见 doc §9）。
    /// 无头截图里弹出层是窗口里的覆盖层：截到的是窗口里的位置与尺寸，桌面上原生弹出窗口的位置要实机看（doc §8）。
    /// </summary>
    private static IEnumerable<Spec> M6G1Shots() => new[]
    {
        new Spec("menu-device", "overview", "", Screens, Setup: "m6g1-menu-device"),
        new Spec("menu-view", "overview", "", Screens, Setup: "m6g1-menu-view"),
        new Spec("menu-window", "overview", "", Screens, Setup: "m6g1-menu-window"),
        new Spec("pop-newtab", "overview", "", Screens, Setup: "m6g1-pop-newtab"),
        new Spec("menu-tabs", "overview", "", Screens, Setup: "m6g1-menu-tabs"),
        new Spec("menu-ovmore", "overview", "", Screens, Setup: "m6g1-menu-ovmore"),
        new Spec("pop-ovinfo", "overview", "", Screens, Setup: "m6g1-pop-ovinfo"),
        // 窄屏的资源管理器是浮层：explorer=1 打开它（宽屏本来就停靠着）
        new Spec("pop-legend", "overview", "explorer=1", Screens, Setup: "m6g1-pop-legend"),
        new Spec("ctx-xdev", "overview", "explorer=1", Screens, Setup: "m6g1-ctx-xdev"),
        new Spec("ctx-xrv", "overview", "explorer=1", Screens, Setup: "m6g1-ctx-xrv"),
        new Spec("ctx-tl", "overview", "", Screens, Setup: "m6g1-ctx-tl"),
        new Spec("ctx-card", "overview", "", Screens, Setup: "m6g1-ctx-card"),
        new Spec("menu-kbd", "overview", "", new[] { new PixelSize(1920, 1080) }, Setup: "m6g1-menu-kbd"),
        new Spec("kbd-menubar", "overview", "", new[] { new PixelSize(1920, 1080) }, Setup: "m6g1-kbd-menubar"),
        new Spec("kbd-ovcard", "overview", "", new[] { new PixelSize(1920, 1080) }, Setup: "m6g1-kbd-ovcard"),
    };
}
#endif
