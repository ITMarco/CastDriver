using System.Runtime.InteropServices;
using NAudio.Wave;

namespace CastDriver.Audio;

// Captures the audio of a single process (and its child processes) using the Windows
// process-loopback API (ActivateAudioInterfaceAsync with PROCESS_LOOPBACK). Requires
// Windows 10 build 20348+ / Windows 11. Delivers 48 kHz 16-bit stereo PCM.
public sealed class ProcessLoopbackCapture : ICaptureSource
{
    public WaveFormat WaveFormat { get; } = new WaveFormat(48000, 16, 2);
    public event EventHandler<WaveInEventArgs>? DataAvailable;

    private readonly uint _pid;
    private IAudioClient?        _client;
    private IAudioCaptureClient? _capture;
    private Thread?              _thread;
    private volatile bool        _running;

    private readonly ManualResetEventSlim _started = new(false);
    private Exception? _startError;

    public ProcessLoopbackCapture(uint processId) => _pid = processId;

    public void Start()
    {
        // All COM lives on one MTA thread: activation, init, and the read loop.
        _thread = new Thread(Run) { IsBackground = true, Name = "ProcessLoopback" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();

        if (!_started.Wait(6000))
            throw new TimeoutException("Process-loopback activation timed out.");
        if (_startError != null)
            throw _startError;
    }

    private void Run()
    {
        try
        {
            _client  = ActivateForProcess(_pid);
            InitializeClient(_client);

            var capIid = IID_IAudioCaptureClient;
            Marshal.ThrowExceptionForHR(_client.GetService(ref capIid, out var svc));
            _capture = (IAudioCaptureClient)svc;

            Marshal.ThrowExceptionForHR(_client.Start());
            _running = true;
            _started.Set();

            CaptureLoop();
        }
        catch (Exception ex)
        {
            _startError = ex;
            _started.Set();
        }
    }

    private void InitializeClient(IAudioClient client)
    {
        var fmt = new WaveFormatEx
        {
            wFormatTag      = 1,           // WAVE_FORMAT_PCM
            nChannels       = 2,
            nSamplesPerSec  = 48000,
            wBitsPerSample  = 16,
            nBlockAlign     = 4,
            nAvgBytesPerSec = 48000 * 4,
            cbSize          = 0,
        };
        var pFmt = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());
        try
        {
            Marshal.StructureToPtr(fmt, pFmt, false);
            const long bufferDuration = 2_000_000; // 200 ms in 100-ns units
            Marshal.ThrowExceptionForHR(client.Initialize(
                AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK,
                bufferDuration, 0, pFmt, IntPtr.Zero));
        }
        finally { Marshal.FreeHGlobal(pFmt); }
    }

    private void CaptureLoop()
    {
        const int blockAlign = 4;
        var buffer = new byte[blockAlign * 4800]; // ~100 ms scratch
        while (_running)
        {
            if (_capture!.GetNextPacketSize(out var packet) < 0) break;
            if (packet == 0) { Thread.Sleep(8); continue; }

            while (packet != 0 && _running)
            {
                if (_capture.GetBuffer(out var data, out var frames, out var flags, out _, out _) < 0) break;
                var bytes = (int)frames * blockAlign;
                if (frames > 0)
                {
                    if (buffer.Length < bytes) buffer = new byte[bytes];
                    if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0) Array.Clear(buffer, 0, bytes);
                    else                                           Marshal.Copy(data, buffer, 0, bytes);
                    DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, bytes));
                }
                _capture.ReleaseBuffer(frames);
                if (_capture.GetNextPacketSize(out packet) < 0) break;
            }
        }
    }

    public void Dispose()
    {
        _running = false;
        try { _thread?.Join(1000); } catch { }
        try { _client?.Stop(); } catch { }
        if (_capture != null) Marshal.ReleaseComObject(_capture);
        if (_client  != null) Marshal.ReleaseComObject(_client);
        _started.Dispose();
    }

    // ── Activation ─────────────────────────────────────────────────────────────

    private static IAudioClient ActivateForProcess(uint pid)
    {
        var prms = new AudioClientActivationParams
        {
            ActivationType      = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
            TargetProcessId     = pid,
            ProcessLoopbackMode = PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE,
        };
        var pBlob = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
        var pProp = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        try
        {
            Marshal.StructureToPtr(prms, pBlob, false);
            var pv = new PropVariantBlob
            {
                vt        = VT_BLOB,
                cbSize    = (uint)Marshal.SizeOf<AudioClientActivationParams>(),
                pBlobData = pBlob,
            };
            Marshal.StructureToPtr(pv, pProp, false);

            var iid = IID_IAudioClient;
            var handler = new ActivationHandler();
            ActivateAudioInterfaceAsync(ProcessLoopbackDevice, ref iid, pProp, handler, out _);

            if (!handler.Completed.Wait(5000))
                throw new TimeoutException("ActivateAudioInterfaceAsync did not complete.");

            Marshal.ThrowExceptionForHR(handler.Operation!.GetActivateResult(out var hr, out var obj));
            Marshal.ThrowExceptionForHR(hr);
            return (IAudioClient)obj;
        }
        finally
        {
            Marshal.FreeHGlobal(pBlob);
            Marshal.FreeHGlobal(pProp);
        }
    }

    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler
    {
        public readonly ManualResetEventSlim Completed = new(false);
        public IActivateAudioInterfaceAsyncOperation? Operation;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
        {
            Operation = op;
            Completed.Set();
        }
    }

    // ── Native / COM definitions ────────────────────────────────────────────────

    private const string ProcessLoopbackDevice = "VAD\\Process_Loopback";
    private const int    AUDCLNT_SHAREMODE_SHARED          = 0;
    private const uint   AUDCLNT_STREAMFLAGS_LOOPBACK      = 0x00020000;
    private const uint   AUDCLNT_BUFFERFLAGS_SILENT        = 0x2;
    private const int    AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1;
    private const int    PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE = 0;
    private const ushort VT_BLOB = 65;

    private static Guid IID_IAudioClient        = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [In] ref Guid riid,
        [In] IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams
    {
        public int  ActivationType;
        public uint TargetProcessId;
        public int  ProcessLoopbackMode;
    }

    // PROPVARIANT carrying a VT_BLOB (x64 layout).
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public uint   cbSize;
        public uint   padding;
        public IntPtr pBlobData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint   nSamplesPerSec;
        public uint   nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig]
        int GetActivateResult(out int activateResult,
            [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration,
                                     long hnsPeriodicity, IntPtr pFormat, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint numBufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService([In] ref Guid riid,
                                     [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint numFramesToRead, out uint flags,
                                    out long devicePosition, out long qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint numFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
    }
}
