// 模拟数据源：没有后台服务时让页面有真实感的数据（照画布的场景：1、2 号跑「加氢筛选-A」，
// 3、4 号跑「加氢筛选-B」，5 号手动，6 号排队等气路，7、8 号停用），每秒推进一次。
// 同时实现 Actions，按用户操作改自己的状态（不做任何安全判断，那是 reactord 的事）。
// 只在 LVGL 线程用（由 LVGL 定时器驱动）；以后换成 net/ 客户端时，页面不用改。
#pragma once
#include "actions.hpp"
#include "model.hpp"

#include <memory>
#include <array>
#include <cstdint>
#include <string>
#include <vector>

namespace reactor::hmi::model {

class SimSource : public Actions {
public:
    // base_ms：模拟时钟的起点（UTC 毫秒）；0 = 今天本地 14:05:12（和画布上的时间对得上）
    explicit SimSource(int64_t base_ms = 0);

    // 推进到「真实流逝 elapsed_ms」后的状态
    void step(int64_t elapsed_ms);
    std::shared_ptr<const Model> snapshot() const { return std::make_shared<const Model>(m_); }
    const Model& model() const { return m_; }

    // 预览用：造出三种级别的报警
    void inject_l1();   // 7 号超压 · 联锁动作
    void inject_l2();   // 压力采集模块离线
    void inject_l3();   // 3 号吸气量未计量（铃铛气泡）
    // 预览第二批的几种状态
    void inject_prog_done(int ch);     // 这一路程序跑完
    void inject_prog_aborted(int ch);  // 这一路程序被操作员中止
    void inject_leak(const char* phase);  // "run" 测量中 / "fail" 7 号不通过 / "pass" 4 路通过
    void inject_heat_curve(int ch, int segs);
    void inject_stir_offline();        // 搅拌驱动离线（设置 · 通讯与模块）  // 这一路草稿的升温步改成 segs 段曲线

    // Actions
    void stop_all(bool include_stir) override;
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
    void inject_control_request();   // 预览：PC「实验室-PC2」申请远程控制

private:
    void build_scenario();
    void rebuild_plans();
    ChannelSnap& c(int id) { return m_.snap.channels[static_cast<size_t>(id - 1)]; }
    void make_programs();    // progs_ 和每一路的程序（sim_pages.cpp）
    void build_pages();      // 第二批页面的模拟数据（sim_pages.cpp）
    void step_pages(double dt);
    void make_trend();
    Model m_;
    std::vector<ProgramDoc> progs_;  // 0 加氢筛选-A / 1 加氢筛选-B / 2 加氢筛选-C
    struct PlanRow { int prog = -1, step = 0; int64_t progElapsed = 0, stepElapsed = 0; };
    std::array<PlanRow, 8> plan_{};
    int calibTick_ = 0;
    double leakClock_ = 0;
    int64_t base_ = 0, now_ = 0, last_ = 0;
    std::array<int64_t, 8> progStart_{};  // 程序开始时刻
    int alarmSeq_ = 100;
};

}  // namespace reactor::hmi::model
