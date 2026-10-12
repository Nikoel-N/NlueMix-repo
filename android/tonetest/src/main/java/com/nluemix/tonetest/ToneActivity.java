package com.nluemix.tonetest;

import android.app.Activity;
import android.media.AudioAttributes;
import android.media.AudioFormat;
import android.media.AudioTrack;
import android.os.Bundle;
import android.widget.TextView;

/**
 * 对照实验：持续播放 1 kHz 正弦波，usage = USAGE_MEDIA。
 * 本应用的 Manifest 里显式声明了 android:allowAudioPlaybackCapture="true"，
 * 因此它应当【可以】被 MediaProjection 采集到。
 *
 * 用它和 B 站做 A/B：同一个 NlueMix APK、同一套 AudioRecord 代码、同一个 MediaProjection 授权。
 */
public class ToneActivity extends Activity {

    private static final int RATE = 48000;
    private static final int FRAME = 960;          // 20 ms

    private AudioTrack track;
    private Thread worker;
    private volatile boolean running;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        TextView tv = new TextView(this);
        tv.setTextSize(18);
        tv.setPadding(48, 96, 48, 48);
        tv.setText("NlueMix 对照测试音\n\n"
                + "正在播放：1 kHz 正弦波（持续）\n"
                + "采样率：" + RATE + " Hz / 立体声\n"
                + "usage：USAGE_MEDIA\n"
                + "Manifest：allowAudioPlaybackCapture=\"true\"\n\n"
                + "这个 App 只用于对照实验。\n"
                + "如果 NlueMix 能采到它、却采不到 B 站，\n"
                + "就说明限制来自 B 站/系统策略，而不是采集代码。");
        setContentView(tv);

        int minBuf = AudioTrack.getMinBufferSize(
                RATE, AudioFormat.CHANNEL_OUT_STEREO, AudioFormat.ENCODING_PCM_16BIT);
        int bufSize = Math.max(minBuf > 0 ? minBuf : 0, RATE / 5 * 4);

        track = new AudioTrack.Builder()
                .setAudioAttributes(new AudioAttributes.Builder()
                        .setUsage(AudioAttributes.USAGE_MEDIA)
                        .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
                        .build())
                .setAudioFormat(new AudioFormat.Builder()
                        .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                        .setSampleRate(RATE)
                        .setChannelMask(AudioFormat.CHANNEL_OUT_STEREO)
                        .build())
                .setBufferSizeInBytes(bufSize)
                .setTransferMode(AudioTrack.MODE_STREAM)
                .build();

        track.play();
        running = true;

        worker = new Thread(new Runnable() {
            @Override public void run() {
                short[] buf = new short[FRAME * 2];
                double phase = 0;
                while (running) {
                    for (int i = 0; i < FRAME; i++) {
                        short v = (short) (0.4 * 32767 * Math.sin(phase));
                        phase += 2 * Math.PI * 1000.0 / RATE;
                        if (phase > 2 * Math.PI) phase -= 2 * Math.PI;
                        buf[i * 2] = v;
                        buf[i * 2 + 1] = v;
                    }
                    try { track.write(buf, 0, buf.length); } catch (Throwable t) { break; }
                }
            }
        }, "tone-writer");
        worker.start();
    }

    @Override
    protected void onDestroy() {
        running = false;
        try { if (worker != null) worker.join(600); } catch (Throwable ignored) { }
        try { if (track != null) { track.stop(); track.release(); } } catch (Throwable ignored) { }
        super.onDestroy();
    }
}
