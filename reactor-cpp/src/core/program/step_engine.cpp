#include "core/program/step_engine.hpp"
#include "core/chan_name.hpp"

#include <algorithm>
#include <cmath>

#include "core/program/step_fields.hpp"
#include "core/valve_frame.hpp"
#include <cstdint>
#include <memory>
#include <optional>
#include <string>
#include <vector>

namespace reactor::core::program {

namespace step_engine {

bool breaks_gas(StepType t) {
    return t == StepType::Leak || t == StepType::Exchange || t == StepType::Press || t == StepType::Vent || t == StepType::End;
}

double leak_drop_rate(double p0, std::optional<double> t0, double p1, std::optional<double> t1, double hold_min) {
    double p1c = (t0 && t1 && *t0 > -273 && *t1 > -273) ? leak_math::compensate(p1, *t1, *t0) : p1;
    return hold_min > 0 ? (p0 - p1c) / (hold_min / 60.0) : 0;
}

}  // namespace step_engine

namespace {

using namespace step_engine;

enum class Sub { Running, Done, Fault };

std::string N(double v) { return fmt_n(v); }

// 一次 tick 的上下文：输入、输出、配置
struct Ctx {
    const StepInput& in;
    StepOutput& out;
    int rv;
    PressureUnit unit;
    const std::string& head;
    bool& gas_held;
    std::string rvs() const { return chan_name(rv); }
    int64_t now() const { return in.now_ms; }

    std::string P(double psi) const { return format_p(psi, unit); }
    void act(Action a) { out.actions.push_back(std::move(a)); }
    void status(std::string t) { Action a; a.kind = ActionKind::Status; a.text = std::move(t); act(std::move(a)); }
    void log(const char* level, std::string title, std::string detail) {
        Action a; a.kind = ActionKind::Log; a.level = level; a.text = std::move(title); a.detail = std::move(detail); act(std::move(a));
    }
    void set_state(ReactorState s) { Action a; a.kind = ActionKind::SetState; a.state = s; act(std::move(a)); }
    void set_psp(double psi) { Action a; a.kind = ActionKind::SetPressureSp; a.value = psi; act(std::move(a)); }
    void simple(ActionKind k, std::string text = "") { Action a; a.kind = k; a.text = std::move(text); act(std::move(a)); }
    void valves(ValveCmd v, int gas = 0) { Action a; a.kind = ActionKind::Valves; a.valve = v; a.gas = gas; act(std::move(a)); }
    void release_gas() {
        if (!gas_held) return;
        simple(ActionKind::ReleaseGas);
        gas_held = false;
    }
    void wake_after(double sec) { out.wake_at_ms = now() + static_cast<int64_t>(std::llround(sec * 1000.0)); }
    void await(Await a) { out.waiting = a; }
    void fail(FaultKind k, std::string msg) { out.fault_kind = k; out.fault = std::move(msg); }
    double el_sec(int64_t t0) const { return static_cast<double>(now() - t0) / 1000.0; }
    double el_min(int64_t t0) const { return static_cast<double>(now() - t0) / 60000.0; }
    // 立即读一次压力（C# ReadPressureAsync：读不到抛 GasFaultException）
    bool read_p(double& p) {
        if (!in.pressure_psi) { fail(FaultKind::Gas, rvs() + " 压力读取失败"); return false; }
        p = *in.pressure_psi;
        return true;
    }
    std::string left(int64_t t0, double cap_min) const { return fmt_dec(std::max(0.0, cap_min - el_min(t0)), 1) + " 分"; }
    std::string pv() const { return in.temp_c ? fmt_dec(*in.temp_c, 1, 1) + "℃" : "—"; }
};

// ============ 取气路（排队）============
struct GasAcquire {
    std::string task;
    bool vessel = true;
    bool requested = false;
    Sub tick(Ctx& c) {
        if (!requested) {
            Action a; a.kind = ActionKind::RequestGas; a.text = task; a.flag = vessel;
            c.act(std::move(a));
            c.await(Await::Gas);
            requested = true;
            return Sub::Running;
        }
        c.gas_held = true;   // 只有收到 GasGranted 才会走到这里
        return Sub::Done;
    }
};

// ============ 单通道自适应脉冲充压（C#: FillToAsync）============
// 首轮 0.2 s 试探，之后按实测增益补 60% 差值，关阀静置 1 s 再读，从下方逼近不超调。
// 到 目标 − max(0.5, 目标 × tol%) 以上算到位；超过 maxSec 或连续 5 次几乎没涨按「充不上去」故障（Gas）。
struct FillTo {
    int gi = 0;
    double target = 0, tol_pct = 0, max_sec = 0;
    enum class Ph { Start, Check, Open, Settle } ph = Ph::Start;
    std::optional<double> gain;
    int stall = 0;
    int64_t t0 = 0;
    double sec = 0, p0 = 0, result = 0;

    Sub tick(Ctx& c) {
        for (;;) {
            switch (ph) {
                case Ph::Start: t0 = c.now(); ph = Ph::Check; break;
                case Ph::Check: {
                    double tol = std::max(0.5, target * tol_pct / 100.0);
                    if (!c.read_p(p0)) return Sub::Fault;
                    if (p0 >= target - tol) { result = p0; return Sub::Done; }
                    if (c.el_sec(t0) > max_sec) {
                        c.fail(FaultKind::Gas, c.rvs() + " 充压 " + N(max_sec) + " s 未到 " + c.P(target) + "（当前 " + c.P(p0) +
                                                   "，气源压力不够或减压阀没开）");
                        return Sub::Fault;
                    }
                    sec = (gain && *gain > 0.05) ? std::clamp(0.6 * (target - p0) / *gain, 0.1, 2.0) : 0.2;
                    c.valves(ValveCmd::GasToVessel, gi);
                    c.wake_after(sec);
                    ph = Ph::Open;
                    return Sub::Running;
                }
                case Ph::Open:
                    c.valves(ValveCmd::AllClosed);
                    c.wake_after(1.0);   // 静置再读，避开充气瞬间的动态压力
                    c.out.fresh_pressure = true;
                    ph = Ph::Settle;
                    return Sub::Running;
                case Ph::Settle: {
                    double p1;
                    if (!c.read_p(p1)) return Sub::Fault;
                    gain = std::max(0.01, (p1 - p0) / sec);
                    stall = p1 - p0 < 0.3 ? stall + 1 : 0;
                    if (stall >= 5) {
                        c.fail(FaultKind::Gas, c.rvs() + " 充压无响应（气源压力不足或管路不通）");
                        return Sub::Fault;
                    }
                    c.status(c.head + " · 充压 " + c.P(p1) + " / " + c.P(target));
                    ph = Ph::Check;
                    break;
                }
            }
        }
    }
};

// ============ 开 SVn + 排空，每 0.2 s 读压，到 ≤ to 关阀（C#: VentToAsync）============
struct VentTo {
    double to = 0, max_sec = 0;
    std::string status, what;
    enum class Ph { Start, Poll } ph = Ph::Start;
    int64_t t0 = 0;
    double result = 0;

    Sub tick(Ctx& c) {
        if (ph == Ph::Start) {
            c.valves(ValveCmd::VentVessel);
            t0 = c.now();
            c.wake_after(kPollSec);
            c.out.fresh_pressure = true;
            ph = Ph::Poll;
            return Sub::Running;
        }
        double p;
        if (!c.read_p(p)) return Sub::Fault;
        c.status(status + " · 放 " + c.P(p) + " / " + c.P(to));
        if (p <= to) {
            c.valves(ValveCmd::AllClosed);
            result = p;
            return Sub::Done;
        }
        if (c.el_sec(t0) >= max_sec) {
            c.valves(ValveCmd::AllClosed);
            std::string r = c.rvs();
            c.fail(FaultKind::Step, what == "放空"
                                        ? r + " 放空 " + N(max_sec) + " s 后釜压仍 " + c.P(p) + " · 釜里的气放不掉 · 不要开釜"
                                        : r + " " + what + "：开 SV" + std::to_string(c.rv) + " + 排空阀 " + N(max_sec) + " s 后釜压仍 " +
                                              c.P(p) + " · 釜里的气放不掉（可能有进气单向阀）· 不要开釜");
            return Sub::Fault;
        }
        c.wake_after(kPollSec);
        c.out.fresh_pressure = true;
        return Sub::Running;
    }
};

// ============ 一次置换：充到 fill → 静置 1 s → 放到 release（C#: ExchangeCycleAsync）============
struct ExchangeCycle {
    int gi = 0;
    double fill = 0, release = 0, max_sec = 0;
    std::string status, what;
    enum class Ph { Start, FillPoll, Rest, Vent } ph = Ph::Start;
    int64_t t0 = 0;
    VentTo vent;

    Sub tick(Ctx& c) {
        for (;;) {
            switch (ph) {
                case Ph::Start:
                    c.valves(ValveCmd::GasToVessel, gi);
                    t0 = c.now();
                    c.wake_after(kPollSec);
                    c.out.fresh_pressure = true;
                    ph = Ph::FillPoll;
                    return Sub::Running;
                case Ph::FillPoll: {
                    double cap = program_limits::press_max(c.in.limits);
                    double p;
                    if (!c.read_p(p)) return Sub::Fault;
                    c.status(status + " · 充 " + c.P(p) + " / " + c.P(fill));
                    if (p >= fill || p >= cap) {
                        c.valves(ValveCmd::AllClosed);
                        c.wake_after(1.0);
                        ph = Ph::Rest;
                        return Sub::Running;
                    }
                    if (c.el_sec(t0) >= max_sec) {
                        c.valves(ValveCmd::AllClosed);
                        c.fail(FaultKind::Step, c.rvs() + " " + what + "：充气 " + N(max_sec) + " s 未到 " + c.P(fill) +
                                                    "（气源压力不够或阀门没开）");
                        return Sub::Fault;
                    }
                    c.wake_after(kPollSec);
                    c.out.fresh_pressure = true;
                    return Sub::Running;
                }
                case Ph::Rest:
                    vent = VentTo{};
                    vent.to = release; vent.max_sec = max_sec; vent.status = status; vent.what = what;
                    ph = Ph::Vent;
                    break;
                case Ph::Vent: return vent.tick(c);
            }
        }
    }
};

// ============ 放空（vent 步骤、气密检查之后 / 不通过）（C#: VentCoreAsync）============
struct VentCore {
    double to = 0, max_sec = 0, flush_pres = 0;
    int flush = 0;
    enum class Ph { Start, Gas, Vent, Cycles, Finish } ph = Ph::Start;
    GasAcquire gas;
    VentTo vent;
    ExchangeCycle cyc;
    int i = 0;
    double p = 0;

    void next_cycle(Ctx& c) {
        cyc = ExchangeCycle{};
        cyc.gi = 0; cyc.fill = flush_pres; cyc.release = to; cyc.max_sec = max_sec;
        cyc.status = c.head + " · 惰性置换 " + std::to_string(i) + "/" + std::to_string(flush);
        cyc.what = "放空后置换";
    }

    Sub tick(Ctx& c) {
        for (;;) {
            switch (ph) {
                case Ph::Start: {
                    c.simple(ActionKind::StopUptake, "放空");
                    if (c.in.temp_c && *c.in.temp_c > kHotVentC)
                        c.log("warn", c.rvs() + " 热态放空", "当前 " + fmt_dec(*c.in.temp_c, 1, 1) + "℃ · 热态放空会带出溶剂蒸气");
                    if (c.in.gas_block_vessel) { c.fail(FaultKind::Step, "放空：" + *c.in.gas_block_vessel); return Sub::Fault; }
                    c.set_state(ReactorState::Pressing);
                    c.set_psp(0);
                    c.status(c.head + " · 放空");
                    gas = GasAcquire{c.rvs() + " 程序 · 放空", true};
                    ph = Ph::Gas;
                    break;
                }
                case Ph::Gas: {
                    Sub r = gas.tick(c);
                    if (r != Sub::Done) return r;
                    vent = VentTo{};
                    vent.to = to; vent.max_sec = max_sec; vent.status = c.head + " · 放空"; vent.what = "放空";
                    ph = Ph::Vent;
                    break;
                }
                case Ph::Vent: {
                    Sub r = vent.tick(c);
                    if (r != Sub::Done) return r;
                    p = vent.result;
                    i = 1;
                    if (i <= flush) next_cycle(c);
                    ph = Ph::Cycles;
                    break;
                }
                case Ph::Cycles: {
                    if (i > flush) { ph = Ph::Finish; break; }
                    Sub r = cyc.tick(c);
                    if (r != Sub::Done) return r;
                    ++i;
                    if (i <= flush) next_cycle(c);
                    break;
                }
                case Ph::Finish: {
                    if (flush > 0 && !c.read_p(p)) return Sub::Fault;
                    c.valves(ValveCmd::AllClosed);
                    c.release_gas();
                    c.log("info", c.rvs() + " 已放空到 " + c.P(p),
                          flush > 0 ? "之后惰性置换 " + std::to_string(flush) + " 次（" + c.P(flush_pres) + "）· 阀全关" : "不置换 · 阀全关");
                    return Sub::Done;
                }
            }
        }
    }
};

// ============ 等 |PV − target| ≤ band（C#: WaitBandAsync）============
enum class Band { Running, InBand, Timeout, Fault };
struct WaitBand {
    double target = 0, band = 0, max_wait_min = 0;
    int64_t since = 0;
    std::optional<int64_t> offline;

    Band tick(Ctx& c) {
        if (c.in.temp_c) {
            offline.reset();
            if (std::fabs(*c.in.temp_c - target) <= band) return Band::InBand;
        } else {
            if (!offline) offline = c.now();
            if (c.el_sec(*offline) >= kOfflineSec) { c.fail(FaultKind::Step, "温控离线超过 60s，升温中止"); return Band::Fault; }
        }
        if (c.el_min(since) >= max_wait_min) return Band::Timeout;
        const std::string& ap = c.in.approach_text;
        c.status(c.head + " · 升温 " + c.pv() + " → " + N(target) + "℃" + (ap.empty() ? std::string() : " · " + ap));
        c.wake_after(2);
        return Band::Running;
    }
};

std::string comp_text(std::optional<double> t0, std::optional<double> t1) {
    return t0 && t1 ? " · 温度补偿 " + fmt_dec(*t0, 1, 1) + " → " + fmt_dec(*t1, 1, 1) + "℃" : std::string(" · 温度读不到，未补偿");
}

}  // namespace

// ============ StepRunner ============

struct StepRunner::Impl {
    StepSnapshot s;
    std::string head;
    RunnerConfig cfg;

    bool started = false;
    bool finished = false;
    bool gas_held = false;
    int64_t wake_at = -1;
    Await waiting = Await::None;
    bool curve_rejected = false;

    // 通用阶段号（各步骤自己解释）
    int ph = 0;
    int64_t t0 = 0;
    int i = 0;
    double p0 = 0, val = 0;
    std::optional<double> temp0;
    std::optional<std::string> pending_fault;
    std::optional<int64_t> offline;
    std::optional<int64_t> low_since;
    bool metered = false;

    GasAcquire gas;
    FillTo fill;
    VentCore vent;
    ExchangeCycle cyc;
    WaitBand band;

    Impl(StepSnapshot snap, std::string h, RunnerConfig c) : s(std::move(snap)), head(std::move(h)), cfg(c) {}

    Sub run(Ctx& c);
    Sub leak(Ctx& c);
    Sub exchange(Ctx& c);
    Sub stir(Ctx& c);
    Sub heat(Ctx& c);
    Sub press(Ctx& c);
    Sub hold(Ctx& c);
    Sub react(Ctx& c);
    Sub end(Ctx& c);

    // 超时：stop = 故障（停止这一路）；continue = 记 warn，进入下一步
    Sub timed_out(Ctx& c, bool stop, const std::string& msg) {
        if (stop) { c.fail(FaultKind::Step, msg); return Sub::Fault; }
        c.log("warn", c.rvs() + " 程序 · 超时继续", msg + " · 按设置继续下一步");
        return Sub::Done;
    }
    void start_vent(double to, double max_sec, int flush, double flush_pres) {
        vent = VentCore{};
        vent.to = to; vent.max_sec = max_sec; vent.flush = flush; vent.flush_pres = flush_pres;
    }
};

StepRunner::StepRunner(StepSnapshot step, std::string head, RunnerConfig cfg)
    : impl_(std::make_unique<Impl>(std::move(step), std::move(head), cfg)) {}
StepRunner::~StepRunner() = default;
StepRunner::StepRunner(StepRunner&&) noexcept = default;
StepRunner& StepRunner::operator=(StepRunner&&) noexcept = default;

const StepSnapshot& StepRunner::step() const { return impl_->s; }
bool StepRunner::finished() const { return impl_->finished; }
bool StepRunner::gas_held() const { return impl_->gas_held; }

std::vector<Action> StepRunner::abort() {
    std::vector<Action> acts;
    if (impl_->gas_held) {
        Action a; a.kind = ActionKind::ReleaseGas;
        acts.push_back(a);
        impl_->gas_held = false;
    }
    impl_->finished = true;
    return acts;
}

StepOutput StepRunner::tick(const StepInput& in) {
    Impl& m = *impl_;
    StepOutput out;
    if (m.finished) { out.status = StepStatus::Done; return out; }
    // 等外部事件：事件没到就原样返回
    if (m.waiting != Await::None) {
        bool ok = (m.waiting == Await::Gas && in.event == StepEvent::GasGranted) ||
                  (m.waiting == Await::LineFlush && in.event == StepEvent::LineFlushDone) ||
                  (m.waiting == Await::Curve && (in.event == StepEvent::CurveFinished || in.event == StepEvent::CurveRejected));
        if (!ok) { out.waiting = m.waiting; return out; }
        if (m.waiting == Await::Curve) m.curve_rejected = in.event == StepEvent::CurveRejected;
        m.waiting = Await::None;
    } else if (m.wake_at >= 0 && in.now_ms < m.wake_at) {
        // 没到点：原样返回
        out.wake_at_ms = m.wake_at;
        return out;
    }
    m.wake_at = -1;

    Ctx c{in, out, m.cfg.rv, m.cfg.unit, m.head, m.gas_held};
    Sub r = m.run(c);
    switch (r) {
        case Sub::Running:
            m.waiting = out.waiting;
            m.wake_at = out.waiting == Await::None ? out.wake_at_ms : -1;
            out.status = StepStatus::Running;
            break;
        case Sub::Done:
            c.release_gas();   // 正常不会还持有；保险
            out.status = StepStatus::Done;
            out.wake_at_ms = -1;
            out.waiting = Await::None;
            m.finished = true;
            break;
        case Sub::Fault:
            c.release_gas();   // 出错时归还气路（C# await using 的 DisposeAsync）
            out.status = StepStatus::Fault;
            out.wake_at_ms = -1;
            out.waiting = Await::None;
            m.finished = true;
            break;
    }
    return out;
}

Sub StepRunner::Impl::run(Ctx& c) {
    if (!started) {
        started = true;
        if (breaks_gas(s.type())) {
            if (c.in.regulation_active) {
                c.simple(ActionKind::StopRegulation);
                c.log("info", c.rvs() + " 稳压结束（进入 " + s.name() + "）",
                      "第 " + fmt_00(s.ordinal()) + " 步 " + s.name() + "开始，后台稳压停止");
            }
            c.simple(ActionKind::StopUptake, "第 " + fmt_00(s.ordinal()) + " 步" + s.name());
        }
    }
    switch (s.type()) {
        case StepType::Leak: return leak(c);
        case StepType::Exchange: return exchange(c);
        case StepType::Stir: return stir(c);
        case StepType::Heat: return heat(c);
        case StepType::Press: return press(c);
        case StepType::Hold: return hold(c);
        case StepType::React: return react(c);
        case StepType::Vent: {
            if (ph == 0) {
                start_vent(s.num("to", 2.9), s.num("maxSec", 120), static_cast<int>(round_even(s.num("flush", 3))), s.num("flushPres", 43.5));
                ph = 1;
            }
            return vent.tick(c);
        }
        case StepType::End: return end(c);
        default:
            c.fail(FaultKind::Step, "第 " + fmt_00(s.ordinal()) + " 步「" + zh(s.type()) + "」是旧格式，须先转换");
            return Sub::Fault;
    }
}

// ============ leak 气密检查 ============
// 0 开始 → 1 取气路 → 2 充气 → 3 静置 → 4 保压 → 5 放空（之后结束或按 pending_fault 失败）
Sub StepRunner::Impl::leak(Ctx& c) {
    double pres = s.num("pres", 435.1), settle = s.num("settle", 5), hold_min = s.num("hold", 30);
    double max_drop = s.num("maxDrop", step_specs::kLeakRateDefault);
    bool stop_on_fail = s.str("onFail", "stop") != "continue", vent_after = s.str("after", "vent") != "keep";
    for (;;) {
        switch (ph) {
            case 0:
                if (c.in.gas_block_vessel) { c.fail(FaultKind::Step, "气密检查：" + *c.in.gas_block_vessel); return Sub::Fault; }
                c.set_state(ReactorState::Pressing);
                c.set_psp(pres);
                c.status(head);
                c.log("info", c.rvs() + " 程序 · 气密检查",
                      "惰性气体充到 " + c.P(pres) + " · 稳定 " + N(settle) + " min · 保压 " + N(hold_min) + " min · 允许 ≤ " + step_specs::p_rate(max_drop, c.unit));
                gas = GasAcquire{c.rvs() + " 程序 · 气密检查", true};
                ph = 1;
                break;
            case 1: {
                Sub r = gas.tick(c);
                if (r != Sub::Done) return r;
                fill = FillTo{};
                fill.gi = 0; fill.target = pres; fill.tol_pct = kLeakTolPct; fill.max_sec = kLeakFillMaxSec;
                ph = 2;
                break;
            }
            case 2: {
                Sub r = fill.tick(c);
                if (r != Sub::Done) return r;
                // 充完释放气路：静置与保压期间 SV 关着，别的路可以同时充气
                c.release_gas();
                c.set_state(ReactorState::Wait);
                t0 = c.now();
                ph = 3;
                break;
            }
            case 3:
                if (c.el_min(t0) < settle) {
                    c.status(head + " · 静置 剩 " + c.left(t0, settle));
                    c.wake_after(1);
                    c.out.fresh_pressure = true;
                    return Sub::Running;
                }
                if (!c.read_p(p0)) return Sub::Fault;
                temp0 = c.in.temp_c;
                t0 = c.now();
                ph = 4;
                break;
            case 4: {
                if (c.el_min(t0) < hold_min) {
                    c.status(head + " · 保压 剩 " + c.left(t0, hold_min) + " · P0 " + c.P(p0) + " · 当前 " +
                             (c.in.pressure_psi ? c.P(*c.in.pressure_psi) : std::string("—")));
                    c.wake_after(1);
                    c.out.fresh_pressure = true;
                    return Sub::Running;
                }
                double p1;
                if (!c.read_p(p1)) return Sub::Fault;
                std::optional<double> temp1 = c.in.temp_c;
                double rate = leak_drop_rate(p0, temp0, p1, temp1, hold_min);
                if (rate <= max_drop) {
                    c.log("info", c.rvs() + " 气密通过",
                          "压降 " + format_rate(rate, c.unit) + " ≤ 阈值 " + step_specs::p_rate(max_drop, c.unit) + comp_text(temp0, temp1));
                } else {
                    std::string msg = format_rate(rate, c.unit) + " > 阈值 " + step_specs::p_rate(max_drop, c.unit);
                    if (stop_on_fail) {
                        c.log("err", c.rvs() + " 气密不通过", msg + comp_text(temp0, temp1) + " · 放空后停止这一路");
                        pending_fault = c.rvs() + " 气密不通过 · " + msg;
                        start_vent(kLeakVentTo, kLeakVentMaxSec, 0, 0);
                        ph = 5;
                        break;
                    }
                    c.log("warn", c.rvs() + " 气密不通过", msg + comp_text(temp0, temp1) + " · 记录后继续");
                }
                if (!vent_after) return Sub::Done;
                start_vent(kLeakVentTo, kLeakVentMaxSec, 0, 0);
                ph = 5;
                break;
            }
            case 5: {
                Sub r = vent.tick(c);
                if (r != Sub::Done) return r;
                if (pending_fault) { c.fail(FaultKind::Step, *pending_fault); return Sub::Fault; }
                return Sub::Done;
            }
        }
    }
}

// ============ exchange 置换 ============
// 0 开始 → 1 取气路 → 2 吹总管（等 LineFlushDone）→ 3 逐次置换 → 结束
Sub StepRunner::Impl::exchange(Ctx& c) {
    std::string gas_code = s.str("gas", "inert");
    int gi = step_specs::gas_index(gas_code);
    std::string gas_name = step_specs::gas_label("exchange", gas_code);
    double fill_p = s.num("fill", 72.5), release = s.num("release", 2.9), max_sec = s.num("maxSec", 60);
    int cycles = static_cast<int>(round_even(s.num("cycles", 3))), flush = static_cast<int>(round_even(s.num("lineFlush", 2)));
    bool vessel = cycles > 0;
    auto next_cycle = [&]() {
        cyc = ExchangeCycle{};
        cyc.gi = gi; cyc.fill = fill_p; cyc.release = release; cyc.max_sec = max_sec;
        cyc.status = head + " · " + gas_name + " " + std::to_string(i) + "/" + std::to_string(cycles);
        cyc.what = "置换";
    };
    for (;;) {
        switch (ph) {
            case 0: {
                const auto& block = vessel ? c.in.gas_block_vessel : c.in.gas_block_line;
                if (block) { c.fail(FaultKind::Step, "置换：" + *block); return Sub::Fault; }
                if (vessel) c.set_state(ReactorState::Pressing);
                c.status(head);
                c.log("info", c.rvs() + " 程序 · 置换",
                      vessel ? gas_name + " · " + c.P(fill_p) + " → " + c.P(release) + " × " + std::to_string(cycles) + " 次 · 先吹总管 " +
                                   std::to_string(flush) + " 次"
                             : gas_name + " · 只吹总管 " + std::to_string(flush) + " 次（SV 关，釜不置换）");
                gas = GasAcquire{c.rvs() + " 程序 · 置换", vessel};
                ph = 1;
                break;
            }
            case 1: {
                Sub r = gas.tick(c);
                if (r != Sub::Done) return r;
                if (flush > 0) {
                    Action a; a.kind = ActionKind::LineFlush; a.gas = gi; a.count = flush; a.text = head + " · 吹总管 " + gas_name;
                    c.act(std::move(a));
                    c.await(Await::LineFlush);
                    ph = 2;
                    return Sub::Running;
                }
                ph = 2;
                break;
            }
            case 2:
                i = 1;
                if (i <= cycles) next_cycle();
                ph = 3;
                break;
            case 3: {
                if (i > cycles) {
                    c.valves(ValveCmd::AllClosed);
                    c.release_gas();
                    c.log("info", c.rvs() + " 置换完成",
                          gas_name + " · " + std::to_string(cycles) + " 次" + (flush > 0 ? " · 吹总管 " + std::to_string(flush) + " 次" : std::string()));
                    return Sub::Done;
                }
                Sub r = cyc.tick(c);
                if (r != Sub::Done) return r;
                ++i;
                if (i <= cycles) next_cycle();
                break;
            }
        }
    }
}

// ============ stir 搅拌（不阻塞）============
Sub StepRunner::Impl::stir(Ctx& c) {
    int rv = c.rv;
    int rpm = static_cast<int>(round_even(s.num("rpm", 400)));
    if (rpm > 0) {
        int before = c.in.stir_rpm;
        std::vector<int> others;
        for (const auto& h : c.in.stir_holders)
            if (h.rv != rv) others.push_back(h.rv);
        if (!others.empty() && c.in.stir_on && before != rpm) {
            std::string list = chan_names(others);
            c.log("warn", c.rvs() + " 改了共用搅拌",
                  c.rvs() + " 把共用搅拌从 " + std::to_string(before) + " 改为 " + std::to_string(rpm) + " rpm（" + list + " 的程序也在用）");
        }
        Action a; a.kind = ActionKind::StirRequest; a.count = rpm;
        c.act(std::move(a));
        if (!c.in.stir_on) { Action on; on.kind = ActionKind::StirSetOn; on.flag = true; c.act(std::move(on)); }
        c.log("info", c.rvs() + " 程序 · 搅拌", std::to_string(rpm) + " rpm（全机共用一台电机）· 保持到下一个搅拌或程序结束");
        c.status(head + " · " + std::to_string(rpm) + " rpm");
    } else {
        // 「搅拌 0」：释放这一路的持有（最后一个释放者才停）；这一路没持有、也没有别的程序持有时直接停
        bool holds = false;
        for (const auto& h : c.in.stir_holders)
            if (h.rv == rv) holds = true;
        if (holds) c.simple(ActionKind::StirRelease);
        else if (c.in.stir_holders.empty() && c.in.stir_on) { Action off; off.kind = ActionKind::StirSetOn; off.flag = false; c.act(std::move(off)); }
        // 释放后的当前持有者 = 其余持有者里最后登记的
        std::optional<StirHolder> keep;
        for (const auto& h : c.in.stir_holders)
            if (h.rv != rv) keep = h;
        std::string text = keep ? "搅拌停止请求 · " + chan_name(keep->rv) + " 程序仍在搅拌，保持 " + std::to_string(keep->rpm) + " rpm"
                                : std::string("搅拌停止");
        c.log("info", c.rvs() + " 程序 · 搅拌", text);
        c.status(head + " · 停止");
    }
    return Sub::Done;
}

// ============ heat 升温 ============
// 定值：0 开始 → 1 等曲线（rate > 0）→ 2 等到位 → 3 平衡；曲线：0 开始 → 1 等曲线 → 2 等到位
Sub StepRunner::Impl::heat(Ctx& c) {
    double bandw = s.num("band", 2), max_wait = s.num("maxWait", 120);
    bool stop = s.str("onTimeout", "stop") != "continue";
    bool curve = s.is_curve();
    double temp = s.num("temp", 80), rate = s.num("rate", 0), wait = s.num("wait", 15);
    double target = curve ? (s.segments().empty() ? 0 : s.segments().back().temp) : temp;
    auto start_band = [&](int64_t since) {
        band = WaitBand{};
        band.target = target; band.band = bandw; band.max_wait_min = max_wait; band.since = since;
    };
    for (;;) {
        switch (ph) {
            case 0: {
                if (c.in.heat_block) { c.fail(FaultKind::Step, "升温：" + *c.in.heat_block); return Sub::Fault; }
                if (!curve) {
                    t0 = c.now();   // maxWait 从步骤开始算
                    c.log("info", c.rvs() + " 程序 · 升温（定值）",
                          "目标 " + N(temp) + "℃ ± " + N(bandw) + "℃" + (rate > 0 ? " · " + N(rate) + "℃/min" : std::string(" · 防过冲逼近")) +
                              " · 到位后平衡 " + N(wait) + " min");
                    c.set_state(ReactorState::Heating);
                    if (rate > 0) {
                        // 设定值从当前 PV 按速率推进到目标（等价于单段曲线）；没有有效段时按定值启动
                        Action a; a.kind = ActionKind::RunCurve; a.segments = {Segment{temp, rate, 0.0}}; a.text = head;
                        c.act(std::move(a));
                        c.await(Await::Curve);
                        ph = 1;
                        return Sub::Running;
                    }
                    Action a; a.kind = ActionKind::StartFixedHeat; a.value = temp;
                    c.act(std::move(a));
                    start_band(t0);
                    ph = 2;
                    break;
                }
                if (s.segments().empty()) { c.fail(FaultKind::Step, "升温曲线为空"); return Sub::Fault; }
                c.log("info", c.rvs() + " 程序 · 升温（曲线）",
                      std::to_string(s.segments().size()) + " 段 · 终温 " + N(target) + "℃ ± " + N(bandw) + "℃");
                c.set_state(ReactorState::Heating);
                {
                    Action a; a.kind = ActionKind::RunCurve; a.segments = s.segments(); a.text = head;
                    c.act(std::move(a));
                }
                c.await(Await::Curve);
                ph = 1;
                return Sub::Running;
            }
            case 1:
                if (curve_rejected) { Action a; a.kind = ActionKind::StartFixedHeat; a.value = target; c.act(std::move(a)); }
                // 定值：maxWait 从步骤开始算；曲线：从曲线结束开始算，到位后不再额外平衡
                start_band(curve ? c.now() : t0);
                ph = 2;
                break;
            case 2: {
                Band b = band.tick(c);
                if (b == Band::Running) return Sub::Running;
                if (b == Band::Fault) return Sub::Fault;
                if (b == Band::Timeout)
                    return timed_out(c, stop, c.rvs() + " 升温 " + N(max_wait) + " min 未到 " + N(target) + " ± " + N(bandw) + " °C（当前 " + c.pv() + "）");
                if (curve) return Sub::Done;
                t0 = c.now();
                ph = 3;
                break;
            }
            case 3:
                if (c.el_min(t0) < wait) {
                    c.status(head + " · 已到 " + N(temp) + "℃ · 平衡剩 " + c.left(t0, wait));
                    c.wake_after(1);
                    return Sub::Running;
                }
                return Sub::Done;
        }
    }
}

// ============ press 充压 ============
// 0 开始（已高于目标直接 3）→ 1 取气路 → 2 充压 → 3 开始计量 / 稳压
Sub StepRunner::Impl::press(Ctx& c) {
    std::string gas_code = s.str("gas", "A");
    std::string gas_name = step_specs::gas_label("press", gas_code);
    int gi = step_specs::gas_index(gas_code);
    double target = s.num("pres", 290.1), tol = s.num("tol", 2), refill = s.num("refill", 3), max_sec = s.num("maxSec", 180);
    bool regulate = s.str("regulate", "on") != "off";
    for (;;) {
        switch (ph) {
            case 0: {
                if (c.in.gas_block_vessel) { c.fail(FaultKind::Step, "充压：" + *c.in.gas_block_vessel); return Sub::Fault; }
                c.set_psp(target);
                c.set_state(ReactorState::Pressing);
                c.log("info", c.rvs() + " 程序 · 充压",
                      gas_name + " 到 " + c.P(target) + " · 偏差 " + N(tol) + "% · 稳压" +
                          (regulate ? "开（补气阈值 " + N(refill) + "%）" : std::string("关")));
                c.status(head);
                double now_p;
                if (!c.read_p(now_p)) return Sub::Fault;
                if (now_p >= target) {
                    // 只能升、不能降
                    c.log("info", c.rvs() + " 已高于目标 " + c.P(target) + "，不放气", "当前 " + c.P(now_p));
                    ph = 3;
                    break;
                }
                gas = GasAcquire{c.rvs() + " 程序 · 充压", true};
                ph = 1;
                break;
            }
            case 1: {
                Sub r = gas.tick(c);
                if (r != Sub::Done) return r;
                fill = FillTo{};
                fill.gi = gi; fill.target = target; fill.tol_pct = tol; fill.max_sec = max_sec;
                ph = 2;
                break;
            }
            case 2: {
                Sub r = fill.tick(c);
                if (r != Sub::Done) return r;
                c.release_gas();
                c.log("info", c.rvs() + " 程序 · 充压完成", gas_name + " 到 " + c.P(fill.result) + "（目标 " + c.P(target) + "）");
                ph = 3;
                break;
            }
            case 3: {
                // 吸气量：充压完成后开始计量（遇到下一次换气停止）
                c.simple(ActionKind::StartUptake, "第 " + fmt_00(s.ordinal()) + " 步充压完成");
                if (regulate) {
                    Action a; a.kind = ActionKind::StartRegulation; a.gas = gi; a.value = target; a.value2 = refill;
                    c.act(std::move(a));
                }
                return Sub::Done;
            }
        }
    }
}

// ============ hold 保持 ============
Sub StepRunner::Impl::hold(Ctx& c) {
    std::string until = s.str("until", "time");
    if (ph == 0) {
        c.set_state(ReactorState::Wait);
        t0 = c.now();
        ph = 1;
    }
    if (until == "time") {
        double time = s.num("time", 10);
        if (c.el_min(t0) < time) {
            c.status(head + " · 保持 剩 " + c.left(t0, time));
            c.wake_after(1);
            return Sub::Running;
        }
        c.log("info", c.rvs() + " 程序 · 保持完成", "时间 " + N(time) + " min");
        return Sub::Done;
    }
    bool is_temp = until == "tempAbove" || until == "tempBelow";
    bool above = until == "tempAbove" || until == "pressAbove";
    double target = is_temp ? s.num("temp", 40) : s.num("pres", 14.5);
    double max_wait = s.num("maxWait", 60);
    bool stop = s.str("onTimeout", "continue") == "stop";
    auto fmt = [&](double v) { return is_temp ? fmt_dec(v, 1, 1) + "℃" : c.P(v); };
    std::string cond = is_temp ? std::string("温度 ") + (above ? "≥" : "≤") + " " + N(target) + "℃"
                               : std::string("压力 ") + (above ? "≥" : "≤") + " " + c.P(target);
    std::optional<double> v = is_temp ? c.in.temp_c : c.in.pressure_psi;
    if (v) {
        offline.reset();
        if (above ? *v >= target : *v <= target) {
            c.log("info", c.rvs() + " 程序 · 保持完成", cond + " · 当前 " + fmt(*v) + " · 用时 " + fmt_dec(c.el_min(t0), 1) + " min");
            return Sub::Done;
        }
    } else {
        if (!offline) offline = c.now();
        // 传感器离线不再悄悄改成按时间等：离线超过 60 s 按超时处理
        if (c.el_sec(*offline) >= kOfflineSec)
            return timed_out(c, stop, c.rvs() + " 保持：" + (is_temp ? "温控" : "压力采集") + "离线超过 60 s，无法判断「" + cond + "」");
    }
    if (c.el_min(t0) >= max_wait)
        return timed_out(c, stop, c.rvs() + " 保持 " + N(max_wait) + " min 未达到「" + cond + "」（当前 " + (v ? fmt(*v) : std::string("—")) + "）");
    c.status(head + " · 等 " + cond + " · 当前 " + (v ? fmt(*v) : std::string("—")) + " · 剩 " + c.left(t0, max_wait));
    c.wake_after(1);
    return Sub::Running;
}

// ============ react 反应 ============
Sub StepRunner::Impl::react(Ctx& c) {
    double time = s.num("time", 480), min_time = s.num("minTime", 30), mmol = s.num("mmol", 5);
    double stall_rate = s.num("stallRate", 0.005), stall_min = s.num("stallMin", 30);
    bool mmol_on = s.str("mmolOn", "off") == "on", stall_on = s.str("stallOn", "on") == "on";
    if (ph == 0) {
        metered = c.in.uptake_counting;
        if ((mmol_on || stall_on) && !metered)
            c.log("warn", c.rvs() + " 程序 · 反应", "吸气量未计量（反应前没有充压），只按最长时间 " + N(time) + " min 结束");
        c.set_state(ReactorState::React);
        t0 = c.now();
        ph = 1;
    }
    bool use_mmol = mmol_on && metered, use_stall = stall_on && metered;
    std::optional<std::string> reason;
    double el = c.el_min(t0);
    if (el >= time) reason = "最长时间到";
    else if (el >= min_time) {
        if (use_mmol && c.in.uptake_valid && c.in.uptake_mmol >= mmol) reason = "吸气量达到 " + fmt_dec(mmol, 2, 2) + " mmol";
        else if (use_stall) {
            // 连续 stallMin 分钟 GasRate < stallRate 才算吸气停止；GasRate 为 null 的时段不算，计时重新开始
            if (c.in.gas_rate && *c.in.gas_rate < stall_rate) {
                if (!low_since) low_since = c.now();
                if (c.el_min(*low_since) >= stall_min) reason = "吸气停止（" + N(stall_min) + " min < " + N(stall_rate) + " mmol/min）";
            } else {
                low_since.reset();
            }
        }
    }
    if (!reason) {
        std::string gas = c.in.uptake_valid
            ? " · 吸气 " + fmt_dec(c.in.uptake_mmol, 2, 2) + (use_mmol ? " / " + N(mmol) : std::string()) + " mmol"
            : std::string();
        std::string rate = c.in.gas_rate ? " · " + fmt_dec(*c.in.gas_rate, 3, 3) + " mmol/min" : std::string();
        c.status(head + " · 反应中 · 剩 " + c.left(t0, time) + gas + rate);
        c.wake_after(2);
        return Sub::Running;
    }
    c.simple(ActionKind::SetEndReason, *reason);
    c.log("info", c.rvs() + " 反应结束 · " + *reason,
          "反应 " + fmt_dec(c.el_min(t0), 1) + " min" +
              (c.in.uptake_valid ? " · 吸气 " + fmt_dec(c.in.uptake_mmol, 2, 2) + " mmol" : std::string(" · 吸气量未计量")));
    return Sub::Done;
}

// ============ end 结束 ============
Sub StepRunner::Impl::end(Ctx& c) {
    c.status(head + " · 收尾");
    c.simple(ActionKind::StopHeat, c.rvs() + " 程序 · 结束步骤停加热");
    bool holds = false;
    for (const auto& h : c.in.stir_holders)
        if (h.rv == c.rv) holds = true;
    if (holds) c.simple(ActionKind::StirRelease);   // 别的路还在用就继续转；本程序没搅拌过不碰（操作员手动开的搅拌）
    c.simple(ActionKind::CloseOwnValve);
    c.set_state(ReactorState::Done);
    c.log("info", c.rvs() + " 程序 · 结束",
          std::string("关加热、阀全关、释放搅拌、停止计量") + (c.in.end_reason ? " · 结束原因：" + *c.in.end_reason : std::string()));
    return Sub::Done;
}

}  // namespace reactor::core::program
