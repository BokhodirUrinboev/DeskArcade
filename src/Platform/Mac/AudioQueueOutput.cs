using System;
using System.Runtime.InteropServices;

namespace DeskArcade.Platform.Mac;

/// <summary>
/// AudioToolbox AudioQueue output. The queue asks for its buffers back on its own thread and they are refilled
/// from a ring that <see cref="Write"/> appends to, so, like waveOut, the mixer never blocks: it writes while
/// <see cref="CanWrite"/> says the ring is running short, and an underrun plays silence instead of stalling the queue.
/// </summary>
public sealed unsafe class AudioQueueOutput : IAudioOutput
{
    const string Lib = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    const uint FormatLinearPcm = 0x6C70636D; // 'lpcm'
    const uint FlagIsSignedInteger = 1 << 2;
    const uint FlagIsPacked = 1 << 3;        // no big-endian flag: native (little-endian) samples
    const int QueueBuffers = 3;
    const int BufferSamples = 512;           // per queue buffer, ~12 ms at 44.1 kHz
    const int RingSamples = 8192;
    const int LeadSamples = 1024;            // CanWrite keeps about this much mixed ahead of the queue

    [StructLayout(LayoutKind.Sequential)]
    struct AudioStreamBasicDescription
    {
        public double SampleRate;
        public uint FormatId, FormatFlags, BytesPerPacket, FramesPerPacket, BytesPerFrame, ChannelsPerFrame, BitsPerChannel, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct AudioQueueBuffer
    {
        public uint AudioDataBytesCapacity;
        public IntPtr AudioData;
        public uint AudioDataByteSize;
        public IntPtr UserData;
        public uint PacketDescriptionCapacity;
        public IntPtr PacketDescriptions;
        public uint PacketDescriptionCount;
    }

    [DllImport(Lib)]
    static extern int AudioQueueNewOutput(ref AudioStreamBasicDescription format, IntPtr callback, IntPtr userData,
        IntPtr callbackRunLoop, IntPtr callbackRunLoopMode, uint flags, out IntPtr queue);

    [DllImport(Lib)] static extern int AudioQueueAllocateBuffer(IntPtr queue, uint byteSize, out IntPtr buffer);
    [DllImport(Lib)] static extern int AudioQueueEnqueueBuffer(IntPtr queue, IntPtr buffer, uint packetDescriptionCount, IntPtr packetDescriptions);
    [DllImport(Lib)] static extern int AudioQueueStart(IntPtr queue, IntPtr startTime);
    [DllImport(Lib)] static extern int AudioQueueStop(IntPtr queue, byte immediate);
    [DllImport(Lib)] static extern int AudioQueueDispose(IntPtr queue, byte immediate);

    readonly short[] _ring = new short[RingSamples];
    readonly object _lock = new();
    int _read, _count;
    IntPtr _queue;
    GCHandle _self;
    volatile bool _disposed;

    AudioQueueOutput() => _self = GCHandle.Alloc(this);

    public static AudioQueueOutput? TryOpen(int sampleRate)
    {
        var output = new AudioQueueOutput();
        try
        {
            if (output.Start(sampleRate)) return output;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // no AudioToolbox: play silently
        }
        output.Dispose();
        return null;
    }

    bool Start(int sampleRate)
    {
        var format = new AudioStreamBasicDescription
        {
            SampleRate = sampleRate, FormatId = FormatLinearPcm, FormatFlags = FlagIsSignedInteger | FlagIsPacked,
            BytesPerPacket = 2, FramesPerPacket = 1, BytesPerFrame = 2, ChannelsPerFrame = 1, BitsPerChannel = 16,
        };
        // no run loop: the queue calls back on its own thread
        var callback = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnBufferPlayed;
        if (AudioQueueNewOutput(ref format, callback, GCHandle.ToIntPtr(_self), IntPtr.Zero, IntPtr.Zero, 0, out IntPtr queue) != 0)
            return false;
        _queue = queue;

        for (int i = 0; i < QueueBuffers; i++)
        {
            if (AudioQueueAllocateBuffer(_queue, BufferSamples * 2, out IntPtr buffer) != 0) return false;
            Fill(buffer); // prime with silence
            if (AudioQueueEnqueueBuffer(_queue, buffer, 0, IntPtr.Zero) != 0) return false;
        }
        return AudioQueueStart(_queue, IntPtr.Zero) == 0;
    }

    [UnmanagedCallersOnly]
    static void OnBufferPlayed(IntPtr userData, IntPtr queue, IntPtr buffer)
    {
        try
        {
            if (GCHandle.FromIntPtr(userData).Target is not AudioQueueOutput { _disposed: false } output) return;
            output.Fill(buffer);
            AudioQueueEnqueueBuffer(queue, buffer, 0, IntPtr.Zero);
        }
        catch
        {
            // an exception must never unwind into the audio thread
        }
    }

    void Fill(IntPtr bufferRef)
    {
        var buffer = (AudioQueueBuffer*)bufferRef;
        int capacity = (int)(buffer->AudioDataBytesCapacity / 2);
        var dest = new Span<short>((void*)buffer->AudioData, capacity);
        int filled = 0;
        lock (_lock)
        {
            while (filled < capacity && _count > 0)
            {
                int run = Math.Min(Math.Min(capacity - filled, _count), RingSamples - _read);
                _ring.AsSpan(_read, run).CopyTo(dest[filled..]);
                filled += run;
                _read = (_read + run) % RingSamples;
                _count -= run;
            }
        }
        dest[filled..].Clear(); // underrun: silence keeps the queue running
        buffer->AudioDataByteSize = (uint)(capacity * 2);
    }

    public bool CanWrite
    {
        get
        {
            if (_disposed) return false;
            lock (_lock) return _count < LeadSamples;
        }
    }

    public void Write(short[] samples)
    {
        if (_disposed) return;
        lock (_lock)
        {
            int count = Math.Min(samples.Length, RingSamples - _count); // a full ring drops the rest
            int write = (_read + _count) % RingSamples;
            for (int copied = 0; copied < count;)
            {
                int run = Math.Min(count - copied, RingSamples - write);
                samples.AsSpan(copied, run).CopyTo(_ring.AsSpan(write));
                copied += run;
                write = (write + run) % RingSamples;
            }
            _count += count;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_queue != IntPtr.Zero)
        {
            AudioQueueStop(_queue, 1);
            AudioQueueDispose(_queue, 1); // synchronous: frees the buffers, and no callback runs after it returns
            _queue = IntPtr.Zero;
        }
        if (_self.IsAllocated) _self.Free();
    }
}
