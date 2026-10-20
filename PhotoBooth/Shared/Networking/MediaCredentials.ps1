param([Parameter(Mandatory=$true)][string]$Hostname)
$ErrorActionPreference = 'Stop'
if ($Hostname.Length -gt 253 -or $Hostname -notmatch '^[A-Za-z0-9_]+(?:[A-Za-z0-9_.-]*[A-Za-z0-9_])?$') { throw 'Invalid Controller hostname.' }
    if (!('HomeTechnologies.MediaCredentialStore' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace HomeTechnologies {
    public static class MediaCredentialStore {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Credential {
            public uint Flags, Type;
            public string TargetName, Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist, AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias, UserName;
        }
        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref Credential credential, uint flags);
        public static void Save(string hostname, string username, string password) {
            IntPtr secret = Marshal.StringToCoTaskMemUni(password);
            try {
                var credential = new Credential {
                    Type = 2, TargetName = hostname, UserName = username,
                    CredentialBlobSize = checked((uint)(password.Length * 2)),
                    CredentialBlob = secret, Persist = 2
                };
                if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            } finally { Marshal.ZeroFreeCoTaskMemUnicode(secret); }
        }
    }
}
'@
    }
[HomeTechnologies.MediaCredentialStore]::Save($Hostname, ($Hostname+'\SaftThings'), '10375Saft')
