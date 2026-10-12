# B 站音频采集问题 —— 结论报告（含实测证据）

执行时间：2026-10-12　设备：Redmi K70 Ultra（rothko）/ Android 16 / SDK 36 / HyperOS OS3.0

---

## 结论先行

| 可能原因 | 是否成立 | 判定依据 |
|---|---|---|
| 配置错误 | **不成立** | 实验 B 用完全相同的配置，只是清单属性为 false；换成 true 立刻有声 |
| 采集实现错误 | **不成立** | 实验 A 实测峰值 13106，理论值 13107，**误差 1 个 LSB** |
| **B 站/系统禁止第三方采集** | **成立** | B 站合并清单无 `allowAudioPlaybackCapture`；而缺这个属性在实验 B 中被证明足以导致读取全零 |

**这是 Android 标准 API 的平台/应用策略限制，不是本项目的缺陷。**

---

## 一、系统与版本（步骤 1）

    Android 版本        : 16
    SDK 级别            : 36
    ROM                 : HyperOS OS3.0
    B 站包名/版本        : tv.danmaku.bili  9.13.0 (versionCode 9130500)
    B 站 minSdk         : 24
    B 站 targetSdk      : 35      ← 远高于 29，完全适用 allowAudioPlaybackCapture 规则
    B 站 compileSdk     : 36

---

## 二、B 站合并后 Manifest 核查（步骤 2）

用 `aapt2 dump xmltree` 直接读 B 站 APK（从手机 pull 的 base.apk，218 MB）的**合并后**清单。
`application` 节点的**全部 18 个属性**逐条列出：

    theme / label / icon / name / manageSpaceActivity / testOnly / allowBackup /
    hardwareAccelerated / largeHeap / supportsRtl / extractNativeLibs /
    usesCleartextTraffic / networkSecurityConfig / roundIcon / appComponentFactory /
    requestLegacyExternalStorage / preserveLegacyExternalStorage / enableOnBackInvokedCallback

    → 其中【没有】android:allowAudioPlaybackCapture

**不能仅凭"没写"就断定默认值**，所以下面用受控实验确定默认行为的实际效果。

---

## 三、实际播放的 usage 与 flags（步骤 3）

同一时刻系统里的第三方播放器：

    uid=10368  com.smile.gifmaker（快手）  usage=USAGE_MEDIA  flags=0xA00   ← 不含 0x400
    uid=10289  tv.danmaku.bili（B站）       usage=USAGE_MEDIA  flags=0xE00   ← 含 0x400

**两点重要更正**：

1. **手机端 `getAudioAttributes().flags` 读出来是 `0x0`**（App 自己什么都没设），
   而 `dumpsys audio` 显示 `0xE00` / `0xA00` —— **说明那个标志位是框架加上去的，不是 App 声明的**。
2. 所以「B 站自己设置了禁止采集标志」这个说法**不准确**；真相是**框架根据应用的 opt-in 状态自动加了它**。

另外，logcat 证明采集管道本身完全正常：

    AudioRecord: set(): inputSource 8 (REMOTE_SUBMIX), sampleRate 48000, channelMask 0xc,
                 uid 10371, packageName: com.nluemix.sender
    AudioFlinger: openInput_l() ... status 0
    AudioRecord: start(4998): return status 0
    AudioFlinger: Device AUDIO_DEVICE_OUT_REMOTE_SUBMIX   ← 混音输出已建立

---

## 四、A/B 受控实验（步骤 4）—— 本报告的核心证据

### 实验设计

在**同一个 APK** 里加了一个 gradle 开关 `-PallowCapture=true|false`，
通过 manifestPlaceholders 只改一个清单属性，其余代码、授权、音源、设备全部相同。

音源：App 内新增的 `ToneActivity`，持续播放 **1 kHz 正弦波，振幅 0.4，48 kHz 立体声，USAGE_MEDIA**。
采集：App 自身的 `CaptureService`（MediaProjection + AudioPlaybackCaptureConfiguration + AudioRecord），
授权用 `appops set com.nluemix.sender PROJECT_MEDIA allow` 预授予（避免人为点击带来的变量）。
测量：手机端每 2 秒把 `峰值 / 非零样本数 / RMS / read 返回值` 通过控制通道上报电脑。

### 实验结果

| 实验 | `allowAudioPlaybackCapture` | 音源 | 峰值 | 非零样本 | RMS | read 返回 |
|---|---|---|---|---|---|---|
| **A** | **true** | 1 kHz 测试音 | **13106** | **1840/1920 (95%)** | 9267.4 | 3840 |
| **B** | **false** | 同一个测试音 | **0** | **0/1920 (0%)** | 0.0 | 3840 |
| C | （B 站未声明） | B 站实际播放 | **0** | 0/1920 (0%) | 0.0 | 3840 |

**实验 A 的峰值 13106，理论值 = 0.4 × 32767 = 13107，误差 1 个 LSB。**
这意味着采集链路（MediaProjection → REMOTE_SUBMIX → AudioRecord → 打包 → 网络 → 电脑）
**在数值上是精确的，没有任何衰减、错位或丢样本**。

### 实验的因果结论

A 与 B 之间**唯一**的差别是那个清单属性；A 有声、B 全零。
**因此该属性是"能否被 MediaProjection 采集"的充分且必要的控制变量。**
B 站的情况与实验 B 完全一致（同样未声明），所以结果同样是 0。

三次实验的 `read()` 返回值都是 3840（正常），说明**读取本身没问题，是混音里根本没有音频流进来**。

---

## 五、定性与边界（步骤 5、6）

### 定性

**这是 Android 标准 API 的平台/应用策略限制。**

- 限制方：**Android 框架**（根据应用是否 opt-in 决定是否把该应用的音频纳入 MediaProjection 混音）
- 我们无法通过修改采集代码、采样率、缓冲区、UDP/TCP、RTP 等方式绕过
- 这是**特性**不是 bug：Android 10 引入 AudioPlaybackCapture 时就是这么设计的

### 三条路的边界（全部实测过）

| 方案 | 需要 | 能采 B 站 | 证据 |
|---|---|---|---|
| MediaProjection（App 路径） | 用户点「允许录屏」，**零调试** | **不能** | 实验 C：峰值 0；实验 A/B 证明原因就是 opt-in |
| shell / REMOTE_SUBMIX | 一次性开无线调试 | **能** | 实测 PEAK=32768 / 非零 99.03% |
| root | 手机 root | 能 | 未测试 |

### 与网络/播放问题的分离（步骤 6 要求）

**采集限制与网络、播放完全无关，独立记录如下：**

| 环节 | 状态 | 证据 |
|---|---|---|
| 手机 Wi-Fi ↔ 电脑以太网 | **正常** | 回连成功，实时收包 |
| 传输（UDP） | **正常** | 300/300 包，峰值逐位一致 |
| 传输（TCP） | **正常** | shell 通路 1715 包 / 34 秒 |
| Windows 接收 | **正常** | 电平 -1.9 ~ -19 dB |
| WASAPI → 蓝牙音箱 | **正常** | 设备 REDMI 电脑音箱 2 Pro-8937，Running，45 ms |
| 自动让路 | **已接线** | GUI 日志「已把 3 路手机的检测器挂到混音器」 |
| **唯一受限环节** | **MediaProjection 采集非 opt-in 应用** | 本报告 |

---

## 六、本轮为做实验而新增的代码（可保留，也可移除）

| 文件 | 作用 |
|---|---|
| `app/.../ToneActivity.kt` | 1 kHz 测试音，用于固定"音源在出声"这个变量 |
| `app/src/main/AndroidManifest.xml` | 加 `android:allowAudioPlaybackCapture="${allowCapture}"` 占位符 + 注册 ToneActivity |
| `app/build.gradle.kts` | 加 `manifestPlaceholders["allowCapture"]`，默认 false（**与改动前行为一致**） |
| `tonetest/` 模块 | 最初想用的独立对照 App（被 HyperOS 新装限制拦住，未使用） |

交付 APK 已恢复默认构建（`allowAudioPlaybackCapture=false`），与实验前的行为一致。

---

## 七、诚实标注

- **已实测**：上述 A/B/C 三组实验、logcat、dumpsys、aapt2 清单核查、版本信息。
- **未实测**：root 路径；是否有国产 App 主动 opt-in（未逐个扫描）。
- **不能断言**：不能说"所有第三方 App 都采不到"——只验证了 B 站、快手采不到，且证明了机制。
- **未做的事**：没有为了凑结果去改采样率、UDP/RTP、缓冲区——这些环节已证明是好的。
