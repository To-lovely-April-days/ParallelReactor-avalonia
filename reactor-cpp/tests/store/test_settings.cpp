// C#: tests/ParallelReactor.Tests/SettingsUpgradeTests.cs、SettingSpecsTests.cs、PidTuneTests.cs（持久化部分）
#include <cmath>
#include <limits>
#include <set>

#include "nlohmann/json.hpp"
#include "store/settings.hpp"
#include "store_test_util.hpp"
#include "testing.hpp"
#include <cstdint>
#include <cstdio>
#include <optional>
#include <string>

using namespace reactor::store;
using store_test::contains;
using store_test::ms_at;
using store_test::slurp;
using store_test::spit;
using store_test::TempDir;

// C# 程序（System.Text.Json，WriteIndented）写出的 v0.8 存档：属性名 PascalCase，DateTime.UtcNow 带 7 位小数和 Z，
// double? 数组里有 null，BandTune 里有 null 和三种来历。
static const char* kCSharpFile = R"({
  "Settings": {
    "TMaxSp": 180,
    "PMaxSp": 450,
    "RpmMax": 400,
    "VolMax": 6.5,
    "StirCurrent": 1.2,
    "StirStandbyPct": 40,
    "StirRampMs": 1500,
    "StirMicrostep": 20000,
    "StirGearRatio": 2.236842105263158,
    "PressFullScaleMPa": 6.895,
    "ApproachDelta": 4.5,
    "ApproachRate": 0.12,
    "ApproachFastRate": 1.5,
    "ApproachFinalZone": 1.2,
    "ApproachSettleBand": 0.3,
    "ApproachSettleSec": 120,
    "OverPressure": 480,
    "OverTemp": 230,
    "StopHeatOnOverPressure": false,
    "HeatTimeout": 25,
    "PressDeviation": 6,
    "LeakRate": 1.5,
    "LeakReminderDays": 14,
    "ProgramGasWaitMin": 90,
    "LeakTestPsi": 60,
    "LeakSettleMin": 12,
    "LeakMeasureMin": 45,
    "LastLeakTestUtc": "2026-11-20T06:30:12.3456789Z",
    "VesselVolume": 10,
    "GasLineVolume": 0.35,
    "ManifoldVolume": 7.42,
    "EffectiveVolume": [
      11.85,
      null,
      12.02,
      null,
      null,
      null,
      null,
      11.9
    ],
    "VolumeCalibUtc": [
      "2026-11-19T03:00:00.5Z",
      null,
      "2026-11-19T03:20:00Z",
      null,
      null,
      null,
      null,
      "2026-11-19T04:00:00Z"
    ]
  },
  "BandBounds": [
    90,
    210
  ],
  "BandPids": [
    [ [ 11.3, 152, 27 ], [ 13.8, 191, 33 ], [ 20, 240, 40 ] ],
    [ [ 11.9, 160, 28 ], [ 14.2, 197, 34 ], [ 20, 240, 40 ] ],
    [ [ 12.4, 171, 29 ], [ 15.1, 204, 37 ], [ 20, 240, 40 ] ],
    [ [ 10.8, 149, 26 ], [ 13.7, 211, 35 ], [ 20, 240, 40 ] ],
    [ [ 12.1, 158, 27 ], [ 15.1, 218, 33 ], [ 20, 240, 40 ] ],
    [ [ 11.5, 162, 30 ], [ 13.0, 185, 31 ], [ 20, 240, 40 ] ],
    [ [ 12.0, 150, 25 ], [ 14.0, 190, 30 ], [ 20, 240, 40 ] ],
    [ [ 12.7, 175, 31 ], [ 15.8, 199, 36 ], [ 20, 240, 40 ] ]
  ],
  "BandTune": [
    [
      {
        "TunedSpC": 80,
        "TunedAt": "2026-11-18T02:00:00Z",
        "ManualEditedAt": null
      },
      {
        "TunedSpC": 150,
        "TunedAt": "2026-11-18T05:10:00Z",
        "ManualEditedAt": "2026-11-18T06:00:00.25Z"
      },
      null
    ],
    [ null, null, { "TunedSpC": null, "TunedAt": null, "ManualEditedAt": "2026-11-21T01:02:03Z" } ],
    [ null, null, null ],
    [ null, null, null ],
    [ null, null, null ],
    [ null, null, null ],
    [ null, null, null ],
    [ { "TunedSpC": 333.5, "TunedAt": "2026-11-15T02:00:00Z", "ManualEditedAt": null }, null, null ]
  ]
})";

// v0.5 及以前的存档（C# SettingsUpgradeTests.OldFile 原样）
static const char* kOldFile = R"({
  "Settings": { "TMaxSp": 200, "PMaxSp": 500, "RpmMax": 1000, "VolMax": 50, "OverPressure": 510, "OverTemp": 250,
                "StirRampMs": 1000, "PressFullScaleMPa": 4.137, "LeakRate": 2, "LastLeakTestUtc": "2026-11-01T08:00:00Z" },
  "BandBounds": [ 100, 200 ],
  "BandPids": [
    [ [ 11.3, 152, 27 ], [ 13.8, 191, 33 ], [ 20, 240, 40 ] ],
    [ [ 11.9, 160, 28 ], [ 14.2, 197, 34 ], [ 20, 240, 40 ] ],
    [ [ 12.4, 171, 29 ], [ 15.1, 204, 37 ], [ 20, 240, 40 ] ],
    [ [ 10.8, 149, 26 ], [ 13.7, 211, 35 ], [ 20, 240, 40 ] ],
    [ [ 12.1, 158, 27 ], [ 15.1, 218, 33 ], [ 20, 240, 40 ] ],
    [ [ 11.5, 162, 30 ], [ 13.0, 185, 31 ], [ 20, 240, 40 ] ],
    [ [ 12.0, 150, 25 ], [ 14.0, 190, 30 ], [ 20, 240, 40 ] ],
    [ [ 12.7, 175, 31 ], [ 15.8, 199, 36 ], [ 20, 240, 40 ] ]
  ]
})";

TEST(store_settings, reads_file_written_by_csharp) {
    SettingsState st;
    std::string err;
    CHECK(settings_from_json(kCSharpFile, st, &err));
    CHECK_EQ(err, std::string(""));
    auto& s = st.settings;
    CHECK_EQ(s.t_max_sp, 180.0);
    CHECK_EQ(s.vol_max, 6.5);
    CHECK_NEAR(s.stir_gear_ratio, 85.0 / 38.0, 1e-12);
    CHECK_EQ(s.press_full_scale_mpa, 6.895);
    CHECK_EQ(s.approach_settle_sec, 120.0);
    CHECK(!s.stop_heat_on_over_pressure);
    CHECK_EQ(s.program_gas_wait_min, 90.0);
    CHECK(s.last_leak_test_utc_ms == std::optional<int64_t>(ms_at(2026, 11, 20, 6, 30, 12, 345)));
    CHECK_EQ(s.gas_line_volume, 0.35);
    CHECK(s.manifold_volume == std::optional<double>(7.42));
    CHECK(s.effective_volume[0] == std::optional<double>(11.85));
    CHECK(!s.effective_volume[1]);
    CHECK(s.effective_volume[7] == std::optional<double>(11.9));
    CHECK(s.volume_calib_utc_ms[0] == std::optional<int64_t>(ms_at(2026, 11, 19, 3, 0, 0, 500)));
    CHECK(!s.volume_calib_utc_ms[1]);
    CHECK_EQ(s.language, std::string("zh-CN"));   // C# 没有这一项 → 默认
    CHECK_EQ(st.band_bounds[0], 90.0);
    CHECK_EQ(st.band_bounds[1], 210.0);
    CHECK(st.has_band_pids);
    CHECK_EQ(st.band_pids[4][1].p, 15.1);
    CHECK_EQ(st.band_pids[7][2].d, 40.0);
    CHECK(st.band_tune[0][0] == std::optional<BandTuneMeta>(BandTuneMeta{80.0, ms_at(2026, 11, 18, 2, 0, 0), std::nullopt}));
    CHECK(st.band_tune[0][1]->manual_edited_at_ms == std::optional<int64_t>(ms_at(2026, 11, 18, 6, 0, 0, 250)));
    CHECK(!st.band_tune[0][2]);
    CHECK(st.band_tune[1][2] == std::optional<BandTuneMeta>(BandTuneMeta{std::nullopt, std::nullopt, ms_at(2026, 11, 21, 1, 2, 3)}));
    CHECK(st.band_tune[7][0]->tuned_sp_c == std::optional<double>(333.5));
    CHECK_EQ(st.revision, int64_t(0));
}

TEST(store_settings, writes_csharp_field_names_and_order) {
    SettingsState st;
    CHECK(settings_from_json(kCSharpFile, st));
    std::string out = settings_to_json(st);
    // C# PersistedState / AppSettings 的属性名（区分大小写），顺序同属性声明
    const char* names[] = {"\"Settings\"", "\"TMaxSp\"", "\"PMaxSp\"", "\"RpmMax\"", "\"VolMax\"", "\"StirCurrent\"",
                           "\"StirStandbyPct\"", "\"StirRampMs\"", "\"StirMicrostep\"", "\"StirGearRatio\"", "\"PressFullScaleMPa\"",
                           "\"ApproachDelta\"", "\"ApproachRate\"", "\"ApproachFastRate\"", "\"ApproachFinalZone\"",
                           "\"ApproachSettleBand\"", "\"ApproachSettleSec\"", "\"OverPressure\"", "\"OverTemp\"",
                           "\"StopHeatOnOverPressure\"", "\"HeatTimeout\"", "\"PressDeviation\"", "\"LeakRate\"",
                           "\"LeakReminderDays\"", "\"ProgramGasWaitMin\"", "\"LeakTestPsi\"", "\"LeakSettleMin\"",
                           "\"LeakMeasureMin\"", "\"LastLeakTestUtc\"", "\"VesselVolume\"", "\"GasLineVolume\"",
                           "\"ManifoldVolume\"", "\"EffectiveVolume\"", "\"VolumeCalibUtc\"", "\"BandBounds\"", "\"BandPids\"",
                           "\"BandTune\"", "\"Revision\""};
    size_t pos = 0;
    for (const char* n : names) {
        size_t p = out.find(n, pos);
        CHECK(p != std::string::npos);
        if (p == std::string::npos) { std::printf("    缺少或顺序不对：%s\n", n); break; }
        pos = p;
    }
    CHECK(contains(out, "\"TunedAt\": \"2026-11-18T02:00:00Z\""));   // UTC 带 Z，整秒不带小数
    CHECK(contains(out, "\"TunedSpC\": 150"));                        // 整数值写成整数（同 System.Text.Json）
    CHECK(contains(out, "\"LastLeakTestUtc\": \"2026-11-20T06:30:12.345Z\""));
    CHECK(contains(out, "\"TMaxSp\": 180,"));
    CHECK(contains(out, "\"ManualEditedAt\": null"));
    CHECK(!contains(out, "\"settings\""));   // 不是 camelCase
    // 再读回来完全一样
    SettingsState back;
    CHECK(settings_from_json(out, back));
    CHECK(settings_to_json(back) == out);
    CHECK(back.band_tune == st.band_tune);
    CHECK(back.band_pids == st.band_pids);
}

TEST(store_settings, defaults_roundtrip_and_revision) {
    SettingsState st;
    st.revision = 7;
    st.settings.language = "en";
    SettingsState back;
    CHECK(settings_from_json(settings_to_json(st), back));
    CHECK_EQ(back.revision, int64_t(7));
    CHECK_EQ(back.settings.language, std::string("en"));
    CHECK(back.has_band_pids);   // 写出的文件总带 BandPids
    CHECK_EQ(back.band_pids[0][0].p, 40.0);   // 默认 PID：P 40+20b、I 100+25b、D 15+5b
    CHECK_EQ(back.band_pids[3][2].i, 150.0);
    CHECK_EQ(back.band_pids[3][2].d, 25.0);
    CHECK_NEAR(back.settings.stir_gear_ratio, kStirGearDefaultRatio, 1e-15);
}

TEST(store_settings, old_file_keeps_every_tuned_pid_and_is_backed_up_once) {
    TempDir d;
    SettingsStore store(d.path);
    spit(store.path(), kOldFile);
    auto r = store.load();
    CHECK(r.found);
    CHECK(r.state.has_value());
    auto& st = *r.state;
    CHECK_EQ(st.band_pids[4][1].p, 15.1);
    CHECK_EQ(st.band_pids[0][0].i, 152.0);
    CHECK_EQ(st.band_bounds[0], 100.0);
    CHECK_EQ(st.band_bounds[1], 200.0);
    // 没有整定来历：为空
    for (auto& row : st.band_tune)
        for (auto& m : row) CHECK(!m);
    // 用户原来的设置照旧（转速上限仍是存档里的 1000）；新增项取默认
    CHECK_EQ(st.settings.rpm_max, 1000.0);
    CHECK_EQ(st.settings.vol_max, 50.0);
    CHECK_EQ(st.settings.vessel_volume, 10.0);
    CHECK_NEAR(st.settings.stir_gear_ratio, kStirGearDefaultRatio, 1e-6);
    // 原样备份一次
    std::string bak = store.path() + ".bak-pre-v0.6";
    CHECK_EQ(slurp(bak), std::string(kOldFile));
    CHECK_EQ(r.backup_path, bak);
    spit(bak, "已有的备份");
    store.load();
    CHECK_EQ(slurp(bak), std::string("已有的备份"));   // 不覆盖已有备份
}

TEST(store_settings, new_file_with_band_tune_is_not_backed_up) {
    TempDir d;
    SettingsStore store(d.path);
    spit(store.path(), R"({ "BandPids": [], "BandTune": [] })");
    auto r = store.load();
    CHECK(r.state.has_value());
    CHECK(!reactor::store::file_exists(store.path() + ".bak-pre-v0.6"));
    CHECK(r.state->has_band_pids);   // 有 BandPids（空数组）：不从仪表读种子，同 C#
}

TEST(store_settings, unreadable_file_is_saved_aside_before_returning_empty) {
    TempDir d;
    SettingsStore store(d.path);
    spit(store.path(), "{ 坏掉的文件 ");
    // 本机时区 2026-11-18 09:30:00 → 备份名用本地时间
    auto r = store.load(store_test::local_ms(2026, 11, 18, 9, 30, 0));
    CHECK(r.found);
    CHECK(!r.state.has_value());
    CHECK(!r.error.empty());
    CHECK_EQ(slurp(store.path() + ".bak-unreadable-20261118-093000"), std::string("{ 坏掉的文件 "));
}

TEST(store_settings, wrong_types_are_unreadable_like_system_text_json) {
    SettingsState st;
    CHECK(!settings_from_json(R"({ "Settings": { "TMaxSp": "200" }, "BandTune": [] })", st));
    CHECK(!settings_from_json(R"({ "Settings": { "TMaxSp": null }, "BandTune": [] })", st));
    CHECK(!settings_from_json(R"({ "BandPids": [[[1, null, 3]]], "BandTune": [] })", st));
    CHECK(!settings_from_json(R"({ "Settings": { "LastLeakTestUtc": "昨天" } })", st));
    CHECK(!settings_from_json("[1,2]", st));
    // 未知字段忽略；属性名区分大小写（tmaxsp 不认，取默认）
    CHECK(settings_from_json(R"({ "Settings": { "tmaxsp": 123, "Whatever": [1] }, "Extra": 1 })", st));
    CHECK_EQ(st.settings.t_max_sp, 200.0);
    CHECK(!st.has_band_pids);
}

TEST(store_settings, missing_file_returns_empty_without_backup) {
    TempDir d;
    SettingsStore store(d.path);
    auto r = store.load();
    CHECK(!r.found);
    CHECK(!r.state.has_value());
    CHECK(reactor::store::list_files(d.path).empty());
}

TEST(store_settings, partial_band_data_import_rules) {
    // C# PidTuneTests.Persistence_OldFileWithoutBandTune_AndPartialData_Load + TempService.ImportBandPids
    SettingsState st;
    CHECK(settings_from_json(
        R"({"BandBounds":[1,2,3],"BandPids":[[[1,2,3],[4,5],null]],"BandTune":[[null,{"TunedSpC":120,"TunedAt":"2026-11-01T08:00:00","ManualEditedAt":null},{"TunedSpC":null,"TunedAt":null,"ManualEditedAt":null},{"TunedSpC":1}],null]})",
        st));
    CHECK_EQ(st.band_bounds[0], 100.0);   // 长度不是 2：不用
    CHECK(st.band_pids[0][0] == (Pid{1, 2, 3}));
    CHECK(st.band_pids[0][1] == default_band_pid(1));   // 长度不是 3：留默认
    CHECK(!st.band_tune[0][0]);
    CHECK(st.band_tune[0][1] == std::optional<BandTuneMeta>(BandTuneMeta{120.0, ms_at(2026, 11, 1, 8, 0, 0), std::nullopt}));   // 不带时区按 UTC
    CHECK(!st.band_tune[0][2]);   // 三项全空当作没有
    CHECK(!st.band_tune[1][0]);
}

TEST(store_settings, tune_meta_complete_and_manual) {
    // C# PidTuneTests.ManualEdit_KeepsTuning_MarksTime_NextTuneClearsMark
    SettingsState st;
    int64_t t0 = ms_at(2026, 11, 18, 2, 0, 0);
    tune_complete(st, 0, 0, 80.0, t0);
    tune_mark_manual(st, 0, 0, t0 + 3600000);
    CHECK(st.band_tune[0][0] == std::optional<BandTuneMeta>(BandTuneMeta{80.0, t0, t0 + 3600000}));
    tune_mark_manual(st, 0, 2, t0 + 7200000);
    CHECK(st.band_tune[0][2] == std::optional<BandTuneMeta>(BandTuneMeta{std::nullopt, std::nullopt, t0 + 7200000}));
    tune_complete(st, 0, 0, 90.0, t0 + 86400000);
    CHECK(st.band_tune[0][0] == std::optional<BandTuneMeta>(BandTuneMeta{90.0, t0 + 86400000, std::nullopt}));
    tune_mark_manual(st, 0, 7, t0);   // 越界档位忽略
    tune_mark_manual(st, 0, -1, t0);
    tune_complete(st, 9, 0, 1.0, t0);
}

TEST(store_settings, save_now_is_atomic_and_debounce_merges) {
    TempDir d;
    SettingsStore store(d.path + "/sub/dir");   // 目录不存在时自动建
    SettingsState st;
    st.settings.t_max_sp = 150;
    store.save_soon(st, 1000);
    st.settings.t_max_sp = 160;
    store.save_soon(st, 1500);   // 800 ms 内的修改合并，到期时间顺延
    CHECK(!store.poll(2000));
    CHECK(!reactor::store::file_exists(store.path()));
    CHECK(store.pending());
    CHECK(store.poll(2300));
    CHECK(!store.pending());
    auto r = store.load();
    CHECK(r.state.has_value());
    CHECK_EQ(r.state->settings.t_max_sp, 160.0);
    CHECK(!store.poll(5000));   // 没有待写的
    st.settings.t_max_sp = 170;
    store.save_soon(st, 6000);
    CHECK(store.flush());       // 退出前立即写
    CHECK_EQ(store.load().state->settings.t_max_sp, 170.0);
    // 没有残留的临时文件
    for (auto& n : reactor::store::list_files(d.path + "/sub/dir")) CHECK(n == "settings.json");
}

// ---------------- 设置项表（SettingSpecsTests）----------------

TEST(store_settings, spec_table_covers_every_key_once) {
    auto& all = setting_specs::all();
    CHECK_EQ(all.size(), size_t(28));
    std::set<int> keys;
    std::set<std::string> names;
    for (auto& s : all) {
        keys.insert(static_cast<int>(s.key));
        names.insert(s.name);
    }
    CHECK_EQ(keys.size(), size_t(28));
    CHECK_EQ(names.size(), size_t(28));
    CHECK_EQ(*keys.rbegin(), static_cast<int>(SettingKey::StirGearRatio));
    CHECK(setting_specs::find("OverTemp") == &setting_specs::of(SettingKey::OverTemp));
    CHECK(setting_specs::find("overTemp") == nullptr);
}

TEST(store_settings, each_spec_has_sane_range_and_step) {
    for (auto& s : setting_specs::all()) {
        CHECK(s.min < s.max);
        CHECK(s.step > 0);
        CHECK(std::string(s.label).size() > 0);
    }
}

TEST(store_settings, defaults_inside_range_and_get_set_roundtrip) {
    AppSettings st;
    for (auto& s : setting_specs::all()) {
        double v = setting_specs::get(st, s.key);
        CHECK(v >= s.min && v <= s.max);
        setting_specs::set(st, s.key, s.max);
        CHECK_EQ(setting_specs::get(st, s.key), s.max);
    }
}

TEST(store_settings, device_keys_are_local_only) {
    for (auto k : {SettingKey::StirCurrent, SettingKey::StirStandbyPct, SettingKey::StirRampMs, SettingKey::StirMicrostep,
                   SettingKey::PressFullScaleMPa}) {
        CHECK(setting_specs::of(k).writes_device);
        CHECK(setting_specs::of(k).exposure == Exposure::LocalOnly);
    }
    for (auto& s : setting_specs::all())
        if (!s.writes_device) CHECK(s.exposure == Exposure::Remote);
}

TEST(store_settings, validate_boundaries) {
    struct Case { SettingKey k; double v; bool ok; };
    Case cases[] = {{SettingKey::TMaxSp, 50, true},       {SettingKey::TMaxSp, 300, true},     {SettingKey::TMaxSp, 49.999, false},
                    {SettingKey::TMaxSp, 300.001, false}, {SettingKey::ApproachRate, 0.02, true}, {SettingKey::ApproachRate, 0.01, false},
                    {SettingKey::StirStandbyPct, 0, true}, {SettingKey::StirStandbyPct, -1, false}};
    for (auto& c : cases) {
        auto r = setting_specs::validate(c.k, c.v);
        CHECK_EQ(c.ok, !r.has_value());
        if (!c.ok && r) {
            CHECK_EQ(r->code, std::string("PARAM_OUT_OF_RANGE"));
            CHECK(contains(r->message, setting_specs::of(c.k).label));
        }
    }
    CHECK(setting_specs::validate(SettingKey::OverTemp, std::numeric_limits<double>::quiet_NaN()).has_value());
    CHECK(setting_specs::validate(SettingKey::OverTemp, std::numeric_limits<double>::infinity()).has_value());
    CHECK_EQ(setting_specs::validate(SettingKey::TMaxSp, 10)->message, std::string("温度 SP 上限须在 50–300 °C 之间"));
    CHECK_EQ(setting_specs::validate(SettingKey::ApproachRate, 5)->message, std::string("末段速率须在 0.02–2 °C/min 之间"));
}

TEST(store_settings, cross_check_rules) {
    AppSettings st;
    st.t_max_sp = 200;
    st.over_temp = 250;
    CHECK(!setting_specs::cross_check(st, SettingKey::TMaxSp, 245));
    CHECK_EQ(setting_specs::cross_check(st, SettingKey::TMaxSp, 250)->code, std::string("PARAM_CROSS_CHECK"));
    CHECK_EQ(setting_specs::cross_check(st, SettingKey::OverTemp, 200)->code, std::string("PARAM_CROSS_CHECK"));
    CHECK(!setting_specs::cross_check(st, SettingKey::OverTemp, 201));
    AppSettings s2;
    s2.leak_test_psi = 50;
    s2.over_pressure = 100;
    CHECK(!setting_specs::cross_check(s2, SettingKey::LeakTestPsi, 89));
    auto r = setting_specs::cross_check(s2, SettingKey::LeakTestPsi, 90);
    CHECK(r.has_value());
    CHECK(contains(r->message, "90%"));
    CHECK(setting_specs::cross_check(s2, SettingKey::OverPressure, 55).has_value());
    CHECK(!setting_specs::cross_check(AppSettings{}, SettingKey::HeatTimeout, 30));
}

TEST(store_settings, patch_with_revision) {
    SettingsState st;
    auto r = patch_settings(st, {{SettingKey::OverTemp, 240}, {SettingKey::HeatTimeout, 30}}, int64_t(0));
    CHECK(r.ok);
    CHECK_EQ(r.revision, int64_t(1));
    CHECK_EQ(r.changed.size(), size_t(2));
    CHECK_EQ(st.settings.over_temp, 240.0);
    // 旧 revision → 冲突，什么都不改
    auto c = patch_settings(st, {{SettingKey::OverTemp, 230}}, int64_t(0));
    CHECK(!c.ok);
    CHECK_EQ(c.code, std::string("REVISION_CONFLICT"));
    CHECK_EQ(st.settings.over_temp, 240.0);
    // 值没变：成功但 revision 不涨
    auto same = patch_settings(st, {{SettingKey::OverTemp, 240}}, int64_t(1));
    CHECK(same.ok);
    CHECK(same.changed.empty());
    CHECK_EQ(st.revision, int64_t(1));
    // 一项越界 → 全部不改
    auto bad = patch_settings(st, {{SettingKey::HeatTimeout, 40}, {SettingKey::TMaxSp, 10}}, std::nullopt);
    CHECK(!bad.ok);
    CHECK_EQ(bad.code, std::string("PARAM_OUT_OF_RANGE"));
    CHECK(bad.bad_key == std::optional<SettingKey>(SettingKey::TMaxSp));
    CHECK_EQ(st.settings.heat_timeout, 30.0);
    // 交叉检查按改完的值：同时降低超温报警值和温度 SP 上限是合法的
    auto both = patch_settings(st, {{SettingKey::OverTemp, 150}, {SettingKey::TMaxSp, 140}}, int64_t(1));
    CHECK(both.ok);
    auto cross = patch_settings(st, {{SettingKey::TMaxSp, 150}}, std::nullopt);
    CHECK_EQ(cross.code, std::string("PARAM_CROSS_CHECK"));
    // 写设备的项：远程不允许
    auto dev = patch_settings(st, {{SettingKey::StirCurrent, 1.5}}, std::nullopt, false);
    CHECK_EQ(dev.code, std::string("NOT_EXPOSED"));
    CHECK(patch_settings(st, {{SettingKey::StirCurrent, 1.5}}, std::nullopt, true).ok);
}

TEST(store_settings, band_bounds_and_pid_rules) {
    SettingsState st;
    CHECK(set_band_bounds(st, 120, 220, int64_t(0)).ok);
    CHECK_EQ(st.revision, int64_t(1));
    auto bad = set_band_bounds(st, 215, 220, std::nullopt);   // 低/中分界须 ≤ 中温上限 − 10
    CHECK(!bad.ok);
    CHECK_EQ(bad.message, std::string("低温 / 中温 分界须在 10–210 ℃ 之间"));
    CHECK(!set_band_bounds(st, 100, 395, std::nullopt).ok);
    CHECK_EQ(set_band_bounds(st, 100, 200, int64_t(0)).code, std::string("REVISION_CONFLICT"));
    auto rg = band_ranges(st.band_bounds);
    CHECK_EQ(rg[0].hi, 120.0);
    CHECK_EQ(rg[2].lo, 220.0);
    CHECK_EQ(rg[2].hi, 400.0);
    CHECK(!rules::pid(0, 3200, 3200, 327));
    CHECK(rules::pid(3, 1, 1, 1).has_value());
    CHECK_EQ(rules::pid(0, 1, 1, 328)->message, std::string("微分时间 D 须在 0–327 s 之间"));
}
