using System;
using System.Runtime.InteropServices;

namespace DeskArcade.Platform.Linux;

/// <summary>PulseAudio "simple" API (also served by PipeWire on current Ubuntu). Writes block, which paces the mixer.</summary>
public sealed class PulseAudio : IAudioOutput
{
    const string Lib = "libpulse-simple.so.0";
    const int StreamPlayback = 1;
    const int SampleS16LE = 3;

    [StructLayout(LayoutKind.Sequential)]
    struct SampleSpec
    {
        public int Format;
        public uint Rate;
        public byte Channels;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BufferAttr
    {
        public uint MaxLength, TargetLength, PreBuffer, MinRequest, FragmentSize;
    }

    [DllImport(Lib)]
    static extern IntPtr pa_simple_new(string? server, string name, int direction, string? device, string streamName,
        ref SampleSpec spec, IntPtr channelMap, ref BufferAttr attr, out int error);

    [DllImport(Lib)] static extern int pa_simple_write(IntPtr stream, short[] data, UIntPtr bytes, out int error);
    [DllImport(Lib)] static extern void pa_simple_free(IntPtr stream);

    IntPtr _stream;

    PulseAudio(IntPtr stream) => _stream = stream;

    public static PulseAudio? TryOpen(int sampleRate)
    {
        try
        {
            var spec = new SampleSpec { Format = SampleS16LE, Rate = (uint)sampleRate, Channels = 1 };
            var attr = new BufferAttr
            {
                MaxLength = uint.MaxValue,
                TargetLength = (uint)(sampleRate * 2 * 40 / 1000), // ~40 ms
                PreBuffer = uint.MaxValue,
                MinRequest = uint.MaxValue,
                FragmentSize = uint.MaxValue,
            };
            IntPtr stream = pa_simple_new(null, "Desk Arcade", StreamPlayback, null, "effects", ref spec, IntPtr.Zero, ref attr, out _);
            return stream == IntPtr.Zero ? null : new PulseAudio(stream);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    public bool CanWrite => _stream != IntPtr.Zero;

    public void Write(short[] samples)
    {
        if (_stream != IntPtr.Zero)
            pa_simple_write(_stream, samples, (UIntPtr)(samples.Length * 2), out _);
    }

    public void Dispose()
    {
        if (_stream == IntPtr.Zero) return;
        pa_simple_free(_stream);
        _stream = IntPtr.Zero;
    }
}
