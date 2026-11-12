#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;

namespace ParallelReactor.Control.Shot;

public static partial class ShotPlan
{
    /// <summary>
    /// M6 第 5 组加的截图（状态集，PORT-PLAN §5「M6」的验证与 §5.9「状态集」；名字不与别的组重复，截图准备 Setup 以「m6g5-」开头，见 ShotRunner.M6G5.cs）。
    /// 参照图一律用 snap 现拍样稿同一个地址（+ bare=1&amp;freeze=1，有准备的配同样的 js 参数），final-&lt;名&gt;-&lt;宽&gt;.png：
    /// <list type="bullet">
    /// <item>新的状态组合：心跳中断 / 断线的设备页、急停 + 断线（检查器红框、设备页两条说明条、反应釜页）、急停对话框里已急停的设备、
    ///   4 台时的全部停止、反应釜页的申请中。</item>
    /// <item>连接的两个子状态（正在重连… / 用户断开）与报警确认之后：样稿没有地址参数，准备时对演示数据做同样的动作（样稿 js 参数 PR.sim.act）。</item>
    /// <item>「不是当前页签」提示条（反应釜页 1/1 上选中 3号机 · RV11）。</item>
    /// <item>原有状态截图补的尺寸（名字后加 -m6：1600 / 1366 的检查器 300、浮层资源管理器；对话框 1366）。</item>
    /// <item>最坏值的状态与对话框（名字后加 -s1 / -s2：stress=1 / 2 的长别名、长文字）。</item>
    /// <item>不冻结的实时走查（live-*：截图前推进 N 秒，带着渲染的界面每秒刷新一次，再冻结、截图、审计；每秒的耗时写 &lt;名&gt;-&lt;宽&gt;.live.txt）。</item>
    /// <item>面板的放大 / 关闭（pn-*：同样稿点面板标题栏的 □ / ×；全部页面、每块面板的放大 / 关闭 / 还原审计在 ShellChecks.M6G5）。</item>
    /// </list>
    /// 样稿的 rv-off311（断线设备的反应釜页签的报警点）是第 4 组的 rv-offalarm311，这里不重复。
    /// </summary>
    private static IEnumerable<Spec> M6G5Shots()
    {
        var big = new[] { new PixelSize(1920, 1080) };
        var bigSmall = new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) };
        var narrow = new[] { new PixelSize(1600, 900), new PixelSize(1366, 768) };
        var mid = new[] { new PixelSize(1600, 900) };
        var small = new[] { new PixelSize(1366, 768) };

        var list = new List<Spec>
        {
            // ———— 新的状态组合 ————
            // 心跳中断（宽限）的设备页：头部徽标「控制权心跳中断 · 宽限 18 s」；断线的设备页：断线说明条（读数停在…）+ 共用资源 / 设备信息去色
            new("dv-lost1", "device/1", "lease=1:lost", bigSmall),
            new("dv-off1", "device/1", "offline=1", bigSmall),
            // 断线前已急停（急停 + 断线一起）：检查器红框「断线前已急停」、设备页断线条 + 红色急停条 + 共用资源「断线前已急停」、反应釜页状态条
            new("st-estopoff3", "overview", "estop=3&offline=3&sel=3", bigSmall),
            new("dv-estopoff3", "device/3", "estop=3&offline=3", bigSmall),
            new("rv-estopoff311", "reactor/3/11", "estop=3&offline=3&sel=3/11", bigSmall),
            // 急停对话框里已经急停的设备：那一行灰、「已在急停 · 需在触屏复位」、不预选；只预选一台而它已急停时说明「2号机 已在急停 · 可勾选其它设备」
            new("dlg-estop-est3", "overview", "estop=3&dialog=estop", bigSmall),
            new("dlg-estop2est", "overview", "estop=2&dialog=estop:2", bigSmall),
            // 4 台时的全部停止（4号机断线：那一行灰、写明原因）；反应釜页上的申请控制权中（状态条 + 检查器倒计时）
            new("dlg-stopall4", "overview", "devs=4&dialog=stopall", bigSmall),
            new("rv-req21", "reactor/2/1", "lease=2:requested&sel=2/1", bigSmall),

            // ———— 连接的子状态（样稿 connInfo：正在重连… / 已断开）————
            // 4号机断线之后点了「立即重连」（冻结时一直停在正在重连…）：资源管理器、总览分区、检查器、时间轴组头、状态栏；设备页说明条；反应釜页状态条
            new("st-conn4", "overview", "devs=4&sel=4", bigSmall, Setup: "m6g5-reconnect:4"),
            new("dv-conn4", "device/4", "devs=4", bigSmall, Setup: "m6g5-reconnect:4"),
            new("rv-conn41", "reactor/4/1", "devs=4&sel=4/1", bigSmall, Setup: "m6g5-reconnect:4"),
            // 1号机被用户断开（「已断开 · 不会自动重连」+「连接」，控制权已释放）
            new("st-disc1", "overview", "sel=1", bigSmall, Setup: "m6g5-disconnect:1"),
            new("dv-disc1", "device/1", "", bigSmall, Setup: "m6g5-disconnect:1"),
            new("rv-disc11", "reactor/1/1", "", bigSmall, Setup: "m6g5-disconnect:1"),

            // ———— 报警确认之后（3号机 RV11 的超压报警：反应釜页状态条「✓ 已确认」、设备页灰卡、总览顶部报警条去掉、
            //      检查器（选中 3号机，正文滚到「报警」分区：顶部在正文顶部下面 8 px）的卡片 70 %「报警 · 已确认」、没有「确认」）————
            new("rv-ack311", "reactor/3/11", "sel=3/11", bigSmall, Setup: "m6g5-ack:3"),
            new("dv-ack3", "device/3", "", bigSmall, Setup: "m6g5-ack:3"),
            new("ov-ack3", "overview", "", bigSmall, Setup: "m6g5-ack:3"),
            new("st-ack3", "overview", "sel=3", bigSmall, Setup: "m6g5-ackinsp:3"),

            // ———— 「不是当前页签 · 回到 …」提示条：1号机 · RV1 页签上单击了 3号机 RV11（1366 / 1600：图标单独一行、句子折两行、「回到 …」再一行）————
            new("insp-other", "reactor/1/1", "", Screens, Setup: "m6g5-sel:3/11"),
            new("insp-other-s1", "reactor/1/1", "stress=1", Screens, Setup: "m6g5-sel:3/16"),
            new("insp-other-s2", "reactor/1/1", "stress=2", Screens, Setup: "m6g5-sel:3/16"),

            // ———— 面板的放大 / 关闭（同样稿点标题栏的 □ / ×）————
            new("pn-ovmax1", "overview", "", Screens, Setup: "m6g5-panel:max:0"),
            // 一台设备的总览：分区下面的「温度趋势」放大（第 2 块面板）
            new("pn-ovtrendmax", "overview", "devs=1", Screens, Setup: "m6g5-panel:max:1"),
            new("pn-dvx1", "device/1", "", Screens, Setup: "m6g5-panel:close:0"),
            new("pn-dvmax1", "device/1", "", Screens, Setup: "m6g5-panel:max:0"),
            new("pn-dvmax3", "device/3", "", Screens, Setup: "m6g5-panel:max:last"),
            new("pn-rvmaxv11", "reactor/1/1", "", Screens, Setup: "m6g5-panel:max:1"),
            new("pn-rvxv11", "reactor/1/1", "", Screens, Setup: "m6g5-panel:close:1"),
        };

        // ———— 原有状态截图补的尺寸（名字后加 -m6；1920 的在 ShotPlan.cs）————
        (string Name, string Route, string Query, PixelSize[] Sizes)[] more =
        {
            ("st-lost1", "overview", "lease=1:lost", narrow),
            ("st-estop1", "overview", "estop=1", narrow),
            ("st-off3", "overview", "offline=3", narrow),
            ("ov-lease2req", "overview", "lease=2:requested", narrow),
            ("ov-alarm21", "overview", "alarm=2:1", narrow),
            ("st-estop3", "overview", "estop=3", mid),
            ("st-lease2req", "device/2", "lease=2:requested", mid),
            ("st-off1", "overview", "offline=1", mid),
            ("st-tl0", "overview", "tl=0", mid),
            ("st-devs4", "overview", "devs=4", mid),
            ("st-x0", "overview", "explorer=0", mid),
            ("st-un16", "overview", "sel=1/6", narrow),
            ("dlg-add", "overview", "dialog=add", small),
            ("dlg-lease2", "device/2", "dialog=lease:2", small),
            ("dlg-estop", "overview", "dialog=estop", small),
            ("dlg-lease2req", "device/2", "lease=2:requested&dialog=lease:2", small),
            ("dlg-lease1", "overview", "dialog=lease:1", small),
            ("dlg-lease1lost", "overview", "lease=1:lost&dialog=lease:1", small),
            ("dlg-lease4off", "overview", "devs=4&dialog=lease:4", small),
            ("dlg-estop4off", "overview", "devs=4&dialog=estop:4", small),
        };
        foreach (var m in more) list.Add(new Spec(m.Name + "-m6", m.Route, m.Query, m.Sizes));

        // ———— 最坏值（stress=1：8 个汉字的别名；stress=2：16 个字母的别名）的状态集与对话框（名字后加 -s1 / -s2）————
        (string Name, string Route, string Query, PixelSize[] Sizes)[] stress =
        {
            ("st-lease2req", "device/2", "lease=2:requested", Screens),
            ("st-lost1", "overview", "lease=1:lost", Screens),
            ("st-off1", "overview", "offline=1", Screens),
            ("st-estop1", "overview", "estop=1", Screens),
            ("st-estop3", "overview", "estop=3", Screens),
            ("ov-lease2req", "overview", "lease=2:requested", Screens),
            ("ov-alarm21", "overview", "alarm=2:1", Screens),
            ("dv-alarm21", "device/2", "alarm=2:1", Screens),
            ("rv-alarm21", "reactor/2/1", "alarm=2:1", Screens),
            ("st-tl0", "overview", "tl=0", Screens),
            ("st-un16", "overview", "sel=1/6", Screens),
            ("st-x1", "overview", "explorer=1", small),
            ("dlg-lease2", "device/2", "dialog=lease:2", bigSmall),
            ("dlg-estop", "overview", "dialog=estop", bigSmall),
            ("dlg-devices", "overview", "dialog=devices", bigSmall),
            ("dlg-stopall", "overview", "dialog=stopall", bigSmall),
            ("dlg-rename2", "overview", "dialog=rename:2", bigSmall),
            ("dlg-remove1", "overview", "dialog=remove:1", bigSmall),
            ("dlg-disconnect1", "overview", "dialog=disconnect:1", bigSmall),
            ("dlg-edit2", "overview", "dialog=edit:2", bigSmall),
            ("dlg-estop2", "overview", "dialog=estop:2", bigSmall),
            ("dlg-lease2req", "device/2", "lease=2:requested&dialog=lease:2", bigSmall),
            ("dlg-lease1", "overview", "dialog=lease:1", bigSmall),
            ("dlg-lease1lost", "overview", "lease=1:lost&dialog=lease:1", bigSmall),
            ("dlg-lease4off", "overview", "devs=4&dialog=lease:4", bigSmall),
            ("dlg-estop4off", "overview", "devs=4&dialog=estop:4", bigSmall),
        };
        foreach (var s in stress)
            for (int level = 1; level <= 2; level++)
                list.Add(new Spec(s.Name + "-s" + level, s.Route, s.Query + "&stress=" + level, s.Sizes));

        // ———— 不冻结的实时走查（README 自检「实时模式」：总览、反应釜页 1/1 与 3/11、设备页 2 / 3 / 在线的 8 路，× 三种数据）————
        // live:N = 推进 N 秒；4 台时 N = 15（4号机 12 s 后正在重连…、2 s 后又断线）；lease2 = 先替 2号机申请控制权（5 s 后自动批准，提示「2号机触屏已确认 · 远程控制中」）
        (string Name, string Route, string Query, string Live)[] live =
        {
            ("live-ov", "overview", "devs=4", "15"),
            ("live-rv11", "reactor/1/1", "", "7"),
            ("live-rv311", "reactor/3/11", "sel=3/11", "7"),
            ("live-dv2", "device/2", "", "7:lease2"),
            ("live-dv3", "device/3", "", "7"),
            ("live-dv4", "device/4", "devs=4&offline=none", "7"),
        };
        foreach (var l in live)
        {
            list.Add(new Spec(l.Name, l.Route, l.Query, Screens, Setup: "m6g5-live:" + l.Live));
            for (int level = 1; level <= 2; level++)
                list.Add(new Spec(l.Name + "-s" + level, l.Route, (l.Query.Length > 0 ? l.Query + "&" : "") + "stress=" + level, Screens, Setup: "m6g5-live:" + l.Live));
        }
        // 宽限的倒计时走起来（18 s → 11 s）
        list.Add(new Spec("live-lost1", "overview", "lease=1:lost", Screens, Setup: "m6g5-live:7"));
        return list;
    }
}
#endif
