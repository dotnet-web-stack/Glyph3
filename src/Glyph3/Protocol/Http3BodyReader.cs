using System.Buffers;
using System.Threading.Tasks.Sources;

namespace Glyph3;

/// <summary>
/// Pull surface for a streaming request body. The connection pushes chunks in, the handler pulls
/// with <see cref="ReadAsync"/>, and an empty chunk means end of body.
/// </summary>
/// <remarks>
/// Each chunk is credited back to the peer's flow-control window only as it reaches the handler,
/// so a slow consumer stalls the peer instead of buffering. Single consumer; each
/// <see cref="ReadAsync"/> invalidates the previous chunk's memory.
/// </remarks>
public sealed class Http3BodyReader : IValueTaskSource<ReadOnlyMemory<byte>>
{
    private readonly Http3Connection _owner;
    private readonly long _streamId;

    private readonly Queue<(byte[] Buf, int Len)> _chunks = new();
    private (byte[]? Buf, int Len) _handedOut;
    private bool _ended;
    private bool _armed;

    private ManualResetValueTaskSourceCore<ReadOnlyMemory<byte>> _core = new()
    {
        RunContinuationsAsynchronously = false,
    };

    internal Http3BodyReader(Http3Connection owner, long streamId, bool ended)
    {
        _owner = owner;
        _streamId = streamId;
        _ended = ended;
    }

    /// <summary>
    /// Next body chunk; empty = end of body. The returned memory is valid until the next
    /// ReadAsync call (or handler return), whichever comes first.
    /// </summary>
    public ValueTask<ReadOnlyMemory<byte>> ReadAsync()
    {
        ReleaseHandedOut();

        if (_chunks.TryDequeue(out (byte[] Buf, int Len) chunk))
        {
            _handedOut = chunk;
            _owner.CreditBody(_streamId, chunk.Len);   // consumption opens the peer's window
            return new ValueTask<ReadOnlyMemory<byte>>(chunk.Buf.AsMemory(0, chunk.Len));
        }

        if (_ended)
        {
            return default;
        }

        _core.Reset();
        _armed = true;
        return new ValueTask<ReadOnlyMemory<byte>>(this, _core.Version);
    }

    // The span dies at return, so copy into a pooled buffer. Never completes the reader inline:
    // the wake is deferred to FireIfReady so a resumed handler cannot re-enter mid-parse.
    internal void Push(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }
        byte[] buf = ArrayPool<byte>.Shared.Rent(data.Length);
        data.CopyTo(buf);
        _chunks.Enqueue((buf, data.Length));
        if (_armed)
        {
            _owner.NoteBodyWake(this);
        }
    }

    // End of body: fin, stream reset, or connection teardown. Idempotent.
    internal void End()
    {
        if (_ended)
        {
            return;
        }
        _ended = true;
        if (_armed)
        {
            _owner.NoteBodyWake(this);
        }
    }

    // The deferred wake: complete a parked ReadAsync now the parse has unwound.
    internal void FireIfReady()
    {
        if (!_armed)
        {
            return;
        }
        if (_chunks.TryDequeue(out (byte[] Buf, int Len) chunk))
        {
            _armed = false;
            _handedOut = chunk;
            _owner.CreditBody(_streamId, chunk.Len);
            _core.SetResult(chunk.Buf.AsMemory(0, chunk.Len));
        }
        else if (_ended)
        {
            _armed = false;
            _core.SetResult(default);
        }
    }

    // Connection teardown while chunks may still be queued: recycle everything.
    internal void Drop()
    {
        End();
        ReleaseHandedOut();
        while (_chunks.TryDequeue(out (byte[] Buf, int Len) chunk))
        {
            ArrayPool<byte>.Shared.Return(chunk.Buf);
        }
    }

    private void ReleaseHandedOut()
    {
        if (_handedOut.Buf is not null)
        {
            ArrayPool<byte>.Shared.Return(_handedOut.Buf);
            _handedOut = (null, 0);
        }
    }

    // Strip the context-post so resumes stay inline on a single-threaded host.
    ReadOnlyMemory<byte> IValueTaskSource<ReadOnlyMemory<byte>>.GetResult(short token) => _core.GetResult(token);

    ValueTaskSourceStatus IValueTaskSource<ReadOnlyMemory<byte>>.GetStatus(short token) => _core.GetStatus(token);

    void IValueTaskSource<ReadOnlyMemory<byte>>.OnCompleted(
        Action<object?> continuation,
        object? state,
        short token,
        ValueTaskSourceOnCompletedFlags flags)
    {
        _core.OnCompleted(continuation, state, token,
            flags & ~ValueTaskSourceOnCompletedFlags.UseSchedulingContext);
    }
}
