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
/// 主页见 <see cref="HomePageVm"/>；程序 / 性能测试暂时是旧样式页面（Pages/LegacyPages.cs，F6 / F8 换新）；其余是占位（<see cref="PlaceholderPageVm"/>）。
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
}

/// <summary>
/// 还没做的页面（F2 占位）：标题 + 英文名 + 「这一页会有什么」+ 由哪个阶段做。样稿的页面内容在 research/main-fusion.html 的 PAGES.*。
/// 工具条照样稿放按钮（都只提示「下一阶段」），外框各处（设备面板第一行、设备下拉）已经按这一页的范围（<see cref="PageScope"/>）显示。
/// </summary>
public sealed class PlaceholderPageVm : PageVm
{
    private readonly ShellVm? _shell;
    private readonly List<RibbonGroup> _tools;

    public PlaceholderPageVm(AppPage page, ShellVm? shell = null) : base(page)
    {
        _shell = shell;
        var info = AppPages.Info(page);
        Title = info.Title;
        (English, Text, Stage) = page switch
        {
            AppPage.Program => ("Programs", "程序库 + 单路程序（步骤卡片、量程校验）+ 条件矩阵（8 路一起改参数）+ 预运行检查 + 下发并运行；草稿从旧程序页迁移。", "F6 程序页"),
            AppPage.Curves => ("Curves", "温度 / 压力 / 吸气量三张图上下排列、共用时间轴；通道识别色固定；十字光标一次读出所有选中通道；下方光标读数 + 区间统计；导出 CSV。", "F5 曲线页"),
            AppPage.Alarms => ("Alarms", "全部设备的当前报警与历史（按设备、优先级、时间筛选），确认不需要控制权；报警阈值只读、修改在触屏。", "F5 报警页"),
            AppPage.Records => ("Records", "跨设备的会话列表、8 路对比（吸气量、最大速率、到 90 % 用时）、统计与结果表；下载遥测 CSV、生成报告。", "F7 记录页"),
            AppPage.Settings => ("Settings", "本机（客户端名、压力单位、报警声音、无人值守 / 防睡眠）、吸气量计算、设备连接；设备参数只读显示。", "F8 设置页"),
            AppPage.Perf => ("Performance test", "测试计划、运行进度、录入外部表计读数、报告。", "F8 性能测试页"),
            _ => ("", "", ""),
        };
        _tools = PlaceholderTools(page);
    }

    public string Title { get; }
    public string English { get; }
    public string Text { get; }
    /// <summary>「F6 程序页」：哪个阶段做这一页。</summary>
    public string Stage { get; }

    public override IReadOnlyList<RibbonGroup> Tools => _tools;

    /// <summary>样稿这一页的工具条按钮（只提示，F5–F8 接上真正的动作）。</summary>
    private List<RibbonGroup> PlaceholderTools(AppPage page)
    {
        RibbonTool T(string icon, string label, bool primary = false) => new(icon, label, () => _shell?.Toast(label + "：" + Stage + "做", Services.ToastKind.Info), primary: primary);
        RibbonGroup G(string title, params RibbonTool[] tools) => new(title, tools);
        return page switch
        {
            AppPage.Program => new()
            {
                G("程序", T("f-add-c", "新建程序"), T("f-save", "保存"), T("f-copy", "复制为新程序")),
                G("文件", T("f-upload", "导入"), T("f-down", "导出")),
                G("运行", T("f-task", "预运行检查"), T("f-play", "下发并运行", primary: true)),
            },
            AppPage.Curves => new()
            {
                G("时间", T("f-pause", "暂停滚动"), T("f-reset", "回到最新")),
                G("标记", T("f-flag", "打标记")),
                G("导出", T("f-down", "导出 CSV"), T("f-copy", "复制图片")),
            },
            AppPage.Alarms => new()
            {
                G("确认", T("f-check", "确认所选"), T("f-check", "全部确认")),
                G("导出", T("f-down", "导出 CSV")),
            },
            AppPage.Records => new()
            {
                G("会话", T("f-rec", "新建会话"), T("f-stop", "结束会话"), T("f-flag", "打标记")),
                G("导出", T("f-down", "下载数据"), T("f-pdf", "生成报告"), T("f-folder", "打开数据目录")),
            },
            AppPage.Settings => new()
            {
                G("设置", T("f-save", "保存"), T("f-reset", "恢复默认")),
                G("配置", T("f-down", "导出配置"), T("f-upload", "导入配置")),
            },
            _ => new(),
        };
    }
}

/// <summary>组件陈列（设计 / 自检用，不在菜单里）。</summary>
public sealed class GalleryPageVm : PageVm
{
    public GalleryPageVm() : base(AppPage.Gallery) { }
}
