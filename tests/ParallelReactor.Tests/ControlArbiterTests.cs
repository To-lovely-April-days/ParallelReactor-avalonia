using System;
using System.Threading;
using ParallelReactor.Commands;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>§5.1 控制权仲裁：设计稿 §2.3 判定表全覆盖（2 模式 × 3 来源 × 4 类别 × 2 暴露 = 48 组）。</summary>
public class ControlArbiterTests
{
    private const string Holder = "c1", HolderName = "上位机A";

    private static CommandSource Src(string s) => s switch
    {
        "local" => CommandSource.Local,
        "holder" => CommandSource.Remote(Holder, HolderName),
        "other" => CommandSource.Remote("c2", "上位机B"),
        _ => throw new ArgumentException(s),
    };

    // 期望：空串 = 允许；否则为拒绝码
    [Theory]
    // —— 本地模式 · 触屏：全部允许 ——
    [InlineData("Local", "local", "Control", "Remote", "")]
    [InlineData("Local", "local", "Control", "LocalOnly", "")]
    [InlineData("Local", "local", "Stop", "Remote", "")]
    [InlineData("Local", "local", "Stop", "LocalOnly", "")]
    [InlineData("Local", "local", "Record", "Remote", "")]
    [InlineData("Local", "local", "Record", "LocalOnly", "")]
    [InlineData("Local", "local", "Reset", "Remote", "")]
    [InlineData("Local", "local", "Reset", "LocalOnly", "")]
    // —— 本地模式 · 远程（无论是否「持有者」）：只能停止 / 记录 ——
    [InlineData("Local", "holder", "Control", "Remote", "MODE_LOCAL")]
    [InlineData("Local", "holder", "Control", "LocalOnly", "NOT_EXPOSED")]
    [InlineData("Local", "holder", "Stop", "Remote", "")]
    [InlineData("Local", "holder", "Stop", "LocalOnly", "NOT_EXPOSED")]
    [InlineData("Local", "holder", "Record", "Remote", "")]
    [InlineData("Local", "holder", "Record", "LocalOnly", "NOT_EXPOSED")]
    [InlineData("Local", "holder", "Reset", "Remote", "NOT_EXPOSED")]
    [InlineData("Local", "holder", "Reset", "LocalOnly", "NOT_EXPOSED")]
    [InlineData("Local", "other", "Control", "Remote", "MODE_LOCAL")]
    [InlineData("Local", "other", "Control", "LocalOnly", "NOT_EXPOSED")]
    [InlineData("Local", "other", "Stop", "Remote", "")]
    [InlineData("Local", "other", "Stop", "LocalOnly", "NOT_EXPOSED")]
    [InlineData("Local", "other", "Record", "Remote", "")]
    [InlineData("Local", "other", "Record", "LocalOnly", "NOT_EXPOSED")]
    [InlineData("Local", "other", "Reset", "Remote", "NOT_EXPOSED")]
    [InlineData("Local", "other", "Reset", "LocalOnly", "NOT_EXPOSED")]
    // —— 远程模式 · 触屏：启动 / 改设定被拒，停止 / 记录 / 复位允许 ——
    [InlineData("Remote", "local", "Control", "Remote", "MODE_REMOTE")]
    [InlineData("Remote", "local", "Control", "LocalOnly", "MODE_REMOTE")]
    [InlineData("Remote", "local", "Stop", "Remote", "")]
    [InlineData("Remote", "local", "Stop", "LocalOnly", "")]
    [InlineData("Remote", "local", "Record", "Remote", "")]
    [InlineData("Remote", "local", "Record", "LocalOnly", "")]
    [InlineData("Remote", "local", "Reset", "Remote", "")]
    [InlineData("Remote", "local", "Reset", "LocalOnly", "")]
    // —— 远程模式 · 持有者 ——
    [InlineData("Remote", "holder", "Control", "Remote", "")]
    [InlineData("Remote", "holder", "Control", "LocalOnly", "NOT_EXPOSED")]
    [InlineData("Remote", "holder", "Stop", "Remote", "")]
    [InlineData("Remote", "holder", "Stop", "LocalOnly", "NOT_EXPOSED")]
    [InlineData("Remote", "holder", "Record", "Remote", "")]
    [InlineData("Remote", "holder", "Record", "LocalOnly", "NOT_EXPOSED")]
    [InlineData("Remote", "holder", "Reset", "Remote", "NOT_EXPOSED")]
    [InlineData("Remote", "holder", "Reset", "LocalOnly", "NOT_EXPOSED")]
    // —— 远程模式 · 非持有者 ——
    [InlineData("Remote", "other", "Control", "Remote", "NOT_CONTROL_HOLDER")]
    [InlineData("Remote", "other", "Control", "LocalOnly", "NOT_EXPOSED")]
    [InlineData("Remote", "other", "Stop", "Remote", "")]
    [InlineData("Remote", "other", "Stop", "LocalOnly", "NOT_EXPOSED")]
    [InlineData("Remote", "other", "Record", "Remote", "")]
    [InlineData("Remote", "other", "Record", "LocalOnly", "NOT_EXPOSED")]
    [InlineData("Remote", "other", "Reset", "Remote", "NOT_EXPOSED")]
    [InlineData("Remote", "other", "Reset", "LocalOnly", "NOT_EXPOSED")]
    public void DecisionTable(string mode, string src, string cls, string exp, string expected)
    {
        var m = Enum.Parse<ControlMode>(mode);
        var d = ControlArbiter.Decide(m, m == ControlMode.Remote ? Holder : "", m == ControlMode.Remote ? HolderName : "",
            Enum.Parse<ActionClass>(cls), Src(src), Enum.Parse<Exposure>(exp));
        if (expected.Length == 0)
        {
            Assert.True(d.Allowed, $"{d.Code} {d.Message}");
            Assert.Null(d.Code);
        }
        else
        {
            Assert.False(d.Allowed);
            Assert.Equal(expected, d.Code);
            Assert.False(string.IsNullOrEmpty(d.Message));
        }
    }

    [Fact]
    public void Messages_MatchDesign()
    {
        Assert.Equal("该操作只允许在设备触屏上进行",
            ControlArbiter.Decide(ControlMode.Local, "", "", ActionClass.Reset, Src("holder"), Exposure.Remote).Message);
        Assert.Equal("本地控制中 · 远程只能执行停止类和记录类操作",
            ControlArbiter.Decide(ControlMode.Local, "", "", ActionClass.Control, Src("holder"), Exposure.Remote).Message);
        Assert.Equal("当前控制权属于「上位机A」",
            ControlArbiter.Decide(ControlMode.Remote, Holder, HolderName, ActionClass.Control, Src("other"), Exposure.Remote).Message);
        Assert.Equal("远程控制中（上位机A）",
            ControlArbiter.Decide(ControlMode.Remote, Holder, HolderName, ActionClass.Control, Src("local"), Exposure.Remote).Message);
    }

    [Fact]
    public void EmptyHolder_NeverMatchesRemote()
    {
        // 远程模式但持有者为空（异常状态）：空 ClientId 的远程来源不能被当成持有者
        var d = ControlArbiter.Decide(ControlMode.Remote, "", "", ActionClass.Control,
            CommandSource.Remote("", "匿名"), Exposure.Remote);
        Assert.Equal(OpCodes.NotControlHolder, d.Code);
    }

    [Fact]
    public void Instance_DefaultsToLocal_EnterAndReturnRaiseChanged()
    {
        var a = new ControlArbiter();
        int changed = 0;
        a.Changed += () => changed++;
        Assert.Equal(ControlMode.Local, a.Mode);
        Assert.True(a.Check(ActionClass.Control, CommandSource.Local).Allowed);

        a.EnterRemote(Holder, HolderName);
        Assert.Equal(1, changed);
        Assert.Equal(ControlMode.Remote, a.Mode);
        Assert.Equal(Holder, a.HolderClientId);
        var local = a.Check(ActionClass.Control, CommandSource.Local);
        Assert.Equal(OpCodes.ModeRemote, local.Code);
        Assert.Contains(HolderName, local.Message);
        Assert.True(a.Check(ActionClass.Control, Src("holder")).Allowed);
        Assert.True(a.Check(ActionClass.Stop, CommandSource.Local).Allowed);

        a.ReturnToLocal("本地夺回");
        Assert.Equal(2, changed);
        Assert.Equal(ControlMode.Local, a.Mode);
        Assert.Equal("", a.HolderClientId);
        Assert.Equal("本地夺回", a.LastReturnReason);
        Assert.True(a.Check(ActionClass.Control, CommandSource.Local).Allowed);
    }

    [Fact]
    public void ReturnToLocal_FromOtherThread_RevokesHolder_LikeEstop()
    {
        // 急停快速路径在任意线程调用 ReturnToLocal("急停")（§5.3 第 4 条）：之后原持有者的控制类命令被拒，停止类照常
        var a = new ControlArbiter();
        int changed = 0;
        a.Changed += () => Interlocked.Increment(ref changed);
        a.EnterRemote(Holder, HolderName);
        bool was = false;
        var t = new Thread(() => was = a.ReturnToLocal("急停"));
        t.Start(); t.Join();
        Assert.True(was);
        Assert.Equal(2, changed);
        Assert.Equal(ControlMode.Local, a.Mode);
        Assert.Equal("", a.HolderName);
        Assert.Equal("急停", a.LastReturnReason);
        Assert.Equal(OpCodes.ModeLocal, a.Check(ActionClass.Control, Src("holder")).Code);
        Assert.True(a.Check(ActionClass.Stop, Src("holder")).Allowed);
        Assert.True(a.Check(ActionClass.Control, CommandSource.Local).Allowed);
        // 已是本地：不再触发 Changed
        Assert.False(a.ReturnToLocal("急停"));
        Assert.Equal(2, changed);
    }

    [Fact]
    public void CommandSource_Labels()
    {
        Assert.Equal("触屏", CommandSource.Local.Label);
        Assert.False(CommandSource.Local.IsRemote);
        var r = CommandSource.Remote("id", "LIMS");
        Assert.True(r.IsRemote);
        Assert.Equal("远程（LIMS）", r.Label);
    }
}

/// <summary>严格守卫开关。改静态开关的测试放进同一个不并行的集合。</summary>
[Collection("CommandPolicy")]
public class CommandPolicyTests
{
    [Fact]
    public void Strict_RemoteOnlyByDefault_LocalWhenSwitched()
    {
        Assert.False(CommandPolicy.Strict(CommandSource.Local));
        Assert.True(CommandPolicy.Strict(CommandSource.Remote("a", "b")));
        try
        {
            CommandPolicy.StrictLocal = true;
            Assert.True(CommandPolicy.Strict(CommandSource.Local));
        }
        finally { CommandPolicy.StrictLocal = false; }
        Assert.False(CommandPolicy.Strict(CommandSource.Local));
    }
}
