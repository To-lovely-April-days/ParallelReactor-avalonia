using System;

namespace ParallelReactor.Api;

/// <summary>GET /files 的一项。from / to 为文件首末行时间（UTC；解析不到为 null）。</summary>
public sealed record HistoryFileInfo(string Name, string Type, long Bytes, DateTime ModifiedAt, DateTime? From, DateTime? To, string? Day);
