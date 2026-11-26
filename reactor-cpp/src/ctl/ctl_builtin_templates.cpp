#include "ctl/ctl_builtin_templates.hpp"

#include <algorithm>
#include <cmath>
#include <utility>

#include "core/program/program_step_format.hpp"
#include "core/units.hpp"

namespace reactor::ctl::builtin_templates {

namespace {

struct Def {
    const char* key;
    bool sel;
    const char* code;
    double num;
};

// C# StepDefaults.Of（顺序 = 参数面板的顺序；压力 psi、压降 psi/h）
const std::vector<Def>& defaults_of(const std::string& t) {
    static const std::vector<Def> leak{{"pres", false, nullptr, 435.1}, {"settle", false, nullptr, 5.0},  {"hold", false, nullptr, 30.0},
                                       {"maxDrop", false, nullptr, 2.0}, {"onFail", true, "stop", 0},     {"after", true, "vent", 0}};
    static const std::vector<Def> exchange{{"gas", true, "inert", 0},          {"fill", false, nullptr, 72.5},    {"release", false, nullptr, 2.9},
                                           {"cycles", false, nullptr, 3.0},    {"lineFlush", false, nullptr, 2.0}, {"maxSec", false, nullptr, 60.0}};
    static const std::vector<Def> stir{{"rpm", false, nullptr, 400.0}};
    static const std::vector<Def> heat{{"mode", true, "fixed", 0},          {"temp", false, nullptr, 80.0},     {"rate", false, nullptr, 0.0},
                                       {"band", false, nullptr, 2.0},       {"wait", false, nullptr, 15.0},     {"maxWait", false, nullptr, 120.0},
                                       {"onTimeout", true, "stop", 0}};
    static const std::vector<Def> press{{"gas", true, "A", 0},             {"pres", false, nullptr, 290.1},    {"tol", false, nullptr, 2.0},
                                        {"regulate", true, "on", 0},       {"refill", false, nullptr, 3.0},    {"maxSec", false, nullptr, 180.0}};
    static const std::vector<Def> hold{{"until", true, "time", 0},          {"time", false, nullptr, 10.0},     {"temp", false, nullptr, 40.0},
                                       {"pres", false, nullptr, 14.5},      {"maxWait", false, nullptr, 60.0},  {"onTimeout", true, "continue", 0}};
    static const std::vector<Def> react{{"time", false, nullptr, 480.0},     {"minTime", false, nullptr, 30.0},  {"mmolOn", true, "off", 0},
                                        {"mmol", false, nullptr, 5.0},       {"stallOn", true, "on", 0},         {"stallRate", false, nullptr, 0.005},
                                        {"stallMin", false, nullptr, 30.0}};
    static const std::vector<Def> vent{{"to", false, nullptr, 2.9}, {"maxSec", false, nullptr, 120.0}, {"flush", false, nullptr, 3.0},
                                       {"flushPres", false, nullptr, 43.5}};
    static const std::vector<Def> none;
    if (t == "leak") return leak;
    if (t == "exchange") return exchange;
    if (t == "stir") return stir;
    if (t == "heat") return heat;
    if (t == "press") return press;
    if (t == "hold") return hold;
    if (t == "react") return react;
    if (t == "vent") return vent;
    return none;
}

// C# StepDefaults.IsPressure / IsRate
bool is_pressure(const std::string& t, const std::string& k) {
    return (t == "leak" && k == "pres") || (t == "exchange" && (k == "fill" || k == "release")) || (t == "press" && k == "pres") ||
           (t == "hold" && k == "pres") || (t == "vent" && (k == "to" || k == "flushPres"));
}
bool is_rate(const std::string& t, const std::string& k) { return t == "leak" && k == "maxDrop"; }

double round_away(double v, int dec) {
    double f = std::pow(10.0, dec);
    return std::round(v * f) / f;  // std::round = 远离零（C# MidpointRounding.AwayFromZero）
}

struct PcStep {
    std::string type, name;
    std::vector<std::pair<std::string, std::string>> sel;
    std::vector<std::pair<std::string, double>> num;
    bool has(const std::string& k) const {
        for (auto& s : sel)
            if (s.first == k) return true;
        for (auto& n : num)
            if (n.first == k) return true;
        return false;
    }
};

struct V {
    const char* key;
    bool is_str;
    std::string str;
    double num;
    V(const char* k, const char* s) : key(k), is_str(true), str(s), num(0) {}
    V(const char* k, double n) : key(k), is_str(false), num(n) {}
};

// C# ProgramTemplates.Step：给定参数（字符串 = 选项码，数值 = 本机单位，压力 MPa）+ StepDefaults.Fill 补缺
PcStep step(const std::string& type, const std::string& name, std::initializer_list<V> ps) {
    PcStep s{type, name, {}, {}};
    for (const auto& v : ps) {
        if (v.is_str) s.sel.emplace_back(v.key, v.str);
        else s.num.emplace_back(v.key, v.num);
    }
    for (const auto& d : defaults_of(type)) {
        if (s.has(d.key)) continue;
        if (d.sel) s.sel.emplace_back(d.key, d.code);
        else if (is_pressure(type, d.key)) s.num.emplace_back(d.key, round_away(core::psi_to_mpa(d.num), 2));   // PsiToStored（MPa 2 位）
        else if (is_rate(type, d.key)) s.num.emplace_back(d.key, round_away(core::psi_to_mpa(d.num), 4));       // RateToStored（MPa/h 4 位）
        else s.num.emplace_back(d.key, d.num);
    }
    return s;
}

PcStep leak(double mpa = 3.0, double hold = 30, const char* after = "vent") {
    return step("leak", "气密检查", {V("pres", mpa), V("hold", hold), V("after", after)});
}
PcStep exchange(const char* gas, int cycles = 3, const char* name = nullptr) {
    std::string n = name ? name : (std::string(gas) == "inert" ? "惰性置换" : "置换");
    return step("exchange", n, {V("gas", gas), V("fill", 0.5), V("release", 0.02), V("cycles", static_cast<double>(cycles)), V("lineFlush", 2.0)});
}
PcStep stir(double rpm = 400) { return step("stir", "搅拌", {V("rpm", rpm)}); }
PcStep heat(double temp, double wait = 15) { return step("heat", "升温", {V("mode", "fixed"), V("temp", temp), V("wait", wait)}); }
PcStep press(const char* gas, double mpa, const char* regulate = "on") {
    return step("press", "充压", {V("gas", gas), V("pres", mpa), V("regulate", regulate)});
}
PcStep hold_time(double min) { return step("hold", "保持", {V("until", "time"), V("time", min)}); }
PcStep react(double min, const char* stall = "on", double min_time = 30) {
    return step("react", "反应", {V("time", min), V("minTime", std::min(min_time, min)), V("stallOn", stall)});
}
PcStep vent(int flush = 3) { return step("vent", "放空", {V("to", 0.02), V("flush", static_cast<double>(flush)), V("flushPres", 0.3)}); }
PcStep end() { return step("end", "结束", {}); }

ojson step_json(const PcStep& s) {
    ojson params = ojson::object();
    for (const auto& kv : s.sel) params[kv.first] = kv.second;  // C# PcProgram.WriteSteps：先选项、再数值
    for (const auto& kv : s.num) params[kv.first] = core::program::json_number(kv.second);
    return ojson{{"type", s.type}, {"name", s.name}, {"params", params}};
}

ojson program(const std::string& id, const std::string& name, const std::string& model, const std::vector<PcStep>& steps) {
    ojson arr = ojson::array();
    for (const auto& s : steps) arr.push_back(step_json(s));
    ojson p;
    p["id"] = id;
    p["name"] = name;
    p["model"] = model;
    p["modified"] = kModified;
    p["by"] = kBy;
    p["draft"] = true;
    p["steps"] = arr;
    return p;
}

ojson tpl(ojson prog, const std::string& note) {
    ojson t;
    t["note"] = note;
    t["from"] = "";
    t["program"] = std::move(prog);
    t["builtIn"] = true;
    return t;
}

}  // namespace

std::vector<ojson> all(const std::string& model) {
    return {
        tpl(program("T1", "加氢筛选", model,
                    {leak(), exchange("inert", 3, "惰性置换"), exchange("A", 3, "气体 A 置换"), stir(), heat(80), press("A", 2.0), react(480),
                     vent(), end()}),
            "加氢筛选的标准流程：先用惰性气体检漏和置换，再换成反应气，升到温度后充压并稳压，吸气停止就结束，最后放空并用惰性气体置换。"),
        tpl(program("T2", "只做气密", model, {leak(), end()}), "只检查密封：用惰性气体充到试验压力，保压后看压降，之后放空。"),
        tpl(program("T3", "惰性气氛加热", model,
                    {exchange("inert", 3, "惰性置换"), stir(), heat(120), press("inert", 0.5), hold_time(240), vent(), end()}),
            "惰性气氛下加热：惰性置换后升温，充惰性气体并稳压，保持一段时间后放空。"),
        tpl(program("T4", "吸气量验证", model,
                    {step("exchange", "吹总管",
                          {V("gas", "A"), V("fill", 0.5), V("release", 0.02), V("cycles", 0.0), V("lineFlush", 3.0)}),
                     press("A", 0.5, "off"), hold_time(10), stir(),
                     step("react", "反应", {V("time", 120.0), V("minTime", 30.0), V("stallOn", "on"), V("stallMin", 15.0)}), end()}),
            "用已知量的吸收剂检验吸气量读数：室温、不稳压，充压后先保持、等各路都充完再开搅拌，吸气停止就结束。"),
    };
}

SeedResult seed(store::ProgramLibrary& lib, const std::string& model) {
    SeedResult r;
    for (auto& t : all(model)) {
        std::string id = t["program"]["id"].get<std::string>();
        std::string status;
        auto res = lib.upsert_builtin(store::LibKind::Template, t, &status);
        if (!res.ok) {
            ++r.outdated;
            r.errors.push_back("内置模板 " + id + " 放不进程序库：" + res.message);
            continue;
        }
        if (status == "added") ++r.added;
        else if (status == "updated") ++r.updated;
        else ++r.unchanged;
    }
    return r;
}

nlohmann::json pc_steps_to_wire(const ojson& steps) {
    nlohmann::json out = nlohmann::json::array();
    if (!steps.is_array()) return out;
    for (const auto& s : steps) {
        if (!s.is_object()) continue;
        std::string type = s.value("type", std::string());
        nlohmann::json o;
        o["type"] = type;
        if (s.contains("name")) o["name"] = s["name"];
        nlohmann::json params = nlohmann::json::object();
        if (s.contains("params") && s["params"].is_object())
            for (auto it = s["params"].begin(); it != s["params"].end(); ++it) {
                const std::string& k = it.key();
                if (it->is_number() && is_pressure(type, k)) params[k] = round_away(core::mpa_to_psi(it->get<double>()), 1);
                else if (it->is_number() && is_rate(type, k)) params[k] = round_away(core::mpa_to_psi(it->get<double>()), 2);
                else params[k] = nlohmann::json::parse(it->dump());
            }
        o["params"] = params;
        out.push_back(o);
    }
    return out;
}

}  // namespace reactor::ctl::builtin_templates
