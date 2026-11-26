#include "core/gas_uptake.hpp"

#include <algorithm>
#include <cmath>

#include "core/num_format.hpp"

namespace reactor::core {

namespace gas_uptake {

double gas_volume_ml(double vessel_ml, double solution_ml, double line_ml) {
    return vessel_ml - std::max(0.0, solution_ml) + std::max(0.0, line_ml);
}

double gas_mmol(double psi_gauge, double temp_c, double vol_ml) {
    return (psi_gauge + kAtmPsi) * kPaPerPsi * vol_ml * 1e-6 / (kR * (temp_c + 273.15)) * 1000.0;
}

double resolution_mmol(double d_psi, double vol_ml) { return d_psi * kPaPerPsi * vol_ml * 1e-6 / (kR * 298.15) * 1000.0; }

double channel_gas_volume_ml(std::optional<double> effective_ml, double solution_ml, double vessel_ml, double line_ml) {
    if (effective_ml) return *effective_ml - std::max(0.0, solution_ml);
    return gas_volume_ml(vessel_ml, solution_ml, line_ml);
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
    prev_n_.reset();
    gen_ = sv_gen;
    closed_since_ = static_cast<double>(t_ms) / 1000.0;
    hist_.clear();
    return true;
}

void GasUptakeMeter::stop() {
    counting_ = false;
    rate_.reset();
    prev_n_.reset();
    hist_.clear();
}

void GasUptakeMeter::reset() {
    stop();
    valid_ = false;
    mmol_ = 0;
    refills_ = 0;
    gas_volume_.reset();
}

bool GasUptakeMeter::sample(int64_t t_ms, bool readings_ok, double psi_gauge, double temp_c, double gas_vol_ml,
                            bool sv_open, int64_t sv_gen) {
    if (!counting_) return false;
    double t = static_cast<double>(t_ms) / 1000.0;
    if (sv_gen != gen_) {
        refills_++;
        gen_ = sv_gen;
        prev_n_.reset();
        if (sv_open) closed_since_.reset();
        else closed_since_ = t;
        return false;
    }
    if (sv_open) { prev_n_.reset(); closed_since_.reset(); return false; }
    if (!closed_since_) { closed_since_ = t; return false; }
    if (t - *closed_since_ < kSettleSec) return false;
    if (!readings_ok || !std::isfinite(psi_gauge) || !std::isfinite(temp_c) || temp_c <= -273) return false;
    if (!std::isfinite(gas_vol_ml) || gas_vol_ml < kMinGasVolumeMl) { prev_n_.reset(); return false; }
    if (gas_volume_ && std::fabs(*gas_volume_ - gas_vol_ml) > 1e-9) prev_n_.reset();  // 体积参数改了：重取基准
    gas_volume_ = gas_vol_ml;

    double n = gas_mmol(psi_gauge, temp_c, gas_vol_ml);
    bool moved = false;
    if (prev_n_) { mmol_ += *prev_n_ - n; moved = true; }
    prev_n_ = n;

    hist_.emplace_back(t, mmol_);
    while (hist_.size() > 1 && t - hist_.front().first > kRateWindowSec) hist_.pop_front();
    auto first = hist_.front();
    double span = t - first.first;
    if (span >= kRateMinSpanSec) rate_ = (mmol_ - first.second) / span * 60.0;
    else rate_.reset();
    return moved;
}

}  // namespace reactor::core
