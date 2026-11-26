#include "net/crypto.hpp"
#include "net/websocket.hpp"
#include "testing.hpp"

using namespace reactor::net;

// FIPS 180-2 / RFC 3174 标准向量
TEST(net_crypto, sha1_vectors) {
    CHECK_EQ(to_hex(sha1("")), std::string("da39a3ee5e6b4b0d3255bfef95601890afd80709"));
    CHECK_EQ(to_hex(sha1("abc")), std::string("a9993e364706816aba3e25717850c26c9cd0d89d"));
    CHECK_EQ(to_hex(sha1("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq")),
             std::string("84983e441c3bd26ebaae4aa1f95129e5e54670f1"));
    CHECK_EQ(to_hex(sha1(std::string(1000000, 'a'))), std::string("34aa973cd4c4daa4f61eeb2bdbad27316534016f"));
    CHECK_EQ(to_hex(sha1("The quick brown fox jumps over the lazy dog")),
             std::string("2fd4e1c67a2d28fced849ee1bb76e7391b93eb12"));
}

TEST(net_crypto, sha256_vectors) {
    CHECK_EQ(sha256_hex(""), std::string("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"));
    CHECK_EQ(sha256_hex("abc"), std::string("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"));
    CHECK_EQ(sha256_hex("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq"),
             std::string("248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1"));
    CHECK_EQ(sha256_hex(std::string(1000000, 'a')),
             std::string("cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0"));
    // 长度跨越填充边界（55 / 56 / 64 字节）
    CHECK_EQ(sha256_hex(std::string(55, 'a')), std::string("9f4390f8d30c2dd92ec9f095b65e2b9ae9b0a925a5258e241c9f1e910f734318"));
    CHECK_EQ(sha256_hex(std::string(56, 'a')), std::string("b35439a4ac6f0948b6d6f9e3c6af0f5f590ce20f1bde7090ef7970686ec6738a"));
    CHECK_EQ(sha256_hex(std::string(64, 'a')), std::string("ffe054fe7ae0cb6dc65c3af9b61d5209f439851db43d0ba5997337df154668eb"));
    // UTF-8 令牌（C#: Encoding.UTF8.GetBytes）
    CHECK_EQ(sha256_hex("令牌").size(), size_t(64));
}

// RFC 4648 §10
TEST(net_crypto, base64_vectors) {
    const char* in[] = {"", "f", "fo", "foo", "foob", "fooba", "foobar"};
    const char* out[] = {"", "Zg==", "Zm8=", "Zm9v", "Zm9vYg==", "Zm9vYmE=", "Zm9vYmFy"};
    for (int i = 0; i < 7; ++i) {
        CHECK_EQ(base64_encode(std::string(in[i])), std::string(out[i]));
        auto d = base64_decode(out[i]);
        CHECK(d.has_value());
        if (d) CHECK_EQ(*d, std::string(in[i]));
    }
    CHECK(!base64_decode("Zg=").has_value());
    CHECK(!base64_decode("Z===").has_value());
    CHECK(!base64_decode("Zm9v!A==").has_value());
    CHECK(!base64_decode("Zg==Zg==").has_value());
    std::string bin;
    for (int i = 0; i < 256; ++i) bin += static_cast<char>(i);
    CHECK_EQ(*base64_decode(base64_encode(bin)), bin);
}

TEST(net_crypto, constant_time_equal) {
    CHECK(constant_time_equal("abc", "abc"));
    CHECK(!constant_time_equal("abc", "abd"));
    CHECK(!constant_time_equal("abc", "abcd"));
    CHECK(constant_time_equal("", ""));
}

// RFC 6455 §1.3 的示例
TEST(net_crypto, ws_accept_key) {
    CHECK_EQ(ws::accept_key("dGhlIHNhbXBsZSBub25jZQ=="), std::string("s3pPLMBiTxaQ9kYGzzhZRbK+xOo="));
    CHECK(ws::valid_client_key("dGhlIHNhbXBsZSBub25jZQ=="));
    CHECK(!ws::valid_client_key("dGhlIHNhbXBsZQ=="));   // 11 字节
    CHECK(!ws::valid_client_key(""));
}
