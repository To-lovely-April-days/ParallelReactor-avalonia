#include "core/leak_fit.hpp"

namespace reactor::core::leak_fit {

LineFit fit(const std::vector<std::pair<double, double>>& pts) {
    int n = static_cast<int>(pts.size());
    LineFit bad;
    bad.n = n;
    if (n < 2) return bad;
    double mx = 0, my = 0;
    for (auto& p : pts) { mx += p.first; my += p.second; }
    mx /= n;
    my /= n;
    double sxx = 0, sxy = 0, syy = 0;
    for (auto& p : pts) {
        sxx += (p.first - mx) * (p.first - mx);
        sxy += (p.first - mx) * (p.second - my);
        syy += (p.second - my) * (p.second - my);
    }
    if (sxx < 1e-12) return bad;
    double slope = sxy / sxx;
    double r2 = syy < 1e-24 ? 1.0 : sxy * sxy / (sxx * syy);
    return LineFit{slope, my - slope * mx, r2, n};
}

LineFit fit_per_hour(const std::vector<std::pair<double, double>>& pts) {
    LineFit f = fit(pts);
    f.slope = f.slope * 3600.0;
    return f;
}

double psig_to_abs_kpa(double psig) { return psig * kKpaPerPsi + kAtmKpa; }

double corrected_abs_kpa(double psig, double t_c, double t_ref_c) {
    if (std::isnan(t_c) || std::isnan(t_ref_c) || t_c + kKelvin <= 0) return NAN;
    return psig_to_abs_kpa(psig) * (t_ref_c + kKelvin) / (t_c + kKelvin);
}

}  // namespace reactor::core::leak_fit
