#if DEBUG
using System;
using System.Collections.Generic;
using Avalonia;

namespace ParallelReactor.Control.Shot;

public static partial class ShotPlan
{
    /// <summary>
    /// M6 第 3 组加的截图（名字不与别的组重复；截图准备 Setup 以「m6g3-」开头，见 ShotRunner.M6G3.cs）。参照 = snap 现拍样稿同一个地址、
    /// js 参数做同样的事（final-&lt;name&gt;-&lt;w&gt;.png，见 doc §9）：
    /// <list type="bullet">
    /// <item>tip-runall2：选中 2号机（本地只读），检查器「全部运行」的原因提示（锁图标 + 原因，按钮正下方居中、间距 6；样稿 js：mouseover 后等 450 ms）。</item>
    /// <item>toast-tones：三条提示 ok / info / err（样稿 js：三次 PR.toast）。</item>
    /// <item>clamp-sp11：选中 1号机 · RV1，设定温度打 999 后离开 → 200.0 + warn「超出量程 · 已限制为 200.0」（样稿 js：input[name=sp] 设 999 再失焦）。</item>
    /// </list>
    /// </summary>
    private static IEnumerable<Spec> M6G3Shots() => new[]
    {
        new Spec("tip-runall2", "overview", "sel=2", Screens, Setup: "m6g3-tip-runall"),
        new Spec("toast-tones", "overview", "sel=1", Screens, Setup: "m6g3-toast-tones"),
        new Spec("clamp-sp11", "overview", "sel=1/1", Screens, Setup: "m6g3-clamp-sp"),
    };
}
#endif
