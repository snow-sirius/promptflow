# PromptFlow

Windows AI 剪贴板助手，使用 .NET 8 WPF 构建。

## 构建

```powershell
dotnet restore PromptFlow\PromptFlow.csproj
dotnet build PromptFlow\PromptFlow.csproj
dotnet publish PromptFlow\PromptFlow.csproj -c Release -r win-x64 --self-contained true -o publish
```

发布目录可直接作为 Inno Setup `installer\PromptFlow.iss` 的输入。

编译安装包（本机 Inno Setup 6.7.3）：

```powershell
& 'C:\Users\Huawei\AppData\Local\Programs\Inno Setup 6\ISCC.exe' installer\PromptFlow.iss
```

默认快捷键为 `Ctrl+鼠标侧键 1`，可在设置中改为 `Ctrl+XButton2` 或键盘组合（例如 `Ctrl+Shift+P`）。
