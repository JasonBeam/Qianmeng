# AI 协作开发教程（新贡献者版）

这份指南给第一次参与浅梦（DesktopBuddy）开发的朋友。你可以使用 Codex、ChatGPT、Claude 或其他代码助手；AI 可以帮你读代码、定位问题、拟定补丁和检查改动，最终是否通过仍由你在目标环境中复核。

## 1. 先弄清仓库现在是什么状态

这份仓库是 0.5.13 开发线的源码快照，不是已验收的正式安装包。开始前先读：

1. `README.md`：平台、构建方式和开源范围。
2. `docs/PROJECT-STATUS.md`：已实现功能与尚未验收的问题。
3. `docs/CONTRIBUTING.md`：证据要求、素材和隐私边界。
4. `docs/DEVELOPMENT-LOG.md`：脱敏后的技术方向和失败记录。
5. `PUBLICATION-MANIFEST.md`：本次发布包含和排除了什么。

项目使用 WPF/.NET，目标平台是 Windows x64。仓库不附带角色 GLB/FBX 模型。通用识别系统目前只有设计草案，不要把它说成已实现功能。

## 2. 安全克隆并开一个自己的分支

在 Windows 电脑安装 Git 和 .NET 10 SDK，然后用 PowerShell：

```powershell
git clone https://github.com/JasonBeam/Qianmeng.git
cd Qianmeng
git status --short --branch
git switch -c fix/hand-contact-timing
```

开始前确认工作树干净，并在个人分支上工作。不要直接提交到 `main`。如果目录里已经有你自己的改动，先看清 `git status` 和差异，别让 AI 重置、清理或覆盖它们。

## 3. 安装和运行检查

先确认 SDK：

```powershell
dotnet --info
```

输出应包含 .NET 10 SDK。NSIS 安装包还需要 NSIS 3。

模型核心检查是一个控制台检查程序，不是 xUnit 测试套件：

```powershell
dotnet run --project .\tests\DesktopBuddy.ModelChecks\DesktopBuddy.ModelChecks.csproj
```

若要检查自己有权使用的本地 GLB，可把原文件留在仓库外，并把缓存输出也放到临时目录：

```powershell
dotnet run --project .\tests\DesktopBuddy.ModelChecks\DesktopBuddy.ModelChecks.csproj -- `
  "C:\Models\your-model.glb" `
  "$env:TEMP\qianmeng-model-check.glb"
```

Windows/WPF 检查需要 Windows 交互桌面。例如动画核心检查：

```powershell
dotnet run --project .\tests\DesktopBuddy.WindowsChecks\DesktopBuddy.WindowsChecks.csproj -- `
  --animation-core-checks `
  --report "$env:TEMP\qianmeng-animation-core.json"
```

其他检查模式见 `tests/DesktopBuddy.WindowsChecks/Program.cs`。按改动选择相关检查；不要把检查宿主的结果写成实际安装版 EXE 的结果。

要构建程序和 NSIS 安装包，可在 Windows x64 的 PowerShell 中运行：

```powershell
.\build-win.ps1 -ProjectDirectory (Get-Location).Path
```

这个脚本需要 .NET 10 SDK 和 NSIS 3；它会先删除并重建 `dist\releases\0.5.13\win-x64`。建议在新克隆的干净目录构建；若该输出目录有需要保留的东西，先检查或另行备份。

## 4. 怎样让 AI 有效地帮忙

一次只交给 AI 一个可说明、可复现的问题。给它这些信息：

- 你使用的分支、`git status --short --branch` 输出和相关文件路径。
- 复现步骤、实际结果、期望结果，以及相关错误日志。
- 你已经做过的检查和仍未确认的猜测。
- 哪些文件或数据必须保留，测试要在哪个环境进行。

先让 AI 阅读相关文档和代码，要求它指出证据与不确定之处，再讨论修法。改动应尽量小；完成后让它说明改了哪些文件、为什么、跑了哪些实际命令，以及哪些验收仍未做。检查 `git diff` 后再提交。不要把密码、访问令牌、私人模型、个人用户目录或未脱敏截图贴给 AI。

### 可以直接发给代码助手的项目说明

```text
你正在协助开发浅梦（DesktopBuddy）。先阅读 README.md、PUBLICATION-MANIFEST.md、docs/PROJECT-STATUS.md 和 docs/CONTRIBUTING.md，再查看与问题直接相关的代码。

先报告：当前分支和工作树状态、复现路径、你找到的证据、尚未确认的假设，以及最小修复方案。保留已有改动；不要重置、清理、覆盖或删除文件，不要改动或上传私有模型、贴图、密钥、原始聊天和本机数据。

一次只处理我指定的问题。修改后检查 git diff，并只运行与改动相关、且我能在本机复核的检查。明确区分“已运行并通过”“失败”“未运行”；不要推断测试结果，也不要把测试宿主说成正式 EXE 或 NSIS 安装版。
```

## 5. 动作问题怎样验收

本项目的动画自然度尚未通过整体验收。改动作时不要只追求数值通过、摆幅更大或某一帧好看：

- 记录完整动作：待机、进入、动作主体、接触、解除接触、回到待机；以正常速度播放。
- 从正面、侧面和手部近景检查重心、锁骨/肩/肘/腕协同、手指形态、遮挡和接触时序。
- 同一个模型、相同视角、相同播放速度下对比修改前后；静帧和单一指标不能证明动作自然。
- 不要通过放宽或删除性能阈值让失败变成通过。CPU、内存和测量时长要写清楚。
- 把 WPF 测试宿主、直接运行的 EXE、NSIS 安装后的 EXE 分别记录；一种环境的结果不能替代另一种。

角色模型是使用者的本地素材。没有额外授权时，不编辑模型顶点、蒙皮权重、材质或纹理；若任务只是调整桌面显示大小，只调整应用的显示缩放，不改模型文件。不要把模型或相关截图上传到公开仓库或 AI 服务。

## 6. 识别功能和隐私

`docs/RECOGNITION-ARCHITECTURE-DRAFT.md` 是草案，不代表实现已完成。它不授权截屏、OCR、读取文档内容或向网络发送前台应用信息。提出识别功能时，先写清采集什么、在哪里处理、何时运行、怎样让用户授权和关闭，再讨论实现。

新增第三方代码、模型或素材前，先核对其许可证和再分发条件；“参考了某项目”不等于可以复制它的代码或动作数据。检查 `third-party/notices` 并保留要求的署名。

## 7. 完成后检查并开 Pull Request

提交前至少执行：

```powershell
git diff --check
git status --short
git diff --stat
git diff
```

确认差异里没有用户目录、私人素材、密钥、生成报告、临时缓存或意外的大文件。然后提交个人分支并推送：

```powershell
# 把路径替换成你检查过的实际文件路径
git add .\path\to\changed-file.cs
git commit -m "Improve arm contact timing"
git push -u origin fix/hand-contact-timing
```

在 GitHub 上对 `main` 发起 Pull Request。说明：问题和复现方法、修复思路、具体改动、实际运行过的命令及结果、未完成的检查，以及涉及动画时的正常速度视频和多视角证据。未做的测试请标为“未运行”，不要用推测补齐。

## 8. 一轮 AI 协作的模板

开工时：

```text
问题：<一句话说明>
复现步骤：<按顺序列出>
期望结果：<说明>
实际结果：<说明，附已脱敏错误>
相关路径：<文件路径>
必须保留：<列出已有改动/本地素材/目录>
验收环境：<Windows 版本、测试宿主/EXE/安装版>
请先只读定位并给出最小方案；我确认后再改。
```

提交前让 AI 自查：

```text
请只审查当前分支相对 main 的差异。找出回归风险、未覆盖的失败路径、隐私/许可证问题和与项目状态文档不一致的说法。不要改文件。每条结论引用文件和代码位置；无法确认的内容明确标成疑问。
```
