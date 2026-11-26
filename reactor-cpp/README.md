# reactor-cpp · 八通道平行反应仪（C++ / LVGL 重构）

设计见 `doc/设计文档.md`，现有 C# 系统的事实清单见 `doc/附录-现有系统盘点.md`。

## 现在到哪了：M0

| 内容 | 位置 | 状态 |
|---|---|---|
| Modbus RTU 组帧 / 解析 / CRC | `src/hw/modbus_rtu.*` | 单测通过（`tests/test_modbus.cpp`） |
| 串口（termios） | `src/hw/serial_port.*` | 在电脑上用伪串口 + 假温控验证通过 |
| 只读探测工具 `rtu-probe` | `apps/rtu-probe` | 预设 temp / press / stir |
| LVGL 示例页 `reactor-hmi` | `apps/reactor-hmi` | 电脑上无屏渲染通过；板子上待验证 |
| 后台服务 `reactord` | `apps/reactord` | 占位 |
| 板子同款编译环境 | `docker/` | Ubuntu 20.04 arm64 |

板子上怎么编、怎么验证：`deploy/M0-板子上验证.md`。

## 目录

```
config/        lv_conf.h（LVGL 配置，后端和字体由 CMake 选项决定）
src/hw/        设备层
apps/          reactord（后台服务）、reactor-hmi（触屏）、rtu-probe（探测工具）
tests/         单测
third_party/   LVGL 9.2.2 源码（已删掉 demos / examples / docs / tests）
docker/        板子同款编译容器
tools/preview/ 没有屏幕的电脑上把界面渲染成图片
deploy/        部署和现场验证说明
```

## 编译

板子上（Ubuntu 20.04）：

```bash
sudo apt install -y build-essential cmake pkg-config libsdl2-dev libfreetype6-dev fonts-noto-cjk
cmake -S . -B build -DREACTOR_FULLSCREEN=ON && cmake --build build -j4
```

电脑上不装任何东西、只看界面效果（macOS / Linux，只要有 cc 和 make）：

```bash
make -C tools/preview -j8 && tools/preview/out/reactor-hmi-shot --shot shot.bmp
```
