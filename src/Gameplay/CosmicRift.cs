using System;
using System.Collections.Generic;
using Godot;

namespace GeometrySwarm.Gameplay;

/// <summary>
/// Anomali Astrofisika Sabuk Dalam: Cosmic Rift (Supermassive Black Hole & White Hole).
/// Siklus Sinematik:
/// 1. Dormant: Black hole stabil dengan distorsi ruang melingkar & cincin foton emas tipis.
/// 2. Charging (8 Detik): Petir dan lidah api menjilat di horizon, asteroid terdekat tersedot mengorbit melingkari black hole.
/// 3. Supernova Blast (3 Detik): Ledakan supernova memancarkan shockwave raksasa setiap 0.5s, asteroid terlempar jauh,
///    dan inti berubah menjadi White Hole putih-perak mutlak.
/// 4. White Hole (7.5 Detik): Menolak materi keluar dengan gravitasi repulsif. Jika jumlah asteroid di arena berkurang
///    (< TotalAsteroids) atau dipicu tombol debug [T]:
///    - Tahap A (Part Ejection & Pure Rock Assembly): Mulut White Hole (R ~ 98 px) memuntahkan part-part batu murni
///      satu per satu yang meluncur melengkung dan merakit asteroid batu utuh (kebalikan dari Bintang Aether).
///    - Tahap B (Genesis Tachyon Beam & Resource Randomization): Setelah batu 100% terakit, White Hole menembakkan
///      Genesis Tachyon Beam ke jantung batu, mengacak deposit resource 2x lebih kaya di dalamnya, dan mengubahnya
///      menjadi Supercharged Asteroid (aktif selama 2 menit sebelum cooling ke biasa).
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
        public float Radius; // Sesuai mulut White Hole (~46 - 52 px radius / ~96 px diameter)
        public Vector2[] LocalPoly;
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
        ZIndex = 4; // Agar part yang keluar dari mulut & Tachyon Beam tergambar jelas di atas objek sekitar
        UpdateOrbitPosition();

        // Seluruh rift mulai dalam kondisi tenang (Dormant) dan hanya aktif jika dipicu oleh tombol [T]
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
                    CopyMode = BackBufferCopy.CopyModeEnum.Viewport
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
        // Selesaikan perakitan sebelumnya jika masih berjalan agar tidak terjadi tabrakan state
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
    /// Memaksa Cosmic Rift langsung masuk ke mode White Hole dan memulai perakitan Supercharged Asteroid (Tombol Debug [T]).
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

        // Jika masih ada asteroid yang sedang dirakit dari trigger sebelumnya, selesaikan dulu agar tidak menumpuk
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

        // Tentukan posisi perakitan di dekat luar mulut White Hole (mudah terlihat oleh pemain)
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

        // Jaga posisi titik rakit tetap mengikuti orbit halus White Hole selama dirakit
        Vector2 assemblyCenterWorld = GlobalPosition + _assemblyAnchorOffset;
        _assemblingAsteroid.GlobalPosition = assemblyCenterWorld;

        if (_genesisPhase == WhiteHoleGenesisPhase.EjectingAndAssemblingParts)
        {
            // 1. Muntahkan part batu (seukuran mulut White Hole) satu per satu
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

            // 2. Gerakkan seluruh part yang sedang melayang di udara menuju slot perakitan masing-masing
            for (int i = _flyingParts.Count - 1; i >= 0; i--)
            {
                var part = _flyingParts[i];
                part.Progress += dt / MathF.Max(0.05f, part.Duration);
                part.Rotation += part.RotSpeed * dt;
                part.TargetWorldPos = _assemblingAsteroid.GetAssemblyPartWorldCenter(part.PartIndex);

                if (part.Progress >= 1.0f)
                {
                    // Part tiba dan mengunci menjadi VoxelType.Rock murni!
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

            // 3. Begitu seluruh part batu murni selesai merakit 100% -> Masuk ke tahap Genesis Tachyon Beam!
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

            // Getarkan batu yang sedang ditembak sinar Tachyon White Hole
            float vibMag = Mathf.Lerp(2.0f, 16.0f, beamProgress * beamProgress);
            Vector2 jitter = new Vector2((GD.Randf() - 0.5f) * vibMag, (GD.Randf() - 0.5f) * vibMag);
            _assemblingAsteroid.GlobalPosition = assemblyCenterWorld + jitter;

            if (cameraDistAtten > 0.05f)
            {
                PlayerCamera.Instance?.TriggerShake(Mathf.Lerp(3.0f, 16.0f, beamProgress) * cameraDistAtten);
            }

            if (_tachyonBeamTimer >= TachyonBeamDuration)
            {
                // Klimaks Supercharge: Acak resource 2x lebih kaya di dalam batu & luncurkan ke sabuk arena!
                Vector2 launchDir = _assemblyAnchorOffset.Normalized();
                Vector2 launchVel = launchDir * 135.0f;
                _assemblingAsteroid.GlobalPosition = assemblyCenterWorld;
                _assemblingAsteroid.InfuseSuperchargedResourcesAndLaunch(launchVel);

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

        // Lontarkan tepat dari mulut horizon White Hole dengan lengkungan spiral terbalik (kebalikan Bintang Aether)
        float sideSign = (partIndex % 2 == 0) ? 1.0f : -1.0f;
        Vector2 startWorld = GlobalPosition + dir * (MouthHorizonRadius * 0.35f);
        Vector2 controlWorld = GlobalPosition.Lerp(targetWorld, 0.48f) + perp * (sideSign * (135.0f + (partIndex % 3) * 35.0f));

        // Ukuran part sesuai dengan mulut Black Hole / White Hole (diameter ~96 px -> radius ~48 px)
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

        // Gelombang kejut kecil di mulut White Hole saat memuntahkan bongkahan batu
        SwarmSimulation.Instance?.TriggerVisualShockwaveEcho(GlobalPosition, MouthHorizonRadius * 2.1f);
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;

        // 1. Presesi Orbit Lambat di Sekitar (0,0)
        OrbitAngle += OrbitSpeed * dt;
        UpdateOrbitPosition();

        // Rift 1 & 2 tetap tenang di sabuk dalam sebagai black hole stabil jika sedang Dormant
        if (RiftId != 0 && State == RiftState.Dormant)
        {
            _chargeProgress = 0.0f;
            _whiteHolePolarity = 0.0f;
            if (_riftRect != null && _riftRect.Visible) _riftRect.Visible = false;
            if (_backBuffer != null && _backBuffer.Visible) _backBuffer.Visible = false;
            return;
        }

        // 2. State Machine Siklus Kosmik
        _stateTimer += dt;

        Vector2 playerPos = PlayerCore.Instance?.GlobalPosition ?? Vector2.Zero;
        float distToPlayer = playerPos.DistanceTo(GlobalPosition);
        float cameraDistAtten = Mathf.Clamp(1.0f - (distToPlayer - 350.0f) / 3200.0f, 0.0f, 1.0f);

        bool isNearPlayer = distToPlayer < 1900.0f || _genesisPhase != WhiteHoleGenesisPhase.Inactive || State != RiftState.Dormant;
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
                // Tidak ada aktivasi otomatis - hanya aktif jika dipicu via tombol [T]
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

                CaptureAndOrbitNearbyAsteroids(p, dt);

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

                    TriggerAetherSupernovaAndSeedEscape();
                    LaunchNearbyAsteroidsSeamlessly();

                    SwarmSimulation.Instance?.TriggerRiftSupernovaShockwave(GlobalPosition, 2800.0f, 4800.0f);
                    float blastShake = 28.0f * cameraDistAtten;
                    if (blastShake > 0.5f)
                    {
                        PlayerCamera.Instance?.TriggerShake(blastShake);
                    }
                }
                break;

            case RiftState.SupernovaBlast:
                _chargeProgress = 0.0f;
                _whiteHolePolarity = Mathf.Lerp(_whiteHolePolarity, 1.0f, dt * 10.0f);
                _riftMaterial?.SetShaderParameter("black_hole_radius", 0.102f);

                PlayerCore.Instance?.ActivateShockwaveShield(GlobalPosition, 0.45f);
                SwarmSimulation.Instance?.EnemyCore?.ActivateShockwaveShield(GlobalPosition, 0.45f);

                // Dorong asteroid secara aktif & berkelanjutan selama fase gelombang kejut Supernova (Zero Delay & Zero Jamming)
                ApplySupernovaShockwavePhysics(dt);

                _shockwavePulseTimer += dt;
                if (_shockwavePulseTimer >= 0.5f)
                {
                    _shockwavePulseTimer = 0.0f;
                    SwarmSimulation.Instance?.TriggerVisualShockwaveEcho(GlobalPosition, 2800.0f);
                    float echoShake = 10.0f * cameraDistAtten;
                    if (echoShake > 0.4f)
                    {
                        PlayerCamera.Instance?.TriggerShake(echoShake);
                    }
                }

                if (_stateTimer >= SupernovaDuration)
                {
                    State = RiftState.WhiteHole;
                    _stateTimer = 0.0f;

                    // Mulai perakitan asteroid: paksa jika dipicu urutan sinematik [T], atau otomatis jika asteroid berkurang
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

                // Tahan durasi WhiteHole jika perakitan / Tachyon Beam masih berlangsung
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

    public override void _Draw()
    {
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

            // Kurva Bezier Kuadratik dari mulut White Hole menuju titik rakit
            Vector2 worldPos = (invT * invT * part.StartWorldPos)
                             + (2.0f * invT * t * part.ControlWorldPos)
                             + (t * t * part.TargetWorldPos);
            Vector2 localPos = worldPos - GlobalPosition;

            // Skala keluar dari mulut White Hole (1.0 pada ukuran mulut -> menyatu ke skala segmen)
            float scale = Mathf.Lerp(0.55f, 1.0f, MathF.Sin(t * Mathf.Pi * 0.5f));
            float cos = MathF.Cos(part.Rotation);
            float sin = MathF.Sin(part.Rotation);

            // Ekor lontaran plasma putih-sian dari mulut White Hole
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

            // Berkas Utama Genesis Tachyon Beam dari mulut White Hole ke inti batu
            DrawLine(Vector2.Zero, targetLocal, auraCyanGold, outerWidth);
            DrawLine(Vector2.Zero, targetLocal, midGold, midWidth);
            DrawLine(Vector2.Zero, targetLocal, coreWhite, coreWidth);

            // Pita Heliks Ganda Tachyon yang mengalir dari White Hole ke dalam batu
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

            // Bola Fokus Injeksi di Pusat Asteroid yang sedang di-Supercharge
            float impactR = Mathf.Lerp(24.0f, _assemblingAsteroid.Radius * 0.58f, p);
            DrawCircle(targetLocal, impactR, new Color(4.5f, 3.8f, 1.4f, 0.28f + 0.25f * pulse));
            DrawArc(targetLocal, impactR * 1.15f, 0.0f, Mathf.Tau, 32, coreWhite, 2.8f);
            DrawCircle(targetLocal, 18.0f + 10.0f * pulse, coreWhite);
        }
    }

    private void CaptureAndOrbitNearbyAsteroids(float progress, float dt)
    {
        var asteroids = Asteroid.ActiveAsteroids;
        for (int a = 0; a < asteroids.Count; a++)
        {
            var ast = asteroids[a];
            if (ast == null || !IsInstanceValid(ast) || !ast.IsActiveInTree) continue;

            Vector2 toRift = GlobalPosition - ast.CachedPosition;
            float dist = toRift.Length();

            if (dist < 1850.0f)
            {
                Vector2 radDir = dist > 5.0f ? toRift / dist : Vector2.Right;
                Vector2 tanDir = new Vector2(-radDir.Y, radDir.X);

                // Pastikan target orbit berada di luar mulut horizon dan di luar radius asteroid
                // agar black hole tidak tertelan masuk ke dalam tubuh asteroid
                float minOrbitRadius = ast.Radius + MouthHorizonRadius + 45.0f;
                float targetRadius = MathF.Max(minOrbitRadius, 340.0f + (MathF.Abs(ast.GetInstanceId()) % 5) * 50.0f);
                float radialDelta = dist - targetRadius;

                float orbitSpeed = Mathf.Lerp(220.0f, 620.0f, progress);
                float radialForce = radialDelta * 4.2f * progress;

                Vector2 desiredVel = tanDir * orbitSpeed + radDir * radialForce;
                ast.LinearVelocity = ast.LinearVelocity.Lerp(desiredVel, dt * (2.8f + progress * 4.5f));
                ast.AngularVelocity = Mathf.Lerp(ast.AngularVelocity, 1.6f, dt * 3.0f);
                ast.Sleeping = false;
            }
        }
    }

    private void TriggerAetherSupernovaAndSeedEscape()
    {
        var asteroids = Asteroid.ActiveAsteroids;
        for (int a = 0; a < asteroids.Count; a++)
        {
            var ast = asteroids[a];
            if (ast == null || !IsInstanceValid(ast) || !ast.IsActiveInTree || ast.IsInSeedCycle) continue;

            if (ast.IsEnergyAsteroid && !ast.IsInSeedCycle)
            {
                float dist = ast.CachedPosition.DistanceTo(GlobalPosition);
                if (dist < 4800.0f)
                {
                    ast.TriggerSupernovaFromBlackHole(GlobalPosition);
                }
            }
        }
    }

    private void LaunchNearbyAsteroidsSeamlessly()
    {
        var asteroids = Asteroid.ActiveAsteroids;
        for (int a = 0; a < asteroids.Count; a++)
        {
            var ast = asteroids[a];
            if (ast == null || !IsInstanceValid(ast) || !ast.IsActiveInTree || ast.IsEnergyAsteroid) continue;

            Vector2 outward = ast.CachedPosition - GlobalPosition;
            float dist = outward.Length();

            if (dist < 4200.0f)
            {
                Vector2 dir;
                if (dist > 10.0f)
                {
                    dir = outward / dist;
                }
                else
                {
                    dir = ast.CachedPosition.LengthSquared() > 10.0f
                        ? ast.CachedPosition.Normalized()
                        : Vector2.Right.Rotated((float)GD.RandRange(0, Mathf.Tau));
                }

                // Jika black hole berada di dalam asteroid atau terlalu dekat, dorong keluar secara instan agar tidak tersangkut
                float minSafeDist = ast.Radius + MouthHorizonRadius + 60.0f;
                if (dist < minSafeDist)
                {
                    ast.GlobalPosition = GlobalPosition + dir * minSafeDist;
                    ast.UpdateCachedTransform();
                }

                float blastSpeed = (float)GD.RandRange(3200.0, 3800.0);
                ast.Sleeping = false;
                ast.ApplySupernovaBlast(dir * blastSpeed, SupernovaDuration + WhiteHoleDuration + 6.0f);
            }
        }
    }

    /// <summary>
    /// Menerapkan dorongan shockwave Supernova berkelanjutan selama 3 detik fase SupernovaBlast:
    /// Menjamin asteroid terpental jauh melintasi angkasa dan membersihkan ruang steril untuk lahirnya asteroid baru di White Hole.
    /// </summary>
    private void ApplySupernovaShockwavePhysics(float dt)
    {
        var asteroids = Asteroid.ActiveAsteroids;
        for (int a = 0; a < asteroids.Count; a++)
        {
            var ast = asteroids[a];
            if (ast == null || !IsInstanceValid(ast) || !ast.IsActiveInTree || ast.IsEnergyAsteroid) continue;

            Vector2 outward = ast.CachedPosition - GlobalPosition;
            float dist = outward.Length();

            if (dist < 4200.0f)
            {
                Vector2 dir;
                if (dist > 10.0f)
                {
                    dir = outward / dist;
                }
                else
                {
                    dir = ast.CachedPosition.LengthSquared() > 10.0f
                        ? ast.CachedPosition.Normalized()
                        : Vector2.Right.Rotated((float)GD.RandRange(0, Mathf.Tau));
                }

                // Jika masih berada di dalam / terlalu dekat dengan horizon, dorong keluar secara halus
                float minSafeDist = ast.Radius + MouthHorizonRadius + 60.0f;
                if (dist < minSafeDist)
                {
                    float nudge = (minSafeDist - dist) * Mathf.Clamp(dt * 15.0f, 0.0f, 1.0f);
                    ast.GlobalPosition += dir * nudge;
                    ast.UpdateCachedTransform();
                }

                // Jika asteroid melambat akibat tabrakan fisik dengan asteroid lain, pulihkan kecepatan lontaran supernova seketika
                float currentOutwardSpeed = ast.LinearVelocity.Dot(dir);
                if (currentOutwardSpeed < 2400.0f)
                {
                    float restoreSpeed = (float)GD.RandRange(3000.0, 3600.0);
                    ast.LinearVelocity = dir * restoreSpeed;
                    ast.AngularVelocity = (float)GD.RandRange(-3.5, 3.5);
                    ast.Sleeping = false;
                }
                else
                {
                    // Dorongan akselerasi badai shockwave berkelanjutan
                    ast.LinearVelocity += dir * (1200.0f * dt);
                }

                // SANGAT PENTING: Refresh status blast agar asteroid tidak terkena pengereman inersia vakum (inertia brake)
                ast.ApplySupernovaBlast(ast.LinearVelocity, SupernovaDuration + WhiteHoleDuration + 6.0f);
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
            if (ast == null || !IsInstanceValid(ast) || !ast.IsActiveInTree || ast.IsInSeedCycle) continue;

            Vector2 outward = ast.CachedPosition - GlobalPosition;
            float dist = outward.Length();

            if (dist < 2800.0f)
            {
                Vector2 dir = dist > 5.0f
                    ? outward / dist
                    : (ast.CachedPosition.LengthSquared() > 10.0f ? ast.CachedPosition.Normalized() : Vector2.Right);
                
                // Repulsi kuat untuk menjamin zona perakitan asteroid tetap steril dan bersih
                float repulseForce = (dist < 1200.0f ? 1600.0f : 850.0f) * coolMult * (1.0f - dist / 2800.0f);
                ast.LinearVelocity += dir * repulseForce * dt;
                ast.Sleeping = false;
                if (dist < 1200.0f)
                {
                    ast.ApplySupernovaBlast(ast.LinearVelocity, 3.0f);
                }
            }
        }
    }
}
