using System;
using System.Collections.Generic;
using GeometrySwarm.Core.ECS.Components;
using Godot;

namespace GeometrySwarm.Gameplay;

/// <summary>
/// Asteroid prosedural raksasa bebas melayang dengan model fisika RigidBody2D.
/// - Memiliki ukuran kolosal (radius 280 hingga 1200+ px).
/// - Swarm tidak dapat menembus cangkang fisik asteroid.
/// - Di dalam batu terdapat patch resource berbentuk kotak-kotak kecil yang redup:
///   * Intensitas kecerahan/visibilitas kotak mengikuti jumlah Scanner Swarm yang dimiliki pemain.
///   * Pada awal permainan (5 Scanner), kotak-kotak kristal sangat-sangat redup.
///   * Semakin banyak Scanner, pendaran warna kotak semakin jelas dan terang.
/// - Asteroid TIDAK menyala putih saat ditabrak/ditembak.
/// - Dapat hancur secara fisik saat ditembak laser mining swarm (pecah menjadi fragmen bongkahan).
/// </summary>
public partial class Asteroid : RigidBody2D
{
    [Signal]
    public delegate void AsteroidDestroyedEventHandler(Vector2 position, float radius, Color resourceColor, bool isEnergy);

    public static readonly List<Asteroid> ActiveAsteroids = new();

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
    private Color _rockBodyColor;

    // Titik-titik geometri poligon prosedural
    public Vector2[] PolygonVertices { get; private set; } = Array.Empty<Vector2>();
    private Vector2[] _closedOutlinePoints = Array.Empty<Vector2>();

    // Patch resource: Kotak-kotak kecil yang redup
    private readonly List<Rect2> _resourceSquares = new();

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
        GravityScale = 0.0f;
        LinearDamp = 0.55f;
        AngularDamp = 0.75f;
        Mass = MathF.Max(150.0f, (Radius * Radius) * 0.12f);
        ContactMonitor = true;
        MaxContactsReported = 6;

        // 2. Tentukan Warna Neon berdasarkan Tipe
        ConfigureColors();

        // 3. Bangkitkan Bentuk Poligon Prosedural Raksasa
        GenerateProceduralGeometry();

        // 4. Bangkitkan Patch Resource Kotak-Kotak Kecil
        GenerateResourceTilePatches();

        // 5. Pasang Collision Shape Fisik Raksasa
        _circleShape = new CircleShape2D { Radius = Radius * 0.98f };
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
                SwarmRole.DartNeedle => new Color(2.4f, 2.6f, 0.4f, 1.0f),       // Kuning Dart
                SwarmRole.GigaShuriken => new Color(2.8f, 0.4f, 2.2f, 1.0f),     // Magenta Shuriken
                SwarmRole.DrillDrone => new Color(3.4f, 0.8f, 0.3f, 1.0f),       // Merah Scarlet Drill
                SwarmRole.CrescentBoomerang => new Color(0.4f, 3.0f, 1.2f, 1.0f), // Hijau Boomerang
                SwarmRole.Guardian => new Color(0.4f, 2.5f, 3.2f, 1.0f),         // Cyan Guardian
                _ => new Color(2.2f, 1.8f, 0.8f, 1.0f)
            };
        }

        // Cangkang luar: Gelap pekat obsidian/charcoal
        _unscannedBorderColor = new Color(0.18f, 0.16f, 0.22f, 0.85f);
        _rockBodyColor = new Color(0.032f, 0.028f, 0.045f, 0.98f);
    }

    private void GenerateProceduralGeometry()
    {
        int sides = (int)Mathf.Clamp(MathF.Round(Radius * 0.038f), 16, 28);
        PolygonVertices = new Vector2[sides];
        _closedOutlinePoints = new Vector2[sides + 1];

        var rng = new RandomNumberGenerator();
        long seedVal = ((long)GlobalPosition.X * 73856093L) ^ ((long)GlobalPosition.Y * 19349663L);
        rng.Seed = (ulong)Math.Abs(seedVal);

        for (int i = 0; i < sides; i++)
        {
            float baseAngle = (float)i / sides * Mathf.Tau;
            float angleJitter = (rng.Randf() - 0.5f) * (Mathf.Tau / sides * 0.35f);
            float angle = baseAngle + angleJitter;

            float rFactor = rng.RandfRange(0.85f, 1.15f);
            float r = Radius * rFactor;

            PolygonVertices[i] = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * r;
            _closedOutlinePoints[i] = PolygonVertices[i];
        }
        _closedOutlinePoints[sides] = _closedOutlinePoints[0];
    }

    private void GenerateResourceTilePatches()
    {
        _resourceSquares.Clear();

        var rng = new RandomNumberGenerator();
        long seedVal = ((long)GlobalPosition.X * 19349663L) ^ ((long)GlobalPosition.Y * 73856093L);
        rng.Seed = (ulong)Math.Abs(seedVal);

        float tileSize = 16.0f;
        float spacing = 20.0f;

        // 45% peluang berisi SATU RESOURCE RAKSASA di tengah inti
        bool isGiantDeposit = rng.Randf() < 0.45f;

        if (isGiantDeposit)
        {
            float coreRadius = Radius * rng.RandfRange(0.32f, 0.48f);
            int tilesSpan = (int)MathF.Ceiling(coreRadius / spacing);

            for (int gx = -tilesSpan; gx <= tilesSpan; gx++)
            {
                for (int gy = -tilesSpan; gy <= tilesSpan; gy++)
                {
                    Vector2 pos = new Vector2(gx * spacing, gy * spacing);
                    if (pos.Length() <= coreRadius && rng.Randf() > 0.12f)
                    {
                        _resourceSquares.Add(new Rect2(pos - new Vector2(tileSize * 0.5f, tileSize * 0.5f), new Vector2(tileSize, tileSize)));
                    }
                }
            }
        }
        else
        {
            // BANYAK RESOURCE KECIL-KECIL: 5 hingga 10 kantung tersebar di dalam batu
            int pocketCount = rng.RandiRange(5, 10);
            for (int p = 0; p < pocketCount; p++)
            {
                float pAngle = rng.Randf() * Mathf.Tau;
                float pDist = rng.RandfRange(Radius * 0.15f, Radius * 0.68f);
                Vector2 pocketCenter = new Vector2(MathF.Cos(pAngle), MathF.Sin(pAngle)) * pDist;

                int tileCountInPocket = rng.RandiRange(4, 12);
                for (int t = 0; t < tileCountInPocket; t++)
                {
                    Vector2 offset = new Vector2(
                        rng.RandiRange(-2, 2) * spacing,
                        rng.RandiRange(-2, 2) * spacing
                    );
                    Vector2 tilePos = pocketCenter + offset;

                    if (tilePos.Length() < Radius * 0.78f)
                    {
                        var rect = new Rect2(tilePos - new Vector2(tileSize * 0.5f, tileSize * 0.5f), new Vector2(tileSize, tileSize));
                        if (!_resourceSquares.Contains(rect))
                        {
                            _resourceSquares.Add(rect);
                        }
                    }
                }
            }
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        // Reaktif gambar ulang jika jumlah Scanner berubah
        if (_lastDrawnScannerCount != CurrentScannerCount)
        {
            _lastDrawnScannerCount = CurrentScannerCount;
            QueueRedraw();
        }
    }

    public override void _Draw()
    {
        if (PolygonVertices.Length < 3) return;

        // 1. Gambar Badan Batu Gelap Pekat (Hitam Obsidian)
        DrawColoredPolygon(PolygonVertices, _rockBodyColor);

        // 2. Gambar Patch Resource: Intensitas disesuaikan murni dari jumlah Scanner!
        // 5 Scanners = 0.04 (Sangat sangat redup di awal permainan)
        // 10 Scanners = 0.14
        // 25 Scanners = 0.48
        // 50+ Scanners = 1.00 (Sangat jelas dan berpendar terang)
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

        foreach (var square in _resourceSquares)
        {
            DrawRect(square, fillColor, true);
            DrawRect(square, borderColor, false, 1.0f);
        }

        // 3. Garis Kontur Luar Asteroid (TIDAK menyala putih ketika ditabrak)
        Color outlineColor = _unscannedBorderColor;
        if (scanRatio > 0.45f)
        {
            // Sedikit pendaran garis tepi jika scanner sudah sangat banyak
            outlineColor = outlineColor.Lerp(ResourceGlowColor * 0.5f, (scanRatio - 0.45f) * 0.8f);
        }

        DrawPolyline(_closedOutlinePoints, outlineColor, 2.4f, true);
    }

    /// <summary>
    /// Menerima tembakan laser penambangan dari Mining Swarm.
    /// Asteroid terkikis dan akan pecah hancur jika HP habis.
    /// </summary>
    public void ApplyLaserMiningDamage(float damage, Vector2 hitPos, Vector2 hitDir)
    {
        CurrentHealth -= damage;
        ApplyImpulse(hitDir * (damage * 0.05f), hitPos - GlobalPosition);

        if (CurrentHealth <= 0.0f)
        {
            DestroyAsteroid();
        }
    }

    /// <summary>
    /// Menerima benturan kinetik dari Swarm Attacker.
    /// Asteroid TIDAK menyala saat ditabrak, melainkan murni terdorong secara fisik.
    /// </summary>
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

    /// <summary>
    /// Diterjang langsung oleh Super Giga Drill Break.
    /// </summary>
    public void ApplyDrillBreach(Vector2 drillPos, Vector2 drillVel, float force)
    {
        Vector2 pushDir = drillVel.LengthSquared() > 1.0f ? drillVel.Normalized() : Vector2.Right;
        ApplyCentralImpulse(pushDir * force);

        float spinDir = GD.Randf() > 0.5f ? 1.0f : -1.0f;
        ApplyTorqueImpulse(spinDir * force * Radius * 0.25f);

        CurrentHealth -= 350.0f;
        if (CurrentHealth <= 0.0f)
        {
            DestroyAsteroid();
        }
    }

    /// <summary>
    /// Penghancuran fisik asteroid saat HP habis terkuras oleh penambangan atau hantaman.
    /// Asteroid pecah menjadi 2-4 bongkahan rubble lebih kecil yang meledak keluar.
    /// </summary>
    public void DestroyAsteroid()
    {
        if (_isDestroyed) return;
        _isDestroyed = true;

        EmitSignal(SignalName.AsteroidDestroyed, GlobalPosition, Radius, ResourceGlowColor, IsEnergyAsteroid);

        // Jika asteroid berukuran besar (Radius > 320), pecah menjadi 2-3 fragmen bongkahan lebih kecil
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
}
