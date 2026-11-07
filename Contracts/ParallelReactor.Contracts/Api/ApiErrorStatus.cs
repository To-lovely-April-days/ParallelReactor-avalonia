using ParallelReactor.Commands;

namespace ParallelReactor.Api;

/// <summary>§11 错误码 → HTTP 状态码（HMI 的 ApiErrors.StatusOf 与 PC 客户端共用这张表；未列出的码一律 409）。</summary>
public static class ApiErrorStatus
{
    public static int Of(string code) => code switch
    {
        OpCodes.BadRequest => 400,
        OpCodes.Unauthorized => 401,
        OpCodes.ForbiddenRole or OpCodes.IpNotAllowed or OpCodes.NotExposed or OpCodes.SessionNotOwner => 403,
        OpCodes.NotFound or OpCodes.ChannelNotFound => 404,
        OpCodes.ParamOutOfRange or OpCodes.ParamCrossCheck or OpCodes.PreflightFailed or OpCodes.ProgramInvalid => 422,
        OpCodes.ClientTooOld => 426,
        OpCodes.PreconditionRequired => 428,
        OpCodes.RateLimited or OpCodes.DownloadBusy => 429,
        OpCodes.HardwareWriteFailed => 502,
        OpCodes.TooManyClients or OpCodes.Starting or OpCodes.UiThreadTimeout or OpCodes.HwOffline => 503,
        OpCodes.OutcomeUnknown => 504,
        OpCodes.Internal => 500,
        _ => 409,
    };
}
