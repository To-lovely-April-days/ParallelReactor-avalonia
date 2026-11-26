// reactor-hmi --selftest-net：网络层自检（不需要后端、不需要屏幕）。
// 单元部分：SHA-1 / base64 / accept key、ISO 时间、HTTP 解析（定长 / chunked / 读到断开 / 100 Continue / 流水线）、
// WS 编解码（分片夹 Ping、126 / 127 长度、掩码规则、关闭码）。
// 集成部分：起一个假服务器（net/fake_server），让 NetBackend 连上去：订阅、遥测 / progress / 报警 / 事件进 Model、
// 动作打到对的端点（方法、路径、正文、Idempotency-Key）、错误文案回到 on_result、REVISION_CONFLICT 重试、
// 断线 → connected=false → 自动重连 → 带 resumeAfterEventSeq 重订阅、gap → REST 补事件后重订阅、服务端 Ping。
// 返回 0 = 全部通过。
#pragma once
#include <string>

namespace reactor::hmi::net {
int selftest_main();
// reactor-hmi --fake-serve PATH [秒数]：开发用的假后台（同一份罐头数据），每 1.2 s 推一帧遥测 + progress，
// 让界面在没有 reactord 时也能按「连后台」模式跑：另开一个 reactor-hmi --server unix:PATH。seconds ≤ 0 = 一直跑。
int fake_serve_main(const std::string& path, int seconds);
}
