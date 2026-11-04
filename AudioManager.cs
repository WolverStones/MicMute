using System.Runtime.InteropServices;

namespace MicMute;

/// <summary>
/// Manages microphone mute state via Windows Core Audio COM APIs.
/// Also provides speaker mute control for deafen mode.
/// </summary>
internal sealed class AudioManager : IDisposable
{
    // COM GUIDs
    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private static readonly Guid IID_IAudioSessionControl2 = new("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D");

    // PKEY_Device_FriendlyName = {A45C254E-DF1C-4EFD-8020-67D146A850E0}, 14
    private static readonly Guid PKEY_FriendlyName_fmtid = new("A45C254E-DF1C-4EFD-8020-67D146A850E0");
    private const int PKEY_FriendlyName_pid = 14;

    // IAudioEndpointVolume vtable offsets (after IUnknown 0-2)
    private const int VT_SetMute = 14;
    private const int VT_GetMute = 15;

    // IMMDeviceEnumerator vtable
    private const int VT_EnumAudioEndpoints = 3;
    private const int VT_GetDefaultAudioEndpoint = 4;
    private const int VT_GetDevice = 5;

    // IMMDevice vtable
    private const int VT_Activate = 3;
    private const int VT_OpenPropertyStore = 4;
    private const int VT_GetId = 5;

    // IMMDeviceCollection vtable
    private const int VT_Collection_GetCount = 3;
    private const int VT_Collection_Item = 4;

    // IPropertyStore vtable
    private const int VT_PropStore_GetValue = 5;

    // IAudioSessionManager2 vtable (IAudioSessionManager occupies 3-4)
    private const int VT_GetSessionEnumerator = 5;

    // IAudioSessionEnumerator vtable
    private const int VT_Sessions_GetCount = 3;
    private const int VT_Sessions_GetSession = 4;

    // IAudioSessionControl vtable
    private const int VT_Session_GetState = 3;

    // IAudioSessionControl2 vtable (IAudioSessionControl occupies 3-11)
    private const int VT_Session_GetProcessId = 14;

    private const int AudioSessionStateActive = 1;

    private nint _pAudioEndpointVolume;
    private bool _disposed;

    public bool HasEndpoint => _pAudioEndpointVolume != 0;

    /// <summary>
    /// Initialize the audio endpoint for the given device ID (empty = system default).
    /// Returns true if successful.
    /// </summary>
    public bool Initialize(string deviceId)
    {
        // Allow re-initialization even after Dispose (e.g. device hotplug)
        _disposed = false;
        Release();

        var clsid = CLSID_MMDeviceEnumerator;
        var iid = IID_IMMDeviceEnumerator;
        nint pEnum = 0;
        int hr = CoCreateInstance(ref clsid, 0, 1 /*CLSCTX_INPROC_SERVER*/,
            ref iid, out pEnum);
        if (hr < 0 || pEnum == 0)
        {
            Log.Warn($"Initialize: CoCreateInstance failed, hr=0x{hr:X8}");
            return false;
        }

        nint pDev = 0;
        if (!string.IsNullOrEmpty(deviceId))
        {
            // Try specific device
            hr = ComCall_GetDevice(pEnum, deviceId, out pDev);
            if (hr < 0 || pDev == 0)
            {
                // Fall back to default
                hr = ComCall_GetDefaultEndpoint(pEnum, out pDev);
            }
        }
        else
        {
            hr = ComCall_GetDefaultEndpoint(pEnum, out pDev);
        }

        Marshal.Release(pEnum);

        if (hr < 0 || pDev == 0)
        {
            Log.Warn($"Initialize: GetDevice/GetDefaultEndpoint failed, hr=0x{hr:X8}");
            return false;
        }

        hr = ComCall_Activate(pDev, out nint pAEV);
        Marshal.Release(pDev);

        // A1-F02 / A4-F02: if Activate fails but driver wrote pAEV non-zero, release defensively
        if (hr < 0)
        {
            Log.Warn($"Initialize: Activate failed, hr=0x{hr:X8}");
            if (pAEV != 0) Marshal.Release(pAEV);
            return false;
        }
        if (pAEV == 0)
        {
            Log.Warn($"Initialize: Activate returned null interface, hr=0x{hr:X8}");
            return false;
        }

        _pAudioEndpointVolume = pAEV;
        return true;
    }

    public void Release()
    {
        if (_pAudioEndpointVolume != 0)
        {
            Marshal.Release(_pAudioEndpointVolume);
            _pAudioEndpointVolume = 0;
        }
    }

    /// <summary>
    /// Get the current mute state. Returns null if the endpoint is invalid.
    /// </summary>
    public bool? GetMute()
    {
        if (_pAudioEndpointVolume == 0)
            return null;

        try
        {
            int hr = ComCall_GetMute(_pAudioEndpointVolume, out int muted);
            if (hr < 0)
                return null;
            return muted != 0;
        }
        catch (Exception ex)
        {
            Log.Warn("GetMute threw: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Set the mute state. Returns true on success.
    /// </summary>
    public bool SetMute(bool muted)
    {
        if (_pAudioEndpointVolume == 0)
            return false;

        try
        {
            int hr = ComCall_SetMute(_pAudioEndpointVolume, muted ? 1 : 0);
            return hr >= 0;
        }
        catch (Exception ex)
        {
            Log.Warn("SetMute threw: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Enumerate active capture (microphone) devices.
    /// </summary>
    public static List<AudioDevice> EnumerateCaptureDevices()
    {
        var devices = new List<AudioDevice>();

        var clsid = CLSID_MMDeviceEnumerator;
        var iid = IID_IMMDeviceEnumerator;
        int hr = CoCreateInstance(ref clsid, 0, 1, ref iid, out nint pEnum);
        if (hr < 0 || pEnum == 0)
            return devices;

        try
        {
            // EnumAudioEndpoints(eCapture=1, DEVICE_STATE_ACTIVE=1)
            hr = ComCall_EnumEndpoints(pEnum, out nint pCollection);

            // A1-F15: move the early-return inside the inner try so the finally's
            // Marshal.Release(pCollection) runs even on HRESULT-violation where the
            // driver may have written a non-zero pCollection before failing.
            try
            {
                if (hr < 0 || pCollection == 0)
                    return devices;

                hr = ComCall_GetCount(pCollection, out int count);
                if (hr < 0)
                    return devices;

                for (int i = 0; i < count; i++)
                {
                    hr = ComCall_Item(pCollection, i, out nint pDev);
                    if (hr < 0 || pDev == 0)
                        continue;

                    try
                    {
                        string devId = GetDeviceId(pDev);
                        string name = GetDeviceFriendlyName(pDev);
                        if (!string.IsNullOrEmpty(devId) && !string.IsNullOrEmpty(name))
                            devices.Add(new AudioDevice(name, devId));
                    }
                    finally
                    {
                        Marshal.Release(pDev);
                    }
                }
            }
            finally
            {
                if (pCollection != 0) Marshal.Release(pCollection);
            }
        }
        finally
        {
            Marshal.Release(pEnum);
        }

        return devices;
    }

    /// <summary>
    /// Return the process names of apps actively capturing from any microphone.
    /// All capture devices are scanned so a call routed through a virtual mic
    /// (VoiceMeeter and similar) is still seen.
    /// </summary>
    public static List<string> GetActiveCaptureProcessNames()
    {
        var names = new List<string>();

        var clsid = CLSID_MMDeviceEnumerator;
        var iid = IID_IMMDeviceEnumerator;
        int hr = CoCreateInstance(ref clsid, 0, 1, ref iid, out nint pEnum);
        if (hr < 0 || pEnum == 0)
            return names;

        try
        {
            hr = ComCall_EnumEndpoints(pEnum, out nint pCollection);
            try
            {
                if (hr < 0 || pCollection == 0)
                    return names;

                hr = ComCall_GetCount(pCollection, out int count);
                if (hr < 0)
                    return names;

                for (int i = 0; i < count; i++)
                {
                    hr = ComCall_Item(pCollection, i, out nint pDev);
                    if (hr < 0 || pDev == 0)
                        continue;

                    try
                    {
                        AddActiveSessionProcessNames(pDev, names);
                    }
                    finally
                    {
                        Marshal.Release(pDev);
                    }
                }
            }
            finally
            {
                if (pCollection != 0) Marshal.Release(pCollection);
            }
        }
        finally
        {
            Marshal.Release(pEnum);
        }

        return names;
    }

    private static void AddActiveSessionProcessNames(nint pDev, List<string> names)
    {
        int hr = ComCall_ActivateInterface(pDev, IID_IAudioSessionManager2, out nint pManager);
        try
        {
            if (hr < 0 || pManager == 0)
                return;

            nint managerVtable = Marshal.ReadIntPtr(pManager);
            nint enumeratorFn = Marshal.ReadIntPtr(managerVtable, VT_GetSessionEnumerator * nint.Size);
            var getEnumerator = Marshal.GetDelegateForFunctionPointer<GetSessionEnumeratorDelegate>(enumeratorFn);
            hr = getEnumerator(pManager, out nint pSessions);
            try
            {
                if (hr < 0 || pSessions == 0)
                    return;

                nint sessionsVtable = Marshal.ReadIntPtr(pSessions);
                nint countFn = Marshal.ReadIntPtr(sessionsVtable, VT_Sessions_GetCount * nint.Size);
                nint itemFn = Marshal.ReadIntPtr(sessionsVtable, VT_Sessions_GetSession * nint.Size);
                var getCount = Marshal.GetDelegateForFunctionPointer<GetCountDelegate>(countFn);
                var getSession = Marshal.GetDelegateForFunctionPointer<ItemDelegate>(itemFn);

                hr = getCount(pSessions, out int count);
                if (hr < 0)
                    return;

                for (int i = 0; i < count; i++)
                {
                    hr = getSession(pSessions, i, out nint pControl);
                    if (hr < 0 || pControl == 0)
                        continue;

                    try
                    {
                        string name = GetActiveSessionProcessName(pControl);
                        if (name != null)
                            names.Add(name);
                    }
                    finally
                    {
                        Marshal.Release(pControl);
                    }
                }
            }
            finally
            {
                if (pSessions != 0) Marshal.Release(pSessions);
            }
        }
        finally
        {
            if (pManager != 0) Marshal.Release(pManager);
        }
    }

    private static string GetActiveSessionProcessName(nint pControl)
    {
        nint vtable = Marshal.ReadIntPtr(pControl);
        nint stateFn = Marshal.ReadIntPtr(vtable, VT_Session_GetState * nint.Size);
        var getState = Marshal.GetDelegateForFunctionPointer<GetSessionStateDelegate>(stateFn);
        int hr = getState(pControl, out int state);
        if (hr < 0 || state != AudioSessionStateActive)
            return null;

        var iid = IID_IAudioSessionControl2;
        hr = Marshal.QueryInterface(pControl, ref iid, out nint pControl2);
        if (hr < 0 || pControl2 == 0)
            return null;

        try
        {
            nint vtable2 = Marshal.ReadIntPtr(pControl2);
            nint pidFn = Marshal.ReadIntPtr(vtable2, VT_Session_GetProcessId * nint.Size);
            var getProcessId = Marshal.GetDelegateForFunctionPointer<GetSessionProcessIdDelegate>(pidFn);
            hr = getProcessId(pControl2, out uint pid);
            if (hr < 0 || pid == 0)
                return null;

            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return null; // The process exited after the session was enumerated.
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        finally
        {
            Marshal.Release(pControl2);
        }
    }

    /// <summary>
    /// Get speaker (render) mute state for deafen mode.
    /// </summary>
    public static bool GetSpeakerMute()
    {
        nint pAEV = GetDefaultRenderEndpointVolume();
        if (pAEV == 0)
            return false;
        try
        {
            int hr = ComCall_GetMuteStatic(pAEV, out int muted);
            return hr >= 0 && muted != 0;
        }
        catch (Exception ex)
        {
            Log.Warn("GetSpeakerMute threw: " + ex.Message);
            return false;
        }
        finally
        {
            Marshal.Release(pAEV);
        }
    }

    /// <summary>
    /// Set speaker (render) mute state for deafen mode.
    /// </summary>
    public static void SetSpeakerMute(bool muted)
    {
        nint pAEV = GetDefaultRenderEndpointVolume();
        if (pAEV == 0)
            return;
        try
        {
            ComCall_SetMuteStatic(pAEV, muted ? 1 : 0);
        }
        catch (Exception ex)
        {
            Log.Warn("SetSpeakerMute threw: " + ex.Message);
        }
        finally
        {
            Marshal.Release(pAEV);
        }
    }

    private static nint GetDefaultRenderEndpointVolume()
    {
        var clsid = CLSID_MMDeviceEnumerator;
        var iid = IID_IMMDeviceEnumerator;
        int hr = CoCreateInstance(ref clsid, 0, 1, ref iid, out nint pEnum);
        if (hr < 0 || pEnum == 0)
            return 0;

        try
        {
            // GetDefaultAudioEndpoint(eRender=0, eConsole=0)
            hr = ComCall_GetDefaultRenderEndpoint(pEnum, out nint pDev);
            if (hr < 0 || pDev == 0)
                return 0;

            try
            {
                hr = ComCall_Activate(pDev, out nint pAEV);
                // A3-F12: log on failure so bad service state doesn't look like "no speakers"
                // A1-F03: if driver wrote pAEV non-zero before failing, release defensively
                if (hr < 0)
                {
                    Log.Warn($"GetDefaultRenderEndpointVolume: Activate failed, hr=0x{hr:X8}");
                    if (pAEV != 0) Marshal.Release(pAEV);
                    return 0;
                }
                return pAEV;
            }
            finally
            {
                Marshal.Release(pDev);
            }
        }
        finally
        {
            Marshal.Release(pEnum);
        }
    }

    private static string GetDeviceId(nint pDev)
    {
        // IMMDevice::GetId
        nint vtable = Marshal.ReadIntPtr(pDev);
        nint fnPtr = Marshal.ReadIntPtr(vtable, VT_GetId * nint.Size);
        var getId = Marshal.GetDelegateForFunctionPointer<GetIdDelegate>(fnPtr);
        int hr = getId(pDev, out nint pIdStr);
        if (hr < 0 || pIdStr == 0)
            return "";
        // A4-F04: ensure FreeCoTaskMem runs even if PtrToStringUni throws
        try
        {
            return Marshal.PtrToStringUni(pIdStr) ?? "";
        }
        finally
        {
            Marshal.FreeCoTaskMem(pIdStr);
        }
    }

    private static string GetDeviceFriendlyName(nint pDev)
    {
        // IMMDevice::OpenPropertyStore(STGM_READ=0)
        nint vtable = Marshal.ReadIntPtr(pDev);
        nint fnPtr = Marshal.ReadIntPtr(vtable, VT_OpenPropertyStore * nint.Size);
        var openProps = Marshal.GetDelegateForFunctionPointer<OpenPropertyStoreDelegate>(fnPtr);
        int hr = openProps(pDev, 0, out nint pStore);
        if (hr < 0 || pStore == 0)
            return "";

        try
        {
            // Build PROPERTYKEY struct
            byte[] pkeyBytes = new byte[20];
            PKEY_FriendlyName_fmtid.TryWriteBytes(pkeyBytes);
            BitConverter.TryWriteBytes(pkeyBytes.AsSpan(16), PKEY_FriendlyName_pid);

            // Allocate PROPVARIANT (24 bytes)
            nint pv = Marshal.AllocCoTaskMem(24);
            for (int i = 0; i < 24; i++)
                Marshal.WriteByte(pv, i, 0);

            try
            {
                nint storeVtable = Marshal.ReadIntPtr(pStore);
                nint getValuePtr = Marshal.ReadIntPtr(storeVtable, VT_PropStore_GetValue * nint.Size);

                nint pkeyMem = Marshal.AllocCoTaskMem(20);
                try
                {
                    Marshal.Copy(pkeyBytes, 0, pkeyMem, 20);
                    var getValue = Marshal.GetDelegateForFunctionPointer<GetValueDelegate>(getValuePtr);
                    hr = getValue(pStore, pkeyMem, pv);
                    if (hr < 0)
                        return "";

                    short vt = Marshal.ReadInt16(pv);
                    if (vt == 31) // VT_LPWSTR
                    {
                        nint pStr = Marshal.ReadIntPtr(pv, 8);
                        return pStr != 0 ? (Marshal.PtrToStringUni(pStr) ?? "") : "";
                    }
                    return "";
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pkeyMem);
                }
            }
            finally
            {
                // A1-F01: PropVariantClear unconditional — handles HRESULT-violation where
                // callee wrote VT+payload before failing; must precede FreeCoTaskMem.
                PropVariantClear(pv);
                Marshal.FreeCoTaskMem(pv);
            }
        }
        finally
        {
            Marshal.Release(pStore);
        }
    }

    // COM call helpers using manual vtable dispatch

    private static int ComCall_GetDefaultEndpoint(nint pEnum, out nint pDev)
    {
        // IMMDeviceEnumerator::GetDefaultAudioEndpoint(eCapture=1, eConsole=0)
        nint vtable = Marshal.ReadIntPtr(pEnum);
        nint fnPtr = Marshal.ReadIntPtr(vtable, VT_GetDefaultAudioEndpoint * nint.Size);
        var fn = Marshal.GetDelegateForFunctionPointer<GetDefaultEndpointDelegate>(fnPtr);
        return fn(pEnum, 1, 0, out pDev);
    }

    private static int ComCall_GetDefaultRenderEndpoint(nint pEnum, out nint pDev)
    {
        // IMMDeviceEnumerator::GetDefaultAudioEndpoint(eRender=0, eConsole=0)
        nint vtable = Marshal.ReadIntPtr(pEnum);
        nint fnPtr = Marshal.ReadIntPtr(vtable, VT_GetDefaultAudioEndpoint * nint.Size);
        var fn = Marshal.GetDelegateForFunctionPointer<GetDefaultEndpointDelegate>(fnPtr);
        return fn(pEnum, 0, 0, out pDev);
    }

    private static int ComCall_GetDevice(nint pEnum, string deviceId, out nint pDev)
    {
        nint vtable = Marshal.ReadIntPtr(pEnum);
        nint fnPtr = Marshal.ReadIntPtr(vtable, VT_GetDevice * nint.Size);
        var fn = Marshal.GetDelegateForFunctionPointer<GetDeviceDelegate>(fnPtr);
        return fn(pEnum, deviceId, out pDev);
    }

    private static int ComCall_EnumEndpoints(nint pEnum, out nint pCollection)
    {
        nint vtable = Marshal.ReadIntPtr(pEnum);
        nint fnPtr = Marshal.ReadIntPtr(vtable, VT_EnumAudioEndpoints * nint.Size);
        var fn = Marshal.GetDelegateForFunctionPointer<EnumEndpointsDelegate>(fnPtr);
        return fn(pEnum, 1, 1, out pCollection); // eCapture=1, ACTIVE=1
    }

    private static int ComCall_GetCount(nint pCollection, out int count)
    {
        nint vtable = Marshal.ReadIntPtr(pCollection);
        nint fnPtr = Marshal.ReadIntPtr(vtable, VT_Collection_GetCount * nint.Size);
        var fn = Marshal.GetDelegateForFunctionPointer<GetCountDelegate>(fnPtr);
        return fn(pCollection, out count);
    }

    private static int ComCall_Item(nint pCollection, int index, out nint pDev)
    {
        nint vtable = Marshal.ReadIntPtr(pCollection);
        nint fnPtr = Marshal.ReadIntPtr(vtable, VT_Collection_Item * nint.Size);
        var fn = Marshal.GetDelegateForFunctionPointer<ItemDelegate>(fnPtr);
        return fn(pCollection, index, out pDev);
    }

    private static int ComCall_Activate(nint pDev, out nint pAEV)
    {
        nint vtable = Marshal.ReadIntPtr(pDev);
        nint fnPtr = Marshal.ReadIntPtr(vtable, VT_Activate * nint.Size);
        var fn = Marshal.GetDelegateForFunctionPointer<ActivateDelegate>(fnPtr);
        var iidAEV = IID_IAudioEndpointVolume;
        return fn(pDev, in iidAEV, 1, 0, out pAEV);
    }

    private static int ComCall_ActivateInterface(nint pDev, Guid iid, out nint pInterface)
    {
        nint vtable = Marshal.ReadIntPtr(pDev);
        nint fnPtr = Marshal.ReadIntPtr(vtable, VT_Activate * nint.Size);
        var fn = Marshal.GetDelegateForFunctionPointer<ActivateDelegate>(fnPtr);
        return fn(pDev, in iid, 1, 0, out pInterface);
    }

    private int ComCall_GetMute(nint pAEV, out int muted)
    {
        nint vtable = Marshal.ReadIntPtr(pAEV);
        nint fnPtr = Marshal.ReadIntPtr(vtable, VT_GetMute * nint.Size);
        var fn = Marshal.GetDelegateForFunctionPointer<GetMuteDelegate>(fnPtr);
        return fn(pAEV, out muted);
    }

    private int ComCall_SetMute(nint pAEV, int muted)
    {
        nint vtable = Marshal.ReadIntPtr(pAEV);
        nint fnPtr = Marshal.ReadIntPtr(vtable, VT_SetMute * nint.Size);
        var fn = Marshal.GetDelegateForFunctionPointer<SetMuteDelegate>(fnPtr);
        return fn(pAEV, muted, 0);
    }

    private static int ComCall_GetMuteStatic(nint pAEV, out int muted)
    {
        nint vtable = Marshal.ReadIntPtr(pAEV);
        nint fnPtr = Marshal.ReadIntPtr(vtable, VT_GetMute * nint.Size);
        var fn = Marshal.GetDelegateForFunctionPointer<GetMuteDelegate>(fnPtr);
        return fn(pAEV, out muted);
    }

    private static int ComCall_SetMuteStatic(nint pAEV, int muted)
    {
        nint vtable = Marshal.ReadIntPtr(pAEV);
        nint fnPtr = Marshal.ReadIntPtr(vtable, VT_SetMute * nint.Size);
        var fn = Marshal.GetDelegateForFunctionPointer<SetMuteDelegate>(fnPtr);
        return fn(pAEV, muted, 0);
    }

    // COM delegates
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDefaultEndpointDelegate(nint pThis, int dataFlow, int role, out nint ppEndpoint);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int GetDeviceDelegate(nint pThis, [MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out nint ppDevice);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumEndpointsDelegate(nint pThis, int dataFlow, int stateMask, out nint ppDevices);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetCountDelegate(nint pThis, out int count);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ItemDelegate(nint pThis, int nDevice, out nint ppDevice);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ActivateDelegate(nint pThis, in Guid iid, int dwClsCtx, nint pActivationParams, out nint ppInterface);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetMuteDelegate(nint pThis, out int pbMute);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetMuteDelegate(nint pThis, int bMute, nint pguidEventContext);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetIdDelegate(nint pThis, out nint ppwstrId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OpenPropertyStoreDelegate(nint pThis, int stgmAccess, out nint ppProperties);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetValueDelegate(nint pThis, nint key, nint pv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSessionEnumeratorDelegate(nint pThis, out nint ppSessionEnum);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSessionStateDelegate(nint pThis, out int pState);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSessionProcessIdDelegate(nint pThis, out uint pRetVal);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid rclsid, nint pUnkOuter, int dwClsContext, ref Guid riid, out nint ppv);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(nint pvar);

    public void Dispose()
    {
        if (!_disposed)
        {
            Release();
            _disposed = true;
        }
    }
}

internal sealed record AudioDevice(string Name, string Id);
