namespace ParallelReactor.Commands;

public enum SourceKind { Local, Remote }

/// <summary>命令来源：触屏（Local）或远程客户端（Remote）。纯逻辑。</summary>
public readonly record struct CommandSource(SourceKind Kind, string ClientId = "", string ClientName = "")
{
    public static readonly CommandSource Local = new(SourceKind.Local);
    public static CommandSource Remote(string clientId, string name) => new(SourceKind.Remote, clientId, name);
    public bool IsRemote => Kind == SourceKind.Remote;
    /// <summary>
    /// 写入 Alarm.Log / LogFileEvent / RequestEstop(source) / Reactor.StartedBy 的来源标签：「远程（客户端名）」，客户端名与令牌名不同时附带令牌名
    /// （「远程（PC-1 · 令牌 op）」）——客户端名是远程自报的，令牌名才是身份；onRemoteLost=stopHeat 按此标签找该租约启动的过程，另一令牌冒用同一客户端名也对不上。
    /// </summary>
    public string Label => IsRemote ? (ClientId.Length > 0 && ClientId != ClientName ? $"远程（{ClientName} · 令牌 {ClientId}）" : $"远程（{ClientName}）") : "触屏";
}
