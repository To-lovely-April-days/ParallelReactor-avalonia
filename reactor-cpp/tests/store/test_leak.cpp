// C#: Models/LeakRecord.cs 的文件格式 + tests/ParallelReactor.Tests/LeakFitTests.cs 的拟合部分
#include <cmath>

#include "nlohmann/json.hpp"
#include "store/csv.hpp"
#include "store/leak_record.hpp"
#include "store_test_util.hpp"
#include "testing.hpp"

using namespace reactor::store;
using store_test::at8;
using store_test::contains;

static WallTime plus(const WallTime& t, double sec) { return WallTime::with_offset(t.ms + static_cast<int64_t>(sec * 1000), t.offset_min); }

TEST(store_leak, fit_line) {
    auto f = leak_fit::fit({{0, 1}, {1, 3}, {2, 5}});
    CHECK_NEAR(f.slope, 2, 1e-12);
    CHECK_NEAR(f.intercept, 1, 1e-12);
    CHECK_NEAR(f.r2, 1, 1e-12);
    CHECK(std::isnan(leak_fit::fit({{0, 1}}).slope));
    CHECK(std::isnan(leak_fit::fit({{1, 1}, {1, 2}}).slope));   // x 无变化
    CHECK_EQ(leak_fit::fit({{0, 5}, {1, 5}, {2, 5}}).r2, 1.0);  // y 全相同：R² 记 1
    CHECK_NEAR(leak_fit::fit_per_hour({{0, 50}, {3600, 49}}).slope, -1, 1e-9);
    CHECK_NEAR(leak_fit::psig_to_abs_kpa(0), 101.325, 1e-12);
    CHECK(std::isnan(leak_fit::corrected_abs_kpa(10, NAN, 25)));
    CHECK_NEAR(leak_fit::corrected_abs_kpa(10, 25, 25), leak_fit::psig_to_abs_kpa(10), 1e-12);
}

TEST(store_leak, run_id_and_samples_csv) {
    auto t0 = at8(2026, 11, 6, 6, 0, 0);
    LeakRecord rec(t0);
    CHECK_EQ(rec.run_id(), std::string("leak-20261106-060000"));
    rec.add(1, LeakRecord::kPrep, t0, 1600, 0.5, 25.0);
    rec.add(1, LeakRecord::kMeasure, plus(t0, 600), 8000, 50.0, 25.0);
    rec.add(2, LeakRecord::kMeasure, plus(t0, 600.5), std::nullopt, 49.98765, NAN);
    rec.t_ref()[1] = 25.0;
    auto text = rec.samples_csv();
    auto lines = std::vector<std::string>();
    size_t p = 0;
    while (p < text.size()) {
        size_t n = text.find('\n', p);
        lines.push_back(text.substr(p, n - p));
        p = n + 1;
    }
    CHECK_EQ(lines.size(), size_t(4));
    CHECK_EQ(lines[0], std::string("测试编号,RV,阶段,t(s),时间,压力原码,压力(psig),压力(kPa表压),温度(℃),P_abs(kPa),T_ref(℃),P_corr(kPa绝压),P_corr(psia),已温度补偿"));
    CHECK_EQ(lines[1], std::string("leak-20261106-060000,RV1,prep,0.0,2026-11-06T06:00:00.000+08:00,1600,0.5,3.447,25.0,104.772,25.0,104.772,15.1959,1"));
    auto c2 = csv::split(lines[2]);
    CHECK_EQ(c2[3], std::string("600.0"));
    CHECK_EQ(c2[6], std::string("50.0"));
    CHECK_EQ(c2[7], std::string("344.738"));   // 50 × 6.894757 = 344.73785
    auto c3 = csv::split(lines[3]);
    CHECK_EQ(c3[5], std::string(""));          // 原码未知
    CHECK_EQ(c3[6], std::string("49.9877"));
    CHECK_EQ(c3[8], std::string(""));          // 温度无效
    CHECK_EQ(c3[10], std::string(""));         // RV2 没有 T_ref
    CHECK_EQ(c3[13], std::string("0"));        // 未补偿
}

TEST(store_leak, summary_csv_and_json) {
    auto t0 = at8(2026, 11, 6, 6, 0, 0);
    LeakRecord rec(t0);
    for (int i = 0; i <= 6; ++i) rec.add(3, LeakRecord::kMeasure, plus(t0, 600 + 600.0 * i), std::nullopt, 50.0 - 0.1 * i, 25.0);
    rec.t_ref()[3] = 25.0;
    std::map<int, double> judged{{3, 0.6}};
    auto rows = rec.summarize({{3, "bad", "压降 0.6 psi/h"}, {5, "skip", ""}}, &judged);
    CHECK_EQ(rows.size(), size_t(2));
    CHECK_EQ(rows[0].n, 7);
    CHECK_EQ(rows[0].n_comp, 7);
    CHECK_NEAR(*rows[0].slope_raw_psi_per_h, -0.6, 1e-9);
    CHECK_NEAR(*rows[0].r2_raw, 1.0, 1e-9);
    CHECK(rows[0].judged_drop_psi_per_h == std::optional<double>(0.6));
    CHECK(!rows[1].slope_raw_psi_per_h);   // 没有点：拟合无效 → 空
    CHECK(!rows[1].t_ref_c);
    CHECK_EQ(rows[1].n, 0);

    auto sum = rec.summary_csv(rows);
    CHECK(contains(sum, "测试编号,RV,结论,详情,\"判读压降速率(psi/h,正=降)\",测量点数"));   // 表头里的逗号被加引号
    CHECK(contains(sum, "\nleak-20261106-060000,RV3,bad,压降 0.6 psi/h,0.6,7,600.0,4200.0,25.0,50.0,49.4,-0.6,-4.13685,1.0,7,"));
    CHECK(contains(sum, "\nleak-20261106-060000,RV5,skip,,,0,,,,,,,,,0,,,\n"));

    LeakParams par{50, 2, 10, 30, 15, false, std::nullopt};
    auto js = rec.summary_json(rows, par);
    auto j = nlohmann::json::parse(js);
    CHECK_EQ(j["runId"].get<std::string>(), std::string("leak-20261106-060000"));
    CHECK_EQ(j["start"].get<std::string>(), std::string("2026-11-06T06:00:00.000+08:00"));
    CHECK(j["parameters"]["fail"].is_null());
    CHECK_EQ(j["parameters"]["testPsi"].get<double>(), 50.0);
    CHECK_EQ(j["channels"][0]["Rv"].get<int>(), 3);   // LeakChannelSummary 用 C# 默认命名（PascalCase）
    CHECK(j["channels"][1]["SlopeRawPsiPerH"].is_null());
    // 键顺序同 C# 匿名对象 / 属性声明
    CHECK(js.find("\"runId\"") < js.find("\"start\"") && js.find("\"assumptions\"") < js.find("\"parameters\"") &&
          js.find("\"parameters\"") < js.find("\"channels\""));
    CHECK(js.find("\"Rv\"") < js.find("\"Status\"") && js.find("\"NComp\"") < js.find("\"R2Comp\""));
    CHECK(contains(js, "\"testPsi\": 50,"));
    CHECK(contains(js, "T_ref = 测量窗口起点釜温"));   // 中文不转义

    auto files = rec.files(rows, par);
    CHECK_EQ(files.size(), size_t(3));
    CHECK_EQ(files[0].first, std::string("leak/leak-20261106-060000.csv"));
    CHECK_EQ(files[1].first, std::string("leak/leak-20261106-060000-summary.csv"));
    CHECK_EQ(files[2].first, std::string("leak/leak-20261106-060000-summary.json"));
}
