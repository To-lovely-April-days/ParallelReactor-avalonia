#include "core/volume_calib.hpp"

#include <algorithm>
#include <cmath>

#include "core/num_format.hpp"
#include <optional>
#include <string>
#include <utility>
#include <vector>

namespace reactor::core::volume_calib {

FitResult fit_steps(const std::vector<std::pair<double, double>>& all) {
    for (auto& s : all)
        if (!std::isfinite(s.first) || !std::isfinite(s.second)) return {std::nullopt, std::string("压力读数无效")};
    // 只用压力明显上升的步骤
    std::vector<std::pair<double, double>> used;
    for (auto& s : all)
        if (s.second - s.first >= kMinRisePsi) used.push_back(s);
    if (static_cast<int>(used.size()) < kMinSteps) {
        double max_rise = 0;
        if (!all.empty()) {
            max_rise = all[0].second - all[0].first;
            for (auto& s : all) max_rise = std::max(max_rise, s.second - s.first);
        }
        std::string why = max_rise < kMinRisePsi
                              ? "每步压力只升了 " + fmt_dec(max_rise, 2) + " psi · 惰性气体减压阀压力太低或气源没开"
                              : "有效数据只有 " + std::to_string(used.size()) + " 组（至少 " + std::to_string(kMinSteps) +
                                    " 组）· 把惰性气体减压阀调高一些再试";
        return {std::nullopt, why};
    }
    int n = static_cast<int>(used.size());
    double mx = 0, my = 0;
    for (auto& s : used) { mx += s.first; my += s.second; }
    mx /= n;
    my /= n;
    double sxx = 0, sxy = 0, syy = 0;
    for (auto& s : used) {
        sxx += (s.first - mx) * (s.first - mx);
        sxy += (s.first - mx) * (s.second - my);
        syy += (s.second - my) * (s.second - my);
    }
    if (sxx <= 0 || syy <= 0) return {std::nullopt, std::string("压力没有变化")};
    double a = sxy / sxx, b = my - a * mx;
    double r2 = sxy * sxy / (sxx * syy);
    Fit fit{a, b, r2, n};
    if (fit.fraction() < kMinFraction || fit.fraction() > kMaxFraction)
        return {std::nullopt, "每步进气比例 " + fmt_dec(fit.fraction(), 3) + " 不合理（应在 " + fmt_dec(kMinFraction, 15) +
                                  "–" + fmt_dec(kMaxFraction, 15) + "）"};
    if (r2 < kMinR2)
        return {std::nullopt, "读数一致性不够（R² = " + fmt_dec(r2, 4, 4) + " < " + fmt_dec(kMinR2, 15) +
                                  "）· 可能有泄漏或温度没稳定"};
    return {fit, std::nullopt};
}

TwoRunResult solve_two_runs(double r_empty, double r_water, double water_ml) {
    if (!(water_ml > 0)) return {std::nullopt, std::nullopt, std::string("加水体积必须大于 0")};
    if (!(r_empty > r_water) || r_water <= 0)
        return {std::nullopt, std::nullopt, std::string("加水后的比值没有变小 · 检查是否真的加了水、加水体积是否填对")};
    double vm = water_ml / (r_empty - r_water);
    double veff = r_empty * vm;
    if (veff < kMinVolumeMl || veff > kMaxVolumeMl || vm < 0.1 || vm > kMaxVolumeMl)
        return {std::nullopt, std::nullopt,
                "结果不合理（总管 " + fmt_dec(vm, 1) + " mL、有效容积 " + fmt_dec(veff, 1) + " mL）· 检查加水体积"};
    if (water_ml >= veff)
        return {std::nullopt, std::nullopt,
                "加水 " + fmt_dec(water_ml, 1) + " mL 不小于算出的有效容积 " + fmt_dec(veff, 1) + " mL · 检查加水体积"};
    return {vm, veff, std::nullopt};
}

OneRunResult solve_one_run(double r_empty, double manifold_ml) {
    if (!(manifold_ml > 0)) return {std::nullopt, std::string("还没有总管体积 · 先做一次「空釜 + 加水」两遍标定")};
    double veff = r_empty * manifold_ml;
    if (veff < kMinVolumeMl || veff > kMaxVolumeMl)
        return {std::nullopt, "结果不合理（有效容积 " + fmt_dec(veff, 1) + " mL）"};
    return {veff, std::nullopt};
}

}  // namespace reactor::core::volume_calib
