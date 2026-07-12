using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Lightweight in-process HTTP server backed by <see cref="HttpListener"/>, matching the
/// MockHttpServer pattern already used in <c>BeeMemoryBank.Updater.Tests</c> (no mocking framework).
/// Captures the exact incoming request and lets each test respond with a scripted status/body,
/// so the real request shape (method/path/query/headers/body) of each DDNS provider can be asserted.
/// </summary>
internal static class DdnsTestPort
{
    public static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

internal sealed class CapturedRequest
{
    public string Method { get; set; } = "";
    public string RawUrl { get; set; } = "";
    public string Path { get; set; } = "";
    public string Query { get; set; } = "";
    public string Body { get; set; } = "";
    public NameValueCollection Headers { get; set; } = new();

    /// <summary>Parses the query string (without the leading '?') into a flat key/value map.</summary>
    public IReadOnlyDictionary<string, string> ParseQuery()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var q = Query.TrimStart('?');
        foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq < 0)
                dict[part] = "";
            else
                dict[part[..eq]] = part[(eq + 1)..];
        }
        return dict;
    }
}

internal sealed class ScriptedResponse
{
    public int StatusCode { get; set; } = 200;
    public string Body { get; set; } = "";
    public Dictionary<string, string>? Headers { get; set; }

    public static ScriptedResponse Ok(string body = "") => new() { StatusCode = 200, Body = body };

    public static ScriptedResponse Json(string json, int status = 200) => new()
    {
        StatusCode = status,
        Body = json,
        Headers = new() { ["Content-Type"] = "application/json" }
    };

    public static ScriptedResponse Status(int status, string body = "") => new() { StatusCode = status, Body = body };
}

internal sealed class DdnsMockHttpServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Thread _thread;
    private readonly Func<CapturedRequest, ScriptedResponse> _handler;
    private volatile bool _running;

    public DdnsMockHttpServer(int port, Func<CapturedRequest, ScriptedResponse> handler)
    {
        _handler = handler;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _thread = new Thread(ListenLoop) { IsBackground = true };
    }

    public void Start()
    {
        _listener.Start();
        _running = true;
        _thread.Start();
    }

    private void ListenLoop()
    {
        while (_running)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = _listener.GetContext();
            }
            catch
            {
                break; // listener stopped
            }

            try
            {
                Handle(ctx);
            }
            catch
            {
                try
                {
                    ctx.Response.StatusCode = 500;
                    ctx.Response.OutputStream.Close();
                }
                catch
                {
                    // best effort
                }
            }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var body = "";
        if (req.HasEntityBody)
        {
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
            body = reader.ReadToEnd();
        }

        // Snapshot headers so they remain readable after the connection is closed.
        var headers = new NameValueCollection();
        foreach (var key in req.Headers.AllKeys)
        {
            if (key != null)
                headers[key] = req.Headers[key];
        }

        var captured = new CapturedRequest
        {
            Method = req.HttpMethod,
            RawUrl = req.RawUrl ?? "",
            Path = req.Url?.AbsolutePath ?? "",
            Query = req.Url?.Query ?? "",
            Body = body,
            Headers = headers
        };

        var resp = _handler(captured);
        ctx.Response.StatusCode = resp.StatusCode;
        if (resp.Headers != null)
        {
            foreach (var kv in resp.Headers)
                ctx.Response.Headers[kv.Key] = kv.Value;
        }

        var bytes = Encoding.UTF8.GetBytes(resp.Body ?? "");
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }

    public void Dispose()
    {
        _running = false;
        try { _listener.Stop(); } catch { /* ignore */ }
        try { _listener.Close(); } catch { /* ignore */ }
    }
}
