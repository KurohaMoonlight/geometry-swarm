using System;
using System.Collections.Generic;
using GeometrySwarm.Core.ECS.Components;
using Godot;

namespace GeometrySwarm.Gameplay;

/// <summary>
/// Kapal Induk Pemain (The Hive Core) berbentuk gugusan Hexagon sarang lebah (Honeycomb).
/// - Ukuran HexRadius diperkecil menjadi 16.0 px (diameter ~28 px) sehingga mudah masuk ke dalam
///   terowongan yang digali oleh laser Mining Droid (terutama pada 15+ Miner / lebar >= 2 blok).
/// - Menggunakan Zero-Lag Circle-to-Grid Voxel Collision terhadap sel 13 px Asteroid.
/// - Mendukung Sacrificial Plasma Boost [SPACE] serta Emergency Escape Pod Ejection berkecepatan tinggi.
/// </summary>
public partial class PlayerCore : CharacterBody2D
{
    public static PlayerCore? Instance { get; private set; }

    [Signal]
    public delegate void CoreDamageTakenEventHandler(float currentHealth, float maxHealth);

    [Signal]
    public delegate void EscapePodEjectedEventHandler(Vector2 ejectionPos, Vector2 ejectionVelocity);

    [Signal]
    public delegate void CoreDestroyedEventHandler();

    [Export] public float Acceleration { get; set; } = 2200.0f;
    [Export] public float Friction { get; set; } = 0.91f;
    [Export] public float MaxSpeed { get; set; } = 420.0f;
    [Export] public float EscapePodMaxSpeed { get; set; } = 660.0f;
    [Export] public float BoostMaxSpeed { get; set; } = 1050.0f;

    [Export] public float BaseHealthPerCell { get; set; } = 600.0f;
    public float MaxHealth => MathF.Max(600.0f, _hexCellOffsets.Count * BaseHealthPerCell);
    public float Health { get; private set; } = 600.0f;
    public bool IsEscapePod { get; private set; } = false;
    public float InvulnerableTimer { get; private set; } = 0.0f;
    public float EscapeBurstTimer { get; private set; } = 0.0f;
    [Export] public float EscapeBuffDuration { get; set; } = 15.0f;
    public float EscapeBuffTimer { get; private set; } = 0.0f;
    public bool HasEscapeSpeedBuff => IsEscapePod && EscapeBuffTimer > 0.0f;
    [Export] public bool ShowEscapeTrail { get; set; } = false;
    public Vector2 EscapeBurstDirection { get; private set; } = Vector2.Left;
    public float HitFlashTimer { get; private set; } = 0.0f;

    public bool IsBoosting { get; set; } = false;
    public Vector2 BoostThrustVector { get; set; } = Vector2.Zero;
    public bool IsBraking { get; set; } = false;
    public Vector2 BrakeThrustVector { get; set; } = Vector2.Zero;

    // Ukuran Hexagon diperkecil dari 24 px ke 16 px agar lincah bermanuver di dalam terowongan
    public const float HexRadius = 16.0f;
    public float CoreCollisionRadius => _circleShape?.Radius ?? (HexRadius * 0.92f);

    private readonly HashSet<Vector2I> _occupiedAxials = new();
    private readonly List<Vector2> _hexCellOffsets = new();
    private readonly List<Vector2[]> _cachedHexPolylines = new();
    private readonly List<Vector2[]> _cachedHexPolygons = new();

    private static readonly Vector2I[] HexDirections = new Vector2I[]
    {
        new(1, 0),
        new(1, -1),
        new(0, -1),
        new(-1, 0),
        new(-1, 1),
        new(0, 1),
    };

    private static readonly Color CoreLineColor = new(2.2f, 1.8f, 0.6f, 1.0f);
    private static readonly Color CoreAuraColor = new(0.9f, 0.6f, 0.1f, 0.4f);
    private static readonly Color CenterPulseColor = new(3.0f, 2.8f, 1.5f, 1.0f);

    public int CoreCellCount => _hexCellOffsets.Count;

    // Sistem Animasi Fabrikasi Swarm (Hologram 3D Nanite Print & Ejection Flash)
    public struct FabricationBurst
    {
        public int CellIndex;
        public Vector2 LocalPos;
        public Color ThemeColor;
        public SwarmRole Role;
        public float Timer;
        public float MaxDuration;
    }

    private readonly List<FabricationBurst> _fabricationBursts = new();

    // Sistem Animasi Mitosis (Pembelahan Sel Core Prosedural)
    public bool IsMitosisActive { get; private set; } = false;
    public float MitosisProgress { get; private set; } = 0.0f; // 0.0 sampai 1.0
    public float MitosisDuration { get; private set; } = 7.0f;
    public float MitosisTimer { get; private set; } = 0.0f;
    public float MitosisRemainingTime => MathF.Max(0.0f, MitosisDuration - MitosisTimer);
    public int MitosisQueueCount => _mitosisQueue.Count;

    private readonly HashSet<Vector2I> _reservedAxials = new();
    private readonly Queue<Vector2I> _mitosisQueue = new();
    private Vector2I _mitosisChosenAxial;
    private Vector2 _mitosisParentOffset;
    private Vector2 _mitosisTargetOffset;

    private CollisionShape2D? _collisionShape;
    private CircleShape2D? _circleShape;

    // Status Distorsi Gravitasi Bintang Aether pada Kapal Pemain (Tidal Spaghettification & Pull)
    private float _gravityTidalStress = 0.0f;
    private float _gravityTidalActiveTimer = 0.0f;
    private Vector2 _gravityStarWorldPos = Vector2.Zero;
    private float _gravityPulseTimer = 0.0f;

    // Status Perisai Neon Anti-Terpental saat Cosmic Rift Supernova Shockwave
    private float _shockwaveShieldTimer = 0.0f;
    private Vector2 _shockwaveShieldSourcePos = Vector2.Zero;
    public bool HasShockwaveShield => _shockwaveShieldTimer > 0.0f;

    /// <summary>
    /// Mengaktifkan perisai neon anti-terpental yang menghadap ke arah Cosmic Rift / Black Hole.
    /// Menyerap hentakan inersia gelombang kejut supernova agar kapal induk tidak terlempar.
    /// </summary>
    public void ActivateShockwaveShield(Vector2 sourcePosition, float duration = 0.55f)
    {
        _shockwaveShieldSourcePos = sourcePosition;
        _shockwaveShieldTimer = MathF.Max(_shockwaveShieldTimer, duration);
        QueueRedraw();
    }

    /// <summary>
    /// Dipanggil oleh Bintang Aether saat kapal pemain berada di dalam sumur gravitasi maut.
    /// Mengaktifkan efek peregangan spageti (tidal shear) & mengurangi redaman gesekan terhadap hisapan gravitasi.
    /// </summary>
    public void ApplyGravityWellTidalStress(Vector2 starWorldPos, float pullRatio, float dt)
    {
        _gravityStarWorldPos = starWorldPos;
        _gravityTidalActiveTimer = 0.16f;
        float targetStress = Mathf.Clamp(pullRatio, 0.0f, 1.0f);
        _gravityTidalStress = Mathf.Max(_gravityTidalStress, Mathf.MoveToward(_gravityTidalStress, targetStress, dt * 4.2f));
        QueueRedraw();
    }

    public Vector2 GetCellOffset(int index)
    {
        if (_hexCellOffsets.Count == 0) return Vector2.Zero;
        int safeIdx = Math.Clamp(index, 0, _hexCellOffsets.Count - 1);
        return _hexCellOffsets[safeIdx];
    }

    /// <summary>
    /// Memicu efek Nanite 3D Holographic Print & Aperture Flare pada sel hexagon yang meluncurkan drone
    /// </summary>
    public void TriggerSwarmFabrication(SwarmRole role, Color roleColor, int cellIndex)
    {
        if (_hexCellOffsets.Count == 0) return;
        int safeIdx = Math.Abs(cellIndex) % _hexCellOffsets.Count;
        Vector2 localPos = _hexCellOffsets[safeIdx];

        for (int i = 0; i < _fabricationBursts.Count; i++)
        {
            if (_fabricationBursts[i].CellIndex == safeIdx)
            {
                var existing = _fabricationBursts[i];
                existing.Timer = 0.42f;
                existing.ThemeColor = roleColor;
                existing.Role = role;
                _fabricationBursts[i] = existing;
                QueueRedraw();
                return;
            }
        }

        _fabricationBursts.Add(new FabricationBurst
        {
            CellIndex = safeIdx,
            LocalPos = localPos,
            ThemeColor = roleColor,
            Role = role,
            Timer = 0.42f,
            MaxDuration = 0.42f
        });
        QueueRedraw();
    }

    public override void _EnterTree()
    {
        Instance = this;
    }

    public override void _ExitTree()
    {
        if (Instance == this)
        {
            Instance = null;
        }
    }

    public override void _Ready()
    {
        // Spawn langsung di dekat Cosmic Rift 0 agar pemain dapat langsung mengamati animasinya
        Vector2 rift0Pos = new Vector2(MathF.Cos(-0.155f), MathF.Sin(-0.155f)) * AsteroidFieldManager.RiftOrbitRadius;
        GlobalPosition = rift0Pos + new Vector2(-460.0f, 140.0f);

        ZIndex = 2; // Berada di antara Back Ring (ZIndex=1) dan Front Ring (ZIndex=3)

        _occupiedAxials.Add(Vector2I.Zero);
        _hexCellOffsets.Add(Vector2.Zero);
        _cachedHexPolylines.Add(BuildHexPoints(Vector2.Zero, HexRadius));
        _cachedHexPolygons.Add(BuildHexPolygon(Vector2.Zero, HexRadius));

        Health = MaxHealth;

        CollisionLayer = 1;
        CollisionMask = 1;

        _circleShape = new CircleShape2D { Radius = HexRadius * 0.92f };
        _collisionShape = new CollisionShape2D { Shape = _circleShape };
        AddChild(_collisionShape);
        QueueRedraw();
    }

    /// <summary>
    /// Menghitung durasi mitosis berdasarkan jumlah core saat ini:
    /// Dimulai dari 7.0 detik pada 1 core, dan semakin banyak core semakin cepat membelah.
    /// </summary>
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

        // Cari sel induk (parent) dari sel-sel aktif yang bertetangga langsung dengan target
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
        _cachedHexPolygons.Add(BuildHexPolygon(_mitosisTargetOffset, HexRadius));

        if (_circleShape != null)
        {
            float maxDist = HexRadius;
            foreach (var off in _hexCellOffsets)
            {
                maxDist = MathF.Max(maxDist, off.Length() + HexRadius);
            }
            _circleShape.Radius = maxDist;
        }

        _reservedAxials.Remove(_mitosisChosenAxial);

        if (IsEscapePod)
        {
            IsEscapePod = false;
            EscapeBurstTimer = 0.0f;
            EscapeBuffTimer = 0.0f;
        }
        Health = MathF.Min(MaxHealth, Health + BaseHealthPerCell);
        EmitSignal(SignalName.CoreDamageTaken, Health, MaxHealth);

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

    /// <summary>
    /// Menerima kerusakan benturan swarm musuh atau proyektil.
    /// Mengembalikan true jika sistem memicu peluncuran Escape Pod darurat.
    /// </summary>
    public bool TakeDamage(float amount, Vector2 impactDir)
    {
        if (InvulnerableTimer > 0.0f) return false;

        Health = MathF.Max(0.0f, Health - amount);
        HitFlashTimer = 0.12f;
        EmitSignal(SignalName.CoreDamageTaken, Health, MaxHealth);

        if (Health <= 0.0f)
        {
            if (!IsEscapePod)
            {
                TriggerEmergencyEscape(impactDir);
                return true;
            }
            else
            {
                EmitSignal(SignalName.CoreDestroyed);
                return false;
            }
        }
        return false;
    }

    /// <summary>
    /// Protokol Penyelamatan Darurat (Escape Pod / Micro-Core Reboot):
    /// Saat lambung kapal induk hancur, menyisakan 1 sel inti (Micro-Core, 16px) yang terlontar
    /// kabur dengan sangat cepat menjauhi ancaman musuh sambil otomatis memproduksi 1 Miner!
    /// </summary>
    public void TriggerEmergencyEscape(Vector2 impactDir)
    {
        IsEscapePod = true;

        _occupiedAxials.Clear();
        _hexCellOffsets.Clear();
        _cachedHexPolylines.Clear();
        _cachedHexPolygons.Clear();
        _reservedAxials.Clear();
        _mitosisQueue.Clear();
        IsMitosisActive = false;

        _occupiedAxials.Add(Vector2I.Zero);
        _hexCellOffsets.Add(Vector2.Zero);
        _cachedHexPolylines.Add(BuildHexPoints(Vector2.Zero, HexRadius));
        _cachedHexPolygons.Add(BuildHexPolygon(Vector2.Zero, HexRadius));

        if (_circleShape != null)
        {
            _circleShape.Radius = HexRadius * 0.92f;
        }

        Health = BaseHealthPerCell;
        InvulnerableTimer = 3.0f;
        EscapeBurstTimer = 1.40f;
        EscapeBuffTimer = EscapeBuffDuration;

        // Arah lontaran kabur: searah dorongan menjauh dari musuh (impactDir sudah mengarah dari musuh ke pemain)
        Vector2 escapeDir = impactDir.LengthSquared() > 0.01f
            ? impactDir.Normalized()
            : new Vector2(-1.0f, (GD.Randf() - 0.5f)).Normalized();

        EscapeBurstDirection = escapeDir;
        Velocity = escapeDir * 1220.0f;

        EmitSignal(SignalName.EscapePodEjected, GlobalPosition, Velocity);
        QueueRedraw();
    }

    private static Vector2 AxialToWorldOffset(Vector2I axial)
    {
        float x = MathF.Sqrt(3.0f) * HexRadius * (axial.X + axial.Y * 0.5f);
        float y = 1.5f * HexRadius * axial.Y;
        return new Vector2(x, y);
    }

    public override void _PhysicsProcess(double delta)
    {
        try
        {
            float dt = (float)delta;

            if (InvulnerableTimer > 0.0f)
            {
                InvulnerableTimer -= dt;
                QueueRedraw();
            }
            if (EscapeBurstTimer > 0.0f)
            {
                EscapeBurstTimer -= dt;
                QueueRedraw();
            }
            if (EscapeBuffTimer > 0.0f)
            {
                EscapeBuffTimer -= dt;
                QueueRedraw();
            }
            if (HitFlashTimer > 0.0f)
            {
                HitFlashTimer -= dt;
                QueueRedraw();
            }
            if (_gravityTidalActiveTimer > 0.0f)
            {
                _gravityTidalActiveTimer -= dt;
                _gravityPulseTimer += dt * (8.0f + _gravityTidalStress * 14.0f);
                QueueRedraw();
            }
            else if (_gravityTidalStress > 0.0f)
            {
                _gravityTidalStress = Mathf.MoveToward(_gravityTidalStress, 0.0f, dt * 2.5f);
                QueueRedraw();
            }

            // Pembaruan Animasi Fabrikasi Swarm (Hologram 3D Nanite Print)
            if (_fabricationBursts.Count > 0)
            {
                for (int i = _fabricationBursts.Count - 1; i >= 0; i--)
                {
                    var fb = _fabricationBursts[i];
                    fb.Timer -= dt;
                    if (fb.Timer <= 0.0f)
                    {
                        _fabricationBursts.RemoveAt(i);
                    }
                    else
                    {
                        _fabricationBursts[i] = fb;
                    }
                }
                QueueRedraw();
            }

            // Pembaruan Animasi Pembelahan Sel (Mitosis)
            if (IsMitosisActive)
            {
                MitosisTimer += dt;
                MitosisProgress = Mathf.Clamp(MitosisTimer / MitosisDuration, 0.0f, 1.0f);
                QueueRedraw();

                if (MitosisTimer >= MitosisDuration)
                {
                    CompleteCurrentMitosis();
                }
            }

            Vector2 inputDir = Vector2.Zero;
            if (Input.IsActionPressed("ui_right") || Input.IsKeyPressed(Key.D)) inputDir.X += 1.0f;
            if (Input.IsActionPressed("ui_left") || Input.IsKeyPressed(Key.A)) inputDir.X -= 1.0f;
            if (Input.IsActionPressed("ui_down") || Input.IsKeyPressed(Key.S)) inputDir.Y += 1.0f;
            if (Input.IsActionPressed("ui_up") || Input.IsKeyPressed(Key.W)) inputDir.Y -= 1.0f;

            float currentAccel = HasEscapeSpeedBuff ? (Acceleration * 1.45f) : Acceleration;
            if (inputDir.LengthSquared() > 0.01f)
            {
                inputDir = inputDir.Normalized();
                Velocity += inputDir * (currentAccel * dt);
                if (EscapeBurstTimer > 0.0f)
                {
                    // Pemain bisa ikut mengarahkan lontaran kabur darurat dengan tombol WASD
                    EscapeBurstDirection = (EscapeBurstDirection + inputDir * 0.65f).Normalized();
                }
            }

            // Dorongan roket darurat saat baru saja Eject sebagai Escape Pod (Lari Cepat!)
            if (IsEscapePod && EscapeBurstTimer > 0.0f)
            {
                Velocity += EscapeBurstDirection * (1850.0f * dt);
            }

            // Dorongan Plasma Boost dari Dart [SPACE]
            if (IsBoosting && BoostThrustVector.LengthSquared() > 0.01f)
            {
                Velocity += BoostThrustVector * dt;
            }

            // Pengereman Retro-Rocket aktif dari Dart di depan arah gerak
            if (IsBraking && BrakeThrustVector.LengthSquared() > 0.01f)
            {
                Velocity += BrakeThrustVector * dt;
                Velocity *= MathF.Pow(0.85f, dt * 60.0f);
            }

            // Saat berada di medan gravitasi Bintang Aether, kurangi redaman gesekan agar tarikan bintang
            // benar-benar mengakumulasi momentum hisap yang kuat pada kapal pemain!
            float baseFriction = (IsEscapePod && EscapeBurstTimer > 0.0f) ? 0.985f : Friction;
            float activeFriction = _gravityTidalStress > 0.02f
                ? Mathf.Lerp(baseFriction, 0.972f, Mathf.Clamp(_gravityTidalStress * 1.15f, 0.0f, 1.0f))
                : baseFriction;
            Velocity *= MathF.Pow(activeFriction, dt * 60.0f);

            float baseMaxSpd = (HasEscapeSpeedBuff ? EscapePodMaxSpeed : MaxSpeed) + (_gravityTidalStress * 340.0f);
            float activeMaxSpeed = (IsEscapePod && EscapeBurstTimer > 0.0f)
                ? 1220.0f
                : (IsBoosting ? BoostMaxSpeed : baseMaxSpd);

            float speedSq = Velocity.LengthSquared();
            if (speedSq > activeMaxSpeed * activeMaxSpeed)
            {
                if (IsBoosting || (IsEscapePod && EscapeBurstTimer > 0.0f))
                {
                    Velocity = Velocity.Normalized() * activeMaxSpeed;
                }
                else
                {
                    // Peredaman aerodinamis halus kembali ke kecepatan normal setelah fase burst/boost selesai
                    float curSpeed = MathF.Sqrt(speedSq);
                    float dampedSpeed = Mathf.Lerp(curSpeed, baseMaxSpd, MathF.Min(1.0f, 6.5f * dt));
                    Velocity = (Velocity / curSpeed) * dampedSpeed;
                }
            }

            // Inersia Jangkar Perisai Neon Anti-Terpental saat gelombang kejut Supernova Rift
            if (_shockwaveShieldTimer > 0.0f)
            {
                _shockwaveShieldTimer -= dt;
                Velocity = Velocity.Lerp(Vector2.Zero, dt * 12.0f);
                QueueRedraw();
            }

            GlobalPosition += Velocity * dt;

            ResolveAsteroidVoxelCollisions();

            Vector2 mousePos = GetGlobalMousePosition();
            Vector2 toMouse = mousePos - GlobalPosition;
            if (toMouse.LengthSquared() > 10.0f)
            {
                Rotation = Mathf.LerpAngle(Rotation, toMouse.Angle() + Mathf.Pi * 0.5f, 14.0f * dt);
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[PlayerCore._PhysicsProcess] {ex}");
        }
    }

    private void ResolveAsteroidVoxelCollisions()
    {
        var asteroids = Asteroid.ActiveAsteroids;
        if (asteroids.Count == 0) return;

        Vector2 shipPos = GlobalPosition;
        Vector2 shipVel = Velocity;
        float hexColRadius = HexRadius * 0.85f;
        float clusterRadius = _circleShape?.Radius ?? HexRadius;

        for (int a = 0; a < asteroids.Count; a++)
        {
            var ast = asteroids[a];
            if (ast == null || !ast.IsActiveInTree) continue;

            float maxReach = clusterRadius + ast.OuterBoundRadius;
            Vector2 toAst = shipPos - ast.CachedPosition;
            if (MathF.Abs(toAst.X) > maxReach || MathF.Abs(toAst.Y) > maxReach) continue;

            for (int c = 0; c < _hexCellOffsets.Count; c++)
            {
                Vector2 rotatedOffset = _hexCellOffsets[c].Rotated(Rotation);
                Vector2 cellWorldPos = shipPos + rotatedOffset;

                if (ast.ResolveCircleVoxelCollision(ref cellWorldPos, ref shipVel, hexColRadius, impulseWeight: 0.45f))
                {
                    shipPos = cellWorldPos - rotatedOffset;
                }
            }
        }

        GlobalPosition = shipPos;
        Velocity = shipVel;
    }

    public override void _Draw()
    {
        try
        {
            bool isHit = HitFlashTimer > 0.0f;
            Color activeLineColor = isHit ? new Color(4.8f, 4.8f, 4.2f, 1.0f) : CoreLineColor;
            Color activeAuraColor = isHit ? new Color(3.5f, 3.5f, 2.5f, 0.85f) : CoreAuraColor;

            // Ekor Thruster Darurat saat Escape Pod melesat kabur cepat (Dihilangkan secara default; dibatasi 15 detik jika ShowEscapeTrail diaktifkan)
            if (ShowEscapeTrail && HasEscapeSpeedBuff && Velocity.LengthSquared() > 2500.0f)
            {
                Vector2 localBack = (-Velocity.Normalized()).Rotated(-Rotation);
                Vector2 localTan = new Vector2(-localBack.Y, localBack.X);
                float jetLen = Mathf.Clamp(Velocity.Length() * 0.085f, 22.0f, 95.0f);
                DrawLine(localBack * 12.0f, localBack * (12.0f + jetLen), new Color(1.2f, 3.8f, 4.8f, 0.85f), 5.0f);
                DrawLine(localBack * 12.0f, localBack * (12.0f + jetLen * 0.72f), new Color(4.8f, 5.2f, 5.8f, 0.95f), 2.4f);
                DrawLine(localBack * 14.0f + localTan * 5.5f, localBack * (12.0f + jetLen * 0.5f), new Color(3.8f, 2.6f, 0.8f, 0.70f), 1.8f);
                DrawLine(localBack * 14.0f - localTan * 5.5f, localBack * (12.0f + jetLen * 0.5f), new Color(3.8f, 2.6f, 0.8f, 0.70f), 1.8f);
            }

            // 1. Gambar semua sel hexagon aktif yang sudah mengkristal permanen
            Color coreHullFill = new Color(0.010f, 0.006f, 0.022f, 0.94f);
            for (int i = 0; i < _hexCellOffsets.Count; i++)
            {
                Vector2 offset = _hexCellOffsets[i];
                Vector2[] poly6 = (i < _cachedHexPolygons.Count)
                    ? _cachedHexPolygons[i]
                    : BuildHexPolygon(offset, HexRadius);
                Vector2[] points = (i < _cachedHexPolylines.Count)
                    ? _cachedHexPolylines[i]
                    : BuildHexPoints(offset, HexRadius);

                // Isi siluet bodi Core agar swarm di Back Ring (ZIndex=1) tertutup saat melintas di belakang Core
                DrawColoredPolygon(poly6, coreHullFill);
                DrawPolyline(points, activeAuraColor, 2.8f, false);
                DrawPolyline(points, activeLineColor, 1.4f, false);
                DrawCircle(offset, 2.6f, CenterPulseColor);
            }

            // Perisai Darurat Invulnerability Flash
            if (InvulnerableTimer > 0.0f)
            {
                float invPulse = 0.5f + 0.5f * MathF.Sin(InvulnerableTimer * 26.0f);
                Color invColor = new Color(1.2f, 3.8f, 4.8f, 0.45f + 0.35f * invPulse);
                DrawArc(Vector2.Zero, HexRadius * 1.55f, 0.0f, Mathf.Tau, 32, invColor, 2.4f);
            }

            // Strobe Peringatan Bahaya jika sedang menjadi Escape Pod tunggal
            if (IsEscapePod)
            {
                float alarm = 0.5f + 0.5f * MathF.Sin((float)Time.GetTicksMsec() * 0.014f);
                Color alarmCol = new Color(4.2f, 1.8f, 0.3f, alarm * 0.75f);
                DrawArc(Vector2.Zero, HexRadius * 1.25f, 0.0f, Mathf.Tau, 24, alarmCol, 1.6f);
            }

            // 2. Gambar Animasi Mitosis Dinamis (Proses Biologis-Geometris Pembelahan Sel)
            if (IsMitosisActive)
            {
                float t = MitosisProgress;
                Vector2 p0 = _mitosisParentOffset;
                Vector2 p1 = _mitosisTargetOffset;

                // FASE 1: Interfase & Duplikasi Nukleus (0.00 - 0.22)
                if (t < 0.22f)
                {
                    float p = t / 0.22f;
                    float pulse = 1.0f + 0.28f * MathF.Sin(MitosisTimer * 22.0f);
                    DrawCircle(p0, HexRadius * 1.32f * pulse, new Color(2.5f, 1.9f, 0.4f, 0.38f));

                    float childNucRadius = Mathf.Lerp(1.2f, 5.0f, p);
                    Vector2 microJitter = new Vector2(GD.Randf() - 0.5f, GD.Randf() - 0.5f) * 2.0f;
                    DrawCircle(p0 + microJitter, childNucRadius, new Color(3.6f, 3.2f, 1.4f, 0.95f));
                    DrawCircle(p0, 2.8f, CenterPulseColor);
                }
                // FASE 2: Anafase & Peregangan Membran Sitokinesis (0.22 - 0.82)
                else if (t < 0.82f)
                {
                    float p = (t - 0.22f) / 0.60f;
                    float pEased = Mathf.SmoothStep(0.0f, 1.0f, p);
                    Vector2 childPos = p0.Lerp(p1, pEased);

                    DrawCircle(p0, HexRadius * 1.20f, new Color(2.3f, 1.7f, 0.4f, 0.30f));

                    Vector2 diff = childPos - p0;
                    Vector2 dir = diff.LengthSquared() > 0.01f ? diff.Normalized() : Vector2.Right;
                    Vector2 normal = new Vector2(-dir.Y, dir.X);
                    Vector2 mid = (p0 + childPos) * 0.5f;

                    float parentWaist = HexRadius * 0.92f;
                    float childWaist = HexRadius * Mathf.Lerp(0.48f, 0.88f, pEased);
                    float midWaist = Mathf.Lerp(HexRadius * 0.72f, 2.4f, pEased);

                    Vector2 ptParentL = p0 + normal * parentWaist;
                    Vector2 ptParentR = p0 - normal * parentWaist;
                    Vector2 ptMidL = mid + normal * midWaist;
                    Vector2 ptMidR = mid - normal * midWaist;
                    Vector2 ptChildL = childPos + normal * childWaist;
                    Vector2 ptChildR = childPos - normal * childWaist;

                    var leftStrand = new Vector2[] { ptParentL, ptMidL, ptChildL };
                    var rightStrand = new Vector2[] { ptParentR, ptMidR, ptChildR };
                    Color strandColor = new Color(2.9f, 2.3f, 0.75f, 0.90f);
                    Color auraStrandColor = new Color(1.3f, 0.9f, 0.25f, 0.50f);

                    DrawPolyline(leftStrand, auraStrandColor, 3.4f);
                    DrawPolyline(leftStrand, strandColor, 1.7f);
                    DrawPolyline(rightStrand, auraStrandColor, 3.4f);
                    DrawPolyline(rightStrand, strandColor, 1.7f);

                    Vector2 arcJitter = normal * (MathF.Sin(MitosisTimer * 38.0f) * 2.8f);
                    DrawLine(p0, mid + arcJitter, new Color(3.8f, 3.2f, 1.6f, 0.98f), 2.0f);
                    DrawLine(mid + arcJitter, childPos, new Color(3.8f, 3.2f, 1.6f, 0.98f), 2.0f);

                    float childRadius = Mathf.Lerp(HexRadius * 0.48f, HexRadius * 0.95f, pEased);
                    DrawCircle(childPos, childRadius, new Color(1.8f, 1.4f, 0.35f, 0.40f));
                    DrawArc(childPos, childRadius, 0.0f, Mathf.Tau, 16, strandColor, 1.9f);
                    DrawCircle(childPos, 3.4f, CenterPulseColor);
                }
                // FASE 3: Telofase, Putusnya Jembatan & Kristalisasi Hexagon (0.82 - 1.00)
                else
                {
                    float q = (t - 0.82f) / 0.18f;

                    float ringRad = HexRadius * (0.85f + q * 1.9f);
                    float ringAlpha = MathF.Max(0.0f, 1.0f - q);
                    DrawArc(p1, ringRad, 0.0f, Mathf.Tau, 24, new Color(3.7f, 3.1f, 1.4f, ringAlpha), 2.4f);

                    float bounce = 1.0f + 0.25f * (1.0f - q) * MathF.Sin(q * Mathf.Pi * 3.5f);
                    Vector2[] snapHex = BuildHexPoints(p1, HexRadius * bounce);

                    Color snapLine = new Color(3.4f, 2.7f, 0.95f, 1.0f);
                    Color snapAura = new Color(2.2f, 1.6f, 0.45f, 0.75f);
                    DrawPolyline(snapHex, snapAura, 3.8f);
                    DrawPolyline(snapHex, snapLine, 2.2f);
                    DrawCircle(p1, 4.0f * bounce, CenterPulseColor);

                    for (int s = 0; s < 6; s++)
                    {
                        float spkAngle = (float)s / 6.0f * Mathf.Tau + q * 2.0f;
                        Vector2 spkDir = new Vector2(MathF.Cos(spkAngle), MathF.Sin(spkAngle));
                        Vector2 spkPos = p1 + spkDir * (HexRadius * (0.95f + q * 1.3f));
                        DrawCircle(spkPos, 1.8f * (1.0f - q), CenterPulseColor);
                    }
                }
            }

            // 3. Gambar Animasi Fabrikasi Nanite 3D Hologram & Ejection Flash (Konsep 1 & 2)
            for (int i = 0; i < _fabricationBursts.Count; i++)
            {
                var fb = _fabricationBursts[i];
                float t = fb.Timer / fb.MaxDuration; // 1.0 -> 0.0
                float sinT = MathF.Sin(t * Mathf.Pi);
                Vector2 p = fb.LocalPos;

                Vector2[] corners = BuildHexPoints(p, HexRadius);
                Color beamCol = fb.ThemeColor * 1.8f;
                beamCol.A = sinT * 0.85f;
                for (int c = 0; c < 6; c++)
                {
                    DrawLine(corners[c], p, beamCol, 1.6f);
                }

                float ringRad = HexRadius * (0.25f + (1.0f - t) * 1.35f);
                Color ringCol = fb.ThemeColor * 2.2f;
                ringCol.A = t * 0.90f;
                DrawArc(p, ringRad, 0.0f, Mathf.Tau, 20, ringCol, 2.0f);

                float glyphAngle = (1.0f - t) * Mathf.Tau * 2.2f;
                float glyphScale = sinT * (HexRadius * 0.72f);
                Color holoCol = fb.ThemeColor * 2.5f;
                holoCol.A = sinT;

                DrawHolographicGlyph(p, fb.Role, glyphScale, glyphAngle, holoCol);

                float flashRadius = Mathf.Lerp(6.5f, 2.8f, 1.0f - t);
                DrawCircle(p, flashRadius, fb.ThemeColor * (1.5f + sinT * 1.5f));
            }

            // 4. Perisai Neon Anti-Terpental (Inertial Anchor Barrier) saat Supernova Cosmic Rift
            if (_shockwaveShieldTimer > 0.0f)
            {
                Vector2 toRiftWorld = _shockwaveShieldSourcePos - GlobalPosition;
                if (toRiftWorld.LengthSquared() > 0.01f)
                {
                    Vector2 localToRift = toRiftWorld.Normalized().Rotated(-Rotation);
                    float midAngle = localToRift.Angle();
                    float clusterRad = _circleShape?.Radius ?? (HexRadius * 2.2f);
                    float shieldRadius = clusterRad + 32.0f;
                    float arcSpread = 1.62f; // ~93 derajat
                    float startAngle = midAngle - arcSpread * 0.5f;

                    float pulse = 0.85f + 0.15f * MathF.Sin((float)Time.GetTicksMsec() * 0.018f);

                    // A. Lapisan Aura Glow Luar Neon Cyan-Teal
                    Color neonAura = new Color(0.1f, 4.2f, 8.8f, 0.95f) * pulse;
                    DrawArc(Vector2.Zero, shieldRadius, startAngle, startAngle + arcSpread, 28, neonAura, 8.5f, false);

                    // B. Lapisan Inti Garis Energi Tajam (White-Cyan HDR)
                    Color neonCore = new Color(5.6f, 7.5f, 9.2f, 1.0f);
                    DrawArc(Vector2.Zero, shieldRadius, startAngle, startAngle + arcSpread, 28, neonCore, 2.8f, false);

                    // C. Cincin Lapisan Kedua Resonansi Medan Gaya (Secondary Harmonic Arc)
                    Color neonSub = new Color(0.2f, 3.2f, 6.8f, 0.70f) * pulse;
                    DrawArc(Vector2.Zero, shieldRadius - 9.0f, startAngle + 0.18f, startAngle + arcSpread - 0.18f, 22, neonSub, 2.2f, false);

                    // D. Taring Penjepit Inersia (Inertial Field Stabilizer Brackets) di kedua ujung arc
                    Vector2 tip1 = new Vector2(MathF.Cos(startAngle), MathF.Sin(startAngle)) * shieldRadius;
                    Vector2 tip2 = new Vector2(MathF.Cos(startAngle + arcSpread), MathF.Sin(startAngle + arcSpread)) * shieldRadius;
                    DrawLine(tip1, tip1 * 0.82f, neonCore, 2.5f);
                    DrawLine(tip2, tip2 * 0.82f, neonCore, 2.5f);

                    // E. Garis Faset Pembelok Gelombang Kejut (Deflector Lattice Lines)
                    int latticeRays = 5;
                    for (int l = 0; l <= latticeRays; l++)
                    {
                        float aL = startAngle + (arcSpread * l / latticeRays);
                        Vector2 pOuter = new Vector2(MathF.Cos(aL), MathF.Sin(aL)) * shieldRadius;
                        Vector2 pInner = pOuter * 0.88f;
                        DrawLine(pInner, pOuter, neonAura * 0.75f, 1.8f);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[PlayerCore._Draw] {ex}");
        }
    }

    private void DrawHolographicGlyph(Vector2 center, SwarmRole role, float size, float angle, Color color)
    {
        if (size <= 0.5f) return;

        switch (role)
        {
            case SwarmRole.DartNeedle:
                Vector2 top = center + new Vector2(0, -size).Rotated(angle);
                Vector2 bR = center + new Vector2(size * 0.55f, size * 0.65f).Rotated(angle);
                Vector2 bL = center + new Vector2(-size * 0.55f, size * 0.65f).Rotated(angle);
                DrawPolyline(new Vector2[] { top, bR, bL, top }, color, 1.6f);
                break;

            case SwarmRole.MiningDroid:
                Vector2 n = center + new Vector2(0, -size).Rotated(angle);
                Vector2 e = center + new Vector2(size * 0.7f, 0).Rotated(angle);
                Vector2 s = center + new Vector2(0, size).Rotated(angle);
                Vector2 w = center + new Vector2(-size * 0.7f, 0).Rotated(angle);
                DrawPolyline(new Vector2[] { n, e, s, w, n }, color, 1.6f);
                DrawLine(n, s, color, 1.2f);
                break;

            case SwarmRole.ScannerDrone:
                DrawArc(center, size * 0.65f, 0.0f, Mathf.Tau, 12, color, 1.4f);
                Vector2 d1 = center + new Vector2(0, -size * 0.9f).Rotated(angle);
                Vector2 d2 = center + new Vector2(size * 0.9f, 0).Rotated(angle);
                Vector2 d3 = center + new Vector2(0, size * 0.9f).Rotated(angle);
                Vector2 d4 = center + new Vector2(-size * 0.9f, 0).Rotated(angle);
                DrawPolyline(new Vector2[] { d1, d2, d3, d4, d1 }, color, 1.2f);
                break;

            case SwarmRole.Guardian:
                Vector2[] gHex = BuildHexPoints(center, size * 0.8f);
                DrawPolyline(gHex, color, 1.8f);
                DrawCircle(center, size * 0.35f, color * 0.7f);
                break;

            case SwarmRole.DrillDrone:
                Vector2 dTop = center + new Vector2(0, size).Rotated(angle);
                Vector2 dLeft = center + new Vector2(-size * 0.6f, -size * 0.7f).Rotated(angle);
                Vector2 dRight = center + new Vector2(size * 0.6f, -size * 0.7f).Rotated(angle);
                DrawPolyline(new Vector2[] { dLeft, dRight, dTop, dLeft }, color, 1.6f);
                DrawLine(center + new Vector2(0, -size * 0.7f).Rotated(angle), dTop, color, 1.4f);
                break;

            case SwarmRole.GigaShuriken:
                for (int i = 0; i < 4; i++)
                {
                    float a = angle + i * (Mathf.Pi * 0.5f);
                    Vector2 tip = center + new Vector2(MathF.Cos(a), MathF.Sin(a)) * size;
                    Vector2 baseA = center + new Vector2(MathF.Cos(a + 0.35f), MathF.Sin(a + 0.35f)) * (size * 0.35f);
                    DrawLine(baseA, tip, color, 1.6f);
                }
                break;

            case SwarmRole.CrescentBoomerang:
                float startA = angle - 1.8f;
                DrawArc(center, size * 0.85f, startA, startA + 3.6f, 16, color, 2.0f);
                DrawArc(center + new Vector2(size * 0.25f, 0).Rotated(angle), size * 0.65f, startA + 0.3f, startA + 3.3f, 14, color, 1.4f);
                break;

            default:
                DrawCircle(center, size * 0.5f, color);
                break;
        }
    }

    private static Vector2[] BuildHexPoints(Vector2 center, float radius)
    {
        var pts = new Vector2[7];
        for (int i = 0; i < 6; i++)
        {
            float angle = Mathf.DegToRad(60.0f * i - 30.0f);
            pts[i] = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
        }
        pts[6] = pts[0];
        return pts;
    }

    private static Vector2[] BuildHexPolygon(Vector2 center, float radius)
    {
        var pts = new Vector2[6];
        for (int i = 0; i < 6; i++)
        {
            float angle = Mathf.DegToRad(60.0f * i - 30.0f);
            pts[i] = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
        }
        return pts;
    }
}
