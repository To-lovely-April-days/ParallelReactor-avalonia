using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ParallelReactor.Control.Core.Models;

namespace ParallelReactor.Control.Core.Programs;

/// <summary>
/// 反应釜页「程序」面板没有程序时（样稿 reactor.js noProgHTML）、真机程序对不上台账时（R5 的降级），以及未安装的位置（样稿 unPanel）的文字，逐字照样稿。
/// 纯函数（Core 测试直接测）；界面再按样稿做断行保护（UiText.Keep）与下标（PR.chem）。
/// </summary>
public static class ReactorPageText
{
    // ———————————————————— 没有程序：程序库 + 预览 ————————————————————

    /// <summary>'RV13 没有程序'。</summary>
    public static string NoProgramTitle(int n) => "RV" + I(n) + " 没有程序";

    /// <summary>'从3号机（HT-PR16）的程序库选一个下发：'。</summary>
    public static string LibraryLead(string alias, string modelLabel) => "从" + alias + "（" + modelLabel + "）的程序库选一个下发：";

    /// <summary>从未连上的设备（还没有程序库，API §3.1）：'5号机尚未连接 · 连上后这里显示程序库'。</summary>
    public static string NeverConnectedLead(string alias) => alias + "尚未连接 · 连上后这里显示程序库";

    /// <summary>程序库一行的第二行：说明 + ' · 4 步'（说明里的 N₂ 由界面画下标）；没有说明（真机的草稿）时只写 '4 步'。</summary>
    public static string LibraryNote(ProgramDef def)
    {
        ArgumentNullException.ThrowIfNull(def);
        return (string.IsNullOrEmpty(def.Note) ? "" : def.Note + " · ") + I(def.Steps.Count) + " 步";
    }

    /// <summary>预览标题：'预览：筛选-80 ℃'。</summary>
    public static string PreviewTitle(string name) => "预览：" + name;

    /// <summary>预览标题右边：'只读 · 下发后在这里运行'。</summary>
    public const string PreviewSub = "只读 · 下发后在这里运行";

    /// <summary>
    /// 预览用的程序记录（样稿 noProgHTML 的 prev：status loaded、stepIndex −1 —— 每一步都是「未到」，没有标记、没有进度）。
    /// </summary>
    public static ProgramRecord Preview(ProgramDef def)
    {
        ArgumentNullException.ThrowIfNull(def);
        return new ProgramRecord { Id = "prev-" + def.Id, Name = def.Name, Note = def.Note, Steps = def.Steps, StepIndex = -1, Status = ProgramStatus.Loaded };
    }

    // ———————————————————— 真机：程序对不上台账（R5，样稿没有这种状态） ————————————————————

    /// <summary>降级时画布上的说明（不显示步骤、不估剩余时间）。</summary>
    public const string DegradedTitle = "只显示设备报告的进度";

    /// <summary>
    /// 降级说明的正文：为什么不显示步骤（没有本机下发记录 / 对不上的原因）。<paramref name="reason"/> = 台账给的原因（LedgerMatch.Reason），没有时按「不是本机下发的」。
    /// </summary>
    public static string DegradedText(string? reason) =>
        (string.IsNullOrEmpty(reason) ? "这个程序不是本机下发的" : reason) + " · 设备上的程序内容读不到（HMI 没有程序读取接口），步骤卡片与剩余时间不显示";

    // ———————————————————— 未安装的位置 ————————————————————

    /// <summary>'1号机 · RV5 未安装'。</summary>
    public static string UninstalledTitle(string alias, int n) => alias + " · RV" + I(n) + " 未安装";

    /// <summary>未安装的位置连成段：'RV5–RV8' / 'RV2、RV5–RV8'。</summary>
    public static string UninstalledRuns(ModelProfile m)
    {
        ArgumentNullException.ThrowIfNull(m);
        return string.Join("、", m.UninstalledRuns.Select(r => r.From == r.To ? "RV" + I(r.From) : "RV" + I(r.From) + "–RV" + I(r.To)));
    }

    /// <summary>
    /// 说明段（样稿 reactor-un-p）：'1号机（HT-PR8 · 现场）只装了 RV1–RV4，RV5–RV8 未安装。这个位置没有釜体，也没有温度、压力、加热和搅拌测点，不能下发程序。'
    /// </summary>
    public static string UninstalledText(string alias, ModelProfile m)
    {
        ArgumentNullException.ThrowIfNull(m);
        return alias + "（" + m.Label + "）只装了 " + m.InstalledText + "，" + UninstalledRuns(m) + " 未安装。" +
               "这个位置没有釜体，也没有温度、压力、加热和搅拌测点，不能下发程序。";
    }

    /// <summary>键值表：型号 / 通道（'8 路 · 已装 RV1–RV4'）/ 釜（'50 mL 釜 · 溶液 ≤ 20 mL'）/ 说明（示例型号 · 参数为假设）。</summary>
    public static IReadOnlyList<(string Key, string Value)> UninstalledKv(ModelProfile m)
    {
        ArgumentNullException.ThrowIfNull(m);
        return new[]
        {
            ("型号", m.Label),
            ("通道", I(m.ChannelCount) + " 路 · 已装 " + m.InstalledText),
            ("釜", m.Vessel.Kind + " · " + m.Vessel.Note),
            ("说明", ModelProfiles.SampleNote),
        };
    }

    /// <summary>位置一节的标题：'1号机 的位置 · 8 路'。</summary>
    public static string SlotsTitle(string alias, ModelProfile m) => alias + " 的位置 · " + I(m?.ChannelCount ?? 0) + " 路";

    /// <summary>'去已安装的反应釜'。</summary>
    public const string ChipsTitle = "去已安装的反应釜";

    /// <summary>按钮：'打开1号机设备页'。</summary>
    public static string OpenDevice(string alias) => "打开" + alias + "设备页";

    /// <summary>釜体下面的说明：'RV5 · 空位'。</summary>
    public static string EmptySlot(int n) => "RV" + I(n) + " · 空位";

    /// <summary>面板副标题「未安装」。</summary>
    public const string UninstalledSub = "未安装";

    /// <summary>位置按钮的悬停说明：已安装 '打开 1号机 · RV1 · 反应中'；未安装 'RV5 未安装'。</summary>
    public static string SlotTip(string alias, int n, bool installed, string stateWord) =>
        installed ? "打开 " + alias + " · RV" + I(n) + " · " + stateWord : "RV" + I(n) + " 未安装";

    /// <summary>「去已安装的反应釜」芯片的悬停说明：'打开 1号机 · RV1'。</summary>
    public static string ChipTip(string alias, int n) => "打开 " + alias + " · RV" + I(n);

    private static string I(int n) => n.ToString(CultureInfo.InvariantCulture);
}
