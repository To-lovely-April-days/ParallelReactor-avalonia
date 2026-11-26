#include "ws_client.hpp"

#include "wire.hpp"

#include <cstring>

namespace reactor::hmi::net {

std::string ws_accept(const std::string& key) {
    auto h = sha1(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11");
    return base64(h.data(), h.size());
}

std::string ws_frame(WsOp op, const std::string& payload, bool fin, bool mask) {
    std::string f;
    size_t n = payload.size();
    f.reserve(n + 14);
    f.push_back(static_cast<char>((fin ? 0x80 : 0) | static_cast<uint8_t>(op)));
    uint8_t mbit = mask ? 0x80 : 0;
    if (n < 126) f.push_back(static_cast<char>(mbit | n));
    else if (n < 65536) {
        f.push_back(static_cast<char>(mbit | 126));
        f.push_back(static_cast<char>((n >> 8) & 0xFF));
        f.push_back(static_cast<char>(n & 0xFF));
    } else {
        f.push_back(static_cast<char>(mbit | 127));
        for (int i = 7; i >= 0; --i) f.push_back(static_cast<char>((static_cast<uint64_t>(n) >> (8 * i)) & 0xFF));
    }
    if (!mask) return f + payload;
    std::string key = random_bytes(4);
    f += key;
    size_t base = f.size();
    f += payload;
    for (size_t i = 0; i < n; ++i) f[base + i] = static_cast<char>(f[base + i] ^ key[i & 3]);
    return f;
}

std::string ws_close_payload(uint16_t code, const std::string& reason) {
    std::string p;
    p.push_back(static_cast<char>(code >> 8));
    p.push_back(static_cast<char>(code & 0xFF));
    return p + reason.substr(0, 123);
}

bool WsDecoder::feed(const char* p, size_t n) {
    if (!err_.empty()) return false;
    buf_.append(p, n);
    return parse();
}

bool WsDecoder::next(WsMsg& out) {
    if (ready_.empty()) return false;
    out = std::move(ready_.front());
    ready_.pop_front();
    return true;
}

bool WsDecoder::parse() {
    for (;;) {
        if (buf_.size() < 2) return true;
        auto b = [&](size_t i) { return static_cast<uint8_t>(buf_[i]); };
        bool fin = b(0) & 0x80;
        if (b(0) & 0x70) { err_ = "RSV 位不为 0"; return false; }
        auto op = static_cast<WsOp>(b(0) & 0x0F);
        bool masked = b(1) & 0x80;
        uint64_t len = b(1) & 0x7F;
        size_t hdr = 2;
        if (len == 126) {
            if (buf_.size() < 4) return true;
            len = (uint64_t(b(2)) << 8) | b(3);
            hdr = 4;
        } else if (len == 127) {
            if (buf_.size() < 10) return true;
            len = 0;
            for (int i = 0; i < 8; ++i) len = (len << 8) | b(static_cast<size_t>(2 + i));
            hdr = 10;
        }
        if (masked != masked_) { err_ = masked ? "服务端的帧不应加掩码" : "客户端的帧必须加掩码"; return false; }
        bool control = static_cast<uint8_t>(op) >= 8;
        if (control && (!fin || len > 125)) { err_ = "控制帧不合法"; return false; }
        if (len > max_message) { err_ = "消息过大"; return false; }
        size_t need = hdr + (masked ? 4 : 0) + static_cast<size_t>(len);
        if (buf_.size() < need) return true;
        std::string payload = buf_.substr(hdr + (masked ? 4 : 0), static_cast<size_t>(len));
        if (masked) {
            const char* k = buf_.data() + hdr;
            for (size_t i = 0; i < payload.size(); ++i) payload[i] = static_cast<char>(payload[i] ^ k[i & 3]);
        }
        buf_.erase(0, need);
        switch (op) {
            case WsOp::Ping:
            case WsOp::Pong:
                ready_.push_back(WsMsg{op, std::move(payload), 0});
                break;
            case WsOp::Close: {
                WsMsg m{op, {}, 1005};
                if (payload.size() >= 2) {
                    m.close_code = static_cast<uint16_t>((static_cast<uint8_t>(payload[0]) << 8) | static_cast<uint8_t>(payload[1]));
                    m.data = payload.substr(2);
                } else if (payload.size() == 1) {
                    err_ = "关闭帧长度不对";
                    return false;
                }
                ready_.push_back(std::move(m));
                break;
            }
            case WsOp::Text:
            case WsOp::Binary:
                if (in_frag_) { err_ = "上一条分片消息还没完"; return false; }
                if (fin) ready_.push_back(WsMsg{op, std::move(payload), 0});
                else {
                    in_frag_ = true;
                    frag_op_ = op;
                    frag_ = std::move(payload);
                }
                break;
            case WsOp::Cont:
                if (!in_frag_) { err_ = "多出来的续帧"; return false; }
                if (frag_.size() + payload.size() > max_message) { err_ = "消息过大"; return false; }
                frag_ += payload;
                if (fin) {
                    in_frag_ = false;
                    ready_.push_back(WsMsg{frag_op_, std::move(frag_), 0});
                    frag_.clear();
                }
                break;
            default:
                err_ = "不认识的操作码";
                return false;
        }
    }
}

bool ws_handshake(int fd, const Endpoint& ep, const std::string& path, const std::string& token, int timeout_ms,
                  std::string* leftover, std::string* err) {
    int64_t deadline = mono_ms() + timeout_ms;
    std::string key = base64(random_bytes(16));
    HttpRequest r;
    r.method = "GET";
    r.target = path;
    r.headers = {{"Upgrade", "websocket"}, {"Connection", "Upgrade"}, {"Sec-WebSocket-Key", key}, {"Sec-WebSocket-Version", "13"}};
    std::string wire = build_request(ep, r, token);
    if (!send_all(fd, wire.data(), wire.size(), deadline, err)) return false;
    std::string got;
    char buf[4096];
    size_t end = std::string::npos;
    while ((end = got.find("\r\n\r\n")) == std::string::npos) {
        int64_t left = deadline - mono_ms();
        long n = left > 0 ? recv_some(fd, buf, sizeof buf, static_cast<int>(left)) : -2;
        if (n == -2) { if (err) *err = "WebSocket 握手超时"; return false; }
        if (n <= 0) { if (err) *err = "WebSocket 握手时连接断开"; return false; }
        got.append(buf, static_cast<size_t>(n));
        if (got.size() > 32768) { if (err) *err = "WebSocket 握手响应太长"; return false; }
    }
    HttpParser ps(HttpParser::Response);
    ps.reset(true);  // 101 没有正文
    size_t used = 0;
    if (ps.feed(got.data(), end + 4, &used) != 1) { if (err) *err = "握手响应格式不对"; return false; }
    const HttpResponse& res = ps.msg();
    if (res.status != 101) {
        if (err) *err = "WebSocket 握手被拒绝（HTTP " + std::to_string(res.status) + "）";
        return false;
    }
    const std::string* acc = res.header("Sec-WebSocket-Accept");
    if (!acc || *acc != ws_accept(key)) { if (err) *err = "Sec-WebSocket-Accept 对不上"; return false; }
    if (leftover) *leftover = got.substr(end + 4);
    return true;
}

}  // namespace reactor::hmi::net
