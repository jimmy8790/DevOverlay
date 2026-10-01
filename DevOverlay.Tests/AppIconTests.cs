using System.Drawing;
using System.Reflection;
using DevOverlay.UI;
using Xunit;

namespace DevOverlay.Tests;

public sealed class AppIconTests
{
    private static readonly int[] RequiredSizes = [16, 24, 32, 48, 64, 128, 256];

    private static string RepositoryFile(params string[] parts) => Path.GetFullPath(Path.Combine(
        [AppContext.BaseDirectory, "..", "..", "..", "..", .. parts]));

    [Fact]
    public void IconContainsEveryRequiredSizeWithTransparentCorners()
    {
        var bytes = File.ReadAllBytes(RepositoryFile("assets", "DevOverlay.ico"));
        Assert.Equal(1, BitConverter.ToUInt16(bytes, 2)); // ICO, not CUR
        var count = BitConverter.ToUInt16(bytes, 4);
        var sizes = new List<int>();
        for (var index = 0; index < count; index++)
        {
            var entry = 6 + 16 * index;
            var size = bytes[entry] == 0 ? 256 : bytes[entry];
            sizes.Add(size);
            var length = (int)BitConverter.ToUInt32(bytes, entry + 8);
            var offset = (int)BitConverter.ToUInt32(bytes, entry + 12);
            using var stream = new MemoryStream(bytes, offset, length);
            using var image = new Bitmap(stream);
            Assert.Equal(size, image.Width);
            Assert.Equal(size, image.Height);
            Assert.Equal(0, image.GetPixel(0, 0).A);
        }
        Assert.Equal(RequiredSizes, sizes.OrderBy(size => size));
    }

    [Fact]
    public void OriginalLogoIsKeptNextToTheGeneratedIcon() =>
        Assert.True(File.Exists(RepositoryFile("assets", "DevOverlay-logo.png")));

    [Fact]
    public void IconIsEmbeddedAsAWpfResourceForWindowsAndTray()
    {
        var assembly = typeof(TrayIconController).Assembly;
        using var resources = assembly.GetManifestResourceStream($"{assembly.GetName().Name}.g.resources");
        Assert.NotNull(resources);
        using var reader = new System.Resources.ResourceReader(resources);
        var names = reader.Cast<System.Collections.DictionaryEntry>().Select(entry => (string)entry.Key).ToList();
        Assert.Contains("assets/devoverlay.ico", names, StringComparer.OrdinalIgnoreCase);
    }
}
