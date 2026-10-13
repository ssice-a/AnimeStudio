# AnimeStudio v1.5.0

配套：[EFF v1.5.0](https://github.com/ssice-a/EFF/releases/tag/v1.5.0)、[EFF Blender v0.42.0](https://github.com/ssice-a/EFF-blender/releases/tag/v0.42.0)。

- 打开游戏目录、上一级 games 目录或已安装 VFS，自动合并 StreamingAssets 与 Persistent；按块版本和内容选择当前来源，缺少新载荷时不回退为不同内容的旧资源。
- 游戏更新后按逐 Bundle 内容 MD5 和长度增量更新索引：只解析新增/变化的包，复用未变资产与 CAB 依赖，清理删除项。CHK 位置和范围变化但内容未变时继续复用。
- 精确匹配当前资源的完整旧索引可直接迁移，不重新解析全部游戏包。索引格式保持，旁边增加单个 .eidx.bundles 文件；升级软件但解析规则未变时继续复用。
- 加入失败重试、可校验的断点恢复与构建期间资源变化拒绝；进度显示 parsed/reused/removed。自动化入口：`AnimeStudio.GUI.exe --update-eff-index <game-or-VFS> <workspace>`。

重新打开游戏目录，继续选择原工作区；请保留 index/。预下载 ZIP/差分不作为已安装资源，等待启动器正式安装后更新索引。源包、Blender 工程和游戏 Mod 不会因索引更新自动重新导出。

增量与冷构建逐字节对照、删除/移动/失败/断点/损坏/解析语义失效回归通过。真实 8 包更新只解析 2 包、复用 6 包；既有完整 255842 包索引迁移的解析数为 0，原 eidx 摘要保持一致。更新仍有目录比较及紧凑索引 I/O，未来实际大版本更新耗时需观察。

选择与 Windows x64 .NET Desktop Runtime 匹配的 net9.0-windows 或 net10.0-windows 附件。解包器、Blender 与游戏运行 DLL 独立安装；附件不含游戏资源。

## English

Open the installed game, its games parent or VFS folder to merge shipped and Persistent resource layers. Active block versions and content determine ownership; missing updated payloads never silently fall back to different old bytes. Predownload ZIPs/deltas are not installed resources.

Index updates parse only new or changed Bundles, reuse unchanged asset/CAB records and remove deleted entries. Content-preserving CHK relocation reuses records. A complete legacy index at the exact current resource identity can be adopted without reparsing game Bundles. Keep the workspace index directory and its single .eidx.bundles sidecar.

Incremental/cold byte equality and failure, resume, corruption and concurrent-update cases passed, including isolated real Bundles and migration of an existing 255842-Bundle index. Updates still compare metadata and read/write compact index records; future full game-update latency remains to be measured. Use the archive matching your Windows x64 .NET Desktop Runtime. No game assets are distributed.
