using System;
using System.Collections.Generic;
using System.Numerics;
using FlatRedBall2.Math;
using FlatRedBall2.Rendering;
using FlatRedBall2.Rendering.Batches;
using Microsoft.Xna.Framework.Graphics;
using XnaColor = Microsoft.Xna.Framework.Color;

namespace FlatRedBall2.Collision;

/// <summary>
/// Sparse static collision geometry made of occupied cells in a <see cref="HexGrid"/>.
/// </summary>
public sealed class HexShapes : ICollidable, IRenderable
{
    private readonly Dictionary<HexCoordinate, Polygon> _hexes = new();
    private readonly List<HexCoordinate> _orderedCoordinates = new();

    /// <summary>Creates an empty collection using <paramref name="grid"/>'s immutable layout.</summary>
    public HexShapes(HexGrid grid) => Grid = grid ?? throw new ArgumentNullException(nameof(grid));

    /// <summary>The immutable layout used to position every occupied cell.</summary>
    public HexGrid Grid { get; }
    /// <summary>The number of occupied cells.</summary>
    public int Count => _hexes.Count;
    /// <inheritdoc/>
    public float AbsoluteX => Grid.Origin.X;
    /// <inheritdoc/>
    public float AbsoluteY => Grid.Origin.Y;
    /// <inheritdoc/>
    public float BroadPhaseRadius => float.MaxValue;
    /// <inheritdoc/>
    public float Z { get; set; }
    /// <inheritdoc/>
    public Layer? Layer { get; set; }
    /// <inheritdoc/>
    public IRenderBatch Batch { get; } = ShapesBatch.Instance;
    /// <inheritdoc/>
    public string? Name { get; set; }
    /// <summary>Whether occupied cells are drawn. Collision does not depend on this value.</summary>
    public bool IsVisible { get; set; }
    /// <summary>Color used when drawing occupied cells.</summary>
    public XnaColor Color { get; set; } = XnaColor.White;
    /// <summary>Whether occupied cells are filled when drawn.</summary>
    public bool IsFilled { get; set; }
    /// <summary>Outline thickness used when <see cref="IsFilled"/> is false.</summary>
    public float OutlineThickness { get; set; } = 1f;

    /// <summary>Adds a solid hex at <paramref name="coordinate"/>. Duplicate calls are ignored.</summary>
    public void AddHexAtCell(HexCoordinate coordinate)
    {
        if (_hexes.ContainsKey(coordinate)) return;

        var polygon = Polygon.FromPoints(Grid.GetCellCorners(coordinate));
        _hexes.Add(coordinate, polygon);
        _orderedCoordinates.Insert(GetCoordinateInsertIndex(coordinate), coordinate);
    }

    /// <summary>Adds a solid hex at the cell containing <paramref name="worldPosition"/>.</summary>
    public void AddHexAtWorld(Vector2 worldPosition) => AddHexAtCell(Grid.GetCellAt(worldPosition));

    /// <summary>Removes all occupied cells.</summary>
    public void Clear()
    {
        _hexes.Clear();
        _orderedCoordinates.Clear();
    }

    /// <summary>Returns whether <paramref name="coordinate"/> is occupied.</summary>
    public bool ContainsCell(HexCoordinate coordinate) => _hexes.ContainsKey(coordinate);

    /// <inheritdoc/>
    public bool Contains(Vector2 worldPoint)
    {
        foreach (var polygon in _hexes.Values)
            if (polygon.Contains(worldPoint))
                return true;
        return false;
    }

    /// <inheritdoc/>
    public bool CollidesWith(ICollidable other)
    {
        if (other is HexShapes || other is Line) return false;

        foreach (var shape in Entity.GetLeafShapes(other))
            foreach (var coordinate in GetCandidateCoordinates(shape))
                if (CollisionDispatcher.CollidesWith(shape, _hexes[coordinate]))
                    return true;
        return false;
    }

    /// <inheritdoc/>
    public Vector2 GetSeparationVector(ICollidable other) => -GetSeparationFor(other);

    /// <inheritdoc/>
    public void SeparateFrom(ICollidable other, float thisMass = 1f, float otherMass = 1f) { }

    /// <inheritdoc/>
    public void ApplySeparationOffset(Vector2 offset) { }

    /// <inheritdoc/>
    public void AdjustVelocityFrom(ICollidable other, float thisMass = 1f, float otherMass = 1f, float elasticity = 1f) { }

    /// <inheritdoc/>
    public void AdjustVelocityFromSeparation(Vector2 sep, ICollidable other, float thisMass = 1f, float otherMass = 1f, float elasticity = 1f) { }

    /// <summary>Draws all occupied cells as one renderable.</summary>
    public void Draw(SpriteBatch spriteBatch, Camera camera)
    {
        if (!IsVisible) return;
        foreach (var polygon in _hexes.Values)
        {
            polygon.Color = Color;
            polygon.IsFilled = IsFilled;
            polygon.OutlineThickness = OutlineThickness;
            polygon.Layer = Layer;
            polygon.Z = Z;
            polygon.IsVisible = true;
            polygon.Draw(spriteBatch, camera);
        }
    }

    /// <summary>Removes the cell at <paramref name="coordinate"/>. Missing cells are ignored.</summary>
    public void RemoveHexAtCell(HexCoordinate coordinate)
    {
        if (!_hexes.Remove(coordinate)) return;
        _orderedCoordinates.RemoveAt(GetCoordinateIndex(coordinate));
    }

    /// <summary>Removes the cell containing <paramref name="worldPosition"/>.</summary>
    public void RemoveHexAtWorld(Vector2 worldPosition) => RemoveHexAtCell(Grid.GetCellAt(worldPosition));

    internal Vector2 GetSeparationFor(ICollidable shape)
    {
        foreach (var leaf in Entity.GetLeafShapes(shape))
        {
            var separation = GetSeparationForLeaf(leaf);
            if (separation != Vector2.Zero)
                return separation;
        }

        return Vector2.Zero;
    }

    private Vector2 GetSeparationForLeaf(ICollidable shape)
    {
        if (!CollidesWith(shape)) return Vector2.Zero;

        ICollidable provisional = CreateTranslatedCopy(shape, Vector2.Zero);
        Vector2 totalSeparation = Vector2.Zero;
        const int maximumIterations = 12;
        const float progressToleranceSquared = 0.000001f;

        for (int iteration = 0; iteration < maximumIterations; iteration++)
        {
            bool foundPenetration = false;
            foreach (var coordinate in GetCandidateCoordinates(provisional))
            {
                var polygon = _hexes[coordinate];
                if (!CollisionDispatcher.CollidesWith(provisional, polygon)) continue;

                var separation = CollisionDispatcher.GetSeparationVector(provisional, polygon);
                if (separation.LengthSquared() <= progressToleranceSquared)
                    continue;
                if (!float.IsFinite(separation.X) || !float.IsFinite(separation.Y))
                    return Vector2.Zero;

                foundPenetration = true;
                provisional.ApplySeparationOffset(separation);
                totalSeparation += separation;
                if (!float.IsFinite(totalSeparation.X) || !float.IsFinite(totalSeparation.Y))
                    return Vector2.Zero;
                break;
            }

            if (!foundPenetration) return totalSeparation;
        }

        return Vector2.Zero;
    }

    private IEnumerable<HexCoordinate> GetCandidateCoordinates(ICollidable shape)
    {
        var (minX, maxX, minY, maxY) = CollisionDispatcher.GetBounds(shape);
        if (!float.IsFinite(minX) || !float.IsFinite(maxX) || !float.IsFinite(minY) || !float.IsFinite(maxY))
            yield break;

        var lowerLeft = Grid.GetCellAt(new Vector2(minX, minY));
        var lowerRight = Grid.GetCellAt(new Vector2(maxX, minY));
        var upperLeft = Grid.GetCellAt(new Vector2(minX, maxY));
        var upperRight = Grid.GetCellAt(new Vector2(maxX, maxY));
        int minQ = ClampToInt((long)System.Math.Min(System.Math.Min(lowerLeft.Q, lowerRight.Q), System.Math.Min(upperLeft.Q, upperRight.Q)) - 2);
        int maxQ = ClampToInt((long)System.Math.Max(System.Math.Max(lowerLeft.Q, lowerRight.Q), System.Math.Max(upperLeft.Q, upperRight.Q)) + 2);
        int minR = ClampToInt((long)System.Math.Min(System.Math.Min(lowerLeft.R, lowerRight.R), System.Math.Min(upperLeft.R, upperRight.R)) - 2);
        int maxR = ClampToInt((long)System.Math.Max(System.Math.Max(lowerLeft.R, lowerRight.R), System.Math.Max(upperLeft.R, upperRight.R)) + 2);
        long candidateWidth = (long)maxQ - minQ + 1;
        long candidateHeight = (long)maxR - minR + 1;

        if (CandidateRangeExceedsOccupancy(candidateWidth, candidateHeight))
        {
            foreach (var coordinate in _orderedCoordinates)
                if (CellBoundsOverlap(coordinate, minX, maxX, minY, maxY))
                    yield return coordinate;
            yield break;
        }

        for (long q = minQ; q <= maxQ; q++)
            for (long r = minR; r <= maxR; r++)
            {
                var coordinate = new HexCoordinate((int)q, (int)r);
                if (_hexes.ContainsKey(coordinate))
                    yield return coordinate;
            }
    }

    private bool CandidateRangeExceedsOccupancy(long width, long height)
    {
        if (width > _hexes.Count || height > _hexes.Count)
            return true;
        return width * height > _hexes.Count;
    }

    private static int ClampToInt(long value) =>
        value < int.MinValue ? int.MinValue : value > int.MaxValue ? int.MaxValue : (int)value;

    private bool CellBoundsOverlap(HexCoordinate coordinate, float minX, float maxX, float minY, float maxY)
    {
        var center = Grid.GetCellCenter(coordinate);
        return center.X + Grid.Radius >= minX && center.X - Grid.Radius <= maxX
            && center.Y + Grid.Radius >= minY && center.Y - Grid.Radius <= maxY;
    }

    private int GetCoordinateIndex(HexCoordinate coordinate)
    {
        int low = 0;
        int high = _orderedCoordinates.Count - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            int comparison = CompareCoordinates(_orderedCoordinates[middle], coordinate);
            if (comparison == 0) return middle;
            if (comparison < 0) low = middle + 1;
            else high = middle - 1;
        }
        return ~low;
    }

    private int GetCoordinateInsertIndex(HexCoordinate coordinate)
    {
        int index = GetCoordinateIndex(coordinate);
        return index >= 0 ? index : ~index;
    }

    private static int CompareCoordinates(HexCoordinate first, HexCoordinate second)
    {
        int q = first.Q.CompareTo(second.Q);
        return q != 0 ? q : first.R.CompareTo(second.R);
    }

    private static ICollidable CreateTranslatedCopy(ICollidable shape, Vector2 offset) => shape switch
    {
        AARect rectangle => new AARect
        {
            Width = rectangle.Width,
            Height = rectangle.Height,
            X = rectangle.AbsoluteX + offset.X,
            Y = rectangle.AbsoluteY + offset.Y
        },
        Circle circle => new Circle
        {
            Radius = circle.Radius,
            X = circle.AbsoluteX + offset.X,
            Y = circle.AbsoluteY + offset.Y
        },
        Polygon polygon => CreateTranslatedPolygon(polygon, offset),
        _ => throw new NotSupportedException($"Hex collision does not support {shape.GetType().Name}.")
    };

    private static Polygon CreateTranslatedPolygon(Polygon polygon, Vector2 offset)
    {
        var copy = Polygon.FromPoints(polygon.Points);
        copy.X = polygon.AbsoluteX + offset.X;
        copy.Y = polygon.AbsoluteY + offset.Y;
        copy.Rotation = polygon.AbsoluteRotation;
        return copy;
    }
}
