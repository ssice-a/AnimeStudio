# AnimeStudio v1.3.0

配套：[EFF v1.3.0](https://github.com/ssice-a/EFF/releases/tag/v1.3.0)、[EFF Blender v0.40.0](https://github.com/ssice-a/EFF-blender/releases/tag/v0.40.0)。

- EFF 源包保留精确 Mesh、材质、贴图、骨骼、字段模板与来源身份，供 Blender 原生导出和离线 Prefab 消费者匹配使用。
- 载荷文件优先使用短资源名，只在名称冲突时加短后缀；大小写碰撞、Windows 保留名及重复身份均有校验。
- 完整 CAB/PathID 身份保留在 manifest，不依赖文件名中的长哈希；旧包名称无需手动修改。
- 文档说明 manifest、serialized/streams 快照、结构报告和最终游戏 Mod 的用途，避免分发整份原游戏包。
- 保留资源浏览、Prefab 结构预览、GUI/CLI 与更新检查。

下载与已安装 .NET Desktop Runtime 匹配的 net9.0-windows 或 net10.0-windows ZIP，解压后运行 AnimeStudio.GUI.exe。打开终末地 VFS、选择 Prefab 并导出 EFF 源包，再在 Blender 导入源包的 mod.ini。发布不包含游戏资源。
