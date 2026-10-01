using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DevOverlay.SensorServiceSetup;

public interface IServiceDirectorySecurity
{
    /// <summary>Throws unless the directory that will contain the service root cannot be modified by non-administrators.</summary>
    void VerifyParent(string directory);

    /// <summary>Creates the service root, or resets an existing one, with the protected DACL and owner, then verifies it.</summary>
    void SecureRoot(string root);

    /// <summary>Resets every object below the root to Administrators ownership with inherited ACEs only, then verifies all of them.</summary>
    void SecureTree(string directory);
}

public sealed class ServiceDirectorySecurityException(string message) : Exception(message);

public sealed class WindowsServiceDirectorySecurity : IServiceDirectorySecurity
{
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    /// <summary>Protected DACL: SYSTEM and Administrators full control, Users read/execute only; nothing inherited from above.</summary>
    public static DirectorySecurity CreateRootSecurity()
    {
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    public void VerifyParent(string directory)
    {
        var info = new DirectoryInfo(directory);
        if (!info.Exists) throw new DirectoryNotFoundException($"Program Files directory not found: {directory}");
        RejectReparsePoint(info);
        ThrowIfViolations(info.FullName, Evaluate(info.GetAccessControl()));
    }

    public void SecureRoot(string root)
    {
        var info = new DirectoryInfo(root);
        if (info.Exists)
        {
            RejectReparsePoint(info);
            info.SetAccessControl(CreateRootSecurity());
        }
        else
        {
            info.Create(CreateRootSecurity());
        }
        info.Refresh();
        RejectReparsePoint(info);
        ThrowIfViolations(info.FullName, Evaluate(info.GetAccessControl()));
    }

    public void SecureTree(string directory)
    {
        var root = new DirectoryInfo(directory);
        if (!root.Exists) throw new DirectoryNotFoundException(directory);
        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            RejectReparsePoint(current);
            current.SetAccessControl(CreateInheritingSecurity<DirectorySecurity>());
            ThrowIfViolations(current.FullName, Evaluate(current.GetAccessControl()));
            foreach (var entry in current.EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo child) { pending.Push(child); continue; }
                var file = (FileInfo)entry;
                RejectReparsePoint(file);
                file.SetAccessControl(CreateInheritingSecurity<FileSecurity>());
                ThrowIfViolations(file.FullName, Evaluate(file.GetAccessControl()));
            }
        }
    }

    /// <summary>Reads the effective DACL/owner of an existing path without changing it.</summary>
    public static IReadOnlyList<string> Inspect(string path) => Directory.Exists(path)
        ? Evaluate(new DirectoryInfo(path).GetAccessControl())
        : Evaluate(new FileInfo(path).GetAccessControl());

    public static IReadOnlyList<string> Evaluate(FileSystemSecurity security)
    {
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.DiscretionaryAcl is null) return ["NULL DACL grants everyone full access"];
        var owner = security.GetOwner(typeof(SecurityIdentifier))?.Value;
        var entries = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(rule => new AclEntry(rule.IdentityReference.Value, (int)rule.FileSystemRights,
                rule.AccessControlType == AccessControlType.Allow,
                rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)));
        return ServiceDirectoryAclPolicy.FindViolations(owner, entries);
    }

    private static T CreateInheritingSecurity<T>() where T : FileSystemSecurity, new()
    {
        // Empty explicit DACL, not protected: the object keeps only ACEs inherited from the protected root.
        var security = new T();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
        return security;
    }

    private static void RejectReparsePoint(FileSystemInfo info)
    {
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new ServiceDirectorySecurityException($"Refusing to use a reparse point: {info.FullName}");
    }

    private static void ThrowIfViolations(string path, IReadOnlyList<string> violations)
    {
        if (violations.Count > 0)
            throw new ServiceDirectorySecurityException($"Unsafe permissions on {path}: {string.Join("; ", violations)}");
    }
}
