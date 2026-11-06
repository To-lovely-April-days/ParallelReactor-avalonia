using System.Collections.Generic;
using System.Linq;

namespace ParallelReactor.Models;

/// <summary>「开始运行」时某通道的安全相关状态（预运行检查 / ConfirmRun 用）。</summary>
/// <param name="Id">通道号（1 起）。</param>
/// <param name="State">当前状态。</param>
/// <param name="OverTempTripped">超温切断已触发且未解除。</param>
/// <param name="OverPressureTripped">超压联锁已触发且未解除。</param>
public readonly record struct RunChannel(int Id, ReactorState State, bool OverTempTripped, bool OverPressureTripped);

/// <summary>
/// 开始运行的通道筛选（纯逻辑，单元测试直接链接）：
/// 只给「已启用 / 已结束」且没有超温 / 超压未解除的通道下发加热；报警（任何原因）、运行中的通道一律不动。
/// 任何通道有报警或超温 / 超压未解除都阻塞本次运行（C19，无豁免——包括停用通道上的超温报警）。
/// </summary>
public static class RunSelection
{
    /// <summary>该通道是否参与本次「开始运行」（转升温中并下发加热）。</summary>
    public static bool CanStartHeat(ReactorState s, bool overTempTripped, bool overPressureTripped)
        => s is ReactorState.Ready or ReactorState.Done && !overTempTripped && !overPressureTripped;

    public static bool CanStartHeat(RunChannel c) => CanStartHeat(c.State, c.OverTempTripped, c.OverPressureTripped);

    /// <summary>有报警或超温 / 超压未解除：阻塞本次运行。</summary>
    public static bool IsBlocking(RunChannel c) => c.State == ReactorState.Alarm || c.OverTempTripped || c.OverPressureTripped;

    /// <summary>阻塞本次运行的通道号。</summary>
    public static IReadOnlyList<int> Blocking(IEnumerable<RunChannel> chans) => chans.Where(IsBlocking).Select(c => c.Id).ToList();

    /// <summary>将参与本次运行的通道号。</summary>
    public static IReadOnlyList<int> ToHeat(IEnumerable<RunChannel> chans) => chans.Where(CanStartHeat).Select(c => c.Id).ToList();
}
