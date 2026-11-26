// 步骤参数：键 → 值（数值 double 或 选项码 string），保持插入顺序（导出 JSON 的键顺序同 C# Dictionary）。
// C#: Models/StepSnapshot.cs 的 StepParams（Num / Str 的解析规则，ProgramStep 与 StepSnapshot 共用）。
// 压力类参数一律 psi（表压）；选项字段存英文码。
#pragma once
#include <initializer_list>
#include <string>
#include <utility>
#include <variant>
#include <vector>

namespace reactor::core::program {

using ParamValue = std::variant<double, std::string>;

// 升温曲线的一段：目标温度 ℃、升温斜率 ℃/min、保温 min（C# 的 (Temp, Rate, Hold) 元组）
struct Segment {
    double temp = 0;
    double rate = 0;
    double hold = 0;
    bool operator==(const Segment& o) const { return temp == o.temp && rate == o.rate && hold == o.hold; }
};

class Params {
public:
    using Item = std::pair<std::string, ParamValue>;

    Params() = default;
    Params(std::initializer_list<Item> items) { for (const auto& i : items) set(i.first, i.second); }

    bool contains(const std::string& k) const { return find(k) != nullptr; }
    const ParamValue* find(const std::string& k) const;
    // 已有键原位替换（保持顺序），否则追加
    void set(const std::string& k, ParamValue v);
    bool erase(const std::string& k);
    size_t size() const { return items_.size(); }
    bool empty() const { return items_.empty(); }
    std::vector<Item>::const_iterator begin() const { return items_.begin(); }
    std::vector<Item>::const_iterator end() const { return items_.end(); }
    bool operator==(const Params& o) const { return items_ == o.items_; }

private:
    std::vector<Item> items_;
};

// 数值参数：double 直接取；字符串按不变区域解析；缺失或无法解析返回 def。C#: StepParams.Num
double param_num(const Params& p, const std::string& k, double def = 0);
// 字符串参数：只认 string，缺失或类型不符返回 def。C#: StepParams.Str
std::string param_str(const Params& p, const std::string& k, const std::string& def = "");
// 字符串 → double（去首尾空白，整串必须是数字；不收十六进制）
bool parse_double(const std::string& s, double& out);

}  // namespace reactor::core::program
