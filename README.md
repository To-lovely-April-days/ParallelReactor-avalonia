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
```

## 备注

- **全屏**：窗口为 Linux 无边框全屏（`WindowState=FullScreen` + `SystemDecorations=None`），无最大化/最小化/标题栏，开机即铺满屏幕。
- **图片资源**：logo、头像、工具栏图标、导航图标均为可替换 PNG，见「图片替换清单.md」。WiFi 按要求用 SVG 矢量还原。
- 气路图采用 **Avalonia 矢量重绘**（非加载 SVG），坐标系与原 `viewBox 0 0 1440 484` 一一对应，可无损缩放并承载交互/动画。
- 加热块斜纹用近似填充（Avalonia 无原生 pattern），如需精确可改用 `VisualBrush` 平铺。
- 后续页面（程序/曲线/记录/报警/设置）尚未实现，点击对应药丸仅切换高亮。
