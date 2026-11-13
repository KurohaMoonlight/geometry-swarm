using System;
using Godot;

namespace GeometrySwarm.Core.Physics;

/// <summary>
/// Spatial Hash Grid 2D berkinerja tinggi dengan alokasi memori nol (Zero-Allocation).
/// Menggunakan linked-list array datar (head + next) untuk query tetangga O(1).
/// </summary>
public sealed class SpatialHashGrid
{
    private readonly float _cellSize;
    private readonly float _invCellSize;
    private readonly int _tableSize;
    private readonly int[] _heads;
    private readonly int[] _next;
    private readonly Vector2[] _positions;
    private readonly int[] _entityIndices;
    private int _count;

    public SpatialHashGrid(float cellSize = 24.0f, int maxEntities = 16384, int tableSize = 8192)
    {
        _cellSize = cellSize;
        _invCellSize = 1.0f / cellSize;
        _tableSize = tableSize;

        _heads = new int[tableSize];
        _next = new int[maxEntities];
        _positions = new Vector2[maxEntities];
        _entityIndices = new int[maxEntities];

        Array.Fill(_heads, -1);
        _count = 0;
    }

    public void Clear()
    {
        Array.Fill(_heads, -1);
        _count = 0;
    }

    public void Insert(int entityIndex, Vector2 position)
    {
        if (_count >= _next.Length) return;

        int idx = _count++;
        _entityIndices[idx] = entityIndex;
        _positions[idx] = position;

        int cx = (int)MathF.Floor(position.X * _invCellSize);
        int cy = (int)MathF.Floor(position.Y * _invCellSize);
        int hash = ComputeHash(cx, cy);

        _next[idx] = _heads[hash];
        _heads[hash] = idx;
    }

    /// <summary>
    /// Mencari semua entitas tetangga dalam radius R menggunakan span/buffer untuk zero GC.
    /// </summary>
    public int QueryNeighbors(Vector2 center, float radius, Span<int> resultsBuffer)
    {
        int found = 0;
        float r2 = radius * radius;

        int minX = (int)MathF.Floor((center.X - radius) * _invCellSize);
        int maxX = (int)MathF.Floor((center.X + radius) * _invCellSize);
        int minY = (int)MathF.Floor((center.Y - radius) * _invCellSize);
        int maxY = (int)MathF.Floor((center.Y + radius) * _invCellSize);

        for (int cy = minY; cy <= maxY; cy++)
        {
            for (int cx = minX; cx <= maxX; cx++)
            {
                int hash = ComputeHash(cx, cy);
                int curr = _heads[hash];

                while (curr != -1)
                {
                    Vector2 pos = _positions[curr];
                    if (pos.DistanceSquaredTo(center) <= r2)
                    {
                        if (found < resultsBuffer.Length)
                        {
                            resultsBuffer[found++] = _entityIndices[curr];
                        }
                        else
                        {
                            return found; // Buffer penuh
                        }
                    }
                    curr = _next[curr];
                }
            }
        }

        return found;
    }

    private int ComputeHash(int cx, int cy)
    {
        // Bilangan prima besar untuk distribusi hash merata
        uint h = (uint)(cx * 73856093 ^ cy * 19349663);
        return (int)(h % (uint)_tableSize);
    }
}

