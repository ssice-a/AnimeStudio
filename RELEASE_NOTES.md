# AnimeStudio v1.4.0

配套：[EFF v1.4.0](https://github.com/ssice-a/EFF/releases/tag/v1.4.0)、[EFF Blender v0.41.0](https://github.com/ssice-a/EFF-blender/releases/tag/v0.41.0)。

- 导出 EFF 精确来源包：Mesh、材质、贴图、骨架、物理证据、字段模板和 manifest 保持 CAB/PathID 来源身份。
- 独立导出 SkeletalMorph 骨骼情绪姿态、口型和动态片段到 `.eanim`，供 Blender 选中骨架导入；不需要 PostModel。
- 入口：勾选角色 SkeletalMorph Avatar 和表情资源，选择 **Export → Format specific → EFF → Checked facial animations as EFF library...**。
- 资源文件使用短名称，真实冲突添加后缀；旧哈希文件名仍通过各自 manifest 解析。解包器文件不进入游戏 DLL 安装包。

选择与 .NET Desktop Runtime 匹配的 net9.0-windows 或 net10.0-windows 包，解压运行 AnimeStudio.GUI.exe。最终 Mod 由 Blender 编译，玩家不需要作者索引或原包快照。发布不含游戏资源。

本地 net9 GUI/CLI Release、来源契约及实际表情导出/导入通过；Liino 样本 182 个源资源生成 119 个 Action。net10 由发布流水线构建。Shader 表情参数、随机调度及完整特殊压缩 AnimationClip 通用导出不属于此入口。

## English

EFF source packages preserve exact CAB/PathID identities, field templates, native resource data and physics evidence. Export selected SkeletalMorph Avatar and facial resources through **Export → Format specific → EFF → Checked facial animations as EFF library...** to produce a standalone `.eanim` library for the paired Blender add-on. A PostModel is not required.

The library covers bone emotion poses, visemes and dynamic segments; shader expressions, random scheduling and general special-compressed AnimationClip extraction are outside this entry point. The Liino sample produced 119 Actions from 182 source resources. Source contracts, .NET 9 builds and actual library export/import passed local checks; .NET 10 builds run in the release workflow.

Use the archive matching your Windows x64 .NET Desktop Runtime. The unpacker, add-on and game runtime remain separate installations. No game assets are distributed.
