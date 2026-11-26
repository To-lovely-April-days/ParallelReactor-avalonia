#include "sim_source.hpp"

#include "../ui/kit/theme.hpp"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <ctime>
#include <cstdint>
#include <optional>
#include <string>
#include <vector>

namespace reactor::hmi::model {

namespace {

// 各路正在跑的程序：步骤取自 SimSource::progs_（按 /program-schema 默认值建的 v0.8 九步程序）
struct ChanPlan {
    int prog;              // progs_ 下标；-1 = 没有程序
    int step;              // 当前步（0 起）
    int64_t progElapsed;   // 程序已运行（秒，起点时刻）
    int64_t stepElapsed;   // 当前步已运行（秒）
};

ChanPlan g_plan[8] = {
    {0, 5, 1 * 3600 + 12 * 60 + 8, 7 * 60},     // 1 号 加氢筛选-A 充压
    {0, 6, 3 * 3600 + 58 * 60 + 41, 148 * 60},  // 2 号 加氢筛选-A 反应
    {1, 4, 1 * 3600 + 34 * 60, 24 * 60},        // 3 号 加氢筛选-B 升温（12:31 开始）
    {1, 4, 2 * 3600 + 40 * 60 + 2, 38 * 60},    // 4 号 加氢筛选-B 升温 · 到位后平衡（保持）
    {-1, 0, 0, 0},                              // 5 号 手动（另有一份未下发的程序）
    {2, 1, 3 * 60 + 12, 3 * 60},                // 6 号 加氢筛选-C 置换，排队等气路
    {-1, 0, 0, 0},
    {-1, 0, 0, 0},
};

struct SD { std::string kind, name; int minutes; };
std::vector<SD> sd(const ProgramDoc& p) {
    std::vector<SD> v;
    for (auto& s : p.steps) {
        double m = step_minutes(s).minutes;
        if (s.type == "exchange") m = std::max(m, 15.0);  // 估算只算阀动作；现场一次置换约 15 min（含等气路）
        if (s.type == "stir") m = std::max(m, 2.0);
        if (s.type == "end") m = std::max(m, 1.0);
        v.push_back({s.type, zh_step_name(s.type), std::max(1, (int)std::lround(m))});
    }
    return v;
}

const char* state_for(const char* kind) {
    std::string k = kind;
    if (k == "heat") return "heating";
    if (k == "press" || k == "exchange" || k == "leak") return "pressing";
    if (k == "react") return "react";
    if (k == "hold") return "wait";
    if (k == "stir") return "stir";
    if (k == "vent") return "cooling";
    return "ready";
}

int64_t today_at(int h, int m, int s) {
    std::time_t t = std::time(nullptr);
    std::tm tm{};
    localtime_r(&t, &tm);
    tm.tm_hour = h; tm.tm_min = m; tm.tm_sec = s;
    return static_cast<int64_t>(std::mktime(&tm)) * 1000;
}

}  // namespace

SimSource::SimSource(int64_t base_ms) {
    base_ = base_ms ? base_ms : today_at(14, 5, 12);
    now_ = base_;
    for (size_t i = 0; i < 8; ++i) plan_[i] = PlanRow{g_plan[i].prog, g_plan[i].step, g_plan[i].progElapsed, g_plan[i].stepElapsed};
    make_programs();
    build_scenario();
    build_pages();
}

void SimSource::build_scenario() {
    Model& m = m_;
    m.connected = true;
    m.bootAt = today_at(8, 2, 0);
    auto& s = m.snap;
    s.simulated = true;
    s.ts = now_;
    s.channels.resize(8);
    const double T[8] = {80.1, 80.0, 62.4, 80.0, 25.0, 25.0, 24.9, 25.1};
    const double P[8] = {1.525, 1.986, 0.312, 2.000, 0.101, 0.100, 0.000, 0.000};
    for (int i = 0; i < 8; ++i) {
        ChannelSnap& ch = s.channels[static_cast<size_t>(i)];
        ch.id = i + 1;
        ch.installed = i < 6;
        ch.tempC = T[i];
        ch.pressurePsi = mpa_to_psi(P[i]);
        ch.solutionVolume = 5.0;
        ch.tempSpC = i < 5 ? 80 : 25;
        ch.state = i < 6 ? "ready" : "idle";
        m_.chanset[static_cast<size_t>(i)].heatOn = false;
        m_.chanset[static_cast<size_t>(i)].curve = {{40, 10}, {55, 10}, {70, 15}, {80, 15}, {80, 30}, {90, 20}};
        const PlanRow& pl = plan_[static_cast<size_t>(i)];
        if (pl.prog >= 0) {
            const std::vector<SD> steps = sd(progs_[static_cast<size_t>(pl.prog)]);
            const SD& st = steps[static_cast<size_t>(pl.step)];
            ch.state = state_for(st.kind.c_str());
            if (i == 3) ch.state = "wait";  // 4 号：升温到位后平衡（釜上显示保持倒计时环）
            ch.programOwned = true;
            ChannelProgramSnap pr;
            pr.running = true; pr.stepIndex = pl.step; pr.stepCount = 9; pr.state = "running";
            pr.startedBy = "local"; pr.startedAt = now_ - pl.progElapsed * 1000;
            ch.program = pr;
            progStart_[static_cast<size_t>(i)] = now_ - pl.progElapsed * 1000;
            m_.chanset[static_cast<size_t>(i)].heatOn = st.kind != "leak" && st.kind != "exchange";
        }
    }
    // 1 号升压中：设定 2.000 MPa，进气阀开
    c(1).pressureSpPsi = mpa_to_psi(2.0); c(1).valveOpen = true;
    // 2 号反应中：吸气量在涨
    c(2).gasConsumed = 0.4; c(2).gasRate = 0.012; c(2).pressureSpPsi = mpa_to_psi(2.0);
    // 4 号保持：已经吸了 1.8
    c(4).gasConsumed = 1.8; c(4).pressureSpPsi = mpa_to_psi(2.0);
    // 6 号排队等气路
    c(6).program->waitingGas = true; c(6).program->state = "waitingGas"; c(6).state = "ready";
    s.stir = StirSnap{}; s.stir.on = true; s.stir.rpmSp = 400;
    s.gas.inlets = {{"inert", true}, {"a", false}, {"b", false}};
    s.gas.manualValve = false; s.gas.owner = "program"; s.gas.task = "1 号充压";
    s.programs.running = 4; s.programs.channels = {1, 2, 3, 4, 6}; s.programs.gasHolder = 1; s.programs.waitingGas = {6};
    m.settings.alarms.overTempC = 200; m.settings.alarms.overPressurePsi = mpa_to_psi(2.6);

    for (int i = 0; i < 8; ++i) {
        ProgressExt& p = m.progress[static_cast<size_t>(i)];
        const PlanRow& pl = plan_[static_cast<size_t>(i)];
        if (pl.prog < 0) continue;
        p.programName = progs_[static_cast<size_t>(pl.prog)].name;
        for (auto& x : sd(progs_[static_cast<size_t>(pl.prog)])) p.stepNames.push_back(x.name);
    }
    m.progress[5].gasQueueRank = 2; m.progress[5].waitingFor = {1}; m.progress[5].waitedSec = 8 * 60;
    // 5 号：已编辑一份程序、还没下发（方案总览里显示）
    m.progress[4].programName = "加氢筛选-B"; m.progress[4].editedNotSent = true; m.progress[4].stepCountDraft = 9;
    m.progress[4].draftTotalSec = 9 * 3600 + 30 * 60;
    for (auto& x : sd(progs_[1])) m.progress[4].stepNames.push_back(x.name);

    // ---- 报警和事件（Alarm-Page 的那一屏）----
    auto alarm = [&](const char* id, const char* level, std::optional<int> ch, const char* title, const char* msg, int64_t at,
                     const char* state, std::optional<int64_t> cleared, AlarmExt ext) {
        AlarmItem a;
        a.dto.id = id; a.dto.level = level; a.dto.channel = ch; a.dto.title = title; a.dto.message = msg;
        a.dto.raisedAt = at; a.dto.state = state; a.dto.clearedAt = cleared;
        a.ext = std::move(ext);
        m.alarms.push_back(std::move(a));
    };
    AlarmExt e1;
    e1.tier = 1; e1.source = "channel"; e1.quantity = "pressure"; e1.value = 2.62; e1.setpoint = 2.0; e1.limit = 2.60;
    e1.rangeLo = 1.5; e1.rangeHi = 3.0;
    e1.did = {"关闭 7 号进气阀", "停止 7 号加热", "其他通道照常运行"};
    e1.next = {"等 7 号压力降回 2.60 MPa 以下", "点「确认」，7 号才能重新加热和进气"};
    e1.context = "程序「加氢筛选-B」第 6 步 反应";
    alarm("a1", "err", 7, "超压 · 联锁动作", "压力 2.62 MPa，上限 2.60 MPa · 已关 7 号进气阀、停 7 号加热", today_at(14, 2, 18), "active",
          std::nullopt, e1);
    AlarmExt e2; e2.tier = 3; e2.source = "channel";
    alarm("a2", "warn", 3, "吸气量未计量", "补气时压力读数跳变，这 2 min 的吸气量没有计入", today_at(13, 56, 40), "active", std::nullopt, e2);
    AlarmExt e3; e3.tier = 2; e3.source = "comm";
    alarm("a3", "err", std::nullopt, "温控模块离线", "8 路温度读不到 · 自动重连", today_at(13, 20, 5), "cleared", today_at(13, 20, 41), e3);
    AlarmExt e4; e4.tier = 1; e4.source = "channel"; e4.quantity = "temp";
    alarm("a4", "err", 5, "超温 · 切断加热", "温度 86.4 °C，上限 85.0 °C", today_at(12, 10, 33), "acked", today_at(12, 12, 2), e4);
    auto ev = [&](const char* level, const char* title, const char* msg, int64_t at) {
        EventRecord r; r.seq = ++alarmSeq_; r.ts = at; r.level = level; r.source = "system"; r.origin = "系统";
        r.title = title; r.message = msg;
        m.events.push_back(r);
    };
    ev("info", "3、4、5 号开始运行「加氢筛选-B」", "", today_at(13, 5, 0));
    ev("info", "泄漏测试 4/4 通过", "3–6 号", today_at(12, 58, 30));
    ev("warn", "搅拌转速恢复", "设定 400 rpm", today_at(12, 40, 10));
    ev("info", "开机自检完成", "8 路温度、压力、阀门正常", today_at(8, 2, 15));
    rebuild_plans();
}

void SimSource::rebuild_plans() {
    for (int i = 0; i < 8; ++i) {
        PlanExt& pe = m_.plans[static_cast<size_t>(i)];
        pe = PlanExt{};
        const PlanRow& pl = plan_[static_cast<size_t>(i)];
        const ChannelSnap& ch = m_.snap.channels[static_cast<size_t>(i)];
        ProgressExt& pg = m_.progress[static_cast<size_t>(i)];
        if (pl.prog < 0) {
            if (pg.editedNotSent) {  // 未下发：从「现在」往后排
                int64_t t = now_ + 25 * 60 * 1000;
                for (auto& x : sd(m_.x.programs[static_cast<size_t>(i)])) {
                    PlanSeg sg; sg.kind = x.kind; sg.name = x.name; sg.start = t;
                    sg.end = t + x.minutes * 60000LL; sg.openEnded = sg.kind == "react";
                    t = sg.end; pe.segs.push_back(sg);
                }
            }
            continue;
        }
        if (!ch.program || !ch.program->running) continue;
        const std::vector<SD> steps = sd(m_.x.programs[static_cast<size_t>(i)]);
        const int nsteps = (int)steps.size();
        bool waiting = ch.program && ch.program->waitingGas;
        int64_t t = progStart_[static_cast<size_t>(i)];
        // 已做完的步：按表里的时长等比压进「程序已运行 - 当前步已运行」
        int64_t pastTotal = 0;
        for (int k = 0; k < pl.step; ++k) pastTotal += steps[static_cast<size_t>(k)].minutes * 60;
        int64_t curStepStart = now_ - (pl.stepElapsed + (now_ - base_) / 1000) * 1000;
        double scale = pastTotal > 0 ? double(curStepStart - t) / 1000.0 / double(pastTotal) : 1.0;
        for (int k = 0; k < nsteps; ++k) {
            const SD& sk = steps[static_cast<size_t>(k)];
            PlanSeg sg; sg.kind = sk.kind; sg.name = sk.name;
            if (k < pl.step) {
                sg.start = t; sg.end = t + (int64_t)(sk.minutes * 60 * scale * 1000); t = sg.end;
            } else if (k == pl.step) {
                sg.start = waiting ? now_ : curStepStart;
                sg.end = std::max<int64_t>(now_ + 60000, sg.start + sk.minutes * int64_t{60000});
                t = sg.end;
            } else {
                sg.start = t; sg.end = t + sk.minutes * 60000LL; t = sg.end;
            }
            sg.openEnded = sg.kind == "react";
            if (sg.openEnded) sg.earliestEnd = sg.start + 2 * 3600 * 1000;
            pe.segs.push_back(sg);
        }
        // 计划温度 / 压力（方案总览点开一路）
        double tp = 25;
        for (auto& sg : pe.segs) {
            if (sg.kind == "heat") {
                pe.tempPlan.push_back({sg.start, tp});
                tp = 80;
                pe.tempPlan.push_back({sg.end, tp});
            } else if (sg.kind == "vent" || sg.kind == "end") {
                pe.tempPlan.push_back({sg.start, tp});
                if (sg.kind == "end") { pe.tempPlan.push_back({sg.end, 25}); }
            } else {
                pe.tempPlan.push_back({sg.start, tp});
                pe.tempPlan.push_back({sg.end, tp});
            }
            double pp = sg.kind == "leak" ? 3.0 : sg.kind == "exchange" ? 0.02 : (sg.kind == "press" || sg.kind == "hold" || sg.kind == "react") ? 2.0 : 0.1;
            if (sg.kind == "stir" || sg.kind == "heat") pp = 0.1;
            if (sg.kind == "vent" || sg.kind == "end") pp = 0;
            pe.pressPlan.push_back({sg.start, pp});
            pe.pressPlan.push_back({sg.end, pp});
        }
        // 实测：从开始到现在（温度慢慢往计划靠）
        for (int64_t x = progStart_[static_cast<size_t>(i)]; x <= now_; x += 60000) {
            double target = 25, ptarget = 0.1;
            for (size_t k = 0; k + 1 < pe.tempPlan.size(); ++k)
                if (x >= pe.tempPlan[k].t && x <= pe.tempPlan[k + 1].t) {
                    double f = pe.tempPlan[k + 1].t == pe.tempPlan[k].t ? 1 : double(x - pe.tempPlan[k].t) / double(pe.tempPlan[k + 1].t - pe.tempPlan[k].t);
                    target = pe.tempPlan[k].v + (pe.tempPlan[k + 1].v - pe.tempPlan[k].v) * f;
                }
            for (size_t k = 0; k < pe.pressPlan.size(); k += 2)
                if (x >= pe.pressPlan[k].t && x <= pe.pressPlan[k + 1].t) ptarget = pe.pressPlan[k].v;
            pe.tempActual.push_back({x, x == now_ ? (ch.tempC ? *ch.tempC : target) : target - 0.6});
            pe.pressActual.push_back({x, ptarget});
        }
        if (!pe.tempActual.empty() && ch.tempC) pe.tempActual.back().v = *ch.tempC;
        // 进度
        pg.elapsedSec = pl.progElapsed + (now_ - base_) / 1000;
        int64_t stepEl = pl.stepElapsed + (now_ - base_) / 1000;
        const SD& st = steps[static_cast<size_t>(pl.step)];
        pg.stepFraction = waiting ? 0 : std::min(1.0, double(stepEl) / (st.minutes * 60.0));
        int64_t rem = std::max<int64_t>(0, st.minutes * 60 - stepEl);
        for (int k = pl.step + 1; k < nsteps; ++k) rem += steps[static_cast<size_t>(k)].minutes * 60;
        pg.remainingSec = rem;
        std::string kind = st.kind;
        char b[64];
        if (kind == "heat") std::snprintf(b, sizeof b, "%.0f °C", ch.tempSpC);
        else if (kind == "press") std::snprintf(b, sizeof b, "%.1f", psi_to_mpa(ch.pressureSpPsi));
        else if (kind == "hold") std::snprintf(b, sizeof b, "%lld min", (long long)std::max<int64_t>(0, (st.minutes * 60 - stepEl) / 60));
        else b[0] = 0;
        pg.stepTarget = b;
        if (waiting) pg.waitedSec = 8 * 60 + (now_ - base_) / 1000;
    }
}

void SimSource::step(int64_t elapsed_ms) {
    now_ = base_ + elapsed_ms;
    double dt = (now_ - last_) / 1000.0;
    if (last_ == 0) dt = 0;
    last_ = now_;
    m_.snap.ts = now_;
    m_.snap.seq++;
    for (int i = 1; i <= 8; ++i) {
        ChannelSnap& ch = c(i);
        if (!ch.installed || !ch.tempC) continue;
        const ChanSetExt& lc = m_.chanset[static_cast<size_t>(i - 1)];
        double t = *ch.tempC;
        bool heating = lc.heatOn || ch.state == "heating";
        double target = heating ? ch.tempSpC : 25.0;
        double rate = heating ? 0.8 / 60.0 : 0.3 / 60.0;  // °C/s
        double d = target - t;
        double stepv = std::copysign(std::min(std::fabs(d), rate * dt + std::fabs(d) * 0.002 * dt), d);
        // 到了设定附近来回抖一点，看起来像真的
        double jitter = std::sin((now_ / 1000.0 + i * 7) * 0.7) * 0.02;
        ch.tempC = std::round((t + stepv + jitter * (std::fabs(d) < 0.5)) * 100) / 100;
        if (ch.state == "pressing" && ch.pressurePsi) {
            double p = *ch.pressurePsi, sp = ch.pressureSpPsi;
            if (p < sp) ch.pressurePsi = std::min(sp, p + mpa_to_psi(0.02 / 60.0) * dt);
        }
        if (ch.state == "react") {
            double r = 0.012 + 0.001 * std::sin(now_ / 9000.0 + i);
            ch.gasRate = r;
            ch.gasConsumed = (ch.gasConsumed ? *ch.gasConsumed : 0) + r * dt / 60.0;
        }
    }
    step_pages(dt);
    if (static_cast<int>((now_ - base_) / 1000) % 5 == 0) rebuild_plans();
}

// ---------------------------------------------------------------- 预览用的报警
void SimSource::inject_l1() {
    for (auto& a : m_.alarms)
        if (a.dto.id == "a1") { a.dto.state = "active"; return; }
}
void SimSource::inject_l2() {
    AlarmItem a;
    a.dto.id = "a" + std::to_string(++alarmSeq_); a.dto.level = "err"; a.dto.title = "压力采集模块离线";
    a.dto.message = "8 路压力读数停在 14:04:54，这段的吸气量也算不出来。"; a.dto.raisedAt = now_; a.dto.state = "active";
    a.ext.tier = 2; a.ext.source = "comm"; a.ext.downSince = now_ - 18000;
    a.ext.highlight = "8 路压力读数停在 " + std::string("14:04:54") + "，这段的吸气量也算不出来。";
    a.ext.hint = "检查 8AI 模块供电和 RS485 接线；恢复后读数自动继续，这条报警自动解除。";
    a.ext.did = {"连续 3 次通讯失败"};
    m_.alarms.insert(m_.alarms.begin(), a);
}
void SimSource::inject_l3() {
    for (auto& a : m_.alarms)
        if (a.dto.id == "a2") {
            AlarmItem b = a;
            b.dto.id = "a" + std::to_string(++alarmSeq_);
            b.dto.raisedAt = now_;
            m_.alarms.insert(m_.alarms.begin(), b);
            return;
        }
}

// ---------------------------------------------------------------- Actions（模拟：直接改状态）
void SimSource::stop_all(bool include_stir) {
    for (int i = 1; i <= 8; ++i) {
        ChannelSnap& ch = c(i);
        m_.chanset[static_cast<size_t>(i - 1)].heatOn = false;
        ch.valveOpen = false;
        if (ch.program && ch.program->running) { ch.program->running = false; ch.program->state = "aborted"; ch.programOwned = false; }
        if (ch.installed && ch.state != "idle") ch.state = "ready";
    }
    for (auto& in : m_.snap.gas.inlets) in.open = false;
    if (include_stir) m_.snap.stir.on = false;
}
void SimSource::stir_set_rpm(int rpm) { m_.snap.stir.rpmSp = rpm; }
void SimSource::stir_run(bool on) { m_.snap.stir.on = on; }
void SimSource::manual_valve(bool on) { m_.snap.gas.manualValve = on; }
void SimSource::set_temp_sp(int ch, double v) { c(ch).tempSpC = v; }
void SimSource::heat(int ch, bool on) {
    m_.chanset[static_cast<size_t>(ch - 1)].heatOn = on;
    if (!c(ch).programOwned) c(ch).state = on ? "heating" : "ready";
}
void SimSource::set_heat_mode(int ch, const std::string& mode) { c(ch).spMode = mode; }
void SimSource::set_band(int ch, double v) { m_.chanset[static_cast<size_t>(ch - 1)].band = v; }
void SimSource::set_anti_overshoot(int ch, bool on) { m_.chanset[static_cast<size_t>(ch - 1)].antiOvershoot = on; }
void SimSource::set_profile(int ch, const std::vector<CurveSeg>& s) { m_.chanset[static_cast<size_t>(ch - 1)].curve = s; }
void SimSource::start_profile(int ch) { heat(ch, true); }
void SimSource::set_valve(int ch, bool open) { c(ch).valveOpen = open; }
void SimSource::set_gas_source(int k, bool open) {
    static const char* ids[3] = {"inert", "a", "b"};
    if (k == 3) { m_.snap.gas.ventOpen = open; return; }
    if (k < 0 || k > 2) return;
    for (auto& in : m_.snap.gas.inlets) {
        if (in.id == ids[k]) in.open = open;
        else if (open) in.open = false;  // 气源最多开一路（后台同样规则）
    }
    if (open) m_.snap.gas.ventOpen = false;
}
void SimSource::gas_zero(int ch) { c(ch).gasConsumed = 0.0; }
void SimSource::set_solution_volume(int ch, double ml) { c(ch).solutionVolume = ml; }
void SimSource::channel_enable(int ch, bool on) {
    c(ch).installed = on;
    c(ch).state = on ? "ready" : "idle";
}
void SimSource::copy_to_all(int ch) {
    for (int i = 1; i <= 8; ++i)
        if (i != ch && c(i).installed && !c(i).programOwned) {
            c(i).tempSpC = c(ch).tempSpC;
            m_.chanset[static_cast<size_t>(i - 1)] = m_.chanset[static_cast<size_t>(ch - 1)];
        }
}
void SimSource::abort_program(int ch) {
    ChannelSnap& x = c(ch);
    if (x.program) { x.program->running = false; x.program->state = "aborted"; }
    x.programOwned = false; x.state = "ready"; x.valveOpen = false;
    m_.chanset[static_cast<size_t>(ch - 1)].heatOn = false;
}
void SimSource::ack_alarm(const std::string& id) {
    for (auto& a : m_.alarms)
        if (a.dto.id == id && a.dto.state == "active") a.dto.state = "acked";
}
void SimSource::ack_all() {
    for (auto& a : m_.alarms)
        if (a.dto.state == "active") a.dto.state = "acked";
}
void SimSource::set_language(const std::string& code) { m_.languageCode = code; }

}  // namespace reactor::hmi::model
