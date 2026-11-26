// 命令层的小工具：随机 id、客户端名清洗。任意线程可用。
#pragma once
#include <string>

namespace reactor::cmd {

// n 字节密码学随机数的小写十六进制（/dev/urandom；读不到时退回 std::random_device）。C#: RandomNumberGenerator + ToHexString
std::string random_hex(int bytes);
// GUID v4 文本（C# Guid.NewGuid().ToString()：8-4-4-4-12 小写）
std::string new_guid();

// 客户端名的权威清洗（C#: Contracts/.../ClientNames.Sanitize）：去掉控制 / 格式字符（含零宽、双向控制符）、行 / 段分隔符、
// BMP 之外的字符（C# 里是代理项）、私用区；空白折叠成一个空格并去首尾；截到 64 个 UTF-16 码元。
constexpr int kClientNameMax = 64;
std::string sanitize_client_name(const std::string& name);

}  // namespace reactor::cmd
