using ParallelReactor.Models;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>报警条目状态流转与按条件键的登记 / 清除。</summary>
public class AlarmLifecycleTests
{
    [Theory]
    [InlineData("active", "cleared")]
    [InlineData("ack", "done")]
    [InlineData("done", "done")]
    [InlineData("cleared", "cleared")]
    public void OnCleared(string from, string to) => Assert.Equal(to, AlarmLifecycle.OnCleared(from));

    [Theory]
    [InlineData("active", "ack")]
    [InlineData("cleared", "done")]
    [InlineData("done", "done")]
    [InlineData("ack", "ack")]
    public void OnAck(string from, string to) => Assert.Equal(to, AlarmLifecycle.OnAck(from));

    [Fact]
    public void ClearedThenAck_IsDone_AckThenCleared_IsDone()
    {
        Assert.Equal("done", AlarmLifecycle.OnAck(AlarmLifecycle.OnCleared("active")));
        Assert.Equal("done", AlarmLifecycle.OnCleared(AlarmLifecycle.OnAck("active")));
    }

    private sealed class Item { public string Stat = "active"; }

    [Fact]
    public void Index_GroupsPerKey_TakeEmpties_SecondClearNoop()
    {
        var idx = new AlarmIndex<Item>();
        var a = new Item(); var b = new Item(); var c = new Item();
        idx.Raise("ot:1", a);
        idx.Raise("ot:1", b);
        idx.Raise("op:1", c);
        Assert.Equal(2, idx.Count("ot:1"));

        var taken = idx.TakeActive("ot:1");
        Assert.Equal(new[] { a, b }, taken);
        foreach (var x in taken) x.Stat = AlarmLifecycle.OnCleared(x.Stat);
        Assert.Equal("cleared", a.Stat);
        Assert.Equal("active", c.Stat);   // 其他键不受影响
        Assert.Empty(idx.TakeActive("ot:1"));   // 再次清除是空操作
        Assert.Equal(1, idx.Count("op:1"));
    }

    [Fact]
    public void RaiseAfterClear_RegistersNewActive()
    {
        var idx = new AlarmIndex<Item>();
        var old = new Item();
        idx.Raise("comm:0", old);
        idx.TakeActive("comm:0");
        var fresh = new Item();
        idx.Raise("comm:0", fresh);
        Assert.Equal(new[] { fresh }, idx.TakeActive("comm:0"));
    }
}
