using EndpointAgents.Shared.LocalIpc;
using EndpointAgents.Shared.Office;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace EndpointAgents.Tray;

/// <summary>
/// Performs the final user-session trust check, obtains local consent, and launches
/// an approved Office executable without caller-controlled arguments.
/// </summary>
public sealed class OfficeRemediationLauncher
{
    public async Task<TrayIpcResponse> LaunchAsync(TrayIpcRequest request,
        CancellationToken cancellationToken)
    {
        var remediation = request.OfficeRemediation;
        if (request.Method != TrayIpcProtocol.OfficeRemediationLaunchMethod
            || remediation is null)
            return new TrayIpcResponse(request.Id, false, "InvalidOfficeRemediationRequest");
        if (!TryResolveTrustPolicy(remediation.TrustPolicy,
                out var requireMicrosoftSignature, out var trustMessage))
            return new TrayIpcResponse(request.Id, false, "UnsupportedTrustPolicy");

        string path;
        try
        {
            path = Path.GetFullPath(remediation.FilePath);
        }
        catch
        {
            return new TrayIpcResponse(request.Id, false, "InvalidRemediationPath");
        }

        var approvedRoot = Path.GetFullPath(OfficeRemediationPaths.StagingRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!path.StartsWith(approvedRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(path))
            return new TrayIpcResponse(request.Id, false, "RemediationFileUnavailable");

        if (!await MatchesExecutableHashAsync(path, remediation.ExpectedSha256,
                cancellationToken))
            return new TrayIpcResponse(request.Id, false, "RemediationHashMismatch");

        string? signer = null;
        if (requireMicrosoftSignature)
        {
            signer = AuthenticodeTrust.GetMicrosoftSigner(path);
            if (signer is null)
                return new TrayIpcResponse(request.Id, false, "MicrosoftSignatureRequired",
                    Message: "The remediation is not signed by a trusted Microsoft certificate.");
        }

        var consent = MessageBox.Show(
            $"The helpdesk recommends this Office tool:\n\n" +
            $"{remediation.DisplayName}\n\n{remediation.Description}\n\n" +
            $"{trustMessage}\n\nRun it now? No command-line arguments or credentials will be passed.",
            "EndpointAgents Office tool",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information,
            MessageBoxDefaultButton.Button2);
        if (consent != DialogResult.Yes)
            return new TrayIpcResponse(request.Id, false, "UserDeclined",
                OfficeRemediationArtifactId: remediation.ArtifactId,
                SignerSubject: signer);

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = path,
            WorkingDirectory = Path.GetDirectoryName(path)!,
            UseShellExecute = true
        });
        return process is null
            ? new TrayIpcResponse(request.Id, false, "LaunchFailed",
                OfficeRemediationArtifactId: remediation.ArtifactId,
                SignerSubject: signer)
            : new TrayIpcResponse(request.Id, true, "Launched", process.Id,
                OfficeRemediationArtifactId: remediation.ArtifactId,
                SignerSubject: signer);
    }

    internal static bool TryResolveTrustPolicy(string trustPolicy,
        out bool requireMicrosoftSignature, out string message)
    {
        requireMicrosoftSignature = false;
        message = string.Empty;
        if (trustPolicy == OfficeArtifactTrustPolicies.MicrosoftSigned)
        {
            requireMicrosoftSignature = true;
            message = "This executable must have a trusted Microsoft signature.";
            return true;
        }
        if (trustPolicy == OfficeArtifactTrustPolicies.AdministratorApprovedSha256)
        {
            message = "This retailer executable is approved by its exact administrator-managed SHA-256 hash; its publisher signature is not verified.";
            return true;
        }
        return false;
    }

    internal static async Task<bool> MatchesExecutableHashAsync(string path,
        string expectedSha256, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256) || expectedSha256.Length != 64
            || !expectedSha256.All(Uri.IsHexDigit))
            return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var header = new byte[2];
        if (await stream.ReadAsync(header, cancellationToken) != 2
            || header[0] != (byte)'M' || header[1] != (byte)'Z')
            return false;
        stream.Position = 0;
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        return actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static class AuthenticodeTrust
    {
        private static readonly Guid GenericVerifyV2 =
            new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        public static string? GetMicrosoftSigner(string path)
        {
            using var data = new WinTrustData(path);
            if (WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, data) != 0)
                return null;

            try
            {
#pragma warning disable SYSLIB0057
                using var signer = new X509Certificate2(
                    X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
                return signer.Subject.Contains("O=Microsoft Corporation",
                    StringComparison.OrdinalIgnoreCase)
                    ? signer.Subject : null;
            }
            catch (CryptographicException)
            {
                return null;
            }
        }

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true,
            CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr window,
            [MarshalAs(UnmanagedType.LPStruct)] Guid actionId, WinTrustData trustData);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private sealed class WinTrustData : IDisposable
        {
            private readonly IntPtr _fileInfo;

            public WinTrustData(string path)
            {
                StructSize = (uint)Marshal.SizeOf(this);
                UIChoice = 2; // WTD_UI_NONE
                RevocationChecks = 0;
                UnionChoice = 1; // WTD_CHOICE_FILE
                StateAction = 0;
                ProviderFlags = 0x00001000; // WTD_CACHE_ONLY_URL_RETRIEVAL
                var file = new WinTrustFileInfo(path);
                _fileInfo = Marshal.AllocCoTaskMem(Marshal.SizeOf(file));
                Marshal.StructureToPtr(file, _fileInfo, false);
                File = _fileInfo;
            }

            public uint StructSize;
            public IntPtr PolicyCallbackData;
            public IntPtr SIPClientData;
            public uint UIChoice;
            public uint RevocationChecks;
            public uint UnionChoice;
            public IntPtr File;
            public uint StateAction;
            public IntPtr StateData;
            public string? URLReference;
            public uint ProviderFlags;
            public uint UIContext;

            public void Dispose()
            {
                if (_fileInfo != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(_fileInfo);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private sealed class WinTrustFileInfo(string path)
        {
            public uint StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>();
            [MarshalAs(UnmanagedType.LPWStr)] public string FilePath = path;
            public IntPtr FileHandle;
            public IntPtr KnownSubject;
        }
    }
}
