namespace RemoteGamepadServer;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Parses INPUT|{json} messages from Android into a structured controller state.
/// </summary>
public static class InputParser
{
    /// <summary>
    /// Raw DTO matching the JSON sent by Android GamepadState.toProtocolMessage().
    /// </summary>
    public class GamepadInputDto
    {
        [JsonPropertyName("lx")]  public float Lx { get; set; }
        [JsonPropertyName("ly")]  public float Ly { get; set; }
        [JsonPropertyName("rx")]  public float Rx { get; set; }
        [JsonPropertyName("ry")]  public float Ry { get; set; }
        [JsonPropertyName("lt")]  public float Lt { get; set; }
        [JsonPropertyName("rt")]  public float Rt { get; set; }
        [JsonPropertyName("a")]   public bool A { get; set; }
        [JsonPropertyName("b")]   public bool B { get; set; }
        [JsonPropertyName("x")]   public bool X { get; set; }
        [JsonPropertyName("y")]   public bool Y { get; set; }
        [JsonPropertyName("lb")]  public bool Lb { get; set; }
        [JsonPropertyName("rb")]  public bool Rb { get; set; }
        [JsonPropertyName("dUp")]    public bool DUp { get; set; }
        [JsonPropertyName("dDown")]  public bool DDown { get; set; }
        [JsonPropertyName("dLeft")]  public bool DLeft { get; set; }
        [JsonPropertyName("dRight")] public bool DRight { get; set; }
        [JsonPropertyName("start")]  public bool Start { get; set; }
        [JsonPropertyName("select")] public bool Select { get; set; }
        [JsonPropertyName("l3")]     public bool L3 { get; set; }
        [JsonPropertyName("r3")]     public bool R3 { get; set; }
    }

    /// <summary>
    /// Normalised controller state used by the virtual controller layer.
    /// </summary>
    public readonly record struct ControllerState(
        float Lx, float Ly, float Rx, float Ry,
        float Lt, float Rt,
        bool A, bool B, bool X, bool Y,
        bool Lb, bool Rb,
        bool DUp, bool DDown, bool DLeft, bool DRight,
        bool Start, bool Select,
        bool L3, bool R3
    );

    /// <summary>
    /// Parse a raw protocol message. Returns null if not an INPUT message or parse fails.
    /// </summary>
    public static ControllerState? Parse(string message)
    {
        var trimmed = message.Trim();
        var json = trimmed.StartsWith(Protocol.MSG_INPUT + "|", StringComparison.Ordinal)
            ? trimmed[(Protocol.MSG_INPUT.Length + 1)..]
            : trimmed.StartsWith('{') ? trimmed : null;
        if (json is null) return null;
        try
        {
            var dto = JsonSerializer.Deserialize<GamepadInputDto>(json);
            if (dto == null) return null;

            return new ControllerState(
                Lx: ClampAxis(dto.Lx), Ly: ClampAxis(dto.Ly),
                Rx: ClampAxis(dto.Rx), Ry: ClampAxis(dto.Ry),
                Lt: ClampTrigger(dto.Lt), Rt: ClampTrigger(dto.Rt),
                A: dto.A, B: dto.B, X: dto.X, Y: dto.Y,
                Lb: dto.Lb, Rb: dto.Rb,
                DUp: dto.DUp, DDown: dto.DDown, DLeft: dto.DLeft, DRight: dto.DRight,
                Start: dto.Start, Select: dto.Select,
                L3: dto.L3, R3: dto.R3
            );
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[INPUT] Parse error: {ex.Message}");
            return null;
        }
    }

    private static float ClampAxis(float v) => v < -1f ? -1f : (v > 1f ? 1f : v);
    private static float ClampTrigger(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
}
