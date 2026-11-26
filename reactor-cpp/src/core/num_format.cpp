#include "core/num_format.hpp"

#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <string>

namespace reactor::core {

std::string fmt_dec(double v, int max_dec, int min_dec) {
    if (std::isnan(v)) return "NaN";
    if (std::isinf(v)) return v > 0 ? "∞" : "-∞";
    if (max_dec < 0) max_dec = 0;
    if (min_dec < 0) min_dec = 0;
    if (min_dec > max_dec) min_dec = max_dec;

    // 15 位有效数字：d.dddddddddddddde±XX
    char buf[64];
    std::snprintf(buf, sizeof buf, "%.14e", v);
    bool neg = buf[0] == '-';
    const char* p = buf + (neg ? 1 : 0);
    std::string digits;
    while (*p && *p != 'e') {
        if (*p >= '0' && *p <= '9') digits.push_back(*p);
        ++p;
    }
    int exp10 = (*p == 'e') ? std::atoi(p + 1) : 0;
    int pos = exp10 + 1;  // 小数点前的位数

    std::string ip, fp;
    int nd = static_cast<int>(digits.size());
    if (pos <= 0) {
        ip = "0";
        fp = std::string(static_cast<size_t>(-pos), '0') + digits;
    } else if (pos >= nd) {
        ip = digits + std::string(static_cast<size_t>(pos - nd), '0');
    } else {
        ip = digits.substr(0, static_cast<size_t>(pos));
        fp = digits.substr(static_cast<size_t>(pos));
    }

    // 舍到 max_dec 位（远离零）
    if (static_cast<int>(fp.size()) > max_dec) {
        bool up = fp[static_cast<size_t>(max_dec)] >= '5';
        fp.resize(static_cast<size_t>(max_dec));
        if (up) {
            int i = static_cast<int>(fp.size()) - 1;
            bool carry = true;
            for (; i >= 0 && carry; --i) {
                if (fp[static_cast<size_t>(i)] == '9') fp[static_cast<size_t>(i)] = '0';
                else { ++fp[static_cast<size_t>(i)]; carry = false; }
            }
            for (i = static_cast<int>(ip.size()) - 1; i >= 0 && carry; --i) {
                if (ip[static_cast<size_t>(i)] == '9') ip[static_cast<size_t>(i)] = '0';
                else { ++ip[static_cast<size_t>(i)]; carry = false; }
            }
            if (carry) ip.insert(ip.begin(), '1');
        }
    }
    while (static_cast<int>(fp.size()) < max_dec) fp.push_back('0');
    while (static_cast<int>(fp.size()) > min_dec && !fp.empty() && fp.back() == '0') fp.pop_back();
    size_t nz = ip.find_first_not_of('0');
    ip = nz == std::string::npos ? "0" : ip.substr(nz);

    bool zero = ip == "0" && fp.find_first_not_of('0') == std::string::npos;
    std::string out = (neg && !zero) ? "-" : "";
    out += ip;
    if (!fp.empty()) { out += '.'; out += fp; }
    return out;
}

}  // namespace reactor::core
