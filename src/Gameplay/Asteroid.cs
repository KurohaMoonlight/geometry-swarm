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
/// Asteroid prosedural raksasa berbasis Grid Voxel 13 px:
/// - Asteroid batu memiliki jumlah deposit resource acak (2-8 kantung) dan mengandung 1-2 jenis resource berbeda.
/// - Setiap tile voxel memiliki HP masif terhadap benturan/bor kinetik non-laser (NonLaserTileMaxHp = 45.0f),
///   sehingga hanya Mining Laser yang dapat memotong lorong asteroid secara cepat dan efisien.
/// - Jika pengeboran/laser sudah sampai ke blok resource (atau membuka batu di sebelahnya menjadi Empty),
///   tepi blok resource 13 px yang terekspos otomatis menyala terang (HDR Neon Vein Edge Glow).
/// - Asteroid khusus Energy Crystal (IsEnergyAsteroid) memiliki bentuk geometri Bintang Kristal Runcing
///   (Star-Crystal 10-12 sudut tajam) yang terus berdenyut menyala-nyala sepanjang waktu.
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

    // 1 atau 2 jenis resource berbeda di dalam asteroid ini
    public ResourceType PrimaryResource { get; set; } = ResourceType.Ferrite;
    public ResourceType SecondaryResource { get; set; } = ResourceType.Quartz;
    public bool HasSecondaryResource { get; set; } = false;

    // Cache transformasi murni di C# (Zero P/Invoke overhead)
    public Vector2 CachedPosition { get; private set; }
    public float CachedRotation { get; private set; }
    public float CachedCos { get; private set; } = 1.0f;
    public float CachedSin { get; private set; } = 0.0f;
    public Vector2 CachedLinearVelocity { get; private set; }
    public bool IsActiveInTree => !_isDestroyed;

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
    private int _carvedVoxelCount = 0;
    private float _energyPulseTimer = 0.0f;
    private float _energyRedrawTimer = 0.0f;
    private readonly Vector2[] _reusableArcPts = new Vector2[4];
    private readonly Vector2[] _reusableDischPts = new Vector2[3];
    private CrystalSpireData[] _crystalSpires = Array.Empty<CrystalSpireData>();
    private float _crystalCoreRadius = 0.0f;
    private int _totalCoreVoxels = 0;
    private int _remainingCoreVoxels = 0;

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
    private const float DestructionDuration = 0.52f;
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

        if (IsEnergyAsteroid)
        {
            SetupEnergyStarVisual();
        }
    }

    private void SetupEnergyStarVisual()
    {
        try
        {
            var shader = GD.Load<Shader>("res://visual/mini_star_singularity.gdshader");
            if (shader == null) return;

            // Kotak visual dibuat cukup luas (4.2x radius) agar distorsi gravitasi pudar mulus tanpa garis kotak
            float boxSize = OuterBoundRadius * 4.2f;

            _energyBackBuffer = new BackBufferCopy
            {
                CopyMode = BackBufferCopy.CopyModeEnum.Rect,
                Rect = new Rect2(-boxSize * 0.5f, -boxSize * 0.5f, boxSize, boxSize)
            };
            AddChild(_energyBackBuffer);

            _energyStarMaterial = new ShaderMaterial { Shader = shader };
            _energyStarMaterial.SetShaderParameter("star_radius", 0.16f);
            _energyStarMaterial.SetShaderParameter("lensing_strength", 0.08f);
            _energyStarMaterial.SetShaderParameter("lensing_radius", 0.38f);
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

            // Asteroid Khusus Floating Crystal Geode: Inti Aether emas-putih, kristal cyan, dan obsidian transparan
            _unscannedBorderColor = new Color(4.5f, 3.8f, 1.4f, 0.98f);
            _tunnelWallBorderColor = new Color(1.2f, 3.4f, 4.6f, 0.95f);
            _rockBodyColor = new Color(0.025f, 0.05f, 0.10f, 0.95f);
        }
        else
        {
            // Tentukan Resource Pertama (Primary) dan peluang 65% memiliki Resource Kedua (Secondary) yang berbeda
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

            _unscannedBorderColor = new Color(0.18f, 0.16f, 0.22f, 0.85f);
            _tunnelWallBorderColor = new Color(0.24f, 0.20f, 0.30f, 0.78f);
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
            // BENTUK KHUSUS CLUSTER PRISMA KRISTAL VEKTOR (OPSI A):
            // 14-16 prisma kristal poligonal berfaset tajam (Diamond & Hexagonal Prisms)
            int spireCount = rng.RandiRange(14, 16);
            _crystalSpires = new CrystalSpireData[spireCount];
            // Inti singularitas Aether dibuat kompak & terlindung di dalam rongga geode pusat
            _crystalCoreRadius = Radius * rng.RandfRange(0.14f, 0.18f);

            // Hanya pilih 2 sampai 3 pilar yang memiliki urat kristal Optic Quartz
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
    }

    /// <summary>
    /// Membangkitkan kantung-kantung resource dengan jumlah ACAK dan 1-2 jenis resource berbeda.
    /// </summary>
    private void GenerateResourceVoxelPatches()
    {
        var rng = new RandomNumberGenerator();
        long seedVal = ((long)CachedPosition.X * 19349663L) ^ ((long)CachedPosition.Y * 73856093L);
        rng.Seed = (ulong)Math.Abs(seedVal);

        int centerCell = GridSide / 2;

        if (IsEnergyAsteroid)
        {
            // BENTUK VOXEL PRISMA KRISTAL VEKTOR (OPSI A):
            // Spires sebagian besar adalah BATU OBSIDIAN GELAP (VoxelType.Rock).
            // Hanya Inti Singularitas Tengah yang mengandung Aether murni, dan hanya 2-3 urat pilar yang mengandung Optic Quartz!
            int totalSolid = 0;
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

                        // Hanya bagian terdalam inti yang merupakan Aether murni (~14-18 tile saja)
                        if (distSq <= coreAetherRadiusSq)
                        {
                            _voxels[idx] = VoxelType.ResourceAether;
                            _totalCoreVoxels++;
                            _remainingCoreVoxels++;
                        }
                        else
                        {
                            _voxels[idx] = VoxelType.Rock;
                        }
                        continue;
                    }

                    // 2. Cek apakah sel berada di dalam salah satu pilar kristal
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

                            // Hanya pilar yang memiliki urat mineral, dan HANYA di simpul kristal rMid, yang menjadi Optic Quartz!
                            if (spire.HasResourceVein)
                            {
                                float dist = MathF.Sqrt(distSq);
                                if (dist >= spire.RadiusMid * 0.80f && dist <= spire.RadiusMid * 1.20f)
                                {
                                    _voxels[idx] = VoxelType.ResourceQuartz;
                                }
                                else
                                {
                                    _voxels[idx] = VoxelType.Rock;
                                }
                            }
                            else
                            {
                                // Pilar kristal obsidian padat biasa (Batu)
                                _voxels[idx] = VoxelType.Rock;
                            }
                            break;
                        }
                    }

                    if (!inSpire)
                    {
                        // Celah hampa udara murni di antara pilar kristal
                        _voxels[idx] = VoxelType.Empty;
                        _initialSolidMask[idx] = false;
                    }
                }
            }

            _initialSolidVoxelCount = totalSolid;
            _remainingSolidVoxelCount = totalSolid;
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
            // Jika memiliki 2 resource berbeda, separuh kantung menggunakan SecondaryResource
            VoxelType chosenVox = (HasSecondaryResource && (p % 2 == 1)) ? secondaryVox : primaryVox;
            PlaceResourceCluster(pocketGrid, clusterRadius, chosenVox, rng);
        }
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

                // Cari sel voxel padat terluar yang benar-benar menopang segmen garis tepi ini
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

            // Fungsi lokal: sel Empty hanya dianggap dinding lorong jika aslinya berada di dalam asteroid!
            // Ruang angkasa hampa di luar asteroid tidak akan digambar garis dinding kisi-kisi (tepian luar tetap mulus poligon!)
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

                    // Garis dinding galian interior di dalam asteroid
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

                    // Tambahan: jika blok Resource sudah tersentuh lorong galian di salah satu sisinya,
                    // nyalakan juga seluruh bingkai luar klaster resource di sekitar titik kontak!
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

                // Jika voxel penopang pinggir atau tetangga langsungnya sudah terkikis (IsCarvedEmpty),
                // buka garis kontur luar agar coakan dari pinggir langsung terlihat terbuka!
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

            // Animasi Retakan Overload & Getaran Katastropik saat Asteroid Sedang Hancur
            if (_isDestroying)
            {
                _destructionTimer -= dt;
                float progress = 1.0f - Mathf.Clamp(_destructionTimer / DestructionDuration, 0.0f, 1.0f);
                float shakeMag = Mathf.Lerp(2.5f, 14.0f, progress * progress);
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

            // Perlindungan Batas Sektor (12.000 x 12.000 px) & Area Kapal Induk Pemain (Pusat 0,0):
            // Jika asteroid mendekati dinding batas luar, belokkan kembali ke arah dalam secara halus
            float maxBoundary = 5700.0f - OuterBoundRadius;
            Vector2 pos = GlobalPosition;
            if (MathF.Abs(pos.X) > maxBoundary || MathF.Abs(pos.Y) > maxBoundary)
            {
                Vector2 toCenter = (-pos).Normalized();
                LinearVelocity = LinearVelocity.Lerp(toCenter * 8.0f, dt * 1.5f);
            }
            // Jika mendekati area awal kapal induk di (0,0), dorong perlahan menjauh agar base tetap lapang
            float distToPlayerSpawn = pos.Length();
            if (distToPlayerSpawn < 950.0f)
            {
                Vector2 awayFromCenter = pos.LengthSquared() > 1.0f ? pos.Normalized() : Vector2.Right;
                LinearVelocity = LinearVelocity.Lerp(awayFromCenter * 10.0f, dt * 2.0f);
            }

            // Asteroid Energy: Update Visual Shader Bintang Mini & Gravitational Lensing
            if (IsEnergyAsteroid)
            {
                _energyPulseTimer += dt * 3.2f;

                if (_energyStarMaterial != null)
                {
                    // 1. Susutkan radius bintang seiring menipisnya cadangan energi Aether di pusat (0.16 -> 0.04)
                    float remainingRatio = _totalCoreVoxels > 0
                        ? (float)_remainingCoreVoxels / _totalCoreVoxels
                        : (float)_remainingSolidVoxelCount / Math.Max(1, _initialSolidVoxelCount);

                    float normalizedRadius = Mathf.Lerp(0.04f, 0.16f, remainingRatio);
                    _energyStarMaterial.SetShaderParameter("star_radius", normalizedRadius);

                    // 2. Pendinginan termal jika laser/drill tidak mengenai bintang di frame ini
                    if (!_energyWasHitThisFrame)
                    {
                        _energyAccumulatedHeat = Mathf.MoveToward(_energyAccumulatedHeat, 0.0f, dt * 0.10f);
                        _energyLaserCavitation = Mathf.MoveToward(_energyLaserCavitation, 0.0f, dt * 2.8f);
                    }
                    _energyWasHitThisFrame = false;

                    // 3. Efek instabilitas getaran & distorsi (saat overheating atau sedang meledak)
                    float currentInstability = _isDestroying
                        ? 1.0f
                        : Mathf.Clamp(_energyAccumulatedHeat, 0.0f, 1.0f);

                    _energyStarMaterial.SetShaderParameter("instability", currentInstability);
                    _energyStarMaterial.SetShaderParameter("hit_angle", _energyLaserHitAngle);
                    _energyStarMaterial.SetShaderParameter("hit_cavitation", _energyLaserCavitation);

                    // 4. MEDAN GRAVITASI MAUT (Gravitational Hazard & Event Horizon Pull)
                    float gravityRadius = Radius * 4.5f;
                    float eventHorizonRadius = Radius * 0.42f;

                    // Tarik Kapal Induk Pemain
                    if (PlayerCore.Instance != null && IsInstanceValid(PlayerCore.Instance))
                    {
                        float playerDist = CachedPosition.DistanceTo(PlayerCore.Instance.GlobalPosition);
                        if (playerDist < gravityRadius)
                        {
                            Vector2 pullDir = (CachedPosition - PlayerCore.Instance.GlobalPosition).Normalized();
                            float pullRatio = 1.0f - (playerDist / gravityRadius);
                            float pullForce = Mathf.Lerp(60.0f, 520.0f, pullRatio * pullRatio);
                            PlayerCore.Instance.Velocity += pullDir * (pullForce * dt);

                            // EVENT HORIZON: Jika tersedot sampai ke pusat bintang -> INSTANT DEATH!
                            if (playerDist < eventHorizonRadius)
                            {
                                PlayerCore.Instance.TakeDamage(99999.0f, -pullDir);
                            }
                        }
                    }

                    // Tarik & Telan Asteroid Lain yang Lewat
                    for (int a = 0; a < ActiveAsteroids.Count; a++)
                    {
                        var other = ActiveAsteroids[a];
                        if (other == this || other._isDestroyed || !IsInstanceValid(other)) continue;
                        float aDist = CachedPosition.DistanceTo(other.CachedPosition);
                        if (aDist < gravityRadius + other.OuterBoundRadius)
                        {
                            Vector2 toStar = (CachedPosition - other.CachedPosition).Normalized();
                            float f = Mathf.Lerp(40.0f, 320.0f, 1.0f - (aDist / (gravityRadius + other.OuterBoundRadius)));
                            other.LinearVelocity += toStar * (f * dt);

                            // Jika asteroid lain terseret masuk ke inti bintang -> hancur & lenyap ditelan singularitas
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

                // Viewport distance culling: hanya redraw jika berada di dalam jangkauan pandang kamera
                Vector2 playerPos = PlayerCore.Instance?.GlobalPosition ?? Vector2.Zero;
                float distSq = GlobalPosition.DistanceSquaredTo(playerPos);
                float cullDist = OuterBoundRadius + 2200.0f;

                if (distSq <= cullDist * cullDist)
                {
                    // Throttle redraw efek denyut energi ke ~30 FPS untuk menghemat canvas draw call
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

    public override void _Draw()
    {
        try
        {
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

            // 2. Gambar Patch Resource (Terkalibrasi Spektrometri Scanner: X-Ray Baru di 60+ Scanner!)
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

            // 4. TEPI BATU RESOURCE MENYALA SAAT DRILLING SUDAH SAMPAI RESOURCE!
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
            // Garis tepi terluar asteroid batu 100% permanen warna batu gelap angkasa.
            // Scanner tidak membuat asteroid menyala seperti bohlam, hanya mendeteksi mineral di dalam!
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

            // 7. Render Efek Animasi Retakan Overload saat Asteroid Sedang Hancur
            if (_isDestroying && _catastrophicFractureLines.Count >= 2)
            {
                float progress = 1.0f - Mathf.Clamp(_destructionTimer / DestructionDuration, 0.0f, 1.0f);
                float flash = 0.55f + 0.45f * MathF.Sin(_destructionTimer * 48.0f);
                Color fractureColor = new Color(3.8f, 3.4f, 2.2f, 0.90f * flash);
                DrawMultiline(_catastrophicFractureLines.ToArray(), fractureColor, 2.8f + progress * 2.0f, false);
                DrawCircle(Vector2.Zero, Radius * progress * 0.65f, new Color(fractureColor.R, fractureColor.G, fractureColor.B, 0.28f * (1.0f - progress)));
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[Asteroid._Draw] {ex}");
        }
    }

    private void DrawEnergyCrystalSpireCluster()
    {
        // 1. Gambar Inti Plasma Aether Singularity Lembut & Dinamis Mengikuti Erosi Core
        float coreRatio = _totalCoreVoxels > 0
            ? Mathf.Clamp((float)_remainingCoreVoxels / _totalCoreVoxels, 0.0f, 1.0f)
            : 1.0f;

        if (coreRatio > 0.02f)
        {
            float pulse = 0.90f + 0.10f * MathF.Sin(_energyPulseTimer * 2.8f);
            float coreR = _crystalCoreRadius * coreRatio * pulse;

            // Pendaran terluar
            DrawCircle(Vector2.Zero, coreR * 1.35f, new Color(1.8f, 1.1f, 0.25f, 0.16f * coreRatio));
            // Korona hangat
            DrawCircle(Vector2.Zero, coreR * 1.05f, new Color(2.4f, 1.8f, 0.55f, 0.35f * coreRatio));
            // Bola plasma inti
            DrawCircle(Vector2.Zero, coreR * 0.72f, new Color(2.8f, 2.4f, 1.2f, 0.78f * coreRatio));
            // Bintang fokus pusat
            DrawCircle(Vector2.Zero, coreR * 0.35f, new Color(3.5f, 3.2f, 2.2f, 0.95f * coreRatio));

            // 2. Busur Listrik Mikro Harmonik Menuju Spires
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

        // 3. Render Pilar-Pilar Prisma Kristal Poligonal Berfaset 3D (Opsi A) dengan Erosi Progresif
        Color heatGlowColor = new Color(4.8f, 4.2f, 2.2f, 0.95f);

        for (int i = 0; i < _crystalSpires.Length; i++)
        {
            ref var spire = ref _crystalSpires[i];
            if (spire.TotalVoxels > 0 && spire.RemainingVoxels <= 0) continue; // Hancur tertambang!

            float healthRatio = spire.TotalVoxels > 0
                ? Mathf.Clamp((float)spire.RemainingVoxels / spire.TotalVoxels, 0.0f, 1.0f)
                : 1.0f;

            if (healthRatio <= 0.01f) continue;

            bool isHeated = spire.Heat > 0.01f;
            float vibe = isHeated ? MathF.Sin(_energyPulseTimer * 48.0f + i * 4.3f) * (spire.Heat * 2.6f) : 0.0f;

            // EROSI PROGRESIF: Ujung (tip), bahu kristal (midLeft, midRight), dan ridge surut menuju baseCenter
            float effectiveRatio = MathF.Max(0.06f, healthRatio);
            Vector2 curTip = spire.BaseCenter.Lerp(spire.Tip, effectiveRatio) + spire.Perp * vibe;
            Vector2 curMidLeft = spire.BaseLeft.Lerp(spire.MidLeft, effectiveRatio);
            Vector2 curMidRight = spire.BaseRight.Lerp(spire.MidRight, effectiveRatio);
            Vector2 curRidge = spire.BaseCenter.Lerp(spire.RidgeCenter, effectiveRatio);

            Vector2[] leftFacet = new Vector2[] { spire.BaseLeft, curMidLeft, curTip, curRidge, spire.BaseCenter };
            Vector2[] rightFacet = new Vector2[] { spire.BaseCenter, curRidge, curTip, curMidRight, spire.BaseRight };
            Vector2[] outline = new Vector2[] { spire.BaseLeft, curMidLeft, curTip, curMidRight, spire.BaseRight };
            Vector2[] ridge = new Vector2[] { spire.BaseCenter, curRidge, curTip };

            // Spires sebagian besar adalah BATU OBSIDIAN GELAP ASTRAL (Vitreous Obsidian Crystal Matrix)
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

            // Jika pilar ini menyimpan urat kristal Optic Quartz, gambar urat kristal bercahaya sian di sepanjang punggungnya!
            if (spire.HasResourceVein)
            {
                Color veinCol = new Color(0.8f, 3.4f, 4.6f, 0.95f * healthRatio);
                DrawLine(curRidge, curTip, veinCol, 2.2f);
                DrawCircle(curRidge, 3.2f, new Color(1.2f, 3.8f, 5.0f, 0.90f * healthRatio));
            }

            // Garis retakan fraktur saat kristal mulai tergerus (healthRatio < 0.80)
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

        // 4. Render Efek Animasi Retakan Overload saat Asteroid Sedang Hancur
        if (_isDestroying && _catastrophicFractureLines.Count >= 2)
        {
            float progress = 1.0f - Mathf.Clamp(_destructionTimer / DestructionDuration, 0.0f, 1.0f);
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
        if (_isDestroyed || GridSide == 0 || _remainingSolidVoxelCount <= 0) return false;

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
        float tEnter = MathF.Max(0.0f, proj - halfChord - VoxelSize);
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

        if (_isDestroyed || !IsValidGridCoord(gx, gy)) return false;

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

        if (IsEnergyAsteroid)
        {
            RegisterEnergySpireHeat(gx, gy, heatDelta);
            Vector2 localHit = worldCellCenter - CachedPosition;
            _energyLaserHitAngle = localHit.Angle();
            _energyLaserCavitation = Mathf.Min(1.0f, _energyLaserCavitation + heatDelta * 4.0f);
            // Akumulasi panas fusi diperlambat: butuh ~5-7 detik tembakan laser terus menerus untuk memicu Supernova
            _energyAccumulatedHeat = Mathf.Min(1.0f, _energyAccumulatedHeat + heatDelta * 0.040f);
            _energyWasHitThisFrame = true;

            // Jika laser terus menerus membakar bintang mini hingga instabilitas mencapai 1.0,
            // memicu reaksi fusi kritis gravitasi runtuh -> SUPERNOVA DETONATION!
            if (_energyAccumulatedHeat >= 1.0f && !_isDestroying)
            {
                StartDestructionSequence();
            }
        }

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

    /// <summary>
    /// Mengecek apakah terdapat voxel padat (bukan Empty) di dalam radius tertentu dari titik dunia.
    /// Digunakan agar Super Giga Drill hanya berinteraksi dengan batu aktual, bukan ruang kosong.
    /// </summary>
    public bool HasSolidVoxelsInRadius(Vector2 worldCenter, float checkRadius, out Vector2 closestVoxelWorldCenter)
    {
        closestVoxelWorldCenter = worldCenter;
        if (_isDestroyed || GridSide == 0 || _remainingSolidVoxelCount <= 0) return false;

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

    /// <summary>
    /// Memberikan damage benturan kinetik tunggal pada 1 tile voxel terdekat (misal saat drone tunggal menabrak batu).
    /// Karena tiap tile memiliki NonLaserTileMaxHp = 45.0f, satu drone hanya menggores tipis (~1/45 HP tile).
    /// </summary>
    public bool ApplyKineticHitAtWorldPos(Vector2 worldHitPos, float kineticDamage, out VoxelType brokenType, out Vector2 brokenWorldCenter)
    {
        brokenType = VoxelType.Empty;
        brokenWorldCenter = worldHitPos;
        if (_isDestroyed || GridSide == 0 || _remainingSolidVoxelCount <= 0) return false;

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
            _energyAccumulatedHeat = Mathf.Min(1.0f, _energyAccumulatedHeat + 0.05f);
            _energyWasHitThisFrame = true;
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

    /// <summary>
    /// Menggerus tile asteroid secara bertahap dengan Super Giga Drill melawan HP tile masif (NonLaserTileMaxHp = 45.0f).
    /// Tidak langsung menghancurkan tile dalam 1 frame, melainkan memanaskan dan mengikis perlahan
    /// sementara mata bor terus berputar dan aus.
    /// </summary>
    public int ApplyDrillGrindAtRadius(
        Vector2 worldCenter,
        float grindRadius,
        float kineticDamagePerSec,
        float dt,
        List<(Vector2 worldCenter, VoxelType type)>? destroyedVoxels = null)
    {
        if (_isDestroyed || GridSide == 0 || _remainingSolidVoxelCount <= 0) return 0;

        Vector2 localCenter = FastWorldToLocal(worldCenter);
        if (localCenter.Length() > OuterBoundRadius + grindRadius) return 0;

        if (IsEnergyAsteroid)
        {
            _energyLaserHitAngle = localCenter.Angle();
            _energyLaserCavitation = Mathf.Min(1.0f, _energyLaserCavitation + 0.35f);
            _energyAccumulatedHeat = Mathf.Min(1.0f, _energyAccumulatedHeat + dt * 0.4f);
            _energyWasHitThisFrame = true;
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
        if (_isDestroyed || GridSide == 0 || _remainingSolidVoxelCount <= 0) return false;

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
        if (_isDestroyed || GridSide == 0 || _remainingSolidVoxelCount <= 0) return 0;

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
        CarveVoxelCircle(drillPos, drillRadius);

        Vector2 pushDir = drillVel.LengthSquared() > 1.0f ? drillVel.Normalized() : Vector2.Right;
        ApplyCentralImpulse(pushDir * (force * 0.15f));

        float spinDir = GD.Randf() > 0.5f ? 1.0f : -1.0f;
        ApplyTorqueImpulse(spinDir * force * Radius * 0.05f);
    }

    private void CheckDestructionThreshold()
    {
        bool shouldDestroy = IsEnergyAsteroid
            ? (_remainingCoreVoxels <= 0 || _remainingSolidVoxelCount <= Math.Max(6, (int)(_initialSolidVoxelCount * 0.28f)))
            : (_remainingSolidVoxelCount <= Math.Max(6, (int)(_initialSolidVoxelCount * 0.22f)));

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
        if (_isDestroyed || _isDestroying) return;
        _isDestroying = true;
        _destructionTimer = DestructionDuration;

        // Nonaktifkan kolisi fisika agar kapal & armada swarm tidak terbentur saat animasi ledakan berlangsung
        if (_collisionShape != null)
        {
            _collisionShape.SetDeferred(CollisionShape2D.PropertyName.Disabled, true);
        }

        // Bentuk pola retakan fraktur katastropik yang menyilang dari pusat ke tepi
        _catastrophicFractureLines.Clear();
        int crackBranches = IsEnergyAsteroid ? 12 : 16;
        for (int b = 0; b < crackBranches; b++)
        {
            float angle = (float)b / crackBranches * Mathf.Tau + (GD.Randf() - 0.5f) * 0.35f;
            Vector2 dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            Vector2 cur = dir * (Radius * 0.08f);

            int segs = GD.RandRange(3, 5);
            for (int s = 1; s <= segs; s++)
            {
                float t = (float)s / segs;
                Vector2 next = dir * (Radius * t * (float)GD.RandRange(0.85f, 1.05f)) + new Vector2(-dir.Y, dir.X) * ((GD.Randf() - 0.5f) * 22.0f);
                _catastrophicFractureLines.Add(cur);
                _catastrophicFractureLines.Add(next);
                cur = next;
            }
        }

        QueueRedraw();
    }

    public void FinalizeDestruction()
    {
        if (_isDestroyed) return;
        _isDestroyed = true;

        EmitSignal(SignalName.AsteroidDestroyed, CachedPosition, Radius, ResourceGlowColor, IsEnergyAsteroid);

        // Panggil efek ledakan spektakuler di SwarmSimulation (Shockwave Ring + Flying Shards + Loot Ejection)
        SwarmSimulation.Instance?.TriggerAsteroidDestructionFx(CachedPosition, Radius, ResourceGlowColor, IsEnergyAsteroid);

        // Jika asteroid batu berukuran besar (Radius > 350 px), pecah menjadi 2 anak asteroid lebih kecil
        if (!IsEnergyAsteroid && Radius > 350.0f && GetParent() != null)
        {
            int fragmentCount = 2;
            float subRadius = Radius * 0.44f;

            for (int i = 0; i < fragmentCount; i++)
            {
                float angle = ((float)i / fragmentCount * Mathf.Tau) + (GD.Randf() - 0.5f) * 0.6f;
                Vector2 offset = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (Radius * 0.45f);

                var frag = new Asteroid
                {
                    GlobalPosition = CachedPosition + offset,
                    Radius = subRadius,
                    MaxHealth = subRadius * 4.0f,
                    AsteroidType = AsteroidType,
                    IsEnergyAsteroid = false
                };

                float blastSpeed = (float)GD.RandRange(70.0, 160.0);
                frag.LinearVelocity = CachedLinearVelocity + offset.Normalized() * blastSpeed;
                frag.AngularVelocity = (GD.Randf() - 0.5f) * 1.5f;

                GetParent().CallDeferred("add_child", frag);
            }
        }

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
