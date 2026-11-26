// 控制权端点（C#: Api/Endpoints.Control.cs）+ 触屏的允许 / 拒绝 / 切回本地（C# OperatorCommands.Control.cs，只在 Unix 入口上开放）。
// 租约是线程安全的纯逻辑状态机：申请 / 心跳 / 释放不进控制线程（inline_exec）。
#include "api/api_routes.hpp"
#include "cmd/cmd_util.hpp"
#include "net/api_headers.hpp"

namespace reactor::api {

namespace {
using net::Request;
using net::Response;
using net::RouteOptions;
using net::RateClass;
using cmd::ActionClass;
using cmd::Commands;
using cmd::Done;
using cmd::Source;
using ctl::OpResult;
}  // namespace

void Routes::control(ApiService& s, net::Router& r) {
    ApiService* self = &s;
    const std::string P = "/api/v1";
    auto W = [self](WriteSpec spec, Binder b) { return [self, spec, b](const Request& q) { return self->command(q, spec, b); }; };

    r.get(P + "/control", [self](const Request&) { return ok_json(self->control_state_json()); });

    // POST /control/request {clientName}：登记申请（202 pending；autoGrant → 202 granted 带 leaseId + requestKey）
    WriteSpec req_spec{"control.request", ActionClass::Record, true};
    req_spec.inline_exec = true;
    r.post(P + "/control/request", W(req_spec, [self](const nlohmann::json* body, const Request& q, const Source& src) {
        if (q.auth.local) return Bound::bad("触屏本来就有控制权 · 不需要申请");
        Params p(body);
        auto client = p.str("clientName");
        if (!client && !p.bad()) client = p.str("client");
        if (p.bad()) return Bound::bad(p.error);
        std::string name = client.value_or(src.client_id);
        if (utf16_len(name) > static_cast<size_t>(cmd::kClientNameMax))
            return Bound::bad("clientName 过长（≤ " + std::to_string(cmd::kClientNameMax) + " 字符）");
        std::string token = src.client_id;
        Bound b;
        b.exec = [self, token, name](Commands&, const Source&, Done d) {
            auto out = self->lease_.request(token, name, self->now_mono(), self->d_.cfg.auto_grant_remote);
            if (!out.ok()) return d(OpResult::fail(out.code, out.message));
            d(OpResult::accept(out.lease ? "已授权远程控制" : "申请已登记 · 等待触屏确认", "ok", self->request_result_json(out)));
        };
        return b;
    }));

    // GET /control/requests/{id}：申请方轮询；leaseId 只对申请令牌且带 X-Control-Request-Key 给出
    r.get(P + "/control/requests/{requestId}", [self](const Request& q) {
        auto st = self->lease_.query_request(q.param("requestId"), q.auth.name, trim(q.header(net::headers::kRequestKey)), self->now_mono());
        if (!st) return api_error("NOT_FOUND", "没有这个申请（或已过期被清理）");
        return ok_json(self->request_status_json(*st));
    });

    // POST /control/heartbeat（带 X-Control-Lease）：续租；宽限期内恢复为 Remote。不审计、不推 opEvent（同 C#）
    r.post(P + "/control/heartbeat", [self](const Request& q) {
        if (!q.auth.has_role(net::Role::Operator)) return api_error("FORBIDDEN_ROLE", "心跳需要 operator 及以上角色");
        auto hb = self->lease_.heartbeat(q.header(net::headers::kLease), q.auth.name, self->now_mono());
        if (!hb.ok) return api_error(hb.code, hb.message);
        ojson m = self->mode();
        ojson res{{"mode", m["control"]},
                  {"resumed", hb.resumed},
                  {"leaseExpiresAt", opt_str(self->utc_of_mono(hb.expires_at))},
                  {"leaseHardExpiresAt", opt_str(self->utc_of_mono(hb.hard_expires_at))},
                  {"heartbeatTimeoutSec", self->d_.cfg.heartbeat_timeout_sec}};
        ojson j{{"ok", true}, {"opId", cmd::new_guid()}, {"status", "confirmed"}, {"result", res}, {"warnings", ojson::array()}, {"opEventSeq", 0}};
        return ok_json(j);
    }, RouteOptions{RateClass::Heartbeat, false, false});

    // POST /control/release（带 X-Control-Lease）：交还控制权
    WriteSpec rel_spec{"control.release", ActionClass::Record};
    rel_spec.inline_exec = true;
    r.post(P + "/control/release", W(rel_spec, [self](const nlohmann::json*, const Request& q, const Source&) {
        std::string lease = q.header(net::headers::kLease), token = q.auth.name;
        Bound b;
        b.exec = [self, lease, token](Commands&, const Source&, Done d) {
            auto rr = self->lease_.release(lease, token, self->now_mono());
            d(rr.ok ? OpResult::done("已交还控制权 · 回到本地", "ok", ojson{{"mode", "local"}}) : OpResult::fail(rr.code, rr.message));
        };
        return b;
    }));

    // —— 触屏（本机）：允许 / 拒绝 / 切回本地 ——
    RouteOptions local_only{RateClass::Auto, false, true};
    auto local_spec = [](const char* action) {
        WriteSpec w{action, ActionClass::Record};
        w.inline_exec = true;
        return w;
    };
    r.post(P + "/control/requests/{requestId}/grant", W(local_spec("control.grant"), [](const nlohmann::json*, const Request& q, const Source&) {
        std::string id = q.param("requestId");
        Bound b;
        b.exec = [id](Commands& c, const Source& s, Done d) { d(c.grant_control(s, id)); };
        return b;
    }), local_only);
    r.post(P + "/control/requests/{requestId}/deny", W(local_spec("control.deny"), [](const nlohmann::json*, const Request& q, const Source&) {
        std::string id = q.param("requestId");
        Bound b;
        b.exec = [id](Commands& c, const Source& s, Done d) { d(c.deny_control(s, id)); };
        return b;
    }), local_only);
    r.post(P + "/control/take-back", W(local_spec("control.takeBack"), [](const nlohmann::json*, const Request&, const Source&) {
        Bound b;
        b.exec = [](Commands& c, const Source& s, Done d) { d(c.take_back_control(s)); };
        return b;
    }), local_only);
}

}  // namespace reactor::api
