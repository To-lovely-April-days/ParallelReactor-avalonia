// 各数值字段的范围校验（C5）。C#: Models/ValueRules.cs
// 返回 nullopt = 合法；NaN / ∞ 一律拒绝。错误码一律 PARAM_OUT_OF_RANGE，文案与 C# 逐字相同。
// 依赖设置的上限（TMaxSp、VolMax、RpmMax、OverTemp…）由调用方作为参数传入。
#pragma once
#include <optional>
#include <string>
#include <utility>
#include <vector>

namespace reactor::core {

// 数值越界：错误码 + 中文消息 + detail（min / max / value）
struct RuleViolation {
    std::string code;
    std::string message;
    double min = 0, max = 0, value = 0;
};

namespace value_rules {
constexpr double kTempSpMin = 10;
constexpr double kVolumeMin = 1;
constexpr double kPidPMax = 3200, kPidIMax = 3200, kPidDMax = 327;
constexpr int kProfileSegMin = 1;
constexpr double kSegMinutesMin = 0.1, kSegMinutesMax = 6000;
constexpr double kAutotuneSpMin = 40;
constexpr double kBandBoundMin = 10, kBandBoundMax = 390, kBandGap = 10;
constexpr int kVolumeDecimals = 2;  // 溶液体积保存 0.01 mL

// 通用闭区间检查；unit 为空时不带单位。文案「{label}须在 {min}–{max}{ unit} 之间」
std::optional<RuleViolation> range(const std::string& label, double v, double min, double max,
                                   const std::string& unit = "");
// 温度 SP：10..t_max_sp °C
std::optional<RuleViolation> temp_sp(double v, double t_max_sp);
// 严格守卫：温度 SP 须低于超温报警值
std::optional<RuleViolation> strict_temp_sp(double v, double over_temp);
// 溶液体积：1..vol_max mL
std::optional<RuleViolation> volume(double ml, double vol_max);
// 溶液体积取整到 2 位小数（远离零）
double round_volume(double ml);
// 全局搅拌转速：0..rpm_max
std::optional<RuleViolation> rpm(double rpm, double rpm_max);
// PID：档位 0..band_count−1、P 0..3200、I 0..3200 s、D 0..327 s（按此顺序报第一个）
std::optional<RuleViolation> pid(int band, double p, double i, double d, int band_count = 3);
// 曲线段数：1..max
std::optional<RuleViolation> profile_seg_count(double n, int max = 16);
// 曲线某段：温度 0..t_max_sp、时长 0.1..6000 分钟（nullopt 的项不检查）
std::optional<RuleViolation> profile_segment(std::optional<double> temp_c, std::optional<double> minutes,
                                             double t_max_sp);
// 自整定目标温度：40..t_max_sp
std::optional<RuleViolation> autotune_sp(double v, double t_max_sp);
// 自整定显式档位：档位 0..N−1 且温度落在该档温区（非最高档 [lo, hi)，最高档 [lo, t_max_sp]）
std::optional<RuleViolation> autotune_band(int band, double temp_c, const std::vector<std::pair<double, double>>& bands,
                                           double t_max_sp);
// 档位分界：which=1 低/中（10..mid_hi−10），which=2 中/高（low_hi+10..390）
std::optional<RuleViolation> band_bound(int which, double value, double low_hi, double mid_hi);
}  // namespace value_rules

}  // namespace reactor::core
