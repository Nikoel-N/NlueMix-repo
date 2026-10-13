# NlueMix · 蓝牙串流混音器

> 一台电脑，把多路声音汇总后，从**同一个蓝牙音响**放出来。
> 朋友用手机放歌时，电脑自动让路；朋友放完，电脑声音自动回来。

---

## 这是什么

蓝牙音响绝大多数**只能同时连一台设备**（这是音响芯片的限制，任何软件都改不了）。
NlueMix 的思路是：**让电脑当中间人**。

```
  手机 1 ──Wi-Fi──┐
  手机 2 ──Wi-Fi──┼──> 电脑（混音 + 自动让路）──蓝牙──> 音响
  手机 3 ──Wi-Fi──┘
  电脑自己的声音 ──┘
```

音响那边**始终只有一条蓝牙连接**，但你能听到最多 3 台手机 + 电脑的声音。

## 核心功能

| 功能 | 说明 |
|---|---|
| **自动让路** | 手机一出声，电脑自动淡出静音（0.1 秒淡出，不会「啪」一声）；手机停 2 秒，电脑自动恢复 |
| **三种模式** | 自动让路 / 自动压低到 20% / 一起放 / 纯手动 |
| **最多 3 路手机** | 每路独立音量、独立静音 |
| **音质优先** | 局域网走 48 kHz 立体声 PCM（**无损**），音质损失只剩蓝牙那一次编码 |
| **免驱动** | 不装虚拟声卡、不改系统设置，卸载不留痕 |
| **八国语言** | 简体中文 / English / 日本語 / 한국어 / Deutsch / Français / Español / Русский，切换即时生效，不用重启 |
| **手机采不到声音也能救** | 部分 App（B 站等）不允许被录制，走 `🔌 采集手机声音` 的 shell 路径照样能采到 |

## 下载与安装

发布页（Releases）里这几个文件。文件名一律用 ASCII，不用中文名 —— 中文名在命令行下载、CI 引用、部分下载工具里都会出问题。



| 文件 | 给谁 | 说明 |
|---|---|---|
| `NlueMix-0.2.0-Windows-Setup.exe` | **普通用户，推荐** | 双击即可。装完自动建桌面 / 开始菜单快捷方式，并放行防火墙 |
| `NlueMix-0.2.0-Windows-User.zip` | 不想用安装程序的人 | 解压后双击 `NlueMix.exe` |
| `NlueMix-0.2.0-Windows-Dev.zip` | 排查问题的人 | 界面上多出诊断入口，音频行为与用户版完全一致 |
| `NlueMix-0.2.0-Android-User.apk` | 安卓手机 | 用户版 / 开发者版同理 |
| `NlueMix-0.2.0-Source.zip` | 想看代码 / 自己编译 | 与仓库内容一致 |

**环境要求**

- 电脑端：Windows 10 版本 2004（内部版本 19041）或更高 / Windows 11，需要 .NET 8 桌面运行时。
  安装程序是**自包含单文件**，用它就不必另外装运行时。
- 手机端：安卓 10 以上。

**安装程序做了什么**

1. 释放程序文件到 `%LOCALAPPDATA%\Programs\NlueMix`
2. 建桌面和开始菜单快捷方式
3. 放行 Windows 防火墙（不弹窗、不改其他规则）

卸载走「设置 → 应用」，或在安装目录里运行 `uninstall.ps1`。

> **用户版和开发者版只差界面上的排查入口，采集、协议、传输完全一致。**
> 这一点是刻意的：如果两个版本的音频行为不一样，「用户版出问题、开发者版正常」
> 会让排查彻底失去参照。
> 电脑端还可以用 `NlueMix.exe --dev`（或环境变量 `NLUEMIX_DEV=1`）临时切换，不用换包。

## 快速开始

### 电脑端

1. 先让 Windows 和你的蓝牙音响**配对一次**（设置 → 蓝牙和其他设备）
2. 双击 `NlueMix.exe`
3. 软件会自动找到音响并接管声音。界面右上角显示**连接码**（4 位数字）

```
NlueMix.exe --selftest     检查环境（不开界面，打印诊断信息）
```

### 手机端

装 `NlueMix-0.2.0-Android-User.apk` → 打开 → 输入电脑上显示的 4 位连接码 → 开始

> 安卓第一次会弹「是否允许录制音频」——**必须允许**，这是系统的隐私保护，不给权限就抓不到声音。
> 极少数 App（Netflix 等有版权保护的）抓不到声音，这是安卓的限制。

## 让路逻辑怎么工作的

手机音频流进来后，软件会实时算它的音量（RMS + 峰值双阈值）：

- 连续 **320 ms** 超过 -42 dB → 判定「手机在放」，电脑声音开始淡出
- 连续 **1800 ms** 低于 -50 dB → 判定「放完了」，电脑声音淡入恢复

阈值做了**迟滞**设计（判断有声和无声的门槛不一样），避免在临界点来回抖。

## 系统要求

- Windows 10 版本 2004（内部版本 19041）或更高 / Windows 11
- 蓝牙 4.0 以上适配器（音响支持 A2DP 即可）
- 电脑和手机在**同一个 Wi-Fi** 下

## 架构

```
src/NlueMix/
├── Audio/
│   ├── AudioEngine.cs        引擎总控（启动/停止/设备热插拔）
│   ├── SystemSource.cs       WASAPI 环回采集本机声音
│   ├── Mixer.cs              混音 + 让路 + 软限幅
│   ├── AudioDevices.cs       播放设备枚举与识别
│   ├── RingSampleProvider.cs 环形缓冲 → NAudio 适配
│   └── NativeThread.cs       MMCSS 音频线程优先级
├── Bluetooth/
│   ├── BluetoothScanner.cs   从配对数据库识别音响
│   └── BluetoothConnector.cs 调用系统连接已配对设备
├── Core/
│   ├── AudioRing.cs          无锁环缓冲
│   ├── SilenceDetector.cs    有声/无声检测（迟滞）
│   ├── GainSmoother.cs       增益平滑（防爆音）
│   ├── Dsp.cs                重采样/声道转换/PCM 转换
│   ├── Models.cs             数据模型
│   └── SettingsStore.cs      设置持久化
├── Network/
│   ├── Protocol.cs           串流协议定义
│   ├── NetworkReceiver.cs    TCP 握手 + UDP 收流
│   ├── PhoneSession.cs       单台手机的会话
│   └── OpusCodec.cs          Opus 编解码（备用）
└── UI/
    ├── MainWindow.xaml       界面
    ├── MainWindow.xaml.cs    界面逻辑
    └── Theme.xaml            配色与控件样式
```

## 通信协议（v1）

两条通道，各司其职：

| 通道 | 端口 | 作用 |
|---|---|---|
| TCP | 47820 | 握手、心跳、断线感知（可靠，但不传音频） |
| UDP | 47821 | 音频数据（不可靠，但低延迟；丢几个包听不出来） |

**为什么音频走 UDP 不走 TCP？**
TCP 遇到丢包会重传，重传会让后面已经到达的数据**排队等着**（队头阻塞），
结果是「卡一下 + 延迟越积越多」。音频宁可丢一个 20 ms 的片段，也不要整体延迟。

### UDP 包头（16 字节，小端）

```
偏移  长度  含义
 0     4   魔术字 0x4D424C55
 4     1   协议版本
 5     1   编码方式（0=PCM16, 1=Opus）
 6     1   标志位
 7     1   保留
 8     4   序号（用来算丢包率）
12     2   本包采样数
14     2   保留
```

后面跟的是 16-bit 小端 PCM。20 ms 一包 = 1920 个采样（48 kHz 立体声）。

## 安全设计

NlueMix 是局域网工具，连接码只有 4 位（1 万种组合）。为此做了三道防线：

| 防线 | 机制 | 效果 |
|---|---|---|
| **① 握手限流** | 同一 IP 错 5 次 → 锁 60 秒；锁定期内**正确的码也进不来** | 暴力破解从"几秒"变成"几百年" |
| **② UDP 源校验** | 音频包必须来自跟 TCP 握手同一个 IP | 堵掉"抢先发包占位"的抢注漏洞 |
| **③ 包严格校验** | 魔术字 / 版本 / 编码 / 采样数 / 长度 五重检查 | 畸形包在进入音频线程**之前**就被丢弃 |

另外还有：TCP 并发连接上限（16）、控制行长度上限（512 字节）、UDP 单包上限（8 KB）、
日志节流（畸形包每 30 秒最多汇报一次，不让日志变成攻击者的放大器）。

**仍未加密**：音频在局域网里是明文 PCM。同一 Wi-Fi 下会用抓包工具的人可以还原音频内容。
自己家的网络没问题；**公共网络请谨慎**（不过多数公共 Wi-Fi 开了客户端隔离，那时 NlueMix 本来也用不了）。

## 已知限制

- **蓝牙那一段的音质由硬件决定**，软件改不了。Windows 允许选编码器（SBC/AAC/aptX），取决于网卡和音响
- **延迟约 200-400 ms**（蓝牙本身的延迟），不适合打游戏或 KTV 麦克风
- **Windows 没有开放「程序内发起配对」的 API**，首次配对必须走系统设置页
- 手机端目前只支持**安卓**（iOS 不开放系统音频采集）

## 配置文件

`%LOCALAPPDATA%\NlueMix\settings.json`（写不进去时退到程序目录）

日志：同目录下的 `nluemix.log`

## 版本说明

### 0.2.0

- **手机端新增 shell 采集路径**（工具栏 `🔌 采集手机声音`）。B 站这类应用没有声明
  `allowAudioPlaybackCapture`，安卓不允许被录制，走 App 自己的采集路径拿不到声音 ——
  这是平台限制，不是 bug。shell 路径绕开它，实测峰值 32768 满幅、非零样本 97–99%
- **界面八国语言**，切换即时生效，不用重启
- **反馈入口**（右上角 `✉`）：邮箱可点、日志路径一键打开，报告问题时不用手抄路径
- 深浅主题切换（右上角 ☀/🌙）、开屏动画
- **防火墙修复**（工具栏 `🛡`）。Windows 防火墙规则按「程序路径 + 网络配置」匹配，
  网络配置一变（换路由器、系统重新判定网络类型）规则就失效，
  现象是**手机说连不上、电脑端日志里连一条连接尝试都没有** —— 用户无从判断，所以做成一键修
- **安全加固**：握手限流（同 IP 错 5 次锁 60 秒）、UDP 源校验、包头五重校验、
  并发连接与行长上限、畸形包日志节流

### 已知问题

- 蓝牙那一段的延迟和音质由硬件决定，软件改不了
- 极少数应用（有版权保护的流媒体）连 shell 路径也采不到，属平台限制
- 界面文案已全部支持八种语言；音频格式那一行里的「设备原生采样率」括号说明
  只在设备原生采样率不是 48 kHz 时出现，目前仍只有中文

## 开源协议

MIT License — 随便用、随便改、随便商用，保留版权声明即可。

### 第三方依赖

| 组件 | 协议 | 用途 |
|---|---|---|
| NAudio | MIT | WASAPI 采集与输出 |
| Concentus | MIT | 纯托管 Opus 编解码 |
| .NET 8 | MIT | 运行时 |

本项目**不包含任何广告、推广、遥测、后台上传**。
不做网络请求（除了局域网内和手机的通信）。

一台音响，所有人的声音。 蓝牙音响同时只认一台设备。NlueMix 让房间里每台手机的声音，都从电脑的这条连接出去。

自己让路 — 手机一出声，电脑自动安静。停两秒，声音自己回来。全程不用碰音量。 三台手机同时在线 — 每台独立音量。混着放，或者轮流来。 到电脑这一段是无损的 — 48 kHz 立体声走局域网。全链路只剩蓝牙那一次编码。 安静的设计 — 无广告、无账号、无遥测。数据不出你的局域网。

Windows 10、Android 10、同一个 Wi-Fi。就这些。

English
One speaker. Every voice. A Bluetooth speaker only remembers one device. NlueMix routes every phone in the room through your PC to that one connection.

It steps aside on its own — A phone starts playing and the PC goes quiet. Two seconds of silence, and it's back. You never touch a slider. Three phones at once — Each with its own volume. Mix them, or take turns. Lossless as far as your PC — 48 kHz stereo over Wi-Fi. The only compression left is the one Bluetooth already does. Quiet by design — No ads. No accounts. No telemetry. Nothing leaves your network.

Windows 10. Android 10. The same Wi-Fi. That's all it takes.

日本語
スピーカーはひとつ。みんなの音を。 Bluetooth スピーカーが同時につなげるのは 1 台だけ。NlueMix は PC を介して、部屋にあるスマホの音をすべてその 1 本の接続へ届けます。

鳴ったほうへ、自分でゆずる — スマホが鳴れば PC は静かに。2 秒の静けさのあと、自然に戻ります。音量つまみに触れる必要はありません。 スマホ 3 台まで同時に — それぞれ別の音量で。混ぜても、順番でも。 PC までは無劣化 — 48 kHz ステレオを Wi-Fi でそのまま。圧縮は Bluetooth の 1 回だけ。 静かな設計 — 広告も、アカウントも、テレメトリーもありません。データは LAN の外へ出ません。

Windows 10 以降、Android 10 以降、同じ Wi-Fi。それだけで動きます。

한국어
스피커는 하나. 모두의 소리를. 블루투스 스피커는 한 번에 한 기기만 기억합니다. NlueMix는 방 안 모든 휴대폰의 소리를 PC를 거쳐 그 하나의 연결로 보냅니다.

소리가 나면, 알아서 비켜줍니다 — 휴대폰이 소리를 내면 PC는 잠잠해지고, 2초 뒤 다시 돌아옵니다. 볼륨을 만질 일이 없습니다. 휴대폰 3대까지 동시에 — 각각 따로 볼륨을 조절합니다. 섞어도, 번갈아도. PC까지는 무손실 — 48 kHz 스테레오를 Wi-Fi로 그대로. 남는 압축은 블루투스 한 번뿐입니다. 조용한 설계 — 광고 없음, 계정 없음, 텔레메트리 없음. 데이터는 공유기 밖으로 나가지 않습니다.

Windows 10 이상, Android 10 이상, 같은 Wi-Fi. 그게 전부입니다.

Deutsch
Ein Lautsprecher. Alle Stimmen. Ein Bluetooth-Lautsprecher kennt immer nur ein Gerät. NlueMix schickt den Ton jedes Handys im Raum über den PC durch genau diese eine Verbindung.

Macht von selbst Platz — Ein Handy klingt auf, der PC wird still. Zwei Sekunden Ruhe, und er ist zurück. Kein Regler, den man anfassen muss. Drei Handys gleichzeitig — Jedes mit eigener Lautstärke. Gemischt oder nacheinander. Bis zum PC verlustfrei — 48 kHz Stereo über WLAN. Komprimiert wird erst wieder auf dem letzten Meter. Still mit Absicht — Keine Werbung. Keine Konten. Keine Telemetrie. Nichts verlässt Ihr Netzwerk.

Windows 10, Android 10, dasselbe WLAN. Mehr braucht es nicht.

Français
Une enceinte. Toutes les voix. Une enceinte Bluetooth ne retient qu'un appareil à la fois. NlueMix fait passer le son de chaque téléphone de la pièce par votre PC, sur cette seule connexion.

Elle s'efface d'elle-même — Un téléphone se met à jouer, le PC se tait. Deux secondes de silence, et il revient. Aucun réglage à toucher. Trois téléphones à la fois — Chacun son volume. À mélanger ou à tour de rôle. Sans perte jusqu'au PC — 48 kHz stéréo en Wi-Fi. La seule compression qui reste est celle du Bluetooth. Silencieux par choix — Pas de publicité. Pas de compte. Pas de télémétrie. Rien ne quitte votre réseau.

Windows 10, Android 10, le même Wi-Fi. Il n'en faut pas plus.

Español
Un altavoz. Todas las voces. Un altavoz Bluetooth solo recuerda un dispositivo. NlueMix lleva el sonido de todos los móviles de la sala a esa única conexión a través del PC.

Se aparta solo — Suena un móvil y el PC se calla. Dos segundos de silencio y vuelve. Sin tocar ningún mando. Tres móviles a la vez — Cada uno con su volumen. Mezclados o por turnos. Sin pérdida hasta el PC — 48 kHz estéreo por Wi-Fi. La única compresión que queda es la del Bluetooth. Silencio por diseño — Sin anuncios. Sin cuentas. Sin telemetría. Nada sale de tu red.

Windows 10, Android 10 y la misma Wi-Fi. No hace falta nada más.

Русский
Одна колонка. Все голоса. Колонка Bluetooth помнит только одно устройство. NlueMix передаёт на неё звук всех телефонов в комнате — через компьютер, по тому самому единственному соединению.

Уступает сам — Телефон зазвучал — компьютер умолкает. Две секунды тишины, и звук возвращается. Регуляторы трогать не нужно. Три телефона одновременно — У каждого своя громкость. Можно вместе или по очереди. До компьютера — без потерь — Стерео 48 кГц по Wi-Fi. Единственное сжатие — то, что делает Bluetooth. Тишина по замыслу — Без рекламы. Без аккаунтов. Без телеметрии. Ничего не выходит за пределы вашей сети.

Windows 10, Android 10 и один общий Wi-Fi. Больше ничего не нужно.
