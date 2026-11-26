# 模块报告 · ctl 控制核心（第二阶段）

2026-11-26 · 命名空间 `reactor::ctl` · 目录 `src/ctl/`、`tests/ctl/`
对应设计文档 §4–§7、决定记录（死区 0.5 / 1.0 s、稳压 2 s 状态机、内置模板种进 library/templates），附录 §5（程序引擎）、§5.3（调度）、§7、§8。

`make -C tools/build -j8 test`：全量 **482 个用例，0 失败**（`run_tests ctl_` 44 个，其中本阶段新增 19 个；`ctl_` 连跑 10 次稳定）；编译无警告。
`src/ctl/*.cpp`、`tests/ctl/test_ctl_*.cpp` 另用 `-Wall -Wextra -Wpedantic -Wshadow` 单独查过，无警告（macOS clang）。
`make -C tools/build reactord && tools/build/out/reactord --sim --seconds 15` 跑过：12 帧快照、正常退出（reactord 还没接第二阶段，见 §8）。
GCC 9.4 没有在本机编过；只用 C++17（结构化绑定没有被 lambda 直接捕获，GCC 9 的已知坑已避开）。

---

## 1. 做了什么

| 范围 | 文件 | 移植自（C#） |
|---|---|---|
| 统一结果 `OpResult`（done / fail / accept / none / invalid，code + 中文 message + level + data + warnings + quiet） | `ctl_op_result.hpp` | `Commands/OpResult.cs` |
| 气路公共底层：`gas_ops::gas_block / heat_block / gas_unsafe / pressure_of / temp_or_nan`；`GasOps`（整帧写阀 → 保持 n 秒每 ≤1 s 安全检查 + IO16R 心跳 → 即时读压（失败再读一次）→ 吹扫序列；续体带代号，`cancel()` 丢弃挂起续体；`when_idle()` 等在途写阀回来再收尾） | `ctl_gas_ops.*` | `MainViewModel.Gas.cs`（GasBlock / ThrowIfGasUnsafe / ApplyValvesAsync / HoldAsync / ReadPressureSampleAsync / RunPurgeAsync）、`MainViewModel.HeatBlock` |
| 程序引擎：草稿（PUT 冻结为 StepSnapshot，开机预置「加氢筛选」）、复制、下发守卫两次、执行（每通道一个 Run 驱动 `core::program::StepRunner`）、气路排队、吹总管、升温曲线、2 s 稳压状态机、中止 / 收尾、快照字段 | `ctl_program_engine.hpp`、`ctl_program_engine_impl.hpp`、`ctl_program_engine.cpp`（主流程）、`ctl_program_engine_gas.cpp`（气路 / 曲线 / 稳压 / 超压）、`ctl_program_engine_snap.cpp`（快照） | `ProgramViewModel.Run.cs`、`ProgramRun.cs`、`ProgramViewModel.cs`（RunGuard / StartRun / TemplateSteps）、`OperatorCommands.Program.cs` |
| 泄漏测试（多通道：关阀读压 → 脉冲充压 → 总管泄压 → 平衡 → 测量 → 判读 → 排空核实 → 结果文件 / 会话 / LastLeakTestUtc） | `ctl_leak_test.*` | `LeakViewModel.cs`、`DeviceActivity.cs` 的 `LeakActivity`、`OperatorCommands.Leak.cs` |
| 容积标定（空釜一遍 / 等加水 / 加水一遍，拟合 + 验收限值在 core，结果写设置） | `ctl_volume_calib.*` | `CalibViewModel.cs`、`CalibActivity`、`OperatorCommands.Calib.cs` |
| 自整定（启动 / 取消 / 进度 / 完成回读存回发起时的档 + 来历） | `ctl_autotune.*` | `AutotuneBook.cs`、`TempService.AutotuneAsync / CancelAutotuneAsync / ReloadPidAsync`、`OperatorCommands.Temp.cs` |
| 内置模板（T1 加氢筛选 / T2 只做气密 / T3 惰性气氛加热 / T4 吸气量验证），种进 `library/templates`（builtIn，只读）；PC 形状（MPa）→ §6.9（psi）换算 | `ctl_builtin_templates.*` | `Control/.../ProgramTemplates.cs`、`StepDefaults.cs`（Of / Fill / PsiToStored / RateToStored / ToWire）、`PcProgram.WriteSteps` |
| 总入口 `Automation`：组装以上全部、注册钩子、`ActivityHooks` 实现、任意线程的 `*_async` 包装 | `ctl_automation.*` | — |

### 1.1 程序引擎要点
- **生命周期**：`preparing → running ⇄ waitingGas → finishing → completed | failed | aborted`（`core::program::ProgramRunInfo`）。下发即 `running`（同 C# SetPhase(Running)）；排队等气路时 `waitingGas`（只有非静默排队；稳压脉冲排队不改阶段）。
- **步骤节拍**：`StepRunner::tick` 返回 `wake_at_ms` → 单次定时器；`fresh_pressure` → 下一次 tick 前 `read_now`（失败再读一次，仍失败交给步骤报「RVn 压力读取失败」）；到点时若还有在途写阀，先等写阀回来再 tick（保证脉冲长度从阀门真正打开算起）。
- **安全检查**（C# 的三种）：持有气路时每次唤醒 `gas_unsafe`（≈ HoldAsync 每秒 ThrowIfGasUnsafe，文案逐字）；不持有气路的定时等待（气密静置 / 保压、升温、保持、反应、曲线每 2 s）查本通道：报警 → 失败「RVn 超温 / 超压报警」，被外部置停 → 手动中止；排队等气路每 0.5 s 查 ThrowIfWaitUnsafe（从非 Done/Idle 转入 Done/Idle 才算外部停止）。
- **气路**：`acquire` 逐条照 C# AcquireGasAsync：泄漏测试 / 标定占用 → 立即故障「泄漏测试进行中，请等待完成或先停止」；空闲且无人排队 → 立即取（不多出事件）；否则 `GasQueue` 排队（单调时钟、每通道一张票），首次进入等待记「RVn 程序 · 等待气路」（含排队位次），排到队首但公共检查不通过 → 继续等待（原因变化再记 warn），取到记「气路就位」；超过 `ProgramGasWaitMin` → 步骤故障「等待气路超过 x 分钟（…）」。
- **写阀**：步骤的 `Valves` 动作 → `valve_frame` 拼整帧 → `ControlCore::write_valves(dead_ms = 1000)`；成功后作为心跳帧（`set_valve_hold`）。`ReleaseGas` = 等在途写阀 → 停心跳 → 全关 → `end_gas(Program, gen)`（只释放自己这段）。吹总管走 `GasOps::purge`（`core::purge_plan`，死区 0.5 s，状态文字「…吹总管 惰性气体 c/n · 阶段」）。
- **稳压**（决定记录）：每通道一个 2 s 状态机：本通道报警 / 被停 → 静默退出；读压 ≥ 目标 − band 不动；公共检查不通过 → 不取气路、记一次原因、超过上限按程序故障；否则排队取短租约脉冲：开阀 sec（首次 0.2 s，之后按增益补 60%，0.1–1.5 s）→ 关阀静置 1 s → 读压 → 记「RVn 稳压补压」→ 释放。脉冲之间气路空闲，其他程序可以取用；对泄漏测试 / 手动阀控仍算占用（`gas_pending()` = 「RVn 程序 · 稳压」）。稳压开着时打开第一阶段的压力偏离监视（`begin_pressure_watch`）。
- **中止**：`abort`（中止按钮）/ `abort_channel`（单路停止 / 停用）/ `abort_all`（全部停止 / 退出）照 C# AbortProgram：立即停曲线 / 逼近、释放搅拌、下发一次停加热（全部停止 / 退出除外，见 §4-2），再收尾。超压联锁经气路中止监听：只有**当前持有气路**的程序失败（「气路被超压联锁中止」，脉冲期间为「稳压：气路被超压联锁中止」），排队的程序继续等。
- **收尾顺序**（C# ExecuteAsync finally 逐条）：阶段 finishing → 停稳压 → 等在途写阀 → 气路仍归自己这段租约才全关 + 释放 → 停曲线 / 逼近 → 停加热（中止时已发且之后没碰过加热就复用，否则再发「RVn 程序收尾 · 加热输出已停止」）→ SV 开着就关 → 释放搅拌 → 停计量（程序中止 / 程序手动停止 / 程序结束）→ 通道非停用转已结束、rpm 归零 → 终态文字 → 移到「最近一次运行」→ 事件 + 提示（程序完成 / 程序中止 / 程序手动停止）。**不自动放空**。
- **搅拌**：`StirShare` 引用计数；同时维护 rv → rpm（`StepInput::stir_holders` 要转速）。释放时没有其他持有者才停；有就恢复为剩余最近登记者的转速并记 warn「搅拌转速恢复」。
- **加热**：定值 = `start_fixed_heat`（防超调逼近 + 升温超时监视，band 用步骤的 band）；曲线 / rate > 0 = 引擎私有 `TempProfile` + `start_heat_chain` + 每 2 s `write_temp_sp`（不动通道的抽屉曲线）。全链启动期间被中止 → 标记「中止后碰过加热」，收尾再停一次。
- **会话**：有活动会话时事件带「· 会话 x」，`SessionBook::tag_program_run(rv, now, startedBy)`。

---

## 2. API（`ctl_automation.hpp` 为总入口）

线程规则：**「控制线程」**的方法只能在控制线程上调（命令层把「仲裁 + 守卫 + 执行」放进一个 `core.call` 任务）；**`*_async`** 任何线程可调，返回 `std::future<OpResult>`（控制线程上不要 `.get()`）。

### 2.1 组装
```cpp
ctl::AutomationDeps ad{&core, &logger, &sessions, &library /*可空*/, core::PressureUnit::MPa, "PR8"};
ctl::Automation automation(ad);   // core.start() 之前；注册 ProgramHooks / ActivityHooks / 气路中止监听 / 整定回调，种内置模板
core.start(); … core.shutdown(true); /* 然后再析构 automation */
```

### 2.2 程序（`automation.programs()` → `ProgramEngine`，控制线程）

| 方法 | 结果 / 错误码（文案照 C#） | async 包装 |
|---|---|---|
| `replace_program(ch, json steps, optional name)` | ok「RVn 程序已更新（n 步）[ · 已从旧格式转换]」data `{rv, steps(§6.9), migrated}` + warnings；`CHANNEL_NOT_FOUND`、`CHANNEL_PROGRAM_OWNED`「程序执行中 · 已锁定为只读」(warn, quiet)、`CHANNEL_BUSY`「该通道正在运行 · 程序已锁定为只读」(warn, quiet)、`program_json` 的 `PROGRAM_INVALID / PARAM_OUT_OF_RANGE`（detail 照 C#）、全局限值 `PARAM_OUT_OF_RANGE`（data `{errors}`） | `replace_program_async` |
| `copy_program(from, to[], strict)` | ok「已将 RVa 的 n 步程序应用到 k 个通道」data `{from, to}`；`BAD_REQUEST`「请先选择目标通道」(warn)；strict 时「RVn 已停用 / 正在运行 · 已跳过」进 warnings | `copy_program_async` |
| `draft_json(ch)` | `{rv, name, steps, migrated, locked, estimate}`（新） | `draft_json_async` |
| `check_run(ch)` | 通过 = silent；被拒「下发被拒绝 · …」(ProgramRunGuard 的码) + 事件「程序下发被拒绝」 | `check_run_async` |
| `run(ch, started_by)` | 再判一次守卫 → accepted data `{rv, steps}` + 下发前提示 warnings | `run_program_async` |
| `abort(ch)` | 没有程序 = silent；ok「RVn 程序已中止 · 正在安全停止」(warn, quiet) data `{rv}`，搅拌只剩它持有时 warnings「中止程序将停止全局搅拌」 | `abort_program_async` |
| `run_guard(ch, steps)` / `gas_block_for(rv, need_p)` / `limits()` | `optional<GuardReject>` / 程序方面的气路公共检查 | — |
| `is_owned / running_ids / running_count / gas_pending / stir_step_rv / run_info / gas_queue / stir_share` | 命令层守卫用（`CHANNEL_PROGRAM_OWNED`、`STIR_PROGRAM_OWNED`、`GAS_BUSY`…） | — |
| `on_step(rv, i, state, msg)` / `on_finished(rv, phase)` 回调 | 每步 running / completed / failed / aborted、终态（给 WS `progress`） | — |
| `set_draft(ch, steps, name)` | 直接放草稿（测试 / 程序库载入，不检查） | — |

### 2.3 泄漏测试（`automation.leak()` → `LeakTest`）
| 方法 | 结果 | async |
|---|---|---|
| `start(channels[], optional testPsi, started_by)` | accepted data `{runId, channels, testPsi}`；`LEAK_TEST_RUNNING`(warn, quiet)、`BAD_REQUEST`「请至少选择一个测试通道」、`CHANNEL_NOT_FOUND`、`CHANNEL_BUSY`「RVa、RVb 已在运行或正在执行程序 · …」、气路公共检查（`HW_NOT_CONFIGURED / HW_OFFLINE / GAS_BUSY / MANUAL_VALVE_ACTIVE / CHANNEL_ALARM / CHANNEL_PRESSURIZED`）、testPsi 越界 `PARAM_OUT_OF_RANGE`、≥ 超压 × 90% `PARAM_CROSS_CHECK`（data `{testPsi, max}`） | `start_leak_test_async` |
| `abort(strict)` | 没有 = silent；strict 且别人占用 `GAS_OWNED_BY_OTHER`；ok「泄漏测试已中止 · 正在全关阀」(warn, quiet) | `abort_leak_test_async` |
| `running() / run_id() / snapshot()` | — | — |

### 2.4 容积标定（`automation.volume_calib()` → `VolumeCalib`，命令层只在本机入口开放）
| 方法 | 结果 | async |
|---|---|---|
| `start(ch, with_water, water_ml, redo_manifold, by)` | accepted data `{channel, withWater, waterMl}`；`SYSTEM_BUSY`(warn, quiet)、`LEAK_TEST_RUNNING`、`CHANNEL_NOT_FOUND`、`CHANNEL_BUSY`、`BAD_REQUEST`「RVn 还没有做空釜一遍 · 请先开始标定」、加水体积 `PARAM_OUT_OF_RANGE`、气路公共检查 | `start_calib_async` |
| `abort()` | silent / ok「容积标定已中止 · 正在全关阀」 | `abort_calib_async` |
| `discard_first()` / `set_water_ml(ml)` | 放弃第一遍（快照 calib → null）/ 面板加水体积 | `discard_calib_async` |

### 2.5 自整定（`automation.tuner()` → `Autotune`）
| 方法 | 结果（done 回调在控制线程上，要等硬件） | async |
|---|---|---|
| `check(ch)` | silent / 启动加热公共检查的码 | — |
| `start(ch, tempC, optional band, strict, done)` | ok「RVn 已设目标 x℃ 并启动自整定」data `{channel, sp, band}`；`CHANNEL_NOT_FOUND`、HeatGuard 的码、`PARAM_OUT_OF_RANGE`（目标 40..TMaxSp / 显式档位不含目标温度）、strict：`AUTOTUNE_ACTIVE` / `CHANNEL_STATE_INVALID`；`HARDWARE_WRITE_FAILED`「SP 下发失败，未启动整定」/「通讯失败，未能启动整定」/ `SP_NOT_SET` | `start_autotune_async` |
| `cancel(ch, done)` | ok「RVn 已取消自整定并停止输出」 | `cancel_autotune_async` |
| `tune_band / tune_sp / is_aborted / snapshot` | AutotuneBook 的查询 | — |

### 2.6 其他（`Automation`，控制线程）
- `gas_locked_for_manual()`：手动阀控 / 手动开关阀前：`GAS_BUSY`「{任务}进行中 · 阀门由自动序列控制」/「RVn 程序 · 稳压进行中 · 阀门由程序控制」。
- `gas_block(need_pressure)`：自动气路序列的公共检查（带程序隐性占用）。
- `seed_result()`：开机种模板的结果。
- `builtin_templates::all(model) / seed(lib, model) / pc_steps_to_wire(steps)`：纯函数（任何线程）。

### 2.7 第一阶段文件的改动（都是文档里的扩展点，原有测试保持绿）
- `ctl_hooks.hpp`：`ProgramHooks::program_run(ch)`、`ActivityHooks::autotune()`（带默认实现）。
- `ctl_snapshot.hpp/.cpp`：`Snapshot::program_runs[8]`、`Snapshot::autotune`；`to_json` 末尾加 `"autotune"`。`tests/ctl/test_ctl_snapshot.cpp` 的顶层字段表末尾加了 `"autotune"`（只追加）。
- `ctl_core_publish.cpp`：填上面两项。
- `ctl_core.hpp/.cpp`：`close_sv(id, cb)`、`emit_notice(kind, msg)`、`remove_gas_abort_listener(id)`、`start_autotune_chain(id, cb)`。
- `ctl_temp_service.hpp/.cpp`：`start_autotune(id, epoch, cb)`（Srun=0 → At=1，带停止代号）、`read_pid(id, cb)`。

---

## 3. 快照新增（只加不改；C# 原有字段名 / 顺序一个不动）

| 位置 | 新字段 |
|---|---|
| `channels[i].program`（C# ChannelProgramSnap 之后） | `name`、`phase`（监视阶段文字）、`status`（一行状态）、`stepName`、`stepNames[]`、`progress`（「3 / 9」）、`elapsedMin`、`remaining{minutes, text「约 ≤ 9h 10m」, approx, upperBound}`（当前步骤估算 − 已用 + 后续步骤，StepFields 的 DurationEstimate）、`gasQueuePos`（排队位次 1 起）、`waitingOn`、`regulating`、`endReason` |
| `programs`（C# ProgramsSnap 之后） | `gasQueue[]`（全部排队含稳压脉冲）、`gasPending`（程序隐性占用说明或 null） |
| `leakTest`（C# LeakSnap 之后） | `lastRunId`、`phaseText`、`resultTitle`、`resultText` |
| `calib`（C# CalibSnap 之后） | `twoRuns`、`resultTitle`、`points[[前, 后]…]` |
| 帧末尾 | `autotune`：`null` 或 `{channels:[{channel, state(tuning/reading/done/aborted/failed), running, band, bandName, spC, startedAt, finishedAt, elapsedSec, result{p,i,d}, message}]}` |
| `Snapshot::program_runs[i]`（不进帧） | GET /channels/{id}/program/run：C# ProgramRunSnap（channel, running, state, stepIndex, stepCount, phase, message, startedBy, startedAt, waitingGas, steps, endReason）+ 新 `name`、`status`、`stepStates[{index,state,message}]`、`remaining` |

遥测 CSV「程序步」列 = `"{index+1}/{phase}"`（同 C#）。

---

## 4. 和 C# 不一样的地方

1. **async/await → 续体 + 状态机**：每个 C# await 是一个定时器或总线回调；取消 = 代号 +1 丢弃挂起续体（`GasOps::cancel`、`Run::gen`），没有 CancellationToken。
2. **全部停止 / 退出时不再重复停加热、关 SV**：快速路径已经紧急 + 普通各发一遍（C# 急停时同样跳过）。单路停止 / 中止按钮照 C# 立即下发一次停加热。
3. **写阀先等在途再收尾**：释放气路、收尾全关之前等在途写阀回来（`when_idle`），保证落到 IO16R 的最后一帧是全关；超压中止时若没有在途写阀就在监听回调里同步「全关（保持排空）→ end_gas」，有就等写阀回来再做（end_gas 会清「保持排空」，顺序不能反）。
4. **曲线每 2 s 推进一次并查安全**（C# WaitSliceAsync 是 1 s 切片查一次、2 s 推进一次）。
5. **超压时的文案**：持有气路的程序在每次唤醒先查 `gas_unsafe`，被监视通道压力到超压值时常常先报「RVn 超压（x MPa）」（与 C# HoldAsync 一样，取决于谁先看到）；控制周期的联锁先动作时报「气路被超压联锁中止」。
6. **步骤状态合并**：中止时 C# 先写「停止指令已下发」再写「已中止」，这里直接是「已中止」。
7. **自整定完成新增事件**「自整定完成」（info，C# 只刷新界面）；完成回读的档没有软件发起记录时按当前 SP 所在档（C# 用界面选中档）。
8. **泄漏测试结果**：面板文字（resultTitle / resultText）也放进快照（C# 只在面板）；`runId` 结束后为 null（同 C#），新增 `lastRunId`。
9. **新增 `on_step` 回调**：每步状态变化给 WS `progress` 用（C# 没有）。
10. 程序事件 kind 显式：`program.started / hint / step / gasWaiting / gasGranted / regulation / regulationFailed / curve / stirRestored / completed / failed / aborted / runRejected`、`leak.started / passed / failed / aborted / error`、`calib.started / step / firstPass / done / failed / aborted`、`temp.autotuneStarted / Rejected / Cancelled / Done / ReadFailed`、`alarm.heatStartFailed`。

---

## 5. 测试（`tests/ctl/`，本阶段 19 个）

`ctl_phase2_util.hpp`：`PRig`（模拟硬件 + ManualClock + 不起线程的核心 + `Automation`；压力总线换成不抖动的主站，读数由 `Physics` 按阀门线圈积分：开气源 → 总管到减压阀压力，开 SV → 釜与总管交换（体积比守恒），开排空 → 泄压，每路可设泄漏 psi/h）、`steps({...})` 造冻结步骤、`fast_settings()`。

| 文件 | 用例 |
|---|---|
| `test_ctl_program.cpp` | `ctl_program.all_nine_step_types_end_to_end`（leak→exchange（吹总管 + 置换）→stir→heat→press（稳压）→hold→react（最长时间到）→vent（置换）→end，检查事件、结束原因、阀全关、气路空闲、搅拌停、计量停、stepStates）、`replace_copy_and_guards`（PUT / 超限 / 复制 / 预检 / 锁定 / 重复下发被拒 + 事件 / 快照新字段 / 中止幂等）、`abort_paths_and_end_reasons`（中止按钮、单路停用、全部停止中途 2 个程序、执行前限值被调低 → 失败）、`over_pressure_during_gas_step_fails_holder_only`（RV1 充压中 RV3 超压 → 只有 RV1 失败，RV2 排队继续等，超压解除后自行完成）、`heat_curve_and_rate_paths`、`stir_share_fallback_and_last_release_stops`、`step_events_in_order` |
| `test_ctl_gas.cpp` | `ctl_gas.two_channels_fifo`（排队位次 / waitingOn / gasPending，手动阀控与泄漏测试 GAS_BUSY）、`eight_channels_fifo_order`（启动顺序 3 1 4 8 2 7 5 6 → 取到顺序 1 4 8 2 7 5 6）、`wait_limit_faults_the_step`（0.5 min 上限 →「等待气路超过 0.5 分钟（RV1 占用中）」，RV1 不受影响）、`regulation_pulses_interleave_with_other_programs`（漏气通道稳压补压 ≥2 次、压力维持、期间 RV2 取用气路完成） |
| `test_ctl_activity.cpp` | `ctl_leak.pass_and_fail_with_result_files`（3 路，RV2 漏 20 psi/h → fail，其余 pass，排空核实，LastLeakTestUtc，leak/*.csv / -summary.csv / -summary.json 落盘，期间程序下发 GAS_BUSY）、`ctl_leak.abort_and_guards`（守卫码、中止、全部停止中止）、`ctl_calib.one_run_with_known_manifold`（Veff ≈ 8 mL）、`ctl_calib.two_runs_wait_for_water_then_fit`（waitWater → 加水 5 mL → 总管 ≈ 2 mL、Veff ≈ 8 mL）、`ctl_autotune.completion_saves_pid_into_starting_band`（显式档位不符被拒；模拟仪表 At=1→0 后回读 PID 存入低温档 + 来历 60 ℃）、`ctl_autotune.cancel_does_not_store`、`ctl_templates.builtin_seeded_read_only_and_runnable`（4 个、参数顺序与取整逐字、只读、重复种不变、重启后仍在、§6.9 换算后 PUT 解析通过并可下发）、`ctl_automation.threaded_async_wrappers`（起控制线程，`*_async` 走一遍：PUT / 草稿 / 预检 / 下发并完成 / 快照 program_runs / 泄漏测试开始与中止 / 自整定参数错） |

C# 没有这一层的单测（流程在 VM 里），以上都是新写的场景测试。

---

## 6. 开放问题 / 需要别的模块配合

1. **store：内置模板没法随版本更新**。`ProgramLibrary::update / remove` 拒绝 builtIn 条目，`create` 对已有 id 回 REVISION_CONFLICT，所以内置模板第一次种下之后就固定了（C# 是「软件升级时跟着更新」）。`seed()` 发现内容不同时只计 `outdated` 并记日志。建议 store 加一个只给服务用的 `upsert_builtin(LibKind::Template, body)`。
2. **core：`StepInput::stir_holders` 要 (rv, rpm)，`StirShare::holders()` 只给通道号**。引擎自己维护 rv → rpm 补上了；建议 core 给 StirShare 加 `holder_list()`。
3. **apps/reactord 还没接第二阶段**（本阶段不许改 apps）。第三阶段要在 main 里：`SessionBook` + `ProgramLibrary` + `Automation` 组装（§2.1），退出顺序 `core.shutdown(true)` → 析构 `automation` → `logger.stop()`。
4. **单路停止正在自整定的通道**：C# StopChannelAsync 只写 At=4、不标记整定打断，轮询看到整定结束会把仪表里的 PID 当结果存档；这里保持 C# 行为。若要改，命令层在 stop_channel 前调 `tuner().cancel` 的打断部分（或第一阶段的 stop_channel 调 `on_autotune_aborted`）。
5. **泄漏测试只能选已停用 / 已结束的通道**（C# IsRunning 含「已启用」），照抄；触屏界面要按这个筛选。
6. **压力显示单位**：`Automation` 的 unit 与 `CoreOptions::pressure_unit` 是两个参数，第三阶段改单位时两边要一起改（或第一阶段给 ControlCore 加 getter）。
7. **`ProgramGasWaitMin = 0`**：按 C# 当作「不限」（GasQueue::timed_out / 稳压等待都是 ≤ 0 不限）；设置页允许的最小值由 store 的 SettingSpecs 决定。
8. **GCC 9.4 / arm64** 未实测。

---

## 7. 实现时发现的小问题（没改别的模块）
- `hw::MockModbusMaster` 的压力随机游走（第一阶段已报）：测试里换成按阀态积分的物理模型绕开。
- 没有发现 core / hw / store 的功能性 bug；§6-1、§6-2 是接口缺口。

---

## 8. 第三阶段（命令层 + 控制租约 + HTTP）要调什么

**组装**：见 §2.1。`AutomationDeps.unit` 与核心的压力单位一致；`library` 传进来即种内置模板。

**写命令**（公共流程：角色 → 正文 → 幂等 → 动作类别 / 租约 → 参数 → 执行 → 结果 → 审计 → WS opEvent）：每个命令在 `core.call([&]{ Admit(租约 / 本地) → 下面的方法 })` 里执行，`OpResult` 原样映射（ok → 200 / accepted → 202 / silent → 200 无提示 / fail → 码到 HTTP 状态的现有映射，`data` 作 error.detail）。

| 端点 | 调用 | 动作类别 |
|---|---|---|
| `PUT /channels/{id}/program` `{steps, name?}` | `programs().replace_program(id, steps, name)` | Control |
| `POST /channels/{id}/program/copy` `{to}` | `programs().copy_program(id, to, strict = 远程)` | Control |
| `POST /channels/{id}/program/run` | 触屏确认框打开时 `check_run(id)`；确认 / 远程 `run(id, src 本地 ? "local" : 客户端名)`（`sessionId` 校验在接口层） | Control |
| `DELETE /channels/{id}/program/run` | `programs().abort(id)` | Stop |
| （触屏程序页读草稿） | `programs().draft_json(id)`（经 `call`；C# 没有这个 GET，触屏新界面用） | 读 |
| `GET /channels/{id}/program/run` | `snapshot()->program_runs[id-1]`（null → 404「RVn 本次开机以来没有执行过程序」），包成 `ProgramRunDto(seq, ts, run)` | 读 |
| `POST /leak-tests` `{channels, testPsi?}` | `leak().start(channels, testPsi, src.Label)` | Control |
| `DELETE /leak-tests/current` | `leak().abort(strict = 远程)` | Stop |
| `POST /calib` `{channel, withWater, waterMl, redoManifold?}`（本机） | `volume_calib().start(...)` | Control（LocalOnly） |
| `DELETE /calib/current`（本机） | `volume_calib().abort()` | Stop（LocalOnly） |
| `GET /calib` | `snapshot()->calib` | 读 |
| `POST /channels/{id}/autotune` `{tempC, band?}` | `tuner().start(id, tempC, band, strict = 远程, done)` — done 里兑现 promise | Control |
| `DELETE /channels/{id}/autotune` | `tuner().cancel(id, done)` | Stop |
| 手动阀控开 / 单阀开关 | 先 `automation.gas_locked_for_manual()`，再 `core.write_valves(...)` | Control |
| 搅拌命令的守卫 | `programs().stir_step_rv()` → `STIR_PROGRAM_OWNED` | Control |
| 各种「通道被程序占用」守卫 | `programs().is_owned(id)` → `CHANNEL_PROGRAM_OWNED`（核心的 ProgramHooks 已接，快照 programOwned 也是它） | — |
| `/library/templates` | 直接 `ProgramLibrary`（内置模板 builtIn 只读）；「用模板新建 / 下发」：`builtin_templates::pc_steps_to_wire(body.program.steps)` → `replace_program` | — |

**WS**：`telemetry` 仍是 `core.snapshots().subscribe`（新字段已在帧里）；`progress`（设计文档 §5.4）用 `programs().on_step` / `on_finished` 触发推送（回调在控制线程上，只能非阻塞入队），内容取 `channels[i].program` 的新字段。

**全部停止 / 单路停止 / 退出**：不用额外调用——核心经 `ProgramHooks::abort_all / abort_channel` 和气路中止监听已接到程序、泄漏测试、容积标定；自整定经 `TempService::on_autotune_aborted`。
