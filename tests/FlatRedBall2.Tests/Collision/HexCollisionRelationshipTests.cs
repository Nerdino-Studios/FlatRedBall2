using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using FlatRedBall2.Collision;
using FlatRedBall2.Math;
using Shouldly;
using Xunit;

namespace FlatRedBall2.Tests.Collision;

public class HexCollisionRelationshipTests
{

    [Fact]
    public void CollidesWith_HexShapesFirstAndEntitySecond_ReportsOverlap()
    {
        var body = new AARect { Width = 4f, Height = 4f };
        var entity = new Entity { X = 9f, Y = 0f };
        entity.Add(body);
        var shapes = new HexShapes(new HexGrid(10f, HexOrientation.FlatTop, Vector2.Zero));
        shapes.AddHexAtCell(new HexCoordinate(0, 0));

        shapes.CollidesWith(entity).ShouldBeTrue();
    }

    [Fact]
    public void GetSeparationVector_EmbeddedOnInternalSharedEdge_TerminatesWithoutMutatingAndReportsContact()
    {
        var body = new AARect { Width = 4f, Height = 4f };
        var entity = new Entity { X = 7.5f, Y = 4.33f };
        entity.Add(body);
        var shapes = CreateAdjacentHexes(HexOrientation.FlatTop);
        var originalPosition = new Vector2(entity.X, entity.Y);

        var separation = entity.GetSeparationVector(shapes);

        float.IsFinite(separation.X).ShouldBeTrue();
        float.IsFinite(separation.Y).ShouldBeTrue();
        entity.X.ShouldBe(originalPosition.X);
        entity.Y.ShouldBe(originalPosition.Y);
        entity.ApplySeparationOffset(separation);
        entity.GetSeparationVector(Polygon.FromPoints(shapes.Grid.GetCellCorners(new HexCoordinate(0, 0)))).Length()
            .ShouldBeLessThanOrEqualTo(0.001f);
        entity.GetSeparationVector(Polygon.FromPoints(shapes.Grid.GetCellCorners(new HexCoordinate(1, 0)))).Length()
            .ShouldBeLessThanOrEqualTo(0.001f);
        // Clearing each cell's positive MTV does not promise an arbitrary embedded start fully escapes.
        entity.CollidesWith(shapes).ShouldBeTrue();
    }

    [Fact]
    public void GetSeparationVector_HexShapesFirstAndEntitySecond_ReturnsOppositeEntitySeparation()
    {
        var body = new AARect { Width = 4f, Height = 4f };
        var entity = new Entity { X = 9f, Y = 0f };
        entity.Add(body);
        var shapes = new HexShapes(new HexGrid(10f, HexOrientation.FlatTop, Vector2.Zero));
        shapes.AddHexAtCell(new HexCoordinate(0, 0));
        var expected = -entity.GetSeparationVector(shapes);

        var separation = shapes.GetSeparationVector(entity);

        separation.ShouldBe(expected);
    }

    [Theory]
    [InlineData(HexOrientation.FlatTop, false)]
    [InlineData(HexOrientation.FlatTop, true)]
    [InlineData(HexOrientation.PointyTop, false)]
    [InlineData(HexOrientation.PointyTop, true)]
    public void GetSeparationVector_TraversingExposedBoundaryNearSharedEdgeEndpoint_LeavesActorOutsideEachCell(
        HexOrientation orientation, bool reverseTraversal)
    {
        const float actorSize = 4f;
        const float penetrationTolerance = 0.001f;
        const float outsideOffset = 4f;
        const float shallowPenetrationOffset = 1.5f;
        var shapes = CreateAdjacentHexes(orientation);
        var grid = shapes.Grid;
        var sharedEndpoint = GetSharedEndpoint(grid);
        var sourceCell = reverseTraversal ? new HexCoordinate(1, 0) : new HexCoordinate(0, 0);
        var destinationCell = reverseTraversal ? new HexCoordinate(0, 0) : new HexCoordinate(1, 0);
        var sourceEdge = GetExposedEdgeAtEndpoint(grid, sourceCell, sharedEndpoint);
        var destinationEdge = GetExposedEdgeAtEndpoint(grid, destinationCell, sharedEndpoint);
        var sourceNormal = Vector2.Normalize((sourceEdge.Start + sourceEdge.End) / 2f - grid.GetCellCenter(sourceCell));
        var destinationNormal = Vector2.Normalize((destinationEdge.Start + destinationEdge.End) / 2f - grid.GetCellCenter(destinationCell));
        var sourceNearEndpoint = Vector2.Lerp(sourceEdge.End, sourceEdge.Start, 0.7f);
        var destinationNearEndpoint = Vector2.Lerp(destinationEdge.End, destinationEdge.Start, 0.7f);
        var path = new[]
        {
            sourceNearEndpoint + sourceNormal * shallowPenetrationOffset,
            sharedEndpoint + Vector2.Normalize(sourceNormal + destinationNormal) * shallowPenetrationOffset,
            destinationNearEndpoint + destinationNormal * shallowPenetrationOffset
        };
        var entity = new Entity
        {
            X = sourceNearEndpoint.X + sourceNormal.X * outsideOffset,
            Y = sourceNearEndpoint.Y + sourceNormal.Y * outsideOffset
        };
        entity.Add(new AARect { Width = actorSize, Height = actorSize });
        var cellZero = Polygon.FromPoints(grid.GetCellCorners(new HexCoordinate(0, 0)));
        var cellOne = Polygon.FromPoints(grid.GetCellCorners(new HexCoordinate(1, 0)));

        entity.CollidesWith(shapes).ShouldBeFalse();

        foreach (var position in path)
        {
            entity.X = position.X;
            entity.Y = position.Y;
            entity.CollidesWith(shapes).ShouldBeTrue();
            var separation = entity.GetSeparationVector(shapes);
            separation.LengthSquared().ShouldBeGreaterThan(0f);
            entity.ApplySeparationOffset(separation);

            entity.GetSeparationVector(cellZero).Length().ShouldBeLessThanOrEqualTo(penetrationTolerance);
            entity.GetSeparationVector(cellOne).Length().ShouldBeLessThanOrEqualTo(penetrationTolerance);
        }
    }

    [Fact]
    public void RunCollisions_EntityOverlappingHex_MovesEntityOut()
    {
        var body = new AARect { Width = 4f, Height = 4f };
        var entity = new Entity { X = 9f, Y = 0f };
        entity.Add(body);
        var shapes = new HexShapes(new HexGrid(10f, HexOrientation.FlatTop, Vector2.Zero));
        shapes.AddHexAtCell(new HexCoordinate(0, 0));
        var relationship = new CollisionRelationship<Entity, HexShapes>(new[] { entity }, new[] { shapes });
        relationship.MoveFirstOnCollision();

        relationship.RunCollisions();

        entity.X.ShouldBeGreaterThan(9f);
        entity.CollidesWith(shapes).ShouldBeFalse();
    }

    [Fact]
    public void RunCollisions_EmbeddedWithNoUsableSeparation_ReportsContactWithoutMoving()
    {
        var body = new AARect { Width = 4f, Height = 4f };
        var entity = new Entity { X = 0f, Y = 0f };
        entity.Add(body);
        var shapes = new HexShapes(new HexGrid(10f, HexOrientation.FlatTop, Vector2.Zero));
        shapes.AddHexAtCell(new HexCoordinate(0, 0));
        foreach (var neighbor in new HexCoordinate(0, 0).GetNeighbors())
            shapes.AddHexAtCell(neighbor);
        var relationship = new CollisionRelationship<Entity, HexShapes>(new[] { entity }, new[] { shapes });
        var originalPosition = new Vector2(entity.X, entity.Y);
        int collisionCount = 0;
        relationship.CollisionOccurred += (_, _) => collisionCount++;

        entity.GetSeparationVector(shapes).ShouldBe(Vector2.Zero);
        relationship.RunCollisions();

        entity.X.ShouldBe(originalPosition.X);
        entity.Y.ShouldBe(originalPosition.Y);
        collisionCount.ShouldBe(1);
        entity.CollidesWith(shapes).ShouldBeTrue();
    }

    [Fact]
    public void RunCollisions_HexFirstEmbeddedWithNoUsableSeparation_ReportsContactWithoutMoving()
    {
        var body = new AARect { Width = 4f, Height = 4f };
        var entity = new Entity { X = 0f, Y = 0f };
        entity.Add(body);
        var shapes = new HexShapes(new HexGrid(10f, HexOrientation.FlatTop, Vector2.Zero));
        shapes.AddHexAtCell(new HexCoordinate(0, 0));
        foreach (var neighbor in new HexCoordinate(0, 0).GetNeighbors())
            shapes.AddHexAtCell(neighbor);
        var relationship = new CollisionRelationship<HexShapes, Entity>(new[] { shapes }, new[] { entity });
        var originalPosition = new Vector2(entity.X, entity.Y);
        int collisionCount = 0;
        relationship.CollisionOccurred += (_, _) => collisionCount++;

        shapes.GetSeparationVector(entity).ShouldBe(Vector2.Zero);
        relationship.RunCollisions();

        entity.X.ShouldBe(originalPosition.X);
        entity.Y.ShouldBe(originalPosition.Y);
        collisionCount.ShouldBe(1);
        shapes.CollidesWith(entity).ShouldBeTrue();
    }

    [Fact]
    public void ScreenAddCollisionRelationship_FactoryEntitySteppedThroughAutomation_ReportsResolvedPosition()
    {
        var commands =
            "{\"cmd\":\"step\"}\n" +
            "{\"cmd\":\"query\",\"target\":\"AutomationHexEntity\"}\n" +
            "{\"cmd\":\"step\"}\n";
        using var input = new EndTrackingReader(commands);
        var output = new StringWriter();
        var engine = new FlatRedBallService();
        engine.Start<AutomationHexScreen>();

        try
        {
            engine.StartAutomationMode(seed: 0, input: input, output: output);
            SpinWait.SpinUntil(() => input.HasReachedEnd, TimeSpan.FromSeconds(1)).ShouldBeTrue(
                "timed out waiting for the automation reader to queue the recorded commands");

            var frame = new Microsoft.Xna.Framework.GameTime(
                TimeSpan.FromSeconds(1f / 60f), TimeSpan.FromSeconds(1f / 60f));
            engine.Update(frame);
            engine.Update(frame);

            var responses = output.ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
            responses.Length.ShouldBe(3);
            using var snapshot = JsonDocument.Parse(responses[1]);
            var entity = snapshot.RootElement.GetProperty("result")[0];
            entity.GetProperty("X").GetSingle().ShouldBeGreaterThan(9f);
            entity.GetProperty("Y").GetSingle().ShouldBe(0f, tolerance: 0.001f);
        }
        finally
        {
            engine.Shutdown();
        }
    }

    private static HexShapes CreateAdjacentHexes(HexOrientation orientation)
    {
        var shapes = new HexShapes(new HexGrid(10f, orientation, Vector2.Zero));
        shapes.AddHexAtCell(new HexCoordinate(0, 0));
        shapes.AddHexAtCell(new HexCoordinate(1, 0));
        return shapes;
    }

    private static (Vector2 Start, Vector2 End) GetExposedEdgeAtEndpoint(
        HexGrid grid, HexCoordinate cell, Vector2 endpoint)
    {
        var corners = grid.GetCellCorners(cell);
        var otherCell = cell == new HexCoordinate(0, 0) ? new HexCoordinate(1, 0) : new HexCoordinate(0, 0);
        var otherCorners = grid.GetCellCorners(otherCell);
        for (int index = 0; index < corners.Count; index++)
        {
            if (Vector2.DistanceSquared(corners[index], endpoint) >= 0.001f) continue;

            var previous = corners[(index + corners.Count - 1) % corners.Count];
            var next = corners[(index + 1) % corners.Count];
            if (!IsSharedWithOtherCell(previous, otherCorners)) return (endpoint, previous);
            if (!IsSharedWithOtherCell(next, otherCorners)) return (endpoint, next);
        }

        throw new InvalidOperationException("The requested point is not on an exposed edge.");
    }

    private static Vector2 GetSharedEndpoint(HexGrid grid)
    {
        var first = grid.GetCellCorners(new HexCoordinate(0, 0));
        var second = grid.GetCellCorners(new HexCoordinate(1, 0));
        var shared = new List<Vector2>();
        foreach (var firstCorner in first)
            foreach (var secondCorner in second)
                if (Vector2.DistanceSquared(firstCorner, secondCorner) < 0.001f)
                    shared.Add(firstCorner);

        shared.Count.ShouldBe(2);
        return shared[0];
    }

    private static bool IsSharedWithOtherCell(Vector2 corner, IReadOnlyList<Vector2> otherCorners)
    {
        foreach (var otherCorner in otherCorners)
            if (Vector2.DistanceSquared(corner, otherCorner) < 0.001f)
                return true;
        return false;
    }

    private class AutomationHexEntity : Entity
    {
        public override void CustomInitialize()
        {
            Add(new AARect { Width = 4f, Height = 4f });
        }
    }

    private class AutomationHexScreen : Screen
    {
        public override void CustomInitialize()
        {
            var actors = new Factory<AutomationHexEntity>(this);
            var actor = actors.Create();
            actor.X = 9f;
            actor.Y = 0f;
            var hexes = new HexShapes(new HexGrid(10f, HexOrientation.FlatTop, Vector2.Zero));
            hexes.AddHexAtCell(new HexCoordinate(0, 0));

            AddCollisionRelationship(actors, hexes).MoveFirstOnCollision();
        }
    }

    private sealed class EndTrackingReader : StringReader
    {
        private int _hasReachedEnd;

        public EndTrackingReader(string commands) : base(commands) { }

        public bool HasReachedEnd => Volatile.Read(ref _hasReachedEnd) != 0;

        public override string? ReadLine()
        {
            var line = base.ReadLine();
            if (line == null)
                Volatile.Write(ref _hasReachedEnd, 1);
            return line;
        }

    }
}
