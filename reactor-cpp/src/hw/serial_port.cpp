#include "hw/serial_port.hpp"

#include <cerrno>
#include <chrono>
#include <cstring>
#include <fcntl.h>
#include <poll.h>
#include <termios.h>
#include <unistd.h>

namespace reactor::hw {

namespace {
bool to_speed(int baud, speed_t& out) {
    switch (baud) {
        case 1200: out = B1200; return true;
        case 2400: out = B2400; return true;
        case 4800: out = B4800; return true;
        case 9600: out = B9600; return true;
        case 19200: out = B19200; return true;
        case 38400: out = B38400; return true;
        case 57600: out = B57600; return true;
        case 115200: out = B115200; return true;
        default: return false;
    }
}
}  // namespace

SerialPort::~SerialPort() { close(); }

bool SerialPort::open(const SerialConfig& cfg) {
    close();
    speed_t sp;
    if (!to_speed(cfg.baud, sp)) { err_ = "不支持的波特率 " + std::to_string(cfg.baud); return false; }
    fd_ = ::open(cfg.path.c_str(), O_RDWR | O_NOCTTY | O_NONBLOCK);
    if (fd_ < 0) { err_ = "打不开 " + cfg.path + "：" + std::strerror(errno); return false; }

    termios tio{};
    if (tcgetattr(fd_, &tio) != 0) { err_ = std::string("tcgetattr 失败：") + std::strerror(errno); close(); return false; }
    cfmakeraw(&tio);
    cfsetispeed(&tio, sp);
    cfsetospeed(&tio, sp);
    tio.c_cflag |= (CLOCAL | CREAD);
    tio.c_cflag &= ~CSIZE;
    tio.c_cflag |= (cfg.data_bits == 7 ? CS7 : CS8);
    tio.c_cflag &= ~(PARENB | PARODD);
    if (cfg.parity == 'E') tio.c_cflag |= PARENB;
    if (cfg.parity == 'O') tio.c_cflag |= (PARENB | PARODD);
    if (cfg.stop_bits == 2) tio.c_cflag |= CSTOPB; else tio.c_cflag &= ~CSTOPB;
#ifdef CRTSCTS
    tio.c_cflag &= ~CRTSCTS;
#endif
    tio.c_cc[VMIN] = 0;
    tio.c_cc[VTIME] = 0;
    if (tcsetattr(fd_, TCSANOW, &tio) != 0) { err_ = std::string("tcsetattr 失败：") + std::strerror(errno); close(); return false; }
    tcflush(fd_, TCIOFLUSH);
    err_.clear();
    return true;
}

void SerialPort::close() {
    if (fd_ >= 0) { ::close(fd_); fd_ = -1; }
}

void SerialPort::flush_input() {
    if (fd_ >= 0) tcflush(fd_, TCIFLUSH);
}

bool SerialPort::write_all(const std::vector<uint8_t>& data) {
    size_t off = 0;
    while (off < data.size()) {
        ssize_t n = ::write(fd_, data.data() + off, data.size() - off);
        if (n < 0) {
            if (errno == EAGAIN || errno == EINTR) { pollfd p{fd_, POLLOUT, 0}; ::poll(&p, 1, 50); continue; }
            err_ = std::string("写串口失败：") + std::strerror(errno);
            return false;
        }
        off += static_cast<size_t>(n);
    }
    tcdrain(fd_);  // 等发完再开始计回包超时
    return true;
}

std::vector<uint8_t> SerialPort::read_up_to(size_t n, int timeout_ms) {
    std::vector<uint8_t> out;
    out.reserve(n);
    auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(timeout_ms);
    uint8_t buf[256];
    while (out.size() < n) {
        auto left = std::chrono::duration_cast<std::chrono::milliseconds>(deadline - std::chrono::steady_clock::now()).count();
        if (left <= 0) break;
        pollfd p{fd_, POLLIN, 0};
        int pr = ::poll(&p, 1, static_cast<int>(left));
        if (pr < 0) { if (errno == EINTR) continue; err_ = std::string("poll 失败：") + std::strerror(errno); break; }
        if (pr == 0) break;
        size_t want = n - out.size();
        ssize_t got = ::read(fd_, buf, want < sizeof buf ? want : sizeof buf);
        if (got < 0) { if (errno == EAGAIN || errno == EINTR) continue; err_ = std::string("读串口失败：") + std::strerror(errno); break; }
        out.insert(out.end(), buf, buf + got);
    }
    return out;
}

}  // namespace reactor::hw
