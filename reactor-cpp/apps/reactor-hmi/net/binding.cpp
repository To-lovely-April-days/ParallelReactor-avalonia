#include <cctype>
#include "binding.hpp"

#include "wire.hpp"

#include <algorithm>
#include <cmath>
#include <limits>
#include <cmath>
#include <cstdio>
#include <ctime>
#include <set>
#include <array>
#include <cstdint>
#include <map>
#include <memory>
#include <optional>
#include <string>
#include <vector>

namespace reactor::hmi::net {

// ============================================================ 小工具
static const json& sub(const json& j, const char* k) {
    static const json kNull;
    if (j.is_object()) {
        auto it = j.find(k);
        if (it != j.end()) return *it;
    }
    return kNull;
}

std::optional<int64_t> j_time(const json& j, const char* k) {
    const json& v = sub(j, k);
    if (v.is_string()) return parse_iso_ms(v.get<std::string>());
    if (v.is_number()) return static_cast<int64_t>(v.get<double>());
    return std::nullopt;
}
double j_num(const json& j, const char* k, double def) {
    const json& v = sub(j, k);
    if (v.is_number()) return v.get<double>();
    if (v.is_boolean()) return v.get<bool>() ? 1 : 0;
    return def;
}
std::optional<double> j_optnum(const json& j, const char* k) {
    const json& v = sub(j, k);
    if (v.is_number()) return v.get<double>();
    return std::nullopt;
}
int64_t j_int(const json& j, const char* k, int64_t def) {
    const json& v = sub(j, k);
    if (v.is_number_integer()) return v.get<int64_t>();
    if (v.is_number()) return static_cast<int64_t>(std::llround(v.get<double>()));
    return def;
}
bool j_bool(const json& j, const char* k, bool def) {
    const json& v = sub(j, k);
    if (v.is_boolean()) return v.get<bool>();
    if (v.is_number()) return v.get<double>() != 0;
    return def;
}
std::string j_str(const json& j, const char* k, const std::string& def) {
    const json& v = sub(j, k);
    if (v.is_string()) return v.get<std::string>();
    return def;
}
std::optional<std::string> j_optstr(const json& j, const char* k) {
    const json& v = sub(j, k);
    if (v.is_string()) return v.get<std::string>();
    return std::nullopt;
}
static std::optional<int> j_optint(const json& j, const char* k) {
    const json& v = sub(j, k);
    if (v.is_number()) return static_cast<int>(std::llround(v.get<double>()));
    return std::nullopt;
}
static std::vector<int> j_ints(const json& j, const char* k) {
    std::vector<int> o;
    const json& v = sub(j, k);
    if (v.is_array())
        for (auto& e : v)
            if (e.is_number()) o.push_back(static_cast<int>(std::llround(e.get<double>())));
    return o;
}
static std::vector<std::string> j_strs(const json& j, const char* k) {
    std::vector<std::string> o;
    const json& v = sub(j, k);
    if (v.is_array())
        for (auto& e : v)
            if (e.is_string()) o.push_back(e.get<std::string>());
    return o;
}
static float j_f(const json& e) { return e.is_number() ? static_cast<float>(e.get<double>()) : NAN; }

// ============================================================ 快照
m::ChannelSnap parse_channel(const json& j) {
    m::ChannelSnap c;
    c.id = static_cast<int>(j_int(j, "id", 0));
    c.installed = j_bool(j, "installed", true);
    c.state = j_str(j, "state", "idle");
    c.alarmCause = j_optstr(j, "alarmCause");
    c.overTempTripped = j_bool(j, "overTempTripped");
    c.overPressureTripped = j_bool(j, "overPressureTripped");
    c.startedBy = j_optstr(j, "startedBy");
    c.tempC = j_optnum(j, "tempC");
    c.tempSpC = j_num(j, "tempSpC", 25);
    c.tempSpDeviceC = j_optnum(j, "tempSpDeviceC");
    c.heatOutputPct = j_optnum(j, "heatOutputPct");
    c.tempOnline = j_bool(j, "tempOnline", true);
    c.autotuning = j_bool(j, "autotuning");
    c.atMode = j_optint(j, "atMode");
    c.spMode = j_str(j, "spMode", "fixed");
    const json& ap = sub(j, "approach");
    c.approach.phase = j_str(ap, "phase", "off");
    c.approach.targetC = j_optnum(ap, "targetC");
    c.approach.currentSpC = j_optnum(ap, "currentSpC");
    c.approach.statusText = j_str(ap, "statusText");
    const json& pf = sub(j, "profile");
    if (pf.is_object()) {
        m::ProfileSnap p;
        p.running = j_bool(pf, "running");
        p.segCount = static_cast<int>(j_int(pf, "segCount"));
        for (auto& s : sub(pf, "segments"))
            p.segments.push_back({static_cast<int>(j_int(s, "index")), j_num(s, "tempC"), j_num(s, "minutes"), j_bool(s, "enabled", true),
                                  j_bool(s, "active")});
        p.elapsedMin = j_num(pf, "elapsedMin");
        p.totalMinutes = j_num(pf, "totalMinutes");
        p.complete = j_bool(pf, "complete");
        p.statusText = j_str(pf, "statusText");
        c.profile = std::move(p);
    }
    c.pressurePsi = j_optnum(j, "pressurePsi");
    c.pressureRaw = j_optint(j, "pressureRaw");
    c.pressureSpPsi = j_num(j, "pressureSpPsi");
    c.gasConsumed = j_optnum(j, "gasConsumed");
    c.valveOpen = j_bool(j, "valveOpen");
    c.programOwned = j_bool(j, "programOwned");
    if (j.contains("hasActivity") && j["hasActivity"].is_boolean()) c.hasActivity = j["hasActivity"].get<bool>();
    const json& pg = sub(j, "program");
    if (pg.is_object()) {
        m::ChannelProgramSnap p;
        p.running = j_bool(pg, "running");
        p.stepIndex = static_cast<int>(j_int(pg, "stepIndex"));
        p.stepCount = static_cast<int>(j_int(pg, "stepCount"));
        p.state = j_str(pg, "state");
        p.message = j_str(pg, "message");
        p.startedBy = j_optstr(pg, "startedBy");
        p.startedAt = j_time(pg, "startedAt");
        p.waitingGas = j_bool(pg, "waitingGas");
        p.stepElapsedMin = j_optnum(pg, "stepElapsedMin");
        c.program = std::move(p);
    }
    c.gasRate = j_optnum(j, "gasRate");
    c.solutionVolume = j_optnum(j, "solutionVolume");
    // 界面里「停用」= 没装或者 idle（接口契约 §2.1 的说明）：enable / disable 走 POST /channels/{id}/enable|disable
    if (c.state == "idle") c.installed = false;
    return c;
}

void pad_channels(std::vector<m::ChannelSnap>& v) {
    std::vector<m::ChannelSnap> o(8);
    for (int i = 0; i < 8; ++i) {
        o[static_cast<size_t>(i)].id = i + 1;
        o[static_cast<size_t>(i)].installed = false;
        o[static_cast<size_t>(i)].state = "idle";
    }
    for (auto& c : v)
        if (c.id >= 1 && c.id <= 8) o[static_cast<size_t>(c.id - 1)] = c;
    v.swap(o);
}

bool parse_snapshot(const json& j, m::TelemetrySnapshot& s, SnapExt& ext) {
    if (!j.is_object() || !j.contains("channels")) return false;
    s.type = j_str(j, "type", "telemetry");
    s.seq = j_int(j, "seq");
    s.ts = j_time(j, "ts").value_or(wall_ms());
    s.simulated = j_bool(j, "simulated");
    s.eventSeqApplied = j_int(j, "eventSeqApplied");
    const json& md = sub(j, "mode");
    s.mode.control = j_str(md, "control", "local");
    s.mode.holder = j_optstr(md, "holder");
    const json& es = sub(j, "estop");
    s.estop.active = j_bool(es, "active");
    const json& cm = sub(j, "comm");
    s.comm.temp = j_bool(cm, "temp", true);
    s.comm.pressure = j_bool(cm, "pressure", true);
    s.comm.stir = j_bool(cm, "stir", true);
    s.comm.io = j_bool(cm, "io", true);
    s.channels.clear();
    ext.hasChanset = false;
    for (auto& c : sub(j, "channels")) {
        if (!c.is_object()) continue;
        m::ChannelSnap ch = parse_channel(c);
        s.channels.push_back(ch);
        if (ch.id >= 1 && ch.id <= 8) {
            auto& cs = ext.chanset[static_cast<size_t>(ch.id - 1)];
            if (c.contains("bandC") || c.contains("antiOvershoot") || c.contains("heatOn")) ext.hasChanset = true;
            cs.band = j_num(c, "bandC", cs.band);
            cs.antiOvershoot = j_bool(c, "antiOvershoot", cs.antiOvershoot);
            cs.heatOn = j_bool(c, "heatOn", ch.state == "heating");
            cs.curve.clear();
            if (ch.profile)
                for (auto& g : ch.profile->segments)
                    if (g.enabled) cs.curve.push_back({g.tempC, g.minutes});
        }
    }
    pad_channels(s.channels);
    const json& st = sub(j, "stir");
    s.stir.on = j_bool(st, "on");
    s.stir.rpmSp = static_cast<int>(j_int(st, "rpmSp"));
    s.stir.online = j_bool(st, "online", true);
    s.stir.locked = j_bool(st, "locked");
    s.stir.motorCurrentA = j_optnum(st, "motorCurrentA");
    s.stir.busVoltageV = j_optnum(st, "busVoltageV");
    s.stir.fault = j_optstr(st, "fault");
    s.stir.alarmCode = j_optint(st, "alarmCode");
    s.stir.rampMsPer1000Rpm = static_cast<int>(j_int(st, "rampMsPer1000Rpm"));
    const json& g = sub(j, "gas");
    s.gas.inlets.clear();
    for (auto& i : sub(g, "inlets")) {
        // 后台发的是 inert / A / B（大写），界面里一律按小写比对
        std::string id = j_str(i, "id");
        for (auto& ch : id) ch = static_cast<char>(std::tolower(static_cast<unsigned char>(ch)));
        s.gas.inlets.push_back({id, j_bool(i, "open")});
    }
    s.gas.ventOpen = j_bool(g, "ventOpen");
    s.gas.manualValve = j_bool(g, "manualValve");
    s.gas.task = j_optstr(g, "task");
    s.gas.owner = j_optstr(g, "owner");
    const json& ps = sub(j, "programs");
    s.programs.running = static_cast<int>(j_int(ps, "running"));
    s.programs.channels = j_ints(ps, "channels");
    s.programs.gasHolder = j_optint(ps, "gasHolder");
    s.programs.waitingGas = j_ints(ps, "waitingGas");
    s.programs.summary = j_str(ps, "summary");
    const json& lk = sub(j, "leakTest");
    auto& L = s.leakTest;
    L = m::LeakSnap{};
    L.running = j_bool(lk, "running");
    L.progress = j_num(lk, "progress");
    L.phase = j_optstr(lk, "phase");
    L.runId = j_optstr(lk, "runId");
    L.done = j_bool(lk, "done");
    L.failed = j_bool(lk, "failed");
    L.stage = j_optstr(lk, "stage");
    L.stageLeftSec = j_optnum(lk, "stageLeftSec");
    L.totalLeftSec = j_optnum(lk, "totalLeftSec");
    L.startedAt = j_time(lk, "startedAt");
    L.startedBy = j_optstr(lk, "startedBy");
    L.targetPsi = j_optnum(lk, "targetPsi");
    L.thresholdPsiH = j_optnum(lk, "thresholdPsiH");
    // 后台（同 C#）8 路都给，没参加这次测试的是 status = untested：界面只要参加测试的那几路
    for (auto& c : sub(lk, "channels"))
        if (j_str(c, "status") != "untested")
            L.channels.push_back({static_cast<int>(j_int(c, "id")), j_str(c, "status"), j_optnum(c, "pressurePsi"), j_optnum(c, "ratePsiH"),
                              j_str(c, "detail")});
    if (sub(lk, "vented").is_boolean()) L.vented = sub(lk, "vented").get<bool>();
    L.aborted = j_bool(lk, "aborted");
    L.failReason = j_optstr(lk, "failReason");
    L.finishedAt = j_time(lk, "finishedAt");
    const json& se = sub(j, "session");
    s.session.active = j_bool(se, "active");
    s.session.id = j_optstr(se, "id");
    s.session.testId = j_optstr(se, "testId");
    s.session.file = j_optstr(se, "file");
    const json& ca = sub(j, "calib");
    s.calib.reset();
    if (ca.is_object()) {
        m::CalibSnap c;
        c.running = j_bool(ca, "running");
        c.channel = j_optint(ca, "channel");
        c.stage = j_str(ca, "stage");
        c.waitingOperator = j_bool(ca, "waitingOperator");
        c.waterMl = j_optnum(ca, "waterMl");
        c.firstPassMl = j_optnum(ca, "firstPassMl");
        c.resultMl = j_optnum(ca, "resultMl");
        c.progress = j_num(ca, "progress");
        c.phaseText = j_optstr(ca, "phaseText");
        c.startedAt = j_time(ca, "startedAt");
        c.waitingSince = j_time(ca, "waitingSince");
        c.startedBy = j_optstr(ca, "startedBy");
        c.done = j_bool(ca, "done");
        c.failed = j_bool(ca, "failed");
        c.resultText = j_optstr(ca, "resultText");
        s.calib = std::move(c);
    }
    // ---- 扩展（接口契约 §2.1）----
    const json& sy = sub(j, "system");
    ext.languageCode = j_str(sy, "languageCode");
    ext.bootAt = j_time(sy, "bootAt").value_or(0);
    const json& rv = sub(j, "revs");
    ext.hasRevs = rv.is_object();
    ext.revs.clear();
    if (ext.hasRevs)
        for (auto it = rv.begin(); it != rv.end(); ++it)
            if (it->is_number()) ext.revs[it.key()] = it->get<int64_t>();
    return true;
}

// ============================================================ 报警 / 事件
m::AlarmItem parse_alarm(const json& j) {
    m::AlarmItem a;
    a.dto.id = j_str(j, "id");
    a.dto.level = j_str(j, "level", "warn");
    a.dto.rv = j_str(j, "rv");
    a.dto.channel = j_optint(j, "channel");
    a.dto.title = j_str(j, "title");
    a.dto.message = j_str(j, "message");
    a.dto.raisedAt = j_time(j, "raisedAt").value_or(0);
    a.dto.state = j_str(j, "state", "active");
    a.dto.clearedAt = j_time(j, "clearedAt");
    auto& e = a.ext;
    // tier 缺省按 level（err → 二级弹窗，warn → 三级气泡，info → 只进列表）；联锁类必须由后端给 tier = 1
    int def = a.dto.level == "err" ? 2 : a.dto.level == "warn" ? 3 : 0;
    e.tier = static_cast<int>(j_int(j, "tier", def));
    e.source = j_str(j, "source", a.dto.channel ? "channel" : "system");
    e.quantity = j_str(j, "quantity");
    bool pressure = e.quantity == "pressure";
    auto conv = [&](const char* k) -> std::optional<double> {
        auto v = j_optnum(j, k);
        if (v && pressure) *v = psi2mpa(*v);
        return v;
    };
    e.value = conv("value");
    e.setpoint = conv("setpoint");
    e.limit = conv("limit");
    e.rangeLo = conv("rangeLo");
    e.rangeHi = conv("rangeHi");
    e.did = j_strs(j, "did");
    e.next = j_strs(j, "next");
    e.context = j_str(j, "context");
    e.downSince = j_time(j, "downSince").value_or(0);
    e.hint = j_str(j, "hint");
    e.highlight = j_str(j, "highlight");
    return a;
}

std::vector<m::AlarmItem> parse_alarms(const json& j) {
    std::vector<m::AlarmItem> v;
    const json& items = j.is_array() ? j : sub(j, "items");
    for (auto& a : items)
        if (a.is_object()) v.push_back(parse_alarm(a));
    std::stable_sort(v.begin(), v.end(), [](const m::AlarmItem& a, const m::AlarmItem& b) { return a.dto.raisedAt > b.dto.raisedAt; });
    return v;
}

m::EventRecord parse_event(const json& j) {
    m::EventRecord e;
    e.seq = j_int(j, "seq");
    e.seqEpoch = static_cast<int>(j_int(j, "seqEpoch"));
    e.ts = j_time(j, "ts").value_or(0);
    e.tsLocal = j_str(j, "tsLocal");
    e.level = j_str(j, "level", "info");
    e.kind = j_str(j, "kind");
    e.channel = j_optint(j, "channel");
    e.source = j_str(j, "source");
    e.origin = j_str(j, "origin");
    e.client = j_optstr(j, "client");
    e.opId = j_optstr(j, "opId");
    e.title = j_str(j, "title");
    e.message = j_str(j, "message");
    return e;
}

std::vector<m::EventRecord> parse_events_page(const json& j, int* epoch, int64_t* head, bool* more) {
    std::vector<m::EventRecord> v;
    if (epoch) *epoch = static_cast<int>(j_int(j, "seqEpoch"));
    if (head) *head = j_int(j, "eventSeqHead");
    if (more) *more = j_bool(j, "more") || j_bool(j, "hasMore");
    for (auto& e : sub(j, "items"))
        if (e.is_object()) v.push_back(parse_event(e));
    return v;
}

// ============================================================ 设置
void parse_settings(const json& j, m::SettingsDto& s, m::PagesExt& x) {
    const json& li = sub(j, "limits");
    s.limits.tempSpMaxC = j_num(li, "tempSpMaxC", s.limits.tempSpMaxC);
    s.limits.pressureSpMaxPsi = j_num(li, "pressureSpMaxPsi", s.limits.pressureSpMaxPsi);
    s.limits.rpmMax = j_num(li, "rpmMax", s.limits.rpmMax);
    s.limits.volumeMaxMl = j_num(li, "volumeMaxMl", s.limits.volumeMaxMl);
    const json& al = sub(j, "alarms");
    s.alarms.overPressurePsi = j_num(al, "overPressurePsi", s.alarms.overPressurePsi);
    s.alarms.overTempC = j_num(al, "overTempC", s.alarms.overTempC);
    s.alarms.stopHeatOnOverPressure = j_bool(al, "stopHeatOnOverPressure", s.alarms.stopHeatOnOverPressure);
    s.alarms.heatTimeoutMin = j_num(al, "heatTimeoutMin", s.alarms.heatTimeoutMin);
    s.alarms.pressDeviationPsi = j_num(al, "pressDeviationPsi", s.alarms.pressDeviationPsi);
    s.pressFullScaleMPa = j_num(sub(j, "pressure"), "fullScaleMPa", s.pressFullScaleMPa);
    s.revision = j_int(j, "revision", s.revision);
    const json& lt = sub(j, "leakTest");
    auto& L = x.leakSettings;
    L.testPsi = j_num(lt, "testPsi", L.testPsi);
    L.settleMin = j_num(lt, "settleMin", L.settleMin);
    L.measureMin = j_num(lt, "measureMin", L.measureMin);
    L.rateLimitPsiPerHr = j_num(lt, "rateLimitPsiPerHr", L.rateLimitPsiPerHr);
    L.reminderDays = j_num(lt, "reminderDays", L.reminderDays);
    L.lastTestUtc = j_time(lt, "lastTestUtc");
    const json& ap = sub(j, "approach");
    auto& A = x.approach;
    A.deltaC = j_num(ap, "deltaC", A.deltaC);
    A.rateCPerMin = j_num(ap, "rateCPerMin", A.rateCPerMin);
    A.fastRateCPerMin = j_num(ap, "fastRateCPerMin", A.fastRateCPerMin);
    A.finalZoneC = j_num(ap, "finalZoneC", A.finalZoneC);
    A.settleBandC = j_num(ap, "settleBandC", A.settleBandC);
    A.settleSec = j_num(ap, "settleSec", A.settleSec);
    const json& st = sub(j, "stir");
    auto& S = x.stir;
    S.peakCurrentA = j_num(st, "peakCurrentA", S.peakCurrentA);
    S.standbyPct = j_num(st, "standbyPct", S.standbyPct);
    S.rampMsPer1000Rpm = j_num(st, "rampMsPer1000Rpm", S.rampMsPer1000Rpm);
    S.microstep = j_num(st, "microstep", S.microstep);
    S.stirGearRatio = j_num(st, "stirGearRatio", S.stirGearRatio);
    s.stirGearRatio = S.stirGearRatio;
    const json& vo = sub(j, "volume");
    if (vo.is_object()) {
        auto& V = x.volume;
        V.vesselVolume = j_num(vo, "vesselVolume", V.vesselVolume);
        V.gasLineVolume = j_num(vo, "gasLineVolume", V.gasLineVolume);
        V.manifoldVolume = j_optnum(vo, "manifoldVolume");
        const json& ev = sub(vo, "effectiveVolume");
        const json& ca = sub(vo, "volumeCalibratedAt");
        for (size_t i = 0; i < 8; ++i) {
            V.effectiveVolume[i] = ev.is_array() && i < ev.size() && ev[i].is_number() ? std::optional<double>(ev[i].get<double>()) : std::nullopt;
            V.volumeCalibratedAt[i] = std::nullopt;
            if (ca.is_array() && i < ca.size() && ca[i].is_string()) V.volumeCalibratedAt[i] = parse_iso_ms(ca[i].get<std::string>());
        }
    }
    // 扩展：ui.*（触屏自己的偏好）、program.gasWaitMaxMin、storage.retentionDays
    const json& ui = sub(j, "ui");
    auto& P = x.prefs;
    P.pressureUnit = j_str(ui, "pressureUnit", P.pressureUnit);
    P.tempUnit = j_str(ui, "tempUnit", P.tempUnit);
    P.theme = j_str(ui, "theme", P.theme);
    P.keyboard = j_str(ui, "keyboard", P.keyboard);
    P.preflightChecklist = j_bool(ui, "preflightChecklist", P.preflightChecklist);
    P.gasWaitMaxMin = j_num(sub(j, "program"), "gasWaitMaxMin", P.gasWaitMaxMin);
    P.retentionDays = j_num(sub(j, "storage"), "retentionDays", P.retentionDays);
    x.retentionDaysShown = static_cast<int>(P.retentionDays);
}

std::string wire_setting_key(const std::string& k) {
    if (k == "ui.gasWaitMaxMin") return "program.gasWaitMaxMin";
    if (k == "ui.retentionDays") return "storage.retentionDays";
    return k;
}
bool setting_is_bool(const std::string& k) { return k == "alarms.stopHeatOnOverPressure" || k == "ui.preflightChecklist"; }
bool setting_is_string(const std::string& k) {
    return k == "ui.pressureUnit" || k == "ui.tempUnit" || k == "ui.theme" || k == "ui.keyboard";
}

// ============================================================ progress
std::string fmt_step_target(const json& t) {
    if (t.is_string()) return t.get<std::string>();
    if (!t.is_object()) return "";
    std::string q = j_str(t, "quantity");
    auto v = j_optnum(t, "value");
    if (!v) return "";
    char b[64];
    if (q == "temp") std::snprintf(b, sizeof b, "%.0f °C", *v);
    else if (q == "pressure") std::snprintf(b, sizeof b, "%.3f MPa", psi2mpa(*v));
    else if (q == "time") std::snprintf(b, sizeof b, "%.0f min", *v);
    else if (q == "rpm") std::snprintf(b, sizeof b, "%.0f rpm", *v);
    else if (q == "mmol") std::snprintf(b, sizeof b, "%.2f mmol", *v);
    else std::snprintf(b, sizeof b, "%g", *v);
    return b;
}

void parse_progress(const json& j, std::array<m::ProgressExt, 8>& prog, std::array<m::PlanExt, 8>& plans, m::PidExt& pid) {
    std::array<bool, 8> seen{};
    for (auto& c : sub(j, "channels")) {
        int id = static_cast<int>(j_int(c, "id"));
        if (id < 1 || id > 8) continue;
        size_t i = static_cast<size_t>(id - 1);
        seen[i] = true;
        m::ProgressExt p;
        p.programName = j_str(c, "programName");
        for (auto& s : sub(c, "steps")) p.stepNames.push_back(j_str(s, "name", m::zh_step_name(j_str(s, "type"))));
        p.stepTarget = fmt_step_target(sub(c, "stepTarget"));
        p.stepFraction = j_num(c, "stepFraction");
        p.elapsedSec = j_int(c, "elapsedSec");
        if (auto r = j_optnum(c, "remainingSec")) p.remainingSec = static_cast<int64_t>(*r);
        p.gasQueueRank = j_optint(c, "gasQueueRank");
        p.waitingFor = j_ints(c, "waitingFor");
        p.waitedSec = j_int(c, "waitedSec");
        const json& d = sub(c, "draft");
        p.editedNotSent = j_bool(d, "editedNotSent");
        p.stepCountDraft = static_cast<int>(j_int(d, "stepCount"));
        p.draftTotalSec = j_int(d, "totalSec");
        prog[i] = std::move(p);
        m::PlanExt pl;
        const json& pj = sub(c, "plan");
        int64_t t0 = j_time(pj, "t0").value_or(0);
        for (auto& s : sub(pj, "segs")) {
            m::PlanSeg g;
            g.kind = j_str(s, "kind");
            g.name = j_str(s, "name", m::zh_step_name(g.kind));
            g.start = t0 + static_cast<int64_t>(j_num(s, "startSec") * 1000);
            g.end = t0 + static_cast<int64_t>(j_num(s, "endSec") * 1000);
            g.openEnded = j_bool(s, "openEnded");
            g.earliestEnd = t0 + static_cast<int64_t>(j_num(s, "earliestEndSec", j_num(s, "endSec")) * 1000);
            pl.segs.push_back(g);
        }
        for (auto& pt : sub(pj, "temp"))
            if (pt.is_array() && pt.size() >= 2 && pt[0].is_number() && pt[1].is_number())
                pl.tempPlan.push_back({t0 + static_cast<int64_t>(pt[0].get<double>() * 1000), pt[1].get<double>()});
        for (auto& pt : sub(pj, "press"))
            if (pt.is_array() && pt.size() >= 2 && pt[0].is_number() && pt[1].is_number())
                pl.pressPlan.push_back({t0 + static_cast<int64_t>(pt[0].get<double>() * 1000), psi2mpa(pt[1].get<double>())});
        pl.tempActual = std::move(plans[i].tempActual);  // 实测由趋势数据补（NetBackend）
        pl.pressActual = std::move(plans[i].pressActual);
        plans[i] = std::move(pl);
    }
    for (size_t i = 0; i < 8; ++i)
        if (!seen[i]) {
            prog[i] = m::ProgressExt{};
            plans[i] = m::PlanExt{};
        }
    for (auto& a : sub(j, "autotune")) {
        int id = static_cast<int>(j_int(a, "id"));
        if (id < 1 || id > 8) continue;
        auto& pc = pid.channels[static_cast<size_t>(id - 1)];
        pc.tuneCycle = static_cast<int>(j_int(a, "cycle", pc.tuneCycle));
        pc.tuneCycles = static_cast<int>(j_int(a, "cycles", pc.tuneCycles));
        pc.tuneLeftSec = j_optnum(a, "leftSec");
        pc.tuneFraction = j_num(a, "fraction", pc.tuneFraction);
    }
}

// ============================================================ 各页
void parse_device(const json& j, m::PagesExt& x, int64_t* bootAt) {
    x.about.model = j_str(j, "model", x.about.model);
    x.about.version = j_str(j, "hmiVersion", x.about.version);
    x.about.serial = j_str(j, "serial", x.about.serial);
    if (bootAt)
        if (auto b = j_time(j, "bootTime")) *bootAt = *b;
    const json& io = sub(j, "io");
    if (io.is_object()) {
        auto ints = j_ints(io, "inletCoils");
        for (size_t i = 0; i < 3 && i < ints.size(); ++i) x.io.inletCoil[i] = ints[i];
        x.io.ventCoil = static_cast<int>(j_int(io, "ventCoil", x.io.ventCoil));
        ints = j_ints(io, "svCoils");
        for (size_t i = 0; i < 8 && i < ints.size(); ++i) x.io.svCoil[i] = ints[i];
        ints = j_ints(io, "aiChannels");
        for (size_t i = 0; i < 8 && i < ints.size(); ++i) x.io.aiChannel[i] = ints[i];
        x.io.ioModel = j_str(io, "ioModel", x.io.ioModel);
        x.io.aiModel = j_str(io, "aiModel", x.io.aiModel);
    }
}

void parse_comm(const json& j, m::CommExt& c) {
    c.pollSec = j_num(j, "pollSec", c.pollSec);
    c.offlineAfter = static_cast<int>(j_int(j, "offlineAfter", c.offlineAfter));
    const json& mods = sub(j, "modules");
    if (mods.is_array()) {
        c.modules.clear();
        for (auto& e : mods) {
            m::ModuleExt mo;
            mo.id = j_str(e, "id");
            mo.name = j_str(e, "name");
            mo.model = j_str(e, "model");
            mo.detail = j_str(e, "detail");
            mo.configured = j_bool(e, "configured", true);
            mo.online = j_bool(e, "online", true);
            mo.port = j_str(e, "port");
            mo.baud = static_cast<int>(j_int(e, "baud", mo.baud));
            mo.polls = j_int(e, "polls");
            mo.fails = j_int(e, "fails");
            mo.lastOkAt = j_time(e, "lastOkAt");
            mo.offlineSince = j_time(e, "offlineSince");
            mo.failStreak = static_cast<int>(j_int(e, "failStreak"));
            c.modules.push_back(std::move(mo));
        }
        return;
    }
    // 后端还没加 modules：用现有 CommDto 的 temp / pressure / stir / io 拼一份
    static const char* ids[4] = {"temp", "pressure", "io", "stir"};
    static const char* names[4] = {"温控", "压力", "阀门", "搅拌"};
    c.modules.clear();
    for (int i = 0; i < 4; ++i) {
        const json& e = sub(j, ids[i]);
        if (!e.is_object()) continue;
        m::ModuleExt mo;
        mo.id = ids[i];
        mo.name = names[i];
        mo.configured = j_bool(e, "configured", true);
        mo.online = j_bool(e, "online", true);
        mo.polls = j_int(e, "polls");
        mo.fails = j_int(e, "fails");
        mo.lastOkAt = j_time(e, "lastOkAt");
        c.modules.push_back(std::move(mo));
    }
}

void parse_network(const json& j, m::NetworkExt& n) {
    n.wifiOn = j_bool(j, "wifiOn", n.wifiOn);
    n.connectedSsid = j_str(j, "connectedSsid");
    n.ip = j_str(j, "ip");
    n.gateway = j_str(j, "gateway");
    n.hostname = j_str(j, "hostname", n.hostname);
    n.strength = static_cast<int>(j_int(j, "strength", 0));
    n.autoConnect = j_bool(j, "autoConnect", n.autoConnect);
    n.nearby.clear();
    for (auto& w : sub(j, "nearby"))
        n.nearby.push_back({j_str(w, "ssid"), j_bool(w, "secured", true), static_cast<int>(j_int(w, "strength", 1)), j_bool(w, "saved")});
    n.ethernetPlugged = j_bool(j, "ethernetPlugged");
    n.ethernetIp = j_str(j, "ethernetIp");
    n.dhcp = j_bool(j, "dhcp", true);
    n.scanning = j_bool(j, "scanning");
}

void parse_usb(const json& j, m::UsbExt& u) {
    u.present = j_bool(j, "present");
    u.label = j_str(j, "label");
    u.freeBytes = j_int(j, "freeBytes");
    u.lastExport = j_str(j, "lastExport");
}

void parse_storage(const json& j, m::StorageExt& s) {
    s.diskTotal = j_int(j, "diskTotal");
    s.diskFree = j_int(j, "diskFree");
    s.systemBytes = j_int(j, "systemBytes");
    s.dataBytes = j_int(j, "dataBytes");
    s.settingsPath = j_str(j, "settingsPath");
    s.dataDir = j_str(j, "dataDir");
    s.todayTelemetryBytes = j_int(j, "todayTelemetryBytes");
    s.todayEventsBytes = j_int(j, "todayEventsBytes");
    s.leakRuns = static_cast<int>(j_int(j, "leakRuns"));
}

void parse_users(const json& j, m::UsersExt& u) {
    u.mode = j_str(j, "mode", u.mode);
    u.role = j_str(j, "role", u.role);
}

void parse_pid(const json& j, m::PidExt& p) {
    p.controller = j_str(j, "controller", p.controller);
    p.bands.clear();
    for (auto& b : sub(j, "bands"))
        p.bands.push_back({static_cast<int>(j_int(b, "index")), j_str(b, "name"), j_num(b, "loC"), j_num(b, "hiC")});
    for (auto& c : sub(j, "channels")) {
        int id = static_cast<int>(j_int(c, "id"));
        if (id < 1 || id > 8) continue;
        auto& pc = p.channels[static_cast<size_t>(id - 1)];
        pc.id = id;
        pc.autotuning = j_bool(c, "autotuning");
        pc.tuneBand = j_optint(c, "tuneBand");
        pc.tuneSpC = j_optnum(c, "tuneSpC");
        pc.bands.clear();
        for (auto& b : sub(c, "bands"))
            pc.bands.push_back({static_cast<int>(j_int(b, "index")), j_num(b, "p", j_num(b, "P")), j_num(b, "i", j_num(b, "I")),
                                j_num(b, "d", j_num(b, "D")), j_optnum(b, "tunedSpC"), j_time(b, "tunedAt"), j_time(b, "manualEditedAt")});
        pc.tuneCycle = static_cast<int>(j_int(c, "tuneCycle", pc.tuneCycle));
        pc.tuneCycles = static_cast<int>(j_int(c, "tuneCycles", pc.tuneCycles));
        if (c.contains("tuneLeftSec")) pc.tuneLeftSec = j_optnum(c, "tuneLeftSec");
        pc.tuneFraction = j_num(c, "tuneFraction", pc.tuneFraction);
    }
}

void parse_calib(const json& j, m::CalibExt& c) {
    c.readings.clear();
    for (auto& r : sub(j, "readings")) {
        m::CalibReading x;
        x.n = static_cast<int>(j_int(r, "n"));
        x.manifoldFrom = psi2mpa(j_num(r, "manifoldFromPsi"));
        x.manifoldTo = psi2mpa(j_num(r, "manifoldToPsi"));
        if (auto v = j_optnum(r, "vesselFromPsi")) x.vesselFrom = psi2mpa(*v);
        if (auto v = j_optnum(r, "vesselToPsi")) x.vesselTo = psi2mpa(*v);
        c.readings.push_back(x);
    }
    c.pass = static_cast<int>(j_int(j, "pass", c.pass));
    c.passes = static_cast<int>(j_int(j, "passes", c.passes));
    c.cycle = static_cast<int>(j_int(j, "cycle", c.cycle));
    c.cycles = static_cast<int>(j_int(j, "cycles", c.cycles));
    auto sm = j_ints(j, "stepMinutes");
    for (size_t i = 0; i < 4; ++i) c.stepMinutes[i] = i < sm.size() ? sm[i] : 0;
    c.vesselTempC = j_num(j, "vesselTempC", c.vesselTempC);
    c.gas = j_str(j, "gas", c.gas);
}

std::shared_ptr<m::Trend> parse_trend(const json& j) {
    if (!j.is_object()) return nullptr;
    auto t = std::make_shared<m::Trend>();
    t->t0 = j_time(j, "t0").value_or(0);
    t->stepMs = std::max<int64_t>(1, j_int(j, "stepMs", 10000));
    size_t n = static_cast<size_t>(std::max<int64_t>(0, j_int(j, "count", 0)));
    for (auto& c : sub(j, "channels")) {
        int id = static_cast<int>(j_int(c, "id"));
        if (id < 1 || id > 8) continue;
        size_t i = static_cast<size_t>(id - 1);
        for (auto& v : sub(c, "tempC")) t->temp[i].push_back(j_f(v));
        for (auto& v : sub(c, "pressurePsi")) t->press[i].push_back(v.is_number() ? static_cast<float>(psi2mpa(v.get<double>())) : NAN);
        for (auto& v : sub(c, "gasMmol")) t->gas[i].push_back(j_f(v));
        n = std::max({n, t->temp[i].size(), t->press[i].size(), t->gas[i].size()});
    }
    for (size_t i = 0; i < 8; ++i) {  // 每一路、每一项都补成同样长（界面按 temp[0].size() 取长度）
        t->temp[i].resize(n, NAN);
        t->press[i].resize(n, NAN);
        t->gas[i].resize(n, NAN);
    }
    return t;
}

m::ProgramDoc program_from_wire(const json& prog) {
    m::ProgramDoc d;
    if (prog.is_object()) {
        std::string s = prog.dump();
        m::program_from_json(s, d);
    }
    return d;
}

m::RunRecord parse_run(const json& j) {
    m::RunRecord r;
    r.id = j_str(j, "id");
    r.programName = j_str(j, "programName");
    r.result = j_str(j, "result", "done");
    r.resultText = j_str(j, "resultText");
    r.start = j_time(j, "start").value_or(0);
    r.end = j_time(j, "end").value_or(0);
    r.plannedSec = j_int(j, "plannedSec");
    r.channels = j_ints(j, "channels");
    r.endReason = j_str(j, "endReason");
    // 没有的（null，例如程序里没有反应 / 充压步骤）记 NaN，界面显示「—」，不显示成 0 或假的默认值
    const double kNa = std::numeric_limits<double>::quiet_NaN();
    r.avgMmol = j_optnum(j, "avgMmol").value_or(kNa);
    r.rsdPct = j_optnum(j, "rsdPct").value_or(kNa);
    r.tempDevC = j_optnum(j, "tempDevC").value_or(kNa);
    r.pressMPa = j_optnum(j, "pressPsi") ? psi2mpa(*j_optnum(j, "pressPsi")) : kNa;
    r.gasStart = j_time(j, "gasStart").value_or(0);
    for (auto& c : sub(j, "perCh"))
        r.perCh.push_back({static_cast<int>(j_int(c, "ch")), j_optnum(c, "mmol").value_or(kNa), j_num(c, "maxRate"),
                           j_optnum(c, "tempC").value_or(kNa), j_bool(c, "flagged")});
    const json& ab = sub(j, "abort");
    if (ab.is_object()) {
        m::RunAbortInfo a;
        a.ch = static_cast<int>(j_int(ab, "ch"));
        a.quantity = j_str(ab, "quantity");
        double k = a.quantity == "pressure" ? 1.0 / kPsiPerMPa : 1.0;
        a.value = j_num(ab, "value") * k;
        a.setpoint = j_num(ab, "setpoint") * k;
        a.limit = j_num(ab, "limit") * k;
        a.rangeHi = j_num(ab, "rangeHi") * k;
        a.at = j_time(ab, "at").value_or(0);
        a.title = j_str(ab, "title");
        a.did = j_str(ab, "did");
        r.abort = a;
    }
    r.note = j_str(j, "note");
    if (sub(j, "trend").is_object()) r.trend = parse_trend(sub(j, "trend"));
    if (sub(j, "program").is_object()) r.program = program_from_wire(sub(j, "program"));
    r.stepMinutes = j_ints(j, "stepMinutes");
    return r;
}

std::vector<m::RunRecord> parse_runs(const json& j) {
    std::vector<m::RunRecord> v;
    for (auto& e : sub(j, "items"))
        if (e.is_object()) v.push_back(parse_run(e));
    std::stable_sort(v.begin(), v.end(), [](const m::RunRecord& a, const m::RunRecord& b) { return a.start > b.start; });
    return v;
}

std::vector<m::HistoryFileInfo> parse_files(const json& j) {
    std::vector<m::HistoryFileInfo> v;
    std::string type = j_str(j, "type");
    for (auto& e : sub(j, "items")) {
        m::HistoryFileInfo f;
        f.name = j_str(e, "name");
        f.type = j_str(e, "type", type);
        f.bytes = j_int(e, "bytes");
        f.modifiedAt = j_time(e, "modifiedAt").value_or(0);
        f.from = j_time(e, "from");
        f.to = j_time(e, "to");
        f.day = j_str(e, "day");
        f.desc = j_str(e, "desc");
        f.lines = static_cast<int>(j_int(e, "lines"));
        v.push_back(std::move(f));
    }
    return v;
}

static std::string local_day(int64_t ms) {
    time_t t = static_cast<time_t>(ms / 1000);
    struct tm tm{};
    localtime_r(&t, &tm);
    char b[16];
    std::strftime(b, sizeof b, "%Y%m%d", &tm);
    return b;
}

std::vector<m::DayInfo> build_days(const std::vector<m::HistoryFileInfo>& files, const std::vector<m::RunRecord>& runs) {
    std::map<std::string, m::DayInfo> by;
    for (auto& f : files) {
        std::string d = !f.day.empty() ? f.day : f.from ? local_day(*f.from) : local_day(f.modifiedAt);
        auto& di = by[d];
        di.day = d;
        di.bytes += f.bytes;
        di.files.push_back(f);
    }
    for (auto& r : runs) {
        if (!r.start) continue;
        std::string d = local_day(r.start);
        auto it = by.find(d);
        if (it == by.end()) continue;  // 没有数据文件的日子不列
        it->second.runs.push_back({r.id, r.programName, r.result, r.start, r.end, r.channels});
    }
    std::vector<m::DayInfo> v;
    for (auto it = by.rbegin(); it != by.rend(); ++it) v.push_back(std::move(it->second));
    return v;
}

std::string fmt_channels(const std::vector<int>& chs) {
    if (chs.empty()) return "";
    std::vector<int> c = chs;
    std::sort(c.begin(), c.end());
    c.erase(std::unique(c.begin(), c.end()), c.end());
    bool contiguous = c.back() - c.front() + 1 == static_cast<int>(c.size());
    if (c.size() == 1) return std::to_string(c[0]) + " 号";
    if (contiguous && c.size() > 2) return std::to_string(c.front()) + "–" + std::to_string(c.back()) + " 号";
    std::string s;
    for (size_t i = 0; i < c.size(); ++i) s += (i ? "、" : "") + std::to_string(c[i]);
    return s + " 号";
}

std::vector<m::LeakHistoryItem> parse_leak_tests(const json& j) {
    std::vector<m::LeakHistoryItem> v;
    for (auto& e : sub(j, "items")) {
        m::LeakHistoryItem h;
        h.at = j_time(e, "start").value_or(0);
        h.channels = fmt_channels(j_ints(e, "channels"));
        h.pass = j_bool(e, "pass", true);
        h.note = j_str(e, "note");
        v.push_back(std::move(h));
    }
    std::stable_sort(v.begin(), v.end(), [](const m::LeakHistoryItem& a, const m::LeakHistoryItem& b) { return a.at > b.at; });
    return v;
}

std::vector<m::LeakSample> parse_leak_samples(const json& j) {
    std::vector<m::LeakSample> v;
    for (auto& p : sub(j, "points")) {
        m::LeakSample s;
        s.min = j_num(p, "min");
        const json& d = sub(p, "dpPsi");
        for (size_t i = 0; i < 8; ++i)
            s.dpKPa[i] = d.is_array() && i < d.size() && d[i].is_number() ? static_cast<float>(d[i].get<double>() * kKPaPerPsi) : NAN;
        v.push_back(s);
    }
    return v;
}

// ============================================================ 程序
json steps_to_wire(const m::ProgramDoc& p) {
    json j = json::parse(m::program_to_json(p), nullptr, false);
    if (j.is_discarded() || !j.contains("steps")) return json::array();
    return j["steps"];
}

bool parse_channel_program(const json& j, m::ProgramDoc& doc, std::string& src) {
    const json* body = &j;
    if (sub(j, "program").is_object()) body = &sub(j, "program");
    if (!sub(*body, "steps").is_array()) return false;
    doc = program_from_wire(*body);
    if (doc.name.empty()) doc.name = j_str(j, "name");
    doc.revision = j_int(*body, "revision", j_int(j, "revision"));
    src = j_str(*body, "sourceId", j_str(j, "sourceId"));
    return !doc.steps.empty();
}

// 压力字段：schema 的 unit 是 psi / psi/h 的；schema 还没取到时用 v0.8 的固定表（§6.9）
static int pressure_kind(const m::ProgramSchema& s, const std::string& type, const std::string& key) {
    if (const auto* ss = s.step(type))
        if (const auto* f = ss->field(key)) return f->unit == "psi" ? 1 : f->unit == "psi/h" ? 2 : 0;
    static const std::set<std::string> P = {"leak.pres", "exchange.fill", "exchange.release", "press.pres", "hold.pres", "vent.to", "vent.flushPres"};
    if (P.count(type + "." + key)) return 1;
    if (type == "leak" && key == "maxDrop") return 2;
    return 0;
}

static void convert_steps(json& steps, const m::ProgramSchema& schema, bool to_psi) {
    if (!steps.is_array()) return;
    for (auto& st : steps) {
        std::string type = j_str(st, "type");
        json& par = st.contains("params") ? st["params"] : st;
        if (!par.is_object()) continue;
        for (auto it = par.begin(); it != par.end(); ++it) {
            if (!it->is_number()) continue;
            int k = pressure_kind(schema, type, it.key());
            if (!k) continue;
            double v = it->get<double>();
            if (to_psi) {
                double psi = mpa2psi(v);
                double q = k == 1 ? 10.0 : 100.0;  // psi 1 位小数、psi/h 2 位（照 PC 端 StepDefaults.ToWire）
                *it = std::round(psi * q) / q;
            } else {
                *it = std::round(psi2mpa(v) * 100000.0) / 100000.0;
            }
        }
    }
}

std::vector<m::LibEntry> parse_library(const json& j, bool tpl, const m::ProgramSchema& schema, int* max) {
    std::vector<m::LibEntry> v;
    if (max) *max = static_cast<int>(j_int(j, "max", *max));
    for (auto& e : sub(j, "items")) {
        json prog = tpl ? sub(e, "program") : e;
        if (!prog.is_object()) continue;
        if (prog.contains("steps")) convert_steps(prog["steps"], schema, true);
        m::LibEntry le;
        le.prog = program_from_wire(prog);
        le.prog.revision = j_int(e, "revision", le.prog.revision);
        le.prog.modified = j_str(prog, "modified");
        le.prog.by = j_str(prog, "by");
        if (auto t = parse_iso_ms(le.prog.modified)) {
            // PC 的 modified 是本地时间、不带时区：按本机时区换成 UTC
            time_t tt = static_cast<time_t>(*t / 1000);
            struct tm tm{};
            gmtime_r(&tt, &tm);
            tm.tm_isdst = -1;
            bool has_zone = le.prog.modified.find('Z') != std::string::npos || le.prog.modified.find('+') != std::string::npos;
            le.prog.modifiedMs = has_zone ? *t : static_cast<int64_t>(mktime(&tm)) * 1000;
        }
        if (tpl) {
            le.prog.note = j_str(e, "note");
            le.prog.from = j_str(e, "from");
            le.prog.builtIn = j_bool(e, "builtIn");
            if (le.prog.id.empty()) le.prog.id = j_str(e, "id");
        }
        le.usedBy = j_ints(e, "usedBy");
        v.push_back(std::move(le));
    }
    return v;
}

json library_to_wire(const m::ProgramDoc& p, bool tpl, const m::ProgramSchema& schema) {
    json prog = json::parse(m::program_to_json(p), nullptr, false);
    if (prog.is_discarded()) prog = json::object();
    prog.erase("revision");
    prog.erase("note");
    if (prog.contains("steps")) {
        for (auto& st : prog["steps"]) st["name"] = m::zh_step_name(j_str(st, "type"));
        convert_steps(prog["steps"], schema, false);
    }
    json body;
    if (tpl) {
        body = {{"note", p.note}, {"from", p.from}, {"program", prog}};
    } else {
        body = prog;
    }
    if (p.revision) body["revision"] = p.revision;
    return body;
}

}  // namespace reactor::hmi::net
