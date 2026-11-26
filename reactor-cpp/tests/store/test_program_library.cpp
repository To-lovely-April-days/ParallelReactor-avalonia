// 程序库（新增）：PC 形状的条目、revision 乐观并发、从 PC 的 library.json / templates.json 导入、导出回 PC 格式。
#include "store/program_library.hpp"
#include "store_test_util.hpp"
#include "testing.hpp"

using namespace reactor::store;
using store_test::contains;
using store_test::slurp;
using store_test::spit;
using store_test::TempDir;

// PC 端 library.json 的一个程序（ProgramLibraryStore / PcProgram.Write 写出的样子，压力 MPa）
static const char* kPcProgram = R"({
  "id": "P261114162000123",
  "name": "催化加氢筛选 · 120 ℃",
  "model": "PR8",
  "modified": "2026-11-14T16:20:00",
  "by": "张工",
  "draft": false,
  "steps": [
    { "type": "leak", "name": "气密", "params": { "gas": "inert", "pres": 1.0, "hold": 10, "maxDrop": 0.02 } },
    { "type": "heat", "name": "升温", "params": { "mode": "curve", "temp": 120, "wait": 15,
      "segments": [ { "temp": 80, "rate": 3, "hold": 20 }, { "temp": 120, "rate": 2, "hold": 0 } ] } },
    { "type": "press", "name": "充氢", "params": { "gas": "A", "pres": 3.0 } },
    { "type": "end", "name": "结束", "params": {} }
  ]
})";

static int64_t fixed_now() { return store_test::local_ms(2026, 11, 26, 9, 30, 15, 250); }

TEST(store_library, create_get_list_roundtrip_keeps_pc_shape) {
    TempDir d;
    ProgramLibrary lib(d.path, fixed_now);
    auto r = lib.create(LibKind::Program, ojson::parse(kPcProgram));
    CHECK(r.ok);
    CHECK_EQ(r.entry->id, std::string("P261114162000123"));
    CHECK_EQ(r.entry->revision, int64_t(1));
    std::string file = d / "library/programs/P261114162000123.json";
    CHECK(file_exists(file));
    auto on_disk = ojson::parse(slurp(file));
    CHECK_EQ(on_disk["revision"].get<int64_t>(), int64_t(1));
    CHECK_EQ(on_disk["steps"][1]["params"]["segments"][1]["rate"].get<double>(), 2.0);   // 步骤参数原样保存
    CHECK_EQ(on_disk["steps"][0]["params"]["pres"].get<double>(), 1.0);                  // MPa 不换算
    CHECK_EQ(on_disk["by"].get<std::string>(), std::string("张工"));
    CHECK(contains(slurp(file), "催化加氢筛选"));   // 中文不转义
    // 重新打开：从文件读回
    ProgramLibrary again(d.path, fixed_now);
    auto e = again.get(LibKind::Program, "P261114162000123");
    CHECK(e.has_value());
    CHECK_EQ(e->revision, int64_t(1));
    CHECK(!e->body.contains("revision"));   // 对外的条目不含 revision（单独给）
    CHECK(e->body == r.entry->body);
    CHECK(again.load_errors().empty());
}

TEST(store_library, defaults_and_generated_id) {
    TempDir d;
    ProgramLibrary lib(d.path, fixed_now);
    auto r = lib.create(LibKind::Program, ojson::parse(R"({ "name": "  新程序  ", "steps": [] })"));
    CHECK(r.ok);
    CHECK_EQ(r.entry->id, std::string("P261126093015250"));   // 'P' + yyMMddHHmmssfff（本地时间）
    CHECK_EQ(r.entry->body["name"].get<std::string>(), std::string("新程序"));
    CHECK_EQ(r.entry->body["model"].get<std::string>(), std::string("PR8"));
    CHECK_EQ(r.entry->body["modified"].get<std::string>(), std::string("2026-11-26T09:30:15"));
    CHECK_EQ(r.entry->body["by"].get<std::string>(), std::string("本机"));
    CHECK_EQ(r.entry->body["draft"].get<bool>(), false);
    auto r2 = lib.create(LibKind::Program, ojson::parse(R"({ "name": "再来一个" })"));
    CHECK_EQ(r2.entry->id, std::string("P261126093015250-1"));   // 同一毫秒加序号
    CHECK(r2.entry->body["steps"].is_array());
}

TEST(store_library, validation) {
    TempDir d;
    ProgramLibrary lib(d.path, fixed_now);
    CHECK_EQ(lib.create(LibKind::Program, ojson::parse(R"({ "name": " " })")).message, std::string("请填程序名称"));
    CHECK_EQ(lib.create(LibKind::Program, ojson::parse(R"({ "name": "12345678901234567890123456789012345678901" })")).message,
             std::string("名称最多 40 个字符"));
    CHECK(lib.create(LibKind::Program, ojson::parse(R"({ "name": "一二三四五六七八九十一二三四五六七八九十一二三四五六七八九十一二三四五六七八九十" })")).ok);   // 40 个汉字可以
    CHECK_EQ(lib.create(LibKind::Program, ojson::parse(R"({ "id": "../x", "name": "a" })")).code, std::string("BAD_REQUEST"));
    CHECK_EQ(lib.create(LibKind::Program, ojson::parse(R"({ "name": "a", "steps": {} })")).code, std::string("BAD_REQUEST"));
    CHECK_EQ(lib.create(LibKind::Program, ojson::parse(R"({ "name": "a", "steps": [ { "name": "x" } ] })")).code, std::string("BAD_REQUEST"));
    CHECK_EQ(lib.create(LibKind::Program, ojson::parse(R"([1])")).code, std::string("BAD_REQUEST"));
    CHECK_EQ(lib.create(LibKind::Template, ojson::parse(R"({ "note": "x" })")).message, std::string("缺少 program"));
}

TEST(store_library, revision_conflict_and_precondition) {
    TempDir d;
    ProgramLibrary lib(d.path, fixed_now);
    lib.create(LibKind::Program, ojson::parse(kPcProgram));
    const std::string id = "P261114162000123";
    // 触屏和 PC 都读到 revision 1
    auto touch = *lib.get(LibKind::Program, id);
    auto pc = *lib.get(LibKind::Program, id);
    touch.body["name"] = "触屏改的";
    auto a = lib.update(LibKind::Program, id, touch.body, touch.revision);
    CHECK(a.ok);
    CHECK_EQ(a.entry->revision, int64_t(2));
    pc.body["name"] = "PC 改的";
    auto b = lib.update(LibKind::Program, id, pc.body, pc.revision);   // 后保存的一方：冲突
    CHECK(!b.ok);
    CHECK_EQ(b.code, std::string("REVISION_CONFLICT"));
    CHECK_EQ(b.current_revision, int64_t(2));
    CHECK_EQ(lib.get(LibKind::Program, id)->body["name"].get<std::string>(), std::string("触屏改的"));
    // 不带 revision
    CHECK_EQ(lib.update(LibKind::Program, id, pc.body, std::nullopt).code, std::string("PRECONDITION_REQUIRED"));
    CHECK_EQ(lib.remove(LibKind::Program, id, std::nullopt).code, std::string("PRECONDITION_REQUIRED"));
    // 正文 id 和地址不一致
    auto other = pc.body;
    other["id"] = "P2";
    CHECK_EQ(lib.update(LibKind::Program, id, other, int64_t(2)).code, std::string("BAD_REQUEST"));
    CHECK_EQ(lib.update(LibKind::Program, "nope", pc.body, int64_t(1)).code, std::string("NOT_FOUND"));
    // 同 id 再建：冲突
    CHECK_EQ(lib.create(LibKind::Program, ojson::parse(kPcProgram)).code, std::string("REVISION_CONFLICT"));
    // 删除：旧 revision 冲突，对的 revision 成功
    CHECK_EQ(lib.remove(LibKind::Program, id, int64_t(1)).code, std::string("REVISION_CONFLICT"));
    CHECK(lib.remove(LibKind::Program, id, int64_t(2)).ok);
    CHECK(!file_exists(d / "library/programs/" + id + ".json"));
    CHECK(!lib.get(LibKind::Program, id));
    CHECK_EQ(lib.remove(LibKind::Program, id, int64_t(2)).code, std::string("NOT_FOUND"));
    // revision 跨重启保留
    auto c = lib.create(LibKind::Program, ojson::parse(R"({ "id": "K1", "name": "k" })"));
    auto e = *lib.get(LibKind::Program, "K1");
    lib.update(LibKind::Program, "K1", e.body, e.revision);
    ProgramLibrary again(d.path, fixed_now);
    CHECK_EQ(again.get(LibKind::Program, "K1")->revision, int64_t(2));
    (void)c;
}

TEST(store_library, templates_unique_names_and_builtin_readonly) {
    TempDir d;
    ProgramLibrary lib(d.path, fixed_now);
    auto t = ojson::parse(std::string(R"({ "note": "Pd/C 体系常用的温度和压力", "from": "催化加氢筛选 · 120 ℃", "program": )") + kPcProgram + "}");
    t["program"]["id"] = "U1";
    t["program"]["name"] = "催化加氢 · 120 ℃ 标准流程";
    auto r = lib.create(LibKind::Template, t);
    CHECK(r.ok);
    CHECK_EQ(r.entry->id, std::string("U1"));
    CHECK_EQ(r.entry->body["program"]["draft"].get<bool>(), false);
    CHECK(file_exists(d / "library/templates/U1.json"));
    // 重名（不区分大小写）
    auto dup = t;
    dup["program"]["id"] = "U2";
    CHECK_EQ(lib.create(LibKind::Template, dup).message, std::string("已有同名模板 · 换一个名字"));
    // 自己改自己不算重名
    CHECK(lib.update(LibKind::Template, "U1", t, int64_t(1)).ok);
    // 生成的模板 id 以 U 开头
    auto t3 = ojson::parse(R"({ "program": { "name": "空白" } })");
    auto r3 = lib.create(LibKind::Template, t3);
    CHECK(r3.ok);
    CHECK_EQ(r3.entry->id.substr(0, 1), std::string("U"));
    CHECK_EQ(r3.entry->body["note"].get<std::string>(), std::string(""));
    // 内置模板：API 不能建；服务自己放进去后不能改不能删
    auto bi = ojson::parse(R"({ "builtIn": true, "note": "随软件提供", "program": { "id": "B1", "name": "只做气密" } })");
    CHECK_EQ(lib.create(LibKind::Template, bi).code, std::string("BAD_REQUEST"));
    CHECK(lib.create(LibKind::Template, bi, true).ok);
    CHECK_EQ(lib.update(LibKind::Template, "B1", bi, int64_t(1)).message, std::string("内置模板不能修改"));
    CHECK_EQ(lib.remove(LibKind::Template, "B1", int64_t(1)).message, std::string("内置模板不能删除"));
    // 导出成 PC 的 templates.json：内置的不在里面
    auto exp = ojson::parse(lib.export_pc_templates());
    CHECK_EQ(exp["version"].get<int>(), 1);
    CHECK_EQ(exp["templates"].size(), size_t(2));
    for (auto& x : exp["templates"]) CHECK(x.contains("note") && x.contains("from") && x.contains("program"));
}

TEST(store_library, import_from_pc_files_and_export_back) {
    TempDir d;
    spit(d / "library.json", std::string(R"({ "version": 1, "programs": [ )") + kPcProgram +
                                 R"(, { "id": "P2", "name": "", "steps": [] }, { "id": "P3", "name": "第三个", "modified": "2026-11-20T08:00:00", "steps": [] } ] })");
    spit(d / "templates.json", std::string(R"({ "version": 1, "templates": [ { "note": "n", "from": "f", "program": { "id": "U9", "name": "模板九", "steps": [] } }, { "note": "坏的" } ] })"));
    ProgramLibrary lib(d / "data", fixed_now);
    auto a = lib.import_pc_library(d / "library.json");
    CHECK_EQ(a.added, 2);
    CHECK_EQ(a.skipped, 1);   // 名字为空的那条
    auto again = lib.import_pc_library(d / "library.json");
    CHECK_EQ(again.added, 0);   // 已有同 id 的跳过
    auto t = lib.import_pc_templates(d / "templates.json");
    CHECK_EQ(t.added, 1);
    CHECK_EQ(t.skipped, 1);
    CHECK(!lib.import_pc_library(d / "nope.json").error.empty());
    // 列表按 modified 倒序
    auto list = lib.list(LibKind::Program);
    CHECK_EQ(list.size(), size_t(2));
    CHECK_EQ(list[0].id, std::string("P3"));
    CHECK_EQ(list[1].id, std::string("P261114162000123"));
    // 导出回 PC 的 library.json，PC 能原样读（形状一致）
    auto exp = ojson::parse(lib.export_pc_library());
    CHECK_EQ(exp["version"].get<int>(), 1);
    CHECK_EQ(exp["programs"].size(), size_t(2));
    CHECK(!exp["programs"][0].contains("revision"));
    CHECK_EQ(exp["programs"][1]["steps"][2]["params"]["gas"].get<std::string>(), std::string("A"));
}

TEST(store_library, corrupt_file_is_reported_and_not_overwritten) {
    TempDir d;
    make_dirs(d / "library/programs");
    spit(d / "library/programs/BAD.json", "{ 坏了");
    spit(d / "library/programs/MISMATCH.json", R"({ "id": "OTHER", "name": "x" })");
    ProgramLibrary lib(d.path, fixed_now);
    CHECK_EQ(lib.load_errors().size(), size_t(2));
    CHECK(lib.list(LibKind::Program).empty());
    CHECK_EQ(lib.create(LibKind::Program, ojson::parse(R"({ "id": "BAD", "name": "x" })")).code, std::string("REVISION_CONFLICT"));
    CHECK_EQ(slurp(d / "library/programs/BAD.json"), std::string("{ 坏了"));
}
