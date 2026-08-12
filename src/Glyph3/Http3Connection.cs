using System.Buffers;

namespace Glyph3;

/// <summary>
/// HTTP/3 over any QUIC transport: frame parsing, QPACK and request dispatch.
/// </summary>
/// <remarks>
/// SETTINGS advertise the QPACK dynamic-table capacity from <see cref="Http3Options"/>, which
/// defaults to 0: at 0 the table is skipped entirely in both directions, and responses are encoded
/// with static-table references and literals only. Calls must be serialised by the host; body-read
/// wakes fire at the end of <see cref="Flush"/>, never inside a parse.
/// </remarks>
public sealed partial class Http3Connection
{
    private readonly IHttp3Transport _transport;
    private bool _fatal;
    private bool _streaming;
    private bool _controlSent;

    // Per-stream ingress state, keyed by stream id. Client bidi (id % 4 == 0) = request streams;
    // client uni (id % 4 == 2) = control / QPACK / push streams.
    private readonly Dictionary<long, ReqStream> _requests = new();
    private readonly Dictionary<long, UniStream> _unis = new();
    private readonly List<long> _ready = [];
    private readonly List<Http3BodyReader> _bodyWakes = [];

    private const long FrameData = 0x0;
    private const long FrameHeaders = 0x1;
    private const int MaxHeaderSection = 64 * 1024;

    private enum ParseState : byte
    {
        FrameHeader,     // accumulating the type+length varints
        HeadersPayload,  // accumulating an encoded field section
        DataPayload,     // streaming DATA payload through
        Skip,            // draining an unknown/greased frame
    }

    private sealed class ReqStream
    {
        public readonly Http3Request Request = new();
        public Http3BodyReader? Sink;

        public ParseState State = ParseState.FrameHeader;
        public readonly byte[] Carry = new byte[16];   // partial frame-header varints across chunks
        public int CarryLen;
        public long Remaining;                          // payload bytes left in the current frame

        public byte[]? HeadersBuf;                      // pooled accumulation for a HEADERS frame
        public int HeadersLen;

        public bool HeadersDone;                        // first HEADERS decoded (later ones = trailers)
        public bool Dispatched;                         // streaming: handler already started
        public bool Finished;                           // fin seen and processed
    }

    private sealed class UniStream
    {
        public bool TypeKnown;
        public bool IsControl;
        public readonly byte[] Carry = new byte[16];
        public int CarryLen;
        // Control-stream frame walk (SETTINGS and friends are parsed-and-ignored, but must be
        // framed correctly); non-control uni streams are drained wholesale.
        public ParseState State = ParseState.FrameHeader;
        public long Remaining;

        // SETTINGS payloads are accumulated so their values can be read; every other frame type is
        // still walked for framing and discarded.
        public bool CollectingSettings;
        public byte[]? SettingsBuf;
        public int SettingsLen;

        // The encoder stream carries instructions that can straddle reads, so a partial one is
        // held here rather than dropped.
        public bool IsEncoder;
        public bool IsPeerDecoder;
        public byte[]? Carry2;
        public int Carry2Len;
    }

    private const int MaxEncoderCarry = 4096;

    /// <summary>What the peer's SETTINGS said about QPACK. Both default to 0, which is also what a
    /// peer that sends no SETTINGS means.</summary>
    private long _peerTableCapacity;

    private long _peerBlockedStreams;

    private bool _peerSettingsReceived;

    // Built from the peer's encoder stream when a capacity is advertised; null keeps the
    // capacity-0 behaviour, where any dynamic reference is refused.
    private QpackDynamicTable? _decodeTable;

    private long _decoderStreamId = -1;

    private int _acknowledgedInserts;

    // The encoding side, created once the peer's SETTINGS say how much table it will hold. Null
    // means responses are encoded exactly as they were: static references and literals.
    private QpackEncoder? _encoder;

    private long _encoderStreamId = -1;

    private static Http3Options Validate(Http3Options? options)
    {
        Http3Options settled = options ?? Http3Options.Default;

        // Blocked references are refused rather than parked, so offering the peer the chance to
        // send one would just break connections.
        if (settled.QpackBlockedStreams != 0)
        {
            throw new NotSupportedException(
                "Blocked streams are not supported; leave QpackBlockedStreams at 0.");
        }

        return settled;
    }

    private static QpackDynamicTable? CreateDecodeTable(Http3Options options)
        => options.DynamicTableEnabled ? new QpackDynamicTable(options.QpackDynamicTableCapacity) : null;

    internal long PeerQpackCapacity() => _peerTableCapacity;

    internal long PeerQpackBlockedStreams() => _peerBlockedStreams;

    /// <summary>
    /// Whether the peer's SETTINGS have arrived yet.
    /// </summary>
    /// <remarks>
    /// Until they have, <see cref="PeerDynamicTableCapacity"/> reads 0 because nothing has been said
    /// - which is indistinguishable from a peer that genuinely advertised 0. Check this before
    /// concluding anything from a zero, since SETTINGS arrive on the control stream and a request
    /// can in principle be dispatched first.
    /// </remarks>
    public bool PeerSettingsReceived => _peerSettingsReceived;

    /// <summary>
    /// What the peer's SETTINGS advertised as its QPACK dynamic table capacity, in bytes.
    /// </summary>
    /// <remarks>
    /// This is the peer's DECODER limit, so it caps what this connection may insert when encoding
    /// responses. Zero - which most deployed clients send - means the dynamic table is unusable
    /// outbound no matter what capacity was configured locally.
    /// </remarks>
    public long PeerDynamicTableCapacity => _peerTableCapacity;

    /// <summary>
    /// Entries the peer has inserted into the table used to decode its requests. Stays 0 when the
    /// peer never uses the dynamic table, which is the common case.
    /// </summary>
    public int InboundDynamicInserts => _decodeTable?.InsertCount ?? 0;

    /// <summary>
    /// Entries inserted into the table used to encode responses. Stays 0 when the peer advertised
    /// no capacity, since there would be nowhere for it to store them.
    /// </summary>
    public int OutboundDynamicInserts => _encoder?.InsertCount ?? 0;

    private readonly Http3Options _options = Http3Options.Default;

    private Func<Http3Request, Http3Response>? _buffered;
    private Func<Http3Request, ValueTask<Http3Response>>? _streamingHandler;

    /// <summary>Buffered: the handler runs at end-of-stream with the whole body in
    /// <see cref="Http3Request.Body"/>.</summary>
    public Http3Connection(IHttp3Transport transport, Func<Http3Request, Http3Response> handler,
        Http3Options? options = null)
    {
        _transport = transport;
        _buffered = handler;
        _streaming = false;
        _options = Validate(options);
    }

    /// <summary>Streaming: the handler runs at end-of-headers and pulls the body through
    /// <see cref="Http3Request.BodyReader"/> as it arrives.</summary>
    public Http3Connection(IHttp3Transport transport, Func<Http3Request, ValueTask<Http3Response>> handler,
        Http3Options? options = null)
    {
        _transport = transport;
        _streamingHandler = handler;
        _streaming = true;
        _options = Validate(options);
        _decodeTable = CreateDecodeTable(_options);
    }

    /// <summary>True once the connection has failed and can only be torn down.</summary>
    public bool IsFaulted => _fatal;

    /// <summary>
    /// Open the control stream and send SETTINGS. Optional, since <see cref="Flush"/> retries it
    /// until it succeeds.
    /// </summary>
    public void Start()
    {
        if (!_controlSent && !_fatal)
        {
            SendControlStream();
        }
    }

    /// <summary>
    /// Feed bytes that arrived on a stream. Parses only; call <see cref="Flush"/> to dispatch.
    /// <paramref name="data"/> is borrowed for the call and may be reused on return.
    /// </summary>
    public void Feed(long streamId, ReadOnlySpan<byte> data, bool fin)
    {
        if (_fatal)
        {
            return;
        }

        // Unidirectional: control, QPACK encoder/decoder, push.
        if ((streamId & 0x3) == 0x2)
        {
            FeedUni(streamId, data, fin);
            return;
        }

        // Server-initiated ids never carry data from the peer.
        if ((streamId & 0x3) != 0x0)
        {
            return;
        }

        if (!_requests.TryGetValue(streamId, out ReqStream? rs))
        {
            if (fin && data.Length == 0)
            {
                return;   // empty stream, nothing to answer
            }
            rs = new ReqStream();
            rs.Request.StreamId = streamId;
            _requests[streamId] = rs;
        }

        FeedRequest(streamId, rs, data, fin);
    }

    /// <summary>A stream ended without data. Any half-parsed request on it is abandoned.</summary>
    public void OnStreamClosed(long streamId)
    {
        if (_requests.Remove(streamId, out ReqStream? dead))
        {
            dead.Sink?.End();
            ReleaseParseBuffers(dead);
        }
        _unis.Remove(streamId);
    }

    /// <summary>Wake parked body reads and dispatch what became complete. Call after a batch of
    /// <see cref="Feed"/> calls, or after each one.</summary>
    public void Flush()
    {
        if (_fatal)
        {
            return;
        }

        // Retried: a transport may not open uni streams before its handshake finishes.
        if (!_controlSent)
        {
            SendControlStream();
        }

        FireBodyWakes();

        if (_streaming)
        {
            DispatchReadyStreaming(_streamingHandler!);
        }
        else
        {
            DispatchReady(_buffered!);
        }
    }

    /// <summary>
    /// Tear down: abandon half-parsed requests and release parked body readers, so a handler
    /// awaiting a body that will never arrive is not left hanging.
    /// </summary>
    public void Close()
    {
        foreach (ReqStream rs in _requests.Values)
        {
            rs.Sink?.Drop();
            ReleaseParseBuffers(rs);
        }
        FireBodyWakes();
        _requests.Clear();
    }

    // Our control stream: stream type 0x00, then SETTINGS. With the dynamic table off - the
    // default - these advertise 0/0, which pins conforming peers to static references and literals.
    private void SendControlStream()
    {
        long ctrl = _transport.OpenUniStream();
        if (ctrl < 0)
        {
            return;   // pre-handshake wake: uni streams aren't openable yet - retry next pass
        }
        _controlSent = true;

        // The payload is built first so its length is measured rather than assumed. It was a
        // constant 4 while both values were single-byte zeros, which silently truncated the frame
        // the moment a real capacity was configured.
        Span<byte> payload = stackalloc byte[32];
        int p = 0;
        p += Varint.Write(payload[p..], 0x1);    // QPACK_MAX_TABLE_CAPACITY
        p += Varint.Write(payload[p..], _options.QpackDynamicTableCapacity);
        p += Varint.Write(payload[p..], 0x7);    // QPACK_BLOCKED_STREAMS
        p += Varint.Write(payload[p..], _options.QpackBlockedStreams);

        Span<byte> buf = stackalloc byte[48];
        int w = 0;
        w += Varint.Write(buf[w..], 0x00);       // stream type: control
        w += Varint.Write(buf[w..], 0x4);        // SETTINGS
        w += Varint.Write(buf[w..], p);          //   length
        payload[..p].CopyTo(buf[w..]);
        w += p;

        _transport.Send(ctrl, buf[..w], fin: false);

        OpenDecoderStream();
    }

    /// <summary>
    /// Our decoder stream, opened only when a table is advertised. Insert Count Increment travels
    /// on it, and with 0 blocked streams the peer cannot use its table until those arrive.
    /// </summary>
    private void OpenDecoderStream()
    {
        if (!_options.DynamicTableEnabled || _decoderStreamId >= 0)
        {
            return;
        }

        long stream = _transport.OpenUniStream();
        if (stream < 0)
        {
            return;   // retried on the next flush, like the control stream
        }

        _decoderStreamId = stream;

        Span<byte> buf = stackalloc byte[8];
        int w = Varint.Write(buf, 0x03);   // stream type: QPACK decoder
        _transport.Send(stream, buf[..w], fin: false);
    }

    /// <summary>
    /// Tell the peer how many insertions we have taken in. Without this a peer honouring 0 blocked
    /// streams never references anything and the table compresses nothing.
    /// </summary>
    private void AcknowledgeInserts()
    {
        if (_decodeTable is null || _decoderStreamId < 0)
        {
            return;
        }

        int increment = _decodeTable.InsertCount - _acknowledgedInserts;
        if (increment <= 0)
        {
            return;
        }

        _acknowledgedInserts = _decodeTable.InsertCount;

        Span<byte> buf = stackalloc byte[8];
        int w = QpackDecoderStream.WriteInsertCountIncrement(buf, increment);
        _transport.Send(_decoderStreamId, buf[..w], fin: false);
    }

    // --- ingress -------------------------------------------------------------------------------

    private void FeedRequest(long sid, ReqStream rs, ReadOnlySpan<byte> data, bool fin)
    {
        // Bytes the parser consumes that are NOT handed to the body sink (frame headers, HEADERS
        // payload, trailers, grease) credit the flow-control window immediately once the stream is
        // paced; sink bytes credit as the handler pulls them.
        int immediateCredit = 0;

        while (!data.IsEmpty && !_fatal)
        {
            switch (rs.State)
            {
                case ParseState.FrameHeader:
                {
                    int take = Math.Min(rs.Carry.Length - rs.CarryLen, data.Length);
                    data[..take].CopyTo(rs.Carry.AsSpan(rs.CarryLen));
                    int have = rs.CarryLen + take;

                    if (!Varint.TryRead(rs.Carry.AsSpan(0, have), out long type, out int c1) ||
                        !Varint.TryRead(rs.Carry.AsSpan(c1, have - c1), out long len, out int c2))
                    {
                        if (have == rs.Carry.Length)
                        {
                            Fatal("oversized frame header");
                            return;
                        }
                        rs.CarryLen = have;
                        if (rs.Sink is not null)
                        {
                            immediateCredit += take;
                        }
                        data = data[take..];
                        continue;
                    }

                    int headerBytes = c1 + c2;
                    int fromData = headerBytes - rs.CarryLen;   // header bytes consumed from THIS chunk
                    rs.CarryLen = 0;
                    if (rs.Sink is not null)
                    {
                        immediateCredit += fromData;
                    }
                    data = data[fromData..];

                    if (type == FrameData)
                    {
                        if (!rs.HeadersDone)
                        {
                            Fatal("DATA before HEADERS");
                            return;
                        }
                        rs.State = len == 0 ? ParseState.FrameHeader : ParseState.DataPayload;
                        rs.Remaining = len;
                    }
                    else if (type == FrameHeaders)
                    {
                        if (len > MaxHeaderSection)
                        {
                            Fatal("header section too large");
                            return;
                        }
                        rs.HeadersBuf = ArrayPool<byte>.Shared.Rent((int)len);
                        rs.HeadersLen = 0;
                        rs.State = ParseState.HeadersPayload;
                        rs.Remaining = len;
                        if (len == 0)
                        {
                            Fatal("empty HEADERS frame");
                            return;
                        }
                    }
                    else if (type is 0x3 or 0x4 or 0x5 or 0x7 or 0xD)
                    {
                        Fatal($"frame 0x{type:x} unexpected on a request stream");
                        return;
                    }
                    else
                    {
                        rs.State = len == 0 ? ParseState.FrameHeader : ParseState.Skip;   // grease
                        rs.Remaining = len;
                    }
                    break;
                }

                case ParseState.HeadersPayload:
                {
                    int take = (int)Math.Min(rs.Remaining, data.Length);
                    data[..take].CopyTo(rs.HeadersBuf.AsSpan(rs.HeadersLen));
                    rs.HeadersLen += take;
                    rs.Remaining -= take;
                    if (rs.Sink is not null)
                    {
                        immediateCredit += take;
                    }
                    data = data[take..];

                    if (rs.Remaining == 0)
                    {
                        OnHeadersComplete(sid, rs);
                        ArrayPool<byte>.Shared.Return(rs.HeadersBuf!);
                        rs.HeadersBuf = null;
                        rs.State = ParseState.FrameHeader;
                    }
                    break;
                }

                case ParseState.DataPayload:
                {
                    int take = (int)Math.Min(rs.Remaining, data.Length);
                    if (rs.Sink is not null)
                    {
                        rs.Sink.Push(data[..take]);   // credited on hand-out, not here
                    }
                    else
                    {
                        rs.Request.BodyBuffer ??= new MemoryStream();
                        rs.Request.BodyBuffer.Write(data[..take]);
                    }
                    rs.Remaining -= take;
                    data = data[take..];
                    if (rs.Remaining == 0)
                    {
                        rs.State = ParseState.FrameHeader;
                    }
                    break;
                }

                case ParseState.Skip:
                {
                    int take = (int)Math.Min(rs.Remaining, data.Length);
                    rs.Remaining -= take;
                    if (rs.Sink is not null)
                    {
                        immediateCredit += take;
                    }
                    data = data[take..];
                    if (rs.Remaining == 0)
                    {
                        rs.State = ParseState.FrameHeader;
                    }
                    break;
                }
            }
        }

        if (immediateCredit > 0)
        {
            _transport.ReleaseFlowControl(sid, immediateCredit);
        }

        if (fin && !_fatal && !rs.Finished)
        {
            if (rs.State != ParseState.FrameHeader || rs.CarryLen != 0)
            {
                Fatal("stream ended mid-frame");
                return;
            }
            rs.Finished = true;

            if (_streaming)
            {
                rs.Sink?.End();
            }
            else if (rs.HeadersDone)
            {
                _ready.Add(sid);
            }
        }
    }

    // First HEADERS frame = the request's field section; later ones are trailers (validated by the
    // frame walk, contents dropped - nothing in the surface carries them yet).
    private void OnHeadersComplete(long sid, ReqStream rs)
    {
        if (rs.HeadersDone)
        {
            return;
        }

        if (!Qpack.TryDecodeFieldSection(rs.HeadersBuf.AsSpan(0, rs.HeadersLen), rs.Request,
                _decodeTable, _options.QpackDynamicTableCapacity))
        {
            Fatal("malformed field section");
            return;
        }
        rs.HeadersDone = true;

        if (_streaming && !rs.Dispatched)
        {
            rs.Dispatched = true;
            var sink = new Http3BodyReader(this, sid, ended: false);
            rs.Sink = sink;
            rs.Request.BodyReader = sink;
            _transport.SetStreamPaced(sid, true);
            _ready.Add(sid);
        }
    }

    // Client uni streams: type varint, then control-stream frames (parsed, ignored) or a plain
    // drain for QPACK/push/greased types - with capacity 0 the QPACK streams carry nothing we need.
    private void FeedUni(long sid, ReadOnlySpan<byte> data, bool fin)
    {
        if (!_unis.TryGetValue(sid, out UniStream? us))
        {
            us = new UniStream();
            _unis[sid] = us;
        }

        if (!us.TypeKnown)
        {
            int take = Math.Min(us.Carry.Length - us.CarryLen, data.Length);
            data[..take].CopyTo(us.Carry.AsSpan(us.CarryLen));
            int have = us.CarryLen + take;
            if (!Varint.TryRead(us.Carry.AsSpan(0, have), out long type, out int consumed))
            {
                us.CarryLen = have;
                return;
            }
            us.TypeKnown = true;
            us.IsControl = type == 0x00;
            us.IsEncoder = type == 0x02 && _decodeTable is not null;
            us.IsPeerDecoder = type == 0x03 && _encoder is not null;
            int fromData = consumed - us.CarryLen;
            us.CarryLen = 0;
            data = data[fromData..];
        }

        if (us.IsEncoder)
        {
            // The peer's encoder stream builds our decode table.
            QpackEncoderStream.Result result =
                QpackEncoderStream.Apply(ref data, _decodeTable!, _options.QpackDynamicTableCapacity);

            if (result == QpackEncoderStream.Result.Error)
            {
                Fatal("malformed QPACK encoder instruction");
                return;
            }

            // Whatever did not complete is kept for the next read.
            us.Carry2 ??= new byte[MaxEncoderCarry];
            if (data.Length > MaxEncoderCarry)
            {
                Fatal("oversized QPACK encoder instruction");
                return;
            }
            data.CopyTo(us.Carry2);
            us.Carry2Len = data.Length;

            AcknowledgeInserts();
            return;
        }

        if (us.IsPeerDecoder)
        {
            // The peer telling us which of our insertions it holds.
            QpackDecoderStreamReader.Result ack = _encoder!.ApplyDecoderStream(ref data);

            if (ack == QpackDecoderStreamReader.Result.Error)
            {
                Fatal("malformed QPACK decoder instruction");
            }

            return;
        }

        if (!us.IsControl)
        {
            return;   // push, grease: drain (uni streams are never paced)
        }

        while (!data.IsEmpty && !_fatal)
        {
            if (us.State == ParseState.FrameHeader)
            {
                int take = Math.Min(us.Carry.Length - us.CarryLen, data.Length);
                data[..take].CopyTo(us.Carry.AsSpan(us.CarryLen));
                int have = us.CarryLen + take;
                if (!Varint.TryRead(us.Carry.AsSpan(0, have), out long frameType, out int c1) ||
                    !Varint.TryRead(us.Carry.AsSpan(c1, have - c1), out long len, out int c2))
                {
                    if (have == us.Carry.Length)
                    {
                        Fatal("oversized control frame header");
                        return;
                    }
                    us.CarryLen = have;
                    return;
                }
                int fromData = c1 + c2 - us.CarryLen;
                us.CarryLen = 0;
                data = data[fromData..];
                us.State = len == 0 ? ParseState.FrameHeader : ParseState.Skip;
                us.Remaining = len;

                // SETTINGS is the one control frame whose values matter: the peer's QPACK limits
                // decide whether responses may use a dynamic table at all. Everything else is
                // walked for framing and dropped.
                us.CollectingSettings = frameType == 0x4 && len is > 0 and <= MaxSettingsBytes;
                if (us.CollectingSettings)
                {
                    us.SettingsBuf ??= new byte[MaxSettingsBytes];
                    us.SettingsLen = 0;
                }
            }
            else
            {
                int take = (int)Math.Min(us.Remaining, data.Length);

                if (us.CollectingSettings)
                {
                    data[..take].CopyTo(us.SettingsBuf.AsSpan(us.SettingsLen));
                    us.SettingsLen += take;
                }

                us.Remaining -= take;
                data = data[take..];

                if (us.Remaining == 0)
                {
                    if (us.CollectingSettings)
                    {
                        ApplyPeerSettings(us.SettingsBuf.AsSpan(0, us.SettingsLen));
                        us.CollectingSettings = false;
                    }
                    us.State = ParseState.FrameHeader;
                }
            }
        }

        if (fin)
        {
            _unis.Remove(sid);
        }
    }

    /// <summary>
    /// Read the peer's SETTINGS: identifier/value varint pairs. Unknown identifiers are ignored, as
    /// the RFC requires - that is what makes the greased ones harmless.
    /// </summary>
    private void ApplyPeerSettings(ReadOnlySpan<byte> payload)
    {
        _peerSettingsReceived = true;

        while (!payload.IsEmpty)
        {
            if (!Varint.TryRead(payload, out long id, out int idLen))
            {
                return;
            }
            payload = payload[idLen..];

            if (!Varint.TryRead(payload, out long value, out int valueLen))
            {
                return;
            }
            payload = payload[valueLen..];

            switch (id)
            {
                case 0x1:   // QPACK_MAX_TABLE_CAPACITY
                    _peerTableCapacity = value;
                    StartEncoder();
                    break;

                case 0x7:   // QPACK_BLOCKED_STREAMS
                    _peerBlockedStreams = value;
                    break;
            }
        }
    }

    /// <summary>
    /// Open our encoder stream and start compressing responses, once the peer has said how much
    /// table it will hold. It cannot happen earlier: that capacity arrives in its SETTINGS.
    /// </summary>
    private void StartEncoder()
    {
        if (_encoder is not null || _peerTableCapacity <= 0 || _fatal)
        {
            return;
        }

        long stream = _transport.OpenUniStream();
        if (stream < 0)
        {
            return;   // no stream to be had; responses stay literal, which is always valid
        }

        int capacity = (int)Math.Min(_peerTableCapacity, MaxEncoderTableBytes);

        _encoderStreamId = stream;
        _encoder = new QpackEncoder(capacity);

        Span<byte> buf = stackalloc byte[16];
        int w = Varint.Write(buf, 0x02);                                  // stream type: QPACK encoder
        w += Qpack.WriteInt(buf[w..], 0x20, 5, capacity);                 // Set Dynamic Table Capacity
        _transport.Send(stream, buf[..w], fin: false);
    }

    /// <summary>
    /// Insert what this response repeats, so the next one can reference it. The instructions go out
    /// before the response itself, which is what gives the peer a chance to acknowledge them.
    /// </summary>
    private void OfferToEncoder(Http3Response response)
    {
        if (_encoder is null)
        {
            return;
        }

        Span<byte> instruction = stackalloc byte[512];

        foreach ((ReadOnlyMemory<byte> name, ReadOnlyMemory<byte> value) in response.Headers)
        {
            if (name.Length + value.Length + 32 > instruction.Length)
            {
                continue;   // too large to be worth a table slot
            }

            int written = _encoder.TryInsert(instruction, name.Span, value.Span);

            if (written > 0)
            {
                _transport.Send(_encoderStreamId, instruction[..written], fin: false);
            }
        }
    }

    /// <summary>A ceiling of our own, whatever the peer offers: its capacity is our memory.</summary>
    private const int MaxEncoderTableBytes = 4096;

    private const int MaxSettingsBytes = 256;

    // --- dispatch ------------------------------------------------------------------------------

    private void DispatchReady(Func<Http3Request, Http3Response> handler)
    {
        for (int i = 0; i < _ready.Count && !_fatal; i++)
        {
            if (!_requests.Remove(_ready[i], out ReqStream? rs))
            {
                continue;
            }
            rs.Request.Freeze();

            if (TryDispatchStreamedResponse(rs.Request))
            {
                continue;   // the writer owns this stream now
            }

            Http3Response resp;
            try
            {
                resp = handler(rs.Request);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[glyph3] request handler faulted: {e.GetBaseException().Message}");
                resp = new Http3Response { Status = 500 };
            }

            Submit(rs.Request.StreamId, resp);
        }
        _ready.Clear();
    }

    private void DispatchReadyStreaming(Func<Http3Request, ValueTask<Http3Response>> handler)
    {
        for (int i = 0; i < _ready.Count && !_fatal; i++)
        {
            long sid = _ready[i];
            if (!_requests.TryGetValue(sid, out ReqStream? rs))
            {
                continue;
            }
            rs.Request.Freeze();

            ValueTask<Http3Response> pending;
            try
            {
                pending = handler(rs.Request);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[glyph3] request handler faulted: {e.GetBaseException().Message}");
                Submit(sid, new Http3Response { Status = 500 });
                continue;
            }

            if (pending.IsCompletedSuccessfully)
            {
                Submit(sid, pending.Result);
            }
            else
            {
                _ = CompleteStreamingAsync(pending, sid);
            }
        }
        _ready.Clear();
    }

    private async Task CompleteStreamingAsync(ValueTask<Http3Response> pending, long streamId)
    {
        Http3Response resp;
        try
        {
            resp = await pending;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[glyph3] request handler faulted: {e.GetBaseException().Message}");
            resp = new Http3Response { Status = 500 };
        }

        if (_fatal)
        {
            return;
        }
        Submit(streamId, resp);
    }

    // --- egress --------------------------------------------------------------------------------

    // Encode and send one response: HEADERS frame (+ DATA frame header) in one SendStream call,
    // the body (with fin) in a second.
    private void Submit(long streamId, Http3Response resp)
    {
        if (resp.HeadIsValid)
        {
            SendSubmitted(streamId, resp, resp.EncodedHead!, resp.EncodedHeadLen);
            return;
        }

        OfferToEncoder(resp);
        byte[] fields = Qpack.EncodeResponseFields(resp, _encoder, out int fieldsLen);

        bool hasContentLength = false;
        foreach ((ReadOnlyMemory<byte> name, _) in resp.Headers)
        {
            hasContentLength |= name.Span.SequenceEqual("content-length"u8);
        }

        byte[] head = ArrayPool<byte>.Shared.Rent(fieldsLen + 64);
        int w = 0;

        if (!hasContentLength && resp.Body.Length > 0)
        {
            // content-length appended into the field section: re-encode is overkill, so it rides
            // as an extra literal at the end of the same section buffer.
            Span<byte> digits = stackalloc byte[20];
            System.Buffers.Text.Utf8Formatter.TryFormat(resp.Body.Length, digits, out int dlen);
            Span<byte> extra = stackalloc byte[32];
            int e = Qpack.WriteInt(extra, 0x50, 4, 4);          // literal w/ name ref: content-length (idx 4)
            e += Qpack.WriteInt(extra[e..], 0x00, 7, dlen);
            digits[..dlen].CopyTo(extra[e..]);
            e += dlen;

            w += Varint.Write(head.AsSpan(w), FrameHeaders);
            w += Varint.Write(head.AsSpan(w), fieldsLen + e);
            fields.AsSpan(0, fieldsLen).CopyTo(head.AsSpan(w));
            w += fieldsLen;
            extra[..e].CopyTo(head.AsSpan(w));
            w += e;
        }
        else
        {
            w += Varint.Write(head.AsSpan(w), FrameHeaders);
            w += Varint.Write(head.AsSpan(w), fieldsLen);
            fields.AsSpan(0, fieldsLen).CopyTo(head.AsSpan(w));
            w += fieldsLen;
        }
        ArrayPool<byte>.Shared.Return(fields);

        if (resp.Body.Length > 0)
        {
            w += Varint.Write(head.AsSpan(w), FrameData);
            w += Varint.Write(head.AsSpan(w), resp.Body.Length);
        }

        // Keep it: this exact byte sequence is what every later request with this response needs.
        // Not pooled - it outlives the call by design.
        resp.EncodedHead = head.AsSpan(0, w).ToArray();
        resp.EncodedHeadLen = w;
        resp.EncodedForStatus = resp.Status;
        resp.EncodedForHeaderCount = resp.Headers.Count;
        resp.EncodedForBodyLength = resp.Body.Length;
        ArrayPool<byte>.Shared.Return(head);

        SendSubmitted(streamId, resp, resp.EncodedHead, w);
    }

    // Small bodies ride WITH the head in one send. Two SendStream calls cost two trips through
    // the QUIC send path per response, which on a short response is a large share of the work;
    // one extra copy of a few hundred bytes is cheaper. Large bodies still go on their own,
    // because copying them would cost more than the second call saves.
    private const int InlineBodyLimit = 4 * 1024;

    private void SendSubmitted(long streamId, Http3Response resp, byte[] head, int headLen)
    {
        if (resp.Body.Length == 0)
        {
            _transport.Send(streamId, head.AsSpan(0, headLen), fin: true);
            return;
        }

        if (resp.Body.Length <= InlineBodyLimit)
        {
            int total = headLen + resp.Body.Length;
            byte[] one = ArrayPool<byte>.Shared.Rent(total);
            head.AsSpan(0, headLen).CopyTo(one);
            resp.Body.Span.CopyTo(one.AsSpan(headLen));
            _transport.Send(streamId, one.AsSpan(0, total), fin: true);
            ArrayPool<byte>.Shared.Return(one);
            return;
        }

        _transport.Send(streamId, head.AsSpan(0, headLen), fin: false);
        _transport.Send(streamId, resp.Body.Span, fin: true);
    }

    // --- streaming plumbing --------------------------------------------------------------------

    internal void CreditBody(long streamId, int bytes) => _transport.ReleaseFlowControl(streamId, bytes);

    internal void NoteBodyWake(Http3BodyReader sink)
    {
        if (!_bodyWakes.Contains(sink))
        {
            _bodyWakes.Add(sink);
        }
    }

    private void FireBodyWakes()
    {
        for (int i = 0; i < _bodyWakes.Count; i++)
        {
            _bodyWakes[i].FireIfReady();
        }
        _bodyWakes.Clear();
    }

    private void Fatal(string reason)
    {
        Console.Error.WriteLine($"[glyph3] protocol error: {reason}");
        _fatal = true;
    }

    private static void ReleaseParseBuffers(ReqStream rs)
    {
        if (rs.HeadersBuf is not null)
        {
            ArrayPool<byte>.Shared.Return(rs.HeadersBuf);
            rs.HeadersBuf = null;
        }
    }
}
