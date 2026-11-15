using System;
using System.Collections.Generic;
using GeometrySwarm.Core.ECS.Components;
using Godot;

namespace GeometrySwarm.Gameplay;

/// <summary>
/// Pengelola peta ruang angkasa luas (12.000 x 12.000 px), pembangun asteroid raksasa prosedural,
/// pusat kontrol 3 Portal Kosmik (Tri-Pillar Void Gates), & penyedia telemetri sumur gravitasi aktif.
/// - Menyebarkan 32 asteroid kolosal awal pada 3 sabuk kosmik.
/// - Saat jumlah asteroid batu di arena berkurang (< TargetAsteroidCount) akibat selesai dipanen,
///   salah satu Cosmic Rift akan melontarkan asteroid mineral baru ke arah sabuk luar.
/// - Bintang Aether bersifat kekal dan bereinkarnasi secara mandiri melalui siklus Aether Seed.
/// </summary>
public partial class AsteroidFieldManager : Node2D
{
    [Export] public Vector2 SectorSize { get; set; } = new(12000.0f, 12000.0f);
    [Export] public int TotalAsteroids { get; set; } = 40;

    public const float RiftOrbitRadius = 1550.0f;

    // Telemetri sumur gravitasi aktif (Bintang Mini Aether + Aether Seed Infall + Cosmic Rift) untuk arena.gd
    public Vector2[] GravityWellPositions { get; private set; } = Array.Empty<Vector2>();
    public float[] GravityWellRadii { get; private set; } = Array.Empty<float>();
    public float[] GravityWellStrengths { get; private set; } = Array.Empty<float>();
    public float[] GravityWellInfallRatios { get; private set; } = Array.Empty<float>();
    public float[] GravityWellPullAccels { get; private set; } = Array.Empty<float>();
    public float[] GravityWellSwirlAccels { get; private set; } = Array.Empty<float>();
    public static AsteroidFieldManager? Instance { get; private set; }
    public IReadOnlyList<CosmicRift> CosmicRifts => _cosmicRifts;

    public float[] GravityWellHeatRates { get; private set; } = Array.Empty<float>();

    private readonly List<Vector2> _gravPosList = new(16);
    private readonly List<float> _gravRadList = new(16);
    private readonly List<float> _gravStrList = new(16);
    private readonly List<float> _gravInfallList = new(16);
    private readonly List<float> _gravPullList = new(16);
    private readonly List<float> _gravSwirlList = new(16);
    private readonly List<float> _gravHeatList = new(16);

    private readonly List<CosmicRift> _cosmicRifts = new();

    private Rect2 _sectorBounds;
    private static readonly Color PerimeterColor = new(0.35f, 0.15f, 0.55f, 0.45f);
    private static readonly Color CornerMarkerColor = new(1.8f, 0.5f, 2.5f, 0.85f);

    public override void _Ready()
    {
        Instance = this;
        _sectorBounds = new Rect2(-SectorSize * 0.5f, SectorSize);
        SpawnCosmicRifts();
        SpawnColossalAsteroidField();
        QueueRedraw();
    }

    /// <summary>
    /// Membangun 3 Portal Kosmik (Tri-Pillar Void Gates) di Inner Belt yang berjarak 120 derajat.
    /// </summary>
    private void SpawnCosmicRifts()
    {
        _cosmicRifts.Clear();

        float[] baseAngles = new float[] { -0.155f, Mathf.Tau / 3.0f, (Mathf.Tau * 2.0f) / 3.0f };

        for (int i = 0; i < 3; i++)
        {
            var rift = new CosmicRift
            {
                Name = $"CosmicRift_{i}",
                RiftId = i,
                OrbitRadius = RiftOrbitRadius,
                OrbitAngle = baseAngles[i],
                OrbitSpeed = 0.012f
            };

            _cosmicRifts.Add(rift);
            AddChild(rift);
        }
    }

    private float _asteroidReplenishTimer = -30.0f; // Grace period 30 detik di awal permainan agar Black Hole tenang
    private const float ReplenishCheckInterval = 5.0f;
    private float _replenishCooldown = 0.0f;
    [Export] public int MinAsteroidThreshold { get; set; } = 28;

    public override void _PhysicsProcess(double delta)
    {
        UpdateGravityWellTelemetry();
        CheckAndReplenishAsteroids((float)delta);
    }

    /// <summary>
    /// Memantau populasi asteroid di sabuk secara berkala.
    /// Jika jumlah asteroid turun drastis di bawah ambang batas (histeresis <= 28),
    /// sistem memilih satu Black Hole menganggur untuk memicu siklus White Hole Genesis.
    /// </summary>
    private void CheckAndReplenishAsteroids(float dt)
    {
        if (_replenishCooldown > 0.0f)
        {
            _replenishCooldown -= dt;
        }

        _asteroidReplenishTimer += dt;
        if (_asteroidReplenishTimer < ReplenishCheckInterval)
            return;

        _asteroidReplenishTimer = 0.0f;

        // 1. Hitung jumlah asteroid aktif yang masih utuh di tree
        int activeCount = 0;
        var asteroids = Asteroid.ActiveAsteroids;
        for (int i = 0; i < asteroids.Count; i++)
        {
            var ast = asteroids[i];
            if (ast != null && IsInstanceValid(ast) && !ast.IsDestroyed && ast.IsActiveInTree)
            {
                activeCount++;
            }
        }

        // 2. Hanya picu replenish jika asteroid turun di bawah ambang batas DAN cooldown habis
        if (activeCount < MinAsteroidThreshold && _replenishCooldown <= 0.0f && _cosmicRifts.Count > 0)
        {
            // Pastikan tidak ada Black Hole yang sedang sibuk merakit (White Hole / Charging)
            for (int r = 0; r < _cosmicRifts.Count; r++)
            {
                var rift = _cosmicRifts[r];
                if (rift != null && IsInstanceValid(rift) && rift.State != CosmicRift.RiftState.Dormant)
                {
                    return; // Proses perakitan sudah berjalan di salah satu Rift
                }
            }

            // Kumpulkan Black Hole yang berstatus Dormant
            var idleRifts = new List<CosmicRift>();
            for (int r = 0; r < _cosmicRifts.Count; r++)
            {
                var rift = _cosmicRifts[r];
                if (rift != null && IsInstanceValid(rift) && rift.State == CosmicRift.RiftState.Dormant)
                {
                    idleRifts.Add(rift);
                }
            }

            if (idleRifts.Count > 0)
            {
                // Pilih satu Black Hole secara acak untuk spawn/merakit asteroid baru
                var rng = new RandomNumberGenerator();
                rng.Randomize();
                int chosenIndex = rng.RandiRange(0, idleRifts.Count - 1);
                idleRifts[chosenIndex].StartFullCinematicSequence();
                _replenishCooldown = 40.0f; // Beri jeda 40 detik antar siklus replenish agar tidak bertubi-tubi
            }
        }
    }

    private void UpdateGravityWellTelemetry()
    {
        _gravPosList.Clear();
        _gravRadList.Clear();
        _gravStrList.Clear();
        _gravInfallList.Clear();
        _gravPullList.Clear();
        _gravSwirlList.Clear();
        _gravHeatList.Clear();

        var asteroids = Asteroid.ActiveAsteroids;
        for (int i = 0; i < asteroids.Count; i++)
        {
            var ast = asteroids[i];
            if (ast == null || !IsInstanceValid(ast)) continue;

            if (ast.TryGetGravityWellParameters(
                out Vector2 pos,
                out float rad,
                out float str,
                out float infall,
                out float pullAccel,
                out float swirlAccel,
                out float heatRate))
            {
                _gravPosList.Add(pos);
                _gravRadList.Add(rad);
                _gravStrList.Add(str);
                _gravInfallList.Add(infall);
                _gravPullList.Add(pullAccel);
                _gravSwirlList.Add(swirlAccel);
                _gravHeatList.Add(heatRate);
            }
        }

        // 2. Gravitasi Cosmic Rift (Distorsi Ruang Melingkar pada Grid Latar Belakang & Bintang Parallax)
        for (int r = 0; r < _cosmicRifts.Count; r++)
        {
            var rift = _cosmicRifts[r];
            if (rift == null || !IsInstanceValid(rift)) continue;

            _gravPosList.Add(rift.GlobalPosition);
            _gravRadList.Add(520.0f);
            _gravStrList.Add(1.4f);
            _gravInfallList.Add(0.0f);
            _gravPullList.Add(140.0f);
            _gravSwirlList.Add(95.0f);
            _gravHeatList.Add(0.0f);
        }

        int count = _gravPosList.Count;
        if (GravityWellPositions.Length != count)
        {
            GravityWellPositions = new Vector2[count];
            GravityWellRadii = new float[count];
            GravityWellStrengths = new float[count];
            GravityWellInfallRatios = new float[count];
            GravityWellPullAccels = new float[count];
            GravityWellSwirlAccels = new float[count];
            GravityWellHeatRates = new float[count];
        }

        for (int i = 0; i < count; i++)
        {
            GravityWellPositions[i] = _gravPosList[i];
            GravityWellRadii[i] = _gravRadList[i];
            GravityWellStrengths[i] = _gravStrList[i];
            GravityWellInfallRatios[i] = _gravInfallList[i];
            GravityWellPullAccels[i] = _gravPullList[i];
            GravityWellSwirlAccels[i] = _gravSwirlList[i];
            GravityWellHeatRates[i] = _gravHeatList[i];
        }
    }

    public void SpawnColossalAsteroidField()
    {
        var rng = new RandomNumberGenerator();
        rng.Randomize();

        var placedAsteroids = new List<(Vector2 Pos, float EffRadius)>();

        const float InterAsteroidPadding = 220.0f;
        float halfSector = SectorSize.X * 0.5f;
        Vector2 playerSpawnPos = PlayerCore.Instance?.GlobalPosition ?? Vector2.Zero;

        bool IsLocationClear(Vector2 candPos, float effRadius, bool isEnergy)
        {
            // Jaga zona aman bebas asteroid di sekitar posisi kapal pemain saat spawn
            if (candPos.DistanceTo(playerSpawnPos) < effRadius + 420.0f)
            {
                return false;
            }

            // Jaga pusat semesta (Origin Safe Zone)
            if (candPos.Length() < 550.0f + effRadius)
            {
                return false;
            }

            float maxLimit = halfSector - (effRadius + 140.0f);
            if (MathF.Abs(candPos.X) > maxLimit || MathF.Abs(candPos.Y) > maxLimit)
            {
                return false;
            }

            // Jaga zona eksklusi luas di sekitar ketiga Cosmic Rift agar Rift tidak pernah tertutup/di belakang asteroid.
            // Bintang Aether secara naluriah menolak mendekati Black Hole: minimal jarak 2600 px!
            for (int r = 0; r < _cosmicRifts.Count; r++)
            {
                float distToRift = candPos.DistanceTo(_cosmicRifts[r].Position);
                float requiredDist = isEnergy ? 2600.0f : (effRadius + 560.0f);
                if (distToRift < requiredDist)
                {
                    return false;
                }
            }

            for (int p = 0; p < placedAsteroids.Count; p++)
            {
                float minDist = effRadius + placedAsteroids[p].EffRadius + InterAsteroidPadding;
                if (candPos.DistanceSquaredTo(placedAsteroids[p].Pos) < minDist * minDist)
                {
                    return false;
                }
            }

            return true;
        }

        // 1. Tempatkan objek showcase di dekat Cosmic Rift 0 TERLEBIH DAHULU (sebelum loop sabuk):
        //    - Disusun pada busur di luar horizon Rift 0.
        //    - Bintang Aether TIDAK PERNAH ditempatkan di sini karena Bintang Aether cenderung menghindari Black Hole!
        //    - Dilengkapi asteroid beruntun di belakangnya (pChain) agar saat shockwave mendorong p1/p2,
        //      asteroid langsung saling bertabrakan secara beruntun!
        if (_cosmicRifts.Count > 0)
        {
            Vector2 r0 = _cosmicRifts[0].Position;
            // Posisikan di luar zona erosi Black Hole (dist >= 850 px) agar tidak terkikis saat diam
            Vector2 p1 = r0 + new Vector2(780.0f, -460.0f);
            Vector2 p2 = r0 + new Vector2(620.0f, 680.0f);
            Vector2 pStar = r0 + new Vector2(-420.0f, -880.0f);
            Vector2 pChain1 = r0 + new Vector2(1380.0f, -540.0f);
            Vector2 pChain2 = r0 + new Vector2(1180.0f, 860.0f);

            placedAsteroids.Add((p1, 240.0f * 1.18f));
            placedAsteroids.Add((p2, 270.0f * 1.18f));
            placedAsteroids.Add((pStar, 105.0f * 1.18f));
            placedAsteroids.Add((pChain1, 310.0f * 1.18f));
            placedAsteroids.Add((pChain2, 330.0f * 1.18f));

            SpawnAsteroid(p1, 240.0f, SwarmRole.DartNeedle, false, rng);
            SpawnAsteroid(p2, 270.0f, SwarmRole.Guardian, false, rng);
            SpawnAsteroid(pStar, 105.0f, SwarmRole.DartNeedle, false, rng);
            SpawnAsteroid(pChain1, 310.0f, SwarmRole.DrillDrone, false, rng);
            SpawnAsteroid(pChain2, 330.0f, SwarmRole.CrescentBoomerang, false, rng);
        }

        var beltConfigs = new (int Count, float MinDist, float MaxDist, float MinRad, float MaxRad)[]
        {
            (6, 1950.0f, 2450.0f, 300.0f, 420.0f),  // Inner Belt: 6
            (17, 2600.0f, 4250.0f, 440.0f, 700.0f), // Mid Belt: 17
            (12, 4450.0f, 5500.0f, 680.0f, 1050.0f) // Outer Belt: 12
        };
        // Total awal: 5 showcase + 6 + 17 + 12 = 40 asteroid utuh saat game mulai!

        for (int beltIndex = 0; beltIndex < beltConfigs.Length; beltIndex++)
        {
            var belt = beltConfigs[beltIndex];
            for (int i = 0; i < belt.Count; i++)
            {
                SwarmRole role;
                bool isEnergy = false;
                if (beltIndex == 0)
                {
                    // Sabuk pertama terlalu dekat dengan horizon Black Hole (Rift ~1580 px),
                    // sehingga Bintang Aether tidak diizinkan spawn di sini.
                    role = PickRandomMineralRole(rng);
                    isEnergy = false;
                }
                else
                {
                    role = PickRandomType(rng, out isEnergy);
                }

                float baseRadius = rng.RandfRange(belt.MinRad, belt.MaxRad);
                float radius = baseRadius;

                float effFactor = isEnergy ? 1.28f : 1.18f;
                float effRadius = radius * effFactor;

                Vector2 chosenPos = Vector2.Zero;
                bool foundSpot = false;

                for (int attempt = 0; attempt < 80; attempt++)
                {
                    float angleBase = ((float)i / belt.Count * Mathf.Tau) + rng.RandfRange(-0.35f, 0.35f);
                    float dist = rng.RandfRange(belt.MinDist, belt.MaxDist);
                    Vector2 candidatePos = new Vector2(MathF.Cos(angleBase), MathF.Sin(angleBase)) * dist;

                    if (attempt > 40)
                    {
                        radius = MathF.Max(belt.MinRad * 0.85f, radius * 0.96f);
                        effRadius = radius * effFactor;
                    }

                    if (IsLocationClear(candidatePos, effRadius, isEnergy))
                    {
                        chosenPos = candidatePos;
                        foundSpot = true;
                        break;
                    }
                }

                if (!foundSpot)
                {
                    for (int fallback = 0; fallback < 60; fallback++)
                    {
                        float randAngle = rng.Randf() * Mathf.Tau;
                        float randDist = rng.RandfRange(belt.MinDist * 0.9f, belt.MaxDist * 1.05f);
                        Vector2 candidatePos = new Vector2(MathF.Cos(randAngle), MathF.Sin(randAngle)) * randDist;
                        radius = belt.MinRad * 0.8f;
                        effRadius = radius * effFactor;

                        if (IsLocationClear(candidatePos, effRadius, isEnergy))
                        {
                            chosenPos = candidatePos;
                            foundSpot = true;
                            break;
                        }
                    }
                }

                if (!foundSpot)
                {
                    for (int em = 0; em < 120; em++)
                    {
                        float randAngle = rng.Randf() * Mathf.Tau;
                        float randDist = rng.RandfRange(belt.MinDist * 0.85f, belt.MaxDist * 1.1f);
                        Vector2 candidatePos = new Vector2(MathF.Cos(randAngle), MathF.Sin(randAngle)) * randDist;
                        radius = belt.MinRad * 0.70f;
                        effRadius = radius * 1.08f;

                        if (IsLocationClear(candidatePos, effRadius, isEnergy))
                        {
                            chosenPos = candidatePos;
                            foundSpot = true;
                            break;
                        }
                    }
                }

                if (foundSpot)
                {
                    placedAsteroids.Add((chosenPos, effRadius));
                    SpawnAsteroid(chosenPos, radius, role, isEnergy, rng);
                }
            }
        }

        TotalAsteroids = placedAsteroids.Count;
        // Ambang batas histeresis: hanya spawn jika populasi asteroid turun drastis (<= 28 dari 40)
        MinAsteroidThreshold = Math.Max(20, TotalAsteroids - 12);
    }

    /// <summary>
    /// Mencari koordinat acak yang sepenuhnya aman di dalam sabuk asteroid:
    /// 1. Bebas dari Black Hole / Cosmic Rift (jarak >= 2000 px).
    /// 2. Bebas dari Bintang Aether (jarak >= 1800 px).
    /// 3. Bebas dari tubuh asteroid (jarak >= OuterBoundRadius + 380 px, tidak pernah di dalam asteroid).
    /// 4. Berjauhan dari target hindar (misal Player vs Rival Hive, jarak >= minAvoidDistance).
    /// </summary>
    public Vector2 FindSafeRandomSpawnPoint(RandomNumberGenerator rng, Vector2? avoidPos = null, float minAvoidDistance = 4200.0f)
    {
        float minRadius = 2400.0f;
        float maxRadius = 4500.0f;
        float halfSector = SectorSize.X * 0.5f;

        for (int attempt = 0; attempt < 250; attempt++)
        {
            float angle;
            if (avoidPos.HasValue && avoidPos.Value.LengthSquared() > 10.0f)
            {
                // Musuh otomatis ditempatkan di sisi galaksi yang berseberangan dengan pemain
                float oppAngle = avoidPos.Value.Angle() + Mathf.Pi;
                angle = oppAngle + rng.RandfRange(-0.95f, 0.95f);
            }
            else
            {
                angle = rng.Randf() * Mathf.Tau;
            }

            float dist = rng.RandfRange(minRadius, maxRadius);
            Vector2 cand = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * dist;

            // 1. Batas Sektor
            if (MathF.Abs(cand.X) > halfSector - 600.0f || MathF.Abs(cand.Y) > halfSector - 600.0f)
                continue;

            // 2. Jarak ke target hindar (Player <-> Enemy)
            if (avoidPos.HasValue)
            {
                float distToAvoid = cand.DistanceTo(avoidPos.Value);
                if (distToAvoid < minAvoidDistance)
                    continue;
            }

            // 3. Jarak ke semua Black Hole (Cosmic Rift)
            bool tooCloseToRift = false;
            for (int r = 0; r < _cosmicRifts.Count; r++)
            {
                var rift = _cosmicRifts[r];
                if (!IsInstanceValid(rift)) continue;
                if (cand.DistanceTo(rift.Position) < 2000.0f)
                {
                    tooCloseToRift = true;
                    break;
                }
            }
            if (tooCloseToRift) continue;

            // 4. Jarak ke Bintang Aether dan Tubuh Asteroid
            bool insideOrNearAsteroid = false;

            for (int a = 0; a < Asteroid.ActiveAsteroids.Count; a++)
            {
                var ast = Asteroid.ActiveAsteroids[a];
                if (!IsInstanceValid(ast) || ast.IsDestroyed) continue;

                float distToAst = cand.DistanceTo(ast.GlobalPosition);

                // Jika ini Bintang Aether, jaga zona aman ekstra luas (minimal 1800 px)
                if (ast.IsEnergyAsteroid)
                {
                    if (distToAst < 1800.0f)
                    {
                        insideOrNearAsteroid = true;
                        break;
                    }
                }

                // Cek clearance fisik: tidak boleh di dalam atau bersentuhan dengan asteroid
                float clearance = distToAst - (ast.OuterBoundRadius + 380.0f);
                if (clearance < 0.0f)
                {
                    insideOrNearAsteroid = true;
                    break;
                }
            }

            if (insideOrNearAsteroid) continue;

            // Lokasi sempurna ditemukan!
            return cand;
        }

        // Fallback jika kondisi sangat padat (relaksasi jarak secara proporsional)
        for (int fallback = 0; fallback < 120; fallback++)
        {
            float angle = avoidPos.HasValue
                ? avoidPos.Value.Angle() + Mathf.Pi + rng.RandfRange(-1.2f, 1.2f)
                : rng.Randf() * Mathf.Tau;
            float dist = rng.RandfRange(2600.0f, 4200.0f);
            Vector2 cand = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * dist;

            bool blocked = false;
            for (int r = 0; r < _cosmicRifts.Count; r++)
            {
                if (cand.DistanceTo(_cosmicRifts[r].Position) < 1600.0f)
                {
                    blocked = true;
                    break;
                }
            }
            if (blocked) continue;

            for (int a = 0; a < Asteroid.ActiveAsteroids.Count; a++)
            {
                var ast = Asteroid.ActiveAsteroids[a];
                if (!IsInstanceValid(ast) || ast.IsDestroyed) continue;
                if (cand.DistanceTo(ast.GlobalPosition) < ast.OuterBoundRadius + 240.0f)
                {
                    blocked = true;
                    break;
                }
            }
            if (blocked) continue;

            return cand;
        }

        return new Vector2(3200.0f, 0.0f);
    }

    public Asteroid SpawnEmptyAsteroidForWhiteHoleAssembly(Vector2 assemblyWorldPos, float radius = 320.0f)
    {
        var rng = new RandomNumberGenerator();
        rng.Randomize();
        SwarmRole role = PickRandomMineralRole(rng);

        var asteroid = new Asteroid
        {
            Position = assemblyWorldPos,
            GlobalPosition = assemblyWorldPos,
            Radius = radius,
            MaxHealth = radius * 5.0f,
            AsteroidType = role,
            IsEnergyAsteroid = false,
            StartEmptyForWhiteHoleAssembly = true
        };

        AddChild(asteroid);
        asteroid.GlobalPosition = assemblyWorldPos;
        return asteroid;
    }

    public void TriggerRandomWhiteHoleAsteroidGenesis()
    {
        var idleRifts = new List<CosmicRift>();
        for (int i = 0; i < _cosmicRifts.Count; i++)
        {
            var r = _cosmicRifts[i];
            if (r != null && IsInstanceValid(r) && r.State == CosmicRift.RiftState.Dormant)
            {
                idleRifts.Add(r);
            }
        }
        if (idleRifts.Count > 0)
        {
            var rng = new RandomNumberGenerator();
            rng.Randomize();
            int idx = rng.RandiRange(0, idleRifts.Count - 1);
            idleRifts[idx].StartFullCinematicSequence();
        }
    }

    public void TriggerDebugWhiteHoleAsteroidGenesis() => TriggerRandomWhiteHoleAsteroidGenesis();

    private void SpawnAsteroid(Vector2 pos, float radius, SwarmRole role, bool isEnergy, RandomNumberGenerator rng)
    {
        float effectiveRadius = isEnergy ? rng.RandfRange(85.0f, 115.0f) : radius;
        var asteroid = new Asteroid
        {
            Position = pos,
            GlobalPosition = pos,
            Radius = effectiveRadius,
            MaxHealth = effectiveRadius * 5.0f,
            AsteroidType = role,
            IsEnergyAsteroid = isEnergy
        };

        float driftAngle = rng.Randf() * Mathf.Tau;
        float driftSpeed = rng.RandfRange(4.0f, 16.0f);
        Vector2 driftVel = new Vector2(MathF.Cos(driftAngle), MathF.Sin(driftAngle)) * driftSpeed;
        float angVel = rng.RandfRange(-0.08f, 0.08f);

        AddChild(asteroid);
        asteroid.GlobalPosition = pos;
        asteroid.Sleeping = false;
        asteroid.LinearVelocity = driftVel;
        asteroid.AngularVelocity = angVel;
    }

    private static SwarmRole PickRandomMineralRole(RandomNumberGenerator rng)
    {
        int roll = rng.RandiRange(0, 4);
        return roll switch
        {
            0 => SwarmRole.DartNeedle,
            1 => SwarmRole.GigaShuriken,
            2 => SwarmRole.DrillDrone,
            3 => SwarmRole.CrescentBoomerang,
            _ => SwarmRole.Guardian
        };
    }

    private static SwarmRole PickRandomType(RandomNumberGenerator rng, out bool isEnergy)
    {
        if (rng.Randf() < 0.20f)
        {
            isEnergy = true;
            return SwarmRole.DartNeedle;
        }

        isEnergy = false;
        return PickRandomMineralRole(rng);
    }

    public override void _Draw()
    {
        DrawRect(_sectorBounds, PerimeterColor, false, 4.0f);

        float cornerLength = 260.0f;
        Vector2 tl = _sectorBounds.Position;
        Vector2 tr = new(_sectorBounds.End.X, _sectorBounds.Position.Y);
        Vector2 bl = new(_sectorBounds.Position.X, _sectorBounds.End.Y);
        Vector2 br = _sectorBounds.End;

        DrawLine(tl, tl + Vector2.Right * cornerLength, CornerMarkerColor, 5.0f);
        DrawLine(tl, tl + Vector2.Down * cornerLength, CornerMarkerColor, 5.0f);
        DrawLine(tr, tr + Vector2.Left * cornerLength, CornerMarkerColor, 5.0f);
        DrawLine(tr, tr + Vector2.Down * cornerLength, CornerMarkerColor, 5.0f);
        DrawLine(bl, bl + Vector2.Right * cornerLength, CornerMarkerColor, 5.0f);
        DrawLine(bl, bl + Vector2.Up * cornerLength, CornerMarkerColor, 5.0f);
        DrawLine(br, br + Vector2.Left * cornerLength, CornerMarkerColor, 5.0f);
        DrawLine(br, br + Vector2.Up * cornerLength, CornerMarkerColor, 5.0f);
    }
}
