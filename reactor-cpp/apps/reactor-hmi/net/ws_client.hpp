// WebSocket（RFC 6455）：帧编解码 + 客户端握手。只在网络线程里用。
//   客户端发的帧必须加掩码；服务端发的帧不能加掩码（加了按协议错误断开）。
//   解码器处理分片（中间可以夹控制帧）、125 字节以内的控制帧、关闭帧的状态码。
// 假服务器（自检）用同一套编解码，参数反过来（mask=false / expect_masked=true）。
#pragma once
#include "http_client.hpp"

#include <cstdint>
#include <deque>
#include <string>

namespace reactor::hmi::net {

enum class WsOp : uint8_t { Cont = 0, Text = 1, Binary = 2, Close = 8, Ping = 9, Pong = 10 };

std::string ws_accept(const std::string& client_key);   // base64(SHA-1(key + GUID))
std::string ws_frame(WsOp op, const std::string& payload, bool fin = true, bool mask = true);
std::string ws_close_payload(uint16_t code, const std::string& reason);

struct WsMsg {
    WsOp op = WsOp::Text;
    std::string data;        // Text / Binary 是整条消息（分片已拼好）；Close 是原因
    uint16_t close_code = 0;
};

class WsDecoder {
public:
    explicit WsDecoder(bool expect_masked = false) : masked_(expect_masked) {}
    // 喂数据；false = 协议错误（error()，调用方应以 1002 断开）
    bool feed(const char* p, size_t n);
    bool next(WsMsg& out);   // 取一条完整的消息
    const std::string& error() const { return err_; }
    size_t max_message = 16u << 20;

private:
    bool parse();
    bool masked_;
    std::string buf_, frag_, err_;
    bool in_frag_ = false;
    WsOp frag_op_ = WsOp::Text;
    std::deque<WsMsg> ready_;
};

// 客户端握手：在已连上的 fd 上发 Upgrade 请求、收 101、核对 Sec-WebSocket-Accept。
// leftover：101 响应后面已经收到的帧数据（交给解码器）。
bool ws_handshake(int fd, const Endpoint& ep, const std::string& path, const std::string& token, int timeout_ms,
                  std::string* leftover, std::string* err);

}  // namespace reactor::hmi::net
