#include "net/api_config.hpp"

#include <algorithm>
#include <cctype>
#include <cstdint>
#include <fstream>
#include <sstream>
#include <stdexcept>

#include "nlohmann/json.hpp"
#include <functional>
#include <optional>
#include <string>

namespace reactor::net {

using nlohmann::json;

namespace {

std::string lower(std::string s) {
    for (auto& c : s) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return s;
}

std::string trim(const std::string& s) {
    size_t a = 0, b = s.size();
    while (a < b && std::isspace(static_cast<unsigned char>(s[a]))) ++a;
    while (b > a && std::isspace(static_cast<unsigned char>(s[b - 1]))) --b;
    return s.substr(a, b - a);
}

std::string basename_of(const std::string& p) {
    auto k = p.find_last_of('/');
    return k == std::string::npos ? p : p.substr(k + 1);
}

// nlohmann 3.11 不认尾逗号：先去掉 「, (空白/注释)* } 或 ]」 里的逗号（跳过字符串和注释）
std::string strip_trailing_commas(const std::string& s) {
    std::string out;
    out.reserve(s.size());
    size_t i = 0, n = s.size();
    auto skip_ws_comments = [&](size_t j) {
        while (j < n) {
            if (std::isspace(static_cast<unsigned char>(s[j]))) { ++j; continue; }
            if (s[j] == '/' && j + 1 < n && s[j + 1] == '/') { while (j < n && s[j] != '\n') ++j; continue; }
            if (s[j] == '/' && j + 1 < n && s[j + 1] == '*') {
                j += 2;
                while (j + 1 < n && !(s[j] == '*' && s[j + 1] == '/')) ++j;
                j = std::min(n, j + 2);
                continue;
            }
            break;
        }
        return j;
    };
    while (i < n) {
        char c = s[i];
        if (c == '"') {
            out += c; ++i;
            while (i < n) {
                out += s[i];
                if (s[i] == '\\' && i + 1 < n) { out += s[i + 1]; i += 2; continue; }
                if (s[i++] == '"') break;
            }
            continue;
        }
        if (c == '/' && i + 1 < n && (s[i + 1] == '/' || s[i + 1] == '*')) {
            size_t j = skip_ws_comments(i);
            out.append(s, i, j - i);
            i = j;
            continue;
        }
        if (c == ',') {
            size_t j = skip_ws_comments(i + 1);
            if (j < n && (s[j] == '}' || s[j] == ']')) { ++i; continue; }
        }
        out += c; ++i;
    }
    return out;
}

// 大小写不敏感地取键（C#: PropertyNameCaseInsensitive）
const json* find_ci(const json& obj, const char* key) {
    if (!obj.is_object()) return nullptr;
    auto it = obj.find(key);
    if (it != obj.end()) return &*it;
    std::string want = lower(key);
    for (auto i = obj.begin(); i != obj.end(); ++i)
        if (lower(i.key()) == want) return &i.value();
    return nullptr;
}

[[noreturn]] void type_error(const char* key, const char* want) {
    throw std::runtime_error(std::string("字段 ") + key + " 应为" + want);
}

void get_bool(const json& o, const char* key, bool& out) {
    const json* v = find_ci(o, key);
    if (!v || v->is_null()) return;
    if (!v->is_boolean()) type_error(key, "布尔值");
    out = v->get<bool>();
}

void get_int(const json& o, const char* key, int& out) {
    const json* v = find_ci(o, key);
    if (!v || v->is_null()) return;
    if (!v->is_number_integer()) type_error(key, "整数");
    auto x = v->get<int64_t>();
    if (x < INT32_MIN || x > INT32_MAX) type_error(key, "32 位整数");
    out = static_cast<int>(x);
}

void get_str(const json& o, const char* key, std::string& out) {
    const json* v = find_ci(o, key);
    if (!v || v->is_null()) return;
    if (!v->is_string()) type_error(key, "字符串");
    out = v->get<std::string>();
}

void get_opt_str(const json& o, const char* key, std::optional<std::string>& out) {
    const json* v = find_ci(o, key);
    if (!v || v->is_null()) { out.reset(); return; }
    if (!v->is_string()) type_error(key, "字符串");
    out = v->get<std::string>();
}

}  // namespace

std::string ApiConfig::source_file_name() const {
    return source_path.empty() ? std::string("api.json") : basename_of(source_path);
}

ApiConfig parse_api_config(const std::string& text, const std::string& source_name, std::string* error) {
    if (error) error->clear();
    ApiConfig cfg;
    try {
        json root = json::parse(strip_trailing_commas(text), nullptr, true, /*ignore_comments=*/true);
        if (root.is_null()) {
            if (error) *error = source_name + "：内容为空";
            return ApiConfig{};
        }
        if (!root.is_object()) throw std::runtime_error("顶层应为对象");
        get_bool(root, "enabled", cfg.enabled);
        get_str(root, "bind", cfg.bind);
        get_int(root, "httpPort", cfg.http_port);
        get_int(root, "httpsPort", cfg.https_port);
        get_opt_str(root, "certPath", cfg.cert_path);
        if (const json* v = find_ci(root, "allowedCidrs"); v && !v->is_null()) {
            if (!v->is_array()) type_error("allowedCidrs", "字符串数组");
            for (auto& e : *v) {
                if (e.is_null()) continue;
                if (!e.is_string()) type_error("allowedCidrs", "字符串数组");
                cfg.allowed_cidrs.push_back(e.get<std::string>());
            }
        }
        if (const json* v = find_ci(root, "tokens"); v && !v->is_null()) {
            if (!v->is_array()) type_error("tokens", "数组");
            for (auto& e : *v) {
                if (e.is_null()) continue;
                if (!e.is_object()) type_error("tokens", "对象数组");
                ApiToken t;
                get_str(e, "name", t.name);
                get_str(e, "sha256", t.sha256);
                std::string role;
                get_str(e, "role", role);
                t.role = trim(role).empty() ? "viewer" : lower(trim(role));
                if (const json* es = find_ci(e, "estop"); es && !es->is_null()) {
                    if (!es->is_boolean()) type_error("estop", "布尔值或 null");
                    t.estop = es->get<bool>();
                }
                cfg.tokens.push_back(std::move(t));
            }
        }
        get_int(root, "heartbeatTimeoutSec", cfg.heartbeat_timeout_sec);
        get_int(root, "leaseGraceSec", cfg.lease_grace_sec);
        std::string policy = cfg.on_remote_lost;
        if (const json* v = find_ci(root, "onRemoteLost"); v && v->is_null()) policy.clear();
        else get_str(root, "onRemoteLost", policy);
        get_bool(root, "autoGrantRemote", cfg.auto_grant_remote);
        get_int(root, "controlRequestTimeoutSec", cfg.control_request_timeout_sec);
        get_int(root, "writeRateLimitPerSec", cfg.write_rate_limit_per_sec);
        get_int(root, "maxClients", cfg.max_clients);
        get_opt_str(root, "serial", cfg.serial);

        // 修正规则 —— C#: ApiConfig.Load
        if (cfg.max_clients <= 0) cfg.max_clients = 4;
        if (trim(cfg.bind).empty()) cfg.bind = "0.0.0.0";
        if (cfg.heartbeat_timeout_sec <= 0) cfg.heartbeat_timeout_sec = 10;
        if (cfg.lease_grace_sec < 0) cfg.lease_grace_sec = 60;
        if (cfg.control_request_timeout_sec <= 0) cfg.control_request_timeout_sec = 60;
        if (cfg.write_rate_limit_per_sec <= 0) cfg.write_rate_limit_per_sec = 10;
        static const char* kPolicies[] = {"keep", "stopHeat", "estop"};
        const char* found = nullptr;
        for (auto p : kPolicies)
            if (lower(p) == lower(trim(policy))) found = p;
        if (!found) {
            cfg.warnings.push_back("onRemoteLost「" + policy + "」不是 keep / stopHeat / estop · 按 keep");
            found = "keep";
        }
        cfg.on_remote_lost = found;
        cfg.file_found = true;
        return cfg;
    } catch (const std::exception& ex) {
        if (error) *error = source_name + "：" + ex.what();
        return ApiConfig{};
    }
}

ApiConfig load_api_config(const std::string& path, std::string* error) {
    if (error) error->clear();
    std::ifstream f(path, std::ios::binary);
    if (!f) {
        ApiConfig c;
        c.source_path = path;
        return c;
    }
    std::ostringstream ss;
    ss << f.rdbuf();
    ApiConfig c = parse_api_config(ss.str(), basename_of(path), error);
    c.source_path = path;
    if (error && !error->empty()) c.file_found = false;
    return c;
}

void apply_api_env(ApiConfig& cfg, const std::function<const char*(const char*)>& getenv_fn) {
    auto get = [&](const char* k) { const char* v = getenv_fn(k); return v ? trim(v) : std::string(); };
    std::string api = get("PR_API");
    if (api == "1" || api == "true") cfg.enabled = true;
    else if (api == "0" || api == "false") cfg.enabled = false;
    std::string port = get("PR_API_PORT");
    if (!port.empty() && port.size() <= 5 && std::all_of(port.begin(), port.end(), [](char c) { return c >= '0' && c <= '9'; })) {
        int p = std::stoi(port);
        if (p >= 0 && p <= 65535) cfg.http_port = p;
    }
    std::string ag = get("PR_API_AUTOGRANT");
    if (ag == "1" || ag == "true") cfg.auto_grant_remote = true;
}

}  // namespace reactor::net
