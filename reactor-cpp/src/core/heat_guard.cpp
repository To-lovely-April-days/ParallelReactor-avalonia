#include "core/heat_guard.hpp"
#include "core/chan_name.hpp"

#include <string>

#include "core/num_format.hpp"
#include "core/over_pressure_guard.hpp"
#include "core/over_temp_guard.hpp"
#include <optional>

namespace reactor::core {

std::optional<GuardReject> heat_guard_check(const HeatGuardInput& s) {
    if (!s.temp_configured) return GuardReject{op_codes::kHwNotConfigured, "温控未配置串口（PR_TEMP_PORT），无法加热"};
    if (!s.temp_online) return GuardReject{op_codes::kHwOffline, "温控离线（AI-8 通讯失败），无法启动加热"};
    if (s.id && s.over_temp_tripped)
        return GuardReject{op_codes::kChannelAlarm, chan_name(*s.id) + " 超温切断未解除（需低于 " +
                                                        fmt_dec(s.over_temp - over_temp::kHysteresis, 1, 0) +
                                                        "℃），不可启动加热"};
    if (s.id && s.stop_heat_on_over_pressure && s.over_pressure_tripped)
        return GuardReject{op_codes::kChannelAlarm,
                           chan_name(*s.id) + " 超压联锁未解除（需低于 " +
                               format_p(s.over_pressure * over_pressure::kRearmRatio, s.pressure_unit) +
                               "），不可启动加热"};
    return std::nullopt;
}

}  // namespace reactor::core
