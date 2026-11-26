// 从快照推出页面显示用的东西（States-D3「乙」：状态只靠数值、序号标颜色和釜上的动画）。
// 只是显示规则，不做安全判断。
#pragma once
#include "model.hpp"

#include <string>
#include <cstdint>
#include <vector>

namespace reactor::hmi::model {

enum class VState {
    Disabled,     // 停用
    Ready,        // 已启用
    Heating,      // 升温中
    Pressing,     // 升压中
    Reacting,     // 反应中
    Holding,      // 等待中（保持）
    Stirring,     // 搅拌中
    Cooling,      // 降温中
    QueuedGas,    // 排队等气路
    Done,         // 已结束
    Stopped,      // 手动停止（停止 / 全部停止 / 中止程序时正在有动作；后台 state = "stopped"）
    OverTemp,     // 超温
    OverPressure, // 超压
    Offline,      // 传感器离线
};

VState vstate(const Model& m, int ch);
uint32_t badge_color(VState s);
bool vs_running(VState s);      // 釜体用「运行」样式（桨叶转）
bool vs_bubbles(VState s);
bool vs_heater(VState s);

// 「全部停止」会转「手动停止」的通道（有动作：程序 / 加热温控 / 气路 / 报警）。后台给 hasActivity 就用它，否则按快照推
bool has_activity(const Model& m, int ch);
std::vector<int> active_channels(const Model& m);

// 数字下面那一行提示：图标名（st_*13）、文字、颜色；没有提示时 text 为空
struct Hint { const char* icon = nullptr; std::string text; uint32_t color = 0; };
Hint hint(const Model& m, int ch);

// 常用格式
std::string fmt_temp(const ChannelSnap& c);          // 「80.1」或「—」
// 目标温度：后台没设过时是 0（低于可设范围），界面当「没设」（NaN → 数值框显示「—」、键盘从空开始）
double temp_sp(const ChannelSnap& c);
std::string fmt_mpa(OptD psi, int decimals = 3);      // 「1.525」或「—」
std::string fmt_mmol(OptD mmol);                      // 「0.4」或「—」
std::string fmt_hms(int64_t sec);                     // 「01:12:08」
std::string fmt_hmin(int64_t sec);                    // 「8 h 52 min」/「21 min」
std::string fmt_clock(int64_t utc_ms, bool seconds = true);  // 本地时间「14:02:18」

}  // namespace reactor::hmi::model
