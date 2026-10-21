using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using RWBR.Shared.Protocol;

namespace RWBR.MatchServer;

/// <summary>Measured run data for the load-test report (BRD §14: CPU, network, tick stability).</summary>
public sealed class HostReport
{
    public required CompletionReason? Reason { get; init; }
    public required ushort? WinnerId { get; init; }
    public required int Players { get; init; }
    public required long Ticks { get; init; }
    public required double WallSeconds { get; init; }
    public required double ActiveSeconds { get; init; }
    public required double TickMsAvg { get; init; }
    public required double TickMsP99 { get; init; }
    public required double TickMsMax { get; init; }
    public required double TickRateAchieved { get; init; }
    public required double KbpsOutPerPlayer { get; init; }
    public required MatchStats Stats { get; init; }
}

/// <summary>Runs a <see cref="Match"/> over UDP with a fixed-timestep loop on a dedicated thread.</summary>
public sealed class UdpMatchHost : IDisposable
{
    private readonly Match _match;
    private readonly Socket _socket;
    private readonly ConcurrentQueue<(IPEndPoint From, byte[] Data)> _inbox = new();

    public UdpMatchHost(Match match, int port)
    {
        _match = match;
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        if (OperatingSystem.IsWindows())
        {
            // SIO_UDP_CONNRESET off: a vanished client must not make ReceiveFrom throw for everyone.
            _socket.IOControl(unchecked((int)0x9800000C), new byte[4], null);
        }
        _socket.Bind(new IPEndPoint(IPAddress.Any, port));
    }

    public int Port => ((IPEndPoint)_socket.LocalEndPoint!).Port;

    public async Task<HostReport> RunAsync(CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task receive = ReceiveLoopAsync(cts.Token);
        var thread = new TaskCompletionSource<HostReport>();
        var t = new Thread(() =>
        {
            try { thread.SetResult(TickLoop(cts.Token)); }
            catch (Exception e) { thread.SetException(e); }
        }) { IsBackground = true, Name = "match-tick", Priority = ThreadPriority.AboveNormal };
        t.Start();
        try
        {
            return await thread.Task.ConfigureAwait(false);
        }
        finally
        {
            cts.Cancel();
            await receive.ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[2048];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult res;
            try
            {
                res = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, any, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            _inbox.Enqueue(((IPEndPoint)res.RemoteEndPoint, buffer.AsSpan(0, res.ReceivedBytes).ToArray()));
        }
    }

    private HostReport TickLoop(CancellationToken ct)
    {
        using var timer = HighResolutionTimer.Begin();
        double tickSeconds = 1.0 / _match.Config.TickRate;
        var tickMs = new List<double>();
        var wall = Stopwatch.StartNew();
        double next = 0;

        while (!ct.IsCancellationRequested && !_match.IsFinished)
        {
            double now = wall.Elapsed.TotalSeconds;
            if (now < next)
            {
                Thread.Sleep(next - now > 0.002 ? 1 : 0);
                continue;
            }
            next += tickSeconds;
            if (now - next > 0.5) next = now; // stalled (debugger, GC): resync instead of spiralling

            long start = Stopwatch.GetTimestamp();
            while (_inbox.TryDequeue(out var msg)) _match.Receive(msg.From, msg.Data, msg.Data.Length);
            _match.Advance();
            foreach (var (to, data) in _match.Outbox)
            {
                try
                {
                    _socket.SendTo(data, to);
                    _match.Stats.PacketsOut++;
                    _match.Stats.BytesOut += data.Length;
                }
                catch (SocketException) { }
            }
            _match.Outbox.Clear();
            tickMs.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        tickMs.Sort();
        double secs = wall.Elapsed.TotalSeconds;
        int players = Math.Max(1, _match.Stats.PeakPlayers);
        return new HostReport
        {
            Reason = _match.Result?.Reason,
            WinnerId = _match.Result?.WinnerId,
            Players = _match.Stats.PeakPlayers,
            Ticks = _match.Tick,
            WallSeconds = Math.Round(secs, 2),
            ActiveSeconds = Math.Round(_match.ActiveSeconds, 2),
            TickMsAvg = tickMs.Count == 0 ? 0 : Math.Round(tickMs.Average(), 3),
            TickMsP99 = tickMs.Count == 0 ? 0 : Math.Round(tickMs[(int)(tickMs.Count * 0.99)], 3),
            TickMsMax = tickMs.Count == 0 ? 0 : Math.Round(tickMs[^1], 3),
            TickRateAchieved = Math.Round(_match.Tick / secs, 2),
            KbpsOutPerPlayer = Math.Round(_match.Stats.BytesOut * 8 / 1000.0 / secs / players, 2),
            Stats = _match.Stats,
        };
    }

    public void Dispose() => _socket.Dispose();

    /// <summary>Raises the Windows timer resolution to 1 ms for the tick thread; no-op elsewhere.</summary>
    private sealed class HighResolutionTimer : IDisposable
    {
        private readonly bool _active;

        private HighResolutionTimer(bool active) => _active = active;

        public static HighResolutionTimer Begin() =>
            new(OperatingSystem.IsWindows() && TimeBeginPeriod(1) == 0);

        public void Dispose()
        {
            if (_active) TimeEndPeriod(1);
        }

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint milliseconds);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint milliseconds);
    }
}
