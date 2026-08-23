using System.IO.Compression;
using FluentAssertions;
using KidControl.Infrastructure.Update;
using Xunit;

namespace KidControl.Infrastructure.Tests;

/// <summary>
/// Covers the self-update zip handling: a normal setup archive extracts and the
/// installer is located, and a malicious "zip-slip" entry is rejected rather than
/// written outside the staging directory. Cross-platform (no Windows APIs touched).
/// </summary>
public sealed class UpdateServiceZipTests : IDisposable
{
    private readonly string _root;

    public UpdateServiceZipTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "kc-ziptests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void ExtractZip_And_FindInstaller_Locate_The_Installer()
    {
        var zip = Path.Combine(_root, "setup.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "KidControl.Installer.exe", "installer");
            WriteEntry(archive, "KidControl.ServiceHost.exe", "service");
            WriteEntry(archive, "KidControl.UiHost.exe", "ui");
        }

        var dest = Path.Combine(_root, "extracted");
        UpdateService.ExtractZip(zip, dest);

        File.Exists(Path.Combine(dest, "KidControl.Installer.exe")).Should().BeTrue();
        File.Exists(Path.Combine(dest, "KidControl.ServiceHost.exe")).Should().BeTrue();

        var installer = UpdateService.FindInstaller(dest);
        installer.Should().NotBeNull();
        Path.GetFileName(installer!).Should().Be("KidControl.Installer.exe");
    }

    [Fact]
    public void ExtractZip_Rejects_ZipSlip_Entry()
    {
        var zip = Path.Combine(_root, "evil.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            // An entry that tries to climb out of the extraction directory.
            WriteEntry(archive, "../escaped.txt", "pwned");
        }

        var dest = Path.Combine(_root, "extract-evil");

        var act = () => UpdateService.ExtractZip(zip, dest);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*escapes the target directory*");

        File.Exists(Path.Combine(_root, "escaped.txt")).Should().BeFalse();
    }

    [Fact]
    public void FindInstaller_Returns_Null_When_Absent()
    {
        var dir = Path.Combine(_root, "empty");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "readme.txt"), "no installer here");

        UpdateService.FindInstaller(dir).Should().BeNull();
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }
}
