#include "net_client.hpp"

#include "wire.hpp"
#include "ws_client.hpp"

#include <algorithm>
#include <chrono>
#include <cerrno>
#include <csignal>
#include <cstdio>
#include <fcntl.h>
#include <poll.h>
#include <unistd.h>
#include <atomic>
#include <cstdint>
#include <deque>
#include <mutex>
#include <string>
#include <thread>

namespace reactor::hmi::net {

using nlohmann::json;

std::string Reply::message() const {
    if (!transport_ok) return "没有连上后台服务（" + err + "）";
    if (json.is_object() && json.contains("error") && json["error"].is_object()) {
        const auto& e = json["error"];
        if (e.contains("message") && e["message"].is_string() && !e["message"].get<std::string>().empty())
            return e["message"].get<std::string>();
        if (e.contains("code") && e["code"].is_string()) return "操作失败（" + e["code"].get<std::string>() + "）";
    }
    if (status >= 200 && status < 300) return "";
    return "后台返回 HTTP " + std::to_string(status);
}

std::string Reply::code() const {
    if (json.is_object() && json.contains("error") && json["error"].is_object() && json["error"].contains("code") &&
        json["error"]["code"].is_string())
        return json["error"]["code"].get<std::string>();
    return {};
}

NetClient::NetClient(Endpoint ep, std::string token, Listener* l) : ep_(std::move(ep)), token_(std::move(token)), l_(l) {}

NetClient::~NetClient() { stop(); }

void NetClient::start() {
    std::signal(SIGPIPE, SIG_IGN);  // 对端断开时 send 不要把整个界面进程带走
    if (::pipe(wake_) == 0) {
        for (int fd : wake_) {
            ::fcntl(fd, F_SETFL, ::fcntl(fd, F_GETFL) | O_NONBLOCK);
            ::fcntl(fd, F_SETFD, FD_CLOEXEC);
        }
    }
    stop_ = false;
    ws_th_ = std::thread([this] { ws_loop(); });
    lanes_[0].th = std::thread([this] { lane_loop(Lane::Read); });
    lanes_[1].th = std::thread([this] { lane_loop(Lane::Command); });
}

void NetClient::stop() {
    if (stop_.exchange(true)) return;
    for (auto& l : lanes_) {
        std::lock_guard<std::mutex> lk(l.mu);
        l.cv.notify_all();
    }
    if (wake_[1] >= 0) (void)!::write(wake_[1], "x", 1);
    if (ws_th_.joinable()) ws_th_.join();
    for (auto& l : lanes_)
        if (l.th.joinable()) l.th.join();
    close_fd(wake_[0]);
    close_fd(wake_[1]);
}

std::string NetClient::last_error() const {
    std::lock_guard<std::mutex> lk(emu_);
    return last_err_;
}

void NetClient::call(Lane lane, Call c) {
    auto& l = lanes_[lane == Lane::Read ? 0 : 1];
    std::lock_guard<std::mutex> lk(l.mu);
    if (c.urgent) l.q.push_front(std::move(c));
    else l.q.push_back(std::move(c));
    l.cv.notify_one();
}

bool NetClient::ws_send(const std::string& text) {
    if (!ws_up_) return false;
    {
        std::lock_guard<std::mutex> lk(wmu_);
        out_.push_back(text);
    }
    if (wake_[1] >= 0) (void)!::write(wake_[1], "x", 1);
    return true;
}

void NetClient::force_reconnect() {
    kick_ = true;
    if (wake_[1] >= 0) (void)!::write(wake_[1], "x", 1);
}

// 等 ms 毫秒，stop 时提前返回
static void nap(int wake_fd, int ms, const std::atomic<bool>& stop) {
    int64_t end = mono_ms() + ms;
    while (!stop) {
        int64_t left = end - mono_ms();
        if (left <= 0) return;
        pollfd p{wake_fd, POLLIN, 0};
        int r = ::poll(&p, wake_fd >= 0 ? 1 : 0, static_cast<int>(std::min<int64_t>(left, 200)));
        if (r > 0) {
            char b[64];
            while (::read(wake_fd, b, sizeof b) > 0) {
            }
        }
    }
}

void NetClient::ws_loop() {
    int attempt = 0;
    static const int kBackoff[] = {250, 500, 1000, 2000, 4000, 5000};
    while (!stop_) {
        std::string err, left;
        int fd = connect_to(ep_, 2000, &err);
        if (fd >= 0 && ws_handshake(fd, ep_, "/api/v1/ws", token_, 3000, &left, &err)) {
            {
                std::lock_guard<std::mutex> lk(wmu_);
                ws_fd_ = fd;
                out_.clear();
            }
            kick_ = false;
            ws_up_ = true;
            int64_t t_up = mono_ms();
            l_->on_ws_open();
            std::string why;
            ws_session(fd, left) ? why = "已停止" : why = last_error();
            ws_up_ = false;
            {
                std::lock_guard<std::mutex> lk(wmu_);
                ws_fd_ = -1;
                out_.clear();
            }
            close_fd(fd);
            ++reconnects_;
            l_->on_ws_closed(why);
            if (mono_ms() - t_up > 5000) attempt = 0;  // 连上过一阵子：重新从短间隔开始
        } else {
            close_fd(fd);
            std::lock_guard<std::mutex> lk(emu_);
            last_err_ = err;
        }
        if (stop_) break;
        int base = kBackoff[std::min(attempt, 5)];
        ++attempt;
        int jitter = static_cast<int>(static_cast<uint8_t>(random_bytes(1)[0]) % 41) - 20;  // ±20 %
        nap(wake_[0], base + base * jitter / 100, stop_);
    }
}

// 返回 true = 因为 stop 退出；false = 断线（原因写进 last_err_）
bool NetClient::ws_session(int fd, const std::string& leftover) {
    WsDecoder dec(false);
    auto fail = [&](const std::string& why) {
        std::lock_guard<std::mutex> lk(emu_);
        last_err_ = why;
        return false;
    };
    auto send = [&](const std::string& frame) {
        std::string e;
        if (!send_all(fd, frame.data(), frame.size(), mono_ms() + 5000, &e)) return fail("发送失败：" + e);
        return true;
    };
    if (!leftover.empty() && !dec.feed(leftover.data(), leftover.size())) return fail("WebSocket 协议错误：" + dec.error());
    int64_t last_rx = mono_ms(), last_ping = mono_ms();
    char buf[65536];
    for (;;) {
        // 先把已经收到的消息处理完
        WsMsg m;
        while (dec.next(m)) {
            switch (m.op) {
                case WsOp::Text: l_->on_ws_text(m.data); break;
                case WsOp::Binary: break;
                case WsOp::Ping: if (!send(ws_frame(WsOp::Pong, m.data))) return false; break;
                case WsOp::Pong: break;
                case WsOp::Close:
                    send(ws_frame(WsOp::Close, ws_close_payload(m.close_code == 1005 ? 1000 : m.close_code, "")));
                    return fail("后台关闭了连接（" + std::to_string(m.close_code) + (m.data.empty() ? "" : " " + m.data) + "）");
                default: break;
            }
        }
        if (stop_) {
            send(ws_frame(WsOp::Close, ws_close_payload(1001, "bye")));
            return true;
        }
        if (kick_) {
            kick_ = false;
            send(ws_frame(WsOp::Close, ws_close_payload(1000, "resync")));
            return fail("主动重连");
        }
        std::deque<std::string> out;
        {
            std::lock_guard<std::mutex> lk(wmu_);
            out.swap(out_);
        }
        for (auto& t : out)
            if (!send(ws_frame(WsOp::Text, t))) return false;
        int64_t now = mono_ms();
        if (now - last_rx > 15000) return fail("15 s 没有收到后台的数据");
        if (now - last_ping >= 5000) {
            last_ping = now;
            if (!send(ws_frame(WsOp::Ping, ""))) return false;
        }
        pollfd p[2] = {{fd, POLLIN, 0}, {wake_[0], POLLIN, 0}};
        int r = ::poll(p, wake_[0] >= 0 ? 2 : 1, 500);
        if (r < 0 && errno != EINTR) return fail("poll 失败");
        if (r <= 0) continue;
        if (wake_[0] >= 0 && (p[1].revents & POLLIN)) {
            char b[64];
            while (::read(wake_[0], b, sizeof b) > 0) {
            }
        }
        if (p[0].revents & (POLLIN | POLLHUP | POLLERR)) {
            for (;;) {
                long n = recv_some(fd, buf, sizeof buf, 0);
                if (n == -2) break;
                if (n == 0) return fail("后台断开了连接");
                if (n < 0) return fail("接收失败");
                last_rx = mono_ms();
                if (!dec.feed(buf, static_cast<size_t>(n))) {
                    send(ws_frame(WsOp::Close, ws_close_payload(1002, "protocol")));
                    return fail("WebSocket 协议错误：" + dec.error());
                }
                if (static_cast<size_t>(n) < sizeof buf) break;
            }
        }
    }
}

void NetClient::lane_loop(Lane which) {
    auto& l = lanes_[which == Lane::Read ? 0 : 1];
    HttpConn conn(ep_, token_);
    int64_t last_tick = 0;
    while (!stop_) {
        Call c;
        bool have = false;
        {
            std::unique_lock<std::mutex> lk(l.mu);
            l.cv.wait_for(lk, std::chrono::milliseconds(250), [&] { return stop_ || !l.q.empty(); });
            if (stop_) break;
            if (!l.q.empty()) {
                c = std::move(l.q.front());
                l.q.pop_front();
                have = true;
            }
        }
        int64_t now = mono_ms();
        if (which == Lane::Read && now - last_tick >= 250) {
            last_tick = now;
            l_->on_tick(now);
        }
        if (!have) continue;
        HttpRequest r;
        r.method = c.method;
        r.target = "/api/v1" + c.path;
        r.body = c.body_fn ? c.body_fn() : c.body;
        r.timeout_ms = c.timeout_ms;
        Reply rep;
        if (c.idem) {
            rep.idem_key = uuid4();
            r.headers.emplace_back("Idempotency-Key", rep.idem_key);
        }
        HttpResponse res;
        std::string err;
        rep.transport_ok = conn.exec(r, res, &err);
        rep.err = err;
        rep.status = res.status;
        rep.body = std::move(res.body);
        if (!rep.body.empty() && (rep.body[0] == '{' || rep.body[0] == '[')) {
            try {
                rep.json = json::parse(rep.body);
            } catch (const std::exception&) {
                rep.json = nullptr;
            }
        }
        if (c.done) c.done(rep);
    }
}

}  // namespace reactor::hmi::net
