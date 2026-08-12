using System.Buffers;

namespace Glyph3;

/// <summary>
/// The streamed-response half: headers go out first, then the body as DATA frames pushed through
/// an <see cref="Http3ResponseWriter"/>.
/// </summary>
public sealed partial class Http3Connection
{
    private readonly Stack<Http3ResponseWriter> _writerPool = new();
    private readonly List<TaskCompletionSource> _capacityWaiters = [];

    /// <summary>True once the connection can no longer make progress; a parked writer gives up.</summary>
    internal bool IsBroken => _fatal;

    /// <summary>
    /// Streamed responses: the handler writes its body through an
    /// <see cref="Http3ResponseWriter"/> and owns the stream until it completes it.
    /// </summary>
    public Http3Connection(IHttp3Transport transport, Func<Http3Request, Http3ResponseWriter, ValueTask> handler)
    {
        _transport = transport;

        // Its own dispatch: a streamed response has no Http3Response to return.
        _streamedResponseHandler = handler;
        _buffered = NoBufferedHandler;
        _streaming = false;
    }

    /// <summary>
    /// The transport can accept writes again. Only needed where
    /// <see cref="IHttp3Transport.CanSend"/> can be false.
    /// </summary>
    public void OnSendCapacityAvailable() => ReleaseCapacityWaiters();

    private Func<Http3Request, Http3ResponseWriter, ValueTask>? _streamedResponseHandler;

    // Never invoked: DispatchReady routes streamed requests to the writer path first.
    private static Http3Response NoBufferedHandler(Http3Request _)
        => throw new InvalidOperationException("A streamed connection dispatches through its writer.");

    /// <summary>Dispatch from the pass that made the request ready, so sends happen inside it.</summary>
    private bool TryDispatchStreamedResponse(Http3Request request)
    {
        if (_streamedResponseHandler is null)
        {
            return false;
        }

        Http3ResponseWriter writer = RentWriter(request.StreamId);
        _ = ServeAsync(_streamedResponseHandler, request, writer);
        return true;
    }

    private async Task ServeAsync(Func<Http3Request, Http3ResponseWriter, ValueTask> handler,
        Http3Request request, Http3ResponseWriter writer)
    {
        try
        {
            await handler(request, writer);
            await writer.CompleteAsync();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[glyph3] request handler faulted: {exception.GetBaseException().Message}");
            await writer.CompleteAsync();
        }
        finally
        {
            _writerPool.Push(writer);
        }
    }

    private Http3ResponseWriter RentWriter(long streamId)
    {
        if (_writerPool.TryPop(out Http3ResponseWriter? pooled))
        {
            pooled.Reset(streamId);
            return pooled;
        }
        return new Http3ResponseWriter(this, _transport, streamId);
    }

    /// <summary>Encode and send the HEADERS frame of a streamed response - no content-length.</summary>
    internal void SendStreamedHeaders(long streamId, Http3Response response)
    {
        byte[] fields = Qpack.EncodeResponseFields(response, out int fieldsLen);
        byte[] head = ArrayPool<byte>.Shared.Rent(fieldsLen + 16);

        int w = Varint.Write(head.AsSpan(), 0x1);           // HEADERS
        w += Varint.Write(head.AsSpan(w), fieldsLen);
        fields.AsSpan(0, fieldsLen).CopyTo(head.AsSpan(w));
        w += fieldsLen;

        _transport.Send(streamId, head.AsSpan(0, w), fin: false);

        ArrayPool<byte>.Shared.Return(fields);
        ArrayPool<byte>.Shared.Return(head);
    }

    /// <summary>Completes when the connection can accept more sends.</summary>
    internal Task WaitForSendCapacityAsync()
    {
        if (IsBroken)
        {
            return Task.CompletedTask;
        }

        var waiter = new TaskCompletionSource();
        _capacityWaiters.Add(waiter);
        return waiter.Task;
    }

    private void ReleaseCapacityWaiters()
    {
        if (_capacityWaiters.Count == 0)
        {
            return;
        }

        TaskCompletionSource[] waiting = _capacityWaiters.ToArray();
        _capacityWaiters.Clear();

        foreach (TaskCompletionSource waiter in waiting)
        {
            waiter.TrySetResult();
        }
    }
}
