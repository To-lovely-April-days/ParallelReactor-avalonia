#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;

namespace ParallelReactor.Control.Shot;

/// <summary>
/// 截图计划（PORT-PLAN §5.9）：截图名 → 演示路由 + 参数 + 尺寸 + 参照图。每个尺寸一个进程（PR_CONTROL_SHOT_SIZE），
/// 该尺寸下跑 Sizes 含这个尺寸的条目；PR_CONTROL_SHOT_NAMES（或 PR_CONTROL_SHOTS）给了名字时只跑这些、并且不看尺寸。
/// 输出 &lt;name&gt;-&lt;w&gt;.png、&lt;name&gt;-&lt;w&gt;.audit.txt、cmp-&lt;name&gt;-&lt;w&gt;.png（参照 = 参照目录下的 final-&lt;name&gt;-&lt;w&gt;.png）。
/// 路由 = 样稿的 hash 路由（overview、device/1、reactor/1/1、placeholder/curves、gallery），参数同样稿 README「常用查询参数」（explorer、tl、sel）。
/// M2：外壳（菜单、页签、图标栏、资源管理器 / 检查器 / 时间轴的框架、状态栏、全局停止格）+ 页面骨架；M4 / M5 起工作区与参照图逐张对上。
/// </summary>
public static class ShotPlan
{
    /// <summary>
    /// 一张截图。Route 与样稿的 hash 路由同名；Query 是路由参数（a=1&amp;b=2）。
    /// From = 先按这个窗口尺寸排一遍、再改成截图尺寸（截窗口尺寸变化之后的样子：检查器 300 ↔ 330、资源管理器停靠 ↔ 收起）。
    /// Setup = 截图前对演示数据做的事（"nodevices"：移除全部设备 —— 空的资源管理器 / 检查器 / 时间轴）。
    /// </summary>
    public sealed record Spec(string Name, string Route, string Query, IReadOnlyList<PixelSize> Sizes, string? Ref = null, PixelSize? From = null, string? Setup = null)
    {
        /// <summary>参照图文件名（默认 final-&lt;name&gt;-&lt;w&gt;.png，与样稿 shots/ 同名）。</summary>
        public string RefFile(int width) => Ref ?? $"final-{Name}-{width}.png";

        public IReadOnlyDictionary<string, string> Params => ParseQuery(Query);
    }

    /// <summary>外壳的三个标准尺寸（PORT-PLAN §5.9）。</summary>
    private static readonly PixelSize[] Screens = { new(1920, 1080), new(1600, 900), new(1366, 768) };

    public static readonly IReadOnlyList<Spec> All = new[]
    {
        // 陈列页：新外壳里的「组件」页签（与样稿整页截图同样含外框）；样稿只有 1920 的参照图，
        // 1600 / 1366（工作区 936 / 1002）只做审计，窗口高度让工作区（高 − 菜单 34 − 页签 44 − 时间轴 220 − 状态栏 30）放得下整页、不出滚动条
        new Spec("gallery", "gallery", "", new[] { new PixelSize(1920, 5100), new PixelSize(1600, 4200), new PixelSize(1366, 4200) }),
        // M2 外壳（工作区是骨架；M4 总览 / 设备页、M5 反应釜页补上内容）
        new Spec("overview", "overview", "", Screens),
        new Spec("device1", "device/1", "", Screens),
        new Spec("reactor11", "reactor/1/1", "", Screens),
        // 演示路由的状态（样稿 README 的地址参数，M2 起由 Core 的 DemoFleet 给出）：4 台（4号机断线重连中）、选中 3号机 · RV11（16 路设备展开）。
        // 外框（资源管理器、页签、状态栏）与参照图对上；工作区是骨架（M4 / M5）
        new Spec("overview4dev", "overview", "devs=4&scroll=end", new[] { new PixelSize(1920, 1080) }),
        new Spec("reactor311", "reactor/3/11", "sel=3/11", Screens),
        // 反应釜页 · 独立搅拌的型号（2号机 HT-PR4）：检查器「搅拌（本釜）」转速框；工作区是骨架（M5）
        new Spec("reactor21", "reactor/2/1", "", Screens),
        // 反应釜页 · 升温中的 RV3（曲线：温度斜坡 + 设定值虚线、分带按「变化大」铺满；标记「RV3 加样后升温」）—— 样稿冻结的参照图 final-reactor13-1920；
        // 1600 / 1366 与 reactor21 / reactor311 的其它尺寸用 snap 现拍样稿同一个地址（曲线的窗口、刻度、上限文字的位置逐张对照）
        new Spec("reactor13", "reactor/1/3", "", Screens),
        // 反应釜页「釜体 / 属性查看器」（M5，Views/Pages/Reactor/ReactorVesselPanel）：属性查看器页签（Setup rvprops = 点「属性查看器」；参照用 snap 现拍样稿同一个地址、
        // js 参数点同一个页签，final-rvp*-<w>.png，见 doc §9）—— 两列卡片、只显示完整的行、「另有 N 项」
        new Spec("rvp11", "reactor/1/1", "", Screens, Setup: "rvprops"),
        new Spec("rvp21", "reactor/2/1", "", new[] { new PixelSize(1920, 1080) }, Setup: "rvprops"),
        new Spec("rvp311", "reactor/3/11", "sel=3/11", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }, Setup: "rvprops"),
        // 釜体页签的最坏值与状态（API.md §3.5 stress=1 / 2：长读数、1234.50 mmol、长气体名；断线：「断线前保温」、不画火焰、读数 t-3；急停：已停止、OFF；
        // 2号机 RV1 一条超压报警：PT「上限 9.99 MPa」、压力表红圈、标题栏不写状态；stress 的最后一路：超压报警 + 程序报警暂停）—— 参照 snap 现拍 final-rv-*-<w>.png
        new Spec("rv-stress1", "reactor/1/1", "stress=1", Screens),
        new Spec("rv-stress2", "reactor/1/1", "stress=2", Screens),
        new Spec("rv-stress1-14", "reactor/1/4", "stress=1", Screens),
        new Spec("rv-stress2-316", "reactor/3/16", "stress=2&sel=3/16", Screens),
        new Spec("rvp-stress1", "reactor/1/1", "stress=1", Screens, Setup: "rvprops"),
        new Spec("rvp-stress2", "reactor/2/1", "stress=2", Screens, Setup: "rvprops"),
        new Spec("rv-off1", "reactor/1/1", "offline=1", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }),
        new Spec("rv-estop1", "reactor/1/1", "estop=1", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }),
        new Spec("rv-alarm21", "reactor/2/1", "alarm=2:1", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }),
        // 反应釜页「程序」面板与整页的其它状态（M5 第四部分；参照用 snap 现拍样稿同一个地址，final-rv-*-<w>.png，见 doc §9）：
        // 未安装的位置（说明页）、没有程序（程序库 + 预览；本地只读 = 原因写一次 / 远程控制中 = 每行「下发」）、断线（4号机：连接中断条 +「立即重连」、卡片去色）、
        // 急停（3号机：急停条 + 超压报警条、程序已中止）、程序已结束（2号机 RV4）、刚添加从未连上的设备（只有一句说明）、
        // 关掉「程序」（右边一栏铺满整个宽度 + 提示）、画布放大一档 / 两档（每行的卡变少）、放大「程序」面板（状态条照样显示）、最坏值的程序（stress=1 的 3号机 RV16）、
        // 1920 收起资源管理器（工作区 1526：同一版式，画布和曲线更宽）、2560 × 1440 收起资源管理器（工作区 ≥ 1800：三列宽版式）
        new Spec("rv-un15", "reactor/1/5", "", Screens),
        new Spec("rv-noprog313", "reactor/3/13", "sel=3/13", Screens),
        new Spec("rv-noprog313r", "reactor/3/13", "sel=3/13&lease=3:remote", Screens),
        new Spec("rv-off41", "reactor/4/1", "devs=4&sel=4/1", Screens),
        new Spec("rv-estop311", "reactor/3/11", "estop=3&sel=3/11", Screens),
        new Spec("rv-done24", "reactor/2/4", "sel=2/4", Screens),
        new Spec("rv-never", "reactor/4/1", "", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }, Setup: "adddevice"),
        new Spec("rv-progx", "reactor/1/1", "", Screens, Setup: "rvprogx"),
        new Spec("rv-zoom1", "reactor/1/1", "", Screens, Setup: "rvzoom1"),
        new Spec("rv-zoom2", "reactor/1/1", "", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }, Setup: "rvzoom2"),
        new Spec("rv-maxprog", "reactor/1/1", "", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }, Setup: "rvmaxprog"),
        new Spec("rv-stress1-prog", "reactor/3/16", "stress=1&sel=3/16", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }),
        new Spec("rv-x0", "reactor/1/1", "explorer=0", new[] { new PixelSize(1920, 1080) }),
        new Spec("rv-wide", "reactor/1/1", "explorer=0", new[] { new PixelSize(2560, 1440) }),
        new Spec("rv-wide-311", "reactor/3/11", "explorer=0&sel=3/11", new[] { new PixelSize(2560, 1440) }),
        // 外框的状态集（PORT-PLAN §5.9「状态集」；参照图用 snap 现拍样稿同一个地址，文件名 final-st-*-<w>.png，见 doc §9）：
        // 急停（检查器红框、资源管理器 / 时间轴组头「急停中」）、申请控制权中、断线（4 种区域的写法）、宽限、资源管理器收起、时间轴收起、未安装的位置、4 台
        new Spec("st-estop3", "overview", "estop=3", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }),
        new Spec("st-estop1", "overview", "estop=1", new[] { new PixelSize(1920, 1080) }),
        new Spec("st-lease2req", "device/2", "lease=2:requested", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }),
        new Spec("st-lost1", "overview", "lease=1:lost", new[] { new PixelSize(1920, 1080) }),
        new Spec("st-off1", "overview", "offline=1", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }),
        new Spec("st-off3", "overview", "offline=3", new[] { new PixelSize(1920, 1080) }),
        new Spec("st-x0", "overview", "explorer=0", new[] { new PixelSize(1920, 1080) }),
        new Spec("st-tl0", "overview", "tl=0", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }),
        new Spec("st-un16", "overview", "sel=1/6", new[] { new PixelSize(1920, 1080) }),
        new Spec("st-devs4", "overview", "devs=4", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }),
        // 长名字（stress=1：「高压加氢测试一号」等）：页签栏刚好放得下 / 放不下的边界 —— 页签的量宽必须同 WebKit（pr:PrText），
        // 1920 下 7 个页签全显示、不能多收一个进「›」（R1）；三个尺寸都对照
        new Spec("st-stress1", "overview", "stress=1", Screens),
        // 窄屏 + explorer=1：资源管理器是打开的浮层（导航之后再套用外框参数，不会被导航收起）
        new Spec("st-x1", "overview", "explorer=1", new[] { new PixelSize(1366, 768) }),
        // 第二阶段视图的占位页（样稿 pages/placeholder.js）：样稿没有 final 参照图，只审计（参照可用 snap 现拍 /placeholder/curves）。
        // 先按 1600 × 900 排一遍再改成截图尺寸：居中的说明文字在窗口变宽 / 变窄之后仍在自己的框里（审计 text drawn outside its box）
        new Spec("curves", "placeholder/curves", "", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }, From: new PixelSize(1600, 900)),
        // 空状态（移除全部设备）：资源管理器「还没有设备」、检查器「没有选中对象」、时间轴运行 0/0；同样先按 1600 × 900 排再改尺寸
        // （检查器 300 → 330 / 资源管理器停靠 → 收起之后，居中的空状态文字不能偏出自己的框）。样稿没有这个地址参数，只审计
        new Spec("st-empty", "overview", "", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }, From: new PixelSize(1600, 900), Setup: "nodevices"),
        // 窗口变大 / 变小之后的总览：应当和直接按这个尺寸打开的一模一样（收进「›」的页签、时间轴组头让出去的型号再显示出来时名字不能是空的 ——
        // Controls/TextRelayout；居中文字不能按旧宽度偏出框）。参照同 overview
        new Spec("overview-grow", "overview", "", new[] { new PixelSize(1920, 1080) }, Ref: "final-overview-1920.png", From: new PixelSize(1366, 768)),
        new Spec("overview-shrink", "overview", "", new[] { new PixelSize(1366, 768) }, Ref: "final-overview-1366.png", From: new PixelSize(1920, 1080)),
        // 对话框（M3，Shell/Dialogs；样稿 js/dialogs.js）：添加设备、申请控制权（2号机 本地只读）、急停（已连接的默认全选）—— 参照 = 样稿的 final 图；
        // 其余对话框在 1920 与 1366 下各拍一张，参照用 snap 现拍样稿同一个地址（final-dlg-*-<w>.png，见 doc §9）
        new Spec("dlg-add", "overview", "dialog=add", new[] { new PixelSize(1920, 1080) }),
        new Spec("dlg-lease2", "device/2", "dialog=lease:2", new[] { new PixelSize(1920, 1080) }),
        new Spec("dlg-estop", "overview", "dialog=estop", new[] { new PixelSize(1920, 1080) }),
        new Spec("dlg-devices", "overview", "dialog=devices", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }),
        new Spec("dlg-stopall", "overview", "dialog=stopall", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }),
        new Spec("dlg-rename2", "overview", "dialog=rename:2", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }),
        new Spec("dlg-remove1", "overview", "dialog=remove:1", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }),
        new Spec("dlg-disconnect1", "overview", "dialog=disconnect:1", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }),
        new Spec("dlg-edit2", "overview", "dialog=edit:2", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }),
        new Spec("dlg-estop2", "overview", "dialog=estop:2", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }),
        // 对话框的其它状态（1920，参照同样用 snap 现拍）：申请中（倒计时环 + 三步）、远程控制中、心跳中断宽限、断线的设备（4号机）、
        // 急停只预选一台而它断线了（说明「4号机 连接中断 · 无法预选 · 可勾选其它设备」，那一行灰、写「连接中断 · 无法发送」）
        new Spec("dlg-lease2req", "device/2", "lease=2:requested&dialog=lease:2", new[] { new PixelSize(1920, 1080) }),
        new Spec("dlg-lease1", "overview", "dialog=lease:1", new[] { new PixelSize(1920, 1080) }),
        new Spec("dlg-lease1lost", "overview", "lease=1:lost&dialog=lease:1", new[] { new PixelSize(1920, 1080) }),
        new Spec("dlg-lease4off", "overview", "devs=4&dialog=lease:4", new[] { new PixelSize(1920, 1080) }),
        new Spec("dlg-estop4off", "overview", "devs=4&dialog=estop:4", new[] { new PixelSize(1920, 1080) }),
        // 总览（M4，Views/Pages/OverviewPage + Pages/Overview）：参照用 snap 现拍样稿同一个地址（final-<name>-<w>.png，见 doc §9）。
        // 滚到底（JS 把 .overview-scroll 滚到底；我们是演示参数 scroll=end）：1600 / 1366 下折叠线以下的 16 路紧凑卡（状态字换行、进度条与文字分两行）
        new Spec("ov-end", "overview", "scroll=end", new[] { new PixelSize(1600, 900), new PixelSize(1366, 768) }),
        // 最坏值（API.md §3.5）：stress=1（8 个汉字的别名，上面的 st-stress1）、stress=2（16 个字母的别名）；三个尺寸都要审计为空，1600 / 1366 再滚到底看紧凑卡
        new Spec("st-stress2", "overview", "stress=2", Screens),
        new Spec("ov-stress1-end", "overview", "stress=1&scroll=end", new[] { new PixelSize(1600, 900), new PixelSize(1366, 768) }),
        new Spec("ov-stress2-end", "overview", "stress=2&scroll=end", new[] { new PixelSize(1600, 900), new PixelSize(1366, 768) }),
        // 状态：申请控制权中（分区头「申请中 45 s」）、2号机加一条超压报警（PR4 宽卡的报警卡 + 分区头「● RV1 超压报警」）、只有一台（下面补「温度趋势」面板，M5）、
        // 三种型号各一台且 8 路装满（PR8 用紧凑卡）
        new Spec("ov-lease2req", "overview", "lease=2:requested", new[] { new PixelSize(1920, 1080) }),
        new Spec("ov-alarm21", "overview", "alarm=2:1", new[] { new PixelSize(1920, 1080) }),
        new Spec("ov-devs1", "overview", "devs=1", Screens),
        // 温度趋势（M5，Pages/Overview/OverviewTrendVm + Controls/Chart/TimeSeriesChart）：16 路 + 4 路两台、最坏值（选中的 3号机 RV1–RV4 四条线、「显示 4/n」）；
        // 选中 2号机（HT-PR4：画它的反应釜）。参照用 snap 现拍样稿同一个地址（final-ov-trend*-1920.png，见 doc §9）
        new Spec("ov-trend-stress2", "overview", "devs=PR16,PR4&stress=2", new[] { new PixelSize(1920, 1080) }),
        new Spec("ov-trend-sel2", "overview", "devs=PR16,PR4&sel=2", new[] { new PixelSize(1920, 1080) }),
        new Spec("ov-devsmix", "overview", "devs=PR8,PR4,PR16", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }),
        // 样稿 API.md §9 要求的组合（每种再配 stress=1 / 2）：急停 + 断线一起（分区头最挤：红「急停中 / 断线前已急停」+ 连接徽标 + 报警小块 + 说明条）、
        // 4 台全部在线（8 路装满的 HT-PR8 + 16 路，滚到底）
        new Spec("ov-worst1", "overview", "estop=1,2,3&offline=3&stress=1", Screens),
        new Spec("ov-worst2", "overview", "estop=1,2,3&offline=3&stress=2", Screens),
        new Spec("ov-all4", "overview", "devs=4&offline=none&stress=2&scroll=end", Screens),
        // 设备页（M4，Views/Pages/DevicePage + Pages/Device）：样稿冻结的参照图 final-device2-1920 / final-device3-{1920,1600} / final-stress-device2-1920；
        // 其它状态的参照用 snap 现拍样稿同一个地址（final-dv-*-<w>.png，见 doc §9）
        new Spec("device2", "device/2", "", new[] { new PixelSize(1920, 1080) }),
        new Spec("device3", "device/3", "", new[] { new PixelSize(1920, 1080), new PixelSize(1600, 900) }),
        // 最坏值（API.md §3.5，stress=1 / 2）：三种型号的设备页在三个尺寸下都要审计为空（HT-PR4 宽卡的「耗气 1234.50」阶梯、长程序名、5 位温度、长别名）
        new Spec("stress-device2", "device/2", "stress=1", new[] { new PixelSize(1920, 1080) }),
        new Spec("dv-stress1-2", "device/2", "stress=1", new[] { new PixelSize(1600, 900), new PixelSize(1366, 768) }),
        new Spec("dv-stress1-1", "device/1", "stress=1", Screens),
        new Spec("dv-stress1-3", "device/3", "stress=1", Screens),
        new Spec("dv-stress2-1", "device/1", "stress=2", Screens),
        new Spec("dv-stress2-2", "device/2", "stress=2", Screens),
        new Spec("dv-stress2-3", "device/3", "stress=2", Screens),
        // 状态：4号机断线（断线说明条 + 「立即重连」、读数 t-3、釜体与进度条去色）、4 台全部在线（8 路装满的 HT-PR8：一排 8 张窄卡 / 折两排 —— 样稿刚修过的进度条 6 px 规则）、
        // 三种型号各一台（PR8 装满 8 路）、时间轴收起（PR4 宽卡变高：耗气行回来、釜体变宽）、急停（急停说明条 + 共用资源「急停中」）、2号机一条超压报警（「报警与事件」自动打开 + 红点）
        new Spec("dv-off4", "device/4", "devs=4", Screens),
        new Spec("dv-all4", "device/4", "devs=4&offline=none", Screens),
        new Spec("dv-mix1", "device/1", "devs=PR8,PR4,PR16", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }),
        new Spec("dv-tl0-2", "device/2", "tl=0", new[] { new PixelSize(1920, 1080) }),
        new Spec("dv-estop3", "device/3", "estop=3", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }),
        new Spec("dv-alarm21", "device/2", "alarm=2:1", new[] { new PixelSize(1920, 1080), new PixelSize(1600, 900) }),
        // 刚添加、从未连上的设备（样稿 addDevice 之后打开它的设备页：反应釜面板只有一条说明，共用资源 / 设备信息写「未知 · 尚未连接」「未连接」）。
        // 参照：snap 现拍时用 JS 调 PR.sim.addDevice(...) 再 PR.route.go('device', [4])（见 doc §9），文件名 final-dv-never-<w>.png
        new Spec("dv-never", "device/4", "", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }, Setup: "adddevice"),
        // 旧样式的页面（D5：程序编辑 / 下发 / 导出、性能测试，M7 重做）：没有参照图；外框照常审计，旧样式的子树（LegacyHost，类 audit-skip）不审计
        new Spec("program", "placeholder/program", "", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }),
        new Spec("perf", "placeholder/perf", "", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }),
    };

    public static Spec? Find(string name) => All.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>这个尺寸下要跑的截图。names 非空时只跑这些名字（任意尺寸）；未知名字抛异常。</summary>
    public static IReadOnlyList<Spec> Select(PixelSize size, IReadOnlyCollection<string>? names)
    {
        if (names is { Count: > 0 })
            return names.Select(n => Find(n) ?? throw new ArgumentException($"截图计划里没有「{n}」（已有：{string.Join(", ", All.Select(s => s.Name))}）")).ToList();
        return All.Where(s => s.Sizes.Contains(size)).ToList();
    }

    /// <summary>PR_CONTROL_SHOT_NAMES / PR_CONTROL_SHOTS：逗号分隔的截图名。</summary>
    public static IReadOnlyCollection<string> NamesFromEnv()
    {
        var s = Environment.GetEnvironmentVariable("PR_CONTROL_SHOT_NAMES");
        if (string.IsNullOrWhiteSpace(s)) s = Environment.GetEnvironmentVariable("PR_CONTROL_SHOTS");
        return string.IsNullOrWhiteSpace(s) ? Array.Empty<string>() : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static IReadOnlyDictionary<string, string> ParseQuery(string? q)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(q)) return d;
        foreach (var part in q.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = part.IndexOf('=');
            if (i < 0) d[Uri.UnescapeDataString(part)] = "1";
            else d[Uri.UnescapeDataString(part[..i])] = Uri.UnescapeDataString(part[(i + 1)..]);
        }
        return d;
    }
}
#endif
