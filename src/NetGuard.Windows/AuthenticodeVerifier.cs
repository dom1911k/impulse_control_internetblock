using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using NetGuard.Core.Paths;

namespace NetGuard.Windows;

/// <summary>
/// Checks that a file has a valid Authenticode signature (WinVerifyTrust) and returns the signer's
/// certificate subject. Works offline: no revocation checks, so it still works while internet is blocked.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AuthenticodeVerifier : ISignatureVerifier
{
    // The service re-checks every 30 s; cache by path + size + modification time so unchanged files aren't re-verified.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SignatureInfo> _cache = new(StringComparer.OrdinalIgnoreCase);

    public SignatureInfo Check(string path)
    {
        string key;
        try
        {
            var fi = new FileInfo(path);
            key = $"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception e) { return new SignatureInfo(false, null, e.Message); }
        if (_cache.TryGetValue(key, out var hit)) return hit;
        var result = CheckUncached(path);
        if (_cache.Count > 2000) _cache.Clear();
        _cache[key] = result;
        return result;
    }

    private static SignatureInfo CheckUncached(string path)
    {
        try
        {
            var rc = Verify(path);
            if (rc != 0) return new SignatureInfo(false, null, $"WinVerifyTrust 0x{rc:X8}");
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return new SignatureInfo(true, cert.Subject, null);
        }
        catch (Exception e)
        {
            return new SignatureInfo(false, null, e.Message);
        }
    }

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private static uint Verify(string path)
    {
        var pathPtr = Marshal.StringToCoTaskMemUni(path);
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = pathPtr,
        };
        var fileInfoPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
        var data = new WINTRUST_DATA
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
            dwUIChoice = 2,              // WTD_UI_NONE
            fdwRevocationChecks = 0,     // WTD_REVOKE_NONE
            dwUnionChoice = 1,           // WTD_CHOICE_FILE
            pFile = fileInfoPtr,
            dwStateAction = 1,           // WTD_STATEACTION_VERIFY
            dwProvFlags = 0x10 | 0x1000, // WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL
        };
        try
        {
            var action = GenericVerifyV2;
            var rc = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            data.dwStateAction = 2; // WTD_STATEACTION_CLOSE
            WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            return rc;
        }
        finally
        {
            Marshal.FreeCoTaskMem(fileInfoPtr);
            Marshal.FreeCoTaskMem(pathPtr);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern uint WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
