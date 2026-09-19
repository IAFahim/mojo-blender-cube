bl_info = {
    "name": "Mojo Default Cube",
    "author": "Devin",
    "version": (1, 0, 0),
    "blender": (5, 2, 0),
    "location": "Startup",
    "description": "Creates the default cube on startup using a Mojo-compiled geometry engine",
    "category": "Mesh",
}

import os
import sys

import bpy

_ADDON_DIR = os.path.dirname(os.path.abspath(__file__))
if _ADDON_DIR not in sys.path:
    sys.path.insert(0, _ADDON_DIR)

import mojo_geo


def spawn_mojo_cube():
    if bpy.data.objects.get("MojoCube"):
        return None
    data = mojo_geo.cube()
    mesh = bpy.data.meshes.new("MojoCube")
    mesh.from_pydata(data["verts"], data["edges"], data["faces"])
    mesh.update()
    obj = bpy.data.objects.new("MojoCube", mesh)
    bpy.context.scene.collection.objects.link(obj)
    return obj


@bpy.app.handlers.persistent
def _on_startup(_dummy):
    try:
        spawn_mojo_cube()
    except Exception as exc:
        print("mojo_cube: failed to spawn cube:", exc)


def _timer_spawn():
    try:
        spawn_mojo_cube()
    except Exception:
        pass
    return None


def register():
    if _on_startup not in bpy.app.handlers.load_factory_startup_post:
        bpy.app.handlers.load_factory_startup_post.append(_on_startup)
    bpy.app.timers.register(_timer_spawn, first_interval=0.1)


def unregister():
    if _on_startup in bpy.app.handlers.load_factory_startup_post:
        bpy.app.handlers.load_factory_startup_post.remove(_on_startup)
    if bpy.app.timers.is_registered(_timer_spawn):
        bpy.app.timers.unregister(_timer_spawn)


if __name__ == "__main__":
    register()
