// 鉴权：Bearer 令牌（只存 SHA-256、全部条目定时比较）、角色 viewer < operator < admin + 独立 estop 位，
// 以及按 IP 的鉴权失败限速和审计限频。C#: Api/TokenAuth.cs（TokenAuth / KeyedThrottle / FailureLimiter）。
// 时间一律由调用方传入（单调毫秒），便于测试。所有类线程安全。
#pragma once
#include <cstdint>
#include <mutex>
#include <optional>
#include <string>
#include <unordered_map>

#include "net/api_config.hpp"

namespace reactor::net {

enum class Role { Viewer = 0, Operator = 1, Admin = 2 };

// C#: TokenAuth.NormalizeRole —— 只认 operator / admin（大小写、空白不敏感），其余一律 viewer
Role parse_role(const std::string& s);
const char* role_name(Role r);

// 一次请求 / 一条 WS 连接的身份。
struct AuthContext {
    bool ok = false;      // 是否通过鉴权
    bool local = false;   // Unix socket 入口（本机触屏）：身份 = 「本地」
    std::string name;     // 令牌名；本地 = "local"
    Role role = Role::Viewer;
    bool estop = false;   // 停止类权限位（见 ApiToken::estop）

    bool has_role(Role r) const { return ok && static_cast<int>(role) >= static_cast<int>(r); }
    static AuthContext denied() { return AuthContext{}; }
    static AuthContext local_identity();   // ok, local, "local", admin, estop
};

// 从 Authorization 头取 Bearer 令牌（"Bearer " 前缀大小写不敏感，前后空白去掉）；格式不符返回 nullopt
std::optional<std::string> extract_bearer(const std::string& header);

// 令牌表的不可变快照（构造时预处理哈希）；authenticate 对全部条目都比较一遍，不因命中提前返回。
class TokenTable {
public:
    explicit TokenTable(const std::vector<ApiToken>& tokens);
    AuthContext authenticate_header(const std::string& authorization) const;
    AuthContext authenticate_token(const std::string& token) const;
    size_t size() const { return entries_.size(); }
    bool has_name(const std::string& name) const;

private:
    struct Entry { std::string name, hash_lower; Role role; bool estop; bool valid; };
    std::vector<Entry> entries_;
};

// 同一键在 window 内只放行一次（审计限频）。C#: KeyedThrottle
class KeyedThrottle {
public:
    explicit KeyedThrottle(int64_t window_ms) : window_ms_(window_ms) {}
    bool allow(const std::string& key, int64_t now_ms);

private:
    int64_t window_ms_;
    std::mutex mu_;
    std::unordered_map<std::string, int64_t> last_;
};

// 按键的固定窗口失败计数：window 内最多 limit 次。C#: FailureLimiter（每 IP 每秒 10 次）
class FailureLimiter {
public:
    FailureLimiter(int limit, int64_t window_ms) : limit_(limit), window_ms_(window_ms) {}
    bool is_blocked(const std::string& key, int64_t now_ms);
    bool record_failure(const std::string& key, int64_t now_ms);   // 返回记完后是否超限

private:
    int limit_;
    int64_t window_ms_;
    std::mutex mu_;
    std::unordered_map<std::string, std::pair<int64_t, int>> m_;
};

}  // namespace reactor::net
