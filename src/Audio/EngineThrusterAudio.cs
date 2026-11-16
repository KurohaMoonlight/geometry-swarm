using System;
using GeometrySwarm.Gameplay;
using Godot;

namespace GeometrySwarm.Audio;

/// <summary>
/// Mesin sintesis audio prosedural real-time (44.1 kHz Stereo, Zero-Asset, Zero-GC)
/// untuk Geometry Swarm. Dirancang dengan karakter suara "Warm, Majestic, & Ear-Friendly":
///
/// BUS 1 — PROPULSI MEGAH & HARMONI SWARM (THRUSTER, SWARM MOTION PAD, & PLASMA BOOST):
///   1. IDLE: Hening total (0 dB).
///   2. SWARM & SHIP MOVE (WASD & CRUISING): Suara mesin bertenaga & terdengar jelas di seluruh speaker!
///      Menggabungkan nada fundamental (68-102 Hz) + harmonik ke-2 & ke-3 hangat + modulasi denyut getaran (~21 Hz)
///      + semburan jet exhaust ionik bertekanan (bandpass ~500-2200 Hz) + Swarm Harmonic Pad stereo megah.
///   3. PRIMING BOOST [SPACE]: Swell reaktor harmonik 3-tahap ("ngung... nguuung... NGUUUNG", 62-128 Hz)
///      dengan lapisan sub-oktaf dan kord kelima stereo yang mengembang secara sinematik & jelas terdengar.
///   4. FULL PLASMA BOOST: Hentakan sub-bloom bulat (78 -> 32 Hz) + koor plasma stereo lebar
///      (Root + Sub + Fifth + Octave dengan modulasi chorus lambat) + desis plasma bertenaga.
///
/// BUS 2 — PENAMBANGAN HALUS (VAPORIZATION MINING, MINERAL RESONANCE & SPARKS, GIGA DRILL):
///   1. LASER HUM & PRIMING: Dengungan koheren lembut yang hangat tanpa frekuensi tajam.
///   2. ROCK VAPORIZATION (BATUAN TERGERUS = SUARA MENGUAP HALUS): Batuan yang tergerus laser
///      menghasilkan suara uap hangat yang sangat lembut dan pelan (whisper-soft steam vaporization).
///   3. RESOURCE PATCH MINING (URAT MINERAL): Resonansi kord kristal harmonis multi-osilator (singing crystal bowl)
///      yang merdu dan mewah, dipadukan dengan desis percikan ionisasi kristal berenergi tinggi (sizzling extraction sparks)
///      yang berakselerasi saat voxel memanas menjelang pecah.
///   4. MINERAL VOXEL BREAK & PICKUP: Denting rekahan kristal yang renyah ("ching-clink!") dengan ekor lonceng resonan.
///
/// BUS 3 — FRAKTUR TEKTONIK & LEDAKAN BATUAN HANCUR (DESTRUCTION & SHOCKWAVE):
///   1. CATASTROPHIC SPIDERWEB FRACTURE (_isDestroying): Gemuruh tektonik rendah saat retakan menjalar.
///   2. ASTEROID SHATTER & SUPERNOVA: Dentuman sub-bass dalam + runtuhan batu/kristal yang bulat.
/// </summary>
public partial class EngineThrusterAudio : AudioStreamPlayer
{
    public static EngineThrusterAudio? Instance { get; private set; }

    private AudioStreamGeneratorPlayback? _playback;
    private AudioStreamGenerator? _generator;
    private PlayerCore? _playerCore;

    private const float SampleRate = 44100.0f;
    private const float InvSampleRate = 1.0f / SampleRate;
    private const float TwoPi = Mathf.Tau;

    // Horizon buffer rendah-latensi (~45ms lead) di dalam ring buffer 220ms
    private const float GeneratorBufferSeconds = 0.22f;
    private const int TotalBufferFrames = (int)(SampleRate * GeneratorBufferSeconds);
    private const int TargetLeadFrames = 1984;
    private const int EmergencyPreFillFrames = 768;

    // =========================================================================
    // BUS 1: STATE OSILATOR PROPULSI MEGAH & SWARM HARMONIC PAD
    // =========================================================================
    private float _phaseSubL = 0.0f;
    private float _phaseSubR = 0.0f;
    private float _phaseSwarmFifthL = 0.0f;
    private float _phaseSwarmFifthR = 0.0f;
    private float _phaseSwarmOctL = 0.0f;
    private float _phaseSwarmOctR = 0.0f;
    private float _phaseSwarmLfo = 0.0f;

    private float _phaseChargeL = 0.0f;
    private float _phaseChargeR = 0.0f;
    private float _phaseChargeSub = 0.0f;
    private float _phaseChargeFifth = 0.0f;

    private float _phaseThump = 0.0f;
    private float _phasePlasmaSubL = 0.0f;
    private float _phasePlasmaSubR = 0.0f;
    private float _phasePlasmaFifthL = 0.0f;
    private float _phasePlasmaFifthR = 0.0f;
    private float _phasePlasmaOct = 0.0f;
    private float _phasePlasmaChorusLfo = 0.0f;

    // Filter Low-Pass 4-kutub (24dB/oct) agar suara propulsi & swarm selalu empuk dan bebas frekuensi tajam
    private float _propLp1L = 0.0f;
    private float _propLp2L = 0.0f;
    private float _propLp1R = 0.0f;
    private float _propLp2R = 0.0f;

    // Semburan Jet Exhaust Thruster & Modulasi Denyut Getaran Mesin
    private float _phaseThrusterPulse = 0.0f;
    private float _jetLp1L = 0.0f;
    private float _jetLp2L = 0.0f;
    private float _jetHpL = 0.0f;
    private float _jetLp1R = 0.0f;
    private float _jetLp2R = 0.0f;
    private float _jetHpR = 0.0f;

    // Steam Vapor Filter States (2-pole LP + 1-pole HP Stereo)
    private float _steamLp1L = 0.0f;
    private float _steamLp2L = 0.0f;
    private float _steamHpL = 0.0f;
    private float _steamLp1R = 0.0f;
    private float _steamLp2R = 0.0f;
    private float _steamHpR = 0.0f;

    private const float SteamAlphaLp = 0.20f; // LP ~1350 Hz (lebih hangat & lembut di telinga)
    private const float SteamAlphaHp = 0.07f; // HP ~480 Hz

    // Target vs Current (Per-sample interpolated)
    private float _targetSubVol = 0.0f;
    private float _curSubVol = 0.0f;
    private float _targetSubFreq = 58.0f;
    private float _curSubFreq = 58.0f;

    private float _targetThrusterJetVol = 0.0f;
    private float _curThrusterJetVol = 0.0f;

    private float _targetSwarmPadVol = 0.0f;
    private float _curSwarmPadVol = 0.0f;

    private float _targetChargeEnv = 0.0f;
    private float _curChargeEnv = 0.0f;
    private float _targetChargeFreq = 65.0f;
    private float _curChargeFreq = 65.0f;
    private float _targetChargeRichness = 0.0f;
    private float _curChargeRichness = 0.0f;

    private float _targetPlasmaVol = 0.0f;
    private float _curPlasmaVol = 0.0f;
    private float _targetBrakeVol = 0.0f;
    private float _curBrakeVol = 0.0f;

    private float _blastThumpEnv = 0.0f;
    private float _launchChordEnv = 0.0f;
    private bool _wasPriming = false;

    // =========================================================================
    // BUS 2: STATE PENAMBANGAN (SOFT VAPORIZATION, MINERAL RESONANCE & SPARKS, GIGA DRILL)
    // =========================================================================
    private bool _isLaserPriming = false;
    private float _targetLaserBeamVol = 0.0f;
    private float _curLaserBeamVol = 0.0f;
    private float _targetLaserFreq = 95.0f;
    private float _curLaserFreq = 95.0f;

    // Batuan tergerus = suara menguap halus & pelan (Soft Rock Vaporization)
    private float _targetRockVaporVol = 0.0f;
    private float _curRockVaporVol = 0.0f;

    // Resource Patch Mining: Resonansi Kord Kristal & Percikan Ionisasi Sizzling
    private float _targetMineralResonanceVol = 0.0f;
    private float _curMineralResonanceVol = 0.0f;
    private float _targetMineralFreq = 523.25f;
    private float _curMineralFreq = 523.25f;
    private float _targetMineralHeat = 0.0f;
    private float _curMineralHeat = 0.0f;

    private float _targetAetherSiphonVol = 0.0f;
    private float _curAetherSiphonVol = 0.0f;
    private float _targetLaserPan = 0.0f;
    private float _curLaserPan = 0.0f;

    private float _targetDrillGrindVol = 0.0f;
    private float _curDrillGrindVol = 0.0f;
    private float _targetDrillFreq = 62.0f;
    private float _curDrillFreq = 62.0f;
    private float _targetDrillPan = 0.0f;
    private float _curDrillPan = 0.0f;

    private float _phaseLaserFund = 0.0f;
    private float _phaseLaserDetune = 0.0f;
    private float _phaseLaserLfo = 0.0f;
    private float _phaseVaporBreath = 0.0f;

    private float _phaseMineralCarL = 0.0f;
    private float _phaseMineralCarR = 0.0f;
    private float _phaseMineralFifth = 0.0f;
    private float _phaseMineralOct = 0.0f;
    private float _phaseMineralChorusLfo = 0.0f;
    private float _phaseMineralSparkleLfo = 0.0f;

    private float _mineralSparkleLp1L = 0.0f;
    private float _mineralSparkleLp2L = 0.0f;
    private float _mineralSparkleHpL = 0.0f;
    private float _mineralSparkleLp1R = 0.0f;
    private float _mineralSparkleLp2R = 0.0f;
    private float _mineralSparkleHpR = 0.0f;

    private float _phaseAetherSiphon1 = 0.0f;
    private float _phaseAetherSiphon2 = 0.0f;
    private float _phaseDrillFund = 0.0f;
    private float _phaseDrillMod = 0.0f;

    // Filter uap batuan tergerus (4-pole soft vapor bandpass, tanpa gerusan kasar)
    private float _vaporLp1L = 0.0f;
    private float _vaporLp2L = 0.0f;
    private float _vaporHpL = 0.0f;
    private float _vaporLp1R = 0.0f;
    private float _vaporLp2R = 0.0f;
    private float _vaporHpR = 0.0f;

    private float _drillNoiseLpL = 0.0f;
    private float _drillNoiseLpR = 0.0f;

    private double _lastRockVaporPuffTimeSec = -10.0;
    private double _lastCrystalChimeTimeSec = -10.0;
    private double _lastPickupChimeTimeSec = -10.0;
    private double _elapsedTimeSec = 0.0;

    private struct VoxelGrainVoice
    {
        public bool Active;
        public bool IsCrystal;
        public float Env;
        public float DecayPerSample;
        public float NoiseEnv;
        public float NoiseDecayPerSample;
        public float Phase1;
        public float Phase2;
        public float FreqStart;
        public float FreqEnd;
        public float FilterState;
        public float FilterState2;
        public float FilterAlpha;
        public float GainL;
        public float GainR;
    }

    private const int MaxGrainVoices = 10;
    private readonly VoxelGrainVoice[] _grainVoices = new VoxelGrainVoice[MaxGrainVoices];
    private int _nextGrainVoiceCursor = 0;

    // =========================================================================
    // DART NEEDLE AUDIO (PELUNCURAN HALUS & IMPAK 4-KATEGORI BERBEDA)
    // =========================================================================
    public enum DartImpactKind : byte
    {
        Enemy = 0,         // Menabrak drone musuh / proyektil musuh / core musuh
        Resource = 1,      // Menabrak vena resource mineral (Ferrite, Carbide, Quartz, Vortexum, Aether)
        AetherStar = 2,    // Menabrak Bintang Aether (Energy Asteroid)
        RockAsteroid = 3   // Menabrak batuan asteroid biasa
    }

    private enum DartVoiceKind : byte
    {
        Launch = 0,
        ImpactEnemy = 1,
        ImpactResource = 2,
        ImpactAetherStar = 3,
        ImpactRockAsteroid = 4
    }

    private struct DartVoice
    {
        public bool Active;
        public DartVoiceKind Kind;
        public float AgeSeconds;
        public float DurationSeconds;
        public float Phase1;
        public float Phase2;
        public float FreqStart;
        public float FreqEnd;
        public float FilterStateL;
        public float FilterStateR;
        public float GainL;
        public float GainR;
    }

    private const int MaxDartVoices = 16;
    private readonly DartVoice[] _dartVoices = new DartVoice[MaxDartVoices];
    private int _nextDartVoiceCursor = 0;

    private double _lastDartLaunchTimeSec = -10.0;
    private double _lastDartImpactEnemyTimeSec = -10.0;
    private double _lastDartImpactResourceTimeSec = -10.0;
    private double _lastDartImpactAetherTimeSec = -10.0;
    private double _lastDartImpactRockTimeSec = -10.0;

    // =========================================================================
    // BUS 3: STATE RETAKAN TEKTONIK, LEDAKAN BATUAN HANCUR, & GELOMBANG KEJUT
    // =========================================================================
    private float _targetFractureCreakVol = 0.0f;
    private float _curFractureCreakVol = 0.0f;
    private float _targetFractureFreq = 44.0f;
    private float _curFractureFreq = 44.0f;
    private float _targetFracturePan = 0.0f;
    private float _curFracturePan = 0.0f;
    private float _phaseFractureSub = 0.0f;
    private float _phaseFractureTension = 0.0f;
    private float _fractureCrackTimer = 0.0f;

    private enum DestructionSoundKind : byte
    {
        RockAsteroidShatter = 0,
        AetherSupernova = 1,
        BoulderCollisionSlam = 2,
        PreSupernovaPulse = 3,
        DrillCraterBlast = 4
    }

    private struct DestructionVoice
    {
        public bool Active;
        public DestructionSoundKind Kind;
        public float AgeSeconds;
        public float DurationSeconds;
        public float Scale;
        public float GainL;
        public float GainR;

        public float SubPhase;
        public float BodyPhase1;
        public float BodyPhase2;
        public float ShimmerPhase1;
        public float ShimmerPhase2;

        public float SvfIc1L;
        public float SvfIc2L;
        public float SvfIc1R;
        public float SvfIc2R;

        public float ClatterEnv;
        public float NextClatterTime;
        public uint LocalRng;
    }

    private const int MaxDestructionVoices = 6;
    private readonly DestructionVoice[] _destructionVoices = new DestructionVoice[MaxDestructionVoices];
    private int _nextDestructionVoiceCursor = 0;

    private float _explosionDuckEnv = 0.0f;

    // PRNG states
    private uint _rngL = 123456789u;
    private uint _rngR = 987654321u;
    private uint _rngSfx = 543216789u;

    // Master DC Blocker states
    private float _dcPrevInL = 0.0f;
    private float _dcPrevOutL = 0.0f;
    private float _dcPrevInR = 0.0f;
    private float _dcPrevOutR = 0.0f;

    public override void _EnterTree()
    {
        Instance = this;
    }

    public override void _ExitTree()
    {
        if (Instance == this)
        {
            Instance = null;
        }
    }

    public override void _Ready()
    {
        Instance = this;

        _generator = new AudioStreamGenerator
        {
            MixRate = SampleRate,
            BufferLength = GeneratorBufferSeconds
        };

        Stream = _generator;
        Bus = "Master";
        Play();

        _playback = GetStreamPlayback() as AudioStreamGeneratorPlayback;
        _playerCore = GetParent() as PlayerCore ?? PlayerCore.Instance;

        PreFillInitialHorizon();
    }

    private void PreFillInitialHorizon()
    {
        if (_playback == null) return;
        int available = _playback.GetFramesAvailable();
        int toPush = Math.Min(available, EmergencyPreFillFrames);
        for (int i = 0; i < toPush; i++)
        {
            _playback.PushFrame(Vector2.Zero);
        }
    }

    public override void _Process(double delta)
    {
        if (!Playing)
        {
            Play();
            _playback = GetStreamPlayback() as AudioStreamGeneratorPlayback;
            PreFillInitialHorizon();
        }

        if (_playback == null)
        {
            _playback = GetStreamPlayback() as AudioStreamGeneratorPlayback;
            if (_playback == null) return;
        }

        if (_playerCore == null || !IsInstanceValid(_playerCore))
        {
            _playerCore = GetParent() as PlayerCore ?? PlayerCore.Instance;
        }

        float dt = Mathf.Clamp((float)delta, 0.0001f, 0.1f);
        _elapsedTimeSec += dt;

        UpdateParameters(dt);
        SynthesizeAudio();
    }

    // =========================================================================
    // PEMICU TRANSIEN AUDIO PUBLIK (DIPANGGIL DARI SWARMSIMULATION & ASTEROID)
    // =========================================================================

    /// <summary>
    /// Saat blok voxel hancur:
    /// - Untuk batu biasa (VoxelType.Rock): hembusan uap lembut & samar ("pssh" menguap pelan, tidak keras).
    /// - Untuk kristal mineral: denting harmonik lembut yang nyaman di telinga.
    /// </summary>
    public void TriggerVoxelBreak(Vector2 worldPos, Asteroid.VoxelType voxelType)
    {
        if (voxelType == Asteroid.VoxelType.Empty) return;

        ComputeSpatialGain(worldPos, maxDist: 1650.0f, out float atten, out float pan);
        if (atten <= 0.015f) return;

        bool isCrystal = voxelType != Asteroid.VoxelType.Rock;

        if (!isCrystal)
        {
            // Batuan tergerus/pecah dianggap menguap halus: jeda 55ms dan volume sangat lembut (0.032f)
            if (_elapsedTimeSec - _lastRockVaporPuffTimeSec < 0.055) return;
            _lastRockVaporPuffTimeSec = _elapsedTimeSec;

            float pitchJitter = 0.88f + NextFloat01(ref _rngSfx) * 0.24f;
            SpawnGrainVoice(
                isCrystal: false,
                freqStart: 135.0f * pitchJitter,
                freqEnd: 58.0f * pitchJitter,
                toneDecaySec: 0.075f,
                noiseDecaySec: 0.085f,
                filterCutoffHz: 620.0f * pitchJitter,
                volume: 0.032f * atten, // Sangat pelan & halus seperti desis uap kecil
                pan: pan
            );
        }
        else
        {
            if (_elapsedTimeSec - _lastCrystalChimeTimeSec < 0.024) return;
            _lastCrystalChimeTimeSec = _elapsedTimeSec;

            float basePitch = voxelType switch
            {
                Asteroid.VoxelType.ResourceFerrite => 329.63f,  // E4 (Warm Bronze)
                Asteroid.VoxelType.ResourceCarbide => 261.63f,  // C4 (Deep Vulcan Magma / Carbide)
                Asteroid.VoxelType.ResourceQuartz => 523.25f,   // C5 (Smooth Cyan Quartz)
                Asteroid.VoxelType.ResourceVortexum => 440.00f, // A4 (Emerald Kinetic Resonator)
                Asteroid.VoxelType.ResourceAether => 783.99f,   // G5 (Celestial Gold)
                _ => 523.25f
            };

            float detune = 0.985f + NextFloat01(ref _rngSfx) * 0.03f;
            float decay = (voxelType == Asteroid.VoxelType.ResourceAether) ? 0.28f : 0.22f;

            // Denting kristal renyah ("ching-clink!") yang memuaskan saat blok resource pecah
            SpawnGrainVoice(
                isCrystal: true,
                freqStart: basePitch * detune,
                freqEnd: basePitch * detune * 2.0f,
                toneDecaySec: decay,
                noiseDecaySec: 0.020f,
                filterCutoffHz: 3400.0f,
                volume: 0.20f * atten,
                pan: pan
            );
        }
    }

    /// <summary>
    /// Denting harmonik kristal saat serpihan mineral diserap masuk ke dalam Core kapal induk.
    /// </summary>
    public void TriggerCrystalPickup(ResourceType resourceType)
    {
        if (_elapsedTimeSec - _lastPickupChimeTimeSec < 0.032) return;
        _lastPickupChimeTimeSec = _elapsedTimeSec;

        float basePitch = resourceType switch
        {
            ResourceType.Ferrite => 392.00f,  // G4
            ResourceType.Carbide => 440.00f,  // A4
            ResourceType.Quartz => 523.25f,   // C5
            ResourceType.Vortexum => 659.25f, // E5
            ResourceType.Aether => 880.00f,   // A5
            _ => 523.25f
        };

        float jitter = 0.992f + NextFloat01(ref _rngSfx) * 0.016f;
        SpawnGrainVoice(
            isCrystal: true,
            freqStart: basePitch * jitter,
            freqEnd: basePitch * jitter * 2.0f,
            toneDecaySec: 0.14f,
            noiseDecaySec: 0.010f,
            filterCutoffHz: 3200.0f,
            volume: 0.14f,
            pan: (NextFloat01(ref _rngSfx) - 0.5f) * 0.24f
        );
    }

    /// <summary>
    /// Peluncuran Dart Needle: suara halus, aerodinamis, dan futuristik ("swiiip" desis jarum magnetik berkecepatan tinggi).
    /// </summary>
    public void TriggerDartLaunch(Vector2 worldPos)
    {
        if (_elapsedTimeSec - _lastDartLaunchTimeSec < 0.022) return;
        _lastDartLaunchTimeSec = _elapsedTimeSec;

        ComputeSpatialGain(worldPos, maxDist: 1800.0f, out float atten, out float pan);
        if (atten <= 0.02f) return;

        float pitchJitter = 0.96f + NextFloat01(ref _rngSfx) * 0.08f;
        SpawnDartVoice(
            kind: DartVoiceKind.Launch,
            duration: 0.048f,
            freqStart: 590.0f * pitchJitter,
            freqEnd: 240.0f * pitchJitter,
            volume: 0.16f * atten,
            pan: pan
        );
    }

    /// <summary>
    /// Benturan Dart Needle pada 4 target berbeda: Musuh, Resource, Bintang Aether, dan Batu Asteroid.
    /// Masing-masing memiliki timbre dan karakter akustik yang sangat kontras.
    /// </summary>
    public void TriggerDartImpact(Vector2 worldPos, DartImpactKind kind, Asteroid.VoxelType resourceType = Asteroid.VoxelType.Empty)
    {
        ComputeSpatialGain(worldPos, maxDist: 2100.0f, out float atten, out float pan);
        if (atten <= 0.02f) return;

        switch (kind)
        {
            case DartImpactKind.Enemy:
            {
                if (_elapsedTimeSec - _lastDartImpactEnemyTimeSec < 0.016) return;
                _lastDartImpactEnemyTimeSec = _elapsedTimeSec;

                float pitchJitter = 0.94f + NextFloat01(ref _rngSfx) * 0.12f;
                SpawnDartVoice(
                    kind: DartVoiceKind.ImpactEnemy,
                    duration: 0.065f,
                    freqStart: 980.0f * pitchJitter,
                    freqEnd: 220.0f * pitchJitter,
                    volume: 0.32f * atten,
                    pan: pan
                );
                break;
            }

            case DartImpactKind.Resource:
            {
                if (_elapsedTimeSec - _lastDartImpactResourceTimeSec < 0.018) return;
                _lastDartImpactResourceTimeSec = _elapsedTimeSec;

                float basePitch = resourceType switch
                {
                    Asteroid.VoxelType.ResourceFerrite => 1318.5f,  // E6 (Bronze Logam)
                    Asteroid.VoxelType.ResourceCarbide => 1046.5f,  // C6 (Vulcan Magma)
                    Asteroid.VoxelType.ResourceQuartz => 2093.0f,   // C7 (Cyan Crystal)
                    Asteroid.VoxelType.ResourceVortexum => 1760.0f, // A6 (Emerald Kinetic)
                    Asteroid.VoxelType.ResourceAether => 1567.98f,  // G6 (Celestial Gold)
                    _ => 1567.98f
                };

                float pitchJitter = 0.985f + NextFloat01(ref _rngSfx) * 0.03f;
                SpawnDartVoice(
                    kind: DartVoiceKind.ImpactResource,
                    duration: 0.115f,
                    freqStart: basePitch * pitchJitter,
                    freqEnd: basePitch * pitchJitter * 2.0f,
                    volume: 0.32f * atten,
                    pan: pan
                );
                break;
            }

            case DartImpactKind.AetherStar:
            {
                if (_elapsedTimeSec - _lastDartImpactAetherTimeSec < 0.022) return;
                _lastDartImpactAetherTimeSec = _elapsedTimeSec;

                float pitchJitter = 0.96f + NextFloat01(ref _rngSfx) * 0.08f;
                SpawnDartVoice(
                    kind: DartVoiceKind.ImpactAetherStar,
                    duration: 0.150f,
                    freqStart: 587.33f * pitchJitter, // D5 (Fundamental Ethereal)
                    freqEnd: 880.00f * pitchJitter,   // A5 (Harmonic Fifth)
                    volume: 0.34f * atten,
                    pan: pan
                );
                break;
            }

            case DartImpactKind.RockAsteroid:
            {
                if (_elapsedTimeSec - _lastDartImpactRockTimeSec < 0.020) return;
                _lastDartImpactRockTimeSec = _elapsedTimeSec;

                float pitchJitter = 0.90f + NextFloat01(ref _rngSfx) * 0.20f;
                SpawnDartVoice(
                    kind: DartVoiceKind.ImpactRockAsteroid,
                    duration: 0.055f,
                    freqStart: 155.0f * pitchJitter,
                    freqEnd: 52.0f * pitchJitter,
                    volume: 0.24f * atten,
                    pan: pan
                );
                break;
            }
        }
    }

    private void SpawnDartVoice(
        DartVoiceKind kind,
        float duration,
        float freqStart,
        float freqEnd,
        float volume,
        float pan)
    {
        int slot = _nextDartVoiceCursor;
        _nextDartVoiceCursor = (_nextDartVoiceCursor + 1) % MaxDartVoices;

        StereoPanFactors(pan, out float panL, out float panR);

        _dartVoices[slot] = new DartVoice
        {
            Active = true,
            Kind = kind,
            AgeSeconds = 0.0f,
            DurationSeconds = MathF.Max(0.02f, duration),
            Phase1 = NextFloat01(ref _rngSfx) * 0.25f,
            Phase2 = 0.0f,
            FreqStart = freqStart,
            FreqEnd = freqEnd,
            FilterStateL = 0.0f,
            FilterStateR = 0.0f,
            GainL = volume * panL,
            GainR = volume * panR
        };
    }

    /// <summary>
    /// Dipanggil saat asteroid mulai mengalami keretakan jaring laba-laba katastropik (StartDestructionSequence).
    /// </summary>
    public void TriggerCatastrophicFractureStart(Vector2 worldPos, float asteroidRadius, bool isEnergy)
    {
        ComputeSpatialGain(worldPos, maxDist: 2600.0f, out float atten, out float pan);
        if (atten <= 0.01f) return;

        float sizeScale = Mathf.Clamp(asteroidRadius / 150.0f, 0.65f, 1.45f);
        SpawnGrainVoice(
            isCrystal: isEnergy,
            freqStart: isEnergy ? 280.0f : 145.0f,
            freqEnd: isEnergy ? 95.0f : 44.0f,
            toneDecaySec: 0.14f * sizeScale,
            noiseDecaySec: 0.085f * sizeScale,
            filterCutoffHz: isEnergy ? 1350.0f : 580.0f,
            volume: 0.22f * atten,
            pan: pan
        );
    }

    /// <summary>
    /// Memicu ledakan saat batuan asteroid hancur berkeping-keping atau Bintang Aether meledak Supernova.
    /// </summary>
    public void TriggerAsteroidDestruction(Vector2 worldPos, float asteroidRadius, bool isEnergy)
    {
        float maxDist = isEnergy ? 3800.0f : 2900.0f;
        ComputeSpatialGain(worldPos, maxDist, out float atten, out float pan);
        if (atten <= 0.01f) return;

        float sizeScale = Mathf.Clamp(asteroidRadius / 155.0f, 0.70f, 1.55f);
        float duration = isEnergy
            ? Mathf.Clamp(1.05f * sizeScale, 0.85f, 1.45f)
            : Mathf.Clamp(0.68f * sizeScale, 0.52f, 0.92f);

        SpawnDestructionVoice(
            kind: isEnergy ? DestructionSoundKind.AetherSupernova : DestructionSoundKind.RockAsteroidShatter,
            duration: duration,
            scale: sizeScale,
            volume: (isEnergy ? 0.76f : 0.66f) * atten,
            pan: pan
        );

        _explosionDuckEnv = MathF.Max(_explosionDuckEnv, Mathf.Clamp(0.48f * atten, 0.0f, 0.55f));
    }

    /// <summary>
    /// Memicu suara benturan batu berat saat dua asteroid saling bertabrakan keras akibat gelombang kejut.
    /// </summary>
    public void TriggerAsteroidCollisionImpact(Vector2 worldPos, float impactSpeed)
    {
        ComputeSpatialGain(worldPos, maxDist: 2400.0f, out float atten, out float pan);
        if (atten <= 0.015f) return;

        float intensity = Mathf.Clamp(impactSpeed / 240.0f, 0.30f, 1.35f);
        SpawnDestructionVoice(
            kind: DestructionSoundKind.BoulderCollisionSlam,
            duration: Mathf.Lerp(0.28f, 0.48f, Mathf.Clamp(intensity, 0.0f, 1.0f)),
            scale: intensity,
            volume: 0.42f * intensity * atten,
            pan: pan
        );
    }

    /// <summary>
    /// Memicu denyut gelombang kejut magnetik saat Bintang Aether memancarkan shockwave menjelang Supernova.
    /// </summary>
    public void TriggerPreSupernovaPulse(Vector2 worldPos, float progress)
    {
        ComputeSpatialGain(worldPos, maxDist: 3000.0f, out float atten, out float pan);
        if (atten <= 0.015f) return;

        float intensity = Mathf.Lerp(0.55f, 1.15f, Mathf.Clamp(progress, 0.0f, 1.0f));
        SpawnDestructionVoice(
            kind: DestructionSoundKind.PreSupernovaPulse,
            duration: 0.34f,
            scale: intensity,
            volume: 0.38f * atten,
            pan: pan
        );
    }

    /// <summary>
    /// Memicu dentuman kawah lokal saat Super Giga Drill selesai menggerus pinggir batu dan meledak.
    /// </summary>
    public void TriggerCraterBlast(Vector2 worldPos, float craterRadius)
    {
        ComputeSpatialGain(worldPos, maxDist: 2200.0f, out float atten, out float pan);
        if (atten <= 0.015f) return;

        float scale = Mathf.Clamp(craterRadius / 28.0f, 0.75f, 1.35f);
        SpawnDestructionVoice(
            kind: DestructionSoundKind.DrillCraterBlast,
            duration: 0.44f,
            scale: scale,
            volume: 0.48f * atten,
            pan: pan
        );
        _explosionDuckEnv = MathF.Max(_explosionDuckEnv, 0.30f * atten);
    }

    private void SpawnGrainVoice(
        bool isCrystal,
        float freqStart,
        float freqEnd,
        float toneDecaySec,
        float noiseDecaySec,
        float filterCutoffHz,
        float volume,
        float pan)
    {
        int slot = _nextGrainVoiceCursor;
        _nextGrainVoiceCursor = (_nextGrainVoiceCursor + 1) % MaxGrainVoices;

        StereoPanFactors(pan, out float panL, out float panR);
        float alpha = Mathf.Clamp(TwoPi * filterCutoffHz * InvSampleRate, 0.03f, 0.65f);

        _grainVoices[slot] = new VoxelGrainVoice
        {
            Active = true,
            IsCrystal = isCrystal,
            Env = 1.0f,
            DecayPerSample = MathF.Exp(-4.6f / MathF.Max(120.0f, toneDecaySec * SampleRate)),
            NoiseEnv = 1.0f,
            NoiseDecayPerSample = MathF.Exp(-4.6f / MathF.Max(80.0f, noiseDecaySec * SampleRate)),
            Phase1 = NextFloat01(ref _rngSfx) * 0.25f,
            Phase2 = 0.0f,
            FreqStart = freqStart,
            FreqEnd = freqEnd,
            FilterState = 0.0f,
            FilterState2 = 0.0f,
            FilterAlpha = alpha,
            GainL = volume * panL,
            GainR = volume * panR
        };
    }

    private void SpawnDestructionVoice(
        DestructionSoundKind kind,
        float duration,
        float scale,
        float volume,
        float pan)
    {
        int slot = _nextDestructionVoiceCursor;
        _nextDestructionVoiceCursor = (_nextDestructionVoiceCursor + 1) % MaxDestructionVoices;

        StereoPanFactors(pan, out float panL, out float panR);

        _destructionVoices[slot] = new DestructionVoice
        {
            Active = true,
            Kind = kind,
            AgeSeconds = 0.0f,
            DurationSeconds = MathF.Max(0.15f, duration),
            Scale = scale,
            GainL = volume * panL,
            GainR = volume * panR,
            SubPhase = 0.0f,
            BodyPhase1 = 0.0f,
            BodyPhase2 = 0.25f,
            ShimmerPhase1 = 0.0f,
            ShimmerPhase2 = 0.0f,
            SvfIc1L = 0.0f,
            SvfIc2L = 0.0f,
            SvfIc1R = 0.0f,
            SvfIc2R = 0.0f,
            ClatterEnv = 1.0f,
            NextClatterTime = 0.035f,
            LocalRng = (_rngSfx ^ (uint)(slot * 7919 + 17)) | 1u
        };
    }

    // =========================================================================
    // PEMBARUAN TARGET PARAMETER KONTINU (PROPULSI MEGAH, MINING UAP, & FRAKTUR)
    // =========================================================================
    private void UpdateParameters(float dt)
    {
        bool isThrusting = false;
        bool isPriming = false;
        bool isBoosting = false;
        bool isAlive = true;
        float speed = 0.0f;
        float speedRatio = 0.0f;
        float primingProgress = 0.0f;

        if (_playerCore != null && IsInstanceValid(_playerCore))
        {
            isThrusting = _playerCore.IsThrusting;
            isPriming = _playerCore.IsBoostPriming;
            isBoosting = _playerCore.IsBoosting || (_playerCore.IsEscapePod && _playerCore.EscapeBurstTimer > 0.0f);
            primingProgress = _playerCore.BoostPrimingProgress;
            isAlive = _playerCore.Health > 0.0f;

            speed = _playerCore.Velocity.Length();
            speedRatio = Mathf.Clamp(speed / MathF.Max(1.0f, _playerCore.MaxSpeed), 0.0f, 1.25f);
        }

        var sim = SwarmSimulation.Instance;
        int playerSwarmCount = (sim != null && IsInstanceValid(sim))
            ? (sim.DartNeedleCount + sim.MiningCount + sim.GuardianCount + sim.DrillDroneCount + sim.GigaShurikenCount + sim.CrescentBoomerangCount + sim.ScannerCount)
            : 30;
        float swarmSizeFactor = Mathf.Clamp(playerSwarmCount / 95.0f, 0.25f, 1.0f);

        // ---------------------------------------------------------------------
        // 1. TARGET BUS PROPULSI MEGAH & SWARM HARMONIC PAD
        // ---------------------------------------------------------------------
        if (!isAlive)
        {
            _targetSubVol = 0.0f;
            _targetThrusterJetVol = 0.0f;
            _targetSwarmPadVol = 0.0f;
            _targetChargeEnv = 0.0f;
            _targetChargeRichness = 0.0f;
            _targetPlasmaVol = 0.0f;
            _targetBrakeVol = 0.0f;
            _wasPriming = false;
        }
        else
        {
            // Deteksi transisi dari Priming -> Full Boost (Memicu Sub-Bloom & Launch Chord)
            if (_wasPriming && isBoosting && !isPriming)
            {
                _blastThumpEnv = 1.0f;
                _launchChordEnv = 1.0f;
                _phaseThump = 0.0f;
            }
            _wasPriming = isPriming;

            // A. Priming Boost [Tahan SPACE]: Swell Kumparan Reaktor 3-Tahap ("ngung... nguuung... NGUUUNG")
            //    Jelas terdengar tahapan pengisian dayanya secara berurutan, namun tetap empuk & nyaman di telinga
            float chargeEnv = 0.0f;
            float chargeFreq = 65.0f;
            float chargeRichness = 0.0f;

            if (isPriming)
            {
                float p = primingProgress;
                // Pengisian bertingkat 3 tahap yang jelas terdengar secara kontinu tanpa jeda hening:
                if (p < 0.33f)
                {
                    float t1 = p / 0.33f;
                    chargeFreq = Mathf.Lerp(65.0f, 85.0f, t1);
                    chargeRichness = Mathf.Lerp(0.20f, 0.40f, t1);
                    chargeEnv = Mathf.Lerp(0.18f, 0.28f, t1);
                }
                else if (p < 0.66f)
                {
                    float t2 = (p - 0.33f) / 0.33f;
                    chargeFreq = Mathf.Lerp(88.0f, 115.0f, t2);
                    chargeRichness = Mathf.Lerp(0.40f, 0.70f, t2);
                    chargeEnv = Mathf.Lerp(0.28f, 0.36f, t2);
                }
                else
                {
                    float t3 = (p - 0.66f) / 0.34f;
                    chargeFreq = Mathf.Lerp(118.0f, 155.0f, t3);
                    chargeRichness = Mathf.Lerp(0.70f, 1.0f, t3);
                    chargeEnv = Mathf.Lerp(0.36f, 0.44f, t3);
                }
            }

            _targetChargeEnv = chargeEnv;
            _targetChargeFreq = chargeFreq;
            _targetChargeRichness = chargeRichness;

            // B. Move Core & Swarm (WASD Hover & Cruising Glide)
            //    Karakter: Suara medan magnetik / gravitasi yang lembut, empuk, dan melayang (Hover) + desis jet hangat
            float subVol = 0.0f;
            float jetVol = 0.0f;
            float swarmPadVol = 0.0f;
            float subFreq = 58.0f;

            if (!isBoosting && !isPriming)
            {
                if (isThrusting)
                {
                    // Dengungan magnetik / gravitasi saat mengarahkan Core kapal (Hover dengan mid presence yang jelas)
                    subVol = 0.38f;
                    jetVol = 0.14f; // Desis jet thruster hangat agar suasana tidak sepi / hampa
                    swarmPadVol = Mathf.Lerp(0.12f, 0.18f, swarmSizeFactor);
                    subFreq = Mathf.Lerp(58.0f, 78.0f, Mathf.Clamp(speedRatio, 0.0f, 1.0f));
                }
                else if (speed > 16.0f)
                {
                    float glideT = Mathf.Clamp(speedRatio, 0.0f, 1.0f);
                    subVol = Mathf.Lerp(0.10f, 0.22f, glideT);
                    jetVol = Mathf.Lerp(0.02f, 0.06f, glideT);
                    swarmPadVol = Mathf.Lerp(0.06f, 0.12f, glideT) * (0.65f + 0.35f * swarmSizeFactor);
                    subFreq = Mathf.Lerp(52.0f, 66.0f, glideT);
                }
            }
            _targetSubVol = subVol;
            _targetThrusterJetVol = jetVol; // Desis semburan jet thruster halus
            _targetSwarmPadVol = swarmPadVol;
            _targetSubFreq = subFreq;

            // C. Meluncur [Lepas SPACE / Boost]: Bertenaga, Mantap, dan Agak Kencang!
            _targetPlasmaVol = (isBoosting && !isPriming) ? 0.65f : 0.0f;
            _targetBrakeVol = (sim != null && sim.IsBrakeActive && !isBoosting) ? 0.16f : 0.0f;
        }

        // ---------------------------------------------------------------------
        // 2. TARGET BUS PENAMBANGAN (PRIMING FOKUS, LASER CUTTING, & RESONANSI MINERAL)
        // ---------------------------------------------------------------------
        float laserBeamVol = 0.0f;
        float laserFreq = 95.0f;
        float rockVaporVol = 0.0f;
        float mineralResVol = 0.0f;
        float mineralFreq = _targetMineralFreq;
        float aetherSiphonVol = 0.0f;
        float laserPan = 0.0f;

        float drillGrindVol = 0.0f;
        float drillFreq = 62.0f;
        float drillPan = 0.0f;

        if (sim != null && IsInstanceValid(sim))
        {
            // A. Laser Penambangan Pemain
            if (sim.IsMiningLaserActive)
            {
                int tierSpan = Math.Clamp(sim.ActiveLaserBlockSpan, 1, 5);
                int minerCount = Math.Max(1, sim.ActiveLaserMinerCount);
                float tierBoost = 1.0f + (tierSpan - 1) * 0.10f;

                ComputeSpatialGain(sim.LaserImpactWorldPos, maxDist: 2200.0f, out float hitAtten, out float hitPan);
                laserPan = hitPan * 0.60f;

                if (sim.IsMiningLaserPriming)
                {
                    // Tahap Priming Laser Penambang (280ms): Desir kapasitor optik mengumpulkan fokus
                    _isLaserPriming = true;
                    float p = sim.MiningLaserPrimingProgress;
                    laserBeamVol = Mathf.Lerp(0.20f, 0.36f, p);
                    laserFreq = Mathf.Lerp(180.0f, 520.0f, p * p);
                    _targetMineralHeat = 0.0f;
                }
                else
                {
                    _isLaserPriming = false;
                    // Berkas laser penuh: jelas terdengar, fokus, dan bertenaga
                    laserBeamVol = Mathf.Clamp(0.36f * tierBoost, 0.34f, 0.44f);
                    laserFreq = 95.0f + tierSpan * 6.0f + MathF.Min(16.0f, minerCount * 0.30f);

                    if (sim.IsMiningLaserHittingAsteroid)
                    {
                        if (sim.IsMiningEnergyAsteroid)
                        {
                            aetherSiphonVol = 0.28f * MathF.Max(0.50f, hitAtten);
                            rockVaporVol = 0.030f * hitAtten;
                            _targetMineralHeat = 0.0f;
                        }
                        else
                        {
                            if (sim.IsMiningResourceVein)
                            {
                                float heatFactor = Mathf.Clamp(sim.MaxActiveVoxelHeat, 0.15f, 1.0f);
                                mineralResVol = (0.24f + 0.16f * heatFactor) * MathF.Max(0.55f, hitAtten);
                                float baseNote = sim.DominantHeatedVoxelType switch
                                {
                                    Asteroid.VoxelType.ResourceFerrite => 329.63f,  // E4 (Warm Metallic Bronze)
                                    Asteroid.VoxelType.ResourceCarbide => 261.63f,  // C4 (Deep Vulcan Magma / Carbide)
                                    Asteroid.VoxelType.ResourceQuartz => 523.25f,   // C5 (Smooth Cyan Quartz)
                                    Asteroid.VoxelType.ResourceVortexum => 440.00f, // A4 (Emerald Kinetic Resonator)
                                    Asteroid.VoxelType.ResourceAether => 783.99f,   // G5 (Celestial Gold)
                                    _ => 440.00f
                                };
                                mineralFreq = baseNote * (1.0f + heatFactor * 0.06f);
                                _targetMineralHeat = heatFactor;

                                // Kurangi desis uap batu biasa agar resonansi kord kristal & percikan mineral lebih dominan
                                rockVaporVol = Mathf.Clamp(0.012f * tierBoost * hitAtten, 0.0f, 0.020f);
                            }
                            else
                            {
                                // Suara batuan tergerus = suara desis uap hangat halus
                                rockVaporVol = Mathf.Clamp(0.045f * tierBoost * MathF.Max(0.50f, hitAtten), 0.0f, 0.065f);
                                _targetMineralHeat = 0.0f;
                            }
                        }
                    }
                    else
                    {
                        _targetMineralHeat = 0.0f;
                    }
                }
            }
            // B. Paritas Spasial Laser Penambangan Rival Hive AI (samar di kejauhan)
            else if (sim.EnemyController != null && sim.EnemyController.IsMiningLaserActive)
            {
                _isLaserPriming = false;
                ComputeSpatialGain(sim.EnemyController.LaserImpactPoint, maxDist: 1600.0f, out float aiAtten, out float aiPan);
                if (aiAtten > 0.02f)
                {
                    laserBeamVol = 0.15f * aiAtten;
                    laserFreq = 88.0f + sim.EnemyController.LaserBlockSpan * 6.0f;
                    rockVaporVol = 0.028f * aiAtten;
                    laserPan = aiPan;
                }
                _targetMineralHeat = 0.0f;
            }
            else
            {
                _isLaserPriming = false;
                _targetMineralHeat = 0.0f;
            }

            // C. Super Giga Drill Charging & Grinding (diturunkan agar tetap nyaman)
            if (sim.IsChargingDrill)
            {
                float cp = sim.DrillChargeRatio;
                drillGrindVol = Mathf.Lerp(0.06f, 0.16f, cp);
                drillFreq = Mathf.Lerp(50.0f, 102.0f, cp * cp);
                drillPan = 0.0f;
            }
            else if (sim.IsGigaDrillActive)
            {
                ComputeSpatialGain(sim.GigaDrillTipPosition, maxDist: 2100.0f, out float dAtten, out float dPan);
                drillPan = dPan;
                if (sim.IsGigaDrillGrindingAsteroid || sim.IsRammingEnemyCore)
                {
                    drillGrindVol = 0.14f * MathF.Max(0.40f, dAtten);
                    drillFreq = sim.IsRammingEnemyCore ? 76.0f : 62.0f;
                    // Tambahkan desis uap lembut saat bor menggerus batu
                    rockVaporVol = MathF.Max(rockVaporVol, 0.042f * dAtten);
                }
                else
                {
                    drillGrindVol = 0.10f * dAtten;
                    drillFreq = 82.0f;
                }
            }
        }

        _targetLaserBeamVol = laserBeamVol;
        _targetLaserFreq = laserFreq;
        _targetRockVaporVol = rockVaporVol;
        _targetMineralResonanceVol = mineralResVol;
        _targetMineralFreq = mineralFreq;
        _targetAetherSiphonVol = aetherSiphonVol;
        _targetLaserPan = laserPan;

        _targetDrillGrindVol = drillGrindVol;
        _targetDrillFreq = drillFreq;
        _targetDrillPan = drillPan;

        // ---------------------------------------------------------------------
        // 3. TARGET BUS FRAKTUR TEKTONIK MENJELANG BATU HANCUR (_isDestroying)
        // ---------------------------------------------------------------------
        float maxCreakVol = 0.0f;
        float creakFreq = 44.0f;
        float creakPan = 0.0f;
        bool hasDestroyingRock = false;
        float highestProgress = 0.0f;

        var activeAsteroids = Asteroid.ActiveAsteroids;
        for (int i = 0; i < activeAsteroids.Count; i++)
        {
            var ast = activeAsteroids[i];
            if (ast == null || !IsInstanceValid(ast) || ast.IsDestroyed || !ast.IsDestroying) continue;

            ComputeSpatialGain(ast.CachedPosition, maxDist: 2500.0f, out float astAtten, out float astPan);
            if (astAtten <= 0.01f) continue;

            float prog = ast.DestructionProgress;
            float vol = (0.12f + 0.22f * prog) * astAtten;
            if (vol > maxCreakVol)
            {
                maxCreakVol = vol;
                creakFreq = ast.IsEnergyAsteroid
                    ? Mathf.Lerp(52.0f, 118.0f, prog * prog)
                    : Mathf.Lerp(38.0f, 74.0f, prog);
                creakPan = astPan;
                hasDestroyingRock = !ast.IsEnergyAsteroid;
                highestProgress = prog;
            }
        }

        _targetFractureCreakVol = maxCreakVol;
        _targetFractureFreq = creakFreq;
        _targetFracturePan = creakPan;

        if (hasDestroyingRock && maxCreakVol > 0.04f)
        {
            _fractureCrackTimer -= dt;
            if (_fractureCrackTimer <= 0.0f)
            {
                _fractureCrackTimer = Mathf.Lerp(0.13f, 0.045f, highestProgress);
                float pitch = 0.72f + highestProgress * 0.35f + NextFloat01(ref _rngSfx) * 0.20f;
                SpawnGrainVoice(
                    isCrystal: false,
                    freqStart: 165.0f * pitch,
                    freqEnd: 52.0f * pitch,
                    toneDecaySec: 0.065f,
                    noiseDecaySec: 0.045f,
                    filterCutoffHz: 680.0f * pitch,
                    volume: 0.12f * maxCreakVol,
                    pan: creakPan + (NextFloat01(ref _rngSfx) - 0.5f) * 0.2f
                );
            }
        }
        else
        {
            _fractureCrackTimer = 0.0f;
        }
    }

    // =========================================================================
    // LOOP SINTESIS DSP REAL-TIME (44.100 HZ STEREO, SAMPLE-ACCURATE)
    // =========================================================================
    private void SynthesizeAudio()
    {
        if (_playback == null) return;

        int availableFrames = _playback.GetFramesAvailable();
        if (availableFrames <= 0) return;

        int queuedFrames = Math.Max(0, TotalBufferFrames - availableFrames);
        int framesToPush = TargetLeadFrames - queuedFrames;
        if (framesToPush <= 0) return;
        if (framesToPush > availableFrames) framesToPush = availableFrames;

        bool anyGrainActive = false;
        for (int g = 0; g < MaxGrainVoices; g++)
        {
            if (_grainVoices[g].Active) { anyGrainActive = true; break; }
        }

        bool anyDestructionActive = false;
        for (int d = 0; d < MaxDestructionVoices; d++)
        {
            if (_destructionVoices[d].Active) { anyDestructionActive = true; break; }
        }

        bool anyDartActive = false;
        for (int d = 0; d < MaxDartVoices; d++)
        {
            if (_dartVoices[d].Active) { anyDartActive = true; break; }
        }

        bool isTotallySilent = !anyGrainActive
            && !anyDestructionActive
            && !anyDartActive
            && _curSubVol < 0.0005f && _targetSubVol < 0.0005f
            && _curThrusterJetVol < 0.0005f && _targetThrusterJetVol < 0.0005f
            && _curSwarmPadVol < 0.0005f && _targetSwarmPadVol < 0.0005f
            && _curChargeEnv < 0.0005f && _targetChargeEnv < 0.0005f
            && _curPlasmaVol < 0.0005f && _targetPlasmaVol < 0.0005f
            && _curBrakeVol < 0.0005f && _targetBrakeVol < 0.0005f
            && _blastThumpEnv < 0.0005f && _launchChordEnv < 0.0005f
            && _curLaserBeamVol < 0.0005f && _targetLaserBeamVol < 0.0005f
            && _curRockVaporVol < 0.0005f && _targetRockVaporVol < 0.0005f
            && _curMineralResonanceVol < 0.0005f && _targetMineralResonanceVol < 0.0005f
            && _curAetherSiphonVol < 0.0005f && _targetAetherSiphonVol < 0.0005f
            && _curDrillGrindVol < 0.0005f && _targetDrillGrindVol < 0.0005f
            && _curFractureCreakVol < 0.0005f && _targetFractureCreakVol < 0.0005f;

        if (isTotallySilent)
        {
            for (int i = 0; i < framesToPush; i++)
            {
                _playback.PushFrame(Vector2.Zero);
            }
            return;
        }

        const float EnvSmoothAlpha = 0.0014f;
        const float FreqSmoothAlpha = 0.0010f;
        const float WarmPropLpAlpha = 0.12f; // ~820 Hz low-pass empuk untuk kehangatan hover magnetik tanpa frekuensi menusuk

        // Koefisien filter uap batuan tergerus (lembut seperti air menguap di atas batu panas)
        const float VaporLpAlpha = 0.16f; // ~1150 Hz
        const float VaporHpAlpha = 0.055f; // ~390 Hz

        for (int i = 0; i < framesToPush; i++)
        {
            // 1. Interpolasi parameter per-sampel (bebas 60Hz zipper noise)
            _curSubVol += (_targetSubVol - _curSubVol) * EnvSmoothAlpha;
            _curThrusterJetVol += (_targetThrusterJetVol - _curThrusterJetVol) * EnvSmoothAlpha;
            _curSwarmPadVol += (_targetSwarmPadVol - _curSwarmPadVol) * EnvSmoothAlpha;
            _curSubFreq += (_targetSubFreq - _curSubFreq) * FreqSmoothAlpha;

            _curChargeEnv += (_targetChargeEnv - _curChargeEnv) * EnvSmoothAlpha;
            _curChargeFreq += (_targetChargeFreq - _curChargeFreq) * FreqSmoothAlpha;
            _curChargeRichness += (_targetChargeRichness - _curChargeRichness) * EnvSmoothAlpha;

            _curPlasmaVol += (_targetPlasmaVol - _curPlasmaVol) * EnvSmoothAlpha;
            _curBrakeVol += (_targetBrakeVol - _curBrakeVol) * EnvSmoothAlpha;

            _curLaserBeamVol += (_targetLaserBeamVol - _curLaserBeamVol) * EnvSmoothAlpha;
            _curLaserFreq += (_targetLaserFreq - _curLaserFreq) * FreqSmoothAlpha;
            _curRockVaporVol += (_targetRockVaporVol - _curRockVaporVol) * EnvSmoothAlpha;
            _curMineralResonanceVol += (_targetMineralResonanceVol - _curMineralResonanceVol) * EnvSmoothAlpha;
            _curMineralFreq += (_targetMineralFreq - _curMineralFreq) * FreqSmoothAlpha;
            _curMineralHeat += (_targetMineralHeat - _curMineralHeat) * EnvSmoothAlpha;
            _curAetherSiphonVol += (_targetAetherSiphonVol - _curAetherSiphonVol) * EnvSmoothAlpha;
            _curLaserPan += (_targetLaserPan - _curLaserPan) * EnvSmoothAlpha;

            _curDrillGrindVol += (_targetDrillGrindVol - _curDrillGrindVol) * EnvSmoothAlpha;
            _curDrillFreq += (_targetDrillFreq - _curDrillFreq) * FreqSmoothAlpha;
            _curDrillPan += (_targetDrillPan - _curDrillPan) * EnvSmoothAlpha;

            _curFractureCreakVol += (_targetFractureCreakVol - _curFractureCreakVol) * EnvSmoothAlpha;
            _curFractureFreq += (_targetFractureFreq - _curFractureFreq) * FreqSmoothAlpha;
            _curFracturePan += (_targetFracturePan - _curFracturePan) * EnvSmoothAlpha;

            if (_explosionDuckEnv > 0.0001f)
            {
                _explosionDuckEnv *= 0.99985f;
            }

            float busPropulsionL = 0.0f;
            float busPropulsionR = 0.0f;
            float busMiningL = 0.0f;
            float busMiningR = 0.0f;
            float busDartL = 0.0f;
            float busDartR = 0.0f;
            float busDestructionL = 0.0f;
            float busDestructionR = 0.0f;

            float noiseL = NextNoise(ref _rngL);
            float noiseR = NextNoise(ref _rngR);

            // =================================================================
            // BUS 1: PROPULSI MEDAN MAGNETIK (HOVER LEMBUT) & PLASMA BOOST (KENCANG)
            // =================================================================
            float rawPropToneL = 0.0f;
            float rawPropToneR = 0.0f;

            // A. Gerak Kapal & Swarm (WASD Hover & Cruising Glide)
            //    Karakter: Suara medan magnetik / gravitasi yang lembut, empuk, dan melayang (Hover)
            if (_curSubVol > 0.0008f || _curSwarmPadVol > 0.0008f)
            {
                _phaseSwarmLfo = WrapPhase(_phaseSwarmLfo + 0.35f * InvSampleRate);
                float lfoSin = MathF.Sin(_phaseSwarmLfo * TwoPi);
                float lfoCos = MathF.Cos(_phaseSwarmLfo * TwoPi);

                // Binaural detune sangat halus untuk kedalaman medan magnetik stereo
                _phaseSubL = WrapPhase(_phaseSubL + (_curSubFreq - 0.18f + lfoSin * 0.06f) * InvSampleRate);
                _phaseSubR = WrapPhase(_phaseSubR + (_curSubFreq + 0.18f - lfoSin * 0.06f) * InvSampleRate);

                // Nada Fifth (1.498x) dan Octave (2.0x) lembut untuk Swarm Harmonic Pad
                float fifthFreq = _curSubFreq * 1.4983f;
                float octFreq = _curSubFreq * 2.0f;
                _phaseSwarmFifthL = WrapPhase(_phaseSwarmFifthL + (fifthFreq - 0.25f + lfoCos * 0.10f) * InvSampleRate);
                _phaseSwarmFifthR = WrapPhase(_phaseSwarmFifthR + (fifthFreq + 0.25f - lfoCos * 0.10f) * InvSampleRate);
                _phaseSwarmOctL = WrapPhase(_phaseSwarmOctL + (octFreq + 0.30f) * InvSampleRate);
                _phaseSwarmOctR = WrapPhase(_phaseSwarmOctR + (octFreq - 0.30f) * InvSampleRate);

                // Nada Dasar Medan Gravitasi / Magnetik Hover:
                // Gelombang sinus empuk diperkaya mid harmonics agar jelas terdengar di speaker laptop/desktop
                float fundL = MathF.Sin(_phaseSubL * TwoPi);
                float fundR = MathF.Sin(_phaseSubR * TwoPi);
                float subCushionL = MathF.Sin(_phaseSubL * 0.5f * TwoPi) * 0.22f; // Bantalan sub-bass empuk
                float subCushionR = MathF.Sin(_phaseSubR * 0.5f * TwoPi) * 0.22f;
                float octSoftL = MathF.Sin(_phaseSubL * 2.0f * TwoPi) * 0.24f;    // Overtone oktaf
                float octSoftR = MathF.Sin(_phaseSubR * 2.0f * TwoPi) * 0.24f;
                float midHarm3L = MathF.Sin(_phaseSubL * 3.0f * TwoPi) * 0.22f;   // Mid-range harm 3 (~180-230 Hz)
                float midHarm3R = MathF.Sin(_phaseSubR * 3.0f * TwoPi) * 0.22f;
                float midHarm4L = MathF.Sin(_phaseSubL * 4.0f * TwoPi) * 0.14f;   // Mid-range harm 4 (~240-310 Hz)
                float midHarm4R = MathF.Sin(_phaseSubR * 4.0f * TwoPi) * 0.14f;

                // Modulasi getaran medan magnetik / hover whir (~8.5 Hz)
                _phaseThrusterPulse = WrapPhase(_phaseThrusterPulse + 8.5f * InvSampleRate);
                float hoverMod = 0.90f + 0.10f * MathF.Sin(_phaseThrusterPulse * TwoPi);

                float hoverToneL = (fundL + subCushionL + octSoftL + midHarm3L + midHarm4L) * hoverMod * _curSubVol;
                float hoverToneR = (fundR + subCushionR + octSoftR + midHarm3R + midHarm4R) * hoverMod * _curSubVol;

                // Pad Harmonik Swarm Ethereal (sangat halus di latar belakang)
                float padModL = 0.85f + 0.15f * lfoSin;
                float padModR = 0.85f + 0.15f * lfoCos;
                float swarmPadL = (MathF.Sin(_phaseSwarmFifthL * TwoPi) * 0.60f
                                 + MathF.Sin(_phaseSwarmOctL * TwoPi) * 0.40f) * padModL * _curSwarmPadVol;

                float swarmPadR = (MathF.Sin(_phaseSwarmFifthR * TwoPi) * 0.60f
                                 + MathF.Sin(_phaseSwarmOctR * TwoPi) * 0.40f) * padModR * _curSwarmPadVol;

                float centerSub = (hoverToneL + hoverToneR) * 0.5f;
                rawPropToneL += centerSub * 0.70f + hoverToneL * 0.30f + swarmPadL;
                rawPropToneR += centerSub * 0.70f + hoverToneR * 0.30f + swarmPadR;
            }

            // B. Priming Thruster [SPACE]: Swell Kumparan Reaktor 3-Tahap ("ngung... nguuung... NGUUUNG")
            //    Jelas terdengar tahapan pengisian dayanya secara berurutan, namun tetap empuk & nyaman di telinga
            if (_curChargeEnv > 0.0008f)
            {
                _phaseChargeL = WrapPhase(_phaseChargeL + (_curChargeFreq - 0.38f) * InvSampleRate);
                _phaseChargeR = WrapPhase(_phaseChargeR + (_curChargeFreq + 0.38f) * InvSampleRate);
                _phaseChargeSub = WrapPhase(_phaseChargeSub + (_curChargeFreq * 0.5f) * InvSampleRate);
                _phaseChargeFifth = WrapPhase(_phaseChargeFifth + (_curChargeFreq * 1.4983f) * InvSampleRate);

                float subLayer = MathF.Sin(_phaseChargeSub * TwoPi) * 0.25f;
                float fifthLayer = MathF.Sin(_phaseChargeFifth * TwoPi) * (0.28f + 0.22f * _curChargeRichness);
                float octLayerL = MathF.Sin(_phaseChargeL * 2.0f * TwoPi) * (0.25f + 0.20f * _curChargeRichness);
                float octLayerR = MathF.Sin(_phaseChargeR * 2.0f * TwoPi) * (0.25f + 0.20f * _curChargeRichness);

                float chargeL = (MathF.Sin(_phaseChargeL * TwoPi) * 0.62f + subLayer + fifthLayer + octLayerL) * 0.85f * _curChargeEnv;
                float chargeR = (MathF.Sin(_phaseChargeR * TwoPi) * 0.62f + subLayer + fifthLayer + octLayerR) * 0.85f * _curChargeEnv;

                rawPropToneL += chargeL;
                rawPropToneR += chargeR;
            }

            // C. Hentakan Peluncuran Awal [Lepas SPACE] (Punchy Sub-Bloom + Launch Chord Swell)
            if (_blastThumpEnv > 0.0008f || _launchChordEnv > 0.0008f)
            {
                float thumpPitch = 32.0f + 48.0f * (_blastThumpEnv * _blastThumpEnv);
                _phaseThump = WrapPhase(_phaseThump + thumpPitch * InvSampleRate);

                float subBloom = (MathF.Sin(_phaseThump * TwoPi) * 0.82f + MathF.Sin(_phaseThump * 1.5f * TwoPi) * 0.18f)
                               * _blastThumpEnv * 0.62f;

                rawPropToneL += subBloom;
                rawPropToneR += subBloom;

                _blastThumpEnv *= 0.99987f;  // Peluruhan sub-bloom hangat
                _launchChordEnv *= 0.99991f; // Ekor kord peluncuran sinematik
            }

            // D. Meluncur Menggunakan Thruster (Full Plasma Boost): Koor Plasma Stereo Bertenaga & Desis Aliran Cepat
            //    Inilah momen dorongan utama yang agak kencang dan memuaskan!
            if (_curPlasmaVol > 0.0008f || _curBrakeVol > 0.0008f)
            {
                if (_curPlasmaVol > 0.0008f)
                {
                    _phasePlasmaChorusLfo = WrapPhase(_phasePlasmaChorusLfo + 0.52f * InvSampleRate);
                    float cSin = MathF.Sin(_phasePlasmaChorusLfo * TwoPi);
                    float cCos = MathF.Cos(_phasePlasmaChorusLfo * TwoPi);

                    // Kord Power Harmonik Plasma (Agak Kencang & Mantap)
                    _phasePlasmaSubL = WrapPhase(_phasePlasmaSubL + (72.0f + cSin * 0.20f) * InvSampleRate);
                    _phasePlasmaSubR = WrapPhase(_phasePlasmaSubR + (72.8f - cSin * 0.20f) * InvSampleRate);
                    _phasePlasmaFifthL = WrapPhase(_phasePlasmaFifthL + (108.0f + cCos * 0.25f) * InvSampleRate);
                    _phasePlasmaFifthR = WrapPhase(_phasePlasmaFifthR + (108.6f - cCos * 0.25f) * InvSampleRate);
                    _phasePlasmaOct = WrapPhase(_phasePlasmaOct + 144.0f * InvSampleRate);

                    float chordBoost = 1.0f + _launchChordEnv * 0.35f;
                    float subFoundation = MathF.Sin(_phasePlasmaOct * 0.25f * TwoPi) * 0.28f;
                    float octShimmer = MathF.Sin(_phasePlasmaOct * TwoPi) * 0.24f * chordBoost;

                    float plasmaL = (MathF.Sin(_phasePlasmaSubL * TwoPi) * 0.56f
                                   + MathF.Sin(_phasePlasmaFifthL * TwoPi) * 0.34f * chordBoost
                                   + subFoundation + octShimmer) * 0.65f * _curPlasmaVol;

                    float plasmaR = (MathF.Sin(_phasePlasmaSubR * TwoPi) * 0.56f
                                   + MathF.Sin(_phasePlasmaFifthR * TwoPi) * 0.34f * chordBoost
                                   + subFoundation + octShimmer) * 0.65f * _curPlasmaVol;

                    rawPropToneL += plasmaL;
                    rawPropToneR += plasmaR;
                }

                // Desis Semburan Plasma Aliran Cepat saat Meluncur
                _steamLp1L += SteamAlphaLp * (noiseL - _steamLp1L);
                _steamLp2L += SteamAlphaLp * (_steamLp1L - _steamLp2L);
                _steamHpL += SteamAlphaHp * (_steamLp2L - _steamHpL);
                float steamL = _steamLp2L - _steamHpL;

                _steamLp1R += SteamAlphaLp * (noiseR - _steamLp1R);
                _steamLp2R += SteamAlphaLp * (_steamLp1R - _steamLp2R);
                _steamHpR += SteamAlphaHp * (_steamLp2R - _steamHpR);
                float steamR = _steamLp2R - _steamHpR;

                float vaporGain = _curPlasmaVol * 0.055f + _curBrakeVol * 0.040f;
                busPropulsionL += steamL * vaporGain;
                busPropulsionR += steamR * vaporGain;
            }

            // Semburan Jet Thruster Normal (WASD): Desis hangat yang mengisi ruang agar tidak sepi / hampa
            if (_curThrusterJetVol > 0.0008f)
            {
                const float JetLpAlpha = 0.24f;  // ~1650 Hz
                const float JetHpAlpha = 0.045f; // ~310 Hz
                _jetLp1L += JetLpAlpha * (noiseL - _jetLp1L);
                _jetLp2L += JetLpAlpha * (_jetLp1L - _jetLp2L);
                _jetHpL += JetHpAlpha * (_jetLp2L - _jetHpL);
                float jetL = (_jetLp2L - _jetHpL);

                _jetLp1R += JetLpAlpha * (noiseR - _jetLp1R);
                _jetLp2R += JetLpAlpha * (_jetLp1R - _jetLp2R);
                _jetHpR += JetHpAlpha * (_jetLp2R - _jetHpR);
                float jetR = (_jetLp2R - _jetHpR);

                busPropulsionL += jetL * _curThrusterJetVol * 0.38f;
                busPropulsionR += jetR * _curThrusterJetVol * 0.38f;
            }

            // Lewatkan komponen nada propulsi & swarm ke filter low-pass empuk
            _propLp1L += WarmPropLpAlpha * (rawPropToneL - _propLp1L);
            _propLp2L += WarmPropLpAlpha * (_propLp1L - _propLp2L);
            _propLp1R += WarmPropLpAlpha * (rawPropToneR - _propLp1R);
            _propLp2R += WarmPropLpAlpha * (_propLp1R - _propLp2R);

            busPropulsionL += rawPropToneL * 0.50f + _propLp2L * 0.50f;
            busPropulsionR += rawPropToneR * 0.50f + _propLp2R * 0.50f;

            // =================================================================
            // BUS 2: PENAMBANGAN (PRIMING LASER FOKUS, CUTTING BEAM, & RESOURCE PATCH)
            // =================================================================
            if (_curLaserBeamVol > 0.0006f || _curRockVaporVol > 0.0006f || _curMineralResonanceVol > 0.0006f || _curAetherSiphonVol > 0.0006f)
            {
                StereoPanFactors(_curLaserPan, out float lPanL, out float lPanR);

                // 1. Dengungan Sinar Laser Koheren & Priming Capacitor Spool
                if (_curLaserBeamVol > 0.0006f)
                {
                    _phaseLaserLfo = WrapPhase(_phaseLaserLfo + (_isLaserPriming ? 20.0f : 4.8f) * InvSampleRate);
                    float vibrato = 1.0f + MathF.Sin(_phaseLaserLfo * TwoPi) * (_isLaserPriming ? 0.030f : 0.008f);

                    _phaseLaserFund = WrapPhase(_phaseLaserFund + (_curLaserFreq * vibrato) * InvSampleRate);
                    _phaseLaserDetune = WrapPhase(_phaseLaserDetune + (_curLaserFreq * 1.498f * vibrato) * InvSampleRate);

                    float fundTone = MathF.Sin(_phaseLaserFund * TwoPi);
                    float detuneTone = MathF.Sin(_phaseLaserDetune * TwoPi);
                    float octTone = MathF.Sin(_phaseLaserFund * 2.0f * TwoPi) * 0.25f;

                    float beamTone = (fundTone * 0.55f + detuneTone * 0.30f + octTone) * _curLaserBeamVol;

                    // Saat laser priming (280ms pertama): tambahkan desis ionisasi optik yang tajam & high-tech
                    if (_isLaserPriming)
                    {
                        float primeBuzz = (noiseL * 0.5f + noiseR * 0.5f) * 0.12f * _curLaserBeamVol;
                        beamTone += primeBuzz;
                    }

                    busMiningL += beamTone * lPanL;
                    busMiningR += beamTone * lPanR;
                }

                // 2. Suara Batuan Tergerus = Suara Menguap Halus ("pssshhh" uap lembut, tidak keras)
                if (_curRockVaporVol > 0.0006f)
                {
                    _phaseVaporBreath = WrapPhase(_phaseVaporBreath + 2.4f * InvSampleRate);
                    float breathMod = 0.82f + 0.18f * MathF.Sin(_phaseVaporBreath * TwoPi);

                    _vaporLp1L += VaporLpAlpha * (noiseL - _vaporLp1L);
                    _vaporLp2L += VaporLpAlpha * (_vaporLp1L - _vaporLp2L);
                    _vaporHpL += VaporHpAlpha * (_vaporLp2L - _vaporHpL);
                    float softSteamL = (_vaporLp2L - _vaporHpL) * breathMod;

                    _vaporLp1R += VaporLpAlpha * (noiseR - _vaporLp1R);
                    _vaporLp2R += VaporLpAlpha * (_vaporLp1R - _vaporLp2R);
                    _vaporHpR += VaporHpAlpha * (_vaporLp2R - _vaporHpR);
                    float softSteamR = (_vaporLp2R - _vaporHpR) * breathMod;

                    busMiningL += softSteamL * _curRockVaporVol * lPanL;
                    busMiningR += softSteamR * _curRockVaporVol * lPanR;
                }

                // 3. Resonansi Penambangan Resource Patch (Kord Kristal Bernyanyi & Percikan Ionisasi Sizzling)
                if (_curMineralResonanceVol > 0.0006f)
                {
                    // LFO Chorus binaural lambat untuk rasa ruang & kemewahan kristal
                    _phaseMineralChorusLfo = WrapPhase(_phaseMineralChorusLfo + 0.85f * InvSampleRate);
                    float chorusL = MathF.Sin(_phaseMineralChorusLfo * TwoPi) * 0.008f;
                    float chorusR = MathF.Cos(_phaseMineralChorusLfo * TwoPi) * 0.008f;

                    // Osilator Kord Kristal Stereo (Root L/R detuned + Fifth 1.498x + Octave 2.0x)
                    _phaseMineralCarL = WrapPhase(_phaseMineralCarL + (_curMineralFreq * (0.997f + chorusL)) * InvSampleRate);
                    _phaseMineralCarR = WrapPhase(_phaseMineralCarR + (_curMineralFreq * (1.003f + chorusR)) * InvSampleRate);
                    _phaseMineralFifth = WrapPhase(_phaseMineralFifth + (_curMineralFreq * 1.4983f) * InvSampleRate);
                    _phaseMineralOct = WrapPhase(_phaseMineralOct + (_curMineralFreq * 2.0f) * InvSampleRate);

                    float fundL = MathF.Sin(_phaseMineralCarL * TwoPi);
                    float fundR = MathF.Sin(_phaseMineralCarR * TwoPi);
                    float fifthTone = MathF.Sin(_phaseMineralFifth * TwoPi) * 0.38f;
                    float octTone = MathF.Sin(_phaseMineralOct * TwoPi) * 0.28f;

                    // Paduan kord kristal yang harmonis & bernyanyi (singing crystal bowl)
                    float crystalChordL = (fundL * 0.58f + fifthTone + octTone) * _curMineralResonanceVol;
                    float crystalChordR = (fundR * 0.58f + fifthTone + octTone) * _curMineralResonanceVol;

                    // Sizzling Extraction Sparks (Percikan ionisasi kristal berenergi tinggi)
                    // Kecepatan percikan semakin cepat seiring kenaikan panas voxel (heatFactor)
                    float sparkleRate = 18.0f + 24.0f * _curMineralHeat;
                    _phaseMineralSparkleLfo = WrapPhase(_phaseMineralSparkleLfo + sparkleRate * InvSampleRate);
                    float sparkleMod = MathF.Max(0.0f, MathF.Sin(_phaseMineralSparkleLfo * TwoPi));
                    sparkleMod *= sparkleMod; // Puncak tajam berpendar

                    // Filter bandpass kristal frekuensi tinggi (~2.8 kHz - 5.8 kHz)
                    const float MineralSparkleLpAlpha = 0.42f; // ~3 kHz - 6 kHz
                    const float MineralSparkleHpAlpha = 0.16f; // ~1.2 kHz - 2.4 kHz
                    _mineralSparkleLp1L += MineralSparkleLpAlpha * (noiseL - _mineralSparkleLp1L);
                    _mineralSparkleLp2L += MineralSparkleLpAlpha * (_mineralSparkleLp1L - _mineralSparkleLp2L);
                    _mineralSparkleHpL += MineralSparkleHpAlpha * (_mineralSparkleLp2L - _mineralSparkleHpL);
                    float sparkleL = (_mineralSparkleLp2L - _mineralSparkleHpL) * sparkleMod;

                    _mineralSparkleLp1R += MineralSparkleLpAlpha * (noiseR - _mineralSparkleLp1R);
                    _mineralSparkleLp2R += MineralSparkleLpAlpha * (_mineralSparkleLp1R - _mineralSparkleLp2R);
                    _mineralSparkleHpR += MineralSparkleHpAlpha * (_mineralSparkleLp2R - _mineralSparkleHpR);
                    float sparkleR = (_mineralSparkleLp2R - _mineralSparkleHpR) * sparkleMod;

                    float sparkleGain = _curMineralResonanceVol * (0.18f + 0.32f * _curMineralHeat);

                    busMiningL += (crystalChordL + sparkleL * sparkleGain) * lPanL;
                    busMiningR += (crystalChordR + sparkleR * sparkleGain) * lPanR;
                }

                // 4. Penyedotan Plasma Bintang Aether
                if (_curAetherSiphonVol > 0.0006f)
                {
                    _phaseAetherSiphon1 = WrapPhase(_phaseAetherSiphon1 + 72.0f * InvSampleRate);
                    _phaseAetherSiphon2 = WrapPhase(_phaseAetherSiphon2 + 108.15f * InvSampleRate);

                    float siphonL = (MathF.Sin(_phaseAetherSiphon1 * TwoPi) * 0.65f + MathF.Sin(_phaseAetherSiphon2 * TwoPi) * 0.35f) * _curAetherSiphonVol;
                    float siphonR = (MathF.Sin(_phaseAetherSiphon2 * TwoPi) * 0.65f + MathF.Sin(_phaseAetherSiphon1 * TwoPi) * 0.35f) * _curAetherSiphonVol;

                    busMiningL += siphonL * lPanL;
                    busMiningR += siphonR * lPanR;
                }
            }

            // 5. Super Giga Drill Rotary Hum
            if (_curDrillGrindVol > 0.0006f)
            {
                StereoPanFactors(_curDrillPan, out float dPanL, out float dPanR);

                _phaseDrillMod = WrapPhase(_phaseDrillMod + 13.0f * InvSampleRate);
                float spiralPulse = 0.78f + 0.22f * MathF.Sin(_phaseDrillMod * TwoPi);

                _phaseDrillFund = WrapPhase(_phaseDrillFund + _curDrillFreq * InvSampleRate);
                float drillTone = (MathF.Sin(_phaseDrillFund * TwoPi) * 0.70f
                                 + MathF.Sin(_phaseDrillFund * 1.5f * TwoPi) * 0.30f) * 0.55f;

                _drillNoiseLpL += 0.10f * (noiseL - _drillNoiseLpL);
                _drillNoiseLpR += 0.10f * (noiseR - _drillNoiseLpR);

                float grindL = (drillTone + _drillNoiseLpL * 0.22f) * spiralPulse * _curDrillGrindVol;
                float grindR = (drillTone + _drillNoiseLpR * 0.22f) * spiralPulse * _curDrillGrindVol;

                busMiningL += grindL * dPanL;
                busMiningR += grindR * dPanR;
            }

            // 6. Polyphonic Voxel Vapor Puff & Crystal Chime Voices (10 Voices)
            if (anyGrainActive)
            {
                for (int g = 0; g < MaxGrainVoices; g++)
                {
                    ref var v = ref _grainVoices[g];
                    if (!v.Active) continue;

                    v.Env *= v.DecayPerSample;
                    v.NoiseEnv *= v.NoiseDecayPerSample;

                    if (v.Env < 0.0012f && v.NoiseEnv < 0.0012f)
                    {
                        v.Active = false;
                        continue;
                    }

                    float sample;
                    if (!v.IsCrystal)
                    {
                        // Hembusan uap lembut saat voxel batu menguap ("pssh" halus)
                        float instFreq = v.FreqEnd + (v.FreqStart - v.FreqEnd) * v.Env;
                        v.Phase1 = WrapPhase(v.Phase1 + instFreq * InvSampleRate);
                        float softWarmBody = MathF.Sin(v.Phase1 * TwoPi) * v.Env * 0.38f;

                        v.FilterState += v.FilterAlpha * (noiseL - v.FilterState);
                        v.FilterState2 += v.FilterAlpha * (v.FilterState - v.FilterState2);
                        float vaporPuff = (v.FilterState - v.FilterState2) * v.NoiseEnv * 0.85f;

                        sample = softWarmBody + vaporPuff;
                    }
                    else
                    {
                        // Denting rekahan kristal mineral ("ching-clink!"): snap rekahan renyah + lonceng kristal resonan
                        v.Phase1 = WrapPhase(v.Phase1 + v.FreqStart * InvSampleRate);
                        v.Phase2 = WrapPhase(v.Phase2 + v.FreqEnd * InvSampleRate);

                        float chime = (MathF.Sin(v.Phase1 * TwoPi) * 0.65f
                                     + MathF.Sin(v.Phase2 * TwoPi) * 0.35f) * v.Env;

                        v.FilterState += v.FilterAlpha * (noiseR - v.FilterState);
                        v.FilterState2 += v.FilterAlpha * (v.FilterState - v.FilterState2);
                        float crystalSnap = (v.FilterState - v.FilterState2) * v.NoiseEnv * 0.35f;

                        sample = chime + crystalSnap;
                    }

                    busMiningL += sample * v.GainL;
                    busMiningR += sample * v.GainR;
                }
            }

            // =================================================================
            // BUS 3: RETAKAN TEKTONIK, LEDAKAN BATUAN HANCUR, & SHOCKWAVE
            // =================================================================
            if (_curFractureCreakVol > 0.0008f)
            {
                StereoPanFactors(_curFracturePan, out float fPanL, out float fPanR);

                _phaseFractureSub = WrapPhase(_phaseFractureSub + _curFractureFreq * InvSampleRate);
                _phaseFractureTension = WrapPhase(_phaseFractureTension + (_curFractureFreq * 1.414f) * InvSampleRate);

                float tectonicGroan = (MathF.Sin(_phaseFractureSub * TwoPi) * 0.72f
                                     + MathF.Sin(_phaseFractureTension * TwoPi) * 0.28f) * _curFractureCreakVol;

                busDestructionL += tectonicGroan * fPanL;
                busDestructionR += tectonicGroan * fPanR;
            }

            if (anyDestructionActive)
            {
                for (int d = 0; d < MaxDestructionVoices; d++)
                {
                    ref var dv = ref _destructionVoices[d];
                    if (!dv.Active) continue;

                    dv.AgeSeconds += InvSampleRate;
                    if (dv.AgeSeconds >= dv.DurationSeconds)
                    {
                        dv.Active = false;
                        continue;
                    }

                    float normTime = dv.AgeSeconds / dv.DurationSeconds;
                    float env = 1.0f - normTime;
                    float expDecay = MathF.Exp(-4.2f * normTime);

                    float outL = 0.0f;
                    float outR = 0.0f;

                    switch (dv.Kind)
                    {
                        case DestructionSoundKind.RockAsteroidShatter:
                        {
                            float subEnv = MathF.Exp(-5.2f * normTime);
                            float subFreq = (26.0f + 64.0f * subEnv) / MathF.Sqrt(MathF.Max(0.7f, dv.Scale));
                            dv.SubPhase = WrapPhase(dv.SubPhase + subFreq * InvSampleRate);
                            float subBoom = MathF.Sin(dv.SubPhase * TwoPi) * subEnv * 0.80f;

                            float bodyEnv = MathF.Exp(-7.5f * normTime);
                            float bodyFreq1 = (52.0f + 80.0f * bodyEnv) / dv.Scale;
                            float bodyFreq2 = bodyFreq1 * 1.47f;
                            dv.BodyPhase1 = WrapPhase(dv.BodyPhase1 + bodyFreq1 * InvSampleRate);
                            dv.BodyPhase2 = WrapPhase(dv.BodyPhase2 + bodyFreq2 * InvSampleRate);
                            float stonyBody = (MathF.Sin(dv.BodyPhase1 * TwoPi) + MathF.Sin(dv.BodyPhase2 * TwoPi) * 0.62f) * bodyEnv * 0.42f;

                            if (dv.AgeSeconds >= dv.NextClatterTime && normTime < 0.78f)
                            {
                                dv.ClatterEnv = (0.48f + NextFloat01(ref dv.LocalRng) * 0.42f) * (1.0f - normTime * 0.75f);
                                dv.NextClatterTime = dv.AgeSeconds + 0.030f + NextFloat01(ref dv.LocalRng) * 0.050f;
                            }
                            dv.ClatterEnv *= 0.9991f;

                            float cutoff = 160.0f + 1350.0f * expDecay + dv.ClatterEnv * 480.0f;
                            ProcessSvfBandpass(noiseL, cutoff, q: 1.35f, ref dv.SvfIc1L, ref dv.SvfIc2L, out float bpL, out float lpL);
                            ProcessSvfBandpass(noiseR, cutoff * 1.05f, q: 1.35f, ref dv.SvfIc1R, ref dv.SvfIc2R, out float bpR, out float lpR);

                            float debrisCrunchL = (lpL * expDecay * 0.52f + bpL * dv.ClatterEnv * 0.50f);
                            float debrisCrunchR = (lpR * expDecay * 0.52f + bpR * dv.ClatterEnv * 0.50f);

                            outL = subBoom + stonyBody + debrisCrunchL;
                            outR = subBoom + stonyBody + debrisCrunchR;
                            break;
                        }

                        case DestructionSoundKind.AetherSupernova:
                        {
                            float subEnv = MathF.Exp(-3.6f * normTime);
                            float subFreq = 22.0f + 90.0f * (subEnv * subEnv);
                            dv.SubPhase = WrapPhase(dv.SubPhase + subFreq * InvSampleRate);
                            float subDrop = (MathF.Sin(dv.SubPhase * TwoPi) + MathF.Sin(dv.SubPhase * 2.0f * TwoPi) * 0.24f) * subEnv * 0.84f;

                            float sweepCutoff = 140.0f + 2100.0f * MathF.Exp(-4.8f * normTime);
                            ProcessSvfBandpass(noiseL, sweepCutoff, q: 2.1f, ref dv.SvfIc1L, ref dv.SvfIc2L, out float pBpL, out float pLpL);
                            ProcessSvfBandpass(noiseR, sweepCutoff * 1.08f, q: 2.1f, ref dv.SvfIc1R, ref dv.SvfIc2R, out float pBpR, out float pLpR);

                            dv.ShimmerPhase1 = WrapPhase(dv.ShimmerPhase1 + 783.99f * InvSampleRate);
                            dv.ShimmerPhase2 = WrapPhase(dv.ShimmerPhase2 + 1174.66f * InvSampleRate);
                            float glassEnv = MathF.Exp(-4.5f * normTime) * (0.68f + 0.32f * MathF.Sin(dv.AgeSeconds * 38.0f));
                            float glassL = (MathF.Sin(dv.ShimmerPhase1 * TwoPi) + MathF.Sin(dv.ShimmerPhase2 * TwoPi) * 0.65f) * glassEnv * 0.14f;
                            float glassR = (MathF.Sin(dv.ShimmerPhase2 * TwoPi) + MathF.Sin(dv.ShimmerPhase1 * 1.5f * TwoPi) * 0.65f) * glassEnv * 0.14f;

                            outL = subDrop + (pBpL * 0.55f + pLpL * 0.35f) * expDecay + glassL;
                            outR = subDrop + (pBpR * 0.55f + pLpR * 0.35f) * expDecay + glassR;
                            break;
                        }

                        case DestructionSoundKind.BoulderCollisionSlam:
                        {
                            float thudEnv = MathF.Exp(-6.8f * normTime);
                            float thudFreq = 35.0f + 72.0f * thudEnv;
                            dv.SubPhase = WrapPhase(dv.SubPhase + thudFreq * InvSampleRate);
                            float boulderThud = MathF.Sin(dv.SubPhase * TwoPi) * thudEnv * 0.74f;

                            float cutoff = 150.0f + 820.0f * thudEnv;
                            ProcessSvfBandpass(noiseL, cutoff, q: 1.5f, ref dv.SvfIc1L, ref dv.SvfIc2L, out float bpL, out float lpL);
                            ProcessSvfBandpass(noiseR, cutoff, q: 1.5f, ref dv.SvfIc1R, ref dv.SvfIc2R, out float bpR, out float lpR);

                            outL = boulderThud + (lpL * 0.42f + bpL * 0.34f) * expDecay;
                            outR = boulderThud + (lpR * 0.42f + bpR * 0.34f) * expDecay;
                            break;
                        }

                        case DestructionSoundKind.PreSupernovaPulse:
                        {
                            float pulseEnv = MathF.Sin(normTime * Mathf.Pi) * MathF.Exp(-2.2f * normTime);
                            float freq = (34.0f + 44.0f * (1.0f - normTime)) * dv.Scale;
                            dv.SubPhase = WrapPhase(dv.SubPhase + freq * InvSampleRate);
                            float wave = (MathF.Sin(dv.SubPhase * TwoPi) + MathF.Sin(dv.SubPhase * 1.5f * TwoPi) * 0.32f) * pulseEnv * 0.65f;
                            outL = wave;
                            outR = wave;
                            break;
                        }

                        case DestructionSoundKind.DrillCraterBlast:
                        {
                            float blastEnv = MathF.Exp(-6.2f * normTime);
                            float blastFreq = 32.0f + 78.0f * blastEnv;
                            dv.SubPhase = WrapPhase(dv.SubPhase + blastFreq * InvSampleRate);
                            float sub = MathF.Sin(dv.SubPhase * TwoPi) * blastEnv * 0.72f;

                            float cutoff = 190.0f + 1150.0f * blastEnv;
                            ProcessSvfBandpass(noiseL, cutoff, q: 1.6f, ref dv.SvfIc1L, ref dv.SvfIc2L, out float bpL, out float lpL);
                            ProcessSvfBandpass(noiseR, cutoff, q: 1.6f, ref dv.SvfIc1R, ref dv.SvfIc2R, out float bpR, out float lpR);

                            outL = sub + (lpL * 0.46f + bpL * 0.38f) * expDecay;
                            outR = sub + (lpR * 0.46f + bpR * 0.38f) * expDecay;
                            break;
                        }
                    }

                    busDestructionL += outL * dv.GainL * env;
                    busDestructionR += outR * dv.GainR * env;
                }
            }

            // =================================================================
            // DART NEEDLE AUDIO VOICES (PELUNCURAN HALUS & IMPAK 4-KATEGORI BERBEDA)
            // =================================================================
            for (int d = 0; d < MaxDartVoices; d++)
            {
                ref var dv = ref _dartVoices[d];
                if (!dv.Active) continue;

                dv.AgeSeconds += InvSampleRate;
                if (dv.AgeSeconds >= dv.DurationSeconds)
                {
                    dv.Active = false;
                    continue;
                }

                float progress = dv.AgeSeconds / dv.DurationSeconds;
                float voiceSample = 0.0f;

                switch (dv.Kind)
                {
                    case DartVoiceKind.Launch:
                    {
                        // Peluncuran halus aerodinamis ("swiiip")
                        float env = 1.0f - progress;
                        env = env * env;
                        float freq = Mathf.Lerp(dv.FreqStart, dv.FreqEnd, progress);
                        dv.Phase1 = WrapPhase(dv.Phase1 + freq * InvSampleRate);
                        float whistle = MathF.Sin(dv.Phase1 * TwoPi) * 0.70f;
                        dv.FilterStateL += 0.20f * (noiseL - dv.FilterStateL);
                        float air = dv.FilterStateL * 0.30f;
                        voiceSample = (whistle + air) * env;
                        break;
                    }

                    case DartVoiceKind.ImpactEnemy:
                    {
                        // Benturan kinetik-elektrik tajam pada lambung/proyektil musuh ("tzzzt-clash")
                        float env = MathF.Exp(-7.0f * progress);
                        float freq = Mathf.Lerp(dv.FreqStart, dv.FreqEnd, progress * progress);
                        dv.Phase1 = WrapPhase(dv.Phase1 + freq * InvSampleRate);
                        dv.Phase2 = WrapPhase(dv.Phase2 + 1480.0f * InvSampleRate);
                        float metalTone = MathF.Sin(dv.Phase1 * TwoPi) * 0.65f + MathF.Sin(dv.Phase2 * TwoPi) * 0.35f;

                        dv.FilterStateL += 0.35f * (noiseL - dv.FilterStateL);
                        float electroCrunch = dv.FilterStateL * 0.45f * MathF.Exp(-14.0f * progress);

                        voiceSample = (metalTone * 0.75f + electroCrunch) * env;
                        break;
                    }

                    case DartVoiceKind.ImpactResource:
                    {
                        // Denting kristal renyah ("ting-clink!")
                        float env = MathF.Exp(-5.2f * progress);
                        dv.Phase1 = WrapPhase(dv.Phase1 + dv.FreqStart * InvSampleRate);
                        dv.Phase2 = WrapPhase(dv.Phase2 + (dv.FreqStart * 2.004f) * InvSampleRate);
                        float crystalTone = MathF.Sin(dv.Phase1 * TwoPi) * 0.65f + MathF.Sin(dv.Phase2 * TwoPi) * 0.35f;

                        dv.FilterStateL += 0.50f * (noiseL - dv.FilterStateL);
                        float clink = (progress < 0.15f) ? (noiseL - dv.FilterStateL) * (1.0f - progress / 0.15f) * 0.40f : 0.0f;

                        voiceSample = (crystalTone * 0.85f + clink) * env;
                        break;
                    }

                    case DartVoiceKind.ImpactAetherStar:
                    {
                        // Resonansi energi selestial Bintang Aether ("vwooom-shimmer")
                        float env = MathF.Exp(-4.0f * progress);
                        float shimmer = 1.0f + MathF.Sin(dv.AgeSeconds * TwoPi * 30.0f) * 0.08f;
                        dv.Phase1 = WrapPhase(dv.Phase1 + (dv.FreqStart * shimmer) * InvSampleRate);
                        dv.Phase2 = WrapPhase(dv.Phase2 + (dv.FreqStart * 1.4983f * shimmer) * InvSampleRate);
                        float plasmaTone = (MathF.Sin(dv.Phase1 * TwoPi) * 0.55f + MathF.Sin(dv.Phase2 * TwoPi) * 0.45f);

                        dv.FilterStateL += 0.22f * (noiseL - dv.FilterStateL);
                        float aetherSizzle = dv.FilterStateL * 0.25f * env;

                        voiceSample = (plasmaTone * 0.80f + aetherSizzle) * env;
                        break;
                    }

                    case DartVoiceKind.ImpactRockAsteroid:
                    {
                        // Benturan batu padat ("tuk-crunch")
                        float env = MathF.Exp(-8.5f * progress);
                        float freq = Mathf.Lerp(dv.FreqStart, dv.FreqEnd, progress);
                        dv.Phase1 = WrapPhase(dv.Phase1 + freq * InvSampleRate);
                        float rockThump = MathF.Sin(dv.Phase1 * TwoPi) * 0.80f;

                        dv.FilterStateL += 0.18f * (noiseL - dv.FilterStateL);
                        dv.FilterStateR += 0.18f * (dv.FilterStateL - dv.FilterStateR);
                        float gravelCrunch = (dv.FilterStateL - dv.FilterStateR) * 0.55f * MathF.Exp(-12.0f * progress);

                        voiceSample = (rockThump * 0.60f + gravelCrunch * 0.70f) * env;
                        break;
                    }
                }

                busDartL += voiceSample * dv.GainL;
                busDartR += voiceSample * dv.GainR;
            }

            // =================================================================
            // MASTERING STAGE: SIDECHAIN DUCKING, DC BLOCKER, & SOFT-KNEE LIMITER
            // =================================================================
            float duckGain = 1.0f - _explosionDuckEnv * 0.40f;
            float mixL = (busPropulsionL + busMiningL + busDartL) * duckGain + busDestructionL;
            float mixR = (busPropulsionR + busMiningR + busDartR) * duckGain + busDestructionR;

            float dcOutL = mixL - _dcPrevInL + 0.9975f * _dcPrevOutL;
            _dcPrevInL = mixL;
            _dcPrevOutL = dcOutL;

            float dcOutR = mixR - _dcPrevInR + 0.9975f * _dcPrevOutR;
            _dcPrevInR = mixR;
            _dcPrevOutR = dcOutR;

            float finalL = SoftMasterLimit(dcOutL * 1.18f);
            float finalR = SoftMasterLimit(dcOutR * 1.18f);

            _playback.PushFrame(new Vector2(finalL, finalR));
        }
    }

    // =========================================================================
    // UTILITAS MATEMATIKA DSP & SPASIALISASI STEREO
    // =========================================================================
    private static void ProcessSvfBandpass(
        float input,
        float cutoffHz,
        float q,
        ref float ic1eq,
        ref float ic2eq,
        out float bandpass,
        out float lowpass)
    {
        float fc = Mathf.Clamp(cutoffHz, 30.0f, 14000.0f);
        float g = MathF.Tan(Mathf.Pi * fc * InvSampleRate);
        float k = 1.0f / MathF.Max(0.5f, q);

        float a1 = 1.0f / (1.0f + g * (g + k));
        float a2 = g * a1;
        float a3 = g * a2;

        float v3 = input - ic2eq;
        float v1 = a1 * ic1eq + a2 * v3;
        float v2 = ic2eq + a2 * ic1eq + a3 * v3;

        ic1eq = 2.0f * v1 - ic1eq;
        ic2eq = 2.0f * v2 - ic2eq;

        if (!float.IsFinite(ic1eq)) ic1eq = 0.0f;
        if (!float.IsFinite(ic2eq)) ic2eq = 0.0f;

        bandpass = v1;
        lowpass = v2;
    }

    private void ComputeSpatialGain(Vector2 worldPos, float maxDist, out float attenuation, out float stereoPan)
    {
        Vector2 listenerPos = (_playerCore != null && IsInstanceValid(_playerCore))
            ? _playerCore.GlobalPosition
            : Vector2.Zero;

        Vector2 delta = worldPos - listenerPos;
        float dist = delta.Length();

        if (dist >= maxDist)
        {
            attenuation = 0.0f;
            stereoPan = 0.0f;
            return;
        }

        float normDist = Mathf.Clamp((dist - 220.0f) / MathF.Max(1.0f, maxDist - 220.0f), 0.0f, 1.0f);
        attenuation = (1.0f - normDist) * (1.0f - normDist * 0.65f);
        stereoPan = Mathf.Clamp(delta.X / 960.0f, -0.82f, 0.82f);
    }

    private static void StereoPanFactors(float pan, out float gainL, out float gainR)
    {
        float clamped = Mathf.Clamp(pan, -1.0f, 1.0f);
        float angle = (clamped + 1.0f) * (Mathf.Pi * 0.25f);
        gainL = MathF.Cos(angle);
        gainR = MathF.Sin(angle);
    }

    private static float SoftMasterLimit(float x)
    {
        if (!float.IsFinite(x)) return 0.0f;
        return MathF.Tanh(x) * 0.95f;
    }

    private static float WrapPhase(float phase)
    {
        if (phase >= 1.0f) phase -= (int)phase;
        return phase;
    }

    private static float NextNoise(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return (state * 4.6566129e-10f) - 1.0f;
    }

    private static float NextFloat01(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state * 2.3283064e-10f;
    }
}
