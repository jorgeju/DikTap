using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using DikTap.Localization;

namespace DikTap.Services.Credentials;

internal interface ICredentialStore
{
    bool HasKey();
    string? Read();
    void Save(string apiKey);
    void Delete();
}

// Generic per-user credential in the Windows Credential Manager.
internal sealed class WindowsCredentialStore : ICredentialStore
{
    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;
    // CRED_MAX_CREDENTIAL_BLOB_SIZE
    private const int MaxBlobBytes = 5 * 512;

    [StructLayout(LayoutKind.Sequential)]
    private struct CREDENTIALW
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWriteW(ref CREDENTIALW credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    private readonly string _target;

    public WindowsCredentialStore(string target) => _target = target;

    public bool HasKey()
    {
        if (!CredReadW(_target, CRED_TYPE_GENERIC, 0, out IntPtr p))
        {
            int err = Marshal.GetLastWin32Error();
            if (err == ERROR_NOT_FOUND) return false;
            throw new Win32Exception(err);
        }
        CredFree(p);
        return true;
    }

    public string? Read()
    {
        if (!CredReadW(_target, CRED_TYPE_GENERIC, 0, out IntPtr p))
        {
            int err = Marshal.GetLastWin32Error();
            if (err == ERROR_NOT_FOUND) return null;
            throw new Win32Exception(err);
        }
        byte[]? bytes = null;
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIALW>(p);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0) return null;
            bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            // Wipes the native copy before returning the block to Windows.
            Marshal.Copy(new byte[bytes.Length], 0, cred.CredentialBlob, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            if (bytes is not null) Array.Clear(bytes);
            CredFree(p);
        }
    }

    public void Save(string apiKey)
    {
        apiKey = ApiKeyValidator.Normalize(apiKey);
        byte[] blob = Encoding.UTF8.GetBytes(apiKey);
        if (blob.Length > MaxBlobBytes) throw new DictationException(Str.KeyTooLong, ErrorKind.Configuration);

        var handle = GCHandle.Alloc(blob, GCHandleType.Pinned);
        IntPtr target = Marshal.StringToHGlobalUni(_target);
        try
        {
            var cred = new CREDENTIALW
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = target,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = handle.AddrOfPinnedObject(),
                Persist = CRED_PERSIST_LOCAL_MACHINE,
            };
            if (!CredWriteW(ref cred, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Array.Clear(blob);
            handle.Free();
            Marshal.FreeHGlobal(target);
        }
    }

    public void Delete()
    {
        if (!CredDeleteW(_target, CRED_TYPE_GENERIC, 0))
        {
            int err = Marshal.GetLastWin32Error();
            if (err != ERROR_NOT_FOUND) throw new Win32Exception(err);
        }
    }
}

internal static class ApiKeyValidator
{
    // Returns the key without spaces, or throws if the format is clearly invalid.
    public static string Normalize(string? apiKey)
    {
        string key = (apiKey ?? string.Empty).Trim();
        if (key.Length < 8 || key.Length > 400)
            throw new DictationException(Str.KeyBadLength, ErrorKind.Configuration);
        foreach (char c in key)
        {
            if (c < 0x21 || c > 0x7E)
                throw new DictationException(Str.KeyBadChars, ErrorKind.Configuration);
        }
        return key;
    }
}
