#include "core/gas_uptake.hpp"

#include <algorithm>
#include <cmath>

#include "core/num_format.hpp"
#include <cstdint>
#include <optional>
#include <string>

namespace reactor::core {

namespace gas_uptake {

double gas_volume_ml(double vessel_ml, double solution_ml, double line_ml) {
    return vessel_ml - std::max(0.0, solution_ml) + std::max(0.0, line_ml);
}

double gas_mmol(double psi_gauge, double temp_c, double vol_ml) {
    return (psi_gauge + kAtmPsi) * kPaPerPsi * vol_ml * 1e-6 / (kR * (temp_c + 273.15)) * 1000.0;
}

double gas_mmol_two_zone(double psi_gauge, double temp_c, double hot_vol_ml, double cold_vol_ml, double ambient_c) {
    double hot = std::max(0.0, hot_vol_ml);
    double cold = std::max(0.0, cold_vol_ml);
    if (cold <= 0.0) return gas_mmol(psi_gauge, temp_c, hot);
    double p_pa = (psi_gauge + kAtmPsi) * kPaPerPsi;
    double v_over_t = hot / (temp_c + 273.15) + cold / (ambient_c + 273.15);
    return p_pa * v_over_t * 1e-6 / kR * 1000.0;
}

double resolution_mmol(double d_psi, double vol_ml) { return d_psi * kPaPerPsi * vol_ml * 1e-6 / (kR * 298.15) * 1000.0; }

double channel_gas_volume_ml(std::optional<double> effective_ml, double solution_ml, double vessel_ml, double line_ml) {
    if (effective_ml) return *effective_ml - std::max(0.0, solution_ml);
    return gas_volume_ml(vessel_ml, solution_ml, line_ml);
}

GasVolumeZones split_channel_gas_volume_ml(std::optional<double> effective_ml, double solution_ml, double vessel_ml,
                                           double line_ml) {
    GasVolumeZones z;
    z.total_ml = channel_gas_volume_ml(effective_ml, solution_ml, vessel_ml, line_ml);
    if (z.total_ml <= 0.0) return z;
    double dead_ml = effective_ml ? std::max(0.0, *effective_ml - std::max(0.0, vessel_ml)) : std::max(0.0, line_ml);
    z.cold_ml = std::min(z.total_ml, dead_ml);
    z.hot_ml = std::max(0.0, z.total_ml - z.cold_ml);
    return z;
}

std::string volume_basis_text(std::optional<double> effective_ml, double solution_ml, double vessel_ml, double line_ml) {
    if (effective_ml)
        return "有效容积 " + fmt_dec(*effective_ml, 2) + "（已标定）− 溶液 " + fmt_dec(solution_ml, 2);
    return "釜 " + fmt_dec(vessel_ml, 1) + " − 溶液 " + fmt_dec(solution_ml, 2) + " + 管路 " + fmt_dec(line_ml, 1) +
           "（未标定）";
}

}  // namespace gas_uptake

using namespace gas_uptake;

bool GasUptakeMeter::start(int64_t t_ms, int64_t sv_gen) {
    if (counting_) return false;
    counting_ = true;
    valid_ = true;
    mmol_ = 0;
    rate_.reset();
    refills_ = 0;
    gas_volume_.reset();
    cold_volume_.reset();
    prev_n_.reset();
    last_sample_t_.reset();
    pre_blind_t_.reset();
    pre_blind_n_ = 0;
    pre_blind_rate_ = 0;
    gen_ = sv_gen;
    closed_since_ = static_cast<double>(t_ms) / 1000.0;
    hist_.clear();
    return true;
}

void GasUptakeMeter::stop() {
    counting_ = false;
    rate_.reset();
    prev_n_.reset();
    last_sample_t_.reset();
    pre_blind_t_.reset();
    hist_.clear();
}

void GasUptakeMeter::reset() {
    stop();
    valid_ = false;
    mmol_ = 0;
    refills_ = 0;
    gas_volume_.reset();
    cold_volume_.reset();
}

bool GasUptakeMeter::sample(int64_t t_ms, bool readings_ok, double psi_gauge, double temp_c, double gas_vol_ml,
                            bool sv_open, int64_t sv_gen, double cold_vol_ml, double ambient_c,
                            bool compensate_refill) {
    if (!counting_) return false;
    double t = static_cast<double>(t_ms) / 1000.0;
    auto enter_blind = [&] {
        if (prev_n_ && last_sample_t_) {
            pre_blind_t_ = *last_sample_t_;
            pre_blind_n_ = *prev_n_;
            pre_blind_rate_ = (rate_ && *rate_ > 0.0) ? *rate_ : 0.0;
        }
        prev_n_.reset();
    };
    if (sv_gen != gen_) {
        refills_++;
        gen_ = sv_gen;
        enter_blind();
        if (sv_open) closed_since_.reset();
        else closed_since_ = t;
        return false;
    }
    if (sv_open) { enter_blind(); closed_since_.reset(); return false; }
    if (!closed_since_) { closed_since_ = t; return false; }
    if (t - *closed_since_ < kSettleSec) return false;
    if (!readings_ok || !std::isfinite(psi_gauge) || !std::isfinite(temp_c) || temp_c <= -273 || ambient_c <= -273) return false;
    if (!std::isfinite(gas_vol_ml) || gas_vol_ml < kMinGasVolumeMl) {
        prev_n_.reset();
        pre_blind_t_.reset();
        return false;
    }
    double cold = std::isfinite(cold_vol_ml) ? std::clamp(cold_vol_ml, 0.0, gas_vol_ml) : 0.0;
    double hot = std::max(0.0, gas_vol_ml - cold);
    if ((gas_volume_ && std::fabs(*gas_volume_ - gas_vol_ml) > 1e-9) ||
        (cold_volume_ && std::fabs(*cold_volume_ - cold) > 1e-9)) {
        prev_n_.reset();  // 体积参数改了：重取基准
        pre_blind_t_.reset();
    }
    gas_volume_ = gas_vol_ml;
    cold_volume_ = cold;

    double n = gas_mmol_two_zone(psi_gauge, temp_c, hot, cold, ambient_c);
    bool moved = false;
    if (prev_n_) {
        mmol_ += *prev_n_ - n;
        moved = true;
    } else if (compensate_refill && pre_blind_t_ && pre_blind_rate_ > 0.0 && n > pre_blind_n_) {
        double dt = t - *pre_blind_t_;
        if (dt > 0.0 && dt <= kMaxRefillBlindSec) {
            mmol_ += pre_blind_rate_ * (dt / 60.0);
            moved = true;
        }
    }
    pre_blind_t_.reset();
    prev_n_ = n;
    last_sample_t_ = t;

    hist_.emplace_back(t, mmol_);
    while (hist_.size() > 1 && t - hist_.front().first > kRateWindowSec) hist_.pop_front();
    auto first = hist_.front();
    double span = t - first.first;
    if (span >= kRateMinSpanSec) rate_ = (mmol_ - first.second) / span * 60.0;
    else rate_.reset();
    return moved;
}

}  // namespace reactor::core
