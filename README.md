# Anime Studio

本 EFF fork 发布 **v1.8.1**，配套 [EFF v1.9.0](https://github.com/ssice-a/EFF/releases/tag/v1.9.0) 与 [EFF Blender v0.48.0](https://github.com/ssice-a/EFF-blender/releases/tag/v0.48.0)。选择游戏 Prefab 或特效资源导出 EFF 源包，再在 Blender 中制作并导出 Mod。

终末地索引可从 GitHub 下载预建包、本地构建或从文件导入。有效工作区会登记到 `%LOCALAPPDATA%/EFF/Resources/registry.json`，Blender 与 EFF Repair 可复用其位置，无需在项目间复制全量索引。下载的索引不包含模型或贴图，游戏资源仍按需从本机提取。全局指纹和索引路径变化不再单独触发旧源包重新解包；逐 Bundle 记录不完整的旧源包会被保留。

### 终末地 EFF 导出

1. 选择终末地，打开游戏目录或上一级 `games` 目录，再选择原工作区。工具自动读取基础与热更新 VFS，并优先提示同一游戏已登记且可用的工作区。没有可用索引时，可选“下载预建索引”“本地构建”或“导入索引包”；下载需要已发布的匹配索引，只省去全游戏索引构建，角色资源仍从本机按需提取。构建时只重新解析新增或变化的 Bundle，复用未变记录、移除删除项；资源未变直接复用索引。准备过程可从 File > Abort 取消。多份终末地安装需明确选择一份。
2. 导出 EFF 源包，将其中的 `mod.ini` 导入 Blender。包内 Mesh、Material、Texture、Skeleton 与源字段模板保持精确来源身份；详细结构见 [EFF_FORMAT.md](EFF_FORMAT.md)。
3. 保留 `source/manifest.json`、`source/serialized/` 和来源索引供作者准备。`source/streams/` 是原始 Mesh/Texture 载荷快照，普通 Blender 导出不读取；结构文本是供人阅读的报告。这些文件不应全量放入游戏 Mod。
4. Blender 使用自身配套工具据源 Mesh 确定全部 Prefab/Renderer 消费者，导出作者资源、`source-inputs.bin` 与独立 `compiled.bin`；游戏 DLL 读取编译结果，F10 提交整个 Mod 集合。

独立表情库入口：勾选角色的 SkeletalMorph Avatar 与表情资源，使用 **Export → Format specific → EFF → Checked facial animations as EFF library...** 导出 `.eanim`，再由 Blender 的“文件 → 导入 → EFF 动画（选中骨架）”读取。不要求从 PostModel 导出；目前支持骨骼情绪姿态、口型及动态片段，Shader 表情与运行时随机调度不包含在动画库中。

贴图等载荷现在优先使用可读短文件名，只在真实名称冲突时添加短后缀。完整 CAB/PathID 身份保留在 manifest 内，旧包带哈希的名称无需手动改名。包兼容性依据实际数据结构与字段验证，不按应用版本号拒绝。

本次发布支持增量来源索引。请保留工作区 `index/`：原 `endfield_assets.eidx` 旁增加一个 `.eidx.bundles` 文件，用于记录每 Bundle 的内容身份与索引范围，不生成逐包小文件。精确匹配当前资源的旧完整两层索引可直接迁移，旧游戏身份且缺少逐包记录时才需要重新建立基线；升级软件不因版本标签直接使索引失效。变化更新仍需读写紧凑索引，进度显示 parsed/reused/removed。新版支持 BLC v5。打开当前游戏及原工作区后，在后台检查工作区已有和已登记的解包源包，仅在可靠逐 Bundle 内容记录或依赖闭包证明相关输入改变时自动刷新；没有完整记录的老包保持原样，原因写入工作区 `source-update-skipped.log`。全局指纹或索引路径变化不单独触发源包刷新。其他目录或需要明确重新提取的老包，可从 EFF 菜单选择 Update previously unpacked EFF resources。旧源包完整备份，保留逻辑资源的文件名和段名。原 Blender 工程可接受更新后的来源并重新导出；解包器不会自动保存 blend 或修改游戏 Mod。

有效工作区自动登记到 `%LOCALAPPDATA%/EFF/Resources/registry.json`，供新版 Blender 与 EFF Repair 复用；旧工作区留在原处，不复制全量索引。`EFF_RESOURCE_ROOT` 可更换公共根，`EFF_INDEX_CATALOG` 可配置 GitHub Release API/直接 catalog HTTPS 地址。下载和 Repair 共用压缩包缓存。工作区完整索引仍需 `.eidx`、`.eidx.bundles` 与 `endfield-index-manifest.json`；索引不能代替 `source/manifest.json` 及清单引用的作者源快照。本次未上传 GitHub 索引附件，无匹配包时仍可本地构建或导入。

本 EFF fork 的 GUI 显示后会异步检查 `ssice-a/AnimeStudio` 的最新正式 Release。
发现新版本可打开下载页、稍后再说或忽略此版本；About 页也可手动重新检查。
检查失败不会影响浏览、解包或导出，程序不会自行覆盖安装。

## Asset extraction tool for unity games !

### Naraka / 永劫无间 Blender packages

打开游戏根目录或 `NarakaBladepoint_Data/StreamingAssets/AppRes.info`，在 Asset List
选择时装 UI Prefab，使用 **Export → Format specific → Naraka / 永劫无间 →
Selected Prefabs as Blender packages...**；Logical Asset Paths 树中勾选多个
Prefab 时使用 **Checked Prefabs as Blender packages...**。

UI 时装文件在 `assets/res/prefab/actor_visual_part/<角色目录>/`，选择具体的
`*_ui.prefab`；角色目录本身不代表一套时装。模型文件通常在
`assets/res/character/female/<角色目录>/model/`（男性为 `male`），展开 `.fbx`
后可直接点击其中的 `[Mesh]` 子项，在右侧查看几何。选择 FBX 默认预览第一个 Mesh；
永劫无间 Prefab 现在可直接点击预览其组合网格与基础色贴图；展开 Prefab 可逐个选择
网格，底部“显示全部网格”包含低精度 LOD 与未启用项。默认只组合启用的高精度网格，
避免多个 LOD 叠加。完整 Shader 效果与编辑请导出后在 Blender 查看。

每个 `.naraka` 包保留所选 Prefab 全部网格实例、层级、蒙皮、材质槽、贴图及原始材质参数，
复用 EFF 原生网格格式和暂存/备份发布流程。配套独立 Naraka Blender 插件读取
`naraka.json`，不要求安装 EFF 插件。材质在 Blender 中重建，已验证 Periodic2
服装的周期发光节点；未知 Shader 和近似部分在导入报告中列出。游戏运行时另行加载的对象、
完整主体骨架/动画及灯光不由衣服 Prefab 自动提供。Renderer 缺少骨骼指针时，导出器会按 Mesh 的精确 FBX 来源与完整 Transform 路径哈希恢复可核对的骨名及父子关系，并保留原有骨骼槽顺序；无法唯一匹配时保留哈希并报告。旧哈希包需重新导出，Blender 0.2.4 已能读取补齐的骨骼引用。

导出时会继续读取所选时装 Prefab 中序列化的 `PrefabLoader` 路径，递归打包静态引用的 FX Prefab、组件字段、真实 Renderer Mesh、独立 Mesh、材质与贴图。Blender 插件 0.4.0 将这些资源放入独立的 **Effects** 集合；Billboard 类粒子使用带来源标记的静态 Unity 四边形检查代理，粒子不会播放，代理也不作为源模型。运行时脚本创建或切换的资源不属于静态引用范围；旧包需要在 AnimeStudio 重新导出才能包含特效。

D 图 A 按已识别 Shader 与材质参数使用；有 A 通道或裁切阈值不等于半透明。
未知 Shader 保留 A 数据并报告待适配，不自动接到透明度。插件 0.2.1 修正相关判断，
已有包无需重导，更新后重新导入以建立新材质。

组合预览使用与 GL 正面一致的正交深度方向，修复外表面透出模型内侧的问题，
透明层按相机远近排序；已通过真实 GL 的外壳、剔除、深度、透明排序和多角度资源检查。

部分头发的原生 `m_Mesh` 为空，实际数据通过 RendererAssistant 的 `avatarMeshAsset`
提供。现在预览与导出均按精确引用读取已识别的 v2 runtime mesh，保留全部网格及原始
自定义数据；未知布局明确报错。普通/UI 头发、衣服和武器的导出前后预览回归通过。

```text
AnimeStudio.GUI.exe --export-naraka-prefab <game-root-or-AppRes.info> <logical-prefab> <output-directory>
```

![image](https://github.com/user-attachments/assets/fc1decdc-a589-43a2-b965-2d8151d0975f)

Anime Studio opens Unity bundles and lets you browse, preview and export what's inside : textures, meshes, animations, audio, shaders, text assets and more. It handles the encrypted bundles used by Genshin Impact, Star Rail and Zenless Zone Zero, all of their others upcoming titles. Also works on a long list of other encrypted games and works fine on regular Unity games too.

It comes as a GUI and a CLI, both in the same download. The GUI is what you want if you're just looking for assets, the CLI is there for scripting and batch exports.

---

# How do I download this ?

- **[EFF AnimeStudio Releases](https://github.com/ssice-a/AnimeStudio/releases)**：下载与已安装 .NET Desktop Runtime 对应的 `net9.0-windows` 或 `net10.0-windows` 压缩包。

Both builds are Windows x64 only and need the matching [.NET Desktop Runtime](https://dotnet.microsoft.com/download/dotnet) installed. Unzip anywhere and run `AnimeStudio.GUI.exe` or `AnimeStudio.CLI.exe`.

---

# How do I use this ?

Pick your game in the game selector first, then load a file or a folder. This matters because each game has its own decryption, loading with the wrong one selected will either fail or give you garbage. There are over 50 entries in that list, including separate ones for closed beta versions, so take the one that matches the build your files come from.

From there, load a file or a folder, browse the list, and export what you want. Assets can be filtered by type, by name or by container path, and previewed before exporting for most types.

For big dumps there's the Asset Browser, it builds a map of everything in a game install so you can search it without loading the whole thing every time. Maps can be saved as message pack or json, relocated to a different install folder, and two of them can be loaded side by side to see what changed between versions.

The CLI takes the same options as flags, with an input and an output path :

```
AnimeStudio.CLI.exe <input> <output> --game GI --types Texture2D,Sprite --export_type convert
```

Useful flags are `--game`, `--types`, `--names` and `--containers` for filtering, `--group_assets` for the output folder layout, `--map_op` and `--map_type` for building asset maps, and `--silent` to keep it quiet. Run it without arguments for the full list.

The [official wiki](https://github.com/Escartem/AnimeStudio/wiki) goes into detail on all of this. If something isn't covered there, look at the [original tutorial by Modder4869](https://gist.github.com/Modder4869/0f5371f8879607eb95b8e63badca227e) or the [original readme](https://github.com/RazTools/Studio/blob/main/README.md). Otherwise [join the discord](https://discord.gg/fzRdtVh) and ask there !

---

# Building

## Endfield facial animation libraries

Open the Endfield VFS in Logical Asset Paths. Under
`assets/beyond/dynamicassets/gamedata/skeletalmorph/`, check animation assets in
`skeletalmorphanim/emotion`, `pose`, or `morphanimation`, and check/select one
facial avatar asset in `skeletalmorphcfg` (for example
`girl/data_facemorph_avatar_liino.asset`). Use **Export → Format specific → EFF →
Checked facial animations as EFF library...** to save an `.eanim` library.
No PostModel is required. Only the selected resources and the avatar's original
skeleton are read; incompatible controls are recorded as skipped.

In EFF Blender, select an imported source armature and use **File → Import → EFF
动画（选中骨架）**. The library contains bone poses/animations for authoring,
not a runtime animation replacement Mod. Shader expression parameters and
random blink scheduling are excluded. Regular AnimationClip FBX export is unchanged.

Headless GUI entry point (selection JSON is an array of logical animation paths):

```text
AnimeStudio.GUI.exe --export-eff-animations <vfs-root> <workspace> <logical-avatar.asset> <selection.json> <output.eanim> [--unity-version <version>]
```

You need Visual Studio 2022 with the C++ desktop workload and the .NET 9 and 10 SDKs.

`build.ps1` builds the GUI and the CLI for both frameworks and packages them into `dist`. That's the same script the CI runs, so if it works there it works locally. For day to day work `dotnet build AnimeStudio.GUI` is enough, the packaging step is only needed for a release.

The native libraries are not built by that script, they sit prebuilt in `AnimeStudio.Libraries` and only need rebuilding when you touch their sources. Each one is its own project in the solution and outputs straight into that folder :

| Project | Output | What it does |
| --- | --- | --- |
| `AnimeStudio.Ooz` | `AnimeStudio.Ooz.dll` | Kraken / Mermaid / Leviathan decompression |
| `AnimeStudio.FBXNative` | `AnimeStudio.FBXNative.dll` | FBX export, needs the Autodesk FBX SDK at exactly version 2020.3.7 installed |
| `AnimeStudio.HLSLDecompiler` | `AnimeStudio.HLSLDecompiler.dll` | Shader decompilation |
| `AnimeStudio.ACL.DB` / `.ZZZ` / `.SR` | `AnimeStudio.ACL.*.dll` | Animation decompression, one per ACL version |

They are all x64 only and excluded from the solution build, build them by hand when needed.

Contributions are welcome, whether it's a new game, a fix for an existing one or anything else. Open a PR and it'll get looked at.

---

# Contributors ✨

Thanks goes to these wonderful people :

<!-- ALL-CONTRIBUTORS-LIST:START - Do not remove or modify this section -->
<!-- prettier-ignore-start -->
<!-- markdownlint-disable -->
<table>
  <tbody>
    <tr>
      <td align="center" valign="top" width="14.28%"><a href="https://github.com/hrothgar234567"><img src="https://avatars.githubusercontent.com/u/215089974?v=4?s=100" width="100px;" alt="hrothgar234567"/><br /><sub><b>hrothgar234567</b></sub></a><br /><a href="https://github.com/Escartem/AnimeStudio/commits?author=hrothgar234567" title="Code">💻</a> <a href="https://github.com/Escartem/AnimeStudio/pulls?q=is%3Apr+reviewed-by%3Ahrothgar234567" title="Reviewed Pull Requests">👀</a> <a href="#ideas-hrothgar234567" title="Ideas, Planning, & Feedback">🤔</a> <a href="#question-hrothgar234567" title="Answering Questions">💬</a> <a href="#platform-hrothgar234567" title="Packaging/porting to new platform">📦</a> <a href="#security-hrothgar234567" title="Security">🛡️</a></td>
      <td align="center" valign="top" width="14.28%"><a href="https://soundcloud.com/eleiyas/"><img src="https://avatars.githubusercontent.com/u/16349939?v=4?s=100" width="100px;" alt="Elliot Bastiani"/><br /><sub><b>Elliot Bastiani</b></sub></a><br /><a href="https://github.com/Escartem/AnimeStudio/commits?author=Eleiyas" title="Code">💻</a> <a href="#ideas-Eleiyas" title="Ideas, Planning, & Feedback">🤔</a></td>
      <td align="center" valign="top" width="14.28%"><a href="https://github.com/yarik0chka"><img src="https://avatars.githubusercontent.com/u/64433879?v=4?s=100" width="100px;" alt="yarik0chka"/><br /><sub><b>yarik0chka</b></sub></a><br /><a href="https://github.com/Escartem/AnimeStudio/commits?author=yarik0chka" title="Code">💻</a> <a href="https://github.com/Escartem/AnimeStudio/issues?q=author%3Ayarik0chka" title="Bug reports">🐛</a> <a href="#question-yarik0chka" title="Answering Questions">💬</a></td>
      <td align="center" valign="top" width="14.28%"><a href="https://www.youtube.com/c/Manashiku"><img src="https://avatars.githubusercontent.com/u/46613923?v=4?s=100" width="100px;" alt="manashiku"/><br /><sub><b>manashiku</b></sub></a><br /><a href="https://github.com/Escartem/AnimeStudio/commits?author=Manashiku" title="Code">💻</a> <a href="https://github.com/Escartem/AnimeStudio/issues?q=author%3AManashiku" title="Bug reports">🐛</a></td>
      <td align="center" valign="top" width="14.28%"><a href="https://github.com/Razmoth"><img src="https://avatars.githubusercontent.com/u/32140579?v=4?s=100" width="100px;" alt="Razmoth"/><br /><sub><b>Razmoth</b></sub></a><br /><a href="https://github.com/Escartem/AnimeStudio/commits?author=Razmoth" title="Code">💻</a> <a href="https://github.com/Escartem/AnimeStudio/issues?q=author%3ARazmoth" title="Bug reports">🐛</a> <a href="#ideas-Razmoth" title="Ideas, Planning, & Feedback">🤔</a> <a href="#research-Razmoth" title="Research">🔬</a></td>
      <td align="center" valign="top" width="14.28%"><a href="https://github.com/Dimbreath"><img src="https://avatars.githubusercontent.com/u/1474840?v=4?s=100" width="100px;" alt="Dimbreath"/><br /><sub><b>Dimbreath</b></sub></a><br /><a href="https://github.com/Escartem/AnimeStudio/issues?q=author%3ADimbreath" title="Bug reports">🐛</a></td>
      <td align="center" valign="top" width="14.28%"><a href="https://github.com/LukeFZ"><img src="https://avatars.githubusercontent.com/u/17146677?v=4?s=100" width="100px;" alt="Luke"/><br /><sub><b>Luke</b></sub></a><br /><a href="https://github.com/Escartem/AnimeStudio/issues?q=author%3ALukeFZ" title="Bug reports">🐛</a> <a href="#security-LukeFZ" title="Security">🛡️</a></td>
    </tr>
    <tr>
      <td align="center" valign="top" width="14.28%"><a href="https://github.com/aelurum"><img src="https://avatars.githubusercontent.com/u/6244109?v=4?s=100" width="100px;" alt="VaDiM"/><br /><sub><b>VaDiM</b></sub></a><br /><a href="https://github.com/Escartem/AnimeStudio/commits?author=aelurum" title="Code">💻</a></td>
      <td align="center" valign="top" width="14.28%"><a href="https://festivity.moe/"><img src="https://avatars.githubusercontent.com/u/77230051?v=4?s=100" width="100px;" alt="festivity"/><br /><sub><b>festivity</b></sub></a><br /><a href="https://github.com/Escartem/AnimeStudio/commits?author=festivities" title="Code">💻</a></td>
      <td align="center" valign="top" width="14.28%"><a href="https://github.com/MiemieMethod"><img src="https://avatars.githubusercontent.com/u/40489495?v=4?s=100" width="100px;" alt="方法放寒假"/><br /><sub><b>方法放寒假</b></sub></a><br /><a href="https://github.com/Escartem/AnimeStudio/commits?author=MiemieMethod" title="Code">💻</a> <a href="#platform-MiemieMethod" title="Packaging/porting to new platform">📦</a></td>
      <td align="center" valign="top" width="14.28%"><a href="https://github.com/jokelbaf"><img src="https://avatars.githubusercontent.com/u/60827680?v=4?s=100" width="100px;" alt="JokelBaf"/><br /><sub><b>JokelBaf</b></sub></a><br /><a href="https://github.com/Escartem/AnimeStudio/commits?author=jokelbaf" title="Code">💻</a></td>
      <td align="center" valign="top" width="14.28%"><a href="https://github.com/formagGinoo"><img src="https://avatars.githubusercontent.com/u/67542068?v=4?s=100" width="100px;" alt="formagGino"/><br /><sub><b>formagGino</b></sub></a><br /><a href="https://github.com/Escartem/AnimeStudio/commits?author=formagGinoo" title="Code">💻</a> <a href="#platform-formagGinoo" title="Packaging/porting to new platform">📦</a></td>
      <td align="center" valign="top" width="14.28%"><a href="https://github.com/hashblen"><img src="https://avatars.githubusercontent.com/u/62646051?v=4?s=100" width="100px;" alt="Hashblen"/><br /><sub><b>Hashblen</b></sub></a><br /><a href="https://github.com/Escartem/AnimeStudio/issues?q=author%3Ahashblen" title="Bug reports">🐛</a> <a href="https://github.com/Escartem/AnimeStudio/commits?author=hashblen" title="Code">💻</a></td>
      <td align="center" valign="top" width="14.28%"><a href="https://github.com/Sieluna"><img src="https://avatars.githubusercontent.com/u/88884784?v=4?s=100" width="100px;" alt="Sieluna"/><br /><sub><b>Sieluna</b></sub></a><br /><a href="https://github.com/Escartem/AnimeStudio/commits?author=Sieluna" title="Code">💻</a> <a href="#infra-Sieluna" title="Infrastructure (Hosting, Build-Tools, etc)">🚇</a></td>
    </tr>
    <tr>
      <td align="center" valign="top" width="14.28%"><a href="https://github.com/1004452714"><img src="https://avatars.githubusercontent.com/u/28773469?v=4?s=100" width="100px;" alt="DarkFlameMaster"/><br /><sub><b>DarkFlameMaster</b></sub></a><br /><a href="https://github.com/Escartem/AnimeStudio/issues?q=author%3A1004452714" title="Bug reports">🐛</a></td>
      <td align="center" valign="top" width="14.28%"><a href="https://github.com/SherkeyXD"><img src="https://avatars.githubusercontent.com/u/57581480?v=4?s=100" width="100px;" alt="SherkeyXD"/><br /><sub><b>SherkeyXD</b></sub></a><br /><a href="https://github.com/Escartem/AnimeStudio/commits?author=SherkeyXD" title="Code">💻</a> <a href="https://github.com/Escartem/AnimeStudio/issues?q=author%3ASherkeyXD" title="Bug reports">🐛</a></td>
      <td align="center" valign="top" width="14.28%"><a href="https://github.com/djpadbit"><img src="https://avatars.githubusercontent.com/u/9431263?v=4?s=100" width="100px;" alt="djpadbit"/><br /><sub><b>djpadbit</b></sub></a><br /><a href="https://github.com/Escartem/AnimeStudio/commits?author=djpadbit" title="Code">💻</a> <a href="#platform-djpadbit" title="Packaging/porting to new platform">📦</a></td>
      <td align="center" valign="top" width="14.28%"><a href="https://github.com/tserj"><img src="https://avatars.githubusercontent.com/u/17748861?v=4?s=100" width="100px;" alt="tserj"/><br /><sub><b>tserj</b></sub></a><br /><a href="https://github.com/Escartem/AnimeStudio/issues?q=author%3Atserj" title="Bug reports">🐛</a></td>
      <td align="center" valign="top" width="14.28%"><a href="http://momokko.moe"><img src="https://avatars.githubusercontent.com/u/78632509?v=4?s=100" width="100px;" alt="綾瀬桃桃"/><br /><sub><b>綾瀬桃桃</b></sub></a><br /><a href="https://github.com/Escartem/AnimeStudio/issues?q=author%3AMomoko-Ayase" title="Bug reports">🐛</a> <a href="https://github.com/Escartem/AnimeStudio/commits?author=Momoko-Ayase" title="Code">💻</a></td>
      <td align="center" valign="top" width="14.28%"><a href="https://github.com/bbdd2729"><img src="https://avatars.githubusercontent.com/u/179790579?v=4?s=100" width="100px;" alt="bbdd"/><br /><sub><b>bbdd</b></sub></a><br /><a href="https://github.com/Escartem/AnimeStudio/commits?author=bbdd2729" title="Code">💻</a></td>
      <td align="center" valign="top" width="14.28%"><a href="https://github.com/ihzgniqgnem"><img src="https://avatars.githubusercontent.com/u/161725897?v=4?s=100" width="100px;" alt="ihzgniqgnem"/><br /><sub><b>ihzgniqgnem</b></sub></a><br /><a href="https://github.com/Escartem/AnimeStudio/issues?q=author%3Aihzgniqgnem" title="Bug reports">🐛</a> <a href="https://github.com/Escartem/AnimeStudio/commits?author=ihzgniqgnem" title="Code">💻</a></td>
    </tr>
  </tbody>
</table>

<!-- markdownlint-restore -->
<!-- prettier-ignore-end -->

<!-- ALL-CONTRIBUTORS-LIST:END -->

Contributions of any kind welcome!

---

# Credits

Anime Studio is a fork of [Studio](https://github.com/RazTools/Studio) by Razmoth, itself a fork of [AssetStudio](https://github.com/Perfare/AssetStudio) by Perfare. After Razmoth's repo was discontinued things started breaking as games evolved, and several forks appeared fixing different bits without ever merging back into each other. This one aims at being the place where those fixes live together. Most of what's here started in those two projects.

The native side is built on other people's work :

- [ooz](https://github.com/powzix/ooz) by powzix, through [zao's fork](https://github.com/zao/ooz) - Kraken decompression
- [FBX SDK](https://aps.autodesk.com/developer/overview/fbx-sdk) by Autodesk, wrapper based on Perfare's and hozuki's - FBX export
- [3Dmigoto](https://github.com/bo3b/3Dmigoto) and [HLSLcc](https://github.com/Unity-Technologies/HLSLcc) by Unity - shader decompilation
- [ACL](https://github.com/nfrechette/acl) and [RTM](https://github.com/nfrechette/rtm) by Nicholas Frechette, wrappers by Razmoth - animation decompression
- [FMOD Engine](https://www.fmod.com/) by Firelight Technologies - audio preview and export

FMOD is used under Firelight's free license for non commercial use, and the FBX SDK under Autodesk's terms. Everything else is MIT or similar, see each project for details.

## Endfield 来源位置与内容

现有完整索引可以按逻辑 Bundle 路径、内容 MD5 和长度校验并重新绑定当前 CHK 位置。仅位置或偏移变化时无需重新解析 Bundle；内容变化继续由增量索引处理。EFF 导出入口与 EFF Repair 使用同一重绑实现，不把旧物理路径或本机索引时间当作资源身份。

## Endfield 独立特效来源

EFF 菜单的 `Checked Prefabs as EFF effect sources...` 可导出纯粒子或含脚本特效 PFB 为 `.efffx` 解析来源，保留完整原生底稿、层级、粒子模块及资源引用。此版本是解析层，尚不包含 Blender 编辑器或游戏播放/热重载。格式、CLI 和验证方法见 [独立特效来源说明](docs/endfield-effects-source.md)。
