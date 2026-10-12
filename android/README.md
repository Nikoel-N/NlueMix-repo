# NlueMix 安卓发送端

把手机的声音发到电脑上，再由电脑转给蓝牙音响。

## 怎么用

1. 电脑上打开 **NlueMix.exe**
2. 手机装 **NlueMix.apk**（构建产物在 `app/build/outputs/apk/debug/`）
3. 打开 App，填入电脑的 **IP 地址**（NlueMix 界面上有）和 **4 位连接码**
4. 点「开始串流」
5. 第一次会弹「是否允许录制或投放」→ **允许**

之后手机放什么，电脑的音响就出什么。

## 为什么必须要「录屏授权」

安卓从 10 开始**不允许 App 偷录别的 App 的声音**（防止盗录音频）。

官方给的唯一正道就是 `MediaProjection`（录屏 API）+ `AudioPlaybackCapture`。
所以：

- 弹窗写着「录制或投放」，**我们只拿音频，不录屏幕**
- 授权是**一次性**的，停止后失效，下次要重新点
- 状态栏会出现一个「正在投放」的小图标，这是系统的，去不掉

**抓不到声音的 App**：Netflix 等带版权保护的（它们主动声明了不允许被捕获）。
网易云、QQ音乐、B站、抖音、YouTube 等**都能正常抓**。

## 系统要求

- **Android 10（API 29）或更高** —— 系统音频采集是 Android 10 才有的 API
- 手机和电脑在**同一个 Wi-Fi** 下

## 代码结构

```
app/src/main/java/com/nluemix/sender/
├── MainActivity.kt     界面：两个输入框 + 一个按钮
├── CaptureService.kt   采集：MediaProjection + AudioPlaybackCapture → 20ms 分包发送
└── Protocol.kt         协议：TCP 握手 + UDP 音频（和电脑端 Protocol.cs 一一对应）
```

### 工作流程

```
点「开始串流」
   ↓
① TCP 连电脑 → 发 HELLO（带连接码）→ 等 WELCOME
   ↓ 连接码错了就在这里被拒，不会白授权一次
② 请求 MediaProjection 授权
   ↓
③ AudioRecord 抓系统声音（48 kHz 立体声 PCM16）
   ↓
④ 每 20 ms 一包（960 采样 = 3840 字节）→ UDP 发到电脑
```

### 为什么先握手再授权

用户体验考虑：**连接码错了应该立刻告诉他**，而不是让他先授权录屏、
再看到「被拒绝」。所以 TCP 预检查放在最前面。

## 协议（和电脑端必须一致）

| 项 | 值 |
|---|---|
| 控制端口 | TCP **47820** |
| 音频端口 | UDP **47821** |
| 魔术字 | `0x4D424C55` |
| 采样率 | 48000 Hz |
| 声道 | 2（立体声） |
| 编码 | PCM16（**无损直传**） |
| 帧长 | 960 采样 / 20 ms |

**PCM 直传的理由**：局域网带宽不是瓶颈（48 kHz 立体声 PCM16 ≈ 1.5 Mbps），
用 Opus 压缩只会白白引入一层编码损失。电脑端也实现了 Opus 解码，留给以后带宽紧张的场景。

### 静音优化

连续 25 帧（0.5 秒）峰值低于 40 就不发包 —— 省带宽、省电，
而且**电脑端的让路逻辑不受影响**（没声音正好不该让路）。

## 自己构建

```powershell
cd NlueMix\android
.\build-apk.ps1
```

需要：
- **JDK 17**
- **Android SDK**（platform 34 + build-tools 34.0.0）
- **Gradle 8.5**

路径在 `local.properties` 和 `gradle.properties` 里配。

## 已知限制

- 只支持 **Android**；iOS 完全不开放系统音频采集（除非越狱或用 AirPlay）
- 录音时状态栏有系统图标
- 部分游戏/DRM 内容抓不到（安卓的限制，不是软件的问题）
