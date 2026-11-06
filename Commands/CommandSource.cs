namespace ParallelReactor.Commands;

public enum SourceKind { Local, Remote }

/// <summary>命令来源：触屏（Local）或远程客户端（Remote）。纯逻辑。</summary>
public readonly record struct CommandSource(SourceKind Kind, string ClientId = "", string ClientName = "")
{
    public static readonly CommandSource Local = new(SourceKind.Local);
    public static CommandSource Remote(string clientId, string name) => new(SourceKind.Remote, clientId, name);
    public bool IsRemote => Kind == SourceKind.Remote;
    /// <summary>写入 Alarm.Log / LogFileEvent / RequestEstop(source) 的来源标签。</summary>
    public string Label => IsRemote ? $"远程（{ClientName}）" : "触屏";
}
