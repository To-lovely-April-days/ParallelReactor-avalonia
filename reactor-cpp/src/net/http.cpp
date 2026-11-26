#include "net/http.hpp"

#include <fcntl.h>
#include <sys/stat.h>
#include <unistd.h>

#include <cctype>
#include <cstdio>
#include <cstring>
#include <ctime>

#include "net/api_errors.hpp"
#include <algorithm>
#include <cstdint>
#include <iterator>
#include <memory>
#include <optional>
#include <string>
#include <utility>
#include <vector>

namespace reactor::net {

// ———————————————————————————— 头 ————————————————————————————

bool iequals(const std::string& a, const std::string& b) {
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i)
        if (std::tolower(static_cast<unsigned char>(a[i])) != std::tolower(static_cast<unsigned char>(b[i]))) return false;
    return true;
}

namespace {
std::string trim_ows(const std::string& s) {
    size_t a = 0, b = s.size();
    while (a < b && (s[a] == ' ' || s[a] == '\t')) ++a;
    while (b > a && (s[b - 1] == ' ' || s[b - 1] == '\t')) --b;
    return s.substr(a, b - a);
}
bool is_tchar(char c) {
    if (std::isalnum(static_cast<unsigned char>(c))) return true;
    return std::strchr("!#$%&'*+-.^_`|~", c) != nullptr && c != 0;
}
}  // namespace

void Headers::add(std::string name, std::string value) { items_.emplace_back(std::move(name), std::move(value)); }

void Headers::set(const std::string& name, std::string value) {
    remove(name);
    items_.emplace_back(name, std::move(value));
}

void Headers::remove(const std::string& name) {
    for (auto it = items_.begin(); it != items_.end();)
        it = iequals(it->first, name) ? items_.erase(it) : std::next(it);
}

const std::string* Headers::find(const std::string& name) const {
    for (auto& kv : items_)
        if (iequals(kv.first, name)) return &kv.second;
    return nullptr;
}

std::string Headers::get(const std::string& name, const std::string& def) const {
    const std::string* v = find(name);
    return v ? *v : def;
}

bool Headers::has_token(const std::string& name, const std::string& token) const {
    for (auto& kv : items_) {
        if (!iequals(kv.first, name)) continue;
        size_t start = 0;
        const std::string& v = kv.second;
        while (start <= v.size()) {
            size_t comma = v.find(',', start);
            if (comma == std::string::npos) comma = v.size();
            if (iequals(trim_ows(v.substr(start, comma - start)), token)) return true;
            start = comma + 1;
        }
    }
    return false;
}

// ———————————————————————————— 请求 / 响应 ————————————————————————————

std::string Request::param(const std::string& name, const std::string& def) const {
    auto it = params.find(name);
    return it == params.end() ? def : it->second;
}

bool Request::has_query(const std::string& name) const {
    for (auto& kv : query)
        if (kv.first == name) return true;
    return false;
}

std::string Request::query_param(const std::string& name, const std::string& def) const {
    for (auto& kv : query)
        if (kv.first == name) return kv.second;
    return def;
}

bool Request::keep_alive() const {
    if (headers.has_token("Connection", "close")) return false;
    if (version_minor == 0) return headers.has_token("Connection", "keep-alive");
    return true;
}

FileBody::~FileBody() {
    if (fd >= 0) ::close(fd);
}

Response Response::json(int status, std::string body) {
    Response r;
    r.status = status;
    r.headers.set("Content-Type", kJsonContentType);
    r.body = std::move(body);
    return r;
}

Response Response::json(int status, const nlohmann::json& j) {
    return json(status, j.dump(-1, ' ', false, nlohmann::json::error_handler_t::replace));
}

Response Response::text(int status, std::string body, const std::string& content_type) {
    Response r;
    r.status = status;
    r.headers.set("Content-Type", content_type);
    r.body = std::move(body);
    return r;
}

Response Response::empty(int status) {
    Response r;
    r.status = status;
    return r;
}

Response Response::error(const std::string& code, const std::string& message, const nlohmann::json& detail,
                         int status_override) {
    Response r = json(status_override ? status_override : status_of(code), error_body(code, message, detail));
    if (code == codes::kRateLimited || code == codes::kDownloadBusy) r.headers.set("Retry-After", "1");
    return r;
}

const char* status_text(int s) {
    switch (s) {
        case 100: return "Continue";
        case 101: return "Switching Protocols";
        case 200: return "OK";
        case 201: return "Created";
        case 202: return "Accepted";
        case 204: return "No Content";
        case 206: return "Partial Content";
        case 304: return "Not Modified";
        case 400: return "Bad Request";
        case 401: return "Unauthorized";
        case 403: return "Forbidden";
        case 404: return "Not Found";
        case 405: return "Method Not Allowed";
        case 408: return "Request Timeout";
        case 409: return "Conflict";
        case 411: return "Length Required";
        case 413: return "Payload Too Large";
        case 416: return "Range Not Satisfiable";
        case 422: return "Unprocessable Entity";
        case 426: return "Upgrade Required";
        case 428: return "Precondition Required";
        case 429: return "Too Many Requests";
        case 431: return "Request Header Fields Too Large";
        case 500: return "Internal Server Error";
        case 501: return "Not Implemented";
        case 502: return "Bad Gateway";
        case 503: return "Service Unavailable";
        case 504: return "Gateway Timeout";
        case 505: return "HTTP Version Not Supported";
        default: return "Unknown";
    }
}

namespace {
const char* kDays[] = {"Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"};
const char* kMonths[] = {"Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"};
}  // namespace

std::string http_date(int64_t unix_sec) {
    time_t t = static_cast<time_t>(unix_sec);
    struct tm g{};
    gmtime_r(&t, &g);
    char buf[64];
    std::snprintf(buf, sizeof buf, "%s, %02d %s %04d %02d:%02d:%02d GMT", kDays[g.tm_wday % 7], g.tm_mday,
                  kMonths[g.tm_mon % 12], g.tm_year + 1900, g.tm_hour, g.tm_min, g.tm_sec);
    return buf;
}

std::optional<int64_t> parse_http_date(const std::string& s) {
    // 只认 RFC 1123（IMF-fixdate），这也是唯一应当生成的格式
    char wd[4] = {0}, mon[4] = {0}, gmt[4] = {0};
    int d, y, h, mi, se;
    if (std::sscanf(s.c_str(), "%3s, %d %3s %d %d:%d:%d %3s", wd, &d, mon, &y, &h, &mi, &se, gmt) != 8) return std::nullopt;
    int m = -1;
    for (int i = 0; i < 12; ++i)
        if (std::strcmp(mon, kMonths[i]) == 0) m = i;
    if (m < 0 || std::strcmp(gmt, "GMT") != 0) return std::nullopt;
    struct tm g{};
    g.tm_year = y - 1900; g.tm_mon = m; g.tm_mday = d; g.tm_hour = h; g.tm_min = mi; g.tm_sec = se;
    return static_cast<int64_t>(timegm(&g));
}

// ———————————————————————————— URL ————————————————————————————

std::string url_decode(const std::string& s, bool plus_as_space) {
    std::string out;
    out.reserve(s.size());
    auto hex = [](char c) -> int {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    };
    for (size_t i = 0; i < s.size(); ++i) {
        char c = s[i];
        if (c == '%' && i + 2 < s.size()) {
            int h1 = hex(s[i + 1]), h2 = hex(s[i + 2]);
            if (h1 >= 0 && h2 >= 0) {
                out += static_cast<char>(h1 * 16 + h2);
                i += 2;
                continue;
            }
        }
        out += (plus_as_space && c == '+') ? ' ' : c;
    }
    return out;
}

std::string url_encode_component(const std::string& s) {
    static const char* hx = "0123456789ABCDEF";
    std::string out;
    for (unsigned char c : s) {
        if (std::isalnum(c) || c == '-' || c == '.' || c == '_' || c == '~') out += static_cast<char>(c);
        else { out += '%'; out += hx[c >> 4]; out += hx[c & 15]; }
    }
    return out;
}

std::vector<std::pair<std::string, std::string>> parse_query(const std::string& qs) {
    std::vector<std::pair<std::string, std::string>> out;
    size_t start = 0;
    while (start <= qs.size()) {
        size_t amp = qs.find('&', start);
        if (amp == std::string::npos) amp = qs.size();
        std::string part = qs.substr(start, amp - start);
        if (!part.empty()) {
            size_t eq = part.find('=');
            if (eq == std::string::npos) out.emplace_back(url_decode(part, true), "");
            else out.emplace_back(url_decode(part.substr(0, eq), true), url_decode(part.substr(eq + 1), true));
        }
        start = amp + 1;
    }
    return out;
}

// ———————————————————————————— 解析 ————————————————————————————

namespace {

ParseResult perr(int status, const char* code, std::string msg) {
    ParseResult r;
    r.kind = ParseResult::Error;
    r.status = status;
    r.code = code;
    r.message = std::move(msg);
    return r;
}

// chunked 正文：返回 1 完成、0 不完整、-1 格式错、-2 超长
int decode_chunked(const char* p, size_t len, size_t max_body, std::string& body, size_t& used) {
    size_t i = 0;
    body.clear();
    while (true) {
        const char* nl = static_cast<const char*>(std::memchr(p + i, '\n', len - i));
        if (!nl) return len - i > 1024 ? -1 : 0;
        size_t line_end = static_cast<size_t>(nl - p);
        if (line_end == i || p[line_end - 1] != '\r') return -1;
        std::string line(p + i, line_end - 1 - i);
        auto semi = line.find(';');
        if (semi != std::string::npos) line = line.substr(0, semi);
        line = trim_ows(line);
        if (line.empty() || line.size() > 15) return -1;
        uint64_t sz = 0;
        for (char c : line) {
            int v = std::isdigit(static_cast<unsigned char>(c)) ? c - '0'
                    : (c >= 'a' && c <= 'f') ? c - 'a' + 10 : (c >= 'A' && c <= 'F') ? c - 'A' + 10 : -1;
            if (v < 0) return -1;
            sz = sz * 16 + static_cast<uint64_t>(v);
        }
        i = line_end + 1;
        if (sz == 0) {
            // 尾部字段（trailers）直到空行
            while (true) {
                const char* n2 = static_cast<const char*>(std::memchr(p + i, '\n', len - i));
                if (!n2) return len - i > 8192 ? -1 : 0;
                size_t e = static_cast<size_t>(n2 - p);
                bool empty = e == i + 1 && p[i] == '\r';
                i = e + 1;
                if (empty) { used = i; return 1; }
            }
        }
        if (body.size() + sz > max_body) return -2;
        if (len - i < sz + 2) return 0;
        body.append(p + i, sz);
        i += sz;
        if (p[i] != '\r' || p[i + 1] != '\n') return -1;
        i += 2;
    }
}

}  // namespace

ParseResult parse_request(const char* data, size_t len, const ParseLimits& lim, Request& out) {
    size_t skip = 0;   // RFC 7230 §3.5：请求行前的空行忽略
    while (skip + 1 < len && data[skip] == '\r' && data[skip + 1] == '\n') skip += 2;
    const char* p = data + skip;
    size_t n = len - skip;

    // 头结束位置
    size_t hend = std::string::npos;
    size_t scan = std::min(n, lim.max_header_bytes + 4);
    for (size_t i = 3; i < scan; ++i)
        if (p[i] == '\n' && p[i - 1] == '\r' && p[i - 2] == '\n' && p[i - 3] == '\r') { hend = i + 1; break; }
    if (hend == std::string::npos) {
        if (n > lim.max_header_bytes) return perr(431, codes::kHeadersTooLarge, "请求头过大");
        return ParseResult{};
    }

    Request req;
    std::string head(p, hend - 4);
    size_t pos = head.find("\r\n");
    std::string line = head.substr(0, pos);
    // 请求行：METHOD SP target SP HTTP/1.x
    size_t s1 = line.find(' ');
    size_t s2 = s1 == std::string::npos ? std::string::npos : line.find(' ', s1 + 1);
    if (s1 == std::string::npos || s2 == std::string::npos || line.find(' ', s2 + 1) != std::string::npos)
        return perr(400, codes::kBadRequest, "请求行格式错误");
    req.method = line.substr(0, s1);
    req.target = line.substr(s1 + 1, s2 - s1 - 1);
    std::string ver = line.substr(s2 + 1);
    if (req.method.empty()) return perr(400, codes::kBadRequest, "请求行格式错误");
    for (char c : req.method)
        if (!is_tchar(c)) return perr(400, codes::kBadRequest, "请求方法不合法");
    if (ver == "HTTP/1.1") req.version_minor = 1;
    else if (ver == "HTTP/1.0") req.version_minor = 0;
    else return perr(505, codes::kBadRequest, "只支持 HTTP/1.0 / 1.1");

    // 目标
    std::string t = req.target;
    if (t.empty()) return perr(400, codes::kBadRequest, "请求目标为空");
    for (unsigned char c : t)
        if (c <= 0x20 || c == 0x7F) return perr(400, codes::kBadRequest, "请求目标含非法字符");
    if (t[0] != '/') {
        if (t == "*") { req.raw_path = "*"; }
        else {
            auto sch = t.find("://");
            if (sch == std::string::npos) return perr(400, codes::kBadRequest, "请求目标格式错误");
            auto slash = t.find('/', sch + 3);
            t = slash == std::string::npos ? "/" : t.substr(slash);
        }
    }
    if (req.raw_path.empty()) {
        auto hash = t.find('#');
        if (hash != std::string::npos) t = t.substr(0, hash);
        auto q = t.find('?');
        req.raw_path = q == std::string::npos ? t : t.substr(0, q);
        req.query_string = q == std::string::npos ? "" : t.substr(q + 1);
    }
    req.path = url_decode(req.raw_path, false);
    req.query = parse_query(req.query_string);

    // 头
    while (pos != std::string::npos) {
        size_t start = pos + 2;
        pos = head.find("\r\n", start);
        std::string h = head.substr(start, pos == std::string::npos ? std::string::npos : pos - start);
        if (h.empty()) continue;
        if (h[0] == ' ' || h[0] == '\t') return perr(400, codes::kBadRequest, "不支持折行的请求头");
        auto colon = h.find(':');
        if (colon == std::string::npos || colon == 0) return perr(400, codes::kBadRequest, "请求头格式错误");
        std::string name = h.substr(0, colon);
        for (char c : name)
            if (!is_tchar(c)) return perr(400, codes::kBadRequest, "请求头名称不合法");
        req.headers.add(name, trim_ows(h.substr(colon + 1)));
    }
    if (req.version_minor == 1 && !req.headers.has("Host")) return perr(400, codes::kBadRequest, "HTTP/1.1 请求缺少 Host 头");

    // 正文长度
    const char* body_p = p + hend;
    size_t body_avail = n - hend;
    bool chunked = false;
    uint64_t clen = 0;
    if (req.headers.has("Transfer-Encoding")) {
        std::string te = req.headers.get("Transfer-Encoding");
        auto comma = te.rfind(',');
        std::string last = trim_ows(comma == std::string::npos ? te : te.substr(comma + 1));
        if (!iequals(last, "chunked")) return perr(501, codes::kBadRequest, "不支持的 Transfer-Encoding");
        if (req.headers.has("Content-Length")) return perr(400, codes::kBadRequest, "Content-Length 与 Transfer-Encoding 不能同时出现");
        chunked = true;
    } else {
        const std::string* first = nullptr;
        for (auto& kv : req.headers.items()) {
            if (!iequals(kv.first, "Content-Length")) continue;
            if (first && *first != kv.second) return perr(400, codes::kBadRequest, "Content-Length 不一致");
            first = &kv.second;
        }
        if (first) {
            if (first->empty() || first->size() > 18) return perr(400, codes::kBadRequest, "Content-Length 不合法");
            for (char c : *first)
                if (c < '0' || c > '9') return perr(400, codes::kBadRequest, "Content-Length 不合法");
            clen = std::stoull(*first);
            if (clen > lim.max_body_bytes)
                return perr(413, codes::kPayloadTooLarge, "请求正文过大（上限 " + std::to_string(lim.max_body_bytes) + " 字节）");
        }
    }

    ParseResult r;
    r.headers_complete = true;
    r.expect_continue = req.headers.has_token("Expect", "100-continue");
    if (chunked) {
        size_t used = 0;
        int rc = decode_chunked(body_p, body_avail, lim.max_body_bytes, req.body, used);
        if (rc == -1) return perr(400, codes::kBadRequest, "chunked 正文格式错误");
        if (rc == -2) return perr(413, codes::kPayloadTooLarge, "请求正文过大（上限 " + std::to_string(lim.max_body_bytes) + " 字节）");
        if (rc == 0) return r;
        r.consumed = skip + hend + used;
    } else {
        if (body_avail < clen) return r;
        req.body.assign(body_p, clen);
        r.consumed = skip + hend + clen;
    }
    r.kind = ParseResult::Complete;
    out = std::move(req);
    return r;
}

std::string serialize_head(const Response& r, int64_t body_length, bool keep_alive, bool chunked) {
    std::string s = "HTTP/1.1 " + std::to_string(r.status) + " " + status_text(r.status) + "\r\n";
    for (auto& kv : r.headers.items()) {
        if (iequals(kv.first, "Content-Length") || iequals(kv.first, "Transfer-Encoding") ||
            iequals(kv.first, "Connection") || iequals(kv.first, "Date"))
            continue;
        s += kv.first + ": " + kv.second + "\r\n";
    }
    s += "Date: " + http_date(static_cast<int64_t>(std::time(nullptr))) + "\r\n";
    bool no_body = (r.status >= 100 && r.status < 200) || r.status == 204 || r.status == 304;
    if (!no_body) {
        if (chunked) s += "Transfer-Encoding: chunked\r\n";
        else if (body_length >= 0) s += "Content-Length: " + std::to_string(body_length) + "\r\n";
        // body_length < 0 且非 chunked：不写长度，以关闭连接为结束（只给 HTTP/1.0 的流式响应用）
    }
    s += keep_alive ? "Connection: keep-alive\r\n" : "Connection: close\r\n";
    s += "\r\n";
    return s;
}

// ———————————————————————————— 文件下载 ————————————————————————————

std::string weak_etag(uint64_t length, int64_t mtime_unix_ns) {
    // C#: $"\"{fi.Length:x}-{fi.LastWriteTimeUtc.Ticks:x}\"" 弱比较；.NET ticks = 100 ns since 0001-01-01
    int64_t ticks = mtime_unix_ns / 100 + 621355968000000000LL;
    char buf[64];
    std::snprintf(buf, sizeof buf, "W/\"%llx-%llx\"", static_cast<unsigned long long>(length),
                  static_cast<unsigned long long>(ticks));
    return buf;
}

namespace {

std::string opaque(const std::string& etag) {
    std::string e = trim_ows(etag);
    if (e.size() >= 2 && e[0] == 'W' && e[1] == '/') e = e.substr(2);
    return e;
}

bool if_none_match_hit(const std::string& header, const std::string& etag) {
    size_t start = 0;
    while (start <= header.size()) {
        size_t comma = header.find(',', start);
        if (comma == std::string::npos) comma = header.size();
        std::string item = trim_ows(header.substr(start, comma - start));
        if (item == "*" || (!item.empty() && opaque(item) == opaque(etag))) return true;
        start = comma + 1;
    }
    return false;
}

}  // namespace

Response file_response(const Request& req, const std::string& path, const std::string& content_type,
                       const std::string& download_name) {
    int fd = ::open(path.c_str(), O_RDONLY | O_CLOEXEC);
    if (fd < 0) return Response::error(codes::kNotFound, "文件不存在或名称不合法");
    struct stat st{};
    if (fstat(fd, &st) != 0 || !S_ISREG(st.st_mode)) {
        ::close(fd);
        return Response::error(codes::kNotFound, "文件不存在或名称不合法");
    }
#ifdef __APPLE__
    int64_t mtime_ns = static_cast<int64_t>(st.st_mtimespec.tv_sec) * 1000000000LL + st.st_mtimespec.tv_nsec;
#else
    int64_t mtime_ns = static_cast<int64_t>(st.st_mtim.tv_sec) * 1000000000LL + st.st_mtim.tv_nsec;
#endif
    uint64_t len = static_cast<uint64_t>(st.st_size);
    std::string etag = weak_etag(len, mtime_ns);
    int64_t mtime_sec = mtime_ns / 1000000000LL;

    Response r;
    r.headers.set("Content-Type", content_type);
    r.headers.set("ETag", etag);
    r.headers.set("Last-Modified", http_date(mtime_sec));
    r.headers.set("Accept-Ranges", "bytes");
    if (!download_name.empty()) {
        std::string ascii;
        for (unsigned char c : download_name) ascii += (c >= 0x20 && c < 0x7F && c != '"' && c != '\\') ? static_cast<char>(c) : '_';
        r.headers.set("Content-Disposition",
                      "attachment; filename=\"" + ascii + "\"; filename*=UTF-8''" + url_encode_component(download_name));
    }

    if (const std::string* inm = req.headers.find("If-None-Match"); inm && if_none_match_hit(*inm, etag)) {
        ::close(fd);
        r.status = 304;
        r.headers.remove("Content-Type");
        return r;
    }

    uint64_t start = 0, end = len;   // [start, end)
    bool partial = false;
    const std::string* range = req.headers.find("Range");
    bool honor = range != nullptr && len > 0;
    if (honor) {
        if (const std::string* ifr = req.headers.find("If-Range")) {
            std::string v = trim_ows(*ifr);
            if (!v.empty() && (v[0] == '"' || v[0] == 'W')) honor = false;   // 强比较：弱 ETag 永不匹配（同 ASP.NET）
            else {
                auto d = parse_http_date(v);
                honor = d && mtime_sec <= *d;
            }
        }
    }
    if (honor) {
        std::string rv = trim_ows(*range);
        if (rv.size() > 6 && iequals(rv.substr(0, 6), "bytes=") && rv.find(',') == std::string::npos) {
            std::string spec = trim_ows(rv.substr(6));
            auto dash = spec.find('-');
            auto digits = [](const std::string& x) {
                if (x.empty() || x.size() > 18) return false;
                for (char c : x)
                    if (c < '0' || c > '9') return false;
                return true;
            };
            if (dash != std::string::npos) {
                std::string a = trim_ows(spec.substr(0, dash)), b = trim_ows(spec.substr(dash + 1));
                bool ok = false, unsat = false;
                if (a.empty() && digits(b)) {   // 后 n 字节
                    uint64_t nlast = std::stoull(b);
                    if (nlast == 0) unsat = true;
                    else { start = nlast >= len ? 0 : len - nlast; end = len; ok = true; }
                } else if (digits(a) && (b.empty() || digits(b))) {
                    uint64_t sa = std::stoull(a);
                    uint64_t sb = b.empty() ? len - 1 : std::stoull(b);
                    if (sa >= len) unsat = true;
                    else if (sb >= sa) { start = sa; end = std::min(sb + 1, len); ok = true; }
                }
                if (unsat) {
                    ::close(fd);
                    Response e = Response::error(codes::kRangeNotSatisfiable, "请求的范围超出文件长度");
                    e.headers.set("Content-Range", "bytes */" + std::to_string(len));
                    return e;
                }
                partial = ok;
            }
        }
    }
    if (partial) {
        r.status = 206;
        r.headers.set("Content-Range",
                      "bytes " + std::to_string(start) + "-" + std::to_string(end - 1) + "/" + std::to_string(len));
    } else {
        r.status = 200;
        start = 0;
        end = len;
    }
    r.file = std::make_shared<FileBody>(fd, start, end - start);
    return r;
}

}  // namespace reactor::net
