# AnimeStudio v1.3.1

配套：[EFF v1.3.1](https://github.com/ssice-a/EFF/releases/tag/v1.3.1)、[EFF Blender v0.40.1](https://github.com/ssice-a/EFF-blender/releases/tag/v0.40.1)。

- 导出 EFF 源包的精确来源、字段模板、Mesh、材质、贴图和骨骼，供 Blender 独立编译使用。
- 保留短资源名、冲突后缀与 manifest 中的完整 CAB/PathID 身份。
- 保留 GUI/CLI 资源浏览、Prefab 结构预览和更新检查。
- 解包器、Blender 作者工具与游戏 DLL 的安装文件各自独立。

选择与 .NET Desktop Runtime 匹配的 net9.0-windows 或 net10.0-windows 包，解压运行 AnimeStudio.GUI.exe。源包用于制作；最终 Mod 由 Blender 导出并携带 compiled.bin，玩家无需索引或原包快照。发布不含游戏资源。
