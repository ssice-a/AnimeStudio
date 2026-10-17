# AnimeStudio v1.7.0

配套：[EFF v1.8.0](https://github.com/ssice-a/EFF/releases/tag/v1.8.0)、[EFF Blender v0.45.0](https://github.com/ssice-a/EFF-blender/releases/tag/v0.45.0)。

- 完整索引不再继承 GUI 的导出类型筛选，修复 GameObject 记录遗漏；旧筛选索引不会被当作完整当前索引复用。
- 按逻辑 Bundle 内容核对索引。只有 CHK 位置、偏移或时间变化时重新绑定位置，不重新解析相同 Bundle；内容变化继续使用逐 Bundle 增量更新。
- 修复旧解包源目录被监视句柄占用时的更新失败：完整备份后按文件提交，失败回滚，保留作者未知文件和稳定来源身份。
- 独立材质源缺少 Prefab 锚点时明确跳过并保留，记录日志，不因缺字段终止整批源包更新。

保留原工作区，关闭旧程序后安装对应 .NET 9 或 .NET 10 Windows Desktop Runtime 的 ZIP。作者仍可从旧 Blender 工程继续编辑与导出；本软件不会保存 `.blend` 或修改已发布 Mod。未知来源结构或缺少实际更新载荷仍会明确报错。

## English

Builds complete indexes independently of GUI export filters, safely rebinds unchanged logical Bundle content to current locations, and retains incremental parsing for changed content. Source-package refresh tolerates directory watchers with backup/rollback and explicitly skips standalone material sources without a Prefab anchor. Preserve the existing workspace and install the archive matching your .NET Windows Desktop Runtime.
