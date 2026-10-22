# Endfield 独立特效来源（解析版）

此入口从当前游戏 VFS 和工作区完整索引导出独立 `.efffx` 来源目录。纯粒子 PFB 不需要 Mesh，也不走人物模型导出器。第二阶段已增加配套 Blender 编辑与既有对象离线重建；游戏播放、挂载、增加完整发射器和热重载尚未实现。

## 使用

GUI：打开 Endfield 游戏目录和当前工作区，在 Logical Asset Paths 勾选特效 `.prefab`，选择 **Export → Format specific → EFF → Checked Prefabs as EFF effect sources...**，选择输出目录。批量导出只读取一次相关索引，失败项单独报告。

命令行（与 GUI 使用同一实现）：

```powershell
AnimeStudio.GUI.exe --export-eff-effects '<游戏或VFS目录>' '<工作区>' 'assets/beyond/.../effect.prefab' '<输出目录>'
```

源文件剥离 Unity 版本时可追加 `--unity-version <实际版本>`。程序校验当前合并 VFS 与工作区索引，不使用硬编码的本机目录，也不猜测同名根节点。缺少 Bundle、层级损坏或组件登记缺失时拒绝发布新结果。

## 目录格式

| 路径 | 内容 |
| --- | --- |
| `effect.json` | `EFF_EFFECT_SOURCE` v1；根、节点、组件、原生范围、资源逻辑路径、引用、模块摘要、诊断 |
| `objects/*.json` | 严格解析成功的完整结构化字段；包括关闭的模块、曲线、渐变、随机范围和已解析的多态数据 |
| `schemas/*.json` | `EFF_TYPE_TREE` v1；实际 TypeTree 和 managed reference 类型表，按内容去重 |
| `source/manifest.json` | 原生基线、依赖闭包、所有原生对象的范围、流式数据范围和 SHA256 |
| `source/serialized/*`、`source/streams/*` | 完整 SerializedFile 和资源流快照，包括未知组件与未开放编辑的数据 |
| `source/generated-files.json` | 原有原子发布机制的生成文件清单 |

重导出采用 staging 和备份，失败保留上一完整目录；未登记为生成物的用户文件保留。`.efffx` 尚不加入人物 `.eff` 自动刷新列表。

节点以 CAB/PathID 身份区分，允许重名；CAB/PathID 只作为该版本的来源证据，不是跨游戏版本的稳定身份。层级的 `identity` 指 GameObject，`transform`、`parent`、`children` 指 Transform；组件通过 `owner` 回指 GameObject。组件与资源统一放在 `objects`，不把某粒子的材质/Mesh误当成层级子节点。

对象状态：

- `parsed`：完整字节读取、完整对象消费、未修改重编码与原始字节相等。`data` 指向结构化字段；`references` 列出所有按 TypeTree 识别的 PPtr，包含 null 和未解析状态。
- `raw-backed`：大资源（Mesh、Texture、Shader、AnimationClip、AudioClip、Avatar）、缺少类型树或超过 16 MiB 的对象，只使用完整原生快照；不声称已经提取其全部内部引用。
- `opaque`：严格解码失败；保留完整快照、类型树和失败原因，没有伪造空的可编辑内容。

`referenceCoverage` 说明每个对象的引用扫描范围。未解析引用进入 `warnings`；引用不能全部解析并不伪装为已解决。完整依赖闭包的对象表仍保存在基线中，结构化引用图不是原生底稿的替代物。

`particleModules` 包含原生已存在的所有 `*Module` 字段及启用状态、原始范围。普通粒子的模块是固定槽，启用 Noise 不意味着向文件追加一个新的 Unity 组件；bursts、曲线键和 sub emitters 等列表的条目可以增长。未来新增发射器需要同时建立 GameObject/Transform、粒子组件、Renderer、资源引用及生命周期契约，当前没有开放此发布操作。

## 严格对象编解码

核心 API `TypeTreeDocument` 与宽松展示用 `TypeTreeHelper` 分离。它保留标量原始表示、非零 padding、浮点 NaN/负零和完整 64 位 ID；数组长度由条目数量编码，字符串按 UTF8 和原生对齐编码，二进制数组以 base64 标记输出。曲线/渐变的枚举值和原生字段名保留，不猜测 UI 标签或取值单位。

managed registry 当前只接受 version 2，通过同一文件 `m_RefTypes` 的 class/namespace/assembly 精确匹配动态数据；未知类型或不支持的 registry 版本明确拒绝严格解码。没有“未知 data 默认空对象”的回退。更改类型描述但不更改匹配数据时，编码器拒绝写出。

`Root["NoiseModule"]["enabled"].Scalar = true` 可修改一个已解析字段；数组条目可用 `Clone()` 独立复制后加入 `Array.Items` 再 `Encode()`。`ApplyJson()` 将完整 JSON 按实际类型严格导入，验证成功后替换当前对象；拒绝缺少/额外字段、类型强制转换和数值溢出。有限浮点值按原生宽度比较，原有 NaN/Infinity 及非零 padding 可以原样保留；不允许新增非有限数值。额外的业务计数字段（例如 `m_BurstCount`）、曲线键排序和引用有效性由第二阶段编辑层与构建器同步校验。

## 第二阶段：既有特效编辑与离线重建

Blender 独立“特效”面板可以导入 `.efffx`，查看层级、组件与资源，启用模块、修改字段、增删曲线键/发射条目，保存作者状态到 `.blend` 并导出 `EFF_EFFECT_EDIT` v1 JSON。它记录 manifest/effect/每份修改对象来源 JSON 的 SHA256，避免用旧工程悄悄覆盖已更新的来源。

```powershell
AnimeStudio.GUI.exe --build-eff-effects '<来源.efffx目录>' '<编辑.effedit.json>' '<独立输出.efffxbuild目录>'
```

当前只允许既有 Transform、Material、MonoBehaviour、ParticleSystem、ParticleSystemRenderer 的参数/列表修改。原生归属和父子关系、脚本引用、managed registry 的成员/type/rid 只读；不新增组件/类型/外部依赖。修改的 PPtr 要求解析到实际现有对象并匹配类型，未修改的未知引用原样保留。支持 little-endian SerializedFile v22。

构建器核对来源快照和每份编辑，按实际原生类型导入 JSON；重建变长对象及原生对象表范围，保留类型/外部/managed 等元数据和未修改对象字节，再生成受影响来源的完整 UnityFS Bundle。每个变更 SerializedFile 均重新解析并逐对象比较字节，每个 Bundle 均重新读取并比较全部成员；输出按原有原子发布机制保留上一完整结果。没有变化时输出 0 个改写对象/Bundle。

`.efffxbuild/build.json`（`EFF_EFFECT_BUILD` v1）给出改写对象数、原生文件和 Bundle 的来源与摘要，标记 `offlineVerified=true, gameVerified=false, installed=false`。`native/`、`bundles/` 是独立验证产物，尚未接入当前 Mod 协议或游戏加载路由；不能直接放入 mods 期待 F10 生效。已有源资源的引用、预加载、动画和生命周期仍需游戏验收。完整新增发射器/挂载/播放/热重载属于下一阶段。

2026-10-19 DLL 开发候选补充：构建另外输出 `runtime.bin` 与 `runtime/bundles/`，即使没有参数变化也可生成首次调试输入。离线隔离选中 Bundle/CAB/stream 的命名空间，保留游戏原有终端依赖，根节点在输出中设为 inactive，并重新验证对象范围和 Bundle 成员。仅供主仓库显式 `effects-debug` DLL 的独立准备/播放通道；默认 DLL、Mod 协议和 F10 不读取。调试候选仍标记未安装/未通过游戏验收；不能当成正式技能特效替换。具体生命周期和命令见主仓库 `docs/effects-runtime-debug.md`。相关确定性、根状态、依赖清单和原生输出测试已扩展到 191 项断言。

## 验证

```powershell
dotnet run --project AnimeStudio.EffectsTests -p:TargetFrameworks=net9.0-windows
dotnet run --project AnimeStudio.EffectsTests -p:TargetFrameworks=net9.0-windows -- --research '<本地研究夹>'
dotnet run --project AnimeStudio.EffectsTests -p:TargetFrameworks=net9.0-windows -- --export-samples '<游戏目录>' '<工作区>' '<逻辑PFB列表.txt>' '<独立测试输出夹>'
dotnet run --project AnimeStudio.EffectsTests -p:TargetFrameworks=net9.0-windows -- --build-sources '<样本来源输出夹>' '<独立构建测试输出夹>'
```

研究夹包含外部实际游戏样本，不提交游戏数据到仓库。默认测试不依赖游戏安装，覆盖 padding/浮点/数组/字符串/64 位引用、managed data、安全拒绝、纯粒子和重名层级、坏引用与原子发布失败。真实样本模式覆盖 28 个 ParticleSystem、28 个 ParticleSystemRenderer、60 个 MonoBehaviour，包括曾被宽松解析器丢失的 `PlaySound.soundName`；修改模块、增加 burst/曲线键和 sub-emitter 引用后检查无关字段字节不变。

12 个实际特效 PFB 的来源导出与快照 SHA256 已验证。第二阶段 116 个对象的严格 JSON 导入/字节往返测试通过 439 项断言；10 个来源的改写、两次确定性构建、无修改输出、无效编辑拒绝通过 104 项断言，包括 PlaySound 变长字符串。Blender 导入、编辑、列表增长、导出、保存/重开和三轮注册卸载通过。离线来源导出和原生重建耗时不能作为特效运行时调参延迟；毫秒级热重载尚未实现或测试。
