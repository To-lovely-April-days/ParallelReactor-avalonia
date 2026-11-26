#include "net/auth.hpp"

#include <cctype>

#include "net/crypto.hpp"
#include <cstdint>
#include <iterator>
#include <mutex>
#include <optional>
#include <string>
#include <vector>

namespace reactor::net {

namespace {
std::string trim_lower(const std::string& s) {
    size_t a = 0, b = s.size();
    while (a < b && std::isspace(static_cast<unsigned char>(s[a]))) ++a;
    while (b > a && std::isspace(static_cast<unsigned char>(s[b - 1]))) --b;
    std::string r = s.substr(a, b - a);
    for (auto& c : r) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return r;
}
std::string trim(const std::string& s) {
    size_t a = 0, b = s.size();
    while (a < b && std::isspace(static_cast<unsigned char>(s[a]))) ++a;
    while (b > a && std::isspace(static_cast<unsigned char>(s[b - 1]))) --b;
    return s.substr(a, b - a);
}
}  // namespace

Role parse_role(const std::string& s) {
    std::string r = trim_lower(s);
    if (r == "operator") return Role::Operator;
    if (r == "admin") return Role::Admin;
    return Role::Viewer;
}

const char* role_name(Role r) {
    switch (r) {
        case Role::Admin: return "admin";
        case Role::Operator: return "operator";
        default: return "viewer";
    }
}

AuthContext AuthContext::local_identity() {
    AuthContext a;
    a.ok = true;
    a.local = true;
    a.name = "local";
    a.role = Role::Admin;
    a.estop = true;
    return a;
}

std::optional<std::string> extract_bearer(const std::string& header) {
    std::string h = trim(header);
    if (h.size() < 7) return std::nullopt;
    if (trim_lower(h.substr(0, 6)) != "bearer" || h[6] != ' ') return std::nullopt;
    std::string tok = trim(h.substr(7));
    if (tok.empty()) return std::nullopt;
    return tok;
}

TokenTable::TokenTable(const std::vector<ApiToken>& tokens) {
    for (auto& t : tokens) {
        Entry e;
        e.name = t.name;
        std::string h = trim_lower(t.sha256);
        // C#: 空白或长度 != 64 的条目跳过（C# 先判 Length 再 Trim，这里一并 trim）
        e.valid = h.size() == 64;
        e.hash_lower = h;
        e.role = parse_role(t.role);
        // C#: Estop ?? role is operator or admin
        e.estop = t.estop.has_value() ? *t.estop : (e.role == Role::Operator || e.role == Role::Admin);
        entries_.push_back(std::move(e));
    }
}

AuthContext TokenTable::authenticate_header(const std::string& authorization) const {
    auto tok = extract_bearer(authorization);
    if (!tok) return AuthContext::denied();
    return authenticate_token(*tok);
}

AuthContext TokenTable::authenticate_token(const std::string& token) const {
    std::string hash = sha256_hex(token);
    const Entry* hit = nullptr;
    for (auto& e : entries_) {
        if (!e.valid) continue;
        bool eq = constant_time_equal(hash, e.hash_lower);   // 每条都比，不提前返回
        if (eq && !hit) hit = &e;
    }
    if (!hit) return AuthContext::denied();
    AuthContext a;
    a.ok = true;
    a.name = hit->name;
    a.role = hit->role;
    a.estop = hit->estop;
    return a;
}

bool TokenTable::has_name(const std::string& name) const {
    for (auto& e : entries_)
        if (e.valid && e.name == name) return true;
    return false;
}

bool KeyedThrottle::allow(const std::string& key, int64_t now_ms) {
    std::lock_guard<std::mutex> lk(mu_);
    auto it = last_.find(key);
    if (it == last_.end()) {
        if (last_.size() >= 1000)
            for (auto i = last_.begin(); i != last_.end();)
                i = now_ms - i->second > window_ms_ * 2 ? last_.erase(i) : std::next(i);
        last_[key] = now_ms;
        return true;
    }
    if (now_ms - it->second < window_ms_) return false;
    it->second = now_ms;
    return true;
}

bool FailureLimiter::is_blocked(const std::string& key, int64_t now_ms) {
    std::lock_guard<std::mutex> lk(mu_);
    auto it = m_.find(key);
    return it != m_.end() && now_ms - it->second.first < window_ms_ && it->second.second >= limit_;
}

bool FailureLimiter::record_failure(const std::string& key, int64_t now_ms) {
    std::lock_guard<std::mutex> lk(mu_);
    auto it = m_.find(key);
    int count;
    if (it == m_.end() || now_ms - it->second.first >= window_ms_) {
        m_[key] = {now_ms, 1};
        count = 1;
    } else {
        count = ++it->second.second;
    }
    if (m_.size() > 1000)
        for (auto i = m_.begin(); i != m_.end();)
            i = now_ms - i->second.first > window_ms_ * 2 ? m_.erase(i) : std::next(i);
    return count >= limit_;
}

}  // namespace reactor::net
