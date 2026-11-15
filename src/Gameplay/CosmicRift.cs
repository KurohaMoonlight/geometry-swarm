using System;
using Godot;

namespace GeometrySwarm.Gameplay;

/// <summary>
/// Anomali Astrofisika Sabuk Dalam: Cosmic Rift (Supermassive Black Hole & White Hole).
/// Siklus Sinematik:
/// 1. Dormant: Black hole stabil dengan distorsi ruang melingkar & cincin foton emas tipis.
/// 2. Charging (8 Detik): Petir dan lidah api menjilat di horizon, asteroid terdekat tersedot mengorbit melingkari black hole.
/// 3. Supernova Blast (3 Detik): Ledakan supernova memancarkan shockwave raksasa setiap 0.5s, asteroid terlempar jauh,
///    dan inti berubah menjadi White Hole putih-perak mutlak.
/// 4. White Hole (5.5 Detik): Menolak materi keluar dengan gravitasi repulsif sebelum kembali ke Black Hole.
/// </summary>
public partial class CosmicRift : Node2D
{
    public enum RiftState
    {
        Dormant = 0,
        Charging = 1,
        SupernovaBlast = 2,
        WhiteHole = 3
    }

    [Export] public float OrbitRadius { get; set; } = 1550.0f;
    [Export] public float OrbitAngle { get; set; } = 0.0f;
    [Export] public float OrbitSpeed { get; set; } = 0.012f; // ~0.68 deg/sec
    [Export] public int RiftId { get; set; } = 0;

    public RiftState State { get; private set; } = RiftState.Dormant;

    // Durasi Fase
    public const float ChargeDuration = 8.0f;
    public const float SupernovaDuration = 3.0f;
    public const float WhiteHoleDuration = 5.5f;
    public const float DormantDuration = 22.0f;

    // Ukuran quad visual efek distorsi lensa gravitasi
    public const float VisualQuadSize = 960.0f;

    private float _stateTimer = 0.0f;
    private float _shockwavePulseTimer = 0.0f;
    private float _chargeProgress = 0.0f;
    private float _whiteHolePolarity = 0.0f;

    private BackBufferCopy? _backBuffer;
    private ColorRect? _riftRect;
    private ShaderMaterial? _riftMaterial;

    public override void _Ready()
    {
        // 1. Tempatkan pada posisi orbit awal
        UpdateOrbitPosition();

        // Rift 0 diatur agar langsung memicu animasi charge dalam 1.5 detik sejak game dimulai
        if (RiftId == 0)
        {
            _stateTimer = DormantDuration - 1.5f;
        }
        else
        {
            _stateTimer = 0.0f;
        }

        // 2. Bangun komponen shader distorsi ruang gravitasi
        try
        {
            var shader = GD.Load<Shader>("res://visual/cosmic_rift_distortion.gdshader");
            if (shader != null)
            {
                _riftMaterial = new ShaderMaterial { Shader = shader };
                _riftMaterial.SetShaderParameter("black_hole_radius", 0.102f);
                _riftMaterial.SetShaderParameter("lensing_strength", 1.65f);
                _riftMaterial.SetShaderParameter("swirl_strength", 2.40f);
                _riftMaterial.SetShaderParameter("swirl_speed", 0.35f);
                _riftMaterial.SetShaderParameter("photon_ring_width", 0.0030f);
                _riftMaterial.SetShaderParameter("photon_ring_intensity", 5.5f);
                _riftMaterial.SetShaderParameter("gold_bloom_width", 0.0100f);
                _riftMaterial.SetShaderParameter("gold_bloom_intensity", 3.4f);
                _riftMaterial.SetShaderParameter("photon_core_color", new Color(5.8f, 5.6f, 5.2f, 1.0f));
                _riftMaterial.SetShaderParameter("gold_bloom_color", new Color(3.4f, 2.3f, 0.58f, 1.0f));
                _riftMaterial.SetShaderParameter("charge_progress", 0.0f);
                _riftMaterial.SetShaderParameter("white_hole_polarity", 0.0f);

                // BackBufferCopy Viewport memastikan seluruh layar diambil tanpa batas terpotong (zero seams)
                _backBuffer = new BackBufferCopy
                {
                    Name = "RiftBackBuffer",
                    CopyMode = BackBufferCopy.CopyModeEnum.Viewport
                };
                AddChild(_backBuffer);

                _riftRect = new ColorRect
                {
                    Name = "RiftDistortionQuad",
                    Material = _riftMaterial,
                    CustomMinimumSize = new Vector2(VisualQuadSize, VisualQuadSize),
                    Size = new Vector2(VisualQuadSize, VisualQuadSize),
                    Position = new Vector2(-VisualQuadSize * 0.5f, -VisualQuadSize * 0.5f),
                    MouseFilter = Control.MouseFilterEnum.Ignore
                };
                AddChild(_riftRect);
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[CosmicRift._Ready] Gagal inisialisasi shader distorsi: {ex}");
        }
    }

    private void UpdateOrbitPosition()
    {
        Position = new Vector2(MathF.Cos(OrbitAngle), MathF.Sin(OrbitAngle)) * OrbitRadius;
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;

        // 1. Presesi Orbit Lambat di Sekitar (0,0)
        OrbitAngle += OrbitSpeed * dt;
        UpdateOrbitPosition();

        // Rift 1 & 2 tetap tenang di sabuk dalam sebagai black hole stabil (Zero cycle conflict)
        if (RiftId != 0)
        {
            State = RiftState.Dormant;
            _chargeProgress = 0.0f;
            _whiteHolePolarity = 0.0f;
            if (_riftRect != null && _riftRect.Visible) _riftRect.Visible = false;
            if (_backBuffer != null && _backBuffer.Visible) _backBuffer.Visible = false;
            return;
        }

        // 2. State Machine Siklus Kosmik
        _stateTimer += dt;

        Vector2 playerPos = PlayerCore.Instance?.GlobalPosition ?? Vector2.Zero;
        float distToPlayer = playerPos.DistanceTo(GlobalPosition);
        // Atenuasi jarak guncangan kamera: semakin jauh semakin ringan, semakin dekat semakin berat
        float cameraDistAtten = Mathf.Clamp(1.0f - (distToPlayer - 350.0f) / 3200.0f, 0.0f, 1.0f);

        // Optimasi GPU: Nonaktifkan shader & viewport backbuffer copy jika rift berada di luar layar
        bool isNearPlayer = distToPlayer < 1700.0f;
        if (_riftRect != null && _riftRect.Visible != isNearPlayer)
        {
            _riftRect.Visible = isNearPlayer;
        }
        if (_backBuffer != null && _backBuffer.Visible != isNearPlayer)
        {
            _backBuffer.Visible = isNearPlayer;
        }

        switch (State)
        {
            case RiftState.Dormant:
                _chargeProgress = 0.0f;
                _whiteHolePolarity = Mathf.Lerp(_whiteHolePolarity, 0.0f, dt * 2.0f);
                _riftMaterial?.SetShaderParameter("black_hole_radius", 0.102f);

                if (_stateTimer >= DormantDuration)
                {
                    State = RiftState.Charging;
                    _stateTimer = 0.0f;
                }
                break;

            case RiftState.Charging:
                float p = Mathf.Clamp(_stateTimer / ChargeDuration, 0.0f, 1.0f);
                _chargeProgress = p;
                _whiteHolePolarity = 0.0f;

                // Guncangan getaran gemuruh halus progresif (tidak membuat frame patah-patah)
                float rumbleShake = Mathf.Lerp(0.0f, 2.2f, p * p * p) * cameraDistAtten;
                if (rumbleShake > 0.05f)
                {
                    PlayerCamera.Instance?.TriggerShake(rumbleShake);
                }

                // Fisika: Asteroid di sekitar tersedot dan berputar mengorbit melingkari black hole
                CaptureAndOrbitNearbyAsteroids(p, dt);

                // Implosi / Pinching sesaat di 0.3s terakhir sebelum meledak Supernova
                if (p >= 0.94f)
                {
                    float pinchT = (p - 0.94f) / 0.06f;
                    float pinchedR = Mathf.Lerp(0.102f, 0.038f, MathF.Sin(pinchT * Mathf.Pi * 0.5f));
                    _riftMaterial?.SetShaderParameter("black_hole_radius", pinchedR);
                }
                else
                {
                    _riftMaterial?.SetShaderParameter("black_hole_radius", 0.102f);
                }

                if (_stateTimer >= ChargeDuration)
                {
                    // Transisi ke Supernova Blast!
                    State = RiftState.SupernovaBlast;
                    _stateTimer = 0.0f;
                    _shockwavePulseTimer = 0.0f;

                    // 1. Pemicu Bintang Aether: TIDAK terdorong, langsung hancur seketika menjadi Supernova,
                    //    menjadi Seed, dan langsung roaming menjauh sejauh mungkin dari Black Hole!
                    TriggerAetherSupernovaAndSeedEscape();

                    // 2. Lontarkan asteroid batu di sekitar SATU KALI dengan kecepatan tinggi & mulus tanpa patah-patah
                    LaunchNearbyAsteroidsSeamlessly();

                    // 3. Shockwave primer dahsyat pertama & getaran kamera
                    SwarmSimulation.Instance?.TriggerRiftSupernovaShockwave(GlobalPosition, 2800.0f, 4800.0f);
                    float blastShake = 28.0f * cameraDistAtten;
                    if (blastShake > 0.5f)
                    {
                        PlayerCamera.Instance?.TriggerShake(blastShake);
                    }
                }
                break;

            case RiftState.SupernovaBlast:
                _chargeProgress = 0.0f;
                // Inversi polaritas instan menjadi White Hole menyilaukan
                _whiteHolePolarity = Mathf.Lerp(_whiteHolePolarity, 1.0f, dt * 10.0f);
                _riftMaterial?.SetShaderParameter("black_hole_radius", 0.102f);

                // Player dan musuh otomatis memunculkan perisai neon anti-terpental menghadap ke black hole
                PlayerCore.Instance?.ActivateShockwaveShield(GlobalPosition, 0.45f);
                SwarmSimulation.Instance?.EnemyCore?.ActivateShockwaveShield(GlobalPosition, 0.45f);

                // Gelombang kejut visual berulang setiap 0.5 detik selama 3 detik (Murni visual echo, tanpa reset kecepatan asteroid!)
                _shockwavePulseTimer += dt;
                if (_shockwavePulseTimer >= 0.5f)
                {
                    _shockwavePulseTimer = 0.0f;

                    // Gelombang visual echo & getaran kamera sekunder halus (tanpa membebani CPU/GDScript)
                    SwarmSimulation.Instance?.TriggerVisualShockwaveEcho(GlobalPosition, 2800.0f);
                    float echoShake = 10.0f * cameraDistAtten;
                    if (echoShake > 0.4f)
                    {
                        PlayerCamera.Instance?.TriggerShake(echoShake);
                    }
                }

                if (_stateTimer >= SupernovaDuration)
                {
                    State = RiftState.WhiteHole;
                    _stateTimer = 0.0f;
                }
                break;

            case RiftState.WhiteHole:
                _chargeProgress = 0.0f;
                _riftMaterial?.SetShaderParameter("black_hole_radius", 0.102f);

                // Gaya tolak repulsif White Hole (mendorong materi agar tidak mendekat)
                ApplyWhiteHoleRepulsion(dt);

                // Mendingin perlahan di 2.0s terakhir durasi
                if (_stateTimer >= WhiteHoleDuration - 2.0f)
                {
                    float coolProgress = (_stateTimer - (WhiteHoleDuration - 2.0f)) / 2.0f;
                    _whiteHolePolarity = Mathf.Clamp(1.0f - coolProgress, 0.0f, 1.0f);
                }
                else
                {
                    _whiteHolePolarity = 1.0f;
                }

                if (_stateTimer >= WhiteHoleDuration)
                {
                    State = RiftState.Dormant;
                    _stateTimer = 0.0f;
                    _whiteHolePolarity = 0.0f;
                }
                break;
        }

        // 3. Kirim parameter dinamis ke shader
        _riftMaterial?.SetShaderParameter("charge_progress", _chargeProgress);
        _riftMaterial?.SetShaderParameter("white_hole_polarity", _whiteHolePolarity);
        _riftMaterial?.SetShaderParameter("swirl_speed", Mathf.Lerp(0.35f, 2.6f, _chargeProgress));
        _riftMaterial?.SetShaderParameter("lensing_strength", Mathf.Lerp(1.65f, 3.4f, _chargeProgress));
        _riftMaterial?.SetShaderParameter("swirl_strength", Mathf.Lerp(2.40f, 4.8f, _chargeProgress));
    }

    /// <summary>
    /// Menghisap asteroid di sekitar dan membuat mereka berputar mengorbit melingkari black hole selama charging.
    /// </summary>
    private void CaptureAndOrbitNearbyAsteroids(float progress, float dt)
    {
        var asteroids = Asteroid.ActiveAsteroids;
        for (int a = 0; a < asteroids.Count; a++)
        {
            var ast = asteroids[a];
            if (ast == null || !IsInstanceValid(ast) || !ast.IsActiveInTree) continue;

            Vector2 toRift = GlobalPosition - ast.CachedPosition;
            float dist = toRift.Length();

            if (dist < 1550.0f && dist > 1.0f)
            {
                Vector2 radDir = toRift / dist;
                Vector2 tanDir = new Vector2(-radDir.Y, radDir.X); // Vektor tangensial putaran berlawanan jarum jam

                // Radius orbit sasaran yang mengitari black hole (~270 - 450 px)
                float targetRadius = 270.0f + (MathF.Abs(ast.GetInstanceId()) % 5) * 45.0f;
                float radialDelta = dist - targetRadius;

                // Kecepatan putaran orbit meningkat pesat seiring bertambahnya charge
                float orbitSpeed = Mathf.Lerp(180.0f, 560.0f, progress);
                float radialForce = radialDelta * 3.6f * progress;

                Vector2 desiredVel = tanDir * orbitSpeed + radDir * radialForce;
                ast.LinearVelocity = ast.LinearVelocity.Lerp(desiredVel, dt * (2.4f + progress * 4.2f));
                ast.AngularVelocity = Mathf.Lerp(ast.AngularVelocity, 1.4f, dt * 2.8f);
            }
        }
    }

    /// <summary>
    /// Pemicu instan untuk Bintang Aether saat Black Hole meledak Supernova:
    /// Bintang Aether TIDAK terdorong, melainkan langsung hancur seketika menjadi Supernova di tempat,
    /// bertransformasi menjadi Aether Seed, dan langsung mulai roaming menjauh sejauh mungkin dari Black Hole.
    /// </summary>
    private void TriggerAetherSupernovaAndSeedEscape()
    {
        var asteroids = Asteroid.ActiveAsteroids;
        for (int a = 0; a < asteroids.Count; a++)
        {
            var ast = asteroids[a];
            if (ast == null || !IsInstanceValid(ast)) continue;

            if (ast.IsEnergyAsteroid && !ast.IsInSeedCycle)
            {
                float dist = ast.CachedPosition.DistanceTo(GlobalPosition);
                if (dist < 3600.0f)
                {
                    ast.TriggerSupernovaFromBlackHole(GlobalPosition);
                }
            }
        }
    }

    /// <summary>
    /// Melontarkan asteroid batu di sekitar SATU KALI SAJA saat ledakan supernova black hole.
    /// Asteroid meluncur mulus, konsisten, dan bebas dari lonjakan kecepatan patah-patah.
    /// </summary>
    private void LaunchNearbyAsteroidsSeamlessly()
    {
        var asteroids = Asteroid.ActiveAsteroids;
        for (int a = 0; a < asteroids.Count; a++)
        {
            var ast = asteroids[a];
            if (ast == null || !IsInstanceValid(ast) || !ast.IsActiveInTree || ast.IsEnergyAsteroid) continue;

            Vector2 outward = ast.CachedPosition - GlobalPosition;
            float dist = outward.Length();

            if (dist < 3400.0f)
            {
                Vector2 dir = dist > 1.0f ? outward / dist : Vector2.Right;
                // Kecepatan awal yang kuat, diluncurkan sekali saja sehingga trajectory 100% mulus
                float blastSpeed = (float)GD.RandRange(2800.0, 3600.0);
                ast.ApplySupernovaBlast(dir * blastSpeed, SupernovaDuration + WhiteHoleDuration + 4.0f);
            }
        }
    }

    /// <summary>
    /// Medan gaya repulsif White Hole yang mendorong partikel & asteroid menjauh.
    /// Memudar dengan halus di 2.0s terakhir agar transisi kembali ke Black Hole bebas lonjakan fisika.
    /// </summary>
    private void ApplyWhiteHoleRepulsion(float dt)
    {
        float coolMult = 1.0f;
        if (_stateTimer >= WhiteHoleDuration - 2.0f)
        {
            float coolProgress = (_stateTimer - (WhiteHoleDuration - 2.0f)) / 2.0f;
            coolMult = Mathf.Clamp(1.0f - coolProgress, 0.0f, 1.0f);
        }

        if (coolMult <= 0.01f) return;

        var asteroids = Asteroid.ActiveAsteroids;
        for (int a = 0; a < asteroids.Count; a++)
        {
            var ast = asteroids[a];
            if (ast == null || !IsInstanceValid(ast)) continue;

            Vector2 outward = ast.CachedPosition - GlobalPosition;
            float dist = outward.Length();

            if (dist < 2600.0f && dist > 1.0f)
            {
                Vector2 dir = outward / dist;
                float repulseForce = 520.0f * coolMult * (1.0f - dist / 2600.0f);
                ast.LinearVelocity += dir * repulseForce * dt;
            }
        }
    }
}
