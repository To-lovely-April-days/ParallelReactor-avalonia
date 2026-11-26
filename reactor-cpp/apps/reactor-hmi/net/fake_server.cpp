#include "fake_server.hpp"

#include "wire.hpp"
#include "ws_client.hpp"

#include <algorithm>
#include <cerrno>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <fcntl.h>
#include <poll.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>
#include <cstdint>
#include <functional>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace reactor::hmi::net {

bool FakeServer::start(Handler h) {
    h_ = std::move(h);
    ::unlink(path_.c_str());
    lfd_ = ::socket(AF_UNIX, SOCK_STREAM, 0);
    if (lfd_ < 0) return false;
    sockaddr_un a{};
    a.sun_family = AF_UNIX;
    std::strncpy(a.sun_path, path_.c_str(), sizeof(a.sun_path) - 1);
    if (::bind(lfd_, reinterpret_cast<sockaddr*>(&a), sizeof a) != 0 || ::listen(lfd_, 16) != 0) {
        close_fd(lfd_);
        return false;
    }
    stop_ = false;
    acc_ = std::thread([this] { accept_loop(); });
    return true;
}

void FakeServer::stop() {
    if (lfd_ < 0 && !acc_.joinable()) return;
    stop_ = true;
    if (acc_.joinable()) acc_.join();
    close_fd(lfd_);
    std::vector<std::thread> ts;
    {
        std::lock_guard<std::mutex> lk(mu_);
        for (int fd : fds_) ::shutdown(fd, SHUT_RDWR);
        ts.swap(conns_);
    }
    for (auto& t : ts)
        if (t.joinable()) t.join();
    ::unlink(path_.c_str());
}

void FakeServer::accept_loop() {
    while (!stop_) {
        pollfd p{lfd_, POLLIN, 0};
        if (::poll(&p, 1, 50) <= 0) continue;
        int fd = ::accept(lfd_, nullptr, nullptr);
        if (fd < 0) continue;
#ifdef SO_NOSIGPIPE
        int one = 1;
        ::setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &one, sizeof one);
#endif
        std::lock_guard<std::mutex> lk(mu_);
        fds_.push_back(fd);
        conns_.emplace_back([this, fd] { conn_loop(fd); });
    }
}

static bool send_raw(int fd, const std::string& s) {
    std::string e;
    return send_all(fd, s.data(), s.size(), mono_ms() + 5000, &e);
}

void FakeServer::conn_loop(int fd) {
    ++http_connects_;
    HttpParser ps(HttpParser::Request);
    ps.reset();
    std::string buf;
    char tmp[8192];
    bool open = true;
    while (open && !stop_) {
        // 先把缓冲里能解析的请求处理掉（流水线）
        size_t used = 0;
        int st = buf.empty() ? 0 : ps.feed(buf.data(), buf.size(), &used);
        if (st < 0) break;
        if (st == 1) {
            buf.erase(0, used);
            FakeReq rq;
            rq.method = ps.method();
            rq.target = ps.target();
            rq.headers = ps.msg().headers;
            rq.body = ps.msg().body;
            bool keep = ps.msg().keep_alive;
            ps.reset();
            const std::string* up = rq.header("Upgrade");
            if (up && (*up == "websocket" || *up == "WebSocket")) {
                const std::string* key = rq.header("Sec-WebSocket-Key");
                std::string resp = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " +
                                   ws_accept(key ? *key : "") + "\r\n\r\n";
                if (send_raw(fd, resp)) ws_loop(fd, buf);
                open = false;
                break;
            }
            {
                std::lock_guard<std::mutex> lk(mu_);
                log_.push_back(rq);
            }
            FakeResp r = h_ ? h_(rq) : FakeResp{404, R"({"ok":false,"error":{"code":"NOT_FOUND","message":"没有这个端点"}})"};
            std::string head = "HTTP/1.1 " + std::to_string(r.status) + " X\r\nContent-Type: application/json; charset=utf-8\r\n";
            std::string out;
            if (r.mode == FakeResp::Length) {
                out = head + "Content-Length: " + std::to_string(r.body.size()) + "\r\n" + (keep ? "" : "Connection: close\r\n") + "\r\n" + r.body;
            } else if (r.mode == FakeResp::Chunked) {
                out = head + "Transfer-Encoding: chunked\r\n\r\n";
                static const size_t sizes[] = {1, 3, 7, 64, 512};
                size_t i = 0, k = 0;
                while (i < r.body.size()) {
                    size_t n = std::min(sizes[k++ % 5], r.body.size() - i);
                    char h[32];
                    std::snprintf(h, sizeof h, (k % 2) ? "%zx;ext=1\r\n" : "%zX\r\n", n);
                    out += h + r.body.substr(i, n) + "\r\n";
                    i += n;
                }
                out += "0\r\nX-Trailer: done\r\n\r\n";
            } else {
                out = head + "Connection: close\r\n\r\n" + r.body;
                keep = false;
            }
            // 分几次写，客户端要能拼起来
            for (size_t i = 0; i < out.size(); i += 777)
                if (!send_raw(fd, out.substr(i, 777))) { open = false; break; }
            if (!keep) open = false;
            continue;
        }
        buf.clear();  // 已经全部喂进解析器
        pollfd p{fd, POLLIN, 0};
        if (::poll(&p, 1, 50) <= 0) continue;
        ssize_t n = ::recv(fd, tmp, sizeof tmp, 0);
        if (n <= 0) break;
        buf.assign(tmp, static_cast<size_t>(n));
    }
    {
        std::lock_guard<std::mutex> lk(mu_);
        fds_.erase(std::remove(fds_.begin(), fds_.end(), fd), fds_.end());
    }
    ::close(fd);
}

void FakeServer::ws_loop(int fd, std::string leftover) {
    ++ws_connects_;
    ++ws_count_;
    {
        std::lock_guard<std::mutex> lk(mu_);
        ws_fds_.push_back(fd);
    }
    {
        std::lock_guard<std::mutex> lk(send_mu_);
        send_raw(fd, ws_frame(WsOp::Text, hello, true, false));
    }
    WsDecoder dec(true);
    if (!leftover.empty()) dec.feed(leftover.data(), leftover.size());
    char tmp[8192];
    bool open = true;
    while (open && !stop_) {
        WsMsg m;
        while (dec.next(m)) {
            if (m.op == WsOp::Text) {
                std::lock_guard<std::mutex> lk(mu_);
                ws_log_.push_back(m.data);
            } else if (m.op == WsOp::Ping) {
                std::lock_guard<std::mutex> lk(send_mu_);
                send_raw(fd, ws_frame(WsOp::Pong, m.data, true, false));
            } else if (m.op == WsOp::Pong) {
                ++pongs;
            } else if (m.op == WsOp::Close) {
                std::lock_guard<std::mutex> lk(send_mu_);
                send_raw(fd, ws_frame(WsOp::Close, ws_close_payload(1000, ""), true, false));
                open = false;
            }
        }
        if (!open) break;
        pollfd p{fd, POLLIN, 0};
        if (::poll(&p, 1, 50) <= 0) continue;
        ssize_t n = ::recv(fd, tmp, sizeof tmp, 0);
        if (n <= 0) break;
        if (!dec.feed(tmp, static_cast<size_t>(n))) break;
    }
    {
        std::lock_guard<std::mutex> lk(mu_);
        ws_fds_.erase(std::remove(ws_fds_.begin(), ws_fds_.end(), fd), ws_fds_.end());
    }
    --ws_count_;
}

bool FakeServer::push(const std::string& text, int fragments, bool ping_between) {
    std::string out;
    if (fragments <= 1) out = ws_frame(WsOp::Text, text, true, false);
    else {
        size_t step = (text.size() + static_cast<size_t>(fragments) - 1) / static_cast<size_t>(fragments);
        for (int i = 0; i < fragments; ++i) {
            size_t a = std::min(text.size(), static_cast<size_t>(i) * step);
            size_t b = std::min(text.size(), a + step);
            bool last = i == fragments - 1;
            out += ws_frame(i == 0 ? WsOp::Text : WsOp::Cont, text.substr(a, last ? std::string::npos : b - a), last, false);
            if (i == 0 && ping_between) out += ws_frame(WsOp::Ping, "p", true, false);
        }
    }
    push_raw(out);
    return ws_count_ > 0;
}

void FakeServer::push_raw(const std::string& bytes) {
    std::vector<int> fds;
    {
        std::lock_guard<std::mutex> lk(mu_);
        fds = ws_fds_;
    }
    std::lock_guard<std::mutex> lk(send_mu_);
    for (int fd : fds) send_raw(fd, bytes);
}

void FakeServer::kill_ws() {
    std::lock_guard<std::mutex> lk(mu_);
    for (int fd : ws_fds_) ::shutdown(fd, SHUT_RDWR);
}

std::vector<std::string> FakeServer::ws_log() const {
    std::lock_guard<std::mutex> lk(mu_);
    return ws_log_;
}
std::vector<FakeReq> FakeServer::log() const {
    std::lock_guard<std::mutex> lk(mu_);
    return log_;
}
void FakeServer::clear_log() {
    std::lock_guard<std::mutex> lk(mu_);
    log_.clear();
    ws_log_.clear();
}
bool FakeServer::wait_for(const std::function<bool()>& pred, int timeout_ms) const {
    int64_t end = mono_ms() + timeout_ms;
    while (mono_ms() < end) {
        if (pred()) return true;
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }
    return pred();
}

}  // namespace reactor::hmi::net
