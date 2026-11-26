#include "ctl/ctl_sd_notify.hpp"

#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>

#include <cstddef>
#include <cstdlib>
#include <cstring>

namespace reactor::ctl {

bool sd_notify(const std::string& state) {
    const char* path = std::getenv("NOTIFY_SOCKET");
    if (!path || !*path) return false;
    size_t len = std::strlen(path);
    sockaddr_un addr;
    std::memset(&addr, 0, sizeof(addr));
    addr.sun_family = AF_UNIX;
    if (len >= sizeof(addr.sun_path)) return false;
    std::memcpy(addr.sun_path, path, len);
    if (addr.sun_path[0] == '@') addr.sun_path[0] = '\0';  // 抽象命名空间
    socklen_t alen = static_cast<socklen_t>(offsetof(sockaddr_un, sun_path) + len);
    int fd = ::socket(AF_UNIX, SOCK_DGRAM, 0);
    if (fd < 0) return false;
    ssize_t n = ::sendto(fd, state.data(), state.size(), 0, reinterpret_cast<const sockaddr*>(&addr), alen);
    ::close(fd);
    return n == static_cast<ssize_t>(state.size());
}

bool sd_watchdog_enabled() {
    const char* us = std::getenv("WATCHDOG_USEC");
    return us && std::atoll(us) > 0;
}

}  // namespace reactor::ctl
