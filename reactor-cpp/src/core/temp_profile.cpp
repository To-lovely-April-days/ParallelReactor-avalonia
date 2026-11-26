#include "core/temp_profile.hpp"

#include <algorithm>
#include <cmath>

#include "core/num_format.hpp"
#include <cstdint>
#include <string>

namespace reactor::core {

using namespace temp_profile;

int TempProfile::n() const { return std::clamp(seg_count, 1, kMaxSegments); }

bool TempProfile::enabled(int index1) const { return index1 >= 1 && index1 <= n(); }

void TempProfile::start(double current_pv, int64_t now_ms) {
    start_temp_ = current_pv;
    start_ms_ = now_ms;
    has_written_ = false;
    running_ = true;
    active_ = 0;
    status_ = "第 1/" + std::to_string(seg_count) + " 段";
}

void TempProfile::stop() {
    running_ = false;
    active_ = 0;
    status_ = "已停止";
}

double TempProfile::current_target(int64_t now_ms) {
    double elapsed = static_cast<double>(now_ms - start_ms_) / 60000.0;
    double from = start_temp_, acc = 0;
    int cnt = n();
    for (int i = 0; i < cnt; i++) {
        const ProfileSegment& seg = segments[static_cast<size_t>(i)];
        double dur = std::max(0.01, seg.minutes);
        if (elapsed < acc + dur) {
            active_ = i + 1;
            double remain = (acc + dur) - elapsed;
            status_ = "第 " + std::to_string(i + 1) + "/" + std::to_string(cnt) + " 段 · 剩 " + fmt_dec(remain, 1, 0) + " 分";
            double k = (elapsed - acc) / dur;
            return from + (seg.temp - from) * k;
        }
        acc += dur;
        from = seg.temp;
    }
    // 全部走完：保持最后一段温度
    active_ = 0;
    status_ = "已完成 · 保持 " + fmt_dec(from, 1, 0) + " ℃";
    return from;
}

bool TempProfile::should_write(double target, int64_t now_ms) const {
    if (!has_written_) return true;
    if (std::fabs(target - last_written_) >= 0.1) return true;
    return now_ms - last_write_ms_ >= kForceRewriteMs;
}

void TempProfile::mark_written(double target, int64_t now_ms) {
    last_written_ = target;
    has_written_ = true;
    last_write_ms_ = now_ms;
}

double TempProfile::elapsed_min(int64_t now_ms) const {
    return running_ ? static_cast<double>(now_ms - start_ms_) / 60000.0 : 0;
}

double TempProfile::total_minutes() const {
    double acc = 0;
    for (int i = 0; i < n(); i++) acc += std::max(0.01, segments[static_cast<size_t>(i)].minutes);
    return acc;
}

bool TempProfile::is_complete(int64_t now_ms) const { return running_ && elapsed_min(now_ms) >= total_minutes(); }

}  // namespace reactor::core
