using System.IO;
using DevOverlay.Configuration;
using DevOverlay.Platform.Windows;
using DevOverlay.Presentation;
using Xunit;

namespace DevOverlay.Tests;

public sealed class StartupRegistrationTests
{
    private const string Current = @"C:\Path With Spaces\DevOverlay\DevOverlay.exe";
    private const string Old = @"C:\Old\DevOverlay\DevOverlay.exe";

    // In-memory stand-in for the Run key: tests never touch the real startup entries of the machine they run on.
    private sealed class FakeRunKey : IStartupRegistry
    {
        internal Dictionary<string, object> Values { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["OneDrive"] = "\"C:\\Program Files\\OneDrive\\OneDrive.exe\" /background",
            ["Steam"] = "\"C:\\Steam\\steam.exe\" -silent",
            ["Figma Agent"] = "\"C:\\Figma\\figma_agent.exe\""
        };
        internal Exception? ReadFailure { get; set; }
        internal Exception? WriteFailure { get; set; }
        internal Exception? DeleteFailure { get; set; }
        internal List<string> Operations { get; } = [];
        public string? Read(string name)
        {
            Operations.Add("read:" + name);
            if (ReadFailure is not null) throw ReadFailure;
            if (!Values.TryGetValue(name, out var value)) return null;
            return value as string ?? throw new InvalidDataException("not a string");
        }
        public void Write(string name, string data) { Operations.Add("write:" + name); if (WriteFailure is not null) throw WriteFailure; Values[name] = data; }
        public void Delete(string name) { Operations.Add("delete:" + name); if (DeleteFailure is not null) throw DeleteFailure; Values.Remove(name); }
        internal Dictionary<string, object> Others() => Values.Where(pair => pair.Key != StartupRegistration.ValueName).ToDictionary(pair => pair.Key, pair => pair.Value);
    }
    private static StartupRegistration Create(FakeRunKey key, string? executable = Current) => new(key, () => executable, _ => { });
    private static string Quoted(string path) => "\"" + path + "\"";

    [Fact]
    public void NoEntryMeansDisabled()
    {
        var status = Create(new FakeRunKey()).Inspect();
        Assert.Equal(StartupState.Disabled, status.State); Assert.False(status.IsEnabled);
    }

    [Fact]
    public void QuotedEntryForTheRunningExecutableIsEnabledEvenWithSpacesAndDifferentCaseOrDotSegments()
    {
        var key = new FakeRunKey(); key.Values["DevOverlay"] = Quoted(Current);
        Assert.True(Create(key).Inspect().IsEnabled);
        key.Values["DevOverlay"] = Quoted(Current.ToUpperInvariant());
        Assert.True(Create(key).Inspect().IsEnabled);
        key.Values["DevOverlay"] = Quoted(@"C:\Path With Spaces\DevOverlay\..\DevOverlay\DevOverlay.exe");
        Assert.True(Create(key).Inspect().IsEnabled);
    }

    [Fact]
    public void EnableWritesTheExactQuotedExecutablePathAndTouchesNothingElse()
    {
        var key = new FakeRunKey(); var others = key.Others();
        var status = Create(key).Apply(enable: true);
        Assert.True(status.IsEnabled); Assert.Null(status.Error);
        Assert.Equal("\"C:\\Path With Spaces\\DevOverlay\\DevOverlay.exe\"", key.Values["DevOverlay"]);
        Assert.Equal(others, key.Others());
        Assert.Equal(["write:DevOverlay", "read:DevOverlay"], key.Operations);
    }

    [Fact]
    public void DisableRemovesOnlyTheDevOverlayValue()
    {
        var key = new FakeRunKey(); key.Values["DevOverlay"] = Quoted(Current); var others = key.Others();
        var status = Create(key).Apply(enable: false);
        Assert.Equal(StartupState.Disabled, status.State); Assert.False(key.Values.ContainsKey("DevOverlay"));
        Assert.Equal(others, key.Others());
        Assert.Equal(["delete:DevOverlay", "read:DevOverlay"], key.Operations);
        Assert.Equal(StartupState.Disabled, Create(key).Apply(enable: false).State); // disabling twice is harmless
    }

    [Fact]
    public void OldDevOverlayPathIsDetectedButNotReportedAsEnabledForThisExecutable()
    {
        var key = new FakeRunKey(); key.Values["DevOverlay"] = Quoted(Old);
        var status = Create(key).Inspect();
        Assert.Equal(StartupState.EnabledOtherPath, status.State); Assert.Equal(Old, status.RegisteredPath); Assert.False(status.IsEnabled);
    }

    [Fact]
    public void EnabledEntryWithAnOldPathIsRepairedToTheCurrentPathOnly()
    {
        var key = new FakeRunKey(); key.Values["DevOverlay"] = Quoted(Old); var others = key.Others();
        var registration = Create(key);
        Assert.True(registration.RepairOwnedEntry());
        Assert.Equal(Quoted(Current), key.Values["DevOverlay"]);
        Assert.Equal(others, key.Others());
        Assert.True(registration.Inspect().IsEnabled);
        Assert.False(registration.RepairOwnedEntry()); // nothing left to repair
    }

    [Fact]
    public void RepairNeverEnablesAndNeverTouchesDisabledMalformedOrForeignValues()
    {
        var key = new FakeRunKey();
        Assert.False(Create(key).RepairOwnedEntry());
        Assert.False(key.Values.ContainsKey("DevOverlay")); Assert.DoesNotContain("write:DevOverlay", key.Operations);

        foreach (var foreign in new[] { "\"C:\\Tools\\other.exe\"", "\"C:\\Old\\DevOverlay.exe\" --silent", "C:\\Old\\DevOverlay.exe", "\"C:\\Old\\DevOverlay.exe",
            "\"\"", "", "\"relative\\DevOverlay.exe\"", "\"C:\\Old\\DevOverlay.exe\"\"x\"" })
        {
            key.Values["DevOverlay"] = foreign; key.Operations.Clear();
            Assert.False(Create(key).RepairOwnedEntry());
            Assert.Equal(foreign, key.Values["DevOverlay"]);
            Assert.DoesNotContain("write:DevOverlay", key.Operations);
            Assert.Equal(StartupState.Invalid, Create(key).Inspect().State);
        }
        key.Values["DevOverlay"] = 5; // not a string at all
        Assert.Equal(StartupState.Invalid, Create(key).Inspect().State);
        Assert.False(Create(key).RepairOwnedEntry());
    }

    [Fact]
    public void ReadFailureIsReportedAsUnavailableAndNeverRepairsOrCrashes()
    {
        var key = new FakeRunKey { ReadFailure = new UnauthorizedAccessException("denied") };
        var status = Create(key).Inspect();
        Assert.Equal(StartupState.Unavailable, status.State); Assert.False(status.IsEnabled);
        Assert.Equal("Access to the Windows startup setting was denied.", status.Error);
        Assert.False(Create(key).RepairOwnedEntry());
        key.ReadFailure = new IOException("boom");
        Assert.Equal("Could not read the Windows startup setting.", Create(key).Inspect().Error);
    }

    [Fact]
    public void WriteFailureLeavesTheRealStateAndExplainsWithoutRawExceptions()
    {
        var key = new FakeRunKey { WriteFailure = new UnauthorizedAccessException("secret internals") };
        var status = Create(key).Apply(enable: true);
        Assert.False(status.IsEnabled); Assert.Equal(StartupState.Disabled, status.State);
        Assert.Equal("Access to the Windows startup setting was denied.", status.Error);
        Assert.DoesNotContain("secret", status.Error);
        key.WriteFailure = new IOException("x");
        Assert.Equal("Could not update the Windows startup setting.", Create(key).Apply(enable: true).Error);
        Assert.False(key.Values.ContainsKey("DevOverlay"));
    }

    [Fact]
    public void DeleteFailureKeepsTheEnabledStateAndReportsIt()
    {
        var key = new FakeRunKey { DeleteFailure = new IOException("locked") }; key.Values["DevOverlay"] = Quoted(Current);
        var status = Create(key).Apply(enable: false);
        Assert.True(status.IsEnabled); Assert.Equal("Could not update the Windows startup setting.", status.Error);
        Assert.True(key.Values.ContainsKey("DevOverlay"));
    }

    [Theory]
    [InlineData(null)] [InlineData(@"C:\Program Files\dotnet\dotnet.exe")] [InlineData(@"relative\DevOverlay.exe")] [InlineData("")]
    public void EnableIsRefusedUnlessTheRunningProcessIsAnAbsoluteDevOverlayExe(string? executable)
    {
        var key = new FakeRunKey(); var others = key.Others();
        var status = Create(key, executable).Apply(enable: true);
        Assert.False(status.IsEnabled); Assert.Contains("only available when DevOverlay.exe is running", status.Error);
        Assert.False(key.Values.ContainsKey("DevOverlay")); Assert.Equal(others, key.Others());
        Assert.False(Create(key, executable).RepairOwnedEntry());
    }

    [Fact]
    public void OnlyTheOwnedValueNameIsEverReadWrittenOrDeleted()
    {
        var key = new FakeRunKey(); var registration = Create(key);
        registration.Inspect(); registration.Apply(true); registration.Apply(false); key.Values["DevOverlay"] = Quoted(Old); registration.RepairOwnedEntry();
        Assert.All(key.Operations, operation => Assert.EndsWith(":DevOverlay", operation));
        Assert.Equal(["Figma Agent", "OneDrive", "Steam"], key.Others().Keys.Order().ToArray());
        Assert.Equal("\"C:\\Steam\\steam.exe\" -silent", key.Values["Steam"]);
    }

    [Fact]
    public void RealRunKeyUsesTheCurrentUserHiveAndOnlyTheDevOverlayValueName()
    {
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", CurrentUserRunKey.KeyPath);
        Assert.Equal("DevOverlay", StartupRegistration.ValueName);
        Assert.Equal("\"C:\\A B\\DevOverlay.exe\"", StartupRegistration.Quote(@"C:\A B\DevOverlay.exe"));
    }

    // ---- Settings view-model behavior
    [Fact]
    public void CheckboxFollowsTheRealStateAndTickingOnlyRequestsAChange()
    {
        var viewModel = new SettingsViewModel(OverlaySettings.CreateDefault());
        var requests = new List<bool>(); viewModel.StartWithWindowsChangeRequested += requests.Add;
        viewModel.UpdateStartupState(new StartupStatus(StartupState.Enabled, Current));
        Assert.True(viewModel.StartWithWindows); Assert.Empty(requests);              // refreshing never re-triggers a change
        viewModel.UpdateStartupState(new StartupStatus(StartupState.Disabled));
        Assert.False(viewModel.StartWithWindows); Assert.Empty(requests);
        viewModel.StartWithWindows = true; viewModel.StartWithWindows = false;
        Assert.Equal([true, false], requests);
    }

    [Fact]
    public void FailedEnableAndFailedDisableRestoreTheRealCheckboxStateWithAShortMessage()
    {
        var viewModel = new SettingsViewModel(OverlaySettings.CreateDefault());
        var key = new FakeRunKey { WriteFailure = new UnauthorizedAccessException() };
        var registration = Create(key);
        viewModel.StartWithWindowsChangeRequested += enable => viewModel.UpdateStartupState(registration.Apply(enable));

        viewModel.StartWithWindows = true;                               // enable fails -> the box is put back
        Assert.False(viewModel.StartWithWindows); Assert.Equal("Access to the Windows startup setting was denied.", viewModel.StartupStatusText);

        key.WriteFailure = null; viewModel.StartWithWindows = true;      // enable works -> message cleared
        Assert.True(viewModel.StartWithWindows); Assert.Equal(string.Empty, viewModel.StartupStatusText);

        key.DeleteFailure = new IOException(); viewModel.StartWithWindows = false;   // disable fails -> stays checked, honestly
        Assert.True(viewModel.StartWithWindows); Assert.Equal("Could not update the Windows startup setting.", viewModel.StartupStatusText);

        key.DeleteFailure = null; viewModel.StartWithWindows = false;
        Assert.False(viewModel.StartWithWindows); Assert.Equal(string.Empty, viewModel.StartupStatusText);
        Assert.Equal(3, key.Others().Count);
    }

    [Fact]
    public void RefreshReflectsExternalRegistryChangesAndMissingEntriesShowUnchecked()
    {
        var viewModel = new SettingsViewModel(OverlaySettings.CreateDefault());
        var key = new FakeRunKey(); var registration = Create(key);
        viewModel.UpdateStartupState(registration.Inspect()); Assert.False(viewModel.StartWithWindows);
        key.Values["DevOverlay"] = Quoted(Current);                        // enabled by someone else
        viewModel.UpdateStartupState(registration.Inspect()); Assert.True(viewModel.StartWithWindows);
        key.Values.Remove("DevOverlay");                                    // entry removed manually
        viewModel.UpdateStartupState(registration.Inspect()); Assert.False(viewModel.StartWithWindows);
        key.Values["DevOverlay"] = Quoted(Old);                             // stale path is not "on" for this executable
        viewModel.UpdateStartupState(registration.Inspect()); Assert.False(viewModel.StartWithWindows);
        key.ReadFailure = new UnauthorizedAccessException();
        viewModel.UpdateStartupState(registration.Inspect());
        Assert.False(viewModel.StartWithWindows); Assert.NotEqual(string.Empty, viewModel.StartupStatusText);
    }
}
