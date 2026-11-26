#include "api/api_system.hpp"

#include <dirent.h>
#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <sys/stat.h>
#include <sys/statvfs.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <unistd.h>

#include <algorithm>
#include <cerrno>
#include <cstdlib>
#include <chrono>
#include <cstring>

#include "common/clock.hpp"
#include "store/store_util.hpp"
#include <cstdint>
#include <optional>
#include <string>
#include <vector>

extern "C" char** environ;

namespace reactor::api {

namespace {
// 在父进程里按 PATH 找到可执行文件（子进程 fork 之后只做异步信号安全的调用）
std::string resolve_exe(const std::string& name) {
    if (name.find('/') != std::string::npos) return name;
    const char* path = std::getenv("PATH");
    std::string p = path ? path : "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";
    size_t pos = 0;
    while (pos <= p.size()) {
        size_t e = p.find(':', pos);
        if (e == std::string::npos) e = p.size();
        std::string dir = p.substr(pos, e - pos);
        if (dir.empty()) dir = ".";
        std::string full = dir + "/" + name;
        if (access(full.c_str(), X_OK) == 0) return full;
        pos = e + 1;
    }
    return "";
}
}  // namespace

ProcResult run_process(const std::vector<std::string>& argv, int64_t timeout_ms) {
    ProcResult r;
    if (argv.empty()) return r;
    std::string exe = resolve_exe(argv[0]);
    if (exe.empty()) return r;
    std::vector<std::string> env_s;
    for (char** e = environ; e && *e; ++e)
        if (std::strncmp(*e, "LC_ALL=", 7) != 0 && std::strncmp(*e, "LANG=", 5) != 0) env_s.push_back(*e);
    env_s.push_back("LC_ALL=C.UTF-8");
    env_s.push_back("LANG=C.UTF-8");
    std::vector<char*> envp;
    for (auto& e : env_s) envp.push_back(const_cast<char*>(e.c_str()));
    envp.push_back(nullptr);
    int out_p[2], err_p[2], exec_p[2];
    if (pipe(out_p) != 0) return r;
    if (pipe(err_p) != 0) { close(out_p[0]); close(out_p[1]); return r; }
    if (pipe(exec_p) != 0) { close(out_p[0]); close(out_p[1]); close(err_p[0]); close(err_p[1]); return r; }
    fcntl(exec_p[1], F_SETFD, FD_CLOEXEC);
    std::vector<char*> args;
    for (const auto& a : argv) args.push_back(const_cast<char*>(a.c_str()));
    args.push_back(nullptr);
    pid_t pid = fork();
    if (pid < 0) {
        for (int fd : {out_p[0], out_p[1], err_p[0], err_p[1], exec_p[0], exec_p[1]}) close(fd);
        return r;
    }
    if (pid == 0) {
        dup2(out_p[1], 1);
        dup2(err_p[1], 2);
        int devnull = open("/dev/null", O_RDONLY);
        if (devnull >= 0) dup2(devnull, 0);
        close(out_p[0]);
        close(err_p[0]);
        close(exec_p[0]);
        // 关掉父进程的其他描述符（监听 socket 等）
        for (int fd = 3; fd < 1024; ++fd)
            if (fd != exec_p[1]) close(fd);
        execve(exe.c_str(), args.data(), envp.data());
        int e = errno;
        ssize_t w = write(exec_p[1], &e, sizeof e);
        (void)w;
        _exit(127);
    }
    close(out_p[1]);
    close(err_p[1]);
    close(exec_p[1]);
    int e = 0;
    ssize_t n = read(exec_p[0], &e, sizeof e);
    close(exec_p[0]);
    r.started = n != static_cast<ssize_t>(sizeof e);
    int64_t deadline = reactor::mono_ms() + timeout_ms;
    bool out_open = true, err_open = true;
    char buf[4096];
    while (out_open || err_open) {
        int64_t left = deadline - reactor::mono_ms();
        if (left <= 0) {
            r.timed_out = true;
            break;
        }
        pollfd p[2];
        int np = 0;
        if (out_open) p[np++] = pollfd{out_p[0], POLLIN, 0};
        if (err_open) p[np++] = pollfd{err_p[0], POLLIN, 0};
        int rc = poll(p, static_cast<nfds_t>(np), static_cast<int>(std::min<int64_t>(left, 200)));
        if (rc < 0 && errno != EINTR) break;
        for (int i = 0; i < np; ++i) {
            if (!(p[i].revents & (POLLIN | POLLHUP | POLLERR))) continue;
            ssize_t k = read(p[i].fd, buf, sizeof buf);
            bool is_out = p[i].fd == out_p[0];
            if (k <= 0) {
                if (is_out) out_open = false;
                else err_open = false;
                continue;
            }
            std::string& dst = is_out ? r.out : r.err;
            if (dst.size() < (is_out ? (1u << 20) : (64u << 10))) dst.append(buf, static_cast<size_t>(k));
        }
    }
    close(out_p[0]);
    close(err_p[0]);
    int status = 0;
    if (r.timed_out) {
        kill(pid, SIGKILL);
        waitpid(pid, &status, 0);
        return r;
    }
    // 输出已关闭：等进程退出（最多到 deadline）
    while (true) {
        pid_t w = waitpid(pid, &status, WNOHANG);
        if (w == pid) break;
        if (w < 0 && errno != EINTR) break;
        if (reactor::mono_ms() > deadline) {
            r.timed_out = true;
            kill(pid, SIGKILL);
            waitpid(pid, &status, 0);
            return r;
        }
        usleep(5000);
    }
    if (WIFEXITED(status)) r.exit_code = WEXITSTATUS(status);
    return r;
}

std::optional<bool> probe_clock_synced() {
#ifdef __linux__
    auto r = run_process({"timedatectl", "show", "-p", "NTPSynchronized", "--value"}, 2000);
    if (!r.started || r.timed_out || r.exit_code != 0) return std::nullopt;
    std::string v = r.out;
    while (!v.empty() && (v.back() == '\n' || v.back() == ' ' || v.back() == '\r')) v.pop_back();
    if (v == "yes") return true;
    if (v == "no") return false;
    return std::nullopt;
#else
    return std::nullopt;
#endif
}

std::vector<std::string> nmcli_split(const std::string& line) {
    std::vector<std::string> out(1);
    for (size_t i = 0; i < line.size(); ++i) {
        char c = line[i];
        if (c == '\\' && i + 1 < line.size()) {
            out.back() += line[++i];
            continue;
        }
        if (c == ':') {
            out.emplace_back();
            continue;
        }
        out.back() += c;
    }
    return out;
}

std::vector<WifiNet> parse_wifi_list(const std::string& out) {
    // nmcli -t -f IN-USE,SSID,SIGNAL,SECURITY dev wifi list
    std::vector<WifiNet> nets;
    size_t pos = 0;
    while (pos < out.size()) {
        size_t e = out.find('\n', pos);
        if (e == std::string::npos) e = out.size();
        std::string line = out.substr(pos, e - pos);
        pos = e + 1;
        if (!line.empty() && line.back() == '\r') line.pop_back();
        if (line.empty()) continue;
        auto f = nmcli_split(line);
        if (f.size() < 4) continue;
        WifiNet n;
        n.in_use = f[0] == "*";
        n.ssid = f[1];
        n.signal = std::atoi(f[2].c_str());
        n.security = f[3];
        if (n.ssid.empty()) continue;
        bool merged = false;
        for (auto& x : nets)
            if (x.ssid == n.ssid) {
                x.signal = std::max(x.signal, n.signal);
                x.in_use = x.in_use || n.in_use;
                merged = true;
            }
        if (!merged) nets.push_back(n);
    }
    std::sort(nets.begin(), nets.end(), [](const WifiNet& a, const WifiNet& b) { return a.signal > b.signal; });
    return nets;
}

std::vector<UsbVolume> list_usb(const std::string& root) {
    std::vector<UsbVolume> v;
    auto probe = [&](const std::string& dir, const std::string& label) {
        struct stat st {};
        if (stat(dir.c_str(), &st) != 0 || !S_ISDIR(st.st_mode)) return;
        struct statvfs fs {};
        if (statvfs(dir.c_str(), &fs) != 0) return;
        if (access(dir.c_str(), W_OK) != 0) return;
        UsbVolume u;
        u.path = dir;
        u.label = label;
        u.free_bytes = static_cast<int64_t>(fs.f_bavail) * static_cast<int64_t>(fs.f_frsize);
        u.total_bytes = static_cast<int64_t>(fs.f_blocks) * static_cast<int64_t>(fs.f_frsize);
        v.push_back(u);
    };
    if (!store::dir_exists(root)) return v;
    struct stat rs {};
    stat(root.c_str(), &rs);
    // /media/<卷> 与 /media/<用户>/<卷>：挂载点和 root 不在同一个设备上才算
    // 开发机联调：目录里放一个 .reactor-fake-usb 空文件也算一个 U 盘（板子上的 /media 不会有它）
    auto is_mount = [&](const std::string& d) {
        struct stat st {};
        if (stat(d.c_str(), &st) != 0 || !S_ISDIR(st.st_mode)) return false;
        return st.st_dev != rs.st_dev || store::file_exists(store::join_path(d, ".reactor-fake-usb"));
    };
    std::vector<std::string> names;
    if (DIR* dp = opendir(root.c_str())) {
        while (dirent* de = readdir(dp)) {
            std::string n = de->d_name;
            if (n == "." || n == "..") continue;
            names.push_back(n);
        }
        closedir(dp);
    }
    std::sort(names.begin(), names.end());
    for (const auto& n : names) {
        std::string d = store::join_path(root, n);
        if (is_mount(d)) { probe(d, n); continue; }
        if (DIR* dp = opendir(d.c_str())) {
            std::vector<std::string> sub;
            while (dirent* de = readdir(dp)) {
                std::string m = de->d_name;
                if (m != "." && m != "..") sub.push_back(m);
            }
            closedir(dp);
            std::sort(sub.begin(), sub.end());
            for (const auto& m : sub) {
                std::string dd = store::join_path(d, m);
                if (is_mount(dd)) probe(dd, m);
            }
        }
    }
    return v;
}

}  // namespace reactor::api
