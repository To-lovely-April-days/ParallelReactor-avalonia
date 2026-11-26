// 模拟数据源 · 第二批页面：程序 / 程序库、趋势、运行记录、每日文件、泄漏测试、PID、容积标定、通讯、网络、存储。
// 数据照画布上的场景，但和第一批的装置总览对得上（同一套程序、同一个时钟）。噪声用固定种子，截图稳定。
#include "sim_source.hpp"

#include "../ui/kit/theme.hpp"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <ctime>
#include <limits>
#include <cstdint>
#include <memory>
#include <optional>
#include <string>
#include <vector>

namespace reactor::hmi::model {

extern const char* const kSampleProgramSchema;

namespace {

int64_t day_at(int days_ago, int h, int m, int s = 0) {
    std::time_t t = std::time(nullptr) - (std::time_t)days_ago * 86400;
    std::tm tm{};
    localtime_r(&t, &tm);
    tm.tm_hour = h; tm.tm_min = m; tm.tm_sec = s;
    return static_cast<int64_t>(std::mktime(&tm)) * 1000;
}
std::string ymd(int64_t ms) {
    std::time_t t = (std::time_t)(ms / 1000);
    std::tm tm{};
    localtime_r(&t, &tm);
    char b[16];
    std::strftime(b, sizeof b, "%Y%m%d", &tm);
    return b;
}

// 固定种子的伪随机（-1..1）
float rnd(uint32_t a, uint32_t b) {
    uint32_t x = a * 2654435761u ^ (b + 0x9e3779b9u + (a << 6) + (a >> 2));
    x ^= x >> 15; x *= 0x2c1b3c6dU; x ^= x >> 12; x *= 0x297a2d39U; x ^= x >> 15;
    return (float)((x & 0xffffff) / double(0xffffff) * 2.0 - 1.0);
}

constexpr float kNaN = std::numeric_limits<float>::quiet_NaN();
constexpr double kPsi = 0.00689475729;

// 一次运行的趋势（只要吸气量；记录页「各路吸气量」）
std::shared_ptr<Trend> run_trend(int64_t start, double minutes, const std::vector<int>& chs, const std::vector<double>& finals,
                                 double gas_start_min, double abort_min, uint32_t seed) {
    auto t = std::make_shared<Trend>();
    t->t0 = start;
    t->stepMs = 60000;
    int n = (int)(std::min(minutes, abort_min > 0 ? abort_min : minutes)) + 1;
    for (auto& v : t->gas) v.assign((size_t)n, kNaN);
    for (auto& v : t->temp) v.assign((size_t)n, kNaN);
    for (auto& v : t->press) v.assign((size_t)n, kNaN);
    for (size_t k = 0; k < chs.size(); ++k) {
        int ch = chs[k] - 1;
        double G = finals[k], tau = 70 + 12 * k;
        double drift = 0;
        for (int i = 0; i < n; ++i) {
            double m = i;
            drift = drift * 0.8 + rnd(seed + (uint32_t)k, (uint32_t)i) * 0.012;
            double g = m < gas_start_min ? 0 : G * (1 - std::exp(-(m - gas_start_min) / tau));
            t->gas[(size_t)ch][(size_t)i] = (float)std::max(0.0, g + drift * (m >= gas_start_min));
            t->temp[(size_t)ch][(size_t)i] = (float)(m < gas_start_min - 40 ? 25 : 80 + rnd(seed + 77, (uint32_t)i) * 0.3);
            t->press[(size_t)ch][(size_t)i] = (float)(m < gas_start_min ? 0.02 : 2.0 + rnd(seed + 99, (uint32_t)i) * 0.01);
        }
    }
    return t;
}

}  // namespace

// ================================================================ 程序
void SimSource::make_programs() {
    PagesExt& x = m_.x;
    parse_program_schema(kSampleProgramSchema, x.schema);
    const ProgramSchema& S = x.schema;
    ProgramDoc a = hydrogenation_template(S);
    a.id = "P251120091502"; a.name = "加氢筛选-A";
    a.note = "加氢筛选，A 组催化剂：80 °C、气体 A 2.000 MPa 稳压，吸气停止就结束。";
    a.modified = "2026-11-24T16:20:00"; a.modifiedMs = day_at(2, 16, 20); a.by = "触屏"; a.revision = 4;
    ProgramDoc b = a;
    b.id = "P251121101233"; b.name = "加氢筛选-B";
    b.note = "加氢筛选，B 组催化剂：80 °C、气体 A 2.000 MPa 稳压，吸气停止就结束。";
    b.modifiedMs = day_at(0, 9, 40); b.revision = 7;
    ProgramDoc c = a;
    c.id = "P251122083010"; c.name = "加氢筛选-C";
    c.note = "加氢筛选，C 组：100 °C、600 rpm，最长 9 h。";
    c.steps[3].set_num("rpm", 450);
    c.steps[4].set_num("temp", 100);
    c.steps[6].set_num("time", 540);
    c.modifiedMs = day_at(1, 17, 5); c.revision = 2;
    progs_ = {a, b, c};
    for (size_t i = 0; i < 8; ++i) {
        const PlanRow& pl = plan_[i];
        if (pl.prog >= 0) {
            x.programs[i] = progs_[static_cast<size_t>(pl.prog)];
            x.programSrcId[i] = progs_[static_cast<size_t>(pl.prog)].id;
        }
    }
    // 5 号：从程序库载入「加氢筛选-B」后改过、还没下发
    x.programs[4] = b;
    x.programSrcId[4] = b.id;

    // 程序库 · 我的程序（按修改时间倒序）
    ProgramDoc cx = a;
    cx.id = "P251118140001"; cx.name = "催化剂 X · 曲线升温"; cx.note = "催化剂 X：分段升到 140 °C 再充压反应。";
    cx.steps.erase(cx.steps.begin() + 2);  // 只置换一次惰性气体
    cx.steps[3].set_code("mode", "curve");
    cx.steps[3].segments = {{40, 2, 5}, {55, 2, 5}, {70, 1.5, 10}, {85, 1.5, 10}, {100, 1, 15}, {115, 1, 15}, {130, 0.5, 20}, {140, 0.5, 30}};
    cx.steps[5].set_num("time", 240);
    cx.modifiedMs = day_at(5, 11, 2); cx.revision = 3;
    ProgramDoc lk;
    lk.id = "P251110090000"; lk.name = "只做气密 · 2 MPa"; lk.note = "只做气密检查：2.000 MPa 保压 30 min。";
    lk.steps = {new_step(S, "leak"), new_step(S, "end")};
    lk.steps[0].set_num("pres", 2.0 / kPsi);
    lk.modifiedMs = day_at(16, 10, 0); lk.revision = 1;
    x.library = {{b, {}}, {a, {}}, {c, {}}, {cx, {}}, {lk, {}}};
    // 内置模板（随软件提供，只读）
    ProgramDoc t1 = hydrogenation_template(S);
    t1.id = "T-hydrogenation"; t1.name = "加氢筛选"; t1.builtIn = true;
    t1.note = "加氢筛选的标准流程：先用惰性气体检漏和置换，再换成反应气，升到温度后充压并稳压，吸气停止就结束，最后放空并用惰性气体置换。";
    ProgramDoc t2;
    t2.id = "T-leak"; t2.name = "只做气密"; t2.builtIn = true; t2.note = "只做一次气密检查，结束后放空。";
    t2.steps = {new_step(S, "leak"), new_step(S, "end")};
    ProgramDoc t3;
    t3.id = "T-inert-heat"; t3.name = "惰性气氛加热"; t3.builtIn = true; t3.note = "惰性气体保护下升温、保持，最后放空。";
    for (const char* k : {"leak", "exchange", "stir", "heat", "hold", "vent", "end"}) t3.steps.push_back(new_step(S, k));
    t3.steps[4].set_num("time", 180);
    ProgramDoc t4;
    t4.id = "T-uptake-check"; t4.name = "吸气量验证"; t4.builtIn = true; t4.note = "用已知量的底物验证吸气量计算。";
    for (const char* k : {"exchange", "heat", "press", "react", "vent", "end"}) t4.steps.push_back(new_step(S, k));
    t4.steps[0].set_code("gas", "A");
    t4.steps[3].set_num("time", 90);
    x.templates = {{t1, {}}, {t2, {}}, {t3, {}}, {t4, {}}};
}

// ================================================================ 趋势
void SimSource::make_trend() {
    auto t = std::make_shared<Trend>();
    t->stepMs = 10000;
    t->t0 = day_at(0, 12, 0);
    int n = (int)((now_ - t->t0) / t->stepMs) + 1;
    for (int c = 0; c < 8; ++c) {
        auto& T = t->temp[(size_t)c]; auto& P = t->press[(size_t)c]; auto& G = t->gas[(size_t)c];
        T.assign((size_t)n, kNaN); P.assign((size_t)n, kNaN); G.assign((size_t)n, kNaN);
        const ChannelSnap& ch = m_.snap.channels[(size_t)c];
        if (!ch.installed) continue;
        const PlanExt& pe = m_.plans[(size_t)c];
        double temp = 25, press = 0.02, wander = 0;
        int64_t gas0 = 0;
        for (auto& sg : pe.segs) if (sg.kind == "react") gas0 = sg.start;
        double sp_now = ch.tempSpC;
        for (int i = 0; i < n; ++i) {
            int64_t at = t->t_at(i);
            double tt = 25, pp = 0.02;
            std::string kind;
            int64_t seg0 = 0;
            const PlanSeg* cur = nullptr;
            for (auto& sg : pe.segs)
                if (at >= sg.start && at < sg.end) { kind = sg.kind; seg0 = sg.start; cur = &sg; }
            bool afterHeat = false;
            for (auto& sg : pe.segs) if (sg.kind == "heat" && at >= sg.start) afterHeat = true;
            if (afterHeat) tt = c == 2 ? std::min(sp_now, 25 + (at - seg0) / 60000.0 * 1.8) : 80;
            if (kind == "heat" && c == 2) tt = 25 + (at - seg0) / 60000.0 * 1.6;
            if (kind == "leak") pp = 3.0 - (at - seg0) / 60000.0 * 0.0006;
            else if (kind == "exchange" && !(ch.program && ch.program->waitingGas && cur && cur->start >= now_ - 60000)) {
                double ph = std::fmod((at - seg0) / 60000.0, 5.0);
                pp = ph < 1.0 ? 0.5 : 0.02 + 0.48 * std::exp(-(ph - 1.0) * 3);
                if (cur && (at - seg0) > 15 * 60000) pp = 0.02;
            } else if (kind == "press") pp = std::min(2.0, 0.02 + (at - seg0) / 60000.0 * 0.12);
            else if (kind == "react" || kind == "hold") pp = 2.0;
            for (auto& sg : pe.segs) if ((sg.kind == "press" || sg.kind == "react") && at >= sg.end && kind != "vent") pp = std::max(pp, 0.0);
            // 一阶跟随 + 噪声
            temp += (tt - temp) * 0.08;
            wander = wander * 0.92 + rnd((uint32_t)c, (uint32_t)i) * 0.05;
            press += (pp - press) * 0.35;
            T[(size_t)i] = (float)(temp + wander + rnd((uint32_t)c + 40, (uint32_t)i) * 0.12);
            P[(size_t)i] = (float)std::max(0.0, press + rnd((uint32_t)c + 80, (uint32_t)i) * 0.004 * (press > 0.1 ? 1 : 0.3));
            if (gas0 && at >= gas0) {
                double m = (at - gas0) / 60000.0;
                G[(size_t)i] = (float)(4.4 * (1 - std::exp(-m / 180.0)) + rnd((uint32_t)c + 120, (uint32_t)i) * 0.008);
            } else if (c == 3) {
                G[(size_t)i] = (float)(1.8 + rnd(7, (uint32_t)i) * 0.004);  // 4 号：前面一段已经吸了 1.8（第一批的场景）
            }
        }
    }
    // 最后 20 min 往快照里的当前值靠（第一批的装置总览场景是手写的，这样曲线末端和卡片上的读数一致）
    for (int c = 0; c < 8; ++c) {
        const ChannelSnap& ch = m_.snap.channels[(size_t)c];
        if (!ch.installed) continue;
        int N = std::min(n, 120);
        for (int k = 0; k < N; ++k) {
            size_t i = (size_t)(n - N + k);
            double w = double(k + 1) / N;
            w = w * w;
            if (ch.tempC) {
                float& v = t->temp[(size_t)c][i];
                v = (float)(v * (1 - w) + (*ch.tempC + rnd((uint32_t)c + 200, (uint32_t)i) * 0.12) * w);
            }
            if (ch.pressurePsi) {
                float& v = t->press[(size_t)c][i];
                v = (float)(v * (1 - w) + (*ch.pressurePsi * kPsi + rnd((uint32_t)c + 300, (uint32_t)i) * 0.003) * w);
            }
        }
    }
    // 快照里的当前值和趋势最后一点对上
    for (int c = 0; c < 8; ++c) {
        ChannelSnap& ch = m_.snap.channels[(size_t)c];
        if (!ch.installed || t->temp[(size_t)c].empty()) continue;
        float g = t->gas[(size_t)c].back();
        if (!std::isnan(g)) ch.gasConsumed = g;
    }
    m_.x.trend = t;
}

// ================================================================ 其余
void SimSource::build_pages() {
    PagesExt& x = m_.x;
    // ---- 设置 ----
    x.leakSettings.testPsi = 0.345 / kPsi;
    x.leakSettings.settleMin = 10; x.leakSettings.measureMin = 30;
    x.leakSettings.rateLimitPsiPerHr = 0.014 / kPsi;
    x.leakSettings.reminderDays = 7;
    x.leakSettings.lastTestUtc = day_at(8, 9, 30);
    x.approach = ApproachSettingsDto{5, 1.0, 1.0, 1.0, 0.2, 90};
    x.volume.vesselVolume = 10;
    const double V[4] = {12.84, 12.79, 12.91, 12.70};
    for (int i = 0; i < 4; ++i) {
        x.volume.effectiveVolume[(size_t)i] = V[i];
        x.volume.volumeCalibratedAt[(size_t)i] = day_at(i == 3 ? 7 : 8, 15, 10 + i * 20);
    }
    m_.settings.limits = LimitsDto{200, 3.447 / kPsi, 450, 8};
    m_.settings.pressFullScaleMPa = 4.137;
    // 第一批的场景里 2、4 号吸气量是示意值；这里按趋势重算
    c(1).gasConsumed.reset();
    // ---- 趋势 ----
    make_trend();
    c(2).gasRate = 0.011;

    // ---- 运行记录 ----
    auto rec = [&](const char* id, const char* name, const char* result, const char* rtext, int64_t start, double minutes,
                   std::vector<int> chs, std::vector<double> finals, double gas_start, double abort_min, const ProgramDoc& prog) {
        RunRecord r;
        r.id = id; r.programName = name; r.result = result; r.resultText = rtext; r.start = start;
        r.end = start + (int64_t)((abort_min > 0 ? abort_min : minutes) * 60000);
        r.plannedSec = (int64_t)(minutes * 60);
        r.channels = chs;
        r.program = prog;
        r.gasStart = start + (int64_t)(gas_start * 60000);
        if (!finals.empty()) {
            r.trend = run_trend(start, minutes, chs, finals, gas_start, abort_min, (uint32_t)start);
            double sum = 0, sq = 0;
            for (size_t k = 0; k < chs.size(); ++k) {
                RunChannelStat st;
                st.ch = chs[k];
                st.mmol = r.trend->gas[(size_t)chs[k] - 1].back();
                st.maxRate = finals[k] / (70 + 12 * k) * (abort_min > 0 ? 1.2 : 1.0);
                st.tempC = 80 + rnd((uint32_t)k, 3) * 0.15;
                sum += st.mmol; sq += st.mmol * st.mmol;
                r.perCh.push_back(st);
            }
            double n = (double)chs.size(), mean = sum / n;
            r.avgMmol = mean;
            r.rsdPct = mean > 0 ? std::sqrt(std::max(0.0, sq / n - mean * mean)) / mean * 100 : 0;
            r.tempDevC = 0.6; r.pressMPa = 2.0;
        }
        m_.x.runs.push_back(r);
        return &m_.x.runs.back();
    };
    // 正在跑的三组（和装置总览一致）
    for (int pidx = 0; pidx < 3; ++pidx) {
        std::vector<int> chs;
        int64_t st = 0;
        for (size_t i = 0; i < 8; ++i)
            if (plan_[i].prog == pidx) {
                chs.push_back((int)i + 1);
                if (!st || progStart_[i] < st) st = progStart_[i];
            }
        if (chs.empty()) continue;
        RunRecord* r = rec(("run-now-" + std::to_string(pidx)).c_str(), progs_[(size_t)pidx].name.c_str(), "running", "", st,
                           program_minutes(progs_[(size_t)pidx]).minutes, chs, {}, 0, 0, progs_[(size_t)pidx]);
        r->end = 0;
    }
    std::sort(m_.x.runs.begin(), m_.x.runs.end(), [](const RunRecord& a, const RunRecord& b) { return a.start > b.start; });
    ProgramDoc leakOnly = m_.x.templates[1].prog;
    rec("run-leak-1126", "只做气密 · 2 MPa", "leak", "8/8 通过", day_at(0, 7, 20), 40, {1, 2, 3, 4, 5, 6, 7, 8}, {}, 0, 0, leakOnly);
    RunRecord* ra = rec("run-1125-1305", "加氢筛选-A", "done", "4.20 mmol", day_at(1, 13, 5), 545, {1, 2, 3, 4, 5, 6},
                        {4.41, 4.30, 3.98, 4.47, 4.19, 3.87}, 62, 0, progs_[0]);
    ra->endReason = "各路都在吸气停止后自动结束";
    ra->stepMinutes = {36, 16, 15, 2, 45, 5, 440, 5, 1};
    RunRecord* rc = rec("run-1125-0910", "加氢筛选-C", "abort", "7 号超压", day_at(1, 9, 10), 630, {4, 5, 6, 7, 8},
                        {4.6, 3.6, 3.3, 3.9, 3.8}, 62, 138, progs_[2]);
    rc->abort = RunAbortInfo{7, "pressure", 2.62, 2.0, 2.60, 3.0, day_at(1, 11, 28, 4), "超压，程序自动停止",
                             "已停止加热、关闭进气阀、停搅拌 · 11:31:40 操作员确认"};
    for (auto& s : rc->perCh) if (s.ch == 7) { s.flagged = true; s.tempC = 83.6; s.maxRate = 0.092; }
    RunRecord* rv = rec("run-1124-1000", "吸气量验证", "warn", "2 号偏低", day_at(2, 10, 0), 140, {1, 2}, {3.1, 2.4}, 20, 0,
                        m_.x.templates[3].prog);
    rv->perCh[1].flagged = true;
    rv->endReason = "2 号吸气量比 1 号低 23 %";
    rec("run-1124-1430", "惰性气氛加热", "done", "完成", day_at(2, 14, 30), 290, {3, 4}, {}, 0, 0, m_.x.templates[2].prog);
    RunRecord* rx = rec("run-1121-0930", "催化剂 X · 曲线升温", "done", "3.86 mmol", day_at(5, 9, 30), 380, {1, 2, 3, 4},
                        {3.9, 3.8, 4.0, 3.75}, 140, 0, m_.x.library[3].prog);
    rx->endReason = "吸气停止后自动结束";

    // ---- 每日数据文件（本月有数据的日子 + 最近 90 天）----
    for (int d = 0; d < 40; ++d) {
        int64_t day0 = day_at(d, 0, 0);
        std::time_t tt = (std::time_t)(day0 / 1000);
        std::tm tm{};
        localtime_r(&tt, &tm);
        if (tm.tm_wday == 0 || tm.tm_wday == 6) continue;  // 周末没开机
        DayInfo di;
        di.day = ymd(day0);
        double mb = d == 0 ? 8.6 : 6 + std::fabs(rnd((uint32_t)d, 5)) * 14;
        HistoryFileInfo f;
        f.type = "telemetry"; f.name = "telemetry-" + di.day + ".csv"; f.day = di.day;
        f.from = day0; f.to = d == 0 ? now_ : day0 + 86399000LL;
        f.desc = "每 1.2 s 一行：8 路温度、压力、阀门、吸气量";
        if (d == 1) {
            f.to = day_at(1, 15, 42); f.bytes = (int64_t)(18.4 * 1048576);
            di.files.push_back(f);
            HistoryFileInfo f2 = f;
            f2.name = "telemetry-" + di.day + "-1.csv"; f2.from = day_at(1, 15, 42); f2.to = day0 + 86399000LL;
            f2.bytes = (int64_t)(2.6 * 1048576); f2.desc = "15:42 改了压力单位，表头变了，另起一个文件";
            di.files.push_back(f2);
        } else {
            f.bytes = (int64_t)(mb * 1048576);
            di.files.push_back(f);
        }
        HistoryFileInfo e;
        e.type = "events"; e.name = "events-" + di.day + ".jsonl"; e.day = di.day; e.from = day0; e.to = f.to;
        e.lines = d == 1 ? 214 : 60 + (int)(std::fabs(rnd((uint32_t)d, 9)) * 160);
        e.bytes = e.lines * 220; e.desc = "报警、阀门动作、操作";
        di.files.push_back(e);
        for (auto& fi : di.files) di.bytes += fi.bytes;
        for (auto& r : m_.x.runs)
            if (ymd(r.start) == di.day && r.result != "leak")
                di.runs.push_back({r.id, r.programName, r.result, r.start, r.end, r.channels});
        m_.x.days.push_back(di);
    }
    m_.x.usb = UsbExt{true, "KINGSTON", (int64_t)(28.4 * 1073741824.0), ""};

    // ---- 泄漏测试 ----
    for (int i = 1; i <= 8; ++i)
        if ((c(i).state == "idle" || c(i).state == "done") && !c(i).programOwned) x.leak.selectable.push_back(i);
    x.leak.history = {{day_at(8, 9, 30), "1–8 号", true, ""},
                      {day_at(15, 9, 10), "1–8 号 · 3 号超标", false, "3 号超标"},
                      {day_at(22, 9, 0), "1–8 号", true, ""}};

    // ---- PID（宇电 AI-8，三档）----
    x.pid.bands = {{0, "低温", 0, 60}, {1, "中温", 60, 150}, {2, "高温", 150, 300}};
    const double PP[8] = {12.0, 11.6, 12.4, 11.8, 12.2, 12.0, 13.0, 12.6};
    const double II[8] = {240, 236, 250, 244, 238, 240, 260, 252};
    const double DD[8] = {60, 58, 62, 60, 59, 60, 65, 63};
    for (int i = 0; i < 8; ++i) {
        PidChannelSnap& p = x.pid.channels[(size_t)i];
        p.id = i + 1;
        for (int b = 0; b < 3; ++b) {
            PidBandValueSnap v;
            v.index = b;
            v.P = PP[i] * (b == 0 ? 0.8 : b == 1 ? 1 : 1.25);
            v.I = II[i] * (b == 0 ? 1.2 : b == 1 ? 1 : 0.85);
            v.D = DD[i] * (b == 0 ? 1.2 : b == 1 ? 1 : 0.85);
            if (b == 1 && i < 6) { v.tunedSpC = 80; v.tunedAt = day_at(9, 10, 0); }
            p.bands.push_back(v);
        }
        ChannelSnap& ch = c(i + 1);
        bool heating = m_.chanset[(size_t)i].heatOn;
        ch.heatOutputPct = heating ? (i == 2 ? 78.0 : 30.0 + i) : 0.0;
    }
    // 3 号：中温档自整定（画布 Set-PID F：第 2 / 3 次振荡）
    c(3).autotuning = true;
    x.pid.channels[2].autotuning = true; x.pid.channels[2].tuneBand = 1; x.pid.channels[2].tuneSpC = 100;
    x.pid.channels[2].tuneCycle = 2; x.pid.channels[2].tuneCycles = 3; x.pid.channels[2].tuneLeftSec = 6 * 60;
    x.pid.channels[2].tuneFraction = 0.55;

    // ---- 容积标定：5 号第一次标定，第 3 / 6 次总管充气 ----
    CalibSnap cs;
    cs.running = true; cs.channel = 5; cs.stage = "second"; cs.waterMl = 2.0; cs.progress = 0.62;
    cs.phaseText = "加水后再做一遍 · 第 3 / 6 次 · 总管充气"; cs.startedAt = now_ - 31 * 60000; cs.startedBy = "触屏";
    cs.firstPassMl = 12.9;
    m_.snap.calib = cs;
    x.calib.pass = 2; x.calib.passes = 2; x.calib.cycle = 3; x.calib.cycles = 6;
    x.calib.stepMinutes = {12, 0, 0, 0};
    x.calib.readings = {{1, 0.000, 0.412, 0.412, 0.188}, {2, 0.188, 0.415, 0.415, 0.291}, {3, 0.291, 0.414, std::nullopt, std::nullopt}};

    // ---- 通讯与模块 ----
    auto mod = [&](const char* id, const char* name, const char* model, const char* detail, const char* port, int64_t polls, int64_t fails) {
        ModuleExt mx;
        mx.id = id; mx.name = name; mx.model = model; mx.detail = detail; mx.port = port; mx.polls = polls; mx.fails = fails;
        mx.lastOkAt = now_ - 1000;
        x.comm.modules.push_back(mx);
    };
    mod("temp", "温控", "宇电 AI-8", "8 路 PID · 1 拖 8", "/dev/ttyUSB0", 18432, 3);
    mod("pressure", "压力采集", "艾莫迅 JY-MODBUS-8AI", "8 路压力", "/dev/ttyUSB1", 18431, 0);
    mod("io", "阀门", "艾莫迅 JY-MODBUS-IO16R", "SV1–8 · 3 路气源 · 排空", "/dev/ttyUSB2", 9210, 0);
    mod("stir", "搅拌驱动", "雷赛 DM2C-RS432", "一台电机带 8 个釜", "/dev/ttyUSB3", 6120, 2);

    // ---- 网络 ----
    x.net.wifiOn = true; x.net.connectedSsid = "Lab-5G"; x.net.ip = "192.168.1.36"; x.net.strength = 4;
    x.net.nearby = {{"Lab-2.4G", true, 4, false}, {"HTLAB-Office", true, 3, false}, {"Guest", false, 3, false},
                    {"Instrument-AP", true, 2, false}, {"TP-LINK_8F21", true, 1, false}};

    // ---- 存储 / 关于 ----
    x.storage.diskTotal = (int64_t)(32.0 * 1073741824.0);
    x.storage.diskFree = (int64_t)(28.4 * 1073741824.0);
    x.storage.systemBytes = (int64_t)(2.6 * 1073741824.0);
    x.storage.dataBytes = (int64_t)(1.0 * 1073741824.0);
    x.storage.settingsPath = "~/.config/ParallelReactor/settings.json";
    x.storage.dataDir = "~/ParallelReactor/data";
    x.storage.todayTelemetryBytes = (int64_t)(18.6 * 1048576);
    x.storage.todayEventsBytes = 42 * 1024;
    x.storage.leakRuns = 36;
}

void SimSource::step_pages(double dt) {
    (void)dt;
    // 趋势：每 10 s 补一点
    if (m_.x.trend && now_ - m_.x.trend->t_end() >= m_.x.trend->stepMs) {
        auto t = std::make_shared<Trend>(*m_.x.trend);
        int i = t->size();
        for (int c = 0; c < 8; ++c) {
            const ChannelSnap& ch = m_.snap.channels[(size_t)c];
            auto push = [&](std::vector<float>& v, OptD x, double k) { v.push_back(x ? (float)(*x * k) : kNaN); };
            push(t->temp[(size_t)c], ch.installed ? ch.tempC : OptD{}, 1);
            push(t->press[(size_t)c], ch.installed ? ch.pressurePsi : OptD{}, kPsi);
            push(t->gas[(size_t)c], ch.gasConsumed, 1);
        }
        (void)i;
        m_.x.trend = t;
    }
    // 泄漏测试：测量阶段每 10 s 一点
    LeakSnap& L = m_.snap.leakTest;
    if (L.running && L.stage && *L.stage == "measure") {
        leakClock_ += dt / 60.0;
        double m = m_.x.leak.samples.empty() ? 0 : m_.x.leak.samples.back().min;
        if (leakClock_ - m >= 1.0 / 6) {
            LeakSample s;
            s.min = leakClock_;
            for (int ch = 5; ch <= 8; ++ch) {
                double rate = ch == 7 ? 0.031 : 0.002 + 0.001 * (ch - 5);  // MPa/h
                s.dpKPa[(size_t)ch - 1] = (float)(-rate * 1000 * s.min / 60 + std::sin(s.min * 1.3 + ch) * 0.35);
            }
            m_.x.leak.samples.push_back(s);
            L.stageLeftSec = std::max(0.0, 30 * 60 - leakClock_ * 60);
        }
    }
}

// ================================================================ 预览注入
void SimSource::inject_prog_done(int ch) {
    ChannelSnap& x = c(ch);
    const ProgramDoc& p = m_.x.programs[(size_t)ch - 1];
    int64_t start = now_ - (9 * 60 + 24) * 60000LL;
    if (x.program) { x.program->running = false; x.program->state = "completed"; x.program->stepIndex = (int)p.steps.size() - 1; }
    x.programOwned = false; x.state = "done"; x.valveOpen = false; x.autotuning = false;
    x.tempC = 41.6; x.pressurePsi = 0.02 / kPsi; x.gasConsumed = 4.12;
    m_.chanset[(size_t)ch - 1].heatOn = false;
    RunRecord r;
    r.id = "run-done-" + std::to_string(ch); r.programName = p.name; r.result = "done"; r.resultText = "4.12 mmol";
    r.start = start; r.end = now_; r.channels = {ch}; r.program = p; r.plannedSec = 9 * 3600 + 14 * 60;
    r.gasStart = start + 128 * 60000LL;
    r.endReason = "反应在吸气停止后自动结束";
    r.stepMinutes = {36, 16, 15, 2, 45, 5, 440, 5, 0};
    r.avgMmol = 4.12; r.tempDevC = 0.5; r.pressMPa = 2.0;
    r.perCh = {{ch, 4.12, 0.043, 80.0, false}};
    // 整次的温度 / 压力 / 吸气量（每分钟一点）
    auto t = std::make_shared<Trend>();
    t->t0 = start; t->stepMs = 60000;
    int n = 9 * 60 + 25;
    for (auto& v : t->temp) v.assign((size_t)n, kNaN);
    for (auto& v : t->press) v.assign((size_t)n, kNaN);
    for (auto& v : t->gas) v.assign((size_t)n, kNaN);
    auto& T = t->temp[(size_t)ch - 1]; auto& P = t->press[(size_t)ch - 1]; auto& G = t->gas[(size_t)ch - 1];
    double temp = 25;
    for (int i = 0; i < n; ++i) {
        double m = i, tt = m < 69 ? 25 : std::min(80.0, 25 + (m - 69) * 55 / 30.0);
        if (m > n - 12) tt = 41;
        temp += (tt - temp) * (m > n - 12 ? 0.08 : 0.25);
        T[(size_t)i] = (float)(temp + rnd(11, (uint32_t)i) * 0.25);
        double pp = m < 2 ? 3.0 * m / 2 : m < 36 ? 3.0 - m * 0.0004 : m < 38 ? 0.02 : m < 67 ? (std::fmod(m, 5.0) < 1.2 ? 0.5 : 0.03) : m < 119 ? 0.02 : m < n - 12 ? 2.0 : 0.02;
        P[(size_t)i] = (float)(pp + rnd(12, (uint32_t)i) * 0.008);
        G[(size_t)i] = (float)(m < 128 ? 0 : 4.12 * (1 - std::exp(-(m - 128) / 110.0)) + rnd(13, (uint32_t)i) * 0.01);
    }
    r.trend = t;
    m_.x.runs.insert(m_.x.runs.begin(), r);
    plan_[(size_t)ch - 1].prog = -1;
    m_.plans[(size_t)ch - 1] = PlanExt{};
}

void SimSource::inject_prog_aborted(int ch) {
    ChannelSnap& x = c(ch);
    const ProgramDoc& p = m_.x.programs[(size_t)ch - 1];
    int64_t start = progStart_[(size_t)ch - 1];
    int stepIdx = x.program ? x.program->stepIndex : 0;
    RunRecord r;
    r.id = "run-abort-" + std::to_string(ch); r.programName = p.name; r.result = "abort"; r.resultText = "操作员中止";
    r.start = start; r.end = now_; r.channels = {ch}; r.program = p;
    r.plannedSec = (int64_t)(program_minutes(p).minutes * 60);
    r.stepMinutes = {36, 16, 15, 2};
    r.abort = RunAbortInfo{ch, "manual", 0, 0, 0, 0, now_, "操作员手动中止", ""};
    // 已走过的部分来自趋势
    if (m_.x.trend) {
        auto t = std::make_shared<Trend>();
        const Trend& src = *m_.x.trend;
        t->t0 = start; t->stepMs = 60000;
        int n = (int)((now_ - start) / 60000) + 1;
        for (auto& v : t->temp) v.assign((size_t)n, kNaN);
        for (auto& v : t->press) v.assign((size_t)n, kNaN);
        for (auto& v : t->gas) v.assign((size_t)n, kNaN);
        for (int i = 0; i < n; ++i) {
            int64_t at = start + i * 60000LL;
            int k = (int)((at - src.t0) / src.stepMs);
            if (k < 0 || k >= src.size()) continue;
            t->temp[(size_t)ch - 1][(size_t)i] = src.temp[(size_t)ch - 1][(size_t)k];
            t->press[(size_t)ch - 1][(size_t)i] = src.press[(size_t)ch - 1][(size_t)k];
            float g = src.gas[(size_t)ch - 1][(size_t)k];
            t->gas[(size_t)ch - 1][(size_t)i] = std::isnan(g) ? 0.f : g;
        }
        r.trend = t;
    }
    m_.x.runs.insert(m_.x.runs.begin(), r);
    abort_program(ch);
    if (x.program) { x.program->stepIndex = stepIdx; x.program->state = "aborted"; }
    x.state = "ready";
    x.autotuning = false;
    plan_[(size_t)ch - 1].prog = -1;
    m_.plans[(size_t)ch - 1] = PlanExt{};
}

void SimSource::inject_leak(const char* phase) {
    std::string ph = phase;
    LeakSnap& L = m_.snap.leakTest;
    L = LeakSnap{};
    m_.x.leak.samples.clear();
    L.targetPsi = m_.x.leakSettings.testPsi;
    L.thresholdPsiH = m_.x.leakSettings.rateLimitPsiPerHr;
    L.startedBy = "触屏";
    double upto = ph == "run" ? 12 : 30;
    bool fail7 = ph != "pass";
    for (int i = 0; i <= (int)(upto * 6); ++i) {
        LeakSample s;
        s.min = i / 6.0;
        for (int ch = 5; ch <= 8; ++ch) {
            double rate = (ch == 7 && fail7) ? 0.031 : ch == 7 ? 0.004 : ch == 8 ? 0.002 : ch == 5 ? 0.003 : 0.005;
            double base = -rate * 1000 * s.min / 60.0;
            if (ch == 7 && fail7) base -= 1.1 * (1 - std::exp(-s.min / 2.0));
            s.dpKPa[(size_t)ch - 1] = (float)(base + std::sin(s.min * 1.1 + ch * 2) * 0.3 * (s.min > 0.5));
        }
        m_.x.leak.samples.push_back(s);
    }
    leakClock_ = upto;
    const double PR[4] = {0.343, 0.342, fail7 ? 0.329 : 0.343, 0.344};
    const double RT[4] = {0.003, 0.005, fail7 ? 0.031 : 0.004, 0.002};
    for (int ch = 5; ch <= 8; ++ch) {
        LeakChannelSnap lc;
        lc.id = ch;
        lc.pressurePsi = (ph == "run" ? (ch == 7 ? 0.339 : PR[ch - 5]) : PR[ch - 5]) / kPsi;
        lc.ratePsiH = RT[ch - 5] / kPsi;
        if (ph == "run") { lc.status = "testing"; lc.detail = ch == 7 ? "偏高" : "测量中"; }
        else lc.status = (ch == 7 && fail7) ? "fail" : "pass";
        L.channels.push_back(lc);
    }
    if (ph == "run") {
        L.running = true; L.stage = "measure"; L.phase = "测量"; L.progress = 0.55;
        L.stageLeftSec = 18 * 60; L.totalLeftSec = 19 * 60; L.startedAt = now_ - 26 * 60000;
    } else {
        L.done = true; L.failed = fail7; L.vented = true; L.startedAt = now_ - 45 * 60000; L.finishedAt = now_;
        L.runId = "leak-20261126-1347";
        if (fail7) L.failReason = "7 号压降超标";
        m_.x.leak.history.insert(m_.x.leak.history.begin(),
                                 LeakHistoryItem{now_, fail7 ? "5 6 7 8 号 · 7 号超标" : "5 6 7 8 号", !fail7, ""});
        if (!fail7) m_.x.leakSettings.lastTestUtc = now_;
    }
}

void SimSource::inject_stir_offline() {
    m_.snap.stir.online = false;
    for (auto& mx : m_.x.comm.modules)
        if (mx.id == "stir") {
            mx.online = false; mx.offlineSince = now_ - 17 * 60000; mx.lastOkAt = now_ - 17 * 60000 - 7000; mx.failStreak = 3;
        }
}

void SimSource::inject_heat_curve(int ch, int segs) {
    ProgramDoc& p = m_.x.programs[(size_t)ch - 1];
    for (auto& s : p.steps)
        if (s.type == "heat") {
            s.set_code("mode", "curve");
            if (segs <= 8) s.segments = {{40, 2, 5}, {55, 2, 5}, {70, 1.5, 10}, {85, 1.5, 10}, {100, 1, 15}, {115, 1, 15}, {130, 0.5, 20}, {140, 0.5, 30}};
            else {
                const double T[16] = {35, 45, 55, 62, 70, 78, 85, 92, 100, 108, 115, 122, 130, 136, 142, 150};
                const double R[16] = {2.0, 1.5, 1.0, 0.5, 2.0, 1.5, 1.0, 0.5, 2.0, 1.5, 1.0, 0.5, 2.0, 1.5, 1.0, 0.5};
                const double H[16] = {5, 5, 10, 10, 5, 5, 10, 10, 5, 5, 10, 5, 5, 5, 10, 10};
                s.segments.clear();
                for (int i = 0; i < 16; ++i) s.segments.push_back({T[i], R[i], H[i]});
            }
        }
}

// ================================================================ Actions（第二批）
void SimSource::put_program(int ch, const ProgramDoc& p) {
    m_.x.programs[(size_t)ch - 1] = p;
    ProgressExt& pg = m_.progress[(size_t)ch - 1];
    if (!c(ch).programOwned) {
        pg.programName = p.name; pg.editedNotSent = false;
        pg.stepNames.clear();
        for (auto& s : p.steps) pg.stepNames.push_back(zh_step_name(s.type));
    }
}
void SimSource::start_program(int ch) {
    ChannelSnap& x = c(ch);
    if (x.programOwned || !x.installed) return;
    progs_.push_back(m_.x.programs[(size_t)ch - 1]);
    plan_[(size_t)ch - 1] = PlanRow{(int)progs_.size() - 1, 0, -(now_ - base_) / 1000, -(now_ - base_) / 1000};
    progStart_[(size_t)ch - 1] = now_;
    ChannelProgramSnap pr;
    pr.running = true; pr.stepIndex = 0; pr.stepCount = (int)progs_.back().steps.size(); pr.state = "running";
    pr.startedBy = "local"; pr.startedAt = now_;
    x.program = pr;
    x.programOwned = true;
    x.state = "pressing";
    ProgressExt& pg = m_.progress[(size_t)ch - 1];
    pg.programName = progs_.back().name; pg.editedNotSent = false;
    pg.stepNames.clear();
    for (auto& s : progs_.back().steps) pg.stepNames.push_back(zh_step_name(s.type));
    rebuild_plans();
}
void SimSource::lib_save(bool tpl, const ProgramDoc& p) {
    auto& list = tpl ? m_.x.templates : m_.x.library;
    for (auto& e : list)
        if (!p.id.empty() && e.prog.id == p.id) {
            e.prog = p;
            e.prog.revision++;
            e.prog.modifiedMs = now_;
            return;
        }
    LibEntry e;
    e.prog = p;
    e.prog.id = (tpl ? "U" : "P") + std::to_string(now_ % 100000000000LL);
    e.prog.revision = 1;
    e.prog.modifiedMs = now_;
    e.prog.by = "触屏";
    list.insert(list.begin(), e);
}
void SimSource::lib_delete(bool tpl, const std::string& id, int64_t) {
    auto& list = tpl ? m_.x.templates : m_.x.library;
    list.erase(std::remove_if(list.begin(), list.end(), [&](const LibEntry& e) { return e.prog.id == id && !e.prog.builtIn; }), list.end());
}
void SimSource::lib_import_usb() {}
void SimSource::leak_start(const std::vector<int>& chs) {
    LeakSnap& L = m_.snap.leakTest;
    L = LeakSnap{};
    L.running = true; L.stage = "fill"; L.phase = "充压"; L.progress = 0.02; L.startedAt = now_; L.startedBy = "触屏";
    L.targetPsi = m_.x.leakSettings.testPsi; L.thresholdPsiH = m_.x.leakSettings.rateLimitPsiPerHr;
    L.stageLeftSec = 180; L.totalLeftSec = 45 * 60;
    for (int ch : chs) {
        LeakChannelSnap lc;
        lc.id = ch; lc.status = "testing"; lc.detail = "充压中"; lc.pressurePsi = c(ch).pressurePsi;
        L.channels.push_back(lc);
    }
    m_.x.leak.samples.clear();
    leakClock_ = 0;
}
void SimSource::leak_abort() {
    LeakSnap& L = m_.snap.leakTest;
    L.running = false; L.aborted = true; L.done = true; L.vented = true; L.finishedAt = now_;
}
void SimSource::run_note(const std::string& id, const std::string& note) {
    for (auto& r : m_.x.runs)
        if (r.id == id) r.note = note;
}
void SimSource::export_usb(const ExportReq& r) {
    m_.x.usb.lastExport = r.runId.empty() ? (r.day.empty() ? r.file : r.day) : r.runId;
}
void SimSource::patch_setting(const std::string& k, double v) {
    PagesExt& x = m_.x;
    SettingsDto& s = m_.settings;
    if (k == "alarms.overTempC") s.alarms.overTempC = v;
    else if (k == "alarms.overPressurePsi") s.alarms.overPressurePsi = v;
    else if (k == "alarms.pressDeviationPsi") s.alarms.pressDeviationPsi = v;
    else if (k == "alarms.heatTimeoutMin") s.alarms.heatTimeoutMin = v;
    else if (k == "alarms.stopHeatOnOverPressure") s.alarms.stopHeatOnOverPressure = v != 0;
    else if (k == "limits.tempSpMaxC") s.limits.tempSpMaxC = v;
    else if (k == "limits.pressureSpMaxPsi") s.limits.pressureSpMaxPsi = v;
    else if (k == "limits.rpmMax") s.limits.rpmMax = v;
    else if (k == "limits.volumeMaxMl") s.limits.volumeMaxMl = v;
    else if (k == "leakTest.testPsi") x.leakSettings.testPsi = v;
    else if (k == "leakTest.settleMin") x.leakSettings.settleMin = v;
    else if (k == "leakTest.measureMin") x.leakSettings.measureMin = v;
    else if (k == "leakTest.rateLimitPsiPerHr") x.leakSettings.rateLimitPsiPerHr = v;
    else if (k == "leakTest.reminderDays") x.leakSettings.reminderDays = v;
    else if (k == "approach.deltaC") x.approach.deltaC = v;
    else if (k == "approach.fastRateCPerMin") x.approach.fastRateCPerMin = v;
    else if (k == "approach.rateCPerMin") x.approach.rateCPerMin = v;
    else if (k == "approach.settleBandC") x.approach.settleBandC = v;
    else if (k == "approach.settleSec") x.approach.settleSec = v;
    else if (k == "stir.peakCurrentA") x.stir.peakCurrentA = v;
    else if (k == "stir.standbyPct") x.stir.standbyPct = v;
    else if (k == "ui.gasWaitMaxMin") x.prefs.gasWaitMaxMin = v;
    else if (k == "ui.retentionDays") x.prefs.retentionDays = v;
    else if (k == "ui.preflightChecklist") x.prefs.preflightChecklist = v != 0;
    s.revision++;
}
void SimSource::patch_setting_str(const std::string& k, const std::string& v) {
    UiPrefsExt& p = m_.x.prefs;
    if (k == "ui.pressureUnit") p.pressureUnit = v;
    else if (k == "ui.tempUnit") p.tempUnit = v;
    else if (k == "ui.theme") p.theme = v;
    else if (k == "ui.keyboard") p.keyboard = v;
    m_.settings.revision++;
}
void SimSource::set_pid(int ch, int band, double p, double i, double d) {
    for (auto& b : m_.x.pid.channels[(size_t)ch - 1].bands)
        if (b.index == band) { b.P = p; b.I = i; b.D = d; b.manualEditedAt = now_; }
}
void SimSource::set_pid_bands(double lo, double hi) {
    auto& B = m_.x.pid.bands;
    if (B.size() == 3) { B[0].hiC = lo; B[1].loC = lo; B[1].hiC = hi; B[2].loC = hi; }
}
void SimSource::autotune(int ch, int band, double sp) {
    PidChannelSnap& p = m_.x.pid.channels[(size_t)ch - 1];
    p.autotuning = true; p.tuneBand = band; p.tuneSpC = sp; p.tuneCycle = 1; p.tuneFraction = 0.05; p.tuneLeftSec = 25 * 60;
    c(ch).autotuning = true;
}
void SimSource::autotune_cancel(int ch) {
    PidChannelSnap& p = m_.x.pid.channels[(size_t)ch - 1];
    p.autotuning = false; p.tuneBand.reset(); p.tuneSpC.reset();
    c(ch).autotuning = false;
}
void SimSource::calib_start(int ch, double w) {
    CalibSnap cs;
    cs.running = true; cs.channel = ch; cs.stage = "first"; cs.waterMl = w; cs.startedAt = now_; cs.startedBy = "触屏";
    cs.phaseText = "空釜做一遍";
    m_.snap.calib = cs;
    m_.x.calib = CalibExt{};
    m_.x.calib.passes = m_.x.volume.manifoldVolume ? 1 : 2;
}
void SimSource::calib_continue() {
    if (m_.snap.calib && m_.snap.calib->stage == "waitWater") { m_.snap.calib->stage = "second"; m_.snap.calib->waitingOperator = false; }
}
void SimSource::calib_abort() {
    if (m_.snap.calib) { m_.snap.calib->running = false; m_.snap.calib->failed = true; m_.snap.calib->resultText = "操作员中止"; }
}
void SimSource::wifi_enable(bool on) {
    m_.x.net.wifiOn = on;
    if (!on) m_.x.net.connectedSsid.clear();
}
void SimSource::wifi_scan() {}
void SimSource::wifi_connect(const std::string& ssid, const std::string&) {
    NetworkExt& n = m_.x.net;
    if (!n.connectedSsid.empty()) n.nearby.insert(n.nearby.begin(), WifiNet{n.connectedSsid, true, n.strength, true});
    for (size_t i = 0; i < n.nearby.size(); ++i)
        if (n.nearby[i].ssid == ssid) { n.strength = n.nearby[i].strength; n.nearby.erase(n.nearby.begin() + (long)i); break; }
    n.connectedSsid = ssid;
}
void SimSource::wifi_forget(const std::string& ssid) {
    if (m_.x.net.connectedSsid == ssid) wifi_disconnect();
}
void SimSource::wifi_disconnect() {
    NetworkExt& n = m_.x.net;
    if (!n.connectedSsid.empty()) n.nearby.insert(n.nearby.begin(), WifiNet{n.connectedSsid, true, n.strength, true});
    n.connectedSsid.clear();
}
void SimSource::set_ip_mode(bool dhcp) { m_.x.net.dhcp = dhcp; }
void SimSource::comm_test(const std::string&) {}
void SimSource::comm_reconnect(const std::string& id) {
    for (auto& mx : m_.x.comm.modules)
        if (mx.id == id) { mx.online = true; mx.offlineSince.reset(); mx.failStreak = 0; }
    if (id == "stir") m_.snap.stir.online = true;
}
void SimSource::quit_app() {}
void SimSource::control_grant(const std::string& id) {
    if (m_.controlPending && m_.controlPending->requestId == id) {
        m_.controlPending.reset();
        m_.snap.mode.control = "remote";
        m_.snap.mode.holder = std::string("实验室-PC2");
    }
}
void SimSource::control_deny(const std::string& id) {
    if (m_.controlPending && m_.controlPending->requestId == id) m_.controlPending.reset();
}
void SimSource::control_take_back() {
    m_.snap.mode.control = "local";
    m_.snap.mode.holder.reset();
}
void SimSource::inject_control_request() {
    ControlPending p;
    p.requestId = "req-sim-1";
    p.clientName = "实验室-PC2";
    p.tokenName = "lab-pc";
    p.expiresAt = now_ + 60 * 1000;
    m_.controlPending = p;
}

}  // namespace reactor::hmi::model
