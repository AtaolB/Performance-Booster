using System.Runtime.InteropServices;

namespace PerformanceBooster.Launcher;

// Before the downloaded installer runs: a valid Authenticode signature (WinVerifyTrust) whose signer is Microsoft.
static unsafe partial class Signature
{
    struct WintrustFileInfo
    {
        public uint Size;
        public char* FilePath;
        public IntPtr File;
        public Guid* KnownSubject;
    }

    struct WintrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData, SipClientData;
        public uint UiChoice, RevocationChecks, UnionChoice;
        public WintrustFileInfo* File;
        public uint StateAction;
        public IntPtr StateData;
        public char* UrlReference;
        public uint ProviderFlags, UiContext;
        public IntPtr SignatureSettings;
    }

    // WINTRUST_ACTION_GENERIC_VERIFY_V2
    static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
    const uint UiNone = 2, ChoiceFile = 1, StateVerify = 1, StateClose = 2, AttributeType = 3;

    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(IntPtr hwnd, Guid* action, WintrustData* data);

    [LibraryImport("wintrust.dll")]
    private static partial IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

    [LibraryImport("wintrust.dll")]
    private static partial byte* WTHelperGetProvSignerFromChain(IntPtr providerData, uint signer, int counterSigner, uint counterSignerIndex);

    [LibraryImport("crypt32.dll")]
    private static partial uint CertGetNameStringW(IntPtr certificate, uint type, uint flags, void* typeParameter, char* name, uint length);

    public static bool IsMicrosoft(string file)
    {
        fixed (char* path = file)
        {
            var info = new WintrustFileInfo { Size = (uint)sizeof(WintrustFileInfo), FilePath = path };
            var data = new WintrustData { Size = (uint)sizeof(WintrustData), UiChoice = UiNone, UnionChoice = ChoiceFile, File = &info, StateAction = StateVerify };
            var action = GenericVerifyV2;
            try
            {
                if (WinVerifyTrust(-1, &action, &data) != 0) return false;
                // CRYPT_PROVIDER_SGNR: certificate count 12, chain 16; CRYPT_PROVIDER_CERT: certificate 8. The first is the signer's.
                var signer = WTHelperGetProvSignerFromChain(WTHelperProvDataFromStateData(data.StateData), 0, 0, 0);
                if (signer == null || *(uint*)(signer + 12) == 0) return false;
                var certificate = *(IntPtr*)(*(byte**)(signer + 16) + 8);
                const int Capacity = 256;
                char* name = stackalloc char[Capacity];
                // The organization (O=): .NET's installers are signed "CN=.NET, O=Microsoft Corporation".
                fixed (byte* organization = "2.5.4.10"u8) // szOID_ORGANIZATION_NAME
                    return CertGetNameStringW(certificate, AttributeType, 0, organization, name, Capacity) > 1
                           && new string(name).Equals("Microsoft Corporation", StringComparison.Ordinal);
            }
            finally
            {
                data.StateAction = StateClose;
                WinVerifyTrust(-1, &action, &data);
            }
        }
    }
}
