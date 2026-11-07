using System;

namespace ParallelReactor.Services;

/// <summary>
/// 一条带全局序号的事件（设计稿 §6.4）。时间为 UTC；<c>Origin</c> 是 P0 事件原有的中文「来源」列（RV3 / 系统 / 通讯 / 阀门 / 设置…），
/// <c>Source</c> 是 §6.4 的来源分类（P2 全部为 system，P3 写命令接入后才有 local / remote）。
/// <c>Kind</c> 由 <see cref="EventKinds.Derive"/> 按来源与标题推导（P2 过渡方案，见设计稿 §6.4 说明）。
/// </summary>
public sealed record EventRecord(
    long Seq,
    int SeqEpoch,
    DateTime Ts,
    string TsLocal,
    string Level,
    string Kind,
    int? Channel,
    string Source,
    string Origin,
    string? Client,
    string? OpId,
    string Title,
    string Message);

/// <summary>GET /alarms 与 WS alarm 消息的一条（由界面报警列表派生，见 ViewModels/AlarmViewModel）。</summary>
public sealed record AlarmDto(string Id, string Level, string Rv, int? Channel, string Title, string Message, DateTime RaisedAt, string State, DateTime? ClearedAt);

