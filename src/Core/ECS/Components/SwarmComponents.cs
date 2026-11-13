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
    DrillDrone = 2,
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
    public int OwnerId;             // 0 = Player, 1+ = Enemy Hives
    public float OrbitDistance;     // Jarak orbit ideal dari Core
    public float OrbitAngle;        // Sudut orbit saat ini
    public float OrbitSpeed;        // Kecepatan angular putaran
    public float Energy;            // HP / Durability
    public float LifeTime;          // Timer durasi terbang
    public float MaxLifeTime;       // Batas waktu sebelum otomatis kembali
    public Vector2 TargetDirection; // Vektor arah tembakan peluru
    public float SpinAngle;         // Rotasi visual mandiri
    public float SpinRate;          // Kecepatan rotasi (misal putaran Shuriken/Bor)
    public float ShieldTargetAngle; // Sudut target pertahanan pada ring terluar
    public int FormationIndex;      // Indeks urutan formasi (misal slot pada Super Giga Drill)

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
        MaxLifeTime = 1.3f;
        TargetDirection = Vector2.Right;
        SpinAngle = GD.Randf() * Mathf.Tau;
        
        // Spin rate bervariasi sesuai tipe peran
        SpinRate = role switch
        {
            SwarmRole.GigaShuriken => 24.0f,
            SwarmRole.DrillDrone => 32.0f,
            SwarmRole.CrescentBoomerang => 12.0f,
            _ => 0.0f
        };
    }
}

public struct VisualComponent
{
    public Color Color;
    public float Scale;
    public Color BaseColor; // Warna default berdasarkan peran

    public VisualComponent(Color color, float scale = 1.0f)
    {
        Color = color;
        BaseColor = color;
        Scale = scale;
    }
}
