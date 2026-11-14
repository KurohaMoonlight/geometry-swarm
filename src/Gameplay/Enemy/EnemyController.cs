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
    FleeingPod
}

/// <summary>
/// AI Controller untuk Rival Hive Core.
/// - Beroperasi sebagai Clone Pemain murni: memanfaatkan fitur yang sudah ada di game tanpa trik buatan.
/// - Early Game: Fokus ekonomi & penambangan mineral secara efisien (membidik kantung mineral, bukan mengikis batu kosong).
/// - Kemudi Luwes & Bebas Patah-Patah:
///   1. Menggunakan akselerasi inersia halus berbasis Core.Acceleration.
///   2. Interpolasi target standoff (tidak melompat mendadak saat rotasi batu).
///   3. Histeresis transisi status (55 px untuk masuk mode menambang, 140 px untuk keluar).
///   4. Jarak parkir tembak di mulut terowongan (Radius + 75 px) sehingga puing kristal langsung tersedot magnet Core.
/// - Laser Identik Pemain: 1 berkas lurus tunggal terarah lurus ke deposit mineral,
///   membuat terowongan tembus ke urat bijih, memanen kepingan kristal, lalu berpindah ke deposit berikutnya.
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

    // Inventori 5 Material Kosmik (Ferrite, Quartz, Carbide, Vortexum, Aether)
    public int[] ResourceStock { get; } = new int[5] { 22, 18, 12, 0, 4 };

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
    private readonly List<Vector2> _activeMinerPositions = new();

    // Pencegah Tersangkut (Anti-Stuck Watchdog)
    private float _stuckTimer = 0.0f;
    private float _findVeinTimer = 0.0f;

    // Timer Pabrik Otomatis
    private float _factoryTimer = 0.0f;
    private const float FactoryInterval = 2.0f;

    public void Initialize(SwarmSimulation simulation, EnemyCore core)
    {
        Simulation = simulation;
        Core = core;
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

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        if (Core == null || !IsInstanceValid(Core)) return;

        // Jika dalam mode Nemesis Escape Pod, serahkan kendali ke manuver kabur darurat bawaan Core
        if (Core.IsEscapePod)
        {
            State = EnemyState.FleeingPod;
            if (IsMiningLaserActive)
            {
                IsMiningLaserActive = false;
                Simulation.QueueRedraw();
            }
            return;
        }

        _factoryTimer += dt;
        _findVeinTimer += dt;

        // 1. Validasi Asteroid Target:
        // Jika asteroid hilang, hancur, atau seluruh mineralnya sudah habis terpanen, cari asteroid baru
        if (TargetAsteroid == null || !TargetAsteroid.IsActiveInTree || TargetAsteroid.RemainingResourceVoxelCount <= 0)
        {
            TargetAsteroid = null;
            FindBestAsteroidWithResources();
        }

        // 2. Siklus Perilaku Gerak Luwes & Penambangan Efisien
        UpdateBehavior(dt);

        // 3. Produksi Pabrik Otomatis (Memanfaatkan Resep Standar Foundry)
        if (_factoryTimer >= FactoryInterval)
        {
            _factoryTimer = 0.0f;
            ProcessAutonomousFoundry();
        }
    }

    /// <summary>
    /// Mencari asteroid terdekat yang masih memiliki kandungan deposit mineral berharga.
    /// Tidak membuang waktu menargetkan asteroid yang mineralnya sudah kosong melompong.
    /// </summary>
    private void FindBestAsteroidWithResources()
    {
        var asteroids = Asteroid.ActiveAsteroids;
        Asteroid? best = null;
        float bestDist = float.MaxValue;
        Vector2 corePos = Core.GlobalPosition;

        for (int i = 0; i < asteroids.Count; i++)
        {
            var ast = asteroids[i];
            if (ast == null || !ast.IsActiveInTree || ast.RemainingResourceVoxelCount <= 0) continue;
            if (ast.IsEnergyAsteroid && ast.IsInSeedCycle) continue;

            float dist = corePos.DistanceTo(ast.GlobalPosition);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = ast;
            }
        }

        TargetAsteroid = best;
        _stuckTimer = 0.0f;
        _findVeinTimer = 0.0f;
        StandoffPosition = Vector2.Zero;

        if (TargetAsteroid != null)
        {
            UpdateTargetVeinPosition();
        }
    }

    /// <summary>
    /// Memindai koordinat dunia dari voxel mineral terdekat di dalam asteroid target.
    /// </summary>
    private bool UpdateTargetVeinPosition()
    {
        if (TargetAsteroid == null || !TargetAsteroid.IsActiveInTree || TargetAsteroid.RemainingResourceVoxelCount <= 0)
        {
            return false;
        }

        if (TargetAsteroid.FindClosestResourceVoxel(Core.GlobalPosition, out Vector2 resPos, out _))
        {
            TargetResourceWorldPos = resPos;
            return true;
        }

        return false;
    }

    private void UpdateBehavior(float dt)
    {
        if (TargetAsteroid == null || !TargetAsteroid.IsActiveInTree || TargetAsteroid.RemainingResourceVoxelCount <= 0)
        {
            State = EnemyState.SearchingAsteroid;
            if (IsMiningLaserActive)
            {
                IsMiningLaserActive = false;
                Simulation.QueueRedraw();
            }
            return;
        }

        // Perbarui lokasi kantung mineral secara berkala jika sudah terpanen
        if (_findVeinTimer >= 0.4f)
        {
            _findVeinTimer = 0.0f;
            if (!UpdateTargetVeinPosition())
            {
                // Mineral di asteroid ini sudah habis total! Cari asteroid lain.
                TargetAsteroid = null;
                FindBestAsteroidWithResources();
                return;
            }
        }

        Vector2 corePos = Core.GlobalPosition;
        Vector2 astPos = TargetAsteroid.GlobalPosition;

        // Hitung arah keluar dari pusat asteroid ke deposit mineral
        Vector2 fromCenterToRes = TargetResourceWorldPos - astPos;
        Vector2 outwardDir = fromCenterToRes.LengthSquared() > 1.0f 
            ? fromCenterToRes.Normalized() 
            : (corePos - astPos).Normalized();

        // Titik parkir tembak aman (Standoff) di mulut terowongan luar batuan (Radius + 75 px)
        // Posisi dekat ini menjamin serpihan kristal yang meluncur keluar langsung tersedot magnet Core
        float standoffDist = TargetAsteroid.Radius + 75.0f;
        Vector2 targetStandoff = astPos + outwardDir * standoffDist;

        // Interpolasi halus titik standoff agar kemudi bergerak luwes tanpa sentakan patah-patah
        if (StandoffPosition == Vector2.Zero || StandoffPosition.DistanceTo(targetStandoff) > 600.0f)
        {
            StandoffPosition = targetStandoff;
        }
        else
        {
            StandoffPosition = StandoffPosition.Lerp(targetStandoff, MathF.Min(1.0f, 3.8f * dt));
        }

        float distToStandoff = corePos.DistanceTo(StandoffPosition);

        // HISTERESIS BERSIH:
        // Masuk mode penambangan jika jarak < 55 px
        // Tetap menambang dan hanya keluar kembali ke traveling jika jarak terdorong > 140 px
        bool shouldMine = (State == EnemyState.MiningResource)
            ? (distToStandoff <= 140.0f)
            : (distToStandoff <= 55.0f);

        if (shouldMine)
        {
            State = EnemyState.MiningResource;

            // Pengereman halus (Arrival Steering): melayang stabil di mulut terowongan
            Vector2 toStandoff = StandoffPosition - corePos;
            Core.Velocity = Core.Velocity.Lerp(toStandoff * 2.8f, MathF.Min(1.0f, 6.0f * dt));
            Core.Velocity = Core.Velocity.LimitLength(35.0f);

            // Operasikan laser penambangan presisi 1 berkas lurus identik pemain
            UpdateMiningLaser(dt);
        }
        else
        {
            State = EnemyState.TravelingToStandoff;
            if (IsMiningLaserActive)
            {
                IsMiningLaserActive = false;
                Simulation.QueueRedraw();
            }

            // Kemudikan kapal induk dengan akselerasi inersia alami menggunakan Core.Acceleration
            Vector2 toStandoff = StandoffPosition - corePos;
            Vector2 steerDir = toStandoff.LengthSquared() > 1.0f ? toStandoff.Normalized() : Vector2.Zero;
            Core.Velocity += steerDir * (Core.Acceleration * 0.85f * dt);

            // Buffer penolak dinding agar tidak terbentur batuan saat berbelok di keliling asteroid
            float distToAstCenter = corePos.DistanceTo(astPos);
            float minSafeDist = TargetAsteroid.Radius + 45.0f;
            if (distToAstCenter < minSafeDist)
            {
                Vector2 pushAway = (corePos - astPos).Normalized();
                Core.Velocity += pushAway * (Core.Acceleration * 1.25f * dt);
            }
        }

        // Stuck Watchdog: Jika dalam 3.5 detik tidak ada serpihan mineral yang hancur, perbarui kantung target
        _stuckTimer += dt;
        if (_stuckTimer >= 3.5f)
        {
            _stuckTimer = 0.0f;
            UpdateTargetVeinPosition();
        }
    }

    /// <summary>
    /// Menembakkan Heavy Mining Laser presisi (1 berkas lurus tunggal) tepat ke arah deposit mineral.
    /// Aturan, lebar, damage per detik, dan durasi priming identik 100% dengan pemain.
    /// </summary>
    private void UpdateMiningLaser(float dt)
    {
        _activeMinerPositions.Clear();
        Vector2 corePos = Core.GlobalPosition;

        // Ambil posisi miner musuh dari simulasi
        Simulation.CollectEnemyMinerPositions(_activeMinerPositions);
        int minerCount = _activeMinerPositions.Count;

        if (minerCount == 0 || TargetAsteroid == null || !TargetAsteroid.IsActiveInTree)
        {
            if (IsMiningLaserActive)
            {
                IsMiningLaserActive = false;
                Simulation.QueueRedraw();
            }
            _laserFocusWarmup = 0.0f;
            return;
        }

        IsMiningLaserActive = true;
        Simulation.QueueRedraw();

        _laserPrimingTimer += dt;
        bool isPriming = _laserPrimingTimer < LaserPrimingDuration;
        float primingProgress = Mathf.Clamp(_laserPrimingTimer / LaserPrimingDuration, 0.0f, 1.0f);

        _laserFocusWarmup = isPriming 
            ? primingProgress * 0.42f 
            : MathF.Min(1.0f, _laserFocusWarmup + dt * 6.0f);

        _laserPulseTimer += dt * (1.0f + minerCount * 0.14f);

        // Arah bidik lurus langsung dari Core ke koordinat deposit mineral sasaran dengan interpolasi halus
        Vector2 toTarget = TargetResourceWorldPos - corePos;
        Vector2 desiredAimDir = toTarget.LengthSquared() > 1.0f ? toTarget.Normalized() : Vector2.Right;
        LaserAimDir = LaserAimDir.Lerp(desiredAimDir, MathF.Min(1.0f, 16.0f * dt)).Normalized();

        // Lebar berkas laser mengikuti rumus resmi pemain: 1 berkas selama miner < 15
        LaserBlockSpan = Math.Clamp(1 + (minerCount / 15), 1, 5);
        float targetWidth = LaserBlockSpan switch
        {
            1 => 3.5f,
            2 => 16.0f,
            3 => 28.0f,
            _ => 42.0f
        };
        VisualBeamWidth = isPriming ? 1.4f : Mathf.Lerp(1.5f, targetWidth, _laserFocusWarmup);

        Vector2 rayOrigin = corePos + LaserAimDir * 14.0f;
        LaserConvergencePoint = corePos + LaserAimDir * 28.0f;
        float maxRange = 1600.0f;

        // Raycast lurus tunggal: Menembus kerak batu terluar di garis bidik (tunneling) menuju mineral
        if (TargetAsteroid.RaycastVoxel(rayOrigin, LaserAimDir, maxRange, out Asteroid.VoxelRayHit vHit) && vHit.Hit)
        {
            LaserImpactPoint = vHit.WorldHitPoint;

            if (!isPriming)
            {
                // Kecepatan erosi identik pemain: (2.8 + miner * 0.95) * dt
                float erosionRate = (2.8f + minerCount * 0.95f) * dt;
                bool destroyed = TargetAsteroid.ApplyLaserHeatAtVoxel(
                    vHit.GridX,
                    vHit.GridY,
                    erosionRate,
                    LaserAimDir,
                    out Asteroid.VoxelType brokenType,
                    out Vector2 brokenWorldCenter,
                    out _
                );

                if (destroyed)
                {
                    _stuckTimer = 0.0f; // Reset watchdog karena ada kemajuan penggalian
                    Simulation.SpawnVoxelErosionDebris(brokenWorldCenter, -LaserAimDir, brokenType);

                    // Jika voxel yang hancur adalah mineral, perbarui segera koordinat deposit berikutnya
                    if (Asteroid.TryGetResourceType(brokenType, out _))
                    {
                        UpdateTargetVeinPosition();
                    }
                }
            }
        }
        else
        {
            LaserImpactPoint = TargetResourceWorldPos;
        }

        // Jika seluruh mineral di asteroid ini sudah habis terkuras, langsung sudahi dan cari asteroid lain
        if (TargetAsteroid.RemainingResourceVoxelCount <= 0)
        {
            TargetAsteroid = null;
            IsMiningLaserActive = false;
            Simulation.QueueRedraw();
            FindBestAsteroidWithResources();
        }
    }

    /// <summary>
    /// Logika antrian pabrik otomatis (Memanfaatkan resep resmi Foundry):
    /// - Prioritas 1: Tambah Miner (+2) sampai 6 miner agar penambangan cepat.
    /// - Prioritas 2: Tambah Guardian (+5) sampai 12 unit untuk mengisi perisai 12 sektor.
    /// - Prioritas 3: Daur ulang kelebihan Ferrite menjadi Dart Needle (+10).
    /// </summary>
    private void ProcessAutonomousFoundry()
    {
        // 1. Prioritas Miner (+2): 10 Ferrite, 8 Quartz, 2 Aether
        if (MinerCount < 6 && ResourceStock[0] >= 10 && ResourceStock[1] >= 8 && ResourceStock[4] >= 2)
        {
            ResourceStock[0] -= 10;
            ResourceStock[1] -= 8;
            ResourceStock[4] -= 2;
            Simulation.AddEnemySwarm(SwarmRole.MiningDroid, 2);
            return;
        }

        // 2. Prioritas Guardian (+5): 12 Ferrite, 12 Quartz, 8 Carbide
        if (GuardianCount < 12 && ResourceStock[0] >= 12 && ResourceStock[1] >= 12 && ResourceStock[2] >= 8)
        {
            ResourceStock[0] -= 12;
            ResourceStock[1] -= 12;
            ResourceStock[2] -= 8;
            Simulation.AddEnemySwarm(SwarmRole.Guardian, 5);
            return;
        }

        // 3. Prioritas Dart Needle (+10): 10 Ferrite (Jika Ferrite melimpah)
        if (ResourceStock[0] >= 25)
        {
            ResourceStock[0] -= 10;
            Simulation.AddEnemySwarm(SwarmRole.DartNeedle, 10);
        }
    }

    /// <summary>
    /// Menggambar laser penambangan musuh (1 berkas lurus tunggal) dalam spektrum crimson-coral neon.
    /// </summary>
    public void DrawLaser(CanvasItem canvas)
    {
        if (!IsMiningLaserActive || _activeMinerPositions.Count == 0) return;

        float pulse = 0.5f + 0.5f * MathF.Sin(_laserPulseTimer * 26.0f);
        Color feederColor = new Color(3.8f, 1.4f, 0.35f, 0.45f * _laserFocusWarmup);

        // 1. Feeder beams dari setiap miner musuh ke titik fokus laser
        for (int i = 0; i < _activeMinerPositions.Count; i++)
        {
            canvas.DrawLine(_activeMinerPositions[i], LaserConvergencePoint, feederColor, 1.2f, false);
        }

        // 2. Bola energi fokus di titik konvergensi
        float orbR = Mathf.Lerp(2.0f, 5.5f, _laserFocusWarmup);
        canvas.DrawCircle(LaserConvergencePoint, orbR * 1.5f, new Color(4.2f, 0.4f, 1.2f, 0.35f * _laserFocusWarmup));
        canvas.DrawCircle(LaserConvergencePoint, orbR, new Color(5.2f, 3.2f, 1.8f, 0.95f * _laserFocusWarmup));

        // 3. Berkas laser lurus utama (Crimson-Coral Plasma)
        float mainWidth = VisualBeamWidth;
        float coreWidth = mainWidth * 0.48f;

        // Selubung aura merah-magenta
        Color sheathColor = new Color(3.6f, 0.25f, 0.85f, (0.30f + 0.15f * pulse) * _laserFocusWarmup);
        canvas.DrawLine(LaserConvergencePoint, LaserImpactPoint, sheathColor, mainWidth + 2.5f, false);

        // Batang laser tengah (Crimson-Coral Pijar)
        Color beamColor = new Color(4.8f, 1.2f, 0.45f, 0.90f * _laserFocusWarmup);
        canvas.DrawLine(LaserConvergencePoint, LaserImpactPoint, beamColor, mainWidth, false);

        // Inti putih-emas terang
        Color coreColor = new Color(5.5f, 4.2f, 2.5f, 0.98f * _laserFocusWarmup);
        canvas.DrawLine(LaserConvergencePoint, LaserImpactPoint, coreColor, coreWidth, false);

        // Titik dampak pada batuan asteroid
        canvas.DrawCircle(LaserImpactPoint, coreWidth * 1.4f, coreColor);
        canvas.DrawCircle(LaserImpactPoint, mainWidth * 1.6f, sheathColor);
    }
}
