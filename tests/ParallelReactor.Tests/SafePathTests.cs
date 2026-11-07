using System;
using System.IO;
using ParallelReactor.Api;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>文件下载的路径安全（设计稿 §9.2）。</summary>
public class SafePathTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "pr-safepath-root");

    [Theory]
    [InlineData("telemetry", "telemetry-20261106.csv")]
    [InlineData("telemetry", "telemetry-20261106-1.csv")]
    [InlineData("events", "events-20261106.csv")]
    [InlineData("events", "events-20261106-2.csv")]
    [InlineData("events", "events-20261106.jsonl")]
    [InlineData("sessions", "TP-017-20261106-070000.csv")]
    [InlineData("sessions", "催化剂A_批次12-20261106-070000.csv")]
    [InlineData("leak", "leak-20261106-060000.csv")]
    [InlineData("leak", "leak-20261106-060000-summary.csv")]
    [InlineData("leak", "leak-20261106-060000-summary.json")]
    public void ValidNames_Resolve_InsideRoot(string type, string name)
    {
        var p = SafePath.TryResolve(Root, type, name);
        Assert.NotNull(p);
        Assert.StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, p!);
        Assert.EndsWith(name, p);
    }

    [Theory]
    [InlineData("telemetry", "../telemetry-20261106.csv")]
    [InlineData("telemetry", "..%2F..%2Fetc%2Fpasswd")]
    [InlineData("telemetry", "../../etc/passwd")]
    [InlineData("telemetry", "..\\telemetry-20261106.csv")]
    [InlineData("telemetry", "/etc/passwd")]
    [InlineData("telemetry", "C:\\windows\\win.ini")]
    [InlineData("telemetry", "telemetry-20261106.csv\0.txt")]
    [InlineData("telemetry", "settings.json")]
    [InlineData("telemetry", "telemetry-2026110.csv")]
    [InlineData("telemetry", "events-20261106.csv")]
    [InlineData("events", "events-20261106-1.jsonl")]
    [InlineData("events", ".seq")]
    [InlineData("sessions", "..csv")]
    [InlineData("sessions", ".hidden.csv")]
    [InlineData("sessions", "a b.csv")]
    [InlineData("sessions", "sub/x.csv")]
    [InlineData("leak", "leak-20261106-060000.json")]
    [InlineData("leak", "leak-2026110-060000.csv")]
    [InlineData("estop", "estop.latch")]
    [InlineData("telemetry", "")]
    [InlineData("telemetry", null)]
    public void BadNames_AreRejected(string type, string? name)
        => Assert.Null(SafePath.TryResolve(Root, type, name));

    [Fact]
    public void UnicodeNormalization_NfdNameRejected()
    {
        // 同一个字符的 NFD 形式（组合字符）：不接受，避免同名不同字节绕过白名单
        var nfd = "催化剂é".Normalize(System.Text.NormalizationForm.FormD) + "-20261106-070000.csv";
        Assert.Null(SafePath.TryResolve(Root, "sessions", nfd));
        var nfc = nfd.Normalize(System.Text.NormalizationForm.FormC);
        Assert.NotNull(SafePath.TryResolve(Root, "sessions", nfc));
    }

    [Fact]
    public void SubDir_PerType()
    {
        Assert.Equal("", SafePath.SubDir("telemetry", "telemetry-20261106.csv"));
        Assert.Equal("", SafePath.SubDir("events", "events-20261106.csv"));
        Assert.Equal("events", SafePath.SubDir("events", "events-20261106.jsonl"));
        Assert.Equal("sessions", SafePath.SubDir("sessions", "x.csv"));
        Assert.Equal("leak", SafePath.SubDir("leak", "leak-20261106-060000.csv"));
    }

    [Fact]
    public void LeakRunId_Regex()
    {
        Assert.Matches(SafePath.LeakRunId, "leak-20261106-060000");
        Assert.DoesNotMatch(SafePath.LeakRunId, "leak-20261106-060000.csv");
        Assert.DoesNotMatch(SafePath.LeakRunId, "../leak-20261106-060000");
    }

    [Theory]
    [InlineData("telemetry", "telemetry-20261106.csv\n")]
    [InlineData("events", "events-20261106.jsonl\n")]
    [InlineData("leak", "leak-20261106-060000-summary.json\n")]
    [InlineData("sessions", "TP-017-20261106-070000.csv\n")]
    public void TrailingNewline_IsRejected(string type, string name)
    {
        // .NET 的 $ 会放过末尾换行，白名单正则用 \z 收尾
        Assert.False(SafePath.IsValidName(type, name));
        Assert.Null(SafePath.TryResolve(Root, type, name));
        Assert.False(SafePath.LeakRunId.IsMatch("leak-20261106-060000\n"));
    }
}
