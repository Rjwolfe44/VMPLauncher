using System.IO;
using System.IO.Compression;
using System.Text;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Publishing;
using Xunit;

namespace VmpLauncher.Tests.Publishing;

public sealed class SecretScannerTests : IDisposable
{
    private const string Password = "Sup3r-Secret-Pw";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vmpl-scan-" + Guid.NewGuid().ToString("N"));

    public SecretScannerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private SecretScanner Scanner()
    {
        var s = new SecretScanner();
        s.Add(Password, "test password");
        return s;
    }

    [Fact]
    public void Finds_a_password_inside_a_gzipped_file()
    {
        using (var gz = new GZipStream(File.Create(Path.Combine(_dir, "inventory.json.gz")), CompressionLevel.Optimal))
        {
            var bytes = Encoding.UTF8.GetBytes("{\"files\":[\"x\"],\"note\":\"" + Password + "\"}");
            gz.Write(bytes);
        }
        Assert.NotEmpty(Scanner().ScanDirectory(_dir));
    }

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("xy")]
    public void Finds_a_password_inside_inline_override_content_at_any_offset(string prefix)
    {
        // Base64 of "<prefix><password>": the plain base64 needle only matches when the secret starts on a 3-byte boundary.
        var inline = Convert.ToBase64String(Encoding.UTF8.GetBytes(prefix + "password=" + Password + "\n"));
        File.WriteAllText(Path.Combine(_dir, "p.vmpack.lock.json"), "{\"overrides\":[{\"content\":{\"inlineBase64\":\"" + inline + "\"}}]}");
        Assert.Contains(Scanner().ScanDirectory(_dir), f => f.Entry == "inline override content");
    }

    [Fact]
    public void A_configured_but_missing_private_connection_file_fails_closed()
    {
        var ex = Assert.Throws<LauncherException>(() => new SecretScanner().LoadFromHost(null, Path.Combine(_dir, "connection.private.json")));
        Assert.Contains("missing", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_private_connection_file_without_secrets_fails_closed()
    {
        var file = Path.Combine(_dir, "connection.private.json");
        File.WriteAllText(file, "{\"ServerName\":\"x\",\"Port\":8800}");
        Assert.Throws<LauncherException>(() => new SecretScanner().LoadFromHost(null, file));
    }

    [Fact]
    public void Private_tag_names_are_safe_for_urls()
    {
        Assert.Equal("apex-2.15.0.276_g5f225d83", CustomPackageBuilder.PrivateTag("apex-", "2.15.0.276+g5f225d83"));
        Assert.Equal("1_2", CustomPackageBuilder.PrivateTag(null, "1:2"));
        Assert.True(CustomPackageBuilder.IsPrivate("private-github"));
        Assert.True(CustomPackageBuilder.IsPrivate("private-sideload"));
        Assert.False(CustomPackageBuilder.IsPrivate("github-release"));
    }
}
