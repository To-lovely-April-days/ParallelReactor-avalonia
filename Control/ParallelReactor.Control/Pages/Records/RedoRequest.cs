using System.Collections.Generic;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Pages.Records;

/// <summary>「再做一次」要去哪：程序页（载入程序、预选通道）或泄漏测试（性能测试页的泄漏测试）。</summary>
public enum RedoKind { Program, LeakTest }

/// <summary>
/// 记录页「用这组参数再做一次」/「再做一次泄漏测试」的交接（融合版 F7 → F6 程序页 / F8 性能测试页）。
/// <para><b>路由</b>：<see cref="RedoKind.Program"/> → <see cref="AppPage.Program"/>，<see cref="RedoKind.LeakTest"/> → <see cref="AppPage.Perf"/>；
/// 记录页先 <c>ShellVm.Go(页面)</c> + <c>ShellVm.SelectDevice(DeviceK)</c>，再把请求交给目标页面（<see cref="IRedoTarget.AcceptRedo"/>）。</para>
/// <para><b>参数</b>：
/// <list type="bullet">
/// <item><see cref="DeviceK"/>：设备位置（样稿 VIEW = s.dev）。</item>
/// <item><see cref="ProgramName"/> / <see cref="ProgramId"/>：要载入的程序（样稿 PG.sel = lp.id；按设备的程序库找到的 id，找不到时记录页的按钮带原因、不发请求）。泄漏测试为 null。</item>
/// <item><see cref="Channels"/>：预选的通道 = 那次会话的通道里此刻空闲的（不在运行、不在报警、没停用；样稿 PG.tch）。</item>
/// <item><see cref="Busy"/>：那次会话的通道里现在不能选的（正在运行 / 报警 / 停用），给提示用。</item>
/// <item><see cref="SessionId"/>：来自哪条记录。</item>
/// </list></para>
/// 目标页面还没实现 <see cref="IRedoTarget"/> 时，记录页只换页面 + 选设备，并提示「请在程序页载入「…」并选 RV…」（不丢请求：<see cref="RecordsPageVm.LastRedo"/>）。
/// </summary>
public sealed record RedoRequest(RedoKind Kind, int DeviceK, string SessionId, string? ProgramName, string? ProgramId,
    IReadOnlyList<int> Channels, IReadOnlyList<int> Busy)
{
    /// <summary>目标页面。</summary>
    public AppPage Page => Kind == RedoKind.LeakTest ? AppPage.Perf : AppPage.Program;
}

/// <summary>
/// 能接「再做一次」的页面（F6 的程序页、F8 的性能测试页实现它）：载入程序 / 切到泄漏测试、预选通道。
/// 返回要提示的文字（null = 不提示，记录页用默认的「已载入…」）；返回 false 表示没接住（记录页给出手动操作的提示）。
/// </summary>
public interface IRedoTarget
{
    bool AcceptRedo(RedoRequest request, out string? toast);
}
