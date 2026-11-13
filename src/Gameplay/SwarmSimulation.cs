using System;
using System.Collections.Generic;
using Arch.Core;
using GeometrySwarm.Core.ECS.Components;
using GeometrySwarm.Core.ECS.Systems;
using GeometrySwarm.Visual;
using Godot;

namespace GeometrySwarm.Gameplay;

/// <summary>
/// Driver utama simulasi kawanan Arch ECS.
/// - Menghubungkan seluruh varian Attacker (Dart, Shuriken, Bor, Boomerang).
/// - Mengatur Guardian di ring terluar (220px) dengan 12 Sektor Pertahanan adaptif.
/// - Mengatur Super Giga Drill Break murni dari susunan fisik swarm drone bor (Zero Hologram).
/// </summary>
public partial class SwarmSimulation : Node2D
{
    [Export] public int InitialSwarmCount { get; set; } = 550;

    private World _world = null!;
    private BoidSystem _boidSystem = null!;
    private QueryDescription _allSwarmQuery;

    private PlayerCore _playerCore = null!;
    private SwarmMultiMeshRenderer _renderer = null!;

    // Warna neon dasar per peran
    private static readonly Color DartColor = new(2.2f, 2.4f, 0.4f, 1.0f);     // Kuning/Lime Terang
    private static readonly Color ShurikenColor = new(2.6f, 0.4f, 2.0f, 1.0f); // Magenta Elektrik
    private static readonly Color DrillColor = new(3.0f, 0.8f, 0.3f, 1.0f);    // Oranye/Crimson Bor
    private static readonly Color CrescentColor = new(0.4f, 2.8f, 1.2f, 1.0f); // Hijau Zamrud
    private static readonly Color GuardianColor = new(0.3f, 2.2f, 2.8f, 1.0f); // Cyan Perisai ]]

    // Status hitungan unit real-time
    public int EntityCount => _entityCount;
    public int OrbitingCount => _boidSystem.OrbitingCount;
    public int LaunchedCount => _boidSystem.LaunchedCount;
    public int ReturningCount => _boidSystem.ReturningCount;
    public int CoreCellCount => _playerCore?.CoreCellCount ?? 1;

    public int DrillDroneCount { get; private set; } = 0;
    public int GuardianCount { get; private set; } = 0;
    public float DrillChargeRatio => Mathf.Clamp(_drillChargeTimer / MaxDrillChargeTime, 0.0f, 1.0f);
    public bool IsChargingDrill => _isChargingDrill;

    private int _entityCount = 0;

    // Timer laju tembakan kontinu (LMB stream fire)
    private float _fireCooldown = 0.0f;
    private const float StreamFireInterval = 0.035f;

    // Parameter Super Giga Drill Break (Swarm-Formed)
    private bool _isChargingDrill = false;
    private float _drillChargeTimer = 0.0f;
    private const float MaxDrillChargeTime = 1.2f;
    private float _gigaDrillSpinTime = 0.0f;

    private bool _isGigaDrillActive = false;
    private Vector2 _gigaDrillTipPos = Vector2.Zero;
    private Vector2 _gigaDrillAimDir = Vector2.Right;
    private Vector2 _gigaDrillVel = Vector2.Zero;
    private float _gigaDrillFlightTimer = 0.0f;

    public override void _Ready()
    {
        // 1. Inisialisasi Arch ECS
        _world = World.Create();
        _boidSystem = new BoidSystem(_world);
        _allSwarmQuery = new QueryDescription().WithAll<PositionComponent, VelocityComponent, SwarmUnitComponent, VisualComponent>();

        // 2. Ambil referensi node
        _playerCore = GetNode<PlayerCore>("PlayerCore");
        _renderer = GetNode<SwarmMultiMeshRenderer>("SwarmRenderer");

        // 3. Spawn kawanan awal mengitari Player dengan komposisi seimbang
        SpawnBalancedSwarm(InitialSwarmCount);
    }

    public void SpawnBalancedSwarm(int totalCount)
    {
        Vector2 corePos = _playerCore.GlobalPosition;

        for (int i = 0; i < totalCount; i++)
        {
            float initialAngle = GD.Randf() * Mathf.Tau;

            // Distribusi peran: 40% Dart, 20% Shuriken, 16% Drill, 12% Crescent, 12% Guardian
            SwarmRole role;
            Color color;
            float scale = 0.8f;
            float orbitDist = 55.0f + GD.Randf() * 115.0f; // Pita dalam untuk attacker
            int mod = i % 25;

            if (mod < 10)
            {
                role = SwarmRole.DartNeedle;
                color = DartColor;
            }
            else if (mod < 15)
            {
                role = SwarmRole.GigaShuriken;
                color = ShurikenColor;
                scale = 0.9f;
            }
            else if (mod < 19)
            {
                role = SwarmRole.DrillDrone;
                color = DrillColor;
                scale = 0.95f;
                DrillDroneCount++;
            }
            else if (mod < 22)
            {
                role = SwarmRole.CrescentBoomerang;
                color = CrescentColor;
                scale = 0.85f;
            }
            else
            {
                // Guardian selalu berada di RING TERLUAR (220px)
                role = SwarmRole.Guardian;
                color = GuardianColor;
                scale = 1.15f;
                orbitDist = 220.0f;
                GuardianCount++;
            }

            float orbitSpeed = (GD.Randf() > 0.45f ? 1.0f : -1.0f) * (0.8f + GD.Randf() * 0.7f);
            Vector2 spawnPos = corePos + new Vector2(MathF.Cos(initialAngle), MathF.Sin(initialAngle)) * orbitDist;

            _world.Create(
                new PositionComponent(spawnPos),
                new VelocityComponent(Vector2.Zero),
                new SwarmUnitComponent(role, 0, orbitDist, orbitSpeed),
                new VisualComponent(color, scale)
            );

            _entityCount++;
        }
    }

    public void AddSpecificSwarm(SwarmRole role, int count)
    {
        Vector2 corePos = _playerCore.GlobalPosition;
        Color color = role switch
        {
            SwarmRole.DartNeedle => DartColor,
            SwarmRole.GigaShuriken => ShurikenColor,
            SwarmRole.DrillDrone => DrillColor,
            SwarmRole.CrescentBoomerang => CrescentColor,
            SwarmRole.Guardian => GuardianColor,
            _ => DartColor
        };

        for (int i = 0; i < count; i++)
        {
            float orbitDist = (role == SwarmRole.Guardian) ? 220.0f : 55.0f + GD.Randf() * 115.0f;
            float orbitSpeed = (GD.Randf() > 0.45f ? 1.0f : -1.0f) * (0.8f + GD.Randf() * 0.7f);
            float initialAngle = GD.Randf() * Mathf.Tau;
            Vector2 spawnPos = corePos + new Vector2(MathF.Cos(initialAngle), MathF.Sin(initialAngle)) * orbitDist;

            float scale = (role == SwarmRole.Guardian) ? 1.15f : 0.85f;
            if (role == SwarmRole.DrillDrone) DrillDroneCount++;
            if (role == SwarmRole.Guardian) GuardianCount++;

            _world.Create(
                new PositionComponent(spawnPos),
                new VelocityComponent(Vector2.Zero),
                new SwarmUnitComponent(role, 0, orbitDist, orbitSpeed),
                new VisualComponent(color, scale)
            );

            _entityCount++;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        Vector2 corePos = _playerCore.GlobalPosition;
        Vector2 coreVel = _playerCore.Velocity;
        Vector2 mousePos = GetGlobalMousePosition();

        _gigaDrillSpinTime += dt;

        // 1. Tembakan Beruntun (Tahan Klik Kiri - Stream Fire)
        if (_fireCooldown > 0.0f) _fireCooldown -= dt;

        if (Input.IsMouseButtonPressed(MouseButton.Left) && !_isChargingDrill && _fireCooldown <= 0.0f)
        {
            FireSwarmStream(mousePos, count: 2);
            _fireCooldown = StreamFireInterval;
        }

        // 2. Mekanik Charge & Formasi Super Giga Drill Break (Tahan Klik Kanan)
        HandleGigaDrillCharge(dt, corePos, mousePos);

        // 3. Update Penerbangan Super Giga Drill Break yang diluncurkan
        UpdateActiveGigaDrill(dt);

        // 4. Jalankan simulasi Boids Flocking, Guardian 12 Sektor, & State Machine
        _boidSystem.Update(dt, corePos, coreVel, mousePos);

        // 5. Update MultiMesh GPU buffer per peran
        _renderer.ResetCounters();
        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
        {
            _renderer.SetInstance((int)swarm.Role, pos.Value, vel.Value, swarm.SpinAngle, vis.Color, vis.Scale);
        });
        _renderer.FlushVisibleCounts();
    }

    private void HandleGigaDrillCharge(float dt, Vector2 corePos, Vector2 mousePos)
    {
        if (Input.IsMouseButtonPressed(MouseButton.Right) && !_isGigaDrillActive)
        {
            _isChargingDrill = true;
            _drillChargeTimer += dt;

            Vector2 toMouse = mousePos - corePos;
            Vector2 aimDir = toMouse.LengthSquared() > 1.0f ? toMouse.Normalized() : Vector2.Right;
            Vector2 tangent = new Vector2(-aimDir.Y, aimDir.X);
            float chargeProgress = DrillChargeRatio;

            // Ujung depan kerucut bor raksasa
            Vector2 tipPos = corePos + aimDir * (65.0f + chargeProgress * 30.0f);
            float drillLength = 70.0f + DrillDroneCount * 0.9f;
            float drillBaseRadius = 32.0f + DrillDroneCount * 0.45f;

            // =========================================================================
            // SUSUNAN FISIK SWARM MENJADI SUPER GIGA DRILL (ZERO HOLOGRAM!)
            // Setiap drone bor mengambil slot parametrik dalam kerucut bor berputar 3D/2D
            // =========================================================================
            int drillIdx = 0;
            _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
            {
                if (swarm.Role != SwarmRole.DrillDrone) return;

                float t = (DrillDroneCount > 1) ? (float)drillIdx / (DrillDroneCount - 1) : 0.0f; // 0 = di tip, 1 = di dasar
                float d = t * drillLength;
                float r = t * drillBaseRadius;

                // 3 alur heliks spiral berputar kencang
                int flute = drillIdx % 3;
                float spiralAngle = flute * (Mathf.Tau / 3.0f) + t * (Mathf.Tau * 2.5f) + _gigaDrillSpinTime * 28.0f;

                Vector2 slotPos = (tipPos - aimDir * d) 
                                + (tangent * (r * MathF.Cos(spiralAngle))) 
                                + (aimDir * (0.3f * r * MathF.Sin(spiralAngle)));

                // Drone melesat dan mengunci posisinya ke slot kerucut bor
                pos.Value = pos.Value.Lerp(slotPos, MathF.Min(1.0f, 26.0f * dt));
                vel.Value = (slotPos - pos.Value) * 32.0f;

                // Hadapkan moncong bor drone sejajar alur spiral kerucut
                swarm.SpinAngle = aimDir.Angle() + Mathf.Pi * 0.5f + MathF.Sin(spiralAngle) * 0.45f;

                // Warna memancarkan panas plasma dari ujung putih hingga pangkal oranye
                vis.Color = (drillIdx < 4) 
                    ? new Color(3.8f, 3.8f, 2.2f, 1.0f) 
                    : new Color(3.2f, 1.1f, 0.3f, 1.0f);

                drillIdx++;
            });
        }
        else if (_isChargingDrill)
        {
            // Tombol Klik Kanan dilepaskan -> LUNCURKAN SUPER GIGA DRILL BREAK!
            _isChargingDrill = false;
            float chargeProgress = DrillChargeRatio;

            Vector2 toMouse = mousePos - corePos;
            Vector2 aimDir = toMouse.LengthSquared() > 1.0f ? toMouse.Normalized() : Vector2.Right;

            if (chargeProgress >= 0.25f && DrillDroneCount > 0)
            {
                LaunchSuperGigaDrill(corePos, aimDir, chargeProgress);
            }
            else
            {
                // Klik kanan singkat -> semburan salvo biasa
                FireSwarmVolley(mousePos, 25);
            }

            _drillChargeTimer = 0.0f;
        }
    }

    private void LaunchSuperGigaDrill(Vector2 corePos, Vector2 aimDir, float chargeProgress)
    {
        _isGigaDrillActive = true;
        _gigaDrillTipPos = corePos + aimDir * 75.0f;
        _gigaDrillAimDir = aimDir;
        _gigaDrillVel = aimDir * (1950.0f + chargeProgress * 350.0f);
        _gigaDrillFlightTimer = 1.35f;

        // Tandai seluruh drone bor masuk mode peluru formasi giga drill
        _world.Query(in _allSwarmQuery, (ref SwarmUnitComponent swarm) =>
        {
            if (swarm.Role == SwarmRole.DrillDrone)
            {
                swarm.State = SwarmState.Launched;
                swarm.TargetDirection = aimDir;
                swarm.LifeTime = 0.0f;
                swarm.MaxLifeTime = 1.35f;
            }
        });
    }

    private void UpdateActiveGigaDrill(float dt)
    {
        if (!_isGigaDrillActive) return;

        _gigaDrillFlightTimer -= dt;
        _gigaDrillTipPos += _gigaDrillVel * dt;

        Vector2 tangent = new Vector2(-_gigaDrillAimDir.Y, _gigaDrillAimDir.X);
        float drillLength = 70.0f + DrillDroneCount * 0.9f;
        float drillBaseRadius = 32.0f + DrillDroneCount * 0.45f;

        // Pertahankan susunan kerucut bor raksasa saat terbang melesat supersonik
        int drillIdx = 0;
        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm, ref VisualComponent vis) =>
        {
            if (swarm.Role != SwarmRole.DrillDrone || swarm.State != SwarmState.Launched) return;

            float t = (DrillDroneCount > 1) ? (float)drillIdx / (DrillDroneCount - 1) : 0.0f;
            float d = t * drillLength;
            float r = t * drillBaseRadius;

            int flute = drillIdx % 3;
            float spiralAngle = flute * (Mathf.Tau / 3.0f) + t * (Mathf.Tau * 2.5f) + _gigaDrillSpinTime * 42.0f;

            Vector2 targetPos = (_gigaDrillTipPos - _gigaDrillAimDir * d) 
                              + (tangent * (r * MathF.Cos(spiralAngle))) 
                              + (_gigaDrillAimDir * (0.3f * r * MathF.Sin(spiralAngle)));

            pos.Value = targetPos;
            vel.Value = _gigaDrillVel;
            swarm.SpinAngle = _gigaDrillAimDir.Angle() + Mathf.Pi * 0.5f + MathF.Sin(spiralAngle) * 0.5f;

            vis.Color = (drillIdx < 4) 
                ? new Color(4.0f, 4.0f, 2.5f, 1.0f) 
                : new Color(3.4f, 1.2f, 0.4f, 1.0f);

            drillIdx++;
        });

        // Waktu tembus habis -> Formasi mata bor pecah dan terbang pulang ke Core
        if (_gigaDrillFlightTimer <= 0.0f)
        {
            _isGigaDrillActive = false;
            _world.Query(in _allSwarmQuery, (ref VelocityComponent vel, ref SwarmUnitComponent swarm) =>
            {
                if (swarm.Role == SwarmRole.DrillDrone && swarm.State == SwarmState.Launched)
                {
                    swarm.State = SwarmState.Returning;
                    swarm.LifeTime = 0.0f;
                    // Terlempar menyebar ke samping
                    float scatter = (GD.Randf() - 0.5f) * Mathf.Tau;
                    vel.Value = new Vector2(MathF.Cos(scatter), MathF.Sin(scatter)) * 400.0f;
                }
            });
        }
    }

    /// <summary>
    /// Menembakkan drone secara beruntun (Stream Fire)
    /// </summary>
    public void FireSwarmStream(Vector2 targetPos, int count = 2)
    {
        int fired = 0;
        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm) =>
        {
            if (fired >= count) return;
            if (swarm.State != SwarmState.Orbiting) return;
            // Guardian dan Drill tidak ditembakkan via Stream biasa
            if (swarm.Role == SwarmRole.Guardian || swarm.Role == SwarmRole.DrillDrone) return;

            Vector2 toTarget = targetPos - pos.Value;
            if (toTarget.LengthSquared() < 1.0f) return;

            float spreadAngle = (GD.Randf() - 0.5f) * 0.12f;
            Vector2 dir = toTarget.Normalized().Rotated(spreadAngle);

            swarm.State = SwarmState.Launched;
            swarm.TargetDirection = dir;
            swarm.LifeTime = 0.0f;
            swarm.MaxLifeTime = (swarm.Role == SwarmRole.CrescentBoomerang) ? 1.6f : 1.15f;

            vel.Value = dir * (850.0f + GD.Randf() * 200.0f);
            fired++;
        });
    }

    /// <summary>
    /// Menembakkan semburan salvo menyebar (Shotgun Volley)
    /// </summary>
    public void FireSwarmVolley(Vector2 targetPos, int count = 25)
    {
        int fired = 0;
        Vector2 toTarget = targetPos - _playerCore.GlobalPosition;
        if (toTarget.LengthSquared() < 1.0f) toTarget = Vector2.Right;
        Vector2 baseDir = toTarget.Normalized();

        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm) =>
        {
            if (fired >= count) return;
            if (swarm.State != SwarmState.Orbiting) return;
            if (swarm.Role == SwarmRole.Guardian) return;

            float spreadAngle = (GD.Randf() - 0.5f) * 0.55f;
            Vector2 dir = baseDir.Rotated(spreadAngle);

            swarm.State = SwarmState.Launched;
            swarm.TargetDirection = dir;
            swarm.LifeTime = 0.0f;
            swarm.MaxLifeTime = 0.95f + GD.Randf() * 0.4f;

            vel.Value = dir * (1000.0f + GD.Randf() * 300.0f);
            fired++;
        });
    }

    /// <summary>
    /// Tembakan Nova 360 Derajat
    /// </summary>
    public void FireRadialNova(int count = 75)
    {
        int fired = 0;
        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm) =>
        {
            if (fired >= count) return;
            if (swarm.State != SwarmState.Orbiting) return;

            float angle = ((float)fired / count) * Mathf.Tau;
            Vector2 dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));

            swarm.State = SwarmState.Launched;
            swarm.TargetDirection = dir;
            swarm.LifeTime = 0.0f;
            swarm.MaxLifeTime = 1.1f;

            vel.Value = dir * 1150.0f;
            fired++;
        });
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey keyEvent && keyEvent.Pressed)
        {
            switch (keyEvent.Keycode)
            {
                case Key.Key1:
                    AddSpecificSwarm(SwarmRole.DartNeedle, 100);
                    break;
                case Key.Key2:
                    _playerCore.AddNextHoneycombCell();
                    break;
                case Key.Key3:
                    AddSpecificSwarm(SwarmRole.GigaShuriken, 50);
                    break;
                case Key.Key4:
                    AddSpecificSwarm(SwarmRole.DrillDrone, 50);
                    break;
                case Key.Key5:
                    AddSpecificSwarm(SwarmRole.CrescentBoomerang, 50);
                    break;
                case Key.Key6:
                    AddSpecificSwarm(SwarmRole.Guardian, 50);
                    break;
                case Key.Space:
                    FireRadialNova(80);
                    break;
            }
        }
    }

    public override void _ExitTree()
    {
        _world?.Dispose();
    }
}
