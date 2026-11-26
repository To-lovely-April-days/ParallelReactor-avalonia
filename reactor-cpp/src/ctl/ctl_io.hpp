// 控制核心访问设备层的公共底座：事件循环 + 设备层 + 时钟 + 「停止代号」。
//
// 「停止代号」（StopEpoch）解决的问题：没有急停锁存以后，「全部停止」快速路径往紧急队列塞停止帧、再按普通优先级补一遍；
// 但一条多步写序列（例如启动加热 Srun=0 → At=0 → PID → SP）每一步做完才把下一步排到队尾——若中途插进了「全部停止」，
// 后面的 At=0 会落在补发的停止帧之后，把刚停下的加热重新打开。所以：
//   - 会让设备「出力 / 开阀 / 转起来」的写入，在**发起时**记下当时的代号；每一步提交前在同一把锁里核对代号，
//     代号变了（中间发生过全部停止）就不再提交，整个序列按失败结束；
//   - 快速路径在同一把锁里先把代号 +1、再提交紧急帧和普通补发帧。
// 这样「核对 + 提交」与「+1 + 提交停止帧」不会交错，保证最后落到设备上的是停止帧（设计文档 §4、决定记录「全部停止之后」）。
// 只关不开的写入（At=4、关阀、停搅拌）不需要核对。
//
// 线程：StopEpoch 任何线程可用；chain()/submit_on_loop() 任何线程可发起，结果一律投递回控制线程执行。
#pragma once
#include <cstdint>
#include <functional>
#include <memory>
#include <mutex>
#include <vector>

#include "ctl/ctl_event_loop.hpp"
#include "hw/hardware.hpp"

namespace reactor::ctl {

class StopEpoch {
public:
    uint64_t current() const {
        std::lock_guard<std::recursive_mutex> lk(mu_);
        return epoch_;
    }
    // 代号仍是 e 时在锁内执行 fn（fn 里提交事务），返回 true；否则不执行，返回 false
    template <class F>
    bool submit_if(uint64_t e, F&& fn) {
        std::lock_guard<std::recursive_mutex> lk(mu_);
        if (epoch_ != e) return false;
        fn();
        return true;
    }
    // 代号 +1，并在同一把锁里执行 fn（快速路径提交停止帧）
    template <class F>
    void bump_and(F&& fn) {
        std::lock_guard<std::recursive_mutex> lk(mu_);
        ++epoch_;
        fn();
    }

private:
    mutable std::recursive_mutex mu_;  // 递归：总线已停时 submit 会在调用方线程同步回调，回调里可能再进来
    uint64_t epoch_ = 1;
};

struct CoreIo {
    EventLoop* loop = nullptr;
    hw::Hardware* hw = nullptr;
    Clock* clock = nullptr;
    StopEpoch* stop = nullptr;

    int64_t mono() const { return clock->mono_ms(); }
    int64_t wall() const { return clock->wall_ms(); }

    using ResultFn = std::function<void(const hw::ModbusResult&)>;
    using SeqFn = std::function<void(const std::vector<hw::ModbusResult>&)>;

    // 提交一笔事务，结果投递回控制线程执行 cb（cb 可空）
    void submit(hw::Bus& bus, hw::ModbusRequest req, ResultFn cb, hw::Priority prio = hw::Priority::Normal) const {
        EventLoop* lp = loop;
        bus.submit(std::move(req), prio, [lp, cb](const hw::ModbusResult& r) {
            if (cb) lp->post([cb, r] { cb(r); });
        });
    }

    // 依次执行一串事务（每步做完再排下一步，同 Bus::submit_seq）。
    // guard_epoch != 0：每一步提交前核对停止代号（见文件头），代号变了就停在那里，结果只含已做的部分，aborted = true。
    // stop_on_fail：遇到失败即停。结果投递回控制线程执行 cb(results, aborted)。
    void chain(hw::Bus& bus, std::vector<hw::ModbusRequest> reqs, bool stop_on_fail, uint64_t guard_epoch,
               std::function<void(const std::vector<hw::ModbusResult>&, bool aborted)> cb,
               hw::Priority prio = hw::Priority::Normal) const;
};

}  // namespace reactor::ctl
