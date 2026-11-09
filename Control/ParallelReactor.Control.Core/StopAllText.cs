using System.Collections.Generic;

namespace ParallelReactor.Control.Core;

/// <summary>
/// 「全部停止」的文字，跟着 <see cref="ControlOptions.StopAllStopsStirring"/> 走。实时设备、演示数据和全部停止对话框都用这一份。
/// <para>
/// 连搅拌一起停（默认，用户 2026-11-09 确认）时逐字同样稿：js/dialogs.js 的 stopall 对话框（副标题、说明、发送后的提示），
/// js/sim.js 的 RUN.stopAll（单台的结果）——副标题末尾「、停止搅拌」，说明的最后一句「搅拌也会一并停止；需要立即切断并锁定请用急停。」，
/// 提示末尾「 · 搅拌一并停止」。不停搅拌（false）时是样稿第一版的文字：「搅拌保持当前状态；需要彻底切断请用急停。」，提示没有后缀。
/// </para>
/// </summary>
public static class StopAllText
{
    /// <summary>对话框副标题。样稿：「停止所选设备上全部反应釜：中止程序、停止加热、关闭进气、停止搅拌」。</summary>
    public static string Sub(bool stopsStirring) =>
        "停止所选设备上全部反应釜：中止程序、停止加热、关闭进气" + (stopsStirring ? "、停止搅拌" : "");

    /// <summary>说明里关于搅拌的最后一句。样稿：「搅拌也会一并停止；需要立即切断并锁定请用急停。」</summary>
    public static string StirNote(bool stopsStirring) =>
        stopsStirring ? "搅拌也会一并停止；需要立即切断并锁定请用急停。" : "搅拌保持当前状态；需要彻底切断请用急停。";

    /// <summary>
    /// 对话框说明，纯文字。样稿把「不需要控制权」加粗，要加粗的视图用「全部停止」+ 粗体「不需要控制权」+ 后半句 + <see cref="StirNote"/> 自己拼。
    /// </summary>
    public static string Note(bool stopsStirring) =>
        "全部停止不需要控制权，连接正常就会发送（本机看到的状态可能滞后，所以不会因为「看起来没在运行」而跳过）。" + StirNote(stopsStirring);

    /// <summary>提示的后缀。样稿：「 · 搅拌一并停止」；不停搅拌时没有后缀（""）。</summary>
    public static string StirSuffix(bool stopsStirring) => stopsStirring ? " · 搅拌一并停止" : "";

    /// <summary>对话框发送后的提示。样稿：「全部停止已发送到 2 台：1号机、2号机 · 搅拌一并停止」。</summary>
    public static string Sent(IReadOnlyCollection<string> aliases, bool stopsStirring) =>
        "全部停止已发送到 " + aliases.Count + " 台：" + string.Join("、", aliases) + StirSuffix(stopsStirring);

    /// <summary>单台设备的结果。样稿 RUN.stopAll：「已停止 3 个反应釜 · 搅拌一并停止」。</summary>
    public static string Stopped(int reactors, bool stopsStirring) => "已停止 " + reactors + " 个反应釜" + StirSuffix(stopsStirring);

    /// <summary>单台设备的结果，本机看到没有运行中的反应釜时（info 语气）。样稿：「没有运行中的反应釜 · 已发送全部停止 · 搅拌一并停止」。</summary>
    public static string NothingRunning(bool stopsStirring) => "没有运行中的反应釜 · 已发送全部停止" + StirSuffix(stopsStirring);
}
