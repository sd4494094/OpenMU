# OpenMU 项目协作规则

本文件适用于本项目的 AI 助手及贡献者。默认用中文与用户沟通，直接说明结果、验证情况和下一步。
本仓库是 `sd4494094/OpenMU` 的开发仓库，日常集成分支为 `dev`。

## 1. 环境分工与唯一开发工作区

**dev 上的 Git 仓库是唯一的服务端开发工作区。** 本机 Codex 通过 SSH/MCP 或远程工作区操作它；
服务端代码修改、生成代码、依赖还原、构建、自动测试和开发服务运行均在 dev 上完成。

| 环境 | 连接与位置 | 用途 |
|---|---|---|
| 本机 Windows | `F:\OpenMU` | 人类与 Codex 交互、查看资料、维护本机配置；代码只作为按需更新的参考副本 |
| dev 深圳开发机 | `Proxy7-sz-OpenMU-Dev`，`will@10.201.0.9`，`/home/will/OpenMU` | 唯一服务端开发工作区，构建、测试与开发运行 |
| prod 香港生产机 | `Proxy8-hk-OpenMU-Prod`，`root@10.201.0.10` | 生产部署与运行，管理访问优先通过 WireGuard |
| 本机游戏客户端 | `E:\MU` | Windows 客户端、中文资源与人工联调，不属于服务端构建工作区 |

- Codex 的当前目录是 `F:\OpenMU`，并不表示命令会自动在 dev 执行。每次明确命令运行位置；
  远端操作显式指定 dev 连接器及 `/home/will/OpenMU`，不要对本机执行服务端编辑或构建。
- SSH/MCP 是可行的远程操作方式，不必为了开发额外启动一个远端 Codex 会话。
  若工具只支持本机文件，改用远端命令或远端文件工具，不能悄悄退回本机开发。
- 本机可维护 `AGENTS.md`、本机配置、私有研究资料及 Windows 客户端资源；
  共享协作规则需同时让 dev 工作区生效，后续纳入 Git。
- dev 无法连接时，继续可独立完成的资料检查并报告连接问题；暂停依赖 dev 的代码修改、构建和测试，
  不将 prod 或本机临时当作开发服务器。
- 地址和路径是当前约定，每次操作仍需核实，不能据此声称连接、环境或部署已经就绪。

## 2. 每次开始工作

1. 阅读本文件；修改代码前阅读 [代码规则](docs/CODING_RULES.md)，并遵守目标目录下更具体的说明。
2. 使用 dev SSH/MCP 执行只读检查：主机名、登录用户、仓库路径、`git status --short --branch`、
   `git rev-parse HEAD`、`git remote -v`。确认目标确实是 dev。
3. 查看工作区已有修改，不覆盖、还原或误提交他人的内容；遇到冲突先保留并分析。
4. 按任务检查实际 SDK、依赖、数据库和开发服务。未验证前，不声称“可构建”“测试通过”或“已部署”。

dev 的默认登录 shell 可能是 fish。需要 POSIX shell 语法时显式使用 `sh -c` 或 `bash -c`，
不要把 bash 循环直接交给 fish。尽量避免不必要的 login shell，防止用户启动脚本影响无界面命令。
长时间构建和测试使用远端后台任务并查询状态，避免超时后重复启动。

## 3. Git 同步与分支

- `origin` 为我们自己的仓库 `sd4494094/OpenMU`；`upstream` 若存在，为原项目上游。
  日常开发基于我们自己的 `dev`；较大任务可从它创建任务分支，最终集成回 `dev`。
- 本机与 dev 通过 **Git 提交和分支** 同步，不进行双向目录镜像、自动覆盖或反复复制源文件。
  本机需要查看最新代码时，先检查本机工作区，再获取远端提交；仅在可安全快进时执行 `git pull --ff-only`。
- 未提交的改动不会被 `git pull` 同步。迁移已有本机修改时，先审查 diff 和未跟踪文件；
  通过明确的提交或一次性补丁移交 dev，并核对结果，之后只在 dev 继续开发。
  补丁移交不能成为日常同步方式。
- 不使用 `reset --hard`、强制覆盖、丢弃未跟踪文件或强推来“解决同步”；
  不擅自自动 stash、提交或合并无关修改。共享凭据与运行数据不通过 Git 迁移。
- 按用户当前任务范围处理提交、推送和发布；授权已明确时直接完成，不重复索要确认。
  提交主题采用 conventional commits，例如 `fix(localization): ...`、`docs(workflow): ...`。
- [CONTRIBUTING.md](CONTRIBUTING.md) 中向上游 `master` 提 PR、提前开 issue 等流程，
  适用于明确向上游贡献的任务，不作为本项目日常 dev 开发的前置步骤。

## 4. 代码质量与兼容性

[docs/CODING_RULES.md](docs/CODING_RULES.md) 是代码风格与质量的权威基线，不在此重复全文。尤其注意：

- 修改保持聚焦，不顺手升级框架/依赖、清理无关代码或改动既有行为。
- 不复制、翻译移植或改写原服务端反编译代码。第三方资料只作为行为研究线索，需交叉核对。
- 协议修改从 XML 定义入手，生成文件由 dev 构建生成；其他生成代码同样修改源定义。
- 配置数据变更同时考虑新数据库初始化与现有数据库更新；数据模型变更按要求提供迁移。
- 持久化 GUID、枚举值和协议字段是兼容性约定，不能随意重编号或复用。
- 当前稳定客户端基准是 S6E3 1.04d ENG 的中文修正版；MuMain 属于独立候选测试路径，
  不得在未验证的情况下替换稳定客户端或改变原协议。
- 玩家可见文本通过资源本地化，不在程序中写死英文；翻译保留占位符、格式、编码与协议长度约束。
  中文资源修改需检查客户端实际显示，不把“资源已翻译”当作“所有场景均已验证”。

<a id="building-and-running-tests"></a>

## 5. 构建与测试（仅在 dev）

- 使用项目实际要求的 .NET SDK，先检查 `dotnet --info` 和项目配置；
  缺少 SDK 时解决 dev 环境问题，不能改目标框架来绕过，也不能用本机构建代替。
- 按改动范围构建、运行相关测试；纯文档修改检查内容、链接和 diff，无需运行整套 .NET 测试。
- 同一版本代码先构建一次，再以相同配置使用 `--no-build` 运行测试。
  代码、项目、包或生成输入改变后必须重新构建，不能用旧产物报告通过。
- 先确认目标测试项目已包含在本次构建中；否则构建对应测试项目后再使用 `--no-build`。
- 配置初始化、协议、数据库和游戏机制变更遵循代码规则中对应的专项验证要求。
  客户端人工验证由本机完成；需用户操作的步骤明确交付，不虚构测试结果。

以下示例在 dev 仓库根目录执行，实际测试项目按任务选择：

```bash
cd /home/will/OpenMU
dotnet build src/MUnique.OpenMU.sln
dotnet test tests/MUnique.OpenMU.Tests --no-build
```

报告说明执行环境、验证范围和结果；未运行、失败、阻塞和待人工检查的部分分别说清楚。

## 6. 生产部署与客户端联调

- prod 不承担日常代码编辑和测试。生产发布使用 dev 验证过、可追溯到提交的版本，
  记录部署前后版本、变更范围、配置/迁移和恢复方法。
- 仅评估或检查时只读访问 prod；部署、数据库写入、重启按用户已授权的范围执行。
  已授权完成部署和重启时，做好必要验证后直接执行，不重复请求同一项许可。
- 数据库与服务配置按环境隔离，不让开发实例误连生产数据库；凭据不写进代码或共享配置。
- 客户端调试遵循用户要求：集中修正后再进行一次验证，不频繁启动游戏。
  没有实际启动时不要声称启动过，没有人工验证时不要声称联调通过。
- 下载的原始压缩包保持原样，解压到独立目录；修改客户端前备份受影响文件，保留恢复方法。
  新客户端、第三方补丁先隔离评估，不能直接覆盖稳定的 `E:\MU`。

## 7. 凭据与本机私有资料

- 本机凭据存放于已忽略的 `F:\OpenMU\.env`；各环境使用各自配置，
  不将完整本机 `.env` 拷到 dev 或 prod。不要输出密码、私钥、令牌、Cookie 或完整连接串。
- SSH/MCP 的认证由连接器配置提供。已配置从 `.env` 读取时，也需以实际登录验证是否生效；
  普通 shell 不会自动继承 `.env`，不要假设更新文件就更新了所有运行进程。
- `.env`、`.claude/`、`downloads/`、`.local_docs/` 保持 Git 忽略。
  修改后检查 `git status`；需要时使用 `git check-ignore` 确认敏感文件没有进入版本控制。
- 私有论坛与补丁调研从本机 `.local_docs/README.md` 开始，复用现有索引、帖子正文及
  `.local_docs/补丁与开发资源评估.md`；新增调查记录来源、日期、版本和验证程度。
- `.local_docs` 和 `downloads` 不通过 Git 同步；远端任务需要其中的非敏感资料时，
  仅按需提供必要内容，不整目录上传。

## 8. 项目资料入口

| 主题 | 位置 |
|---|---|
| 代码规则 | [docs/CODING_RULES.md](docs/CODING_RULES.md) |
| 源码运行 | [from-source.md](docs-website/docs/getting-started/from-source.md) |
| 架构与项目划分 | [architecture.md](docs-website/docs/development/architecture.md)、[solution-structure.md](docs-website/docs/development/solution-structure.md) |
| 插件机制 | [src/PlugIns/Readme.md](src/PlugIns/Readme.md) |
| 协议与生成来源 | [packets.md](docs-website/docs/reference/packets.md)、`src/Network/Packets/`、`docs/Packets/` |
| 数据库迁移 | [Cheatsheet.md](src/Persistence/EntityFramework/Migrations/Cheatsheet.md) |
| 部署说明 | [deploy/README.md](deploy/README.md) 及实际部署方式的子目录说明 |
| 文档归属 | [docs/Readme.md](docs/Readme.md) |

各项目的 `Readme.md` 提供更具体说明。产品/运维说明写入 `docs-website/docs`，
代码相关文档留在代码旁；优先更新已有页面。
