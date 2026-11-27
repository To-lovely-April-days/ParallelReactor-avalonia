// 吸气量计量：按釜内压降折算 mmol（理想气体 n = P绝·V气/(R·T)）。C#: Models/GasUptake.cs（GasUptakeMeter）
// 以及 ViewModels/MainViewModel.Uptake.cs 里选气相体积的纯逻辑（已标定 → 有效容积 − 溶液；未标定 → 釜 − 溶液 + 管路）。
//  - 每周期累加「上一次气相 mmol − 这一次」（首尾相消，噪声不累积）；
//  - SV 打开过（sv_gen 变）→ 断基准，SV 关满 2 s 后重取基准（补气不算消耗，refills++）；SV 开着不计；
//  - 读数无效的周期跳过但不断基准；气相体积变了 → 重取基准；气相体积 < 0.5 mL 不计；
//  - 速率 = 最近 60 s 窗口差分（跨度 ≥10 s 才给），mmol/min。
// 与 C# 的差别：时刻用单调毫秒 t_ms（C# 是 Stopwatch 秒 double）。
#pragma once
#include <cstdint>
#include <deque>
#include <optional>
#include <string>
#include <utility>

namespace reactor::core {

namespace gas_uptake {
constexpr double kAtmPsi = 14.6959;          // C#: GasUptakeMeter.AtmPsi
constexpr double kPaPerPsi = 6894.757293;    // C#: PaPerPsi
constexpr double kR = 8.314462618;           // C#: R J/(mol·K)
constexpr double kAmbientTempC = 25.0;       // 釜外未加热管路与变送器死体积的环境温度 ℃
constexpr double kSettleSec = 2.0;           // SV 关闭后静置多久才重取基准
constexpr double kMaxRefillBlindSec = 30.0;  // 稳压补气盲区速率外推补偿的最长窗口 s
constexpr double kRateWindowSec = 60;
constexpr double kRateMinSpanSec = 10;
constexpr double kMinGasVolumeMl = 0.5;

struct GasVolumeZones {
    double total_ml = 0;  // 总气相体积 mL（= hot_ml + cold_ml）
    double hot_ml = 0;    // 釜内受热气相体积 mL（随釜温 temp_c 变化）
    double cold_ml = 0;   // 釜外未加热管路/接头死体积 mL（按环境温度 ambient_c 计）
};

// 气相体积 mL = 釜容积 − max(0, 溶液) + max(0, 管路)
double gas_volume_ml(double vessel_ml, double solution_ml, double line_ml);
// 气相 mmol（单温区：表压 psi、温度 ℃、体积 mL）
double gas_mmol(double psi_gauge, double temp_c, double vol_ml);
// 冷热双温区气相 mmol：n = P绝·(V热/T釜 + V冷/T环)/R
double gas_mmol_two_zone(double psi_gauge, double temp_c, double hot_vol_ml, double cold_vol_ml,
                         double ambient_c = kAmbientTempC);
// 压力分辨率 d_psi 对应的 mmol（25 ℃），界面说明用
double resolution_mmol(double d_psi, double vol_ml);
// 某通道用于计量的气相体积：effective_ml（已标定有效容积）有值 → effective − max(0, 溶液)；否则 gas_volume_ml(...)
double channel_gas_volume_ml(std::optional<double> effective_ml, double solution_ml, double vessel_ml, double line_ml);
// 拆分通道的受热釜内气相体积与釜外常温死体积（已标定时死体积 = max(0, effective − vessel)，未标定时 = max(0, line)）
GasVolumeZones split_channel_gas_volume_ml(std::optional<double> effective_ml, double solution_ml, double vessel_ml,
                                           double line_ml);
// 气相体积来由的说明文字（C#: MainViewModel.VolumeBasis，事件用）
std::string volume_basis_text(std::optional<double> effective_ml, double solution_ml, double vessel_ml, double line_ml);
}  // namespace gas_uptake

class GasUptakeMeter {
public:
    bool counting() const { return counting_; }
    bool valid() const { return valid_; }
    double mmol() const { return mmol_; }
    std::optional<double> rate_mmol_per_min() const { return rate_; }
    int refills() const { return refills_; }
    std::optional<double> gas_volume() const { return gas_volume_; }

    // 开始计量（已在计量返回 false，不清零）
    bool start(int64_t t_ms, int64_t sv_gen);
    // 停止，保留读数
    void stop();
    // 停止并清空（新程序）
    void reset();
    // 一个采样周期；返回本周期是否更新了累计值。
    // cold_vol_ml：总气相体积中处于釜外常温区的死体积；compensate_refill：稳压补气期间按补气前速率补偿盲区消耗
    bool sample(int64_t t_ms, bool readings_ok, double psi_gauge, double temp_c, double gas_vol_ml, bool sv_open,
                int64_t sv_gen, double cold_vol_ml = 0.0, double ambient_c = gas_uptake::kAmbientTempC,
                bool compensate_refill = false);

private:
    bool counting_ = false, valid_ = false;
    double mmol_ = 0;
    std::optional<double> rate_;
    int refills_ = 0;
    std::optional<double> gas_volume_;
    std::optional<double> cold_volume_;
    std::optional<double> prev_n_;
    std::optional<double> last_sample_t_;
    std::optional<double> pre_blind_t_;
    double pre_blind_n_ = 0;
    double pre_blind_rate_ = 0;
    int64_t gen_ = 0;
    std::optional<double> closed_since_;  // 秒
    std::deque<std::pair<double, double>> hist_;  // (秒, 累计 mmol)
};

}  // namespace reactor::core
