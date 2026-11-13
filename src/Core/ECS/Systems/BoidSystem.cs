using System;
using System.Collections.Generic;
using Arch.Core;
using GeometrySwarm.Core.ECS.Components;
using Godot;

namespace GeometrySwarm.Core.ECS.Systems;

/// <summary>
/// Sistem gerak & state machine kawanan (Orbiting, Launched, Returning)
/// dengan perisai Guardian berbobot kokoh di ring terluar (220px) yang bergeser anggun.
/// </summary>
public sealed class BoidSystem
{
    private readonly World _world;
    private readonly QueryDescription _boidQuery;

    // Spatial Density Grid (L1-Cache friendly)
    public const float CellSize = 32.0f;
    private const float InvCellSize = 1.0f / CellSize;
    public const int GridWidth = 64;
    public const int GridHeight = 38;
    private readonly short[] _densityGrid = new short[GridWidth * GridHeight];

    // Parameter Orbiting Attacker
    public float SeparationWeight { get; set; } = 85.0f;
    public float OrbitSpringWeight { get; set; } = 5.2f;
    public float OrbitTangentialWeight { get; set; } = 240.0f;
    public float Damping { get; set; } = 0.94f;
    public float MaxOrbitSpeed { get; set; } = 480.0f;

    // Parameter Perisai Guardian (Ring Terluar 220px - Berbobot Berat & Anggun)
    public const int SectorCount = 12;
    public float ShieldRadius { get; set; } = 220.0f;
    public float ShieldSpringWeight { get; set; } = 8.5f;       // Pegas lebih lembut agar tidak teleportasi
    public float MaxGuardianSpeed { get; set; } = 240.0f;        // Batas kecepatan luncur tameng di lingkar luar

    // Parameter Peluru (Launched Mode)
    public float BulletThrust { get; set; } = 2200.0f;
    public float BulletMaxSpeed { get; set; } = 1350.0f;

    // Parameter Kepulangan (Returning Mode)
    public float ReturnThrust { get; set; } = 2400.0f;
    public float ReturnMaxSpeed { get; set; } = 750.0f;

    // Counter status kawanan real-time
    public int OrbitingCount { get; private set; }
    public int LaunchedCount { get; private set; }
    public int ReturningCount { get; private set; }

    // Warna saat drone menjadi peluru berenergi tinggi (Blazing White-Gold)
    private static readonly Color BulletGlowColor = new(3.2f, 2.6f, 1.2f, 1.0f);
    // Warna saat drone terbang pulang (Emerald Glow)
    private static readonly Color ReturnGlowColor = new(0.8f, 2.6f, 1.8f, 1.0f);

    public BoidSystem(World world)
    {
        _world = world;
        _boidQuery = new QueryDescription().WithAll<PositionComponent, VelocityComponent, SwarmUnitComponent, VisualComponent>();
    }

    /// <summary>
    /// Update simulasi dan transisi state seluruh kawanan.
    /// threatPos digunakan Core untuk mengalokasikan konsentrasi Guardian ke sektor ancaman.
    /// </summary>
    public void Update(float delta, Vector2 anchorPos, Vector2 anchorVelocity, Vector2 threatPos)
    {
        float dt = MathF.Min(delta, 0.033f);
        float decayOrbit = MathF.Pow(Damping, dt * 60.0f);
        float decayGuardian = MathF.Pow(0.92f, dt * 60.0f); // Redaman inersia tameng berat
        float decayBullet = MathF.Pow(0.992f, dt * 60.0f);
        float decayReturn = MathF.Pow(0.95f, dt * 60.0f);

        int orbitCount = 0;
        int launchCount = 0;
        int returnCount = 0;

        // ===============================================================
        // 1. ALOKASI 12 SEKTOR DINAMIS UNTUK GUARDIAN (RING TERLUAR)
        // ===============================================================
        int totalGuardians = 0;
        _world.Query(in _boidQuery, (ref SwarmUnitComponent swarm) =>
        {
            if (swarm.Role == SwarmRole.Guardian && swarm.State == SwarmState.Orbiting)
            {
                totalGuardians++;
            }
        });

        Vector2 toThreat = threatPos - anchorPos;
        float threatAngle = toThreat.LengthSquared() > 10.0f ? toThreat.Angle() : 0.0f;

        // Distribusi sudut target Guardian meluncur halus (smooth shift)
        int guardianIndex = 0;
        if (totalGuardians > 0)
        {
            _world.Query(in _boidQuery, (ref SwarmUnitComponent swarm) =>
            {
                if (swarm.Role == SwarmRole.Guardian && swarm.State == SwarmState.Orbiting)
                {
                    float t = (float)guardianIndex / totalGuardians;
                    float baseAngle = t * Mathf.Tau;

                    // Bias kurva ke sektor ancaman (konsentrasi tameng)
                    float angleDiff = Mathf.AngleDifference(baseAngle, threatAngle);
                    float pullFactor = MathF.Exp(-(angleDiff * angleDiff) / 1.9f);
                    float desiredAngle = Mathf.LerpAngle(baseAngle, threatAngle, pullFactor * 0.60f);

                    // Interpolasi sudut target secara bertahap agar tidak melesat mendadak
                    swarm.ShieldTargetAngle = Mathf.LerpAngle(swarm.ShieldTargetAngle, desiredAngle, MathF.Min(1.0f, 3.5f * dt));

                    guardianIndex++;
                }
            });
        }

        // ===============================================================
        // 2. ISI SPATIAL DENSITY FIELD (HANYA UNTUK ATTACKER YANG MENGORBIT)
        // ===============================================================
        Array.Clear(_densityGrid, 0, _densityGrid.Length);
        _world.Query(in _boidQuery, (ref PositionComponent pos, ref SwarmUnitComponent swarm) =>
        {
            if (swarm.State == SwarmState.Orbiting && swarm.Role != SwarmRole.Guardian)
            {
                int cx = Math.Clamp((int)(pos.Value.X * InvCellSize), 1, GridWidth - 2);
                int cy = Math.Clamp((int)(pos.Value.Y * InvCellSize), 1, GridHeight - 2);
                _densityGrid[cy * GridWidth + cx]++;
            }
        });

        // ===============================================================
        // 3. PROSES GERAK BERDASARKAN STATE MACHINE & PERAN
        // ===============================================================
        _world.Query(in _boidQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
        {
            // Perbarui putaran spin visual mandiri (Shuriken berputar, Bor berputar)
            if (MathF.Abs(swarm.SpinRate) > 0.01f)
            {
                swarm.SpinAngle += swarm.SpinRate * dt;
                if (swarm.SpinAngle > Mathf.Tau) swarm.SpinAngle -= Mathf.Tau;
            }

            switch (swarm.State)
            {
                // ==========================================
                // 1. STATE ORBITING
                // ==========================================
                case SwarmState.Orbiting:
                    orbitCount++;
                    vis.Color = vis.BaseColor;

                    if (swarm.Role == SwarmRole.Guardian)
                    {
                        // ----------------------------------------------------
                        // GUARDIAN: Barisan Tameng di Ring Terluar (220px)
                        // Mengikuti target sudut sektor dengan bobot inersia anggun
                        // ----------------------------------------------------
                        Vector2 targetSlot = anchorPos + new Vector2(
                            MathF.Cos(swarm.ShieldTargetAngle),
                            MathF.Sin(swarm.ShieldTargetAngle)
                        ) * ShieldRadius;

                        Vector2 toSlot = targetSlot - pos.Value;
                        vel.Value += toSlot * (ShieldSpringWeight * dt);
                        vel.Value += anchorVelocity * 1.0f;
                        vel.Value *= decayGuardian;

                        // Batas kecepatan luncur tameng agar tidak melesat terlalu cepat
                        if (vel.Value.LengthSquared() > MaxGuardianSpeed * MaxGuardianSpeed)
                        {
                            vel.Value = vel.Value.Normalized() * MaxGuardianSpeed;
                        }

                        // Hadapkan kurung siku ]] tegak lurus keluar menghalau ancaman
                        swarm.SpinAngle = (pos.Value - anchorPos).Angle() + Mathf.Pi * 0.5f;
                    }
                    else
                    {
                        // ----------------------------------------------------
                        // ATTACKER: Awan Boids di Pita Dalam (55 - 160px)
                        // ----------------------------------------------------
                        Vector2 toCore = anchorPos - pos.Value;
                        float distToCore = toCore.Length();
                        Vector2 dirToCore = distToCore > 0.001f ? toCore / distToCore : Vector2.Right;

                        Vector2 accel = Vector2.Zero;

                        float distError = distToCore - swarm.OrbitDistance;
                        accel += dirToCore * (distError * OrbitSpringWeight);

                        Vector2 tangent = new Vector2(-dirToCore.Y, dirToCore.X);
                        accel += tangent * (swarm.OrbitSpeed * OrbitTangentialWeight);

                        accel += anchorVelocity * 1.6f;

                        int cx = Math.Clamp((int)(pos.Value.X * InvCellSize), 1, GridWidth - 2);
                        int cy = Math.Clamp((int)(pos.Value.Y * InvCellSize), 1, GridHeight - 2);
                        int idx = cy * GridWidth + cx;
                        float gradX = _densityGrid[idx - 1] - _densityGrid[idx + 1];
                        float gradY = _densityGrid[idx - GridWidth] - _densityGrid[idx + GridWidth];

                        if (MathF.Abs(gradX) + MathF.Abs(gradY) > 0.05f)
                        {
                            accel += new Vector2(gradX, gradY) * SeparationWeight;
                        }

                        vel.Value = (vel.Value + accel * dt) * decayOrbit;
                        if (vel.Value.LengthSquared() > MaxOrbitSpeed * MaxOrbitSpeed)
                        {
                            vel.Value = vel.Value.Normalized() * MaxOrbitSpeed;
                        }
                    }
                    break;

                // ==========================================
                // 2. STATE LAUNCHED (Swarm sebagai Peluru)
                // ==========================================
                case SwarmState.Launched:
                    launchCount++;
                    swarm.LifeTime += dt;

                    if (swarm.Role == SwarmRole.CrescentBoomerang)
                    {
                        swarm.TargetDirection = swarm.TargetDirection.Rotated(2.2f * dt);
                        vel.Value += swarm.TargetDirection * (BulletThrust * 1.1f * dt);
                        vis.Color = BulletGlowColor;
                    }
                    else if (swarm.Role == SwarmRole.DrillDrone)
                    {
                        // Jika bukan bagian dari Giga Drill aktif, terbang normal
                        vel.Value += swarm.TargetDirection * (BulletThrust * 1.5f * dt);
                    }
                    else
                    {
                        vel.Value += swarm.TargetDirection * (BulletThrust * dt);
                        vis.Color = BulletGlowColor;
                    }

                    vel.Value *= decayBullet;
                    if (vel.Value.LengthSquared() > BulletMaxSpeed * BulletMaxSpeed)
                    {
                        vel.Value = vel.Value.Normalized() * BulletMaxSpeed;
                    }

                    if (swarm.LifeTime >= swarm.MaxLifeTime)
                    {
                        swarm.State = SwarmState.Returning;
                        swarm.LifeTime = 0.0f;
                    }
                    break;

                // ==========================================
                // 3. STATE RETURNING (Terbang Pulang ke Core)
                // ==========================================
                case SwarmState.Returning:
                    returnCount++;
                    vis.Color = ReturnGlowColor;

                    Vector2 toReturn = anchorPos - pos.Value;
                    float distRet = toReturn.Length();
                    Vector2 dirRet = distRet > 0.001f ? toReturn / distRet : Vector2.Zero;

                    vel.Value += dirRet * (ReturnThrust * dt);
                    vel.Value *= decayReturn;

                    if (vel.Value.LengthSquared() > ReturnMaxSpeed * ReturnMaxSpeed)
                    {
                        vel.Value = vel.Value.Normalized() * ReturnMaxSpeed;
                    }

                    float returnRadius = (swarm.Role == SwarmRole.Guardian) ? ShieldRadius : swarm.OrbitDistance;
                    if (distRet <= returnRadius + 30.0f)
                    {
                        swarm.State = SwarmState.Orbiting;
                        swarm.LifeTime = 0.0f;
                        vis.Color = vis.BaseColor;
                    }
                    break;
            }

            pos.Value += vel.Value * dt;
        });

        OrbitingCount = orbitCount;
        LaunchedCount = launchCount;
        ReturningCount = returnCount;
    }
}
