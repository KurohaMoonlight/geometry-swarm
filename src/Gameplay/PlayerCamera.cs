using System;
using Godot;

namespace GeometrySwarm.Gameplay;

/// <summary>
/// Kamera 2D dengan pelacakan halus (smoothing) dan zoom tetap (Fixed Zoom)
/// tanpa efek scaling/zoom-out dinamis agar skala visual dan grid selalu stabil.
/// </summary>
public partial class PlayerCamera : Camera2D
{
    public static PlayerCamera? Instance { get; private set; }

    [Export] public NodePath PlayerCorePath { get; set; } = "../SwarmSimulation/PlayerCore";
    [Export] public NodePath SwarmSimPath { get; set; } = "../SwarmSimulation";
    [Export] public float FixedZoom { get; set; } = 0.85f;

    private Node2D? _playerCore;

    // Shake kamera independen
    private float _shakeIntensity = 0.0f;

    public override void _Ready()
    {
        Instance = this;
        PositionSmoothingEnabled = true;
        PositionSmoothingSpeed = 10.0f;

        if (HasNode(PlayerCorePath))
        {
            _playerCore = GetNode<Node2D>(PlayerCorePath);
        }

        if (_playerCore != null)
        {
            GlobalPosition = _playerCore.GlobalPosition;
            ResetSmoothing();
        }

        Zoom = new Vector2(FixedZoom, FixedZoom);
    }

    public void TriggerShake(float amount)
    {
        _shakeIntensity = MathF.Min(22.0f, MathF.Max(_shakeIntensity, amount));
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;

        // 1. Pelacakan Posisi Kapal Induk (Tanpa efek scaling/zoom kamera)
        if (_playerCore != null)
        {
            GlobalPosition = _playerCore.GlobalPosition;
        }

        // Pastikan Zoom selalu terkunci konstan
        if (MathF.Abs(Zoom.X - FixedZoom) > 0.001f)
        {
            Zoom = new Vector2(FixedZoom, FixedZoom);
        }

        // 2. Redam Shake Kamera
        if (_shakeIntensity > 0.05f)
        {
            Offset = new Vector2(
                (GD.Randf() - 0.5f) * 2.0f * _shakeIntensity,
                (GD.Randf() - 0.5f) * 2.0f * _shakeIntensity
            );
            _shakeIntensity = Mathf.Lerp(_shakeIntensity, 0.0f, 10.0f * dt);
        }
        else
        {
            Offset = Vector2.Zero;
        }
    }
}
