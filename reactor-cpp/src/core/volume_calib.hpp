// 釜有效容积标定（气体膨胀法）的计算。C#: Models/VolumeCalib.cs
// 每步：Pk = (1−f)·P(k−1) + f·Preg，f = Vm/(Vm+Vg)；N 组 (P(k−1), Pk) 直线拟合斜率 a → Vg/Vm = a/(1−a)。
// 首次两遍（空釜 r1、加水 L 后 r2）：Vm = L/(r1−r2)，Veff = r1·Vm；之后单遍：Veff = r·Vm。
// 交互流程（等待操作员加水等）在控制层，这里只有计算。
#pragma once
#include <optional>
#include <string>
#include <utility>
#include <vector>

namespace reactor::core {

namespace volume_calib {
constexpr int kSteps = 6;                                 // 每遍最多充放次数
constexpr int kMinSteps = 3;                              // 拟合至少组数
constexpr double kMinFraction = 0.01, kMaxFraction = 0.9; // f 合理范围
constexpr double kMinR2 = 0.995;
constexpr double kMinRisePsi = 0.5;                       // 每步至少升这么多才用于拟合
constexpr double kMinVolumeMl = 2, kMaxVolumeMl = 2000;

struct Fit {
    double slope = 0, intercept = 0, r2 = 0;
    int n = 0;
    double fraction() const { return 1 - slope; }              // f = 1 − 斜率
    double ratio() const { return slope / (1 - slope); }       // Vg/Vm
    double supply_psi() const { return intercept / fraction(); } // 等效出口压力
};

struct FitResult {
    std::optional<Fit> fit;
    std::optional<std::string> why;  // 不合理时的原因（中文，逐字同 C#）
};

struct TwoRunResult {
    std::optional<double> manifold_ml, effective_ml;
    std::optional<std::string> why;
};

struct OneRunResult {
    std::optional<double> effective_ml;
    std::optional<std::string> why;
};

// 拟合一遍（每组 = 放气前、后的釜压 psi）
FitResult fit_steps(const std::vector<std::pair<double, double>>& steps);
// 两遍解总管体积与有效容积
TwoRunResult solve_two_runs(double r_empty, double r_water, double water_ml);
// 已知总管体积，一遍空釜 → 有效容积
OneRunResult solve_one_run(double r_empty, double manifold_ml);
}  // namespace volume_calib

}  // namespace reactor::core
