# Resource format regressions

Run the synthetic material/streamed-mesh regressions without a game installation:

```powershell
dotnet run --project AnimeStudio.FormatTests -c Release -p:TargetFrameworks=net9.0-windows
```

To also compare the Naraka readers against each object's embedded TypeTree,
append one or more local Bundle paths after `--`. The comparison checks the full
object boundary, material texture references/UV transforms/float and color
parameters, mesh vertex counts, finite positions/normals/tangents, triangle bounds,
and external vertex-data references. Files are read from the user's installation
and are not modified. The synthetic compressed-mesh regression also checks split
XYZ positions, octahedral directions, tangent handedness and lightmap UV against
known geometry, including zero-bit constant payloads.

Validated locally on 2026-10-15 against the installed Naraka 2019.4.41f2 resources:

| Bundle | Materials | Meshes | Failed |
| --- | ---: | ---: | ---: |
| `0/0/005a2e73459c0abc` | 0 | 16 | 0 |
| `f/6/f655df17fdf8e7aa` | 662 | 304 | 0 |
| `3/1/312ba9c886d5c842` | 359 | 1472 | 0 |

The FX Bundle includes 25 meshes with nonempty split XYZ payloads. A sampled
curved effect mesh's octahedral normals agree with its triangle normals (mean
dot product 0.995). This verifies parser and geometry data, not the game's final
Shader, particle, lighting or UI rendering behavior.
