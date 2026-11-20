using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Luma.Core.Exceptions;
using Luma.Core.Interfaces;
using Luma.Core.Logic;
using Luma.Infrastructure.Platform.Windows.Native;
using Microsoft.Extensions.Logging;

namespace Luma.Infrastructure.Platform.Windows.Video;

/// <summary>
/// Shrinks a video with Media Foundation: a Source Reader decodes and scales (advanced video processing, on the GPU through
/// a DXGI device manager when possible) and a Sink Writer encodes H.264 into MP4, using the GPU's hardware encoder when
/// there is one. Only the video stream is written; wallpapers are always silent. When the GPU path fails (old driver,
/// remote session) the same pipeline runs again in software.
/// Slot indices come from the Windows 10.0.26100 SDK headers (mfreadwrite.h, mfobjects.h).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsVideoOptimizer(ILogger<WindowsVideoOptimizer> logger) : IVideoOptimizer
{
    // IMFAttributes
    private const int GetUINT64 = 8, SetUINT32 = 21, SetUINT64 = 22, SetGUID = 24, SetUnknown = 27;
    // IMFSourceReader
    private const int SetStreamSelection = 4, GetCurrentMediaType = 6, SetCurrentMediaType = 7, ReadSample = 9;
    // IMFSinkWriter
    private const int AddStream = 3, SetInputMediaType = 4, BeginWriting = 5, WriteSample = 6, SendStreamTick = 7, FinalizeWriter = 11;
    // IMFSample
    private const int SetSampleTime = 36;

    private const uint FirstVideoStream = 0xFFFFFFFC, AllStreams = 0xFFFFFFFE;
    private const uint ReaderError = 0x1, EndOfStream = 0x2, StreamTick = 0x100;
    private const uint ProgressiveVideo = 2;

    private static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid MFVideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    private static readonly Guid MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING = new("0f81da2c-b537-4672-a8b2-a681b17307a3");
    private static readonly Guid MF_READWRITE_D3D_MANAGER = new("ec822da2-e1e9-4b29-a0d8-563c719f5269"); // source reader and sink writer
    private static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a91c-822b-41b9-a494-4de4643612b0");
    private static readonly Guid MF_TRANSCODE_CONTAINERTYPE = new("150ff23f-4abc-478b-ac4f-e1916fba1cca");
    private static readonly Guid MFTranscodeContainerType_MPEG4 = new("dc6cd05d-b9d0-40ef-bd35-fa622c1ab28a");

    public bool IsSupported => MediaEngineSession.EnsureMediaFoundation() is null;

    public Task ShrinkAsync(string inputPath, string outputPath, int width, int height, TimeSpan duration, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        if (MediaEngineSession.EnsureMediaFoundation() is { } problem) throw new VideoNotPlayableException(problem);
        if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0) throw new ArgumentException("The size must be positive and even.");

        var started = DateTime.UtcNow;
        try
        {
            try
            {
                Encode(inputPath, outputPath, width, height, duration, useGpu: true, progress, cancellationToken);
            }
            catch (COMException ex)
            {
                logger.LogWarning(ex, "Re-encoding on the graphics card failed; retrying in software");
                TryDelete(outputPath);
                progress?.Report(0);
                Encode(inputPath, outputPath, width, height, duration, useGpu: false, progress, cancellationToken);
            }
            logger.LogInformation("Re-encoded {Input} at {Width}x{Height} in {Seconds:N1}s ({Bytes:N0} bytes)",
                Path.GetFileName(inputPath), width, height, (DateTime.UtcNow - started).TotalSeconds, new FileInfo(outputPath).Length);
        }
        catch (COMException ex)
        {
            TryDelete(outputPath);
            throw new VideoNotPlayableException($"Windows couldn't re-encode the video (0x{ex.HResult:X8}).", ex);
        }
        catch
        {
            TryDelete(outputPath);
            throw;
        }
    }, cancellationToken);

    private static void Encode(string inputPath, string outputPath, int width, int height, TimeSpan duration, bool useGpu,
        IProgress<double>? progress, CancellationToken ct)
    {
        nint device = 0, context = 0, multithread = 0, manager = 0;
        nint readerAttributes = 0, reader = 0, requested = 0, decoded = 0;
        nint writerAttributes = 0, writer = 0, encoded = 0;
        try
        {
            if (useGpu)
            {
                Com.Check(NativeMethods.D3D11CreateDevice(0, NativeMethods.D3D_DRIVER_TYPE_HARDWARE, 0,
                    NativeMethods.D3D11_CREATE_DEVICE_BGRA_SUPPORT | NativeMethods.D3D11_CREATE_DEVICE_VIDEO_SUPPORT, 0, 0,
                    NativeMethods.D3D11_SDK_VERSION, out device, out _, out context), "Creating the Direct3D device");
                Com.Check(Com.QueryInterface(device, Iid.D3D11Multithread, out multithread), "Querying ID3D11Multithread");
                ((delegate* unmanaged[Stdcall]<nint, int, int>)Com.Slot(multithread, 5))(multithread, 1); // SetMultithreadProtected(TRUE)
                Com.Check(NativeMethods.MFCreateDXGIDeviceManager(out var token, out manager), "MFCreateDXGIDeviceManager");
                Com.Check(((delegate* unmanaged[Stdcall]<nint, nint, uint, int>)Com.Slot(manager, 7))(manager, device, token), "ResetDevice");
            }

            // ----- reader: decode + scale to NV12 at the target size -----
            Com.Check(NativeMethods.MFCreateAttributes(out readerAttributes, 3), "MFCreateAttributes");
            SetUInt32(readerAttributes, MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, 1);
            if (useGpu)
            {
                SetUInt32(readerAttributes, MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
                SetUnknownValue(readerAttributes, MF_READWRITE_D3D_MANAGER, manager);
            }
            Com.Check(NativeMethods.MFCreateSourceReaderFromURL(inputPath, readerAttributes, out reader), "Opening the video");
            Com.Check(((delegate* unmanaged[Stdcall]<nint, uint, int, int>)Com.Slot(reader, SetStreamSelection))(reader, AllStreams, 0), "SetStreamSelection");
            Com.Check(((delegate* unmanaged[Stdcall]<nint, uint, int, int>)Com.Slot(reader, SetStreamSelection))(reader, FirstVideoStream, 1), "SetStreamSelection");

            Com.Check(NativeMethods.MFCreateMediaType(out requested), "MFCreateMediaType");
            SetGuidValue(requested, MfGuids.MF_MT_MAJOR_TYPE, MfGuids.MFMediaType_Video);
            SetGuidValue(requested, MfGuids.MF_MT_SUBTYPE, MFVideoFormat_NV12);
            SetUInt64(requested, MF_MT_FRAME_SIZE, Pack((uint)width, (uint)height));
            SetUInt64(requested, MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
            SetUInt32(requested, MF_MT_INTERLACE_MODE, ProgressiveVideo);
            Com.Check(((delegate* unmanaged[Stdcall]<nint, uint, uint*, nint, int>)Com.Slot(reader, SetCurrentMediaType))(reader, FirstVideoStream, null, requested),
                "Choosing the decoded format");
            Com.Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Com.Slot(reader, GetCurrentMediaType))(reader, FirstVideoStream, &decoded), "GetCurrentMediaType");
            if (GetUInt64(decoded, MF_MT_FRAME_SIZE) != Pack((uint)width, (uint)height))
                throw new COMException("The video couldn't be scaled to the requested size.", unchecked((int)0xC00D36B4) /* MF_E_INVALIDMEDIATYPE */);

            var rate = GetUInt64(decoded, MF_MT_FRAME_RATE);
            uint rateNumerator = (uint)(rate >> 32), rateDenominator = (uint)rate;
            if (rateNumerator == 0 || rateDenominator == 0) (rateNumerator, rateDenominator) = (30, 1);

            // ----- writer: H.264 in MP4 -----
            Com.Check(NativeMethods.MFCreateAttributes(out writerAttributes, 3), "MFCreateAttributes");
            SetUInt32(writerAttributes, MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
            SetGuidValue(writerAttributes, MF_TRANSCODE_CONTAINERTYPE, MFTranscodeContainerType_MPEG4);
            if (useGpu) SetUnknownValue(writerAttributes, MF_READWRITE_D3D_MANAGER, manager);
            Com.Check(NativeMethods.MFCreateSinkWriterFromURL(outputPath, 0, writerAttributes, out writer), "Creating the output file");

            Com.Check(NativeMethods.MFCreateMediaType(out encoded), "MFCreateMediaType");
            SetGuidValue(encoded, MfGuids.MF_MT_MAJOR_TYPE, MfGuids.MFMediaType_Video);
            SetGuidValue(encoded, MfGuids.MF_MT_SUBTYPE, MFVideoFormat_H264);
            SetUInt32(encoded, MF_MT_AVG_BITRATE, VideoShrinkPolicy.Bitrate(width, height, (double)rateNumerator / rateDenominator));
            SetUInt32(encoded, MF_MT_INTERLACE_MODE, ProgressiveVideo);
            SetUInt64(encoded, MF_MT_FRAME_SIZE, Pack((uint)width, (uint)height));
            SetUInt64(encoded, MF_MT_FRAME_RATE, Pack(rateNumerator, rateDenominator));
            SetUInt64(encoded, MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));

            uint stream;
            Com.Check(((delegate* unmanaged[Stdcall]<nint, nint, uint*, int>)Com.Slot(writer, AddStream))(writer, encoded, &stream), "Adding the video stream");
            Com.Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, nint, int>)Com.Slot(writer, SetInputMediaType))(writer, stream, decoded, 0),
                "Connecting the encoder");
            Com.Check(((delegate* unmanaged[Stdcall]<nint, int>)Com.Slot(writer, BeginWriting))(writer), "BeginWriting");

            // ----- copy every frame, starting the output at time zero -----
            long? start = null;
            var total = duration.Ticks; // Media Foundation times are 100 ns units, like TimeSpan ticks
            var lastReport = 0d;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                uint actualStream, flags;
                long timestamp;
                nint sample = 0;
                Com.Check(((delegate* unmanaged[Stdcall]<nint, uint, uint, uint*, uint*, long*, nint*, int>)Com.Slot(reader, ReadSample))(
                    reader, FirstVideoStream, 0, &actualStream, &flags, &timestamp, &sample), "Reading a frame");
                try
                {
                    if ((flags & ReaderError) != 0) throw new COMException("The video couldn't be decoded.", unchecked((int)0xC00D36B9) /* MF_E_INVALID_STREAM_DATA */);
                    if (sample != 0)
                    {
                        start ??= timestamp;
                        Com.Check(((delegate* unmanaged[Stdcall]<nint, long, int>)Com.Slot(sample, SetSampleTime))(sample, timestamp - start.Value), "SetSampleTime");
                        Com.Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, int>)Com.Slot(writer, WriteSample))(writer, stream, sample), "Writing a frame");
                    }
                    else if ((flags & StreamTick) != 0 && start is not null)
                    {
                        ((delegate* unmanaged[Stdcall]<nint, uint, long, int>)Com.Slot(writer, SendStreamTick))(writer, stream, timestamp - start.Value);
                    }
                    if ((flags & EndOfStream) != 0) break;
                }
                finally
                {
                    Com.Release(sample);
                }

                if (progress is not null && total > 0 && start is not null)
                {
                    var fraction = Math.Clamp((double)(timestamp - start.Value) / total, 0, 0.99);
                    if (fraction - lastReport >= 0.01)
                    {
                        lastReport = fraction;
                        progress.Report(fraction);
                    }
                }
            }
            if (start is null) throw new COMException("The video has no frames.", unchecked((int)0xC00D36B9));
            Com.Check(((delegate* unmanaged[Stdcall]<nint, int>)Com.Slot(writer, FinalizeWriter))(writer), "Finishing the file");
            progress?.Report(1);
        }
        finally
        {
            // The writer closes its file on release (incomplete when Finalize didn't run; the caller deletes it).
            Com.Release(encoded);
            Com.Release(writer);
            Com.Release(writerAttributes);
            Com.Release(decoded);
            Com.Release(requested);
            Com.Release(reader);
            Com.Release(readerAttributes);
            Com.Release(manager);
            Com.Release(multithread);
            Com.Release(context);
            Com.Release(device);
        }
    }

    private static ulong Pack(uint high, uint low) => ((ulong)high << 32) | low;

    private static void SetUInt32(nint attributes, Guid key, uint value) =>
        Com.Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Com.Slot(attributes, SetUINT32))(attributes, &key, value), "SetUINT32");

    private static void SetUInt64(nint attributes, Guid key, ulong value) =>
        Com.Check(((delegate* unmanaged[Stdcall]<nint, Guid*, ulong, int>)Com.Slot(attributes, SetUINT64))(attributes, &key, value), "SetUINT64");

    private static void SetGuidValue(nint attributes, Guid key, Guid value) =>
        Com.Check(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Com.Slot(attributes, SetGUID))(attributes, &key, &value), "SetGUID");

    private static void SetUnknownValue(nint attributes, Guid key, nint value) =>
        Com.Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint, int>)Com.Slot(attributes, SetUnknown))(attributes, &key, value), "SetUnknown");

    private static ulong GetUInt64(nint attributes, Guid key)
    {
        ulong value;
        return ((delegate* unmanaged[Stdcall]<nint, Guid*, ulong*, int>)Com.Slot(attributes, GetUINT64))(attributes, &key, &value) >= 0 ? value : 0;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // left in the staging folder; the library's start-up cleanup removes it
        }
    }
}
