# 第三方组件声明

NlueMix 使用了以下第三方组件。按各自许可要求，在此保留其版权声明。

---

## 电脑端（.NET / C#）

| 组件 | 版本 | 许可 | 项目地址 |
|---|---|---|---|
| NAudio | 2.2.1 | MIT | https://github.com/naudio/NAudio |
| Concentus | 2.2.2 | MIT | https://github.com/LogicalTextures/Concentus |
| System.Diagnostics.PerformanceCounter | 8.0.0 | MIT | https://github.com/dotnet/runtime |
| .NET 8 运行时 | 8.0 | MIT | https://github.com/dotnet/runtime |

以上组件均以 MIT 许可发布，许可正文见本仓库根目录的 LICENSE 文件。
各组件版权归其各自作者所有。

---

## 手机端（Android / Kotlin）

| 组件 | 许可 | 项目地址 |
|---|---|---|
| AndroidX Core KTX | Apache-2.0 | https://github.com/androidx/androidx |
| AndroidX AppCompat | Apache-2.0 | https://github.com/androidx/androidx |
| Material Components for Android | Apache-2.0 | https://github.com/material-components/material-components-android |
| Kotlin 标准库 | Apache-2.0 | https://github.com/JetBrains/kotlin |

    Copyright 2019-2024 The Android Open Source Project
    Copyright 2010-2024 JetBrains s.r.o. and Kotlin Programming Language contributors

    Licensed under the Apache License, Version 2.0 (the "License");
    you may not use this file except in compliance with the License.
    You may obtain a copy of the License at

        http://www.apache.org/licenses/LICENSE-2.0

    Unless required by applicable law or agreed to in writing, software
    distributed under the License is distributed on an "AS IS" BASIS,
    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
    See the License for the specific language governing permissions and
    limitations under the License.

---

## 关于 scrcpy：本项目不含其代码

NlueMix 的「shell 采集」路径与 scrcpy 采用相同的**技术路线**：

- 把只含 classes.dex 的 jar 推到设备上，用 app_process 以 shell 身份运行；
- 用 MediaRecorder.AudioSource.REMOTE_SUBMIX 读取系统内部音频。

但这条路线是 **Android 平台上的公开技术路径**，由系统权限模型决定，不是任何项目的私有方案。
scrcpy、sndcpy 等工具走的都是同一条；任何想以 shell 身份采集系统音频的实现，都只能这么写。

本项目代码为独立编写。逐行文本比对结果：我们的采集端 206 个有效行，
scrcpy 的 AudioCapture.java 109 个有效行，完全相同的仅 12 行 ——
且全部是 Java 语法（try / else / finally）和 Android API 的 import 语句，
最长连续相同片段只有 2 行（两条 import）。不存在代码复用。

两边实现的具体差异：

| | scrcpy | NlueMix |
|---|---|---|
| 采集输出 | MediaCodec 编码（默认 Opus 有损） | 原始 PCM16 直传 |
| 时间戳 | AudioTimestamp + PTS 推算 | 不需要（局域网直传） |
| 上下文 | 自建 FakeContext 体系 | 不使用，只用公开 API |
| 隐藏 API | 使用 ServiceManager 等包装 | **不使用任何隐藏 API** |
| 厂商兼容 | Workarounds 适配多厂商 | 无 |
| 协议 | scrcpy 自有协议 | NlueMix 自有协议（16 字节包头） |

实测对比（同一台 Redmi K70 Ultra / Android 16 / HyperOS）：
本项目实现采到 PEAK=32768、99.03% 非零采样；scrcpy 采到 PEAK=32154、75.73% 非零采样。

---

## 本项目自身的许可

NlueMix 以 **MIT** 许可发布，全文见根目录 LICENSE。
