package com.nluemix.sender

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioPlaybackCaptureConfiguration
import android.media.AudioRecord
import android.media.MediaRecorder
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.content.pm.ServiceInfo
import android.os.IBinder
import android.util.Log
import androidx.core.app.NotificationCompat

/**
 * 采集服务。
 *
 * 这是整个安卓端最关键的部分：
 *  - 用 MediaProjection 拿到"录屏"权限（安卓规定，采系统声音必须走这条路）
 *  - 用 AudioPlaybackCapture 抓"别的 App 正在播放"的声音
 *  - 20 ms 一包，PCM16 直传电脑（局域网带宽完全够，而且零损失）
 *
 * 做成前台服务是为了退到后台不被杀。
 */
class CaptureService : Service() {

    companion object {
        const val ACTION_START = "com.nluemix.sender.START"
        const val ACTION_STOP = "com.nluemix.sender.STOP"
        const val EXTRA_USE_MIC = "use_mic"

        /**
         * 音频传输方式。**默认 UDP**（需求指定 RTP/UDP）。
         * 只有显式传 true 才走 TCP —— 保留它仅作为"UDP 被网络环境阻断"时的兼容退路。
         */
        const val EXTRA_USE_TCP = "use_tcp"

        /**
         * 纯音频模式（默认开）。
         *
         * 不创建虚拟显示器：投屏画面会随息屏一起停止，部分 ROM 会因此把整个
         * MediaProjection 会话（含音频）一起收掉。纯音频根本不需要画面，
         * 索性不建虚显，锁屏后音频依然在采。
         */
        const val EXTRA_AUDIO_ONLY = "audio_only"
        private const val CHANNEL_ID = "nluemix_capture"
        private const val NOTIFICATION_ID = 1

        @Volatile
        var isRunning = false
            private set
    }

    private var projection: MediaProjection? = null
    private var virtualDisplay: android.hardware.display.VirtualDisplay? = null
    private var audioRecord: AudioRecord? = null
    private var sender: SenderClient? = null
    private var captureThread: Thread? = null

    @Volatile private var capturing = false
    @Volatile private var useMic = false

    /** 音频是否走 TCP。默认 false = 走 UDP（需求要求）。 */
    @Volatile private var useTcpAudio = false

    /** 纯音频模式：不建虚拟显示器，锁屏也能继续采。 */
    @Volatile private var audioOnlyMode = true

    /** 采集期间的 CPU 唤醒锁。锁屏后没有它，音频会断。 */
    private var wakeLock: android.os.PowerManager.WakeLock? = null

    /** 自动配置的端口是否已经上报过（只报一次，避免刷屏）。 */
    private var autoCfgSentOnce = false
    private var packetsSent = 0L

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        Log.i("NlueMix", "onStartCommand action=" + intent?.action)
        broadcast("STEP", "服务已启动")

        when (intent?.action) {
            ACTION_STOP -> {
                stopEverything("已停止")
                return START_NOT_STICKY
            }
            ACTION_START -> {
                try {
                val host = intent.getStringExtra(MainActivity.EXTRA_HOST) ?: run {
                    stopEverything("没有拿到电脑地址")
                    return START_NOT_STICKY
                }
                val code = intent.getStringExtra(MainActivity.EXTRA_CODE) ?: ""
                useMic = intent.getBooleanExtra(EXTRA_USE_MIC, false)
                // ★ 修复：音频默认走 UDP。缺省 false，只有显式传 EXTRA_USE_TCP=true 才用 TCP。
                useTcpAudio = intent.getBooleanExtra(EXTRA_USE_TCP, false)
                // 纯音频模式默认开
                audioOnlyMode = intent.getBooleanExtra(EXTRA_AUDIO_ONLY, true)
                broadcast("STEP", if (useMic) "麦克风模式" else "系统音频模式")
                val resultCode = intent.getIntExtra(MainActivity.EXTRA_RESULT_CODE, 0)
                @Suppress("DEPRECATION")
                val resultData = intent.getParcelableExtra<Intent>(MainActivity.EXTRA_RESULT_DATA)

                // 麦克风模式不需要录屏授权数据
                if (resultData == null && !useMic) {
                    stopEverything("没有拿到录屏授权数据")
                    return START_NOT_STICKY
                }

                broadcast("STEP", "正在建立前台服务…")
                startForegroundNotification("正在准备…")
                broadcast("STEP", "前台服务已建立")

                startCapture(host, code, resultCode, resultData)
                return START_NOT_STICKY
                } catch (e: Exception) {
                    broadcast("ERROR", "启动失败：" + (e.message ?: e.javaClass.simpleName))
                    Log.e("NlueMix", "启动采集服务失败", e)
                    stopEverything("启动失败")
                    return START_NOT_STICKY
                }
            }
        }
        return START_NOT_STICKY
    }

    // ──────────────────────────────────────────── 采集主流程

    /// <summary>
    /// 启动采集。**整个过程必须在后台线程做**：
    /// Android 禁止在主线程发起网络操作（NetworkOnMainThreadException），
    /// 之前就是在这里直接建 TCP 连接导致服务被杀。
    /// </summary>
    private fun startCapture(host: String, code: String, resultCode: Int, resultData: Intent?) {
        Thread {
            try {
                startCaptureInner(host, code, resultCode, resultData)
            } catch (e: Throwable) {
                Log.e("NlueMix", "启动采集失败", e)
                broadcast("ERROR", "启动失败：" + (e.message ?: e.javaClass.simpleName)
                    + "（" + e.javaClass.simpleName + "）")
                stopEverything("启动失败")
            }
        }.start()
    }

    private fun startCaptureInner(host: String, code: String, resultCode: Int, resultData: Intent?) {
        try {
            val prefs = getSharedPreferences("nluemix", MODE_PRIVATE)
            var deviceId = prefs.getString("deviceId", null)
            if (deviceId == null) {
                deviceId = java.util.UUID.randomUUID().toString()
                prefs.edit().putString("deviceId", deviceId).apply()
            }

            val deviceName = (Build.MANUFACTURER + " " + Build.MODEL).trim()

            // 1) 先连电脑
            broadcast("CONNECTING", "正在连接电脑…")
            updateNotification("正在连接电脑…")

            broadcast("STEP", "正在连接电脑 " + host + " …")

            // ★ 修复：以前这里写死 useTcpAudio = true，注释理由是"UDP 会被 Windows 防火墙拦"。
            //   这个判断是错的：防火墙拦的是"没有放行规则的程序的入站连接"，
            //   而 NlueMix.exe 自身已有放行规则，所以它的入站 UDP 同样被放行。
            //   写死 TCP 的后果是 Windows 端完整的 UDP 接收链路
            //   （NetworkReceiver.ReceiveLoop）从来没有被真实音频触发过。
            broadcast("STEP", "音频传输方式：" + (if (useTcpAudio) "TCP（兼容模式）" else "UDP（默认）"))
            val client = SenderClient(host, code, deviceName, useTcpAudio = useTcpAudio) { state, message ->
                if (state == ConnState.ERROR) {
                    broadcast("ERROR", message)
                    stopEverything(message)
                }
            }
            sender = client

            if (!client.connect(deviceId)) {
                broadcast("ERROR", "连不上电脑，检查地址和连接码")
                stopEverything("连不上电脑")
                return
            }

            // 诊断：打印关键的 appops 状态
            try {
                val pm = packageManager
                val sb = StringBuilder("appops: ")
                for (op in listOf("PROJECT_MEDIA", "RECORD_AUDIO")) {
                    val mode = try {
                        @Suppress("DEPRECATION")
                        pm.checkPermission("android.permission." + op, packageName)
                    } catch (e: Exception) { -999 }
                    sb.append(op).append("=").append(mode).append(" ")
                }
                broadcast("STEP", sb.toString())
            } catch (_: Exception) { }

            if (useMic) {
                // 麦克风模式：不需要 MediaProjection，直接开采集
                broadcast("STEP", "麦克风模式：跳过录屏授权，直接开始采集")
                startAudioRecord(null)
                return
            }

            broadcast("STEP", "已连上电脑，准备申请录屏授权")

            // 2) 拿 MediaProjection（必须在主线程回调，这里用 Handler 兜一下）
            val manager = getSystemService(MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
            val handler = android.os.Handler(mainLooper)
            handler.post {
                try {
                    val mp = manager.getMediaProjection(resultCode, resultData!!)
                    if (mp == null) {
                        broadcast("ERROR", "录屏授权失败")
                        stopEverything("录屏授权失败")
                        return@post
                    }
                    projection = mp
                    mp.registerCallback(object : MediaProjection.Callback() {
                        override fun onStop() {
                            stopEverything("录屏授权被系统收回")
                        }
                    }, handler)

                    // 某些系统（含部分 HyperOS）要求投影必须真的创建了虚拟显示器，
                    // 音频采集才会被激活。这里建一个 1x1 的隐形显示器，几乎不耗资源。
                    try {
                        val vd = mp.createVirtualDisplay(
                            "NlueMixAudioOnly",
                            1, 1, 1,
                            android.hardware.display.DisplayManager.VIRTUAL_DISPLAY_FLAG_PUBLIC,
                            null, null, handler
                        )
                        virtualDisplay = vd
                        broadcast("STEP", "已创建虚拟显示器（激活音频通路）")
                    } catch (e: Throwable) {
                        broadcast("STEP", "创建虚拟显示器失败（不影响音频）：" + e.message)
                    }

                    broadcast("STEP", "已拿到录屏授权，正在创建音频采集器")

                    // 放到后台线程做，避免主线程卡顿或被系统判定超时
                    Thread {
                        try {
                            startAudioRecord(mp)
                        } catch (e: Throwable) {
                            Log.e("NlueMix", "采集启动失败", e)
                            broadcast("ERROR", "采集启动失败：" + (e.message ?: e.javaClass.simpleName)
                                + "（" + e.javaClass.simpleName + "）")
                            stopEverything("采集启动失败")
                        }
                    }.start()
                } catch (e: Exception) {
                    broadcast("ERROR", "启动采集失败：" + (e.message ?: "未知"))
                    stopEverything("启动采集失败")
                }
            }
        } catch (e: Exception) {
            broadcast("ERROR", "启动失败：" + (e.message ?: e.javaClass.simpleName))
            stopEverything("启动失败")
        }
    }

    // ════════════════════════════════════════════ 采集：系统音频（播放捕获）
    //
    // 【★ 曾经的致命 bug，已修 ★】
    //   旧代码的 4 个方案全部是"不加任何 usage"（想让空列表 = 匹配全部）。
    //   但安卓的真实行为不是这样：AudioPlaybackCaptureConfiguration.Builder.build()
    //   会把 usage 列表转成 AudioMixingRule，列表为空时直接抛异常：
    //
    //     java.lang.IllegalArgumentException: Cannot build AudioMixingRule with no rules.
    //         at android.media.audiopolicy.AudioMixingRule$Builder.build(AudioMixingRule.java:729)
    //         at android.media.AudioPlaybackCaptureConfiguration$Builder.build(...:246)
    //         at com.nluemix.sender.CaptureService.makePlaybackRecord(CaptureService.kt:292)
    //
    //   （真机 Redmi K70 Ultra / Android 16 的 logcat 原文，2026-10-11 16:30）
    //   结果：4 个方案在 build() 就全部炸掉，一个采集器都建不起来，
    //   界面只会看到"试了 N 次都建不起音频采集器"。空列表 ≠ 全匹配。
    //
    // 结论：必须显式 addMatchingUsage，而且安卓只接受这 3 个 usage：
    //   USAGE_MEDIA(1) / USAGE_GAME(14) / USAGE_UNKNOWN(0)
    //   传别的会抛 "Unsupported usage"。这里逐个 try 添加，某个 ROM 不认就跳过。

    /** 一种采集方案：采样率 + 要捕获哪些 usage */
    private class Plan(val rate: Int, val usages: IntArray, val label: String)

    private val usageAll = intArrayOf(
        AudioAttributes.USAGE_MEDIA,
        AudioAttributes.USAGE_GAME,
        AudioAttributes.USAGE_UNKNOWN
    )

    /** 有些 ROM 只认 MEDIA，留一个最小方案兜底 */
    private val usageMediaOnly = intArrayOf(AudioAttributes.USAGE_MEDIA)

    private val plans = listOf(
        Plan(48000, usageAll, "48kHz·MEDIA+GAME+UNKNOWN"),
        Plan(44100, usageAll, "44.1kHz·MEDIA+GAME+UNKNOWN"),
        Plan(48000, usageMediaOnly, "48kHz·仅MEDIA")
    )

    /** 最多造几次采集器（防止死循环） */
    private val maxAttempts = plans.size + 2

    /** 按方案造一个 AudioRecord */
    private fun makePlaybackRecord(mp: MediaProjection, plan: Plan): AudioRecord {
        val fmt = AudioFormat.Builder()
            .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
            .setSampleRate(plan.rate)
            .setChannelMask(AudioFormat.CHANNEL_IN_STEREO)
            .build()

        val minBuf = AudioRecord.getMinBufferSize(
            plan.rate, AudioFormat.CHANNEL_IN_STEREO, AudioFormat.ENCODING_PCM_16BIT
        )
        val per20ms = (plan.rate / 50) * Protocol.CHANNELS * 2
        val bufSize = maxOf(if (minBuf > 0) minBuf * 4 else 0, per20ms * 8)

        val cfgBuilder = AudioPlaybackCaptureConfiguration.Builder(mp)

        // ★ 核心修复：至少要加一个 usage，否则下面 build() 必定抛异常
        var added = 0
        for (u in plan.usages) {
            try {
                cfgBuilder.addMatchingUsage(u)
                added++
            } catch (e: Throwable) {
                broadcast("STEP", "  这台手机不接受 usage=" + u + "，跳过它")
            }
        }
        if (added == 0) {
            // 兜底：USAGE_MEDIA 是标准里必定支持的
            cfgBuilder.addMatchingUsage(AudioAttributes.USAGE_MEDIA)
            broadcast("STEP", "  已回退到 USAGE_MEDIA")
        }

        val cfg = cfgBuilder.build()

        return AudioRecord.Builder()
            .setAudioFormat(fmt)
            .setBufferSizeInBytes(bufSize)
            .setAudioPlaybackCaptureConfig(cfg)
            .build()
    }

    /**
     * 诊断：列出此刻真正在播放的 App，以及它们的 usage。
     * 用来区分两种"读出来全是 0"：
     *   A) 播放的 App 主动禁止被录制 / usage 不在允许列表里
     *   B) 系统（某些 HyperOS / MIUI）在应用层把数据换成了静音
     * 如果这里能看到正在播放的 App、而且它的 usage 就是 1(MEDIA=允许被录)，
     * 而我们仍然读到 0，那基本就是 B。
     */
    private fun activePlayersReport(): String {
        return try {
            val am = getSystemService(AUDIO_SERVICE) as android.media.AudioManager
            // 注意：AudioPlaybackConfiguration 只有 getAudioAttributes() 是公开 API，
            // isActive() / getClientUid() 都是 @SystemApi，普通 App 调不到（编译期就报 unresolved）。
            val list = am.activePlaybackConfigurations
            if (list == null || list.isEmpty()) {
                "此刻系统里没有任何 App 在播放声音"
            } else {
                // 逐个播放器把 usage + flags 原样打出来。
                // 关键位：0x400 —— AOSP AudioAttributes.FLAG_NO_MEDIA_PROJECTION。
                // 播放器置了它，就表示"本音频不允许被 MediaProjection 捕获"，
                // AudioPlaybackCapture 会直接拿到静音（不是我们的代码问题）。
                // 同机对比 B站 与对照播放器的 flags，就能定性"全零"到底怪谁。
                val sb = StringBuilder()
                sb.append("系统共报告 ").append(list.size).append(" 个播放器：")
                for (c in list) {
                    val attr = try { c.audioAttributes } catch (e: Throwable) { null }
                    val usage = attr?.usage ?: -1
                    val flags = attr?.flags ?: -1
                    val ct = try { attr?.contentType ?: -1 } catch (e: Throwable) { -1 }
                    sb.append("\n    usage=").append(usage)
                    sb.append(" content=").append(ct)
                    sb.append(" flags=0x").append(Integer.toHexString(flags))
                    if (flags and 0x400 != 0) {
                        sb.append(" ★0x400=FLAG_NO_MEDIA_PROJECTION（该播放器禁止被录屏采集）")
                    } else if (usage == AudioAttributes.USAGE_MEDIA) {
                        sb.append(" （无 0x400，媒体类，理论上可被捕获）")
                    }
                }
                sb.toString()
            }
        } catch (e: Throwable) {
            "诊断不可用（这台系统不给读）：" + (e.message ?: e.javaClass.simpleName)
        }
    }

    /** 需要时才创建虚拟显示器（整个生命周期只建一次） */
    private fun ensureVirtualDisplay(mp: MediaProjection): Boolean {
        // ★ 纯音频模式：刻意【不建】虚拟显示器。
        //   原因：虚显制造的投屏画面会随息屏一起停止，部分 ROM（含 HyperOS）
        //   会把"投影已结束"当成整个 MediaProjection 会话结束，顺手把音频也收掉 ——
        //   表现就是"一锁屏就没声音"。
        //   纯音频不需要画面，不建虚显反而更稳。Android 14+ 只要求
        //   前台服务类型为 mediaProjection 且回调已注册，并不要求必须有虚显。
        if (audioOnlyMode) {
            broadcast("STEP", "纯音频模式：不创建虚拟显示器（锁屏可继续）")
            return true
        }
        if (virtualDisplay != null) return true
        return try {
            virtualDisplay = mp.createVirtualDisplay(
                "NlueMixAudioOnly", 1, 1, 1,
                android.hardware.display.DisplayManager.VIRTUAL_DISPLAY_FLAG_PUBLIC,
                null, null, android.os.Handler(mainLooper)
            )
            broadcast("STEP", "已创建虚拟显示器")
            true
        } catch (e: Throwable) {
            Log.w("NlueMix", "创建虚拟显示器失败", e)
            false
        }
    }

    /**
     * 拿 CPU 唤醒锁。
     *
     * 锁屏后系统会让 CPU 进入休眠，AudioRecord.read() 拿不到数据，
     * 表现就是"亮屏有声、锁屏就断"。PARTIAL_WAKE_LOCK 只保 CPU 不保屏幕，
     * 正好是纯音频模式需要的。
     *
     * 设 6 小时上限是防泄漏兜底：万一某条路径忘了释放，也不会永久占着。
     */
    private fun acquireWakeLock() {
        try {
            if (wakeLock?.isHeld == true) return
            val pm = getSystemService(POWER_SERVICE) as android.os.PowerManager
            wakeLock = pm.newWakeLock(android.os.PowerManager.PARTIAL_WAKE_LOCK, "NlueMix:capture").apply {
                setReferenceCounted(false)
                acquire(6 * 60 * 60 * 1000L)
            }
            broadcast("STEP", "已持有 CPU 唤醒锁（锁屏后可继续采集）")
        } catch (e: Throwable) {
            Log.w("NlueMix", "获取唤醒锁失败（锁屏可能会断流）", e)
        }
    }

    private fun releaseWakeLock() {
        try {
            if (wakeLock?.isHeld == true) wakeLock?.release()
        } catch (_: Throwable) {
        }
        wakeLock = null
    }

    private fun startAudioRecord(mp: MediaProjection?) {
        try {
            if (useMic) { startMicCapture(); return }
            if (mp == null) {
                broadcast("ERROR", "系统音频模式需要录屏授权，但没有拿到")
                stopEverything("缺少录屏授权")
                return
            }
            startAdaptiveCapture(mp)
        } catch (e: Exception) {
            val detail = e.message ?: e.javaClass.simpleName
            Log.e("NlueMix", "启动音频采集失败", e)
            broadcast("ERROR", "采集启动失败：" + detail + "（" + e.javaClass.simpleName + "）")
            stopEverything("采集启动失败")
        }
    }

    // ──────────────────────────────────────────── 麦克风模式（简单路径）

    private fun startMicCapture() {
        broadcast("STEP", "使用【麦克风模式】采集")
        val fmt = AudioFormat.Builder()
            .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
            .setSampleRate(Protocol.SAMPLE_RATE)
            .setChannelMask(AudioFormat.CHANNEL_IN_STEREO)
            .build()
        val per20ms = (Protocol.SAMPLE_RATE / 50) * Protocol.CHANNELS * 2
        val minBuf = AudioRecord.getMinBufferSize(
            Protocol.SAMPLE_RATE, AudioFormat.CHANNEL_IN_STEREO, AudioFormat.ENCODING_PCM_16BIT
        )
        val rec = AudioRecord.Builder()
            .setAudioFormat(fmt)
            .setBufferSizeInBytes(maxOf(if (minBuf > 0) minBuf * 4 else 0, per20ms * 8))
            .setAudioSource(MediaRecorder.AudioSource.MIC)
            .build()

        if (rec.state != AudioRecord.STATE_INITIALIZED) {
            broadcast("ERROR", "麦克风初始化失败（state=" + rec.state + "）")
            stopEverything("麦克风初始化失败")
            return
        }
        audioRecord = rec
        rec.startRecording()
        broadcast("STEP", "麦克风已启动")
        beginSending(Protocol.SAMPLE_RATE)
    }

    /** 麦克风模式的发送循环（采样率固定 48k，无需重采样） */
    private fun beginSending(rate: Int) {
        capturing = true
        isRunning = true
        broadcast("CAPTURING", "麦克风模式")
        updateNotification("正在串流到电脑")

        captureThread = Thread {
            val frameBytes = (rate / 50) * Protocol.CHANNELS * 2
            val buf = ByteArray(frameBytes)
            var local = 0L
            var lastReport = System.currentTimeMillis()
            var peakSeen = 0
            try {
                while (capturing) {
                    val r = audioRecord ?: break
                    val n = r.read(buf, 0, frameBytes)
                    if (n <= 0) {
                        if (n < 0) { broadcast("ERROR", "麦克风返回错误码 " + n); break }
                        Thread.sleep(5); continue
                    }
                    var peak = 0
                    var i = 0
                    while (i < n - 1) {
                        val s = (buf[i + 1].toInt() shl 8) or (buf[i].toInt() and 0xFF)
                        val a = if (s < 0) -s else s
                        if (a > peak) peak = a
                        i += 2
                    }
                    if (peak > peakSeen) peakSeen = peak
                    if (n >= frameBytes) {
                        sender?.sendPcm(buf, frameBytes)
                        local++
                        packetsSent = local
                    }
                    val now = System.currentTimeMillis()
                    if (now - lastReport > 2000) {
                        broadcast("STEP", "已发 " + local + " 包，峰值 " + peakSeen)
                        lastReport = now
                    }
                }
            } catch (e: Throwable) {
                broadcast("ERROR", "麦克风采集异常：" + (e.message ?: e.javaClass.simpleName))
            }
            broadcast("STEP", "麦克风循环已退出（共发 " + local + " 包）")
        }.also { it.start() }
    }

    // ──────────────────────────────────────────── 系统音频：自适应

    /** 当前正在用的方案下标；-1 表示还没锁定 */
    @Volatile private var planIndex = 0
    @Volatile private var planLocked = false

    private fun startAdaptiveCapture(mp: MediaProjection) {
        acquireWakeLock()
        broadcast("STEP", "开始自动匹配采集方式（共 " + plans.size + " 种）"
            + "（" + (if (audioOnlyMode) "纯音频模式" else "含画面模式") + "）")
        planIndex = 0
        planLocked = false
        // Android 14+ 要求投影"真的在用"；建一个 1×1 隐形显示器最稳（失败也不影响音频）
        ensureVirtualDisplay(mp)

        var record: AudioRecord? = null
        var currentPlan: Plan? = null

        // 20 ms 一帧的累积缓冲（按当前方案的采样率算）
        var frameBytes = 0
        var acc = ByteArray(0)
        var accLen = 0
        var tmp = ByteArray(0)

        var silenceMs = 0
        var lastReadN = 0            // AudioRecord.read() 最近一次实际返回值
        var lastReport = System.currentTimeMillis()
        var packetsSentLocal = 0L
        var lastSendMs = System.currentTimeMillis()
        var attemptsLeft = maxAttempts
        var triedAllPlans = false

        val resampler = Resampler()
        var resampleOut = ByteArray(0)

        capturing = true
        isRunning = true
        broadcast("CAPTURING", "正在匹配采集方式…")
        updateNotification("正在串流到电脑")

        captureThread = Thread {
            try {
                while (capturing) {
                    // 1) 需要时创建/切换采集器
                    if (record == null) {
                        // ★ 防死循环：造采集器的次数有上限，超了就报错退出
                        if (attemptsLeft <= 0) {
                            broadcast("ERROR", "试了 " + maxAttempts + " 次都建不起音频采集器。" +
                                "这台手机的音频系统可能不允许 App 采集内部声音。")
                            stopEverything("采集器无法建立")
                            return@Thread
                        }
                        attemptsLeft--

                        val plan = plans[planIndex]
                        broadcast("STEP", "尝试第 " + (planIndex + 1) + "/" + plans.size + " 种：" + plan.label
                            + "（剩余 " + attemptsLeft + " 次）")

                        val r = try { makePlaybackRecord(mp, plan) } catch (e: Throwable) {
                            Log.w("NlueMix", "创建失败: " + plan.label, e)
                            broadcast("STEP", "  创建失败：" + (e.message ?: e.javaClass.simpleName))
                            null
                        }
                        if (r == null || r.state != AudioRecord.STATE_INITIALIZED) {
                            try { r?.release() } catch (_: Exception) {}
                            advancePlan()
                            Thread.sleep(100)     // ← 关键：不要空转
                            continue
                        }
                        try { r.startRecording() } catch (e: Throwable) {
                            Log.w("NlueMix", "startRecording 失败: " + plan.label, e)
                            try { r.release() } catch (_: Exception) {}
                            advancePlan(); continue
                        }
                        broadcast("STEP", "  实际采样率=" + r.sampleRate + " 声道=" + r.channelCount)
                        record = r
                        currentPlan = plan
                        audioRecord = r
                        frameBytes = (plan.rate / 50) * Protocol.CHANNELS * 2
                        acc = ByteArray(frameBytes)
                        accLen = 0
                        tmp = ByteArray(frameBytes)
                        resampler.configure(plan.rate / 50, Protocol.FRAME_SAMPLES)
                        resampleOut = ByteArray(Protocol.FRAME_SAMPLES * Protocol.CHANNELS * 2)
                        silenceMs = 0
                    }

                    val plan = currentPlan
                    if (plan == null) { advancePlan(); continue }
                    val r = record
                    if (r == null) continue

                    // 2) 读数据
                    val want = minOf(tmp.size, frameBytes - accLen)
                    val n = r.read(tmp, 0, want)
                    lastReadN = n
                    if (n <= 0) {
                        if (n < 0) {
                            broadcast("STEP", "采集器错误码 " + n + "，换方案")
                            releaseRecord(record); record = null
                            Thread.sleep(150)
                            continue
                        }
                        Thread.sleep(5)
                        continue
                    }
                    System.arraycopy(tmp, 0, acc, accLen, n)
                    accLen += n
                    if (accLen < frameBytes) continue
                    accLen = 0

                    // 3) 算峰值 + 非零样本数 + RMS
                    //    任务 1 要求能定性"读出来全是 0"，所以这里必须同时给出：
                    //    峰值、非零样本占比、RMS、以及 AudioRecord.read() 的实际返回值。
                    var peak = 0
                    var nonZero = 0
                    var sumSq = 0.0
                    var total = 0
                    var i = 0
                    while (i < frameBytes - 1) {
                        // ★ 修复：高字节必须先 & 0xFF 再左移。
                        //   原写法 acc[i + 1].toInt() shl 8 会先做符号扩展：
                        //   高字节 0x80~0xFF 会被扩展成 0xFFFFFFxx，
                        //   导致负数采样的峰值严重虚高（统计失真）。
                        val lo = acc[i].toInt() and 0xFF
                        val hi = acc[i + 1].toInt() and 0xFF
                        var s = (hi shl 8) or lo
                        if (s >= 0x8000) s -= 0x10000        // 还原为有符号 16 位
                        val a = if (s < 0) -s else s
                        if (a > peak) peak = a
                        if (a != 0) nonZero++
                        sumSq += s.toDouble() * s.toDouble()
                        total++
                        i += 2
                    }
                    val rms = if (total > 0) Math.sqrt(sumSq / total) else 0.0

                    // 4) 收到声音 → 锁定方案
                    if (peak >= 60 && !planLocked) {
                        planLocked = true
                        broadcast("STEP", "✅ 收到声音了！锁定方案：" + plan.label)
                    }

                    // 5) 长时间静音 → 换下一个方案
                    if (peak < 60) {
                        silenceMs += 20

                        // 已锁定但连续 12 秒没声音：可能是换了 App / 换了音频类型，
                        // 解锁并重新开始匹配（保留当前方案，但如果继续静音就会往下切）
                        if (planLocked && silenceMs >= 12000) {
                            planLocked = false
                            silenceMs = 3000
                            broadcast("STEP", "  已锁定但 12 秒无声音，重新匹配采集方式…")
                        }

                        // 首次跑起来后，把自动配置读到的无线调试端口发给电脑（只发一次）。
                        //
                        // 为什么放在这里而不是启动时：这时控制通道已经握手完成、能写了。
                        // 电脑收到后会执行 adb pair/connect，之后就可以切到 shell 路径
                        // 采 B 站这类没声明 allowAudioPlaybackCapture 的应用。
                        if (!autoCfgSentOnce) {
                            autoCfgSentOnce = true
                            try {
                                val prefs = getSharedPreferences("nluemix", MODE_PRIVATE)
                                val ep = prefs.getString("auto_phone_endpoint", null)
                                if (!ep.isNullOrEmpty()) {
                                    val pp = prefs.getString("auto_pair_port", "").orEmpty()
                                    val pc = prefs.getString("auto_pair_code", "").orEmpty()
                                    sender?.sendAutoCfg(ep, pp, pc)
                                    broadcast("STEP", "已把无线调试端口 " + ep + " 发给电脑，电脑会自动连接")
                                }
                            } catch (e: Exception) {
                                Log.w("NlueMix", "上报自动配置信息失败", e)
                            }
                        }

                        // 静音期间每 6 秒把"谁在播放、它的 flags 是什么"打一次，
                        // 这样即便采集为 0，日志里也能直接看到根因证据。
                        if (silenceMs > 0 && silenceMs % 6000 == 0) {
                            val diag = activePlayersReport()
                            broadcast("STEP", "  诊断·当前在播放的 App → " + diag)
                            // 同时通过控制通道发给电脑：没有 adb 时电脑端也能拿到设备真值
                            sender?.sendDiag(diag)
                        }

                        if (!planLocked && !triedAllPlans && silenceMs >= 3000) {
                            if (planIndex >= plans.size - 1) {
                                // 所有方案都试过了：不再来回切（切来切去反而永远收不到），
                                // 停在当前方案守着，并把诊断信息打出来。
                                triedAllPlans = true
                                broadcast("STEP", "  所有采集方案都读不到声音，停在【" + plan.label + "】继续守候")
                                broadcast("STEP", "  诊断·当前在播放的 App → " + activePlayersReport())
                            } else {
                                broadcast("STEP", "  静音 3 秒，换下一种…")
                                releaseRecord(record)
                                record = null
                                advancePlan()
                                Thread.sleep(150)
                                continue
                            }
                        }
                    } else {
                        silenceMs = 0
                    }

                    // 6) 发出（必要时重采样到 48k）
                    val now = System.currentTimeMillis()
                    if (peak >= 40 || packetsSentLocal < 25) {
                        try {
                            if (plan.rate == Protocol.SAMPLE_RATE) {
                                sender?.sendPcm(acc, frameBytes)
                            } else {
                                val outLen = resampler.resample(acc, resampleOut)
                                if (outLen > 0) sender?.sendPcm(resampleOut, outLen)
                            }
                        } catch (e: Throwable) {
                            Log.w("NlueMix", "发送这一帧失败（已跳过）", e)
                        }
                        packetsSentLocal++
                        packetsSent = packetsSentLocal
                        lastSendMs = now
                    } else if (now - lastSendMs >= 500) {
                        // ★ 静音保活：手机没声音时也要让电脑端知道"人还在"。
                        //   否则电脑端 6 秒判离线、8 秒断 TCP，用户放个暂停回来就得重连。
                        sender?.sendKeepAlive()
                        lastSendMs = now
                    }

                    if (now - lastReport > 2000) {
                        // 诊断行：任务 1 定性"全零"所需的全部原始数据
                        broadcast("STEP", "已发 " + packetsSentLocal + " 包"
                            + "，峰值 " + peak
                            + "，非零 " + nonZero + "/" + total
                            + "(" + (if (total > 0) nonZero * 100 / total else 0) + "%)"
                            + "，RMS " + String.format(java.util.Locale.US, "%.1f", rms)
                            + "，read 返回 " + lastReadN
                            + "，格式 " + plan.rate + "Hz/" + (r.state) )
                        broadcast("STEP", "  方案 " + plan.label + (if (planLocked) "（已锁定）" else ""))
                        // 同时把采集质量上报电脑：没有 adb 也能看到手机端究竟采到了什么
                        sender?.sendDiag("采集 峰值=" + peak
                            + " 非零=" + nonZero + "/" + total
                            + "(" + (if (total > 0) nonZero * 100 / total else 0) + "%)"
                            + " RMS=" + String.format(java.util.Locale.US, "%.1f", rms)
                            + " read=" + lastReadN
                            + " 方案=" + plan.label)
                        lastReport = now
                    }
                }
            } catch (e: Throwable) {
                Log.e("NlueMix", "采集循环异常", e)
                broadcast("ERROR", "采集循环异常：" + (e.message ?: e.javaClass.simpleName))
                stopEverything("采集循环异常")
            } finally {
                releaseRecord(record)
            }

            if (!planLocked) {
                broadcast("ERROR", "所有 " + plans.size + " 种采集方式都收不到声音。" +
                    "请确认：1) 手机上确实在播放声音  2) 播放的 App 没有禁止被录制")
            }
            broadcast("STEP", "采集循环已退出")
        }.also { it.start() }
    }

    private fun advancePlan() {
        if (planIndex < plans.size - 1) planIndex++ else planIndex = 0
    }

    private fun releaseRecord(r: AudioRecord?) {
        try { r?.stop() } catch (_: Exception) {}
        try { r?.release() } catch (_: Exception) {}
        if (audioRecord === r) audioRecord = null
    }

    /**
     * 线性插值重采样器：把 srcFrames 个采样重采样成 dstFrames 个。
     * 只在采集采样率和协议不一致时用到（比如 44100 → 48000）。
     * 插值系数是固定的，所以预先算好，每帧 O(n) 且没有除法。
     */
    private class Resampler {
        private var idx = IntArray(0)
        private var frac = FloatArray(0)
        private var dstFrames = 0
        private var srcFrames = 0

        fun configure(src: Int, dst: Int) {
            if (srcFrames == src && dstFrames == dst && idx.size == dst) return
            srcFrames = src
            dstFrames = dst
            idx = IntArray(dst)
            frac = FloatArray(dst)
            val ratio = if (dst > 1) (src - 1).toDouble() / (dst - 1).toDouble() else 0.0
            for (i in 0 until dst) {
                var i0 = (i * ratio).toInt()
                if (i0 < 0) i0 = 0
                if (i0 > src - 1) i0 = src - 1
                idx[i] = i0
                frac[i] = (i * ratio - i0).toFloat()
            }
        }

        /** 返回写入 out 的字节数。全程做边界保护，绝不越界。 */
        fun resample(src: ByteArray, out: ByteArray): Int {
            if (dstFrames == 0 || srcFrames == 0) return 0
            val maxSrc = srcFrames - 1
            val srcBytes = srcFrames * 4
            if (src.size < srcBytes) return 0

            for (i in 0 until dstFrames) {
                var a = idx[i]
                if (a < 0) a = 0
                if (a > maxSrc) a = maxSrc
                var b = a + 1
                if (b > maxSrc) b = maxSrc          // ← 关键修复：上限是【源帧数-1】，不是目标帧数-1
                val f = frac[i]
                val oa = a * 4
                val ob = b * 4
                for (ch in 0 until Protocol.CHANNELS) {
                    val ca = oa + ch * 2
                    val cb = ob + ch * 2
                    val sa = ((src[ca + 1].toInt() shl 8) or (src[ca].toInt() and 0xFF)).toShort().toInt()
                    val sb = ((src[cb + 1].toInt() shl 8) or (src[cb].toInt() and 0xFF)).toShort().toInt()
                    val v = (sa + (sb - sa) * f).toInt().coerceIn(-32768, 32767)
                    val o = i * 4 + ch * 2
                    out[o] = (v and 0xFF).toByte()
                    out[o + 1] = ((v shr 8) and 0xFF).toByte()
                }
            }
            return dstFrames * Protocol.CHANNELS * 2
        }
    }

    // ──────────────────────────────────────────── 停止

    private fun stopEverything(message: String) {
        capturing = false
        isRunning = false

        // 注意：本函数也可能由采集线程自己调用，那种情况下 join 自己会白等 800 ms
        if (Thread.currentThread() !== captureThread) {
            try { captureThread?.join(800) } catch (_: Exception) { }
        }
        captureThread = null

        try { audioRecord?.stop() } catch (_: Exception) { }
        try { audioRecord?.release() } catch (_: Exception) { }
        audioRecord = null

        try { virtualDisplay?.release() } catch (_: Exception) { }
        virtualDisplay = null
        try { projection?.stop() } catch (_: Exception) { }
        projection = null

        try { sender?.close() } catch (_: Exception) { }
        sender = null

        // 唤醒锁必须在这里释放：这是所有停止路径的唯一出口
        releaseWakeLock()

        broadcast("IDLE", message)

        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    override fun onDestroy() {
        super.onDestroy()
        if (isRunning) stopEverything("服务被销毁")
    }

    // ──────────────────────────────────────────── 通知 & 广播

    private fun startForegroundNotification(text: String) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(
                CHANNEL_ID, "NlueMix 串流", NotificationManager.IMPORTANCE_LOW
            ).apply { description = "正在把手机声音发送到电脑" }
            val manager = getSystemService(NotificationManager::class.java)
            manager.createNotificationChannel(channel)
        }
        // Android 10+ 必须声明前台服务类型；
        // Android 14+ 如果这里不传类型，会直接抛 MissingForegroundServiceTypeException 把服务杀掉 ——
        // 这就是"一选完共享屏幕就停止"的真正原因。
        try {
            // ★ 修复：前台服务类型必须和"实际在干什么"一致。
            //   Android 14 起，声明 mediaProjection 类型却没有有效的录屏授权时，
            //   startForeground() 会抛 SecurityException，服务进不了前台 → 麦克风模式也会挂。
            //   所以麦克风模式用 microphone 类型（清单里两个类型都声明了）。
            if (Build.VERSION.SDK_INT >= 30) {
                val type = if (useMic) {
                    ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE
                } else {
                    ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION
                }
                startForeground(NOTIFICATION_ID, buildNotification(text), type)
            } else if (Build.VERSION.SDK_INT >= 29 && !useMic) {
                startForeground(NOTIFICATION_ID, buildNotification(text), ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION)
            } else {
                startForeground(NOTIFICATION_ID, buildNotification(text))
            }
        } catch (e: Throwable) {
            Log.e("NlueMix", "startForeground 失败", e)
            broadcast("ERROR", "前台服务启动失败：" + (e.message ?: e.javaClass.simpleName)
                + "（" + e.javaClass.simpleName + "）")
            throw e
        }
    }

    private fun updateNotification(text: String) {
        try {
            val manager = getSystemService(NotificationManager::class.java)
            manager.notify(NOTIFICATION_ID, buildNotification(text))
        } catch (_: Exception) { }
    }

    private fun buildNotification(text: String): Notification {
        val openIntent = PendingIntent.getActivity(
            this, 0,
            Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )

        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle("NlueMix")
            .setContentText(text)
            .setSmallIcon(android.R.drawable.ic_media_play)
            .setContentIntent(openIntent)
            .setOngoing(true)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .build()
    }

    private fun broadcast(state: String, message: String) {
        // 同时写一份到 logcat：界面上的日志区在手机上不好复制，
        // 出问题时用 "adb logcat -s NlueMix" 就能拿到全部诊断信息。
        try { Log.i("NlueMix", state + ": " + message) } catch (_: Exception) { }
        try {
            sendBroadcast(Intent(MainActivity.ACTION_STATE).apply {
                setPackage(packageName)
                putExtra("state", state)
                putExtra("message", message)
            })
        } catch (_: Exception) { }
    }
}
