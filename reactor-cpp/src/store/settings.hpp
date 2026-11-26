// 全局设置 + 温度档位边界 + 8 通道 × 3 档 PID + 各档整定来历 + 容积标定，存 <config>/settings.json。
// C#: Models/AppSettings.cs、Models/SettingSpecs.cs、Models/ValueRules.cs（只抄设置校验要用的几条）、
//     Service/SettingsStore.cs、Service/SettingsFileGuard.cs、Service/AutotuneBook.cs（持久化部分）。
//
// 文件格式和 C# PersistedState 完全相同（System.Text.Json 默认选项：属性名 PascalCase、缩进 2 空格）：
//   { "Settings": { "TMaxSp": 200, … }, "BandBounds": [100, 200], "BandPids": [[[P,I,D]×3]×8],
//     "BandTune": [[{ "TunedSpC", "TunedAt", "ManualEditedAt" } | null ×3]×8], "Revision": 3 }
// "Revision" 是新加的（C# 读的时候忽略未知字段，所以旧程序照样能读新文件）；"Settings.Language" 同理。
//
// 线程：SettingsState / patch_settings / SettingSpecs 都是普通值和纯函数，不加锁；
// SettingsStore 只给一个线程（控制线程）用：load / save_soon / poll / flush 都在那个线程调。
#pragma once

#include <array>
#include <cstdint>
#include <functional>
#include <optional>
#include <string>
#include <vector>

namespace reactor::store {

constexpr int kChannelCount = 8;
constexpr int kBandCount = 3;

// 搅拌传动比默认值：电机 38 齿带釜上 85 齿（C#: Models/StirGear.cs DefaultRatio）
constexpr double kStirGearDefaultRatio = 85.0 / 38.0;

// C#: Models/AppSettings.cs（默认值逐个照抄）。时间一律 UTC 毫秒。
struct AppSettings {
    // 可设置参数范围（上限）
    double t_max_sp = 200;        // 温度 SP 上限 °C
    double p_max_sp = 500;        // 压力 SP 上限 psi
    double rpm_max = 450;         // 搅拌转速上限 rpm（釜里的转速）
    double vol_max = 8;           // 溶液体积上限 mL
    // 搅拌电机（DM2C）
    double stir_current = 1.0;        // 峰值电流 A
    double stir_standby_pct = 50;     // 待机电流 %
    double stir_ramp_ms = 1000;       // 加减速 ms/1000rpm
    double stir_microstep = 10000;    // 细分 脉冲/转
    double stir_gear_ratio = kStirGearDefaultRatio;   // 电机 ÷ 釜
    double press_full_scale_mpa = 4.137;              // 压力变送器满量程 MPa
    // 定值加热防超调逼近
    double approach_delta = 5;
    double approach_rate = 0.2;
    double approach_fast_rate = 1.0;
    double approach_final_zone = 1.0;
    double approach_settle_band = 0.2;
    double approach_settle_sec = 90;
    // 报警阈值
    double over_pressure = 510;               // psi
    double over_temp = 250;                   // °C
    bool stop_heat_on_over_pressure = true;
    double heat_timeout = 20;                 // 分钟
    double press_deviation = 8;               // psi
    double leak_rate = 2;                     // psi/hr
    double leak_reminder_days = 7;            // 天
    double program_gas_wait_min = 60;         // 分钟
    // 泄漏测试
    double leak_test_psi = 50;
    double leak_settle_min = 10;
    double leak_measure_min = 30;
    std::optional<int64_t> last_leak_test_utc_ms;
    // 吸气量计算
    double vessel_volume = 10;    // mL
    double gas_line_volume = 0;   // mL
    // 容积标定
    std::optional<double> manifold_volume;                               // 总管体积 mL
    std::array<std::optional<double>, kChannelCount> effective_volume{};   // 各通道有效容积 mL（空 = 未标定）
    std::array<std::optional<int64_t>, kChannelCount> volume_calib_utc_ms{};
    // 新增：界面语言（PUT /system/language；C# 没有这一项，读旧文件时取默认）
    std::string language = "zh-CN";
    // 新增（接口契约 §3.1，触屏界面偏好与数据保留；C# 没有这几项，读旧文件时取默认，C# 读新文件时忽略）
    std::string ui_pressure_unit = "MPa";   // MPa / bar / psi
    std::string ui_temp_unit = "C";         // C / F
    std::string ui_theme = "light";         // light / dark
    std::string ui_keyboard = "num";        // num / full
    bool ui_preflight_checklist = true;
    double storage_retention_days = 90;     // 7..3650：运行记录 / 每日文件保留天数
};

struct Pid {
    double p = 0, i = 0, d = 0;
    bool operator==(const Pid& o) const { return p == o.p && i == o.i && d == o.d; }
};

// C#: Service/AutotuneBook.cs BandTuneMeta。三项都为空的记录不存在（没有来历 = nullopt）。
struct BandTuneMeta {
    std::optional<double> tuned_sp_c;
    std::optional<int64_t> tuned_at_ms;
    std::optional<int64_t> manual_edited_at_ms;
    bool operator==(const BandTuneMeta& o) const {
        return tuned_sp_c == o.tuned_sp_c && tuned_at_ms == o.tuned_at_ms && manual_edited_at_ms == o.manual_edited_at_ms;
    }
};

using BandPidTable = std::array<std::array<Pid, kBandCount>, kChannelCount>;
using BandTuneTable = std::array<std::array<std::optional<BandTuneMeta>, kBandCount>, kChannelCount>;

// 每档预置的 PID（C#: Service/TempService.cs TempChannel 构造：P 40+20b、I 100+25b、D 15+5b）
Pid default_band_pid(int band);

// 档位温区（C#: TempService.Bands 默认 低温 0–100 / 中温 100–200 / 高温 200–400，分界可改）
struct BandRange { double lo, hi; };
std::array<BandRange, kBandCount> band_ranges(const std::array<double, 2>& bounds);

// settings.json 的全部内容（已按 C# 的导入规则展开成固定大小）。
struct SettingsState {
    AppSettings settings;
    std::array<double, 2> band_bounds{100, 200};   // [低/中分界, 中/高分界]
    BandPidTable band_pids;                         // [通道][档]
    BandTuneTable band_tune{};                      // [通道][档] 来历
    // 存档里有 BandPids（C#: InitAsync(seedFromInstrument: persisted?.BandPids == null)）：
    // false = 没有存档 PID，控制层可以从仪表读 PID 作种子
    bool has_band_pids = false;
    int64_t revision = 0;   // 每次成功修改 +1（PATCH /settings 防覆盖）

    SettingsState();
};

// ---- JSON ----
// 序列化成 C# PersistedState 的样子（字段顺序 = C# 属性声明顺序，缩进 2 空格，末尾不带换行）
std::string settings_to_json(const SettingsState& st);
// 解析（规则同 C# System.Text.Json 默认：属性名区分大小写、未知字段忽略、类型不对 → 整个文件读不了）。
// 导入规则同 C#：BandBounds 长度为 2 才用；BandPids 每项长度为 3 才用、缺的留默认；BandTune 三项全空当作没有。
bool settings_from_json(const std::string& text, SettingsState& out, std::string* err = nullptr);

// ---- 整定来历（C#: AutotuneBook 的持久化部分）----
// 整定完成：来历替换为 {目标温度, 完成时刻, 手改 = 空}；sp 为空 = 仪表自己发起、不知道目标温度
void tune_complete(SettingsState& st, int ch_index, int band, std::optional<double> sp, int64_t now_utc_ms);
// 手改 PID：保留整定记录，只更新手改时刻；越界档位忽略
void tune_mark_manual(SettingsState& st, int ch_index, int band, int64_t now_utc_ms);

// ============================ 设置项表 + 校验 ============================

// C#: Models/SettingSpecs.cs SettingKey（顺序即设置页显示顺序）
enum class SettingKey {
    TMaxSp, PMaxSp, RpmMax, VolMax,
    OverPressure, OverTemp, HeatTimeout, PressDeviation, LeakRate, LeakReminderDays, ProgramGasWaitMin,
    LeakTestPsi, LeakSettleMin, LeakMeasureMin,
    ApproachDelta, ApproachFastRate, ApproachFinalZone, ApproachRate, ApproachSettleBand, ApproachSettleSec,
    StirCurrent, StirStandbyPct, StirRampMs, StirMicrostep,
    PressFullScaleMPa,
    VesselVolume, GasLineVolume,
    StirGearRatio,
};

enum class Exposure { Remote, LocalOnly };

struct SettingSpec {
    SettingKey key;
    const char* name;    // 和 AppSettings 的 JSON 属性名相同（"TMaxSp"），PATCH 正文用
    const char* label;   // 中文标签
    const char* unit;
    double min, max, step;
    const char* sub;     // 说明（可为 nullptr）
    Exposure exposure;
    bool writes_device;
    double AppSettings::*field;
};

// 数值越界：错误码 + 中文消息 + detail（C#: Models/ValueRules.cs RuleViolation）
struct RuleViolation {
    std::string code;      // PARAM_OUT_OF_RANGE / PARAM_CROSS_CHECK
    std::string message;
    double min, max, value;
};

namespace setting_specs {
const std::vector<SettingSpec>& all();
const SettingSpec& of(SettingKey key);
const SettingSpec* find(const std::string& name);   // 按 JSON 名（区分大小写）
double get(const AppSettings& s, SettingKey key);
void set(AppSettings& s, SettingKey key, double v);
// 范围检查（越界 / NaN / ∞ → PARAM_OUT_OF_RANGE，消息带范围）
std::optional<RuleViolation> validate(SettingKey key, double value);
constexpr double kLeakTestMaxRatio = 0.9;
// 交叉校验：温度 SP 上限 < 超温报警值；泄漏测试压力 < 超压报警值 × 90%（→ PARAM_CROSS_CHECK）
std::optional<RuleViolation> cross_check(const AppSettings& current, SettingKey key, double value);
}  // namespace setting_specs

// ValueRules 里设置相关的几条（本地拷贝；core/ 里另有完整移植）
namespace rules {
constexpr double kPidPMax = 3200, kPidIMax = 3200, kPidDMax = 327;
constexpr double kBandBoundMin = 10, kBandBoundMax = 390, kBandGap = 10;
std::string fmt_n(double v);   // C# v.ToString("0.##")
std::optional<RuleViolation> range(const std::string& label, double v, double min, double max, const std::string& unit = "");
std::optional<RuleViolation> pid(int band, double p, double i, double d, int band_count = kBandCount);
// which = 1：低/中分界（10..中温上限−10）；which = 2：中/高分界（低温上限+10..390）
std::optional<RuleViolation> band_bound(int which, double value, double low_hi, double mid_hi);
}  // namespace rules

// ---- 修改（PATCH /settings、PUT /pid/bands 用）----
struct SettingChange {
    SettingKey key;
    double value;
};

struct PatchResult {
    bool ok = false;
    std::string code;      // 成功为空；REVISION_CONFLICT / PARAM_OUT_OF_RANGE / PARAM_CROSS_CHECK / NOT_EXPOSED
    std::string message;
    std::optional<RuleViolation> violation;
    std::optional<SettingKey> bad_key;
    int64_t revision = 0;               // 成功后的 revision（失败时为当前 revision）
    std::vector<SettingKey> changed;    // 实际变了的项（值相同的不算）
};

// 全有或全无：先核对 expected_revision（给了且不等 → REVISION_CONFLICT），再逐项范围检查，最后按改完的值做交叉检查，
// 全部通过才写回 st；有实际变化时 revision + 1。allow_local_only = false 时写设备的项 → NOT_EXPOSED。
PatchResult patch_settings(SettingsState& st, const std::vector<SettingChange>& changes,
                           std::optional<int64_t> expected_revision, bool allow_local_only = true);

// 档位分界（PUT /pid/bands）：两条都校验通过才写；有变化时 revision + 1
PatchResult set_band_bounds(SettingsState& st, double low_mid, double mid_high, std::optional<int64_t> expected_revision);

// ============================ 文件 ============================

// C#: Service/SettingsFileGuard.cs
namespace settings_guard {
extern const char kPreV06Tag[];   // "pre-v0.6"
// 把原文另存为 <path>.bak-<tag>（同名备份已存在则不再写）。失败不影响启动。返回这次写了的备份路径，没写为空
std::string backup_once(const std::string& path, const std::string& text, const std::string& tag);
}  // namespace settings_guard

class SettingsStore {
public:
    static constexpr int64_t kDebounceMs = 800;   // C#: SettingsStore.SaveSoon

    // config_dir：C# 默认 ~/.config/ParallelReactor，新系统默认 /etc/reactord（由 apps/reactord 传入）
    explicit SettingsStore(std::string config_dir);

    const std::string& dir() const { return dir_; }
    std::string path() const;   // <dir>/settings.json

    struct LoadResult {
        bool found = false;                    // 文件存在
        std::optional<SettingsState> state;    // 读成功；文件不存在 / 读不了为空（调用方用默认值冷启动）
        std::string backup_path;               // 这次写了的备份（.bak-pre-v0.6 / .bak-unreadable-时间），没有为空
        std::string error;                     // 读不了的原因
    };
    // 读存档（C#: SettingsFileGuard.Read）：没有 "BandTune" 的旧存档先原样备份为 .bak-pre-v0.6（只一次）；
    // 读不了先另存为 .bak-unreadable-yyyyMMdd-HHmmss 再返回空。now_utc_ms 给备份名用（缺省取当前时间，按本机时区）。
    LoadResult load(std::optional<int64_t> now_utc_ms = std::nullopt) const;

    // 立即原子写盘（tmp + fsync + rename）
    bool save_now(const SettingsState& st, std::string* err = nullptr);

    // 防抖保存：记下最新快照，800 ms 内再来的修改合并；poll 到期时写盘
    void save_soon(const SettingsState& st, int64_t now_mono_ms);
    // 到期则写盘，返回 true = 这次写了（控制线程的定时器里周期调用）
    bool poll(int64_t now_mono_ms);
    // 有待写的就立即写（退出前调用）
    bool flush();
    bool pending() const { return pending_.has_value(); }
    const std::string& last_error() const { return last_error_; }

private:
    std::string dir_;
    std::optional<SettingsState> pending_;
    int64_t due_mono_ms_ = 0;
    std::string last_error_;
};

}  // namespace reactor::store
