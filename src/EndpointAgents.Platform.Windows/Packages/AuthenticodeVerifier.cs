using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace EndpointAgents.Platform.Windows.Packages;

/// <summary>Verifies an EXE's Authenticode signature with WinVerifyTrust.</summary>
[SupportedOSPlatform("windows")]
public static class AuthenticodeVerifier
{
    private static readonly Guid GenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    /// <summary>The signer subject when the signature is valid and trusted, otherwise null.</summary>
    public static string? GetVerifiedSigner(string path)
    {
        using (var data = new WinTrustData(path))
            if (WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, data) != 0)
                return null;
        try
        {
#pragma warning disable SYSLIB0057
            using var signer = new X509Certificate2(
                X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return signer.Subject;
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
