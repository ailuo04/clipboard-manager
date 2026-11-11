using System.Text.Json;
using ClipboardManager.Core.Updates;
using Xunit;

namespace ClipboardManager.Core.Tests;

public sealed class UpdateManifestTests
{
    private static readonly string InstallDirectory = Path.Combine(Path.GetTempPath(), "ClipboardManager-TestInstall");
    private static readonly string DataDirectory = Path.Combine(Path.GetTempPath(), "ClipboardManager-TestData");

    [Fact]
    public void Parses_a_manifest_when_release_version_platform_and_file_metadata_match()
    {
        UpdateManifest manifest = UpdateManifest.ParseAndValidate(
            JsonSerializer.SerializeToUtf8Bytes(Manifest("1.2.3", "win-x64", "ClipboardManager.exe")),
            "1.2.3", "win-x64", InstallDirectory, DataDirectory);

        Assert.Equal(1, manifest.Schema);
        Assert.Equal("ClipboardManager", manifest.Application);
        Assert.Equal("1.2.3", manifest.Version);
        Assert.Equal("win-x64", manifest.Platform);
        Assert.Equal(UpdateManifest.SupportedPackageKind, manifest.PackageKind);
        Assert.Single(manifest.Files);
        Assert.Equal("ClipboardManager.exe", manifest.Files[0].Path);
        Assert.Equal(10, manifest.Files[0].Size);
        Assert.Equal(5, manifest.Files[0].CompressedSize);
    }

    [Theory]
    [InlineData("../history.db")]
    [InlineData("folder/../../history.db")]
    [InlineData("C:/Windows/system.ini")]
    [InlineData("CON.txt")]
    [InlineData("folder/NUL/file.dll")]
    [InlineData("folder/name./file.dll")]
    [InlineData("folder/name /file.dll")]
    public void Rejects_unsafe_manifest_paths(string path)
    {
        Assert.Throws<InvalidDataException>(() => Parse(Manifest("1.2.3", "win-x64", path)));
    }

    [Fact]
    public void Rejects_manifest_targets_that_overlap_the_user_data_directory()
    {
        string install = Path.Combine(Path.GetTempPath(), "ClipboardManager-Install");
        string data = Path.Combine(install, "userdata");

        Assert.Throws<InvalidDataException>(() => UpdateManifest.ParseAndValidate(
            JsonSerializer.SerializeToUtf8Bytes(Manifest("1.2.3", "win-x64", "userdata/history.db")),
            "1.2.3", "win-x64", install, data));
    }

    [Fact]
    public void Rejects_an_update_manifest_for_a_different_release_or_platform()
    {
        Assert.Throws<InvalidDataException>(() => UpdateManifest.ParseAndValidate(
            JsonSerializer.SerializeToUtf8Bytes(Manifest("1.2.4", "win-x64", "ClipboardManager.exe")),
            "1.2.3", "win-x64", InstallDirectory, DataDirectory));
        Assert.Throws<InvalidDataException>(() => UpdateManifest.ParseAndValidate(
            JsonSerializer.SerializeToUtf8Bytes(Manifest("1.2.3", "win-arm64", "ClipboardManager.exe")),
            "1.2.3", "win-x64", InstallDirectory, DataDirectory));
    }

    [Fact]
    public void Rejects_duplicate_paths_and_files_above_the_per_file_limit()
    {
        var duplicate = Manifest("1.2.3", "win-x64", "ClipboardManager.exe");
        duplicate["files"] = new[]
        {
            FileEntry("ClipboardManager.exe", 10, 5),
            FileEntry("clipboardmanager.EXE", 10, 5)
        };
        Assert.Throws<InvalidDataException>(() => Parse(duplicate));

        Assert.Throws<InvalidDataException>(() => Parse(Manifest("1.2.3", "win-x64", "large.dll", 512L * 1024 * 1024 + 1)));
    }
    [Fact]
    public void Rejects_a_legacy_WPF_package_kind_even_when_release_metadata_is_otherwise_valid()
    {
        var legacy = Manifest("1.2.3", "win-x64", "ClipboardManager.exe");
        legacy.Remove("packageKind");
        legacy["packaging"] = "sdk-self-contained";
        Assert.Throws<InvalidDataException>(() => Parse(legacy));
    }
    [Fact]
    public void Rejects_reparse_point_paths_even_when_manifest_segments_are_relative()
    {
        string install = Path.Combine(Path.GetTempPath(), "ClipboardManager-LinkInstall");
        string data = Path.Combine(Path.GetTempPath(), "ClipboardManager-LinkData");
        string linkedPath = Path.Combine(install, "linked");
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(Manifest("1.2.3", "win-x64", "linked/ClipboardManager.exe"));

        Assert.Throws<InvalidDataException>(() => UpdateManifest.ParseAndValidate(json, "1.2.3", "win-x64", install, data,
            path => path.Equals(linkedPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)));
    }

    [Fact]
    public void Rejects_manifest_metadata_and_compressed_or_expanded_totals_above_limits()
    {
        Assert.Throws<InvalidDataException>(() => UpdateManifest.ParseAndValidate(new byte[UpdateManifest.MaximumMetadataBytes + 1],
            "1.2.3", "win-x64", InstallDirectory, DataDirectory));

        var expanded = Manifest("1.2.3", "win-x64", "file0.bin");
        expanded["files"] = Enumerable.Range(0, 5).Select(index => FileEntry($"file{index}.bin", UpdateManifest.MaximumFileBytes, 1)).ToArray();
        Assert.Throws<InvalidDataException>(() => Parse(expanded));

        var compressed = Manifest("1.2.3", "win-x64", "file0.bin");
        compressed["files"] = Enumerable.Range(0, 5).Select(index => FileEntry($"file{index}.bin", 1, UpdateManifest.MaximumFileBytes, new string((char)('a' + index), 64))).ToArray();
        Assert.Throws<InvalidDataException>(() => Parse(compressed));
    }
    [Fact]
    public void Rejects_duplicate_json_properties_in_release_manifests()
    {
        const string duplicate = "{\"schema\":1,\"schema\":1,\"application\":\"ClipboardManager\",\"version\":\"1.2.3\",\"platform\":\"win-x64\",\"packageKind\":\"winui3\",\"files\":[{\"path\":\"ClipboardManager.exe\",\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"size\":10,\"compressedSize\":5}]}";

        Assert.Throws<InvalidDataException>(() => UpdateManifest.ParseAndValidate(
            System.Text.Encoding.UTF8.GetBytes(duplicate), "1.2.3", "win-x64", InstallDirectory, DataDirectory));
    }
    [Fact]
    public void Requires_the_new_application_package_kind_field()
    {
        var missing = Manifest("1.2.3", "win-x64", "ClipboardManager.exe");
        missing.Remove("packageKind");
        missing["packaging"] = UpdateManifest.SupportedPackageKind;

        Assert.Throws<InvalidDataException>(() => Parse(missing));
    }
    [Fact]
    public void Allows_an_installation_root_that_contains_data_when_this_manifest_does_not_overlap_it()
    {
        string install = Path.Combine(Path.GetTempPath(), "ClipboardManager-InstallWithData");
        string data = Path.Combine(install, "userdata");
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(Manifest("1.2.3", "win-x64", "ClipboardManager.exe"));

        UpdateManifest manifest = UpdateManifest.ParseAndValidate(json, "1.2.3", "win-x64", install, data);

        Assert.Single(manifest.Files);
    }
    private static UpdateManifest Parse(Dictionary<string, object> manifest)
        => UpdateManifest.ParseAndValidate(JsonSerializer.SerializeToUtf8Bytes(manifest), "1.2.3", "win-x64", InstallDirectory, DataDirectory);

    private static Dictionary<string, object> Manifest(string version, string platform, string path, long size = 10)
        => new()
        {
            ["schema"] = 1,
            ["application"] = "ClipboardManager",
            ["version"] = version,
            ["platform"] = platform,
            ["packageKind"] = "winui3",
            ["files"] = new[] { FileEntry(path, size, 5) }
        };

    private static Dictionary<string, object> FileEntry(string path, long size, long compressedSize, string? sha256 = null)
        => new()
        {
            ["path"] = path,
            ["sha256"] = sha256 ?? new string('a', 64),
            ["size"] = size,
            ["compressedSize"] = compressedSize
        };
}










