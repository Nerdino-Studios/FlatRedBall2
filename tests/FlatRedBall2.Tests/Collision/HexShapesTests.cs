using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using FlatRedBall2.Collision;
using FlatRedBall2.Math;
using Shouldly;
using Xunit;

namespace FlatRedBall2.Tests.Collision;

public class HexShapesTests
{
    [Fact]
    public void AddHexAtCell_DuplicateCoordinate_StoresOneOccupiedCell()
    {
        var coordinate = new HexCoordinate(1, -2);
        var shapes = new HexShapes(new HexGrid(10f, HexOrientation.FlatTop, Vector2.Zero));

        shapes.AddHexAtCell(coordinate);
        shapes.AddHexAtCell(coordinate);

        shapes.Count.ShouldBe(1);
        shapes.ContainsCell(coordinate).ShouldBeTrue();
    }

    [Fact]
    public void CollidesWith_AARectInHexBoundingBoxCorner_ReturnsFalse()
    {
        var coordinate = new HexCoordinate(0, 0);
        var rectangle = new AARect { X = 9f, Y = 8.5f, Width = 1f, Height = 1f };
        var shapes = new HexShapes(new HexGrid(10f, HexOrientation.FlatTop, Vector2.Zero));
        shapes.AddHexAtCell(coordinate);

        shapes.CollidesWith(rectangle).ShouldBeFalse();
    }

    [Fact]
    public void CollidesWith_CircleOverlappingOccupiedCell_ReturnsTrue()
    {
        var circle = new Circle { X = 9f, Y = 0f, Radius = 2f };
        var shapes = new HexShapes(new HexGrid(10f, HexOrientation.FlatTop, Vector2.Zero));
        shapes.AddHexAtCell(new HexCoordinate(0, 0));

        shapes.CollidesWith(circle).ShouldBeTrue();
    }

    [Theory]
    [InlineData(HexOrientation.FlatTop, -3, 2)]
    [InlineData(HexOrientation.PointyTop, 2, -3)]
    public void CollidesWith_SparseNegativeCellAndMultiCellAARect_MatchesIndividualCellGeometry(
        HexOrientation orientation, int q, int r)
    {
        var grid = new HexGrid(10f, orientation, Vector2.Zero);
        var occupied = new[]
        {
            new HexCoordinate(q, r),
            new HexCoordinate(q + 4, r - 2),
            new HexCoordinate(q - 5, r + 3)
        };
        var center = grid.GetCellCenter(occupied[0]);
        var rectangle = new AARect { X = center.X, Y = center.Y, Width = 44f, Height = 44f };
        var shapes = new HexShapes(grid);
        foreach (var coordinate in occupied)
            shapes.AddHexAtCell(coordinate);
        bool expected = occupied.Any(coordinate =>
            CollisionDispatcher.CollidesWith(rectangle, Polygon.FromPoints(grid.GetCellCorners(coordinate))));

        shapes.CollidesWith(rectangle).ShouldBe(expected);
    }

    [Fact]
    public void GetCandidateCoordinates_ExtremeRepresentableCell_IncludesOccupiedCellWithoutRangeWrap()
    {
        const int q = int.MinValue;
        var result = FindGridWhoseCellCenterRoundsTo(q);
        result.HasValue.ShouldBeTrue($"no representable world center was found for axial q={q}");
        var (grid, coordinate, center) = result!.Value;
        var rectangle = new AARect { X = center.X, Y = center.Y, Width = 1f, Height = 1f };
        var shapes = new HexShapes(grid);
        shapes.AddHexAtCell(coordinate);
        var getCandidates = typeof(HexShapes).GetMethod(
            "GetCandidateCoordinates", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var candidates = ((System.Collections.Generic.IEnumerable<HexCoordinate>)getCandidates.Invoke(
            shapes, new object[] { rectangle })!).ToArray();

        candidates.ShouldContain(coordinate);
    }

    [Fact]
    public void RemoveHexAtWorld_ExistingCell_RemovesIt()
    {
        var coordinate = new HexCoordinate(-1, 2);
        var grid = new HexGrid(10f, HexOrientation.PointyTop, new Vector2(3f, 4f));
        var shapes = new HexShapes(grid);
        shapes.AddHexAtCell(coordinate);

        shapes.RemoveHexAtWorld(grid.GetCellCenter(coordinate));

        shapes.Count.ShouldBe(0);
        shapes.ContainsCell(coordinate).ShouldBeFalse();
    }

    private static (HexGrid Grid, HexCoordinate Coordinate, Vector2 Center)? FindGridWhoseCellCenterRoundsTo(int q)
    {
        for (int index = 1; index <= 8192; index++)
        {
            float radius = 1f + index / 1024f;
            var grid = new HexGrid(radius, HexOrientation.FlatTop, Vector2.Zero);
            var coordinate = new HexCoordinate(q, 0);
            var center = grid.GetCellCenter(coordinate);
            try
            {
                if (grid.GetCellAt(center).Q == q)
                    return (grid, coordinate, center);
            }
            catch (OverflowException)
            {
                // This radius maps the float world center past the representable axial range.
            }
        }

        return null;
    }
}
