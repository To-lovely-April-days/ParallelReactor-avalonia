// 鉴权 / 白名单 / 配置 / 限流 / 幂等 的单元测试。逐条翻译自 C# tests/ParallelReactor.Tests 的
// TokenAuthTests、CidrTests、ApiConfigTests、IdempotencyStoreTests（输入和期望照抄），再加 C++ 版特有的几条。
#include <stdlib.h>
#include <unistd.h>

#include <atomic>
#include <chrono>
#include <cstdio>
#include <fstream>
#include <map>
#include <thread>

#include "net/api_config.hpp"
#include "net/api_errors.hpp"
#include "net/auth.hpp"
#include "net/cidr.hpp"
#include "net/crypto.hpp"
#include "net/idempotency.hpp"
#include "net/rate_limit.hpp"
#include "testing.hpp"

using namespace reactor::net;

namespace {
ApiToken tok(const std::string& name, const std::string& sha, const std::string& role = "viewer",
             std::optional<bool> estop = std::nullopt) {
    ApiToken t;
    t.name = name;
    t.sha256 = sha;
    t.role = role;
    t.estop = estop;
    return t;
}
std::string upper(std::string s) {
    for (auto& c : s) c = static_cast<char>(std::toupper(static_cast<unsigned char>(c)));
    return s;
}
std::string tmpdir() {
    char tmpl[] = "/tmp/net-test-XXXXXX";
    char* d = mkdtemp(tmpl);
    return d ? std::string(d) : std::string("/tmp");
}
bool allowed(const CidrMatcher& m, const char* ip) { return m.is_allowed(parse_ip(ip)); }
}  // namespace

// ———————— TokenAuthTests ————————

TEST(net_auth, hash_is_lowercase_sha256_hex) {
    CHECK_EQ(sha256_hex("devtoken"), std::string("9428e07c68054de014032f21e0716501aa937714fd31033caf589e66b276e53b"));
    CHECK_EQ(sha256_hex("").size(), size_t(64));
}

TEST(net_auth, matches_lower_and_upper_hex) {
    TokenTable t({tok("pc", upper(sha256_hex("secret")), "operator")});
    auto r = t.authenticate_header("Bearer secret");
    CHECK(r.ok);
    CHECK_EQ(r.name, std::string("pc"));
    CHECK(r.role == Role::Operator);
    CHECK(r.estop);   // operator 默认带急停权限
}

TEST(net_auth, rejects_missing_or_garbled) {
    TokenTable t({tok("pc", sha256_hex("secret"))});
    const char* bad[] = {"", "Bearer", "Bearer ", "Basic c2VjcmV0", "secret", "Bearer wrong"};
    for (auto h : bad) CHECK(!t.authenticate_header(h).ok);
}

TEST(net_auth, scheme_case_insensitive_and_trims) {
    TokenTable t({tok("pc", sha256_hex("secret"))});
    CHECK(t.authenticate_header("bearer   secret  ").ok);
}

TEST(net_auth, viewer_estop_flag) {
    TokenTable t({tok("watch", sha256_hex("w"), "viewer", false), tok("watch-estop", sha256_hex("e"), "viewer", true)});
    CHECK(!t.authenticate_token("w").estop);
    CHECK(t.authenticate_token("e").estop);
    TokenTable t2({tok("op-noestop", sha256_hex("n"), "operator", false), tok("op-default", sha256_hex("d"), "operator")});
    CHECK(!t2.authenticate_token("n").estop);
    CHECK(t2.authenticate_token("d").estop);
    CHECK_EQ(t.authenticate_token("e").name, std::string("watch-estop"));
}

TEST(net_auth, bad_hash_entries_skipped) {
    TokenTable t({tok("broken", "abc"), tok("ok", sha256_hex("t"))});
    CHECK(!t.authenticate_token("abc").ok);
    CHECK_EQ(t.authenticate_token("t").name, std::string("ok"));
}

TEST(net_auth, roles_rank) {
    AuthContext a;
    a.ok = true;
    a.role = Role::Admin;
    CHECK(a.has_role(Role::Operator));
    a.role = Role::Viewer;
    CHECK(a.has_role(Role::Viewer));
    CHECK(!a.has_role(Role::Operator));
    CHECK(!AuthContext::denied().has_role(Role::Viewer));
    CHECK(parse_role("root") == Role::Viewer);
    CHECK(parse_role(" ADMIN ") == Role::Admin);
    auto l = AuthContext::local_identity();
    CHECK(l.ok && l.local && l.estop && l.role == Role::Admin);
    CHECK_EQ(l.name, std::string("local"));
}

TEST(net_auth, keyed_throttle_one_per_window) {
    KeyedThrottle t(60 * 1000);
    int64_t now = 1000000;
    CHECK(t.allow("10.0.0.1", now));
    CHECK(!t.allow("10.0.0.1", now + 30000));
    CHECK(t.allow("10.0.0.2", now + 30000));
    CHECK(t.allow("10.0.0.1", now + 61000));
}

TEST(net_auth, failure_limiter_blocks_after_limit) {
    FailureLimiter f(3, 1000);
    int64_t now = 5000000;
    CHECK(!f.is_blocked("ip", now));
    CHECK(!f.record_failure("ip", now));
    CHECK(!f.record_failure("ip", now + 100));
    CHECK(f.record_failure("ip", now + 200));   // 第 3 次：到达上限
    CHECK(f.is_blocked("ip", now + 900));
    CHECK(!f.is_blocked("other", now + 900));
    CHECK(!f.is_blocked("ip", now + 1000));      // 新窗口
    CHECK(!f.record_failure("ip", now + 1000));
}

// ———————— CidrTests ————————

TEST(net_cidr, empty_allows_loopback_only) {
    CidrMatcher m({});
    CHECK(allowed(m, "127.0.0.1"));
    CHECK(allowed(m, "::1"));
    CHECK(allowed(m, "::ffff:127.0.0.1"));
    CHECK(!allowed(m, "192.168.1.5"));
    CHECK(m.is_allowed(std::nullopt));
}

TEST(net_cidr, ipv4_ranges) {
    struct C { const char* cidr; const char* ip; bool expect; } cases[] = {
        {"192.168.10.0/24", "192.168.10.77", true}, {"192.168.10.0/24", "192.168.11.1", false},
        {"10.0.0.5/32", "10.0.0.5", true},          {"10.0.0.5", "10.0.0.5", true},
        {"10.0.0.5", "10.0.0.6", false},            {"172.16.0.0/12", "172.31.255.255", true},
        {"172.16.0.0/12", "172.32.0.1", false},     {"0.0.0.0/0", "8.8.8.8", true},
    };
    for (auto& c : cases) CHECK_EQ(allowed(CidrMatcher({c.cidr}), c.ip), c.expect);
}

TEST(net_cidr, ipv4_mapped_normalized) {
    CidrMatcher m({"192.168.1.0/24"});
    CHECK(allowed(m, "::ffff:192.168.1.5"));
    CHECK(!allowed(m, "::ffff:192.168.2.5"));
}

TEST(net_cidr, ipv6_ranges) {
    CidrMatcher m({"fd00::/8"});
    CHECK(allowed(m, "fd12:3456::1"));
    CHECK(!allowed(m, "fe80::1"));
    CHECK(!allowed(m, "10.0.0.1"));
}

TEST(net_cidr, loopback_not_implicit_when_list_nonempty) {
    CidrMatcher m({"10.255.0.0/24"});
    CHECK(!allowed(m, "127.0.0.1"));
    CHECK(allowed(m, "10.255.0.9"));
}

TEST(net_cidr, malformed_ignored_and_reported) {
    CidrMatcher m({"nonsense", "10.0.0.0/33", "192.168.1.0/24", "", "1.2.3.4/-1"});
    CHECK_EQ(m.count(), size_t(1));
    CHECK_EQ(m.invalid().size(), size_t(4));
    CHECK(allowed(m, "192.168.1.9"));
}

TEST(net_cidr, parse_ip_forms) {
    CHECK(parse_ip("[::1]").has_value());
    CHECK(parse_ip("fe80::1%en0").has_value());
    CHECK(!parse_ip("10.1").has_value());
    CHECK(!parse_ip("").has_value());
    CHECK_EQ(ip_to_string(*parse_ip("::ffff:10.0.0.1")), std::string("10.0.0.1"));
}

// ———————— ApiConfigTests ————————

TEST(net_config, defaults_disabled_loopback_only) {
    std::string err = "x";
    auto c = load_api_config(tmpdir() + "/nope/api.json", &err);
    CHECK(err.empty());
    CHECK(!c.enabled);
    CHECK(!c.file_found);
    CHECK_EQ(c.bind, std::string("0.0.0.0"));
    CHECK_EQ(c.http_port, 8080);
    CHECK(c.allowed_cidrs.empty());
    CHECK(c.tokens.empty());
    CHECK_EQ(c.max_clients, 4);
    CHECK(!c.serial.has_value());
    CHECK_EQ(c.heartbeat_timeout_sec, 10);
    CHECK_EQ(c.lease_grace_sec, 60);
    CHECK_EQ(c.on_remote_lost, std::string("keep"));
    CHECK_EQ(c.control_request_timeout_sec, 60);
    CHECK_EQ(c.write_rate_limit_per_sec, 10);
}

TEST(net_config, parses_schema_with_comments_and_trailing_commas) {
    std::string dir = tmpdir();
    std::string path = dir + "/api.json";
    {
        std::ofstream f(path);
        f << "{\n  // 注释与尾随逗号都允许\n  \"enabled\": true, \"bind\": \"0.0.0.0\", \"httpPort\": 18080,\n"
             "  \"allowedCidrs\": [\"192.168.10.0/24\"],\n"
             "  \"tokens\": [ { \"name\": \"PC-测试工位1\", \"sha256\": \"9F2C\", \"role\": \"Operator\", \"estop\": true } ],\n"
             "  \"maxClients\": 2, \"serial\": \"PR8-2026-0001\",\n}\n";
    }
    std::string err;
    auto c = load_api_config(path, &err);
    CHECK(err.empty());
    CHECK(c.enabled);
    CHECK(c.file_found);
    CHECK_EQ(c.source_path, path);
    CHECK_EQ(c.source_file_name(), std::string("api.json"));
    CHECK_EQ(c.http_port, 18080);
    CHECK_EQ(c.allowed_cidrs.size(), size_t(1));
    CHECK_EQ(c.tokens.size(), size_t(1));
    if (!c.tokens.empty()) {
        CHECK_EQ(c.tokens[0].role, std::string("operator"));
        CHECK(c.tokens[0].estop == std::optional<bool>(true));
        CHECK_EQ(c.tokens[0].name, std::string("PC-测试工位1"));
    }
    CHECK_EQ(c.max_clients, 2);
    CHECK(c.serial == std::optional<std::string>("PR8-2026-0001"));
    unlink(path.c_str());
    rmdir(dir.c_str());
}

TEST(net_config, corrupt_json_defaults_with_error) {
    std::string dir = tmpdir();
    std::string path = dir + "/api.json";
    { std::ofstream f(path); f << "{ not json"; }
    std::string err;
    auto c = load_api_config(path, &err);
    CHECK(!err.empty());
    CHECK(err.find("api.json") != std::string::npos);
    CHECK(err.find(dir) == std::string::npos);   // 只给文件名
    CHECK(!c.enabled);
    unlink(path.c_str());
    rmdir(dir.c_str());
}

TEST(net_config, fixups_and_all_fields) {
    std::string err;
    auto c = parse_api_config(R"({"ENABLED":true,"maxClients":0,"bind":" ","heartbeatTimeoutSec":-1,"leaseGraceSec":-5,
        "controlRequestTimeoutSec":0,"writeRateLimitPerSec":0,"onRemoteLost":"STOPHEAT","autoGrantRemote":true,
        "tokens":[{"name":"a","sha256":"x","role":"  "},{"name":"b","sha256":"y","estop":null}],"certPath":null,"httpsPort":8443})",
                              "api.json", &err);
    CHECK(err.empty());
    CHECK(c.enabled);   // 键名大小写不敏感
    CHECK_EQ(c.max_clients, 4);
    CHECK_EQ(c.bind, std::string("0.0.0.0"));
    CHECK_EQ(c.heartbeat_timeout_sec, 10);
    CHECK_EQ(c.lease_grace_sec, 60);
    CHECK_EQ(c.control_request_timeout_sec, 60);
    CHECK_EQ(c.write_rate_limit_per_sec, 10);
    CHECK_EQ(c.on_remote_lost, std::string("stopHeat"));
    CHECK(c.auto_grant_remote);
    CHECK_EQ(c.https_port, 8443);
    CHECK_EQ(c.tokens.size(), size_t(2));
    CHECK_EQ(c.tokens[0].role, std::string("viewer"));
    CHECK(!c.tokens[1].estop.has_value());
    CHECK(c.warnings.empty());
    auto c2 = parse_api_config(R"({"onRemoteLost":"explode"})", "api.json", &err);
    CHECK_EQ(c2.on_remote_lost, std::string("keep"));
    CHECK_EQ(c2.warnings.size(), size_t(1));
    auto c3 = parse_api_config(R"({"httpPort":"8080"})", "api.json", &err);   // 类型不对 → 默认 + 错误
    CHECK(!err.empty());
    CHECK_EQ(c3.http_port, 8080);
    CHECK(!c3.enabled);
}

TEST(net_config, apply_env_overrides) {
    std::map<std::string, std::string> env = {{"PR_API", "1"}, {"PR_API_PORT", "18080"}};
    auto get = [&](const char* k) -> const char* { auto it = env.find(k); return it == env.end() ? nullptr : it->second.c_str(); };
    ApiConfig c;
    apply_api_env(c, get);
    CHECK(c.enabled);
    CHECK_EQ(c.http_port, 18080);
    env = {{"PR_API", "0"}, {"PR_API_PORT", "notaport"}};
    ApiConfig c2;
    c2.enabled = true;
    c2.http_port = 9000;
    apply_api_env(c2, get);
    CHECK(!c2.enabled);
    CHECK_EQ(c2.http_port, 9000);
    env.clear();
    ApiConfig c3;
    c3.enabled = true;
    apply_api_env(c3, get);
    CHECK(c3.enabled);
    env = {{"PR_API_AUTOGRANT", "true"}};
    apply_api_env(c3, get);
    CHECK(c3.auto_grant_remote);
}

// ———————— IdempotencyStoreTests（时间：单调毫秒）————————

TEST(net_idem, new_replay_mismatch_inprogress) {
    IdempotencyStore s;
    int64_t t0 = 1000000;
    std::string h = IdempotencyStore::hash("POST", "/api/v1/run", "{}");
    IdemEntryPtr e, e2, e3, x;
    CHECK(s.begin("k1", h, "k1", t0, e) == IdemBegin::New);
    CHECK(s.begin("k1", h, "k1", t0 + 1000, e2) == IdemBegin::InProgress);
    CHECK(e == e2);
    CHECK(s.begin("k1", IdempotencyStore::hash("POST", "/api/v1/run", "{\"x\":1}"), "k1", t0, x) == IdemBegin::Mismatch);
    s.complete(e, 202, "{\"ok\":true}");
    CHECK(s.begin("k1", h, "k1", t0 + 9 * 60000, e3) == IdemBegin::Replay);
    CHECK_EQ(e3->status, 202);
    CHECK_EQ(e3->body, std::string("{\"ok\":true}"));
    CHECK(s.begin("k1", IdempotencyStore::hash("POST", "/api/v1/leak-tests", "{}"), "k1", t0, x) == IdemBegin::Mismatch);
}

TEST(net_idem, ttl_expires_after_10_minutes) {
    IdempotencyStore s;
    int64_t t0 = 1000000;
    std::string h = IdempotencyStore::hash("POST", "/x", "");
    IdemEntryPtr e, f;
    s.begin("k", h, "k", t0, e);
    s.complete(e, 200, "a");
    CHECK(s.begin("k", h, "k", t0 + 600000, f) == IdemBegin::Replay);
    CHECK(s.begin("k", h, "k", t0 + 601000, f) == IdemBegin::New);
    CHECK(!f->done);
}

TEST(net_idem, abandon_removes_entry) {
    IdempotencyStore s;
    std::string h = IdempotencyStore::hash("POST", "/x", "");
    IdemEntryPtr e, f;
    s.begin("k", h, "k", 0, e);
    s.abandon(e);
    CHECK_EQ(s.size(), size_t(0));
    CHECK(s.begin("k", h, "k", 0, f) == IdemBegin::New);
}

TEST(net_idem, capacity_evicts_oldest) {
    IdempotencyStore s;
    int64_t t0 = 1000000;
    for (size_t i = 0; i < kIdemCapacity + 10; ++i) {
        IdemEntryPtr e;
        s.begin("k" + std::to_string(i), "h", "k", t0 + static_cast<int64_t>(i), e);
        s.complete(e, 200, "");
    }
    CHECK(s.size() <= kIdemCapacity);
    IdemEntryPtr x;
    CHECK(s.begin("k0", "h", "k0", t0 + 5000, x) == IdemBegin::New);
    CHECK(s.begin("k" + std::to_string(kIdemCapacity + 9), "h", "x", t0 + 5000, x) == IdemBegin::Replay);
}

TEST(net_idem, waiter_released_on_complete_and_abandon) {
    IdempotencyStore s;
    IdemEntryPtr e;
    s.begin("k", "h", "k", 0, e);
    CHECK(!s.wait(e, 10));   // 仍在执行
    std::thread t([&] {
        std::this_thread::sleep_for(std::chrono::milliseconds(50));
        s.complete(e, 200, "done");
    });
    CHECK(s.wait(e, 2000));
    t.join();
    IdemEntryPtr e2, f;
    s.begin("k2", "h", "k2", 0, e2);
    s.abandon(e2);
    CHECK(s.wait(e2, 10));
    CHECK(e2->abandoned);
    CHECK(s.begin("k2", "h", "x", 0, f) == IdemBegin::New);
}

TEST(net_idem, scoped_key_isolates_tokens) {
    IdempotencyStore s;
    std::string h = IdempotencyStore::hash("POST", "/x", "{}");
    IdemEntryPtr e, x;
    s.begin(IdempotencyStore::scoped_key("tokA", "k"), h, "k", 0, e);
    s.complete(e, 200, "a");
    CHECK(s.begin(IdempotencyStore::scoped_key("tokB", "k"), h, "k", 0, x) == IdemBegin::New);
    CHECK(s.begin(IdempotencyStore::scoped_key("tokA", "k"), h, "k", 0, x) == IdemBegin::Replay);
}

TEST(net_idem, acquire_waits_then_replays_or_times_out) {
    IdempotencyStore s;
    auto a = s.acquire("k", "h", "op1", 0);
    CHECK(a.kind == IdemBegin::New);
    // 别人同键：原请求 100 ms 后完成 → 重放
    std::thread t([&] {
        std::this_thread::sleep_for(std::chrono::milliseconds(100));
        s.complete(a.entry, 202, "{\"ok\":true}");
    });
    auto b = s.acquire("k", "h", "op2", 0, 2000);
    t.join();
    CHECK(b.kind == IdemBegin::Replay);
    CHECK_EQ(b.entry->status, 202);
    // 进行中等满 → InProgress，op_id 是原请求的
    auto c = s.acquire("k3", "h", "orig", 0);
    auto d = s.acquire("k3", "h", "other", 0, 50);
    CHECK(d.kind == IdemBegin::InProgress);
    CHECK_EQ(d.entry->op_id, std::string("orig"));
    // 原请求撤销 → 等待方重新登记，成为新的执行者
    std::thread t2([&] {
        std::this_thread::sleep_for(std::chrono::milliseconds(50));
        s.abandon(c.entry);
    });
    auto e = s.acquire("k3", "h", "other", 0, 2000);
    t2.join();
    CHECK(e.kind == IdemBegin::New);
    CHECK_EQ(e.entry->op_id, std::string("other"));
}

// ———————— 限流 / 错误码 ————————

TEST(net_rate, fixed_window_and_concurrency) {
    FixedWindowLimiter l(3, 1000);
    CHECK(l.try_acquire("a", 0));
    CHECK(l.try_acquire("a", 10));
    CHECK(l.try_acquire("a", 20));
    CHECK(!l.try_acquire("a", 999));
    CHECK(l.try_acquire("b", 999));   // 分区独立
    CHECK(l.try_acquire("a", 1000));  // 新窗口
    ConcurrencyLimiter c(2);
    auto p1 = c.try_acquire();
    auto p2 = c.try_acquire();
    CHECK(p1 && p2);
    CHECK(!c.try_acquire());
    p1.reset();
    CHECK_EQ(c.in_use(), 1);
    auto p3 = c.try_acquire();
    CHECK(p3 != nullptr);
}

TEST(net_errors, status_table_and_body) {
    CHECK_EQ(status_of("BAD_REQUEST"), 400);
    CHECK_EQ(status_of("UNAUTHORIZED"), 401);
    CHECK_EQ(status_of("IP_NOT_ALLOWED"), 403);
    CHECK_EQ(status_of("NOT_EXPOSED"), 403);
    CHECK_EQ(status_of("CHANNEL_NOT_FOUND"), 404);
    CHECK_EQ(status_of("PROGRAM_INVALID"), 422);
    CHECK_EQ(status_of("CLIENT_TOO_OLD"), 426);
    CHECK_EQ(status_of("PRECONDITION_REQUIRED"), 428);
    CHECK_EQ(status_of("DOWNLOAD_BUSY"), 429);
    CHECK_EQ(status_of("HARDWARE_WRITE_FAILED"), 502);
    CHECK_EQ(status_of("HW_OFFLINE"), 503);
    CHECK_EQ(status_of("OUTCOME_UNKNOWN"), 504);
    CHECK_EQ(status_of("INTERNAL"), 500);
    CHECK_EQ(status_of("GAS_BUSY"), 409);
    CHECK_EQ(status_of("IDEMPOTENCY_MISMATCH"), 409);
    auto j = nlohmann::json::parse(error_body("GAS_BUSY", "气路正被「泄漏测试」占用", {{"gasTask", "泄漏测试"}}));
    CHECK(j["ok"] == false);
    CHECK(j["error"]["code"] == "GAS_BUSY");
    CHECK(j["error"]["detail"]["gasTask"] == "泄漏测试");
    auto j2 = nlohmann::json::parse(error_body("X", "m"));
    CHECK(!j2["error"].contains("detail"));
}
