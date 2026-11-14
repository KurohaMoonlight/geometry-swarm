using System;
using Godot;

namespace GeometrySwarm.Gameplay;

/// <summary>
/// Anomali Astrofisika Sabuk Dalam: Dualitas Black Hole & White Hole.
/// - Fase Pasif (Dormant): Black Hole sunyi berupa lingkaran hitam pekat mutlak (menutup grid & latar)
///   dikelilingi cincin radiasi Hawking (Photon Sphere) yang sangat tipis, tajam, dan elegan.
/// - Fase Infall (3.2s): Inversi polaritas ruang-waktu menjadi White Hole yang berpijar terang menyilaukan.
/// - Fase Erupting (1.0s): White Hole memancarkan ledakan cahaya dan melahirkan asteroid baru
///   dari pusat singularitasnya dengan animasi pembesaran ala iOS App Launch.
/// - Fase Cooling (2.8s): White Hole mereda dan mendingin kembali menjadi Black Hole abadi.
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

    public const float InfallDuration = 3.8f;
    public const float EruptionDuration = 1.35f;
    public const float CoolingDuration = 2.6f;

    private float _stateTimer = 0.0f;
    private float _pulseTimer = 0.0f;
    private float _spinAngle = 0.0f;
    private float _infallImpulseTimer = 0.0f;
    private float _redrawThrottleTimer = 0.0f;

    // Partikel Kuantum Virtual di Tepi Horizon (Hawking Radiation Fluctuation Motes)
    private const int HawkingPhotonCount = 28;
    private readonly float[] _hawkingPhotonAngles = new float[HawkingPhotonCount];
    private readonly float[] _hawkingPhotonSpeeds = new float[HawkingPhotonCount];
    private readonly float[] _hawkingPhotonPhases = new float[HawkingPhotonCount];

    // Jaringan Retakan Ruang-Waktu Event Horizon saat Black Hole Mengalami Supercharge Inversi
    private readonly System.Collections.Generic.List<(Vector2 Start, Vector2 End, float Jitter)> _horizonFractures = new();

    // Palet Warna Singularitas
    private static readonly Color VoidCoreColor = new(0.002f, 0.001f, 0.005f, 1.0f); // Hitam pekat mutlak (occlude grid & parallax)
    private static readonly Color WhiteHoleCoreColor = new(5.5f, 5.2f, 4.8f, 1.0f); // Putih-perak blinding HDR
    private static readonly Color HawkingRadiationCyan = new(0.75f, 2.95f, 4.8f, 0.95f);
    private static readonly Color HawkingRadiationViolet = new(3.4f, 1.8f, 4.5f, 0.85f);
    private static readonly Color SupercriticalFlareColor = new(5.2f, 4.6f, 5.5f, 1.0f);
    private static readonly Color IncandescentCrackColor = new(5.5f, 4.8f, 3.2f, 1.0f);

    public override void _Ready()
    {
        var rng = new RandomNumberGenerator();
        rng.Seed = (ulong)(1337 + RiftId * 9871);

        for (int i = 0; i < HawkingPhotonCount; i++)
        {
            _hawkingPhotonAngles[i] = rng.Randf() * Mathf.Tau;
            _hawkingPhotonSpeeds[i] = rng.RandfRange(1.4f, 3.2f) * (rng.Randf() > 0.5f ? 1.0f : -1.0f);
            _hawkingPhotonPhases[i] = rng.Randf() * Mathf.Tau;
        }

        // Bangun pola retakan petir geometris melintasi piringan Event Horizon (R ~ 46 px)
        _horizonFractures.Clear();
        int crackBranches = 16;
        for (int b = 0; b < crackBranches; b++)
        {
            float baseAngle = (float)b / crackBranches * Mathf.Tau + rng.RandfRange(-0.15f, 0.15f);
            Vector2 pCenter = new Vector2(MathF.Cos(baseAngle), MathF.Sin(baseAngle)) * rng.RandfRange(3.0f, 9.0f);
            Vector2 pMid = new Vector2(MathF.Cos(baseAngle + rng.RandfRange(-0.25f, 0.25f)), MathF.Sin(baseAngle + rng.RandfRange(-0.25f, 0.25f))) * rng.RandfRange(22.0f, 33.0f);
            Vector2 pRim = new Vector2(MathF.Cos(baseAngle + rng.RandfRange(-0.45f, 0.45f)), MathF.Sin(baseAngle + rng.RandfRange(-0.45f, 0.45f))) * rng.RandfRange(42.0f, 47.0f);

            _horizonFractures.Add((pCenter, pMid, rng.Randf()));
            _horizonFractures.Add((pMid, pRim, rng.Randf()));

            if (rng.Randf() > 0.5f)
            {
                Vector2 pSubFork = new Vector2(MathF.Cos(baseAngle + rng.RandfRange(0.35f, 0.65f)), MathF.Sin(baseAngle + rng.RandfRange(0.35f, 0.65f))) * rng.RandfRange(38.0f, 46.0f);
                _horizonFractures.Add((pMid, pSubFork, rng.Randf()));
            }
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

        // 2. Putaran Fluktuasi Kuantum
        float spinMult = State switch
        {
            RiftState.Infall => Mathf.Lerp(1.5f, 28.0f, MathF.Pow(_stateTimer / InfallDuration, 2.2f)),
            RiftState.Erupting => 14.0f,
            RiftState.Cooling => Mathf.Lerp(8.0f, 1.0f, _stateTimer / CoolingDuration),
            _ => 1.0f
        };
        _spinAngle += dt * 2.4f * spinMult;

        for (int i = 0; i < HawkingPhotonCount; i++)
        {
            _hawkingPhotonAngles[i] += _hawkingPhotonSpeeds[i] * dt * spinMult;
        }

        // 3. State Machine Ejection
        _stateTimer += dt;

        switch (State)
        {
            case RiftState.Dormant:
                break;

            case RiftState.Infall:
                // Tarikan gravitasi hisap eskalatif dahsyat pada grid (ruang-waktu melengkung ekstrem ke singularitas)
                _infallImpulseTimer += dt;
                float progress = Mathf.Clamp(_stateTimer / InfallDuration, 0.0f, 1.0f);
                float impulseInterval = Mathf.Lerp(0.18f, 0.09f, progress); // Frekuensi getaran hisap makin rapat
                if (_infallImpulseTimer >= impulseInterval)
                {
                    _infallImpulseTimer = 0.0f;
                    float pullForce = Mathf.Lerp(750.0f, 3800.0f, progress * progress);
                    float pullRadius = Mathf.Lerp(450.0f, 850.0f, progress);
                    EmitSignal(SignalName.RiftInfallStarted, GlobalPosition, pullForce, pullRadius);
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

        // Culling & redraw throttling
        Vector2 playerPos = PlayerCore.Instance?.GlobalPosition ?? Vector2.Zero;
        float distSq = GlobalPosition.DistanceSquaredTo(playerPos);
        bool isNearPlayer = distSq <= (2600.0f * 2600.0f);
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

        // Hentakan hisap awal pada grid saat inversi polaritas dimulai
        EmitSignal(SignalName.RiftInfallStarted, GlobalPosition, 850.0f, 450.0f);
        return true;
    }

    private void EnterEruptingState()
    {
        State = RiftState.Erupting;
        _stateTimer = 0.0f;

        // 1. Gelombang Kejut Radial Dahsyat pada Grid saat White Hole Terbuka
        EmitSignal(SignalName.RiftErupted, GlobalPosition, 4600.0f, 850.0f);

        // 2. Hitung Vektor Lontaran Spiral Archimedes
        Vector2 radialDir = GlobalPosition.LengthSquared() > 1.0f ? GlobalPosition.Normalized() : Vector2.Right;
        float spinDir = (RiftId % 2 == 0) ? 1.0f : -1.0f;
        Vector2 tangentDir = new Vector2(-radialDir.Y, radialDir.X) * spinDir;

        float radialSpeed = (float)GD.RandRange(240.0, 320.0);
        float tangentSpeed = (float)GD.RandRange(95.0, 145.0);
        Vector2 ejectVel = (radialDir * radialSpeed) + (tangentDir * tangentSpeed);

        // Asteroid lahir tepat di pusat White Hole (GlobalPosition)!
        Vector2 spawnPos = GlobalPosition;

        // 3. Minta AsteroidFieldManager melahirkan asteroid baru
        EmitSignal(SignalName.RiftSpawnAsteroidRequested, spawnPos, ejectVel);
    }

    public override void _Draw()
    {
        float pulse = 0.96f + 0.04f * MathF.Sin(_pulseTimer * 3.2f);
        float baseHorizonR = 46.0f * pulse;

        // =====================================================================
        // 1. FASE DORMANT: THE PITCH-BLACK HOLE & HAWKING RADIATION
        // =====================================================================
        if (State == RiftState.Dormant)
        {
            // A. Selimut Gravitasi Luar (Faint Lensing Corona)
            DrawCircle(Vector2.Zero, baseHorizonR * 1.55f, new Color(0.20f, 0.08f, 0.38f, 0.07f));
            DrawCircle(Vector2.Zero, baseHorizonR * 1.25f, new Color(0.12f, 0.28f, 0.45f, 0.12f));

            // B. INTI BLACK HOLE PEKAT (Penutup Mutlak Grid & Parallax)
            // Lingkaran hitam mutlak (alpha 1.0) memutus dan menelan seluruh garis grid di baliknya
            DrawCircle(Vector2.Zero, baseHorizonR, VoidCoreColor);

            // C. CINCIN RADIASI HAWKING (Thin & Razor-Sharp Photon Sphere)
            // Lapisan 1: Cincin batas horizon ultra-tipis (Cyan Kuantum Tajam)
            DrawArc(Vector2.Zero, baseHorizonR + 1.2f, 0.0f, Mathf.Tau, 64, HawkingRadiationCyan, 1.8f, true);

            // Lapisan 2: Harmonik radiasi sekunder (Violet Fluktuatif)
            float secPulse = MathF.Sin(_pulseTimer * 4.8f);
            Color secCol = HawkingRadiationViolet;
            secCol.A = 0.65f + 0.25f * secPulse;
            DrawArc(Vector2.Zero, baseHorizonR + 3.2f, _spinAngle * 0.5f, _spinAngle * 0.5f + Mathf.Pi * 1.6f, 48, secCol, 1.1f, true);

            // D. FLUKTUASI PARTIKEL KUANTUM VIRTUAL (Hawking Virtual Photons)
            for (int i = 0; i < HawkingPhotonCount; i++)
            {
                float angle = _hawkingPhotonAngles[i];
                float blink = 0.5f + 0.5f * MathF.Sin(_pulseTimer * 8.0f + _hawkingPhotonPhases[i]);
                if (blink < 0.25f) continue;

                float r = baseHorizonR + (i % 2 == 0 ? 1.4f : 3.0f) + MathF.Sin(angle * 4.0f + _pulseTimer) * 1.2f;
                Vector2 pos = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * r;
                Color pCol = (i % 2 == 0) ? HawkingRadiationCyan : HawkingRadiationViolet;
                pCol.A = blink * 0.85f;
                DrawCircle(pos, 1.3f + blink * 0.7f, pCol);
            }
            return;
        }

        // =====================================================================
        // 2. FASE INFALL (SUPERCHARGED GRAVITATIONAL ACCRETION & FRACTURE INVERSION, 3.8 DETIK)
        // =====================================================================
        if (State == RiftState.Infall)
        {
            float p = Mathf.Clamp(_stateTimer / InfallDuration, 0.0f, 1.0f);

            // Radius Event Horizon:
            // Pada p in [0.86, 0.98]: SUPERCRITICAL PRE-ERUPTION PINCH!
            // Horizon memampat ekstrem dari 46 px ke 14 px tepat sebelum Big Bang White Hole!
            float currentHorizonR = baseHorizonR;
            if (p >= 0.86f && p < 0.98f)
            {
                float pinchNorm = (p - 0.86f) / 0.12f;
                currentHorizonR = Mathf.Lerp(baseHorizonR, 14.0f, MathF.Sin(pinchNorm * Mathf.Pi * 0.5f));
            }
            else if (p >= 0.98f)
            {
                currentHorizonR = 14.0f;
            }
            else
            {
                // Pulsasi gelisah berfrekuensi tinggi saat energi membuncah
                float jitter = MathF.Sin(p * Mathf.Tau * 6.0f) * (2.2f * p);
                currentHorizonR = baseHorizonR + jitter;
            }

            // A. Selimut Gravitasi Luar (Violent Infall Lensing Glow)
            DrawCircle(Vector2.Zero, currentHorizonR * 2.1f, new Color(0.18f, 0.06f, 0.38f, 0.15f + p * 0.25f));
            DrawCircle(Vector2.Zero, currentHorizonR * 1.5f, new Color(0.10f, 0.45f, 0.65f, 0.22f + p * 0.38f));

            // B. Inti Black Hole Pekat (Tetap Hitam Menutup Grid Latar Belakang!)
            DrawCircle(Vector2.Zero, currentHorizonR, VoidCoreColor);

            // C. RETAKAN RUANG-WAKTU EVENT HORIZON (White-Hot Fractures across Event Horizon)
            if (p > 0.18f)
            {
                float crackNorm = (p - 0.18f) / 0.82f;
                float crackAlpha = Mathf.Clamp(crackNorm * 1.35f, 0.0f, 1.0f);
                float crackWidth = 1.3f + crackNorm * 2.8f;
                float radiusRatio = currentHorizonR / baseHorizonR;

                Color crackCol = IncandescentCrackColor.Lerp(WhiteHoleCoreColor, crackNorm * 0.65f);
                crackCol.A = crackAlpha;

                for (int i = 0; i < _horizonFractures.Count; i++)
                {
                    var f = _horizonFractures[i];
                    DrawLine(f.Start * radiusRatio, f.End * radiusRatio, crackCol, crackWidth);
                }

                // Titik-titik singularitas bocor di simpul retakan
                for (int i = 0; i < _horizonFractures.Count; i += 2)
                {
                    Vector2 node = _horizonFractures[i].End * radiusRatio;
                    DrawCircle(node, 1.8f + crackNorm * 2.2f, crackCol);
                }
            }

            // D. INTI BLINDING BURST DARI DALAM PUSAT SINGULARITAS (p > 0.72)
            if (p > 0.72f)
            {
                float burstNorm = (p - 0.72f) / 0.28f;
                float burstRadius = currentHorizonR * MathF.Pow(burstNorm, 1.6f);
                DrawCircle(Vector2.Zero, burstRadius, WhiteHoleCoreColor);
                DrawArc(Vector2.Zero, burstRadius + 1.5f, 0.0f, Mathf.Tau, 48, SupercriticalFlareColor, 2.5f, true);
            }

            // E. LENGAN SPIRAL HISAPAN AKRESI KUANTUM (14 Inward Accretion Vortex Arms)
            int vortexArms = 14;
            float suctionProgress = p * p;
            for (int v = 0; v < vortexArms; v++)
            {
                float armBaseAngle = _spinAngle + (v * (Mathf.Tau / vortexArms));
                float armOuterR = Mathf.Lerp(260.0f, currentHorizonR + 8.0f, suctionProgress);
                float armInnerR = currentHorizonR + 2.0f;

                // Lengkungan spiral logaritmik masuk ke horizon
                Vector2 pOuter = new Vector2(MathF.Cos(armBaseAngle + 0.85f), MathF.Sin(armBaseAngle + 0.85f)) * armOuterR;
                Vector2 pInner = new Vector2(MathF.Cos(armBaseAngle), MathF.Sin(armBaseAngle)) * armInnerR;

                Color armCol = HawkingRadiationCyan.Lerp(SupercriticalFlareColor, p);
                armCol.A = (0.25f + 0.75f * p) * (1.0f - (float)v / (vortexArms * 2.0f));
                DrawLine(pOuter, pInner, armCol, 1.8f + p * 2.2f);
            }

            // F. CINCIN FOTON PINCH & SUPERCRITICAL CORONA
            Color flareRingCol = SupercriticalFlareColor;
            flareRingCol.A = 0.85f + 0.15f * MathF.Sin(_pulseTimer * 16.0f);
            float ringThick = 2.4f + p * 4.5f;
            DrawArc(Vector2.Zero, currentHorizonR + 1.5f, 0.0f, Mathf.Tau, 64, flareRingCol, ringThick, true);

            // G. CINCIN KONTRAKSI GELOMBANG HISAP LUAR
            float contractR = Mathf.Lerp(320.0f, currentHorizonR, (p * 2.2f) % 1.0f);
            Color contractCol = new Color(0.7f, 2.8f, 4.5f, 0.45f * (1.0f - (p % 0.5f) * 2.0f));
            DrawArc(Vector2.Zero, contractR, 0.0f, Mathf.Tau, 48, contractCol, 1.8f, true);
            return;
        }

        // =====================================================================
        // 3. FASE ERUPTING (WHITE HOLE SUPERNOVA-SCALE OUTBURST, 1.35 DETIK)
        // =====================================================================
        if (State == RiftState.Erupting)
        {
            float p = Mathf.Clamp(_stateTimer / EruptionDuration, 0.0f, 1.0f);

            // Inti White Hole Blinding White-Silver
            float whiteR = baseHorizonR * (1.45f - 0.25f * p);
            DrawCircle(Vector2.Zero, whiteR * 2.2f, new Color(0.45f, 2.85f, 4.5f, 0.42f * (1.0f - p)));
            DrawCircle(Vector2.Zero, whiteR * 1.5f, new Color(5.5f, 5.0f, 4.2f, 0.75f * (1.0f - p * 0.4f)));
            DrawCircle(Vector2.Zero, whiteR, WhiteHoleCoreColor);

            // Cincin ledakan foton memancar keluar (Dual Shockwave)
            float shockRadius1 = Mathf.Lerp(whiteR, 480.0f, MathF.Sqrt(p));
            Color shockCol1 = new Color(5.5f, 5.2f, 4.5f, 1.0f - p);
            DrawArc(Vector2.Zero, shockRadius1, 0.0f, Mathf.Tau, 56, shockCol1, 4.2f * (1.0f - p * 0.6f), true);

            float shockRadius2 = Mathf.Lerp(whiteR, 320.0f, p * (2.0f - p));
            Color shockCol2 = new Color(0.75f, 3.2f, 5.0f, 0.85f * (1.0f - p));
            DrawArc(Vector2.Zero, shockRadius2, 0.0f, Mathf.Tau, 48, shockCol2, 2.6f * (1.0f - p * 0.5f), true);

            // Berkas sinar White Hole radial (Relativistic Light Beams)
            int rayCount = 20;
            for (int r = 0; r < rayCount; r++)
            {
                float rayAngle = _spinAngle * 0.4f + (r * (Mathf.Tau / rayCount));
                Vector2 dir = new Vector2(MathF.Cos(rayAngle), MathF.Sin(rayAngle));
                float rayLen = whiteR + (140.0f + 60.0f * (r % 3)) * (1.0f - p * 0.7f);
                DrawLine(dir * (whiteR * 0.85f), dir * rayLen, new Color(5.5f, 5.2f, 5.0f, 0.75f * (1.0f - p)), 2.8f);
            }
            return;
        }

        // =====================================================================
        // 4. FASE COOLING (PENENANGAN KEMBALI KE BLACK HOLE, 2.6 DETIK)
        // =====================================================================
        if (State == RiftState.Cooling)
        {
            float p = Mathf.Clamp(_stateTimer / CoolingDuration, 0.0f, 1.0f);
            float smoothP = p * p;

            // Inti mendingin dari putih kembali ke hitam pekat
            Color coolCore = WhiteHoleCoreColor.Lerp(VoidCoreColor, smoothP);
            DrawCircle(Vector2.Zero, baseHorizonR, coolCore);

            // Cincin putih meredup kembali menjadi cincin Hawking Cyan tipis
            Color coolRing = SupercriticalFlareColor.Lerp(HawkingRadiationCyan, smoothP);
            coolRing.A = Mathf.Lerp(1.0f, 0.85f, p);
            float ringW = Mathf.Lerp(4.5f, 1.8f, smoothP);
            DrawArc(Vector2.Zero, baseHorizonR + 1.2f, 0.0f, Mathf.Tau, 64, coolRing, ringW, true);
        }
    }
}
