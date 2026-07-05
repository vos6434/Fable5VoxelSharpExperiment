using Silk.NET.Input;

namespace Voxel.Client;

/// <summary>
/// Creative-flight camera, matching the web client's controls: mouselook
/// while the cursor is captured, WASD in the yaw plane, Space/Shift for
/// vertical, Ctrl to sprint.
/// </summary>
public sealed class FlyCamera
{
    private const float BaseSpeed = 10f;   // blocks per second
    private const float SprintMultiplier = 3f;
    private const float MouseSensitivity = 0.0022f;

    public float X, Y, Z;
    public float Yaw, Pitch;
    public bool Captured;

    public void ApplyMouseDelta(float dx, float dy)
    {
        if (!Captured) return;
        Yaw -= dx * MouseSensitivity;
        Pitch -= dy * MouseSensitivity;
        float limit = MathF.PI / 2 - 0.01f;
        Pitch = Math.Clamp(Pitch, -limit, limit);
    }

    public void Update(IKeyboard keyboard, float dt)
    {
        if (!Captured) return;

        float mx = (keyboard.IsKeyPressed(Key.D) ? 1 : 0) - (keyboard.IsKeyPressed(Key.A) ? 1 : 0);
        float my = (keyboard.IsKeyPressed(Key.Space) ? 1 : 0) - (keyboard.IsKeyPressed(Key.ShiftLeft) ? 1 : 0);
        float mz = (keyboard.IsKeyPressed(Key.S) ? 1 : 0) - (keyboard.IsKeyPressed(Key.W) ? 1 : 0);
        if (mx == 0 && my == 0 && mz == 0) return;

        float len = MathF.Sqrt(mx * mx + my * my + mz * mz);
        mx /= len; my /= len; mz /= len;

        float speed = BaseSpeed * (keyboard.IsKeyPressed(Key.ControlLeft) ? SprintMultiplier : 1);
        float sin = MathF.Sin(Yaw);
        float cos = MathF.Cos(Yaw);
        // WASD moves in the yaw plane; vertical stays world-aligned.
        X += (mx * cos + mz * sin) * speed * dt;
        Z += (mz * cos - mx * sin) * speed * dt;
        Y += my * speed * dt;
    }
}
