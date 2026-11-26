#include "core/reactor_state.hpp"
#include <optional>
#include <string>

namespace reactor::core {

const char* state_code(ReactorState s) {
    switch (s) {
        case ReactorState::React: return "react";
        case ReactorState::Pressing: return "pressing";
        case ReactorState::Heating: return "heating";
        case ReactorState::Alarm: return "alarm";
        case ReactorState::Done: return "done";
        case ReactorState::Idle: return "idle";
        case ReactorState::Ready: return "ready";
        case ReactorState::Wait: return "wait";
        case ReactorState::Stir: return "stir";
        case ReactorState::Cooling: return "cooling";
        case ReactorState::Stopped: return "stopped";
    }
    return "idle";
}

std::optional<ReactorState> parse_state_code(const std::string& c) {
    static const ReactorState all[] = {ReactorState::React, ReactorState::Pressing, ReactorState::Heating,
                                       ReactorState::Alarm, ReactorState::Done,     ReactorState::Idle,
                                       ReactorState::Ready, ReactorState::Wait,     ReactorState::Stir,
                                       ReactorState::Cooling, ReactorState::Stopped};
    for (auto s : all)
        if (c == state_code(s)) return s;
    return std::nullopt;
}

const char* state_zh(ReactorState s, bool over_temp) {
    switch (s) {
        case ReactorState::React: return "反应中";
        case ReactorState::Pressing: return "升压中";
        case ReactorState::Heating: return "升温中";
        case ReactorState::Alarm: return over_temp ? "超温" : "超压";
        case ReactorState::Done: return "已结束";
        case ReactorState::Ready: return "已启用";
        case ReactorState::Wait: return "等待中";
        case ReactorState::Stir: return "搅拌中";
        case ReactorState::Cooling: return "降温中";
        case ReactorState::Idle: return "停用";
        case ReactorState::Stopped: return "手动停止";
    }
    return "停用";
}

bool state_is_running(ReactorState s) {
    return s != ReactorState::Idle && !state_is_ended(s);
}

bool state_is_ended(ReactorState s) { return s == ReactorState::Done || s == ReactorState::Stopped; }

bool state_is_active(ReactorState s) {
    switch (s) {
        case ReactorState::Heating:
        case ReactorState::Pressing:
        case ReactorState::React:
        case ReactorState::Wait:
        case ReactorState::Stir:
        case ReactorState::Cooling: return true;
        default: return false;
    }
}

}  // namespace reactor::core
