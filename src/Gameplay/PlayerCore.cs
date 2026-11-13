using System;
using System.Collections.Generic;
using Godot;

namespace GeometrySwarm.Gameplay;

/// <summary>
/// Kapal Induk Pemain (The Hive Core) berbentuk gugusan Hexagon sarang lebah (Honeycomb).
/// Menggunakan koordinat aksial heksagonal diskrit agar sel selalu mengunci presisi.
/// </summary>
public partial class PlayerCore : CharacterBody2D
{
    [Export] public float Acceleration { get; set; } = 2200.0f;
    [Export] public float Friction { get; set; } = 0.91f;
    [Export] public float MaxSpeed { get; set; } = 420.0f;

    // Radius satu sel hexagon
    public const float HexRadius = 24.0f;

    // Set koordinat aksial (q, r) yang sudah terisi
    private readonly HashSet<Vector2I> _occupiedAxials = new();
    // Offset piksel relatif terhadap pusat kapal untuk penggambaran
    private readonly List<Vector2> _hexCellOffsets = new();

    // 6 arah tetangga dalam koordinat aksial heksagonal (Pointy-topped)
    private static readonly Vector2I[] HexDirections = new Vector2I[]
    {
        new(1, 0),   // Timur (Kanan)
        new(1, -1),  // Timur Laut (Kanan Atas)
        new(0, -1),  // Barat Laut (Kiri Atas)
        new(-1, 0),  // Barat (Kiri)
        new(-1, 1),  // Barat Daya (Kiri Bawah)
        new(0, 1),   // Tenggara (Kanan Bawah)
    };

    // Warna neon Honeycomb Core
    private static readonly Color CoreLineColor = new(2.2f, 1.8f, 0.6f, 1.0f);     // Emas Terang HDR
    private static readonly Color CoreAuraColor = new(0.9f, 0.6f, 0.1f, 0.4f);     // Aura Emas Transparan
    private static readonly Color CenterPulseColor = new(3.0f, 2.8f, 1.5f, 1.0f);  // Inti Plasma Putih-Emas

    private float _pulseTimer = 0.0f;

    public int CoreCellCount => _hexCellOffsets.Count;

    private CollisionShape2D? _collisionShape;
    private CircleShape2D? _circleShape;

    public override void _Ready()
    {
        // Mulai dengan 1 Core Hexagon di pusat (0, 0)
        _occupiedAxials.Add(Vector2I.Zero);
        _hexCellOffsets.Add(Vector2.Zero);

        // Pasang Collision Shape fisik untuk interaksi dengan Asteroid
        _circleShape = new CircleShape2D { Radius = HexRadius * 1.15f };
        _collisionShape = new CollisionShape2D { Shape = _circleShape };
        AddChild(_collisionShape);
    }

    /// <summary>
    /// Menambah sel hexagon baru yang mengunci sempurna pada slot kisi sarang lebah terdekat
    /// </summary>
    public void AddNextHoneycombCell()
    {
        List<Vector2I> candidates = new();

        foreach (var cell in _occupiedAxials)
        {
            foreach (var dir in HexDirections)
            {
                var neighbor = cell + dir;
                if (!_occupiedAxials.Contains(neighbor) && !candidates.Contains(neighbor))
                {
                    candidates.Add(neighbor);
                }
            }
        }

        // Urutkan kandidat konsentris dari lapisan dalam ke luar secara spiral rapi
        candidates.Sort((a, b) =>
        {
            int distA = Math.Max(Math.Abs(a.X), Math.Max(Math.Abs(a.Y), Math.Abs(a.X + a.Y)));
            int distB = Math.Max(Math.Abs(b.X), Math.Max(Math.Abs(b.Y), Math.Abs(b.X + b.Y)));
            if (distA != distB) return distA.CompareTo(distB);
            return MathF.Atan2(a.Y, a.X).CompareTo(MathF.Atan2(b.Y, b.X));
        });

        if (candidates.Count > 0)
        {
            var chosen = candidates[0];
            _occupiedAxials.Add(chosen);

            // Konversi koordinat aksial heksagonal (q, r) ke piksel presisi
            float x = MathF.Sqrt(3.0f) * HexRadius * (chosen.X + chosen.Y * 0.5f);
            float y = 1.5f * HexRadius * chosen.Y;
            _hexCellOffsets.Add(new Vector2(x, y));

            // Sesuaikan radius perisai/hitbox fisik Core
            if (_circleShape != null)
            {
                float maxDist = HexRadius;
                foreach (var offset in _hexCellOffsets)
                {
                    maxDist = MathF.Max(maxDist, offset.Length() + HexRadius);
                }
                _circleShape.Radius = maxDist;
            }

            QueueRedraw();
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        _pulseTimer += dt * 4.0f;

        // 1. Input Gerakan (WASD)
        Vector2 inputDir = Vector2.Zero;
        if (Input.IsActionPressed("ui_right") || Input.IsKeyPressed(Key.D)) inputDir.X += 1.0f;
        if (Input.IsActionPressed("ui_left") || Input.IsKeyPressed(Key.A)) inputDir.X -= 1.0f;
        if (Input.IsActionPressed("ui_down") || Input.IsKeyPressed(Key.S)) inputDir.Y += 1.0f;
        if (Input.IsActionPressed("ui_up") || Input.IsKeyPressed(Key.W)) inputDir.Y -= 1.0f;

        if (inputDir.LengthSquared() > 0.01f)
        {
            inputDir = inputDir.Normalized();
            Velocity += inputDir * (Acceleration * dt);
        }

        // Terapkan gesekan inersia
        Velocity *= MathF.Pow(Friction, dt * 60.0f);
        if (Velocity.LengthSquared() > MaxSpeed * MaxSpeed)
        {
            Velocity = Velocity.Normalized() * MaxSpeed;
        }

        MoveAndSlide();

        // 2. Berikan impuls dorongan fisik jika menabrak Asteroid bebas melayang
        int slideCount = GetSlideCollisionCount();
        for (int i = 0; i < slideCount; i++)
        {
            var collision = GetSlideCollision(i);
            if (collision.GetCollider() is Asteroid asteroid)
            {
                Vector2 pushDir = -collision.GetNormal();
                asteroid.ApplyHitImpulse(collision.GetPosition(), pushDir * Velocity.Length() * 0.35f, 2.0f);
            }
        }

        // 3. Arahkan Hadap Kapal ke Posisi Kursor Mouse
        Vector2 mousePos = GetGlobalMousePosition();
        Vector2 toMouse = mousePos - GlobalPosition;
        if (toMouse.LengthSquared() > 10.0f)
        {
            Rotation = Mathf.LerpAngle(Rotation, toMouse.Angle() + Mathf.Pi * 0.5f, 14.0f * dt);
        }

        QueueRedraw();
    }

    public override void _Draw()
    {
        float pulse = 1.0f + MathF.Sin(_pulseTimer) * 0.15f;

        // Gambar setiap sel hexagon dalam gugusan sarang lebah
        foreach (Vector2 offset in _hexCellOffsets)
        {
            Vector2[] points = GetHexPoints(offset, HexRadius);

            // Lapisan aura luar
            DrawPolyline(points, CoreAuraColor, 3.5f, true);
            // Lapisan garis neon inti
            DrawPolyline(points, CoreLineColor, 1.6f, true);

            // Inti reaktor di tengah tiap sel
            DrawCircle(offset, 3.5f * pulse, CenterPulseColor);
        }
    }

    private static Vector2[] GetHexPoints(Vector2 center, float radius)
    {
        var pts = new Vector2[7];
        for (int i = 0; i < 6; i++)
        {
            float angle = Mathf.DegToRad(60.0f * i - 30.0f);
            pts[i] = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
        }
        pts[6] = pts[0]; // Menutup loop
        return pts;
    }
}
