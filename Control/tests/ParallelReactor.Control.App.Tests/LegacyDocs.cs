using ParallelReactor.Client;
using ParallelReactor.Models;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 旧格式的 4 步示例程序（程序步骤 v0.8 之前 ProgramDraftStore.Template 的内容：升温 80 → 升压 A 150 psi → 反应 60 min → 结束 · 排空 · 先降温至 30 ℃）。
/// 程序台账 / 设备程序的测试按步骤序号对帧，用它保持原来的 4 步（台账里的旧程序照样一步对一步显示，见 ProgramDocs）；草稿迁移的测试用它当旧草稿。
/// </summary>
internal static class LegacyDocs
{
    public static ProgramDoc Four(string name)
    {
        var d = new ProgramDoc { Name = name };
        d.Add(StepType.Heat, new { mode = "fixed", temp = 80.0, wait = 10.0 });
        d.Add(StepType.Press, new { gas = "A", pres = 150.0, regulate = "on" });
        d.Add(StepType.React, new { endBy = "time", time = 60.0 });
        d.Add(StepType.End, new { mode = "vent", cool = 30.0 });
        return d;
    }
}
