# Glyph3

A transport-agnostic **HTTP/3** connection for .NET — frame parsing, QPACK and request dispatch in
pure managed C#, with no native dependencies of its own.

Glyph3 does no I/O. It takes stream bytes in and hands stream bytes back, so it runs over
`System.Net.Quic`, over io_uring, or over a pair of in-memory queues in a test. QUIC — handshake,
TLS, loss recovery, flow control — belongs to whatever you plug in underneath.

```csharp
var h3 = new Http3Connection(transport, request =>
    request.Path.Span.SequenceEqual("/"u8)
        ? Http3Response.Text("hello\n")
        : Http3Response.Text("not found\n", status: 404));

h3.Start();                                   // control stream + SETTINGS

h3.Feed(streamId, bytes, fin);                // whatever arrived
h3.Flush();                                   // dispatch what became complete
```

Requests are **post-QPACK bytes** throughout, so routing is a byte compare rather than a string
allocation per request.

## The transport interface

Two methods are required. The rest have defaults that are correct for a transport handling that
concern itself — over MsQuic you implement two and inherit three.

```csharp
public interface IHttp3Transport
{
    long OpenUniStream();
    void Send(long streamId, ReadOnlySpan<byte> data, bool fin);

    void ReleaseFlowControl(long streamId, int bytes) { }   // manual credit, where it exists
    bool CanSend => true;                                   // backpressure, where it exists
    void SetStreamPaced(long streamId, bool paced) { }      // advisory
}
```

Two rules the interface depends on:

**Buffers are borrowed, never retained.** `Send` gets a span Glyph3 may reuse the instant the call
returns. That is deliberate — it lets a host holding kernel-owned memory pass it straight through
instead of copying into something Glyph3 could keep.

**A connection is not thread-safe.** QPACK's table and the frame parsers span streams, so it is one
state machine and all calls must be serialised. A transport that reads streams concurrently funnels
them through one consumer; a single-threaded host pays nothing for a lock it does not need.

## Request and response bodies

Three shapes, chosen by which constructor you use:

| constructor | dispatch | body |
|---|---|---|
| `Func<Http3Request, Http3Response>` | end of stream | `request.Body`, whole |
| `Func<Http3Request, ValueTask<Http3Response>>` | **end of headers** | pulled through `request.BodyReader` while it arrives |
| `Func<Http3Request, Http3ResponseWriter, ValueTask>` | end of headers | response written incrementally through an `IBufferWriter<byte>` |

## Running the sample

```sh
dotnet run -c Release --project Playground/Glyph3.Playground.MsQuic
curl --http3 -k https://127.0.0.1:8443/
```

`Playground/Glyph3.Playground.MsQuic` is an HTTP/3 server over `System.Net.Quic` — the bridge is
about a hundred lines, and it is worth reading for the three impedance mismatches any transport
adapter hits: a single-threaded state machine fed from concurrent stream loops, a synchronous
`Send` against an async `WriteAsync`, and a synchronous `OpenUniStream` against an async
`OpenOutboundStreamAsync`.

QUIC needs libmsquic present:

| | |
|---|---|
| **Windows** | Windows 11 / Server 2022+. Ships with the .NET runtime, nothing to install |
| **Linux** | `sudo apt install libmsquic` (or `apk`/`dnf`/`zypper`), 2.2 or later |
| **macOS** | `brew install libmsquic`, plus `DYLD_FALLBACK_LIBRARY_PATH=$(brew --prefix)/lib` |

`QuicListener.IsSupported` is the runtime check; the sample fails with a pointer to the docs rather
than a `PlatformNotSupportedException`.

## Scope

Glyph3 advertises `QPACK_MAX_TABLE_CAPACITY = 0` and `QPACK_BLOCKED_STREAMS = 0` in SETTINGS, which
pins conforming peers to static-table references and literals — the decoder surface implemented
here. That covers what browsers and clients send in practice; it is a deliberate simplification,
not an oversight.

Server-side only for now.

## Licence

MIT.
