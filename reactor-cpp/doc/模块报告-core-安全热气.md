# 模块报告：core · 安全 / 加热 / 气路（reactor::core）

C# 源：`ParallelReactor-avalonia-fusion/Models/*`（只读）。全部是纯逻辑：不碰 I/O、不读时钟、不开线程，「现在」由调用方传入（单调毫秒 `int64_t`），只在控制线程使用。

## 1. 文件和公开接口

| 头文件 | 公开接口（一行一个） |
|---|---|
| `src/core/num_format.hpp` | `fmt_dec(v, max_dec, min_dec)`：模仿 C# `"0.#"`/`"0.0"`/`"0.##"` 等自定义格式（15 位有效数字、远离零舍入），保证文案数值逐字一致 |
| `src/core/op_codes.hpp` | `op_codes::k*`：§11 全部错误码字符串；`GuardReject{code,message}` 守卫拒绝结果 |
| `src/core/reactor_state.hpp` | `enum class ReactorState`；`state_code()` / `parse_state_code()` 小写状态码；`state_zh(s, over_temp)`；`state_is_running()`；`state_is_active()` |
| `src/core/units.hpp` | `enum class PressureUnit`；`parse_pressure_unit()`；`pressure_label()`；`pressure_decimals()`；`psi_to_unit()` / `unit_to_psi()`；`p_value()`；`format_p()`；`mpa_to_psi()` / `psi_to_mpa()` |
| `src/core/pid_params.hpp` | `struct PidParams{max_pulse=3, p_boost=7}` |
| `src/core/value_rules.hpp` | `RuleViolation`；`value_rules::range / temp_sp / strict_temp_sp / volume / round_volume / rpm / pid / profile_seg_count / profile_segment / autotune_sp / autotune_band / band_bound` |
| `src/core/stir.hpp` | `stir_gear::kDefaultRatio / to_motor() / to_vessel()`；`StirHolder`；`class StirShare`：`request / release / holds / count / holders / last_rpm / current / clear` |
| `src/core/heat_guard.hpp` | `HeatGuardInput`；`heat_guard_check()` → `optional<GuardReject>` |
| `src/core/gas_guard.hpp` | `GasChannel`、`GasGuardInput`；`gas_guard_check()` → `optional<GuardReject>` |
| `src/core/gas_lease.hpp` | `GasOwner`、`GasAbortCause`；`class GasLease`：`begin / can_abort / try_end / clear / includes / invalidate / is_invalidated / fault_reason` + 只读属性；`class GasQueue`：`enqueue / is_head / remove / contains / waited_ms / timed_out / waiting / clear` |
| `src/core/valve_frame.hpp` | `using ValveFrame = std::array<bool,12>`；`valve_frame::all_closed / gas / vent_only / is_safe / check_apply / needs_dead_time / close_all / valve_name / change_text`；`PurgeAction`、`purge_plan::kDeadSec / gas_index / build`；`leak_math::compensate / drop_rate_per_hour` |
| `src/core/over_temp_guard.hpp` | `OverTempAction`；`over_temp::kRetryIntervalMs / kHysteresis / kEscalateAfterSends`；`class OverTempGuard`：`evaluate / on_stop_result / is_tripped / is_confirmed / in_flight / unconfirmed_sends` |
| `src/core/over_pressure_guard.hpp` | `ActuatorStep`、`class ActuatorRetry`（`reset / send_now / step / on_result / seq`）；`OverPressureActions`；`over_pressure::kRearmRatio`；`class OverPressureGuard`：`evaluate / on_valve_result / on_heat_result / valve_seq / heat_seq / valve_sends / heat_sends / is_*` |
| `src/core/alarm_lifecycle.hpp` | `alarm_lifecycle::on_cleared / on_ack` + 状态常量；模板 `AlarmIndex<T>`：`raise / count / take_active` |
| `src/core/heat_approach.hpp` | `ApproachParams`（默认值 = AppSettings 默认）、`ApproachPhase`、`ApproachEvent`；`class HeatApproach`：`start / retarget / stop / step / should_write / mark_written / sp_to_write / status_text / preheat_overshoot` |
| `src/core/temp_profile.hpp` | `ProfileSegment`；`class TempProfile`：`segments / seg_count / start / stop / current_target / should_write / mark_written / elapsed_min / total_minutes / is_complete / enabled / active_segment / status_text` |
| `src/core/heat_curve_segments.hpp` | `HeatSegment`；`heat_curve_to_profile(segs, start_temp, max=16)` |
| `src/core/gas_uptake.hpp` | `gas_uptake::gas_volume_ml / gas_mmol / resolution_mmol / channel_gas_volume_ml / volume_basis_text` + 常量；`class GasUptakeMeter`：`start / stop / reset / sample` + 读数属性 |
| `src/core/volume_calib.hpp` | `volume_calib::Fit / FitResult / TwoRunResult / OneRunResult`；`fit_steps / solve_two_runs / solve_one_run` + 常量 |
| `src/core/leak_fit.hpp` | `LineFit`；`leak_fit::fit / fit_per_hour / psig_to_abs_kpa / corrected_abs_kpa` + 常量 |

每个 `.hpp` 都有同名 `.cpp`（`alarm_lifecycle` 模板在头里、`pid_params`/`op_codes` 只有头）。

## 2. 移植了哪些 C# 文件

HeatGuard、GasGuard、GasLease、GasQueue、GasSequence（ValveFrame、PurgeAction、PurgePlan、LeakMath）、StirShare、StirGear、OverTempGuard、OverPressureGuard（含 ActuatorRetry）、AlarmLifecycle（含 AlarmIndex）、HeatApproach、TempProfile（含 ProfileSegment）、HeatCurvePlan、GasUptake、VolumeCalib、LeakFit、ValueRules、Units、PidParams、ReactorState。

另外从 ViewModel 抽出了纯判定部分：
- `MainViewModel.Gas.cs ApplyValvesAsync` 的下发前检查 → `valve_frame::check_apply`（不安全帧 / 越权 SV，文案逐字）；
- `CloseAllValvesAsync` 的「超压中止期间排空保持」→ `valve_frame::close_all(keep_vent, vent_on)`；
- `MainViewModel.Logging.cs ValveName / LogValveFrame` → `valve_name / change_text`；
- `MainViewModel.Uptake.cs GasVolumeMl / VolumeBasis` → `channel_gas_volume_ml / volume_basis_text`；
- `Contracts/.../ChannelStates.cs` 状态码 → `state_code / parse_state_code`；`OpCodes.cs` → `op_codes.hpp`。

## 3. 测试

`tests/core/` 下 11 个文件，组名都以 `core_` 开头：
`test_alarm_lifecycle / test_stir / test_gas_lease / test_guards / test_over_temp / test_over_pressure / test_gas_uptake / test_leak_fit / test_volume_calib / test_value_rules / test_heat_curve / test_valve_frame`。

逐条翻译的 C# 用例：AlarmLifecycleTests、GasGuardTests、GasLeaseTests、HeatGuardTests、StirGearTests、OverTempGuardTests、OverPressureGuardTests、GasUptakeTests、LeakFitTests（数学部分）、VolumeCalibTests、ValueRulesTests、ProgramStepTests 里的 HeatCurvePlanTests、ProgramConcurrencyTests 里的 GasQueueTests / StirShareTests / GasLeasePerChannelTests。

没翻译的（及原因）：
- 急停相关断言（HeatGuard / GasGuard 的 `EstopActive` 文案和「急停优先」顺序）：急停已删除；
- `StirGearTests` 里 `new AppSettings().StirGearRatio`：设置归设置模块；
- `LeakFitTests.Summary_OnlyUsesMeasureWindow` 与 Compensation 里的 LeakRecord 汇总 / CSV：归存储模块（数学部分已翻译）；
- 随机数用例（GasUptake 噪声、LeakFit、VolumeCalib 噪声）把 C# `Random(seed)` 换成 `std::mt19937(seed)`：数值不同，但断言的性质不变。

补充的用例（C# 没有单测）：TempProfile、HeatApproach、ValveFrame / PurgePlan / LeakMath / check_apply / needs_dead_time、fmt_dec、Units、ReactorState、autotune_band、VolumeCalib 文案、选气相体积。

测试数量：**136 个用例（`run_tests core_`），0 失败**；单独用 `-Wall -Wextra` 编译 src/core 与 tests/core，没有警告。

## 4. 和 C# 不一样的地方（及原因）

1. **急停删除**：`HeatGuardInput` / `GasGuardInput` 没有 `EstopLocked`，也就没有 `ESTOP_ACTIVE` 分支（设计文档 §6）。`ESTOP_ACTIVE` 字符串只留在 `op_codes.hpp` 里，给接口层兼容旧客户端用。`kEscalateAfterSends` 保留；C# 升级报警文案里的「请使用急停」要由控制层改成「点主页的全部停止」（见 §6）。
2. **时间**：C# 的 `DateTime` / `TimeSpan` / Stopwatch 秒全部改成调用方传入的 `int64_t` 毫秒（建议传 `mono_ms()`）。C# 的 `DateTime.MinValue`（「从未下发」）换成哨兵 `over_temp::kNeverMs`，所以时钟从 0 开始时也能立即下发。`TempProfile` 原来直接读 `DateTime.UtcNow`，现在 `start / current_target / should_write / mark_written / elapsed_min / is_complete` 都要带 `now_ms`。`GasUptakeMeter` 的 `t` 从秒改成毫秒。
3. **压力显示单位**：C# `Units.Pressure` 是全局静态状态（带 Changed 事件）。core 不放全局状态，单位作为参数传入（`HeatGuardInput::pressure_unit`、`format_p(psi, unit)`）。
4. **界面属性去掉**：`ObservableObject` 通知、`ProfileSegment.Enabled/Active` 改成 `TempProfile::enabled(i)` / `active_segment()` 查询；`HeatApproach.StatusText` 改成 `status_text()`。
5. **不用异常**：`GasLease.Begin(None, …)` 在 C# 抛 ArgumentException，这里改成不改状态、返回 0。`check_apply` 返回拒绝原因字符串（C# 是抛 `GasFaultException`）。`GasFaultException` 本身不移植。
6. **舍入**：`StirGear.ToMotor` 用 `std::nearbyint`（等同 C# `Math.Round` 的「.5 取偶」），`round_volume` 用 `std::round`（等同 `AwayFromZero`）。`fmt_dec` 在结果为 0 时不带负号（.NET 可能输出 "-0"，属于极端情况）。
7. **新增 `valve_frame::needs_dead_time`**：C# 没有这条规则，是各序列在切换处手工插入 `AllClosed + DeadSec`。这里把规则写成判定（旧帧开着某个气源 / 排空，新帧要开一个旧帧没开的 → 先全关一拍），让控制层的阀门下发统一执行，不再依赖每个序列自己记得插。`PurgePlan.build` 生成的帧序列满足这条规则（有单测）。

## 5. 需要确认的问题

1. **需要别的模块确认**：`needs_dead_time` 只给出判定，死区时长（C# `PurgePlan.DeadSec = 0.5 s`，程序引擎里部分地方用 1.0 s）和插帧由谁执行，要等 ctl 定。建议放在 ctl 统一的「阀门整帧下发」入口里。
2. **新规则还没写**：升温超时（HeatTimeout）、压力偏离（PressDeviation）是设计文档 §6 新加的三级警告，C# 里没有判定代码，不在本次移植范围内，本模块**没有**实现。需要的话可以照 OverTempGuard 的写法在 core 里补一个小判定类。
3. **需要别的模块确认**：`op_codes.hpp` 放在 `reactor::core` 下，cmd / api 层是否直接用它，还是各自再定义一份？建议共用这一份。
5. **和程序模块有重复定义，需要合并**：`src/core/program/` 里另有 `ReactorState`、`op_codes`、`ProfileSegment`、`StirHolder` 和 `heat_curve_plan`（`reactor::core::program` 命名空间）。命名空间不同，链接不冲突，但同一概念有两份。建议以后让 program 直接用 `core/reactor_state.hpp`、`core/op_codes.hpp`、`core/stir.hpp`、`core/heat_curve_segments.hpp`。为了避免 `libreactor.a` 里出现两个同名的 `heat_curve_plan.o`（`ar` 可能用后一个覆盖前一个），本模块的文件命名为 `heat_curve_segments.*`。
4. **需要设置模块提供**：`ApproachParams` 的默认值是照 AppSettings 默认值抄在结构体里的，运行时应该由设置模块提供的值覆盖。

## 6. 控制层（ctl）要接的东西

- **每个控制周期，对所有 8 个通道（不论状态）**：
  - `OverTempGuard::evaluate(i, pv, temp在线&&通道在线, OverTemp, now, 回读At==4 / 模式未知时nullopt)`。返回 TripSend / RetrySend / Reopened → **先**下发 At=4，结果回来调 `on_stop_result`。置报警、记事件、超温空闲豁免（`_otIdleAlarm`）、「仍超温·重新置报警」照 C# `MainViewModel.CheckOverTemp` 写。
  - `OverPressureGuard::evaluate(...)`：`send_valves` → 登记到本周期末**合并一帧**：只关三路气源 + 触发通道的 SV，不碰排空和其他 SV。下发时记下 `valve_seq`，回来调 `on_valve_result(i, ok, seq)`。`send_heat` → 立即 At=4，带 `heat_seq`。`trip` 时调用 `abort_all_gas(GasAbortCause::OverPressure)`，并置「超压中止保持排空」，此后收尾用 `close_all(true, vent_on)`。照 C# `CheckOverPressure / FlushOverPressureValves`。
  - 连续 `kEscalateAfterSends` 次未确认 → 升级报警。文案把「急停」改成「全部停止 / 到现场断电」。
- **报警**：`AlarmIndex` 的键用 `ot:N / op:N / opv:N / oph:N / comm:k`；条目状态流转用 `alarm_lifecycle`。
- **启动加热前** `heat_guard_check`；**气路序列 / 手动阀控前** `gas_guard_check`。输入快照（idle_over_temp_alarm、program_owned_ids、program_gas_pending 等）由 ctl 填。
- **每次写阀前**先 `valve_frame::check_apply(frame, lease.busy(), lease.participants(), lease.task())`，再按 `needs_dead_time` 插全关帧。写成功后用 `change_text` 记「阀门整帧下发」事件。长时间保持期间每秒重写当前帧（IO16R 心跳，C# `HoldAsync`）。
- **气路序列执行中**周期检查：`lease.fault_reason(state_of)`，再加 C# `ThrowIfGasUnsafe` 里的报警 / 监视通道超压检查（这部分还在 VM 里，需要 ctl 写）。
- **程序气路**：`GasQueue` 用 `mono_ms`，超时上限 = 设置 `ProgramGasWaitMin`（默认 60 min）× 60000。搅拌用 `StirShare`，释放后按 `current()` 恢复转速。
- **加热**：每周期 `HeatApproach::step` / `TempProfile::current_target`，再按 `should_write` 下发、`mark_written` 确认；写在途时置 `write_in_flight`。
- **吸气量**：每周期对计量中的通道调 `GasUptakeMeter::sample(mono_ms, T&P有效, P, T, channel_gas_volume_ml(...), SV开, svOpenGen)`。`svOpenGen` 是「该通道 SV 被打开的累计次数」，要由 hw 的阀门驱动提供（C# `ValveService.GetSvOpenGen`）。
