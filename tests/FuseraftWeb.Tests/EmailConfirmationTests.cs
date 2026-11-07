using Microsoft.AspNetCore.DataProtection;
using fuseraft.Web.Services.Email;
using fuseraft.Web.Services.Users;

namespace FuseraftWeb.Tests;

public sealed class EmailConfirmationTests
{
    private readonly EmailConfirmation _confirmation = new(new EphemeralDataProtectionProvider());

    private static WebUser User(string id, string? email = null) => new() { Id = id, UserName = id, Home = "/tmp", Email = email };

    [Fact]
    public void ALink_ConfirmsItsAddress_ForItsAccountOnly()
    {
        var bob   = User("bob");
        var token = _confirmation.Token(bob, "bob@example.com");

        Assert.Equal("bob@example.com", _confirmation.Check(bob, token));
        Assert.Null(_confirmation.Check(User("eve"), token));
        Assert.Null(_confirmation.Check(bob, token[..^4] + "AAAA"));
        Assert.Null(_confirmation.Check(bob, null));
    }

    [Fact]
    public void ALink_StopsWorking_OnceTheAddressChanges()
    {
        var bob   = User("bob", "old@example.com");
        var token = _confirmation.Token(bob, "new@example.com");
        bob.Email = "new@example.com";

        Assert.Null(_confirmation.Check(bob, token));
    }

    [Fact]
    public void ALink_Expires()
    {
        var expired = new EmailConfirmation(new EphemeralDataProtectionProvider()) { Lifetime = TimeSpan.FromSeconds(-1) };
        var bob = User("bob");

        Assert.Null(expired.Check(bob, expired.Token(bob, "bob@example.com")));
    }

    [Theory]
    [InlineData(" bob@example.com ", "bob@example.com")]
    [InlineData("Bob <bob@example.com>", null)]
    [InlineData("bob@example.com\r\nBcc: eve@example.com", null)]
    [InlineData("bob smith@example.com", null)]
    [InlineData("bob", null)]
    [InlineData("", null)]
    public void Normalize_TakesOnlyABareAddress(string input, string? expected) =>
        Assert.Equal(expected, EmailConfirmation.Normalize(input));

    [Fact]
    public void Links_AreRateLimitedPerAccount()
    {
        var bob = User("bob");

        Assert.True(_confirmation.TryReserve(bob));
        Assert.False(_confirmation.TryReserve(bob));
        Assert.True(_confirmation.TryReserve(User("carol")));
    }
}
