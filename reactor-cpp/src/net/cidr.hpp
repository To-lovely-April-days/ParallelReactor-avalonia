// 网段白名单：IPv4 / IPv6 CIDR；::ffff:a.b.c.d 按 IPv4 判定；列表为空只允许回环；写错的条目忽略并列出。
// C#: Api/Cidr.cs。不可变，线程安全。
#pragma once
#include <cstdint>
#include <optional>
#include <string>
#include <vector>

namespace reactor::net {

// 4 或 16 字节；IPv4 映射的 IPv6 已折成 4 字节
struct IpAddr {
    std::vector<uint8_t> bytes;
    bool is_v4() const { return bytes.size() == 4; }
    bool is_loopback() const;   // 127.0.0.0/8 或 ::1
};

// 解析地址文本（"10.0.0.5"、"fd00::1"、"::ffff:1.2.3.4"、"[::1]"、带 %scope 的 v6 去掉 scope）
std::optional<IpAddr> parse_ip(const std::string& s);
// 把 sockaddr 里取出的原始 v4 / v6 字节规范化（v4-mapped → v4）
IpAddr normalize_ip(const IpAddr& ip);
std::string ip_to_string(const IpAddr& ip);

class CidrMatcher {
public:
    explicit CidrMatcher(const std::vector<std::string>& cidrs);
    // 解析 "a.b.c.d/24"、"10.0.0.5"（= /32）、"fd00::/8"
    static bool try_parse(const std::string& s, IpAddr& net, int& bits);
    // 空地址（无远端地址）按回环处理 → 允许（C#: ip == null → true）
    bool is_allowed(const std::optional<IpAddr>& ip) const;
    const std::vector<std::string>& invalid() const { return invalid_; }
    size_t count() const { return nets_.size(); }

private:
    std::vector<std::pair<IpAddr, int>> nets_;
    std::vector<std::string> invalid_;
};

}  // namespace reactor::net
