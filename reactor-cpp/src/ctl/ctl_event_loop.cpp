#include "ctl/ctl_event_loop.hpp"

#include <exception>
#include <vector>

#include "common/log.hpp"
#include <chrono>
#include <cstdint>
#include <mutex>
#include <optional>
#include <thread>

namespace reactor::ctl {

EventLoop::EventLoop(Clock& clock) : clock_(clock) {}

EventLoop::~EventLoop() {
    stop();
    if (th_.joinable() && std::this_thread::get_id() != th_.get_id()) th_.join();
}

bool EventLoop::post(Task t) {
    {
        std::lock_guard<std::mutex> lk(mu_);
        if (stopping_) return false;
        q_.push_back(std::move(t));
    }
    cv_.notify_one();
    return true;
}

bool EventLoop::on_loop_thread() const {
    std::lock_guard<std::mutex> lk(mu_);
    return owner_ == std::this_thread::get_id();
}

size_t EventLoop::queued() const {
    std::lock_guard<std::mutex> lk(mu_);
    return q_.size();
}

bool EventLoop::stopped() const {
    std::lock_guard<std::mutex> lk(mu_);
    return stopping_;
}

EventLoop::TimerId EventLoop::every(int64_t period_ms, Task t, int64_t first_delay_ms) {
    if (period_ms < 1) period_ms = 1;
    TimerId id = next_id_++;
    int64_t first = first_delay_ms < 0 ? period_ms : first_delay_ms;
    timers_[id] = Timer{clock_.mono_ms() + first, period_ms, std::move(t)};
    cv_.notify_one();
    return id;
}

EventLoop::TimerId EventLoop::after(int64_t delay_ms, Task t) {
    TimerId id = next_id_++;
    timers_[id] = Timer{clock_.mono_ms() + (delay_ms < 0 ? 0 : delay_ms), 0, std::move(t)};
    cv_.notify_one();
    return id;
}

void EventLoop::cancel(TimerId id) { timers_.erase(id); }

std::optional<int64_t> EventLoop::next_due() const {
    std::optional<int64_t> best;
    for (const auto& kv : timers_)
        if (!best || kv.second.due < *best) best = kv.second.due;
    return best;
}

void EventLoop::run_safely(const Task& t, const char* what) {
    try {
        if (t) t();
    } catch (const std::exception& ex) {
        LOGE("ctl", "%s 抛出异常：%s", what, ex.what());
    } catch (...) {
        LOGE("ctl", "%s 抛出未知异常", what);
    }
}

bool EventLoop::pop_due(int64_t now, TimerId& id, Task& fn) {
    auto best = timers_.end();
    for (auto it = timers_.begin(); it != timers_.end(); ++it)
        if (it->second.due <= now && (best == timers_.end() || it->second.due < best->second.due)) best = it;
    if (best == timers_.end()) return false;
    id = best->first;
    fn = best->second.fn;
    if (best->second.period > 0) {
        int64_t next = best->second.due + best->second.period;
        if (next <= now) next = now + best->second.period;  // 落后超过一个周期：不补发
        best->second.due = next;
    } else {
        timers_.erase(best);
    }
    return true;
}

size_t EventLoop::fire_due(int64_t now) {
    size_t n = 0;
    TimerId id;
    Task fn;
    while (pop_due(now, id, fn)) {
        run_safely(fn, "定时器");
        ++n;
    }
    return n;
}

size_t EventLoop::run_pending() {
    size_t n = 0;
    for (;;) {
        Task t;
        {
            std::lock_guard<std::mutex> lk(mu_);
            if (q_.empty()) break;
            t = std::move(q_.front());
            q_.pop_front();
        }
        run_safely(t, "控制线程任务");
        ++n;
    }
    return n;
}

void EventLoop::bind_current_thread() {
    std::lock_guard<std::mutex> lk(mu_);
    owner_ = std::this_thread::get_id();
}

void EventLoop::start_thread(const char* name) {
    (void)name;
    th_ = std::thread([this] { run(); });
}

void EventLoop::run() {
    bind_current_thread();
    for (;;) {
        run_pending();
        fire_due(clock_.mono_ms());
        std::unique_lock<std::mutex> lk(mu_);
        if (stopping_) break;
        if (!q_.empty()) continue;
        int64_t wait = 1000;
        if (auto d = next_due()) wait = *d - clock_.mono_ms();
        if (wait > 1000) wait = 1000;
        if (wait > 0) cv_.wait_for(lk, std::chrono::milliseconds(wait), [this] { return stopping_ || !q_.empty(); });
        if (stopping_) break;
    }
}

void EventLoop::stop() {
    {
        std::lock_guard<std::mutex> lk(mu_);
        stopping_ = true;
        q_.clear();
    }
    cv_.notify_all();
}

void EventLoop::join() {
    if (th_.joinable() && std::this_thread::get_id() != th_.get_id()) th_.join();
}

}  // namespace reactor::ctl
