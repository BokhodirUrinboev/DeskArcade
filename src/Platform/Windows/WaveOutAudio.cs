using System;
using System.Runtime.InteropServices;

namespace DeskArcade.Platform.Windows;

/// <summary>winmm waveOut with a small ring of reusable buffers (no dependencies, low latency).</summary>
public sealed class WaveOutAudio : IAudioOutput
{
    const int BufCount = 4;
    const int MaxSamples = 4096;

    readonly IntPtr _hwo;
    readonly IntPtr[] _headers = new IntPtr[BufCount];
    readonly IntPtr[] _data = new IntPtr[BufCount];
    readonly bool[] _queued = new bool[BufCount];
    readonly int _hdrSize = Marshal.SizeOf<Win32.WAVEHDR>();
    bool _disposed;

    WaveOutAudio(IntPtr hwo)
    {
        _hwo = hwo;
        for (int i = 0; i < BufCount; i++)
        {
            _headers[i] = Marshal.AllocHGlobal(_hdrSize);
            _data[i] = Marshal.AllocHGlobal(MaxSamples * 2);
        }
    }

    public static WaveOutAudio? TryOpen(int sampleRate)
    {
        var fmt = new Win32.WAVEFORMATEX
        {
            wFormatTag = 1, nChannels = 1, nSamplesPerSec = (uint)sampleRate, wBitsPerSample = 16,
            nBlockAlign = 2, nAvgBytesPerSec = (uint)sampleRate * 2,
        };
        return Win32.waveOutOpen(out var hwo, Win32.WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) == 0
            ? new WaveOutAudio(hwo)
            : null;
    }

    int FreeSlot()
    {
        for (int i = 0; i < BufCount; i++)
        {
            if (!_queued[i]) return i;
            var hdr = Marshal.PtrToStructure<Win32.WAVEHDR>(_headers[i]);
            if ((hdr.dwFlags & Win32.WHDR_DONE) != 0)
            {
                Win32.waveOutUnprepareHeader(_hwo, _headers[i], _hdrSize);
                _queued[i] = false;
                return i;
            }
        }
        return -1;
    }

    public bool CanWrite => !_disposed && FreeSlot() >= 0;

    public void Write(short[] samples)
    {
        int i = FreeSlot();
        if (_disposed || i < 0) return;
        int count = Math.Min(samples.Length, MaxSamples);
        Marshal.Copy(samples, 0, _data[i], count);
        var hdr = new Win32.WAVEHDR { lpData = _data[i], dwBufferLength = (uint)(count * 2) };
        Marshal.StructureToPtr(hdr, _headers[i], false);
        Win32.waveOutPrepareHeader(_hwo, _headers[i], _hdrSize);
        Win32.waveOutWrite(_hwo, _headers[i], _hdrSize);
        _queued[i] = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Win32.waveOutReset(_hwo);
        for (int i = 0; i < BufCount; i++)
        {
            Win32.waveOutUnprepareHeader(_hwo, _headers[i], _hdrSize);
            Marshal.FreeHGlobal(_headers[i]);
            Marshal.FreeHGlobal(_data[i]);
        }
        Win32.waveOutClose(_hwo);
    }
}
