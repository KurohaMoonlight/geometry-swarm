using System;
using Godot;

namespace GeometrySwarm.Gameplay;

/// <summary>
/// Kamera dinamis 2D dengan pelacakan halus (smoothing) & zoom adaptif otomatis murni.
/// - Scroll roda mouse dinonaktifkan: kamera murni otomatis menjauh (zoom out)
///   seiring bertambahnya jumlah swarm agar formasi kawanan dan asteroid kolosal
///   selalu terlihat proporsional dan tidak sesak di layar.
/// - Memberikan zoom out sinematik ekstra saat Super Giga Drill Break aktif.
/// </summary>
public partial class PlayerCamera : Camera2D
{
    public static PlayerCamera? Instance { get; private set; }

    [Export] public NodePath PlayerCorePath { get; set; } = "../SwarmSimulation/PlayerCore";
    [Export] public NodePath SwarmSimPath { get; set; } = "../SwarmSimulation";

    private Node2D? _playerCore;
    private SwarmSimulation? _swarmSim;

    // Nilai zoom real-time
    private float _currentZoom = 0.88f;
    private const float ZoomLerpSpeed = 2.8f;

    // Shake kamera independen
    private float _shakeIntensity = 0.0f;

    public override void _Ready()
    {
        Instance = this;
        PositionSmoothingEnabled = true;
        PositionSmoothingSpeed = 7.5f;

        if (HasNode(PlayerCorePath))
        {
            _playerCore = GetNode<Node2D>(PlayerCorePath);
        }
        if (HasNode(SwarmSimPath))
        {
            _swarmSim = GetNode<SwarmSimulation>(SwarmSimPath);
        }

        if (_playerCore != null)
        {
            GlobalPosition = _playerCore.GlobalPosition;
            ResetSmoothing();
        }

        Zoom = new Vector2(_currentZoom, _currentZoom);
    }

    public void TriggerShake(float amount)
    {
        _shakeIntensity = MathF.Max(_shakeIntensity, amount);
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;

        // 1. Pelacakan Posisi Kapal Induk
        if (_playerCore != null)
        {
            GlobalPosition = _playerCore.GlobalPosition;
        }

        // 2. Hitung Zoom Murni Otomatis Berdasarkan Jumlah Swarm
        // Semakin banyak swarm (15 -> 2.000 unit), kamera otomatis mundur secara organik (0.90x -> 0.35x)
        int swarmCount = _swarmSim?.EntityCount ?? 15;
        
        float targetZoom = 0.90f - MathF.Sqrt(MathF.Max(0, swarmCount - 10)) * 0.0135f;
        targetZoom = Mathf.Clamp(targetZoom, 0.35f, 0.90f);

        // Zoom out ekstra sinematik ketika Super Giga Drill aktif/charging
        if (_swarmSim != null && (_swarmSim.IsChargingDrill || _swarmSim.IsGigaDrillActive))
        {
            targetZoom *= 0.82f;
        }

        _currentZoom = Mathf.Lerp(_currentZoom, targetZoom, ZoomLerpSpeed * dt);
        Zoom = new Vector2(_currentZoom, _currentZoom);

        // 3. Redam Shake Kamera
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
