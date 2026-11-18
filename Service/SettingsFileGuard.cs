using System;
using System.IO;

namespace ParallelReactor.Services;

/// <summary>
/// 存档读取保护（纯逻辑，测试工程直接链接）：现场的 settings.json 里有已经整定好的 PID，升级或读失败都不能让它丢。
/// <list type="bullet">
/// <item>读到 v0.6 之前的存档（没有 <c>BandTune</c>）：先原样备份为 <c>settings.json.bak-pre-v0.6</c>（只备份一次）；</item>
/// <item>读不出来（损坏 / 格式对不上）：先原样另存为 <c>settings.json.bak-unreadable-时间</c> 再返回 null——
///   调用方会按「没有存档」处理，之后存盘会覆盖原文件，备份保证数据还能人工找回。</item>
/// </list>
/// </summary>
public static class SettingsFileGuard
{
    public const string PreV06Tag = "pre-v0.6";

    /// <summary>读并解析存档；文件不存在返回 default。parse 抛异常时另存原文后返回 default。</summary>
    public static T? Read<T>(string path, Func<string, T?> parse, Func<DateTime>? now = null) where T : class
    {
        string? text = null;
        try
        {
            if (!File.Exists(path)) return null;
            text = File.ReadAllText(path);
            if (!text.Contains("\"BandTune\"", StringComparison.Ordinal)) BackupOnce(path, text, PreV06Tag);
            return parse(text);
        }
        catch
        {
            if (text != null) BackupOnce(path, text, "unreadable-" + (now?.Invoke() ?? DateTime.Now).ToString("yyyyMMdd-HHmmss"));
            return null;
        }
    }

    /// <summary>把原文另存为 &lt;path&gt;.bak-&lt;tag&gt;（同名备份已存在则不再写）。失败不影响启动。</summary>
    public static void BackupOnce(string path, string text, string tag)
    {
        try
        {
            string bak = path + ".bak-" + tag;
            if (!File.Exists(bak)) File.WriteAllText(bak, text);
        }
        catch { /* 备份失败不阻塞启动 */ }
    }
}
