#include "store/csv.hpp"

#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>

namespace reactor::store::csv {

bool looks_like_number(const std::string& in) {
    size_t a = 0, b = in.size();
    auto ws = [](char c) { return c == ' ' || (c >= '\t' && c <= '\r'); };
    while (a < b && ws(in[a])) ++a;
    while (b > a && ws(in[b - 1])) --b;
    std::string s = in.substr(a, b - a);
    if (s.empty()) return false;
    size_t i = 0;
    if (s[0] == '+' || s[0] == '-') i = 1;
    std::string rest = s.substr(i);
    if (iequals_ascii(rest, "infinity") || iequals_ascii(rest, "nan")) return true;
    size_t int_digits = 0, frac_digits = 0;
    while (i < s.size() && s[i] >= '0' && s[i] <= '9') { ++i; ++int_digits; }
    if (i < s.size() && s[i] == '.') {
        ++i;
        while (i < s.size() && s[i] >= '0' && s[i] <= '9') { ++i; ++frac_digits; }
    }
    if (int_digits + frac_digits == 0) return false;
    if (i < s.size() && (s[i] == 'e' || s[i] == 'E')) {
        ++i;
        if (i < s.size() && (s[i] == '+' || s[i] == '-')) ++i;
        size_t ed = 0;
        while (i < s.size() && s[i] >= '0' && s[i] <= '9') { ++i; ++ed; }
        if (ed == 0) return false;
    }
    return i == s.size();
}

std::string escape(const std::string& in) {
    if (in.empty()) return "";
    std::string s = in;
    // 防 Excel 公式注入：以 = + - @ 开头的文本前加 '；纯数字（如 -1.5）不动
    if ((s[0] == '=' || s[0] == '+' || s[0] == '-' || s[0] == '@') && !looks_like_number(s)) s = "'" + s;
    if (s.find_first_of(",\"\r\n") == std::string::npos) return s;
    std::string out = "\"";
    for (char c : s) {
        if (c == '"') out += "\"\"";
        else out += c;
    }
    out += '"';
    return out;
}

std::string net_format(double v, const char* fmt) {
    if (!std::isfinite(v)) return "";
    int min_dec = 0, max_dec = 0;
    if (const char* dot = std::strchr(fmt, '.')) {
        for (const char* p = dot + 1; *p; ++p) {
            if (*p == '0') { ++min_dec; ++max_dec; }
            else if (*p == '#') ++max_dec;
        }
    }
    bool neg = std::signbit(v);
    double a = std::fabs(v);
    std::string digits;
    int scale = 0;   // 小数点前的位数：值 = 0.d1d2… × 10^scale
    if (a != 0) {
        char buf[48];
        std::snprintf(buf, sizeof buf, "%.14e", a);   // d.dddddddddddddde±XX（15 位有效数字）
        const char* e = std::strchr(buf, 'e');
        digits += buf[0];
        for (const char* p = buf + 2; p < e; ++p) digits += *p;
        scale = std::atoi(e + 1) + 1;
        while (!digits.empty() && digits.back() == '0') digits.pop_back();
        long pos = static_cast<long>(scale) + max_dec;   // 保留的有效数字位数
        if (pos < 0) {
            digits.clear();
        } else if (pos < static_cast<long>(digits.size())) {
            bool up = digits[static_cast<size_t>(pos)] >= '5';
            digits.resize(static_cast<size_t>(pos));
            if (up) {
                long k = pos - 1;
                for (; k >= 0; --k) {
                    if (digits[static_cast<size_t>(k)] == '9') digits[static_cast<size_t>(k)] = '0';
                    else { ++digits[static_cast<size_t>(k)]; break; }
                }
                if (k < 0) { digits.insert(digits.begin(), '1'); ++scale; }
            }
            while (!digits.empty() && digits.back() == '0') digits.pop_back();
        }
    }
    bool zero = digits.empty();
    std::string ip, fp;
    if (zero) {
        ip = "0";
    } else if (scale <= 0) {
        ip = "0";
        fp = std::string(static_cast<size_t>(-scale), '0') + digits;
    } else {
        size_t sc = static_cast<size_t>(scale);
        if (digits.size() <= sc) ip = digits + std::string(sc - digits.size(), '0');
        else { ip = digits.substr(0, sc); fp = digits.substr(sc); }
    }
    if (fp.size() > static_cast<size_t>(max_dec)) fp.resize(static_cast<size_t>(max_dec));
    while (fp.size() < static_cast<size_t>(min_dec)) fp += '0';
    std::string out = (neg && !zero) ? "-" : "";
    out += ip;
    if (!fp.empty()) out += "." + fp;
    return out;
}

std::string num(std::optional<double> v, const char* fmt) {
    if (!v || !std::isfinite(*v)) return "";
    return net_format(*v, fmt);
}

std::string integer(std::optional<int64_t> v) { return v ? std::to_string(*v) : ""; }

std::string boolean(std::optional<bool> v) { return v ? (*v ? "1" : "0") : ""; }

std::string time(const std::optional<WallTime>& t) { return t ? fmt_offset_time(*t) : ""; }

std::string line(const std::vector<std::string>& fields) {
    std::string out;
    for (size_t i = 0; i < fields.size(); ++i) {
        if (i) out += ',';
        out += escape(fields[i]);
    }
    return out;
}

std::vector<std::string> split(const std::string& line) {
    std::vector<std::string> fields;
    std::string cur;
    bool quoted = false;
    for (size_t i = 0; i < line.size(); ++i) {
        char ch = line[i];
        if (quoted) {
            if (ch == '"') {
                if (i + 1 < line.size() && line[i + 1] == '"') { cur += '"'; ++i; }
                else quoted = false;
            } else {
                cur += ch;
            }
        } else if (ch == '"') {
            quoted = true;
        } else if (ch == ',') {
            fields.push_back(cur);
            cur.clear();
        } else {
            cur += ch;
        }
    }
    fields.push_back(cur);
    return fields;
}

}  // namespace reactor::store::csv
