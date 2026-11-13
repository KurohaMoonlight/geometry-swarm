using System;
using Arch.Core;
using GeometrySwarm.Core.ECS.Components;
using GeometrySwarm.Core.Physics;
using GeometrySwarm.Gameplay;
using Godot;

namespace GeometrySwarm.Core.ECS.Systems;

/// <summary>
/// Sistem gerak & state machine kawanan (Orbiting, Launched, Returning, ChargingDrill, DrillExhaust).
/// Mengimplementasikan:
/// 1. Pembagian 12 Sektor (Pizza Slices) di sekitar Core yang memindai Spatial Grid untuk menghitung
///    Skor Ancaman = (Jumlah Musuh di Sektor - Jumlah Defender di Sektor).
/// 2. Alokasi sudut orbit dinamis dari sektor aman ke sektor berancaman tinggi.
/// 3. Defender "bodoh" & organik yang hanya mengikuti gaya pegas sederhana (radial + sudut orbit ~120px)
///    serta saling menjaga jarak (separation via SpatialHashGrid) dengan kecepatan lebih lambat dari swarm,
///    sehingga membentuk barisan tameng rapat namun organik dengan celah alami.
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

    // SpatialHashGrid untuk Separation antar-Defender dan deteksi tumbukan peluru musuh vs Defender
    private readonly SpatialHashGrid _defenderSpatialGrid = new(cellSize: 24.0f, maxEntities: 4096, tableSize: 2048);
    private readonly Vector2[] _defenderPositions = new Vector2[4096];
    private readonly Vector2[] _defenderVelocities = new Vector2[4096];
    private readonly int[] _neighborBuffer = new int[32];
    private int _activeDefenderCount = 0;

    // Parameter Orbiting Attacker (Swarm Cepat & Lincah)
    public float SeparationWeight { get; set; } = 88.0f;
    public float OrbitSpringWeight { get; set; } = 5.6f;
    public float OrbitTangentialWeight { get; set; } = 250.0f;
    public float Damping { get; set; } = 0.94f;
    public float MaxOrbitSpeed { get; set; } = 500.0f;

    // ===============================================================
    // PARAMETER DEFENDER / GUARDIAN (Organik, Reaktif, Pegas Sederhana)
    // ===============================================================
    public const int SectorCount = 12;
    public const float SectorArc = Mathf.Tau / SectorCount;
    public float ShieldRadius { get; set; } = 88.0f;                // Radius perisai organik diperketat untuk skala swarm baru
    public float ThreatScanRadius { get; set; } = 750.0f;           // Radius pemindaian Spatial Grid di sekitar markas
    public float ShieldRadialSpring { get; set; } = 4.8f;           // Gaya pegas radial menjaga jarak ring dari Core
    public float ShieldAngularSpring { get; set; } = 7.5f;          // Gaya pegas sudut reaktif meluncur cepat ke sektor ancaman
    public float DefenderSeparationRadius { get; set; } = 18.0f;    // Jarak jaga antar Defender (separation) disesuaikan
    public float DefenderSeparationWeight { get; set; } = 260.0f;   // Kekuatan tolak-menolak agar berbaris rapat tapi organik
    public float MaxGuardianSpeed { get; set; } = 340.0f;           // Lincah berputar merespon ancaman!

    // Array 12 Sektor untuk pemindaian ancaman Core
    private readonly int[] _sectorEnemyCounts = new int[SectorCount];
    private readonly int[] _sectorDefenderCounts = new int[SectorCount];
    private readonly float[] _sectorThreatScores = new float[SectorCount];
    private readonly int[] _sectorTargetQuotas = new int[SectorCount];
    private readonly int[] _sectorAssignedCounts = new int[SectorCount];
    private float _sectorRebalanceTimer = 0.0f;
    private float _organicWaveTime = 0.0f;

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
    public int EnemyCount { get; private set; }

    // Warna saat drone menjadi peluru berenergi tinggi (Blazing White-Gold)
    private static readonly Color BulletGlowColor = new(3.2f, 2.6f, 1.2f, 1.0f);
    // Warna saat drone terbang pulang (Emerald Glow)
    private static readonly Color ReturnGlowColor = new(0.8f, 2.6f, 1.8f, 1.0f);
    // Warna proyektil/swarm musuh (Crimson-Violet)
    private static readonly Color EnemyGlowColor = new(3.4f, 0.35f, 0.9f, 1.0f);

    public BoidSystem(World world)
    {
        _world = world;
        _boidQuery = new QueryDescription().WithAll<PositionComponent, VelocityComponent, SwarmUnitComponent, VisualComponent>();
    }

    /// <summary>
    /// Ekspos skor ancaman per sektor untuk keperluan debug/visualisasi jika dibutuhkan.
    /// </summary>
    public ReadOnlySpan<float> GetSectorThreatScores() => _sectorThreatScores;

    public void Update(float delta, Vector2 anchorPos, Vector2 anchorVelocity, Vector2 cursorThreatPos)
    {
        float dt = MathF.Min(delta, 0.033f);
        _organicWaveTime += dt;
        _sectorRebalanceTimer += dt;

        float decayOrbit = MathF.Pow(Damping, dt * 60.0f);
        float decayGuardian = MathF.Pow(0.89f, dt * 60.0f); // Redaman inersia Defender yang berat & lambat
        float decayBullet = MathF.Pow(0.992f, dt * 60.0f);
        float decayReturn = MathF.Pow(0.95f, dt * 60.0f);

        int orbitCount = 0;
        int launchCount = 0;
        int returnCount = 0;
        int enemyCount = 0;

        // =========================================================================
        // 1. ISI SPATIAL GRID (Attacker Density, Enemy Grid Cells, & Defender Hash)
        // =========================================================================
        Array.Clear(_densityGrid, 0, _densityGrid.Length);
        Array.Clear(_enemyCellGrid, 0, _enemyCellGrid.Length);
        Array.Clear(_sectorEnemyCounts, 0, SectorCount);
        Array.Clear(_sectorDefenderCounts, 0, SectorCount);
        Array.Clear(_sectorAssignedCounts, 0, SectorCount);

        _defenderSpatialGrid.Clear();
        _activeDefenderCount = 0;

        // Grid spasial lokal dinamis berpusat di posisi Core (anchorPos)
        // Memastikan koordinat tidak pernah overflow/clamp saat pemain menjelajahi map 12.000 x 12.000
        Vector2 gridOrigin = anchorPos - new Vector2(GridWidth * CellSize * 0.5f, GridHeight * CellSize * 0.5f);
        var activeAsteroids = Asteroid.ActiveAsteroids;

        _world.Query(in _boidQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm) =>
        {
            if (swarm.State == SwarmState.Dead) return;

            Vector2 local = pos.Value - gridOrigin;
            int cx = Math.Clamp((int)(local.X * InvCellSize), 1, GridWidth - 2);
            int cy = Math.Clamp((int)(local.Y * InvCellSize), 1, GridHeight - 2);
            int cellIdx = cy * GridWidth + cx;

            // Musuh (OwnerId > 0) dimasukkan ke sel Spatial Grid untuk dipindai Core
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

                    // Hitung posisi aktual Defender berada di sektor mana saat ini
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

        // Jika belum ada unit musuh aktif di arena, gunakan posisi kursor di sekitar Core
        // sebagai simulasi sinyal ancaman pada sel Spatial Grid agar alokasi sektor tetap bisa diuji kapan saja.
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
                // Sebarkan ancaman virtual ke sel-sel Spatial Grid di sekitar kursor
                _enemyCellGrid[ccy * GridWidth + ccx] += 25;
                _enemyCellGrid[ccy * GridWidth + (ccx - 1)] += 10;
                _enemyCellGrid[ccy * GridWidth + (ccx + 1)] += 10;
                _enemyCellGrid[(ccy - 1) * GridWidth + ccx] += 10;
                _enemyCellGrid[(ccy + 1) * GridWidth + ccx] += 10;
            }
        }

        // =========================================================================
        // 2. CORE MEMINDAI SEL SPATIAL GRID DI RADIUS SEKITAR MARKAS (12 SEKTOR PIZZA)
        //    Skor Ancaman Sektor = Jumlah Musuh di Sektor - Jumlah Defender di Sektor
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

        // Hitung Skor Ancaman per Sektor (Jumlah Musuh - Jumlah Defender)
        for (int s = 0; s < SectorCount; s++)
        {
            _sectorThreatScores[s] = _sectorEnemyCounts[s] - _sectorDefenderCounts[s];
        }

        // =========================================================================
        // 3. ALOKASI SUDUT ORBIT DINAMIS OLEH CORE
        //    Menggeser sebagian Defender dari sektor aman menuju sektor dengan skor ancaman tinggi
        // =========================================================================
        if (_activeDefenderCount > 0 && _sectorRebalanceTimer >= 0.02f)
        {
            _sectorRebalanceTimer = 0.0f;
            ComputeSectorTargetQuotas(_activeDefenderCount, totalScannedEnemies);

            // Geser alokasi Defender dari sektor yang surplus (aman) ke sektor yang defisit (ancaman tinggi)
            _world.Query(in _boidQuery, (ref SwarmUnitComponent swarm) =>
            {
                if (swarm.State != SwarmState.Orbiting || swarm.Role != SwarmRole.Guardian || swarm.OwnerId != 0)
                    return;

                int curSec = Math.Clamp(swarm.AssignedSector, 0, SectorCount - 1);
                if (_sectorAssignedCounts[curSec] > _sectorTargetQuotas[curSec])
                {
                    // Cari sektor dengan defisit terbesar (membutuhkan Defender paling mendesak)
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

                // Core memberikan sudut orbit target sesuai sektor + sedikit osilasi organik
                float sectorCenterAngle = SectorToCenterAngle(swarm.AssignedSector);
                float dumbWander = MathF.Sin(_organicWaveTime * 1.4f + swarm.OrbitAngle) * 0.14f;
                swarm.ShieldTargetAngle = sectorCenterAngle + swarm.PersonalAngleOffset + dumbWander;
            });
        }

        // =========================================================================
        // 4. PROSES GERAK UNIT BERDASARKAN STATE MACHINE & GAYA PEGAS SEDERHANA
        // =========================================================================
        _world.Query(in _boidQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
        {
            if (swarm.State == SwarmState.Dead) return;

            // -----------------------------------------------------------------
            // LOGIKA SWARM / AMUNISI MUSUH (OwnerId > 0)
            // Menyerang ke arah Core, dapat ditahan Defender atau lolos lewat celah!
            // -----------------------------------------------------------------
            if (swarm.OwnerId > 0)
            {
                swarm.LifeTime += dt;
                vis.Color = EnemyGlowColor;

                Vector2 toCore = anchorPos - pos.Value;
                float distToCore = toCore.Length();
                Vector2 dirToCore = distToCore > 0.001f ? toCore / distToCore : Vector2.Right;

                // Kecepatan proyektil musuh menuju Core
                vel.Value += dirToCore * (620.0f * dt);
                vel.Value *= decayBullet;
                if (vel.Value.LengthSquared() > 420.0f * 420.0f)
                {
                    vel.Value = vel.Value.Normalized() * 420.0f;
                }

                swarm.SpinAngle = vel.Value.Angle() + Mathf.Pi * 0.5f;

                // Cek tumbukan dengan Defender menggunakan SpatialHashGrid
                int hits = _defenderSpatialGrid.QueryNeighbors(pos.Value, 16.0f, _neighborBuffer);
                if (hits > 0)
                {
                    // Tertahan oleh barisan tameng Defender!
                    swarm.State = SwarmState.Dead;
                    return;
                }

                // Jika lolos melewati celah Defender (musuh hoki) dan mencapai Core atau habis waktu
                if (distToCore < 28.0f || swarm.LifeTime >= swarm.MaxLifeTime)
                {
                    swarm.State = SwarmState.Dead;
                    return;
                }

                pos.Value += vel.Value * dt;

            // =========================================================================
            // PEMBATAS FISIK ASTEROID RAKSASA: SWARM TIDAK BISA MENEMBUS BATU
            // =========================================================================
            for (int a = 0; a < activeAsteroids.Count; a++)
            {
                var ast = activeAsteroids[a];
                if (!GodotObject.IsInstanceValid(ast)) continue;

                float r = ast.Radius + 6.0f;
                Vector2 diff = pos.Value - ast.GlobalPosition;

                // Cek AABB cepat untuk eliminasi 95% asteroid jauh tanpa hitung akar
                if (MathF.Abs(diff.X) > r || MathF.Abs(diff.Y) > r) continue;

                float distSq = diff.LengthSquared();
                if (distSq < r * r)
                {
                    float dist = MathF.Sqrt(distSq);
                    Vector2 normal = dist > 0.001f ? diff / dist : Vector2.Right;

                    // Tolak paksa posisi drone ke luar permukaan asteroid
                    pos.Value = ast.GlobalPosition + normal * r;

                    // Batalkan komponen kecepatan yang mengarah ke dalam batu (slide di permukaan)
                    float inwardVel = vel.Value.Dot(normal);
                    if (inwardVel < 0.0f)
                    {
                        vel.Value -= normal * inwardVel;
                    }

                    if (swarm.State == SwarmState.Launched)
                    {
                        swarm.State = SwarmState.Returning;
                        swarm.LifeTime = 0.0f;
                    }
                    break;
                }
            }
                return;
            }

            // Perbarui putaran spin visual mandiri (Shuriken berputar, Bor berputar)
            if (MathF.Abs(swarm.SpinRate) > 0.01f && swarm.State != SwarmState.ChargingDrill && swarm.State != SwarmState.DrillExhaust)
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
                        // -----------------------------------------------------------------
                        // DEFENDER (GUARDIAN): Bodoh, Lambat, & Organik
                        // Unit tidak tahu soal musuh. Hanya mengikuti:
                        // (a) Gaya pegas radial ke radius perisai (~120px dari Core)
                        // (b) Gaya pegas sudut menuju ShieldTargetAngle dari Core
                        // (c) Gaya Separation (jaga jarak antar Defender) via SpatialHashGrid
                        // Tidak ada tambahan kecepatan instan anchorVelocity agar Defender
                        // tertinggal secara alami di belakang swarm saat Core berpindah!
                        // -----------------------------------------------------------------
                        Vector2 fromCore = pos.Value - anchorPos;
                        float distFromCore = fromCore.Length();
                        Vector2 radialOut = distFromCore > 0.001f ? fromCore / distFromCore : Vector2.Right;
                        Vector2 tangent = new Vector2(-radialOut.Y, radialOut.X);

                        // Radius perisai organik (~120px +- offset personal & napas lambat)
                        float breathing = MathF.Sin(_organicWaveTime * 1.8f + swarm.OrbitAngle) * 6.5f;
                        float targetRadius = ShieldRadius + swarm.PersonalRadialOffset + breathing;

                        // (a) Gaya Pegas Radial menuju targetRadius (120px)
                        float radialError = targetRadius - distFromCore;
                        Vector2 force = radialOut * (radialError * ShieldRadialSpring);

                        // (b) Gaya Pegas Sudut Orbit (mendorong sepanjang garis singgung menuju sudut sektor)
                        float currentAngle = fromCore.Angle();
                        float angleError = Mathf.AngleDifference(currentAngle, swarm.ShieldTargetAngle);
                        float clampedAngleErr = Mathf.Clamp(angleError, -2.0f, 2.0f);
                        force += tangent * (clampedAngleErr * ShieldRadius * ShieldAngularSpring);

                        // Ikuti pergerakan kapal induk Core agar tidak tertinggal jauh
                        force += anchorVelocity * 1.55f;

                        // Jika terlalu jauh dari Core (misal Core berakselerasi kencang), tarik kembali ke radius perisai
                        if (distFromCore > ShieldRadius * 1.35f)
                        {
                            force -= radialOut * ((distFromCore - ShieldRadius) * (ShieldRadialSpring * 1.15f));
                        }

                        // (c) Gaya Separation antar-Defender agar membentuk kerumunan tameng rapat tapi bercelah alami
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

                        // Integrasi kecepatan dengan redaman responsif
                        vel.Value = (vel.Value + force * dt) * decayGuardian;
                        if (vel.Value.LengthSquared() > MaxGuardianSpeed * MaxGuardianSpeed)
                        {
                            vel.Value = vel.Value.Normalized() * MaxGuardianSpeed;
                        }

                        // Orientasi kurung perisai ]] berputar organik (sedikit goyang/wobble bodoh)
                        float desiredFaceAngle = currentAngle + Mathf.Pi * 0.5f + MathF.Sin(_organicWaveTime * 2.5f + swarm.OrbitAngle) * 0.12f;
                        swarm.SpinAngle = Mathf.LerpAngle(swarm.SpinAngle, desiredFaceAngle, MathF.Min(1.0f, 7.5f * dt));
                    }
                    else
                    {
                        // ----------------------------------------------------
                        // ATTACKER: Awan Boids Lincah (Sampai Lebih Dulu dari Defender)
                        // ----------------------------------------------------
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

                        // Mining Droid (Tapal Kuda U): Selalu menghadap ke arah kursor dengan bukaan U ke kursor
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

                // ==========================================
                // 2. STATE CHARGING DRILL (Dikelola SwarmSimulation)
                // ==========================================
                case SwarmState.ChargingDrill:
                    orbitCount++;
                    break;

                // ==========================================
                // 3. STATE LAUNCHED (Swarm sebagai Peluru)
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
                // 4. STATE DRILL EXHAUST (Semburan Roket Belakang Giga Drill -> Mati)
                // ==========================================
                case SwarmState.DrillExhaust:
                    launchCount++;
                    // Integrasi posisi & kematian exhaust ditangani di SwarmSimulation
                    return;

                // ==========================================
                // 5. STATE RETURNING (Terbang Pulang ke Core)
                // ==========================================
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

            pos.Value += vel.Value * dt;

            // =========================================================================
            // PEMBATAS FISIK ASTEROID RAKSASA: SWARM TIDAK BISA MENEMBUS BATU
            // =========================================================================
            for (int a = 0; a < activeAsteroids.Count; a++)
            {
                var ast = activeAsteroids[a];
                if (!GodotObject.IsInstanceValid(ast)) continue;

                float r = ast.Radius + 6.0f;
                Vector2 diff = pos.Value - ast.GlobalPosition;

                // Cek AABB cepat untuk eliminasi 95% asteroid jauh tanpa hitung akar
                if (MathF.Abs(diff.X) > r || MathF.Abs(diff.Y) > r) continue;

                float distSq = diff.LengthSquared();
                if (distSq < r * r)
                {
                    float dist = MathF.Sqrt(distSq);
                    Vector2 normal = dist > 0.001f ? diff / dist : Vector2.Right;

                    // Tolak paksa posisi drone ke luar permukaan asteroid
                    pos.Value = ast.GlobalPosition + normal * r;

                    // Batalkan komponen kecepatan yang mengarah ke dalam batu (slide di permukaan)
                    float inwardVel = vel.Value.Dot(normal);
                    if (inwardVel < 0.0f)
                    {
                        vel.Value -= normal * inwardVel;
                    }

                    if (swarm.State == SwarmState.Launched)
                    {
                        swarm.State = SwarmState.Returning;
                        swarm.LifeTime = 0.0f;
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
            // Bobot dasar kecil agar sektor aman tetap punya sedikit penjaga patroli longgar
            float w = (totalEnemies > 0) ? 0.22f : 1.0f;

            if (totalEnemies > 0)
            {
                int prevSec = (s + SectorCount - 1) % SectorCount;
                int nextSec = (s + 1) % SectorCount;

                // Skor ancaman langsung di sektor ini + rembesan ancaman dari sektor sebelah (flank)
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

        // Bagikan sisa pembulatan ke sektor dengan skor ancaman tertinggi
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
        // Normalisasi sudut [-Pi, Pi] ke [0, Tau)
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
