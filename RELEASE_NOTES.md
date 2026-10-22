# AnimeStudio v1.8.0

配套：[EFF v1.9.0](https://github.com/ssice-a/EFF/releases/tag/v1.9.0)、[EFF Blender v0.47.0](https://github.com/ssice-a/EFF-blender/releases/tag/v0.47.0)。

- 增加终末地 ParticleSystem 特效来源读取：解包原生特效档案及其引用资源，导出可由 Blender 特效编辑器直接导入的 `.efffx` 源包。
- 保留特效层级、原始组件数据、字段类型、引用 Mesh/材质/贴图和序列帧资源；未适配字段保留在来源数据中供编辑器识别。
- 修复完整资源索引筛选遗漏、逻辑 Bundle 重定位和增量刷新边界；更新来源目录时使用备份、逐文件提交和失败回滚。
- 增加 Naraka Prefab 模型预览、材质状态和网格/骨骼检查工具。
- 保留 .NET 9 与 .NET 10 两种独立 GUI/CLI 发布包。

保留原工作区并安装与本机 .NET Windows Desktop Runtime 匹配的 ZIP。AnimeStudio 不保存 Blender 工程，也不修改已发布 Mod。特效源数据不能代表游戏最终 Shader 和碰撞表现；编辑后仍需由 Blender 离线导出并在游戏中验证。

## English

Adds Endfield ParticleSystem effect extraction, preserving the native hierarchy, component fields, Mesh/material/texture references and flipbook resources in `.efffx` source packages that Blender can import. Improves complete-index correctness, logical Bundle relocation, incremental refresh and transactional source updates. Adds Naraka Prefab model/material preview and retains separate .NET 9 and .NET 10 GUI/CLI packages.
