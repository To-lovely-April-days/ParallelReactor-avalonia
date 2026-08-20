using System;
using System.IO;
using System.Text.Json;

namespace ParallelReactor.Services;

/// <summary>
/// 本地持久化：全局设置 + 温度档位边界 + 8 通道 × 3 档 PID。
/// <para>
/// JSON 存于用户配置目录（Linux: ~/.config/ParallelReactor/settings.json）。
/// 之前这些数据全在内存：重启后档位 PID 回默认，且 InitAsync 会把仪表里残留的
/// 上次整定值（可能属于高温档）误写进当前档（开机默认低温档）——整定结果既丢又串档。
/// </para>
/// 防抖写盘（频繁修改合并为一次），读失败/文件损坏返回 null 用默认值冷启动。
/// </summary>
public static class SettingsStore
{
    public sealed class PersistedState
    {
        public Models.AppSettings? Settings { get; set; }
        public double[]? BandBounds { get; set; }     // [低/中分界, 中/高分界]
        public double[][][]? BandPids { get; set; }   // [通道8][档3][P,I,D]
    }

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ParallelReactor");
    private static string FilePath => Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static readonly object Lock = new();
    private static System.Threading.Timer? _debounce;
    private static Func<PersistedState>? _snapshot;

    /// <summary>读取持久化状态；无文件/损坏返回 null。</summary>
    public static PersistedState? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(FilePath));
        }
        catch { return null; }
    }

    /// <summary>防抖保存：800ms 内的连续修改合并为一次写盘。快照函数在真正写盘时才求值。</summary>
    public static void SaveSoon(Func<PersistedState> snapshot)
    {
        lock (Lock)
        {
            _snapshot = snapshot;
            _debounce ??= new System.Threading.Timer(_ =>
            {
                Func<PersistedState>? snap;
                lock (Lock) snap = _snapshot;
                if (snap != null) SaveNow(snap());
            }, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            _debounce.Change(800, System.Threading.Timeout.Infinite);
        }
    }

    /// <summary>立即写盘（退出前调用，确保所有设置落盘）。</summary>
    public static void SaveNow(PersistedState state)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(state, JsonOpts));
        }
        catch { /* 磁盘异常不影响运行 */ }
    }

    /// <summary>把 src 的公共可写属性复制到 dst（设置对象被界面绑定持有引用，不能整体替换）。</summary>
    public static void CopySettings(Models.AppSettings src, Models.AppSettings dst)
    {
        foreach (var p in typeof(Models.AppSettings).GetProperties())
            if (p.CanRead && p.CanWrite) p.SetValue(dst, p.GetValue(src));
    }
}
