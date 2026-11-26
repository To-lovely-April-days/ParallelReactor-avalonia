#include "core/program/params.hpp"

#include <cctype>
#include <cstdlib>

namespace reactor::core::program {

const ParamValue* Params::find(const std::string& k) const {
    for (const auto& i : items_)
        if (i.first == k) return &i.second;
    return nullptr;
}

void Params::set(const std::string& k, ParamValue v) {
    for (auto& i : items_)
        if (i.first == k) { i.second = std::move(v); return; }
    items_.emplace_back(k, std::move(v));
}

bool Params::erase(const std::string& k) {
    for (auto it = items_.begin(); it != items_.end(); ++it)
        if (it->first == k) { items_.erase(it); return true; }
    return false;
}

bool parse_double(const std::string& s, double& out) {
    size_t b = 0, e = s.size();
    while (b < e && std::isspace(static_cast<unsigned char>(s[b]))) ++b;
    while (e > b && std::isspace(static_cast<unsigned char>(s[e - 1]))) --e;
    if (b == e) return false;
    std::string t = s.substr(b, e - b);
    if (t.find_first_of("xX") != std::string::npos) return false;
    char* end = nullptr;
    double v = std::strtod(t.c_str(), &end);
    if (end != t.c_str() + t.size()) return false;
    out = v;
    return true;
}

double param_num(const Params& p, const std::string& k, double def) {
    const ParamValue* v = p.find(k);
    if (!v) return def;
    if (const double* d = std::get_if<double>(v)) return *d;
    double x;
    return parse_double(std::get<std::string>(*v), x) ? x : def;
}

std::string param_str(const Params& p, const std::string& k, const std::string& def) {
    const ParamValue* v = p.find(k);
    if (!v) return def;
    if (const std::string* s = std::get_if<std::string>(v)) return *s;
    return def;
}

}  // namespace reactor::core::program
