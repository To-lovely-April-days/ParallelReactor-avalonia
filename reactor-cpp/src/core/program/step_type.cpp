#include "core/program/step_type.hpp"

#include <cctype>
#include <optional>
#include <string>

namespace reactor::core::program {

namespace {
struct Row { StepType t; const char* code; const char* zh; };
// C#: StepTypeCodes.Zh
constexpr Row kRows[] = {
    {StepType::Purge, "purge", "吹扫（旧）"},
    {StepType::Heat, "heat", "升温"},
    {StepType::Press, "press", "充压"},
    {StepType::Stir, "stir", "搅拌"},
    {StepType::Wait, "wait", "等待（旧）"},
    {StepType::React, "react", "反应"},
    {StepType::End, "end", "结束"},
    {StepType::Leak, "leak", "气密检查"},
    {StepType::Exchange, "exchange", "置换"},
    {StepType::Hold, "hold", "保持"},
    {StepType::Vent, "vent", "放空"},
};
}  // namespace

bool is_legacy(StepType t) { return t == StepType::Purge || t == StepType::Wait; }

std::string to_code(StepType t) {
    for (const auto& r : kRows)
        if (r.t == t) return r.code;
    return "";
}

std::optional<StepType> parse_step_type(const std::string& code) {
    size_t b = 0, e = code.size();
    while (b < e && std::isspace(static_cast<unsigned char>(code[b]))) ++b;
    while (e > b && std::isspace(static_cast<unsigned char>(code[e - 1]))) --e;
    std::string s;
    for (size_t i = b; i < e; ++i) s += static_cast<char>(std::tolower(static_cast<unsigned char>(code[i])));
    for (const auto& r : kRows)
        if (s == r.code) return r.t;
    return std::nullopt;
}

std::string zh(StepType t) {
    for (const auto& r : kRows)
        if (r.t == t) return r.zh;
    return "";
}

}  // namespace reactor::core::program
