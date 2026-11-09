using System;
using System.IO;

namespace ParallelReactor.Control.Tests.Fakes;

/// <summary>
/// 测试用的临时目录：放在测试程序自己的输出目录下（bin/…/test-tmp/&lt;guid&gt;，仓库里、被 .gitignore 忽略），不写系统临时目录、不写用户目录；
/// 用完删除。
/// </summary>
internal sealed class TestDir : IDisposable
{
    public TestDir()
    {
        Path = System.IO.Path.Combine(AppContext.BaseDirectory, "test-tmp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}
