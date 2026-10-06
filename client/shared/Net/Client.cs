// WebSocket transport: connect, handshake, pump frames, reconnect.
//
// NO UnityEngine — Net.asmdef is "noEngineReferences", so this is plain .NET
// and is testable headless. That constraint also forces the right shape: the
// socket cannot touch the scene, so it hands frames to a queue and the render
// loop drains them. Unity's API is main-thread-only and a background task that
// called into it would crash on the first snapshot.
//
// Threading contract, in one line each:
//   - Receive runs on a background task; decoded frames go into _inbound.
//   - Send is callable from any thread; ClientWebSocket permits one send at a
//     time, so a semaphore serialises them.
//   - Poll() is called by the main thread each frame and returns what arrived.
//
// The server URL is a parameter, never a constant: C45 requires a packaged
// build to join from config, and the same-origin assumption died with browser
// delivery (ARCHITECTURE, "Client delivery").

using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace SpaceAdventure.Net
{
    /// <summary>What the transport is doing right now.</summary>
    public enum LinkState
    {
        Idle,
        Connecting,
        Joined,
        Reconnecting,
        Failed,
    }

    /// <summary>One frame received from the server: its type and payload.</summary>
    public readonly struct Frame
    {
        public readonly ushort Type;
        public readonly byte[] Bytes; // the whole frame, type included

        public Frame(ushort type, byte[] bytes) { Type = type; Bytes = bytes; }

        /// <summary>A reader positioned at the payload, past the u16 type.</summary>
        public WireReader Reader => new WireReader(Bytes, 2, Bytes.Length - 2);
    }

    /// <summary>
    /// The connection to the game server. Owns the socket, the handshake and
    /// the reconnect policy; owns no game state.
    /// </summary>
    public sealed class NetClient : IDisposable
    {
        /// <summary>
        /// Ping cadence. Well inside the server's 10 s silence rule, and it is
        /// what measures RTT. Every QA harness that skipped this looked like a
        /// product bug: the socket died at 10 s and every later read returned
        /// stale entities, which reported as zero hits and a 0.00 Hz respawn
        /// (docs/QA-STATUS.md, "Three harness defects").
        /// </summary>
        private const int PingIntervalMs = 1_000;

        private readonly ConcurrentQueue<Frame> _inbound = new ConcurrentQueue<Frame>();
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

        private ClientWebSocket _socket;
        private CancellationTokenSource _cts;

        private string _url, _name, _token;
        private long _rttMs = -1;

        public LinkState State { get; private set; } = LinkState.Idle;

        /// <summary>Last error, for the HUD to show. Null when healthy.</summary>
        public string LastError { get; private set; }

        /// <summary>
        /// The status of the last close frame the server sent (1008 = the
        /// hello was refused, Phase 16), 0 when none since Connect.
        /// </summary>
        public int LastCloseCode { get; private set; }

        /// <summary>Our own entity id, from hello_ack. Zero until joined.</summary>
        public uint EntityId { get; private set; }

        /// <summary>Server tick rate and world seed, from hello_ack.</summary>
        public ushort TickHz { get; private set; }
        public uint WorldSeed { get; private set; }

        /// <summary>Smoothed round trip in ms, or -1 before the first pong.</summary>
        public long RttMs => Interlocked.Read(ref _rttMs);

        /// <summary>How many reconnects have been attempted this session.</summary>
        public int Reconnects { get; private set; }

        /// <summary>
        /// Opens the connection and starts the pump. Returns immediately; watch
        /// <see cref="State"/> for progress.
        /// </summary>
        public void Connect(string url, string playerName, string token)
        {
            if (State == LinkState.Connecting || State == LinkState.Joined)
            {
                throw new InvalidOperationException("already connected");
            }
            _url = url ?? throw new ArgumentNullException(nameof(url));
            _name = playerName ?? "";
            _token = token ?? "";
            _cts = new CancellationTokenSource();
            State = LinkState.Connecting;
            LastError = null;
            LastCloseCode = 0;
            _ = Task.Run(() => PumpForeverAsync(_cts.Token));
        }

        /// <summary>
        /// Drains everything that arrived since the last call. Main thread
        /// only, once per frame: the queue is the whole thread boundary.
        /// </summary>
        public bool Poll(out Frame frame) => _inbound.TryDequeue(out frame);

        /// <summary>
        /// Queues a full frame for the server. Safe from any thread. Silently
        /// drops while disconnected — an input for a socket that does not exist
        /// is not an error the caller can do anything about, and the reconnect
        /// re-handshakes anyway.
        /// </summary>
        public async void Send(byte[] frame)
        {
            var socket = _socket;
            if (socket == null || socket.State != WebSocketState.Open) return;
            try
            {
                await SendAsync(socket, frame, _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // The pump owns reconnection; a failed send just means the
                // socket is already on its way down.
                LastError = e.Message;
            }
        }

        private async Task SendAsync(ClientWebSocket socket, byte[] frame, CancellationToken ct)
        {
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await socket.SendAsync(new ArraySegment<byte>(frame),
                    WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>
        /// Connect, handshake, receive until the socket dies, then back off and
        /// do it again. Reconnect is a loop rather than an event because every
        /// caller wants the same thing: keep trying, and say so on the HUD.
        /// </summary>
        private async Task PumpForeverAsync(CancellationToken ct)
        {
            int backoffMs = 500;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await SessionAsync(ct).ConfigureAwait(false);
                    backoffMs = 500; // a session that ran resets the backoff
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    LastError = e.Message;
                }

                if (ct.IsCancellationRequested) return;
                State = LinkState.Reconnecting;
                Reconnects++;
                try
                {
                    await Task.Delay(backoffMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                backoffMs = Math.Min(backoffMs * 2, 8_000);
            }
        }

        private async Task SessionAsync(CancellationToken ct)
        {
            using var socket = new ClientWebSocket();
            _socket = socket;
            EntityId = 0;

            await socket.ConnectAsync(new Uri(_url), ct).ConfigureAwait(false);
            await SendAsync(socket, Encode.Hello(_name, _token), ct).ConfigureAwait(false);

            using var pings = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, pings.Token);
            var pinger = PingLoopAsync(socket, linked.Token);

            try
            {
                await ReceiveLoopAsync(socket, ct).ConfigureAwait(false);
            }
            finally
            {
                pings.Cancel();
                try { await pinger.ConfigureAwait(false); } catch { /* shutting down */ }
                _socket = null;
                if (State == LinkState.Joined) State = LinkState.Reconnecting;
            }
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
        {
            // One frame can arrive in several WebSocket fragments; the message
            // is only complete at EndOfMessage. Reading a fragment as a frame
            // decodes the first two bytes of a partial payload as a type.
            var chunk = new byte[16 * 1024];
            var assembled = new System.IO.MemoryStream();

            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(chunk), ct).ConfigureAwait(false);
                }
                catch (WebSocketException e)
                {
                    LastError = e.Message;
                    return;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    LastError = $"server closed: {result.CloseStatus} {result.CloseStatusDescription}";
                    LastCloseCode = (int)(result.CloseStatus ?? 0);
                    return;
                }
                if (result.MessageType != WebSocketMessageType.Binary)
                {
                    // PROTOCOL.md: binary messages only, no text frames.
                    LastError = "server sent a text frame";
                    return;
                }

                assembled.Write(chunk, 0, result.Count);
                if (assembled.Length > Wire.MaxMessageSize)
                {
                    LastError = $"frame over {Wire.MaxMessageSize} bytes";
                    return;
                }
                if (!result.EndOfMessage) continue;

                byte[] frame = assembled.ToArray();
                assembled.SetLength(0);
                if (frame.Length < 2) continue;

                ushort type = (ushort)(frame[0] | (frame[1] << 8));
                if (!HandleTransportFrame(type, frame))
                {
                    _inbound.Enqueue(new Frame(type, frame));
                }
            }
        }

        /// <summary>
        /// Consumes the frames the transport itself owns and reports whether it
        /// did. hello_ack and pong are connection facts, not gameplay; letting
        /// them through would make every caller re-implement the handshake.
        /// </summary>
        private bool HandleTransportFrame(ushort type, byte[] frame)
        {
            switch (type)
            {
                case Msg.HelloAck:
                {
                    var ack = Decode.HelloAck(new WireReader(frame, 2, frame.Length - 2));
                    if (ack.ServerVer != Wire.VersionPhase2)
                    {
                        LastError = $"server speaks v{ack.ServerVer}, this client speaks v{Wire.VersionPhase2}";
                        State = LinkState.Failed;
                        return true;
                    }
                    EntityId = ack.EntityId;
                    TickHz = ack.TickHz;
                    WorldSeed = ack.WorldSeed;
                    State = LinkState.Joined;
                    // Still queued: the game layer wants to know it joined, and
                    // which entity is its own body.
                    return false;
                }
                case Msg.Pong:
                {
                    uint sentAt = Decode.Pong(new WireReader(frame, 2, frame.Length - 2));
                    long rtt = NowMs() - sentAt;
                    long prev = Interlocked.Read(ref _rttMs);
                    // Same smoothing the server keeps, so both ends agree about
                    // the number the rewind is computed from.
                    long next = prev < 0 ? rtt : (prev * 7 + rtt) / 8;
                    Interlocked.Exchange(ref _rttMs, next);
                    return true;
                }
                default:
                    return false;
            }
        }

        private async Task PingLoopAsync(ClientWebSocket socket, CancellationToken ct)
        {
            // Ping first, delay after. The server's rewind is computed from
            // its own RTT measurement, so a connection that waits a full
            // interval before its first ping spends that time shooting with an
            // unmeasured latency (GDD "Lag compensation").
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                try
                {
                    await SendAsync(socket, Encode.Ping((uint)NowMs()), ct).ConfigureAwait(false);
                    await Task.Delay(PingIntervalMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    return; // the receive loop will notice and reconnect
                }
            }
        }

        /// <summary>
        /// Milliseconds on a monotonic clock, truncated to fit `ping`'s u32.
        /// The server echoes the value untouched, so only the difference
        /// matters and the wrap is harmless.
        ///
        /// Stopwatch, not DateTime: an RTT measured against wall time jumps
        /// when the clock is adjusted, and the rewind the server computes is
        /// derived from this number.
        /// </summary>
        internal static long NowMs() => _clock.ElapsedMilliseconds & 0xFFFFFFFFL;

        private static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { /* already gone */ }
            var socket = _socket;
            if (socket != null && socket.State == WebSocketState.Open)
            {
                try
                {
                    socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                          .GetAwaiter().GetResult();
                }
                catch { /* closing a dead socket is not an error worth raising */ }
            }
            _cts?.Dispose();
            _sendLock.Dispose();
            State = LinkState.Idle;
        }
    }
}
