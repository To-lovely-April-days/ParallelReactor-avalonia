// 测试用的 TLS 客户端（mbedTLS，阻塞 socket + 超时）：模拟 PC 端——不验 CA，握手后取对方证书的 SHA-256 指纹自己比对。
// 和 net_test_client.hpp 配合：reader() / sender() 填进 WsClient，read_response_io 读 HTTP 响应。
#pragma once
#include <poll.h>
#include <sys/socket.h>
#include <sys/time.h>
#include <unistd.h>

#include <cerrno>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>

#include "mbedtls/ctr_drbg.h"
#include "mbedtls/entropy.h"
#include "mbedtls/ssl.h"
#include "mbedtls/x509_crt.h"
#include "net/tls.hpp"
#include "net_test_client.hpp"
#include "psa/crypto.h"

namespace nettest {

struct TlsClient {
    int fd = -1;
    mbedtls_entropy_context entropy;
    mbedtls_ctr_drbg_context drbg;
    mbedtls_ssl_config conf;
    mbedtls_ssl_context ssl;
    std::string peer_fingerprint;   // 握手时对方给的证书指纹
    std::string version, suite;
    int last_error = 0;

    TlsClient() {
        static std::once_flag once;
        std::call_once(once, [] { (void)psa_crypto_init(); });
        mbedtls_entropy_init(&entropy);
        mbedtls_ctr_drbg_init(&drbg);
        mbedtls_ssl_config_init(&conf);
        mbedtls_ssl_init(&ssl);
    }
    ~TlsClient() {
        close();
        mbedtls_ssl_free(&ssl);
        mbedtls_ssl_config_free(&conf);
        mbedtls_ctr_drbg_free(&drbg);
        mbedtls_entropy_free(&entropy);
    }
    TlsClient(const TlsClient&) = delete;
    TlsClient& operator=(const TlsClient&) = delete;

    static int bio_send(void* ctx, const unsigned char* b, size_t n) {
        int fd = *static_cast<int*>(ctx);
        ssize_t r = ::send(fd, b, n, kFlags);
        if (r < 0) return errno == EINTR ? MBEDTLS_ERR_SSL_WANT_WRITE : MBEDTLS_ERR_NET_SEND_FAILED_COMPAT;
        return static_cast<int>(r);
    }
    static int bio_recv(void* ctx, unsigned char* b, size_t n) {
        int fd = *static_cast<int*>(ctx);
        ssize_t r = ::recv(fd, b, n, 0);
        if (r < 0) return errno == EINTR ? MBEDTLS_ERR_SSL_WANT_READ : MBEDTLS_ERR_NET_RECV_FAILED_COMPAT;
        return static_cast<int>(r);   // 0 = 对方断开
    }
    // mbedTLS 没编 net 模块，借用它的错误码数值
    static constexpr int MBEDTLS_ERR_NET_SEND_FAILED_COMPAT = -0x004E;
    static constexpr int MBEDTLS_ERR_NET_RECV_FAILED_COMPAT = -0x004C;

    // 连接 + 握手。max13 = false 时只允许 TLS 1.2
    bool connect(int port, bool allow13 = true, bool allow12 = true) {
        fd = connect_tcp(port);
        if (fd < 0) return false;
        timeval tv{5, 0};   // 握手最多等 5 s
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof tv);
        static const char pers[] = "tls-test-client";
        if (mbedtls_ctr_drbg_seed(&drbg, mbedtls_entropy_func, &entropy, reinterpret_cast<const unsigned char*>(pers), sizeof pers - 1))
            return false;
        if (mbedtls_ssl_config_defaults(&conf, MBEDTLS_SSL_IS_CLIENT, MBEDTLS_SSL_TRANSPORT_STREAM, MBEDTLS_SSL_PRESET_DEFAULT)) return false;
        mbedtls_ssl_conf_rng(&conf, mbedtls_ctr_drbg_random, &drbg);
        mbedtls_ssl_conf_authmode(&conf, MBEDTLS_SSL_VERIFY_NONE);   // 自签：不验 CA，改成比对指纹
        mbedtls_ssl_conf_min_tls_version(&conf, allow12 ? MBEDTLS_SSL_VERSION_TLS1_2 : MBEDTLS_SSL_VERSION_TLS1_3);
        mbedtls_ssl_conf_max_tls_version(&conf, allow13 ? MBEDTLS_SSL_VERSION_TLS1_3 : MBEDTLS_SSL_VERSION_TLS1_2);
        if (mbedtls_ssl_setup(&ssl, &conf)) return false;
        mbedtls_ssl_set_bio(&ssl, &fd, bio_send, bio_recv, nullptr);
        int rc;
        while ((rc = mbedtls_ssl_handshake(&ssl)) != 0) {
            if (rc != MBEDTLS_ERR_SSL_WANT_READ && rc != MBEDTLS_ERR_SSL_WANT_WRITE) {
                last_error = rc;
                return false;
            }
        }
        const mbedtls_x509_crt* c = mbedtls_ssl_get_peer_cert(&ssl);
        if (c) peer_fingerprint = reactor::net::tls_fingerprint_sha256(c->raw.p, c->raw.len);
        version = mbedtls_ssl_get_version(&ssl);
        suite = mbedtls_ssl_get_ciphersuite(&ssl);
        return true;
    }

    bool send_all(const std::string& s) {
        size_t off = 0;
        while (off < s.size()) {
            int r = mbedtls_ssl_write(&ssl, reinterpret_cast<const unsigned char*>(s.data() + off), s.size() - off);
            if (r == MBEDTLS_ERR_SSL_WANT_READ || r == MBEDTLS_ERR_SSL_WANT_WRITE) continue;
            if (r <= 0) return false;
            off += static_cast<size_t>(r);
        }
        return true;
    }

    // 读一些明文追加到 buf；超时 / 断开返回 false
    bool read_more(std::string& buf, int timeout_ms, bool* eof = nullptr) {
        if (mbedtls_ssl_get_bytes_avail(&ssl) == 0 && !mbedtls_ssl_check_pending(&ssl)) {
            pollfd p{fd, POLLIN, 0};
            if (::poll(&p, 1, timeout_ms) <= 0) return false;
        }
        unsigned char tmp[16384];
        while (true) {
            int r = mbedtls_ssl_read(&ssl, tmp, sizeof tmp);
            if (r > 0) {
                buf.append(reinterpret_cast<const char*>(tmp), static_cast<size_t>(r));
                return true;
            }
            if (r == MBEDTLS_ERR_SSL_WANT_READ || r == MBEDTLS_ERR_SSL_WANT_WRITE) {
                pollfd p{fd, POLLIN, 0};
                if (::poll(&p, 1, timeout_ms) <= 0) return false;
                continue;
            }
            if (eof) *eof = true;   // 0 / close_notify / 出错
            return false;
        }
    }

    ReadFn reader() { return [this](std::string& b, int t, bool* eof) { return read_more(b, t, eof); }; }
    SendFn sender() { return [this](const std::string& s) { return send_all(s); }; }

    HttpResp request(const std::string& method, const std::string& target, const std::string& token = "", const std::string& body = "",
                     const std::string& extra = "") {
        if (!send_all(build(method, target, token, body, extra))) return HttpResp{};
        std::string buf;
        return read_response_io(reader(), buf, 5000, method == "HEAD");
    }

    void close() {
        if (fd >= 0) {
            mbedtls_ssl_close_notify(&ssl);
            ::close(fd);
        }
        fd = -1;
    }
};

// 单发 HTTPS 请求（新连接）
inline HttpResp tls_request(int port, const std::string& method, const std::string& target, const std::string& token = "",
                            const std::string& body = "", const std::string& extra = "", std::string* fingerprint = nullptr) {
    TlsClient c;
    if (!c.connect(port)) return HttpResp{};
    if (fingerprint) *fingerprint = c.peer_fingerprint;
    return c.request(method, target, token, body, extra);
}

}  // namespace nettest
