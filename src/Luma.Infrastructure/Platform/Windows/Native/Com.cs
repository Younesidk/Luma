using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Luma.Infrastructure.Platform.Windows.Native;

/// <summary>
/// Raw COM calls through vtable slots. Every slot index below was taken from the Windows 10.0.26100 SDK headers
/// (mfmediaengine.h, mfobjects.h, d3d11.h, d3d11_4.h, dxgi.h, dxgi1_2.h); comments name the method at each slot.
/// Raw calls keep Luma free of interop packages and let the code show exactly which methods are used.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class Com
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_NOINTERFACE = unchecked((int)0x80004002);

    public static void* Slot(nint obj, int index) => (*(void***)obj)[index];

    public static uint AddRef(nint obj) => ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(obj, 1))(obj);

    public static uint Release(nint obj) => obj == 0 ? 0 : ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(obj, 2))(obj);

    public static void ReleaseAndClear(ref nint obj)
    {
        var p = obj;
        obj = 0;
        if (p != 0) Release(p);
    }

    public static int QueryInterface(nint obj, in Guid iid, out nint result)
    {
        nint r = 0;
        int hr;
        fixed (Guid* g = &iid) hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(obj, 0))(obj, g, &r);
        result = r;
        return hr;
    }

    public static void Check(int hr, string operation)
    {
        if (hr < 0) throw new COMException($"{operation} failed (0x{hr:X8}).", hr);
    }
}

[SupportedOSPlatform("windows")]
internal static class Iid
{
    public static readonly Guid IUnknown = new("00000000-0000-0000-C000-000000000046");
    public static readonly Guid MFMediaEngineClassFactory = new("4D645ACE-26AA-4688-9BE1-DF3516990B93");
    public static readonly Guid MFMediaEngine = new("98a1b0bb-03eb-4935-ae7c-93c1fa0e1c93");
    public static readonly Guid MFMediaEngineEx = new("83015ead-b1e6-40d0-a98a-37145ffe1ad1");
    public static readonly Guid MFMediaEngineNotify = new("fee7c112-e776-42b5-9bbf-0048524e2bd5");
    public static readonly Guid D3D11Multithread = new("9B7E4E00-342C-4106-A19F-4F2704F689F0");
    public static readonly Guid D3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    public static readonly Guid DXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    public static readonly Guid DXGIFactory2 = new("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
}

[SupportedOSPlatform("windows")]
internal static class MfGuids
{
    public static readonly Guid CLSID_MFMediaEngineClassFactory = new("b44392da-499b-446b-a4cb-005fead0e6d5");
    public static readonly Guid MF_MEDIA_ENGINE_CALLBACK = new("c60381b8-83a4-41f8-a3d0-de05076849a9");
    public static readonly Guid MF_MEDIA_ENGINE_DXGI_MANAGER = new("065702da-1094-486d-8617-ee7cc4ee4648");
    public static readonly Guid MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT = new("5066893c-8cf9-42bc-8b8a-472212e52726");
    public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
}

/// <summary>Media Engine event codes (MF_MEDIA_ENGINE_EVENT).</summary>
internal enum MediaEngineEvent : uint
{
    LoadStart = 1,
    Abort = 4,
    Error = 5,
    Stalled = 7,
    LoadedMetadata = 10,
    LoadedData = 11,
    Playing = 13,
    CanPlay = 14,
    Seeked = 17,
    Ended = 19,
    FirstFrameReady = 1009
}

[StructLayout(LayoutKind.Sequential)]
internal struct MFVideoNormalizedRect
{
    public float Left, Top, Right, Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left, Top, Right, Bottom;
    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MFARGB
{
    public byte Blue, Green, Red, Alpha;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DXGI_SAMPLE_DESC
{
    public uint Count, Quality;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DXGI_SWAP_CHAIN_DESC1
{
    public uint Width, Height;
    public int Format;
    public int Stereo;
    public DXGI_SAMPLE_DESC SampleDesc;
    public uint BufferUsage;
    public uint BufferCount;
    public int Scaling;
    public int SwapEffect;
    public int AlphaMode;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11_TEXTURE2D_DESC
{
    public uint Width, Height, MipLevels, ArraySize;
    public int Format;
    public DXGI_SAMPLE_DESC SampleDesc;
    public int Usage;
    public uint BindFlags, CPUAccessFlags, MiscFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct D3D11_MAPPED_SUBRESOURCE
{
    public void* Data;
    public uint RowPitch, DepthPitch;
}

[SupportedOSPlatform("windows")]
internal static partial class NativeMethods
{
    public const uint MF_VERSION = 0x00020070;
    public const uint MFSTARTUP_LITE = 1;
    public const int DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    public const int D3D_DRIVER_TYPE_HARDWARE = 1;
    public const int D3D_DRIVER_TYPE_WARP = 5;
    public const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    public const uint D3D11_CREATE_DEVICE_VIDEO_SUPPORT = 0x800;
    public const uint D3D11_SDK_VERSION = 7;
    public const int DXGI_ERROR_DEVICE_REMOVED = unchecked((int)0x887A0005);
    public const int DXGI_ERROR_DEVICE_RESET = unchecked((int)0x887A0007);

    [LibraryImport("mfplat.dll")]
    public static partial int MFStartup(uint version, uint flags);

    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateAttributes(out nint attributes, uint initialSize);

    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateDXGIDeviceManager(out uint resetToken, out nint manager);

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint result);

    [LibraryImport("ole32.dll")]
    public static unsafe partial int PropVariantClear(void* propVariant);

    [LibraryImport("d3d11.dll")]
    public static partial int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, nint featureLevels,
        uint featureLevelCount, uint sdkVersion, out nint device, out int featureLevel, out nint immediateContext);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmFlush();
}
