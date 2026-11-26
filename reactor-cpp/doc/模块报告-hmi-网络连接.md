# 模块报告 · reactor-hmi 连接后台（net/）（2026-11-26）

范围：`apps/reactor-hmi/net/`（新）、`apps/reactor-hmi/main.cpp`、`ui/app.{hpp,cpp}`（断线盖层）、`model/model.hpp`（加一个 `linkStatus`）、`model/actions.hpp`（只改注释里的端点）、`tools/preview/Makefile`（`shots` 带 `--sim`，加 `selftest-net`），以及新文档 `doc/接口契约-新增.md`。
没动 `src/`、`tests/`、`tools/build`、`CMakeLists.txt`、`config/`，没碰 git。触屏进程**不包含 `src/` 的任何东西**（SHA-1 / base64 / HTTP / WS 都在 `net/` 里自己写了一份）。

## 1. 怎么用

```bash
make -C tools/preview -j8
tools/preview/out/reactor-hmi-shot --selftest-net                 # 网络层自检（内置假服务器），94 项
make -C tools/preview shots                                       # 截图照旧（现在带 --sim）
tools/preview/out/reactor-hmi-shot --fake-serve /tmp/f.sock 600 & # 开发用假后台（罐头数据，每 1.2 s 推遥测）
tools/preview/out/reactor-hmi-shot --server unix:/tmp/f.sock --page rec-list --shot x.bmp
```

| 参数 | 说明 |
|---|---|
| （默认） | 连 `unix:/run/reactord/hmi.sock`；环境变量 `REACTOR_HMI_SERVER` 可改 |
| `--server ADDR` | `unix:/路径`、`/路径` 或 `tcp://127.0.0.1:8080`（开发机连 PC 入口） |
| `--token T` | TCP 入口的 Bearer 令牌（或 `REACTOR_TOKEN`）；Unix 入口不用 |
| `--sim` | 不连后台，用模拟数据源（原来的行为） |
| `--wait-ms N` | 无屏 + 连后台时，截图前最多等多久连上（默认 3000） |
| `--selftest-net` | 自检后退出，返回 0 = 全过 |
| `--fake-serve PATH [秒]` | 起一个假后台（自检用的同一套罐头数据）给界面连 |

连后台时，`--page` 只能切到主要页面（`setup_page_net` 的表）；要预览「注入某种状态」的页面（跑完、中止、泄漏不通过…）用 `--sim`。

## 2. 结构

```
net/
├── wire.{hpp,cpp}         SHA-1、base64、uuid4（Idempotency-Key）、ISO-8601 ↔ UTC 毫秒、URL 编码、时钟
├── http_client.{hpp,cpp}  地址解析、非阻塞 connect + poll 超时、增量 HTTP 解析器（定长 / chunked / 读到断开 / 100 Continue / 流水线）、
│                          HttpConn（keep-alive；复用的连接收到字节前就失败 → 重连重发一次）
├── ws_client.{hpp,cpp}    WS 帧编解码（客户端加掩码；分片拼接，中间夹控制帧；126 / 127 长度；关闭码）、客户端握手（核对 Accept）
├── net_client.{hpp,cpp}   NetClient：ws / read / cmd 三个线程、请求队列、退避重连；UiQueue（网络线程 → LVGL 线程的回调）
├── binding.{hpp,cpp}      JSON → model 结构体（纯函数）：快照 + 扩展、报警、事件、progress、设置、各页数据、程序 / 程序库（MPa ↔ psi）
├── backend.{hpp,cpp}      NetBackend：拼 Model、决定什么时候取哪份数据、gap 重同步；NetActions：Actions 的每个方法 → 端点
├── fake_server.{hpp,cpp}  自检用的假 reactord（Unix socket，HTTP + WS）
└── selftest.{hpp,cpp}     --selftest-net、--fake-serve
```

## 3. 线程

| 线程 | 做什么 | 碰什么 |
|---|---|---|
| LVGL 主线程 | 每 30 ms：`UiQueue::drain()`（命令结果 → toast）、`ModelMailbox::take()` → `App::apply()`；页面调 `NetActions`（只排队，不阻塞） | LVGL 对象 |
| ws 线程 | 连接、握手、收帧 → `NetBackend::on_ws_text`；回 Pong；每 5 s 发 Ping；15 s 没收到任何字节算断线；别的线程要发的消息（subscribe）经队列 + 唤醒管道由它发 | 自己的 socket |
| read 线程 | 顺序执行 GET（一条 keep-alive 连接）；每 250 ms 调 `on_tick`（决定要取哪些数据、更新断线说明） | 自己的 socket |
| cmd 线程 | 顺序执行写命令（单独一条连接，不排在大的读请求后面）；全部停止 / 停加热 / 关阀 / 中止这类 `urgent` 插到队头 | 自己的 socket |

- `NetBackend` 的 `Model m_` 由一把锁保护；三个网络线程改完都 `publish_locked()`：拷一份 `shared_ptr<const Model>` 投进 `ModelMailbox`（只留最新一份）。JSON 解析在锁外做。
- 锁的顺序只有一种：`NetBackend::mu_` → 信箱 / UiQueue 的锁；LVGL 线程只拿后两者，所以不会死锁。
- `SIGPIPE` 忽略（Linux 上另用 `MSG_NOSIGNAL`，macOS 用 `SO_NOSIGPIPE`）。

## 4. 连接、断线、重连

1. ws 线程连 `/api/v1/ws`。握手成功 → 盖层文字改成「已连上 …，等后台的第一帧数据」。
2. 收到 `hello`：记 `seqEpoch` / `eventSeqHead`；纪元变了（或第一次）就清掉本地事件；全部数据组标脏；发
   `subscribe {topics:[telemetry, events, alarms, progress], resumeAfterEventSeq?, seqEpoch?}`（本地记着最大 seq 时才带 resume）；第一次连上再 `GET /events?afterSeq=head−200` 填事件列表。
3. 收到第一帧 `telemetry` → `Model.connected = true`，盖层消失。
4. 断线（对端关、读写错、协议错、15 s 没数据）→ `connected = false`，界面盖上「正在连接后台服务…」并**挡住全部点击**（盖层在 `lv_layer_sys`，在所有弹窗之上；顶栏透过半透明底能看到「未连接」，钟照走）。小字写正在连哪、上一次为什么失败。
5. 重连退避：0.25 → 0.5 → 1 → 2 → 4 → 5 s 封顶，±20 % 抖动；连上超过 5 s 再断，从 0.25 s 重新开始。重连后回到第 2 步（带 `resumeAfterEventSeq`，服务端从环里补发）。
6. `gap`：`GET /snapshot`（马上刷新状态）+ `GET /events?afterSeq=<最大 seq>&limit=1000` 翻页补齐（最多 5 页；缺口 > 5000 条就只取最近 200 条），补完再发一次 `subscribe`（resume = 补到的最大 seq）。`epoch_changed` 先清本地事件。事件按 seq 去重、按 seq 倒序存，最多 500 条。
7. 写命令全带 `Idempotency-Key`，HTTP 连接在收到响应前断掉时用同一个键重发一次（服务端重放），不会执行两次。

## 5. 什么时候取哪份数据

数据分 19 组（`NetBackend::G`），每组「脏了且没在取」就在下一个 tick 取一次：

- 连上时全部标脏。
- 快照的 `revs.*`（接口契约 §2.1）变了 → 对应的组标脏。后端还没给 `revs` 时退回定时：设置 / 程序库 / 运行记录 30 s、单路程序 10 s、网络 10 s、U 盘 5 s、文件 / 用户 60 s。
- 一直定时的：`/device/comm` 5 s、`/storage` 60 s、`/trend` 10 s（增量：连上取最近 6 h，之后取上次末点之后的，最多留 24 h）；整定中 `/pid` 2 s。
- 事件 kind 前缀（`library.` / `settings.` / `program.` …）也会标脏（保险）；泄漏测试 / 标定 / 整定从「进行中」变成结束时，再取一次最终数据。
- 写命令成功后马上把相关的组标脏（存程序库 → 程序库，下发程序 → 单路程序 …），不等 revs。
- 运行记录：`GET /runs` 列表，再给前 20 条取 `/runs/{id}`（每分钟吸气量、程序副本、每步用时），结果按 id 缓存；运行中的每 60 s 重取。
- 界面自己算的：泄漏测试可选通道（启用、没跑程序、没报警）、程序库「正用在」（后端没给 `usedBy` 时按 `sourceId`）、每日文件按天分组、方案总览「已走过的实测」（从趋势里每分钟取一点）。

## 6. 动作 → 端点（NetActions）

全部在 `backend.cpp` 末尾，一个方法一两行。要点：

- 现有端点照 C# 的真实路径（第一、二批注释里有几个写错了，已经一起改了 `model/actions.hpp` 的注释）：设定温度 `PUT …/temperature/setpoint`、清零 `POST …/gas-counter/reset`、开始程序 `POST …/program/run`、中止 `DELETE …/program/run`、泄漏测试 `POST /leak-tests` / `DELETE /leak-tests/current`、取消整定 `DELETE …/autotune`。
- 启用 / 停用 → `POST /channels/{id}/enable|disable`。界面原来用 `installed` 表示「启用」，binding 里把 `state == idle` 的路的 `installed` 置 false，页面不用改。
- 「复制到全部」→ `POST /channels/copy-config {from, to}`，`to` = 其余启用、没跑程序的路。
- 设置 → `PATCH /settings {revision, values{键: 值}}`：revision 在请求**真正发出时**才取（连着改几项不会互相冲突）；`REVISION_CONFLICT` 时先 `GET /settings` 再重发一次；开关类发 JSON 布尔；`ui.gasWaitMaxMin` / `ui.retentionDays` 在线上叫 `program.gasWaitMaxMin` / `storage.retentionDays`（它们不是界面偏好）。失败时把设置重取一遍，界面上的值回到后台的真实值。
- 程序库：条目按 PC 端形状发（压力 MPa），按 schema 的单位换算；schema 还没取到时用 v0.8 的固定压力字段表。
- 结果：失败 → `on_result(false, 服务端 error.message)`；成功时只有服务端带了 `warnings`（例如「加热仍在按 147.0 °C 运行」）才 `on_result(true, …)`。main.cpp 把 `on_result` 接到 `kit::toast`。导出、WiFi 连接这类 202 的，结果看事件 `export.*` / `network.wifi*` 再弹。
- `quit_app` 只置一个标志，主循环看到就退出（reactord 照常跑）。

## 7. 测试

`reactor-hmi --selftest-net`（`make -C tools/preview selftest-net`），约 3.5 s，94 项，连跑 6 次都全过：

- 单元：SHA-1（abc / 空 / 100 万个 a）、base64（RFC 4648）、WS accept（RFC 6455 示例）、uuid4、ISO 时间（7 位小数、带时区、输出）、URL 编码；
  HTTP 解析（chunked 逐字节喂 + 扩展 + 尾部头、100 Continue、流水线第二条、204、读到断开、正文不完整、垃圾）；地址解析；
  WS（分片中间夹 Ping、70000 字节 127 长度、中文、关闭码、掩码帧 126 长度、服务端发掩码帧 / 控制帧 > 125 / 多余续帧 = 协议错误）。
- HttpConn 对假服务器：chunked 5000 字节、keep-alive 复用同一连接、Bearer 头、读到断开、对端关后自动重连、**服务端重启后旧连接失效 → 重发成功**、POST 的 Content-Length、连不上的错误。
- NetBackend 对假服务器：开始 `connected=false` 且 8 路占位；首次订阅（含 progress、不带 resume）；遥测**分 3 片中间夹 Ping** 进 Model；服务端 Ping → Pong；通道扩展、idle = 停用、bootAt；progress（步骤名、目标「80 °C」、约剩、计划压力 psi→MPa、自整定进度）；一级报警（量值 MPa、已经做了）；事件；设置扩展；schema（chunked）；程序库 MPa→psi；开机事件和实时事件合并去重；趋势（读到断开的响应、null→NaN、MPa）；运行记录 + 详情 + 中止信息；每日文件分组。
- 15 个动作打到的方法、路径、正文、`Idempotency-Key`（36 位）；`PATCH` 冲突后取新 revision 只重发一次、三项最后都成功；失败文案原样回到 `on_result`；成功的 warnings 也回来。
- 断线 → `connected=false` → 自动重连 → 重订阅带 `resumeAfterEventSeq=10, seqEpoch=1` → 第一帧遥测 `connected=true`；`gap` → `GET /events?afterSeq=10` + `GET /snapshot` → 补到 12 → `subscribe` resume 12；服务端发掩码帧 → 断开重连；后台整个停掉 → 盖层写「连不上：No such file or directory」→ 后台重新起来自动连上。

另外手工核对：
- `make -C tools/preview shots`（`--sim`）照常出 63 张；和 `doc/hmi-shots/` 比，除了 6 张都逐字节相同，这 6 张是带动画的主页类截图（`main-f1/f2`、`stop-all`、`abort-ask`、`alarm-l2`、`kbd-text`），同一个程序连截两次本来就差几十到几百个像素（桨叶 / 气泡的时刻）。
- 连一个不存在的 socket 把 63 页各截一张（`--wait-ms 0`）：空 Model 下没有页面崩，主页盖着「正在连接后台服务…」。
- 连 `--fake-serve` 截主页 / 记录页：数据从假后台进来（1 号 78.5 °C、程序「加氢筛选-A 第 5/9 步」、运行记录的超压块 2.60 MPa）。
- 新文件单独用 `-Wall -Wextra -Wpedantic`（不加 `-Wno-unused-parameter`）过了一遍：无警告。`main.cpp` 按 X11 后端做了 `-fsyntax-only`。

没测的：15 s 无数据判断断线（要等 15 s，逻辑简单，没放进自检）；真 reactord（api 阶段还没做）；GCC 9.4 / aarch64（本机只有 Apple clang）；SDL / X11 窗口里实际点按。

## 8. 开放问题 / 需要别的模块配合

1. **api 阶段**按 `doc/接口契约-新增.md` 实现。其中两项 C# 设计稿里有、但 C# 实现里没有，也算新增：`GET /channels/{id}/program`、`PATCH /settings`。
2. **store**：`AppSettings.language` 默认值是 `"zh-CN"`，契约里语言码是 `zh / en / …`（`PUT /system/language`、快照 `system.languageCode`）。请 store 改默认值为 `"zh"`，或 api 层做映射；否则触屏第一次连上会看到 `zh-CN`，不认识就保持当前语言（不会出错，但英文界面重启后不会自动回来）。
3. **store / api**：新设置项 `ui.*`（5 个）、`storage.retentionDays` 要进 settings.json；`program.gasWaitMaxMin` 是已有的 `ProgramGasWaitMin`，只是以前没进 `SettingsDto`。
4. **控制申请**：设计文档 §5.4 的「允许 / 拒绝 PC 的控制申请」界面还没有弹窗（`Actions` 里也没有方法），所以契约里没写。要做的话：WS `control` 消息里 `event: requested` 时弹窗，调 `POST /control/requests/{id}/grant|deny`。
5. **快照扩展 `revs`**：是我为了「不轮询」加的。后端如果暂时不做，触屏退回定时刷新，可以用，只是数据晚 10–60 s。
6. **progress 的推送频率**：契约写的是每 Tick 整份推（8 路带计划曲线，约 3–6 KB）。如果后端觉得多，可以改成「有变化才推 + 每 10 s 至少一次」，触屏不用改。
7. **退出时**：cmd / read 线程如果正卡在一个长请求上（趋势最多 20 s 超时），`stop()` 要等它超时才返回。现在只有「设置 · 退出程序」会走到，影响不大。
8. **泄漏测试历史的 `pass: null`**（没测完）现在按「通过」显示，等 api 定了再看要不要单独显示「未完成」。
9. **Unix 入口的 WS 名额**（net 报告开放问题 2）：触屏只开 1 条 WS + 2 条 HTTP，默认 4 / 16 够用。
10. **CMake**：没改。`net/` 下的 `.cpp` 由 `GLOB_RECURSE` 自动收进 `reactor-hmi`，线程库经 `lvgl` 的 `Threads::Threads` 传过来。自检和假服务器也编进了界面程序（约 800 行，不跑不占资源）；如果不想让它们进板子上的程序，可以以后在 CMake 里排除 `net/selftest.cpp` 和 `net/fake_server.cpp`，同时去掉 `main.cpp` 里的两个参数。
