#include "store/program_library.hpp"

#include <algorithm>
#include <cstdio>
#include <stdexcept>

#include "common/clock.hpp"
#include <cstdint>
#include <functional>
#include <map>
#include <mutex>
#include <optional>
#include <string>
#include <vector>

namespace reactor::store {

namespace {

LibResult err(std::string code, std::string msg, int64_t cur = 0) {
    LibResult r;
    r.code = std::move(code);
    r.message = std::move(msg);
    r.current_revision = cur;
    return r;
}

const char* kind_name(LibKind k) { return k == LibKind::Program ? "程序" : "模板"; }

bool is_string(const ojson& o, const char* k) {
    auto it = o.find(k);
    return it != o.end() && it->is_string();
}

std::string str_or(const ojson& o, const char* k, const std::string& def = "") {
    auto it = o.find(k);
    return it != o.end() && it->is_string() ? it->get<std::string>() : def;
}

}  // namespace

ProgramLibrary::ProgramLibrary(std::string data_dir, std::function<int64_t()> now_ms)
    : root_(join_path(data_dir, "library")), now_ms_(std::move(now_ms)) {
    if (!now_ms_) now_ms_ = [] { return wall_ms(); };
    load_dir(LibKind::Program);
    load_dir(LibKind::Template);
}

bool ProgramLibrary::valid_id(const std::string& id) {
    if (id.empty() || id.size() > 64) return false;
    for (char c : id)
        if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-')) return false;
    return true;
}

std::string ProgramLibrary::dir_of(LibKind k) const { return join_path(root_, k == LibKind::Program ? "programs" : "templates"); }
std::string ProgramLibrary::file_of(LibKind k, const std::string& id) const { return join_path(dir_of(k), id + ".json"); }
std::map<std::string, LibEntry>& ProgramLibrary::map_of(LibKind k) { return k == LibKind::Program ? programs_ : templates_; }
const std::map<std::string, LibEntry>& ProgramLibrary::map_of(LibKind k) const { return k == LibKind::Program ? programs_ : templates_; }

std::string ProgramLibrary::id_of(LibKind k, const ojson& body) {
    if (k == LibKind::Program) return str_or(body, "id");
    auto it = body.find("program");
    return it != body.end() && it->is_object() ? str_or(*it, "id") : "";
}

std::string ProgramLibrary::modified_of(LibKind k, const ojson& body) {
    if (k == LibKind::Program) return str_or(body, "modified");
    auto it = body.find("program");
    return it != body.end() && it->is_object() ? str_or(*it, "modified") : "";
}

void ProgramLibrary::load_dir(LibKind k) {
    auto& m = map_of(k);
    for (auto& name : list_files(dir_of(k))) {
        if (name.size() < 6 || name.compare(name.size() - 5, 5, ".json") != 0) continue;
        std::string stem = name.substr(0, name.size() - 5);
        std::string path = join_path(dir_of(k), name);
        std::string text;
        try {
            if (!valid_id(stem)) throw std::runtime_error("文件名不是合法 id");
            if (!read_file(path, text)) throw std::runtime_error("读不了");
            ojson body = ojson::parse(strip_bom(text));
            if (!body.is_object()) throw std::runtime_error("不是 JSON 对象");
            LibEntry e;
            e.revision = 1;
            if (auto it = body.find("revision"); it != body.end()) {
                if (it->is_number_integer()) e.revision = it->get<int64_t>();
                body.erase("revision");
            }
            std::string id = id_of(k, body);
            e.id = valid_id(id) ? id : stem;
            if (e.id != stem) throw std::runtime_error("条目 id 和文件名不一致");
            e.body = std::move(body);
            m[e.id] = std::move(e);
        } catch (const std::exception& ex) {
            load_errors_.push_back(path + "：" + ex.what());
        }
    }
}

std::vector<std::string> ProgramLibrary::load_errors() const {
    std::lock_guard<std::mutex> lk(mu_);
    return load_errors_;
}

std::vector<LibEntry> ProgramLibrary::list(LibKind k) const {
    std::lock_guard<std::mutex> lk(mu_);
    std::vector<LibEntry> v;
    for (auto& p : map_of(k)) v.push_back(p.second);
    std::stable_sort(v.begin(), v.end(), [&](const LibEntry& a, const LibEntry& b) {
        std::string ma = modified_of(k, a.body), mb = modified_of(k, b.body);
        if (ma != mb) return ma > mb;
        return a.id < b.id;
    });
    return v;
}

std::optional<LibEntry> ProgramLibrary::get(LibKind k, const std::string& id) const {
    std::lock_guard<std::mutex> lk(mu_);
    auto& m = map_of(k);
    auto it = m.find(id);
    if (it == m.end()) return std::nullopt;
    return it->second;
}

std::string ProgramLibrary::new_id(LibKind k) const {
    std::string prefix = k == LibKind::Program ? "P" : "U";
    // C#: ProgramLibraryStore.NewId / ProgramTemplateStore.NewId（yyMMddHHmmssfff；fmt_wall 只认 yyyy，去掉前两位）
    std::string stamp = fmt_wall(WallTime::local(now_ms_()), "yyyyMMddHHmmssfff").substr(2);
    std::string id = prefix + stamp;
    auto& m = map_of(k);
    for (int n = 1; m.count(id) || file_exists(file_of(k, id)); ++n) id = prefix + stamp + "-" + std::to_string(n);
    return id;
}

std::string ProgramLibrary::normalize(LibKind k, ojson& body, const std::string& self_id) const {
    if (!body.is_object()) return std::string(kind_name(k)) + "必须是 JSON 对象";
    auto check_program = [&](ojson& p, bool is_template) -> std::string {
        if (!p.is_object()) return "program 必须是 JSON 对象";
        p["id"] = self_id;
        if (p.contains("name") && !p["name"].is_string()) return "name 必须是字符串";
        std::string name = trim(str_or(p, "name"));
        if (name.empty()) return is_template ? "请填模板名称" : "请填程序名称";
        if (utf16_length(name) > static_cast<size_t>(kNameMax)) return "名称最多 " + std::to_string(kNameMax) + " 个字符";
        p["name"] = name;
        for (const char* key : {"model", "modified", "by"})
            if (p.contains(key) && !p[key].is_string()) return std::string(key) + " 必须是字符串";
        if (!is_string(p, "model")) p["model"] = "PR8";
        if (!is_string(p, "modified")) p["modified"] = fmt_wall(WallTime::local(now_ms_()), "yyyy-MM-ddTHH:mm:ss");
        if (!is_string(p, "by")) p["by"] = "本机";
        if (p.contains("draft") && !p["draft"].is_boolean()) return "draft 必须是 true / false";
        if (!p.contains("draft")) p["draft"] = false;
        if (is_template) p["draft"] = false;   // C#: 模板的 Draft 不用（读进来一律 false）
        if (!p.contains("steps")) p["steps"] = ojson::array();
        if (!p["steps"].is_array()) return "steps 必须是数组";
        for (auto& s : p["steps"]) {
            if (!s.is_object() || !is_string(s, "type")) return "每一步必须是带 type 的对象";
            if (s.contains("params") && !s["params"].is_object()) return "步骤的 params 必须是对象";
        }
        return "";
    };
    if (k == LibKind::Program) return check_program(body, false);
    if (!body.contains("program")) return "缺少 program";
    for (const char* key : {"note", "from"})
        if (body.contains(key) && !body[key].is_string()) return std::string(key) + " 必须是字符串";
    if (!body.contains("note")) body["note"] = "";
    if (!body.contains("from")) body["from"] = "";
    std::string note = trim(body["note"].get<std::string>());
    if (utf16_length(note) > static_cast<size_t>(kNoteMax)) return "说明最多 " + std::to_string(kNoteMax) + " 个字符";
    body["note"] = note;
    if (body.contains("builtIn") && !body["builtIn"].is_boolean()) return "builtIn 必须是 true / false";
    std::string e = check_program(body["program"], true);
    if (!e.empty()) return e;
    // 不能和别的模板重名（不区分大小写，C#: PcTemplate.NameError）
    std::string name = body["program"]["name"].get<std::string>();
    for (auto& p : templates_) {
        if (p.first == self_id) continue;
        auto it = p.second.body.find("program");
        if (it == p.second.body.end() || !it->is_object()) continue;
        if (iequals_ascii(trim(str_or(*it, "name")), name)) return "已有同名模板 · 换一个名字";
    }
    return "";
}

bool ProgramLibrary::write_entry(LibKind k, const LibEntry& e, std::string* error) {
    ojson out = e.body;
    out["revision"] = e.revision;
    return write_file_atomic(file_of(k, e.id), dump_json(out, 2), true, error);
}

LibResult ProgramLibrary::create(LibKind k, ojson body, bool allow_builtin) {
    std::lock_guard<std::mutex> lk(mu_);
    if (!body.is_object()) return err("BAD_REQUEST", std::string(kind_name(k)) + "必须是 JSON 对象");
    body.erase("revision");
    if (k == LibKind::Template && !allow_builtin && body.contains("builtIn") && body["builtIn"].is_boolean() &&
        body["builtIn"].get<bool>())
        return err("BAD_REQUEST", "内置模板只能随软件提供");
    std::string id = id_of(k, body);
    if (id.empty()) id = new_id(k);
    if (!valid_id(id)) return err("BAD_REQUEST", "id 只能用字母、数字、- 和 _（最长 64）");
    auto& m = map_of(k);
    if (m.count(id) || file_exists(file_of(k, id)))
        return err("REVISION_CONFLICT", std::string("已有同一 id 的") + kind_name(k), m.count(id) ? m[id].revision : 0);
    std::string bad = normalize(k, body, id);
    if (!bad.empty()) return err("BAD_REQUEST", bad);
    LibEntry e{id, 1, std::move(body)};
    std::string werr;
    if (!write_entry(k, e, &werr)) return err("INTERNAL", std::string(kind_name(k)) + "保存失败：" + werr);
    m[id] = e;
    LibResult r;
    r.ok = true;
    r.entry = e;
    r.current_revision = e.revision;
    return r;
}

LibResult ProgramLibrary::upsert_builtin(LibKind k, ojson body, std::string* status) {
    std::lock_guard<std::mutex> lk(mu_);
    if (!body.is_object()) return err("BAD_REQUEST", std::string(kind_name(k)) + "必须是 JSON 对象");
    body.erase("revision");
    if (k == LibKind::Template) body["builtIn"] = true;
    std::string id = id_of(k, body);
    if (id.empty() || !valid_id(id)) return err("BAD_REQUEST", "内置条目必须带合法 id");
    auto& m = map_of(k);
    auto it = m.find(id);
    bool exists = it != m.end();
    if (exists && k == LibKind::Template) {
        auto b = it->second.body.find("builtIn");
        if (b == it->second.body.end() || !b->is_boolean() || !b->get<bool>())
            return err("BAD_REQUEST", "已有同一 id 的用户" + std::string(kind_name(k)) + " · 不覆盖", it->second.revision);
    }
    if (!exists && file_exists(file_of(k, id)))
        return err("REVISION_CONFLICT", std::string("已有同一 id 的") + kind_name(k) + "（文件读不了）", 0);
    // 比较内容（不含时间 / 作者）：相同就不动，避免每次开机都把 revision 加 1
    auto strip = [k](ojson b) {
        ojson* p = k == LibKind::Template ? (b.contains("program") ? &b["program"] : nullptr) : &b;
        if (p && p->is_object()) {
            p->erase("modified");
            p->erase("by");
        }
        return b;
    };
    if (exists) {
        ojson keep_time = it->second.body;
        ojson* cur_p = k == LibKind::Template ? &keep_time["program"] : &keep_time;
        ojson* new_p = k == LibKind::Template ? &body["program"] : &body;
        // 新内容没带 modified / by 时沿用旧值（normalize 才不会填成「现在」）
        if (new_p->is_object() && cur_p->is_object()) {
            for (const char* key : {"modified", "by"})
                if (!new_p->contains(key) && cur_p->contains(key)) (*new_p)[key] = (*cur_p)[key];
        }
    }
    std::string bad = normalize(k, body, id);
    if (!bad.empty()) return err("BAD_REQUEST", bad);
    if (exists && strip(body) == strip(it->second.body)) {
        if (status) *status = "unchanged";
        LibResult r;
        r.ok = true;
        r.entry = it->second;
        r.current_revision = it->second.revision;
        return r;
    }
    LibEntry e{id, exists ? it->second.revision + 1 : 1, std::move(body)};
    std::string werr;
    if (!write_entry(k, e, &werr)) return err("INTERNAL", std::string(kind_name(k)) + "保存失败：" + werr, exists ? it->second.revision : 0);
    m[id] = e;
    if (status) *status = exists ? "updated" : "added";
    LibResult r;
    r.ok = true;
    r.entry = e;
    r.current_revision = e.revision;
    return r;
}

LibResult ProgramLibrary::update(LibKind k, const std::string& id, ojson body, std::optional<int64_t> expected) {
    std::lock_guard<std::mutex> lk(mu_);
    auto& m = map_of(k);
    auto it = m.find(id);
    if (it == m.end()) return err("NOT_FOUND", std::string("没有这个") + kind_name(k));
    if (!expected) return err("PRECONDITION_REQUIRED", "保存时要带上读到的 revision", it->second.revision);
    if (*expected != it->second.revision)
        return err("REVISION_CONFLICT", std::string("这个") + kind_name(k) + "已在别处修改过（当前版本 " +
                                            std::to_string(it->second.revision) + "），请刷新后再改",
                   it->second.revision);
    if (k == LibKind::Template) {
        auto b = it->second.body.find("builtIn");
        if (b != it->second.body.end() && b->is_boolean() && b->get<bool>()) return err("BAD_REQUEST", "内置模板不能修改");
    }
    if (!body.is_object()) return err("BAD_REQUEST", std::string(kind_name(k)) + "必须是 JSON 对象");
    body.erase("revision");
    std::string body_id = id_of(k, body);
    if (!body_id.empty() && body_id != id) return err("BAD_REQUEST", "正文里的 id 和地址里的不一致");
    if (k == LibKind::Template) body.erase("builtIn");
    std::string bad = normalize(k, body, id);
    if (!bad.empty()) return err("BAD_REQUEST", bad);
    LibEntry e{id, it->second.revision + 1, std::move(body)};
    std::string werr;
    if (!write_entry(k, e, &werr)) return err("INTERNAL", std::string(kind_name(k)) + "保存失败：" + werr, it->second.revision);
    it->second = e;
    LibResult r;
    r.ok = true;
    r.entry = e;
    r.current_revision = e.revision;
    return r;
}

LibResult ProgramLibrary::remove(LibKind k, const std::string& id, std::optional<int64_t> expected) {
    std::lock_guard<std::mutex> lk(mu_);
    auto& m = map_of(k);
    auto it = m.find(id);
    if (it == m.end()) return err("NOT_FOUND", std::string("没有这个") + kind_name(k));
    if (!expected) return err("PRECONDITION_REQUIRED", "删除时要带上读到的 revision", it->second.revision);
    if (*expected != it->second.revision)
        return err("REVISION_CONFLICT", std::string("这个") + kind_name(k) + "已在别处修改过（当前版本 " +
                                            std::to_string(it->second.revision) + "），请刷新后再删",
                   it->second.revision);
    if (k == LibKind::Template) {
        auto b = it->second.body.find("builtIn");
        if (b != it->second.body.end() && b->is_boolean() && b->get<bool>()) return err("BAD_REQUEST", "内置模板不能删除");
    }
    std::string path = file_of(k, id);
    if (std::remove(path.c_str()) != 0 && file_exists(path)) return err("INTERNAL", std::string(kind_name(k)) + "删除失败");
    LibResult r;
    r.ok = true;
    r.entry = it->second;
    m.erase(it);
    return r;
}

ProgramLibrary::ImportResult ProgramLibrary::import_pc_library(const std::string& path) {
    ImportResult r;
    std::string text;
    if (!read_file(path, text)) { r.error = "读不了 " + path; return r; }
    ojson root;
    try { root = ojson::parse(strip_bom(text)); } catch (const std::exception& e) { r.error = std::string("library.json 读不了：") + e.what(); return r; }
    auto it = root.is_object() ? root.find("programs") : root.end();
    if (!root.is_object() || it == root.end() || !it->is_array()) return r;
    for (auto& p : *it) {
        std::string id = p.is_object() ? str_or(p, "id") : "";
        if (!id.empty() && get(LibKind::Program, id)) { ++r.skipped; continue; }
        if (create(LibKind::Program, p).ok) ++r.added;
        else ++r.skipped;
    }
    return r;
}

ProgramLibrary::ImportResult ProgramLibrary::import_pc_templates(const std::string& path) {
    ImportResult r;
    std::string text;
    if (!read_file(path, text)) { r.error = "读不了 " + path; return r; }
    ojson root;
    try { root = ojson::parse(strip_bom(text)); } catch (const std::exception& e) { r.error = std::string("templates.json 读不了：") + e.what(); return r; }
    auto it = root.is_object() ? root.find("templates") : root.end();
    if (!root.is_object() || it == root.end() || !it->is_array()) return r;
    for (auto& t : *it) {
        std::string id = t.is_object() ? id_of(LibKind::Template, t) : "";
        if (!id.empty() && get(LibKind::Template, id)) { ++r.skipped; continue; }
        if (create(LibKind::Template, t).ok) ++r.added;
        else ++r.skipped;
    }
    return r;
}

std::string ProgramLibrary::export_pc_library() const {
    ojson root = ojson::object();
    root["version"] = 1;
    root["programs"] = ojson::array();
    for (auto& e : list(LibKind::Program)) root["programs"].push_back(e.body);
    return dump_json(root, 2);
}

std::string ProgramLibrary::export_pc_templates() const {
    ojson root = ojson::object();
    root["version"] = 1;
    root["templates"] = ojson::array();
    for (auto& e : list(LibKind::Template)) {
        if (auto b = e.body.find("builtIn"); b != e.body.end() && b->is_boolean() && b->get<bool>()) continue;   // 内置模板不在文件里
        ojson t = ojson::object();
        t["note"] = str_or(e.body, "note");
        t["from"] = str_or(e.body, "from");
        t["program"] = e.body.at("program");
        root["templates"].push_back(t);
    }
    return dump_json(root, 2);
}

}  // namespace reactor::store
