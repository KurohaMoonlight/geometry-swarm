using System;
using GeometrySwarm.Gameplay;
using Godot;

namespace GeometrySwarm.Audio;

/// <summary>
/// Generator audio prosedural untuk mesin pendorong kapal induk (The Hive Core).
/// Desain Audio:
/// 1. IDLE (Melayang diam): Hening total (0 dB).
/// 2. GERAK / CRUISING: Dengungan mesin halus proporsional dengan kecepatan saat meluncur di angkasa.
/// 3. THRUST AKTIF (WASD): Dengungan sub-bass bulat & bertenaga (54 Hz) tanpa desis/kemresek.
/// 4. PRIMING BOOST [SPACE]: Dengungan rendah berdenyut bertingkat ("ngung... nguuung... NGUUUNG", 58-108 Hz).
///    Puncak volume seimbang dengan volume thruster normal (0.34).
/// 5. FULL PLASMA BOOST:
///    - Hentakan bass thump awal yang punchy (sub-kick 75 -> 30 Hz).
///    - Dengungan plasma tebal disertai suara "bsss" uap air/steam yang halus & samar.
/// 6. AUTO-RECOVERY & BUFFER SAFETY:
///    - Buffer 350ms & auto-restart jika audio thread sempat underflow agar suara TIDAK PERNAH mati di tengah jalan.
/// </summary>
public partial class EngineThrusterAudio : AudioStreamPlayer
{
    private AudioStreamGeneratorPlayback? _playback;
    private AudioStreamGenerator? _generator;
    private PlayerCore? _playerCore;

    private const float SampleRate = 44100.0f;
    private const float TwoPi = Mathf.Tau;

    // Phases
    private float _phaseSub = 0.0f;
    private float _phaseCharge = 0.0f;
    private float _phaseThump = 0.0f;
    private float _phasePlasmaSub = 0.0f;
    private float _phasePlasmaHarm = 0.0f;

    // Steam Vapor Filter States (2-pole LP + 1-pole HP Stereo)
    private float _steamLp1L = 0.0f;
    private float _steamLp2L = 0.0f;
    private float _steamHpL = 0.0f;
    private float _steamLp1R = 0.0f;
    private float _steamLp2R = 0.0f;
    private float _steamHpR = 0.0f;

    // PRNG states for soft steam hiss
    private uint _rngL = 123456789;
    private uint _rngR = 987654321;

    // Filter coefficients for soft steam vapor
    private const float SteamAlphaLp = 0.29f; // LP ~2100 Hz
    private const float SteamAlphaHp = 0.10f; // HP ~750 Hz

    // Envelopes & States
    private float _currentVolume = 0.0f;
    private float _blastThump = 0.0f;
    private bool _wasPriming = false;

    // Smooth parameters
    private float _chargeEnvelope = 0.0f;
    private float _chargeFreq = 58.0f;
    private float _subVolume = 0.0f;
    private float _plasmaVolume = 0.0f;

    public override void _Ready()
    {
        _generator = new AudioStreamGenerator
        {
            MixRate = SampleRate,
            BufferLength = 0.35f // 350ms buffer untuk keandalan tinggi tanpa drop
        };

        Stream = _generator;
        Bus = "Master";
        Play();

        _playback = GetStreamPlayback() as AudioStreamGeneratorPlayback;
        _playerCore = GetParent() as PlayerCore ?? PlayerCore.Instance;

        // Pre-fill buffer awal agar tidak pernah terjadi starvation saat start
        PreFillBuffer();
    }

    private void PreFillBuffer()
    {
        if (_playback == null) return;
        int available = _playback.GetFramesAvailable();
        for (int i = 0; i < available; i++)
        {
            _playback.PushFrame(Vector2.Zero);
        }
    }

    public override void _Process(double delta)
    {
        // Auto-recovery jika audio server sempat underflow atau stop
        if (!Playing)
        {
            Play();
            _playback = GetStreamPlayback() as AudioStreamGeneratorPlayback;
            PreFillBuffer();
        }

        if (_playback == null)
        {
            _playback = GetStreamPlayback() as AudioStreamGeneratorPlayback;
            if (_playback == null) return;
        }

        if (_playerCore == null || !IsInstanceValid(_playerCore))
        {
            _playerCore = GetParent() as PlayerCore ?? PlayerCore.Instance;
        }

        float dt = (float)delta;
        UpdateParameters(dt);
        SynthesizeAudio();
    }

    private void UpdateParameters(float dt)
    {
        bool isThrusting = false;
        bool isPriming = false;
        bool isBoosting = false;
        bool isAlive = true;
        float speed = 0.0f;
        float speedRatio = 0.0f;
        float primingProgress = 0.0f;

        if (_playerCore != null && IsInstanceValid(_playerCore))
        {
            isThrusting = _playerCore.IsThrusting;
            isPriming = _playerCore.IsBoostPriming;
            isBoosting = _playerCore.IsBoosting || (_playerCore.IsEscapePod && _playerCore.EscapeBurstTimer > 0.0f);
            primingProgress = _playerCore.BoostPrimingProgress;
            isAlive = _playerCore.Health > 0.0f;

            speed = _playerCore.Velocity.Length();
            speedRatio = Mathf.Clamp(speed / MathF.Max(1.0f, _playerCore.MaxSpeed), 0.0f, 1.2f);
        }

        if (!isAlive)
        {
            _currentVolume = Mathf.MoveToward(_currentVolume, 0.0f, dt * 5.0f);
            return;
        }

        // Deteksi transisi dari Priming -> Full Boost (Memicu Bass Thump)
        if (_wasPriming && isBoosting && !isPriming)
        {
            _blastThump = 1.0f;
        }
        _wasPriming = isPriming;

        // 1. Logika Suara Priming Rendah ("ngung... nguuung... NGUUUNG")
        float targetChargeEnv = 0.0f;
        float targetChargeFreq = 58.0f;

        if (isPriming)
        {
            float p = primingProgress;
            float pulseWave;

            if (p < 0.33f)
            {
                float t1 = p / 0.33f;
                pulseWave = MathF.Sin(t1 * Mathf.Pi);
                targetChargeFreq = Mathf.Lerp(58.0f, 68.0f, t1);
            }
            else if (p < 0.66f)
            {
                float t2 = (p - 0.33f) / 0.33f;
                pulseWave = MathF.Sin(t2 * Mathf.Pi);
                targetChargeFreq = Mathf.Lerp(70.0f, 84.0f, t2);
            }
            else
            {
                float t3 = (p - 0.66f) / 0.34f;
                pulseWave = MathF.Sin(t3 * Mathf.Pi * 0.5f);
                targetChargeFreq = Mathf.Lerp(86.0f, 108.0f, t3);
            }

            // Puncak volume sama rata dengan thruster WASD (0.34)
            targetChargeEnv = pulseWave * 0.34f;
        }

        _chargeEnvelope = Mathf.Lerp(_chargeEnvelope, targetChargeEnv, dt * 20.0f);
        _chargeFreq = Mathf.Lerp(_chargeFreq, targetChargeFreq, dt * 22.0f);

        // 2. Logika Dengungan Bass Normal (WASD & Cruising Motion)
        // - Jika menekan WASD: Volume penuh (0.34f)
        // - Jika sedang meluncur/gerak terus (coasting/cruising): Volume proporsional dengan kecepatan (0.08f - 0.22f)
        // - Jika diam (speed < 18 px/s & tidak ada input): Hening total (0.0f)
        float targetSubVol = 0.0f;
        if (!isBoosting && !isPriming)
        {
            if (isThrusting)
            {
                targetSubVol = 0.34f;
            }
            else if (speed > 18.0f)
            {
                // Suara dengungan mesin saat meluncur terus di angkasa
                targetSubVol = Mathf.Lerp(0.08f, 0.22f, speedRatio);
            }
        }
        _subVolume = Mathf.Lerp(_subVolume, targetSubVol, dt * 10.0f);

        // 3. Logika Plasma Full Boost (SPACE rilis)
        float targetPlasmaVol = (isBoosting && !isPriming) ? 0.44f : 0.0f;
        _plasmaVolume = Mathf.Lerp(_plasmaVolume, targetPlasmaVol, dt * 16.0f);

        // Peluruhan bass thump awal detonasi (decay natural)
        if (_blastThump > 0.001f)
        {
            _blastThump = Mathf.MoveToward(_blastThump, 0.0f, dt * 4.2f);
        }

        // Master Volume Gate
        bool hasActiveAudio = isThrusting || isPriming || isBoosting || (speed > 18.0f) || _blastThump > 0.01f || _subVolume > 0.01f;
        float targetMasterVol = hasActiveAudio ? 1.0f : 0.0f;
        _currentVolume = Mathf.MoveToward(_currentVolume, targetMasterVol, dt * 10.0f);
    }

    private void SynthesizeAudio()
    {
        if (_playback == null) return;

        int framesToPush = _playback.GetFramesAvailable();
        if (framesToPush <= 0) return;

        // Jika dalam kondisi hening total (Idle diam), dorong frame hening & hemat CPU
        if (_currentVolume < 0.001f && _subVolume < 0.001f && _chargeEnvelope < 0.001f && _plasmaVolume < 0.001f && _blastThump < 0.001f)
        {
            for (int i = 0; i < framesToPush; i++)
            {
                _playback.PushFrame(Vector2.Zero);
            }
            return;
        }

        float subDelta = 54.0f / SampleRate;
        float chargeDelta = _chargeFreq / SampleRate;
        float plasmaSubDelta = 52.0f / SampleRate;
        float plasmaHarmDelta = 104.0f / SampleRate;

        for (int i = 0; i < framesToPush; i++)
        {
            float centerMono = 0.0f;
            float leftStereo = 0.0f;
            float rightStereo = 0.0f;

            // -------------------------------------------------------------
            // A. DENGUNGAN BASS HALUS (WASD & CRUISING)
            // -------------------------------------------------------------
            if (_subVolume > 0.002f)
            {
                _phaseSub += subDelta;
                if (_phaseSub >= 1.0f) _phaseSub -= 1.0f;

                float subFund = MathF.Sin(_phaseSub * TwoPi);
                float sub2nd = MathF.Sin(_phaseSub * 2.0f * TwoPi) * 0.22f;
                float subLow = MathF.Sin(_phaseSub * 0.5f * TwoPi) * 0.15f;

                centerMono += (subFund + sub2nd + subLow) * _subVolume;
            }

            // -------------------------------------------------------------
            // B. PRIMING RENDAH BERDENYUT ("ngung... nguuung... NGUUUNG")
            // -------------------------------------------------------------
            if (_chargeEnvelope > 0.002f)
            {
                _phaseCharge += chargeDelta;
                if (_phaseCharge >= 1.0f) _phaseCharge -= 1.0f;

                // Resonansi rongga bodi "ngung": nada rendah tebal (56-108 Hz)
                float f1 = MathF.Sin(_phaseCharge * TwoPi);
                float f2 = MathF.Sin(_phaseCharge * 2.0f * TwoPi) * 0.32f;
                float f3 = MathF.Sin(_phaseCharge * 3.0f * TwoPi) * 0.10f;
                float ngungTone = (f1 + f2 + f3) * 0.82f;

                centerMono += ngungTone * _chargeEnvelope;
            }

            // -------------------------------------------------------------
            // C. HENTAKAN BASS THUMP AWAL
            // -------------------------------------------------------------
            if (_blastThump > 0.002f)
            {
                // Sub-kick pitch dive: dari 75 Hz meluncur ke 30 Hz
                float thumpPitch = Mathf.Lerp(30.0f, 75.0f, _blastThump);
                _phaseThump += thumpPitch / SampleRate;
                if (_phaseThump >= 1.0f) _phaseThump -= 1.0f;

                float thump = MathF.Sin(_phaseThump * TwoPi) * _blastThump * 0.58f;
                centerMono += thump;
            }

            // -------------------------------------------------------------
            // D. SUSTAINED PLASMA BEAM: BASS TEBAL + "BSSS SAMAR" UAP AIR
            // -------------------------------------------------------------
            if (_plasmaVolume > 0.002f)
            {
                _phasePlasmaSub += plasmaSubDelta;
                if (_phasePlasmaSub >= 1.0f) _phasePlasmaSub -= 1.0f;

                _phasePlasmaHarm += plasmaHarmDelta;
                if (_phasePlasmaHarm >= 1.0f) _phasePlasmaHarm -= 1.0f;

                // Nada bass plasma tebal
                float pSub = MathF.Sin(_phasePlasmaSub * TwoPi);
                float pHarm = MathF.Sin(_phasePlasmaHarm * TwoPi) * 0.28f;
                float plasmaBass = (pSub + pHarm) * 0.40f;
                centerMono += plasmaBass * _plasmaVolume;

                // Suara "bsss samar seperti air yang jadi uap" (Bandpass Steam Vapor)
                float rawNoiseL = NextNoise(ref _rngL);
                float rawNoiseR = NextNoise(ref _rngR);

                _steamLp1L += SteamAlphaLp * (rawNoiseL - _steamLp1L);
                _steamLp2L += SteamAlphaLp * (_steamLp1L - _steamLp2L);
                _steamHpL += SteamAlphaHp * (_steamLp2L - _steamHpL);
                float steamL = (_steamLp2L - _steamHpL);

                _steamLp1R += SteamAlphaLp * (rawNoiseR - _steamLp1R);
                _steamLp2R += SteamAlphaLp * (_steamLp1R - _steamLp2R);
                _steamHpR += SteamAlphaHp * (_steamLp2R - _steamHpR);
                float steamR = (_steamLp2R - _steamHpR);

                // Dibuat sangat samar (0.030f)
                leftStereo += steamL * 0.030f * _plasmaVolume;
                rightStereo += steamR * 0.030f * _plasmaVolume;
            }

            // Gabungkan center mono dan stereo steam vapor
            float left = (centerMono + leftStereo) * _currentVolume;
            float right = (centerMono + rightStereo) * _currentVolume;

            // Warm Analog Saturation (Tanh)
            left = MathF.Tanh(left * 1.30f);
            right = MathF.Tanh(right * 1.30f);

            _playback.PushFrame(new Vector2(left, right));
        }
    }

    private static float NextNoise(ref uint state)
    {
        // 32-bit Xorshift PRNG (-1.0f to 1.0f)
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return (state * 4.6566129e-10f) - 1.0f;
    }
}
