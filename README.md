# 浅梦（DesktopBuddy）

浅梦是一个面向 Windows 的桌面伙伴应用。本仓库提供源代码，供社区检查和继续改进。当前源代码快照对应项目记录的 0.5.13 开发线，动画自然度仍在审查中；它不是已验收的成熟动作版本，也不是签名安装包。

## 当前状态

- 目标平台：Windows x64。当前有 Windows 11 x64 开发证据；Windows 10、ARM64 和签名安装验收尚未完成。
- 动作质量尚未通过完整验收。脸部、手臂/手型、接触过程和多动作转换仍需要正常速度视频、多视角检查及独立复查。
- 最新手腕路径诊断只确认了部分采样时刻存在肩腕距离缩短；隔离候选仍未构建或验证，不能视作修复。
- 仓库不包含角色模型。请导入你有权使用的本地 GLB；不需要也不应上传私人模型。
- GitHub 中的工程记录是脱敏摘要，不包含原始聊天记录、私人路径、模型文件或本机测试截图。

## 构建

需要 Windows x64、.NET 10 SDK；制作 NSIS 安装包还需 NSIS 3。克隆仓库后，在仓库根目录运行：

`.\build-win.ps1 -ProjectDirectory (Get-Location).Path`

如果只需自包含程序，可运行：

`dotnet publish .\src\DesktopBuddy.App\DesktopBuddy.App.csproj -c Release -r win-x64 --self-contained true -o .\dist\win-x64 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`

在应用的模型页导入本地 GLB。不同模型的骨骼、蒙皮和授权各不相同；导入模型不会使它自动成为项目默认素材。

## 开源范围

本仓库根目录的 MIT 许可证适用于项目原创代码和文档。第三方组件与资料仍受 `third-party/notices` 中各自的许可证和署名条件约束。未授权的角色模型、纹理和聊天内容不属于本仓库。

## 文档

- `docs/PROJECT-STATUS.md`：当前完成情况和未通过项。
- `docs/DEVELOPMENT-LOG.md`：脱敏后的工程记录摘要。
- `docs/CONTRIBUTING.md`：构建、贡献和证据边界。
- `docs/RECOGNITION-ARCHITECTURE-DRAFT.md`：后续识别系统的草案；尚未实现。
- `third-party/notices`：第三方许可证与署名信息。
