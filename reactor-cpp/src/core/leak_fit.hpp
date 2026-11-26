// 泄漏测试的逐点数据处理：最小二乘拟合 + 温度补偿到绝对压力。C#: Models/LeakFit.cs
// P_corr = P_abs × T_ref / T（K），T_ref = 测量窗口起点釜温；大气压取标准 101.325 kPa。
// 逐点记录 / 汇总 / CSV（C# LeakRecord）属于存储模块，不在这里。
#pragma once
#include <cmath>
#include <utility>
#include <vector>

namespace reactor::core {

// y = intercept + slope·x；点数 < 2 或 x 无变化时 slope / intercept / r2 为 NaN
struct LineFit {
    double slope = NAN, intercept = NAN, r2 = NAN;
    int n = 0;
    bool valid() const { return !std::isnan(slope); }
};

namespace leak_fit {
constexpr double kKpaPerPsi = 6.894757;
constexpr double kAtmKpa = 101.325;
constexpr double kKelvin = 273.15;

// 最小二乘（去均值累加）；所有 y 相同时 r2 = 1
LineFit fit(const std::vector<std::pair<double, double>>& pts);
// 按 (秒, 值) 拟合，斜率换算成每小时（截距不变）
LineFit fit_per_hour(const std::vector<std::pair<double, double>>& pts);
// 表压 psig → 绝压 kPa
double psig_to_abs_kpa(double psig);
// 温度补偿后的绝压 kPa；温度无效返回 NaN
double corrected_abs_kpa(double psig, double t_c, double t_ref_c);
}  // namespace leak_fit

}  // namespace reactor::core
