"""Mojo geometry engine exposed to Blender as a CPython extension module.

Functions:
  cube()                       -> {"verts": [[x,y,z]...], "edges": [...], "faces": [...]}
  grid_mesh(n)                 -> same dict shape, n x n grid with sine heights (pure lists)
  grid_into(verts, faces, n)   -> fills preallocated numpy buffers in place (zero marshalling)
"""

from std.math import cos, sin
from std.os import abort
from std.python import Python, PythonObject
from std.python.bindings import PythonModuleBuilder

from grid_fill import fill_grid


def cube() raises -> PythonObject:
    var verts: PythonObject = [
        [-1.0, -1.0, -1.0],
        [-1.0, 1.0, -1.0],
        [1.0, 1.0, -1.0],
        [1.0, -1.0, -1.0],
        [-1.0, -1.0, 1.0],
        [-1.0, 1.0, 1.0],
        [1.0, 1.0, 1.0],
        [1.0, -1.0, 1.0],
    ]
    var edges: PythonObject = [
        [0, 1], [1, 2], [2, 3], [3, 0],
        [4, 5], [5, 6], [6, 7], [7, 4],
        [0, 4], [1, 5], [2, 6], [3, 7],
    ]
    var faces: PythonObject = [
        [0, 1, 2, 3], [4, 7, 6, 5], [0, 4, 5, 1],
        [1, 5, 6, 2], [2, 6, 7, 3], [4, 0, 3, 7],
    ]
    return Python.dict(verts=verts, edges=edges, faces=faces)


def grid_mesh(n_obj: PythonObject) raises -> PythonObject:
    """Build an n x n grid entirely as Python lists (marshalling included in cost)."""
    var n = Int(py=n_obj)
    var verts = Python.list()
    var size = Float64(n - 1)
    var freq = 0.05
    for i in range(n):
        for j in range(n):
            var fi = Float64(i)
            var fj = Float64(j)
            var z = sin(fi * freq) * cos(fj * freq) * 4.0
            verts.append(
                Python.list(fi / size * 20.0 - 10.0, fj / size * 20.0 - 10.0, z)
            )
    var faces = Python.list()
    for i in range(n - 1):
        for j in range(n - 1):
            var a = i * n + j
            faces.append(Python.list(a, a + 1, a + n + 1, a + n))
    return Python.dict(verts=verts, edges=Python.list(), faces=faces)


def grid_into(
    verts: PythonObject, faces: PythonObject, n_obj: PythonObject
) raises -> PythonObject:
    """Fill preallocated numpy arrays: verts (n*n,3) float32, faces ((n-1)^2,4) int32."""
    var n = Int(py=n_obj)
    var v_addr = Int(py=verts.ctypes.data)
    var f_addr = Int(py=faces.ctypes.data)
    var vptr = Pointer[Float32, origin=MutAnyOrigin](unsafe_from_address=v_addr)
    var fptr = Pointer[Int32, origin=MutAnyOrigin](unsafe_from_address=f_addr)
    fill_grid(vptr, fptr, n)
    return Python.none()


def bpy_cube_add() raises -> PythonObject:
    """Calls bpy.ops.mesh.primitive_cube_add() through the Python interop layer."""
    var bpy = Python.import_module("bpy")
    bpy.ops.mesh.primitive_cube_add()
    return Python.none()


def meshes_new_loop(n_obj: PythonObject) raises -> PythonObject:
    """Calls bpy.data.meshes.new() n times from a Mojo loop."""
    var bpy = Python.import_module("bpy")
    var meshes = bpy.data.meshes
    var n = Int(py=n_obj)
    for _ in range(n):
        _ = meshes.new("tmp")
    return Python.none()


def from_pydata_call(
    mesh: PythonObject,
    verts: PythonObject,
    edges: PythonObject,
    faces: PythonObject,
) raises -> PythonObject:
    """Calls mesh.from_pydata(verts, edges, faces) via interop."""
    mesh.from_pydata(verts, edges, faces)
    return Python.none()


@export
def PyInit_mojo_geo() abi("C") -> PythonObject:
    try:
        var m = PythonModuleBuilder("mojo_geo")
        m.def_function[cube]("cube")
        m.def_function[grid_mesh]("grid_mesh")
        m.def_function[grid_into]("grid_into")
        m.def_function[bpy_cube_add]("bpy_cube_add")
        m.def_function[meshes_new_loop]("meshes_new_loop")
        m.def_function[from_pydata_call]("from_pydata_call")
        return m.finalize()
    except e:
        abort(String("failed to create module: ", e))
