using System;
using System.Collections.Generic;
using GeometrySwarm.Core.ECS.Components;
using Godot;

namespace GeometrySwarm.Gameplay;

/// <summary>
/// Pengelola peta ruang angkasa luas (12.000 x 12.000 px) & pembangun asteroid raksasa prosedural.
/// Menyebarkan 32 asteroid kolosal (radius 320 hingga 1.250 px) dengan patch kotak-kotak kecil redup
/// (1 deposit raksasa atau banyak deposit kecil).
/// </summary>
public partial class AsteroidFieldManager : Node2D
{
    [Export] public Vector2 SectorSize { get; set; } = new(12000.0f, 12000.0f);
    [Export] public int TotalAsteroids { get; set; } = 32;

    private Rect2 _sectorBounds;
    private static readonly Color PerimeterColor = new(0.35f, 0.15f, 0.55f, 0.45f);
    private static readonly Color CornerMarkerColor = new(1.8f, 0.5f, 2.5f, 0.85f);

    public override void _Ready()
    {
        _sectorBounds = new Rect2(-SectorSize * 0.5f, SectorSize);
        SpawnColossalAsteroidField();
        QueueRedraw();
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

            // 3. Cek jarak terhadap semua asteroid yang sudah ada sebelumnya (Anti-Overlap!)
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
        var asteroid = new Asteroid
        {
            GlobalPosition = pos,
            Radius = radius,
            MaxHealth = radius * 5.0f,
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
