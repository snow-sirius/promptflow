# PromptFlow Project Notes

## Workspace

- Project root: `D:\codes\202608\promptflow`
- Temporary files: `D:\codes\202608\promptflow\turn`
- Runtime logs: `D:\codes\202608\promptflow\turn\logs`

## Environment

- .NET SDK: `8.0.422`
- Windows Desktop runtime: `8.0.28`
- Codegraph: `C:\Users\Huawei\AppData\Local\codegraph\codegraph.cmd`
- rtk: `C:\Users\Huawei\.local\bin\rtk.exe`
- Java/Node/Maven/EnvKey: not required by this WPF project

## Project Commands

- Build: `dotnet build PromptFlow\PromptFlow.csproj`
- Test: `dotnet test`
- Publish: `dotnet publish PromptFlow\PromptFlow.csproj -c Release -r win-x64 --self-contained true`
- Installer: compile `installer\PromptFlow.iss` with Inno Setup Compiler
- Inno Setup compiler: `C:\Users\Huawei\AppData\Local\Programs\Inno Setup 6\ISCC.exe` (version `6.7.3`; not on PATH)

## Local Rules

- Data is stored under the user's LocalAppData by default.
- Do not commit `bin`, `obj`, `turn`, or published installer output.
- Preserve user data during upgrades; uninstall offers explicit data removal.
