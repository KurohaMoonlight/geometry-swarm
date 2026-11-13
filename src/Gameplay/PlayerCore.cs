using System;
using System.Collections.Generic;
using Godot;

namespace GeometrySwarm.Gameplay;

/// <summary>
/// Kapal Induk Pemain (The Hive Core) berbentuk gugusan Hexagon sarang lebah (Honeycomb).
/// </summary>
public partial class PlayerCore : CharacterBody2D
{
    [Export] public float Acceleration { get; set; } = 2200.0f;
    [Export] public float Friction { get; set; } = 0.91f;
    [Export] public float MaxSpeed { get; set; } = 420.0f;

    // Radius satu sel hexagon
    private const float HexRadius = 24.0f;

    // Daftar sel hexagon yang menyatu membentuk sarang lebah
    private readonly List<Vector2> _hexCellOffsets = new();

    // Warna neon Honeycomb Core
    private static readonly Color CoreLineColor = new(2.2f, 1.8f, 0.6f, 1.0f);     // Emas Terang HDR
    private static readonly Color CoreAuraColor = new(0.9f, 0.6f, 0.1f, 0.4f);     // Aura Emas Transparan
    private static readonly Color CenterPulseColor = new(3.0f, 2.8f, 1.5f, 1.0f);  // Inti Plasma Putih-Emas

    private float _pulseTimer = 0.0f;

    public override void _Ready()
    {
        // Mulai dengan 1 Core Hexagon utama di tengah
        _hexCellOffsets.Add(Vector2.Zero);
    }

    /// <summary>
    /// Menambah sel hexagon baru ke gugusan sarang lebah
    /// </summary>
    public void AddHexCell(Vector2 offset)
    {
        _hexCellOffsets.Add(offset);
        QueueRedraw();
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

        // 2. Arahkan Hadap Kapal ke Posisi Kursor Mouse
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
            DrawPolyline(points, CoreAuraColor, 4.0f, true);
            // Lapisan garis neon inti
            DrawPolyline(points, CoreLineColor, 1.8f, true);

            // Inti reaktor di tengah tiap sel
            DrawCircle(offset, 4.0f * pulse, CenterPulseColor);
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

