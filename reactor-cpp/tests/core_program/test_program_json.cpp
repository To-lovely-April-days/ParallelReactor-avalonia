// C#: tests/ParallelReactor.Tests/ProgramJsonTests.cs、ProgramStepFormatTests.cs
#include <algorithm>

#include "core/program/program_json.hpp"
#include "core/program/program_limits.hpp"
#include "core/program/program_schema.hpp"
#include "core/program/program_step_format.hpp"
#include "core/program/program_types.hpp"
#include "testing.hpp"
#include <cmath>
#include <string>
#include <tuple>
#include <vector>

using namespace reactor::core;
using namespace reactor::core::program;
using json = nlohmann::json;
using ojson = nlohmann::ordered_json;

namespace {

const ProgramLimitSettings kDef{200, 250, 450, 500, 510};

ProgramParseResult Parse(const std::string& text, double leak_rate = 2) { return program_json::parse(json::parse(text), leak_rate); }

std::vector<StepType> types(const std::vector<StepSnapshot>& s) {
    std::vector<StepType> t;
    for (const auto& x : s) t.push_back(x.type());
    return t;
}

std::string code_of(const ProgramParseResult& r) { return r.error ? r.error->code : std::string("<ok>"); }

// 参数按键排序后拼成 "k=v,…"（C# 测试里的 OrderBy(kv.Key) + ToString）
std::string params_text(const StepSnapshot& s) {
    std::vector<std::string> items;
    for (const auto& kv : s.params()) {
        std::string v = std::holds_alternative<double>(kv.second) ? fmt_n(std::get<double>(kv.second)) : std::get<std::string>(kv.second);
        items.push_back(kv.first + "=" + v);
    }
    std::sort(items.begin(), items.end());
    std::string out;
    for (const auto& i : items) out += i + ",";
    return out;
}

StepSnapshot Step(StepType t, const std::string& name, std::initializer_list<Params::Item> p) {
    return StepSnapshot::create(t, 1, name, name + " 摘要", Params(p));
}

const ojson* find_by(const ojson& arr, const char* key, const std::string& value) {
    for (const auto& x : arr)
        if (x.at(key) == value) return &x;
    return nullptr;
}

}  // namespace

// ============ ProgramJsonTests ============

TEST(core_program_json, NewFormat_ParsesWithDefaults_EndAppended) {
    auto r = Parse(R"([ { "type": "leak", "params": { "pres": 400 } },
                        { "type": "exchange", "params": { "gas": "A", "cycles": 2 } },
                        { "type": "stir", "params": { "rpm": 400 } },
                        { "type": "hold", "params": { "until": "tempBelow", "temp": 45 } },
                        { "type": "react", "params": { "stallOn": "on", "minTime": 10 } } ])",
                   3);
    CHECK(!r.error.has_value());
    CHECK(!r.migrated);
    const auto& s = *r.steps;
    CHECK(types(s) == (std::vector<StepType>{StepType::Leak, StepType::Exchange, StepType::Stir, StepType::Hold, StepType::React, StepType::End}));
    CHECK_EQ(s[0].num("pres"), 400.0);
    CHECK_EQ(s[0].num("maxDrop"), 3.0);   // 缺省 = 设备设置 LeakRate
    CHECK_EQ(s[0].str("onFail"), std::string("stop"));
    CHECK_EQ(s[1].str("gas"), std::string("A"));
    CHECK_EQ(s[1].num("fill"), 72.5);
    CHECK_EQ(s[3].str("until"), std::string("tempBelow"));
    CHECK_EQ(s[5].ordinal(), 6);
    CHECK(program_limits::validate(s, kDef).empty());
}

TEST(core_program_json, FlatParams_AndChineseLabels_Accepted) {
    auto r = Parse(R"([ { "type": "press", "gas": "惰性气体", "pres": 100, "regulate": "关闭" } ])");
    CHECK(!r.error.has_value());
    CHECK_EQ((*r.steps)[0].str("gas"), std::string("inert"));
    CHECK_EQ((*r.steps)[0].str("regulate"), std::string("off"));
}

TEST(core_program_json, Legacy_ConvertedAndFlagged) {
    auto r = Parse(R"([ { "type": "purge", "params": { "gas": "惰性气体", "cycles": 5, "fill": 3, "vent": 5 } },
                        { "type": "heat", "params": { "mode": "fixed", "temp": 120, "wait": 10 } },
                        { "type": "press", "params": { "gas": "A", "pres": 100, "regulate": "on" } },
                        { "type": "wait", "params": { "mode": "pressure", "time": 20 } },
                        { "type": "stir", "params": { "rpm": 300, "dur": 5 } },
                        { "type": "react", "params": { "endBy": "either", "time": 120, "mmol": 2 } },
                        { "type": "end", "params": { "mode": "vent", "cool": 40 } } ])");
    CHECK(!r.error.has_value());
    CHECK(r.migrated);
    const auto& s = *r.steps;
    CHECK(types(s) == (std::vector<StepType>{StepType::Exchange, StepType::Heat, StepType::Press, StepType::Hold, StepType::Stir,
                                             StepType::Hold, StepType::Stir, StepType::React, StepType::Hold, StepType::Vent, StepType::End}));
    CHECK_EQ(s[0].str("gas"), std::string("inert"));   // label 先换成码再转换
    CHECK_EQ(s[0].num("cycles"), 0.0);
    CHECK_EQ(s[0].num("lineFlush"), 5.0);
    CHECK_EQ(s[3].str("until"), std::string("pressAbove"));
    CHECK_EQ(s[3].num("pres"), 97.0);
    CHECK_EQ(s[7].str("mmolOn"), std::string("on"));
    CHECK_EQ(s[7].str("stallOn"), std::string("off"));
    CHECK_EQ(s[9].num("flush"), 0.0);
    for (int k = 0; k < 11; ++k) CHECK_EQ(s[static_cast<size_t>(k)].ordinal(), k + 1);
    CHECK(program_limits::validate(s, kDef).empty());
}

TEST(core_program_json, Legacy_OutOfNewRange_ClampedWithNote) {
    auto r = Parse(R"([ { "type": "purge", "params": { "cycles": 50 } }, { "type": "react", "params": { "endBy": "time", "time": 0 } } ])");
    CHECK(!r.error.has_value());
    CHECK(r.migrated);
    CHECK_EQ((*r.steps)[0].num("lineFlush"), 10.0);
    CHECK_EQ((*r.steps)[1].num("time"), 1.0);
    CHECK_EQ(r.notes.size(), size_t(2));
    CHECK(r.notes[0].find("先吹扫总管 50") != std::string::npos);
    CHECK_EQ(r.notes[0], std::string("第 1 步（转换后） 置换：先吹扫总管 50次 超出新范围，已改为 10次"));
}

TEST(core_program_json, NewFormat_OutOfRange_Rejected) {
    auto r = Parse(R"([ { "type": "exchange", "params": { "cycles": 50 } } ])");
    CHECK_EQ(code_of(r), std::string(op_codes::kParamOutOfRange));
    CHECK(r.error->message.find("次数") != std::string::npos);
    CHECK_EQ(r.error->message, std::string("第 1 步：次数 50次 超出 0–20次"));
    CHECK_EQ(r.error->detail.dump(), std::string(R"({"step":1,"field":"cycles","value":50,"min":0,"max":20})"));
    CHECK_EQ(code_of(Parse(R"([ { "type": "exchange", "params": { "cycles": 2.5 } } ])")), std::string(op_codes::kBadRequest));
    CHECK_EQ(code_of(Parse(R"([ { "type": "press", "params": { "gas": "N2" } } ])")), std::string(op_codes::kProgramInvalid));
    CHECK_EQ(code_of(Parse(R"([ { "type": "cool" } ])")), std::string(op_codes::kProgramInvalid));   // 没有降温步骤
    CHECK_EQ(code_of(Parse("[]")), std::string(op_codes::kProgramInvalid));
    CHECK_EQ(code_of(Parse(R"({ "type": "end" })")), std::string(op_codes::kBadRequest));
    CHECK_EQ(code_of(Parse(R"([ { "type": "heat", "params": { "mode": "curve" } } ])")), std::string(op_codes::kProgramInvalid));
    // 其他输入错误
    CHECK_EQ(code_of(Parse(R"([ 3 ])")), std::string(op_codes::kBadRequest));
    CHECK_EQ(code_of(Parse(R"([ { "params": {} } ])")), std::string(op_codes::kBadRequest));
    CHECK_EQ(code_of(Parse(R"([ { "type": "stir", "params": { "rpm": "400" } } ])")), std::string(op_codes::kBadRequest));
    CHECK_EQ(code_of(Parse(R"([ { "type": "stir", "params": { "rpm": true } } ])")), std::string(op_codes::kBadRequest));
    CHECK_EQ(code_of(Parse(R"([ { "type": "press", "params": { "gas": 1 } } ])")), std::string(op_codes::kBadRequest));
    auto unk = Parse(R"([ { "type": "cool" } ])");
    CHECK_EQ(unk.error->message,
             std::string("第 1 步：未知步骤类型「cool」（leak / exchange / stir / heat / press / hold / react / vent / end；旧码 purge / wait 自动转换）"));
}

TEST(core_program_json, Curve_SegmentsKept) {
    auto r = Parse(R"([ { "type": "heat", "params": { "mode": "curve", "band": 3, "segments": [ { "temp": 80, "rate": 5, "hold": 10 }, { "temp": 120, "rate": 2, "hold": 30 } ] } } ])");
    CHECK(!r.error.has_value());
    CHECK((*r.steps)[0].is_curve());
    CHECK_EQ((*r.steps)[0].segments().size(), size_t(2));
    CHECK_EQ((*r.steps)[0].num("band"), 3.0);
    CHECK_EQ(code_of(Parse(R"([ { "type": "heat", "params": { "mode": "curve", "segments": [ { "temp": 80, "rate": 99, "hold": 10 } ] } } ])")),
             std::string(op_codes::kParamOutOfRange));
}

TEST(core_program_json, Export_RoundTrips) {
    auto r = Parse(R"([ { "type": "purge", "params": { "cycles": 3 } }, { "type": "hold", "params": { "until": "pressBelow", "pres": 5 } } ])");
    auto dto = program_step_format::format(*r.steps);
    CHECK_EQ(dto.size(), size_t(3));
    CHECK_EQ(dto[0].type, std::string("exchange"));
    CHECK_EQ(dto[1].type, std::string("hold"));
    CHECK_EQ(dto[2].type, std::string("end"));
    CHECK(dto[0].params.at("gas") == "inert");
    CHECK(dto[1].params.at("until") == "pressBelow");
    // 导出的 steps 可以原样再下发，结果相同且不再算转换
    ojson arr = ojson::array();
    for (const auto& d : dto) arr.push_back(ojson{{"type", d.type}, {"params", d.params}});
    auto again = Parse(arr.dump());
    CHECK(!again.error.has_value());
    CHECK(!again.migrated);
    CHECK(types(*r.steps) == types(*again.steps));
    for (size_t k = 0; k < r.steps->size(); ++k) CHECK_EQ(params_text((*r.steps)[k]), params_text((*again.steps)[k]));
}

TEST(core_program_json, Schema_ListsOnlyNineTypes_WithVisibleWhenAndPsi) {
    ojson sc = program_schema::build(kDef, 7, 2.5);
    CHECK(sc.at("stepTypes") == ojson::parse(R"(["leak","exchange","stir","heat","press","hold","react","vent","end"])"));
    CHECK(sc.at("settingsRevision") == 7);
    CHECK(sc.at("apiVersion") == "1.0");
    const ojson& hold = *find_by(sc.at("steps"), "type", "hold");
    const ojson& temp = *find_by(hold.at("fields"), "key", "temp");
    CHECK(temp.at("visibleWhen").at("key") == "until");
    CHECK(temp.at("visibleWhen").at("in") == ojson::parse(R"(["tempAbove","tempBelow"])"));
    CHECK(temp.at("max") == 200);   // 温度上限 min(TMaxSp, OverTemp − 1)
    const ojson& pres = *find_by(hold.at("fields"), "key", "pres");
    CHECK(pres.at("unit") == "psi");
    CHECK_NEAR(pres.at("max").get<double>(), 458.9, 1e-6);   // 压力上限 min(PMaxSp, OP × 0.9 − 0.1)
    CHECK(find_by(hold.at("fields"), "key", "until")->at("defaultCode") == "time");
    const ojson& react = *find_by(sc.at("steps"), "type", "react");
    CHECK(find_by(react.at("fields"), "key", "mmol")->at("visibleWhen").at("in") == ojson::parse(R"(["on"])"));
    CHECK(find_by(react.at("fields"), "key", "time")->at("visibleWhen").is_null());
    CHECK(find_by(find_by(sc.at("steps"), "type", "leak")->at("fields"), "key", "maxDrop")->at("default") == 2.5);
    CHECK(find_by(find_by(sc.at("steps"), "type", "stir")->at("fields"), "key", "rpm")->at("max") == 450);
    CHECK(find_by(sc.at("steps"), "type", "end")->at("fields").empty());
    const ojson& heat = *find_by(sc.at("steps"), "type", "heat");
    CHECK(heat.at("fields")[0].at("key") == "mode");
    const ojson& segs = *find_by(heat.at("fields"), "key", "segments");
    CHECK(segs.at("visibleWhen").at("in") == ojson::parse(R"(["curve"])"));
    CHECK(segs.at("minItems") == 1);
    CHECK(segs.at("maxItems") == 16);
    CHECK_EQ(segs.at("itemFields").size(), size_t(3));
    CHECK(segs.at("itemFields")[0].at("max") == 200);   // 段温度同样封顶
    std::vector<std::string> gas_codes;
    for (const auto& o : find_by(find_by(sc.at("steps"), "type", "press")->at("fields"), "key", "gas")->at("options"))
        gas_codes.push_back(o.at("code").get<std::string>());
    CHECK(gas_codes == (std::vector<std::string>{"inert", "A", "B"}));
    // 旧码不在 schema 里
    CHECK(find_by(sc.at("steps"), "type", "purge") == nullptr);
    CHECK(find_by(sc.at("steps"), "type", "wait") == nullptr);
    // camelCase 序列化：visibleWhen / in
    std::string text = sc.dump();
    CHECK(text.find(R"("visibleWhen":{"key":"until","in":["tempAbove","tempBelow"]})") != std::string::npos);
}

TEST(core_program_json, Schema_FieldShape_MatchesCSharpDto) {
    ojson sc = program_schema::build(kDef, 1);
    const ojson& press = *find_by(sc.at("steps"), "type", "press");
    // 数值字段：17 个键，顺序同 FieldSchemaDto
    const ojson& pres = *find_by(press.at("fields"), "key", "pres");
    CHECK_EQ(pres.dump(), std::string(R"({"key":"pres","label":"目标压力","kind":"num","unit":"psi","default":290.1,"min":7.3,"max":458.9,)"
                                      R"("declaredMin":7.3,"declaredMax":493.1,"options":null,"available":true,"reason":null,"itemFields":null,)"
                                      R"("minItems":null,"maxItems":null,"visibleWhen":null,"defaultCode":null})"));
    const ojson& reg = *find_by(press.at("fields"), "key", "regulate");
    CHECK_EQ(reg.dump(), std::string(R"({"key":"regulate","label":"之后稳压","kind":"sel","unit":null,"default":null,"min":null,"max":null,)"
                                     R"("declaredMin":null,"declaredMax":null,"options":[{"code":"on","label":"开启","available":true,"reason":null},)"
                                     R"({"code":"off","label":"关闭","available":true,"reason":null}],"available":true,"reason":null,)"
                                     R"("itemFields":null,"minItems":null,"maxItems":null,"visibleWhen":null,"defaultCode":"on"})"));
    // 步骤：type / label / chip / help / fields
    std::vector<std::string> keys;
    for (auto it = press.begin(); it != press.end(); ++it) keys.push_back(it.key());
    CHECK(keys == (std::vector<std::string>{"type", "label", "chip", "help", "fields"}));
    std::vector<std::string> root;
    for (auto it = sc.begin(); it != sc.end(); ++it) root.push_back(it.key());
    CHECK(root == (std::vector<std::string>{"apiVersion", "settingsRevision", "stepTypes", "steps"}));
}

// ============ ProgramStepFormatTests ============

TEST(core_program_step_format, Codes_StayCodes_LabelsBecomeCodes_NumbersStayNumbers) {
    std::vector<StepSnapshot> steps = {
        Step(StepType::Exchange, "置换", {{"gas", std::string("inert")}, {"fill", 72.5}, {"release", 2.9}, {"cycles", 3.0}, {"lineFlush", 2.0}, {"maxSec", 60.0}}),
        Step(StepType::Press, "充压", {{"gas", std::string("B")}, {"pres", 50.5}, {"regulate", std::string("off")}}),
        Step(StepType::Hold, "保持", {{"until", std::string("pressAbove")}, {"pres", 14.5}, {"maxWait", 30.0}}),
        Step(StepType::React, "反应", {{"mmolOn", std::string("开启")}, {"time", 120.0}, {"mmol", 1.5}}),   // 界面 label 也换成码
        Step(StepType::End, "结束", {}),
    };
    auto dto = program_step_format::format(steps);
    std::vector<std::string> t;
    for (const auto& d : dto) t.push_back(d.type);
    CHECK(t == (std::vector<std::string>{"exchange", "press", "hold", "react", "end"}));
    CHECK(dto[0].params.at("gas") == "inert");
    CHECK(dto[0].params.at("cycles").is_number());
    CHECK(dto[0].params.at("cycles") == 3);
    CHECK(dto[1].params.at("gas") == "B");
    CHECK(dto[1].params.at("pres") == 50.5);
    CHECK(dto[1].params.at("regulate") == "off");
    CHECK(dto[2].params.at("until") == "pressAbove");
    CHECK(dto[3].params.at("mmolOn") == "on");
    CHECK(dto[4].params.empty());
    CHECK(dto[4].params.is_object());
    CHECK_EQ(dto[0].name, std::string("置换"));
    CHECK_EQ(dto[0].detail, std::string("置换 摘要"));
}

TEST(core_program_step_format, Heat_FixedAndCurve) {
    auto fixed_step = Step(StepType::Heat, "升温", {{"temp", 150.0}, {"wait", 15.0}});   // 没有 mode 参数 → fixed
    auto curve = StepSnapshot::create(StepType::Heat, 2, "升温", "曲线", Params{{"mode", std::string("curve")}, {"temp", 150.0}, {"wait", 10.0}},
                                      {{80, 2, 20}, {150, 1, 60}});
    auto f = program_step_format::format(fixed_step);
    CHECK(f.params.at("mode") == "fixed");
    CHECK(!f.params.contains("segments"));
    CHECK_EQ(f.params.begin().key(), std::string("mode"));
    auto c = program_step_format::format(curve);
    CHECK(c.params.at("mode") == "curve");
    int modes = 0;
    for (auto it = c.params.begin(); it != c.params.end(); ++it)
        if (it.key() == "mode") ++modes;
    CHECK_EQ(modes, 1);   // 不重复输出参数里的 mode
    const auto& segs = c.params.at("segments");
    CHECK_EQ(segs.size(), size_t(2));
    std::vector<std::string> keys;
    for (auto it = segs[0].begin(); it != segs[0].end(); ++it) keys.push_back(it.key());
    CHECK(keys == (std::vector<std::string>{"temp", "rate", "hold"}));
    CHECK(segs[1].at("temp") == 150);
    CHECK(segs[1].at("hold") == 60);
}

TEST(core_program_step_format, Output_SerializesLikeApiJson) {
    auto dto = program_step_format::format(Step(StepType::Stir, "搅拌", {{"rpm", 300.0}}));
    CHECK_EQ(program_step_format::to_json(dto).dump(), std::string(R"({"type":"stir","params":{"rpm":300},"name":"搅拌","detail":"搅拌 摘要"})"));
    CHECK(json_number(std::nan("")).is_null());
    CHECK_EQ(json_number(0.005).dump(), std::string("0.005"));
}
