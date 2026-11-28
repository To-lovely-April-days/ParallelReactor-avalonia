// 对外接口的部署参数 api.json。C#: Api/ApiConfig.cs（字段、默认值、修正规则逐条照抄）。
// 缺文件 / 损坏一律回到默认值（enabled = false）。纯逻辑，线程安全（无共享状态）。
#pragma once
#include <functional>
#include <optional>
#include <string>
#include <vector>

namespace reactor::net {

// 一枚预共享令牌：只存 SHA-256，不存明文。
struct ApiToken {
    std::string name;
    std::string sha256;          // 64 位十六进制，大小写不限
    std::string role = "viewer"; // viewer / operator / admin（读入时已 trim + 小写；空 → viewer）
    // 独立急停权限位：nullopt = 按角色默认（operator / admin 有、viewer 无）；显式 false 收回。
    // 新后端已删急停，但 PC 端仍会调 /estop（设计文档 §5.3），所以照旧解析、照旧用于「停止类」权限判定。
    std::optional<bool> estop;
};

struct ApiConfig {
    bool enabled = false;
    std::string bind = "0.0.0.0";
    int http_port = 8080;
    // TCP 入口加密（HTTPS / WSS，自签证书 + PC 端指纹固定）。缺省开；false = 明文 HTTP / WS，只给还不会 TLS 的老 Avalonia PC 端用
    //（启动时记 warn 事件）。证书在 <配置目录>/tls/，不在这里配。
    bool tls = true;
    int https_port = 0;                   // 旧字段：只读入，不用（加密就开在 httpPort 上）
    std::optional<std::string> cert_path; // 同上（证书由设备自己生成，见 net/tls.hpp）
    std::vector<std::string> allowed_cidrs;  // 空 = 只允许回环
    std::vector<ApiToken> tokens;
    int heartbeat_timeout_sec = 10;
    int lease_grace_sec = 60;
    std::string on_remote_lost = "keep";  // keep / stopHeat / estop
    bool auto_grant_remote = false;
    int control_request_timeout_sec = 60;
    int write_rate_limit_per_sec = 10;
    int max_clients = 4;
    std::optional<std::string> serial;

    // —— 不序列化 ——
    std::string source_path;
    bool file_found = false;
    std::vector<std::string> warnings;    // 已修正的非致命问题（启动时记 warn 事件）
    std::string source_file_name() const; // 不带目录（事件文字里用）
};

// 读取：文件不存在 → 默认值，error 为空；JSON 损坏 / 类型不对 → 默认值并给出错误文字（"api.json：…"）。
// 键名大小写不敏感，允许注释和尾逗号（C#: ApiConfigJsonContext 的选项）。
ApiConfig load_api_config(const std::string& path, std::string* error);
// 从 JSON 文本解析（测试和 load 共用）；source_name 只用于错误文字
ApiConfig parse_api_config(const std::string& text, const std::string& source_name, std::string* error);
// 叠加环境变量：PR_API=1/true → enabled；0/false → 关；PR_API_PORT=n → http_port；PR_API_AUTOGRANT=1/true → auto_grant_remote
void apply_api_env(ApiConfig& cfg, const std::function<const char*(const char*)>& getenv_fn);

}  // namespace reactor::net
