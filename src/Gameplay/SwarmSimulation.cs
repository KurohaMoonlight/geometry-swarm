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
/// & GUI Pabrik Interaktif (Hive Foundry).
/// - Bug Fix Priming Laser: Menghilangkan sinar tembus asteroid pada masa priming (hanya bola fokus & feeder).
/// - Fitur Sacrificial Plasma Boost [SPACE]:
///   * Para Dart berkumpul ke belakang kapal berlawanan arah kursor membentuk nozzle pendorong plasma.
///   * Jika satu Dart mati terbakar, posisinya otomatis digantikan Dart lain yang masih mengorbit.
///   * Laju kematian Dart meningkat seiring bertambahnya kecepatan kapal hingga batas atas (BoostMaxSpeed).
/// - Mode Debug Infinite Resource: Stok seluruh bahan diatur tak terbatas (∞) untuk kemudahan pengujian.
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
    // PARAMETER DART PLASMA BOOST & RETRO-BRAKE [SPACE]
    // =========================================================================
    public struct DartThrusterJet
    {
        public Vector2 Position;
        public Vector2 Direction; // Arah semburan exhaust
        public float Power;
        public float Length;
        public Color ThemeColor;
        public bool IsBrake;
    }

    private const int MaxBoostThrusterSlots = 36;
    private const int MaxBrakeSlots = 14;
    private bool _isBoostActive = false;
    private bool _isBoostFullPower = false;
    private float _boostPrimingTimer = 0.0f;
    private float _boostPrimingDuration = 4.0f; // Dasar ~4.0s, semakin banyak dart semakin cepat
    private float _boostPulseTimer = 0.0f;
    private int _activeBoostDartCount = 0;
    private readonly bool[] _boostSlotOccupied = new bool[MaxBoostThrusterSlots];
    private readonly List<DartThrusterJet> _activeThrusterJets = new(MaxBoostThrusterSlots);
    private readonly List<Vector2> _activeThrusterDartPositions = new(MaxBoostThrusterSlots);

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
    // INVENTORI 5 RESOURCE (DEBUG INFINITE) & GUI PABRIK INTERAKTIF (HIVE FOUNDRY)
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

    public int GigaDrillTotalUnits => _activeDrillFormationCount + _activeDefenderCoreCount;
    public int GigaDrillDefenderCoreCount => _activeDefenderCoreCount;
    public int GigaDrillLanceCount => _activeDrillFormationCount;

    public override void _Ready()
    {
        _world = World.Create();
        _boidSystem = new BoidSystem(_world);
        _allSwarmQuery = new QueryDescription().WithAll<PositionComponent, VelocityComponent, SwarmUnitComponent, VisualComponent>();

        _playerCore = GetNode<PlayerCore>("PlayerCore");
        _renderer = GetNode<SwarmMultiMeshRenderer>("SwarmRenderer");

        InitializeRecipes();
        CallDeferred(MethodName.BuildInteractiveFoundryGui);
        SpawnStartingSwarm();
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
            HotkeyLabel = "[1]",
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
            HotkeyLabel = "[2]",
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
            HotkeyLabel = "[3]",
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
            Description = "Perisai putar tangguh penahan musuh",
            HotkeyLabel = "[4]",
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
            Description = "Penembus batu & tombak peluncur",
            HotkeyLabel = "[5]",
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
            HotkeyLabel = "[6]",
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
            HotkeyLabel = "[7]",
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
            HotkeyLabel = "[8]",
            IsCoreExpansion = true,
            OutputCount = 1,
            Costs = new[] { 35, 25, 25, 0, 20 },
            ThemeColor = new Color(1.0f, 0.88f, 0.35f)
        });
    }

    private const float FoundryPanelWidth = 380.0f;
    private const float FoundryOpenX = 16.0f;
    private const float FoundryClosedX = -410.0f;
    private const float FoundryOpenBtnX = 276.0f;
    private const float FoundryClosedBtnX = 0.0f;

    /// <summary>
    /// Membangun GUI Pabrik (Hive Foundry) bergaya Cyber-Glassmorphic dengan animasi sliding halus
    /// dan kartu unit ramah pengguna.
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
            CustomMinimumSize = new Vector2(FoundryPanelWidth - 24, 430),
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

            // Baris 1: Glyph + Hotkey + Nama Unit + Tombol Auto-Craft
            var topHBox = new HBoxContainer();
            topHBox.AddThemeConstantOverride("separation", 6);
            cardVBox.AddChild(topHBox);

            var titleLabel = new Label
            {
                Text = $"{rec.HotkeyLabel} {rec.Glyph} {rec.Name}",
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

        RefreshFoundryGui();
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
    }

    private bool IsMouseOverFoundryGui()
    {
        if (GetViewport().GuiGetHoveredControl() != null) return true;
        if (_foundryMainPanel != null && _foundryMainPanel.Visible)
        {
            Rect2 rect = _foundryMainPanel.GetGlobalRect();
            if (rect.HasPoint(GetViewport().GetMousePosition()))
            {
                return true;
            }
        }
        return false;
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

            if (role == SwarmRole.DartNeedle) DartNeedleCount++;
            if (role == SwarmRole.DrillDrone) DrillDroneCount++;
            if (role == SwarmRole.Guardian) GuardianCount++;
            if (role == SwarmRole.ScannerDrone) ScannerCount++;
            if (role == SwarmRole.MiningDroid) MiningCount++;
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

            if (role == SwarmRole.DartNeedle) DartNeedleCount++;
            if (role == SwarmRole.DrillDrone) DrillDroneCount++;
            if (role == SwarmRole.Guardian) GuardianCount++;
            if (role == SwarmRole.ScannerDrone) ScannerCount++;
            if (role == SwarmRole.MiningDroid) MiningCount++;

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

            bool isLeftClick = Input.IsMouseButtonPressed(MouseButton.Left) && !IsMouseOverFoundryGui();
            if (isLeftClick && !_isChargingDrill && !_isGigaDrillActive)
            {
                if (MiningCount > 0)
                {
                    UpdateMiningLaser(dt, corePos, mousePos);
                }

                if (_fireCooldown <= 0.0f)
                {
                    FireSwarmStream(mousePos, count: 2);
                    _fireCooldown = StreamFireInterval;
                }
            }
            else
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

            UpdateMiningDebris(dt, corePos);
            HandleGigaDrillCharge(dt, corePos, coreVel, mousePos);
            UpdateActiveGigaDrill(dt);
            UpdateDartBoost(dt, corePos, coreVel, mousePos);
            UpdateExhaustDrones(dt);
            CheckAsteroidCollisions(dt);

            _boidSystem.Update(dt, corePos, coreVel, mousePos);

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

    // =========================================================================
    // SISTEM SACRIFICIAL DART PLASMA BOOST [SPACE]
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

        // 1. Periksa slot pendorong yang sedang terisi oleh DartNeedle
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

        // 2. Tarik DartNeedle dari Orbiting/Returning untuk mengisi slot 3/4 busur lingkaran di belakang Core
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

        // Jika sudah tidak ada DartNeedle yang tersisa sama sekali, hentikan boost
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

        // 3. Masa Priming Plasma: Berbobot ~4 detik tergantung jumlah dart
        //    Semakin banyak dart yang terpasang di busur, induksi plasma semakin cepat (1.5s - 4.0s)
        _boostPrimingDuration = Mathf.Clamp(4.0f / (1.0f + (activeCount - 6) * 0.055f), 1.5f, 4.0f);
        _boostPrimingTimer += dt;
        float primingProgress = Mathf.Clamp(_boostPrimingTimer / _boostPrimingDuration, 0.0f, 1.0f);
        _isBoostFullPower = (_boostPrimingTimer >= _boostPrimingDuration);

        _boostPulseTimer += dt * (18.0f + primingProgress * 30.0f);
        float thrusterFillRatio = Mathf.Clamp((float)activeCount / 18.0f, 0.35f, 1.0f);

        if (_isBoostFullPower)
        {
            float boostAccel = 6800.0f * thrusterFillRatio;
            _playerCore.IsBoosting = true;
            _playerCore.BoostThrustVector = aimDir * boostAccel;

            float currentSpeed = _playerCore.Velocity.Length();
            float speedNorm = Mathf.Clamp(
                (currentSpeed - _playerCore.MaxSpeed * 0.60f) / MathF.Max(1.0f, _playerCore.BoostMaxSpeed - _playerCore.MaxSpeed * 0.60f),
                0.0f,
                1.0f
            );
            BoostPowerRatio = Mathf.Clamp(0.55f + speedNorm * 0.45f, 0.55f, 1.0f);
        }
        else
        {
            // Selama masa priming: pendorong memberikan gaya dorong pemanasan bertahap
            float warmupAccel = 1200.0f * primingProgress * thrusterFillRatio;
            _playerCore.IsBoosting = true;
            _playerCore.BoostThrustVector = aimDir * warmupAccel;
            BoostPowerRatio = Mathf.Lerp(0.12f, 0.50f, primingProgress);
        }

        // 4. Formasi Dart 3/4 Busur Lingkaran (Arc Span = 1.5 * PI = 270 derajat) di belakang Core
        _activeThrusterJets.Clear();
        _activeThrusterDartPositions.Clear();

        float coreRadius = 24.0f + MathF.Sqrt(Math.Max(1, _playerCore.CoreCellCount)) * 9.0f;
        float arcRadius = coreRadius + 14.0f;
        float arcSpan = 1.5f * Mathf.Pi; // 270 derajat menyebar di belakang Core
        float baseAngle = backDir.Angle();
        float pulseTime = _boostPulseTimer;
        Vector2 shipVel = _playerCore.Velocity;
        float power = BoostPowerRatio;

        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
        {
            if (swarm.OwnerId != 0 || swarm.Role != SwarmRole.DartNeedle || swarm.State != SwarmState.BoostThruster)
                return;

            int slot = Math.Clamp(swarm.FormationIndex, 0, MaxBoostThrusterSlots - 1);
            swarm.LifeTime += dt;

            float arcT = (activeCount > 1)
                ? ((float)slot / (activeCount - 1) - 0.5f)
                : 0.0f;
            float angle = baseAngle + arcT * arcSpan;

            float microJitter = MathF.Sin(pulseTime * 2.8f + slot * 1.6f) * (1.2f + power * 2.5f);
            float currentArcRadius = arcRadius + microJitter;
            Vector2 slotWorldPos = corePos + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * currentArcRadius;
            Vector2 slotOutwardDir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));

            // Exhaust mengarah ke belakang dan sedikit menyebar radial dari pusat core
            Vector2 dartExhaustDir = (backDir * 0.72f + slotOutwardDir * 0.28f).Normalized();

            float lockProgress = Mathf.Clamp(swarm.LifeTime / 0.14f, 0.0f, 1.0f);
            Vector2 toSlot = slotWorldPos - pos.Value;
            Vector2 targetVel = shipVel + toSlot * Mathf.Lerp(16.0f, 36.0f, lockProgress);

            vel.Value = vel.Value.Lerp(targetVel, MathF.Min(1.0f, 22.0f * dt));
            pos.Value = pos.Value.Lerp(slotWorldPos, MathF.Min(1.0f, (12.0f + 18.0f * lockProgress) * dt));

            // Orientasi: Dart menghadap ke Core (ekor/nozzle menghadap ke dartExhaustDir)
            float targetSpin = (-dartExhaustDir).Angle() + Mathf.Pi * 0.5f;
            swarm.SpinAngle = Mathf.LerpAngle(swarm.SpinAngle, targetSpin, MathF.Min(1.0f, 24.0f * dt));

            // Visual: Dart menyala sesuai fase priming vs full power
            if (_isBoostFullPower)
            {
                float heatPulse = 0.5f + 0.5f * MathF.Sin(pulseTime * 3.4f + slot);
                vis.Color = new Color(
                    Mathf.Lerp(1.8f, 5.0f, power),
                    Mathf.Lerp(3.2f, 4.8f, heatPulse),
                    Mathf.Lerp(4.5f, 5.8f, power),
                    1.0f
                );
                vis.Scale = Mathf.Lerp(0.85f, 1.15f, power);
            }
            else
            {
                vis.Color = new Color(
                    Mathf.Lerp(1.2f, 3.8f, primingProgress),
                    Mathf.Lerp(2.2f, 3.2f, primingProgress),
                    Mathf.Lerp(4.0f, 5.2f, primingProgress),
                    1.0f
                );
                vis.Scale = Mathf.Lerp(0.65f, 0.85f, primingProgress);
            }

            // Semburan plasma langsung dari masing-masing dart
            float jetLen = _isBoostFullPower
                ? Mathf.Lerp(75.0f, 195.0f, power) * (0.88f + 0.12f * MathF.Sin(pulseTime * 2.4f + slot))
                : Mathf.Lerp(16.0f, 48.0f, primingProgress) * (0.85f + 0.15f * MathF.Sin(pulseTime * 4.2f + slot * 2.0f));

            _activeThrusterJets.Add(new DartThrusterJet
            {
                Position = pos.Value,
                Direction = dartExhaustDir,
                Power = power,
                Length = jetLen,
                ThemeColor = vis.Color,
                IsBrake = false
            });
            _activeThrusterDartPositions.Add(pos.Value);
        });

        BoostNozzlePosition = corePos + backDir * arcRadius;
        QueueRedraw();
    }

    private void DeactivateDartBoost()
    {
        _isBoostActive = false;
        _isBoostFullPower = false;
        _boostPrimingTimer = 0.0f;
        _activeBoostDartCount = 0;
        _activeThrusterJets.Clear();
        _activeThrusterDartPositions.Clear();

        if (_playerCore != null)
        {
            _playerCore.IsBoosting = false;
            _playerCore.BoostThrustVector = Vector2.Zero;
        }

        _world.Query(in _allSwarmQuery, (ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
        {
            if (swarm.OwnerId == 0 && swarm.State == SwarmState.BoostThruster)
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

        int defenderAPQuota = Math.Max(2, (int)MathF.Round(readyDefenderCount * 0.25f));
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
                swarm.TargetDirection = aimDir;
                swarm.LifeTime = 0.0f;
                swarm.MaxLifeTime = GigaDrillFlightDuration + 0.2f;

                if (swarm.Role == SwarmRole.DrillDrone && swarm.FormationIndex >= exhaustStartIdx)
                {
                    int orderFromBack = (totalUnits - 1) - swarm.FormationIndex;
                    float ratio = (float)orderFromBack / exhaustCount;
                    swarm.ExhaustTriggerTime = ratio * (GigaDrillFlightDuration * 0.82f);

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
        _world.Query(in _allSwarmQuery, (ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
        {
            if (swarm.OwnerId == 0 && swarm.Role == SwarmRole.Guardian && swarm.State == SwarmState.Launched)
            {
                swarm.State = SwarmState.Dead;
                vis.Color = new Color(0, 0, 0, 0);
                breachedCount++;
            }
        });

        _entityCount = Math.Max(0, _entityCount - breachedCount);
        GuardianCount = Math.Max(0, GuardianCount - breachedCount);
        _activeDefenderCoreCount = 0;

        _gigaDrillVel *= 1.38f;

        EmitSignal(SignalName.GigaDrillNoseBreached, breachCenter, _gigaDrillAimDir, breachedCount);
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
            bool nearTarget = distToTarget <= 140.0f && _gigaDrillElapsedFlight >= 0.12f;
            bool flightProgressBreach = _gigaDrillElapsedFlight >= GigaDrillFlightDuration * 0.65f;

            float defConeLength = MathF.Min(56.0f, 20.0f + MathF.Sqrt(_activeDefenderCoreCount) * 6.5f);
            Vector2 noseApex = _gigaDrillTipPos + _gigaDrillAimDir * defConeLength;
            int nearbyEnemies = 0;
            _world.Query(in _allSwarmQuery, (ref PositionComponent p, ref SwarmUnitComponent s) =>
            {
                if (s.OwnerId != 0 && s.State != SwarmState.Dead)
                {
                    if (p.Value.DistanceSquaredTo(noseApex) <= 65.0f * 65.0f)
                    {
                        nearbyEnemies++;
                    }
                }
            });
            bool enemyImpactBreach = nearbyEnemies >= 2;

            if (nearTarget || flightProgressBreach || enemyImpactBreach)
            {
                TriggerNoseShieldBreach(defConeLength);
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
        }
    }

    private void UpdateExhaustDrones(float dt)
    {
        float drag = MathF.Pow(0.92f, dt * 60.0f);

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
                    if (swarm.Role == SwarmRole.DartNeedle)
                    {
                        DartNeedleCount = Math.Max(0, DartNeedleCount - 1);
                    }
                    else if (swarm.Role == SwarmRole.DrillDrone)
                    {
                        DrillDroneCount = Math.Max(0, DrillDroneCount - 1);
                    }
                    else if (swarm.Role == SwarmRole.Guardian)
                    {
                        GuardianCount = Math.Max(0, GuardianCount - 1);
                    }
                }
            }
        });
    }

    private void CheckAsteroidCollisions(float _dt)
    {
        if (!_isGigaDrillActive) return;

        var asteroids = Asteroid.ActiveAsteroids;
        for (int i = 0; i < asteroids.Count; i++)
        {
            var ast = asteroids[i];
            if (ast == null || !ast.IsActiveInTree) continue;

            float drillHitRadius = ast.OuterBoundRadius + GigaDrillRadius + 24.0f;
            if (ast.CachedPosition.DistanceSquaredTo(_gigaDrillTipPos) <= drillHitRadius * drillHitRadius
                || ast.CachedPosition.DistanceSquaredTo(GigaDrillBasePosition) <= drillHitRadius * drillHitRadius)
            {
                ast.ApplyDrillBreach(_gigaDrillTipPos, _gigaDrillVel, 35000.0f, GigaDrillRadius * 0.9f);
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
                if (dist <= 22.0f)
                {
                    int rIdx = (int)p.MatterType;
                    if (rIdx >= 0 && rIdx < _resourceStock.Length)
                    {
                        _resourceStock[rIdx] = InfiniteDebugResources ? InfiniteStockValue : (_resourceStock[rIdx] + 2);
                        stockChanged = true;
                    }

                    _miningDebris.RemoveAt(i);
                    continue;
                }

                Vector2 pullDir = toCore / MathF.Max(0.001f, dist);
                p.Velocity = p.Velocity.Lerp(pullDir * 720.0f, MathF.Min(1.0f, dt * 6.8f));
                p.Position += p.Velocity * dt;
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

            // 2. Gambar Efek Visual Dart Plasma Boost [SPACE] & Retro-Rocket Braking
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
                // =========================================================================
                // TAHAP PRIMING: HANYA BOLA FOKUS ENERGI & PERCIKAN KONVERGEN
                // (Tanpa seberkas cahaya tracer ke depan agar tidak menembus asteroid!)
                // =========================================================================
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
                // =========================================================================
                // TAHAP MENEMBAK PENUH (HEAVY MINING LASER BLAST)
                // =========================================================================
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

    private void DrawDartPlasmaBoostVfx()
    {
        float power = BoostPowerRatio;
        float pulse = _boostPulseTimer;
        int dartCount = _activeThrusterJets.Count;
        if (dartCount == 0) return;

        bool isFull = _isBoostFullPower;

        // 1. Busur Induksi Listrik / Plasma Arcs antar Dart yang berdampingan sepanjang 3/4 busur lingkaran
        if (dartCount > 1)
        {
            for (int i = 0; i < dartCount - 1; i++)
            {
                Vector2 pA = _activeThrusterDartPositions[i];
                Vector2 pB = _activeThrusterDartPositions[i + 1];
                Vector2 mid = (pA + pB) * 0.5f;
                Vector2 delta = pB - pA;
                Vector2 normal = delta.LengthSquared() > 0.01f ? new Vector2(-delta.Y, delta.X).Normalized() : Vector2.Up;

                float jitterAmp = isFull ? (2.2f + power * 3.2f) : (1.8f + (1.0f - power) * 3.5f);
                float jitter = MathF.Sin(pulse * 4.5f + i * 2.7f) * jitterAmp;
                Vector2 arcMid = mid + normal * jitter;

                Color arcCol = isFull
                    ? new Color(2.6f, 4.4f, 5.8f, 0.85f)
                    : new Color(3.8f, 3.2f, 1.2f, 0.72f);

                DrawLine(pA, arcMid, arcCol, isFull ? 2.0f : 1.4f, false);
                DrawLine(arcMid, pB, arcCol, isFull ? 2.0f : 1.4f, false);
            }
        }

        // 2. Berkas Semburan Plasma Beam LANGSUNG dari Masing-Masing Dart (bukan di ruang kosong)
        for (int i = 0; i < dartCount; i++)
        {
            var jet = _activeThrusterJets[i];
            Vector2 origin = jet.Position;
            Vector2 dir = jet.Direction;
            Vector2 tangent = new Vector2(-dir.Y, dir.X);
            float len = jet.Length;

            if (isFull)
            {
                // Corong selubung plasma eksternal per-dart
                float sheathW = (4.0f + 5.5f * power);
                var sheathPts = new Vector2[]
                {
                    origin + tangent * sheathW,
                    origin + dir * (len * 0.45f) + tangent * (sheathW * 1.35f),
                    origin + dir * len,
                    origin + dir * (len * 0.45f) - tangent * (sheathW * 1.35f),
                    origin - tangent * sheathW
                };
                var sheathCols = new Color[]
                {
                    new(0.6f, 3.2f, 5.5f, 0.75f * power),
                    new(2.4f, 2.2f, 0.8f, 0.45f * power),
                    new(3.5f, 1.2f, 0.2f, 0.0f),
                    new(2.4f, 2.2f, 0.8f, 0.45f * power),
                    new(0.6f, 3.2f, 5.5f, 0.75f * power)
                };
                DrawPolygon(sheathPts, sheathCols);

                // Inti Plasma Beam
                float coreW = 2.4f + 3.2f * power;
                DrawLine(origin, origin + dir * len, new Color(1.8f, 4.2f, 5.8f, 0.92f), coreW * 1.8f, false);
                DrawLine(origin, origin + dir * (len * 0.85f), new Color(5.2f, 5.2f, 3.8f, 0.98f), coreW, false);

                // Mini Mach Shock Diamonds per-dart
                int diamonds = 2 + (int)(power * 2.0f);
                float dStep = len / (diamonds + 1);
                for (int d = 1; d <= diamonds; d++)
                {
                    Vector2 dCenter = origin + dir * (dStep * d);
                    float dw = (3.5f + power * 3.0f) * (1.0f - (float)d / (diamonds + 1));
                    DrawCircle(dCenter, MathF.Max(1.2f, dw * 0.6f), new Color(5.5f, 5.2f, 3.8f, 0.95f));
                }
            }
            else
            {
                // Mode Priming: semburan percikan api pra-pengapian kecil berderak
                DrawLine(origin, origin + dir * len, new Color(4.8f, 2.8f, 0.8f, 0.85f), 2.2f, false);
                DrawLine(origin, origin + dir * (len * 0.6f), new Color(5.4f, 4.8f, 1.8f, 0.98f), 1.2f, false);
                DrawCircle(origin, 2.8f * (0.8f + 0.2f * MathF.Sin(pulse * 3.5f + i)), new Color(4.8f, 3.8f, 1.2f, 0.9f));
            }
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

    public void FireSwarmStream(Vector2 targetPos, int count = 2)
    {
        int fired = 0;
        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm) =>
        {
            if (fired >= count) return;
            if (swarm.State != SwarmState.Orbiting || swarm.OwnerId != 0) return;
            if (swarm.Role == SwarmRole.Guardian
                || swarm.Role == SwarmRole.DrillDrone
                || swarm.Role == SwarmRole.MiningDroid
                || swarm.Role == SwarmRole.ScannerDrone) return;

            Vector2 toTarget = targetPos - pos.Value;
            if (toTarget.LengthSquared() < 1.0f) return;

            float spreadAngle = (GD.Randf() - 0.5f) * 0.12f;
            Vector2 dir = toTarget.Normalized().Rotated(spreadAngle);

            swarm.State = SwarmState.Launched;
            swarm.TargetDirection = dir;
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
            if (swarm.Role == SwarmRole.Guardian) return;

            float spreadAngle = (GD.Randf() - 0.5f) * 0.55f;
            Vector2 dir = baseDir.Rotated(spreadAngle);

            swarm.State = SwarmState.Launched;
            swarm.TargetDirection = dir;
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
            if (swarm.Role == SwarmRole.Guardian) return;

            float angle = ((float)fired / count) * Mathf.Tau;
            Vector2 dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));

            swarm.State = SwarmState.Launched;
            swarm.TargetDirection = dir;
            swarm.LifeTime = 0.0f;
            swarm.MaxLifeTime = 1.1f;

            vel.Value = dir * 1150.0f;
            fired++;
        });
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey keyEvent && keyEvent.Pressed)
        {
            switch (keyEvent.Keycode)
            {
                case Key.Tab:
                case Key.B:
                    ToggleFoundryPanel();
                    break;
                case Key.Key1:
                    if (!TryCraftRecipe(0)) AddSpecificSwarm(SwarmRole.DartNeedle, 10);
                    break;
                case Key.Key2:
                    if (!TryCraftRecipe(1)) AddSpecificSwarm(SwarmRole.MiningDroid, 5);
                    break;
                case Key.Key3:
                    if (!TryCraftRecipe(2)) AddSpecificSwarm(SwarmRole.ScannerDrone, 5);
                    break;
                case Key.Key4:
                    if (!TryCraftRecipe(3)) AddSpecificSwarm(SwarmRole.Guardian, 10);
                    break;
                case Key.Key5:
                    if (!TryCraftRecipe(4)) AddSpecificSwarm(SwarmRole.DrillDrone, 10);
                    break;
                case Key.Key6:
                    if (!TryCraftRecipe(5)) AddSpecificSwarm(SwarmRole.GigaShuriken, 10);
                    break;
                case Key.Key7:
                    if (!TryCraftRecipe(6)) AddSpecificSwarm(SwarmRole.CrescentBoomerang, 10);
                    break;
                case Key.Key8:
                    if (!TryCraftRecipe(7)) _playerCore.AddNextHoneycombCell();
                    break;
                case Key.Key9:
                    AddSpecificSwarm(SwarmRole.MiningDroid, 15);
                    break;
                case Key.E:
                    SpawnEnemySectorAttack(GetGlobalMousePosition(), 50);
                    break;
                case Key.Q:
                    FireRadialNova(80);
                    break;
            }
        }
    }

    public override void _ExitTree()
    {
        _world?.Dispose();
    }
}
