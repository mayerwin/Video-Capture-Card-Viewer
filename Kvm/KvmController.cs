using System.Threading.Channels;
using Avalonia.Input;

namespace VideoCaptureCardViewer.Kvm;

/// <summary>
/// Owns a KVM backend and the live HID state (pressed keys, modifiers, mouse buttons), and
/// translates Avalonia input into HID reports while "grabbed".
///
/// All shared HID state is touched only on the UI thread (every <c>On*</c> entry point is called
/// from the UI thread). Reports are handed to a single-consumer <see cref="Channel{T}"/> pump so
/// they are transmitted strictly in order — this prevents the classic "stuck key / stuck modifier"
/// bug that fire-and-forget writes cause, surfaces serial errors, and lets mouse-moves coalesce.
///
/// A PC cannot emulate USB HID by itself, so a real backend (CH9329 / Flipper) drives external
/// hardware; Loopback just logs.
/// </summary>
public sealed class KvmController : IAsyncDisposable
{
    private enum Kind { Keyboard, Mouse, Move }

    private readonly struct Item
    {
        public readonly Kind Kind;
        public readonly byte Modifiers;
        public readonly byte[] Keys;
        public readonly int X, Y;
        public readonly byte Buttons;
        public readonly sbyte Wheel;

        private Item(Kind k, byte mod, byte[] keys, int x, int y, byte btn, sbyte wheel)
        { Kind = k; Modifiers = mod; Keys = keys; X = x; Y = y; Buttons = btn; Wheel = wheel; }

        public static Item Keyboard(byte mod, byte[] keys) => new(Kind.Keyboard, mod, keys, 0, 0, 0, 0);
        public static Item Mouse(int x, int y, byte btn, sbyte wheel) => new(Kind.Mouse, 0, System.Array.Empty<byte>(), x, y, btn, wheel);
        public static Item Move(byte btn) => new(Kind.Move, 0, System.Array.Empty<byte>(), 0, 0, btn, 0);
    }

    private IKvmBackend? _backend;
    private Channel<Item>? _channel;
    private Task? _pump;
    private volatile bool _faulted;
    private readonly SemaphoreSlim _gate = new(1, 1); // serializes connect/disconnect

    // HID state — UI thread only.
    private readonly List<byte> _pressed = new(6);
    private byte _modifiers;
    private byte _mouseButtons;

    // Latest pending mouse-move (coalesced); guarded because the pump reads it.
    private readonly object _moveLock = new();
    private int _moveX, _moveY;
    private bool _moveQueued;

    public event Action<string>? Log;
    public event Action? StateChanged;

    public bool IsConnected => _backend?.IsConnected == true && !_faulted;
    public string? BackendName => _backend?.Name;
    public bool IsGrabbed { get; private set; }

    public async Task ConnectAsync(KvmConnectionOptions options)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync().ConfigureAwait(false);

            var backend = options.Kind switch
            {
                KvmBackendKind.Ch9329 => (IKvmBackend)new Ch9329Backend(),
                KvmBackendKind.FlipperZero => new FlipperZeroBackend(),
                _ => new LoopbackBackend(m => Log?.Invoke(m)),
            };

            await backend.ConnectAsync(options).ConfigureAwait(false);

            _backend = backend;
            _faulted = false;
            var channel = Channel.CreateUnbounded<Item>(new UnboundedChannelOptions { SingleReader = true });
            _channel = channel;
            _pump = Task.Run(() => PumpAsync(backend, channel));

            Log?.Invoke($"{backend.Name} connected.");
            StateChanged?.Invoke();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await DisconnectCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task DisconnectCoreAsync()
    {
        IsGrabbed = false;

        var channel = _channel;
        if (channel is not null)
        {
            ReleaseAll();                 // enqueue zero reports first
            channel.Writer.TryComplete(); // then let the pump drain and exit
        }

        var pump = _pump;
        if (pump is not null)
        {
            try { await pump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch { /* timed out / faulted — proceed to teardown */ }
        }

        if (_backend is not null)
        {
            try { await _backend.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
        }

        _backend = null;
        _channel = null;
        _pump = null;
        ResetState();
        StateChanged?.Invoke();
    }

    private async Task PumpAsync(IKvmBackend backend, Channel<Item> channel)
    {
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                switch (item.Kind)
                {
                    case Kind.Keyboard:
                        await backend.SendKeyboardAsync(item.Modifiers, item.Keys).ConfigureAwait(false);
                        break;
                    case Kind.Mouse:
                        await backend.SendMouseAbsoluteAsync(item.X, item.Y, item.Buttons, item.Wheel).ConfigureAwait(false);
                        break;
                    case Kind.Move:
                        int x, y;
                        lock (_moveLock) { _moveQueued = false; x = _moveX; y = _moveY; }
                        await backend.SendMouseAbsoluteAsync(x, y, item.Buttons, 0).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _faulted = true;
            Log?.Invoke($"KVM transport error: {ex.Message}. Disconnecting.");
            StateChanged?.Invoke();
            // Release the (now dead) port so a reconnect to the same COM port isn't blocked.
            _ = DisconnectAsync();
        }
    }

    public void SetGrab(bool grab)
    {
        if (!IsConnected) { IsGrabbed = false; return; }
        if (IsGrabbed == grab) return;

        IsGrabbed = grab;
        if (!grab) ReleaseAll();
        Log?.Invoke(grab
            ? "KVM grabbed - input is going to the target. Press Scroll Lock to release."
            : "KVM released - input is local again.");
        StateChanged?.Invoke();
    }

    public void ToggleGrab() => SetGrab(!IsGrabbed);

    // ----- keyboard (UI thread) -----

    public bool OnKeyDown(Key key)
    {
        if (!IsGrabbed || !IsConnected) return false;

        bool changed = false;
        if (HidKeyMap.TryGetModifier(key, out var bit))
        {
            var nm = (byte)(_modifiers | bit);
            if (nm != _modifiers) { _modifiers = nm; changed = true; }
        }
        else if (HidKeyMap.TryGetUsage(key, out var usage))
        {
            if (!_pressed.Contains(usage))
            {
                if (_pressed.Count >= 6) _pressed.RemoveAt(0);
                _pressed.Add(usage);
                changed = true;
            }
        }
        if (changed) EnqueueKeyboard();
        return true; // while grabbed, swallow everything so it doesn't act locally
    }

    public bool OnKeyUp(Key key)
    {
        if (!IsGrabbed || !IsConnected) return false;

        bool changed = false;
        if (HidKeyMap.TryGetModifier(key, out var bit))
        {
            var nm = (byte)(_modifiers & ~bit);
            if (nm != _modifiers) { _modifiers = nm; changed = true; }
        }
        else if (HidKeyMap.TryGetUsage(key, out var usage))
        {
            changed = _pressed.Remove(usage);
        }
        if (changed) EnqueueKeyboard();
        return true;
    }

    private void EnqueueKeyboard() => _channel?.Writer.TryWrite(Item.Keyboard(_modifiers, _pressed.ToArray()));

    // ----- mouse (UI thread) -----

    public void OnMouseMove(int xNorm, int yNorm)
    {
        if (!IsGrabbed || !IsConnected) return;
        bool enqueue;
        lock (_moveLock)
        {
            _moveX = xNorm; _moveY = yNorm;
            enqueue = !_moveQueued;
            if (enqueue) _moveQueued = true;
        }
        if (enqueue) _channel?.Writer.TryWrite(Item.Move(_mouseButtons));
    }

    public void OnMouseButton(KvmMouseButtons button, bool down, int xNorm, int yNorm)
    {
        if (!IsGrabbed || !IsConnected) return;
        if (down) _mouseButtons |= (byte)button;
        else _mouseButtons &= (byte)~(byte)button;
        // Keep the coalesced-move coordinate in sync so a pending Move can't overwrite this position.
        lock (_moveLock) { _moveX = xNorm; _moveY = yNorm; _moveQueued = false; }
        _channel?.Writer.TryWrite(Item.Mouse(xNorm, yNorm, _mouseButtons, 0));
    }

    public void OnWheel(int xNorm, int yNorm, double delta)
    {
        if (!IsGrabbed || !IsConnected) return;
        sbyte w = (sbyte)Math.Clamp((int)Math.Round(delta), -127, 127);
        if (w == 0) return;
        lock (_moveLock) { _moveX = xNorm; _moveY = yNorm; _moveQueued = false; }
        _channel?.Writer.TryWrite(Item.Mouse(xNorm, yNorm, _mouseButtons, w));
    }

    private void ReleaseAll()
    {
        ResetState();
        _channel?.Writer.TryWrite(Item.Keyboard(0, System.Array.Empty<byte>()));
        _channel?.Writer.TryWrite(Item.Mouse(0, 0, 0, 0));
    }

    private void ResetState()
    {
        _pressed.Clear();
        _modifiers = 0;
        _mouseButtons = 0;
        lock (_moveLock) { _moveQueued = false; }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
