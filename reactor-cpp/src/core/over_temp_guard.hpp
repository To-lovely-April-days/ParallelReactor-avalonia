// 超温切断判定（纯逻辑）。C#: Models/OverTempGuard.cs
// 每通道独立、与通道状态无关（停用 / 已结束 / 自整定中同样判定）：
//  - 温控在线且 PV ≥ 限值的上升沿 → TripSend；
//  - 「已确认」以轮询回读 At=4 为准；回读未知（nullopt）时退而以写入回显为准；
//  - 已触发、未确认、无在途写入、距上次下发 ≥ 5 s → RetrySend（未解除就一直重发）；
//  - 已确认后回读 At≠4 → 下一次允许下发时 Reopened；
//  - 温控离线 / PV 为 NaN：不触发、不下发、不解除；
//  - PV < 限值 − 2 ℃ → Cleared，重新布防。
// 时刻 now_ms 为调用方传入的单调毫秒。
#pragma once
#include <cstdint>
#include <optional>
#include <vector>

namespace reactor::core {

enum class OverTempAction {
    None,       // 无动作
    TripSend,   // 上升沿：记事件 + 置报警 + 下发切断
    RetrySend,  // 已超温、未确认 → 重发切断
    Confirmed,  // 切断已确认（回读 At=4）：只在跳变时出现一次
    Reopened,   // 已确认后回读 At≠4 → 重新置报警 + 记事件 + 重发切断
    Cleared,    // PV 回落到 限值 − 回差 以下 → 解除
};

namespace over_temp {
constexpr int64_t kRetryIntervalMs = 5000;  // C#: OverTempGuard.RetryInterval = 5 s
constexpr double kHysteresis = 2.0;         // C#: OverTempGuard.Hysteresis ℃
constexpr int kEscalateAfterSends = 3;      // C#: OverTempGuard.EscalateAfterSends（连续未确认次数 → 调用方升级报警）
constexpr int64_t kNeverMs = INT64_MIN / 4; // 「从未下发」（对应 C# DateTime 默认值）
}  // namespace over_temp

class OverTempGuard {
public:
    explicit OverTempGuard(int channels = 8);
    int count() const { return static_cast<int>(ch_.size()); }
    bool is_tripped(int ch) const { return ch_[ch].tripped; }
    bool is_confirmed(int ch) const { return ch_[ch].confirmed; }
    bool in_flight(int ch) const { return ch_[ch].in_flight; }
    // 本次触发（或复切）以来已下发、仍未确认的切断次数
    int unconfirmed_sends(int ch) const { return ch_[ch].sends; }

    // ch 通道下标（0 起）；output_stopped = 回读模式是否 At=4，未知 nullopt
    OverTempAction evaluate(int ch, double pv, bool online, double limit, int64_t now_ms,
                            std::optional<bool> output_stopped = std::nullopt);
    // 切断写入结果回来（ok = 写入回显正常）。已解除的通道忽略迟到的结果。写入成功不等于已确认。
    void on_stop_result(int ch, bool ok, int64_t now_ms = 0);

private:
    struct Ch {
        bool tripped = false, confirmed = false, in_flight = false, acked = false, reopen_pending = false;
        int sends = 0;
        int64_t last_send = over_temp::kNeverMs;
    };
    OverTempAction send(int ch, int64_t now_ms, OverTempAction a);
    std::vector<Ch> ch_;
};

}  // namespace reactor::core
