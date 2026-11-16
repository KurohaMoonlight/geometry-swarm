using System;
using System.Collections.Generic;
using GeometrySwarm.Core.ECS.Components;
using Godot;

namespace GeometrySwarm.Gameplay.Enemy;

/// <summary>
/// Kapal Induk Musuh (Enemy Hive Core) bertema crimson-magenta neon.
/// - Memiliki fitur fisik, ekspansi sel Honeycomb (Mitosis), dan kolisi voxel per-sel yang 100% setara dengan PlayerCore.
/// - Ukuran CoreCollisionRadius dan CoreCellCount otomatis bertambah saat Rival Hive melakukan ekspansi Core di Foundry.
/// - Mode Emergency Escape Pod (1 Core tersisa): Terlontar kabur menjauhi pemain dengan 1 Miner otomatis.
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
    [Export] public bool IsInvincibleDebug { get; set; } = true;
    [Export] public bool ZeroKnockbackDebug { get; set; } = true;
    public float MaxHealth => MathF.Max(600.0f, _hexCellOffsets.Count * BaseHealthPerCell);
    public float Health { get; private set; } = 600.0f;

    public const float HexRadius = 16.0f;
    private float _clusterCollisionRadius = HexRadius * 0.92f;
    public float CoreCollisionRadius => _clusterCollisionRadius;
    public int CoreCellCount => _hexCellOffsets.Count;

    [Export] public float Acceleration { get; set; } = 2200.0f;
    [Export] public float Friction { get; set; } = 0.91f;
    [Export] public float MaxSpeed { get; set; } = 420.0f;
    [Export] public float EscapePodMaxSpeed { get; set; } = 580.0f;

    public bool IsEscapePod { get; private set; } = false;
    public float InvulnerableTimer { get; private set; } = 0.0f;
    public float EscapeBurstTimer { get; private set; } = 0.0f;
    [Export] public float EscapeBuffDuration { get; set; } = 15.0f;
    public float EscapeBuffTimer { get; private set; } = 0.0f;
    public bool HasEscapeSpeedBuff => IsEscapePod && EscapeBuffTimer > 0.0f;
    [Export] public bool ShowEscapeTrail { get; set; } = false;
    public Vector2 EscapeBurstDirection { get; private set; } = Vector2.Right;
    public Vector2 Velocity { get; set; } = Vector2.Zero;

    // Status Perisai Neon Anti-Terpental saat Cosmic Rift Supernova Shockwave
    private float _shockwaveShieldTimer = 0.0f;
    private Vector2 _shockwaveShieldSourcePos = Vector2.Zero;
    public bool HasShockwaveShield => _shockwaveShieldTimer > 0.0f;

    /// <summary>
    /// Mengaktifkan perisai neon anti-terpental yang menghadap ke arah Cosmic Rift / Black Hole.
    /// Menyerap hentakan inersia gelombang kejut supernova agar armada rival tidak terlempar.
    /// </summary>
    public void ActivateShockwaveShield(Vector2 sourcePosition, float duration = 0.55f)
    {
        _shockwaveShieldSourcePos = sourcePosition;
        _shockwaveShieldTimer = MathF.Max(_shockwaveShieldTimer, duration);
        QueueRedraw();
    }

    // Sistem Mitosis (Pembelahan Sel Core Prosedural - Identik 1:1 dengan PlayerCore)
    public bool IsMitosisActive { get; private set; } = false;
    public float MitosisProgress { get; private set; } = 0.0f;
    public float MitosisDuration { get; private set; } = 7.0f;
    public float MitosisTimer { get; private set; } = 0.0f;
    public float MitosisRemainingTime => MathF.Max(0.0f, MitosisDuration - MitosisTimer);

    private static readonly Vector2I[] HexDirections = new Vector2I[]
    {
        new(1, 0),
        new(1, -1),
        new(0, -1),
        new(-1, 0),
        new(-1, 1),
        new(0, 1),
    };

    private readonly HashSet<Vector2I> _occupiedAxials = new();
    private readonly HashSet<Vector2I> _reservedAxials = new();
    private readonly Queue<Vector2I> _mitosisQueue = new();
    private Vector2I _mitosisChosenAxial;
    private Vector2 _mitosisParentOffset;
    private Vector2 _mitosisTargetOffset;

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
        _occupiedAxials.Clear();
        _reservedAxials.Clear();
        _mitosisQueue.Clear();
        IsMitosisActive = false;

        _hexCellOffsets.Clear();
        _cachedHexPolylines.Clear();

        _occupiedAxials.Add(Vector2I.Zero);
        _hexCellOffsets.Add(Vector2.Zero);
        _cachedHexPolylines.Add(BuildHexPoints(Vector2.Zero, HexRadius));
        _clusterCollisionRadius = HexRadius * 0.92f;
    }

    public float GetMitosisDurationForCurrentCores()
    {
        int currentCount = _hexCellOffsets.Count;
        float duration = 7.0f / (1.0f + (currentCount - 1) * 0.40f);
        return MathF.Max(0.75f, duration);
    }

    public void AddNextHoneycombCell()
    {
        List<Vector2I> candidates = new();

        foreach (var cell in _occupiedAxials)
        {
            foreach (var dir in HexDirections)
            {
                var neighbor = cell + dir;
                if (!_occupiedAxials.Contains(neighbor) && !_reservedAxials.Contains(neighbor) && !candidates.Contains(neighbor))
                {
                    candidates.Add(neighbor);
                }
            }
        }

        candidates.Sort((a, b) =>
        {
            int distA = Math.Max(Math.Abs(a.X), Math.Max(Math.Abs(a.Y), Math.Abs(a.X + a.Y)));
            int distB = Math.Max(Math.Abs(b.X), Math.Max(Math.Abs(b.Y), Math.Abs(b.X + b.Y)));
            if (distA != distB) return distA.CompareTo(distB);
            return MathF.Atan2(a.Y, a.X).CompareTo(MathF.Atan2(b.Y, b.X));
        });

        if (candidates.Count > 0)
        {
            var chosen = candidates[0];
            _reservedAxials.Add(chosen);

            if (!IsMitosisActive)
            {
                StartMitosis(chosen);
            }
            else
            {
                _mitosisQueue.Enqueue(chosen);
            }
        }
    }

    private void StartMitosis(Vector2I targetAxial)
    {
        _mitosisChosenAxial = targetAxial;

        Vector2I parentAxial = Vector2I.Zero;
        foreach (var dir in HexDirections)
        {
            var neighbor = targetAxial - dir;
            if (_occupiedAxials.Contains(neighbor))
            {
                parentAxial = neighbor;
                break;
            }
        }

        _mitosisParentOffset = AxialToWorldOffset(parentAxial);
        _mitosisTargetOffset = AxialToWorldOffset(targetAxial);

        MitosisDuration = GetMitosisDurationForCurrentCores();
        MitosisTimer = 0.0f;
        MitosisProgress = 0.0f;
        IsMitosisActive = true;
        QueueRedraw();
    }

    private void CompleteCurrentMitosis()
    {
        _occupiedAxials.Add(_mitosisChosenAxial);
        _hexCellOffsets.Add(_mitosisTargetOffset);
        _cachedHexPolylines.Add(BuildHexPoints(_mitosisTargetOffset, HexRadius));

        float maxDist = HexRadius * 0.92f;
        for (int i = 0; i < _hexCellOffsets.Count; i++)
        {
            maxDist = MathF.Max(maxDist, _hexCellOffsets[i].Length() + HexRadius * 0.92f);
        }
        _clusterCollisionRadius = maxDist;

        _reservedAxials.Remove(_mitosisChosenAxial);

        if (IsEscapePod)
        {
            IsEscapePod = false;
            EscapeBurstTimer = 0.0f;
            EscapeBuffTimer = 0.0f;
        }

        Health = MathF.Min(MaxHealth, Health + BaseHealthPerCell);
        EmitSignal(SignalName.EnemyCoreDamageTaken, Health, MaxHealth);

        if (_mitosisQueue.Count > 0)
        {
            var next = _mitosisQueue.Dequeue();
            StartMitosis(next);
        }
        else
        {
            IsMitosisActive = false;
        }

        QueueRedraw();
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

        // Sandbag Testing Mode: Redam gaya dorong eksternal / ledakan (kebal knockback)
        if (ZeroKnockbackDebug && Velocity.Length() > MaxSpeed)
        {
            Velocity = Velocity.Normalized() * MaxSpeed;
        }

        // Inersia Jangkar Perisai Neon Anti-Terpental saat gelombang kejut Supernova Rift
        if (_shockwaveShieldTimer > 0.0f)
        {
            _shockwaveShieldTimer -= dt;
            Velocity = Velocity.Lerp(Vector2.Zero, dt * 12.0f);
        }

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
        if (EscapeBuffTimer > 0.0f)
        {
            EscapeBuffTimer -= dt;
        }

        if (IsMitosisActive)
        {
            MitosisTimer += dt;
            MitosisProgress = Mathf.Clamp(MitosisTimer / MitosisDuration, 0.0f, 1.0f);
            if (MitosisTimer >= MitosisDuration)
            {
                CompleteCurrentMitosis();
            }
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
                float targetAccel = HasEscapeSpeedBuff ? (Acceleration * 0.75f) : (Acceleration * 0.45f);
                float targetMaxSpeed = HasEscapeSpeedBuff ? EscapePodMaxSpeed : MaxSpeed;
                Velocity += fleeDir * (targetAccel * dt);

                float curSpeed = Velocity.Length();
                if (curSpeed > targetMaxSpeed)
                {
                    float dampedSpeed = Mathf.Lerp(curSpeed, targetMaxSpeed, MathF.Min(1.0f, 6.0f * dt));
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

        // Kolisi fisik per-sel Hexagon terhadap voxel asteroid (identik 1:1 dengan PlayerCore)
        ResolveAsteroidVoxelCollisions();

        QueueRedraw();
    }

    private void ResolveAsteroidVoxelCollisions()
    {
        var asteroids = Asteroid.ActiveAsteroids;
        if (asteroids.Count == 0) return;

        Vector2 shipPos = GlobalPosition;
        Vector2 shipVel = Velocity;
        float hexColRadius = HexRadius * 0.85f;
        float clusterRadius = _clusterCollisionRadius;

        for (int i = 0; i < asteroids.Count; i++)
        {
            var ast = asteroids[i];
            if (ast == null || !ast.IsActiveInTree) continue;

            float maxReach = clusterRadius + ast.OuterBoundRadius;
            Vector2 toAst = shipPos - ast.CachedPosition;
            if (MathF.Abs(toAst.X) > maxReach || MathF.Abs(toAst.Y) > maxReach) continue;

            for (int c = 0; c < _hexCellOffsets.Count; c++)
            {
                Vector2 rotatedOffset = _hexCellOffsets[c].Rotated(Rotation);
                Vector2 cellWorldPos = shipPos + rotatedOffset;

                if (ast.ResolveCircleVoxelCollision(ref cellWorldPos, ref shipVel, hexColRadius, out Vector2 hitNormal, impulseWeight: 0.35f))
                {
                    shipPos = cellWorldPos - rotatedOffset;
                    if (IsEscapePod)
                    {
                        Vector2 tangent = new Vector2(-hitNormal.Y, hitNormal.X);
                        if (tangent.Dot(EscapeBurstDirection) < 0.0f) tangent = -tangent;
                        float keepSpeed = MathF.Max(shipVel.Length(), 680.0f);
                        shipVel = (tangent * 0.85f + hitNormal * 0.15f).Normalized() * keepSpeed;
                        EscapeBurstDirection = shipVel.Normalized();
                    }
                }
            }
        }

        GlobalPosition = shipPos;
        Velocity = shipVel;
    }

    public bool TakeDamage(float amount, Vector2 impactDir)
    {
        if (InvulnerableTimer > 0.0f) return false;

        _hitFlashTimer = 0.10f;

        // Sandbag Testing Mode: HP abadi & tidak memicu kematian / escape pod
        if (IsInvincibleDebug)
        {
            Health = MaxHealth;
            EmitSignal(SignalName.EnemyCoreDamageTaken, Health, MaxHealth);
            return false;
        }

        Health = MathF.Max(0.0f, Health - amount);
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
        BuildHoneycombCluster();

        Health = 600.0f;
        InvulnerableTimer = 3.0f;
        EscapeBurstTimer = 1.45f;
        EscapeBuffTimer = EscapeBuffDuration;

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

        // Gambar animasi Mitosis bila sedang membelah sel baru
        if (IsMitosisActive)
        {
            float pEased = Mathf.SmoothStep(0.0f, 1.0f, MitosisProgress);
            Vector2 childPos = _mitosisParentOffset.Lerp(_mitosisTargetOffset, pEased);
            float childRadius = Mathf.Lerp(HexRadius * 0.45f, HexRadius * 0.95f, pEased);
            DrawCircle(childPos, childRadius, new Color(2.2f, 0.2f, 0.75f, 0.35f));
            DrawArc(childPos, childRadius, 0.0f, Mathf.Tau, 16, EnemyLineColor, 1.8f);
            DrawLine(_mitosisParentOffset, childPos, EnemyPulseColor, 1.8f);
        }

        // Ekor Thruster Darurat (Dihilangkan secara default; hanya menyala jika ShowEscapeTrail aktif)
        if (ShowEscapeTrail && IsEscapePod && EscapeBurstTimer > 0.0f)
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
            DrawArc(Vector2.Zero, _clusterCollisionRadius * 1.35f, 0.0f, Mathf.Tau, 32, invColor, 2.5f);
        }

        // Strobe Peringatan Bahaya jika sedang menjadi Nemesis Escape Pod
        if (IsEscapePod)
        {
            float alarm = 0.5f + 0.5f * MathF.Sin((float)Time.GetTicksMsec() * 0.014f);
            Color alarmCol = new Color(4.8f, 0.3f, 0.8f, alarm * 0.75f);
            DrawArc(Vector2.Zero, HexRadius * 1.25f, 0.0f, Mathf.Tau, 24, alarmCol, 1.6f);
        }

        // Lingkaran batas sensor perisai inti
        DrawArc(Vector2.Zero, _clusterCollisionRadius, 0.0f, Mathf.Tau, 32, new Color(EnemyLineColor.R, EnemyLineColor.G, EnemyLineColor.B, 0.28f * pulse), 1.2f);

        // Perisai Neon Anti-Terpental (Rival Inertial Anchor Barrier) saat Supernova Cosmic Rift
        if (_shockwaveShieldTimer > 0.0f)
        {
            Vector2 toRiftWorld = _shockwaveShieldSourcePos - GlobalPosition;
            if (toRiftWorld.LengthSquared() > 0.01f)
            {
                Vector2 localToRift = toRiftWorld.Normalized().Rotated(-Rotation);
                float midAngle = localToRift.Angle();
                float shieldRadius = _clusterCollisionRadius + 32.0f;
                float arcSpread = 1.62f; // ~93 derajat
                float startAngle = midAngle - arcSpread * 0.5f;

                float shieldPulse = 0.85f + 0.15f * MathF.Sin((float)Time.GetTicksMsec() * 0.018f);

                // A. Lapisan Aura Glow Luar Neon Crimson-Magenta
                Color enemyNeonAura = new Color(6.8f, 0.35f, 4.6f, 0.95f) * shieldPulse;
                DrawArc(Vector2.Zero, shieldRadius, startAngle, startAngle + arcSpread, 28, enemyNeonAura, 8.5f, false);

                // B. Lapisan Inti Garis Energi Tajam (White-Magenta HDR)
                Color enemyNeonCore = new Color(8.8f, 5.2f, 7.8f, 1.0f);
                DrawArc(Vector2.Zero, shieldRadius, startAngle, startAngle + arcSpread, 28, enemyNeonCore, 2.8f, false);

                // C. Cincin Lapisan Kedua Resonansi Medan Gaya
                Color enemyNeonSub = new Color(4.5f, 0.2f, 3.2f, 0.70f) * shieldPulse;
                DrawArc(Vector2.Zero, shieldRadius - 9.0f, startAngle + 0.18f, startAngle + arcSpread - 0.18f, 22, enemyNeonSub, 2.2f, false);

                // D. Taring Penjepit Inersia di kedua ujung arc
                Vector2 tip1 = new Vector2(MathF.Cos(startAngle), MathF.Sin(startAngle)) * shieldRadius;
                Vector2 tip2 = new Vector2(MathF.Cos(startAngle + arcSpread), MathF.Sin(startAngle + arcSpread)) * shieldRadius;
                DrawLine(tip1, tip1 * 0.82f, enemyNeonCore, 2.5f);
                DrawLine(tip2, tip2 * 0.82f, enemyNeonCore, 2.5f);

                // E. Garis Faset Pembelok Gelombang Kejut (Deflector Lattice Lines)
                int latticeRays = 5;
                for (int l = 0; l <= latticeRays; l++)
                {
                    float aL = startAngle + (arcSpread * l / latticeRays);
                    Vector2 pOuter = new Vector2(MathF.Cos(aL), MathF.Sin(aL)) * shieldRadius;
                    Vector2 pInner = pOuter * 0.88f;
                    DrawLine(pInner, pOuter, enemyNeonAura * 0.75f, 1.8f);
                }
            }
        }
    }
}
