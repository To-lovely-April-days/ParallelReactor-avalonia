#include "hw/analog_module.hpp"

namespace reactor::hw {

double AnalogModule::scale(uint16_t raw, double eng_lo, double eng_hi, bool is4to20) {
    const double raw_lo = is4to20 ? kRawLo4to20 : 0.0;
    double t = (raw - raw_lo) / (kRawHi - raw_lo);
    if (t < 0) t = 0;
    return eng_lo + t * (eng_hi - eng_lo);
}

std::optional<std::vector<uint16_t>> AnalogModule::decode_raw(const ModbusResult& r) const {
    if (!r.ok || r.regs.size() != static_cast<size_t>(count_)) return std::nullopt;
    return r.regs;
}

std::optional<std::vector<double>> AnalogModule::decode_psi(const ModbusResult& r, double fullscale_psi, PressSignal sig) const {
    auto raw = decode_raw(r);
    if (!raw) return std::nullopt;
    std::vector<double> v(raw->size());
    for (size_t i = 0; i < v.size(); ++i) v[i] = to_psi((*raw)[i], fullscale_psi, sig);
    return v;
}

std::optional<std::vector<double>> AnalogModule::read_scaled(ModbusMaster& m, double eng_lo, double eng_hi, bool is4to20) const {
    auto raw = read_raw(m);
    if (!raw) return std::nullopt;
    std::vector<double> v(raw->size());
    for (size_t i = 0; i < v.size(); ++i) v[i] = scale((*raw)[i], eng_lo, eng_hi, is4to20);
    return v;
}

}  // namespace reactor::hw
