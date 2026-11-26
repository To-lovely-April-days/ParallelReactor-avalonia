#include "ctl/ctl_clock.hpp"

#include "common/clock.hpp"
#include <cstdint>

namespace reactor::ctl {

int64_t SystemClock::mono_ms() const { return ::reactor::mono_ms(); }
int64_t SystemClock::wall_ms() const { return ::reactor::wall_ms(); }

}  // namespace reactor::ctl
