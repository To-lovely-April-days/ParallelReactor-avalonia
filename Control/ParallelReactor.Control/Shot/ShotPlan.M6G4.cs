#if DEBUG
using System;
using System.Collections.Generic;
using Avalonia;

namespace ParallelReactor.Control.Shot;

public static partial class ShotPlan
{
    /// <summary>
    /// M6 第 4 组加的截图（名字不与别的组重复；截图准备 Setup 以「m6g4-」开头，见 ShotRunner.M6G4.cs）。参照用 snap 现拍样稿同一个地址（final-&lt;名字&gt;-&lt;宽&gt;.png）：
    /// <list type="bullet">
    /// <item>rv-maxalarm21：2号机 RV1 超压报警、放大「曲线」（截图准备 m6g4-maxcurves = 点曲线面板的 □；样稿 JS 点 [data-key="rvp-curves"] [data-panel-max]）——
    ///   放大的面板框外 1 px line-strong 的圈和 ShadowDlg 盖到上方的报警条上（条的最下一行变 line-strong、往上一截压暗）。</item>
    /// <item>rv-offalarm311：3号机断线、打开它超压报警的 RV11（offline=3&amp;sel=3/11）—— 反应釜页签照样画报警红点（看反应釜自己的状态），
    ///   悬停提示「3号机 · RV11 · 超压报警 · 数据停在 …」（样稿 tabInfo）。</item>
    /// </list>
    /// </summary>
    private static IEnumerable<Spec> M6G4Shots() => new[]
    {
        new Spec("rv-maxalarm21", "reactor/2/1", "alarm=2:1", new[] { Screens[0], Screens[2] }, Setup: "m6g4-maxcurves"),
        new Spec("rv-offalarm311", "reactor/3/11", "offline=3&sel=3/11", new[] { Screens[0], Screens[2] }),
    };
}
#endif
