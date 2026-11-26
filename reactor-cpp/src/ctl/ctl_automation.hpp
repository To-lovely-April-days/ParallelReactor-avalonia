// 控制核心第二阶段的总入口：程序引擎 + 泄漏测试 + 容积标定 + 自整定 + 内置模板。
//
// 组装（启动线程，ControlCore::start() 之前）：
//     ctl::ControlCore core(deps);
//     ctl::Automation automation({&core, &logger, &sessions, &library});   // 注册 ProgramHooks / ActivityHooks / 气路中止监听 / 整定回调，种内置模板
//     core.start();
//     …
//     core.shutdown(true);   // 退出时核心经 ProgramHooks::abort_all(Shutdown) 中止程序
//     // automation 在 core 之后析构（或至少在 shutdown 之后）
//
// 两组接口：
//   1) 控制线程上直接调（命令层把「仲裁 + 守卫 + 执行」整个放进一个 core.call 任务时用）：programs() / leak() / volume_calib() / tuner() 的方法；
//   2) 任何线程调（下面的 *_async）：投递到控制线程执行，返回 std::future<OpResult>。要等硬件的（自整定）在结果回来后才兑现。
//      控制线程上不要 .get() 这些 future（会死锁）。
//
// 线程：构造 / 析构在启动线程（核心未运行或已停止时）；其余见上。
#pragma once
#include <future>
#include <memory>
#include <optional>
#include <string>
#include <vector>

#include "ctl/ctl_autotune.hpp"
#include "ctl/ctl_builtin_templates.hpp"
#include "ctl/ctl_core.hpp"
#include "ctl/ctl_leak_test.hpp"
#include "ctl/ctl_op_result.hpp"
#include "ctl/ctl_program_engine.hpp"
#include "ctl/ctl_volume_calib.hpp"
#include "store/data_logger.hpp"
#include "store/program_library.hpp"
#include "store/session.hpp"

namespace reactor::ctl {

struct AutomationDeps {
    ControlCore* core = nullptr;                  // 必填
    store::DataLogger* logger = nullptr;          // 泄漏结果文件 / 会话标记（可空）
    store::SessionBook* sessions = nullptr;       // 会话 manifest 的 programRuns / leakTests（可空）
    store::ProgramLibrary* library = nullptr;     // 种内置模板（可空）
    core::PressureUnit unit = core::PressureUnit::MPa;  // 文案里的压力单位
    std::string model = "PR8";                    // 内置模板的适用型号
};

class Automation : public ActivityHooks {
public:
    explicit Automation(AutomationDeps d);
    ~Automation() override;
    Automation(const Automation&) = delete;
    Automation& operator=(const Automation&) = delete;

    // ---- 控制线程 ----
    ProgramEngine& programs() { return *programs_; }
    LeakTest& leak() { return *leak_; }
    VolumeCalib& volume_calib() { return *calib_; }
    Autotune& tuner() { return *autotune_; }
    const builtin_templates::SeedResult& seed_result() const { return seed_; }
    // 手动阀控 / 手动开关阀前的气路检查（C# MainViewModel.GasLockedForManual）：通过 = silent
    OpResult gas_locked_for_manual() const;
    // 自动气路序列的公共检查（C# GasBlock(needPressure)，不带程序排队豁免）
    std::optional<core::GuardReject> gas_block(bool need_pressure) const;

    // ActivityHooks（快照 leakTest / calib / autotune）
    ojson leak_test() const override;
    ojson calib() const override;
    ojson autotune() const override;

    // ---- 任何线程（投递到控制线程） ----
    std::future<OpResult> replace_program_async(int ch, nlohmann::json steps, std::optional<std::string> name = std::nullopt);
    std::future<OpResult> copy_program_async(int from, std::vector<int> to, bool strict);
    std::future<OpResult> check_run_async(int ch);
    std::future<OpResult> run_program_async(int ch, std::string started_by);
    std::future<OpResult> abort_program_async(int ch);
    std::future<OpResult> start_leak_test_async(std::vector<int> channels, std::optional<double> test_psi, std::string started_by);
    std::future<OpResult> abort_leak_test_async(bool strict);
    std::future<OpResult> start_calib_async(int ch, bool with_water, double water_ml, bool redo_manifold, std::string started_by);
    std::future<OpResult> abort_calib_async();
    std::future<OpResult> discard_calib_async();
    std::future<OpResult> start_autotune_async(int ch, double temp_c, std::optional<int> band, bool strict);
    std::future<OpResult> cancel_autotune_async(int ch);
    std::future<ojson> draft_json_async(int ch);

private:
    template <class F>
    std::future<OpResult> post(F f);
    std::future<OpResult> post_async(std::function<void(std::function<void(const OpResult&)>)> f);

    AutomationDeps d_;
    std::unique_ptr<ProgramEngine> programs_;
    std::unique_ptr<LeakTest> leak_;
    std::unique_ptr<VolumeCalib> calib_;
    std::unique_ptr<Autotune> autotune_;
    builtin_templates::SeedResult seed_;
};

}  // namespace reactor::ctl
