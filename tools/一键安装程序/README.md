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

**运行中的 NlueMix 会把 DLL 映射住，覆盖释放会 `Access Denied`。** 这是最费时间的一个坑。
`ExtractToFile(overwrite: true)` 内部是 `FileMode.Create`，也就是**截断重写**；
而正在运行的 .NET 程序，它的程序集是被内存映射（image section）着的，**映射文件不允许被截断**，
于是报 `Access to the path '...\Concentus.dll' is denied`。

两个反直觉的地方：

1. 用 `FileMode.Open` 去试开那个 DLL 是**测不出来**的 —— 不截断就能成功，会给你"文件没被占用"的假结论。
2. 映射文件**可以改名，只是不能截断**。所以"把旧文件挪到一边、再写新的"能成功，
   用户不必先关软件。

现在安装程序的处理顺序是：尽力杀进程 → 覆盖失败重试 5 次 → 还不行就把旧文件改名成
`xxx.dll.old` 再写新的（下次安装时清掉）。

**`GetProcessesByName` 会返回早就死掉的进程条目。** Windows 上进程退出后，只要还有别的进程
持有它的句柄（例如调试器、性能计数器），进程表项就一直留着 —— 实测有台机器上挂着 3 个几个月前
就结束的条目（`Threads=0`）。所以**不要**用"列表非空"当"还在运行"，会永远等不到；
也不要靠读 `Threads.Count` 判断死活 —— 读它需要权限，受限环境下会抛异常，
活进程会被误判成死的。结论：杀进程只做 best-effort，正确性交给"改名挪走"那条兜底路径。
