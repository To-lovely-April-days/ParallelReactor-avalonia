# 模块报告 · reactor-hmi 触屏界面第一批（2026-11-26）

范围：`apps/reactor-hmi/`（整个重写）和 `tools/preview/Makefile`。没动 `src/`、`config/`、`CMakeLists.txt`、`tools/build`，也没碰 git。
界面进程不依赖 `src/` 里的任何后端代码。

## 1. 怎么编、怎么看

```bash
make -C tools/preview -j8                        # 无屏预览程序（tiny_ttf + 无窗口）
tools/preview/out/reactor-hmi-shot --help        # 列出能截的页面
tools/preview/out/reactor-hmi-shot --page chan-full --shot x.bmp [--lang en]
make -C tools/preview shots                      # 所有页面各截一张 → tools/preview/out/shots/*.bmp/.png
```

- 预览 Makefile 自动收集 `apps/reactor-hmi/**/*.cpp`（和 CMake 的 `GLOB_RECURSE` 一致），加文件不用改 Makefile。
- `main.cpp` 三个后端都保留：`REACTOR_USE_SDL` / `REACTOR_USE_X11` / `REACTOR_HEADLESS`。SDL、X11 两种在本机做了语法检查（`-fsyntax-only`，含 FreeType 路径），没有实际链接运行（本机没有 SDL / X11）。
- 新参数：`--fonts DIR`、`--lang zh|en`、`--page NAME`、`--shots DIR`。旧的 `--font` 还认，但忽略。
- 旧的 `hello_page.{hpp,cpp}` 已删掉。CMake 是 glob，不用改。
- 编译选项 `-Wall -Wextra`，没有警告。本机只有 Apple clang，GCC 9.4 没试过。代码里没用 C++20 的东西（没有 designated initializer）。

## 2. 目录结构

```
apps/reactor-hmi/
├── main.cpp                 后端选择、参数、主循环；无屏时按 --page 截图
├── assets/                  生成图标的离线脚本（板子上不用跑）
│   ├── svg2lvgl.py          SVG → LVGL A8 / ARGB8888 数组（只用 numpy）
│   ├── icons_manifest.py    120 个图标的 SVG 源（从画布定稿里摘出来的）
│   └── text2a8.py           随包字体缺字时，把文字预渲染成 A8（现在只有「한국어」，用 Pillow）
├── model/                   纯数据，不含安全判断
│   ├── telemetry.hpp        遥测快照结构体，逐个对照 C#（字段名 = JSON 名）
│   ├── model.hpp            Model = 快照 + 设置 + 报警/事件 + *Ext（后端要补的字段）；ModelMailbox
│   ├── derive.{hpp,cpp}     显示规则：釜的 12 种显示状态、提示行、数字格式
│   ├── actions.{hpp,cpp}    用户意图接口 Actions + LoggingActions（打印，再转给模拟源）
│   └── sim_source.{hpp,cpp} 模拟数据源（画布上的场景），同时实现 Actions
└── ui/
    ├── app.{hpp,cpp}        屏幕管理：顶栏常驻、内容区换页、弹层、报警分级弹出、停止确认
    ├── kit/                 公共控件（第 3 节）
    └── pages/               home.cpp（装置总览 / 气路图 / 方案总览）、popups.cpp（点釜面板、语言）、
                             chan_full.cpp（全部设定三种）、alarm_page.cpp（报警页 + 第二批占位页）
```

**线程**：现在只有 LVGL 主线程。模拟源每 200 ms（无屏时每帧）推进一次，`post()` 进 `ModelMailbox`，主循环 `take()` 以后调 `App::apply()`。以后 `net/` 的网络线程只要调 `mailbox.post()`，LVGL 这边不用改。`ModelMailbox` 用互斥锁保护，只留最新一份。

**点击回调**：`on_tap()` 把回调推迟到这一轮事件处理完（`lv_async_call`）再跑。回调里常常要换页、关弹窗，会删掉被点的那个控件，推迟以后就安全了。

## 3. 公共控件（`ui/kit/`）

| 控件 | API | 画布出处 |
|---|---|---|
| 主题 | `theme.hpp`：颜色 `col::*`、`chan_color(ch)`、`liquid_color(°C)`、`font(px, weight)`、`box/label/label_at/rich/place_baseline/card_shadow/ring/icon/on_tap`、`psi_to_mpa` | family.py、states*.py |
| 字体 | `fonts_init(dir, argv0)`。主字体 Inter，中文回退 Noto Sans SC（`lv_font_t::fallback`），按字号 × 字重缓存 | 设计稿「Inter, Noto Sans SC」 |
| 文案 | `tr("中文")`、`trf(fmt, …)`、`trc("曲线 (升温方式)")`（同一个词在不同地方译法不同时用）。英文表在 `i18n_en.cpp` | — |
| 顶栏 | `TopBar::create / set_nav / set_status / set_bell(count, tier, selected) / set_time`，回调 `on_nav / on_bell / on_lang` | family.topbar + estopfix |
| 页签卡片 | `TabCard::create(parent, x, y, w, {TabDef{icon, text, badge, style}}, sel)`、`on_select` | family.tabs、Alarm-Page |
| 卡片 | `card(parent, x, y, w, h)` | — |
| 圆钮 | `round_btn(…, RoundStyle::White/WhiteRed/Stop/Blue/TempCtl, icon, 文字)`：圆钮，字在下面<br>`round_btn_side(…)`：字在右边两行（启动控温 / 确认 / 看曲线）<br>`col_btn()`：竖栏 62 px 的大圆钮，带状态点和进度弧 | Chan-Full、Chan-QuickRun、Alarm-Page |
| 矩形按钮 | `rect_btn(parent, x, y, w, h, 文字, filled, 颜色, px, 圆角, icon)` | — |
| 带灯选项 | `LampGroup::create(parent, x, y, {选项}, {宽}, sel)`、`set()`、`on_change`；按钮宽度会跟着文字撑开 | Chan-Full「定值 / 曲线」 |
| 开关 | `Switch::create(parent, x, y, on)`、`set()`、`on_change(bool)`（64×34，里面写开 / 关） | Chan-Full |
| 数值框 | `NumField::create(parent, x, y, w, h, NumSpec, readonly)`，点了弹数字键盘；`on_apply(double)`；只读值块用 `value_block()` | Chan-Full / FullRun |
| 数字键盘 | `NumPad::open(NumSpec{标题, 单位, 值, min, max, 小数位, 上次设定, 当前实测}, on_apply)`；有滑条，超范围时给红字、不让应用 | Kbd-Num、Chan-QuickKbd |
| 文字键盘 | `TextKbd::open(标题, 初值, 最大长度, on_ok)`：大写 / 退格 / 123 / 中英 / 空格 / - / . / 确定，带拼音候选行 | Kbd-Text |
| L 型弹窗 | `Dialog::open(DialogSpec{w, 颜色, 图标, 标题, 副标题, body 回调, 次按钮, 主按钮, 两个回调})`：彩色标题条 + 内容 + 右下两个 150×52 的按钮 | abortask2.py、Alarm-L2 |
| 停止确认 | `confirm_stop(…)`、`App::stop_all_confirm()`、`App::abort_confirm(ch)`。每种停止都先弹这个 | Prog-AbortAsk |
| 报警三级 | `AlarmFull::show()`（一级全屏）、`alarm_dialog()`（二级弹窗）、`Bubble::show()`（三级铃铛气泡，5 s 后自己收起）；`App::show_alarm()` 按 `AlarmExt::tier` 分到这三种 | Alarm-L1/L2/L3 |
| 文件图标 | `file_icon(parent, x, y, "CSV"/"JSON"/"PDF", large)` | fileicons.py |
| 小釜图标 | `mini_vessel(parent, x, y, 高 17/18/19/21/22/24/34, 液体颜色, 停用)` | 列表、页签里的小釜 |
| 釜「乙」 | `Vessel::create(parent, x, y, S/M/L)`、`set(VState, 序号, 温度, 保持进度)` | States-D3 / States-Liquid 乙 |
| 读数块 | `Readings::create(parent, x, y, w, scale)`、`set(model, ch)`：温度、压力、吸气量，加一行提示 | states3.values |
| 自绘 | `draw.hpp`：斜纹 `hatch()`、折线（实线 / 虚线）、折线下面的填充、文字、圆 | 甘特图、计划曲线、升温曲线 |

**图标方案**（`LV_USE_VECTOR_GRAPHIC 0`）：图标在编译前离线画成位图，生成的 C 数组 `ui/kit/icons_gen.{hpp,cpp}` 直接提交，CMake 和预览 Makefile 都不跑脚本。

- 脚本是 `assets/svg2lvgl.py`，自己实现的 SVG 光栅化（path/rect/circle/渐变/虚线/描边端点和拐角），每像素 8×8 采样抗锯齿，只依赖 numpy。
- **单色图标出 A8**：运行时用 `image_recolor` 上色，一张图可以用在任何颜色上。
- **多色图标出 ARGB8888**：国旗、报警三角这类。
- 改图标的步骤：在 `icons_manifest.py` 里改 SVG，然后运行 `python3 assets/svg2lvgl.py`；加 `--preview DIR` 可以出 PNG 对照。
- 釜拆成几层：钢件 + 玻璃（ARGB）、液体（A8，按温度上色）、玻璃高光 + 搅拌轴（ARGB）、加热带 / 降温虚线 / 报警红边（A8）。桨叶、气泡、序号标和保持倒计时环用 LVGL 对象加动画。
- 釜有三种尺寸：120×134（总览、气路、点釜面板）、190×212（全部设定）、22×25（全部设定顶上的通道条）。

**釜的显示规则**（States-D3「乙」）：

| 状态 | 怎么显示 |
|---|---|
| 升温 | 加热带呼吸，橙色，周期 2 s |
| 升压 | 冒泡；总览里进气管的短管变绿，气路图里管路是绿色虚线 |
| 反应 | 冒泡，桨叶转 |
| 保持 | 序号外一圈倒计时环 |
| 降温 | 外壁一圈青色虚线，在闪 |
| 等气路 | 进气管黄色虚线，旁边一个沙漏 |
| 结束 | 序号变成对勾 |
| 超温 / 超压 | 釜身红边闪；数字底下一块红色 |
| 离线 | 序号外一圈黄色，在闪 |
| 停用 | 整只釜半透明，液体变灰 |

- 液体颜色按温度插值：20 °C 蓝 → 65 °C 黄 → 110 °C 橙 → 200 °C 红。
- 「急停」状态没有做（已经决定不做急停）。

## 4. model：结构体和动作接口

**`telemetry.hpp`** 逐个对照 C# 的 `TelemetrySnapshot.cs` 和 `EventRecord.cs`。

- 成员名直接用 JSON 的 camelCase 名（例如 `tempSpC`、`pressurePsi`、`program->waitingGas`），以后 JSON → 结构体逐字段赋值就行。这一点和代码约定里的 snake_case 不一样，是有意的。
- 可空字段用 `std::optional`，时间一律 UTC 毫秒，压力仍是 psi 表压，显示时再换成 MPa。
- 包括的结构：
  - 快照本身：`TelemetrySnapshot`、`ModeSnap`、`EstopSnap`、`CommSnap`
  - 通道：`ChannelSnap`、`ApproachSnap`、`ProfileSnap`、`ChannelProgramSnap`
  - 其他设备和任务：`StirSnap`、`GasSnap`、`ProgramsSnap`、`LeakSnap`、`SessionSnap`、`CalibSnap`
  - 报警和事件：`AlarmDto`、`EventRecord`
  - 设置：`SettingsDto` 里界面用到的部分（limits / alarms）

**`model.hpp` 里的 \*Ext**：现有 `/api/v1` 里没有、但触屏新界面要用的数据。这些都由后端补，详见第 7 节。

| 结构 | 内容 |
|---|---|
| `ProgressExt` | 程序名、各步中文短名、当前步目标、步进度、已运行 / 约剩、排队位次、在等哪几路、未下发草稿 |
| `PlanExt` | 方案甘特图的各段（开始 / 结束 / 是否「最长」），计划温度 / 压力，已走过的实测 |
| `AlarmExt` | 级别 1/2/3、来源（channel / comm / system）、量值 / 设定 / 上限 / 量程、「系统已经做了」「接下来」、上下文、断开时刻、处理建议 |
| `ChanSetExt` | 到位范围、防超调开关、加热开关、曲线各段 |

**动作接口**是 `model::Actions`（抽象类），页面只调它。每个方法注释里写了对应的端点：

- 全局：`stop_all(include_stir)`、`stir_set_rpm`、`stir_run`、`manual_valve`
- 加热：`set_temp_sp`、`heat(ch, on)`、`set_heat_mode`、`set_band`、`set_anti_overshoot`、`set_profile`、`start_profile`
- 气体：`set_valve`、`gas_zero`、`set_solution_volume`
- 通道：`channel_enable`、`copy_to_all`、`abort_program`
- 报警：`ack_alarm`、`ack_all`
- 系统：`set_language`
- 结果回调：`on_result(ok, 文案)`，留着以后做 toast

`LoggingActions` 把每次调用打印成 `[action] …`，再转给 `SimSource`，所以模拟时界面有反应。

**模拟源** `SimSource` 照画布的场景：

| 通道 | 状态 |
|---|---|
| 1 号 | 加氢筛选-A，第 4 步升压 |
| 2 号 | 加氢筛选-A，反应中，吸气量在涨 |
| 3 号 | 加氢筛选-B，升温中 |
| 4 号 | 加氢筛选-B，保持 |
| 5 号 | 手动，另有一份已编辑、还没下发的程序 |
| 6 号 | 加氢筛选-C，排第 2 等气路 |
| 7、8 号 | 停用 |

- 报警表和 Alarm-Page 一样。
- 模拟时钟从今天 14:05:12 开始，所以截图稳定。
- `inject_l1/l2/l3()` 用来预览三级报警。

## 5. 页面和截图

截图在 `doc/hmi-shots/`，分辨率 1280×800，全部由 `make -C tools/preview shots` 生成。

| 画布 | 页面名 / 截图 | 说明 |
|---|---|---|
| Main-F1 装置总览 | `main-f1.png` | 8 张卡片（每张高 290 → 330），右边是「全部釜」竖栏。点卡片弹点釜面板，停用的路直接进全部设定 |
| Main-F2 气路图 | `main-f2.png` | 每路的读数、釜、单向阀和进气阀（SVn），共用总管，三路气源和排空。开着的气源到最远一个开阀通道之间的总管画成绿色 |
| Plan-All 方案总览 | `plan-all.png` | 甘特图：做完 / 正在做 / 还没做 / 反应用斜纹 / 等气路、「现在」竖线、未下发的路。点一行点开那一路 |
| Plan-One 点开一路 | `plan-one.png` | 步骤名一行；温度、压力两张计划图（实测是实线 + 填充，计划是虚线，「现在」橙线）；下面列出其余几路 |
| Chan-Quick | `chan-quick.png` | 遮罩挖掉被点的卡片，加蓝框。四块：启动加热、目标温度、进气阀、吸气量清零；下面是「全部设定 · 进入 N 号」 |
| Chan-QuickRun | `chan-quick-run.png` | 程序控制。9 段进度条；四个圆钮：程序页、曲线、设定(只看)、中止… |
| Chan-QuickKbd | `chan-quick-kbd.png` | 数字键盘 |
| Chan-Full（定值） | `chan-full.png` | 左：大釜和读数，加三个圆钮。右：温度卡（升温方式、目标、到位范围、防超调、启动控温）和气体卡 |
| Chan-FullCurve | `chan-full-curve.png` | 曲线图跟温度变色，选中的那段有提示框；6 列表格，加一段；启动曲线升温 |
| Chan-FullRun | `chan-full-run.png` | 程序进度 + 只读的温度、气体值块（「程序控制 · 只能看」） |
| Alarm-Page | `alarm-page.png` | 5 个页签（带数字），列表和详情。详情里有量程条、「系统已经做了」「接下来」、确认 / 看曲线 |
| Alarm-L1 | `alarm-l1.png` | 红色全屏，顶栏还在，第 3 条是「情况不对就点主页的『全部停止』」 |
| Alarm-L2 | `alarm-l2.png` | L 型红色弹窗 |
| Alarm-L3 | `alarm-l3.png` | 铃铛下面的黄色气泡，倒数 5 s |
| Lang-Pop | `lang-pop.png` | 8 种语言：中、英标「完整」，其余标「未齐」。点了马上换（整页重建） |
| Kbd-Num / Kbd-Text | `kbd-num.png`、`kbd-text.png` | — |
| 停止确认（另加） | `stop-all.png`、`abort-ask.png` | 全部停止 / 中止程序，都先确认 |
| 英文界面（抽查） | `en-main-f1.png`、`en-chan-full.png`、`en-alarm-page.png` | — |

程序、曲线、记录、设置四个导航先放了占位页。

## 6. 和画布还对不上的地方（清单）

1. **字体**
   - 本机预览时中文用的是 STHeiti，只有一种字重，所以中文的粗体看不出来；英文和数字用 Inter，字重是对的。板子上用 FreeType 加载 Noto Sans SC 的 Regular / Medium / Bold，中文粗体会正常。
   - 72 px 大读数画布用的是 300（Light），我们只有 Regular。
   - letter-spacing 和 `tabular-nums` 没做：LVGL 只有整数字间距，Inter 默认数字就是等宽的。
2. **韩文**：随包的 Inter 和 Noto Sans SC 子集里都没有韩文字形。语言弹窗里的「한국어」改用预渲染的图（`text2a8.py`，从 macOS 的 AppleSDGothicNeo 渲染）。以后如果真要韩文界面，得加一个韩文字体。
3. **动画**：静态截图里看不出来，没有逐帧核对。
   - 等气路的沙漏不翻转。
   - 降温外壁的虚线不流动，改成透明度呼吸。
   - 升压管路的「绿色流动」是静态虚线：LVGL 9.2 的线没有 dash offset。
4. **阴影**：LVGL 每个对象只有一层阴影，画布上两层的阴影（细边 + 柔影）用「1 px 边框 + 一层阴影」近似。
5. **一级全屏**：背景画布是径向渐变，现在是竖直的线性渐变。要做径向得打开 `LV_USE_DRAW_SW_COMPLEX_GRADIENTS`，见第 9 节。
6. **点釜面板的遮罩**：用 4 块矩形围住卡片，卡片圆角那 4 个小角没被压暗。
7. **方案总览**
   - 时间轴按数据自动取范围（从最早开始的整点，至少 14 h），所以起止时间和画布的 8:00–22:00 不完全一样。
   - 「最晚 xx:xx」由模拟数据算出来。
8. **全部设定 · 曲线**
   - 表格最多显示 6 段；超过 6 段时怎么翻页，画布没画，还没做。
   - 曲线下面的浅蓝渐变填充改成了单色半透明。
9. **800 高多出来的 80 px**
   - 主页卡片、甘特图的行高、全部设定的卡片都变高了。
   - 全部设定里，曲线图拿到多出高度的 3/4。
   - 一级全屏整块往下移 40 px。
   - 设计文档 §8.3 说移植前会在画布上补一版 1280×800；那一版出来以后需要按它再对一次。
10. **画布里本身对不上的数据**
    - 方案总览各路的步骤和装置总览不一致，现在统一按装置总览的场景出数据。
    - 画布上的「等 RV1 用完气路」按「不出现 RV」的决定改成「等 1 号用完气路」。
11. **数据里的文案**：报警正文、「系统已经做了 / 接下来」、步骤名这些来自后台，切到英文后仍是中文；`tr()` 表里只放了几条演示用。要么后台按语言出文案，要么给出码表，见第 8 节。
12. **文字键盘**
    - 拼音只有一张很小的词表（程序名常用的几十个字），候选翻页键只是画了出来、没有功能。
    - 完整输入法有两条路：一是打开 `LV_USE_IME_PINYIN` 接 LVGL 自带的拼音（还得配词库），二是我们自己带一份词库。
13. **交互**：只在截图里核对过外观，没有在 SDL 窗口里逐个点过，本机没有 SDL。下面这些逻辑写了，但还没经过实机点击验证：
    - 拖滑条、超范围红字
    - 点后台语言切换
    - 按钮推迟执行
    - 报警只在新出现时弹出（开机时已有的报警不弹，避免一开机就盖住全屏）
14. **全部停止**：确认框里写的是「搅拌照常转」，`stop_all(false)` 也不停搅拌。设计文档 §6 说「可选停搅拌」，具体给不给这个选项，还要你定。

## 7. 第二批要做的页面和要先定的事

- **程序**：编辑程序（Prog-1）、9 种步骤的参数区（Prog-Params，要用带灯选项、开关、数值框）、添加步骤、升温曲线 8 / 16 段、程序库（我的程序 / 内置模板，名字输入用文字键盘）、运行中 / 中止确认 / 跑完 / 已中止。
- **泄漏测试**：开始前、进行中、结果（通过 / 不通过）。
- **曲线**：气体消耗 / 温度 / 压力 / 三项对照（Curve2-*）。
  - 设计文档 §8.4 已经说了 `lv_chart` 不够用，要自己画；`kit/draw.hpp` 的折线、填充、虚线可以直接用。
  - 还要做时间轴缩放、读数浮窗、「设定值附近」纵轴。
- **记录**：运行记录（完成 / 中止）、每日数据文件（用 `file_icon`）、导出到 U 盘、按时间段导出（要日历控件）。
- **设置**：10 个分类（通用、参数与报警、通道与气路、容积标定、温控 PID、通讯与模块、网络 / WiFi 密码、数据存储、用户与权限、关于）。
- **公共的**：toast（接 `Actions::on_result`）、断线全屏「正在连接后台服务…」（`Model::connected = false` 时显示，按钮全部变灰；设计文档 §8.1 要求）、一级报警没确认时每次换页都重新盖上。

## 8. 网络绑定（`net/`）要做的事

1. **网络线程**：连接 `/run/reactord/hmi.sock`，走 HTTP + WS。
   - 订阅 `telemetry / alarm / event / progress`，断线以后自动重连、重新订阅。
   - 每收到一帧，就拼一份完整的 `Model`，调 `ModelMailbox::post()`。不要在网络线程里碰任何 LVGL 对象。
2. **JSON → 结构体**：`telemetry.hpp` 的成员名就是 JSON 字段名；用 nlohmann 的 `get_to` 逐个赋值；可空字段对应 `optional`；时间字符串（`…Z`）换成 UTC 毫秒。
3. **实现 `model::Actions`**：每个方法注释里写了端点。
   - 要求是不阻塞：把请求丢给网络线程，结果再经 `lv_async_call` 回到 LVGL 线程，调 `on_result(ok, message)`。
   - 失败文案照抄后端返回的中文 `message`。
4. **后端要补的数据**（`*Ext`），建议放在新端点或 WS `progress` 里：
   - 程序名、各步短名 / 种类、当前步目标、步进度、约剩时间、排队位次和在等谁，`PlanExt` 的各段时间。
     这些都可以从 `GET /channels/{id}/program/run` 的 `steps` 加引擎状态算出来，最好由后端算。
   - 报警分级 `tier`（1 = 联锁、2 = 错误、3 = 警告）、来源分类、量值 / 设定 / 上限 / 量程、「系统已经做了 / 接下来」、上下文。
     现在 `AlarmDto` 只有 level / title / message；界面里的 `alarm_tier` 不应该靠猜标题。
   - 单路的到位范围、防超调开关、加热开关。曲线各段已有，在 `/channels/{id}` 的 `profile` 里。
   - 语言 `languageCode`（`PUT /system/language` 保存后由快照带回来）、开机时刻。
5. **语言切换**：`App::apply()` 看到 `languageCode` 变了，就自动重建界面。

## 9. lv_conf.h

这一批**不需要**改 `lv_conf.h`。下面几项可选，打开的话效果更接近画布：

- `LV_USE_DRAW_SW_COMPLEX_GRADIENTS 1`，同时 `LV_GRADIENT_MAX_STOPS` ≥ 3：一级全屏的径向渐变、曲线下面的渐变填充。
- `LV_USE_IME_PINYIN 1`：完整拼音输入（还要配词库）。
- `LV_USE_FLOAT 1`：自绘折线可以用小数坐标，斜线更顺。现在是整数坐标。

现在用到、默认已经开着的功能：tiny_ttf（预览）/ FreeType（板子）、`LV_USE_SPAN`（一段话里几种样式；9.2 的 label 没有 recolor）、arc、line、image（A8 / ARGB8888）、`lv_async_call`、动画、`transform_rotation`（气泡和面板旁边的小三角）。
