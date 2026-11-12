using System;
using System.Collections.Generic;

namespace ParallelReactor.Control.Core.State;

/// <summary>反应釜的显示状态（样稿 PR.ST 的键，API.md §3.1 / §4），顺序同 PR.ST。代码见 <see cref="StateKeys.Code"/>（"heat" 等，样式类名用）。</summary>
public enum StateKey { Idle, Ready, Heat, Press, React, Alarm, Wait, Stir, Cool, Done, Stopped, Uninstalled, Offline }

/// <summary>
/// 状态的样式类别（RESTYLE-BRIEF §2.1、PORT-PLAN §4.5）：状态靠「字」区分，不靠色相；类别只决定点、字、釜内液体的样式
/// （run 浅绿点 · alarm 红点红字 600 · neutral / done 灰点（done 画 ✓）· offline 空心圈 · uninstalled 无点）。配色在 App 的 StateStyles。
/// </summary>
public enum StateKind { Run, Alarm, Neutral, Done, Offline, Uninstalled }

/// <summary>状态键的固定信息：类别、代码、默认状态字（与样稿 PR.ST[k].zh 逐字相同）、是否「运行中」。</summary>
public static class StateKeys
{
    /// <summary>断线设备的状态字：连上过 =「离线」，从未连上 =「未连接」（样稿 ui.js offWord）。</summary>
    public const string OfflineWord = "离线", NeverConnectedWord = "未连接";

    private static readonly string[] Codes = { "idle", "ready", "heat", "press", "react", "alarm", "wait", "stir", "cool", "done", "stopped", "uninstalled", "offline" };
    private static readonly string[] Words = { "停用", "已启用", "升温中", "升压中", "反应中", "超压报警", "等待中", "搅拌中", "降温中", "已结束", "已停止", "未安装", "离线" };

    /// <summary>全部键（PR.ST 的顺序）。</summary>
    public static IReadOnlyList<StateKey> All { get; } = (StateKey[])Enum.GetValues(typeof(StateKey));

    /// <summary>占用通道的状态（样稿 PR.ST.RUNNING）：升温、升压、反应、报警、等待、搅拌、降温。注意报警也算。</summary>
    public static IReadOnlyList<StateKey> Running { get; } = new[] { StateKey.Heat, StateKey.Press, StateKey.React, StateKey.Alarm, StateKey.Wait, StateKey.Stir, StateKey.Cool };

    /// <summary>组件陈列页「徽标 / 状态」一行的顺序（样稿 placeholder.js）。</summary>
    public static IReadOnlyList<StateKey> GalleryOrder { get; } = new[]
    {
        StateKey.Heat, StateKey.Press, StateKey.React, StateKey.Cool, StateKey.Wait, StateKey.Alarm,
        StateKey.Ready, StateKey.Idle, StateKey.Stopped, StateKey.Done, StateKey.Offline,
    };

    public static StateKind KindOf(StateKey key) => key switch
    {
        StateKey.Heat or StateKey.Press or StateKey.React or StateKey.Stir or StateKey.Cool or StateKey.Wait => StateKind.Run,
        StateKey.Alarm => StateKind.Alarm,
        StateKey.Done => StateKind.Done,
        StateKey.Offline => StateKind.Offline,
        StateKey.Uninstalled => StateKind.Uninstalled,
        _ => StateKind.Neutral,   // idle · ready · stopped
    };

    /// <summary>样稿的状态代码："heat" / "press" / … / "offline"。</summary>
    public static string Code(StateKey key) => Codes[(int)key];

    /// <summary>默认状态字（PR.ST[k].zh）：报警是「超压报警」（样稿只模拟超压；真机按原因给字，见 <see cref="StateMap"/>），离线是「离线」。</summary>
    public static string Word(StateKey key) => Words[(int)key];

    /// <summary>样稿状态代码 → 键（"heat" → Heat）；不认识返回 false。HMI 的代码（heating 等）用 <see cref="StateMap.KeyOfHmi"/>。</summary>
    public static bool TryParse(string? code, out StateKey key)
    {
        int i = code is null ? -1 : Array.IndexOf(Codes, code);
        key = i >= 0 ? (StateKey)i : StateKey.Idle;
        return i >= 0;
    }

    /// <summary>是否占用通道（<see cref="Running"/>）。</summary>
    public static bool IsRunning(StateKey key) => key is StateKey.Heat or StateKey.Press or StateKey.React or StateKey.Alarm
        or StateKey.Wait or StateKey.Stir or StateKey.Cool;

    /// <summary>状态字是否加粗（只有报警：600）。</summary>
    public static bool IsBold(StateKey key) => key == StateKey.Alarm;

    /// <summary>断线时的状态字：连上过「离线」，从未连上「未连接」。</summary>
    public static string OfflineText(bool everOnline) => everOnline ? OfflineWord : NeverConnectedWord;
}
