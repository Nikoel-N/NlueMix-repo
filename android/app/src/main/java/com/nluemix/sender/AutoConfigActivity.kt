package com.nluemix.sender

import android.content.Intent
import android.os.Bundle
import android.provider.Settings
import android.widget.Button
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity

/**
 * 自动配置向导。
 *
 * 目标是把「开启无线调试 → 读端口 → 读配对码」这几步从"用户手动折腾"
 * 变成"点两下"。
 *
 * <p><b>为什么要用户手动开无障碍：</b>无障碍权限只能由用户在系统设置里授权，
 * 任何 App 都不能自己打开它（这是 Android 的设计，也不该被绕过）。
 * 所以第一步永远是引导用户去设置页。</p>
 *
 * <p><b>每一步都有超时和失败提示</b>，因为不同 ROM 的设置页面差异很大，
 * 读不到是正常情况，这时要把当前屏幕内容展示出来让人能看懂发生了什么。</p>
 */
class AutoConfigActivity : AppCompatActivity() {

    private lateinit var status: TextView
    private lateinit var output: TextView

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_auto_config)

        status = findViewById(R.id.acStatus)
        output = findViewById(R.id.acOutput)

        findViewById<Button>(R.id.acGrant).setOnClickListener {
            try {
                startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
                toast("请在列表里找到 NlueMix，打开开关")
            } catch (e: Throwable) {
                toast("打不开无障碍设置：" + e.message)
            }
        }

        findViewById<Button>(R.id.acRun).setOnClickListener { runAutoConfig() }

        // 「（调试）读取当前屏幕内容」只在开发者版出现。
        // 它是排查"选择器为什么没匹配上"用的，对普通用户没有任何意义，
        // 反而会让人以为软件在偷偷读屏幕。
        val dumpBtn = findViewById<Button>(R.id.acDump)
        if (Edition.isDeveloper) {
            dumpBtn.setOnClickListener {
                val svc = AutoConfigService.instance
                if (svc == null) { toast("无障碍服务没开"); return@setOnClickListener }
                output.text = svc.screenText()
            }
        } else {
            dumpBtn.visibility = android.view.View.GONE
        }
    }

    override fun onResume() {
        super.onResume()
        refreshStatus()
    }

    private fun refreshStatus() {
        status.text = if (AutoConfigService.isRunning()) {
            "无障碍权限：已开启 ✓\n\n现在可以点第 2 步了。它会自动打开无线调试页面并把端口读出来。"
        } else {
            "无障碍权限：未开启\n\n请点第 1 步，在系统设置里找到 NlueMix 并打开开关。\n\n" +
            "这个权限只用来操作「开发者选项」页面，不读取其他屏幕内容。"
        }
    }

    /**
     * 自动配置主流程。
     *
     * 分步做，每步之间留时间等页面切换 —— 无障碍操作是异步的，
     * 点完立刻读下一个页面必然读到旧的。
     */
    private fun runAutoConfig() {
        val svc = AutoConfigService.instance
        if (svc == null) {
            toast("请先开启无障碍权限")
            return
        }

        log("=== 开始自动配置 ===")

        // 第 1 步：打开开发者选项页面
        log("1) 打开开发者选项页面…")
        if (!svc.openDeveloperOptions()) {
            log("   ✗ 打不开。请确认手机已开启开发者选项（设置 → 关于手机 → 连点版本号 7 次）。")
            return
        }

        // 第 2 步：等页面出来，找「无线调试」并点进去
        svc.post(1500) {
            log("2) 在当前页面找「无线调试」…")
            if (!svc.openWirelessDebugging()) {
                log("   ✗ 没找到。下面把当前屏幕的文字打出来，据此可以判断是页面没加载完，还是入口名字不一样。")
                log("")
                log(svc.screenText())
                return@post
            }
            log("   ✓ 已进入无线调试页面")

            // 第 3 步：等页面稳定，读端口
            svc.post(1800) {
                log("3) 读取「IP 地址和端口」…")
                val ep = svc.readWirelessEndpoint()
                if (ep == null) {
                    log("   ✗ 没读到。当前屏幕内容如下：")
                    log("")
                    log(svc.screenText())
                } else {
                    log("   ✓ 读到：" + ep)
                    onEndpointFound(ep)
                }
            }
        }
    }

    /**
     * 读到端口之后的动作。
     *
     * 目前只把它显示出来并回填到主界面；真正"发给电脑让电脑去 adb connect"
     * 需要电脑端配合（接收消息并执行 adb），下一步再做。
     */
    private fun onEndpointFound(endpoint: String) {
        val parts = endpoint.split(":")
        if (parts.size != 2) return
        val ip = parts[0]
        val port = parts[1]

        log("")
        log("手机 IP：" + ip)
        log("调试端口：" + port)
        log("")
        log("已回填到主界面的「电脑地址」旁边的手机 IP（如果之前是空的）。")

        // 回填到主界面：主界面用 phoneIp 去 adb connect，需要的是「IP:端口」
        getSharedPreferences("nluemix", MODE_PRIVATE).edit()
            .putString("auto_phone_ip", ip)
            .putString("auto_phone_port", port)
            .putString("auto_phone_endpoint", endpoint)
            .apply()

        toast("读到端口 " + endpoint)
    }

    private fun log(s: String) {
        output.append(if (output.text.isEmpty()) s else "\n" + s)
    }

    private fun toast(s: String) = Toast.makeText(this, s, Toast.LENGTH_SHORT).show()
}
