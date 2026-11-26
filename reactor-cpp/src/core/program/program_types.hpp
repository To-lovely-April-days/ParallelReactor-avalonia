// 程序模块用到的通道状态与 §11 错误码：直接用 reactor::core 的定义（core/reactor_state.hpp、core/op_codes.hpp）。
// reactor::core::program 嵌在 reactor::core 里，所以本模块里不带前缀的 ReactorState / state_code / op_codes::k* / GuardReject
// 就是 core 的那一份。保留这个头只是为了本模块各文件统一包含。
#pragma once
#include "core/op_codes.hpp"
#include "core/reactor_state.hpp"
