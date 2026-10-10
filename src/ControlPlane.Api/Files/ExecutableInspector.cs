using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ControlPlane.Api.Files;

/// <summary>Publish-time facts about an EXE package file, shown in the Packages tab.</summary>
public static class ExecutableInspector
{
    public const string Console = "Console";
    public const string Gui = "GUI";

    /// <summary>
    /// Reads the Windows subsystem from the PE header: <see cref="Console"/>,
    /// <see cref="Gui"/>, or null when the file is not a Windows executable.
    /// </summary>
    public static string? ReadSubsystem(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D) return null; // MZ
            stream.Position = 0x3C;
            var peOffset = reader.ReadInt32();
            // Signature (4) + COFF header (20) + optional header up to Subsystem (68) + 2.
            if (peOffset <= 0 || peOffset > stream.Length - 94) return null;
            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550) return null; // PE\0\0
            stream.Position = peOffset + 24;
            var magic = reader.ReadUInt16();
            if (magic is not 0x10B and not 0x20B) return null; // PE32 or PE32+
            stream.Position = peOffset + 24 + 68;
            return reader.ReadUInt16() switch
            {
                2 => Gui,
                3 => Console,
                _ => null
            };
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// The subject of the certificate embedded in the file's Authenticode signature,
    /// or null when unsigned. This is not a trust decision: the endpoint verifies the
    /// signature with WinVerifyTrust before it runs an EXE whose publisher is pinned.
    /// </summary>
    public static string? ReadSignerSubject(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(
                X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return certificate.Subject;
        }
        catch (CryptographicException) { return null; }
    }
}
