using System.Runtime.InteropServices;
using Baioss.Record.Infrastructure.Review;

namespace Baioss.Record.App.Review;

/// <summary>
/// Salida de audio del reproductor de la revisión por el <c>waveOut</c> de Windows (winmm: sin paquetes nuevos): PCM de 16 bits
/// estéreo a 48 kHz en la tarjeta de sonido por defecto. Unos pocos búferes de 100 ms encolados (lo justo para que no se corte con
/// la CPU ocupada); su posición —lo que ha sonado— es el reloj con el que el reproductor enseña cada cuadro.
/// </summary>
internal sealed class WaveOutAudioOutput : IReviewAudioOutput
{
    private const int SampleRate = 48_000, Channels = 2, BytesPerSecond = SampleRate * Channels * 2;
    private const int BufferBytes = BytesPerSecond / 10; // 100 ms
    private const int BufferCount = 6;                   // como mucho ~0,6 s por delante
    private const uint WaveMapper = 0xFFFFFFFF, CallbackEvent = 0x00050000, WhdrDone = 0x1, TimeBytes = 0x4;
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

    /// <summary>La salida, o null si el equipo no tiene tarjeta de sonido utilizable (escritorio remoto sin audio…): entonces el
    /// reproductor va sin sonido, con su reloj.</summary>
    public static WaveOutAudioOutput? TryOpen()
    {
        try
        {
            var format = new WaveFormat
            {
                FormatTag = 1, Channels = Channels, SamplesPerSec = SampleRate, AvgBytesPerSec = BytesPerSecond,
                BlockAlign = Channels * 2, BitsPerSample = 16,
            };
            var signal = new AutoResetEvent(false);
            if (waveOutOpen(out var device, WaveMapper, ref format, signal.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CallbackEvent) != 0)
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
