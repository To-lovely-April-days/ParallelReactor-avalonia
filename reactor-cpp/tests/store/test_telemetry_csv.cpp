// C#: tests/ParallelReactor.Tests/TelemetryCsvTests.cs（表头稳定性、行格式）
#include <set>

#include "store/csv.hpp"
#include "store/telemetry_csv.hpp"
#include "store_test_util.hpp"
#include "testing.hpp"

using namespace reactor::store;
using store_test::at8;
using store_test::contains;

static std::vector<std::string> cells_of(const TelemetryRow& r) { return csv::split(telemetry_csv::format(r)); }
static const std::string& back(const std::vector<std::string>& v, size_t i_from_end) { return v[v.size() - i_from_end]; }

TEST(store_telemetry, header_is_stable) {
    auto& h = telemetry_csv::header_fields();
    // 3 通用列 + 8 通道 × 15 列 + 12 全局列 + 4 模块 × 5 列 + 模拟 + 8 程序步 + 8 吸气量 + 搅拌电机转速
    CHECK_EQ(h.size(), size_t(3 + 8 * 15 + 12 + 4 * 5 + 1 + 8 + 8 + 1));
    CHECK_EQ(h[0], std::string("时间"));
    CHECK_EQ(h[1], std::string("标记"));
    CHECK_EQ(h[2], std::string("会话"));
    CHECK_EQ(h[3], std::string("RV1_PV(℃)"));
    CHECK_EQ(h[4], std::string("RV1_PV原码"));
    CHECK_EQ(h[3 + 8 * 15 - 1], std::string("RV8_进气阀"));
    CHECK_EQ(back(h, 19), std::string("搅拌_最近响应"));
    CHECK_EQ(back(h, 18), std::string("模拟"));
    CHECK_EQ(back(h, 17), std::string("RV1_程序步"));
    CHECK_EQ(back(h, 10), std::string("RV8_程序步"));
    CHECK_EQ(back(h, 9), std::string("RV1_吸气量(mmol)"));
    CHECK_EQ(back(h, 2), std::string("RV8_吸气量(mmol)"));
    CHECK_EQ(back(h, 1), std::string("搅拌电机(rpm)"));
    CHECK_EQ(std::set<std::string>(h.begin(), h.end()).size(), h.size());   // 列名不重复
    CHECK(&telemetry_csv::header() == &telemetry_csv::header());           // 缓存
    CHECK_EQ(csv::line(h), telemetry_csv::header());
    // 全局列、模块列的确切顺序（C# TelemetryCsv.HeaderFields 照抄）
    CHECK_EQ(h[123], std::string("AI8_Srun"));
    CHECK_EQ(h[134], std::string("急停"));
    CHECK_EQ(h[135], std::string("温控_已配置"));
    CHECK_EQ(h[154], std::string("搅拌_最近响应"));
    CHECK_EQ(h[3 + 14], std::string("RV1_进气阀"));
    CHECK_EQ(h[3 + 11], std::string("RV1_压力(MPa)"));
}

TEST(store_telemetry, empty_row_has_same_columns_and_empty_cells) {
    TelemetryRow row;
    row.time = WallTime::with_offset(store_test::ms_at(2026, 1, 2, 3, 4, 5, 6), 0);
    auto cells = cells_of(row);
    CHECK_EQ(cells.size(), telemetry_csv::header_fields().size());
    CHECK_EQ(cells[0], std::string("2026-01-02T03:04:05.006+00:00"));
    CHECK_EQ(cells[3], std::string(""));   // RV1 PV 未知 → 空，不写 0
    CHECK_EQ(cells[4], std::string(""));
    CHECK_EQ(cells[3 + 13], std::string("0"));   // 超温切断 false → 0
    CHECK_EQ(cells[3 + 14], std::string(""));    // 进气阀未知 → 空
}

TEST(store_telemetry, fills_values_and_quotes_marker) {
    TelemetryRow row;
    row.time = at8(2026, 11, 6, 10, 0, 0);
    row.marker = "开始, 第1段";
    row.session = "T01";
    row.ch[0].pv = 25.4;
    row.ch[0].pv_raw = 254;
    row.ch[0].at = 4;
    row.ch[0].press_psi = 145.037738;
    row.ch[0].state = "Idle";
    row.temp.configured = true;
    row.temp.online = true;
    row.temp.polls = 10;
    row.temp.fails = 2;
    auto line = telemetry_csv::format(row);
    CHECK(contains(line, "\"开始, 第1段\",T01,25.4,254,"));
    CHECK(contains(line, ",4,"));
    CHECK(contains(line, ",1.00000,"));   // 145.037738 psi = 1 MPa
    CHECK(contains(line, ",1,1,10,2,"));  // 温控：已配置 / 在线 / 事务 / 失败
    CHECK(!contains(line, "\n"));
}

TEST(store_telemetry, program_step_columns_at_tail) {
    TelemetryRow row;
    row.time = WallTime::with_offset(store_test::ms_at(2026, 1, 2, 3, 4, 5, 6), 0);
    row.ch[0].program_step = "3/running";
    row.ch[2].program_step = "1/waitingGas";
    auto c = cells_of(row);
    CHECK_EQ(c.size(), telemetry_csv::header_fields().size());
    CHECK_EQ(back(c, 17), std::string("3/running"));
    CHECK_EQ(back(c, 16), std::string(""));
    CHECK_EQ(back(c, 15), std::string("1/waitingGas"));
    CHECK_EQ(back(c, 10), std::string(""));
}

TEST(store_telemetry, gas_columns_at_tail_empty_when_not_metered) {
    TelemetryRow row;
    row.time = WallTime::with_offset(store_test::ms_at(2026, 1, 2, 3, 4, 5, 6), 0);
    row.ch[0].gas_mmol = 12.34567;
    row.ch[7].gas_mmol = 0;
    auto c = cells_of(row);
    CHECK_EQ(back(c, 9), std::string("12.346"));
    CHECK_EQ(back(c, 8), std::string(""));
    CHECK_EQ(back(c, 2), std::string("0.000"));
    CHECK_EQ(back(c, 1), std::string(""));   // 搅拌电机转速未知 → 空
}

TEST(store_telemetry, stir_motor_rpm_is_last_column) {
    TelemetryRow row;
    row.time = at8(2026, 11, 6, 10, 0, 0);
    row.stir_rpm_set = 447;
    row.stir_motor_rpm = 1000;
    auto c = cells_of(row);
    CHECK_EQ(back(c, 1), std::string("1000"));
}

TEST(store_telemetry, module_last_ok_time_and_globals) {
    TelemetryRow row;
    row.time = at8(2026, 11, 6, 10, 0, 0);
    row.stir.last_ok_at = at8(2026, 11, 6, 9, 59, 59, 5);
    row.bus_voltage = 24.06;
    row.gas = {true, false, true};
    row.vent = true;
    auto c = cells_of(row);
    CHECK_EQ(c[154], std::string("2026-11-06T09:59:59.005+08:00"));
    CHECK_EQ(c[129], std::string("24.06"));
    CHECK_EQ(c[130], std::string("1"));
    CHECK_EQ(c[131], std::string("0"));
    CHECK_EQ(c[132], std::string("1"));
    CHECK_EQ(c[133], std::string("1"));
    CHECK_EQ(c[134], std::string("0"));   // 急停列恒 0
}

TEST(store_telemetry, event_line_format) {
    auto t = at8(2026, 11, 6, 9, 0, 0, 5);
    CHECK_EQ(telemetry_csv::format_event(t, "err", "RV1", "超温 · 切断加热", "PV 251.0℃, 限值 250℃"),
             std::string("2026-11-06T09:00:00.005+08:00,err,RV1,超温 · 切断加热,\"PV 251.0℃, 限值 250℃\""));
    CHECK_EQ(telemetry_csv::event_header(), std::string("时间,级别,来源,标题,详情"));
}
