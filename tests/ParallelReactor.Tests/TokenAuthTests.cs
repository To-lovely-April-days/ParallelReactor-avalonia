using System;
using System.Collections.Generic;
using ParallelReactor.Api;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>Bearer 令牌校验（设计稿 §4.2）。</summary>
public class TokenAuthTests
{
    private static ApiConfig Cfg(params ApiToken[] tokens) => new() { Tokens = new List<ApiToken>(tokens) };

    [Fact]
    public void Hash_IsLowercaseSha256Hex()
    {
        // echo -n devtoken | shasum -a 256
        Assert.Equal("9428e07c68054de014032f21e0716501aa937714fd31033caf589e66b276e53b", TokenAuth.Hash("devtoken"));
        Assert.Equal(64, TokenAuth.Hash("").Length);
    }

    [Fact]
    public void Authenticate_MatchesLowerAndUpperHex()
    {
        var cfg = Cfg(new ApiToken { Name = "pc", Sha256 = TokenAuth.Hash("secret").ToUpperInvariant(), Role = "Operator" });
        var r = TokenAuth.Authenticate("Bearer secret", cfg);
        Assert.True(r.Ok);
        Assert.Equal("pc", r.Name);
        Assert.Equal("operator", r.Role);
        Assert.True(r.Estop);   // operator 默认带急停权限
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    [InlineData("Basic c2VjcmV0")]
    [InlineData("secret")]
    [InlineData("Bearer wrong")]
    public void Authenticate_RejectsMissingOrGarbled(string? header)
    {
        var cfg = Cfg(new ApiToken { Name = "pc", Sha256 = TokenAuth.Hash("secret") });
        Assert.False(TokenAuth.Authenticate(header, cfg).Ok);
    }

    [Fact]
    public void Authenticate_IsCaseInsensitiveOnScheme_AndTrimsSpaces()
    {
        var cfg = Cfg(new ApiToken { Name = "pc", Sha256 = TokenAuth.Hash("secret") });
        Assert.True(TokenAuth.Authenticate("bearer   secret  ", cfg).Ok);
    }

    [Fact]
    public void Viewer_WithoutEstopFlag_HasNoEstop()
    {
        var cfg = Cfg(new ApiToken { Name = "watch", Sha256 = TokenAuth.Hash("w"), Role = "viewer", Estop = false },
                      new ApiToken { Name = "watch-estop", Sha256 = TokenAuth.Hash("e"), Role = "viewer", Estop = true });
        Assert.False(TokenAuth.AuthenticateToken("w", cfg).Estop);
        Assert.True(TokenAuth.AuthenticateToken("e", cfg).Estop);
        Assert.Equal("watch-estop", TokenAuth.AuthenticateToken("e", cfg).Name);
    }

    [Fact]
    public void BadHashEntries_AreSkipped()
    {
        var cfg = Cfg(new ApiToken { Name = "broken", Sha256 = "abc" }, new ApiToken { Name = "ok", Sha256 = TokenAuth.Hash("t") });
        Assert.False(TokenAuth.AuthenticateToken("abc", cfg).Ok);
        Assert.Equal("ok", TokenAuth.AuthenticateToken("t", cfg).Name);
    }

    [Fact]
    public void Roles_Rank()
    {
        Assert.True(TokenAuth.HasRole(new AuthResult(true, "a", "admin", true), "operator"));
        Assert.True(TokenAuth.HasRole(new AuthResult(true, "a", "viewer", false), "viewer"));
        Assert.False(TokenAuth.HasRole(new AuthResult(true, "a", "viewer", false), "operator"));
        Assert.False(TokenAuth.HasRole(AuthResult.Denied, "viewer"));
        Assert.Equal("viewer", TokenAuth.NormalizeRole("root"));
    }

    [Fact]
    public void KeyedThrottle_OnePerWindowPerKey()
    {
        var t = new KeyedThrottle(TimeSpan.FromMinutes(1));
        var now = new DateTime(2026, 11, 6, 8, 0, 0, DateTimeKind.Utc);
        Assert.True(t.Allow("10.0.0.1", now));
        Assert.False(t.Allow("10.0.0.1", now.AddSeconds(30)));
        Assert.True(t.Allow("10.0.0.2", now.AddSeconds(30)));
        Assert.True(t.Allow("10.0.0.1", now.AddSeconds(61)));
    }

    [Fact]
    public void FailureLimiter_BlocksAfterLimitWithinWindow()
    {
        var f = new FailureLimiter(3, TimeSpan.FromSeconds(1));
        var now = new DateTime(2026, 11, 6, 8, 0, 0, DateTimeKind.Utc);
        Assert.False(f.IsBlocked("ip", now));
        Assert.False(f.RecordFailure("ip", now));
        Assert.False(f.RecordFailure("ip", now.AddMilliseconds(100)));
        Assert.True(f.RecordFailure("ip", now.AddMilliseconds(200)));     // 第 3 次：到达上限
        Assert.True(f.IsBlocked("ip", now.AddMilliseconds(900)));
        Assert.False(f.IsBlocked("other", now.AddMilliseconds(900)));
        Assert.False(f.IsBlocked("ip", now.AddSeconds(1)));                 // 新窗口
        Assert.False(f.RecordFailure("ip", now.AddSeconds(1)));
    }
}
