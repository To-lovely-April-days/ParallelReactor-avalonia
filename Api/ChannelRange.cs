using System;
using System.Collections.Generic;
using System.Linq;

namespace ParallelReactor.Api;

/// <summary>
/// 已安装通道（环境变量 <c>PR_CHANNELS</c>，设计稿 §1.4 / §6.1）的解析："1-4"、"1,3,5-8"、"2"。
/// 无效 / 为空 → 默认全部 8 路。只影响 /device 的 installedChannels 与 channels[].installed，不改变任何控制行为。
/// </summary>
public static class ChannelRange
{
    public const int ChannelCount = 8;

    public static int[] All => Enumerable.Range(1, ChannelCount).ToArray();

    /// <summary>解析失败（任何片段非法、越界、结果为空）返回默认 1..8。</summary>
    public static int[] Parse(string? spec) => TryParse(spec, out var r) ? r : All;

    public static bool TryParse(string? spec, out int[] result)
    {
        result = All;
        if (string.IsNullOrWhiteSpace(spec)) return false;
        var set = new SortedSet<int>();
        foreach (var partRaw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = partRaw;
            int dash = part.IndexOf('-');
            if (dash < 0)
            {
                if (!int.TryParse(part, out var n) || n < 1 || n > ChannelCount) return false;
                set.Add(n);
                continue;
            }
            if (!int.TryParse(part[..dash].Trim(), out var a) || !int.TryParse(part[(dash + 1)..].Trim(), out var b)) return false;
            if (a < 1 || b > ChannelCount || a > b) return false;
            for (int i = a; i <= b; i++) set.Add(i);
        }
        if (set.Count == 0) return false;
        result = set.ToArray();
        return true;
    }
}
