namespace RemoteGamepadServer;

using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

/// <summary>
/// Virtual Xbox 360 controller output layer using ViGEm.
///
/// Maps parsed Android controller input to a virtual Xbox 360 gamepad
/// visible to Windows games and applications.
/// </summary>
public class VirtualController : IDisposable
{
    private ViGEmClient? _client;
    private IXbox360Controller? _controller;
    private bool _available;

    public bool IsAvailable => _available;

    /// <summary>
    /// Attempt to create and connect the virtual controller.
    /// If ViGEm is not installed, logs a warning and continues in passthrough mode.
    /// </summary>
    public void Initialize()
    {
        try
        {
            _client = new ViGEmClient();
            _controller = _client.CreateXbox360Controller();
            _controller.Connect();
            _available = true;
            Console.WriteLine("[VIGEM] Virtual Xbox 360 controller connected");
        }
        catch (Exception ex)
        {
            _available = false;
            Console.WriteLine($"[VIGEM] WARNING: Could not initialize virtual controller. " +
                $"Ensure ViGEmBus driver is installed. Error: {ex.Message}");
            Console.WriteLine("[VIGEM] Server will continue in passthrough mode (no virtual output).");
        }
    }

    /// <summary>
    /// Apply a parsed controller state to the virtual Xbox 360 gamepad.
    /// </summary>
    public void Update(InputParser.ControllerState state)
    {
        if (!_available || _controller == null) return;

        // Buttons
        _controller.SetButtonState(Xbox360Button.A, state.A);
        _controller.SetButtonState(Xbox360Button.B, state.B);
        _controller.SetButtonState(Xbox360Button.X, state.X);
        _controller.SetButtonState(Xbox360Button.Y, state.Y);

        _controller.SetButtonState(Xbox360Button.LeftShoulder, state.Lb);
        _controller.SetButtonState(Xbox360Button.RightShoulder, state.Rb);

        _controller.SetButtonState(Xbox360Button.Start, state.Start);
        _controller.SetButtonState(Xbox360Button.Back, state.Select);

        _controller.SetButtonState(Xbox360Button.LeftThumb, state.L3);
        _controller.SetButtonState(Xbox360Button.RightThumb, state.R3);

        // D-pad
        _controller.SetButtonState(Xbox360Button.Up, state.DUp);
        _controller.SetButtonState(Xbox360Button.Down, state.DDown);
        _controller.SetButtonState(Xbox360Button.Left, state.DLeft);
        _controller.SetButtonState(Xbox360Button.Right, state.DRight);

        // Axes: float [-1, 1] → short [-32768, 32767]
        _controller.SetAxisValue(Xbox360Axis.LeftThumbX, FloatToShort(state.Lx));
        _controller.SetAxisValue(Xbox360Axis.LeftThumbY, FloatToShort(state.Ly));
        _controller.SetAxisValue(Xbox360Axis.RightThumbX, FloatToShort(state.Rx));
        _controller.SetAxisValue(Xbox360Axis.RightThumbY, FloatToShort(state.Ry));

        // Triggers: float [0, 1] → byte [0, 255]
        _controller.SetSliderValue(Xbox360Slider.LeftTrigger, FloatToByte(state.Lt));
        _controller.SetSliderValue(Xbox360Slider.RightTrigger, FloatToByte(state.Rt));

        _controller.SubmitReport();
    }

    /// <summary>
    /// Reset all buttons and axes to neutral.
    /// </summary>
    public void Reset()
    {
        if (!_available || _controller == null) return;

        _controller.SetButtonState(Xbox360Button.A, false);
        _controller.SetButtonState(Xbox360Button.B, false);
        _controller.SetButtonState(Xbox360Button.X, false);
        _controller.SetButtonState(Xbox360Button.Y, false);
        _controller.SetButtonState(Xbox360Button.LeftShoulder, false);
        _controller.SetButtonState(Xbox360Button.RightShoulder, false);
        _controller.SetButtonState(Xbox360Button.Start, false);
        _controller.SetButtonState(Xbox360Button.Back, false);
        _controller.SetButtonState(Xbox360Button.LeftThumb, false);
        _controller.SetButtonState(Xbox360Button.RightThumb, false);
        _controller.SetButtonState(Xbox360Button.Up, false);
        _controller.SetButtonState(Xbox360Button.Down, false);
        _controller.SetButtonState(Xbox360Button.Left, false);
        _controller.SetButtonState(Xbox360Button.Right, false);

        _controller.SetAxisValue(Xbox360Axis.LeftThumbX, 0);
        _controller.SetAxisValue(Xbox360Axis.LeftThumbY, 0);
        _controller.SetAxisValue(Xbox360Axis.RightThumbX, 0);
        _controller.SetAxisValue(Xbox360Axis.RightThumbY, 0);

        _controller.SetSliderValue(Xbox360Slider.LeftTrigger, 0);
        _controller.SetSliderValue(Xbox360Slider.RightTrigger, 0);

        _controller.SubmitReport();
    }

    public void Dispose()
    {
        Reset();
        _controller?.Disconnect();
        _client?.Dispose();
    }

    private static short FloatToShort(float v)
    {
        // Map [-1, 1] to [-32768, 32767]
        return (short)(v * 32767f);
    }

    private static byte FloatToByte(float v)
    {
        // Map [0, 1] to [0, 255]
        return (byte)(v * 255f);
    }
}
