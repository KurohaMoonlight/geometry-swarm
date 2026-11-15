using System;
using System.Collections.Generic;
using Arch.Core;
using GeometrySwarm.Core.ECS.Components;
using GeometrySwarm.Core.ECS.Systems;
using GeometrySwarm.Visual;
using Godot;
using GeometrySwarm.Gameplay.Enemy;
using static GeometrySwarm.Gameplay.Asteroid;

namespace GeometrySwarm.Gameplay;

/// <summary>
/// Driver utama simulasi kawanan Arch ECS, Laser Multi-Blok Dinamis, Sacrificial Dart Boost [SPACE],
/// GUI Pabrik Interaktif (Hive Foundry), & Tactical Swarm Dock (Bottom-Center Multi-Toggle HUD).
/// - Bug Fix Priming Laser: Menghilangkan sinar tembus asteroid pada masa priming (hanya bola fokus & feeder).
/// - Fitur Sacrificial Plasma Boost [SPACE]:
///   * 10 DartNeedle berkumpul di buritan kapal membentuk cincin nozzle ruang bakar (Focal Plasma Chamber).
///   * Seluruh sisa swarm (Mining Droid, Scanner, Guardian, Shuriken, Drill, & sisa Dart cadangan)
///     membentuk Kubah Parabola Aerodinamis (Parabolic Bow Shock Dome) di depan Core agar 0% unit tertinggal.
///   * Dart di nozzle terbakar habis satu per satu menjadi propelan exhaust (BoostDartSacrificed),
///     dan slot yang kosong langsung diisi ulang secara mulus oleh Dart cadangan dari kubah depan.
/// - Fitur Tactical Swarm Dock (Tengah Bawah Layar):
///   * Menampilkan jumlah real-time masing-masing jenis swarm (Mining, Dart, Shuriken, Boomerang, Drill, Guardian, Scanner).
///   * Mendukung Multi-Toggle independen lewat tombol [1]-[5] atau klik langsung pada kartu slot di bawah layar.
///   * Klik Kiri [LMB] HANYA mengaktifkan Mining Laser (jika [1] ON) dan/atau menembakkan swarm tempur yang slotnya sedang ON.
///   * Guardian [AUTO SHIELD] tetap bekerja otomatis mengikuti pembagian 12 Pizza Slice ancaman musuh,
///     dan Scanner [AUTO SCAN] bekerja pasif memindai deposit mineral asteroid.
/// </summary>
public partial class SwarmSimulation : Node2D
{
    [Signal]
    public delegate void GigaDrillLaunchedEventHandler(Vector2 tipPos, Vector2 basePos, Vector2 aimDir, float charge);

    [Signal]
    public delegate void GigaDrillDetonatedEventHandler(Vector2 tipPos, Vector2 basePos, int sacrificedCount);

    [Signal]
    public delegate void GigaDrillNoseBreachedEventHandler(Vector2 breachPos, Vector2 aimDir, int defenderCount);

    [Signal]
    public delegate void BoostDartSacrificedEventHandler(Vector2 nozzlePos, Vector2 aimDir, float powerRatio);

    [Signal]
    public delegate void SwarmClashImpulseEventHandler(Vector2 pos, float power, float radius);

    private struct ClashSparkParticle
    {
        public Vector2 Position;
        public Vector2 Velocity;
        public Color Color;
        public float Life;
        public float MaxLife;
        public float Size;
    }

    // =========================================================================
    // ENEMY CORE, RIVAL HIVE CONTROLLER & CLASH ANIMATION
    // =========================================================================
    private EnemyCore? _enemyCore;
    private EnemyController? _enemyController;
    public EnemyCore? EnemyCore => _enemyCore;
    public EnemyController? EnemyController => _enemyController;
    public bool IsEnemyFiring { get; set; } = false;
    private float _duelFireTimer = 0.0f;
    private const float EnemyFireInterval = 0.20f;
    private readonly List<ClashSparkParticle> _clashSparks = new();

    private struct MiningDebrisParticle
    {
        public Vector2 Position;
        public Vector2 Velocity;
        public Color Color;
        public float Life;
        public float MaxLife;
        public float Size;
        public bool IsCrystalMatter;
        public ResourceType MatterType;
        public float Rotation;
        public float RotSpeed;
        public bool IsQuantumGlass;
        public bool IsPlasmaPuff;
    }

    private readonly Vector2[] _debrisPrismVerts4 = new Vector2[4];
    private readonly Vector2[] _debrisPrismVerts5 = new Vector2[5];

    private sealed class FactoryRecipe
    {
        public string Name = "";
        public string Glyph = "";
        public string Description = "";
        public string HotkeyLabel = "";
        public SwarmRole Role;
        public bool IsCoreExpansion;
        public int OutputCount;
        public int[] Costs = new int[5]; // [Ferrite, Quartz, Carbide, Vortexum, Aether]
        public Color ThemeColor;
        public Button? CraftButton;
        public Button? AutoButton;
        public Label? CostLabel;
        public ProgressBar? ProgressBar;
        public bool AutoCraftEnabled;
    }

    private sealed class TacticalSwarmSlot
    {
        public int SlotIndex;
        public string HotkeyText = "";
        public string ShortName = "";
        public string Glyph = "";
        public SwarmRole Role;
        public bool IsPassiveAuto;
        public bool IsToggledOn;
        public Color ThemeColor;
        public PanelContainer? CardPanel;
        public StyleBoxFlat? CardStyle;
        public Label? CountLabel;
        public Label? StateBadgeLabel;
        public Button? ToggleOverlayButton;
    }

    [Export] public int InitialSwarmCount { get; set; } = 15;
    [Export] public bool InfiniteDebugResources { get; set; } = false;

    public static SwarmSimulation? Instance { get; private set; }

    private World _world = null!;
    private BoidSystem _boidSystem = null!;
    private QueryDescription _allSwarmQuery;

    private PlayerCore _playerCore = null!;
    private SwarmMultiMeshRenderer _renderer = null!;

    private static readonly Color DartColor = new(2.2f, 2.4f, 0.4f, 1.0f);
    private static readonly Color ShurikenColor = new(2.6f, 0.4f, 2.0f, 1.0f);
    private static readonly Color DrillColor = new(3.0f, 0.8f, 0.3f, 1.0f);
    private static readonly Color CrescentColor = new(0.4f, 2.8f, 1.2f, 1.0f);
    private static readonly Color GuardianColor = new(0.3f, 2.2f, 2.8f, 1.0f);
    private static readonly Color ScannerColor = new(2.5f, 0.5f, 3.6f, 1.0f);
    private static readonly Color MiningColor = new(3.4f, 1.6f, 0.3f, 1.0f);

    public int EntityCount => _entityCount;
    public int OrbitingCount => _boidSystem.OrbitingCount;
    public int LaunchedCount => _boidSystem.LaunchedCount;
    public int ReturningCount => _boidSystem.ReturningCount;
    public int EnemyCount => _boidSystem.EnemyCount;
    public int CoreCellCount => _playerCore?.CoreCellCount ?? 1;

    public int DartNeedleCount { get; private set; } = 0;
    public int GigaShurikenCount { get; private set; } = 0;
    public int CrescentBoomerangCount { get; private set; } = 0;
    public int DrillDroneCount { get; private set; } = 0;
    public int GuardianCount { get; private set; } = 0;
    public int ScannerCount { get; private set; } = 0;
    public int MiningCount { get; private set; } = 0;
    public float DrillChargeRatio => Mathf.Clamp(_drillChargeTimer / MaxDrillChargeTime, 0.0f, 1.0f);
    public bool IsChargingDrill => _isChargingDrill;
    public bool IsGigaDrillActive => _isGigaDrillActive;

    public Vector2 GigaDrillTipPosition { get; private set; } = Vector2.Zero;
    public Vector2 GigaDrillBasePosition { get; private set; } = Vector2.Zero;
    public Vector2 GigaDrillDirection { get; private set; } = Vector2.Right;
    public float GigaDrillLength { get; private set; } = 90.0f;
    public float GigaDrillRadius { get; private set; } = 32.0f;
    public float GigaDrillDensityFactor { get; private set; } = 1.0f;
    public float GigaDrillSpinTime => _gigaDrillSpinTime;
    public bool HasDefenderNoseBreached => _hasDefenderNoseBreached;
    public Vector2 GigaDrillTargetPosition => _gigaDrillTargetPos;
    public float GigaDrillNoseLength => (_activeDefenderCoreCount > 0 && !_hasDefenderNoseBreached) ? MathF.Min(56.0f, 20.0f + MathF.Sqrt(_activeDefenderCoreCount) * 6.5f) : 0.0f;
    public float GigaDrillNoseRadius => (_activeDefenderCoreCount > 0 && !_hasDefenderNoseBreached) ? MathF.Min(28.0f, 12.0f + MathF.Sqrt(_activeDefenderCoreCount) * 3.6f) : 0.0f;

    // =========================================================================
    // PARAMETER SACRIFICIAL DART PLASMA BOOST, KUBAH PARABOLA, & RETRO-BRAKE [SPACE]
    // =========================================================================
    public struct DartThrusterJet
    {
        public Vector2 Position;
        public Vector2 Direction; // Arah semburan exhaust atau konvergen
        public float Power;
        public float Length;
        public Color ThemeColor;
        public bool IsBrake;
    }

    private const int MaxBoostThrusterSlots = 10;
    private const int MaxBrakeSlots = 14;
    private bool _isBoostActive = false;
    private bool _isBoostFullPower = false;
    private float _boostPrimingTimer = 0.0f;
    private float _boostPrimingDuration = 0.45f;
    private float _boostPulseTimer = 0.0f;
    private float _boostSacrificeAccumulator = 0.0f;
    private float _boostSacrificeFlash = 0.0f;
    private int _nextSacrificeSlotCursor = 0;
    private int _activeBoostDartCount = 0;
    private readonly bool[] _boostSlotOccupied = new bool[MaxBoostThrusterSlots];
    private readonly List<DartThrusterJet> _activeThrusterJets = new(MaxBoostThrusterSlots);
    private readonly List<Vector2> _activeThrusterDartPositions = new(MaxBoostThrusterSlots);

    // Kubah Parabola di depan Core (Parabolic Bow Shock Dome) untuk seluruh sisa swarm
    private Vector2 _parabolaApexWorldPos = Vector2.Zero;
    private float _parabolaHalfSpan = 105.0f;
    private float _parabolaCurvatureK = 0.0052f;
    private int _activeParabolaSwarmCount = 0;

    // Sistem pengereman balik retro-rocket (Reverse Thruster Braking)
    private bool _isBrakeActive = false;
    private float _brakeTimer = 0.0f;
    private int _activeBrakeDartCount = 0;
    private readonly bool[] _brakeSlotOccupied = new bool[MaxBrakeSlots];
    private readonly List<DartThrusterJet> _activeBrakeJets = new(MaxBrakeSlots);
    private readonly List<Vector2> _activeBrakeDartPositions = new(MaxBrakeSlots);

    // Komponen GPU Shader Aerospike Afterburner Plasma Boost [SPACE]
    private MeshInstance2D? _plasmaBoostMesh;
    private ShaderMaterial? _plasmaBoostMat;

    public bool IsBoostActive => _isBoostActive;
    public bool IsBrakeActive => _isBrakeActive;
    public Vector2 BoostNozzlePosition { get; private set; } = Vector2.Zero;
    public Vector2 BoostAimDirection { get; private set; } = Vector2.Right;
    public float BoostPowerRatio { get; private set; } = 0.0f;
    public float BoostPulseTime => _boostPulseTimer;
    public int ActiveBoostDartCount => _activeBoostDartCount;

    private int _entityCount = 0;

    private float _fireCooldown = 0.0f;
    private const float StreamFireInterval = 0.035f;

    // =========================================================================
    // PARAMETER LASER PENAMBANGAN MULTI-BLOK (SKALA 15+ MINER -> LEBAR > 1 BLOK)
    // =========================================================================
    private bool _isMiningLaserActive = false;
    private float _laserPrimingTimer = 0.0f;
    private const float LaserPrimingDuration = 0.28f; // Priming time singkat (280 ms) sebelum menembak
    private float _laserFocusWarmup = 0.0f;
    private float _laserPulseTimer = 0.0f;
    private int _currentLaserBlockSpan = 1;
    private float _currentVisualBeamWidth = 4.0f;
    private Vector2 _laserConvergencePoint = Vector2.Zero;
    private Vector2 _laserImpactPoint = Vector2.Zero;
    private Vector2 _laserAimDir = Vector2.Right;
    private float _laserHitAsteroidRotation = 0.0f;
    private readonly List<(Vector2 worldCenter, float heat, VoxelType type)> _laserActiveHitBoxes = new();
    private readonly List<Vector2> _activeMinerPositions = new();
    private readonly List<MiningDebrisParticle> _miningDebris = new();

    private struct AsteroidShockwaveRing
    {
        public Vector2 Center;
        public float Radius;
        public float StartRadius;
        public float MaxRadius;
        public float Life;
        public float MaxLife;
        public Color Color1;
        public Color Color2;
        public bool IsEnergy;
        public bool IsInward;
    }
    private readonly List<AsteroidShockwaveRing> _asteroidShockwaves = new();

    // Efek Plasma Siphoning (Aliran plasma mengalir mundur dari Bintang Mini ke kapal saat ditambang)
    private struct PlasmaSiphonWisp
    {
        public float Progress;
        public float Speed;
        public float Radius;
        public float Angle;
        public float AngleSpeed;
        public Color Color;
        public float Size;
    }
    private readonly List<PlasmaSiphonWisp> _plasmaSiphonWisps = new();
    private bool _isMiningEnergyAsteroid = false;

    // =========================================================================
    // INVENTORI 5 RESOURCE, GUI PABRIK, & TACTICAL SWARM DOCK (TENGAH BAWAH)
    // =========================================================================
    private const int InfiniteStockValue = 999999;
    private readonly int[] _resourceStock = new int[5] { 35, 15, 10, 0, 0 };
    private readonly Label[] _resourceLabels = new Label[5];
    private Label? _laserTierInfoLabel;
    private PanelContainer? _foundryMainPanel;
    private VBoxContainer? _foundryRecipesBox;
    private Button? _toggleFoundryButton;
    private Tween? _foundrySlideTween;
    private bool _isFoundryExpanded = true;
    private float _autoCraftTimer = 0.0f;
    private readonly List<FactoryRecipe> _recipes = new();

    // Tactical Swarm Hotbar Dock di Tengah Bawah (Multi-Toggle [1]-[5] + Indikator Pasif)
    private PanelContainer? _tacticalDockPanel;
    private readonly List<TacticalSwarmSlot> _tacticalSlots = new();
    private readonly bool[] _roleToggledOn = new bool[7];

    // Parameter Super Giga Drill Break
    private bool _isChargingDrill = false;
    private float _drillChargeTimer = 0.0f;
    private const float MaxDrillChargeTime = 1.65f;
    private float _gigaDrillSpinTime = 0.0f;
    private float _overchargeExhaustTimer = 0.0f;

    private bool _isGigaDrillActive = false;
    private Vector2 _gigaDrillTipPos = Vector2.Zero;
    private Vector2 _gigaDrillAimDir = Vector2.Right;
    private Vector2 _gigaDrillVel = Vector2.Zero;
    private Vector2 _gigaDrillTargetPos = Vector2.Zero;
    private bool _hasDefenderNoseBreached = false;
    private float _gigaDrillFlightTimer = 0.0f;
    private float _gigaDrillElapsedFlight = 0.0f;
    private const float GigaDrillFlightDuration = 1.45f;

    private int _activeDrillFormationCount = 0;
    private int _activeDefenderCoreCount = 0;
    private int _gigaDrillExhaustStartIndex = 0;

    private float _drillAsteroidGrindTimer = 0.0f;
    private float _drillEnemyGrindTimer = 0.0f;
    private float _drillAttritionTimer = 0.0f;

    // Parameter Core-Ramming Sequence (Stall -> 0.5s Spin -> Spectacular Supernova) & Asteroid Grinding
    private bool _isRammingEnemyCore = false;
    private float _coreRamStallTimer = 0.0f;
    private float _coreRamSpinTimer = 0.0f;
    private int _coreRamInitialDrillerCount = 0;
    private float _coreRamAttritionAccumulator = 0.0f;
    private bool _isGrindingAsteroid = false;
    private Vector2 _gigaDrillSafeOutsidePos = Vector2.Zero;
    private readonly List<(Vector2 worldCenter, VoxelType type)> _destroyedVoxelsBuffer = new();

    public bool IsRammingEnemyCore => _isRammingEnemyCore;

    public float PlayerHealth => _playerCore?.Health ?? 0.0f;
    public float PlayerMaxHealth => _playerCore?.MaxHealth ?? 1.0f;
    public bool IsPlayerEscapePod => _playerCore?.IsEscapePod ?? false;
    public float EnemyHealth => _enemyCore?.Health ?? 0.0f;
    public float EnemyMaxHealth => _enemyCore?.MaxHealth ?? 1.0f;
    public bool IsEnemyEscapePod => _enemyCore?.IsEscapePod ?? false;
    public float DistanceToEnemy => (_playerCore != null && _enemyCore != null) ? _playerCore.GlobalPosition.DistanceTo(_enemyCore.GlobalPosition) : 0.0f;
    public string EnemyStateText => _enemyController != null
        ? $"{_enemyController.State} [⛏ {_enemyController.MinerCount} | ⛨ {_enemyController.GuardianCount} | ▲ {_enemyController.DartCount}]"
        : "None";

    public int GigaDrillTotalUnits => _activeDrillFormationCount + _activeDefenderCoreCount;
    public int GigaDrillDefenderCoreCount => _activeDefenderCoreCount;
    public int GigaDrillLanceCount => _activeDrillFormationCount;

    public override void _Ready()
    {
        Instance = this;
        _world = World.Create();
        _boidSystem = new BoidSystem(_world);
        _allSwarmQuery = new QueryDescription().WithAll<PositionComponent, VelocityComponent, SwarmUnitComponent, VisualComponent>();

        _playerCore = GetNode<PlayerCore>("PlayerCore");
        if (_playerCore != null)
        {
            _playerCore.EscapePodEjected += OnPlayerEscapePodEjected;
            _playerCore.CoreDestroyed += OnPlayerCoreDestroyed;
        }
        _renderer = GetNode<SwarmMultiMeshRenderer>("SwarmRenderer");

        if (InfiniteDebugResources)
        {
            for (int r = 0; r < 5; r++) _resourceStock[r] = InfiniteStockValue;
        }

        InitializeRecipes();
        InitializeTacticalSlots();
        CallDeferred(MethodName.BuildInteractiveFoundryGui);
        SpawnStartingSwarm();

        // Inisialisasi Shader Mesh Aerospike Plasma Boost [SPACE]
        var boostShader = GD.Load<Shader>("res://visual/plasma_boost_beam.gdshader");
        if (boostShader != null)
        {
            _plasmaBoostMat = new ShaderMaterial { Shader = boostShader };
            var quad = new QuadMesh { Size = new Vector2(1.0f, 1.0f) };
            _plasmaBoostMesh = new MeshInstance2D
            {
                Name = "PlasmaBoostBeamMesh",
                Mesh = quad,
                Material = _plasmaBoostMat,
                Visible = false,
                ZIndex = 5
            };
            AddChild(_plasmaBoostMesh);
        }

        // Inisialisasi Rival Hive Core & AI Controller di Sabuk Asteroid (Jarak ~1.750 px dari Pusat)
        _enemyCore = new EnemyCore();
        _enemyCore.GlobalPosition = new Vector2(1750.0f, 350.0f);
        AddChild(_enemyCore);
        _enemyCore.EnemyEscapePodEjected += OnEnemyEscapePodEjected;
        _enemyCore.EnemyCoreDestroyed += OnEnemyCoreDestroyed;

        _enemyController = new EnemyController();
        _enemyCore.AddChild(_enemyController);
        _enemyController.Initialize(this, _enemyCore);

        // Spawn armada awal Rival Hive (fokus penambangan mandiri)
        AddEnemySwarm(SwarmRole.MiningDroid, 3);
        AddEnemySwarm(SwarmRole.DartNeedle, 10);
    }

    public void SpawnStartingSwarm()
    {
        AddSpecificSwarm(SwarmRole.DartNeedle, 40);
        AddSpecificSwarm(SwarmRole.ScannerDrone, 5);
        AddSpecificSwarm(SwarmRole.MiningDroid, 10);
    }

    private void InitializeRecipes()
    {
        _recipes.Clear();

        // 1. Dart Needle (+10): 10 Ferrite
        _recipes.Add(new FactoryRecipe
        {
            Name = "Dart Needle (+10)",
            Glyph = "▲",
            Description = "Swarm penusuk cepat & bahan bakar Plasma Boost [SPACE]",
            HotkeyLabel = "▲",
            Role = SwarmRole.DartNeedle,
            OutputCount = 10,
            Costs = new[] { 10, 0, 0, 0, 0 },
            ThemeColor = new Color(0.95f, 1.0f, 0.35f)
        });

        // 2. Mining Droid (+2): 10 Ferrite, 8 Quartz, 2 Aether
        _recipes.Add(new FactoryRecipe
        {
            Name = "Mining Droid (+2)",
            Glyph = "⛏",
            Description = "Gali batu asteroid & perlebar laser kapal",
            HotkeyLabel = "⛏",
            Role = SwarmRole.MiningDroid,
            OutputCount = 2,
            Costs = new[] { 10, 8, 0, 0, 2 },
            ThemeColor = new Color(1.0f, 0.68f, 0.22f)
        });

        // 3. Scanner Drone (+2): 6 Ferrite, 12 Quartz, 2 Aether
        _recipes.Add(new FactoryRecipe
        {
            Name = "Scanner Drone (+2)",
            Glyph = "◈",
            Description = "Pindai deposit mineral tersembunyi",
            HotkeyLabel = "◈",
            Role = SwarmRole.ScannerDrone,
            OutputCount = 2,
            Costs = new[] { 6, 12, 0, 0, 2 },
            ThemeColor = new Color(0.85f, 0.50f, 1.0f)
        });

        // 4. Guardian Shield (+5): 12 Ferrite, 12 Quartz, 8 Carbide
        _recipes.Add(new FactoryRecipe
        {
            Name = "Guardian Shield (+5)",
            Glyph = "⛨",
            Description = "Perisai otomatis 12 sektor penahan musuh",
            HotkeyLabel = "⛨",
            Role = SwarmRole.Guardian,
            OutputCount = 5,
            Costs = new[] { 12, 12, 8, 0, 0 },
            ThemeColor = new Color(0.25f, 0.92f, 1.0f)
        });

        // 5. Drill Drone / Lance (+5): 12 Ferrite, 18 Carbide, 4 Aether
        _recipes.Add(new FactoryRecipe
        {
            Name = "Drill Drone (+5)",
            Glyph = "▼",
            Description = "Penembus batu & tombak Super Giga Drill [RMB]",
            HotkeyLabel = "▼",
            Role = SwarmRole.DrillDrone,
            OutputCount = 5,
            Costs = new[] { 12, 0, 18, 0, 4 },
            ThemeColor = new Color(1.0f, 0.38f, 0.25f)
        });

        // 6. Giga Shuriken (+5): 10 Ferrite, 14 Carbide, 10 Vortexum
        _recipes.Add(new FactoryRecipe
        {
            Name = "Giga Shuriken (+5)",
            Glyph = "✹",
            Description = "Cakram putar pemotong rintangan",
            HotkeyLabel = "✹",
            Role = SwarmRole.GigaShuriken,
            OutputCount = 5,
            Costs = new[] { 10, 0, 14, 10, 0 },
            ThemeColor = new Color(1.0f, 0.32f, 0.85f)
        });

        // 7. Crescent Boomerang (+5): 8 Quartz, 16 Vortexum, 4 Aether
        _recipes.Add(new FactoryRecipe
        {
            Name = "Boomerang (+5)",
            Glyph = "☽",
            Description = "Sapu melengkung area luas dan kembali",
            HotkeyLabel = "☽",
            Role = SwarmRole.CrescentBoomerang,
            OutputCount = 5,
            Costs = new[] { 0, 8, 0, 16, 4 },
            ThemeColor = new Color(0.32f, 1.0f, 0.65f)
        });

        // 8. Expand Hive Core (+1 Hex): 35 Ferrite, 25 Quartz, 25 Carbide, 20 Aether
        _recipes.Add(new FactoryRecipe
        {
            Name = "Expand Hive Core (+1 Hex)",
            Glyph = "⬡",
            Description = "Membelah sel core secara mitosis",
            HotkeyLabel = "⬡",
            IsCoreExpansion = true,
            OutputCount = 1,
            Costs = new[] { 35, 25, 25, 0, 20 },
            ThemeColor = new Color(1.0f, 0.88f, 0.35f)
        });
    }

    private void InitializeTacticalSlots()
    {
        _tacticalSlots.Clear();
        Array.Clear(_roleToggledOn, 0, _roleToggledOn.Length);

        // Default: [1] Mining Laser dan [2] Dart Needle aktif di awal permainan
        _roleToggledOn[(int)SwarmRole.MiningDroid] = true;
        _roleToggledOn[(int)SwarmRole.DartNeedle] = true;
        _roleToggledOn[(int)SwarmRole.GigaShuriken] = true;
        _roleToggledOn[(int)SwarmRole.CrescentBoomerang] = true;
        _roleToggledOn[(int)SwarmRole.DrillDrone] = false; // Default disimpan untuk Super Giga Drill [RMB], bisa di-toggle [5]

        // 5 Slot Aktif Multi-Toggle (Klik Kiri / Tombol 1-5)
        _tacticalSlots.Add(new TacticalSwarmSlot
        {
            SlotIndex = 0,
            HotkeyText = "[1]",
            ShortName = "MINING",
            Glyph = "⛏",
            Role = SwarmRole.MiningDroid,
            IsPassiveAuto = false,
            IsToggledOn = _roleToggledOn[(int)SwarmRole.MiningDroid],
            ThemeColor = new Color(1.0f, 0.68f, 0.22f)
        });

        _tacticalSlots.Add(new TacticalSwarmSlot
        {
            SlotIndex = 1,
            HotkeyText = "[2]",
            ShortName = "DART",
            Glyph = "▲",
            Role = SwarmRole.DartNeedle,
            IsPassiveAuto = false,
            IsToggledOn = _roleToggledOn[(int)SwarmRole.DartNeedle],
            ThemeColor = new Color(0.95f, 1.0f, 0.35f)
        });

        _tacticalSlots.Add(new TacticalSwarmSlot
        {
            SlotIndex = 2,
            HotkeyText = "[3]",
            ShortName = "SHURIKEN",
            Glyph = "✹",
            Role = SwarmRole.GigaShuriken,
            IsPassiveAuto = false,
            IsToggledOn = _roleToggledOn[(int)SwarmRole.GigaShuriken],
            ThemeColor = new Color(1.0f, 0.32f, 0.85f)
        });

        _tacticalSlots.Add(new TacticalSwarmSlot
        {
            SlotIndex = 3,
            HotkeyText = "[4]",
            ShortName = "BOOMERANG",
            Glyph = "☽",
            Role = SwarmRole.CrescentBoomerang,
            IsPassiveAuto = false,
            IsToggledOn = _roleToggledOn[(int)SwarmRole.CrescentBoomerang],
            ThemeColor = new Color(0.32f, 1.0f, 0.65f)
        });

        _tacticalSlots.Add(new TacticalSwarmSlot
        {
            SlotIndex = 4,
            HotkeyText = "[5]",
            ShortName = "DRILL",
            Glyph = "▼",
            Role = SwarmRole.DrillDrone,
            IsPassiveAuto = false,
            IsToggledOn = _roleToggledOn[(int)SwarmRole.DrillDrone],
            ThemeColor = new Color(1.0f, 0.38f, 0.25f)
        });

        // 2 Slot Indikator Otomatis / Pasif (Guardian 12-Sector Pizza Slice & Scanner)
        _tacticalSlots.Add(new TacticalSwarmSlot
        {
            SlotIndex = 5,
            HotkeyText = "AUTO",
            ShortName = "GUARDIAN",
            Glyph = "⛨",
            Role = SwarmRole.Guardian,
            IsPassiveAuto = true,
            IsToggledOn = true,
            ThemeColor = new Color(0.25f, 0.92f, 1.0f)
        });

        _tacticalSlots.Add(new TacticalSwarmSlot
        {
            SlotIndex = 6,
            HotkeyText = "PASSIVE",
            ShortName = "SCANNER",
            Glyph = "◈",
            Role = SwarmRole.ScannerDrone,
            IsPassiveAuto = true,
            IsToggledOn = true,
            ThemeColor = new Color(0.85f, 0.50f, 1.0f)
        });
    }

    private const float FoundryPanelWidth = 380.0f;
    private const float FoundryOpenX = 16.0f;
    private const float FoundryClosedX = -410.0f;
    private const float FoundryOpenBtnX = 276.0f;
    private const float FoundryClosedBtnX = 0.0f;

    /// <summary>
    /// Membangun GUI Pabrik (Hive Foundry) di sebelah kiri dan Tactical Swarm Hotbar Dock di Tengah Bawah.
    /// </summary>
    private void BuildInteractiveFoundryGui()
    {
        CanvasLayer? hudLayer = GetParent()?.GetNodeOrNull<CanvasLayer>("HUD");
        if (hudLayer == null) return;

        var panelStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.04f, 0.045f, 0.08f, 0.92f),
            BorderColor = new Color(0.25f, 0.65f, 1.0f, 0.65f),
            BorderWidthLeft = 2,
            BorderWidthTop = 2,
            BorderWidthRight = 2,
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 10,
            CornerRadiusTopRight = 10,
            CornerRadiusBottomLeft = 10,
            CornerRadiusBottomRight = 10,
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 10,
            ContentMarginBottom = 10
        };

        _foundryMainPanel = new PanelContainer
        {
            Name = "HiveFoundryPanel",
            Position = new Vector2(FoundryOpenX, 72),
            CustomMinimumSize = new Vector2(FoundryPanelWidth, 0),
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        _foundryMainPanel.AddThemeStyleboxOverride("panel", panelStyle);
        hudLayer.AddChild(_foundryMainPanel);

        var rootVBox = new VBoxContainer();
        rootVBox.AddThemeConstantOverride("separation", 6);
        _foundryMainPanel.AddChild(rootVBox);

        // 1. Header Pabrik
        var headerHBox = new HBoxContainer();
        headerHBox.AddThemeConstantOverride("separation", 8);
        rootVBox.AddChild(headerHBox);

        var titleLbl = new Label
        {
            Text = "⚙ HIVE FOUNDRY // PABRIK (DEBUG: ∞)",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        titleLbl.AddThemeColorOverride("font_color", new Color(1.0f, 0.88f, 0.35f));
        titleLbl.AddThemeFontSizeOverride("font_size", 14);
        headerHBox.AddChild(titleLbl);

        var tabStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.09f, 0.16f, 0.95f),
            BorderColor = new Color(0.35f, 0.85f, 1.0f, 0.85f),
            BorderWidthLeft = 2,
            BorderWidthTop = 2,
            BorderWidthRight = 2,
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 0,
            CornerRadiusBottomLeft = 0,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomRight = 8,
            ContentMarginLeft = 8,
            ContentMarginRight = 10,
            ContentMarginTop = 4,
            ContentMarginBottom = 4
        };

        _toggleFoundryButton = new Button
        {
            Text = "◀ TUTUP [TAB]",
            Position = new Vector2(FoundryOpenBtnX, 72),
            CustomMinimumSize = new Vector2(110, 28),
            MouseFilter = Control.MouseFilterEnum.Stop,
            FocusMode = Control.FocusModeEnum.None
        };
        _toggleFoundryButton.AddThemeStyleboxOverride("normal", tabStyle);
        _toggleFoundryButton.AddThemeStyleboxOverride("hover", tabStyle);
        _toggleFoundryButton.AddThemeStyleboxOverride("pressed", tabStyle);
        _toggleFoundryButton.AddThemeColorOverride("font_color", new Color(0.4f, 2.2f, 2.6f));
        _toggleFoundryButton.AddThemeFontSizeOverride("font_size", 11);
        _toggleFoundryButton.Pressed += ToggleFoundryPanel;
        hudLayer.AddChild(_toggleFoundryButton);

        // 2. Bar Inventori 5 Resource Real-Time (Capsule Pill Cards)
        var resHBox = new HBoxContainer();
        resHBox.AddThemeConstantOverride("separation", 6);
        rootVBox.AddChild(resHBox);

        string[] resSymbols = { "◆ Fe", "◆ Qz", "◆ Cb", "◆ Vx", "⚡ Ae" };
        Color[] resUiColors =
        {
            new(0.95f, 1.0f, 0.35f),  // Ferrite (Amber)
            new(0.25f, 0.92f, 1.0f),  // Quartz (Cyan)
            new(1.0f, 0.42f, 0.25f),  // Carbide (Crimson)
            new(0.3f, 1.0f, 0.6f),    // Vortexum (Emerald)
            new(1.0f, 0.92f, 0.45f)   // Aether (Astral Gold)
        };

        for (int r = 0; r < 5; r++)
        {
            var pillPanel = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            var pillStyle = new StyleBoxFlat
            {
                BgColor = new Color(0.08f, 0.10f, 0.16f, 0.85f),
                BorderColor = resUiColors[r] * 0.4f,
                BorderWidthLeft = 1,
                BorderWidthTop = 1,
                BorderWidthRight = 1,
                BorderWidthBottom = 1,
                CornerRadiusTopLeft = 4,
                CornerRadiusTopRight = 4,
                CornerRadiusBottomLeft = 4,
                CornerRadiusBottomRight = 4,
                ContentMarginLeft = 4,
                ContentMarginRight = 4,
                ContentMarginTop = 2,
                ContentMarginBottom = 2
            };
            pillPanel.AddThemeStyleboxOverride("panel", pillStyle);
            resHBox.AddChild(pillPanel);

            var lbl = new Label
            {
                Text = InfiniteDebugResources ? $"{resSymbols[r]}: ∞" : $"{resSymbols[r]}: {_resourceStock[r]}",
                HorizontalAlignment = HorizontalAlignment.Center
            };
            lbl.AddThemeColorOverride("font_color", resUiColors[r]);
            lbl.AddThemeFontSizeOverride("font_size", 11);
            pillPanel.AddChild(lbl);
            _resourceLabels[r] = lbl;
        }

        // Info Lebar Laser Saat Ini
        _laserTierInfoLabel = new Label
        {
            Text = "⚡ LASER: Tier 1 (1 Blok / 13px) • 10/15 Miner"
        };
        _laserTierInfoLabel.AddThemeColorOverride("font_color", new Color(1.0f, 0.76f, 0.35f));
        _laserTierInfoLabel.AddThemeFontSizeOverride("font_size", 11);
        rootVBox.AddChild(_laserTierInfoLabel);

        // 3. Scroll Container untuk Daftar Kartu Unit (Modern Card List)
        var scrollContainer = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(FoundryPanelWidth - 24, 380),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto
        };
        rootVBox.AddChild(scrollContainer);

        _foundryRecipesBox = new VBoxContainer();
        _foundryRecipesBox.AddThemeConstantOverride("separation", 6);
        _foundryRecipesBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scrollContainer.AddChild(_foundryRecipesBox);

        for (int i = 0; i < _recipes.Count; i++)
        {
            int recipeIndex = i;
            var rec = _recipes[i];

            var cardPanel = new PanelContainer();
            cardPanel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            var cardStyle = new StyleBoxFlat
            {
                BgColor = new Color(0.05f, 0.06f, 0.11f, 0.88f),
                BorderColor = rec.ThemeColor * 0.5f,
                BorderWidthLeft = 3,
                BorderWidthTop = 1,
                BorderWidthRight = 1,
                BorderWidthBottom = 1,
                CornerRadiusTopLeft = 6,
                CornerRadiusTopRight = 6,
                CornerRadiusBottomLeft = 6,
                CornerRadiusBottomRight = 6,
                ContentMarginLeft = 8,
                ContentMarginRight = 8,
                ContentMarginTop = 5,
                ContentMarginBottom = 5
            };
            cardPanel.AddThemeStyleboxOverride("panel", cardStyle);
            _foundryRecipesBox.AddChild(cardPanel);

            var cardVBox = new VBoxContainer();
            cardVBox.AddThemeConstantOverride("separation", 3);
            cardPanel.AddChild(cardVBox);

            // Baris 1: Glyph + Nama Unit + Tombol Auto-Craft
            var topHBox = new HBoxContainer();
            topHBox.AddThemeConstantOverride("separation", 6);
            cardVBox.AddChild(topHBox);

            var titleLabel = new Label
            {
                Text = $"{rec.Glyph} {rec.Name}",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            titleLabel.AddThemeColorOverride("font_color", rec.ThemeColor);
            titleLabel.AddThemeFontSizeOverride("font_size", 12);
            topHBox.AddChild(titleLabel);

            var autoBtn = new Button
            {
                Text = "AUTO",
                CustomMinimumSize = new Vector2(56, 20),
                MouseFilter = Control.MouseFilterEnum.Stop,
                FocusMode = Control.FocusModeEnum.None
            };
            autoBtn.AddThemeFontSizeOverride("font_size", 10);
            autoBtn.Pressed += () => ToggleAutoCraft(recipeIndex);
            topHBox.AddChild(autoBtn);
            rec.AutoButton = autoBtn;

            // Baris 2: Deskripsi Ramah & Informatif
            var descLabel = new Label
            {
                Text = rec.Description
            };
            descLabel.AddThemeColorOverride("font_color", new Color(0.68f, 0.72f, 0.82f, 0.85f));
            descLabel.AddThemeFontSizeOverride("font_size", 10);
            cardVBox.AddChild(descLabel);

            // Baris 3: Tag Biaya Bahan + Tombol Rakit
            var botHBox = new HBoxContainer();
            botHBox.AddThemeConstantOverride("separation", 6);
            cardVBox.AddChild(botHBox);

            var costLbl = new Label
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            costLbl.AddThemeFontSizeOverride("font_size", 11);
            botHBox.AddChild(costLbl);
            rec.CostLabel = costLbl;

            var craftBtn = new Button
            {
                Text = "RAKIT",
                CustomMinimumSize = new Vector2(80, 24),
                MouseFilter = Control.MouseFilterEnum.Stop,
                FocusMode = Control.FocusModeEnum.None
            };
            craftBtn.AddThemeFontSizeOverride("font_size", 11);
            craftBtn.Pressed += () => TryCraftRecipe(recipeIndex);
            botHBox.AddChild(craftBtn);
            rec.CraftButton = craftBtn;

            // Khusus Core Expansion: Progress Bar Mitosis
            if (rec.IsCoreExpansion)
            {
                var pbar = new ProgressBar
                {
                    CustomMinimumSize = new Vector2(0, 6),
                    ShowPercentage = false,
                    MinValue = 0,
                    MaxValue = 100,
                    Value = 0,
                    Visible = false
                };
                cardVBox.AddChild(pbar);
                rec.ProgressBar = pbar;
            }
        }

        // 4. Tombol Uji Tambah Dart Cepat
        var cheatBtn = new Button
        {
            Text = "+ Tambah 50 Dart Needle [Uji Boost Space]",
            CustomMinimumSize = new Vector2(0, 26),
            MouseFilter = Control.MouseFilterEnum.Stop,
            FocusMode = Control.FocusModeEnum.None
        };
        cheatBtn.AddThemeFontSizeOverride("font_size", 10);
        cheatBtn.Pressed += () =>
        {
            AddSpecificSwarm(SwarmRole.DartNeedle, 50);
            RefreshFoundryGui();
        };
        rootVBox.AddChild(cheatBtn);

        // 5. Bangun Tactical Swarm Hotbar Dock di Tengah Bawah Layar
        BuildBottomCenterSwarmDockGui(hudLayer);

        RefreshFoundryGui();
    }

    /// <summary>
    /// Membangun GUI Tactical Swarm Dock di Tengah Bawah Layar:
    /// - Menampilkan jumlah masing-masing jenis swarm secara real-time.
    /// - Slot [1] s/d [5] mendukung Multi-Toggle untuk memilih aksi Klik Kiri (Mining Laser / Tembak Swarm).
    /// - Slot Guardian & Scanner menampilkan jumlah dan status otomatis (12-Sector Pizza Slice Shield & Auto-Scan).
    /// </summary>
    private void BuildBottomCenterSwarmDockGui(CanvasLayer hudLayer)
    {
        var bottomAnchorBox = new HBoxContainer
        {
            Name = "BottomCenterDockAnchor",
            AnchorLeft = 0.0f,
            AnchorRight = 1.0f,
            AnchorTop = 1.0f,
            AnchorBottom = 1.0f,
            OffsetTop = -102.0f,
            OffsetBottom = -14.0f,
            Alignment = BoxContainer.AlignmentMode.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        hudLayer.AddChild(bottomAnchorBox);

        var dockOuterStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.03f, 0.04f, 0.075f, 0.92f),
            BorderColor = new Color(0.28f, 0.72f, 1.0f, 0.55f),
            BorderWidthLeft = 2,
            BorderWidthTop = 2,
            BorderWidthRight = 2,
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 12,
            CornerRadiusTopRight = 12,
            CornerRadiusBottomLeft = 12,
            CornerRadiusBottomRight = 12,
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 8,
            ContentMarginBottom = 8
        };

        _tacticalDockPanel = new PanelContainer
        {
            Name = "TacticalSwarmDockPanel",
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        _tacticalDockPanel.AddThemeStyleboxOverride("panel", dockOuterStyle);
        bottomAnchorBox.AddChild(_tacticalDockPanel);

        var dockVBox = new VBoxContainer();
        dockVBox.AddThemeConstantOverride("separation", 4);
        _tacticalDockPanel.AddChild(dockVBox);

        var dockHintLabel = new Label
        {
            Text = "⚡ TACTICAL SWARM MULTI-TOGGLE [1-5] • KLIK KIRI [LMB]: AKTIFKAN LASER / TEMBAK SWARM YANG ON",
            HorizontalAlignment = HorizontalAlignment.Center
        };
        dockHintLabel.AddThemeColorOverride("font_color", new Color(0.68f, 0.85f, 1.0f, 0.88f));
        dockHintLabel.AddThemeFontSizeOverride("font_size", 10);
        dockVBox.AddChild(dockHintLabel);

        var slotsHBox = new HBoxContainer();
        slotsHBox.AddThemeConstantOverride("separation", 8);
        slotsHBox.Alignment = BoxContainer.AlignmentMode.Center;
        dockVBox.AddChild(slotsHBox);

        for (int i = 0; i < _tacticalSlots.Count; i++)
        {
            int slotIdx = i;
            var slot = _tacticalSlots[i];

            // Beri pemisah tipis sebelum slot pasif otomatis (Guardian & Scanner)
            if (i == 5)
            {
                var sep = new VSeparator
                {
                    CustomMinimumSize = new Vector2(6, 0)
                };
                slotsHBox.AddChild(sep);
            }

            var cardStyle = new StyleBoxFlat
            {
                BgColor = new Color(0.06f, 0.08f, 0.14f, 0.92f),
                BorderColor = slot.ThemeColor,
                BorderWidthLeft = 2,
                BorderWidthTop = 2,
                BorderWidthRight = 2,
                BorderWidthBottom = 2,
                CornerRadiusTopLeft = 8,
                CornerRadiusTopRight = 8,
                CornerRadiusBottomLeft = 8,
                CornerRadiusBottomRight = 8,
                ContentMarginLeft = 8,
                ContentMarginRight = 8,
                ContentMarginTop = 5,
                ContentMarginBottom = 5
            };

            var cardPanel = new PanelContainer
            {
                CustomMinimumSize = new Vector2(96, 56),
                MouseFilter = Control.MouseFilterEnum.Stop
            };
            cardPanel.AddThemeStyleboxOverride("panel", cardStyle);
            slotsHBox.AddChild(cardPanel);

            slot.CardPanel = cardPanel;
            slot.CardStyle = cardStyle;

            var cardVBox = new VBoxContainer();
            cardVBox.AddThemeConstantOverride("separation", 1);
            cardVBox.MouseFilter = Control.MouseFilterEnum.Ignore;
            cardPanel.AddChild(cardVBox);

            // Baris 1: Hotkey [1] di kiri + Status ON/OFF di kanan
            var topRow = new HBoxContainer();
            topRow.MouseFilter = Control.MouseFilterEnum.Ignore;
            cardVBox.AddChild(topRow);

            var keyLbl = new Label
            {
                Text = slot.HotkeyText,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            keyLbl.AddThemeFontSizeOverride("font_size", 10);
            keyLbl.AddThemeColorOverride("font_color", new Color(0.78f, 0.86f, 0.98f));
            topRow.AddChild(keyLbl);

            var badgeLbl = new Label
            {
                Text = slot.IsPassiveAuto ? "AUTO" : (slot.IsToggledOn ? "● ON" : "○ OFF"),
                HorizontalAlignment = HorizontalAlignment.Right,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            badgeLbl.AddThemeFontSizeOverride("font_size", 10);
            topRow.AddChild(badgeLbl);
            slot.StateBadgeLabel = badgeLbl;

            // Baris 2: Glyph + Jumlah Unit Real-Time berukuran besar
            var countLbl = new Label
            {
                Text = $"{slot.Glyph} 0",
                HorizontalAlignment = HorizontalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            countLbl.AddThemeFontSizeOverride("font_size", 15);
            countLbl.AddThemeColorOverride("font_color", slot.ThemeColor);
            cardVBox.AddChild(countLbl);
            slot.CountLabel = countLbl;

            // Baris 3: Nama Jenis Swarm
            var nameLbl = new Label
            {
                Text = slot.ShortName,
                HorizontalAlignment = HorizontalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            nameLbl.AddThemeFontSizeOverride("font_size", 9);
            nameLbl.AddThemeColorOverride("font_color", new Color(0.75f, 0.80f, 0.90f, 0.90f));
            cardVBox.AddChild(nameLbl);

            // Jika bukan pasif, tambahkan tombol transparan di atas kartu agar bisa diklik langsung dengan mouse
            if (!slot.IsPassiveAuto)
            {
                var emptyStyle = new StyleBoxEmpty();
                var hoverStyle = new StyleBoxFlat
                {
                    BgColor = new Color(1.0f, 1.0f, 1.0f, 0.07f),
                    CornerRadiusTopLeft = 8,
                    CornerRadiusTopRight = 8,
                    CornerRadiusBottomLeft = 8,
                    CornerRadiusBottomRight = 8
                };

                var btnOverlay = new Button
                {
                    Flat = false,
                    FocusMode = Control.FocusModeEnum.None,
                    MouseFilter = Control.MouseFilterEnum.Stop,
                    MouseDefaultCursorShape = Control.CursorShape.PointingHand
                };
                btnOverlay.AddThemeStyleboxOverride("normal", emptyStyle);
                btnOverlay.AddThemeStyleboxOverride("hover", hoverStyle);
                btnOverlay.AddThemeStyleboxOverride("pressed", hoverStyle);
                btnOverlay.Pressed += () => ToggleTacticalSlot(slotIdx);
                cardPanel.AddChild(btnOverlay);
                slot.ToggleOverlayButton = btnOverlay;
            }
        }

        RefreshTacticalDockGui();
    }

    public void ToggleTacticalSlot(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= _tacticalSlots.Count) return;
        var slot = _tacticalSlots[slotIndex];
        if (slot.IsPassiveAuto) return;

        slot.IsToggledOn = !slot.IsToggledOn;
        _roleToggledOn[(int)slot.Role] = slot.IsToggledOn;

        // Jika Mining Droid baru saja di-OFF-kan saat laser menyala, langsung matikan laser
        if (slot.Role == SwarmRole.MiningDroid && !slot.IsToggledOn && _isMiningLaserActive)
        {
            _laserPrimingTimer = 0.0f;
            _laserFocusWarmup = 0.0f;
            _isMiningLaserActive = false;
            _laserActiveHitBoxes.Clear();
            QueueRedraw();
        }

        RefreshTacticalDockGui();
    }

    private int GetRoleSwarmCount(SwarmRole role)
    {
        return role switch
        {
            SwarmRole.MiningDroid => MiningCount,
            SwarmRole.DartNeedle => DartNeedleCount,
            SwarmRole.GigaShuriken => GigaShurikenCount,
            SwarmRole.CrescentBoomerang => CrescentBoomerangCount,
            SwarmRole.DrillDrone => DrillDroneCount,
            SwarmRole.Guardian => GuardianCount,
            SwarmRole.ScannerDrone => ScannerCount,
            _ => 0
        };
    }

    private void RefreshTacticalDockGui()
    {
        for (int i = 0; i < _tacticalSlots.Count; i++)
        {
            var slot = _tacticalSlots[i];
            int count = GetRoleSwarmCount(slot.Role);

            if (slot.CountLabel != null)
            {
                slot.CountLabel.Text = $"{slot.Glyph} {count}";
            }

            if (slot.IsPassiveAuto)
            {
                if (slot.StateBadgeLabel != null)
                {
                    slot.StateBadgeLabel.Text = slot.Role == SwarmRole.Guardian ? "12-SEC" : "PASSIVE";
                    slot.StateBadgeLabel.AddThemeColorOverride("font_color", slot.ThemeColor * 0.95f);
                }
                if (slot.CardStyle != null)
                {
                    slot.CardStyle.BgColor = new Color(0.05f, 0.07f, 0.12f, 0.88f);
                    slot.CardStyle.BorderColor = slot.ThemeColor * (count > 0 ? 0.55f : 0.25f);
                }
                if (slot.CountLabel != null)
                {
                    slot.CountLabel.AddThemeColorOverride("font_color", count > 0 ? slot.ThemeColor : new Color(0.45f, 0.50f, 0.60f));
                }
            }
            else
            {
                bool isOn = slot.IsToggledOn;
                if (slot.StateBadgeLabel != null)
                {
                    slot.StateBadgeLabel.Text = isOn ? "● ON" : "○ OFF";
                    slot.StateBadgeLabel.AddThemeColorOverride(
                        "font_color",
                        isOn ? new Color(0.35f, 1.0f, 0.55f) : new Color(0.52f, 0.55f, 0.65f)
                    );
                }

                if (slot.CardStyle != null)
                {
                    slot.CardStyle.BgColor = isOn
                        ? new Color(slot.ThemeColor.R * 0.16f, slot.ThemeColor.G * 0.16f, slot.ThemeColor.B * 0.18f, 0.94f)
                        : new Color(0.04f, 0.045f, 0.08f, 0.78f);

                    slot.CardStyle.BorderColor = isOn
                        ? slot.ThemeColor
                        : new Color(0.25f, 0.28f, 0.36f, 0.45f);

                    int bw = isOn ? 2 : 1;
                    slot.CardStyle.BorderWidthLeft = bw;
                    slot.CardStyle.BorderWidthTop = bw;
                    slot.CardStyle.BorderWidthRight = bw;
                    slot.CardStyle.BorderWidthBottom = bw;
                }

                if (slot.CountLabel != null)
                {
                    slot.CountLabel.AddThemeColorOverride(
                        "font_color",
                        isOn ? slot.ThemeColor : (slot.ThemeColor * 0.48f)
                    );
                }
            }
        }
    }

    private void ToggleFoundryPanel()
    {
        _isFoundryExpanded = !_isFoundryExpanded;

        _foundrySlideTween?.Kill();
        _foundrySlideTween = CreateTween().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out).SetParallel(true);

        float targetPanelX = _isFoundryExpanded ? FoundryOpenX : FoundryClosedX;
        float targetBtnX = _isFoundryExpanded ? FoundryOpenBtnX : FoundryClosedBtnX;

        if (_foundryMainPanel != null)
        {
            _foundrySlideTween.TweenProperty(_foundryMainPanel, "position:x", targetPanelX, 0.32f);
        }

        if (_toggleFoundryButton != null)
        {
            _toggleFoundryButton.Text = _isFoundryExpanded ? "◀ TUTUP [TAB]" : "⚙ PABRIK [TAB] ▶";
            _foundrySlideTween.TweenProperty(_toggleFoundryButton, "position:x", targetBtnX, 0.32f);
        }
    }

    private void ToggleAutoCraft(int recipeIndex)
    {
        if (recipeIndex < 0 || recipeIndex >= _recipes.Count) return;
        var rec = _recipes[recipeIndex];
        rec.AutoCraftEnabled = !rec.AutoCraftEnabled;
        RefreshFoundryGui();
    }

    private bool CanAffordRecipe(FactoryRecipe rec)
    {
        if (InfiniteDebugResources) return true;
        for (int r = 0; r < 5; r++)
        {
            if (_resourceStock[r] < rec.Costs[r]) return false;
        }
        return true;
    }

    public bool TryCraftRecipe(int recipeIndex)
    {
        if (recipeIndex < 0 || recipeIndex >= _recipes.Count) return false;
        var rec = _recipes[recipeIndex];
        if (!CanAffordRecipe(rec)) return false;

        if (!InfiniteDebugResources)
        {
            for (int r = 0; r < 5; r++)
            {
                _resourceStock[r] -= rec.Costs[r];
            }
        }
        else
        {
            for (int r = 0; r < 5; r++)
            {
                _resourceStock[r] = InfiniteStockValue;
            }
        }

        if (rec.IsCoreExpansion)
        {
            _playerCore.AddNextHoneycombCell();
        }
        else
        {
            AddSpecificSwarm(rec.Role, rec.OutputCount);
        }

        RefreshFoundryGui();
        return true;
    }

    private void RefreshFoundryGui()
    {
        string[] resSymbols = { "◆ Fe", "◆ Qz", "◆ Cb", "◆ Vx", "⚡ Ae" };
        for (int r = 0; r < 5; r++)
        {
            if (_resourceLabels[r] != null)
            {
                _resourceLabels[r].Text = InfiniteDebugResources
                    ? $"{resSymbols[r]}: ∞"
                    : $"{resSymbols[r]}: {_resourceStock[r]}";
            }
        }

        if (_laserTierInfoLabel != null)
        {
            int span = 1 + (MiningCount / 15);
            int nextThreshold = (MiningCount / 15 + 1) * 15;
            _laserTierInfoLabel.Text = $"⚡ LASER: Tier {span} (Lebar {span * 13}px) • {MiningCount}/{nextThreshold} Miner • DART: {DartNeedleCount}";
        }

        string[] shortNames = { "Fe", "Qz", "Cb", "Vx", "⚡Ae" };

        for (int i = 0; i < _recipes.Count; i++)
        {
            var rec = _recipes[i];
            bool affordable = CanAffordRecipe(rec);

            if (rec.CostLabel != null)
            {
                if (InfiniteDebugResources)
                {
                    List<string> costParts = new();
                    for (int r = 0; r < 5; r++)
                    {
                        if (rec.Costs[r] > 0)
                        {
                            costParts.Add($"{rec.Costs[r]} {shortNames[r]} (∞)");
                        }
                    }
                    rec.CostLabel.Text = string.Join("  ", costParts);
                    rec.CostLabel.AddThemeColorOverride("font_color", new Color(0.85f, 0.92f, 1.0f));
                }
                else
                {
                    List<string> costParts = new();
                    for (int r = 0; r < 5; r++)
                    {
                        if (rec.Costs[r] > 0)
                        {
                            if (_resourceStock[r] >= rec.Costs[r])
                            {
                                costParts.Add($"{rec.Costs[r]} {shortNames[r]}");
                            }
                            else
                            {
                                costParts.Add($"!({_resourceStock[r]}/{rec.Costs[r]} {shortNames[r]})");
                            }
                        }
                    }
                    rec.CostLabel.Text = string.Join("  ", costParts);
                    rec.CostLabel.AddThemeColorOverride("font_color", affordable ? new Color(0.85f, 0.92f, 1.0f) : new Color(1.0f, 0.42f, 0.42f));
                }
            }

            if (rec.CraftButton != null)
            {
                if (rec.IsCoreExpansion && _playerCore != null && _playerCore.IsMitosisActive)
                {
                    int pct = (int)(_playerCore.MitosisProgress * 100.0f);
                    rec.CraftButton.Text = $"MEMBELAH {pct}% ({_playerCore.MitosisRemainingTime:F1}s)";
                    rec.CraftButton.Disabled = true;
                    rec.CraftButton.AddThemeColorOverride("font_color", new Color(1.0f, 0.88f, 0.35f));

                    if (rec.ProgressBar != null)
                    {
                        rec.ProgressBar.Visible = true;
                        rec.ProgressBar.Value = pct;
                    }
                }
                else
                {
                    rec.CraftButton.Text = rec.IsCoreExpansion ? "BELAH CORE" : "RAKIT";
                    rec.CraftButton.Disabled = !affordable;
                    rec.CraftButton.AddThemeColorOverride("font_color", affordable ? rec.ThemeColor : new Color(0.55f, 0.50f, 0.55f));

                    if (rec.ProgressBar != null)
                    {
                        rec.ProgressBar.Visible = false;
                    }
                }
            }

            if (rec.AutoButton != null)
            {
                rec.AutoButton.Text = rec.AutoCraftEnabled ? "AUTO: ON" : "AUTO: OFF";
                rec.AutoButton.AddThemeColorOverride("font_color", rec.AutoCraftEnabled ? new Color(0.3f, 1.0f, 0.5f) : new Color(0.65f, 0.65f, 0.70f));
            }
        }

        RefreshTacticalDockGui();
    }

    private bool IsMouseOverFoundryGui()
    {
        Vector2 mPos = GetViewport().GetMousePosition();

        if (_foundryMainPanel != null && _foundryMainPanel.Visible)
        {
            if (_foundryMainPanel.GetGlobalRect().HasPoint(mPos))
            {
                return true;
            }
        }

        if (_tacticalDockPanel != null && _tacticalDockPanel.Visible)
        {
            if (_tacticalDockPanel.GetGlobalRect().HasPoint(mPos))
            {
                return true;
            }
        }

        return false;
    }

    private void IncrementRoleCounter(SwarmRole role, int delta)
    {
        switch (role)
        {
            case SwarmRole.DartNeedle:
                DartNeedleCount = Math.Max(0, DartNeedleCount + delta);
                break;
            case SwarmRole.GigaShuriken:
                GigaShurikenCount = Math.Max(0, GigaShurikenCount + delta);
                break;
            case SwarmRole.CrescentBoomerang:
                CrescentBoomerangCount = Math.Max(0, CrescentBoomerangCount + delta);
                break;
            case SwarmRole.DrillDrone:
                DrillDroneCount = Math.Max(0, DrillDroneCount + delta);
                break;
            case SwarmRole.Guardian:
                GuardianCount = Math.Max(0, GuardianCount + delta);
                break;
            case SwarmRole.ScannerDrone:
                ScannerCount = Math.Max(0, ScannerCount + delta);
                break;
            case SwarmRole.MiningDroid:
                MiningCount = Math.Max(0, MiningCount + delta);
                break;
        }
    }

    public void AddSpecificSwarm(SwarmRole role, int count)
    {
        Vector2 corePos = _playerCore.GlobalPosition;
        Color color = role switch
        {
            SwarmRole.DartNeedle => DartColor,
            SwarmRole.GigaShuriken => ShurikenColor,
            SwarmRole.DrillDrone => DrillColor,
            SwarmRole.CrescentBoomerang => CrescentColor,
            SwarmRole.Guardian => GuardianColor,
            SwarmRole.ScannerDrone => ScannerColor,
            SwarmRole.MiningDroid => MiningColor,
            _ => DartColor
        };

        float targetScale = role switch
        {
            SwarmRole.Guardian => 0.72f,
            SwarmRole.ScannerDrone => 0.60f,
            SwarmRole.MiningDroid => 0.62f,
            _ => 0.55f
        };

        int bayCount = Math.Max(1, _playerCore.CoreCellCount);
        int remaining = count;
        int spawnIdx = 0;

        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
        {
            if (remaining <= 0 || swarm.State != SwarmState.Dead) return;

            int cellIdx = spawnIdx % bayCount;
            Vector2 bayLocal = _playerCore.GetCellOffset(cellIdx);
            Vector2 bayWorld = corePos + bayLocal.Rotated(_playerCore.Rotation);

            _playerCore.TriggerSwarmFabrication(role, color, cellIdx);

            float orbitDist = (role == SwarmRole.Guardian)
                ? 78.0f + (GD.Randf() - 0.5f) * 16.0f
                : 32.0f + GD.Randf() * 68.0f;
            float orbitSpeed = (GD.Randf() > 0.45f ? 1.0f : -1.0f) * (0.8f + GD.Randf() * 0.7f);

            float baseAngle = bayLocal.LengthSquared() > 0.01f
                ? bayLocal.Angle() + _playerCore.Rotation
                : (GD.Randf() * Mathf.Tau);
            float ejectAngle = baseAngle + (GD.Randf() - 0.5f) * 0.85f;
            Vector2 ejectDir = new Vector2(MathF.Cos(ejectAngle), MathF.Sin(ejectAngle));

            pos.Value = bayWorld + ejectDir * 6.0f;
            vel.Value = _playerCore.Velocity * 0.5f + ejectDir * (380.0f + GD.Randf() * 140.0f);
            swarm = new SwarmUnitComponent(role, 0, orbitDist, orbitSpeed);
            vis = new VisualComponent(color * 2.5f, targetScale * 0.42f);

            IncrementRoleCounter(role, 1);
            _entityCount++;
            remaining--;
            spawnIdx++;
        });

        for (int i = 0; i < remaining; i++)
        {
            int cellIdx = spawnIdx % bayCount;
            Vector2 bayLocal = _playerCore.GetCellOffset(cellIdx);
            Vector2 bayWorld = corePos + bayLocal.Rotated(_playerCore.Rotation);

            _playerCore.TriggerSwarmFabrication(role, color, cellIdx);

            float orbitDist = (role == SwarmRole.Guardian)
                ? 78.0f + (GD.Randf() - 0.5f) * 16.0f
                : 32.0f + GD.Randf() * 68.0f;
            float orbitSpeed = (GD.Randf() > 0.45f ? 1.0f : -1.0f) * (0.8f + GD.Randf() * 0.7f);

            float baseAngle = bayLocal.LengthSquared() > 0.01f
                ? bayLocal.Angle() + _playerCore.Rotation
                : (GD.Randf() * Mathf.Tau);
            float ejectAngle = baseAngle + (GD.Randf() - 0.5f) * 0.85f;
            Vector2 ejectDir = new Vector2(MathF.Cos(ejectAngle), MathF.Sin(ejectAngle));

            Vector2 spawnPos = bayWorld + ejectDir * 6.0f;
            Vector2 spawnVel = _playerCore.Velocity * 0.5f + ejectDir * (380.0f + GD.Randf() * 140.0f);

            IncrementRoleCounter(role, 1);

            _world.Create(
                new PositionComponent(spawnPos),
                new VelocityComponent(spawnVel),
                new SwarmUnitComponent(role, 0, orbitDist, orbitSpeed),
                new VisualComponent(color * 2.5f, targetScale * 0.42f)
            );

            _entityCount++;
            spawnIdx++;
        }

        RefreshFoundryGui();
    }

    public void AddEnemySwarm(SwarmRole role, int count)
    {
        if (_enemyCore == null || !IsInstanceValid(_enemyCore)) return;
        Vector2 corePos = _enemyCore.GlobalPosition;
        Color color = role switch
        {
            SwarmRole.MiningDroid => new Color(3.6f, 1.8f, 0.4f, 1.0f),
            SwarmRole.Guardian => new Color(2.6f, 0.45f, 3.2f, 1.0f),
            SwarmRole.DartNeedle => new Color(3.5f, 0.45f, 0.95f, 1.0f),
            SwarmRole.DrillDrone => new Color(4.2f, 0.6f, 0.2f, 1.0f),
            SwarmRole.GigaShuriken => new Color(3.8f, 0.3f, 1.8f, 1.0f),
            SwarmRole.CrescentBoomerang => new Color(0.4f, 2.8f, 1.2f, 1.0f),
            _ => new Color(3.5f, 0.45f, 0.95f, 1.0f)
        };

        float targetScale = role switch
        {
            SwarmRole.Guardian => 0.72f,
            SwarmRole.ScannerDrone => 0.60f,
            SwarmRole.MiningDroid => 0.62f,
            _ => 0.55f
        };

        int remaining = count;
        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
        {
            if (remaining <= 0 || swarm.State != SwarmState.Dead) return;

            float orbitDist = role switch
            {
                SwarmRole.Guardian => 78.0f + (GD.Randf() - 0.5f) * 8.0f,
                SwarmRole.MiningDroid => 58.0f + (GD.Randf() - 0.5f) * 6.0f,
                _ => 68.0f + (GD.Randf() - 0.5f) * 10.0f
            };
            float orbitSpeed = 1.75f + (GD.Randf() - 0.5f) * 0.25f;

            float angle = GD.Randf() * Mathf.Tau;
            Vector2 offset = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * orbitDist;

            pos.Value = corePos + offset;
            vel.Value = _enemyCore.Velocity;
            swarm = new SwarmUnitComponent(role, 1, orbitDist, orbitSpeed);
            vis = new VisualComponent(color, targetScale);

            _entityCount++;
            remaining--;
        });

        for (int i = 0; i < remaining; i++)
        {
            float orbitDist = role switch
            {
                SwarmRole.Guardian => 78.0f + (GD.Randf() - 0.5f) * 8.0f,
                SwarmRole.MiningDroid => 58.0f + (GD.Randf() - 0.5f) * 6.0f,
                _ => 68.0f + (GD.Randf() - 0.5f) * 10.0f
            };
            float orbitSpeed = 1.75f + (GD.Randf() - 0.5f) * 0.25f;

            float angle = GD.Randf() * Mathf.Tau;
            Vector2 offset = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * orbitDist;

            var unit = new SwarmUnitComponent(role, 1, orbitDist, orbitSpeed);
            _world.Create(
                new PositionComponent(corePos + offset),
                new VelocityComponent(_enemyCore.Velocity),
                unit,
                new VisualComponent(color, targetScale)
            );
            _entityCount++;
        }
    }

    public void CollectEnemyMinerPositions(List<Vector2> outputList)
    {
        outputList.Clear();
        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref SwarmUnitComponent swarm) =>
        {
            if (swarm.OwnerId == 1 && (swarm.State == SwarmState.Orbiting || swarm.State == SwarmState.Returning) && swarm.Role == SwarmRole.MiningDroid)
            {
                outputList.Add(pos.Value);
            }
        });
    }

    /// <summary>
    /// Mengumpulkan posisi serpihan kristal resource (IsCrystalMatter) yang tampak di sekitar originPos
    /// agar AI dapat melihat dan menghampiri drop resource sebagaimana pemain melihatnya di layar.
    /// </summary>
    public void CollectNearbyCrystalPositions(Vector2 originPos, float maxRadius, List<Vector2> outputList)
    {
        outputList.Clear();
        float maxRadSq = maxRadius * maxRadius;
        for (int i = 0; i < _miningDebris.Count; i++)
        {
            var p = _miningDebris[i];
            if (!p.IsCrystalMatter || p.Life <= 0.25f) continue;
            if (originPos.DistanceSquaredTo(p.Position) <= maxRadSq)
            {
                outputList.Add(p.Position);
            }
        }
    }

    public void SpawnEnemySectorAttack(Vector2 attackOriginDirPos, int enemyCount = 50)
    {
        Vector2 corePos = _playerCore.GlobalPosition;
        Vector2 toAttack = attackOriginDirPos - corePos;
        Vector2 dirFromCore = toAttack.LengthSquared() > 10.0f ? toAttack.Normalized() : Vector2.Left;
        Vector2 tangent = new Vector2(-dirFromCore.Y, dirFromCore.X);

        Color enemyColor = new Color(3.4f, 0.35f, 0.9f, 1.0f);
        int remaining = enemyCount;

        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
        {
            if (remaining <= 0 || swarm.State != SwarmState.Dead) return;

            float dist = 360.0f + GD.Randf() * 130.0f;
            float lateral = (GD.Randf() - 0.5f) * 140.0f;
            Vector2 spawnPos = corePos + dirFromCore * dist + tangent * lateral;
            Vector2 moveDir = (corePos - spawnPos).Normalized().Rotated((GD.Randf() - 0.5f) * 0.14f);

            pos.Value = spawnPos;
            vel.Value = moveDir * (260.0f + GD.Randf() * 110.0f);
            swarm = new SwarmUnitComponent(SwarmRole.DartNeedle, ownerId: 1, orbitDist: 0.0f, orbitSpd: 0.0f)
            {
                State = SwarmState.Launched,
                MaxLifeTime = 3.2f,
                TargetDirection = moveDir
            };
            vis = new VisualComponent(enemyColor, 0.95f);
            remaining--;
        });

        for (int i = 0; i < remaining; i++)
        {
            float dist = 360.0f + GD.Randf() * 130.0f;
            float lateral = (GD.Randf() - 0.5f) * 140.0f;
            Vector2 spawnPos = corePos + dirFromCore * dist + tangent * lateral;
            Vector2 moveDir = (corePos - spawnPos).Normalized().Rotated((GD.Randf() - 0.5f) * 0.14f);

            var unit = new SwarmUnitComponent(SwarmRole.DartNeedle, ownerId: 1, orbitDist: 0.0f, orbitSpd: 0.0f)
            {
                State = SwarmState.Launched,
                MaxLifeTime = 3.2f,
                TargetDirection = moveDir
            };

            _world.Create(
                new PositionComponent(spawnPos),
                new VelocityComponent(moveDir * (260.0f + GD.Randf() * 110.0f)),
                unit,
                new VisualComponent(enemyColor, 0.95f)
            );
        }
    }

    public void SpawnInitialEnemySwarm(int count = 60)
    {
        if (_enemyCore == null) return;
        Vector2 enemyCorePos = _enemyCore.GlobalPosition;
        Color enemyColor = new Color(3.4f, 0.35f, 0.9f, 1.0f);
        int remaining = count;

        // Daur ulang entitas Dead terlebih dahulu agar memori ECS tidak bocor
        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
        {
            if (remaining <= 0 || swarm.State != SwarmState.Dead) return;

            float orbitDist = 55.0f + GD.Randf() * 75.0f;
            float orbitSpd = (GD.Randf() > 0.5f ? 1.0f : -1.0f) * (1.2f + GD.Randf() * 1.8f);
            float angle = GD.Randf() * Mathf.Tau;
            Vector2 offset = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * orbitDist;

            pos.Value = enemyCorePos + offset;
            vel.Value = Vector2.Zero;
            swarm = new SwarmUnitComponent(SwarmRole.DartNeedle, ownerId: 1, orbitDist, orbitSpd)
            {
                State = SwarmState.Orbiting,
                MaxLifeTime = 3.5f
            };
            vis = new VisualComponent(enemyColor, 0.70f);
            remaining--;
        });

        for (int i = 0; i < remaining; i++)
        {
            float orbitDist = 55.0f + GD.Randf() * 75.0f;
            float orbitSpd = (GD.Randf() > 0.5f ? 1.0f : -1.0f) * (1.2f + GD.Randf() * 1.8f);
            float angle = GD.Randf() * Mathf.Tau;
            Vector2 offset = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * orbitDist;

            var pos = new PositionComponent(enemyCorePos + offset);
            var vel = new VelocityComponent(Vector2.Zero);
            var unit = new SwarmUnitComponent(SwarmRole.DartNeedle, ownerId: 1, orbitDist, orbitSpd)
            {
                State = SwarmState.Orbiting,
                MaxLifeTime = 3.5f
            };
            var vis = new VisualComponent(enemyColor, 0.70f);

            _world.Create(pos, vel, unit, vis);
        }
    }

    public void FirePlayerDuelStream(Vector2 targetPos, int count = 1)
    {
        int fired = 0;
        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm) =>
        {
            if (fired >= count) return;
            if (swarm.OwnerId != 0 || swarm.State != SwarmState.Orbiting) return;
            if (swarm.Role != SwarmRole.DartNeedle) return;

            Vector2 toTarget = targetPos - pos.Value;
            if (toTarget.LengthSquared() < 1.0f) return;

            float spread = (GD.Randf() - 0.5f) * 0.08f;
            Vector2 dir = toTarget.Normalized().Rotated(spread);

            swarm.State = SwarmState.Launched;
            swarm.FormationIndex = -1;
            swarm.ExhaustTriggerTime = -1.0f;
            swarm.MaxHealth = 1;
            swarm.Health = 1;
            swarm.TargetDirection = dir;
            swarm.TargetPosition = targetPos;
            swarm.LifeTime = 0.0f;
            swarm.MaxLifeTime = 2.0f;
            vel.Value = dir * (_boidSystem.BulletMaxSpeed * (0.85f + GD.Randf() * 0.15f));
            fired++;
        });
    }

    public void FireEnemyDuelStream(Vector2 targetPos, int count = 1)
    {
        int fired = 0;
        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm) =>
        {
            if (fired >= count) return;
            if (swarm.OwnerId == 0 || swarm.State != SwarmState.Orbiting) return;

            Vector2 toTarget = targetPos - pos.Value;
            if (toTarget.LengthSquared() < 1.0f) return;

            // Tembakan lurus berkecepatan agak tinggi (720 px/s) untuk skenario uji coba menembus Giga Drill
            Vector2 dir = toTarget.Normalized();

            swarm.State = SwarmState.Launched;
            swarm.MaxHealth = 1;
            swarm.Health = 1;
            swarm.TargetDirection = dir;
            swarm.TargetPosition = targetPos;
            swarm.LifeTime = 0.0f;
            swarm.MaxLifeTime = 3.5f;
            vel.Value = dir * 720.0f;
            fired++;
        });

        if (fired < count && _enemyCore != null)
        {
            SpawnInitialEnemySwarm(6);
        }
    }

    public void TriggerClashAnimation(Vector2 pos, bool dropScrap)
    {
        // 1. Animasi percikan partikel kinetik (Cyan + Magenta energy flash)
        int sparkCount = 9;
        for (int i = 0; i < sparkCount; i++)
        {
            float angle = GD.Randf() * Mathf.Tau;
            float speed = 240.0f + GD.Randf() * 320.0f;
            Vector2 sVel = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * speed;
            Color sCol = (i % 2 == 0)
                ? new Color(0.9f, 2.5f, 3.2f, 1.0f)
                : new Color(3.5f, 0.4f, 1.8f, 1.0f);

            _clashSparks.Add(new ClashSparkParticle
            {
                Position = pos,
                Velocity = sVel,
                Color = sCol,
                Life = 0.22f,
                MaxLife = 0.22f,
                Size = 3.8f
            });
        }

        // 2. Kepingan Pecahan (Salvageable Scrap Debris yang bisa didekati untuk daur ulang)
        if (dropScrap && _miningDebris.Count < 300)
        {
            _miningDebris.Add(new MiningDebrisParticle
            {
                Position = pos + new Vector2((GD.Randf() - 0.5f) * 8.0f, (GD.Randf() - 0.5f) * 8.0f),
                Velocity = new Vector2((GD.Randf() - 0.5f) * 60.0f, (GD.Randf() - 0.5f) * 60.0f),
                Color = new Color(2.6f, 1.9f, 0.7f, 0.95f),
                Life = 24.0f,
                MaxLife = 24.0f,
                Size = 4.2f,
                IsCrystalMatter = true,
                MatterType = ResourceType.Ferrite
            });
        }

        // 3. Gelombang kejut mikro pada grid kosmik
        EmitSignal(SignalName.SwarmClashImpulse, pos, 380.0f, 65.0f);
    }

    private void UpdateClashSparks(float dt)
    {
        if (_clashSparks.Count == 0) return;
        for (int i = _clashSparks.Count - 1; i >= 0; i--)
        {
            var sp = _clashSparks[i];
            sp.Life -= dt;
            if (sp.Life <= 0.0f)
            {
                _clashSparks.RemoveAt(i);
            }
            else
            {
                sp.Position += sp.Velocity * dt;
                sp.Velocity *= MathF.Pow(0.85f, dt * 60.0f);
                _clashSparks[i] = sp;
            }
        }
        QueueRedraw();
    }

    private void SpawnOneOrbitingDartNearPlayer()
    {
        if (_playerCore == null) return;
        float orbitDist = 55.0f + GD.Randf() * 75.0f;
        float orbitSpd = (GD.Randf() > 0.5f ? 1.0f : -1.0f) * (1.2f + GD.Randf() * 1.8f);
        float angle = GD.Randf() * Mathf.Tau;
        Vector2 spawnPos = _playerCore.GlobalPosition + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 30.0f;

        var pos = new PositionComponent(spawnPos);
        var vel = new VelocityComponent(Vector2.Zero);
        var unit = new SwarmUnitComponent(SwarmRole.DartNeedle, ownerId: 0, orbitDist, orbitSpd);
        var vis = new VisualComponent(DartColor, 0.55f);
        _world.Create(pos, vel, unit, vis);
    }

    public override void _PhysicsProcess(double delta)
    {
        try
        {
            float dt = (float)delta;
            Vector2 corePos = _playerCore.GlobalPosition;
            Vector2 coreVel = _playerCore.Velocity;
            Vector2 mousePos = GetGlobalMousePosition();

            Asteroid.CurrentScannerCount = ScannerCount;

            // Proses Auto-Craft di Pabrik setiap 0.75 detik
            _autoCraftTimer += dt;
            if (_autoCraftTimer >= 0.75f)
            {
                _autoCraftTimer = 0.0f;
                for (int i = 0; i < _recipes.Count; i++)
                {
                    if (_recipes[i].AutoCraftEnabled)
                    {
                        if (_recipes[i].IsCoreExpansion && _playerCore != null && _playerCore.IsMitosisActive)
                        {
                            continue;
                        }
                        TryCraftRecipe(i);
                    }
                }
            }

            if (_playerCore != null && _playerCore.IsMitosisActive)
            {
                RefreshFoundryGui();
            }

            if (_fireCooldown > 0.0f) _fireCooldown -= dt;

            // =========================================================================
            // AKSI KLIK KIRI [LMB] BERDASARKAN MULTI-TOGGLE TACTICAL DOCK [1-5]
            // =========================================================================
            bool isLeftClick = Input.IsMouseButtonPressed(MouseButton.Left) && !IsMouseOverFoundryGui();
            bool isMiningToggled = _roleToggledOn[(int)SwarmRole.MiningDroid];

            if (isLeftClick && !_isChargingDrill && !_isGigaDrillActive)
            {
                // 1. Jika slot [1] Mining Droid sedang ON, nyalakan Heavy Mining Laser
                if (isMiningToggled && MiningCount > 0)
                {
                    UpdateMiningLaser(dt, corePos, mousePos);
                }
                else
                {
                    DeactivateMiningLaser(dt);
                }

                // 2. Tembakkan unit swarm tempur yang slot toggle-nya ([2]-[5]) sedang ON
                if (_fireCooldown <= 0.0f)
                {
                    FireSwarmStream(mousePos, count: 2);
                    _fireCooldown = StreamFireInterval;
                }
            }
            else
            {
                DeactivateMiningLaser(dt);
            }

            UpdateMiningDebris(dt, corePos);
            HandleGigaDrillCharge(dt, corePos, coreVel, mousePos);
            UpdateActiveGigaDrill(dt);
            UpdateDartBoost(dt, corePos, coreVel, mousePos);
            UpdateExhaustDrones(dt);
            CheckAsteroidCollisions(dt);

            // Tembakan musuh berkala untuk menguji perisai pertahanan swarm (autofire swarm pemain dimatikan)
            if (IsEnemyFiring && _enemyCore != null && IsInstanceValid(_enemyCore))
            {
                _duelFireTimer += dt;
                if (_duelFireTimer >= EnemyFireInterval)
                {
                    _duelFireTimer = 0.0f;
                    FireEnemyDuelStream(corePos, 1);
                }
            }

            Vector2 enemyCorePos = (_enemyCore != null && IsInstanceValid(_enemyCore))
                ? _enemyCore.GlobalPosition
                : corePos + new Vector2(1150.0f, 0.0f);

            float playerCoreRadius = _playerCore?.CoreCollisionRadius ?? 26.0f;
            float enemyCoreRadius = (_enemyCore != null && IsInstanceValid(_enemyCore))
                ? _enemyCore.CoreCollisionRadius
                : 42.0f;
            Vector2 enemyCoreVel = (_enemyCore != null && IsInstanceValid(_enemyCore))
                ? _enemyCore.Velocity
                : Vector2.Zero;
            Vector2 enemyAimDir = (_enemyController != null && _enemyController.IsMiningLaserActive)
                ? _enemyController.LaserAimDir
                : Vector2.Zero;

            _boidSystem.Update(
                dt,
                corePos,
                coreVel,
                enemyCorePos,
                mousePos,
                _isBoostActive,
                _isGigaDrillActive,
                playerCoreRadius,
                enemyCoreRadius,
                enemyCoreVel,
                enemyAimDir
            );

            // Eksekusi animasi benturan (Clash FX) & spawn kepingan scrap
            for (int i = 0; i < _boidSystem.ClashEvents.Count; i++)
            {
                var ev = _boidSystem.ClashEvents[i];
                TriggerClashAnimation(ev.Position, ev.DropScrap);
            }

            if (_boidSystem.EnemyCoreHitsThisFrame > 0 && _enemyCore != null && IsInstanceValid(_enemyCore))
            {
                Vector2 hitDir = _boidSystem.LastEnemyHitVelocity.LengthSquared() > 0.01f
                    ? _boidSystem.LastEnemyHitVelocity.Normalized()
                    : Vector2.Right;
                _enemyCore.TakeDamage(_boidSystem.EnemyCoreHitsThisFrame * 40.0f, hitDir);
            }

            if (_enemyController != null && _enemyController.IsMiningLaserActive)
            {
                QueueRedraw();
            }

            if (_boidSystem.PlayerCoreHitsThisFrame > 0 && _playerCore != null && IsInstanceValid(_playerCore))
            {
                Vector2 hitDir = _boidSystem.LastPlayerHitVelocity.LengthSquared() > 0.01f
                    ? _boidSystem.LastPlayerHitVelocity.Normalized()
                    : Vector2.Right;
                _playerCore.TakeDamage(_boidSystem.PlayerCoreHitsThisFrame * 40.0f, hitDir);
            }

            UpdateClashSparks(dt);

            // Sinkronisasi jumlah swarm hidup milik pemain dan musuh secara real-time dalam 1 pass render (100% bebas desync!)
            int liveDart = 0, liveShuriken = 0, liveBoomerang = 0, liveDrill = 0, liveGuardian = 0, liveScanner = 0, liveMining = 0;
            int eMiners = 0, eGuardians = 0, eDarts = 0, eShurikens = 0, eDrills = 0, eBoomerangs = 0;
            _renderer.ResetCounters();
            _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
            {
                if (swarm.State == SwarmState.Dead) return;
                bool isBehindCore = swarm.State == SwarmState.Orbiting && !_isBoostActive && swarm.OrbitDepth < 0.0f;
                _renderer.SetInstance((int)swarm.Role, pos.Value, vel.Value, swarm.SpinAngle, vis.Color, vis.Scale, isBehindCore);

                if (swarm.OwnerId == 0)
                {
                    switch (swarm.Role)
                    {
                        case SwarmRole.DartNeedle: liveDart++; break;
                        case SwarmRole.GigaShuriken: liveShuriken++; break;
                        case SwarmRole.CrescentBoomerang: liveBoomerang++; break;
                        case SwarmRole.DrillDrone: liveDrill++; break;
                        case SwarmRole.Guardian: liveGuardian++; break;
                        case SwarmRole.ScannerDrone: liveScanner++; break;
                        case SwarmRole.MiningDroid: liveMining++; break;
                    }
                }
                else if (swarm.OwnerId == 1)
                {
                    switch (swarm.Role)
                    {
                        case SwarmRole.MiningDroid: eMiners++; break;
                        case SwarmRole.Guardian: eGuardians++; break;
                        case SwarmRole.DartNeedle: eDarts++; break;
                        case SwarmRole.GigaShuriken: eShurikens++; break;
                        case SwarmRole.DrillDrone: eDrills++; break;
                        case SwarmRole.CrescentBoomerang: eBoomerangs++; break;
                    }
                }
            });
            _renderer.FlushVisibleCounts();
            _enemyController?.SetUnitCounts(eMiners, eGuardians, eDarts, eShurikens, eDrills, eBoomerangs);

            int liveTotal = liveDart + liveShuriken + liveBoomerang + liveDrill + liveGuardian + liveScanner + liveMining;
            if (liveTotal != _entityCount
                || liveDart != DartNeedleCount
                || liveShuriken != GigaShurikenCount
                || liveBoomerang != CrescentBoomerangCount
                || liveDrill != DrillDroneCount
                || liveGuardian != GuardianCount
                || liveScanner != ScannerCount
                || liveMining != MiningCount)
            {
                _entityCount = liveTotal;
                DartNeedleCount = liveDart;
                GigaShurikenCount = liveShuriken;
                CrescentBoomerangCount = liveBoomerang;
                DrillDroneCount = liveDrill;
                GuardianCount = liveGuardian;
                ScannerCount = liveScanner;
                MiningCount = liveMining;
                RefreshFoundryGui();
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SwarmSimulation._PhysicsProcess] {ex}");
        }
    }

    private void DeactivateMiningLaser(float dt)
    {
        _laserPrimingTimer = 0.0f;
        _laserFocusWarmup = MathF.Max(0.0f, _laserFocusWarmup - dt * 8.0f);
        _isMiningEnergyAsteroid = false;
        _plasmaSiphonWisps.Clear();
        if (_isMiningLaserActive)
        {
            _isMiningLaserActive = false;
            _laserActiveHitBoxes.Clear();
            QueueRedraw();
        }
    }

    // =========================================================================
    // SISTEM SACRIFICIAL DART PLASMA BOOST & KUBAH PARABOLA DEPAN [SPACE]
    // =========================================================================

    private void UpdateDartBoost(float dt, Vector2 corePos, Vector2 _coreVel, Vector2 mousePos)
    {
        bool wantBoost = Input.IsKeyPressed(Key.Space) && !_isChargingDrill && !_isGigaDrillActive;
        if (!wantBoost)
        {
            if (_isBoostActive)
            {
                Vector2 coreVel = _playerCore.Velocity;
                if (coreVel.Length() > _playerCore.MaxSpeed * 0.40f)
                {
                    ActivateDartBrake(coreVel);
                }
                DeactivateDartBoost();
            }
            else if (_isBrakeActive)
            {
                UpdateDartBrake(dt, corePos, _playerCore.Velocity);
            }
            return;
        }

        // Jika sedang ngerem lalu user tekan SPACE lagi, langsung batalkan rem dan lanjut boost
        if (_isBrakeActive)
        {
            DeactivateDartBrake();
        }

        Vector2 toMouse = mousePos - corePos;
        Vector2 aimDir = toMouse.LengthSquared() > 1.0f ? toMouse.Normalized() : Vector2.Right;
        Vector2 backDir = -aimDir;
        Vector2 tangent = new Vector2(-aimDir.Y, aimDir.X);

        // 1. Periksa slot pendorong yang sedang terisi oleh DartNeedle (maksimal 10 slot cincin ruang bakar)
        Array.Clear(_boostSlotOccupied, 0, MaxBoostThrusterSlots);
        int activeCount = 0;

        _world.Query(in _allSwarmQuery, (ref SwarmUnitComponent swarm) =>
        {
            if (swarm.OwnerId == 0 && swarm.Role == SwarmRole.DartNeedle && swarm.State == SwarmState.BoostThruster)
            {
                int slot = swarm.FormationIndex;
                if (slot >= 0 && slot < MaxBoostThrusterSlots && !_boostSlotOccupied[slot])
                {
                    _boostSlotOccupied[slot] = true;
                    activeCount++;
                }
                else
                {
                    swarm.FormationIndex = -1;
                }
            }
        });

        // Rapikan jika ada Dart yang belum punya indeks slot valid
        int assignCursor = 0;
        _world.Query(in _allSwarmQuery, (ref SwarmUnitComponent swarm) =>
        {
            if (swarm.OwnerId == 0 && swarm.Role == SwarmRole.DartNeedle && swarm.State == SwarmState.BoostThruster && swarm.FormationIndex == -1)
            {
                while (assignCursor < MaxBoostThrusterSlots && _boostSlotOccupied[assignCursor])
                {
                    assignCursor++;
                }
                if (assignCursor < MaxBoostThrusterSlots)
                {
                    swarm.FormationIndex = assignCursor;
                    _boostSlotOccupied[assignCursor] = true;
                    activeCount++;
                }
                else
                {
                    swarm.State = SwarmState.Orbiting;
                }
            }
        });

        // 2. Tarik DartNeedle cadangan dari Kubah Parabola depan (Orbiting/Returning) untuk mengisi slot nozzle kosong
        if (activeCount < MaxBoostThrusterSlots)
        {
            int nextSlot = 0;
            _world.Query(in _allSwarmQuery, (ref SwarmUnitComponent swarm) =>
            {
                if (activeCount >= MaxBoostThrusterSlots) return;
                if (swarm.OwnerId == 0
                    && swarm.Role == SwarmRole.DartNeedle
                    && (swarm.State == SwarmState.Orbiting || swarm.State == SwarmState.Returning))
                {
                    while (nextSlot < MaxBoostThrusterSlots && _boostSlotOccupied[nextSlot])
                    {
                        nextSlot++;
                    }
                    if (nextSlot < MaxBoostThrusterSlots)
                    {
                        swarm.State = SwarmState.BoostThruster;
                        swarm.FormationIndex = nextSlot;
                        swarm.LifeTime = 0.0f;
                        _boostSlotOccupied[nextSlot] = true;
                        activeCount++;
                    }
                }
            });
        }

        // Jika sudah tidak ada DartNeedle yang tersisa sama sekali (habis dikorbankan), hentikan boost
        if (activeCount == 0)
        {
            if (_isBoostActive)
            {
                DeactivateDartBoost();
            }
            return;
        }

        _isBoostActive = true;
        _activeBoostDartCount = activeCount;
        BoostAimDirection = aimDir;

        // 3. Masa Ignition Priming Singkat (~0.42s) lalu langsung Full Power Sacrificial Thrust
        _boostPrimingDuration = 0.42f;
        _boostPrimingTimer += dt;
        float primingProgress = Mathf.Clamp(_boostPrimingTimer / _boostPrimingDuration, 0.0f, 1.0f);
        _isBoostFullPower = (_boostPrimingTimer >= _boostPrimingDuration);

        _boostPulseTimer += dt * (22.0f + primingProgress * 36.0f);
        _boostSacrificeFlash = MathF.Max(0.0f, _boostSacrificeFlash - dt * 6.5f);

        float thrusterFillRatio = Mathf.Clamp((float)activeCount / MaxBoostThrusterSlots, 0.40f, 1.0f);
        Vector2 shipVel = _playerCore.Velocity;
        float currentSpeed = shipVel.Length();
        float speedNorm = Mathf.Clamp(
            (currentSpeed - _playerCore.MaxSpeed * 0.45f) / MathF.Max(1.0f, _playerCore.BoostMaxSpeed - _playerCore.MaxSpeed * 0.45f),
            0.0f,
            1.0f
        );

        if (_isBoostFullPower)
        {
            float boostAccel = 7200.0f * thrusterFillRatio;
            _playerCore.IsBoosting = true;
            _playerCore.BoostThrustVector = aimDir * boostAccel;
            BoostPowerRatio = Mathf.Clamp(0.62f + speedNorm * 0.38f, 0.62f, 1.0f);
        }
        else
        {
            float warmupAccel = 2600.0f * primingProgress * thrusterFillRatio;
            _playerCore.IsBoosting = true;
            _playerCore.BoostThrustVector = aimDir * warmupAccel;
            BoostPowerRatio = Mathf.Lerp(0.22f, 0.62f, primingProgress);
        }

        float coreRadius = 20.0f + MathF.Sqrt(Math.Max(1, _playerCore.CoreCellCount)) * 8.5f;
        float nozzleRingRadius = coreRadius + 16.0f;
        Vector2 focalChamberPos = corePos + backDir * (nozzleRingRadius + 24.0f);
        BoostNozzlePosition = focalChamberPos;

        // =========================================================================
        // 4. LOGIKA PENGORBANAN DART (SACRIFICIAL BURN & AUTO-REFILL)
        //    Dart di nozzle terbakar habis menjadi propelan plasma seiring laju kapal!
        // =========================================================================
        float burnRatePerSec = _isBoostFullPower
            ? (3.2f + speedNorm * speedNorm * 10.8f) // ~3.2 Dart/s saat awal -> ~14 Dart/s pada top speed
            : (1.5f * primingProgress);

        _boostSacrificeAccumulator += burnRatePerSec * dt;
        if (_boostSacrificeAccumulator >= 1.0f)
        {
            _boostSacrificeAccumulator -= 1.0f;
            SacrificeOneThrusterDart(focalChamberPos, aimDir, BoostPowerRatio);
        }

        // =========================================================================
        // 5. FORMASI NOZZLE BELAKANG (10 DART MENYATU KE FOCAL PLASMA CHAMBER)
        // =========================================================================
        _activeThrusterJets.Clear();
        _activeThrusterDartPositions.Clear();

        float nozzleArcSpan = 0.68f * Mathf.Pi; // ~122 derajat cincin buritan yang rapi & terfokus
        float baseBackAngle = backDir.Angle();
        float pulseTime = _boostPulseTimer;
        float power = BoostPowerRatio;

        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
        {
            if (swarm.OwnerId != 0 || swarm.Role != SwarmRole.DartNeedle || swarm.State != SwarmState.BoostThruster)
                return;

            int slot = Math.Clamp(swarm.FormationIndex, 0, MaxBoostThrusterSlots - 1);
            swarm.LifeTime += dt;

            float arcT = (MaxBoostThrusterSlots > 1)
                ? ((float)slot / (MaxBoostThrusterSlots - 1) - 0.5f)
                : 0.0f;
            float angle = baseBackAngle + arcT * nozzleArcSpan;

            float microVib = MathF.Sin(pulseTime * 3.5f + slot * 1.9f) * (1.2f + power * 1.8f);
            Vector2 slotWorldPos = corePos + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (nozzleRingRadius + microVib);

            // Arah feeder konvergen dari Dart menuju Focal Chamber di belakang Core
            Vector2 toFocal = focalChamberPos - slotWorldPos;
            Vector2 feederDir = toFocal.LengthSquared() > 0.01f ? toFocal.Normalized() : backDir;

            float lockProgress = Mathf.Clamp(swarm.LifeTime / 0.12f, 0.0f, 1.0f);
            Vector2 toSlot = slotWorldPos - pos.Value;
            Vector2 targetVel = shipVel + toSlot * Mathf.Lerp(22.0f, 44.0f, lockProgress);

            vel.Value = vel.Value.Lerp(targetVel, MathF.Min(1.0f, 26.0f * dt));
            pos.Value = pos.Value.Lerp(slotWorldPos, MathF.Min(1.0f, (16.0f + 22.0f * lockProgress) * dt));

            // Orientasi: Moncong Dart menghadap ke luar/depan, ekor memancarkan plasma ke Focal Chamber
            float targetSpin = (-feederDir).Angle() + Mathf.Pi * 0.5f;
            swarm.SpinAngle = Mathf.LerpAngle(swarm.SpinAngle, targetSpin, MathF.Min(1.0f, 26.0f * dt));

            // Warna pemanasan pengorbanan: makin lama di nozzle, makin membara sian-putih ke emas pijar
            float sacrificeHeat = Mathf.Clamp(swarm.LifeTime * 2.2f, 0.0f, 1.0f);
            vis.Color = new Color(
                Mathf.Lerp(1.6f, 4.8f, sacrificeHeat),
                Mathf.Lerp(3.6f, 4.4f, power),
                Mathf.Lerp(5.2f, 2.2f, sacrificeHeat),
                1.0f
            );
            vis.Scale = Mathf.Lerp(0.78f, 1.05f, sacrificeHeat);

            _activeThrusterJets.Add(new DartThrusterJet
            {
                Position = pos.Value,
                Direction = feederDir,
                Power = power,
                Length = pos.Value.DistanceTo(focalChamberPos),
                ThemeColor = vis.Color,
                IsBrake = false
            });
            _activeThrusterDartPositions.Add(pos.Value);
        });

        // =========================================================================
        // 6. FORMASI KUBAH PARABOLA DI DEPAN CORE (PARABOLIC BOW SHOCK DOME)
        //    Seluruh sisa swarm (Miner, Scanner, Guardian, Shuriken, Drill, & sisa Dart)
        //    membentuk perisai parabola aerodinamis di depan Core agar TIDAK ADA yang tertinggal!
        // =========================================================================
        UpdateParabolicFrontDome(dt, corePos, shipVel, aimDir, tangent, coreRadius, power);

        // =========================================================================
        // 7. UPDATE GPU SHADER MESH AEROSPIKE PLASMA BOOST BEAM [SPACE]
        // =========================================================================
        if (_plasmaBoostMesh != null && _plasmaBoostMat != null)
        {
            float pulse = _boostPulseTimer;
            float mainJetLen = Mathf.Lerp(175.0f, 360.0f, power) * (0.94f + 0.06f * MathF.Sin(pulse * 3.2f));
            float plumeWidth = (34.0f + 26.0f * power);
            Vector2 midPos = focalChamberPos + backDir * (mainJetLen * 0.5f);

            _plasmaBoostMesh.Visible = true;
            _plasmaBoostMesh.GlobalPosition = midPos;
            _plasmaBoostMesh.GlobalRotation = backDir.Angle();
            _plasmaBoostMesh.Scale = new Vector2(mainJetLen, plumeWidth);

            _plasmaBoostMat.SetShaderParameter("power", power);
            _plasmaBoostMat.SetShaderParameter("time", pulse);
            _plasmaBoostMat.SetShaderParameter("flash_boost", _boostSacrificeFlash);
            _plasmaBoostMat.SetShaderParameter("diamond_count", 6.8f);
        }

        QueueRedraw();
    }

    private void SacrificeOneThrusterDart(Vector2 focalChamberPos, Vector2 aimDir, float powerRatio)
    {
        bool sacrificed = false;
        int startCursor = _nextSacrificeSlotCursor;

        // Pilih Dart yang sudah paling matang / sesuai urutan rotasi cincin nozzle untuk dikorbankan
        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
        {
            if (sacrificed) return;
            if (swarm.OwnerId == 0 && swarm.Role == SwarmRole.DartNeedle && swarm.State == SwarmState.BoostThruster)
            {
                if (swarm.FormationIndex == startCursor || swarm.LifeTime >= 0.18f)
                {
                    IgniteDartAsPlasmaExhaust(ref pos, ref vel, ref swarm, ref vis, focalChamberPos, aimDir, powerRatio);
                    sacrificed = true;
                }
            }
        });

        if (!sacrificed)
        {
            _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
            {
                if (sacrificed) return;
                if (swarm.OwnerId == 0 && swarm.Role == SwarmRole.DartNeedle && swarm.State == SwarmState.BoostThruster)
                {
                    IgniteDartAsPlasmaExhaust(ref pos, ref vel, ref swarm, ref vis, focalChamberPos, aimDir, powerRatio);
                    sacrificed = true;
                }
            });
        }

        if (sacrificed)
        {
            _nextSacrificeSlotCursor = (_nextSacrificeSlotCursor + 1) % MaxBoostThrusterSlots;
            _boostSacrificeFlash = 1.0f;
            EmitSignal(SignalName.BoostDartSacrificed, focalChamberPos, aimDir, powerRatio);
            RefreshFoundryGui();
        }
    }

    private static void IgniteDartAsPlasmaExhaust(
        ref PositionComponent pos,
        ref VelocityComponent vel,
        ref SwarmUnitComponent swarm,
        ref VisualComponent vis,
        Vector2 focalChamberPos,
        Vector2 aimDir,
        float powerRatio)
    {
        swarm.State = SwarmState.DrillExhaust;
        swarm.LifeTime = 0.0f;
        swarm.MaxLifeTime = 0.26f + GD.Randf() * 0.14f;
        swarm.FormationIndex = -1;

        Vector2 backDir = -aimDir;
        Vector2 tangent = new Vector2(-aimDir.Y, aimDir.X);
        float jitterLat = (GD.Randf() - 0.5f) * 14.0f;
        pos.Value = focalChamberPos + tangent * jitterLat;

        float spreadAngle = (GD.Randf() - 0.5f) * 0.24f;
        Vector2 exhaustDir = backDir.Rotated(spreadAngle);
        float exhaustSpeed = (980.0f + 520.0f * powerRatio) * (0.85f + GD.Randf() * 0.30f);

        vel.Value = exhaustDir * exhaustSpeed;
        swarm.TargetDirection = exhaustDir;
        swarm.SpinAngle = exhaustDir.Angle() + Mathf.Pi * 0.5f;

        // Warna lontaran pengorbanan: inti plasma putih-sian berpijar yang meluruh jadi ungu-emas
        vis.Color = new Color(4.8f, 4.6f, 5.8f, 1.0f);
        vis.Scale = 1.38f;
    }

    /// <summary>
    /// Menyusun seluruh sisa swarm yang sedang Orbiting/Returning menjadi Kubah Parabola berlapis
    /// di depan Core (Parabolic Bow Shock Dome): P(s) = Apex - aimDir * (k * s^2) + tangent * s
    /// sehingga tidak ada satu pun unit yang tercecer di belakang saat kecepatan tinggi.
    /// </summary>
    private void UpdateParabolicFrontDome(
        float dt,
        Vector2 corePos,
        Vector2 shipVel,
        Vector2 aimDir,
        Vector2 tangent,
        float coreRadius,
        float powerRatio)
    {
        int domeTotal = 0;
        _world.Query(in _allSwarmQuery, (ref SwarmUnitComponent swarm) =>
        {
            if (swarm.OwnerId == 0 && (swarm.State == SwarmState.Orbiting || swarm.State == SwarmState.Returning))
            {
                domeTotal++;
            }
        });

        _activeParabolaSwarmCount = domeTotal;
        float apexDist = coreRadius + 46.0f + powerRatio * 16.0f;
        _parabolaApexWorldPos = corePos + aimDir * apexDist;
        _parabolaHalfSpan = Mathf.Clamp(68.0f + MathF.Sqrt(Math.Max(1, domeTotal)) * 11.5f, 72.0f, 165.0f);
        _parabolaCurvatureK = 0.0054f;

        if (domeTotal == 0) return;

        int layerCount = domeTotal > 42 ? 3 : (domeTotal > 16 ? 2 : 1);
        float pulse = _boostPulseTimer;
        int domeIdx = 0;

        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
        {
            if (swarm.OwnerId != 0 || (swarm.State != SwarmState.Orbiting && swarm.State != SwarmState.Returning))
                return;

            // Pastikan unit yang tadinya Returning langsung masuk ke formasi kubah Orbiting
            if (swarm.State == SwarmState.Returning)
            {
                swarm.State = SwarmState.Orbiting;
            }

            int layer = domeIdx % layerCount;
            int idxInLayer = domeIdx / layerCount;
            int countInLayer = Math.Max(1, (domeTotal + layerCount - 1 - layer) / layerCount);

            // Sebar simetris kiri-kanan di sepanjang kurva parabola: u dari -1.0 hingga +1.0
            float u = (countInLayer > 1)
                ? ((float)idxInLayer / (countInLayer - 1)) * 2.0f - 1.0f
                : 0.0f;

            float layerSpan = _parabolaHalfSpan * (1.0f - layer * 0.13f);
            float s = u * layerSpan;
            float layerDepthOffset = layer * 14.0f;

            // Persamaan Kubah Parabola: x(s) = -k * s^2, y(s) = s
            float parabolaBackX = _parabolaCurvatureK * (s * s) + layerDepthOffset;
            float shimmerWave = MathF.Sin(pulse * 2.4f + domeIdx * 0.9f) * 2.2f;

            Vector2 domeSlotPos = _parabolaApexWorldPos
                                - aimDir * parabolaBackX
                                + tangent * (s + shimmerWave);

            // Tarikan pegas pengunci super responsif agar 100% menempel di depan Core tanpa keteteran
            Vector2 toSlot = domeSlotPos - pos.Value;
            float distSq = toSlot.LengthSquared();
            Vector2 targetVel = shipVel + toSlot * 28.0f;

            vel.Value = vel.Value.Lerp(targetVel, MathF.Min(1.0f, 24.0f * dt));
            pos.Value += vel.Value * dt;

            // Koreksi posisi langsung (hard-lock elastis) agar mustahil tertinggal meski kapal bermanuver tajam
            float snapFactor = distSq > 180.0f * 180.0f ? 22.0f : 14.0f;
            pos.Value = pos.Value.Lerp(domeSlotPos, MathF.Min(1.0f, snapFactor * dt));

            // Orientasi unit mengikuti garis singgung/normal kurva parabola menghadap ke depan
            float slopeAngle = MathF.Atan(-2.0f * _parabolaCurvatureK * s) * 0.45f;
            float targetSpin = aimDir.Angle() + slopeAngle + Mathf.Pi * 0.5f;
            swarm.SpinAngle = Mathf.LerpAngle(swarm.SpinAngle, targetSpin, MathF.Min(1.0f, 20.0f * dt));

            // Kilau ionisasi aerodinamis pada unit di kubah depan
            float targetScale = swarm.Role switch
            {
                SwarmRole.Guardian => 0.78f,
                SwarmRole.ScannerDrone => 0.65f,
                SwarmRole.MiningDroid => 0.66f,
                _ => 0.60f
            };
            vis.Scale = Mathf.Lerp(vis.Scale, targetScale, dt * 10.0f);
            Color bowTint = new Color(1.6f, 3.4f, 4.8f, 1.0f);
            vis.Color = vis.BaseColor.Lerp(bowTint, 0.35f * powerRatio);

            domeIdx++;
        });
    }

    private void DeactivateDartBoost()
    {
        _isBoostActive = false;
        _isBoostFullPower = false;
        _boostPrimingTimer = 0.0f;
        _boostSacrificeAccumulator = 0.0f;
        _activeBoostDartCount = 0;
        _activeParabolaSwarmCount = 0;
        _activeThrusterJets.Clear();
        _activeThrusterDartPositions.Clear();

        if (_plasmaBoostMesh != null)
        {
            _plasmaBoostMesh.Visible = false;
        }

        if (_playerCore != null)
        {
            _playerCore.IsBoosting = false;
            _playerCore.BoostThrustVector = Vector2.Zero;
        }

        _world.Query(in _allSwarmQuery, (ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
        {
            if (swarm.OwnerId == 0 && (swarm.State == SwarmState.BoostThruster || swarm.State == SwarmState.Orbiting))
            {
                if (swarm.State == SwarmState.BoostThruster)
                {
                    swarm.State = SwarmState.Orbiting;
                    swarm.LifeTime = 0.0f;
                    swarm.FormationIndex = -1;
                }
                vis.Color = vis.BaseColor;
                vis.Scale = swarm.Role switch
                {
                    SwarmRole.Guardian => 0.72f,
                    SwarmRole.ScannerDrone => 0.60f,
                    SwarmRole.MiningDroid => 0.62f,
                    _ => 0.55f
                };
            }
        });

        QueueRedraw();
    }

    // =========================================================================
    // SISTEM RETRO-ROCKET BRAKING: DART BERKUMPUL DI DEPAN Core MENAHAN LAJU
    // =========================================================================

    private void ActivateDartBrake(Vector2 coreVel)
    {
        if (coreVel.LengthSquared() < 100.0f) return;

        _isBrakeActive = true;
        _brakeTimer = 0.0f;
        Array.Clear(_brakeSlotOccupied, 0, MaxBrakeSlots);
        _activeBrakeDartCount = 0;
        _activeBrakeJets.Clear();
        _activeBrakeDartPositions.Clear();

        int recruited = 0;
        _world.Query(in _allSwarmQuery, (ref SwarmUnitComponent swarm) =>
        {
            if (recruited >= MaxBrakeSlots) return;
            if (swarm.OwnerId == 0
                && swarm.Role == SwarmRole.DartNeedle
                && (swarm.State == SwarmState.Orbiting || swarm.State == SwarmState.Returning))
            {
                swarm.State = SwarmState.BoostBrake;
                swarm.FormationIndex = recruited;
                swarm.LifeTime = 0.0f;
                _brakeSlotOccupied[recruited] = true;
                recruited++;
            }
        });

        _activeBrakeDartCount = recruited;
    }

    private void UpdateDartBrake(float dt, Vector2 corePos, Vector2 coreVel)
    {
        _brakeTimer += dt;
        float currentSpeed = coreVel.Length();

        // Selesai ngerem jika kecepatan sudah rendah atau waktu pengereman maksimal (~2.2 detik) tercapai
        if (currentSpeed <= _playerCore.MaxSpeed * 0.30f || _brakeTimer >= 2.2f || _activeBrakeDartCount == 0)
        {
            DeactivateDartBrake();
            return;
        }

        Vector2 velDir = coreVel.Normalized();

        // Darts berkumpul di depan Core searah vektor kecepatan, membentuk busur pencegat (counter-brake arc)
        float frontalDist = 32.0f + MathF.Sqrt(Math.Max(1, _playerCore.CoreCellCount)) * 9.5f;
        float brakeArcSpan = 0.65f * Mathf.Pi; // ~117 derajat busur cekung di depan Core
        float baseAngle = velDir.Angle();

        // Gaya pengereman retro-rocket ke Core
        float brakeStrengthRatio = Mathf.Clamp(currentSpeed / _playerCore.MaxSpeed, 0.4f, 1.0f);
        float brakePower = 6400.0f * brakeStrengthRatio * Mathf.Clamp((float)_activeBrakeDartCount / 8.0f, 0.35f, 1.0f);
        _playerCore.IsBraking = true;
        _playerCore.BrakeThrustVector = -velDir * brakePower;

        _activeBrakeJets.Clear();
        _activeBrakeDartPositions.Clear();

        float pulseTime = _boostPulseTimer;

        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
        {
            if (swarm.OwnerId != 0 || swarm.Role != SwarmRole.DartNeedle || swarm.State != SwarmState.BoostBrake)
                return;

            int slot = Math.Clamp(swarm.FormationIndex, 0, MaxBrakeSlots - 1);
            swarm.LifeTime += dt;

            float arcT = (_activeBrakeDartCount > 1)
                ? ((float)slot / (_activeBrakeDartCount - 1) - 0.5f)
                : 0.0f;
            float angle = baseAngle + arcT * brakeArcSpan;

            Vector2 slotPos = corePos + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * frontalDist;
            Vector2 slotDir = (slotPos - corePos).Normalized();

            // Dart menghadap ke Core (pantat/nozzle menghadap ke depan searah velDir)
            // Semburan plasma diluncurkan ke depan (searah velDir) untuk menahan laju kapal
            Vector2 brakeExhaustDir = (velDir * 0.75f + slotDir * 0.25f).Normalized();

            float lockProgress = Mathf.Clamp(swarm.LifeTime / 0.12f, 0.0f, 1.0f);
            Vector2 toSlot = slotPos - pos.Value;
            Vector2 targetVel = coreVel + toSlot * Mathf.Lerp(14.0f, 32.0f, lockProgress);

            vel.Value = vel.Value.Lerp(targetVel, MathF.Min(1.0f, 24.0f * dt));
            pos.Value = pos.Value.Lerp(slotPos, MathF.Min(1.0f, (12.0f + 20.0f * lockProgress) * dt));

            float targetSpin = (-brakeExhaustDir).Angle() + Mathf.Pi * 0.5f;
            swarm.SpinAngle = Mathf.LerpAngle(swarm.SpinAngle, targetSpin, MathF.Min(1.0f, 24.0f * dt));

            // Warna plasma pengereman: oranye-kuning panas membara
            vis.Color = new Color(
                4.8f,
                Mathf.Lerp(1.8f, 3.8f, brakeStrengthRatio),
                0.5f,
                1.0f
            );
            vis.Scale = 0.95f;

            float jetLen = Mathf.Lerp(35.0f, 95.0f, brakeStrengthRatio) * (0.85f + 0.15f * MathF.Sin(pulseTime * 2.8f + slot * 1.5f));
            _activeBrakeJets.Add(new DartThrusterJet
            {
                Position = pos.Value,
                Direction = brakeExhaustDir,
                Power = brakeStrengthRatio,
                Length = jetLen,
                ThemeColor = vis.Color,
                IsBrake = true
            });
            _activeBrakeDartPositions.Add(pos.Value);
        });

        QueueRedraw();
    }

    private void DeactivateDartBrake()
    {
        _isBrakeActive = false;
        _brakeTimer = 0.0f;
        _activeBrakeDartCount = 0;
        _activeBrakeJets.Clear();
        _activeBrakeDartPositions.Clear();

        if (_playerCore != null)
        {
            _playerCore.IsBraking = false;
            _playerCore.BrakeThrustVector = Vector2.Zero;
        }

        _world.Query(in _allSwarmQuery, (ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
        {
            if (swarm.OwnerId == 0 && swarm.State == SwarmState.BoostBrake)
            {
                swarm.State = SwarmState.Orbiting;
                swarm.LifeTime = 0.0f;
                swarm.FormationIndex = -1;
                vis.Color = vis.BaseColor;
                vis.Scale = 0.55f;
            }
        });

        QueueRedraw();
    }

    private static void ComputeGigaDrillDimensions(int lanceCount, out float drillLength, out float drillBaseRadius, out float densityFactor)
    {
        int n = Math.Max(1, lanceCount);
        float sqrtN = MathF.Sqrt(n);

        drillLength = Mathf.Clamp(76.0f + sqrtN * 5.4f, 82.0f, 172.0f);
        drillBaseRadius = Mathf.Clamp(23.0f + sqrtN * 1.95f, 25.0f, 60.0f);
        densityFactor = Mathf.Clamp(n / 65.0f, 0.6f, 4.5f);
    }

    private void BeginGigaDrillCharge(Vector2 corePos, Vector2 aimDir)
    {
        if (_isBoostActive)
        {
            DeactivateDartBoost();
        }

        // Murni menggunakan DrillDrone hasil rakitan Pabrik (Hive Foundry), tanpa konversi otomatis dari DartNeedle!
        int readyLanceCount = 0;
        _world.Query(in _allSwarmQuery, (ref SwarmUnitComponent swarm) =>
        {
            if (swarm.OwnerId == 0 && swarm.Role == SwarmRole.DrillDrone && swarm.State == SwarmState.Orbiting)
            {
                readyLanceCount++;
            }
        });

        if (readyLanceCount < 1)
        {
            _isChargingDrill = false;
            return;
        }

        _isChargingDrill = true;
        _drillChargeTimer = 0.0f;
        _overchargeExhaustTimer = 0.0f;

        Vector2 frontAnchor = corePos + aimDir * 56.0f;
        List<(float distSq, int tempId)> sortList = new();
        int tempCounter = 0;

        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref SwarmUnitComponent swarm) =>
        {
            if (swarm.State != SwarmState.Orbiting || swarm.OwnerId != 0 || swarm.Role != SwarmRole.DrillDrone)
                return;

            swarm.State = SwarmState.ChargingDrill;
            swarm.FormationIndex = tempCounter;
            sortList.Add((pos.Value.DistanceSquaredTo(frontAnchor), tempCounter));
            tempCounter++;
        });

        sortList.Sort((a, b) => a.distSq.CompareTo(b.distSq));
        int[] rankLookup = new int[tempCounter];
        for (int rank = 0; rank < sortList.Count; rank++)
        {
            rankLookup[sortList[rank].tempId] = rank;
        }

        _activeDrillFormationCount = tempCounter;
        int exhaustCount = Math.Max(2, (int)MathF.Round(_activeDrillFormationCount * 0.28f));
        if (_activeDrillFormationCount <= 4) exhaustCount = 1;
        _gigaDrillExhaustStartIndex = Math.Max(1, _activeDrillFormationCount - exhaustCount);

        _world.Query(in _allSwarmQuery, (ref SwarmUnitComponent swarm) =>
        {
            if (swarm.State == SwarmState.ChargingDrill && swarm.OwnerId == 0 && swarm.Role == SwarmRole.DrillDrone)
            {
                if (swarm.FormationIndex >= 0 && swarm.FormationIndex < rankLookup.Length)
                {
                    swarm.FormationIndex = rankLookup[swarm.FormationIndex];
                }
            }
        });

        int readyDefenderCount = 0;
        _world.Query(in _allSwarmQuery, (ref SwarmUnitComponent swarm) =>
        {
            if (swarm.OwnerId == 0 && swarm.Role == SwarmRole.Guardian && swarm.State == SwarmState.Orbiting)
            {
                readyDefenderCount++;
            }
        });

        int defenderAPQuota = readyDefenderCount > 0 ? Math.Max(1, (int)MathF.Round(readyDefenderCount * 0.25f)) : 0;
        int defCounter = 0;

        _world.Query(in _allSwarmQuery, (ref SwarmUnitComponent swarm) =>
        {
            if (defCounter >= defenderAPQuota) return;
            if (swarm.OwnerId == 0 && swarm.Role == SwarmRole.Guardian && swarm.State == SwarmState.Orbiting)
            {
                swarm.State = SwarmState.ChargingDrill;
                swarm.FormationIndex = defCounter;
                defCounter++;
            }
        });

        _activeDefenderCoreCount = defCounter;
    }

    private void HandleGigaDrillCharge(float dt, Vector2 corePos, Vector2 coreVel, Vector2 mousePos)
    {
        if (Input.IsMouseButtonPressed(MouseButton.Right) && !_isGigaDrillActive)
        {
            Vector2 toMouse = mousePos - corePos;
            Vector2 aimDir = toMouse.LengthSquared() > 1.0f ? toMouse.Normalized() : Vector2.Right;
            Vector2 tangent = new Vector2(-aimDir.Y, aimDir.X);

            if (!_isChargingDrill)
            {
                BeginGigaDrillCharge(corePos, aimDir);
            }

            _drillChargeTimer += dt;
            float chargeProgress = DrillChargeRatio;

            float spinSpeed = Mathf.Lerp(9.0f, 38.0f, chargeProgress * chargeProgress);
            _gigaDrillSpinTime += dt * spinSpeed;

            int totalUnits = Math.Max(1, _activeDrillFormationCount);
            ComputeGigaDrillDimensions(totalUnits, out float drillLength, out float drillBaseRadius, out float densityFactor);

            float desiredTipDist = 52.0f + chargeProgress * 38.0f;
            if (TryRaycastClosestAsteroidVoxel(corePos, aimDir, desiredTipDist + 10.0f, out _, out VoxelRayHit chargeHit))
            {
                // Jangan biarkan ujung bor menembus melewati kulit pinggir asteroid saat sedang charge di dekat batu!
                desiredTipDist = MathF.Min(desiredTipDist, MathF.Max(16.0f, chargeHit.Distance));
            }
            Vector2 tipPos = corePos + aimDir * desiredTipDist;
            Vector2 basePos = tipPos - aimDir * drillLength;

            GigaDrillTipPosition = tipPos;
            GigaDrillBasePosition = basePos;
            GigaDrillDirection = aimDir;
            GigaDrillLength = drillLength;
            GigaDrillRadius = drillBaseRadius;
            GigaDrillDensityFactor = densityFactor;

            int fluteCount = Math.Clamp(3 + totalUnits / 42, 3, 9);
            int shellCount = totalUnits >= 135 ? 3 : (totalUnits >= 55 ? 2 : 1);
            int exhaustStartIdx = _gigaDrillExhaustStartIndex;

            _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
            {
                if (swarm.State != SwarmState.ChargingDrill || swarm.OwnerId != 0)
                    return;

                if (swarm.Role == SwarmRole.Guardian)
                {
                    int defTotal = Math.Max(1, _activeDefenderCoreCount);
                    float defConeLength = MathF.Min(56.0f, 20.0f + MathF.Sqrt(defTotal) * 6.5f);
                    float defBaseRadius = MathF.Min(28.0f, 12.0f + MathF.Sqrt(defTotal) * 3.6f);

                    float defT = (defTotal > 1) ? (float)swarm.FormationIndex / (defTotal - 1) : 0.0f;
                    float defDistForward = (1.0f - defT) * defConeLength;
                    float defConeProfile = MathF.Pow(defT, 0.78f);
                    float defR = defConeProfile * defBaseRadius;

                    int strand = swarm.FormationIndex % (defTotal >= 14 ? 3 : 2);
                    float defSpiralAngle = strand * (Mathf.Tau / (defTotal >= 14 ? 3 : 2))
                                         + defT * (Mathf.Tau * 2.0f)
                                         + _gigaDrillSpinTime * 0.95f;

                    float defActivationDelay = (1.0f - defT) * 0.35f;
                    float defLocalProgress = Mathf.Clamp((chargeProgress - defActivationDelay) / 0.55f, 0.0f, 1.0f);
                    float defSmoothLock = defLocalProgress * defLocalProgress * (3.0f - 2.0f * defLocalProgress);

                    float defVortexExpansion = (1.0f - defSmoothLock) * (32.0f + defR * 1.35f);
                    float defCurrentR = defR + defVortexExpansion;

                    Vector2 defSlotPos = (tipPos + aimDir * defDistForward)
                                       + (tangent * (defCurrentR * MathF.Cos(defSpiralAngle)))
                                       + (aimDir * (0.18f * defCurrentR * MathF.Sin(defSpiralAngle)));

                    Vector2 toDefSlot = defSlotPos - pos.Value;
                    float defPull = Mathf.Lerp(8.0f, 26.0f, defSmoothLock);
                    Vector2 defTargetVel = toDefSlot * defPull + coreVel;

                    vel.Value = vel.Value.Lerp(defTargetVel, MathF.Min(1.0f, (7.0f + 16.0f * defSmoothLock) * dt));
                    pos.Value += vel.Value * dt;

                    if (defSmoothLock > 0.65f)
                    {
                        float defLockFactor = (defSmoothLock - 0.65f) / 0.35f;
                        pos.Value = pos.Value.Lerp(defSlotPos, defLockFactor * MathF.Min(1.0f, 18.0f * dt));
                    }

                    swarm.SpinAngle = aimDir.Angle() + Mathf.Pi * 0.5f + MathF.Sin(defSpiralAngle) * 0.35f;
                    vis.Color = (defT < 0.28f)
                        ? new Color(2.8f, 5.5f, 7.0f, 1.0f)
                        : new Color(0.9f, 3.8f, 5.5f, 1.0f);
                    vis.Scale = 1.15f;
                    return;
                }

                if (swarm.Role != SwarmRole.DrillDrone)
                    return;

                int idx = swarm.FormationIndex;
                bool isExhaustStage = idx >= exhaustStartIdx;
                float t = (totalUnits > 1) ? (float)idx / (totalUnits - 1) : 0.0f;

                float activationDelay = t * 0.44f;
                float localProgress = Mathf.Clamp((chargeProgress - activationDelay) / 0.54f, 0.0f, 1.0f);
                float smoothLock = localProgress * localProgress * (3.0f - 2.0f * localProgress);

                float d;
                float rCone;
                float spiralAngle;

                if (!isExhaustStage)
                {
                    float bodyT = (exhaustStartIdx > 1) ? (float)idx / (exhaustStartIdx - 1) : 0.0f;
                    d = bodyT * (drillLength * 0.84f);

                    int shell = idx % shellCount;
                    float shellRadiusMult = shell switch
                    {
                        0 => 1.0f,
                        1 => 0.62f,
                        _ => 0.28f
                    };
                    float shellSpinDir = (shell == 1) ? -0.75f : 1.0f;

                    float coneProfile = MathF.Pow(bodyT, 0.82f);
                    rCone = coneProfile * drillBaseRadius * shellRadiusMult;

                    int flute = idx % fluteCount;
                    spiralAngle = flute * (Mathf.Tau / fluteCount)
                                + bodyT * (Mathf.Tau * 3.0f)
                                + _gigaDrillSpinTime * shellSpinDir;
                }
                else
                {
                    int exhaustCount = Math.Max(1, totalUnits - exhaustStartIdx);
                    float exhaustT = (float)(idx - exhaustStartIdx) / exhaustCount;

                    d = drillLength * (0.84f + exhaustT * 0.20f);
                    rCone = drillBaseRadius * (0.55f + 0.55f * exhaustT);

                    int nozzleRing = (idx - exhaustStartIdx) % Math.Max(4, fluteCount);
                    spiralAngle = nozzleRing * (Mathf.Tau / Math.Max(4, fluteCount))
                                - _gigaDrillSpinTime * 1.45f
                                + exhaustT * Mathf.Pi;
                }

                float vortexExpansion = (1.0f - smoothLock) * (36.0f + rCone * 1.45f);
                float currentRadius = rCone + vortexExpansion;

                float jitter = (chargeProgress > 0.75f)
                    ? MathF.Sin(_gigaDrillSpinTime * 4.0f + idx * 1.7f) * (isExhaustStage ? 3.5f : 1.2f)
                    : 0.0f;

                Vector2 slotPos = (tipPos - aimDir * d)
                                + (tangent * ((currentRadius + jitter) * MathF.Cos(spiralAngle)))
                                + (aimDir * (0.28f * currentRadius * MathF.Sin(spiralAngle)));

                Vector2 toSlot = slotPos - pos.Value;
                float springPull = Mathf.Lerp(7.5f, 24.0f, smoothLock);
                Vector2 vortexTangentVel = tangent * (MathF.Cos(spiralAngle) * 240.0f * (1.0f - smoothLock * 0.65f));
                Vector2 targetVel = toSlot * springPull + vortexTangentVel + coreVel;

                vel.Value = vel.Value.Lerp(targetVel, MathF.Min(1.0f, (6.5f + 14.0f * smoothLock) * dt));
                pos.Value += vel.Value * dt;

                if (smoothLock > 0.65f)
                {
                    float lockFactor = (smoothLock - 0.65f) / 0.35f;
                    pos.Value = pos.Value.Lerp(slotPos, lockFactor * MathF.Min(1.0f, 18.0f * dt));
                }

                if (!isExhaustStage)
                {
                    float targetSpin = aimDir.Angle() + Mathf.Pi * 0.5f + MathF.Sin(spiralAngle) * 0.42f;
                    swarm.SpinAngle = Mathf.LerpAngle(swarm.SpinAngle, targetSpin, MathF.Min(1.0f, 14.0f * dt));

                    if (idx < Math.Max(4, totalUnits / 12))
                    {
                        vis.Color = new Color(4.0f, 4.0f, 2.6f, 1.0f);
                        vis.Scale = 1.08f;
                    }
                    else
                    {
                        float heat = Mathf.Lerp(0.85f, 1.35f, smoothLock);
                        vis.Color = new Color(3.2f * heat, 1.05f * heat, 0.35f, 1.0f);
                        vis.Scale = 0.96f;
                    }
                }
                else
                {
                    float targetSpin = (-aimDir).Angle() + Mathf.Pi * 0.5f + MathF.Sin(spiralAngle * 2.0f) * 0.25f;
                    swarm.SpinAngle = Mathf.LerpAngle(swarm.SpinAngle, targetSpin, MathF.Min(1.0f, 14.0f * dt));

                    float ignitionPulse = 0.5f + 0.5f * MathF.Sin(_gigaDrillSpinTime * 3.2f + idx);
                    vis.Color = new Color(
                        Mathf.Lerp(3.4f, 4.2f, ignitionPulse * chargeProgress),
                        Mathf.Lerp(0.9f, 2.8f, ignitionPulse * chargeProgress),
                        Mathf.Lerp(0.4f, 3.2f, chargeProgress),
                        1.0f
                    );
                    vis.Scale = Mathf.Lerp(0.95f, 1.22f, chargeProgress);
                }
            });

            if (chargeProgress >= 0.92f && _activeDrillFormationCount > 6)
            {
                _overchargeExhaustTimer += dt;
                if (_overchargeExhaustTimer >= 0.14f)
                {
                    _overchargeExhaustTimer = 0.0f;
                    TriggerSingleRearExhaust(basePos, aimDir, 720.0f);
                }
            }
        }
        else if (_isChargingDrill)
        {
            _isChargingDrill = false;
            float chargeProgress = DrillChargeRatio;

            Vector2 toMouse = mousePos - corePos;
            Vector2 aimDir = toMouse.LengthSquared() > 1.0f ? toMouse.Normalized() : Vector2.Right;

            if (chargeProgress >= 0.30f && _activeDrillFormationCount > 0)
            {
                LaunchSuperGigaDrill(corePos, aimDir, mousePos, chargeProgress);
            }
            else
            {
                _world.Query(in _allSwarmQuery, (ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
                {
                    if (swarm.State == SwarmState.ChargingDrill)
                    {
                        swarm.State = SwarmState.Orbiting;
                        vis.Color = vis.BaseColor;
                        vis.Scale = (swarm.Role == SwarmRole.Guardian) ? 1.15f : 0.95f;
                    }
                });
                _activeDefenderCoreCount = 0;
                FireSwarmVolley(mousePos, 25);
            }

            _drillChargeTimer = 0.0f;
        }
    }

    private void TriggerSingleRearExhaust(Vector2 nozzlePos, Vector2 drillAimDir, float baseExhaustSpeed)
    {
        int highestIdx = -1;
        _world.Query(in _allSwarmQuery, (ref SwarmUnitComponent swarm) =>
        {
            if ((swarm.State == SwarmState.ChargingDrill || swarm.State == SwarmState.Launched)
                && swarm.Role == SwarmRole.DrillDrone && swarm.OwnerId == 0)
            {
                if (swarm.FormationIndex > highestIdx)
                {
                    highestIdx = swarm.FormationIndex;
                }
            }
        });

        if (highestIdx < _gigaDrillExhaustStartIndex || highestIdx <= 1) return;

        bool triggered = false;
        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
        {
            if (triggered) return;
            if ((swarm.State == SwarmState.ChargingDrill || swarm.State == SwarmState.Launched)
                && swarm.Role == SwarmRole.DrillDrone && swarm.OwnerId == 0
                && swarm.FormationIndex == highestIdx)
            {
                IgniteUnitAsExhaust(ref pos, ref vel, ref swarm, ref vis, nozzlePos, drillAimDir, baseExhaustSpeed);
                _activeDrillFormationCount = Math.Max(1, _activeDrillFormationCount - 1);
                triggered = true;
            }
        });
    }

    private static void IgniteUnitAsExhaust(
        ref PositionComponent pos,
        ref VelocityComponent vel,
        ref SwarmUnitComponent swarm,
        ref VisualComponent vis,
        Vector2 nozzlePos,
        Vector2 drillAimDir,
        float baseExhaustSpeed)
    {
        swarm.State = SwarmState.DrillExhaust;
        swarm.LifeTime = 0.0f;
        swarm.MaxLifeTime = 0.28f + GD.Randf() * 0.16f;

        Vector2 tangent = new Vector2(-drillAimDir.Y, drillAimDir.X);
        float radialJitter = (GD.Randf() - 0.5f) * 20.0f;
        pos.Value = nozzlePos + tangent * radialJitter;

        float plumeSpread = (GD.Randf() - 0.5f) * 0.45f;
        Vector2 exhaustDir = (-drillAimDir).Rotated(plumeSpread);
        float speed = baseExhaustSpeed * (0.8f + GD.Randf() * 0.45f);

        vel.Value = exhaustDir * speed;
        swarm.TargetDirection = exhaustDir;
        swarm.SpinAngle = exhaustDir.Angle() + Mathf.Pi * 0.5f;

        vis.Color = new Color(4.5f, 4.0f, 3.0f, 1.0f);
        vis.Scale = 1.35f;
    }

    private void LaunchSuperGigaDrill(Vector2 corePos, Vector2 aimDir, Vector2 targetPos, float chargeProgress)
    {
        _isGigaDrillActive = true;
        _isRammingEnemyCore = false;
        _isGrindingAsteroid = false;
        _coreRamStallTimer = 0.0f;
        _coreRamSpinTimer = 0.0f;
        _coreRamAttritionAccumulator = 0.0f;
        _drillAsteroidGrindTimer = 0.0f;
        _drillEnemyGrindTimer = 0.0f;
        _drillAttritionTimer = 0.0f;

        _gigaDrillSafeOutsidePos = corePos;
        float launchTipDist = 52.0f + chargeProgress * 38.0f;
        if (TryRaycastClosestAsteroidVoxel(corePos, aimDir, launchTipDist + 10.0f, out _, out VoxelRayHit launchHit))
        {
            launchTipDist = MathF.Min(launchTipDist, MathF.Max(16.0f, launchHit.Distance));
        }
        _gigaDrillTipPos = corePos + aimDir * launchTipDist;
        _gigaDrillAimDir = aimDir;
        _gigaDrillTargetPos = targetPos;
        _hasDefenderNoseBreached = false;
        _gigaDrillVel = aimDir * (1650.0f + chargeProgress * 450.0f);
        _gigaDrillFlightTimer = GigaDrillFlightDuration;
        _gigaDrillElapsedFlight = 0.0f;

        int totalUnits = Math.Max(1, _activeDrillFormationCount);
        int exhaustStartIdx = _gigaDrillExhaustStartIndex;
        int exhaustStageCount = Math.Max(1, totalUnits - exhaustStartIdx);

        ComputeGigaDrillDimensions(totalUnits, out float drillLength, out float drillBaseRadius, out _);
        Vector2 nozzlePos = _gigaDrillTipPos - aimDir * drillLength;

        EmitSignal(SignalName.GigaDrillLaunched, _gigaDrillTipPos, nozzlePos, aimDir, chargeProgress);

        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
        {
            if (swarm.State == SwarmState.ChargingDrill && swarm.OwnerId == 0)
            {
                swarm.State = SwarmState.Launched;
                if (swarm.Role == SwarmRole.Guardian)
                {
                    swarm.MaxHealth = 5; // Defender di Ballistic Shield punya 5 HP
                    swarm.Health = 5;
                }
                else
                {
                    swarm.MaxHealth = 1;
                    swarm.Health = 1;
                }
                swarm.TargetDirection = aimDir;
                swarm.LifeTime = 0.0f;
                swarm.MaxLifeTime = GigaDrillFlightDuration + 0.5f;

                if (swarm.Role == SwarmRole.DrillDrone)
                {
                    // Hanya driller tahap belakang (exhaust stage) yang terbakar bertahap sebagai thruster selama meluncur.
                    // Batang utama mata bor tetap utuh untuk menghantam Core atau menggerus Asteroid!
                    if (swarm.FormationIndex >= exhaustStartIdx && totalUnits > 3)
                    {
                        int orderInExhaust = (totalUnits - 1) - swarm.FormationIndex;
                        float ratio = (float)(orderInExhaust + 1) / exhaustStageCount;
                        swarm.ExhaustTriggerTime = ratio * (GigaDrillFlightDuration * 0.90f);

                        if (swarm.ExhaustTriggerTime <= 0.04f)
                        {
                            IgniteUnitAsExhaust(ref pos, ref vel, ref swarm, ref vis, nozzlePos, aimDir, 1050.0f);
                        }
                    }
                    else
                    {
                        swarm.ExhaustTriggerTime = -1.0f;
                    }
                }
                else
                {
                    swarm.ExhaustTriggerTime = -1.0f;
                }
            }
        });
    }

    private void TriggerNoseShieldBreach(float defConeLength)
    {
        if (_hasDefenderNoseBreached) return;
        _hasDefenderNoseBreached = true;
        Vector2 breachCenter = _gigaDrillTipPos + _gigaDrillAimDir * (defConeLength * 0.5f);

        int breachedCount = 0;
        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
        {
            if (swarm.OwnerId == 0 && swarm.Role == SwarmRole.Guardian && swarm.State == SwarmState.Launched)
            {
                swarm.State = SwarmState.Dead;
                vis.Color = new Color(0, 0, 0, 0);
                TriggerClashAnimation(pos.Value, dropScrap: true);
                breachedCount++;
            }
        });

        _entityCount = Math.Max(0, _entityCount - breachedCount);
        GuardianCount = Math.Max(0, GuardianCount - breachedCount);
        _activeDefenderCoreCount = 0;

        EmitSignal(SignalName.GigaDrillNoseBreached, breachCenter, _gigaDrillAimDir, Math.Max(1, breachedCount));
        RefreshFoundryGui();
    }

    private void UpdateActiveGigaDrill(float dt)
    {
        if (!_isGigaDrillActive) return;

        int totalUnits = Math.Max(1, _activeDrillFormationCount);
        int exhaustStartIdx = Math.Max(1, _gigaDrillExhaustStartIndex);
        ComputeGigaDrillDimensions(totalUnits, out float drillLength, out float drillBaseRadius, out float densityFactor);

        // =====================================================================
        // 1. CEK JARAK DEKAT ENEMY CORE -> PECAHKAN BALLISTIC SHIELD (DEFENDER)
        // =====================================================================
        if (!_hasDefenderNoseBreached && _activeDefenderCoreCount > 0)
        {
            float defConeLength = MathF.Min(56.0f, 20.0f + MathF.Sqrt(_activeDefenderCoreCount) * 6.5f);
            float distToEnemyCore = (_enemyCore != null && IsInstanceValid(_enemyCore))
                ? _gigaDrillTipPos.DistanceTo(_enemyCore.GlobalPosition)
                : 9999.0f;
            float shieldShatterDist = (_enemyCore != null && IsInstanceValid(_enemyCore))
                ? (_enemyCore.CoreCollisionRadius + defConeLength + 42.0f)
                : 130.0f;

            int aliveConeDefenders = 0;
            _world.Query(in _allSwarmQuery, (ref SwarmUnitComponent s) =>
            {
                if (s.OwnerId == 0 && s.Role == SwarmRole.Guardian && s.State == SwarmState.Launched)
                {
                    aliveConeDefenders++;
                }
            });

            if (aliveConeDefenders == 0)
            {
                _hasDefenderNoseBreached = true;
                _activeDefenderCoreCount = 0;
            }
            else if (distToEnemyCore <= shieldShatterDist)
            {
                // Begitu dekat dengan Core, Ballistic Shield Defender pecah & mati semua!
                TriggerNoseShieldBreach(defConeLength);
            }
        }

        // =====================================================================
        // 2. MEKANIK TABRAKAN KE ENEMY CORE (CORE-RAMMING SEQUENCE):
        //    - Menabrak permukaan Core (TIDAK menembus) + Inersia Core berat
        //    - Berhenti berputar sejenak (Stall)
        //    - 0.5 detik berputar kencang selama sisa driller menggerus Core
        //    - Saat hampir habis -> Meledak Spektakuler (Efek Grid + Supernova)
        // =====================================================================
        if (_enemyCore != null && IsInstanceValid(_enemyCore))
        {
            float distToEnemy = _gigaDrillTipPos.DistanceTo(_enemyCore.GlobalPosition);
            float coreContactDist = _enemyCore.CoreCollisionRadius + 8.0f;

            if (_isRammingEnemyCore || distToEnemy <= coreContactDist)
            {
                if (!_hasDefenderNoseBreached && _activeDefenderCoreCount > 0)
                {
                    float defConeLength = MathF.Min(56.0f, 20.0f + MathF.Sqrt(_activeDefenderCoreCount) * 6.5f);
                    TriggerNoseShieldBreach(defConeLength);
                }

                if (!_isRammingEnemyCore)
                {
                    _isRammingEnemyCore = true;
                    _coreRamStallTimer = 0.16f; // Berhenti berputar sejenak saat benturan keras pertama
                    _coreRamSpinTimer = 0.0f;
                    _coreRamAttritionAccumulator = 0.0f;

                    int currentAlive = 0;
                    _world.Query(in _allSwarmQuery, (ref SwarmUnitComponent s) =>
                    {
                        if (s.OwnerId == 0 && s.Role == SwarmRole.DrillDrone && s.State == SwarmState.Launched)
                        {
                            s.ExhaustTriggerTime = -1.0f; // Hentikan auto-exhaust belakang, fokuskan ke tumbukan Core
                            currentAlive++;
                        }
                    });
                    _coreRamInitialDrillerCount = Math.Max(1, currentAlive);

                    // Dorongan inersia awal ke EnemyCore (Core sangat berat: bergeser mundur sedikit tanpa terpental jauh)
                    _enemyCore.Velocity += _gigaDrillAimDir * 115.0f;
                    EmitSignal(SignalName.SwarmClashImpulse, _gigaDrillTipPos, 1650.0f, 220.0f);
                }

                // Kunci ujung bor menempel di permukaan Core (TIDAK menembus masuk!)
                // dan bergerak mengikuti pergeseran inersia berat Core
                _gigaDrillVel = Vector2.Zero;
                _gigaDrillTipPos = _enemyCore.GlobalPosition - _gigaDrillAimDir * coreContactDist;
                _gigaDrillFlightTimer = 0.5f;

                if (_coreRamStallTimer > 0.0f)
                {
                    // FASE A: Berhenti berputar sejenak akibat impak keras menahan beban Core
                    _coreRamStallTimer -= dt;
                    _enemyCore.Velocity += _gigaDrillAimDir * (135.0f * dt);
                    bool ejectedNow = _enemyCore.TakeDamage(950.0f * dt, _gigaDrillAimDir);
                    if (ejectedNow || _enemyCore.Health <= 0.0f)
                    {
                        TriggerGigaDrillCoreSupernova(_enemyCore, applyFinalDamage: !ejectedNow);
                        return;
                    }

                    for (int s = 0; s < 2; s++)
                    {
                        Vector2 sparkJitter = new Vector2((GD.Randf() - 0.5f) * 18.0f, (GD.Randf() - 0.5f) * 18.0f);
                        TriggerClashAnimation(_gigaDrillTipPos + sparkJitter, dropScrap: false);
                    }
                }
                else
                {
                    // FASE B: 0.5 Detik Berputar Kencang Selama Sisa Driller Menggerus Core
                    _coreRamSpinTimer += dt;
                    _gigaDrillSpinTime += dt * 56.0f;

                    // Inersia berat terus menekan Core perlahan
                    _enemyCore.Velocity += _gigaDrillAimDir * (95.0f * dt);
                    bool ejectedDuringSpin = _enemyCore.TakeDamage(2600.0f * dt, _gigaDrillAimDir);
                    if (ejectedDuringSpin)
                    {
                        TriggerGigaDrillCoreSupernova(_enemyCore, applyFinalDamage: false);
                        return;
                    }

                    for (int s = 0; s < 4; s++)
                    {
                        Vector2 sparkJitter = new Vector2((GD.Randf() - 0.5f) * 24.0f, (GD.Randf() - 0.5f) * 24.0f);
                        TriggerClashAnimation(_gigaDrillTipPos + sparkJitter, dropScrap: false);
                    }

                    // Kuras driller selama 0.5 detik hingga hampir habis
                    float wearRatePerSec = MathF.Max(3.5f, (_coreRamInitialDrillerCount * 0.82f) / 0.50f);
                    _coreRamAttritionAccumulator += wearRatePerSec * dt;
                    while (_coreRamAttritionAccumulator >= 1.0f)
                    {
                        _coreRamAttritionAccumulator -= 1.0f;
                        WearDownFrontDriller();
                    }

                    int remainingDrillers = 0;
                    _world.Query(in _allSwarmQuery, (ref SwarmUnitComponent s) =>
                    {
                        if (s.OwnerId == 0 && s.Role == SwarmRole.DrillDrone && s.State == SwarmState.Launched)
                        {
                            remainingDrillers++;
                        }
                    });

                    int almostEmptyThreshold = Math.Max(1, (int)MathF.Ceiling(_coreRamInitialDrillerCount * 0.15f));

                    // FASE C: Jika sudah 0.5 detik berputar ATAU driller hampir habis -> MELEDAK SPEKTAKULER!
                    if (_coreRamSpinTimer >= 0.50f || remainingDrillers <= almostEmptyThreshold || _enemyCore.Health <= 0.0f)
                    {
                        TriggerGigaDrillCoreSupernova(_enemyCore);
                        return;
                    }
                }
            }
        }

        // Jika tidak sedang menempel di Core, cek pergerakan maju dengan Swept Raycast terhadap kulit pinggir Asteroid
        if (!_isRammingEnemyCore)
        {
            _gigaDrillFlightTimer -= dt;
            _gigaDrillElapsedFlight += dt;
            _gigaDrillSpinTime += dt * (_isGrindingAsteroid ? 52.0f : 44.0f);

            if (!_isGrindingAsteroid)
            {
                Vector2 nextTip = _gigaDrillTipPos + _gigaDrillVel * dt;
                float sweepDist = _gigaDrillSafeOutsidePos.DistanceTo(nextTip) + 8.0f;

                if (TryRaycastClosestAsteroidVoxel(_gigaDrillSafeOutsidePos, _gigaDrillAimDir, sweepDist, out _, out VoxelRayHit edgeHit))
                {
                    // Kunci tepat di tile kulit pinggir terluar asteroid! Tidak boleh melompat ke tengah batu!
                    _gigaDrillTipPos = edgeHit.WorldCellCenter;
                    _gigaDrillVel = Vector2.Zero;
                }
                else
                {
                    _gigaDrillTipPos = nextTip;
                    _gigaDrillSafeOutsidePos = _gigaDrillTipPos - _gigaDrillAimDir * 24.0f;
                }
            }
        }

        totalUnits = Math.Max(1, _activeDrillFormationCount);
        exhaustStartIdx = Math.Max(1, _gigaDrillExhaustStartIndex);
        ComputeGigaDrillDimensions(totalUnits, out drillLength, out drillBaseRadius, out densityFactor);

        Vector2 tangent = new Vector2(-_gigaDrillAimDir.Y, _gigaDrillAimDir.X);
        Vector2 basePos = _gigaDrillTipPos - _gigaDrillAimDir * drillLength;

        GigaDrillTipPosition = _gigaDrillTipPos;
        GigaDrillBasePosition = basePos;
        GigaDrillDirection = _gigaDrillAimDir;
        GigaDrillLength = drillLength;
        GigaDrillRadius = drillBaseRadius;
        GigaDrillDensityFactor = densityFactor;

        int fluteCount = Math.Clamp(3 + totalUnits / 42, 3, 9);
        int shellCount = totalUnits >= 135 ? 3 : (totalUnits >= 55 ? 2 : 1);

        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
        {
            if (swarm.State != SwarmState.Launched || swarm.OwnerId != 0)
                return;

            if (swarm.Role == SwarmRole.Guardian)
            {
                if (_hasDefenderNoseBreached) return;

                int defTotal = Math.Max(1, _activeDefenderCoreCount);
                float defConeLength = MathF.Min(56.0f, 20.0f + MathF.Sqrt(defTotal) * 6.5f);
                float defBaseRadius = MathF.Min(28.0f, 12.0f + MathF.Sqrt(defTotal) * 3.6f);

                float defT = (defTotal > 1) ? (float)swarm.FormationIndex / (defTotal - 1) : 0.0f;
                float defDistForward = (1.0f - defT) * defConeLength;
                float defR = MathF.Pow(defT, 0.78f) * defBaseRadius;

                int strand = swarm.FormationIndex % (defTotal >= 14 ? 3 : 2);
                float defSpiralAngle = strand * (Mathf.Tau / (defTotal >= 14 ? 3 : 2))
                                     + defT * (Mathf.Tau * 2.0f)
                                     + _gigaDrillSpinTime * 0.95f;

                Vector2 defTargetPos = (_gigaDrillTipPos + _gigaDrillAimDir * defDistForward)
                                     + (tangent * (defR * MathF.Cos(defSpiralAngle)))
                                     + (_gigaDrillAimDir * (0.18f * defR * MathF.Sin(defSpiralAngle)));

                pos.Value = defTargetPos;
                vel.Value = _gigaDrillVel;
                swarm.SpinAngle = _gigaDrillAimDir.Angle() + Mathf.Pi * 0.5f + MathF.Sin(defSpiralAngle) * 0.35f;
                vis.Color = (defT < 0.28f)
                    ? new Color(2.8f, 5.5f, 7.0f, 1.0f)
                    : new Color(0.9f, 3.8f, 5.5f, 1.0f);
                vis.Scale = 1.15f;
                return;
            }

            if (swarm.Role != SwarmRole.DrillDrone || swarm.FormationIndex < 0)
                return;

            if (!_isRammingEnemyCore && !_isGrindingAsteroid && swarm.ExhaustTriggerTime >= 0.0f && _gigaDrillElapsedFlight >= swarm.ExhaustTriggerTime)
            {
                IgniteUnitAsExhaust(ref pos, ref vel, ref swarm, ref vis, basePos, _gigaDrillAimDir, 980.0f);
                _activeDrillFormationCount = Math.Max(1, _activeDrillFormationCount - 1);
                return;
            }

            int idx = Math.Clamp(swarm.FormationIndex, 0, Math.Max(0, totalUnits - 1));
            bool isExhaustStage = idx >= exhaustStartIdx;

            float d;
            float r;
            float spiralAngle;

            if (!isExhaustStage)
            {
                float bodyT = (exhaustStartIdx > 1) ? (float)idx / (exhaustStartIdx - 1) : 0.0f;
                d = bodyT * (drillLength * 0.84f);

                int shell = idx % shellCount;
                float shellRadiusMult = shell switch
                {
                    0 => 1.0f,
                    1 => 0.62f,
                    _ => 0.28f
                };
                float shellSpinDir = (shell == 1) ? -0.75f : 1.0f;

                r = MathF.Pow(bodyT, 0.82f) * drillBaseRadius * shellRadiusMult;
                int flute = idx % fluteCount;
                spiralAngle = flute * (Mathf.Tau / fluteCount)
                            + bodyT * (Mathf.Tau * 3.0f)
                            + _gigaDrillSpinTime * shellSpinDir;
            }
            else
            {
                int exhaustCount = Math.Max(1, totalUnits - exhaustStartIdx);
                float exhaustT = (float)(idx - exhaustStartIdx) / exhaustCount;
                d = drillLength * (0.84f + exhaustT * 0.16f);
                r = drillBaseRadius * (0.55f + 0.45f * exhaustT);

                int nozzleRing = (idx - exhaustStartIdx) % Math.Max(4, fluteCount);
                spiralAngle = nozzleRing * (Mathf.Tau / Math.Max(4, fluteCount)) - _gigaDrillSpinTime * 1.5f;
            }

            Vector2 targetPos = (_gigaDrillTipPos - _gigaDrillAimDir * d)
                              + (tangent * (r * MathF.Cos(spiralAngle)))
                              + (_gigaDrillAimDir * (0.28f * r * MathF.Sin(spiralAngle)));

            pos.Value = targetPos;
            vel.Value = _gigaDrillVel;
            swarm.SpinAngle = _gigaDrillAimDir.Angle() + Mathf.Pi * 0.5f + MathF.Sin(spiralAngle) * 0.48f;

            vis.Color = (idx < Math.Max(4, totalUnits / 12))
                ? new Color(4.2f, 4.0f, 2.6f, 1.0f)
                : new Color(3.5f, 1.25f, 0.4f, 1.0f);
        });

        float killRadiusSq = (drillBaseRadius + 30.0f) * (drillBaseRadius + 30.0f);
        Vector2 noseTip = (!_hasDefenderNoseBreached && _activeDefenderCoreCount > 0)
            ? _gigaDrillTipPos + _gigaDrillAimDir * MathF.Min(56.0f, 20.0f + MathF.Sqrt(_activeDefenderCoreCount) * 6.5f)
            : _gigaDrillTipPos;

        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref SwarmUnitComponent swarm) =>
        {
            if (swarm.State == SwarmState.Dead || swarm.OwnerId == 0) return;
            if (pos.Value.DistanceSquaredTo(_gigaDrillTipPos) <= killRadiusSq
                || pos.Value.DistanceSquaredTo(basePos) <= killRadiusSq
                || pos.Value.DistanceSquaredTo(noseTip) <= killRadiusSq)
            {
                swarm.State = SwarmState.Dead;
            }
        });

        // Cek berapa driller yang masih hidup di formasi Giga Drill
        int aliveDrillers = 0;
        _world.Query(in _allSwarmQuery, (ref SwarmUnitComponent s) =>
        {
            if (s.OwnerId == 0 && s.Role == SwarmRole.DrillDrone && s.State == SwarmState.Launched && s.FormationIndex >= 0)
            {
                aliveDrillers++;
            }
        });

        if (aliveDrillers == 0)
        {
            _isGigaDrillActive = false;
            _isRammingEnemyCore = false;
            _isGrindingAsteroid = false;
            _gigaDrillFlightTimer = 0.0f;
            TriggerClashAnimation(_gigaDrillTipPos, dropScrap: true);
            EmitSignal(SignalName.GigaDrillDetonated, _gigaDrillTipPos, basePos, 0);
            return;
        }

        // Jika waktu terbang habis di ruang hampa (MISS: tidak menabrak Core dan tidak menabrak Asteroid)
        // Sesuai aturan: drone yang MISS dan tidak menabrak apapun kembali ke orbit (Returning)!
        if (!_isRammingEnemyCore && !_isGrindingAsteroid && _gigaDrillFlightTimer <= 0.0f)
        {
            _isGigaDrillActive = false;
            EmitSignal(SignalName.GigaDrillDetonated, _gigaDrillTipPos, basePos, 0);

            _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
            {
                if (swarm.State == SwarmState.Launched && swarm.OwnerId == 0)
                {
                    if (swarm.Role == SwarmRole.DrillDrone || swarm.Role == SwarmRole.Guardian)
                    {
                        swarm.State = SwarmState.Returning;
                        swarm.FormationIndex = -1;
                        swarm.ExhaustTriggerTime = -1.0f;
                        swarm.LifeTime = 0.0f;
                        float scatter = (GD.Randf() - 0.5f) * Mathf.Tau;
                        vel.Value = new Vector2(MathF.Cos(scatter), MathF.Sin(scatter)) * 420.0f;
                    }
                }
            });
            _activeDefenderCoreCount = 0;
            _activeDrillFormationCount = 0;
            RefreshFoundryGui();
        }
    }

    private void UpdateExhaustDrones(float dt)
    {
        float drag = MathF.Pow(0.92f, dt * 60.0f);
        bool anyDied = false;

        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
        {
            if (swarm.State != SwarmState.DrillExhaust) return;

            swarm.LifeTime += dt;
            float lifeRatio = Mathf.Clamp(swarm.LifeTime / MathF.Max(0.05f, swarm.MaxLifeTime), 0.0f, 1.0f);

            Vector2 perp = new Vector2(-swarm.TargetDirection.Y, swarm.TargetDirection.X);
            float flutter = MathF.Sin(swarm.LifeTime * 48.0f + swarm.FormationIndex * 2.3f) * 320.0f;
            vel.Value = (vel.Value + perp * (flutter * dt)) * drag;
            pos.Value += vel.Value * dt;
            swarm.SpinAngle += 28.0f * dt;

            if (lifeRatio < 0.30f)
            {
                float u = lifeRatio / 0.30f;
                vis.Color = new Color(
                    Mathf.Lerp(4.2f, 3.8f, u),
                    Mathf.Lerp(3.8f, 1.6f, u),
                    Mathf.Lerp(2.8f, 0.35f, u),
                    1.0f
                );
                vis.Scale = Mathf.Lerp(1.42f, 1.10f, u);
            }
            else if (lifeRatio < 0.72f)
            {
                float u = (lifeRatio - 0.30f) / 0.42f;
                vis.Color = new Color(
                    Mathf.Lerp(3.8f, 2.6f, u),
                    Mathf.Lerp(1.6f, 0.35f, u),
                    Mathf.Lerp(0.35f, 0.65f, u),
                    Mathf.Lerp(1.0f, 0.75f, u)
                );
                vis.Scale = Mathf.Lerp(1.10f, 0.55f, u);
            }
            else
            {
                float u = (lifeRatio - 0.72f) / 0.28f;
                vis.Color = new Color(
                    Mathf.Lerp(2.6f, 0.8f, u),
                    Mathf.Lerp(0.35f, 0.05f, u),
                    Mathf.Lerp(0.65f, 0.25f, u),
                    Mathf.Lerp(0.75f, 0.0f, u)
                );
                vis.Scale = Mathf.Lerp(0.55f, 0.12f, u);
            }

            if (swarm.LifeTime >= swarm.MaxLifeTime)
            {
                swarm.State = SwarmState.Dead;
                if (swarm.OwnerId == 0)
                {
                    _entityCount = Math.Max(0, _entityCount - 1);
                    IncrementRoleCounter(swarm.Role, -1);
                    anyDied = true;
                }
            }
        });

        if (anyDied)
        {
            RefreshFoundryGui();
        }
    }

    /// <summary>
    /// Mekanik Tabrakan Super Giga Drill ke Asteroid (dengan Tile HP Masif):
    /// - Tidak lagi memotong instan lewat CarveVoxelCircle setiap frame.
    /// - Menggerus terus di permukaan batu (ApplyDrillGrindAtRadius) sambil mengauskan driller satu per satu.
    /// - Begitu driller habis, meledak kecil dan membuat lubang/kawah lokal di permukaan asteroid.
    /// </summary>
    /// <summary>
    /// Menembakkan raycast dari titik aman di luar asteroid (_gigaDrillSafeOutsidePos) searah aimDir
    /// untuk menemukan tile padat PALING PINGGIR (kulit luar) yang pertama kali disentuh mata bor.
    /// </summary>
    private static bool TryRaycastClosestAsteroidVoxel(
        Vector2 rayOrigin,
        Vector2 aimDir,
        float maxDist,
        out Asteroid? hitAsteroid,
        out VoxelRayHit bestHit)
    {
        hitAsteroid = null;
        bestHit = default;
        float closestDist = maxDist;

        var asteroids = Asteroid.ActiveAsteroids;
        for (int i = 0; i < asteroids.Count; i++)
        {
            var ast = asteroids[i];
            if (ast == null || !ast.IsActiveInTree) continue;

            if (ast.RaycastVoxel(rayOrigin, aimDir, closestDist, out VoxelRayHit vHit))
            {
                if (vHit.Hit && vHit.Distance < closestDist)
                {
                    closestDist = vHit.Distance;
                    hitAsteroid = ast;
                    bestHit = vHit;
                }
            }
        }

        return hitAsteroid != null && bestHit.Hit;
    }

    private void CheckAsteroidCollisions(float dt)
    {
        if (!_isGigaDrillActive || _isRammingEnemyCore) return;

        // Cari tile padat paling pinggir di sepanjang jalur mata bor dari titik luar (_gigaDrillSafeOutsidePos)
        float maxScanDist = _gigaDrillSafeOutsidePos.DistanceTo(_gigaDrillTipPos) + 18.0f;
        Asteroid? targetAst = null;
        Vector2 grindCenter = _gigaDrillTipPos;

        if (TryRaycastClosestAsteroidVoxel(_gigaDrillSafeOutsidePos, _gigaDrillAimDir, maxScanDist, out Asteroid? rayAst, out VoxelRayHit rayHit))
        {
            targetAst = rayAst;
            grindCenter = rayHit.WorldCellCenter;
        }
        else
        {
            // Fallback: cek apakah sisi samping mata bor menyentuh tile pinggir asteroid
            var asteroids = Asteroid.ActiveAsteroids;
            for (int i = 0; i < asteroids.Count; i++)
            {
                var ast = asteroids[i];
                if (ast == null || !ast.IsActiveInTree) continue;
                if (ast.HasSolidVoxelsInRadius(_gigaDrillTipPos, GigaDrillRadius * 0.62f, out Vector2 closestVox))
                {
                    targetAst = ast;
                    grindCenter = closestVox;
                    break;
                }
            }
        }

        if (targetAst != null)
        {
            if (!_hasDefenderNoseBreached && _activeDefenderCoreCount > 0)
            {
                float defConeLength = MathF.Min(56.0f, 20.0f + MathF.Sqrt(_activeDefenderCoreCount) * 6.5f);
                TriggerNoseShieldBreach(defConeLength);
            }

            if (!_isGrindingAsteroid)
            {
                _isGrindingAsteroid = true;
                // Matikan timer exhaust belakang agar seluruh sisa driller dipakai menggerus batu sampai habis
                _world.Query(in _allSwarmQuery, (ref SwarmUnitComponent s) =>
                {
                    if (s.OwnerId == 0 && s.Role == SwarmRole.DrillDrone && s.State == SwarmState.Launched)
                    {
                        s.ExhaustTriggerTime = -1.0f;
                    }
                });
            }

            // KUNCI UJUNG BOR TEPAT DI PERMUKAAN TILE PINGGIR!
            // Bor berhenti (_gigaDrillVel = Zero) dan hanya maju 1 tile ke dalam SETELAH tile pinggir di depannya pecah!
            _gigaDrillVel = Vector2.Zero;
            _gigaDrillTipPos = _gigaDrillTipPos.Lerp(grindCenter, MathF.Min(1.0f, 35.0f * dt));
            _gigaDrillFlightTimer = MathF.Max(_gigaDrillFlightTimer, 0.35f);

            // Gerus tile pinggir di titik kontak melawan NonLaserTileMaxHp (45.0f)
            // Radius gerusan dibuat terfokus di ujung mata bor agar mengikis dari tepi luar ke dalam secara berurutan
            float focusedGrindRadius = Mathf.Clamp(GigaDrillRadius * 0.65f, 18.0f, 30.0f);
            _destroyedVoxelsBuffer.Clear();
            targetAst.ApplyDrillGrindAtRadius(grindCenter, focusedGrindRadius, 62.0f, dt, _destroyedVoxelsBuffer);
            for (int v = 0; v < _destroyedVoxelsBuffer.Count; v++)
            {
                var broken = _destroyedVoxelsBuffer[v];
                SpawnVoxelErosionDebris(broken.worldCenter, -_gigaDrillAimDir, broken.type);
            }

            // Gaya dorong fisik ke asteroid
            targetAst.ApplyCentralImpulse(_gigaDrillAimDir * (28000.0f * dt));
            targetAst.ApplyTorqueImpulse((GD.Randf() > 0.5f ? 1.0f : -1.0f) * 12000.0f * dt);

            // Percikan bunga api penggalian batuan tepat di permukaan pinggir
            for (int s = 0; s < 2; s++)
            {
                Vector2 sparkJitter = new Vector2((GD.Randf() - 0.5f) * 14.0f, (GD.Randf() - 0.5f) * 14.0f);
                TriggerClashAnimation(grindCenter + sparkJitter, dropScrap: false);
            }

            // Auskan driller terus-menerus sampai driller habis
            _drillAttritionTimer += dt;
            if (_drillAttritionTimer >= 0.055f)
            {
                _drillAttritionTimer = 0.0f;
                WearDownFrontDriller();
            }

            int remainingDrillers = 0;
            _world.Query(in _allSwarmQuery, (ref SwarmUnitComponent s) =>
            {
                if (s.OwnerId == 0 && s.Role == SwarmRole.DrillDrone && s.State == SwarmState.Launched && s.FormationIndex >= 0)
                {
                    remainingDrillers++;
                }
            });

            // Begitu driller habis -> Meledak kecil tepat di mulut coakan pinggir yang sedang digerus!
            if (remainingDrillers <= 1)
            {
                _gigaDrillTipPos = grindCenter;
                TriggerGigaDrillAsteroidSmallBlast(targetAst);
                return;
            }
        }
        else
        {
            _isGrindingAsteroid = false;
            _drillAsteroidGrindTimer = MathF.Max(0.0f, _drillAsteroidGrindTimer - dt * 2.0f);
        }
    }

    private void WearDownFrontDriller()
    {
        int minIdx = int.MaxValue;
        Entity targetEnt = Entity.Null;
        Vector2 targetPos = Vector2.Zero;

        _world.Query(in _allSwarmQuery, (Entity ent, ref PositionComponent pos, ref SwarmUnitComponent swarm) =>
        {
            if (swarm.OwnerId == 0 && swarm.Role == SwarmRole.DrillDrone && swarm.State == SwarmState.Launched && swarm.FormationIndex >= 0)
            {
                if (swarm.FormationIndex < minIdx)
                {
                    minIdx = swarm.FormationIndex;
                    targetEnt = ent;
                    targetPos = pos.Value;
                }
            }
        });

        if (targetEnt != Entity.Null && _world.IsAlive(targetEnt))
        {
            ref var swarm = ref _world.Get<SwarmUnitComponent>(targetEnt);
            ref var vis = ref _world.Get<VisualComponent>(targetEnt);
            swarm.State = SwarmState.Dead;
            vis.Color = new Color(0, 0, 0, 0);

            TriggerClashAnimation(targetPos, dropScrap: true);
            _entityCount = Math.Max(0, _entityCount - 1);
            IncrementRoleCounter(SwarmRole.DrillDrone, -1);
            _activeDrillFormationCount = Math.Max(0, _activeDrillFormationCount - 1);
            RefreshFoundryGui();
        }
    }

    /// <summary>
    /// Ledakan kecil akhir ketika seluruh driller habis menggerus permukaan asteroid:
    /// Menciptakan lubang/kawah lokal pada titik gerusan tanpa membelah tembus asteroid.
    /// </summary>
    private void TriggerGigaDrillAsteroidSmallBlast(Asteroid hitAsteroid)
    {
        _isGigaDrillActive = false;
        _isGrindingAsteroid = false;
        _isRammingEnemyCore = false;
        _gigaDrillFlightTimer = 0.0f;
        _drillAsteroidGrindTimer = 0.0f;

        Vector2 detPos = _gigaDrillTipPos;
        Vector2 basePos = GigaDrillBasePosition;

        if (hitAsteroid != null && IsInstanceValid(hitAsteroid))
        {
            float holeRadius = Mathf.Clamp(GigaDrillRadius * 0.85f, 22.0f, 36.0f);
            _destroyedVoxelsBuffer.Clear();
            hitAsteroid.CarveVoxelCircle(detPos, holeRadius, _destroyedVoxelsBuffer);
            for (int v = 0; v < _destroyedVoxelsBuffer.Count; v++)
            {
                var broken = _destroyedVoxelsBuffer[v];
                SpawnVoxelErosionDebris(broken.worldCenter, -_gigaDrillAimDir, broken.type);
            }
            hitAsteroid.ApplyCentralImpulse(_gigaDrillAimDir * 42000.0f);
        }

        // Habiskan sisa driller terakhir yang meledak di dalam kawah batu
        _world.Query(in _allSwarmQuery, (ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
        {
            if (swarm.OwnerId == 0 && swarm.State == SwarmState.Launched && (swarm.Role == SwarmRole.DrillDrone || swarm.Role == SwarmRole.Guardian))
            {
                swarm.State = SwarmState.Dead;
                vis.Color = new Color(0, 0, 0, 0);
            }
        });

        for (int i = 0; i < 6; i++)
        {
            float r = GD.Randf() * 24.0f;
            float a = GD.Randf() * Mathf.Tau;
            Vector2 p = detPos + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r;
            TriggerClashAnimation(p, dropScrap: (i % 2 == 0));
        }

        _activeDefenderCoreCount = 0;
        _activeDrillFormationCount = 0;

        // Nilai negatif (-1) menandakan ledakan kecil pembuat lubang asteroid ke Arena & GigaDrillVFX
        EmitSignal(SignalName.GigaDrillDetonated, detPos, basePos, -1);
        RefreshFoundryGui();
    }

    /// <summary>
    /// Ledakan Spektakuler Supernova (Grid Rupture + Multi-Ring Blast) saat Giga Drill selesai berputar 0.5 detik
    /// dan driller hampir habis di permukaan Enemy Core!
    /// </summary>
    private void TriggerGigaDrillCoreSupernova(EnemyCore hitEnemyCore, bool applyFinalDamage = true)
    {
        _isGigaDrillActive = false;
        _isRammingEnemyCore = false;
        _isGrindingAsteroid = false;
        _gigaDrillFlightTimer = 0.0f;
        _drillEnemyGrindTimer = 0.0f;

        Vector2 detPos = _gigaDrillTipPos;
        Vector2 basePos = GigaDrillBasePosition;

        int remainingSacrificed = 0;
        _world.Query(in _allSwarmQuery, (ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
        {
            if (swarm.OwnerId == 0 && swarm.State == SwarmState.Launched && (swarm.Role == SwarmRole.DrillDrone || swarm.Role == SwarmRole.Guardian))
            {
                swarm.State = SwarmState.Dead;
                vis.Color = new Color(0, 0, 0, 0);
                remainingSacrificed++;
            }
        });

        int totalPowerCount = Math.Max(16, _coreRamInitialDrillerCount + remainingSacrificed);

        if (hitEnemyCore != null && IsInstanceValid(hitEnemyCore))
        {
            if (applyFinalDamage)
            {
                bool ejectedNow = hitEnemyCore.TakeDamage(1600.0f + _coreRamInitialDrillerCount * 28.0f, _gigaDrillAimDir);
                // Hanya berikan dorongan inersia berat jika belum berubah menjadi Escape Pod yang sedang lari cepat!
                if (!ejectedNow && !hitEnemyCore.IsEscapePod)
                {
                    hitEnemyCore.Velocity += _gigaDrillAimDir * 135.0f;
                }
            }
        }

        // 26 Semburan bunga api & kepingan scrap menyebar radial di sekitar titik ledakan Core
        for (int i = 0; i < 26; i++)
        {
            float r = GD.Randf() * (GigaDrillRadius * 2.2f);
            float a = GD.Randf() * Mathf.Tau;
            Vector2 p = detPos + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r;
            TriggerClashAnimation(p, dropScrap: (i % 3 == 0));
        }

        _activeDefenderCoreCount = 0;
        _activeDrillFormationCount = 0;

        EmitSignal(SignalName.GigaDrillDetonated, detPos, basePos, totalPowerCount);
        RefreshFoundryGui();
    }

    private void OnPlayerEscapePodEjected(Vector2 ejectionPos, Vector2 ejectionVel)
    {
        if (_isBoostActive) DeactivateDartBoost();
        if (_isBrakeActive) DeactivateDartBrake();

        // Hancurkan sisa swarm lama saat sarang lebah hancur sehingga menyisakan 1 Core + 1 Miner otomatis
        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
        {
            if (swarm.OwnerId == 0 && swarm.State != SwarmState.Dead)
            {
                swarm.State = SwarmState.Dead;
                vis.Color = new Color(0, 0, 0, 0);
            }
        });

        for (int i = 0; i < 18; i++)
        {
            Vector2 p = ejectionPos + new Vector2((GD.Randf() - 0.5f) * 56.0f, (GD.Randf() - 0.5f) * 56.0f);
            TriggerClashAnimation(p, dropScrap: true);
        }

        // Gelombang kejut lontaran Escape Pod pada grid
        EmitSignal(SignalName.SwarmClashImpulse, ejectionPos, 2100.0f, 290.0f);

        // Otomatis memproduksi 1 Miner yang ikut melesat bersama Escape Pod pemain
        SpawnEmergencyMiner(0);
        RefreshFoundryGui();
    }

    private void OnPlayerCoreDestroyed()
    {
        if (_playerCore == null) return;
        for (int i = 0; i < 24; i++)
        {
            Vector2 p = _playerCore.GlobalPosition + new Vector2((GD.Randf() - 0.5f) * 64.0f, (GD.Randf() - 0.5f) * 64.0f);
            TriggerClashAnimation(p, dropScrap: true);
        }
    }

    private void OnEnemyEscapePodEjected(Vector2 ejectionPos, Vector2 ejectionVel)
    {
        // Hancurkan sisa swarm pengorbit sarang lama musuh saat sarang induk pecah
        _world.Query(in _allSwarmQuery, (ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
        {
            if (swarm.OwnerId == 1 && swarm.State == SwarmState.Orbiting)
            {
                swarm.State = SwarmState.Dead;
                vis.Color = new Color(0, 0, 0, 0);
            }
        });

        for (int i = 0; i < 18; i++)
        {
            Vector2 p = ejectionPos + new Vector2((GD.Randf() - 0.5f) * 56.0f, (GD.Randf() - 0.5f) * 56.0f);
            TriggerClashAnimation(p, dropScrap: true);
        }

        // Gelombang kejut lontaran Nemesis Escape Pod pada grid
        EmitSignal(SignalName.SwarmClashImpulse, ejectionPos, 2100.0f, 290.0f);

        // Otomatis memproduksi 1 Miner untuk Escape Pod musuh yang baru terlontar cepat
        SpawnEmergencyMiner(1);
    }

    private void OnEnemyCoreDestroyed()
    {
        if (_enemyCore == null) return;
        for (int i = 0; i < 28; i++)
        {
            Vector2 p = _enemyCore.GlobalPosition + new Vector2((GD.Randf() - 0.5f) * 72.0f, (GD.Randf() - 0.5f) * 72.0f);
            TriggerClashAnimation(p, dropScrap: true);
        }
    }

    public void SpawnEmergencyMiner(int ownerId)
    {
        if (ownerId == 0)
        {
            AddSpecificSwarm(SwarmRole.MiningDroid, 1);
        }
        else
        {
            if (_enemyCore == null || !IsInstanceValid(_enemyCore)) return;
            Vector2 enemyCorePos = _enemyCore.GlobalPosition;
            Vector2 enemyVel = _enemyCore.Velocity;
            Color minerColor = new Color(3.5f, 1.8f, 0.4f, 1.0f);

            // Trigger efek visual hologram / kilatan fabrikasi dan gunakan ulang entitas Dead bila ada
            float orbitDist = 36.0f;
            float orbitSpd = 1.8f;
            float angle = GD.Randf() * Mathf.Tau;
            Vector2 offset = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 18.0f;

            bool reused = false;
            _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
            {
                if (reused || swarm.State != SwarmState.Dead) return;
                pos.Value = enemyCorePos + offset;
                vel.Value = enemyVel;
                swarm = new SwarmUnitComponent(SwarmRole.MiningDroid, ownerId: 1, orbitDist, orbitSpd)
                {
                    State = SwarmState.Orbiting,
                    MaxLifeTime = 9999.0f
                };
                vis = new VisualComponent(minerColor, 0.68f);
                reused = true;
            });

            if (!reused)
            {
                var pos = new PositionComponent(enemyCorePos + offset);
                var vel = new VelocityComponent(enemyVel);
                var unit = new SwarmUnitComponent(SwarmRole.MiningDroid, ownerId: 1, orbitDist, orbitSpd)
                {
                    State = SwarmState.Orbiting,
                    MaxLifeTime = 9999.0f
                };
                var vis = new VisualComponent(minerColor, 0.68f);

                _world.Create(pos, vel, unit, vis);
            }
        }
    }

    // =========================================================================
    // LASER MULTI-BLOK DINAMIS (15+ MINER -> LEBAR > 1 BLOK, 30+ -> > 2 BLOK, DST)
    // =========================================================================

    private void UpdateMiningLaser(float dt, Vector2 corePos, Vector2 mousePos)
    {
        _activeMinerPositions.Clear();
        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref SwarmUnitComponent swarm) =>
        {
            if (swarm.OwnerId == 0 && swarm.State == SwarmState.Orbiting && swarm.Role == SwarmRole.MiningDroid)
            {
                _activeMinerPositions.Add(pos.Value);
            }
        });

        int minerCount = _activeMinerPositions.Count;
        if (minerCount == 0)
        {
            if (_isMiningLaserActive)
            {
                _isMiningLaserActive = false;
                _laserActiveHitBoxes.Clear();
                QueueRedraw();
            }
            return;
        }

        _isMiningLaserActive = true;
        _laserPrimingTimer += dt;
        bool isPriming = _laserPrimingTimer < LaserPrimingDuration;
        float primingProgress = Mathf.Clamp(_laserPrimingTimer / LaserPrimingDuration, 0.0f, 1.0f);

        if (isPriming)
        {
            _laserFocusWarmup = primingProgress * 0.42f;
        }
        else
        {
            _laserFocusWarmup = MathF.Min(1.0f, _laserFocusWarmup + dt * 6.0f);
        }

        float freqMultiplier = 1.0f + minerCount * 0.14f;
        _laserPulseTimer += dt * freqMultiplier;

        Vector2 toMouse = mousePos - corePos;
        Vector2 aimDir = toMouse.LengthSquared() > 1.0f ? toMouse.Normalized() : Vector2.Right;
        Vector2 tangent = new Vector2(-aimDir.Y, aimDir.X);
        _laserAimDir = aimDir;

        _currentLaserBlockSpan = Math.Clamp(1 + (minerCount / 15), 1, 5);
        int remainderInTier = minerCount % 15;
        float tierProgress = remainderInTier / 14.0f;

        float targetWidth = _currentLaserBlockSpan switch
        {
            1 => Mathf.Lerp(3.2f, 5.0f, tierProgress),
            2 => Mathf.Lerp(15.5f, 19.5f, tierProgress),
            3 => Mathf.Lerp(28.0f, 33.0f, tierProgress),
            4 => Mathf.Lerp(40.5f, 46.0f, tierProgress),
            _ => Mathf.Lerp(53.0f, 58.0f, tierProgress)
        };
        _currentVisualBeamWidth = isPriming ? 1.4f : Mathf.Lerp(1.5f, targetWidth, _laserFocusWarmup);

        Vector2 rayBaseOrigin = corePos + aimDir * 14.0f;
        float maxRange = 2600.0f;

        int subRayCount = (_currentLaserBlockSpan == 1) ? 1 : (_currentLaserBlockSpan * 2 - 1);
        float totalLateralSpan = (_currentLaserBlockSpan - 1) * (Asteroid.VoxelSize * 0.92f);

        _laserActiveHitBoxes.Clear();
        float closestCenterDist = maxRange;
        Vector2 centerHitPoint = rayBaseOrigin + aimDir * maxRange;
        bool anyRayHit = false;
        _isMiningEnergyAsteroid = false;

        var asteroids = Asteroid.ActiveAsteroids;
        float erosionBlocksPerSec = 2.8f + minerCount * 0.95f;
        float heatDeltaPerSubRay = (erosionBlocksPerSec * dt) / MathF.Max(1.0f, subRayCount * 0.42f);

        for (int r = 0; r < subRayCount; r++)
        {
            float lateralOffset = (subRayCount == 1)
                ? 0.0f
                : Mathf.Lerp(-totalLateralSpan * 0.5f, totalLateralSpan * 0.5f, (float)r / (subRayCount - 1));

            Vector2 subOrigin = rayBaseOrigin + tangent * lateralOffset;
            float subClosest = maxRange;
            Asteroid? subHitAst = null;
            VoxelRayHit subBestHit = default;

            for (int i = 0; i < asteroids.Count; i++)
            {
                var ast = asteroids[i];
                if (ast == null || !ast.IsActiveInTree) continue;

                if (ast.RaycastVoxel(subOrigin, aimDir, subClosest, out VoxelRayHit vHit))
                {
                    if (vHit.Distance < subClosest)
                    {
                        subClosest = vHit.Distance;
                        subHitAst = ast;
                        subBestHit = vHit;
                    }
                }
            }

            if (subHitAst != null && subBestHit.Hit)
            {
                anyRayHit = true;
                if (subHitAst.IsEnergyAsteroid)
                {
                    _isMiningEnergyAsteroid = true;
                }
                if (subClosest < closestCenterDist)
                {
                    closestCenterDist = subClosest;
                    centerHitPoint = subOrigin + aimDir * subClosest - tangent * lateralOffset;
                    _laserHitAsteroidRotation = subHitAst.CachedRotation;
                }

                // Erosi batuan asteroid HANYA terjadi setelah tahap priming selesai!
                if (!isPriming)
                {
                    bool destroyed = subHitAst.ApplyLaserHeatAtVoxel(
                        subBestHit.GridX,
                        subBestHit.GridY,
                        heatDeltaPerSubRay,
                        aimDir,
                        out VoxelType brokenType,
                        out Vector2 brokenWorldCenter,
                        out float updatedHeat
                    );

                    _laserActiveHitBoxes.Add((subBestHit.WorldCellCenter, updatedHeat, subBestHit.Type));

                    if (destroyed)
                    {
                        SpawnVoxelErosionDebris(brokenWorldCenter, -aimDir, brokenType);
                    }
                }
            }
        }

        float focusDist = MathF.Min(48.0f, MathF.Max(16.0f, closestCenterDist * 0.65f));
        _laserConvergencePoint = corePos + aimDir * focusDist;
        _laserImpactPoint = anyRayHit ? centerHitPoint : (_laserConvergencePoint + aimDir * maxRange);

        // Update Partikel Plasma Siphoning (mengalir mundur dari Bintang Mini ke kapal)
        if (_isMiningEnergyAsteroid && anyRayHit && !isPriming && _plasmaSiphonWisps.Count < 36)
        {
            for (int p = 0; p < 2; p++)
            {
                Color wispColor = (GD.Randf() < 0.65f)
                    ? new Color(3.8f, 3.2f, 1.15f, 0.95f)
                    : new Color(0.4f, 2.5f, 3.9f, 0.90f);
                _plasmaSiphonWisps.Add(new PlasmaSiphonWisp
                {
                    Progress = GD.Randf() * 0.05f,
                    Speed = 1.35f + GD.Randf() * 0.95f,
                    Radius = 5.0f + GD.Randf() * 12.0f,
                    Angle = GD.Randf() * Mathf.Tau,
                    AngleSpeed = (GD.Randf() > 0.5f ? 1.0f : -1.0f) * (14.0f + GD.Randf() * 12.0f),
                    Color = wispColor,
                    Size = 2.4f + GD.Randf() * 2.0f
                });
            }
        }

        for (int w = _plasmaSiphonWisps.Count - 1; w >= 0; w--)
        {
            var wisp = _plasmaSiphonWisps[w];
            wisp.Progress += dt * wisp.Speed;
            wisp.Angle += dt * wisp.AngleSpeed;
            if (wisp.Progress >= 1.0f)
            {
                _plasmaSiphonWisps.RemoveAt(w);
            }
            else
            {
                _plasmaSiphonWisps[w] = wisp;
            }
        }

        QueueRedraw();
    }

    public void SpawnVoxelErosionDebris(Vector2 worldCenter, Vector2 outNormal, VoxelType brokenType)
    {
        if (brokenType == VoxelType.Rock)
        {
            // Puing batu kosmik: 2 serpihan debu/gravel cepat pudar (0.22 - 0.35 detik)
            for (int i = 0; i < 2; i++)
            {
                float spread = (GD.Randf() - 0.5f) * 1.35f;
                Vector2 dir = outNormal.Rotated(spread);
                float speed = 40.0f + GD.Randf() * 75.0f;
                float life = 0.22f + GD.Randf() * 0.15f;

                _miningDebris.Add(new MiningDebrisParticle
                {
                    Position = worldCenter + new Vector2((GD.Randf() - 0.5f) * 4.0f, (GD.Randf() - 0.5f) * 4.0f),
                    Velocity = dir * speed,
                    Color = new Color(0.50f, 0.45f, 0.42f, 0.70f).Lerp(new Color(1.4f, 0.80f, 0.35f, 0.80f), GD.Randf() * 0.35f),
                    Life = life,
                    MaxLife = life,
                    Size = 1.5f + GD.Randf() * 1.2f,
                    IsCrystalMatter = false,
                    IsQuantumGlass = false,
                    IsPlasmaPuff = false
                });
            }
        }
        else if (brokenType == VoxelType.ResourceAether)
        {
            // Resource Aether (Sangat Langka):
            // 1 serpihan Kaca Kuantum Bercahaya (+ 25% peluang pecahan bonus kedua)
            int nuggetCount = (GD.Randf() < 0.25f) ? 2 : 1;
            for (int i = 0; i < nuggetCount; i++)
            {
                float spread = (GD.Randf() - 0.5f) * 1.8f;
                Vector2 dir = outNormal.Rotated(spread);
                float speed = 65.0f + GD.Randf() * 95.0f;
                float life = 24.0f + GD.Randf() * 8.0f; // BERTAHAN LAMA (24 - 32 DETIK)!
                float size = 4.0f + GD.Randf() * 2.0f;

                Color prismCol = (GD.Randf() > 0.35f)
                    ? new Color(4.8f, 4.0f, 1.5f, 1.0f)   // Aether Gold-White
                    : new Color(0.9f, 3.5f, 5.2f, 1.0f);  // Refractive Cyan Flash

                _miningDebris.Add(new MiningDebrisParticle
                {
                    Position = worldCenter + new Vector2((GD.Randf() - 0.5f) * 5.0f, (GD.Randf() - 0.5f) * 5.0f),
                    Velocity = dir * speed,
                    Color = prismCol,
                    Life = life,
                    MaxLife = life,
                    Size = size,
                    IsCrystalMatter = true,
                    MatterType = ResourceType.Aether,
                    Rotation = GD.Randf() * Mathf.Tau,
                    RotSpeed = (GD.Randf() - 0.5f) * 35.0f,
                    IsQuantumGlass = true,
                    IsPlasmaPuff = false
                });
            }

            // 1 Semburan Gas Plasma Ethereal (VFX sekejap)
            _miningDebris.Add(new MiningDebrisParticle
            {
                Position = worldCenter,
                Velocity = outNormal * 32.0f,
                Color = new Color(3.6f, 2.5f, 0.8f, 0.65f),
                Life = 0.30f,
                MaxLife = 0.30f,
                Size = 5.0f,
                IsCrystalMatter = false,
                IsQuantumGlass = false,
                IsPlasmaPuff = true
            });
        }
        else if (Asteroid.TryGetResourceType(brokenType, out ResourceType resType))
        {
            // Resource Mineral Padat (Quartz, Ferrite, Carbide, Vortexum):
            // 1 serpihan kristal padat (+ 20% peluang pecahan bonus kedua)
            int nuggetCount = (GD.Randf() < 0.20f) ? 2 : 1;
            Color resCol = Asteroid.GetResourceColor(resType);

            for (int i = 0; i < nuggetCount; i++)
            {
                float spread = (GD.Randf() - 0.5f) * 0.55f;
                Vector2 dir = outNormal.Rotated(spread);
                float speed = 145.0f + GD.Randf() * 115.0f;
                float life = 24.0f + GD.Randf() * 8.0f; // BERTAHAN LAMA (24 - 32 DETIK)!

                _miningDebris.Add(new MiningDebrisParticle
                {
                    Position = worldCenter + new Vector2((GD.Randf() - 0.5f) * 4.0f, (GD.Randf() - 0.5f) * 4.0f),
                    Velocity = dir * speed,
                    Color = resCol,
                    Life = life,
                    MaxLife = life,
                    Size = 3.6f,
                    IsCrystalMatter = true,
                    MatterType = resType,
                    Rotation = GD.Randf() * Mathf.Tau,
                    RotSpeed = (GD.Randf() - 0.5f) * 18.0f,
                    IsQuantumGlass = false,
                    IsPlasmaPuff = false
                });
            }
        }
    }

    private void UpdateMiningDebris(float dt, Vector2 corePos)
    {
        bool redrawNeeded = !_isMiningLaserActive && !_isBoostActive;
        bool stockChanged = false;

        // Update gelombang kejut kehancuran asteroid
        for (int i = _asteroidShockwaves.Count - 1; i >= 0; i--)
        {
            var sw = _asteroidShockwaves[i];
            sw.Life -= dt;
            float progress = 1.0f - Mathf.Clamp(sw.Life / sw.MaxLife, 0.0f, 1.0f);
            float startR = sw.StartRadius > 0.0f ? sw.StartRadius : 12.0f;
            sw.Radius = sw.IsInward
                ? Mathf.Lerp(startR, sw.MaxRadius, progress * progress)
                : Mathf.Lerp(startR, sw.MaxRadius, MathF.Sqrt(progress));
            if (sw.Life <= 0.0f)
            {
                _asteroidShockwaves.RemoveAt(i);
            }
            else
            {
                _asteroidShockwaves[i] = sw;
            }
            redrawNeeded = true;
        }

        if (_miningDebris.Count == 0 && _asteroidShockwaves.Count == 0) return;

        for (int i = _miningDebris.Count - 1; i >= 0; i--)
        {
            var p = _miningDebris[i];
            p.Life -= dt;

            if (p.IsCrystalMatter)
            {
                Vector2 toPlayer = corePos - p.Position;
                float distPlayer = toPlayer.Length();

                Vector2 toEnemy = (_enemyCore != null && IsInstanceValid(_enemyCore))
                    ? _enemyCore.GlobalPosition - p.Position
                    : new Vector2(99999f, 99999f);
                float distEnemy = toEnemy.Length();

                float playerPickupRad = _playerCore != null ? MathF.Max(28.0f, _playerCore.CoreCollisionRadius + 12.0f) : 28.0f;
                float enemyPickupRad = (_enemyCore != null && IsInstanceValid(_enemyCore)) ? MathF.Max(28.0f, _enemyCore.CoreCollisionRadius + 12.0f) : 28.0f;

                // 1. Penyerapan Kristal oleh Player Core (identik dengan ukuran Core saat itu)
                if (distPlayer <= playerPickupRad)
                {
                    int rIdx = (int)p.MatterType;
                    if (rIdx >= 0 && rIdx < _resourceStock.Length)
                    {
                        // PACING EARLY GAME: +1 Resource per serpihan yang dikumpulkan (Bukan +2)!
                        _resourceStock[rIdx] = InfiniteDebugResources ? InfiniteStockValue : (_resourceStock[rIdx] + 1);
                        stockChanged = true;
                    }

                    _miningDebris.RemoveAt(i);
                    continue;
                }

                // 2. Penyerapan Kristal oleh Enemy Core (100% identik dengan Player Core)
                if (distEnemy <= enemyPickupRad)
                {
                    int rIdx = (int)p.MatterType;
                    _enemyController?.AddResource(rIdx, 1);
                    _miningDebris.RemoveAt(i);
                    continue;
                }

                // 3. Tarikan Magnetis (Magnet Pull 260 px identik 1:1 antara Pemain dan Musuh)
                if (distPlayer <= 260.0f && distPlayer <= distEnemy)
                {
                    Vector2 pullDir = toPlayer / MathF.Max(0.001f, distPlayer);
                    p.Velocity = p.Velocity.Lerp(pullDir * 860.0f, MathF.Min(1.0f, dt * 8.0f));
                    p.Position += p.Velocity * dt;
                }
                else if (distEnemy <= 260.0f)
                {
                    Vector2 pullDir = toEnemy / MathF.Max(0.001f, distEnemy);
                    p.Velocity = p.Velocity.Lerp(pullDir * 860.0f, MathF.Min(1.0f, dt * 8.0f));
                    p.Position += p.Velocity * dt;
                }
                else
                {
                    // Melayang pelan di luar angkasa dengan inersia
                    p.Velocity *= MathF.Pow(0.94f, dt * 60.0f);
                    p.Position += p.Velocity * dt;
                }
            }
            else
            {
                p.Velocity *= MathF.Pow(0.88f, dt * 60.0f);
                p.Position += p.Velocity * dt;
            }

            if (p.IsQuantumGlass)
            {
                p.Rotation += p.RotSpeed * dt;
            }

            if (p.Life <= 0.0f)
            {
                _miningDebris.RemoveAt(i);
            }
            else
            {
                _miningDebris[i] = p;
            }
        }

        if (stockChanged)
        {
            RefreshFoundryGui();
        }

        if (redrawNeeded)
        {
            QueueRedraw();
        }
    }

    public void TriggerAsteroidDestructionFx(Vector2 position, float asteroidRadius, Color glowColor, bool isEnergy)
    {
        // 1. Gelombang kejut ledakan asteroid melingkar dinamis (Expanding Vector Shockwave Ring)
        _asteroidShockwaves.Add(new AsteroidShockwaveRing
        {
            Center = position,
            Radius = 16.0f,
            MaxRadius = asteroidRadius * (isEnergy ? 4.8f : 1.75f),
            Life = isEnergy ? 0.85f : 0.55f,
            MaxLife = isEnergy ? 0.85f : 0.55f,
            Color1 = isEnergy ? new Color(5.2f, 4.4f, 1.8f, 0.98f) : new Color(0.9f, 0.9f, 1.0f, 0.75f),
            Color2 = isEnergy ? new Color(0.8f, 3.8f, 5.2f, 0.90f) : new Color(0.35f, 0.35f, 0.45f, 0.5f),
            IsEnergy = isEnergy
        });

        // EFEK SUPERNOVA DETONATION: Dorongan Kinetik Masif & Robekan Grid Latar Belakang
        if (isEnergy)
        {
            // A. Dorongan Kinetik Supernova Dahsyat ke Kapal Induk Pemain
            if (PlayerCore.Instance != null && IsInstanceValid(PlayerCore.Instance))
            {
                float pDist = position.DistanceTo(PlayerCore.Instance.GlobalPosition);
                if (pDist < 950.0f)
                {
                    Vector2 pDir = (PlayerCore.Instance.GlobalPosition - position).Normalized();
                    float blastForce = Mathf.Lerp(1200.0f, 280.0f, pDist / 950.0f);
                    PlayerCore.Instance.Velocity += pDir * blastForce;
                }
            }

            // B. Dorongan Kinetik Radial ke Seluruh Armada Drone di Sekitar
            _world.Query(in _allSwarmQuery, (ref PositionComponent pPos, ref VelocityComponent pVel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
            {
                if (swarm.State == SwarmState.Dead) return;
                float dDist = position.DistanceTo(pPos.Value);
                if (dDist < 900.0f)
                {
                    Vector2 dDir = (pPos.Value - position).Normalized();
                    float blast = Mathf.Lerp(980.0f, 240.0f, dDist / 900.0f);
                    pVel.Value += dDir * blast;
                }
            });

            // C. Dorongan Kinetik Radial ke Asteroid Lain di Sekitarnya
            for (int a = 0; a < Asteroid.ActiveAsteroids.Count; a++)
            {
                var other = Asteroid.ActiveAsteroids[a];
                if (!IsInstanceValid(other) || !other.IsActiveInTree) continue;
                float aDist = position.DistanceTo(other.CachedPosition);
                if (aDist < 950.0f && aDist > 1.0f)
                {
                    Vector2 aDir = (other.CachedPosition - position).Normalized();
                    other.LinearVelocity += aDir * 360.0f;
                }
            }

            // D. GELOMBANG KEJUT ROBEKAN GRID LATAR BELAKANG (SUPERNOVA GRID RUPTURE)
            EmitSignal(SignalName.SwarmClashImpulse, position, 3800.0f, 750.0f);
        }
        else
        {
            // Gelombang kejut runtuhnya cangkang asteroid batu saat 100% mineral tuntas dipanen
            EmitSignal(SignalName.SwarmClashImpulse, position, 1950.0f, 410.0f);
        }

        // 2. Serpihan bongkahan asteroid terbang berputar 360 derajat (Destruction Shards)
        int shardCount = isEnergy ? GD.RandRange(28, 38) : GD.RandRange(26, 36);
        for (int i = 0; i < shardCount; i++)
        {
            float angle = (float)i / shardCount * Mathf.Tau + (GD.Randf() - 0.5f) * 0.45f;
            Vector2 dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            float speed = isEnergy ? (160.0f + GD.Randf() * 320.0f) : (120.0f + GD.Randf() * 220.0f);
            float life = isEnergy ? (2.8f + GD.Randf() * 1.5f) : (2.0f + GD.Randf() * 1.2f);
            float size = isEnergy ? (4.5f + GD.Randf() * 4.0f) : (3.5f + GD.Randf() * 3.5f);

            Color shardColor = isEnergy
                ? (GD.Randf() < 0.45f ? new Color(0.12f, 0.16f, 0.28f, 0.9f) : (GD.Randf() < 0.5f ? new Color(4.5f, 3.8f, 1.4f, 0.95f) : new Color(0.8f, 3.2f, 4.4f, 0.95f)))
                : new Color(0.38f, 0.35f, 0.42f, 0.85f).Lerp(glowColor * 0.7f, GD.Randf() * 0.35f);

            _miningDebris.Add(new MiningDebrisParticle
            {
                Position = position + dir * (asteroidRadius * 0.22f),
                Velocity = dir * speed,
                Color = shardColor,
                Life = life,
                MaxLife = life,
                Size = size,
                IsCrystalMatter = false,
                Rotation = GD.Randf() * Mathf.Tau,
                RotSpeed = (GD.Randf() - 0.5f) * 18.0f,
                IsQuantumGlass = isEnergy,
                IsPlasmaPuff = false
            });
        }

        // 3. Pelepasan kristal Aether & Quartz saat Bintang Mini meledak (Supernova Loot Drop)
        int dropCount = isEnergy ? GD.RandRange(24, 32) : GD.RandRange(2, 4);
        for (int i = 0; i < dropCount; i++)
        {
            float angle = GD.Randf() * Mathf.Tau;
            Vector2 dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            float speed = isEnergy ? (75.0f + GD.Randf() * 160.0f) : (45.0f + GD.Randf() * 75.0f);
            float life = 25.0f + GD.Randf() * 8.0f;

            ResourceType curDrop = isEnergy
                ? ((GD.Randf() < 0.78f) ? ResourceType.Aether : ResourceType.Quartz)
                : ResourceType.Ferrite;
            Color col = Asteroid.GetResourceColor(curDrop);

            _miningDebris.Add(new MiningDebrisParticle
            {
                Position = position + dir * (isEnergy ? 24.0f : 16.0f),
                Velocity = dir * speed,
                Color = col,
                Life = life,
                MaxLife = life,
                Size = isEnergy ? (4.2f + GD.Randf() * 1.5f) : 3.8f,
                IsCrystalMatter = true,
                MatterType = curDrop,
                Rotation = GD.Randf() * Mathf.Tau,
                RotSpeed = (GD.Randf() - 0.5f) * 15.0f,
                IsQuantumGlass = (curDrop == ResourceType.Aether),
                IsPlasmaPuff = false
            });
        }

        QueueRedraw();
    }

    public void TriggerPreSupernovaShockwave(Vector2 position, float asteroidRadius, float progress)
    {
        _asteroidShockwaves.Add(new AsteroidShockwaveRing
        {
            Center = position,
            Radius = 14.0f,
            StartRadius = 14.0f,
            MaxRadius = asteroidRadius * Mathf.Lerp(2.2f, 4.5f, progress),
            Life = 0.55f,
            MaxLife = 0.55f,
            Color1 = new Color(4.2f, 3.4f, 1.2f, 0.95f),
            Color2 = new Color(0.45f, 2.55f, 4.2f, 0.90f),
            IsEnergy = true,
            IsInward = false
        });

        float impulsePower = Mathf.Lerp(1200.0f, 3200.0f, progress);
        EmitSignal(SignalName.SwarmClashImpulse, position, impulsePower, asteroidRadius * 3.5f);
        QueueRedraw();
    }

    public void TriggerAetherStarRebirthFx(Vector2 position, float asteroidRadius)
    {
        _asteroidShockwaves.Add(new AsteroidShockwaveRing
        {
            Center = position,
            Radius = 12.0f,
            StartRadius = 12.0f,
            MaxRadius = asteroidRadius * 4.2f,
            Life = 0.78f,
            MaxLife = 0.78f,
            Color1 = new Color(3.8f, 3.2f, 1.25f, 0.96f),
            Color2 = new Color(0.45f, 2.55f, 3.85f, 0.90f),
            IsEnergy = true,
            IsInward = false
        });

        EmitSignal(SignalName.SwarmClashImpulse, position, 2650.0f, 540.0f);
        QueueRedraw();
    }

    public void TriggerAetherSeedCompressFx(Vector2 position)
    {
        // 1. Cincin gelombang ruang-waktu menyusut kuat ke dalam titik tunggal (Ruang terkompress sangat gila)
        _asteroidShockwaves.Add(new AsteroidShockwaveRing
        {
            Center = position,
            Radius = 340.0f,
            StartRadius = 340.0f,
            MaxRadius = 6.0f,
            Life = 0.35f,
            MaxLife = 0.35f,
            Color1 = new Color(0.45f, 2.85f, 4.2f, 0.96f),
            Color2 = new Color(3.8f, 3.2f, 1.25f, 0.92f),
            IsEnergy = true,
            IsInward = true
        });

        // Impuls menyedot simpul grid latar belakang melesak ke titik tengah seed (radius teroptimasi)
        EmitSignal(SignalName.SwarmClashImpulse, position, -1600.0f, 280.0f);
        QueueRedraw();
    }

    public void TriggerAetherSeedExpandFx(Vector2 position)
    {
        // 2. Gelombang kejut ruang melebar hebat saat seed muncul kembali dari sub-space
        _asteroidShockwaves.Add(new AsteroidShockwaveRing
        {
            Center = position,
            Radius = 14.0f,
            StartRadius = 14.0f,
            MaxRadius = 480.0f,
            Life = 0.52f,
            MaxLife = 0.52f,
            Color1 = new Color(3.8f, 3.2f, 1.25f, 0.98f),
            Color2 = new Color(0.45f, 2.55f, 3.85f, 0.92f),
            IsEnergy = true,
            IsInward = false
        });

        // Impuls grid latar belakang melebar keluar secara spektakuler (radius teroptimasi)
        EmitSignal(SignalName.SwarmClashImpulse, position, 1800.0f, 320.0f);
        QueueRedraw();
    }

    /// <summary>
    /// Gelombang kejut primer raksasa Supernova dari Cosmic Rift saat pertama kali meledak.
    /// Menghentak grid arena dengan radius terukur (~380 px) satu kali saja agar FPS tetap solid.
    /// </summary>
    public void TriggerRiftSupernovaShockwave(Vector2 position, float radius, float impulsePower)
    {
        _asteroidShockwaves.Add(new AsteroidShockwaveRing
        {
            Center = position,
            Radius = 24.0f,
            StartRadius = 24.0f,
            MaxRadius = radius,
            Life = 0.65f,
            MaxLife = 0.65f,
            Color1 = new Color(7.5f, 7.0f, 6.2f, 1.0f),
            Color2 = new Color(4.2f, 2.8f, 0.85f, 0.95f),
            IsEnergy = true,
            IsInward = false
        });

        // Hentakan fisik grid arena berkekuatan terkontrol agar tidak lag
        float gridDeformRadius = MathF.Min(radius * 0.15f, 380.0f);
        float gridDeformPower = MathF.Min(impulsePower * 0.25f, 2200.0f);
        EmitSignal(SignalName.SwarmClashImpulse, position, gridDeformPower, gridDeformRadius);
        QueueRedraw();
    }

    /// <summary>
    /// Gelombang kejut visual sekunder (Echo Pulse) tanpa menghentak grid GDScript lagi,
    /// menghasilkan visual gelombang kejut beruntun yang mulus dan bebas lag.
    /// </summary>
    public void TriggerVisualShockwaveEcho(Vector2 position, float radius)
    {
        _asteroidShockwaves.Add(new AsteroidShockwaveRing
        {
            Center = position,
            Radius = 24.0f,
            StartRadius = 24.0f,
            MaxRadius = radius,
            Life = 0.52f,
            MaxLife = 0.52f,
            Color1 = new Color(6.8f, 6.2f, 5.5f, 0.90f),
            Color2 = new Color(3.8f, 2.5f, 0.75f, 0.85f),
            IsEnergy = true,
            IsInward = false
        });
        QueueRedraw();
    }

    /// <summary>
    /// Memancarkan aliran partikel materi spageti (batu & kristal mineral) yang terkoyak dari ujung asteroid
    /// dan tersedot melengkung masuk ke dalam inti Bintang Aether.
    /// </summary>
    public void EmitTidalSpaghettiStream(Vector2 tipWorldPos, Vector2 starWorldPos, Color rockColor, Color mineralColor, float intensity, float orbitSpinDir)
    {
        if (_miningDebris.Count > 420) return;

        Vector2 toStar = starWorldPos - tipWorldPos;
        float dist = toStar.Length();
        if (dist < 8.0f) return;

        Vector2 dir = toStar / dist;
        Vector2 tang = new Vector2(-dir.Y, dir.X) * orbitSpinDir;

        int count = intensity > 0.65f ? 7 : (intensity > 0.35f ? 4 : 2);
        for (int i = 0; i < count; i++)
        {
            float latJitter = (GD.Randf() - 0.5f) * 36.0f * (1.0f - intensity * 0.4f);
            Vector2 spawnPos = tipWorldPos + tang * latJitter;
            float speed = Mathf.Lerp(320.0f, 760.0f, intensity) + GD.Randf() * 140.0f;
            Vector2 streamVel = (dir * 0.82f + tang * (0.44f + (GD.Randf() - 0.5f) * 0.22f)).Normalized() * speed;
            float life = Mathf.Clamp(dist / MathF.Max(120.0f, speed), 0.18f, 0.95f);

            bool isMineralShard = GD.Randf() < (0.45f + intensity * 0.35f);
            Color shardCol = isMineralShard
                ? mineralColor.Lerp(new Color(5.8f, 5.2f, 2.2f, 1.0f), intensity * 0.65f)
                : rockColor.Lerp(new Color(4.8f, 2.2f, 0.6f, 0.98f), intensity * 0.75f);

            _miningDebris.Add(new MiningDebrisParticle
            {
                Position = spawnPos,
                Velocity = streamVel,
                Color = shardCol,
                Life = life,
                MaxLife = life,
                Size = isMineralShard ? (3.6f + GD.Randf() * 3.2f) : (3.0f + GD.Randf() * 2.8f),
                IsCrystalMatter = false,
                Rotation = streamVel.Angle(),
                RotSpeed = orbitSpinDir * 18.0f,
                IsQuantumGlass = isMineralShard,
                IsPlasmaPuff = !isMineralShard
            });
        }
        QueueRedraw();
    }

    /// <summary>
    /// Efek klimaks saat asteroid selesai mengalami Spaghettification dan hancur lebur ditelan Bintang Aether.
    /// </summary>
    public void TriggerTidalSpaghettiConsumptionFx(Vector2 starPos, Vector2 asteroidLastPos, float originalRadius, Color resourceColor, ResourceType primaryRes)
    {
        // 0. Shake kamera sinematik dahsyat saat singularitas menelan asteroid
        PlayerCamera.Instance?.TriggerShake(26.0f);

        // 1. Cincin kilatan putih menyilaukan (Event Horizon Consumption Flash)
        _asteroidShockwaves.Add(new AsteroidShockwaveRing
        {
            Center = starPos,
            Radius = 14.0f,
            StartRadius = 14.0f,
            MaxRadius = originalRadius * 2.8f,
            Life = 0.52f,
            MaxLife = 0.52f,
            Color1 = new Color(6.5f, 6.0f, 5.0f, 1.0f),
            Color2 = new Color(5.2f, 3.8f, 1.4f, 0.95f),
            IsEnergy = true,
            IsInward = false
        });

        // 2. Cincin implosi hisapan spageti ke dalam Bintang Aether
        _asteroidShockwaves.Add(new AsteroidShockwaveRing
        {
            Center = starPos,
            Radius = originalRadius * 1.45f,
            StartRadius = originalRadius * 1.45f,
            MaxRadius = 14.0f,
            Life = 0.44f,
            MaxLife = 0.44f,
            Color1 = resourceColor * 1.5f,
            Color2 = new Color(3.8f, 3.2f, 1.25f, 0.95f),
            IsEnergy = true,
            IsInward = true
        });

        // 2. Cincin semburan akresi balik (Accretion Flare Burst) dari korona Bintang Aether
        _asteroidShockwaves.Add(new AsteroidShockwaveRing
        {
            Center = starPos,
            Radius = 18.0f,
            StartRadius = 18.0f,
            MaxRadius = originalRadius * 2.1f,
            Life = 0.68f,
            MaxLife = 0.68f,
            Color1 = new Color(5.2f, 4.4f, 1.8f, 0.98f),
            Color2 = new Color(0.65f, 3.2f, 4.8f, 0.92f),
            IsEnergy = true,
            IsInward = false
        });

        // 3. Lontaran kristal mineral & Aether di sekitar orbit bintang hasil koyakan pasang-surut
        int lootCount = GD.RandRange(10, 16);
        for (int i = 0; i < lootCount; i++)
        {
            float angle = (float)i / lootCount * Mathf.Tau + (GD.Randf() - 0.5f) * 0.35f;
            Vector2 dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            Vector2 tang = new Vector2(-dir.Y, dir.X);
            float speed = 95.0f + GD.Randf() * 145.0f;
            float life = 24.0f + GD.Randf() * 8.0f;

            ResourceType dropType = (i % 3 == 0) ? ResourceType.Aether : primaryRes;
            Color col = Asteroid.GetResourceColor(dropType);

            _miningDebris.Add(new MiningDebrisParticle
            {
                Position = starPos + dir * 72.0f,
                Velocity = (dir * 0.65f + tang * 0.75f).Normalized() * speed,
                Color = col,
                Life = life,
                MaxLife = life,
                Size = 4.0f + GD.Randf() * 1.4f,
                IsCrystalMatter = true,
                MatterType = dropType,
                Rotation = GD.Randf() * Mathf.Tau,
                RotSpeed = (GD.Randf() - 0.5f) * 16.0f,
                IsQuantumGlass = true,
                IsPlasmaPuff = false
            });
        }

        EmitSignal(SignalName.SwarmClashImpulse, starPos, -2950.0f, 620.0f);
        QueueRedraw();
    }

    public override void _Draw()
    {
        try
        {
            // 1. Gambar serpihan puing tambang & kristal resource
            for (int i = 0; i < _miningDebris.Count; i++)
            {
                var p = _miningDebris[i];
                float alpha = Mathf.Clamp(p.Life / p.MaxLife, 0.0f, 1.0f);
                Color c = new Color(p.Color.R, p.Color.G, p.Color.B, p.Color.A * alpha);

                if (p.IsPlasmaPuff)
                {
                    float expandRatio = 1.0f - alpha;
                    float radius = p.Size + expandRatio * 22.0f;
                    DrawCircle(p.Position, radius, new Color(c.R, c.G, c.B, c.A * 0.30f));
                    DrawArc(p.Position, radius, 0.0f, Mathf.Tau, 18, c, 1.4f, true);
                }
                else if (p.IsQuantumGlass)
                {
                    // Prisma Kaca Kuantum Berlian 4-Sisi Terdistorsi (Zero heap allocation)
                    Vector2 fwd = new Vector2(MathF.Cos(p.Rotation), MathF.Sin(p.Rotation)) * p.Size;
                    Vector2 right = new Vector2(-fwd.Y, fwd.X) * 0.52f;
                    _debrisPrismVerts4[0] = p.Position + fwd;
                    _debrisPrismVerts4[1] = p.Position + right;
                    _debrisPrismVerts4[2] = p.Position - fwd;
                    _debrisPrismVerts4[3] = p.Position - right;

                    _debrisPrismVerts5[0] = _debrisPrismVerts4[0];
                    _debrisPrismVerts5[1] = _debrisPrismVerts4[1];
                    _debrisPrismVerts5[2] = _debrisPrismVerts4[2];
                    _debrisPrismVerts5[3] = _debrisPrismVerts4[3];
                    _debrisPrismVerts5[4] = _debrisPrismVerts4[0];

                    DrawColoredPolygon(_debrisPrismVerts4, c);
                    DrawPolyline(_debrisPrismVerts5, new Color(5.5f, 5.5f, 5.0f, c.A * 0.95f), 1.2f);
                }
                else if (p.IsCrystalMatter)
                {
                    Rect2 shardRect = new Rect2(p.Position - new Vector2(p.Size * 0.5f, p.Size * 0.5f), new Vector2(p.Size, p.Size));
                    DrawRect(shardRect, c, true);
                }
                else
                {
                    DrawCircle(p.Position, p.Size * (0.6f + 0.4f * alpha), c);
                }
            }

            // 1b. Gambar percikan kinetik benturan antar drone (Clash Sparks)
            for (int i = 0; i < _clashSparks.Count; i++)
            {
                var sp = _clashSparks[i];
                float alpha = Mathf.Clamp(sp.Life / sp.MaxLife, 0.0f, 1.0f);
                Color c = new Color(sp.Color.R, sp.Color.G, sp.Color.B, sp.Color.A * alpha);
                DrawLine(sp.Position, sp.Position - sp.Velocity * 0.025f, c, sp.Size);
            }

            // 1c. Gambar Gelombang Kejut Ledakan Asteroid (Shockwave Rings)
            for (int i = 0; i < _asteroidShockwaves.Count; i++)
            {
                var sw = _asteroidShockwaves[i];
                float alpha = Mathf.Clamp(sw.Life / sw.MaxLife, 0.0f, 1.0f);
                Color col1 = new Color(sw.Color1.R, sw.Color1.G, sw.Color1.B, sw.Color1.A * alpha);
                DrawArc(sw.Center, sw.Radius, 0.0f, Mathf.Tau, 40, col1, 2.8f, false);

                if (sw.IsEnergy)
                {
                    Color col2 = new Color(sw.Color2.R, sw.Color2.G, sw.Color2.B, sw.Color2.A * alpha);
                    DrawArc(sw.Center, sw.Radius * 0.72f, 0.0f, Mathf.Tau, 32, col2, 2.0f, false);
                }
            }

            // 2. Gambar Efek Visual Sacrificial Dart Plasma Boost [SPACE], Kubah Parabola, & Retro-Brake
            if (_isBoostActive && _activeThrusterJets.Count > 0)
            {
                DrawDartPlasmaBoostVfx();
            }
            if (_isBrakeActive && _activeBrakeJets.Count > 0)
            {
                DrawDartPlasmaBrakeVfx();
            }

            // 3. Gambar Efek Visual Laser Penambangan Multi-Blok Pemain
            if (_isMiningLaserActive && _activeMinerPositions.Count > 0)
            {
                int minerCount = _activeMinerPositions.Count;
            float densityFactor = Mathf.Clamp((minerCount - 1) / 45.0f, 0.0f, 1.0f);
            float highFreqWave = 0.5f + 0.5f * MathF.Sin(_laserPulseTimer * 28.0f);

            bool isPriming = _laserPrimingTimer < LaserPrimingDuration;
            float primingProgress = Mathf.Clamp(_laserPrimingTimer / LaserPrimingDuration, 0.0f, 1.0f);

            float feederWidth = Mathf.Lerp(0.85f, 1.35f, _laserFocusWarmup);
            Color feederColor = new Color(
                Mathf.Lerp(1.6f, 2.4f, densityFactor),
                Mathf.Lerp(1.1f, 1.85f, densityFactor),
                0.35f,
                (0.38f + 0.25f * densityFactor) * (isPriming ? (0.35f + 0.65f * primingProgress) : _laserFocusWarmup)
            );

            for (int i = 0; i < minerCount; i++)
            {
                DrawLine(_activeMinerPositions[i], _laserConvergencePoint, feederColor, feederWidth, false);
            }

            if (isPriming)
            {
                float primingPulse = 0.72f + 0.28f * MathF.Sin(_laserPrimingTimer * 44.0f);
                float orbRadius = Mathf.Lerp(2.0f, 7.5f, primingProgress) * primingPulse;

                DrawCircle(_laserConvergencePoint, orbRadius * 1.6f, new Color(2.6f, 1.35f, 0.22f, 0.35f * primingProgress));
                DrawCircle(_laserConvergencePoint, orbRadius, new Color(4.4f, 3.8f, 1.4f, 0.95f * primingProgress));

                for (int s = 0; s < 4; s++)
                {
                    float spkAngle = (float)s / 4.0f * Mathf.Tau + _laserPrimingTimer * 26.0f;
                    Vector2 spkPos = _laserConvergencePoint + new Vector2(MathF.Cos(spkAngle), MathF.Sin(spkAngle)) * (orbRadius * 1.35f);
                    DrawCircle(spkPos, 1.3f, new Color(4.8f, 4.0f, 1.6f, 0.85f * primingProgress));
                }
            }
            else
            {
                float focusRadius = Mathf.Lerp(2.0f, 3.2f + _currentLaserBlockSpan * 1.4f, _laserFocusWarmup);
                DrawCircle(_laserConvergencePoint, focusRadius * 1.45f, new Color(2.2f, 1.2f, 0.25f, 0.32f * _laserFocusWarmup));
                DrawCircle(_laserConvergencePoint, focusRadius, new Color(3.2f, 2.6f, 1.1f, 0.95f * _laserFocusWarmup));

                float mainBeamWidth = _currentVisualBeamWidth;
                float coreBeamWidth = mainBeamWidth * Mathf.Lerp(0.42f, 0.58f, densityFactor);

                Color sheathColor = new Color(
                    2.0f + densityFactor * 0.8f,
                    0.95f + densityFactor * 0.55f,
                    0.18f,
                    (0.24f + 0.16f * highFreqWave) * _laserFocusWarmup
                );
                DrawLine(_laserConvergencePoint, _laserImpactPoint, sheathColor, mainBeamWidth + 2.4f, false);

                Color mainBeamColor = new Color(
                    Mathf.Lerp(2.4f, 3.4f, densityFactor),
                    Mathf.Lerp(1.25f, 2.45f, densityFactor) + highFreqWave * 0.25f,
                    Mathf.Lerp(0.25f, 0.85f, densityFactor),
                    (0.88f + 0.10f * densityFactor) * _laserFocusWarmup
                );
                DrawLine(_laserConvergencePoint, _laserImpactPoint, mainBeamColor, mainBeamWidth, false);

                Color coreBeamColor = new Color(
                    Mathf.Lerp(3.2f, 4.4f, densityFactor),
                    Mathf.Lerp(2.6f, 4.1f, densityFactor),
                    Mathf.Lerp(1.2f, 2.6f, densityFactor),
                    0.96f * _laserFocusWarmup
                );
                DrawLine(_laserConvergencePoint, _laserImpactPoint, coreBeamColor, coreBeamWidth, false);

                if (_currentLaserBlockSpan >= 2)
                {
                    Vector2 tangent = new Vector2(-_laserAimDir.Y, _laserAimDir.X);
                    Color subLineCol = new Color(4.6f, 4.2f, 2.2f, 0.65f * _laserFocusWarmup);
                    for (int b = 0; b < _currentLaserBlockSpan; b++)
                    {
                        float offset = ((float)b / (_currentLaserBlockSpan - 1) - 0.5f) * (mainBeamWidth * 0.72f);
                        DrawLine(_laserConvergencePoint + tangent * offset, _laserImpactPoint + tangent * offset, subLineCol, 1.4f, false);
                    }
                }

                if (_laserActiveHitBoxes.Count > 0)
                {
                    if (_isMiningEnergyAsteroid)
                    {
                        // Untuk Bintang Aether: Efek kontak plasma berupa pendaran sirkular fusi tinggi (bebas garis kotak!)
                        float flareR = 5.0f + _currentLaserBlockSpan * 2.2f;
                        Color flareAether = new Color(4.2f, 3.6f, 1.35f, 0.95f);
                        DrawCircle(_laserImpactPoint, flareR * 1.5f, new Color(0.45f, 2.5f, 3.8f, 0.45f));
                        DrawCircle(_laserImpactPoint, flareR, flareAether);
                    }
                    else
                    {
                        Color hotOrange = new Color(2.8f, 0.95f, 0.15f, 0.90f);
                        Color yellowIncandescent = new Color(4.8f, 4.3f, 1.35f, 1.0f);

                        for (int i = 0; i < _laserActiveHitBoxes.Count; i++)
                        {
                            var box = _laserActiveHitBoxes[i];
                            float h = Mathf.Clamp(box.heat, 0.0f, 1.0f);
                            Color impactColor = hotOrange.Lerp(yellowIncandescent, h);

                            DrawSetTransform(box.worldCenter, _laserHitAsteroidRotation, Vector2.One);
                            Rect2 targetBox = new Rect2(
                                new Vector2(-Asteroid.VoxelSize * 0.5f, -Asteroid.VoxelSize * 0.5f),
                                new Vector2(Asteroid.VoxelSize, Asteroid.VoxelSize)
                            );
                            DrawRect(targetBox, new Color(impactColor.R, impactColor.G, impactColor.B, 0.25f + h * 0.55f), true);

                            if (Asteroid.TryGetResourceType(box.type, out ResourceType rType))
                            {
                                Color resNeon = Asteroid.GetResourceColor(rType) * 1.7f;
                                resNeon.A = 1.0f;
                                DrawRect(targetBox.Grow(1.2f), resNeon, false, 2.2f);
                            }
                            else
                            {
                                DrawRect(targetBox, impactColor, false, 1.5f);
                            }
                        }
                        DrawSetTransform(Vector2.Zero, 0.0f, Vector2.One);

                        float spotRadius = 2.4f + _currentLaserBlockSpan * 1.4f;
                        DrawCircle(_laserImpactPoint, spotRadius, yellowIncandescent);
                    }
                }
                else
                {
                    DrawCircle(_laserImpactPoint, mainBeamWidth * 0.5f, coreBeamColor);
                }

                // EFEK PLASMA SIPHONING: Aliran plasma terpilin mengalir mundur dari bintang ke kapal
                if (_isMiningEnergyAsteroid && _plasmaSiphonWisps.Count > 0)
                {
                    Vector2 beamVec = _laserConvergencePoint - _laserImpactPoint;
                    Vector2 beamNorm = beamVec.Normalized();
                    Vector2 beamNormalPerp = new Vector2(-beamNorm.Y, beamNorm.X);

                    // 1. Pilinan pita heliks ganda (Double Helix Plasma Strands) di sekeliling berkas laser
                    int helixSteps = 22;
                    Vector2 prevH1 = _laserImpactPoint;
                    Vector2 prevH2 = _laserImpactPoint;
                    Color helix1 = new Color(3.8f, 3.2f, 1.15f, 0.40f * _laserFocusWarmup);
                    Color helix2 = new Color(0.4f, 2.5f, 3.9f, 0.40f * _laserFocusWarmup);

                    for (int h = 1; h <= helixSteps; h++)
                    {
                        float t = (float)h / helixSteps;
                        Vector2 curPt = _laserImpactPoint + beamVec * t;
                        float phase = t * 24.0f - _laserPulseTimer * 16.0f;
                        float r = 11.0f * (1.0f - t * 0.35f);

                        Vector2 curH1 = curPt + beamNormalPerp * (MathF.Sin(phase) * r);
                        Vector2 curH2 = curPt - beamNormalPerp * (MathF.Sin(phase) * r);

                        DrawLine(prevH1, curH1, helix1, 1.4f, false);
                        DrawLine(prevH2, curH2, helix2, 1.2f, false);
                        prevH1 = curH1;
                        prevH2 = curH2;
                    }

                    // 2. Partikel plasma siphoning yang melesat mundur ke kapal
                    for (int w = 0; w < _plasmaSiphonWisps.Count; w++)
                    {
                        var wisp = _plasmaSiphonWisps[w];
                        Vector2 centerPt = _laserImpactPoint + beamVec * wisp.Progress;
                        float spiralOffset = MathF.Sin(wisp.Angle) * wisp.Radius * (1.0f - wisp.Progress * 0.4f);
                        Vector2 pos = centerPt + beamNormalPerp * spiralOffset;

                        float alphaFade = MathF.Sin(wisp.Progress * Mathf.Pi);
                        Color c = new Color(wisp.Color.R, wisp.Color.G, wisp.Color.B, wisp.Color.A * alphaFade);
                        DrawCircle(pos, wisp.Size, c);
                    }
                }
            }
            }

            // 4. Gambar Laser Penambangan Musuh (Rival Hive Mining Laser)
            _enemyController?.DrawLaser(this);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SwarmSimulation._Draw] {ex}");
        }
    }

    /// <summary>
    /// Menggambar Efek Megah Sacrificial Plasma Boost:
    /// 1. Selubung Gelombang Kejut Kubah Parabola (Parabolic Bow Shock Shield) di depan Core.
    /// 2. Berkas Feeder Konvergen dari 10 Dart Nozzle menuju ruang bakar (Focal Plasma Chamber).
    /// 3. Satu Aliran Jet Plasma Utama yang Ramping, Tajam, & Megah dengan Cincin Mach Shock Diamonds.
    /// </summary>
    private void DrawDartPlasmaBoostVfx()
    {
        float power = BoostPowerRatio;
        float pulse = _boostPulseTimer;
        int dartCount = _activeThrusterJets.Count;
        if (dartCount == 0) return;

        Vector2 aimDir = BoostAimDirection;
        Vector2 backDir = -aimDir;
        Vector2 tangent = new Vector2(-aimDir.Y, aimDir.X);
        Vector2 focalPos = BoostNozzlePosition;

        // =========================================================================
        // A. SELUBUNG GELOMBANG KEJUT KUBAH PARABOLA DI DEPAN CORE (BOW SHOCK DOME)
        // =========================================================================
        const int domeSegments = 24;
        var domeOuterPts = new Vector2[domeSegments + 1];
        var domeInnerPts = new Vector2[domeSegments + 1];
        float span = _parabolaHalfSpan * 1.06f;

        for (int i = 0; i <= domeSegments; i++)
        {
            float u = ((float)i / domeSegments) * 2.0f - 1.0f;
            float s = u * span;
            float backX = _parabolaCurvatureK * (s * s);
            float ripple = MathF.Sin(pulse * 2.2f + u * 5.0f) * 1.6f;

            domeOuterPts[i] = _parabolaApexWorldPos - aimDir * (backX - 6.0f) + tangent * (s + ripple);
            domeInnerPts[i] = _parabolaApexWorldPos - aimDir * (backX + 4.0f) + tangent * s;
        }

        Color bowOuterCol = new Color(0.6f, 2.8f, 4.8f, (0.32f + 0.25f * power));
        Color bowCoreCol = new Color(2.4f, 4.6f, 5.8f, (0.55f + 0.30f * power));
        DrawPolyline(domeOuterPts, bowOuterCol, 3.2f, false);
        DrawPolyline(domeInnerPts, bowCoreCol, 1.5f, false);

        // Kilau puncak kubah parabola (Apex Mach Nose)
        float apexPulse = 1.0f + 0.25f * MathF.Sin(pulse * 3.8f);
        DrawCircle(_parabolaApexWorldPos + aimDir * 5.0f, 4.5f * apexPulse * power, new Color(3.2f, 5.0f, 6.0f, 0.65f * power));

        // =========================================================================
        // B. BUSUR INDUKSI & FEEDER KONVERGEN DARI 10 DART KE FOCAL PLASMA CHAMBER
        // =========================================================================
        for (int i = 0; i < dartCount; i++)
        {
            Vector2 dPos = _activeThrusterDartPositions[i];
            Color feederGlow = new Color(0.8f, 3.4f, 5.2f, 0.55f * power);
            Color feederCore = new Color(3.8f, 4.8f, 5.8f, 0.88f * power);

            DrawLine(dPos, focalPos, feederGlow, 3.4f, false);
            DrawLine(dPos, focalPos, feederCore, 1.4f, false);

            if (i < dartCount - 1)
            {
                Vector2 nextPos = _activeThrusterDartPositions[i + 1];
                Vector2 mid = (dPos + nextPos) * 0.5f + backDir * (MathF.Sin(pulse * 4.2f + i * 1.7f) * 2.5f);
                Color arcCol = new Color(1.8f, 4.0f, 5.5f, 0.65f * power);
                DrawLine(dPos, mid, arcCol, 1.3f, false);
                DrawLine(mid, nextPos, arcCol, 1.3f, false);
            }
        }

        // Bola Ruang Bakar Plasma (Focal Pinch Orb) + Flash saat Dart dikorbankan
        float flashBoost = _boostSacrificeFlash;
        float orbRad = (7.5f + 5.0f * power + 6.5f * flashBoost) * (0.90f + 0.10f * MathF.Sin(pulse * 5.0f));
        DrawCircle(focalPos, orbRad * 1.85f, new Color(0.5f, 2.4f, 4.8f, 0.35f * power));
        DrawCircle(focalPos, orbRad, new Color(2.8f + flashBoost * 2.0f, 4.6f, 5.8f, 0.92f));
        DrawCircle(focalPos, orbRad * 0.52f, new Color(5.5f, 5.4f, 4.2f, 0.98f));

        // =========================================================================
        // C. SEMBURAN JET PLASMA UTAMA TERPUSAT (AEROSPIKE AFTERBURNER)
        // Di-render secara volumetrik & ultra-smooth oleh GPU Shader Mesh (_plasmaBoostMesh)
        // =========================================================================
    }

    private void DrawDartPlasmaBrakeVfx()
    {
        int dartCount = _activeBrakeJets.Count;
        if (dartCount == 0) return;

        float pulse = _boostPulseTimer;

        // 1. Busur Gelombang Kejut Pengereman (Deceleration Bow Shockwave) di depan barisan Dart
        if (_activeBrakeDartPositions.Count >= 3)
        {
            for (int i = 0; i < _activeBrakeDartPositions.Count - 1; i++)
            {
                Vector2 pA = _activeBrakeDartPositions[i];
                Vector2 pB = _activeBrakeDartPositions[i + 1];
                Vector2 mid = (pA + pB) * 0.5f;
                Vector2 delta = pB - pA;
                Vector2 norm = delta.LengthSquared() > 0.01f ? new Vector2(-delta.Y, delta.X).Normalized() : Vector2.Up;
                Vector2 bowPt = mid + norm * (4.0f + 2.0f * MathF.Sin(pulse * 3.0f + i));

                Color shockCol = new Color(5.0f, 2.2f, 0.4f, 0.78f);
                DrawLine(pA, bowPt, shockCol, 2.4f, false);
                DrawLine(bowPt, pB, shockCol, 2.4f, false);
            }
        }

        // 2. Berkas Counter-Plasma Jets memancar ke depan menahan laju kapal
        for (int i = 0; i < dartCount; i++)
        {
            var jet = _activeBrakeJets[i];
            Vector2 origin = jet.Position;
            Vector2 dir = jet.Direction;
            Vector2 tangent = new Vector2(-dir.Y, dir.X);
            float len = jet.Length;
            float power = jet.Power;

            float sheathW = 3.5f + 4.0f * power;
            var sheathPts = new Vector2[]
            {
                origin + tangent * sheathW,
                origin + dir * (len * 0.4f) + tangent * (sheathW * 1.3f),
                origin + dir * len,
                origin + dir * (len * 0.4f) - tangent * (sheathW * 1.3f),
                origin - tangent * sheathW
            };
            var sheathCols = new Color[]
            {
                new(5.0f, 1.8f, 0.4f, 0.80f),
                new(4.2f, 3.2f, 0.6f, 0.55f),
                new(3.0f, 0.8f, 0.1f, 0.0f),
                new(4.2f, 3.2f, 0.6f, 0.55f),
                new(5.0f, 1.8f, 0.4f, 0.80f)
            };
            DrawPolygon(sheathPts, sheathCols);

            // Core Retro-Jet
            DrawLine(origin, origin + dir * len, new Color(5.2f, 2.6f, 0.6f, 0.95f), 3.2f, false);
            DrawLine(origin, origin + dir * (len * 0.75f), new Color(5.6f, 4.8f, 2.2f, 0.98f), 1.8f, false);

            // Flare di moncong nozzle dart
            DrawCircle(origin, 3.2f, new Color(5.5f, 4.2f, 1.8f, 0.95f));
        }
    }

    /// <summary>
    /// Menembakkan rentetan unit swarm yang status Multi-Toggle di bar tengah bawah sedang ON ([2]-[5]).
    /// Guardian (12-Sector Auto-Shield), Scanner (Auto-Scan), dan Mining Droid (Mining Laser) tidak ditembakkan sebagai peluru.
    /// </summary>
    public void FireSwarmStream(Vector2 targetPos, int count = 2)
    {
        int fired = 0;
        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm) =>
        {
            if (fired >= count) return;
            if (swarm.State != SwarmState.Orbiting || swarm.OwnerId != 0) return;

            // Guardian, MiningDroid, dan ScannerDrone bukan proyektil tembakan klik kiri
            if (swarm.Role == SwarmRole.Guardian
                || swarm.Role == SwarmRole.MiningDroid
                || swarm.Role == SwarmRole.ScannerDrone) return;

            // Hanya tembakkan jenis swarm tempur yang di-toggle ON oleh pemain di Tactical Swarm Dock!
            int roleIdx = (int)swarm.Role;
            if (roleIdx < 0 || roleIdx >= _roleToggledOn.Length || !_roleToggledOn[roleIdx])
                return;

            Vector2 toTarget = targetPos - pos.Value;
            if (toTarget.LengthSquared() < 1.0f) return;

            float spreadAngle = (GD.Randf() - 0.5f) * 0.12f;
            Vector2 dir = toTarget.Normalized().Rotated(spreadAngle);

            swarm.State = SwarmState.Launched;
            swarm.FormationIndex = -1;
            swarm.ExhaustTriggerTime = -1.0f;
            swarm.MaxHealth = 1;
            swarm.Health = 1;
            swarm.TargetDirection = dir;
            swarm.TargetPosition = targetPos;
            swarm.LifeTime = 0.0f;
            swarm.MaxLifeTime = (swarm.Role == SwarmRole.CrescentBoomerang) ? 1.6f : 1.15f;

            vel.Value = dir * (850.0f + GD.Randf() * 200.0f);
            fired++;
        });
    }

    public void FireSwarmVolley(Vector2 targetPos, int count = 25)
    {
        int fired = 0;
        Vector2 toTarget = targetPos - _playerCore.GlobalPosition;
        if (toTarget.LengthSquared() < 1.0f) toTarget = Vector2.Right;
        Vector2 baseDir = toTarget.Normalized();

        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm) =>
        {
            if (fired >= count) return;
            if (swarm.State != SwarmState.Orbiting || swarm.OwnerId != 0) return;
            if (swarm.Role == SwarmRole.Guardian
                || swarm.Role == SwarmRole.MiningDroid
                || swarm.Role == SwarmRole.ScannerDrone) return;

            float spreadAngle = (GD.Randf() - 0.5f) * 0.55f;
            Vector2 dir = baseDir.Rotated(spreadAngle);

            swarm.State = SwarmState.Launched;
            swarm.FormationIndex = -1;
            swarm.ExhaustTriggerTime = -1.0f;
            swarm.MaxHealth = 1;
            swarm.Health = 1;
            swarm.TargetDirection = dir;
            swarm.TargetPosition = targetPos;
            swarm.LifeTime = 0.0f;
            swarm.MaxLifeTime = 0.95f + GD.Randf() * 0.4f;

            vel.Value = dir * (1000.0f + GD.Randf() * 300.0f);
            fired++;
        });
    }

    public void FireRadialNova(int count = 75)
    {
        int fired = 0;
        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm) =>
        {
            if (fired >= count) return;
            if (swarm.State != SwarmState.Orbiting || swarm.OwnerId != 0) return;
            if (swarm.Role == SwarmRole.Guardian
                || swarm.Role == SwarmRole.MiningDroid
                || swarm.Role == SwarmRole.ScannerDrone) return;

            float angle = ((float)fired / count) * Mathf.Tau;
            Vector2 dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));

            swarm.State = SwarmState.Launched;
            swarm.FormationIndex = -1;
            swarm.ExhaustTriggerTime = -1.0f;
            swarm.MaxHealth = 1;
            swarm.Health = 1;
            swarm.TargetDirection = dir;
            swarm.TargetPosition = pos.Value + dir * 1200.0f;
            swarm.LifeTime = 0.0f;
            swarm.MaxLifeTime = 1.1f;

            vel.Value = dir * 1150.0f;
            fired++;
        });
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey keyEvent && keyEvent.Pressed && !keyEvent.Echo)
        {
            switch (keyEvent.Keycode)
            {
                case Key.Tab:
                case Key.B:
                    ToggleFoundryPanel();
                    break;
                case Key.Key1:
                    // Multi-Toggle [1]: Mining Droid (Heavy Mining Laser)
                    ToggleTacticalSlot(0);
                    break;
                case Key.Key2:
                    // Multi-Toggle [2]: Dart Needle
                    ToggleTacticalSlot(1);
                    break;
                case Key.Key3:
                    // Multi-Toggle [3]: Giga Shuriken
                    ToggleTacticalSlot(2);
                    break;
                case Key.Key4:
                    // Multi-Toggle [4]: Crescent Boomerang
                    ToggleTacticalSlot(3);
                    break;
                case Key.Key5:
                    // Multi-Toggle [5]: Drill Drone (Lance Projectile)
                    ToggleTacticalSlot(4);
                    break;
                case Key.Q:
                    FireRadialNova(80);
                    break;
            }
        }
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        _world?.Dispose();
    }
}
