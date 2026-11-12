#if DEBUG
using System;
using System.Collections.Generic;
using Avalonia;

namespace ParallelReactor.Control.Shot;

public static partial class ShotPlan
{
    /// <summary>
    /// M6 第 2 组加的截图（名字不与别的组重复；截图准备 Setup 以「m6g2-」开头，见 ShotRunner.M6G2.cs）：键盘焦点环，一张一个 —— 截图准备用 Tab 聚焦一个控件。
    /// 参照用 snap 现拍样稿同一个地址（final-kbd-*-&lt;w&gt;.png）：WebKit 截图时网页没有焦点，先用 JS 把样稿 css 里的 :focus-visible 与 :focus-within 规则
    /// 复制成一个类，再给同一个元素加上这个类并 focus()（只复制 :focus-visible 的话页签的左移出不来；见文档 §8 第 29 条、§9）。
    /// 树的行（1366 打开浮层）、没打开的页签（× 出现、名字左移 10 px）、「+」、图标栏（白、内侧 2–4 px、直角）、
    /// 状态栏报警项（外侧，上下被状态栏裁掉）、设备页卡片、未安装位置页的位置按钮与芯片（外侧，不是 Fluent 的黑框）、程序库的行（内侧）。
    /// </summary>
    private static IEnumerable<Spec> M6G2Shots() => new[]
    {
        new Spec("kbd-xrv", "overview", "sel=1/2", new[] { Screens[0], Screens[1] }, Setup: "m6g2-kbd-xrv"),
        new Spec("kbd-xrv-float", "overview", "sel=1/2&explorer=1", new[] { Screens[2] }, Setup: "m6g2-kbd-xrv"),
        new Spec("kbd-tab", "device/1", "", new[] { Screens[0], Screens[2] }, Setup: "m6g2-kbd-tab"),
        new Spec("kbd-add", "overview", "", new[] { Screens[0] }, Setup: "m6g2-kbd-add"),
        new Spec("kbd-side", "overview", "", new[] { Screens[0] }, Setup: "m6g2-kbd-side"),
        new Spec("kbd-stalarm", "overview", "", new[] { Screens[0] }, Setup: "m6g2-kbd-stalarm"),
        new Spec("kbd-dvcard", "device/1", "", new[] { Screens[0] }, Setup: "m6g2-kbd-dvcard"),
        new Spec("kbd-slot", "reactor/1/5", "", new[] { Screens[0] }, Setup: "m6g2-kbd-slot"),
        new Spec("kbd-chip", "reactor/1/5", "", new[] { Screens[0] }, Setup: "m6g2-kbd-chip"),
        new Spec("kbd-libpick", "reactor/3/13", "sel=3/13", new[] { Screens[0] }, Setup: "m6g2-kbd-libpick"),
    };
}
#endif
