// 接口层的小工具：正文 JSON 解析（允许注释和尾逗号，同 C# JsonDocumentOptions）、参数绑定（C# Endpoints.Write.cs 的 Num / Bool / Str …，
// 文案逐字照抄）、时间格式、写命令外层信封（C# CommandResults）。全部无状态，任意线程可用。
#pragma once
#include <optional>
#include <string>
#include <vector>

#include "ctl/ctl_op_result.hpp"
#include "net/http.hpp"
#include "nlohmann/json.hpp"
#include <cstdint>

namespace reactor::api {

using ctl::ojson;

// 正文：空白 → has=false；不是对象 → error「正文必须是 JSON 对象」；解析失败 → error「正文不是合法 JSON：…」
struct ParsedBody {
    bool has = false;
    nlohmann::json value;
    std::string error;
    const nlohmann::json* ptr() const { return has ? &value : nullptr; }
};
ParsedBody parse_body(const std::string& text);
// 去掉 JSON 文本里对象 / 数组结尾前的多余逗号（字符串和注释里的不动）
std::string strip_trailing_commas(const std::string& text);

// 参数读取（C# Endpoints.Write.cs Prop / Num / Str / Bool / OptInt / IntList / Note）。第一处错误记进 error（400 文案）
class Params {
public:
    explicit Params(const nlohmann::json* body) : b_(body) {}
    const nlohmann::json* prop(const char* name) const;   // 存在且不为 null
    std::optional<double> num(const char* name);
    std::optional<bool> boolean(const char* name);
    std::optional<std::string> str(const char* name);
    std::optional<int> opt_int(const char* name);
    std::optional<std::int64_t> opt_int64(const char* name);
    std::optional<std::vector<int>> int_list(const char* name, bool required);
    std::optional<std::string> note(const char* name);   // 去换行、截到 200 字符
    std::optional<std::string> require(const char* name); // 字符串原样，其他类型取原文
    bool bad() const { return !error.empty(); }
    void fail(const std::string& m) { if (error.empty()) error = m; }
    std::string error;

private:
    const nlohmann::json* b_;
};

std::string trim(const std::string& s);
std::string truncate_note(const std::string& s, size_t max_units);
size_t utf16_len(const std::string& s);
std::string utc_iso(int64_t ms);            // C# DateTime UTC 序列化（store::fmt_utc_json）
ojson jnum(double v);                       // 整数值的 double 写成整数
ojson opt_num(const std::optional<double>& v);
ojson opt_str(const std::optional<std::string>& v);
ojson opt_utc(const std::optional<int64_t>& ms);
std::string dump(const ojson& j);           // 不因非法 UTF-8 抛异常
// 本地时间文本 "yyyyMMdd-HHmmss" → UTC 毫秒（C# DateTimeStyles.AssumeLocal → ToUniversalTime）
std::optional<int64_t> parse_local_stamp(const std::string& s);
// 通道号字符串 → 1..8；否则 nullopt
std::optional<int> parse_channel(const std::string& s);

// —— 写命令信封（C# CommandResults）——
struct CommandHttp {
    int status = 200;
    std::string body;
    bool ok = true;
    std::string word;      // confirmed / accepted / rejected
    std::string code;      // OK 或错误码
    std::string message;
};
CommandHttp result_http(const ctl::OpResult& r, const std::string& op_id, int64_t seq);
CommandHttp error_http(const std::string& op_id, const std::string& code, const std::string& message, const ojson& detail, int64_t seq);
CommandHttp unconfirmed_http(const std::string& op_id, int64_t seq);
std::string summary(const CommandHttp& h);  // "200 confirmed" / "409 GAS_BUSY"
net::Response json_response(int status, const std::string& body);
net::Response ok_json(const ojson& j);       // 200 JSON（C# ApiErrors.Ok）
// 统一错误 {ok:false, error:{code, message, detail}}（字段顺序、detail:null 同 C# ErrorDto）；状态码按 status_of(code)
net::Response api_error(const std::string& code, const std::string& message, const ojson& detail = nullptr);

}  // namespace reactor::api
