// 共用气路（总管）的占用登记 + 程序气路步骤的先到先得等待队列。
// C#: Models/GasLease.cs（GasOwner、GasAbortCause、GasLease）、Models/GasQueue.cs
// 超压联锁不是占用者：它无条件中止任何占用者（控制层的 abort_all_gas）。
#pragma once
#include <cstdint>
#include <functional>
#include <map>
#include <optional>
#include <set>
#include <string>
#include <vector>

#include "core/reactor_state.hpp"

namespace reactor::core {

enum class GasOwner { None, Program, Leak, Calib };

// 最近一次「无条件中止气路」的来由：超压联锁 → 持有者程序按失败「气路被超压联锁中止」收尾；退出程序 → 按手动中止。
enum class GasAbortCause { None, OverPressure, Shutdown };

// 气路占用登记（只在控制线程用）。
class GasLease {
public:
    GasOwner owner() const { return owner_; }
    std::optional<int> owner_rv() const { return owner_rv_; }  // 程序租约所属通道（泄漏测试为 nullopt）
    const std::string& task() const { return task_; }
    const std::vector<int>& participants() const { return participants_; }
    const std::vector<int>& watch() const { return watch_; }
    int64_t generation() const { return generation_; }
    bool busy() const { return owner_ != GasOwner::None; }

    // 占用气路（覆盖上一段租约），返回本段代号（每次 +1）。同一占用者（程序还要同一通道）续租时，
    // 延续下来的参与通道保留失效标记与开始状态；新占用者清除失效标记。
    // owner == None 时不做任何事、返回 0（C# 抛 ArgumentException）。
    int64_t begin(GasOwner owner, const std::string& task, const std::vector<int>& participants,
                  const std::vector<int>& watch, const std::map<int, ReactorState>& start_states,
                  std::optional<int> owner_rv = std::nullopt);
    // 只有当前占用者本人能按自己的身份中止；rv 有值时还要求是该通道的程序租约
    bool can_abort(GasOwner requester, std::optional<int> rv = std::nullopt) const;
    // 占用者一致（且 gen 为 0 或与当前一致）才释放
    bool try_end(GasOwner owner, int64_t gen = 0);
    // 无条件释放
    void clear() { try_end(owner_); }
    bool includes(int id) const;
    // 参与通道被「停止 / 停用」：标记失效；不参与的忽略
    void invalidate(int id);
    bool is_invalidated(int id) const { return invalid_.count(id) > 0; }
    // 序列是否必须中止（失效 / 报警 / 开始后转入停用或已结束）；nullopt = 安全。state_of 返回 nullopt = 未知通道，不判
    std::optional<std::string> fault_reason(const std::function<std::optional<ReactorState>(int)>& state_of) const;

private:
    std::set<int> invalid_;
    std::map<int, ReactorState> start_;
    GasOwner owner_ = GasOwner::None;
    std::optional<int> owner_rv_;
    std::string task_;
    std::vector<int> participants_;
    std::vector<int> watch_;
    int64_t generation_ = 0;
};

// 程序气路步骤的先到先得等待队列。每通道同一时间只有一张票。时刻一律为单调毫秒（调用方传入）。
class GasQueue {
public:
    // 入队，返回票号；该通道已在队中则返回原票（不改入队时刻）
    int64_t enqueue(int rv, int64_t now_ms);
    int count() const { return static_cast<int>(q_.size()); }
    bool is_head(int64_t ticket) const { return !q_.empty() && q_.front().ticket == ticket; }
    // 出队（取到租约 / 放弃 / 被中止）；不在队中返回 false
    bool remove(int64_t ticket);
    bool contains(int rv) const;
    // 已等待毫秒（不在队中为 0）
    int64_t waited_ms(int64_t ticket, int64_t now_ms) const;
    // 等待是否已超过上限（max_ms ≤ 0 视为不限）
    bool timed_out(int64_t ticket, int64_t now_ms, int64_t max_ms) const;
    // 等待中的通道号（按先后）
    std::vector<int> waiting() const;
    void clear() { q_.clear(); }

private:
    struct Entry { int64_t ticket; int rv; int64_t since; };
    std::vector<Entry> q_;
    int64_t next_ = 0;
};

}  // namespace reactor::core
