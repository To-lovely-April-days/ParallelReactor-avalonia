using System.Globalization;
using System.Text;

namespace ParallelReactor.Api;

/// <summary>
/// 客户端名的权威规范化（设计稿 §4.2 防伪装弹窗）：HMI 收到 <c>POST /control/request</c> 时清洗 clientName 并以清洗后的名字作为 holder 广播；
/// PC 端在申请前用同一函数处理自己的名字，GET /control 的 holder 才能与本地名按 Ordinal 相等。
/// 规则：去掉控制 / 格式字符（含零宽、双向控制符）、行 / 段分隔符、代理项、私用区与未分配码位；空白折叠为一个空格并去首尾；截到 <see cref="Max"/>。
/// </summary>
public static class ClientNames
{
    public const int Max = 64;

    public static string Sanitize(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var sb = new StringBuilder(name.Length);
        bool pendingSpace = false;
        foreach (var ch in name)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (char.IsWhiteSpace(ch) || cat == UnicodeCategory.SpaceSeparator) { pendingSpace = sb.Length > 0; continue; }   // 含 \t \r \n：折叠为一个空格
            if (cat is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned) continue;
            if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
            sb.Append(ch);
            if (sb.Length >= Max) break;
        }
        return sb.ToString();
    }
}
