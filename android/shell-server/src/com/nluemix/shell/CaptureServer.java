package com.nluemix.shell;

import android.media.AudioFormat;
import android.media.AudioRecord;
import android.media.MediaRecorder;
import android.os.Build;

import java.io.BufferedOutputStream;
import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.Socket;

/**
 * NlueMix 采集端 —— 以 shell 身份运行，走 REMOTE_SUBMIX 采集系统音频，输出走 NlueMix 自有协议。
 *
 * <p><b>技术来源说明（写清楚，不含糊）：</b>
 * 「以 shell 身份运行 + REMOTE_SUBMIX 采集系统音频」是 Android 平台上的一条<strong>公开技术路径</strong>，
 * scrcpy、sndcpy 等工具走的都是这条；它由 Android 的权限模型决定，不是任何项目的私有方案。
 * 本项目是在实测确认这条路可行之后<strong>独立实现</strong>的：
 * 采集只用 Android 公开 API（{@code AudioRecord} + {@code MediaRecorder.AudioSource.REMOTE_SUBMIX}），
 * 帧划分、16 字节包头、握手协议、传输层全部为本项目自有设计。
 * 与 scrcpy（Apache-2.0）之间不存在代码复用。</p>
 *
 * <p><b>为什么需要它：</b>在 Redmi K70 Ultra / Android 16 / HyperOS V816 上实测，
 * 普通 App 走 MediaProjection + AudioPlaybackCapture 只能读到纯静音（PEAK=0）；
 * 而同一时刻以 shell 身份走 REMOTE_SUBMIX 能录到真实声音（PEAK=32154，75.73% 非零采样）。
 * 原因是 shell 用户持有签名权限 {@code CAPTURE_AUDIO_OUTPUT}，普通 App 拿不到。</p>
 *
 * <p><b>两种输出方式：</b></p>
 * <ul>
 *   <li><b>无线模式（推荐）</b>：{@code --host=<电脑IP> --port=47820 --code=<连接码>}
 *       —— 自己通过 Wi-Fi 连到电脑，按 NlueMix 协议（HELLO + 16 字节包头 + PCM16）推流。
 *       adb 只负责把它启动起来，音频**完全不经过 adb**，全程无损。</li>
 *   <li><b>标准输出模式</b>：不传 --host，则把 PCM 写到 stdout，由 {@code adb exec-out} 读走（USB 备用）。</li>
 * </ul>
 *
 * <p>音频格式固定：48000 Hz / 立体声 / PCM16 / 20 ms 一帧（1.536 Mbps，Wi-Fi 毫无压力）。</p>
 */
public final class CaptureServer {

    private static final int DEFAULT_RATE = 48000;
    private static final int CHANNELS = 2;
    private static final int FRAME_SAMPLES = 960;               // 20 ms @ 48 kHz
    private static final int FRAME_BYTES = FRAME_SAMPLES * CHANNELS * 2;
    private static final int MAGIC = 0x4D424C55;                // "MBLU"

    public static void main(String[] args) throws Exception {
        int rate = DEFAULT_RATE;
        String host = null;
        String code = "";
        String name = null;
        int port = 47820;
        boolean listOnly = false;

        for (String a : args) {
            if (a.startsWith("--rate=")) rate = Integer.parseInt(a.substring(7));
            else if (a.startsWith("--host=")) host = a.substring(7);
            else if (a.startsWith("--port=")) port = Integer.parseInt(a.substring(7));
            else if (a.startsWith("--code=")) code = a.substring(7);
            else if (a.startsWith("--name=")) name = a.substring(7);
            else if ("--list".equals(a)) listOnly = true;
        }

        if (name == null) name = (Build.MANUFACTURER + " " + Build.MODEL).trim();
        if (name.isEmpty()) name = "Android 手机";

        System.err.println("nluemix-server: rate=" + rate + " host=" + host + " port=" + port);

        if (listOnly) {
            System.err.println("nluemix-server: ready");
            return;
        }

        // ★ 先建采集器，拿到【设备实际】的采样率，再决定对外声称多少。
        //
        //   原来的顺序是先按请求值发 HELLO、再建采集器，中间没有任何校验。
        //   如果设备实际给的是 44100 而我们声称 48000，电脑就会把音频放快 8.8%，
        //   听感正是「电音」（音调偏高、发飘）。这是实测听到的症状，不是推测。
        AudioRecord recorder = createRecorder(rate);
        if (recorder == null) {
            System.err.println("nluemix-server: AudioRecord 初始化失败");
            System.exit(2);
            return;
        }
        int actualRate = recorder.getSampleRate();
        if (actualRate > 0 && actualRate != rate) {
            System.err.println("nluemix-server: 注意：请求 " + rate + " Hz，设备实际给 "
                    + actualRate + " Hz —— 按实际值上报，否则电脑侧会变速成电音");
            rate = actualRate;
        }

        OutputStream sink;
        Socket socket = null;

        if (host != null && host.length() > 0) {
            // ── 无线模式：直接连电脑，走 NlueMix 协议 ──
            socket = new Socket();
            socket.setTcpNoDelay(true);
            socket.connect(new InetSocketAddress(host, port), 8000);
            OutputStream raw = new BufferedOutputStream(socket.getOutputStream(), 1 << 16);

            String hello = "HELLO\t1\tnluemix-shell\t" + name.replace("\t", " ")
                    + "\t" + code + "\t" + rate + "\t" + CHANNELS + "\t0\ttcp\n";
            raw.write(hello.getBytes("UTF-8"));
            raw.flush();

            BufferedReader reader = new BufferedReader(new InputStreamReader(socket.getInputStream(), "UTF-8"));
            String reply = reader.readLine();
            if (reply == null || !reply.startsWith("WELCOME")) {
                System.err.println("nluemix-server: 电脑拒绝了连接: " + reply);
                return;
            }
            System.err.println("nluemix-server: 已连上电脑 " + host + ":" + port + " → " + reply.trim());
            sink = new FramedSink(raw);
        } else {
            // ── 标准输出模式（USB / adb exec-out）──
            //
            // ★ 这里也走帧格式，不是裸 PCM。
            //   原因：adb exec-out 是裸字节流，既没有帧边界也没有校验；
            //   Wi-Fi 上抖动/丢几个字节就会让接收端按固定长度硬切帧【永久错位】，
            //   左右声道被拆开重新配对，听感是持续风噪 + 音质极差，且不会自愈。
            //   加 16 字节包头（含 magic + 序号）之后，接收端能靠 magic 重新同步。
            sink = new FramedSink(new BufferedOutputStream(System.out, 1 << 16));
        }

        recorder.startRecording();
        System.err.println("nluemix-server: 采集已开始（REMOTE_SUBMIX, " + rate + " Hz, "
                + CHANNELS + " ch, PCM16）");

        byte[] frame = new byte[FRAME_BYTES];
        // ★ 修复：AudioRecord.read() 返回的字节数不保证是"一个采样"（立体声 16-bit = 4 字节）的整数倍。
        //   原写法 samples = n / (CHANNELS * 2) 会向下取整，接收端按 samples*4 消费字节，
        //   比实际写入的少 —— TCP 是字节流，少消费的字节会永久滞留，导致整个流【错位】：
        //   后续包头全读歪、序号乱跳（实测丢包计数虚高到 9359）。
        //   这里改成先累积、只发送"整采样对齐"的部分，余下不足一个采样的字节留到下一轮，
        //   保证写入字节数 === 16 + samples*4，接收端永不失步。
        final int SAMPLE_BYTES = CHANNELS * 2;
        byte[] pending = new byte[FRAME_BYTES + SAMPLE_BYTES];
        int pendingLen = 0;
        long total = 0;
        int seq = 0;

        // ── 诊断统计 ──
        // 每 5 秒往 stderr 打一行，供 adb logcat / 电脑端观察。
        // 作用：锁屏测试时用它可以判断"到底还在不在传"，不用猜。
        long statStart = System.currentTimeMillis();
        long statPackets = 0;
        long statNonZero = 0;
        long statSamples = 0;
        int statPeak = 0;

        // 尽力申请 CPU 唤醒锁。shell(uid 2000) 持有 android.permission.WAKE_LOCK
        // （实测 granted=true），所以这里能拿到。全部失败也不影响采集。
        Object wakeLock = acquireWakeLock();

        try {
            while (true) {
                int n = recorder.read(frame, 0, frame.length);
                if (n < 0) {
                    System.err.println("nluemix-server: read 返回错误码 " + n);
                    break;
                }
                if (n == 0) continue;

                if (n > pending.length - pendingLen) n = pending.length - pendingLen;   // 防御
                System.arraycopy(frame, 0, pending, pendingLen, n);
                pendingLen += n;

                int usable = pendingLen - (pendingLen % SAMPLE_BYTES);
                if (usable == 0) continue;

                if (sink instanceof FramedSink) {
                    ((FramedSink) sink).writeFrame(pending, usable, seq++, usable / SAMPLE_BYTES);
                } else {
                    sink.write(pending, 0, usable);
                    sink.flush();
                }
                total += usable;
                statPackets++;

                // 统计本次发送的样本（小端 PCM16 立体声交错）
                for (int i = 0; i + 1 < usable; i += 2) {
                    int v = (pending[i] & 0xFF) | (pending[i + 1] << 8);
                    if (v > 32767) v -= 65536;              // 有符号还原
                    int av = v < 0 ? -v : v;
                    if (av > statPeak) statPeak = av;
                    statSamples++;
                    if (v != 0) statNonZero++;
                }

                long now = System.currentTimeMillis();
                if (now - statStart >= 5000) {
                    long ms = now - statStart;
                    System.err.println("nluemix-server: [统计] " + (statPackets * 1000 / ms)
                            + " 包/秒, 峰值=" + statPeak
                            + ", 非零=" + statNonZero + "/" + statSamples
                            + "(" + (statSamples == 0 ? 0 : statNonZero * 100 / statSamples) + "%)"
                            + ", 累计=" + total + " 字节");
                    statStart = now;
                    statPackets = 0;
                    statNonZero = 0;
                    statSamples = 0;
                    statPeak = 0;
                }

                // 不足一个采样的尾部留到下一轮，不能丢（丢了就又是错位）
                int leftover = pendingLen - usable;
                if (leftover > 0) System.arraycopy(pending, usable, pending, 0, leftover);
                pendingLen = leftover;

                // 注意：这里原先有一句 "if (System.in.available() > 0 && System.in.read() < 0) break;"，
                // 想靠关闭 stdin 来停止采集。但在 adb shell 场景下 stdin 的行为不确定，
                // 一旦被判成 EOF 就会让采集端【静默退出】，表现成"声音响一会儿就没了"。
                // 停止采集改由电脑端 StopRemote() 显式完成，不再依赖 stdin。
            }
        } catch (Throwable t) {
            System.err.println("nluemix-server: 采集循环结束: " + t);
        } finally {
            releaseWakeLock(wakeLock);
            try { recorder.stop(); } catch (Throwable ignored) { }
            try { recorder.release(); } catch (Throwable ignored) { }
            try { sink.flush(); } catch (Throwable ignored) { }
            try { if (socket != null) socket.close(); } catch (Throwable ignored) { }
            System.err.println("nluemix-server: 退出，共输出 " + total + " 字节");
        }
    }

    /**
     * 尽力申请 CPU 唤醒锁，锁屏后 CPU 不被挂起。
     *
     * <p>app_process 没有 Context，PowerManager 是隐藏 API，所以走
     * ActivityThread.systemMain() 拿系统 Context —— 全程反射，任何一步失败都只是
     * 少一层保险，不影响采集本身，因此这里把所有异常都吞掉并如实打日志。</p>
     */
    private static Object acquireWakeLock() {
        try {
            Class<?> atClass = Class.forName("android.app.ActivityThread");
            Object at = atClass.getMethod("systemMain").invoke(null);
            android.content.Context ctx =
                    (android.content.Context) atClass.getMethod("getSystemContext").invoke(at);
            if (ctx == null) {
                System.err.println("nluemix-server: 拿不到系统 Context，跳过唤醒锁");
                return null;
            }
            android.os.PowerManager pm =
                    (android.os.PowerManager) ctx.getSystemService(android.content.Context.POWER_SERVICE);
            if (pm == null) {
                System.err.println("nluemix-server: 拿不到 PowerManager，跳过唤醒锁");
                return null;
            }
            android.os.PowerManager.WakeLock wl =
                    pm.newWakeLock(android.os.PowerManager.PARTIAL_WAKE_LOCK, "NlueMix:shell");
            wl.setReferenceCounted(false);
            wl.acquire(6 * 60 * 60 * 1000L);      // 6 小时上限，防泄漏
            System.err.println("nluemix-server: 已持有 CPU 唤醒锁（锁屏后可继续采集）");
            return wl;
        } catch (Throwable t) {
            System.err.println("nluemix-server: 唤醒锁申请失败（锁屏可能断流）: " + t);
            return null;
        }
    }

    private static void releaseWakeLock(Object lock) {
        try {
            if (lock instanceof android.os.PowerManager.WakeLock) {
                android.os.PowerManager.WakeLock wl = (android.os.PowerManager.WakeLock) lock;
                if (wl.isHeld()) wl.release();
            }
        } catch (Throwable ignored) {
        }
    }

    /** 按 NlueMix 协议打包：16 字节小端包头 + PCM16 */
    private static final class FramedSink extends BufferedOutputStream {
        private final byte[] header = new byte[16];

        FramedSink(OutputStream out) { super(out, 1 << 16); }

        void writeFrame(byte[] pcm, int length, int seq, int samples) throws Exception {
            header[0] = (byte) (MAGIC & 0xFF);
            header[1] = (byte) ((MAGIC >> 8) & 0xFF);
            header[2] = (byte) ((MAGIC >> 16) & 0xFF);
            header[3] = (byte) ((MAGIC >> 24) & 0xFF);
            header[4] = 1;                       // 版本
            header[5] = 0;                       // 编码 PCM16
            header[6] = 0;
            header[7] = 0;
            header[8] = (byte) (seq & 0xFF);
            header[9] = (byte) ((seq >> 8) & 0xFF);
            header[10] = (byte) ((seq >> 16) & 0xFF);
            header[11] = (byte) ((seq >> 24) & 0xFF);
            header[12] = (byte) (samples & 0xFF);
            header[13] = (byte) ((samples >> 8) & 0xFF);
            header[14] = 0;
            header[15] = 0;
            write(header, 0, 16);
            write(pcm, 0, length);
            flush();
        }
    }

    private static AudioRecord createRecorder(int rate) {
        int minBuf = AudioRecord.getMinBufferSize(
                rate, AudioFormat.CHANNEL_IN_STEREO, AudioFormat.ENCODING_PCM_16BIT);
        int bufSize = Math.max(minBuf > 0 ? minBuf * 4 : 0, FRAME_BYTES * 8);

        try {
            AudioRecord r = new AudioRecord.Builder()
                    .setAudioSource(MediaRecorder.AudioSource.REMOTE_SUBMIX)
                    .setAudioFormat(new AudioFormat.Builder()
                            .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                            .setSampleRate(rate)
                            .setChannelMask(AudioFormat.CHANNEL_IN_STEREO)
                            .build())
                    .setBufferSizeInBytes(bufSize)
                    .build();
            if (r.getState() == AudioRecord.STATE_INITIALIZED) return r;
            System.err.println("nluemix-server: Builder 方式 state=" + r.getState() + "，改用构造器");
            try { r.release(); } catch (Throwable ignored) { }
        } catch (Throwable t) {
            System.err.println("nluemix-server: Builder 方式失败: " + t);
        }

        try {
            AudioRecord r = new AudioRecord(
                    MediaRecorder.AudioSource.REMOTE_SUBMIX,
                    rate, AudioFormat.CHANNEL_IN_STEREO,
                    AudioFormat.ENCODING_PCM_16BIT, bufSize);
            if (r.getState() == AudioRecord.STATE_INITIALIZED) return r;
            System.err.println("nluemix-server: 构造器方式 state=" + r.getState());
            try { r.release(); } catch (Throwable ignored) { }
        } catch (Throwable t) {
            System.err.println("nluemix-server: 构造器方式失败: " + t);
        }
        return null;
    }
}
