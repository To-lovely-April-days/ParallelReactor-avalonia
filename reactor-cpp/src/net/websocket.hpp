// WebSocket（RFC 6455）的纯编解码：握手 accept key、帧编码、帧解析、分片重组、UTF-8 校验。
// 服务器端的连接 / 队列 / 背压在 server.hpp 的 WsSession。纯逻辑，线程安全（MessageAssembler 实例除外）。
#pragma once
#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

namespace reactor::net::ws {

enum Opcode : uint8_t { kContinuation = 0x0, kText = 0x1, kBinary = 0x2, kClose = 0x8, kPing = 0x9, kPong = 0xA };

// 关闭码
inline constexpr uint16_t kCloseNormal = 1000;
inline constexpr uint16_t kCloseGoingAway = 1001;
inline constexpr uint16_t kCloseProtocolError = 1002;
inline constexpr uint16_t kCloseUnsupported = 1003;
inline constexpr uint16_t kCloseNoStatus = 1005;      // 只在本地表示「对方没带码」，不上线
inline constexpr uint16_t kCloseAbnormal = 1006;      // 只在本地表示「没有关闭握手就断了」
inline constexpr uint16_t kCloseInvalidData = 1007;
inline constexpr uint16_t kClosePolicyViolation = 1008;
inline constexpr uint16_t kCloseMessageTooBig = 1009;
inline constexpr uint16_t kCloseInternalError = 1011;

// base64(SHA-1(key + 258EAFA5-E914-47DA-95CA-C5AB0DC85B11))
std::string accept_key(const std::string& client_key);
// Sec-WebSocket-Key 是否合法（base64，解码后正好 16 字节）
bool valid_client_key(const std::string& key);

// 编码一帧（mask 非空 = 客户端帧，4 字节掩码）
std::string encode_frame_bytes(uint8_t opcode, const char* data, size_t len, bool fin = true, const uint8_t* mask = nullptr);
inline std::string encode_frame(uint8_t opcode, const std::string& s, bool fin = true, const uint8_t* mask = nullptr) {
    return encode_frame_bytes(opcode, s.data(), s.size(), fin, mask);
}
std::string encode_close(uint16_t code, const std::string& reason, const uint8_t* mask = nullptr);

bool valid_utf8(const char* p, size_t len);
// 关闭帧里允许出现的码（RFC 6455 §7.4）
bool valid_close_code(uint16_t code);

struct Frame {
    bool fin = true;
    uint8_t opcode = 0;
    bool masked = false;
    std::string payload;   // 已去掩码
};

// 从 p 解析一帧：1 = 成功（consumed 字节），0 = 不完整，-1 = 协议错误（err_code 为应回的关闭码）。
// 负载长度超过 max_payload 时，读完帧头就判 1009，不等负载收齐。
int parse_frame(const char* p, size_t len, size_t max_payload, Frame& out, size_t& consumed, uint16_t& err_code);

// 分片重组 + 控制帧处理。服务器用 require_mask = true（客户端帧必须带掩码）。
class MessageAssembler {
public:
    struct Event {
        enum Kind { Message, Ping, Pong, Close, Error } kind = Message;
        bool binary = false;
        std::string data;      // Message / Ping / Pong 负载；Close 的 reason；Error 的说明
        uint16_t code = 0;     // Close：对方的码（无码 = 1005）；Error：应回的关闭码
    };
    MessageAssembler(size_t max_message, bool require_mask) : max_(max_message), require_mask_(require_mask) {}
    // 吃掉 buf 里能解析的帧（解析掉的字节从 buf 头部删除），事件追加到 out。遇到 Error 停止并返回 false。
    bool feed(std::string& buf, std::vector<Event>& out);

private:
    size_t max_;
    bool require_mask_;
    bool in_message_ = false;
    bool msg_binary_ = false;
    std::string msg_;
};

}  // namespace reactor::net::ws
