// 命令来源：触屏（本地，Unix socket）或远程客户端（TCP + 令牌）。C#: Commands/CommandSource.cs、Commands/CommandPolicy.cs
// 纯值类型，任意线程可用。
#pragma once
#include <string>

namespace reactor::cmd {

struct Source {
    enum class Kind { Local, Remote };
    Kind kind = Kind::Local;
    std::string client_id;    // 远程：令牌名（身份；仲裁器的 HolderClientId）
    std::string client_name;  // 远程：申请控制权时的客户端名（自报，已清洗）

    static Source local() { return Source{}; }
    static Source remote(std::string id, std::string name) {
        Source s;
        s.kind = Kind::Remote;
        s.client_id = std::move(id);
        s.client_name = std::move(name);
        return s;
    }
    bool is_remote() const { return kind == Kind::Remote; }

    // C# CommandSource.Label：「远程（PC-1 · 令牌 op）」/「远程（op）」/「触屏」
    std::string label() const {
        if (!is_remote()) return "触屏";
        if (!client_id.empty() && client_id != client_name) return "远程（" + client_name + " · 令牌 " + client_id + "）";
        return "远程（" + client_name + "）";
    }
};

// 通道 / 程序的 startedBy 标签（C# OperatorCommands.StartedByOf）：触屏 "local"，远程 = label()
inline std::string started_by_of(const Source& s) { return s.is_remote() ? s.label() : "local"; }

// 严格守卫开关（C# CommandPolicy）：只对远程生效；strict_local 只给测试用（改完要恢复）
struct CommandPolicy {
    static bool strict_local;
    static bool strict(const Source& s) { return s.is_remote() || strict_local; }
};

}  // namespace reactor::cmd
