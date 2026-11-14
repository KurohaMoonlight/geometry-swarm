using System;
using System.Collections.Generic;
using GeometrySwarm.Core.ECS.Components;
using Godot;

namespace GeometrySwarm.Gameplay;

/// <summary>
/// Jenis mineral tambang & kristal energi untuk fabrikasi di Pabrik (Hive Foundry).
/// </summary>
public enum ResourceType : byte
{
    Ferrite = 0,   // Ferrite Alloy (Kuning Amber) - Rangka dasar semua drone & Dart Needle
    Quartz = 1,    // Optic Quartz (Sian Elektrik) - Lensa Scanner, Miner, Guardian, Boomerang
    Carbide = 2,   // Vulcan Carbide (Merah Bara) - Baja keras Drill Drone, Shuriken, Guardian
    Vortexum = 3,  // Vortexum Ore (Hijau Zamrud) - Pendorong kurva Boomerang & rotasi Shuriken
    Aether = 4     // Aether Crystal (Putih-Emas Menyala) - Energi murni Hive Core & baterai aktif
}

/// <summary>
/// Asteroid prosedural raksasa berbasis Grid Voxel 13 px & Entitas Kosmik Bintang Aether:
/// - Asteroid Batu Biasa:
///   * Memiliki deposit mineral acak (1-2 jenis resource).
///   * Saat seluruh deposit mineral di dalam asteroid habis ditambang (0 resource tersisa),
///     asteroid otomatis runtuh/hancur dengan animasi katastropik (tremor hebat + retakan jaring laba-laba menyala),
///     meledak menjadi puing kosmik, dan membuka ruang kosong di arena (100% tuntas dipanen).
/// - Bintang Aether (IsEnergyAsteroid):
///   * Memiliki siklus reinkarnasi kosmik abadi:
///     1. Dormant Seed (10 Detik): Setelah meledak Supernova & melontarkan kristal Aether, menyisakan
///        Biji Energi Kecil (Aether Seed) yang berpendar lembut dan diam di titik nol selama 10 detik.
///     2. Cosmic Drift / Migrasi (20 Detik): Meluncur pelan melintasi ruang angkasa selama 20 detik
///        mencari koordinat baru yang aman di sabuk asteroid.
///     3. Spacetime Infall & Rebirth (30 Detik): Berhenti di lokasi baru, menyedot energi ruang-waktu,
///        menarik grid arena & partikel cahaya secara agresif selama 30 detik, mengembang, mekar,
///        dan terlahir kembali menjadi Bintang Aether baru!
/// </summary>
public partial class Asteroid : RigidBody2D
{
    [Signal]
    public delegate void AsteroidDestroyedEventHandler(Vector2 position, float radius, Color resourceColor, bool isEnergy);

    public static readonly List<Asteroid> ActiveAsteroids = new();

    public const float VoxelSize = 13.0f;
    public const float InvVoxelSize = 1.0f / VoxelSize;

    /// <summary>
    /// HP struktural masif per tile 13px terhadap serangan NON-MINING LASER (Driller / Tabrakan Drone).
    /// Mining Laser tetap menggunakan ambang panas 1.0f (efisiensi penuh 100%).
    /// </summary>
    public const float NonLaserTileMaxHp = 45.0f;

    /// <summary>
    /// Durasi 3 Tahap Siklus Reinkarnasi Bintang Aether.
    /// </summary>
    public const float DormantSeedDuration = 10.0f;
    public const float CosmicDriftDuration = 20.0f;
    public const float SpacetimeInfallDuration = 30.0f;

    public enum AetherSeedStage : byte
    {
        ActiveStar = 0,
        DormantSeed = 1,
        CosmicDrift = 2,
        SpacetimeInfall = 3
    }

    public enum SeedTeleportState : byte
    {
        None = 0,
        Compressing = 1,
        Folded = 2,
        Expanding = 3
    }

    public enum VoxelType : byte
    {
        Empty = 0,
        Rock = 1,
        ResourceFerrite = 2,
        ResourceQuartz = 3,
        ResourceCarbide = 4,
        ResourceVortexum = 5,
        ResourceAether = 6
    }

    public struct VoxelRayHit
    {
        public bool Hit;
        public int GridX;
        public int GridY;
        public VoxelType Type;
        public Vector2 WorldHitPoint;
        public Vector2 WorldCellCenter;
        public float Distance;
        public float CurrentHeat;
    }

    private struct OutlineSegment
    {
        public Vector2 A;
        public Vector2 B;
        public int CheckGridX;
        public int CheckGridY;
    }

    private struct InfallPhotonParticle
    {
        public float Angle;
        public float Distance;
        public float MaxDistance;
        public float RadialSpeed;
        public float AngularSpeed;
        public float Size;
        public Color Color;
    }

    public struct CrystalSpireData
    {
        public Vector2 BaseCenter;
        public Vector2 BaseLeft;
        public Vector2 BaseRight;
        public Vector2 MidLeft;
        public Vector2 MidRight;
        public Vector2 RidgeCenter;
        public Vector2 Tip;
        public Vector2 Dir;
        public Vector2 Perp;
        public Vector2[] Polygon; // 5-point polygon untuk deteksi voxel solid presisi
        public ResourceType Resource;
        public bool HasResourceVein;
        public float Angle;
        public float Length;
        public float RadiusMid;
        public float HalfWidthBase;
        public float HalfWidthMid;
        public float Heat;
        public int TotalVoxels;
        public int RemainingVoxels;
    }

    public static int CurrentScannerCount { get; set; } = 5;

    [Export] public float Radius { get; set; } = 450.0f;
    [Export] public float MaxHealth { get; set; } = 2500.0f;
    [Export] public float CurrentHealth { get; set; } = 2500.0f;
    [Export] public SwarmRole AsteroidType { get; set; } = SwarmRole.DartNeedle;
    [Export] public bool IsEnergyAsteroid { get; set; } = false;
    [Export] public bool IsScanned { get; set; } = false;
    [Export] public bool IsBirthAnimationActive { get; set; } = false;
    public Vector2 BirthRiftOrigin { get; set; } = Vector2.Zero;
    public Vector2 BirthEjectDirection { get; set; } = Vector2.Right;

    // 1 atau 2 jenis resource berbeda di dalam asteroid ini
    public ResourceType PrimaryResource { get; set; } = ResourceType.Ferrite;
    public ResourceType SecondaryResource { get; set; } = ResourceType.Quartz;
    public bool HasSecondaryResource { get; set; } = false;

    // Status Siklus Reinkarnasi Bintang Aether
    public AetherSeedStage SeedStage { get; private set; } = AetherSeedStage.ActiveStar;
    public bool IsInSeedCycle => IsEnergyAsteroid && SeedStage != AetherSeedStage.ActiveStar;

    // Cache transformasi murni di C# (Zero P/Invoke overhead)
    public Vector2 CachedPosition { get; private set; }
    public float CachedRotation { get; private set; }
    public float CachedCos { get; private set; } = 1.0f;
    public float CachedSin { get; private set; } = 0.0f;
    public Vector2 CachedLinearVelocity { get; private set; }
    public bool IsActiveInTree => !_isDestroyed && !IsInSeedCycle;

    public Color ResourceGlowColor => GetResourceColor(IsEnergyAsteroid ? ResourceType.Aether : PrimaryResource);
    private Color _unscannedBorderColor;
    private Color _tunnelWallBorderColor;
    private Color _rockBodyColor;

    public Vector2[] PolygonVertices { get; private set; } = Array.Empty<Vector2>();
    private Vector2[] _closedOutlinePoints = Array.Empty<Vector2>();
    private readonly List<OutlineSegment> _perimeterSegments = new();

    public int GridSide { get; private set; }
    public Vector2 GridOriginLocal { get; private set; }
    public float OuterBoundRadius { get; private set; }

    private VoxelType[] _voxels = Array.Empty<VoxelType>();
    private bool[] _initialSolidMask = Array.Empty<bool>();
    private float[] _voxelHeat = Array.Empty<float>();
    private float[] _voxelKineticDamage = Array.Empty<float>();
    private readonly List<int> _activeHeatedIndices = new();
    private readonly HashSet<int> _heatedThisFrame = new();

    // Cache 2D Greedy Meshed Rectangles & Batched Lines per Tipe Resource (5 jenis resource)
    private const int ResourceCount = 5;
    private List<Rect2>[] _rockSpansByRow = Array.Empty<List<Rect2>>();
    private List<Rect2>[][] _resourceSpansByTypeAndRow = Array.Empty<List<Rect2>[]>();
    private readonly List<Rect2> _mergedRockRects = new();
    private readonly List<Rect2>[] _mergedResourceRects = new List<Rect2>[ResourceCount];
    private readonly Vector2[][] _batchedExposedResourceEdges = new Vector2[ResourceCount][];
    private Vector2[] _batchedWallLines = Array.Empty<Vector2>();
    private Vector2[] _batchedPerimeterLines = Array.Empty<Vector2>();
    private bool _geometryBatchDirty = true;

    private int _initialSolidVoxelCount = 0;
    private int _remainingSolidVoxelCount = 0;
    private int _initialResourceVoxelCount = 0;
    private int _remainingResourceVoxelCount = 0;
    private int _carvedVoxelCount = 0;
    private float _energyPulseTimer = 0.0f;
    private float _energyRedrawTimer = 0.0f;
    private readonly Vector2[] _reusableArcPts = new Vector2[4];
    private readonly Vector2[] _reusableDischPts = new Vector2[3];
    private CrystalSpireData[] _crystalSpires = Array.Empty<CrystalSpireData>();
    private float _crystalCoreRadius = 0.0f;
    private int _totalCoreVoxels = 0;
    private int _remainingCoreVoxels = 0;

    // Variabel Siklus Reinkarnasi Aether Seed (Dormant -> Drift -> Spacetime Infall -> Rebirth)
    private float _seedStageTimer = 0.0f;
    private Vector2 _seedDriftStartPos = Vector2.Zero;
    private Vector2 _seedDriftTargetPos = Vector2.Zero;
    private Vector2 _seedDriftPerpDir = Vector2.Up;
    private float _seedDriftArcAmplitude = 180.0f;
    private readonly List<Vector2> _seedDriftTrailWorld = new(32);
    private float _seedTrailSampleTimer = 0.0f;
    private const int InfallPhotonCount = 28;
    private readonly InfallPhotonParticle[] _infallPhotons = new InfallPhotonParticle[InfallPhotonCount];

    // Mekanika Teleportasi Lipat Ruang (Spacetime Fold & Warp Leap)
    private SeedTeleportState _seedTeleportState = SeedTeleportState.None;
    private float _seedTeleportStateTimer = 0.0f;
    private float _seedTeleportNextCooldown = 2.0f;
    private Vector2 _seedTeleportDeparturePos = Vector2.Zero;
    private Vector2 _seedTeleportTargetPos = Vector2.Zero;
    private const float SeedTeleportCompressDuration = 0.32f;
    private const float SeedTeleportFoldDuration = 0.10f;
    private const float SeedTeleportExpandDuration = 0.36f;

    // Animasi Kelahiran Asteroid dari White Hole (Efek Jin Aladdin / Genie Effect)
    private float _birthAnimTimer = 0.0f;
    private const float BirthAnimDuration = 1.65f;
    private float _birthGlow = 0.0f;
    private float _birthLongitudinalScale = 1.0f;
    private float _birthLateralScale = 1.0f;

    // Komponen Visual Bintang Mini & Gravitational Lensing (Shader Anomali Singularitas)
    private BackBufferCopy? _energyBackBuffer;
    private ColorRect? _energyStarRect;
    private ShaderMaterial? _energyStarMaterial;
    private float _energyLaserCavitation = 0.0f;
    private float _energyLaserHitAngle = 0.0f;
    private float _energyAccumulatedHeat = 0.0f;
    private bool _energyWasHitThisFrame = false;

    private CollisionShape2D _collisionShape = null!;
    private CircleShape2D _circleShape = null!;
    private int _lastDrawnScannerCount = -1;
    private bool _isDestroyed = false;
    private bool _isDestroying = false;
    private float _destructionTimer = 0.0f;
    private float _currentDestructionDuration = 0.72f;
    private readonly List<Vector2> _catastrophicFractureLines = new();

    public static Color GetResourceColor(ResourceType resType)
    {
        return resType switch
        {
            ResourceType.Ferrite => new Color(2.3f, 2.5f, 0.35f, 1.0f),   // Kuning Amber
            ResourceType.Quartz => new Color(0.3f, 2.4f, 3.1f, 1.0f),     // Sian Elektrik
            ResourceType.Carbide => new Color(3.3f, 0.65f, 0.22f, 1.0f),  // Merah Bara
            ResourceType.Vortexum => new Color(0.35f, 2.9f, 1.25f, 1.0f), // Hijau Zamrud
            ResourceType.Aether => new Color(3.8f, 3.2f, 1.15f, 1.0f),    // Putih-Emas Menyala
            _ => new Color(2.3f, 2.5f, 0.35f, 1.0f)
        };
    }

    public static VoxelType ResourceToVoxelType(ResourceType resType)
    {
        return resType switch
        {
            ResourceType.Ferrite => VoxelType.ResourceFerrite,
            ResourceType.Quartz => VoxelType.ResourceQuartz,
            ResourceType.Carbide => VoxelType.ResourceCarbide,
            ResourceType.Vortexum => VoxelType.ResourceVortexum,
            ResourceType.Aether => VoxelType.ResourceAether,
            _ => VoxelType.ResourceFerrite
        };
    }

    public static bool TryGetResourceType(VoxelType voxelType, out ResourceType resType)
    {
        switch (voxelType)
        {
            case VoxelType.ResourceFerrite:
                resType = ResourceType.Ferrite;
                return true;
            case VoxelType.ResourceQuartz:
                resType = ResourceType.Quartz;
                return true;
            case VoxelType.ResourceCarbide:
                resType = ResourceType.Carbide;
                return true;
            case VoxelType.ResourceVortexum:
                resType = ResourceType.Vortexum;
                return true;
            case VoxelType.ResourceAether:
                resType = ResourceType.Aether;
                return true;
            default:
                resType = ResourceType.Ferrite;
                return false;
        }
    }

    /// <summary>
    /// Mengekspor parameter sumur gravitasi aktif (untuk Bintang Aether aktif maupun Seed pada tahap Spacetime Infall / Drift)
    /// agar GridPhysics dan Parallax Starfield di GridRenderer ikut terdistorsi secara fisik & optik.
    /// </summary>
    public bool TryGetGravityWellParameters(
        out Vector2 worldPos,
        out float influenceRadius,
        out float normalizedStrength,
        out float infallRatio,
        out float gridPullAccel,
        out float gridSwirlAccel,
        out float gridHeatRate)
    {
        worldPos = CachedPosition;
        influenceRadius = 0.0f;
        normalizedStrength = 0.0f;
        infallRatio = 0.0f;
        gridPullAccel = 0.0f;
        gridSwirlAccel = 0.0f;
        gridHeatRate = 0.0f;

        if (_isDestroyed || !IsEnergyAsteroid) return false;

        switch (SeedStage)
        {
            case AetherSeedStage.ActiveStar:
            {
                float coreRatio = _totalCoreVoxels > 0
                    ? Mathf.Clamp((float)_remainingCoreVoxels / _totalCoreVoxels, 0.35f, 1.0f)
                    : 1.0f;
                float instab = _isDestroying ? 1.0f : Mathf.Clamp(_energyAccumulatedHeat, 0.0f, 1.0f);

                // Massa materi eksotis super-berat: jangkauan luas & hisapan corong ruang-waktu sangat dalam
                influenceRadius = Radius * (5.6f + instab * 1.4f);
                normalizedStrength = (1.35f + instab * 0.65f) * coreRatio;
                infallRatio = 0.65f + instab * 0.35f;
                gridPullAccel = (1350.0f + instab * 850.0f) * coreRatio;
                gridSwirlAccel = (380.0f + instab * 360.0f) * coreRatio;
                gridHeatRate = 0.26f + instab * 0.45f;
                return true;
            }

            case AetherSeedStage.DormantSeed:
                // Selama 10 detik pertama (Dormant Seed), biji energi tenang tanpa tarikan gravitasi berbahaya
                return false;

            case AetherSeedStage.CosmicDrift:
            {
                if (_seedTeleportState == SeedTeleportState.Folded) return false;

                if (_seedTeleportState == SeedTeleportState.Compressing)
                {
                    // Saat ruang terkompresi sangat gila, gravitasi sedot meningkat ekstrem
                    influenceRadius = 450.0f;
                    normalizedStrength = 1.95f;
                    infallRatio = 0.98f;
                    gridPullAccel = 2200.0f;
                    gridSwirlAccel = 580.0f;
                    gridHeatRate = 0.48f;
                    return true;
                }

                // Saat bermigrasi cepat, menimbulkan riak gravitasi gesit di sepanjang lintasan
                influenceRadius = 240.0f;
                normalizedStrength = 0.35f;
                infallRatio = 0.15f;
                gridPullAccel = 340.0f;
                gridSwirlAccel = 120.0f;
                gridHeatRate = 0.10f;
                return true;
            }

            case AetherSeedStage.SpacetimeInfall:
            {
                // Selama 30 detik Spacetime Infall, sedotan ruang-waktu meningkat agresif hingga terlahir kembali!
                float p = Mathf.Clamp(_seedStageTimer / SpacetimeInfallDuration, 0.0f, 1.0f);
                float smoothP = p * p * (3.0f - 2.0f * p);
                float pulse = 1.0f + 0.14f * MathF.Sin(_energyPulseTimer * (3.0f + p * 8.0f));

                influenceRadius = Mathf.Lerp(260.0f, Radius * 5.6f, smoothP);
                normalizedStrength = Mathf.Lerp(0.50f, 1.75f, smoothP) * pulse;
                infallRatio = Mathf.Lerp(0.28f, 1.0f, smoothP);
                gridPullAccel = Mathf.Lerp(540.0f, 1780.0f, smoothP) * pulse;
                gridSwirlAccel = Mathf.Lerp(190.0f, 560.0f, smoothP);
                gridHeatRate = Mathf.Lerp(0.18f, 0.72f, smoothP);
                return true;
            }

            default:
                return false;
        }
    }

    public override void _EnterTree()
    {
        ActiveAsteroids.Add(this);
    }

    public override void _ExitTree()
    {
        ActiveAsteroids.Remove(this);
    }

    public override void _Ready()
    {
        CollisionLayer = 2;
        CollisionMask = 2;
        GravityScale = 0.0f;
        // Hanyut pelan kosmik alami tanpa redaman gesekan ruang hampa
        LinearDampMode = DampMode.Replace;
        LinearDamp = 0.0f;
        AngularDampMode = DampMode.Replace;
        AngularDamp = 0.0f;
        PhysicsMaterialOverride = new PhysicsMaterial
        {
            Bounce = 0.95f,
            Friction = 0.05f
        };
        Mass = MathF.Max(150.0f, (Radius * Radius) * 0.12f);
        ContactMonitor = false;

        for (int t = 0; t < ResourceCount; t++)
        {
            _mergedResourceRects[t] = new List<Rect2>();
            _batchedExposedResourceEdges[t] = Array.Empty<Vector2>();
        }

        UpdateCachedTransform();
        ConfigureResourceCompositionAndColors();
        GenerateProceduralGeometry();
        InitializeVoxelGrid();
        GenerateResourceVoxelPatches();
        BuildPerimeterSegments();
        RebuildAllRowSpans();
        RebuildBatchedRenderGeometry();

        // Bentuk collision melingkupi seluruh batas terluar polygon (termasuk faset kristal)
        _circleShape = new CircleShape2D { Radius = OuterBoundRadius };
        _collisionShape = new CollisionShape2D { Shape = _circleShape };
        AddChild(_collisionShape);

        CurrentHealth = MaxHealth;

        if (IsBirthAnimationActive)
        {
            _birthGlow = 1.0f;
            _birthAnimTimer = 0.0f;
            _birthLongitudinalScale = 0.12f;
            _birthLateralScale = 0.06f;
            if (_collisionShape != null)
            {
                _collisionShape.SetDeferred(CollisionShape2D.PropertyName.Disabled, true);
            }
        }

        if (IsEnergyAsteroid)
        {
            SetupEnergyStarVisual();
            InitializeInfallPhotons();
        }
    }

    private void SetupEnergyStarVisual()
    {
        try
        {
            var shader = GD.Load<Shader>("res://visual/mini_star_singularity.gdshader");
            if (shader == null) return;

            // Kotak visual dibuat transparan murni tanpa BackBufferCopy agar grid & bintang di belakang tembus sempurna
            float boxSize = OuterBoundRadius * 4.4f;

            _energyStarMaterial = new ShaderMaterial { Shader = shader };
            _energyStarMaterial.SetShaderParameter("star_radius", 0.16f);
            _energyStarMaterial.SetShaderParameter("lensing_strength", 0.16f);
            _energyStarMaterial.SetShaderParameter("lensing_radius", 0.42f);
            _energyStarMaterial.SetShaderParameter("instability", 0.0f);

            _energyStarRect = new ColorRect
            {
                Material = _energyStarMaterial,
                CustomMinimumSize = new Vector2(boxSize, boxSize),
                Size = new Vector2(boxSize, boxSize),
                Position = new Vector2(-boxSize * 0.5f, -boxSize * 0.5f),
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            AddChild(_energyStarRect);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[Asteroid.SetupEnergyStarVisual] {ex}");
        }
    }

    private void InitializeInfallPhotons()
    {
        var rng = new RandomNumberGenerator();
        rng.Randomize();

        Color[] photonPalette = new Color[]
        {
            new(3.8f, 3.2f, 1.15f, 0.95f), // Aether White-Gold
            new(0.45f, 2.8f, 3.9f, 0.95f), // Electric Cyan
            new(2.2f, 1.4f, 3.6f, 0.90f)   // Cosmic Indigo-Violet
        };

        for (int i = 0; i < InfallPhotonCount; i++)
        {
            float maxD = rng.RandfRange(240.0f, 480.0f);
            _infallPhotons[i] = new InfallPhotonParticle
            {
                Angle = rng.Randf() * Mathf.Tau,
                Distance = rng.RandfRange(28.0f, maxD),
                MaxDistance = maxD,
                RadialSpeed = rng.RandfRange(95.0f, 210.0f),
                AngularSpeed = rng.RandfRange(1.2f, 2.8f) * (i % 2 == 0 ? 1.0f : -1.0f),
                Size = rng.RandfRange(2.0f, 3.6f),
                Color = photonPalette[i % photonPalette.Length]
            };
        }
    }

    private void UpdateCachedTransform()
    {
        CachedPosition = GlobalPosition;
        CachedRotation = GlobalRotation;
        CachedCos = MathF.Cos(CachedRotation);
        CachedSin = MathF.Sin(CachedRotation);
        CachedLinearVelocity = LinearVelocity;
    }

    private Vector2 FastWorldToLocal(Vector2 worldPt)
    {
        float dx = worldPt.X - CachedPosition.X;
        float dy = worldPt.Y - CachedPosition.Y;
        return new Vector2(dx * CachedCos + dy * CachedSin, -dx * CachedSin + dy * CachedCos);
    }

    private Vector2 FastLocalToWorld(Vector2 localPt)
    {
        return new Vector2(
            CachedPosition.X + (localPt.X * CachedCos - localPt.Y * CachedSin),
            CachedPosition.Y + (localPt.X * CachedSin + localPt.Y * CachedCos)
        );
    }

    private Vector2 FastRotateToLocal(Vector2 v)
    {
        return new Vector2(v.X * CachedCos + v.Y * CachedSin, -v.X * CachedSin + v.Y * CachedCos);
    }

    private Vector2 FastRotateToWorld(Vector2 v)
    {
        return new Vector2(v.X * CachedCos - v.Y * CachedSin, v.X * CachedSin + v.Y * CachedCos);
    }

    private void ConfigureResourceCompositionAndColors()
    {
        var rng = new RandomNumberGenerator();
        long seedVal = ((long)CachedPosition.X * 2654435761L) ^ ((long)CachedPosition.Y * 19349663L);
        rng.Seed = (ulong)Math.Abs(seedVal);
        _energyPulseTimer = rng.Randf() * Mathf.Tau;

        if (IsEnergyAsteroid)
        {
            PrimaryResource = ResourceType.Aether;
            SecondaryResource = ResourceType.Quartz;
            HasSecondaryResource = rng.Randf() < 0.45f;

            _unscannedBorderColor = new Color(4.5f, 3.8f, 1.4f, 0.98f);
            _tunnelWallBorderColor = new Color(1.2f, 3.4f, 4.6f, 0.95f);
            _rockBodyColor = new Color(0.025f, 0.05f, 0.10f, 0.95f);
        }
        else
        {
            PrimaryResource = AsteroidType switch
            {
                SwarmRole.DartNeedle => ResourceType.Ferrite,
                SwarmRole.Guardian => ResourceType.Quartz,
                SwarmRole.DrillDrone => ResourceType.Carbide,
                SwarmRole.GigaShuriken => ResourceType.Carbide,
                SwarmRole.CrescentBoomerang => ResourceType.Vortexum,
                _ => (ResourceType)rng.RandiRange(0, 3)
            };

            HasSecondaryResource = rng.Randf() < 0.65f;
            if (HasSecondaryResource)
            {
                int sec = (int)PrimaryResource;
                while (sec == (int)PrimaryResource)
                {
                    sec = rng.RandiRange(0, 3);
                }
                SecondaryResource = (ResourceType)sec;
            }
            else
            {
                SecondaryResource = PrimaryResource;
            }

            _unscannedBorderColor = new Color(0.22f, 0.24f, 0.34f, 0.88f);
            _tunnelWallBorderColor = new Color(0.28f, 0.26f, 0.38f, 0.82f);
            _rockBodyColor = new Color(0.032f, 0.028f, 0.045f, 0.98f);
        }
    }

    private void GenerateProceduralGeometry()
    {
        var rng = new RandomNumberGenerator();
        long seedVal = ((long)CachedPosition.X * 73856093L) ^ ((long)CachedPosition.Y * 19349663L);
        rng.Seed = (ulong)Math.Abs(seedVal);

        if (IsEnergyAsteroid)
        {
            int spireCount = rng.RandiRange(14, 16);
            _crystalSpires = new CrystalSpireData[spireCount];
            _crystalCoreRadius = Radius * rng.RandfRange(0.14f, 0.18f);

            HashSet<int> quartzSpireIndices = new();
            int quartzCount = rng.RandiRange(2, 3);
            while (quartzSpireIndices.Count < quartzCount)
            {
                quartzSpireIndices.Add(rng.RandiRange(0, spireCount - 1));
            }

            PolygonVertices = new Vector2[spireCount];
            _closedOutlinePoints = new Vector2[spireCount + 1];

            float maxVertDist = Radius;
            float stepAngle = Mathf.Tau / spireCount;

            for (int i = 0; i < spireCount; i++)
            {
                float angle = i * stepAngle + (rng.Randf() - 0.5f) * (stepAngle * 0.22f);
                bool isPrimary = (i % 2 == 0);

                float length = isPrimary
                    ? Radius * rng.RandfRange(1.05f, 1.25f)
                    : Radius * rng.RandfRange(0.68f, 0.85f);

                if (length > maxVertDist) maxVertDist = length;

                Vector2 dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                Vector2 perp = new Vector2(-dir.Y, dir.X);

                float rBase = _crystalCoreRadius * 0.95f;
                float rMid = rBase + (length - rBase) * 0.52f;

                float halfAngleSpan = (stepAngle * 0.5f) * 0.76f;
                float wBase = rBase * MathF.Tan(halfAngleSpan * 0.85f);
                float wMid = wBase * 1.35f;

                Vector2 baseCenter = dir * rBase;
                Vector2 baseLeft = baseCenter - perp * wBase;
                Vector2 baseRight = baseCenter + perp * wBase;

                Vector2 midLeft = (dir * rMid) - (perp * wMid);
                Vector2 midRight = (dir * rMid) + (perp * wMid);
                Vector2 ridgeCenter = dir * (rMid * 1.04f);
                Vector2 tip = dir * length;

                PolygonVertices[i] = tip;
                _closedOutlinePoints[i] = tip;

                bool hasQuartzVein = quartzSpireIndices.Contains(i);
                ResourceType res = hasQuartzVein ? ResourceType.Quartz : ResourceType.Ferrite;

                _crystalSpires[i] = new CrystalSpireData
                {
                    BaseCenter = baseCenter,
                    BaseLeft = baseLeft,
                    BaseRight = baseRight,
                    MidLeft = midLeft,
                    MidRight = midRight,
                    RidgeCenter = ridgeCenter,
                    Tip = tip,
                    Dir = dir,
                    Perp = perp,
                    Polygon = new Vector2[] { baseLeft, midLeft, tip, midRight, baseRight },
                    Resource = res,
                    HasResourceVein = hasQuartzVein,
                    Angle = (angle + Mathf.Tau) % Mathf.Tau,
                    Length = length,
                    RadiusMid = rMid,
                    HalfWidthBase = wBase,
                    HalfWidthMid = wMid,
                    Heat = 0.0f,
                    TotalVoxels = 0,
                    RemainingVoxels = 0
                };
            }

            _closedOutlinePoints[spireCount] = _closedOutlinePoints[0];
            OuterBoundRadius = maxVertDist + VoxelSize * 1.5f;
        }
        else
        {
            int sides = (int)Mathf.Clamp(MathF.Round(Radius * 0.038f), 16, 28);
            PolygonVertices = new Vector2[sides];
            _closedOutlinePoints = new Vector2[sides + 1];

            float maxVertDist = Radius;
            for (int i = 0; i < sides; i++)
            {
                float baseAngle = (float)i / sides * Mathf.Tau;
                float angleJitter = (rng.Randf() - 0.5f) * (Mathf.Tau / sides * 0.35f);
                float angle = baseAngle + angleJitter;

                float rFactor = rng.RandfRange(0.85f, 1.15f);
                float r = Radius * rFactor;
                if (r > maxVertDist) maxVertDist = r;

                PolygonVertices[i] = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * r;
                _closedOutlinePoints[i] = PolygonVertices[i];
            }
            _closedOutlinePoints[sides] = _closedOutlinePoints[0];
            OuterBoundRadius = maxVertDist + VoxelSize * 1.5f;
        }
    }

    private void InitializeVoxelGrid()
    {
        int halfCells = (int)MathF.Ceiling(OuterBoundRadius * InvVoxelSize) + 1;
        GridSide = halfCells * 2;
        GridOriginLocal = new Vector2(-halfCells * VoxelSize, -halfCells * VoxelSize);

        int totalCells = GridSide * GridSide;
        _voxels = new VoxelType[totalCells];
        _initialSolidMask = new bool[totalCells];
        _voxelHeat = new float[totalCells];
        _voxelKineticDamage = new float[totalCells];

        _rockSpansByRow = new List<Rect2>[GridSide];
        _resourceSpansByTypeAndRow = new List<Rect2>[ResourceCount][];

        for (int t = 0; t < ResourceCount; t++)
        {
            _resourceSpansByTypeAndRow[t] = new List<Rect2>[GridSide];
            for (int gy = 0; gy < GridSide; gy++)
            {
                _resourceSpansByTypeAndRow[t][gy] = new List<Rect2>(2);
            }
        }

        for (int gy = 0; gy < GridSide; gy++)
        {
            _rockSpansByRow[gy] = new List<Rect2>(4);
        }

        _initialSolidVoxelCount = 0;
        for (int gy = 0; gy < GridSide; gy++)
        {
            float cy = GridOriginLocal.Y + (gy + 0.5f) * VoxelSize;
            int rowOffset = gy * GridSide;

            for (int gx = 0; gx < GridSide; gx++)
            {
                float cx = GridOriginLocal.X + (gx + 0.5f) * VoxelSize;
                Vector2 pt = new Vector2(cx, cy);

                if (pt.LengthSquared() <= OuterBoundRadius * OuterBoundRadius && IsPointInPolygon(pt, PolygonVertices))
                {
                    _initialSolidMask[rowOffset + gx] = true;
                    _voxels[rowOffset + gx] = VoxelType.Rock;
                    _initialSolidVoxelCount++;
                }
            }
        }

        _remainingSolidVoxelCount = _initialSolidVoxelCount;
        _carvedVoxelCount = 0;
    }

    /// <summary>
    /// Membangkitkan kantung-kantung resource dengan jumlah ACAK dan 1-2 jenis resource berbeda,
    /// serta menghitung total voxel resource agar asteroid batu otomatis hancur saat 100% mineral habis dipanen.
    /// </summary>
    private void GenerateResourceVoxelPatches()
    {
        var rng = new RandomNumberGenerator();
        long seedVal = ((long)CachedPosition.X * 19349663L) ^ ((long)CachedPosition.Y * 73856093L);
        rng.Seed = (ulong)Math.Abs(seedVal);

        int centerCell = GridSide / 2;

        if (IsEnergyAsteroid)
        {
            int totalSolid = 0;
            int totalRes = 0;
            _totalCoreVoxels = 0;
            _remainingCoreVoxels = 0;
            float coreAetherRadius = _crystalCoreRadius * 0.82f;
            float coreAetherRadiusSq = coreAetherRadius * coreAetherRadius;
            float coreShellRadiusSq = _crystalCoreRadius * _crystalCoreRadius;

            for (int gy = 0; gy < GridSide; gy++)
            {
                int rowOffset = gy * GridSide;
                for (int gx = 0; gx < GridSide; gx++)
                {
                    int idx = rowOffset + gx;
                    Vector2 cellPos = GridToLocalCenter(gx, gy);
                    float distSq = cellPos.LengthSquared();

                    if (distSq <= coreShellRadiusSq)
                    {
                        _initialSolidMask[idx] = true;
                        totalSolid++;

                        if (distSq <= coreAetherRadiusSq)
                        {
                            _voxels[idx] = VoxelType.ResourceAether;
                            _totalCoreVoxels++;
                            _remainingCoreVoxels++;
                            totalRes++;
                        }
                        else
                        {
                            _voxels[idx] = VoxelType.Rock;
                        }
                        continue;
                    }

                    bool inSpire = false;

                    for (int s = 0; s < _crystalSpires.Length; s++)
                    {
                        ref var spire = ref _crystalSpires[s];
                        if (IsPointInPolygon(cellPos, spire.Polygon))
                        {
                            inSpire = true;
                            _initialSolidMask[idx] = true;
                            spire.TotalVoxels++;
                            spire.RemainingVoxels++;
                            totalSolid++;

                            if (spire.HasResourceVein)
                            {
                                float dist = MathF.Sqrt(distSq);
                                if (dist >= spire.RadiusMid * 0.80f && dist <= spire.RadiusMid * 1.20f)
                                {
                                    _voxels[idx] = VoxelType.ResourceQuartz;
                                    totalRes++;
                                }
                                else
                                {
                                    _voxels[idx] = VoxelType.Rock;
                                }
                            }
                            else
                            {
                                _voxels[idx] = VoxelType.Rock;
                            }
                            break;
                        }
                    }

                    if (!inSpire)
                    {
                        _voxels[idx] = VoxelType.Empty;
                        _initialSolidMask[idx] = false;
                    }
                }
            }

            _initialSolidVoxelCount = totalSolid;
            _remainingSolidVoxelCount = totalSolid;
            _initialResourceVoxelCount = totalRes;
            _remainingResourceVoxelCount = totalRes;
            return;
        }

        // Asteroid Batu Biasa: Jumlah deposit ACAK (antara 2 hingga 9 kantung dengan ukuran acak)
        // dan berisi 1 atau 2 jenis resource berbeda!
        VoxelType primaryVox = ResourceToVoxelType(PrimaryResource);
        VoxelType secondaryVox = ResourceToVoxelType(SecondaryResource);

        bool hasMotherlodeCore = rng.Randf() < 0.35f;
        if (hasMotherlodeCore)
        {
            float coreRadius = Radius * rng.RandfRange(0.20f, 0.34f);
            PlaceResourceCluster(new Vector2I(centerCell, centerCell), (int)MathF.Ceiling(coreRadius * InvVoxelSize), primaryVox, rng);
        }

        int randomPocketCount = rng.RandiRange(2, 9);
        for (int p = 0; p < randomPocketCount; p++)
        {
            float pAngle = rng.Randf() * Mathf.Tau;
            float pDist = rng.RandfRange(Radius * 0.14f, Radius * 0.68f);
            Vector2 pocketCenter = new Vector2(MathF.Cos(pAngle), MathF.Sin(pAngle)) * pDist;
            Vector2I pocketGrid = LocalToGrid(pocketCenter);

            int clusterRadius = rng.RandiRange(2, 5);
            VoxelType chosenVox = (HasSecondaryResource && (p % 2 == 1)) ? secondaryVox : primaryVox;
            PlaceResourceCluster(pocketGrid, clusterRadius, chosenVox, rng);
        }

        // Hitung total voxel resource yang berhasil ditanam di dalam asteroid batu
        int resourceVoxelTotal = 0;
        for (int i = 0; i < _voxels.Length; i++)
        {
            if (TryGetResourceType(_voxels[i], out _))
            {
                resourceVoxelTotal++;
            }
        }

        // Jaminan pengaman: jika karena acakan belum ada voxel resource yang tertanam, tanam 1 kantung inti di tengah
        if (resourceVoxelTotal == 0)
        {
            PlaceResourceCluster(new Vector2I(centerCell, centerCell), 3, primaryVox, rng);
            for (int i = 0; i < _voxels.Length; i++)
            {
                if (TryGetResourceType(_voxels[i], out _))
                {
                    resourceVoxelTotal++;
                }
            }
        }

        _initialResourceVoxelCount = resourceVoxelTotal;
        _remainingResourceVoxelCount = resourceVoxelTotal;
    }

    private void PlaceResourceCluster(Vector2I centerGrid, int clusterRadius, VoxelType targetVoxelType, RandomNumberGenerator rng)
    {
        for (int dy = -clusterRadius; dy <= clusterRadius; dy++)
        {
            for (int dx = -clusterRadius; dx <= clusterRadius; dx++)
            {
                if (dx * dx + dy * dy > clusterRadius * clusterRadius + 1) continue;
                if (rng.Randf() < 0.22f) continue;

                int gx = centerGrid.X + dx;
                int gy = centerGrid.Y + dy;
                if (!IsValidGridCoord(gx, gy)) continue;

                Vector2 cellCenter = GridToLocalCenter(gx, gy);
                if (cellCenter.Length() < Radius * 0.80f && _voxels[gy * GridSide + gx] == VoxelType.Rock)
                {
                    _voxels[gy * GridSide + gx] = targetVoxelType;
                }
            }
        }
    }

    private void BuildPerimeterSegments()
    {
        _perimeterSegments.Clear();
        int n = PolygonVertices.Length;
        if (n < 3) return;

        for (int i = 0; i < n; i++)
        {
            Vector2 a = PolygonVertices[i];
            Vector2 b = PolygonVertices[(i + 1) % n];
            float len = a.DistanceTo(b);
            int steps = Math.Max(1, (int)MathF.Ceiling(len / (VoxelSize * 1.5f)));

            for (int s = 0; s < steps; s++)
            {
                float t0 = (float)s / steps;
                float t1 = (float)(s + 1) / steps;
                Vector2 segA = a.Lerp(b, t0);
                Vector2 segB = a.Lerp(b, t1);
                Vector2 mid = (segA + segB) * 0.5f;

                Vector2 inwardDir = mid.LengthSquared() > 1.0f ? -mid.Normalized() : Vector2.Zero;
                Vector2I chosenGrid = LocalToGrid(mid + inwardDir * (VoxelSize * 0.65f));

                for (int probe = 1; probe <= 6; probe++)
                {
                    Vector2 probePt = mid + inwardDir * (VoxelSize * (0.35f * probe));
                    Vector2I cand = LocalToGrid(probePt);
                    if (IsValidGridCoord(cand.X, cand.Y) && _initialSolidMask[cand.Y * GridSide + cand.X])
                    {
                        chosenGrid = cand;
                        break;
                    }
                }

                _perimeterSegments.Add(new OutlineSegment
                {
                    A = segA,
                    B = segB,
                    CheckGridX = Math.Clamp(chosenGrid.X, 0, GridSide - 1),
                    CheckGridY = Math.Clamp(chosenGrid.Y, 0, GridSide - 1)
                });
            }
        }
    }

    private void RebuildAllRowSpans()
    {
        for (int gy = 0; gy < GridSide; gy++)
        {
            RebuildRowSpans(gy);
        }
        _geometryBatchDirty = true;
    }

    private void RebuildRowSpans(int gy)
    {
        if (gy < 0 || gy >= GridSide) return;

        var rockSpans = _rockSpansByRow[gy];
        rockSpans.Clear();

        for (int t = 0; t < ResourceCount; t++)
        {
            _resourceSpansByTypeAndRow[t][gy].Clear();
        }

        int rowOffset = gy * GridSide;
        float rowY = GridOriginLocal.Y + gy * VoxelSize;

        int rockStart = -1;
        int curResTypeIdx = -1;
        int resStart = -1;

        for (int gx = 0; gx < GridSide; gx++)
        {
            VoxelType vox = _voxels[rowOffset + gx];

            if (vox != VoxelType.Empty)
            {
                if (rockStart == -1) rockStart = gx;
            }
            else if (rockStart != -1)
            {
                rockSpans.Add(new Rect2(
                    GridOriginLocal.X + rockStart * VoxelSize,
                    rowY,
                    (gx - rockStart) * VoxelSize,
                    VoxelSize
                ));
                rockStart = -1;
            }

            int thisResIdx = TryGetResourceType(vox, out ResourceType rType) ? (int)rType : -1;
            if (thisResIdx == curResTypeIdx)
            {
                // Lanjutkan span resource yang sama
            }
            else
            {
                if (curResTypeIdx != -1 && resStart != -1)
                {
                    _resourceSpansByTypeAndRow[curResTypeIdx][gy].Add(new Rect2(
                        GridOriginLocal.X + resStart * VoxelSize + 1.0f,
                        rowY + 1.0f,
                        (gx - resStart) * VoxelSize - 2.0f,
                        VoxelSize - 2.0f
                    ));
                }
                curResTypeIdx = thisResIdx;
                resStart = (thisResIdx != -1) ? gx : -1;
            }
        }

        if (rockStart != -1)
        {
            rockSpans.Add(new Rect2(
                GridOriginLocal.X + rockStart * VoxelSize,
                rowY,
                (GridSide - rockStart) * VoxelSize,
                VoxelSize
            ));
        }

        if (curResTypeIdx != -1 && resStart != -1)
        {
            _resourceSpansByTypeAndRow[curResTypeIdx][gy].Add(new Rect2(
                GridOriginLocal.X + resStart * VoxelSize + 1.0f,
                rowY + 1.0f,
                (GridSide - resStart) * VoxelSize - 2.0f,
                VoxelSize - 2.0f
            ));
        }

        _geometryBatchDirty = true;
    }

    private void RebuildBatchedRenderGeometry()
    {
        _geometryBatchDirty = false;
        MergeVerticalSpans(_rockSpansByRow, _mergedRockRects);

        for (int t = 0; t < ResourceCount; t++)
        {
            MergeVerticalSpans(_resourceSpansByTypeAndRow[t], _mergedResourceRects[t]);
        }

        if (_carvedVoxelCount > 0)
        {
            List<Vector2> wallPts = new(128);
            List<Vector2>[] exposedResPts = new List<Vector2>[ResourceCount];
            for (int t = 0; t < ResourceCount; t++)
            {
                exposedResPts[t] = new List<Vector2>(32);
            }

            bool IsCarvedEmpty(int nGx, int nGy)
            {
                if (nGx < 0 || nGx >= GridSide || nGy < 0 || nGy >= GridSide) return false;
                int nIdx = nGy * GridSide + nGx;
                if (nIdx < 0 || nIdx >= _voxels.Length || _initialSolidMask == null || nIdx >= _initialSolidMask.Length) return false;
                return _voxels[nIdx] == VoxelType.Empty && _initialSolidMask[nIdx];
            }

            for (int gy = 0; gy < GridSide; gy++)
            {
                int rowOffset = gy * GridSide;
                float y0 = GridOriginLocal.Y + gy * VoxelSize;
                float y1 = y0 + VoxelSize;

                for (int gx = 0; gx < GridSide; gx++)
                {
                    VoxelType vox = _voxels[rowOffset + gx];
                    if (vox == VoxelType.Empty) continue;

                    float x0 = GridOriginLocal.X + gx * VoxelSize;
                    float x1 = x0 + VoxelSize;

                    bool isRes = TryGetResourceType(vox, out ResourceType rType);
                    List<Vector2> targetList = isRes ? exposedResPts[(int)rType] : wallPts;

                    if (gx > 0 && IsCarvedEmpty(gx - 1, gy))
                    {
                        targetList.Add(new Vector2(x0, y0));
                        targetList.Add(new Vector2(x0, y1));
                    }
                    if (gx < GridSide - 1 && IsCarvedEmpty(gx + 1, gy))
                    {
                        targetList.Add(new Vector2(x1, y0));
                        targetList.Add(new Vector2(x1, y1));
                    }
                    if (gy > 0 && IsCarvedEmpty(gx, gy - 1))
                    {
                        targetList.Add(new Vector2(x0, y0));
                        targetList.Add(new Vector2(x1, y0));
                    }
                    if (gy < GridSide - 1 && IsCarvedEmpty(gx, gy + 1))
                    {
                        targetList.Add(new Vector2(x0, y1));
                        targetList.Add(new Vector2(x1, y1));
                    }

                    if (isRes)
                    {
                        bool touchesEmpty =
                            (gx > 0 && IsCarvedEmpty(gx - 1, gy)) ||
                            (gx < GridSide - 1 && IsCarvedEmpty(gx + 1, gy)) ||
                            (gy > 0 && IsCarvedEmpty(gx, gy - 1)) ||
                            (gy < GridSide - 1 && IsCarvedEmpty(gx, gy + 1));

                        if (touchesEmpty)
                        {
                            if (gx > 0 && _voxels[rowOffset + gx - 1] == VoxelType.Rock)
                            {
                                targetList.Add(new Vector2(x0, y0));
                                targetList.Add(new Vector2(x0, y1));
                            }
                            if (gx < GridSide - 1 && _voxels[rowOffset + gx + 1] == VoxelType.Rock)
                            {
                                targetList.Add(new Vector2(x1, y0));
                                targetList.Add(new Vector2(x1, y1));
                            }
                            if (gy > 0 && _voxels[(gy - 1) * GridSide + gx] == VoxelType.Rock)
                            {
                                targetList.Add(new Vector2(x0, y0));
                                targetList.Add(new Vector2(x1, y0));
                            }
                            if (gy < GridSide - 1 && _voxels[(gy + 1) * GridSide + gx] == VoxelType.Rock)
                            {
                                targetList.Add(new Vector2(x0, y1));
                                targetList.Add(new Vector2(x1, y1));
                            }
                        }
                    }
                }
            }

            _batchedWallLines = wallPts.ToArray();
            for (int t = 0; t < ResourceCount; t++)
            {
                _batchedExposedResourceEdges[t] = exposedResPts[t].ToArray();
            }

            List<Vector2> perimPts = new(_perimeterSegments.Count * 2);
            for (int i = 0; i < _perimeterSegments.Count; i++)
            {
                var seg = _perimeterSegments[i];
                int cgx = seg.CheckGridX;
                int cgy = seg.CheckGridY;

                bool edgeBroken = IsCarvedEmpty(cgx, cgy)
                    || IsCarvedEmpty(cgx - 1, cgy)
                    || IsCarvedEmpty(cgx + 1, cgy)
                    || IsCarvedEmpty(cgx, cgy - 1)
                    || IsCarvedEmpty(cgx, cgy + 1);

                if (!edgeBroken && _voxels[cgy * GridSide + cgx] != VoxelType.Empty)
                {
                    perimPts.Add(seg.A);
                    perimPts.Add(seg.B);
                }
            }
            _batchedPerimeterLines = perimPts.ToArray();
        }
    }

    private static void MergeVerticalSpans(List<Rect2>[] rows, List<Rect2> output)
    {
        output.Clear();
        for (int gy = 0; gy < rows.Length; gy++)
        {
            var curRow = rows[gy];
            for (int i = 0; i < curRow.Count; i++)
            {
                Rect2 r = curRow[i];
                bool merged = false;

                for (int j = output.Count - 1; j >= 0; j--)
                {
                    Rect2 prev = output[j];
                    if (prev.End.Y + 2.5f < r.Position.Y) break;

                    if (MathF.Abs(prev.Position.X - r.Position.X) < 0.1f
                        && MathF.Abs(prev.Size.X - r.Size.X) < 0.1f
                        && MathF.Abs(prev.End.Y - r.Position.Y) <= 2.1f)
                    {
                        output[j] = new Rect2(prev.Position, new Vector2(prev.Size.X, r.End.Y - prev.Position.Y));
                        merged = true;
                        break;
                    }
                }

                if (!merged)
                {
                    output.Add(r);
                }
            }
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        try
        {
            UpdateCachedTransform();

            float dt = (float)delta;
            bool needsRedraw = false;

            // Animasi Kelahiran Asteroid dari White Hole: EFEK JIN ALADDIN / MAC OS GENIE EFFECT
            if (IsBirthAnimationActive)
            {
                _birthAnimTimer += dt;
                float p = Mathf.Clamp(_birthAnimTimer / BirthAnimDuration, 0.0f, 1.0f);

                // Genie Effect Non-Uniform Taffy Stretch:
                // Sambil ditarik keluar sepanjang sumbu lontaran:
                // 1. Sumbu Membujur (Longitudinal): ditarik memanjang ke depan seperti asap lampu jin (hingga 1.55x)
                // 2. Sumbu Melintang (Lateral): diperas sangat tipis di leher lubang (0.06x -> 0.48x),
                //    kemudian menggelembung mekar seperti balon saat keluar dari White Hole (hingga 1.24x),
                //    lalu tali plasma putus pada p=0.75 dan asteroid membal (elastic harmonic wobble) ke bentuk stabil.
                if (p < 0.45f)
                {
                    float t1 = p / 0.45f;
                    _birthLongitudinalScale = Mathf.Lerp(0.12f, 1.55f, MathF.Sqrt(t1));
                    _birthLateralScale = Mathf.Lerp(0.06f, 0.48f, t1 * t1);
                }
                else if (p < 0.75f)
                {
                    float t2 = (p - 0.45f) / 0.30f;
                    _birthLongitudinalScale = Mathf.Lerp(1.55f, 1.05f, t2);
                    _birthLateralScale = Mathf.Lerp(0.48f, 1.24f, MathF.Sqrt(t2));
                }
                else
                {
                    // Tali pusar putus: getaran elastis damped harmonic settling
                    float t3 = (p - 0.75f) / 0.25f;
                    float wobble = MathF.Sin(t3 * Mathf.Tau * 1.5f) * MathF.Exp(-t3 * 3.2f) * 0.16f;
                    _birthLongitudinalScale = Mathf.Lerp(1.05f, 1.0f, t3) - wobble;
                    _birthLateralScale = Mathf.Lerp(1.24f, 1.0f, t3) + wobble;
                }

                _birthGlow = 1.0f - p;

                if (_birthAnimTimer >= BirthAnimDuration)
                {
                    IsBirthAnimationActive = false;
                    _birthGlow = 0.0f;
                    _birthLongitudinalScale = 1.0f;
                    _birthLateralScale = 1.0f;
                    if (_collisionShape != null)
                    {
                        _collisionShape.SetDeferred(CollisionShape2D.PropertyName.Disabled, false);
                    }
                }
                needsRedraw = true;
            }

            // =========================================================================
            // SIKLUS REINKARNASI BINTANG AETHER (DORMANT SEED -> COSMIC DRIFT -> SPACETIME INFALL)
            // =========================================================================
            if (IsInSeedCycle)
            {
                UpdateAetherSeedLifecycle(dt);
                return;
            }

            // Animasi Retakan Katastropik & Getaran Tremor saat Asteroid Sedang Hancur
            if (_isDestroying)
            {
                _destructionTimer -= dt;
                float progress = 1.0f - Mathf.Clamp(_destructionTimer / MathF.Max(0.01f, _currentDestructionDuration), 0.0f, 1.0f);
                float shakeMag = Mathf.Lerp(3.5f, 18.0f, progress * progress);
                CachedPosition += new Vector2((GD.Randf() - 0.5f) * shakeMag, (GD.Randf() - 0.5f) * shakeMag);
                QueueRedraw();

                if (_destructionTimer <= 0.0f)
                {
                    FinalizeDestruction();
                    return;
                }
            }

            // Jaga kecepatan hanyut tetap pelan & stabil khas objek kosmik (antara 4.0 - 14.0 px/s)
            float currentSpeed = LinearVelocity.Length();
            if (currentSpeed > 14.0f)
            {
                LinearVelocity = LinearVelocity.Normalized() * Mathf.Lerp(currentSpeed, 12.0f, dt * 2.0f);
            }
            else if (currentSpeed < 3.5f && currentSpeed > 0.01f)
            {
                LinearVelocity = LinearVelocity.Normalized() * Mathf.Lerp(currentSpeed, 5.0f, dt * 2.0f);
            }

            // Perlindungan Batas Sektor (12.000 x 12.000 px) & Area Kapal Induk Pemain (Pusat 0,0)
            float maxBoundary = 5700.0f - OuterBoundRadius;
            Vector2 pos = GlobalPosition;
            if (MathF.Abs(pos.X) > maxBoundary || MathF.Abs(pos.Y) > maxBoundary)
            {
                Vector2 toCenter = (-pos).Normalized();
                LinearVelocity = LinearVelocity.Lerp(toCenter * 8.0f, dt * 1.5f);
            }
            float distToPlayerSpawn = pos.Length();
            if (distToPlayerSpawn < 950.0f)
            {
                Vector2 awayFromCenter = pos.LengthSquared() > 1.0f ? pos.Normalized() : Vector2.Right;
                LinearVelocity = LinearVelocity.Lerp(awayFromCenter * 10.0f, dt * 2.0f);
            }

            // Asteroid Energy (ActiveStar): Update Visual Shader Bintang Mini & Gravitational Lensing
            if (IsEnergyAsteroid)
            {
                _energyPulseTimer += dt * 3.2f;

                if (_energyStarMaterial != null)
                {
                    float remainingRatio = _totalCoreVoxels > 0
                        ? (float)_remainingCoreVoxels / _totalCoreVoxels
                        : (float)_remainingSolidVoxelCount / Math.Max(1, _initialSolidVoxelCount);

                    float normalizedRadius = Mathf.Lerp(0.04f, 0.16f, remainingRatio);
                    _energyStarMaterial.SetShaderParameter("star_radius", normalizedRadius);
                    _energyStarMaterial.SetShaderParameter("lensing_strength", 0.12f);

                    if (_energyWasHitThisFrame)
                    {
                        // Dikalibrasi tepat 15 detik tembakan laser terus menerus (1.0f / 15.0f per detik)
                        _energyAccumulatedHeat = Mathf.Min(1.0f, _energyAccumulatedHeat + (dt / 15.0f));

                        if (_energyAccumulatedHeat >= 1.0f && !_isDestroying)
                        {
                            StartDestructionSequence();
                        }
                    }
                    else
                    {
                        // Pendinginan bertahap saat tembakan terputus (memberi toleransi saat pemain bermanuver)
                        _energyAccumulatedHeat = Mathf.MoveToward(_energyAccumulatedHeat, 0.0f, dt * 0.035f);
                        _energyLaserCavitation = Mathf.MoveToward(_energyLaserCavitation, 0.0f, dt * 2.8f);
                    }
                    _energyWasHitThisFrame = false;

                    float currentInstability = _isDestroying
                        ? 1.0f
                        : Mathf.Clamp(_energyAccumulatedHeat, 0.0f, 1.0f);

                    _energyStarMaterial.SetShaderParameter("instability", currentInstability);
                    _energyStarMaterial.SetShaderParameter("hit_angle", _energyLaserHitAngle);
                    _energyStarMaterial.SetShaderParameter("hit_cavitation", _energyLaserCavitation);

                    // MEDAN GRAVITASI MAUT (Gravitational Hazard & Event Horizon Pull)
                    float gravityRadius = Radius * 4.5f;
                    float eventHorizonRadius = Radius * 0.42f;

                    if (PlayerCore.Instance != null && IsInstanceValid(PlayerCore.Instance))
                    {
                        float playerDist = CachedPosition.DistanceTo(PlayerCore.Instance.GlobalPosition);
                        if (playerDist < gravityRadius)
                        {
                            Vector2 pullDir = (CachedPosition - PlayerCore.Instance.GlobalPosition).Normalized();
                            float pullRatio = 1.0f - (playerDist / gravityRadius);
                            float pullForce = Mathf.Lerp(60.0f, 520.0f, pullRatio * pullRatio);
                            PlayerCore.Instance.Velocity += pullDir * (pullForce * dt);

                            if (playerDist < eventHorizonRadius)
                            {
                                PlayerCore.Instance.TakeDamage(99999.0f, -pullDir);
                            }
                        }
                    }

                    for (int a = 0; a < ActiveAsteroids.Count; a++)
                    {
                        var other = ActiveAsteroids[a];
                        if (other == this || other._isDestroyed || other.IsInSeedCycle || !IsInstanceValid(other)) continue;
                        float aDist = CachedPosition.DistanceTo(other.CachedPosition);
                        if (aDist < gravityRadius + other.OuterBoundRadius)
                        {
                            Vector2 toStar = (CachedPosition - other.CachedPosition).Normalized();
                            float f = Mathf.Lerp(40.0f, 320.0f, 1.0f - (aDist / (gravityRadius + other.OuterBoundRadius)));
                            other.LinearVelocity += toStar * (f * dt);

                            if (aDist < eventHorizonRadius + other.OuterBoundRadius * 0.35f)
                            {
                                other.DestroyAsteroid();
                                _energyAccumulatedHeat = Mathf.Min(0.95f, _energyAccumulatedHeat + 0.15f);
                            }
                        }
                    }
                }

                if (_crystalSpires.Length > 0)
                {
                    for (int i = 0; i < _crystalSpires.Length; i++)
                    {
                        if (_crystalSpires[i].Heat > 0.0f)
                        {
                            _crystalSpires[i].Heat = MathF.Max(0.0f, _crystalSpires[i].Heat - dt * 2.5f);
                        }
                    }
                }

                Vector2 playerPos = PlayerCore.Instance?.GlobalPosition ?? Vector2.Zero;
                float distSq = GlobalPosition.DistanceSquaredTo(playerPos);
                float cullDist = OuterBoundRadius + 2200.0f;

                if (distSq <= cullDist * cullDist)
                {
                    _energyRedrawTimer += dt;
                    if (_energyRedrawTimer >= 0.033f)
                    {
                        _energyRedrawTimer = 0.0f;
                        needsRedraw = true;
                    }
                }
            }

            if (_lastDrawnScannerCount != CurrentScannerCount)
            {
                _lastDrawnScannerCount = CurrentScannerCount;
                needsRedraw = true;
            }

            if (_activeHeatedIndices.Count > 0)
            {
                for (int i = _activeHeatedIndices.Count - 1; i >= 0; i--)
                {
                    int idx = _activeHeatedIndices[i];
                    if (_heatedThisFrame.Contains(idx)) continue;

                    _voxelHeat[idx] -= dt * 2.2f;
                    needsRedraw = true;

                    if (_voxelHeat[idx] <= 0.0f)
                    {
                        _voxelHeat[idx] = 0.0f;
                        _activeHeatedIndices.RemoveAt(i);
                    }
                }
                _heatedThisFrame.Clear();
            }

            if (needsRedraw)
            {
                QueueRedraw();
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[Asteroid._PhysicsProcess] {ex}");
        }
    }

    // =========================================================================
    // LOGIKA 3 TAHAP SIKLUS REINKARNASI BINTANG AETHER
    // =========================================================================

    private void EnterDormantSeedStage()
    {
        SeedStage = AetherSeedStage.DormantSeed;
        _seedStageTimer = 0.0f;
        _isDestroying = false;
        _isDestroyed = false;

        // Kosongkan voxel & matikan kolisi selama fase Seed agar kapal & drone bebas memanen Aether
        _remainingSolidVoxelCount = 0;
        _remainingCoreVoxels = 0;
        _remainingResourceVoxelCount = 0;
        _activeHeatedIndices.Clear();
        _heatedThisFrame.Clear();
        _seedDriftTrailWorld.Clear();

        LinearVelocity = Vector2.Zero;
        AngularVelocity = 0.0f;
        GlobalRotation = 0.0f;
        UpdateCachedTransform();

        if (_collisionShape != null)
        {
            _collisionShape.SetDeferred(CollisionShape2D.PropertyName.Disabled, true);
        }

        if (_energyStarMaterial != null)
        {
            // Biji energi kecil yang berpendar lembut di titik nol ledakan
            _energyStarMaterial.SetShaderParameter("star_radius", 0.026f);
            _energyStarMaterial.SetShaderParameter("lensing_strength", 0.018f);
            _energyStarMaterial.SetShaderParameter("instability", 0.0f);
            _energyStarMaterial.SetShaderParameter("hit_cavitation", 0.0f);
        }

        QueueRedraw();
    }

    private void EnterCosmicDriftStage()
    {
        SeedStage = AetherSeedStage.CosmicDrift;
        _seedStageTimer = 0.0f;
        _seedTrailSampleTimer = 0.0f;
        _seedDriftStartPos = GlobalPosition;
        _seedDriftTargetPos = PickSafeRebirthCoordinate(_seedDriftStartPos);

        Vector2 travelVec = _seedDriftTargetPos - _seedDriftStartPos;
        Vector2 travelDir = travelVec.LengthSquared() > 1.0f ? travelVec.Normalized() : Vector2.Right;
        _seedDriftPerpDir = new Vector2(-travelDir.Y, travelDir.X);
        _seedDriftArcAmplitude = (GD.Randf() > 0.5f ? 1.0f : -1.0f) * (float)GD.RandRange(140.0, 260.0);

        _seedDriftTrailWorld.Clear();
        _seedDriftTrailWorld.Add(_seedDriftStartPos);

        _seedTeleportState = SeedTeleportState.None;
        _seedTeleportStateTimer = 0.0f;
        _seedTeleportNextCooldown = (float)GD.RandRange(1.8, 2.5);

        if (_energyStarRect != null) _energyStarRect.Visible = true;

        if (_energyStarMaterial != null)
        {
            _energyStarMaterial.SetShaderParameter("star_radius", 0.032f);
            _energyStarMaterial.SetShaderParameter("lensing_strength", 0.028f);
            _energyStarMaterial.SetShaderParameter("instability", 0.05f);
        }

        QueueRedraw();
    }

    private void EnterSpacetimeInfallStage()
    {
        SeedStage = AetherSeedStage.SpacetimeInfall;
        _seedStageTimer = 0.0f;
        _seedTeleportState = SeedTeleportState.None;
        if (_energyStarRect != null) _energyStarRect.Visible = true;
        GlobalPosition = _seedDriftTargetPos;
        LinearVelocity = Vector2.Zero;
        AngularVelocity = 0.0f;
        UpdateCachedTransform();
        _seedDriftTrailWorld.Clear();
        InitializeInfallPhotons();
        QueueRedraw();
    }

    private Vector2 PickSafeRebirthCoordinate(Vector2 originPos)
    {
        var rng = new RandomNumberGenerator();
        rng.Randomize();

        Vector2 bestCandidate = originPos + new Vector2(1800.0f, 0.0f);
        float bestClearance = -1.0f;

        for (int attempt = 0; attempt < 64; attempt++)
        {
            // Pilih jarak migrasi 1.400 - 3.100 px di dalam sabuk asteroid (jarak pusat 1.450 - 5.100 px)
            float moveAngle = rng.Randf() * Mathf.Tau;
            float moveDist = rng.RandfRange(1450.0f, 3050.0f);
            Vector2 cand = originPos + new Vector2(MathF.Cos(moveAngle), MathF.Sin(moveAngle)) * moveDist;

            float radialDist = cand.Length();
            if (radialDist < 1450.0f || radialDist > 5100.0f)
            {
                float clampedR = Mathf.Clamp(radialDist, 1650.0f, 4800.0f);
                float beltAngle = cand.LengthSquared() > 1.0f ? cand.Angle() : rng.Randf() * Mathf.Tau;
                cand = new Vector2(MathF.Cos(beltAngle), MathF.Sin(beltAngle)) * clampedR;
            }

            // Jaga jarak dari 3 Cosmic Rift di R ~ 1550 px
            if (MathF.Abs(cand.Length() - AsteroidFieldManager.RiftOrbitRadius) < 340.0f)
            {
                cand = cand.Normalized() * (AsteroidFieldManager.RiftOrbitRadius + 480.0f);
            }

            float minDistToOthers = float.MaxValue;
            for (int i = 0; i < ActiveAsteroids.Count; i++)
            {
                var other = ActiveAsteroids[i];
                if (other == this || other._isDestroyed || !IsInstanceValid(other)) continue;
                float d = cand.DistanceTo(other.CachedPosition) - (other.OuterBoundRadius + OuterBoundRadius + 240.0f);
                if (d < minDistToOthers) minDistToOthers = d;
            }

            if (minDistToOthers > bestClearance)
            {
                bestClearance = minDistToOthers;
                bestCandidate = cand;
                if (minDistToOthers >= 120.0f)
                {
                    break;
                }
            }
        }

        return bestCandidate;
    }

    private void UpdateAetherSeedLifecycle(float dt)
    {
        _energyPulseTimer += dt * 3.4f;
        _seedStageTimer += dt;
        LinearVelocity = Vector2.Zero;
        AngularVelocity = 0.0f;

        switch (SeedStage)
        {
            case AetherSeedStage.DormantSeed:
            {
                // Tahap 1 (10 Detik): Diam di titik nol ledakan sambil berpendar lembut
                if (_energyStarMaterial != null)
                {
                    float gentleBreath = 0.025f + 0.004f * MathF.Sin(_energyPulseTimer * 1.8f);
                    _energyStarMaterial.SetShaderParameter("star_radius", gentleBreath);
                    _energyStarMaterial.SetShaderParameter("lensing_strength", 0.016f);
                    _energyStarMaterial.SetShaderParameter("instability", 0.0f);
                }

                if (_seedStageTimer >= DormantSeedDuration)
                {
                    EnterCosmicDriftStage();
                }
                break;
            }

            case AetherSeedStage.CosmicDrift:
            {
                // Tahap 2: Meluncur cepat melintasi angkasa mencari koordinat baru & sesekali teleportasi melipat ruang
                switch (_seedTeleportState)
                {
                    case SeedTeleportState.None:
                    {
                        // 1. GERAK CEPAT MELUNCUR MENUJU TARGET (Fast Cosmic Glide)
                        Vector2 toTarget = _seedDriftTargetPos - GlobalPosition;
                        float distToTarget = toTarget.Length();

                        if (distToTarget <= 48.0f || _seedStageTimer >= CosmicDriftDuration)
                        {
                            GlobalPosition = _seedDriftTargetPos;
                            UpdateCachedTransform();
                            EnterSpacetimeInfallStage();
                            break;
                        }

                        Vector2 moveDir = toTarget / distToTarget;
                        Vector2 perpDir = new Vector2(-moveDir.Y, moveDir.X);
                        float wobble = MathF.Sin(_seedStageTimer * 5.2f) * 150.0f;
                        float speed = 540.0f; // Bergerak sangat cepat & gesit!

                        Vector2 stepVel = moveDir * speed + perpDir * wobble;
                        GlobalPosition += stepVel * dt;
                        UpdateCachedTransform();

                        // Catat jejak pendaran ekor bintang
                        _seedTrailSampleTimer += dt;
                        if (_seedTrailSampleTimer >= 0.06f)
                        {
                            _seedTrailSampleTimer = 0.0f;
                            _seedDriftTrailWorld.Add(CachedPosition);
                            if (_seedDriftTrailWorld.Count > 36)
                            {
                                _seedDriftTrailWorld.RemoveAt(0);
                            }
                        }

                        if (_energyStarMaterial != null)
                        {
                            if (_energyStarRect != null && !_energyStarRect.Visible) _energyStarRect.Visible = true;
                            float driftPulse = 0.030f + 0.005f * MathF.Sin(_energyPulseTimer * 3.2f);
                            _energyStarMaterial.SetShaderParameter("star_radius", driftPulse);
                            _energyStarMaterial.SetShaderParameter("lensing_strength", 0.028f);
                            _energyStarMaterial.SetShaderParameter("instability", 0.06f);
                        }

                        // Pengecekan pemicu Teleportasi Spacetime Lipat Ruang
                        _seedTeleportNextCooldown -= dt;
                        if (_seedTeleportNextCooldown <= 0.0f && distToTarget > 220.0f)
                        {
                            // Masuk ke fase 1: Kompresi Ruang-Waktu Ekstrem!
                            _seedTeleportState = SeedTeleportState.Compressing;
                            _seedTeleportStateTimer = 0.0f;
                            _seedTeleportDeparturePos = GlobalPosition;

                            SwarmSimulation.Instance?.TriggerAetherSeedCompressFx(_seedTeleportDeparturePos);
                        }
                        break;
                    }

                    case SeedTeleportState.Compressing:
                    {
                        // 2. RUANG TERKOMPRES SANGAT GILA MENUJU TITIK TUNGGAL (Extreme Singularity Collapse)
                        _seedTeleportStateTimer += dt;
                        float compProgress = Mathf.Clamp(_seedTeleportStateTimer / SeedTeleportCompressDuration, 0.0f, 1.0f);

                        if (_energyStarMaterial != null)
                        {
                            float compRadius = Mathf.Lerp(0.032f, 0.003f, compProgress * compProgress);
                            float compLensing = Mathf.Lerp(0.028f, 0.36f, compProgress);
                            _energyStarMaterial.SetShaderParameter("star_radius", compRadius);
                            _energyStarMaterial.SetShaderParameter("lensing_strength", compLensing);
                            _energyStarMaterial.SetShaderParameter("instability", 0.95f);
                        }

                        if (_seedTeleportStateTimer >= SeedTeleportCompressDuration)
                        {
                            // Masuk ke fase 2: Lipat Ruang & Lenyap Seketika!
                            _seedTeleportState = SeedTeleportState.Folded;
                            _seedTeleportStateTimer = 0.0f;

                            if (_energyStarRect != null) _energyStarRect.Visible = false;

                            // Tentukan titik kemunculan baru secara dinamis & acak di jalur sabuk asteroid
                            Vector2 toDest = _seedDriftTargetPos - GlobalPosition;
                            float distDest = toDest.Length();

                            Vector2 newJumpPos;
                            if (distDest <= 680.0f)
                            {
                                newJumpPos = _seedDriftTargetPos;
                            }
                            else
                            {
                                Vector2 fwd = toDest / distDest;
                                Vector2 side = new Vector2(-fwd.Y, fwd.X);
                                float jumpDist = (float)GD.RandRange(540.0, MathF.Min(distDest * 0.85f, 980.0f));
                                float lateralOffset = (GD.Randf() - 0.5f) * 500.0f;
                                newJumpPos = GlobalPosition + fwd * jumpDist + side * lateralOffset;

                                // Jaga radius koordinat dalam sabuk aman arena (1550 - 4900 px)
                                float rDist = newJumpPos.Length();
                                if (rDist < 1550.0f || rDist > 4900.0f)
                                {
                                    float clR = Mathf.Clamp(rDist, 1650.0f, 4800.0f);
                                    newJumpPos = newJumpPos.Normalized() * clR;
                                }
                            }

                            _seedTeleportTargetPos = newJumpPos;
                            GlobalPosition = _seedTeleportTargetPos;
                            UpdateCachedTransform();
                            _seedDriftTrailWorld.Clear();
                        }
                        break;
                    }

                    case SeedTeleportState.Folded:
                    {
                        // 3. SEED BENAR-BENAR LENYAP DI RUANG SUB-SPACE
                        _seedTeleportStateTimer += dt;
                        if (_seedTeleportStateTimer >= SeedTeleportFoldDuration)
                        {
                            // Masuk ke fase 3: Muncul kembali & Ruang Melebar Sangat Hebat!
                            _seedTeleportState = SeedTeleportState.Expanding;
                            _seedTeleportStateTimer = 0.0f;

                            if (_energyStarRect != null) _energyStarRect.Visible = true;
                            SwarmSimulation.Instance?.TriggerAetherSeedExpandFx(GlobalPosition);
                        }
                        break;
                    }

                    case SeedTeleportState.Expanding:
                    {
                        // 4. RUANG MELEBAR SANGAT BESAR & SEED MEKAR KEMBALI
                        _seedTeleportStateTimer += dt;
                        float expProgress = Mathf.Clamp(_seedTeleportStateTimer / SeedTeleportExpandDuration, 0.0f, 1.0f);

                        if (_energyStarMaterial != null)
                        {
                            float expRadius = Mathf.Lerp(0.092f, 0.032f, MathF.Sqrt(expProgress));
                            float expLensing = Mathf.Lerp(0.26f, 0.028f, expProgress);
                            _energyStarMaterial.SetShaderParameter("star_radius", expRadius);
                            _energyStarMaterial.SetShaderParameter("lensing_strength", expLensing);
                            _energyStarMaterial.SetShaderParameter("instability", 0.5f * (1.0f - expProgress));
                        }

                        if (_seedTeleportStateTimer >= SeedTeleportExpandDuration)
                        {
                            // Selesai teleportasi: Lanjut jalan menuju target!
                            _seedTeleportState = SeedTeleportState.None;
                            _seedTeleportStateTimer = 0.0f;
                            _seedTeleportNextCooldown = (float)GD.RandRange(1.8, 2.7);
                        }
                        break;
                    }
                }
                break;
            }

            case AetherSeedStage.SpacetimeInfall:
            {
                // Tahap 3 (30 Detik): Spacetime Infall & Rebirth - Menyedot ruang-waktu, partikel cahaya, & mekar kembali!
                GlobalPosition = _seedDriftTargetPos;
                UpdateCachedTransform();

                float p = Mathf.Clamp(_seedStageTimer / SpacetimeInfallDuration, 0.0f, 1.0f);
                float smoothP = p * p * (3.0f - 2.0f * p);

                if (_energyStarMaterial != null)
                {
                    // Biji mengembang bertahap dari 0.032 menjadi ukuran penuh Bintang Aether (0.16)
                    float growingRadius = Mathf.Lerp(0.032f, 0.16f, smoothP);
                    float growingLensing = Mathf.Lerp(0.035f, 0.165f, smoothP);
                    float birthInstability = Mathf.Lerp(0.08f, 0.45f, p * p);

                    _energyStarMaterial.SetShaderParameter("star_radius", growingRadius);
                    _energyStarMaterial.SetShaderParameter("lensing_strength", growingLensing);
                    _energyStarMaterial.SetShaderParameter("instability", birthInstability);
                }

                // Simulasi partikel cahaya kosmik yang tersedot masuk ke dalam biji (Spacetime Infall Stream)
                float speedMult = Mathf.Lerp(0.85f, 2.65f, smoothP);
                for (int i = 0; i < InfallPhotonCount; i++)
                {
                    ref var ph = ref _infallPhotons[i];
                    ph.Angle += ph.AngularSpeed * dt * speedMult;
                    ph.Distance -= ph.RadialSpeed * dt * speedMult;

                    if (ph.Distance <= 10.0f)
                    {
                        ph.Distance = ph.MaxDistance * (0.75f + 0.25f * p);
                    }
                }

                if (_seedStageTimer >= SpacetimeInfallDuration)
                {
                    ReincarnateAsNewAetherStar();
                    return;
                }
                break;
            }
        }

        QueueRedraw();
    }

    private void ReincarnateAsNewAetherStar()
    {
        SeedStage = AetherSeedStage.ActiveStar;
        _seedStageTimer = 0.0f;
        _isDestroying = false;
        _isDestroyed = false;
        _energyAccumulatedHeat = 0.0f;
        _energyLaserCavitation = 0.0f;
        _seedDriftTrailWorld.Clear();

        // Bangun kembali geometri kristal & inti Aether baru yang utuh di koordinat reinkarnasi
        UpdateCachedTransform();
        ConfigureResourceCompositionAndColors();
        GenerateProceduralGeometry();
        InitializeVoxelGrid();
        GenerateResourceVoxelPatches();
        BuildPerimeterSegments();
        RebuildAllRowSpans();
        RebuildBatchedRenderGeometry();

        CurrentHealth = MaxHealth;

        if (_circleShape != null)
        {
            _circleShape.Radius = OuterBoundRadius;
        }
        if (_collisionShape != null)
        {
            _collisionShape.SetDeferred(CollisionShape2D.PropertyName.Disabled, false);
        }

        if (_energyStarMaterial != null)
        {
            _energyStarMaterial.SetShaderParameter("star_radius", 0.16f);
            _energyStarMaterial.SetShaderParameter("lensing_strength", 0.12f);
            _energyStarMaterial.SetShaderParameter("instability", 0.0f);
            _energyStarMaterial.SetShaderParameter("hit_cavitation", 0.0f);
        }

        // Berikan kecepatan hanyut kosmik pelan pada bintang yang baru lahir
        float angle = GD.Randf() * Mathf.Tau;
        float speed = (float)GD.RandRange(5.0, 12.0);
        LinearVelocity = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * speed;
        AngularVelocity = (float)GD.RandRange(-0.06, 0.06);

        // Gelombang kejut kelahiran kembali Bintang Aether pada grid & arena
        SwarmSimulation.Instance?.TriggerAetherStarRebirthFx(CachedPosition, Radius);
        QueueRedraw();
    }

    public override void _Draw()
    {
        try
        {
            // Render visual khusus selama 3 Tahap Siklus Reinkarnasi Aether Seed
            if (IsInSeedCycle)
            {
                DrawAetherSeedLifecycle();
                return;
            }

            if (PolygonVertices.Length < 3) return;

            // RENDER KHUSUS ASTEROID ENERGY (MINI STAR SINGULARITY SHADER / FALLBACK VECTOR)
            if (IsEnergyAsteroid)
            {
                if (_energyStarMaterial == null)
                {
                    DrawEnergyCrystalSpireCluster();
                }
                return;
            }

            if (GridSide == 0) return;

            if (_geometryBatchDirty)
            {
                RebuildBatchedRenderGeometry();
            }

            // Genie Effect Transform Matrix:
            // Mentransformasikan seluruh geometri tubuh asteroid (batu, vein, dinding terowongan, outline)
            // dengan distorsi non-uniform taffy stretch (seperti efek lampu Aladdin / macOS Genie)
            // tanpa mengubah skala RigidBody2D fisik Godot!
            bool isGenieActive = IsBirthAnimationActive && (_birthLongitudinalScale != 1.0f || _birthLateralScale != 1.0f);
            if (isGenieActive)
            {
                float localAngle = BirthEjectDirection.Angle() - CachedRotation;
                Transform2D rotTo = new Transform2D(-localAngle, Vector2.Zero);
                Transform2D scaleMat = Transform2D.Identity.Scaled(new Vector2(_birthLongitudinalScale, _birthLateralScale));
                Transform2D rotBack = new Transform2D(localAngle, Vector2.Zero);

                // Sedikit pergeseran titik tumpu maju agar ujung depan asteroid memimpin keluar
                Vector2 localDir = BirthEjectDirection.Rotated(-CachedRotation);
                float p = Mathf.Clamp(_birthAnimTimer / BirthAnimDuration, 0.0f, 1.0f);
                float leadOffset = (1.0f - _birthLongitudinalScale) * Radius * 0.25f * (1.0f - p);
                Transform2D leadShift = Transform2D.Identity.Translated(localDir * leadOffset);

                DrawSetTransformMatrix(leadShift * rotBack * scaleMat * rotTo);
            }

            // 1. Gambar Badan Asteroid Batu Biasa
            if (_carvedVoxelCount == 0)
            {
                DrawColoredPolygon(PolygonVertices, _rockBodyColor);
            }
            else
            {
                for (int i = 0; i < _mergedRockRects.Count; i++)
                {
                    DrawRect(_mergedRockRects[i], _rockBodyColor, true);
                }
            }

            // 2. Gambar Patch Resource
            float scanRatio;
            if (CurrentScannerCount <= 0)
            {
                scanRatio = 0.05f;
            }
            else
            {
                float norm = Mathf.Clamp((float)CurrentScannerCount / 65.0f, 0.0f, 1.0f);
                scanRatio = 0.05f + 0.95f * MathF.Pow(norm, 1.35f);
            }

            for (int t = 0; t < ResourceCount; t++)
            {
                var rectList = _mergedResourceRects[t];
                if (rectList.Count == 0) continue;

                Color baseResCol = GetResourceColor((ResourceType)t);
                Color fillColor = new Color(
                    baseResCol.R * (0.12f + scanRatio * 0.88f),
                    baseResCol.G * (0.12f + scanRatio * 0.88f),
                    baseResCol.B * (0.12f + scanRatio * 0.88f),
                    0.06f + scanRatio * 0.84f
                );

                bool drawVein = scanRatio > 0.55f;
                Color veinBorderCol = drawVein
                    ? new Color(baseResCol.R * 1.25f, baseResCol.G * 1.25f, baseResCol.B * 1.25f, (scanRatio - 0.55f) * 0.85f)
                    : default;

                for (int i = 0; i < rectList.Count; i++)
                {
                    DrawRect(rectList[i], fillColor, true);
                    if (drawVein)
                    {
                        DrawRect(rectList[i], veinBorderCol, false, 1.1f);
                    }
                }
            }

            // 3. Gambar Tepian Dinding Terowongan Batu Biasa
            if (_carvedVoxelCount > 0 && _batchedWallLines.Length >= 2)
            {
                DrawMultiline(_batchedWallLines, _tunnelWallBorderColor, 1.2f, false);
            }

            // 4. TEPI BATU RESOURCE MENYALA SAAT DRILLING SUPAI RESOURCE
            if (_carvedVoxelCount > 0)
            {
                for (int t = 0; t < ResourceCount; t++)
                {
                    var exposedEdges = _batchedExposedResourceEdges[t];
                    if (exposedEdges != null && exposedEdges.Length >= 2)
                    {
                        Color neonEdgeCol = GetResourceColor((ResourceType)t) * 1.45f;
                        neonEdgeCol.A = 1.0f;
                        DrawMultiline(exposedEdges, neonEdgeCol, 2.4f, false);
                    }
                }
            }

            // 5. Gambar Pemanasan Bertahap pada Blok 13 px yang Sedang Dikikis Laser / Driller
            if (_activeHeatedIndices.Count > 0)
            {
                Color hotOrange = new Color(2.6f, 0.85f, 0.12f, 0.85f);
                Color incandescentYellow = new Color(4.6f, 4.1f, 1.15f, 0.98f);

                for (int i = 0; i < _activeHeatedIndices.Count; i++)
                {
                    int idx = _activeHeatedIndices[i];
                    VoxelType vox = _voxels[idx];
                    if (vox == VoxelType.Empty) continue;

                    float heat = Mathf.Clamp(_voxelHeat[idx], 0.0f, 1.0f);
                    if (heat <= 0.01f) continue;

                    int gx = idx % GridSide;
                    int gy = idx / GridSide;
                    Rect2 cellRect = new Rect2(
                        GridOriginLocal.X + gx * VoxelSize,
                        GridOriginLocal.Y + gy * VoxelSize,
                        VoxelSize,
                        VoxelSize
                    );

                    Color glowCol = hotOrange.Lerp(incandescentYellow, heat * heat);
                    Color fillHeatCol = new Color(glowCol.R, glowCol.G, glowCol.B, 0.22f + heat * 0.72f);

                    DrawRect(cellRect, fillHeatCol, true);

                    if (TryGetResourceType(vox, out ResourceType rType))
                    {
                        Color resNeon = GetResourceColor(rType) * 1.65f;
                        resNeon.A = 1.0f;
                        DrawRect(cellRect.Grow(1.2f), resNeon, false, 2.2f);
                    }
                    else
                    {
                        Color outlineHeatCol = new Color(glowCol.R * 1.15f, glowCol.G * 1.15f, glowCol.B * 1.1f, 0.55f + heat * 0.45f);
                        DrawRect(cellRect, outlineHeatCol, false, 1.4f);
                    }
                }
            }

            // 6. Garis Kontur Luar Asteroid
            Color outlineColor = _unscannedBorderColor;
            float borderWidth = 2.2f;

            if (_carvedVoxelCount == 0)
            {
                DrawPolyline(_closedOutlinePoints, outlineColor, borderWidth, false);
            }
            else if (_batchedPerimeterLines.Length >= 2)
            {
                DrawMultiline(_batchedPerimeterLines, outlineColor, borderWidth, false);
            }

            // 7. Render Animasi Retakan Jaring Laba-Laba (Spiderweb Catastrophic Fracture) saat Asteroid Runtuh
            if (_isDestroying && _catastrophicFractureLines.Count >= 2)
            {
                float progress = 1.0f - Mathf.Clamp(_destructionTimer / MathF.Max(0.01f, _currentDestructionDuration), 0.0f, 1.0f);
                float flash = 0.60f + 0.40f * MathF.Sin(_destructionTimer * 52.0f);
                Color resCol = ResourceGlowColor;
                Color fractureColor = resCol.Lerp(new Color(4.8f, 4.2f, 2.2f, 1.0f), 0.55f + progress * 0.35f);
                fractureColor.A = 0.92f * flash;

                DrawMultiline(_catastrophicFractureLines.ToArray(), fractureColor, 2.6f + progress * 2.4f, false);
                DrawCircle(Vector2.Zero, Radius * progress * 0.68f, new Color(fractureColor.R, fractureColor.G, fractureColor.B, 0.28f * (1.0f - progress)));
            }

            // 8. Render Pendaran Panas Super-Heated saat Asteroid Baru Lahir dari White Hole
            if (_birthGlow > 0.01f)
            {
                Color birthThermal = new Color(5.5f, 4.8f, 3.2f, _birthGlow * 0.85f);
                DrawCircle(Vector2.Zero, Radius * 0.96f, birthThermal);
                DrawArc(Vector2.Zero, Radius * 1.05f, 0.0f, Mathf.Tau, 48, new Color(5.5f, 5.2f, 4.8f, _birthGlow), 3.5f, false);
            }

            // Reset matrix transform kembali ke koordinat lokal normal untuk rendering tali pusar
            if (isGenieActive)
            {
                DrawSetTransformMatrix(Transform2D.Identity);
            }

            // 9. TALI PUSAR PLASMA KELAHIRAN JIN (Genie Umbilical Funnel)
            if (IsBirthAnimationActive && _birthAnimTimer < BirthAnimDuration * 0.78f)
            {
                DrawGenieUmbilicalFunnel();
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[Asteroid._Draw] {ex}");
        }
    }

    private void DrawGenieUmbilicalFunnel()
    {
        float p = Mathf.Clamp(_birthAnimTimer / (BirthAnimDuration * 0.78f), 0.0f, 1.0f);
        float funnelAlpha = 1.0f - p;
        if (funnelAlpha <= 0.01f) return;

        Vector2 riftLocal = FastWorldToLocal(BirthRiftOrigin);
        Vector2 localEjectDir = BirthEjectDirection.Rotated(-CachedRotation);

        // Pangkal belakang asteroid (tempat menempelnya corong jin)
        Vector2 asteroidBase = -localEjectDir * (Radius * _birthLongitudinalScale * 0.75f);
        Vector2 spine = asteroidBase - riftLocal;
        float spineLen = spine.Length();
        if (spineLen < 6.0f) return;

        Vector2 spineDir = spine / spineLen;
        Vector2 spinePerp = new Vector2(-spineDir.Y, spineDir.X);

        // Lebar corong: sempit di mulut white hole, melebar di pangkal asteroid (efek terompet/lampu jin)
        float mouthWidth = Mathf.Lerp(24.0f, 12.0f, p);
        float baseWidth = Radius * 2.0f * _birthLateralScale * 0.65f;

        const int SliceCount = 8;
        Vector2[] polyPts = new Vector2[(SliceCount + 1) * 2];
        Vector2[] corePolyPts = new Vector2[(SliceCount + 1) * 2];

        for (int i = 0; i <= SliceCount; i++)
        {
            float u = (float)i / SliceCount; // 0 di white hole, 1 di asteroid base
            Vector2 ptCenter = riftLocal.Lerp(asteroidBase, u);
            // Non-linear cubic flare: ramping di pangkal lubang, melebar cepat mendekati asteroid
            float flare = u * u * (3.0f - 2.0f * u);
            float currentWidth = Mathf.Lerp(mouthWidth, baseWidth, flare);

            Vector2 offset = spinePerp * (currentWidth * 0.5f);
            polyPts[i] = ptCenter + offset;
            polyPts[(SliceCount + 1) * 2 - 1 - i] = ptCenter - offset;

            Vector2 coreOffset = spinePerp * (currentWidth * 0.22f);
            corePolyPts[i] = ptCenter + coreOffset;
            corePolyPts[(SliceCount + 1) * 2 - 1 - i] = ptCenter - coreOffset;
        }

        // 1. Lapisan plasma luar: warna magma aetherik keemasan/cyan
        Color sheathColor = new Color(3.6f, 2.2f, 0.8f, funnelAlpha * 0.75f);
        DrawColoredPolygon(polyPts, sheathColor);

        // 2. Lapisan inti putih-perak ultra panas di tengah corong
        Color coreColor = new Color(5.5f, 5.2f, 4.6f, funnelAlpha * 0.95f);
        DrawColoredPolygon(corePolyPts, coreColor);

        // 3. Garis filament tegangan ruang-waktu di tepi corong
        Color filamentCol = new Color(0.75f, 2.8f, 4.5f, funnelAlpha * 0.85f);
        for (int i = 0; i < SliceCount; i++)
        {
            DrawLine(polyPts[i], polyPts[i + 1], filamentCol, 1.8f);
            int idxR1 = (SliceCount + 1) * 2 - 1 - i;
            int idxR2 = idxR1 - 1;
            DrawLine(polyPts[idxR1], polyPts[idxR2], filamentCol, 1.8f);
        }

        // 4. Percikan plasma saat tali meregang mendekati batas putus
        if (p > 0.45f)
        {
            float sparkRatio = (p - 0.45f) / 0.55f;
            int sparkCount = 6;
            for (int s = 0; s < sparkCount; s++)
            {
                float su = 0.30f + 0.60f * ((s * 0.17f + p * 1.8f) % 1.0f);
                Vector2 sPos = riftLocal.Lerp(asteroidBase, su) + spinePerp * (MathF.Sin(s * 3.1f + p * 12.0f) * 14.0f * (1.0f - su));
                DrawCircle(sPos, 2.4f * (1.0f - sparkRatio * 0.4f), new Color(5.0f, 4.5f, 2.5f, funnelAlpha));
            }
        }
    }

    private void DrawAetherSeedLifecycle()
    {
        float pulse = 0.88f + 0.12f * MathF.Sin(_energyPulseTimer * 2.6f);
        Color seedGold = new Color(3.8f, 3.2f, 1.25f, 0.92f);
        Color seedCyan = new Color(0.45f, 2.65f, 3.85f, 0.85f);

        if (SeedStage == AetherSeedStage.DormantSeed)
        {
            // Tahap 1: Dormant Seed (10s) - Pendaran lembut dan tenang di titik nol ledakan
            float haloR = 32.0f * pulse;
            DrawCircle(Vector2.Zero, haloR * 1.45f, new Color(0.4f, 1.8f, 2.8f, 0.14f));
            DrawCircle(Vector2.Zero, haloR, new Color(2.6f, 2.1f, 0.8f, 0.24f));
            DrawArc(Vector2.Zero, 22.0f * pulse, 0.0f, Mathf.Tau, 32, new Color(seedCyan.R, seedCyan.G, seedCyan.B, 0.55f), 1.6f, true);
            DrawCircle(Vector2.Zero, 8.5f * pulse, seedGold);
        }
        else if (SeedStage == AetherSeedStage.CosmicDrift)
        {
            // Tahap 2: Cosmic Drift - Gerak cepat & Lipat Ruang (Teleportasi Sub-Space)
            if (_seedTeleportState == SeedTeleportState.Folded)
            {
                // Saat melipat ruang (Folded), biji benar-benar lenyap!
                return;
            }

            if (_seedTeleportState == SeedTeleportState.Compressing)
            {
                // Efek visual ruang terkompresi sangat gila: pusaran garis foton menyusut ke titik singularitas
                float compRatio = Mathf.Clamp(_seedTeleportStateTimer / SeedTeleportCompressDuration, 0.0f, 1.0f);
                float compR = Mathf.Lerp(42.0f, 3.5f, compRatio);
                DrawCircle(Vector2.Zero, compR, new Color(3.8f, 3.2f, 1.25f, 0.95f * (1.0f - compRatio * 0.45f)));

                int rayCount = 8;
                for (int r = 0; r < rayCount; r++)
                {
                    float angle = (float)r / rayCount * Mathf.Tau + compRatio * 8.5f;
                    Vector2 dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                    Vector2 pOuter = dir * (compR * 3.6f);
                    Vector2 pInner = dir * compR;
                    DrawLine(pOuter, pInner, new Color(0.45f, 2.85f, 4.2f, 0.85f * compRatio), 2.2f);
                }
                return;
            }

            if (_seedTeleportState == SeedTeleportState.Expanding)
            {
                // Efek visual ruang melebar hebat saat muncul kembali dari sub-space
                float expRatio = Mathf.Clamp(_seedTeleportStateTimer / SeedTeleportExpandDuration, 0.0f, 1.0f);
                float bloomR = Mathf.Lerp(64.0f, 14.0f, MathF.Sqrt(expRatio));
                float alpha = (1.0f - expRatio);
                DrawCircle(Vector2.Zero, bloomR * 1.55f, new Color(0.45f, 2.5f, 3.8f, 0.35f * alpha));
                DrawCircle(Vector2.Zero, bloomR, new Color(3.8f, 3.2f, 1.25f, 0.75f * alpha));
                DrawArc(Vector2.Zero, bloomR * 2.1f, 0.0f, Mathf.Tau, 32, new Color(3.8f, 3.2f, 1.25f, alpha), 2.4f, true);
            }

            // Jejak pendaran ekor bintang meluncur cepat
            if (_seedDriftTrailWorld.Count >= 2)
            {
                for (int i = 0; i < _seedDriftTrailWorld.Count - 1; i++)
                {
                    float t = (float)(i + 1) / _seedDriftTrailWorld.Count;
                    Vector2 pA = FastWorldToLocal(_seedDriftTrailWorld[i]);
                    Vector2 pB = FastWorldToLocal(_seedDriftTrailWorld[i + 1]);
                    Color trailCol = new Color(0.45f, 2.35f, 3.55f, t * 0.55f);
                    DrawLine(pA, pB, trailCol, 1.5f + t * 2.2f);
                }
            }

            float haloR = 36.0f * pulse;
            DrawCircle(Vector2.Zero, haloR, new Color(0.45f, 2.2f, 3.4f, 0.26f));
            DrawArc(Vector2.Zero, 24.0f * pulse, _energyPulseTimer, _energyPulseTimer + Mathf.Pi * 1.35f, 24, seedCyan, 2.0f, true);
            DrawArc(Vector2.Zero, 18.0f * pulse, -_energyPulseTimer * 1.2f, -_energyPulseTimer * 1.2f + Mathf.Pi * 1.35f, 24, seedGold, 1.8f, true);
            DrawCircle(Vector2.Zero, 10.0f * pulse, seedGold);
        }
        else if (SeedStage == AetherSeedStage.SpacetimeInfall)
        {
            // Tahap 3: Spacetime Infall & Rebirth (30s) - Partikel cahaya angkasa tersedot agresif ke arah biji!
            float p = Mathf.Clamp(_seedStageTimer / SpacetimeInfallDuration, 0.0f, 1.0f);
            float smoothP = p * p * (3.0f - 2.0f * p);

            // 1. Cincin gelombang ruang-waktu yang menyusut tersedot ke dalam pusat biji
            int ringCount = 3;
            for (int r = 0; r < ringCount; r++)
            {
                float ringPhase = ((_energyPulseTimer * (0.45f + p * 0.65f)) + (float)r / ringCount) % 1.0f;
                float invPhase = 1.0f - ringPhase;
                float maxInfallR = Mathf.Lerp(180.0f, 380.0f, smoothP);
                float curR = Mathf.Lerp(16.0f, maxInfallR, invPhase);
                float alpha = MathF.Sin(ringPhase * Mathf.Pi) * (0.32f + 0.45f * smoothP);
                Color ringCol = (r % 2 == 0)
                    ? new Color(0.45f, 2.45f, 3.65f, alpha)
                    : new Color(3.6f, 2.9f, 1.1f, alpha);
                DrawArc(Vector2.Zero, curR, 0.0f, Mathf.Tau, 40, ringCol, 1.8f + smoothP * 1.6f, true);
            }

            // 2. Partikel cahaya angkasa (Infall Photons) yang tersedot melengkung ke arah biji
            for (int i = 0; i < InfallPhotonCount; i++)
            {
                var ph = _infallPhotons[i];
                Vector2 dir = new Vector2(MathF.Cos(ph.Angle), MathF.Sin(ph.Angle));
                Vector2 tang = new Vector2(-dir.Y, dir.X);
                Vector2 headPos = dir * (ph.Distance * 0.82f);
                float streakLen = Mathf.Lerp(14.0f, 42.0f, smoothP) * (1.0f - Mathf.Clamp(ph.Distance / ph.MaxDistance, 0.0f, 0.85f));
                Vector2 tailPos = headPos + (dir * streakLen) - (tang * (streakLen * 0.38f));

                Color streakCol = ph.Color;
                streakCol.A = Mathf.Clamp((1.0f - ph.Distance / ph.MaxDistance) * (0.55f + 0.45f * smoothP), 0.12f, 0.98f);
                DrawLine(tailPos, headPos, streakCol, ph.Size * (0.75f + 0.5f * smoothP));
                DrawCircle(headPos, ph.Size * 0.65f, seedGold);
            }

            // 3. Biji mengembang dan mekar di pusat pusaran
            float bloomR = Mathf.Lerp(12.0f, _crystalCoreRadius * 1.05f, smoothP) * pulse;
            DrawCircle(Vector2.Zero, bloomR * 1.55f, new Color(0.45f, 2.2f, 3.5f, 0.22f + 0.25f * smoothP));
            DrawCircle(Vector2.Zero, bloomR, new Color(3.8f, 3.2f, 1.25f, 0.75f + 0.20f * smoothP));
        }
    }

    private void DrawEnergyCrystalSpireCluster()
    {
        float coreRatio = _totalCoreVoxels > 0
            ? Mathf.Clamp((float)_remainingCoreVoxels / _totalCoreVoxels, 0.0f, 1.0f)
            : 1.0f;

        if (coreRatio > 0.02f)
        {
            float pulse = 0.90f + 0.10f * MathF.Sin(_energyPulseTimer * 2.8f);
            float coreR = _crystalCoreRadius * coreRatio * pulse;

            DrawCircle(Vector2.Zero, coreR * 1.35f, new Color(1.8f, 1.1f, 0.25f, 0.16f * coreRatio));
            DrawCircle(Vector2.Zero, coreR * 1.05f, new Color(2.4f, 1.8f, 0.55f, 0.35f * coreRatio));
            DrawCircle(Vector2.Zero, coreR * 0.72f, new Color(2.8f, 2.4f, 1.2f, 0.78f * coreRatio));
            DrawCircle(Vector2.Zero, coreR * 0.35f, new Color(3.5f, 3.2f, 2.2f, 0.95f * coreRatio));

            int arcCount = 5;
            float arcStep = Mathf.Tau / arcCount;
            Color arcColor = new Color(1.2f, 2.8f, 3.8f, 0.75f * coreRatio);
            Color arcGlow = new Color(0.4f, 1.6f, 2.8f, 0.35f * coreRatio);

            for (int a = 0; a < arcCount; a++)
            {
                float arcAngle = a * arcStep + _energyPulseTimer * 0.55f;
                Vector2 start = new Vector2(MathF.Cos(arcAngle), MathF.Sin(arcAngle)) * (coreR * 0.75f);
                Vector2 end = new Vector2(MathF.Cos(arcAngle), MathF.Sin(arcAngle)) * (_crystalCoreRadius * 1.28f);

                float j1 = MathF.Sin(_energyPulseTimer * 18.0f + a * 11.0f) * 7.0f;
                float j2 = MathF.Cos(_energyPulseTimer * 25.0f + a * 17.0f) * 8.0f;

                Vector2 perp = new Vector2(-MathF.Sin(arcAngle), MathF.Cos(arcAngle));
                _reusableArcPts[0] = start;
                _reusableArcPts[1] = start.Lerp(end, 0.33f) + perp * j1;
                _reusableArcPts[2] = start.Lerp(end, 0.66f) + perp * j2;
                _reusableArcPts[3] = end;

                DrawPolyline(_reusableArcPts, arcGlow, 2.6f, false);
                DrawPolyline(_reusableArcPts, arcColor, 1.3f, false);
            }
        }

        Color heatGlowColor = new Color(4.8f, 4.2f, 2.2f, 0.95f);

        for (int i = 0; i < _crystalSpires.Length; i++)
        {
            ref var spire = ref _crystalSpires[i];
            if (spire.TotalVoxels > 0 && spire.RemainingVoxels <= 0) continue;

            float healthRatio = spire.TotalVoxels > 0
                ? Mathf.Clamp((float)spire.RemainingVoxels / spire.TotalVoxels, 0.0f, 1.0f)
                : 1.0f;

            if (healthRatio <= 0.01f) continue;

            bool isHeated = spire.Heat > 0.01f;
            float vibe = isHeated ? MathF.Sin(_energyPulseTimer * 48.0f + i * 4.3f) * (spire.Heat * 2.6f) : 0.0f;

            float effectiveRatio = MathF.Max(0.06f, healthRatio);
            Vector2 curTip = spire.BaseCenter.Lerp(spire.Tip, effectiveRatio) + spire.Perp * vibe;
            Vector2 curMidLeft = spire.BaseLeft.Lerp(spire.MidLeft, effectiveRatio);
            Vector2 curMidRight = spire.BaseRight.Lerp(spire.MidRight, effectiveRatio);
            Vector2 curRidge = spire.BaseCenter.Lerp(spire.RidgeCenter, effectiveRatio);

            Vector2[] leftFacet = new Vector2[] { spire.BaseLeft, curMidLeft, curTip, curRidge, spire.BaseCenter };
            Vector2[] rightFacet = new Vector2[] { spire.BaseCenter, curRidge, curTip, curMidRight, spire.BaseRight };
            Vector2[] outline = new Vector2[] { spire.BaseLeft, curMidLeft, curTip, curMidRight, spire.BaseRight };
            Vector2[] ridge = new Vector2[] { spire.BaseCenter, curRidge, curTip };

            Color litFacet = new Color(0.10f, 0.14f, 0.24f, 0.85f * healthRatio);
            Color shadedFacet = new Color(0.05f, 0.08f, 0.15f, 0.90f * healthRatio);
            Color outlineCol = new Color(0.40f, 0.60f, 0.90f, 0.88f * healthRatio);
            Color ridgeCol = new Color(0.55f, 0.80f, 1.20f, 0.92f * healthRatio);

            if (isHeated)
            {
                litFacet = litFacet.Lerp(heatGlowColor, spire.Heat * 0.75f);
                outlineCol = outlineCol.Lerp(heatGlowColor, spire.Heat);
            }

            DrawColoredPolygon(leftFacet, litFacet);
            DrawColoredPolygon(rightFacet, shadedFacet);
            DrawPolyline(outline, outlineCol, 1.6f, false);
            DrawPolyline(ridge, ridgeCol, 1.3f, false);

            if (spire.HasResourceVein)
            {
                Color veinCol = new Color(0.8f, 3.4f, 4.6f, 0.95f * healthRatio);
                DrawLine(curRidge, curTip, veinCol, 2.2f);
                DrawCircle(curRidge, 3.2f, new Color(1.2f, 3.8f, 5.0f, 0.90f * healthRatio));
            }

            if (healthRatio < 0.80f)
            {
                Color crackCol = new Color(4.8f, 4.4f, 2.8f, (1.0f - healthRatio) * 0.90f);
                Vector2 crack1 = curMidLeft.Lerp(curRidge, 0.42f);
                Vector2 crack2 = curTip.Lerp(spire.BaseCenter, 0.52f) + spire.Perp * (vibe * 1.8f + 2.5f);
                Vector2 crack3 = curMidRight.Lerp(spire.BaseCenter, 0.32f);
                DrawLine(crack1, crack2, crackCol, 1.4f);
                DrawLine(crack2, crack3, crackCol, 1.1f);

                if (healthRatio < 0.45f)
                {
                    Vector2 crack4 = curMidLeft.Lerp(spire.BaseLeft, 0.48f);
                    Vector2 crack5 = crack2.Lerp(curTip, 0.45f) - spire.Perp * 3.0f;
                    DrawLine(crack4, crack5, crackCol, 1.2f);
                }
            }
        }

        if (_isDestroying && _catastrophicFractureLines.Count >= 2)
        {
            float progress = 1.0f - Mathf.Clamp(_destructionTimer / MathF.Max(0.01f, _currentDestructionDuration), 0.0f, 1.0f);
            float flash = 0.55f + 0.45f * MathF.Sin(_destructionTimer * 48.0f);
            Color fractureColor = new Color(5.5f, 4.8f, 1.8f, 0.95f * flash);
            DrawMultiline(_catastrophicFractureLines.ToArray(), fractureColor, 2.8f + progress * 2.0f, false);
            DrawCircle(Vector2.Zero, Radius * progress * 0.72f, new Color(fractureColor.R, fractureColor.G, fractureColor.B, 0.30f * (1.0f - progress)));
        }
    }

    private void OnEnergyVoxelDestroyed(int gx, int gy)
    {
        Vector2 cellPos = GridToLocalCenter(gx, gy);
        float distSq = cellPos.LengthSquared();
        if (distSq <= _crystalCoreRadius * _crystalCoreRadius)
        {
            _remainingCoreVoxels = Math.Max(0, _remainingCoreVoxels - 1);
            QueueRedraw();
            return;
        }

        if (_crystalSpires.Length == 0) return;

        for (int i = 0; i < _crystalSpires.Length; i++)
        {
            if (IsPointInPolygon(cellPos, _crystalSpires[i].Polygon))
            {
                _crystalSpires[i].RemainingVoxels = Math.Max(0, _crystalSpires[i].RemainingVoxels - 1);
                QueueRedraw();
                return;
            }
        }
    }

    private void RegisterEnergySpireHeat(int gx, int gy, float heat)
    {
        if (_crystalSpires.Length == 0) return;
        Vector2 cellPos = GridToLocalCenter(gx, gy);

        for (int i = 0; i < _crystalSpires.Length; i++)
        {
            if (IsPointInPolygon(cellPos, _crystalSpires[i].Polygon))
            {
                _crystalSpires[i].Heat = MathF.Min(1.0f, _crystalSpires[i].Heat + heat * 1.5f);
                QueueRedraw();
                return;
            }
        }
    }

    // =========================================================================
    // A. MEKANIK PENGIKISAN TITIK TUMBUKAN (RAYMARCHING & EROSION 13 PX)
    // =========================================================================

    public bool RaycastVoxel(Vector2 worldStart, Vector2 worldDir, float maxDist, out VoxelRayHit hit)
    {
        hit = default;
        if (_isDestroyed || IsInSeedCycle || GridSide == 0 || _remainingSolidVoxelCount <= 0) return false;

        Vector2 dirNorm = worldDir.Normalized();
        Vector2 toCenter = CachedPosition - worldStart;
        float proj = toCenter.Dot(dirNorm);
        if (proj < -OuterBoundRadius || proj - OuterBoundRadius > maxDist) return false;

        Vector2 perpFoot = worldStart + dirNorm * proj;
        float perpDistSq = (CachedPosition - perpFoot).LengthSquared();
        float boundSq = OuterBoundRadius * OuterBoundRadius;
        if (perpDistSq > boundSq) return false;

        Vector2 localStart = FastWorldToLocal(worldStart);
        Vector2 localDir = FastRotateToLocal(dirNorm);

        float halfChord = MathF.Sqrt(MathF.Max(0.0f, boundSq - perpDistSq));
        float tEnter = MathF.Max(0.0f, proj - halfChord);
        if (tEnter > maxDist) return false;

        // BINTANG AETHER: Bola plasma fusi murni. Laser selalu membentur permukaan luar bintang
        // (tidak pernah melubangi terowongan kosong / tembus ke belakang!)
        if (IsEnergyAsteroid)
        {
            Vector2 worldImpact = worldStart + dirNorm * tEnter;
            Vector2 localImpact = FastWorldToLocal(worldImpact);
            Vector2I gPos = LocalToGrid(localImpact);

            hit = new VoxelRayHit
            {
                Hit = true,
                Distance = tEnter,
                GridX = Math.Clamp(gPos.X, 0, GridSide - 1),
                GridY = Math.Clamp(gPos.Y, 0, GridSide - 1),
                WorldCellCenter = worldImpact,
                WorldHitPoint = worldImpact,
                Type = VoxelType.ResourceAether,
                CurrentHeat = _energyAccumulatedHeat
            };
            return true;
        }

        float tExit = MathF.Min(maxDist, proj + halfChord + VoxelSize);
        if (tExit < tEnter) return false;

        Vector2 rayEntry = localStart + localDir * tEnter;
        float relX = (rayEntry.X - GridOriginLocal.X) * InvVoxelSize;
        float relY = (rayEntry.Y - GridOriginLocal.Y) * InvVoxelSize;

        int gx = (int)MathF.Floor(relX);
        int gy = (int)MathF.Floor(relY);

        int stepX = localDir.X >= 0.0f ? 1 : -1;
        int stepY = localDir.Y >= 0.0f ? 1 : -1;

        float tDeltaX = MathF.Abs(localDir.X) > 1e-6f ? VoxelSize / MathF.Abs(localDir.X) : 1e30f;
        float tDeltaY = MathF.Abs(localDir.Y) > 1e-6f ? VoxelSize / MathF.Abs(localDir.Y) : 1e30f;

        float nextBoundX = GridOriginLocal.X + (localDir.X >= 0.0f ? (gx + 1) : gx) * VoxelSize;
        float nextBoundY = GridOriginLocal.Y + (localDir.Y >= 0.0f ? (gy + 1) : gy) * VoxelSize;

        float tMaxX = MathF.Abs(localDir.X) > 1e-6f ? tEnter + (nextBoundX - rayEntry.X) / localDir.X : 1e30f;
        float tMaxY = MathF.Abs(localDir.Y) > 1e-6f ? tEnter + (nextBoundY - rayEntry.Y) / localDir.Y : 1e30f;

        float t = tEnter;
        int maxSteps = GridSide * 3;

        for (int step = 0; step < maxSteps && t <= tExit; step++)
        {
            if (gx >= 0 && gx < GridSide && gy >= 0 && gy < GridSide)
            {
                int idx = gy * GridSide + gx;
                VoxelType cellType = _voxels[idx];

                if (cellType != VoxelType.Empty)
                {
                    float hitT = MathF.Max(0.0f, t);
                    Vector2 localHit = localStart + localDir * hitT;
                    Vector2 localCellCenter = GridToLocalCenter(gx, gy);

                    hit = new VoxelRayHit
                    {
                        Hit = true,
                        GridX = gx,
                        GridY = gy,
                        Type = cellType,
                        WorldHitPoint = FastLocalToWorld(localHit),
                        WorldCellCenter = FastLocalToWorld(localCellCenter),
                        Distance = hitT,
                        CurrentHeat = _voxelHeat[idx]
                    };
                    return true;
                }
            }

            if (tMaxX < tMaxY)
            {
                t = tMaxX;
                tMaxX += tDeltaX;
                gx += stepX;
            }
            else
            {
                t = tMaxY;
                tMaxY += tDeltaY;
                gy += stepY;
            }
        }

        return false;
    }

    public bool ApplyLaserHeatAtVoxel(
        int gx,
        int gy,
        float heatDelta,
        Vector2 worldRayDir,
        out VoxelType destroyedType,
        out Vector2 worldCellCenter,
        out float currentHeat)
    {
        destroyedType = VoxelType.Empty;
        worldCellCenter = CachedPosition;
        currentHeat = 0.0f;

        if (_isDestroyed || IsInSeedCycle) return false;

        // BINTANG AETHER: Menyerap radiasi laser fusi secara terpusat (tidak pernah dilubangi menjadi Empty!)
        if (IsEnergyAsteroid)
        {
            Vector2 impactWorld = IsValidGridCoord(gx, gy)
                ? FastLocalToWorld(GridToLocalCenter(gx, gy))
                : CachedPosition;
            worldCellCenter = impactWorld;

            // Hit angle menghadap arah datangnya tembakan laser (arah mundur dari worldRayDir)
            _energyLaserHitAngle = (-worldRayDir).Angle();
            _energyLaserCavitation = Mathf.Min(1.0f, _energyLaserCavitation + 0.35f);
            _energyWasHitThisFrame = true;

            // Efek dorongan momentum radiasi foton laser pada bintang
            ApplyImpulse(worldRayDir * (heatDelta * 1.2f), worldCellCenter - CachedPosition);

            currentHeat = _energyAccumulatedHeat;
            QueueRedraw();
            return false; // Voxel fusi plasma murni tidak dilubangi/dikosongkan!
        }

        if (!IsValidGridCoord(gx, gy)) return false;

        int idx = gy * GridSide + gx;
        VoxelType currentType = _voxels[idx];
        if (currentType == VoxelType.Empty) return false;

        worldCellCenter = FastLocalToWorld(GridToLocalCenter(gx, gy));

        if (_voxelHeat[idx] <= 0.0f && !_activeHeatedIndices.Contains(idx))
        {
            _activeHeatedIndices.Add(idx);
        }

        _heatedThisFrame.Add(idx);
        _voxelHeat[idx] += heatDelta;
        currentHeat = Mathf.Clamp(_voxelHeat[idx], 0.0f, 1.0f);

        ApplyImpulse(worldRayDir * (heatDelta * 1.2f), worldCellCenter - CachedPosition);
        QueueRedraw();

        if (_voxelHeat[idx] >= 1.0f)
        {
            destroyedType = currentType;
            _voxels[idx] = VoxelType.Empty;
            _voxelHeat[idx] = 0.0f;
            _voxelKineticDamage[idx] = 0.0f;
            _activeHeatedIndices.Remove(idx);

            _remainingSolidVoxelCount = Math.Max(0, _remainingSolidVoxelCount - 1);
            if (TryGetResourceType(currentType, out _))
            {
                _remainingResourceVoxelCount = Math.Max(0, _remainingResourceVoxelCount - 1);
            }
            _carvedVoxelCount++;

            if (IsEnergyAsteroid)
            {
                OnEnergyVoxelDestroyed(gx, gy);
            }

            RebuildRowSpans(gy);
            CheckDestructionThreshold();

            return true;
        }

        return false;
    }

    public bool HasSolidVoxelsInRadius(Vector2 worldCenter, float checkRadius, out Vector2 closestVoxelWorldCenter)
    {
        closestVoxelWorldCenter = worldCenter;
        if (_isDestroyed || IsInSeedCycle || GridSide == 0 || _remainingSolidVoxelCount <= 0) return false;

        float maxBound = OuterBoundRadius + checkRadius;
        float diffX = worldCenter.X - CachedPosition.X;
        float diffY = worldCenter.Y - CachedPosition.Y;
        if (MathF.Abs(diffX) > maxBound || MathF.Abs(diffY) > maxBound) return false;
        if (diffX * diffX + diffY * diffY > maxBound * maxBound) return false;

        Vector2 localCenter = FastWorldToLocal(worldCenter);
        int minGx = Math.Clamp((int)MathF.Floor((localCenter.X - checkRadius - GridOriginLocal.X) * InvVoxelSize), 0, GridSide - 1);
        int maxGx = Math.Clamp((int)MathF.Floor((localCenter.X + checkRadius - GridOriginLocal.X) * InvVoxelSize), 0, GridSide - 1);
        int minGy = Math.Clamp((int)MathF.Floor((localCenter.Y - checkRadius - GridOriginLocal.Y) * InvVoxelSize), 0, GridSide - 1);
        int maxGy = Math.Clamp((int)MathF.Floor((localCenter.Y + checkRadius - GridOriginLocal.Y) * InvVoxelSize), 0, GridSide - 1);

        float rSq = checkRadius * checkRadius;
        float bestDistSq = float.MaxValue;
        Vector2 bestLocal = localCenter;
        bool found = false;

        for (int gy = minGy; gy <= maxGy; gy++)
        {
            float cy = GridOriginLocal.Y + (gy + 0.5f) * VoxelSize;
            int rowOffset = gy * GridSide;
            for (int gx = minGx; gx <= maxGx; gx++)
            {
                if (_voxels[rowOffset + gx] == VoxelType.Empty) continue;

                float cx = GridOriginLocal.X + (gx + 0.5f) * VoxelSize;
                float dx = cx - localCenter.X;
                float dy = cy - localCenter.Y;
                float dSq = dx * dx + dy * dy;
                if (dSq <= rSq && dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    bestLocal = new Vector2(cx, cy);
                    found = true;
                }
            }
        }

        if (found)
        {
            closestVoxelWorldCenter = FastLocalToWorld(bestLocal);
        }
        return found;
    }

    public bool ApplyKineticHitAtWorldPos(Vector2 worldHitPos, float kineticDamage, out VoxelType brokenType, out Vector2 brokenWorldCenter)
    {
        brokenType = VoxelType.Empty;
        brokenWorldCenter = worldHitPos;
        if (_isDestroyed || IsInSeedCycle || GridSide == 0 || _remainingSolidVoxelCount <= 0) return false;

        Vector2 localPt = FastWorldToLocal(worldHitPos);
        Vector2I centerGrid = LocalToGrid(localPt);

        int bestGx = -1;
        int bestGy = -1;
        float bestDistSq = float.MaxValue;

        for (int dy = -1; dy <= 1; dy++)
        {
            int gy = centerGrid.Y + dy;
            if (gy < 0 || gy >= GridSide) continue;
            int rowOffset = gy * GridSide;
            for (int dx = -1; dx <= 1; dx++)
            {
                int gx = centerGrid.X + dx;
                if (gx < 0 || gx >= GridSide) continue;
                if (_voxels[rowOffset + gx] == VoxelType.Empty) continue;

                Vector2 cellCenter = GridToLocalCenter(gx, gy);
                float dSq = cellCenter.DistanceSquaredTo(localPt);
                if (dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    bestGx = gx;
                    bestGy = gy;
                }
            }
        }

        if (bestGx == -1) return false;

        if (IsEnergyAsteroid)
        {
            _energyLaserHitAngle = localPt.Angle();
            _energyLaserCavitation = Mathf.Min(1.0f, _energyLaserCavitation + 0.25f);
            _energyAccumulatedHeat = Mathf.Min(1.0f, _energyAccumulatedHeat + 0.02f);
            _energyWasHitThisFrame = true;

            if (_energyAccumulatedHeat >= 1.0f && !_isDestroying)
            {
                StartDestructionSequence();
            }

            brokenType = VoxelType.ResourceAether;
            brokenWorldCenter = worldHitPos;
            QueueRedraw();
            return false;
        }

        int idx = bestGy * GridSide + bestGx;
        _voxelKineticDamage[idx] += kineticDamage;
        float ratio = Mathf.Clamp(_voxelKineticDamage[idx] / NonLaserTileMaxHp, 0.0f, 1.0f);

        if (_voxelHeat[idx] <= 0.0f && !_activeHeatedIndices.Contains(idx))
        {
            _activeHeatedIndices.Add(idx);
        }
        _heatedThisFrame.Add(idx);
        _voxelHeat[idx] = MathF.Max(_voxelHeat[idx], ratio);
        QueueRedraw();

        if (_voxelKineticDamage[idx] >= NonLaserTileMaxHp)
        {
            brokenType = _voxels[idx];
            brokenWorldCenter = FastLocalToWorld(GridToLocalCenter(bestGx, bestGy));
            _voxels[idx] = VoxelType.Empty;
            _voxelHeat[idx] = 0.0f;
            _voxelKineticDamage[idx] = 0.0f;
            _activeHeatedIndices.Remove(idx);

            _remainingSolidVoxelCount = Math.Max(0, _remainingSolidVoxelCount - 1);
            if (TryGetResourceType(brokenType, out _))
            {
                _remainingResourceVoxelCount = Math.Max(0, _remainingResourceVoxelCount - 1);
            }
            _carvedVoxelCount++;
            if (IsEnergyAsteroid)
            {
                OnEnergyVoxelDestroyed(bestGx, bestGy);
            }
            RebuildRowSpans(bestGy);
            CheckDestructionThreshold();
            return true;
        }

        return false;
    }

    public int ApplyDrillGrindAtRadius(
        Vector2 worldCenter,
        float grindRadius,
        float kineticDamagePerSec,
        float dt,
        List<(Vector2 worldCenter, VoxelType type)>? destroyedVoxels = null)
    {
        if (_isDestroyed || IsInSeedCycle || GridSide == 0 || _remainingSolidVoxelCount <= 0) return 0;

        Vector2 localCenter = FastWorldToLocal(worldCenter);
        if (localCenter.Length() > OuterBoundRadius + grindRadius) return 0;

        if (IsEnergyAsteroid)
        {
            _energyLaserHitAngle = localCenter.Angle();
            _energyLaserCavitation = Mathf.Min(1.0f, _energyLaserCavitation + 0.35f);
            _energyAccumulatedHeat = Mathf.Min(1.0f, _energyAccumulatedHeat + dt / 15.0f);
            _energyWasHitThisFrame = true;

            if (_energyAccumulatedHeat >= 1.0f && !_isDestroying)
            {
                StartDestructionSequence();
            }
            return 1;
        }

        int minGx = Math.Clamp((int)MathF.Floor((localCenter.X - grindRadius - GridOriginLocal.X) * InvVoxelSize), 0, GridSide - 1);
        int maxGx = Math.Clamp((int)MathF.Floor((localCenter.X + grindRadius - GridOriginLocal.X) * InvVoxelSize), 0, GridSide - 1);
        int minGy = Math.Clamp((int)MathF.Floor((localCenter.Y - grindRadius - GridOriginLocal.Y) * InvVoxelSize), 0, GridSide - 1);
        int maxGy = Math.Clamp((int)MathF.Floor((localCenter.Y + grindRadius - GridOriginLocal.Y) * InvVoxelSize), 0, GridSide - 1);

        float rSq = grindRadius * grindRadius;
        int brokenNow = 0;
        int minModifiedGy = GridSide;
        int maxModifiedGy = -1;
        bool anyHeated = false;

        for (int gy = minGy; gy <= maxGy; gy++)
        {
            float cy = GridOriginLocal.Y + (gy + 0.5f) * VoxelSize;
            int rowOffset = gy * GridSide;

            for (int gx = minGx; gx <= maxGx; gx++)
            {
                int idx = rowOffset + gx;
                VoxelType currentType = _voxels[idx];
                if (currentType == VoxelType.Empty) continue;

                float cx = GridOriginLocal.X + (gx + 0.5f) * VoxelSize;
                float dx = cx - localCenter.X;
                float dy = cy - localCenter.Y;
                float dSq = dx * dx + dy * dy;

                if (dSq <= rSq)
                {
                    float dist = MathF.Sqrt(dSq);
                    float falloff = MathF.Max(0.20f, 1.0f - (dist / MathF.Max(1.0f, grindRadius)) * 0.75f);
                    float dmgDelta = kineticDamagePerSec * falloff * dt;

                    _voxelKineticDamage[idx] += dmgDelta;
                    float heatRatio = Mathf.Clamp(_voxelKineticDamage[idx] / NonLaserTileMaxHp, 0.0f, 1.0f);

                    if (_voxelHeat[idx] <= 0.0f && !_activeHeatedIndices.Contains(idx))
                    {
                        _activeHeatedIndices.Add(idx);
                    }
                    _heatedThisFrame.Add(idx);
                    _voxelHeat[idx] = MathF.Max(_voxelHeat[idx], heatRatio);
                    anyHeated = true;

                    if (_voxelKineticDamage[idx] >= NonLaserTileMaxHp)
                    {
                        destroyedVoxels?.Add((FastLocalToWorld(new Vector2(cx, cy)), currentType));
                        _voxels[idx] = VoxelType.Empty;
                        _voxelHeat[idx] = 0.0f;
                        _voxelKineticDamage[idx] = 0.0f;
                        _activeHeatedIndices.Remove(idx);

                        _remainingSolidVoxelCount = Math.Max(0, _remainingSolidVoxelCount - 1);
                        if (TryGetResourceType(currentType, out _))
                        {
                            _remainingResourceVoxelCount = Math.Max(0, _remainingResourceVoxelCount - 1);
                        }
                        _carvedVoxelCount++;
                        brokenNow++;

                        if (IsEnergyAsteroid)
                        {
                            OnEnergyVoxelDestroyed(gx, gy);
                        }

                        if (gy < minModifiedGy) minModifiedGy = gy;
                        if (gy > maxModifiedGy) maxModifiedGy = gy;
                    }
                }
            }
        }

        if (brokenNow > 0)
        {
            int startRow = Math.Max(0, minModifiedGy - 1);
            int endRow = Math.Min(GridSide - 1, maxModifiedGy + 1);
            for (int gy = startRow; gy <= endRow; gy++)
            {
                RebuildRowSpans(gy);
            }
            QueueRedraw();
            CheckDestructionThreshold();
        }
        else if (anyHeated)
        {
            QueueRedraw();
        }

        return brokenNow;
    }

    // =========================================================================
    // B. KOLISI FISIKA TEROWONGAN (ZERO-INTEROP CIRCLE-TO-GRID COLLISION)
    // =========================================================================

    public bool ResolveCircleVoxelCollision(
        ref Vector2 worldPos,
        ref Vector2 worldVel,
        float radius,
        float impulseWeight = 0.0f)
    {
        return ResolveCircleVoxelCollision(ref worldPos, ref worldVel, radius, out _, impulseWeight);
    }

    public bool ResolveCircleVoxelCollision(
        ref Vector2 worldPos,
        ref Vector2 worldVel,
        float radius,
        out Vector2 hitNormal,
        float impulseWeight = 0.0f)
    {
        hitNormal = Vector2.Zero;
        if (_isDestroyed || IsInSeedCycle || GridSide == 0 || _remainingSolidVoxelCount <= 0) return false;

        float maxBound = OuterBoundRadius + radius;
        float diffX = worldPos.X - CachedPosition.X;
        float diffY = worldPos.Y - CachedPosition.Y;
        if (MathF.Abs(diffX) > maxBound || MathF.Abs(diffY) > maxBound) return false;
        if (diffX * diffX + diffY * diffY > maxBound * maxBound) return false;

        Vector2 localPos = new Vector2(diffX * CachedCos + diffY * CachedSin, -diffX * CachedSin + diffY * CachedCos);

        int minGx = Math.Clamp((int)MathF.Floor((localPos.X - radius - GridOriginLocal.X) * InvVoxelSize), 0, GridSide - 1);
        int maxGx = Math.Clamp((int)MathF.Floor((localPos.X + radius - GridOriginLocal.X) * InvVoxelSize), 0, GridSide - 1);
        int minGy = Math.Clamp((int)MathF.Floor((localPos.Y - radius - GridOriginLocal.Y) * InvVoxelSize), 0, GridSide - 1);
        int maxGy = Math.Clamp((int)MathF.Floor((localPos.Y + radius - GridOriginLocal.Y) * InvVoxelSize), 0, GridSide - 1);

        bool anyCollision = false;
        Vector2 accumLocalNormal = Vector2.Zero;
        float radiusSq = radius * radius;

        for (int pass = 0; pass < 2; pass++)
        {
            float deepestPenetration = 0.0f;
            Vector2 bestPushNormal = Vector2.Zero;

            for (int gy = minGy; gy <= maxGy; gy++)
            {
                int rowOffset = gy * GridSide;
                float cellMinY = GridOriginLocal.Y + gy * VoxelSize;
                float cellMaxY = cellMinY + VoxelSize;

                for (int gx = minGx; gx <= maxGx; gx++)
                {
                    if (_voxels[rowOffset + gx] == VoxelType.Empty) continue;

                    float cellMinX = GridOriginLocal.X + gx * VoxelSize;
                    float cellMaxX = cellMinX + VoxelSize;

                    float closestX = Mathf.Clamp(localPos.X, cellMinX, cellMaxX);
                    float closestY = Mathf.Clamp(localPos.Y, cellMinY, cellMaxY);

                    float dx = localPos.X - closestX;
                    float dy = localPos.Y - closestY;
                    float distSq = dx * dx + dy * dy;

                    if (distSq < radiusSq)
                    {
                        float penetration;
                        Vector2 pushNormal;

                        if (distSq > 1e-5f)
                        {
                            float dist = MathF.Sqrt(distSq);
                            penetration = radius - dist;
                            pushNormal = new Vector2(dx / dist, dy / dist);
                        }
                        else
                        {
                            float leftPen = (localPos.X - cellMinX) + radius;
                            float rightPen = (cellMaxX - localPos.X) + radius;
                            float topPen = (localPos.Y - cellMinY) + radius;
                            float bottomPen = (cellMaxY - localPos.Y) + radius;

                            penetration = leftPen;
                            pushNormal = Vector2.Left;

                            if (rightPen < penetration) { penetration = rightPen; pushNormal = Vector2.Right; }
                            if (topPen < penetration) { penetration = topPen; pushNormal = Vector2.Up; }
                            if (bottomPen < penetration) { penetration = bottomPen; pushNormal = Vector2.Down; }
                        }

                        if (penetration > deepestPenetration)
                        {
                            deepestPenetration = penetration;
                            bestPushNormal = pushNormal;
                        }
                    }
                }
            }

            if (deepestPenetration > 0.0001f)
            {
                localPos += bestPushNormal * deepestPenetration;
                accumLocalNormal += bestPushNormal;
                anyCollision = true;

                minGx = Math.Clamp((int)MathF.Floor((localPos.X - radius - GridOriginLocal.X) * InvVoxelSize), 0, GridSide - 1);
                maxGx = Math.Clamp((int)MathF.Floor((localPos.X + radius - GridOriginLocal.X) * InvVoxelSize), 0, GridSide - 1);
                minGy = Math.Clamp((int)MathF.Floor((localPos.Y - radius - GridOriginLocal.Y) * InvVoxelSize), 0, GridSide - 1);
                maxGy = Math.Clamp((int)MathF.Floor((localPos.Y + radius - GridOriginLocal.Y) * InvVoxelSize), 0, GridSide - 1);
            }
            else
            {
                break;
            }
        }

        if (!anyCollision) return false;

        worldPos = FastLocalToWorld(localPos);

        Vector2 worldNormal = accumLocalNormal.LengthSquared() > 1e-5f
            ? FastRotateToWorld(accumLocalNormal).Normalized()
            : (worldPos - CachedPosition).Normalized();

        Vector2 relVel = worldVel - CachedLinearVelocity;
        float inwardSpeed = relVel.Dot(worldNormal);
        if (inwardSpeed < 0.0f)
        {
            worldVel -= worldNormal * inwardSpeed;

            if (impulseWeight > 0.0f)
            {
                ApplyImpulse(-worldNormal * (-inwardSpeed * impulseWeight), worldPos - CachedPosition);
            }
        }

        hitNormal = worldNormal;
        return true;
    }

    public int CarveVoxelCircle(Vector2 worldCenter, float carveRadius, List<(Vector2 worldCenter, VoxelType type)>? destroyedVoxels = null)
    {
        if (_isDestroyed || IsInSeedCycle || GridSide == 0 || _remainingSolidVoxelCount <= 0) return 0;

        Vector2 localCenter = FastWorldToLocal(worldCenter);
        if (localCenter.Length() > OuterBoundRadius + carveRadius) return 0;

        int minGx = Math.Clamp((int)MathF.Floor((localCenter.X - carveRadius - GridOriginLocal.X) * InvVoxelSize), 0, GridSide - 1);
        int maxGx = Math.Clamp((int)MathF.Floor((localCenter.X + carveRadius - GridOriginLocal.X) * InvVoxelSize), 0, GridSide - 1);
        int minGy = Math.Clamp((int)MathF.Floor((localCenter.Y - carveRadius - GridOriginLocal.Y) * InvVoxelSize), 0, GridSide - 1);
        int maxGy = Math.Clamp((int)MathF.Floor((localCenter.Y + carveRadius - GridOriginLocal.Y) * InvVoxelSize), 0, GridSide - 1);

        float rSq = carveRadius * carveRadius;
        int carvedNow = 0;
        int minModifiedGy = GridSide;
        int maxModifiedGy = -1;

        for (int gy = minGy; gy <= maxGy; gy++)
        {
            float cy = GridOriginLocal.Y + (gy + 0.5f) * VoxelSize;
            int rowOffset = gy * GridSide;

            for (int gx = minGx; gx <= maxGx; gx++)
            {
                int idx = rowOffset + gx;
                VoxelType currentType = _voxels[idx];
                if (currentType == VoxelType.Empty) continue;

                float cx = GridOriginLocal.X + (gx + 0.5f) * VoxelSize;
                float dx = cx - localCenter.X;
                float dy = cy - localCenter.Y;

                if (dx * dx + dy * dy <= rSq)
                {
                    destroyedVoxels?.Add((FastLocalToWorld(new Vector2(cx, cy)), currentType));
                    _voxels[idx] = VoxelType.Empty;
                    _voxelHeat[idx] = 0.0f;
                    _voxelKineticDamage[idx] = 0.0f;
                    _activeHeatedIndices.Remove(idx);
                    _remainingSolidVoxelCount = Math.Max(0, _remainingSolidVoxelCount - 1);
                    if (TryGetResourceType(currentType, out _))
                    {
                        _remainingResourceVoxelCount = Math.Max(0, _remainingResourceVoxelCount - 1);
                    }
                    _carvedVoxelCount++;
                    carvedNow++;

                    if (IsEnergyAsteroid)
                    {
                        OnEnergyVoxelDestroyed(gx, gy);
                    }

                    if (gy < minModifiedGy) minModifiedGy = gy;
                    if (gy > maxModifiedGy) maxModifiedGy = gy;
                }
            }
        }

        if (carvedNow > 0)
        {
            int startRow = Math.Max(0, minModifiedGy - 1);
            int endRow = Math.Min(GridSide - 1, maxModifiedGy + 1);
            for (int gy = startRow; gy <= endRow; gy++)
            {
                RebuildRowSpans(gy);
            }
            QueueRedraw();
            CheckDestructionThreshold();
        }

        return carvedNow;
    }

    public void ApplyHitImpulse(Vector2 hitWorldPos, Vector2 impulse, float damage)
    {
        if (IsInSeedCycle) return;
        Vector2 offset = hitWorldPos - CachedPosition;
        ApplyImpulse(impulse, offset);
        CurrentHealth -= damage;

        if (CurrentHealth <= 0.0f)
        {
            DestroyAsteroid();
        }
    }

    public void ApplyDrillBreach(Vector2 drillPos, Vector2 drillVel, float force, float drillRadius = 36.0f)
    {
        if (IsInSeedCycle) return;

        if (IsEnergyAsteroid)
        {
            _energyWasHitThisFrame = true;
            _energyAccumulatedHeat = Mathf.Min(1.0f, _energyAccumulatedHeat + 0.04f);
            if (_energyAccumulatedHeat >= 1.0f && !_isDestroying)
            {
                StartDestructionSequence();
            }
            Vector2 push = drillVel.LengthSquared() > 1.0f ? drillVel.Normalized() : Vector2.Right;
            ApplyCentralImpulse(push * (force * 0.15f));
            return;
        }

        CarveVoxelCircle(drillPos, drillRadius);

        Vector2 pushDir = drillVel.LengthSquared() > 1.0f ? drillVel.Normalized() : Vector2.Right;
        ApplyCentralImpulse(pushDir * (force * 0.15f));

        float spinDir = GD.Randf() > 0.5f ? 1.0f : -1.0f;
        ApplyTorqueImpulse(spinDir * force * Radius * 0.05f);
    }

    /// <summary>
    /// Mengecek apakah seluruh mineral di dalam asteroid sudah habis ditambang (0 resource tersisa)
    /// atau struktur batu sudah runtuh.
    /// </summary>
    private void CheckDestructionThreshold()
    {
        if (IsInSeedCycle) return;

        bool shouldDestroy;
        if (IsEnergyAsteroid)
        {
            shouldDestroy = (_remainingCoreVoxels <= 0)
                || (_remainingSolidVoxelCount <= Math.Max(6, (int)(_initialSolidVoxelCount * 0.28f)));
        }
        else
        {
            // Asteroid biasa otomatis runtuh & meledak saat seluruh urat mineral di dalamnya habis (0 resource tersisa)
            bool allMineralsHarvested = (_initialResourceVoxelCount > 0 && _remainingResourceVoxelCount <= 0);
            bool structuralCollapse = (_remainingSolidVoxelCount <= Math.Max(6, (int)(_initialSolidVoxelCount * 0.22f)));
            shouldDestroy = allMineralsHarvested || structuralCollapse;
        }

        if (shouldDestroy)
        {
            StartDestructionSequence();
        }
    }

    public void DestroyAsteroid()
    {
        StartDestructionSequence();
    }

    public void StartDestructionSequence()
    {
        if (_isDestroyed || _isDestroying || IsInSeedCycle) return;
        _isDestroying = true;
        _currentDestructionDuration = IsEnergyAsteroid ? 0.54f : 0.76f;
        _destructionTimer = _currentDestructionDuration;

        // Nonaktifkan kolisi fisika agar kapal & armada swarm tidak terbentur saat animasi ledakan berlangsung
        if (_collisionShape != null)
        {
            _collisionShape.SetDeferred(CollisionShape2D.PropertyName.Disabled, true);
        }

        // Bentuk pola retakan jaring laba-laba katastropik (Radial + Concentric Web Rings) yang menyala melintasi batu
        _catastrophicFractureLines.Clear();
        int crackBranches = IsEnergyAsteroid ? 12 : 16;
        const int rings = 4;
        Vector2[,] webNodes = new Vector2[crackBranches, rings + 1];

        for (int b = 0; b < crackBranches; b++)
        {
            float baseAngle = (float)b / crackBranches * Mathf.Tau + (GD.Randf() - 0.5f) * 0.24f;
            Vector2 dir = new Vector2(MathF.Cos(baseAngle), MathF.Sin(baseAngle));
            Vector2 perp = new Vector2(-dir.Y, dir.X);

            webNodes[b, 0] = dir * (Radius * 0.06f);
            for (int s = 1; s <= rings; s++)
            {
                float t = (float)s / rings;
                float rDist = Radius * t * (float)GD.RandRange(0.84, 1.02);
                float jitter = (GD.Randf() - 0.5f) * (Radius * 0.08f);
                webNodes[b, s] = dir * rDist + perp * jitter;

                _catastrophicFractureLines.Add(webNodes[b, s - 1]);
                _catastrophicFractureLines.Add(webNodes[b, s]);
            }
        }

        // Hubungkan cabang-cabang radial dengan cincin jaring laba-laba konsentris
        for (int s = 1; s < rings; s++)
        {
            for (int b = 0; b < crackBranches; b++)
            {
                int nextB = (b + 1) % crackBranches;
                if (s == 2 || (b + s) % 2 == 0)
                {
                    _catastrophicFractureLines.Add(webNodes[b, s]);
                    _catastrophicFractureLines.Add(webNodes[nextB, s]);
                }
            }
        }

        QueueRedraw();
    }

    public void FinalizeDestruction()
    {
        if (_isDestroyed) return;

        EmitSignal(SignalName.AsteroidDestroyed, CachedPosition, Radius, ResourceGlowColor, IsEnergyAsteroid);

        // Panggil efek ledakan spektakuler di SwarmSimulation (Shockwave Ring + Flying Shards + Loot Ejection)
        SwarmSimulation.Instance?.TriggerAsteroidDestructionFx(CachedPosition, Radius, ResourceGlowColor, IsEnergyAsteroid);

        // JIKA INI ADALAH BINTANG AETHER: Jangan dihapus! Masuk ke Tahap 1 Siklus Reinkarnasi (Dormant Seed 10 Detik)
        if (IsEnergyAsteroid)
        {
            EnterDormantSeedStage();
            return;
        }

        // Asteroid batu biasa: hancur berkeping-keping menjadi puing kosmik dan membuka ruang kosong di arena!
        _isDestroyed = true;
        QueueFree();
    }

    private bool IsValidGridCoord(int gx, int gy)
    {
        return gx >= 0 && gx < GridSide && gy >= 0 && gy < GridSide;
    }

    private Vector2 GridToLocalCenter(int gx, int gy)
    {
        return new Vector2(
            GridOriginLocal.X + (gx + 0.5f) * VoxelSize,
            GridOriginLocal.Y + (gy + 0.5f) * VoxelSize
        );
    }

    private Vector2I LocalToGrid(Vector2 localPos)
    {
        int gx = (int)MathF.Floor((localPos.X - GridOriginLocal.X) * InvVoxelSize);
        int gy = (int)MathF.Floor((localPos.Y - GridOriginLocal.Y) * InvVoxelSize);
        return new Vector2I(gx, gy);
    }

    private static bool IsPointInPolygon(Vector2 point, Vector2[] polygon)
    {
        bool inside = false;
        int count = polygon.Length;
        for (int i = 0, j = count - 1; i < count; j = i++)
        {
            Vector2 pi = polygon[i];
            Vector2 pj = polygon[j];

            if (((pi.Y > point.Y) != (pj.Y > point.Y)) &&
                (point.X < (pj.X - pi.X) * (point.Y - pi.Y) / (pj.Y - pi.Y + 1e-7f) + pi.X))
            {
                inside = !inside;
            }
        }
        return inside;
    }
}
