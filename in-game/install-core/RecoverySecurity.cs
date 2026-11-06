using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace AimMod.InGame;

static class RecoverySecurity
{
    public static bool Elevated => OperatingSystem.IsWindows() && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public static void NoLinks(string path)
    {
        for (var walk = Path.GetFullPath(path); walk is not null; walk = Path.GetDirectoryName(walk))
        {
            try
            {
                if ((File.GetAttributes(walk) & FileAttributes.ReparsePoint) != 0)
                    throw new InstallException("Recovery and installation paths must not contain links or junctions.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    public static string Root(string userRoot, string win64)
    {
        NoLinks(win64);
        if (!InstallLayout.IsWin64(win64)) throw new InstallException("Recovery requires a validated game folder.");
        if (!OperatingSystem.IsWindows() || !Elevated) { NoLinks(userRoot); return userRoot; }
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AimModRecovery");
        NoLinks(root);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(admins);
        foreach (var sid in new[] { admins, system })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var directory = new DirectoryInfo(root);
        if (!directory.Exists) directory.Create(security);
        // Never adopt a pre-created user-owned directory or silently repair its permissions.
        var actual = directory.GetAccessControl();
        if (!actual.AreAccessRulesProtected || !Equals(actual.GetOwner(typeof(SecurityIdentifier)), admins))
            throw new InstallException("Recovery storage is not administrator protected.");
        foreach (FileSystemAccessRule rule in actual.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !rule.IdentityReference.Equals(admins) && !rule.IdentityReference.Equals(system))
                throw new InstallException("Recovery storage has untrusted permissions.");
        return Path.Combine(root, Sha256Hex.Of(Encoding.UTF8.GetBytes(Path.GetFullPath(win64).ToUpperInvariant())));
    }
}
