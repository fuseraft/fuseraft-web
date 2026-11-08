using Microsoft.AspNetCore.DataProtection;
using fuseraft.Web.Services.Webhooks;

namespace FuseraftWeb.Tests;

public sealed class WebhookStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("fuseraft-web-webhooks").FullName;
    private readonly EphemeralDataProtectionProvider _protection = new();
    private string FilePath => Path.Combine(_dir, "webhooks.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Secret_IsEncryptedOnDisk_AndSurvivesAReload()
    {
        var hook = new WebhookStore(FilePath, _protection).Add(new Webhook { OwnerId = "bob", Name = "ci", Url = "https://h.example.com" });

        var reloaded = new WebhookStore(FilePath, _protection);
        var secret = reloaded.Secret(hook.Id)!;

        Assert.StartsWith("whsec_", secret);
        Assert.Equal(32, Convert.FromBase64String(secret[6..]).Length);
        Assert.DoesNotContain(secret, File.ReadAllText(FilePath));
        Assert.DoesNotContain("h.example.com", File.ReadAllText(FilePath));
        Assert.Equal(("ci", "https://h.example.com"), (Assert.Single(reloaded.For("bob")).Name, reloaded.Find(hook.Id)!.Url));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(FilePath));
    }

    [Fact]
    public void RegenerateSecret_ReplacesIt()
    {
        var store = new WebhookStore(FilePath, _protection);
        var hook = store.Add(new Webhook { OwnerId = "bob", Url = "https://h.example.com" });
        var before = store.Secret(hook.Id);

        var after = store.RegenerateSecret(hook.Id);

        Assert.NotEqual(before, after);
        Assert.Equal(after, store.Secret(hook.Id));
    }

    [Fact]
    public void Hooks_HandedOut_AreCopies()
    {
        var store = new WebhookStore(FilePath, _protection);
        var hook = store.Add(new Webhook { OwnerId = "bob", Url = "https://h.example.com" });

        store.Find(hook.Id)!.Url = "https://elsewhere.example.com";

        Assert.Equal("https://h.example.com", store.Find(hook.Id)!.Url);
    }

    [Fact]
    public void EachUser_HasALimit()
    {
        var store = new WebhookStore(FilePath, _protection);
        for (var i = 0; i < WebhookStore.MaxPerUser; i++) store.Add(new Webhook { OwnerId = "bob", Url = "https://h.example.com" });

        Assert.Throws<InvalidOperationException>(() => store.Add(new Webhook { OwnerId = "bob", Url = "https://h.example.com" }));
        store.Add(new Webhook { OwnerId = "carol", Url = "https://h.example.com" });
    }

    [Fact]
    public void AfterTheKeysChange_AWebhookIsOff_AndHasNoSecret()
    {
        var hook = new WebhookStore(FilePath, _protection).Add(new Webhook { OwnerId = "bob", Url = "https://h.example.com" });

        var rekeyed = new WebhookStore(FilePath, new EphemeralDataProtectionProvider());

        Assert.Null(rekeyed.Secret(hook.Id));
        Assert.False(rekeyed.Find(hook.Id)!.Enabled);
    }
}
