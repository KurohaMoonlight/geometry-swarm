using System;
using System.Collections.Generic;
using Godot;

namespace GeometrySwarm.Gameplay;

/// <summary>
/// Kapal Induk Musuh (Enemy Hive Core) bertema crimson-magenta neon.
/// Berdiri berhadapan dengan Player Core sebagai markas komando swarm musuh.
/// Dilengkapi sistem pertahanan darurat (Emergency Nemesis Escape Pod & Mining Drone Reboot).
/// </summary>
public partial class EnemyCore : Node2D
{
    [Signal]
    public delegate void EnemyCoreDamageTakenEventHandler(float currentHealth, float maxHealth);

    [Signal]
    public delegate void EnemyEscapePodEjectedEventHandler(Vector2 ejectionPos, Vector2 ejectionVelocity);

    [Signal]
    public delegate void EnemyCoreDestroyedEventHandler();

    [Export] public float MaxHealth { get; set; } = 4000.0f;
    public float Health { get; private set; }

    public const float HexRadius = 16.0f;
    public float CoreCollisionRadius => IsEscapePod ? 22.0f : 42.0f;

    public bool IsEscapePod { get; private set; } = false;
    public float InvulnerableTimer { get; private set; } = 0.0f;
    public Vector2 Velocity { get; set; } = Vector2.Zero;

    private readonly List<Vector2> _hexCellOffsets = new();
    private readonly List<Vector2[]> _cachedHexPolylines = new();

    private static readonly Vector2I[] HexDirections = new Vector2I[]
    {
        new(0, 0),
        new(1, 0),
        new(1, -1),
        new(0, -1),
        new(-1, 0),
        new(-1, 1),
        new(0, 1),
    };

    private static readonly Color EnemyAuraColor = new(2.2f, 0.15f, 0.65f, 0.45f);
    private static readonly Color EnemyLineColor = new(3.5f, 0.35f, 0.9f, 1.0f);
    private static readonly Color EnemyPulseColor = new(4.2f, 0.75f, 1.3f, 1.0f);
    private static readonly Color HitFlashColor = new(4.8f, 4.2f, 4.0f, 1.0f);

    private float _pulseTimer = 0.0f;
    private float _hitFlashTimer = 0.0f;

    public override void _Ready()
    {
        Health = MaxHealth;
        BuildHoneycombCluster();
    }

    private void BuildHoneycombCluster()
    {
        _hexCellOffsets.Clear();
        _cachedHexPolylines.Clear();

        for (int i = 0; i < HexDirections.Length; i++)
        {
            Vector2 offset = AxialToWorldOffset(HexDirections[i]);
            _hexCellOffsets.Add(offset);
            _cachedHexPolylines.Add(BuildHexPoints(offset, HexRadius));
        }
    }

    private static Vector2 AxialToWorldOffset(Vector2I axial)
    {
        float x = MathF.Sqrt(3.0f) * HexRadius * (axial.X + axial.Y * 0.5f);
        float y = 1.5f * HexRadius * axial.Y;
        return new Vector2(x, y);
    }

    private static Vector2[] BuildHexPoints(Vector2 center, float radius)
    {
        Vector2[] points = new Vector2[7];
        for (int i = 0; i < 6; i++)
        {
            float angle = Mathf.DegToRad(60.0f * i - 30.0f);
            points[i] = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
        }
        points[6] = points[0];
        return points;
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        _pulseTimer += dt * 3.2f;

        if (_hitFlashTimer > 0.0f)
        {
            _hitFlashTimer -= dt;
        }
        if (InvulnerableTimer > 0.0f)
        {
            InvulnerableTimer -= dt;
        }

        // Manuver pelarian AI jika berada dalam mode Escape Pod tunggal
        if (IsEscapePod)
        {
            Node? playerNode = GetTree()?.Root?.FindChild("PlayerCore", true, false);
            if (playerNode is Node2D player2D)
            {
                Vector2 toPlayer = player2D.GlobalPosition - GlobalPosition;
                float dist = toPlayer.Length();
                if (dist < 1500.0f && dist > 1.0f)
                {
                    Vector2 fleeDir = -toPlayer / dist;
                    Velocity += fleeDir * (420.0f * dt);
                }
            }
            Velocity = Velocity.LimitLength(360.0f);
        }

        GlobalPosition += Velocity * dt;
        Velocity *= MathF.Pow(0.92f, dt * 60.0f);

        // Pantulan fisik terhadap batuan asteroid agar tidak tembus/terjebak
        var asteroids = Asteroid.ActiveAsteroids;
        for (int i = 0; i < asteroids.Count; i++)
        {
            var ast = asteroids[i];
            if (ast == null || !ast.IsActiveInTree) continue;

            Vector2 curPos = GlobalPosition;
            Vector2 curVel = Velocity;
            if (ast.ResolveCircleVoxelCollision(ref curPos, ref curVel, CoreCollisionRadius, impulseWeight: 0.35f))
            {
                GlobalPosition = curPos;
                Velocity = curVel;
            }
        }

        QueueRedraw();
    }

    public bool TakeDamage(float amount, Vector2 impactDir)
    {
        if (InvulnerableTimer > 0.0f) return false;

        Health = MathF.Max(0.0f, Health - amount);
        _hitFlashTimer = 0.10f;
        EmitSignal(SignalName.EnemyCoreDamageTaken, Health, MaxHealth);

        if (Health <= 0.0f)
        {
            if (!IsEscapePod)
            {
                TriggerEmergencyEscape(impactDir);
                return true;
            }
            else
            {
                EmitSignal(SignalName.EnemyCoreDestroyed);
                return false;
            }
        }
        return false;
    }

    public void TriggerEmergencyEscape(Vector2 impactDir)
    {
        IsEscapePod = true;

        // Sarang luar hancur total, menyisakan 1 sel inti Nemesis Pod tunggal
        _hexCellOffsets.Clear();
        _cachedHexPolylines.Clear();
        _hexCellOffsets.Add(Vector2.Zero);
        _cachedHexPolylines.Add(BuildHexPoints(Vector2.Zero, HexRadius));

        Health = 800.0f;
        InvulnerableTimer = 2.5f;

        Vector2 escapeDir = impactDir.LengthSquared() > 0.01f
            ? -impactDir.Normalized()
            : new Vector2(1.0f, (GD.Randf() - 0.5f)).Normalized();

        Velocity = escapeDir * 700.0f;

        EmitSignal(SignalName.EnemyEscapePodEjected, GlobalPosition, Velocity);
        QueueRedraw();
    }

    public override void _Draw()
    {
        bool isHit = _hitFlashTimer > 0.0f;
        Color lineColor = isHit ? HitFlashColor : EnemyLineColor;
        Color auraColor = isHit ? HitFlashColor : EnemyAuraColor;

        float pulse = 0.85f + 0.15f * MathF.Sin(_pulseTimer);
        float coreGlowRadius = 3.2f * pulse;

        for (int i = 0; i < _hexCellOffsets.Count; i++)
        {
            Vector2 offset = _hexCellOffsets[i];
            Vector2[] points = _cachedHexPolylines[i];

            DrawPolyline(points, auraColor, 3.2f, false);
            DrawPolyline(points, lineColor, 1.5f, false);
            DrawCircle(offset, coreGlowRadius, EnemyPulseColor);
        }

        // Perisai Darurat Invulnerability Flash
        if (InvulnerableTimer > 0.0f)
        {
            float invPulse = 0.5f + 0.5f * MathF.Sin(InvulnerableTimer * 26.0f);
            Color invColor = new Color(4.5f, 0.4f, 2.2f, 0.50f + 0.35f * invPulse);
            DrawArc(Vector2.Zero, HexRadius * 1.55f, 0.0f, Mathf.Tau, 32, invColor, 2.5f);
        }

        // Strobe Peringatan Bahaya jika sedang menjadi Nemesis Escape Pod
        if (IsEscapePod)
        {
            float alarm = 0.5f + 0.5f * MathF.Sin((float)Time.GetTicksMsec() * 0.014f);
            Color alarmCol = new Color(4.8f, 0.3f, 0.8f, alarm * 0.75f);
            DrawArc(Vector2.Zero, HexRadius * 1.25f, 0.0f, Mathf.Tau, 24, alarmCol, 1.6f);
        }

        // Lingkaran batas sensor perisai inti
        DrawArc(Vector2.Zero, CoreCollisionRadius, 0.0f, Mathf.Tau, 32, new Color(EnemyLineColor.R, EnemyLineColor.G, EnemyLineColor.B, 0.28f * pulse), 1.2f);
    }
}

