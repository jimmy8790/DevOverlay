using System.IO;
using System.Runtime.CompilerServices;
using DevOverlay.Platform.Windows;

namespace DevOverlay.Tests;

// Production code logs lifecycle events (overlay close, provider connect, NVML init). Tests exercise those paths, so the
// shared log is redirected to a temporary file for the whole test run instead of the user's real diagnostics folder.
internal static class TestLifecycleLogRedirect
{
    [ModuleInitializer]
    internal static void Redirect() => LifecycleLog.Current = new LifecycleLog(
        Path.Combine(Path.GetTempPath(), "DevOverlay-tests", "lifecycle-tests.log"));
}
