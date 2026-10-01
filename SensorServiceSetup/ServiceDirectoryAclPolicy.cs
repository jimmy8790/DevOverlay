namespace DevOverlay.SensorServiceSetup;

public readonly record struct AclEntry(string Sid, int Rights, bool IsAllow, bool IsInheritOnly);

/// <summary>
/// Decides whether a file-system object that a LocalSystem service loads code from is protected:
/// only SYSTEM, Administrators and TrustedInstaller may own it or hold any right that can replace, rename,
/// delete, or re-permission it.
/// </summary>
public static class ServiceDirectoryAclPolicy
{
    public const string LocalSystemSid = "S-1-5-18";
    public const string AdministratorsSid = "S-1-5-32-544";
    public const string UsersSid = "S-1-5-32-545";
    public const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    // FILE_WRITE_DATA/ADD_FILE, FILE_APPEND_DATA/ADD_SUBDIRECTORY, FILE_WRITE_EA, FILE_DELETE_CHILD,
    // FILE_WRITE_ATTRIBUTES, DELETE, WRITE_DAC, WRITE_OWNER, GENERIC_ALL, GENERIC_WRITE.
    public const int ModifyingRights = 0x2 | 0x4 | 0x10 | 0x40 | 0x100 | 0x10000 | 0x40000 | 0x80000 |
                                       0x10000000 | 0x40000000;

    private static readonly HashSet<string> Trusted = new(StringComparer.OrdinalIgnoreCase)
    {
        LocalSystemSid, AdministratorsSid, TrustedInstallerSid
    };

    public static bool IsTrusted(string? sid) => sid is not null && Trusted.Contains(sid);

    public static IReadOnlyList<string> FindViolations(string? ownerSid, IEnumerable<AclEntry> entries)
    {
        var violations = new List<string>();
        if (!IsTrusted(ownerSid)) violations.Add($"owner {ownerSid ?? "<none>"} is not SYSTEM/Administrators/TrustedInstaller");
        foreach (var entry in entries)
        {
            // Deny entries only remove access; inherit-only entries do not apply to this object.
            if (!entry.IsAllow || entry.IsInheritOnly) continue;
            if ((entry.Rights & ModifyingRights) != 0 && !IsTrusted(entry.Sid))
                violations.Add($"{entry.Sid} has modifying rights 0x{entry.Rights:X8}");
        }
        return violations;
    }
}
