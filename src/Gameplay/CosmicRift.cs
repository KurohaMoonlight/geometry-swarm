using System;
using System.Collections.Generic;
using Godot;

namespace GeometrySwarm.Gameplay;

/// <summary>
/// Anomali Astrofisika Sabuk Dalam: Cosmic Rift (Supermassive Black Hole & White Hole).
/// Siklus & Fisika Jarak Dekat:
/// 1. Dormant (Black Hole Aktif):
///    - Menjaga Horizon Exclusion Barrier agar tidak pernah berada di belakang/tertutup asteroid maupun bintang.
///    - Menerapkan gaya pasang-surut jarak dekat (Tidal Spaghettification, Voxel Accretion Erosion pada asteroid,
///      serta Coronal Plasma Siphoning & Instability Drain pada Bintang Aether di sekitarnya).
/// 2. Charging (8 Detik): Petir dan lidah api menjilat di horizon, hisapan akresi & orbit meningkat ekstrem.
/// 3. Supernova Blast (3 Detik): Ledakan supernova memancarkan gelombang kejut fisik (wavefront) setiap 0.5s
///    yang mendorong asteroid & bintang secara seamless sehingga asteroid saling bertabrakan beruntun.
/// 4. White Hole (7.5 Detik): Menolak materi keluar dan merakit Supercharged Asteroid baru (Option A).
/// </summary>
public partial class CosmicRift : Node2D
{
    public enum RiftState
    {
        Dormant = 0,
        Charging = 1,
        SupernovaBlast = 2,
        WhiteHole = 3
    }

    private enum WhiteHoleGenesisPhase : byte
    {
        Inactive = 0,
        EjectingAndAssemblingParts = 1,
        FiringGenesisTachyonBeam = 2,
        Completed = 3
    }

    private struct EjectedRockPartChunk
    {
        public int PartIndex;
        public Vector2 StartWorldPos;
        public Vector2 ControlWorldPos;
        public Vector2 TargetWorldPos;
        public float Progress;
        public float Duration;
        public float Rotation;
        public float RotSpeed;
        public float Radius;
        public Vector2[] LocalPoly;
    }

    private struct RiftAccretionVoxel
    {
        public Vector2 WorldPos;
        public Vector2 Velocity;
        public Color Color;
        public float Size;
        public float OrbitAngle;
        public float OrbitRadius;
        public float InfallSpeed;
        public float AngularSpeed;
        public bool HasCapturedOrbit;
        public float Lifetime;
        public float MaxLifetime;
        public float Rotation;
        public float RotSpeed;
    }

    [Export] public float OrbitRadius { get; set; } = 1550.0f;
    [Export] public float OrbitAngle { get; set; } = 0.0f;
    [Export] public float OrbitSpeed { get; set; } = 0.012f; // ~0.68 deg/sec
    [Export] public int RiftId { get; set; } = 0;

    public RiftState State { get; private set; } = RiftState.Dormant;

    // Durasi Fase
    public const float ChargeDuration = 8.0f;
    public const float SupernovaDuration = 3.0f;
    public const float WhiteHoleDuration = 7.5f;
    public const float DormantDuration = 22.0f;

    // Ukuran quad visual efek distorsi lensa gravitasi & radius mulut horizon (~98 px)
    public const float VisualQuadSize = 960.0f;
    public const float MouthHorizonRadius = VisualQuadSize * 0.102f; // ~97.9 px

    private float _stateTimer = 0.0f;
    private float _shockwavePulseTimer = 0.0f;
    private float _chargeProgress = 0.0f;
    private float _whiteHolePolarity = 0.0f;

    // Ide 2: Gravitational Polarity Inversion Ripple (Efek Visual Megah & Ringan)
    private float _inversionRippleTimer = 0.0f;
    private const float InversionRippleDuration = 2.6f;
    private bool _inversionRippleActive = false;

    // Pengikisan Voxel & Siphoning Jarak Dekat Black Hole (Zero GC)
    private float _accretionErodeTimer = 0.0f;
    private const float AccretionErodeInterval = 0.16f;
    private float _starSiphonStreamTimer = 0.0f;
    private readonly List<(Vector2 worldCenter, Asteroid.VoxelType type)> _erodedVoxelBuffer = new(8);
    private readonly List<RiftAccretionVoxel> _accretionVoxels = new(64);
    private readonly Vector2[] _voxelQuadBuffer = new Vector2[4];
    private readonly Vector2[] _voxelOutlineBuffer = new Vector2[5];

    // State Perakitan Asteroid White Hole & Genesis Tachyon Beam
    private WhiteHoleGenesisPhase _genesisPhase = WhiteHoleGenesisPhase.Inactive;
    private bool _pendingForceGenesis = false;
    private Asteroid? _assemblingAsteroid = null;
    private Vector2 _assemblyAnchorOffset = new(440.0f, -140.0f);
    private int _nextPartToEject = 0;
    private int _totalPartsToEject = 12;
    private int _lockedPartsCount = 0;
    private float _partEjectTimer = 0.0f;
    private const float PartEjectInterval = 0.24f;
    private float _tachyonBeamTimer = 0.0f;
    private const float TachyonBeamDuration = 1.15f;
    private float _visualPulseTime = 0.0f;
    private readonly List<EjectedRockPartChunk> _flyingParts = new(16);
    private readonly Vector2[] _drawPolyBuffer = new Vector2[8];
    private readonly Vector2[] _drawOutlineBuffer = new Vector2[9];

    private BackBufferCopy? _backBuffer;
    private ColorRect? _riftRect;
    private ShaderMaterial? _riftMaterial;

    public override void _Ready()
    {
        ZIndex = 4; // Selalu berada di atas Asteroid (ZIndex=0), Core (ZIndex=2), & Swarm (ZIndex=3)
        UpdateOrbitPosition();

        _stateTimer = 0.0f;

        try
        {
            var shader = GD.Load<Shader>("res://visual/cosmic_rift_distortion.gdshader");
            if (shader != null)
            {
                _riftMaterial = new ShaderMaterial { Shader = shader };
                _riftMaterial.SetShaderParameter("black_hole_radius", 0.102f);
                _riftMaterial.SetShaderParameter("lensing_strength", 1.65f);
                _riftMaterial.SetShaderParameter("swirl_strength", 2.40f);
                _riftMaterial.SetShaderParameter("swirl_speed", 0.35f);
                _riftMaterial.SetShaderParameter("photon_ring_width", 0.0030f);
                _riftMaterial.SetShaderParameter("photon_ring_intensity", 5.5f);
                _riftMaterial.SetShaderParameter("gold_bloom_width", 0.0100f);
                _riftMaterial.SetShaderParameter("gold_bloom_intensity", 3.4f);
                _riftMaterial.SetShaderParameter("photon_core_color", new Color(5.8f, 5.6f, 5.2f, 1.0f));
                _riftMaterial.SetShaderParameter("gold_bloom_color", new Color(3.4f, 2.3f, 0.58f, 1.0f));
                _riftMaterial.SetShaderParameter("charge_progress", 0.0f);
                _riftMaterial.SetShaderParameter("white_hole_polarity", 0.0f);

                _backBuffer = new BackBufferCopy
                {
                    Name = "RiftBackBuffer",
                    CopyMode = BackBufferCopy.CopyModeEnum.Rect,
                    Rect = new Rect2(-VisualQuadSize * 0.5f, -VisualQuadSize * 0.5f, VisualQuadSize, VisualQuadSize)
                };
                AddChild(_backBuffer);

                _riftRect = new ColorRect
                {
                    Name = "RiftDistortionQuad",
                    Material = _riftMaterial,
                    CustomMinimumSize = new Vector2(VisualQuadSize, VisualQuadSize),
                    Size = new Vector2(VisualQuadSize, VisualQuadSize),
                    Position = new Vector2(-VisualQuadSize * 0.5f, -VisualQuadSize * 0.5f),
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                    ShowBehindParent = true
                };
                AddChild(_riftRect);
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[CosmicRift._Ready] Gagal inisialisasi shader distorsi: {ex}");
        }
    }

    private void UpdateOrbitPosition()
    {
        Position = new Vector2(MathF.Cos(OrbitAngle), MathF.Sin(OrbitAngle)) * OrbitRadius;
    }

    /// <summary>
    /// Memulai urutan sinematik penuh Cosmic Rift (Tombol [T]):
    /// Black Hole Charging -> Supernova Blast (shockwave & lempar asteroid) -> White Hole -> Rakit Asteroid.
    /// </summary>
    public void StartFullCinematicSequence()
    {
        if (_assemblingAsteroid != null && IsInstanceValid(_assemblingAsteroid) && _assemblingAsteroid.IsAssemblingFromWhiteHole)
        {
            Vector2 prevOut = (_assemblingAsteroid.GlobalPosition - GlobalPosition).Normalized();
            _assemblingAsteroid.InfuseSuperchargedResourcesAndLaunch(prevOut * 140.0f);
            _assemblingAsteroid = null;
        }

        _pendingForceGenesis = true;
        State = RiftState.Charging;
        _stateTimer = 0.0f;
        _chargeProgress = 0.0f;
        _whiteHolePolarity = 0.0f;
        _flyingParts.Clear();
        _genesisPhase = WhiteHoleGenesisPhase.Inactive;

        _riftMaterial?.SetShaderParameter("black_hole_radius", 0.102f);
        _riftMaterial?.SetShaderParameter("charge_progress", 0.0f);
        _riftMaterial?.SetShaderParameter("white_hole_polarity", 0.0f);

        if (_riftRect != null) _riftRect.Visible = true;
        if (_backBuffer != null) _backBuffer.Visible = true;

        QueueRedraw();
    }

    /// <summary>
    /// Memaksa Cosmic Rift langsung masuk ke mode White Hole dan memulai perakitan Supercharged Asteroid.
    /// </summary>
    public void ForceStartWhiteHoleAsteroidGenesis(bool forceDebug = true)
    {
        State = RiftState.WhiteHole;
        _stateTimer = 0.0f;
        _chargeProgress = 0.0f;
        _whiteHolePolarity = 1.0f;
        _riftMaterial?.SetShaderParameter("black_hole_radius", 0.102f);
        _riftMaterial?.SetShaderParameter("charge_progress", 0.0f);
        _riftMaterial?.SetShaderParameter("white_hole_polarity", 1.0f);

        TryBeginWhiteHoleAsteroidGenesis(forceDebug);
        QueueRedraw();
    }

    private void TryBeginWhiteHoleAsteroidGenesis(bool forceSpawn)
    {
        var mgr = AsteroidFieldManager.Instance;
        if (mgr == null) return;

        if (_assemblingAsteroid != null && IsInstanceValid(_assemblingAsteroid) && _assemblingAsteroid.IsAssemblingFromWhiteHole)
        {
            Vector2 prevOut = (_assemblingAsteroid.GlobalPosition - GlobalPosition).Normalized();
            _assemblingAsteroid.InfuseSuperchargedResourcesAndLaunch(prevOut * 140.0f);
            _assemblingAsteroid = null;
        }

        int currentCount = Asteroid.ActiveAsteroids.Count;
        if (!forceSpawn && currentCount >= mgr.TotalAsteroids)
        {
            _genesisPhase = WhiteHoleGenesisPhase.Inactive;
            return;
        }

        Vector2 playerPos = PlayerCore.Instance?.GlobalPosition ?? Vector2.Zero;
        Vector2 toPlayer = playerPos - GlobalPosition;
        Vector2 baseDir = toPlayer.LengthSquared() > 100.0f
            ? toPlayer.Normalized().Rotated(0.42f)
            : GlobalPosition.Normalized();
        if (baseDir.LengthSquared() < 0.01f) baseDir = Vector2.Right;

        _assemblyAnchorOffset = baseDir * 440.0f;
        Vector2 assemblyWorldPos = GlobalPosition + _assemblyAnchorOffset;

        float targetRadius = (float)GD.RandRange(270.0, 340.0);
        _assemblingAsteroid = mgr.SpawnEmptyAsteroidForWhiteHoleAssembly(assemblyWorldPos, targetRadius);

        _totalPartsToEject = _assemblingAsteroid.AssemblyPartCount;
        _nextPartToEject = 0;
        _lockedPartsCount = 0;
        _partEjectTimer = 0.05f;
        _tachyonBeamTimer = 0.0f;
        _flyingParts.Clear();
        _genesisPhase = WhiteHoleGenesisPhase.EjectingAndAssemblingParts;
        QueueRedraw();
    }

    private void UpdateWhiteHoleAsteroidGenesis(float dt, float cameraDistAtten)
    {
        if (_genesisPhase == WhiteHoleGenesisPhase.Inactive || _genesisPhase == WhiteHoleGenesisPhase.Completed)
            return;

        if (_assemblingAsteroid == null || !IsInstanceValid(_assemblingAsteroid))
        {
            _genesisPhase = WhiteHoleGenesisPhase.Inactive;
            _flyingParts.Clear();
            QueueRedraw();
            return;
        }

        _visualPulseTime += dt;

        Vector2 assemblyCenterWorld = GlobalPosition + _assemblyAnchorOffset;
        _assemblingAsteroid.GlobalPosition = assemblyCenterWorld;

        if (_genesisPhase == WhiteHoleGenesisPhase.EjectingAndAssemblingParts)
        {
            if (_nextPartToEject < _totalPartsToEject)
            {
                _partEjectTimer -= dt;
                if (_partEjectTimer <= 0.0f)
                {
                    _partEjectTimer = PartEjectInterval;
                    SpawnNextEjectedRockPart(_nextPartToEject);
                    _nextPartToEject++;
                }
            }

            for (int i = _flyingParts.Count - 1; i >= 0; i--)
            {
                var part = _flyingParts[i];
                part.Progress += dt / MathF.Max(0.05f, part.Duration);
                part.Rotation += part.RotSpeed * dt;
                part.TargetWorldPos = _assemblingAsteroid.GetAssemblyPartWorldCenter(part.PartIndex);

                if (part.Progress >= 1.0f)
                {
                    _assemblingAsteroid.LockInAssembledRockPart(part.PartIndex);
                    _lockedPartsCount++;
                    _flyingParts.RemoveAt(i);

                    if (cameraDistAtten > 0.1f)
                    {
                        PlayerCamera.Instance?.TriggerShake(2.4f * cameraDistAtten);
                    }
                }
                else
                {
                    _flyingParts[i] = part;
                }
            }

            if (_lockedPartsCount >= _totalPartsToEject && _flyingParts.Count == 0)
            {
                _assemblingAsteroid.CompletePureRockAssembly();
                _genesisPhase = WhiteHoleGenesisPhase.FiringGenesisTachyonBeam;
                _tachyonBeamTimer = 0.0f;

                if (cameraDistAtten > 0.1f)
                {
                    PlayerCamera.Instance?.TriggerShake(10.0f * cameraDistAtten);
                }
            }

            QueueRedraw();
        }
        else if (_genesisPhase == WhiteHoleGenesisPhase.FiringGenesisTachyonBeam)
        {
            _tachyonBeamTimer += dt;
            float beamProgress = Mathf.Clamp(_tachyonBeamTimer / TachyonBeamDuration, 0.0f, 1.0f);

            float vibMag = Mathf.Lerp(2.0f, 16.0f, beamProgress * beamProgress);
            Vector2 jitter = new Vector2((GD.Randf() - 0.5f) * vibMag, (GD.Randf() - 0.5f) * vibMag);
            _assemblingAsteroid.GlobalPosition = assemblyCenterWorld + jitter;

            if (cameraDistAtten > 0.05f)
            {
                PlayerCamera.Instance?.TriggerShake(Mathf.Lerp(3.0f, 16.0f, beamProgress) * cameraDistAtten);
            }

            if (_tachyonBeamTimer >= TachyonBeamDuration)
            {
                Vector2 launchDir = _assemblyAnchorOffset.Normalized();
                Vector2 launchVel = launchDir * 320.0f;
                _assemblingAsteroid.GlobalPosition = assemblyCenterWorld;
                _assemblingAsteroid.InfuseSuperchargedResourcesAndLaunch(launchVel);

                // Lontarkan asteroid baru meluncur aman dengan Ghost Phasing ke sabuk luar (r = 2400 px)
                // agar tidak tertahan atau termakan kembali oleh erosi Black Hole!
                _assemblingAsteroid.EnterGhostPhase(GlobalPosition, targetRadius: 2400.0f, duration: 4.5f);

                SwarmSimulation.Instance?.TriggerAetherStarRebirthFx(assemblyCenterWorld, _assemblingAsteroid.Radius * 0.85f);
                if (cameraDistAtten > 0.05f)
                {
                    PlayerCamera.Instance?.TriggerShake(24.0f * cameraDistAtten);
                }

                _assemblingAsteroid = null;
                _genesisPhase = WhiteHoleGenesisPhase.Completed;
            }

            QueueRedraw();
        }
    }

    private void SpawnNextEjectedRockPart(int partIndex)
    {
        if (_assemblingAsteroid == null) return;

        Vector2 targetWorld = _assemblingAsteroid.GetAssemblyPartWorldCenter(partIndex);
        Vector2 toTarget = targetWorld - GlobalPosition;
        Vector2 dir = toTarget.LengthSquared() > 1.0f ? toTarget.Normalized() : Vector2.Right;
        Vector2 perp = new Vector2(-dir.Y, dir.X);

        float sideSign = (partIndex % 2 == 0) ? 1.0f : -1.0f;
        Vector2 startWorld = GlobalPosition + dir * (MouthHorizonRadius * 0.35f);
        Vector2 controlWorld = GlobalPosition.Lerp(targetWorld, 0.48f) + perp * (sideSign * (135.0f + (partIndex % 3) * 35.0f));

        float chunkRadius = MouthHorizonRadius * 0.50f;
        const int sides = 7;
        Vector2[] poly = new Vector2[sides];
        for (int s = 0; s < sides; s++)
        {
            float a = (float)s / sides * Mathf.Tau;
            float r = chunkRadius * (0.82f + 0.28f * MathF.Sin(partIndex * 3.1f + s * 2.3f));
            poly[s] = new Vector2(MathF.Cos(a), MathF.Sin(a)) * r;
        }

        _flyingParts.Add(new EjectedRockPartChunk
        {
            PartIndex = partIndex,
            StartWorldPos = startWorld,
            ControlWorldPos = controlWorld,
            TargetWorldPos = targetWorld,
            Progress = 0.0f,
            Duration = 0.58f,
            Rotation = (float)GD.RandRange(0.0, Mathf.Tau),
            RotSpeed = sideSign * 5.2f,
            Radius = chunkRadius,
            LocalPoly = poly
        });

        SwarmSimulation.Instance?.TriggerVisualShockwaveEcho(GlobalPosition, MouthHorizonRadius * 2.4f, pushPower: 260.0f);
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;

        // 1. Presesi Orbit Lambat di Sekitar (0,0)
        OrbitAngle += OrbitSpeed * dt;
        UpdateOrbitPosition();

        // 2. Selalu jalankan Horizon Exclusion Barrier agar Rift TIDAK PERNAH berada di belakang asteroid/bintang
        EnforceHorizonExclusionBarrier(dt);

        _stateTimer += dt;

        Vector2 playerPos = PlayerCore.Instance?.GlobalPosition ?? Vector2.Zero;
        float distToPlayer = playerPos.DistanceTo(GlobalPosition);
        float cameraDistAtten = Mathf.Clamp(1.0f - (distToPlayer - 350.0f) / 3200.0f, 0.0f, 1.0f);

        bool isNearPlayer = distToPlayer < 1250.0f || _genesisPhase != WhiteHoleGenesisPhase.Inactive || State != RiftState.Dormant;
        if (_riftRect != null && _riftRect.Visible != isNearPlayer)
        {
            _riftRect.Visible = isNearPlayer;
        }
        if (_backBuffer != null && _backBuffer.Visible != isNearPlayer)
        {
            _backBuffer.Visible = isNearPlayer;
        }

        switch (State)
        {
            case RiftState.Dormant:
                _chargeProgress = 0.0f;
                _whiteHolePolarity = Mathf.Lerp(_whiteHolePolarity, 0.0f, dt * 2.0f);
                _riftMaterial?.SetShaderParameter("black_hole_radius", 0.102f);

                // Efek fisika & distorsi jarak dekat Black Hole terhadap bintang & asteroid di sekitarnya!
                ApplyCloseRangeBlackHoleEffects(dt, chargeFactor: 0.0f);
                break;

            case RiftState.Charging:
                float p = Mathf.Clamp(_stateTimer / ChargeDuration, 0.0f, 1.0f);
                _chargeProgress = p;
                _whiteHolePolarity = 0.0f;

                float rumbleShake = Mathf.Lerp(0.0f, 2.2f, p * p * p) * cameraDistAtten;
                if (rumbleShake > 0.05f)
                {
                    PlayerCamera.Instance?.TriggerShake(rumbleShake);
                }

                // Efek hisapan orbit & pengikisan akresi semakin ganas selama Charging
                ApplyCloseRangeBlackHoleEffects(dt, chargeFactor: p);

                if (p >= 0.94f)
                {
                    float pinchT = (p - 0.94f) / 0.06f;
                    float pinchedR = Mathf.Lerp(0.102f, 0.038f, MathF.Sin(pinchT * Mathf.Pi * 0.5f));
                    _riftMaterial?.SetShaderParameter("black_hole_radius", pinchedR);
                }
                else
                {
                    _riftMaterial?.SetShaderParameter("black_hole_radius", 0.102f);
                }

                if (_stateTimer >= ChargeDuration)
                {
                    State = RiftState.SupernovaBlast;
                    _stateTimer = 0.0f;
                    _shockwavePulseTimer = 0.0f;
                    _accretionVoxels.Clear();

                    // IDE 2: Gravitational Polarity Inversion & Ghost Phasing
                    // Seluruh asteroid di dalam radius 1950 px masuk ke mode Ghost Phasing (bebas tabrakan fisik)
                    // dan meluncur mulus ke orbit sabuk luar tanpa risiko macet atau tertahan gesekan!
                    TriggerGravitationalPolarityInversion();

                    float blastShake = 18.0f * cameraDistAtten;
                    if (blastShake > 0.5f)
                    {
                        PlayerCamera.Instance?.TriggerShake(blastShake);
                    }
                    QueueRedraw();
                }
                break;

            case RiftState.SupernovaBlast:
                _chargeProgress = 0.0f;
                _whiteHolePolarity = Mathf.Lerp(_whiteHolePolarity, 1.0f, dt * 8.0f);
                _riftMaterial?.SetShaderParameter("black_hole_radius", 0.102f);

                PlayerCore.Instance?.ActivateShockwaveShield(GlobalPosition, 0.45f);
                SwarmSimulation.Instance?.EnemyCore?.ActivateShockwaveShield(GlobalPosition, 0.45f);

                // Update durasi Inversion Ripple
                if (_inversionRippleActive)
                {
                    _inversionRippleTimer += dt;
                    if (_inversionRippleTimer >= InversionRippleDuration)
                    {
                        _inversionRippleActive = false;
                    }
                    QueueRedraw();
                }

                if (_stateTimer >= SupernovaDuration)
                {
                    State = RiftState.WhiteHole;
                    _stateTimer = 0.0f;

                    bool force = _pendingForceGenesis;
                    _pendingForceGenesis = false;
                    int currentCount = Asteroid.ActiveAsteroids.Count;
                    var mgr = AsteroidFieldManager.Instance;
                    bool shouldSpawn = force || (mgr != null && currentCount < mgr.TotalAsteroids);

                    TryBeginWhiteHoleAsteroidGenesis(forceSpawn: shouldSpawn);
                }
                break;

            case RiftState.WhiteHole:
                _chargeProgress = 0.0f;
                _riftMaterial?.SetShaderParameter("black_hole_radius", 0.102f);

                ApplyWhiteHoleRepulsion(dt);
                UpdateWhiteHoleAsteroidGenesis(dt, cameraDistAtten);

                if (_genesisPhase == WhiteHoleGenesisPhase.EjectingAndAssemblingParts
                    || _genesisPhase == WhiteHoleGenesisPhase.FiringGenesisTachyonBeam)
                {
                    _stateTimer = MathF.Min(_stateTimer, WhiteHoleDuration - 2.2f);
                }

                if (_stateTimer >= WhiteHoleDuration - 2.0f)
                {
                    float coolProgress = (_stateTimer - (WhiteHoleDuration - 2.0f)) / 2.0f;
                    _whiteHolePolarity = Mathf.Clamp(1.0f - coolProgress, 0.0f, 1.0f);
                }
                else
                {
                    _whiteHolePolarity = 1.0f;
                }

                if (_stateTimer >= WhiteHoleDuration)
                {
                    State = RiftState.Dormant;
                    _stateTimer = 0.0f;
                    _whiteHolePolarity = 0.0f;
                    _genesisPhase = WhiteHoleGenesisPhase.Inactive;
                    QueueRedraw();
                }
                break;
        }

        _riftMaterial?.SetShaderParameter("charge_progress", _chargeProgress);
        _riftMaterial?.SetShaderParameter("white_hole_polarity", _whiteHolePolarity);
        _riftMaterial?.SetShaderParameter("swirl_speed", Mathf.Lerp(0.35f, 2.6f, _chargeProgress));
        _riftMaterial?.SetShaderParameter("lensing_strength", Mathf.Lerp(1.65f, 3.4f, _chargeProgress));
        _riftMaterial?.SetShaderParameter("swirl_strength", Mathf.Lerp(2.40f, 4.8f, _chargeProgress));
    }

    /// <summary>
    /// Menjamin tidak ada satu pun Asteroid maupun Bintang Aether yang menumpuk di depan/belakang
    /// pusat Event Horizon Cosmic Rift pada fase apa pun.
    /// </summary>
    private void EnforceHorizonExclusionBarrier(float dt)
    {
        var asteroids = Asteroid.ActiveAsteroids;
        for (int a = 0; a < asteroids.Count; a++)
        {
            var ast = asteroids[a];
            if (ast == null || !IsInstanceValid(ast) || ast.IsAssemblingFromWhiteHole || ast.IsGhostPhased) continue;

            Vector2 away = ast.GlobalPosition - GlobalPosition;
            float dist = away.Length();
            float minSafeRadius = (ast.IsInSeedCycle ? 95.0f : ast.OuterBoundRadius) + MouthHorizonRadius + 62.0f;

            if (dist < minSafeRadius)
            {
                Vector2 outDir = dist > 1.0f
                    ? away / dist
                    : (ast.GlobalPosition.LengthSquared() > 1.0f ? ast.GlobalPosition.Normalized() : Vector2.Right);

                float overlap = minSafeRadius - dist;
                // Geser keluar dari cakrawala horizon secara radial (tanpa putaran orbit!)
                ast.GlobalPosition += outDir * MathF.Min(overlap, overlap * Mathf.Clamp(dt * 18.0f, 0.25f, 1.0f));
                if (!ast.Freeze)
                {
                    float radialVel = ast.LinearVelocity.Dot(outDir);
                    if (radialVel < 65.0f)
                    {
                        ast.LinearVelocity += outDir * (65.0f - radialVel);
                    }
                    ast.Sleeping = false;
                }
                ast.UpdateCachedTransform();
            }
        }
    }

    /// <summary>
    /// Efek fisika & distorsi jarak dekat Black Hole (Dormant & Charging) terhadap bintang & asteroid sekitar:
    /// 1. Tarikan orbit Keplerian & Tidal Spaghettification (peregangan bentuk bodi ke arah Black Hole).
    /// 2. Pengikisan Voxel Akresi (Voxel Erosion) + Aliran Materi Spageti dari permukaan asteroid terdekat.
    /// 3. Siphoning Plasma Korona & peningkatan Instabilitas pada Bintang Aether terdekat hingga Supernova.
    /// 4. Tarikan gravitasi jarak dekat pada PlayerCore & EnemyCore.
    /// </summary>
    private void ApplyCloseRangeBlackHoleEffects(float dt, float chargeFactor)
    {
        float maxReach = Mathf.Lerp(1080.0f, 1850.0f, chargeFactor);
        float closeDistortionZone = Mathf.Lerp(560.0f, 820.0f, chargeFactor);
        _starSiphonStreamTimer += dt;
        bool emitStarStreamNow = false;
        if (_starSiphonStreamTimer >= 0.055f)
        {
            _starSiphonStreamTimer = 0.0f;
            emitStarStreamNow = true;
        }

        // 1. Update partikel voxel akresi yang sedang berputar spiral masuk ke mulut Black Hole
        UpdateAccretionVoxelsInfall(dt);

        // 2. Interaksi terhadap seluruh Asteroid & Bintang Aether di sekitar Black Hole
        var asteroids = Asteroid.ActiveAsteroids;
        for (int a = 0; a < asteroids.Count; a++)
        {
            var ast = asteroids[a];
            if (ast == null || !IsInstanceValid(ast) || !ast.IsActiveInTree || ast.IsGhostPhased) continue;

            Vector2 toRift = GlobalPosition - ast.CachedPosition;
            float dist = toRift.Length();
            if (dist >= maxReach || dist <= 1.0f) continue;

            Vector2 radDir = toRift / dist;

            float surfaceDist = MathF.Max(0.0f, dist - ast.OuterBoundRadius);

            // A. Jika masuk ke zona distorsi dekat Black Hole: terapkan peregangan pasang-surut (Tidal Spaghettification)
            if (surfaceDist < closeDistortionZone)
            {
                float tidalRatio = Mathf.Clamp(1.0f - (surfaceDist / closeDistortionZone), 0.0f, 1.0f);
                float tidalStrength = (0.14f + 0.24f * chargeFactor) * (tidalRatio * tidalRatio);
                ast.ApplyExternalTidalDistortion(GlobalPosition, tidalStrength, dt);
            }

            // B. Efek Orbit dinonaktifkan sesuai permintaan pengguna (batu tetap stabil tanpa berputar mengorbit!)

            // C. Efek Khusus terhadap BINTANG AETHER di dekat Black Hole (Coronal Siphoning & Tidal Overload)
            if (ast.IsEnergyAsteroid && surfaceDist < 520.0f)
            {
                float starProximity = Mathf.Clamp(1.0f - (surfaceDist / 520.0f), 0.0f, 1.0f);
                float drainIntensity = starProximity * (0.55f + 0.45f * chargeFactor);

                // Sedot panas & picu penyok kavitasi korona Bintang Aether menghadap ke Black Hole!
                ast.ApplyBlackHoleTidalDrain(GlobalPosition, drainIntensity, dt);

                if (emitStarStreamNow)
                {
                    Vector2 starSurfacePt = ast.CachedPosition + radDir * (ast.Radius * 0.78f);
                    SwarmSimulation.Instance?.EmitTidalSpaghettiStream(
                        starSurfacePt,
                        GlobalPosition,
                        new Color(2.4f, 0.35f, 3.8f, 0.95f),
                        new Color(5.4f, 0.42f, 4.3f, 1.0f),
                        Mathf.Clamp(drainIntensity * 1.25f, 0.25f, 1.0f),
                        1.0f
                    );
                }
            }
        }

        // 3. Pengikisan Voxel Akresi pada Asteroid Batu yang Sangat Dekat dengan Black Hole
        _accretionErodeTimer += dt;
        float currentErodeInterval = Mathf.Lerp(AccretionErodeInterval, 0.055f, chargeFactor);
        if (_accretionErodeTimer >= currentErodeInterval)
        {
            _accretionErodeTimer = 0.0f;
            ErodeClosestAsteroidVoxelsIntoBlackHole(chargeFactor);
        }

        // 4. Tarikan Gravitasi Jarak Dekat pada Kapal Pemain & Musuh (di dalam radius distorsi 480 px)
        ApplyBlackHolePullToCores(dt, chargeFactor);
    }

    private void ErodeClosestAsteroidVoxelsIntoBlackHole(float chargeFactor)
    {
        float maxSurfaceDist = Mathf.Lerp(340.0f, 520.0f, chargeFactor);
        var asteroids = Asteroid.ActiveAsteroids;

        for (int a = 0; a < asteroids.Count; a++)
        {
            var ast = asteroids[a];
            if (ast == null || !IsInstanceValid(ast) || !ast.IsActiveInTree || ast.IsEnergyAsteroid || ast.IsDestroying || ast.IsGhostPhased || ast.IsSupercharged)
                continue;

            float centerDist = GlobalPosition.DistanceTo(ast.CachedPosition);
            float surfaceDist = centerDist - ast.OuterBoundRadius;
            if (surfaceDist > maxSurfaceDist) continue;

            if (ast.FindClosestSolidVoxel(GlobalPosition, out Vector2 closestVoxelPos, out Asteroid.VoxelType _))
            {
                float voxDist = GlobalPosition.DistanceTo(closestVoxelPos);
                if (voxDist > maxSurfaceDist + 90.0f) continue;

                float proximity = Mathf.Clamp(1.0f - (voxDist - MouthHorizonRadius) / MathF.Max(60.0f, maxSurfaceDist), 0.15f, 1.0f);
                _erodedVoxelBuffer.Clear();
                int carved = ast.CarveVoxelCircle(closestVoxelPos, Asteroid.VoxelSize * (1.35f + 0.45f * proximity), _erodedVoxelBuffer);

                if (carved > 0)
                {
                    SwarmSimulation.Instance?.EmitTidalSpaghettiStream(
                        closestVoxelPos,
                        GlobalPosition,
                        new Color(0.18f, 0.16f, 0.26f, 0.95f),
                        ast.ResourceGlowColor,
                        proximity,
                        1.0f
                    );

                    for (int v = 0; v < _erodedVoxelBuffer.Count; v++)
                    {
                        if (_accretionVoxels.Count >= 32) break;

                        var vox = _erodedVoxelBuffer[v];
                        Color vCol;
                        if (Asteroid.TryGetResourceType(vox.type, out ResourceType resT))
                        {
                            vCol = Asteroid.GetResourceColor(resT) * 1.55f;
                            vCol.A = 1.0f;
                        }
                        else
                        {
                            vCol = new Color(0.16f, 0.14f, 0.24f, 0.96f).Lerp(new Color(3.4f, 2.3f, 0.58f, 1.0f), 0.38f);
                        }

                        Vector2 toHole = GlobalPosition - vox.worldCenter;
                        Vector2 launchDir = toHole.LengthSquared() > 1.0f ? toHole.Normalized() : Vector2.Right;
                        Vector2 perp = new Vector2(-launchDir.Y, launchDir.X);

                        _accretionVoxels.Add(new RiftAccretionVoxel
                        {
                            WorldPos = vox.worldCenter,
                            Velocity = launchDir * 290.0f + perp * (160.0f + (GD.Randf() - 0.5f) * 90.0f),
                            Color = vCol,
                            Size = Asteroid.VoxelSize * 0.88f,
                            OrbitAngle = 0.0f,
                            OrbitRadius = 0.0f,
                            InfallSpeed = 0.0f,
                            AngularSpeed = 0.0f,
                            HasCapturedOrbit = false,
                            Lifetime = 0.0f,
                            MaxLifetime = 3.2f,
                            Rotation = GD.Randf() * Mathf.Tau,
                            RotSpeed = (float)GD.RandRange(-9.0, 9.0)
                        });
                    }
                    QueueRedraw();
                }
            }
        }
    }

    private void UpdateAccretionVoxelsInfall(float dt)
    {
        if (_accretionVoxels.Count == 0) return;

        float captureR = MouthHorizonRadius * 1.85f;
        float horizonR = MouthHorizonRadius * 0.88f;

        for (int i = _accretionVoxels.Count - 1; i >= 0; i--)
        {
            var p = _accretionVoxels[i];
            p.Lifetime += dt;

            if (!p.HasCapturedOrbit)
            {
                Vector2 toHole = GlobalPosition - p.WorldPos;
                float d = toHole.Length();
                if (d <= captureR || p.Lifetime > 1.8f)
                {
                    p.HasCapturedOrbit = true;
                    p.OrbitRadius = MathF.Max(horizonR + 8.0f, d);
                    p.OrbitAngle = (p.WorldPos - GlobalPosition).Angle();
                    p.AngularSpeed = (float)GD.RandRange(5.5, 9.2);
                    p.InfallSpeed = (float)GD.RandRange(55.0, 98.0);
                }
                else
                {
                    Vector2 dir = toHole / MathF.Max(0.001f, d);
                    Vector2 tang = new Vector2(-dir.Y, dir.X);
                    float accel = Mathf.Lerp(540.0f, 1450.0f, Mathf.Clamp(1.0f - d / 560.0f, 0.0f, 1.0f));
                    p.Velocity += (dir * accel + tang * (accel * 0.48f)) * dt;
                    float spd = p.Velocity.Length();
                    if (spd > 720.0f) p.Velocity = (p.Velocity / spd) * 720.0f;
                    p.WorldPos += p.Velocity * dt;
                    p.Rotation += p.RotSpeed * dt;
                }
            }
            else
            {
                p.OrbitRadius -= p.InfallSpeed * dt;
                float keplerMult = MathF.Sqrt(captureR / MathF.Max(20.0f, p.OrbitRadius));
                p.OrbitAngle += p.AngularSpeed * keplerMult * dt;
                p.WorldPos = GlobalPosition + new Vector2(MathF.Cos(p.OrbitAngle), MathF.Sin(p.OrbitAngle)) * p.OrbitRadius;
                p.Rotation += p.RotSpeed * keplerMult * dt;

                float sizeRatio = Mathf.Clamp((p.OrbitRadius - horizonR) / MathF.Max(1.0f, captureR - horizonR), 0.0f, 1.0f);
                p.Size = MathF.Max(1.6f, Asteroid.VoxelSize * 0.88f * MathF.Pow(sizeRatio, 0.6f));

                if (p.OrbitRadius <= horizonR)
                {
                    _accretionVoxels.RemoveAt(i);
                    continue;
                }
            }

            if (p.Lifetime >= p.MaxLifetime)
            {
                _accretionVoxels.RemoveAt(i);
            }
            else
            {
                _accretionVoxels[i] = p;
            }
        }

        QueueRedraw();
    }

    private void ApplyBlackHolePullToCores(float dt, float chargeFactor)
    {
        float coreGravityRadius = Mathf.Lerp(480.0f, 760.0f, chargeFactor);

        if (PlayerCore.Instance != null && IsInstanceValid(PlayerCore.Instance) && !PlayerCore.Instance.HasShockwaveShield)
        {
            Vector2 toHole = GlobalPosition - PlayerCore.Instance.GlobalPosition;
            float dist = toHole.Length();
            if (dist < coreGravityRadius && dist > 1.0f)
            {
                Vector2 pullDir = toHole / dist;
                Vector2 swirlDir = new Vector2(-pullDir.Y, pullDir.X);
                float ratio = Mathf.Clamp(1.0f - (dist / coreGravityRadius), 0.0f, 1.0f);
                float pullAccel = Mathf.Lerp(140.0f, 820.0f, ratio * ratio) * (1.0f + chargeFactor * 0.5f);
                float swirlAccel = Mathf.Lerp(65.0f, 340.0f, ratio * ratio);
                PlayerCore.Instance.Velocity += (pullDir * pullAccel + swirlDir * swirlAccel) * dt;
                PlayerCore.Instance.ApplyGravityWellTidalStress(GlobalPosition, ratio * 0.85f, dt);
            }
        }

        var enemyCore = SwarmSimulation.Instance?.EnemyCore;
        if (enemyCore != null && IsInstanceValid(enemyCore) && !enemyCore.HasShockwaveShield)
        {
            Vector2 toHole = GlobalPosition - enemyCore.GlobalPosition;
            float dist = toHole.Length();
            if (dist < coreGravityRadius && dist > 1.0f)
            {
                Vector2 pullDir = toHole / dist;
                Vector2 swirlDir = new Vector2(-pullDir.Y, pullDir.X);
                float ratio = Mathf.Clamp(1.0f - (dist / coreGravityRadius), 0.0f, 1.0f);
                float pullAccel = Mathf.Lerp(140.0f, 820.0f, ratio * ratio) * (1.0f + chargeFactor * 0.5f);
                float swirlAccel = Mathf.Lerp(65.0f, 340.0f, ratio * ratio);
                enemyCore.Velocity += (pullDir * pullAccel + swirlDir * swirlAccel) * dt;
            }
        }
    }

    public override void _Draw()
    {
        // 0. Gambar bongkahan voxel akresi yang tersedot berputar spiral ke mulut Black Hole
        if (_accretionVoxels.Count > 0)
        {
            for (int i = 0; i < _accretionVoxels.Count; i++)
            {
                var p = _accretionVoxels[i];
                Vector2 localPos = p.WorldPos - GlobalPosition;
                float halfSz = p.Size * 0.5f;

                float cos = MathF.Cos(p.Rotation);
                float sin = MathF.Sin(p.Rotation);
                float stretch = p.HasCapturedOrbit ? 1.45f : 1.15f;
                Vector2 u = new Vector2(cos, sin) * (halfSz * stretch);
                Vector2 v = new Vector2(-sin, cos) * (halfSz / MathF.Sqrt(stretch));

                _voxelQuadBuffer[0] = localPos - u - v;
                _voxelQuadBuffer[1] = localPos + u - v;
                _voxelQuadBuffer[2] = localPos + u + v;
                _voxelQuadBuffer[3] = localPos - u + v;

                DrawColoredPolygon(_voxelQuadBuffer, p.Color);
            }
        }

        // 0b. Render Visual Megah & Ringan: Gravitational Polarity Inversion Ripple (Ide 2)
        if (_inversionRippleActive)
        {
            float prog = Mathf.Clamp(_inversionRippleTimer / InversionRippleDuration, 0.0f, 1.0f);
            float invProg = 1.0f - prog;
            float waveRadius = Mathf.Lerp(MouthHorizonRadius, 1950.0f, MathF.Sqrt(prog));

            // Pendaran kubah inversi gravitasi lembut
            Color innerGlow = new Color(0.2f, 1.2f, 2.0f, invProg * 0.08f);
            DrawCircle(Vector2.Zero, waveRadius, innerGlow);

            // Cincin ganda bercahaya perak-cyan
            Color waveColor1 = new Color(0.85f, 3.8f, 5.2f, invProg * 0.95f);
            Color waveColor2 = new Color(3.8f, 4.2f, 5.2f, invProg * 0.65f);
            DrawArc(Vector2.Zero, waveRadius, 0.0f, Mathf.Tau, 48, waveColor1, 3.8f);
            DrawArc(Vector2.Zero, waveRadius * 0.88f, 0.0f, Mathf.Tau, 36, waveColor2, 1.8f);

            // Garis-garis radial medan polaritas kuantum
            const int rayCount = 12;
            float angleStep = Mathf.Tau / rayCount;
            float rayRot = _inversionRippleTimer * 1.5f;
            for (int r = 0; r < rayCount; r++)
            {
                float ang = r * angleStep + rayRot;
                Vector2 rayDir = new Vector2(MathF.Cos(ang), MathF.Sin(ang));
                Vector2 pStart = rayDir * (MouthHorizonRadius * 1.2f);
                Vector2 pEnd = rayDir * (waveRadius * 0.98f);
                Color rayCol = new Color(0.6f, 2.8f, 4.5f, invProg * 0.35f);
                DrawLine(pStart, pEnd, rayCol, 1.6f);
            }
        }

        if (_genesisPhase == WhiteHoleGenesisPhase.Inactive || _genesisPhase == WhiteHoleGenesisPhase.Completed)
            return;

        // 1. Gambar part-part batu murni (seukuran mulut White Hole) yang sedang meluncur & merakit
        Color rockFill = new Color(0.038f, 0.034f, 0.055f, 0.98f);
        Color whiteHoleRim = new Color(3.6f, 4.2f, 5.2f, 0.92f);
        Color trailColor = new Color(2.2f, 3.2f, 4.5f, 0.42f);

        for (int i = 0; i < _flyingParts.Count; i++)
        {
            var part = _flyingParts[i];
            float t = Mathf.Clamp(part.Progress, 0.0f, 1.0f);
            float invT = 1.0f - t;

            Vector2 worldPos = (invT * invT * part.StartWorldPos)
                             + (2.0f * invT * t * part.ControlWorldPos)
                             + (t * t * part.TargetWorldPos);
            Vector2 localPos = worldPos - GlobalPosition;

            float scale = Mathf.Lerp(0.55f, 1.0f, MathF.Sin(t * Mathf.Pi * 0.5f));
            float cos = MathF.Cos(part.Rotation);
            float sin = MathF.Sin(part.Rotation);

            Vector2 prevT = MathF.Max(0.0f, t - 0.14f) * Vector2.One;
            float pt0 = prevT.X;
            float piv0 = 1.0f - pt0;
            Vector2 tailWorld = (piv0 * piv0 * part.StartWorldPos)
                              + (2.0f * piv0 * pt0 * part.ControlWorldPos)
                              + (pt0 * pt0 * part.TargetWorldPos);
            DrawLine(tailWorld - GlobalPosition, localPos, trailColor, 6.0f * (1.0f - t * 0.5f));

            int n = part.LocalPoly.Length;
            for (int v = 0; v < n; v++)
            {
                Vector2 p = part.LocalPoly[v] * scale;
                Vector2 rotP = new Vector2(p.X * cos - p.Y * sin, p.X * sin + p.Y * cos);
                _drawPolyBuffer[v] = localPos + rotP;
                _drawOutlineBuffer[v] = _drawPolyBuffer[v];
            }
            _drawOutlineBuffer[n] = _drawPolyBuffer[0];

            DrawColoredPolygon(_drawPolyBuffer.AsSpan(0, n).ToArray(), rockFill);
            DrawPolyline(_drawOutlineBuffer.AsSpan(0, n + 1).ToArray(), whiteHoleRim, 2.2f);
        }

        // 2. Gambar Genesis Tachyon Beam (Opsi A) saat White Hole menyuntikkan Supercharge & mengacak resource!
        if (_genesisPhase == WhiteHoleGenesisPhase.FiringGenesisTachyonBeam && _assemblingAsteroid != null && IsInstanceValid(_assemblingAsteroid))
        {
            float p = Mathf.Clamp(_tachyonBeamTimer / TachyonBeamDuration, 0.0f, 1.0f);
            float pulse = 0.5f + 0.5f * MathF.Sin(_visualPulseTime * 38.0f);

            Vector2 targetLocal = _assemblingAsteroid.GlobalPosition - GlobalPosition;
            Vector2 dir = targetLocal.LengthSquared() > 1.0f ? targetLocal.Normalized() : Vector2.Right;
            Vector2 perp = new Vector2(-dir.Y, dir.X);

            float outerWidth = Mathf.Lerp(18.0f, 78.0f, MathF.Sin(p * Mathf.Pi)) * (0.9f + 0.2f * pulse);
            float midWidth = outerWidth * 0.56f;
            float coreWidth = outerWidth * 0.26f;

            Color auraCyanGold = new Color(1.4f, 3.8f, 5.2f, 0.42f);
            Color midGold = new Color(4.8f, 3.8f, 1.4f, 0.88f);
            Color coreWhite = new Color(6.5f, 6.2f, 5.5f, 0.98f);

            DrawLine(Vector2.Zero, targetLocal, auraCyanGold, outerWidth);
            DrawLine(Vector2.Zero, targetLocal, midGold, midWidth);
            DrawLine(Vector2.Zero, targetLocal, coreWhite, coreWidth);

            const int segs = 18;
            Vector2 prevH1 = Vector2.Zero;
            Vector2 prevH2 = Vector2.Zero;
            for (int s = 1; s <= segs; s++)
            {
                float st = (float)s / segs;
                Vector2 basePt = targetLocal * st;
                float wave = MathF.Sin(st * 18.0f - _visualPulseTime * 28.0f) * (outerWidth * 0.45f);
                Vector2 curH1 = basePt + perp * wave;
                Vector2 curH2 = basePt - perp * wave;

                DrawLine(prevH1, curH1, midGold, 2.4f);
                DrawLine(prevH2, curH2, auraCyanGold, 2.2f);
                prevH1 = curH1;
                prevH2 = curH2;
            }

            float impactR = Mathf.Lerp(24.0f, _assemblingAsteroid.Radius * 0.58f, p);
            DrawCircle(targetLocal, impactR, new Color(4.5f, 3.8f, 1.4f, 0.28f + 0.25f * pulse));
            DrawArc(targetLocal, impactR * 1.15f, 0.0f, Mathf.Tau, 32, coreWhite, 2.8f);
            DrawCircle(targetLocal, 18.0f + 10.0f * pulse, coreWhite);
        }
    }

    /// <summary>
    /// Ide 2: Pembalikan Kutub Gravitasi (Gravitational Polarity Inversion & Ghost Phasing).
    /// Mengaktifkan mode Ghost Phasing pada seluruh asteroid & bintang dalam radius 1950 px:
    /// Asteroid dinonaktifkan collision fisiknya sementara sehingga dapat menembus satu sama lain tanpa macet,
    /// dan meluncur keluar secara deterministik ke orbit sabuk luar (R ~ 1850 px).
    /// </summary>
    private void TriggerGravitationalPolarityInversion()
    {
        _inversionRippleTimer = 0.0f;
        _inversionRippleActive = true;

        var asteroids = Asteroid.ActiveAsteroids;
        for (int a = 0; a < asteroids.Count; a++)
        {
            var ast = asteroids[a];
            if (ast == null || !IsInstanceValid(ast)) continue;
            if (!ast.IsActiveInTree && !ast.IsInSeedCycle) continue;

            float dist = ast.GlobalPosition.DistanceTo(GlobalPosition);
            if (dist < 2600.0f)
            {
                if (ast.IsEnergyAsteroid)
                {
                    if (ast.IsInSeedCycle)
                    {
                        ast.EnterCosmicDriftEscapingBlackHole(GlobalPosition);
                    }
                    else
                    {
                        ast.EnterGhostPhase(GlobalPosition, targetRadius: 2200.0f, duration: 3.2f);
                        Vector2 outward = ast.GlobalPosition - GlobalPosition;
                        Vector2 blastDir = outward.LengthSquared() > 1.0f ? outward.Normalized() : Vector2.Right;
                        ast.SchedulePushedSupernova(blastDir * 1100.0f, pushDuration: 3.5f, GlobalPosition);
                    }
                }
                else
                {
                    ast.EnterGhostPhase(GlobalPosition, targetRadius: 1950.0f, duration: 3.0f);
                }
            }
        }
    }

    private void ApplyWhiteHoleRepulsion(float dt)
    {
        float coolMult = 1.0f;
        if (_stateTimer >= WhiteHoleDuration - 2.0f)
        {
            float coolProgress = (_stateTimer - (WhiteHoleDuration - 2.0f)) / 2.0f;
            coolMult = Mathf.Clamp(1.0f - coolProgress, 0.0f, 1.0f);
        }

        if (coolMult <= 0.01f) return;

        var asteroids = Asteroid.ActiveAsteroids;
        for (int a = 0; a < asteroids.Count; a++)
        {
            var ast = asteroids[a];
            if (ast == null || !IsInstanceValid(ast) || !ast.IsActiveInTree || ast.IsInSeedCycle || ast.IsGhostPhased) continue;

            Vector2 outward = ast.CachedPosition - GlobalPosition;
            float dist = outward.Length();

            if (dist < 1600.0f)
            {
                Vector2 dir = dist > 5.0f
                    ? outward / dist
                    : (ast.CachedPosition.LengthSquared() > 10.0f ? ast.CachedPosition.Normalized() : Vector2.Right);

                float falloff = Mathf.Clamp(1.0f - dist / 1600.0f, 0.0f, 1.0f);
                float repulseAccel = (dist < 900.0f ? 580.0f : 240.0f) * coolMult * falloff;
                ast.LinearVelocity += dir * (repulseAccel * dt);
                ast.Sleeping = false;
            }
        }
    }
}
