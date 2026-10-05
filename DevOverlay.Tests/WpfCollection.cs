using Xunit;

namespace DevOverlay.Tests;

// WPF loads BAML from the assembly's shared resource package, which is not safe to read from several STA threads at once
// (seen as a NullReferenceException in PackagePart.IsStreamClosed). Tests that create WPF UI share this collection so they run one at a time.
[CollectionDefinition(Name)]
public sealed class WpfCollection
{
    public const string Name = "WPF UI";
}
