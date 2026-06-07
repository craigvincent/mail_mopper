using MailMopper.Config;
using MailMopper.Services;

namespace MailMopper.Tests.Services;

public class GmailAuthServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _settings;
    private readonly GmailSession _session;

    public GmailAuthServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"auth_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _settings = new AppSettings
        {
            Gmail = new GmailSettings
            {
                CredentialsPath = Path.Combine(_tempDir, "credentials.json"),
                TokenPath = Path.Combine(_tempDir, "tokens")
            }
        };

        _session = new GmailSession();
    }

    public void Dispose()
    {
        try
        { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    #region TryRestoreSessionAsync

    [Fact]
    public async Task TryRestoreSessionAsync_CredentialsFileNotFound_ReturnsFalse()
    {
        var service = new GmailAuthService(_settings, _session);

        var result = await service.TryRestoreSessionAsync(CancellationToken.None);

        Assert.False(result);
        Assert.False(_session.IsAuthenticated);
    }

    [Fact]
    public async Task TryRestoreSessionAsync_TokenDirMissing_ReturnsFalse()
    {
        File.WriteAllText(_settings.Gmail.CredentialsPath, "fake-json");
        _settings.Gmail.TokenPath = Path.Combine(_tempDir, "nonexistent-dir");

        var service = new GmailAuthService(_settings, _session);

        var result = await service.TryRestoreSessionAsync(CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task TryRestoreSessionAsync_TokenFileMissing_ReturnsFalse()
    {
        File.WriteAllText(_settings.Gmail.CredentialsPath, "fake-json");
        var tokenDir = Path.Combine(_tempDir, "empty-tokens");
        Directory.CreateDirectory(tokenDir);
        _settings.Gmail.TokenPath = tokenDir;

        var service = new GmailAuthService(_settings, _session);

        var result = await service.TryRestoreSessionAsync(CancellationToken.None);

        Assert.False(result);
    }

    #endregion

    #region Logout

    [Fact]
    public void Logout_ServiceNotAuthenticated_DoesNotThrow()
    {
        var service = new GmailAuthService(_settings, _session);

        // Should not throw when service is null
        service.Logout();

        Assert.Null(_session.Service);
    }

    [Fact]
    public void Logout_TokenDirMissing_DoesNotThrow()
    {
        _settings.Gmail.TokenPath = Path.Combine(_tempDir, "no-such-dir");
        var service = new GmailAuthService(_settings, _session);

        // Should not throw when token directory does not exist
        service.Logout();

        Assert.Null(_session.Service);
    }

    [Fact]
    public void Logout_DeletesTokenFiles()
    {
        var tokenDir = Path.Combine(_tempDir, "populated-tokens");
        Directory.CreateDirectory(tokenDir);
        File.WriteAllText(Path.Combine(tokenDir, "token-response"), "data");
        File.WriteAllText(Path.Combine(tokenDir, "other-file"), "more-data");
        _settings.Gmail.TokenPath = tokenDir;

        var service = new GmailAuthService(_settings, _session);

        service.Logout();

        Assert.Empty(Directory.GetFiles(tokenDir));
    }

    #endregion

    #region Constructor Validation

    [Fact]
    public void Constructor_NullSettings_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => new GmailAuthService(null!, _session));
        Assert.Equal("settings", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullSession_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => new GmailAuthService(_settings, null!));
        Assert.Equal("session", ex.ParamName);
    }

    #endregion
}
