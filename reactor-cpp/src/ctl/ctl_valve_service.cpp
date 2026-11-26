#include "ctl/ctl_valve_service.hpp"
#include <cstdint>
#include <functional>
#include <string>

namespace reactor::ctl {

namespace {
hw::IoModule::Valves to_hw(const core::ValveFrame& f) {
    hw::IoModule::Valves v{};
    for (size_t i = 0; i < v.size(); ++i) v[i] = f[i];
    return v;
}
const char* kNotConfigured = "阀门模块未配置串口（hardware.json io.port）";
const char* kStopped = "已被全部停止中止，未下发";
}  // namespace

void ValveService::track(bool ok) {
    if (ok) stats_.ok(io_.wall());
    else stats_.fail();
}

void ValveService::bump_opens(const core::ValveFrame& f) {
    for (size_t i = 0; i < 8; ++i)
        if (f[i]) ++sv_gen_[i];
}

void ValveService::show(const core::ValveFrame& f) {
    core::ValveFrame prev = shown_;
    shown_ = f;
    if (prev != f && on_shown_changed) on_shown_changed(prev, f);
}

void ValveService::read_back(std::function<void(bool, const core::ValveFrame&)> cb) {
    if (!configured()) { if (cb) cb(false, shown_); return; }
    io_.submit(*io_.hw->io_bus, io_.hw->io.req_read_coils(), [this, cb](const hw::ModbusResult& r) {
        track(r.ok);
        auto v = io_.hw->io.decode_open_states(r);
        if (!v) { if (cb) cb(false, shown_); return; }
        core::ValveFrame f{};
        for (size_t i = 0; i < f.size(); ++i) f[i] = (*v)[i];
        shown_ = f;  // 开机回填，不算「整帧下发」
        if (cb) cb(true, f);
    });
}

void ValveService::write_frame(const core::ValveFrame& frame, const ValveWriteOpts& opts, DoneFn cb) {
    if (!configured()) { if (cb) cb(false, kNotConfigured); return; }
    if (auto why = core::valve_frame::check_apply(frame, opts.lease_busy, opts.participants, opts.lease_task)) {
        if (cb) cb(false, *why);
        return;
    }
    uint64_t epoch = io_.stop->current();
    if (!core::valve_frame::needs_dead_time(shown_, frame)) {
        attempt(frame, epoch, opts.retries, std::move(cb));
        return;
    }
    // 气源 / 排空之间切换：先全关一拍，等死区，再写目标帧
    core::ValveFrame closed = core::valve_frame::all_closed();
    int64_t dead = opts.dead_ms;
    int retries = opts.retries;
    attempt(closed, epoch, retries, [this, frame, epoch, dead, retries, cb](bool ok, const std::string& err) {
        if (!ok) { if (cb) cb(false, err); return; }
        io_.loop->after(dead, [this, frame, epoch, retries, cb] { attempt(frame, epoch, retries, cb); });
    });
}

void ValveService::attempt(const core::ValveFrame& frame, uint64_t epoch, int left, DoneFn cb) {
    bump_opens(frame);
    auto req = io_.hw->io.req_write_valves(to_hw(frame));
    bool submitted = io_.stop->submit_if(epoch, [&] {
        io_.submit(*io_.hw->io_bus, req, [this, frame, epoch, left, cb](const hw::ModbusResult& r) {
            track(r.ok);
            if (r.ok) {
                show(frame);
                if (cb) cb(true, "");
                return;
            }
            if (left > 0) {
                io_.loop->after(kRetryDelayMs, [this, frame, epoch, left, cb] { attempt(frame, epoch, left - 1, cb); });
                return;
            }
            if (cb) cb(false, "阀门写入失败（IO16R 通讯）：" + r.error);
        });
    });
    if (!submitted && cb) cb(false, kStopped);
}

void ValveService::close_range(int start, int count, DoneFn cb, int retries) {
    if (!configured()) { if (cb) cb(false, kNotConfigured); return; }
    io_.submit(*io_.hw->io_bus, io_.hw->io.req_close_range(start, count),
               [this, start, count, cb, retries](const hw::ModbusResult& r) {
                   track(r.ok);
                   if (r.ok) {
                       core::ValveFrame f = shown_;
                       for (int i = start; i < start + count && i < core::valve_frame::kCount; ++i) f[static_cast<size_t>(i)] = false;
                       show(f);
                       if (cb) cb(true, "");
                       return;
                   }
                   if (retries > 0) {
                       io_.loop->after(kCloseRetryDelayMs,
                                       [this, start, count, cb, retries] { close_range(start, count, cb, retries - 1); });
                       return;
                   }
                   if (cb) cb(false, r.error);
               });
}

void ValveService::close_sv(int id, DoneFn cb) {
    if (!configured()) { if (cb) cb(false, kNotConfigured); return; }
    io_.submit(*io_.hw->io_bus, io_.hw->io.req_set_sv(id, false), [this, id, cb](const hw::ModbusResult& r) {
        track(r.ok);
        if (r.ok) {
            core::ValveFrame f = shown_;
            f[static_cast<size_t>(id - 1)] = false;
            show(f);
        }
        if (cb) cb(r.ok, r.ok ? std::string() : r.error);
    });
}

void ValveService::close_all(bool keep_vent, DoneFn cb) {
    if (keep_vent) {
        close_range(0, core::valve_frame::kVent, std::move(cb), 3);  // 线圈 0–10 全关，排空不写
        return;
    }
    close_range(0, core::valve_frame::kCount, std::move(cb), 3);  // 12 路全关（只关不开，等同整帧全关）
}

void ValveService::set_hold(const core::ValveFrame& frame) {
    hold_ = frame;
    hold_epoch_ = io_.stop->current();
    if (hold_timer_) return;
    hold_timer_ = io_.loop->every(kHoldHeartbeatMs, [this] {
        if (!hold_) return;
        core::ValveFrame f = *hold_;
        bump_opens(f);
        auto req = io_.hw->io.req_write_valves(to_hw(f));
        bool ok = io_.stop->submit_if(hold_epoch_, [&] {
            io_.submit(*io_.hw->io_bus, req, [this](const hw::ModbusResult& r) { track(r.ok); });
        });
        if (!ok) clear_hold();  // 全部停止之后不再重写开阀帧
    });
}

void ValveService::clear_hold() {
    hold_.reset();
    if (hold_timer_) io_.loop->cancel(hold_timer_);
    hold_timer_ = 0;
}

void ValveService::mark_all_closed() { show(core::valve_frame::all_closed()); }

}  // namespace reactor::ctl
