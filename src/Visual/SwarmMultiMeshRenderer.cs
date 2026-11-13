using Godot;

namespace GeometrySwarm.Visual;

/// <summary>
/// Renderer GPU batching menggunakan MultiMeshInstance2D.
/// Menggambar ribuan drone geometri neon dalam 1 kali draw call tanpa overhead Node.
/// </summary>
public partial class SwarmMultiMeshRenderer : Node2D
{
    private MultiMeshInstance2D _multiMeshInstance = null!;
    private MultiMesh _multiMesh = null!;
    private int _maxCapacity = 16384;

    public override void _Ready()
    {
        _multiMeshInstance = new MultiMeshInstance2D();
        _multiMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
            UseColors = true,
            InstanceCount = _maxCapacity,
            VisibleInstanceCount = 0
        };

        // Buat Mesh Segitiga Geometri Prosedural (Dart Needle Mini Sleek)
        _multiMesh.Mesh = CreateNeedleMesh();
        _multiMeshInstance.Multimesh = _multiMesh;

        AddChild(_multiMeshInstance);
    }

    /// <summary>
    /// Update transformasi (posisi, rotasi, skala) dan warna instance pada GPU buffer
    /// </summary>
    public void SetInstance(int index, Vector2 position, Vector2 velocity, Color color, float scale = 1.0f)
    {
        if (index < 0 || index >= _maxCapacity) return;

        // Hadapkan hidung drone ke arah gerak kecepatan
        float angle = velocity.LengthSquared() > 1.0f 
            ? velocity.Angle() + Mathf.Pi * 0.5f 
            : 0.0f;

        Transform2D xform = new Transform2D(angle, new Vector2(scale, scale), 0.0f, position);
        _multiMesh.SetInstanceTransform2D(index, xform);
        _multiMesh.SetInstanceColor(index, color);
    }

    public void SetVisibleCount(int count)
    {
        _multiMesh.VisibleInstanceCount = Mathf.Clamp(count, 0, _maxCapacity);
    }

    /// <summary>
    /// Membuat Mesh geometri panah/jarum lancip neon mini (ArrayMesh)
    /// </summary>
    private static ArrayMesh CreateNeedleMesh()
    {
        var vertices = new Vector2[]
        {
            new Vector2(0.0f, -4.8f),   // Hidung lancip atas
            new Vector2(2.6f, 3.8f),    // Sayap kanan
            new Vector2(0.0f, 1.8f),    // Lekukan ekor dalam
            new Vector2(-2.6f, 3.8f),   // Sayap kiri
        };

        // Dua segitiga pembentuk arrowhead
        var indices = new int[]
        {
            0, 1, 2, // Segitiga kanan
            0, 2, 3  // Segitiga kiri
        };

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Index] = indices;

        var arrayMesh = new ArrayMesh();
        arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        return arrayMesh;
    }
}

