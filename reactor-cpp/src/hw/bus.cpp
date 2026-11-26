#include "hw/bus.hpp"

#include <chrono>
#include <exception>

#include "common/clock.hpp"
#include "common/log.hpp"

namespace reactor::hw {

struct Bus::SeqState {
    std::vector<ModbusRequest> reqs;
    size_t next = 0;
    std::vector<ModbusResult> results;
    bool stop_on_fail = true;
    Priority prio = Priority::Normal;
    SeqCallback cb;
};

Bus::Bus(std::string name, std::shared_ptr<ModbusMaster> master) : name_(std::move(name)), master_(std::move(master)) {
    std::promise<void> started;
    auto f = started.get_future();
    thread_ = std::thread([this, &started] {
        thread_id_ = std::this_thread::get_id();
        started.set_value();
        run();
    });
    f.wait();  // thread_id_ 写好再返回，on_bus_thread() 不会读到半截值
}

Bus::~Bus() { stop(false); }

static void invoke(const Bus::Callback& cb, const ModbusResult& r, const std::string& bus) {
    if (!cb) return;
    try {
        cb(r);
    } catch (const std::exception& e) {
        LOGE("hw", "总线 %s 回调抛异常：%s", bus.c_str(), e.what());
    } catch (...) {
        LOGE("hw", "总线 %s 回调抛未知异常", bus.c_str());
    }
}

bool Bus::submit(ModbusRequest req, Priority prio, Callback cb) {
    {
        std::lock_guard<std::mutex> lk(mu_);
        if (!stopping_) {
            req.urgent = (prio == Priority::Urgent);
            (prio == Priority::Urgent ? urgent_ : normal_).push_back(Entry{std::move(req), std::move(cb)});
            cv_.notify_one();
            return true;
        }
    }
    invoke(cb, fail_result("总线已关闭（" + name_ + "）"), name_);
    return false;
}

std::future<ModbusResult> Bus::submit_future(ModbusRequest req, Priority prio) {
    auto p = std::make_shared<std::promise<ModbusResult>>();
    auto f = p->get_future();
    submit(std::move(req), prio, [p](const ModbusResult& r) { p->set_value(r); });
    return f;
}

bool Bus::submit_seq(std::vector<ModbusRequest> reqs, Priority prio, bool stop_on_fail, SeqCallback cb) {
    if (reqs.empty()) {
        if (cb) cb({});
        return running();
    }
    auto st = std::make_shared<SeqState>();
    st->reqs = std::move(reqs);
    st->stop_on_fail = stop_on_fail;
    st->prio = prio;
    st->cb = std::move(cb);
    return submit_seq_step(st);
}

bool Bus::submit_seq_step(const std::shared_ptr<SeqState>& st) {
    ModbusRequest req = st->reqs[st->next];
    return submit(std::move(req), st->prio, [this, st](const ModbusResult& r) {
        st->results.push_back(r);
        st->next++;
        if ((!r.ok && st->stop_on_fail) || st->next >= st->reqs.size()) {
            if (st->cb) st->cb(st->results);
            return;
        }
        submit_seq_step(st);  // 总线已停时 submit 会同步回调失败，递归收尾
    });
}

void Bus::run() {
    std::unique_lock<std::mutex> lk(mu_);
    for (;;) {
        cv_.wait(lk, [this] { return stopping_ || !urgent_.empty() || !normal_.empty(); });
        const bool empty = urgent_.empty() && normal_.empty();
        if (stopping_ && (!drain_ || empty)) break;
        if (empty) continue;

        Entry e;
        if (!urgent_.empty()) { e = std::move(urgent_.front()); urgent_.pop_front(); }
        else { e = std::move(normal_.front()); normal_.pop_front(); }
        in_flight_ = true;
        lk.unlock();

        const int64_t t0 = mono_ms();
        ModbusResult r;
        try {
            r = master_->execute(e.req);
        } catch (const std::exception& ex) {
            r = fail_result(std::string("主站内部错误：") + ex.what());
        } catch (...) {
            r = fail_result("主站内部错误");
        }
        r.elapsed_ms = mono_ms() - t0;
        if (r.ok) stats_.ok(wall_ms());
        else stats_.fail();
        invoke(e.cb, r, name_);

        lk.lock();
        in_flight_ = false;
        idle_cv_.notify_all();
    }
    std::deque<Entry> left;
    for (auto& e : urgent_) left.push_back(std::move(e));
    for (auto& e : normal_) left.push_back(std::move(e));
    urgent_.clear();
    normal_.clear();
    exited_ = true;
    idle_cv_.notify_all();
    lk.unlock();
    for (auto& e : left) invoke(e.cb, fail_result("总线已关闭（" + name_ + "）"), name_);
}

void Bus::stop(bool drain) {
    {
        std::lock_guard<std::mutex> lk(mu_);
        if (!stopping_) {
            stopping_ = true;
            drain_ = drain;
        }
        cv_.notify_all();
    }
    if (on_bus_thread()) return;  // 回调里调的：不能 join 自己，析构时再 join
    if (thread_.joinable()) thread_.join();
}

bool Bus::running() const {
    std::lock_guard<std::mutex> lk(mu_);
    return !stopping_;
}

size_t Bus::queued(Priority prio) const {
    std::lock_guard<std::mutex> lk(mu_);
    return prio == Priority::Urgent ? urgent_.size() : normal_.size();
}

bool Bus::idle() const {
    std::lock_guard<std::mutex> lk(mu_);
    return !in_flight_ && urgent_.empty() && normal_.empty();
}

bool Bus::wait_idle(int timeout_ms) const {
    std::unique_lock<std::mutex> lk(mu_);
    return idle_cv_.wait_for(lk, std::chrono::milliseconds(timeout_ms),
                             [this] { return exited_ || (!in_flight_ && urgent_.empty() && normal_.empty()); });
}

ModbusResult BusMaster::execute(const ModbusRequest& req) {
    if (bus_.on_bus_thread()) return fail_result("不能在总线线程上同步等待本总线的事务");
    return bus_.submit_future(req, prio_).get();
}

}  // namespace reactor::hw
