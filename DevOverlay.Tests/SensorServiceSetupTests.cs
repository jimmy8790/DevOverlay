using System.Diagnostics;
using DevOverlay.SensorServiceSetup;
using Xunit;

namespace DevOverlay.Tests;

public sealed class SensorServiceSetupTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), $"devoverlay setup {Guid.NewGuid():N}");
    private readonly string _programFiles;
    private readonly SensorServiceInstallLayout _layout;
    private readonly FakeServiceControl _scm = new();
    private readonly FakeSecurity _security = new();

    public SensorServiceSetupTests()
    {
        _programFiles = Path.Combine(_sandbox, "Program Files");
        Directory.CreateDirectory(_programFiles);
        _layout = new SensorServiceInstallLayout(_programFiles);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void LayoutUsesFixedProgramFilesLocationIndependentOfThePackage()
    {
        var layout = new SensorServiceInstallLayout(@"C:\Program Files\");
        Assert.Equal(@"C:\Program Files\DevOverlaySensorService\current\DevOverlay.SensorService.exe", layout.ExecutablePath);
        Assert.Equal("\"C:\\Program Files\\DevOverlaySensorService\\current\\DevOverlay.SensorService.exe\"", layout.QuotedExecutablePath);
        Assert.Throws<ArgumentException>(() => new SensorServiceInstallLayout("Program Files"));
        Assert.Throws<ArgumentException>(() => new SensorServiceInstallLayout(""));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\DevOverlaySensorService\\current\\DevOverlay.SensorService.exe\"", true)]
    [InlineData("\"c:\\program files\\devoverlaysensorservice\\CURRENT\\devoverlay.sensorservice.exe\"", true)]
    [InlineData("\"C:\\Users\\dev\\DevOverlay\\bin\\Debug\\net8.0-windows10.0.19041.0\\SensorService\\DevOverlay.SensorService.exe\"", false)]
    [InlineData("\"C:\\Users\\dev\\Downloads\\DevOverlay-v0.1.0-win-x64\\SensorService\\DevOverlay.SensorService.exe\"", false)]
    [InlineData("C:\\Program Files\\DevOverlaySensorService\\current\\DevOverlay.SensorService.exe", false)] // unquoted with spaces
    [InlineData("\"C:\\Program Files\\DevOverlaySensorService\\current\\..\\..\\Evil\\DevOverlay.SensorService.exe\"", false)]
    [InlineData("\"DevOverlaySensorService\\current\\DevOverlay.SensorService.exe\"", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyTheProtectedCopyIsRecognizedAsTheInstalledImage(string? imagePath, bool expected) =>
        Assert.Equal(expected, new SensorServiceInstallLayout(@"C:\Program Files").IsInstalledImage(imagePath));

    [Fact]
    public void InstallCopiesPayloadAndRegistersOnlyTheProtectedCopy()
    {
        var source = CreatePayload("Downloads\\DevOverlay v0.1.0\\SensorService", "v1");

        Installer().Install(source);

        Assert.Equal(_layout.QuotedExecutablePath, _scm.ImagePath);
        Assert.DoesNotContain(source, _scm.ImagePath!, StringComparison.OrdinalIgnoreCase);
        foreach (var name in SensorServiceInstallLayout.RequiredPayloadFiles)
            Assert.True(File.Exists(Path.Combine(_layout.CurrentDirectory, name)), name);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(_layout.CurrentDirectory, "en", "resources.txt")));
        Assert.Equal(["Query", "Create", "Start"], _scm.Calls);
        Assert.Equal([$"parent:{_layout.ProgramFilesDirectory}", $"root:{_layout.RootDirectory}",
            $"tree:{_layout.StagingDirectory}", $"tree:{_layout.CurrentDirectory}"], _security.Calls);
        Assert.False(Directory.Exists(_layout.StagingDirectory));
        Assert.True(File.Exists(Path.Combine(source, SensorServiceInstallLayout.ExecutableName))); // source untouched
    }

    [Fact]
    public void SingleFileReleasePayloadInstallsWithItsNativeHelpersAndNoLooseManagedFiles()
    {
        var source = Path.Combine(_sandbox, "Downloads", "DevOverlay v0.2.0", "SensorService");
        Directory.CreateDirectory(source);
        foreach (var name in new[] { SensorServiceInstallLayout.ExecutableName, "MonoPosixHelper.dll", "libMonoPosixHelper.dll" })
            File.WriteAllText(Path.Combine(source, name), "bundle");

        Installer().Install(source);

        Assert.Equal(_layout.QuotedExecutablePath, _scm.ImagePath);
        var installed = Directory.GetFiles(_layout.CurrentDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["DevOverlay.SensorService.exe", "MonoPosixHelper.dll", "libMonoPosixHelper.dll"], installed);
    }

    [Fact]
    public void PayloadWithoutTheServiceExecutableIsRejectedBeforeAnyChange()
    {
        var source = Path.Combine(_sandbox, "Downloads", "DevOverlay v0.2.0", "SensorService");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "MonoPosixHelper.dll"), "native only");

        Assert.Throws<FileNotFoundException>(() => Installer().Install(source));

        Assert.Empty(_scm.Calls);
        Assert.False(Directory.Exists(_layout.CurrentDirectory));
    }

    [Fact]
    public void RepairStopsFirstRefreshesProtectedCopyAndMigratesLegacyImagePath()
    {
        var legacy = CreatePayload("repo\\bin\\Debug\\net8.0-windows10.0.19041.0\\SensorService", "legacy");
        _scm.Registration = new ServiceRegistration($"\"{Path.Combine(legacy, SensorServiceInstallLayout.ExecutableName)}\"");
        Directory.CreateDirectory(_layout.CurrentDirectory);
        File.WriteAllText(Path.Combine(_layout.CurrentDirectory, "stale.dll"), "old");
        var source = CreatePayload("Desktop\\DevOverlay\\SensorService", "v2");

        Installer().Repair(source);

        Assert.Equal(["Query", "Stop", "Reconfigure", "Start"], _scm.Calls);
        Assert.Equal(_layout.QuotedExecutablePath, _scm.ImagePath);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(_layout.CurrentDirectory, "en", "resources.txt")));
        Assert.False(File.Exists(Path.Combine(_layout.CurrentDirectory, "stale.dll")));
        Assert.False(Directory.Exists(_layout.PreviousDirectory));
        Assert.True(Directory.Exists(legacy)); // the old location is never deleted
    }

    [Fact]
    public void MovingThePortableAppDoesNotChangeTheServiceImagePath()
    {
        Installer().Install(CreatePayload("first location\\SensorService", "a"));
        var installed = _scm.ImagePath;
        _scm.Registration = new ServiceRegistration(installed);

        Installer().Repair(CreatePayload("moved elsewhere\\SensorService", "b"));

        Assert.Equal(installed, _scm.ImagePath);
        Assert.Equal(_layout.QuotedExecutablePath, _scm.ImagePath);
    }

    [Fact]
    public void InstallFailureToSecurePayloadNeverRegistersTheService()
    {
        _security.FailOn = "tree";
        var source = CreatePayload("pkg\\SensorService", "v1");

        Assert.Throws<ServiceDirectorySecurityException>(() => Installer().Install(source));

        Assert.Equal(["Query"], _scm.Calls);
        Assert.Null(_scm.ImagePath);
        Assert.False(Directory.Exists(_layout.RootDirectory));
    }

    [Fact]
    public void RepairFailureDisablesTheStoppedServiceInsteadOfLeavingItOnAnUnsafePath()
    {
        var legacy = CreatePayload("legacy\\SensorService", "legacy");
        var legacyImage = $"\"{Path.Combine(legacy, SensorServiceInstallLayout.ExecutableName)}\"";
        _scm.Registration = new ServiceRegistration(legacyImage);
        _security.FailOn = "tree";

        var error = Assert.Throws<SensorServiceSetupException>(() => Installer().Repair(CreatePayload("pkg\\SensorService", "v2")));

        Assert.Contains("disabled", error.Message);
        Assert.Equal(["Query", "Stop", "Disable"], _scm.Calls);
        Assert.Equal(legacyImage, _scm.Registration!.ImagePath);
        Assert.True(_scm.Disabled);
        Assert.False(Directory.Exists(_layout.StagingDirectory));
    }

    [Fact]
    public void RepairFailurePreservesThePreviousProtectedPayload()
    {
        Installer().Install(CreatePayload("pkg1\\SensorService", "v1"));
        _scm.Registration = new ServiceRegistration(_scm.ImagePath);
        _scm.Calls.Clear();
        _security.FailOn = "tree";

        Assert.Throws<SensorServiceSetupException>(() => Installer().Repair(CreatePayload("pkg2\\SensorService", "v2")));

        Assert.Equal("v1", File.ReadAllText(Path.Combine(_layout.CurrentDirectory, "en", "resources.txt")));
        Assert.Equal(["Query", "Stop", "Disable"], _scm.Calls);
    }

    [Fact]
    public void InstallAndRepairRespectCurrentRegistrationState()
    {
        var source = CreatePayload("pkg\\SensorService", "v1");
        Assert.Throws<SensorServiceSetupException>(() => Installer().Repair(source));
        _scm.Registration = new ServiceRegistration("\"C:\\x\\DevOverlay.SensorService.exe\"");
        Assert.Throws<SensorServiceSetupException>(() => Installer().Install(source));
        Assert.False(Directory.Exists(_layout.RootDirectory));
    }

    [Fact]
    public void UninstallRemovesOnlyTheOwnedServiceDirectory()
    {
        Installer().Install(CreatePayload("pkg\\SensorService", "v1"));
        _scm.Registration = new ServiceRegistration(_scm.ImagePath);
        var sibling = Path.Combine(_programFiles, "DevOverlaySensorService.backup");
        var unrelated = Path.Combine(_programFiles, "Other Vendor");
        Directory.CreateDirectory(sibling);
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "keep.txt"), "keep");
        var portable = Path.Combine(_sandbox, "pkg");
        _scm.Calls.Clear();

        Installer().Uninstall();

        Assert.Equal(["Query", "Stop", "Delete"], _scm.Calls);
        Assert.False(Directory.Exists(_layout.RootDirectory));
        Assert.True(Directory.Exists(sibling));
        Assert.True(File.Exists(Path.Combine(unrelated, "keep.txt")));
        Assert.True(Directory.Exists(portable));
    }

    [Fact]
    public void UninstallWithNothingInstalledSucceeds()
    {
        Installer().Uninstall();
        Assert.Equal(["Query"], _scm.Calls);
    }

    [Fact]
    public void InvalidPayloadSourcesAreRejected()
    {
        var payload = new SensorServicePayload(_layout);
        Assert.Throws<ArgumentException>(() => payload.ValidateSource("relative\\SensorService"));
        Assert.Throws<DirectoryNotFoundException>(() => payload.ValidateSource(Path.Combine(_sandbox, "missing")));

        var incomplete = CreatePayload("incomplete", "v1");
        File.Delete(Path.Combine(incomplete, SensorServiceInstallLayout.ExecutableName));
        Assert.Throws<FileNotFoundException>(() => payload.ValidateSource(incomplete));

        Directory.CreateDirectory(_layout.CurrentDirectory);
        Assert.Throws<InvalidOperationException>(() => payload.ValidateSource(_layout.CurrentDirectory));
    }

    [Theory]
    [InlineData("..\\outside.dll")]
    [InlineData("sub\\..\\..\\outside.dll")]
    [InlineData("C:\\Windows\\System32\\evil.dll")]
    [InlineData("\\evil.dll")]
    [InlineData("")]
    public void PayloadPathsThatEscapeTheServiceDirectoryAreRejected(string relative) =>
        Assert.Throws<InvalidDataException>(() => SensorServicePayload.ResolveInside(@"C:\Program Files\DevOverlaySensorService\staging", relative));

    [Fact]
    public void PayloadPathsInsideTheServiceDirectoryResolve() =>
        Assert.Equal(@"C:\Program Files\DevOverlaySensorService\staging\en\a b.dll",
            SensorServicePayload.ResolveInside(@"C:\Program Files\DevOverlaySensorService\staging", @"en\a b.dll"));

    [Fact]
    public void ReparsePointsInThePayloadAreNotFollowed()
    {
        var source = CreatePayload("pkg\\SensorService", "v1");
        var target = Path.Combine(_sandbox, "junction target");
        Directory.CreateDirectory(target);
        var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", Path.Combine(source, "linked"), target }) info.ArgumentList.Add(argument);
        using (var process = Process.Start(info)!) process.WaitForExit();
        Assert.True(Directory.Exists(Path.Combine(source, "linked")));

        Assert.Throws<InvalidDataException>(() => Installer().Install(source));
        Assert.Null(_scm.ImagePath);
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public void DeleteRefusesDirectoriesTheInstallDoesNotOwn()
    {
        var payload = new SensorServicePayload(_layout);
        Assert.Throws<InvalidOperationException>(() => payload.DeleteOwned(_programFiles));
        Assert.Throws<InvalidOperationException>(() => payload.DeleteOwned(Path.Combine(_layout.CurrentDirectory, "..", "..")));
        Assert.True(Directory.Exists(_programFiles));
    }

    [Fact]
    public void AclPolicyAllowsOnlySystemAdministratorsAndTrustedInstallerToModify()
    {
        const int fullControl = 0x1F01FF, modify = 0x1301BF, readExecute = 0x1200A9;
        var protectedEntries = new[]
        {
            new AclEntry(ServiceDirectoryAclPolicy.LocalSystemSid, fullControl, true, false),
            new AclEntry(ServiceDirectoryAclPolicy.AdministratorsSid, fullControl, true, false),
            new AclEntry(ServiceDirectoryAclPolicy.UsersSid, readExecute, true, false),
            new AclEntry("S-1-3-0", 0x10000000, true, true), // CREATOR OWNER, inherit-only: does not apply here
            new AclEntry("S-1-1-0", fullControl, false, false) // deny entries never grant access
        };
        Assert.Empty(ServiceDirectoryAclPolicy.FindViolations(ServiceDirectoryAclPolicy.AdministratorsSid, protectedEntries));
        Assert.Empty(ServiceDirectoryAclPolicy.FindViolations(ServiceDirectoryAclPolicy.TrustedInstallerSid, protectedEntries));

        Assert.NotEmpty(ServiceDirectoryAclPolicy.FindViolations(ServiceDirectoryAclPolicy.AdministratorsSid,
            [.. protectedEntries, new AclEntry(ServiceDirectoryAclPolicy.UsersSid, modify, true, false)]));
        Assert.NotEmpty(ServiceDirectoryAclPolicy.FindViolations(ServiceDirectoryAclPolicy.AdministratorsSid,
            [new AclEntry("S-1-5-11", 0x2, true, false)])); // Authenticated Users: write data
        Assert.NotEmpty(ServiceDirectoryAclPolicy.FindViolations(ServiceDirectoryAclPolicy.AdministratorsSid,
            [new AclEntry("S-1-1-0", 0x40000, true, false)])); // Everyone: WRITE_DAC
        Assert.NotEmpty(ServiceDirectoryAclPolicy.FindViolations(ServiceDirectoryAclPolicy.AdministratorsSid,
            [new AclEntry("S-1-5-32-545", 0x40000000, true, false)])); // GENERIC_WRITE
        Assert.NotEmpty(ServiceDirectoryAclPolicy.FindViolations("S-1-5-21-1-2-3-1001", protectedEntries)); // user-owned
    }

    [Fact]
    public void ProtectedRootSecurityPassesThePolicy() =>
        Assert.Empty(WindowsServiceDirectorySecurity.Evaluate(WindowsServiceDirectorySecurity.CreateRootSecurity()));

    [Fact]
    public void UserWritableDirectoryIsDetectedFromTheRealAcl()
    {
        var directory = Path.Combine(_sandbox, "user writable");
        Directory.CreateDirectory(directory);
        Assert.NotEmpty(WindowsServiceDirectorySecurity.Inspect(directory));
    }

    [Fact]
    public void MachineProgramFilesIsAnAcceptableParentForTheServiceRoot() =>
        new WindowsServiceDirectorySecurity().VerifyParent(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));

    private SensorServiceInstaller Installer() => new(_layout, _scm, _security);

    private string CreatePayload(string relative, string marker)
    {
        var directory = Path.Combine(_sandbox, relative);
        Directory.CreateDirectory(Path.Combine(directory, "en"));
        foreach (var name in SensorServiceInstallLayout.RequiredPayloadFiles)
            File.WriteAllText(Path.Combine(directory, name), marker);
        File.WriteAllText(Path.Combine(directory, "en", "resources.txt"), marker);
        return directory;
    }

    private sealed class FakeServiceControl : IServiceControlManager
    {
        public ServiceRegistration? Registration { get; set; }
        public List<string> Calls { get; } = [];
        public string? ImagePath { get; private set; }
        public bool Disabled { get; private set; }

        public ServiceRegistration? Query() { Calls.Add("Query"); return Registration; }
        public void Stop() => Calls.Add("Stop");
        public void Create(string quotedImagePath) { Calls.Add("Create"); ImagePath = quotedImagePath; }
        public void Reconfigure(string quotedImagePath) { Calls.Add("Reconfigure"); ImagePath = quotedImagePath; }
        public void Disable() { Calls.Add("Disable"); Disabled = true; }
        public void Start() => Calls.Add("Start");
        public void Delete() => Calls.Add("Delete");
    }

    private sealed class FakeSecurity : IServiceDirectorySecurity
    {
        public List<string> Calls { get; } = [];
        public string? FailOn { get; set; }

        public void VerifyParent(string directory) => Record("parent", directory);

        public void SecureRoot(string root)
        {
            Record("root", root);
            Directory.CreateDirectory(root);
        }

        public void SecureTree(string directory) => Record("tree", directory);

        private void Record(string step, string path)
        {
            Calls.Add($"{step}:{path}");
            if (FailOn == step) throw new ServiceDirectorySecurityException($"simulated unsafe ACL at {path}");
        }
    }
}
