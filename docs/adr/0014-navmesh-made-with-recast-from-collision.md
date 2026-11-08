# A cell's navmesh is made with Recast from its meshes' collision

The navmesh is what NPCs and followers walk on: without one a new room has no followers, and a cell whose objects moved
keeps paths through the new table. Until now only the Creation Kit made one, by hand or by its generator and hours of
cleanup in a window modders need a course for. The user started this project wanting that to be a single call
(2026-11-08).

A navmesh is the walkable surface of what actors collide with. The core reads that collision from the meshes the cell's
references show — each node's Havok collision object on the layers actors stand on or bump into (static, animated static,
props, trees, terrain, ground, stair helpers, invisible walls, nav cuts; clutter, triggers and water left out) — with
NiflySharp, the C# NIF library generated from nifxml. Compressed mesh shapes, convex shapes, boxes, capsules, lists and
their transforms are decoded; Havok units are 69.99 game units. Doors are left out: actors walk through their frames.
Recast, through DotRecast (its C# port), voxelizes the triangles at 8 units and gives the walkable polygons as triangles
(an actor 120 tall, 8 wide, climbing 32, up slopes of 50°).

What the game stores was read off its own navmeshes (the Sleeping Giant Inn's and Breezehome's, a Creation Club
plugin's) and is written the same way:

- triangles counter-clockwise seen from above, each edge's neighbour (-1 at a border) and the flag `Found`;
- the triangle before each load door is the one whose centre is nearest the door, flagged `Door`, linked in
  `DoorTriangles` with the constant every door link carries;
- the grid is the vertices' bounds cut into divisor × divisor cells, row by row (y, then x), each listing the triangles
  whose bounds overlap it;
- the Navigation Info Map (`NAVI`) is merged by the game from every plugin's version: a plugin carries only its own
  navmeshes' entries, as the Creation Club's does. The core writes an entry only where a navmesh is new or its load
  doors changed — its point the mean of its vertices, its doors, the cell — and keeps the winning version's preferred
  pathing beside it, as the Creation Kit's plugins do.

Recast also walks table tops and the gaps behind furniture. The pieces no edge joins are kept where a load door stands
or where a piece is at least a sixth of the largest. On the inn and Breezehome the result covers 89–97% of the game's
navmesh and 76–86% of it lies on the game's; the rest is mostly narrow space a level designer leaves out.

The navmesh replaces the cell's largest (its FormID kept, so nothing that points at it breaks); a cell without one gets
a new one. Interior cells only, for now: an exterior cell's needs its ground and the edges it shares with its
neighbours' navmeshes, whose links point at triangle numbers that change.

## Consequences

- Two packages in the helper: NiflySharp (GPL-3.0, with Miniball, Apache-2.0) and DotRecast.Recast (Zlib).
- A generated navmesh is no hand-tuned one: cover data is not written, and narrow gaps are walkable. NPCs path through the
  room; a level designer's polish is the Creation Kit's still.
- The game's acceptance is shown in game, not by a test here: the first in-game check is the inn's NPCs walking it.
