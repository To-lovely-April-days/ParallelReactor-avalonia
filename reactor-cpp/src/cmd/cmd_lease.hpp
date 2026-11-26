// 远程控制租约状态机（设计稿 §5.3 / §5.5）。C#: Commands/ControlLease.cs（状态、文案、判定顺序逐条照抄）
//
// 状态 local / requested / remote / remoteLost。持有 ControlArbiter：授权时 enter_remote(令牌名, 客户端名)，
// 释放 / 夺回 / 宽限期满 / 拒绝时 return_to_local —— 都在本类的锁内完成，仲裁器模式和租约状态任何时刻一致。
// leaseId（128 位随机）只在申请响应和带申请凭据（requestKey，一次性随申请响应给出）的 GET /control/requests/{id} 里给出，不进 WS。
// 拒绝 / 申请超时后同一令牌 30 s 内再申请 → RATE_LIMITED；心跳超时 → remoteLost（设备照常运行），宽限期内同一 leaseId
// 心跳 / 受控命令可恢复；宽限期满 → 回本地，并发 Expired（调用方按 onRemoteLost 处理）。
//
// 时刻一律是单调毫秒（调用方传入 now）；所有公开方法先按 now 推进超时，所以计时器没跑也不会放过已过期的租约。
// 线程：任意线程可调。状态在锁内改；on_transition / on_changed 在锁外按发生顺序触发（单消费者排空，回调里再调本类不会嵌套触发）。
#pragma once
#include <atomic>
#include <cstdint>
#include <deque>
#include <functional>
#include <map>
#include <mutex>
#include <optional>
#include <set>
#include <string>
#include <thread>
#include <vector>

#include "cmd/cmd_arbiter.hpp"

namespace reactor::cmd {

enum class LeaseState { Local, Requested, Remote, RemoteLost };
const char* lease_state_word(LeaseState s);   // local / requested / remote / remoteLost

enum class LeaseEventKind { Requested, Granted, Denied, RequestExpired, Released, TakenBack, Lost, Resumed, Expired, Invalidated };
const char* lease_event_name(LeaseEventKind k);   // requested / granted / … （C# 枚举名首字母小写）

struct LeaseEvent {
    LeaseEventKind kind;
    std::string token, client;
    std::optional<std::string> request_id, lease_id;
    std::string detail;
    int64_t at = 0;
    LeaseState state = LeaseState::Local;   // 这一批迁移之后的状态
};

struct LeaseInfo {
    std::string lease_id, token, client;
    int64_t expires_at = 0, hard_expires_at = 0;
};

enum class RequestResult { Unknown, Pending, Granted, Denied, Expired };
const char* request_result_word(RequestResult r);   // unknown / pending / granted / denied / expired

struct RequestStatus {
    RequestResult result = RequestResult::Unknown;
    std::string request_id, token, client;
    std::optional<int64_t> deadline;
    std::optional<LeaseInfo> lease;
};

struct RequestOutcome {
    std::string code, message;   // code 为空 = 成功
    std::string request_id;
    std::optional<int64_t> deadline;
    std::optional<LeaseInfo> lease;
    std::string secret;          // requestKey（只随这次响应给出一次）
    bool ok() const { return code.empty(); }
};

struct LeaseCheck {
    bool ok = false;
    std::string code, message, token, client;
};

struct HeartbeatResult {
    bool ok = false;
    std::string code, message;
    bool resumed = false;
    std::optional<int64_t> expires_at, hard_expires_at;
};

struct LeaseSnapshot {
    LeaseState state = LeaseState::Local;
    std::string token, client;
    std::optional<std::string> request_id;
    std::optional<int64_t> request_deadline, lease_expires_at, hard_expires_at;
    bool has_lease = false;
};

class ControlLease {
public:
    static constexpr const char* kMsgNoLease = "远程模式下受控写命令须带 X-Control-Lease";
    static constexpr const char* kMsgLeaseExpired = "租约已过期或已作废（宽限期满 / 本地夺回 / 急停）· 请重新申请控制权";
    static constexpr const char* kMsgNotCurrentLease = "带的不是当前租约";
    static constexpr const char* kMsgRequestPending = "已有待处理的远程申请";
    static constexpr int kIssuedLeaseHistory = 256;
    static constexpr int kRequestHistory = 50;

    explicit ControlLease(ControlArbiter& arbiter);

    // 超时（毫秒）；<= 0 的项不改（grace / cooldown 可以为 0）。不改状态
    void configure(int64_t heartbeat_ms, int64_t grace_ms, int64_t request_ms, std::optional<int64_t> cooldown_ms = std::nullopt);
    int64_t heartbeat_timeout_ms() const;
    int64_t grace_ms() const;
    int64_t request_timeout_ms() const;
    int64_t deny_cooldown_ms() const;

    LeaseState state() const;
    LeaseSnapshot snapshot(int64_t now);
    ControlArbiter& arbiter() { return arb_; }

    RequestOutcome request(const std::string& token, const std::string& client_name, int64_t now, bool auto_grant);
    std::optional<LeaseInfo> grant(const std::string& request_id, int64_t now);
    bool deny(const std::string& request_id, int64_t now);
    std::optional<RequestStatus> query_request(const std::string& request_id, const std::string& token, const std::string& secret,
                                               int64_t now);
    LeaseCheck validate(const std::string& lease_header, const std::string& token, int64_t now);
    HeartbeatResult heartbeat(const std::string& lease_id, const std::string& token, int64_t now);
    LeaseCheck release(const std::string& lease_id, const std::string& token, int64_t now);
    bool take_back(int64_t now);
    std::vector<LeaseEvent> tick(int64_t now);

    // 订阅（启动前设好）：锁外、按序
    std::function<void(const LeaseEvent&)> on_transition;
    std::function<void()> on_changed;

private:
    struct Rec {
        RequestResult result = RequestResult::Pending;
        std::string token, client, secret;
        int64_t deadline = 0;
        std::string lease_id;
    };
    bool advance_locked(int64_t now, std::vector<LeaseEvent>& ev);
    LeaseInfo grant_locked(int64_t now, std::vector<LeaseEvent>& ev);
    LeaseCheck validate_locked(const std::string& lease, const std::string& token) const;
    bool touch_locked(int64_t now, std::vector<LeaseEvent>& ev);
    void to_local_locked(const char* reason);
    void cooldown_locked(int64_t now);
    std::optional<int64_t> hard_expiry_locked() const;
    LeaseInfo lease_locked() const;
    LeaseEvent ev(LeaseEventKind k, std::string detail, int64_t now) const;
    void remember(const std::string& id, Rec r);
    void commit(std::vector<LeaseEvent>& ev, bool changed);
    void drain();

    ControlArbiter& arb_;
    mutable std::mutex mu_;
    LeaseState state_ = LeaseState::Local;
    std::string request_id_, lease_id_, token_, client_;
    int64_t deadline_ = 0;   // Requested：申请截止；Remote：心跳截止；RemoteLost：宽限截止
    int64_t last_hb_ = 0;
    std::map<std::string, Rec> requests_;
    std::deque<std::string> request_order_;
    std::set<std::string> issued_;
    std::deque<std::string> issued_order_;
    std::map<std::string, int64_t> cooldown_;
    int64_t hb_ms_ = 10000, grace_ms_ = 60000, req_ms_ = 60000, cooldown_ms_ = 30000;

    std::mutex pend_mu_;
    std::deque<std::pair<std::vector<LeaseEvent>, bool>> pending_;
    std::mutex drain_mu_;
    std::atomic<std::thread::id> drainer_{};
};

}  // namespace reactor::cmd
