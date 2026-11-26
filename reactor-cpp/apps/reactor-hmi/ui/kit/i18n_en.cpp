// 英文译文表：中文原文 → English。界面里用 tr("中文") / trf("中文格式", ...) 取，格式符必须一一对应。
// 后台发来的文案（程序步骤名、报警标题）也经 tr() 查一次；查不到就原样显示。
#include <string>
#include <unordered_map>

namespace reactor::hmi {

const std::unordered_map<std::string, const char*>& en_table() {
    static const std::unordered_map<std::string, const char*> t = {
        // ---- 顶栏 / 导航 ----
        {"八通道平行反应仪 · HT-PR8", "8-Channel Parallel Reactor · HT-PR8"},
        {"总览", "Home"}, {"程序", "Program"}, {"曲线", "Curves"}, {"记录", "Records"}, {"设置", "Settings"},
        {"已连接", "Connected"}, {"未连接", "Offline"}, {"模拟模式", "Simulation"},
        {"装置总览", "Overview"}, {"气路图", "Gas lines"}, {"方案总览", "Schedule"},
        // ---- 竖栏 ----
        {"全部釜", "All vessels"}, {"搅拌", "Stirring"}, {"手动阀控", "Manual valves"}, {"全部停止", "Stop all"},
        {"%d 路", "%d ch"}, {"开", "On"}, {"关", "Off"}, {"停", "Off"},
        {"搅拌转速 · 全部釜", "Stirring speed · all vessels"}, {"停止搅拌？", "Stop stirring?"},
        {"8 路的搅拌一起停（共用一个电机）。", "Stirring stops on all 8 vessels (one shared motor)."},
        {"停止搅拌", "Stop stirring"}, {"不停", "Keep running"},
        {"全部停止？", "Stop all?"},
        {"停止全部 %d 路的加热，中止正在跑的程序和气路，关闭全部阀门。",
         "Stops heating on all %d channels, aborts running programs and gas sequences, closes all valves."},
        {"搅拌照常转，不放空；停完可以正常重新开始。", "Stirring keeps running, nothing is vented; you can start again right after."},
        // ---- 装置总览卡片 ----
        {"没有程序 · 手动", "No program · manual"}, {"这一路已停用", "Channel disabled"},
        {"第 %d/%d 步", "Step %d/%d"}, {"剩 %s", "%s left"}, {"等 %s 号用完气路", "Waiting for ch %s"}, {"等气路", "Waiting for gas"},
        {"排第 %d", "Queue #%d"}, {"自然降温", "Cooling"}, {"读数中断", "No reading"},
        {"限 %.0f °C", "Limit %.0f °C"}, {"限 %.3f MPa", "Limit %.3f MPa"},
        // ---- 气路图 ----
        {"共用气路总管", "Common gas manifold"}, {"惰性气体", "Inert gas"}, {"气体 A", "Gas A"}, {"气体 B", "Gas B"}, {"排空", "Vent"},
        {"点反应釜进入这一路 · 阀门要先打开手动阀控", "Tap a vessel to open it · turn on manual valves to switch valves"},
        // ---- 方案总览 ----
        {"8 路程序", "8 programs"}, {"%d 路在跑", "%d running"}, {"%s 号最早", "ch %s first"}, {"最晚 %s 结束", "all done by %s"},
        {"做完", "Done"}, {"正在做", "Running"}, {"还没做", "Pending"}, {"反应（时长看吸气）", "Reaction (length depends on uptake)"},
        {"运行中", "Running"}, {"等气路", "Waiting gas"}, {"未下发", "Not sent"},
        {"第 %d/%d 步 · %s", "Step %d/%d · %s"}, {"已 %s", "%s in"}, {"等 %s 号放开气路", "Waiting for ch %s to release gas"},
        {"已等 %s", "waited %s"}, {"已编辑，还没下发", "Edited, not sent yet"}, {"%d 步 约 %s", "%d steps · about %s"},
        {"反应 · 吸气中", "Reaction · uptake"}, {"反应 · 最长 8 h", "Reaction · max 8 h"},
        {"吸气停止就结束 · 最晚 %s", "Ends when uptake stops · latest %s"}, {"现在 %s", "Now %s"},
        {"点任意一路看这一路的温度、压力计划", "Tap a row to see its temperature and pressure plan"},
        {"%d 号 · 没有程序", "Ch %d · no program"},
        {"%d 步 · %s 开始 · 已 %s · 反应最晚到 %s", "%d steps · started %s · %s in · reaction until %s at latest"},
        {"运行中 · 第 %d / %d 步 %s", "Running · step %d / %d %s"}, {"温度 °C", "Temp °C"}, {"压力 MPa", "Pressure MPa"},
        // ---- 步骤名 ----
        {"气密检查", "Leak check"}, {"气密", "Leak"}, {"置换", "Purge"}, {"升温", "Heat"}, {"充压", "Pressurize"},
        {"保持", "Hold"}, {"反应", "React"}, {"放空", "Vent"}, {"结束", "End"},
        // ---- 点釜面板 ----
        {"%d 号", "Ch %d"}, {"手动", "Manual"}, {"加热中", "Heating"}, {"启动加热", "Start heating"}, {"停止加热", "Stop heating"},
        {"目标", "Target"}, {"进气阀", "Inlet valve"}, {"吸气量清零", "Reset uptake"},
        {"全部设定 · 进入 %d 号 ›", "All settings · ch %d ›"}, {"程序控制", "Program"}, {"温度", "Temperature"}, {"压力", "Pressure"},
        {"约剩 %s", "about %s left"}, {"程序页", "Program"}, {"设定(只看)", "Settings (view)"}, {"中止…", "Abort…"},
        {"停止加热？", "Stop heating?"}, {"这一路停止加热，阀门和搅拌不变。", "Heating stops on this channel; valves and stirring stay as they are."},
        {"要先打开手动阀控", "Turn on manual valves first"}, {"知道了", "OK"},
        {"进气阀要先在主页右边打开「手动阀控」才能开。", "Turn on \"Manual valves\" on the right of the home page before opening an inlet valve."},
        // ---- 全部设定 ----
        {"已启用", "Enabled"}, {"已停用", "Disabled"}, {"没有程序（手动）", "no program (manual)"},
        {"吸气量", "Gas uptake"}, {"加热", "Heating"}, {"已运行", "Elapsed"},
        {"去程序页", "Program"}, {"去曲线页", "Curves"}, {"中止程序…", "Abort…"}, {"复制到全部", "Copy to all"},
        {"停用这一路", "Disable"}, {"启用这一路", "Enable"}, {"停用这一路？", "Disable this channel?"}, {"不停用", "Keep"}, {"停用", "Disable"},
        {"%d 号停止加热、关闭进气阀，从主页和程序里拿掉。", "Ch %d stops heating, closes its inlet valve and is removed from the home page and programs."},
        {"把 %d 号的设定复制到其余各路？", "Copy ch %d settings to the other channels?"}, {"取消", "Cancel"}, {"复制", "Copy"},
        {"目标温度、升温方式、到位范围、防超调一起复制；正在跑程序和已停用的路不改。",
         "Target, heating mode, band and anti-overshoot are copied; channels running a program or disabled are left alone."},
        {"曲线 (升温方式)", "Curve"}, {"升温方式", "Heating mode"}, {"定值", "Fixed"}, {"目标温度", "Target"}, {"到位范围 ±", "Band ±"}, {"防超调", "Anti-overshoot"},
        {"%d 号 · 目标温度", "Ch %d · target temperature"}, {"%d 号 · 到位范围 ±", "Ch %d · band ±"},
        {"启动控温", "Start heating"}, {"停止控温", "Stop heating"}, {"启动曲线升温", "Start ramp"}, {"停止曲线升温", "Stop ramp"},
        {"点按开始", "Tap to start"}, {"点按停止 · 会先问你", "Tap to stop · asks first"},
        {"从现在的温度开始，按段自动下发", "Starts from the current temperature, segment by segment"},
        {"选「曲线」时在这里编最多 16 段", "Pick \"Curves\" to edit up to 16 segments here"},
        {"%d 号停止加热，阀门和搅拌不变。", "Ch %d stops heating; valves and stirring stay as they are."},
        {"气体", "Gas"}, {"溶液体积", "Liquid volume"}, {"%d 号 · 溶液体积", "Ch %d · liquid volume"}, {"清零 · 重新计量", "Reset · measure again"},
        {"进气阀要先在主页打开「手动阀控」才能开；溶液体积用来算气相体积和吸气量。",
         "Inlet valves need \"Manual valves\" on (home page); liquid volume is used for the gas volume and uptake."},
        {"%d 段 · 终温 %.0f °C · 共 %s · 每段从上一段的温度线性升到目标",
         "%d segments · final %.0f °C · %s total · each segment ramps linearly from the previous one"},
        {"段", "Seg"}, {"这一段用时", "Duration"}, {"加一段", "Add"}, {"用时 %.0f min", "%.0f min"},
        {"%d 号 · 第 %d 段目标温度", "Ch %d · segment %d target"}, {"%d 号 · 第 %d 段用时", "Ch %d · segment %d duration"},
        {"程序控制 · 只能看", "Program · view only"}, {"第 %d 步 %s", "step %d %s"}, {"到 %s", " to %s"},
        {"反应开始后计量", "Measured once reaction starts"}, {"已运行 %s", "running %s"},
        // ---- 中止 ----
        {"中止程序？", "Abort program?"}, {"继续运行", "Keep running"}, {"中止", "Abort"}, {"%d 号 · %s", "Ch %d · %s"},
        {"中止后，", "After aborting, "}, {"%d 号停止加热、关闭进气阀", "ch %d stops heating and closes its inlet valve"},
        {"，其他通道照常运行。", "; other channels keep running."},
        // ---- 报警 ----
        {"全部", "All"}, {"未处理", "Open"}, {"报警", "Alarms"}, {"警告", "Warnings"}, {"事件", "Events"},
        {"本次开机以来 · %s 开机", "Since boot · %s"}, {"报警与事件", "Alarms & events"}, {"共 %d 条 ·", "%d total ·"},
        {"未处理 %d", "%d open"}, {"全部确认", "Ack all"}, {"时间", "Time"}, {"来源", "Source"}, {"内容", "Message"}, {"状态", "Status"},
        {"确认", "Ack"}, {"已解除", "Cleared"}, {"已确认", "Acked"}, {"通讯", "Comm"}, {"系统", "System"}, {"没有报警", "No alarms"},
        {"超上限", "Over by"}, {"设定", "set"}, {"上限", "Limit"}, {"系统已经做了", "Already done"}, {"接下来", "Next"},
        {"%d 号恢复加热、进气", "Ch %d may heat and take gas again"}, {"这条报警标为已处理", "Mark this alarm as handled"},
        {"看曲线", "Curves"}, {"报警前后的压力", "Pressure around the alarm"}, {"报警前后的读数", "Readings around the alarm"},
        {"一级报警 · 安全联锁已动作", "Level 1 · safety interlock tripped"}, {"%d 号压力", "Ch %d pressure"}, {"%d 号温度", "Ch %d temperature"},
        {"情况不对就点主页的「全部停止」", "If something looks wrong, tap \"Stop all\" on the home page"},
        {"还有 %d 条未处理", "%d more open"}, {"去报警页", "Alarm page"}, {"看这一刻的曲线", "Curves at this moment"},
        {"二级 · %s", "Level 2 · %s"}, {"已断开", "Down for"}, {"%s · %d s 后收起", "%s · closes in %d s"}, {"查看", "View"},
        // 后台 / 模拟数据里的报警文案（以后由后台按语言给出）
        {"超压 · 联锁动作", "Overpressure · interlock"}, {"超温 · 切断加热", "Overtemperature · heating cut"},
        {"吸气量未计量", "Uptake not counted"}, {"温控模块离线", "Temperature module offline"}, {"压力采集模块离线", "Pressure module offline"},
        {"搅拌转速恢复", "Stirring speed restored"}, {"开机自检完成", "Self-test passed"},
        // ---- 语言 / 键盘 ----
        {"完整", "Complete"}, {"未齐", "Partial"}, {"完成", "Done"},
        {"点一下马上就换，不合适再点回来。标「未齐」的，没译到的条目显示英文",
         "Applies immediately; tap again to switch back. \"Partial\" languages show English where not translated"},
        {"上次设定", "Last set"}, {"当前实测", "Measured"}, {"应用", "Apply"}, {"范围 %s – %s %s", "Range %s – %s %s"},
        {"大写", "Shift"}, {"退格", "Back"}, {"空格", "Space"}, {"确定", "OK"}, {"给这个程序起个名字", "Name this program"},
        {"5 号 · 第 5 步 升温 · 目标温度", "Ch 5 · step 5 heat · target temperature"},
        // ---- 占位页 ----
        {"第二批", "batch 2"}, {"编辑程序、程序库、泄漏测试", "Program editor, library, leak test"},
        {"气体消耗、温度、压力、三项对照", "Gas uptake, temperature, pressure, comparison"},
        {"运行记录、每日数据文件、导出到 U 盘", "Run records, daily data files, USB export"}, {"10 个分类", "10 categories"},
    };
    return t;
}

}  // namespace reactor::hmi
