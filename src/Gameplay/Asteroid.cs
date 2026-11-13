using System;
using System.Collections.Generic;
using GeometrySwarm.Core.ECS.Components;
using Godot;

namespace GeometrySwarm.Gameplay;

/// <summary>
/// Asteroid prosedural raksasa berbasis Grid Voxel 13 px (Zero-Lag Voxel Collision & Raymarching Erosion).
/// - Setiap sel berukuran 13 x 13 px memiliki status: Empty (Kosong), Rock (Batu), atau Resource (Kristal).
/// - Mendukung Raymarching presisi (DDA Grid Traversal): sinar laser berhenti tepat di blok solid 13 px
///   pertama yang dihadang, memanaskannya secara bertahap (oranye panas -> kuning pijar), lalu mengubahnya
///   menjadi Empty sehingga di frame berikutnya laser otomatis maju 1 langkah lebih dalam (membentuk terowongan).
/// - Menggunakan kolisi Circle-to-Grid tanpa merekonstruksi poligon fisika Godot setiap kali sel 13 px hancur:
///   Kapal Induk Core dan Swarm bergerak bebas di sel Empty (terowongan) dan otomatis tertahan serta meluncur
///   (slide) di sepanjang dinding sel Rock atau Resource.
/// </summary>
public partial class Asteroid : RigidBody2D
{
    [Signal]
    public delegate void AsteroidDestroyedEventHandler(Vector2 position, float radius, Color resourceColor, bool isEnergy);

    public static readonly List<Asteroid> ActiveAsteroids = new();

    // Ukuran baku 1 blok voxel asteroid = 13 px
    public const float VoxelSize = 13.0f;
    public const float InvVoxelSize = 1.0f / VoxelSize;

    public enum VoxelType : byte
    {
        Empty = 0,
        Rock = 1,
        Resource = 2
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

    private struct WallEdge
    {
        public Vector2 A;
        public Vector2 B;

        public WallEdge(Vector2 a, Vector2 b)
        {
            A = a;
            B = b;
        }
    }

    private struct OutlineSegment
    {
        public Vector2 A;
        public Vector2 B;
        public int CheckGridX;
        public int CheckGridY;
    }

    // Jumlah Scanner Drone yang dimiliki armada pemain saat ini
    public static int CurrentScannerCount { get; set; } = 5;

    [Export] public float Radius { get; set; } = 450.0f;
    [Export] public float MaxHealth { get; set; } = 2500.0f;
    [Export] public float CurrentHealth { get; set; } = 2500.0f;
    [Export] public SwarmRole AsteroidType { get; set; } = SwarmRole.DartNeedle;
    [Export] public bool IsEnergyAsteroid { get; set; } = false;
    [Export] public bool IsScanned { get; set; } = false;

    // Palet warna neon inti asteroid sesuai tipe swarm
    public Color ResourceGlowColor { get; private set; }
    private Color _unscannedBorderColor;
    private Color _tunnelWallBorderColor;
    private Color _rockBodyColor;

    // Titik-titik geometri poligon prosedural awal
    public Vector2[] PolygonVertices { get; private set; } = Array.Empty<Vector2>();
    private readonly List<OutlineSegment> _perimeterSegments = new();

    // Data Grid Voxel 13 px (Flat 1D Array untuk akses L1-Cache O(1))
    public int GridSide { get; private set; }
    public Vector2 GridOriginLocal { get; private set; }
    public float OuterBoundRadius { get; private set; }

    private VoxelType[] _voxels = Array.Empty<VoxelType>();
    private float[] _voxelHeat = Array.Empty<float>();
    private readonly List<int> _activeHeatedIndices = new();
    private readonly HashSet<int> _heatedThisFrame = new();

    // Cache Horizontal Row-Span (Run-Length Encoding per baris untuk rendering ribuan voxel 13 px tanpa lag)
    private List<Rect2>[] _rockSpansByRow = Array.Empty<List<Rect2>>();
    private List<Rect2>[] _resourceRectsByRow = Array.Empty<List<Rect2>>();
    private List<WallEdge>[] _exposedWallEdgesByRow = Array.Empty<List<WallEdge>>();

    private int _initialSolidVoxelCount = 0;
    private int _remainingSolidVoxelCount = 0;
    private int _carvedVoxelCount = 0;

    private CollisionShape2D _collisionShape = null!;
    private CircleShape2D _circleShape = null!;
    private int _lastDrawnScannerCount = -1;
    private bool _isDestroyed = false;

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
        // 1. Setup Fisika Ruang Hampa (Zero Gravity, Linear & Angular Damp)
        // Gunakan CollisionLayer/Mask = 2 agar benturan antar-asteroid tetap berjalan,
        // sedangkan PlayerCore & Swarm menggunakan Zero-Lag Voxel Collision (Circle-to-Grid)
        // agar bisa masuk dengan mulus ke dalam terowongan 13 px yang sudah dilubangi!
        CollisionLayer = 2;
        CollisionMask = 2;
        GravityScale = 0.0f;
        LinearDamp = 0.55f;
        AngularDamp = 0.75f;
        Mass = MathF.Max(150.0f, (Radius * Radius) * 0.12f);
        ContactMonitor = false;

        // 2. Tentukan Warna Neon berdasarkan Tipe
        ConfigureColors();

        // 3. Bangkitkan Bentuk Poligon Prosedural Raksasa
        GenerateProceduralGeometry();

        // 4. Bangkitkan Grid Voxel 13 px (Rock & Resource) beserta Cache Row-Span
        InitializeVoxelGrid();
        GenerateResourceVoxelPatches();
        BuildPerimeterSegments();
        RebuildAllRowSpans();

        // 5. Pasang Collision Shape Fisik khusus antar-asteroid (Layer 2)
        _circleShape = new CircleShape2D { Radius = Radius * 0.95f };
        _collisionShape = new CollisionShape2D { Shape = _circleShape };
        AddChild(_collisionShape);

        CurrentHealth = MaxHealth;
    }

    private void ConfigureColors()
    {
        if (IsEnergyAsteroid)
        {
            ResourceGlowColor = new Color(3.2f, 2.8f, 1.2f, 1.0f); // Emas / Putih Energy
        }
        else
        {
            ResourceGlowColor = AsteroidType switch
            {
                SwarmRole.DartNeedle => new Color(2.4f, 2.6f, 0.4f, 1.0f),        // Kuning Dart
                SwarmRole.GigaShuriken => new Color(2.8f, 0.4f, 2.2f, 1.0f),      // Magenta Shuriken
                SwarmRole.DrillDrone => new Color(3.4f, 0.8f, 0.3f, 1.0f),        // Merah Scarlet Drill
                SwarmRole.CrescentBoomerang => new Color(0.4f, 3.0f, 1.2f, 1.0f), // Hijau Boomerang
                SwarmRole.Guardian => new Color(0.4f, 2.5f, 3.2f, 1.0f),          // Cyan Guardian
                _ => new Color(2.2f, 1.8f, 0.8f, 1.0f)
            };
        }

        // Cangkang luar & dinding terowongan: Gelap pekat obsidian/charcoal
        _unscannedBorderColor = new Color(0.18f, 0.16f, 0.22f, 0.85f);
        _tunnelWallBorderColor = new Color(0.24f, 0.20f, 0.30f, 0.78f);
        _rockBodyColor = new Color(0.032f, 0.028f, 0.045f, 0.98f);
    }

    private void GenerateProceduralGeometry()
    {
        int sides = (int)Mathf.Clamp(MathF.Round(Radius * 0.038f), 16, 28);
        PolygonVertices = new Vector2[sides];

        var rng = new RandomNumberGenerator();
        long seedVal = ((long)GlobalPosition.X * 73856093L) ^ ((long)GlobalPosition.Y * 19349663L);
        rng.Seed = (ulong)Math.Abs(seedVal);

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
        }

        OuterBoundRadius = maxVertDist + VoxelSize * 1.5f;
    }

    /// <summary>
    /// Menginisialisasi sel-sel grid 13 px di dalam siluet poligon asteroid sebagai VoxelType.Rock.
    /// </summary>
    private void InitializeVoxelGrid()
    {
        int halfCells = (int)MathF.Ceiling(OuterBoundRadius * InvVoxelSize) + 1;
        GridSide = halfCells * 2;
        GridOriginLocal = new Vector2(-halfCells * VoxelSize, -halfCells * VoxelSize);

        int totalCells = GridSide * GridSide;
        _voxels = new VoxelType[totalCells];
        _voxelHeat = new float[totalCells];

        _rockSpansByRow = new List<Rect2>[GridSide];
        _resourceRectsByRow = new List<Rect2>[GridSide];
        _exposedWallEdgesByRow = new List<WallEdge>[GridSide];

        for (int gy = 0; gy < GridSide; gy++)
        {
            _rockSpansByRow[gy] = new List<Rect2>(4);
            _resourceRectsByRow[gy] = new List<Rect2>(4);
            _exposedWallEdgesByRow[gy] = new List<WallEdge>(4);
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
                    _voxels[rowOffset + gx] = VoxelType.Rock;
                    _initialSolidVoxelCount++;
                }
            }
        }

        _remainingSolidVoxelCount = _initialSolidVoxelCount;
    }

    /// <summary>
    /// Menempatkan deposit kristal (VoxelType.Resource) berukuran 13 px di dalam grid batu asteroid.
    /// </summary>
    private void GenerateResourceVoxelPatches()
    {
        var rng = new RandomNumberGenerator();
        long seedVal = ((long)GlobalPosition.X * 19349663L) ^ ((long)GlobalPosition.Y * 73856093L);
        rng.Seed = (ulong)Math.Abs(seedVal);

        int centerCell = GridSide / 2;
        bool isGiantDeposit = rng.Randf() < 0.45f;

        if (isGiantDeposit)
        {
            float coreRadius = Radius * rng.RandfRange(0.30f, 0.45f);
            int cellSpan = (int)MathF.Ceiling(coreRadius * InvVoxelSize);

            for (int dy = -cellSpan; dy <= cellSpan; dy++)
            {
                int gy = centerCell + dy;
                if (gy < 2 || gy >= GridSide - 2) continue;

                for (int dx = -cellSpan; dx <= cellSpan; dx++)
                {
                    int gx = centerCell + dx;
                    if (gx < 2 || gx >= GridSide - 2) continue;

                    Vector2 localPos = GridToLocalCenter(gx, gy);
                    if (localPos.Length() <= coreRadius && _voxels[gy * GridSide + gx] == VoxelType.Rock && rng.Randf() > 0.15f)
                    {
                        _voxels[gy * GridSide + gx] = VoxelType.Resource;
                    }
                }
            }
        }
        else
        {
            int pocketCount = rng.RandiRange(5, 10);
            for (int p = 0; p < pocketCount; p++)
            {
                float pAngle = rng.Randf() * Mathf.Tau;
                float pDist = rng.RandfRange(Radius * 0.15f, Radius * 0.65f);
                Vector2 pocketCenter = new Vector2(MathF.Cos(pAngle), MathF.Sin(pAngle)) * pDist;
                Vector2I pocketGrid = LocalToGrid(pocketCenter);

                int clusterRadius = rng.RandiRange(2, 4);
                for (int dy = -clusterRadius; dy <= clusterRadius; dy++)
                {
                    for (int dx = -clusterRadius; dx <= clusterRadius; dx++)
                    {
                        if (dx * dx + dy * dy > clusterRadius * clusterRadius + 1) continue;
                        if (rng.Randf() < 0.28f) continue;

                        int gx = pocketGrid.X + dx;
                        int gy = pocketGrid.Y + dy;
                        if (!IsValidGridCoord(gx, gy)) continue;

                        Vector2 cellCenter = GridToLocalCenter(gx, gy);
                        if (cellCenter.Length() < Radius * 0.78f && _voxels[gy * GridSide + gx] == VoxelType.Rock)
                        {
                            _voxels[gy * GridSide + gx] = VoxelType.Resource;
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Memecah garis kontur luar poligon menjadi segmen-segmen ~13 px yang terikat ke sel voxel terdekat,
    /// sehingga ketika mulut terowongan digali oleh laser, garis kontur luar di titik itu ikut terbuka.
    /// </summary>
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
            int steps = Math.Max(1, (int)MathF.Ceiling(len / VoxelSize));

            for (int s = 0; s < steps; s++)
            {
                float t0 = (float)s / steps;
                float t1 = (float)(s + 1) / steps;
                Vector2 segA = a.Lerp(b, t0);
                Vector2 segB = a.Lerp(b, t1);
                Vector2 mid = (segA + segB) * 0.5f;

                Vector2 inward = mid.LengthSquared() > 1.0f ? mid - mid.Normalized() * (VoxelSize * 0.65f) : mid;
                Vector2I gridPt = LocalToGrid(inward);

                _perimeterSegments.Add(new OutlineSegment
                {
                    A = segA,
                    B = segB,
                    CheckGridX = Math.Clamp(gridPt.X, 0, GridSide - 1),
                    CheckGridY = Math.Clamp(gridPt.Y, 0, GridSide - 1)
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
    }

    /// <summary>
    /// Membangun ulang rentang gambar horizontal (RLE Spans) hanya pada 1 baris gy.
    /// Menggabungkan kotak-kotak 13 px bersebelahan menjadi 1 Rect2 memanjang agar rendering bebas lag.
    /// </summary>
    private void RebuildRowSpans(int gy)
    {
        if (gy < 0 || gy >= GridSide) return;

        var rockSpans = _rockSpansByRow[gy];
        var resRects = _resourceRectsByRow[gy];
        var wallEdges = _exposedWallEdgesByRow[gy];

        rockSpans.Clear();
        resRects.Clear();
        wallEdges.Clear();

        int rowOffset = gy * GridSide;
        float rowY = GridOriginLocal.Y + gy * VoxelSize;

        int runStart = -1;
        for (int gx = 0; gx < GridSide; gx++)
        {
            VoxelType t = _voxels[rowOffset + gx];
            bool isSolid = t != VoxelType.Empty;

            if (isSolid)
            {
                if (runStart == -1) runStart = gx;

                if (t == VoxelType.Resource)
                {
                    float cellX = GridOriginLocal.X + gx * VoxelSize;
                    resRects.Add(new Rect2(cellX + 1.0f, rowY + 1.0f, VoxelSize - 2.0f, VoxelSize - 2.0f));
                }

                if (_carvedVoxelCount > 0)
                {
                    float x0 = GridOriginLocal.X + gx * VoxelSize;
                    float x1 = x0 + VoxelSize;
                    float y0 = rowY;
                    float y1 = rowY + VoxelSize;

                    if (gx > 0 && _voxels[rowOffset + gx - 1] == VoxelType.Empty)
                        wallEdges.Add(new WallEdge(new Vector2(x0, y0), new Vector2(x0, y1)));
                    if (gx < GridSide - 1 && _voxels[rowOffset + gx + 1] == VoxelType.Empty)
                        wallEdges.Add(new WallEdge(new Vector2(x1, y0), new Vector2(x1, y1)));
                    if (gy > 0 && _voxels[(gy - 1) * GridSide + gx] == VoxelType.Empty)
                        wallEdges.Add(new WallEdge(new Vector2(x0, y0), new Vector2(x1, y0)));
                    if (gy < GridSide - 1 && _voxels[(gy + 1) * GridSide + gx] == VoxelType.Empty)
                        wallEdges.Add(new WallEdge(new Vector2(x0, y1), new Vector2(x1, y1)));
                }
            }
            else if (runStart != -1)
            {
                int runLen = gx - runStart;
                rockSpans.Add(new Rect2(
                    GridOriginLocal.X + runStart * VoxelSize,
                    rowY,
                    runLen * VoxelSize,
                    VoxelSize
                ));
                runStart = -1;
            }
        }

        if (runStart != -1)
        {
            int runLen = GridSide - runStart;
            rockSpans.Add(new Rect2(
                GridOriginLocal.X + runStart * VoxelSize,
                rowY,
                runLen * VoxelSize,
                VoxelSize
            ));
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        bool needsRedraw = false;

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

    public override void _Draw()
    {
        if (PolygonVertices.Length < 3 || GridSide == 0) return;

        // 1. Gambar Badan Batu 13 px:
        if (_carvedVoxelCount == 0)
        {
            DrawColoredPolygon(PolygonVertices, _rockBodyColor);
        }
        else
        {
            for (int gy = 0; gy < GridSide; gy++)
            {
                var spans = _rockSpansByRow[gy];
                for (int i = 0; i < spans.Count; i++)
                {
                    DrawRect(spans[i], _rockBodyColor, true);
                }
            }
        }

        // 2. Gambar Patch Resource 13 px: Intensitas disesuaikan dari jumlah Scanner
        float scanRatio = Mathf.Clamp((CurrentScannerCount - 4) / 46.0f, 0.04f, 1.0f);

        Color fillColor = new Color(
            ResourceGlowColor.R * (0.06f + scanRatio * 0.94f),
            ResourceGlowColor.G * (0.06f + scanRatio * 0.94f),
            ResourceGlowColor.B * (0.06f + scanRatio * 0.94f),
            0.04f + scanRatio * 0.72f
        );

        Color borderColor = new Color(
            ResourceGlowColor.R * (0.10f + scanRatio * 0.90f),
            ResourceGlowColor.G * (0.10f + scanRatio * 0.90f),
            ResourceGlowColor.B * (0.10f + scanRatio * 0.90f),
            0.07f + scanRatio * 0.88f
        );

        for (int gy = 0; gy < GridSide; gy++)
        {
            var resList = _resourceRectsByRow[gy];
            for (int i = 0; i < resList.Count; i++)
            {
                Rect2 sq = resList[i];
                DrawRect(sq, fillColor, true);
                DrawRect(sq, borderColor, false, 1.0f);
            }
        }

        // 3. Gambar Tepian Dinding Terowongan Dalam
        if (_carvedVoxelCount > 0)
        {
            for (int gy = 0; gy < GridSide; gy++)
            {
                var edges = _exposedWallEdgesByRow[gy];
                for (int i = 0; i < edges.Count; i++)
                {
                    DrawLine(edges[i].A, edges[i].B, _tunnelWallBorderColor, 1.3f, true);
                }
            }
        }

        // 4. Gambar Pemanasan Bertahap pada Blok 13 px yang Sedang Dikikis Laser
        //    (Berpendar dari Oranye Panas -> Kuning Pijar sebelum hancur jadi Empty)
        if (_activeHeatedIndices.Count > 0)
        {
            Color hotOrange = new Color(2.6f, 0.85f, 0.12f, 0.85f);          // Oranye Panas
            Color incandescentYellow = new Color(4.6f, 4.1f, 1.15f, 0.98f);  // Kuning Pijar

            for (int i = 0; i < _activeHeatedIndices.Count; i++)
            {
                int idx = _activeHeatedIndices[i];
                if (_voxels[idx] == VoxelType.Empty) continue;

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
                Color outlineHeatCol = new Color(glowCol.R * 1.15f, glowCol.G * 1.15f, glowCol.B * 1.1f, 0.55f + heat * 0.45f);

                DrawRect(cellRect, fillHeatCol, true);
                DrawRect(cellRect, outlineHeatCol, false, 1.4f);
            }
        }

        // 5. Garis Kontur Luar Asteroid (Terbuka otomatis pada mulut terowongan yang sudah Empty)
        Color outlineColor = _unscannedBorderColor;
        if (scanRatio > 0.45f)
        {
            outlineColor = outlineColor.Lerp(ResourceGlowColor * 0.5f, (scanRatio - 0.45f) * 0.8f);
        }

        for (int i = 0; i < _perimeterSegments.Count; i++)
        {
            var seg = _perimeterSegments[i];
            if (_voxels[seg.CheckGridY * GridSide + seg.CheckGridX] != VoxelType.Empty)
            {
                DrawLine(seg.A, seg.B, outlineColor, 2.4f, true);
            }
        }
    }

    // =========================================================================
    // A. MEKANIK PENGIKISAN TITIK TUMBUKAN (RAYMARCHING & EROSION 13 PX)
    // =========================================================================

    /// <summary>
    /// Menelusuri sel-sel 13 px asteroid sepanjang lintasan sinar (2D DDA Voxel Raymarching)
    /// dan berhenti tepat pada blok solid (Rock atau Resource) 13 px pertama yang dihadang.
    /// Jika blok di depan sudah Empty (Kosong), sinar otomatis menembus masuk ke blok di belakangnya.
    /// </summary>
    public bool RaycastVoxel(Vector2 worldStart, Vector2 worldDir, float maxDist, out VoxelRayHit hit)
    {
        hit = default;
        if (_isDestroyed || GridSide == 0 || _remainingSolidVoxelCount <= 0) return false;

        Vector2 dirNorm = worldDir.Normalized();
        Vector2 toCenter = GlobalPosition - worldStart;
        float proj = toCenter.Dot(dirNorm);
        if (proj < -OuterBoundRadius || proj - OuterBoundRadius > maxDist) return false;

        // Jarak tegak lurus dari pusat asteroid ke garis lintasan sinar
        Vector2 perpFoot = worldStart + dirNorm * proj;
        float perpDistSq = (GlobalPosition - perpFoot).LengthSquared();
        float boundSq = OuterBoundRadius * OuterBoundRadius;
        if (perpDistSq > boundSq) return false;

        // Transformasi murni rotasi & translasi ke ruang lokal grid asteroid
        Vector2 localStart = (worldStart - GlobalPosition).Rotated(-GlobalRotation);
        Vector2 localDir = dirNorm.Rotated(-GlobalRotation);

        float halfChord = MathF.Sqrt(MathF.Max(0.0f, boundSq - perpDistSq));
        float tEnter = MathF.Max(0.0f, proj - halfChord - VoxelSize);
        float tExit = MathF.Min(maxDist, proj + halfChord + VoxelSize);
        if (tExit < tEnter) return false;

        // Inisialisasi 2D Amanatides & Woo Voxel DDA pada ukuran sel 13 px
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
                    // Berhenti tepat di blok solid 13 px pertama yang dihadang!
                    float hitT = MathF.Max(0.0f, t);
                    Vector2 localHit = localStart + localDir * hitT;
                    Vector2 localCellCenter = GridToLocalCenter(gx, gy);

                    hit = new VoxelRayHit
                    {
                        Hit = true,
                        GridX = gx,
                        GridY = gy,
                        Type = cellType,
                        WorldHitPoint = GlobalPosition + localHit.Rotated(GlobalRotation),
                        WorldCellCenter = GlobalPosition + localCellCenter.Rotated(GlobalRotation),
                        Distance = hitT,
                        CurrentHeat = _voxelHeat[idx]
                    };
                    return true;
                }
            }

            // Melangkah 1 sel 13 px lebih dalam mengikuti lintasan sinar
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

    /// <summary>
    /// Menyalurkan panas laser ke blok 13 px pada koordinat (gx, gy).
    /// Ketika panas mencapai >= 1.0, blok tersebut hancur dan berubah status menjadi Empty (Kosong).
    /// </summary>
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
        worldCellCenter = GlobalPosition;
        currentHeat = 0.0f;

        if (_isDestroyed || !IsValidGridCoord(gx, gy)) return false;

        int idx = gy * GridSide + gx;
        VoxelType currentType = _voxels[idx];
        if (currentType == VoxelType.Empty) return false;

        worldCellCenter = GlobalPosition + GridToLocalCenter(gx, gy).Rotated(GlobalRotation);

        if (_voxelHeat[idx] <= 0.0f && !_activeHeatedIndices.Contains(idx))
        {
            _activeHeatedIndices.Add(idx);
        }

        _heatedThisFrame.Add(idx);
        _voxelHeat[idx] += heatDelta;
        currentHeat = Mathf.Clamp(_voxelHeat[idx], 0.0f, 1.0f);

        // Tekanan radiasi halus dari sinar laser
        ApplyImpulse(worldRayDir * (heatDelta * 1.5f), worldCellCenter - GlobalPosition);
        QueueRedraw();

        if (_voxelHeat[idx] >= 1.0f)
        {
            // Blok 13 px hancur dan berubah status menjadi Empty (Kosong)!
            destroyedType = currentType;
            _voxels[idx] = VoxelType.Empty;
            _voxelHeat[idx] = 0.0f;
            _activeHeatedIndices.Remove(idx);

            _remainingSolidVoxelCount = Math.Max(0, _remainingSolidVoxelCount - 1);
            _carvedVoxelCount++;

            // Perbarui span pada baris yang terdampak beserta tetangga atas/bawahnya
            RebuildRowSpans(gy);
            if (gy > 0) RebuildRowSpans(gy - 1);
            if (gy < GridSide - 1) RebuildRowSpans(gy + 1);

            // Jika seluruh isi asteroid hampir habis tergali (< 8% tersisa), runtuhkan sisa cangkang
            if (_remainingSolidVoxelCount <= Math.Max(6, (int)(_initialSolidVoxelCount * 0.08f)))
            {
                DestroyAsteroid();
            }

            return true;
        }

        return false;
    }

    // =========================================================================
    // B. KOLISI FISIKA TEROWONGAN (ZERO-LAG VOXEL COLLISION / CIRCLE-TO-GRID)
    // =========================================================================

    /// <summary>
    /// Pemeriksaan titik lingkaran terhadap grid voxel 13 px (Circle-to-Grid Collision):
    /// - Mengecek sel-sel 13 px di sekitar tepian lingkaran kapal/drone.
    /// - Jika sel berstatus Empty (sudah dilubangi / luar angkasa), bergerak bebas tanpa hambatan.
    /// - Jika menyentuh sel yang masih Rock atau Resource, posisi tertahan di luar permukaan blok 13 px
    ///   dan vektor kecepatan otomatis meluncur (slide) di sepanjang dinding batu.
    /// </summary>
    public bool ResolveCircleVoxelCollision(
        ref Vector2 worldPos,
        ref Vector2 worldVel,
        float radius,
        float impulseWeight = 0.0f)
    {
        if (_isDestroyed || GridSide == 0 || _remainingSolidVoxelCount <= 0) return false;

        // 1. Cek AABB & Lingkaran Luar Cepat (Eliminasi 98% asteroid jauh tanpa beban CPU)
        float maxBound = OuterBoundRadius + radius;
        Vector2 diffFromCenter = worldPos - GlobalPosition;
        if (MathF.Abs(diffFromCenter.X) > maxBound || MathF.Abs(diffFromCenter.Y) > maxBound) return false;
        if (diffFromCenter.LengthSquared() > maxBound * maxBound) return false;

        // 2. Transformasi posisi ke ruang lokal grid asteroid
        Vector2 localPos = diffFromCenter.Rotated(-GlobalRotation);

        int minGx = Math.Clamp((int)MathF.Floor((localPos.X - radius - GridOriginLocal.X) * InvVoxelSize), 0, GridSide - 1);
        int maxGx = Math.Clamp((int)MathF.Floor((localPos.X + radius - GridOriginLocal.X) * InvVoxelSize), 0, GridSide - 1);
        int minGy = Math.Clamp((int)MathF.Floor((localPos.Y - radius - GridOriginLocal.Y) * InvVoxelSize), 0, GridSide - 1);
        int maxGy = Math.Clamp((int)MathF.Floor((localPos.Y + radius - GridOriginLocal.Y) * InvVoxelSize), 0, GridSide - 1);

        bool anyCollision = false;
        Vector2 accumLocalNormal = Vector2.Zero;
        float radiusSq = radius * radius;

        // Resolusi hingga 3 iterasi terdalam lebih dulu agar meluncur mulus di dinding & sudut terowongan 13 px
        for (int pass = 0; pass < 3; pass++)
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
                    // Jika sel berstatus Empty (ruang angkasa / terowongan), lewati tanpa hambatan!
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

        // 3. Kembalikan posisi hasil resolusi ke koordinat dunia & luncurkan kecepatan sepanjang dinding batu
        worldPos = GlobalPosition + localPos.Rotated(GlobalRotation);

        Vector2 worldNormal = accumLocalNormal.LengthSquared() > 1e-5f
            ? accumLocalNormal.Rotated(GlobalRotation).Normalized()
            : (worldPos - GlobalPosition).Normalized();

        Vector2 relVel = worldVel - LinearVelocity;
        float inwardSpeed = relVel.Dot(worldNormal);
        if (inwardSpeed < 0.0f)
        {
            worldVel -= worldNormal * inwardSpeed;

            if (impulseWeight > 0.0f)
            {
                ApplyImpulse(-worldNormal * (-inwardSpeed * impulseWeight), worldPos - GlobalPosition);
            }
        }

        return true;
    }

    /// <summary>
    /// Melubangi sel-sel voxel 13 px dalam radius lingkaran (misal saat diterjang Super Giga Drill Break).
    /// </summary>
    public int CarveVoxelCircle(Vector2 worldCenter, float carveRadius)
    {
        if (_isDestroyed || GridSide == 0 || _remainingSolidVoxelCount <= 0) return 0;

        Vector2 localCenter = (worldCenter - GlobalPosition).Rotated(-GlobalRotation);
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
                if (_voxels[idx] == VoxelType.Empty) continue;

                float cx = GridOriginLocal.X + (gx + 0.5f) * VoxelSize;
                float dx = cx - localCenter.X;
                float dy = cy - localCenter.Y;

                if (dx * dx + dy * dy <= rSq)
                {
                    _voxels[idx] = VoxelType.Empty;
                    _voxelHeat[idx] = 0.0f;
                    _remainingSolidVoxelCount = Math.Max(0, _remainingSolidVoxelCount - 1);
                    _carvedVoxelCount++;
                    carvedNow++;

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

            if (_remainingSolidVoxelCount <= Math.Max(6, (int)(_initialSolidVoxelCount * 0.08f)))
            {
                DestroyAsteroid();
            }
        }

        return carvedNow;
    }

    public void ApplyHitImpulse(Vector2 hitWorldPos, Vector2 impulse, float damage)
    {
        Vector2 offset = hitWorldPos - GlobalPosition;
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

    public void DestroyAsteroid()
    {
        if (_isDestroyed) return;
        _isDestroyed = true;

        EmitSignal(SignalName.AsteroidDestroyed, GlobalPosition, Radius, ResourceGlowColor, IsEnergyAsteroid);

        if (Radius > 320.0f && GetParent() != null)
        {
            int fragmentCount = GD.RandRange(2, 3);
            float subRadius = Radius * 0.42f;

            for (int i = 0; i < fragmentCount; i++)
            {
                float angle = ((float)i / fragmentCount * Mathf.Tau) + (GD.Randf() - 0.5f) * 0.5f;
                Vector2 offset = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (Radius * 0.48f);

                var frag = new Asteroid
                {
                    GlobalPosition = GlobalPosition + offset,
                    Radius = subRadius,
                    MaxHealth = subRadius * 4.0f,
                    AsteroidType = AsteroidType,
                    IsEnergyAsteroid = IsEnergyAsteroid
                };

                float blastSpeed = (float)GD.RandRange(50.0, 140.0);
                frag.LinearVelocity = LinearVelocity + offset.Normalized() * blastSpeed;
                frag.AngularVelocity = (GD.Randf() - 0.5f) * 1.2f;

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
