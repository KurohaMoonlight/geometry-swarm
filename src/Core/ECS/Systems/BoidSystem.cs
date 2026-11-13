using System;
using Arch.Core;
using GeometrySwarm.Core.ECS.Components;
using GeometrySwarm.Core.Physics;
using Godot;

namespace GeometrySwarm.Core.ECS.Systems;

/// <summary>
/// Sistem kalkulasi kemudi kawanan (Flocking & Dynamic Orbiting).
/// </summary>
public sealed class BoidSystem
{
    private readonly World _world;
    private readonly SpatialHashGrid _spatialGrid;
    private readonly QueryDescription _boidQuery;

    // Parameter Kemudi Boids
    public float SeparationRadius { get; set; } = 13.0f;
    public float SeparationWeight { get; set; } = 110.0f;
    public float OrbitSpringWeight { get; set; } = 5.5f;
    public float OrbitTangentialWeight { get; set; } = 240.0f;
    public float Damping { get; set; } = 0.94f;
    public float MaxSpeed { get; set; } = 460.0f;

    // Buffer lokal untuk query tetangga (zero GC)
    private readonly int[] _neighborBuffer = new int[64];

    public BoidSystem(World world, SpatialHashGrid spatialGrid)
    {
        _world = world;
        _spatialGrid = spatialGrid;

        _boidQuery = new QueryDescription().WithAll<PositionComponent, VelocityComponent, SwarmUnitComponent>();
    }

    /// <summary>
    /// Update simulasi kawanan boids mengorbit di sekitar posisi anchor (Core Ship)
    /// </summary>
    public void Update(float delta, Vector2 anchorPos, Vector2 anchorVelocity)
    {
        float dt = MathF.Min(delta, 0.033f);

        // Query dan perbarui semua entitas swarm yang sedang mengorbit
        _world.Query(in _boidQuery, (ref PositionComponent pos, ref VelocityComponent vel, ref SwarmUnitComponent swarm) =>
        {
            if (swarm.State != SwarmState.Orbiting) return;

            Vector2 toCore = anchorPos - pos.Value;
            float distToCore = toCore.Length();
            Vector2 dirToCore = distToCore > 0.001f ? toCore / distToCore : Vector2.Right;

            Vector2 acceleration = Vector2.Zero;

            // 1. Gaya Pegas Radial (Menjaga jarak orbit ideal dari Core)
            float distError = distToCore - swarm.OrbitDistance;
            // Jika terlalu jauh ditarik ke dalam, jika terlalu dekat didorong keluar
            acceleration += dirToCore * (distError * OrbitSpringWeight);

            // 2. Gaya Tangensial (Membuat kawanan berputar mengelilingi Core seperti elektron)
            // Vektor tegak lurus (perpendicular)
            Vector2 tangent = new Vector2(-dirToCore.Y, dirToCore.X);
            acceleration += tangent * (swarm.OrbitSpeed * OrbitTangentialWeight);

            // 3. Efek Inertia Gerak Core (Kawanan terbawa saat Core melesat cepat)
            acceleration += anchorVelocity * 1.5f;

            // 4. Separation (Menghindari tabrakan sesama drone tetangga via Spatial Grid)
            int neighborCount = _spatialGrid.QueryNeighbors(pos.Value, SeparationRadius, _neighborBuffer);
            Vector2 separationForce = Vector2.Zero;

            for (int i = 0; i < neighborCount; i++)
            {
                // Query posisi tetangga dihitung berdasarkan jarak
                // Untuk optimasi, kita dorong menjauh jika terlalu rapat
                separationForce += (pos.Value - anchorPos).Normalized() * 2.0f;
            }

            if (neighborCount > 1)
            {
                acceleration += separationForce * (SeparationWeight / neighborCount);
            }

            // Integrasi Euler
            vel.Value += acceleration * dt;
            vel.Value *= MathF.Pow(Damping, dt * 60.0f);

            // Batasi kecepatan maksimal
            if (vel.Value.LengthSquared() > MaxSpeed * MaxSpeed)
            {
                vel.Value = vel.Value.Normalized() * MaxSpeed;
            }

            pos.Value += vel.Value * dt;
        });
    }
}

