# 模块报告 · net（通用 HTTP/1.1 + WebSocket 服务器与安全管道）

命名空间 `reactor::net`。零依赖（标准库 + POSIX + nlohmann/json），macOS clang 下 `-Wall -Wextra -Wpedantic` 无警告，ThreadSanitizer / AddressSanitizer 下测试全绿。
**没有领域端点**：这里只提供服务器、路由、鉴权、白名单、限流、幂等表、WS 传输和背压，`api/` 往里挂路由。

## 1. 文件

| 文件 | 内容 | C# 出处 |
|---|---|---|
| `src/net/crypto.hpp/.cpp` | SHA-1、SHA-256、base64、hex、定时比较（全部自己实现） | `TokenAuth.Hash`、`CryptographicOperations.FixedTimeEquals` |
| `src/net/api_config.hpp/.cpp` | `ApiConfig` / `ApiToken`、`load_api_config`、`parse_api_config`、`apply_api_env` | `Api/ApiConfig.cs` |
| `src/net/api_errors.hpp/.cpp` | 通用错误码常量、`status_of`（码→HTTP）、`error_body` | `Api/ApiErrors.cs`、`Contracts/.../ApiErrorStatus.cs` |
| `src/net/api_headers.hpp` | `X-Control-Lease`、`Idempotency-Key` 等头名常量 | `Contracts/.../ApiHeaders.cs` |
| `src/net/auth.hpp/.cpp` | `Role`、`AuthContext`、`TokenTable`（Bearer 校验）、`KeyedThrottle`、`FailureLimiter` | `Api/TokenAuth.cs` |
| `src/net/cidr.hpp/.cpp` | `IpAddr`、`parse_ip`、`CidrMatcher` | `Api/Cidr.cs` |
| `src/net/rate_limit.hpp/.cpp` | `RateClass`、`FixedWindowLimiter`、`ConcurrencyLimiter`、各限额常量 | `Api/ApiHost.cs` 189–247 |
| `src/net/idempotency.hpp/.cpp` | `IdempotencyStore`（begin/complete/abandon/wait/acquire） | `Api/IdempotencyStore.cs` + `Endpoints.Write.cs` 第 3 步 |
| `src/net/http.hpp/.cpp` | `Headers`、`Request`、`Response`、`parse_request`、`serialize_head`、URL 编解码、`file_response`（Range + 弱 ETag）、`Responder` / `StreamWriter` 声明 | `Endpoints.FileResult` |
| `src/net/router.hpp/.cpp` | `Router`（路径参数、`{*rest}`、方法匹配、405、兜底、`local_only`、WS 路由） | `Endpoints.cs` MapFallback |
| `src/net/websocket.hpp/.cpp` | 握手 accept key、帧编解码、分片重组 `MessageAssembler`、UTF-8 校验 | — |
| `src/net/security.hpp/.cpp` | `Gate`（网关）、`ApiStats`、`AuditFn` | `ApiHost.GateAsync` + 限流管线 |
| `src/net/server.hpp/.cpp` | `Server`、`ServerOptions`、`WsSession`、`WsQueue`，Responder / StreamWriter 实现，poll 循环、线程池 | `ApiHost.cs`、`WsHub.cs`（传输 / 背压部分） |
| `tests/net/net_test_client.hpp` | 测试用最小 HTTP / WS 客户端 | — |
| `tests/net/test_net_crypto.cpp` | SHA-1 / SHA-256 / base64 标准向量、accept key（RFC 6455 示例） | — |
| `tests/net/test_net_security.cpp` | 鉴权、CIDR、配置、幂等、限流、错误码 | 逐条翻译 `TokenAuthTests` `CidrTests` `ApiConfigTests` `IdempotencyStoreTests` |
| `tests/net/test_net_http.cpp` | 解析器、路由、WS 编解码、文件 Range / ETag | — |
| `tests/net/test_net_server.cpp` | 真起服务器的集成测试 | 参考 `ApiHostIntegrationTests` |

## 2. 公开接口：api/ 怎么用

### 2.1 起服务器

```cpp
#include "net/server.hpp"
using namespace reactor::net;

std::string err;
ApiConfig cfg = load_api_config(dir + "/api.json", &err);       // err 非空 → 记事件「接口配置读取失败 · 按默认（未启用）」
apply_api_env(cfg, [](const char* k) { return std::getenv(k); });

ServerOptions opt = server_options_from_config(cfg);           // bind / httpPort / maxClients
opt.tcp_enabled = cfg.enabled;                                  // enabled=false：只开 Unix 入口（触屏照常可用）
opt.unix_path = "/run/reactord/hmi.sock";
opt.gate.unix_allowed_uids = {hmi_uid};                         // 只认 reactor-hmi 的系统用户
opt.audit = [&](auto& level, auto& title, auto& detail) { events.append_async(level, title, detail); };  // I/O 线程调用，必须不阻塞

Server srv(opt, cfg);
register_routes(srv.router(), ...);                             // 必须在 start() 前登记完
if (!srv.start(&err)) { /* Unix 入口失败才会 false；TCP 失败会后台每 10 s 重试 */ }
...
srv.stop();                                                     // 退出路径；不要在工作线程 / I/O 线程里调
```

C# `TryStart` 里那几条配置警告（没令牌、autoGrantRemote、HTTPS 未实现、`cfg.warnings`）由 api 层自己记；net 只记传输相关的：白名单无效条目、绑定失败（首次后每 10 min 一条）、启动 / 停止、鉴权失败（每 IP 每分钟一条）、WS 连接 / 断开（每「令牌@IP」10 s 一条，带省略条数）。

### 2.2 登记路由

```cpp
Router& r = srv.router();
// 同步处理器：直接返回响应（在工作线程上跑）
r.get("/api/v1/health", [](const Request&) { return Response::json(200, health_json()); },
      RouteOptions{RateClass::None, /*no_auth=*/true});
r.get("/api/v1/channels/{id}", [](const Request& q) {
    int id = std::stoi(q.param("id"));   // 路径参数已百分号解码
    ...
});
r.put("/api/v1/channels/{id}/valve", handler);                 // Auto：非 GET → 写分区（全局 50/s + writeRateLimitPerSec）
r.post("/api/v1/estop", handler, RouteOptions{RateClass::Estop});
r.post("/api/v1/control/heartbeat", handler, RouteOptions{RateClass::Heartbeat});
r.get("/api/v1/preflight", handler, RouteOptions{RateClass::Preflight});
r.get("/api/v1/files/{type}/{name}", handler, RouteOptions{RateClass::Download});
r.patch("/api/v1/settings", handler, RouteOptions{RateClass::Auto, false, /*local_only=*/true});   // TCP 上 = 404
r.set_fallback(...);   // 可选；不设时 404 {code:NOT_FOUND, message:"没有这个端点"}
```

- 字面段大小写不敏感、末尾 `/` 忽略（同 ASP.NET）；字面段多的模式优先。
- HEAD 自动匹配 GET 路由，服务器去掉正文、保留 Content-Length。
- 路径存在但方法不对 → 405 + `Allow`；`method_mismatch_as_404 = true` 时改走兜底 404（见 §8 问题 1）。

**处理器里能拿到的**（`const Request& q`）：`method`、`path`（已解码）、`raw_path`、`query`（`query_param("afterSeq")`）、`headers`（大小写不敏感，`q.header("X-Control-Lease")`）、`body`（已按 Content-Length 或 chunked 收齐，≤ 64 KB）、`params`、`peer`（`transport`、`ip_text`、`port`；Unix 上 `uid` / `pid` / `gid`）、`auth`（见 2.6）、`rate_class`、`conn_id`。

**响应**：`Response::json(200, j)`、`Response::json(202, str)`、`Response::text(...)`、`Response::empty(204)`、`Response::error(code, message, detail)`（状态码按 C# 表自动定；`RATE_LIMITED` / `DOWNLOAD_BUSY` 自动带 `Retry-After: 1`）。写命令的外层 `{ok, opId, status, result, warnings, opEventSeq}` 由 api 层拼，net 不管。

### 2.3 延迟响应（处理器把等待交给别的线程）

```cpp
r.add("POST", "/api/v1/run", [&ctl](const Request& q, Responder res) {
    ctl.post(Command{...}, [res](CommandOutcome o) {     // 控制线程上回调
        res.send(Response::json(o.http, o.body));          // 任意线程都可以回
    });
});                                                        // 处理器立即返回，工作线程被还回池子
```

- `Responder` 可复制、可跨线程；只有第一次 `send` / `stream` 生效（返回 false 表示已回过或连接已断）。
- 所有副本都销毁仍未回复 → 服务器自动回 500 `INTERNAL`「处理器未返回响应」，连接不会挂住。处理器抛异常 → 500 `INTERNAL`「处理器异常：…」。
- `res.connection_closed()` 可用于提前放弃慢操作。

### 2.4 文件下载与流式输出

```cpp
// 单文件（/files/{type}/{name}）：Range、If-None-Match → 304、If-Range、416、Content-Disposition 都处理好
r.get("/api/v1/files/{type}/{name}", [&](const Request& q) {
    auto path = safe_path(data_dir, q.param("type"), q.param("name"));   // 路径安全检查是 api/store 的事（C#: SafePath）
    if (!path) return Response::error(codes::kNotFound, "文件不存在或名称不合法");
    return file_response(q, *path, "text/csv; charset=utf-8", q.param("name"));
}, RouteOptions{RateClass::Download});

// 拼接日 CSV（/telemetry?from&to）：chunked 流，带背压
r.add("GET", "/api/v1/telemetry", [](const Request& q, Responder res) {
    Headers h; h.set("Content-Type", "text/csv; charset=utf-8");
    auto w = res.stream(200, std::move(h));        // content_length = -1 → chunked；HTTP/1.0 客户端自动改成「以断开结束」
    for (...) if (!w->write(chunk)) return;        // 缓冲 > 256 KB 时阻塞；对端断开或 10 s 发不动 → false
    w->end();                                       // 不 end() 就销毁 → 连接直接断（客户端能看出响应不完整）
}, RouteOptions{RateClass::Download});
```

弱 ETag 格式与 C# 相同：`W/"<长度hex>-<mtime .NET ticks hex>"`。If-Range 带弱 ETag 时按 RFC 强比较不成立 → 忽略 Range（与 ASP.NET 一致）。下载并发许可（2 个）一直占到响应全部发完。

### 2.5 WebSocket

```cpp
WsHandlers h;
h.on_open = [&](const WsSessionPtr& s, const Request& q) {
    s->user = std::make_shared<SubState>();                              // 订阅开关等，自己定义
    s->send(hello_json(), WsQueue::Event);                               // on_open 里发的一定是第一条
    if (auto f = snapshots.current()) s->send(f->json, WsQueue::Telemetry);
};
h.on_message = [&](const WsSessionPtr& s, const std::string& text, bool binary) { /* subscribe / resync / ping / hb */ };
h.on_close   = [&](const WsSessionPtr& s, uint16_t code, const std::string& reason) { /* reason 是中文断开原因 */ };
r.ws("/api/v1/ws", h);

// 推送（任意线程，不阻塞）：同一份序列化结果投给所有连接
auto msg = std::make_shared<const std::string>(frame_json);
srv.ws_broadcast(msg, WsQueue::Telemetry, [](const WsSession& s) { return sub_of(s).telemetry; });
srv.ws_broadcast(event_json, WsQueue::Event, [](const WsSession& s) { return sub_of(s).events; });
```

- 回调在工作线程上跑，**同一连接串行**：`on_open` → `on_message`… → `on_close`（必定调用一次，包括服务器停止时）。`on_open` 返回之后连接才进入 `ws_broadcast` 范围。
- 握手前已过网关（白名单、Bearer、限流）；只认 `Authorization` 头。名额 `maxClients`（默认 4）**先占位再握手**，超了 503 `TOO_MANY_CLIENTS`；Unix 入口单独计数（`ws_max_clients_local`，默认 4），触屏不和 PC 抢。
- 背压（C# WsHub 原样）：`WsQueue::Telemetry` 容量 50，满了丢最旧（`dropped_telemetry_frames` +1，不断开）；`WsQueue::Event` 容量 2000，满了以 1008 断开（`close_reason` =「事件队列溢出（慢客户端）」，线上 reason `resync required`）；事件队列优先发送；单条消息 10 s 发不完 → 断开（「发送超时 10 s（慢客户端）」）；入站消息 > 64 KB → 1009 断开（「消息过大」）；未加掩码 / 坏帧 → 1002；非法 UTF-8 → 1007。
- 自动处理：Ping → Pong；关闭握手；每 5 s 主动发空 Pong 保活（同 Kestrel `KeepAliveInterval`，客户端不用回）。
- `s->close(code, reason)` 主动断开；`srv.update_config(cfg)` 时已吊销令牌的连接以 1008 断开。
- C# 的「中枢事件队列」在这里不存在：`ws_broadcast` 直接投到每个连接的队列（加锁 push，微秒级），扇出不需要单独的线程，所以也没有「中枢溢出→全部 1008」这条路径；单个连接的溢出规则照旧。

### 2.6 身份（`AuthContext`）

| 字段 | TCP | Unix |
|---|---|---|
| `ok` | 通过 Bearer 校验 | true |
| `local` | false | **true** |
| `name` | 令牌名（限流分区、幂等键、审计都用它） | `"local"` |
| `role` | viewer / operator / admin | admin |
| `estop` | 令牌 estop 位（null 时 operator/admin 默认有） | true |

`q.auth.has_role(Role::Operator)`。写命令管线第 1 步（角色 / estop 位，「停止类 = operator 或 estop」）由 api 层按 C# `CommandAsync` 写；net 只保证 `auth` 可信。`no_auth` 路由上 TCP 请求的 `auth.ok = false`。

### 2.7 幂等

```cpp
std::string key = q.header(headers::kIdempotency);
if (key.empty()) { if (idem_required) return 428 PRECONDITION_REQUIRED; }
else {
    if (key.size() > kIdemKeyMaxLen) return Response::error(codes::kBadRequest, "Idempotency-Key 过长（≤ 128 字符）");
    auto a = store.acquire(IdempotencyStore::scoped_key(q.auth.name, key),
                           IdempotencyStore::hash(q.method, q.path, q.body), /*op_id=*/key, reactor::mono_ms());
    switch (a.kind) {
        case IdemBegin::Replay:     /* 回放 a.entry->status / body，加头 Idempotent-Replay: true */
        case IdemBegin::Mismatch:   /* 409 IDEMPOTENCY_MISMATCH */
        case IdemBegin::InProgress: /* 等满 5 s 仍未完成：409 IDEMPOTENCY_IN_PROGRESS，opId = a.entry->op_id */
        case IdemBegin::New:        /* 执行；成功 store.complete(a.entry, status, body)，没登记结果 store.abandon(a.entry) */
    }
}
```

`acquire` 会阻塞最多 5 s（等同键的进行中请求），只在工作线程上调。原请求 `abandon` 时等待方会被唤醒并重新登记。TTL 10 min、容量 1000（满了先清过期、再淘汰最旧）、只在内存。

### 2.8 统计

`srv.stats()`（`ApiStats`，全部原子）：`clients`、`dropped_telemetry_frames`、`dropped_events`（保留字段，见 2.5 末尾，恒 0）、`frames_sent`、`events_sent`、`auth_failures`、`requests`。每连接：`s->dropped_telemetry()`、`frames_sent()`、`events_sent()`。

## 3. 线程约定

| 线程 | 个数 | 做什么 | 不做什么 |
|---|---|---|---|
| I/O 线程 | 1 | poll、accept、读写、HTTP 解析、网关（白名单 / SHA-256 鉴权 / 限流）、WS 编解码与背压、超时、文件按 64 KB 块 `pread` | 从不调用业务代码；审计钩子是唯一例外（必须不阻塞） |
| 工作线程 | `worker_threads`（默认 4） | 路由处理器、WS 回调（同连接串行）、`Server::submit` 的任务 | — |
| 任意线程 | — | `Responder::send/stream`、`StreamWriter::write/end`、`WsSession::send/close`、`Server::ws_broadcast/update_config/ws_sessions/stats` | `Router` 登记（只能在 `start()` 前） |

- 处理器**可以**阻塞（等控制线程回结果、`IdempotencyStore::acquire`），但池子共享：同时阻塞的处理器数 ≥ `worker_threads` 时其他请求排队。长等待请用 `Responder` 延迟回复。
- 跨线程投递：内部 mutex 队列 + 自管道唤醒 I/O 线程（合并唤醒，不会写满管道）。
- 停止：`stop()` → I/O 线程给 WS 发 1001、关全部连接、为每条 WS 排 `on_close` → 线程池跑完剩下的任务再退出 → 删除 Unix socket 文件。之后 `Responder::send` 等安全地变成空操作（内部用 weak_ptr，Responder 活得比 Server 久也没事）。
- SIGPIPE：Linux 用 `MSG_NOSIGNAL`，macOS 每个 socket 设 `SO_NOSIGPIPE`；不改进程信号处理。

## 4. 网关顺序（每个请求，I/O 线程）

**TCP**：`requests++` → 路由匹配（只为拿到分区 / 免鉴权标志）→ 白名单（403 `IP_NOT_ALLOWED`「<ip> 不在允许的网段内」+ 断开）→ 前缀 `/api/v1`（404「接口前缀为 /api/v1」）→ `no_auth` 路由跳过鉴权 → `?access_token=`（401）→ Bearer（401 + `WWW-Authenticate: Bearer`；同 IP 每秒失败 > 10 次后 429，先校验再判限速，合法令牌不受影响）→ 限流：除 `None` 外都先吃全局 50/s（按 `tok:<名>`），再吃路由分区（写 N/s、estop 5/s、heartbeat 10/s、preflight 2/s、下载并发 2）→ 405 / 404 兜底 / WS 升级 / 处理器。

**Unix**：uid 白名单（`opt.gate.unix_allowed_uids`，空 = 不限；不在名单 → 403 + 断开）→ 前缀 → 身份 = 本地 → 不鉴权、不限流。

## 5. HTTP 细节

- 请求头 ≤ 16 KB（431）、正文 ≤ 64 KB（413，同 Kestrel `MaxRequestBodySize`）；Content-Length 与 chunked 正文都支持（.NET `PostAsJsonAsync` 会发 chunked）；`Expect: 100-continue`；流水线请求按顺序处理；HTTP/1.0 支持（默认关闭连接）。
- 解析错误 → JSON 错误 + 断开。CL 与 TE 同时出现、CL 不一致、折行头 → 400（防请求走私）。
- keep-alive 空闲 130 s（同 Kestrel）、请求头 10 s 内收齐、响应 10 s 发不动 → 断开。TCP 最多 64 条连接（同 Kestrel `MaxConcurrentConnections`），Unix 16 条，超了直接关。
- 响应自动加 `Date`、`Content-Length` / `Transfer-Encoding`、`Connection`；不加 `Server` 头（同 C# `AddServerHeader = false`）。

## 6. 测试

`make -C tools/build -j8 test`，组名都以 `net_` 开头（`run_tests net_` 只跑本模块）：68 个用例，约 5 s。全量用例全绿、无警告。另用 `-fsanitize=thread` / `-fsanitize=address` 单独编过一遍，均无报告。

- `net_crypto`：SHA-1（空串、abc、448 位、100 万个 a、quick brown fox）、SHA-256（同上 + 55/56/64 字节填充边界）、base64（RFC 4648 全部向量 + 非法输入 + 0..255 往返）、定时比较、WS accept key（RFC 6455 示例）。
- `net_auth` / `net_cidr` / `net_config` / `net_idem`：C# 四个测试类逐条翻译（输入和期望照抄），外加配置修正规则 / 类型错误 / 尾逗号注释、`acquire` 的等待 / 重放 / 撤销重登记。
- `net_http` / `net_router` / `net_ws`：增量解析、chunked、各种 400/413/431/505、URL 解码、路由优先级 / 参数 / 405 / HEAD / local_only、帧往返（0/125/126/65535/65536/70000 字节）、分片夹 ping、超限、未掩码、坏 UTF-8、关闭帧；文件 206 / 后缀范围 / 416 / 多段忽略 / 304 / If-Range。
- `net_server`（真 socket）：鉴权与 401 / `access_token`、前缀 / 404 / 405 / 路径参数、CIDR 拒绝并断开 + 无效条目审计、IPv6 双栈（`::1` 与 v4-mapped）、鉴权失败限速（10 次 401 后 429，合法令牌照常）、全部限流分区（estop / heartbeat / preflight / 写 / 读 50 / health 不限 / 按令牌隔离）、下载并发 2、流水线 + chunked 请求 + 100-continue + 413 + Connection: close + 垃圾请求、延迟响应 / 忘了回复 → 500 / 抛异常 → 500、文件 Range / HEAD / 304 / 416、20000 行 chunked CSV 流、固定长度流、幂等全流程（重放头、mismatch、跨令牌隔离、428、键过长、同键并发等待后重放）、Unix socket（本地身份、uid / pid、权限 0660、local_only 路由、uid 白名单 403、第二实例拒绝、残留 socket 清理、非 socket 文件不删）、WS（握手 accept、hello 第一条、回显、分片夹 ping、二进制、中文、客户端关闭、服务端关闭、64 KB 超限 1009、未掩码 1002、无令牌 401、非升级 400、版本 400、maxClients 503 与名额释放、广播与过滤、Unix 单独名额、事件队列溢出 1008、遥测丢最旧不断开、发送超时、令牌吊销 1008、stop 时 1001 + on_close）。

## 7. 限制

1. 只有 HTTP（无 TLS），与 C# P2 一致；`httpsPort / certPath` 只读入。
2. 对端半关闭（`shutdown(SHUT_WR)` 后等响应）会被当成断开，响应丢弃。浏览器 / .NET / curl 都不这么做。
3. 文件块在 I/O 线程上 `pread`（64 KB / 次），SD 卡很慢时会让 I/O 线程卡几毫秒。下载并发只有 2，实际影响很小。
4. 不支持多段 Range（按 ASP.NET 忽略 → 200 全文）、不支持 `Content-Encoding` 压缩、不支持 WS 扩展（permessage-deflate）和子协议。
5. Unix socket 先 `bind` 后 `chmod`，中间有极短的窗口用默认 umask 权限；部署靠 `/run/reactord`（0750）目录权限兜底。
6. 改白名单（`update_config`）不断开已建立的连接，只影响新请求；吊销令牌会断开该令牌的 WS。
7. 没在 Linux GCC 9.4 / aarch64 上实际编过（本机只有 clang）。代码只用 POSIX + C++17，Linux 分支（`SO_PEERCRED`、`MSG_NOSIGNAL`）有 `#if` 区分；请板子上第一次编译时留意。

## 8. 问题和需要别的模块提供的东西

1. **405 与 C# 不同**：ASP.NET 有 `MapFallback` 时，方法不对会落进兜底 → 404「没有这个端点」。按任务要求默认回 405 `METHOD_NOT_ALLOWED` + `Allow`；要逐字兼容 PC 端就设 `ServerOptions::method_mismatch_as_404 = true`（已实现、有测试）。请 api 层决定用哪个，我建议开启（PC 端兼容是验收标准）。
2. **触屏的 WS 名额**：C# 的 `maxClients` 只针对远程；这里 Unix 入口单独计数（默认 4）。需要的话改成无限或别的数。
3. **本地入口不限流**：Unix 上不吃任何限流分区（包括下载并发 2）。触屏「导出到 U 盘」走别的端点，应无影响；如需限制请说。
4. **`unix_allowed_uids` 的值**：需要部署方（systemd / 安装脚本）给出 reactor-hmi 的 uid（或改成按 gid 判断）。现在空列表 = 不限，开发机方便。
5. **`X-Client-Version` / 426 `CLIENT_TOO_OLD`**、`STARTING`（首个快照前 503）、写命令外层信封、opEvent、`hello` / `subscribe` / `resync` / `gap` 等 WS 消息语义，都属于 api 层；net 只提供常量和传输。
6. **审计钩子**在 I/O 线程上调用，事件存储（store/）需要提供一个不阻塞的追加入口（入队即返回）。
7. **路径安全**（C# `SafePath`）和日 CSV 拼接（`HistoryFiles.WriteRangeAsync`）由 api / store 实现，用本模块的 `file_response` 和 `Responder::stream` 输出。
