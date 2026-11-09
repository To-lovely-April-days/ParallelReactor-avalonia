using System;
using System.IO;
using System.Linq;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// Samples/ 下是对真 HMI（PR_SIM、walk.sh 走通）抓取的响应原文。
/// 两个测试工程共用这一份：ParallelReactor.Control.App.Tests 链接这个文件，Samples/ 也复制到它的输出目录（见那边的 csproj）。
/// </summary>
public static class Samples
{
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "Samples");
    public static string Read(string name) => File.ReadAllText(Path.Combine(Dir, name));
    public static string[] Lines(string name) => File.ReadAllLines(Path.Combine(Dir, name)).Where(l => l.Trim().Length > 0).ToArray();
    public static bool Has(string name) => File.Exists(Path.Combine(Dir, name));
}
