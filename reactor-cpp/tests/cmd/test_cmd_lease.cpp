// 仲裁判定表 + 租约状态机 + 客户端名清洗。逐条翻译 C# tests/ControlArbiterTests.cs、ControlLeaseTests.cs（输入、期望照抄）。
#include <algorithm>
#include <thread>
#include <vector>

#include "cmd/cmd_arbiter.hpp"
#include "cmd/cmd_lease.hpp"
#include "cmd/cmd_util.hpp"
#include "testing.hpp"
#include <cstdint>
#include <mutex>
#include <string>

using namespace reactor;
using namespace reactor::cmd;

namespace {

const std::string kHolder = "c1", kHolderName = "上位机A";

Source src_of(const std::string& s) {
    if (s == "local") return Source::local();
    if (s == "holder") return Source::remote(kHolder, kHolderName);
    return Source::remote("c2", "上位机B");
}
ActionClass cls_of(const std::string& s) {
    if (s == "Control") return ActionClass::Control;
    if (s == "Stop") return ActionClass::Stop;
    if (s == "Record") return ActionClass::Record;
    return ActionClass::Reset;
}

int64_t S(double s) { return static_cast<int64_t>(s * 1000 + 0.5); }

struct LeaseRig {
    ControlArbiter arb;
    ControlLease l{arb};
    std::vector<LeaseEvent> ev;
    explicit LeaseRig(double hb = 10, double grace = 60, double req = 60, double cooldown = 30) {
        l.configure(S(hb), S(grace), S(req), S(cooldown));
        l.on_transition = [this](const LeaseEvent& e) { ev.push_back(e); };
    }
    std::vector<LeaseEventKind> kinds() const {
        std::vector<LeaseEventKind> k;
        for (const auto& e : ev) k.push_back(e.kind);
        return k;
    }
};

}  // namespace

TEST(cmd_arbiter, decision_table_48) {
    struct Row { const char *mode, *src, *cls, *exp, *expected; };
    const Row rows[] = {
        {"Local", "local", "Control", "Remote", ""}, {"Local", "local", "Control", "LocalOnly", ""},
        {"Local", "local", "Stop", "Remote", ""}, {"Local", "local", "Stop", "LocalOnly", ""},
        {"Local", "local", "Record", "Remote", ""}, {"Local", "local", "Record", "LocalOnly", ""},
        {"Local", "local", "Reset", "Remote", ""}, {"Local", "local", "Reset", "LocalOnly", ""},
        {"Local", "holder", "Control", "Remote", "MODE_LOCAL"}, {"Local", "holder", "Control", "LocalOnly", "NOT_EXPOSED"},
        {"Local", "holder", "Stop", "Remote", ""}, {"Local", "holder", "Stop", "LocalOnly", "NOT_EXPOSED"},
        {"Local", "holder", "Record", "Remote", ""}, {"Local", "holder", "Record", "LocalOnly", "NOT_EXPOSED"},
        {"Local", "holder", "Reset", "Remote", "NOT_EXPOSED"}, {"Local", "holder", "Reset", "LocalOnly", "NOT_EXPOSED"},
        {"Local", "other", "Control", "Remote", "MODE_LOCAL"}, {"Local", "other", "Control", "LocalOnly", "NOT_EXPOSED"},
        {"Local", "other", "Stop", "Remote", ""}, {"Local", "other", "Stop", "LocalOnly", "NOT_EXPOSED"},
        {"Local", "other", "Record", "Remote", ""}, {"Local", "other", "Record", "LocalOnly", "NOT_EXPOSED"},
        {"Local", "other", "Reset", "Remote", "NOT_EXPOSED"}, {"Local", "other", "Reset", "LocalOnly", "NOT_EXPOSED"},
        {"Remote", "local", "Control", "Remote", "MODE_REMOTE"}, {"Remote", "local", "Control", "LocalOnly", "MODE_REMOTE"},
        {"Remote", "local", "Stop", "Remote", ""}, {"Remote", "local", "Stop", "LocalOnly", ""},
        {"Remote", "local", "Record", "Remote", ""}, {"Remote", "local", "Record", "LocalOnly", ""},
        {"Remote", "local", "Reset", "Remote", ""}, {"Remote", "local", "Reset", "LocalOnly", ""},
        {"Remote", "holder", "Control", "Remote", ""}, {"Remote", "holder", "Control", "LocalOnly", "NOT_EXPOSED"},
        {"Remote", "holder", "Stop", "Remote", ""}, {"Remote", "holder", "Stop", "LocalOnly", "NOT_EXPOSED"},
        {"Remote", "holder", "Record", "Remote", ""}, {"Remote", "holder", "Record", "LocalOnly", "NOT_EXPOSED"},
        {"Remote", "holder", "Reset", "Remote", "NOT_EXPOSED"}, {"Remote", "holder", "Reset", "LocalOnly", "NOT_EXPOSED"},
        {"Remote", "other", "Control", "Remote", "NOT_CONTROL_HOLDER"}, {"Remote", "other", "Control", "LocalOnly", "NOT_EXPOSED"},
        {"Remote", "other", "Stop", "Remote", ""}, {"Remote", "other", "Stop", "LocalOnly", "NOT_EXPOSED"},
        {"Remote", "other", "Record", "Remote", ""}, {"Remote", "other", "Record", "LocalOnly", "NOT_EXPOSED"},
        {"Remote", "other", "Reset", "Remote", "NOT_EXPOSED"}, {"Remote", "other", "Reset", "LocalOnly", "NOT_EXPOSED"},
    };
    int n = 0;
    for (const auto& r : rows) {
        bool remote = std::string(r.mode) == "Remote";
        auto d = ControlArbiter::decide(remote ? ControlMode::Remote : ControlMode::Local, remote ? kHolder : "", remote ? kHolderName : "",
                                        cls_of(r.cls), src_of(r.src), std::string(r.exp) == "LocalOnly" ? Exposure::LocalOnly : Exposure::Remote);
        if (std::string(r.expected).empty()) {
            CHECK(d.allowed);
            CHECK(d.code.empty());
        } else {
            CHECK(!d.allowed);
            CHECK_EQ(d.code, std::string(r.expected));
            CHECK(!d.message.empty());
        }
        ++n;
    }
    CHECK_EQ(n, 48);
}

TEST(cmd_arbiter, messages_match_design) {
    CHECK_EQ(ControlArbiter::decide(ControlMode::Local, "", "", ActionClass::Reset, src_of("holder"), Exposure::Remote).message,
             std::string("该操作只允许在设备触屏上进行"));
    CHECK_EQ(ControlArbiter::decide(ControlMode::Local, "", "", ActionClass::Control, src_of("holder"), Exposure::Remote).message,
             std::string("本地控制中 · 远程只能执行停止类和记录类操作"));
    CHECK_EQ(ControlArbiter::decide(ControlMode::Remote, kHolder, kHolderName, ActionClass::Control, src_of("other"), Exposure::Remote).message,
             std::string("当前控制权属于「上位机A」"));
    CHECK_EQ(ControlArbiter::decide(ControlMode::Remote, kHolder, kHolderName, ActionClass::Control, src_of("local"), Exposure::Remote).message,
             std::string("远程控制中（上位机A）"));
    // 空持有者：空 ClientId 的远程来源不能被当成持有者
    CHECK_EQ(ControlArbiter::decide(ControlMode::Remote, "", "", ActionClass::Control, Source::remote("", "匿名"), Exposure::Remote).code,
             std::string("NOT_CONTROL_HOLDER"));
    ControlArbiter a;
    CHECK(a.mode() == ControlMode::Local);
    CHECK(a.check(ActionClass::Control, Source::local()).allowed);
    a.enter_remote(kHolder, kHolderName);
    CHECK(a.check(ActionClass::Control, src_of("holder")).allowed);
    CHECK(!a.check(ActionClass::Control, Source::local()).allowed);
    CHECK(a.return_to_local("x"));
    CHECK(!a.return_to_local("y"));
    CHECK_EQ(a.last_return_reason(), std::string("y"));
}

TEST(cmd_lease, request_grant_validate_release) {
    LeaseRig t;
    auto r = t.l.request("tokA", "PC-1", S(0), false);
    CHECK(r.ok());
    CHECK(t.l.state() == LeaseState::Requested);
    CHECK(t.arb.mode() == ControlMode::Local);
    CHECK(r.deadline && *r.deadline == S(60));
    CHECK_EQ(t.ev.size(), static_cast<size_t>(1));
    CHECK(t.ev[0].kind == LeaseEventKind::Requested);

    CHECK_EQ(t.l.validate("", "tokA", S(1)).code, std::string("MODE_LOCAL"));
    CHECK_EQ(t.l.request("tokB", "PC-2", S(1), false).code, std::string("CONTROL_REQUEST_PENDING"));
    CHECK_EQ(t.l.request("tokA", "PC-1", S(1), false).request_id, r.request_id);
    CHECK(t.l.query_request(r.request_id, "tokA", r.secret, S(1))->result == RequestResult::Pending);

    auto lease = t.l.grant(r.request_id, S(2));
    CHECK(lease.has_value());
    CHECK_EQ(lease->lease_id.size(), static_cast<size_t>(32));
    CHECK(t.l.state() == LeaseState::Remote);
    CHECK(t.arb.mode() == ControlMode::Remote);
    CHECK_EQ(t.arb.holder_id(), std::string("tokA"));
    CHECK_EQ(t.arb.holder_name(), std::string("PC-1"));
    CHECK_EQ(lease->expires_at, S(12));
    CHECK_EQ(lease->hard_expires_at, S(72));

    CHECK_EQ(t.l.query_request(r.request_id, "tokA", r.secret, S(3))->lease->lease_id, lease->lease_id);
    CHECK(!t.l.query_request(r.request_id, "tokB", r.secret, S(3))->lease);
    CHECK(t.l.query_request(r.request_id, "tokB", r.secret, S(3))->result == RequestResult::Granted);

    CHECK_EQ(t.l.validate("", "tokA", S(3)).code, std::string("NOT_CONTROL_HOLDER"));
    CHECK_EQ(t.l.validate("deadbeef", "tokA", S(3)).code, std::string("NOT_CONTROL_HOLDER"));
    CHECK_EQ(t.l.validate(lease->lease_id, "tokB", S(3)).code, std::string("NOT_CONTROL_HOLDER"));
    auto ok = t.l.validate(lease->lease_id, "tokA", S(3));
    CHECK(ok.ok);
    CHECK_EQ(ok.client, std::string("PC-1"));
    CHECK_EQ(t.l.request("tokB", "PC-2", S(3), false).code, std::string("NOT_CONTROL_HOLDER"));

    CHECK(t.l.release(lease->lease_id, "tokA", S(4)).ok);
    CHECK(t.l.state() == LeaseState::Local);
    CHECK(t.arb.mode() == ControlMode::Local);
    CHECK_EQ(t.arb.last_return_reason(), std::string("远程释放"));
    CHECK_EQ(t.l.validate(lease->lease_id, "tokA", S(5)).code, std::string("MODE_LOCAL"));
    auto r2 = t.l.request("tokA", "PC-1", S(6), true);
    CHECK(r2.lease.has_value());
    CHECK_EQ(t.l.validate(lease->lease_id, "tokA", S(7)).code, std::string("CONTROL_LEASE_EXPIRED"));
    std::vector<LeaseEventKind> want{LeaseEventKind::Requested, LeaseEventKind::Granted, LeaseEventKind::Released,
                                     LeaseEventKind::Requested, LeaseEventKind::Granted};
    CHECK(t.kinds() == want);
}

TEST(cmd_lease, auto_grant_grants_immediately) {
    LeaseRig t;
    auto r = t.l.request("tokA", "PC-1", S(0), true);
    CHECK(r.ok());
    CHECK(r.lease.has_value());
    CHECK(t.l.state() == LeaseState::Remote);
    CHECK(t.arb.mode() == ControlMode::Remote);
    std::vector<LeaseEventKind> want{LeaseEventKind::Requested, LeaseEventKind::Granted};
    CHECK(t.kinds() == want);
    CHECK(t.ev[0].detail.find("自动授权") != std::string::npos);
}

TEST(cmd_lease, deny_and_request_timeout_with_cooldown) {
    LeaseRig t(10, 60, 60);
    auto r = t.l.request("tokA", "PC-1", S(0), false);
    CHECK(t.l.deny(r.request_id, S(1)));
    CHECK(t.l.state() == LeaseState::Local);
    CHECK(t.l.query_request(r.request_id, "tokA", r.secret, S(1))->result == RequestResult::Denied);
    CHECK(!t.l.deny(r.request_id, S(1)));
    CHECK(!t.l.grant(r.request_id, S(1)));

    CHECK_EQ(t.l.request("tokA", "PC-1", S(10), false).code, std::string("RATE_LIMITED"));
    CHECK(t.l.state() == LeaseState::Local);
    auto r2 = t.l.request("tokA", "PC-1", S(31), false);
    CHECK(r2.ok());
    CHECK(t.l.tick(S(90.9)).empty());
    auto evs = t.l.tick(S(91));
    CHECK_EQ(evs.size(), static_cast<size_t>(1));
    CHECK(evs[0].kind == LeaseEventKind::RequestExpired);
    CHECK(t.l.state() == LeaseState::Local);
    CHECK(t.l.query_request(r2.request_id, "tokA", r2.secret, S(92))->result == RequestResult::Expired);
    CHECK(!t.l.grant(r2.request_id, S(92)));
    CHECK(t.arb.mode() == ControlMode::Local);
    CHECK_EQ(t.l.request("tokA", "PC-1", S(93), false).code, std::string("RATE_LIMITED"));
    CHECK(t.l.request("tokB", "PC-2", S(93), false).ok());
}

TEST(cmd_lease, heartbeat_lost_resume_expire) {
    LeaseRig t(10, 60);
    auto lease = *t.l.request("tokA", "PC-1", S(0), true).lease;
    auto hb = t.l.heartbeat(lease.lease_id, "tokA", S(5));
    CHECK(hb.ok);
    CHECK(!hb.resumed);
    CHECK(hb.expires_at && *hb.expires_at == S(15));
    CHECK(t.l.tick(S(14.9)).empty());
    auto lost = t.l.tick(S(15));
    CHECK(lost.size() == 1 && lost[0].kind == LeaseEventKind::Lost);
    CHECK(t.l.state() == LeaseState::RemoteLost);
    CHECK(t.arb.mode() == ControlMode::Remote);
    auto back = t.l.heartbeat(lease.lease_id, "tokA", S(30));
    CHECK(back.ok && back.resumed);
    CHECK(t.l.state() == LeaseState::Remote);
    t.l.tick(S(40));
    CHECK(t.l.state() == LeaseState::RemoteLost);
    CHECK(t.l.tick(S(99.9)).empty());
    auto exp = t.l.tick(S(100));
    CHECK(exp.size() == 1 && exp[0].kind == LeaseEventKind::Expired);
    CHECK(t.l.state() == LeaseState::Local);
    CHECK(t.arb.mode() == ControlMode::Local);
    CHECK_EQ(t.arb.last_return_reason(), std::string("心跳丢失"));
    CHECK_EQ(t.l.heartbeat(lease.lease_id, "tokA", S(101)).code, std::string("MODE_LOCAL"));
}

TEST(cmd_lease, validate_counts_as_heartbeat_and_zero_grace) {
    {
        LeaseRig t(10, 60);
        auto lease = *t.l.request("tokA", "PC-1", S(0), true).lease;
        t.l.tick(S(20));
        CHECK(t.l.state() == LeaseState::RemoteLost);
        CHECK(t.l.validate(lease.lease_id, "tokA", S(25)).ok);
        CHECK(t.l.state() == LeaseState::Remote);
        CHECK(*t.l.snapshot(S(26)).lease_expires_at == S(35));
    }
    {
        LeaseRig t(3, 0);
        t.l.request("tokA", "PC-1", S(0), true);
        auto evs = t.l.tick(S(3));
        CHECK(evs.size() == 2 && evs[0].kind == LeaseEventKind::Lost && evs[1].kind == LeaseEventKind::Expired);
        CHECK(t.l.state() == LeaseState::Local);
        CHECK(t.arb.mode() == ControlMode::Local);
    }
}

TEST(cmd_lease, take_back_from_requested_and_remote) {
    LeaseRig t;
    t.l.request("tokA", "PC-1", S(0), false);
    CHECK(t.l.take_back(S(1)));
    CHECK(t.ev.back().kind == LeaseEventKind::Denied);
    CHECK(!t.l.take_back(S(1)));
    // 被切回也进冷却：等过 30 s 再申请
    auto lease = *t.l.request("tokA", "PC-1", S(40), true).lease;
    CHECK(t.l.take_back(S(41)));
    CHECK(t.ev.back().kind == LeaseEventKind::TakenBack);
    CHECK(t.arb.mode() == ControlMode::Local);
    CHECK_EQ(t.arb.last_return_reason(), std::string("本地切回"));
    t.l.request("tokA", "PC-1", S(42), true);
    CHECK_EQ(t.l.validate(lease.lease_id, "tokA", S(43)).code, std::string("CONTROL_LEASE_EXPIRED"));
}

TEST(cmd_lease, external_return_to_local_invalidates_on_next_call) {
    // C# 的急停快速路径直接收回仲裁器；新后端没有急停，但保留「仲裁器被外部收回 → 租约作废」的判定（下一次调用时生效）
    LeaseRig t;
    auto lease = *t.l.request("tokA", "PC-1", S(0), true).lease;
    bool changed = false;
    t.l.on_changed = [&] { changed = true; };
    t.arb.return_to_local("外部收回");
    t.l.tick(S(1));
    CHECK(t.l.state() == LeaseState::Local);
    CHECK(changed);
    CHECK(t.ev.back().kind == LeaseEventKind::Invalidated);
    CHECK(t.ev.back().detail.find("外部收回") != std::string::npos);
    CHECK_EQ(t.l.validate(lease.lease_id, "tokA", S(1)).code, std::string("MODE_LOCAL"));
    CHECK(!t.l.snapshot(S(1)).has_lease);
}

TEST(cmd_lease, snapshot_history_secret) {
    {
        LeaseRig t(10, 60, 30);
        auto r = t.l.request("tokA", "PC-1", S(0), false);
        auto s = t.l.snapshot(S(1));
        CHECK(s.state == LeaseState::Requested);
        CHECK(s.request_id && *s.request_id == r.request_id);
        CHECK(*s.request_deadline == S(30));
        CHECK(!s.has_lease);
        t.l.grant(r.request_id, S(2));
        s = t.l.snapshot(S(3));
        CHECK(s.state == LeaseState::Remote && s.has_lease);
        CHECK(*s.lease_expires_at == S(12));
        CHECK(*s.hard_expires_at == S(72));
        CHECK_EQ(s.client, std::string("PC-1"));
        CHECK_EQ(s.token, std::string("tokA"));
    }
    {
        LeaseRig t(10, 60, 60, 0);
        CHECK(!t.l.query_request("nope", "tokA", "", S(0)));
        std::string first;
        for (int i = 0; i < 60; ++i) {
            auto r = t.l.request("tokA", "PC-1", S(i), false);
            if (first.empty()) first = r.request_id;
            t.l.deny(r.request_id, S(i));
        }
        CHECK(!t.l.query_request(first, "tokA", "", S(61)));   // 只保留最近 50 条
    }
    {
        LeaseRig t;
        auto r = t.l.request("tokA", "PC-1", S(0), true);
        CHECK_EQ(r.secret.size(), static_cast<size_t>(32));
        CHECK(!t.l.query_request(r.request_id, "tokA", "", S(1))->lease);
        CHECK(!t.l.query_request(r.request_id, "tokA", "deadbeef", S(1))->lease);
        CHECK_EQ(t.l.query_request(r.request_id, "tokA", r.secret, S(1))->lease->lease_id, r.lease->lease_id);
        CHECK(!t.l.query_request(r.request_id, "tokB", r.secret, S(1))->lease);
        LeaseRig t2;
        auto p = t2.l.request("tokA", "PC-1", S(0), false);
        auto again = t2.l.request("tokA", "PC-1", S(1), false);
        CHECK_EQ(p.request_id, again.request_id);
        CHECK_EQ(p.secret, again.secret);
    }
}

TEST(cmd_lease, remote_lost_same_client_rerequest) {
    LeaseRig t(10, 60);
    auto old = *t.l.request("tokA", "PC-1", S(0), true).lease;
    t.l.tick(S(11));
    CHECK(t.l.state() == LeaseState::RemoteLost);
    CHECK_EQ(t.l.request("tokB", "PC-2", S(12), true).code, std::string("NOT_CONTROL_HOLDER"));
    CHECK_EQ(t.l.request("tokA", "PC-9", S(12), true).code, std::string("NOT_CONTROL_HOLDER"));
    auto r = t.l.request("tokA", "PC-1", S(12), true);
    CHECK(r.ok());
    CHECK(old.lease_id != r.lease->lease_id);
    CHECK(t.l.state() == LeaseState::Remote);
    std::vector<LeaseEventKind> want{LeaseEventKind::Requested, LeaseEventKind::Granted, LeaseEventKind::Lost,
                                     LeaseEventKind::Invalidated, LeaseEventKind::Requested, LeaseEventKind::Granted};
    CHECK(t.kinds() == want);
    CHECK_EQ(t.l.validate(old.lease_id, "tokA", S(13)).code, std::string("CONTROL_LEASE_EXPIRED"));
    CHECK(t.l.validate(r.lease->lease_id, "tokA", S(13)).ok);
    for (size_t i = 3; i < t.ev.size(); ++i) CHECK(t.ev[i].state == LeaseState::Remote);
    CHECK(t.ev[2].state == LeaseState::RemoteLost);
}

TEST(cmd_lease, sanitize_client_name) {
    CHECK_EQ(sanitize_client_name("PC-1"), std::string("PC-1"));
    CHECK_EQ(sanitize_client_name("  PC  1\t\n2 "), std::string("PC 1 2"));
    CHECK_EQ(sanitize_client_name("维护‮工程师​（已批准）\r\n请点允许"), std::string("维护工程师（已批准） 请点允许"));
    CHECK_EQ(sanitize_client_name("\x07\x1b[31m"), std::string("[31m"));
    CHECK_EQ(sanitize_client_name("​\x07"), std::string(""));
    CHECK_EQ(sanitize_client_name(std::string(200, 'x')).size(), static_cast<size_t>(kClientNameMax));
    CHECK_EQ(sanitize_client_name("ok😀"), std::string("ok"));   // BMP 之外（C# 代理项）去掉
    LeaseRig t;
    auto r = t.l.request("tokA", "​\x07", S(0), true);
    CHECK_EQ(r.lease->client, std::string("tokA"));
    CHECK_EQ(t.arb.holder_name(), std::string("tokA"));
}

TEST(cmd_lease, concurrent_expire_and_request_never_invalidates_fresh_lease) {
    for (int round = 0; round < 50; ++round) {
        ControlArbiter arb;
        ControlLease l(arb);
        l.configure(S(1), S(0), S(60), S(0));
        std::mutex mu;
        std::vector<LeaseEvent> ev;
        l.on_transition = [&](const LeaseEvent& e) {
            std::lock_guard<std::mutex> lk(mu);
            ev.push_back(e);
        };
        l.request("tokA", "PC-1", S(0), true);
        RequestOutcome r2;
        std::thread t1([&] { l.tick(S(5)); });
        std::thread t2([&] { r2 = l.request("tokB", "PC-2", S(5), true); });
        t1.join();
        t2.join();
        if (r2.ok()) {
            CHECK(l.state() == LeaseState::Remote);
            CHECK(arb.mode() == ControlMode::Remote);
            CHECK_EQ(arb.holder_id(), std::string("tokB"));
            CHECK(l.validate(r2.lease->lease_id, "tokB", S(5)).ok);
        } else {
            CHECK_EQ(r2.code, std::string("NOT_CONTROL_HOLDER"));
            CHECK(l.state() == LeaseState::Local);
            CHECK(arb.mode() == ControlMode::Local);
        }
        for (const auto& e : ev) CHECK(e.kind != LeaseEventKind::Invalidated);
        CHECK(!ev.empty() && ev.back().state == l.state());
    }
}

TEST(cmd_util, random_ids) {
    auto a = random_hex(16), b = random_hex(16);
    CHECK_EQ(a.size(), static_cast<size_t>(32));
    CHECK(a != b);
    auto g = new_guid();
    CHECK_EQ(g.size(), static_cast<size_t>(36));
    CHECK(g[8] == '-' && g[13] == '-' && g[14] == '4' && g[18] == '-' && g[23] == '-');
}
