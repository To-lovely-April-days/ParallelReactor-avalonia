#include "core/program/step_fields.hpp"

#include <algorithm>
#include <cmath>
#include <map>

#include "core/program/step_snapshot.hpp"
#include <optional>
#include <string>
#include <vector>

namespace reactor::core::program {

// ============ FieldDef / StepSpec ============

std::string FieldDef::default_sel() const {
    if (default_code) return *default_code;
    if (options && !options->empty()) return (*options)[0].code;
    return "";
}

std::string FieldDef::label_of(const std::string& code) const {
    if (options)
        for (const auto& o : *options)
            if (o.code == code) return o.label;
    return code;
}

std::optional<std::string> FieldDef::code_of(const std::string& text) const {
    if (options)
        for (const auto& o : *options)
            if (o.code == text || o.label == text) return o.code;
    return std::nullopt;
}

const FieldDef* StepSpec::field(const std::string& key) const {
    for (const auto& f : fields)
        if (f.key == key) return &f;
    return nullptr;
}

namespace step_specs {

namespace {

double Num(const Params& p, const char* k, double def) { return param_num(p, k, def); }
std::string Str(const Params& p, const char* k, const char* def) { return param_str(p, k, def); }
std::string N(double v) { return fmt_n(v); }
std::string P(const Params& p, const char* k, double def, PressureUnit u) { return format_p(Num(p, k, def), u); }

FieldDef num(const char* key, const char* label, std::optional<std::string> unit, double def, double min, double max,
             FieldLimit limit = FieldLimit::None, bool integer = false, bool pressure = false,
             std::optional<VisibleWhen> vis = std::nullopt) {
    FieldDef f;
    f.key = key; f.label = label; f.kind = "num"; f.unit = std::move(unit); f.default_value = def;
    f.min = min; f.max = max; f.limit = limit; f.integer = integer; f.pressure = pressure; f.visible_when = std::move(vis);
    return f;
}

FieldDef sel(const char* type, const char* key, const char* label, const char* def,
             std::optional<VisibleWhen> vis = std::nullopt) {
    FieldDef f;
    f.key = key; f.label = label; f.kind = "sel";
    f.options = schema_codes::options(std::string(type) + "." + key);
    f.default_code = def; f.visible_when = std::move(vis);
    return f;
}

VisibleWhen when(const char* key, std::vector<std::string> codes) { return VisibleWhen{key, std::move(codes)}; }

std::map<StepType, StepSpec> build() {
    const std::vector<std::string> hold_cond = {"tempAbove", "tempBelow", "pressAbove", "pressBelow"};
    std::map<StepType, StepSpec> r;

    {
        StepSpec s;
        s.type = StepType::Leak; s.name = "气密检查"; s.chip = "LEAK";
        s.help = "用惰性气体把这一路充到试验压力，关阀静置后保压计时，按温度补偿后的压降速率判定。保压期间不占用气路，别的路可以同时充气。"
                 "试验压力建议不低于后面反应的压力；「允许压降」默认取设置里的泄漏率阈值。";
        s.fields = {
            num("pres", "试验压力", std::string("psi"), 435.1, 14.5, kPressDeclaredMax, FieldLimit::Pressure, false, true),
            num("settle", "稳定时间", std::string("分钟"), 5, 0, 60),
            num("hold", "保压时间", std::string("分钟"), 30, 5, 600),
            num("maxDrop", "允许压降", std::string("psi/h"), kLeakRateDefault, 0.1, 100, FieldLimit::None, false, true),
            sel("leak", "onFail", "不通过时", "stop"),
            sel("leak", "after", "之后", "vent"),
        };
        s.detail = [](const Params& p, const std::vector<Segment>&, PressureUnit u) {
            return "惰性气体 " + P(p, "pres", 435.1, u) + " · 稳定 " + N(Num(p, "settle", 5)) + " min · 保压 " + N(Num(p, "hold", 30)) + " min" +
                   " · ≤ " + p_rate(Num(p, "maxDrop", kLeakRateDefault), u) + " · 不通过" +
                   (Str(p, "onFail", "stop") == "continue" ? "记录后继续" : "停止这一路") +
                   (Str(p, "after", "vent") == "keep" ? " · 之后保持压力" : " · 之后放空");
        };
        r[s.type] = s;
    }
    {
        StepSpec s;
        s.type = StepType::Exchange; s.name = "置换"; s.chip = "EXCHANGE";
        s.help = "把釜充到「充到」再放到「放到」以下，重复「次数」次，真正换掉釜里的气；可先吹扫总管（SV 关着，把上一种气赶出总管）。"
                 "单次充或放超过「单次最长」就报警停止这一路（充不上去：检查气源与减压阀；放不下来：可能有进气单向阀，不要开釜）。次数 0 = 只吹总管。";
        s.fields = {
            sel("exchange", "gas", "气体", "inert"),
            num("fill", "充到", std::string("psi"), 72.5, 7.3, kPressDeclaredMax, FieldLimit::Pressure, false, true),
            num("release", "放到", std::string("psi"), 2.9, 0, kPressDeclaredMax, FieldLimit::None, false, true),
            num("cycles", "次数", std::string("次"), 3, 0, 20, FieldLimit::None, true),
            num("lineFlush", "先吹扫总管", std::string("次"), 2, 0, 10, FieldLimit::None, true),
            num("maxSec", "单次最长", std::string("秒"), 60, 10, 300),
        };
        s.detail = [](const Params& p, const std::vector<Segment>&, PressureUnit u) {
            std::string gas = gas_label("exchange", Str(p, "gas", "inert"));
            int cycles = static_cast<int>(round_even(Num(p, "cycles", 3)));
            int flush = static_cast<int>(round_even(Num(p, "lineFlush", 2)));
            std::string head = cycles <= 0
                ? gas + " · 只吹总管 " + std::to_string(flush) + " 次（釜不置换）"
                : gas + " · " + P(p, "fill", 72.5, u) + " → " + P(p, "release", 2.9, u) + " × " + std::to_string(cycles);
            return cycles > 0 && flush > 0 ? head + " · 先吹总管 " + std::to_string(flush) + " 次" : head;
        };
        r[s.type] = s;
    }
    {
        StepSpec s;
        s.type = StepType::Stir; s.name = "搅拌"; s.chip = "STIR";
        s.help = "设定釜里的转速（电机 = 釜 × 传动比），立即进入下一步，转速一直保持到下一个「搅拌」或程序结束；0 = 停。"
                 "一台电机带全部釜：同时运行的几路程序转速应一致，改写别的程序正在用的转速会记一条提醒。要「搅 10 分钟」用「搅拌 → 保持 10 min」。";
        s.fields = {num("rpm", "转速", std::string("rpm"), 400, 0, kRpmDeclaredMax, FieldLimit::Rpm, true)};
        s.detail = [](const Params& p, const std::vector<Segment>&, PressureUnit) {
            return Num(p, "rpm", 400) <= 0 ? std::string("停止搅拌") : N(Num(p, "rpm", 400)) + " rpm · 保持到下一个搅拌";
        };
        r[s.type] = s;
    }
    {
        StepSpec s;
        s.type = StepType::Heat; s.name = "升温"; s.chip = "HEAT";
        s.help = "定值：升到目标温度，实测进入「到位范围」后再平衡「到位后平衡」分钟（让溶液温度均匀）；升温速率 0 = 尽快（防过冲逼近）。"
                 "曲线：跑完各段后同样等实测温度到位（最后一段的温度）。超过「最长等待」仍未到位按「超时」处理。加热一直保持到下一个升温或程序结束。";
        FieldDef mode;
        mode.key = "mode"; mode.label = "升温方式"; mode.kind = "sel"; mode.options = schema_codes::options("heat.mode");
        mode.default_code = "fixed"; mode.custom_editor = true;
        s.fields = {
            mode,
            num("temp", "目标温度", std::string("°C"), 80, 35, kTempDeclaredMax, FieldLimit::Temp, false, false, when("mode", {"fixed"})),
            num("rate", "升温速率", std::string("°C/min"), 0, 0, 20, FieldLimit::None, false, false, when("mode", {"fixed"})),
            num("band", "到位范围 ±", std::string("°C"), 2, 0.5, 10),
            num("wait", "到位后平衡", std::string("分钟"), 15, 0, 120, FieldLimit::None, false, false, when("mode", {"fixed"})),
            num("maxWait", "最长等待", std::string("分钟"), 120, 10, 600),
            sel("heat", "onTimeout", "超时", "stop"),
        };
        s.detail = [](const Params& p, const std::vector<Segment>& segs, PressureUnit) {
            std::string band = "±" + N(Num(p, "band", 2)) + "°C";
            if (Str(p, "mode", "fixed") == "curve" && !segs.empty())
                return "16 段曲线 · " + std::to_string(segs.size()) + " 段 · 终温 " + N(segs.back().temp) + "°C · ≈ " +
                       N(round_even(curve_minutes(segs))) + " min · 到位 " + band;
            double rate = Num(p, "rate", 0);
            return "定值 " + N(Num(p, "temp", 80)) + "°C" + (rate > 0 ? " · " + N(rate) + "°C/min" : std::string()) + " · " + band +
                   " · 平衡 " + N(Num(p, "wait", 15)) + " min";
        };
        r[s.type] = s;
    }
    {
        StepSpec s;
        s.type = StepType::Press; s.name = "充压"; s.chip = "PRESS";
        s.help = "用所选气体充到目标压力（从下方逼近，到「允许偏差」以内算到位）；只能升、不能降，已经高于目标时不动并写明。"
                 "稳压开 = 压力低于目标 ×（1 − 补气阈值）时自动补气，一直到下一个充压 / 置换 / 放空 / 气密检查或结束；补进去的气不算吸气量。"
                 "升温会让压力上去，建议先升温到位再充压。";
        s.fields = {
            sel("press", "gas", "气体", "A"),
            num("pres", "目标压力", std::string("psi"), 290.1, 7.3, kPressDeclaredMax, FieldLimit::Pressure, false, true),
            num("tol", "允许偏差", std::string("%"), 2, 0.5, 10),
            sel("press", "regulate", "之后稳压", "on"),
            num("refill", "补气阈值", std::string("%"), 3, 1, 10, FieldLimit::None, false, false, when("regulate", {"on"})),
            num("maxSec", "最长充气", std::string("秒"), 180, 30, 600),
        };
        s.detail = [](const Params& p, const std::vector<Segment>&, PressureUnit u) {
            return gas_label("press", Str(p, "gas", "A")) + " 到 " + P(p, "pres", 290.1, u) + " · 稳压" +
                   (Str(p, "regulate", "on") == "off" ? "关" : "开");
        };
        r[s.type] = s;
    }
    {
        StepSpec s;
        s.type = StepType::Hold; s.name = "保持"; s.chip = "HOLD";
        s.help = "什么都不动，等条件：时间到，或温度 / 压力达到步骤里写的目标值（每 1 s 判断一次）。"
                 "按温度 / 压力等待时，超过「最长等待」或传感器离线超过 60 s 按「超时」处理。";
        s.fields = {
            sel("hold", "until", "结束条件", "time"),
            num("time", "时间", std::string("分钟"), 10, 0, 1440, FieldLimit::None, false, false, when("until", {"time"})),
            num("temp", "目标温度", std::string("°C"), 40, 0, kTempDeclaredMax, FieldLimit::Temp, false, false,
                when("until", {"tempAbove", "tempBelow"})),
            num("pres", "目标压力", std::string("psi"), 14.5, 0, kPressDeclaredMax, FieldLimit::Pressure, false, true,
                when("until", {"pressAbove", "pressBelow"})),
            num("maxWait", "最长等待", std::string("分钟"), 60, 1, 1440, FieldLimit::None, false, false, when("until", hold_cond)),
            sel("hold", "onTimeout", "超时", "continue", when("until", hold_cond)),
        };
        s.detail = [](const Params& p, const std::vector<Segment>&, PressureUnit u) {
            std::string until = Str(p, "until", "time");
            std::string cap = " · 最长 " + N(Num(p, "maxWait", 60)) + " min";
            if (until == "tempAbove") return "温度 ≥ " + N(Num(p, "temp", 40)) + "°C" + cap;
            if (until == "tempBelow") return "温度 ≤ " + N(Num(p, "temp", 40)) + "°C" + cap;
            if (until == "pressAbove") return "压力 ≥ " + P(p, "pres", 14.5, u) + cap;
            if (until == "pressBelow") return "压力 ≤ " + P(p, "pres", 14.5, u) + cap;
            return "时间 " + N(Num(p, "time", 10)) + " min";
        };
        r[s.type] = s;
    }
    {
        StepSpec s;
        s.type = StepType::React; s.name = "反应"; s.chip = "REACT";
        s.help = "不动阀、不改温度：温度靠前面的升温保持，压力靠充压的稳压，搅拌靠前面的搅拌。每 2 s 判断一次，先到者结束：最长时间到；"
                 "过了最短时间且吸气量达到目标；过了最短时间且吸气速率连续「持续」分钟低于「停止速率」。反应前没有充压时吸气量不计量，只按最长时间结束。";
        s.fields = {
            num("time", "最长时间", std::string("分钟"), 480, 1, 4320),
            num("minTime", "最短时间", std::string("分钟"), 30, 0, 4320),
            sel("react", "mmolOn", "吸气量达到", "off"),
            num("mmol", "目标吸气量", std::string("mmol"), 5, 0.01, 100, FieldLimit::None, false, false, when("mmolOn", {"on"})),
            sel("react", "stallOn", "吸气停止", "on"),
            num("stallRate", "停止速率", std::string("mmol/min"), 0.005, 0.001, 1, FieldLimit::None, false, false, when("stallOn", {"on"})),
            num("stallMin", "持续", std::string("分钟"), 30, 5, 240, FieldLimit::None, false, false, when("stallOn", {"on"})),
        };
        s.detail = [](const Params& p, const std::vector<Segment>&, PressureUnit) {
            std::string out = "最长 " + N(Num(p, "time", 480)) + " min · 最短 " + N(Num(p, "minTime", 30)) + " min";
            if (Str(p, "mmolOn", "off") == "on") out += " · 吸气量 " + N(Num(p, "mmol", 5)) + " mmol";
            if (Str(p, "stallOn", "on") == "on")
                out += " · 吸气停止 < " + N(Num(p, "stallRate", 0.005)) + " mmol/min 持续 " + N(Num(p, "stallMin", 30)) + " min";
            return out;
        };
        r[s.type] = s;
    }
    {
        StepSpec s;
        s.type = StepType::Vent; s.name = "放空"; s.chip = "VENT";
        s.help = "开 SVn + 排空阀，读到釜压 ≤「放到」才算放完；超过「最长时间」还放不下来就报警停止这一路（釜里的气放不掉，不要开釜）。"
                 "之后可用惰性气体置换几次，把残余的反应气赶走再开釜。釜温高于 60 °C 时放空会带出溶剂蒸气（不拦，可在前面加「保持 · 温度 ≤ x」）。";
        s.fields = {
            num("to", "放到", std::string("psi"), 2.9, 0, 72.5, FieldLimit::None, false, true),
            num("maxSec", "最长时间", std::string("秒"), 120, 10, 600),
            num("flush", "之后惰性置换", std::string("次"), 3, 0, 10, FieldLimit::None, true),
            num("flushPres", "置换充到", std::string("psi"), 43.5, 7.3, kPressDeclaredMax, FieldLimit::Pressure, false, true),
        };
        s.detail = [](const Params& p, const std::vector<Segment>&, PressureUnit u) {
            int flush = static_cast<int>(round_even(Num(p, "flush", 3)));
            return "放到 " + P(p, "to", 2.9, u) + " · " +
                   (flush > 0 ? "之后惰性置换 × " + std::to_string(flush) + "（" + P(p, "flushPres", 43.5, u) + "）" : std::string("不置换"));
        };
        r[s.type] = s;
    }
    {
        StepSpec s;
        s.type = StepType::End; s.name = "结束"; s.chip = "END";
        s.help = "固定的最后一步（不能删、不能挪）：关加热、所有阀关、释放搅拌（别的路还在用就继续转）、停止吸气计量、写结束原因。"
                 "不降温：结束时釜可能还是热的；需要等凉了再放空，可在放空前加「保持 · 温度 ≤ x」。";
        s.detail = [](const Params&, const std::vector<Segment>&, PressureUnit) { return std::string("关加热、阀全关、停搅拌"); };
        r[s.type] = s;
    }
    return r;
}

const std::map<StepType, StepSpec>& registry() {
    static const std::map<StepType, StepSpec> r = build();
    return r;
}

}  // namespace

const StepSpec* find(StepType t) {
    auto it = registry().find(t);
    return it == registry().end() ? nullptr : &it->second;
}

const StepSpec& of(StepType t) { return registry().at(t); }

Params defaults(StepType t) {
    Params d;
    const StepSpec* spec = find(t);
    if (!spec) return d;
    for (const auto& f : spec->fields) {
        if (f.is_sel()) d.set(f.key, f.default_sel());
        else if (f.default_value) d.set(f.key, *f.default_value);
    }
    return d;
}

bool is_visible(StepType t, const FieldDef& f, const Params& p) {
    if (!f.visible_when) return true;
    const VisibleWhen& w = *f.visible_when;
    std::string cur = param_str(p, w.key, "");
    if (cur.empty()) {
        if (const StepSpec* spec = find(t))
            if (const FieldDef* c = spec->field(w.key)) cur = c->default_sel();
    }
    return std::find(w.in.begin(), w.in.end(), cur) != w.in.end();
}

std::string detail(StepType t, const Params& p, const std::vector<Segment>& segs, PressureUnit unit) {
    const StepSpec* spec = find(t);
    return spec ? spec->detail(p, segs, unit) : zh(t);
}

double curve_minutes(const std::vector<Segment>& segs, double start_temp) {
    double total = 0, prev = start_temp;
    for (const auto& s : segs) {
        total += (s.rate > 0 ? std::fabs(s.temp - prev) / s.rate : 0) + std::max(0.0, s.hold);
        prev = s.temp;
    }
    return total;
}

std::string gas_label(const std::string& type, const std::string& code) {
    auto l = schema_codes::label_of(type, "gas", code);
    return l ? *l : code;
}

int gas_index(const std::string& code) { return code == "A" ? 1 : code == "B" ? 2 : 0; }

std::string p_rate(double psi_per_hour, PressureUnit unit) {
    return p_value(psi_per_hour, unit) + " " + pressure_label(unit) + "/h";
}

}  // namespace step_specs

// ============ 时长估算 ============

std::string DurationEstimate::text() const {
    int total = static_cast<int>(round_even(minutes));
    return std::string(approx ? "约 " : "") + (upper_bound ? "≤ " : "") + std::to_string(total / 60) + "h " +
           std::to_string(total % 60) + "m";
}

namespace step_duration {

DurationEstimate of(StepType t, const Params& p, const std::vector<Segment>& segs) {
    auto Num = [&](const char* k, double d) { return param_num(p, k, d); };
    switch (t) {
        case StepType::Leak: return {Num("settle", 5) + Num("hold", 30) + 1, false, false};
        case StepType::Exchange: return {(Num("lineFlush", 2) * 9 + Num("cycles", 3) * 20) / 60.0, false, false};
        case StepType::Heat: {
            if (param_str(p, "mode", "fixed") == "curve") return {step_specs::curve_minutes(segs), false, false};
            double rate = Num("rate", 0);
            return {std::fabs(Num("temp", 80) - 25) / (rate > 0 ? rate : 3.5) + Num("wait", 15), false, false};
        }
        case StepType::Press: return {3, false, false};
        case StepType::Hold:
            return param_str(p, "until", "time") == "time" ? DurationEstimate{Num("time", 10), false, false}
                                                          : DurationEstimate{std::min(Num("maxWait", 60), 15.0), true, false};
        case StepType::React: return {Num("time", 480), false, true};
        case StepType::Vent: return {1 + Num("flush", 3) * 20 / 60.0, false, false};
        default: return {0, false, false};   // stir / end（旧码不估算）
    }
}

DurationEstimate of(const StepSnapshot& s) { return of(s.type(), s.params(), s.segments()); }

DurationEstimate total(const std::vector<StepSnapshot>& steps) {
    DurationEstimate sum;
    for (const auto& s : steps) sum = sum + of(s);
    return sum;
}

}  // namespace step_duration

// ============ 模板 ============

namespace step_templates {

namespace {
TemplateStep T(StepType t, std::initializer_list<Params::Item> kv = {}) {
    Params p = step_specs::defaults(t);
    for (const auto& i : kv) p.set(i.first, i.second);
    return TemplateStep{t, p};
}
}  // namespace

std::vector<TemplateStep> hydrogenation(double leak_rate) {
    return {
        T(StepType::Leak, {{"maxDrop", leak_rate}}),
        T(StepType::Exchange, {{"gas", std::string("inert")}}),
        T(StepType::Exchange, {{"gas", std::string("A")}}),
        T(StepType::Stir),
        T(StepType::Heat),
        T(StepType::Press),
        T(StepType::React),
        T(StepType::Vent),
        T(StepType::End),
    };
}

}  // namespace step_templates

}  // namespace reactor::core::program
