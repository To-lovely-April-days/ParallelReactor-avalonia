// 程序：/program-schema 的结构（照 C# ProgramSchemaDto / StepSchemaDto / FieldSchemaDto），
// 程序 JSON（§6.9：每步 { type, params{…} }，选项是英文码，压力 psi，heat.mode=curve 时 params.segments[{temp,rate,hold}]），
// 以及界面要用的几个纯显示函数（步骤流的两行字、时长估算）。
// 参数区完全按 schema 画（字段、范围、默认值、选项、visibleWhen 都取自后端），这样永远和后端一致；
// 这里不做任何校验，下发时由 reactord 校验（PROGRAM_INVALID / PARAM_OUT_OF_RANGE）。
#pragma once
#include "telemetry.hpp"

#include <map>
#include <string>
#include <vector>

namespace reactor::hmi::model {

// ---- /program-schema（C#: Api/ApiDtos.cs、Api/ProgramSchemaBuilder.cs；C++: src/core/program/program_schema.hpp）----
struct SchemaOption { std::string code, label; bool available = true; std::string reason; };
struct VisibleWhen { std::string key; std::vector<std::string> in; };
struct FieldSchema {
    std::string key, label, kind;  // kind：num / sel / array
    std::string unit;              // 压力字段 "psi" / "psi/h"
    OptD def, min, max, declaredMin, declaredMax;
    std::vector<SchemaOption> options;
    bool available = true;
    std::string reason;
    std::vector<FieldSchema> itemFields;  // array（heat.segments）：temp / rate / hold
    int minItems = 0, maxItems = 0;
    std::optional<VisibleWhen> visibleWhen;
    std::string defaultCode;
    bool is_sel() const { return kind == "sel"; }
    bool is_pressure() const { return unit == "psi" || unit == "psi/h"; }
    bool is_onoff() const;  // 选项正好是 on / off → 界面用开关
    std::string label_of(const std::string& code) const;
};
struct StepSchema {
    std::string type, label, chip, help;
    std::vector<FieldSchema> fields;
    const FieldSchema* field(const std::string& key) const;
};
struct ProgramSchema {
    std::string apiVersion;
    int64_t settingsRevision = 0;
    std::vector<std::string> stepTypes;
    std::vector<StepSchema> steps;
    const StepSchema* step(const std::string& type) const;
    bool empty() const { return steps.empty(); }
};
// 解析 GET /program-schema 的正文；失败返回 false（err 里写原因）
bool parse_program_schema(const std::string& json, ProgramSchema& out, std::string* err = nullptr);

// ---- 程序 JSON ----
struct ParamVal {
    bool isCode = false;
    double num = 0;
    std::string code;
};
struct HeatSeg { double temp = 0, rate = 0, hold = 0; };  // °C、°C/min、min
struct StepDoc {
    std::string type;                         // leak / exchange / stir / heat / press / hold / react / vent / end
    std::map<std::string, ParamVal> params;
    std::vector<HeatSeg> segments;            // heat.mode = curve
    double num(const std::string& k, double def = 0) const;
    std::string code(const std::string& k, const std::string& def = "") const;
    void set_num(const std::string& k, double v) { params[k] = ParamVal{false, v, {}}; }
    void set_code(const std::string& k, const std::string& c) { params[k] = ParamVal{true, 0, c}; }
};
// 程序库条目 / 单路程序（store 的 library/programs/<id>.json 形状，步骤用 §6.9 的 psi 格式）
struct ProgramDoc {
    std::string id, name, model = "PR8";
    std::string modified, by;   // 服务端填（本机 = 「触屏」）
    bool draft = false;
    std::vector<StepDoc> steps;
    int64_t revision = 0;
    std::string note;           // 模板说明 / 程序说明
    std::string from;           // 模板：从哪个程序来
    bool builtIn = false;       // 内置模板（只读）
    int64_t modifiedMs = 0;     // 显示「修改于今天 09:40」用（modified 解析出来）
};
std::string program_to_json(const ProgramDoc& p);              // PUT /channels/{id}/program、/library/*
bool program_from_json(const std::string& json, ProgramDoc& out);

// 新建一步：数值 = schema 默认值，选项 = defaultCode（没有就第一个选项）
StepDoc new_step(const ProgramSchema& s, const std::string& type);
// 按 schema 默认值建一份「加氢筛选」：leak → exchange 惰性 → exchange A → stir → heat → press → react → vent → end
ProgramDoc hydrogenation_template(const ProgramSchema& s);
// 字段当前是否起作用（visibleWhen；控制字段没写时取它的默认码）
bool field_active(const StepSchema& ss, const FieldSchema& f, const StepDoc& st);
// 字段显示方式：起作用 = 正常；不起作用时，由开关控制的、或者「等待上限」这类辅助字段显示成变淡，
// 只属于另一个选项的目标值（hold.temp / hold.pres、heat 定值的几项）直接隐藏。
enum class FieldShow { Normal, Faded, Hidden };
FieldShow field_show(const StepSchema& ss, const FieldSchema& f, const StepDoc& st);

// ---- 显示 ----
std::string zh_step_name(const std::string& type);   // 「气密检查」…（schema 里的 label 优先）
std::string fmt_num(double v, int decimals);
std::string fmt_press_psi(double psi, int decimals = 3);       // psi → 「2.000」(MPa)
int field_decimals(const FieldSchema& f);                       // 显示小数位（压力 3，速率 1 …）
std::string unit_label(const FieldSchema& f);                   // 「MPa」「min」「s」「°C」…
double to_display(const FieldSchema& f, double stored);         // psi → MPa；其他不变
double from_display(const FieldSchema& f, double shown);
// 步骤流里的两行（Prog-1：「3.000 MPa」/「保压 30 min」）
std::string step_value_line(const StepDoc& s);
std::string step_sub_line(const StepDoc& s);
// 参数区标题旁的一句短说明
std::string step_brief(const std::string& type);
// 时长估算（分钟，照 core step_duration）。approx：含按条件结束的保持；upper：含反应（「≤」）
struct DurEst { double minutes = 0; bool approx = false, upper = false; };
DurEst step_minutes(const StepDoc& s);
DurEst program_minutes(const ProgramDoc& p);
double curve_minutes(const std::vector<HeatSeg>& segs, double start_temp = 25);
std::string fmt_dur_min(double minutes);  // 「9 h 14 min」「40 min」

// 预计曲线（Prog-1 底部、程序库预览）：按每一步的设定估算出温度 / 压力随时间的拐点
struct PlanCurve {
    std::vector<std::pair<double, double>> temp;   // (分钟, °C)
    std::vector<std::pair<double, double>> press;  // (分钟, MPa)
    std::vector<std::pair<double, double>> stepAt; // 每步 (开始分钟, 结束分钟)
    double total = 0;
};
PlanCurve plan_curve(const ProgramDoc& p);

}  // namespace reactor::hmi::model
