#if DEBUG
using Avalonia;

namespace ParallelReactor.Control.Shot;

/// <summary>
/// v0.8.1 设备活动同步（research/fusion-port/notes/ACT-SPEC.md §3.4）：浮动活动面板（方向 A）的各个状态 + 全局标记。
/// 参照图（样稿 main-fusion.html 加 actstyle=a 现拍，名字 final-act-*-&lt;宽&gt;.png）不在默认参照目录里：对照时把 PR_CONTROL_SHOT_REF 指到拍参照图的目录。
/// <list type="bullet">
/// <item>act-leak-run / -done / -fail / -abort：泄漏测试进行中（触屏发起，平衡）/ 全部通过 / 有不通过 + 釜内带压 / 已中止。</item>
/// <item>act-calib-wait：容积标定 RV3 等加水；act-stack：1号机泄漏测试 + 2号机标定（等人操作的展开，另一台是胶囊）。</item>
/// <item>act-collapsed（-pane：1366 时把设备面板浮出看活动行）：收起成胶囊 + 设备栏活动行 + 状态栏一项 + 气路「泄漏测试占用」。</item>
/// <item>act-runtable / act-leak-gas / act-leak-vessel / act-leak-bar：运行表 / 气路图 / 卡片 / 棒图里的泄漏测试、自整定、排队等气路的通道。</item>
/// <item>act-badges / act-badges-res：搅拌电机故障 + 触屏手动阀控（气路图标题栏的标签 / 共用资源）；act-estop-part：急停后阀门没确认（报警弹窗）。</item>
/// <item>act-abort-dlg：点「中止」后的确认框；act-perf-leak：性能测试 → 泄漏测试（进行中）；act-leak-psi：压力单位 psi。</item>
/// </list>
/// </summary>
public static partial class ShotPlan
{
    /// <summary>不用 Both：分文件的静态字段初始化顺序不定（Both 可能还是 null）。</summary>
    private static PixelSize[] Sz => new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) };

    private static readonly Spec[] PartAct =
    {
        // 曲线升温（设计稿 curve-design 方向 B）：检查器摘要 / 运行中；曲线窗口（选了 RV6、RV8）/ 带提示 / 运行中查看；停止曲线的确认
        new("curve-insp", "main", "dev=1&apop=0&ch=5&po=insp&cv=5&cvfree=2,6,8", Sz),
        new("curve-insp-run", "main", "dev=1&apop=0&ch=5&po=insp&cv=5:run&cvfree=2,6,8", Sz),
        new("curve-dlg", "main", "dev=1&apop=0&ch=5&cv=5&cvfree=2,6,8&dialog=curve:1:5:6,8", Sz),
        new("curve-dlg-warn", "main", "dev=1&apop=0&ch=5&cv=5:warn&cvfree=2,6,8&dialog=curve:1:5", Sz),
        new("curve-dlg-run", "main", "dev=1&apop=0&ch=5&cv=5:run&cvfree=2,6,8&dialog=curve:1:5", Sz),
        new("curve-stop-dlg", "main", "dev=1&apop=0&ch=5&cv=5:run&dialog=curvestop:1:5", Sz),
        new("act-leak-run", "main", "dev=1&apop=0&act=leak-run", Sz),
        new("act-leak-done", "main", "dev=1&apop=0&act=leak-done", Sz),
        new("act-leak-fail", "main", "dev=1&apop=0&act=leak-fail", Sz),
        new("act-leak-abort", "main", "dev=1&apop=0&act=leak-abort", Sz),
        new("act-calib-wait", "main", "dev=1&apop=0&act=calib-wait", Sz),
        new("act-stack", "main", "dev=1&apop=0&act=stack", Sz),
        new("act-collapsed", "main", "dev=1&apop=0&act=leak-run&actfold=1", Sz),
        new("act-collapsed-pane", "main", "dev=1&apop=0&act=leak-run&actfold=1&po=dev", new[] { new PixelSize(1366, 768) }),
        new("act-runtable", "main", "dev=1&apop=0&act=leak-run&tune=1&actfold=1", Sz),
        new("act-leak-gas", "main", "dev=1&apop=0&act=leak-run&tune=1&actfold=1&mv=gas", Sz),
        new("act-leak-vessel", "main", "dev=1&apop=0&act=leak-run&tune=1&actfold=1&mv=vessel", Sz),
        new("act-leak-bar", "main", "dev=1&apop=0&act=leak-run&tune=1&actfold=1&mv=bar", Sz),
        new("act-badges", "main", "dev=1&apop=0&stirfault=1&manual=1&mv=gas", Sz),
        new("act-badges-res", "main", "dev=1&apop=0&stirfault=1&manual=1", Sz),
        new("act-estop-part", "main", "dev=1&estoppart=1", Sz),
        new("act-abort-dlg", "main", "dev=1&apop=0&act=leak-run&axabort=1", Sz),
        new("act-perf-leak", "test", "dev=1&apop=0&act=leak-run&actfold=1&ptm=leak", Sz),
        new("act-leak-psi", "main", "dev=1&apop=0&act=leak-run&punit=psi", Sz),
    };
}
#endif
