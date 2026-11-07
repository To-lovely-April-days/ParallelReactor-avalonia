using System;

namespace ParallelReactor.Api;

/// <summary>§6.2 通道状态码（小写字符串）与中文文案；PC 端显示用。</summary>
public static class ChannelStates
{
    public const string Idle = "idle", Ready = "ready", Heating = "heating", Pressing = "pressing", React = "react",
        Wait = "wait", Stir = "stir", Cooling = "cooling", Done = "done", Alarm = "alarm";

    /// <summary>状态码 → 中文（alarm 可带 alarmCause：overTemp / overPressure）。未知码原样返回。</summary>
    public static string Zh(string? state, string? alarmCause = null) => state switch
    {
        Idle => "停用",
        Ready => "已启用",
        Heating => "升温中",
        Pressing => "升压中",
        React => "反应中",
        Wait => "等待中",
        Stir => "搅拌中",
        Cooling => "降温中",
        Done => "已结束",
        Alarm => alarmCause switch { "overTemp" => "超温", "overPressure" => "超压", _ => "报警" },
        null or "" => "—",
        _ => state,
    };

    /// <summary>运行中的状态（时间轴 / 状态点用）。</summary>
    public static bool IsActive(string? state) => state is Heating or Pressing or React or Wait or Stir or Cooling;
}

/// <summary>§6.3 channels[i].program.state 的取值与中文。</summary>
public static class ChannelProgramStates
{
    public const string Preparing = "preparing", Running = "running", WaitingGas = "waitingGas", Finishing = "finishing",
        Completed = "completed", Failed = "failed", Aborted = "aborted";

    public static string Zh(string? state) => state switch
    {
        Preparing => "准备中",
        Running => "运行中",
        WaitingGas => "等待气路",
        Finishing => "收尾中",
        Completed => "已完成",
        Failed => "失败",
        Aborted => "已中止",
        null or "" => "—",
        _ => state,
    };
}

/// <summary>§5 控制模式 / 租约状态词（GET /control 的 mode / state，WS control 消息）。</summary>
public static class ControlModes
{
    public const string Local = "local", Remote = "remote", Requested = "requested", RemoteLost = "remoteLost";
}
