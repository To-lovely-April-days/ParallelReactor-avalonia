# 模块报告：core · 程序步骤（reactor::core::program）

C# 源：`ParallelReactor-avalonia-fusion`（只读）。全部是纯逻辑：不碰 I/O、不读时钟、不开线程，「现在」和读数由调用方传入，只在控制线程用。
命名空间 `reactor::core::program` 嵌在 `reactor::core` 里，直接使用 core 模块已定稿的 `ReactorState`、`op_codes`、`GuardReject`、`PressureUnit` / `format_p`、`fmt_dec`、`StirHolder`、`HeatSegment` / `heat_curve_to_profile`、`leak_math`（没有再复制一份）。

## 1. 文件和公开接口

| 头文件（`src/core/program/`） | 公开接口 |
|---|---|
| `step_type.hpp` | `enum class StepType`（顺序照抄 C#）；`kAllStepTypes`（9 种，schema 顺序）；`is_legacy / to_code / parse_step_type / zh` |
| `params.hpp` | `ParamValue = variant<double,string>`；`Segment{temp,rate,hold}`；`Params`（保持插入顺序的键值表：`find/set/erase/contains`）；`param_num / param_str`（C# StepParams 的解析规则）；`parse_double` |
| `schema_codes.hpp` | `SchemaOption`；`schema_codes::options(key) / is_legacy_key / is_unavailable_field / code_of / label_of / kSegmentFields / kSegmentsMin/Max / kGasUnavailableReason` |
| `step_fields.hpp` | `FieldLimit`、`VisibleWhen`、`FieldDef`、`StepSpec`；`step_specs::find / of / defaults / is_visible / detail(…, unit) / curve_minutes / gas_label / gas_index / p_rate` + 常量（LeakRateDefault 2、PressDeclaredMax 493.1 …）；`DurationEstimate{minutes,approx,upper_bound}.text()`；`step_duration::of / total`；`step_templates::hydrogenation(leak_rate)` |
| `step_snapshot.hpp` | `StepSnapshot::create(...)`（深拷贝、只读）；`type / ordinal / name / detail / params / segments / num / str / is_curve` |
| `program_step.hpp` | `ProgramStep`（编辑中的步骤：`create / from_snapshot / with / set_num / set_str / segments / ensure_segments / spec / name / detail / duration / to_snapshot`）；检查器换算 `display_range / display_number / stored_from_display`；`to_heat_segments`（交给 `core::heat_curve_to_profile`） |
| `step_migration.hpp` | `MigStep{type,p,segments}`（`num / str / with`）；`MigrationResult`；`step_migration::is_legacy / migrate` |
| `program_limits.hpp` | `ProgramLimitSettings{t_max_sp, over_temp, rpm_max, p_max_sp, over_pressure}`；`PreRunResult`；`FieldRange`；`program_limits::heat_temp_max / press_max / rpm_max_allowed / field_range / clamp_field / check_step / validate / pre_run / estimate_after_heat / heat_target` + 常量 |
| `program_run_guard.hpp` | `ProgramRunInput`（纯输入结构）；`program_run_guard::check`（→ `optional<GuardReject>`）/ `reason` |
| `program_run_info.hpp` | `ProgramRunPhase`；`ProgramRunInfo`（每通道一份的运行数据）；`program_run_set::kMaxConcurrent / is_active / is_owned / running_ids / first_running / summary / phase_name` |
| `program_json.hpp` | `ProgramJsonError{code,message,detail}`；`ProgramParseResult{steps,migrated,notes,error}`；`program_json::parse(json steps, leak_rate, unit)` |
| `program_step_format.hpp` | `ProgramStepDto{type,params,name,detail}`；`json_number(double)`；`program_step_format::format / to_json` |
| `program_schema.hpp` | `program_schema::build(limits, settings_revision, leak_rate)` → `/program-schema` 的完整 JSON |
| `step_engine.hpp` | `step_engine::breaks_gas / leak_drop_rate` + 常量；`StepEvent`、`Await`、`StepInput`、`ActionKind`、`ValveCmd`、`Action`、`StepStatus`、`FaultKind`、`StepOutput`、`RunnerConfig`；`class StepRunner`：`tick / abort / step / finished / gas_held` |
| `text_format.hpp` | `fmt_n`（"0.###"，整数不带小数）、`fmt_00`、`round_even`（C# Math.Round 取偶）；其余格式化用 core 的 units / num_format |
| `program_types.hpp` | 只包含 `core/reactor_state.hpp` + `core/op_codes.hpp` |

## 2. 移植的 C# 文件

- `Models/ProgramStep.cs`（纯逻辑部分：ProgramStep、HeatSeg、StepField 的范围 / 写回；图标、颜色、绑定不搬）
- `Models/StepFields.cs`（FieldDef、StepSpec、StepSpecs 全表含帮助与摘要文案、DurationEstimate、StepDuration、StepTemplates）
- `Models/StepSnapshot.cs`（StepParams + StepSnapshot）
- `Models/ProgramLimits.cs`、`Models/ProgramRunGuard.cs`（判定顺序逐条照抄）、`Models/ProgramRunInfo.cs`
- `Models/StepEngine.cs`（改写成显式状态机，见 §4）
- `Contracts/.../StepType.cs`、`StepTypeCodes.cs`、`StepMigration.cs`、`Api/ProgramSchemaCodes.cs`
- `Api/ProgramJson.cs`、`Api/ProgramStepFormat.cs`、`Api/ProgramSchemaBuilder.cs`（+ ApiDtos 里 FieldSchemaDto / StepSchemaDto / ProgramSchemaDto / VisibleWhenDto / ProgramStepDto 的 JSON 形状）
- 参考：`doc/对外通讯接口设计.md` §6.9、`research/fusion-port/notes/STEPS-SPEC.md`

## 3. 测试

`tests/core_program/`，组名都以 `core_program` 开头，共 **113 个用例，0 失败**（`run_tests core_program`），`-Wall -Wextra` 无警告。

| 文件 | 用例 | 翻译自 |
|---|---|---|
| `test_step_specs.cpp` | 16 | StepSpecsTests（全部）、StepSnapshotTests（全部）+ ProgramStep / 显示范围 / 格式化补充 |
| `test_step_migration.cpp` | 18 | StepMigrationTests（全部）+ MigStep.Num 解析 |
| `test_program_limits.cpp` | 11 | ProgramLimitsTests（全部）+ 文案逐字检查 |
| `test_program_run_guard.cpp` | 13 | ProgramRunGuardTests + ProgramRunGuardCodeTests + ProgramConcurrencyTests 的 ProgramRunSetTests |
| `test_program_json.cpp` | 12 | ProgramJsonTests + ProgramStepFormatTests（全部）+ schema 字段形状逐字比对 |
| `test_step_engine.cpp` | 43 | StepEngineTests（全部 41 个场景；C# 的 FakeIo 改成驱动 StepRunner 的虚拟时钟循环，压力物理照抄）+ 状态机约定 2 个 |

没翻译的：`ProgramStepTests.cs`（实为 HeatCurvePlanTests）已由 core 模块在 `tests/core/test_heat_curve.cpp` 翻译；GasQueue / StirShare 测试归 core 模块；RunSelectionTests 不在本模块范围。`Snapshot_IsReadOnly` 在 C++ 里由 const 接口在编译期保证，只保留了空段断言。

## 4. 和 C# 不一样的地方

1. **StepEngine 改成显式状态机**（设计文档 §4）。C# 的每个 `await`（DelayAsync、HoldAsync、AcquireGasAsync、LineFlushAsync、RunCurveAsync）变成一个等待点：`tick` 返回 `wake_at_ms` 或 `waiting`（Gas / LineFlush / Curve）。时序（0.2 s 读压、充气脉冲 0.1–2 s、静置 1 s、保持 1 s / 升温和反应 2 s 判一次）与 C# 一致，所以 41 个 C# 场景的时间断言原样通过。提前调用 `tick` 是安全的（不会重复动作）。
2. **I/O 变成动作**：开关阀用语义命令 `ValveCmd::{AllClosed, GasToVessel(gas), VentVessel}`，由 ctl 用 `core::valve_frame` 拼整帧；取 / 还气路是 `RequestGas` / `ReleaseGas`；稳压、计量、加热、搅拌、关 SV、写结束原因都是动作。
3. **读压**：C# `ReadPressureAsync` 会立刻读一次；这里用 `StepInput::pressure_psi`，输出 `fresh_pressure = true` 表示下一次 tick 要用压力做气路判断，ctl 应在 tick 前读一次最新值。读不到 → `FaultKind::Gas`「RVn 压力读取失败」（C# 文案来自 I/O 层，这里自定）。
4. **气路安全检查 `ThrowIfUnsafe`**（租约被超压联锁取消等）不在步骤里：由 ctl 发现后调用 `StepRunner::abort()`（返回 `ReleaseGas`），程序按 C# 的「气路被超压联锁中止」收尾。
5. **GasBlockReason(needPressure)** 变成两个输入 `gas_block_vessel` / `gas_block_line`；StirShare 变成输入 `stir_holders`（= `core::StirShare::holders()`）+ 动作 `StirRequest / StirSetOn / StirRelease`。
6. **压力显示单位**：C# 是全局 `Units.Pressure`；这里所有带压力文案的函数（detail、check_step、validate、pre_run、parse、StepRunner）都带 `PressureUnit` 参数，缺省 MPa（C# 默认）。
7. **JSON**：输出用 `nlohmann::ordered_json`，键顺序与 C# 一致（参数按插入顺序，heat 先写 mode）。数值经 `json_number`：先按 15 位有效数字取整（510×0.9−0.1 = 458.90000000000003 → 458.9），整数值写成整数（"300" 而不是 nlohmann 的 "300.0"）。输入 `parse` 收 `nlohmann::json`（键按字母序遍历）：同一步里有多个错误时，报的第一条可能和 C#（按文档顺序）不同。
8. **急停**：`ProgramRunInput::estop_locked` 保留（判定顺序照抄），C++ 版没有急停，填 false 即可。
9. **`Segment`** 是步骤参数里的曲线段（带 `==`，参与 MigStep / 快照比较），与 core 的 `HeatSegment` 字段相同；交给曲线换算时用 `to_heat_segments` 转。没有合并成一个类型，因为 core 的 `HeatSegment` 没有 `==`，而迁移和测试要比较。

## 5. 需要确认的问题

1. `fresh_pressure` 的读法：ctl 是在 tick 前专门读一次 8AI（C# 行为），还是用总线最近一次轮询值（可能晚 ≤ 1 个轮询周期）？建议气路阶段专门读。
2. 稳压回路（C# `ProgramViewModel.Run.cs` 的 RegulateLoop：2 s 周期、短租约脉冲、补压事件）不在 StepEngine.cs 里，本模块没搬；需要 ctl（或 core 再补一个小状态机）实现。`StartRegulation{gas,target,refill}` 已给出参数。
3. LineFlush（吹总管）由 ctl 用 `core::purge_plan::build(gas, cycles, kFlushFillSec, kFlushVentSec)` 执行，进度文字前缀在 `Action::text`。
4. 程序库（PC 存 MPa、`programs/<id>.json`）不在本模块，HMI 侧程序只在内存里（同 C#）。如果以后要持久化，`program_step_format` + `program_json` 就是往返格式。

## 6. ctl 的程序引擎要怎么用

1. **PUT /channels/{id}/program**：`program_json::parse(steps, 设置 LeakRate, 显示单位)` → 有 error 就按 `code / message / detail` 回 4xx；否则再 `program_limits::validate(steps, 限值)` 查全局上限；`migrated` 时 warnings 带「已从旧格式转换，请检查」+ `notes`。返回的 `result.steps` 用 `program_step_format::to_json(format(steps))`。
2. **POST program/run**：填 `ProgramRunInput`（`limit_errors = validate(...)`、`needs_gas` = 程序含 leak/exchange/press/vent、`gas_block` 用 core 的 `gas_guard_check`）→ `program_run_guard::check`；打开确认与确认时各判一次。`pre_run` 的 errors 拒绝、warnings 随响应返回。
3. **执行**：每通道一个 `ProgramRunInfo` + 当前步骤的 `StepRunner(snapshot, "03/09 升温", {rv, 单位})`。控制线程定时器在 `now ≥ wake_at_ms` 或 `waiting` 的事件到来时调 `tick(StepInput)`：
   - 按顺序执行 `actions`（Status → `run.status`；Log → 事件「系统」；SetState → 通道状态；Valves → `valve_frame` 整帧 + 死区；RequestGas → `GasQueue` 排队，取得后带 `StepEvent::GasGranted` 再 tick，排队时 `run.phase = WaitingGas`；ReleaseGas → 全关阀 + `GasLease::try_end`；LineFlush / RunCurve 做完后带对应事件再 tick；RunCurve 没有有效段时回 `CurveRejected`；SetEndReason → `run.end_reason`，并在之后的 tick 里放进 `StepInput::end_reason`）。
   - `Done` → 下一步（`index++`）；`Fault` → 程序失败收尾（`fault_kind` 区分 Step / Gas）。
   - 每步开始前检查取消；中止 / 全部停止 / 超压联锁 → `runner.abort()` 的动作 + 程序收尾。
4. **收尾**（C# ExecuteAsync 的 finally，不自动放空）：停稳压 → 只释放自己的气路占用并全关阀 → 停曲线 / 逼近 → 停本路加热、关 SVn → 释放搅拌 → 停计量 → 通道转 Done → 记事件。最近一次运行保留给 `GET /channels/{id}/program/run`（步骤用 `program_step_format`，阶段名用 `program_run_set::phase_name`）。
5. **/program-schema**：`program_schema::build(限值, 设置修订号, LeakRate)`，按修订号缓存。
