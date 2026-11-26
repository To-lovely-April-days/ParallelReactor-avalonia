// 测试用的最小 HTTP / WebSocket 客户端（阻塞 socket + 超时）。只给 tests/net 用。
#pragma once
#include <arpa/inet.h>
#include <netinet/in.h>
#include <poll.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>

#include <cerrno>
#include <cstring>
#include <map>
#include <string>

#include "net/http.hpp"
#include "net/websocket.hpp"

namespace nettest {

#ifdef MSG_NOSIGNAL
constexpr int kFlags = MSG_NOSIGNAL;
#else
constexpr int kFlags = 0;
#endif

inline void nosig(int fd) {
#ifdef SO_NOSIGPIPE
    int one = 1;
    setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &one, sizeof one);
#else
    (void)fd;
#endif
}

inline int connect_tcp(int port, const char* host = "127.0.0.1") {
    bool v6 = std::strchr(host, ':') != nullptr;
    int fd = ::socket(v6 ? AF_INET6 : AF_INET, SOCK_STREAM, 0);
    if (fd < 0) return -1;
    nosig(fd);
    int rc;
    if (v6) {
        sockaddr_in6 a{};
        a.sin6_family = AF_INET6;
        a.sin6_port = htons(static_cast<uint16_t>(port));
        inet_pton(AF_INET6, host, &a.sin6_addr);
        rc = ::connect(fd, reinterpret_cast<sockaddr*>(&a), sizeof a);
    } else {
        sockaddr_in a{};
        a.sin_family = AF_INET;
        a.sin_port = htons(static_cast<uint16_t>(port));
        inet_pton(AF_INET, host, &a.sin_addr);
        rc = ::connect(fd, reinterpret_cast<sockaddr*>(&a), sizeof a);
    }
    if (rc != 0) { ::close(fd); return -1; }
    return fd;
}

inline int connect_unix(const std::string& path) {
    int fd = ::socket(AF_UNIX, SOCK_STREAM, 0);
    if (fd < 0) return -1;
    nosig(fd);
    sockaddr_un a{};
    a.sun_family = AF_UNIX;
    std::strncpy(a.sun_path, path.c_str(), sizeof(a.sun_path) - 1);
    if (::connect(fd, reinterpret_cast<sockaddr*>(&a), sizeof a) != 0) { ::close(fd); return -1; }
    return fd;
}

inline bool send_all(int fd, const std::string& s) {
    size_t off = 0;
    while (off < s.size()) {
        ssize_t n = ::send(fd, s.data() + off, s.size() - off, kFlags);
        if (n <= 0) { if (n < 0 && errno == EINTR) continue; return false; }
        off += static_cast<size_t>(n);
    }
    return true;
}

// 读到 buf 里至少有 want 字节或超时 / 断开；返回是否读到新数据（false = 超时或 EOF）
inline bool read_more(int fd, std::string& buf, int timeout_ms, bool* eof = nullptr) {
    pollfd p{fd, POLLIN, 0};
    int rc = ::poll(&p, 1, timeout_ms);
    if (rc <= 0) return false;
    char tmp[65536];
    ssize_t n = ::recv(fd, tmp, sizeof tmp, 0);
    if (n <= 0) { if (eof) *eof = true; return false; }
    buf.append(tmp, static_cast<size_t>(n));
    return true;
}

struct HttpResp {
    int status = 0;
    std::map<std::string, std::string> headers;   // 名字小写
    std::string body;
    bool ok = false;
    bool closed = false;   // 服务端在响应后关闭了连接（读到 EOF）
    std::string h(const std::string& n) const { auto it = headers.find(n); return it == headers.end() ? "" : it->second; }
};

inline std::string lower(std::string s) {
    for (auto& c : s) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return s;
}

// 从 buf（可能已含部分数据）读一个完整响应；head_only = HEAD 请求（无正文）
inline HttpResp read_response(int fd, std::string& buf, int timeout_ms = 3000, bool head_only = false) {
    HttpResp r;
    size_t hend;
    while ((hend = buf.find("\r\n\r\n")) == std::string::npos) {
        bool eof = false;
        if (!read_more(fd, buf, timeout_ms, &eof)) { r.closed = eof; return r; }
    }
    std::string head = buf.substr(0, hend);
    buf.erase(0, hend + 4);
    size_t sp = head.find(' ');
    r.status = std::atoi(head.c_str() + sp + 1);
    size_t pos = head.find("\r\n");
    while (pos != std::string::npos) {
        size_t s = pos + 2;
        pos = head.find("\r\n", s);
        std::string line = head.substr(s, pos == std::string::npos ? std::string::npos : pos - s);
        auto c = line.find(':');
        if (c == std::string::npos) continue;
        std::string v = line.substr(c + 1);
        while (!v.empty() && v[0] == ' ') v.erase(0, 1);
        r.headers[lower(line.substr(0, c))] = v;
    }
    bool no_body = head_only || r.status == 204 || r.status == 304 || r.status == 101 || (r.status >= 100 && r.status < 200);
    if (no_body) { r.ok = true; return r; }
    if (lower(r.h("transfer-encoding")) == "chunked") {
        while (true) {
            size_t nl;
            while ((nl = buf.find("\r\n")) == std::string::npos)
                if (!read_more(fd, buf, timeout_ms)) return r;
            size_t sz = std::stoul(buf.substr(0, nl), nullptr, 16);
            while (buf.size() < nl + 2 + sz + 2)
                if (!read_more(fd, buf, timeout_ms)) return r;
            r.body.append(buf, nl + 2, sz);
            buf.erase(0, nl + 2 + sz + 2);
            if (sz == 0) break;
        }
    } else if (!r.h("content-length").empty()) {
        size_t len = std::stoul(r.h("content-length"));
        while (buf.size() < len)
            if (!read_more(fd, buf, timeout_ms)) return r;
        r.body = buf.substr(0, len);
        buf.erase(0, len);
    } else {
        bool eof = false;
        while (read_more(fd, buf, timeout_ms, &eof)) {}
        r.body = buf;
        buf.clear();
        r.closed = eof;
    }
    r.ok = true;
    if (lower(r.h("connection")) == "close") {
        bool eof = false;
        std::string extra;
        read_more(fd, extra, 500, &eof);
        r.closed = eof;
    }
    return r;
}

inline std::string build(const std::string& method, const std::string& target, const std::string& token = "",
                         const std::string& body = "", const std::string& extra_headers = "") {
    std::string s = method + " " + target + " HTTP/1.1\r\nHost: test\r\n";
    if (!token.empty()) s += "Authorization: Bearer " + token + "\r\n";
    if (!body.empty() || method == "POST" || method == "PUT") s += "Content-Length: " + std::to_string(body.size()) + "\r\n";
    s += extra_headers;
    s += "\r\n" + body;
    return s;
}

// 单发请求（新连接）
inline HttpResp request(int port, const std::string& method, const std::string& target, const std::string& token = "",
                        const std::string& body = "", const std::string& extra_headers = "") {
    int fd = connect_tcp(port);
    if (fd < 0) return HttpResp{};
    send_all(fd, build(method, target, token, body, extra_headers));
    std::string buf;
    HttpResp r = read_response(fd, buf, 5000, method == "HEAD");
    ::close(fd);
    return r;
}

// —— WebSocket 客户端 ——
struct WsClient {
    int fd = -1;
    std::string buf;
    reactor::net::ws::MessageAssembler asmb{1 << 24, false};
    int status = 0;
    HttpResp handshake;

    bool open_fd(int f, const std::string& path, const std::string& token, const std::string& key = "dGhlIHNhbXBsZSBub25jZQ==") {
        fd = f;
        if (fd < 0) return false;
        std::string req = "GET " + path + " HTTP/1.1\r\nHost: test\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                          "Sec-WebSocket-Key: " + key + "\r\nSec-WebSocket-Version: 13\r\n";
        if (!token.empty()) req += "Authorization: Bearer " + token + "\r\n";
        req += "\r\n";
        send_all(fd, req);
        handshake = read_response(fd, buf, 3000);
        status = handshake.status;
        return status == 101;
    }
    bool open(int port, const std::string& path, const std::string& token) { return open_fd(connect_tcp(port), path, token); }

    bool send_frame(uint8_t op, const std::string& data, bool fin = true, bool masked = true) {
        static const uint8_t mask[4] = {0x12, 0x34, 0x56, 0x78};
        return send_all(fd, reactor::net::ws::encode_frame(op, data, fin, masked ? mask : nullptr));
    }
    bool send_text(const std::string& s) { return send_frame(reactor::net::ws::kText, s); }

    // 读下一条事件（消息 / ping / pong / close）；跳过服务器保活 Pong（skip_pong）
    bool next(reactor::net::ws::MessageAssembler::Event& out, int timeout_ms = 3000, bool skip_pong = true) {
        std::vector<reactor::net::ws::MessageAssembler::Event> evs;
        while (true) {
            if (!pending.empty()) {
                out = std::move(pending.front());
                pending.erase(pending.begin());
                if (skip_pong && out.kind == reactor::net::ws::MessageAssembler::Event::Pong) continue;
                return true;
            }
            evs.clear();
            asmb.feed(buf, evs);
            for (auto& e : evs) pending.push_back(std::move(e));
            if (!pending.empty()) continue;
            if (!read_more(fd, buf, timeout_ms)) return false;
        }
    }
    // 读到下一条文本消息
    bool next_text(std::string& out, int timeout_ms = 3000) {
        reactor::net::ws::MessageAssembler::Event e;
        while (next(e, timeout_ms)) {
            if (e.kind == reactor::net::ws::MessageAssembler::Event::Message) { out = e.data; return true; }
            if (e.kind == reactor::net::ws::MessageAssembler::Event::Close) { closed_code = e.code; return false; }
        }
        return false;
    }
    // 等关闭帧，返回码（0 = 没等到）
    int wait_close(int timeout_ms = 3000) {
        reactor::net::ws::MessageAssembler::Event e;
        while (next(e, timeout_ms))
            if (e.kind == reactor::net::ws::MessageAssembler::Event::Close) return e.code;
        return 0;
    }
    void close_fd() { if (fd >= 0) ::close(fd); fd = -1; }
    ~WsClient() { close_fd(); }
    std::vector<reactor::net::ws::MessageAssembler::Event> pending;
    int closed_code = 0;
};

}  // namespace nettest
