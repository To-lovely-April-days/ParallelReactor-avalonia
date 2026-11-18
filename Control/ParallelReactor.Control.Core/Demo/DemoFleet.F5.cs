using System;
using System.Collections.Generic;
using System.Linq;
using ParallelReactor.Control.Core.Alarms;
using ParallelReactor.Control.Core.State;

namespace ParallelReactor.Control.Core.Demo;

/// <summary>
/// 融合版 F5（报警页）的演示数据：样稿 ALOG 里已经解除的报警（H1–H4）。只在融合版数据（fusion=1）里有；演示的报警簿（DemoDeviceSource.AlarmBook）
/// 把它们排在当前报警前面一起同步 —— 不进 DemoDevice.Alarms，所以不影响状态、计数和报警弹窗（同真机：已解除的报警只在报警簿的 All 里）。
/// 文字按 HMI 的写法（报警名 · 说明，读数段「PV 205.3℃ ≥ 超温限值 200℃」），报警页从文字里拆出「值 / 限值」。
/// </summary>
public sealed partial class DemoFleet
{
    /// <summary>样稿 ALOG 的 H1–H4（今天 3 条、昨天 1 条），全部已解除、已确认。</summary>
    private static readonly (int K, AlarmLevel Level, int? N, string Text, DateTime At, DateTime ClearedAt, string AckBy)[] FusionCleared =
    {
        (2, AlarmLevel.Err, null, "压力采集模块离线 · 连续 3 次无应答 · 压力读数暂停 · 程序等待", DemoFusion.At(12, 40, 5), DemoFusion.At(12, 40, 41), "张工 · 触屏"),
        (1, AlarmLevel.Err, 6, "超温 · 切断加热 · PV 205.3℃ ≥ 超温限值 200℃ · 已切断 RV6 加热（回读 At=4 确认）", DemoFusion.At(11, 22, 10), DemoFusion.At(11, 24, 52), Client),
        (3, AlarmLevel.Err, null, "搅拌电机故障 · 报警码 0x2203（DM2C 驱动器）· 搅拌停止 · 需在设备旁清除", DemoFusion.At(10, 5, 33), DemoFusion.At(10, 12, 8), "李工 · 触屏"),
        (1, AlarmLevel.Warn, 2, "升温超时 · 已用 62 min ≥ 超时 60 min · 仅提醒", DemoFusion.At(16, 47, 20).AddDays(-1), DemoFusion.At(16, 51, 2).AddDays(-1), Client),
    };

    /// <summary>
    /// 第 <paramref name="k"/> 台已经解除的报警（报警簿 All 里、Current 之外的条目；融合版数据之外为空）。身份「h1」…，代码按文字推断。
    /// </summary>
    public IReadOnlyList<AlarmEntry> ClearedAlarms(int k)
    {
        if (!Fusion) return Array.Empty<AlarmEntry>();
        var list = new List<AlarmEntry>();
        for (int i = 0; i < FusionCleared.Length; i++)
        {
            var c = FusionCleared[i];
            if (c.K != k) continue;
            string id = "h" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            string title = c.Text.Split(" · ")[0];
            list.Add(new AlarmEntry(id, id, c.Level, c.N, AlarmCodes.Infer(title), c.Text, c.At, Acked: true, AckBy: c.AckBy,
                Active: false, ClearedAt: c.ClearedAt, Condition: true));
        }
        return list;
    }
}
