"""A Skyrim cell in Blender and back — the Skyrim Modding Tools' exportCell and importCell.

exportCell writes a cell into a folder: cell.json, the meshes and textures its references show, and this script.
In Blender, with the PyNifly add-on enabled:

    import_cell(folder)     places every reference of the cell where the game places it: each mesh imported once
                            through PyNifly, each reference an instance of it, named after its reference
    export_layout(folder)   writes layout.json beside cell.json: where each reference stands now, the copies made
                            with Shift+D, and — by their absence — the ones deleted; importCell brings it into a project

The game turns a reference by its rotation's z, then y, then x, each angle clockwise seen down its axis
(NiMatrix3::SetEulerAnglesXYZ): in Blender that is the Euler order ZYX with every angle negated. Positions and
sizes are game units, one to one — as PyNifly imports a mesh.
"""

import json
import math
import os

import bpy
from mathutils import Euler

ROOT = "Skyrim Cell"
MESHES = "Skyrim Meshes"
TAU = 2 * math.pi
# The parts of a mesh the game never draws, by the names PyNifly gives them: culling volumes and editor markers.
HIDDEN_PARTS = ("MultiBound", "BSBound", "EditorMarker", "bhkCollision", "bhkRigidBody")


def game_to_blender_rotation(rotation):
    """A reference's rotation as the game stores it — radians about x, y, z — as a Blender Euler."""
    x, y, z = rotation
    return Euler((-x, -y, -z), "ZYX")


def blender_to_game_rotation(matrix):
    """An object's world rotation as the game stores a reference's: radians about x, y, z, each in [0, 2π)."""
    euler = matrix.to_quaternion().to_euler("ZYX")
    return [round((-angle) % TAU, 6) for angle in (euler.x, euler.y, euler.z)]


def _layer_collection(layer, name):
    if layer.collection.name == name:
        return layer
    for child in layer.children:
        found = _layer_collection(child, name)
        if found:
            return found
    return None


def _model_collection(model, folder, meshes, library, failed):
    """The collection holding one model's objects, imported once through PyNifly — None where its file is missing or unreadable."""
    if model in library:
        return library[model]
    library[model] = None
    file = os.path.join(folder, *model.replace("/", "\\").split("\\"))
    if not os.path.isfile(file):
        failed.append(f"{model}: not exported (no game file)")
        return None
    collection = bpy.data.collections.new(os.path.basename(model))
    meshes.children.link(collection)
    layer = _layer_collection(bpy.context.view_layer.layer_collection, collection.name)
    bpy.context.view_layer.active_layer_collection = layer
    before = set(bpy.data.objects)
    try:
        bpy.ops.import_scene.pynifly(filepath=file, import_collisions=False, import_animations=False,
                                     import_tris=False, create_collection=False)
    except Exception as error:  # a mesh PyNifly cannot read: the reference stays an empty where it stands
        failed.append(f"{model}: {error}")
    for obj in [o for o in bpy.data.objects if o not in before]:
        # What the game never draws: the bounding volumes it culls with (a tree's BSMultiBoundOBB, a box taller than the tree)
        # and the markers only the Creation Kit shows.
        if any(word in obj.name for word in HIDDEN_PARTS):
            bpy.data.objects.remove(obj, do_unlink=True)
            continue
        for other in list(obj.users_collection):
            if other != collection:
                other.objects.unlink(obj)
        if collection not in obj.users_collection:
            collection.objects.link(obj)
    if not collection.objects:
        failed.append(f"{model}: nothing imported")
        return None
    library[model] = collection
    return collection


def _label(reference):
    base = reference.get("base") or {}
    name = reference.get("editorId") or base.get("editorId") or reference["formId"]
    return f"{name} [{reference['formId'].split(':')[0]}]"[:63]


def _ground(ground, collection):
    """An exterior cell's ground as a mesh: 33 × 33 heights, 128 units apart, west to east and south to north."""
    heights = ground["heights"]
    ox, oy = ground["origin"]
    step = ground["spacing"]
    verts = [(ox + x * step, oy + y * step, heights[y][x]) for y in range(33) for x in range(33)]
    faces = [(y * 33 + x, y * 33 + x + 1, (y + 1) * 33 + x + 1, (y + 1) * 33 + x) for y in range(32) for x in range(32)]
    mesh = bpy.data.meshes.new("Ground")
    mesh.from_pydata(verts, [], faces)
    mesh.update()
    earth = bpy.data.materials.new("Ground")
    earth.diffuse_color = (0.22, 0.2, 0.14, 1)
    mesh.materials.append(earth)
    # Sculpted or with its vertices moved up and down, export_layout writes its heights back: importCell sets the cell's
    # ground to them, the edges its neighbours share with it moved too. Only the heights count, vertex by vertex.
    obj = bpy.data.objects.new("Ground", mesh)
    obj["skyrim_ground"] = True
    collection.objects.link(obj)


def _navmeshes(navmeshes, collection):
    """The cell's navmeshes — where NPCs walk — each a wireframe mesh drawn in front, named after its FormID."""
    if not navmeshes:
        return
    green = bpy.data.materials.new("Navmesh")
    green.diffuse_color = (0.1, 0.85, 0.25, 0.6)
    for navmesh in navmeshes:
        mesh = bpy.data.meshes.new("Navmesh " + navmesh["formId"])
        # Two units above, so the ground does not hide it.
        mesh.from_pydata([(x, y, z + 2) for x, y, z in navmesh["vertices"]], [], [tuple(t) for t in navmesh["triangles"]])
        mesh.update()
        mesh.materials.append(green)
        obj = bpy.data.objects.new("Navmesh " + navmesh["formId"], mesh)
        obj["skyrim_navmesh"] = navmesh["formId"]
        obj.display_type = "WIRE"
        obj.show_in_front = True
        obj.hide_select = True
        collection.objects.link(obj)


def import_cell(folder, clear=True):
    """Places the cell exportCell wrote into `folder`. Answers what it placed and what it could not import."""
    if not hasattr(bpy.ops.import_scene, "pynifly"):
        raise RuntimeError("The PyNifly add-on is not enabled: Edit > Preferences > Add-ons, enable 'NIF format (PyNifly)'.")
    folder = os.path.abspath(folder)
    with open(os.path.join(folder, "cell.json"), encoding="utf-8") as f:
        scene = json.load(f)
    cell = scene["cell"]
    title = f"{ROOT}: {cell.get('editorId') or cell['formId']}"
    if clear:
        for collection in [c for c in bpy.data.collections if c.get("skyrim_folder") == folder or c.name == MESHES + ": " + title]:
            for obj in list(collection.all_objects):
                bpy.data.objects.remove(obj, do_unlink=True)
            for child in list(collection.children_recursive):
                bpy.data.collections.remove(child)
            bpy.data.collections.remove(collection)

    root = bpy.data.collections.new(title)
    root["skyrim_folder"] = folder
    root["skyrim_cell"] = cell["formId"]
    bpy.context.scene.collection.children.link(root)
    meshes = bpy.data.collections.new(MESHES + ": " + title)
    bpy.context.scene.collection.children.link(meshes)

    library, failed = {}, []
    placed = 0
    for reference in scene["references"]:
        model = reference.get("model")
        source = _model_collection(model, folder, meshes, library, failed) if model else None
        obj = bpy.data.objects.new(_label(reference), None)
        if source is not None:
            obj.instance_type = "COLLECTION"
            obj.instance_collection = source
        else:
            obj.empty_display_type = "SINGLE_ARROW" if reference.get("kind") == "PlacedNpc" else "PLAIN_AXES"
            obj.empty_display_size = 32
        obj.location = reference["position"]
        obj.rotation_mode = "ZYX"
        obj.rotation_euler = game_to_blender_rotation(reference["rotation"])
        scale = reference.get("scale") or 1
        obj.scale = (scale, scale, scale)
        base = reference.get("base") or {}
        obj["skyrim_ref"] = reference["formId"]
        obj["skyrim_base"] = base.get("formId") or ""
        obj["skyrim_base_editor_id"] = base.get("editorId") or ""
        obj["skyrim_kind"] = reference.get("kind") or "PlacedObject"
        root.objects.link(obj)
        obj["skyrim_name"] = obj.name
        if reference.get("light"):
            light = bpy.data.lights.new(obj.name + " light", "POINT")
            light.color = reference["light"]["color"]
            light.energy = max(100.0, float(reference["light"]["radius"]) * 20)
            lamp = bpy.data.objects.new(obj.name + " light", light)
            lamp.parent = obj
            lamp.hide_select = True
            root.objects.link(lamp)
        if reference.get("disabled"):
            obj.hide_set(True)
        placed += 1
    if scene.get("ground"):
        _ground(scene["ground"], root)
    _navmeshes(scene.get("navmeshes"), root)
    layer = _layer_collection(bpy.context.view_layer.layer_collection, meshes.name)
    if layer:
        layer.exclude = True
    bpy.context.view_layer.active_layer_collection = _layer_collection(bpy.context.view_layer.layer_collection, root.name)
    answer = {"cell": cell.get("editorId") or cell["formId"], "placed": placed, "meshes": sum(1 for c in library.values() if c),
              "notImported": failed}
    print("import_cell:", json.dumps(answer))
    return answer


def _root(folder=None):
    roots = [c for c in bpy.data.collections if c.get("skyrim_folder")]
    if folder:
        roots = [c for c in roots if os.path.normcase(c["skyrim_folder"]) == os.path.normcase(os.path.abspath(folder))]
    if not roots:
        raise RuntimeError("No Skyrim cell in this file: run import_cell(folder) first.")
    return roots[0]


def _world(obj):
    """An object's world matrix from its own location, rotation and scale and its parents' — matrix_world is stale for a hidden
    object, and for one changed by a script until the scene updates (always, without a window)."""
    matrix = obj.matrix_basis.copy()
    while obj.parent is not None:
        matrix = obj.parent.matrix_basis @ obj.matrix_parent_inverse @ matrix
        obj = obj.parent
    return matrix


def export_layout(folder=None):
    """Writes layout.json beside cell.json: every reference of the cell as it stands now. Answers its path and counts."""
    root = _root(folder)
    folder = root["skyrim_folder"]
    by_reference = {}
    for obj in root.all_objects:
        if "skyrim_ref" in obj:
            by_reference.setdefault(obj["skyrim_ref"], []).append(obj)
    rows = []
    copies = 0
    for form_id, objects in by_reference.items():
        # Shift+D copies an object's properties too: the original is the one still under the name it was placed with.
        original = next((o for o in objects if o.name == o.get("skyrim_name")), objects[0])
        for obj in objects:
            matrix = _world(obj)
            size = matrix.to_scale()
            rows.append({
                "formId": form_id if obj is original else None,
                "copyOf": None if obj is original else form_id,
                "base": obj.get("skyrim_base"),
                "baseEditorId": obj.get("skyrim_base_editor_id"),
                "kind": obj.get("skyrim_kind"),
                "name": obj.name,
                "position": [round(v, 4) for v in matrix.translation],
                "rotation": blender_to_game_rotation(matrix),
                "scale": round((size.x + size.y + size.z) / 3, 4),
            })
            copies += obj is not original
    layout = {"cell": root["skyrim_cell"], "references": rows}
    ground = next((o for o in root.all_objects if o.get("skyrim_ground") and o.type == "MESH"), None)
    if ground is not None and len(ground.data.vertices) == 33 * 33:
        # In the order _ground made them: row by row, south to north, west to east.
        world = ground.matrix_world
        layout["ground"] = {"heights": [[round((world @ ground.data.vertices[y * 33 + x].co).z, 2) for x in range(33)] for y in range(33)]}
    path = os.path.join(folder, "layout.json")
    with open(path, "w", encoding="utf-8") as f:
        json.dump(layout, f, indent=1)
    answer = {"layout": path, "references": len(rows), "copies": copies, "ground": "ground" in layout}
    print("export_layout:", json.dumps(answer))
    return answer
