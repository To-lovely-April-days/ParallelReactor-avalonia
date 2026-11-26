#include "program.hpp"

#include "../ui/kit/i18n.hpp"
#include "nlohmann/json.hpp"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <ctime>

namespace reactor::hmi::model {

using json = nlohmann::json;

namespace {

constexpr double kPsiToMpa = 0.00689475729;

OptD opt_num(const json& j, const char* k) {
    auto it = j.find(k);
    if (it == j.end() || !it->is_number()) return std::nullopt;
    return it->get<double>();
}
std::string str(const json& j, const char* k) {
    auto it = j.find(k);
    return it != j.end() && it->is_string() ? it->get<std::string>() : std::string();
}

FieldSchema parse_field(const json& f) {
    FieldSchema x;
    x.key = str(f, "key");
    x.label = str(f, "label");
    x.kind = str(f, "kind");
    x.unit = str(f, "unit");
    x.def = opt_num(f, "default");
    x.min = opt_num(f, "min");
    x.max = opt_num(f, "max");
    x.declaredMin = opt_num(f, "declaredMin");
    x.declaredMax = opt_num(f, "declaredMax");
    if (f.contains("options") && f["options"].is_array())
        for (auto& o : f["options"]) {
            SchemaOption so;
            so.code = str(o, "code");
            so.label = str(o, "label");
            so.available = o.value("available", true);
            so.reason = str(o, "reason");
            x.options.push_back(so);
        }
    x.available = f.value("available", true);
    x.reason = str(f, "reason");
    if (f.contains("itemFields") && f["itemFields"].is_array())
        for (auto& i : f["itemFields"]) x.itemFields.push_back(parse_field(i));
    if (f.contains("minItems") && f["minItems"].is_number()) x.minItems = f["minItems"].get<int>();
    if (f.contains("maxItems") && f["maxItems"].is_number()) x.maxItems = f["maxItems"].get<int>();
    if (f.contains("visibleWhen") && f["visibleWhen"].is_object()) {
        VisibleWhen v;
        v.key = str(f["visibleWhen"], "key");
        for (auto& c : f["visibleWhen"]["in"]) v.in.push_back(c.get<std::string>());
        x.visibleWhen = v;
    }
    x.defaultCode = str(f, "defaultCode");
    return x;
}

}  // namespace

bool FieldSchema::is_onoff() const {
    return options.size() == 2 && ((options[0].code == "on" && options[1].code == "off") ||
                                   (options[0].code == "off" && options[1].code == "on"));
}

std::string FieldSchema::label_of(const std::string& code) const {
    for (auto& o : options)
        if (o.code == code) return o.label;
    return code;
}

const FieldSchema* StepSchema::field(const std::string& key) const {
    for (auto& f : fields)
        if (f.key == key) return &f;
    return nullptr;
}

const StepSchema* ProgramSchema::step(const std::string& type) const {
    for (auto& s : steps)
        if (s.type == type) return &s;
    return nullptr;
}

bool parse_program_schema(const std::string& text, ProgramSchema& out, std::string* err) {
    try {
        json j = json::parse(text);
        ProgramSchema s;
        s.apiVersion = str(j, "apiVersion");
        s.settingsRevision = j.value("settingsRevision", (int64_t)0);
        for (auto& t : j["stepTypes"]) s.stepTypes.push_back(t.get<std::string>());
        for (auto& st : j["steps"]) {
            StepSchema x;
            x.type = str(st, "type");
            x.label = str(st, "label");
            x.chip = str(st, "chip");
            x.help = str(st, "help");
            for (auto& f : st["fields"]) x.fields.push_back(parse_field(f));
            s.steps.push_back(std::move(x));
        }
        out = std::move(s);
        return true;
    } catch (const std::exception& e) {
        if (err) *err = e.what();
        return false;
    }
}

// ---------------------------------------------------------------- 程序 JSON
double StepDoc::num(const std::string& k, double def) const {
    auto it = params.find(k);
    return it != params.end() && !it->second.isCode ? it->second.num : def;
}
std::string StepDoc::code(const std::string& k, const std::string& def) const {
    auto it = params.find(k);
    return it != params.end() && it->second.isCode ? it->second.code : def;
}

std::string program_to_json(const ProgramDoc& p) {
    json j;
    if (!p.id.empty()) j["id"] = p.id;
    j["name"] = p.name;
    j["model"] = p.model;
    j["draft"] = p.draft;
    if (!p.note.empty()) j["note"] = p.note;
    if (p.revision) j["revision"] = p.revision;
    json steps = json::array();
    for (auto& s : p.steps) {
        json par = json::object();
        for (auto& kv : s.params) {
            if (kv.second.isCode) par[kv.first] = kv.second.code;
            else par[kv.first] = kv.second.num;
        }
        if (s.type == "heat" && s.code("mode", "fixed") == "curve") {
            json segs = json::array();
            for (auto& g : s.segments) segs.push_back({{"temp", g.temp}, {"rate", g.rate}, {"hold", g.hold}});
            par["segments"] = segs;
        }
        steps.push_back({{"type", s.type}, {"params", par}});
    }
    j["steps"] = steps;
    return j.dump();
}

bool program_from_json(const std::string& text, ProgramDoc& out) {
    try {
        json j = json::parse(text);
        ProgramDoc p;
        p.id = str(j, "id");
        p.name = str(j, "name");
        if (j.contains("model")) p.model = str(j, "model");
        p.modified = str(j, "modified");
        p.by = str(j, "by");
        p.draft = j.value("draft", false);
        p.revision = j.value("revision", (int64_t)0);
        p.note = str(j, "note");
        p.builtIn = j.value("builtIn", false);
        for (auto& st : j["steps"]) {
            StepDoc s;
            s.type = str(st, "type");
            const json& par = st.contains("params") ? st["params"] : st;
            for (auto it = par.begin(); it != par.end(); ++it) {
                if (it.key() == "type" || it.key() == "name" || it.key() == "detail") continue;
                if (it.key() == "segments" && it->is_array()) {
                    for (auto& g : *it) s.segments.push_back({g.value("temp", 0.0), g.value("rate", 0.0), g.value("hold", 0.0)});
                } else if (it->is_string()) {
                    s.set_code(it.key(), it->get<std::string>());
                } else if (it->is_number()) {
                    s.set_num(it.key(), it->get<double>());
                }
            }
            p.steps.push_back(std::move(s));
        }
        out = std::move(p);
        return true;
    } catch (const std::exception&) {
        return false;
    }
}

StepDoc new_step(const ProgramSchema& s, const std::string& type) {
    StepDoc d;
    d.type = type;
    const StepSchema* ss = s.step(type);
    if (!ss) return d;
    for (auto& f : ss->fields) {
        if (f.kind == "num" && f.def) d.set_num(f.key, *f.def);
        else if (f.is_sel()) d.set_code(f.key, !f.defaultCode.empty() ? f.defaultCode : f.options.empty() ? "" : f.options[0].code);
    }
    if (type == "heat") d.segments = {{40, 2, 5}, {60, 2, 5}, {80, 1, 15}};
    return d;
}

ProgramDoc hydrogenation_template(const ProgramSchema& s) {
    ProgramDoc p;
    p.name = "加氢筛选";
    for (const char* t : {"leak", "exchange", "exchange", "stir", "heat", "press", "react", "vent", "end"})
        p.steps.push_back(new_step(s, t));
    p.steps[1].set_code("gas", "inert");
    p.steps[2].set_code("gas", "A");
    return p;
}

bool field_active(const StepSchema& ss, const FieldSchema& f, const StepDoc& st) {
    if (!f.visibleWhen) return true;
    const FieldSchema* ctl = ss.field(f.visibleWhen->key);
    std::string def = ctl ? (!ctl->defaultCode.empty() ? ctl->defaultCode : ctl->options.empty() ? "" : ctl->options[0].code) : "";
    std::string cur = st.code(f.visibleWhen->key, def);
    return std::find(f.visibleWhen->in.begin(), f.visibleWhen->in.end(), cur) != f.visibleWhen->in.end();
}

FieldShow field_show(const StepSchema& ss, const FieldSchema& f, const StepDoc& st) {
    if (field_active(ss, f, st)) return FieldShow::Normal;
    const FieldSchema* ctl = ss.field(f.visibleWhen->key);
    if (ctl && ctl->is_onoff()) return FieldShow::Faded;   // 开关关着：变淡（Prog-Params「吸气量达到」）
    if (f.visibleWhen->in.size() >= 4) return FieldShow::Faded;  // 只在「非默认选项」时有用的辅助字段（最长等待 / 超时）
    return FieldShow::Hidden;
}

// ---------------------------------------------------------------- 显示
std::string zh_step_name(const std::string& t) {
    static const std::map<std::string, std::string> m = {
        {"leak", "气密检查"}, {"exchange", "置换"}, {"stir", "搅拌"}, {"heat", "升温"}, {"press", "充压"},
        {"hold", "保持"},     {"react", "反应"},    {"vent", "放空"}, {"end", "结束"}};
    auto it = m.find(t);
    return it == m.end() ? t : it->second;
}

std::string fmt_num(double v, int d) {
    if (std::isnan(v)) return "—";  // 后台给 null（没有这一项，比如程序里没有反应步）
    char b[32];
    std::snprintf(b, sizeof b, "%.*f", d, v);
    return b;
}

std::string fmt_press_psi(double psi, int d) { return fmt_num(psi * kPsiToMpa, d); }

int field_decimals(const FieldSchema& f) {
    if (f.is_pressure()) return 3;
    if (f.unit == "mmol/min") return 3;
    if (f.unit == "mmol") return 2;
    if (f.unit == "°C/min") return 1;
    if (f.key == "band") return 1;
    return 0;
}

std::string unit_label(const FieldSchema& f) {
    if (f.unit == "psi") return "MPa";
    if (f.unit == "psi/h") return "MPa/h";
    if (f.unit == "分钟") return "min";
    if (f.unit == "秒") return "s";
    if (f.unit == "次") return trc("次 (次数单位)");
    return f.unit;
}

double to_display(const FieldSchema& f, double v) { return f.is_pressure() ? v * kPsiToMpa : v; }
double from_display(const FieldSchema& f, double v) { return f.is_pressure() ? v / kPsiToMpa : v; }

namespace {
std::string N(double v) {
    if (std::fabs(v - std::round(v)) < 1e-9) return fmt_num(v, 0);
    return fmt_num(v, std::fabs(v * 10 - std::round(v * 10)) < 1e-9 ? 1 : 2);
}
// 下面几段显示文案在这里就按当前语言拼好（数值夹在中间，界面没法整串查表）
std::string gas_short(const std::string& c) { return tr(c == "A" ? "气体 A" : c == "B" ? "气体 B" : "惰性气体"); }
}  // namespace

std::string step_value_line(const StepDoc& s) {
    const std::string& t = s.type;
    if (t == "leak") return fmt_press_psi(s.num("pres", 435.1)) + " MPa";
    if (t == "exchange") return gas_short(s.code("gas", "inert"));
    if (t == "stir") return s.num("rpm", 400) <= 0 ? std::string(tr("停止")) : N(s.num("rpm", 400)) + " rpm";
    if (t == "heat") {
        if (s.code("mode", "fixed") == "curve") return trf("曲线 %d 段", (int)s.segments.size());
        return N(s.num("temp", 80)) + " °C";
    }
    if (t == "press") return fmt_press_psi(s.num("pres", 290.1)) + " MPa";
    if (t == "hold") {
        std::string u = s.code("until", "time");
        if (u == "tempAbove") return "≥ " + N(s.num("temp", 40)) + " °C";
        if (u == "tempBelow") return "≤ " + N(s.num("temp", 40)) + " °C";
        if (u == "pressAbove") return "≥ " + fmt_press_psi(s.num("pres", 14.5)) + " MPa";
        if (u == "pressBelow") return "≤ " + fmt_press_psi(s.num("pres", 14.5)) + " MPa";
        return N(s.num("time", 10)) + " min";
    }
    if (t == "react") return "≤ " + N(s.num("time", 480)) + " min";
    if (t == "vent") return fmt_press_psi(s.num("to", 2.9)) + " MPa";
    return "";
}

std::string step_sub_line(const StepDoc& s) {
    const std::string& t = s.type;
    if (t == "leak") return trf("保压 %s min", N(s.num("hold", 30)).c_str());
    if (t == "exchange") {
        int c = (int)std::lround(s.num("cycles", 3));
        return c <= 0 ? std::string(tr("只吹总管")) : "× " + std::to_string(c);
    }
    if (t == "heat") {
        if (s.code("mode", "fixed") == "curve") return s.segments.empty() ? "" : trf("终温 %s °C", N(s.segments.back().temp).c_str());
        return trf("平衡 %s min", N(s.num("wait", 15)).c_str());
    }
    if (t == "press") return gas_short(s.code("gas", "A"));
    if (t == "hold") return s.code("until", "time") == "time" ? "" : trf("最长 %s min", N(s.num("maxWait", 60)).c_str());
    if (t == "react") {
        if (s.code("mmolOn", "off") == "on") return trf("吸气 %s mmol", N(s.num("mmol", 5)).c_str());
        if (s.code("stallOn", "on") == "on") return tr("吸气停止");
        return "";
    }
    if (t == "vent") {
        int f = (int)std::lround(s.num("flush", 3));
        return f > 0 ? trf("置换 × %d", f) : "";
    }
    return "";
}

std::string step_brief(const std::string& t) {
    if (t == "leak") return "用惰性气体充到试验压力，静置后保压，按压降速率判定";
    if (t == "exchange") return "充到「充到」再放到「放到」以下，重复几次换掉釜里的气";
    if (t == "stir") return "设定釜里的转速，立即进入下一步，一直保持到下一个搅拌";
    if (t == "heat") return "升到目标温度，进入到位范围后再平衡一段时间";
    if (t == "press") return "用所选气体充到目标压力，之后可自动稳压补气";
    if (t == "hold") return "什么都不动，等时间到，或等温度 / 压力到位";
    if (t == "react") return "不动阀不改温，先到者结束：最长时间到，或吸气量达到，或吸气停止";
    if (t == "vent") return "开阀放到设定压力以下，之后可用惰性气体置换几次再开釜";
    if (t == "end") return "固定的最后一步：关加热、关全部阀、释放搅拌、停止计量；不降温";
    return "";
}

double curve_minutes(const std::vector<HeatSeg>& segs, double start) {
    double total = 0, prev = start;
    for (auto& s : segs) {
        total += (s.rate > 0 ? std::fabs(s.temp - prev) / s.rate : 0) + std::max(0.0, s.hold);
        prev = s.temp;
    }
    return total;
}

// C++: src/core/program/step_fields.cpp step_duration::of（照抄）
DurEst step_minutes(const StepDoc& s) {
    const std::string& t = s.type;
    if (t == "leak") return {s.num("settle", 5) + s.num("hold", 30) + 1};
    if (t == "exchange") return {(s.num("lineFlush", 2) * 9 + s.num("cycles", 3) * 20) / 60.0};
    if (t == "heat") {
        if (s.code("mode", "fixed") == "curve") return {curve_minutes(s.segments)};
        double r = s.num("rate", 0);
        return {std::fabs(s.num("temp", 80) - 25) / (r > 0 ? r : 3.5) + s.num("wait", 15)};
    }
    if (t == "press") return {3};
    if (t == "hold") {
        if (s.code("until", "time") == "time") return {s.num("time", 10)};
        return {std::min(s.num("maxWait", 60), 15.0), true, false};
    }
    if (t == "react") return {s.num("time", 480), false, true};
    if (t == "vent") return {1 + s.num("flush", 3) * 20 / 60.0};
    return {0};
}

DurEst program_minutes(const ProgramDoc& p) {
    DurEst d;
    for (auto& s : p.steps) {
        DurEst x = step_minutes(s);
        d.minutes += x.minutes;
        d.approx = d.approx || x.approx;
        d.upper = d.upper || x.upper;
    }
    return d;
}

std::string fmt_dur_min(double minutes) {
    int m = (int)std::lround(minutes);
    char b[48];
    if (m >= 60) std::snprintf(b, sizeof b, "%d h %02d min", m / 60, m % 60);
    else std::snprintf(b, sizeof b, "%d min", m);
    return b;
}

PlanCurve plan_curve(const ProgramDoc& p) {
    PlanCurve c;
    double t = 0, temp = 25, pr = 0;
    c.temp.push_back({0, temp});
    c.press.push_back({0, pr});
    auto P = [&](double v) { c.press.push_back({t, pr}); pr = v; c.press.push_back({t, pr}); };
    for (auto& s : p.steps) {
        double d = step_minutes(s).minutes;
        double t0 = t;
        const std::string& k = s.type;
        if (k == "leak") {
            P(s.num("pres", 435.1) * kPsiToMpa);
            t += d;
            c.press.push_back({t, pr});
            if (s.code("after", "vent") == "vent") P(0.02);
        } else if (k == "exchange") {
            P(s.num("release", 2.9) * kPsiToMpa);
            t += d;
        } else if (k == "heat") {
            if (s.code("mode", "fixed") == "curve") {
                double prev = temp;
                for (auto& g : s.segments) {
                    double ramp = g.rate > 0 ? std::fabs(g.temp - prev) / g.rate : 0;
                    t += ramp;
                    c.temp.push_back({t, g.temp});
                    t += g.hold;
                    c.temp.push_back({t, g.temp});
                    prev = g.temp;
                }
                temp = prev;
            } else {
                double r = s.num("rate", 0);
                double ramp = std::fabs(s.num("temp", 80) - temp) / (r > 0 ? r : 3.5);
                c.temp.push_back({t, temp});
                t += ramp;
                temp = s.num("temp", 80);
                c.temp.push_back({t, temp});
                t = t0 + d;
            }
        } else if (k == "press") {
            P(s.num("pres", 290.1) * kPsiToMpa);
            t += d;
        } else if (k == "vent") {
            P(s.num("to", 2.9) * kPsiToMpa);
            t += d;
        } else {
            t += d;
        }
        c.stepAt.push_back({t0, t});
    }
    c.temp.push_back({t, temp});
    c.press.push_back({t, pr});
    // 结束：加热关，温度往下（画一小段）
    c.temp.push_back({t + std::max(5.0, t * 0.01), std::max(25.0, temp - 8)});
    c.total = t;
    return c;
}

}  // namespace reactor::hmi::model
