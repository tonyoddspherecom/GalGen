using System.Numerics;

namespace UniverseGen.Models;

/// <summary>
/// First-person camera for navigating galaxy normalised space.
/// +X and +Z are in the galactic plane; +Y is the galactic north pole.
/// Yaw = 0 looks along +Z; Pitch > 0 looks upward.
/// </summary>
public struct Camera3D
{
    public Vector3 Position;
    public float   Yaw;        // radians
    public float   Pitch;      // radians, clamped to ±89°
    public float   FovRadians;

    public Camera3D()
    {
        // Default: sitting in a spiral arm, looking toward galactic centre
        Position  = new Vector3(0.35f, 0.015f, 0f);
        Yaw       = MathF.PI;   // looking along -Z toward origin
        Pitch     = -0.08f;
        FovRadians = MathF.PI / 3f;  // 60°
    }

    public readonly Vector3 Forward
    {
        get
        {
            float cp = MathF.Cos(Pitch), sp = MathF.Sin(Pitch);
            return new Vector3(cp * MathF.Sin(Yaw), -sp, cp * MathF.Cos(Yaw));
        }
    }

    public readonly Vector3 Right =>
        Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitY));

    public void MoveForward(float delta) => Position += Forward * delta;
    public void MoveRight(float delta)   => Position += Right   * delta;
    public void MoveUp(float delta)      => Position += Vector3.UnitY * delta;

    public void Look(float dyaw, float dpitch)
    {
        Yaw  += dyaw;
        Pitch = Math.Clamp(Pitch + dpitch, -MathF.PI / 2f + 0.01f, MathF.PI / 2f - 0.01f);
    }

    /// Projects a world-space point into screen coordinates.
    /// Returns NaN components if the point is behind the camera.
    public readonly (float ScreenX, float ScreenY, float Depth) Project(
        Vector3 world, int screenW, int screenH)
    {
        Vector3 rel   = world - Position;
        Vector3 fwd   = Forward;
        Vector3 right = Right;
        Vector3 up    = Vector3.Cross(right, fwd);

        float cX = Vector3.Dot(rel, right);
        float cY = Vector3.Dot(rel, up);
        float cZ = Vector3.Dot(rel, fwd);

        if (cZ <= 0.000002f) return (float.NaN, float.NaN, float.NaN);  // near-plane: ~0.1 ly

        float tanH = MathF.Tan(FovRadians / 2f);
        float tanV = tanH * screenH / screenW;

        float sx = cX / (cZ * tanH) * (screenW / 2f) + screenW / 2f;
        float sy = -cY / (cZ * tanV) * (screenH / 2f) + screenH / 2f;

        return (sx, sy, cZ);
    }
}
