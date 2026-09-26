# AnimeStudio v1.2.1

- Prefab previews no longer show Unity internal PathID, CAB identifiers, or hash-style references.
- GameObjects show hierarchy paths; meshes, materials, avatars, and controllers prefer readable logical asset paths.
- Bone references show hierarchy paths. If an asset cannot be resolved, the preview keeps its name without exposing an internal numeric ID.
- GUI previews and exported Prefab structure files use the same readable format.

**Install:** download the `net9.0-windows` or `net10.0-windows` package matching the installed .NET Desktop Runtime, extract it, and run `AnimeStudio.GUI.exe`.
