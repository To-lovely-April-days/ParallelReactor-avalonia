// 一条 RS485 总线 = 一个 I/O 线程 + 两条队列（紧急 / 普通）。C#: Hardware/PriorityGate.cs + ModbusRtuMaster.Txn
//
// 规则（照 PriorityGate）：
//   - 同一时间只有一笔事务在途（半双工一问一答）；
//   - 取下一笔时先取紧急队列，紧急队列空了才取普通队列；同一级别内先来先得（FIFO）；
//   - 在途事务不会被打断：紧急帧最多等当前这一笔做完（正常几十 ms，超时 300–600 ms）；
//   - 已排队的事务不可取消（防半帧错位），只有 stop() 时把还没开始的统一回失败。
//   - 不自动重试。
// 紧急队列只给「全部停止」快速路径的停止帧用（设计文档 §4）。没有急停锁存。
//
// 线程契约：
//   - submit / submit_future / submit_seq / queued / idle / stats：任何线程都可以调。
//   - 回调在**总线 I/O 线程**上执行：要短，不要阻塞，不要在里面同步等待本总线（会死锁）；
//     正常用法是把结果投递回控制线程。回调里再 submit 是允许的（排到对应队列队尾）。
//   - stop() / 析构：不要在回调里调（不能 join 自己）。stop 后再 submit 会立即在**调用方线程**上回调失败。
//   - 回调只会被调用一次。
#pragma once
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <functional>
#include <future>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "hw/comm_stats.hpp"
#include "hw/modbus_master.hpp"

namespace reactor::hw {

enum class Priority { Normal, Urgent };

class Bus {
public:
    using Callback = std::function<void(const ModbusResult&)>;
    using SeqCallback = std::function<void(const std::vector<ModbusResult>&)>;

    // 构造即启动 I/O 线程。name 用于日志（如「温控」）。
    Bus(std::string name, std::shared_ptr<ModbusMaster> master);
    ~Bus();
    Bus(const Bus&) = delete;
    Bus& operator=(const Bus&) = delete;

    // 排一笔事务。返回 false = 总线已停止（此时 cb 已在调用方线程上以失败结果调用过）。
    bool submit(ModbusRequest req, Priority prio, Callback cb);
    // 同上，返回 future（任何非总线线程可 .get() 等待；不要在总线线程上等）。
    std::future<ModbusResult> submit_future(ModbusRequest req, Priority prio = Priority::Normal);
    // 依次执行一串事务：每一笔做完后，下一笔才排到同级队列**队尾**（和 C# 逐个 await 同一把 PriorityGate 一样，
    // 中间可以插进紧急帧和别人的事务）。stop_on_fail=true 时遇到失败即停，结果只含已做的部分。
    // cb 收到按顺序的结果；空序列立即在调用方线程上回调。
    bool submit_seq(std::vector<ModbusRequest> reqs, Priority prio, bool stop_on_fail, SeqCallback cb);

    // 停止：不再接新事务；在途那笔做完；drain=false 时排队中的统一回失败「总线已关闭」，
    // drain=true 时先把已排队的做完再退出。然后 join 线程。可重复调用。
    void stop(bool drain = false);

    bool running() const;
    bool on_bus_thread() const { return std::this_thread::get_id() == thread_id_; }
    size_t queued(Priority prio) const;
    bool idle() const;                     // 没有在途、没有排队
    bool wait_idle(int timeout_ms) const;  // 等到 idle（测试 / 退出前用）；超时返回 false

    // 本总线逐笔事务的统计（每笔都算，成功 / 失败 / 连续失败 3 次 = 离线）。
    // 注意：C# 的温控统计是「一轮 5 次块读算 1 次」，要和旧遥测 CSV 的计数口径一致时，由 ctl 另建 CommStats 按轮计。
    const CommStats& stats() const { return stats_; }
    const std::string& name() const { return name_; }
    const std::shared_ptr<ModbusMaster>& master() const { return master_; }

private:
    struct Entry {
        ModbusRequest req;
        Callback cb;
    };
    struct SeqState;
    bool submit_seq_step(const std::shared_ptr<SeqState>& st);
    void run();

    std::string name_;
    std::shared_ptr<ModbusMaster> master_;
    CommStats stats_;

    mutable std::mutex mu_;
    std::condition_variable cv_;
    mutable std::condition_variable idle_cv_;
    std::deque<Entry> urgent_, normal_;
    bool stopping_ = false;
    bool drain_ = false;
    bool in_flight_ = false;
    bool exited_ = false;
    std::thread thread_;
    std::thread::id thread_id_;
};

// 把一条总线包装成同步 ModbusMaster：execute() = submit_future().get()。
// 给不在控制线程上的慢流程、工具和测试用（会阻塞调用线程最多「排队 + 300 ms」）。
// 不能在本总线的 I/O 线程上调（直接返回失败，不会死锁）。线程安全。
class BusMaster : public ModbusMaster {
public:
    BusMaster(Bus& bus, Priority prio = Priority::Normal) : bus_(bus), prio_(prio) {}
    ModbusResult execute(const ModbusRequest& req) override;

private:
    Bus& bus_;
    Priority prio_;
};

}  // namespace reactor::hw
