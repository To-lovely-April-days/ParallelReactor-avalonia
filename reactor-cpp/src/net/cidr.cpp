#include "net/cidr.hpp"

#include <arpa/inet.h>
#include <netinet/in.h>

#include <cctype>
#include <cstring>

namespace reactor::net {

namespace {
std::string trim(const std::string& s) {
    size_t a = 0, b = s.size();
    while (a < b && std::isspace(static_cast<unsigned char>(s[a]))) ++a;
    while (b > a && std::isspace(static_cast<unsigned char>(s[b - 1]))) --b;
    return s.substr(a, b - a);
}
}  // namespace

bool IpAddr::is_loopback() const {
    if (bytes.size() == 4) return bytes[0] == 127;
    if (bytes.size() == 16) {
        for (int i = 0; i < 15; ++i)
            if (bytes[i]) return false;
        return bytes[15] == 1;
    }
    return false;
}

IpAddr normalize_ip(const IpAddr& ip) {
    if (ip.bytes.size() == 16) {
        static const uint8_t prefix[12] = {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xff, 0xff};
        if (std::memcmp(ip.bytes.data(), prefix, 12) == 0)
            return IpAddr{std::vector<uint8_t>(ip.bytes.begin() + 12, ip.bytes.end())};
    }
    return ip;
}

std::optional<IpAddr> parse_ip(const std::string& raw) {
    std::string s = trim(raw);
    if (s.size() >= 2 && s.front() == '[' && s.back() == ']') s = s.substr(1, s.size() - 2);
    auto pct = s.find('%');
    if (pct != std::string::npos) s = s.substr(0, pct);
    if (s.empty()) return std::nullopt;
    in_addr a4{};
    // inet_pton 的 IPv4 只认点分四段十进制（不认 "10.1" 这类旧式写法），比 .NET 的 IPAddress.TryParse 严格；白名单里这是好事
    if (inet_pton(AF_INET, s.c_str(), &a4) == 1) {
        IpAddr ip;
        ip.bytes.resize(4);
        std::memcpy(ip.bytes.data(), &a4, 4);
        return ip;
    }
    in6_addr a6{};
    if (inet_pton(AF_INET6, s.c_str(), &a6) == 1) {
        IpAddr ip;
        ip.bytes.resize(16);
        std::memcpy(ip.bytes.data(), &a6, 16);
        return normalize_ip(ip);
    }
    return std::nullopt;
}

std::string ip_to_string(const IpAddr& ip) {
    char buf[INET6_ADDRSTRLEN] = {0};
    if (ip.bytes.size() == 4) inet_ntop(AF_INET, ip.bytes.data(), buf, sizeof buf);
    else if (ip.bytes.size() == 16) inet_ntop(AF_INET6, ip.bytes.data(), buf, sizeof buf);
    else return "-";
    return buf;
}

bool CidrMatcher::try_parse(const std::string& raw, IpAddr& net, int& bits) {
    std::string s = trim(raw);
    if (s.empty()) return false;
    std::string ip_part = s;
    int prefix = -1;
    auto slash = s.find('/');
    if (slash != std::string::npos) {
        ip_part = s.substr(0, slash);
        std::string p = trim(s.substr(slash + 1));
        if (p.empty() || p.size() > 3) return false;
        for (char c : p)
            if (c < '0' || c > '9') return false;
        prefix = std::stoi(p);
    }
    auto ip = parse_ip(ip_part);
    if (!ip) return false;
    int max = static_cast<int>(ip->bytes.size()) * 8;
    bits = prefix < 0 ? max : prefix;
    if (bits < 0 || bits > max) return false;
    net = *ip;
    return true;
}

CidrMatcher::CidrMatcher(const std::vector<std::string>& cidrs) {
    for (auto& c : cidrs) {
        IpAddr net;
        int bits = 0;
        if (try_parse(c, net, bits)) nets_.emplace_back(std::move(net), bits);
        else invalid_.push_back(c);
    }
}

bool CidrMatcher::is_allowed(const std::optional<IpAddr>& ip_in) const {
    if (!ip_in) return true;
    IpAddr ip = normalize_ip(*ip_in);
    if (nets_.empty()) return ip.is_loopback();
    for (auto& [net, bits] : nets_) {
        if (net.bytes.size() != ip.bytes.size()) continue;
        int full = bits / 8, rem = bits % 8;
        bool ok = true;
        for (int i = 0; i < full && ok; ++i) ok = ip.bytes[i] == net.bytes[i];
        if (ok && rem) {
            uint8_t mask = static_cast<uint8_t>((0xFF << (8 - rem)) & 0xFF);
            ok = (ip.bytes[full] & mask) == (net.bytes[full] & mask);
        }
        if (ok) return true;
    }
    return false;
}

}  // namespace reactor::net
