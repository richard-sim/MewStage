using Aprillz.MewUI.Diagnostics;
using Aprillz.MewUI.Platform.Linux.DBus;

namespace Aprillz.MewUI.Platform.Linux.X11.IBus;

/// <summary>
/// Manages the D-Bus connection to the IBus daemon and input context lifecycle.
/// IBus can be reached either through its own socket or through the session bus.
/// </summary>
internal sealed class IBusConnection : IDisposable
{
    private static readonly EnvDebugLogger Logger = new("MEWUI_IME_DEBUG", "[IBus]");

    private DBusConnection? _conn;
    private string? _inputContextPath;
    private bool _disposed;

    public bool IsConnected => _conn?.IsConnected == true && _inputContextPath != null;

    internal DBusConnection? Connection => _conn;
    internal string? InputContextPath => _inputContextPath;

    /// <summary>
    /// Attempts to connect to IBus and create an input context.
    /// Returns null if IBus is not available.
    /// </summary>
    public static IBusConnection? TryConnect()
    {
        var ibus = new IBusConnection();

        // Try IBus-specific socket first (used when IBus is the system IM)
        ibus._conn = TryConnectIBusSocket();

        // Fall back to session bus
        ibus._conn ??= TryConnectViaSessionBus();

        if (ibus._conn == null)
        {
            Logger.Write("Cannot connect to IBus");
            ibus.Dispose();
            return null;
        }

        // Create input context
        if (!ibus.CreateInputContext())
        {
            Logger.Write("Failed to create IBus input context");
            ibus.Dispose();
            return null;
        }

        // Subscribe to signals
        ibus.SubscribeSignals();

        Logger.Write($"Connected, IC path: {ibus._inputContextPath}");
        return ibus;
    }

    private static DBusConnection? TryConnectIBusSocket()
    {
        // IBus has its own D-Bus server, address stored in a file or env var
        string? ibusAddress = null;
        try { ibusAddress = Environment.GetEnvironmentVariable("IBUS_ADDRESS"); } catch { }

        if (string.IsNullOrEmpty(ibusAddress))
        {
            ibusAddress = ReadIBusAddress();
        }

        if (string.IsNullOrEmpty(ibusAddress))
            return null;

        Logger.Write($"Trying IBus socket: {ibusAddress}");
        return DBusConnection.TryConnect(ibusAddress);
    }

    private static DBusConnection? TryConnectViaSessionBus()
    {
        Logger.Write("Trying IBus via session bus");
        var sessionConn = DBusConnection.TryConnectSession();
        if (sessionConn == null) return null;

        // Ask the IBus portal for the real IBus address
        uint serial = sessionConn.NextSerial();
        var msg = DBusWriter.BuildMethodCall(
            serial,
            "org.freedesktop.IBus",
            "/org/freedesktop/IBus",
            "org.freedesktop.IBus",
            "GetAddress");

        var reply = sessionConn.SendAndWaitReply(msg, serial, 2000);
        if (reply != null && reply.Type == DBusConstants.MethodReturn && reply.Body.Length > 0)
        {
            try
            {
                var r = new DBusReader(reply.Body, 0);
                string ibusAddr = r.ReadString();
                Logger.Write($"IBus address from session bus: {ibusAddr}");

                // Connect to the real IBus socket
                sessionConn.Dispose();
                return DBusConnection.TryConnect(ibusAddr);
            }
            catch { }
        }

        // Session bus itself doesn't host IBus
        Logger.Write("IBus not found on session bus");
        sessionConn.Dispose();
        return null;
    }

    private bool CreateInputContext()
    {
        if (_conn == null) return false;

        uint serial = _conn.NextSerial();
        var msg = DBusWriter.BuildMethodCall(
            serial,
            "org.freedesktop.IBus",
            "/org/freedesktop/IBus",
            "org.freedesktop.IBus",
            "CreateInputContext",
            "s",
            bw => bw.WriteString("MewUI"));

        var reply = _conn.SendAndWaitReply(msg, serial, 3000);
        if (reply == null || reply.Type != DBusConstants.MethodReturn)
        {
            if (reply?.Type == DBusConstants.Error)
                Logger.Write($"CreateInputContext error: {reply.ErrorName}");
            return false;
        }

        // Reply body contains the object path of the new input context
        if (reply.Body.Length == 0) return false;

        try
        {
            var r = new DBusReader(reply.Body, 0);
            _inputContextPath = r.ReadObjectPath();
            return !string.IsNullOrEmpty(_inputContextPath);
        }
        catch
        {
            return false;
        }
    }

    private void SubscribeSignals()
    {
        if (_conn == null || _inputContextPath == null) return;

        // Subscribe to CommitText, UpdatePreeditText, HidePreeditText
        string[] signals = ["CommitText", "UpdatePreeditText", "HidePreeditText", "ForwardKeyEvent"];
        foreach (var signal in signals)
        {
            uint serial = _conn.NextSerial();
            string rule = $"type='signal',interface='org.freedesktop.IBus.InputContext',member='{signal}',path='{_inputContextPath}'";
            var msg = DBusWriter.BuildAddMatch(serial, rule);
            _conn.Send(msg); // fire and forget
        }
    }

    /// <summary>
    /// Calls ProcessKeyEvent on the IBus input context.
    /// Returns (handled, forwardToApp).
    /// </summary>
    public (bool handled, bool forward) ProcessKeyEvent(uint keyval, uint keycode, uint state)
    {
        if (_conn == null || _inputContextPath == null)
            return (false, true);

        uint serial = _conn.NextSerial();
        var msg = DBusWriter.BuildMethodCall(
            serial,
            "org.freedesktop.IBus",
            _inputContextPath,
            "org.freedesktop.IBus.InputContext",
            "ProcessKeyEvent",
            "uuu",
            bw =>
            {
                bw.WriteUInt32(keyval);
                bw.WriteUInt32(keycode);
                bw.WriteUInt32(state);
            });

        var reply = _conn.SendAndWaitReply(msg, serial, 200); // tight timeout for key latency
        if (reply == null || reply.Type != DBusConstants.MethodReturn || reply.Body.Length < 4)
            return (false, true);

        try
        {
            var r = new DBusReader(reply.Body, 0);
            bool handled = r.ReadBool();
            return (handled, !handled);
        }
        catch
        {
            return (false, true);
        }
    }

    public void FocusIn()
    {
        CallVoidMethod("FocusIn");
    }

    public void FocusOut()
    {
        CallVoidMethod("FocusOut");
    }

    public void Reset()
    {
        CallVoidMethod("Reset");
    }

    public void SetCursorLocation(int x, int y, int w, int h)
    {
        if (_conn == null || _inputContextPath == null) return;

        uint serial = _conn.NextSerial();
        var msg = DBusWriter.BuildMethodCall(
            serial,
            "org.freedesktop.IBus",
            _inputContextPath,
            "org.freedesktop.IBus.InputContext",
            "SetCursorLocation",
            "iiii",
            bw =>
            {
                bw.WriteInt32(x);
                bw.WriteInt32(y);
                bw.WriteInt32(w);
                bw.WriteInt32(h);
            });

        _conn.Send(msg); // fire and forget
    }

    /// <summary>
    /// Enables IBus input context capabilities (preedit, etc).
    /// </summary>
    public void SetCapabilities(uint caps)
    {
        if (_conn == null || _inputContextPath == null) return;

        uint serial = _conn.NextSerial();
        var msg = DBusWriter.BuildMethodCall(
            serial,
            "org.freedesktop.IBus",
            _inputContextPath,
            "org.freedesktop.IBus.InputContext",
            "SetCapabilities",
            "u",
            bw => bw.WriteUInt32(caps));

        _conn.Send(msg);
    }

    private void CallVoidMethod(string method)
    {
        if (_conn == null || _inputContextPath == null) return;

        uint serial = _conn.NextSerial();
        var msg = DBusWriter.BuildMethodCall(
            serial,
            "org.freedesktop.IBus",
            _inputContextPath,
            "org.freedesktop.IBus.InputContext",
            method);

        _conn.Send(msg); // fire and forget
    }

    private static string? ReadIBusAddress()
    {
        // IBus stores its address in ~/.config/ibus/bus/<machine-id>-<display>
        try
        {
            string? configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(configHome))
            {
                string? home = Environment.GetEnvironmentVariable("HOME");
                if (string.IsNullOrEmpty(home)) return null;
                configHome = Path.Combine(home, ".config");
            }

            string ibusDir = Path.Combine(configHome, "ibus", "bus");
            if (!Directory.Exists(ibusDir)) return null;

            // Read machine-id
            string machineId = "";
            if (File.Exists("/etc/machine-id"))
                machineId = File.ReadAllText("/etc/machine-id").Trim();
            else if (File.Exists("/var/lib/dbus/machine-id"))
                machineId = File.ReadAllText("/var/lib/dbus/machine-id").Trim();

            if (string.IsNullOrEmpty(machineId)) return null;

            // Display number
            string? display = Environment.GetEnvironmentVariable("DISPLAY");
            if (string.IsNullOrEmpty(display)) return null;

            // Extract display number: ":0" -> "0", ":1.0" -> "1"
            string displayNum = "0";
            int colonIdx = display.IndexOf(':');
            if (colonIdx >= 0)
            {
                string rest = display[(colonIdx + 1)..];
                int dotIdx = rest.IndexOf('.');
                displayNum = dotIdx >= 0 ? rest[..dotIdx] : rest;
            }

            // The file is named like: <machine-id>-unix-<n>
            string pattern = $"{machineId}-unix-{displayNum}";
            string filePath = Path.Combine(ibusDir, pattern);

            if (!File.Exists(filePath))
            {
                // Try to find any matching file
                var files = Directory.GetFiles(ibusDir, $"{machineId}*");
                if (files.Length == 0) return null;
                filePath = files[0];
            }

            foreach (var line in File.ReadAllLines(filePath))
            {
                if (line.StartsWith("IBUS_ADDRESS=", StringComparison.Ordinal))
                    return line[13..];
            }
        }
        catch (Exception ex)
        {
            Logger.Write($"ReadIBusAddress error: {ex.Message}");
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            // Destroy input context
            if (_conn != null && _inputContextPath != null)
            {
                uint serial = _conn.NextSerial();
                var msg = DBusWriter.BuildMethodCall(
                    serial,
                    "org.freedesktop.IBus",
                    _inputContextPath,
                    "org.freedesktop.IBus.InputContext",
                    "Destroy");
                _conn.Send(msg);
            }
        }
        catch { }

        _conn?.Dispose();
        _conn = null;
    }
}
