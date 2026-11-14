using System;
using System.Collections.Generic;
using Godot;

namespace GeometrySwarm.Gameplay.Enemy;

/// <summary>
/// Kapal Induk Musuh (Enemy Hive Core) bertema crimson-magenta neon.
/// Berdiri berhadapan dengan Player Core sebagai markas komando swarm musuh.
/// - Mode Hive Core: Massa kapal induk sangat berat (Heavy Mass Inertia).
/// - Mode Emergency Escape Pod (1 Core tersisa): Terlontar kabur dengan kecepatan sangat tinggi
///   menjauhi pemain, memproduksi 1 Miner otomatis, dan bermanuver lincah menghindari kejaran.
/// </summary>
public partial class EnemyCore : Node2D
{
    [Signal]
    public delegate void EnemyCoreDamageTakenEventHandler(float currentHealth, float maxHealth);

    [Signal]
    public delegate void EnemyEscapePodEjectedEventHandler(Vector2 ejectionPos, Vector2 ejectionVelocity);

    [Signal]
    public delegate void EnemyCoreDestroyedEventHandler();

    [Export] public float BaseHealthPerCell { get; set; } = 600.0f;
    public float MaxHealth => MathF.Max(600.0f, _hexCellOffsets.Count * BaseHealthPerCell);
    public float Health { get; private set; } = 600.0f;

    public const float HexRadius = 16.0f;
    public float CoreCollisionRadius => HexRadius * 0.92f;

    [Export] public float Acceleration { get; set; } = 2200.0f;
    [Export] public float Friction { get; set; } = 0.91f;
    [Export] public float MaxSpeed { get; set; } = 420.0f;
    [Export] public float EscapePodMaxSpeed { get; set; } = 580.0f;

    public bool IsEscapePod { get; private set; } = false;
    public float InvulnerableTimer { get; private set; } = 0.0f;
    public float EscapeBurstTimer { get; private set; } = 0.0f;
    public Vector2 EscapeBurstDirection { get; private set; } = Vector2.Right;
    public Vector2 Velocity { get; set; } = Vector2.Zero;

    private readonly List<Vector2> _hexCellOffsets = new();
    private readonly List<Vector2[]> _cachedHexPolylines = new();

    private static readonly Color EnemyAuraColor = new(2.2f, 0.15f, 0.65f, 0.45f);
    private static readonly Color EnemyLineColor = new(3.5f, 0.35f, 0.9f, 1.0f);
    private static readonly Color EnemyPulseColor = new(4.2f, 0.75f, 1.3f, 1.0f);
    private static readonly Color HitFlashColor = new(4.8f, 4.2f, 4.0f, 1.0f);
    private static readonly Color EnemyHullFillColor = new(0.02f, 0.01f, 0.035f, 0.98f);

    private float _pulseTimer = 0.0f;
    private float _hitFlashTimer = 0.0f;

    public override void _Ready()
    {
        ZIndex = 2; // Berada di antara Back Ring (ZIndex=1) dan Front Ring (ZIndex=3)
        BuildHoneycombCluster();
        Health = MaxHealth;
    }

    private void BuildHoneycombCluster()
    {
        _hexCellOffsets.Clear();
        _cachedHexPolylines.Clear();
        _hexCellOffsets.Add(Vector2.Zero);
        _cachedHexPolylines.Add(BuildHexPoints(Vector2.Zero, HexRadius));
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
        if (EscapeBurstTimer > 0.0f)
        {
            EscapeBurstTimer -= dt;
        }

        // Manuver gerak Escape Pod tunggal (1 Core) atau Hive Core
        if (IsEscapePod)
        {
            if (EscapeBurstTimer > 0.0f)
            {
                // Fase Lontaran Darurat Awal (Eject Burst): Lari sangat cepat (~1.180 px/s) selama 1.45 detik
                Velocity += EscapeBurstDirection * (1850.0f * dt);
                Velocity = Velocity.LimitLength(1180.0f);
                GlobalPosition += Velocity * dt;
                Velocity *= MathF.Pow(0.985f, dt * 60.0f);
            }
            else
            {
                // Setelah fase burst selesai: Kecepatan kembali normal (EscapePodMaxSpeed = 580 px/s) secara mulus
                Vector2 fleeDir = EscapeBurstDirection;
                Node? playerNode = GetTree()?.Root?.FindChild("PlayerCore", true, false);
                if (playerNode is Node2D player2D)
                {
                    Vector2 awayFromPlayer = GlobalPosition - player2D.GlobalPosition;
                    if (awayFromPlayer.LengthSquared() > 1.0f)
                    {
                        fleeDir = awayFromPlayer.Normalized();
                    }
                }

                // Kemudi kabur dengan akselerasi normal
                Velocity += fleeDir * (Acceleration * 0.75f * dt);

                float curSpeed = Velocity.Length();
                if (curSpeed > EscapePodMaxSpeed)
                {
                    float dampedSpeed = Mathf.Lerp(curSpeed, EscapePodMaxSpeed, MathF.Min(1.0f, 6.0f * dt));
                    Velocity = (Velocity / curSpeed) * dampedSpeed;
                }

                GlobalPosition += Velocity * dt;
                Velocity *= MathF.Pow(Friction, dt * 60.0f);
            }

            // Hindari batas luar sektor (5.100 px) dengan berbelok tangensial agar tidak terpojok di sudut map
            const float safeArenaBound = 5100.0f;
            if (MathF.Abs(GlobalPosition.X) > safeArenaBound || MathF.Abs(GlobalPosition.Y) > safeArenaBound)
            {
                Vector2 toCenter = (-GlobalPosition).Normalized();
                Vector2 tangent = new Vector2(-toCenter.Y, toCenter.X);
                Velocity += (toCenter * 0.65f + tangent * 0.75f).Normalized() * (900.0f * dt);
            }
        }
        else
        {
            // Mode Hive Core normal: Peredaman aerodinamis halus identik pemain
            float curSpeed = Velocity.Length();
            if (curSpeed > MaxSpeed)
            {
                float dampedSpeed = Mathf.Lerp(curSpeed, MaxSpeed, MathF.Min(1.0f, 6.0f * dt));
                Velocity = (Velocity / curSpeed) * dampedSpeed;
            }
            GlobalPosition += Velocity * dt;
            Velocity *= MathF.Pow(Friction, dt * 60.0f);
        }

        // Pantulan fisik & seluncur tangensial terhadap batuan asteroid agar Escape Pod tidak nyangkut saat kabur
        var asteroids = Asteroid.ActiveAsteroids;
        for (int i = 0; i < asteroids.Count; i++)
        {
            var ast = asteroids[i];
            if (ast == null || !ast.IsActiveInTree) continue;

            Vector2 curPos = GlobalPosition;
            Vector2 curVel = Velocity;
            if (ast.ResolveCircleVoxelCollision(ref curPos, ref curVel, CoreCollisionRadius, out Vector2 hitNormal, impulseWeight: 0.35f))
            {
                if (IsEscapePod)
                {
                    Vector2 tangent = new Vector2(-hitNormal.Y, hitNormal.X);
                    if (tangent.Dot(EscapeBurstDirection) < 0.0f) tangent = -tangent;
                    float keepSpeed = MathF.Max(curVel.Length(), 680.0f);
                    curVel = (tangent * 0.85f + hitNormal * 0.15f).Normalized() * keepSpeed;
                    EscapeBurstDirection = curVel.Normalized();
                }
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

        Health = 600.0f;
        InvulnerableTimer = 3.0f;
        EscapeBurstTimer = 1.45f;

        // Pastikan arah eject SELALU menjauhi PlayerCore (bukan berbalik ke arah pemain!)
        Vector2 awayDir = impactDir.LengthSquared() > 0.01f ? impactDir.Normalized() : Vector2.Right;
        Node? playerNode = GetTree()?.Root?.FindChild("PlayerCore", true, false);
        if (playerNode is Node2D player2D)
        {
            Vector2 fromPlayer = GlobalPosition - player2D.GlobalPosition;
            if (fromPlayer.LengthSquared() > 1.0f)
            {
                awayDir = fromPlayer.Normalized();
            }
        }

        // Beri sedikit sudut menghindar acak (~15-25 derajat) agar lintasannya dinamis & sulit ditembak lurus
        float evadeAngle = (GD.Randf() > 0.5f ? 1.0f : -1.0f) * Mathf.DegToRad(18.0f + GD.Randf() * 12.0f);
        EscapeBurstDirection = awayDir.Rotated(evadeAngle).Normalized();
        Velocity = EscapeBurstDirection * 1180.0f;

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

            // Isi siluet bodi sel heksagonal agar swarm di Back Ring (ZIndex=1) tertutup saat melintas di belakang Core
            Vector2[] poly6 = new Vector2[6];
            Array.Copy(points, poly6, 6);
            DrawColoredPolygon(poly6, EnemyHullFillColor);

            DrawPolyline(points, auraColor, 3.2f, false);
            DrawPolyline(points, lineColor, 1.5f, false);
            DrawCircle(offset, coreGlowRadius, EnemyPulseColor);
        }

        // Ekor Thruster Darurat HANYA menyala saat fase Escape Burst aktif (1.45 detik), lalu mati
        if (IsEscapePod && EscapeBurstTimer > 0.0f)
        {
            Vector2 backDir = -Velocity.Normalized();
            Vector2 tangent = new Vector2(-backDir.Y, backDir.X);
            float jetLen = Mathf.Clamp(Velocity.Length() * 0.085f, 24.0f, 95.0f);
            DrawLine(backDir * 12.0f, backDir * (12.0f + jetLen), new Color(4.8f, 0.6f, 2.2f, 0.85f), 5.2f);
            DrawLine(backDir * 12.0f, backDir * (12.0f + jetLen * 0.72f), new Color(5.5f, 4.2f, 4.8f, 0.95f), 2.4f);
            DrawLine(backDir * 14.0f + tangent * 6.0f, backDir * (12.0f + jetLen * 0.5f), new Color(3.8f, 0.4f, 1.8f, 0.65f), 1.8f);
            DrawLine(backDir * 14.0f - tangent * 6.0f, backDir * (12.0f + jetLen * 0.5f), new Color(3.8f, 0.4f, 1.8f, 0.65f), 1.8f);
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
