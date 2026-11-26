// 第二阶段 API 的统一结果（C#: Commands/OpResult.cs 的 Done / Fail / Accepted / Silent）。
//   ok        = false：被拒绝 / 失败（code = §11 错误码，message = 中文原文，data = error.detail）
//   accepted  = true ：已受理、在后台执行（进度与结果走快照 / 事件，不是返回值），对应 HTTP 202
//   silent    = true ：什么也没做、不提示（幂等的中止等）
//   quiet     = true ：触屏不弹提示（C# with { Quiet = true }）
//   level     ：ok / warn / err（触屏提示颜色）
//   warnings  ：不拦的提示项（C# WithWarnings）
// 命令层（第三阶段）把它原样映射成 HTTP 响应：成功 → { ok, message, result: data, warnings }，失败 → { error: { code, message, detail: data } }。
#pragma once
#include <string>
#include <vector>

#include "core/op_codes.hpp"
#include "core/value_rules.hpp"
#include "ctl/ctl_hooks.hpp"

namespace reactor::ctl {

struct OpResult {
    bool ok = true;
    bool accepted = false;
    bool silent = false;
    bool quiet = false;
    std::string code = core::op_codes::kOk;
    std::string message;
    std::string level = "ok";
    ojson data;  // 成功：result；失败：error.detail；没有为 null
    std::vector<std::string> warnings;

    static OpResult done(std::string msg, std::string level = "ok", ojson data = nullptr) {
        OpResult r;
        r.message = std::move(msg);
        r.level = std::move(level);
        r.data = std::move(data);
        return r;
    }
    static OpResult fail(std::string code, std::string msg, std::string level = "err", ojson data = nullptr) {
        OpResult r;
        r.ok = false;
        r.code = std::move(code);
        r.message = std::move(msg);
        r.level = std::move(level);
        r.data = std::move(data);
        return r;
    }
    static OpResult accept(std::string msg = "", std::string level = "ok", ojson data = nullptr) {
        OpResult r = done(std::move(msg), std::move(level), std::move(data));
        r.accepted = true;
        return r;
    }
    static OpResult none() {
        OpResult r;
        r.silent = true;
        r.quiet = true;
        return r;
    }
    // 数值越界（C# OperatorCommands.Invalid）：detail = { min, max, value }
    static OpResult invalid(const core::RuleViolation& v) {
        return fail(v.code, v.message, "err", ojson{{"min", v.min}, {"max", v.max}, {"value", v.value}});
    }
    OpResult& with_warnings(const std::vector<std::string>& w) {
        warnings.insert(warnings.end(), w.begin(), w.end());
        return *this;
    }
    OpResult& set_quiet() {
        quiet = true;
        return *this;
    }
};

}  // namespace reactor::ctl
