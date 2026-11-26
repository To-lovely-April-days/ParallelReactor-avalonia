#include "api/api_util.hpp"

#include <cmath>
#include <ctime>
#include <limits>

#include "core/program/program_step_format.hpp"
#include "net/api_errors.hpp"
#include "store/store_util.hpp"
#include <cstdint>
#include <optional>
#include <string>
#include <vector>

namespace reactor::api {

std::string strip_trailing_commas(const std::string& t) {
    std::string out;
    out.reserve(t.size());
    bool in_str = false, esc = false;
    for (size_t i = 0; i < t.size(); ++i) {
        char c = t[i];
        if (in_str) {
            out += c;
            if (esc) esc = false;
            else if (c == '\\') esc = true;
            else if (c == '"') in_str = false;
            continue;
        }
        if (c == '"') { in_str = true; out += c; continue; }
        if (c == '/' && i + 1 < t.size() && t[i + 1] == '/') {   // 行注释原样保留（nlohmann 负责跳过）
            size_t e = t.find('\n', i);
            if (e == std::string::npos) e = t.size();
            out.append(t, i, e - i);
            i = e - 1;
            continue;
        }
        if (c == '/' && i + 1 < t.size() && t[i + 1] == '*') {
            size_t e = t.find("*/", i + 2);
            e = e == std::string::npos ? t.size() : e + 2;
            out.append(t, i, e - i);
            i = e - 1;
            continue;
        }
        if (c == ',') {
            size_t j = i + 1;
            while (j < t.size()) {
                if (t[j] == ' ' || t[j] == '\t' || t[j] == '\r' || t[j] == '\n') { ++j; continue; }
                if (t[j] == '/' && j + 1 < t.size() && t[j + 1] == '/') {
                    size_t e = t.find('\n', j);
                    j = e == std::string::npos ? t.size() : e;
                    continue;
                }
                if (t[j] == '/' && j + 1 < t.size() && t[j + 1] == '*') {
                    size_t e = t.find("*/", j + 2);
                    j = e == std::string::npos ? t.size() : e + 2;
                    continue;
                }
                break;
            }
            if (j < t.size() && (t[j] == '}' || t[j] == ']')) continue;   // 尾逗号：丢掉
        }
        out += c;
    }
    return out;
}

ParsedBody parse_body(const std::string& text) {
    ParsedBody r;
    bool blank = true;
    for (char c : text)
        if (!(c == ' ' || c == '\t' || c == '\r' || c == '\n')) { blank = false; break; }
    if (blank) return r;
    try {
        r.value = nlohmann::json::parse(strip_trailing_commas(text), nullptr, true, true);
    } catch (const std::exception& e) {
        r.error = std::string("正文不是合法 JSON：") + e.what();
        return r;
    }
    if (!r.value.is_object()) {
        r.error = "正文必须是 JSON 对象";
        r.value = nullptr;
        return r;
    }
    r.has = true;
    return r;
}

const nlohmann::json* Params::prop(const char* name) const {
    if (!b_ || !b_->is_object()) return nullptr;
    auto it = b_->find(name);
    if (it == b_->end() || it->is_null()) return nullptr;
    return &*it;
}

std::optional<double> Params::num(const char* name) {
    auto v = prop(name);
    if (!v) return std::nullopt;
    if (!v->is_number()) { fail(std::string(name) + " 须为数值"); return std::nullopt; }
    double d = v->get<double>();
    if (!std::isfinite(d)) { fail(std::string(name) + " 须为数值"); return std::nullopt; }
    return d;
}

std::optional<bool> Params::boolean(const char* name) {
    auto v = prop(name);
    if (!v) return std::nullopt;
    if (!v->is_boolean()) { fail(std::string(name) + " 须为 true / false"); return std::nullopt; }
    return v->get<bool>();
}

std::optional<std::string> Params::str(const char* name) {
    auto v = prop(name);
    if (!v) return std::nullopt;
    if (!v->is_string()) { fail(std::string(name) + " 须为字符串"); return std::nullopt; }
    return v->get<std::string>();
}

namespace {
bool as_int32(const nlohmann::json& v, int& out) {
    if (v.is_number_integer()) {
        if (v.is_number_unsigned()) {
            auto u = v.get<uint64_t>();
            if (u > static_cast<uint64_t>(std::numeric_limits<int>::max())) return false;
            out = static_cast<int>(u);
            return true;
        }
        auto i = v.get<int64_t>();
        if (i < std::numeric_limits<int>::min() || i > std::numeric_limits<int>::max()) return false;
        out = static_cast<int>(i);
        return true;
    }
    return false;   // 1.0 这种 .NET TryGetInt32 同样不收
}
}  // namespace

std::optional<int> Params::opt_int(const char* name) {
    auto v = prop(name);
    if (!v) return std::nullopt;
    int n = 0;
    if (!as_int32(*v, n)) { fail(std::string(name) + " 须为整数"); return std::nullopt; }
    return n;
}

std::optional<std::int64_t> Params::opt_int64(const char* name) {
    auto v = prop(name);
    if (!v) return std::nullopt;
    if (!v->is_number_integer()) { fail(std::string(name) + " 须为整数"); return std::nullopt; }
    return v->get<int64_t>();
}

std::optional<std::vector<int>> Params::int_list(const char* name, bool required) {
    auto v = prop(name);
    if (!v) {
        if (required) fail(std::string("缺少 ") + name + "（整数数组）");
        return std::nullopt;
    }
    if (!v->is_array()) { fail(std::string(name) + " 须为整数数组"); return std::nullopt; }
    std::vector<int> out;
    for (const auto& x : *v) {
        int n = 0;
        if (!as_int32(x, n)) { fail(std::string(name) + " 须为整数数组"); return std::nullopt; }
        out.push_back(n);
    }
    return out;
}

std::optional<std::string> Params::note(const char* name) {
    auto v = str(name);
    if (!v) return std::nullopt;
    std::string s = *v;
    for (auto& c : s)
        if (c == '\r' || c == '\n') c = ' ';
    return truncate_note(trim(s), 200);
}

std::optional<std::string> Params::require(const char* name) {
    auto v = prop(name);
    if (!v) { fail(std::string("缺少 ") + name); return std::nullopt; }
    return v->is_string() ? v->get<std::string>() : v->dump();
}

std::string trim(const std::string& s) {
    size_t a = 0, b = s.size();
    while (a < b && (s[a] == ' ' || s[a] == '\t' || s[a] == '\r' || s[a] == '\n')) ++a;
    while (b > a && (s[b - 1] == ' ' || s[b - 1] == '\t' || s[b - 1] == '\r' || s[b - 1] == '\n')) --b;
    return s.substr(a, b - a);
}

size_t utf16_len(const std::string& s) {
    size_t n = 0;
    for (unsigned char c : s) {
        if ((c & 0xC0) == 0x80) continue;
        n += c >= 0xF0 ? 2 : 1;
    }
    return n;
}

std::string truncate_note(const std::string& s, size_t max_units) {
    size_t n = 0, i = 0;
    while (i < s.size()) {
        unsigned char c = static_cast<unsigned char>(s[i]);
        size_t len = c < 0x80 ? 1 : c < 0xE0 ? 2 : c < 0xF0 ? 3 : 4;
        size_t units = c >= 0xF0 ? 2 : 1;
        if (n + units > max_units) break;
        n += units;
        i += len;
    }
    return s.substr(0, i);
}

std::string utc_iso(int64_t ms) { return store::fmt_utc_json(ms); }
ojson jnum(double v) { return core::program::json_number(v); }
ojson opt_num(const std::optional<double>& v) { return v ? jnum(*v) : ojson(nullptr); }
ojson opt_str(const std::optional<std::string>& v) { return v ? ojson(*v) : ojson(nullptr); }
ojson opt_utc(const std::optional<int64_t>& ms) { return ms ? ojson(utc_iso(*ms)) : ojson(nullptr); }
std::string dump(const ojson& j) { return store::dump_json(j); }

std::optional<int64_t> parse_local_stamp(const std::string& s) {
    // yyyyMMdd-HHmmss
    if (s.size() != 15 || s[8] != '-') return std::nullopt;
    for (size_t i = 0; i < 15; ++i)
        if (i != 8 && (s[i] < '0' || s[i] > '9')) return std::nullopt;
    std::tm t{};
    t.tm_year = std::stoi(s.substr(0, 4)) - 1900;
    t.tm_mon = std::stoi(s.substr(4, 2)) - 1;
    t.tm_mday = std::stoi(s.substr(6, 2));
    t.tm_hour = std::stoi(s.substr(9, 2));
    t.tm_min = std::stoi(s.substr(11, 2));
    t.tm_sec = std::stoi(s.substr(13, 2));
    t.tm_isdst = -1;
    if (t.tm_mon < 0 || t.tm_mon > 11 || t.tm_mday < 1 || t.tm_mday > 31 || t.tm_hour > 23 || t.tm_min > 59 || t.tm_sec > 59)
        return std::nullopt;
    std::time_t tt = std::mktime(&t);
    if (tt == static_cast<std::time_t>(-1)) return std::nullopt;
    return static_cast<int64_t>(tt) * 1000;
}

std::optional<int> parse_channel(const std::string& s) {
    if (s.empty() || s.size() > 9) return std::nullopt;
    size_t i = 0;
    bool neg = false;
    if (s[0] == '+' || s[0] == '-') { neg = s[0] == '-'; i = 1; }
    if (i >= s.size()) return std::nullopt;
    int v = 0;
    for (; i < s.size(); ++i) {
        if (s[i] < '0' || s[i] > '9') return std::nullopt;
        v = v * 10 + (s[i] - '0');
    }
    if (neg) v = -v;
    if (v < 1 || v > 8) return std::nullopt;
    return v;
}

// ===================== 写命令信封 =====================

CommandHttp error_http(const std::string& op_id, const std::string& code, const std::string& message, const ojson& detail, int64_t seq) {
    ojson j;
    j["ok"] = false;
    j["opId"] = op_id;
    j["error"] = ojson{{"code", code}, {"message", message}, {"detail", detail}};
    j["opEventSeq"] = seq;
    CommandHttp h;
    h.status = net::status_of(code);
    h.body = dump(j);
    h.ok = false;
    h.word = "rejected";
    h.code = code;
    h.message = message;
    return h;
}

namespace {
CommandHttp success(int status, const std::string& word, const std::string& op_id, const ojson& data, const std::vector<std::string>& w,
                    int64_t seq, const std::string& message) {
    ojson warn = ojson::array();
    for (const auto& s : w) {
        bool dup = false;
        for (const auto& x : warn) dup = dup || x.get<std::string>() == s;
        if (!dup) warn.push_back(s);
    }
    ojson j;
    j["ok"] = true;
    j["opId"] = op_id;
    j["status"] = word;
    j["result"] = data;
    j["warnings"] = warn;
    j["opEventSeq"] = seq;
    CommandHttp h;
    h.status = status;
    h.body = dump(j);
    h.ok = true;
    h.word = word;
    h.code = "OK";
    h.message = message;
    return h;
}
}  // namespace

CommandHttp result_http(const ctl::OpResult& r, const std::string& op_id, int64_t seq) {
    if (!r.ok) return error_http(op_id, r.code, r.message, r.data, seq);
    return r.accepted ? success(202, "accepted", op_id, r.data, r.warnings, seq, r.message)
                      : success(200, "confirmed", op_id, r.data, r.warnings, seq, r.message);
}

CommandHttp unconfirmed_http(const std::string& op_id, int64_t seq) {
    return success(202, "accepted", op_id, nullptr, {"HW_UNCONFIRMED"}, seq,
                   "已下发，硬件在 2 s 内尚未确认 · 最终结果见带同一 opId 的 op.*.result 事件");
}

std::string summary(const CommandHttp& h) {
    return std::to_string(h.status) + " " + (h.ok ? h.word : h.code);
}

net::Response json_response(int status, const std::string& body) { return net::Response::json(status, body); }
net::Response ok_json(const ojson& j) { return net::Response::json(200, dump(j)); }

net::Response api_error(const std::string& code, const std::string& message, const ojson& detail) {
    ojson j;
    j["ok"] = false;
    j["error"] = ojson{{"code", code}, {"message", message}, {"detail", detail}};
    auto r = net::Response::json(net::status_of(code), dump(j));
    if (code == "RATE_LIMITED" || code == "DOWNLOAD_BUSY") r.headers.set("Retry-After", "1");
    return r;
}

}  // namespace reactor::api
