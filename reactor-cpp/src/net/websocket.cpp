#include "net/websocket.hpp"

#include "net/crypto.hpp"
#include <cstdint>
#include <string>
#include <vector>

namespace reactor::net::ws {

std::string accept_key(const std::string& client_key) {
    auto h = sha1(client_key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11");
    return base64_encode(h.data(), h.size());
}

bool valid_client_key(const std::string& key) {
    auto d = base64_decode(key);
    return d && d->size() == 16;
}

std::string encode_frame_bytes(uint8_t opcode, const char* data, size_t len, bool fin, const uint8_t* mask) {
    std::string f;
    f.reserve(len + 14);
    f += static_cast<char>((fin ? 0x80 : 0x00) | (opcode & 0x0F));
    uint8_t mbit = mask ? 0x80 : 0x00;
    if (len < 126) {
        f += static_cast<char>(mbit | len);
    } else if (len <= 0xFFFF) {
        f += static_cast<char>(mbit | 126);
        f += static_cast<char>((len >> 8) & 0xFF);
        f += static_cast<char>(len & 0xFF);
    } else {
        f += static_cast<char>(mbit | 127);
        for (int i = 7; i >= 0; --i) f += static_cast<char>((static_cast<uint64_t>(len) >> (8 * i)) & 0xFF);
    }
    if (mask) {
        f.append(reinterpret_cast<const char*>(mask), 4);
        size_t base = f.size();
        f.append(data, len);
        for (size_t i = 0; i < len; ++i) f[base + i] = static_cast<char>(f[base + i] ^ mask[i & 3]);
    } else {
        f.append(data, len);
    }
    return f;
}

std::string encode_close(uint16_t code, const std::string& reason, const uint8_t* mask) {
    std::string p;
    p += static_cast<char>(code >> 8);
    p += static_cast<char>(code & 0xFF);
    p += reason.substr(0, 123);
    return encode_frame(kClose, p, true, mask);
}

bool valid_utf8(const char* s, size_t len) {
    const unsigned char* p = reinterpret_cast<const unsigned char*>(s);
    size_t i = 0;
    while (i < len) {
        unsigned char c = p[i];
        if (c < 0x80) { ++i; continue; }
        size_t n;
        uint32_t cp;
        if ((c & 0xE0) == 0xC0) { n = 1; cp = c & 0x1F; }
        else if ((c & 0xF0) == 0xE0) { n = 2; cp = c & 0x0F; }
        else if ((c & 0xF8) == 0xF0) { n = 3; cp = c & 0x07; }
        else return false;
        if (i + n >= len) return false;
        for (size_t k = 1; k <= n; ++k) {
            if (i + k >= len) return false;
            if ((p[i + k] & 0xC0) != 0x80) return false;
            cp = (cp << 6) | (p[i + k] & 0x3F);
        }
        if ((n == 1 && cp < 0x80) || (n == 2 && cp < 0x800) || (n == 3 && cp < 0x10000)) return false;   // 过长编码
        if (cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF)) return false;
        i += n + 1;
    }
    return true;
}

bool valid_close_code(uint16_t c) {
    if (c >= 3000 && c <= 4999) return true;
    return c == 1000 || c == 1001 || c == 1002 || c == 1003 || (c >= 1007 && c <= 1011);
}

int parse_frame(const char* p, size_t len, size_t max_payload, Frame& out, size_t& consumed, uint16_t& err) {
    if (len < 2) return 0;
    uint8_t b0 = static_cast<uint8_t>(p[0]), b1 = static_cast<uint8_t>(p[1]);
    if (b0 & 0x70) { err = kCloseProtocolError; return -1; }   // RSV 位（没有协商扩展）
    out.fin = (b0 & 0x80) != 0;
    out.opcode = b0 & 0x0F;
    out.masked = (b1 & 0x80) != 0;
    uint64_t plen = b1 & 0x7F;
    size_t pos = 2;
    if (plen == 126) {
        if (len < 4) return 0;
        plen = (uint64_t(uint8_t(p[2])) << 8) | uint8_t(p[3]);
        pos = 4;
    } else if (plen == 127) {
        if (len < 10) return 0;
        plen = 0;
        for (int i = 0; i < 8; ++i) plen = (plen << 8) | uint8_t(p[2 + i]);
        if (plen >> 63) { err = kCloseProtocolError; return -1; }
        pos = 10;
    }
    bool control = (out.opcode & 0x08) != 0;
    if (control && (plen > 125 || !out.fin)) { err = kCloseProtocolError; return -1; }
    if (plen > max_payload) { err = kCloseMessageTooBig; return -1; }
    uint8_t mask[4] = {0, 0, 0, 0};
    if (out.masked) {
        if (len < pos + 4) return 0;
        for (int i = 0; i < 4; ++i) mask[i] = static_cast<uint8_t>(p[pos + i]);
        pos += 4;
    }
    if (len - pos < plen) return 0;
    out.payload.assign(p + pos, static_cast<size_t>(plen));
    if (out.masked)
        for (size_t i = 0; i < out.payload.size(); ++i) out.payload[i] = static_cast<char>(out.payload[i] ^ mask[i & 3]);
    consumed = pos + static_cast<size_t>(plen);
    return 1;
}

bool MessageAssembler::feed(std::string& buf, std::vector<Event>& out) {
    size_t off = 0;
    auto fail = [&](uint16_t code, const char* why) {
        Event e;
        e.kind = Event::Error;
        e.code = code;
        e.data = why;
        out.push_back(std::move(e));
        buf.erase(0, off);
        return false;
    };
    while (off < buf.size()) {
        Frame f;
        size_t used = 0;
        uint16_t err = 0;
        int rc = parse_frame(buf.data() + off, buf.size() - off, max_, f, used, err);
        if (rc == 0) break;
        if (rc < 0) return fail(err, err == kCloseMessageTooBig ? "消息过大" : "帧格式错误");
        off += used;
        if (require_mask_ && !f.masked) return fail(kCloseProtocolError, "客户端帧未加掩码");
        switch (f.opcode) {
            case kText:
            case kBinary:
                if (in_message_) return fail(kCloseProtocolError, "分片消息未结束又开始新消息");
                if (f.fin) {
                    if (f.opcode == kText && !valid_utf8(f.payload.data(), f.payload.size()))
                        return fail(kCloseInvalidData, "文本消息不是合法 UTF-8");
                    Event e;
                    e.binary = f.opcode == kBinary;
                    e.data = std::move(f.payload);
                    out.push_back(std::move(e));
                } else {
                    in_message_ = true;
                    msg_binary_ = f.opcode == kBinary;
                    msg_ = std::move(f.payload);
                }
                break;
            case kContinuation:
                if (!in_message_) return fail(kCloseProtocolError, "没有起始帧的续帧");
                if (msg_.size() + f.payload.size() > max_) return fail(kCloseMessageTooBig, "消息过大");
                msg_ += f.payload;
                if (f.fin) {
                    if (!msg_binary_ && !valid_utf8(msg_.data(), msg_.size()))
                        return fail(kCloseInvalidData, "文本消息不是合法 UTF-8");
                    Event e;
                    e.binary = msg_binary_;
                    e.data = std::move(msg_);
                    msg_.clear();
                    in_message_ = false;
                    out.push_back(std::move(e));
                }
                break;
            case kPing:
            case kPong: {
                Event e;
                e.kind = f.opcode == kPing ? Event::Ping : Event::Pong;
                e.data = std::move(f.payload);
                out.push_back(std::move(e));
                break;
            }
            case kClose: {
                Event e;
                e.kind = Event::Close;
                if (f.payload.size() == 1) return fail(kCloseProtocolError, "关闭帧长度错误");
                if (f.payload.size() >= 2) {
                    e.code = static_cast<uint16_t>((uint8_t(f.payload[0]) << 8) | uint8_t(f.payload[1]));
                    if (!valid_close_code(e.code)) return fail(kCloseProtocolError, "关闭码不合法");
                    e.data = f.payload.substr(2);
                    if (!valid_utf8(e.data.data(), e.data.size())) return fail(kCloseInvalidData, "关闭原因不是合法 UTF-8");
                } else {
                    e.code = kCloseNoStatus;
                }
                out.push_back(std::move(e));
                buf.erase(0, off);
                return true;   // 关闭之后的字节不再处理
            }
            default:
                return fail(kCloseProtocolError, "未知操作码");
        }
    }
    buf.erase(0, off);
    return true;
}

}  // namespace reactor::net::ws
