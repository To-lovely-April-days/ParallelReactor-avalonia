// 自动泄漏测试（独立于程序，泄漏测试页发起）。C#: ViewModels/LeakViewModel.cs（RunAsync / FillAsync / SettleAsync / MeasureAsync /
// Judge / TryVentAsync / Finish / SaveRecord）+ Models/DeviceActivity.cs 的 LeakActivity（快照 leakTest）+ Commands/OperatorCommands.Leak.cs。
//
// 流程：关阀读初始压力 → 脉冲充压到测试压力（惰性气体，每路按增益定开阀时长，到压即关该路 SV）→ 总管泄压 → 平衡 → 测量
// （每 10 s 读压，温度补偿后最小二乘拟合压降速率）→ 判读（< LeakRate psi/h 通过）→ 尝试经排空阀泄压并核实 → 全关阀、释放气路 →
// 结果文件（leak/<runId>.csv、-summary.csv、-summary.json，经 DataLogger::write_file）+ 会话 manifest 的 leakTests + 设置 LastLeakTestUtc。
// 中止：abort()（只中止泄漏测试自己）、全部停止 / 超压联锁 / 退出（气路中止监听）、参与通道被停止 / 停用。
//
// 线程：只在控制线程用。
#pragma once
#include <array>
#include <map>
#include <memory>
#include <optional>
#include <set>
#include <string>
#include <vector>

#include "ctl/ctl_core.hpp"
#include "ctl/ctl_gas_ops.hpp"
#include "ctl/ctl_op_result.hpp"
#include "store/data_logger.hpp"
#include "store/leak_record.hpp"
#include "store/session.hpp"
#include <cstdint>
#include <functional>
#include <utility>

namespace reactor::ctl {

class ProgramEngine;

namespace leak_stage {  // C# LeakStageCodes
inline constexpr const char* kPrep = "prep";
inline constexpr const char* kFill = "fill";
inline constexpr const char* kLineVent = "lineVent";
inline constexpr const char* kSettle = "settle";
inline constexpr const char* kMeasure = "measure";
inline constexpr const char* kVent = "vent";
inline constexpr const char* kDone = "done";
}  // namespace leak_stage

class LeakTest {
public:
    static constexpr double kFillTimeoutSec = 180;  // C# LeakViewModel.FillTimeoutSec
    static constexpr double kSampleSec = 10;        // C# LeakViewModel.SampleSec
    // C# LeakActivity 的经验时长（估算剩余时间用）
    static constexpr double kPrepEstSec = 10, kFillEstSec = 120, kFillMinLeftSec = 10, kLineVentEstSec = 10, kVentEstSec = 15;

    LeakTest(ControlCore& core, const ProgramEngine* programs, store::DataLogger* logger, store::SessionBook* sessions,
             core::PressureUnit unit = core::PressureUnit::MPa);
    ~LeakTest();

    // POST /leak-tests（C# TryStartLeakTest）。test_psi 覆盖本次测试压力（远程用）
    OpResult start(const std::vector<int>& channels, std::optional<double> test_psi, const std::string& started_by = "触屏");
    // DELETE /leak-tests/current（C# TryAbortLeakTest）。strict = 远程严格守卫（气路由别人占用时 GAS_OWNED_BY_OTHER）
    OpResult abort(bool strict = false);

    bool running() const { return running_; }
    std::optional<std::string> run_id() const { return rec_ ? std::optional<std::string>(rec_->run_id()) : std::nullopt; }
    double progress() const { return progress_; }
    const std::string& phase_text() const { return phase_; }
    // 快照 leakTest（C# LeakSnap，字段与顺序不改）
    ojson snapshot() const;

private:
    struct Cell {
        int id = 0;
        bool picked = false;
        std::string status;  // "" | ok | bad | skip
        std::string detail;
        double pressure = NAN;
    };
    void run(const std::vector<int>& ids, double target, const std::string& by);
    void enter(const char* stage, std::optional<double> duration_sec = std::nullopt);
    void sample(const char* phase, const std::vector<int>& ids, std::function<void(const PressureSample&)> then);
    void fill_round();
    void fill_close(size_t k);
    void fill_after();
    void line_vent();
    void settle_loop();
    void measure_begin();
    void measure_loop();
    void judge_and_vent();
    void fault(const std::string& why);
    void cancelled();
    void cleanup();
    void finalize();
    void save_record();
    void on_gas_abort(core::GasAbortCause cause, std::optional<int> channel);
    Cell& cell(int id) { return cells_[static_cast<size_t>(id - 1)]; }
    std::string rvs(const std::vector<int>& ids) const;
    std::optional<double> stage_left_sec() const;
    std::optional<double> total_left_sec() const;
    double est_sec(const std::string& stage) const;
    static std::string status_code(bool picked, bool finished, const std::string& status, const std::string& detail);

    ControlCore& core_;
    const ProgramEngine* programs_;
    store::DataLogger* logger_;
    store::SessionBook* sessions_;
    core::PressureUnit unit_;
    std::unique_ptr<GasOps> ops_;
    int listener_ = 0;

    // 面板状态
    bool running_ = false, done_ = false, failed_ = false, leak_failed_ = false;
    double progress_ = 0;
    std::string phase_;
    std::array<Cell, 8> cells_{};
    std::vector<int> picked_, tested_, over_fill_;
    double target_ = 0, thr_ = 0, settle_min_ = 0, measure_min_ = 0;
    std::optional<std::string> fail_;
    bool aborted_ = false, ending_ = false;
    std::optional<bool> vented_;
    std::optional<std::map<int, double>> rates_;
    std::unique_ptr<store::LeakRecord> rec_;
    std::string started_by_;
    std::string last_run_id_;
    std::string result_title_, result_text_;  // 面板结果（快照新字段）  // 最近一次结束的 runId（快照新字段 lastRunId）

    // 充压
    std::vector<int> fill_ok_, fill_pend_;
    std::map<int, double> gain_;
    std::map<int, int> stall_;
    std::vector<std::pair<int, double>> plan_;
    std::map<int, double> before_;
    std::vector<int> open_;
    int64_t fill_t0_ = 0, round_t0_ = 0;
    // 平衡 / 测量
    int64_t phase_t0_ = 0;
    std::map<int, std::vector<std::pair<double, double>>> series_;
    std::map<int, double> t_ref_;
    std::map<int, double> vent_before_;

    // LeakActivity（快照）
    std::optional<std::string> stage_;
    int64_t stage_started_mono_ = 0;
    std::optional<int64_t> stage_ends_mono_;
    std::optional<int64_t> started_at_, finished_at_;
    double settle_sec_ = 0, measure_sec_ = 0;
    std::array<std::optional<double>, 9> rate_{};
    bool act_failed_ = false, act_aborted_ = false;
    std::optional<std::string> act_fail_;
    std::optional<bool> act_vented_;
    double final_progress_ = 0;
    std::set<int> act_picked_;
};

}  // namespace reactor::ctl
