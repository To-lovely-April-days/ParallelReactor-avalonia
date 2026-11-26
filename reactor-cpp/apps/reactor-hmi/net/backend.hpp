// 触屏连 reactord：把 WS 推送和 REST 结果拼成一份 Model（经 ModelMailbox 交给 LVGL 线程），
// 并用 NetActions 把页面的每个动作翻译成对应的端点（doc/接口契约-新增.md）。
//
// 线程：
//   - Model 的工作副本 m_ 由一把锁保护；ws 线程（遥测 / 报警 / 事件 / progress）、read 线程（REST 结果）、
//     cmd 线程（写命令结果）都可能改它，改完 publish() 拷一份 shared_ptr<const Model> 投进信箱。
//   - NetActions 的方法只在 LVGL 线程调（Actions 的约定），只是把请求排进 cmd 队列，从不阻塞。
//     命令结果经 UiQueue 回到 LVGL 线程，调 Actions::on_result(ok, 中文)（main.cpp 接成 toast）。
//
// 什么时候取哪份数据（接口契约 §5）：
//   - 连上（收到 hello）：订阅 telemetry / events / alarms / progress，带 resumeAfterEventSeq；全部 REST 数据标脏重取。
//   - 快照里的 revs.* 变了 → 重取对应的那几份；后端没给 revs 时退回定时刷新。
//   - gap → 按 seq 用 GET /events 补齐，再 GET /snapshot、/alarms，然后重新订阅。
//   - 断线 → Model.connected = false（界面盖上「正在连接后台服务…」），ws 线程按退避自动重连。
#pragma once
#include "../model/actions.hpp"
#include "../model/model.hpp"
#include "binding.hpp"
#include "net_client.hpp"

#include <atomic>
#include <map>
#include <memory>
#include <mutex>
#include <set>
#include <string>
#include <cstdint>
#include <functional>
#include <vector>

namespace reactor::hmi::net {

class NetActions;

class NetBackend : public NetClient::Listener {
public:
    NetBackend(Endpoint ep, std::string token, model::ModelMailbox& mailbox, UiQueue& ui);
    ~NetBackend() override;
    void start();
    void stop();
    model::Actions& actions();
    bool connected() const { return connected_; }
    bool quit_requested() const { return quit_; }
    std::shared_ptr<const model::Model> model_copy() const;
    NetClient& client() { return *client_; }

    // NetClient::Listener（网络线程）
    void on_ws_open() override;
    void on_ws_text(const std::string& text) override;
    void on_ws_closed(const std::string& why) override;
    void on_tick(int64_t mono) override;

    // 数据分组：每组对应一个（或几个）GET
    enum G {
        Settings, Schema, Device, Alarms, Programs, Library, Templates, Pid, Comm, Network, Usb, Storage, Users,
        Runs, Files, Leak, LeakSamples, Calib, Trend, kGroups
    };
    void invalidate(G g);   // 任意线程：标脏，下一次 tick 重取
    void refetch_now(G g);  // 任意线程：马上重取（正在取就标脏，取完再取一次）

private:
    friend class NetActions;
    struct Grp { bool dirty = false, inflight = false; int64_t last = 0; };
    void publish_locked();
    void fetch(G g);
    void done(G g);
    void get(const std::string& path, std::function<void(const Reply&)> fn, int timeout_ms = 10000);
    void handle_hello(const json& j);
    void handle_control(const json& j);           // WS control / GET /control → 待处理的控制申请
    void clear_pending(const std::string& id);    // 已允许 / 拒绝：本地先清掉，免得弹窗再弹
    void handle_telemetry(const json& j);
    void handle_event(const model::EventRecord& e);
    void add_events_locked(std::vector<model::EventRecord> ev);
    void fetch_events(int64_t after, int pages_left, bool resubscribe);
    void resync_after_gap();
    void send_subscribe();
    void apply_trend(const std::shared_ptr<model::Trend>& t, bool replace);
    void update_plan_actuals_locked();
    void refresh_derived_locked();
    void fetch_run_details(const std::vector<std::string>& ids);
    int64_t interval_of(G g) const;   // 后端没给 revs 时的定时刷新间隔（0 = 只按需）

    Endpoint ep_;
    model::ModelMailbox& mailbox_;
    UiQueue& ui_;
    std::unique_ptr<NetClient> client_;
    std::unique_ptr<NetActions> actions_;
    std::atomic<bool> connected_{false}, quit_{false};
    // 最近一次改设置成功后后台给的 revision（受 mu_ 保护）。只给下一次 PATCH 用，
    // 不写进 m_.settings：否则界面会拿「新 revision + 旧数值」整页重画，刚点的选项被打回去
    int64_t settings_rev_hint_ = 0;

    mutable std::mutex mu_;
    model::Model m_;
    Grp grp_[kGroups];
    bool have_revs_ = false;
    std::map<std::string, int64_t> revs_;
    int seq_epoch_ = -1;
    int64_t last_seq_ = 0, head_seq_ = 0;
    std::string boot_id_;
    bool events_loaded_ = false;
    std::shared_ptr<const model::Trend> trend_;
    std::vector<model::HistoryFileInfo> files_tel_, files_ev_;
    struct RunDetail { std::shared_ptr<const model::Trend> trend; model::ProgramDoc program; std::vector<int> stepMinutes; int64_t at = 0; bool have = false; };
    std::map<std::string, RunDetail> run_detail_;
    std::set<std::string> run_inflight_;
    int64_t schema_rev_ = -1;
    std::string link_err_;
    int link_tries_ = -1;
    int64_t clock_pub_ = 0;
    bool resyncing_ = false;
};

// 页面动作 → 端点。只在 LVGL 线程调；不阻塞。
class NetActions : public model::Actions {
public:
    explicit NetActions(NetBackend& b) : b_(b) {}
    void stop_all(bool include_stir) override;
    void stir_set_rpm(int rpm) override;
    void stir_run(bool on) override;
    void manual_valve(bool on) override;
    void set_temp_sp(int ch, double c) override;
    void heat(int ch, bool on) override;
    void set_heat_mode(int ch, const std::string& mode) override;
    void set_band(int ch, double c) override;
    void set_anti_overshoot(int ch, bool on) override;
    void set_profile(int ch, const std::vector<model::CurveSeg>& segs) override;
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
    void put_program(int ch, const model::ProgramDoc& p) override;
    void start_program(int ch) override;
    void lib_save(bool tpl, const model::ProgramDoc& p) override;
    void lib_delete(bool tpl, const std::string& id, int64_t revision) override;
    void lib_import_usb() override;
    void leak_start(const std::vector<int>& chs) override;
    void leak_abort() override;
    void run_note(const std::string& run_id, const std::string& note) override;
    void export_usb(const model::ExportReq& r) override;
    void patch_setting(const std::string& key, double v) override;
    void patch_setting_str(const std::string& key, const std::string& v) override;
    void set_pid(int ch, int band, double p, double i, double d) override;
    void set_pid_bands(double lo, double hi) override;
    void autotune(int ch, int band, double sp_c) override;
    void autotune_cancel(int ch) override;
    void calib_start(int ch, double water_ml) override;
    void calib_continue() override;
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
    // 发一条写命令（cmd 队列）。成功时 ok_msg 非空就 toast；服务端 warnings 也 toast。
    // refresh：成功后要重取的数据组。
    void cmd(const std::string& method, const std::string& path, json body, std::vector<NetBackend::G> refresh = {},
             const std::string& ok_msg = "", bool urgent = false, std::function<void(const Reply&)> after = nullptr);
    void patch(json value_wire, const std::string& wire_key, bool retried);
    void result(bool ok, const std::string& msg);
    NetBackend& b_;
};

}  // namespace reactor::hmi::net
