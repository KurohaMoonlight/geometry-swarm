using System;
using Arch.Core;
using GeometrySwarm.Core.ECS.Components;
using GeometrySwarm.Core.Physics;
using GeometrySwarm.Gameplay;
using Godot;

namespace GeometrySwarm.Core.ECS.Systems;

/// <summary>
/// Sistem gerak & state machine kawanan (Dioptimalkan dengan Pre-Filtered Nearby Asteroids & Zero-Interop).
/// - Menyaring hanya asteroid yang berada di dekat Core (1x per frame) sehingga ribuan unit Swarm
///   tidak perlu meloop 32 asteroid atau memanggil P/Invoke C++ Godot di dalam loop utama.
/// </summary>
public sealed class BoidSystem
{
    private readonly World _world;
    private readonly QueryDescription _boidQuery;

    // Spatial Density & Threat Grid (L1-Cache friendly)
    public const float CellSize = 32.0f;
    private const float InvCellSize = 1.0f / CellSize;
    public const int GridWidth = 68;
    public const int GridHeight = 42;
    private readonly short[] _densityGrid = new short[GridWidth * GridHeight];
    private readonly short[] _enemyCellGrid = new short[GridWidth * GridHeight];

    private readonly SpatialHashGrid _defenderSpatialGrid = new(cellSize: 24.0f, maxEntities: 4096, tableSize: 2048);
    private readonly Vector2[] _defenderPositions = new Vector2[4096];
    private readonly Vector2[] _defenderVelocities = new Vector2[4096];
    private readonly int[] _neighborBuffer = new int[32];
    private int _activeDefenderCount = 0;

    // Buffer asteroid terdekat yang sudah disaring sekali per frame (Zero-GC, Zero-Interop di dalam loop)
    private readonly Asteroid[] _nearbyAsteroids = new Asteroid[16];
    private int _nearbyAsteroidCount = 0;

    public float SeparationWeight { get; set; } = 88.0f;
    public float OrbitSpringWeight { get; set; } = 5.6f;
    public float OrbitTangentialWeight { get; set; } = 250.0f;
    public float Damping { get; set; } = 0.94f;
    public float MaxOrbitSpeed { get; set; } = 500.0f;

    public const int SectorCount = 12;
    public const float SectorArc = Mathf.Tau / SectorCount;
    public float ShieldRadius { get; set; } = 88.0f;
    public float ThreatScanRadius { get; set; } = 750.0f;
    public float ShieldRadialSpring { get; set; } = 4.8f;
    public float ShieldAngularSpring { get; set; } = 7.5f;
    public float DefenderSeparationRadius { get; set; } = 18.0f;
    public float DefenderSeparationWeight { get; set; } = 260.0f;
    public float MaxGuardianSpeed { get; set; } = 340.0f;

    private readonly int[] _sectorEnemyCounts = new int[SectorCount];
    private readonly int[] _sectorDefenderCounts = new int[SectorCount];
    private readonly float[] _sectorThreatScores = new float[SectorCount];
    private readonly int[] _sectorTargetQuotas = new int[SectorCount];
    private readonly int[] _sectorAssignedCounts = new int[SectorCount];
    private float _sectorRebalanceTimer = 0.0f;
    private float _organicWaveTime = 0.0f;

    public float BulletThrust { get; set; } = 2200.0f;
    public float BulletMaxSpeed { get; set; } = 1350.0f;

    public float ReturnThrust { get; set; } = 2400.0f;
    public float ReturnMaxSpeed { get; set; } = 750.0f;

    private const float DroneVoxelRadius = 5.0f;

    public int OrbitingCount { get; private set; }
    public int LaunchedCount { get; private set; }
    public int ReturningCount { get; private set; }
    public int EnemyCount { get; private set; }

    private static readonly Color BulletGlowColor = new(3.2f, 2.6f, 1.2f, 1.0f);
    private static readonly Color ReturnGlowColor = new(0.8f, 2.6f, 1.8f, 1.0f);
    private static readonly Color EnemyGlowColor = new(3.4f, 0.35f, 0.9f, 1.0f);

    public BoidSystem(World world)
    {
        _world = world;
        _boidQuery = new QueryDescription().WithAll<PositionComponent, VelocityComponent, SwarmUnitComponent, VisualComponent>();
    }

    public ReadOnlySpan<float> GetSectorThreatScores() => _sectorThreatScores;

    public void Update(float delta, Vector2 anchorPos, Vector2 anchorVelocity, Vector2 cursorThreatPos)
    {
        float dt = MathF.Min(delta, 0.033f);
        _organicWaveTime += dt;
        _sectorRebalanceTimer += dt;

        float decayOrbit = MathF.Pow(Damping, dt * 60.0f);
        float decayGuardian = MathF.Pow(0.89f, dt * 60.0f);
        float decayBullet = MathF.Pow(0.992f, dt * 60.0f);
        float decayReturn = MathF.Pow(0.95f, dt * 60.0f);

        int orbitCount = 0;
        int launchCount = 0;
        int returnCount = 0;
        int enemyCount = 0;

        // =========================================================================
        // 0. PRE-FILTER ASTEROID TERDEKAT SEKALI PER FRAME (MENGHILANGKAN >95% INTEROP!)
        // =========================================================================
        _nearbyAsteroidCount = 0;
        var allAsteroids = Asteroid.ActiveAsteroids;
        const float maxSwarmReach = 1850.0f;

        for (int a = 0; a < allAsteroids.Count; a++)
        {
            var ast = allAsteroids[a];
            if (ast == null || !ast.IsActiveInTree) continue;

            float checkDist = maxSwarmReach + ast.OuterBoundRadius;
            Vector2 d = ast.CachedPosition - anchorPos;
            if (MathF.Abs(d.X) <= checkDist && MathF.Abs(d.Y) <= checkDist)
            {
                if (_nearbyAsteroidCount < _nearbyAsteroids.Length)
                {
                    _nearbyAsteroids[_nearbyAsteroidCount++] = ast;
                }
            }
        }

        // =========================================================================
        // 1. ISI SPATIAL GRID
        // =========================================================================
        Array.Clear(_densityGrid, 0, _densityGrid.Length);
        Array.Clear(_enemyCellGrid, 0, _enemyCellGrid.Length);
        Array.Clear(_sectorEnemyCounts, 0, SectorCount);
        Array.Clear(_sectorDefenderCounts, 0, SectorCount);
        Array.Clear(_sectorAssignedCounts, 0, SectorCount);

        _defenderSpatialGrid.Clear();
        _activeDefenderCount = 0;

        Vector2 gridOrigin = anchorPos - new Vector2(GridWidth * CellSize * 0.5f, GridHeight * CellSize * 0.5f);

        _world.Query(in _boidQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm) =>
        {
            if (swarm.State == SwarmState.Dead) return;

            Vector2 local = pos.Value - gridOrigin;
            int cx = Math.Clamp((int)(local.X * InvCellSize), 1, GridWidth - 2);
            int cy = Math.Clamp((int)(local.Y * InvCellSize), 1, GridHeight - 2);
            int cellIdx = cy * GridWidth + cx;

            if (swarm.OwnerId > 0)
            {
                _enemyCellGrid[cellIdx]++;
                enemyCount++;
                return;
            }

            if (swarm.State == SwarmState.Orbiting)
            {
                if (swarm.Role == SwarmRole.Guardian)
                {
                    if (_activeDefenderCount < _defenderPositions.Length)
                    {
                        int defIdx = _activeDefenderCount++;
                        _defenderPositions[defIdx] = pos.Value;
                        _defenderVelocities[defIdx] = vel.Value;
                        _defenderSpatialGrid.Insert(defIdx, pos.Value);
                    }

                    Vector2 rel = pos.Value - anchorPos;
                    int actualSector = AngleToSector(rel.Angle());
                    _sectorDefenderCounts[actualSector]++;

                    int assignedSec = Math.Clamp(swarm.AssignedSector, 0, SectorCount - 1);
                    _sectorAssignedCounts[assignedSec]++;
                }
                else
                {
                    _densityGrid[cellIdx]++;
                }
            }
        });

        if (enemyCount == 0)
        {
            Vector2 toCursor = cursorThreatPos - anchorPos;
            float cursorDist = toCursor.Length();
            if (cursorDist > 25.0f)
            {
                Vector2 samplePos = (cursorDist <= ThreatScanRadius)
                    ? cursorThreatPos
                    : anchorPos + (toCursor / cursorDist) * (ThreatScanRadius * 0.9f);

                Vector2 localCursor = samplePos - gridOrigin;
                int ccx = Math.Clamp((int)(localCursor.X * InvCellSize), 2, GridWidth - 3);
                int ccy = Math.Clamp((int)(localCursor.Y * InvCellSize), 2, GridWidth - 3);
                _enemyCellGrid[ccy * GridWidth + ccx] += 25;
                _enemyCellGrid[ccy * GridWidth + (ccx - 1)] += 10;
                _enemyCellGrid[ccy * GridWidth + (ccx + 1)] += 10;
                _enemyCellGrid[(ccy - 1) * GridWidth + ccx] += 10;
                _enemyCellGrid[(ccy + 1) * GridWidth + ccx] += 10;
            }
        }

        // =========================================================================
        // 2. CORE MEMINDAI SEL SPATIAL GRID DI RADIUS SEKITAR MARKAS
        // =========================================================================
        int centerCx = GridWidth / 2;
        int centerCy = GridHeight / 2;
        int scanCells = (int)MathF.Ceiling(ThreatScanRadius * InvCellSize);
        int minScanX = Math.Clamp(centerCx - scanCells, 1, GridWidth - 2);
        int maxScanX = Math.Clamp(centerCx + scanCells, 1, GridWidth - 2);
        int minScanY = Math.Clamp(centerCy - scanCells, 1, GridHeight - 2);
        int maxScanY = Math.Clamp(centerCy + scanCells, 1, GridHeight - 2);
        float scanRadiusSq = ThreatScanRadius * ThreatScanRadius;

        int totalScannedEnemies = 0;
        for (int cy = minScanY; cy <= maxScanY; cy++)
        {
            int rowOffset = cy * GridWidth;
            float cellCenterY = gridOrigin.Y + (cy + 0.5f) * CellSize;
            for (int cx = minScanX; cx <= maxScanX; cx++)
            {
                short enemiesInCell = _enemyCellGrid[rowOffset + cx];
                if (enemiesInCell <= 0) continue;

                float cellCenterX = gridOrigin.X + (cx + 0.5f) * CellSize;
                Vector2 diff = new Vector2(cellCenterX - anchorPos.X, cellCenterY - anchorPos.Y);
                float distSq = diff.LengthSquared();

                if (distSq <= scanRadiusSq && distSq > 1.0f)
                {
                    int sector = AngleToSector(diff.Angle());
                    _sectorEnemyCounts[sector] += enemiesInCell;
                    totalScannedEnemies += enemiesInCell;
                }
            }
        }

        for (int s = 0; s < SectorCount; s++)
        {
            _sectorThreatScores[s] = _sectorEnemyCounts[s] - _sectorDefenderCounts[s];
        }

        // =========================================================================
        // 3. ALOKASI SUDUT ORBIT DINAMIS OLEH CORE
        // =========================================================================
        if (_activeDefenderCount > 0 && _sectorRebalanceTimer >= 0.04f)
        {
            _sectorRebalanceTimer = 0.0f;
            ComputeSectorTargetQuotas(_activeDefenderCount, totalScannedEnemies);

            _world.Query(in _boidQuery, (ref SwarmUnitComponent swarm) =>
            {
                if (swarm.State != SwarmState.Orbiting || swarm.Role != SwarmRole.Guardian || swarm.OwnerId != 0)
                    return;

                int curSec = Math.Clamp(swarm.AssignedSector, 0, SectorCount - 1);
                if (_sectorAssignedCounts[curSec] > _sectorTargetQuotas[curSec])
                {
                    int bestSector = -1;
                    int maxDeficit = 0;
                    float highestThreat = -99999.0f;

                    for (int s = 0; s < SectorCount; s++)
                    {
                        int deficit = _sectorTargetQuotas[s] - _sectorAssignedCounts[s];
                        if (deficit > maxDeficit || (deficit == maxDeficit && deficit > 0 && _sectorThreatScores[s] > highestThreat))
                        {
                            maxDeficit = deficit;
                            highestThreat = _sectorThreatScores[s];
                            bestSector = s;
                        }
                    }

                    if (bestSector != -1)
                    {
                        _sectorAssignedCounts[curSec]--;
                        _sectorAssignedCounts[bestSector]++;
                        swarm.AssignedSector = bestSector;
                    }
                }

                float sectorCenterAngle = SectorToCenterAngle(swarm.AssignedSector);
                float dumbWander = MathF.Sin(_organicWaveTime * 1.4f + swarm.OrbitAngle) * 0.14f;
                swarm.ShieldTargetAngle = sectorCenterAngle + swarm.PersonalAngleOffset + dumbWander;
            });
        }

        // =========================================================================
        // 4. PROSES GERAK UNIT BERDASARKAN STATE MACHINE & KOLISI VOXEL 13 PX
        // =========================================================================
        int nearbyCount = _nearbyAsteroidCount;

        _world.Query(in _boidQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
        {
            if (swarm.State == SwarmState.Dead) return;

            if (swarm.OwnerId > 0)
            {
                swarm.LifeTime += dt;
                vis.Color = EnemyGlowColor;

                Vector2 toCore = anchorPos - pos.Value;
                float distToCore = toCore.Length();
                Vector2 dirToCore = distToCore > 0.001f ? toCore / distToCore : Vector2.Right;

                vel.Value += dirToCore * (620.0f * dt);
                vel.Value *= decayBullet;
                if (vel.Value.LengthSquared() > 420.0f * 420.0f)
                {
                    vel.Value = vel.Value.Normalized() * 420.0f;
                }

                swarm.SpinAngle = vel.Value.Angle() + Mathf.Pi * 0.5f;

                int hits = _defenderSpatialGrid.QueryNeighbors(pos.Value, 16.0f, _neighborBuffer);
                if (hits > 0)
                {
                    swarm.State = SwarmState.Dead;
                    return;
                }

                if (distToCore < 28.0f || swarm.LifeTime >= swarm.MaxLifeTime)
                {
                    swarm.State = SwarmState.Dead;
                    return;
                }

                pos.Value += vel.Value * dt;

                for (int a = 0; a < nearbyCount; a++)
                {
                    if (_nearbyAsteroids[a].ResolveCircleVoxelCollision(ref pos.Value, ref vel.Value, DroneVoxelRadius, impulseWeight: 0.0f))
                    {
                        break;
                    }
                }
                return;
            }

            if (MathF.Abs(swarm.SpinRate) > 0.01f
                && swarm.State != SwarmState.ChargingDrill
                && swarm.State != SwarmState.DrillExhaust
                && swarm.State != SwarmState.BoostThruster
                && swarm.State != SwarmState.BoostBrake)
            {
                swarm.SpinAngle += swarm.SpinRate * dt;
                if (swarm.SpinAngle > Mathf.Tau) swarm.SpinAngle -= Mathf.Tau;
            }

            switch (swarm.State)
            {
                case SwarmState.Orbiting:
                    orbitCount++;

                    // Transisi mekar ukuran dan peredaman flash saat unit baru saja diluncurkan dari palka Core
                    if (vis.Color != vis.BaseColor)
                    {
                        vis.Color = vis.Color.Lerp(vis.BaseColor, dt * 5.0f);
                    }
                    float targetRoleScale = swarm.Role switch
                    {
                        SwarmRole.Guardian => 0.72f,
                        SwarmRole.ScannerDrone => 0.60f,
                        SwarmRole.MiningDroid => 0.62f,
                        _ => 0.55f
                    };
                    if (vis.Scale < targetRoleScale)
                    {
                        vis.Scale = Mathf.Lerp(vis.Scale, targetRoleScale, dt * 6.5f);
                    }

                    if (swarm.Role == SwarmRole.Guardian)
                    {
                        Vector2 fromCore = pos.Value - anchorPos;
                        float distFromCore = fromCore.Length();
                        Vector2 radialOut = distFromCore > 0.001f ? fromCore / distFromCore : Vector2.Right;
                        Vector2 tangent = new Vector2(-radialOut.Y, radialOut.X);

                        float breathing = MathF.Sin(_organicWaveTime * 1.8f + swarm.OrbitAngle) * 6.5f;
                        float targetRadius = ShieldRadius + swarm.PersonalRadialOffset + breathing;

                        float radialError = targetRadius - distFromCore;
                        Vector2 force = radialOut * (radialError * ShieldRadialSpring);

                        float currentAngle = fromCore.Angle();
                        float angleError = Mathf.AngleDifference(currentAngle, swarm.ShieldTargetAngle);
                        float clampedAngleErr = Mathf.Clamp(angleError, -2.0f, 2.0f);
                        force += tangent * (clampedAngleErr * ShieldRadius * ShieldAngularSpring);

                        force += anchorVelocity * 1.55f;

                        if (distFromCore > ShieldRadius * 1.35f)
                        {
                            force -= radialOut * ((distFromCore - ShieldRadius) * (ShieldRadialSpring * 1.15f));
                        }

                        int nearCount = _defenderSpatialGrid.QueryNeighbors(pos.Value, DefenderSeparationRadius, _neighborBuffer);
                        Vector2 sepForce = Vector2.Zero;
                        for (int n = 0; n < nearCount; n++)
                        {
                            int otherIdx = _neighborBuffer[n];
                            Vector2 diff = pos.Value - _defenderPositions[otherIdx];
                            float dSq = diff.LengthSquared();
                            if (dSq > 0.01f && dSq < DefenderSeparationRadius * DefenderSeparationRadius)
                            {
                                float d = MathF.Sqrt(dSq);
                                float push = (DefenderSeparationRadius - d) / DefenderSeparationRadius;
                                sepForce += (diff / d) * (push * DefenderSeparationWeight);
                            }
                        }
                        force += sepForce;

                        vel.Value = (vel.Value + force * dt) * decayGuardian;
                        if (vel.Value.LengthSquared() > MaxGuardianSpeed * MaxGuardianSpeed)
                        {
                            vel.Value = vel.Value.Normalized() * MaxGuardianSpeed;
                        }

                        float desiredFaceAngle = currentAngle + Mathf.Pi * 0.5f + MathF.Sin(_organicWaveTime * 2.5f + swarm.OrbitAngle) * 0.12f;
                        swarm.SpinAngle = Mathf.LerpAngle(swarm.SpinAngle, desiredFaceAngle, MathF.Min(1.0f, 7.5f * dt));
                    }
                    else
                    {
                        Vector2 toCore = anchorPos - pos.Value;
                        float distToCore = toCore.Length();
                        Vector2 dirToCore = distToCore > 0.001f ? toCore / distToCore : Vector2.Right;

                        Vector2 accel = Vector2.Zero;

                        float distError = distToCore - swarm.OrbitDistance;
                        accel += dirToCore * (distError * OrbitSpringWeight);

                        Vector2 tangent = new Vector2(-dirToCore.Y, dirToCore.X);
                        accel += tangent * (swarm.OrbitSpeed * OrbitTangentialWeight);

                        accel += anchorVelocity * 1.65f;

                        Vector2 localPos = pos.Value - gridOrigin;
                        int cx = Math.Clamp((int)(localPos.X * InvCellSize), 1, GridWidth - 2);
                        int cy = Math.Clamp((int)(localPos.Y * InvCellSize), 1, GridHeight - 2);
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

                        if (swarm.Role == SwarmRole.MiningDroid)
                        {
                            Vector2 toCursor = cursorThreatPos - pos.Value;
                            if (toCursor.LengthSquared() > 4.0f)
                            {
                                float desiredAngle = toCursor.Angle() + Mathf.Pi * 0.5f;
                                swarm.SpinAngle = Mathf.LerpAngle(swarm.SpinAngle, desiredAngle, MathF.Min(1.0f, 24.0f * dt));
                            }
                        }
                    }
                    break;

                case SwarmState.ChargingDrill:
                    orbitCount++;
                    break;

                case SwarmState.BoostThruster:
                case SwarmState.BoostBrake:
                    orbitCount++;
                    return;

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

                case SwarmState.DrillExhaust:
                    launchCount++;
                    return;

                case SwarmState.Returning:
                    returnCount++;
                    vis.Color = ReturnGlowColor;
                    vis.Scale = (swarm.Role == SwarmRole.Guardian) ? 1.15f : 0.9f;

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

            // =========================================================================
            // PROTOKOL KEMBALI OTOMATIS SWARM NYANGKUT (QUANTUM PHASE RECALL)
            // =========================================================================
            bool isPhaseRecalling = false;
            if (swarm.OwnerId == 0
                && swarm.State != SwarmState.Dead
                && swarm.State != SwarmState.Launched
                && swarm.State != SwarmState.ChargingDrill
                && swarm.State != SwarmState.DrillExhaust
                && swarm.State != SwarmState.BoostThruster
                && swarm.State != SwarmState.BoostBrake)
            {
                Vector2 diffCore = anchorPos - pos.Value;
                float distCore = diffCore.Length();
                float maxAllowedDist = (swarm.Role == SwarmRole.Guardian) ? (ShieldRadius + 160.0f) : (swarm.OrbitDistance + 220.0f);

                // Jika drone tertinggal jauh di belakang asteroid atau terjebak (> 520 px dari Core)
                if (distCore > maxAllowedDist)
                {
                    swarm.State = SwarmState.Returning;
                    Vector2 recallDir = diffCore / MathF.Max(0.001f, distCore);
                    
                    // Kecepatan meluncur tinggi fase nano-energi langsung kembali ke Core
                    vel.Value = vel.Value.Lerp(recallDir * 1280.0f, dt * 7.5f);
                    vis.Color = new Color(2.8f, 3.6f, 4.5f, 0.96f); // Cahaya Neon Phase Return
                    isPhaseRecalling = true;
                }
            }

            pos.Value += vel.Value * dt;

            // Jika sedang dalam Phase Recall menembus rintangan untuk kembali ke Core, lewati benturan voxel
            if (isPhaseRecalling)
            {
                float curDist = (anchorPos - pos.Value).Length();
                float resetDist = (swarm.Role == SwarmRole.Guardian) ? (ShieldRadius + 35.0f) : (swarm.OrbitDistance + 35.0f);
                if (curDist <= resetDist)
                {
                    swarm.State = SwarmState.Orbiting;
                    vis.Color = vis.BaseColor;
                }
                return;
            }

            // Cek kolisi terhadap asteroid terdekat + navigasi meluncur tangensial menghindari dinding
            for (int a = 0; a < nearbyCount; a++)
            {
                float impulseWeight = (swarm.State == SwarmState.Launched) ? 0.08f : 0.0f;
                if (_nearbyAsteroids[a].ResolveCircleVoxelCollision(ref pos.Value, ref vel.Value, DroneVoxelRadius, out Vector2 hitNormal, impulseWeight))
                {
                    if (swarm.State == SwarmState.Launched && swarm.Role != SwarmRole.DrillDrone)
                    {
                        swarm.State = SwarmState.Returning;
                        swarm.LifeTime = 0.0f;
                    }
                    else if (swarm.OwnerId == 0) // Drone pemain (Orbiting atau Returning)
                    {
                        Vector2 toCore = anchorPos - pos.Value;
                        float distCore = toCore.Length();

                        // Jika arah tarikan menuju Core terhalang oleh dinding batu (toCore berlawanan dengan hitNormal)
                        if (toCore.Dot(hitNormal) < 0.0f)
                        {
                            Vector2 tangentA = new Vector2(-hitNormal.Y, hitNormal.X);
                            Vector2 tangent = (tangentA.Dot(toCore) >= 0.0f) ? tangentA : -tangentA;

                            // Berikan dorongan meluncur tangensial sepanjang kontur asteroid + pantulan keluar dinding
                            vel.Value += (tangent * 620.0f + hitNormal * 240.0f) * dt;

                            if (distCore > swarm.OrbitDistance + 50.0f)
                            {
                                swarm.State = SwarmState.Returning;
                            }
                        }

                        // Anti-terjepit di ceruk sempit: jika kecepatan sangat rendah saat bersentuhan dengan asteroid
                        if (vel.Value.LengthSquared() < 70.0f * 70.0f)
                        {
                            Vector2 unstuckDir = hitNormal.LengthSquared() > 0.01f ? hitNormal : (pos.Value - _nearbyAsteroids[a].CachedPosition).Normalized();
                            Vector2 slideSide = new Vector2(-unstuckDir.Y, unstuckDir.X);
                            vel.Value += unstuckDir * 320.0f + slideSide * 280.0f;
                            swarm.State = SwarmState.Returning;
                        }
                    }
                    break;
                }
            }
        });

        OrbitingCount = orbitCount;
        LaunchedCount = launchCount;
        ReturningCount = returnCount;
        EnemyCount = enemyCount;
    }

    private void ComputeSectorTargetQuotas(int totalDefenders, int totalEnemies)
    {
        Span<float> weights = stackalloc float[SectorCount];
        float weightSum = 0.0f;

        for (int s = 0; s < SectorCount; s++)
        {
            float w = (totalEnemies > 0) ? 0.22f : 1.0f;

            if (totalEnemies > 0)
            {
                int prevSec = (s + SectorCount - 1) % SectorCount;
                int nextSec = (s + 1) % SectorCount;

                float directEnemy = _sectorEnemyCounts[s];
                float positiveThreat = MathF.Max(0.0f, _sectorThreatScores[s]);
                float neighborEnemy = (_sectorEnemyCounts[prevSec] + _sectorEnemyCounts[nextSec]) * 0.35f;

                w += directEnemy * 2.8f + positiveThreat * 1.8f + neighborEnemy * 1.4f;
            }

            weights[s] = w;
            weightSum += w;
        }

        int allocated = 0;
        for (int s = 0; s < SectorCount; s++)
        {
            int q = (int)MathF.Floor((weights[s] / MathF.Max(0.001f, weightSum)) * totalDefenders);
            _sectorTargetQuotas[s] = q;
            allocated += q;
        }

        int remainder = totalDefenders - allocated;
        while (remainder > 0)
        {
            int bestSec = 0;
            float bestPriority = -99999.0f;
            for (int s = 0; s < SectorCount; s++)
            {
                float priority = weights[s] - _sectorTargetQuotas[s] * (weightSum / totalDefenders);
                if (priority > bestPriority)
                {
                    bestPriority = priority;
                    bestSec = s;
                }
            }
            _sectorTargetQuotas[bestSec]++;
            remainder--;
        }
    }

    private static int AngleToSector(float angleRad)
    {
        float norm = angleRad + Mathf.Pi;
        if (norm < 0.0f) norm += Mathf.Tau;
        if (norm >= Mathf.Tau) norm -= Mathf.Tau;
        int sec = (int)(norm / SectorArc);
        return Math.Clamp(sec, 0, SectorCount - 1);
    }

    private static float SectorToCenterAngle(int sector)
    {
        return (sector + 0.5f) * SectorArc - Mathf.Pi;
    }
}
