// AI-8 自整定（每通道、每档位）。C#: Service/AutotuneBook.cs（进行状态 + 来历）、Service/TempService.cs 的 AutotuneAsync /
// CancelAutotuneAsync / ReloadPidAsync / OverTempStopAsync 的整定打断、Commands/OperatorCommands.Temp.cs 的 TryStartAutotune / TryCancelAutotune。
//
//   start：守卫（启动加热公共检查、目标温度 40..TMaxSp、显式档位须包含目标温度；严格守卫：整定中 / 通道正在运行）→ 写 SP →
//          回读 SP（< 35 ℃ 不整定）→ Srun=0 → At=1 → 记下「发起时的档位 + 目标温度」（整定期间切档 / 改分界也存回这一档）。
//   完成：温控轮询看到 At 从 1 变成别的（TempService::on_autotune_finished）→ 没被打断就回读仪表 P/I/D，存入发起时的档并记来历
//          （store::tune_complete：目标温度 + 完成时刻），设置 revision +1、防抖写盘；被打断（取消 / 全部停止 / 超温切断）不回读。
//   cancel：标记打断 → At=4（停止输出，不写回 0）→ 解除「整定中」。
//
// 线程：只在控制线程用。
#pragma once
#include <array>
#include <functional>
#include <optional>
#include <string>

#include "ctl/ctl_core.hpp"
#include "ctl/ctl_op_result.hpp"

namespace reactor::ctl {

class Autotune {
public:
    static constexpr double kMinSp = 35;  // C# TempService.AutotuneAsync：SP 必须明显高于室温
    static constexpr const char* kNoSetpoint = "SP_NOT_SET";  // C# TempService.NoSetpoint

    explicit Autotune(ControlCore& core, core::PressureUnit unit = core::PressureUnit::MPa);
    ~Autotune();

    // 打开整定目标键盘之前的预检（C# CheckAutotune）：通过 = silent
    OpResult check(int ch) const;
    // C# TryStartAutotune。band 缺省 = 目标温度所在档；strict = 远程严格守卫。done 在控制线程上给结果（要等硬件）
    void start(int ch, double temp_c, std::optional<int> band, bool strict, std::function<void(const OpResult&)> done);
    // C# TryCancelAutotune
    void cancel(int ch, std::function<void(const OpResult&)> done);

    // ---- AutotuneBook ----
    int tune_band(int ch) const { return book_[idx(ch)].band; }
    std::optional<double> tune_sp(int ch) const { return book_[idx(ch)].sp; }
    bool is_aborted(int ch) const { return book_[idx(ch)].aborted; }
    // 快照 autotune（新字段）：本次开机整定过的通道的进度 / 结果；都没有为 null
    ojson snapshot() const;

    static std::string band_name(int band);

private:
    struct Book {
        int band = -1;                 // 进行中的整定要存回的档（-1 = 没有软件发起的整定）
        std::optional<double> sp;
        bool aborted = false;
        // 进度（快照）
        std::string state;             // "" / tuning / reading / done / aborted / failed
        int shown_band = -1;
        std::optional<double> shown_sp;
        std::optional<int64_t> started_at, finished_at;
        int64_t started_mono = 0;
        std::optional<store::Pid> result;
        std::string message;
    };
    static size_t idx(int ch) { return static_cast<size_t>(ch - 1); }
    void on_finished(int ch);
    void on_aborted(int ch);

    ControlCore& core_;
    core::PressureUnit unit_;
    std::array<Book, 8> book_{};
};

}  // namespace reactor::ctl
