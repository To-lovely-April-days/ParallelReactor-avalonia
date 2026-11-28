#include "net/tls.hpp"

#include <fcntl.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <unistd.h>

#include <algorithm>
#include <cerrno>
#include <cstdio>
#include <cstring>
#include <ctime>
#include <string>
#include <vector>

#include "mbedtls/ctr_drbg.h"
#include "mbedtls/ecp.h"
#include "mbedtls/entropy.h"
#include "mbedtls/error.h"
#include "mbedtls/pk.h"
#include "mbedtls/sha256.h"
#include "mbedtls/ssl.h"
#include "mbedtls/x509_crt.h"
#include "psa/crypto.h"

namespace reactor::net {

namespace {

constexpr int kValidityYears = 20;

// PSA 全局初始化（TLS 1.3 和 PK 都要；可重复调用，线程安全）
void ensure_psa() {
    static std::once_flag once;
    std::call_once(once, [] { (void)psa_crypto_init(); });
}

std::string err_text(int code) {
    char b[160] = {0};
    std::snprintf(b, sizeof b, "-0x%04X", static_cast<unsigned>(-code));
    return b;
}

// 一套随机数发生器（熵源 + CTR_DRBG）
struct Rng {
    mbedtls_entropy_context entropy;
    mbedtls_ctr_drbg_context drbg;
    bool ok = false;
    Rng() {
        mbedtls_entropy_init(&entropy);
        mbedtls_ctr_drbg_init(&drbg);
        static const char pers[] = "reactord-tls";
        ok = mbedtls_ctr_drbg_seed(&drbg, mbedtls_entropy_func, &entropy, reinterpret_cast<const unsigned char*>(pers),
                                   sizeof pers - 1) == 0;
    }
    ~Rng() {
        mbedtls_ctr_drbg_free(&drbg);
        mbedtls_entropy_free(&entropy);
    }
    Rng(const Rng&) = delete;
    Rng& operator=(const Rng&) = delete;
};

int64_t x509_time_ms(const mbedtls_x509_time& t) {
    std::tm tm{};
    tm.tm_year = t.year - 1900;
    tm.tm_mon = t.mon - 1;
    tm.tm_mday = t.day;
    tm.tm_hour = t.hour;
    tm.tm_min = t.min;
    tm.tm_sec = t.sec;
    return static_cast<int64_t>(timegm(&tm)) * 1000;
}

std::string x509_time_text(std::time_t t) {   // YYYYMMDDhhmmss（UTC），x509write 的格式
    std::tm tm{};
    gmtime_r(&t, &tm);
    char b[32];
    std::snprintf(b, sizeof b, "%04d%02d%02d%02d%02d%02d", tm.tm_year + 1900, tm.tm_mon + 1, tm.tm_mday, tm.tm_hour, tm.tm_min,
                  tm.tm_sec);
    return b;
}

bool read_text(const std::string& path, std::string& out) {
    FILE* f = std::fopen(path.c_str(), "rb");
    if (!f) return false;
    out.clear();
    char buf[4096];
    size_t n;
    while ((n = std::fread(buf, 1, sizeof buf, f)) > 0) {
        out.append(buf, n);
        if (out.size() > 256 * 1024) break;   // 证书文件不会这么大
    }
    std::fclose(f);
    return true;
}

// 原子写：先写同目录临时文件（权限 mode）、fsync，再 rename
bool write_atomic(const std::string& path, const std::string& data, mode_t mode, std::string* err) {
    std::string tmp = path + ".tmp";
    int fd = ::open(tmp.c_str(), O_WRONLY | O_CREAT | O_TRUNC | O_CLOEXEC, mode);
    if (fd < 0) {
        if (err) *err = tmp + " · " + std::strerror(errno);
        return false;
    }
    ::fchmod(fd, mode);   // umask 可能去掉了位；也防止旧文件权限残留
    size_t off = 0;
    while (off < data.size()) {
        ssize_t n = ::write(fd, data.data() + off, data.size() - off);
        if (n < 0 && errno == EINTR) continue;
        if (n <= 0) {
            if (err) *err = tmp + " · " + std::strerror(errno);
            ::close(fd);
            ::unlink(tmp.c_str());
            return false;
        }
        off += static_cast<size_t>(n);
    }
    ::fsync(fd);
    ::close(fd);
    if (::rename(tmp.c_str(), path.c_str()) != 0) {
        if (err) *err = path + " · " + std::strerror(errno);
        ::unlink(tmp.c_str());
        return false;
    }
    return true;
}

bool make_dir_p(const std::string& dir, std::string* err) {
    if (dir.empty()) return true;
    std::string cur;
    size_t pos = 0;
    while (pos <= dir.size()) {
        size_t e = dir.find('/', pos);
        if (e == std::string::npos) e = dir.size();
        cur = dir.substr(0, e);
        if (!cur.empty()) {
            struct stat st{};
            if (::stat(cur.c_str(), &st) != 0) {
                if (::mkdir(cur.c_str(), 0755) != 0 && errno != EEXIST) {
                    if (err) *err = cur + " · " + std::strerror(errno);
                    return false;
                }
            }
        }
        pos = e + 1;
        if (e == dir.size()) break;
    }
    return true;
}

// CN 里的主机名只留安全字符（X.509 名字串里逗号、等号等有特殊含义）
std::string sanitize_host(std::string h) {
    std::string o;
    for (char c : h) {
        bool okc = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '.' || c == '_';
        if (okc) o += c;
        if (o.size() >= 48) break;
    }
    return o.empty() ? std::string("reactor") : o;
}

TlsCertInfo info_from_crt(const mbedtls_x509_crt& crt) {
    TlsCertInfo i;
    i.valid = true;
    i.fingerprint_sha256 = tls_fingerprint_sha256(crt.raw.p, crt.raw.len);
    char dn[512] = {0};
    if (mbedtls_x509_dn_gets(dn, sizeof dn, &crt.subject) > 0) i.subject = dn;
    i.not_before_ms = x509_time_ms(crt.valid_from);
    i.not_after_ms = x509_time_ms(crt.valid_to);
    return i;
}

}  // namespace

// ———————————————————————————— TlsServerConfig ————————————————————————————

class TlsServerConfig {
public:
    mbedtls_x509_crt crt;
    mbedtls_pk_context pk;
    mbedtls_ssl_config conf;
    Rng rng;
    TlsCertInfo info;

    TlsServerConfig() {
        mbedtls_x509_crt_init(&crt);
        mbedtls_pk_init(&pk);
        mbedtls_ssl_config_init(&conf);
    }
    ~TlsServerConfig() {
        mbedtls_ssl_config_free(&conf);
        mbedtls_pk_free(&pk);
        mbedtls_x509_crt_free(&crt);
    }
    TlsServerConfig(const TlsServerConfig&) = delete;
    TlsServerConfig& operator=(const TlsServerConfig&) = delete;

    bool init(const std::string& cert_pem, const std::string& key_pem, std::string* err) {
        ensure_psa();
        if (!rng.ok) {
            if (err) *err = "随机数发生器初始化失败";
            return false;
        }
        int rc = mbedtls_x509_crt_parse(&crt, reinterpret_cast<const unsigned char*>(cert_pem.c_str()), cert_pem.size() + 1);
        if (rc != 0) {
            if (err) *err = "证书读不了（" + err_text(rc) + "）";
            return false;
        }
        rc = mbedtls_pk_parse_key(&pk, reinterpret_cast<const unsigned char*>(key_pem.c_str()), key_pem.size() + 1, nullptr, 0,
                                  mbedtls_ctr_drbg_random, &rng.drbg);
        if (rc != 0) {
            if (err) *err = "私钥读不了（" + err_text(rc) + "）";
            return false;
        }
        if (!mbedtls_pk_can_do(&pk, MBEDTLS_PK_ECKEY)) {
            if (err) *err = "私钥不是 ECDSA（只支持 P-256）";
            return false;
        }
        rc = mbedtls_pk_check_pair(&crt.pk, &pk, mbedtls_ctr_drbg_random, &rng.drbg);
        if (rc != 0) {
            if (err) *err = "私钥和证书不配对（" + err_text(rc) + "）";
            return false;
        }
        rc = mbedtls_ssl_config_defaults(&conf, MBEDTLS_SSL_IS_SERVER, MBEDTLS_SSL_TRANSPORT_STREAM, MBEDTLS_SSL_PRESET_DEFAULT);
        if (rc != 0) {
            if (err) *err = "TLS 参数初始化失败（" + err_text(rc) + "）";
            return false;
        }
        mbedtls_ssl_conf_rng(&conf, mbedtls_ctr_drbg_random, &rng.drbg);
        mbedtls_ssl_conf_min_tls_version(&conf, MBEDTLS_SSL_VERSION_TLS1_2);
        mbedtls_ssl_conf_max_tls_version(&conf, MBEDTLS_SSL_VERSION_TLS1_3);
        mbedtls_ssl_conf_authmode(&conf, MBEDTLS_SSL_VERIFY_NONE);   // 不要客户端证书（PC 端用令牌鉴权）
        rc = mbedtls_ssl_conf_own_cert(&conf, &crt, &pk);
        if (rc != 0) {
            if (err) *err = "证书装不上（" + err_text(rc) + "）";
            return false;
        }
        info = info_from_crt(crt);
        return true;
    }
};

// ———————————————————————————— TlsCertStore ————————————————————————————

TlsCertStore::TlsCertStore(std::string dir, std::string host_label) : dir_(std::move(dir)), host_label_(std::move(host_label)) {
    ensure_psa();
    while (dir_.size() > 1 && dir_.back() == '/') dir_.pop_back();
}

TlsCertStore::~TlsCertStore() = default;

bool TlsCertStore::install(const std::string& cert_pem, const std::string& key_pem, std::string* error) {
    auto cfg = std::make_shared<TlsServerConfig>();
    if (!cfg->init(cert_pem, key_pem, error)) return false;
    TlsCertInfo inf = cfg->info;
    struct stat st{};
    if (::stat(cert_path().c_str(), &st) == 0) {
#if defined(__APPLE__)
        inf.created_ms = static_cast<int64_t>(st.st_mtimespec.tv_sec) * 1000 + st.st_mtimespec.tv_nsec / 1000000;
#else
        inf.created_ms = static_cast<int64_t>(st.st_mtim.tv_sec) * 1000 + st.st_mtim.tv_nsec / 1000000;
#endif
    }
    std::lock_guard<std::mutex> lk(mu_);
    cfg_ = std::move(cfg);
    info_ = inf;
    return true;
}

bool TlsCertStore::generate_and_save(std::string* error) {
    ensure_psa();
    Rng rng;
    if (!rng.ok) {
        if (error) *error = "随机数发生器初始化失败";
        return false;
    }
    std::string host = host_label_;
    if (host.empty()) {
        char h[256] = {0};
        ::gethostname(h, sizeof h - 1);
        host = h;
    }
    std::string cn = "CN=HT-PR8 " + sanitize_host(host) + ",O=HT-PR8 reactord";

    mbedtls_pk_context key;
    mbedtls_pk_init(&key);
    mbedtls_x509write_cert w;
    mbedtls_x509write_crt_init(&w);
    std::string cert_pem, key_pem;
    int rc = 0;
    auto fail = [&](const char* what) {
        if (error) *error = std::string(what) + "（" + err_text(rc) + "）";
        mbedtls_x509write_crt_free(&w);
        mbedtls_pk_free(&key);
        return false;
    };
    if ((rc = mbedtls_pk_setup(&key, mbedtls_pk_info_from_type(MBEDTLS_PK_ECKEY))) != 0) return fail("密钥初始化失败");
    if ((rc = mbedtls_ecp_gen_key(MBEDTLS_ECP_DP_SECP256R1, mbedtls_pk_ec(key), mbedtls_ctr_drbg_random, &rng.drbg)) != 0)
        return fail("生成 P-256 密钥失败");

    // 序列号：16 字节随机，最高位清零（保证正数）且不为 0
    unsigned char serial[16];
    if ((rc = mbedtls_ctr_drbg_random(&rng.drbg, serial, sizeof serial)) != 0) return fail("生成序列号失败");
    serial[0] = static_cast<unsigned char>((serial[0] & 0x7f) | 0x01);

    std::time_t now = std::time(nullptr);
    std::time_t nb = now - 60;   // 往前留 1 分钟，PC 时钟稍慢也不会「尚未生效」
    std::tm tm{};
    gmtime_r(&now, &tm);
    tm.tm_year += kValidityYears;
    std::time_t na = timegm(&tm);

    mbedtls_x509write_crt_set_version(&w, MBEDTLS_X509_CRT_VERSION_3);
    mbedtls_x509write_crt_set_md_alg(&w, MBEDTLS_MD_SHA256);
    mbedtls_x509write_crt_set_subject_key(&w, &key);
    mbedtls_x509write_crt_set_issuer_key(&w, &key);
    if ((rc = mbedtls_x509write_crt_set_subject_name(&w, cn.c_str())) != 0) return fail("证书名字不对");
    if ((rc = mbedtls_x509write_crt_set_issuer_name(&w, cn.c_str())) != 0) return fail("证书名字不对");
    if ((rc = mbedtls_x509write_crt_set_serial_raw(&w, serial, sizeof serial)) != 0) return fail("设置序列号失败");
    if ((rc = mbedtls_x509write_crt_set_validity(&w, x509_time_text(nb).c_str(), x509_time_text(na).c_str())) != 0)
        return fail("设置有效期失败");
    if ((rc = mbedtls_x509write_crt_set_basic_constraints(&w, 0, -1)) != 0) return fail("设置证书扩展失败");
    if ((rc = mbedtls_x509write_crt_set_key_usage(&w, MBEDTLS_X509_KU_DIGITAL_SIGNATURE)) != 0) return fail("设置证书扩展失败");

    std::vector<unsigned char> buf(8192);
    if ((rc = mbedtls_x509write_crt_pem(&w, buf.data(), buf.size(), mbedtls_ctr_drbg_random, &rng.drbg)) != 0)
        return fail("写证书失败");
    cert_pem = reinterpret_cast<const char*>(buf.data());
    std::fill(buf.begin(), buf.end(), 0);
    if ((rc = mbedtls_pk_write_key_pem(&key, buf.data(), buf.size())) != 0) return fail("写私钥失败");
    key_pem = reinterpret_cast<const char*>(buf.data());
    std::fill(buf.begin(), buf.end(), 0);
    mbedtls_x509write_crt_free(&w);
    mbedtls_pk_free(&key);

    std::string e;
    if (!make_dir_p(dir_, &e)) {
        if (error) *error = "建不了证书目录：" + e;
        return false;
    }
    ::chmod(dir_.c_str(), 0700);
    // 先写私钥再写证书：中途断电最多留下一对不配对的文件，下次启动会检测出来重新生成
    if (!write_atomic(key_path(), key_pem, 0600, &e) || !write_atomic(cert_path(), cert_pem, 0644, &e)) {
        if (error) *error = "证书写不进去：" + e;
        return false;
    }
    return install(cert_pem, key_pem, error);
}

bool TlsCertStore::load_or_create(std::string* error, bool* created, std::string* reason) {
    if (created) *created = false;
    std::string cert_pem, key_pem, why;
    bool have_cert = read_text(cert_path(), cert_pem);
    bool have_key = read_text(key_path(), key_pem);
    if (have_cert && have_key) {
        std::string e;
        if (install(cert_pem, key_pem, &e)) {
            // 私钥权限被人改宽了：收回
            struct stat st{};
            if (::stat(key_path().c_str(), &st) == 0 && (st.st_mode & 077)) ::chmod(key_path().c_str(), 0600);
            return true;
        }
        why = "原证书不能用：" + e;
    } else if (!have_cert && !have_key) {
        why = "首次启动，还没有证书";
    } else {
        why = have_cert ? "缺私钥文件 key.pem" : "缺证书文件 cert.pem";
    }
    if (reason) *reason = why;
    if (!generate_and_save(error)) return false;
    if (created) *created = true;
    return true;
}

bool TlsCertStore::regenerate(std::string* error) { return generate_and_save(error); }

TlsCertInfo TlsCertStore::info() const {
    std::lock_guard<std::mutex> lk(mu_);
    return info_;
}

std::shared_ptr<TlsServerConfig> TlsCertStore::server_config() const {
    std::lock_guard<std::mutex> lk(mu_);
    return cfg_;
}

// ———————————————————————————— TlsStream ————————————————————————————

struct TlsStream::Impl {
    std::shared_ptr<TlsServerConfig> cfg;
    mbedtls_ssl_context ssl;
    std::string in;
    size_t in_pos = 0;
    std::string out;
    bool done = false;
    int last_err = 0;

    Impl() { mbedtls_ssl_init(&ssl); }
    ~Impl() { mbedtls_ssl_free(&ssl); }

    static int bio_send(void* ctx, const unsigned char* buf, size_t len) {
        auto* s = static_cast<Impl*>(ctx);
        s->out.append(reinterpret_cast<const char*>(buf), len);   // 全收：真正的发送由服务器 I/O 线程按 socket 节奏做
        return static_cast<int>(len);
    }
    static int bio_recv(void* ctx, unsigned char* buf, size_t len) {
        auto* s = static_cast<Impl*>(ctx);
        size_t avail = s->in.size() - s->in_pos;
        if (avail == 0) return MBEDTLS_ERR_SSL_WANT_READ;
        size_t n = std::min(avail, len);
        std::memcpy(buf, s->in.data() + s->in_pos, n);
        s->in_pos += n;
        if (s->in_pos == s->in.size()) {
            s->in.clear();
            s->in_pos = 0;
        }
        return static_cast<int>(n);
    }
};

TlsStream::TlsStream() : p_(std::make_unique<Impl>()) {}
TlsStream::~TlsStream() = default;

std::unique_ptr<TlsStream> TlsStream::create(std::shared_ptr<TlsServerConfig> cfg, std::string* error) {
    if (!cfg) {
        if (error) *error = "没有证书";
        return nullptr;
    }
    std::unique_ptr<TlsStream> s(new TlsStream());
    s->p_->cfg = std::move(cfg);
    int rc = mbedtls_ssl_setup(&s->p_->ssl, &s->p_->cfg->conf);
    if (rc != 0) {
        if (error) *error = "TLS 连接初始化失败（" + err_text(rc) + "）";
        return nullptr;
    }
    mbedtls_ssl_set_bio(&s->p_->ssl, s->p_.get(), &Impl::bio_send, &Impl::bio_recv, nullptr);
    return s;
}

void TlsStream::feed(const char* data, size_t n) {
    auto& p = *p_;
    if (p.in_pos > 0 && p.in_pos >= p.in.size() / 2) {   // 压缩：别让已消费的前缀无限长
        p.in.erase(0, p.in_pos);
        p.in_pos = 0;
    }
    p.in.append(data, n);
}

size_t TlsStream::buffered() const { return p_->in.size() - p_->in_pos; }

const std::string& TlsStream::peek_input() const {
    static const std::string empty;
    if (p_->in_pos == 0) return p_->in;
    return empty;
}

TlsStream::Status TlsStream::handshake() {
    auto& p = *p_;
    if (p.done) return Status::Ok;
    int rc = mbedtls_ssl_handshake(&p.ssl);
    if (rc == 0) {
        p.done = true;
        return Status::Ok;
    }
    if (rc == MBEDTLS_ERR_SSL_WANT_READ || rc == MBEDTLS_ERR_SSL_WANT_WRITE) return Status::WantMore;
    p.last_err = rc;
    return Status::Error;
}

bool TlsStream::handshake_done() const { return p_->done; }

TlsStream::Status TlsStream::read(std::string& out, size_t max) {
    auto& p = *p_;
    if (!p.done) return Status::WantMore;
    unsigned char buf[16384];
    size_t want = std::min(max, sizeof buf);
    if (want == 0) return Status::WantMore;
    int rc = mbedtls_ssl_read(&p.ssl, buf, want);
    if (rc > 0) {
        out.append(reinterpret_cast<const char*>(buf), static_cast<size_t>(rc));
        return Status::Ok;
    }
    if (rc == MBEDTLS_ERR_SSL_WANT_READ || rc == MBEDTLS_ERR_SSL_WANT_WRITE) return Status::WantMore;
    if (rc == 0 || rc == MBEDTLS_ERR_SSL_PEER_CLOSE_NOTIFY) return Status::Closed;
    p.last_err = rc;
    return Status::Error;
}

bool TlsStream::pending() const {
    if (!p_->done) return false;
    return buffered() > 0 || mbedtls_ssl_get_bytes_avail(&p_->ssl) > 0;
}

long TlsStream::write(const char* data, size_t n) {
    auto& p = *p_;
    if (!p.done) return 0;
    if (n == 0) return 0;
    int rc = mbedtls_ssl_write(&p.ssl, reinterpret_cast<const unsigned char*>(data), n);
    if (rc >= 0) return rc;
    if (rc == MBEDTLS_ERR_SSL_WANT_READ || rc == MBEDTLS_ERR_SSL_WANT_WRITE) return 0;
    p.last_err = rc;
    return -1;
}

void TlsStream::close_notify() {
    if (p_->done) (void)mbedtls_ssl_close_notify(&p_->ssl);
}

std::string& TlsStream::out() { return p_->out; }
bool TlsStream::out_pending() const { return out_pos < p_->out.size(); }
std::string TlsStream::last_error() const { return p_->last_err ? tls_error_text(p_->last_err) : std::string(); }
std::string TlsStream::version() const { return p_->done ? mbedtls_ssl_get_version(&p_->ssl) : ""; }
std::string TlsStream::ciphersuite() const {
    const char* s = p_->done ? mbedtls_ssl_get_ciphersuite(&p_->ssl) : nullptr;
    return s ? s : "";
}

// ———————————————————————————— 工具 ————————————————————————————

std::string tls_fingerprint_sha256(const unsigned char* der, size_t len) {
    unsigned char h[32];
    if (mbedtls_sha256(der, len, h, 0) != 0) return "";
    static const char* hx = "0123456789ABCDEF";
    std::string s;
    s.reserve(95);
    for (int i = 0; i < 32; ++i) {
        if (i) s += ':';
        s += hx[h[i] >> 4];
        s += hx[h[i] & 15];
    }
    return s;
}

std::string tls_error_text(int code) {
    char b[200] = {0};
    mbedtls_strerror(code, b, sizeof b);
    std::string s = err_text(code);
    if (b[0]) s += " " + std::string(b);
    return s;
}

TlsCertInfo tls_cert_info_from_pem(const std::string& cert_pem) {
    ensure_psa();
    mbedtls_x509_crt crt;
    mbedtls_x509_crt_init(&crt);
    TlsCertInfo i;
    if (mbedtls_x509_crt_parse(&crt, reinterpret_cast<const unsigned char*>(cert_pem.c_str()), cert_pem.size() + 1) == 0)
        i = info_from_crt(crt);
    mbedtls_x509_crt_free(&crt);
    return i;
}

}  // namespace reactor::net
