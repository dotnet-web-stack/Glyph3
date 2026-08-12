using System.Collections.Concurrent;
using System.Net;
using System.Net.Quic;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using System.Text;
using Glyph3;
using Glyph3.Playground.MsQuic;

//  The whole bridge is MsQuicHttp3Connection - read QUIC streams into Glyph3, write what comes
//  back. Everything above the transport is Glyph3's and would be identical over any other QUIC.

ushort port = ushort.TryParse(Environment.GetEnvironmentVariable("GLYPH3_PORT"), out ushort p) ? p : (ushort)8443;

if (!QuicListener.IsSupported)
{
    Console.Error.WriteLine(
        "QUIC is not available: libmsquic is missing, or TLS 1.3 is unavailable. "
        + "See https://learn.microsoft.com/dotnet/fundamentals/networking/quic/quic-overview");
    return 1;
}

// A browser refuses QUIC to a certificate it does not trust, and an origin carrying certificate
// errors has its Alt-Svc ignored outright, so a self-signed leaf cannot be used to test one. Point
// PLAYGROUND_CERT at a PKCS#12 issued by a CA the browser already trusts to do that.
using X509Certificate2 certificate =
    Environment.GetEnvironmentVariable("PLAYGROUND_CERT") is { Length: > 0 } certPath && File.Exists(certPath)
        ? X509CertificateLoader.LoadPkcs12FromFile(certPath, null)
        : DevCertificate.CreateSelfSigned("localhost");

int qpackCapacity = int.TryParse(Environment.GetEnvironmentVariable("GLYPH3_QPACK"), out int configured)
    ? configured
    : 4096;

var options = new Http3Options { QpackDynamicTableCapacity = qpackCapacity };

// Live connections, so the QPACK counters can be sampled while a browser is still attached.
var live = new ConcurrentDictionary<Http3Connection, byte>();

var listener = await QuicListener.ListenAsync(new QuicListenerOptions
{
    // IPv6Any, not Loopback: a browser resolves "localhost" itself and prefers ::1, so an IPv4-only
    // listener never receives a single packet from one. TCP hides this by falling back to IPv4;
    // QUIC has no such fallback, and the failure looks like a broken server rather than a bind.
    ListenEndPoint = new IPEndPoint(IPAddress.IPv6Any, port),
    ApplicationProtocols = [SslApplicationProtocol.Http3],
    
#pragma warning disable CA1416
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
#pragma warning restore CA1416
});

Console.WriteLine($"[glyph3-msquic] HTTP/3 on https://localhost:{port}/  (curl --http3 -k)");
Console.WriteLine($"[glyph3-msquic] QPACK dynamic table capacity {qpackCapacity} B");

_ = Task.Run(async () =>
{
    while (true)
    {
        await Task.Delay(2000);

        foreach (Http3Connection h3 in live.Keys)
        {
            Console.WriteLine(
                $"[qpack] requests {Volatile.Read(ref Program.Requests)} | "
                + $"peer advertised {h3.PeerDynamicTableCapacity} B | "
                + $"inbound inserts {h3.InboundDynamicInserts} | outbound inserts {h3.OutboundDynamicInserts}");
        }
    }
});

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };

try
{
    while (!stopping.IsCancellationRequested)
    {
        QuicConnection connection = await listener.AcceptConnectionAsync(stopping.Token);
        _ = ServeAsync(connection, stopping.Token, options, live);
    }
}
catch (OperationCanceledException)
{
    // Ctrl-C is how this ends.
}

await listener.DisposeAsync();
return 0;

static async Task ServeAsync(QuicConnection connection, CancellationToken cancellationToken,
    Http3Options options, ConcurrentDictionary<Http3Connection, byte> live)
{
    Http3Connection? tracked = null;

    try
    {
        await MsQuicHttp3Connection.ServeAsync(connection, Handle, cancellationToken, options, h3 =>
        {
            tracked = h3;
            live[h3] = 0;
        });
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"[glyph3-msquic] connection failed: {e.GetBaseException().Message}");
    }
    finally
    {
        if (tracked is not null)
        {
            live.TryRemove(tracked, out _);

            Console.WriteLine(
                $"[qpack] closed: peer advertised {tracked.PeerDynamicTableCapacity} B | "
                + $"inbound inserts {tracked.InboundDynamicInserts} | outbound inserts {tracked.OutboundDynamicInserts}");
        }
    }
}

// The handler. Requests are post-QPACK BYTES, so routing is a byte compare rather than a string
// allocation per request.
static Http3Response Handle(Http3Request request)
{
    Interlocked.Increment(ref Program.Requests);

    if (request.Path.Span.SequenceEqual("/"u8))
    {
        return Html("""
            <!doctype html><meta charset="utf-8"><title>Glyph3 QPACK</title>
            <h1>Glyph3 over MsQuic</h1>
            <p id="proto">measuring...</p>
            <div id="tiles"></div>
            <script>
              // Many sub-resources on one connection: repeated field values across requests are the
              // only thing a QPACK dynamic table can help with.
              const tiles = document.getElementById("tiles");
              for (let i = 0; i < 40; i++) {
                const img = document.createElement("img");
                img.src = "/tile?n=" + i;
                img.width = 24; img.height = 24;
                tiles.appendChild(img);
              }
              addEventListener("load", () => {
                const nav = performance.getEntriesByType("navigation")[0];
                document.getElementById("proto").textContent =
                  "negotiated protocol: " + (nav ? nav.nextHopProtocol : "unknown");
              });
            </script>
            """);
    }

    if (request.Path.Span.StartsWith("/tile"u8))
    {
        return Svg("""<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24"><rect width="24" height="24" fill="#3b82f6"/></svg>""");
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

static Http3Response Html(string body) => Typed(body, HtmlType);

static Http3Response Svg(string body) => Typed(body, SvgType);

static Http3Response Typed(string body, byte[] contentType)
{
    var response = new Http3Response { Status = 200, Body = Encoding.UTF8.GetBytes(body) };

    response.Headers.Add((ContentTypeName, contentType));

    return response;
}
partial class Program
{
    internal static int Requests;

    private static readonly byte[] ContentTypeName = "content-type"u8.ToArray();
    private static readonly byte[] HtmlType = "text/html; charset=utf-8"u8.ToArray();
    private static readonly byte[] SvgType = "image/svg+xml"u8.ToArray();
}
