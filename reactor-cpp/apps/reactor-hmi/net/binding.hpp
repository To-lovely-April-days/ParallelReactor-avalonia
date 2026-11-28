// JSON → model 结构体（纯函数，不碰网络、不碰 LVGL，自检直接调）。
// 形状：现有的照 C# DTO（Contracts/ParallelReactor.Contracts/Api/*.cs，camelCase），新增的照 doc/接口契约-新增.md。
// 规则：
//   - 线上压力一律 psi（表压）；model 里标了「MPa」的字段（PlanExt.pressPlan、Trend.press、AlarmExt 的量值、
//     RunRecord.pressMPa / abort、CalibReading）在这里换算。程序库条目是 PC 端的形状，压力存 MPa，进 model 时换成 psi。
//   - 时间：ISO-8601 字符串（「…Z」）或 UNIX 毫秒数字都认，统一成 UTC 毫秒。
//   - 缺字段 / 类型不对：保留结构体默认值，不报错（后端新字段可以晚一点上线）。
#pragma once
#include "../model/model.hpp"
#include "nlohmann/json.hpp"

#include <array>
#include <map>
#include <memory>
#include <string>
#include <vector>
#include <cstdint>
#include <optional>

namespace reactor::hmi::net {

using nlohmann::json;
namespace m = reactor::hmi::model;

constexpr double kPsiPerMPa = 145.0377377;   // 1 MPa = 145.0377 psi
inline double psi2mpa(double psi) { return psi / kPsiPerMPa; }
inline double mpa2psi(double mpa) { return mpa * kPsiPerMPa; }
constexpr double kKPaPerPsi = 6.894757293;

// ---- 小工具 ----
std::optional<int64_t> j_time(const json& j, const char* k);   // ISO 字符串或毫秒数
double j_num(const json& j, const char* k, double def = 0);
std::optional<double> j_optnum(const json& j, const char* k);
int64_t j_int(const json& j, const char* k, int64_t def = 0);
bool j_bool(const json& j, const char* k, bool def = false);
std::string j_str(const json& j, const char* k, const std::string& def = "");
std::optional<std::string> j_optstr(const json& j, const char* k);

// ---- 遥测快照（WS telemetry / GET /snapshot）+ 触屏扩展 ----
struct SnapExt {
    std::array<m::ChanSetExt, 8> chanset{};
    bool hasChanset = false;
    std::string languageCode;            // system.languageCode（空 = 没给）
    int64_t bootAt = 0;                  // system.bootAt
    std::map<std::string, int64_t> revs; // revs.*（没有这一项 = 后端还不支持，界面改为定时刷新）
    bool hasRevs = false;
};
bool parse_snapshot(const json& j, m::TelemetrySnapshot& out, SnapExt& ext);
m::ChannelSnap parse_channel(const json& j);
// 快照里的通道补满 8 个（id 1..8），界面按下标取
void pad_channels(std::vector<m::ChannelSnap>& v);

// ---- 报警 / 事件 ----
m::AlarmItem parse_alarm(const json& j);
m::EventRecord parse_event(const json& j);

// ---- GET /settings（含 ui.* / program.* / storage.* 扩展）----
void parse_settings(const json& j, m::SettingsDto& s, m::PagesExt& x);
// 界面用的设置键（model/actions.hpp 的 patch_setting）→ 线上 PATCH /settings 的键（接口契约 §3.1）
std::string wire_setting_key(const std::string& ui_key);
bool setting_is_bool(const std::string& wire_key);
bool setting_is_string(const std::string& wire_key);

// ---- WS progress ----
void parse_progress(const json& j, std::array<m::ProgressExt, 8>& prog, std::array<m::PlanExt, 8>& plans, m::PidExt& pid);
std::string fmt_step_target(const json& t);   // {quantity, value} → 「80 °C」「2.000 MPa」

// ---- 各页的数据 ----
void parse_device(const json& j, m::PagesExt& x, int64_t* bootAt);
void parse_comm(const json& j, m::CommExt& c);
void parse_network(const json& j, m::NetworkExt& n);
void parse_tls(const json& j, m::TlsExt& t);   // GET /system/tls
void parse_usb(const json& j, m::UsbExt& u);
void parse_storage(const json& j, m::StorageExt& s);
void parse_users(const json& j, m::UsersExt& u);
void parse_pid(const json& j, m::PidExt& p);
void parse_calib(const json& j, m::CalibExt& c);
std::vector<m::RunRecord> parse_runs(const json& j);        // GET /runs
m::RunRecord parse_run(const json& j);                      // 一条（列表项或 GET /runs/{id}）
std::shared_ptr<m::Trend> parse_trend(const json& j);        // GET /trend、runs/{id}.trend
std::vector<m::HistoryFileInfo> parse_files(const json& j);  // GET /files?type=…
std::vector<m::DayInfo> build_days(const std::vector<m::HistoryFileInfo>& files, const std::vector<m::RunRecord>& runs);
std::vector<m::LeakHistoryItem> parse_leak_tests(const json& j);
std::vector<m::LeakSample> parse_leak_samples(const json& j);
std::vector<m::EventRecord> parse_events_page(const json& j, int* epoch, int64_t* head, bool* more);
std::vector<m::AlarmItem> parse_alarms(const json& j);
std::string fmt_channels(const std::vector<int>& chs);       // 「1–4 号」「1、3、5 号」

// ---- 程序 ----
// GET /channels/{id}/program（§6.9 + name / sourceId）；没有程序 → false
bool parse_channel_program(const json& j, m::ProgramDoc& doc, std::string& source_id);
json steps_to_wire(const m::ProgramDoc& p);                 // §6.9 的 steps（压力 psi）
m::ProgramDoc program_from_wire(const json& prog);           // §6.9（psi）→ ProgramDoc
// 程序库（PC 端形状，压力 MPa / 压降 MPa/h）
std::vector<m::LibEntry> parse_library(const json& j, bool tpl, const m::ProgramSchema& schema, int* max);
json library_to_wire(const m::ProgramDoc& p, bool tpl, const m::ProgramSchema& schema);

}  // namespace reactor::hmi::net
