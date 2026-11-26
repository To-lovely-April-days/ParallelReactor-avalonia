// 自检用的假 reactord：Unix socket 上的 HTTP/1.1 + WebSocket，只够网络层自检用（--selftest-net）。
// 不是后端，不做鉴权 / 限流；每条连接一个线程。
//   - HTTP：处理器返回 FakeResp；正文按 mode 发：定长 / chunked（随机切块、带 chunk 扩展和尾部头）/ 读到断开为止。
//     每条请求记进 log（方法、路径、头、正文），测试按它核对 NetActions 打到了哪个端点。
//   - WS：握手（核对 Sec-WebSocket-Key → Accept）；连上先发 hello；收到的文本消息记进 ws_log；
//     push() 可以整条发，也可以拆成几片、中间夹一个 Ping；客户端的 Ping 回 Pong。
#pragma once
#include "http_client.hpp"

#include <atomic>
#include <condition_variable>
#include <functional>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace reactor::hmi::net {

struct FakeReq {
    std::string method, target, body;
    Headers headers;
    const std::string* header(const std::string& n) const { return find_header(headers, n); }
    std::string path() const { return target.substr(0, target.find('?')); }
};
struct FakeResp {
    int status = 200;
    std::string body = "{}";
    enum Mode { Length, Chunked, Close } mode = Length;
};

class FakeServer {
public:
    using Handler = std::function<FakeResp(const FakeReq&)>;
    explicit FakeServer(std::string path) : path_(std::move(path)) {}
    ~FakeServer() { stop(); }
    bool start(Handler h);
    void stop();                                      // 关监听、断开全部连接、删 socket 文件
    // WS
    bool push(const std::string& text, int fragments = 1, bool ping_between = false);  // 发给所有 WS 连接
    void push_raw(const std::string& bytes);          // 原样发帧（测坏帧用）
    void kill_ws();                                   // 直接断开 WS（模拟后台重启 / 网络断）
    int ws_clients() const { return ws_count_; }
    int ws_connects() const { return ws_connects_; }
    int http_connects() const { return http_connects_; }
    std::vector<std::string> ws_log() const;           // 客户端发来的文本消息
    std::vector<FakeReq> log() const;
    void clear_log();
    bool wait_for(const std::function<bool()>& pred, int timeout_ms) const;
    std::string hello = R"({"type":"hello","apiVersion":"1.0","serverTime":"2026-11-26T06:00:00Z","clockSynced":true,"bootId":"b1","simulated":true,"seqEpoch":1,"eventSeqHead":0,"telemetrySeqHead":0,"control":{"control":"local","holder":null},"eventRingSize":5000})";
    std::atomic<int> pongs{0};

private:
    void accept_loop();
    void conn_loop(int fd);
    void ws_loop(int fd, std::string leftover);
    std::string path_;
    Handler h_;
    int lfd_ = -1;
    std::atomic<bool> stop_{false};
    std::thread acc_;
    mutable std::mutex mu_;
    std::vector<std::thread> conns_;
    std::vector<int> fds_, ws_fds_;
    std::vector<FakeReq> log_;
    std::vector<std::string> ws_log_;
    std::atomic<int> ws_count_{0}, ws_connects_{0}, http_connects_{0};
    std::mutex send_mu_;
};

}  // namespace reactor::hmi::net
