// RS485 串口（POSIX termios，原始模式）。一个对象只给一条总线用，不做线程同步。
#pragma once
#include <cstdint>
#include <string>
#include <vector>

namespace reactor::hw {

struct SerialConfig {
    std::string path;      // 如 /dev/ttyS5
    int baud = 9600;       // 1200..115200
    char parity = 'N';     // 'N' / 'E' / 'O'
    int data_bits = 8;
    int stop_bits = 1;
};

class SerialPort {
public:
    SerialPort() = default;
    ~SerialPort();
    SerialPort(const SerialPort&) = delete;
    SerialPort& operator=(const SerialPort&) = delete;

    // 打开并配置串口；失败返回 false，原因在 last_error()。
    bool open(const SerialConfig& cfg);
    void close();
    bool is_open() const { return fd_ >= 0; }

    void flush_input();                                   // 丢掉输入缓冲里的残留字节
    bool write_all(const std::vector<uint8_t>& data);     // 写完整一帧
    // 读满 n 个字节或超时；返回实际读到的字节（可能少于 n）。
    std::vector<uint8_t> read_up_to(size_t n, int timeout_ms);

    const std::string& last_error() const { return err_; }

private:
    int fd_ = -1;
    std::string err_;
};

}  // namespace reactor::hw
