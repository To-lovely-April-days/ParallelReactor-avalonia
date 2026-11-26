# 模块报告 · ctl 控制核心（第一阶段）

2026-11-26 · 命名空间 `reactor::ctl` · 目录 `src/ctl/`、`tests/ctl/`，另改了 `apps/reactord/main.cpp`
对应设计文档 §2–§7、决定记录（死区、CommStats 口径、全部停止之后、升温超时 / 压力偏离），附录 §3、§4、§6、§8。

`make -C tools/build -j8 test`：全量 **463 个用例，0 失败**（其中 `run_tests ctl_` 25 个，连跑 20 次稳定）；编译无警告。
ctl 源文件、tests/ctl、reactord 另用 `-Wall -Wextra -Wpedantic -Wshadow`（不带 `-Wno-unused-parameter`）单独查过，无警告（macOS clang）。
`make -C tools/build reactord` 后 `reactord --sim --seconds 10` 跑过：10 s 内 8 帧快照、遥测 CSV 每周期一行、事件 CSV + JSONL 正常、退出时下发停止帧。
GCC 9.4 没有在本机编过（本机无 GCC）；只用 C++17 + POSIX（`sendto` / `sockaddr_un`），`std::atomic_load/atomic_store(shared_ptr*)` 在 C++17 合法。

---

## 1. 架构

### 1.1 线程与所有权

| 线程 | 谁起 | 做什么 | 能碰什么 |
|---|---|---|---|
| 控制线程（1） | `ControlCore::start()`（`EventLoop::start_thread`） | 定时器（控制周期 1.2 s、设置防抖检查 200 ms、阀门保持心跳 1 s、死区 / 重试的单次定时器）、任务队列、总线回包处理 | **独占** `ControlCore` 全部可变状态：8 个 `Channel`、温控 / 压力 / 阀门 / 搅拌服务的状态、报警表、设置副本 `SettingsState`、气路租约 `core::GasLease`、超温 / 超压守卫 |
| 总线 I/O 线程（4） | `hw::make_hardware` | 串行收发 Modbus；回调里只做 `loop.post(...)` 把结果送回控制线程 | 只碰自己的串口 |
| 任意线程 | — | `request_stop_all`（快速路径）、`post` / `call`、读 `snapshot()` | 停止代号（锁）、总线队列、快照指针（原子） |
| 存储线程 | store 模块内部 | DataLogger / EventStore 写盘 | 文件 |

**所有设备读写都是异步的**：控制线程提交事务（`CoreIo::submit` / `CoreIo::chain`），结果在总线线程上回来后投递进控制线程队列，
在控制线程上改状态。所以每周期的判定用的是「上一轮」的读数，和 C# 的 fire-and-forget 轮询语义一致。

### 1.2 队列和顺序

- **命令队列**：`EventLoop::post(fn)` 任何线程可投递，控制线程 FIFO 执行；`call(fn)` 带 `std::future`。命令层（第三阶段）把「守卫 + 执行」整个放进一个任务里，判定与动作之间不会插进别的命令（设计文档 §4）。
- **多步写序列**（启动加热全链、搅拌启动）：`CoreIo::chain` 每一步做完才把下一步排到总线队尾（同 `Bus::submit_seq`），每步提交前核对**停止代号**。
- **停止代号 `StopEpoch`**（`ctl_io.hpp`）：没有急停锁存之后，用它保证「全部停止」之后落到设备上的一定是停止帧——快速路径在同一把（递归）锁里「代号 +1 → 提交紧急帧 → 提交普通补发帧」；会让设备出力 / 开阀 / 转动的写入在发起时记下代号，每步「核对 + 提交」也在这把锁里。测试 `ctl_stop.stop_all_urgent_then_normal_over_stale_frames` 固定了这个顺序（Srun=0 已写、后面的 At=0 被截断、从未写下去）。

### 1.3 控制周期（`ControlCore::tick`，照 C# `MainViewModel.Tick` 的顺序）

模拟步进 → 温控轮询（上一轮未完跳过）/ 压力轮询（skip-if-busy）/ 搅拌故障轮询（÷3）→ 模块掉线跟踪（开机前 5 周期不判）→
每通道：写 T/P（离线保留旧值并标无效）、吸气量采样、超温判定、超压判定、运行中超温置报警 → 本周期超压关阀合并下发 →
曲线引擎 → 定值逼近 → **升温超时 / 压力偏离（新）** → `ProgramHooks::on_tick` → 门闸统计 / 超时事件 → 遥测行入队 → `sd_notify("WATCHDOG=1")` → 发布快照。

### 1.4 文件

| 文件 | 内容 |
|---|---|
| `ctl_clock.*` | `Clock` / `SystemClock` / `ManualClock`（测试推进） |
| `ctl_event_loop.*` | `EventLoop`：任务队列、周期 / 单次定时器、线程模式与手动模式 |
| `ctl_tick_gate.hpp` | `TickGate`（C# TickGate 逐行语义） |
| `ctl_sd_notify.*` | `sd_notify()`：往 `NOTIFY_SOCKET` 发数据报（支持 `@` 抽象命名空间），不依赖 libsystemd |
| `ctl_io.*` | `StopEpoch`、`CoreIo`（submit / chain，结果投递回控制线程） |
| `ctl_temp_service.*` | 温控服务（AI-8） |
| `ctl_pressure_service.*` | 压力服务（8AI） |
| `ctl_valve_service.*` | 阀门服务（IO16R，唯一写阀入口） |
| `ctl_stir_service.*` | 搅拌服务（DM2C） |
| `ctl_alarm_table.*` | 报警表 + AlarmDto JSON |
| `ctl_channel.hpp` | `Channel`（通道状态）、`HeatWatch` / `PressWatch` |
| `ctl_hooks.hpp` | 第二 / 三阶段扩展接口 `ProgramHooks` / `ActivityHooks` / `ModeHooks`、`AbortReason` |
| `ctl_snapshot.*` | `Snapshot`（不可变）、`to_json`、`SnapshotStore`（原子 shared_ptr + 订阅） |
| `ctl_core.hpp` | `ControlCore` 公开接口 |
| `ctl_core.cpp` | 构造 / 启动 / 退出 / 控制周期 / 全部停止 / 单通道停止 / 阀门 / 搅拌 / 气路租约 / 监视 / 吸气量 |
| `ctl_core_safety.cpp` | 超温、超压、模块掉线、搅拌故障、升温超时、压力偏离 |
| `ctl_core_heat.cpp` | 定值逼近、曲线、启动 / 停止加热 |
| `ctl_core_publish.cpp` | 快照生成、遥测行 |

全部文件名带 `ctl_` 前缀：`libreactor.a` 用 `ar rcs` 打包，同名 `.o`（如 `common/clock.o`）会互相覆盖。

---

## 2. 公开接口（`ctl_core.hpp`）

```cpp
struct CoreOptions { tick_ms=1200; settings_poll_ms=200; threaded=true; watchdog=true; comm_grace_ticks=5;
                     installed{1..8}; pressure_unit=MPa; heat_band_c=2.0; press_deviation_hold_ms=30000; };
struct CoreDeps { hw::Hardware* hw; Clock* clock; store::DataLogger* logger; store::SettingsStore* settings_store; store::SettingsState settings; };

class ControlCore {
  // 生命周期（启动线程）
  void start();  void shutdown(bool stop_outputs = true);
  // 任何线程
  bool post(fn);  future call(fn);  SnapshotStore& snapshots();  shared_ptr<const Snapshot> snapshot();
  void request_stop_all(StopAllOptions{stop_stir, source}, function<void(const StopAllResult&)> done);
  // 手动驱动（测试：调用线程即控制线程）
  void settle(int timeout_ms = 3000);  void tick();
  // —— 以下只在控制线程 ——
  const Channel& channel(id);  Channel& channel_mut(id);  temp()/pressure()/valves()/stir()/settings()/alarms();
  is_over_temp_tripped(id); is_over_pressure_tripped(id); any_over_pressure_tripped(); is_idle_over_temp_alarm(id);
  void update_settings(fn, bool bump_revision = true);         // revision+1、运行参数生效、防抖写盘
  void file_event(level, origin, title, detail, kind);        // 只写事件（显式 kind）
  bool ack_alarm(id);  int subscribe_alarms(fn);  int subscribe_notices(fn(kind,msg));
  // 加热
  void start_fixed_heat(id, optional<double> target, optional<string> started_by);
  void retarget(id, target);  void start_band_heat(id, sp);  void set_sp_mode(id, "fixed"|"curve");
  void set_profile(id, vector<core::ProfileSegment>);  void start_curve(id, started_by);  void stop_curve(id);
  void stop_heat(id, cb);  void start_heat_chain(id, band_temp, initial_sp, cb);  void write_temp_sp(id, sp, band_temp, cb);
  // 通道
  void stop_channel(id, bool disable, function<void(const ChannelStopResult&)> cb);
  // 阀门
  void write_valves(frame, ValveWriteOpts{dead_ms, retries}, cb);  void close_all_valves(cb);
  void set_valve_hold(frame);  void clear_valve_hold();  int64_t sv_open_gen(id);  void set_manual_valve(bool);
  // 搅拌
  void stir_start(rpm, cb);  stir_stop(cb);  stir_set_rpm(rpm, cb);  stir_clear_fault(cb);
  // 吸气量
  bool start_uptake(id, why);  void stop_uptake(id, why);  void reset_uptake(id);  double gas_volume_ml(id);
  // 气路租约
  const core::GasLease& gas_lease();  int64_t begin_gas(owner, task, participants, watch, owner_rv);  bool end_gas(owner, gen);
  void abort_all_gas(core::GasAbortCause);  int add_gas_abort_listener(fn(cause, optional<int> channel));  bool abort_keep_vent();
  // 新三级警告
  void begin_heat_watch(id, target, band);  end_heat_watch(id);  begin_pressure_watch(id, target_psi);  end_pressure_watch(id);
  // 扩展点
  set_program_hooks(ProgramHooks*);  set_activity_hooks(ActivityHooks*);  set_mode_hooks(ModeHooks*);  temp_mut();  pressure_mut();
  store::TelemetryRow build_telemetry_row() const;
};
```

快照：`Snapshot`（对外帧字段 + 附加：全量通道含曲线、设置副本、门闸统计、4 模块 `ModuleCommSnap`、记录器丢行数、报警表副本、各通道生效 PID 档）；
`to_json(Snapshot)` → C# `TelemetrySnapshot`；`channel_to_json(c, with_profile)` → `ChannelSnap`（`/channels/{id}` 用 `with_profile=true`）；
`tick_stats_to_json`、`module_comm_to_json` → `/device/comm`。报警：`alarm_to_json(AlarmItem)` → C# `AlarmDto` + 末尾 `uiLevel`。

---

## 3. 从哪些 C# 文件移植了什么

| C# | C++ | 说明 |
|---|---|---|
| `Service/ControlLoop.cs` RunTick、`Service/TickGate.cs` | `ControlCore::tick`、`TickGate` | 门闸语义逐行；超时事件「控制循环超时」每分钟 ≤1；Tick 之后才发布快照 |
| `ViewModels/MainViewModel.cs` Tick / StartFixedHeat / SetFixedTarget / WriteTempSpSafeAsync / WriteCurveSpAsync / StepApproaches / StopHeatCoreAsync / CloseReactorValveCoreAsync / ReportHeatStartFailed / SyncValvesFromHardware | `ctl_core.cpp`、`ctl_core_heat.cpp` | 逼近事件文案、PV 无效按 25 ℃ 规划、曲线 / 逼近遇报警自动停 |
| `MainViewModel.Logging.cs` CheckOverTemp / SendOverTempStopAsync / CheckOverPressure / FlushOverPressureValves / SendOverPressureValvesAsync / SendOverPressureHeatAsync / TrackModules / TrackStirFault / BuildTelemetryRow / LogValveFrame | `ctl_core_safety.cpp`、`ctl_core_publish.cpp`、`ValveService::on_shown_changed` | 报警键 `ot:N / op:N / opv:N / oph:N / comm:k / stir:fault`；失败报警 1 分钟节流；第 3 次未确认升级；超压解除时未确认执行器的 warn；遥测行留空规则 |
| `MainViewModel.Gas.cs` BeginGas / EndGas / AbortAllGas(cause) / `_abortKeepVent` / ApplyValvesAsync / WriteValvesAsync（重试 2 次、120 ms）/ CloseAllValvesAsync（4 次、200 ms、超压中止保持排空）/ HoldAsync 心跳 | `begin_gas / end_gas / abort_all_gas`、`ValveService` | 中止由监听者回调收尾（替代 CancellationToken） |
| `MainViewModel.Uptake.cs` | `start_uptake / stop_uptake / reset_uptake / update_uptake / gas_volume_ml` | 文案逐字；SV 打开计数在提交总线前加一（失败也加） |
| `MainViewModel.Estop.cs` 快速路径（只借用顺序） | `request_stop_all` | 紧急一遍 + 普通一遍、结果以普通那一遍为准；**无锁存、无复位、无 Locked 位** |
| `Commands/OperatorCommands.System.cs` TryStopAll / StopAllTailAsync | `stop_all_cleanup / stop_all_maybe_finish` | 中止气路 / 程序、清搅拌引用、停曲线 / 逼近、非停用 → 已结束、清 startedBy；失败报警文案照抄 |
| `Commands/OperatorCommands.Channels.cs` TryStopChannel / StopChannelTailAsync、TryStartProfile 的硬件部分、TryStartHeat 的状态迁移 | `stop_channel`、`start_curve`、`start_fixed_heat(started_by)` | 命令层的守卫 / 错误码不在这里（第三阶段） |
| `Service/TempService.cs` | `TempService` | 轮询（一轮 5 笔算 1 次）、Ctn 自愈、开机 8 路 At=4、无存档读 PID 种子、全链启动、按目标温度套档（`_lastBand` + PidGen）、超温切断 At=4 |
| `Service/PressureService.cs` | `PressureService` | skip-if-busy、整帧替换、满量程运行时可改 |
| `Service/ValveService.cs` | `ValveService` | SV 打开计数、只关不开的 CloseRange、逐笔 CommStats |
| `Service/StirController.cs` + MainViewModel 搅拌部分 + `Models/DeviceActivity.cs` StirFaultWatch | `StirService` + `on_stir_alarm_code` | 速度模式、传动比、5 s 通讯错误节流、报警码中文、故障跳变报警 |
| `ViewModels/AlarmViewModel.cs` | `AlarmTable` | Log / Raise / Clear / HasActive / Ack；id 为本次运行递增串 |
| `Api/Hmi/SnapshotBuilder.cs` + `Contracts/.../TelemetrySnapshot.cs` | `publish_snapshot`、`ctl_snapshot.cpp` | 字段名 / 顺序 / null 规则逐个对照 |

---

## 4. 和 C# 不一样的地方（及原因）

1. **没有急停**（设计文档 §6）：无锁存文件、无复位、无服务层 Locked 位；快照 `estop` 恒为 `{active:false, heat/stir/valves:"pending", since:null, latched:false, restoredOnBoot:false}`；`stir.locked` 恒 false；遥测「急停」列恒 0。原来靠 Locked 位拦「启动途中按停止」，改成**停止代号**（§1.2）。
2. **文案里的「急停」**改成「点主页的「全部停止」」（如「超温切断多次未确认 · 请点主页的「全部停止」」「…请点主页的「全部停止」或到现场断电」）；单路停止加热失败的提示同理。
3. **全部停止走紧急队列**（C# 的 TryStopAll 走普通优先级；设计文档 §4 要求用急停的快速路径）。紧急那一遍的结果只记事件（「全部停止 · 紧急帧回应」），对外结果以普通补发那一遍为准；未配置的模块按成功（同 C# `Task.FromResult(true)`）。全部停止一律 12 路全关（含排空），不受「超压保持排空」影响；监听者没自己释放的气路租约强制释放。新增事件「全部停止」（kind `system.stopAll`，含来源、停止的通道、中止的序列 / 程序）。
4. **Toast** 没有落盘的等价物：改成 `subscribe_notices(kind, msg)` 回调（WS / 触屏自己决定怎么显示），文案照抄 C#。
5. **报警表**加了界面三级 `ui_level`（§5）和条件键 / kind 字段；表长上限 5000（C# 无上限，72 h 运行防内存增长），超出时丢最旧的非活动条目。`AlarmDto` 末尾多一个 `uiLevel`（PC 端 System.Text.Json 忽略未知字段）。
6. **事件 kind 一律显式给出**（不靠 `event_kinds::derive`），新加的 kind：`system.stopAll`、`system.simRefused`、`heat.preheatDone / exoPause / overLimit`、`uptake.started / stopped / rejected`、`alarm.heatTimeout / heatTimeoutCleared / pressureDeviation / pressureDeviationCleared / stirStopFailed`。
7. **阀门写入统一走整帧**：手动开关单个阀也应经 `write_valves`（0x0F 整帧，带死区判定），不再用 0x05 单写（决定记录「控制层唯一的写阀入口」）。只关不开的联锁 / 收尾仍是 C# 的 `CloseRange` / 单路关 SV（不按界面阀态重写整帧）。死区默认 500 ms（吹扫），程序步骤传 1000 ms；手动阀控 C# 没有死区，这里也按 500 ms。
8. **单路停止关 SV** 不再另记「SV{n} 关」事件：阀态变化统一由「阀门整帧下发」事件（`valve.frame`）记录。
9. **单路停止不等气路序列收尾**（C# 最多等 3 s）：控制线程不能阻塞，改成「标失效 + 同步通知监听者」，第二阶段的序列应在回调里同步收尾。
10. **超温切断的「整定被打断」**：C# 在写之前标记、失败撤销；这里写成功后再经 `TempService::on_autotune_aborted` 通知（结果相同，少一次撤销）。
11. **曲线 SP 写入加了在途标记**（C# 没有，慢总线上可能堆积重复写）；逼近同样（C# 有 WriteInFlight）。
12. **时刻**：守卫、逼近、曲线、吸气量一律用单调毫秒（C# 部分用 UtcNow）。
13. **满量程**：开机按 `hardware.json` 的 `press.fullScalePsi` 回填设置的 `PressFullScaleMPa`（同 C# 用硬件选项回填），之后 `update_settings` 改 MPa 即时生效。
14. **退出**：`shutdown(true)` 照 C# 退出程序：中止程序 / 气路，停搅拌、8 路 At=4、阀门全关并等写完（≤3 s），设置落盘。reactord 收到 SIGTERM 也这样做（C# 的 ProcessExit 只停记录器；但 C# 开机本来就会 8 路 At=4，所以重启后加热都是停的，结果一致）。

---

## 5. 报警级别映射（界面三级）

| `ui_level` | 含义 | 判定 |
|---|---|---|
| 1 | 安全联锁（一级，全屏） | 条件键 `ot:N`（超温切断，含「仍超温 · 重新置报警」这种 warn）、`op:N`（超压联锁）、`opv:N`（超压关阀未确认）、`oph:N`（超压停热未确认） |
| 2 | 错误（二级，弹窗） | 其余 `err`：`comm:k` 模块离线、`stir:fault` 搅拌故障、启动加热失败、停止加热未确认、阀门写入失败、模拟模式提示… |
| 3 | 警告（三级，铃铛气泡） | 其余 `warn`：`ht:N` 升温超时、`pd:N` 压力偏离、放热超出控温能力、吸气量未计量、超压已解除但联锁动作未确认… |
| 0 | 普通事件 | `info`（状态直接 done，不算报警） |

新警告规则（设计文档 §6）：
- **升温超时** `ht:N`：`start_fixed_heat` 自动打开监视（到位范围 ±2 ℃ = 程序 heat 步骤默认 band，`CoreOptions::heat_band_c`）；超过 `HeatTimeout` 分钟仍未进入范围 → warn 一次；进入范围 / 停加热 / 改目标（重新计时）时解除。温控离线不判。曲线升温不监视（目标一直在变）；程序的 heat 步骤由第二阶段调 `begin_heat_watch`。
- **压力偏离** `pd:N`：第二阶段在充压完成、稳压打开时调 `begin_pressure_watch(ch, 目标 psi)`、稳压结束时 `end_pressure_watch`；|P − 目标| > `PressDeviation` 持续 30 s → warn，回到范围内自动解除。压力离线不判。

---

## 6. 测试（`tests/ctl/`，组名都以 `ctl_` 开头，25 个用例）

| 文件 | 组 | 覆盖 |
|---|---|---|
| `ctl_test_util.hpp` | — | `Rig`（模拟硬件 + ManualClock + 不起线程的核心，`advance/ticks` 按定时器到期顺序推进并等总线空闲）、`GateMaster`（可关闸、记录是否走紧急队列）、`FilterMaster`（按请求回失败 / 吞掉）、`quiet_pressure_hw`（不抖动的压力主站） |
| `test_ctl_loop.cpp` | `ctl_loop`、`ctl_alarms` | 定时器到期顺序与不补发、跨线程 post FIFO、任务异常不终止循环、TickGate（C# 语义）、停止代号、报警表 raise / clear / ack / 三级 / AlarmDto 字段 |
| `test_ctl_safety.cpp` | `ctl_safety` | 超温触发 → 回读确认 → 解除（通道保持报警）；写被吞掉 → 每 5 s 重发 + 第 3 次升级；写失败 → 「超温切断未确认」；确认后被重开 → 复切；超压：气源一帧 0x0F(8..10) + SV3 单关、不碰排空和 SV5、中止气路（cause=OverPressure）、序列收尾只写 0–10、停加热、解除；阀门总线断线 → 关阀未确认 / 重发 / 升级 / 恢复后确认解除；模块掉线：开机 5 周期宽限、「开机后未连通」、恢复、在线后再断、离线读数为 null；升温超时只警告一次、停加热解除；压力偏离 30 s 后警告、回到范围自动解除 |
| `test_ctl_stop.cpp` | `ctl_stop` | **全部停止顺序**：阀门总线 [卡住的一笔] → 紧急全关 → 旧开阀帧 → 普通补发全关（最后是全关）；温控总线启动全链被截断（At=0 从未写）、8 路 At=4 紧急 8 笔 + 普通 8 笔；收尾状态；从另一个线程调用；带停搅拌（0x6002=0x40 两笔）；断线时结果未确认 + 报警；单路停止 / 停用（At=4、关 SV、气源不动） |
| `test_ctl_heat.cpp` | `ctl_heat`、`ctl_valves`、`ctl_uptake` | 定值加热全链帧序（Srun=0、At=0、P/I/D、SP=目标−5）、30 s 强制重写不重写 PID、改到高温档先写高温档 PID；曲线 SP 单调推进、5 min ≈ 半程、curve 通道 profile 进帧、报警自动停曲线；死区（排空 → 气源先全关、1000 ms 后写目标）、不安全帧 / 越权 SV 拒绝、保持心跳每 1 s；吸气量：关阀压降累计（误差 < 0.01 mmol）、开 SV 补气不计入（refills=1）、再降继续累计、气相体积不合理不开始 |
| `test_ctl_snapshot.cpp` | `ctl_snapshot` | **快照 JSON 与 C# DTO 字段名 / 顺序逐个比对**（TelemetrySnapshot、Mode、Estop、Comm、Channel、Approach、Profile、Segment、Stir、Gas、Inlet、Program、Programs、Leak、Session、TickStats、ModuleComm），值的类型（整数值的 double 写成整数、null 规则、ts 以 Z 结尾）；每周期发布 + 订阅；遥测行 173 列 = 表头列数、状态 / 逼近阶段写 C# 枚举名 |

C# 里没有这一层的单测（控制循环在 VM 里），所以这些都是新写的场景测试；core 的守卫本身已由 core 模块翻译过 C# 用例。

---

## 7. 第二 / 第三阶段要用的扩展点（照这里接，不要改核心内部）

**线程规则**：下面所有调用都在控制线程上。接口线程、WS 线程只能 `post` / `call` 投递，或读 `snapshot()`。

### 7.1 第二阶段：程序引擎（每通道驱动 `core::program::StepRunner`）
1. 实现 `ProgramHooks`，`core.set_program_hooks(&engine)`：`is_owned / abort_all / abort_channel / clear_stir_share / channel_program / legacy_program / programs / telemetry_step / on_tick`。核心在全部停止（`AbortReason::StopAll`）、单路停止（`ChannelStop`，返回 true 时核心**不再**下发 At=4）、退出（`Shutdown`）时调用；快照和遥测行从这里取程序字段。
2. 步骤的节拍用 `core.loop().after(ms, fn)` / `every`（0.5 / 1 / 2 s），不要自己开线程。
3. StepRunner 的动作怎么落地：
   - `Valves`（AllClosed / GasToVessel / VentVessel）→ 用 `core::valve_frame` 拼整帧 → `core.write_valves(frame, {dead_ms = 1000}, cb)`；保持阶段 `core.set_valve_hold(frame)`，结束 `clear_valve_hold()`；收尾 `core.close_all_valves(cb)`（超压中止期间自动保持排空）。
   - `RequestGas` → `core::GasQueue`（引擎自己持有，单调时钟 = `core.clock().mono_ms()`）排到队头后 `core.begin_gas(GasOwner::Program, task, {rv}, watch, rv)`；`ReleaseGas` → 全关后 `core.end_gas(GasOwner::Program, gen)`。
   - `core.add_gas_abort_listener(fn(cause, channel))`：`cause == OverPressure` → 持有气路的程序按「气路被超压联锁中止」失败收尾（`StepRunner::abort()`）；`channel` 有值 = 该通道被停止 / 停用。**回调里同步收尾并 `end_gas`**（核心不等）。
   - 加热：定值 `core.start_fixed_heat(id, temp)`（自动带防超调逼近与升温超时监视；想改 band 再调 `begin_heat_watch(id, temp, band)`）；曲线自己持有一份 `core::TempProfile`，用 `core.start_heat_chain(id, band_temp, start_pv, cb)` + 每周期 `core.write_temp_sp(id, sp, band_temp, cb)`；停 `core.stop_heat(id)`。
   - 充压完成：`core.start_uptake(id, "升压完成")`、稳压打开时 `core.begin_pressure_watch(id, target_psi)`；end 步 / 收尾 `stop_uptake`、`end_pressure_watch`；新程序 `reset_uptake`。
   - `fresh_pressure`：`core.pressure_mut().read_now(cb)`（不跳过的即时读）。
   - 搅拌：`core.stir_start / stir_set_rpm / stir_stop`，引用计数用 `core::StirShare`（引擎持有，`clear_stir_share` 时清空）。
   - 通道状态：`core.channel_mut(id).set_state(...)`、`started_by`。
4. 稳压回路（决定记录：每通道一个 2 s 状态机）放在第二阶段，用上面的阀门 / 租约 / 即时读接口。

### 7.2 第二阶段：泄漏测试 / 容积标定 / 自整定
- 泄漏测试、容积标定：`begin_gas(GasOwner::Leak / Calib, ...)` + `write_valves` + `set_valve_hold` + `read_now`；进行状态实现 `ActivityHooks::leak_test()` / `calib()`（形状见 `ctl_hooks.hpp` 默认值 = C# LeakSnap / CalibSnap），`core.set_activity_hooks(...)`。结果写设置用 `core.update_settings(fn)`（`last_leak_test_utc_ms`、`effective_volume[]`…）。
- 自整定：`core.temp_mut().read_setpoint(id, cb)`（SP ≥ 35 ℃ 预检）；Srun=0 + At=1 用 `core.start_heat_chain` 同款的 `CoreIo::chain`（需要时在 TempService 上加一个 `start_autotune`，带停止代号）；`temp_mut().on_autotune_finished = fn(id)`（轮询看到 At 从 1 变成别的）→ 回读 PID、`update_settings` 存档 + `store::tune_complete`、`temp_mut().bump_pid_gen(id)`；`on_autotune_aborted = fn(id)`（超温 / 超压 / 全部停止打断）→ 整定簿标记不回读。手改 PID 后调 `bump_pid_gen(id)` + `forget_live_band(id)`，下一次写 SP 时按档重写。

### 7.3 第三阶段：命令层 / 控制租约 / 接口
- 每个写命令打包成一个任务：`core.call([&]{ 守卫（读 core 状态 + core::heat_guard_check / gas_guard_check / program_run_guard）→ 执行（调上面的接口）→ 结果 })`。需要等硬件结果的（停加热、关阀、全部停止）用回调把结果送回 promise（`HW_UNCONFIRMED` 的 2 s 尾部在命令层计时）。
- `POST /stop-all`、`POST /estop`（兼容：= 全部停止含搅拌）→ `core.request_stop_all({stop_stir, 来源}, done)`，**可以直接在接口线程调**；`done` 在控制线程上给出 `StopAllResult`（映射 C# StopAllTailAsync 的结果与文案）。
- 单路 stop / disable → `core.stop_channel(id, disable, cb)`（`ChannelStopResult` 里有 heat / valve 错误文案、被中止的气路序列名）。
- `ModeHooks::mode()` 给快照的 `mode`；`onRemoteLost=stopHeat` 按 `channel(id).started_by` 筛通道。
- 读端点：`snapshot()`（首帧之前为空 → 503 STARTING）；`/snapshot` = `to_json(*s)`；`/channels/{id}` = `channel_to_json(s->channels[i], true)`；`/alarms?state=` = `s->alarms` 过滤后 `alarm_to_json`；`/device/comm` = `tick_stats_to_json(s->tick)` + `module_comm_to_json(s->temp_comm…)`；`/settings`、`/pid` 从 `s->settings`、`s->live_band`、`s->channels[i].autotuning` 组。
- WS：`core.snapshots().subscribe(fn)`（控制线程上回调，只能非阻塞入队）推 `telemetry`；`core.subscribe_alarms(fn)` 推 `alarm`；`core.subscribe_notices(fn)` 可推触屏提示；事件仍走 store `EventStore::subscribe`。
- 新端点 `POST /alarms/ack-all` → `core.alarms().ack_all()`；`PATCH /settings` → `core.update_settings(fn, false)`（fn 里调 `store::patch_settings`，它自己 +1 revision，见 §8 问题 3）；改到写设备的项（搅拌电流 / 细分 / 满量程 / 传动比）：传动比、加减速、满量程由 `update_settings` 自动生效，电流 / 细分 / 待机需要第三阶段另下发（本阶段没做）。

---

## 8. 开放问题 / 需要别的模块确认

1. **手动阀控的死区时长**：C# 手动开关单个阀没有死区；按决定记录统一走整帧入口后，这里按吹扫的 0.5 s。如要不同请定。
2. **单路停止等不等气路序列收尾**：C# 最多等 3 s 再停加热 / 关 SV；这里不阻塞控制线程，要求第二阶段在中止回调里同步收尾。若第二阶段做不到同步，需要在 `stop_channel` 里加「等 lease 释放」的定时器版本。
3. **设置 revision 由谁加**：`ControlCore::update_settings(fn)` 默认 +1；`store::patch_settings` / `set_band_bounds` 成功时自己已 +1，所以在 fn 里调它们时传 `update_settings(fn, /*bump_revision=*/false)`，避免一次修改 +2。
4. **升温超时的到位范围**：用 ±2 ℃（程序 heat 步骤默认 band）。设置里没有这一项，如需可加设置。曲线升温不判升温超时。
5. **压力偏离的设定值**：由第二阶段在稳压打开时给出目标（press 步骤的 pres）；手动充压没有「稳压」概念，不判。
6. **reactord 收到 SIGTERM 时停不停输出**：现在停（同 C# 退出程序）。如果希望 systemd 重启 / 升级时保持加热，需要改成 `shutdown(false)`，但开机流程（C# InitAsync）本来就会 8 路 At=4，保持也保持不住——要改得连开机流程一起改。
7. **全部停止期间的手动阀写**：快速路径之后、控制线程收尾之前若又有新的开阀命令，它记下的是新代号，会被正常执行（这是「不锁存」的本意）。若希望全部停止后短时间内拒绝开阀，需在命令层加守卫。
8. **hw**：`MockModbusMaster` 的压力是随机游走（每步 ±30 原码，不回归 8000），长时间模拟运行压力会漂到量程两端；测试里用了不抖动的压力主站绕开。不影响真机。没有发现 core / hw / store 的功能性 bug。
9. **GCC 9.4 / arm64**：未实测，请在 arm64 容器里跑一次 `make test`。
