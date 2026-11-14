using System;
using System.Collections.Generic;
using Arch.Core;
using GeometrySwarm.Core.ECS.Components;
using GeometrySwarm.Core.ECS.Systems;
using GeometrySwarm.Visual;
using Godot;
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
    // ENEMY CORE, MUSUH MENEMBAK & CLASH ANIMATION
    // =========================================================================
    private EnemyCore? _enemyCore;
    public bool IsEnemyFiring { get; set; } = true;
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
    }

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
    [Export] public bool InfiniteDebugResources { get; set; } = true;

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

    // =========================================================================
    // INVENTORI 5 RESOURCE, GUI PABRIK, & TACTICAL SWARM DOCK (TENGAH BAWAH)
    // =========================================================================
    private const int InfiniteStockValue = 999999;
    private readonly int[] _resourceStock = new int[5] { InfiniteStockValue, InfiniteStockValue, InfiniteStockValue, InfiniteStockValue, InfiniteStockValue };
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

    public float PlayerHealth => _playerCore?.Health ?? 0.0f;
    public float PlayerMaxHealth => _playerCore?.MaxHealth ?? 1.0f;
    public bool IsPlayerEscapePod => _playerCore?.IsEscapePod ?? false;
    public float EnemyHealth => _enemyCore?.Health ?? 0.0f;
    public float EnemyMaxHealth => _enemyCore?.MaxHealth ?? 1.0f;
    public bool IsEnemyEscapePod => _enemyCore?.IsEscapePod ?? false;

    public int GigaDrillTotalUnits => _activeDrillFormationCount + _activeDefenderCoreCount;
    public int GigaDrillDefenderCoreCount => _activeDefenderCoreCount;
    public int GigaDrillLanceCount => _activeDrillFormationCount;

    public override void _Ready()
    {
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

        InitializeRecipes();
        InitializeTacticalSlots();
        CallDeferred(MethodName.BuildInteractiveFoundryGui);
        SpawnStartingSwarm();

        // Inisialisasi Kapal Induk Musuh (Enemy Hive Core) berhadapan dengan Player Core
        _enemyCore = new EnemyCore();
        _enemyCore.Position = new Vector2(1150.0f, 0.0f);
        _enemyCore.EnemyEscapePodEjected += OnEnemyEscapePodEjected;
        _enemyCore.EnemyCoreDestroyed += OnEnemyCoreDestroyed;
        AddChild(_enemyCore);
        SpawnInitialEnemySwarm(60);
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

        for (int i = 0; i < count; i++)
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
            swarm.MaxHealth = 1;
            swarm.Health = 1;
            swarm.TargetDirection = dir;
            swarm.TargetPosition = targetPos;
            swarm.LifeTime = 0.0f;
            swarm.MaxLifeTime = 2.0f;
            vel.Value = dir * (_boidSystem.BulletMaxSpeed * (0.85f + GD.Randf() * 0.15f));
            fired++;
        });

        if (DartNeedleCount < 25)
        {
            AddSpecificSwarm(SwarmRole.DartNeedle, 6);
        }
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

            _boidSystem.Update(dt, corePos, coreVel, enemyCorePos, mousePos, _isBoostActive);

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

            if (_boidSystem.PlayerCoreHitsThisFrame > 0 && _playerCore != null && IsInstanceValid(_playerCore))
            {
                Vector2 hitDir = _boidSystem.LastPlayerHitVelocity.LengthSquared() > 0.01f
                    ? _boidSystem.LastPlayerHitVelocity.Normalized()
                    : Vector2.Right;
                _playerCore.TakeDamage(_boidSystem.PlayerCoreHitsThisFrame * 40.0f, hitDir);
            }

            UpdateClashSparks(dt);

            _renderer.ResetCounters();
            _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
            {
                if (swarm.State == SwarmState.Dead) return;
                _renderer.SetInstance((int)swarm.Role, pos.Value, vel.Value, swarm.SpinAngle, vis.Color, vis.Scale);
            });
            _renderer.FlushVisibleCounts();
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

        int readyLanceCount = 0;
        _world.Query(in _allSwarmQuery, (ref SwarmUnitComponent swarm) =>
        {
            if (swarm.OwnerId == 0 && swarm.Role == SwarmRole.DrillDrone && swarm.State == SwarmState.Orbiting)
            {
                readyLanceCount++;
            }
        });

        if (readyLanceCount < 6)
        {
            _isChargingDrill = false;
            return;
        }

        _isChargingDrill = true;
        _drillChargeTimer = 0.0f;
        _overchargeExhaustTimer = 0.0f;

        if (DrillDroneCount < 28)
        {
            int needed = 36 - DrillDroneCount;
            _world.Query(in _allSwarmQuery, (ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
            {
                if (needed <= 0) return;
                if (swarm.OwnerId == 0 && swarm.State == SwarmState.Orbiting && swarm.Role == SwarmRole.DartNeedle)
                {
                    swarm.Role = SwarmRole.DrillDrone;
                    swarm.SpinRate = 32.0f;
                    vis.BaseColor = DrillColor;
                    vis.Color = DrillColor;
                    vis.Scale = 0.95f;
                    DrillDroneCount++;
                    DartNeedleCount = Math.Max(0, DartNeedleCount - 1);
                    needed--;
                }
            });
            RefreshFoundryGui();
        }

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

            Vector2 tipPos = corePos + aimDir * (52.0f + chargeProgress * 38.0f);
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
        _gigaDrillTipPos = corePos + aimDir * (52.0f + chargeProgress * 38.0f);
        _gigaDrillAimDir = aimDir;
        _gigaDrillTargetPos = targetPos;
        _hasDefenderNoseBreached = false;
        _gigaDrillVel = aimDir * (1650.0f + chargeProgress * 450.0f);
        _gigaDrillFlightTimer = GigaDrillFlightDuration;
        _gigaDrillElapsedFlight = 0.0f;

        int totalUnits = Math.Max(1, _activeDrillFormationCount);
        int exhaustStartIdx = _gigaDrillExhaustStartIndex;
        int exhaustCount = Math.Max(1, totalUnits - exhaustStartIdx);

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
                    swarm.MaxHealth = 5; // Defender di Ballistic Shield punya 5 HP (tidak kebal, bisa hancur jika ditembak 5 kali)
                    swarm.Health = 5;
                }
                else
                {
                    swarm.MaxHealth = 1;
                    swarm.Health = 1;
                }
                swarm.TargetDirection = aimDir;
                swarm.LifeTime = 0.0f;
                swarm.MaxLifeTime = GigaDrillFlightDuration + 0.3f;

                if (swarm.Role == SwarmRole.DrillDrone)
                {
                    // Seluruh driller bertindak sebagai pendorong bertingkat dari belakang ke depan
                    // Semakin jauh jarak tempuh, semakin banyak driller yang mati terbakar sebagai thruster
                    int orderFromBack = (totalUnits - 1) - swarm.FormationIndex;
                    float ratio = (float)(orderFromBack + 1) / totalUnits;
                    swarm.ExhaustTriggerTime = ratio * (GigaDrillFlightDuration * 0.95f);

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
        });
    }

    private void TriggerNoseShieldBreach(float defConeLength)
    {
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

        _gigaDrillVel *= 1.35f;

        EmitSignal(SignalName.GigaDrillNoseBreached, breachCenter, _gigaDrillAimDir, breachedCount);
        RefreshFoundryGui();
    }

    private void UpdateActiveGigaDrill(float dt)
    {
        if (!_isGigaDrillActive) return;

        _gigaDrillFlightTimer -= dt;
        _gigaDrillElapsedFlight += dt;
        _gigaDrillSpinTime += dt * 44.0f;
        _gigaDrillTipPos += _gigaDrillVel * dt;

        int totalUnits = Math.Max(1, _activeDrillFormationCount);
        int exhaustStartIdx = Math.Max(1, _gigaDrillExhaustStartIndex);
        ComputeGigaDrillDimensions(totalUnits, out float drillLength, out float drillBaseRadius, out float densityFactor);

        Vector2 tangent = new Vector2(-_gigaDrillAimDir.Y, _gigaDrillAimDir.X);
        Vector2 basePos = _gigaDrillTipPos - _gigaDrillAimDir * drillLength;

        GigaDrillTipPosition = _gigaDrillTipPos;
        GigaDrillBasePosition = basePos;
        GigaDrillDirection = _gigaDrillAimDir;
        GigaDrillLength = drillLength;
        GigaDrillRadius = drillBaseRadius;
        GigaDrillDensityFactor = densityFactor;

        if (!_hasDefenderNoseBreached && _activeDefenderCoreCount > 0)
        {
            float distToTarget = _gigaDrillTipPos.DistanceTo(_gigaDrillTargetPos);
            Vector2 enemyCorePos = (_enemyCore != null && IsInstanceValid(_enemyCore))
                ? _enemyCore.GlobalPosition
                : Vector2.Zero;
            float distToEnemyCore = enemyCorePos != Vector2.Zero ? _gigaDrillTipPos.DistanceTo(enemyCorePos) : 9999.0f;

            bool nearTarget = (distToTarget <= 130.0f || distToEnemyCore <= 130.0f) && _gigaDrillElapsedFlight >= 0.08f;
            bool flightProgressBreach = _gigaDrillElapsedFlight >= GigaDrillFlightDuration * 0.75f;

            int aliveConeDefenders = 0;
            _world.Query(in _allSwarmQuery, (ref SwarmUnitComponent s) =>
            {
                if (s.OwnerId == 0 && s.Role == SwarmRole.Guardian && s.State == SwarmState.Launched)
                {
                    aliveConeDefenders++;
                }
            });

            // Jika seluruh defender cone mati tertembak musuh di perjalanan:
            if (aliveConeDefenders == 0)
            {
                _hasDefenderNoseBreached = true;
                _activeDefenderCoreCount = 0;
            }
            else if (nearTarget || flightProgressBreach)
            {
                float defConeLength = MathF.Min(56.0f, 20.0f + MathF.Sqrt(_activeDefenderCoreCount) * 6.5f);
                TriggerNoseShieldBreach(defConeLength);
            }
        }

        // Tabrakan & Gerusan Kinetik Aktif terhadap Kapal Induk Musuh (Enemy Core)
        if (_enemyCore != null && IsInstanceValid(_enemyCore))
        {
            float distToEnemy = _gigaDrillTipPos.DistanceTo(_enemyCore.GlobalPosition);
            float contactRadius = _enemyCore.CoreCollisionRadius + drillBaseRadius * 0.65f;

            if (distToEnemy <= contactRadius)
            {
                if (!_hasDefenderNoseBreached && _activeDefenderCoreCount > 0)
                {
                    float defConeLength = MathF.Min(56.0f, 20.0f + MathF.Sqrt(_activeDefenderCoreCount) * 6.5f);
                    TriggerNoseShieldBreach(defConeLength);
                }

                // OPSI 1: KINETIC DRAG & HEAVY GRINDING PADA ARMOR ENEMY CORE!
                float curSpd = _gigaDrillVel.Length();
                float decelSpd = MathF.Max(80.0f, curSpd - 3200.0f * dt);
                _gigaDrillVel = _gigaDrillAimDir * decelSpd;

                // Kerusakan gerusan terus menerus pada armor musuh
                _enemyCore.TakeDamage(2500.0f * dt, _gigaDrillAimDir);
                _enemyCore.Velocity += _gigaDrillAimDir * (380.0f * dt);

                for (int s = 0; s < 3; s++)
                {
                    Vector2 sparkJitter = new Vector2((GD.Randf() - 0.5f) * 22.0f, (GD.Randf() - 0.5f) * 22.0f);
                    TriggerClashAnimation(_gigaDrillTipPos + sparkJitter, dropScrap: false);
                }

                _drillEnemyGrindTimer += dt;
                _drillAttritionTimer += dt;
                if (_drillAttritionTimer >= 0.10f)
                {
                    _drillAttritionTimer = 0.0f;
                    WearDownFrontDriller();
                }

                // OPSI 2: TERMINAL BUNKER BUSTER DETONATION PADA ENEMY CORE!
                if (_drillEnemyGrindTimer >= 0.35f || _gigaDrillVel.Length() <= 140.0f || _enemyCore.Health <= 0.0f)
                {
                    TriggerGigaDrillBunkerBuster(null, _enemyCore);
                    return;
                }
            }
            else
            {
                _drillEnemyGrindTimer = MathF.Max(0.0f, _drillEnemyGrindTimer - dt * 2.0f);
            }
        }

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

            if (swarm.Role != SwarmRole.DrillDrone)
                return;

            if (swarm.ExhaustTriggerTime >= 0.0f && _gigaDrillElapsedFlight >= swarm.ExhaustTriggerTime)
            {
                IgniteUnitAsExhaust(ref pos, ref vel, ref swarm, ref vis, basePos, _gigaDrillAimDir, 980.0f);
                return;
            }

            int idx = swarm.FormationIndex;
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
            if (s.OwnerId == 0 && s.Role == SwarmRole.DrillDrone && s.State == SwarmState.Launched)
            {
                aliveDrillers++;
            }
        });

        // Jika seluruh driller habis terbakar karena jarak terlalu jauh atau tertembak musuh: Serangan GAGAL!
        if (aliveDrillers == 0)
        {
            _isGigaDrillActive = false;
            _gigaDrillFlightTimer = 0.0f;
            TriggerClashAnimation(_gigaDrillTipPos, dropScrap: true);
            EmitSignal(SignalName.GigaDrillDetonated, _gigaDrillTipPos, basePos, 0);
            return;
        }

        if (_gigaDrillFlightTimer <= 0.0f)
        {
            _isGigaDrillActive = false;
            int sacrificedCount = Math.Max(1, _activeDrillFormationCount - _gigaDrillExhaustStartIndex);
            EmitSignal(SignalName.GigaDrillDetonated, _gigaDrillTipPos, basePos, sacrificedCount);

            _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
            {
                if (swarm.State == SwarmState.Launched && swarm.OwnerId == 0)
                {
                    if (swarm.Role == SwarmRole.DrillDrone)
                    {
                        if (swarm.ExhaustTriggerTime >= 0.0f)
                        {
                            IgniteUnitAsExhaust(ref pos, ref vel, ref swarm, ref vis, basePos, _gigaDrillAimDir, 850.0f);
                        }
                        else
                        {
                            swarm.State = SwarmState.Returning;
                            swarm.LifeTime = 0.0f;
                            float scatter = (GD.Randf() - 0.5f) * Mathf.Tau;
                            vel.Value = new Vector2(MathF.Cos(scatter), MathF.Sin(scatter)) * 420.0f;
                        }
                    }
                    else if (swarm.Role == SwarmRole.Guardian)
                    {
                        swarm.State = SwarmState.Dead;
                        vis.Color = new Color(0, 0, 0, 0);
                        _entityCount = Math.Max(0, _entityCount - 1);
                        GuardianCount = Math.Max(0, GuardianCount - 1);
                    }
                }
            });
            _activeDefenderCoreCount = 0;
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

    private void CheckAsteroidCollisions(float dt)
    {
        if (!_isGigaDrillActive) return;

        bool isGrindingAsteroid = false;
        var asteroids = Asteroid.ActiveAsteroids;
        for (int i = 0; i < asteroids.Count; i++)
        {
            var ast = asteroids[i];
            if (ast == null || !ast.IsActiveInTree) continue;

            float outerReach = ast.OuterBoundRadius + GigaDrillRadius + 18.0f;
            float distSq = ast.CachedPosition.DistanceSquaredTo(_gigaDrillTipPos);
            if (distSq > outerReach * outerReach) continue;

            // Gali lingkaran voxel di titik ujung bor
            int carved = ast.CarveVoxelCircle(_gigaDrillTipPos, GigaDrillRadius * 0.88f);
            float rockReach = ast.Radius + GigaDrillRadius * 0.5f;
            bool inRock = distSq <= rockReach * rockReach;

            if (carved > 0 || inRock)
            {
                isGrindingAsteroid = true;

                // OPSI 1: KINETIC DRAG & ACTIVE GRINDING!
                // Kecepatan bor anjlok drastis akibat friksi batuan keras
                float curSpeed = _gigaDrillVel.Length();
                float decelSpeed = MathF.Max(120.0f, curSpeed - 2400.0f * dt);
                _gigaDrillVel = _gigaDrillAimDir * decelSpeed;

                // Gaya dorong & torsi kinetik ke batuan asteroid
                ast.ApplyCentralImpulse(_gigaDrillAimDir * (85000.0f * dt));
                ast.ApplyTorqueImpulse((GD.Randf() > 0.5f ? 1.0f : -1.0f) * 35000.0f * dt);

                // Percikan bunga api penggalian batuan (sparks)
                for (int s = 0; s < 3; s++)
                {
                    Vector2 sparkJitter = new Vector2((GD.Randf() - 0.5f) * 18.0f, (GD.Randf() - 0.5f) * 18.0f);
                    TriggerClashAnimation(_gigaDrillTipPos + sparkJitter, dropScrap: false);
                }

                // Keausan mata bor (Drill Wear / Attrition)
                _drillAttritionTimer += dt;
                if (_drillAttritionTimer >= 0.12f)
                {
                    _drillAttritionTimer = 0.0f;
                    WearDownFrontDriller();
                }

                _drillAsteroidGrindTimer += dt;

                // OPSI 2: TERMINAL BUNKER BUSTER DETONATION!
                // Meledak jika kecepatan bor turun di bawah 170 px/s, atau grinding > 0.55s, atau waktu terbang habis
                if (_gigaDrillVel.Length() <= 170.0f || _drillAsteroidGrindTimer >= 0.55f || _gigaDrillFlightTimer <= 0.05f)
                {
                    TriggerGigaDrillBunkerBuster(ast, null);
                    return;
                }

                break;
            }
        }

        if (!isGrindingAsteroid)
        {
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
            if (swarm.OwnerId == 0 && swarm.Role == SwarmRole.DrillDrone && swarm.State == SwarmState.Launched)
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

    private void TriggerGigaDrillBunkerBuster(Asteroid? hitAsteroid, EnemyCore? hitEnemyCore)
    {
        _isGigaDrillActive = false;
        _gigaDrillFlightTimer = 0.0f;
        _drillAsteroidGrindTimer = 0.0f;
        _drillEnemyGrindTimer = 0.0f;

        Vector2 detPos = _gigaDrillTipPos;
        Vector2 basePos = GigaDrillBasePosition;

        if (hitAsteroid != null && IsInstanceValid(hitAsteroid))
        {
            hitAsteroid.CarveVoxelCircle(detPos, GigaDrillRadius * 2.3f);
            hitAsteroid.ApplyCentralImpulse(_gigaDrillAimDir * 240000.0f);
            hitAsteroid.ApplyTorqueImpulse((GD.Randf() > 0.5f ? 1.0f : -1.0f) * 60000.0f);
        }

        if (hitEnemyCore != null && IsInstanceValid(hitEnemyCore))
        {
            hitEnemyCore.TakeDamage(1800.0f, _gigaDrillAimDir);
            hitEnemyCore.Velocity += _gigaDrillAimDir * 780.0f;
        }

        // Ledakan katastropik: 14 ledakan percikan dan scrap di sekitar titik detonasi
        for (int i = 0; i < 14; i++)
        {
            float r = GD.Randf() * GigaDrillRadius * 1.5f;
            float a = GD.Randf() * Mathf.Tau;
            Vector2 p = detPos + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r;
            TriggerClashAnimation(p, dropScrap: (i % 2 == 0));
        }

        // Konsumsi unit hulu ledak (maksimal 35% formasi meledak jadi serpihan)
        // Unit driller belakang yang selamat terlempar keluar dan kembali ke orbit (Returning)
        int totalUnits = _activeDrillFormationCount;
        int warheadSacrifice = Math.Max(1, (int)(totalUnits * 0.35f));
        int sacrificedCount = 0;

        _world.Query(in _allSwarmQuery, (Entity ent, ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
        {
            if (swarm.OwnerId == 0 && swarm.State == SwarmState.Launched)
            {
                if (swarm.Role == SwarmRole.DrillDrone)
                {
                    if (swarm.FormationIndex < warheadSacrifice)
                    {
                        swarm.State = SwarmState.Dead;
                        vis.Color = new Color(0, 0, 0, 0);
                        _entityCount = Math.Max(0, _entityCount - 1);
                        IncrementRoleCounter(SwarmRole.DrillDrone, -1);
                        sacrificedCount++;
                    }
                    else
                    {
                        swarm.State = SwarmState.Returning;
                        swarm.LifeTime = 0.0f;
                        float scatterAngle = _gigaDrillAimDir.Angle() + Mathf.Pi + (GD.Randf() - 0.5f) * 1.8f;
                        vel.Value = new Vector2(MathF.Cos(scatterAngle), MathF.Sin(scatterAngle)) * (360.0f + GD.Randf() * 200.0f);
                    }
                }
                else if (swarm.Role == SwarmRole.Guardian)
                {
                    swarm.State = SwarmState.Dead;
                    vis.Color = new Color(0, 0, 0, 0);
                    _entityCount = Math.Max(0, _entityCount - 1);
                    GuardianCount = Math.Max(0, GuardianCount - 1);
                }
            }
        });

        _activeDefenderCoreCount = 0;
        _activeDrillFormationCount = 0;

        EmitSignal(SignalName.GigaDrillDetonated, detPos, basePos, sacrificedCount);
        RefreshFoundryGui();
    }

    private void OnPlayerEscapePodEjected(Vector2 ejectionPos, Vector2 ejectionVel)
    {
        for (int i = 0; i < 16; i++)
        {
            Vector2 p = ejectionPos + new Vector2((GD.Randf() - 0.5f) * 48.0f, (GD.Randf() - 0.5f) * 48.0f);
            TriggerClashAnimation(p, dropScrap: true);
        }
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
        for (int i = 0; i < 16; i++)
        {
            Vector2 p = ejectionPos + new Vector2((GD.Randf() - 0.5f) * 48.0f, (GD.Randf() - 0.5f) * 48.0f);
            TriggerClashAnimation(p, dropScrap: true);
        }
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
            Color minerColor = new Color(3.5f, 1.8f, 0.4f, 1.0f);

            float orbitDist = 42.0f;
            float orbitSpd = 1.6f;
            float angle = GD.Randf() * Mathf.Tau;
            Vector2 offset = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * orbitDist;

            var pos = new PositionComponent(enemyCorePos + offset);
            var vel = new VelocityComponent(Vector2.Zero);
            var unit = new SwarmUnitComponent(SwarmRole.MiningDroid, ownerId: 1, orbitDist, orbitSpd)
            {
                State = SwarmState.Orbiting,
                MaxLifeTime = 9999.0f
            };
            var vis = new VisualComponent(minerColor, 0.65f);

            _world.Create(pos, vel, unit, vis);
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

        QueueRedraw();
    }

    private void SpawnVoxelErosionDebris(Vector2 worldCenter, Vector2 outNormal, VoxelType brokenType)
    {
        if (brokenType == VoxelType.Rock)
        {
            for (int i = 0; i < 3; i++)
            {
                float spread = (GD.Randf() - 0.5f) * 1.35f;
                Vector2 dir = outNormal.Rotated(spread);
                float speed = 45.0f + GD.Randf() * 95.0f;
                float life = 0.20f + GD.Randf() * 0.18f;

                _miningDebris.Add(new MiningDebrisParticle
                {
                    Position = worldCenter + new Vector2((GD.Randf() - 0.5f) * 6.0f, (GD.Randf() - 0.5f) * 6.0f),
                    Velocity = dir * speed,
                    Color = new Color(0.55f, 0.48f, 0.42f, 0.78f).Lerp(new Color(1.6f, 0.85f, 0.35f, 0.85f), GD.Randf() * 0.45f),
                    Life = life,
                    MaxLife = life,
                    Size = 1.6f + GD.Randf() * 1.4f,
                    IsCrystalMatter = false
                });
            }
        }
        else if (Asteroid.TryGetResourceType(brokenType, out ResourceType resType))
        {
            Color resCol = Asteroid.GetResourceColor(resType);
            for (int i = 0; i < 2; i++)
            {
                float spread = (GD.Randf() - 0.5f) * 1.6f;
                Vector2 dir = outNormal.Rotated(spread);
                float speed = 90.0f + GD.Randf() * 110.0f;

                _miningDebris.Add(new MiningDebrisParticle
                {
                    Position = worldCenter + new Vector2((GD.Randf() - 0.5f) * 5.0f, (GD.Randf() - 0.5f) * 5.0f),
                    Velocity = dir * speed,
                    Color = resCol,
                    Life = 1.45f,
                    MaxLife = 1.45f,
                    Size = 3.4f,
                    IsCrystalMatter = true,
                    MatterType = resType
                });
            }
        }
    }

    private void UpdateMiningDebris(float dt, Vector2 corePos)
    {
        if (_miningDebris.Count == 0) return;

        bool redrawNeeded = !_isMiningLaserActive && !_isBoostActive;
        bool stockChanged = false;

        for (int i = _miningDebris.Count - 1; i >= 0; i--)
        {
            var p = _miningDebris[i];
            p.Life -= dt;

            if (p.IsCrystalMatter)
            {
                Vector2 toCore = corePos - p.Position;
                float dist = toCore.Length();
                if (dist <= 24.0f)
                {
                    int rIdx = (int)p.MatterType;
                    if (rIdx >= 0 && rIdx < _resourceStock.Length)
                    {
                        _resourceStock[rIdx] = InfiniteDebugResources ? InfiniteStockValue : (_resourceStock[rIdx] + 2);
                        stockChanged = true;
                    }

                    // Daur ulang puing menjadi Dart tambahan untuk koloni pemain!
                    IncrementRoleCounter(SwarmRole.DartNeedle, 1);
                    _entityCount++;
                    SpawnOneOrbitingDartNearPlayer();

                    _miningDebris.RemoveAt(i);
                    continue;
                }

                // MAGNET PULL: Hanya aktif saat kapal pemain mendekat (dist <= 180 px)!
                if (dist <= 180.0f)
                {
                    Vector2 pullDir = toCore / MathF.Max(0.001f, dist);
                    p.Velocity = p.Velocity.Lerp(pullDir * 780.0f, MathF.Min(1.0f, dt * 7.5f));
                    p.Position += p.Velocity * dt;
                }
                else
                {
                    // Melayang pelan di luar angkasa dengan inersia
                    p.Velocity *= MathF.Pow(0.92f, dt * 60.0f);
                    p.Position += p.Velocity * dt;
                }
            }
            else
            {
                p.Velocity *= MathF.Pow(0.88f, dt * 60.0f);
                p.Position += p.Velocity * dt;
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

                if (p.IsCrystalMatter)
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

            // 2. Gambar Efek Visual Sacrificial Dart Plasma Boost [SPACE], Kubah Parabola, & Retro-Brake
            if (_isBoostActive && _activeThrusterJets.Count > 0)
            {
                DrawDartPlasmaBoostVfx();
            }
            if (_isBrakeActive && _activeBrakeJets.Count > 0)
            {
                DrawDartPlasmaBrakeVfx();
            }

            // 3. Gambar Efek Visual Laser Penambangan Multi-Blok
            if (!_isMiningLaserActive || _activeMinerPositions.Count == 0) return;

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
                else
                {
                    DrawCircle(_laserImpactPoint, mainBeamWidth * 0.5f, coreBeamColor);
                }
            }
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
        // C. SEMBURAN JET PLASMA UTAMA TERPUSAT & MACH SHOCK DIAMONDS
        // =========================================================================
        float mainJetLen = Mathf.Lerp(165.0f, 340.0f, power) * (0.94f + 0.06f * MathF.Sin(pulse * 3.2f));
        float throatW = 7.0f + 5.5f * power;
        float midW = 18.0f + 14.0f * power;
        Vector2 jetEnd = focalPos + backDir * mainJetLen;

        // 1. Selubung Luar Plasma (Outer Ionized Plume Envelope)
        var outerPlumePts = new Vector2[]
        {
            focalPos + tangent * throatW,
            focalPos + backDir * (mainJetLen * 0.28f) + tangent * midW,
            focalPos + backDir * (mainJetLen * 0.72f) + tangent * (midW * 0.62f),
            jetEnd,
            focalPos + backDir * (mainJetLen * 0.72f) - tangent * (midW * 0.62f),
            focalPos + backDir * (mainJetLen * 0.28f) - tangent * midW,
            focalPos - tangent * throatW
        };
        var outerPlumeCols = new Color[]
        {
            new(0.4f, 2.6f, 4.8f, 0.62f * power),
            new(0.8f, 1.8f, 4.4f, 0.42f * power),
            new(2.2f, 0.6f, 3.2f, 0.22f * power),
            new(2.8f, 0.2f, 1.8f, 0.0f),
            new(2.2f, 0.6f, 3.2f, 0.22f * power),
            new(0.8f, 1.8f, 4.4f, 0.42f * power),
            new(0.4f, 2.6f, 4.8f, 0.62f * power)
        };
        DrawPolygon(outerPlumePts, outerPlumeCols);

        // 2. Inti Jet Kecepatan Tinggi (High-Velocity Core Beam)
        float coreBeamLen = mainJetLen * 0.82f;
        DrawLine(focalPos, focalPos + backDir * coreBeamLen, new Color(1.2f, 3.8f, 5.4f, 0.78f * power), 6.8f * power, false);
        DrawLine(focalPos, focalPos + backDir * (coreBeamLen * 0.78f), new Color(4.6f, 5.0f, 5.8f, 0.94f * power), 3.0f * power, false);

        // 3. Cincin & Poligon Mach Shock Diamonds di Sepanjang Ekor Jet
        int diamondCount = 4 + (int)(power * 3.0f);
        float spacing = (mainJetLen * 0.78f) / (diamondCount + 0.5f);

        for (int d = 0; d < diamondCount; d++)
        {
            float flowOffset = ((pulse * 0.35f) % 1.0f) * spacing * 0.35f;
            float dist = (d + 0.65f) * spacing + flowOffset;
            float t = dist / mainJetLen;
            if (t >= 0.92f) continue;

            float fade = 1.0f - t;
            Vector2 dCenter = focalPos + backDir * dist;
            float dHalfLen = spacing * 0.36f * (0.7f + 0.3f * fade);
            float dHalfWidth = (8.5f + 7.5f * power) * fade;

            var diamondPts = new Vector2[]
            {
                dCenter - backDir * dHalfLen,
                dCenter + tangent * dHalfWidth,
                dCenter + backDir * dHalfLen,
                dCenter - tangent * dHalfWidth
            };
            Color dCol = new Color(
                Mathf.Lerp(4.8f, 2.8f, t),
                Mathf.Lerp(5.0f, 2.2f, t),
                5.6f,
                0.78f * fade * power
            );
            var diamondCols = new Color[] { dCol, dCol * 0.65f, dCol * 0.45f, dCol * 0.65f };
            DrawPolygon(diamondPts, diamondCols);

            // Cincin elips kompresi di simpul Mach Diamond
            DrawLine(dCenter - tangent * (dHalfWidth * 1.15f), dCenter + tangent * (dHalfWidth * 1.15f), new Color(5.2f, 5.0f, 3.8f, 0.82f * fade), 1.8f, false);
        }

        // 4. Pita Heliks Magnetik Kembar (Twin Magnetic Containment Helices)
        const int helixSteps = 18;
        Vector2 prevH1 = Vector2.Zero;
        Vector2 prevH2 = Vector2.Zero;
        for (int h = 0; h <= helixSteps; h++)
        {
            float ht = (float)h / helixSteps;
            float hDist = ht * (mainJetLen * 0.85f);
            float envW = Mathf.Lerp(throatW * 0.9f, midW * 0.85f, MathF.Sin(ht * Mathf.Pi));
            float phase = ht * Mathf.Tau * 2.2f - pulse * 1.4f;

            Vector2 basePt = focalPos + backDir * hDist;
            Vector2 pt1 = basePt + tangent * (MathF.Sin(phase) * envW);
            Vector2 pt2 = basePt - tangent * (MathF.Sin(phase) * envW);

            if (h > 0)
            {
                float hAlpha = (1.0f - ht) * 0.68f * power;
                Color hCol1 = new Color(1.4f, 4.2f, 5.6f, hAlpha);
                Color hCol2 = new Color(3.8f, 2.4f, 5.2f, hAlpha);
                DrawLine(prevH1, pt1, hCol1, 1.5f, false);
                DrawLine(prevH2, pt2, hCol2, 1.5f, false);
            }
            prevH1 = pt1;
            prevH2 = pt2;
        }
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
                case Key.E:
                    SpawnEnemySectorAttack(GetGlobalMousePosition(), 50);
                    break;
                case Key.Q:
                    FireRadialNova(80);
                    break;
                case Key.T:
                    IsEnemyFiring = !IsEnemyFiring;
                    break;
            }
        }
    }

    public override void _ExitTree()
    {
        _world?.Dispose();
    }
}
