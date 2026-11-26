#include "ctl/ctl_core.hpp"
#include "core/chan_name.hpp"

#include <algorithm>
#include <cmath>

#include "common/clock.hpp"
#include "common/log.hpp"
#include "core/num_format.hpp"
#include "ctl/ctl_sd_notify.hpp"
#include "store/store_util.hpp"
#include <chrono>
#include <cstdint>
#include <functional>
#include <map>
#include <memory>
#include <optional>
#include <string>
#include <vector>

namespace reactor::ctl {

using core::ReactorState;
using core::fmt_dec;

ControlCore::ControlCore(CoreDeps deps, CoreOptions opt)
    : opt_(std::move(opt)),
      hw_(deps.hw),
      clock_(deps.clock),
      logger_(deps.logger),
      settings_store_(deps.settings_store),
      settings_(std::move(deps.settings)),
      loop_(*deps.clock),
      io_{&loop_, deps.hw, deps.clock, &stop_},
      temp_(io_, &settings_),
      press_(io_, deps.hw->cfg.press_fullscale_psi, deps.hw->press_signal()),
      valves_(io_),
      stir_(io_),
      alarms_([this] { return clock_->wall_ms(); },
              [this](const std::string& level, const std::string& origin, const std::string& title, const std::string& detail,
                     const std::string& kind) { file_event(level, origin, title, detail, kind); }),
      gate_(opt_.tick_ms) {
    for (int i = 0; i < 8; ++i) ch_[static_cast<size_t>(i)].id = i + 1;
    // C#：设置页的 MPa 量程显示值按设备层满量程回填（满量程的权威来源是 hardware.json）
    settings_.settings.press_full_scale_mpa = std::round(core::psi_to_mpa(press_.fullscale_psi()) * 1000.0) / 1000.0;
    apply_runtime_settings();
    valves_.on_shown_changed = [this](const core::ValveFrame& prev, const core::ValveFrame& next) {
        if (auto text = core::valve_frame::change_text(prev, next, lease_.busy() ? lease_.task() : std::string()))
            file_event("info", "阀门", "阀门整帧下发", *text, "valve.frame");
    };
    stir_.on_alarm_code = [this](int code) { on_stir_alarm_code(code); };
    stir_.on_comm_error = [this](const std::string& msg) { notice("err", "搅拌通讯失败 · " + msg); };
    alarms_.on_change = [this](const AlarmItem& a) {
        for (auto& s : alarm_subs_) s.second(a);
    };
}

ControlCore::~ControlCore() { shutdown(false); }

void ControlCore::apply_runtime_settings() {
    const auto& s = settings_.settings;
    stir_.set_ramp_ms(static_cast<int>(s.stir_ramp_ms));
    stir_.set_gear_ratio(s.stir_gear_ratio);
    double fs = core::mpa_to_psi(s.press_full_scale_mpa);
    if (fs > 0 && std::fabs(fs - press_.fullscale_psi()) > 0.05) press_.set_fullscale_psi(fs);
}

void ControlCore::update_settings(const std::function<void(store::SettingsState&)>& fn, bool bump_revision) {
    fn(settings_);
    if (bump_revision) ++settings_.revision;
    apply_runtime_settings();
    if (settings_store_) settings_store_->save_soon(settings_, clock_->mono_ms());
    // 快照里的设置立即跟上：GET /settings 读快照，原来要等下一个控制周期（≤ 1.2 s），
    // 触屏改完一项马上改下一项时会拿旧 revision 撞 REVISION_CONFLICT（联调 2026-11-26）
    if (auto cur = snaps_.latest()) {
        auto s = std::make_shared<Snapshot>(*cur);
        s->settings = settings_;
        snaps_.replace_latest(std::move(s));
    }
}

std::string ControlCore::fmt_p(double psi) const { return core::format_p(psi, opt_.pressure_unit); }

std::string ControlCore::local_time(int64_t wall_ms) const {
    return store::fmt_offset_time(store::WallTime::local(wall_ms));
}

void ControlCore::file_event(const std::string& level, const std::string& origin, const std::string& title,
                             const std::string& detail, const std::string& kind) {
    if (logger_) logger_->event(store::WallTime::local(clock_->wall_ms()), level, origin, title, detail, "system", std::nullopt,
                                std::nullopt, kind);
}

void ControlCore::notice(const std::string& kind, const std::string& msg) {
    for (auto& s : notice_subs_) s.second(kind, msg);
}

int ControlCore::subscribe_alarms(std::function<void(const AlarmItem&)> fn) {
    alarm_subs_.emplace_back(next_sub_, std::move(fn));
    return next_sub_++;
}

int ControlCore::subscribe_notices(std::function<void(const std::string&, const std::string&)> fn) {
    notice_subs_.emplace_back(next_sub_, std::move(fn));
    return next_sub_++;
}

int ControlCore::add_gas_abort_listener(std::function<void(core::GasAbortCause, std::optional<int>)> fn) {
    gas_listeners_.emplace_back(next_sub_, std::move(fn));
    return next_sub_++;
}

void ControlCore::remove_gas_abort_listener(int id) {
    for (auto it = gas_listeners_.begin(); it != gas_listeners_.end(); ++it)
        if (it->first == id) { gas_listeners_.erase(it); return; }
}

// ============================ 生命周期 ============================

void ControlCore::start() {
    if (started_) return;
    started_ = true;
    if (opt_.threaded) {
        loop_.post([this] { boot(); });
        loop_.start_thread("ctl");
    } else {
        loop_.bind_current_thread();
        boot();
    }
}

void ControlCore::boot() {
    // 开机提示哪些模块没配串口（C# MainViewModel 构造函数）
    std::vector<std::string> off;
    if (!stir_.configured()) off.push_back("搅拌");
    if (!temp_.configured()) off.push_back("温控");
    if (!press_.configured()) off.push_back("压力");
    if (!valves_.configured()) off.push_back("阀门");
    std::string offs;
    for (size_t i = 0; i < off.size(); ++i) offs += (i ? " / " : "") + off[i];
    alarms_.log("info", "系统", "程序启动", std::string("reactord 控制核心") + (off.empty() ? "" : " · 未配置串口：" + offs),
                "system.boot");
    // 模拟模式不是报警：只记一条普通事件，界面靠快照 simulated 在顶栏挂「模拟模式」标（联调 2026-11-26）
    if (hw_->simulated)
        alarms_.log("info", "系统", "模拟模式", "hardware.json sim=true：全部模块为内存模拟，读数非真实设备，超温切断 / 全部停止不作用于任何硬件",
                    "system.simulated");
    if (hw_->sim_refused)
        alarms_.log("err", "系统", "已拒绝模拟模式", "hardware.json 同时设置了 sim=true 与真机串口：按真机串口运行，请删除 sim",
                    "system.simRefused");

    // 温控：Ctn 自愈、8 路 At=4、无存档 PID 时读仪表 PID 作种子（C# TempService.InitAsync）
    if (temp_.configured())
        temp_.init(!settings_.has_band_pids, [this](int id, const hw::PidParams& p) {
            settings_.band_pids[idx(id)][0] = store::Pid{p.p, p.i, p.d};  // C# _curBand 开机为 0（低温档）
            temp_.bump_pid_gen(id);
        });
    // 阀门：读回真实阀态（C# SyncValvesFromHardware）
    if (valves_.configured())
        valves_.read_back([this](bool ok, const core::ValveFrame& f) {
            if (!ok) {
                notice("err", "开机读取阀门状态失败（IO16R 通讯）· 气路图按全关显示，实际状态未知");
                return;
            }
            std::string on;
            for (int i = 0; i < core::valve_frame::kCount; ++i)
                if (f[static_cast<size_t>(i)]) on += (on.empty() ? "" : "、") + core::valve_frame::valve_name(i);
            file_event("info", "阀门", "开机读回阀态", on.empty() ? "全关" : "开启：" + on, "valve.frame");
            if (!on.empty()) notice("warn", "开机读回：有阀门处于开启状态（见气路图）");
        });
    stir_.init([this](int microstep) { settings_.settings.stir_microstep = microstep; });

    tick_timer_ = loop_.every(opt_.tick_ms, [this] { tick(); });
    if (settings_store_)
        settings_timer_ = loop_.every(opt_.settings_poll_ms, [this] { settings_store_->poll(clock_->mono_ms()); });
    if (opt_.watchdog) sd_notify("READY=1");
}

void ControlCore::shutdown(bool stop_outputs) {
    if (shut_) return;
    shut_ = true;
    auto final_steps = [this, stop_outputs] {
        if (!started_) return;
        valves_.clear_hold();
        programs_->abort_all(AbortReason::Shutdown);
        if (lease_.busy()) abort_all_gas(core::GasAbortCause::Shutdown);
        if (tick_timer_) loop_.cancel(tick_timer_);
        if (settings_timer_) loop_.cancel(settings_timer_);
        if (stop_outputs) {
            // C# ExitAppTailAsync：停搅拌、停全部温控输出、全关阀门（三条总线并行）
            if (stir_.configured()) stir_.stop(nullptr);
            if (temp_.configured())
                hw_->temp_bus->submit_seq(hw_->temp.stop_all_loops_requests(), hw::Priority::Normal, false, nullptr);
            if (valves_.configured()) hw_->io_bus->submit(hw_->io.req_close_all_valves(), hw::Priority::Normal, nullptr);
        }
        if (settings_store_) settings_store_->flush();
        alarms_.log("info", "系统", "程序退出", stop_outputs ? "已下发停搅拌 / 8 路 At=4 / 阀门全关" : "不停输出", "system.shutdown");
        if (opt_.watchdog) sd_notify("STOPPING=1");
    };
    if (opt_.threaded && started_) {
        auto f = loop_.call(final_steps);
        if (f.wait_for(std::chrono::seconds(5)) == std::future_status::ready) {
            try { f.get(); } catch (...) {}
        }
    } else {
        final_steps();
    }
    // 等停止帧落地再停总线（最多 3 s）
    for (hw::Bus* b : {hw_->temp_bus.get(), hw_->io_bus.get(), hw_->stir_bus.get(), hw_->press_bus.get()})
        if (b) b->wait_idle(3000);
    loop_.stop();
    loop_.join();
    hw_->stop();
}

void ControlCore::settle(int timeout_ms) {
    int64_t deadline = ::reactor::mono_ms() + timeout_ms;
    for (;;) {
        for (hw::Bus* b : {hw_->temp_bus.get(), hw_->io_bus.get(), hw_->stir_bus.get(), hw_->press_bus.get()})
            if (b) b->wait_idle(timeout_ms);
        size_t n = loop_.run_pending();
        bool idle = true;
        for (hw::Bus* b : {hw_->temp_bus.get(), hw_->io_bus.get(), hw_->stir_bus.get(), hw_->press_bus.get()})
            if (b && !b->idle()) idle = false;
        if (n == 0 && idle && loop_.queued() == 0) return;
        if (::reactor::mono_ms() > deadline) return;
    }
}

// ============================ 控制周期 ============================

void ControlCore::tick() {
    if (!gate_.try_enter()) return;
    int64_t t0 = ::reactor::mono_ms();
    hw_->simulate_step();  // 仅模拟：推进 PV / 压力
    if (temp_.configured()) temp_.poll();  // 结果是上一轮的（同 C# fire-and-forget）
    if (press_.configured()) press_.poll();
    stir_.tick_poll();

    bool real_t = temp_.online();
    bool real_p = press_.online();
    track_modules();

    const double over_temp = settings_.settings.over_temp;
    for (auto& c : ch_) {
        c.t_online = real_t;
        c.p_online = real_p;
        if (real_t) c.t = temp_.ch(c.id).pv;
        if (real_p && press_.last().valid()) c.p = press_.last().psi[idx(c.id)];
        update_uptake(c, real_t, real_p);
        check_over_temp(c, real_t);      // 所有通道（含停用 / 已结束 / 自整定中）
        check_over_pressure(c, real_p);  // 所有通道
        if (c.state == ReactorState::Idle || c.state == ReactorState::Done) continue;
        if (real_t && c.t >= over_temp) {  // 运行中通道的超温报警
            c.set_state(ReactorState::Alarm);
            c.over_temp = true;
        }
    }
    flush_over_pressure_valves();
    step_curves();
    step_approaches(real_t);
    check_heat_timeouts(real_t);
    check_pressure_deviation(real_p);
    programs_->on_tick(clock_->mono_ms());

    double took = static_cast<double>(::reactor::mono_ms() - t0);
    if (gate_.exit(took, clock_->mono_ms()))
        file_event("warn", "系统", "控制循环超时",
                   "本次 Tick 耗时 " + fmt_dec(took, 0) + " ms（节拍 " + std::to_string(opt_.tick_ms) + " ms）· 累计超时 " +
                       std::to_string(gate_.overruns()) + " 次 · 跳过重入 " + std::to_string(gate_.skipped()) + " 次",
                   "system.tickOverrun");
    if (logger_) logger_->telemetry(build_telemetry_row());
    if (opt_.watchdog) sd_notify("WATCHDOG=1");
    publish_snapshot();
}

bool ControlCore::any_over_pressure_tripped() const {
    for (int i = 0; i < 8; ++i)
        if (op_guard_.is_tripped(i)) return true;
    return false;
}

bool ControlCore::is_idle_over_temp_alarm(int id) const {
    const auto& c = ch_[idx(id)];
    return c.state == ReactorState::Alarm && c.over_temp && c.ot_idle_alarm && ot_guard_.is_tripped(id - 1) &&
           !op_guard_.is_tripped(id - 1);
}

// ============================ 全部停止 ============================

struct ControlCore::StopAllState {
    StopAllOptions opts;
    std::function<void(const StopAllResult&)> done;
    StopAllResult res;
    int pending = 0;       // 普通补发还没回来的笔数（总线）
    bool cleaned = false;  // 控制线程收尾已做
    bool finished = false;
};

void ControlCore::request_stop_all(StopAllOptions opts, std::function<void(const StopAllResult&)> done) {
    auto st = std::make_shared<StopAllState>();
    st->opts = opts;
    st->done = std::move(done);
    st->res.stir_requested = opts.stop_stir;
    hw::Hardware* hw = hw_;
    EventLoop* lp = &loop_;
    bool heat = hw->temp_configured(), valves = hw->io_configured(), stir = opts.stop_stir && hw->stir_configured();
    st->pending = (heat ? 1 : 0) + (valves ? 1 : 0) + (stir ? 1 : 0);
    // 同一把锁里：停止代号 +1（之后核对代号的启动 / 开阀序列不再提交）→ 紧急一遍 → 普通补发一遍（决定记录「全部停止之后」）
    stop_.bump_and([&] {
        hw->urgent_stop_all(opts.stop_stir, [lp, st, this](const hw::StopAllOutcome& o) {
            lp->post([this, st, o] {
                st->res.urgent_heat_ok = o.heat_ok;
                st->res.urgent_valves_ok = o.valves_ok;
                st->res.urgent_stir_ok = o.stir_ok;
                file_event(o.heat_ok && o.valves_ok && o.stir_ok ? "info" : "warn", "系统", "全部停止 · 紧急帧回应",
                           std::string("加热 ") + (o.heat_ok ? "已确认" : "未确认") + " · 阀门 " + (o.valves_ok ? "已确认" : "未确认") +
                               (o.stir_requested ? std::string(" · 搅拌 ") + (o.stir_ok ? "已确认" : "未确认") : ""),
                           "system.stopAll");
            });
        });
        auto arrive = [this, st](int which, bool ok) {
            if (which == 0) st->res.heat_ok = ok;
            else if (which == 1) st->res.valves_ok = ok;
            else st->res.stir_ok = ok;
            --st->pending;
            stop_all_maybe_finish(st);
        };
        if (heat)
            hw->temp_bus->submit_seq(hw->temp.stop_all_loops_requests(), hw::Priority::Normal, false,
                                     [lp, arrive, n = static_cast<size_t>(hw->temp.loops())](const std::vector<hw::ModbusResult>& rs) {
                                         bool ok = rs.size() == n;
                                         for (const auto& r : rs) ok = ok && r.ok;
                                         lp->post([arrive, ok] { arrive(0, ok); });
                                     });
        if (valves)
            hw->io_bus->submit(hw->io.req_close_all_valves(), hw::Priority::Normal, [lp, arrive](const hw::ModbusResult& r) {
                bool ok = r.ok;
                lp->post([arrive, ok] { arrive(1, ok); });
            });
        if (stir)
            hw->stir_bus->submit(hw->stir.req_stop(), hw::Priority::Normal, [lp, arrive](const hw::ModbusResult& r) {
                bool ok = r.ok;
                lp->post([arrive, ok] { arrive(2, ok); });
            });
    });
    // 控制线程收尾（控制线程卡住时上面的停止帧照样已经下去了）
    loop_.post([this, st] { stop_all_cleanup(st); });
}

void ControlCore::stop_all_cleanup(const std::shared_ptr<StopAllState>& st) {
    auto& res = st->res;
    res.gas_aborted = lease_.busy();
    res.gas_task = lease_.task();
    abort_keep_vent_ = false;  // 全部停止一律全关（含排空）
    valves_.clear_hold();
    if (lease_.busy()) abort_all_gas(core::GasAbortCause::None);
    res.programs_aborted = programs_->abort_all(AbortReason::StopAll);
    if (lease_.busy()) lease_.clear();  // 监听者没有自己释放的：全部停止是绝对的，强制释放
    programs_->clear_stir_share();
    for (auto& c : ch_) {
        if (c.profile.running()) c.profile.stop();
        c.approach.stop();
        end_heat_watch(c.id);
        end_pressure_watch(c.id);
        if (c.state != ReactorState::Idle) {
            res.channels.push_back(c.id);
            c.set_state(ReactorState::Done);
        }
        c.started_by.reset();
        // 正在自整定的被 At=4 打断：整定簿不回读存档（第二阶段）
        if (temp_.ch(c.id).autotuning && temp_.on_autotune_aborted) temp_.on_autotune_aborted(c.id);
    }
    if (st->opts.stop_stir && stir_.on()) {
        stir_.mark_stopped();
        res.stir_stopped = true;
    }
    std::string chs;
    chs = core::chan_names(res.channels);
    file_event("warn", "系统", "全部停止",
               "来源 " + st->opts.source + " · 停止帧已直接下发（紧急优先一遍 + 普通优先级一遍）· 停止通道 " +
                   (chs.empty() ? "无" : chs) + (res.gas_aborted ? " · 中止气路序列「" + res.gas_task + "」" : "") +
                   (res.programs_aborted > 0 ? " · 中止程序 " + std::to_string(res.programs_aborted) + " 个" : "") +
                   (st->opts.stop_stir ? " · 停搅拌" : "") + " · 不放空",
               "system.stopAll");
    st->cleaned = true;
    stop_all_maybe_finish(st);
}

void ControlCore::stop_all_maybe_finish(const std::shared_ptr<StopAllState>& st) {
    if (st->finished || !st->cleaned || st->pending > 0) return;
    st->finished = true;
    auto& res = st->res;
    if (res.valves_ok && valves_.configured()) valves_.mark_all_closed();
    if (!res.valves_ok)
        alarms_.log("err", "系统", "阀门全关写入失败", "IO16R 通讯异常，阀门实际状态未知，请手动关闭气源", "alarm.valveWriteFailed");
    if (!res.heat_ok)
        alarms_.log("err", "系统", "全部停止 · 停止加热未确认", "AI-8 通讯失败，加热输出可能仍在工作", "alarm.heatStopFailed");
    if (res.stir_requested && !res.stir_ok)
        alarms_.log("err", "系统", "全部停止 · 停止搅拌未确认", "DM2C 通讯失败，搅拌可能仍在运转", "alarm.stirStopFailed");
    if (st->done) st->done(res);
}

// ============================ 单通道停止 ============================

void ControlCore::stop_channel(int id, bool disable, std::function<void(const ChannelStopResult&)> cb) {
    auto& c = ch_[idx(id)];
    auto res = std::make_shared<ChannelStopResult>();
    if (lease_.includes(id)) res->gas_aborted = lease_.task();
    c.set_state(disable ? ReactorState::Idle : ReactorState::Done);
    c.started_by.reset();
    if (c.profile.running()) c.profile.stop();
    c.approach.stop();
    end_heat_watch(id);
    end_pressure_watch(id);
    // C# ReleaseChannelFromGasAsync：先标失效（取消晚到一步时序列的安全检查也会中止），程序 → 中止程序，否则中止该序列
    lease_.invalidate(id);
    if (programs_->is_owned(id)) res->program_aborted = programs_->abort_channel(id, AbortReason::ChannelStop);
    if (!res->program_aborted && lease_.includes(id))
        for (auto& l : gas_listeners_) l.second(core::GasAbortCause::None, id);

    auto finish_valve = [this, id, res, cb] {
        if (!valves_.configured() || !valves_.sv_open(id)) {
            if (cb) cb(*res);
            return;
        }
        valves_.close_sv(id, [this, id, res, cb](bool ok, const std::string& err) {
            if (!ok) {
                res->valve_ok = false;
                res->valve_error = "SV" + std::to_string(id) + " 关闭失败（IO16R 通讯）· 阀门可能仍开启";
                alarms_.log("err", "RV" + std::to_string(id), "SV" + std::to_string(id) + " 关闭失败",
                            "IO16R 通讯异常，阀门可能仍开启" + (err.empty() ? std::string() : " · " + err), "alarm.valveWriteFailed");
            }
            if (cb) cb(*res);
        });
    };
    if (res->program_aborted || !temp_.configured()) {  // 程序收尾自己停加热，不重复下发
        finish_valve();
        return;
    }
    temp_.stop_channel(id, [this, id, res, finish_valve](bool ok) {
        if (!ok) {
            res->heat_ok = false;
            res->heat_error = core::chan_name(id) + " 停止加热未确认（温控通讯失败）· 请点主页的「全部停止」或到设备现场确认";
            alarms_.log("err", "RV" + std::to_string(id), "停止加热未确认", "AI-8 通讯失败，加热输出可能仍在工作",
                        "alarm.heatStopFailed");
        }
        finish_valve();
    });
}

// ============================ 阀门 / 搅拌 ============================

void ControlCore::write_valves(const core::ValveFrame& f, ValveWriteOpts opts, ValveService::DoneFn cb) {
    opts.lease_busy = lease_.busy();
    opts.participants = lease_.participants();
    opts.lease_task = lease_.task();
    valves_.write_frame(f, opts, std::move(cb));
}

void ControlCore::close_all_valves(ValveService::DoneFn cb) {
    valves_.close_all(abort_keep_vent_, [this, cb](bool ok, const std::string& err) {
        if (!ok) {
            notice("err", "阀门全关写入失败（IO16R 通讯）· 请立即手动关闭气源");
            alarms_.log("err", "系统", "阀门全关写入失败", "IO16R 通讯异常，阀门实际状态未知，请手动关闭气源", "alarm.valveWriteFailed");
        }
        if (cb) cb(ok, err);
    });
}

void ControlCore::close_sv(int id, ValveService::DoneFn cb) {
    if (!valves_.configured()) { if (cb) cb(true, ""); return; }
    valves_.close_sv(id, [this, id, cb](bool ok, const std::string& err) {
        if (!ok)
            alarms_.log("err", "RV" + std::to_string(id), "SV" + std::to_string(id) + " 关闭失败",
                        "IO16R 通讯异常，阀门可能仍开启" + (err.empty() ? std::string() : " · " + err), "alarm.valveWriteFailed");
        if (cb) cb(ok, err);
    });
}

void ControlCore::stir_start(int rpm, std::function<void(bool)> cb) { stir_.start(rpm, stop_.current(), std::move(cb)); }
void ControlCore::stir_stop(std::function<void(bool)> cb) { stir_.stop(std::move(cb)); }
void ControlCore::stir_set_rpm(int rpm, std::function<void(bool)> cb) { stir_.set_rpm(rpm, stop_.current(), std::move(cb)); }
void ControlCore::stir_clear_fault(std::function<void(bool)> cb) { stir_.clear_alarm(std::move(cb)); }

// ============================ 气路租约 ============================

int64_t ControlCore::begin_gas(core::GasOwner owner, const std::string& task, const std::vector<int>& participants,
                               const std::vector<int>& watch, std::optional<int> owner_rv) {
    std::map<int, ReactorState> start;
    for (int id : participants)
        if (id >= 1 && id <= 8) start[id] = ch_[idx(id)].state;
    last_abort_cause_ = core::GasAbortCause::None;
    return lease_.begin(owner, task, participants, watch, start, owner_rv);
}

bool ControlCore::end_gas(core::GasOwner owner, int64_t gen) {
    if (!lease_.try_end(owner, gen)) return false;
    abort_keep_vent_ = false;
    return true;
}

void ControlCore::abort_all_gas(core::GasAbortCause cause) {
    if (lease_.busy()) last_abort_cause_ = cause;
    valves_.clear_hold();
    for (auto& l : gas_listeners_) l.second(cause, std::nullopt);
}

// ============================ 新三级警告的监视 ============================

void ControlCore::begin_heat_watch(int id, double target, double band_c) {
    auto& w = ch_[idx(id)].heat_watch;
    if (w.warned) alarms_.clear("ht:" + std::to_string(id), "RV" + std::to_string(id), "升温超时 · 解除", "目标温度已更改，重新计时",
                                "alarm.heatTimeoutCleared");
    w = HeatWatch{};
    w.active = true;
    w.target = target;
    w.band = band_c;
    w.since_ms = clock_->mono_ms();
}

void ControlCore::end_heat_watch(int id) {
    auto& w = ch_[idx(id)].heat_watch;
    if (w.warned && alarms_.has_active("ht:" + std::to_string(id)))
        alarms_.clear("ht:" + std::to_string(id), "RV" + std::to_string(id), "升温超时 · 解除", "加热已停止",
                      "alarm.heatTimeoutCleared");
    w = HeatWatch{};
}

void ControlCore::begin_pressure_watch(int id, double target_psi) {
    end_pressure_watch(id);
    auto& w = ch_[idx(id)].press_watch;
    w.active = true;
    w.target_psi = target_psi;
}

void ControlCore::end_pressure_watch(int id) {
    auto& w = ch_[idx(id)].press_watch;
    if (w.warned)
        alarms_.clear("pd:" + std::to_string(id), "RV" + std::to_string(id), "压力偏离 · 解除", "稳压已结束",
                      "alarm.pressureDeviationCleared");
    w = PressWatch{};
}

// ============================ 吸气量 ============================

double ControlCore::gas_volume_ml(int id) const {
    const auto& s = settings_.settings;
    return core::gas_uptake::channel_gas_volume_ml(s.effective_volume[idx(id)], ch_[idx(id)].vol, s.vessel_volume,
                                                   s.gas_line_volume);
}

void ControlCore::update_uptake(Channel& c, bool real_t, bool real_p) {
    if (!c.uptake.counting()) return;
    c.uptake.sample(clock_->mono_ms(), real_t && real_p, c.p, c.t, gas_volume_ml(c.id), valves_.sv_open(c.id),
                    valves_.sv_open_gen(c.id));
}

bool ControlCore::start_uptake(int id, const std::string& why) {
    auto& c = ch_[idx(id)];
    if (c.uptake.counting()) return false;
    const auto& s = settings_.settings;
    double v = gas_volume_ml(id);
    std::string basis = core::gas_uptake::volume_basis_text(s.effective_volume[idx(id)], c.vol, s.vessel_volume, s.gas_line_volume);
    std::string rv = core::chan_name(id);
    if (v < core::gas_uptake::kMinGasVolumeMl) {
        alarms_.log("warn", "系统", rv + " 吸气量未计量",
                    "气相体积 " + fmt_dec(v, 1) + " mL 不合理（" + basis + " mL）· 请检查容积标定、设置与溶液体积", "uptake.rejected");
        return false;
    }
    c.uptake.start(clock_->mono_ms(), valves_.sv_open_gen(id));
    alarms_.log("info", "系统", rv + " 开始计量吸气量",
                why + " · 气相体积 " + fmt_dec(v, 1) + " mL（" + basis + "）" + (c.vol <= 0 ? " · 溶液体积为 0，按空釜计算" : ""),
                "uptake.started");
    return true;
}

void ControlCore::stop_uptake(int id, const std::string& why) {
    auto& c = ch_[idx(id)];
    if (!c.uptake.counting()) return;
    c.uptake.stop();
    alarms_.log("info", "系统", core::chan_name(id) + " 吸气量计量结束",
                why + " · 累计 " + fmt_dec(c.uptake.mmol(), 2, 2) + " mmol" +
                    (c.uptake.refills() > 0 ? " · 期间补气 / 开阀 " + std::to_string(c.uptake.refills()) + " 次" : ""),
                "uptake.stopped");
}

void ControlCore::reset_uptake(int id) { ch_[idx(id)].uptake.reset(); }

}  // namespace reactor::ctl
