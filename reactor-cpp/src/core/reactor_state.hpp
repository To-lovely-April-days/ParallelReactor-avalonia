// 反应釜运行状态。C#: Models/ReactorState.cs（枚举顺序照抄）；
// 状态码 / 中文：Contracts/.../Api/ChannelStates.cs（§6.2，小写字符串）与 Models/Reactor.cs StateZh。
#pragma once
#include <optional>
#include <string>

namespace reactor::core {

enum class ReactorState {
    React,     // 反应中
    Pressing,  // 升压中
    Heating,   // 升温中
    Alarm,     // 报警（超温 / 超压）
    Done,      // 已结束
    Idle,      // 停用
    Ready,     // 已启用（未开始加热）
    Wait,      // 等待中（程序）
    Stir,      // 搅拌中（程序）
    Cooling,   // 降温中（程序）
};

// 接口状态码："react" / "pressing" / "heating" / "alarm" / "done" / "idle" / "ready" / "wait" / "stir" / "cooling"
const char* state_code(ReactorState s);
// 状态码 → 枚举；未知码返回 nullopt
std::optional<ReactorState> parse_state_code(const std::string& code);
// 中文名（Reactor.StateZh）：报警时 over_temp=true 为「超温」，否则「超压」
const char* state_zh(ReactorState s, bool over_temp = false);
// 运行中（Reactor.IsRunning）：React/Heating/Pressing/Alarm/Ready/Wait/Stir/Cooling
bool state_is_running(ReactorState s);
// 正在干活（ChannelStates.IsActive）：Heating/Pressing/React/Wait/Stir/Cooling
bool state_is_active(ReactorState s);

}  // namespace reactor::core
