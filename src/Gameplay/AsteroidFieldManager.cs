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

    // Telemetri sumur gravitasi aktif (Bintang Mini Aether + Aether Seed Infall) untuk arena.gd
    public Vector2[] GravityWellPositions { get; private set; } = Array.Empty<Vector2>();
    public float[] GravityWellRadii { get; private set; } = Array.Empty<float>();
    public float[] GravityWellStrengths { get; private set; } = Array.Empty<float>();
    public float[] GravityWellInfallRatios { get; private set; } = Array.Empty<float>();
    public float[] GravityWellPullAccels { get; private set; } = Array.Empty<float>();
    public float[] GravityWellSwirlAccels { get; private set; } = Array.Empty<float>();
    public float[] GravityWellHeatRates { get; private set; } = Array.Empty<float>();

    private readonly List<Vector2> _gravPosList = new(16);
    private readonly List<float> _gravRadList = new(16);
    private readonly List<float> _gravStrList = new(16);
    private readonly List<float> _gravInfallList = new(16);
    private readonly List<float> _gravPullList = new(16);
    private readonly List<float> _gravSwirlList = new(16);
    private readonly List<float> _gravHeatList = new(16);

    private readonly List<CosmicRift> _cosmicRifts = new();
    private float _riftEjectionTimer = 2.5f;
    private int _nextRiftIndex = 0;
    private bool _hasTriggeredInitialDemo = false;

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
                OrbitSpeed = 0.012f
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

        UpdateGravityWellTelemetry();

        if (_riftEjectionTimer <= 0.0f && _cosmicRifts.Count > 0)
        {
            int currentAsteroidCount = Asteroid.ActiveAsteroids.Count;
            bool shouldTrigger = (currentAsteroidCount < TargetAsteroidCount) || !_hasTriggeredInitialDemo;

            if (shouldTrigger)
            {
                var rift = _cosmicRifts[_nextRiftIndex];
                _nextRiftIndex = (_nextRiftIndex + 1) % _cosmicRifts.Count;

                if (rift.TriggerEjectionSequence())
                {
                    _hasTriggeredInitialDemo = true;
                    _riftEjectionTimer = (float)GD.RandRange(RiftCheckIntervalMin, RiftCheckIntervalMax);
                }
                else
                {
                    _riftEjectionTimer = 5.0f;
                }
            }
            else
            {
                _riftEjectionTimer = 14.0f;
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

        GravityWellPositions = _gravPosList.ToArray();
        GravityWellRadii = _gravRadList.ToArray();
        GravityWellStrengths = _gravStrList.ToArray();
        GravityWellInfallRatios = _gravInfallList.ToArray();
        GravityWellPullAccels = _gravPullList.ToArray();
        GravityWellSwirlAccels = _gravSwirlList.ToArray();
        GravityWellHeatRates = _gravHeatList.ToArray();
    }

    private void OnRiftSpawnAsteroid(Vector2 spawnPos, Vector2 ejectVelocity)
    {
        var rng = new RandomNumberGenerator();
        rng.Randomize();

        // Cosmic Rift melontarkan asteroid batu mineral baru untuk menggantikan asteroid yang habis dipanen
        // (Bintang Aether bereinkarnasi secara abadi melalui siklus Aether Seed)
        SwarmRole role = PickRandomMineralRole(rng);
        float radius = (float)GD.RandRange(350.0, 520.0);

        var asteroid = new Asteroid
        {
            GlobalPosition = spawnPos,
            Radius = radius,
            MaxHealth = radius * 5.0f,
            AsteroidType = role,
            IsEnergyAsteroid = false,
            IsBirthAnimationActive = true,
            BirthRiftOrigin = spawnPos,
            BirthEjectDirection = ejectVelocity.LengthSquared() > 1.0f ? ejectVelocity.Normalized() : Vector2.Right
        };

        asteroid.LinearVelocity = ejectVelocity;
        asteroid.AngularVelocity = (float)GD.RandRange(-0.12, 0.12);

        AddChild(asteroid);
    }

    public void SpawnColossalAsteroidField()
    {
        var rng = new RandomNumberGenerator();
        rng.Randomize();

        var placedAsteroids = new List<(Vector2 Pos, float EffRadius)>();

        const float InterAsteroidPadding = 260.0f;
        const float PlayerSafeZoneRadius = 1100.0f;
        float halfSector = SectorSize.X * 0.5f;

        bool IsLocationClear(Vector2 candPos, float effRadius)
        {
            if (candPos.Length() < PlayerSafeZoneRadius + effRadius)
            {
                return false;
            }

            float maxLimit = halfSector - (effRadius + 140.0f);
            if (MathF.Abs(candPos.X) > maxLimit || MathF.Abs(candPos.Y) > maxLimit)
            {
                return false;
            }

            for (int r = 0; r < _cosmicRifts.Count; r++)
            {
                float distToRift = candPos.DistanceTo(_cosmicRifts[r].Position);
                if (distToRift < effRadius + 320.0f)
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

                    if (IsLocationClear(candidatePos, effRadius))
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

        float driftAngle = rng.Randf() * Mathf.Tau;
        float driftSpeed = rng.RandfRange(4.0f, 16.0f);
        asteroid.LinearVelocity = new Vector2(MathF.Cos(driftAngle), MathF.Sin(driftAngle)) * driftSpeed;
        asteroid.AngularVelocity = rng.RandfRange(-0.08f, 0.08f);

        AddChild(asteroid);
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
