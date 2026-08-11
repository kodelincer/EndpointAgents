using EndpointAgents.Platform.Windows.UserSessions;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;

namespace EndpointAgents.Platform.Windows.Office;

[SupportedOSPlatform("windows")]
internal static class DesktopArtifactFile
{
    public static async Task<string> CopyForSessionAsync(string source,
        InteractiveUserSession session, string fileName, string sha256,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sha256) || sha256.Length != 64
            || !sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("The desktop artifact hash is invalid.");
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName != Path.GetFileName(fileName))
            throw new InvalidDataException("The desktop artifact file name is invalid.");

        var sourcePath = Path.GetFullPath(source);
        if (!File.Exists(sourcePath)
            || (File.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The staged artifact is not a regular file.");

        await using var sourceStream = new FileStream(sourcePath, FileMode.Open,
            FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(sourceStream,
            cancellationToken));
        if (!actualHash.Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The staged artifact hash does not match.");
        sourceStream.Position = 0;

        using var currentIdentity = WindowsIdentity.GetCurrent();
        if (!currentIdentity.IsSystem)
        {
            if (!string.Equals(currentIdentity.User?.Value, session.UserSid,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The current process does not represent the requested desktop user.");
            return CopyOpenedSource(sourceStream, session.DesktopPath, fileName,
                sha256, cancellationToken);
        }

        if (!NativeMethods.WTSQueryUserToken(session.SessionId, out var userToken)
            || userToken.IsInvalid)
            throw new Win32Exception(Marshal.GetLastPInvokeError(),
                "The active Windows user token could not be obtained.");
        using (userToken)
        {
            return WindowsIdentity.RunImpersonated(userToken, () =>
            {
                using var userIdentity = WindowsIdentity.GetCurrent();
                if (!string.Equals(userIdentity.User?.Value, session.UserSid,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "The active Windows user token no longer matches the requested session.");
                return CopyOpenedSource(sourceStream, session.DesktopPath, fileName,
                    sha256, cancellationToken);
            });
        }
    }

    private static string CopyOpenedSource(FileStream sourceStream,
        string desktopPath, string fileName, string sha256,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var desktop = Path.GetFullPath(desktopPath);
        if (!Directory.Exists(desktop)
            || (File.GetAttributes(desktop) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException(
                "The active user's desktop is not a regular directory.");

        var destination = Path.GetFullPath(Path.Combine(desktop, fileName));
        if (!string.Equals(Path.GetDirectoryName(destination), desktop,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The desktop artifact path is invalid.");
        if (File.Exists(destination)
            && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException(
                "The desktop artifact destination is a reparse point.");

        var partial = destination + $".{Guid.NewGuid():N}.copy";
        try
        {
            using (var output = new FileStream(partial, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 64 * 1024,
                FileOptions.WriteThrough | FileOptions.SequentialScan))
            {
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = sourceStream.Read(buffer, 0, buffer.Length);
                    if (read == 0) break;
                    output.Write(buffer, 0, read);
                }
                output.Flush(flushToDisk: true);
            }
            sourceStream.Position = 0;

            if (!MatchesHash(partial, sha256, cancellationToken))
                throw new InvalidDataException(
                    "The copied desktop artifact hash does not match.");
            File.Move(partial, destination, overwrite: true);
            if ((File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0
                || !MatchesHash(destination, sha256, cancellationToken))
                throw new InvalidDataException(
                    "The desktop artifact did not verify.");
            return destination;
        }
        finally
        {
            try { File.Delete(partial); } catch { }
        }
    }

    private static bool MatchesHash(string path, string expectedSha256,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        return actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static class NativeMethods
    {
        [DllImport("Wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSQueryUserToken(int sessionId,
            out SafeAccessTokenHandle token);
    }
}
