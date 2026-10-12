package com.nluemix.sender

import android.content.Intent
import android.os.Bundle
import android.view.View
import android.widget.Button
import android.widget.CheckBox
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity

/**
 * 开屏风险提示页。两种模式共用这一个页面：
 *
 *  · 闸门模式（默认）：用户还没同意，或文案改版了。必须勾选确认框才能继续；
 *    不勾就点，只提醒不阻拦（拦着不让点会变成另一种强迫）。
 *    按返回键 = 不同意，直接退出。
 *
 *  · 查看模式（Intent 带 EXTRA_REVIEW=true）：从主界面的「使用前必读」进来，
 *    随时可以重看这一页，并且可以在这里【撤回同意】—— 撤回后退出 App。
 *    这是《个人信息保护法》第 15 条要求的撤回同意权。
 *
 * 一次修复（原来这里的 bug）：
 *    点「我已阅读并同意」只写了版本号、没写 skip_risk_notice，而主界面的判定
 *    要求两者都满足，于是主界面一启动又把本页拉起来 —— 两个 Activity 互相
 *    无限启动，用户只有点「永久关闭」才出得来。那等于强迫用户做"永久同意"
 *    才能用软件，正是本页设计上要避免的诱导。
 *    现在同意一次就记住（见 RiskNotice.rememberAgreed），文案改版才重新问。
 */
class RiskNoticeActivity : AppCompatActivity() {

    companion object {
        /** 带这个 extra 打开 = 查看模式，而不是闸门。 */
        const val EXTRA_REVIEW = "review"
    }

    private val review: Boolean by lazy { intent.getBooleanExtra(EXTRA_REVIEW, false) }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_risk_notice)

        findViewById<TextView>(R.id.riskTitle).text = RiskNotice.TITLE
        findViewById<TextView>(R.id.riskSubtitle).text = RiskNotice.SUBTITLE
        findViewById<TextView>(R.id.riskBody).text = RiskNotice.BODY

        val check = findViewById<CheckBox>(R.id.riskCheck)
        val hint = findViewById<TextView>(R.id.riskHint)
        val primary = findViewById<Button>(R.id.riskAgree)
        val secondary = findViewById<Button>(R.id.riskClosePermanently)

        if (review) {
            // ── 查看模式 ──
            // 不要求再勾一次：用户是回来看内容的，不是来重新签字的。
            check.visibility = View.GONE
            hint.text = "这是查看模式。你可以随时回到这里重看这一页。"
            primary.text = "关闭"
            primary.setOnClickListener { finish() }
            secondary.text = "撤回同意，退出 App"
            secondary.setOnClickListener { revokeAndExit() }
        } else {
            // ── 闸门模式 ──
            primary.setOnClickListener {
                if (!check.isChecked) {
                    Toast.makeText(this,
                        "请先勾选确认框，表示你确实看过这些内容。\n（不想看的话，按返回键退出即可。）",
                        Toast.LENGTH_LONG).show()
                    return@setOnClickListener
                }
                RiskNotice.rememberAgreed(this)
                enterApp()
            }
            secondary.text = "不同意，退出"
            secondary.setOnClickListener { finishAffinity() }
        }
    }

    /**
     * 撤回同意。
     *
     * 必须先把后果说清楚，再让用户决定 —— 一开始这里没有确认框，点一下 App 就退了，
     * 用户的第一反应是"怎么把我东西删了"。撤回并不删除任何数据（只清掉"已同意"这一条
     * 记录），但"程序会退出"这件事必须提前讲明白，否则就是偷袭。
     */
    private fun revokeAndExit() {
        AlertDialog.Builder(this)
            .setTitle("撤回同意")
            .setMessage(
                "撤回后：\n\n" +
                "· 程序会立即退出\n" +
                "· 下次打开需要重新阅读并同意这一页\n\n" +
                "不会删除你的任何设置（电脑地址、连接码、麦克风开关都保留），" +
                "也不会卸载 App。确定撤回吗？"
            )
            .setNegativeButton("取消", null)
            .setPositiveButton("确定撤回") { _, _ ->
                RiskNotice.revoke(this)
                Toast.makeText(this, "已撤回同意。下次打开需要重新阅读并同意。",
                    Toast.LENGTH_LONG).show()
                finishAffinity()
            }
            .show()
    }

    private fun enterApp() {
        startActivity(Intent(this, MainActivity::class.java))
        finish()
    }

    /** 闸门模式下按返回键 = 不同意，直接退出，不做任何挽留。 */
    @Deprecated("Deprecated in Java")
    override fun onBackPressed() {
        if (review) {
            super.onBackPressed()      // 查看模式：返回就是关掉这一页
        } else {
            finishAffinity()           // 闸门模式：不同意，退出
        }
    }
}
