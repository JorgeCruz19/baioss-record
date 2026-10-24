using System.Runtime.InteropServices;
using Baioss.Record.Infrastructure.Review;

namespace Baioss.Record.App.Review;

/// <summary>Por dónde suena la revisión, para contárselo al operador.</summary>
public enum ReviewSound
{
    /// <summary>Por la salida de Windows por defecto (o la grabación no tiene sonido).</summary>
    Default,
    /// <summary>La salida por defecto es una DeckLink: suena por otra.</summary>
    OtherOutput,
    /// <summary>Las únicas salidas son de la DeckLink: sin sonido.</summary>
    OnlyDeckLink,
    /// <summary>No hay salida de audio utilizable: sin sonido.</summary>
    NoOutput,
}

/// <summary>El reproductor de la ventana «Revisar» y por dónde suena (<paramref name="Output"/>: la salida, si no es la de por
/// defecto).</summary>
public sealed record ReviewPlayback(ReviewPlayer? Player, ReviewSound Sound = ReviewSound.Default, string? Output = null);

/// <summary>
/// Salida de audio del reproductor de la revisión por el <c>waveOut</c> de Windows (winmm: sin paquetes nuevos): PCM de 16 bits
/// estéreo a 48 kHz en la salida de Windows por defecto, salvo que sea una DeckLink (<see cref="Open"/>). Unos pocos búferes de
/// 100 ms encolados (lo justo para que no se corte con la CPU ocupada); su posición —lo que ha sonado— es el reloj con el que el
/// reproductor enseña cada cuadro.
/// </summary>
internal sealed class WaveOutAudioOutput : IReviewAudioOutput
{
    private const int SampleRate = 48_000, Channels = 2, BytesPerSecond = SampleRate * Channels * 2;
    private const int BufferBytes = BytesPerSecond / 10; // 100 ms
    private const int BufferCount = 6;                   // como mucho ~0,6 s por delante
    private const uint CallbackEvent = 0x00050000, WhdrDone = 0x1, TimeBytes = 0x4;
    private const uint DrvQueryFunctionInstanceId = 0x0811, DrvQueryFunctionInstanceIdSize = 0x0812, MapperPreferredGet = 0x2015;
    private static readonly IntPtr WaveMapperHandle = new(0xFFFFFFFFL);
    private static readonly int HeaderSize = Marshal.SizeOf<WaveHeader>();

    private readonly object _lock = new();
    private readonly AutoResetEvent _done = new(false);
    private readonly IntPtr[] _headers = new IntPtr[BufferCount];
    private readonly IntPtr[] _data = new IntPtr[BufferCount];
    private readonly bool[] _queued = new bool[BufferCount];
    private IntPtr _device;
    private bool _disposed;

    private WaveOutAudioOutput(IntPtr device)
    {
        _device = device;
        for (int i = 0; i < BufferCount; i++)
        {
            _data[i] = Marshal.AllocHGlobal(BufferBytes);
            _headers[i] = Marshal.AllocHGlobal(HeaderSize);
            Marshal.StructureToPtr(new WaveHeader { Data = _data[i], BufferLength = BufferBytes }, _headers[i], false);
            waveOutPrepareHeader(_device, _headers[i], (uint)HeaderSize);
        }
    }

    /// <summary>
    /// La salida por la que suena la revisión (<see cref="ReviewAudioRoute.Pick"/>): la de Windows por defecto, salvo que sea una
    /// DeckLink —lo que sonara por ella saldría por la SDI de la tarjeta, que puede ir a un equipo de la emisión—; entonces la
    /// primera que no lo sea. Sin salida (solo DeckLinks, ninguna, o no abre), null: el reproductor va sin sonido, con su reloj.
    /// </summary>
    public static (WaveOutAudioOutput? Output, ReviewSound Sound, string? Name) Open()
    {
        var outputs = Outputs();
        var chosen = ReviewAudioRoute.Pick(outputs);
        if (chosen is null)
            return (null, outputs.Count > 0 && outputs.All(o => o.IsDeckLink) ? ReviewSound.OnlyDeckLink : ReviewSound.NoOutput, null);
        var output = TryOpen((uint)chosen.Id);
        if (output is null) return (null, ReviewSound.NoOutput, null);
        bool avoided = !chosen.IsDefault && outputs.Any(o => o.IsDefault && o.IsDeckLink);
        return (output, avoided ? ReviewSound.OtherOutput : ReviewSound.Default, chosen.Name);
    }

    /// <summary>Abre la salida <paramref name="deviceId"/> de waveOut, o null si no se puede.</summary>
    private static WaveOutAudioOutput? TryOpen(uint deviceId)
    {
        try
        {
            var format = new WaveFormat
            {
                FormatTag = 1, Channels = Channels, SamplesPerSec = SampleRate, AvgBytesPerSec = BytesPerSecond,
                BlockAlign = Channels * 2, BitsPerSample = 16,
            };
            var signal = new AutoResetEvent(false);
            if (waveOutOpen(out var device, deviceId, ref format, signal.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CallbackEvent) != 0)
            {
                signal.Dispose();
                return null;
            }
            var output = new WaveOutAudioOutput(device);
            output._signal = signal;
            return output;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    /// <summary>
    /// Las salidas de audio de Windows en el orden de waveOut, con su nombre entero, el de su adaptador y cuál es la de por defecto
    /// (por la API de dispositivos de Windows: el nombre de waveOut se corta en 31 letras y no dice el adaptador). Si esa API no
    /// responde, los nombres de waveOut y la salida preferida del mapeador.
    /// </summary>
    internal static IReadOnlyList<AudioOutputInfo> Outputs()
    {
        var list = new List<AudioOutputInfo>();
        int count;
        try { count = waveOutGetNumDevs(); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return list; }
        IMMDeviceEnumerator? devices = null;
        string? defaultEndpoint = null;
        try
        {
            devices = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            if (devices.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var device) == 0)
            {
                device.GetId(out defaultEndpoint);
                Marshal.ReleaseComObject(device);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException) { devices = null; }
        uint preferred = waveOutMessage(WaveMapperHandle, MapperPreferredGet, out uint id, out _) == 0 ? id : uint.MaxValue;
        try
        {
            for (uint i = 0; i < count; i++)
            {
                var caps = new WaveOutCaps();
                string name = waveOutGetDevCapsW((UIntPtr)i, ref caps, (uint)Marshal.SizeOf<WaveOutCaps>()) == 0 ? caps.Name ?? "" : "";
                string? endpoint = EndpointId(i), adapter = null;
                if (devices is not null && endpoint is not null) (name, adapter) = Names(devices, endpoint, name);
                bool isDefault = defaultEndpoint is not null && endpoint is not null
                    ? string.Equals(endpoint, defaultEndpoint, StringComparison.OrdinalIgnoreCase)
                    : i == preferred;
                list.Add(new AudioOutputInfo((int)i, name, adapter, isDefault));
            }
        }
        finally { if (devices is not null) Marshal.ReleaseComObject(devices); }
        return list;
    }

    /// <summary>El identificador de Windows (MMDevice) de la salida <paramref name="device"/> de waveOut, o null.</summary>
    private static string? EndpointId(uint device)
    {
        if (waveOutMessage((IntPtr)device, DrvQueryFunctionInstanceIdSize, out uint size, IntPtr.Zero) != 0 || size == 0) return null;
        var buffer = Marshal.AllocHGlobal((int)size);
        try { return waveOutMessage((IntPtr)device, DrvQueryFunctionInstanceId, buffer, (IntPtr)size) == 0 ? Marshal.PtrToStringUni(buffer) : null; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>El nombre entero de la salida y el de su adaptador («Blackmagic DeckLink Duo 2 (4) Audio», aunque la salida se
    /// haya renombrado).</summary>
    private static (string Name, string? Adapter) Names(IMMDeviceEnumerator devices, string endpoint, string fallback)
    {
        if (devices.GetDevice(endpoint, out var device) != 0) return (fallback, null);
        try
        {
            if (device.OpenPropertyStore(0 /* STGM_READ */, out var store) != 0) return (fallback, null);
            try { return (Read(store, FriendlyName) ?? fallback, Read(store, AdapterName)); }
            finally { Marshal.ReleaseComObject(store); }
        }
        catch (COMException) { return (fallback, null); }
        finally { Marshal.ReleaseComObject(device); }
    }

    private static string? Read(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var value) != 0) return null;
        try { return value.Type == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(value.Pointer) : null; }
        finally { PropVariantClear(ref value); }
    }

    private static readonly PropertyKey FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14); // PKEY_Device_FriendlyName
    private static readonly PropertyKey AdapterName = new(new Guid("026e516e-b814-414b-83cd-856d6fef4822"), 2);   // PKEY_DeviceInterface_FriendlyName

    private AutoResetEvent? _signal; // el que avisa winmm al terminar un búfer

    public TimeSpan Position
    {
        get
        {
            lock (_lock)
            {
                if (_disposed) return TimeSpan.Zero;
                var time = new MmTime { Type = TimeBytes };
                if (waveOutGetPosition(_device, ref time, (uint)Marshal.SizeOf<MmTime>()) != 0) return TimeSpan.Zero;
                return TimeSpan.FromSeconds(time.Value / (double)BytesPerSecond);
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            if (_disposed) return;
            waveOutReset(_device);   // devuelve todos los búferes y pone la posición a 0
            waveOutRestart(_device); // por si estaba en pausa
            Array.Clear(_queued);
        }
        _done.Set();
    }

    public void Write(byte[] buffer, int count, CancellationToken ct)
    {
        int offset = 0;
        while (count > 0)
        {
            int slot = WaitForFreeBuffer(ct);
            if (slot < 0) return;
            int n = Math.Min(count, BufferBytes);
            lock (_lock)
            {
                if (_disposed) return;
                Marshal.Copy(buffer, offset, _data[slot], n);
                var header = Marshal.PtrToStructure<WaveHeader>(_headers[slot]);
                header.BufferLength = (uint)n;
                header.Flags &= ~WhdrDone;
                Marshal.StructureToPtr(header, _headers[slot], false);
                _queued[slot] = true;
                waveOutWrite(_device, _headers[slot], (uint)HeaderSize);
            }
            offset += n;
            count -= n;
        }
    }

    /// <summary>Un búfer libre (que no se encoló o que ya sonó), esperando a que suene alguno si están todos encolados.</summary>
    private int WaitForFreeBuffer(CancellationToken ct)
    {
        while (true)
        {
            lock (_lock)
            {
                if (_disposed) return -1;
                for (int i = 0; i < BufferCount; i++)
                {
                    if (!_queued[i]) return i;
                    var flags = Marshal.PtrToStructure<WaveHeader>(_headers[i]).Flags;
                    if ((flags & WhdrDone) != 0) { _queued[i] = false; return i; }
                }
            }
            ct.ThrowIfCancellationRequested();
            WaitHandle.WaitAny(new WaitHandle[] { _signal!, _done, ct.WaitHandle }, 50);
        }
    }

    public void Pause() { lock (_lock) if (!_disposed) waveOutPause(_device); }

    public void Resume() { lock (_lock) if (!_disposed) waveOutRestart(_device); }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            waveOutReset(_device);
            for (int i = 0; i < BufferCount; i++)
            {
                waveOutUnprepareHeader(_device, _headers[i], (uint)HeaderSize);
                Marshal.FreeHGlobal(_headers[i]);
                Marshal.FreeHGlobal(_data[i]);
            }
            waveOutClose(_device);
            _device = IntPtr.Zero;
        }
        _done.Set();
        _signal?.Dispose();
        _done.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormat
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public IntPtr User;
        public uint Flags;
        public uint Loops;
        public IntPtr Next;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct MmTime
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(4)] public uint Value;
        [FieldOffset(4)] private ulong _padding; // la unión más grande del MMTIME (SMPTE: 8 bytes)
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WaveOutCaps
    {
        public ushort Mid, Pid;
        public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        public uint Formats;
        public ushort ChannelCount, Reserved;
        public uint Support;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PropertyKey(Guid FormatId, int PropertyId);

    /// <summary>PROPVARIANT: 24 bytes en 64 bits (la unión lleva estructuras de 16, como BLOB); con 16, GetValue escribía fuera.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
    [DllImport("winmm.dll")] private static extern int waveOutGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern int waveOutGetDevCapsW(UIntPtr deviceId, ref WaveOutCaps caps, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutMessage(IntPtr device, uint message, out uint value1, out uint value2);
    [DllImport("winmm.dll")] private static extern int waveOutMessage(IntPtr device, uint message, out uint value1, IntPtr value2);
    [DllImport("winmm.dll")] private static extern int waveOutMessage(IntPtr device, uint message, IntPtr value1, IntPtr value2);

    [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr device, uint deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr device);
    [DllImport("winmm.dll")] private static extern int waveOutPause(IntPtr device);
    [DllImport("winmm.dll")] private static extern int waveOutRestart(IntPtr device);
    [DllImport("winmm.dll")] private static extern int waveOutGetPosition(IntPtr device, ref MmTime time, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr device);
}
