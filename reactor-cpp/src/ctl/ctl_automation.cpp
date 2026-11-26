#include "ctl/ctl_automation.hpp"

#include "common/log.hpp"

namespace reactor::ctl {

Automation::Automation(AutomationDeps d) : d_(std::move(d)) {
    ControlCore& core = *d_.core;
    programs_ = std::make_unique<ProgramEngine>(core, d_.sessions, d_.unit);
    leak_ = std::make_unique<LeakTest>(core, programs_.get(), d_.logger, d_.sessions, d_.unit);
    calib_ = std::make_unique<VolumeCalib>(core, programs_.get(), leak_.get(), d_.logger, d_.unit);
    autotune_ = std::make_unique<Autotune>(core, d_.unit);
    core.set_program_hooks(programs_.get());
    core.set_activity_hooks(this);
    if (d_.library) {
        seed_ = builtin_templates::seed(*d_.library, d_.model);
        for (const auto& e : seed_.errors) LOGW("ctl", "%s", e.c_str());
    }
}

Automation::~Automation() {
    d_.core->set_program_hooks(nullptr);
    d_.core->set_activity_hooks(nullptr);
    autotune_.reset();
    calib_.reset();
    leak_.reset();
    programs_.reset();
}

OpResult Automation::gas_locked_for_manual() const {
    const auto& l = d_.core->gas_lease();
    if (l.busy()) return OpResult::fail(core::op_codes::kGasBusy, l.task() + "进行中 · 阀门由自动序列控制", "warn");
    std::string pend = programs_->gas_pending();
    if (!pend.empty()) return OpResult::fail(core::op_codes::kGasBusy, pend + "进行中 · 阀门由程序控制", "warn");
    return OpResult::none();
}

std::optional<core::GuardReject> Automation::gas_block(bool need_pressure) const {
    return gas_ops::gas_block(*d_.core, need_pressure, std::nullopt, false, {}, programs_->gas_pending());
}

ojson Automation::leak_test() const { return leak_->snapshot(); }
ojson Automation::calib() const { return calib_->snapshot(); }
ojson Automation::autotune() const { return autotune_->snapshot(); }

// ============================ 任何线程 ============================

template <class F>
std::future<OpResult> Automation::post(F f) {
    auto p = std::make_shared<std::promise<OpResult>>();
    auto fut = p->get_future();
    if (!d_.core->post([p, f]() mutable {
            try { p->set_value(f()); } catch (...) { p->set_exception(std::current_exception()); }
        }))
        p->set_value(OpResult::fail(core::op_codes::kInternal, "控制线程已停止"));
    return fut;
}

std::future<OpResult> Automation::post_async(std::function<void(std::function<void(const OpResult&)>)> f) {
    auto p = std::make_shared<std::promise<OpResult>>();
    auto fut = p->get_future();
    if (!d_.core->post([p, f] { f([p](const OpResult& r) { p->set_value(r); }); }))
        p->set_value(OpResult::fail(core::op_codes::kInternal, "控制线程已停止"));
    return fut;
}

std::future<OpResult> Automation::replace_program_async(int ch, nlohmann::json steps, std::optional<std::string> name) {
    return post([this, ch, steps, name] { return programs_->replace_program(ch, steps, name); });
}
std::future<OpResult> Automation::copy_program_async(int from, std::vector<int> to, bool strict) {
    return post([this, from, to, strict] { return programs_->copy_program(from, to, strict); });
}
std::future<OpResult> Automation::check_run_async(int ch) {
    return post([this, ch] { return programs_->check_run(ch); });
}
std::future<OpResult> Automation::run_program_async(int ch, std::string started_by) {
    return post([this, ch, started_by] { return programs_->run(ch, started_by); });
}
std::future<OpResult> Automation::abort_program_async(int ch) {
    return post([this, ch] { return programs_->abort(ch); });
}
std::future<OpResult> Automation::start_leak_test_async(std::vector<int> channels, std::optional<double> test_psi, std::string started_by) {
    return post([this, channels, test_psi, started_by] { return leak_->start(channels, test_psi, started_by); });
}
std::future<OpResult> Automation::abort_leak_test_async(bool strict) {
    return post([this, strict] { return leak_->abort(strict); });
}
std::future<OpResult> Automation::start_calib_async(int ch, bool with_water, double water_ml, bool redo_manifold, std::string started_by) {
    return post([this, ch, with_water, water_ml, redo_manifold, started_by] {
        return calib_->start(ch, with_water, water_ml, redo_manifold, started_by);
    });
}
std::future<OpResult> Automation::abort_calib_async() {
    return post([this] { return calib_->abort(); });
}
std::future<OpResult> Automation::discard_calib_async() {
    return post([this] { return calib_->discard_first(); });
}
std::future<OpResult> Automation::start_autotune_async(int ch, double temp_c, std::optional<int> band, bool strict) {
    return post_async([this, ch, temp_c, band, strict](std::function<void(const OpResult&)> done) {
        autotune_->start(ch, temp_c, band, strict, std::move(done));
    });
}
std::future<OpResult> Automation::cancel_autotune_async(int ch) {
    return post_async([this, ch](std::function<void(const OpResult&)> done) { autotune_->cancel(ch, std::move(done)); });
}
std::future<ojson> Automation::draft_json_async(int ch) {
    return d_.core->call([this, ch] { return programs_->draft_json(ch); });
}

}  // namespace reactor::ctl
