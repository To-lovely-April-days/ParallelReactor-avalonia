#include "core/alarm_lifecycle.hpp"
#include <string>

namespace reactor::core::alarm_lifecycle {

std::string on_cleared(const std::string& stat) {
    if (stat == kActive) return kCleared;
    if (stat == kAck) return kDone;
    return stat;
}

std::string on_ack(const std::string& stat) {
    if (stat == kActive) return kAck;
    if (stat == kCleared) return kDone;
    return stat;
}

}  // namespace reactor::core::alarm_lifecycle
