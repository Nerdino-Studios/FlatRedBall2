---
name: hex-grid
description: "Axial hex grids and sparse hex collision in FlatRedBall2. Triggers: HexGrid, HexCoordinate, HexOrientation, HexShapes, pointy-top or flat-top cells."
---

# Hex Grid

Use `HexGrid` for layout math and `HexShapes` for sparse static collision. See the **collision-relationships**, **entities-and-factories**, and **shapes** skills for the surrounding engine patterns.

## Map

| File | Purpose |
|---|---|
| `src/Math/HexCoordinate.cs` | Axial `(Q, R)` identity, neighbors, and distance |
| `src/Math/HexGrid.cs` | Immutable pointy/flat layout and world-coordinate conversion |
| `src/Collision/HexShapes.cs` | Occupied cells, collision, and aggregate debug rendering |

## Setup Pattern

```csharp
var grid = new HexGrid(16f, HexOrientation.PointyTop, Vector2.Zero);
var solids = new HexShapes(grid) { IsVisible = true };
solids.AddHexAtCell(new HexCoordinate(0, 0));
Add(solids);

var actors = new Factory<Player>(this);
actors.Create();
AddCollisionRelationship(actors, solids).MoveFirstOnCollision();
```

`grid.GetCellAt(worldPosition)` returns a cell in the mathematical grid; check `solids.ContainsCell(cell)` separately when occupancy matters.

## Landmines

- `Radius` is center-to-vertex distance, and `Origin` is the center of cell `(0,0)`. World Y increases upward; negative axial coordinates are valid.
- `HexGrid` is immutable layout math. Rebuild `HexShapes` when radius, orientation, or origin changes.
- Add `HexShapes` once as an aggregate renderable; its visibility affects drawing only, and occupied cells remain collidable while hidden.
- Collision is discrete. High-speed tunneling and escape from arbitrary embedded positions are not guaranteed; unresolved queries terminate and still report contact.
