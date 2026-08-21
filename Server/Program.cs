using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using QRCoder;
// Bluetooth (classic RFCOMM/SPP) server-side transport - additive, second
// input path alongside the existing UDP listener below. See RunBluetoothListener.
using InTheHand.Net.Bluetooth;
using InTheHand.Net.Sockets;

class Program
{
    static ViGEmClient vigemClient = new ViGEmClient();

    // Guards ALL access to the players dictionary itself (adds and
    // enumeration). Per-player input mutation is guarded separately by
    // that player's own PlayerConnection.Lock - see below.
    static readonly object playersLock = new object();
    static Dictionary<string, PlayerConnection> players =
        new Dictionary<string, PlayerConnection>();

    static int playerCount = 0;

    // Digital buttons carried in a STATE mask, bit position = index here.
    // MUST stay in sync with Android's ButtonState.ORDER.
    static readonly (int bit, Xbox360Button button)[] MaskButtons =
    {
        (0, Xbox360Button.A),
        (1, Xbox360Button.B),
        (2, Xbox360Button.X),
        (3, Xbox360Button.Y),
        (4, Xbox360Button.LeftShoulder),
        (5, Xbox360Button.RightShoulder),
        (6, Xbox360Button.LeftThumb),
        (7, Xbox360Button.RightThumb),
        (8, Xbox360Button.Start),
        (9, Xbox360Button.Back),
        (10, Xbox360Button.Up),
        (11, Xbox360Button.Down),
        (12, Xbox360Button.Left),
        (13, Xbox360Button.Right),
    };
    const int LtBit = 14;
    const int RtBit = 15;

    // If we haven't heard from a controller in this long, assume the
    // phone crashed / lost Wi-Fi / went out of range and force every
    // input back to neutral rather than leaving something stuck held.
    // Within the requested 500-1000ms window.
    const long FailsafeTimeoutMs = 750;
    const int FailsafeCheckIntervalMs = 150;

    // ================= TEMPORARY STUCK-STICK DIAGNOSTIC =================
    // Disabled: per-packet logging at ~90-180 lines/sec was enough
    // console I/O to make the host machine sluggish. Left as a no-op
    // helper (DiagEnabled = false short-circuits every call site below)
    // so it's cheap to re-enable narrowly and briefly if ever needed
    // again - but the drain thread and the queue are gone, and every
    // per-packet JOY_L/JOY_R call site has been removed outright rather
    // than just gated, since those were the actual volume source.
    const bool DiagEnabled = false;

    static void Diag(PlayerConnection player, string msg)
    {
        if (!DiagEnabled) return;
        Console.WriteLine($"[{Environment.TickCount64}] [{player.PlayerId}] [ctrl={player.Controller.GetHashCode()}] {msg}");
    }

    // TEMP AXIS-WRITE DIAGNOSTIC: logs a line for EVERY SetAxisValue() call
    // that reaches LeftThumbX/Y or RightThumbX/Y, but only when the value
    // being written actually differs from the last value this same axis
    // logged (per-player, per-axis) - so a held stick or a quiet controller
    // does not produce ongoing log spam, while every genuine transition
    // (including every write of 0) is captured. `lastLogged` is one of the
    // PlayerConnection.DiagLast* fields below, passed by ref so the "last
    // value seen" state updates in place. Returns true iff it printed, so
    // call sites can conditionally log a matching SUBMIT-REPORT line right
    // after SubmitReport() - only when this call actually changed an axis,
    // never unconditionally.
    static bool DiagAxis(PlayerConnection player, string axisName, short value, ref short? lastLogged, string source, long seq)
    {
        if (!DiagEnabled) return false;
        if (lastLogged.HasValue && lastLogged.Value == value) return false;
        lastLogged = value;
        Console.WriteLine(
            $"[{Environment.TickCount64}] [AXIS-WRITE] player={player.PlayerId} ctrl={player.Controller.GetHashCode()} " +
            $"axis={axisName} value={value} source={source} seq={seq}");
        return true;
    }
    // ============= END TEMPORARY STUCK-STICK DIAGNOSTIC (setup) ==========
    // A joystick is a continuously refreshed state stream. If one stick stops
    // receiving fresh JOY packets while the connection itself is still alive
    // (for example, because of packet loss), center that stick independently
    // instead of letting its last value remain held forever.
    const long JoystickTimeoutMs = 150;

    /// <summary>
    /// Everything the server tracks for one connected controller. Wraps
    /// the ViGEm controller together with the bookkeeping needed for the
    /// STATE resync protocol and the disconnect failsafe.
    /// </summary>
    class PlayerConnection
    {
        public IXbox360Controller Controller;

        // TEMP DIAGNOSTIC: remote endpoint string, purely for tagging log
        // lines - set once at creation, never mutated.
        public string PlayerId = "";

        // Guards all mutation of Controller (SetButtonState / SetAxisValue
        // / SetSliderValue / SubmitReport) for this player specifically,
        // since both the main receive loop and the failsafe watcher thread
        // can touch the same controller.
        public readonly object Lock = new object();

        public long LastSeenMs;

        // Newest STATE sequence number applied so far. -1 = none yet.
        // Any incoming STATE packet with seq <= LastStateSeq is a
        // duplicate or a reordered/stale packet and must be ignored so an
        // old snapshot can never clobber a newer one.
        public long LastStateSeq = -1;

        // Per-stick sequence numbers for JOY_ packets: same reordering
        // protection as STATE. -1 = none received yet so any first packet
        // is always accepted.
        public long LastJoyLSeq = -1;
        public long LastJoyRSeq = -1;

        // Last accepted JOY packet time for each stick. These are deliberately
        // independent of LastSeenMs because STATE packets must not keep an
        // analog stick alive when its dedicated JOY stream has stopped.
        public long LastJoyLSeenMs = 0;
        public long LastJoyRSeenMs = 0;
        public bool LeftStickExpired = true;
        public bool RightStickExpired = true;

        // True once the failsafe watcher has already neutralized this
        // controller for the current gap in traffic, so it doesn't spam
        // SubmitReport() every check interval while the gap continues.
        // Cleared back to false the moment any packet arrives again.
        public bool Released;

        // TEMP AXIS-WRITE DIAGNOSTIC: last value DiagAxis logged for each
        // axis, so it can log only on change. Null = nothing logged yet
        // (so the first write for a fresh player always logs).
        public short? DiagLastLeftX, DiagLastLeftY, DiagLastRightX, DiagLastRightY;
    }

    static void Main()
    {
        string ip = GetLocalIPAddress();

        int port = 5000;

        Console.WriteLine($"🌐 UDP Server: {ip}:{port}");

        GenerateQRCode($"GAMEPAD|{ip}|{port}");

        UdpClient server = new UdpClient(port);

        // Give the OS more headroom to buffer bursts of input packets
        // (rapid button mashing + both joysticks moving at once) so a
        // momentary stall in our loop doesn't turn into silent packet loss.
        server.Client.ReceiveBufferSize = 1 << 20; // 1 MB

        var failsafeThread = new Thread(RunFailsafeWatcher)
        {
            IsBackground = true,
            Name = "Failsafe-Watcher"
        };
        failsafeThread.Start();


        // Additive second input path - see RunBluetoothListener. Runs on
        // its own background thread; if Bluetooth isn't available on this
        // PC at all, RunBluetoothListener logs that and returns, and UDP
        // above is completely unaffected either way.
        var bluetoothThread = new Thread(RunBluetoothListener)
        {
            IsBackground = true,
            Name = "Bluetooth-Listener"
        };
        bluetoothThread.Start();

        Console.WriteLine("📡 Waiting for UDP players...");

        while (true)
        {
            IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);

            byte[] data = server.Receive(ref remoteEP);

            string message = Encoding.UTF8.GetString(data).Trim();

            string playerId = remoteEP.ToString();

            PlayerConnection player = GetOrCreatePlayer(playerId);

            // Any packet at all - button, joystick, or STATE - counts as a
            // live heartbeat from this controller and clears a prior
            // failsafe release. This must be under player.Lock: it used to
            // be a bare unsynchronized write, which meant the failsafe
            // watcher's timeout decision (see RunFailsafeWatcher) could
            // read/act on LastSeenMs/Released while this update was
            // concurrently in flight, with no ordering guarantee between
            // them at all.
            MarkAlive(player);

            HandleInput(message, player);
        }
    }

    /// <summary>
    /// Looks up the PlayerConnection for a given transport-specific player
    /// id (UDP: "ip:port" from the datagram's remote endpoint; Bluetooth:
    /// "BT:" + the peer's Bluetooth address - see HandleBluetoothClient),
    /// creating one (and its backing ViGEm controller) on first contact.
    /// Shared by both the UDP loop and the Bluetooth per-client handler so
    /// neither transport has its own copy of the player-creation logic,
    /// and so a reconnect on the SAME id (same UDP socket, or same phone's
    /// Bluetooth address) reuses the existing controller instead of
    /// creating a duplicate one.
    /// </summary>
    static PlayerConnection GetOrCreatePlayer(string playerId)
    {
        lock (playersLock)
        {
            if (!players.TryGetValue(playerId, out var player))
            {
                playerCount++;

                var controller = vigemClient.CreateXbox360Controller();
                controller.Connect();

                player = new PlayerConnection { Controller = controller, PlayerId = playerId };
                players[playerId] = player;

                Console.WriteLine($"🎮 Player {playerCount} connected ({playerId}) — TEMP DIAGNOSTIC: total tracked players now = {players.Count}");
                if (DiagEnabled)
                {
                    Diag(player, $"PLAYER CREATED ctrl={controller.GetHashCode()} totalPlayers={players.Count}");
                }
            }
            return player;
        }
    }

    /// <summary>
    /// Marks a player as having just been heard from, clearing any prior
    /// failsafe release - see the call site comments in Main() for why
    /// this has to happen under player.Lock. Shared by both transports.
    /// </summary>
    static void MarkAlive(PlayerConnection player)
    {
        lock (player.Lock)
        {
            player.LastSeenMs = Environment.TickCount64;
            player.Released = false;
        }
    }

    // ============================================================
    // BLUETOOTH (RFCOMM/SPP) - additive second transport, PC side.
    //
    // BluetoothClient.kt on the Android side already sends the EXACT same
    // wire payloads as SocketClient.kt (JOY_L:seq:x,y / JOY_R:seq:x,y /
    // STATE:seq:mask:lx,ly:rx,ry / <NAME>_DOWN / <NAME>_UP), just framed
    // with a trailing '\n' per message since RFCOMM is a continuous byte
    // stream rather than discrete UDP datagrams. This section's only job
    // is to accept that stream, split it back into the same messages, and
    // feed each one into the SAME HandleInput(...) used by UDP above via
    // the SAME GetOrCreatePlayer/MarkAlive helpers - there is no second
    // parser and no separate joystick/button/ViGEm logic here.
    //
    // Uses the InTheHand.Net.Bluetooth NuGet package (RFCOMM client +
    // server support for .NET on Windows) for the BluetoothListener /
    // BluetoothClient types - see GamepadServer.csproj.
    // ============================================================

    const string BluetoothPlayerPrefix = "BT:";

    /// <summary>
    /// Runs for the lifetime of the process on its own background thread,
    /// exactly like RunFailsafeWatcher. Opens a standard SPP (Serial Port
    /// Profile) RFCOMM listener - the same well-known service UUID
    /// (00001101-0000-1000-8000-00805F9B34FB) that BluetoothClient.kt
    /// connects to via createRfcommSocketToServiceRecord - and hands off
    /// each accepted connection to its own reader thread.
    ///
    /// If this PC has no Bluetooth radio, or Bluetooth is off, Start()
    /// throws; that's caught here and logged, and the UDP server above is
    /// completely unaffected - Bluetooth is strictly an additional,
    /// optional input path, never a replacement for UDP.
    /// </summary>
    static void RunBluetoothListener()
    {
        // ================= TEMPORARY DIAGNOSTIC LOGGING =================
        // Added solely to determine where Bluetooth startup stalls/fails.
        // Not a functional change - remove once root cause is identified.
        Console.WriteLine("[BT DEBUG] RunBluetoothListener entered");

        BluetoothListener listener;
        try
        {
            Console.WriteLine("[BT DEBUG] Creating BluetoothListener...");
            listener = new BluetoothListener(BluetoothService.SerialPort);
            Console.WriteLine("[BT DEBUG] BluetoothListener created");

            Console.WriteLine("[BT DEBUG] Calling listener.Start()");
            listener.Start();
            Console.WriteLine("[BT DEBUG] listener.Start() succeeded");
        }
        catch (Exception e)
        {
            Console.WriteLine($"⚠ Bluetooth listener unavailable (no/disabled Bluetooth radio?): {e.ToString()}");
            return;
        }
        // ============= END TEMPORARY DIAGNOSTIC LOGGING (setup) ==========

        Console.WriteLine("📶 Waiting for Bluetooth players...");

        while (true)
        {
            BluetoothClient client;
            try
            {
                // Blocking accept - mirrors the blocking server.Receive()
                // pattern the UDP loop already uses.
                client = listener.AcceptBluetoothClient();
            }
            catch (Exception e)
            {
                Console.WriteLine($"⚠ Bluetooth accept failed: {e.Message}");
                continue;
            }

            var clientThread = new Thread(() => HandleBluetoothClient(client))
            {
                IsBackground = true,
                Name = "BT-Client"
            };
            clientThread.Start();
        }
    }

    /// <summary>
    /// Owns one accepted Bluetooth RFCOMM connection for its lifetime:
    /// reads the raw byte stream, reassembles it into the same
    /// newline-delimited text messages BluetoothClient.kt sent, and feeds
    /// each complete one into the shared GetOrCreatePlayer/MarkAlive/
    /// HandleInput pipeline - identically to how the UDP loop feeds a
    /// datagram in. No joystick, button, or ViGEm logic lives here.
    /// </summary>
    static void HandleBluetoothClient(BluetoothClient client)
    {
        string playerId;
        NetworkStream stream;
        try
        {
            // The peer's Bluetooth address is stable across reconnects
            // from the same phone (unlike UDP's ip:port, which changes
            // whenever SocketClient opens a fresh DatagramSocket) - so a
            // dropped-and-resumed Bluetooth session from the same device
            // lands back on the SAME PlayerConnection/ViGEm controller
            // via GetOrCreatePlayer, rather than creating a duplicate one.
            // The "BT:" prefix guarantees this id can never collide with
            // a UDP "ip:port" id.
            playerId = BluetoothPlayerPrefix + ((InTheHand.Net.BluetoothEndPoint)client.Client.RemoteEndPoint).Address;
            stream = client.GetStream();
        }
        catch (Exception e)
        {
            Console.WriteLine($"⚠ Bluetooth client rejected: {e.Message}");
            try { client.Close(); } catch { /* already gone */ }
            return;
        }

        Console.WriteLine($"📶 Bluetooth player connecting ({playerId})");

        var readBuffer = new byte[4096];
        string pending = "";

        try
        {
            while (true)
            {
                int read = stream.Read(readBuffer, 0, readBuffer.Length);
                if (read == 0)
                    break; // remote end closed the stream cleanly

                pending += Encoding.UTF8.GetString(readBuffer, 0, read);

                // RFCOMM is a byte stream with no message boundaries of its
                // own, so split on the '\n' framing BluetoothClient.kt adds
                // to every send. A single Read() can contain zero, one, or
                // several complete messages plus a trailing partial one -
                // only the complete ones (up to each '\n') are consumed
                // here; any partial remainder stays in `pending` until the
                // rest arrives on a later Read().
                int newlineIndex;
                while ((newlineIndex = pending.IndexOf('\n')) >= 0)
                {
                    // Trim CR/LF/whitespace the same way the UDP path
                    // already trims each datagram before handling it.
                    string message = pending.Substring(0, newlineIndex).Trim();
                    pending = pending.Substring(newlineIndex + 1);

                    if (message.Length == 0)
                        continue;

                    PlayerConnection player = GetOrCreatePlayer(playerId);
                    MarkAlive(player);
                    HandleInput(message, player);
                }
            }
        }
        catch (Exception)
        {
            // Stream errored out (radio dropped out of range, phone app
            // killed, etc). Deliberately NOT doing any release/removal
            // here: the existing RunFailsafeWatcher already owns
            // detecting a player that's gone quiet (via LastSeenMs) and
            // neutralizing + removing it after FailsafeTimeoutMs, exactly
            // as it does for a UDP connection that silently stops sending.
            // A Bluetooth disconnect is just left to go stale the same
            // way, instead of duplicating that cleanup/failsafe logic here.
        }
        finally
        {
            Console.WriteLine($"📶 Bluetooth stream closed ({playerId})");
            try { client.Close(); } catch { /* already gone */ }
        }
    }

    static void HandleInput(string data, PlayerConnection player)
    {
        try
        {
            var controller = player.Controller;

            // BUTTONS - immediate low-latency edge events. Unchanged wire
            // format from before; still applied as soon as they arrive.
            if (data.EndsWith("_DOWN") || data.EndsWith("_UP"))
            {
                bool pressed = data.EndsWith("_DOWN");

                string key = data.Substring(0, data.LastIndexOf('_'));

                lock (player.Lock)
                {
                    switch (key)
                    {
                        case "A":
                            controller.SetButtonState(Xbox360Button.A, pressed);
                            break;

                        case "B":
                            controller.SetButtonState(Xbox360Button.B, pressed);
                            break;

                        case "X":
                            controller.SetButtonState(Xbox360Button.X, pressed);
                            break;

                        case "Y":
                            controller.SetButtonState(Xbox360Button.Y, pressed);
                            break;

                        case "LB":
                            controller.SetButtonState(Xbox360Button.LeftShoulder, pressed);
                            break;

                        case "RB":
                            controller.SetButtonState(Xbox360Button.RightShoulder, pressed);
                            break;

                        case "LS":
                            controller.SetButtonState(Xbox360Button.LeftThumb, pressed);
                            break;

                        case "RS":
                            controller.SetButtonState(Xbox360Button.RightThumb, pressed);
                            break;

                        case "START":
                            controller.SetButtonState(Xbox360Button.Start, pressed);
                            break;

                        case "SELECT":
                            controller.SetButtonState(Xbox360Button.Back, pressed);
                            break;

                        case "DPAD_UP":
                            controller.SetButtonState(Xbox360Button.Up, pressed);
                            break;

                        case "DPAD_DOWN":
                            controller.SetButtonState(Xbox360Button.Down, pressed);
                            break;

                        case "DPAD_LEFT":
                            controller.SetButtonState(Xbox360Button.Left, pressed);
                            break;

                        case "DPAD_RIGHT":
                            controller.SetButtonState(Xbox360Button.Right, pressed);
                            break;

                        case "LT":
                            controller.SetSliderValue(
                                Xbox360Slider.LeftTrigger,
                                pressed ? (byte)255 : (byte)0
                            );
                            break;

                        case "RT":
                            controller.SetSliderValue(
                                Xbox360Slider.RightTrigger,
                                pressed ? (byte)255 : (byte)0
                            );
                            break;
                    }

                    controller.SubmitReport();
                }
            }

            // JOYSTICKS
            // Wire format: JOY_L:seq:x,y  (seq is monotonically increasing
            // per stick; the server rejects any packet whose seq is not
            // strictly greater than the last one applied, preventing a
            // delayed/reordered old position from overwriting a newer one).
            else if (data.StartsWith("JOY_"))
            {
                var parts = data.Split(':');

                // Expect exactly 3 parts: tag, seq, "x,y"
                if (parts.Length < 3)
                    return;

                var tag = parts[0];

                // Parse the per-packet sequence number.
                if (!long.TryParse(parts[1], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out long joySeq))
                    return;

                var values = parts[2].Split(',');

                if (values.Length < 2)
                    return;

                // IMPORTANT: parse with InvariantCulture. The Android client
                // always formats floats with '.' as the decimal separator
                // (Kotlin/Java Float.toString is locale-independent), but
                // float.Parse(string) without a culture uses the OS locale.
                // On a machine set to a locale that uses ',' as the decimal
                // separator, every single joystick packet would throw here
                // and get silently swallowed by the catch-all below - i.e.
                // every joystick update dropped. TryParse also avoids the
                // (comparatively expensive) exception path entirely.
                if (!float.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
                    !float.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
                {
                    return;
                }

                short joyX = (short)(x * 32767);
                short joyY = (short)(-y * 32767);

                lock (player.Lock)
                {
                    bool axisLogged = false;

                    if (tag == "JOY_L")
                    {
                        // Reject stale/reordered packets.
                        if (joySeq <= player.LastJoyLSeq)
                            return;
                        player.LastJoyLSeq = joySeq;
                        player.LastJoyLSeenMs = Environment.TickCount64;
                        player.LeftStickExpired = false;

                        controller.SetAxisValue(Xbox360Axis.LeftThumbX, joyX);
                        controller.SetAxisValue(Xbox360Axis.LeftThumbY, joyY);
                        axisLogged |= DiagAxis(player, "LeftThumbX", joyX, ref player.DiagLastLeftX, "JOY_L", joySeq);
                        axisLogged |= DiagAxis(player, "LeftThumbY", joyY, ref player.DiagLastLeftY, "JOY_L", joySeq);
                    }
                    else if (tag == "JOY_R")
                    {
                        // Reject stale/reordered packets.
                        if (joySeq <= player.LastJoyRSeq)
                            return;
                        player.LastJoyRSeq = joySeq;
                        player.LastJoyRSeenMs = Environment.TickCount64;
                        player.RightStickExpired = false;

                        controller.SetAxisValue(Xbox360Axis.RightThumbX, joyX);
                        controller.SetAxisValue(Xbox360Axis.RightThumbY, joyY);
                        axisLogged |= DiagAxis(player, "RightThumbX", joyX, ref player.DiagLastRightX, "JOY_R", joySeq);
                        axisLogged |= DiagAxis(player, "RightThumbY", joyY, ref player.DiagLastRightY, "JOY_R", joySeq);
                    }

                    controller.SubmitReport();

                    if (axisLogged)
                    {
                        Console.WriteLine(
                            $"[{Environment.TickCount64}] [SUBMIT-REPORT] player={player.PlayerId} ctrl={controller.GetHashCode()} " +
                            $"after source={tag} seq={joySeq}");
                    }
                }
            }

            // STATE - periodic authoritative digital-button snapshot. This
            // is the fix for the stuck-button bug: even if an individual
            // *_UP packet above was lost on the wire, the next one of
            // these re-syncs every digital button to the true state, since
            // it's applied as an absolute value rather than a toggle.
            else if (data.StartsWith("STATE:"))
            {
                var parts = data.Split(':');
                if (parts.Length < 3)
                    return;

                if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long seq))
                    return;

                if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mask))
                    return;

                // STATE:seq:mask:leftX,leftY:rightX,rightY
                // Older clients may still send only STATE:seq:mask; those
                // packets remain valid for digital-button resync.
                float leftX = 0f, leftY = 0f, rightX = 0f, rightY = 0f;
                bool hasJoystickState = parts.Length >= 5;

                if (hasJoystickState)
                {
                    var left  = parts[3].Split(',');
                    var right = parts[4].Split(',');

                    // If joystick fields are malformed, fall through and
                    // still apply the digital-button mask below rather than
                    // discarding the entire packet. The joystick positions
                    // simply remain at (0,0) for this STATE packet.
                    if (left.Length < 2 || right.Length < 2 ||
                        !float.TryParse(left[0],  NumberStyles.Float, CultureInfo.InvariantCulture, out leftX)  ||
                        !float.TryParse(left[1],  NumberStyles.Float, CultureInfo.InvariantCulture, out leftY)  ||
                        !float.TryParse(right[0], NumberStyles.Float, CultureInfo.InvariantCulture, out rightX) ||
                        !float.TryParse(right[1], NumberStyles.Float, CultureInfo.InvariantCulture, out rightY))
                    {
                        hasJoystickState = false; // malformed – skip joystick, keep mask
                    }
                    else
                    {
                        leftX  = Math.Clamp(leftX,  -1f, 1f);
                        leftY  = Math.Clamp(leftY,  -1f, 1f);
                        rightX = Math.Clamp(rightX, -1f, 1f);
                        rightY = Math.Clamp(rightY, -1f, 1f);
                    }
                }

                lock (player.Lock)
                {
                    // Reordering safety: an older snapshot can never
                    // overwrite a newer one already applied.
                    if (seq <= player.LastStateSeq)
                        return;

                    player.LastStateSeq = seq;

                    ApplyButtonMask(controller, mask);

                    // JOYSTICK AUTHORITY: JOY_L/JOY_R are now the ONLY
                    // packets allowed to write LeftThumbX/Y and
                    // RightThumbX/Y (see HandleInput's "JOY_" branch above).
                    // STATE still PARSES its embedded leftX/leftY/rightX/
                    // rightY above (hasJoystickState) for wire-format
                    // compatibility with the existing Android client, but
                    // deliberately does NOT apply them to the controller:
                    // JOY_ and STATE used independent sequence numbers
                    // (LastJoyLSeq/LastJoyRSeq vs LastStateSeq) with no
                    // cross-stream ordering, so a STATE packet carrying a
                    // pre-release snapshot could arrive after a fresher
                    // JOY_L zero packet and re-stick the axis at its old
                    // position. Giving each axis exactly one writer removes
                    // that race entirely.
                    controller.SubmitReport();
                }
            }
        }
        catch
        {
            // Ignore malformed packets
        }
    }

    static void ApplyButtonMask(IXbox360Controller controller, int mask)
    {
        foreach (var (bit, button) in MaskButtons)
        {
            controller.SetButtonState(button, (mask & (1 << bit)) != 0);
        }

        controller.SetSliderValue(Xbox360Slider.LeftTrigger, (mask & (1 << LtBit)) != 0 ? (byte)255 : (byte)0);
        controller.SetSliderValue(Xbox360Slider.RightTrigger, (mask & (1 << RtBit)) != 0 ? (byte)255 : (byte)0);
    }

    static void ApplyJoystickState(
        IXbox360Controller controller,
        float leftX,
        float leftY,
        float rightX,
        float rightY)
    {
        controller.SetAxisValue(
            Xbox360Axis.LeftThumbX,
            (short)(leftX * 32767));
        controller.SetAxisValue(
            Xbox360Axis.LeftThumbY,
            (short)(-leftY * 32767));
        controller.SetAxisValue(
            Xbox360Axis.RightThumbX,
            (short)(rightX * 32767));
        controller.SetAxisValue(
            Xbox360Axis.RightThumbY,
            (short)(-rightY * 32767));
    }

    /// <summary>
    /// Runs for the lifetime of the process on its own thread. Every
    /// FailsafeCheckIntervalMs, checks every connected controller's last-
    /// seen timestamp; anything that's gone quiet for longer than
    /// FailsafeTimeoutMs gets every button released and both sticks
    /// centered, regardless of why the traffic stopped (crash, Wi-Fi
    /// drop, app killed, phone out of range, etc). This is the backstop
    /// for cases where the app never gets a chance to send an explicit
    /// close()-time release at all.
    /// </summary>
    static void RunFailsafeWatcher()
    {
        while (true)
        {
            Thread.Sleep(FailsafeCheckIntervalMs);

            List<KeyValuePair<string, PlayerConnection>> snapshot;
            lock (playersLock)
            {
                snapshot = new List<KeyValuePair<string, PlayerConnection>>(players);
            }

            long now = Environment.TickCount64;

            foreach (var kvp in snapshot)
            {
                string playerId = kvp.Key;
                PlayerConnection player = kvp.Value;

                if (player.Released)
                    continue;

                bool timedOut = false;

                // Everything - the stick-specific expiry check, the overall
                // connection-timeout check, and (if it fires) the release -
                // now happens in ONE lock(player.Lock) acquisition.
                //
                // Previously this was two separate lock blocks: the code
                // would check "now - LastSeenMs < FailsafeTimeoutMs" inside
                // the FIRST block, release the lock, then act on that
                // decision inside a SECOND block. A packet handled on the
                // main receive thread (which also takes player.Lock, see
                // Main()/HandleInput) could land in the gap between the two
                // blocks: it would legitimately update LastSeenMs and clear
                // Released, but this method - having already decided
                // "timed out" using the now-stale pre-packet snapshot -
                // would still barrel into the second block, re-check only
                // player.Released (already false again from that fresh
                // packet, so it wouldn't even skip), call ReleaseAll on a
                // controller that was just legitimately updated, and reset
                // its sequence counters. Doing the whole decision under one
                // continuous lock acquisition makes that interleaving
                // impossible: whichever side gets the lock first fully
                // finishes before the other side's decision is made.
                lock (player.Lock)
                {
                    if (!player.LeftStickExpired && now - player.LastJoyLSeenMs >= JoystickTimeoutMs)
                    {
                        player.Controller.SetAxisValue(Xbox360Axis.LeftThumbX, 0);
                        player.Controller.SetAxisValue(Xbox360Axis.LeftThumbY, 0);
                        bool axisLoggedL = false;
                        axisLoggedL |= DiagAxis(player, "LeftThumbX", 0, ref player.DiagLastLeftX, "stick timeout", player.LastJoyLSeq);
                        axisLoggedL |= DiagAxis(player, "LeftThumbY", 0, ref player.DiagLastLeftY, "stick timeout", player.LastJoyLSeq);
                        player.LeftStickExpired = true;
                        player.Controller.SubmitReport();
                        if (axisLoggedL)
                        {
                            Console.WriteLine(
                                $"[{Environment.TickCount64}] [SUBMIT-REPORT] player={player.PlayerId} ctrl={player.Controller.GetHashCode()} " +
                                $"after source=stick timeout(L) seq={player.LastJoyLSeq}");
                        }
                        Diag(player, $"STICK-TIMEOUT LeftThumb->0 lastJoyLSeq={player.LastJoyLSeq} gapMs={now - player.LastJoyLSeenMs}");
                    }

                    if (!player.RightStickExpired && now - player.LastJoyRSeenMs >= JoystickTimeoutMs)
                    {
                        player.Controller.SetAxisValue(Xbox360Axis.RightThumbX, 0);
                        player.Controller.SetAxisValue(Xbox360Axis.RightThumbY, 0);
                        bool axisLoggedR = false;
                        axisLoggedR |= DiagAxis(player, "RightThumbX", 0, ref player.DiagLastRightX, "stick timeout", player.LastJoyRSeq);
                        axisLoggedR |= DiagAxis(player, "RightThumbY", 0, ref player.DiagLastRightY, "stick timeout", player.LastJoyRSeq);
                        player.RightStickExpired = true;
                        player.Controller.SubmitReport();
                        if (axisLoggedR)
                        {
                            Console.WriteLine(
                                $"[{Environment.TickCount64}] [SUBMIT-REPORT] player={player.PlayerId} ctrl={player.Controller.GetHashCode()} " +
                                $"after source=stick timeout(R) seq={player.LastJoyRSeq}");
                        }
                        Diag(player, $"STICK-TIMEOUT RightThumb->0 lastJoyRSeq={player.LastJoyRSeq} gapMs={now - player.LastJoyRSeenMs}");
                    }

                    if (!player.Released && now - player.LastSeenMs >= FailsafeTimeoutMs)
                    {
                        Diag(player, $"CONNECTION-TIMEOUT ReleaseAll gapMs={now - player.LastSeenMs}");
                        ReleaseAll(player.Controller, player);
                        player.Released = true;
                        timedOut = true;

                        // Sequence counters (LastStateSeq/LastJoyLSeq/
                        // LastJoyRSeq) are deliberately left AS-IS here -
                        // they used to be reset to -1 on the theory that a
                        // resumed connection might need to resync from a
                        // lower/unknown sequence number. That's not sound
                        // for this protocol:
                        //
                        //  - playerId is the client's IP:port. The Android
                        //    client (SocketClient.connect) opens a brand
                        //    new DatagramSocket - a brand new source port,
                        //    i.e. a brand new playerId - every time it
                        //    (re)connects. A genuinely new connection never
                        //    lands on this same PlayerConnection at all; it
                        //    gets its own fresh object (LastJoyLSeq/
                        //    LastJoyRSeq default to -1 already, see the
                        //    PlayerConnection field declarations).
                        //  - Traffic that resumes on THIS SAME playerId
                        //    after a gap (e.g. a Wi-Fi power-save stall,
                        //    or a delayed/duplicated UDP frame) is by
                        //    definition still the same client socket,
                        //    whose per-stick sequence counters
                        //    (SocketClient.joyLSeq/joyRSeq) only ever
                        //    increase for the life of that socket. They
                        //    never need "resyncing" to a lower value.
                        //
                        // Resetting them bought nothing but a hole: a
                        // stale JOY_L/JOY_R packet from before the gap
                        // (delayed in flight, or a duplicate MAC-layer
                        // retransmit - both real possibilities on Wi-Fi)
                        // that lands after the reset would satisfy
                        // "seq > -1" and get accepted as if it were new,
                        // re-sticking that axis at an old, possibly
                        // non-zero position - exactly the intermittent
                        // stuck-stick symptom this fix addresses. Leaving
                        // the counters untouched means such a packet still
                        // gets correctly rejected as stale even across a
                        // failsafe release, per the "old JOY packet must
                        // never resurrect a stick" invariant.
                    }
                }

                if (timedOut)
                {
                    // Drop the stale PlayerConnection and disconnect its
                    // ViGEm virtual controller. Previously the object was
                    // left in `players` forever with an undisposed
                    // controller still connected to the system - so a
                    // phone reconnecting from a new source port (a new
                    // playerId, see above) would get a SECOND virtual
                    // Xbox360 controller created alongside the first,
                    // never-cleaned-up one, and whatever tool/game is
                    // reading controller slots could end up bound to the
                    // wrong (or a now-orphaned) device.
                    lock (playersLock)
                    {
                        // Only remove it if it's still the same object
                        // under that key, in case a new connection already
                        // replaced this entry between releasing player.Lock
                        // above and taking playersLock here.
                        if (players.TryGetValue(playerId, out var current) && current == player)
                        {
                            players.Remove(playerId);
                        }
                    }

                    player.Controller.Disconnect();

                    Console.WriteLine("⏱ Controller timed out - releasing all inputs and removing stale connection");
                    if (DiagEnabled)
                    {
                        int liveCount;
                        lock (playersLock) { liveCount = players.Count; }
                        Diag(player, $"PLAYER REMOVED + Disconnect() called; remaining live players={liveCount}");
                    }
                }
            }
        }
    }

    // TEMP: `player` param added SOLELY for axis-write diagnostics (per-
    // axis identity/last-value tracking + PlayerId in log lines) - it is
    // optional and unused for anything behavioral. The one real call site
    // (connection-timeout branch in RunFailsafeWatcher) now passes it;
    // button/slider/axis logic below is byte-for-byte unchanged.
    static void ReleaseAll(IXbox360Controller controller, PlayerConnection player = null)
    {
        if (DiagEnabled)
        {
            Console.WriteLine($"[{Environment.TickCount64}] [ReleaseAll] ctrl={controller.GetHashCode()}");
        }

        foreach (var (_, button) in MaskButtons)
        {
            controller.SetButtonState(button, false);
        }

        controller.SetSliderValue(Xbox360Slider.LeftTrigger, 0);
        controller.SetSliderValue(Xbox360Slider.RightTrigger, 0);

        controller.SetAxisValue(Xbox360Axis.LeftThumbX, 0);
        controller.SetAxisValue(Xbox360Axis.LeftThumbY, 0);
        controller.SetAxisValue(Xbox360Axis.RightThumbX, 0);
        controller.SetAxisValue(Xbox360Axis.RightThumbY, 0);

        bool axisLogged = false;
        if (player != null)
        {
            axisLogged |= DiagAxis(player, "LeftThumbX", 0, ref player.DiagLastLeftX, "connection failsafe", player.LastJoyLSeq);
            axisLogged |= DiagAxis(player, "LeftThumbY", 0, ref player.DiagLastLeftY, "connection failsafe", player.LastJoyLSeq);
            axisLogged |= DiagAxis(player, "RightThumbX", 0, ref player.DiagLastRightX, "connection failsafe", player.LastJoyRSeq);
            axisLogged |= DiagAxis(player, "RightThumbY", 0, ref player.DiagLastRightY, "connection failsafe", player.LastJoyRSeq);
        }

        controller.SubmitReport();

        if (axisLogged)
        {
            Console.WriteLine(
                $"[{Environment.TickCount64}] [SUBMIT-REPORT] player={player.PlayerId} ctrl={controller.GetHashCode()} " +
                $"after source=connection failsafe");
        }
    }

    static string GetLocalIPAddress()
    {
        foreach (var ip in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                return ip.ToString();
            }
        }

        return "127.0.0.1";
    }

    static void GenerateQRCode(string text)
    {
        QRCodeGenerator qrGenerator = new QRCodeGenerator();

        QRCodeData qrCodeData =
            qrGenerator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);

        PngByteQRCode qrCode = new PngByteQRCode(qrCodeData);

        byte[] qrBytes = qrCode.GetGraphic(20);

        string path = "server_qr.png";

        System.IO.File.WriteAllBytes(path, qrBytes);

        Console.WriteLine("🖼 QR generated");

        Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true
        });
    }
}