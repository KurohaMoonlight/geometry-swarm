using System;
using System.Collections.Generic;
using GeometrySwarm.Core.ECS.Components;
using Godot;

namespace GeometrySwarm.Gameplay;

/// <summary>
/// Pengelola peta ruang angkasa luas (12.000 x 12.000 px), pembangun asteroid raksasa prosedural,
/// & pusat kontrol 3 Portal Kosmik (Tri-Pillar Void Gates) di Inner Belt (R ~ 1.550 px).
/// - Menyebarkan 32 asteroid kolosal awal pada 3 sabuk kosmik.
/// - Menghadirkan Infinite Asteroid Engine alami: Saat jumlah asteroid di arena berkurang (< TargetAsteroidCount),
///   salah satu Cosmic Rift akan mengalami instabilitas gravitasi dan melontarkan asteroid baru
///   ke arah sabuk luar dengan lintasan spiral Archimedes (centrifugal drift).
/// </summary>
public partial class AsteroidFieldManager : Node2D
{
    [Signal]
    public delegate void CosmicRiftInfallEventHandler(Vector2 riftPos, float power, float radius);

    [Signal]
    public delegate void CosmicRiftEruptedEventHandler(Vector2 riftPos, float power, float radius);

    [Export] public Vector2 SectorSize { get; set; } = new(12000.0f, 12000.0f);
    [Export] public int TotalAsteroids { get; set; } = 32;
    [Export] public int TargetAsteroidCount { get; set; } = 32;
    [Export] public float RiftCheckIntervalMin { get; set; } = 42.0f;
    [Export] public float RiftCheckIntervalMax { get; set; } = 58.0f;

    public const float RiftOrbitRadius = 1550.0f;

    private readonly List<CosmicRift> _cosmicRifts = new();
    private float _riftEjectionTimer = 35.0f;
    private int _nextRiftIndex = 0;

    private Rect2 _sectorBounds;
    private static readonly Color PerimeterColor = new(0.35f, 0.15f, 0.55f, 0.45f);
    private static readonly Color CornerMarkerColor = new(1.8f, 0.5f, 2.5f, 0.85f);

    public override void _Ready()
    {
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

        float[] baseAngles = new float[] { 0.0f, Mathf.Tau / 3.0f, (Mathf.Tau * 2.0f) / 3.0f };

        for (int i = 0; i < 3; i++)
        {
            var rift = new CosmicRift
            {
                Name = $"CosmicRift_{i}",
                RiftId = i,
                OrbitRadius = RiftOrbitRadius,
                OrbitAngle = baseAngles[i],
                OrbitSpeed = 0.012f // Presesi rotasi orbit lembut
            };

            rift.RiftInfallStarted += (pos, power, radius) =>
            {
                EmitSignal(SignalName.CosmicRiftInfall, pos, power, radius);
            };

            rift.RiftErupted += (pos, power, radius) =>
            {
                EmitSignal(SignalName.CosmicRiftErupted, pos, power, radius);
            };

            rift.RiftSpawnAsteroidRequested += OnRiftSpawnAsteroid;

            _cosmicRifts.Add(rift);
            AddChild(rift);
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        _riftEjectionTimer -= dt;

        if (_riftEjectionTimer <= 0.0f && _cosmicRifts.Count > 0)
        {
            // Cek kondisi populasi asteroid di seluruh arena
            int currentAsteroidCount = Asteroid.ActiveAsteroids.Count;
            if (currentAsteroidCount < TargetAsteroidCount)
            {
                var rift = _cosmicRifts[_nextRiftIndex];
                _nextRiftIndex = (_nextRiftIndex + 1) % _cosmicRifts.Count;

                if (rift.TriggerEjectionSequence())
                {
                    _riftEjectionTimer = (float)GD.RandRange(RiftCheckIntervalMin, RiftCheckIntervalMax);
                }
                else
                {
                    _riftEjectionTimer = 5.0f;
                }
            }
            else
            {
                // Jika asteroid masih penuh, tunda cek selama 14 detik
                _riftEjectionTimer = 14.0f;
            }
        }
    }

    private void OnRiftSpawnAsteroid(Vector2 spawnPos, Vector2 ejectVelocity)
    {
        var rng = new RandomNumberGenerator();
        rng.Randomize();

        // 70% Asteroid Tambang Biasa, 30% Asteroid Energy
        SwarmRole role = PickRandomType(rng, out bool isEnergy);
        float radius = isEnergy ? (float)GD.RandRange(85.0, 115.0) : (float)GD.RandRange(350.0, 520.0);

        var asteroid = new Asteroid
        {
            GlobalPosition = spawnPos,
            Radius = radius,
            MaxHealth = radius * 5.0f,
            AsteroidType = role,
            IsEnergyAsteroid = isEnergy
        };

        // Mulai dengan kecepatan lontaran spiral Archimedes,
        // yang akan diredam secara bertahap oleh Asteroid._PhysicsProcess menuju kecepatan normal
        asteroid.LinearVelocity = ejectVelocity;
        asteroid.AngularVelocity = (float)GD.RandRange(-0.12, 0.12);

        AddChild(asteroid);
    }

    public void SpawnColossalAsteroidField()
    {
        var rng = new RandomNumberGenerator();
        rng.Randomize();

        // Daftar posisi asteroid yang sudah berhasil ditempatkan beserta radius efektifnya
        var placedAsteroids = new List<(Vector2 Pos, float EffRadius)>();

        // Jarak bebas minimum antar tepi asteroid (margin lorong manuver pemain dan swarm)
        const float InterAsteroidPadding = 260.0f;
        const float PlayerSafeZoneRadius = 1100.0f;
        float halfSector = SectorSize.X * 0.5f;

        // Fungsi pembantu untuk memvalidasi posisi tanpa tumpang tindih
        bool IsLocationClear(Vector2 candPos, float effRadius)
        {
            // 1. Cek zona aman pemain di pusat (0,0)
            if (candPos.Length() < PlayerSafeZoneRadius + effRadius)
            {
                return false;
            }

            // 2. Cek batas dalam sektor (12.000 x 12.000)
            float maxLimit = halfSector - (effRadius + 140.0f);
            if (MathF.Abs(candPos.X) > maxLimit || MathF.Abs(candPos.Y) > maxLimit)
            {
                return false;
            }

            // 3. Jaga jarak dari 3 Portal Kosmik di Inner Belt (Zona Bebas Rift ~320 px)
            for (int r = 0; r < _cosmicRifts.Count; r++)
            {
                float distToRift = candPos.DistanceTo(_cosmicRifts[r].Position);
                if (distToRift < effRadius + 320.0f)
                {
                    return false;
                }
            }

            // 4. Cek jarak terhadap semua asteroid yang sudah ada sebelumnya (Anti-Overlap!)
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

        // Definisi 3 Sabuk Kosmik:
        // Sabuk 1: Inner (5 asteroid, radius 320 - 450 px, jarak 1.200 - 2.200 px)
        // Sabuk 2: Mid (15 asteroid, radius 440 - 720 px, jarak 2.450 - 4.250 px)
        // Sabuk 3: Outer (11 asteroid, radius 700 - 1.100 px, jarak 4.450 - 5.500 px)
        var beltConfigs = new (int Count, float MinDist, float MaxDist, float MinRad, float MaxRad)[]
        {
            (5, 1250.0f, 2200.0f, 320.0f, 440.0f),
            (15, 2450.0f, 4250.0f, 440.0f, 700.0f),
            (11, 4450.0f, 5500.0f, 680.0f, 1050.0f)
        };

        foreach (var belt in beltConfigs)
        {
            for (int i = 0; i < belt.Count; i++)
            {
                SwarmRole role = PickRandomType(rng, out bool isEnergy);
                float baseRadius = rng.RandfRange(belt.MinRad, belt.MaxRad);
                float radius = baseRadius;
                
                // Energy Asteroid memiliki ujung bintang yang menjulur hingga ~1.25x
                float effFactor = isEnergy ? 1.28f : 1.18f;
                float effRadius = radius * effFactor;

                Vector2 chosenPos = Vector2.Zero;
                bool foundSpot = false;

                // Coba hingga 80 kali per asteroid untuk menemukan posisi yang 100% bebas tumpang tindih
                for (int attempt = 0; attempt < 80; attempt++)
                {
                    float angleBase = ((float)i / belt.Count * Mathf.Tau) + rng.RandfRange(-0.35f, 0.35f);
                    float dist = rng.RandfRange(belt.MinDist, belt.MaxDist);
                    Vector2 candidatePos = new Vector2(MathF.Cos(angleBase), MathF.Sin(angleBase)) * dist;

                    // Jika percobaan sudah lebih dari 40, sedikit perkecil radius agar muat di celah padat
                    if (attempt > 40)
                    {
                        radius = MathF.Max(belt.MinRad * 0.85f, radius * 0.96f);
                        effRadius = radius * effFactor;
                    }

                    if (IsLocationClear(candidatePos, effRadius))
                    {
                        chosenPos = candidatePos;
                        foundSpot = true;
                        break;
                    }
                }

                // Fallback jika area sangat padat: acak radius dan jarak di seluruh jangkauan sabuk
                if (!foundSpot)
                {
                    for (int fallback = 0; fallback < 60; fallback++)
                    {
                        float randAngle = rng.Randf() * Mathf.Tau;
                        float randDist = rng.RandfRange(belt.MinDist * 0.9f, belt.MaxDist * 1.05f);
                        Vector2 candidatePos = new Vector2(MathF.Cos(randAngle), MathF.Sin(randAngle)) * randDist;
                        radius = belt.MinRad * 0.8f;
                        effRadius = radius * effFactor;

                        if (IsLocationClear(candidatePos, effRadius))
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
    }

    private void SpawnAsteroid(Vector2 pos, float radius, SwarmRole role, bool isEnergy, RandomNumberGenerator rng)
    {
        float effectiveRadius = isEnergy ? rng.RandfRange(85.0f, 115.0f) : radius;
        var asteroid = new Asteroid
        {
            GlobalPosition = pos,
            Radius = effectiveRadius,
            MaxHealth = effectiveRadius * 5.0f,
            AsteroidType = role,
            IsEnergyAsteroid = isEnergy
        };

        // Kecepatan hanyut sangat lambat & stabil khas benda angkasa raksasa
        float driftAngle = rng.Randf() * Mathf.Tau;
        float driftSpeed = rng.RandfRange(4.0f, 16.0f);
        asteroid.LinearVelocity = new Vector2(MathF.Cos(driftAngle), MathF.Sin(driftAngle)) * driftSpeed;
        asteroid.AngularVelocity = rng.RandfRange(-0.08f, 0.08f);

        AddChild(asteroid);
    }

    private static SwarmRole PickRandomType(RandomNumberGenerator rng, out bool isEnergy)
    {
        if (rng.Randf() < 0.20f)
        {
            isEnergy = true;
            return SwarmRole.DartNeedle;
        }

        isEnergy = false;
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

    public override void _Draw()
    {
        // Garis batas perimeter sektor kosmik (12.000 x 12.000 px)
        DrawRect(_sectorBounds, PerimeterColor, false, 4.0f);

        // Penanda sudut sektor
        float cornerLength = 260.0f;
        Vector2 tl = _sectorBounds.Position;
        Vector2 tr = new(_sectorBounds.End.X, _sectorBounds.Position.Y);
        Vector2 bl = new(_sectorBounds.Position.X, _sectorBounds.End.Y);
        Vector2 br = _sectorBounds.End;

        // Top-Left
        DrawLine(tl, tl + Vector2.Right * cornerLength, CornerMarkerColor, 5.0f);
        DrawLine(tl, tl + Vector2.Down * cornerLength, CornerMarkerColor, 5.0f);
        // Top-Right
        DrawLine(tr, tr + Vector2.Left * cornerLength, CornerMarkerColor, 5.0f);
        DrawLine(tr, tr + Vector2.Down * cornerLength, CornerMarkerColor, 5.0f);
        // Bottom-Left
        DrawLine(bl, bl + Vector2.Right * cornerLength, CornerMarkerColor, 5.0f);
        DrawLine(bl, bl + Vector2.Up * cornerLength, CornerMarkerColor, 5.0f);
        // Bottom-Right
        DrawLine(br, br + Vector2.Left * cornerLength, CornerMarkerColor, 5.0f);
        DrawLine(br, br + Vector2.Up * cornerLength, CornerMarkerColor, 5.0f);
    }
}
