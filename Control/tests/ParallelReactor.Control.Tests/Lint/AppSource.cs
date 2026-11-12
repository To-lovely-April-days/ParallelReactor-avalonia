using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ParallelReactor.Control.Tests.Lint;

/// <summary>
/// 静态检查（PORT-PLAN §4.7）读的 App 源码：测试工程不引用 App，直接扫 Control/ParallelReactor.Control 下的文件。
/// 位置：从测试输出目录往上找 ParallelReactor.sln；环境变量 PR_LINT_APP_DIR 可以换成别的目录（只给「证明检查会失败」用：
/// 复制一份源码到 scratch、故意放进违规，再指过去跑，看到失败后删掉）。
/// </summary>
internal static class AppSource
{
    public const string OverrideVar = "PR_LINT_APP_DIR";

    private static readonly Lazy<string> RepoDir = new(() =>
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "ParallelReactor.sln"))) return d.FullName;
        throw new DirectoryNotFoundException("从测试输出目录往上找不到 ParallelReactor.sln");
    });

    /// <summary>仓库根目录（ParallelReactor.sln 所在）。</summary>
    public static string Repo => RepoDir.Value;

    /// <summary>App 工程目录（或 PR_LINT_APP_DIR）。</summary>
    public static string Dir
    {
        get
        {
            var o = Environment.GetEnvironmentVariable(OverrideVar);
            return string.IsNullOrWhiteSpace(o) ? Path.Combine(Repo, "Control", "ParallelReactor.Control") : Path.GetFullPath(o);
        }
    }

    /// <summary>App 目录下的相对路径（统一用 /）。</summary>
    public static string Rel(string full) => Path.GetRelativePath(Dir, full).Replace('\\', '/');

    /// <summary>App 下全部 .axaml / .cs 源文件（不含 bin / obj），相对路径 → 全路径。</summary>
    public static IReadOnlyList<(string Rel, string Full)> Sources()
    {
        var list = new List<(string, string)>();
        foreach (var f in Directory.EnumerateFiles(Dir, "*.*", SearchOption.AllDirectories))
        {
            if (!f.EndsWith(".axaml", StringComparison.Ordinal) && !f.EndsWith(".cs", StringComparison.Ordinal)) continue;
            var rel = Rel(f);
            if (rel.StartsWith("bin/", StringComparison.Ordinal) || rel.StartsWith("obj/", StringComparison.Ordinal)) continue;
            list.Add((rel, f));
        }
        return list.OrderBy(x => x.Item1, StringComparer.Ordinal).ToList();
    }

    public static string Read(string rel) => File.ReadAllText(Path.Combine(Dir, rel));

    /// <summary>样稿目录（research/pc-v3，仓库旁边）；不存在时返回 null（可选的交叉核对）。</summary>
    public static string? MockDir
    {
        get
        {
            var d = Path.GetFullPath(Path.Combine(Repo, "..", "research", "pc-v3"));
            return Directory.Exists(d) ? d : null;
        }
    }
}
