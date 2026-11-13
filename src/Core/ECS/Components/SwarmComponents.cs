using Godot;

namespace GeometrySwarm.Core.ECS.Components;

public enum SwarmState : byte
{
    Orbiting = 0,
    Launched = 1,
    Returning = 2,
    Mining = 3
}

public enum SwarmRole : byte
{
    DartNeedle = 0,
    GigaShuriken = 1,
    HeavyLance = 2,
    CrescentBoomerang = 3,
    MiningDroid = 4,
    Guardian = 5
}

public struct PositionComponent
{
    public Vector2 Value;

    public PositionComponent(Vector2 value) => Value = value;
    public PositionComponent(float x, float y) => Value = new Vector2(x, y);
}

public struct VelocityComponent
{
    public Vector2 Value;

    public VelocityComponent(Vector2 value) => Value = value;
    public VelocityComponent(float x, float y) => Value = new Vector2(x, y);
}

public struct SwarmUnitComponent
{
    public SwarmRole Role;
    public SwarmState State;
    public int OwnerId;          // 0 = Player, 1+ = Enemy Hives
    public float OrbitDistance;  // Jarak orbit ideal dari Core
    public float OrbitAngle;     // Sudut orbit saat ini
    public float OrbitSpeed;     // Kecepatan angular putaran
    public float Energy;         // HP / Durability
    public float LifeTime;       // Timer untuk kembali jika diluncurkan

    public SwarmUnitComponent(SwarmRole role, int ownerId, float orbitDist, float orbitSpd)
    {
        Role = role;
        State = SwarmState.Orbiting;
        OwnerId = ownerId;
        OrbitDistance = orbitDist;
        OrbitAngle = GD.Randf() * Mathf.Tau;
        OrbitSpeed = orbitSpd;
        Energy = 100.0f;
        LifeTime = 0.0f;
    }
}

public struct VisualComponent
{
    public Color Color;
    public float Scale;

    public VisualComponent(Color color, float scale = 1.0f)
    {
        Color = color;
        Scale = scale;
    }
}

