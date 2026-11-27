// 页面发出的用户意图。页面只调这里，不知道背后是网络还是模拟。
// net/backend.hpp 的 NetActions 实现这个接口（每个方法对应 /api/v1 的一个写命令，注释里是端点，新端点见 doc/接口契约-新增.md）；
// --sim 时用 LoggingActions：打印出来，并（可选）转给模拟数据源，让界面有反应。
// 所有方法只在 LVGL 线程调用；实现方不能阻塞（网络实现要把请求投递到网络线程）。
// 命令的结果（成功 / 失败 + 中文文案）经 on_result 回到界面（main.cpp 接成 toast）。
#pragma once
#include "model.hpp"

#include <functional>
#include <string>
#include <vector>
#include <cstdint>

namespace reactor::hmi::model {

// 导出到 U 盘（POST /export）：按一次运行 / 一个文件 / 一天 / 一个时间段
struct ExportReq {
    std::string runId, file, day;               // 三选一，或都空 = 按时间段
    int64_t from = 0, to = 0;                   // 时间段（UTC 毫秒）
    std::vector<std::string> kinds;             // csv / pdf / events / program（按运行）；telemetry / events（按时间段）
    std::vector<int> channels;                  // 空 = 全部
    int stepSec = 0;                            // 读数间隔：0 = 原始（1.2 s），15，60
};

class Actions {
public:
    virtual ~Actions() = default;
    // —— 全局 ——
    virtual void stop_all(bool include_stir) = 0;                 // POST /stop-all {stopStir}（旧 PC 的 /estop 同义）
    virtual void stir_set_rpm(int rpm) = 0;                       // PUT /stir {rpm}
    virtual void stir_run(bool on) = 0;                           // PUT /stir {on}
    virtual void manual_valve(bool on) = 0;                       // PUT /gas/manual {on}（本地）
    // —— 单路加热 ——
    virtual void set_temp_sp(int ch, double c) = 0;               // PUT /channels/{id}/temperature/setpoint {tempC}
    virtual void heat(int ch, bool on) = 0;                       // POST /channels/{id}/heat/start|stop
    virtual void set_heat_mode(int ch, const std::string& mode) = 0;  // PUT /channels/{id}/temperature/mode {spMode: fixed / curve}
    virtual void set_band(int ch, double c) = 0;                  // PUT /channels/{id}/temperature/band {bandC}（新）
    virtual void set_anti_overshoot(int ch, bool on) = 0;         // PUT /channels/{id}/temperature/anti-overshoot {on}（新）
    virtual void set_profile(int ch, const std::vector<CurveSeg>& segs) = 0;  // PUT /channels/{id}/profile
    virtual void start_profile(int ch) = 0;                       // POST /channels/{id}/profile/start
    // —— 单路气体 ——
    virtual void set_valve(int ch, bool open) = 0;                // PUT /channels/{id}/valve {open}（要先开手动阀控）
    // 气路图下面的气源阀 / 排空阀：k 0 惰性气体 1 气体 A 2 气体 B → PUT /gas/inlets/{inert|A|B}；k 3 排空 → PUT /gas/vent（要先开手动阀控）
    virtual void set_gas_source(int k, bool open) = 0;
    virtual void gas_zero(int ch) = 0;                            // POST /channels/{id}/gas-counter/reset
    virtual void set_solution_volume(int ch, double ml) = 0;      // PUT /channels/{id}/volume
    // —— 单路其他 ——
    virtual void channel_enable(int ch, bool on) = 0;             // POST /channels/{id}/enable | /disable（界面里 idle = 停用）
    virtual void copy_to_all(int ch) = 0;                         // POST /channels/copy-config {from, to[]}：复制到其余启用、没跑程序的路
    virtual void abort_program(int ch) = 0;                       // DELETE /channels/{id}/program/run
    // —— 报警 ——
    virtual void ack_alarm(const std::string& id) = 0;            // POST /alarms/{id}/ack
    virtual void ack_all() = 0;                                   // POST /alarms/ack-all
    // —— 本机 ——
    virtual void set_language(const std::string& code) = 0;       // PUT /system/language
    // ================= 第二批 =================
    // —— 程序（§6.9 程序 JSON，压力 psi）——
    virtual void put_program(int ch, const ProgramDoc& p) = 0;    // PUT /channels/{id}/program
    virtual void start_program(int ch) = 0;                       // POST /channels/{id}/program/run（先 PUT 再 run = 「下发并运行」）
    // —— 程序库（新端点；带 revision，冲突回 409 REVISION_CONFLICT / NAME_CONFLICT）——
    virtual void lib_save(bool tpl, const ProgramDoc& p) = 0;     // id 空：POST /library/{programs|templates}；否则 PUT …/{id}（带 revision）
    virtual void lib_delete(bool tpl, const std::string& id, int64_t revision) = 0;  // DELETE /library/…/{id}?revision=N
    virtual void lib_import_usb() = 0;                            // POST /library/import（从 U 盘导入 PC 的 library.json）
    // —— 泄漏测试 ——
    virtual void leak_start(const std::vector<int>& chs) = 0;     // POST /leak-tests {channels}
    virtual void leak_abort() = 0;                                // DELETE /leak-tests/current
    // —— 记录 / 导出 ——
    virtual void run_note(const std::string& run_id, const std::string& note) = 0;  // PUT /runs/{id}/note
    virtual void export_usb(const ExportReq& r) = 0;              // POST /export
    // —— 设置（本地专用）——
    virtual void patch_setting(const std::string& key, double v) = 0;            // PATCH /settings {revision, values{key: v}}（key 是 JSON 路径，如 "alarms.overTempC"）
    virtual void patch_setting_str(const std::string& key, const std::string& v) = 0;
    virtual void set_pid(int ch, int band, double p, double i, double d) = 0;   // PUT /channels/{id}/pid/{band}
    virtual void set_pid_bands(double lo, double hi) = 0;         // PUT /pid/bands
    virtual void autotune(int ch, int band, double sp_c) = 0;     // POST /channels/{id}/autotune {tempC, band}
    virtual void autotune_cancel(int ch) = 0;                     // DELETE /channels/{id}/autotune
    virtual void calib_start(int ch, double water_ml, bool redo_manifold = false) = 0;  // POST /calib {channel, waterMl, redoManifold}
    virtual void calib_continue(double water_ml = 0) = 0;         // POST /calib/continue（操作员加完水）
    virtual void calib_abort() = 0;                               // DELETE /calib/current
    virtual void wifi_enable(bool on) = 0;                        // PUT /network/wifi {enabled}
    virtual void wifi_scan() = 0;                                 // POST /network/wifi/scan
    virtual void wifi_connect(const std::string& ssid, const std::string& pw) = 0;  // POST /network/wifi/connect
    virtual void wifi_forget(const std::string& ssid) = 0;        // DELETE /network/wifi/saved/{ssid}
    virtual void wifi_disconnect() = 0;                           // POST /network/wifi/disconnect
    virtual void set_ip_mode(bool dhcp) = 0;                      // PUT /network/ip {mode: dhcp|manual}
    virtual void comm_test(const std::string& module) = 0;        // POST /device/comm/{module}/test
    virtual void comm_reconnect(const std::string& module) = 0;   // POST /device/comm/{module}/reconnect
    virtual void quit_app() = 0;                                  // 退出触屏程序（只退界面，reactord 照常跑）
    // —— 控制权（本地专用）——
    virtual void control_grant(const std::string& request_id) = 0;  // POST /control/requests/{id}/grant
    virtual void control_deny(const std::string& request_id) = 0;   // POST /control/requests/{id}/deny
    virtual void control_take_back() = 0;                           // POST /control/take-back（PC 控制中 → 收回到触屏）

    // 命令结果回调（ok, 文案）。实现方在 LVGL 线程里调用。
    std::function<void(bool, const std::string&)> on_result;
};

// 打印到 stdout；forward 不为空时把同一调用转过去（模拟数据源用它改状态）。
class LoggingActions : public Actions {
public:
    explicit LoggingActions(Actions* forward = nullptr) : fwd_(forward) {}
    void stop_all(bool s) override;
    void stir_set_rpm(int rpm) override;
    void stir_run(bool on) override;
    void manual_valve(bool on) override;
    void set_temp_sp(int ch, double c) override;
    void heat(int ch, bool on) override;
    void set_heat_mode(int ch, const std::string& mode) override;
    void set_band(int ch, double c) override;
    void set_anti_overshoot(int ch, bool on) override;
    void set_profile(int ch, const std::vector<CurveSeg>& segs) override;
    void start_profile(int ch) override;
    void set_valve(int ch, bool open) override;
    void set_gas_source(int k, bool open) override;
    void gas_zero(int ch) override;
    void set_solution_volume(int ch, double ml) override;
    void channel_enable(int ch, bool on) override;
    void copy_to_all(int ch) override;
    void abort_program(int ch) override;
    void ack_alarm(const std::string& id) override;
    void ack_all() override;
    void set_language(const std::string& code) override;
    void put_program(int ch, const ProgramDoc& p) override;
    void start_program(int ch) override;
    void lib_save(bool tpl, const ProgramDoc& p) override;
    void lib_delete(bool tpl, const std::string& id, int64_t revision) override;
    void lib_import_usb() override;
    void leak_start(const std::vector<int>& chs) override;
    void leak_abort() override;
    void run_note(const std::string& run_id, const std::string& note) override;
    void export_usb(const ExportReq& r) override;
    void patch_setting(const std::string& key, double v) override;
    void patch_setting_str(const std::string& key, const std::string& v) override;
    void set_pid(int ch, int band, double p, double i, double d) override;
    void set_pid_bands(double lo, double hi) override;
    void autotune(int ch, int band, double sp_c) override;
    void autotune_cancel(int ch) override;
    void calib_start(int ch, double water_ml, bool redo_manifold = false) override;
    void calib_continue(double water_ml = 0) override;
    void calib_abort() override;
    void wifi_enable(bool on) override;
    void wifi_scan() override;
    void wifi_connect(const std::string& ssid, const std::string& pw) override;
    void wifi_forget(const std::string& ssid) override;
    void wifi_disconnect() override;
    void set_ip_mode(bool dhcp) override;
    void comm_test(const std::string& module) override;
    void comm_reconnect(const std::string& module) override;
    void quit_app() override;
    void control_grant(const std::string& id) override;
    void control_deny(const std::string& id) override;
    void control_take_back() override;

private:
    Actions* fwd_;
};

}  // namespace reactor::hmi::model
