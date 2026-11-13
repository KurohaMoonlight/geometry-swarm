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

        // 1. Sabuk Awal (Radius 950 - 2.200 px dari pusat)
        // Berada di luar area spawn pemain agar kapal tidak bertubrukan saat mulai
        int innerCount = 5;
        for (int i = 0; i < innerCount; i++)
        {
            float angle = ((float)i / innerCount * Mathf.Tau) + rng.RandfRange(-0.25f, 0.25f);
            float dist = rng.RandfRange(1000.0f, 2100.0f);
            Vector2 spawnPos = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * dist;

            float radius = rng.RandfRange(320.0f, 480.0f);
            SpawnAsteroid(spawnPos, radius, PickRandomType(rng, out bool isEnergy), isEnergy, rng);
        }

        // 2. Sabuk Tengah (Radius 2.400 - 4.400 px)
        int midCount = 16;
        for (int i = 0; i < midCount; i++)
        {
            float angle = ((float)i / midCount * Mathf.Tau) + rng.RandfRange(-0.2f, 0.2f);
            float dist = rng.RandfRange(2400.0f, 4300.0f);
            Vector2 spawnPos = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * dist;

            float radius = rng.RandfRange(460.0f, 780.0f);
            SpawnAsteroid(spawnPos, radius, PickRandomType(rng, out bool isEnergy), isEnergy, rng);
        }

        // 3. Sabuk Luar & Deep Space (Radius 4.500 - 5.650 px)
        // Asteroid Geode Raksasa Kolosal (Monolith)
        int outerCount = 11;
        for (int i = 0; i < outerCount; i++)
        {
            float angle = ((float)i / outerCount * Mathf.Tau) + rng.RandfRange(-0.2f, 0.2f);
            float dist = rng.RandfRange(4600.0f, 5600.0f);
            Vector2 spawnPos = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * dist;

            float radius = rng.RandfRange(750.0f, 1200.0f);
            SpawnAsteroid(spawnPos, radius, PickRandomType(rng, out bool isEnergy), isEnergy, rng);
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
