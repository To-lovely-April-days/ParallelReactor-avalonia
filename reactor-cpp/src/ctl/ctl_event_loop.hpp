// 控制线程的事件循环（设计文档 §4）：一个线程、一个任务队列、若干定时器。
//
//   - post(fn)：任何线程把一个任务投递进队列，任务在控制线程上按 FIFO 执行。总线回调就是这样把结果送回控制线程的；
//     接口层的写命令也一样（call() 带 future）。循环停止后 post 返回 false、任务被丢弃。
//   - every / after / cancel：周期 / 单次定时器，只在控制线程上（或 start 之前）调用。到期时间按 Clock::mono_ms()。
//     周期定时器按「上次应到时刻 + 周期」排下一次；落后超过一个周期时从现在重新起算（同 DispatcherTimer，不补发）。
//   - 任务 / 定时器里抛出的异常被捕获并记日志，循环继续。
//
// 两种驱动方式：
//   1) 生产：start_thread()（或在当前线程 run()），内部按时钟等待；stop() 让 run() 返回。
//   2) 测试 / 手动：bind_current_thread() 把调用线程当作控制线程，然后自己调 run_pending() / fire_due(now)，
//      配合 ManualClock 精确推进时间。
#pragma once
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <functional>
#include <future>
#include <map>
#include <memory>
#include <mutex>
#include <optional>
#include <thread>

#include "ctl/ctl_clock.hpp"

namespace reactor::ctl {

class EventLoop {
public:
    using Task = std::function<void()>;
    using TimerId = uint64_t;

    explicit EventLoop(Clock& clock);
    ~EventLoop();
    EventLoop(const EventLoop&) = delete;
    EventLoop& operator=(const EventLoop&) = delete;

    Clock& clock() const { return clock_; }

    // ---- 任何线程 ----
    bool post(Task t);
    // 投递并返回 future（不要在控制线程上 .get()：会死锁）。循环已停止时 future 里是 broken_promise。
    template <class F>
    auto call(F f) -> std::future<decltype(f())> {
        using R = decltype(f());
        auto p = std::make_shared<std::promise<R>>();
        auto fut = p->get_future();
        post([p, f]() mutable { set_promise(*p, f); });
        return fut;
    }
    bool on_loop_thread() const;
    size_t queued() const;
    bool stopped() const;

    // ---- 控制线程（或 start 之前） ----
    TimerId every(int64_t period_ms, Task t, int64_t first_delay_ms = -1);  // first_delay < 0 = 一个周期后
    TimerId after(int64_t delay_ms, Task t);
    void cancel(TimerId id);
    std::optional<int64_t> next_due() const;  // 最早到期时刻（mono），无定时器为空

    // ---- 生产驱动 ----
    void start_thread(const char* name = "ctl");
    void run();    // 阻塞到 stop()
    void stop();   // 任何线程；让 run() 尽快返回，之后 post 失败
    void join();   // 等 start_thread 起的线程结束（不要在控制线程上调）

    // ---- 手动驱动（测试） ----
    void bind_current_thread();
    size_t run_pending();              // 执行当前已排队的任务（含执行期间新投递的），返回执行个数
    size_t fire_due(int64_t now_mono); // 触发所有 due <= now 的定时器（按到期先后），返回触发个数

private:
    struct Timer {
        int64_t due;
        int64_t period;  // 0 = 单次
        Task fn;
    };
    template <class R, class F>
    static void set_promise(std::promise<R>& p, F& f) {
        try { p.set_value(f()); } catch (...) { p.set_exception(std::current_exception()); }
    }
    template <class F>
    static void set_promise(std::promise<void>& p, F& f) {
        try { f(); p.set_value(); } catch (...) { p.set_exception(std::current_exception()); }
    }
    static void run_safely(const Task& t, const char* what);
    bool pop_due(int64_t now, TimerId& id, Task& fn);

    Clock& clock_;
    mutable std::mutex mu_;
    std::condition_variable cv_;
    std::deque<Task> q_;
    bool stopping_ = false;
    std::thread::id owner_;
    std::thread th_;

    std::map<TimerId, Timer> timers_;  // 只在控制线程上动
    TimerId next_id_ = 1;
};

}  // namespace reactor::ctl
