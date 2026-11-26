// 程序库（新增，触屏和 PC 互通；设计文档 §5.4 /library/*、§7）。
//   <data>/library/programs/<id>.json    一个程序 = PC 端 library.json 里 "programs" 数组的一项
//   <data>/library/templates/<id>.json   一个「我的模板」= PC 端 templates.json 里 "templates" 数组的一项
// 条目的 JSON 形状和 PC 端完全一样（C#: Control/ParallelReactor.Control.Core/Programs/Editor/PcProgram.cs Write / Read、
// Control/ParallelReactor.Control/Services/ProgramTemplateStore.cs）：
//   程序   { "id", "name", "model": "PR8", "modified": "2026-11-14T16:20:00"（本地时间）, "by", "draft",
//            "steps": [ { "type": "heat", "name": "升温", "params": { "mode": "fixed", "temp": 150, … , "segments": [...] } } ] }
//   模板   { "note", "from", "program": { 同上 } }        模板的 id = program.id
// 参数键同 HMI 的程序 JSON，sel 存协议码，压力存 MPa（下发时由客户端 / 命令层换算 psi）。存储层不解释步骤参数，原样保存；
// 未知字段原样保留。文件里另加一个 "revision"（每次保存 +1），用于乐观并发：更新 / 删除必须带上读到的 revision，
// 不一致 → REVISION_CONFLICT（后保存的一方收到冲突提示）。
//
// 线程：内部一把锁，任意线程可调（接口线程直接调用即可，不必经控制线程）。
#pragma once

#include <cstdint>
#include <functional>
#include <map>
#include <mutex>
#include <optional>
#include <string>
#include <vector>

#include "store/store_util.hpp"

namespace reactor::store {

enum class LibKind { Program, Template };

struct LibEntry {
    std::string id;
    int64_t revision = 0;
    ojson body;   // PC 形状的条目（不含 "revision"）
};

struct LibResult {
    bool ok = false;
    std::string code;      // BAD_REQUEST / NOT_FOUND / REVISION_CONFLICT / PRECONDITION_REQUIRED / INTERNAL
    std::string message;   // 中文
    std::optional<LibEntry> entry;
    int64_t current_revision = 0;   // 冲突时：服务端当前 revision
};

class ProgramLibrary {
public:
    static constexpr int kNameMax = 40;    // C#: PcTemplate.NameMax
    static constexpr int kNoteMax = 120;   // C#: PcTemplate.NoteMax

    // data_dir：数据目录（库在 <data_dir>/library）。now_ms：取当前时间（测试注入），缺省 wall_ms()
    explicit ProgramLibrary(std::string data_dir, std::function<int64_t()> now_ms = {});

    const std::string& root() const { return root_; }
    // 启动时读不了的文件（原样留在磁盘上，不覆盖）
    std::vector<std::string> load_errors() const;

    // 列表：按 modified 倒序（新的在前），同时间按 id
    std::vector<LibEntry> list(LibKind kind) const;
    std::optional<LibEntry> get(LibKind kind, const std::string& id) const;

    // 新建：id 缺省时生成（程序 "P"、模板 "U" + yyMMddHHmmssfff）；id 已存在 → REVISION_CONFLICT。revision = 1。
    // allow_builtin = false 时拒绝 "builtIn": true 的模板（内置模板只能由服务自己放进去）
    LibResult create(LibKind kind, ojson body, bool allow_builtin = false);
    // 只给服务自己用（开机种内置模板 / 软件升级刷新内置模板）：body 一律按 "builtIn": true 存。
    // 不存在 → 新建（revision 1）；已有且是内置模板、内容（note / from / program 去掉 modified、by 之后）不同 → 覆盖、revision + 1；
    // 相同 → 不动。已有同 id 的非内置条目 → BAD_REQUEST（不覆盖用户的东西）。status 出参：added / updated / unchanged
    LibResult upsert_builtin(LibKind kind, ojson body, std::string* status = nullptr);
    // 更新：必须带 expected_revision（缺 → PRECONDITION_REQUIRED；不一致 → REVISION_CONFLICT）。成功后 revision + 1
    LibResult update(LibKind kind, const std::string& id, ojson body, std::optional<int64_t> expected_revision);
    // 删除：同上
    LibResult remove(LibKind kind, const std::string& id, std::optional<int64_t> expected_revision);

    // 首次启动从 PC 导入：library.json（{"version":1,"programs":[…]}）/ templates.json（{"version":1,"templates":[…]}）。
    // 已有同 id 的跳过；单条不合格的跳过并计数
    struct ImportResult {
        int added = 0;
        int skipped = 0;
        std::string error;   // 文件读不了
    };
    ImportResult import_pc_library(const std::string& library_json_path);
    ImportResult import_pc_templates(const std::string& templates_json_path);

    // 导出成 PC 端的整文件格式（给旧版 PC 客户端 / U 盘导出用）
    std::string export_pc_library() const;
    std::string export_pc_templates() const;

    static bool valid_id(const std::string& id);

private:
    std::string dir_of(LibKind k) const;
    std::string file_of(LibKind k, const std::string& id) const;
    std::map<std::string, LibEntry>& map_of(LibKind k);
    const std::map<std::string, LibEntry>& map_of(LibKind k) const;
    void load_dir(LibKind k);
    // 校验并补齐缺省字段；返回错误消息（空 = 通过）。id 由调用方给（模板写进 program.id）
    std::string normalize(LibKind k, ojson& body, const std::string& self_id) const;
    std::string new_id(LibKind k) const;
    bool write_entry(LibKind k, const LibEntry& e, std::string* err);
    static std::string id_of(LibKind k, const ojson& body);
    static std::string modified_of(LibKind k, const ojson& body);

    std::string root_;
    std::function<int64_t()> now_ms_;
    mutable std::mutex mu_;
    std::map<std::string, LibEntry> programs_, templates_;
    std::vector<std::string> load_errors_;
};

}  // namespace reactor::store
