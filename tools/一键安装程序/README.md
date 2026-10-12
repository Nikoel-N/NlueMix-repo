# 一键安装程序（发布用的那个）

`发布\电脑端-用户版-安装程序.exe` 就是这个工程编出来的：一个自包含单文件 exe，
内嵌整个电脑端用户版（payload.zip），双击即装。

它只做四件事，全部按用户身份完成，**不需要管理员权限**：

1. 关掉正在运行的 NlueMix
2. 把内嵌的 payload.zip 释放到 `%LOCALAPPDATA%\Programs\NlueMix`
3. 建桌面 / 开始菜单快捷方式
4. 生成 `卸载.bat`

## 怎么编

`payload.zip` 不在仓库里（它是编译产物），要先造出来：

```powershell
# 1) 先编电脑端用户版
dotnet publish ..\..\src\NlueMix\NlueMix.csproj -c Release -r win-x64 `
  --self-contained false -o ..\..\发布\电脑端-用户版

# 2) 把它的输出压成 payload.zip（放在本目录）
Compress-Archive -Path ..\..\发布\电脑端-用户版\* -DestinationPath payload.zip

# 3) 编安装程序（自包含单文件，约 70 MB）
dotnet publish Launcher.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -p:Version=0.2.0 -o out
```

`BlueMix\_验证\build-release.ps1` 把这三步连同其余发布产物一起做了，改版本号时改那一处即可。

## 踩过的坑

**`Encoding.GetEncoding(936)` 在 .NET 8 上会抛异常。** 生成 `卸载.bat` 时要把中文按 GBK
写入，而 .NET Core 之后只自带 UTF-8/UTF-16/ASCII，传统代码页必须显式注册：

```csharp
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
```

少了这一句，安装程序会在第 3 步抛 `NotSupportedException`，**用户永远看不到"安装成功"，
也拿不到卸载脚本**——但它已经把文件释放完了，所以表现成"装是装上了，但报了个看不懂的错"。
`System.Text.Encoding.CodePages` 就在 .NET 共享框架里，不需要额外 NuGet 包。

**不要从网络共享或受限目录直接运行。** 某些受限执行环境会给该目录下的进程套上写入限制，
表现是释放文件时报 `Access to the path ... is denied`，看起来像权限问题，其实是执行位置的限制。
把 exe 拷到本地再跑即可。
