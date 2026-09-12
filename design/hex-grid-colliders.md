# Hex grid with colliders — implementation handoff

Status: **Planned; no engine or test implementation yet.**
Branch: **`feature/hex-grid-colliders`**, created from the original detached HEAD.

## Request and scope

Add a reusable hex grid system with colliders to FlatRedBall2. The user requested a dedicated branch and a plan for a lower model to implement. This is one subsystem-sized change, not a multi-phase initiative; keep this document in `design/`, not `plan/`.

Deliver:
- Axial coordinates, six neighbors, and hex distance.
- Pointy-top and flat-top layouts, world/cell conversion, and cell geometry.
- Sparse static collision cells backed by regular six-sided `Polygon` shapes.
- Exact overlap queries and normal-based move/bounce relationships with entities containing `AARect`, `Circle`, or `Polygon` shapes.
- Optional debug drawing and a short usage document.

Not included: hex movement behavior, A*, offset-coordinate APIs, TMX hex import, textured tile rendering, procedural level generation, grid rotation/scaling at runtime, platformer slope/one-way semantics, continuous collision detection, or a new game/sample. `Line` queries and static-grid-vs-static-grid collision are not initial supported pairs; document this rather than allowing recursive dispatch.

## Before implementation

- [ ] Confirm the active branch is `feature/hex-grid-colliders`; inspect and preserve uncommitted changes. If starting from a separate checkout, create that branch from the intended baseline before editing. Do not commit or push without permission.
- [ ] Read `CLAUDE.md`, `.claude/agents/coder.md`, and `.claude/code-style.md`.
- [ ] Load `.claude/skills/engine-tdd/SKILL.md` and `.claude/skills/content-boundary/SKILL.md`.
- [ ] Read the canonical `frb-skills/{engine-overview,tile-grid,shapes,collision-relationships,entities-and-factories,screens}/SKILL.md` files. If the tool cannot load repository skills by name, read their files directly.
- [ ] Consult `grid-movement` and `tile-node-network` only for boundaries: their square-grid arithmetic and four/eight-way graph are **not** hex implementations.
- [ ] Establish baseline build/tests. For each behavior slice below, write and run failing tests **before** implementing source behavior. Record actual red/green results; an infrastructure or restore failure is not a red test.

## Existing code to reuse and inspect

Paths in this document are relative to the repository root.

| File | Relevant behavior / warning |
|---|---|
| `src/Collision/Polygon.cs` | Local-space points, `FromPoints`, existing SAT and drawing. Uses `System.Numerics.Vector2`. |
| `src/Collision/TileShapes.cs` | Sparse cell storage and static `ICollidable` precedent. Do not copy its square spatial math, component-wise response aggregation, or occupancy-only overlap test. |
| `src/Collision/CollisionDispatcher.cs` | Concrete-type dispatch; `GetBounds` recognizes leaves, **not entities**. Add deliberate hex dispatch, not an assumption that implementing the interface is sufficient. |
| `src/Collision/ICollidable.cs` | Static movement methods are no-ops; separation is documented as moving the receiver out of the other object. |
| `src/Entity.cs` | `GetLeafShapes`, `CollidesWith`, and `GetSeparationVector`; default/non-default collision children already handled here. |
| `src/Collision/CollisionRelationship.cs` | `ComputeSeparationVector`, selected-shape handling, events, and impulse response. Its `TileShapes` special case is square/platformer-specific. |
| `src/Screen.cs` | Existing generic static-geometry relationship overload and `Add/Remove(IRenderable)`. Prefer these without new base-class members. |
| `src/Rendering/IRenderable.cs` | Aggregate debug renderable can use existing registration/lifecycle without per-cell subscriptions. |
| `tests/FlatRedBall2.Tests/Collision/TileShapesTests.cs` | Storage/collision examples; follow current style rules rather than copying historical test organization. |
| `tests/FlatRedBall2.Tests/Collision/PolygonCollisionTests.cs` | Existing polygon collision expectations and tolerances. |
| `tests/FlatRedBall2.Tests/ScreenTests.cs` | Headless screen/factory lifecycle. No graphics device is needed to test boot, registration, or relationships. |

### Accuracy notes discovered during planning

- `frb-skills/shapes/SKILL.md` says `TileShapes.X/Y` cannot move existing tiles. Source now shifts all existing shapes in those setters. Only changing `GridSize` requires clearing first.
- That skill describes three built-in shapes, whereas `ICollidable` also lists `Line`; do not treat the table as an exhaustive collision-support matrix.
- Visibility controls rendering, **not collision**. The collision skill's troubleshooting advice to check for visible shapes should not become a validation requirement.
- The `TileShapes.GetSeparationVector` implementation does not follow the receiver-direction wording of `ICollidable`. Do not perpetuate this ambiguity in a new type or change old behavior as part of this task.

Correct the touched skill guidance during the documentation slice, following the skill-edit approval process. Planning intentionally leaves skills and engine source unchanged.

## Proposed design decisions

### 1. Coordinates and immutable layout

New types in `FlatRedBall2.Math`:
- `HexCoordinate` in `src/Math/HexCoordinate.cs`: immutable value identity with axial integer coordinates; equality/hash suitable for keyed lookup; six neighbors and distance.
- `HexOrientation` in `src/Math/HexOrientation.cs`: `PointyTop` and `FlatTop`.
- `HexGrid` in `src/Math/HexGrid.cs`: immutable layout, **not** collision occupancy or game terrain data.

Contract:
- World Y+ is up. Origin is the **center of cell (0,0)**, not the corner convention of `TileShapes`.
- Radius is center-to-vertex distance in world units; require finite, strictly positive radius. Origin and query coordinates must be finite. Reject invalid enum values and coordinate overflow instead of silently wrapping. Use double intermediates and validate derived centers/corners before storing floats; finite inputs alone do not ensure representable geometry. Return distance as a wide integer.
- Radius, orientation, and origin are constructor configuration. Rebuild a collection to change layout; there must be no mutable layout shared by colliders with stale positions.
- Negative cells are valid. There is no built-in rectangular bound; games own map membership/terrain data separately.
- World-to-cell finds a cell in the mathematical grid even when no collider occupies it. Collider lookup is a distinct operation.
- Corner generation returns six distinct counter-clockwise vertices; no duplicate closing vertex. Supply local corners for collider construction and world corners for consumers without exposing shared mutable arrays.

Mathematical reference for the implementer (R = radius; subtract origin before inverse conversion):

| Orientation | Center X | Center Y | Fractional q | Fractional r |
|---|---|---|---|---|
| Pointy-top | R × sqrt(3) × (q + r/2) | R × 3/2 × r | (sqrt(3)/3 × x − y/3) / R | (2/3 × y) / R |
| Flat-top | R × 3/2 × q | R × sqrt(3) × (r + q/2) | (2/3 × x) / R | (−x/3 + sqrt(3)/3 × y) / R |

Use cube rounding of `(q, r, -q-r)`, correcting the largest rounding error to preserve the zero-sum constraint. Do not round axial components independently or copy square-grid floor conversion. Define a deterministic tie rule for exact shared edges/vertices; either adjacent cell is acceptable, but repeated calls must agree. Use wider intermediate arithmetic for cube coordinates/distance.

Six axial neighbor offsets, in fixed order: `(1,0), (0,1), (-1,1), (-1,0), (0,-1), (1,-1)`. Distance is half the sum of absolute cube-coordinate differences. Pointy corners begin at 30 degrees; flat corners at 0 degrees; advance by 60 degrees in Y-up space.

### 2. Sparse collision collection

New `HexShapes` in `src/Collision/HexShapes.cs`, implementing `ICollidable` and `IRenderable`.

- One layout instance plus sparse occupied cells, each with one private regular polygon. No `Entity` per cell. No subclassing or wrapping `TileShapes` to disguise axial cells as square cells.
- Provide cell/world add, remove, occupancy query, and clear operations, using `TileShapes` naming conventions where useful. Duplicate add and missing remove are idempotent.
- Keep polygon geometry privately owned. Do not expose mutable polygon references that could invalidate cell indexing; consumers can construct standalone visuals from layout corners.
- Collection-wide visibility/color/fill/outline/layer/Z for debug rendering; hidden by default. Existing and newly added cells reflect the same settings.
- It is static terrain: movement and velocity application are no-ops; broad-phase radius uses the existing static-geometry convention. Origin is the layout origin.
- `Contains` checks occupied hex geometry, boundary-inclusive; `GetCellAt` resolves one deterministic coordinate. On a boundary, containment must not accidentally return false just because rounding picked an empty neighbor.
- An empty collection never collides. Occupancy alone is not a collision: narrow-phase must test actual hex polygons, rejecting empty triangular corners of their bounding boxes.

### 3. Collision integration and hard cases

- Add dedicated dispatch paths for a hex collection on either side: leaf → hex returns the querying leaf's displacement; hex → leaf returns its negation. Return before the dispatcher's directional-AARect post-processing, which cannot compute bounds for aggregates. Actor-side `SolidSides` does not make hex terrain directional. Unsupported pairs return false/zero without recursive delegation. Static no-op movement does not excuse an inverted returned vector.
- Enumerate an entity's default leaves before computing bounds. A compound entity is not a point at world origin. Keep existing shape-selector semantics intact. Preserve the engine's existing first-nonzero-leaf response convention for compound entities; whole-compound constraint solving is not included. The adjacent-cell response guarantee applies to a single effective leaf or selected shape, not an arbitrary compound actor.
- Use conservative query bounds to identify candidate axial cells, then exact polygon narrow-phase. Transform **all four corners** of the query AARect expanded by hex extents into fractional axial space and conservatively bound integer candidates. For very sparse, enormous query ranges, cap the range walk by falling back to filtering occupied cells; do not iterate billions of empty coordinates. Clip candidate ranges to representable axial coordinates before integer conversion, compare range sizes without multiplication overflow, and avoid integer wraparound at loop endpoints.
- Reuse polygon SAT for supported shape pairs. Do not replace circles with bounding rectangles in narrow-phase.
- Determine boolean overlap independently of the aggregate separation vector; opposing contacts can cancel even though overlap exists. `CollisionRelationship.RunPair` currently exits when separation is zero: add a hex-aware contact path that still records overlap and fires events, applying physics only when a usable vector exists. Keep existing non-hex behavior unchanged.
- For multiple cells, use deterministic, bounded resolution against a provisional translated querying shape, re-querying candidates as position changes. Do not mutate the real entity during a query, return the first arbitrary cell response as the whole answer, sum opposing MTVs blindly, or combine X/Y components of diagonal normals as square `TileShapes` does.
- Resolver termination must be explicit: fixed axial candidate order, bounded iteration count, progress tolerance, and internal resolved/unresolved outcome. On cycling or budget exhaustion, return zero displacement rather than an unverified partial escape; retain true overlap for queries/events. This is a documented trapped-object limitation, not successful resolution.
- Reuse the existing normal-based impulse path. Do not set the square-grid `axisAlignedAggregate` flag for hex polygon contacts.
- **Early risk gate — exposed-boundary traversal:** start an actor outside two adjacent occupied hexes, approach their exposed boundary with shallow penetration, then traverse it near the endpoint of their internal shared edge. After response, no positive penetration beyond tolerance may remain; the actor must not be pushed into the neighboring solid cell or falsely report no collision. Test traversal in both directions and both orientations. This is distinct from spawning an actor deep inside the combined solid region. If the bounded resolver cannot satisfy these boundary cases, retain and report the precise failing fixtures before expanding the collision architecture. Do not claim seamless terrain support without evidence.
- **Boundary-aware response is an option, not a predetermined mesh requirement.** An edge is exposed when its axial neighbor is unoccupied; boundary information can therefore be derived from occupancy without first merging cells into a polygon mesh. If traversal tests demonstrate a need for boundary handling, evaluate that approach and test add/remove updates. Simply dropping SAT axes corresponding to internal edges is not a sufficient collision algorithm. Failure of greedy per-cell resolution alone does not prove that a union-mesh resolver is necessary.
- Fully embedded/trapped objects and high-speed tunneling retain discrete-collision limitations. Queries/resolution must terminate and remain finite; do not promise globally shortest escape through arbitrary solid regions.
- Initial events identify the entity and **collection**, as existing static-geometry relationships do; per-cell contact events are not required.

### 4. Drawing and lifecycle without expanding Screen

Implement the collection as one debug `IRenderable`, delegating to its polygons in the existing shapes batch. Register through `Screen.Add(IRenderable)` and remove through `Screen.Remove(IRenderable)`. Each cell belongs to the collection, not the screen render list.

This avoids new `Screen.Add(HexShapes)` overloads, tile-added subscriptions, teardown leaks, and expensive per-cell registrations. Dynamic add/remove/clear is immediately reflected on subsequent draws. Treat the collection as one layer/Z unit; per-cell draw sorting is not included. Do not call batch Begin/End inside Draw.

Use the existing generic `Screen.AddCollisionRelationship` static-geometry overload. Prove type inference with a real `Factory<T>` call in a compile-tested screen fixture. No new public/virtual base-class members are expected; if one becomes necessary, obtain approval per `engine-tdd` before implementing it.

## Test-first work slices

Use xUnit theories for orientation/shape variants, Shouldly assertions, alphabetical test ordering, and explicit expected values with float tolerances. Favor a small set of meaningful fixtures over exhaustive permutations. Mirror namespaces in test folders.

### A — Layout (coder)
- [ ] Add `tests/FlatRedBall2.Tests/Math/HexCoordinateTests.cs` and `HexGridTests.cs`; run red.
- [ ] Cover value identity, six unique neighbors at distance one, known nontrivial distance, and invalid configuration.
- [ ] Cover known centers/corners plus negative/nonzero-origin round trips in both orientations. Include points on either side of a slanted edge; center-only round trips can conceal an incorrect inverse.
- [ ] Implement coordinates/layout; run green.

### B — Occupancy and geometry (coder)
- [ ] Add `tests/FlatRedBall2.Tests/Collision/HexShapesTests.cs`; run red for add/remove/clear, duplicate add, world lookup, and containment at an occupied/empty shared boundary.
- [ ] Add exact overlap tests for AARect/Circle/Polygon, including a bounding-box corner outside the actual hex and an entity away from the origin with an offset child.
- [x] Implement sparse collection and safe candidate enumeration; run green. `CollidesWith_SparseNegativeCellAndMultiCellAARect_MatchesIndividualCellGeometry` compares candidate enumeration with individual cell polygons for negative sparse cells and multi-cell bounds in both orientations.

### C — Resolution and relationships (coder; highest risk)
- [x] Write and run the actual exposed-boundary approach/traversal risk fixtures in both orientations and both traversal directions. `GetSeparationVector_TraversingExposedBoundaryNearSharedEdgeEndpoint_LeavesActorOutsideEachCell` starts outside, moves through shallow contact along the exposed contour near a shared-edge endpoint, and verifies each cell polygon independently after every response.
- [x] Restore and retain the separate embedded-object regression fixture: radius `10`, flat-top cells `(0,0)` and `(1,0)`, `AARect` size `4×4`, actor center `(7.5, 4.33)`. Keep its unresolved escape expectation explicit; do not delete it to make the suite green or treat its failure as an exposed-boundary failure. Also cover the specified finite unresolved fallback and overlap/events independently of successful escape.

**Finding from 2026-09-11 — embedded multi-cell resolution remains unresolved:** the two cell centers are `(0,0)` and approximately `(15,8.660)`. The tested actor is at their midpoint, on an internal shared edge and inside the combined solid region. It remained overlapping after one `MoveFirstOnCollision` pass; the attempted bounded greedy provisional SAT resolver did not produce a verified escape. The attempt and test were subsequently removed. This demonstrates a failure of that recovery approach, not that exposed-boundary traversal fails or that a union-mesh resolver is required. Full escape from arbitrary embedded positions is not guaranteed by the scope; unresolved response must still terminate, return no unverified displacement, and preserve overlap/contact reporting.
- [x] Implement bounded resolution and dispatcher integration; confirm querying does not alter shape/entity state.

**Finding from 2026-09-11 — corrected risk gate:** `GetSeparationVector_TraversingExposedBoundaryNearSharedEdgeEndpoint_LeavesActorOutsideEachCell` is green for flat/pointy layouts and both contour directions. It uses an outside start and three shallow-contact positions across the exposed contour, rather than treating the two shared-edge endpoints as traversal directions. The bounded provisional-copy resolver clears positive penetration to `0.001` without mutating the queried actor. The embedded `(7.5,4.33)` fixture remains a contact/termination regression, not an escape guarantee; a seven-cell enclosure proves that zero usable separation still fires collision events without moving the actor.

**Finding from review follow-up:** `HexShapes` now expands entity leaf shapes for direct overlap and separation queries, so `HexShapes -> Entity` has the same boolean/separation meaning as the reverse ordering. Zero-usable-separation event detection routes through `CollisionDispatcher` and is covered in both relationship orderings. Candidate selection no longer sorts or probes all occupied cells for each provisional-resolution iteration: it uses a deterministic axial window, with a conservative occupied-cell bounds fallback when that window is larger than the sparse collection. Candidate window expansion clips axial bounds before enumeration, uses `long` loop indices, and has a regression for the representable lower `int` boundary so neither endpoint arithmetic nor inclusive iteration can wrap.
- [ ] Add `tests/FlatRedBall2.Tests/Collision/HexCollisionRelationshipTests.cs`: trigger (no movement), move-first (terrain unchanged), and bounce against a slanted edge. Verify reflected velocity, not just overlap events.
- [ ] Cover reversed pair dispatch/sign (including actor AARect with restricted `SolidSides`), selected/non-default shapes, removal while overlapping, and overlap independent of zero/cancelled resolution. Direct reversed boolean/separation and zero-usable-separation contact reporting are now covered, but the remaining selector, restricted-side, lifecycle, and enter/exit cases are still open.
- [x] Prove generic screen registration with Factory-created entities and automatic collision execution using the existing headless screen pattern. `ScreenAddCollisionRelationship_FactoryEntitySteppedThroughAutomation_ReportsResolvedPosition` uses the generic static-geometry overload and automation-mode NDJSON step/query ordering; it is green.

### D — Debug renderable and lifecycle (coder)
- [ ] Add headless tests for single render-list registration, collection settings on later additions, clear/remove, and screen transition cleanup; run red before wiring.
- [ ] Implement aggregate drawing using existing Polygon behavior; run green.
- [ ] Supply a small manual-test harness only if needed to verify rendered output, following `coder.md` and `sample-project-setup`. Do not create a game or launch the app. Ask a human to check both orientations, origin alignment, outlines/fill, and visual/collider agreement. Headless tests are not pixel validation.

### E — Documentation and review (docs-writer, then qa)
- [ ] Document radius, center origin, Y-up axes, negative cells, tie semantics, immutable layout, supported collision pairs, and discrete/multi-contact limitations in public XML docs.
- [x] User documentation decision: keep usage guidance in the public `hex-grid` skill rather than adding `docs/hex-grid.md`; broader FRB2 documentation does not currently call for a standalone page.
- [x] Read `.claude/skills/skills-writer/SKILL.md`, add the concise `frb-skills/hex-grid/SKILL.md`, and route axial layouts away from square-grid arithmetic in `frb-skills/tile-grid/SKILL.md`.
- [ ] QA review: dispatch recursion/sign, multi-contact response, negative-cell candidate bounds, compound shapes, no query-time mutation, lifecycle, and existing square collision regressions.

## Validation and completion

Run from repository root with bounded tool timeouts:

```sh
dotnet test tests/FlatRedBall2.Tests/ --filter 'FullyQualifiedName~Hex'
dotnet test tests/FlatRedBall2.Tests/ --filter 'FullyQualifiedName~Collision'
dotnet build src/FlatRedBall2.csproj
dotnet test tests/FlatRedBall2.Tests/
```

Also discover/verify `src/Kni/FlatRedBall2.Kni.csproj` and build that backend to catch accidental MonoGame-only coupling; it compiles the shared engine source. Do not add new packages for hex math/collision.

Completion means the above slices are checked off, actual failing/passing test results are recorded, exposed-boundary traversal passes, and unresolved embedded-object recovery is explicitly distinguished from seam behavior. Retain regression fixtures and report any unresolved escape expectation alongside the tested safe fallback; do not conceal it by deleting tests or require a mesh architecture without supporting evidence. The final handoff names the branch, changed files, validation results, and any pending human visual check. No commits or pushes without instruction.

**Hand off to coder agent for implementation.**
