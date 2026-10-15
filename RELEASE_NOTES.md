# AnimeStudio v1.6.0

配套：[EFF v1.7.0](https://github.com/ssice-a/EFF/releases/tag/v1.7.0)、[EFF Blender v0.44.0](https://github.com/ssice-a/EFF-blender/releases/tag/v0.44.0)。

- 修复本次游戏更新后的 **BLC v5** 目录解析，保留 v3/v4。继续合并 StreamingAssets 与 Persistent，并按内容增量更新索引。
- 重新打开当前游戏及原工作区时，自动更新工作区已有和已登记的 EFF 解包源包；菜单 **Export → Format specific → EFF → Update previously unpacked EFF resources...** 可指定其他旧输出目录。
- EFF 源包先完整暂存、验证后提交，保留 `.eff-backups`；更新失败保留原包。按逻辑资源身份保留原文件名和段名，未知文件继续保留。
- 与 Blender 共用来源锁，更新模板、清单和当前索引绑定。作者可从原 Blender 工程直接重新导出 Mod，不需要重新导入人物或使用 Mod 修复工具。
- 新增完整公共索引打包/导入，核验 Bundle 实际内容后建立本地位置绑定；同内容的不同盘符与文件时间可共用。公共索引不包含游戏 Mesh/贴图，提取仍读取本机游戏资源。

关闭旧解包器后，选择与 Windows x64 .NET Desktop Runtime 对应的 net9.0-windows 或 net10.0-windows 附件安装，保留工作区 `index/`。预下载尚未正式安装时不作为当前资源。`.blend` 和已发布 Mod 不会被解包器自动保存或更新。

实际本次更新目录、完整公共索引打包导入、旧庄方宜 105 条资源文件/段名稳定、旧工程隔离导出，以及源包回退/并发和索引增量回归通过。资产删除、来源歧义或不兼容结构会报告具体失败，不猜测替代资产。

## English

Fixes BLC v5 parsing for the current update and refreshes previously extracted EFF source packages when reopening their game/workspace. Packages are staged, validated and backed up before replacement; logical resource identities preserve existing filenames and section IDs. A dedicated menu refreshes other output folders.

Paired Blender projects accept updated templates and index bindings without reimporting characters. Public full indexes include content proofs and can be rebound across installation paths/timestamps without parsing every Bundle. Extraction still reads local game resources. Existing blend files and distributed Mods are not automatically rewritten. Use the archive matching your Windows x64 .NET Desktop Runtime.
