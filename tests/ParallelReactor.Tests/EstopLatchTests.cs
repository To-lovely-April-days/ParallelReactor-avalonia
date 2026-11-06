using System;
using System.IO;
using ParallelReactor.Services;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>急停锁存文件：原子写、读回、覆盖、损坏按已锁存处理、清除、真机 / 模拟路径分开。</summary>
public class EstopLatchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pr-latch-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private EstopLatch New(bool sim = false) => new(EstopLatch.PathFor(_dir, sim));

    [Fact]
    public void Missing_NotLatched()
    {
        var l = New();
        Assert.False(l.Exists);
        Assert.False(l.TryRead(out var info));
        Assert.Null(info);
    }

    [Fact]
    public void WriteRead_RoundTrip_NoTmpLeft()
    {
        var l = New();
        var t = new DateTimeOffset(2026, 11, 6, 10, 11, 12, TimeSpan.FromHours(8));
        l.Write(new LatchInfo(t, "触屏"));   // 目录不存在时自动创建
        Assert.True(l.Exists);
        Assert.False(File.Exists(l.Path + ".tmp"));
        Assert.True(l.TryRead(out var info));
        Assert.Equal(t, info!.Time);
        Assert.Equal("触屏", info.Source);
    }

    [Fact]
    public void Write_Overwrites()
    {
        var l = New();
        l.Write(new LatchInfo(DateTimeOffset.Now, "触屏"));
        l.Write(new LatchInfo(DateTimeOffset.Now, "远程"));
        Assert.True(l.TryRead(out var info));
        Assert.Equal("远程", info!.Source);
    }

    [Fact]
    public void Corrupt_TreatedAsLatched()
    {
        var l = New();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(l.Path, "{not json");
        Assert.True(l.TryRead(out var info));
        Assert.Contains("损坏", info!.Source);
        File.WriteAllText(l.Path, "");
        Assert.True(l.TryRead(out info));
        Assert.Contains("损坏", info!.Source);
    }

    [Fact]
    public void Clear_RemovesFile_AndIsIdempotent()
    {
        var l = New();
        l.Write(new LatchInfo(DateTimeOffset.Now, "触屏"));
        Assert.True(l.Clear());
        Assert.False(l.Exists);
        Assert.False(l.TryRead(out _));
        Assert.True(l.Clear());   // 不存在时清除也成功、不抛
    }

    [Fact]
    public void SimAndReal_UseDifferentFiles()
    {
        Assert.NotEqual(EstopLatch.PathFor(_dir, sim: true), EstopLatch.PathFor(_dir, sim: false));
        New(sim: true).Write(new LatchInfo(DateTimeOffset.Now, "模拟"));
        Assert.False(New(sim: false).Exists);   // 模拟里按的急停不锁真机
        Assert.True(New(sim: true).Exists);
    }
}
