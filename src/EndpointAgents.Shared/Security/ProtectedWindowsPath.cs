using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace EndpointAgents.Shared.Security;

/// <summary>Validates a SYSTEM/Administrators-owned Windows file trust boundary.</summary>
[SupportedOSPlatform("windows")]
public static class ProtectedWindowsPath
{
    private static readonly SecurityIdentifier LocalSystem =
        new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators =
        new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier TrustedInstaller =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
    private const FileSystemRights DangerousRights = FileSystemRights.Write
        | FileSystemRights.Delete
        | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions
        | FileSystemRights.TakeOwnership;

    public static void EnsureDirectory(string path, bool requireProtectedAcl)
    {
        if (!IsDirectorySecure(path, requireProtectedAcl))
            throw new InvalidDataException(
                "The protected directory is missing, redirected, or writable by an untrusted account.");
    }

    public static void EnsureFile(string path)
    {
        if (!IsFileSecure(path))
            throw new InvalidDataException(
                "The protected file is missing, redirected, or writable by an untrusted account.");
    }

    public static bool IsDirectorySecure(string path, bool requireProtectedAcl)
    {
        try
        {
            var directory = new DirectoryInfo(Path.GetFullPath(path));
            return directory.Exists
                && (directory.Attributes & FileAttributes.ReparsePoint) == 0
                && AreAncestorsSecure(directory.Parent)
                && IsSecure(directory.GetAccessControl(
                    AccessControlSections.Owner | AccessControlSections.Access),
                    requireProtectedAcl);
        }
        catch (SystemException)
        {
            return false;
        }
    }

    public static bool IsFileSecure(string path)
    {
        try
        {
            var file = new FileInfo(Path.GetFullPath(path));
            return file.Exists
                && (file.Attributes & FileAttributes.ReparsePoint) == 0
                && AreAncestorsSecure(file.Directory)
                && IsSecure(file.GetAccessControl(
                    AccessControlSections.Owner | AccessControlSections.Access),
                    requireProtectedAcl: false);
        }
        catch (SystemException)
        {
            return false;
        }
    }

    public static bool IsSecure(FileSystemSecurity security,
        bool requireProtectedAcl)
    {
        if (requireProtectedAcl && !security.AreAccessRulesProtected)
            return false;
        var descriptor = new RawSecurityDescriptor(
            security.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.DiscretionaryAcl is null)
            return false;
        if (security.GetOwner(typeof(SecurityIdentifier))
                is not SecurityIdentifier owner
            || (!owner.Equals(LocalSystem) && !owner.Equals(Administrators)))
            return false;

        var rules = security.GetAccessRules(includeExplicit: true,
            includeInherited: true, typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow
                || rule.IdentityReference is not SecurityIdentifier identity
                || identity.Equals(LocalSystem) || identity.Equals(Administrators))
                continue;
            if ((rule.FileSystemRights & DangerousRights) != 0)
                return false;
        }
        return true;
    }

    private static bool AreAncestorsSecure(DirectoryInfo? directory)
    {
        while (directory is not null)
        {
            if (!directory.Exists
                || (directory.Attributes & FileAttributes.ReparsePoint) != 0
                || !IsSafeAncestor(directory.GetAccessControl(
                    AccessControlSections.Owner | AccessControlSections.Access)))
                return false;
            directory = directory.Parent;
        }
        return true;
    }

    internal static bool IsSafeAncestor(FileSystemSecurity security)
    {
        var descriptor = new RawSecurityDescriptor(
            security.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.DiscretionaryAcl is null)
            return false;
        if (security.GetOwner(typeof(SecurityIdentifier))
                is not SecurityIdentifier owner
            || (!owner.Equals(LocalSystem) && !owner.Equals(Administrators)
                && !owner.Equals(TrustedInstaller)))
            return false;

        const FileSystemRights pathReplacementRights = FileSystemRights.Delete
            | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions
            | FileSystemRights.TakeOwnership;
        var rules = security.GetAccessRules(includeExplicit: true,
            includeInherited: true, typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow
                || rule.IdentityReference is not SecurityIdentifier identity
                || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0
                || identity.Equals(LocalSystem) || identity.Equals(Administrators)
                || identity.Equals(TrustedInstaller))
                continue;
            if ((rule.FileSystemRights & pathReplacementRights) != 0)
                return false;
        }
        return true;
    }
}
