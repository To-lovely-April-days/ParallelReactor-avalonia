// PC 端 TCP 入口的加密（HTTPS / WSS）：设备自己生成的自签证书 + PC 端首次连接核对指纹后固定（trust-on-first-use）。
// 底层用 mbedTLS 3.6（third_party/mbedtls，裁剪配置 config/reactor_mbedtls_config.h）；这个头文件不暴露 mbedTLS 类型。
//
//   - TlsCertStore：证书仓库 <配置目录>/tls/cert.pem + key.pem（key.pem 权限 0600）。启动时读，读不了就生成
//     ECDSA P-256 自签证书（CN = "HT-PR8 <主机名>"，有效期 20 年，随机序列号）。regenerate() 换新证书，
//     之后新建的 TCP 连接立刻用新证书，已有连接不受影响（各自持有旧配置的引用）。线程安全。
//   - TlsStream：一条服务端 TLS 连接（内存 BIO）。服务器 I/O 线程把收到的密文 feed 进来，取明文；写明文得到
//     待发密文 out()。不碰 socket，不阻塞，没有 WANT_WRITE。只在一个线程上用。
//
// 指纹 = 证书 DER 的 SHA-256，大写十六进制两位一组用「:」隔开（和 openssl x509 -fingerprint -sha256 一样）。
#pragma once
#include <cstddef>
#include <cstdint>
#include <memory>
#include <mutex>
#include <string>

namespace reactor::net {

struct TlsCertInfo {
    bool valid = false;
    std::string fingerprint_sha256;   // "AB:CD:…"（32 组）
    std::string subject;              // "CN=HT-PR8 reactor"
    int64_t not_before_ms = 0;        // UTC 毫秒
    int64_t not_after_ms = 0;
    int64_t created_ms = 0;           // 证书文件写入时间（cert.pem 的修改时间）
};

// 已解析好的证书 + 私钥 + TLS 服务端参数（不透明；连接持有 shared_ptr，换证书时旧的随最后一条连接释放）
class TlsServerConfig;

class TlsCertStore {
public:
    // dir：证书目录（一般是 <配置目录>/tls）；host_label：写进 CN 的主机名（空 = gethostname）
    explicit TlsCertStore(std::string dir, std::string host_label = "");
    ~TlsCertStore();
    TlsCertStore(const TlsCertStore&) = delete;
    TlsCertStore& operator=(const TlsCertStore&) = delete;

    // 读 cert.pem + key.pem；缺文件 / 读不了 / 密钥和证书不配对 → 生成新的并写盘。
    // created = 这次新生成了；reason = 为什么新生成（中文，给事件用）。失败（连生成都失败）返回 false。
    bool load_or_create(std::string* error = nullptr, bool* created = nullptr, std::string* reason = nullptr);
    // 生成新的证书 + 密钥，写盘，热替换。失败时旧证书照常使用。
    bool regenerate(std::string* error = nullptr);

    TlsCertInfo info() const;
    std::shared_ptr<TlsServerConfig> server_config() const;   // 未加载 = nullptr
    const std::string& dir() const { return dir_; }
    std::string cert_path() const { return dir_ + "/cert.pem"; }
    std::string key_path() const { return dir_ + "/key.pem"; }

private:
    bool install(const std::string& cert_pem, const std::string& key_pem, std::string* error);
    bool generate_and_save(std::string* error);

    std::string dir_, host_label_;
    mutable std::mutex mu_;
    std::shared_ptr<TlsServerConfig> cfg_;
    TlsCertInfo info_;
};

class TlsStream {
public:
    enum class Status { Ok, WantMore, Closed, Error };

    static std::unique_ptr<TlsStream> create(std::shared_ptr<TlsServerConfig> cfg, std::string* error = nullptr);
    ~TlsStream();
    TlsStream(const TlsStream&) = delete;
    TlsStream& operator=(const TlsStream&) = delete;

    void feed(const char* data, size_t n);     // 收到的密文
    size_t buffered() const;                   // 还没交给 mbedTLS 的密文字节数
    const std::string& peek_input() const;     // 未处理的密文（握手前判断是不是明文 HTTP）

    Status handshake();                        // Ok = 握手完成；WantMore = 等对方数据
    bool handshake_done() const;
    // 解密一段明文追加到 out（最多 max 字节）。Ok = 读到了；WantMore = 暂时没有；Closed = 对方 close_notify / 断开
    Status read(std::string& out, size_t max);
    // 还有能立刻解密的数据（缓冲的密文 / mbedTLS 里已解密未取走的明文）—— poll 不报可读时也要取
    bool pending() const;
    // 加密：一次最多一条记录，返回吃掉的明文字节数；< 0 = 出错
    long write(const char* data, size_t n);
    void close_notify();

    std::string& out();                        // 待发的密文
    size_t out_pos = 0;                        // out 里已发出的字节
    bool out_pending() const;
    std::string last_error() const;            // 最近一次错误（mbedTLS 错误码文字）
    std::string version() const;               // "TLSv1.3" 等
    std::string ciphersuite() const;

private:
    TlsStream();
    struct Impl;
    std::unique_ptr<Impl> p_;
};

// 工具
std::string tls_fingerprint_sha256(const unsigned char* der, size_t len);   // "AB:CD:…"
std::string tls_error_text(int code);                                          // mbedTLS 错误码 → "-0x7780"
// 解析 PEM 证书得到信息（测试 / 外部校验用）
TlsCertInfo tls_cert_info_from_pem(const std::string& cert_pem);

}  // namespace reactor::net
