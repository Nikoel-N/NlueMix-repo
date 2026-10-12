package com.nluemix.sender

/**
 * 版本形态：用户版 / 开发者版。
 *
 * 为什么分：
 *   排查期间界面上放了一些"只对开发者有意义"的东西 —— 调试按钮、屏幕内容转储、
 *   采集方案细节、诊断日志。这些对普通用户是噪音，还会让人以为软件不稳定。
 *   但直接删掉以后就没法排查了，所以做成两种形态。
 *
 * 怎么切换：构建时加 -PdevBuild=true 就是开发者版，不加就是用户版。
 *   值由 build.gradle.kts 的 buildConfigField 注入 BuildConfig.DEV_BUILD，
 *   所以两个 APK 是【分别构建】的，用户版里根本不存在那些调试代码路径。
 *
 * 默认是用户版 —— 发给别人的那个。
 *
 * 注意：采集、协议、传输两个版本【完全一致】。只差界面上的排查入口。
 *       这一点是刻意的：如果两个版本音频行为不一样，排查就没有参照意义了。
 */
object Edition {

    /** 是否开发者版。 */
    val isDeveloper: Boolean = BuildConfig.DEV_BUILD

    /** 用户版为 true。 */
    val isUser: Boolean get() = !isDeveloper

    /** 版本名，用于界面和日志。 */
    val name: String get() = if (isDeveloper) "开发者版" else "用户版"
}
