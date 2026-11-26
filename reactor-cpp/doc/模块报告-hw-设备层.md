# 模块报告 · hw 设备层（`src/hw/`，命名空间 `reactor::hw`）

2026-11-26 · 对应设计文档 §4（总线线程 + 紧急队列）、§6（无急停）、§7（hardware.json + 波特率），附录 §2。

## 1. 文件

| 文件 | 内容 | C# 出处 |
|---|---|---|
| `modbus_rtu.{hpp,cpp}` | CRC、8 个功能码组帧、期望回包长度、读响应解析、位解码、异常码文案（原有接口保留，rtu-probe 不受影响） | `Hardware/ModbusCrc.cs`、`ModbusRtuMaster.cs` 组帧部分 |
| `serial_port.{hpp,cpp}` | termios 串口（原样未改） | — |
| `modbus_master.{hpp,cpp}` | `ModbusRequest` / `ModbusResult`、`ModbusMaster` 接口（同步）、`req_*` 构造、`validate_request`、`build_frame`、`describe_request`、`execute_seq`、五成双舍入工具、`OfflineModbusMaster` | `IModbusMaster.cs`、`OfflineModbusMaster.cs` |
| `rtu_master.{hpp,cpp}` | `RtuModbusMaster`：串口上的 RTU 事务 | `ModbusRtuMaster.cs` TxnCore |
| `comm_stats.hpp` | `CommStats`：polls / fails / last_ok_ms / 连续失败 / online | `Service/CommStats.cs` + 各 Service 的 `_fails` |
| `bus.{hpp,cpp}` | `Bus`（每总线一个 I/O 线程、紧急 + 普通两条队列）、`BusMaster`（同步视图） | `PriorityGate.cs` + `ModbusRtuMaster.Txn` + `IUrgentModbusMaster.Urgent` |
| `mock_master.{hpp,cpp}` | `MockModbusMaster`：内存从站 + `simulate_step()` + 测试后门 / 故障注入 | `MockModbusMaster.cs` |
| `tracing_master.{hpp,cpp}` | `TracingModbusMaster`：逐笔事务追加写日志，紧急带【急】 | `TracingModbusMaster.cs` |
| `temp_controller.{hpp,cpp}` | AI-8 驱动 | `TempController.cs` + `TempService.cs` 寄存器部分 |
| `analog_module.{hpp,cpp}` | 8AI 驱动 | `AnalogModule.cs` |
| `io_module.{hpp,cpp}` | IO16R 驱动（含阀门映射和通电极性） | `IoModule.cs` + `ValveService.cs` 寄存器部分 |
| `stepper_driver.{hpp,cpp}` | DM2C 驱动 + `stir_gear` 传动换算 | `StepperDriver.cs` + `Models/StirGear.cs` |
| `hardware_config.{hpp,cpp}` | `hardware.json` 结构、读 / 写 / 校验 | `Service/HardwareOptions.cs` + `run.sh` |
| `hardware.{hpp,cpp}` | `make_hardware()` 组装 4 总线 + 4 驱动 + 模拟；`urgent_stop_all()` 全部停止快速路径 | `HardwareOptions.CreateXxxBus` / `SimBus` |

测试：`tests/hw/test_modbus.cpp`、`test_bus.cpp`、`test_drivers.cpp`、`test_hw_config.cpp`、`test_rtu_pty.cpp`。

## 2. 公共 API 要点

**事务模型**：一笔事务 = 一个 `ModbusRequest`（用 `req_read_holding(slave, addr, n)` / `req_write_register(...)` / `req_write_coils(...)` 等构造），结果是 `ModbusResult{ok, exception, error(中文), regs, bits, elapsed_ms}`。**不抛异常**。

```cpp
class ModbusMaster { virtual ModbusResult execute(const ModbusRequest&) = 0; /* + read_holding/write_register… 便捷封装 */ };
RtuModbusMaster(SerialConfig, int timeout_ms = 300);
OfflineModbusMaster(std::string what);              // 错误 = what + "未配置串口"
MockModbusMaster(MockModbusMaster::Options{temp_slaves, temp_loops, analog_slave, seed});
TracingModbusMaster(std::shared_ptr<ModbusMaster> inner, std::string path);

class Bus {
  Bus(std::string name, std::shared_ptr<ModbusMaster>);         // 构造即起线程
  bool submit(ModbusRequest, Priority, Callback);               // 回调在总线线程
  std::future<ModbusResult> submit_future(ModbusRequest, Priority = Normal);
  bool submit_seq(std::vector<ModbusRequest>, Priority, bool stop_on_fail, SeqCallback);
  void stop(bool drain = false);
  const CommStats& stats() const; size_t queued(Priority) const; bool idle() const; bool wait_idle(int ms) const;
};
BusMaster(Bus&, Priority = Normal);                             // 同步视图 = submit_future().get()
```

**驱动**（`TempController` / `AnalogModule` / `IoModule` / `StepperDriver`）：对象只存站号等不可变参数。每个操作三件套，换算只写一处：
- `req_xxx()` → 请求（控制线程异步用：交给 `Bus::submit` / `submit_seq`）；
- `decode_xxx(const ModbusResult&)` → `std::optional<值>`（回调里解码）；
- `xxx(ModbusMaster&)` → 同步便捷（工具、测试、慢流程；经 `BusMaster` 时阻塞）。

主要入口：
- AI-8：`poll_requests()` + `decode_poll()` → `TempPoll{pv_raw, pv, op_pct, at, srun, ctn}`（C# 一轮 5 次块读）；`req_write_sp / req_start / req_stop / req_autotune / req_write_pid / req_read_pid / req_read_sv_block / req_stop_all(Srun=9655) / req_run_all / req_write_ctn`；`stop_all_loops_requests()`（8 路 At=4）。
- 8AI：`req_read_raw()`、`decode_psi(r, fullscale, PressSignal)`、静态 `scale()` / `to_psi()`。
- IO16R：`req_write_valves(Valves)`（0x0F 从 0 起 12 路）、`req_set_sv / req_set_gas / req_set_vent`（0x05）、`req_close_range / req_close_gas_sources / req_close_all_valves`、`decode_open_states`（经极性）。
- DM2C：`req_run_velocity(motor_rpm, accel, decel)`、`req_stop()`、`req_start(motor_rpm, ramp)`（复位报警 + 触发）、`req_set_peak_current / microstep / standby_pct`（各两笔：写参数 + 存 EEPROM）、`req_read_status / alarm / bus_voltage / peak_current / microstep / control_word`；`stir_gear::to_motor / to_vessel`。

**组装**：`std::unique_ptr<Hardware> make_hardware(const HardwareConfig&)`，`Hardware` 里有 `temp_bus / stir_bus / press_bus / io_bus`、4 个驱动、`sim_main / sim_stir`、`simulate_step()`、`urgent_stop_all(stop_stir, done)`、`xxx_configured()`。

**配置**：`load_hardware_config(path, out, &err)` / `save_hardware_config(...)`（tmp + rename）/ `hardware_config_from_json` / `validate_hardware_config`。格式见 `hardware_config.hpp` 头注释（camelCase，缺字段取默认）。默认：temp `/dev/ttyS5` 19200、stir `/dev/ttyS4` 38400、press `/dev/ttyS6` 9600、io `/dev/ttyS8` 9600，站号 1，超时 300 ms，8N1，dpt 1，信号 4-20，满量程 600 psi，energize open，sim false。`HardwareConfig::for_sim()` 给开发机用。

## 3. 线程契约

- **每条总线一个 I/O 线程**，只有它碰自己的串口（`RtuModbusMaster` 不是线程安全的）。
- 取事务：紧急队列优先，同级 FIFO；在途那笔不打断；不重试；排上的事务不可取消。
- `submit*` / `queued` / `idle` / `stats`：**任何线程**可调。
- **回调在总线 I/O 线程上执行**：要短；正常做法是把结果投递回控制线程。回调里可以再 `submit`；不能在回调里同步等本总线（`BusMaster` 会直接返回失败而不是死锁）；不能在回调里 `stop()` / 析构 Bus。
- `submit_seq`：每步做完才把下一步排到同级**队尾**——中间可插入紧急帧和别人的事务（与 C# 每步重新 await PriorityGate 一致，测试 `sequence_reenters_queue_tail` 固定了这个顺序）。
- `stop()` 后 `submit` 立即在**调用方线程**上回调失败「总线已关闭（名字）」。每个回调保证只调一次。
- `CommStats` 全原子；`MockModbusMaster`、`OfflineModbusMaster` 线程安全（Mock 单锁，可被三条总线共用）；驱动对象不可变，随便共享。
- `Hardware::urgent_stop_all`：任何线程可调，立即返回；`done` 在最后完成的那条总线线程上调一次。

## 4. 移植了的 C# 行为

- RTU 事务：惰性开串口、每笔前 `DiscardInBuffer`（`tcflush`）、写帧、先读 2 字节判异常、再读 3 字节或剩余部分、校验地址 + CRC（另加了功能码、字节数字段校验）、**任何错误关串口**、下笔重开；300 ms 超时（每段读各自 300 ms，同 C# ReadTimeout）；异常文案「Modbus 异常 0x02（非法数据地址）· 从站 1 功能 0x03」、「Modbus 响应不足：期望 n 字节，实收 m」、「Modbus 从站地址不匹配…」、「Modbus CRC 校验失败」照抄。
- PriorityGate 语义（见 §3）。
- Mock：内存表、未写地址读 0、`simulate_step` 的 PV 漂移（每步最多 5 原码 + ±1 噪声、At=4 或 Srun=9655 目标 25.0 ℃、PV 初值 = SP）、自整定 6 步后回 0 并写随机 PID、首步 SeedTemp（TryAdd）、压力 8000 起 ±30 夹 3200..16000；温控 / 压力 / 阀门共用一个、搅拌单独；`sim` 且配置了端口 → 拒绝模拟。
- Offline：每笔失败，文案「…未配置串口」。
- Tracing：行格式、【写】【急】标记、开头「=== 追踪开始 … ===」照抄。
- CommStats：初始离线、连续 3 次失败离线、成功清零。
- 4 个驱动的寄存器、换算、夹值全部照抄（含 C# 的不对称：P 写无符号、读有符号；D 有符号；I 无符号 0.1 s；OP ÷256；8AI 低于起点按 0、高于 16000 不夹；DM2C 电流 0.3–3.2 A、细分 200–51200、待机 0–100；传动比 85/38、电机上限 3000）。`Math.Round` 的五成双用 `std::nearbyint` 复刻（测试覆盖 12.25 ℃ → 122、1.25 A → 1.2 A）。
- 「全部停止」用逐路 At=4，不用 Srun=9655（C# `StopAllChannelsAsync`；设计文档 §4）。

## 5. 测试（35 个用例，全部 `hw_` 开头，全绿）

| 组 | 覆盖 |
|---|---|
| `hw_modbus` | CRC、8 个功能码组帧、回包长度、位解码、请求校验、追踪描述文案、五成双 / 夹值、Offline 文案 |
| `hw_bus` | **翻译 C# PriorityGateTests**：空闲立即执行、紧急插队 + 普通 FIFO、「紧急一遍 + 普通一遍」落点、紧急之间 FIFO、200 笔并发压测永不两笔在途；另有序列重新排队尾、失败即停、stop 回失败 / 拒新、drain、CommStats 离线判定、经总线的离线检测、OfflineModbusMaster 永远离线、BusMaster 同步 / 防死锁、模拟硬件上的 `urgent_stop_all`（8 路 At=4、12 路全关不碰 13 号、DM2C 0x6002=0x40、Srun 不动）、全离线配置、模拟拒绝 |
| `hw_temp` | SP 编码（负数、五成双、dPt 0/2）、块读、`poll` 5 笔顺序 / 地址、一轮失败即停、模式 / Srun / Ctn、PID 编解码和写顺序 |
| `hw_mock` | 首步预置、PV 漂移步长和收敛、At=4 / Srun=9655 冷却、自整定 6 步、压力抖动范围、故障注入 |
| `hw_analog` | 4-20 / 0-10 换算（含越界行为）、经模拟读 |
| `hw_io` | 12 路整帧、极性翻转、只关不开范围、单路 0x05 地址映射、DI |
| `hw_stepper` | 速度路径 8 寄存器、停止、启动两笔、参数夹值 + 存 EEPROM、状态位；**翻译 C# StirGearTests** |
| `hw_config` | 默认值 = run.sh + 代码默认波特率、往返、部分字段、各种非法值、坏文件 |
| `hw_rtu` | **pty 真串口收发**（`posix_openpt`，不需 `-lutil`）：8 个功能码、异常码、CRC 错、站号错、超时 ≈300 ms 且只发一次、出错关串口后重开、残留输入被丢弃、非法请求不发帧、打不开串口；再用 Bus + AI-8 驱动跑一轮轮询 |

本机 macOS clang：`make -C tools/build -j8 test` 编译无警告，hw 用例 35/35 通过（重复 10 次稳定）。整体运行时有 1 个失败在别的模块（`tests/core_program/test_program_json.cpp` 浮点格式 458.90000000000003），与 hw 无关。另用 `-Wpedantic -Wshadow -Wconversion` 查过 hw 文件，无警告。

## 6. 和 C# 不同的地方（有意为之）

1. **同步主站 + 总线线程**代替 async/await；结果结构代替异常。
2. **没有急停**：无 Locked 位、无 `ValveLockedException`、无锁存。紧急队列保留，只给「全部停止」用。
3. 端口 / 波特率来自 `hardware.json`，不读环境变量。模拟拒绝规则在 JSON 下等价为「`sim:true` 且四个端口都为空才模拟」——默认配置有端口，所以开发机必须显式清空端口（`HardwareConfig::for_sim()`），现场残留 `sim:true` 不会生效。
4. 非有限值 / 越界值写寄存器时**夹到范围**（C# 是未定义截断）；NaN 写 0。上层本该先用 ValueRules 拦住。
5. RTU 多校验了功能码和读响应的字节数字段（C# 只验地址 + CRC）。异常帧的 CRC 仍不验（同 C#）。
6. `Bus::stats()` 是**逐笔**计数；C# 温控是「一轮 5 笔算 1 次」、IO / 搅拌是逐笔。要和旧 telemetry CSV 计数口径一致，ctl 另建 `CommStats` 按 C# 口径记（见 §8）。
7. 追踪默认**关**（run.sh 里 `PR_STIR_TRACE=1` 是开的）：守护进程长期运行文件会无限长，且工作目录是 `/`。需要时在 `stir.trace` 填路径。
8. `TempController` 默认 8 回路（C# 构造默认 4，但实际一律按 8 用）。
9. 失败的写请求在 Mock 的 `writes()` 里也记录（测试便利）。

## 7. 待确认的问题（寄存器 / 现场）

1. **波特率**：温控 19200、搅拌 38400 来自 C# 代码默认（run.sh 没设），上真机先核对（设计文档 §13 #1）。
2. **SV 0x0480**：C# 只定义了常量从未读过，我按「有符号 ÷10^dPt、8 路块读」实现，未对手册逐字核对。
3. **P 寄存器的符号**：C# 写 `(ushort)Math.Round(p×10^dPt)`、读 `(short)`，照抄了；P 实际不会为负，影响不大。
4. **IO16R 整帧只写 12 路**（C# 注释说「16 路」但实际传 12 个值）；线圈 12–15 和 16 路 DI 目前没用途，是否接了东西未知。
5. **DM2C**：0x6002=0x0040 手册叫「急停」，C# 注释说「减速停」——实际是急停还是按 Pr9.05 减速，需上机看；Pr9.07=0x10 触发映射、母线电压 0x0177、32 位参数只写低 16 位，均来自 C# 未对照 PDF；PR 路径是否要求先回零（`req_manual_set_zero` 已保留）。
6. **Srun=15**（断电后自动全停）由 ctl 在启动加热时写 Srun=0 处理；设备层只提供读写。
7. **超时口径**：头 2 字节、剩余部分各等 300 ms，最坏一笔约 600 ms + 发送时间（与 C# 相同），紧急帧最坏等这么久。
8. `hardware.json` 放在 hw 模块（按本任务要求）；代码约定的 store 表里也写了「硬件配置」——如 store 要统一管理，可以直接调 `hardware_config_from_json` / `to_json`，不要再定义一份。
9. 板子上的 **GCC 9.4 没有在本机验证**（本机无 GCC / Docker）。代码只用 C++17 + POSIX，Linux 专有 termios 位沿用原文件的 `#ifdef CRTSCTS` 保护；请在 arm64 容器里跑一次 `make test`。
10. 读 `hardware.json` 失败时怎么办（拒绝启动 / 用默认值 / 全离线）由 apps/reactord 决定；我的建议是记 err 级事件并按**全离线**启动（不要用默认端口去碰串口）。

## 8. ctl/ 需要接上的东西

1. **启动**：`load_hardware_config("/etc/reactord/hardware.json")` → `make_hardware(cfg)`；`simulated` / `sim_refused` 进 `/health` 和快照；数据目录模拟时另放。
2. **回调投递**：所有 Bus 回调都在 I/O 线程上，ctl 把解码结果投递进控制线程队列，在控制线程上改状态。
3. **每 Tick（1.2 s）**：模拟时 `hw->simulate_step()`；温控 `temp_bus->submit_seq(temp.poll_requests(), Normal, true, …)` + `decode_poll`，**上一轮未完就跳过**（C# `_polling`，可用自己的标志或 `queued()`）；压力 `press_bus->submit(press.req_read_raw(), …)` 同样 skip-if-busy，泄漏 / 序列的即时读不跳过；搅拌每 3 Tick 读报警码 / 母线电压；IO 不轮询。
4. **模块在线**：按 C# 口径各建一个 `CommStats`（温控一轮 1 次；压力每次读；IO、搅拌每笔），`online()` 驱动 `comm:k` 报警（开机前 5 Tick 不判）。
5. **TempService 的业务**（全在 ctl）：开机 Ctn<8 写 8 并回读、8 路 At=4、仅首次运行用仪表 PID 作种子；StartChannel = Srun=0 → At=0 → 按档套 PID → 写 SP；按档 PID 缓存 `_lastBand` 与 PidGen；自整定（先读 SP ≥35 ℃、Srun=0、At=1、AutotuneBook 记发起档，完成回读）；取消自整定写 At=4；超温切断 At=4 + 回读确认；`StopAllChannels` 逐路 At=4。
6. **ValveService 的业务**：SV 打开计数（吸气量断基准用，写总线前加一、失败也加）；整帧只经 `core/ValveFrame` 拼好再 `req_write_valves`；联锁收尾用 `req_close_range`（不要按界面阀态重写整帧）；气路序列每 1 s 重写整帧当保持心跳。
7. **StirController 的业务**：开 / 关 / 釜转速状态、`StirGearRatio`（settings，默认 85/38）换算 `stir_gear::to_motor`、`RampMs` 默认 1000、启动 = `req_start(motor, ramp)`、改速只在开着时重发 `req_run_velocity`、通讯错误 5 s 节流上报；电流 / 细分 / 待机写入走设置页（本地命令）。
8. **PressureService**：满量程运行时可改（设置页），每次换算用当前值；保留最近一帧 {raw, psi, at}。
9. **全部停止**：快速路径先 `hw->urgent_stop_all(stop_stir, done)`（紧急队列），**再补一遍普通优先级**的同样停止帧（盖住按下前已排队的旧开阀帧，见测试 `normal_copy_after_urgent_lands_after_stale_normal`），然后控制线程做完整收尾；不放空、不锁存。
10. `/device/comm` 的统计取第 4 条里 ctl 自己的 CommStats（需要逐笔数据时可加 `bus.stats()`）。
11. 退出：`hw->stop()`（或析构），排队中的事务回失败；需要保证停止帧落地时先 `wait_idle()`。
