using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace DikTap.Services.Audio;

// In-memory mono PCM16 WAV, in 64 KB blocks (below the LOH threshold, without reserving the maximum up front).
// Phase 1: Append(). Phase 2: Seal() and sequential/seekable reading to send it with StreamContent without copies.
internal sealed class WavMemoryStream : Stream
{
    public const int HeaderSize = 44;
    private const int ChunkSize = 64 * 1024;

    private readonly List<byte[]> _chunks = new();
    private readonly long _maxDataBytes;
    private byte[]? _header;
    private long _dataBytes;
    private long _position;
    private bool _disposed;
    private bool _hasNonZero;

    public int SampleRate { get; }
    public long DataBytes => _dataBytes;
    public bool HasNonZeroSamples => _hasNonZero;
    public bool IsSealed => _header is not null;
    public bool LimitReached { get; private set; }
    public TimeSpan Duration => TimeSpan.FromSeconds(_dataBytes / 2.0 / SampleRate);
    // Capacity actually reserved (may exceed DataBytes by up to one block).
    public long AllocatedBytes => (long)_chunks.Count * ChunkSize;

    public WavMemoryStream(int sampleRate, long maxTotalBytes, TimeSpan maxDuration)
    {
        if (sampleRate < 8000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        SampleRate = sampleRate;
        long byDuration = (long)(maxDuration.TotalSeconds * sampleRate) * 2;
        long bySize = maxTotalBytes - HeaderSize;
        long max = Math.Min(byDuration, bySize);
        _maxDataBytes = max - (max % 2);
    }

    // Appends samples; returns false if the limit was reached (those that do not fit are discarded).
    public bool Append(ReadOnlySpan<short> samples)
    {
        if (IsSealed) throw new InvalidOperationException("El WAV ya esta finalizado.");
        if (LimitReached) return false;

        long room = (_maxDataBytes - _dataBytes) / 2;
        if (samples.Length > room)
        {
            samples = samples[..(int)room];
            LimitReached = true;
        }

        var bytes = MemoryMarshal.AsBytes(samples);
        if (!_hasNonZero)
        {
            foreach (short s in samples) { if (s != 0) { _hasNonZero = true; break; } }
        }

        while (!bytes.IsEmpty)
        {
            int offsetInChunk = (int)(_dataBytes % ChunkSize);
            if (offsetInChunk == 0 && _dataBytes / ChunkSize == _chunks.Count)
                _chunks.Add(new byte[ChunkSize]);
            var chunk = _chunks[(int)(_dataBytes / ChunkSize)];
            int n = Math.Min(bytes.Length, ChunkSize - offsetInChunk);
            bytes[..n].CopyTo(chunk.AsSpan(offsetInChunk));
            _dataBytes += n;
            bytes = bytes[n..];
        }

        if (_dataBytes >= _maxDataBytes) LimitReached = true;
        return !LimitReached;
    }

    public void Seal()
    {
        if (IsSealed) return;
        var h = new byte[HeaderSize];
        var s = h.AsSpan();
        "RIFF"u8.CopyTo(s);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], (uint)(36 + _dataBytes));
        "WAVEfmt "u8.CopyTo(s[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(s[20..], 1);              // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(s[22..], 1);              // mono
        BinaryPrimitives.WriteUInt32LittleEndian(s[24..], (uint)SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(s[28..], (uint)SampleRate * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(s[32..], 2);              // block align
        BinaryPrimitives.WriteUInt16LittleEndian(s[34..], 16);             // bits
        "data"u8.CopyTo(s[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(s[40..], (uint)_dataBytes);
        _header = h;
        _position = 0;
    }

    public override bool CanRead => IsSealed && !_disposed;
    public override bool CanSeek => IsSealed && !_disposed;
    public override bool CanWrite => false;
    public override long Length => IsSealed ? HeaderSize + _dataBytes : throw new NotSupportedException();

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => Length + offset,
        };
        if (target < 0) throw new IOException("Posicion negativa.");
        _position = target;
        return _position;
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsSealed) throw new NotSupportedException();
        long total = Length;
        int copied = 0;
        while (copied < buffer.Length && _position < total)
        {
            int n;
            if (_position < HeaderSize)
            {
                n = (int)Math.Min(HeaderSize - _position, buffer.Length - copied);
                _header.AsSpan((int)_position, n).CopyTo(buffer[copied..]);
            }
            else
            {
                long d = _position - HeaderSize;
                var chunk = _chunks[(int)(d / ChunkSize)];
                int off = (int)(d % ChunkSize);
                n = (int)Math.Min(Math.Min(ChunkSize - off, total - _position), buffer.Length - copied);
                chunk.AsSpan(off, n).CopyTo(buffer[copied..]);
            }
            copied += n;
            _position += n;
        }
        return copied;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => cancellationToken.IsCancellationRequested ? ValueTask.FromCanceled<int>(cancellationToken) : ValueTask.FromResult(Read(buffer.Span));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            _chunks.Clear();
            _header = null;
        }
        base.Dispose(disposing);
    }
}
