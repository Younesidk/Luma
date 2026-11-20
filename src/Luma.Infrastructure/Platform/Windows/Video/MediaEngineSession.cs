using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Luma.Core.Logic;
using Luma.Core.Models;
using Luma.Infrastructure.Platform.Windows.Native;

namespace Luma.Infrastructure.Platform.Windows.Video;

/// <summary>
/// One Media Foundation Media Engine in frame-server mode plus the Direct3D 11 device it decodes on. Hardware decoding
/// (DXVA) is used whenever the GPU supports the codec. Frames are pulled with <see cref="TryGetNewFrame"/> and copied
/// (scaled and colour converted on the GPU) into any number of target textures, so one decode can feed several monitors.
/// Engine events arrive on Media Foundation worker threads through <see cref="EventReceived"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class MediaEngineSession : IDisposable
{
    // IMFMediaEngine / IMFMediaEngineEx slots (mfmediaengine.h).
    private const int GetError = 3, SetSource = 6, GetReadyState = 14, SetCurrentTime = 17, GetDuration = 19, IsPaused = 20,
        SetLoop = 31, Play = 32, Pause = 33, SetMuted = 35, HasVideo = 38, HasAudio = 39, GetNativeVideoSize = 40, Shutdown = 42,
        TransferVideoFrame = 43, OnVideoStreamTick = 44, GetNumberOfStreams = 54, GetStreamAttribute = 55, SetStreamSelection = 57,
        ApplyStreamSelections = 58;

    private const uint MF_MEDIA_ENGINE_FORCEMUTE = 0x4;

    private static readonly object StartupLock = new();
    private static int _startupResult = 1; // 1 = not attempted

    private nint _device;
    private nint _context;
    private nint _multithread;
    private nint _dxgiManager;
    private nint _engine;
    private nint _engineEx;
    private EngineNotify? _notify;
    private int _disposed;

    private MediaEngineSession()
    {
    }

    /// <summary>Raised on Media Foundation threads. Never call blocking engine methods from the handler.</summary>
    public event Action<MediaEngineEvent, nint, uint>? EventReceived;

    public nint Device => _device;

    public bool IsHardwareDevice { get; private set; }

    /// <summary>Starts Media Foundation once per process. Returns null on success, otherwise why it isn't available.</summary>
    public static string? EnsureMediaFoundation()
    {
        lock (StartupLock)
        {
            if (_startupResult == 1)
            {
                try
                {
                    _startupResult = NativeMethods.MFStartup(NativeMethods.MF_VERSION, NativeMethods.MFSTARTUP_LITE);
                }
                catch (DllNotFoundException)
                {
                    _startupResult = unchecked((int)0x8007007E);
                }
            }
            return _startupResult >= 0
                ? null
                : "Windows Media Foundation isn't available on this PC (for example on Windows \"N\" editions). Install the Media Feature Pack from Settings › Apps › Optional features.";
        }
    }

    public static MediaEngineSession Create()
    {
        if (EnsureMediaFoundation() is { } problem) throw new InvalidOperationException(problem);
        var session = new MediaEngineSession();
        try
        {
            session.CreateDevice();
            session.CreateEngine();
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private void CreateDevice()
    {
        var hr = NativeMethods.D3D11CreateDevice(0, NativeMethods.D3D_DRIVER_TYPE_HARDWARE, 0,
            NativeMethods.D3D11_CREATE_DEVICE_BGRA_SUPPORT | NativeMethods.D3D11_CREATE_DEVICE_VIDEO_SUPPORT, 0, 0,
            NativeMethods.D3D11_SDK_VERSION, out _device, out _, out _context);
        IsHardwareDevice = hr >= 0;
        if (hr < 0)
        {
            // No usable GPU (remote sessions, broken driver): software rendering still works, just with more CPU.
            hr = NativeMethods.D3D11CreateDevice(0, NativeMethods.D3D_DRIVER_TYPE_WARP, 0, NativeMethods.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                0, 0, NativeMethods.D3D11_SDK_VERSION, out _device, out _, out _context);
        }
        Com.Check(hr, "Creating the Direct3D device");

        // Media Foundation uses the device from its own threads: the device must be multithread protected.
        Com.Check(Com.QueryInterface(_device, Iid.D3D11Multithread, out _multithread), "Querying ID3D11Multithread");
        ((delegate* unmanaged[Stdcall]<nint, int, int>)Com.Slot(_multithread, 5))(_multithread, 1); // SetMultithreadProtected(TRUE)

        Com.Check(NativeMethods.MFCreateDXGIDeviceManager(out var token, out _dxgiManager), "MFCreateDXGIDeviceManager");
        Com.Check(((delegate* unmanaged[Stdcall]<nint, nint, uint, int>)Com.Slot(_dxgiManager, 7))(_dxgiManager, _device, token), "ResetDevice");
    }

    private void CreateEngine()
    {
        nint factory = 0, attributes = 0;
        try
        {
            Com.Check(NativeMethods.CoCreateInstance(MfGuids.CLSID_MFMediaEngineClassFactory, 0, 1 /* CLSCTX_INPROC_SERVER */,
                Iid.MFMediaEngineClassFactory, out factory), "Creating the Media Engine factory");
            Com.Check(NativeMethods.MFCreateAttributes(out attributes, 3), "MFCreateAttributes");

            _notify = new EngineNotify(OnEngineEvent);
            SetUnknown(attributes, MfGuids.MF_MEDIA_ENGINE_DXGI_MANAGER, _dxgiManager);
            SetUnknown(attributes, MfGuids.MF_MEDIA_ENGINE_CALLBACK, _notify.Pointer);
            var outputFormatKey = MfGuids.MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT;
            // IMFAttributes::SetUINT32 (slot 21)
            Com.Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Com.Slot(attributes, 21))(attributes, &outputFormatKey, NativeMethods.DXGI_FORMAT_B8G8R8A8_UNORM), "SetUINT32");

            nint engine;
            // IMFMediaEngineClassFactory::CreateInstance (slot 3)
            Com.Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, nint*, int>)Com.Slot(factory, 3))(factory, MF_MEDIA_ENGINE_FORCEMUTE, attributes, &engine),
                "Creating the Media Engine");
            _engine = engine;
            if (Com.QueryInterface(_engine, Iid.MFMediaEngineEx, out var ex) >= 0) _engineEx = ex;

            Call(SetMuted, 1);
            Call(SetLoop, 1);
        }
        finally
        {
            Com.Release(attributes);
            Com.Release(factory);
        }

        static void SetUnknown(nint attributes, Guid key, nint value)
        {
            // IMFAttributes::SetUnknown (slot 27)
            Com.Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint, int>)Com.Slot(attributes, 27))(attributes, &key, value), "SetUnknown");
        }
    }

    public void Load(string filePath)
    {
        var url = Marshal.StringToBSTR(new Uri(filePath).AbsoluteUri);
        try
        {
            Com.Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Com.Slot(_engine, SetSource))(_engine, url), "Opening the video");
        }
        finally
        {
            Marshal.FreeBSTR(url);
        }
    }

    public void SetPlaying(bool playing) => Com.Check(((delegate* unmanaged[Stdcall]<nint, int>)Com.Slot(_engine, playing ? Play : Pause))(_engine), playing ? "Play" : "Pause");

    public bool Paused => ((delegate* unmanaged[Stdcall]<nint, int>)Com.Slot(_engine, IsPaused))(_engine) != 0;

    public int ReadyState => ((delegate* unmanaged[Stdcall]<nint, ushort>)Com.Slot(_engine, GetReadyState))(_engine);

    public bool HasVideoStream => ((delegate* unmanaged[Stdcall]<nint, int>)Com.Slot(_engine, HasVideo))(_engine) != 0;

    public bool HasAudioStream => ((delegate* unmanaged[Stdcall]<nint, int>)Com.Slot(_engine, HasAudio))(_engine) != 0;

    /// <summary>Duration in seconds; NaN or infinity when unknown.</summary>
    public double DurationSeconds => ((delegate* unmanaged[Stdcall]<nint, double>)Com.Slot(_engine, GetDuration))(_engine);

    public (int Width, int Height) NativeSize
    {
        get
        {
            uint cx = 0, cy = 0;
            var hr = ((delegate* unmanaged[Stdcall]<nint, uint*, uint*, int>)Com.Slot(_engine, GetNativeVideoSize))(_engine, &cx, &cy);
            return hr >= 0 ? ((int)cx, (int)cy) : (0, 0);
        }
    }

    public void Seek(double seconds) => Com.Check(((delegate* unmanaged[Stdcall]<nint, double, int>)Com.Slot(_engine, SetCurrentTime))(_engine, seconds), "Seek");

    /// <summary>True when a new frame is ready since the last call (IMFMediaEngine::OnVideoStreamTick returns S_OK).</summary>
    public bool TryGetNewFrame()
    {
        long pts;
        return ((delegate* unmanaged[Stdcall]<nint, long*, int>)Com.Slot(_engine, OnVideoStreamTick))(_engine, &pts) == Com.S_OK;
    }

    /// <summary>Copies the current frame into <paramref name="texture"/>, scaling <paramref name="layout"/>'s source region into its destination. The rest is black.</summary>
    public int TransferFrame(nint texture, VideoFrameLayout layout)
    {
        var src = new MFVideoNormalizedRect { Left = layout.Source.Left, Top = layout.Source.Top, Right = layout.Source.Right, Bottom = layout.Source.Bottom };
        var dst = new RECT { Left = layout.Destination.X, Top = layout.Destination.Y, Right = layout.Destination.Right, Bottom = layout.Destination.Bottom };
        var border = new MFARGB { Alpha = 255 };
        return ((delegate* unmanaged[Stdcall]<nint, nint, MFVideoNormalizedRect*, RECT*, MFARGB*, int>)Com.Slot(_engine, TransferVideoFrame))(_engine, texture, &src, &dst, &border);
    }

    /// <summary>The engine's error after an <see cref="MediaEngineEvent.Error"/> event: (MF_MEDIA_ENGINE_ERR code, extended HRESULT).</summary>
    public (int Code, int Extended) ReadError()
    {
        nint error;
        if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Com.Slot(_engine, GetError))(_engine, &error) < 0 || error == 0) return (0, 0);
        try
        {
            var code = ((delegate* unmanaged[Stdcall]<nint, ushort>)Com.Slot(error, 3))(error);       // IMFMediaError::GetErrorCode
            var extended = ((delegate* unmanaged[Stdcall]<nint, int>)Com.Slot(error, 4))(error);    // GetExtendedErrorCode
            return (code, extended);
        }
        finally
        {
            Com.Release(error);
        }
    }

    /// <summary>Deselects audio streams so they are never decoded (the wallpaper is always silent). Best effort.</summary>
    public void DisableAudioStreams()
    {
        if (_engineEx == 0 || !HasAudioStream) return;
        uint count;
        if (((delegate* unmanaged[Stdcall]<nint, uint*, int>)Com.Slot(_engineEx, GetNumberOfStreams))(_engineEx, &count) < 0) return;

        var changed = false;
        var propVariant = stackalloc byte[24];
        for (uint i = 0; i < count; i++)
        {
            new Span<byte>(propVariant, 24).Clear();
            int hr;
            var majorType = MfGuids.MF_MT_MAJOR_TYPE;
            hr = ((delegate* unmanaged[Stdcall]<nint, uint, Guid*, void*, int>)Com.Slot(_engineEx, GetStreamAttribute))(_engineEx, i, &majorType, propVariant);
            if (hr < 0) continue;
            try
            {
                const ushort VT_CLSID = 72;
                var vt = *(ushort*)propVariant;
                var guid = *(Guid**)(propVariant + 8);
                if (vt == VT_CLSID && guid != null && *guid == MfGuids.MFMediaType_Audio)
                {
                    changed |= ((delegate* unmanaged[Stdcall]<nint, uint, int, int>)Com.Slot(_engineEx, SetStreamSelection))(_engineEx, i, 0) >= 0;
                }
            }
            finally
            {
                NativeMethods.PropVariantClear(propVariant);
            }
        }
        if (changed) ((delegate* unmanaged[Stdcall]<nint, int>)Com.Slot(_engineEx, ApplyStreamSelections))(_engineEx);
    }

    /// <summary>The video codec's FourCC-based name (H.264, HEVC, VP9, AV1...), or null when the engine doesn't say.</summary>
    public string? VideoCodec()
    {
        if (_engineEx == 0) return null;
        uint count;
        if (((delegate* unmanaged[Stdcall]<nint, uint*, int>)Com.Slot(_engineEx, GetNumberOfStreams))(_engineEx, &count) < 0) return null;
        for (uint i = 0; i < count; i++)
        {
            if (GetStreamGuid(i, MfGuids.MF_MT_MAJOR_TYPE) != MfGuids.MFMediaType_Video) continue;
            if (GetStreamGuid(i, MfGuids.MF_MT_SUBTYPE) is not { } subtype) return null;
            var fourCc = BitConverter.GetBytes(BitConverter.ToUInt32(subtype.ToByteArray(), 0));
            var code = System.Text.Encoding.ASCII.GetString(fourCc).TrimEnd('\0', ' ').ToUpperInvariant();
            return code switch
            {
                "H264" or "AVC1" => "H.264",
                "HEVC" or "H265" or "HVC1" => "HEVC (H.265)",
                "VP90" => "VP9",
                "VP80" => "VP8",
                "AV01" => "AV1",
                "MP4V" => "MPEG-4 Part 2",
                "WMV3" or "WVC1" => "Windows Media Video",
                "MJPG" => "Motion JPEG",
                _ => code.All(c => c is >= ' ' and <= '~') && code.Length > 0 ? code : null
            };
        }
        return null;
    }

    private Guid? GetStreamGuid(uint stream, Guid key)
    {
        var propVariant = stackalloc byte[24];
        new Span<byte>(propVariant, 24).Clear();
        if (((delegate* unmanaged[Stdcall]<nint, uint, Guid*, void*, int>)Com.Slot(_engineEx, GetStreamAttribute))(_engineEx, stream, &key, propVariant) < 0) return null;
        try
        {
            const ushort VT_CLSID = 72;
            var guid = *(Guid**)(propVariant + 8);
            return *(ushort*)propVariant == VT_CLSID && guid != null ? *guid : null;
        }
        finally
        {
            NativeMethods.PropVariantClear(propVariant);
        }
    }

    // ----- Direct3D helpers used by outputs and frame capture -----

    public void EnterDevice() => ((delegate* unmanaged[Stdcall]<nint, void>)Com.Slot(_multithread, 3))(_multithread);

    public void LeaveDevice() => ((delegate* unmanaged[Stdcall]<nint, void>)Com.Slot(_multithread, 4))(_multithread);

    /// <summary>Grabs the current frame scaled to <paramref name="width"/>×<paramref name="height"/> as BGRA bytes (row pitch = width * 4).</summary>
    public byte[]? CaptureFrame(int width, int height)
    {
        nint target = 0, staging = 0;
        try
        {
            var desc = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = NativeMethods.DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 }, Usage = 0 /* DEFAULT */, BindFlags = 0x20 /* RENDER_TARGET */
            };
            // ID3D11Device::CreateTexture2D (slot 5)
            if (((delegate* unmanaged[Stdcall]<nint, D3D11_TEXTURE2D_DESC*, nint, nint*, int>)Com.Slot(_device, 5))(_device, &desc, 0, &target) < 0) return null;
            desc.Usage = 3; // STAGING
            desc.BindFlags = 0;
            desc.CPUAccessFlags = 0x20000; // CPU_ACCESS_READ
            if (((delegate* unmanaged[Stdcall]<nint, D3D11_TEXTURE2D_DESC*, nint, nint*, int>)Com.Slot(_device, 5))(_device, &desc, 0, &staging) < 0) return null;

            if (TransferFrame(target, VideoLayout.Compute(width, height, width, height, Core.Enums.WallpaperFitMode.Stretch)) < 0) return null;

            EnterDevice();
            try
            {
                ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)Com.Slot(_context, 47))(_context, staging, target); // CopyResource
                D3D11_MAPPED_SUBRESOURCE mapped;
                if (((delegate* unmanaged[Stdcall]<nint, nint, uint, int, uint, D3D11_MAPPED_SUBRESOURCE*, int>)Com.Slot(_context, 14))(_context, staging, 0, 1 /* READ */, 0, &mapped) < 0)
                    return null;
                try
                {
                    var rowBytes = width * 4;
                    var pixels = new byte[rowBytes * height];
                    for (var y = 0; y < height; y++)
                        new ReadOnlySpan<byte>((byte*)mapped.Data + (long)y * mapped.RowPitch, rowBytes).CopyTo(pixels.AsSpan(y * rowBytes, rowBytes));
                    return pixels;
                }
                finally
                {
                    ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Com.Slot(_context, 15))(_context, staging, 0); // Unmap
                }
            }
            finally
            {
                LeaveDevice();
            }
        }
        finally
        {
            Com.Release(staging);
            Com.Release(target);
        }
    }

    private void Call(int slot, int arg) => ((delegate* unmanaged[Stdcall]<nint, int, int>)Com.Slot(_engine, slot))(_engine, arg);

    private void OnEngineEvent(uint code, nint param1, uint param2) => EventReceived?.Invoke((MediaEngineEvent)code, param1, param2);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        EventReceived = null;
        if (_engine != 0) ((delegate* unmanaged[Stdcall]<nint, int>)Com.Slot(_engine, Shutdown))(_engine);
        Com.ReleaseAndClear(ref _engineEx);
        Com.ReleaseAndClear(ref _engine);
        _notify?.Dispose();
        _notify = null;
        Com.ReleaseAndClear(ref _dxgiManager);
        Com.ReleaseAndClear(ref _multithread);
        Com.ReleaseAndClear(ref _context);
        Com.ReleaseAndClear(ref _device);
    }

    /// <summary>
    /// A hand-built COM object implementing IMFMediaEngineNotify. Native memory holds the vtable pointer, a reference count and a
    /// GC handle to this object; the memory and handle are freed when Media Foundation and Luma have both released it.
    /// </summary>
    private sealed class EngineNotify : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeObject
        {
            public void** Vtable;
            public int RefCount;
            public nint Handle;
        }

        private static readonly void** Vtable = CreateVtable();
        private readonly Action<uint, nint, uint> _callback;
        private NativeObject* _object;

        public EngineNotify(Action<uint, nint, uint> callback)
        {
            _callback = callback;
            _object = (NativeObject*)NativeMemory.AllocZeroed((nuint)sizeof(NativeObject));
            _object->Vtable = Vtable;
            _object->RefCount = 1;
            _object->Handle = GCHandle.ToIntPtr(GCHandle.Alloc(this));
        }

        public nint Pointer => (nint)_object;

        public void Dispose()
        {
            var obj = _object;
            _object = null;
            if (obj != null) ReleaseCore(obj);
        }

        private static void** CreateVtable()
        {
            var table = (void**)NativeMemory.Alloc((nuint)(4 * sizeof(void*))); // lives for the process
            table[0] = (delegate* unmanaged[Stdcall]<NativeObject*, Guid*, nint*, int>)&QueryInterfaceImpl;
            table[1] = (delegate* unmanaged[Stdcall]<NativeObject*, uint>)&AddRefImpl;
            table[2] = (delegate* unmanaged[Stdcall]<NativeObject*, uint>)&ReleaseImpl;
            table[3] = (delegate* unmanaged[Stdcall]<NativeObject*, uint, nuint, uint, int>)&EventNotifyImpl;
            return table;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
        private static int QueryInterfaceImpl(NativeObject* self, Guid* iid, nint* result)
        {
            if (*iid == Iid.IUnknown || *iid == Iid.MFMediaEngineNotify)
            {
                Interlocked.Increment(ref self->RefCount);
                *result = (nint)self;
                return Com.S_OK;
            }
            *result = 0;
            return Com.E_NOINTERFACE;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
        private static uint AddRefImpl(NativeObject* self) => (uint)Interlocked.Increment(ref self->RefCount);

        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
        private static uint ReleaseImpl(NativeObject* self) => ReleaseCore(self);

        private static uint ReleaseCore(NativeObject* self)
        {
            var count = Interlocked.Decrement(ref self->RefCount);
            if (count == 0)
            {
                GCHandle.FromIntPtr(self->Handle).Free();
                NativeMemory.Free(self);
            }
            return (uint)count;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
        private static int EventNotifyImpl(NativeObject* self, uint code, nuint param1, uint param2)
        {
            try
            {
                if (GCHandle.FromIntPtr(self->Handle).Target is EngineNotify target) target._callback(code, (nint)param1, param2);
            }
            catch
            {
                // An exception must never cross back into Media Foundation.
            }
            return Com.S_OK;
        }
    }
}
