using System;
using Arch.Core;
using GeometrySwarm.Core.ECS.Components;
using GeometrySwarm.Core.ECS.Systems;
using GeometrySwarm.Core.Physics;
using GeometrySwarm.Visual;
using Godot;

namespace GeometrySwarm.Gameplay;

/// <summary>
/// Driver utama simulasi kawanan Arch ECS.
/// Menghubungkan ECS World, Spatial Grid, Boid System, dan MultiMesh GPU Renderer.
/// </summary>
public partial class SwarmSimulation : Node2D
{
    [Export] public int InitialSwarmCount { get; set; } = 400;

    private World _world = null!;
    private SpatialHashGrid _spatialGrid = null!;
    private BoidSystem _boidSystem = null!;
    private QueryDescription _allSwarmQuery;

    private PlayerCore _playerCore = null!;
    private SwarmMultiMeshRenderer _renderer = null!;

    // Warna neon per peran
    private static readonly Color DartColor = new(2.2f, 2.4f, 0.4f, 1.0f);     // Kuning/Lime Terang
    private static readonly Color GuardianColor = new(0.4f, 2.0f, 2.6f, 1.0f); // Cyan Perisai
    private static readonly Color ShurikenColor = new(2.6f, 0.4f, 2.0f, 1.0f); // Magenta
    private static readonly Color LanceColor = new(2.8f, 0.5f, 0.3f, 1.0f);    // Merah Scarlet

    public int EntityCount => _entityCount;
    private int _entityCount = 0;

    public override void _Ready()
    {
        // 1. Inisialisasi Arch ECS
        _world = World.Create();
        _spatialGrid = new SpatialHashGrid(cellSize: 36.0f, maxEntities: 8192);
        _boidSystem = new BoidSystem(_world, _spatialGrid);
        _allSwarmQuery = new QueryDescription().WithAll<PositionComponent, VelocityComponent, SwarmUnitComponent, VisualComponent>();

        // 2. Ambil referensi node
        _playerCore = GetNode<PlayerCore>("PlayerCore");
        _renderer = GetNode<SwarmMultiMeshRenderer>("SwarmRenderer");

        // 3. Spawn kawanan awal mengitari Player
        SpawnInitialSwarm(InitialSwarmCount);
    }

    public void SpawnInitialSwarm(int count)
    {
        Vector2 corePos = _playerCore.GlobalPosition;

        for (int i = 0; i < count; i++)
        {
            // Variasi band jarak orbit (55px sampai 210px) agar muat ribuan drone dengan indah
            float orbitDist = 55.0f + GD.Randf() * 145.0f;
            // Arah putaran orbit (sebagian searah jarum jam, sebagian berlawanan)
            float orbitSpeed = (GD.Randf() > 0.45f ? 1.0f : -1.0f) * (0.8f + GD.Randf() * 0.7f);

            float initialAngle = GD.Randf() * Mathf.Tau;
            Vector2 spawnPos = corePos + new Vector2(MathF.Cos(initialAngle), MathF.Sin(initialAngle)) * orbitDist;

            // Campuran beberapa peran dengan ukuran ramping
            SwarmRole role = SwarmRole.DartNeedle;
            Color color = DartColor;
            float scale = 0.8f;

            if (i % 6 == 0)
            {
                role = SwarmRole.Guardian;
                color = GuardianColor;
                scale = 0.95f;
                orbitDist = 45.0f + GD.Randf() * 30.0f; // Guardian mengorbit lebih rapat
            }
            else if (i % 8 == 0)
            {
                role = SwarmRole.GigaShuriken;
                color = ShurikenColor;
                scale = 0.85f;
            }

            _world.Create(
                new PositionComponent(spawnPos),
                new VelocityComponent(Vector2.Zero),
                new SwarmUnitComponent(role, 0, orbitDist, orbitSpeed),
                new VisualComponent(color, scale)
            );

            _entityCount++;
        }

        _renderer.SetVisibleCount(_entityCount);
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        Vector2 corePos = _playerCore.GlobalPosition;
        Vector2 coreVel = _playerCore.Velocity;

        // 1. Bersihkan dan isi Spatial Hash Grid
        _spatialGrid.Clear();
        int entityIdx = 0;
        _world.Query(in _allSwarmQuery, (ref PositionComponent pos) =>
        {
            _spatialGrid.Insert(entityIdx++, pos.Value);
        });

        // 2. Jalankan simulasi Boids Flocking
        _boidSystem.Update(dt, corePos, coreVel);

        // 3. Update MultiMesh GPU buffer
        int renderIdx = 0;
        _world.Query(in _allSwarmQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref VisualComponent vis) =>
        {
            _renderer.SetInstance(renderIdx++, pos.Value, vel.Value, vis.Color, vis.Scale);
        });

        _renderer.SetVisibleCount(renderIdx);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey keyEvent && keyEvent.Pressed)
        {
            if (keyEvent.Keycode == Key.Key1)
            {
                // Tambah 100 swarm unit baru secara instan
                SpawnInitialSwarm(100);
            }
            else if (keyEvent.Keycode == Key.Key2)
            {
                // Tambah sel hexagon baru pada gugusan sarang lebah (Honeycomb expand)
                float angle = GD.Randf() * Mathf.Tau;
                Vector2 hexOffset = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 38.0f;
                _playerCore.AddHexCell(hexOffset);
            }
        }
    }

    public override void _ExitTree()
    {
        _world?.Dispose();
    }
}
