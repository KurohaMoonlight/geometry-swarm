using System;
using System.Collections.Generic;
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
/// - Menerapkan aturan mutlak: Drone APAPUN yang dilontarkan (SwarmState.Launched) dan menabrak
///   target apapun (Proyektil Musuh, Orbit Musuh, Core Musuh, atau Asteroid) PASTI MATI (SwarmState.Dead),
///   dan HANYA kembali (SwarmState.Returning) apabila MISS (tidak menabrak apapun sampai durasi habis).
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
    private readonly Entity[] _defenderEntities = new Entity[4096];
    private readonly int[] _neighborBuffer = new int[32];
    private int _activeDefenderCount = 0;

    // Spatial Grid untuk Musuh Aktif (Targeting & Intersepsi Swarm Pemain)
    private readonly SpatialHashGrid _enemySpatialGrid = new(cellSize: 32.0f, maxEntities: 4096, tableSize: 2048);
    private readonly Vector2[] _enemyPositions = new Vector2[4096];
    private readonly Vector2[] _enemyVelocities = new Vector2[4096];
    private readonly Entity[] _launchedEnemyEntities = new Entity[4096];
    private int _activeEnemyCount = 0;

    // Spatial Grid untuk Swarm Pemain yang Sedang Meluncur (Launched Projectiles)
    private readonly SpatialHashGrid _launchedSpatialGrid = new(cellSize: 28.0f, maxEntities: 4096, tableSize: 2048);
    private readonly Vector2[] _launchedPositions = new Vector2[4096];
    private readonly int[] _launchedRoles = new int[4096];
    private readonly bool[] _launchedHitConsumed = new bool[4096];
    private readonly Entity[] _launchedPlayerEntities = new Entity[4096];
    private int _activeLaunchedCount = 0;

    // Spatial Grid untuk Lapisan Perisai Orbit Pemain (Ablative Swarm Armor)
    private readonly SpatialHashGrid _playerOrbitShieldGrid = new(cellSize: 32.0f, maxEntities: 4096, tableSize: 2048);
    private readonly Entity[] _playerOrbitEntities = new Entity[4096];
    private int _activePlayerOrbitCount = 0;

    // Spatial Grid untuk Lapisan Perisai Orbit Musuh (Ablative Swarm Armor Musuh)
    private readonly SpatialHashGrid _enemyOrbitShieldGrid = new(cellSize: 32.0f, maxEntities: 4096, tableSize: 2048);
    private readonly Entity[] _enemyOrbitEntities = new Entity[4096];
    private int _activeEnemyOrbitCount = 0;

    public readonly struct ClashEvent
    {
        public readonly Vector2 Position;
        public readonly bool DropScrap;
        public ClashEvent(Vector2 position, bool dropScrap = true)
        {
            Position = position;
            DropScrap = dropScrap;
        }
    }

    public readonly List<ClashEvent> ClashEvents = new(128);
    public int EnemyCoreHitsThisFrame { get; set; } = 0;
    public int PlayerCoreHitsThisFrame { get; set; } = 0;
    public Vector2 LastPlayerHitVelocity { get; set; } = Vector2.Zero;
    public Vector2 LastEnemyHitVelocity { get; set; } = Vector2.Zero;

    private readonly int[] _enemyScanBuffer = new int[32];
    private readonly int[] _launchedHitBuffer = new int[16];

    // Buffer asteroid terdekat yang sudah disaring sekali per frame (Zero-GC, Zero-Interop di dalam loop)
    private readonly Asteroid[] _nearbyAsteroids = new Asteroid[16];
    private int _nearbyAsteroidCount = 0;

    public float SeparationWeight { get; set; } = 88.0f;
    public float OrbitSpringWeight { get; set; } = 5.6f;
    public float OrbitTangentialWeight { get; set; } = 250.0f;
    public float Damping { get; set; } = 0.94f;
    public float MaxOrbitSpeed { get; set; } = 500.0f;

    // Parameter 3D Tilted Orbital Ring (Berputar relatif terhadap arah kursor)
    public float RingTiltAspect { get; set; } = 0.25f;
    public float RingCantAngle { get; set; } = 0.20f;
    public float RingSpringStiffness { get; set; } = 16.5f;
    private float _ringCursorAngle = 0.0f;
    private bool _ringAngleInitialized = false;
    private float _enemyRingAimAngle = 0.0f;
    private bool _enemyRingAngleInitialized = false;

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

    public void Update(
        float delta,
        Vector2 anchorPos,
        Vector2 anchorVelocity,
        Vector2 enemyCorePos,
        Vector2 cursorThreatPos,
        bool isBoostActive = false,
        bool isGigaDrillActive = false,
        float playerCoreRadius = 26.0f,
        float enemyCoreRadius = 42.0f,
        Vector2 enemyCoreVelocity = default,
        Vector2 enemyAimDirection = default)
    {
        float dt = MathF.Min(delta, 0.033f);
        _organicWaveTime += dt;
        _sectorRebalanceTimer += dt;

        // Perbarui orientasi sumbu Cincin 3D agar berputar mulus terhadap arah kursor
        Vector2 toCursorAim = cursorThreatPos - anchorPos;
        if (toCursorAim.LengthSquared() > 4.0f)
        {
            float targetCursorAngle = toCursorAim.Angle();
            if (!_ringAngleInitialized)
            {
                _ringCursorAngle = targetCursorAngle;
                _ringAngleInitialized = true;
            }
            else
            {
                _ringCursorAngle = Mathf.LerpAngle(_ringCursorAngle, targetCursorAngle, MathF.Min(1.0f, 12.0f * dt));
            }
        }

        float ringAxisAngle = _ringCursorAngle + RingCantAngle;
        Vector2 ringMinorDir = new Vector2(MathF.Cos(ringAxisAngle), MathF.Sin(ringAxisAngle));
        Vector2 ringMajorDir = new Vector2(-ringMinorDir.Y, ringMinorDir.X);
        float coreRadiusOffset = MathF.Max(0.0f, playerCoreRadius - 16.0f);

        // Orientasi Cincin 3D Musuh: Dihaluskan terhadap arah bidik penambangan atau arah terbang
        float targetEnemyAimAngle;
        if (enemyAimDirection.LengthSquared() > 0.01f)
        {
            targetEnemyAimAngle = enemyAimDirection.Angle();
        }
        else if (enemyCoreVelocity.LengthSquared() > 16.0f)
        {
            targetEnemyAimAngle = enemyCoreVelocity.Angle();
        }
        else if (!_enemyRingAngleInitialized)
        {
            targetEnemyAimAngle = (anchorPos - enemyCorePos).Angle();
        }
        else
        {
            targetEnemyAimAngle = _enemyRingAimAngle;
        }

        if (!_enemyRingAngleInitialized)
        {
            _enemyRingAimAngle = targetEnemyAimAngle;
            _enemyRingAngleInitialized = true;
        }
        else
        {
            _enemyRingAimAngle = Mathf.LerpAngle(_enemyRingAimAngle, targetEnemyAimAngle, MathF.Min(1.0f, 10.0f * dt));
        }

        float enemyAxisAngle = _enemyRingAimAngle + RingCantAngle;
        Vector2 enemyRingMinorDir = new Vector2(MathF.Cos(enemyAxisAngle), MathF.Sin(enemyAxisAngle));
        Vector2 enemyRingMajorDir = new Vector2(-enemyRingMinorDir.Y, enemyRingMinorDir.X);
        float enemyCoreRadiusOffset = MathF.Max(0.0f, enemyCoreRadius - 16.0f);

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
            Vector2 dPlayer = ast.CachedPosition - anchorPos;
            Vector2 dEnemy = ast.CachedPosition - enemyCorePos;
            bool nearPlayer = MathF.Abs(dPlayer.X) <= checkDist && MathF.Abs(dPlayer.Y) <= checkDist;
            bool nearEnemy = MathF.Abs(dEnemy.X) <= checkDist && MathF.Abs(dEnemy.Y) <= checkDist;
            if (nearPlayer || nearEnemy)
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

        _enemySpatialGrid.Clear();
        _activeEnemyCount = 0;

        _launchedSpatialGrid.Clear();
        _activeLaunchedCount = 0;

        _playerOrbitShieldGrid.Clear();
        _activePlayerOrbitCount = 0;

        _enemyOrbitShieldGrid.Clear();
        _activeEnemyOrbitCount = 0;

        ClashEvents.Clear();
        EnemyCoreHitsThisFrame = 0;
        PlayerCoreHitsThisFrame = 0;

        Vector2 gridOrigin = anchorPos - new Vector2(GridWidth * CellSize * 0.5f, GridHeight * CellSize * 0.5f);

        _world.Query(in _boidQuery, (Entity entity, ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
        {
            if (swarm.State == SwarmState.Dead) return;

            Vector2 local = pos.Value - gridOrigin;
            int cx = Math.Clamp((int)(local.X * InvCellSize), 1, GridWidth - 2);
            int cy = Math.Clamp((int)(local.Y * InvCellSize), 1, GridHeight - 2);
            int cellIdx = cy * GridWidth + cx;

            if (swarm.OwnerId > 0)
            {
                if (swarm.State == SwarmState.Launched)
                {
                    _enemyCellGrid[cellIdx]++;
                    enemyCount++;
                    if (_activeEnemyCount < _enemyPositions.Length)
                    {
                        int eIdx = _activeEnemyCount++;
                        _enemyPositions[eIdx] = pos.Value;
                        _enemyVelocities[eIdx] = vel.Value;
                        _launchedEnemyEntities[eIdx] = entity;
                        _enemySpatialGrid.Insert(eIdx, pos.Value);
                        swarm.SpatialEntityId = eIdx;
                    }
                    return;
                }
                else if (swarm.State == SwarmState.Orbiting)
                {
                    // Masukkan ke Perisai Orbit Musuh (Ablative Shield Core Musuh)
                    if (_activeEnemyOrbitCount < 4096)
                    {
                        int eoIdx = _activeEnemyOrbitCount++;
                        _enemyOrbitEntities[eoIdx] = entity;
                        _enemyOrbitShieldGrid.Insert(eoIdx, pos.Value);
                        swarm.SpatialEntityId = eoIdx;
                    }
                    return;
                }
            }

            if (swarm.State == SwarmState.Launched)
            {
                if (_activeLaunchedCount < _launchedPositions.Length)
                {
                    int lIdx = _activeLaunchedCount++;
                    _launchedPositions[lIdx] = pos.Value;
                    _launchedRoles[lIdx] = (int)swarm.Role;
                    _launchedPlayerEntities[lIdx] = entity;
                    _launchedHitConsumed[lIdx] = false;
                    _launchedSpatialGrid.Insert(lIdx, pos.Value);
                    swarm.SpatialEntityId = lIdx;
                }
            }
            else if (swarm.State == SwarmState.Orbiting)
            {
                // Masukkan ke Perisai Orbit Pemain (Ablative Swarm Armor - menahan tembakan musuh)
                if (_activePlayerOrbitCount < 4096)
                {
                    int poIdx = _activePlayerOrbitCount++;
                    _playerOrbitEntities[poIdx] = entity;
                    _playerOrbitShieldGrid.Insert(poIdx, pos.Value);
                    swarm.SpatialEntityId = poIdx;
                }

                if (swarm.Role == SwarmRole.Guardian)
                {
                    if (_activeDefenderCount < _defenderPositions.Length)
                    {
                        int defIdx = _activeDefenderCount++;
                        _defenderPositions[defIdx] = pos.Value;
                        _defenderVelocities[defIdx] = vel.Value;
                        _defenderEntities[defIdx] = entity;
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
                int ccy = Math.Clamp((int)(localCursor.Y * InvCellSize), 2, GridHeight - 3);
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
        if (!isBoostActive && _activeDefenderCount > 0 && _sectorRebalanceTimer >= 0.04f)
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
        float effectivePlayerCoreHitRadius = MathF.Max(22.0f, playerCoreRadius + 6.0f);
        float effectiveEnemyCoreHitRadius = MathF.Max(22.0f, enemyCoreRadius);

        _world.Query(in _boidQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
        {
            if (swarm.State == SwarmState.Dead) return;

            if (swarm.OwnerId > 0)
            {
                if (swarm.State == SwarmState.Orbiting)
                {
                    // =====================================================================
                    // FORMASI CINCIN 3D MIRING (3D TILTED ORBITAL RING) UNTUK ARMADA RIVAL HIVE
                    // - Seksama & simetris 100% dengan formasi cincin 3D pemain
                    // - Menggunakan kedalaman depthZ = sin(theta) untuk Back/Front Ring depth
                    // =====================================================================
                    float angularSpd = MathF.Abs(swarm.OrbitSpeed) > 0.2f ? MathF.Abs(swarm.OrbitSpeed) : 1.75f;
                    swarm.OrbitAngle = Mathf.Wrap(swarm.OrbitAngle + angularSpd * dt, 0.0f, Mathf.Tau);

                    float waveJitter = MathF.Sin(_organicWaveTime * 2.2f + swarm.PersonalAngleOffset * 8.0f) * 0.035f;
                    float effectiveTheta = swarm.OrbitAngle + waveJitter;
                    float cosT = MathF.Cos(effectiveTheta);
                    float sinT = MathF.Sin(effectiveTheta);

                    float depthZ = sinT;
                    swarm.OrbitDepth = depthZ;

                    float baseRoleRadius = swarm.Role switch
                    {
                        SwarmRole.Guardian => 80.0f,
                        SwarmRole.MiningDroid or SwarmRole.ScannerDrone => 68.0f,
                        _ => 74.0f
                    };
                    float majorR = baseRoleRadius + Math.Clamp(swarm.PersonalRadialOffset, -6.0f, 6.0f) + enemyCoreRadiusOffset;
                    float minorR = majorR * RingTiltAspect;

                    Vector2 ringOffset = enemyRingMajorDir * (majorR * cosT) + enemyRingMinorDir * (minorR * sinT);
                    Vector2 targetRingPos = enemyCorePos + ringOffset;

                    Vector2 ringTangentVel = (-enemyRingMajorDir * (majorR * sinT) + enemyRingMinorDir * (minorR * cosT)) * angularSpd;

                    Vector2 posError = targetRingPos - pos.Value;
                    Vector2 desiredVel = enemyCoreVelocity + ringTangentVel + posError * RingSpringStiffness;
                    vel.Value = vel.Value.Lerp(desiredVel, MathF.Min(1.0f, 14.0f * dt));

                    float maxEnemyOrbitSpd = MathF.Max(MaxOrbitSpeed, enemyCoreVelocity.Length() + 360.0f);
                    if (vel.Value.LengthSquared() > maxEnemyOrbitSpd * maxEnemyOrbitSpd)
                    {
                        vel.Value = vel.Value.Normalized() * maxEnemyOrbitSpd;
                    }
                    pos.Value += vel.Value * dt;

                    // Skala Perspektif 3D berdasarkan kedalaman depthZ
                    float baseRoleScale = swarm.Role switch
                    {
                        SwarmRole.Guardian => 0.72f,
                        SwarmRole.ScannerDrone => 0.60f,
                        SwarmRole.MiningDroid => 0.62f,
                        _ => 0.55f
                    };
                    float depthScaleMult = depthZ < 0.0f
                        ? Mathf.Lerp(0.92f, 0.70f, -depthZ)
                        : Mathf.Lerp(0.92f, 1.12f, depthZ);
                    vis.Scale = Mathf.Lerp(vis.Scale, baseRoleScale * depthScaleMult, MathF.Min(1.0f, dt * 12.0f));

                    // Orientasi hadap unit musuh: Mining Droid membidik arah target penambangan, lainnya mengikuti aliran cincin
                    if (swarm.Role == SwarmRole.MiningDroid)
                    {
                        Vector2 aimDir = (enemyAimDirection.LengthSquared() > 0.01f) ? enemyAimDirection : ringTangentVel;
                        if (aimDir.LengthSquared() > 0.01f)
                        {
                            float desiredAngle = aimDir.Angle() + Mathf.Pi * 0.5f;
                            swarm.SpinAngle = Mathf.LerpAngle(swarm.SpinAngle, desiredAngle, MathF.Min(1.0f, 24.0f * dt));
                        }
                    }
                    else if (MathF.Abs(swarm.SpinRate) <= 0.01f)
                    {
                        Vector2 flowDir = ringTangentVel.LengthSquared() > 1.0f ? ringTangentVel : vel.Value;
                        if (flowDir.LengthSquared() > 1.0f)
                        {
                            float desiredFaceAngle = flowDir.Angle() + Mathf.Pi * 0.5f;
                            swarm.SpinAngle = Mathf.LerpAngle(swarm.SpinAngle, desiredFaceAngle, MathF.Min(1.0f, 18.0f * dt));
                        }
                    }

                    // Efek Redup Kedalaman (Depth Dimming) & Warna Role Musuh
                    if (swarm.HitFlashTimer > 0.0f)
                    {
                        swarm.HitFlashTimer -= dt;
                        vis.Color = new Color(5.0f, 5.0f, 5.0f, 1.0f);
                    }
                    else
                    {
                        Color baseCol = swarm.Role switch
                        {
                            SwarmRole.MiningDroid => new Color(3.6f, 1.8f, 0.4f, 1.0f),
                            SwarmRole.Guardian => new Color(2.6f, 0.45f, 3.2f, 1.0f),
                            SwarmRole.DrillDrone => new Color(4.2f, 0.6f, 0.2f, 1.0f),
                            SwarmRole.GigaShuriken => new Color(3.8f, 0.3f, 1.8f, 1.0f),
                            SwarmRole.CrescentBoomerang => new Color(0.4f, 2.8f, 1.2f, 1.0f),
                            _ => EnemyGlowColor
                        };

                        if (depthZ < 0.0f)
                        {
                            vis.Color = new Color(baseCol.R * 0.58f, baseCol.G * 0.58f, baseCol.B * 0.58f, 0.72f);
                        }
                        else
                        {
                            vis.Color = baseCol;
                        }
                    }

                    // Kolisi voxel & seluncur tangensial untuk unit Orbiting musuh (identik 1:1 dengan pemain)
                    Vector2 toEnemyAnchor = enemyCorePos - pos.Value;
                    float distEnemyAnchor = toEnemyAnchor.Length();
                    if (distEnemyAnchor <= swarm.OrbitDistance + 220.0f)
                    {
                        for (int a = 0; a < nearbyCount; a++)
                        {
                            if (_nearbyAsteroids[a].ResolveCircleVoxelCollision(ref pos.Value, ref vel.Value, DroneVoxelRadius, out Vector2 hitNormal, 0.0f))
                            {
                                if (distEnemyAnchor > 4.0f)
                                {
                                    Vector2 dirToAnchor = toEnemyAnchor / distEnemyAnchor;
                                    Vector2 n1 = new Vector2(-hitNormal.Y, hitNormal.X);
                                    Vector2 n2 = -n1;
                                    Vector2 slideTangent = (n1.Dot(dirToAnchor) >= n2.Dot(dirToAnchor)) ? n1 : n2;
                                    float slideSpeed = MathF.Max(vel.Value.Length(), 310.0f);
                                    vel.Value = (slideTangent * 0.82f + hitNormal * 0.18f).Normalized() * slideSpeed;
                                }
                                break;
                            }
                        }
                    }
                    return;
                }

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

                // 1. Midfield clash dengan Swarm Pemain yang Sedang Meluncur
                // Defender di Ballistic Shield Giga Drill punya 5 HP, sedangkan semua drone proyektil biasa 1-hit mati
                int launchHits = _launchedSpatialGrid.QueryNeighbors(pos.Value, 24.0f, _launchedHitBuffer);
                if (launchHits > 0)
                {
                    for (int h = 0; h < launchHits; h++)
                    {
                        int lIdx = _launchedHitBuffer[h];
                        if (lIdx >= 0 && lIdx < _activeLaunchedCount && !_launchedHitConsumed[lIdx])
                        {
                            Entity lEnt = _launchedPlayerEntities[lIdx];
                            if (_world.IsAlive(lEnt))
                            {
                                ref var lSwarm = ref _world.Get<SwarmUnitComponent>(lEnt);
                                if (lSwarm.State == SwarmState.Launched)
                                {
                                    _launchedHitConsumed[lIdx] = true;

                                    if (lSwarm.Role == SwarmRole.Guardian && isGigaDrillActive)
                                    {
                                        lSwarm.Health--;
                                        lSwarm.HitFlashTimer = 0.12f;
                                        if (_world.Has<VisualComponent>(lEnt))
                                        {
                                            ref var lVis = ref _world.Get<VisualComponent>(lEnt);
                                            lVis.Color = new Color(5.0f, 5.0f, 5.0f, 1.0f);
                                        }

                                        if (lSwarm.Health <= 0)
                                        {
                                            lSwarm.State = SwarmState.Dead;
                                            ClashEvents.Add(new ClashEvent(pos.Value, dropScrap: true));
                                        }
                                        else
                                        {
                                            ClashEvents.Add(new ClashEvent(pos.Value, dropScrap: false));
                                        }
                                    }
                                    else
                                    {
                                        // Drone pemain yang dilontarkan dan menabrak peluru musuh PASTI MATI
                                        lSwarm.State = SwarmState.Dead;
                                        ClashEvents.Add(new ClashEvent(pos.Value, dropScrap: true));
                                    }

                                    swarm.State = SwarmState.Dead; // Peluru musuh selalu hancur saat menabrak
                                    return;
                                }
                            }
                        }
                    }
                }

                // 2. Tabrakan dengan Perisai Swarm Pengorbit Pemain (Ablative Swarm Armor)
                int shieldHits = _playerOrbitShieldGrid.QueryNeighbors(pos.Value, 22.0f, _neighborBuffer);
                if (shieldHits > 0)
                {
                    int bestCandidateIdx = -1;
                    int bestPriority = -1;
                    float closestDistSq = float.MaxValue;

                    for (int s = 0; s < shieldHits; s++)
                    {
                        int pIdx = _neighborBuffer[s];
                        if (pIdx < 0 || pIdx >= _activePlayerOrbitCount) continue;
                        Entity pEnt = _playerOrbitEntities[pIdx];
                        if (!_world.IsAlive(pEnt)) continue;
                        ref var pSwarm = ref _world.Get<SwarmUnitComponent>(pEnt);
                        if (pSwarm.State != SwarmState.Orbiting) continue;

                        ref var pPos = ref _world.Get<PositionComponent>(pEnt);
                        float distSq = pos.Value.DistanceSquaredTo(pPos.Value);
                        if (distSq > 22.0f * 22.0f) continue;

                        int priority = pSwarm.Role switch
                        {
                            SwarmRole.Guardian => 3,
                            SwarmRole.DartNeedle or SwarmRole.DrillDrone or SwarmRole.GigaShuriken or SwarmRole.CrescentBoomerang => 2,
                            _ => 1
                        };

                        if (priority > bestPriority || (priority == bestPriority && distSq < closestDistSq))
                        {
                            bestPriority = priority;
                            bestCandidateIdx = pIdx;
                            closestDistSq = distSq;
                        }
                    }

                    if (bestCandidateIdx != -1)
                    {
                        Entity targetEnt = _playerOrbitEntities[bestCandidateIdx];
                        ApplyDamage(targetEnt, pos.Value, vel.Value);
                        swarm.State = SwarmState.Dead;
                        return;
                    }
                }

                // 3. Kolisi langsung dengan Defender (Guardians) di lingkaran perisai kapal
                int hits = _defenderSpatialGrid.QueryNeighbors(pos.Value, 22.0f, _neighborBuffer);
                if (hits > 0)
                {
                    for (int d = 0; d < hits; d++)
                    {
                        int defIdx = _neighborBuffer[d];
                        if (defIdx >= 0 && defIdx < _activeDefenderCount)
                        {
                            Entity defEnt = _defenderEntities[defIdx];
                            if (!_world.IsAlive(defEnt)) continue;
                            ref var defPos = ref _world.Get<PositionComponent>(defEnt);
                            if (pos.Value.DistanceSquaredTo(defPos.Value) <= 22.0f * 22.0f)
                            {
                                ApplyDamage(defEnt, pos.Value, vel.Value);
                                swarm.State = SwarmState.Dead;
                                return;
                            }
                        }
                    }
                }

                // 4. Tabrakan langsung ke Player Core (menggunakan radius aktual Core)
                if (distToCore <= effectivePlayerCoreHitRadius)
                {
                    PlayerCoreHitsThisFrame++;
                    LastPlayerHitVelocity = vel.Value;
                    swarm.State = SwarmState.Dead;
                    ClashEvents.Add(new ClashEvent(pos.Value, dropScrap: false));
                    return;
                }

                if (swarm.LifeTime >= swarm.MaxLifeTime)
                {
                    swarm.State = SwarmState.Dead;
                    return;
                }

                pos.Value += vel.Value * dt;

                // Proyektil musuh yang menabrak batu asteroid PASTI MATI
                for (int a = 0; a < nearbyCount; a++)
                {
                    if (_nearbyAsteroids[a].ResolveCircleVoxelCollision(ref pos.Value, ref vel.Value, DroneVoxelRadius, impulseWeight: 0.04f))
                    {
                        _nearbyAsteroids[a].ApplyKineticHitAtWorldPos(pos.Value, 1.0f, out _, out _);
                        ClashEvents.Add(new ClashEvent(pos.Value, dropScrap: false));
                        swarm.State = SwarmState.Dead;
                        return;
                    }
                }
                return;
            }

            if (MathF.Abs(swarm.SpinRate) > 0.01f
                && !isBoostActive
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

                    // Saat Sacrificial Plasma Boost [SPACE] aktif, semua unit Orbiting dikunci penuh
                    // ke Kubah Parabola di depan Core oleh SwarmSimulation.UpdateDartBoost!
                    if (isBoostActive)
                    {
                        swarm.OrbitDepth = 1.0f;
                        return;
                    }

                    // =====================================================================
                    // FORMASI CINCIN 3D MIRING (3D TILTED ORBITAL RING) UNTUK SELURUH ROLE
                    // - Berputar mulus mengikuti arah kursor (ringMajorDir & ringMinorDir)
                    // - Mencakup semua unit termasuk Guardian (⛨)
                    // - Kedalaman depthZ = sin(theta) in [-1..+1]:
                    //   * depthZ < 0: Lewat di BELAKANG Core (Back Ring ZIndex=1, agak redup & mengecil)
                    //   * depthZ >= 0: Lewat di DEPAN Core (Front Ring ZIndex=3, menyala terang & membesar)
                    // =====================================================================
                    float angularSpd = MathF.Abs(swarm.OrbitSpeed) > 0.2f ? MathF.Abs(swarm.OrbitSpeed) : 1.75f;
                    swarm.OrbitAngle = Mathf.Wrap(swarm.OrbitAngle + angularSpd * dt, 0.0f, Mathf.Tau);

                    float waveJitter = MathF.Sin(_organicWaveTime * 2.2f + swarm.PersonalAngleOffset * 8.0f) * 0.035f;
                    float effectiveTheta = swarm.OrbitAngle + waveJitter;
                    float cosT = MathF.Cos(effectiveTheta);
                    float sinT = MathF.Sin(effectiveTheta);

                    float depthZ = sinT;
                    swarm.OrbitDepth = depthZ;

                    float baseRoleRadius = swarm.Role switch
                    {
                        SwarmRole.Guardian => 80.0f,
                        SwarmRole.MiningDroid or SwarmRole.ScannerDrone => 68.0f,
                        _ => 74.0f
                    };
                    float majorR = baseRoleRadius + Math.Clamp(swarm.PersonalRadialOffset, -6.0f, 6.0f) + coreRadiusOffset;
                    float minorR = majorR * RingTiltAspect;

                    Vector2 ringOffset = ringMajorDir * (majorR * cosT) + ringMinorDir * (minorR * sinT);
                    Vector2 targetRingPos = anchorPos + ringOffset;

                    // Kecepatan tangensial elips analitik (memberikan efek foreshortening 3D alami di ujung elips)
                    Vector2 ringTangentVel = (-ringMajorDir * (majorR * sinT) + ringMinorDir * (minorR * cosT)) * angularSpd;

                    Vector2 posError = targetRingPos - pos.Value;
                    Vector2 desiredVel = anchorVelocity + ringTangentVel + posError * RingSpringStiffness;
                    vel.Value = vel.Value.Lerp(desiredVel, MathF.Min(1.0f, 14.0f * dt));

                    float dynamicMaxOrbitSpd = MathF.Max(MaxOrbitSpeed, anchorVelocity.Length() + 360.0f);
                    if (vel.Value.LengthSquared() > dynamicMaxOrbitSpd * dynamicMaxOrbitSpd)
                    {
                        vel.Value = vel.Value.Normalized() * dynamicMaxOrbitSpd;
                    }

                    // Skala Perspektif 3D berdasarkan kedalaman depthZ
                    float baseRoleScale = swarm.Role switch
                    {
                        SwarmRole.Guardian => 0.72f,
                        SwarmRole.ScannerDrone => 0.60f,
                        SwarmRole.MiningDroid => 0.62f,
                        _ => 0.55f
                    };
                    float depthScaleMult = depthZ < 0.0f
                        ? Mathf.Lerp(0.92f, 0.70f, -depthZ)
                        : Mathf.Lerp(0.92f, 1.12f, depthZ);
                    vis.Scale = Mathf.Lerp(vis.Scale, baseRoleScale * depthScaleMult, MathF.Min(1.0f, dt * 12.0f));

                    // Efek Redup Kedalaman (Depth Dimming) saat di belakang Core vs Terang di depan Core
                    if (swarm.HitFlashTimer > 0.0f)
                    {
                        swarm.HitFlashTimer -= dt;
                        vis.Color = new Color(5.0f, 5.0f, 5.0f, 1.0f);
                    }
                    else
                    {
                        Color baseCol = vis.BaseColor;
                        Color targetDepthColor;
                        if (depthZ < 0.0f)
                        {
                            // Saat di belakang Core: redupkan di bawah ambang bloom HDR (~25%) & turunkan alpha
                            float backFactor = -depthZ;
                            float rgbMult = Mathf.Lerp(0.68f, 0.25f, backFactor);
                            float alphaMult = Mathf.Lerp(0.88f, 0.50f, backFactor);
                            targetDepthColor = new Color(
                                baseCol.R * rgbMult,
                                baseCol.G * rgbMult,
                                baseCol.B * rgbMult,
                                alphaMult
                            );
                        }
                        else
                        {
                            // Saat di depan Core: menyala penuh hingga 114% di puncak depan
                            float frontFactor = depthZ;
                            float rgbMult = Mathf.Lerp(0.82f, 1.14f, frontFactor);
                            targetDepthColor = new Color(
                                baseCol.R * rgbMult,
                                baseCol.G * rgbMult,
                                baseCol.B * rgbMult,
                                1.0f
                            );
                        }
                        vis.Color = vis.Color.Lerp(targetDepthColor, MathF.Min(1.0f, dt * 14.0f));
                    }

                    // Orientasi hadap unit mengikuti aliran cincin (MiningDroid tetap siap membidik kursor)
                    if (swarm.Role == SwarmRole.MiningDroid)
                    {
                        Vector2 toCursor = cursorThreatPos - pos.Value;
                        if (toCursor.LengthSquared() > 4.0f)
                        {
                            float desiredAngle = toCursor.Angle() + Mathf.Pi * 0.5f;
                            swarm.SpinAngle = Mathf.LerpAngle(swarm.SpinAngle, desiredAngle, MathF.Min(1.0f, 24.0f * dt));
                        }
                    }
                    else if (MathF.Abs(swarm.SpinRate) <= 0.01f)
                    {
                        Vector2 flowDir = ringTangentVel.LengthSquared() > 1.0f ? ringTangentVel : vel.Value;
                        if (flowDir.LengthSquared() > 1.0f)
                        {
                            float desiredFaceAngle = flowDir.Angle() + Mathf.Pi * 0.5f;
                            swarm.SpinAngle = Mathf.LerpAngle(swarm.SpinAngle, desiredFaceAngle, MathF.Min(1.0f, 14.0f * dt));
                        }
                    }
                    break;

                case SwarmState.ChargingDrill:
                    orbitCount++;
                    return;

                case SwarmState.BoostThruster:
                case SwarmState.BoostBrake:
                    orbitCount++;
                    return;

                case SwarmState.Launched:
                    launchCount++;

                    // Jika unit ini adalah bagian dari formasi Super Giga Drill yang sedang aktif,
                    // seluruh posisi, rotasi, dan kolisi diatur oleh SwarmSimulation.UpdateActiveGigaDrill!
                    if (isGigaDrillActive && (swarm.Role == SwarmRole.Guardian || (swarm.Role == SwarmRole.DrillDrone && swarm.FormationIndex >= 0)))
                    {
                        return;
                    }

                    swarm.LifeTime += dt;

                    // 1. Jika drone pemain yang dilontarkan ini sudah bertabrakan dengan peluru musuh -> PASTI MATI!
                    if (swarm.SpatialEntityId >= 0 && swarm.SpatialEntityId < _activeLaunchedCount && _launchedHitConsumed[swarm.SpatialEntityId])
                    {
                        swarm.State = SwarmState.Dead;
                        return;
                    }

                    // =================================================================
                    // LOGIKA INTERSEPSI DINAMIS: PRIORITASKAN MUSUH YANG MENGARAH KE KITA
                    // =================================================================
                    int candCount = _enemySpatialGrid.QueryNeighbors(pos.Value, 280.0f, _enemyScanBuffer);
                    int bestEnemyIdx = -1;
                    float highestThreatScore = -1.0f;
                    Vector2 bestEnemyPos = Vector2.Zero;
                    Vector2 bestEnemyVel = Vector2.Zero;

                    Vector2 currentVel = vel.Value;
                    Vector2 forwardDir = currentVel.LengthSquared() > 10.0f ? currentVel.Normalized() : swarm.TargetDirection;

                    for (int c = 0; c < candCount; c++)
                    {
                        int eIdx = _enemyScanBuffer[c];
                        if (eIdx < 0 || eIdx >= _activeEnemyCount) continue;
                        Entity eEnt = _launchedEnemyEntities[eIdx];
                        if (!_world.IsAlive(eEnt)) continue;
                        ref var eSwarm = ref _world.Get<SwarmUnitComponent>(eEnt);
                        if (eSwarm.State == SwarmState.Dead) continue;

                        Vector2 ePos = _enemyPositions[eIdx];
                        Vector2 eVel = _enemyVelocities[eIdx];

                        Vector2 toEnemy = ePos - pos.Value;
                        float distToEnemy = toEnemy.Length();
                        if (distToEnemy < 1.0f) continue;
                        Vector2 dirToEnemy = toEnemy / distToEnemy;

                        // A. SENSOR ARC / CONE (~75 derajat):
                        // Musuh dari arah samping/belakang tidak dikejar agar BISA LOLOS (defender berguna)!
                        float fwdDot = forwardDir.Dot(dirToEnemy);
                        if (fwdDot < 0.25f) continue;

                        // B. MUSUH MENGARAH KE KITA (Approach Rate):
                        Vector2 toOurCore = anchorPos - ePos;
                        float distToCore = toOurCore.Length();
                        if (distToCore < 1.0f) continue;
                        Vector2 dirToOurCore = toOurCore / distToCore;

                        // Kecepatan mendekati Core kita
                        float approachSpeed = eVel.Dot(dirToOurCore);

                        // C. ZONA PERTAHANAN DEFENDER:
                        // Jika musuh sudah masuk perimeter dekat (< ShieldRadius + 20px),
                        // serahkan ke Guardian defender agar swarm tidak berbalik arah 180 derajat ke markas.
                        if (distToCore < ShieldRadius + 20.0f) continue;

                        // Evaluasi musuh yang aktif melaju ke Core kita!
                        if (approachSpeed > 15.0f)
                        {
                            float tti = distToCore / approachSpeed; // Time-to-Impact

                            // Skor Ancaman: prioritas tinggi untuk TTI kecil (segera tabrak core) + jarak terdekat
                            float threatScore = (350.0f / (tti + 0.15f)) + (180.0f / (distToEnemy + 25.0f));

                            if (threatScore > highestThreatScore)
                            {
                                highestThreatScore = threatScore;
                                bestEnemyIdx = eIdx;
                                bestEnemyPos = ePos;
                                bestEnemyVel = eVel;
                            }
                        }
                    }

                    Vector2 desiredDirection;
                    if (bestEnemyIdx != -1)
                    {
                        // Lead Pursuit: Prediksi posisi temu musuh
                        float hitTime = Math.Clamp(pos.Value.DistanceTo(bestEnemyPos) / BulletMaxSpeed, 0.04f, 0.35f);

                        // Jitter dispersion alami: sedikit ketidaksempurnaan matematis
                        // agar tidak 100% laser-lock (musuh berkecepatan tinggi atau menyilang berpeluang lolos)
                        float dispersion = MathF.Sin(pos.Value.X * 0.04f + swarm.OrbitAngle) * 10.0f;
                        Vector2 leadPos = bestEnemyPos + bestEnemyVel * hitTime + new Vector2(-forwardDir.Y, forwardDir.X) * (dispersion * 0.06f);

                        desiredDirection = (leadPos - pos.Value).Normalized();
                    }
                    else
                    {
                        // Tidak ada ancaman musuh yang mengarah ke kita -> Lanjut menuju Core/target sasaran!
                        if (swarm.TargetPosition.DistanceSquaredTo(Vector2.Zero) > 1.0f)
                        {
                            Vector2 toTargetPos = swarm.TargetPosition - pos.Value;
                            desiredDirection = toTargetPos.LengthSquared() > 16.0f ? toTargetPos.Normalized() : swarm.TargetDirection;
                        }
                        else
                        {
                            desiredDirection = swarm.TargetDirection;
                        }
                    }

                    // Smooth Steering dengan laju belok finite (12.0 rad/s)
                    swarm.TargetDirection = swarm.TargetDirection.Slerp(desiredDirection, MathF.Min(1.0f, 12.0f * dt)).Normalized();

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

                    // 5. Cek tabrakan dengan Perisai Swarm Pengorbit Core Musuh (Ablative Swarm Armor Musuh)
                    // Aturan mutlak: Drone APAPUN yang dilontarkan dan menabrak unit musuh PASTI MATI!
                    int enemyShieldHits = _enemyOrbitShieldGrid.QueryNeighbors(pos.Value, 22.0f, _neighborBuffer);
                    if (enemyShieldHits > 0)
                    {
                        for (int s = 0; s < enemyShieldHits; s++)
                        {
                            int eoIdx = _neighborBuffer[s];
                            if (eoIdx >= 0 && eoIdx < _activeEnemyOrbitCount)
                            {
                                Entity eoEnt = _enemyOrbitEntities[eoIdx];
                                if (!_world.IsAlive(eoEnt)) continue;
                                ref var eoPos = ref _world.Get<PositionComponent>(eoEnt);
                                if (pos.Value.DistanceSquaredTo(eoPos.Value) > 22.0f * 22.0f) continue;

                                if (ApplyDamage(eoEnt, pos.Value, vel.Value))
                                {
                                    swarm.State = SwarmState.Dead;
                                    return;
                                }
                            }
                        }
                    }

                    // 6. Cek tabrakan langsung ke Core Musuh (menggunakan radius aktual EnemyCore)
                    // Aturan mutlak: Drone APAPUN yang menabrak Core Musuh PASTI MATI!
                    Vector2 toEnemyCore = enemyCorePos - pos.Value;
                    float distToEnemyCore = toEnemyCore.Length();
                    if (distToEnemyCore <= effectiveEnemyCoreHitRadius)
                    {
                        EnemyCoreHitsThisFrame++;
                        LastEnemyHitVelocity = vel.Value;
                        swarm.State = SwarmState.Dead;
                        ClashEvents.Add(new ClashEvent(pos.Value, dropScrap: false));
                        return;
                    }

                    // 7. Hanya jika MISS (tidak menabrak apapun sampai durasi terbang habis), kembali ke Core!
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
                    if (isBoostActive)
                    {
                        return;
                    }
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

                    swarm.OrbitDepth = 1.0f;
                    float returnRadius = 74.0f + coreRadiusOffset;
                    if (distRet <= returnRadius + 30.0f)
                    {
                        Vector2 relEntry = pos.Value - anchorPos;
                        float pMaj = relEntry.Dot(ringMajorDir) / MathF.Max(1.0f, returnRadius);
                        float pMin = relEntry.Dot(ringMinorDir) / MathF.Max(1.0f, returnRadius * RingTiltAspect);
                        swarm.OrbitAngle = Mathf.Wrap(MathF.Atan2(pMin, pMaj), 0.0f, Mathf.Tau);
                        swarm.OrbitDepth = MathF.Sin(swarm.OrbitAngle);
                        swarm.State = SwarmState.Orbiting;
                        swarm.LifeTime = 0.0f;
                        swarm.MaxHealth = SwarmUnitComponent.GetMaxHealthForRole(swarm.Role, SwarmState.Orbiting);
                        swarm.Health = swarm.MaxHealth;
                        swarm.HitFlashTimer = 0.0f;
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

            // Cek kolisi terhadap asteroid terdekat
            for (int a = 0; a < nearbyCount; a++)
            {
                float impulseWeight = (swarm.State == SwarmState.Launched) ? 0.08f : 0.0f;
                if (_nearbyAsteroids[a].ResolveCircleVoxelCollision(ref pos.Value, ref vel.Value, DroneVoxelRadius, out Vector2 hitNormal, impulseWeight))
                {
                    if (swarm.State == SwarmState.Launched)
                    {
                        // Aturan mutlak: Drone APAPUN yang dilontarkan dan menabrak asteroid PASTI MATI!
                        _nearbyAsteroids[a].ApplyKineticHitAtWorldPos(pos.Value, 1.0f, out _, out _);
                        ClashEvents.Add(new ClashEvent(pos.Value, dropScrap: false));
                        swarm.State = SwarmState.Dead;
                        return;
                    }
                    else if (swarm.OwnerId == 0) // Drone pemain yang sedang Orbiting atau Returning
                    {
                        Vector2 toCore = anchorPos - pos.Value;
                        float distCore = toCore.Length();

                        if (distCore > 4.0f)
                        {
                            Vector2 dirToCore = toCore / distCore;
                            Vector2 n1 = new Vector2(-hitNormal.Y, hitNormal.X);
                            Vector2 n2 = -n1;

                            // Pilih arah tangen yang paling searah menuju Core agar drone memutari sudut batu
                            Vector2 slideTangent = (n1.Dot(dirToCore) >= n2.Dot(dirToCore)) ? n1 : n2;
                            float slideSpeed = MathF.Max(vel.Value.Length(), 310.0f);
                            vel.Value = (slideTangent * 0.82f + hitNormal * 0.18f).Normalized() * slideSpeed;
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

    private bool ApplyDamage(Entity targetEnt, Vector2 hitPos, Vector2 impactVelocity)
    {
        if (!_world.IsAlive(targetEnt)) return false;
        ref var targetSwarm = ref _world.Get<SwarmUnitComponent>(targetEnt);
        if (targetSwarm.State == SwarmState.Dead) return false;

        targetSwarm.Health--;
        targetSwarm.HitFlashTimer = 0.12f;

        // Impuls kinetik flinch/knockback pada unit yang tertabrak
        if (_world.Has<VelocityComponent>(targetEnt))
        {
            ref var targetVel = ref _world.Get<VelocityComponent>(targetEnt);
            targetVel.Value += impactVelocity * 0.40f;
        }

        if (_world.Has<VisualComponent>(targetEnt))
        {
            ref var targetVis = ref _world.Get<VisualComponent>(targetEnt);
            targetVis.Color = new Color(5.0f, 5.0f, 5.0f, 1.0f);
        }

        if (targetSwarm.Health <= 0)
        {
            targetSwarm.State = SwarmState.Dead;
            ClashEvents.Add(new ClashEvent(hitPos, dropScrap: true));
            return true;
        }
        else
        {
            ClashEvents.Add(new ClashEvent(hitPos, dropScrap: false));
            return true;
        }
    }

    private void ComputeSectorTargetQuotas(int totalDefenders, int totalEnemies)
    {
        if (totalEnemies <= 0)
        {
            int basePerSector = totalDefenders / SectorCount;
            int rem = totalDefenders % SectorCount;
            for (int s = 0; s < SectorCount; s++)
            {
                _sectorTargetQuotas[s] = basePerSector + (s < rem ? 1 : 0);
            }
            return;
        }

        int baselineTotal = Math.Max(0, (int)(totalDefenders * 0.24f));
        int baseEach = baselineTotal / SectorCount;
        int dynamicPool = totalDefenders - (baseEach * SectorCount);

        float[] smoothedWeights = new float[SectorCount];
        float weightSum = 0.0f;

        for (int s = 0; s < SectorCount; s++)
        {
            int prev = (s + SectorCount - 1) % SectorCount;
            int next = (s + 1) % SectorCount;
            float w = _sectorEnemyCounts[s] * 1.0f
                    + _sectorEnemyCounts[prev] * 0.42f
                    + _sectorEnemyCounts[next] * 0.42f;
            smoothedWeights[s] = w;
            weightSum += w;
        }

        int allocated = 0;
        for (int s = 0; s < SectorCount; s++)
        {
            int share = weightSum > 0.001f
                ? (int)MathF.Round((smoothedWeights[s] / weightSum) * dynamicPool)
                : (dynamicPool / SectorCount);
            _sectorTargetQuotas[s] = baseEach + share;
            allocated += _sectorTargetQuotas[s];
        }

        int diff = totalDefenders - allocated;
        while (diff != 0)
        {
            int targetSec = 0;
            float bestScore = -999999.0f;
            for (int s = 0; s < SectorCount; s++)
            {
                float score = diff > 0 ? smoothedWeights[s] : _sectorTargetQuotas[s];
                if (score > bestScore)
                {
                    bestScore = score;
                    targetSec = s;
                }
            }

            if (diff > 0)
            {
                _sectorTargetQuotas[targetSec]++;
                diff--;
            }
            else if (_sectorTargetQuotas[targetSec] > 0)
            {
                _sectorTargetQuotas[targetSec]--;
                diff++;
            }
            else
            {
                break;
            }
        }
    }

    private static int AngleToSector(float angleRad)
    {
        float normalized = (angleRad + Mathf.Pi) / Mathf.Tau;
        normalized -= MathF.Floor(normalized);
        int sector = (int)(normalized * SectorCount);
        return Math.Clamp(sector, 0, SectorCount - 1);
    }

    private static float SectorToCenterAngle(int sector)
    {
        return (sector + 0.5f) * SectorArc - Mathf.Pi;
    }
}
