// C#: tests/ParallelReactor.Tests/StepEngineTests.cs
// C# 的 FakeIo（虚拟时钟的假设备）在这里改成驱动 StepRunner 的小循环：执行输出的动作、按阀态积分压力、
// 推进时间到 wake_at、按 waiting 投递外部事件（取气路立即成功；吹总管 cycles × 9 s；曲线按 CurveMinutes 推进）。
#include <algorithm>
#include <cmath>
#include <functional>

#include "core/program/step_engine.hpp"
#include "core/program/step_fields.hpp"
#include "core/valve_frame.hpp"
#include "testing.hpp"
#include <cstdint>
#include <optional>
#include <string>
#include <utility>
#include <vector>

using namespace reactor::core;
using namespace reactor::core::program;

namespace {

// C#: Models/StirShare.cs 的最小副本（只给测试用：真正的 StirShare 由另一个模块在 src/core 里实现）
struct FakeStirShare {
    std::vector<StirHolder> holders;
    void request(int rv, int rpm) {
        holders.erase(std::remove_if(holders.begin(), holders.end(), [&](const StirHolder& h) { return h.rv == rv; }), holders.end());
        holders.push_back({rv, rpm});
    }
    bool release(int rv) {
        holders.erase(std::remove_if(holders.begin(), holders.end(), [&](const StirHolder& h) { return h.rv == rv; }), holders.end());
        return !holders.empty();
    }
    bool holds(int rv) const {
        return std::any_of(holders.begin(), holders.end(), [&](const StirHolder& h) { return h.rv == rv; });
    }
};

struct Frame {
    bool sv = false;     // 这一路的 SV
    int gas = -1;        // 开着的气源（-1 = 无）
    bool vent = false;
};

struct Log { std::string level, title, detail; };

struct RunResult {
    StepStatus status = StepStatus::Running;
    FaultKind kind = FaultKind::Step;
    std::string fault;
    bool faulted() const { return status == StepStatus::Fault; }
};

struct FakeIo {
    int rv = 1;
    ProgramLimitSettings limits{200, 250, 450, 500, 510};
    int64_t now_ms = 0;

    double P = 0;                 // 釜压 psi
    bool p_offline = false;
    double source_psi = 600, fill_rate = 20, vent_rate = 30;
    bool vent_blocked = false;
    double leak_per_min = 0;      // SV 关着时的压降 psi/min
    std::optional<double> fixed_temp = 25.0;
    std::function<std::optional<double>(double /*min*/)> temp_at;
    std::function<void(FakeIo&)> on_tick;
    Frame frame;

    std::vector<Log> logs;
    std::vector<ReactorState> states;
    std::vector<std::string> calls;
    std::vector<Frame> frames;
    std::string last_status;
    double pressure_sp = 0;
    std::optional<std::string> gas_block, heat_block;
    int gas_acquired = 0, gas_released = 0;
    bool gas_held() const { return gas_acquired > gas_released; }
    bool regulation_active = false;
    struct Reg { int gas; double target; double refill; };
    std::optional<Reg> regulation;
    bool uptake_counting = false, uptake_valid = false;
    double uptake_mmol = 0;
    std::function<std::optional<double>(double /*min*/)> rate_at;
    std::optional<double> fixed_heat_target;
    std::optional<std::vector<Segment>> curve;
    FakeStirShare stir;
    int stir_rpm = 300;
    bool stir_on = false;
    std::optional<std::string> end_reason;

    double minutes() const { return static_cast<double>(now_ms) / 60000.0; }
    double seconds() const { return static_cast<double>(now_ms) / 1000.0; }
    std::optional<double> temp() const { return temp_at ? temp_at(minutes()) : fixed_temp; }

    // C#: FakeIo.Advance —— 阀开着按 0.05 s 积分，否则按 1 s
    void advance_ms(int64_t ms) {
        int64_t left = ms;
        while (left > 0) {
            bool gas_open = frame.gas >= 0;
            bool moving = frame.sv && (gas_open || frame.vent);
            int64_t dt = std::min<int64_t>(moving ? 50 : 1000, left);
            double dts = static_cast<double>(dt) / 1000.0;
            if (frame.sv && gas_open) P = std::min(source_psi, P + fill_rate * dts);
            else if (frame.sv && frame.vent) { if (!vent_blocked) P = std::max(0.0, P - vent_rate * dts); }
            else P = std::max(0.0, P - leak_per_min * dts / 60.0);
            now_ms += dt;
            left -= dt;
            if (on_tick) on_tick(*this);
        }
    }

    bool has_log(const std::string& part) const {
        return std::any_of(logs.begin(), logs.end(), [&](const Log& l) {
            return l.title.find(part) != std::string::npos || l.detail.find(part) != std::string::npos;
        });
    }
    bool has_call(const std::string& c) const { return std::find(calls.begin(), calls.end(), c) != calls.end(); }

    StepInput input(StepEvent ev) const {
        StepInput in;
        in.now_ms = now_ms;
        in.limits = limits;
        in.temp_c = temp();
        if (!p_offline) in.pressure_psi = P;
        in.regulation_active = regulation_active;
        in.uptake_counting = uptake_counting;
        in.uptake_valid = uptake_valid;
        in.uptake_mmol = uptake_mmol;
        if (rate_at) in.gas_rate = rate_at(minutes());
        in.gas_block_vessel = gas_block;
        in.gas_block_line = gas_block;
        in.heat_block = heat_block;
        in.stir_rpm = stir_rpm;
        in.stir_on = stir_on;
        in.stir_holders = stir.holders;
        in.end_reason = end_reason;
        in.event = ev;
        return in;
    }

    void apply(const Action& a) {
        switch (a.kind) {
            case ActionKind::Status: last_status = a.text; break;
            case ActionKind::Log: logs.push_back({a.level, a.text, a.detail}); break;
            case ActionKind::SetState: states.push_back(a.state); break;
            case ActionKind::SetPressureSp: pressure_sp = a.value; break;
            case ActionKind::StopRegulation: regulation_active = false; calls.push_back("reg:stop"); break;
            case ActionKind::StartRegulation: regulation_active = true; regulation = Reg{a.gas, a.value, a.value2}; break;
            case ActionKind::StartUptake: uptake_counting = true; uptake_valid = true; calls.push_back("uptake:start"); break;
            case ActionKind::StopUptake: if (uptake_counting) calls.push_back("uptake:stop"); uptake_counting = false; break;
            case ActionKind::RequestGas:
                calls.push_back("gas:" + a.text + ":" + (a.flag ? "True" : "False"));
                gas_acquired++;
                break;
            case ActionKind::ReleaseGas: frame = Frame{}; gas_released++; break;
            case ActionKind::Valves: {
                CHECK(gas_held());   // 只在持有气路时动阀
                Frame f;
                if (a.valve == ValveCmd::GasToVessel) { f.sv = true; f.gas = a.gas; }
                else if (a.valve == ValveCmd::VentVessel) { f.sv = true; f.vent = true; }
                frame = f;
                frames.push_back(f);
                break;
            }
            case ActionKind::LineFlush: calls.push_back("flush:" + std::to_string(a.gas) + ":" + std::to_string(a.count)); break;
            case ActionKind::StartFixedHeat: fixed_heat_target = a.value; calls.push_back("heat:fixed:" + fmt_n(a.value)); break;
            case ActionKind::RunCurve: break;   // 在等待处理里推进
            case ActionKind::StopHeat: calls.push_back("heat:stop"); break;
            case ActionKind::StirRequest: stir.request(rv, a.count); stir_rpm = a.count; break;
            case ActionKind::StirSetOn: stir_on = a.flag; break;
            case ActionKind::StirRelease: {
                calls.push_back("stir:release");
                bool others = stir.release(rv);
                if (!others) stir_on = false;
                else stir_rpm = stir.holders.back().rpm;
                break;
            }
            case ActionKind::CloseOwnValve: calls.push_back("sv:close"); break;
            case ActionKind::SetEndReason: end_reason = a.text; break;
        }
    }

    RunResult run(const StepSnapshot& s) {
        StepRunner r(s, "03/09 " + s.name(), RunnerConfig{rv, PressureUnit::MPa});
        StepEvent ev = StepEvent::None;
        std::vector<Segment> pending_curve;
        for (int guard = 0; guard < 2000000; ++guard) {
            StepOutput out = r.tick(input(ev));
            ev = StepEvent::None;
            for (const auto& a : out.actions) {
                apply(a);
                if (a.kind == ActionKind::RunCurve) pending_curve = a.segments;
            }
            if (out.status != StepStatus::Running) return RunResult{out.status, out.fault_kind, out.fault};
            switch (out.waiting) {
                case Await::Gas: ev = StepEvent::GasGranted; continue;
                case Await::LineFlush: {
                    int n = 0;
                    for (auto it = out.actions.rbegin(); it != out.actions.rend(); ++it)
                        if (it->kind == ActionKind::LineFlush) { n = it->count; break; }
                    advance_ms(static_cast<int64_t>(n) * 9000);
                    ev = StepEvent::LineFlushDone;
                    continue;
                }
                case Await::Curve: {
                    double start = temp().value_or(25);
                    double min = step_specs::curve_minutes(pending_curve, start);
                    if (min <= 0) { ev = StepEvent::CurveRejected; continue; }
                    curve = pending_curve;
                    calls.push_back("heat:curve");
                    advance_ms(static_cast<int64_t>(std::llround(min * 60000)));
                    ev = StepEvent::CurveFinished;
                    continue;
                }
                case Await::None: break;
            }
            CHECK(out.wake_at_ms >= now_ms);
            advance_ms(out.wake_at_ms - now_ms);
        }
        CHECK(false);   // 没跑完
        return {};
    }
};

StepSnapshot S(StepType t, std::initializer_list<Params::Item> kv = {}) {
    Params p = step_specs::defaults(t);
    for (const auto& i : kv) p.set(i.first, i.second);
    return StepSnapshot::create(t, 3, zh(t), "", p);
}

StepSnapshot Curve(std::vector<Segment> segs) {
    Params p = step_specs::defaults(StepType::Heat);
    p.set("mode", std::string("curve"));
    return StepSnapshot::create(StepType::Heat, 3, "升温", "", p, std::move(segs));
}

int count_frames(const FakeIo& io, std::function<bool(const Frame&)> f) {
    return static_cast<int>(std::count_if(io.frames.begin(), io.frames.end(), f));
}

bool all_closed(const Frame& f) { return !f.sv && f.gas < 0 && !f.vent; }

std::string sv(const char* s) { return std::string(s); }

}  // namespace

// ============ exchange ============

TEST(core_program_step_engine, Exchange_FlushesLineThenCyclesFillAndRelease) {
    FakeIo io;
    auto r = io.run(S(StepType::Exchange, {{"gas", sv("A")}, {"cycles", 3.0}, {"lineFlush", 2.0}}));
    CHECK(!r.faulted());
    CHECK(io.has_call("flush:1:2"));   // 气体 A = 气源 1，先吹总管 2 次
    CHECK_EQ(count_frames(io, [](const Frame& f) { return f.sv && f.gas == 1; }), 3);
    CHECK_EQ(count_frames(io, [](const Frame& f) { return f.sv && f.vent; }), 3);
    CHECK(io.P <= 2.9);
    CHECK(!io.gas_held());
    CHECK(io.has_log("置换完成"));
    CHECK(all_closed(io.frame));
}

TEST(core_program_step_engine, Exchange_ZeroCycles_OnlyFlushesLine_NoVesselValve) {
    FakeIo io;
    io.run(S(StepType::Exchange, {{"cycles", 0.0}, {"lineFlush", 3.0}}));
    CHECK(io.has_call("flush:0:3"));
    CHECK(io.has_call("gas:1 号 程序 · 置换:False"));   // 不开 SV：不监视、不需要压力
    CHECK_EQ(count_frames(io, [](const Frame& f) { return f.sv; }), 0);
}

TEST(core_program_step_engine, Exchange_FillTimeout_Faults) {
    FakeIo io;
    io.source_psi = 30;   // 减压阀出口只有 30 psi，充不到 72.5
    auto r = io.run(S(StepType::Exchange, {{"maxSec", 10.0}}));
    CHECK(r.faulted());
    CHECK(r.kind == FaultKind::Step);
    CHECK(r.fault.find("1 号 置换：充气 10 s 未到") != std::string::npos);
    CHECK(r.fault.find("气源压力不够或阀门没开") != std::string::npos);
    CHECK(!io.gas_held());
    CHECK(!io.has_log("置换完成"));
}

TEST(core_program_step_engine, Exchange_ReleaseTimeout_Faults_CheckValveWording) {
    FakeIo io;
    io.vent_blocked = true;
    auto r = io.run(S(StepType::Exchange, {{"maxSec", 15.0}}));
    CHECK(r.faulted());
    CHECK(r.fault.find("1 号 置换：开 SV1 + 排空阀 15 s 后釜压仍") != std::string::npos);
    CHECK(r.fault.find("可能有进气单向阀") != std::string::npos);
    CHECK(r.fault.find("不要开釜") != std::string::npos);
    CHECK(!io.gas_held());
    CHECK(!io.has_log("置换完成"));
}

// ============ vent ============

TEST(core_program_step_engine, Vent_DropsPressure_ThenLogsVented) {
    FakeIo io;
    io.P = 290;
    auto r = io.run(S(StepType::Vent, {{"flush", 0.0}}));
    CHECK(!r.faulted());
    CHECK(io.P <= 2.9);
    CHECK(io.has_log("已放空到"));
    CHECK(!io.gas_held());
    CHECK(count_frames(io, [](const Frame& f) { return f.sv && f.vent; }) > 0);
}

TEST(core_program_step_engine, Vent_PressureNotDropping_Faults_AndNeverLogsVented) {
    FakeIo io;
    io.P = 290;
    io.vent_blocked = true;
    auto r = io.run(S(StepType::Vent, {{"maxSec", 20.0}}));
    CHECK(r.faulted());
    CHECK(r.fault.find("1 号 放空 20 s 后釜压仍") != std::string::npos);
    CHECK(r.fault.find("釜里的气放不掉 · 不要开釜") != std::string::npos);
    CHECK(!io.has_log("已放空到"));   // 只有真的放下来了才写
    CHECK(!io.gas_held());
}

TEST(core_program_step_engine, Vent_WithInertFlush_AndHotWarning) {
    FakeIo io;
    io.P = 290;
    io.fixed_temp = 85.0;
    io.run(S(StepType::Vent, {{"flush", 2.0}, {"flushPres", 43.5}}));
    CHECK(io.has_log("热态放空会带出溶剂蒸气"));
    CHECK_EQ(count_frames(io, [](const Frame& f) { return f.sv && f.gas == 0; }), 2);   // 惰性气体 × 2
    CHECK(io.P <= 2.9);
    CHECK(std::any_of(io.logs.begin(), io.logs.end(), [](const Log& l) {
        return l.title.rfind("1 号 已放空到", 0) == 0 && l.detail.find("惰性置换 2 次") != std::string::npos;
    }));
}

TEST(core_program_step_engine, Vent_StopsUptakeAndRegulation_LogsRegulationEnd) {
    FakeIo io;
    io.P = 100;
    io.regulation_active = true;
    io.uptake_counting = true;
    io.run(S(StepType::Vent, {{"flush", 0.0}}));
    CHECK(!io.regulation_active);
    CHECK(!io.uptake_counting);
    CHECK(std::any_of(io.logs.begin(), io.logs.end(), [](const Log& l) { return l.title == "1 号 稳压结束（进入 放空）"; }));
}

// ============ leak ============

TEST(core_program_step_engine, Leak_Pass_ThenVents) {
    FakeIo io;
    auto r = io.run(S(StepType::Leak, {{"pres", 100.0}, {"settle", 1.0}, {"hold", 10.0}, {"maxDrop", 2.0}}));
    CHECK(!r.faulted());
    CHECK(std::any_of(io.logs.begin(), io.logs.end(), [](const Log& l) { return l.title == "1 号 气密通过"; }));
    CHECK(io.has_log("已放空到"));
    CHECK(io.P <= 2.9);
    CHECK(count_frames(io, [](const Frame& f) { return f.sv && f.gas == 0; }) > 0);   // 充气用惰性气体
    CHECK_EQ(count_frames(io, [](const Frame& f) { return f.gas == 1 || f.gas == 2; }), 0);
    CHECK_EQ(io.gas_acquired, 2);   // 充气一段 + 放空一段：保压期间不占用气路
}

TEST(core_program_step_engine, Leak_Fail_Stop_VentsThenFaults) {
    FakeIo io;
    io.leak_per_min = 0.2;   // 12 psi/h
    auto r = io.run(S(StepType::Leak, {{"pres", 100.0}, {"settle", 0.0}, {"hold", 10.0}, {"maxDrop", 2.0}, {"onFail", sv("stop")}}));
    CHECK(r.faulted());
    CHECK(r.fault.find("1 号 气密不通过") != std::string::npos);
    CHECK(r.fault.find("> 阈值 " + reactor::core::program::step_specs::p_rate(2, reactor::core::PressureUnit::MPa)) != std::string::npos);
    CHECK(io.has_log("已放空到"));   // 不通过且 onFail = stop：先放空再失败
    CHECK(io.P <= 2.9);
}

TEST(core_program_step_engine, Leak_Fail_Continue_WarnsAndKeepsPressure) {
    FakeIo io;
    io.leak_per_min = 0.2;
    io.run(S(StepType::Leak, {{"pres", 100.0}, {"settle", 0.0}, {"hold", 10.0}, {"onFail", sv("continue")}, {"after", sv("keep")}}));
    CHECK(std::any_of(io.logs.begin(), io.logs.end(), [](const Log& l) { return l.level == "warn" && l.title == "1 号 气密不通过"; }));
    CHECK(!io.has_log("已放空到"));
    CHECK(io.P > 90);
}

TEST(core_program_step_engine, Leak_TemperatureCompensation) {
    double p0 = 435.1;
    double p1 = leak_math::compensate(p0, 25, 20);
    CHECK(p0 - p1 > 5);   // 不补偿看起来降了 7 psi
    CHECK_NEAR(step_engine::leak_drop_rate(p0, 25.0, p1, 20.0, 30), 0, 1e-6);
    CHECK_NEAR(step_engine::leak_drop_rate(p0, std::nullopt, p1, 20.0, 30), (p0 - p1) * 2, 1e-6);   // 读不到温度不补偿
}

TEST(core_program_step_engine, Leak_Engine_CompensatesCoolingDuringHold) {
    // 保压 30 min 内釜温 25 → 20 ℃，压力跟着温度走（无泄漏）：补偿后通过
    FakeIo io;
    std::optional<double> hold_start_p;
    double hold_start_min = 0;
    io.temp_at = [&](double t) -> std::optional<double> {
        return !hold_start_p ? 25.0 : 25 - 5 * std::min(1.0, (t - hold_start_min) / 30);
    };
    io.on_tick = [&](FakeIo& f) {
        if (hold_start_p) f.P = leak_math::compensate(*hold_start_p, 25, *f.temp());
        else if (!f.states.empty() && f.states.back() == ReactorState::Wait) { hold_start_p = f.P; hold_start_min = f.minutes(); }
    };
    io.run(S(StepType::Leak, {{"pres", 100.0}, {"settle", 0.0}, {"hold", 30.0}, {"after", sv("keep")}}));
    CHECK(hold_start_p.has_value());
    CHECK(io.P < hold_start_p.value_or(0) - 1);   // 表压确实降了
    CHECK(std::any_of(io.logs.begin(), io.logs.end(), [](const Log& l) {
        return l.title == "1 号 气密通过" && l.detail.find("温度补偿") != std::string::npos;
    }));
}

// ============ press ============

TEST(core_program_step_engine, Press_FillsToTarget_StartsUptakeAndRegulation) {
    FakeIo io;
    auto r = io.run(S(StepType::Press, {{"gas", sv("inert")}, {"pres", 100.0}, {"tol", 2.0}, {"refill", 4.0}}));
    CHECK(!r.faulted());
    CHECK(io.P >= 98 && io.P <= 110);
    CHECK(count_frames(io, [](const Frame& f) { return f.sv && f.gas == 0; }) > 0);   // 惰性气体充压
    CHECK(io.has_call("uptake:start"));
    CHECK(io.regulation.has_value());
    if (io.regulation) {
        CHECK_EQ(io.regulation->gas, 0);
        CHECK_EQ(io.regulation->target, 100.0);
        CHECK_EQ(io.regulation->refill, 4.0);
    }
    CHECK(!io.gas_held());
}

TEST(core_program_step_engine, Press_AlreadyAbove_DoesNotVent) {
    FakeIo io;
    io.P = 150;
    io.run(S(StepType::Press, {{"pres", 100.0}, {"regulate", sv("off")}}));
    CHECK_EQ(io.P, 150.0);
    CHECK_EQ(io.gas_acquired, 0);
    CHECK(std::any_of(io.logs.begin(), io.logs.end(), [](const Log& l) {
        return l.title.find("已高于目标") != std::string::npos && l.title.find("不放气") != std::string::npos;
    }));
    CHECK(!io.regulation.has_value());
}

TEST(core_program_step_engine, Press_FillTimeout_GasFault) {
    FakeIo io;
    io.fill_rate = 0.5;   // 每次脉冲只涨约 1 psi：不触发「无响应」，到 maxSec 超时
    auto r = io.run(S(StepType::Press, {{"pres", 290.0}, {"maxSec", 60.0}}));
    CHECK(r.faulted());
    CHECK(r.kind == FaultKind::Gas);
    CHECK(r.fault.find("1 号 充压 60 s 未到") != std::string::npos);
    CHECK(!io.gas_held());
    CHECK(!io.regulation.has_value());
}

TEST(core_program_step_engine, Press_StopsPreviousRegulation_AndLogsIt) {
    FakeIo io;
    io.regulation_active = true;
    io.uptake_counting = true;
    io.run(S(StepType::Press, {{"pres", 50.0}}));
    CHECK(std::any_of(io.logs.begin(), io.logs.end(), [](const Log& l) { return l.title == "1 号 稳压结束（进入 充压）"; }));
    auto first_reg = std::find_if(io.calls.begin(), io.calls.end(), [](const std::string& c) { return c.rfind("reg:", 0) == 0; });
    CHECK(first_reg != io.calls.end() && *first_reg == "reg:stop");
    CHECK(io.regulation_active);   // 新的稳压
}

// ============ cross-step rules ============

TEST(core_program_step_engine, Regulation_StoppedOnlyByGasSteps) {
    const std::pair<StepType, bool> rows[] = {{StepType::Leak, true},  {StepType::Exchange, true}, {StepType::Press, true},
                                              {StepType::Vent, true},  {StepType::End, true},      {StepType::Heat, false},
                                              {StepType::Hold, false}, {StepType::React, false},   {StepType::Stir, false}};
    for (const auto& r : rows) CHECK_EQ(step_engine::breaks_gas(r.first), r.second);
}

TEST(core_program_step_engine, Regulation_StoppedByExchange_IsLogged_HoldKeepsIt) {
    FakeIo io;
    io.regulation_active = true;
    io.run(S(StepType::Hold, {{"until", sv("time")}, {"time", 1.0}}));
    CHECK(io.regulation_active);
    io.run(S(StepType::Exchange, {{"cycles", 1.0}, {"lineFlush", 0.0}}));
    CHECK(!io.regulation_active);
    CHECK(std::any_of(io.logs.begin(), io.logs.end(), [](const Log& l) { return l.level == "info" && l.title == "1 号 稳压结束（进入 置换）"; }));
}

// ============ stir ============

TEST(core_program_step_engine, Stir_IsNonBlocking_AndHoldsShare) {
    FakeIo io;
    int64_t t0 = io.now_ms;
    io.run(S(StepType::Stir, {{"rpm", 400.0}}));
    CHECK_EQ(io.now_ms, t0);   // 立即进入下一步
    CHECK(io.stir_on);
    CHECK_EQ(io.stir_rpm, 400);
    CHECK(io.stir.holds(1));
    CHECK(std::none_of(io.logs.begin(), io.logs.end(), [](const Log& l) { return l.level == "warn"; }));
}

TEST(core_program_step_engine, Stir_ChangingSharedRpm_WarnsWithOtherProgram) {
    FakeIo io;
    io.stir_on = true;
    io.stir_rpm = 400;
    io.stir.request(2, 400);   // RV2 的程序正在用 400 rpm
    io.run(S(StepType::Stir, {{"rpm", 300.0}}));
    CHECK_EQ(io.stir_rpm, 300);   // 照样写入
    CHECK(std::any_of(io.logs.begin(), io.logs.end(), [](const Log& l) {
        return l.level == "warn" && l.detail == "1 号 把共用搅拌从 400 改为 300 rpm（2 号 的程序也在用）";
    }));
}

TEST(core_program_step_engine, Stir_SameRpm_NoWarning_ZeroReleases) {
    FakeIo io;
    io.stir_on = true;
    io.stir_rpm = 400;
    io.stir.request(2, 400);
    io.run(S(StepType::Stir, {{"rpm", 400.0}}));
    CHECK(std::none_of(io.logs.begin(), io.logs.end(), [](const Log& l) { return l.level == "warn"; }));
    io.run(S(StepType::Stir, {{"rpm", 0.0}}));
    CHECK(!io.stir.holds(1));
    CHECK(io.stir_on);   // RV2 还在用
    CHECK(io.has_log("2 号 程序仍在搅拌"));
}

// ============ heat ============

TEST(core_program_step_engine, Heat_Fixed_WaitsBandThenBalances) {
    FakeIo io;
    io.temp_at = [](double t) -> std::optional<double> { return t < 20 ? 25 + 2.5 * t : 80; };
    io.run(S(StepType::Heat, {{"temp", 80.0}, {"band", 2.0}, {"wait", 15.0}}));
    CHECK(io.fixed_heat_target == std::optional<double>(80.0));
    // 约 21 min 进入 ±2（78 ℃），再平衡 15 min
    CHECK(io.minutes() >= 35 && io.minutes() <= 38);
}

TEST(core_program_step_engine, Heat_Fixed_WithRate_RunsSingleSegmentCurve) {
    FakeIo io;
    io.temp_at = [&io](double) -> std::optional<double> { return io.curve ? 80.0 : 25.0; };
    io.run(S(StepType::Heat, {{"temp", 80.0}, {"rate", 5.0}, {"wait", 0.0}}));
    CHECK(io.curve.has_value());
    if (io.curve) CHECK(*io.curve == (std::vector<Segment>{{80, 5, 0}}));
    CHECK(!io.fixed_heat_target.has_value());
}

TEST(core_program_step_engine, Heat_Timeout_Stop_Faults) {
    FakeIo io;
    auto r = io.run(S(StepType::Heat, {{"temp", 80.0}, {"maxWait", 10.0}, {"onTimeout", sv("stop")}}));
    CHECK(r.faulted());
    CHECK_EQ(r.fault, std::string("1 号 升温 10 min 未到 80 ± 2 °C（当前 25.0℃）"));
}

TEST(core_program_step_engine, Heat_Timeout_Continue_WarnsAndSkipsBalance) {
    FakeIo io;
    auto r = io.run(S(StepType::Heat, {{"temp", 80.0}, {"maxWait", 10.0}, {"wait", 30.0}, {"onTimeout", sv("continue")}}));
    CHECK(!r.faulted());
    CHECK(std::any_of(io.logs.begin(), io.logs.end(), [](const Log& l) {
        return l.level == "warn" && l.detail.find("升温 10 min 未到 80 ± 2 °C") != std::string::npos;
    }));
    CHECK(io.minutes() >= 10 && io.minutes() <= 10.2);   // 不再平衡 30 min
}

TEST(core_program_step_engine, Heat_TempOffline60s_Faults) {
    FakeIo io;
    io.fixed_temp.reset();
    auto r = io.run(S(StepType::Heat, {{"temp", 80.0}}));
    CHECK(r.faulted());
    CHECK(r.fault.find("温控离线超过 60s") != std::string::npos);
    CHECK(io.seconds() >= 60 && io.seconds() <= 64);
}

TEST(core_program_step_engine, Heat_Curve_WaitsBandAfterCurve_MaxWaitFromCurveEnd) {
    FakeIo io;
    double curve_end = -1;
    io.temp_at = [&](double t) -> std::optional<double> {
        return !io.curve ? 25.0 : (curve_end >= 0 && t > curve_end + 5 ? 120.0 : 100.0);
    };
    io.on_tick = [&](FakeIo& f) { if (f.curve && curve_end < 0) curve_end = f.minutes(); };
    auto r = io.run(Curve({{80, 5, 10}, {120, 2, 20}}));
    CHECK(!r.faulted());
    CHECK(io.curve.has_value());
    CHECK(io.minutes() > curve_end + 5);   // 曲线时间到不算完：等实测到位

    FakeIo io2;
    io2.temp_at = [&io2](double) -> std::optional<double> { return io2.curve ? 100.0 : 25.0; };   // 永远不到 120
    auto r2 = io2.run(Curve({{120, 5, 0}}));
    CHECK(r2.faulted());
    CHECK(r2.fault.find("升温 120 min 未到 120 ± 2 °C") != std::string::npos);
    double curve_min = (120 - 25) / 5.0;
    CHECK(io2.minutes() >= curve_min + 120 && io2.minutes() <= curve_min + 120.2);   // maxWait 从曲线结束开始算
}

TEST(core_program_step_engine, Heat_Blocked_Faults) {
    FakeIo io;
    io.heat_block = "温控离线";
    auto r = io.run(S(StepType::Heat));
    CHECK(r.faulted());
    CHECK_EQ(r.fault, std::string("升温：温控离线"));
}

// ============ hold ============

TEST(core_program_step_engine, Hold_Time) {
    FakeIo io;
    io.run(S(StepType::Hold, {{"until", sv("time")}, {"time", 12.0}}));
    CHECK(io.minutes() >= 12 && io.minutes() <= 12.05);
    CHECK(!io.states.empty() && io.states.back() == ReactorState::Wait);
}

TEST(core_program_step_engine, Hold_TempBelow_EndsWhenReached) {
    FakeIo io;
    io.temp_at = [](double t) -> std::optional<double> { return 80 - t; };
    io.run(S(StepType::Hold, {{"until", sv("tempBelow")}, {"temp", 40.0}, {"maxWait", 120.0}}));
    CHECK(io.minutes() >= 40 && io.minutes() <= 40.1);
    CHECK(io.has_log("保持完成"));
}

TEST(core_program_step_engine, Hold_PressAbove_Timeout_Continue) {
    FakeIo io;
    io.P = 10;
    io.run(S(StepType::Hold, {{"until", sv("pressAbove")}, {"pres", 50.0}, {"maxWait", 5.0}}));
    CHECK(io.minutes() >= 5 && io.minutes() <= 5.05);
    CHECK(std::any_of(io.logs.begin(), io.logs.end(), [](const Log& l) {
        return l.level == "warn" && l.detail.find("保持 5 min 未达到") != std::string::npos;
    }));
}

TEST(core_program_step_engine, Hold_SensorOffline_TreatedAsTimeoutAfter60s) {
    FakeIo io;
    io.fixed_temp.reset();
    io.run(S(StepType::Hold, {{"until", sv("tempAbove")}, {"temp", 40.0}, {"maxWait", 60.0}, {"onTimeout", sv("continue")}}));
    CHECK(io.seconds() >= 60 && io.seconds() <= 62);   // 不是悄悄按时间等 60 min
    CHECK(std::any_of(io.logs.begin(), io.logs.end(), [](const Log& l) {
        return l.level == "warn" && l.detail.find("温控离线超过 60 s") != std::string::npos;
    }));

    FakeIo io2;
    io2.p_offline = true;
    auto r = io2.run(S(StepType::Hold, {{"until", sv("pressBelow")}, {"pres", 5.0}, {"onTimeout", sv("stop")}}));
    CHECK(r.faulted());
    CHECK(r.fault.find("压力采集离线超过 60 s") != std::string::npos);
}

// ============ react ============

TEST(core_program_step_engine, React_Stall_CountsOnlyAfterMinTime) {
    FakeIo io;
    io.uptake_counting = io.uptake_valid = true;
    io.rate_at = [](double) -> std::optional<double> { return 0.001; };
    io.run(S(StepType::React, {{"time", 480.0}, {"minTime", 30.0}, {"stallOn", sv("on")}, {"stallRate", 0.005}, {"stallMin", 30.0}}));
    CHECK(io.minutes() >= 60 && io.minutes() <= 60.1);   // 最短 30 + 持续 30
    CHECK(io.end_reason == std::optional<std::string>("吸气停止（30 min < 0.005 mmol/min）"));
    CHECK(std::any_of(io.logs.begin(), io.logs.end(), [](const Log& l) { return l.title == "1 号 反应结束 · 吸气停止（30 min < 0.005 mmol/min）"; }));
}

TEST(core_program_step_engine, React_Stall_NullRateDoesNotCount) {
    FakeIo io;
    io.uptake_counting = io.uptake_valid = true;
    io.rate_at = [](double t) -> std::optional<double> { if (t < 40) return std::nullopt; return 0.001; };
    io.run(S(StepType::React, {{"minTime", 30.0}, {"stallMin", 30.0}}));
    CHECK(io.minutes() >= 70 && io.minutes() <= 70.1);
    // 中途出现 null：计时重新开始
    FakeIo io2;
    io2.uptake_counting = io2.uptake_valid = true;
    io2.rate_at = [](double t) -> std::optional<double> { if (t > 45 && t < 46) return std::nullopt; return 0.001; };
    io2.run(S(StepType::React, {{"minTime", 30.0}, {"stallMin", 30.0}}));
    CHECK(io2.minutes() >= 76 && io2.minutes() <= 76.1);
}

TEST(core_program_step_engine, React_HighRate_RunsToMaxTime) {
    FakeIo io;
    io.uptake_counting = io.uptake_valid = true;
    io.rate_at = [](double) -> std::optional<double> { return 0.05; };
    io.run(S(StepType::React, {{"time", 90.0}, {"minTime", 10.0}}));
    CHECK(io.minutes() >= 90 && io.minutes() <= 90.05);
    CHECK(io.end_reason == std::optional<std::string>("最长时间到"));
}

TEST(core_program_step_engine, React_MmolReached_AfterMinTime) {
    FakeIo io;
    io.uptake_counting = io.uptake_valid = true;
    io.rate_at = [](double) -> std::optional<double> { return 0.1; };
    io.on_tick = [](FakeIo& f) { f.uptake_mmol = 0.1 * f.minutes(); };   // 5 mmol 在 50 min
    io.run(S(StepType::React, {{"minTime", 60.0}, {"mmolOn", sv("on")}, {"mmol", 5.0}, {"stallOn", sv("off")}}));
    CHECK(io.minutes() >= 60 && io.minutes() <= 60.1);   // 到了 5 mmol 但没过最短时间，等到 60 min
    CHECK(io.end_reason == std::optional<std::string>("吸气量达到 5.00 mmol"));
}

TEST(core_program_step_engine, React_NotMetered_OnlyTime_WarnsOnce) {
    FakeIo io;
    io.uptake_counting = false;
    io.rate_at = [](double) -> std::optional<double> { return 0.0; };
    io.run(S(StepType::React, {{"time", 45.0}, {"minTime", 0.0}, {"mmolOn", sv("on")}, {"mmol", 0.01}}));
    CHECK(io.minutes() >= 45 && io.minutes() <= 45.05);
    CHECK(io.end_reason == std::optional<std::string>("最长时间到"));
    CHECK_EQ(std::count_if(io.logs.begin(), io.logs.end(), [](const Log& l) {
                 return l.level == "warn" && l.detail.find("吸气量未计量") != std::string::npos;
             }),
             1);
}

// ============ end ============

TEST(core_program_step_engine, End_StopsHeatReleasesStirClosesValve) {
    FakeIo io;
    io.regulation_active = true;
    io.uptake_counting = true;
    io.stir_on = true;
    io.stir.request(1, 400);
    io.end_reason = "最长时间到";
    io.run(S(StepType::End));
    CHECK(io.has_call("heat:stop"));
    CHECK(io.has_call("stir:release"));
    CHECK(io.has_call("sv:close"));
    CHECK(!io.stir_on);
    CHECK(!io.regulation_active);
    CHECK(!io.uptake_counting);
    CHECK(!io.states.empty() && io.states.back() == ReactorState::Done);
    CHECK(io.has_log("结束原因：最长时间到"));
}

TEST(core_program_step_engine, End_DoesNotStopOperatorStir_WhenProgramNeverHeldIt) {
    FakeIo io;
    io.stir_on = true;   // 操作员手动开的搅拌，本程序没有搅拌步骤
    io.run(S(StepType::End));
    CHECK(io.stir_on);
    CHECK(!io.has_call("stir:release"));
}

TEST(core_program_step_engine, LegacyStep_Faults) {
    FakeIo io;
    auto r = io.run(StepSnapshot::create(StepType::Wait, 1, "等待", "", Params{}));
    CHECK(r.faulted());
}

// ============ C++ 状态机自身的约定 ============

TEST(core_program_step_engine, EarlyTicks_AreNoOps_AndAbortReleasesGas) {
    StepRunner r(S(StepType::Vent, {{"flush", 0.0}}), "03/09 放空", RunnerConfig{1, PressureUnit::MPa});
    FakeIo io;
    io.P = 100;
    StepOutput o = r.tick(io.input(StepEvent::None));
    CHECK(o.waiting == Await::Gas);
    // 事件没到：原样返回，不重复请求气路
    StepOutput o2 = r.tick(io.input(StepEvent::None));
    CHECK(o2.actions.empty());
    CHECK(o2.waiting == Await::Gas);
    StepOutput o3 = r.tick(io.input(StepEvent::GasGranted));
    CHECK(r.gas_held());
    CHECK(o3.wake_at_ms == io.now_ms + 200);
    CHECK(o3.fresh_pressure);
    // 没到点：原样返回
    StepOutput o4 = r.tick(io.input(StepEvent::None));
    CHECK(o4.actions.empty());
    CHECK_EQ(o4.wake_at_ms, o3.wake_at_ms);
    auto acts = r.abort();
    CHECK_EQ(acts.size(), size_t(1));
    CHECK(acts[0].kind == ActionKind::ReleaseGas);
    CHECK(!r.gas_held());
    CHECK(r.finished());
    CHECK(r.tick(io.input(StepEvent::None)).status == StepStatus::Done);
}

TEST(core_program_step_engine, PressureReadFailure_IsGasFault_AndReleasesGas) {
    FakeIo io;
    io.P = 100;
    io.on_tick = [](FakeIo& f) { if (f.frame.vent) f.p_offline = true; };
    auto r = io.run(S(StepType::Vent, {{"flush", 0.0}}));
    CHECK(r.faulted());
    CHECK(r.kind == FaultKind::Gas);
    CHECK_EQ(r.fault, std::string("1 号 压力读取失败"));
    CHECK(!io.gas_held());
}
