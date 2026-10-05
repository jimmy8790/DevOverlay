using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace DevOverlay.Updates;

// Numeric major.minor.patch with an optional leading "v" and optional pre-release suffix; never compared as text.
internal readonly partial record struct ReleaseVersion(int Major, int Minor, int Patch, string? PreRelease = null)
    : IComparable<ReleaseVersion>
{
    [GeneratedRegex(@"^[vV]?(\d{1,6})\.(\d{1,6})\.(\d{1,6})(?:-([0-9A-Za-z][0-9A-Za-z.-]*))?(?:\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex Pattern();

    internal bool IsPreRelease => PreRelease is not null;

    internal static ReleaseVersion? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = Pattern().Match(text.Trim());
        if (!match.Success) return null;
        return new ReleaseVersion(
            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
            match.Groups[4].Success ? match.Groups[4].Value : null);
    }

    // The running product version comes from the build's informational version (VersionPrefix in Directory.Build.props).
    internal static ReleaseVersion? Running(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return TryParse(informational) ?? (assembly.GetName().Version is { } version
            ? new ReleaseVersion(version.Major, version.Minor, Math.Max(version.Build, 0)) : null);
    }

    public int CompareTo(ReleaseVersion other)
    {
        var numeric = Major != other.Major ? Major.CompareTo(other.Major)
            : Minor != other.Minor ? Minor.CompareTo(other.Minor)
            : Patch.CompareTo(other.Patch);
        if (numeric != 0) return numeric;
        if (PreRelease is null) return other.PreRelease is null ? 0 : 1;
        if (other.PreRelease is null) return -1;
        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    private static int ComparePreRelease(string left, string right)
    {
        var a = left.Split('.'); var b = right.Split('.');
        for (var index = 0; index < Math.Min(a.Length, b.Length); index++)
        {
            var leftNumeric = int.TryParse(a[index], NumberStyles.None, CultureInfo.InvariantCulture, out var x);
            var rightNumeric = int.TryParse(b[index], NumberStyles.None, CultureInfo.InvariantCulture, out var y);
            var result = leftNumeric && rightNumeric ? x.CompareTo(y)
                : leftNumeric ? -1 : rightNumeric ? 1 : string.CompareOrdinal(a[index], b[index]);
            if (result != 0) return result;
        }
        return a.Length.CompareTo(b.Length);
    }

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (PreRelease is null ? "" : "-" + PreRelease);
}
