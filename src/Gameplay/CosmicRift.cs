using System;
using Godot;

namespace GeometrySwarm.Gameplay;

/// <summary>
/// Cosmic Rift / Void Gate di Sabuk Dalam (Inner Belt, R ~ 1.550 px).
/// - Anomali lubang cacing kosmik berdenyut dengan pusaran partikel gravitasi & piringan akresi neon.
/// - Berotasi orbit mengelilingi pusat arena secara perlahan (Orbital Precession ~8.7 menit/putaran).
/// - Mengalami Instabilitas Gravitasi berkala (Gravitational Ejection) untuk melontarkan asteroid baru
///   ke arah sabuk luar dengan lintasan spiral Archimedes (centrifugal + tangential drift).
/// - Memiliki 4 fase dinamis: Dormant -> Infall (Akresi sedot grid) -> Erupting (Flash & Shockwave) -> Cooling.
/// </summary>
public partial class CosmicRift : Node2D
{
    [Signal]
    public delegate void RiftInfallStartedEventHandler(Vector2 riftPos, float power, float radius);

    [Signal]
    public delegate void RiftEruptedEventHandler(Vector2 riftPos, float power, float radius);

    [Signal]
    public delegate void RiftSpawnAsteroidRequestedEventHandler(Vector2 riftPos, Vector2 ejectVelocity);

    public enum RiftState
    {
        Dormant = 0,
        Infall = 1,
        Erupting = 2,
        Cooling = 3
    }

    [Export] public float OrbitRadius { get; set; } = 1550.0f;
    [Export] public float OrbitAngle { get; set; } = 0.0f;
    [Export] public float OrbitSpeed { get; set; } = 0.012f; // ~0.68 deg/sec
    [Export] public int RiftId { get; set; } = 0;

    public RiftState State { get; private set; } = RiftState.Dormant;
    public bool IsEjecting => State != RiftState.Dormant;

    public const float InfallDuration = 3.2f;
    public const float EruptionDuration = 0.85f;
    public const float CoolingDuration = 2.8f;

    private float _stateTimer = 0.0f;
    private float _pulseTimer = 0.0f;
    private float _spinAngle = 0.0f;
    private float _infallImpulseTimer = 0.0f;
    private float _redrawThrottleTimer = 0.0f;
    private readonly Vector2[] _reusableArmPts = new Vector2[17];

    // Partikel akresi debu kosmik (Motes) yang berputar mengitari singularitas
    private const int MoteCount = 20;
    private readonly float[] _moteRadii = new float[MoteCount];
    private readonly float[] _moteAngles = new float[MoteCount];
    private readonly float[] _moteSpeeds = new float[MoteCount];

    // Warna Palet Kosmik Neon Geometry Wars
    private static readonly Color VoidCoreColor = new(0.005f, 0.002f, 0.015f, 1.0f);
    private static readonly Color EventHorizonColor = new(3.8f, 2.6f, 5.0f, 1.0f);
    private static readonly Color AccretionViolet = new(2.5f, 0.45f, 4.2f, 0.85f);
    private static readonly Color AccretionMagenta = new(4.0f, 0.35f, 2.6f, 0.90f);
    private static readonly Color AccretionCyan = new(0.4f, 3.2f, 4.5f, 0.95f);
    private static readonly Color InfallWarningColor = new(4.8f, 1.2f, 2.8f, 1.0f);

    public override void _Ready()
    {
        var rng = new RandomNumberGenerator();
        rng.Seed = (ulong)(1337 + RiftId * 9871);

        for (int i = 0; i < MoteCount; i++)
        {
            _moteRadii[i] = rng.RandfRange(42.0f, 115.0f);
            _moteAngles[i] = rng.Randf() * Mathf.Tau;
            _moteSpeeds[i] = rng.RandfRange(1.2f, 2.8f) * (rng.Randf() > 0.5f ? 1.0f : -1.0f);
        }

        UpdateOrbitPosition();
    }

    private void UpdateOrbitPosition()
    {
        Position = new Vector2(MathF.Cos(OrbitAngle), MathF.Sin(OrbitAngle)) * OrbitRadius;
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        _pulseTimer += dt;

        // 1. Presesi Orbit Lambat di Sekitar (0,0)
        OrbitAngle += OrbitSpeed * dt;
        UpdateOrbitPosition();

        // 2. Kecepatan Putaran Piringan Akresi
        float spinMult = State switch
        {
            RiftState.Infall => Mathf.Lerp(1.2f, 4.8f, _stateTimer / InfallDuration),
            RiftState.Erupting => 5.2f,
            RiftState.Cooling => Mathf.Lerp(3.5f, 1.0f, _stateTimer / CoolingDuration),
            _ => 1.0f
        };
        _spinAngle += dt * 1.8f * spinMult;

        // 3. Simulasi Gerak Partikel Motes Akresi
        for (int i = 0; i < MoteCount; i++)
        {
            _moteAngles[i] += _moteSpeeds[i] * dt * spinMult;

            if (State == RiftState.Infall)
            {
                // Partikel terhisap spiral ke dalam singularitas
                _moteRadii[i] -= dt * 24.0f * (_stateTimer / InfallDuration + 0.4f);
                if (_moteRadii[i] < 30.0f)
                {
                    _moteRadii[i] = 110.0f + (float)GD.RandRange(0.0, 15.0);
                }
            }
            else
            {
                // Variasi getar radial halus alami
                _moteRadii[i] += MathF.Sin(_pulseTimer * 3.0f + i) * (dt * 4.0f);
                _moteRadii[i] = Mathf.Clamp(_moteRadii[i], 38.0f, 118.0f);
            }
        }

        // 4. State Machine Ejection
        _stateTimer += dt;

        switch (State)
        {
            case RiftState.Dormant:
                break;

            case RiftState.Infall:
                // Tarikan gravitasi berkala pada grid sekitar portal
                _infallImpulseTimer += dt;
                if (_infallImpulseTimer >= 0.28f)
                {
                    _infallImpulseTimer = 0.0f;
                    float progress = _stateTimer / InfallDuration;
                    float pullForce = Mathf.Lerp(450.0f, 950.0f, progress);
                    EmitSignal(SignalName.RiftInfallStarted, GlobalPosition, pullForce, 440.0f);
                }

                if (_stateTimer >= InfallDuration)
                {
                    EnterEruptingState();
                }
                break;

            case RiftState.Erupting:
                if (_stateTimer >= EruptionDuration)
                {
                    State = RiftState.Cooling;
                    _stateTimer = 0.0f;
                }
                break;

            case RiftState.Cooling:
                if (_stateTimer >= CoolingDuration)
                {
                    State = RiftState.Dormant;
                    _stateTimer = 0.0f;
                }
                break;
        }

        // Culling & redraw throttling: hanya redraw bila dekat pemain atau sedang aktif infall/erupsi
        Vector2 playerPos = PlayerCore.Instance?.GlobalPosition ?? Vector2.Zero;
        float distSq = GlobalPosition.DistanceSquaredTo(playerPos);
        bool isNearPlayer = distSq <= (2500.0f * 2500.0f);
        bool isActivePhase = State != RiftState.Dormant;

        if (isNearPlayer || isActivePhase)
        {
            _redrawThrottleTimer += dt;
            if (_redrawThrottleTimer >= 0.033f) // ~30 FPS
            {
                _redrawThrottleTimer = 0.0f;
                QueueRedraw();
            }
        }
    }

    public bool TriggerEjectionSequence()
    {
        if (State != RiftState.Dormant) return false;

        State = RiftState.Infall;
        _stateTimer = 0.0f;
        _infallImpulseTimer = 0.0f;

        // Hentakan hisap awal pada grid
        EmitSignal(SignalName.RiftInfallStarted, GlobalPosition, 650.0f, 420.0f);
        return true;
    }

    private void EnterEruptingState()
    {
        State = RiftState.Erupting;
        _stateTimer = 0.0f;

        // 1. Gelombang Kejut Radial Dahsyat pada Grid
        EmitSignal(SignalName.RiftErupted, GlobalPosition, 2600.0f, 540.0f);

        // 2. Hitung Vektor Lontaran Spiral Archimedes (Centrifugal + Tangential Drift)
        Vector2 radialDir = GlobalPosition.LengthSquared() > 1.0f ? GlobalPosition.Normalized() : Vector2.Right;
        float spinDir = (RiftId % 2 == 0) ? 1.0f : -1.0f;
        Vector2 tangentDir = new Vector2(-radialDir.Y, radialDir.X) * spinDir;

        float radialSpeed = (float)GD.RandRange(185.0, 245.0);
        float tangentSpeed = (float)GD.RandRange(65.0, 105.0);
        Vector2 ejectVel = (radialDir * radialSpeed) + (tangentDir * tangentSpeed);

        // Titik spawn sedikit di luar horizon terluar portal searah lontaran
        Vector2 spawnPos = GlobalPosition + radialDir * 80.0f;

        // 3. Minta AsteroidFieldManager memunculkan asteroid baru
        EmitSignal(SignalName.RiftSpawnAsteroidRequested, spawnPos, ejectVel);
    }

    public override void _Draw()
    {
        float pulse = 0.88f + 0.12f * MathF.Sin(_pulseTimer * 3.5f);

        // A. Cincin Aura Lensa Gravitasi Terluar (Atmospheric Lens Glow)
        Color haloCol = (State == RiftState.Infall)
            ? new Color(InfallWarningColor.R * 0.4f, InfallWarningColor.G * 0.2f, InfallWarningColor.B * 0.5f, 0.22f)
            : new Color(0.25f, 0.08f, 0.45f, 0.18f);

        DrawCircle(Vector2.Zero, 125.0f * pulse, haloCol);

        // B. Gelombang Kejut Erupsi Mengembang (Shockwave Ring)
        if (State == RiftState.Erupting || (State == RiftState.Cooling && _stateTimer < 1.0f))
        {
            float shockProgress = (State == RiftState.Erupting)
                ? (_stateTimer / EruptionDuration)
                : (1.0f + _stateTimer / 1.0f);

            float shockRadius = Mathf.Lerp(34.0f, 260.0f, Mathf.Clamp(shockProgress * 0.5f, 0.0f, 1.0f));
            float shockAlpha = Mathf.Clamp(1.0f - (shockProgress * 0.5f), 0.0f, 1.0f);

            Color shockCol = new Color(EventHorizonColor.R, EventHorizonColor.G, EventHorizonColor.B, shockAlpha);
            DrawArc(Vector2.Zero, shockRadius, 0.0f, Mathf.Tau, 36, shockCol, 3.8f, true);
        }

        // C. 4 Lengan Spiral Pusaran Akresi (Accretion Vortex Spiral Arms)
        int armCount = 4;
        for (int a = 0; a < armCount; a++)
        {
            float armBaseAngle = _spinAngle + (a * (Mathf.Tau / armCount));
            Color armColor = (a % 2 == 0) ? AccretionViolet : AccretionMagenta;
            if (State == RiftState.Infall)
            {
                armColor = armColor.Lerp(InfallWarningColor, 0.65f);
            }

            int segments = 16;
            for (int s = 0; s <= segments; s++)
            {
                float t = (float)s / segments;
                float r = Mathf.Lerp(34.0f, 115.0f, t);
                // Kurva spiral logaritmik
                float spiralAngle = armBaseAngle + (t * 1.65f);
                _reusableArmPts[s] = new Vector2(MathF.Cos(spiralAngle), MathF.Sin(spiralAngle)) * r;
            }

            DrawPolyline(_reusableArmPts, armColor, 2.2f, false);
        }

        // D. Partikel Motes Akresi Kosmik
        for (int i = 0; i < MoteCount; i++)
        {
            Vector2 motePos = new Vector2(MathF.Cos(_moteAngles[i]), MathF.Sin(_moteAngles[i])) * _moteRadii[i];
            Color moteColor = (i % 3 == 0) ? AccretionCyan : AccretionViolet;
            if (State == RiftState.Infall)
            {
                moteColor = InfallWarningColor;
            }

            float moteSize = 2.0f + MathF.Sin(_pulseTimer * 4.0f + i) * 0.8f;
            DrawCircle(motePos, moteSize, moteColor);
        }

        // E. Cincin Horizon Peristiwa (Photon Sphere / Event Horizon Rim)
        float horizonRadius = (State == RiftState.Infall)
            ? Mathf.Lerp(34.0f, 26.0f, _stateTimer / InfallDuration) // Mengkerut padat saat infall
            : (34.0f + MathF.Sin(_pulseTimer * 4.2f) * 2.2f);

        Color ringCol = (State == RiftState.Infall) ? InfallWarningColor : EventHorizonColor;
        if (State == RiftState.Erupting)
        {
            ringCol = new Color(5.0f, 4.5f, 5.0f, 1.0f); // Blinding flash
        }

        DrawArc(Vector2.Zero, horizonRadius, 0.0f, Mathf.Tau, 48, ringCol, 3.2f, true);
        DrawArc(Vector2.Zero, horizonRadius - 4.0f, 0.0f, Mathf.Tau, 36, ringCol * 0.65f, 1.6f, true);

        // F. Inti Void Singularitas (Pitch Black Hole Center)
        DrawCircle(Vector2.Zero, horizonRadius - 5.0f, VoidCoreColor);
    }
}

