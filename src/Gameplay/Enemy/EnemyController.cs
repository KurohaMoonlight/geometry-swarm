using System;
using System.Collections.Generic;
using Godot;
using GeometrySwarm.Core.ECS.Components;

namespace GeometrySwarm.Gameplay.Enemy;

public enum EnemyState
{
    SearchingAsteroid,
    TravelingToStandoff,
    MiningResource,
    CollectingResources,
    FleeingPod,
    Wandering
}

/// <summary>
/// AI Controller untuk Rival Hive Core (100% Fair-Play Player Clone):
/// - Tidak menggunakan fitur khusus atau cheat apa pun: seluruh mekanik gerak (WASD), laser penambangan
///   (lebar & sub-ray mengikuti rumus MinerCount / 15), magnet resource (260 px), dan resep Foundry identik 1:1 dengan Pemain.
/// - Strategi Pembuatan Terowongan (Tunneling) & Penambangan Efisien:
///   1. Mengunci satu poros lorong terowongan dalam koordinat lokal asteroid (_tunnelShaftLocalOrigin & _tunnelLocalOutwardDir)
///      dari sisi permukaan terdekat menuju kantung mineral, sehingga AI tidak pernah berputar-putar membuat
///      sayatan radial bintang (starburst) saat voxel mineral pecah.
///   2. Menghitung lebar koridor terowongan yang dibutuhkan sesuai ukuran + jumlah sel Core & swarm saat itu
///      (GetRequiredTunnelHalfWidth).
///   3. Mengarahkan bidikan laser tunggalnya (LaserAimDir) menyapu dinding kiri-tengah-kanan koridor terowongan
///      dan mengunci setiap blok sampai hancur penuh (zero heat decay waste) sebelum bergeser ke blok sebelahnya.
///   4. Memajukan kapal masuk ke dalam terowongan yang sudah bolong tepat di belakang dinding batu terdepan
///      serta menghampiri kepingan kristal resource (IsCrystalMatter) hingga seluruhnya tersedot magnet kapal.
/// </summary>
public partial class EnemyController : Node
{
    public EnemyCore Core { get; private set; } = null!;
    public SwarmSimulation Simulation { get; private set; } = null!;

    public EnemyState State { get; private set; } = EnemyState.SearchingAsteroid;
    public Asteroid? TargetAsteroid { get; private set; }
    public Vector2 TargetResourceWorldPos { get; private set; } = Vector2.Zero;
    public Vector2 StandoffPosition { get; private set; } = Vector2.Zero;

    // Early game: fokus penambangan dan ekspansi ekonomi
    public bool IsEarlyGamePassive { get; set; } = true;

    // Inventori 5 Material Kosmik (Ferrite, Quartz, Carbide, Vortexum, Aether) - Identik 1:1 dengan Pemain
    public int[] ResourceStock { get; } = new int[5] { 35, 15, 10, 0, 0 };

    // Penghitung Armada
    public int MinerCount { get; private set; } = 0;
    public int GuardianCount { get; private set; } = 0;
    public int DartCount { get; private set; } = 0;
    public int ShurikenCount { get; private set; } = 0;
    public int DrillCount { get; private set; } = 0;
    public int BoomerangCount { get; private set; } = 0;

    // Logika Laser Penambangan (Identik 1:1 dengan Pemain)
    public bool IsMiningLaserActive { get; private set; } = false;
    public Vector2 LaserAimDir { get; private set; } = Vector2.Right;
    public Vector2 LaserConvergencePoint { get; private set; } = Vector2.Zero;
    public Vector2 LaserImpactPoint { get; private set; } = Vector2.Zero;
    public float VisualBeamWidth { get; private set; } = 3.5f;
    public int LaserBlockSpan { get; private set; } = 1;

    private float _laserPrimingTimer = 0.0f;
    private const float LaserPrimingDuration = 0.28f;
    private float _laserFocusWarmup = 0.0f;
    private float _laserPulseTimer = 0.0f;
    private float _laserHitAsteroidRotation = 0.0f;
    private readonly List<Vector2> _activeMinerPositions = new();
    private readonly List<(Vector2 worldCenter, float heat, Asteroid.VoxelType type)> _laserActiveHitBoxes = new();

    // Status Penguncian Poros Terowongan di Koordinat Lokal Asteroid
    private bool _isTunnelLocked = false;
    private Vector2 _tunnelShaftLocalOrigin = Vector2.Zero;
    private Vector2 _tunnelPocketLocalCenter = Vector2.Zero;
    private Vector2 _tunnelLocalOutwardDir = Vector2.Right;
    private float _sweepDirection = 1.0f;
    private float _currentSweepLateral = 0.0f;

    // Penguncian Voxel Target Laser (Sticky Block Aiming agar panas laser tidak terbuang sia-sia)
    private bool _hasStickyAimVoxel = false;
    private int _stickyAimGx = -1;
    private int _stickyAimGy = -1;
    private Vector2 _currentLaserAimWorldPoint = Vector2.Zero;

    // Pelacakan Kristal Resource (IsCrystalMatter) untuk Pemungutan Aktif
    private readonly List<Vector2> _nearbyCrystals = new(64);
    private float _postDestructionHarvestTimer = 0.0f;

    // Pencegah Tersangkut (Anti-Stuck Watchdog & Active WASD Rebound)
    private float _stuckTimer = 0.0f;
    private float _stuckVelocityTimer = 0.0f;
    private float _unstuckActiveTimer = 0.0f;
    private Vector2 _unstuckDir = Vector2.Zero;

    // Timer Pabrik Otomatis (Mengikuti interval Auto-Craft pemain)
    private float _factoryTimer = 0.0f;
    private const float FactoryInterval = 1.0f;

    // Mode Gerak Random / Wandering Target (Bukan Mining & Tidak Terlalu Jauh)
    public bool IsWanderingMode { get; set; } = true;
    public bool IsStationaryMode { get; set; } = true;
    private Vector2 _wanderAnchorPos = Vector2.Zero;
    private Vector2 _currentWanderTarget = Vector2.Zero;
    private float _wanderTimer = 0.0f;
    private const float WanderRadius = 320.0f;

    public void Initialize(SwarmSimulation simulation, EnemyCore core)
    {
        Simulation = simulation;
        Core = core;
        _wanderAnchorPos = core.GlobalPosition;
        _currentWanderTarget = core.GlobalPosition;
    }

    public void AddResource(int resourceIndex, int amount)
    {
        if (resourceIndex >= 0 && resourceIndex < ResourceStock.Length)
        {
            ResourceStock[resourceIndex] += amount;
        }
    }

    public void SetUnitCounts(int miners, int guardians, int darts, int shurikens, int drills, int boomerangs)
    {
        MinerCount = miners;
        GuardianCount = guardians;
        DartCount = darts;
        ShurikenCount = shurikens;
        DrillCount = drills;
        BoomerangCount = boomerangs;
    }

    /// <summary>
    /// Menghitung separuh lebar koridor terowongan (tunnelHalfWidth) yang harus dibuka oleh laser
    /// berdasarkan ukuran fisik Core (CoreCollisionRadius), jumlah sel Core (CoreCellCount), dan jumlah swarm saat itu.
    /// </summary>
    private float GetRequiredTunnelHalfWidth()
    {
        float coreRad = Core != null ? Core.CoreCollisionRadius : 15.0f;
        int cellCount = Core != null ? Math.Max(1, Core.CoreCellCount) : 1;
        int totalUnits = MinerCount + GuardianCount + DartCount + ShurikenCount + DrillCount + BoomerangCount;

        float cellAllowance = MathF.Min(22.0f, (cellCount - 1) * 4.5f);
        float swarmAllowance = MathF.Min(10.0f, totalUnits * 0.08f);

        // Minimal ~31 px (lebar total ~62 px / ~5 blok voxel) untuk 1 Core agar aman dari sudut miring voxel 13 px
        return coreRad + 16.0f + cellAllowance + swarmAllowance;
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        if (Core == null || !IsInstanceValid(Core)) return;

        // Jika dalam mode Nemesis Escape Pod, serahkan kendali ke manuver kabur darurat bawaan Core
        if (Core.IsEscapePod)
        {
            State = EnemyState.FleeingPod;
            DeactivateMiningLaser(dt);
            return;
        }

        // Mode Uji Coba Diam (Stationary Mode): Musuh tetap diam di tempat tanpa bergerak/menambang
        if (IsStationaryMode)
        {
            State = EnemyState.SearchingAsteroid;
            Core.Velocity = Vector2.Zero;
            DeactivateMiningLaser(dt);
            return;
        }

        _factoryTimer += dt;
        if (_postDestructionHarvestTimer > 0.0f)
        {
            _postDestructionHarvestTimer -= dt;
        }

        // Jika dalam mode Wandering, jalankan manuver gerak acak dan lewati penambangan / pabrik
        if (IsWanderingMode)
        {
            UpdateBehavior(dt);
            return;
        }

        // Siklus Perilaku Penambangan Terowongan & Pemungutan Resource
        UpdateBehavior(dt);

        // Produksi Pabrik Otomatis (100% menggunakan resep resmi Foundry)
        if (_factoryTimer >= FactoryInterval)
        {
            _factoryTimer = 0.0f;
            ProcessAutonomousFoundry();
        }
    }

    private void UpdateBehavior(float dt)
    {
        Vector2 corePos = Core.GlobalPosition;

        // Mode Wandering: Lawan bergerak random (bukan mining) di area terbatas dekat titik spawn
        if (IsWanderingMode)
        {
            State = EnemyState.Wandering;
            DeactivateMiningLaser(dt);

            // Geser jangkar secara halus jika pemain bergerak sangat jauh agar tetap dalam jangkauan uji coba
            if (PlayerCore.Instance != null && IsInstanceValid(PlayerCore.Instance))
            {
                float distToPlayer = corePos.DistanceTo(PlayerCore.Instance.GlobalPosition);
                if (distToPlayer > 850.0f)
                {
                    _wanderAnchorPos = _wanderAnchorPos.Lerp(PlayerCore.Instance.GlobalPosition, dt * 0.45f);
                }
            }

            _wanderTimer -= dt;
            if (_wanderTimer <= 0.0f || corePos.DistanceTo(_currentWanderTarget) < 45.0f)
            {
                _wanderTimer = (float)GD.RandRange(2.0f, 3.8f);
                float ang = (float)GD.RandRange(0.0f, Mathf.Tau);
                float dist = (float)GD.RandRange(60.0f, WanderRadius);
                _currentWanderTarget = _wanderAnchorPos + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * dist;
            }

            // Kemudikan musuh secara mulus menuju target wander acak dengan penghindaran rintangan
            SteerShipSmoothlyToward(_currentWanderTarget, dt, maxApproachSpeed: 210.0f, arrivalRadius: 40.0f);

            // Rotasikan badan Core mengikuti arah gerak
            if (Core.Velocity.LengthSquared() > 40.0f)
            {
                float targetRot = Core.Velocity.Angle();
                Core.Rotation = Mathf.LerpAngle(Core.Rotation, targetRot, MathF.Min(1.0f, 5.0f * dt));
            }
            return;
        }

        // Manuver Unstuck Aktif (Simulasi WASD mundur dan tembak laser perata titik jepit)
        if (_unstuckActiveTimer > 0.0f)
        {
            _unstuckActiveTimer -= dt;
            Vector2 reverseVel = _unstuckDir * 230.0f;
            if (TargetAsteroid != null && TargetAsteroid.IsActiveInTree)
            {
                reverseVel += TargetAsteroid.CachedLinearVelocity;
            }
            Core.Velocity = Core.Velocity.Lerp(reverseVel, MathF.Min(1.0f, 9.0f * dt));

            if (TargetAsteroid != null && TargetAsteroid.IsActiveInTree)
            {
                CheckAndDrillObstacleAhead(corePos, -_unstuckDir, dt, Core.CoreCollisionRadius + 32.0f);
            }
            return;
        }

        // 0. Jika baru saja menghancurkan kantung/asteroid atau ada kristal lepas di dekat kapal di ruang terbuka,
        //    prioritaskan memungut kristal resource yang jatuh sebelum pergi jauh.
        if ((TargetAsteroid == null || !TargetAsteroid.IsActiveInTree || TargetAsteroid.IsDestroying || TargetAsteroid.RemainingResourceVoxelCount <= 0)
            && TryFindReachableFreeCrystal(corePos, 620.0f, out Vector2 freeCrystalPos))
        {
            State = EnemyState.CollectingResources;
            SteerShipSmoothlyToward(freeCrystalPos, dt, maxApproachSpeed: 380.0f, arrivalRadius: 16.0f);
            if (!CheckAndDrillObstacleAhead(corePos, freeCrystalPos - corePos, dt))
            {
                DeactivateMiningLaser(dt);
            }
            return;
        }

        // 1. Validasi Asteroid Target (Lepaskan target jika sedang terpental kencang akibat Shockwave Black Hole)
        if (TargetAsteroid == null
            || !IsInstanceValid(TargetAsteroid)
            || !TargetAsteroid.IsActiveInTree
            || TargetAsteroid.CachedLinearVelocity.LengthSquared() > 180.0f * 180.0f)
        {
            TargetAsteroid = null;
            _isTunnelLocked = false;
            _hasStickyAimVoxel = false;
            FindBestAsteroidWithResources();
        }

        if (TargetAsteroid == null)
        {
            State = EnemyState.SearchingAsteroid;
            DeactivateMiningLaser(dt);
            return;
        }

        // Jika asteroid target sedang dalam animasi runtuh/meledak (0.76s setelah mineral habis),
        // mundur sedikit ke luar batas aman sambil menyedot seluruh kristal yang terlontar!
        if (TargetAsteroid.IsDestroying || TargetAsteroid.RemainingResourceVoxelCount <= 0)
        {
            _postDestructionHarvestTimer = 2.2f;

            if (TryFindReachableFreeCrystal(corePos, 560.0f, out Vector2 dropPos))
            {
                State = EnemyState.CollectingResources;
                SteerShipSmoothlyToward(dropPos, dt, maxApproachSpeed: 360.0f, arrivalRadius: 16.0f);
                if (!CheckAndDrillObstacleAhead(corePos, dropPos - corePos, dt))
                {
                    DeactivateMiningLaser(dt);
                }
            }
            else
            {
                DeactivateMiningLaser(dt);
                Vector2 awayDir = (corePos - TargetAsteroid.CachedPosition);
                awayDir = awayDir.LengthSquared() > 1.0f ? awayDir.Normalized() : Vector2.Right;
                Vector2 waitOutsidePos = TargetAsteroid.CachedPosition + awayDir * (TargetAsteroid.OuterBoundRadius + Core.CoreCollisionRadius + 55.0f);
                State = EnemyState.CollectingResources;
                SteerShipSmoothlyToward(waitOutsidePos, dt, maxApproachSpeed: 280.0f, arrivalRadius: 35.0f);

                if (!TargetAsteroid.IsDestroying && TargetAsteroid.RemainingResourceVoxelCount <= 0)
                {
                    TargetAsteroid = null;
                    _isTunnelLocked = false;
                }
            }
            return;
        }

        // 2. Penanganan Khusus Bintang Aether (IsEnergyAsteroid):
        //    Bintang Aether adalah bola plasma fusi yang dipanaskan dari jarak aman luar cakrawala peristiwa.
        if (TargetAsteroid.IsEnergyAsteroid)
        {
            UpdateEnergyStarMiningBehavior(dt);
            return;
        }

        // 3. Pastikan kita memiliki Poros Terowongan yang terkunci pada kantung mineral aktif
        if (!_isTunnelLocked || !ValidateAndRefreshLockedPocket())
        {
            if (!LockTunnelToBestPocket())
            {
                TargetAsteroid = null;
                DeactivateMiningLaser(dt);
                return;
            }
        }

        // 4. Hitung Geometri Terowongan di Koordinat Dunia (otomatis ikut berputar mulus bersama rotasi asteroid):
        //    Garis tengah terowongan (shaft centerline) SELALU melewati _tunnelShaftLocalOrigin!
        float tunnelHalfWidth = GetRequiredTunnelHalfWidth();
        Vector2 astPos = TargetAsteroid.CachedPosition;
        Vector2 shaftOriginWorld = TargetAsteroid.FastLocalToWorld(_tunnelShaftLocalOrigin);
        Vector2 outwardWorld = TargetAsteroid.FastRotateToWorld(_tunnelLocalOutwardDir).Normalized();
        Vector2 inwardWorld = -outwardWorld;
        Vector2 perpWorld = new Vector2(-inwardWorld.Y, inwardWorld.X);

        float mouthRefRadius = TargetAsteroid.OuterBoundRadius + 52.0f;
        float shaftProjOnOutward = (shaftOriginWorld - astPos).Dot(outwardWorld);
        Vector2 mouthRefWorld = shaftOriginWorld + outwardWorld * (mouthRefRadius - shaftProjOnOutward);

        ScanTunnelCorridorAndPickLaserTarget(
            mouthRefWorld,
            inwardWorld,
            perpWorld,
            tunnelHalfWidth,
            out float minCorridorBlockedDist,
            out float coreClearanceDist,
            out Vector2 laserAimWorldPoint
        );

        _currentLaserAimWorldPoint = laserAimWorldPoint;

        // 5. Hitung Posisi Target Kapal di Dalam Terowongan:
        //    Gunakan coreClearanceDist (jarak bebas fisik untuk bodi Core) agar kapal maju mulus
        //    tanpa terhenti oleh sudut batu miring di dinding samping luar mulut terowongan.
        float safeClearanceFromRockFace = Core.CoreCollisionRadius + 28.0f;
        float maxSafeAdvanceDepth = MathF.Max(0.0f, coreClearanceDist - safeClearanceFromRockFace);
        float safeLateralPlay = MathF.Max(0.0f, tunnelHalfWidth - Core.CoreCollisionRadius - 8.0f);
        float desiredLateralBias = Mathf.Clamp(_currentSweepLateral * 0.28f, -safeLateralPlay, safeLateralPlay);

        Vector2 tunnelAdvanceWorldPos = mouthRefWorld + inwardWorld * maxSafeAdvanceDepth + perpWorld * desiredLateralBias;

        // Cek apakah ada kristal resource (IsCrystalMatter) di dalam lorong terowongan yang sudah terbuka
        // atau di dekat mulut terowongan yang perlu dihampiri agar tersedot magnet (260 px)
        if (TryFindCrystalInsideClearedTunnel(
            corePos,
            mouthRefWorld,
            inwardWorld,
            perpWorld,
            tunnelHalfWidth,
            coreClearanceDist,
            out Vector2 tunnelCrystalPos))
        {
            tunnelAdvanceWorldPos = tunnelCrystalPos;
        }

        StandoffPosition = tunnelAdvanceWorldPos;

        // 6. Navigasi & Kemudi Kapal (Simulasi WASD Pemain):
        Vector2 relFromMouth = corePos - mouthRefWorld;
        float shipDepthFromMouth = relFromMouth.Dot(inwardWorld);
        float shipLateralOffset = relFromMouth.Dot(perpWorld);
        float absLateral = MathF.Abs(shipLateralOffset);
        float distToAstCenter = corePos.DistanceTo(astPos);
        float safePerimeterRadius = TargetAsteroid.OuterBoundRadius + Core.CoreCollisionRadius + 46.0f;

        bool isAlignedWithTunnelCorridor = absLateral <= tunnelHalfWidth * 0.95f
                                           && shipDepthFromMouth >= -110.0f
                                           && shipDepthFromMouth <= coreClearanceDist + 22.0f;

        Vector2 resLocal = TargetAsteroid.FastWorldToLocal(TargetResourceWorldPos);
        Vector2 localInward = -_tunnelLocalOutwardDir;
        Vector2 localPerp = new Vector2(-localInward.Y, localInward.X);
        float resLateralFromShaft = MathF.Abs((resLocal - _tunnelShaftLocalOrigin).Dot(localPerp));
        bool isShipInsideCavern = distToAstCenter < TargetAsteroid.OuterBoundRadius + 15.0f && shipDepthFromMouth > 30.0f;
        bool isTargetInCavernBranch = isShipInsideCavern && (resLateralFromShaft > tunnelHalfWidth * 0.85f);

        if (isTargetInCavernBranch)
        {
            // KASUS KHUSUS: Ekskavasi Rongga Dalam / Percabangan Gua (Chamber Branching)
            // Kapal sudah berada di dalam perut asteroid dan urat mineral berikutnya berada di samping/dasar gua.
            // Arahkan laser langsung ke sekat batu yang memisahkan kapal dengan mineral (Drill-through Branching)!
            State = EnemyState.MiningResource;

            Vector2 toRes = TargetResourceWorldPos - corePos;
            float distToRes = toRes.Length();
            Vector2 dirToRes = distToRes > 1.0f ? toRes / distToRes : Vector2.Right;

            Vector2 laserAimPoint = TargetResourceWorldPos;
            Vector2 chamberAdvancePos = TargetResourceWorldPos;

            if (TargetAsteroid.RaycastVoxel(corePos, dirToRes, distToRes, out Asteroid.VoxelRayHit branchHit) && branchHit.Hit)
            {
                // Masih ada sekat dinding batu: kunci laser ke titik batu terdepan
                laserAimPoint = branchHit.WorldCellCenter;
                _stickyAimGx = branchHit.GridX;
                _stickyAimGy = branchHit.GridY;
                _hasStickyAimVoxel = true;

                float maxAdvance = MathF.Max(0.0f, branchHit.Distance - (Core.CoreCollisionRadius + 26.0f));
                chamberAdvancePos = corePos + dirToRes * maxAdvance;
            }
            else
            {
                // Dinding sekat sudah bolong: bidik langsung mineral sasaran!
                laserAimPoint = TargetResourceWorldPos;
                float maxAdvance = MathF.Max(0.0f, distToRes - (Core.CoreCollisionRadius + 18.0f));
                chamberAdvancePos = corePos + dirToRes * maxAdvance;
            }

            StandoffPosition = chamberAdvancePos;

            Vector2 toAdvance = chamberAdvancePos - corePos;
            Vector2 desiredVel = toAdvance.LimitLength(210.0f) + TargetAsteroid.CachedLinearVelocity;
            Core.Velocity = Core.Velocity.Lerp(desiredVel, MathF.Min(1.0f, 6.5f * dt));

            UpdateMiningLaser(dt, laserAimPoint);
        }
        else if (isAlignedWithTunnelCorridor)
        {
            // KASUS A: Kapal sudah sejajar di mulut atau di dalam koridor terowongan!
            // Maju masuk ke dalam terowongan menuju tunnelAdvanceWorldPos sambil menjaga posisi di koridor lorong.
            State = EnemyState.MiningResource;

            Vector2 toAdvancePos = tunnelAdvanceWorldPos - corePos;
            float lateralError = shipLateralOffset - desiredLateralBias;
            Vector2 centeringCorrection = -perpWorld * (lateralError * 4.5f);
            Vector2 desiredVel = toAdvancePos * 3.4f + centeringCorrection + TargetAsteroid.CachedLinearVelocity;

            float maxTunnelSpeed = toAdvancePos.Length() > 45.0f ? 240.0f : 115.0f;
            desiredVel = desiredVel.LimitLength(maxTunnelSpeed);

            Core.Velocity = Core.Velocity.Lerp(desiredVel, MathF.Min(1.0f, 7.5f * dt));

            // Nyalakan laser penambangan terus-menerus selama menggali & maju di dalam lorong
            UpdateMiningLaser(dt, _currentLaserAimWorldPoint);
        }
        else if (distToAstCenter < safePerimeterRadius - 18.0f && shipDepthFromMouth > 15.0f)
        {
            // KASUS B: Kapal berada di dalam terowongan dan sedang mundur keluar menuju mulut terowongan.
            // Gunakan vektor poros terowongan (outwardWorld) agar mundur lurus di lorong yang sudah bolong, BUKAN radial!
            State = EnemyState.TravelingToStandoff;

            Vector2 exitTarget = mouthRefWorld + outwardWorld * 45.0f;

            // Jaga posisi tetap di garis tengah terowongan saat mundur
            Vector2 centeringCorrection = -perpWorld * (shipLateralOffset * 4.5f);
            Vector2 desiredExitVel = outwardWorld * 240.0f + centeringCorrection + TargetAsteroid.CachedLinearVelocity;
            desiredExitVel = desiredExitVel.LimitLength(270.0f);

            Core.Velocity = Core.Velocity.Lerp(desiredExitVel, MathF.Min(1.0f, 7.5f * dt));

            // Jika ada voxel batuan yang menghalangi jalur keluar searah outwardWorld, tembak dengan laser
            if (TargetAsteroid.RaycastVoxel(corePos, outwardWorld, Core.CoreCollisionRadius + 45.0f, out Asteroid.VoxelRayHit exitHit) && exitHit.Hit)
            {
                UpdateMiningLaser(dt, exitHit.WorldCellCenter);
            }
            else
            {
                DeactivateMiningLaser(dt);
            }
        }
        else
        {
            // KASUS C: Kapal berada di luar asteroid dan sedang terbang menuju mulut terowongan (mouthRefWorld).
            State = EnemyState.TravelingToStandoff;

            Vector2 mouthEntryTarget = mouthRefWorld;
            Vector2 toMouth = mouthEntryTarget - corePos;
            float distToMouth = toMouth.Length();

            // Navigasi Multi-Asteroid menuju mulut terowongan:
            // SteerShipSmoothlyToward secara otomatis membelokkan haluan mengitari asteroid target maupun asteroid tetangga
            SteerShipSmoothlyToward(mouthEntryTarget, dt, maxApproachSpeed: 390.0f, arrivalRadius: 32.0f);

            // Jika kapal sudah cukup dekat dengan mulut terowongan, langsung nyalakan laser untuk mulai mengikis mulut lorong!
            if (distToMouth <= 135.0f && absLateral <= tunnelHalfWidth * 1.85f)
            {
                UpdateMiningLaser(dt, _currentLaserAimWorldPoint);
            }
            else
            {
                // Drill-to-Clear: Jika ada tonjolan batu yang menghalangi penerbangan ke mulut lorong, tembak dengan laser
                if (!CheckAndDrillObstacleAhead(corePos, toMouth, dt))
                {
                    DeactivateMiningLaser(dt);
                }
            }
        }

        // Deteksi Kemacetan Fisik Aktif (Active Stuck Detection):
        // Jika kapal bermaksud bergerak tapi kecepatannya tertahan < 18 px/s selama > 0.70s, aktifkan manuver mundur
        float currentSpeed = Core.Velocity.Length();
        bool isTryingToMove = State == EnemyState.MiningResource
                              || State == EnemyState.TravelingToStandoff
                              || State == EnemyState.CollectingResources;

        if (isTryingToMove && currentSpeed < 18.0f)
        {
            _stuckVelocityTimer += dt;
            if (_stuckVelocityTimer >= 0.70f)
            {
                _stuckVelocityTimer = 0.0f;
                _unstuckActiveTimer = 0.35f;

                Vector2 forwardDir = Core.Velocity.LengthSquared() > 4.0f
                    ? Core.Velocity.Normalized()
                    : (TargetAsteroid != null ? (TargetAsteroid.CachedPosition - corePos).Normalized() : Vector2.Right);

                float wiggleAngle = (GD.Randf() - 0.5f) * Mathf.Pi * 0.40f;
                _unstuckDir = (-forwardDir).Rotated(wiggleAngle).Normalized();
            }
        }
        else
        {
            _stuckVelocityTimer = MathF.Max(0.0f, _stuckVelocityTimer - dt * 2.5f);
        }

        // Watchdog Anti-Stuck: jika selama 5.0 detik tidak ada satu pun voxel yang berhasil dihancurkan, evaluasi ulang kantung
        _stuckTimer += dt;
        if (_stuckTimer >= 5.0f)
        {
            _stuckTimer = 0.0f;
            _hasStickyAimVoxel = false;
            if (!ValidateAndRefreshLockedPocket())
            {
                _isTunnelLocked = false;
                LockTunnelToBestPocket();
            }
        }
    }

    /// <summary>
    /// Memindai koridor terowongan selebar [-tunnelHalfWidth, +tunnelHalfWidth] dari luar ke dalam:
    /// 1. Menemukan jarak batu terdepan (minCorridorBlockedDist) yang membatasi seberapa dalam kapal bisa masuk.
    /// 2. Memilih voxel sasaran laser tunggal secara konsisten (Sticky Voxel Aiming) pada permukaan terdepan
    ///    agar seluruh lebar terowongan terkikis rata tanpa membuang panas laser.
    /// </summary>
    private void ScanTunnelCorridorAndPickLaserTarget(
        Vector2 mouthRefWorld,
        Vector2 inwardWorld,
        Vector2 perpWorld,
        float tunnelHalfWidth,
        out float minCorridorBlockedDist,
        out float coreClearanceDist,
        out Vector2 laserAimWorldPoint)
    {
        laserAimWorldPoint = TargetResourceWorldPos;
        minCorridorBlockedDist = 0.0f;
        coreClearanceDist = 0.0f;

        if (TargetAsteroid == null) return;

        float resourceDepth = MathF.Max(20.0f, (TargetResourceWorldPos - mouthRefWorld).Dot(inwardWorld));
        float maxScanDist = resourceDepth + Asteroid.VoxelSize * 2.0f;
        minCorridorBlockedDist = resourceDepth;
        coreClearanceDist = resourceDepth;

        // Lebar bodi fisik Core yang benar-benar membutuhkan celah bebas untuk melaju
        float coreHullHalfWidth = (Core != null ? Core.CoreCollisionRadius : 16.0f) + 6.0f;

        // Spasi antar lajur pindai (8.0 px < ukuran voxel 13.0 px) agar tidak ada satu pun gigi batu yang terlewat di tengah lorong
        const float laneStep = 8.0f;
        int halfLanes = Math.Max(1, (int)MathF.Ceiling(tunnelHalfWidth / laneStep));

        for (int l = -halfLanes; l <= halfLanes; l++)
        {
            float lat = Mathf.Clamp(l * laneStep, -tunnelHalfWidth, tunnelHalfWidth);
            Vector2 rayOrigin = mouthRefWorld + perpWorld * lat;

            if (TargetAsteroid.RaycastVoxel(rayOrigin, inwardWorld, maxScanDist, out Asteroid.VoxelRayHit hit) && hit.Hit)
            {
                if (hit.Distance < minCorridorBlockedDist)
                {
                    minCorridorBlockedDist = hit.Distance;
                }

                // Lajur inti fisik kapal: hanya rintangan di lajur ini yang membatasi maju kapal
                if (MathF.Abs(lat) <= coreHullHalfWidth)
                {
                    if (hit.Distance < coreClearanceDist)
                    {
                        coreClearanceDist = hit.Distance;
                    }
                }
            }
        }

        bool isCorridorOpenToVein = coreClearanceDist >= resourceDepth - Asteroid.VoxelSize * 0.95f;

        // 1. Cek apakah voxel yang sedang dipanaskan frame sebelumnya (Sticky Voxel) masih padat
        //    dan masih berada di lapisan terdepan koridor terowongan (atau di ruang urat mineral). Jika ya, tuntaskan sampai pecah!
        if (_hasStickyAimVoxel && TargetAsteroid.IsValidGridCoord(_stickyAimGx, _stickyAimGy))
        {
            Asteroid.VoxelType stickyType = TargetAsteroid.GetVoxelAt(_stickyAimGx, _stickyAimGy);
            if (stickyType != Asteroid.VoxelType.Empty)
            {
                Vector2 stickyWorld = TargetAsteroid.FastLocalToWorld(TargetAsteroid.GridToLocalCenter(_stickyAimGx, _stickyAimGy));
                Vector2 relSticky = stickyWorld - mouthRefWorld;
                float stickyDepth = relSticky.Dot(inwardWorld);
                float stickyLat = MathF.Abs(relSticky.Dot(perpWorld));

                bool validInCorridor = stickyDepth <= minCorridorBlockedDist + Asteroid.VoxelSize * 1.85f
                                       && stickyLat <= tunnelHalfWidth + Asteroid.VoxelSize * 1.1f;
                bool validInVeinChamber = isCorridorOpenToVein
                                          && stickyLat <= tunnelHalfWidth + 60.0f
                                          && stickyDepth <= resourceDepth + Asteroid.VoxelSize * 2.5f;

                if (validInCorridor || validInVeinChamber)
                {
                    laserAimWorldPoint = stickyWorld;
                    return;
                }
            }
            _hasStickyAimVoxel = false;
        }

        // 2. Jika lorong di depan urat mineral sudah bersih terbuka selebar tunnelHalfWidth,
        //    bidik langsung voxel mineral sasaran!
        if (minCorridorBlockedDist >= resourceDepth - Asteroid.VoxelSize * 0.95f)
        {
            Vector2I resGrid = TargetAsteroid.LocalToGrid(TargetAsteroid.FastWorldToLocal(TargetResourceWorldPos));
            if (TargetAsteroid.IsValidGridCoord(resGrid.X, resGrid.Y) && TargetAsteroid.GetVoxelAt(resGrid.X, resGrid.Y) != Asteroid.VoxelType.Empty)
            {
                _stickyAimGx = resGrid.X;
                _stickyAimGy = resGrid.Y;
                _hasStickyAimVoxel = true;
            }
            laserAimWorldPoint = TargetResourceWorldPos;
            return;
        }

        // 3. Pilih voxel pada lapisan terdepan koridor (front face: hit.Distance <= minCorridorBlockedDist + 1.25 * VoxelSize)
        //    dengan menyapu secara berurutan dari satu sisi ke sisi lain (seperti gerakan mouse pemain).
        float frontLayerThreshold = minCorridorBlockedDist + Asteroid.VoxelSize * 1.25f;
        bool anyCorridorVoxelHit = false;
        Asteroid.VoxelRayHit bestCandidateHit = default;
        float bestCandidateScore = float.MaxValue;
        float chosenCandidateLateral = 0.0f;

        for (int l = -halfLanes; l <= halfLanes; l++)
        {
            float lat = Mathf.Clamp(l * laneStep, -tunnelHalfWidth, tunnelHalfWidth);
            Vector2 rayOrigin = mouthRefWorld + perpWorld * lat;

            if (TargetAsteroid.RaycastVoxel(rayOrigin, inwardWorld, maxScanDist, out Asteroid.VoxelRayHit hit) && hit.Hit)
            {
                if (hit.Distance > frontLayerThreshold) continue;

                float depthPenalty = (hit.Distance - minCorridorBlockedDist) * 3.5f;
                float heatBonus = -hit.CurrentHeat * 28.0f;
                float sweepDelta = (lat - _currentSweepLateral) * _sweepDirection;
                float sweepPenalty = sweepDelta >= -4.0f
                    ? MathF.Abs(lat - _currentSweepLateral) * 0.45f
                    : (tunnelHalfWidth * 2.0f + MathF.Abs(lat - _currentSweepLateral)) * 0.45f;

                float score = depthPenalty + heatBonus + sweepPenalty;
                if (!anyCorridorVoxelHit || score < bestCandidateScore)
                {
                    anyCorridorVoxelHit = true;
                    bestCandidateScore = score;
                    bestCandidateHit = hit;
                    chosenCandidateLateral = lat;
                }
            }
        }

        if (anyCorridorVoxelHit)
        {
            _stickyAimGx = bestCandidateHit.GridX;
            _stickyAimGy = bestCandidateHit.GridY;
            _hasStickyAimVoxel = true;
            _currentSweepLateral = chosenCandidateLateral;

            // Balik arah sapuan jika sudah menyentuh tepi kiri/kanan dinding terowongan
            if (chosenCandidateLateral >= tunnelHalfWidth - laneStep * 1.1f)
            {
                _sweepDirection = -1.0f;
            }
            else if (chosenCandidateLateral <= -tunnelHalfWidth + laneStep * 1.1f)
            {
                _sweepDirection = 1.0f;
            }

            laserAimWorldPoint = bestCandidateHit.WorldCellCenter;
        }
        else
        {
            laserAimWorldPoint = TargetResourceWorldPos;
        }
    }

    /// <summary>
    /// Memilih kantung mineral terbaik di dalam TargetAsteroid dan mengunci poros terowongan
    /// dalam koordinat lokal asteroid (_tunnelShaftLocalOrigin & _tunnelLocalOutwardDir) yang paling tipis kerak batunya.
    /// </summary>
    private bool LockTunnelToBestPocket()
    {
        if (TargetAsteroid == null || !TargetAsteroid.IsActiveInTree || TargetAsteroid.RemainingResourceVoxelCount <= 0)
        {
            _isTunnelLocked = false;
            return false;
        }

        Vector2 corePos = Core.GlobalPosition;
        if (!TargetAsteroid.FindClosestResourceVoxel(corePos, out Vector2 seedResWorld, out _))
        {
            _isTunnelLocked = false;
            return false;
        }

        // Hitung titik pusat (centroid) dari gugus kantung mineral tempat seedResWorld berada
        // agar poros terowongan mengarah tepat ke jantung deposit mineral tersebut.
        Vector2 seedLocal = TargetAsteroid.FastWorldToLocal(seedResWorld);
        Vector2I seedGrid = TargetAsteroid.LocalToGrid(seedLocal);

        Vector2 sumLocal = Vector2.Zero;
        int countInPocket = 0;
        const int pocketScanRad = 6;

        for (int dy = -pocketScanRad; dy <= pocketScanRad; dy++)
        {
            for (int dx = -pocketScanRad; dx <= pocketScanRad; dx++)
            {
                int gx = seedGrid.X + dx;
                int gy = seedGrid.Y + dy;
                if (!TargetAsteroid.IsValidGridCoord(gx, gy)) continue;
                if (Asteroid.TryGetResourceType(TargetAsteroid.GetVoxelAt(gx, gy), out _))
                {
                    sumLocal += TargetAsteroid.GridToLocalCenter(gx, gy);
                    countInPocket++;
                }
            }
        }

        Vector2 pocketCenterLocal = countInPocket > 0 ? (sumLocal / countInPocket) : seedLocal;

        // Pilih voxel resource nyata di dalam kantung yang paling dekat dengan centroid kantung
        Vector2 pocketCenterWorld = TargetAsteroid.FastLocalToWorld(pocketCenterLocal);
        if (TargetAsteroid.FindClosestResourceVoxel(pocketCenterWorld, out Vector2 anchorResWorld, out _))
        {
            _tunnelShaftLocalOrigin = TargetAsteroid.FastWorldToLocal(anchorResWorld);
            _tunnelPocketLocalCenter = _tunnelShaftLocalOrigin;
            TargetResourceWorldPos = anchorResWorld;
        }
        else
        {
            _tunnelShaftLocalOrigin = seedLocal;
            _tunnelPocketLocalCenter = seedLocal;
            TargetResourceWorldPos = seedResWorld;
        }

        // Uji 16 arah masuk kandidat di sekeliling asteroid untuk mencari jalur terowongan paling efisien
        // (kombinasi kerak batu paling tipis + jarak tempuh kapal paling dekat)
        Vector2 localShipPos = TargetAsteroid.FastWorldToLocal(corePos);
        Vector2 bestLocalOutward = _tunnelShaftLocalOrigin.LengthSquared() > 16.0f
            ? _tunnelShaftLocalOrigin.Normalized()
            : (localShipPos.LengthSquared() > 1.0f ? localShipPos.Normalized() : Vector2.Right);

        float bestScore = float.MaxValue;
        const int candidateDirs = 16;

        for (int i = 0; i < candidateDirs + 2; i++)
        {
            Vector2 candLocalOut;
            if (i < candidateDirs)
            {
                float ang = (float)i / candidateDirs * Mathf.Tau;
                candLocalOut = new Vector2(MathF.Cos(ang), MathF.Sin(ang));
            }
            else if (i == candidateDirs && _tunnelShaftLocalOrigin.LengthSquared() > 4.0f)
            {
                candLocalOut = _tunnelShaftLocalOrigin.Normalized();
            }
            else
            {
                Vector2 toShip = localShipPos - _tunnelShaftLocalOrigin;
                candLocalOut = toShip.LengthSquared() > 4.0f ? toShip.Normalized() : Vector2.Right;
            }

            Vector2 candWorldOut = TargetAsteroid.FastRotateToWorld(candLocalOut).Normalized();
            Vector2 candWorldIn = -candWorldOut;
            Vector2 anchorWorld = TargetAsteroid.FastLocalToWorld(_tunnelShaftLocalOrigin);
            Vector2 probeStartWorld = anchorWorld + candWorldOut * (TargetAsteroid.OuterBoundRadius + 50.0f);
            float distProbeToAnchor = (anchorWorld - probeStartWorld).Dot(candWorldIn);

            float rockThickness = distProbeToAnchor;
            if (TargetAsteroid.RaycastVoxel(probeStartWorld, candWorldIn, distProbeToAnchor + 20.0f, out Asteroid.VoxelRayHit surfaceHit) && surfaceHit.Hit)
            {
                rockThickness = MathF.Max(0.0f, distProbeToAnchor - surfaceHit.Distance);
            }

            float shaftProj = (anchorWorld - TargetAsteroid.CachedPosition).Dot(candWorldOut);
            Vector2 entryWorld = anchorWorld + candWorldOut * ((TargetAsteroid.OuterBoundRadius + Core.CoreCollisionRadius + 45.0f) - shaftProj);
            float travelDist = corePos.DistanceTo(entryWorld);

            // Bobot: ketebalan batu yang harus dibor (2.4x) + jarak terbang kapal ke mulut lorong (0.38x)
            float totalCost = rockThickness * 2.4f + travelDist * 0.38f;
            if (totalCost < bestScore)
            {
                bestScore = totalCost;
                bestLocalOutward = candLocalOut;
            }
        }

        _tunnelLocalOutwardDir = bestLocalOutward.Normalized();
        _isTunnelLocked = true;
        _hasStickyAimVoxel = false;
        _currentSweepLateral = 0.0f;
        _stuckTimer = 0.0f;
        return true;
    }

    /// <summary>
    /// Menghitung dan membandingkan estimasi efisiensi waktu (detik) antara:
    /// Opsi A (T_internal): Meneruskan ekskavasi percabangan dari dalam gua saat ini menuju nextResWorld.
    /// Opsi B (T_external): Mundur keluar melalui lorong, terbang memutar di ruang hampa, dan mengebor lubang baru dari luar.
    /// Mengembalikan true jika Opsi A (Internal Branching) lebih cepat atau setara dengan Opsi B.
    /// </summary>
    private bool EvaluateInternalVsExternalMiningCost(
        Vector2 corePos,
        Vector2 nextResWorld,
        float shipDepthFromMouth,
        Vector2 outwardWorld,
        out Vector2 bestExternalEntryWorld,
        out Vector2 bestExternalOutwardDir,
        out float estimatedInternalTime,
        out float estimatedExternalTime)
    {
        bestExternalEntryWorld = Vector2.Zero;
        bestExternalOutwardDir = outwardWorld;
        estimatedInternalTime = float.MaxValue;
        estimatedExternalTime = float.MaxValue;

        if (TargetAsteroid == null || !TargetAsteroid.IsActiveInTree)
        {
            return false;
        }

        float blocksPerSec = 2.8f + MinerCount * 0.95f;
        float laserSpeed = MathF.Max(20.0f, blocksPerSec * Asteroid.VoxelSize); // px/s

        // -------------------------------------------------------------------------
        // 1. Hitung Waktu Opsi A (T_internal): Menembus dari dalam gua ke nextResWorld
        // -------------------------------------------------------------------------
        Vector2 toRes = nextResWorld - corePos;
        float distToRes = toRes.Length();
        Vector2 dirToRes = distToRes > 1.0f ? toRes / distToRes : Vector2.Right;

        float rockInternalThickness = distToRes;
        float openAirDist = 0.0f;

        if (TargetAsteroid.RaycastVoxel(corePos, dirToRes, distToRes, out Asteroid.VoxelRayHit inHit))
        {
            if (inHit.Hit)
            {
                openAirDist = inHit.Distance;
                rockInternalThickness = MathF.Max(0.0f, distToRes - inHit.Distance);
            }
            else
            {
                // Tidak ada batu sama sekali antara kapal dan target mineral (gua sudah bolong!)
                openAirDist = distToRes;
                rockInternalThickness = 0.0f;
            }
        }

        // Kecepatan gerak di dalam gua ~180 px/s
        estimatedInternalTime = (rockInternalThickness / laserSpeed) + (openAirDist / 180.0f);

        // -------------------------------------------------------------------------
        // 2. Hitung Waktu Opsi B (T_external): Mundur keluar + Terbang memutar + Bor baru
        // -------------------------------------------------------------------------
        // Waktu mundur keluar melalui lorong bolong (kecepatan mundur ~240 px/s)
        float exitDist = MathF.Max(0.0f, shipDepthFromMouth + 30.0f);
        float exitTime = exitDist / 240.0f;

        // Cari titik masuk eksternal terbaik dari luar angkasa ke nextResWorld
        Vector2 nextResLocal = TargetAsteroid.FastWorldToLocal(nextResWorld);
        Vector2 bestCandLocalOut = nextResLocal.LengthSquared() > 16.0f ? nextResLocal.Normalized() : Vector2.Right;
        float bestExternalScore = float.MaxValue;
        float bestRockExt = distToRes;
        const int candidateDirs = 12;

        for (int i = 0; i < candidateDirs; i++)
        {
            float ang = (float)i / candidateDirs * Mathf.Tau;
            Vector2 candLocal = new Vector2(MathF.Cos(ang), MathF.Sin(ang));
            Vector2 candWorldOut = TargetAsteroid.FastRotateToWorld(candLocal).Normalized();
            Vector2 candWorldIn = -candWorldOut;

            Vector2 probeStartWorld = nextResWorld + candWorldOut * (TargetAsteroid.OuterBoundRadius + 50.0f);
            float distProbeToRes = (nextResWorld - probeStartWorld).Dot(candWorldIn);

            float candRockThick = distProbeToRes;
            if (TargetAsteroid.RaycastVoxel(probeStartWorld, candWorldIn, distProbeToRes + 20.0f, out Asteroid.VoxelRayHit sHit) && sHit.Hit)
            {
                candRockThick = MathF.Max(0.0f, distProbeToRes - sHit.Distance);
            }

            if (candRockThick < bestExternalScore)
            {
                bestExternalScore = candRockThick;
                bestCandLocalOut = candLocal;
                bestRockExt = candRockThick;
            }
        }

        Vector2 candExtWorldOut = TargetAsteroid.FastRotateToWorld(bestCandLocalOut).Normalized();
        bestExternalOutwardDir = candExtWorldOut;
        bestExternalEntryWorld = TargetAsteroid.CachedPosition + candExtWorldOut * (TargetAsteroid.OuterBoundRadius + 50.0f);

        // Waktu terbang memutar di ruang angkasa (kecepatan terbang ~380 px/s)
        float angleDelta = MathF.Abs(outwardWorld.AngleTo(candExtWorldOut));
        float arcDist = angleDelta * (TargetAsteroid.OuterBoundRadius + 45.0f);
        float spaceFlightTime = arcDist / 380.0f;

        // Waktu mengebor terowongan baru dari luar
        float externalDrillTime = bestRockExt / laserSpeed;

        // Tambahkan penalti jeda manuver relokasi (2.5 detik) agar tidak terjadi osilasi bolak-balik
        const float relocateOverheadDelay = 2.5f;
        estimatedExternalTime = exitTime + spaceFlightTime + externalDrillTime + relocateOverheadDelay;

        // Opsi A (Internal) dipilih jika lebih cepat atau sama dengan Opsi B (External)
        return estimatedInternalTime <= estimatedExternalTime;
    }

    /// <summary>
    /// Memastikan kantung mineral yang sedang dikunci masih memiliki sisa voxel mineral.
    /// Memperbarui TargetResourceWorldPos ke voxel mineral terdekat di dalam kantung yang sama
    /// TANPA menggeser garis tengah lorong (_tunnelShaftLocalOrigin & _tunnelLocalOutwardDir).
    /// </summary>
    private bool ValidateAndRefreshLockedPocket()
    {
        if (!_isTunnelLocked || TargetAsteroid == null || !TargetAsteroid.IsActiveInTree || TargetAsteroid.RemainingResourceVoxelCount <= 0)
        {
            return false;
        }

        Vector2 pocketCenterWorld = TargetAsteroid.FastLocalToWorld(_tunnelPocketLocalCenter);
        Vector2 outwardWorld = TargetAsteroid.FastRotateToWorld(_tunnelLocalOutwardDir).Normalized();

        // Cari voxel mineral yang paling dekat ke arah datangnya terowongan dari kantung yang sedang kita gali
        Vector2 searchProbeWorld = pocketCenterWorld + outwardWorld * 38.0f;
        if (!TargetAsteroid.FindClosestResourceVoxel(searchProbeWorld, out Vector2 nextResWorld, out _))
        {
            return false;
        }

        Vector2 nextResLocal = TargetAsteroid.FastWorldToLocal(nextResWorld);
        float distFromPocketCenter = nextResLocal.DistanceTo(_tunnelPocketLocalCenter);

        Vector2 localInward = -_tunnelLocalOutwardDir;
        Vector2 localPerp = new Vector2(-localInward.Y, localInward.X);
        float lateralFromShaft = MathF.Abs((nextResLocal - _tunnelShaftLocalOrigin).Dot(localPerp));
        float maxReachableLateral = GetRequiredTunnelHalfWidth() + 55.0f;

        // Selama voxel mineral tersebut masih berada di dalam gugus kantung yang sama (<= 125 px)
        // dan masih terjangkau dari ujung lorong terowongan, pertahankan lorong yang sama!
        if (distFromPocketCenter <= 125.0f && lateralFromShaft <= maxReachableLateral)
        {
            TargetResourceWorldPos = nextResWorld;
            _tunnelPocketLocalCenter = _tunnelPocketLocalCenter.Lerp(nextResLocal, 0.20f);
            return true;
        }

        // Evaluator Adaptif (Fase 3): Jika deposit segaris sudah habis tapi kapal berada di dalam rongga asteroid,
        // hitung efisiensi biaya waktu: apakah lebih cepat tembus dari dalam (Opsi A) atau putar luar (Opsi B)?
        Vector2 corePos = Core.GlobalPosition;
        float distToAstCenter = corePos.DistanceTo(TargetAsteroid.CachedPosition);
        Vector2 shaftOriginWorld = TargetAsteroid.FastLocalToWorld(_tunnelShaftLocalOrigin);
        float mouthRefRadius = TargetAsteroid.OuterBoundRadius + 52.0f;
        float shaftProjOnOutward = (shaftOriginWorld - TargetAsteroid.CachedPosition).Dot(outwardWorld);
        Vector2 mouthRefWorld = shaftOriginWorld + outwardWorld * (mouthRefRadius - shaftProjOnOutward);
        float shipDepthFromMouth = (corePos - mouthRefWorld).Dot(-outwardWorld);

        bool isInsideCavern = distToAstCenter < TargetAsteroid.OuterBoundRadius + 15.0f && shipDepthFromMouth > 35.0f;

        if (isInsideCavern)
        {
            if (EvaluateInternalVsExternalMiningCost(
                corePos,
                nextResWorld,
                shipDepthFromMouth,
                outwardWorld,
                out _,
                out _,
                out float tIn,
                out float tExt))
            {
                // Opsi A MENANG: Lebih cepat dan efisien melanjutkan ekskavasi percabangan dari dalam gua!
                TargetResourceWorldPos = nextResWorld;
                _tunnelPocketLocalCenter = _tunnelPocketLocalCenter.Lerp(nextResLocal, 0.25f);
                return true;
            }
        }

        // Kantung mineral di terowongan ini sudah habis dan lebih efisien keluar mencari jalur baru!
        return false;
    }

    /// <summary>
    /// Mencari kristal resource (IsCrystalMatter) yang jatuh di dalam bagian terowongan yang sudah bolong
    /// atau tepat di mulut terowongan agar kapal maju sedikit untuk menyedotnya.
    /// </summary>
    private bool TryFindCrystalInsideClearedTunnel(
        Vector2 corePos,
        Vector2 mouthRefWorld,
        Vector2 inwardWorld,
        Vector2 perpWorld,
        float tunnelHalfWidth,
        float coreClearanceDist,
        out Vector2 targetCrystalStayPos)
    {
        targetCrystalStayPos = corePos;
        Simulation.CollectNearbyCrystalPositions(corePos, 480.0f, _nearbyCrystals);
        if (_nearbyCrystals.Count == 0) return false;

        float bestDistSq = float.MaxValue;
        bool found = false;
        float maxSafeShipDepth = MathF.Max(0.0f, coreClearanceDist - (Core.CoreCollisionRadius + 18.0f));
        float maxSafeLat = MathF.Max(0.0f, tunnelHalfWidth - Core.CoreCollisionRadius - 6.0f);

        for (int i = 0; i < _nearbyCrystals.Count; i++)
        {
            Vector2 cPos = _nearbyCrystals[i];
            Vector2 rel = cPos - mouthRefWorld;
            float cDepth = rel.Dot(inwardWorld);
            float cLat = rel.Dot(perpWorld);

            // Kristal berada di dalam koridor terowongan atau tepat di luar mulut terowongan
            if (MathF.Abs(cLat) <= tunnelHalfWidth + 24.0f && cDepth >= -140.0f && cDepth <= coreClearanceDist + 35.0f)
            {
                float dSq = corePos.DistanceSquaredTo(cPos);
                if (dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    float clampedDepth = Mathf.Clamp(cDepth, -60.0f, maxSafeShipDepth);
                    float clampedLat = Mathf.Clamp(cLat, -maxSafeLat, maxSafeLat);
                    targetCrystalStayPos = mouthRefWorld + inwardWorld * clampedDepth + perpWorld * clampedLat;
                    found = true;
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Mencari kristal resource bebas di luar batu yang tidak terhalang dinding asteroid.
    /// </summary>
    private bool TryFindReachableFreeCrystal(Vector2 corePos, float searchRadius, out Vector2 crystalPos)
    {
        crystalPos = corePos;
        Simulation.CollectNearbyCrystalPositions(corePos, searchRadius, _nearbyCrystals);
        if (_nearbyCrystals.Count == 0) return false;

        float bestDistSq = float.MaxValue;
        bool found = false;

        for (int i = 0; i < _nearbyCrystals.Count; i++)
        {
            Vector2 cand = _nearbyCrystals[i];
            float dSq = corePos.DistanceSquaredTo(cand);
            if (dSq >= bestDistSq) continue;

            // Pastikan garis menuju kristal tidak terhalang batu asteroid yang masih padat
            bool blockedBySolidAsteroid = false;
            var asteroids = Asteroid.ActiveAsteroids;
            for (int a = 0; a < asteroids.Count; a++)
            {
                var ast = asteroids[a];
                if (ast == null || !ast.IsActiveInTree || ast.IsDestroying) continue;
                if (cand.DistanceTo(ast.CachedPosition) < ast.OuterBoundRadius + 12.0f)
                {
                    Vector2 toCand = cand - corePos;
                    float dist = toCand.Length();
                    if (dist > 1.0f && ast.RaycastVoxel(corePos, toCand / dist, dist, out Asteroid.VoxelRayHit hit) && hit.Hit)
                    {
                        blockedBySolidAsteroid = true;
                        break;
                    }
                }
            }

            if (!blockedBySolidAsteroid)
            {
                bestDistSq = dSq;
                crystalPos = cand;
                found = true;
            }
        }

        return found;
    }

    /// <summary>
    /// Perilaku saat menambang Bintang Aether (IsEnergyAsteroid):
    /// Menjaga jarak aman di luar cakrawala peristiwa sambil memanaskan bintang selama 15 detik hingga Supernova.
    /// </summary>
    private void UpdateEnergyStarMiningBehavior(float dt)
    {
        if (TargetAsteroid == null) return;

        Vector2 corePos = Core.GlobalPosition;
        Vector2 starPos = TargetAsteroid.CachedPosition;
        Vector2 fromStar = corePos - starPos;
        Vector2 outDir = fromStar.LengthSquared() > 1.0f ? fromStar.Normalized() : Vector2.Right;

        float safeStandDist = TargetAsteroid.OuterBoundRadius + Core.CoreCollisionRadius + 165.0f;
        StandoffPosition = starPos + outDir * safeStandDist;
        TargetResourceWorldPos = starPos;

        SteerShipSmoothlyToward(StandoffPosition, dt, maxApproachSpeed: 360.0f, arrivalRadius: 40.0f);

        if (corePos.DistanceTo(starPos) <= safeStandDist + 180.0f)
        {
            State = EnemyState.MiningResource;
            UpdateMiningLaser(dt, starPos);
        }
        else
        {
            State = EnemyState.TravelingToStandoff;
            DeactivateMiningLaser(dt);
        }
    }

    /// <summary>
    /// Memeriksa apakah ada voxel batuan padat yang langsung menghalangi gerak haluan kapal di jarak dekat.
    /// Jika ada, arahkan dan tembakkan laser penambangan ke voxel penghalang tersebut (Drill-to-Clear).
    /// Mengembalikan true jika laser digunakan untuk membersihkan rintangan di depan kapal.
    /// </summary>
    private bool CheckAndDrillObstacleAhead(Vector2 corePos, Vector2 moveDir, float dt, float checkReach = -1.0f)
    {
        if (moveDir.LengthSquared() < 0.01f)
        {
            return false;
        }

        float reach = checkReach > 0.0f ? checkReach : (Core.CoreCollisionRadius + 32.0f);
        Vector2 normDir = moveDir.Normalized();

        // 1. Cek asteroid target terlebih dahulu jika valid
        if (TargetAsteroid != null && TargetAsteroid.IsActiveInTree && !TargetAsteroid.IsDestroying)
        {
            if (TargetAsteroid.RaycastVoxel(corePos, normDir, reach, out Asteroid.VoxelRayHit hit) && hit.Hit)
            {
                UpdateMiningLaser(dt, hit.WorldCellCenter);
                return true;
            }
        }

        // 2. Cek asteroid tetangga jika ada yang berada sangat dekat dan berpotensi menyenggol haluan
        var asteroids = Asteroid.ActiveAsteroids;
        if (asteroids != null)
        {
            for (int i = 0; i < asteroids.Count; i++)
            {
                var ast = asteroids[i];
                if (ast == null || !IsInstanceValid(ast) || !ast.IsActiveInTree || ast.IsDestroying || ast == TargetAsteroid)
                {
                    continue;
                }

                if (corePos.DistanceTo(ast.CachedPosition) <= ast.OuterBoundRadius + reach + 10.0f)
                {
                    if (ast.RaycastVoxel(corePos, normDir, reach, out Asteroid.VoxelRayHit hit) && hit.Hit)
                    {
                        UpdateMiningLaser(dt, hit.WorldCellCenter);
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Menghitung arah kemudi (analog WASD) dengan penghindaran rintangan multi-asteroid (Steering Raycast Avoidance).
    /// Murni membelokkan arah kemudi virtual tanpa gaya tolak fisis (100% fair play).
    /// </summary>
    private Vector2 ComputeObstacleAvoidanceSteerDir(Vector2 shipPos, Vector2 targetPos, float safeClearance = 32.0f)
    {
        Vector2 toTarget = targetPos - shipPos;
        float distToTarget = toTarget.Length();
        if (distToTarget < 1.0f)
        {
            return Vector2.Zero;
        }

        Vector2 dirToTarget = toTarget / distToTarget;
        var asteroids = Asteroid.ActiveAsteroids;
        if (asteroids == null || asteroids.Count == 0)
        {
            return dirToTarget;
        }

        Asteroid? mostThreateningObs = null;
        float nearestCollisionDist = float.MaxValue;
        float chosenObsClearanceRadius = 0.0f;

        float lookAhead = MathF.Min(distToTarget, 750.0f);

        for (int i = 0; i < asteroids.Count; i++)
        {
            var ast = asteroids[i];
            if (ast == null || !IsInstanceValid(ast) || !ast.IsActiveInTree || ast.IsDestroying)
            {
                continue;
            }

            Vector2 astPos = ast.CachedPosition;
            Vector2 toAst = astPos - shipPos;
            float distToAstCenter = toAst.Length();

            if (ast == TargetAsteroid)
            {
                // Untuk asteroid target: cek apakah garis lurus ke targetPos memotong badan batuan asteroid target
                float targetClearanceRadius = ast.OuterBoundRadius + Core.CoreCollisionRadius + 14.0f;
                float proj = toAst.Dot(dirToTarget);

                // Jika target berada di balik asteroid (garis potong memotong badan asteroid)
                if (proj > 0.0f && proj < distToTarget)
                {
                    Vector2 closestPt = shipPos + dirToTarget * proj;
                    float distFromLine = closestPt.DistanceTo(astPos);
                    if (distFromLine < targetClearanceRadius)
                    {
                        if (proj < nearestCollisionDist)
                        {
                            nearestCollisionDist = proj;
                            mostThreateningObs = ast;
                            chosenObsClearanceRadius = targetClearanceRadius + 18.0f;
                        }
                    }
                }
                else if (distToAstCenter < targetClearanceRadius - 10.0f)
                {
                    // Kapal terdorong terlalu dekat ke permukaan luar asteroid target
                    if (distToAstCenter < nearestCollisionDist)
                    {
                        nearestCollisionDist = distToAstCenter;
                        mostThreateningObs = ast;
                        chosenObsClearanceRadius = targetClearanceRadius;
                    }
                }
            }
            else
            {
                // Asteroid Tetangga / Lingkungan: Jaga jarak aman penuh agar tidak menabrak atau terjepit
                float neighborClearanceRadius = ast.OuterBoundRadius + Core.CoreCollisionRadius + safeClearance;
                float proj = toAst.Dot(dirToTarget);

                // Cek apakah asteroid berada di jangkauan pandang ke depan
                if (proj > -neighborClearanceRadius && proj < lookAhead + neighborClearanceRadius)
                {
                    float clampedProj = Mathf.Clamp(proj, 0.0f, lookAhead);
                    Vector2 closestPt = shipPos + dirToTarget * clampedProj;
                    float distFromLine = closestPt.DistanceTo(astPos);

                    if (distFromLine < neighborClearanceRadius)
                    {
                        // Menemukan potensi tabrakan dengan asteroid tetangga
                        float collisionUrgency = MathF.Max(0.0f, proj);
                        if (collisionUrgency < nearestCollisionDist)
                        {
                            nearestCollisionDist = collisionUrgency;
                            mostThreateningObs = ast;
                            chosenObsClearanceRadius = neighborClearanceRadius;
                        }
                    }
                }
            }
        }

        // Jika tidak ada rintangan yang menghalangi jalur penerbangan, terbang lurus ke sasaran
        if (mostThreateningObs == null)
        {
            return dirToTarget;
        }

        // Ada rintangan: belokkan kemudi secara tangensial melingkari rintangan menuju target
        Vector2 obsPos = mostThreateningObs.CachedPosition;
        Vector2 fromObs = shipPos - obsPos;
        float distFromObsCenter = fromObs.Length();
        Vector2 radialDir = distFromObsCenter > 1.0f ? fromObs / distFromObsCenter : -dirToTarget;

        Vector2 tangCW = new Vector2(-radialDir.Y, radialDir.X);
        Vector2 tangCCW = -tangCW;

        float scoreCW = tangCW.Dot(toTarget) + (Core.Velocity.LengthSquared() > 10.0f ? tangCW.Dot(Core.Velocity.Normalized()) * 0.35f : 0.0f);
        float scoreCCW = tangCCW.Dot(toTarget) + (Core.Velocity.LengthSquared() > 10.0f ? tangCCW.Dot(Core.Velocity.Normalized()) * 0.35f : 0.0f);
        Vector2 chosenTang = (scoreCW >= scoreCCW) ? tangCW : tangCCW;

        // Koreksi radial jika kapal berada di dalam radius aman
        float radialDeficit = chosenObsClearanceRadius - distFromObsCenter;
        float pushFactor = radialDeficit > 0.0f
            ? Mathf.Clamp(radialDeficit / chosenObsClearanceRadius * 1.5f, 0.1f, 1.2f)
            : Mathf.Clamp(radialDeficit * 0.02f, -0.4f, 0.0f);

        // Padukan arah tangensial dengan dorongan keluar dari rintangan
        Vector2 avoidanceDir = (chosenTang + radialDir * pushFactor).Normalized();

        // Jika rintangan masih agak jauh ke depan (bukan sangat dekat), perhalus transisi dengan arah tujuan
        if (distFromObsCenter > chosenObsClearanceRadius + 30.0f)
        {
            avoidanceDir = avoidanceDir.Lerp(dirToTarget, 0.25f).Normalized();
        }

        return avoidanceDir;
    }

    private void SteerShipSmoothlyToward(Vector2 targetWorldPos, float dt, float maxApproachSpeed, float arrivalRadius)
    {
        Vector2 toTarget = targetWorldPos - Core.GlobalPosition;
        float dist = toTarget.Length();
        if (dist < 1.0f) return;

        Vector2 steerDir = ComputeObstacleAvoidanceSteerDir(Core.GlobalPosition, targetWorldPos);
        if (steerDir.LengthSquared() < 0.01f)
        {
            steerDir = toTarget / dist;
        }

        if (dist <= arrivalRadius * 2.5f)
        {
            float desiredSpd = Mathf.Lerp(0.0f, maxApproachSpeed, Mathf.Clamp(dist / (arrivalRadius * 2.5f), 0.0f, 1.0f));
            Vector2 desiredVel = steerDir * desiredSpd;
            Core.Velocity = Core.Velocity.Lerp(desiredVel, MathF.Min(1.0f, 6.5f * dt));
        }
        else
        {
            Core.Velocity += steerDir * (Core.Acceleration * 0.88f * dt);
            if (Core.Velocity.Length() > maxApproachSpeed)
            {
                Core.Velocity = Core.Velocity.LimitLength(maxApproachSpeed);
            }
        }
    }

    /// <summary>
    /// Mencari asteroid terbaik yang masih memiliki deposit mineral berharga.
    /// Memprioritaskan asteroid terdekat serta jenis mineral yang sedang dibutuhkan oleh pabrik.
    /// </summary>
    private void FindBestAsteroidWithResources()
    {
        var asteroids = Asteroid.ActiveAsteroids;
        Asteroid? best = null;
        float bestScore = float.MaxValue;
        Vector2 corePos = Core.GlobalPosition;

        bool needAether = ResourceStock[(int)ResourceType.Aether] < 2 && MinerCount >= 3;

        for (int i = 0; i < asteroids.Count; i++)
        {
            var ast = asteroids[i];
            if (ast == null || !IsInstanceValid(ast) || !ast.IsActiveInTree || ast.IsDestroying) continue;
            if (ast.RemainingResourceVoxelCount <= 0) continue;
            if (ast.CachedLinearVelocity.LengthSquared() > 180.0f * 180.0f) continue;

            if (ast.IsEnergyAsteroid)
            {
                if (ast.IsInSeedCycle || !needAether) continue;
            }

            float dist = corePos.DistanceTo(ast.CachedPosition);
            float score = dist;

            if (!ast.IsEnergyAsteroid)
            {
                if ((ast.PrimaryResource == ResourceType.Quartz || (ast.HasSecondaryResource && ast.SecondaryResource == ResourceType.Quartz))
                    && ResourceStock[(int)ResourceType.Quartz] < 12)
                {
                    score *= 0.68f;
                }
                else if ((ast.PrimaryResource == ResourceType.Ferrite || (ast.HasSecondaryResource && ast.SecondaryResource == ResourceType.Ferrite))
                    && ResourceStock[(int)ResourceType.Ferrite] < 12)
                {
                    score *= 0.72f;
                }
            }
            else if (needAether)
            {
                score *= 0.75f;
            }

            if (score < bestScore)
            {
                bestScore = score;
                best = ast;
            }
        }

        TargetAsteroid = best;
        _isTunnelLocked = false;
        _hasStickyAimVoxel = false;
        _stuckTimer = 0.0f;
        StandoffPosition = Vector2.Zero;

        if (TargetAsteroid != null && !TargetAsteroid.IsEnergyAsteroid)
        {
            LockTunnelToBestPocket();
        }
    }

    private void DeactivateMiningLaser(float dt)
    {
        _laserPrimingTimer = 0.0f;
        _laserFocusWarmup = MathF.Max(0.0f, _laserFocusWarmup - dt * 8.0f);
        if (IsMiningLaserActive)
        {
            IsMiningLaserActive = false;
            _laserActiveHitBoxes.Clear();
            Simulation.QueueRedraw();
        }
    }

    /// <summary>
    /// Menembakkan Heavy Mining Laser yang 100% identik rumusnya dengan SwarmSimulation.UpdateMiningLaser milik Pemain:
    /// - Lebar blok (LaserBlockSpan) = 1 + (minerCount / 15)
    /// - Jumlah sub-ray = 1 saat Miner < 15, atau (LaserBlockSpan * 2 - 1) saat Miner >= 15
    /// - Laju erosi = (2.8 + minerCount * 0.95) * dt
    /// </summary>
    private void UpdateMiningLaser(float dt, Vector2 aimTargetWorldPos)
    {
        _activeMinerPositions.Clear();
        Vector2 corePos = Core.GlobalPosition;

        Simulation.CollectEnemyMinerPositions(_activeMinerPositions);
        int minerCount = _activeMinerPositions.Count;

        if (minerCount == 0 || TargetAsteroid == null || !TargetAsteroid.IsActiveInTree)
        {
            DeactivateMiningLaser(dt);
            return;
        }

        IsMiningLaserActive = true;
        _laserPrimingTimer += dt;
        bool isPriming = _laserPrimingTimer < LaserPrimingDuration;
        float primingProgress = Mathf.Clamp(_laserPrimingTimer / LaserPrimingDuration, 0.0f, 1.0f);

        _laserFocusWarmup = isPriming
            ? primingProgress * 0.42f
            : MathF.Min(1.0f, _laserFocusWarmup + dt * 6.0f);

        _laserPulseTimer += dt * (1.0f + minerCount * 0.14f);

        Vector2 toTarget = aimTargetWorldPos - corePos;
        Vector2 desiredAimDir = toTarget.LengthSquared() > 1.0f ? toTarget.Normalized() : Vector2.Right;
        LaserAimDir = LaserAimDir.Lerp(desiredAimDir, MathF.Min(1.0f, 26.0f * dt)).Normalized();
        Vector2 tangent = new Vector2(-LaserAimDir.Y, LaserAimDir.X);

        // Rumus lebar laser identik 100% dengan pemain (SwarmSimulation.UpdateMiningLaser)
        LaserBlockSpan = Math.Clamp(1 + (minerCount / 15), 1, 5);
        int remainderInTier = minerCount % 15;
        float tierProgress = remainderInTier / 14.0f;

        float targetWidth = LaserBlockSpan switch
        {
            1 => Mathf.Lerp(3.2f, 5.0f, tierProgress),
            2 => Mathf.Lerp(15.5f, 19.5f, tierProgress),
            3 => Mathf.Lerp(28.0f, 33.0f, tierProgress),
            4 => Mathf.Lerp(40.5f, 46.0f, tierProgress),
            _ => Mathf.Lerp(53.0f, 58.0f, tierProgress)
        };
        VisualBeamWidth = isPriming ? 1.4f : Mathf.Lerp(1.5f, targetWidth, _laserFocusWarmup);

        Vector2 rayBaseOrigin = corePos + LaserAimDir * 14.0f;
        float maxRange = 2600.0f;

        int subRayCount = (LaserBlockSpan == 1) ? 1 : (LaserBlockSpan * 2 - 1);
        float totalLateralSpan = (LaserBlockSpan - 1) * (Asteroid.VoxelSize * 0.92f);

        _laserActiveHitBoxes.Clear();
        float closestCenterDist = maxRange;
        Vector2 centerHitPoint = aimTargetWorldPos;
        bool anyRayHit = false;

        var asteroids = Asteroid.ActiveAsteroids;
        float erosionBlocksPerSec = 2.8f + minerCount * 0.95f;
        float heatDeltaPerSubRay = (erosionBlocksPerSec * dt) / MathF.Max(1.0f, subRayCount * 0.42f);

        for (int r = 0; r < subRayCount; r++)
        {
            float lateralOffset = (subRayCount == 1)
                ? 0.0f
                : Mathf.Lerp(-totalLateralSpan * 0.5f, totalLateralSpan * 0.5f, (float)r / (subRayCount - 1));

            Vector2 subOrigin = rayBaseOrigin + tangent * lateralOffset;
            float subClosest = maxRange;
            Asteroid? subHitAst = null;
            Asteroid.VoxelRayHit subBestHit = default;

            for (int i = 0; i < asteroids.Count; i++)
            {
                var ast = asteroids[i];
                if (ast == null || !ast.IsActiveInTree) continue;

                if (ast.RaycastVoxel(subOrigin, LaserAimDir, subClosest, out Asteroid.VoxelRayHit vHit))
                {
                    if (vHit.Hit && vHit.Distance < subClosest)
                    {
                        subClosest = vHit.Distance;
                        subHitAst = ast;
                        subBestHit = vHit;
                    }
                }
            }

            if (subHitAst != null && subBestHit.Hit)
            {
                anyRayHit = true;
                if (subClosest < closestCenterDist)
                {
                    closestCenterDist = subClosest;
                    centerHitPoint = subOrigin + LaserAimDir * subClosest - tangent * lateralOffset;
                    _laserHitAsteroidRotation = subHitAst.CachedRotation;
                }

                // Jika arah bidik sudah terkunci ke sasaran (dot >= 0.996) dan ada voxel di koridor yang menghalangi garis pandang langsung,
                // kunci voxel penghalang tersebut sampai hancur agar panas laser 100% tuntas tanpa osilasi.
                if (subHitAst == TargetAsteroid && subRayCount == 1 && !subHitAst.IsEnergyAsteroid && LaserAimDir.Dot(desiredAimDir) >= 0.996f)
                {
                    _stickyAimGx = subBestHit.GridX;
                    _stickyAimGy = subBestHit.GridY;
                    _hasStickyAimVoxel = true;
                }

                if (!isPriming)
                {
                    bool destroyed = subHitAst.ApplyLaserHeatAtVoxel(
                        subBestHit.GridX,
                        subBestHit.GridY,
                        heatDeltaPerSubRay,
                        LaserAimDir,
                        out Asteroid.VoxelType brokenType,
                        out Vector2 brokenWorldCenter,
                        out float updatedHeat
                    );

                    _laserActiveHitBoxes.Add((subBestHit.WorldCellCenter, updatedHeat, subBestHit.Type));

                    if (destroyed)
                    {
                        _stuckTimer = 0.0f;
                        if (_hasStickyAimVoxel && subBestHit.GridX == _stickyAimGx && subBestHit.GridY == _stickyAimGy)
                        {
                            _hasStickyAimVoxel = false;
                        }
                        Simulation.SpawnVoxelErosionDebris(brokenWorldCenter, -LaserAimDir, brokenType, subHitAst.IsSupercharged ? 2 : 1);
                    }
                }
            }
        }

        float focusDist = MathF.Min(42.0f, MathF.Max(16.0f, closestCenterDist * 0.55f));
        LaserConvergencePoint = corePos + LaserAimDir * focusDist;
        LaserImpactPoint = anyRayHit ? centerHitPoint : aimTargetWorldPos;
        Simulation.QueueRedraw();
    }

    /// <summary>
    /// Logika antrian pabrik otomatis (100% menggunakan harga resep resmi Hive Foundry Pemain):
    /// 1. Expand Hive Core (+1 Hex): 35 Ferrite, 25 Quartz, 25 Carbide, 20 Aether
    /// 2. Mining Droid (+2): 10 Ferrite, 8 Quartz, 2 Aether (hingga 18+ miner untuk membuka laser Tier 2)
    /// 3. Guardian Shield (+5): 12 Ferrite, 12 Quartz, 8 Carbide
    /// 4. Drill Drone (+5): 12 Ferrite, 18 Carbide, 4 Aether
    /// 5. Giga Shuriken (+5): 10 Ferrite, 14 Carbide, 10 Vortexum
    /// 6. Crescent Boomerang (+5): 8 Quartz, 16 Vortexum, 4 Aether
    /// 7. Dart Needle (+10): 10 Ferrite
    /// </summary>
    private void ProcessAutonomousFoundry()
    {
        // 1. Expand Hive Core (+1 Hex): 35 Fe, 25 Qz, 25 Cb, 20 Ae
        if (!Core.IsMitosisActive
            && ResourceStock[0] >= 35
            && ResourceStock[1] >= 25
            && ResourceStock[2] >= 25
            && ResourceStock[4] >= 20)
        {
            ResourceStock[0] -= 35;
            ResourceStock[1] -= 25;
            ResourceStock[2] -= 25;
            ResourceStock[4] -= 20;
            Core.AddNextHoneycombCell();
            return;
        }

        // 2. Prioritas Mining Droid (+2): 10 Fe, 8 Qz, 2 Ae (Naikkan sampai 18 Miner agar laser makin cepat & lebar)
        if (MinerCount < 18 && ResourceStock[0] >= 10 && ResourceStock[1] >= 8 && ResourceStock[4] >= 2)
        {
            ResourceStock[0] -= 10;
            ResourceStock[1] -= 8;
            ResourceStock[4] -= 2;
            Simulation.AddEnemySwarm(SwarmRole.MiningDroid, 2);
            return;
        }

        // 3. Prioritas Guardian Shield (+5): 12 Fe, 12 Qz, 8 Cb
        if (GuardianCount < 15 && ResourceStock[0] >= 12 && ResourceStock[1] >= 12 && ResourceStock[2] >= 8)
        {
            ResourceStock[0] -= 12;
            ResourceStock[1] -= 12;
            ResourceStock[2] -= 8;
            Simulation.AddEnemySwarm(SwarmRole.Guardian, 5);
            return;
        }

        // 4. Giga Shuriken (+5): 10 Fe, 14 Cb, 10 Vx
        if (ShurikenCount < 15 && ResourceStock[0] >= 10 && ResourceStock[2] >= 14 && ResourceStock[3] >= 10)
        {
            ResourceStock[0] -= 10;
            ResourceStock[2] -= 14;
            ResourceStock[3] -= 10;
            Simulation.AddEnemySwarm(SwarmRole.GigaShuriken, 5);
            return;
        }

        // 5. Crescent Boomerang (+5): 8 Qz, 16 Vx, 4 Ae
        if (BoomerangCount < 10 && ResourceStock[1] >= 8 && ResourceStock[3] >= 16 && ResourceStock[4] >= 4)
        {
            ResourceStock[1] -= 8;
            ResourceStock[3] -= 16;
            ResourceStock[4] -= 4;
            Simulation.AddEnemySwarm(SwarmRole.CrescentBoomerang, 5);
            return;
        }

        // 6. Drill Drone (+5): 12 Fe, 18 Cb, 4 Ae
        if (DrillCount < 15 && ResourceStock[0] >= 12 && ResourceStock[2] >= 18 && ResourceStock[4] >= 4)
        {
            ResourceStock[0] -= 12;
            ResourceStock[2] -= 18;
            ResourceStock[4] -= 4;
            Simulation.AddEnemySwarm(SwarmRole.DrillDrone, 5);
            return;
        }

        // 7. Dart Needle (+10): 10 Fe (Sisakan cadangan Ferrite untuk Miner/Guardian jika Miner masih sedikit)
        int ferriteReserve = MinerCount < 8 ? 20 : 12;
        if (ResourceStock[0] >= ferriteReserve + 10)
        {
            ResourceStock[0] -= 10;
            Simulation.AddEnemySwarm(SwarmRole.DartNeedle, 10);
        }
    }

    /// <summary>
    /// Menggambar laser penambangan musuh dalam spektrum crimson-coral neon (mendukung single-beam & multi-block tier).
    /// </summary>
    public void DrawLaser(CanvasItem canvas)
    {
        if (!IsMiningLaserActive || _activeMinerPositions.Count == 0) return;

        bool isPriming = _laserPrimingTimer < LaserPrimingDuration;
        float primingProgress = Mathf.Clamp(_laserPrimingTimer / LaserPrimingDuration, 0.0f, 1.0f);
        float pulse = 0.5f + 0.5f * MathF.Sin(_laserPulseTimer * 26.0f);
        Color feederColor = new Color(3.8f, 1.4f, 0.35f, 0.45f * (isPriming ? (0.35f + 0.65f * primingProgress) : _laserFocusWarmup));

        // 1. Feeder beams dari setiap miner musuh ke titik fokus laser
        for (int i = 0; i < _activeMinerPositions.Count; i++)
        {
            canvas.DrawLine(_activeMinerPositions[i], LaserConvergencePoint, feederColor, 1.2f, false);
        }

        // 2. Bola energi fokus di titik konvergensi
        float orbR = Mathf.Lerp(2.0f, 4.2f + LaserBlockSpan * 1.2f, isPriming ? primingProgress : _laserFocusWarmup);
        canvas.DrawCircle(LaserConvergencePoint, orbR * 1.5f, new Color(4.2f, 0.4f, 1.2f, 0.35f * _laserFocusWarmup));
        canvas.DrawCircle(LaserConvergencePoint, orbR, new Color(5.2f, 3.2f, 1.8f, 0.95f * _laserFocusWarmup));

        if (isPriming) return;

        // 3. Berkas laser lurus utama (Crimson-Coral Plasma)
        float mainWidth = VisualBeamWidth;
        float coreWidth = mainWidth * 0.48f;

        Color sheathColor = new Color(3.6f, 0.25f, 0.85f, (0.30f + 0.15f * pulse) * _laserFocusWarmup);
        canvas.DrawLine(LaserConvergencePoint, LaserImpactPoint, sheathColor, mainWidth + 2.5f, false);

        Color beamColor = new Color(4.8f, 1.2f, 0.45f, 0.90f * _laserFocusWarmup);
        canvas.DrawLine(LaserConvergencePoint, LaserImpactPoint, beamColor, mainWidth, false);

        Color coreColor = new Color(5.5f, 4.2f, 2.5f, 0.98f * _laserFocusWarmup);
        canvas.DrawLine(LaserConvergencePoint, LaserImpactPoint, coreColor, coreWidth, false);

        // Garis sub-beam bila mencapai Tier 2+ (15+ Miner)
        if (LaserBlockSpan >= 2)
        {
            Vector2 tangent = new Vector2(-LaserAimDir.Y, LaserAimDir.X);
            Color subLineCol = new Color(5.2f, 3.6f, 2.0f, 0.65f * _laserFocusWarmup);
            for (int b = 0; b < LaserBlockSpan; b++)
            {
                float offset = ((float)b / (LaserBlockSpan - 1) - 0.5f) * (mainWidth * 0.72f);
                canvas.DrawLine(LaserConvergencePoint + tangent * offset, LaserImpactPoint + tangent * offset, subLineCol, 1.4f, false);
            }
        }

        // Kotak pemanasan voxel di titik tumbukan (identik visual laser pemain)
        if (_laserActiveHitBoxes.Count > 0 && TargetAsteroid != null && !TargetAsteroid.IsEnergyAsteroid)
        {
            Color hotCrimson = new Color(3.4f, 0.65f, 0.25f, 0.90f);
            Color yellowIncandescent = new Color(5.2f, 4.2f, 1.6f, 1.0f);

            for (int i = 0; i < _laserActiveHitBoxes.Count; i++)
            {
                var box = _laserActiveHitBoxes[i];
                float h = Mathf.Clamp(box.heat, 0.0f, 1.0f);
                Color impactColor = hotCrimson.Lerp(yellowIncandescent, h);

                canvas.DrawSetTransform(box.worldCenter, _laserHitAsteroidRotation, Vector2.One);
                Rect2 targetBox = new Rect2(
                    new Vector2(-Asteroid.VoxelSize * 0.5f, -Asteroid.VoxelSize * 0.5f),
                    new Vector2(Asteroid.VoxelSize, Asteroid.VoxelSize)
                );
                canvas.DrawRect(targetBox, new Color(impactColor.R, impactColor.G, impactColor.B, 0.25f + h * 0.55f), true);
                canvas.DrawRect(targetBox, impactColor, false, 1.5f);
            }
            canvas.DrawSetTransform(Vector2.Zero, 0.0f, Vector2.One);
        }

        // Titik dampak pada batuan asteroid
        canvas.DrawCircle(LaserImpactPoint, coreWidth * 1.4f, coreColor);
        canvas.DrawCircle(LaserImpactPoint, mainWidth * 1.4f, sheathColor);
    }
}
