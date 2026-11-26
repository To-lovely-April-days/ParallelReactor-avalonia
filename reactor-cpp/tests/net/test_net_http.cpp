// HTTP 解析 / 路由 / WS 编解码 / 文件响应 的单元测试（不开 socket）。
#include <fcntl.h>
#include <stdlib.h>
#include <sys/stat.h>
#include <unistd.h>

#include <fstream>

#include "net/http.hpp"
#include "net/router.hpp"
#include "net/websocket.hpp"
#include "testing.hpp"

using namespace reactor::net;

namespace {
ParseResult parse(const std::string& s, Request& r, ParseLimits lim = {}) { return parse_request(s.data(), s.size(), lim, r); }
}

TEST(net_http, parse_basic_get) {
    Request r;
    std::string raw = "GET /api/v1/events?afterSeq=10&limit=5&q=a%20b+c HTTP/1.1\r\nHost: x\r\nX-Thing:  v  \r\n\r\nGET /next";
    auto pr = parse(raw, r);
    CHECK(pr.kind == ParseResult::Complete);
    CHECK_EQ(pr.consumed, raw.size() - 9);
    CHECK_EQ(r.method, std::string("GET"));
    CHECK_EQ(r.path, std::string("/api/v1/events"));
    CHECK_EQ(r.query_param("afterSeq"), std::string("10"));
    CHECK_EQ(r.query_param("q"), std::string("a b c"));
    CHECK_EQ(r.header("x-thing"), std::string("v"));   // 名字大小写不敏感，值去掉空白
    CHECK(r.keep_alive());
}

TEST(net_http, parse_incremental_and_body) {
    std::string raw = "POST /a HTTP/1.1\r\nHost: x\r\nContent-Length: 5\r\nExpect: 100-continue\r\n\r\nhello";
    Request r;
    auto p1 = parse(raw.substr(0, 20), r);
    CHECK(p1.kind == ParseResult::Incomplete);
    CHECK(!p1.headers_complete);
    auto p2 = parse(raw.substr(0, raw.size() - 5), r);
    CHECK(p2.kind == ParseResult::Incomplete);
    CHECK(p2.headers_complete && p2.expect_continue);
    auto p3 = parse(raw, r);
    CHECK(p3.kind == ParseResult::Complete);
    CHECK_EQ(r.body, std::string("hello"));
}

TEST(net_http, parse_chunked_body) {
    std::string raw = "POST /a HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n5;ext=1\r\nhello\r\n6\r\n world\r\n0\r\nX-Trailer: 1\r\n\r\n";
    Request r;
    auto p = parse(raw, r);
    CHECK(p.kind == ParseResult::Complete);
    CHECK_EQ(r.body, std::string("hello world"));
    CHECK_EQ(p.consumed, raw.size());
    Request r2;
    CHECK(parse(raw.substr(0, raw.size() - 3), r2).kind == ParseResult::Incomplete);
    Request r3;
    CHECK(parse("POST /a HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\nzz\r\n", r3).kind == ParseResult::Error);
}

TEST(net_http, parse_errors_and_limits) {
    Request r;
    ParseLimits lim;
    lim.max_body_bytes = 10;
    lim.max_header_bytes = 100;
    auto big = parse("POST /a HTTP/1.1\r\nHost: x\r\nContent-Length: 11\r\n\r\n", r, lim);
    CHECK(big.kind == ParseResult::Error);
    CHECK_EQ(big.status, 413);
    auto hdr = parse("GET /a HTTP/1.1\r\nHost: x\r\nX: " + std::string(200, 'a'), r, lim);
    CHECK(hdr.kind == ParseResult::Error);
    CHECK_EQ(hdr.status, 431);
    CHECK_EQ(parse("GET /a HTTP/2.0\r\nHost: x\r\n\r\n", r).status, 505);
    CHECK_EQ(parse("GET /a HTTP/1.1\r\n\r\n", r).status, 400);   // 缺 Host
    CHECK_EQ(parse("GET  /a HTTP/1.1\r\nHost: x\r\n\r\n", r).status, 400);
    CHECK_EQ(parse("GET /a HTTP/1.1\r\nHost: x\r\nContent-Length: 1\r\nContent-Length: 2\r\n\r\n", r).status, 400);
    CHECK_EQ(parse("GET /a HTTP/1.1\r\nHost: x\r\nBad Name: 1\r\n\r\n", r).status, 400);
    CHECK_EQ(parse("POST /a HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\nContent-Length: 3\r\n\r\n", r).status, 400);
    // HTTP/1.0 默认关闭
    Request r10;
    CHECK(parse("GET /a HTTP/1.0\r\n\r\n", r10).kind == ParseResult::Complete);
    CHECK(!r10.keep_alive());
}

TEST(net_http, url_decode_and_query) {
    CHECK_EQ(url_decode("a%2Fb%zz+c", false), std::string("a/b%zz+c"));
    CHECK_EQ(url_decode("a+b%41", true), std::string("a bA"));
    auto q = parse_query("from=2026-11-26T08%3A00%3A00%2B08%3A00&flag&=x&a=1&a=2");
    CHECK_EQ(q.size(), size_t(5));
    CHECK_EQ(q[0].second, std::string("2026-11-26T08:00:00+08:00"));
    CHECK_EQ(q[1].first, std::string("flag"));
    CHECK_EQ(url_encode_component("数据 a.csv"), std::string("%E6%95%B0%E6%8D%AE%20a.csv"));
}

TEST(net_http, serialize_head) {
    Response r = Response::json(200, std::string("{}"));
    r.headers.set("Content-Length", "999");   // 用户写的长度被忽略
    std::string h = serialize_head(r, 2, true, false);
    CHECK(h.find("HTTP/1.1 200 OK\r\n") == 0);
    CHECK(h.find("Content-Length: 2\r\n") != std::string::npos);
    CHECK(h.find("999") == std::string::npos);
    CHECK(h.find("Connection: keep-alive") != std::string::npos);
    std::string c = serialize_head(Response::empty(200), -1, false, true);
    CHECK(c.find("Transfer-Encoding: chunked") != std::string::npos);
    CHECK(c.find("Connection: close") != std::string::npos);
    std::string n = serialize_head(Response::empty(304), 10, true, false);
    CHECK(n.find("Content-Length") == std::string::npos);
    auto e = Response::error("RATE_LIMITED", "x");
    CHECK_EQ(e.status, 429);
    CHECK_EQ(e.headers.get("Retry-After"), std::string("1"));
    CHECK_EQ(http_date(0), std::string("Thu, 01 Jan 1970 00:00:00 GMT"));
    CHECK(parse_http_date("Thu, 26 Nov 2026 08:02:15 GMT") == std::optional<int64_t>(1795680135));
}

TEST(net_router, params_methods_priority) {
    Router rt;
    auto h = [](const Request&) { return Response::empty(200); };
    rt.get("/api/v1/channels", h);
    rt.get("/api/v1/channels/{id}", h);
    rt.put("/api/v1/channels/{id}/valve", h);
    rt.post("/api/v1/channels/copy-config", h);
    rt.get("/api/v1/files/{type}/{name}", h);
    rt.get("/api/v1/static/{*rest}", h);
    rt.patch("/api/v1/settings", h, RouteOptions{RateClass::Auto, false, true});
    auto m = rt.match("PUT", "/api/v1/channels/3/valve", false);
    CHECK(m.route && m.route->pattern == "/api/v1/channels/{id}/valve");
    CHECK_EQ(m.params["id"], std::string("3"));
    auto m2 = rt.match("POST", "/API/V1/Channels/copy-config/", false);   // 字面段大小写不敏感，末尾斜杠忽略
    CHECK(m2.route && m2.route->pattern == "/api/v1/channels/copy-config");
    auto m3 = rt.match("DELETE", "/api/v1/channels/3", false);
    CHECK(!m3.route && m3.method_mismatch);
    CHECK_EQ(m3.allow, std::string("GET, HEAD"));
    auto m4 = rt.match("HEAD", "/api/v1/files/telemetry/a%20b.csv", false);
    CHECK(m4.route != nullptr);
    CHECK_EQ(m4.params["name"], std::string("a b.csv"));
    auto m5 = rt.match("GET", "/api/v1/static/a/b/c", false);
    CHECK(m5.route && m5.params["rest"] == "a/b/c");
    CHECK(!rt.match("GET", "/api/v1/nothing", false).route);
    CHECK(!rt.match("GET", "/api/v1/nothing", false).method_mismatch);
    // local_only：TCP 上当作不存在（404，而不是 405）
    auto m6 = rt.match("PATCH", "/api/v1/settings", false);
    CHECK(!m6.route && !m6.method_mismatch);
    CHECK(rt.match("PATCH", "/api/v1/settings", true).route != nullptr);
    CHECK(effective_rate_class(RateClass::Auto, "GET") == RateClass::Read);
    CHECK(effective_rate_class(RateClass::Auto, "DELETE") == RateClass::Write);
    CHECK(effective_rate_class(RateClass::Estop, "POST") == RateClass::Estop);
}

TEST(net_ws, frame_roundtrip_and_errors) {
    const uint8_t mask[4] = {1, 2, 3, 4};
    for (size_t len : {size_t(0), size_t(125), size_t(126), size_t(65535), size_t(65536), size_t(70000)}) {
        std::string payload(len, 'x');
        std::string f = ws::encode_frame(ws::kBinary, payload, true, mask);
        ws::Frame fr;
        size_t used = 0;
        uint16_t err = 0;
        CHECK_EQ(ws::parse_frame(f.data(), f.size(), 1 << 20, fr, used, err), 1);
        CHECK_EQ(used, f.size());
        CHECK(fr.masked && fr.fin && fr.opcode == ws::kBinary);
        CHECK(fr.payload == payload);
        CHECK_EQ(ws::parse_frame(f.data(), f.size() - 1, 1 << 20, fr, used, err), 0);
    }
    ws::Frame fr;
    size_t used;
    uint16_t err = 0;
    std::string big = ws::encode_frame(ws::kText, std::string(100, 'a'));
    CHECK_EQ(ws::parse_frame(big.data(), 4, 50, fr, used, err), -1);   // 只看帧头就判过大
    CHECK_EQ(err, ws::kCloseMessageTooBig);
    std::string ctl = ws::encode_frame(ws::kPing, std::string(126, 'a'));
    CHECK_EQ(ws::parse_frame(ctl.data(), ctl.size(), 1 << 20, fr, used, err), -1);
    CHECK_EQ(err, ws::kCloseProtocolError);
}

TEST(net_ws, assembler_fragments_control_and_limits) {
    const uint8_t mask[4] = {9, 8, 7, 6};
    ws::MessageAssembler a(16, true);
    std::string buf = ws::encode_frame(ws::kText, "hel", false, mask) + ws::encode_frame(ws::kPing, "p", true, mask) +
                      ws::encode_frame(ws::kContinuation, "lo", true, mask);
    std::vector<ws::MessageAssembler::Event> ev;
    CHECK(a.feed(buf, ev));
    CHECK_EQ(ev.size(), size_t(2));
    CHECK(ev[0].kind == ws::MessageAssembler::Event::Ping);
    CHECK(ev[1].kind == ws::MessageAssembler::Event::Message && ev[1].data == "hello");
    CHECK(buf.empty());
    // 累计超限
    ev.clear();
    buf = ws::encode_frame(ws::kText, std::string(10, 'a'), false, mask) + ws::encode_frame(ws::kContinuation, std::string(10, 'a'), true, mask);
    CHECK(!a.feed(buf, ev));
    CHECK(ev.back().kind == ws::MessageAssembler::Event::Error && ev.back().code == ws::kCloseMessageTooBig);
    // 未加掩码
    ws::MessageAssembler b(100, true);
    ev.clear();
    buf = ws::encode_frame(ws::kText, "x");
    CHECK(!b.feed(buf, ev));
    CHECK_EQ(ev.back().code, ws::kCloseProtocolError);
    // 非法 UTF-8
    ws::MessageAssembler c(100, true);
    ev.clear();
    buf = ws::encode_frame(ws::kText, std::string("\xC3\x28"), true, mask);
    CHECK(!c.feed(buf, ev));
    CHECK_EQ(ev.back().code, ws::kCloseInvalidData);
    // 关闭帧
    ws::MessageAssembler d(100, true);
    ev.clear();
    buf = ws::encode_close(1000, "bye", mask);
    CHECK(d.feed(buf, ev));
    CHECK(ev.back().kind == ws::MessageAssembler::Event::Close && ev.back().code == 1000 && ev.back().data == "bye");
    CHECK(ws::valid_utf8("温度 ok", std::string("温度 ok").size()));
    CHECK(!ws::valid_utf8("\xED\xA0\x80", 3));   // 代理项
    CHECK(!ws::valid_utf8("\xE6\x95", 2));       // 截断
}

TEST(net_http, file_response_range_etag) {
    char tmpl[] = "/tmp/net-file-XXXXXX";
    int fd = mkstemp(tmpl);
    std::string content;
    for (int i = 0; i < 1000; ++i) content += static_cast<char>('0' + i % 10);
    CHECK(write(fd, content.data(), content.size()) == static_cast<ssize_t>(content.size()));
    close(fd);
    auto body_of = [](const Response& r) {
        std::string s(r.file->length, '\0');
        CHECK(pread(r.file->fd, &s[0], s.size(), static_cast<off_t>(r.file->offset)) == static_cast<ssize_t>(s.size()));
        return s;
    };
    Request req;
    Response full = file_response(req, tmpl, "text/csv; charset=utf-8", "数据.csv");
    CHECK_EQ(full.status, 200);
    CHECK(full.file && full.file->length == 1000);
    std::string etag = full.headers.get("ETag");
    CHECK(etag.rfind("W/\"3e8-", 0) == 0);
    CHECK_EQ(full.headers.get("Accept-Ranges"), std::string("bytes"));
    CHECK(full.headers.get("Content-Disposition").find("filename*=UTF-8''%E6%95%B0%E6%8D%AE.csv") != std::string::npos);

    Request r1;
    r1.headers.add("Range", "bytes=10-19");
    Response p = file_response(r1, tmpl, "text/csv");
    CHECK_EQ(p.status, 206);
    CHECK_EQ(p.headers.get("Content-Range"), std::string("bytes 10-19/1000"));
    CHECK_EQ(body_of(p), content.substr(10, 10));

    Request r2;
    r2.headers.add("Range", "bytes=-5");
    Response p2 = file_response(r2, tmpl, "text/csv");
    CHECK_EQ(p2.status, 206);
    CHECK_EQ(body_of(p2), content.substr(995));

    Request r3;
    r3.headers.add("Range", "bytes=990-");
    CHECK_EQ(file_response(r3, tmpl, "text/csv").headers.get("Content-Range"), std::string("bytes 990-999/1000"));

    Request r4;
    r4.headers.add("Range", "bytes=1000-");
    Response p4 = file_response(r4, tmpl, "text/csv");
    CHECK_EQ(p4.status, 416);
    CHECK_EQ(p4.headers.get("Content-Range"), std::string("bytes */1000"));

    Request r5;
    r5.headers.add("Range", "bytes=0-1,5-6");   // 多段：忽略
    CHECK_EQ(file_response(r5, tmpl, "text/csv").status, 200);

    Request r6;
    r6.headers.add("If-None-Match", etag);
    Response nm = file_response(r6, tmpl, "text/csv");
    CHECK_EQ(nm.status, 304);
    CHECK(!nm.file);

    Request r7;   // If-Range 带弱 ETag：强比较不成立 → 忽略 Range（同 ASP.NET）
    r7.headers.add("Range", "bytes=0-9");
    r7.headers.add("If-Range", etag);
    CHECK_EQ(file_response(r7, tmpl, "text/csv").status, 200);

    Request r8;   // If-Range 带日期（不早于修改时间）→ 照常 206
    r8.headers.add("Range", "bytes=0-9");
    r8.headers.add("If-Range", full.headers.get("Last-Modified"));
    CHECK_EQ(file_response(r8, tmpl, "text/csv").status, 206);

    Request r9;
    CHECK_EQ(file_response(r9, "/nonexistent/zzz", "text/csv").status, 404);
    unlink(tmpl);
}
