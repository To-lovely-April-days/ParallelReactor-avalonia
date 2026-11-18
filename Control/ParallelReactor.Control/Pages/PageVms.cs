using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Pages;

/// <summary>
/// 页面视图模型的基类（融合版 F2）：每种页面一个实例（ShellVm 按 <see cref="AppPage"/> 缓存，PageHost 给每个一份视图，换页面时滚动位置和输入不丢）。
/// 页面接到外壳的地方：
/// <list type="bullet">
/// <item><see cref="Tools"/>：工具条右侧的按钮（分组，见 Shell/PageTools.cs）。页面持有这些对象、就地改原因 / 文字；组变了（比如换了模式）时 <see cref="ToolsChanged"/>。</item>
/// <item><see cref="Refresh"/>：设备数据或路由（页面 / 设备 / 通道，<see cref="ShellVm.Nav"/>）变了。</item>
/// <item><see cref="OwnedAlarms"/> / <see cref="ShowsAlarmLog"/> / <see cref="ShowsReconnect"/>：同一个动作屏幕上只放一处（检查器读它们）。</item>
/// </list>
/// 主页见 <see cref="HomePageVm"/>；程序（F6）、曲线 / 报警（F5）、记录（F7）、性能测试 / 设置（F8）各在 Pages/&lt;页名&gt;/。
/// </summary>
public abstract partial class PageVm : ObservableObject, IDisposable
{
    protected PageVm(AppPage page) => Page = page;

    public AppPage Page { get; }

    /// <summary>没有页面自己的「确认」。</summary>
    public static readonly IReadOnlyDictionary<string, string> NoOwnedAlarms = new Dictionary<string, string>();

    /// <summary>页面上已经有「确认」按钮的报警（键「k:报警 id」→ 位置）。检查器里的这条报警不再放第二个按钮。F2 的页面都没有。</summary>
    public virtual IReadOnlyDictionary<string, string> OwnedAlarms => NoOwnedAlarms;

    /// <summary>页面上有这台设备的「报警与事件」：检查器的设备正文不再列报警 / 最近事件。</summary>
    public virtual bool ShowsAlarmLog => false;

    /// <summary>页面上有这台设备的断线说明条（带「立即重连」，F3 的断线横条）：检查器的控制权卡不再放第二个「立即重连」。</summary>
    public virtual bool ShowsReconnect => false;

    /// <summary>工具条右侧的按钮组（样稿 PAGES[p].ribbon；主页 = mainTools）。默认没有。</summary>
    public virtual IReadOnlyList<RibbonGroup> Tools => Array.Empty<RibbonGroup>();

    /// <summary>按钮组换了（不是某个按钮的状态变了，那个按钮自己通知）。</summary>
    public event EventHandler? ToolsChanged;

    protected void RaiseToolsChanged() => ToolsChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>设备数据或路由变了（就地刷新）。</summary>
    public virtual void Refresh(IFleetView fleet) { }

    /// <summary>不在显示时也跟着数据刷新（默认）。重的页面可以为 false：切回来时（ShellVm.Go）先刷新一次再显示。</summary>
    public virtual bool RefreshWhenHidden => true;

    /// <summary>页面被释放（退订事件等）。</summary>
    public virtual void Dispose() { }

    /// <summary>
    /// 页面自己的地址参数（截图 / 演示，ShellVm.ApplyDemoParams 换到这一页之后调；F5 起：报警页 alsel / alf…、曲线页 cvr / cvv…）。默认不认任何参数。
    /// </summary>
    public virtual void ApplyParams(Func<string, string?> param) { }

    /// <summary>回到刚打开时的样子（ShellVm.ResetView：每张截图前）：筛选、选中、时间范围等页面状态还原。默认什么都不做。</summary>
    public virtual void ResetView() { }
}

/// <summary>组件陈列（设计 / 自检用，不在菜单里）。</summary>
public sealed class GalleryPageVm : PageVm
{
    public GalleryPageVm() : base(AppPage.Gallery) { }
}
