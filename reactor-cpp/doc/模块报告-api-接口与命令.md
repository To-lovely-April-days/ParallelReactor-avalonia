# 模块报告 · cmd / api（命令层、控制租约、全部 HTTP + WebSocket 端点、reactord 组装）

2026-11-26 · 命名空间 `reactor::cmd`、`reactor::api` · 目录 `src/cmd/`、`src/api/`、`tests/cmd/`、`tests/api/`，另改了 `apps/reactord/main.cpp`、
`src/store/program_library.*`（内置模板 upsert）、`src/store/settings.*`（界面偏好 6 项）、`src/ctl/ctl_builtin_templates.*`（种模板改用 upsert）。

`make -C tools/build -j8 test`：全量 **514 个用例，0 失败**（`cmd_` 23 个、`api_` 9 个是本阶段新增；`api_` 连跑 5 次稳定），编译无警告。
`src/cmd/*.cpp`、`src/api/*.cpp` 另用 `-Wall -Wextra -Wpedantic -Wshadow` 单独查过，无警告（macOS clang）。GCC 9.4 / aarch64 没实测。

---

## 1. 文件

| 文件 | 内容 | C# 出处 |
|---|---|---|
| `cmd/cmd_source.hpp` | `Source`（本地 / 远程，`label()`「远程（PC-1 · 令牌 op）」）、`started_by_of`、`CommandPolicy::strict` | `CommandSource.cs`、`CommandPolicy.cs` |
| `cmd/cmd_arbiter.*` | `ControlArbiter`（模式 + 持有者，锁内成对）、判定表 `decide` | `ControlArbiter.cs` |
| `cmd/cmd_lease.*` | `ControlLease` 状态机（local / requested / remote / remoteLost）、迁移锁外按序送达 | `ControlLease.cs` |
| `cmd/cmd_util.*` | 128 位随机 hex、GUID、客户端名清洗 | `ClientNames.cs` |
| `cmd/cmd_commands.hpp`、`cmd_commands.cpp`、`cmd_commands_ch.cpp` | `Commands`：每个命令的仲裁 → 守卫 → 执行，结果 `ctl::OpResult`（码 + 中文） | `OperatorCommands.*.cs`、`MainViewModel.ComputePreRun`、`RunSelection.cs`、`ControlBridge.cs`（onRemoteLost） |
| `api/api_service.*` | `ApiService`：组装、写命令公共管线、租约迁移 → 审计 / WS、WS 中枢线程、各 DTO | `Endpoints.Write.cs CommandAsync`、`CommandResults.cs`、`ControlService.cs`、`WsHub.cs`、`HmiApiSources.cs` |
| `api/api_util.*` | 正文解析（注释 + 尾逗号）、参数绑定（文案照抄）、写命令信封、C# 顺序的错误体 | `Endpoints.Write.cs` 参数部分、`ApiErrors.cs` |
| `api/api_routes.hpp` | 路由分文件登记的内部头、`on_control<T>()`（在控制线程上算并等结果） | — |
| `api/api_routes_read.cpp` | 只读端点、文件下载、遥测流、泄漏测试、会话 GET | `Endpoints.cs`、`Endpoints.Sessions.cs` |
| `api/api_routes_write.cpp` | 现有写端点 | `Endpoints.Write.cs`、`Endpoints.Sessions.cs`、`HmiRemoteCommands.cs` |
| `api/api_routes_control.cpp` | 控制权端点 + 本机允许 / 拒绝 / 切回本地 | `Endpoints.Control.cs`、`OperatorCommands.Control.cs` |
| `api/api_routes_local.cpp` | 契约 §3.1–3.7、§3.12：PATCH 设置、档位、到位范围 / 防超调、手动阀控、全部确认、语言、程序库、容积标定、重启服务 | 新增 |
| `api/api_extras.cpp` | 契约 §2.1–2.3 快照 / progress / 报警扩展、修订号、§3.8 `/runs`、§3.9 `/trend`、§3.11 采样、§3.13 通讯测试、§3.16 `/storage`、§3.17 `/users` | 新增 |
| `api/api_runs.*` | `RunsBook`：运行记录（程序运行 + 泄漏测试），`<data>/runs/<id>.json` | 新增 |
| `api/api_routes_system.cpp` | 契约 §3.14 网络（nmcli）、§3.15 U 盘 / 导出 | 新增 |
| `api/api_system.*` | `run_process`（fork + execve，不经 shell，超时 SIGKILL）、NTP 探测、nmcli 输出解析、U 盘挂载点 | `HmiApiSources.ProbeClockSynced` |
| `tests/cmd/test_cmd_lease.cpp` | 判定表 48 组、文案、租约全部场景、并发、清洗 | 逐条翻译 `ControlArbiterTests.cs`、`ControlLeaseTests.cs` |
| `tests/cmd/test_cmd_guards.cpp` | 命令守卫 / 错误码 / 文案（不起线程的核心上直接调） | 新写 |
| `tests/api/api_test_util.hpp`、`test_api_server.cpp` | 进程内起完整后台 + 真 socket 的集成测试 | 参考 `ApiHostIntegrationTests`、`ApiWriteIntegrationTests` |

---

## 2. 端点表

入口：两边 = TCP + Unix；本地 = 只在 Unix（TCP 上 404「没有这个端点」）。类：C 控制（TCP 上要租约）/ S 停止 / R 记录。幂等：必 = 缺键 428。
执行：「控制线程」= 投递 `ControlCore::post`；「接口线程」= 线程安全的纯逻辑直接在工作线程上做。

### 2.1 现有 GET（任何角色）

| 路径 | 入口 | 数据来源 |
|---|---|---|
| `/health` | 两边（免鉴权、不限流） | — |
| `/device` | 两边 | hardware.json + 快照（另加 `io` 接线，契约 §3.13） |
| `/snapshot` | 两边 | `ctl::to_json` + 契约 §2.1 扩展 |
| `/preflight` | 两边（2/s） | 控制线程 `Commands::preflight` |
| `/device/comm` | 两边 | 快照 + `ApiStats` + EventStore（另加 `pollSec / offlineAfter / modules`） |
| `/channels`、`/channels/{id}`、`/channels/{id}/program/run` | 两边 | 快照（`/channels/{id}` 带曲线） |
| `/pid` | 两边 | 快照设置副本 + 自整定快照（另加 `controller`、`tune*`） |
| `/events?afterSeq&limit` | 两边 | `EventStore::query`（`{seqEpoch, eventSeqHead, afterSeq, count, more, items}`） |
| `/alarms?state=` | 两边 | 控制线程报警表（2 s 内不回就用快照副本），每项加契约 §2.3 字段 |
| `/settings` | 两边 | 快照设置副本（另加 `ui / program / storage / language / values / bandBounds`） |
| `/program-schema` | 两边 | `program_schema::build`，按设置 revision 缓存 |
| `/files?type=`、`/files/{type}/{name}` | 两边（下载并发 2） | `history_files` / `safe_path` + `net::file_response`（Range、弱 ETag、304） |
| `/telemetry?from&to` | 两边（下载） | `write_range` → chunked 流 |
| `/leak-tests`、`/leak-tests/{runId}`、`…/data.csv`、`…/summary.csv` | 两边 | `leak/` 文件（列表另加 `channels / pass / note`） |
| `/sessions`、`/sessions/current`、`/sessions/{id}` | 两边 | `SessionBook` + manifest 文件 |
| `/control`、`/control/requests/{id}` | 两边 | 租约（接口线程） |

### 2.2 现有写端点

| 方法 路径 | 类 | 幂等 | 角色 | 命令（控制线程，除注明） |
|---|---|---|---|---|
| POST `/estop` | S | | estop 位 | `estop` → `request_stop_all(stopStir=true)`，202，不锁存 |
| POST `/stop-all` `{stopStir?}` | S | | operator 或 estop | `stop_all` → `request_stop_all` |
| POST `/run` | C | 必 | operator | `run(false)` |
| POST `/channels/{id}/enable` | C | | | `enable_channel` |
| POST `/channels/{id}/start` `{tempC?}` | C | | | `start_channel`（启用 + 加热） |
| POST `/channels/{id}/stop`、`/disable` | S | | | `stop_channel` → `ControlCore::stop_channel` |
| POST `/channels/copy-config` `{from, to?}` | C | 必 | | `copy_channel_config`（另带到位范围 / 防超调 / 升温方式） |
| PUT `/channels/{id}/volume` `{ml}` | C | | | `set_volume` |
| POST `/channels/{id}/gas-counter/reset` | C | | | `reset_gas_counter` |
| POST `/channels/{id}/heat/start` `{tempC?}` | C | | | `start_heat` |
| POST `/channels/{id}/heat/stop` | S | | | `stop_heat`（被程序占用 = 中止程序） |
| PUT `/channels/{id}/temperature/setpoint` `{tempC}` | C | | | `set_setpoint` → `retarget` |
| PUT `/channels/{id}/temperature/mode` `{spMode}` | C | | | `set_sp_mode` |
| PUT `/channels/{id}/profile` `{segments[]}` | C | | | `replace_profile` → `set_profile` |
| POST `…/profile/start` / `…/profile/stop` | C / S | | | `start_profile` / `stop_profile` |
| PUT `/channels/{id}/pid/{band}` `{p,i,d}` | C | | **admin** | `set_pid` |
| POST / DELETE `/channels/{id}/autotune` | C / S | | | `Autotune::start / cancel` |
| PUT `/stir` `{on?, rpm?}` | 只有 `{on:false}` 是 S | | | `set_stir`（rpm 先于 on） |
| PUT `/channels/{id}/valve`、`/gas/inlets/{inert\|A\|B}`、`/gas/vent` `{open}` | 开 C / 关 S | | | `set_reactor_valve / set_gas_inlet / set_vent` → `write_valves` |
| POST `/leak-tests` / DELETE `/leak-tests/current` | C / S | 必 / | | `LeakTest::start / abort(strict)` |
| PUT `/channels/{id}/program` `{steps, name?, sourceId?}` | C | | | `ProgramEngine::replace_program` |
| POST / DELETE `/channels/{id}/program/run` `{sessionId?}` | C / S | 必 / | | `run(startedBy)` / `abort` |
| POST `/channels/{id}/program/copy` `{to}` | C | 必 | | `copy_program(strict)` |
| POST `/alarms/{id}/ack` | R | | | `ack_alarm` |
| POST `/sessions` / DELETE `/sessions/current` / POST `…/markers` | R | 必 / / 必 | | `begin_session / end_session / add_marker` |
| POST `/control/request` `{clientName}` | R | 必 | | 接口线程：`ControlLease::request` |
| POST `/control/heartbeat` | — | | operator | 接口线程（10/s，不审计，同 C#） |
| POST `/control/release` | R | | | 接口线程：`ControlLease::release` |

### 2.3 新增端点（`doc/接口契约-新增.md` + 设计文档 §5.4）

| 方法 路径 | 入口 | 类 | 幂等 | 说明 |
|---|---|---|---|---|
| POST `/control/requests/{id}/grant`、`/deny` | 本地 | R | | 允许 / 拒绝（拒绝后该令牌 30 s 冷却） |
| POST `/control/take-back` | 本地 | R | | 切回本地（C# TryTakeBackControl） |
| PATCH `/settings` `{revision, values{点路径: 值}}` | 本地 | R | | 全有或全无；`result {revision, changed[]}` |
| PUT `/pid/bands` `{lowMidC, midHighC, revision?}` | 本地 | C | | 整定中 409 `AUTOTUNE_ACTIVE` |
| PUT `/channels/{id}/temperature/band` `{bandC}`、`…/anti-overshoot` `{on}` | 两边 | C | | 存 `<data>/channel-prefs.json` |
| GET `/channels/{id}/program` | 两边 | — | | 运行中 = 下发副本，否则草稿 |
| PUT `/gas/manual` `{on}` | 本地 | 开 C / 关 S | | 关时顺手全关阀 |
| POST `/alarms/ack-all` | 两边 | R | | `result {acked}` |
| PUT `/system/language` `{code}` | 本地 | R | | 线上短码，存储 `zh-CN` |
| POST `/system/restart` | 本地 | S | | 主循环退出（systemd 重新拉起） |
| GET / POST / PUT / DELETE `/library/programs[/{id}]`、`/library/templates[/{id}]` | 两边 | R | POST 必 | `modified / by` 服务端填；冲突 / 重名 / 只读 / 满 |
| POST `/library/import` `{source:"usb"}` | 本地 | R | 必 | U 盘根目录 `library.json` / `templates.json` |
| GET `/runs?limit`、`/runs/{id}`，PUT `/runs/{id}/note` | 两边 | R | | 见 §6 |
| GET `/trend?from&to&stepMs` | 两边 | — | | 24 h 内存环 + 每日 CSV |
| GET `/leak-tests/current/samples` | 两边 | — | | 测量阶段每 10 s 一点 |
| POST `/calib` `{channel, waterMl?}` / POST `/calib/continue` / DELETE `/calib/current` / GET `/calib` | 本地 | C / C / S / — | POST 必 | `CALIB_RUNNING`、`CALIB_NOT_WAITING` |
| POST `/device/comm/{module}/test`、`…/reconnect` | 本地 | R / C | | test = 真读一笔并计时 |
| GET `/network`；PUT `/network/wifi`；POST `…/wifi/scan`、`…/wifi/connect`、`…/wifi/disconnect`；DELETE `…/wifi/saved/{ssid}`；PUT `/network/ip` | 本地 | R | | nmcli，scan / connect 202 |
| GET `/usb`；POST `/export` | 本地 | R | export 必 | 202，结果走 `export.done / failed` 事件 |
| GET `/storage`、`/users` | 本地 | — | | |

新错误码都按「未列出 → 409」：`REVISION_CONFLICT`、`NAME_CONFLICT`、`READ_ONLY`、`LIBRARY_FULL`、`USB_NOT_PRESENT`、`EXPORT_BUSY`、`CALIB_RUNNING`、`CALIB_NOT_WAITING`、`NETWORK_BUSY`。

---

## 3. 写命令公共管线（`ApiService::command`，顺序照 C# CommandAsync）

0. 带了有效 `X-Control-Lease` → 来源标签用申请时的客户端名（这次校验也算一次心跳）。
1. 角色：`/estop` 只看 estop 位；其余 `has_role` 或（停止类且有 estop 位）。本机身份 = admin + estop，直接过。
2. 正文：空 = 无正文；`//`、`/* */` 注释和尾逗号照收；不是对象 / 解析不了 → 400（文案照抄）。
3. 幂等：键 ≤128；按「令牌名 + 键」隔离；`acquire` 处理重放（`Idempotent-Replay: true`，原样回放）/ 不同正文 409 / 进行中等 5 s；必填缺键 428。
4. 动作类：远程控制类 `lease.validate`（`MODE_LOCAL` / `NOT_CONTROL_HOLDER` / `CONTROL_LEASE_EXPIRED`）；本机不查租约，由命令层仲裁（远程控制中 → `MODE_REMOTE`）。
5. 参数绑定（400 / 404 `CHANNEL_NOT_FOUND`，可带 `detail.key`）。
6. 执行：投递控制线程；5 s 没开始 → 撤销，503 `UI_THREAD_TIMEOUT`；开始了 5 s 没跑完同步头 → 504 `OUTCOME_UNKNOWN`；同步头完成后等硬件尾部 2 s，没回来 → 202 + `HW_UNCONFIRMED`，
   最终结果再记 `op.<action>.result`、推第二条 `opEvent`（同 opId）、那时才回填幂等表。
7. 审计 `op.<action>`（其 seq = `opEventSeq`；本机命令标题「触屏命令」、source = local）→ 信封 → WS `opEvent` → 幂等完成。前段被拒同样审计，且撤销自己新建的幂等条目。
- 信封字段顺序同 C#：`{ok, opId, status, result, warnings, opEventSeq}` / `{ok:false, opId, error{code, message, detail}, opEventSeq}`；GET 的错误体 `{ok:false, error{code, message, detail}}`（`detail` 为 null 时也写出）。
- WiFi 连接的正文含密码：`redact_body`，审计里只写「（正文含密码，不记录）」，结果事件只写 SSID。

---

## 4. WebSocket `/api/v1/ws`

| type | 方向 | 内容 |
|---|---|---|
| `hello` | S→C 首条 | apiVersion、serverTime、clockSynced、bootId、simulated、seqEpoch、eventSeqHead、telemetrySeqHead、control、eventRingSize（同 C# HelloMsg） |
| `telemetry` | S→C 每周期 | 快照 + 契约 §2.1 扩展；连上时立即补发当前帧 |
| `progress` | S→C 每周期 | 契约 §2.2 整份；只发给订阅了 `progress` 的连接 |
| `event` / `alarm` | S→C | EventRecord；AlarmDto + 契约 §2.3 字段 |
| `control` | S→C | C# ControlMsg（`leaseId` 恒 null）+ 新增 `client`、`token`、`pending{requestId, clientName, tokenName, expiresAt}`（触屏弹允许 / 拒绝框用） |
| `opEvent` | S→C | 每条写命令结局（含本机命令，source = local） |
| `notice` | S→C 只发本机连接 | 控制核心的提示（C# Toast） |
| `gap` / `subscribed` / `resynced` / `pong` / `error` | S→C | 同 C# |
| `subscribe {topics, resumeAfterEventSeq?, seqEpoch?}`、`unsubscribe`、`resync {afterSeq}`、`ping`、`hb {lease?}` | C→S | 同 C#；topics 多一个 `progress` |

扇出：快照 / 事件 / 报警 / 控制消息都只做入队，由一个中枢线程每条序列化一次再 `ws_broadcast`（遥测、progress 走 50 帧丢旧队列，其余走 2000 条事件队列，满了 1008）。
事件存储的订阅回调在存储锁内，所以不直接碰服务器，避免和 I/O 线程的审计钩子互锁。

---

## 5. 租约 / 控制权

照 C# `ControlLease`：申请 → 触屏允许 / 拒绝（`controlRequestTimeoutSec` 超时 = 拒绝）或 `autoGrantRemote` 当场授权；leaseId 128 位随机，只在申请响应
和带一次性 `requestKey` 的轮询里给，不进 WS；拒绝 / 超时 / 切回后同令牌 30 s 冷却（429）；心跳超时 → remoteLost（设备照常），宽限期内同 leaseId 的心跳
或受控命令恢复；宽限期满 → 回本地 + `onRemoteLost`：`keep` 只提示；`stopHeat` 中止该租约启动的程序、停 `startedBy` 是它的通道；`estop` → **全部停止（含搅拌）**（没有急停）。
宽限期内同令牌 + 同客户端名再申请 → 旧租约作废。本地随时可切回（`/control/take-back`）。计时由一个 1 s 线程推进，不依赖控制线程。
迁移记 `control.*` 事件、推 WS `control`；快照 `mode` 由 `ApiService`（`ModeHooks`）给。

---

## 6. 契约里需要额外说明的实现

- **revs**：`settings` = 设置 revision；`programs / library / runs / network / usb` 在对应命令或程序结束时 +1；`leak / calib / pid` 按快照内容签名变化 +1（泄漏每个测量点也 +1）；
  `files / usb` 每 5 s 列一次目录比较；`users / device` 恒 0。
- **progress**：`steps / plan` 来自下发时冻结的步骤（运行中）或最近一次 PUT 的草稿（`draft.editedNotSent`）；计划时长用 `step_duration`，计划温度 / 压力按 heat / press / vent 拐点；
  `stepFraction` 按计划起止估；`waitedSec` 只在排队时给 0（引擎不报等待时长）；自整定没有回读周期数：`cycle / cycles` = 0，进度按 30 min 估。
- **报警扩展**：`tier` = 控制核心的 uiLevel；`quantity` 按条件键（`op:/opv:/oph:/pd:` 压力、`ot:/ht:` 温度）；`value / setpoint / limit / range*` 取当前快照；`did / next` 按键类型的固定中文句子。
- **/runs**：中枢线程每帧看 `program_runs[]`，同时开始（≤10 s）的几路合成一次；结束时取吸气量 / 温度算 avg / RSD / 温差；超压 / 超温（按结束原因）填 `abort`；步骤用时来自 `on_step`。
  泄漏测试的运行直接读 `leak/*-summary.json`。重启前没结束的运行开机时标为「服务重启 · 中断」。
- **/trend**：内存环存最近 24 h（每周期一点，约 7 MB）；更早的读每日遥测 CSV 的 `RVn_PV(℃) / 压力(psi) / 吸气量(mmol)` 列。
- **设置**：点路径键 ↔ `SettingKey` 的表在 `api_routes_local.cpp`；`ui.*`、`storage.retentionDays` 进了 store 的 `AppSettings`（settings.json 新键 `UiPressureUnit` 等，类型不对按默认，不让整个文件读不了）；
  数据保留每 6 h 清一次过期的 `telemetry-* / events-*` 每日 CSV。
- **语言**：线上短码（zh / en / ja / ko / de / fr / es / ru），存储里 zh 存 `zh-CN`；读出时 `zh-CN → zh`、`en-US → en`。
- **程序库**：`modified`（本地时间 `yyyy-MM-ddTHH:mm:ss`）/ `by`（本机「触屏」，远程 = 令牌名）一律服务端填；store 的「已有同名模板」→ `NAME_CONFLICT`，「内置模板不能改 / 删」→ `READ_ONLY`；
  程序上限 50 个（`LIBRARY_FULL`）；`usedBy` 按各路 PUT 时给的 `sourceId` 算；每次改动记 `library.*` 事件。
- **store 修补**：`ProgramLibrary::upsert_builtin(kind, body, &status)`（只给服务用：不存在 → 新建；内容去掉 modified / by 后不同 → 覆盖、revision + 1；同 id 的用户条目不覆盖），
  `ctl::builtin_templates::seed` 改用它，`SeedResult` 加 `updated`。原有用例不变全绿。

---

## 7. 和 C# 不一样的地方

1. **没有急停**：`/estop` = 全部停止（含搅拌），202，不锁存、不收回控制权，`ESTOP_ACTIVE` 不再产生；预运行检查去掉「急停」一项；文案里「请按「急停」」改为「请到设备现场确认」。
2. **触屏也走 HTTP 管线**：本机命令同样审计（标题「触屏命令」）、推 opEvent、可带幂等键；C# 的触屏命令不经接口。
3. **手动开关阀经整帧入口**：先按当前阀态拼整帧再 `write_valves`（0.5 s 死区）；不安全的组合（气源 + 排空、两路气源）直接拒绝 `CHANNEL_STATE_INVALID`（C# 手动阀不查 ValveFrame.IsSafe）。
4. **改 PID 不立即写仪表**：存档 + 记来历，如果正是仪表当前生效档就把「生效档」标为未知，下一次写 SP 时按档重写（控制层没有单独写 PID 的入口）；`live` 字段照旧。
5. **UI_THREAD_TIMEOUT / OUTCOME_UNKNOWN** 改指控制线程（5 s 未开始 / 未执行完），正常运行不会出现。
6. **错误体字段顺序**：GET 的错误由 api 层自己写成 C# 的 `{ok, error{code, message, detail:null}}`；网关（401 / 403 IP / 429）仍是 net 模块的 `{error, ok}`（JSON 键序不同，内容相同）。
7. `/settings` 的 `notes.unused` 去掉了 `heatTimeoutMin / pressDeviationPsi`（新后端已实现这两项警告）。
8. `/alarms` 优先读控制线程上的实时报警表（C# 读界面列表），控制线程 2 s 不回才用快照副本。
9. `POST /control/request` 从本机发 → 400（触屏本来就有控制权）。
10. `GET /channels/{id}/program`、`PATCH /settings` 在 C# 设计稿里有、实现里没有，按契约当新增做。

---

## 8. 测试

| 组 | 用例 | 内容 |
|---|---|---|
| `cmd_arbiter` | 2 | 判定表 48 组逐行、文案、空持有者 |
| `cmd_lease` | 11 | 申请 / 允许 / 校验 / 释放、autoGrant、拒绝 + 超时 + 冷却、心跳丢失 / 恢复 / 期满、校验算心跳、grace 0、切回、外部收回、快照 / 历史 50 条 / 凭据、宽限期内重新申请、清洗 5 组 + 截断 + BMP 外、并发过期与申请 50 轮 |
| `cmd_util` | 1 | 随机 id / GUID 格式 |
| `cmd_guards` | 9 | MODE_LOCAL / MODE_REMOTE / NOT_CONTROL_HOLDER / NOT_EXPOSED；严格守卫只对远程；程序占用 + 停加热中止程序；全部停止 / 停用结果；预运行检查与开始运行；复制配置严格跳过；会话归属 / 标记；PATCH revision / 越界、档位、语言、全部确认；手动阀（远程直开、本机要先开手动阀控、不安全组合） |
| `api_server` | 9 | 鉴权 / 角色 / estop 位 / 本机专用 404 / 方法不对 404；租约全流程（428、pending、WS control 带 pending、允许、requestKey、无头 / 他人 / 正确、MODE_REMOTE、心跳、心跳丢失回本地、CONTROL_LEASE_EXPIRED、释放、拒绝冷却 429）；幂等重放 / 不同正文 / 跨令牌、400 / 404 / 422 / 428、注释与尾逗号、信封字段顺序；程序 PUT + 下发 + WS telemetry / progress / opEvent + 跑完 + /runs + 备注 + /trend；5100 条事件后 WS gap / resync / epoch_changed / ping / error、/events 分页；文件下载 Range 206 / 路径穿越 404 / 列表 / 遥测流；**兼容性**：/snapshot 顶层顺序、ChannelSnap 26 个字段、SettingsDto、ProgramSchemaDto（步骤 / 字段键）、Device / Comm / Pid / Control / Sessions / Preflight 字段；契约：PATCH 设置（点路径、冲突、未知键 detail.key、越界 detail.key）、语言、档位、到位范围 / 防超调进快照、手动阀控、全部确认、程序库全流程（428 / revision / 409 currentRevision / 删 / 内置 READ_ONLY / NAME_CONFLICT）、导入导出没插 U 盘、PDF 422、没有 nmcli、users / storage / samples 404 / calib continue / 通讯测试；会话归属与 /estop 兼容 |

集成测试的后台：模拟硬件 + 真控制线程（周期 200 ms）+ 真 `net::Server`（127.0.0.1:0 + 临时 Unix socket），租约心跳 2 s、宽限 2 s。

---

## 9. 冒烟运行（`reactord --sim --config cfg --data data --socket run/hmi.sock --seconds 20`，api.json 开 TCP 127.0.0.1:18080、3 枚令牌）

- `curl /health` → `{"ok":true,"apiVersion":"1.0","simulated":true,…}`；TCP `/snapshot` 顶层以 `type, seq, ts` 开头、以 `autotune, system, revs` 结尾。
- TCP `PATCH /settings` → 404（本机专用）；TCP 远程 `heat/start` 无租约 → 409 `MODE_LOCAL`（中文原文）。
- `curl --unix-socket run/hmi.sock`：`enable` 200、`heat/start` 202 `{tempC:60}`；PC 申请（202 pending + requestKey）→ 触屏 `grant` 200 →`/control` = remote / 冒烟PC；
  `/pid`、`/device/comm`（4 个模块在线）、`/runs`、`/usb`、`/network`（无 nmcli 也回 200）、`/storage`；`PATCH /settings {ui.theme}` → revision 1、changed `["ui.theme"]`；`stop-all` 200。
- 事件文件：`op.*`（触屏命令 / 远程命令）、`control.requested / granted / lost`、`system.stopAll`、`接口已启动 / 已停止`、`程序退出` 都按序落盘；20 s 到点正常退出（停搅拌 / At=4 / 阀门全关）。

---

## 10. 开放问题

1. **部署**：`--hmi-uid` 要填 reactor-hmi 的运行用户（决定记录）；`/run/reactord` 目录由 systemd `RuntimeDirectory=reactord` 建。`/system/restart` 依赖 `Restart=always`。
2. **store 的「同名模板」只按消息文字映射成 `NAME_CONFLICT`**：建议 store 直接返回码（改动很小）。程序（非模板）重名 store 不检查，契约说程序也可能 `NAME_CONFLICT`，现在不会出现。
3. **契约里没法精确给的数**：progress `waitedSec`、自整定 `cycle / cycles / leftSec`（仪表不回读）、`/calib` 的 `stepMinutes`、`vessel*Psi`（标定只公开了快照 points）、`/files` 的 `desc`（只对 `-1` 文件给「表头变了」）、
   `/device/comm` 的 `offlineSince / failStreak`（控制核心的 CommStats 没公开）。都给了 null / 0，需要的话 ctl 加 getter。
4. **导出的 `channels` 过滤没做**（导出全部列）；PDF 不支持（422）。导出在 net 工作线程里做，大文件会占一个线程直到写完。
5. **`/device/comm/{module}/reconnect`**：总线层没有「重开串口」的入口，现在只核对配置并记事件；需要 hw 提供 `Bus::reopen()`。
6. **程序引擎的 `on_step / on_finished` 只有一个回调位**：被 ApiService 占用了，别的模块要用需改成订阅列表。
7. **改 PID 的「立即写仪表」**（§7-4）：如需和 C# 一样立刻生效，ctl 的 TempService 要加 `write_pid(id, band, pid, cb)`。
8. 数据保留只清每日 CSV，不清 `events/*.jsonl`、会话、泄漏结果；清理前不提示。
9. GCC 9.4 / aarch64 未实测；`run_process` 在子进程里只用 dup2 / close / execve（父进程先解析 PATH、拼好环境变量）。
