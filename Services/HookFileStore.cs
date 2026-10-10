using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace ThermalScope.Services;

/// <summary>
/// Extracts the embedded FPS helper DLL and makes sure the file that gets injected is exactly the
/// one that shipped inside the executable: the file name carries the hash (so upgrades never reuse a
/// stale copy), elevated runs use an admin-only-writable folder, and the file is re-hashed while a
/// write-denying handle is held for the duration of the injection.
/// </summary>
internal static class HookFileStore
{
    private const string ResourceName = "ThermalScope.ThermalScopeHook.dll";
    private const string FilePrefix = "ThermalScopeHook-";

    public sealed record HookFile(string Path, string Sha256);

    public static HookFile Prepare()
    {
        byte[] bytes;
        using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
        {
            if (resource is null)
            {
                throw new FileNotFoundException("The FPS helper is not embedded in this build.");
            }

            using var buffer = new MemoryStream();
            resource.CopyTo(buffer);
            bytes = buffer.ToArray();
        }

        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var directory = EnsureDirectory();
        var path = System.IO.Path.Combine(directory, $"{FilePrefix}{hash[..12]}.dll");
        if (!File.Exists(path) || !HashMatches(path, hash))
        {
            File.WriteAllBytes(path, bytes);
        }

        DeleteOldVersions(directory, path);
        return new HookFile(path, hash);
    }

    /// <summary>Opens the file read-only, denies writers, and verifies its hash. Dispose after the injection.</summary>
    public static FileStream OpenVerified(HookFile hook)
    {
        var stream = new FileStream(hook.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!actual.Equals(hook.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The FPS helper on disk was modified. Restart ThermalScope to restore it.");
            }

            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static bool HashMatches(string path, string expected)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string EnsureDirectory()
    {
        var elevated = AppRelauncher.IsElevated;
        var root = Environment.GetFolderPath(
            elevated ? Environment.SpecialFolder.CommonApplicationData : Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = AppContext.BaseDirectory;
        }

        var directory = System.IO.Path.Combine(root, "ThermalScope", "native");
        var info = new DirectoryInfo(directory);
        info.Create();

        if (elevated)
        {
            // A process running as the user must not be able to swap a DLL that an elevated process injects.
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            Allow(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl);
            Allow(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl);
            Allow(security, new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute);
            Allow(security, new SecurityIdentifier("S-1-15-2-1"), FileSystemRights.ReadAndExecute); // ALL APPLICATION PACKAGES
            info.SetAccessControl(security);
        }

        return directory;
    }

    private static void Allow(DirectorySecurity security, SecurityIdentifier sid, FileSystemRights rights) =>
        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

    private static void DeleteOldVersions(string directory, string keep)
    {
        foreach (var file in Directory.EnumerateFiles(directory, FilePrefix + "*.dll"))
        {
            if (string.Equals(file, keep, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                File.Delete(file);
            }
            catch
            {
                // Still loaded in a game; it is removed on a later run.
            }
        }
    }
}
