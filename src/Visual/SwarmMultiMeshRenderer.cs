using System;
using Godot;

namespace GeometrySwarm.Visual;

/// <summary>
/// Renderer GPU MultiMesh dengan dukungan beragam geometri drone:
/// Dart Needle, Giga Shuriken, Drill Drone, Crescent Boomerang, dan Guardian (Kurung Tebal Ganda ]] ).
/// </summary>
public partial class SwarmMultiMeshRenderer : Node2D
{
    private const int RoleCount = 6;
    private const int MaxInstancesPerRole = 16384;

    private readonly MultiMeshInstance2D[] _meshInstances = new MultiMeshInstance2D[RoleCount];
    private readonly MultiMesh[] _multiMeshes = new MultiMesh[RoleCount];
    private readonly int[] _visibleCounts = new int[RoleCount];

    public override void _Ready()
    {
        for (int role = 0; role < RoleCount; role++)
        {
            var mm = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
                UseColors = true,
                InstanceCount = MaxInstancesPerRole,
                VisibleInstanceCount = 0,
                Mesh = CreateMeshForRole(role)
            };

            var mmi = new MultiMeshInstance2D { Multimesh = mm };
            AddChild(mmi);

            _multiMeshes[role] = mm;
            _meshInstances[role] = mmi;
            _visibleCounts[role] = 0;
        }
    }

    public void ResetCounters()
    {
        Array.Clear(_visibleCounts, 0, RoleCount);
    }

    /// <summary>
    /// Update transformasi dan warna drone ke MultiMesh sesuai perannya
    /// </summary>
    public void SetInstance(int role, Vector2 position, Vector2 velocity, float rotation, Color color, float scale = 1.0f)
    {
        if (role < 0 || role >= RoleCount) return;

        int index = _visibleCounts[role];
        if (index >= MaxInstancesPerRole) return;

        // Gunakan rotasi eksplisit jika ada, jika tidak ikuti arah hadap kecepatan
        float angle = MathF.Abs(rotation) > 0.0001f 
            ? rotation 
            : (velocity.LengthSquared() > 1.0f ? velocity.Angle() + Mathf.Pi * 0.5f : 0.0f);

        Transform2D xform = new Transform2D(angle, new Vector2(scale, scale), 0.0f, position);
        _multiMeshes[role].SetInstanceTransform2D(index, xform);
        _multiMeshes[role].SetInstanceColor(index, color);

        _visibleCounts[role]++;
    }

    public void FlushVisibleCounts()
    {
        for (int r = 0; r < RoleCount; r++)
        {
            _multiMeshes[r].VisibleInstanceCount = _visibleCounts[r];
        }
    }

    private static ArrayMesh CreateMeshForRole(int role)
    {
        return role switch
        {
            0 => CreateDartNeedleMesh(),     // Dart Needle
            1 => CreateShurikenMesh(),       // Giga Shuriken
            2 => CreateDrillMesh(),          // Drill Drone
            3 => CreateCrescentMesh(),       // Crescent Boomerang
            4 => CreateMagnetMesh(),         // Mining Droid
            5 => CreateGuardianMesh(),       // Guardian (Kurung Tebal Ganda ]] )
            _ => CreateDartNeedleMesh()
        };
    }

    private static ArrayMesh CreateDartNeedleMesh()
    {
        var vertices = new Vector2[]
        {
            new(0.0f, -5.2f),
            new(2.8f, 4.0f),
            new(0.0f, 2.0f),
            new(-2.8f, 4.0f)
        };
        var indices = new int[] { 0, 1, 2, 0, 2, 3 };
        return BuildArrayMesh(vertices, indices);
    }

    private static ArrayMesh CreateShurikenMesh()
    {
        // Bintang 4 sisi tajam (Quad-blade shuriken)
        var vertices = new Vector2[]
        {
            new(0.0f, -6.5f),   // 0: Atas
            new(1.5f, -1.5f),   // 1: Notch timur laut
            new(6.5f, 0.0f),    // 2: Kanan
            new(1.5f, 1.5f),    // 3: Notch tenggara
            new(0.0f, 6.5f),    // 4: Bawah
            new(-1.5f, 1.5f),   // 5: Notch barat daya
            new(-6.5f, 0.0f),   // 6: Kiri
            new(-1.5f, -1.5f),  // 7: Notch barat laut
            new(0.0f, 0.0f)     // 8: Center
        };
        var indices = new int[]
        {
            8, 0, 1, 8, 1, 2,
            8, 2, 3, 8, 3, 4,
            8, 4, 5, 8, 5, 6,
            8, 6, 7, 8, 7, 0
        };
        return BuildArrayMesh(vertices, indices);
    }

    private static ArrayMesh CreateDrillMesh()
    {
        // Mata bor spiral bertingkat
        var vertices = new Vector2[]
        {
            new(0.0f, -7.0f),   // Tip lancip
            new(2.2f, -2.5f),
            new(-2.2f, -2.5f),
            new(3.5f, 1.5f),
            new(-3.5f, 1.5f),
            new(4.5f, 5.5f),
            new(-4.5f, 5.5f),
            new(0.0f, 3.2f)
        };
        var indices = new int[]
        {
            0, 1, 2,
            2, 1, 3, 2, 3, 4,
            4, 3, 5, 4, 5, 6,
            6, 5, 7
        };
        return BuildArrayMesh(vertices, indices);
    }

    private static ArrayMesh CreateCrescentMesh()
    {
        // Lengkungan bulan sabit tajam
        var vertices = new Vector2[]
        {
            new(0.0f, -6.5f),
            new(3.5f, -2.5f),
            new(4.8f, 1.5f),
            new(3.2f, 5.8f),
            new(1.2f, 6.0f),
            new(2.0f, 2.0f),
            new(1.2f, -1.5f),
            new(-0.5f, -4.5f)
        };
        var indices = new int[]
        {
            0, 1, 7, 1, 6, 7,
            1, 2, 6, 2, 5, 6,
            2, 3, 5, 3, 4, 5
        };
        return BuildArrayMesh(vertices, indices);
    }

    private static ArrayMesh CreateMagnetMesh()
    {
        // Bentuk magnet U tapal kuda
        var vertices = new Vector2[]
        {
            new(-4.0f, -5.5f), new(-2.0f, -5.5f),
            new(2.0f, -5.5f), new(4.0f, -5.5f),
            new(4.5f, 4.0f), new(2.5f, 2.5f),
            new(-2.5f, 2.5f), new(-4.5f, 4.0f),
            new(0.0f, 5.5f), new(0.0f, 3.5f)
        };
        var indices = new int[]
        {
            0, 1, 7, 1, 6, 7,
            2, 3, 5, 2, 5, 4,
            7, 6, 8, 6, 9, 8,
            6, 5, 9, 5, 4, 8
        };
        return BuildArrayMesh(vertices, indices);
    }

    /// <summary>
    /// Membuat siluet Guardian berupa kurung tebal siku ganda ]]
    /// </summary>
    private static ArrayMesh CreateGuardianMesh()
    {
        // Dua kurung siku ]]
        // Kurung 1 (Kiri): X dari -4.2 ke -1.2
        // Kurung 2 (Kanan): X dari 0.4 ke 3.4
        var vertices = new Vector2[]
        {
            // --- Kurung 1 (Kiri) ---
            new(-4.2f, -5.5f),  // 0: kiri atas luar
            new(-1.2f, -5.5f),  // 1: kanan atas luar
            new(-1.2f, 5.5f),   // 2: kanan bawah luar
            new(-4.2f, 5.5f),   // 3: kiri bawah luar
            new(-4.2f, 3.8f),   // 4: kiri bawah dalam
            new(-2.5f, 3.8f),   // 5: kanan bawah dalam
            new(-2.5f, -3.8f),  // 6: kanan atas dalam
            new(-4.2f, -3.8f),  // 7: kiri atas dalam

            // --- Kurung 2 (Kanan) ---
            new(0.4f, -5.5f),   // 8
            new(3.4f, -5.5f),   // 9
            new(3.4f, 5.5f),    // 10
            new(0.4f, 5.5f),    // 11
            new(0.4f, 3.8f),    // 12
            new(2.1f, 3.8f),    // 13
            new(2.1f, -3.8f),   // 14
            new(0.4f, -3.8f),   // 15
        };

        var indices = new int[]
        {
            // Kurung 1 (Kiri)
            0, 1, 6,   0, 6, 7,    // Bar atas
            7, 1, 2,   7, 2, 5,    // Batang vertikal kanan
            4, 5, 2,   4, 2, 3,    // Bar bawah

            // Kurung 2 (Kanan)
            8, 9, 14,  8, 14, 15,  // Bar atas
            15, 9, 10, 15, 10, 13, // Batang vertikal kanan
            12, 13, 10, 12, 10, 11 // Bar bawah
        };

        return BuildArrayMesh(vertices, indices);
    }

    private static ArrayMesh BuildArrayMesh(Vector2[] vertices, int[] indices)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Index] = indices;

        var arrayMesh = new ArrayMesh();
        arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return arrayMesh;
    }
}
