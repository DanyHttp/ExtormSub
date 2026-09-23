using System.Runtime.InteropServices;
using ExtormSub.Core.Audio;
using ExtormSub.Core.Settings;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace ExtormSub.Infrastructure.Audio;

/// <summary>
/// WASAPI shared-mode capture: loopback of a playback device (what the PC plays) or a microphone.
/// The DataAvailable handler runs on NAudio's capture thread and only forwards the span — no conversion,
/// no allocation.
/// </summary>
public sealed class WasapiSource : IAudioSource
{
    private readonly MMDevice _device;
    private readonly bool _loopback;
    private WasapiCapture? _capture;

    public WasapiSource(MMDevice device, bool loopback)
    {
        _device = device;
        _loopback = loopback;
        DeviceId = device.ID;
        DeviceName = device.FriendlyName;
    }

    public AudioFormat Format { get; private set; } = new(48000, 2, SampleEncoding.Float32);
    public string DeviceId { get; }
    public string DeviceName { get; }

    public event AudioDataHandler? DataAvailable;
    public event EventHandler<Exception?>? Stopped;

    public void Start()
    {
        if (_capture is not null) return;
        try
        {
            _capture = _loopback ? new WasapiLoopbackCapture(_device) : new WasapiCapture(_device, true, 50);
            Format = Map(_capture.WaveFormat);
            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += OnStopped;
            _capture.StartRecording();
        }
        catch (Exception ex) when (!_loopback && IsAccessDenied(ex))
        {
            Stop();
            throw new UnauthorizedAccessException(
                "Windows is blocking microphone access. Turn on Settings → Privacy & security → Microphone → “Let desktop apps access your microphone”.", ex);
        }
    }

    public void Stop()
    {
        var capture = _capture;
        if (capture is null) return;
        _capture = null;
        capture.DataAvailable -= OnData;
        capture.RecordingStopped -= OnStopped;
        try { capture.StopRecording(); } catch (Exception) { /* device may already be gone */ }
        capture.Dispose();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded > 0) DataAvailable?.Invoke(e.Buffer.AsSpan(0, e.BytesRecorded));
    }

    // Raised by NAudio when capture ends; with an exception when the device was invalidated/unplugged.
    private void OnStopped(object? sender, StoppedEventArgs e) => Stopped?.Invoke(this, e.Exception);

    private static bool IsAccessDenied(Exception ex) =>
        ex is UnauthorizedAccessException || (ex is COMException com && com.HResult == unchecked((int)0x80070005));

    internal static AudioFormat Map(WaveFormat f)
    {
        bool isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat
            || (f is WaveFormatExtensible ext && ext.SubFormat == KsDataFormatIeeeFloat);
        var encoding = isFloat ? SampleEncoding.Float32 : f.BitsPerSample switch
        {
            16 => SampleEncoding.Pcm16,
            24 => SampleEncoding.Pcm24,
            32 => SampleEncoding.Pcm32,
            _ => throw new NotSupportedException($"Unsupported device format: {f}"),
        };
        return new AudioFormat(f.SampleRate, f.Channels, encoding);
    }

    private static readonly Guid KsDataFormatIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    public void Dispose()
    {
        Stop();
        _device.Dispose();
    }
}

public sealed record AudioDevice(string Id, string Name, bool IsDefault);

/// <summary>
/// Enumerates playback and recording endpoints and reports device changes. COM notification callbacks
/// are forwarded to the thread pool — calling back into WASAPI from inside them can deadlock.
/// </summary>
public sealed class AudioDeviceService : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly NotificationClient _client;

    public AudioDeviceService()
    {
        _client = new NotificationClient(this);
        _enumerator.RegisterEndpointNotificationCallback(_client);
    }

    /// <summary>The Windows default device of this kind changed (new id).</summary>
    public event Action<AudioSourceKind, string?>? DefaultDeviceChanged;

    /// <summary>A device was unplugged, disabled or removed.</summary>
    public event Action<string>? DeviceLost;

    public IReadOnlyList<AudioDevice> ListDevices(AudioSourceKind kind)
    {
        string? defaultId = TryDefault(kind)?.ID;
        var result = new List<AudioDevice>();
        foreach (var d in _enumerator.EnumerateAudioEndPoints(Flow(kind), DeviceState.Active))
        {
            using (d) result.Add(new AudioDevice(d.ID, d.FriendlyName, d.ID == defaultId));
        }
        return result;
    }

    public string? DefaultDeviceName(AudioSourceKind kind)
    {
        using var d = TryDefault(kind);
        return d?.FriendlyName;
    }

    /// <summary>
    /// Opens a source for <paramref name="deviceId"/>, or the default device of that kind when null or missing.
    /// <paramref name="fellBack"/> is true when a specific device was requested but is unavailable.
    /// </summary>
    public IAudioSource CreateSource(AudioSourceKind kind, string? deviceId, out bool fellBack)
    {
        fellBack = false;
        bool loopback = kind == AudioSourceKind.SystemAudio;
        if (!string.IsNullOrEmpty(deviceId))
        {
            try
            {
                var device = _enumerator.GetDevice(deviceId);
                if (device.State == DeviceState.Active && device.DataFlow == Flow(kind)) return new WasapiSource(device, loopback);
                device.Dispose();
            }
            catch (Exception) { /* not found */ }
            fellBack = true;
        }
        var fallback = TryDefault(kind) ?? throw new NoAudioDeviceException(kind);
        return new WasapiSource(fallback, loopback);
    }

    private static DataFlow Flow(AudioSourceKind kind) => kind == AudioSourceKind.Microphone ? DataFlow.Capture : DataFlow.Render;

    // Loopback follows the multimedia default (where players render); microphones follow the console default.
    private static Role RoleFor(AudioSourceKind kind) => kind == AudioSourceKind.Microphone ? Role.Console : Role.Multimedia;

    private MMDevice? TryDefault(AudioSourceKind kind)
    {
        try
        {
            return _enumerator.HasDefaultAudioEndpoint(Flow(kind), RoleFor(kind))
                ? _enumerator.GetDefaultAudioEndpoint(Flow(kind), RoleFor(kind))
                : null;
        }
        catch (Exception) { return null; }
    }

    public void Dispose()
    {
        try { _enumerator.UnregisterEndpointNotificationCallback(_client); } catch (Exception) { }
        _enumerator.Dispose();
    }

    private sealed class NotificationClient(AudioDeviceService owner) : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            var kind = flow == DataFlow.Capture ? AudioSourceKind.Microphone : AudioSourceKind.SystemAudio;
            if (role == RoleFor(kind))
                ThreadPool.QueueUserWorkItem(_ => owner.DefaultDeviceChanged?.Invoke(kind, defaultDeviceId));
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            if (newState != DeviceState.Active)
                ThreadPool.QueueUserWorkItem(_ => owner.DeviceLost?.Invoke(deviceId));
        }

        public void OnDeviceRemoved(string deviceId) =>
            ThreadPool.QueueUserWorkItem(_ => owner.DeviceLost?.Invoke(deviceId));

        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}

public sealed class NoAudioDeviceException(AudioSourceKind kind) : Exception(kind == AudioSourceKind.Microphone
    ? "No microphone is available. Connect one and try again."
    : "No playback device is available. Connect speakers or headphones and try again.");
