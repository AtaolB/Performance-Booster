using System.Runtime.InteropServices;

namespace PerformanceBooster.Core;

// NVIDIA driver settings of the global profile (Control Panel > Manage 3D settings > Global settings), through NVAPI's
// driver settings (DRS) functions. nvapi64.dll exports only nvapi_QueryInterface; function and setting IDs come from
// NVIDIA's own headers (nvapi_interface.h, NvApiDriverSettings.h, MIT licensed). Reading needs no administrator rights,
// saving does. Not available without an NVIDIA driver.
internal static partial class NvApi
{
    public const uint PowerManagementMode = 0x1057EB71; // PREFERRED_PSTATE_ID
    public const uint PreRenderedFrames = 0x007BA09E;   // PRERENDERLIMIT_ID ("Low latency mode")
    public const uint ShaderCacheSize = 0x00AC8497;     // PS_SHADERDISKCACHE_MAX_SIZE_ID (MB)
    public const uint RefreshRateOverride = 0x0064B541; // REFRESH_RATE_OVERRIDE_ID ("Preferred refresh rate"): 1 = highest available

    const int Ok = 0, SettingNotFound = -160;

    // NVDRS_SETTING_V1 (pack 4): version, name (2048 UTF-16), id 4100, type 4104, location 4108, isCurrentPredefined 4112,
    // isPredefinedValid 4116, predefined value 4120 (4100 bytes), current value 8220 (4100 bytes).
    const int SettingSize = 12320, IdOffset = 4100, TypeOffset = 4104, PredefinedOffset = 4112, CurrentOffset = 8220;
    const uint SettingVersion = SettingSize | (1 << 16);

    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    [LibraryImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface")]
    private static partial IntPtr QueryInterface(uint id);

    // NVAPI functions by ID, called through cdecl function pointers (no delegate is created per call).
    const uint InitializeId = 0x0150E828, CreateSessionId = 0x0694D52E, LoadSettingsId = 0x375DBD6B, GlobalProfileId = 0x617BFF9F,
        GetSettingId = 0x73BF8338, SetSettingId = 0x577DD202, DeleteSettingId = 0xE4A26362, SaveSettingsId = 0xFCBC7E14, DestroySessionId = 0xDAD9CFF8;

    static readonly Lazy<bool> _available = new(Initialize);

    public static bool Available => _available.Value;

    static unsafe bool Initialize()
    {
        try
        {
            return ((delegate* unmanaged[Cdecl]<int>)Function(InitializeId))() == Ok;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            return false;
        }
    }

    static IntPtr Function(uint id)
    {
        var pointer = QueryInterface(id);
        if (pointer == IntPtr.Zero) throw new InvalidOperationException(F("NVAPI function {0:X8} is missing", id));
        return pointer;
    }

    static void Check(int status, string what)
    {
        if (status != Ok) throw new InvalidOperationException(F("NVIDIA driver settings: {0} failed ({1})", what, status));
    }

    // The value the user (or a program) set in the global profile; null when the driver default applies.
    public static unsafe uint? Get(uint setting) => Session(false, (session, profile) =>
    {
        var data = NewSetting(setting);
        try
        {
            int status = ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, byte*, int>)Function(GetSettingId))(session, profile, setting, data);
            if (status == SettingNotFound) return null;
            Check(status, "GetSetting");
            return *(int*)(data + PredefinedOffset) != 0 ? null : *(uint*)(data + CurrentOffset);
        }
        finally
        {
            NativeMemory.Free(data);
        }
    });

    // null = back to the driver default.
    public static unsafe void Set(uint setting, uint? value) => Session(true, (session, profile) =>
    {
        if (value is null)
        {
            int status = ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, int>)Function(DeleteSettingId))(session, profile, setting);
            if (status != SettingNotFound) Check(status, "DeleteProfileSetting");
            return (uint?)null;
        }
        var data = NewSetting(setting);
        try
        {
            *(uint*)(data + CurrentOffset) = value.Value;
            Check(((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte*, int>)Function(SetSettingId))(session, profile, data), "SetSetting");
            return null;
        }
        finally
        {
            NativeMemory.Free(data);
        }
    });

    // A zeroed NVDRS_SETTING_V1 for a DWORD setting.
    static unsafe byte* NewSetting(uint setting)
    {
        var data = (byte*)NativeMemory.AllocZeroed(SettingSize);
        *(uint*)data = SettingVersion;
        *(uint*)(data + IdOffset) = setting;
        *(uint*)(data + TypeOffset) = 0; // NVDRS_DWORD_TYPE
        return data;
    }

    static unsafe uint? Session(bool save, Func<IntPtr, IntPtr, uint?> work)
    {
        if (!Available) throw new InvalidOperationException(T("No NVIDIA driver was found."));
        IntPtr session, profile;
        Check(((delegate* unmanaged[Cdecl]<IntPtr*, int>)Function(CreateSessionId))(&session), "CreateSession");
        try
        {
            Check(((delegate* unmanaged[Cdecl]<IntPtr, int>)Function(LoadSettingsId))(session), "LoadSettings");
            Check(((delegate* unmanaged[Cdecl]<IntPtr, IntPtr*, int>)Function(GlobalProfileId))(session, &profile), "GetCurrentGlobalProfile");
            var result = work(session, profile);
            if (save) Check(((delegate* unmanaged[Cdecl]<IntPtr, int>)Function(SaveSettingsId))(session), "SaveSettings");
            return result;
        }
        finally
        {
            ((delegate* unmanaged[Cdecl]<IntPtr, int>)Function(DestroySessionId))(session);
        }
    }
}

internal sealed class NvidiaStore(uint setting) : ISettingStore
{
    public bool Available => NvApi.Available;

    public string? Read() => NvApi.Get(setting)?.ToString();

    public void Write(string? value) => NvApi.Set(setting, value is null ? null : uint.Parse(value));
}
