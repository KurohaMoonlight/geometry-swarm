using System;
using System.Collections.Generic;
using Arch.Core;
using GeometrySwarm.Core.ECS.Components;
using GeometrySwarm.Core.ECS.Systems;
using GeometrySwarm.Visual;
using Godot;
using static GeometrySwarm.Gameplay.Asteroid;

namespace GeometrySwarm.Gameplay;

/// <summary>
/// Driver utama simulasi kawanan Arch ECS.
/// - Menghubungkan seluruh varian Attacker (Dart, Shuriken, Bor, Boomerang).
/// - Mengatur Defender (Guardian) organik di sekitar Core (~120px) dengan 12 Sektor Pertahanan Spatial Grid.
/// - Mengatur Super Giga Drill Break:
///   * Semakin banyak lance/drill -> semakin PADAT (multi-layer shell + ekstra ulir spiral), bukan sekadar membesar.
///   * Pembentukan (assembly) bertahap via pusaran vortex dari ujung (tip) ke pangkal (tail), tidak instan.
///   * Swarm lance penyusun bagian paling belakang berfungsi sebagai ROCKET EXHAUST dan MATI (pengorbanan swarm).
///   * Mengekspos telemetri ujung & nozzle belakang untuk efek riak robekan ruang-waktu & exhaust di Hologram Grid.
/// </summary>
public partial class SwarmSimulation : Node2D
{
    [Signal]
    public delegate void GigaDrillLaunchedEventHandler(Vector2 tipPos, Vector2 basePos, Vector2 aimDir, float charge);

    [Signal]
    public delegate void GigaDrillDetonatedEventHandler(Vector2 tipPos, Vector2 basePos, int sacrificedCount);

    [Signal]
    public delegate void GigaDrillNoseBreachedEventHandler(Vector2 breachPos, Vector2 aimDir, int defenderCount);

    [Export] public int InitialSwarmCount { get; set; } = 15;

    private World _world = null!;
    private BoidSystem _boidSystem = null!;
    private QueryDescription _allSwarmQuery;

    private PlayerCore _playerCore = null!;
    private SwarmMultiMeshRenderer _renderer = null!;

    // Warna neon dasar per peran
    private static readonly Color DartColor = new(2.2f, 2.4f, 0.4f, 1.0f);     // Kuning/Lime Terang
    private static readonly Color ShurikenColor = new(2.6f, 0.4f, 2.0f, 1.0f); // Magenta Elektrik
    private static readonly Color DrillColor = new(3.0f, 0.8f, 0.3f, 1.0f);    // Oranye/Crimson Bor
    private static readonly Color CrescentColor = new(0.4f, 2.8f, 1.2f, 1.0f); // Hijau Zamrud
    private static readonly Color GuardianColor = new(0.3f, 2.2f, 2.8f, 1.0f); // Cyan Perisai ]]
    private static readonly Color ScannerColor = new(2.5f, 0.5f, 3.6f, 1.0f);  // Violet Radar Sensor
    private static readonly Color MiningColor = new(3.4f, 1.6f, 0.3f, 1.0f);   // Oranye Magnet Harvester

    // Status hitungan unit real-time
    public int EntityCount => _entityCount;
    public int OrbitingCount => _boidSystem.OrbitingCount;
    public int LaunchedCount => _boidSystem.LaunchedCount;
    public int ReturningCount => _boidSystem.ReturningCount;
    public int EnemyCount => _boidSystem.EnemyCount;
    public int CoreCellCount => _playerCore?.CoreCellCount ?? 1;

    public int DrillDroneCount { get; private set; } = 0;
    public int GuardianCount { get; private set; } = 0;
    public int ScannerCount { get; private set; } = 0;
    public int MiningCount { get; private set; } = 0;
    public float DrillChargeRatio => Mathf.Clamp(_drillChargeTimer / MaxDrillChargeTime, 0.0f, 1.0f);
    public bool IsChargingDrill => _isChargingDrill;
    public bool IsGigaDrillActive => _isGigaDrillActive;

    // Telemetri Giga Drill untuk efek riak robekan ruang-waktu & exhaust roket pada GridPhysics (arena.gd)
    public Vector2 GigaDrillTipPosition { get; private set; } = Vector2.Zero;
    public Vector2 GigaDrillBasePosition { get; private set; } = Vector2.Zero;
    public Vector2 GigaDrillDirection { get; private set; } = Vector2.Right;
    public float GigaDrillLength { get; private set; } = 90.0f;
    public float GigaDrillRadius { get; private set; } = 32.0f;
    public float GigaDrillDensityFactor { get; private set; } = 1.0f;
    public float GigaDrillSpinTime => _gigaDrillSpinTime;
    public bool HasDefenderNoseBreached => _hasDefenderNoseBreached;
    public Vector2 GigaDrillTargetPosition => _gigaDrillTargetPos;
    public float GigaDrillNoseLength => (_activeDefenderCoreCount > 0 && !_hasDefenderNoseBreached) ? MathF.Min(56.0f, 20.0f + MathF.Sqrt(_activeDefenderCoreCount) * 6.5f) : 0.0f;
    public float GigaDrillNoseRadius => (_activeDefenderCoreCount > 0 && !_hasDefenderNoseBreached) ? MathF.Min(28.0f, 12.0f + MathF.Sqrt(_activeDefenderCoreCount) * 3.6f) : 0.0f;

    private int _entityCount = 0;

    // Timer laju tembakan kontinu (LMB stream fire)
    private float _fireCooldown = 0.0f;
    private const float StreamFireInterval = 0.035f;

    // Parameter Penyatuan Laser Penambangan (Mining Laser Convergence)
    private bool _isMiningLaserActive = false;
    private Vector2 _laserConvergencePoint = Vector2.Zero;
    private Vector2 _laserImpactPoint = Vector2.Zero;
    private readonly List<Vector2> _activeMinerPositions = new();
    private float _laserPulseTimer = 0.0f;

    // Parameter Super Giga Drill Break (Swarm-Formed, Staged Vortex, Sacrificial Exhaust)
    private bool _isChargingDrill = false;
    private float _drillChargeTimer = 0.0f;
    private const float MaxDrillChargeTime = 1.65f;
    private float _gigaDrillSpinTime = 0.0f;
    private float _overchargeExhaustTimer = 0.0f;

    private bool _isGigaDrillActive = false;
    private Vector2 _gigaDrillTipPos = Vector2.Zero;
    private Vector2 _gigaDrillAimDir = Vector2.Right;
    private Vector2 _gigaDrillVel = Vector2.Zero;
    private Vector2 _gigaDrillTargetPos = Vector2.Zero;
    private bool _hasDefenderNoseBreached = false;
    private float _gigaDrillFlightTimer = 0.0f;
    private float _gigaDrillElapsedFlight = 0.0f;
    private const float GigaDrillFlightDuration = 1.45f;

    private int _activeDrillFormationCount = 0;
    private int _activeDefenderCoreCount = 0;
    private int _gigaDrillExhaustStartIndex = 0;

    public int GigaDrillTotalUnits => _activeDrillFormationCount + _activeDefenderCoreCount;
    public int GigaDrillDefenderCoreCount => _activeDefenderCoreCount;
    public int GigaDrillLanceCount => _activeDrillFormationCount;

    public override void _Ready()
    {
        // 1. Inisialisasi Arch ECS
        _world = World.Create();
        _boidSystem = new BoidSystem(_world);
        _allSwarmQuery = new QueryDescription().WithAll<PositionComponent, VelocityComponent, SwarmUnitComponent, VisualComponent>();

        // 2. Ambil referensi node
        _playerCore = GetNode<PlayerCore>("PlayerCore");
        _renderer = GetNode<SwarmMultiMeshRenderer>("SwarmRenderer");

        // 3. Spawn kawanan starter awal (5 Scanner Swarm + 10 Mining Droid)
        SpawnStartingSwarm();
    }

    /// <summary>
    /// Memunculkan komposisi awal permainan: 5 Scanner Swarm + 10 Mining Droid
    /// </summary>
    public void SpawnStartingSwarm()
    {
        AddSpecificSwarm(SwarmRole.ScannerDrone, 5);
        AddSpecificSwarm(SwarmRole.MiningDroid, 10);
    }

    public void SpawnBalancedSwarm(int totalCount)
    {
        Vector2 corePos = _playerCore.GlobalPosition;

        for (int i = 0; i < totalCount; i++)
        {
            float initialAngle = GD.Randf() * Mathf.Tau;

            // Distribusi peran: 36% Dart, 16% Shuriken, 24% Drill (Lance), 12% Crescent, 12% Guardian (Defender)
            SwarmRole role;
            Color color;
            float scale = 0.52f;
            float orbitDist = 38.0f + GD.Randf() * 75.0f; // Pita orbit organik diperkecil untuk attacker
            int mod = i % 25;

            if (mod < 9)
            {
                role = SwarmRole.DartNeedle;
                color = DartColor;
                scale = 0.52f;
            }
            else if (mod < 13)
            {
                role = SwarmRole.GigaShuriken;
                color = ShurikenColor;
                scale = 0.56f;
            }
            else if (mod < 19)
            {
                role = SwarmRole.DrillDrone;
                color = DrillColor;
                scale = 0.58f;
                DrillDroneCount++;
            }
            else if (mod < 22)
            {
                role = SwarmRole.CrescentBoomerang;
                color = CrescentColor;
                scale = 0.54f;
            }
            else
            {
                // Defender berada di radius perisai ~88px dari Core dengan variasi organik
                role = SwarmRole.Guardian;
                color = GuardianColor;
                scale = 0.72f;
                orbitDist = 88.0f + (GD.Randf() - 0.5f) * 16.0f;
                GuardianCount++;
            }

            float orbitSpeed = (GD.Randf() > 0.45f ? 1.0f : -1.0f) * (0.8f + GD.Randf() * 0.7f);
            Vector2 spawnPos = corePos + new Vector2(MathF.Cos(initialAngle), MathF.Sin(initialAngle)) * orbitDist;

            _world.Create(
                new PositionComponent(spawnPos),
                new VelocityComponent(Vector2.Zero),
                new SwarmUnitComponent(role, 0, orbitDist, orbitSpeed),
                new VisualComponent(color, scale)
            );

            _entityCount++;
        }
    }

    public void AddSpecificSwarm(SwarmRole role, int count)
    {
        Vector2 corePos = _playerCore.GlobalPosition;
        Color color = role switch
        {
            SwarmRole.DartNeedle => DartColor,
            SwarmRole.GigaShuriken => ShurikenColor,
            SwarmRole.DrillDrone => DrillColor,
            SwarmRole.CrescentBoomerang => CrescentColor,
            SwarmRole.Guardian => GuardianColor,
            SwarmRole.ScannerDrone => ScannerColor,
            SwarmRole.MiningDroid => MiningColor,
            _ => DartColor
        };

        float targetScale = role switch
        {
            SwarmRole.Guardian => 0.72f,
            SwarmRole.ScannerDrone => 0.60f,
            SwarmRole.MiningDroid => 0.62f,
            _ => 0.55f
        };

        int remaining = count;

        // Gunakan kembali entitas Dead jika ada (Zero-GC Object Pooling)
        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
        {
            if (remaining <= 0 || swarm.State != SwarmState.Dead) return;

            float orbitDist = (role == SwarmRole.Guardian)
                ? 88.0f + (GD.Randf() - 0.5f) * 16.0f
                : 38.0f + GD.Randf() * 75.0f;
            float orbitSpeed = (GD.Randf() > 0.45f ? 1.0f : -1.0f) * (0.8f + GD.Randf() * 0.7f);
            float initialAngle = GD.Randf() * Mathf.Tau;

            pos.Value = corePos + new Vector2(MathF.Cos(initialAngle), MathF.Sin(initialAngle)) * orbitDist;
            vel.Value = Vector2.Zero;
            swarm = new SwarmUnitComponent(role, 0, orbitDist, orbitSpeed);
            vis = new VisualComponent(color, targetScale);

            if (role == SwarmRole.DrillDrone) DrillDroneCount++;
            if (role == SwarmRole.Guardian) GuardianCount++;
            if (role == SwarmRole.ScannerDrone) ScannerCount++;
            if (role == SwarmRole.MiningDroid) MiningCount++;
            _entityCount++;
            remaining--;
        });

        for (int i = 0; i < remaining; i++)
        {
            float orbitDist = (role == SwarmRole.Guardian)
                ? 88.0f + (GD.Randf() - 0.5f) * 16.0f
                : 38.0f + GD.Randf() * 75.0f;
            float orbitSpeed = (GD.Randf() > 0.45f ? 1.0f : -1.0f) * (0.8f + GD.Randf() * 0.7f);
            float initialAngle = GD.Randf() * Mathf.Tau;
            Vector2 spawnPos = corePos + new Vector2(MathF.Cos(initialAngle), MathF.Sin(initialAngle)) * orbitDist;

            if (role == SwarmRole.DrillDrone) DrillDroneCount++;
            if (role == SwarmRole.Guardian) GuardianCount++;
            if (role == SwarmRole.ScannerDrone) ScannerCount++;
            if (role == SwarmRole.MiningDroid) MiningCount++;

            _world.Create(
                new PositionComponent(spawnPos),
                new VelocityComponent(Vector2.Zero),
                new SwarmUnitComponent(role, 0, orbitDist, orbitSpeed),
                new VisualComponent(color, targetScale)
            );

            _entityCount++;
        }
    }

    /// <summary>
    /// Memunculkan gelombang serbuan 50 amunisi/swarm musuh dari arah kursor menuju Core
    /// untuk menguji pemindaian Spatial Grid 12 Sektor & barisan Defender organik.
    /// </summary>
    public void SpawnEnemySectorAttack(Vector2 attackOriginDirPos, int enemyCount = 50)
    {
        Vector2 corePos = _playerCore.GlobalPosition;
        Vector2 toAttack = attackOriginDirPos - corePos;
        Vector2 dirFromCore = toAttack.LengthSquared() > 10.0f ? toAttack.Normalized() : Vector2.Left;
        Vector2 tangent = new Vector2(-dirFromCore.Y, dirFromCore.X);

        Color enemyColor = new Color(3.4f, 0.35f, 0.9f, 1.0f);
        int remaining = enemyCount;

        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
        {
            if (remaining <= 0 || swarm.State != SwarmState.Dead) return;

            float dist = 360.0f + GD.Randf() * 130.0f;
            float lateral = (GD.Randf() - 0.5f) * 140.0f;
            Vector2 spawnPos = corePos + dirFromCore * dist + tangent * lateral;
            Vector2 moveDir = (corePos - spawnPos).Normalized().Rotated((GD.Randf() - 0.5f) * 0.14f);

            pos.Value = spawnPos;
            vel.Value = moveDir * (260.0f + GD.Randf() * 110.0f);
            swarm = new SwarmUnitComponent(SwarmRole.DartNeedle, ownerId: 1, orbitDist: 0.0f, orbitSpd: 0.0f)
            {
                State = SwarmState.Launched,
                MaxLifeTime = 3.2f,
                TargetDirection = moveDir
            };
            vis = new VisualComponent(enemyColor, 0.95f);
            remaining--;
        });

        for (int i = 0; i < remaining; i++)
        {
            float dist = 360.0f + GD.Randf() * 130.0f;
            float lateral = (GD.Randf() - 0.5f) * 140.0f;
            Vector2 spawnPos = corePos + dirFromCore * dist + tangent * lateral;
            Vector2 moveDir = (corePos - spawnPos).Normalized().Rotated((GD.Randf() - 0.5f) * 0.14f);

            var unit = new SwarmUnitComponent(SwarmRole.DartNeedle, ownerId: 1, orbitDist: 0.0f, orbitSpd: 0.0f)
            {
                State = SwarmState.Launched,
                MaxLifeTime = 3.2f,
                TargetDirection = moveDir
            };

            _world.Create(
                new PositionComponent(spawnPos),
                new VelocityComponent(moveDir * (260.0f + GD.Randf() * 110.0f)),
                unit,
                new VisualComponent(enemyColor, 0.95f)
            );
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        Vector2 corePos = _playerCore.GlobalPosition;
        Vector2 coreVel = _playerCore.Velocity;
        Vector2 mousePos = GetGlobalMousePosition();

        // 0. Update visibilitas scanner ke seluruh asteroid di semesta
        Asteroid.CurrentScannerCount = ScannerCount;

        // 1. Tembakan Beruntun & Laser Penambangan Bersatu (Tahan Klik Kiri)
        if (_fireCooldown > 0.0f) _fireCooldown -= dt;

        bool isLeftClick = Input.IsMouseButtonPressed(MouseButton.Left);
        if (isLeftClick && !_isChargingDrill && !_isGigaDrillActive)
        {
            if (MiningCount > 0)
            {
                UpdateMiningLaser(dt, corePos, mousePos);
            }

            if (_fireCooldown <= 0.0f)
            {
                FireSwarmStream(mousePos, count: 2);
                _fireCooldown = StreamFireInterval;
            }
        }
        else if (_isMiningLaserActive)
        {
            _isMiningLaserActive = false;
            QueueRedraw();
        }

        // 2. Mekanik Charge & Formasi Bertahap Super Giga Drill Break (Tahan Klik Kanan)
        HandleGigaDrillCharge(dt, corePos, coreVel, mousePos);

        // 3. Update Penerbangan Super Giga Drill Break & Pelepasan Exhaust Roket Belakang
        UpdateActiveGigaDrill(dt);

        // 4. Update Drone Exhaust Roket yang Sedang Terbakar & Mati
        UpdateExhaustDrones(dt);

        // 5. Cek Benturan Kinetik Swarm & Super Giga Drill vs Asteroid Bebas Melayang
        CheckAsteroidCollisions(dt);

        // 6. Jalankan simulasi Boids Flocking, Pemindaian 12 Sektor Spatial Grid, & State Machine
        _boidSystem.Update(dt, corePos, coreVel, mousePos);

        // 7. Update MultiMesh GPU buffer per peran (abaikan unit yang sudah Dead)
        _renderer.ResetCounters();
        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
        {
            if (swarm.State == SwarmState.Dead) return;
            _renderer.SetInstance((int)swarm.Role, pos.Value, vel.Value, swarm.SpinAngle, vis.Color, vis.Scale);
        });
        _renderer.FlushVisibleCounts();
    }

    /// <summary>
    /// Menghitung dimensi kerucut Giga Drill secara sub-linier agar semakin banyak lance
    /// maka struktur bor semakin PADAT (bukan sekadar membesar tanpa batas).
    /// </summary>
    private static void ComputeGigaDrillDimensions(int lanceCount, out float drillLength, out float drillBaseRadius, out float densityFactor)
    {
        int n = Math.Max(1, lanceCount);
        float sqrtN = MathF.Sqrt(n);

        // Pertumbuhan ukuran dibatasi secara sub-linier (akar kuadrat), sehingga tambahan unit
        // memadatkan bagian dalam kerucut (multi-layer shell & ekstra ulir)
        drillLength = Mathf.Clamp(76.0f + sqrtN * 5.4f, 82.0f, 172.0f);
        drillBaseRadius = Mathf.Clamp(23.0f + sqrtN * 1.95f, 25.0f, 60.0f);
        densityFactor = Mathf.Clamp(n / 65.0f, 0.6f, 4.5f);
    }

    private void BeginGigaDrillCharge(Vector2 corePos, Vector2 aimDir)
    {
        // =========================================================================
        // ATURAN MUTLAK: DRILL HANYA BISA DIBENTUK JIKA LANCE SUDAH BENAR-BENAR KEMBALI!
        // Unit yang masih dalam status Returning / Launched tidak boleh direkrut atau teleportasi.
        // =========================================================================
        int readyLanceCount = 0;
        _world.Query(in _allSwarmQuery, (ref SwarmUnitComponent swarm) =>
        {
            if (swarm.OwnerId == 0 && swarm.Role == SwarmRole.DrillDrone && swarm.State == SwarmState.Orbiting)
            {
                readyLanceCount++;
            }
        });

        // Jika jumlah lance yang sudah kembali ke orbit Core belum cukup (misal masih terbang pulang),
        // tahan formasi sampai unit-unit tersebut benar-benar tiba di Core!
        if (readyLanceCount < 6)
        {
            _isChargingDrill = false;
            return;
        }

        _isChargingDrill = true;
        _drillChargeTimer = 0.0f;
        _overchargeExhaustTimer = 0.0f;

        // Jika jumlah DrillDrone menipis akibat dikorbankan sebagai exhaust pada serangan sebelumnya,
        // rekrut sebagian DartNeedle (lance) yang sedang mengorbit untuk ikut menyusun Giga Drill.
        if (DrillDroneCount < 28)
        {
            int needed = 36 - DrillDroneCount;
            _world.Query(in _allSwarmQuery, (ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
            {
                if (needed <= 0) return;
                if (swarm.OwnerId == 0 && swarm.State == SwarmState.Orbiting && swarm.Role == SwarmRole.DartNeedle)
                {
                    swarm.Role = SwarmRole.DrillDrone;
                    swarm.SpinRate = 32.0f;
                    vis.BaseColor = DrillColor;
                    vis.Color = DrillColor;
                    vis.Scale = 0.95f;
                    DrillDroneCount++;
                    needed--;
                }
            });
        }

        // Kumpulkan HANYA DrillDrone yang SUDAH BERADA DI ORBIT CORE (State == Orbiting)
        // Unit yang sedang Returning atau Launched tetap terbang bebas dan dilarang teleportasi!
        Vector2 frontAnchor = corePos + aimDir * 70.0f;
        List<(float distSq, int tempId)> sortList = new();
        int tempCounter = 0;

        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref SwarmUnitComponent swarm) =>
        {
            if (swarm.State != SwarmState.Orbiting || swarm.OwnerId != 0 || swarm.Role != SwarmRole.DrillDrone)
                return;

            swarm.State = SwarmState.ChargingDrill;
            swarm.FormationIndex = tempCounter;
            sortList.Add((pos.Value.DistanceSquaredTo(frontAnchor), tempCounter));
            tempCounter++;
        });

        sortList.Sort((a, b) => a.distSq.CompareTo(b.distSq));
        int[] rankLookup = new int[tempCounter];
        for (int rank = 0; rank < sortList.Count; rank++)
        {
            rankLookup[sortList[rank].tempId] = rank;
        }

        _activeDrillFormationCount = tempCounter;
        // 28% unit paling belakang berfungsi sebagai pendorong Rocket Exhaust (dan akan dikorbankan/mati)
        int exhaustCount = Math.Max(2, (int)MathF.Round(_activeDrillFormationCount * 0.28f));
        if (_activeDrillFormationCount <= 4) exhaustCount = 1;
        _gigaDrillExhaustStartIndex = Math.Max(1, _activeDrillFormationCount - exhaustCount);

        _world.Query(in _allSwarmQuery, (ref SwarmUnitComponent swarm) =>
        {
            if (swarm.State == SwarmState.ChargingDrill && swarm.OwnerId == 0 && swarm.Role == SwarmRole.DrillDrone)
            {
                if (swarm.FormationIndex >= 0 && swarm.FormationIndex < rankLookup.Length)
                {
                    swarm.FormationIndex = rankLookup[swarm.FormationIndex];
                }
            }
        });

        // =========================================================================
        // REKRUT 25% DEFENDER SWARM SEBAGAI INTI PENETRATOR AP (ARMOR PIERCING)
        // Membentuk kerucut dalam (inner cone) berlapis tameng titanium/tungsten padat
        // =========================================================================
        int readyDefenderCount = 0;
        _world.Query(in _allSwarmQuery, (ref SwarmUnitComponent swarm) =>
        {
            if (swarm.OwnerId == 0 && swarm.Role == SwarmRole.Guardian && swarm.State == SwarmState.Orbiting)
            {
                readyDefenderCount++;
            }
        });

        int defenderAPQuota = Math.Max(2, (int)MathF.Round(readyDefenderCount * 0.25f));
        int defCounter = 0;

        _world.Query(in _allSwarmQuery, (ref SwarmUnitComponent swarm) =>
        {
            if (defCounter >= defenderAPQuota) return;
            if (swarm.OwnerId == 0 && swarm.Role == SwarmRole.Guardian && swarm.State == SwarmState.Orbiting)
            {
                swarm.State = SwarmState.ChargingDrill;
                swarm.FormationIndex = defCounter;
                defCounter++;
            }
        });

        _activeDefenderCoreCount = defCounter;
    }

    private void HandleGigaDrillCharge(float dt, Vector2 corePos, Vector2 coreVel, Vector2 mousePos)
    {
        if (Input.IsMouseButtonPressed(MouseButton.Right) && !_isGigaDrillActive)
        {
            Vector2 toMouse = mousePos - corePos;
            Vector2 aimDir = toMouse.LengthSquared() > 1.0f ? toMouse.Normalized() : Vector2.Right;
            Vector2 tangent = new Vector2(-aimDir.Y, aimDir.X);

            if (!_isChargingDrill)
            {
                BeginGigaDrillCharge(corePos, aimDir);
            }

            _drillChargeTimer += dt;
            float chargeProgress = DrillChargeRatio;

            // Kecepatan putar ulir meningkat secara bertahap seiring charge (spin-up mesin bor)
            float spinSpeed = Mathf.Lerp(9.0f, 38.0f, chargeProgress * chargeProgress);
            _gigaDrillSpinTime += dt * spinSpeed;

            int totalUnits = Math.Max(1, _activeDrillFormationCount);
            ComputeGigaDrillDimensions(totalUnits, out float drillLength, out float drillBaseRadius, out float densityFactor);

            // Ujung depan kerucut bor merentang maju secara bertahap saat pusaran memadat
            Vector2 tipPos = corePos + aimDir * (64.0f + chargeProgress * 38.0f);
            Vector2 basePos = tipPos - aimDir * drillLength;

            GigaDrillTipPosition = tipPos;
            GigaDrillBasePosition = basePos;
            GigaDrillDirection = aimDir;
            GigaDrillLength = drillLength;
            GigaDrillRadius = drillBaseRadius;
            GigaDrillDensityFactor = densityFactor;

            // Jumlah ulir spiral (flutes) dan lapisan kulit konsentris (shells) bertambah sesuai jumlah lance!
            int fluteCount = Math.Clamp(3 + totalUnits / 42, 3, 9);
            int shellCount = totalUnits >= 135 ? 3 : (totalUnits >= 55 ? 2 : 1);
            int exhaustStartIdx = _gigaDrillExhaustStartIndex;

            _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
            {
                if (swarm.State != SwarmState.ChargingDrill || swarm.OwnerId != 0)
                    return;

                // =========================================================================
                // SECOND CONE DI UJUNG BOR: 25% DEFENDER SWARM SEBAGAI NOSE SHIELD CAP (AP)
                // Membentuk kerucut pelindung kedua di depan mata bor utama (capping the tip),
                // menyerap tembakan musuh & mendobrak formasi pertahanan lawan sebelum pecah/mati!
                // =========================================================================
                if (swarm.Role == SwarmRole.Guardian)
                {
                    int defTotal = Math.Max(1, _activeDefenderCoreCount);
                    float defConeLength = MathF.Min(56.0f, 20.0f + MathF.Sqrt(defTotal) * 6.5f);
                    float defBaseRadius = MathF.Min(28.0f, 12.0f + MathF.Sqrt(defTotal) * 3.6f);

                    float defT = (defTotal > 1) ? (float)swarm.FormationIndex / (defTotal - 1) : 0.0f;

                    // defT = 0 adalah puncak kerucut kedua paling depan (forward piercing apex)
                    // defT = 1 adalah pangkal kerucut kedua yang menempel di ujung bor tipPos
                    float defDistForward = (1.0f - defT) * defConeLength;
                    float defConeProfile = MathF.Pow(defT, 0.78f);
                    float defR = defConeProfile * defBaseRadius;

                    int strand = swarm.FormationIndex % (defTotal >= 14 ? 3 : 2);
                    float defSpiralAngle = strand * (Mathf.Tau / (defTotal >= 14 ? 3 : 2))
                                         + defT * (Mathf.Tau * 2.0f)
                                         + _gigaDrillSpinTime * 0.95f;

                    float defActivationDelay = (1.0f - defT) * 0.35f;
                    float defLocalProgress = Mathf.Clamp((chargeProgress - defActivationDelay) / 0.55f, 0.0f, 1.0f);
                    float defSmoothLock = defLocalProgress * defLocalProgress * (3.0f - 2.0f * defLocalProgress);

                    float defVortexExpansion = (1.0f - defSmoothLock) * (32.0f + defR * 1.35f);
                    float defCurrentR = defR + defVortexExpansion;

                    // Posisi kerucut kedua berada di DEPAN tipPos (+ aimDir * defDistForward)
                    Vector2 defSlotPos = (tipPos + aimDir * defDistForward)
                                       + (tangent * (defCurrentR * MathF.Cos(defSpiralAngle)))
                                       + (aimDir * (0.18f * defCurrentR * MathF.Sin(defSpiralAngle)));

                    Vector2 toDefSlot = defSlotPos - pos.Value;
                    float defPull = Mathf.Lerp(8.0f, 26.0f, defSmoothLock);
                    Vector2 defTargetVel = toDefSlot * defPull + coreVel;

                    vel.Value = vel.Value.Lerp(defTargetVel, MathF.Min(1.0f, (7.0f + 16.0f * defSmoothLock) * dt));
                    pos.Value += vel.Value * dt;

                    if (defSmoothLock > 0.65f)
                    {
                        float defLockFactor = (defSmoothLock - 0.65f) / 0.35f;
                        pos.Value = pos.Value.Lerp(defSlotPos, defLockFactor * MathF.Min(1.0f, 18.0f * dt));
                    }

                    // Kurung siku ]] menghadap miring ke depan membentuk tameng AP balistik
                    swarm.SpinAngle = aimDir.Angle() + Mathf.Pi * 0.5f + MathF.Sin(defSpiralAngle) * 0.35f;

                    // Warna Sian Kristal AP Super Terang
                    vis.Color = (defT < 0.28f)
                        ? new Color(2.8f, 5.5f, 7.0f, 1.0f)
                        : new Color(0.9f, 3.8f, 5.5f, 1.0f);
                    vis.Scale = 1.15f;
                    return;
                }

                if (swarm.Role != SwarmRole.DrillDrone)
                    return;

                int idx = swarm.FormationIndex;
                bool isExhaustStage = idx >= exhaustStartIdx;

                // Normalisasi posisi aksial t (0.0 = ujung mata bor, 1.0 = pangkal nozzle roket belakang)
                float t = (totalUnits > 1) ? (float)idx / (totalUnits - 1) : 0.0f;

                // Pembentukan bertahap (Progressive Wave Lock):
                // Ujung mata bor (t=0) mengunci lebih dulu, diikuti badan bor, lalu cincin exhaust di belakang
                float activationDelay = t * 0.44f;
                float localProgress = Mathf.Clamp((chargeProgress - activationDelay) / 0.54f, 0.0f, 1.0f);
                float smoothLock = localProgress * localProgress * (3.0f - 2.0f * localProgress);

                float d;
                float rCone;
                float spiralAngle;

                if (!isExhaustStage)
                {
                    // --- BAGIAN MATA BOR & BADAN MULTI-LAPIS PADAT ---
                    float bodyT = (exhaustStartIdx > 1) ? (float)idx / (exhaustStartIdx - 1) : 0.0f;
                    d = bodyT * (drillLength * 0.84f);

                    // Lapisan konsentris: Lapisan 0 = Kulit luar, Lapisan 1 = Inti tengah, Lapisan 2 = Tulang punggung plasma
                    int shell = idx % shellCount;
                    float shellRadiusMult = shell switch
                    {
                        0 => 1.0f,
                        1 => 0.62f,
                        _ => 0.28f
                    };
                    float shellSpinDir = (shell == 1) ? -0.75f : 1.0f;

                    float coneProfile = MathF.Pow(bodyT, 0.82f);
                    rCone = coneProfile * drillBaseRadius * shellRadiusMult;

                    int flute = idx % fluteCount;
                    spiralAngle = flute * (Mathf.Tau / fluteCount)
                                + bodyT * (Mathf.Tau * 3.0f)
                                + _gigaDrillSpinTime * shellSpinDir;
                }
                else
                {
                    // --- BAGIAN BELAKANG: NOZZLE ROCKET EXHAUST (Akan Terbakar & Mati) ---
                    int exhaustCount = Math.Max(1, totalUnits - exhaustStartIdx);
                    float exhaustT = (float)(idx - exhaustStartIdx) / exhaustCount;

                    d = drillLength * (0.84f + exhaustT * 0.20f);
                    // Membentuk corong thruster roket (nozzle bell) yang sedikit melebar di bagian belakang
                    rCone = drillBaseRadius * (0.55f + 0.55f * exhaustT);

                    int nozzleRing = (idx - exhaustStartIdx) % Math.Max(4, fluteCount);
                    spiralAngle = nozzleRing * (Mathf.Tau / Math.Max(4, fluteCount))
                                - _gigaDrillSpinTime * 1.45f
                                + exhaustT * Mathf.Pi;
                }

                // Efek Vortex Saat Proses Pembentukan:
                // Saat smoothLock masih rendah, drone berputar dalam cincin pusaran lebar di depan Core
                // lalu memadat secara spiral ke dalam kerucut bor (tidak langsung snap instan!)
                float vortexExpansion = (1.0f - smoothLock) * (36.0f + rCone * 1.45f);
                float currentRadius = rCone + vortexExpansion;

                // Getaran mekanis berenergi tinggi saat mendekati charge penuh
                float jitter = (chargeProgress > 0.75f)
                    ? MathF.Sin(_gigaDrillSpinTime * 4.0f + idx * 1.7f) * (isExhaustStage ? 3.5f : 1.2f)
                    : 0.0f;

                Vector2 slotPos = (tipPos - aimDir * d)
                                + (tangent * ((currentRadius + jitter) * MathF.Cos(spiralAngle)))
                                + (aimDir * (0.28f * currentRadius * MathF.Sin(spiralAngle)));

                // Kemudikan kecepatan drone dengan gaya pegas + pusaran tangensial (bukan teleportasi instan!)
                Vector2 toSlot = slotPos - pos.Value;
                float springPull = Mathf.Lerp(7.5f, 24.0f, smoothLock);
                Vector2 vortexTangentVel = tangent * (MathF.Cos(spiralAngle) * 240.0f * (1.0f - smoothLock * 0.65f));
                Vector2 targetVel = toSlot * springPull + vortexTangentVel + coreVel;

                vel.Value = vel.Value.Lerp(targetVel, MathF.Min(1.0f, (6.5f + 14.0f * smoothLock) * dt));
                pos.Value += vel.Value * dt;

                // Saat sudah hampir terkunci (>75%), bantu penguncian posisi presisi agar kerucut tampak tajam
                if (smoothLock > 0.65f)
                {
                    float lockFactor = (smoothLock - 0.65f) / 0.35f;
                    pos.Value = pos.Value.Lerp(slotPos, lockFactor * MathF.Min(1.0f, 18.0f * dt));
                }

                // Orientasi drone: bagian badan menghadap ke depan miring sesuai ulir,
                // sedangkan bagian exhaust belakang bergetar siap menyembur ke belakang
                if (!isExhaustStage)
                {
                    float targetSpin = aimDir.Angle() + Mathf.Pi * 0.5f + MathF.Sin(spiralAngle) * 0.42f;
                    swarm.SpinAngle = Mathf.LerpAngle(swarm.SpinAngle, targetSpin, MathF.Min(1.0f, 14.0f * dt));

                    // Gradasi warna plasma berdasar kepadatan dan posisi
                    if (idx < Math.Max(4, totalUnits / 12))
                    {
                        vis.Color = new Color(4.0f, 4.0f, 2.6f, 1.0f); // Ujung mata bor putih-emas membara
                        vis.Scale = 1.08f;
                    }
                    else
                    {
                        float heat = Mathf.Lerp(0.85f, 1.35f, smoothLock);
                        vis.Color = new Color(3.2f * heat, 1.05f * heat, 0.35f, 1.0f);
                        vis.Scale = 0.96f;
                    }
                }
                else
                {
                    // Unit penyusun exhaust belakang menyala biru-putih/oranye roket dan menghadap ke belakang
                    float targetSpin = (-aimDir).Angle() + Mathf.Pi * 0.5f + MathF.Sin(spiralAngle * 2.0f) * 0.25f;
                    swarm.SpinAngle = Mathf.LerpAngle(swarm.SpinAngle, targetSpin, MathF.Min(1.0f, 14.0f * dt));

                    float ignitionPulse = 0.5f + 0.5f * MathF.Sin(_gigaDrillSpinTime * 3.2f + idx);
                    vis.Color = new Color(
                        Mathf.Lerp(3.4f, 4.2f, ignitionPulse * chargeProgress),
                        Mathf.Lerp(0.9f, 2.8f, ignitionPulse * chargeProgress),
                        Mathf.Lerp(0.4f, 3.2f, chargeProgress),
                        1.0f
                    );
                    vis.Scale = Mathf.Lerp(0.95f, 1.22f, chargeProgress);
                }
            });

            // Jika pemain menahan charge penuh (>92%), semburkan percikan exhaust awal dari bagian paling belakang!
            if (chargeProgress >= 0.92f && _activeDrillFormationCount > 6)
            {
                _overchargeExhaustTimer += dt;
                if (_overchargeExhaustTimer >= 0.14f)
                {
                    _overchargeExhaustTimer = 0.0f;
                    TriggerSingleRearExhaust(basePos, aimDir, 720.0f);
                }
            }
        }
        else if (_isChargingDrill)
        {
            // Tombol Klik Kanan dilepaskan!
            _isChargingDrill = false;
            float chargeProgress = DrillChargeRatio;

            Vector2 toMouse = mousePos - corePos;
            Vector2 aimDir = toMouse.LengthSquared() > 1.0f ? toMouse.Normalized() : Vector2.Right;

            if (chargeProgress >= 0.30f && _activeDrillFormationCount > 0)
            {
                LaunchSuperGigaDrill(corePos, aimDir, mousePos, chargeProgress);
            }
            else
            {
                // Klik kanan terlalu singkat -> batalkan formasi bor dan tembakkan salvo biasa
                _world.Query(in _allSwarmQuery, (ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
                {
                    if (swarm.State == SwarmState.ChargingDrill)
                    {
                        swarm.State = SwarmState.Orbiting;
                        vis.Color = vis.BaseColor;
                        vis.Scale = (swarm.Role == SwarmRole.Guardian) ? 1.15f : 0.95f;
                    }
                });
                _activeDefenderCoreCount = 0;
                FireSwarmVolley(mousePos, 25);
            }

            _drillChargeTimer = 0.0f;
        }
    }

    /// <summary>
    /// Melepaskan 1 unit paling belakang dari formasi Giga Drill untuk menjadi semburan api roket (dan mati).
    /// </summary>
    private void TriggerSingleRearExhaust(Vector2 nozzlePos, Vector2 drillAimDir, float baseExhaustSpeed)
    {
        int highestIdx = -1;
        _world.Query(in _allSwarmQuery, (ref SwarmUnitComponent swarm) =>
        {
            if ((swarm.State == SwarmState.ChargingDrill || swarm.State == SwarmState.Launched)
                && swarm.Role == SwarmRole.DrillDrone && swarm.OwnerId == 0)
            {
                if (swarm.FormationIndex > highestIdx)
                {
                    highestIdx = swarm.FormationIndex;
                }
            }
        });

        if (highestIdx < _gigaDrillExhaustStartIndex || highestIdx <= 1) return;

        bool triggered = false;
        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
        {
            if (triggered) return;
            if ((swarm.State == SwarmState.ChargingDrill || swarm.State == SwarmState.Launched)
                && swarm.Role == SwarmRole.DrillDrone && swarm.OwnerId == 0
                && swarm.FormationIndex == highestIdx)
            {
                IgniteUnitAsExhaust(ref pos, ref vel, ref swarm, ref vis, nozzlePos, drillAimDir, baseExhaustSpeed);
                _activeDrillFormationCount = Math.Max(1, _activeDrillFormationCount - 1);
                triggered = true;
            }
        });
    }

    private static void IgniteUnitAsExhaust(
        ref PositionComponent pos,
        ref VelocityComponent vel,
        ref SwarmUnitComponent swarm,
        ref VisualComponent vis,
        Vector2 nozzlePos,
        Vector2 drillAimDir,
        float baseExhaustSpeed)
    {
        swarm.State = SwarmState.DrillExhaust;
        swarm.LifeTime = 0.0f;
        swarm.MaxLifeTime = 0.28f + GD.Randf() * 0.16f;

        Vector2 tangent = new Vector2(-drillAimDir.Y, drillAimDir.X);
        float radialJitter = (GD.Randf() - 0.5f) * 26.0f;
        pos.Value = nozzlePos + tangent * radialJitter;

        // Semburkan ke arah belakang berlawanan arah laju Giga Drill (seperti afterburner roket)
        float plumeSpread = (GD.Randf() - 0.5f) * 0.52f;
        Vector2 exhaustDir = (-drillAimDir).Rotated(plumeSpread);
        float speed = baseExhaustSpeed * (0.8f + GD.Randf() * 0.45f);

        vel.Value = exhaustDir * speed;
        swarm.TargetDirection = exhaustDir;
        swarm.SpinAngle = exhaustDir.Angle() + Mathf.Pi * 0.5f;

        // Warna awal putih-sian super panas di mulut nozzle
        vis.Color = new Color(4.2f, 3.8f, 2.8f, 1.0f);
        vis.Scale = 1.38f;
    }

    private void LaunchSuperGigaDrill(Vector2 corePos, Vector2 aimDir, Vector2 targetPos, float chargeProgress)
    {
        _isGigaDrillActive = true;
        _gigaDrillTipPos = corePos + aimDir * (64.0f + chargeProgress * 38.0f);
        _gigaDrillAimDir = aimDir;
        _gigaDrillTargetPos = targetPos;
        _hasDefenderNoseBreached = false;
        _gigaDrillVel = aimDir * (1650.0f + chargeProgress * 450.0f);
        _gigaDrillFlightTimer = GigaDrillFlightDuration;
        _gigaDrillElapsedFlight = 0.0f;

        int totalUnits = Math.Max(1, _activeDrillFormationCount);
        int exhaustStartIdx = _gigaDrillExhaustStartIndex;
        int exhaustCount = Math.Max(1, totalUnits - exhaustStartIdx);

        ComputeGigaDrillDimensions(totalUnits, out float drillLength, out float drillBaseRadius, out _);
        Vector2 nozzlePos = _gigaDrillTipPos - aimDir * drillLength;

        // Pancarkan sinyal peluncuran Super Giga Drill untuk memicu efek Ion Beam & screen shake
        EmitSignal(SignalName.GigaDrillLaunched, _gigaDrillTipPos, nozzlePos, aimDir, chargeProgress);

        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
        {
            if (swarm.State == SwarmState.ChargingDrill && swarm.OwnerId == 0)
            {
                swarm.State = SwarmState.Launched;
                swarm.TargetDirection = aimDir;
                swarm.LifeTime = 0.0f;
                swarm.MaxLifeTime = GigaDrillFlightDuration + 0.2f;

                if (swarm.Role == SwarmRole.DrillDrone && swarm.FormationIndex >= exhaustStartIdx)
                {
                    // Jadwalkan waktu ledakan semburan roket secara beruntun selama penerbangan Giga Drill
                    // dari paling belakang ke depan sehingga membentuk ekor roket kontinu!
                    int orderFromBack = (totalUnits - 1) - swarm.FormationIndex;
                    float ratio = (float)orderFromBack / exhaustCount;
                    swarm.ExhaustTriggerTime = ratio * (GigaDrillFlightDuration * 0.82f);

                    // Ledakan pendorong tahap pertama tepat saat peluncuran (Ignition Burst!)
                    if (swarm.ExhaustTriggerTime <= 0.04f)
                    {
                        IgniteUnitAsExhaust(ref pos, ref vel, ref swarm, ref vis, nozzlePos, aimDir, 1050.0f);
                    }
                }
                else
                {
                    swarm.ExhaustTriggerTime = -1.0f;
                }
            }
        });
    }

    /// <summary>
    /// Meledakkan perisai hidung (second cone) defender di depan bor saat mendekati target atau mendobrak benteng musuh.
    /// Defender berkorban (mati), dan peluru bor di belakangnya terus melesat dengan akselerasi tembus!
    /// </summary>
    private void TriggerNoseShieldBreach(float defConeLength)
    {
        _hasDefenderNoseBreached = true;
        Vector2 breachCenter = _gigaDrillTipPos + _gigaDrillAimDir * (defConeLength * 0.5f);

        int breachedCount = 0;
        _world.Query(in _allSwarmQuery, (ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
        {
            if (swarm.OwnerId == 0 && swarm.Role == SwarmRole.Guardian && swarm.State == SwarmState.Launched)
            {
                swarm.State = SwarmState.Dead;
                vis.Color = new Color(0, 0, 0, 0);
                breachedCount++;
            }
        });

        _entityCount = Math.Max(0, _entityCount - breachedCount);
        GuardianCount = Math.Max(0, GuardianCount - breachedCount);
        _activeDefenderCoreCount = 0;

        // "dan peluru dilanjutkan" -> Sisa bor mendapatkan dorongan akselerasi tembus roket!
        _gigaDrillVel *= 1.38f;

        EmitSignal(SignalName.GigaDrillNoseBreached, breachCenter, _gigaDrillAimDir, breachedCount);
    }

    private void UpdateActiveGigaDrill(float dt)
    {
        if (!_isGigaDrillActive) return;

        _gigaDrillFlightTimer -= dt;
        _gigaDrillElapsedFlight += dt;
        _gigaDrillSpinTime += dt * 44.0f;
        _gigaDrillTipPos += _gigaDrillVel * dt;

        int totalUnits = Math.Max(1, _activeDrillFormationCount);
        int exhaustStartIdx = Math.Max(1, _gigaDrillExhaustStartIndex);
        ComputeGigaDrillDimensions(totalUnits, out float drillLength, out float drillBaseRadius, out float densityFactor);

        Vector2 tangent = new Vector2(-_gigaDrillAimDir.Y, _gigaDrillAimDir.X);
        Vector2 basePos = _gigaDrillTipPos - _gigaDrillAimDir * drillLength;

        GigaDrillTipPosition = _gigaDrillTipPos;
        GigaDrillBasePosition = basePos;
        GigaDrillDirection = _gigaDrillAimDir;
        GigaDrillLength = drillLength;
        GigaDrillRadius = drillBaseRadius;
        GigaDrillDensityFactor = densityFactor;

        // Cek Pemicu Pecahnya Perisai Kerucut Depan (Defender Nose Breach):
        // 1. Sudah mendekati target tujuan (distToTarget <= 140px)
        // 2. ATAU sudah menempuh 65% durasi penerbangan (jika player mengklik target sangat jauh di ujung layar)
        // 3. ATAU menabrak konsentrasi pertahanan musuh di depan hidung
        if (!_hasDefenderNoseBreached && _activeDefenderCoreCount > 0)
        {
            float distToTarget = _gigaDrillTipPos.DistanceTo(_gigaDrillTargetPos);
            bool nearTarget = distToTarget <= 140.0f && _gigaDrillElapsedFlight >= 0.12f;
            bool flightProgressBreach = _gigaDrillElapsedFlight >= GigaDrillFlightDuration * 0.65f;

            float defConeLength = MathF.Min(56.0f, 20.0f + MathF.Sqrt(_activeDefenderCoreCount) * 6.5f);
            Vector2 noseApex = _gigaDrillTipPos + _gigaDrillAimDir * defConeLength;
            int nearbyEnemies = 0;
            _world.Query(in _allSwarmQuery, (ref PositionComponent p, ref SwarmUnitComponent s) =>
            {
                if (s.OwnerId != 0 && s.State != SwarmState.Dead)
                {
                    if (p.Value.DistanceSquaredTo(noseApex) <= 65.0f * 65.0f)
                    {
                        nearbyEnemies++;
                    }
                }
            });
            bool enemyImpactBreach = nearbyEnemies >= 2;

            if (nearTarget || flightProgressBreach || enemyImpactBreach)
            {
                TriggerNoseShieldBreach(defConeLength);
            }
        }

        int fluteCount = Math.Clamp(3 + totalUnits / 42, 3, 9);
        int shellCount = totalUnits >= 135 ? 3 : (totalUnits >= 55 ? 2 : 1);

        // Pertahankan susunan kerucut padat multi-lapis & lepaskan unit bagian belakang sebagai exhaust roket
        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
        {
            if (swarm.State != SwarmState.Launched || swarm.OwnerId != 0)
                return;

            // =========================================================================
            // SECOND CONE DI UJUNG BOR: DEFENDER MELUNCUR SEBAGAI TAMENG DEPAN (AP CAP)
            // =========================================================================
            if (swarm.Role == SwarmRole.Guardian)
            {
                if (_hasDefenderNoseBreached) return;

                int defTotal = Math.Max(1, _activeDefenderCoreCount);
                float defConeLength = MathF.Min(56.0f, 20.0f + MathF.Sqrt(defTotal) * 6.5f);
                float defBaseRadius = MathF.Min(28.0f, 12.0f + MathF.Sqrt(defTotal) * 3.6f);

                float defT = (defTotal > 1) ? (float)swarm.FormationIndex / (defTotal - 1) : 0.0f;
                float defDistForward = (1.0f - defT) * defConeLength;
                float defR = MathF.Pow(defT, 0.78f) * defBaseRadius;

                int strand = swarm.FormationIndex % (defTotal >= 14 ? 3 : 2);
                float defSpiralAngle = strand * (Mathf.Tau / (defTotal >= 14 ? 3 : 2))
                                     + defT * (Mathf.Tau * 2.0f)
                                     + _gigaDrillSpinTime * 0.95f;

                Vector2 defTargetPos = (_gigaDrillTipPos + _gigaDrillAimDir * defDistForward)
                                     + (tangent * (defR * MathF.Cos(defSpiralAngle)))
                                     + (_gigaDrillAimDir * (0.18f * defR * MathF.Sin(defSpiralAngle)));

                pos.Value = defTargetPos;
                vel.Value = _gigaDrillVel;
                swarm.SpinAngle = _gigaDrillAimDir.Angle() + Mathf.Pi * 0.5f + MathF.Sin(defSpiralAngle) * 0.35f;
                vis.Color = (defT < 0.28f)
                    ? new Color(2.8f, 5.5f, 7.0f, 1.0f)
                    : new Color(0.9f, 3.8f, 5.5f, 1.0f);
                vis.Scale = 1.15f;
                return;
            }

            if (swarm.Role != SwarmRole.DrillDrone)
                return;

            // Cek apakah unit bagian belakang ini sudah waktunya terbakar menjadi Rocket Exhaust
            if (swarm.ExhaustTriggerTime >= 0.0f && _gigaDrillElapsedFlight >= swarm.ExhaustTriggerTime)
            {
                IgniteUnitAsExhaust(ref pos, ref vel, ref swarm, ref vis, basePos, _gigaDrillAimDir, 980.0f);
                return;
            }

            int idx = swarm.FormationIndex;
            bool isExhaustStage = idx >= exhaustStartIdx;

            float d;
            float r;
            float spiralAngle;

            if (!isExhaustStage)
            {
                float bodyT = (exhaustStartIdx > 1) ? (float)idx / (exhaustStartIdx - 1) : 0.0f;
                d = bodyT * (drillLength * 0.84f);

                int shell = idx % shellCount;
                float shellRadiusMult = shell switch
                {
                    0 => 1.0f,
                    1 => 0.62f,
                    _ => 0.28f
                };
                float shellSpinDir = (shell == 1) ? -0.75f : 1.0f;

                r = MathF.Pow(bodyT, 0.82f) * drillBaseRadius * shellRadiusMult;
                int flute = idx % fluteCount;
                spiralAngle = flute * (Mathf.Tau / fluteCount)
                            + bodyT * (Mathf.Tau * 3.0f)
                            + _gigaDrillSpinTime * shellSpinDir;
            }
            else
            {
                int exhaustCount = Math.Max(1, totalUnits - exhaustStartIdx);
                float exhaustT = (float)(idx - exhaustStartIdx) / exhaustCount;
                d = drillLength * (0.84f + exhaustT * 0.16f);
                r = drillBaseRadius * (0.55f + 0.45f * exhaustT);

                int nozzleRing = (idx - exhaustStartIdx) % Math.Max(4, fluteCount);
                spiralAngle = nozzleRing * (Mathf.Tau / Math.Max(4, fluteCount)) - _gigaDrillSpinTime * 1.5f;
            }

            Vector2 targetPos = (_gigaDrillTipPos - _gigaDrillAimDir * d)
                              + (tangent * (r * MathF.Cos(spiralAngle)))
                              + (_gigaDrillAimDir * (0.28f * r * MathF.Sin(spiralAngle)));

            pos.Value = targetPos;
            vel.Value = _gigaDrillVel;
            swarm.SpinAngle = _gigaDrillAimDir.Angle() + Mathf.Pi * 0.5f + MathF.Sin(spiralAngle) * 0.48f;

            vis.Color = (idx < Math.Max(4, totalUnits / 12))
                ? new Color(4.2f, 4.0f, 2.6f, 1.0f)
                : new Color(3.5f, 1.25f, 0.4f, 1.0f);
        });

        // Hancurkan unit musuh yang dilindas oleh kerucut Giga Drill & perisai hidung
        float killRadiusSq = (drillBaseRadius + 30.0f) * (drillBaseRadius + 30.0f);
        Vector2 noseTip = (!_hasDefenderNoseBreached && _activeDefenderCoreCount > 0)
            ? _gigaDrillTipPos + _gigaDrillAimDir * MathF.Min(56.0f, 20.0f + MathF.Sqrt(_activeDefenderCoreCount) * 6.5f)
            : _gigaDrillTipPos;

        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref SwarmUnitComponent swarm) =>
        {
            if (swarm.State == SwarmState.Dead || swarm.OwnerId == 0) return;
            if (pos.Value.DistanceSquaredTo(_gigaDrillTipPos) <= killRadiusSq
                || pos.Value.DistanceSquaredTo(basePos) <= killRadiusSq
                || pos.Value.DistanceSquaredTo(noseTip) <= killRadiusSq)
            {
                swarm.State = SwarmState.Dead;
            }
        });

        // Waktu tembus habis -> Sisa unit belakang yang belum meledak ikut terbakar sebagai final exhaust,
        // sedangkan badan utama bor pecah dan terbang pulang ke Core!
        if (_gigaDrillFlightTimer <= 0.0f)
        {
            _isGigaDrillActive = false;
            int sacrificedCount = Math.Max(1, _activeDrillFormationCount - _gigaDrillExhaustStartIndex);
            EmitSignal(SignalName.GigaDrillDetonated, _gigaDrillTipPos, basePos, sacrificedCount);

            _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
            {
                if (swarm.State == SwarmState.Launched && swarm.OwnerId == 0)
                {
                    if (swarm.Role == SwarmRole.DrillDrone)
                    {
                        if (swarm.ExhaustTriggerTime >= 0.0f)
                        {
                            IgniteUnitAsExhaust(ref pos, ref vel, ref swarm, ref vis, basePos, _gigaDrillAimDir, 850.0f);
                        }
                        else
                        {
                            swarm.State = SwarmState.Returning;
                            swarm.LifeTime = 0.0f;
                            float scatter = (GD.Randf() - 0.5f) * Mathf.Tau;
                            vel.Value = new Vector2(MathF.Cos(scatter), MathF.Sin(scatter)) * 420.0f;
                        }
                    }
                    else if (swarm.Role == SwarmRole.Guardian)
                    {
                        // Jika perisai hidung belum meledak sebelum detonasi akhir, mereka hancur bersama detonasi
                        swarm.State = SwarmState.Dead;
                        vis.Color = new Color(0, 0, 0, 0);
                        _entityCount = Math.Max(0, _entityCount - 1);
                        GuardianCount = Math.Max(0, GuardianCount - 1);
                    }
                }
            });
            _activeDefenderCoreCount = 0;
        }
    }

    /// <summary>
    /// Memperbarui pergerakan, perubahan warna api roket, penyusutan skala,
    /// hingga kematian permanen (pengorbanan swarm) pada drone exhaust bagian belakang Giga Drill.
    /// </summary>
    private void UpdateExhaustDrones(float dt)
    {
        float drag = MathF.Pow(0.92f, dt * 60.0f);

        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
        {
            if (swarm.State != SwarmState.DrillExhaust) return;

            swarm.LifeTime += dt;
            float lifeRatio = Mathf.Clamp(swarm.LifeTime / MathF.Max(0.05f, swarm.MaxLifeTime), 0.0f, 1.0f);

            // Turbulensi semburan jet roket
            Vector2 perp = new Vector2(-swarm.TargetDirection.Y, swarm.TargetDirection.X);
            float flutter = MathF.Sin(swarm.LifeTime * 48.0f + swarm.FormationIndex * 2.3f) * 320.0f;
            vel.Value = (vel.Value + perp * (flutter * dt)) * drag;
            pos.Value += vel.Value * dt;
            swarm.SpinAngle += 28.0f * dt;

            // Transisi warna api exhaust: Putih-Biru Plasma -> Oranye-Merah Roket -> Bara Ungu Redup
            if (lifeRatio < 0.30f)
            {
                float u = lifeRatio / 0.30f;
                vis.Color = new Color(
                    Mathf.Lerp(4.2f, 3.8f, u),
                    Mathf.Lerp(3.8f, 1.6f, u),
                    Mathf.Lerp(2.8f, 0.35f, u),
                    1.0f
                );
                vis.Scale = Mathf.Lerp(1.42f, 1.10f, u);
            }
            else if (lifeRatio < 0.72f)
            {
                float u = (lifeRatio - 0.30f) / 0.42f;
                vis.Color = new Color(
                    Mathf.Lerp(3.8f, 2.6f, u),
                    Mathf.Lerp(1.6f, 0.35f, u),
                    Mathf.Lerp(0.35f, 0.65f, u),
                    Mathf.Lerp(1.0f, 0.75f, u)
                );
                vis.Scale = Mathf.Lerp(1.10f, 0.55f, u);
            }
            else
            {
                float u = (lifeRatio - 0.72f) / 0.28f;
                vis.Color = new Color(
                    Mathf.Lerp(2.6f, 0.8f, u),
                    Mathf.Lerp(0.35f, 0.05f, u),
                    Mathf.Lerp(0.65f, 0.25f, u),
                    Mathf.Lerp(0.75f, 0.0f, u)
                );
                vis.Scale = Mathf.Lerp(0.55f, 0.12f, u);
            }

            // Habis terbakar -> MATI PERMANEN (mengorbankan swarm!)
            if (swarm.LifeTime >= swarm.MaxLifeTime)
            {
                swarm.State = SwarmState.Dead;
                if (swarm.OwnerId == 0)
                {
                    _entityCount = Math.Max(0, _entityCount - 1);
                    if (swarm.Role == SwarmRole.DrillDrone)
                    {
                        DrillDroneCount = Math.Max(0, DrillDroneCount - 1);
                    }
                    else if (swarm.Role == SwarmRole.Guardian)
                    {
                        GuardianCount = Math.Max(0, GuardianCount - 1);
                    }
                }
            }
        });
    }

    /// <summary>
    /// Memeriksa tumbukan kinetik antara proyektil swarm yang meluncur (Launched) dan
    /// terjangan Super Giga Drill Break terhadap Asteroid yang melayang bebas di arena.
    /// Memberikan efek dorongan fisik (impulse), pantulan swarm, serta retakan visual.
    /// </summary>
    private void CheckAsteroidCollisions(float dt)
    {
        var asteroids = Asteroid.ActiveAsteroids;
        if (asteroids.Count == 0) return;

        // 1. Tumbukan Proyektil Swarm (Launched Attacker) vs Asteroid
        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm) =>
        {
            if (swarm.State != SwarmState.Launched || swarm.OwnerId != 0) return;

            for (int i = 0; i < asteroids.Count; i++)
            {
                var ast = asteroids[i];
                if (!GodotObject.IsInstanceValid(ast)) continue;

                float hitRadius = ast.Radius + 10.0f;
                if (pos.Value.DistanceSquaredTo(ast.GlobalPosition) <= hitRadius * hitRadius)
                {
                    // Berikan impuls benturan kinetik pada asteroid
                    Vector2 impactVelocity = vel.Value;
                    ast.ApplyHitImpulse(pos.Value, impactVelocity * 0.14f, 12.0f);

                    // Pantulkan unit swarm (masuk mode Returning pulang ke Core)
                    swarm.State = SwarmState.Returning;
                    swarm.LifeTime = 0.0f;
                    Vector2 bounceDir = (pos.Value - ast.GlobalPosition).Normalized();
                    vel.Value = bounceDir * (440.0f + GD.Randf() * 160.0f);
                    break;
                }
            }
        });

        // 2. Terjangan Dahsyat Super Giga Drill Break vs Asteroid
        if (_isGigaDrillActive)
        {
            for (int i = 0; i < asteroids.Count; i++)
            {
                var ast = asteroids[i];
                if (!GodotObject.IsInstanceValid(ast)) continue;

                float drillHitRadius = ast.Radius + GigaDrillRadius + 22.0f;
                if (ast.GlobalPosition.DistanceSquaredTo(_gigaDrillTipPos) <= drillHitRadius * drillHitRadius
                    || ast.GlobalPosition.DistanceSquaredTo(GigaDrillBasePosition) <= drillHitRadius * drillHitRadius)
                {
                    ast.ApplyDrillBreach(_gigaDrillTipPos, _gigaDrillVel, 45000.0f);
                }
            }
        }
    }

    /// <summary>
    /// Menghitung konvergensi sinar laser penambangan dari seluruh Mining Droid (Tapal Kuda U).
    /// Laser dari masing-masing miner menyatu di satu titik fusi di depan Core, lalu
    /// memancar keluar sebagai satu sorotan laser raksasa berdaya hancur tinggi ke arah kursor/asteroid.
    /// </summary>
    private void UpdateMiningLaser(float dt, Vector2 corePos, Vector2 mousePos)
    {
        _activeMinerPositions.Clear();
        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref SwarmUnitComponent swarm) =>
        {
            if (swarm.OwnerId == 0 && swarm.State == SwarmState.Orbiting && swarm.Role == SwarmRole.MiningDroid)
            {
                _activeMinerPositions.Add(pos.Value);
            }
        });

        if (_activeMinerPositions.Count == 0)
        {
            if (_isMiningLaserActive)
            {
                _isMiningLaserActive = false;
                QueueRedraw();
            }
            return;
        }

        _isMiningLaserActive = true;
        _laserPulseTimer += dt;

        Vector2 toMouse = mousePos - corePos;
        Vector2 aimDir = toMouse.LengthSquared() > 1.0f ? toMouse.Normalized() : Vector2.Right;

        // Titik temu fusi laser di depan kapal induk
        _laserConvergencePoint = corePos + aimDir * 76.0f;

        // Hitung benturan sinar laser ke Asteroid terdekat
        float maxRange = 2800.0f;
        float closestHitDist = maxRange;
        Vector2 hitPos = _laserConvergencePoint + aimDir * maxRange;
        Asteroid? hitAsteroid = null;

        var asteroids = Asteroid.ActiveAsteroids;
        for (int i = 0; i < asteroids.Count; i++)
        {
            var ast = asteroids[i];
            if (!GodotObject.IsInstanceValid(ast)) continue;

            Vector2 toAst = ast.GlobalPosition - _laserConvergencePoint;
            float projection = toAst.Dot(aimDir);
            if (projection > 0.0f && projection < closestHitDist + ast.Radius)
            {
                Vector2 closestPt = _laserConvergencePoint + aimDir * projection;
                float distSq = (ast.GlobalPosition - closestPt).LengthSquared();
                float rSq = ast.Radius * ast.Radius;
                if (distSq <= rSq)
                {
                    float halfChord = MathF.Sqrt(rSq - distSq);
                    float entryDist = projection - halfChord;
                    if (entryDist > 0.0f && entryDist < closestHitDist)
                    {
                        closestHitDist = entryDist;
                        hitPos = _laserConvergencePoint + aimDir * entryDist;
                        hitAsteroid = ast;
                    }
                }
            }
        }

        _laserImpactPoint = hitPos;

        if (hitAsteroid != null)
        {
            // Damage kontinu terkonsentrasi sebanding jumlah mining swarm!
            float miningDPS = _activeMinerPositions.Count * 280.0f;
            hitAsteroid.ApplyLaserMiningDamage(miningDPS * dt, hitPos, aimDir);

            // Screen shake mikro saat mengebor batu
            GetParent()?.Call("trigger_shake", 0.45f + _activeMinerPositions.Count * 0.035f);
        }

        QueueRedraw();
    }

    public override void _Draw()
    {
        if (!_isMiningLaserActive || _activeMinerPositions.Count == 0) return;

        // 1. Sinar laser individual dari masing-masing Mining Droid (kutub U) menuju titik konvergensi
        Color feederColor = new Color(0.2f, 2.6f, 3.8f, 0.75f);
        for (int i = 0; i < _activeMinerPositions.Count; i++)
        {
            DrawLine(_activeMinerPositions[i], _laserConvergencePoint, feederColor, 1.8f);
        }

        // 2. Node Titik Konvergensi: Bola fusi plasma berdenyut
        float orbRadius = 4.2f + _activeMinerPositions.Count * 0.45f + MathF.Sin(_laserPulseTimer * 24.0f) * 1.5f;
        DrawCircle(_laserConvergencePoint, orbRadius * 1.7f, new Color(0.1f, 1.5f, 3.2f, 0.35f));
        DrawCircle(_laserConvergencePoint, orbRadius, new Color(3.6f, 3.2f, 1.2f, 1.0f));

        // 3. LASER BESAR TERSATUKAN (MEGA CONCENTRATED MINING BEAM)
        // Ketebalan laser bertambah seiring banyaknya jumlah miner!
        float beamWidth = Mathf.Clamp(7.0f + _activeMinerPositions.Count * 1.85f, 8.0f, 65.0f);

        // Lapisan 1: Aura Plasma Luar
        DrawLine(_laserConvergencePoint, _laserImpactPoint, new Color(0.15f, 1.8f, 3.5f, 0.42f), beamWidth * 1.65f);
        // Lapisan 2: Sorotan Sinar Utama Oranye-Emas Khas Mining
        DrawLine(_laserConvergencePoint, _laserImpactPoint, new Color(3.4f, 2.2f, 0.5f, 0.95f), beamWidth);
        // Lapisan 3: Inti Putih Super Panas Membara
        DrawLine(_laserConvergencePoint, _laserImpactPoint, new Color(4.8f, 4.8f, 4.2f, 1.0f), beamWidth * 0.35f);

        // 4. Efek Percikan & Flare Kontak pada Permukaan Asteroid
        float flareSize = beamWidth * 1.15f + MathF.Sin(_laserPulseTimer * 30.0f) * 3.0f;
        DrawCircle(_laserImpactPoint, flareSize, new Color(4.5f, 3.6f, 1.5f, 0.9f));
        DrawCircle(_laserImpactPoint, flareSize * 0.5f, Colors.White);

        // Percikan partikel mikro di titik kontak
        for (int s = 0; s < 4; s++)
        {
            float spAngle = GD.Randf() * Mathf.Tau;
            float spDist = (float)GD.RandRange(beamWidth * 0.4f, beamWidth * 1.6f);
            Vector2 spEnd = _laserImpactPoint + new Vector2(MathF.Cos(spAngle), MathF.Sin(spAngle)) * spDist;
            DrawLine(_laserImpactPoint, spEnd, new Color(4.2f, 2.8f, 0.8f, 0.8f), 1.6f);
        }
    }

    /// <summary>
    /// Menembakkan drone secara beruntun (Stream Fire)
    /// </summary>
    public void FireSwarmStream(Vector2 targetPos, int count = 2)
    {
        int fired = 0;
        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm) =>
        {
            if (fired >= count) return;
            if (swarm.State != SwarmState.Orbiting || swarm.OwnerId != 0) return;
            // Guardian, Drill, Miner, dan Scanner tidak ditembakkan via Stream biasa
            if (swarm.Role == SwarmRole.Guardian 
                || swarm.Role == SwarmRole.DrillDrone 
                || swarm.Role == SwarmRole.MiningDroid 
                || swarm.Role == SwarmRole.ScannerDrone) return;

            Vector2 toTarget = targetPos - pos.Value;
            if (toTarget.LengthSquared() < 1.0f) return;

            float spreadAngle = (GD.Randf() - 0.5f) * 0.12f;
            Vector2 dir = toTarget.Normalized().Rotated(spreadAngle);

            swarm.State = SwarmState.Launched;
            swarm.TargetDirection = dir;
            swarm.LifeTime = 0.0f;
            swarm.MaxLifeTime = (swarm.Role == SwarmRole.CrescentBoomerang) ? 1.6f : 1.15f;

            vel.Value = dir * (850.0f + GD.Randf() * 200.0f);
            fired++;
        });
    }

    /// <summary>
    /// Menembakkan semburan salvo menyebar (Shotgun Volley)
    /// </summary>
    public void FireSwarmVolley(Vector2 targetPos, int count = 25)
    {
        int fired = 0;
        Vector2 toTarget = targetPos - _playerCore.GlobalPosition;
        if (toTarget.LengthSquared() < 1.0f) toTarget = Vector2.Right;
        Vector2 baseDir = toTarget.Normalized();

        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm) =>
        {
            if (fired >= count) return;
            if (swarm.State != SwarmState.Orbiting || swarm.OwnerId != 0) return;
            if (swarm.Role == SwarmRole.Guardian) return;

            float spreadAngle = (GD.Randf() - 0.5f) * 0.55f;
            Vector2 dir = baseDir.Rotated(spreadAngle);

            swarm.State = SwarmState.Launched;
            swarm.TargetDirection = dir;
            swarm.LifeTime = 0.0f;
            swarm.MaxLifeTime = 0.95f + GD.Randf() * 0.4f;

            vel.Value = dir * (1000.0f + GD.Randf() * 300.0f);
            fired++;
        });
    }

    /// <summary>
    /// Tembakan Nova 360 Derajat
    /// </summary>
    public void FireRadialNova(int count = 75)
    {
        int fired = 0;
        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm) =>
        {
            if (fired >= count) return;
            if (swarm.State != SwarmState.Orbiting || swarm.OwnerId != 0) return;
            if (swarm.Role == SwarmRole.Guardian) return;

            float angle = ((float)fired / count) * Mathf.Tau;
            Vector2 dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));

            swarm.State = SwarmState.Launched;
            swarm.TargetDirection = dir;
            swarm.LifeTime = 0.0f;
            swarm.MaxLifeTime = 1.1f;

            vel.Value = dir * 1150.0f;
            fired++;
        });
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey keyEvent && keyEvent.Pressed)
        {
            switch (keyEvent.Keycode)
            {
                case Key.Key1:
                    AddSpecificSwarm(SwarmRole.DartNeedle, 100);
                    break;
                case Key.Key2:
                    _playerCore.AddNextHoneycombCell();
                    break;
                case Key.Key3:
                    AddSpecificSwarm(SwarmRole.GigaShuriken, 50);
                    break;
                case Key.Key4:
                    AddSpecificSwarm(SwarmRole.DrillDrone, 50);
                    break;
                case Key.Key5:
                    AddSpecificSwarm(SwarmRole.CrescentBoomerang, 50);
                    break;
                case Key.Key6:
                    AddSpecificSwarm(SwarmRole.Guardian, 50);
                    break;
                case Key.Key8:
                    AddSpecificSwarm(SwarmRole.ScannerDrone, 5);
                    break;
                case Key.Key9:
                    AddSpecificSwarm(SwarmRole.MiningDroid, 10);
                    break;
                case Key.Key7:
                case Key.E:
                    SpawnEnemySectorAttack(GetGlobalMousePosition(), 50);
                    break;
                case Key.Space:
                    FireRadialNova(80);
                    break;
            }
        }
    }

    public override void _ExitTree()
    {
        _world?.Dispose();
    }
}
