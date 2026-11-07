using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ParallelReactor.Api;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>
/// 接口层的结构约束（设计稿 §2.2 第 4 条、§3.2）：
/// <list type="bullet">
/// <item>Api/*.cs（纯逻辑，测试工程链接）不得引用 Avalonia 或 ViewModels——Kestrel 线程绝不碰 VM；只有 Api/Hmi/ 下的适配器可以；</item>
/// <item>Api/Hmi/ 以外不得出现 Dispatcher 调用；Api/Hmi/HmiApiSources 里 Dispatcher 只用于 /preflight（唯一进 UI 线程的读取）；</item>
/// <item>ProgramSchemaCodes 的枚举 label 与 Models/ProgramStep.cs 的 Options 一一对应（§6.9：协议用英文码、中文只是 label）。</item>
/// </list>
/// </summary>
public class ApiArchitectureTests
{
    private static IEnumerable<string> PureApiFiles() => SourceScan.Files("Api", "*.cs", recursive: false);

    [Fact]
    public void Api_PureFiles_DoNotReferenceAvaloniaOrViewModels()
    {
        var files = PureApiFiles().ToList();
        Assert.True(files.Count >= 10, "Api/ 下应有纯逻辑文件");
        var bad = new List<string>();
        foreach (var f in files)
        {
            var src = SourceScan.Mask(File.ReadAllText(f));
            if (Regex.IsMatch(src, @"\bAvalonia\b")) bad.Add($"{SourceScan.Rel(f)}：引用 Avalonia");
            if (Regex.IsMatch(src, @"\bParallelReactor\.ViewModels\b|\bMainViewModel\b")) bad.Add($"{SourceScan.Rel(f)}：引用 ViewModels");
            if (Regex.IsMatch(src, @"\bDispatcher\b")) bad.Add($"{SourceScan.Rel(f)}：使用 Dispatcher");
        }
        Assert.True(bad.Count == 0, "接口纯逻辑层越界：\n" + string.Join("\n", bad));
    }

    [Fact]
    public void Api_HmiAdapter_UsesDispatcherOnlyForPreflight()
    {
        var hmi = Path.Combine(SourceScan.RepoRoot, "Api", "Hmi", "HmiApiSources.cs");
        Assert.True(File.Exists(hmi));
        var members = SourceScan.Members(hmi);
        var withDispatcher = members.Where(m => Regex.IsMatch(m.Body, @"Dispatcher\.UIThread")).Select(m => m.Name).Distinct().ToList();
        Assert.Equal(new[] { "PreflightAsync" }, withDispatcher);

        // SnapshotBuilder 只在 UI 线程被 ControlLoop 调用，本身不投递
        var sb = File.ReadAllText(Path.Combine(SourceScan.RepoRoot, "Api", "Hmi", "SnapshotBuilder.cs"));
        Assert.DoesNotMatch(@"Dispatcher", SourceScan.Mask(sb));
    }

    [Fact]
    public void ControlLoop_PublishesSnapshot_AfterGateExit_InFinally()
    {
        var loop = SourceScan.Mask(File.ReadAllText(Path.Combine(SourceScan.RepoRoot, "Service", "ControlLoop.cs")));
        int exitAt = loop.IndexOf("Gate.Exit(", StringComparison.Ordinal);
        int publishAt = loop.IndexOf("Snapshots.Publish(", StringComparison.Ordinal);
        Assert.True(exitAt > 0 && publishAt > exitAt, "快照应在 Gate.Exit 之后发布（门闸统计为本 Tick 终值）");
        Assert.Contains("SnapshotBuilder.Build(", loop);
    }

    [Fact]
    public void ViewModels_DoNotReferenceApiHostOrKestrel()
    {
        // VM / 服务层不认识接口宿主（只有 ApiStop 回调由 App 注入；Service 只用 Api.SnapshotStore / ChannelRange 两个纯类型）：
        // 接口层（Kestrel、WsHub）可整体拆掉而不影响触屏与控制循环
        var bad = SourceScan.Files("ViewModels", "*.cs", recursive: true).Concat(SourceScan.Files("Service", "*.cs", recursive: true))
            .Where(f => Regex.IsMatch(SourceScan.Mask(File.ReadAllText(f)), @"\bApiHost\b|\bWsHub\b|Microsoft\.AspNetCore"))
            .Select(SourceScan.Rel).ToList();
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    [Fact]
    public void ProgramSchemaCodes_LabelsMatchStepDefOptions()
    {
        // 测试工程不能链接 Models/ProgramStep.cs（含 Avalonia 类型），改为从源码解析 [StepType.X] = … Options=new[]{…}
        var src = File.ReadAllText(Path.Combine(SourceScan.RepoRoot, "Models", "ProgramStep.cs"));
        var found = new Dictionary<string, string[]>(StringComparer.Ordinal);
        string? step = null;
        foreach (var line in src.Split('\n'))
        {
            var ms = Regex.Match(line, @"\[StepType\.(\w+)\]\s*=");
            if (ms.Success) { step = ms.Groups[1].Value.ToLowerInvariant(); continue; }
            var mf = Regex.Match(line, @"Key\s*=\s*""(\w+)"".*?Kind\s*=\s*""sel"".*?Options\s*=\s*new\[\]\s*\{([^}]*)\}");
            if (!mf.Success || step == null) continue;
            var labels = Regex.Matches(mf.Groups[2].Value, @"""([^""]*)""").Select(x => x.Groups[1].Value).ToArray();
            found[$"{step}.{mf.Groups[1].Value}"] = labels;
        }
        Assert.True(found.Count >= 6, $"只解析到 {found.Count} 个 sel 字段");
        foreach (var (key, labels) in found)
        {
            Assert.True(ProgramSchemaCodes.Options.TryGetValue(key, out var opts), $"ProgramSchemaCodes 缺少 {key}");
            Assert.Equal(labels, opts!.Select(o => o.Label).ToArray());
            Assert.Equal(opts.Length, opts.Select(o => o.Code).Distinct().Count());
        }
        // 表里多出的键只能是手工补的 heat.mode
        var extra = ProgramSchemaCodes.Options.Keys.Except(found.Keys).ToList();
        Assert.Equal(new[] { "heat.mode" }, extra);
        Assert.Equal("seal", ProgramSchemaCodes.CodeOf("end", "mode", "密封 (推荐)"));
        Assert.Equal("气体 A", ProgramSchemaCodes.LabelOf("press", "gas", "A"));
        Assert.Null(ProgramSchemaCodes.CodeOf("end", "mode", "不存在"));
        Assert.False(ProgramSchemaCodes.Options["react.endBy"].Single(o => o.Code == "gasConsumed").Available);
    }

    [Fact]
    public void ApiErrorCodes_ExistInOpCodes()
    {
        // 接口里出现的 OpCodes.X 常量名都在 Commands/OpResult.cs 中定义（设计稿 §11）
        var opcodes = File.ReadAllText(Path.Combine(SourceScan.RepoRoot, "Commands", "OpResult.cs"));
        var defined = Regex.Matches(opcodes, @"public const string (\w+)\s*=").Select(m => m.Groups[1].Value).ToHashSet();
        var used = PureApiFiles().SelectMany(f => Regex.Matches(File.ReadAllText(f), @"OpCodes\.(\w+)").Select(m => m.Groups[1].Value)).Distinct().ToList();
        Assert.True(used.Count >= 8);
        Assert.Empty(used.Where(u => !defined.Contains(u)));
    }
}
