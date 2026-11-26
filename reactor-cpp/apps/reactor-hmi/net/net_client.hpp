// 连接 reactord 的传输层：三个后台线程，LVGL 线程一个都不碰。
//
//   ws 线程     连接 → WebSocket 握手 → 收消息（交给 Listener::on_ws_text）；断了按退避重连
//               （0.25 → 0.5 → 1 → 2 → 4 → 5 s 封顶，±20 % 抖动）。15 s 没收到任何字节（服务端每 5 s 发保活
//               Pong、每 1.2 s 发遥测）算断线；每 5 s 发一个 Ping 帧。
//   read 线程   GET（刷新快照以外的数据），keep-alive 一条连接，按顺序执行；顺带每 250 ms 调一次 Listener::on_tick。
//   cmd 线程    写命令，单独一条连接：不会排在大的读请求后面。urgent（全部停止）插到队头。
//
// 回调（ReplyFn、Listener）都在网络线程上调；要碰界面的，经 UiQueue 投递回 LVGL 线程。
#pragma once
#include "http_client.hpp"
#include "nlohmann/json.hpp"

#include <atomic>
#include <condition_variable>
#include <deque>
#include <functional>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace reactor::hmi::net {

// 网络线程 → LVGL 线程的回调队列。post 任意线程；drain 只在 LVGL 线程（主循环每帧）调。
class UiQueue {
public:
    void post(std::function<void()> fn) {
        std::lock_guard<std::mutex> lk(mu_);
        q_.push_back(std::move(fn));
    }
    void drain() {
        std::vector<std::function<void()>> run;
        {
            std::lock_guard<std::mutex> lk(mu_);
            run.swap(q_);
        }
        for (auto& f : run) f();
    }

private:
    std::mutex mu_;
    std::vector<std::function<void()>> q_;
};

struct Reply {
    bool transport_ok = false;   // false：连不上 / 超时 / 断开（err 是原因）
    int status = 0;
    std::string body, err;
    nlohmann::json json;         // 正文是 JSON 时解析好的（否则 null）
    std::string idem_key;        // 这条请求用的 Idempotency-Key（= opId）
    bool ok() const { return transport_ok && status >= 200 && status < 300; }
    // 失败时给人看的中文：服务端 error.message；没有就按状态 / 传输错误拼一句
    std::string message() const;
    std::string code() const;    // error.code（没有为空）
};
using ReplyFn = std::function<void(const Reply&)>;

enum class Lane { Read, Command };

struct Call {
    std::string method = "GET", path;   // path 不带 /api/v1 前缀，例如 "/settings"
    std::string body;                   // 空 = 没有正文
    std::function<std::string()> body_fn;  // 不空时在发送前（网络线程）才生成正文（PATCH 要用最新的 revision）
    bool idem = false;                  // 带 Idempotency-Key（网络层给全部写命令都带）
    bool urgent = false;                // 插到队头（全部停止）
    int timeout_ms = 10000;
    ReplyFn done;
};

class NetClient {
public:
    struct Listener {
        virtual ~Listener() = default;
        virtual void on_ws_open() = 0;                        // 握手成功（还没收到 hello）
        virtual void on_ws_text(const std::string& text) = 0;
        virtual void on_ws_closed(const std::string& why) = 0;
        virtual void on_tick(int64_t mono) = 0;               // read 线程，约每 250 ms
    };
    NetClient(Endpoint ep, std::string token, Listener* l);
    ~NetClient();
    void start();
    void stop();
    void call(Lane lane, Call c);
    bool ws_send(const std::string& text);    // 任意线程；没连上返回 false
    bool ws_up() const { return ws_up_; }
    int reconnects() const { return reconnects_; }
    std::string last_error() const;
    const Endpoint& endpoint() const { return ep_; }
    void force_reconnect();                   // 断开 WS（自检、gap 处理失败时用）

private:
    struct LaneState {
        std::mutex mu;
        std::condition_variable cv;
        std::deque<Call> q;
        std::thread th;
    };
    void ws_loop();
    void lane_loop(Lane which);
    bool ws_session(int fd, const std::string& leftover);
    Endpoint ep_;
    std::string token_;
    Listener* l_;
    std::atomic<bool> stop_{false}, ws_up_{false}, kick_{false};
    std::atomic<int> reconnects_{0};
    std::thread ws_th_;
    LaneState lanes_[2];
    std::mutex wmu_;          // 保护 ws_fd_ 写和 out_
    int ws_fd_ = -1;
    int wake_[2] = {-1, -1};  // 让 ws 线程从 poll 里醒来（有消息要发 / 要停）
    std::deque<std::string> out_;
    mutable std::mutex emu_;
    std::string last_err_;
};

}  // namespace reactor::hmi::net
