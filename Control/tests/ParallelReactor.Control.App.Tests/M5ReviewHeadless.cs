#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Pages;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// M5 复核的无头界面测试共用的一套无头 Avalonia（只在 Debug 构建里：App 只在 Debug 里引用 Avalonia.Headless / Skia）。同截图：Skia 真排字，字体同 App 的 BuildAvaloniaApp（内嵌 Inter + 苹方）。
/// <list type="bullet">
/// <item>整个测试进程只起一个 <see cref="HeadlessUnitTestSession"/>（一条专用的 UI 线程）。每个测试体在它上面跑，各自一个新的 App 与定位器作用域；
///   跑完 UI 调度器复位，测试体之外 <c>Dispatcher.UIThread</c> 仍是没起平台时的空实现（不认线程），别的测试在线程池上创建 Avalonia 对象不受影响。</item>
/// <item>只有这一套：以前 g2、g5 各起一套（全局的 SetupWithoutStarting 与无头会话），同一进程里先起的那套把渲染循环挂在全局上，
///   后起的会话画不出帧、命中测试不工作 —— 不并行集合的先后是随机的。</item>
/// <item>这些测试在同一个不并行的集合里：Avalonia 的平台与 UI 线程是进程级的。xunit 等并行的集合都跑完再跑（与另一个不并行的集合 Env 先后随机）。</item>
/// </list>
/// </summary>
[CollectionDefinition(Collection, DisableParallelization = true)]
public sealed class M5ReviewHeadless
{
    public const string Collection = "M5 复核 · 无头 Avalonia";

    private static readonly Lazy<HeadlessUnitTestSession> Session =
        new(() => HeadlessUnitTestSession.StartNew(typeof(M5ReviewHeadlessApp)), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>在无头 Avalonia 的 UI 线程上跑 <paramref name="body"/>，跑完把排着的活做完（断言失败照常从返回的任务里抛出）。</summary>
    public static Task RunAsync(Action body) =>
        Session.Value.Dispatch(() =>
        {
            body();
            Dispatcher.UIThread.RunJobs();
        }, CancellationToken.None);

    /// <summary>同 <see cref="RunAsync"/>，等它跑完（最多 2 分钟）。</summary>
    public static void Run(Action body)
    {
        var t = RunAsync(body);
        if (!((IAsyncResult)t).AsyncWaitHandle.WaitOne(TimeSpan.FromMinutes(2))) throw new TimeoutException("无头测试 2 分钟没跑完");
        t.GetAwaiter().GetResult();
    }

    /// <summary>往演示设备的程序库里加一个程序（演示数据的程序库按型号固定，只能这样加；字段改名时这里先失败）。</summary>
    public static void AddToLibrary(DemoDevice d, ProgramDef def)
    {
        var f = typeof(DemoDevice).GetField("ProgramList", BindingFlags.Instance | BindingFlags.NonPublic);
        var list = Assert.IsType<List<ProgramDef>>(f?.GetValue(d));
        list.Add(def);
    }

    /// <summary>
    /// 放面板的窗口：字符串内容同外框（ShellView 的 DataTemplate：按钮的字是 PrText，按 WebKit 量宽 ——「下发」30 px 宽，普通的 AccessText 是 32）。
    /// </summary>
    public static Window ShellWindow(Avalonia.Controls.Control content, double width, double height)
    {
        var win = new Window { Width = width, Height = height, Content = content };
        win.DataTemplates.Add(new FuncDataTemplate<string>((_, _) => new PrText { [!TextBlock.TextProperty] = new Binding(".") }, supportsRecycling: true));
        return win;
    }

    /// <summary>程序库一行的选择按钮（按命令认）。</summary>
    public static Button PickButton(Visual root, LibRowVm row) =>
        root.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, row.PickCommand));

    /// <summary>程序库一行的「下发」。</summary>
    public static Button LoadButton(Visual root, LibRowVm row) =>
        root.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, row.LoadCommand));

    /// <summary>按钮右端往里 3 px、纵向居中的点（窗口坐标）。</summary>
    public static Point RightEnd(Window win, Visual v)
    {
        var m = v.TransformToVisual(win) ?? throw new InvalidOperationException("不在窗口里");
        return new Point(v.Bounds.Width - 3, v.Bounds.Height / 2).Transform(m);
    }

    /// <summary>真的点一下（经过命中测试）：按钮右端往里 3 px。</summary>
    public static void ClickRight(Window win, Visual v)
    {
        var pt = RightEnd(win, v);
        win.MouseMove(pt, RawInputModifiers.None);
        win.MouseDown(pt, MouseButton.Left, RawInputModifiers.None);
        win.MouseUp(pt, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }
}

/// <summary>无头会话的入口（HeadlessUnitTestSession 找 BuildAvaloniaApp）：同截图（Skia 真排字，Inter + 苹方）。</summary>
public static class M5ReviewHeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}
#endif
