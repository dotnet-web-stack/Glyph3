# Glyph3

[![Glyph3](https://img.shields.io/nuget/v/Glyph3.svg?label=Glyph3)](https://www.nuget.org/packages/Glyph3/)
![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-512bd4)

HTTP/3 without the QUIC: frame parsing, QPACK and request dispatch in pure managed C#, with no
native dependencies. Glyph3 does no I/O of its own: you hand it the bytes that arrived on a stream
and it hands back the bytes to send, so the transport underneath can be `System.Net.Quic`, io_uring,
or a pair of in-memory queues in a test.

```csharp
// Two methods is the whole transport contract; the rest have defaults.
sealed class MyTransport : IHttp3Transport
{
    public long OpenUniStream() => /* your QUIC connection */;
    public void Send(long streamId, ReadOnlySpan<byte> data, bool fin) => /* ... */;
}

var h3 = new Http3Connection(new MyTransport(), request =>
    request.Path.Span.SequenceEqual("/"u8)
        ? Http3Response.Text("hello\n")
        : Http3Response.Text("not found\n", status: 404));

h3.Start();                      // control stream + SETTINGS

h3.Feed(streamId, bytes, fin);   // whatever arrived, as many times as you like
h3.Flush();                      // dispatch whatever became complete
```

A working HTTP/3 server over `System.Net.Quic` is in
[`Playground/Glyph3.Playground.MsQuic`](Playground/Glyph3.Playground.MsQuic):

```sh
dotnet run -c Release --project Playground/Glyph3.Playground.MsQuic
curl --http3 -k https://127.0.0.1:8443/
```

## Licence

MIT.
