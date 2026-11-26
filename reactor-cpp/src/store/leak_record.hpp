// 泄漏测试结果文件（C#: Models/LeakRecord.cs，拟合部分 Models/LeakFit.cs 在这里本地拷贝一份）：
//   leak/<runId>.csv            逐点数据
//   leak/<runId>-summary.csv    每通道汇总
//   leak/<runId>-summary.json   汇总 + 测试参数 + 假设说明
// runId = leak-yyyyMMdd-HHmmss（开始时刻的本地时间）。文件内容和 C# 逐字节一致（UTF-8 BOM 由写文件的一方加，见 DataLogger::write_file）。
// 纯逻辑，不加锁：一个 LeakRecord 只给一个线程（控制线程）用。
#pragma once

#include <cstdint>
#include <map>
#include <optional>
#include <string>
#include <vector>

#include "store/store_util.hpp"
#include <utility>

namespace reactor::store {

// 最小二乘拟合（C#: LeakFit）。点数 < 2 或 x 无变化时 slope / r2 为 NaN
namespace leak_fit {
constexpr double kKpaPerPsi = 6.894757;
constexpr double kAtmKpa = 101.325;
constexpr double kKelvin = 273.15;
struct LineFit { double slope, intercept, r2; int n; };
LineFit fit(const std::vector<std::pair<double, double>>& pts);
LineFit fit_per_hour(const std::vector<std::pair<double, double>>& pts_sec);   // 斜率换算为每小时
double psig_to_abs_kpa(double psig);
double corrected_abs_kpa(double psig, double t_c, double t_ref_c);   // 温度无效返回 NaN
}  // namespace leak_fit

struct LeakSample {
    int rv = 0;
    std::string phase;   // prep / fill / equilibrate / measure / vent
    double t_sec = 0;    // 相对测试开始
    WallTime time;
    std::optional<int64_t> raw;
    double psig = 0;
    double temp_c = 0;   // NaN = 温度无效
};

struct LeakChannelSummary {
    int rv = 0;
    std::string status;   // ok / bad / skip / ""（未完成）
    std::string detail;
    std::optional<double> judged_drop_psi_per_h;
    int n = 0;
    std::optional<double> window_start_sec, window_end_sec, t_ref_c, start_psig, end_psig;
    std::optional<double> slope_raw_psi_per_h, slope_raw_kpa_per_h, r2_raw;
    int n_comp = 0;
    std::optional<double> slope_comp_psi_per_h, slope_comp_kpa_per_h, r2_comp;
};

// summary.json 的 parameters（C#: LeakViewModel.SaveRecord 的匿名对象，字段顺序照抄）
struct LeakParams {
    double test_psi = 0;
    double threshold_psi_per_h = 0;
    double settle_min = 0;
    double measure_min = 0;
    double sample_sec = 0;
    bool aborted = false;
    std::optional<std::string> fail;
};

struct LeakCell {
    int rv;
    std::string status;
    std::string detail;
};

class LeakRecord {
public:
    static constexpr const char* kPrep = "prep";
    static constexpr const char* kFill = "fill";
    static constexpr const char* kEquilibrate = "equilibrate";
    static constexpr const char* kMeasure = "measure";
    static constexpr const char* kVent = "vent";

    explicit LeakRecord(const WallTime& start, std::optional<std::string> run_id = std::nullopt);

    const std::string& run_id() const { return run_id_; }
    const WallTime& start() const { return start_; }
    const std::vector<LeakSample>& samples() const { return samples_; }
    std::map<int, double>& t_ref() { return t_ref_; }   // 每通道补偿参考温度（测量窗口起点釜温）

    void add(int rv, const std::string& phase, const WallTime& t, std::optional<int64_t> raw, double psig, double temp_c);

    double corr_kpa(const LeakSample& s) const;

    static const std::string& samples_header();
    std::string samples_csv() const;   // 含表头，每行以 \n 结束
    std::vector<LeakChannelSummary> summarize(const std::vector<LeakCell>& cells, const std::map<int, double>* judged) const;
    static const std::string& summary_header();
    std::string summary_csv(const std::vector<LeakChannelSummary>& rows) const;
    std::string summary_json(const std::vector<LeakChannelSummary>& rows, const LeakParams& p) const;

    // 三个文件的 (相对数据目录的路径, 内容)，交给 DataLogger::write_file
    std::vector<std::pair<std::string, std::string>> files(const std::vector<LeakChannelSummary>& rows, const LeakParams& p) const;

private:
    WallTime start_;
    std::string run_id_;
    std::vector<LeakSample> samples_;
    std::map<int, double> t_ref_;
};

}  // namespace reactor::store
