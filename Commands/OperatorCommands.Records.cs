using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ParallelReactor.Models;

namespace ParallelReactor.Commands;

/// <summary>记录类（报警确认、测试会话 / 标记）与配方应用。</summary>
public sealed partial class OperatorCommands
{
    /// <summary>
    /// 把配方的给定值（TSp / PSp / 转速 / 体积 / 桨型 / 收尾 / 时长）套到所选通道（只允许在设备触屏上进行，附录 B）。
    /// 跳过正在执行程序的通道（打开面板后才开始的程序也跳过，原逻辑）。recipe 可以是配方名或 Id。
    /// 数值逐项按 <see cref="ValueRules"/> 校验：本地仍然直接写入（今天没有限幅），越界项放进 Warnings；严格守卫下拒绝。
    /// 修改 TSp 后由下一个 Tick 的逼近间接写入仪表（§5.11），Data.approachReplanned 标出。
    /// </summary>
    public Task<OpResult> TryApplyRecipe(CommandSource src, string recipe, IReadOnlyList<int> channels)
    {
        if (Admit(src, ActionClass.Control, Exposure.LocalOnly) is { } deny) return deny.AsTask();
        var r = _m.Recipes.FirstOrDefault(x => x.Name == recipe) ?? _m.Recipes.FirstOrDefault(x => x.Id == recipe);
        if (r == null) return OpResult.Fail(OpCodes.NotFound, $"配方「{recipe}」不存在").AsTask();
        var st = _m.Settings;
        var issues = new[]
        {
            ValueRules.TempSp(r.TSp, st.TMaxSp), ValueRules.Volume(r.Vol, st.VolMax), ValueRules.Rpm(r.RpmSp, st.RpmMax),
        }.Where(v => v != null).Select(v => $"配方「{r.Name}」：{v!.Message}（当前 {v.Value:0.##}）").ToArray();
        if (issues.Length > 0 && CommandPolicy.Strict(src))
            return OpResult.Fail(OpCodes.ParamOutOfRange, issues[0], "err", new { issues }).AsTask();

        var applied = new List<int>();
        foreach (int id in channels.Distinct())
        {
            var rv = _m.FindReactor(id);
            if (rv is null || rv.Id == _m.Program.RunningRv) continue;
            rv.TSp = r.TSp;
            rv.PSp = r.PSp;
            rv.RpmSp = r.RpmSp;
            rv.Vol = r.Vol;
            rv.Blade = r.Blade;
            rv.End = r.End;
            rv.Run = r.Run;
            rv.AppliedRecipe = r.Name;
            applied.Add(rv.Id);
        }
        _m.RefreshSchematic();
        if (_m.Drawer.IsOpen) _m.Drawer.RaiseAll();
        return OpResult.Done($"已将「{r.Name}」应用到 {applied.Count} 个通道", "ok",
            new { recipe = r.Name, channels = applied, approachReplanned = applied.Count > 0 }).WithWarnings(issues).AsTask();
    }

    /// <summary>确认一条报警（未处理 → 已确认，已解除 → 已完成，见 <see cref="AlarmLifecycle"/>）。找不到 → NOT_FOUND。没有 Toast。</summary>
    public Task<OpResult> TryAckAlarm(CommandSource src, string alarmId)
    {
        if (Admit(src, ActionClass.Record) is { } deny) return deny.AsTask();
        if (_m.Alarm.Find(alarmId) is not { } a) return OpResult.Fail(OpCodes.NotFound, $"报警 {alarmId} 不存在").AsTask();
        _m.Alarm.AckCore(a);
        return (OpResult.Done("已确认", "ok", new { id = a.Id, stat = a.Stat }) with { Quiet = true }).AsTask();
    }

    /// <summary>当前会话的创建者（会话号 + 来源）。会话被其他途径结束（退出）后会话号对不上，视为无归属。</summary>
    private (string SessionId, CommandSource Owner)? _sessionOwner;

    /// <summary>该会话的创建者（仍是同一个会话时）；本地创建或不明时为 null。</summary>
    private CommandSource? SessionOwnerOf(string sessionId)
        => _sessionOwner is { } o && o.SessionId == sessionId ? o.Owner : null;

    /// <summary>
    /// 开始测试会话（之后的运行记录另写一份到 sessions/）。本地：已有会话时先结束它（今天的行为）；
    /// 严格守卫（远程）：已有会话时拒绝 SESSION_ACTIVE，不替别人结束会话（§5.1：只有创建者或本地操作员可以结束）。
    /// </summary>
    public Task<OpResult> TryBeginSession(CommandSource src, string testId)
    {
        if (Admit(src, ActionClass.Record) is { } deny) return deny.AsTask();
        if (string.IsNullOrWhiteSpace(testId)) return OpResult.Fail(OpCodes.BadRequest, "测试编号不能为空", "warn").AsTask();
        if (CommandPolicy.Strict(src) && _m.Logger.SessionId is { } cur)
            return OpResult.Fail(OpCodes.SessionActive, $"已有测试会话进行中（{cur}）· 请先结束", "warn", new { sessionId = cur }).AsTask();
        var path = _m.BeginTestSession(testId.Trim());
        var id = _m.Logger.SessionId;
        _sessionOwner = id == null ? null : (id, src);
        return OpResult.Done($"测试会话已开始 · {id}", "ok", new { sessionId = id, path }).AsTask();
    }

    /// <summary>
    /// 结束当前测试会话（没有会话时 Silent，幂等）。本地操作员可以结束任何会话；
    /// 远程只能结束自己创建的会话（同一 ClientId），否则 SESSION_NOT_OWNER（§5.1）。
    /// </summary>
    public Task<OpResult> TryEndSession(CommandSource src)
    {
        if (Admit(src, ActionClass.Record) is { } deny) return deny.AsTask();
        var id = _m.Logger.SessionId;
        if (id == null) return OpResult.Silent.AsTask();
        if (src.IsRemote && !(SessionOwnerOf(id) is { IsRemote: true } o && o.ClientId.Length > 0 && o.ClientId == src.ClientId))
            return OpResult.Fail(OpCodes.SessionNotOwner, "只有会话创建者或设备触屏可以结束该会话", "warn", new { sessionId = id }).AsTask();
        _m.EndTestSession();
        _sessionOwner = null;
        return OpResult.Done($"测试会话已结束 · {id}", "ok", new { sessionId = id }).AsTask();
    }

    /// <summary>添加标记：出现在下一行运行记录的「标记」列，同时记一条事件。</summary>
    public Task<OpResult> TryAddMarker(CommandSource src, string text)
    {
        if (Admit(src, ActionClass.Record) is { } deny) return deny.AsTask();
        if (string.IsNullOrWhiteSpace(text)) return OpResult.Fail(OpCodes.BadRequest, "标记内容不能为空", "warn").AsTask();
        _m.AddTestMarker(text);
        return OpResult.Done("已添加标记", "ok", new { text }).AsTask();
    }
}
