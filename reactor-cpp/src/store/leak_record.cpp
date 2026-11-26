#include "store/leak_record.hpp"

#include <algorithm>
#include <cmath>
#include <limits>

#include "store/csv.hpp"

namespace reactor::store {

// ============================ LeakFit（本地拷贝，C#: Models/LeakFit.cs）============================

namespace leak_fit {

LineFit fit(const std::vector<std::pair<double, double>>& pts) {
    const double nan = std::numeric_limits<double>::quiet_NaN();
    int n = static_cast<int>(pts.size());
    if (n < 2) return {nan, nan, nan, n};
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
    if (sxx < 1e-12) return {nan, nan, nan, n};
    double slope = sxy / sxx;
    double r2 = syy < 1e-24 ? 1.0 : sxy * sxy / (sxx * syy);   // 所有点 y 相同：完美拟合，R² 记 1
    return {slope, my - slope * mx, r2, n};
}

LineFit fit_per_hour(const std::vector<std::pair<double, double>>& pts) {
    auto f = fit(pts);
    f.slope *= 3600.0;
    return f;
}

double psig_to_abs_kpa(double psig) { return psig * kKpaPerPsi + kAtmKpa; }

double corrected_abs_kpa(double psig, double t_c, double t_ref_c) {
    if (std::isnan(t_c) || std::isnan(t_ref_c) || t_c + kKelvin <= 0) return std::numeric_limits<double>::quiet_NaN();
    return psig_to_abs_kpa(psig) * (t_ref_c + kKelvin) / (t_c + kKelvin);
}

}  // namespace leak_fit

// ============================ LeakRecord ============================

LeakRecord::LeakRecord(const WallTime& start, std::optional<std::string> run_id)
    : start_(start), run_id_(run_id ? *run_id : "leak-" + fmt_wall(start, "yyyyMMdd-HHmmss")) {}

void LeakRecord::add(int rv, const std::string& phase, const WallTime& t, std::optional<int64_t> raw, double psig, double temp_c) {
    samples_.push_back(LeakSample{rv, phase, static_cast<double>(t.ms - start_.ms) / 1000.0, t, raw, psig, temp_c});
}

double LeakRecord::corr_kpa(const LeakSample& s) const {
    auto it = t_ref_.find(s.rv);
    if (it == t_ref_.end()) return std::numeric_limits<double>::quiet_NaN();
    return leak_fit::corrected_abs_kpa(s.psig, s.temp_c, it->second);
}

const std::string& LeakRecord::samples_header() {
    static const std::string h = csv::line({"测试编号", "RV", "阶段", "t(s)", "时间", "压力原码", "压力(psig)", "压力(kPa表压)", "温度(℃)",
                                            "P_abs(kPa)", "T_ref(℃)", "P_corr(kPa绝压)", "P_corr(psia)", "已温度补偿"});
    return h;
}

std::string LeakRecord::samples_csv() const {
    std::string out = samples_header() + "\n";
    for (auto& s : samples_) {
        double corr = corr_kpa(s);
        auto it = t_ref_.find(s.rv);
        std::optional<double> tref = it != t_ref_.end() ? std::optional<double>(it->second) : std::nullopt;
        out += csv::line({run_id_, "RV" + std::to_string(s.rv), s.phase, csv::num(s.t_sec, "0.0##"), csv::time(s.time),
                          csv::integer(s.raw), csv::num(s.psig, "0.0###"), csv::num(s.psig * leak_fit::kKpaPerPsi, "0.0##"),
                          csv::num(s.temp_c, "0.0##"), csv::num(leak_fit::psig_to_abs_kpa(s.psig), "0.0##"), csv::num(tref, "0.0##"),
                          csv::num(corr, "0.0##"), csv::num(corr / leak_fit::kKpaPerPsi, "0.0###"), csv::boolean(!std::isnan(corr))});
        out += "\n";
    }
    return out;
}

static std::optional<double> V(double d) {
    if (!std::isfinite(d)) return std::nullopt;
    return d;
}

std::vector<LeakChannelSummary> LeakRecord::summarize(const std::vector<LeakCell>& cells, const std::map<int, double>* judged) const {
    std::vector<LeakChannelSummary> list;
    for (auto& c : cells) {
        std::vector<const LeakSample*> m;
        for (auto& s : samples_)
            if (s.rv == c.rv && s.phase == kMeasure) m.push_back(&s);
        std::stable_sort(m.begin(), m.end(), [](const LeakSample* a, const LeakSample* b) { return a->t_sec < b->t_sec; });
        std::vector<std::pair<double, double>> raw_pts, comp_pts;
        for (auto* s : m) {
            raw_pts.emplace_back(s->t_sec, s->psig);
            double k = corr_kpa(*s);
            if (!std::isnan(k)) comp_pts.emplace_back(s->t_sec, k / leak_fit::kKpaPerPsi);
        }
        auto raw = leak_fit::fit_per_hour(raw_pts);
        auto comp = leak_fit::fit_per_hour(comp_pts);
        LeakChannelSummary r;
        r.rv = c.rv;
        r.status = c.status;
        r.detail = c.detail;
        if (judged) {
            auto it = judged->find(c.rv);
            if (it != judged->end()) r.judged_drop_psi_per_h = it->second;
        }
        r.n = static_cast<int>(m.size());
        if (!m.empty()) {
            r.window_start_sec = m.front()->t_sec;
            r.window_end_sec = m.back()->t_sec;
            r.start_psig = m.front()->psig;
            r.end_psig = m.back()->psig;
        }
        auto tr = t_ref_.find(c.rv);
        if (tr != t_ref_.end() && !std::isnan(tr->second)) r.t_ref_c = tr->second;
        r.slope_raw_psi_per_h = V(raw.slope);
        r.slope_raw_kpa_per_h = V(raw.slope * leak_fit::kKpaPerPsi);
        r.r2_raw = V(raw.r2);
        r.n_comp = static_cast<int>(comp_pts.size());
        r.slope_comp_psi_per_h = V(comp.slope);
        r.slope_comp_kpa_per_h = V(comp.slope * leak_fit::kKpaPerPsi);
        r.r2_comp = V(comp.r2);
        list.push_back(r);
    }
    return list;
}

const std::string& LeakRecord::summary_header() {
    static const std::string h = csv::line({"测试编号", "RV", "结论", "详情", "判读压降速率(psi/h,正=降)", "测量点数", "窗口起(s)", "窗口止(s)",
                                            "T_ref(℃)", "起始压力(psig)", "结束压力(psig)", "未补偿斜率(psi/h)", "未补偿斜率(kPa/h)",
                                            "未补偿R²", "补偿点数", "补偿斜率(psi/h)", "补偿斜率(kPa/h)", "补偿R²"});
    return h;
}

std::string LeakRecord::summary_csv(const std::vector<LeakChannelSummary>& rows) const {
    std::string out = summary_header() + "\n";
    for (auto& r : rows) {
        out += csv::line({run_id_, "RV" + std::to_string(r.rv), r.status, r.detail, csv::num(r.judged_drop_psi_per_h, "0.0###"),
                          csv::integer(r.n), csv::num(r.window_start_sec, "0.0#"), csv::num(r.window_end_sec, "0.0#"),
                          csv::num(r.t_ref_c, "0.0##"), csv::num(r.start_psig, "0.0###"), csv::num(r.end_psig, "0.0###"),
                          csv::num(r.slope_raw_psi_per_h, "0.0####"), csv::num(r.slope_raw_kpa_per_h, "0.0####"),
                          csv::num(r.r2_raw, "0.0####"), csv::integer(r.n_comp), csv::num(r.slope_comp_psi_per_h, "0.0####"),
                          csv::num(r.slope_comp_kpa_per_h, "0.0####"), csv::num(r.r2_comp, "0.0####")});
        out += "\n";
    }
    return out;
}

static ojson onum(const std::optional<double>& v) { return v ? json_num(*v) : ojson(nullptr); }

std::string LeakRecord::summary_json(const std::vector<LeakChannelSummary>& rows, const LeakParams& p) const {
    ojson o = ojson::object();
    o["runId"] = run_id_;
    o["start"] = csv::time(start_);
    o["assumptions"] =
        "P_corr = P_abs × T_ref / T（K）；T_ref = 测量窗口起点釜温；P_abs = 表压 + 101.325 kPa（标准大气压，系统无气压计）；斜率为最小二乘 dP/dt（负=压力下降）";
    ojson par = ojson::object();
    par["testPsi"] = json_num(p.test_psi);
    par["thresholdPsiPerH"] = json_num(p.threshold_psi_per_h);
    par["settleMin"] = json_num(p.settle_min);
    par["measureMin"] = json_num(p.measure_min);
    par["sampleSec"] = json_num(p.sample_sec);
    par["aborted"] = p.aborted;
    par["fail"] = p.fail ? ojson(*p.fail) : ojson(nullptr);
    o["parameters"] = par;
    ojson ch = ojson::array();
    for (auto& r : rows) {
        ojson x = ojson::object();
        x["Rv"] = r.rv;
        x["Status"] = r.status;
        x["Detail"] = r.detail;
        x["JudgedDropPsiPerH"] = onum(r.judged_drop_psi_per_h);
        x["N"] = r.n;
        x["WindowStartSec"] = onum(r.window_start_sec);
        x["WindowEndSec"] = onum(r.window_end_sec);
        x["TRefC"] = onum(r.t_ref_c);
        x["StartPsig"] = onum(r.start_psig);
        x["EndPsig"] = onum(r.end_psig);
        x["SlopeRawPsiPerH"] = onum(r.slope_raw_psi_per_h);
        x["SlopeRawKpaPerH"] = onum(r.slope_raw_kpa_per_h);
        x["R2Raw"] = onum(r.r2_raw);
        x["NComp"] = r.n_comp;
        x["SlopeCompPsiPerH"] = onum(r.slope_comp_psi_per_h);
        x["SlopeCompKpaPerH"] = onum(r.slope_comp_kpa_per_h);
        x["R2Comp"] = onum(r.r2_comp);
        ch.push_back(x);
    }
    o["channels"] = ch;
    return dump_json(o, 2);
}

std::vector<std::pair<std::string, std::string>> LeakRecord::files(const std::vector<LeakChannelSummary>& rows,
                                                                   const LeakParams& p) const {
    return {
        {"leak/" + run_id_ + ".csv", samples_csv()},
        {"leak/" + run_id_ + "-summary.csv", summary_csv(rows)},
        {"leak/" + run_id_ + "-summary.json", summary_json(rows, p)},
    };
}

}  // namespace reactor::store
