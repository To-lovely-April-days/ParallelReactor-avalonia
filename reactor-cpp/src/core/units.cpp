#include "core/units.hpp"

#include "core/num_format.hpp"
#include <string>

namespace reactor::core {

PressureUnit parse_pressure_unit(const std::string& s) {
    if (s == "psi") return PressureUnit::Psi;
    if (s == "bar") return PressureUnit::Bar;
    return PressureUnit::MPa;
}

const char* pressure_label(PressureUnit u) {
    switch (u) {
        case PressureUnit::Bar: return "bar";
        case PressureUnit::Psi: return "psi";
        default: return "MPa";
    }
}

int pressure_decimals(PressureUnit u) {
    switch (u) {
        case PressureUnit::Bar: return 2;
        case PressureUnit::Psi: return 1;
        default: return 3;
    }
}

double psi_to_unit(double psi, PressureUnit u) {
    switch (u) {
        case PressureUnit::Bar: return psi / units::kPsiPerBar;
        case PressureUnit::MPa: return psi / units::kPsiPerMpa;
        default: return psi;
    }
}

double unit_to_psi(double v, PressureUnit u) {
    switch (u) {
        case PressureUnit::Bar: return v * units::kPsiPerBar;
        case PressureUnit::MPa: return v * units::kPsiPerMpa;
        default: return v;
    }
}

std::string p_value(double psi, PressureUnit u) {
    int d = pressure_decimals(u);
    return fmt_dec(psi_to_unit(psi, u), d, d);
}

std::string format_p(double psi, PressureUnit u) { return p_value(psi, u) + " " + pressure_label(u); }

std::string format_rate(double psi_per_h, PressureUnit u) {
    int d = pressure_decimals(u) + 1;
    return fmt_dec(psi_to_unit(psi_per_h, u), d, d) + " " + pressure_label(u) + "/h";
}

double mpa_to_psi(double mpa) { return mpa * units::kPsiPerMpa; }
double psi_to_mpa(double psi) { return psi / units::kPsiPerMpa; }

}  // namespace reactor::core
