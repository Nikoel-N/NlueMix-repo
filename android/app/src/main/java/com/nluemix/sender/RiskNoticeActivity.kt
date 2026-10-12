package com.nluemix.sender

import android.content.Intent
import android.os.Bundle
import android.widget.Button
import android.widget.CheckBox
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity

/**
 * 开屏风险提示页。
 *
 * 设计要点（有意为之）：
 *  · 每次启动都弹，除非用户主动选了「永久关闭」。
 *  · 只有「永久关闭」那个按钮会写 skip_risk_notice=true，
 *    也就是说关闭这个提示本身就等于永久同意 —— 不会出现"用户没看就被静默跳过"。
 *  · 全部文字 12-22sp，没有小字，正文可滚动可横向拖动。
 *  · 不同意就退出，不弹二次挽留、不诱导。
 *  · 撤回入口：系统设置里清应用数据，或后续在 App 设置页加（见 RiskNotice.reset）。
 */
class RiskNoticeActivity : AppCompatActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_risk_notice)

        findViewById<TextView>(R.id.riskTitle).text = RiskNotice.TITLE
        findViewById<TextView>(R.id.riskSubtitle).text = RiskNotice.SUBTITLE
        findViewById<TextView>(R.id.riskBody).text = RiskNotice.BODY

        val check = findViewById<CheckBox>(R.id.riskCheck)

        findViewById<Button>(R.id.riskAgree).setOnClickListener {
            if (!check.isChecked) {
                // 不阻止，只提醒 —— 拦着不让点会变成另一种强迫
                Toast.makeText(this,
                    "请先勾选确认框，表示你确实看过这些内容。\n（不想看的话，按返回键退出即可。）",
                    Toast.LENGTH_LONG).show()
                return@setOnClickListener
            }
            RiskNotice.rememberAgreed(this, skipFromNowOn = false)
            enterApp()
        }

        findViewById<Button>(R.id.riskClosePermanently).setOnClickListener {
            if (!check.isChecked) {
                Toast.makeText(this,
                    "「永久关闭」等于永久同意本页全部内容，请先勾选确认框。",
                    Toast.LENGTH_LONG).show()
                return@setOnClickListener
            }
            RiskNotice.rememberAgreed(this, skipFromNowOn = true)
            enterApp()
        }
    }

    private fun enterApp() {
        startActivity(Intent(this, MainActivity::class.java))
        finish()
    }

    /** 按返回键 = 不同意，直接退出，不做任何挽留。 */
    @Deprecated("Deprecated in Java")
    override fun onBackPressed() {
        finishAffinity()
    }
}
