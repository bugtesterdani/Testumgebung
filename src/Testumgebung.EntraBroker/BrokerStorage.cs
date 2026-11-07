using System.Security.AccessControl;
using System.Security.Principal;

namespace Testumgebung.EntraBroker;

/// <summary>Machine-scope DPAPI relies on the file ACL to keep the encrypted value away from other accounts.</summary>
internal sealed class BrokerStorage
{
    private readonly SecurityIdentifier _administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private readonly SecurityIdentifier _system = new(WellKnownSidType.LocalSystemSid, null);
    private readonly SecurityIdentifier _reader;

    public BrokerStorage(SecurityIdentifier reader, string directory)
    {
        _reader = reader;
        Directory = new DirectoryInfo(directory);
        if (!Directory.Exists)
            throw new InvalidOperationException("Der geschützte Entra-Konfigurationsordner fehlt. Siehe docs/entra-dienst.md.");
        Check(Directory, Directory.GetAccessControl());
    }

    private DirectoryInfo Directory { get; }

    public string RequireProtectedFile(string name)
    {
        var file = new FileInfo(Path.Combine(Directory.FullName, name));
        Check(file, file.GetAccessControl());
        return file.FullName;
    }

    private void Check(FileSystemInfo entry, FileSystemSecurity acl)
    {
        if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Der Entra-Dienstspeicher darf keine Datei- oder Ordnerverknüpfungen verwenden.");

        var owner = (SecurityIdentifier?)acl.GetOwner(typeof(SecurityIdentifier));
        if (owner != _administrators && owner != _system)
            throw new InvalidOperationException("Der Entra-Dienstspeicher muss Administratoren oder SYSTEM gehören.");

        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Deny) continue;
            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (sid == _administrators || sid == _system) continue;
            if (sid != _reader || (rule.FileSystemRights & ~FileSystemRights.ReadAndExecute & ~FileSystemRights.Synchronize) != 0)
                throw new InvalidOperationException("Die Zugriffsrechte des Entra-Dienstspeichers sind zu weit gefasst.");
        }
    }
}
