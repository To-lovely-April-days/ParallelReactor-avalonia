// HTTP/1.1 的数据结构和纯函数：请求 / 响应、头（大小写不敏感）、URL 解码、请求解析、文件响应（Range + 弱 ETag）。
// 服务器本身见 server.hpp。本文件除 Responder / StreamWriter（实现在 server.cpp）外都是纯逻辑、线程安全。
#pragma once
#include <cstdint>
#include <functional>
#include <memory>
#include <optional>
#include <string>
#include <unordered_map>
#include <utility>
#include <vector>

#include "net/auth.hpp"
#include "net/cidr.hpp"
#include "net/rate_limit.hpp"
#include "nlohmann/json.hpp"

namespace reactor::net {

inline constexpr const char* kJsonContentType = "application/json; charset=utf-8";

// —— 头 ——（保持插入顺序；名字比较不区分大小写）
class Headers {
public:
    void add(std::string name, std::string value);
    void set(const std::string& name, std::string value);   // 替换同名的全部
    void remove(const std::string& name);
    const std::string* find(const std::string& name) const;
    std::string get(const std::string& name, const std::string& def = "") const;
    bool has(const std::string& name) const { return find(name) != nullptr; }
    // 逗号分隔的列表里是否含 token（不区分大小写），如 Connection: keep-alive, Upgrade
    bool has_token(const std::string& name, const std::string& token) const;
    const std::vector<std::pair<std::string, std::string>>& items() const { return items_; }

private:
    std::vector<std::pair<std::string, std::string>> items_;
};

bool iequals(const std::string& a, const std::string& b);

// —— 对端 ——
enum class Transport { Tcp, Unix };

struct PeerInfo {
    Transport transport = Transport::Tcp;
    std::optional<IpAddr> ip;   // TCP：已规范化（v4-mapped → v4）；Unix：无
    std::string ip_text;        // TCP："192.168.1.5" / "fd00::1"；Unix："local"
    uint16_t port = 0;
    int64_t uid = -1;           // Unix：对端进程的 uid / pid（Linux SO_PEERCRED；macOS getpeereid + LOCAL_PEERPID）
    int64_t pid = -1;
    int64_t gid = -1;
};

// —— 请求 ——
struct Request {
    std::string method;        // 原样（大写）
    std::string target;        // 请求行里的原文
    std::string raw_path;      // 未解码路径
    std::string path;          // 百分号解码后的路径
    std::string query_string;  // ? 之后（未解码）
    std::vector<std::pair<std::string, std::string>> query;   // 解码后的查询参数（保持顺序，可重复）
    int version_minor = 1;     // HTTP/1.x
    Headers headers;
    std::string body;

    // 由服务器填写
    std::unordered_map<std::string, std::string> params;   // 路由参数 {id}（已解码）
    PeerInfo peer;
    AuthContext auth;          // 已通过网关的身份（no_auth 路由上为 denied 或 local）
    RateClass rate_class = RateClass::None;   // 实际生效的限流分区
    uint64_t conn_id = 0;
    int64_t received_mono_ms = 0;

    std::string header(const std::string& name, const std::string& def = "") const { return headers.get(name, def); }
    std::string param(const std::string& name, const std::string& def = "") const;
    bool has_query(const std::string& name) const;
    std::string query_param(const std::string& name, const std::string& def = "") const;
    bool keep_alive() const;   // HTTP/1.1 默认保持；HTTP/1.0 需 keep-alive
};

// —— 响应 ——
struct FileBody {   // 由 I/O 线程按块读出发送；析构时关闭 fd
    int fd = -1;
    uint64_t offset = 0, length = 0;
    FileBody(int f, uint64_t off, uint64_t len) : fd(f), offset(off), length(len) {}
    ~FileBody();
    FileBody(const FileBody&) = delete;
    FileBody& operator=(const FileBody&) = delete;
};

struct Response {
    int status = 200;
    Headers headers;
    std::string body;
    std::shared_ptr<FileBody> file;   // 非空时正文取文件，body 被忽略

    static Response json(int status, std::string body);
    static Response json(int status, const nlohmann::json& j);
    static Response text(int status, std::string body, const std::string& content_type = "text/plain; charset=utf-8");
    static Response empty(int status);
    // 统一错误 {ok:false,error:{code,message,detail?}}；status_override=0 时用 status_of(code)；
    // RATE_LIMITED / DOWNLOAD_BUSY 自动带 Retry-After: 1（C#: ApiErrors.WriteAsync）
    static Response error(const std::string& code, const std::string& message,
                          const nlohmann::json& detail = nlohmann::json(), int status_override = 0);
};

const char* status_text(int status);
std::string http_date(int64_t unix_sec);   // RFC 1123："Thu, 26 Nov 2026 08:02:15 GMT"
std::optional<int64_t> parse_http_date(const std::string& s);

// —— URL ——
// 百分号解码；plus_as_space 用于查询串；非法 %xx 原样保留
std::string url_decode(const std::string& s, bool plus_as_space);
std::string url_encode_component(const std::string& s);   // RFC 3986 unreserved 之外全部 %XX
std::vector<std::pair<std::string, std::string>> parse_query(const std::string& qs);

// —— 请求解析 ——（无状态：每次给出从请求开头起的全部已收字节）
struct ParseLimits {
    size_t max_header_bytes = 16 * 1024;
    size_t max_body_bytes = 64 * 1024;   // C#: Kestrel MaxRequestBodySize = 64 KB
};

struct ParseResult {
    enum Kind { Incomplete, Complete, Error } kind = Incomplete;
    bool headers_complete = false;   // Incomplete 时：头已收齐（用于 Expect: 100-continue）
    bool expect_continue = false;
    size_t consumed = 0;             // Complete：本请求占用的字节数（后面可能是下一个流水线请求）
    int status = 0;                  // Error：HTTP 状态
    std::string code, message;       // Error：错误码 / 文案
};

// 解析一个请求（Content-Length 或 chunked 正文）。Complete 时填 out（不含服务器字段）。
ParseResult parse_request(const char* data, size_t len, const ParseLimits& lim, Request& out);

// 序列化响应头。chunked = true 写 Transfer-Encoding: chunked；否则 body_length >= 0 写 Content-Length，< 0 不写（以断开为结束）。
// 1xx / 204 / 304 不写长度。用户头里的 Content-Length / Transfer-Encoding / Connection / Date 被忽略，由这里统一写。
std::string serialize_head(const Response& r, int64_t body_length, bool keep_alive, bool chunked);

// —— 文件下载 ——（C#: Endpoints.FileResult —— Range、弱 ETag = 长度 + 修改时间、Last-Modified、Content-Disposition）
// 文件不存在 / 不是普通文件 → 404 NOT_FOUND「文件不存在或名称不合法」；If-None-Match 命中 → 304；
// Range: bytes=a-b | a- | -n（单段）→ 206 + Content-Range；起点越界 → 416；多段 / 语法错 → 忽略（200 全文）；
// If-Range 与 ETag 不符 → 忽略 Range。HEAD 由服务器去掉正文。download_name 为空则不写 Content-Disposition。
Response file_response(const Request& req, const std::string& path, const std::string& content_type,
                       const std::string& download_name = "");
// 弱 ETag：W/"<长度hex>-<mtime .NET ticks hex>"（与 C# 同格式）
std::string weak_etag(uint64_t length, int64_t mtime_unix_ns);

// —— 处理器的回复句柄 ——（实现在 server.cpp）
namespace detail { struct Exchange; struct StreamState; }

// 流式写出（大文件拼接、CSV 流）：在工作线程上 write()，缓冲超过高水位时阻塞等 I/O 线程发走（背压）。
// 析构时若没 end()，连接按「响应不完整」直接关闭。任意线程可用，但同一时刻只应有一个线程写。
class StreamWriter {
public:
    explicit StreamWriter(std::shared_ptr<detail::StreamState> st) : st_(std::move(st)) {}
    ~StreamWriter();
    StreamWriter(const StreamWriter&) = delete;
    StreamWriter& operator=(const StreamWriter&) = delete;
    // 写一块；连接已断 / 发送超时（10 s 无进展）返回 false，之后的写都返回 false
    bool write(const char* data, size_t len);
    bool write(const std::string& s) { return write(s.data(), s.size()); }
    // 结束（chunked 写终止块）。Content-Length 模式下写的总字节不等于声明长度 → 连接关闭，返回 false
    bool end();
    bool closed() const;

private:
    std::shared_ptr<detail::StreamState> st_;
};

// 一次请求的回复句柄：可复制，可跨线程，可以稍后（在任意线程）再回复 —— 即「延迟响应」。
// 只有第一次 send / stream 生效。所有副本都销毁仍未回复 → 服务器自动回 500 INTERNAL。
class Responder {
public:
    Responder() = default;
    explicit Responder(std::shared_ptr<detail::Exchange> ex) : ex_(std::move(ex)) {}
    bool send(Response r) const;   // false：已回复过或连接已断
    // 开始流式响应。content_length < 0 → chunked；>= 0 → 固定长度。失败返回 nullptr。
    std::unique_ptr<StreamWriter> stream(int status, Headers headers, int64_t content_length = -1) const;
    bool connection_closed() const;   // 对端已断（可用于提前放弃慢操作）
    bool valid() const { return ex_ != nullptr; }

private:
    std::shared_ptr<detail::Exchange> ex_;
};

}  // namespace reactor::net
