# AnimeStudio v1.9.4

This release preserves VAT lookup UVs, vertex colors, tangent handedness, native submesh boundaries, and texture metadata in effect preview exports while retaining the v1 preview JSON compatibility aliases. Both .NET 9 and .NET 10 Windows packages remain supported.

## AnimeStudio v1.9.3

- 修复 EFFSOURCE PNG 行方向回归，恢复与 v1.9.0 相同的 V 翻转，使贴图能正确对应 Blender 模型 UV。
- 游戏 DDS 导出仍按 Blender 当前像素上下翻转一次，以匹配游戏采样方向。
- 保持 .NET 9 与 .NET 10 Windows GUI/CLI 包。

## AnimeStudio v1.9.2

- 构建脚本支持独立 `OutputRoot`，本地发布和部署不再共用或清理同一个 dist 目录。
- 输出清理仍限制在指定目录内部，避免误删其他构建结果。
- 保持 .NET 9 与 .NET 10 Windows GUI/CLI 包，发布资产在本地构建并核验。

## AnimeStudio v1.9.1

- 此版本错误地移除了来源包 PNG 的 V 翻转，导致与旧版及 Blender UV 朝向不一致；v1.9.3 已恢复。
- 索引准备窗口说明手动下载并导入 `.effindex.gz` 的步骤。
- 保持 .NET 9 与 .NET 10 Windows GUI/CLI 包。

## AnimeStudio v1.9.0

- 新增旧 Mod 迁移所需的来源资源导出，供 Mod Manager 与共享工具离线转换使用。
- 完整索引和原生作者工具使用公共资源位置；支持公共索引目录下载与校验。
- 保留来源包增量更新、共享索引重用和事务回滚。
- 同时提供 .NET 9 与 .NET 10 Windows GUI/CLI 包。
