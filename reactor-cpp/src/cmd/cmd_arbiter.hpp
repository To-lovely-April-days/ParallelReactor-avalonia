// 控制权仲裁（设计稿 §5.1，判定表 §2.3）。C#: Commands/ControlArbiter.cs（判定表逐行照抄）
//
// 模式与持有者成对放在锁里：任意线程可读；enter_remote / return_to_local 只由 ControlLease 在它自己的锁内调用，
// 所以仲裁器模式与租约状态任何时刻一致。没有急停之后，不再有「外部收回」的路径。
#pragma once
#include <mutex>
#include <string>

#include "cmd/cmd_source.hpp"

namespace reactor::cmd {

enum class ControlMode { Local, Remote };
// 动作类别：Control = 启动 / 改设定 / 下发；Stop = 停止类；Record = 记录类；Reset = 复位（新后端没有急停复位，保留枚举）
enum class ActionClass { Control, Stop, Record, Reset };
// 是否对外开放：LocalOnly = 只在触屏（Unix socket）上开放
enum class Exposure { Remote, LocalOnly };

struct Decision {
    bool allowed = true;
    std::string code, message;
    static Decision allow() { return Decision{}; }
    static Decision deny(std::string c, std::string m) { return Decision{false, std::move(c), std::move(m)}; }
};

class ControlArbiter {
public:
    static constexpr const char* kMsgNotExposed = "该操作只允许在设备触屏上进行";
    static constexpr const char* kMsgModeLocal = "本地控制中 · 远程只能执行停止类和记录类操作";

    ControlMode mode() const;
    std::string holder_id() const;
    std::string holder_name() const;
    std::string last_return_reason() const;

    void enter_remote(const std::string& client_id, const std::string& name);
    // 回到本地；返回之前是否在远程
    bool return_to_local(const std::string& reason);

    Decision check(ActionClass cls, const Source& src, Exposure exp = Exposure::Remote) const;
    // 判定表（纯函数）
    static Decision decide(ControlMode mode, const std::string& holder_id, const std::string& holder_name, ActionClass cls,
                           const Source& src, Exposure exp);

private:
    mutable std::mutex mu_;
    ControlMode mode_ = ControlMode::Local;
    std::string holder_id_, holder_name_, last_reason_;
};

}  // namespace reactor::cmd
