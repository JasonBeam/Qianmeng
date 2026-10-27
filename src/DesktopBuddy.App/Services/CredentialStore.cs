using System.Runtime.InteropServices;
using System.Text;

namespace DesktopBuddy.Services;

/// <summary>Stores the optional LLM token in the current Windows user's Credential Manager.</summary>
public static class CredentialStore
{
    private const string Target = "DesktopBuddy:LLM:APIKey";
    private const uint Generic = 1;
    private const uint PersistLocalMachine = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public nint TargetName;
        public nint Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public nint TargetAlias;
        public nint UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out nint credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(nint buffer);

    public static void Save(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            Delete();
            return;
        }

        var bytes = Encoding.Unicode.GetBytes(secret);
        var targetPtr = Marshal.StringToHGlobalUni(Target);
        var userPtr = Marshal.StringToHGlobalUni(Environment.UserName);
        var blobPtr = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blobPtr, bytes.Length);
            var credential = new CREDENTIAL
            {
                Type = Generic,
                TargetName = targetPtr,
                UserName = userPtr,
                CredentialBlob = blobPtr,
                CredentialBlobSize = (uint)bytes.Length,
                Persist = PersistLocalMachine
            };
            if (!CredWrite(ref credential, 0))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法保存 API Key 到 Windows 凭据管理器。");
        }
        finally
        {
            Array.Clear(bytes, 0, bytes.Length);
            Marshal.ZeroFreeGlobalAllocUnicode(targetPtr);
            Marshal.ZeroFreeGlobalAllocUnicode(userPtr);
            try { Marshal.Copy(new byte[bytes.Length], 0, blobPtr, bytes.Length); }
            finally { Marshal.FreeHGlobal(blobPtr); }
        }
    }

    public static string? Read()
    {
        if (!CredRead(Target, Generic, 0, out var credentialPtr)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(credentialPtr);
            if (credential.CredentialBlob == 0 || credential.CredentialBlobSize == 0) return null;
            var bytes = new byte[checked((int)credential.CredentialBlobSize)];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            try { return Encoding.Unicode.GetString(bytes).TrimEnd('\0'); }
            finally { Array.Clear(bytes, 0, bytes.Length); }
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    public static void Delete()
    {
        if (!CredDelete(Target, Generic, 0))
        {
            var error = Marshal.GetLastWin32Error();
            const int ErrorNotFound = 1168;
            if (error != ErrorNotFound)
                throw new System.ComponentModel.Win32Exception(error, "无法从 Windows 凭据管理器删除 API Key。");
        }
    }
}
