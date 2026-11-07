using System;
using System.Collections.Generic;
using System.Linq;
using ParallelReactor.Commands;

namespace ParallelReactor.Api;

/// <summary>一条写命令的 HTTP 结果：状态码 + 已序列化的正文 + 摘要字段（审计 / opEvent 推送用）。</summary>
public sealed record CommandHttp(int Status, string Body, bool Ok, string StatusWord, string Code, string Message);

/// <summary>
/// <see cref="CommandOutcome"/> / <see cref="OpResult"/> → HTTP（设计稿 §7.1、§11、§13.1）。纯逻辑，测试逐条核对：
/// <list type="bullet">
/// <item>Ok 且 Confirmed → 200 <c>{ ok, opId, status:"confirmed", result, warnings, opEventSeq }</c>；Ok 且 Accepted → 202 <c>status:"accepted"</c>；</item>
/// <item>失败 → <see cref="ApiErrors.StatusOf"/>(code) <c>{ ok:false, opId, error{ code, message, detail }, opEventSeq }</c>；</item>
/// <item>Followups 里失败的一步并入 warnings（主结果不变，与触屏「主提示 + 后续提示」一致）；</item>
/// <item>HwUnconfirmed → 202 accepted + warnings <c>HW_UNCONFIRMED</c>；UiThreadTimeout → 503；OutcomeUnknown → 504。</item>
/// </list>
/// <c>Quiet</c> 只影响触屏 Toast，不影响 HTTP。
/// </summary>
public static class CommandResults
{
    public const string Confirmed = "confirmed", Accepted = "accepted", Rejected = "rejected";

    public static CommandHttp Build(CommandOutcome outcome, string opId, long opEventSeq) => outcome.Kind switch
    {
        OutcomeKind.Completed => FromResult(outcome.Result!, opId, opEventSeq),
        OutcomeKind.HwUnconfirmed => Success(202, Accepted, opId, null, new[] { OpCodes.HwUnconfirmed }, opEventSeq,
            "已下发，硬件在 2 s 内尚未确认 · 最终结果见带同一 opId 的 op.*.result 事件"),
        OutcomeKind.UiThreadTimeout => Error(opId, OpCodes.UiThreadTimeout, "UI 线程 5 s 内未响应，命令已撤销、确定未执行，可重试", null, opEventSeq),
        OutcomeKind.OutcomeUnknown => Error(opId, OpCodes.OutcomeUnknown, "命令已进入 UI 线程但 5 s 内未执行完 · 不得直接重发，请用同一 Idempotency-Key 查询结果", null, opEventSeq),
        _ => Error(opId, OpCodes.Internal, "未知的命令结局", null, opEventSeq),
    };

    public static CommandHttp FromResult(OpResult r, string opId, long opEventSeq)
    {
        if (!r.Ok) return Error(opId, r.Code, r.Message, r.Data, opEventSeq);
        var warnings = r.Warnings.Concat(r.Followups.Where(f => !f.Ok && f.Message.Length > 0).Select(f => f.Message))
            .Concat(r.Followups.SelectMany(f => f.Warnings)).Distinct().ToArray();
        return r.Status == OpStatus.Accepted
            ? Success(202, Accepted, opId, r.Data, warnings, opEventSeq, r.Message)
            : Success(200, Confirmed, opId, r.Data, warnings, opEventSeq, r.Message);
    }

    private static CommandHttp Success(int status, string word, string opId, object? data, string[] warnings, long seq, string message)
    {
        var dto = new CommandResponseDto(true, opId, word, ApiJson.ToElement(data), warnings, seq);
        return new CommandHttp(status, ApiJson.Serialize(dto), true, word, OpCodes.Ok, message);
    }

    public static CommandHttp Error(string opId, string code, string message, object? detail, long seq)
    {
        var dto = new CommandErrorDto(false, opId, new CommandErrorBody(code, message, ApiJson.ToElement(detail)), seq);
        return new CommandHttp(ApiErrors.StatusOf(code), ApiJson.Serialize(dto), false, Rejected, code, message);
    }

    /// <summary>审计 / opEvent 里用的一句话结果。</summary>
    public static string Summary(CommandHttp h) => h.Ok ? $"{h.Status} {h.StatusWord}" : $"{h.Status} {h.Code}";
}
