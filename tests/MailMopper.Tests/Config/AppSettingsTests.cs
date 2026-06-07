using MailMopper.Commands;

namespace MailMopper.Tests.Config;

public class AppSettingsTests
{
    [Fact]
    public void LoadSettings_ReturnsNonNullSettings()
    {
        var settings = CommandHelper.LoadSettings();

        Assert.NotNull(settings);
        Assert.NotNull(settings.Gmail);
        Assert.NotNull(settings.Ml);
        Assert.NotNull(settings.Classification);
        Assert.NotNull(settings.Actions);
    }

    [Fact]
    public void LoadSettings_GmailSettings_HasDefaultValues()
    {
        var settings = CommandHelper.LoadSettings();

        Assert.False(string.IsNullOrWhiteSpace(settings.Gmail.CredentialsPath));
        Assert.False(string.IsNullOrWhiteSpace(settings.Gmail.TokenPath));
        Assert.True(settings.Gmail.BatchSize > 0);
        Assert.NotEmpty(settings.Gmail.Scopes);
        Assert.Contains("https://www.googleapis.com/auth/gmail.modify", settings.Gmail.Scopes);
    }

    [Fact]
    public void LoadSettings_ClassificationSettings_HasRulesPath()
    {
        var settings = CommandHelper.LoadSettings();

        Assert.False(string.IsNullOrWhiteSpace(settings.Classification.RulesPath));
        Assert.Contains("default-rules.json", settings.Classification.RulesPath);
    }

    [Fact]
    public void LoadSettings_ActionSettings_HasDefaults()
    {
        var settings = CommandHelper.LoadSettings();

        Assert.True(settings.Actions.TrashBatchSize > 0);
        Assert.True(settings.Actions.DryRunDefault);
    }

    [Fact]
    public void LoadSettings_ResolvesRelativeCredentialPath()
    {
        var settings = CommandHelper.LoadSettings();

        Assert.True(Path.IsPathRooted(settings.Gmail.CredentialsPath));
        Assert.True(Path.IsPathRooted(settings.Gmail.TokenPath));
        Assert.True(Path.IsPathRooted(settings.Classification.RulesPath));
    }

    [Fact]
    public void LoadSettings_MlModelPath_NullByDefault()
    {
        var settings = CommandHelper.LoadSettings();

        Assert.Null(settings.Ml.ModelPath);
    }
}
