# 模块报告 · store（存储）

2026-11-26 · 命名空间 `reactor::store` · 目录 `src/store/`、`tests/store/`

`make -C tools/build -j8 test` 里 store 的 85 个用例全部通过（`run_tests store_`，连跑 3 次都稳定）。store 的源文件和测试另外用
`-Wall -Wextra -Wpedantic`（不带 Makefile 里的 `-Wno-unused-parameter`）单独编了一遍，没有警告（macOS clang 21）。
这台机器上没有 GCC，Linux GCC 9 **没有实际编过**；代码只用了 C++17 和 POSIX（`tm_gmtoff`、`pread`、`nftw`），已经逐处对照过 GCC 9 的已知问题。
全量测试里另有 5 个失败用例都在 `tests/net/`（别的模块，正在写），和 store 无关。

---

## 1. 文件

| 文件 | 内容 | C# 出处 |
|---|---|---|
| `store_util.hpp/.cpp` | `WallTime`（UTC 毫秒 + 时区偏移，对应 C# `DateTimeOffset`）、时间格式化 / 解析、原子写（tmp + fsync + rename + fsync 目录）、目录 / 文件工具、UTF-8、JSON 辅助（`json_num`、不会因非法 UTF-8 抛异常的 `dump_json`） | — |
| `csv.hpp/.cpp` | RFC 4180 转义 + 防公式注入、.NET 自定义数值格式（`"0.0##"` 等）、拆行 | `Service/TelemetryCsv.cs` 的 `Csv`、`Api/HistoryFiles.cs` 的 `SplitCsv` |
| `settings.hpp/.cpp` | `AppSettings`、`SettingsState`（档位边界、8×3 档 PID、来历 BandTune、容积标定、revision）、settings.json 读写、`SettingSpecs`（28 项）+ 校验、`ValueRules` 的本地拷贝、`patch_settings` / `set_band_bounds`、整定来历、`SettingsStore`（备份、原子写、800 ms 防抖） | `Models/AppSettings.cs`、`Models/SettingSpecs.cs`、`Models/ValueRules.cs`（部分）、`Service/SettingsStore.cs`、`Service/SettingsFileGuard.cs`、`Service/AutotuneBook.cs`（持久化部分）、`Service/TempService.cs`（默认 PID / 档位） |
| `event_store.hpp/.cpp` | `EventRecord`、JSONL 读写、`event_kinds::derive`、`EventStore`（5000 条环、按日 JSONL + `.seq`、后台写盘线程、订阅、resync / query） | `Service/EventStore.cs`、`Contracts/.../EventRecord.cs`、`Api/Endpoints.cs` 的 `/events` |
| `telemetry_csv.hpp/.cpp` | `TelemetryRow` / `TelemetryChannel` / `TelemetryModule`、表头生成、行生成、events CSV 行 | `Service/TelemetryCsv.cs` |
| `data_logger.hpp/.cpp` | `DataLogger`：遥测日文件、events CSV、会话 CSV、整文件写；后台线程，队列 20000，≤10 s flush | `Service/DataLogger.cs` |
| `session.hpp/.cpp` | `SessionManifest` JSON、`SessionBook`、开机 active → interrupted | `Service/SessionManifest.cs`、`Contracts/.../SessionManifest.cs` |
| `leak_record.hpp/.cpp` | `LeakRecord`：逐点 CSV、汇总 CSV、汇总 JSON；`leak_fit` 本地拷贝 | `Models/LeakRecord.cs`、`Models/LeakFit.cs`、`ViewModels/LeakViewModel.cs` 的 `SaveRecord` |
| `history_files.hpp/.cpp` | `safe_path`（白名单、安全拼路径）、`history_files`（列表 + 首末行时间、选日文件、按时间段输出、按列名重排） | `Api/SafePath.cs`、`Api/HistoryFiles.cs` |
| `program_library.hpp/.cpp` | `ProgramLibrary`：`library/programs/*.json`、`library/templates/*.json`，带 revision；导入 / 导出 PC 端文件 | 新增；条目形状照 `Control/.../PcProgram.cs`、`PcTemplate.cs`、`ProgramLibraryStore.cs`、`ProgramTemplateStore.cs` |
| `tests/store/store_test_util.hpp` | 临时目录（`mkdtemp("/tmp/reactor-store-test-XXXXXX")`，结束时递归删掉）、时刻构造 | — |
| `tests/store/test_*.cpp` | 9 个测试文件，组名都以 `store_` 开头 | 见 §5 |

目录都由构造参数传入，没有写死：`SettingsStore(config_dir)`、`EventStore(data_dir)`、`DataLogger(data_dir)`、`ProgramLibrary(data_dir)`、
`SessionBook::recover_interrupted(data_dir)`、`history_files::list(data_dir, …)`。C# 的默认目录是 `~/ParallelReactor-data` 和 `~/.config/ParallelReactor`，
新系统的默认值 `/var/lib/reactord`、`/etc/reactord` 由 `apps/reactord` 传进来（store 里没有默认值）。测试全部用 mkdtemp 建的临时目录。

---

## 2. 公共接口（摘要）

### 2.1 设置（`settings.hpp`）
```cpp
struct AppSettings { double t_max_sp = 200; … std::array<std::optional<double>,8> effective_volume; std::string language = "zh-CN"; };
struct Pid { double p, i, d; };
struct BandTuneMeta { std::optional<double> tuned_sp_c; std::optional<int64_t> tuned_at_ms, manual_edited_at_ms; };
struct SettingsState { AppSettings settings; std::array<double,2> band_bounds{100,200};
                       BandPidTable band_pids;  /*[8][3]*/  BandTuneTable band_tune; /*[8][3] optional*/
                       bool has_band_pids; int64_t revision; };
std::string settings_to_json(const SettingsState&);
bool settings_from_json(const std::string&, SettingsState&, std::string* err);
void tune_complete(SettingsState&, int ch_index, int band, std::optional<double> sp, int64_t now_utc_ms);
void tune_mark_manual(SettingsState&, int ch_index, int band, int64_t now_utc_ms);
Pid default_band_pid(int band);                 // P 40+20b、I 100+25b、D 15+5b
std::array<BandRange,3> band_ranges(bounds);    // 0–b0 / b0–b1 / b1–400

namespace setting_specs { all(); of(key); find("TMaxSp"); get(); set(); validate(key, v); cross_check(cur, key, v); }
namespace rules { range(); pid(); band_bound(); fmt_n(); }      // ValueRules 的本地拷贝
PatchResult patch_settings(SettingsState&, const std::vector<SettingChange>&, std::optional<int64_t> expected_revision, bool allow_local_only);
PatchResult set_band_bounds(SettingsState&, double low_mid, double mid_high, std::optional<int64_t> expected_revision);

class SettingsStore {
  explicit SettingsStore(std::string config_dir);
  LoadResult load(std::optional<int64_t> now_utc_ms = {}) const;   // {found, state?, backup_path, error}
  bool save_now(const SettingsState&, std::string* err = nullptr);  // 原子写
  void save_soon(const SettingsState&, int64_t now_mono_ms);        // 防抖 800 ms
  bool poll(int64_t now_mono_ms);  bool flush();  bool pending() const;
};
```
settings.json 的格式和 C# `PersistedState` 一样：属性名 **PascalCase**（System.Text.Json 默认，区分大小写），字段顺序 = C# 属性声明顺序，缩进 2 空格。
`BandTune` 的时间写成 `2026-11-18T02:00:00Z`。新加的 `"Revision"`（顶层）和 `"Settings.Language"` 两项，C# 读的时候会忽略，所以旧程序照样能读新文件。

### 2.2 事件（`event_store.hpp`）
```cpp
struct EventRecord { seq, seq_epoch, ts_ms, ts_local, level, kind, channel?, source, origin, client?, op_id?, title, message; };
std::string event_to_json(const EventRecord&);  std::optional<EventRecord> event_from_json(const std::string&);
namespace event_kinds { channel_of(origin); derive(origin, title); }
class EventStore {
  EventStore(std::string data_dir, std::function<void(const std::string&)> on_error = {});
  EventRecord append(const WallTime& ts, level, origin, title, message, source = "system", client = {}, op_id = {}, kind = "");
  int64_t head() const; int64_t oldest_buffered() const; int64_t dropped_lines() const;
  int subscribe(std::function<void(const EventRecord&)>); void unsubscribe(int);
  ResyncResult resync(int64_t after, int limit = 1000) const;
  std::vector<EventRecord> read_after(int64_t after, int limit = 500) const;
  EventsPage query(std::optional<int64_t> after, std::optional<int> limit) const;   // 缺省 500，上限 1000，带 head / hasMore
  void sync(); void stop();
  static recover_last_seq / max_seq_in / first_seq_in / read_files;
};
```
JSONL 每行字段顺序：`seq, seqEpoch, ts, tsLocal, level, kind, channel, source, origin, client, opId, title, message`（camelCase，中文不转义），和 C# 一样。

### 2.3 遥测 CSV 与数据记录（`telemetry_csv.hpp`、`data_logger.hpp`）
```cpp
struct TelemetryChannel { pv, pv_raw, sp_written, target_sp, output_pct, at, approach, approach_sp, curve_sp,
                          press_raw, press_psi, state, over_temp, valve, program_step, gas_mmol; };
struct TelemetryModule { configured, online, polls, fails, last_ok_at; };
struct TelemetryRow { WallTime time; marker; session?; ch[8]; srun, ctn, stir_on, stir_rpm_set, stir_motor_rpm, stir_ramp_ms,
                      stir_alarm, bus_voltage, gas[3], vent, estopped, temp/press/io/stir, sim; };
namespace telemetry_csv { header_fields(); header(); row_fields(row); format(row); event_header(); format_event(...); }

class DataLogger {
  DataLogger(std::string dir, on_error = {}, EventStore* events = nullptr);
  void telemetry(TelemetryRow);
  std::optional<EventRecord> event(t, level, origin, title, detail, event_source = "system", client = {}, op_id = {}, kind = "");
  std::string begin_session(test_id, const WallTime& now);  void end_session(final_row = {}, end_marker = "");
  void add_marker(text);  session_id(); session_key(); session_path();
  void write_file(rel_path, content);   // UTF-8 BOM，原子替换
  void sync(); void stop(); int64_t dropped_lines() const; static std::string safe_name(const std::string&);
};
```
173 列的顺序和 C# `TelemetryCsv.HeaderFields` 完全相同（测试逐个核对了关键下标）。「急停」列保留，恒写 0，这样文件格式不变。

### 2.4 会话、泄漏、历史文件、程序库
```cpp
struct SessionManifest { id, test_id, note?, started_at_ms, ended_at_ms?, state, source, client?, simulated, start_event_seq,
                         end_event_seq?, file, markers[], program_runs[], leak_tests[]; to_json(); from_json(); load(); rel_path(id); };
class SessionBook { SessionBook(write); current(); current_id(); begin(...); add_marker(...); tag_program_run(...);
                    tag_leak_test(...); end(...); static recover_interrupted(data_dir); };

class LeakRecord { LeakRecord(const WallTime& start, run_id = {}); add(...); t_ref(); samples_csv(); summarize(cells, judged);
                   summary_csv(rows); summary_json(rows, LeakParams); files(rows, params) /* 3 个 (相对路径, 内容) */; };

namespace safe_path { is_valid_name(type, name); is_leak_run_id(s); sub_dir(type, name); try_resolve(root, type, name); }
namespace history_files { list(dir, type, parse_range); select_daily_files(dir, from, to, prefix); write_range(files, from, to, out);
                          first_last_timestamp(path); last_complete_line(path); parse_row_time(line); try_parse_query_time(s, out);
                          column_map(); remap(); day_of(); parse_session_name(); }

class ProgramLibrary {
  ProgramLibrary(std::string data_dir, std::function<int64_t()> now_ms = {});
  std::vector<LibEntry> list(LibKind) const;  std::optional<LibEntry> get(LibKind, id) const;
  LibResult create(LibKind, ojson body, bool allow_builtin = false);
  LibResult update(LibKind, id, ojson body, std::optional<int64_t> expected_revision);
  LibResult remove(LibKind, id, std::optional<int64_t> expected_revision);
  ImportResult import_pc_library(path); ImportResult import_pc_templates(path);
  std::string export_pc_library() const; std::string export_pc_templates() const; std::vector<std::string> load_errors() const;
};
```

### 2.5 程序库的格式（新设计）
- `library/programs/<id>.json`：一个程序，形状和 PC 端 `library.json` 的 `programs[]` 里的一项相同，只是多一个 `"revision"`：
  `{ "id", "name", "model", "modified": "yyyy-MM-ddTHH:mm:ss"（本地时间）, "by", "draft", "steps": [ { "type", "name", "params": {…, "segments": [...] } } ], "revision": 3 }`
- `library/templates/<id>.json`：一个「我的模板」，形状同 PC 端 `templates.json` 的 `templates[]` 一项 `{ "note", "from", "program": {…} }`，再加 `"revision"`；
  可以有 `"builtIn": true`（内置模板：只能由服务自己放进去，`create(..., allow_builtin=true)`；API 上不能建、不能改、不能删，导出 PC 文件时不带）。模板的 id = `program.id`。
- 步骤参数**原样保存**（压力 MPa、sel 协议码、未知字段都保留），存储层不解释，校验交给 core / cmd。存储层只检查这些：名字不能空、≤40 个 UTF-16 字符；
  模板说明 ≤120；模板名不区分大小写不能重；`steps` 必须是数组、每一步必须是带 `type` 的对象；id 只能用 `[A-Za-z0-9_-]{1,64}`。
- 缺省值照 PC 端：`model` = PR8、`by` = 本机、`draft` = false、`modified` = 服务器本地时间；id 缺省时生成 `P` / `U` + `yyMMddHHmmssfff`，同一毫秒再来一个就加 `-1`。
- 乐观并发：新建时 revision = 1，之后每次保存 +1。`update` / `remove` **必须**带读到的 revision：不带返回 `PRECONDITION_REQUIRED`，对不上返回 `REVISION_CONFLICT`（同时给出 `current_revision`）。
  已有的 id 再 create 也返回 `REVISION_CONFLICT`。
- 读不了的文件（坏 JSON、id 和文件名对不上）记进 `load_errors()`，原样留在磁盘上不覆盖；对同一个 id 的 create 会被拒。
- 列表按 `modified` 倒序排，时间相同再按 id 排。

---

## 3. 线程约定

| 对象 | 哪个线程可以调 |
|---|---|
| `SettingsState`、`patch_settings`、`setting_specs`、`rules`、`settings_to/from_json` | 纯值 / 纯函数，不加锁，由持有者（控制线程）保证单线程 |
| `SettingsStore` | **只给一个线程（控制线程）用**。`poll()` 到期时在调用线程里同步原子写盘：几 KB + fsync，eMMC 上一般十几到几十毫秒。如果不想在控制线程上 fsync，可以在控制线程上 `settings_to_json()`，再在存储线程上调 `write_file_atomic()` |
| `EventStore::append` | 任意线程。分配 seq、入环、入队、通知订阅者都在同一把锁里，所以 JSONL 行序、环、推送顺序都严格按 seq 递增 |
| `EventStore` 的订阅回调 | 在 append 的线程上、**存储锁内**执行：只能做非阻塞入队；不能阻塞，不能回调存储（包括 `unsubscribe`，否则死锁） |
| `EventStore` 查询（`resync` / `read_after` / `query` / `head`） | 任意线程 |
| `EventStore` 写盘 | 内部 1 个后台线程：有内容后最迟 2 s flush 并写 `.seq`；失败时丢弃并计数，30 s 后重试，同一故障只报一次（`on_error` 在后台线程上调用） |
| `DataLogger` 全部公开方法 | 任意线程。行在调用方线程上格式化后入队；会话 / 标记状态有锁。内部 1 个后台线程写盘，至少每 10 s flush 一次；队列 20000 条，满了丢弃并计数；`on_error` 在后台线程上调用 |
| `SessionBook` | 单写者（控制线程）；`current()` 返回副本，任意线程可读 |
| `LeakRecord` | 单线程（控制线程） |
| `safe_path` / `history_files` | 任意线程（首末行时间的缓存有锁） |
| `ProgramLibrary` | 内部一把锁，任意线程都能调（接口线程直接调就行，不用经过控制线程） |

停止的顺序：先 `DataLogger::stop()`，再 `EventStore::stop()`，最后 `SettingsStore::flush()`（析构函数也会自动 stop）。

---

## 4. 从 C# 逐条移植的内容

- **settings**：`AppSettings` 的默认值、`SettingSpecs` 28 项（标签 / 单位 / 范围 / 步进 / 说明 / 写设备 / 只能本地改）、`Validate` 和 `CrossCheck` 的中文文案、
  `ValueRules.Range` 的消息格式（`{标签}须在 {min}–{max} {单位} 之间`，数字按 `0.##` 格式）、`Pid`、`BandBound`；`SettingsFileGuard` 的两种备份
  （没有 `"BandTune"` 时先备份 `.bak-pre-v0.6`，只备份一次；读不了另存 `.bak-unreadable-yyyyMMdd-HHmmss`，用本地时间；备份已存在就不覆盖）；
  `TempService.ImportBandPids` 和 `AutotuneBook.Import` 的导入规则（BandBounds 长度为 2 才用；PID 长度为 3 才用；来历三项全空当作没有；不带时区的时间按 UTC）；
  类型不对就把整个文件当作读不了（和 System.Text.Json 的默认行为一样）。
- **EventStore**：seq 跨重启连续（取 `.seq` 和最新 JSONL 里能解析出的最大 seq 中较大的一个；坏行、半行跳过）、半行先补换行、
  `ReadFiles` 跳过整体都在 afterSeq 之前的旧文件、`Resync` 的 gap 规则、`/events` 的 limit 夹取和多取一条判断 hasMore、`EventKinds.Derive` 全表。
- **TelemetryCsv / DataLogger**：`Csv.Escape`（`= + - @` 开头、又不是数字的文本前面加 `'`；数字的判断规则照 `double.TryParse(Float, Invariant)`）、
  各列的 .NET 数值格式（实现了 .NET Core 3.0+ 的做法：先取 15 位有效数字，再按十进制四舍五入、远离零）、`yyyy-MM-ddTHH:mm:ss.fffzzz`、
  表头只能在末尾追加；同名文件表头不同就另起 `-1`、`-2`；UTF-8 BOM 只在新文件开头写一次；半行先补换行；标记只给下一行用；
  会话文件和结束时的最后一行；`SafeName`；写盘失败按文件只报一次，两次上报至少隔 10 分钟。
- **Session**：manifest 字段和顺序（camelCase、缩进）、读的时候属性名不区分大小写、允许注释、开机把 active 改成 interrupted（endedAt 取 CSV 的修改时间）、写文件带 BOM。
- **Leak**：三个文件的表头、列格式、runId；summary.json 的键顺序（`channels` 里的对象用 PascalCase，因为 C# 用的是默认命名）、assumptions 原文。
- **SafePath / HistoryFiles**：白名单正则（实现上相当于 `\z` 收尾，末尾带换行的名字也拒）、会话名的长度按 UTF-16 码元算、列表、首末行时间
  （只读文件尾部 64 KB，必要时退回整文件扫描，按长度 + 修改时间缓存）、按本地日期选日文件、`[from, to)` 输出、表头不同的文件按列名重排。

---

## 5. 测试（`tests/store/`，85 个用例）

| 文件 | 组 | 内容 |
|---|---|---|
| `test_csv.cpp` | `store_csv` | 翻译 `TelemetryCsvTests` 的 Escape 全部用例（含注入保护），外加 .NET 数值格式的进位和远离零舍入、ISO 时间、拆行往返 |
| `test_telemetry_csv.cpp` | `store_telemetry` | 翻译 `TelemetryCsvTests`：表头 173 列、关键下标、不重复、空行列数、值格式、程序步 / 吸气量 / 电机转速都在末尾、events 行 |
| `test_settings.cpp` | `store_settings` | **读一份按 C# 序列化器字段名构造的 settings.json**（7 位小数的 UTC 时间、数组里有 null、BandTune 有三种来历）；字段名和顺序核对；往返；翻译 `SettingsUpgradeTests` 4 个用例；类型不对的文件读不了；部分数据的导入规则；`PidTuneTests` 的来历用例；原子写和防抖；翻译 `SettingSpecsTests` 全部用例；patch 的 revision 冲突、全有或全无、交叉检查、只能本地改的项；档位边界和 PID 规则 |
| `test_event_store.cpp` | `store_events` | 翻译 `EventStoreTests`：seq / JSONL / 订阅、跨重启连续、坏 JSONL、8 线程并发下顺序不乱、读文件时跳过旧文件、resync 的命中和 gap、重启后回落到读文件、Derive 全表；另加 query 分页、写盘失败 |
| `test_data_logger.cpp` | `store_logger` | 翻译 `DataLoggerTests` 全部用例 + `DataLogger_SessionKey_IsFileStem`，另加事件同时写入 EventStore |
| `test_session.cpp` | `store_session` | 翻译 `SessionManifestTests`；读 C# 写出的 manifest（BOM、`\uXXXX` 转义、大小写不同的属性名） |
| `test_leak.cpp` | `store_leak` | 拟合；逐点 CSV 逐列核对；汇总 CSV / JSON 的列、键名和键顺序 |
| `test_history.cpp` | `store_history` | 翻译 `SafePathTests`（包括 NFD 拒绝、末尾换行）和 `HistoryFilesTests` 全部用例 |
| `test_program_library.cpp` | `store_library` | PC 形状的程序往返、缺省值和生成 id、校验、revision 冲突、PRECONDITION_REQUIRED、模板重名、内置模板只读、从 PC 的 library.json / templates.json 导入再导出、坏文件不覆盖 |

---

## 6. 和 C# 不一样的地方

1. **settings.json 的数字和时间写法**：数字用 nlohmann 输出，整数值写成整数（和 C# 一样），小数用能原样读回的最短写法；极小或极大的数用指数时写成 `1e-05`，C# 写成 `1E-05`，两边都能读。
   时间只保留到毫秒（C# 写 7 位小数），小数末尾的 0 去掉。带 `+08:00` 偏移的时间按偏移换算成 UTC（C# 会读成本地时间，再被 SpecifyKind 当成 UTC，是个小 bug）。
2. **新增两个字段**：`"Revision"`（顶层，防覆盖用，会持久化。C# 的 `SettingsRevision` 只在内存里，每次开机从 0 开始）和 `"Settings.Language"`（默认 `"zh-CN"`）。
3. **写盘方式**：settings.json、会话 manifest、`write_file` 写的整文件都是原子写（tmp + fsync + rename），C# 用的是 `File.WriteAllText` 直接覆盖。
   备份文件按原字节写（C# 写的是解码以后、去掉 BOM 的文本）。`.seq` 也用 tmp + rename，但不 fsync（丢了还能从 JSONL 恢复）。
4. **EventStore 的 flush 时机**：第一条未 flush 的内容写入后，最迟 2 s 就 flush。C# 要等空闲 2 s 才 flush，事件一直不断时会拖很久。
5. **会话 manifest 的中文不转义**（C# 默认编码器会转成 `\uXXXX`），两边都能读。读的时候不接受末尾多余的逗号（C# 设了 `AllowTrailingCommas`，但我们自己写出来的文件不会有）。
6. **NFC 检查是近似的**：C# 用 `IsNormalized(FormC)`；这里没有完整的 Unicode 规范化表，改成含组合附加符号（U+0300–036F 等几个区段）的文件名一律拒绝，只会多拒，不会多放。
7. **数值舍成 0 时不带负号**（如 -0.0004 按 `0.###` 写成 `0`）。按 .NET 自定义格式的源码应该也是这样，但没在 .NET 上实测。
8. **patch_settings 的交叉检查用全部改完以后的值**：一次 PATCH 里同时降低超温报警值和温度 SP 上限是允许的。只改一项时和 C# `CrossCheck(current, key, value)` 完全一样。
9. **程序库列表按 modified 倒序**（每个条目单独一个文件，没有数组顺序）。PC 端 library.json 的顺序是「新建 / 复制的放最前」，效果接近。
10. `DataLogger` 不再带「模拟模式默认目录」的逻辑（`DefaultDirFor(sim)`），目录完全由调用方传入。
11. `HistoryFiles.ParseSessionName` 返回 `(testId, "yyyyMMdd-HHmmss")` 字符串，不再解析成本地 DateTime（需要时调用方自己转）。

---

## 7. 待定的问题

1. **内置模板由谁放进服务**：PC 端的内置模板写在代码里（`ProgramTemplates.BuiltIn(model)`，按型号生成）。触屏要显示「内置模板」，就得有人把它们移植到 C++，
   在开机时用 `create(Template, …, allow_builtin=true)` 放进去，或者另外做一个只读的来源。现在 store 只提供存放和只读保护。
2. **首次启动从 PC 导入**：`import_pc_library` / `import_pc_templates` 已经有了，但 PC 的文件怎么到板子上还没定（U 盘？新增一个上传端点？）。
3. 程序库保存时，`modified` / `by` 要不要由服务端按调用方身份重写？现在的做法：客户端给了就保留，没给才填服务器时间和「本机」。
4. 模板重名现在返回 `BAD_REQUEST`（消息「已有同名模板 · 换一个名字」）。要不要单独定一个码或改成 409？需要 api 层定。
5. `Language` 字段的取值格式（`zh-CN` / `en` …）要和 reactor-hmi 的语言表对齐。
6. 程序库变更后要不要推送 WS 消息（比如 `library.changed`），好让另一端刷新？store 没做，建议 api 层在 create / update / remove 成功后推送。
7. 汇总 JSON 里的 `sampleSec` 由泄漏测试流程（ctl）给出，C# 里是 `LeakViewModel.SampleSec`。

---

## 8. ctl / api / apps 要接上的东西

**reactord 启动（apps/reactord）**
1. `SettingsStore settings(config_dir); auto r = settings.load();` 读到了就用 `r.state`，否则用默认的 `SettingsState{}`；`r.backup_path` 不为空时记一条 warn 事件。
   `state.has_band_pids == false` 时，控制层可以从仪表读 PID 作种子（同 C# `seedFromInstrument`）。
2. `EventStore events(data_dir, on_error);`，然后 `DataLogger logger(data_dir, on_error, &events);`。on_error 在后台线程上调用，里面只能 LOGW 或投递，不能直接改状态。
3. `auto fixed = SessionBook::recover_interrupted(data_dir);` 不为空时记事件：「上次未结束的测试会话已标为 interrupted」（warn，来源「系统」）。
   然后 `SessionBook sessions([&](auto& rel, auto& json){ logger.write_file(rel, json); });`。
4. `ProgramLibrary library(data_dir);`，`library.load_errors()` 有内容时记 warn。
5. 退出：`settings.flush()`；会话没结束就先结束；然后 `logger.stop(); events.stop();`。

**ctl（控制线程）**
- 每个周期组一行 `TelemetryRow`（`time = WallTime::local(wall_ms())`；`estopped` 一直是 false）交给 `logger.telemetry(row)`。
- 事件统一走 `logger.event(t, level, 来源, 标题, 详情, source, client, opId, kind)`，并且**显式给 kind**（Derive 只是兼容旧调用）。
- 设置改动：`patch_settings(state, changes, expected_rev, is_local)` → 成功后 `settings.save_soon(state, mono_ms())`，记 `system.settingsChanged` 事件；
  改到写设备的项（搅拌电流 / 细分 / 满量程 / 传动比）要下发到设备。控制循环里周期调用 `settings.poll(mono_ms())`。
- 档位边界：`set_band_bounds`；PID 手改：改 `state.band_pids` + `tune_mark_manual`；整定完成：写 PID + `tune_complete`；每次改完都 `save_soon`。
- 容积标定的结果写 `state.settings.manifold_volume / effective_volume / volume_calib_utc_ms`，泄漏测试完成后写 `last_leak_test_utc_ms`，都要 `++state.revision` 再 `save_soon`。
- 会话：`logger.begin_session(testId, now)` + `logger.add_marker("会话开始 " + key)` + `sessions.begin(key, testId, note, wall_ms(), source, client, sim, events.head(), base_name(path))`；
  结束时按 C# `EndTestSession` 的顺序调 `add_marker`、`end_session(最后一行, 结束标记)`、`sessions.end(...)`。
- 泄漏测试：`LeakRecord rec(WallTime::local(now))`，每次读压调 `rec.add(...)`；结束时 `rows = rec.summarize(cells, &rates)`，
  然后对 `rec.files(rows, params)` 的每一项调 `logger.write_file(path, content)`，再 `logger.add_marker("泄漏测试结束 " + runId)`、`sessions.tag_leak_test(...)`。

**api**
- `GET /events`：`events.query(afterSeq, limit)` → `EventsPageDto { seqEpoch, head, afterSeq, count, hasMore, items }`（字段名照 C# DTO）。单条事件可以直接用 `event_to_json`。
- WS：`events.subscribe(cb)`，回调里只做非阻塞入队；断线重连用 `events.resync(afterSeq, 1000)`，`gap` 为 true 时让客户端走 `GET /events`。
- `GET /files?type=`：`history_files::list(data_dir, type)` → `HistoryFileInfo { name, type, bytes, modifiedAt, from, to, day }`（时间用 `fmt_utc_json`）。
- 下载：先 `safe_path::try_resolve(data_dir, type, name)`（返回空 → 404），然后按文件流式输出。
- `GET /telemetry?from&to`：`try_parse_query_time` → `select_daily_files` → `write_range(files, from, to, sink)`；sink 返回 false 表示客户端断开，输出就此取消。
- `/library/programs`、`/library/templates`：直接调 `ProgramLibrary`。revision 建议用 `If-Match` 头或正文里的 `revision` 字段传；
  错误码到 HTTP 状态的映射建议：`REVISION_CONFLICT` → 409、`PRECONDITION_REQUIRED` → 428、`NOT_FOUND` → 404、`BAD_REQUEST` → 400、`INTERNAL` → 500。
  响应里要带 `revision`（LibEntry 把它和 body 分开给出）。
- `PATCH /settings`、`PUT /pid/bands`（只在本地入口开放）：把命令投递给控制线程，由控制线程调 `patch_settings` / `set_band_bounds`；
  `PatchResult.code` 按现有映射转成 HTTP 状态（`PARAM_OUT_OF_RANGE` / `PARAM_CROSS_CHECK` 的 detail = `{min, max, value}`）。
