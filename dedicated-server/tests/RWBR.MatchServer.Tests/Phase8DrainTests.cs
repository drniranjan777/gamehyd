using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RWBR.MatchServer.Platform;
using Xunit;

namespace RWBR.MatchServer.Tests;

/// <summary>Rolling deploys: a managed server that is told to drain announces it and stops taking matches.</summary>
public class DrainTests
{
    /// <summary>Minimal stand-in for the platform API: records heartbeats, never assigns a match.</summary>
    private sealed class FakePlatform : IDisposable
    {
        private readonly HttpListener _listener = new();
        public ConcurrentQueue<JsonElement> Heartbeats { get; } = new();
        public Uri Url { get; }

        public FakePlatform()
        {
            int port = FreePort();
            Url = new Uri($"http://localhost:{port}/api/v1/");
            _listener.Prefixes.Add(Url.ToString());
            _listener.Start();
            _ = Task.Run(Serve);
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private async Task Serve()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch (Exception) { return; }
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    Heartbeats.Enqueue(JsonDocument.Parse(await reader.ReadToEndAsync()).RootElement.Clone());
                byte[] body = Encoding.UTF8.GetBytes("{\"serverId\":\"5b0f7a3e-0000-4000-8000-000000000001\",\"assignment\":null}");
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(body);
                ctx.Response.Close();
            }
        }

        public void Dispose() => _listener.Close();
    }

    [Fact]
    public async Task Draining_an_idle_server_reports_it_and_stops()
    {
        using var platform = new FakePlatform();
        using var api = new PlatformClient(platform.Url, new string('s', 40), "127.0.0.1:47999");
        var server = new ManagedServer(api, new ManagedOptions
        {
            Port = 0,
            Address = "127.0.0.1:47999",
            Region = "IN-SOUTH",
            TicketSecret = new byte[32],
            BuildVersion = "test",
            TickRate = 30,
            SnapshotEveryTicks = 1,
            HeartbeatInterval = TimeSpan.FromSeconds(30),
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task run = server.RunAsync(cts.Token);
        while (platform.Heartbeats.IsEmpty) await Task.Delay(20, cts.Token);

        server.Drain(); // wakes the 30 s idle wait at once
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        JsonElement[] beats = platform.Heartbeats.ToArray();
        Assert.False(beats[0].GetProperty("draining").GetBoolean());
        JsonElement last = beats[^1];
        Assert.True(last.GetProperty("draining").GetBoolean());
        Assert.Equal("IDLE", last.GetProperty("state").GetString());
        Assert.False(cts.IsCancellationRequested, "the server stopped on its own, not by cancellation");
    }
}
