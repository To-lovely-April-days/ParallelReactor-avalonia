// 接口层：/api/v1 下全部 HTTP 端点 + WebSocket 中枢。设计文档 §5；C#: Api/Endpoints*.cs、Api/WsHub.cs、Api/ControlService.cs、
// Api/CommandResults.cs、Api/Hmi/ControlBridge.cs。
//
// 两个入口共用一套路由（net::Server）：TCP（PC：令牌 + 白名单）和 Unix socket（本机触屏：身份 = 本地，admin）。
// local_only 的路由只在 Unix 上存在，TCP 上是统一 404「没有这个端点」（方法不对也是 404，同 C# MapFallback）。
//
// 写命令公共管线（顺序固定，C# Endpoints.Write.cs CommandAsync）：
//   角色 → 正文 JSON → 幂等 → 动作类别（Control 须租约）→ 参数 → 执行（投递控制线程）→ 结果映射 → 审计 op.<action> → WS opEvent → 幂等完成。
//   2 s 内硬件尾部没回来 → 202 + HW_UNCONFIRMED，最终结果再记 op.<action>.result 并推第二条 opEvent（同 opId）。
//
// 组装顺序（apps/reactord）：ControlCore → Automation → ApiService(构造：注册 ModeHooks / 程序进度回调) → core.start()
//   → net::Server → register_routes(srv.router()) → attach(&srv) → start() → srv.start() … stop_ticker() → srv.stop() → stop() → core.shutdown()。
//
// 线程：register_routes / attach / start / stop 在启动线程；处理器在 net 的工作线程上跑（可阻塞等控制线程结果，最多约 12 s）；
// 命令本身在控制线程上执行；WS 推送由一个中枢线程扇出；租约计时一个 1 s 线程。
#pragma once
#include <atomic>
#include <condition_variable>
#include <array>
#include <deque>
#include <functional>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <thread>
#include <vector>

#include "cmd/cmd_arbiter.hpp"
#include "cmd/cmd_commands.hpp"
#include "cmd/cmd_lease.hpp"
#include "ctl/ctl_automation.hpp"
#include "ctl/ctl_core.hpp"
#include "ctl/ctl_hooks.hpp"
#include "hw/hardware.hpp"
#include "net/api_config.hpp"
#include "net/idempotency.hpp"
#include "net/router.hpp"
#include "net/server.hpp"
#include "store/data_logger.hpp"
#include "store/event_store.hpp"
#include "store/program_library.hpp"
#include "store/session.hpp"

namespace reactor::api {

using ctl::ojson;

constexpr const char* kApiVersion = "1.0";
constexpr const char* kModel = "ParallelReactor-8";
constexpr const char* kDaemonVersion = "0.3.0";

struct ApiDeps {
    net::ApiConfig cfg;                          // api.json（onRemoteLost / 超时 / autoGrant / maxClients / serial）
    ctl::ControlCore* core = nullptr;            // 必填
    ctl::Automation* automation = nullptr;       // 必填
    store::DataLogger* logger = nullptr;         // 审计事件（可空：审计 seq 为 0）
    store::EventStore* events = nullptr;         // 必填（/events、WS 事件）
    store::SessionBook* sessions = nullptr;      // 可空
    store::ProgramLibrary* library = nullptr;    // 可空（/library/* 回 503）
    hw::Hardware* hw = nullptr;                  // /device、搅拌驱动器参数（可空）
    std::string data_dir;                        // /files、/leak-tests、/sessions
    std::function<int64_t()> mono_ms;            // 缺省 reactor::mono_ms（租约计时）
    std::function<int64_t()> wall_ms;            // 缺省 reactor::wall_ms
    std::string usb_root = "/media";             // GET /usb 扫描的挂载点根目录
    std::string config_dir;                      // GET /storage 显示 settings.json 路径
    std::vector<std::string> nmcli = {"nmcli"};  // NetworkManager 命令（测试可换成假脚本）
    std::function<void()> request_exit;          // POST /system/restart：让主循环退出（systemd Restart=always 再拉起）
    bool lease_ticker = true;                    // 起 1 s 租约计时线程（测试可关掉、手动 tick）
    core::PressureUnit unit = core::PressureUnit::MPa;
};

// 一个写端点的静态属性（C# WriteSpec）
struct WriteSpec {
    std::string action;                    // 事件 kind = op.<action>
    cmd::ActionClass cls = cmd::ActionClass::Control;
    bool idem_required = false;
    net::Role role = net::Role::Operator;
    bool estop = false;                    // /estop：只看 estop 位
    bool inline_exec = false;              // 不进控制线程（租约状态机是线程安全的纯逻辑）
    bool redact_body = false;              // 正文不进审计（WiFi 密码）
};

// 参数绑定的结果：code 非空 = 拒绝（400 / 404 …）；否则 exec 在控制线程上执行命令，done 恰好调用一次
using Exec = std::function<void(cmd::Commands&, const cmd::Source&, cmd::Done)>;
struct Bound {
    std::string code, message;
    Exec exec;
    ojson detail = nullptr;   // 拒绝时的 error.detail
    static Bound reject(std::string c, std::string m) { return Bound{std::move(c), std::move(m), {}}; }
    static Bound bad(std::string m) { return Bound{"BAD_REQUEST", std::move(m), {}}; }
};
using Binder = std::function<Bound(const nlohmann::json* body, const net::Request& q, const cmd::Source& src)>;

class ApiService : public ctl::ModeHooks {
public:
    explicit ApiService(ApiDeps d);
    ~ApiService() override;
    ApiService(const ApiService&) = delete;
    ApiService& operator=(const ApiService&) = delete;

    void register_routes(net::Router& r);
    void attach(net::Server* srv);
    void start();          // 订阅快照 / 事件 / 报警，起中枢线程与租约计时
    void stop_ticker();    // 先停租约计时（退出前不要再判宽限期满）
    void stop();           // 退订、停中枢线程（服务器 stop 之后调）

    // ModeHooks（快照 mode）：控制线程调用
    ojson mode() const override;

    cmd::ControlLease& lease() { return lease_; }
    cmd::ControlArbiter& arbiter() { return arbiter_; }
    cmd::Commands& commands() { return commands_; }
    net::IdempotencyStore& idempotency() { return idem_; }
    const std::string& boot_id() const { return boot_id_; }
    void tick_lease() { lease_.tick(now_mono()); }

    // ---- 接口契约新增：快照 / 报警扩展、修订号（任意线程） ----
    ojson augment_frame(const ctl::Snapshot& s);
    ojson alarm_json(const ctl::AlarmItem& a, const ctl::Snapshot* s) const;
    ojson revs_json(const ctl::Snapshot* s);
    void bump_programs() { revs_programs_.fetch_add(1); }
    void bump_library() { revs_library_.fetch_add(1); }
    void bump_runs() { revs_runs_.fetch_add(1); }
    void bump_network() { revs_network_.fetch_add(1); }
    void bump_usb() { revs_usb_.fetch_add(1); }
    // 趋势（GET /trend）：[from, to) 每 step_ms 一桶，取桶内最后一个有效读数；fields 为 tempC / pressurePsi / gasMmol 的子集
    ojson trend(int64_t from, int64_t to, int64_t step_ms, const std::vector<std::string>& fields, const std::vector<int>& channels);

    // ---- 管线（给路由用；也给测试） ----
    net::Response command(const net::Request& q, const WriteSpec& spec, const Binder& bind);

private:
    friend struct Routes;
    int64_t now_mono() const { return d_.mono_ms(); }
    int64_t now_wall() const { return d_.wall_ms(); }
    std::optional<std::string> utc_of_mono(std::optional<int64_t> mono) const;
    ctl::SnapshotStore::Ptr snap() const { return d_.core->snapshot(); }
    int64_t audit(const std::string& level, const std::string& title, const std::string& detail, const std::string& source,
                  const std::optional<std::string>& client, const std::optional<std::string>& op_id, const std::string& kind);
    void audit_plain(const std::string& level, const std::string& title, const std::string& detail);
    void on_lease_event(const cmd::LeaseEvent& e);

    // 在控制线程上跑一个只读 / 计算任务并等结果（最多 timeout_ms；控制线程没在跑返回 nullopt）
    template <class T>
    std::optional<T> on_control(std::function<T()> fn, int64_t timeout_ms = 5000);

    // ---- JSON 构造 ----
    ojson control_state_json();
    ojson request_result_json(const cmd::RequestOutcome& r);
    ojson request_status_json(const cmd::RequestStatus& s);
    ojson hello_json();
    ojson device_json();
    ojson settings_json(const store::SettingsState& st) const;
    ojson pid_json(const ctl::Snapshot& s) const;
    std::string schema_json();

    // ---- WS 中枢 ----
    enum class HubKind { Event, Alarm, Control, Local };
    void hub_push(HubKind k, std::string json);
    void hub_loop();
    void ws_open(const net::WsSessionPtr& s, const net::Request& q);
    void ws_message(const net::WsSessionPtr& s, const std::string& text);
    int ws_replay(const net::WsSessionPtr& s, int64_t after);

    ApiDeps d_;
    cmd::ControlArbiter arbiter_;
    cmd::ControlLease lease_;
    cmd::Commands commands_;
    net::IdempotencyStore idem_;
    net::Server* srv_ = nullptr;
    std::string boot_id_;
    int64_t boot_wall_ms_ = 0;
    int64_t started_at_ms_ = 0;
    std::optional<bool> clock_synced_;
    bool clock_probed_ = false;
    std::mutex probe_mu_;

    std::mutex schema_mu_;
    int64_t schema_rev_ = -1;
    std::string schema_cache_;

    struct HubItem {
        HubKind kind;
        std::shared_ptr<const std::string> json;
    };
    std::mutex hub_mu_;
    std::condition_variable hub_cv_;
    std::deque<HubItem> hub_q_;
    ctl::SnapshotStore::Ptr hub_frame_;
    bool hub_stop_ = false;
    std::thread hub_thread_;
    std::atomic<int> ws_count_{0};

    std::thread ticker_;
    std::atomic<bool> ticker_stop_{false};
    std::mutex ticker_mu_;
    std::condition_variable ticker_cv_;

    // —— 接口契约新增的状态 ——
    void on_frame(const ctl::Snapshot& s);       // 中枢线程：修订号、趋势环、泄漏采样、运行记录、progress
    ojson progress_json(const ctl::Snapshot& s);
    void record_trend(const ctl::Snapshot& s);
    void record_leak(const ctl::Snapshot& s);
    void poll_slow(int64_t now);                 // 文件 / U 盘变化、数据保留清理（中枢线程，每几秒）
    std::atomic<int64_t> revs_programs_{0}, revs_library_{0}, revs_runs_{0}, revs_files_{0}, revs_leak_{0}, revs_calib_{0},
        revs_network_{0}, revs_usb_{0}, revs_users_{0}, revs_device_{0}, revs_pid_{0};
    std::mutex sig_mu_;
    std::string leak_sig_, calib_sig_, pid_sig_, files_sig_, usb_sig_;
    int64_t last_slow_ms_ = 0, last_clean_ms_ = 0;

    // 每路程序的附加信息（PUT 时客户端给的 name / sourceId，草稿是否还没下发）
    struct ProgInfo {
        std::string name;
        std::optional<std::string> source_id;
        int64_t revision = 0;
        std::optional<int64_t> modified_ms;
        bool edited_not_sent = false;
        ojson steps = ojson::array();   // 最近一次 PUT 成功后的草稿（§6.9，psi）
    };
    mutable std::mutex prog_mu_;
    std::array<ProgInfo, 8> prog_{};

    struct TrendPt {
        int64_t ts = 0;
        std::array<float, 8> t{}, p{}, g{};
        uint32_t valid = 0;   // 位 0-7 温度、8-15 压力、16-23 吸气量
    };
    mutable std::mutex trend_mu_;
    std::deque<TrendPt> trend_;

    struct LeakSamples {
        std::string run_id;
        double sample_sec = 10;
        int64_t measure_start_ms = 0;
        int64_t last_ms = 0;
        std::array<std::optional<double>, 8> p0{};
        ojson points = ojson::array();
        bool measuring = false;
    };
    mutable std::mutex leak_mu_;
    std::optional<LeakSamples> leak_samples_;

    std::shared_ptr<class RunsBook> runs_;
    std::atomic<bool> net_busy_{false}, export_busy_{false};
    std::mutex misc_mu_;
    std::string last_export_;
    int export_seq_ = 0;

    int snap_sub_ = 0, event_sub_ = 0, alarm_sub_ = 0, notice_sub_ = 0;
    std::shared_ptr<std::atomic<bool>> alive_;   // 控制核心的报警 / 提示 / 程序回调没有退订：用它让回调在 stop 之后变成空操作
    bool started_ = false;
};

}  // namespace reactor::api
