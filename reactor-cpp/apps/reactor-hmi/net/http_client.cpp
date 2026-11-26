#include "http_client.hpp"

#include "wire.hpp"

#include <algorithm>
#include <cstdlib>
#include <arpa/inet.h>
#include <cerrno>
#include <cstring>
#include <fcntl.h>
#include <netdb.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <poll.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>

namespace reactor::hmi::net {

namespace {
std::string lower(std::string s) {
    for (auto& c : s) c = static_cast<char>(c >= 'A' && c <= 'Z' ? c + 32 : c);
    return s;
}
std::string trim(const std::string& s) {
    size_t a = 0, b = s.size();
    while (a < b && (s[a] == ' ' || s[a] == '\t')) ++a;
    while (b > a && (s[b - 1] == ' ' || s[b - 1] == '\t' || s[b - 1] == '\r')) --b;
    return s.substr(a, b - a);
}
#ifdef MSG_NOSIGNAL
constexpr int kSendFlags = MSG_NOSIGNAL;
#else
constexpr int kSendFlags = 0;
#endif
}  // namespace

std::string Endpoint::text() const { return is_unix ? "unix:" + path : "tcp://" + host + ":" + std::to_string(port); }

bool parse_endpoint(const std::string& s, Endpoint& out, std::string* err) {
    Endpoint e;
    if (s.rfind("unix:", 0) == 0 || (!s.empty() && s[0] == '/')) {
        e.is_unix = true;
        e.path = s[0] == '/' ? s : s.substr(5);
        while (e.path.rfind("//", 0) == 0) e.path.erase(0, 1);  // unix:///run/… 也认
        if (e.path.empty() || e.path.size() >= sizeof(sockaddr_un{}.sun_path)) {
            if (err) *err = "Unix socket 路径不对：" + s;
            return false;
        }
    } else if (s.rfind("tcp://", 0) == 0 || s.rfind("http://", 0) == 0) {
        std::string hp = s.substr(s.find("://") + 3);
        if (auto sl = hp.find('/'); sl != std::string::npos) hp.resize(sl);
        e.is_unix = false;
        auto c = hp.rfind(':');
        if (c == std::string::npos || c == 0) {
            if (err) *err = "地址要写成 tcp://主机:端口：" + s;
            return false;
        }
        e.host = hp.substr(0, c);
        if (e.host.size() > 2 && e.host.front() == '[' && e.host.back() == ']') e.host = e.host.substr(1, e.host.size() - 2);
        e.port = std::atoi(hp.c_str() + c + 1);
        if (e.port <= 0 || e.port > 65535) {
            if (err) *err = "端口不对：" + s;
            return false;
        }
    } else {
        if (err) *err = "不认识的地址（unix:/路径 或 tcp://主机:端口）：" + s;
        return false;
    }
    out = e;
    return true;
}

void close_fd(int& fd) {
    if (fd >= 0) ::close(fd);
    fd = -1;
}

static bool wait_fd(int fd, short ev, int timeout_ms) {
    pollfd p{fd, ev, 0};
    for (;;) {
        int r = ::poll(&p, 1, timeout_ms < 0 ? 0 : timeout_ms);
        if (r < 0 && errno == EINTR) continue;
        return r > 0;
    }
}

static int connect_addr(int family, const sockaddr* sa, socklen_t len, int timeout_ms, std::string* err) {
    int fd = ::socket(family, SOCK_STREAM, 0);
    if (fd < 0) {
        if (err) *err = std::string("socket 失败：") + std::strerror(errno);
        return -1;
    }
    ::fcntl(fd, F_SETFD, FD_CLOEXEC);
    ::fcntl(fd, F_SETFL, ::fcntl(fd, F_GETFL) | O_NONBLOCK);
#ifdef SO_NOSIGPIPE
    int one = 1;
    ::setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &one, sizeof one);
#endif
    int r = ::connect(fd, sa, len);
    if (r < 0 && errno != EINPROGRESS && errno != EAGAIN) {
        if (err) *err = std::string("连不上：") + std::strerror(errno);
        ::close(fd);
        return -1;
    }
    if (r < 0) {
        if (!wait_fd(fd, POLLOUT, timeout_ms)) {
            if (err) *err = "连接超时";
            ::close(fd);
            return -1;
        }
        int so = 0;
        socklen_t sl = sizeof so;
        ::getsockopt(fd, SOL_SOCKET, SO_ERROR, &so, &sl);
        if (so != 0) {
            if (err) *err = std::string("连不上：") + std::strerror(so);
            ::close(fd);
            return -1;
        }
    }
    return fd;
}

int connect_to(const Endpoint& ep, int timeout_ms, std::string* err) {
    if (ep.is_unix) {
        sockaddr_un a{};
        a.sun_family = AF_UNIX;
        std::strncpy(a.sun_path, ep.path.c_str(), sizeof(a.sun_path) - 1);
        return connect_addr(AF_UNIX, reinterpret_cast<sockaddr*>(&a), sizeof a, timeout_ms, err);
    }
    addrinfo hints{}, *res = nullptr;
    hints.ai_socktype = SOCK_STREAM;
    hints.ai_family = AF_UNSPEC;
    std::string port = std::to_string(ep.port);
    if (::getaddrinfo(ep.host.c_str(), port.c_str(), &hints, &res) != 0 || !res) {
        if (err) *err = "找不到主机 " + ep.host;
        return -1;
    }
    int fd = -1;
    for (addrinfo* ai = res; ai && fd < 0; ai = ai->ai_next)
        fd = connect_addr(ai->ai_family, ai->ai_addr, ai->ai_addrlen, timeout_ms, err);
    ::freeaddrinfo(res);
    if (fd >= 0) {
        int one = 1;
        ::setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &one, sizeof one);
    }
    return fd;
}

bool send_all(int fd, const char* p, size_t n, int64_t deadline, std::string* err) {
    while (n > 0) {
        ssize_t w = ::send(fd, p, n, kSendFlags);
        if (w > 0) {
            p += w;
            n -= static_cast<size_t>(w);
            continue;
        }
        if (w < 0 && errno == EINTR) continue;
        if (w < 0 && (errno == EAGAIN || errno == EWOULDBLOCK)) {
            int64_t left = deadline - mono_ms();
            if (left <= 0 || !wait_fd(fd, POLLOUT, static_cast<int>(left))) {
                if (err) *err = "发送超时";
                return false;
            }
            continue;
        }
        if (err) *err = std::string("发送失败：") + std::strerror(errno);
        return false;
    }
    return true;
}

long recv_some(int fd, char* buf, size_t n, int timeout_ms) {
    for (;;) {
        ssize_t r = ::recv(fd, buf, n, 0);
        if (r >= 0) return static_cast<long>(r);
        if (errno == EINTR) continue;
        if (errno != EAGAIN && errno != EWOULDBLOCK) return -1;
        if (timeout_ms <= 0) return -2;
        pollfd p{fd, POLLIN, 0};
        int pr = ::poll(&p, 1, timeout_ms);
        if (pr < 0 && errno == EINTR) continue;
        if (pr <= 0) return -2;
        timeout_ms = 0;  // 再读一次；poll 说可读，读不到就算超时
        ssize_t r2 = ::recv(fd, buf, n, 0);
        if (r2 >= 0) return static_cast<long>(r2);
        if (errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR) return -2;
        return -1;
    }
}

const std::string* find_header(const Headers& h, const std::string& name) {
    std::string n = lower(name);
    for (auto& kv : h)
        if (lower(kv.first) == n) return &kv.second;
    return nullptr;
}

// ------------------------------------------------------------ 解析器
void HttpParser::reset(bool head_request) {
    state_ = Start;
    head_ = head_request;
    line_.clear();
    err_.clear();
    method_.clear();
    target_.clear();
    res_ = HttpResponse{};
    remain_ = 0;
}

bool HttpParser::line_complete(const char*& p, const char* end, std::string& line) {
    while (p < end) {
        char c = *p++;
        if (c == '\n') {
            if (!line.empty() && line.back() == '\r') line.pop_back();
            return true;
        }
        line.push_back(c);
        if (line.size() > 16384) {
            err_ = "头太长";
            return false;
        }
    }
    return false;
}

bool HttpParser::on_start_line(const std::string& l) {
    if (kind_ == Response) {
        // HTTP/1.1 200 OK
        if (l.rfind("HTTP/1.", 0) != 0 || l.size() < 12) { err_ = "不是 HTTP 响应：" + l.substr(0, 40); return false; }
        res_.status = std::atoi(l.c_str() + 9);
        res_.reason = l.size() > 13 ? l.substr(13) : "";
        res_.keep_alive = l[7] == '1';
        if (res_.status < 100 || res_.status > 999) { err_ = "状态码不对"; return false; }
    } else {
        auto a = l.find(' '), b = l.rfind(' ');
        if (a == std::string::npos || b == a) { err_ = "请求行不对"; return false; }
        method_ = l.substr(0, a);
        target_ = l.substr(a + 1, b - a - 1);
        res_.keep_alive = l.compare(b + 1, 8, "HTTP/1.1") == 0;
    }
    return true;
}

bool HttpParser::on_headers_done() {
    if (auto* c = res_.header("Connection")) {
        std::string v = lower(*c);
        if (v.find("close") != std::string::npos) res_.keep_alive = false;
        if (v.find("keep-alive") != std::string::npos) res_.keep_alive = true;
    }
    int st = res_.status;
    bool no_body = kind_ == Response && (head_ || (st >= 100 && st < 200) || st == 204 || st == 304);
    if (no_body) { state_ = Done; return true; }
    if (auto* te = res_.header("Transfer-Encoding"); te && lower(*te).find("chunked") != std::string::npos) {
        state_ = ChunkSize;
        return true;
    }
    if (auto* cl = res_.header("Content-Length")) {
        char* e = nullptr;
        unsigned long long v = std::strtoull(cl->c_str(), &e, 10);
        if (e == cl->c_str()) { err_ = "Content-Length 不对"; return false; }
        if (v > max_body) { err_ = "正文太大"; return false; }
        remain_ = v;
        state_ = v ? Body : Done;
        return true;
    }
    if (kind_ == Request) { state_ = Done; return true; }  // 请求没写长度 = 没有正文
    state_ = UntilClose;
    res_.keep_alive = false;
    return true;
}

int HttpParser::feed(const char* p, size_t n, size_t* used) {
    const char* s = p;
    const char* end = p + n;
    auto fin = [&](int r) { if (used) *used = static_cast<size_t>(p - s); return r; };
    while (p < end || state_ == Done) {
        switch (state_) {
            case Start:
                if (!line_complete(p, end, line_)) return err_.empty() ? fin(0) : fin(-1);
                if (line_.empty()) break;  // 报文之间多余的空行
                if (!on_start_line(line_)) return fin(-1);
                line_.clear();
                state_ = Headers_;
                break;
            case Headers_:
                if (!line_complete(p, end, line_)) return err_.empty() ? fin(0) : fin(-1);
                if (line_.empty()) {
                    if (kind_ == Response && res_.status >= 100 && res_.status < 200 && res_.status != 101) {
                        res_ = HttpResponse{};  // 100 Continue：丢掉，等真正的响应
                        state_ = Start;
                        break;
                    }
                    if (!on_headers_done()) return fin(-1);
                } else {
                    auto c = line_.find(':');
                    if (c == std::string::npos) { err_ = "头格式不对"; return fin(-1); }
                    res_.headers.emplace_back(trim(line_.substr(0, c)), trim(line_.substr(c + 1)));
                    if (res_.headers.size() > 200) { err_ = "头太多"; return fin(-1); }
                    line_.clear();
                }
                break;
            case Body: {
                size_t take = static_cast<size_t>(std::min<uint64_t>(remain_, static_cast<uint64_t>(end - p)));
                res_.body.append(p, take);
                p += take;
                remain_ -= take;
                if (!remain_) state_ = Done;
                break;
            }
            case ChunkSize:
                if (!line_complete(p, end, line_)) return err_.empty() ? fin(0) : fin(-1);
                {
                    std::string hex = line_.substr(0, line_.find(';'));  // 去掉 chunk 扩展
                    hex = trim(hex);
                    char* e = nullptr;
                    unsigned long long v = std::strtoull(hex.c_str(), &e, 16);
                    if (hex.empty() || e == hex.c_str()) { err_ = "chunk 长度不对"; return fin(-1); }
                    if (res_.body.size() + v > max_body) { err_ = "正文太大"; return fin(-1); }
                    line_.clear();
                    remain_ = v;
                    state_ = v ? ChunkData : Trailer;
                }
                break;
            case ChunkData: {
                size_t take = static_cast<size_t>(std::min<uint64_t>(remain_, static_cast<uint64_t>(end - p)));
                res_.body.append(p, take);
                p += take;
                remain_ -= take;
                if (!remain_) state_ = ChunkCrlf;
                break;
            }
            case ChunkCrlf:
                if (!line_complete(p, end, line_)) return err_.empty() ? fin(0) : fin(-1);
                if (!line_.empty()) { err_ = "chunk 后面缺 CRLF"; return fin(-1); }
                state_ = ChunkSize;
                break;
            case Trailer:
                if (!line_complete(p, end, line_)) return err_.empty() ? fin(0) : fin(-1);
                if (line_.empty()) state_ = Done;
                else line_.clear();  // 尾部头：不用
                break;
            case UntilClose:
                if (res_.body.size() + static_cast<size_t>(end - p) > max_body) { err_ = "正文太大"; return fin(-1); }
                res_.body.append(p, static_cast<size_t>(end - p));
                p = end;
                break;
            case Done:
                return fin(1);
        }
    }
    return fin(state_ == Done ? 1 : 0);
}

int HttpParser::finish_eof() {
    if (state_ == UntilClose || state_ == Done) {
        state_ = Done;
        return 1;
    }
    err_ = "连接在响应中途断开";
    return -1;
}

// ------------------------------------------------------------ 连接
std::string build_request(const Endpoint& ep, const HttpRequest& r, const std::string& token, bool keep_alive) {
    std::string s;
    s.reserve(256 + r.body.size());
    s += r.method + " " + r.target + " HTTP/1.1\r\n";
    s += "Host: " + std::string(ep.is_unix ? "localhost" : ep.host + ":" + std::to_string(ep.port)) + "\r\n";
    s += "User-Agent: reactor-hmi\r\n";
    s += "Accept: application/json\r\n";
    if (!token.empty()) s += "Authorization: Bearer " + token + "\r\n";
    for (auto& kv : r.headers) s += kv.first + ": " + kv.second + "\r\n";
    if (!r.body.empty() || r.method == "POST" || r.method == "PUT" || r.method == "PATCH") {
        if (!r.body.empty()) s += "Content-Type: application/json; charset=utf-8\r\n";
        s += "Content-Length: " + std::to_string(r.body.size()) + "\r\n";
    }
    if (!keep_alive) s += "Connection: close\r\n";
    s += "\r\n";
    s += r.body;
    return s;
}

void HttpConn::close() {
    close_fd(fd_);
    pending_.clear();
}

bool HttpConn::once(const HttpRequest& r, HttpResponse& out, std::string* err, bool* got_bytes) {
    int64_t deadline = mono_ms() + r.timeout_ms;
    *got_bytes = false;
    if (fd_ < 0) {
        fd_ = connect_to(ep_, std::min(r.timeout_ms, 3000), err);
        if (fd_ < 0) return false;
        pending_.clear();
    }
    std::string wire = build_request(ep_, r, token_);
    if (!send_all(fd_, wire.data(), wire.size(), deadline, err)) return false;
    HttpParser ps(HttpParser::Response);
    ps.reset(r.method == "HEAD");
    char buf[16384];
    std::string carry;
    carry.swap(pending_);
    for (;;) {
        if (!carry.empty()) {
            size_t used = 0;
            int st = ps.feed(carry.data(), carry.size(), &used);
            if (st < 0) { if (err) *err = "响应格式不对：" + ps.error(); return false; }
            if (st == 1) {
                pending_ = carry.substr(used);
                out = std::move(ps.msg());
                return true;
            }
            carry.clear();
        }
        int64_t left = deadline - mono_ms();
        if (left <= 0) { if (err) *err = "等后台响应超时"; return false; }
        long n = recv_some(fd_, buf, sizeof buf, static_cast<int>(left));
        if (n == -2) { if (err) *err = "等后台响应超时"; return false; }
        if (n < 0) { if (err) *err = std::string("接收失败：") + std::strerror(errno); return false; }
        if (n == 0) {
            if (ps.finish_eof() == 1) { out = std::move(ps.msg()); out.keep_alive = false; return true; }
            if (err) *err = *got_bytes ? "连接在响应中途断开" : "后台关闭了连接";
            return false;
        }
        *got_bytes = true;
        carry.assign(buf, static_cast<size_t>(n));
    }
}

bool HttpConn::exec(const HttpRequest& r, HttpResponse& out, std::string* err) {
    // 空闲太久的连接主动丢掉（服务端 keep-alive 130 s），少碰一次「对端刚关」
    if (fd_ >= 0 && mono_ms() - last_used_ > 60000) close();
    bool reused = fd_ >= 0;
    bool got = false;
    bool ok = once(r, out, err, &got);
    if (!ok && reused && !got) {
        close();
        ok = once(r, out, err, &got);
    }
    if (!ok) { close(); return false; }
    last_used_ = mono_ms();
    if (!out.keep_alive) close();
    return true;
}

}  // namespace reactor::hmi::net
