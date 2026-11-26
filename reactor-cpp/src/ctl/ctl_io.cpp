#include "ctl/ctl_io.hpp"
#include <cstdint>
#include <functional>
#include <memory>
#include <vector>

namespace reactor::ctl {

namespace {

struct ChainState {
    hw::Bus* bus;
    std::vector<hw::ModbusRequest> reqs;
    size_t next = 0;
    bool stop_on_fail;
    uint64_t epoch;
    hw::Priority prio;
    std::vector<hw::ModbusResult> results;
    EventLoop* loop;
    StopEpoch* stop;
    std::function<void(const std::vector<hw::ModbusResult>&, bool)> cb;
};

void finish(const std::shared_ptr<ChainState>& st, bool aborted) {
    if (!st->cb) return;
    auto cb = st->cb;
    auto rs = st->results;
    st->loop->post([cb, rs, aborted] { cb(rs, aborted); });
}

void step(const std::shared_ptr<ChainState>& st) {
    if (st->next >= st->reqs.size()) { finish(st, false); return; }
    auto submit_one = [st] {
        hw::ModbusRequest req = st->reqs[st->next];
        st->bus->submit(std::move(req), st->prio, [st](const hw::ModbusResult& r) {
            st->results.push_back(r);
            st->next++;
            if (!r.ok && st->stop_on_fail) { finish(st, false); return; }
            step(st);
        });
    };
    if (st->epoch == 0) { submit_one(); return; }
    if (!st->stop->submit_if(st->epoch, submit_one)) finish(st, true);
}

}  // namespace

void CoreIo::chain(hw::Bus& bus, std::vector<hw::ModbusRequest> reqs, bool stop_on_fail, uint64_t guard_epoch,
                   std::function<void(const std::vector<hw::ModbusResult>&, bool)> cb, hw::Priority prio) const {
    auto st = std::make_shared<ChainState>();
    st->bus = &bus;
    st->reqs = std::move(reqs);
    st->stop_on_fail = stop_on_fail;
    st->epoch = guard_epoch;
    st->prio = prio;
    st->loop = loop;
    st->stop = stop;
    st->cb = std::move(cb);
    step(st);
}

}  // namespace reactor::ctl
