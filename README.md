# ParallelReactor · 多通道平行反应仪控制系统（Avalonia 还原版）

HTML 原型首页（反应器与气路系统）的 1:1 Avalonia 桌面还原，主打 **Linux**，固定 **1280×800**（10.1 寸工控屏）。

## 已实现（首页）

- **顶栏**：品牌区、居中标题、WiFi/服务热线(021-67230701)/实时时钟/通知铃/头像
- **工具栏**：开始运行、全部停止、配方模板、泄漏测试、手动阀控（可切换高亮）、运行通道计数、**急停**（已移到工具栏最右）
- **首页主体**：面板头 + 图例 + **气路图**
  - 8 个反应釜的完整 P&ID 矢量结构（注射口/单向阀/釜头/加热块/玻璃内衬/液体/桨叶/气泡/热电偶 TT/压力变送器 PT/出气管），按状态着色
  - 共用气路总管 + 3 路进气阀 + 1 路排空阀
  - 动画：桨叶旋转、反应气泡上升、超压脉冲圈
- **底栏**：退出登录、6 个导航药丸（HOME 高亮）、型号标签
- **交互**：
  - 点击反应釜 → 弹出右侧详情抽屉（PV 读数 / SP 步进调节 / 桨叶·结束方式分段选择 / 阀门开关 / 启停·停用·复制配置 / 高级 PID 滑块 / Modbus 寄存器）
  - 点击进气阀 / 排空阀 / SV 阀 → 手动阀控（带管理员与运行态守卫）
  - 实时数据 tick（温度/压力/气体微动）+ 走时时钟
  - Toast 提示（ok/warn/err 三色）

## 运行

需要 .NET 8 SDK。

```bash
cd ParallelReactor
dotnet restore
dotnet run
```

### Linux 依赖

确保已安装 X11 / 字体相关库（Debian/Ubuntu）：

```bash
sudo apt-get install -y libice6 libsm6 libfontconfig1
# 中文显示建议安装：
sudo apt-get install -y fonts-noto-cjk
```

字体：Inter 已通过 `Avalonia.Fonts.Inter` 内置；中文走系统字体（建议安装 Noto Sans CJK）。

## 打包发布（Linux）

> 需在已安装 **.NET 8 SDK** 的机器上执行（Windows/Mac/Linux 均可交叉编译出 Linux 包）。
> 按目标设备 CPU 选 RID：x86 工控机/PC 用 `linux-x64`，ARM 工控屏/树莓派用 `linux-arm64`。

### 自包含单文件（推荐，目标机无需装 .NET）

```bash
dotnet publish -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o publish/linux-x64
```

产物为单个可执行文件 `publish/linux-x64/ParallelReactor`（约 70–100 MB，已内含运行时）。
ARM 设备把 `-r linux-x64` 换成 `-r linux-arm64` 即可。

> ⚠️ 不要加 `-p:PublishTrimmed=true`：Avalonia 大量使用反射，裁剪会破坏界面/绑定。

### 框架依赖（目标机已装 .NET 8 运行时，包更小）

```bash
dotnet publish -c Release -r linux-x64 --self-contained false -o publish/linux-x64-fd
```

### 拷到 Linux 运行

```bash
chmod +x ParallelReactor
./ParallelReactor
```

首次在新机器上跑前，请确认已装 X11/字体依赖与中文字体（见上文「Linux 依赖」）；缺中文字体会显示成方块 □□□。

## 结构

```
Models/        数据模型（Reactor / GasInlet / Recipe / PidParams / ReactorState）
ViewModels/    MainViewModel（数据中枢·工具栏·tick）、DrawerViewModel（抽屉交互）
Controls/      SchematicControl（气路图矢量绘制+命中检测）、DrawerView（抽屉）
Views/         MainWindow（顶栏/工具栏/首页面板/底栏/Toast 接线）
Styles/        Controls.axaml（按钮/药丸/步进器/分段/开关样式）
Converters/    值转换器
Api/           对外接口（REST + WebSocket）：鉴权、控制权租约、写命令、会话（见 doc/接口使用说明.md）
Contracts/     ParallelReactor.Contracts：HMI 与 PC 共用的线上类型（纯 net8.0，无 Avalonia）
Control/       PC 控制软件（见下）
tests/         HMI 单元测试（xunit）
```

### 解决方案布局（ParallelReactor.sln）

| 工程 | 说明 |
|---|---|
| `ParallelReactor.csproj`（根） | 触屏 HMI（Avalonia，Linux 1280×800）；`Compile Remove` 了 `Contracts/**`、`Control/**`、`tests/**` |
| `Contracts/ParallelReactor.Contracts` | 线上类型：`TelemetrySnapshot`、REST / WS DTO、`ApiJsonContext`、错误码、`OpCodes`、`EventRecord`、`SessionManifest`、`StepType` |
| `Control/ParallelReactor.Client` | PC 端接口客户端（REST + WS、租约 / 心跳状态机、重连 / 重同步、幂等重放），纯 BCL |
| `Control/ParallelReactor.PerfTest` | RV1–RV4 摸底性能测试：方案 JSON、`PerfRunner` 引擎、指标、`report.html` / CSV / JSON |
| `Control/ParallelReactor.Control` | PC 控制软件（Avalonia 11.1，`research/pc-control.html` 样稿 1:1）：系统视图 + 性能测试视图，Debug 带无头截图模式 |
| `Control/tests/ParallelReactor.Control.Tests` | PC 端单元测试（Contracts 往返、Client、PerfTest、Core、色板 / 字号检查；不引用 App） |
| `Control/tests/ParallelReactor.Control.App.Tests` | PC 控制软件的视图模型、对话框与实时数据源测试（引用 App） |
| `Control/tools/ClientSmoke` | 对真 HMI 的接口走通工具 |
| `tests/ParallelReactor.Tests` | HMI 单元测试 |

## Control/ · PC 控制软件

```bash
dotnet run --project Control/ParallelReactor.Control        # 桌面窗口（1440×900）
dotnet test Control/tests/ParallelReactor.Control.Tests     # PC 端测试（不引用 App）
dotnet test Control/tests/ParallelReactor.Control.App.Tests # PC 控制软件的视图模型与数据源
```

- 连接触屏 HMI 的接口（`api.json` 里 `enabled=true` + 令牌），申请控制权后可下发程序 / 加热 / 搅拌等命令；停止类与急停不需要控制权。
- **性能测试**标签：按 `test-plan/RV1-4摸底性能测试方案.md` 的测试项（阶段 0–5；6–7 二期灰显）勾选 → 开始 → 现场确认 / 仪器读数录入 → 自动生成 `report.html` + `raw/telemetry.csv` + `result.json`（默认 `~/Documents/ParallelReactor/perf/<运行编号>/`）。
- 截图 / 自动化：`PR_CONTROL_HEADLESS=1 PR_CONTROL_SHOT=<目录> PR_CONTROL_SCENARIO=main|perf|perf-report`。
- 全部说明见 **doc/控制软件说明.md**（连接与控制权流程、每个界面与截图、性能测试流程、文件去向、环境变量、已知限制）。

## 备注

- **全屏**：窗口为 Linux 无边框全屏（`WindowState=FullScreen` + `SystemDecorations=None`），无最大化/最小化/标题栏，开机即铺满屏幕。
- **图片资源**：logo、头像、工具栏图标、导航图标均为可替换 PNG，见「图片替换清单.md」。WiFi 按要求用 SVG 矢量还原。
- 气路图采用 **Avalonia 矢量重绘**（非加载 SVG），坐标系与原 `viewBox 0 0 1440 484` 一一对应，可无损缩放并承载交互/动画。
- 加热块斜纹用近似填充（Avalonia 无原生 pattern），如需精确可改用 `VisualBrush` 平铺。
- 后续页面（程序/曲线/记录/报警/设置）尚未实现，点击对应药丸仅切换高亮。
