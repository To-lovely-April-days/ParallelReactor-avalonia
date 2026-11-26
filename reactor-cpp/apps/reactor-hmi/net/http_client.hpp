// HTTP/1.1 客户端（Unix domain socket 或 TCP），阻塞调用 + 超时，只在网络线程里用。
//   - 地址：「unix:/run/reactord/hmi.sock」「/run/reactord/hmi.sock」「tcp://127.0.0.1:8080」
//   - 响应正文：Content-Length / chunked（带扩展、尾部头）/ 读到断开为止；HEAD、1xx、204、304 没有正文
//   - keep-alive 复用；复用的连接在收到任何字节之前就失败（对端已经关了空闲连接）时，重连再发一次。
//     网络层给每条写命令都带 Idempotency-Key，所以重发是安全的（服务端按键重放）。
#pragma once
#include <cstdint>
#include <string>
#include <utility>
#include <vector>

namespace reactor::hmi::net {

struct Endpoint {
    bool is_unix = true;
    std::string path = "/run/reactord/hmi.sock";  // Unix
    std::string host = "127.0.0.1";               // TCP
    int port = 8080;
    std::string text() const;                     // 日志 / 界面显示
};
bool parse_endpoint(const std::string& s, Endpoint& out, std::string* err = nullptr);

// ---- socket 小工具（WebSocket 也用）----
// 连接（非阻塞 connect + poll 等待），返回非阻塞 fd；失败 -1，err 写原因
int connect_to(const Endpoint& ep, int timeout_ms, std::string* err);
// 写完为止；deadline 是 mono_ms() 的绝对时刻
bool send_all(int fd, const char* p, size_t n, int64_t deadline, std::string* err);
// 读一次：>0 字节数；0 对端关闭；-1 出错；-2 超时
long recv_some(int fd, char* buf, size_t n, int timeout_ms);
void close_fd(int& fd);

using Headers = std::vector<std::pair<std::string, std::string>>;
// 大小写不敏感地找头；没有返回 nullptr
const std::string* find_header(const Headers& h, const std::string& name);

struct HttpResponse {
    int status = 0;
    std::string reason;
    Headers headers;
    std::string body;
    bool keep_alive = true;
    const std::string* header(const std::string& n) const { return find_header(headers, n); }
};

// 增量解析器（客户端收响应、假服务器收请求都用）
class HttpParser {
public:
    enum Kind { Response, Request };
    explicit HttpParser(Kind k = Response) : kind_(k) {}
    void reset(bool head_request = false);
    // 喂数据：返回 1 = 一条完整报文（*used 是用掉的字节，剩下的属于下一条）；0 = 还要更多；-1 = 格式错误（error()）
    int feed(const char* p, size_t n, size_t* used);
    // 对端关闭：正文是「读到断开为止」的这时才算完；返回 1 完整 / -1 不完整
    int finish_eof();
    HttpResponse& msg() { return res_; }
    // Request 模式：请求行
    const std::string& method() const { return method_; }
    const std::string& target() const { return target_; }
    const std::string& error() const { return err_; }
    bool started() const { return state_ != Start || !line_.empty(); }
    size_t max_body = 64u << 20;  // 64 MB（趋势、运行记录这类大一点的响应）

private:
    enum State { Start, Headers_, Body, ChunkSize, ChunkData, ChunkCrlf, Trailer, UntilClose, Done };
    bool line_complete(const char*& p, const char* end, std::string& line);
    bool on_start_line(const std::string& l);
    bool on_headers_done();
    Kind kind_;
    State state_ = Start;
    bool head_ = false;
    std::string line_, err_, method_, target_;
    HttpResponse res_;
    uint64_t remain_ = 0;
};

struct HttpRequest {
    std::string method = "GET", target;  // target 带 /api/v1 前缀和查询串
    Headers headers;                     // 额外的头（Idempotency-Key 等）
    std::string body;                    // 非空时 Content-Type: application/json
    int timeout_ms = 10000;
};

class HttpConn {
public:
    HttpConn(Endpoint ep, std::string token) : ep_(std::move(ep)), token_(std::move(token)) {}
    ~HttpConn() { close(); }
    HttpConn(const HttpConn&) = delete;
    HttpConn& operator=(const HttpConn&) = delete;
    // 阻塞执行（总超时 r.timeout_ms）。返回 false = 传输层失败（连不上、超时、断开、格式错），err 写中文原因
    bool exec(const HttpRequest& r, HttpResponse& out, std::string* err);
    void close();
    int64_t idle_since() const { return last_used_; }

private:
    bool once(const HttpRequest& r, HttpResponse& out, std::string* err, bool* got_bytes);
    Endpoint ep_;
    std::string token_;
    int fd_ = -1;
    int64_t last_used_ = 0;
    std::string pending_;   // 上一条响应之后多出来的字节（正常不会有）
};

std::string build_request(const Endpoint& ep, const HttpRequest& r, const std::string& token, bool keep_alive = true);

}  // namespace reactor::hmi::net
