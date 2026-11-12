using Avalonia;
using ParallelReactor.Control.Shell;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// M6 外框布局随窗口尺寸变（样稿 shell.js 的 resize 只 fit + applyLayout，资源管理器的模式要等下一次导航才重算）：
/// <list type="bullet">
/// <item>没动过开关：随宽度立刻停靠 / 收起（样稿要等导航；这里立刻改，结果同导航之后）。</item>
/// <item>1366 打开的浮层，窗口变宽到 ≥ 1600：改成停靠 300（样稿留着 320 的浮层和遮罩，下一次导航才停靠 —— 这里直接停靠，宽窗口上不留浮层）。</item>
/// <item>1920 上用户停靠的资源管理器，窗口变窄：收起，导航（CloseFloat）之后照样收着（样稿下一次导航会弹成浮层）；再宽回来又停靠。</item>
/// </list>
/// </summary>
public class M6ShellLayoutTests
{
    [Fact]
    public void FloatOpenedNarrow_DocksWhenTheWindowGetsWide()
    {
        var l = new ShellLayout();
        l.Update(new Size(1366, 768));
        Assert.False(l.ExplorerOpen);
        l.ToggleExplorer();
        Assert.True(l.ExplorerFloat);
        Assert.Equal(0, l.ExplorerColumn.Value);

        l.Update(new Size(1920, 1080));
        Assert.True(l.ExplorerDocked);
        Assert.False(l.ExplorerFloat);
        Assert.True(l.ExplorerOpen);
        Assert.Equal(ShellLayout.ExplorerW, l.ExplorerColumn.Value);
        Assert.Equal(ExplorerMode.Docked, l.Mode);

        // 再变窄：收起（不弹回浮层），图标栏「设备」再点一次是浮层
        l.Update(new Size(1366, 768));
        Assert.False(l.ExplorerOpen);
        Assert.False(l.ExplorerFloat);
        l.ToggleExplorer();
        Assert.True(l.ExplorerFloat);
    }

    [Fact]
    public void UntouchedExplorer_FollowsTheWidthAtOnce()
    {
        var l = new ShellLayout();
        l.Update(new Size(1366, 768));
        Assert.False(l.ExplorerOpen);
        l.Update(new Size(1920, 1080));
        Assert.True(l.ExplorerDocked);
        l.Update(new Size(1366, 768));
        Assert.False(l.ExplorerOpen);
    }

    [Fact]
    public void UserDockedWide_HidesWhenNarrow_StaysHiddenOnNavigation_DocksAgainWhenWide()
    {
        var l = new ShellLayout();
        l.Update(new Size(1920, 1080));
        l.ToggleExplorer();
        l.ToggleExplorer();                       // 用户收起再打开：停靠（来自用户）
        Assert.True(l.ExplorerDocked);
        l.Update(new Size(1366, 768));
        Assert.False(l.ExplorerOpen);
        l.CloseFloat();                           // 导航
        Assert.False(l.ExplorerOpen);
        Assert.False(l.ExplorerFloat);
        l.Update(new Size(1920, 1080));
        Assert.True(l.ExplorerDocked);
    }

    [Fact]
    public void Float_DocksAtExactly1600_NotBelow()
    {
        var l = new ShellLayout();
        l.Update(new Size(1366, 768));
        l.ToggleExplorer();
        l.Update(new Size(1599, 900));            // 还是窄屏：浮层留着
        Assert.True(l.ExplorerFloat);
        l.Update(new Size(1600, 900));            // 正好 1600：停靠
        Assert.True(l.ExplorerDocked);
        Assert.False(l.ExplorerFloat);
        Assert.Equal(ShellLayout.ExplorerW, l.ExplorerColumn.Value);
    }

    [Fact]
    public void Float_InFocusMode_DocksHiddenUntilFocusEnds()
    {
        var l = new ShellLayout();
        l.Update(new Size(1366, 768));
        l.ToggleExplorer();
        l.ToggleFocus();                          // 专注模式：资源管理器收起
        l.Update(new Size(1920, 1080));
        Assert.False(l.ExplorerOpen);
        l.ToggleFocus();                          // 退出专注：停靠（不是浮层）
        Assert.True(l.ExplorerDocked);
        Assert.False(l.ExplorerFloat);
    }
}
