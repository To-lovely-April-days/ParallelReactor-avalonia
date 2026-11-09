#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Shot;

/// <summary>
/// 演示模式（PR_CONTROL_DEMO=1，仅 Debug）：不创建 ConnectionService，不连接任何设备。
///  · 有 PR_CONTROL_SHOT=目录：按 ShotPlan 逐张截图（PR_CONTROL_SHOT_SIZE=宽x高，每个尺寸一个进程；PR_CONTROL_SHOT_NAMES=逗号列表可选），
///    每张之后跑 ShotAudit 写 &lt;name&gt;-&lt;w&gt;.audit.txt（干净时为空文件）；参照目录（PR_CONTROL_SHOT_REF 或 PR_CONTROL_SHOT_COMPARE）
///    里有 final-&lt;name&gt;-&lt;w&gt;.png 时拼左右对照 cmp-&lt;name&gt;-&lt;w&gt;.png。审计有问题退出码 1；异常写 error.txt、退出码 2。
///  · PR_CONTROL_AUDIT_SELFTEST=1：只渲染故意做坏的自检视图（AuditSelfTest），写 audit-selftest-&lt;w&gt;.{png,audit.txt,result.txt}；漏报或误报时退出码 3。
///  · PR_CONTROL_FONT_PROBE=1：字体探针（font-probe.txt + font-probe-&lt;w&gt;.png）。
///  · PR_CONTROL_ICON_SHEET=1：图标核对表（icon-sheet-&lt;w&gt;.png，与样稿同样排布的 snap 参照对照）。
///  · PR_CONTROL_THEME_SHEET=1：主题核对表（Shot/ThemeSheet：各主题的悬停 / 按下 / 焦点 / 禁用 / 原因状态、菜单、弹出、列表、页签、对话框、
///    BarFitPanel / FitText / SizeClasses / Keep 断行）+ 审计 + 无头点击的行为核对（theme-sheet-&lt;w&gt;.{png,audit.txt,checks.txt}，
///    真正弹出的下拉 / 子菜单 / 提示框另存 theme-popup-*.png）；有 FAIL 时退出码 4。
///  · 没有 PR_CONTROL_SHOT：打开普通窗口显示组件陈列页（设计用）。
/// 截图时页面根元素带 frozen（Theme/Frozen.cs）：没有过渡和动画。
/// </summary>
public static partial class ShotRunner
{
    public static bool IsDemo => Flag("PR_CONTROL_DEMO");

    private static bool Flag(string name) => (Environment.GetEnvironmentVariable(name) ?? "").Trim() is "1" or "true";

    private static PixelSize? ParseSize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Trim().ToLowerInvariant().Split('x');
        return parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) && w > 0 && h > 0 ? new PixelSize(w, h) : null;
    }

    public static void StartDemo(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var dir = Environment.GetEnvironmentVariable("PR_CONTROL_SHOT");
        bool shot = !string.IsNullOrWhiteSpace(dir);
        var size = ParseSize(Environment.GetEnvironmentVariable("PR_CONTROL_SHOT_SIZE")) ?? new PixelSize(1440, 900);

        Window window;
        if (Flag("PR_CONTROL_FONT_PROBE")) window = new Window { Content = FontProbe.Sheet(), Title = "字体探针" };
        else if (Flag("PR_CONTROL_AUDIT_SELFTEST")) window = PlainWindow("审计自检", AuditSelfTest.Build());
        else if (Flag("PR_CONTROL_ICON_SHEET")) window = PlainWindow("图标核对", IconSheet.Build());
        else if (Flag("PR_CONTROL_THEME_SHEET")) window = PlainWindow("主题核对", ThemeSheet.Build());
        else window = new GalleryWindow();

        window.Width = size.Width; window.Height = size.Height;
        if (shot)
        {
            window.SystemDecorations = SystemDecorations.None;
            window.CanResize = false;
            window.Opened += (_, _) => Dispatcher.UIThread.Post(() => _ = RunDemoAsync(window, desktop, dir!, size), DispatcherPriority.Background);
        }
        desktop.MainWindow = window;
    }

    /// <summary>新令牌的普通窗口（UiFont、15 px、t-2 字、bg-card 底）。</summary>
    private static Window PlainWindow(string title, Avalonia.Controls.Control content)
    {
        var w = new Window { Title = title, Content = content, FontSize = 15, Foreground = Palette.T2Brush, Background = Palette.BgCardBrush };
        if (Application.Current!.TryFindResource("UiFont", out var f) && f is FontFamily ff) w.FontFamily = ff;
        return w;
    }

    private static async Task RunDemoAsync(Window window, IClassicDesktopStyleApplicationLifetime desktop, string dir, PixelSize size)
    {
        int code = 0;
        var log = new StringBuilder();
        try
        {
            Directory.CreateDirectory(dir);
            log.AppendLine($"demo size={size.Width}x{size.Height} font={UiFonts.Stack()}");
            foreach (var key in new[] { "UiFont", "ShadowCard", "Ease", "Radius", "FsBody", "BgAppBrush" })
                log.AppendLine($"  resource {key} = {(Application.Current!.TryFindResource(key, out var rv) ? rv?.GetType().Name + " " + rv : "(missing)")}");
            await SettleAsync();

            if (Flag("PR_CONTROL_FONT_PROBE"))
            {
                FontProbe.Write(dir);
                Capture(window, Path.Combine(dir, $"font-probe-{size.Width}.png"), log);
                return;
            }

            if (Flag("PR_CONTROL_ICON_SHEET"))
            {
                Capture(window, Path.Combine(dir, $"icon-sheet-{size.Width}.png"), log);
                return;
            }

            if (Flag("PR_CONTROL_AUDIT_SELFTEST"))
            {
                code = SelfTest(window, dir, size, log);
                return;
            }

            if (Flag("PR_CONTROL_THEME_SHEET"))
            {
                var stem = $"theme-sheet-{size.Width}";
                Capture(window, Path.Combine(dir, stem + ".png"), log);
                var issues = ShotAudit.Run(window);
                File.WriteAllText(Path.Combine(dir, stem + ".audit.txt"), issues.Count == 0 ? "" : string.Join("\n", issues) + "\n");
                log.AppendLine($"audit {stem}: {issues.Count} issue(s) · {ShotAudit.LastStats}");
                var (fails, lines) = await ThemeSheet.CheckAsync(window, dir, size.Width);
                File.WriteAllText(Path.Combine(dir, stem + ".checks.txt"), string.Join("\n", lines) + "\n");
                log.AppendLine($"checks {stem}: {fails} fail(s)");
                code = fails > 0 ? 4 : issues.Count > 0 ? 1 : 0;
                return;
            }

            var specs = ShotPlan.Select(size, ShotPlan.NamesFromEnv());
            if (specs.Count == 0) throw new InvalidOperationException($"截图计划里没有 {size.Width}x{size.Height} 的条目（用 PR_CONTROL_SHOT_NAMES 指定名字）");
            var refDir = Environment.GetEnvironmentVariable("PR_CONTROL_SHOT_REF");
            if (string.IsNullOrWhiteSpace(refDir)) refDir = Environment.GetEnvironmentVariable("PR_CONTROL_SHOT_COMPARE");

            foreach (var spec in specs)
            {
                Show(window, spec, size);
                await SettleAsync();
                var stem = $"{spec.Name}-{size.Width}";
                var png = Path.Combine(dir, stem + ".png");
                Capture(window, png, log);
                Diagnostics(window, log);

                var issues = ShotAudit.Run(window);
                File.WriteAllText(Path.Combine(dir, stem + ".audit.txt"), issues.Count == 0 ? "" : string.Join("\n", issues) + "\n");
                log.AppendLine($"audit {stem}: {issues.Count} issue(s) · {ShotAudit.LastStats}");
                if (issues.Count > 0) code = 1;

                var refPng = string.IsNullOrWhiteSpace(refDir) ? null : Path.Combine(refDir, spec.RefFile(size.Width));
                if (refPng != null && File.Exists(refPng) && File.Exists(png))
                {
                    var cmp = Path.Combine(dir, $"cmp-{stem}.png");
                    var cs = ShotCompare.Write(refPng, png, cmp, $"样稿 {Path.GetFileName(refPng)}（WebKit）", $"Avalonia {stem}.png（Skia）");
                    log.AppendLine($"compare {cmp} {cs.Width}x{cs.Height}");
                }
                else log.AppendLine($"compare {stem}: 没有参照图（{refPng ?? "未设置 PR_CONTROL_SHOT_REF"}）");
            }
        }
        catch (Exception ex)
        {
            code = 2;
            log.AppendLine("ERROR: " + ex);
            try { File.WriteAllText(Path.Combine(dir, "error.txt"), ex.ToString()); } catch { }
        }
        finally
        {
            try { File.WriteAllText(Path.Combine(dir, "shot.log"), log.ToString()); } catch { }
            desktop.Shutdown(code);
        }
    }

    /// <summary>按路由显示截图内容。M1 只有 gallery（GalleryWindow）；M2 起由新外壳的路由处理。</summary>
    private static void Show(Window window, ShotPlan.Spec spec, PixelSize size)
    {
        switch (spec.Route)
        {
            case "gallery" when window is GalleryWindow gw:
                gw.SetFrame(spec.Params.TryGetValue("frame", out var f) ? f : null, size.Width);
                Frozen.Apply(gw.Page);
                break;
            default:
                throw new NotSupportedException($"演示路由「{spec.Route}」还没有实现（M1 只有 gallery）");
        }
    }

    /// <summary>截图后的几条事实（写进 shot.log）：Fluent 覆盖是否生效、num 类是否带上 +tnum、页面是否冻结。</summary>
    private static void Diagnostics(Window window, StringBuilder log)
    {
        string R(string key) => window.TryFindResource(key, window.ActualThemeVariant, out var v)
            ? v is ISolidColorBrush b ? $"{b.Color}" : $"{v}" : "(missing)";
        log.AppendLine($"  Fluent 覆盖：SystemAccentColor = {R("SystemAccentColor")} · AccentButtonBackground = {R("AccentButtonBackground")}"
                       + $" · CheckBoxCheckBackgroundFillChecked = {R("CheckBoxCheckBackgroundFillChecked")} · ControlContentThemeFontSize = {R("ControlContentThemeFontSize")}");
        var num = window.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains("num"));
        if (num != null) log.AppendLine($"  first .num TextBlock \"{num.Text}\" FontFeatures = {string.Join(" ", num.FontFeatures?.Select(f => f.ToString()) ?? Array.Empty<string>())}");
        var frozen = window.GetVisualDescendants().OfType<StyledElement>().FirstOrDefault(e => e.Classes.Contains("frozen"));
        log.AppendLine($"  frozen root = {(frozen == null ? "(none)" : frozen.GetType().Name)} · IsFrozen = {(frozen == null ? false : Frozen.GetIsFrozen(frozen))}");
    }

    private static int SelfTest(Window window, string dir, PixelSize size, StringBuilder log)
    {
        var stem = $"audit-selftest-{size.Width}";
        Capture(window, Path.Combine(dir, stem + ".png"), log);
        var issues = ShotAudit.Run(window);
        File.WriteAllText(Path.Combine(dir, stem + ".audit.txt"), string.Join("\n", issues) + "\n");
        var (missing, falsePos) = AuditSelfTest.Verify(issues);
        var sb = new StringBuilder();
        foreach (var e in AuditSelfTest.Expected) sb.AppendLine((missing.Any(m => m.StartsWith(e.What, StringComparison.Ordinal)) ? "FAIL 漏报 " : "PASS 报出 ") + e.What);
        foreach (var e in AuditSelfTest.MustNot) sb.AppendLine((falsePos.Any(m => m.StartsWith(e.What, StringComparison.Ordinal)) ? "FAIL 误报 " : "PASS 未报 ") + e.What);
        sb.AppendLine(missing.Count == 0 && falsePos.Count == 0 ? "RESULT PASS" : $"RESULT FAIL（漏报 {missing.Count}，误报 {falsePos.Count}）");
        File.WriteAllText(Path.Combine(dir, stem + ".result.txt"), sb.ToString());
        log.AppendLine($"selftest: {issues.Count} issue(s), missing={missing.Count}, false={falsePos.Count} · {ShotAudit.LastStats}");
        return missing.Count == 0 && falsePos.Count == 0 ? 0 : 3;
    }
}
#endif
