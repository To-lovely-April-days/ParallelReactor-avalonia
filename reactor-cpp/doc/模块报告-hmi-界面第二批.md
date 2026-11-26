# 模块报告 · reactor-hmi 触屏界面第二批（2026-11-26）

范围：`apps/reactor-hmi/`、`tools/preview/Makefile`。没动 `src/`、`tests/`、`tools/build`、`CMakeLists.txt`、`config/`，也没碰 git。
界面进程仍然不链接后端代码；程序参数区按 `/program-schema` 的 JSON 画（样例 JSON 由后端自己的代码生成，见 §4.1）。

## 1. 怎么编、怎么看

```bash
make -C tools/preview -j8
tools/preview/out/reactor-hmi-shot --help                 # 列出全部页面名
tools/preview/out/reactor-hmi-shot --page prog-run --shot x.bmp
make -C tools/preview shots                               # 全部页面 → out/shots/*.png（这批已拷到 doc/hmi-shots/）
```

- 预览 Makefile 只加了一处：`INC` 里加 `-I$(ROOT)/third_party`（`model/program.cpp` 用 nlohmann 解析 schema / 程序 JSON）。CMake 的 `reactor-hmi` 目标本来就带 `third_party`，不用改。
- 新文件都在 `apps/reactor-hmi/**` 下，CMake `GLOB_RECURSE` 自动收进来。
- 编译：预览 `-Wall -Wextra` 无警告；另外用 `-Wall -Wextra -Wpedantic` 单独过了新文件的语法检查，也无警告。本机只有 Apple clang，GCC 9.4 没试（没用 C++20 特性、没用 designated initializer）。
- 截图用的设计稿对照图：WKWebView 把 `*.dc.html` 渲染成 1280×720 PNG（工具在 scratchpad，不进工程）。

## 2. 页面（截图都在 `doc/hmi-shots/`，1280×800）

| 画布 | 页面名 / 截图 | 说明 |
|---|---|---|
| Prog-1 编辑程序 | `prog-1` | 页签（编辑程序 / 程序库 / 泄漏测试）+ 8 路通道条 + 程序卡（名字、步数、预计时长、「已改动未保存」；从程序库选… / 存到程序库 / 用到其他通道…；步骤流 + 添加；右栏「下发并运行 · 只运行 N 号」）+ 参数卡 + 预计曲线（选中步浅蓝底） |
| Prog-Params（9 种参数区） | `prog-param-leak/exchange/stir/heat/press/hold/react/vent/end` | **按 schema 画**：num → 数值框（标题旁小字给范围 / 提示），sel → 带灯选项，on/off 两选项 → 开关。visibleWhen 不满足时：由开关控制的、或只在非默认选项下有用的（最长等待 / 超时）变淡，属于另一选项的目标值隐藏（见 §6.3）。前移 / 后移 / 删除，「?」弹出 schema 的 help 全文；结束步不能动 |
| Prog-AddStep | `prog-addstep` | 四组（准备 / 加热和搅拌 / 加气和反应 / 收尾），已有「结束」时结束变灰；插在选中步后、结束前 |
| Prog-Curve8 / 16 | `prog-curve8`、`prog-curve16` | 升温方式选「分段曲线」时参数卡变高：左边方式 / 到位范围 / 最长等待 / 超时，右边曲线（线色按温度由蓝到橙，选中段浅蓝底 + 白芯橙点 + 深色提示框）和三行表格（目标温度 / 升温速率 / 到了保温，点格子弹数字键盘），不到 16 段时有「加一段」 |
| Lib-Mine / Lib-Tpl | `lib-mine`、`lib-tpl` | 左：「我的程序 N / 内置模板 N」两列卡片（约时长、步数、正用在的通道号）+ 新建空白程序；右：名字 + 标签、修改时间、正用在、说明、步骤流、预计温度曲线；载入到 N 号 / 载入到多个通道… / 复制 / 改名 / 删除（模板：另存为我的程序） |
| Prog-Run | `prog-run` | 步骤节点（做完绿勾 + 用时、当前步蓝底 + 进度环、后面「约 HH:MM」），左边这一步的读数（升温：现在 / 目标 / 进度条 / 升温速率 / 预计到温；充压、反应、排队等气路各有一套），下一步提示；右边整次运行三块图（温度 / 压力 / 吸气量，实际实线、计划虚线、当前步浅蓝底、「现在」橙线、反应前吸气量斜纹「反应开始后计量」） |
| Prog-AbortAsk | `prog-abort-ask` | 沿用第一批的 `App::abort_confirm` |
| Prog-Done | `prog-done` | 全绿节点 + 用时、这一次的结果（吸气量、反应用时、最快吸气、温度最大偏差）、冷却提示、整次运行三块图；看运行记录 / 再跑一次 |
| Prog-Aborted | `prog-aborted` | 中止步红叉、后面虚线「没有执行」；系统已经做了；后面灰底 + 红竖线；看运行记录 / 用同样设置再跑一次 |
| Leak-Setup | `leak-setup` | 8 格选通道（运行中 / 停用不能选）、选全部空闲 / 清空、上次泄漏测试（小日历）、怎么测（参数取自设置 leakTest）、开始测试 · N 路 |
| Leak-Run | `leak-run` | 箭头流程（做完绿 / 当前蓝 / 后面灰）、还要 N min、进度环中止钮（先确认）、测量阶段压降曲线（阈值红虚线、「现在」、通过的几路合一个标签）、每一路压力 / 压降 / 进度条（红竖线 = 阈值） |
| Leak-Result / Leak-Pass | `leak-result`、`leak-pass` | 头部大圈（不通过红叉 / 通过「4/4 通过」）、各路结果 2×2、30 min 曲线；不通过：排查顺序 5 条、只重测 N 号 / 重新选择通道；通过：下一次（日历）、最近几次、再测一次 / 去编辑程序 |
| Curve2-Gas / Temp / Press / Three | `curve-gas`、`curve-temp`、`curve-press`、`curve-three` | 自绘曲线（§3.1）：工具条（自适应、导出到 U 盘、通道开关、横轴 时钟 / 运行时间、纵轴 全部 / 设定值附近），「现在」之后浅灰蓝底，读数竖虚线 + 线上的点 + 底部时间气泡，读数卡片（三列，当前页签那列加粗）；气体页左上信息条（显示中的路数 · 平均吸气速率 · 各路偏差 RSD），温度 / 压力「设定值附近」绿带 + 中线 + ±说明 + 「N 路都在 80 ± 2 °C 内 · 已 x min」；三项对照是三块图共用时间轴（**没有双纵轴**）。点 / 拖图移动读数线 |
| Rec-List / Rec-Abort | `rec-list`、`rec-abort` | 左：筛选（全部 / 完成 / 有警告 / 中止，带数）+ 按天分组的时间线列表（运行中写「在主页看」）；右：名字 + 标签、写备注、说明行、四个指标块 / 中止时的超压说明块（量值、设定、上限、量程条）、各路吸气量（开始充反应气虚线、中止红线 + 签）、每一路表（出问题那路红底）、在曲线页打开 / 导出到 U 盘 / 存进程序库 / 用同样设置再跑一次 |
| Rec-Export | `rec-export` | 蓝色标题条弹窗：四个选项方块（CSV / PDF / JSONL / JSON 文件图标）、哪几路、存到哪 |
| Rec-Files | `rec-files` | 月历（有数据的日子白底 + 数据量条，今天橙框，选中蓝底，翻月）+ 这一天：跑过的程序（看记录 ›）、文件（文件图标、读数 / 事件标签、说明、时间范围、大小、导出）、按时间段导出… / 这一天全部导出 |
| Rec-RangeExport | `rec-range-export` | 从 / 到、快捷（最近 1 小时 / 这一天 / 按一次运行…）、当天时间轴（跑过的程序色块 + 选中段框）、导出什么、读数间隔、存到哪 |
| Set-General … Set-About | `set-general`、`set-params`、`set-pid`、`set-calib`、`set-channels`、`set-net`、`set-net-pw`、`set-comm`、`set-data`、`set-user`、`set-about`、`set-quit` | 左菜单 10 项 + 退出程序（先确认）。PID 是 F 方案（整定那一行浅黄底、进度条 + 斜纹 + 约剩、取消整定）；通讯与模块是 C 方案（离线模块红图标、「离线 17 min」、连续失败、实心「重新连接」）；WiFi 密码用文字键盘的密码模式（●●●、显示、连接） |
| 全部停止（改） | `stop-all` | 按决定记录加了「同时停搅拌」开关，默认关；打开后提示行改成「搅拌也停」，`stop_all(true)` |

## 3. kit 新增 / 扩展（`ui/kit/`）

### 3.1 曲线图 `chart.hpp`：`TimeChart`
- `create(parent, x, y, w, h)`、`set(ChartConfig, std::vector<ChartPanel>)`、`panel_area(i)`、`x_to_px(x)`、`on_press(x)`（点 / 拖）。
- 一个控件里竖着叠几块图（`ChartPanel::weight` 分高度），共用一条横轴；横轴是任意 double（毫秒时间戳、分钟、段号都行），刻度文字由 `x_label` 回调给出。
- 每块：`ymin/ymax/yticks/ydec/ysuffix`、左上角标题（带框 / 不带框）、顶上单位、设定值带（`band` + `band_center` + ±文字）、斜纹区 + 文字、多条 `ChartSeries`。
- `ChartSeries`：等间隔（`x0 + i·dx`）或不等间隔（`xs`），NaN 断开；线宽、虚线、透明度、线下填充、末端点 + 文字、「线色按温度」（升温曲线）。太密时按像素保留最小 / 最大值抽稀，噪声的样子不丢。
- `ChartConfig`：「现在」竖线、之后浅灰蓝底 / 灰底（已中止）、当前步浅蓝底、步骤分界竖线、读数竖虚线 + 点 + 底部时间气泡、标记竖虚线（「开始充反应气」）、中止红线 + 签、四边留白、网格开关。
- 全部用 LVGL draw API（`lv_draw_line / rect / triangle / label`）在 `DRAW_MAIN` 里画；曲线按块裁剪（设置 `layer->_clip_area`，9.2 文档允许在加任务前设）。
- 工具函数：`nice_ticks()`、`clock_ticks()`。

### 3.2 其他
| 控件 | API |
|---|---|
| 药丸 | `pill(parent, text, fg, bg, px, icon, dot, weight)` |
| 通道小块 | `chan_chip(parent, x, y, ch, on, disabled, w, h)`（小釜 + 号码；曲线页、导出、多通道载入共用） |
| 进度条 | `bar(parent, x, y, w, h, frac, color, marker)`（可带阈值红竖线） |
| 步骤图标 | `step_icon(type, 16/20/28)`（9 种，SVG 摘自画布） |
| 关闭钮 / 工具按钮 | `close_btn()`、`tool_btn(parent, x, y, text, icon, color, h, px, ring)` |
| 小日历 | `cal_badge(parent, x, y, month, day, color, w, h)` |
| 选项方块 | `LampTile`（灯 + 文件图标 + 两行字，Rec-Export） |
| toast | `toast(text, ok)`：屏幕下方一条，3 s 消失（导出、存库、连接 WiFi 等用；`Actions::on_result` 以后也接这里） |
| 自绘 | `draw_rect()`、`draw_hatch()`；`draw_text()` 改成拷贝文字（`text_local = 1`，绘制是延后执行的，原来传临时字符串会乱码） |
| 带灯选项 | `LampGroup::set_faded()`、`width()`、`allow_reselect`（单个开关用） |
| 开关 | `Switch::create(..., compact)`：设置页的 52×30 小开关（没有字） |
| 数值框 | `NumField::set_text()`（「85 / 38」「±0.2」）、`set_faded()`、`set_align_right()`（设置页的右对齐大数字）、`spec()` |
| 文字键盘 | `TextKbd::open(title, init, max, TextKbdOpts{password, ok_text, hint}, cb)`：密码模式（●●●、显示、不出拼音、默认英文、确定键写「连接」） |
| 弹窗 | `DialogSpec::icon = nullptr` 时标题从左边 20 起（蓝色标题条的导出、载入弹窗） |

**图标**：`assets/icons_manifest.py` 末尾追加了第二批的 70 多个图标（步骤 9 种 × 3 尺寸、页签、菜单、文件、WiFi、锁、眼睛、插头 / 断开、U 盘、日历钟、二维码 126×126 等），`python3 assets/svg2lvgl.py` 重新生成 `ui/kit/icons_gen.{hpp,cpp}`（已提交为源文件）。

## 4. model / actions 新增（后端要提供的东西）

### 4.1 程序 schema 和程序 JSON（`model/program.{hpp,cpp}`）
- `ProgramSchema / StepSchema / FieldSchema / SchemaOption / VisibleWhen`：字段名照 C# `ProgramSchemaDto`（`apiVersion, settingsRevision, stepTypes[], steps[{type,label,chip,help,fields[{key,label,kind,unit,default,min,max,declaredMin,declaredMax,options[{code,label,available,reason}],available,reason,itemFields,minItems,maxItems,visibleWhen{key,in[]},defaultCode}]}]`）。`parse_program_schema(json)` 解析 **GET /program-schema** 原文。
- `ProgramDoc / StepDoc / ParamVal / HeatSeg`：§6.9 程序 JSON（每步 `{type, params{…}}`，选项英文码，**压力 psi**，`heat.mode=curve` 时 `params.segments[{temp,rate,hold}]`）+ 程序库条目的 `id, name, model, modified, by, draft, revision, note, builtIn`。`program_to_json / program_from_json`。
- 显示用的纯函数：步骤流两行字、短说明、时长估算（逐字照 `core/program/step_fields.cpp` 的 `step_duration::of`）、预计曲线拐点、单位换算（psi ↔ MPa、分钟 → min、秒 → s）。
- 模拟源里的 schema 样例：`model/sim_schema_gen.cpp`（生成文件），由 `assets/schema_sample.py` 编译并运行 `src/core/program/program_schema.cpp` 得到（出厂设置限值、leakRate 2 psi/h、revision 1）。后端 schema 改了重跑这个脚本即可。

### 4.2 `Model::x`（`model/ext2.hpp`，`PagesExt`）
| 字段 | 内容 | 由谁提供（建议端点） |
|---|---|---|
| `schema` | 上面的 schema | **GET /program-schema**（已有；`settingsRevision` 变了重新取） |
| `programs[8]`、`programSrcId[8]` | 每一路当前的程序（运行中的是下发时的副本）、来自程序库哪一条 | **GET /channels/{id}/program**（要能返回 §6.9 格式 + 新增 `sourceId`） |
| `library[]`、`templates[]`、`libraryMax` | 程序库 / 模板（`LibEntry{prog, usedBy}`） | **GET /library/programs、/library/templates**（§5.4；`usedBy` 可以界面自己算，后端给 `sourceId` 就行） |
| `trend` | `Trend{t0, stepMs, temp[8][], press[8][](MPa), gas[8][]}` | **新：GET /trend?from&to&stepMs**（取自遥测 CSV + 内存环；NaN/null = 没数据）。界面曲线页看最近 60 min，运行中页看整次运行 |
| `runs[]` | `RunRecord`：`id, programName, result(running/done/warn/abort/leak), resultText, start, end, plannedSec, channels[], endReason, avgMmol, rsdPct, tempDevC, pressMPa, gasStart, perCh[{ch,mmol,maxRate,tempC,flagged}], abort{ch,quantity,value,setpoint,limit,rangeHi,at,title,did}, note, trend(每分钟), program, stepMinutes[]` | **新：GET /runs、GET /runs/{id}（含 trend）、PUT /runs/{id}/note**。数据来源：SessionManifest.programRuns + 事件 + 遥测 CSV 汇总；`resultText` 这种文案建议后端给码、界面翻译 |
| `days[]` | `DayInfo{day, bytes, files[HistoryFileInfo{name,type,bytes,modifiedAt,from,to,day,desc,lines}], runs[]}` | **GET /files?type=telemetry|events**（C# `HistoryFileInfo` 已有 name/type/bytes/modifiedAt/from/to/day）；新增 `desc`（另起文件的原因，比如「15:42 改了压力单位」）和 events 的 `lines`；`runs` 可由 /runs 按日期分组 |
| `usb` | `present, label, freeBytes, lastExport` | **GET /usb**（§5.4） |
| `leak` | `selectable[]`、`samples[{min, dpKPa[8]}]`、`history[{at, channels, pass, note}]` | 状态仍用遥测 `leakTest`（C# `LeakSnap/LeakChannelSnap` 已有）；**新：GET /leak/current/samples**（或 WS leak 帧带逐点 Δp）；历史用 **GET /leak-tests** 再加汇总字段 `channels / pass` |
| `leakSettings`、`approach`、`stir`、`volume` | C# `LeakTestSettingsDto / ApproachSettingsDto / StirSettingsDto / VolumeSettingsDto`（字段名照抄） | **GET /settings**（已有） |
| `prefs` | `pressureUnit, tempUnit, theme, preflightChecklist, keyboard, gasWaitMaxMin, retentionDays` | **新设置键**，走 **PATCH /settings**（`ui.*`） |
| `pid` | `bands[{index,name,loC,hiC}]`、`channels[8]{id,autotuning,tuneBand,tuneSpC,bands[{index,P,I,D,tunedSpC,tunedAt,manualEditedAt}]}` + 新增 `tuneCycle, tuneCycles, tuneLeftSec, tuneFraction`、`controller` | **GET /pid**（C# `PidDto` 已有）；整定进度 4 个字段要新增 |
| `calib` | `readings[{n, manifoldFrom, manifoldTo, vesselFrom, vesselTo}]`、`pass/passes`、`cycle/cycles`、`stepMinutes[4]`、`vesselTempC`、`gas` | 状态用遥测 `calib`（C# `CalibSnap`）；逐次读数 **GET /calib**（§5.4） |
| `io` | 三路气源 / 排空线圈、SV1–8 线圈、8AI 通道、模块型号 | **GET /device**（hardware.json 的接线，新增） |
| `comm` | `modules[{id,name,model,detail,configured,online,port,baud,polls,fails,lastOkAt,offlineSince,failStreak}]`、`pollSec`、`offlineAfter` | **GET /device/comm**（C# `CommDto/ModuleCommSnap` 已有 polls/fails/lastOkAt）；新增 `port/baud`（`HardwareDto` 有）、`offlineSince/failStreak`、型号文字 |
| `net` | `wifiOn, connectedSsid, ip, gateway, hostname, strength, autoConnect, nearby[{ssid,secured,strength,saved}], ethernetPlugged, ethernetIp, dhcp` | **GET /network**（§5.4） |
| `storage` | 磁盘总量 / 可用 / 系统 / 记录、两个路径、今天 CSV / 事件大小、泄漏次数 | **新：GET /storage** |
| `about`、`users` | 型号、版本、序列号；`mode`、`role` | **GET /device**（`DeviceInfoDto` 已有 model / hmiVersion / bootTime）、**GET /users** |

另外 `ChannelSnap.heatOutputPct`（PID 页「输出」）、`ChannelSnap.autotuning`、`StirSnap.online` 用的是遥测已有字段。

### 4.3 `Actions` 新方法（`model/actions.hpp`，每个注释里写了端点）
| 方法 | 端点 |
|---|---|
| `put_program(ch, ProgramDoc)` | PUT /channels/{id}/program（§6.9 JSON，压力 psi） |
| `start_program(ch)` | POST /channels/{id}/program/start（「下发并运行」= 先 PUT 再 start） |
| `lib_save(tpl, doc)` | id 空 POST /library/{programs\|templates}；否则 PUT …/{id}，带 `revision` |
| `lib_delete(tpl, id, revision)` | DELETE /library/…/{id} |
| `lib_import_usb()` | POST /library/import |
| `leak_start(chs)`、`leak_abort()` | POST /leak-test/start {channels}、POST /leak-test/abort |
| `run_note(id, note)` | PUT /runs/{id}/note |
| `export_usb(ExportReq{runId \| file \| day \| from/to, kinds[], channels[], stepSec})` | POST /export |
| `patch_setting(key, double)`、`patch_setting_str(key, str)` | PATCH /settings {key: v, revision}；key 是 JSON 路径：`alarms.overTempC / overPressurePsi / pressDeviationPsi / heatTimeoutMin / stopHeatOnOverPressure`、`limits.tempSpMaxC / pressureSpMaxPsi / rpmMax / volumeMaxMl`、`leakTest.testPsi / settleMin / measureMin / rateLimitPsiPerHr / reminderDays`、`approach.deltaC / fastRateCPerMin / rateCPerMin / settleBandC / settleSec`、`stir.peakCurrentA / standbyPct`、`ui.pressureUnit / tempUnit / theme / keyboard / preflightChecklist / gasWaitMaxMin / retentionDays`（压力一律 psi） |
| `set_pid(ch, band, P, I, D)` | PUT /channels/{id}/pid/{band} |
| `set_pid_bands(lo, hi)` | PUT /pid/bands |
| `autotune(ch, band, spC)`、`autotune_cancel(ch)` | POST /channels/{id}/autotune {band, spC}、POST …/autotune/cancel |
| `calib_start(ch, waterMl)`、`calib_continue()`、`calib_abort()` | POST /calib、POST /calib/continue、DELETE /calib/current |
| `wifi_enable / wifi_scan / wifi_connect(ssid, pw) / wifi_forget / wifi_disconnect / set_ip_mode(dhcp)` | PUT /network/wifi、POST /network/wifi/scan、POST /network/wifi/connect、DELETE /network/wifi/{ssid}、POST /network/wifi/disconnect、PUT /network/ip |
| `comm_test(module)`、`comm_reconnect(module)` | POST /device/comm/{module}/test、…/reconnect |
| `quit_app()` | 只退出触屏界面（本地动作，不发请求也行） |
| `stop_all(include_stir)`（第一批已有） | 现在弹窗里有「同时停搅拌」开关，默认 false |

`LoggingActions` 全部打印（WiFi 密码只打印长度），再转给 `SimSource`。

### 4.4 模拟源
- 三份程序（加氢筛选-A / B / C）由 schema 默认值按 v0.8 九步模板生成，第一批的通道场景（1 号充压、2 号反应、3 号升温、4 号升温到位后平衡、5 号未下发、6 号排队等气路）改成按这三份程序走，步骤名、时长都由程序算。
- 趋势：今天 12:00 起每 10 s 一点，按每一路的计划生成（一阶跟随 + 固定种子噪声，置换是一串充放脉冲），最后 20 min 往快照读数靠拢，所以曲线末端和装置总览的数字一致；之后每 10 s 追加一点。
- 运行记录 9 条（3 条运行中 + 今天的气密 + 昨天完成 / 超压中止 + 前天吸气量验证 / 惰性加热 + 11-21 曲线升温），每条带每分钟的吸气量曲线；每日文件 40 天（周末没开机），11-25 有「另起一个文件」的例子。
- 预览注入：`inject_prog_done / inject_prog_aborted / inject_leak("run"|"fail"|"pass") / inject_heat_curve / inject_stir_offline`。
- 所有新 Actions 都会改模拟状态（下发、开始程序、存库、删库、泄漏测试开始 / 中止、设置、PID、标定、WiFi、重连）。

## 5. 页面结构 / 状态
- 新页面文件：`ui/pages/program_page.cpp`（程序页本体、通道条、编辑、参数区、添加步骤、曲线编辑）、`program_run.cpp`、`program_lib.cpp`、`leak_page.cpp`、`curve_page.cpp`、`records_page.cpp`、`settings_page.cpp`，共用头 `common2.hpp`。
- 路由（`App::go`）：`Program` 的 arg = `页签 × 100 + 通道`（0 编辑 / 1 程序库 / 2 泄漏测试）；`Curve` arg = 页签；`Records` arg = 页签；`Settings` arg = 分类。
- 编辑中的程序是界面里的副本（`ProgEdit`，换页不丢）；「存到程序库」「下发并运行」「用到其他通道」以后清掉改动标记，没改动的路跟着后端的 `programs[]` 走。
- 刷新：每页按「签名」决定要不要重建（页签、选中项、数据条数、版本号；运行中 / 泄漏测试每秒、曲线页每 5 s）。整页重建在板子上的开销还没测（§7）。
- 预览批量截图时 `reset_page_state()` 清掉各页记住的状态。

## 6. 和画布还对不上的地方
1. **场景数据**：画布各页的数字是各自编的（曲线页 6 路都在反应、PID 页 3 号整定、标定页 5 号），这里统一按第一批的装置总览场景出数据，所以曲线页只有 2、4 号有吸气量，温度带里「6 路里 3 路在 80 ± 2 °C 内」；PID 和标定照画布放在 3 号 / 5 号（和 3 号正在跑程序、5 号手动的场景在现实里会冲突，只是演示）。
2. **Prog-1 预计曲线**：画布把压力虚线和温度画在同一块里、只标温度刻度；这里照画布做了（压力按比例缩放、没有自己的刻度）。如果要严格「不做双纵轴」，改成两小块叠放即可（`TimeChart` 直接支持）。
3. **参数区的显示规则**：schema 只有 visibleWhen，没有「变淡还是隐藏」。这里的规则：控制字段是开 / 关 → 变淡；`in` 里有 4 个以上的码（最长等待 / 超时）→ 变淡；其余隐藏（保持步的目标温度 / 压力、升温定值的几项）。和 Prog-Params 板一致。字段标题旁的小字（「≤ 2.340」「0 = 尽快」「超时报警」「设置里的阈值」）是界面里的一张小表；画布上有的分隔竖线没做。
4. **选项短名**：灯按钮上把「停止这一路」写成「停这一路」、「惰性气体」写成「惰性」、升温方式「曲线」写成「分段曲线」（界面映射，不改 schema）。
5. **曲线编辑器**：表格只有加一段，没有删一段（画布没画）；曲线是示意（每段 55% 斜坡 + 平台，按列对齐表格），准数看表格。
6. **跑完 / 已中止**：右上多了一个「改程序」按钮（画布没有；不加的话跑完的路回不到编辑页）。
7. **程序库**：「正用在」按 `programSrcId` 算（载入以后改过的也算），画布没定义。
8. **泄漏测试**：通道颜色用第一批的 `chan_color()`（画布这几页用的颜色不同）；箭头流程条的凹口用白色三角盖出来。
9. **记录 · 运行中**的那几条：画布没画右边详情，这里给了指标 + 实时吸气量曲线 + 「在主页看」。
10. **按时间段导出**：从 / 到两个框只显示，点了还不能改（快捷按钮能改范围）；要做的话需要一个日期时间选择器（画布没画）。
11. **设置**：压力单位 / 温度单位 / 主题只是提交设置键，界面本身还是 MPa / °C / 浅色（换单位的显示没做）；数据存储页的长路径和说明有时挨得很近。
12. **WiFi 密码键盘**：预览截图时框里预填了 8 位（`main.cpp` 传的初值），真机上点「连接」打开时是空的。
13. **英文**：第二批的文案都走了 `tr()`，但 `i18n_en.cpp` 没有补第二批的条目，切英文时这些页面仍是中文。
14. 第一批报告里列的公共项（断线全屏、一级报警换页重新盖上）这批没做；toast 做了。

## 7. 没验证的
- 只在无屏预览里截图核对外观；SDL / X11 窗口里逐个点（拖动读数线、数字键盘改参数、拖滑条、翻月、添加步骤后滚动）没做，本机没有 SDL。
- 曲线页重建一次大约画 6 × 900 段线（抽稀后），在 RK3568 软件渲染上的帧率没测；如果卡，可以把曲线画进 `lv_canvas` 缓存，只在数据变时重画。

## 8. lv_conf.h
- 这批**不需要**改 `lv_conf.h`。用到的都已开着：label / span / arc / image（A8、ARGB8888）、`lv_draw_triangle`、`lv_async_call`、`lv_timer`。
- 二维码用的是离线生成的 126×126 图标（`LV_USE_QRCODE` 仍是 0）；以后要按设备信息生成二维码再打开它。
- 可选（第一批已列）：`LV_USE_FLOAT 1`（曲线斜线更顺）、`LV_USE_DRAW_SW_COMPLEX_GRADIENTS 1`（曲线下渐变填充，现在是单色半透明）。
