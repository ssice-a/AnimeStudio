# AnimeStudio v1.9.2

- 构建脚本支持独立 `OutputRoot`，本地发布和部署不再共用或清理同一个 dist 目录。
- 输出清理仍限制在指定目录内部，避免误删其他构建结果。
- 保持 .NET 9 与 .NET 10 Windows GUI/CLI 包，发布资产在本地构建并核验。

## AnimeStudio v1.9.1

- 来源包 PNG 导出不再翻转 V，保持与原始贴图一致的行方向。
- 索引准备窗口说明手动下载并导入 `.effindex.gz` 的步骤。
- 保持 .NET 9 与 .NET 10 Windows GUI/CLI 包。

## AnimeStudio v1.9.0

- 新增旧 Mod 迁移所需的来源资源导出，供 Mod Manager 与共享工具离线转换使用。
- 完整索引和原生作者工具使用公共资源位置；支持公共索引目录下载与校验。
- 保留来源包增量更新、共享索引重用和事务回滚。
- 同时提供 .NET 9 与 .NET 10 Windows GUI/CLI 包。
