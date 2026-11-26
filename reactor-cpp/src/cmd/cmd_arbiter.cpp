#include "cmd/cmd_arbiter.hpp"

#include "core/op_codes.hpp"
#include <mutex>
#include <string>

namespace reactor::cmd {

bool CommandPolicy::strict_local = false;

ControlMode ControlArbiter::mode() const {
    std::lock_guard<std::mutex> lk(mu_);
    return mode_;
}
std::string ControlArbiter::holder_id() const {
    std::lock_guard<std::mutex> lk(mu_);
    return holder_id_;
}
std::string ControlArbiter::holder_name() const {
    std::lock_guard<std::mutex> lk(mu_);
    return holder_name_;
}
std::string ControlArbiter::last_return_reason() const {
    std::lock_guard<std::mutex> lk(mu_);
    return last_reason_;
}

void ControlArbiter::enter_remote(const std::string& client_id, const std::string& name) {
    std::lock_guard<std::mutex> lk(mu_);
    mode_ = ControlMode::Remote;
    holder_id_ = client_id;
    holder_name_ = name;
}

bool ControlArbiter::return_to_local(const std::string& reason) {
    std::lock_guard<std::mutex> lk(mu_);
    bool was = mode_ == ControlMode::Remote;
    mode_ = ControlMode::Local;
    holder_id_.clear();
    holder_name_.clear();
    last_reason_ = reason;
    return was;
}

Decision ControlArbiter::check(ActionClass cls, const Source& src, Exposure exp) const {
    ControlMode m;
    std::string id, name;
    {
        std::lock_guard<std::mutex> lk(mu_);
        m = mode_;
        id = holder_id_;
        name = holder_name_;
    }
    return decide(m, id, name, cls, src, exp);
}

// C#: ControlArbiter.Decide
Decision ControlArbiter::decide(ControlMode mode, const std::string& holder_id, const std::string& holder_name, ActionClass cls,
                                const Source& src, Exposure exp) {
    namespace oc = core::op_codes;
    // 1. 远程来源：不对外的动作、复位一律拒绝
    if (src.is_remote() && (exp == Exposure::LocalOnly || cls == ActionClass::Reset))
        return Decision::deny(oc::kNotExposed, kMsgNotExposed);
    // 2. 停止 / 记录 / 复位任何模式都允许
    if (cls != ActionClass::Control) return Decision::allow();
    if (mode == ControlMode::Local)  // 3 / 4
        return src.is_remote() ? Decision::deny(oc::kModeLocal, kMsgModeLocal) : Decision::allow();
    if (src.is_remote())  // 5 / 6
        return src.client_id == holder_id && !holder_id.empty()
                   ? Decision::allow()
                   : Decision::deny(oc::kNotControlHolder, "当前控制权属于「" + holder_name + "」");
    // 7. 远程控制中，触屏的启动 / 改设定被拒绝
    return Decision::deny(oc::kModeRemote, "远程控制中（" + holder_name + "）");
}

}  // namespace reactor::cmd
