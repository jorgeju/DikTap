using System.Runtime.InteropServices;
using DikTap.Interop;
using DikTap.Localization;
using static DikTap.Interop.CoreAudioConstants;

namespace DikTap.Services.Audio;

// Microphone capture with WASAPI in shared mode (capture endpoint, no loopback), event-driven.
// First requests 16 kHz mono PCM16 with native conversion by the audio engine (AUTOCONVERTPCM); if the device
// does not support it, uses the mix format and converts in our own code (PcmConverter).
internal sealed class WasapiRecorder : IAudioRecorder, IAudioDeviceCatalog
{
    private const int TargetSampleRate = 16000;

    public async Task<IRecordingSession> StartAsync(string? deviceId, CancellationToken cancellationToken)
    {
        var session = new WasapiSession(deviceId);
        try
        {
            using var reg = cancellationToken.Register(static s => ((WasapiSession)s!).Abort(), session);
            await session.StartAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public Task<IReadOnlyList<AudioDeviceInfo>> ListCaptureDevicesAsync() => Task.Run(ListCaptureDevices);

    private static IReadOnlyList<AudioDeviceInfo> ListCaptureDevices()
    {
        var result = new List<AudioDeviceInfo>();
        IMMDeviceEnumerator? en = null;
        IMMDeviceCollection? col = null;
        try
        {
            en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCoClass();
            if (en.EnumAudioEndpoints(eCapture, DEVICE_STATE_ACTIVE, out col) < 0) return result;
            col.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                IMMDevice? dev = null;
                IPropertyStore? props = null;
                try
                {
                    if (col.Item(i, out dev) < 0) continue;
                    if (dev.GetId(out string id) < 0) continue;
                    string name = id;
                    if (dev.OpenPropertyStore(STGM_READ, out props) >= 0)
                    {
                        var key = PKEY_Device_FriendlyName;
                        if (props.GetValue(ref key, out PROPVARIANT pv) >= 0)
                        {
                            if (pv.vt == VT_LPWSTR && pv.p != IntPtr.Zero)
                                name = Marshal.PtrToStringUni(pv.p) ?? id;
                            NativeMethods.PropVariantClear(ref pv);
                        }
                    }
                    result.Add(new AudioDeviceInfo(id, name));
                }
                finally
                {
                    Release(props);
                    Release(dev);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Logger.Error("Device enumeration failed", ex);
        }
        finally
        {
            Release(col);
            Release(en);
        }
        return result.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com);
    }

    internal static DictationException MapHResult(int hr, string step) => hr switch
    {
        E_ACCESSDENIED => new DictationException(Str.MicAccessDenied, ErrorKind.Audio),
        E_NOTFOUND => new DictationException(Str.MicNotFound, ErrorKind.Audio),
        AUDCLNT_E_DEVICE_INVALIDATED => new DictationException(Str.MicDisconnected, ErrorKind.Audio) { DeviceLost = true },
        AUDCLNT_E_DEVICE_IN_USE => new DictationException(Str.MicInUse, ErrorKind.Audio),
        AUDCLNT_E_SERVICE_NOT_RUNNING => new DictationException(Str.AudioServiceDown, ErrorKind.Audio),
        _ => new DictationException(Str.AudioError(hr, step), ErrorKind.Audio),
    };

    private sealed class WasapiSession : IRecordingSession
    {
        private readonly string? _deviceId;
        // The capture loop runs on a pool thread: creating a Thread per dictation retains ~3 handles until GC.
        private readonly ManualResetEventSlim _done = new(false);
        private readonly ManualResetEvent _stopEvent = new(false);
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<RecordingResult> _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _abort;
        private WavMemoryStream? _wav;
        private bool _wavHandedOut;
        private int _discontinuities;
        private volatile bool _startSignalled;
        private int _disposed;
        private int _peak;

        public event Action? LimitReached;
        public event Action? DeviceLost;
        public event Action<DictationException>? Faulted;

        public WasapiSession(string? deviceId) => _deviceId = deviceId;

        public Task StartAsync()
        {
            ThreadPool.UnsafeQueueUserWorkItem(static s => s.Run(), this, preferLocal: false);
            return _started.Task;
        }

        public async Task<RecordingResult> StopAsync()
        {
            _stopEvent.Set();
            var result = await _completed.Task.ConfigureAwait(false);
            _wavHandedOut = true;
            return result;
        }

        public void Abort()
        {
            _abort = true;
            try { _stopEvent.Set(); } catch (ObjectDisposedException) { }
        }

        public int TakePeak() => Interlocked.Exchange(ref _peak, 0);

        private void NotePeak(int peak)
        {
            int current;
            while (peak > (current = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, peak, current) != current) { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Abort();
            _done.Wait(TimeSpan.FromSeconds(3));
            _stopEvent.Dispose();
            if (!_wavHandedOut) _wav?.Dispose();
        }

        private void Run()
        {
            var thread = Thread.CurrentThread;
            var oldPriority = thread.Priority;
            thread.Priority = ThreadPriority.AboveNormal;
            try
            {
                RunCore();
            }
            finally
            {
                thread.Priority = oldPriority;
                _done.Set();
            }
        }

        private void RunCore()
        {
            IMMDeviceEnumerator? enumerator = null;
            IMMDevice? device = null;
            IAudioClient? client = null;
            IAudioCaptureClient? capture = null;
            IntPtr requested = IntPtr.Zero, mix = IntPtr.Zero;
            bool clientStarted = false;
            using var audioEvent = new AutoResetEvent(false);

            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCoClass();
                device = OpenDevice(enumerator);

                client = Activate(device);
                requested = AllocWaveFormat(TargetSampleRate, 1, 16);
                const uint directFlags = AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
                int hr = client.Initialize(AUDCLNT_SHAREMODE_SHARED, directFlags, 0, 0, requested, IntPtr.Zero);
                AudioFormat format;
                if (hr == 0)
                {
                    format = new AudioFormat(SampleEncoding.Pcm, 1, TargetSampleRate, 16, 2);
                }
                else
                {
                    if (hr is E_ACCESSDENIED or AUDCLNT_E_DEVICE_INVALIDATED or AUDCLNT_E_SERVICE_NOT_RUNNING)
                        throw MapHResult(hr, "Initialize");
                    Logger.Info($"16 kHz mono format not available (0x{hr:X8}); using the mix format");
                    Release(client);
                    client = Activate(device);
                    Check(client.GetMixFormat(out mix), "GetMixFormat");
                    format = AudioFormat.FromNative(mix);
                    Check(client.Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_EVENTCALLBACK, 0, 0, mix, IntPtr.Zero), "Initialize");
                }

                var converter = new PcmConverter(format);
                _wav = new WavMemoryStream(converter.SampleRate, RecordingLimits.MaxWavBytes, RecordingLimits.MaxDuration);

                Check(client.SetEventHandle(audioEvent.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");
                var iid = IID_IAudioCaptureClient;
                Check(client.GetService(ref iid, out object svc), "GetService");
                capture = (IAudioCaptureClient)svc;

                Check(client.Start(), "Start");
                clientStarted = true;

                // "Iniciada" (Started) is announced when the first audio packet arrives (inside CaptureLoop), not when Start() is called.
                bool limit = CaptureLoop(capture, converter, audioEvent, format.ToString());
                if (limit) LimitReached?.Invoke();

                if (_abort)
                {
                    _completed.TrySetCanceled();
                }
                else
                {
                    _wav.Seal();
                    _completed.TrySetResult(new RecordingResult(_wav, limit, _wav.HasNonZeroSamples));
                }
            }
            catch (Exception ex)
            {
                var de = ex as DictationException
                    ?? (ex is COMException ce ? MapHResult(ce.HResult, "captura") : new DictationException(Str.CaptureUnexpected, ErrorKind.Audio, ex));
                Logger.Error("Capture failed", ex);
                if (!_startSignalled) _started.TrySetException(de);
                else if (!_abort && de.DeviceLost && _wav is { DataBytes: > 0 })
                {
                    // What was already recorded is kept: it is delivered as a normal stop and the user is told the dictation is partial.
                    Logger.Info("Microphone lost during capture; keeping what was recorded so far");
                    _wav.Seal();
                    _completed.TrySetResult(new RecordingResult(_wav, false, _wav.HasNonZeroSamples, DeviceLost: true));
                    DeviceLost?.Invoke();
                    return;
                }
                else if (!_abort) Faulted?.Invoke(de);
                _completed.TrySetException(de);
            }
            finally
            {
                if (clientStarted && client is not null)
                {
                    try { client.Stop(); } catch (COMException) { }
                }
                Release(capture);
                Release(client);
                Release(device);
                Release(enumerator);
                if (requested != IntPtr.Zero) Marshal.FreeHGlobal(requested);
                if (mix != IntPtr.Zero) NativeMethods.CoTaskMemFree(mix);
                _started.TrySetCanceled();
                if (_discontinuities > 0) Logger.Info($"Audio discontinuities: {_discontinuities}");
            }
        }

        // Returns true if the recording limit was reached.
        private bool CaptureLoop(IAudioCaptureClient capture, PcmConverter converter, AutoResetEvent audioEvent, string formatText)
        {
            var handles = new WaitHandle[] { audioEvent, _stopEvent };
            var scratch = new short[4096];
            int idleTimeouts = 0;
            while (true)
            {
                int idx = WaitHandle.WaitAny(handles, 2000);
                if (idx == WaitHandle.WaitTimeout)
                {
                    // With no packet at all after Start(), fail right away instead of pretending to record.
                    if (++idleTimeouts >= (_startSignalled ? 3 : 1))
                        throw new DictationException(Str.MicSilent, ErrorKind.Audio) { DeviceLost = _startSignalled };
                    continue;
                }
                idleTimeouts = 0;
                bool stopping = idx == 1 || _stopEvent.WaitOne(0);
                if (_abort) return false;
                // A final drain after stopping avoids losing the end of the phrase.
                bool limitReached = Drain(capture, converter, scratch);
                if (!_startSignalled)
                {
                    _startSignalled = true;
                    Logger.Info($"Capture started: {formatText}");
                    _started.TrySetResult();
                }
                if (limitReached) return true;
                if (stopping) return false;
            }
        }

        private unsafe bool Drain(IAudioCaptureClient capture, PcmConverter converter, short[] scratch)
        {
            while (true)
            {
                Check(capture.GetNextPacketSize(out uint next), "GetNextPacketSize");
                if (next == 0) return false;

                int hr = capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _);
                if (hr == AUDCLNT_S_BUFFER_EMPTY) return false;
                Check(hr, "GetBuffer");

                bool limit = false;
                try
                {
                    bool silent = (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0;
                    if ((flags & AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) != 0) _discontinuities++;
                    var src = new ReadOnlySpan<byte>((void*)data, checked((int)frames * converter.FrameBytes));
                    int offset = 0, remaining = (int)frames;
                    while (remaining > 0 && !limit)
                    {
                        int n = Math.Min(remaining, scratch.Length);
                        if (silent) scratch.AsSpan(0, n).Clear();
                        else converter.Convert(src.Slice(offset * converter.FrameBytes), n, scratch);
                        NotePeak(AudioLevel.Peak(scratch.AsSpan(0, n)));
                        if (!_wav!.Append(scratch.AsSpan(0, n))) limit = true;
                        offset += n;
                        remaining -= n;
                    }
                }
                finally
                {
                    capture.ReleaseBuffer(frames);
                }
                if (limit) return true;
            }
        }

        private IMMDevice OpenDevice(IMMDeviceEnumerator enumerator)
        {
            IMMDevice? device;
            if (string.IsNullOrEmpty(_deviceId))
            {
                int hr = enumerator.GetDefaultAudioEndpoint(eCapture, eConsole, out device);
                if (hr < 0) throw hr == E_NOTFOUND
                    ? new DictationException(Str.NoMicAvailable, ErrorKind.Audio)
                    : MapHResult(hr, "GetDefaultAudioEndpoint");
            }
            else
            {
                int hr = enumerator.GetDevice(_deviceId, out device);
                bool active = false;
                if (hr >= 0 && device.GetState(out uint state) >= 0) active = state == DEVICE_STATE_ACTIVE;
                if (!active)
                {
                    Release(device);
                    throw new DictationException(Str.SelectedMicUnavailable, ErrorKind.Audio);
                }
            }
            return device;
        }

        private static IAudioClient Activate(IMMDevice device)
        {
            var iid = IID_IAudioClient;
            Check(device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object obj), "Activate");
            return (IAudioClient)obj;
        }

        private static void Check(int hr, string step)
        {
            if (hr < 0) throw MapHResult(hr, step);
        }

        // 18-byte WAVEFORMATEX (2-byte packed).
        private static IntPtr AllocWaveFormat(int rate, int channels, int bits)
        {
            IntPtr p = Marshal.AllocHGlobal(18);
            int blockAlign = channels * bits / 8;
            Marshal.WriteInt16(p, 0, 1);
            Marshal.WriteInt16(p, 2, (short)channels);
            Marshal.WriteInt32(p, 4, rate);
            Marshal.WriteInt32(p, 8, rate * blockAlign);
            Marshal.WriteInt16(p, 12, (short)blockAlign);
            Marshal.WriteInt16(p, 14, (short)bits);
            Marshal.WriteInt16(p, 16, 0);
            return p;
        }
    }
}
