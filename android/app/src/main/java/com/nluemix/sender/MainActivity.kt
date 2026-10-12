package com.nluemix.sender

import android.Manifest
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Bundle
import android.text.InputFilter
import android.view.View
import android.widget.Button
import android.widget.EditText
import android.widget.TextView
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import java.util.UUID

/**
 * NlueMix 发送端主界面。
 *
 * 设计原则：屏幕上只有「电脑地址」和「4 位连接码」两个输入框 + 一个大按钮。
 * 点开始 → 先连电脑确认连接码对不对 → 再请求录屏授权（安卓硬性要求）→ 开始串流。
 */
class MainActivity : AppCompatActivity() {

    companion object {
        const val EXTRA_RESULT_CODE = "result_code"
        const val EXTRA_RESULT_DATA = "result_data"
        const val EXTRA_HOST = "host"
        const val EXTRA_CODE = "code"
        const val ACTION_STATE = "com.nluemix.sender.STATE"
    }

    private lateinit var hostInput: EditText
    private lateinit var codeInput: EditText
    private lateinit var startButton: Button
    private lateinit var statusText: TextView
    private lateinit var hintText: TextView
    private lateinit var micSwitch: android.widget.CheckBox

    private var pairing = false

    private val projectionLauncher = registerForActivityResult(
        ActivityResultContracts.StartActivityForResult()
    ) { result ->
        val data = result.data
        if (result.resultCode != RESULT_OK || data == null) {
            setStatus("你取消了录屏授权。安卓规定必须授权才能采集手机声音。")
            resetButton()
            return@registerForActivityResult
        }
        startCaptureService(result.resultCode, data)
    }

    private val notificationPermissionLauncher = registerForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { }

    // 录音权限：AudioPlaybackCapture 在 Android 12+ 需要它
    private val recordAudioPermissionLauncher = registerForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { granted ->
        if (granted) {
            if (micSwitch.isChecked) {
                setStatus("权限已给，麦克风模式直接开始")
                startCaptureService(0, android.content.Intent())
            } else {
                setStatus("权限已给，正在请求录屏授权…")
                requestProjection()
            }
        } else {
            setStatus("没有录音权限，采不到声音。请到系统设置里给 NlueMix 开「录音」权限。")
            resetButton()
        }
    }

    private val stateReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context?, intent: Intent?) {
            val state = intent?.getStringExtra("state") ?: return
            val message = intent.getStringExtra("message") ?: ""

            // 把每一步都记到界面下方的日志区，方便定位卡在哪
            try {
                val log = findViewById<TextView>(R.id.logText)
                if (log != null) {
                    log.visibility = View.VISIBLE
                    val time = java.text.SimpleDateFormat("HH:mm:ss", java.util.Locale.getDefault()).format(java.util.Date())
                    log.text = ("[" + time + "] " + state + ": " + message + "\n" + log.text).take(2000)
                }
            } catch (_: Exception) { }

            when (state) {
                "STEP" -> {
                    setStatus("进行中 · " + message)
                }
                "CAPTURING" -> {
                    setStatus("正在串流 · " + message)
                    startButton.text = "停止"
                    startButton.isEnabled = true
                    hintText.text = "可以按 Home 键退到后台，声音会继续发送。"
                }
                "ERROR" -> {
                    setStatus("出错了：" + message)
                    resetButton()
                }
                "IDLE" -> {
                    setStatus("已停止")
                    resetButton()
                }
            }
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // ── 开屏风险提示（前置闸门）──
        // 位置很关键：必须在 setContentView 之前。用户没同意就直接把界面让给提示页并结束自己，
        // 不会出现"主界面已经开起来、采集可能已经在跑"的情况。
        //
        // 用闸门而不是弹窗，是因为弹窗下面还看得见主界面，容易让人以为软件已经开始工作。
        if (!RiskNotice.shouldSkip(this)) {
            startActivity(Intent(this, RiskNoticeActivity::class.java))
            finish()
            return
        }

        setContentView(R.layout.activity_main)

        hostInput = findViewById(R.id.hostInput)
        codeInput = findViewById(R.id.codeInput)
        startButton = findViewById(R.id.startButton)
        statusText = findViewById(R.id.statusText)
        hintText = findViewById(R.id.hintText)
        micSwitch = findViewById(R.id.micSwitch)

        // 自动配置入口：走无障碍自动开无线调试、读端口
        findViewById<Button>(R.id.autoConfigButton).setOnClickListener {
            startActivity(Intent(this, AutoConfigActivity::class.java))
        }

        installCrashLogger()

        codeInput.filters = arrayOf(InputFilter.LengthFilter(4))

        val prefs = getSharedPreferences("nluemix", MODE_PRIVATE)
        hostInput.setText(prefs.getString("host", ""))
        micSwitch.isChecked = prefs.getBoolean("use_mic", false)

        startButton.setOnClickListener { onStartStopClicked() }

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            if (ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS)
                != PackageManager.PERMISSION_GRANTED
            ) {
                notificationPermissionLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
            }
        }

        setStatus("填入电脑上 NlueMix 显示的地址和连接码")

        // 调试用（仅 DEBUG 包）：允许 adb 直接带着地址/连接码启动并自动开播，
        // 方便在无法做点击注入的设备上跑自动化验证。
        //   adb shell am start -n com.nluemix.sender/.MainActivity \
        //       --ez autostart true --es host 192.168.1.8 --es code 1234
        // 正式包（Release）里 BuildConfig.DEBUG 为 false，这段完全不会执行。
        val debuggable = (applicationInfo.flags and
            android.content.pm.ApplicationInfo.FLAG_DEBUGGABLE) != 0
        if (debuggable && intent?.getBooleanExtra("autostart", false) == true) {
            intent.getStringExtra("host")?.let { if (it.isNotEmpty()) hostInput.setText(it) }
            intent.getStringExtra("code")?.let { if (it.isNotEmpty()) codeInput.setText(it) }
            startButton.postDelayed({ onStartStopClicked() }, 1200)
        }
    }

    /// <summary>
    /// 崩溃时把堆栈写到文件 + 弹提示，方便定位问题（普通用户也能看到原因）。
    /// </summary>
    private fun installCrashLogger() {
        val prev = Thread.getDefaultUncaughtExceptionHandler()
        Thread.setDefaultUncaughtExceptionHandler { t, e ->
            try {
                val sw = java.io.StringWriter()
                e.printStackTrace(java.io.PrintWriter(sw))
                val f = java.io.File(filesDir, "crash.txt")
                f.writeText("时间: " + java.util.Date().toString() + "\n" +
                    "线程: " + t.name + "\n\n" + sw.toString())
            } catch (_: Exception) { }
            try {
                val text = e.javaClass.simpleName + ": " + (e.message ?: "")
                android.util.Log.e("NlueMix", "崩溃: " + text, e)
            } catch (_: Exception) { }
            prev?.uncaughtException(t, e)
        }
    }

    override fun onResume() {
        super.onResume()
        val filter = IntentFilter(MainActivity.ACTION_STATE)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            registerReceiver(stateReceiver, filter, RECEIVER_NOT_EXPORTED)
        } else {
            @Suppress("UnspecifiedRegisterReceiverFlag")
            registerReceiver(stateReceiver, filter)
        }
    }

    override fun onPause() {
        super.onPause()
        try { unregisterReceiver(stateReceiver) } catch (_: Exception) { }
    }

    private fun onStartStopClicked() {
        if (CaptureService.isRunning) {
            stopCaptureService()
            return
        }
        if (pairing) return

        val host = hostInput.text.toString().trim()
        val code = codeInput.text.toString().trim()

        if (host.isEmpty()) {
            toast("请填写电脑的 IP 地址")
            return
        }
        if (code.length != 4) {
            toast("连接码是 4 位数字")
            return
        }

        getSharedPreferences("nluemix", MODE_PRIVATE).edit()
            .putString("host", host)
            .putBoolean("use_mic", micSwitch.isChecked)
            .apply()

        pairing = true
        setStatus("正在连接电脑…")
        startButton.isEnabled = false

        val deviceName = (Build.MANUFACTURER + " " + Build.MODEL).trim()
        val prefs = getSharedPreferences("nluemix", MODE_PRIVATE)
        var deviceId = prefs.getString("deviceId", null)
        if (deviceId == null) {
            deviceId = UUID.randomUUID().toString()
            prefs.edit().putString("deviceId", deviceId).apply()
        }
        val finalDeviceId = deviceId

        Thread {
            // 预检查只做握手，不建立音频通道
            // （之前这里开了 TCP 音频通道又立刻关掉，导致电脑端看到"接入后立刻断开"）
            val ok = preCheck(host, code, finalDeviceId, deviceName)
            runOnUiThread {
                pairing = false
                startButton.isEnabled = true
                if (ok) {
                    if (ContextCompat.checkSelfPermission(this, Manifest.permission.RECORD_AUDIO)
                        != PackageManager.PERMISSION_GRANTED) {
                        setStatus("需要「录音」权限才能采集声音，请点允许")
                        recordAudioPermissionLauncher.launch(Manifest.permission.RECORD_AUDIO)
                    } else if (micSwitch.isChecked) {
                        // 麦克风模式不需要录屏授权，直接启动
                        setStatus("麦克风模式：不需要录屏授权，直接开始")
                        startCaptureService(0, android.content.Intent())
                    } else {
                        setStatus("连接码正确，正在请求录屏授权…")
                        requestProjection()
                    }
                } else {
                    setStatus("连不上电脑。检查：1) 电脑上 NlueMix 开着吗  2) 连接码对不对  3) 手机和电脑在同一个 Wi-Fi 吗")
                }
            }
        }.start()
    }

    /// <summary>
    /// 只做一次握手，验证地址和连接码对不对，然后立刻断开。
    /// 不建立音频通道，避免电脑端看到"接入又断开"的假象。
    /// </summary>
    private fun preCheck(host: String, code: String, deviceId: String, deviceName: String): Boolean {
        return try {
            val socket = java.net.Socket()
            socket.tcpNoDelay = true
            socket.connect(java.net.InetSocketAddress(host, Protocol.CONTROL_PORT), 6000)

            val hello = listOf(
                "HELLO", Protocol.VERSION.toString(),
                deviceId + "-check", deviceName,
                code, Protocol.SAMPLE_RATE.toString(),
                Protocol.CHANNELS.toString(), Protocol.CODEC_PCM16.toString(),
                "udp"   // 关键：预检查不用 TCP 音频
            ).joinToString("\t") + "\n"
            socket.getOutputStream().write(hello.toByteArray(Charsets.UTF_8))
            socket.getOutputStream().flush()

            val reply = socket.getInputStream().bufferedReader(Charsets.UTF_8).readLine()
            try { socket.close() } catch (_: Exception) {}

            reply != null && reply.startsWith("WELCOME")
        } catch (e: Exception) {
            false
        }
    }

    private fun requestProjection() {
        val manager = getSystemService(MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
        projectionLauncher.launch(manager.createScreenCaptureIntent())
    }

    private fun startCaptureService(resultCode: Int, data: Intent) {
        val intent = Intent(this, CaptureService::class.java).apply {
            action = CaptureService.ACTION_START
            putExtra(EXTRA_RESULT_CODE, resultCode)
            putExtra(EXTRA_RESULT_DATA, data)
            putExtra(EXTRA_HOST, hostInput.text.toString().trim())
            putExtra(EXTRA_CODE, codeInput.text.toString().trim())
            putExtra(CaptureService.EXTRA_USE_MIC, micSwitch.isChecked)
        }
        ContextCompat.startForegroundService(this, intent)
        setStatus("正在启动采集…")
    }

    private fun stopCaptureService() {
        startService(Intent(this, CaptureService::class.java).apply {
            action = CaptureService.ACTION_STOP
        })
        setStatus("正在停止…")
    }

    private fun setStatus(text: String) {
        statusText.text = text
    }

    private fun resetButton() {
        startButton.text = "开始串流"
        startButton.isEnabled = true
    }

    private fun toast(text: String) {
        Toast.makeText(this, text, Toast.LENGTH_SHORT).show()
    }
}
