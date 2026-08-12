using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Text;
using Glyph3;
using Glyph3.Playground.MsQuic;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  An HTTP/3 server in pure managed C#: Glyph3 for HTTP/3, System.Net.Quic (MsQuic) for QUIC.
//  No native code of its own, and nothing io_uring-shaped - it runs wherever MsQuic does.
//
//      dotnet run -c Release --project Playground/Glyph3.Playground.MsQuic
//      curl --http3 -k https://127.0.0.1:8443/
//
//  QUIC needs libmsquic present. Windows ships it with the .NET runtime; on Linux install it
//  (`sudo apt install libmsquic`), and on macOS `brew install libmsquic` plus
//  DYLD_FALLBACK_LIBRARY_PATH=$(brew --prefix)/lib. QuicListener.IsSupported below is the check.
//
//  The whole bridge is MsQuicHttp3Connection - read QUIC streams into Glyph3, write what comes
//  back. Everything above the transport is Glyph3's and would be identical over any other QUIC.
// ─────────────────────────────────────────────────────────────────────────────────────────────

ushort port = ushort.TryParse(Environment.GetEnvironmentVariable("GLYPH3_PORT"), out ushort p) ? p : (ushort)8443;

if (!QuicListener.IsSupported)
{
    Console.Error.WriteLine(
        "QUIC is not available: libmsquic is missing, or TLS 1.3 is unavailable. "
        + "See https://learn.microsoft.com/dotnet/fundamentals/networking/quic/quic-overview");
    return 1;
}

using var certificate = DevCertificate.CreateSelfSigned("localhost");

var listener = await QuicListener.ListenAsync(new QuicListenerOptions
{
    ListenEndPoint = new IPEndPoint(IPAddress.Loopback, port),
    ApplicationProtocols = [SslApplicationProtocol.Http3],
    ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
    {
        DefaultStreamErrorCode = 0x010c,   // H3_REQUEST_CANCELLED
        DefaultCloseErrorCode = 0x0100,    // H3_NO_ERROR
        ServerAuthenticationOptions = new SslServerAuthenticationOptions
        {
            ApplicationProtocols = [SslApplicationProtocol.Http3],
            ServerCertificate = certificate,

            // Mutual TLS would go here - ClientCertificateRequired plus a validation callback -
            // and Glyph3 never sees it, because client authentication belongs to the transport.
        },
    }),
});

Console.WriteLine($"[glyph3-msquic] HTTP/3 on https://127.0.0.1:{port}/  (curl --http3 -k)");

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };

try
{
    while (!stopping.IsCancellationRequested)
    {
        QuicConnection connection = await listener.AcceptConnectionAsync(stopping.Token);
        _ = ServeAsync(connection, stopping.Token);
    }
}
catch (OperationCanceledException)
{
    // Ctrl-C is how this ends.
}

await listener.DisposeAsync();
return 0;

static async Task ServeAsync(QuicConnection connection, CancellationToken cancellationToken)
{
    try
    {
        await MsQuicHttp3Connection.ServeAsync(connection, Handle, cancellationToken);
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"[glyph3-msquic] connection failed: {e.GetBaseException().Message}");
    }
}

// The handler. Requests are post-QPACK BYTES, so routing is a byte compare rather than a string
// allocation per request.
static Http3Response Handle(Http3Request request)
{
    if (request.Path.Span.SequenceEqual("/"u8))
    {
        return Http3Response.Text("Hello from Glyph3 over MsQuic!\n");
    }

    if (request.Path.Span.SequenceEqual("/echo"u8))
    {
        return new Http3Response { Status = 200, Body = request.Body };
    }

    if (request.Path.Span.SequenceEqual("/who"u8))
    {
        return Http3Response.Text(
            $"{Encoding.ASCII.GetString(request.Method.Span)} "
            + $"{Encoding.ASCII.GetString(request.Path.Span)} "
            + $"({request.Headers.Count} headers, {request.Body.Length} body bytes)\n");
    }

    return Http3Response.Text("not found\n", status: 404);
}
