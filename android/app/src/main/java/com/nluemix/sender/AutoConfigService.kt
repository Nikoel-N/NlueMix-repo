package com.nluemix.sender

import android.accessibilityservice.AccessibilityService
import android.os.Handler
import android.os.Looper
import android.util.Log
import android.view.accessibility.AccessibilityEvent
import android.view.accessibility.AccessibilityNodeInfo

/**
 * 自动配置用的无障碍服务。
 *
 * <p><b>它只干一件事：</b>帮用户操作手机的「开发者选项 → 无线调试」页面 ——
 * 打开开关、读出「IP 地址和端口」、读出配对码。别的什么都不做。</p>
 *
 * <p><b>为什么必须用无障碍：</b>Android 把 {@code Settings.Global.adb_wifi_enabled}
 * 放在 MANAGE_DEBUGGING（signature|privileged）后面，普通 App 根本写不了；
 * 系统设置界面是这个开关<b>唯一</b>被允许的写入方，而它是个真实 UI 控件，
 * 所以无障碍能操作它。这不是绕过系统，是走系统允许的那条路。</p>
 *
 * <p><b>能力边界（写在这里，也写进隐私说明）：</b>
 * 无障碍在原理上能读屏幕上任何内容。本服务：
 * · 只在被 {@link AutoConfigActivity} 显式调用时才读取屏幕；
 * · 只读取，不缓存、不上传任何屏幕内容；
 * · 不监听文本变化事件，拿不到输入框里逐字输入的内容。</p>
 */
class AutoConfigService : AccessibilityService() {

    companion object {
        private const val TAG = "NlueMix.A11y"

        /** 供 Activity 判断服务是否已开启。 */
        @Volatile
        var instance: AutoConfigService? = null
            private set

        fun isRunning(): Boolean = instance != null
    }

    private val main = Handler(Looper.getMainLooper())

    override fun onServiceConnected() {
        super.onServiceConnected()
        instance = this
        Log.i(TAG, "无障碍服务已连接")
    }

    override fun onDestroy() {
        instance = null
        Log.i(TAG, "无障碍服务已断开")
        super.onDestroy()
    }

    override fun onInterrupt() {
        Log.i(TAG, "无障碍服务被中断")
    }

    /**
     * 我们不订阅事件流。
     *
     * 刻意留空 —— 自动配置是"被调用时才动"的模式，
     * 常驻监听屏幕变化既没必要，也违背最小权限。
     */
    override fun onAccessibilityEvent(event: AccessibilityEvent?) {
        // 故意不处理
    }

    // ──────────────────────────────────────────── 屏幕读取

    /** 当前活动窗口的根节点；拿不到时返回 null。 */
    fun root(): AccessibilityNodeInfo? = try {
        rootInActiveWindow
    } catch (e: Throwable) {
        Log.w(TAG, "取根节点失败", e)
        null
    }

    /**
     * 把当前屏幕上的所有可见文字dump 成一行一行，用于调试选择器。
     *
     * 这是开发工具：真机上不同 ROM 的页面结构千差万别，
     * 先把实际能看到什么打出来，再写定位规则。
     */
    fun dumpScreen(): List<String> {
        val out = ArrayList<String>()
        val r = root() ?: return listOf("(拿不到根节点：无障碍服务可能没开，或当前页面不允许读取)")
        walk(r, 0, out)
        return out
    }

    private fun walk(node: AccessibilityNodeInfo?, depth: Int, out: MutableList<String>) {
        if (node == null || depth > 30 || out.size > 500) return
        val text = node.text?.toString()?.trim().orEmpty()
        val desc = node.contentDescription?.toString()?.trim().orEmpty()
        val id = node.viewIdResourceName.orEmpty()
        if (text.isNotEmpty() || desc.isNotEmpty()) {
            val kind = node.className?.toString()?.substringAfterLast('.') ?: "?"
            out.add(
                "  ".repeat(depth) + kind +
                (if (id.isNotEmpty()) " #" + id.substringAfterLast('/') else "") +
                (if (text.isNotEmpty()) "  text=\"" + text + "\"" else "") +
                (if (desc.isNotEmpty()) "  desc=\"" + desc + "\"" else "")
            )
        }
        for (i in 0 until node.childCount) {
            walk(node.getChild(i), depth + 1, out)
        }
    }

    // ──────────────────────────────────────────── 查找与操作

    /** 在整棵树里找第一个满足条件的节点。 */
    fun find(predicate: (AccessibilityNodeInfo) -> Boolean): AccessibilityNodeInfo? {
        val r = root() ?: return null
        return search(r, predicate, 0)
    }

    private fun search(
        node: AccessibilityNodeInfo?,
        predicate: (AccessibilityNodeInfo) -> Boolean,
        depth: Int
    ): AccessibilityNodeInfo? {
        if (node == null || depth > 30) return null
        if (predicate(node)) return node
        for (i in 0 until node.childCount) {
            val hit = search(node.getChild(i), predicate, depth + 1)
            if (hit != null) return hit
        }
        return null
    }

    /** 找出所有满足条件的节点（用于一屏里有多个同文案控件的情况）。 */
    fun findAll(predicate: (AccessibilityNodeInfo) -> Boolean): List<AccessibilityNodeInfo> {
        val out = ArrayList<AccessibilityNodeInfo>()
        val r = root() ?: return out
        collect(r, predicate, out, 0)
        return out
    }

    private fun collect(
        node: AccessibilityNodeInfo?,
        predicate: (AccessibilityNodeInfo) -> Boolean,
        out: MutableList<AccessibilityNodeInfo>,
        depth: Int
    ) {
        if (node == null || depth > 30) return
        if (predicate(node)) out.add(node)
        for (i in 0 until node.childCount) collect(node.getChild(i), predicate, out, depth + 1)
    }

    /** 按文字找节点（相等或包含）。 */
    fun findByText(text: String, exact: Boolean = false): AccessibilityNodeInfo? =
        find { n ->
            val t = n.text?.toString()?.trim().orEmpty()
            if (t.isEmpty()) false else if (exact) t == text else t.contains(text)
        }

    /**
     * 点一个节点。会沿着父链往上找第一个可点击的祖先 ——
     * Android 设置页里带文案的往往是 TextView，真正可点的是它的父容器。
     */
    fun clickNode(node: AccessibilityNodeInfo?): Boolean {
        var n = node
        var hops = 0
        while (n != null && hops < 8) {
            if (n.isClickable) {
                val ok = try { n.performAction(AccessibilityNodeInfo.ACTION_CLICK) } catch (e: Throwable) { false }
                if (ok) return true
            }
            n = n.parent
            hops++
        }
        return false
    }

    /** 按文字点击。 */
    fun clickText(text: String, exact: Boolean = false): Boolean {
        val n = findByText(text, exact) ?: return false
        return clickNode(n)
    }

    /** 延迟执行（都跑在主线程，避免跨线程碰 UI 节点）。 */
    fun post(delayMs: Long, block: () -> Unit) {
        main.postDelayed(block, delayMs)
    }

    // ──────────────────────────────────────────── 高层动作

    /**
     * 打开「开发者选项」页面。
     *
     * 这是公开 API，不需要无障碍也能跳；但进去之后的导航要靠无障碍。
     */
    fun openDeveloperOptions(): Boolean = try {
        val i = android.content.Intent(android.provider.Settings.ACTION_APPLICATION_DEVELOPMENT_SETTINGS)
        i.addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK)
        startActivity(i)
        true
    } catch (e: Throwable) {
        Log.w(TAG, "打不开开发者选项页面", e)
        false
    }

    /**
     * 在「开发者选项」页面里找到「无线调试」入口并点进去。
     *
     * 注意：Android 没有公开的 action 能直接跳到无线调试子页面
     * （社区共识是用快捷设置磁贴长按），所以这里走"找文字再点"。
     */
    fun openWirelessDebugging(): Boolean {
        // 页面里可能同时有"无线调试"标题和说明文字，取第一个可点的
        val candidates = findAll { n ->
            val t = n.text?.toString()?.trim().orEmpty()
            t == "无线调试" || t == "Wireless debugging"
        }
        for (c in candidates) {
            if (clickNode(c)) return true
        }
        return false
    }

    /**
     * 读取「无线调试」页面上的「IP 地址和端口」。
     *
     * 页面上通常呈现为：
     *     IP 地址和端口
     *     192.168.1.6:37251
     * 但不同 ROM 的排版差别很大，所以这里做两种尝试：
     *   1. 先找那串"IP:端口"本身（正则匹配）
     *   2. 找不到就返回页面上的全部文字，让人工判断
     */
    fun readWirelessEndpoint(): String? {
        val pattern = Regex("""\b(\d{1,3}(?:\.\d{1,3}){3}):(\d{2,5})\b""")
        var hit: String? = null
        find { n ->
            val t = n.text?.toString().orEmpty()
            val m = pattern.find(t)
            if (m != null) { hit = m.value; true } else false
        }
        return hit
    }

    /**
     * 读取配对弹窗上的「6 位配对码」。
     *
     * 配对页面标题一般是「使用配对码配对设备」，正文里有：
     *     Wi-Fi 配对码
     *     123456
     * 以及「IP 地址和端口」下的配对端口。
     */
    fun readPairingCode(): String? {
        val codePattern = Regex("""^\d{6}$""")
        var hit: String? = null
        find { n ->
            val t = n.text?.toString()?.trim().orEmpty()
            if (codePattern.matches(t)) { hit = t; true } else false
        }
        return hit
    }

    /** 当前屏幕的全部文字，用于开发期调试和"读不到时给用户看"。 */
    fun screenText(): String = dumpScreen().joinToString("\n")
}
