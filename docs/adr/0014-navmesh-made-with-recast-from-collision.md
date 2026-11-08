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
  navmeshes' entries, as the Creation Club's does. An entry is computed from its navmesh: `MergedTo` and
  `PreferredMerges` together are exactly the navmeshes it links to, `PreferredMerges` those reached from a `Preferred`
  (road) triangle; `PreferredMergesFlag` is the share of road triangles as a float's bits (Riverwood's 58 of 392 is
  0.148); `Point` is the mean of the vertices; `LinkedDoors` its load doors. The winning version's preferred pathing
  is kept beside the entries, as the Creation Kit's plugins do.

Outdoors a cell's navmeshes end exactly at its edges, and the edges along a side link to the neighbouring cell's
navmesh both ways — by overlap: the two sides' vertices differ. The core reads the ground of the cell and the eight
around it and their objects, builds one Recast tile that is the cell's square (the border read as far as an actor
reaches), and links each edge along a side to the neighbour's edge it overlaps most at a height within 64 units; the
neighbour's edges along that side are relinked the same way, and its navmesh is written with them. A cell often has
several navmeshes (Riverwood four): the new surface is shared out among them by where each triangle lies, so every
one keeps its FormID and nothing that points at it breaks; one no triangle lies on is kept as it was, unlinked from
the rest. Each new triangle keeps the road and water flags of the old one under it. Water deeper than 80 units — the
planes the cells place, by their bounds, and the cell's water level — gets no navmesh; shallower is flagged `Water`.

Recast also walks table tops, roofs and the gaps behind furniture. The pieces no edge joins are kept where a load
door stands, where they meet a neighbouring cell's navmesh, or where a piece is at least a sixth of the largest. On the
inn and Breezehome the result covers 82–97% of the game's navmesh and 76–86% of it lies on the game's; on Riverwood 88%
and 68%: the rest is mostly narrow space and steep ground a level designer leaves out.

A room the project made gets a new navmesh.

## Consequences

- Two packages in the helper: NiflySharp (GPL-3.0, with Miniball, Apache-2.0) and DotRecast.Recast (Zlib).
- A generated navmesh is no hand-tuned one: cover data is not written, and narrow gaps are walkable. NPCs path through the
  room; a level designer's polish is the Creation Kit's still.
- The game's acceptance is shown in game, not by a test here. The first in-game check passed on 2026-11-08 in the test
  instance, with a plugin of generated navmeshes for the Sleeping Giant Inn and Riverwood. The inn's NPCs walked it,
  and so did Riverwood's, with the neighbouring cells' navmeshes relinked.
