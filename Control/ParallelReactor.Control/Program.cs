using System;
using Avalonia;

namespace ParallelReactor.Control;

internal static class Program
{
    /// <summary>
    /// 桌面入口。PR_CONTROL_HEADLESS=1（仅 Debug）：Avalonia.Headless + Skia 真渲染，窗口尺寸 PR_CONTROL_SHOT_SIZE（默认 1440x900），
    /// 配合 PR_CONTROL_SHOT=&lt;目录&gt; 跑 PR_CONTROL_SCENARIO 脚本并截图后退出（见 Shot/ShotRunner.cs）。无环境变量 → 普通窗口。
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        // 进程级兜底：任何漏网异常至少留痕到 stderr，不静默崩溃（命令级 try/catch 仍是第一道）
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Console.Error.WriteLine("[Control] 未处理异常: " + e.ExceptionObject);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) => { Console.Error.WriteLine("[Control] 未观察的任务异常: " + e.Exception); e.SetObserved(); };
        bool headless = false;
#if DEBUG
        headless = (Environment.GetEnvironmentVariable("PR_CONTROL_HEADLESS") ?? "") is "1" or "true";
        // 截图计划（tools/shots.sh 用：每个尺寸开拍前删掉这些名字上一次留下的截图；tools/snap-refs.sh 用：现拍样稿参照图）：
        // 一行一张「名字 路由 尺寸,尺寸 [参数]」，不启动界面
        if ((Environment.GetEnvironmentVariable("PR_CONTROL_SHOT_PLAN") ?? "") is "1" or "true")
        {
            foreach (var s in Shot.ShotPlan.All)
                Console.WriteLine((s.Name + " " + s.Route + " " + string.Join(",", System.Linq.Enumerable.Select(s.Sizes, z => z.Width + "x" + z.Height)) + " " + s.Query).TrimEnd());
            return;
        }
#endif
        // PC 端 1:1 渲染，不设全局缩放；PR_CONTROL_UI_SCALE 可选覆盖（默认 1.0）
        var scale = Environment.GetEnvironmentVariable("PR_CONTROL_UI_SCALE");
        if (!string.IsNullOrWhiteSpace(scale) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR")))
            Environment.SetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR", scale.Trim());

#if DEBUG
        if (headless)
        {
            Avalonia.Headless.AvaloniaHeadlessPlatformExtensions.UseHeadless(
                    BuildAvaloniaApp().UseSkia(),
                    new Avalonia.Headless.AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .StartWithClassicDesktopLifetime(args);
            return;
        }
#endif
        BuildAvaloniaApp().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .WithInterFont()
            .LogToTrace();
}
