using Microsoft.Playwright;

namespace JellyfinAndroidTranscoder.IntegrationTests;

public sealed class ConfigurationPageTests
{
    [Fact]
    public async Task ConfigurationPageUsesPlainServerGeneratedQrImage()
    {
        var repoRoot = FindRepoRoot();
        var pagePath = Path.Combine(
            repoRoot,
            "third_party",
            "jellyfin-android-transcoder",
            "jellyfin-android-transcoder",
            "src",
            "Jellyfin.Plugin.AndroidTranscoder",
            "Configuration",
            "configPage.html");
        var html = await File.ReadAllTextAsync(pagePath, CancellationToken.None);

        Assert.DoesNotContain("api.qrserver", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("txtPairingUrl", html, StringComparison.Ordinal);
        Assert.DoesNotContain("pairUrl", html, StringComparison.Ordinal);
        Assert.DoesNotContain("${", html, StringComparison.Ordinal);
        Assert.DoesNotContain("`", html, StringComparison.Ordinal);
        Assert.Contains("<img class=\"jfat-qr\" src=\"/AndroidTranscoder/PairingQr.svg\"", html, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "JellyfinAndroidTranscoderIntegration.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not find repository root.");
    }
}
