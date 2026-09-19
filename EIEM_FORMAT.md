# EIEM exchange format

## Current authoring format

The offline exporter, Blender add-on and runtime plugin use one shared EIEM
package with no FBX or OBJ conversion step:

```text
<mod>/
  mod.ini
  meshes/<name>.mesh
  skeletons/<name>.skeleton
  materials/<name>.mat
  textures/<name>.png
  physics/components.json       # optional original native authoring graph
  physics/<id>.bin
  physics/<id>.schema.json
  physics/<id>.data.json
```

- AnimeStudio writes an `EIEMESH` v3 `.mesh` preserving positions, source normals/tangents,
  colours, all eight UV channels with their original dimensions, submeshes,
  weights, bind poses, bone hashes, BlendShapes and its compact bone palette
  as paths into the shared skeleton. The Blender authoring repository upgrades
  edited meshes to v5 by adding hierarchy-index and authoritative source-slot
  identities used by the runtime across world, NPC and UI Prefabs.
- A `.skeleton` preserves the ordered transform hierarchy and local TRS once
  per Prefab hierarchy. It is not duplicated for every Mesh or renderer.
- A `.mat` file identifies the logical game Material to copy and typed shader
  parameter/texture overrides.
- `physics/` preserves supported BeyondDynamicBone components found in the
  selected Prefab, their CAB/PathID identities, references, original bytes,
  TypeTree schemas, decoded fields and the relevant Prefab Transform graph.
  It is authoring input for Blender and does not declare a runtime action.
- `mod.ini` declares reusable resources and `Render` operations. Resource
  sections may include optional `target.path` and `target.asset` authoring
  identity fields; these do not execute global redirection. `Render` owns
  instance actions such as `handling=skip`. It contains user-readable paths only,
  never Bundle hashes or runtime pointers.

The runtime and cross-Prefab binding contracts are maintained in
[ssice-a/EIEM](https://github.com/ssice-a/EIEM/tree/main/docs). The Blender
authoring implementation is maintained in
[ssice-a/EIEM-blender](https://github.com/ssice-a/EIEM-blender).

## Blender status

`EIEM-blender/eiem_blender_addon.py` provides the package entry points:

- **File > Import > EIEM mod package** reads `mod.ini`, creates resource Mesh
  objects, material slots, PNG images and one Armature for each unique
  skeleton hierarchy. Meshes are grouped into LOD collections.
- **File > Export > EIEM mod package** writes the same directory layout and
  validates it by reading the generated files again.

Known Blender data is stored in its native editing system: UV0..UV7 in UV
layers, vertex colours in Color Attributes, and BlendShapes in Shape Keys.
Custom split normals are installed through `normals_split_custom_set` and
affect smooth shading. A source-normal backup preserves original float values
only while the native normal state is unchanged. Tangents and UV Z/W components
use named Mesh Attributes; this does not automatically generate tangents for
newly added geometry.

Original packed tangent frames must be decoded, not replaced by UV-based
recalculation. A package round trip alone does not prove that the source AB's
channels were all exported. The EIEM repository's `docs/vertex-data-contract.md`
records the source-data tests and the distinction between preservation and
generation: [vertex data contract](https://github.com/ssice-a/EIEM/blob/main/docs/vertex-data-contract.md).

Mesh, Material, and Texture sections generated from a resolved game asset
include their logical target identity for authoring. The shared resource files
are consumed by the runtime backend when referenced by Render operations;
source metadata is not itself an executable replacement rule.

`EIEMESH` v2 remains readable. On import, its legacy renderer-local Skeleton
palette is migrated to per-Mesh binding metadata and identical hierarchies are
deduplicated. AnimeStudio writes v3 source packages; the current Blender add-on
reads v2-v5 and writes v5 authoring output.
