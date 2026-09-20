# Calling C# from Mojo — a practical guide

How this repo makes Mojo call into C#: C# is compiled to a **native shared
library** with NativeAOT, and Mojo `dlopen`s it and calls exported functions
through the C ABI. No Python, no IPC, no sockets — a direct in-process
native call.

```
┌─────────────┐   dlopen    ┌──────────────────────┐
│  mojo binary │ ──────────▶ │  NativeMesh.so (C#)  │
│  OwnedDLHandle│  fn ptr    │  [UnmanagedCallersOnly]│
└─────────────┘  call       └──────────────────────┘
```

## The mental model

Two facts make this work:

1. **NativeAOT produces a real `.so`.** `dotnet publish -c Release -r linux-x64`
   with `<PublishAot>true</PublishAot>` compiles C# to native code and bundles
   a mini runtime (GC, thread pool) inside the library. Anything that can load
   a `.so` can call it — no `dotnet` needed at run time.
2. **Mojo speaks the C ABI.** `std.ffi.OwnedDLHandle` wraps `dlopen`, and
   `get_function` returns a callable that marshals Mojo arguments to C types
   (like `ctypes`).

The contract between the two is the **C ABI**: functions must take and return
*blittable* types — scalars, pointers, structs of blittables. No strings, no
`List<T>`, no exceptions across the boundary.

## Step 1 — the C# export

```csharp
using System.Runtime.InteropServices;

public static unsafe class NativeMath
{
    [UnmanagedCallersOnly(EntryPoint = "add")]   // the symbol name in the .so
    public static int Add(int a, int b) => a + b;
}
```

Rules for `[UnmanagedCallersOnly]`:

- `static` only, unmanaged signature only (pointers + blittable scalars)
- `EntryPoint` sets the exported symbol name — this is what `get_function`
  looks up
- **Exceptions must not escape** — there is no one to catch them. They become
  `FailFast` crashes (we hit this). Wrap risky bodies in `try/catch` yourself.
- No generics, no delegates — flat C surface area

## Step 2 — the csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>   <!-- pointers need this -->
    <PublishAot>true</PublishAot>
    <IlcInstructionSet>avx2</IlcInstructionSet>   <!-- see gotchas -->
  </PropertyGroup>
</Project>
```

## Step 3 — publish and verify

```bash
dotnet publish -c Release -r linux-x64
# produces: bin/Release/net10.0/linux-x64/publish/NativeMesh.so

nm -D bin/Release/net10.0/linux-x64/publish/NativeMesh.so | grep add
# 000000000006f360 T add@@V1.0     ← T = exported text symbol ✓
```

If `nm` doesn't show your function, check `EntryPoint` spelling and that the
method is `public static`.

## Step 4 — call it from Mojo

```mojo
from std.ffi import OwnedDLHandle

def main() raises:
    var lib = OwnedDLHandle("path/to/NativeMesh.so")   # dlopen, stays open
    var add = lib.get_function[Int32]("add")           # int add(int,int)
    print(add(Int32(40), Int32(2)))                    # 42
```

That's the whole mechanism. `get_function` has one comptime parameter — the
**return type** (default `NoneType` = void). For a function that returns a
value, pass it explicitly:

```mojo
var probe = lib.get_function[Int64]("probe_hw")     # long probe_hw()
var f = lib.get_function[Float32]("probe_cos")      # float probe_cos(float)
print(f(Float32(0.5)))
```

## Type marshalling — where beginners get burned

`get_function` is loosely typed like `ctypes` — **you** must get the ABI right;
nothing checks it. Mismatch = garbage values or a crash.

| C# parameter | Mojo argument | Notes |
|---|---|---|
| `int` (i32) | `Int32(x)` | ⚠️ **Mojo `Int` is i64** — passing `Int` to a C# `int` param shifts the arg |
| `long` (i64) | `Int` or `Int64` | natural pairing |
| `float` (f32) | `Float32(x)` | not `Float64` |
| `double` (f64) | `Float64(x)` | |
| `float*` / `int*` | `Pointer[Float32, ...]` / `Pointer[Int32, ...]` | zero-copy — C# writes your buffer directly |
| `void` return | `get_function(...)` default | use `get_function[T]` for returns |

The pointer case is the powerful one: allocate in Mojo (`alloc`), pass the
pointer, C# fills the buffer in place — exactly what `fill_grid` does. The
caller owns the memory; the callee borrows it for the call duration.

```mojo
var v_alloc = alloc(Layout[Float32](count=n * n * 3))
fill(v_alloc.unsafe_ptr(), fptr, n)   # C# writes into mojo's memory
dealloc(v_alloc^)                      # still mojo's job to free
```

## Gotchas we hit in this repo

1. **`PlatformNotSupportedException` on AVX intrinsics under NativeAOT.**
   NativeAOT compiles for the x86-64 baseline ISA by default, so `Avx.*` /
   `Vector256.*` intrinsics think the CPU lacks AVX2 and throw.
   Fix: `<IlcInstructionSet>avx2</IlcInstructionSet>` (or `native`).

2. **Return values need `get_function[T]`.** `get_function("probe_hw")`
   returns the callable but treats the result as void — we printed `None`
   before figuring this out.

3. **Mojo `Int` is 64-bit; C# `int` is 32-bit.** Match widths deliberately:
   we used `long n` on the C# side and pass mojo `Int` — both i64.

4. **Managed exceptions crossing the FFI boundary = process death.**
   The unhandled `PlatformNotSupportedException` inside `Parallel.For`
   became `RuntimeFailFast` → SIGABRT. Exceptions can't propagate to Mojo —
   catch them in C# or prevent them.

5. **`OwnedDLHandle` keeps the library loaded.** Keep the handle alive as
   long as you use the function pointers from it.

## The other direction — C# calling Mojo

Symmetric. Build any Mojo file as a shared lib with a C export:

```mojo
@export
def answer() abi("C") -> Int32:
    return 42
```

```bash
mojo build --emit shared-lib -o libanswer.so answer.mojo
```

```csharp
[LibraryImport("libanswer.so")]
private static partial int answer();
```

(`LibraryImport` = source-generated P/Invoke, no runtime marshalling.)

## Alternatives, and when to use them

| Route | When |
|---|---|
| **NativeAOT + dlopen** (this guide) | Hot kernels, batch calls, zero marshalling — the default choice |
| `hostfxr` hosting | You need the *full* .NET runtime (NuGet libs, reflection, no AOT trimming) — heavier, more moving parts |
| Mojo→Python→pythonnet | C# code isn't AOT-compatible and you can't change it — works, two interop hops, slow |

## Working code

- `csharp_native/MeshFill.cs` — the exported kernel
- `csharp_native/NativeMesh.csproj` — the csproj above
- `mojo/call_csharp.mojo` — the caller + benchmark harness

```bash
pixi run build-csharp-native && pixi run bench-mojo-csharp
```

## Owning the runtime — async + live shared state

`fill_grid_async` runs the fill on .NET's thread pool and returns immediately.
`get_progress_ptr` hands out a pointer to unmanaged counter memory that worker
threads `Interlocked.Increment`. Mojo polls that pointer and watches progress
live — C# never calls back; it just writes its own memory.

```mojo
var progress = Pointer[Int32, MutAnyOrigin](
    unsafe_from_address=Int(lib.get_function[Int64]("get_progress_ptr")())
)
fill_async(vptr, fptr, n)               # returns instantly
while Int(progress.unsafe_load()) < n:  # mojo watches the CLR work
    sleep(0.0005)
```

This is the shared-memory pattern: one pointer handoff, then each side reads
and writes on its own schedule. The whole CLR — GC, thread pool, `Task` — is
hosted inside the Mojo process via the `.so`.

Run: `pixi run bench-mojo-csharp-live`
