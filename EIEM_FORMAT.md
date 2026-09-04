# EIEM exchange format

## Current authoring format

The offline exporter, Blender add-on and runtime plugin will use one shared
EIEM package with no FBX or OBJ conversion step:

```text
<mod>/
  mod.ini
  meshes/<name>.mesh
  skeletons/<name>.skeleton
  materials/<name>.mat
  textures/<name>.png
```

- An `EIEMESH` v3 `.mesh` preserves positions, source normals/tangents,
  colours, all eight UV channels with their original dimensions, submeshes,
  weights, bind poses, bone hashes, BlendShapes and its compact bone palette
  as paths into the shared skeleton.
- A `.skeleton` preserves the ordered transform hierarchy and local TRS once
  per Prefab hierarchy. It is not duplicated for every Mesh or renderer.
- A `.mat` file identifies the logical game Material to copy and typed shader
  parameter/texture overrides.
- `mod.ini` declares reusable resources and `Render` operations. Resource
  sections may include optional `target.path` and `target.asset` fields for
  global logical redirection; `Render` remains the only place for instance
  actions such as `handling=skip`. It contains user-readable paths only,
  never Bundle hashes or runtime pointers.

The full normative contract is maintained in
`docs/model-replacement-design.md`.

## Blender status

`tools/Blender/eiem_blender_addon.py` now provides the package entry points:

- **File > Import > EIEM mod package** reads `mod.ini`, creates resource Mesh
  objects, material slots, PNG images and one Armature for each unique
  skeleton hierarchy. Meshes are grouped into LOD collections.
- **File > Export > EIEM mod package** writes the same directory layout and
  validates it by reading the generated files again.

Known Blender data is stored in its native editing system: UV0..UV7 in UV
layers, vertex colours in Color Attributes, and BlendShapes in Shape Keys.
Blender 5 does not expose writable custom split normals or a native tangent
layer, so exact source normals, tangents and UV Z/W components use named Mesh
Attributes and are written back losslessly.

Mesh, Material, and Texture sections generated from a resolved game asset
include their logical target identity automatically, so the same package can
be consumed by the runtime redirector without hand-editing `mod.ini`.

`EIEMESH` v2 remains readable. On import, its legacy renderer-local Skeleton
palette is migrated to per-Mesh binding metadata and identical hierarchies are
deduplicated. Blender and AnimeStudio write v3 only.
