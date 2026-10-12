package com.nluemix.sender

import android.app.Activity
import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import android.os.Bundle
import android.widget.TextView

/**
 * 对照实验专用：持续播放 1 kHz 正弦波。
 *
 * 目的：把"音源是不是在出声"这个变量固定住。
 *   · B 站声明里没有 allowAudioPlaybackCapture → 理论上采不到
 *   · 本应用可通过 -PallowCapture=true 声明它 → 理论上采得到
 * 两边用同一个 NlueMix APK、同一套 MediaProjection / AudioRecord 代码。
 */
class ToneActivity : Activity() {

    private var track: AudioTrack? = null
    private var worker: Thread? = null
    @Volatile private var running = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val tv = TextView(this)
        tv.textSize = 18f
        tv.setPadding(48, 96, 48, 48)
        tv.text = "NlueMix 对照测试音\n\n" +
                "正在播放：1 kHz 正弦波（持续）\n" +
                "采样率：48000 Hz / 立体声\n" +
                "usage：USAGE_MEDIA\n" +
                "allowAudioPlaybackCapture：" +
                (if (isCaptureAllowed()) "true（应能被采集）" else "false / 未声明（应采不到）")
        setContentView(tv)

        val rate = 48000
        val minBuf = AudioTrack.getMinBufferSize(
            rate, AudioFormat.CHANNEL_OUT_STEREO, AudioFormat.ENCODING_PCM_16BIT
        )
        val bufSize = maxOf(if (minBuf > 0) minBuf else 0, rate / 5 * 4)

        track = AudioTrack.Builder()
            .setAudioAttributes(
                AudioAttributes.Builder()
                    .setUsage(AudioAttributes.USAGE_MEDIA)
                    .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
                    .build()
            )
            .setAudioFormat(
                AudioFormat.Builder()
                    .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                    .setSampleRate(rate)
                    .setChannelMask(AudioFormat.CHANNEL_OUT_STEREO)
                    .build()
            )
            .setBufferSizeInBytes(bufSize)
            .setTransferMode(AudioTrack.MODE_STREAM)
            .build()

        track?.play()
        running = true

        worker = Thread({
            val buf = ShortArray(960 * 2)
            var phase = 0.0
            while (running) {
                for (i in 0 until 960) {
                    val v = (0.4 * 32767 * Math.sin(phase)).toInt().toShort()
                    phase += 2 * Math.PI * 1000.0 / rate
                    if (phase > 2 * Math.PI) phase -= 2 * Math.PI
                    buf[i * 2] = v
                    buf[i * 2 + 1] = v
                }
                try { track?.write(buf, 0, buf.size) } catch (t: Throwable) { break }
            }
        }, "tone-writer")
        worker?.start()
    }

    private fun isCaptureAllowed(): Boolean {
        return try {
            val ai = packageManager.getApplicationInfo(packageName, 0)
            // ApplicationInfo.FLAG_ALLOW_AUDIO_PLAYBACK_CAPTURE 是隐藏常量，这里用反射读字段值
            val f = android.content.pm.ApplicationInfo::class.java.getField("FLAG_ALLOW_AUDIO_PLAYBACK_CAPTURE")
            val flag = f.getInt(null)
            (ai.flags and flag) != 0
        } catch (t: Throwable) { false }
    }

    override fun onDestroy() {
        running = false
        try { worker?.join(600) } catch (_: Throwable) { }
        try { track?.stop(); track?.release() } catch (_: Throwable) { }
        super.onDestroy()
    }
}
