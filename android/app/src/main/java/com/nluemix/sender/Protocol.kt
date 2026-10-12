package com.nluemix.sender

import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket

/**
 * NlueMix 局域网串流协议 v1 —— 电脑端是 Network/Protocol.cs，两边必须严格一致。
 *
 * 控制通道走 TCP（可靠，传握手和心跳）
 * 音频通道走 UDP（低延迟；丢几个包听不出来，重传反而会让延迟越积越多）
 */
object Protocol {
    const val VERSION = 1
    const val CONTROL_PORT = 47820
    const val AUDIO_PORT = 47821
    const val MAGIC = 0x4D424C55.toInt()
    const val SAMPLE_RATE = 48000
    const val CHANNELS = 2
    /** 20 ms 一包 = 960 个采样（48 kHz 下） */
    const val FRAME_SAMPLES = 960
    const val CODEC_PCM16 = 0
    const val HEADER_SIZE = 16
    const val MAX_PACKET = HEADER_SIZE + FRAME_SAMPLES * CHANNELS * 2

    /** 往 buffer 头部写入 16 字节包头（小端） */
    fun buildAudioPacket(buffer: ByteArray, sequence: Int, samples: Int) {
        buffer[0] = (MAGIC and 0xFF).toByte()
        buffer[1] = ((MAGIC shr 8) and 0xFF).toByte()
        buffer[2] = ((MAGIC shr 16) and 0xFF).toByte()
        buffer[3] = ((MAGIC shr 24) and 0xFF).toByte()
        buffer[4] = VERSION.toByte()
        buffer[5] = CODEC_PCM16.toByte()
        buffer[6] = 0
        buffer[7] = 0
        buffer[8] = (sequence and 0xFF).toByte()
        buffer[9] = ((sequence shr 8) and 0xFF).toByte()
        buffer[10] = ((sequence shr 16) and 0xFF).toByte()
        buffer[11] = ((sequence shr 24) and 0xFF).toByte()
        buffer[12] = (samples and 0xFF).toByte()
        buffer[13] = ((samples shr 8) and 0xFF).toByte()
        buffer[14] = 0
        buffer[15] = 0
    }
}

enum class ConnState { IDLE, CONNECTING, CAPTURING, ERROR }

/**
 * 发送端：TCP 握手 + UDP 发音频。
 * 网络操作都是阻塞的，必须在后台线程调用。
 */
class SenderClient(
    private val host: String,
    private val pairCode: String,
    private val deviceName: String,
    private val useTcpAudio: Boolean = false,
    private val onState: (ConnState, String) -> Unit
) {
    private var tcp: Socket? = null
    private var audioTcp: Socket? = null
    private var audioOut: java.io.OutputStream? = null
    private var udp: DatagramSocket? = null
    private var target: InetAddress? = null
    private var sequence = 0
    @Volatile private var running = false
    private val packetBuffer = ByteArray(Protocol.MAX_PACKET)

    fun connect(deviceId: String): Boolean {
        return try {
            onState(ConnState.CONNECTING, "正在连接 $host …")

            val socket = Socket()
            socket.tcpNoDelay = true
            socket.connect(InetSocketAddress(host, Protocol.CONTROL_PORT), 6000)
            tcp = socket

            val hello = listOf(
                "HELLO", Protocol.VERSION.toString(),
                deviceId.replace("\t", " "), deviceName.replace("\t", " "),
                pairCode, Protocol.SAMPLE_RATE.toString(),
                Protocol.CHANNELS.toString(), Protocol.CODEC_PCM16.toString(),
                if (useTcpAudio) "tcp" else "udp"
            ).joinToString("\t") + "\n"
            socket.getOutputStream().write(hello.toByteArray(Charsets.UTF_8))
            socket.getOutputStream().flush()

            val reader = socket.getInputStream().bufferedReader(Charsets.UTF_8)
            val reply = reader.readLine()
            if (reply == null) {
                onState(ConnState.ERROR, "电脑没有回应")
                return false
            }

            when {
                reply.startsWith("WELCOME") -> {
                    val slot = reply.split("\t").getOrNull(1) ?: "?"
                    onState(ConnState.CONNECTING, "已连上电脑（位置 $slot），准备采集音频")
                }
                reply.startsWith("REJECT") -> {
                    val why = reply.split("\t").getOrNull(1) ?: "未知原因"
                    onState(ConnState.ERROR, "被电脑拒绝：$why")
                    close()
                    return false
                }
                else -> {
                    onState(ConnState.ERROR, "电脑回了看不懂的内容：$reply")
                    close()
                    return false
                }
            }

            target = InetAddress.getByName(host)

            if (useTcpAudio) {
                // ★ 修复：以前这里会【再开一条 TCP 连接】专门传音频
                //   （用 deviceId + "-audio" 重新握手一次）。后果：
                //     · 电脑端把这条连接当成"第二台手机"，一台手机就吃掉两个位置
                //       （一共只有 3 个），第二台手机直接被拒"位置已满"；
                //     · 界面上还会多出一张 "<手机名> (音频)" 的假卡片；
                //     · 那条空闲的控制连接在电脑端会干等 8 秒超时。
                //   其实完全没必要：握手用的这条 TCP 连接本来就已经是"tcp"模式，
                //   电脑端收到 WELCOME 后就直接在这条连接上收音频帧了。
                //   所以音频直接复用同一条连接。
                onState(ConnState.CONNECTING, "TCP 音频通道已就绪（复用同一条连接）")
                audioTcp = socket
                audioOut = java.io.BufferedOutputStream(socket.getOutputStream(), 1 shl 16)
            } else {
                udp = DatagramSocket().apply { sendBufferSize = 1 shl 20 }
            }

            running = true
            sequence = 0
            true
        } catch (e: Exception) {
            onState(ConnState.ERROR, "连不上电脑：${e.message ?: e.javaClass.simpleName}")
            close()
            false
        }
    }

    fun sendPcm(pcm: ByteArray, length: Int) {
        if (!running || length <= 0) return

        try {
            var offset = 0
            val bytesPerFrame = Protocol.FRAME_SAMPLES * Protocol.CHANNELS * 2
            while (offset < length) {
                val chunk = minOf(length - offset, bytesPerFrame)
                val samples = chunk / (Protocol.CHANNELS * 2)

                Protocol.buildAudioPacket(packetBuffer, sequence, samples)
                System.arraycopy(pcm, offset, packetBuffer, Protocol.HEADER_SIZE, chunk)

                if (useTcpAudio) {
                    val out = audioOut ?: return
                    out.write(packetBuffer, 0, Protocol.HEADER_SIZE + chunk)
                } else {
                    val socket = udp ?: return
                    val address = target ?: return
                    socket.send(DatagramPacket(packetBuffer, Protocol.HEADER_SIZE + chunk, address, Protocol.AUDIO_PORT))
                }
                sequence++
                offset += chunk
            }
            // TCP 模式需要主动 flush（缓冲流）
            if (useTcpAudio) audioOut?.flush()
        } catch (e: Exception) {
            onState(ConnState.ERROR, "发送音频失败：${e.message}")
        }
    }

    /**
     * 静音保活包。
     *
     * 手机没声音时【不能一个字节都不发】：电脑端靠"有没有数据"判断手机还在不在，
     * 6 秒没数据就把这台手机标成离线，8 秒没数据直接把 TCP 断掉，用户就得重新连一次。
     *
     * 这里发一个完全合法的音频包：包头 + 1 个静音采样（共 20 字节），
     * 每 0.5 秒一次 → 只有 40 字节/秒，比静音时硬发 PCM（1.5 Mbps）省得多。
     * 注意不能发 0 采样：电脑端的包校验会把 Samples==0 判为非法包直接丢掉。
     */
    /**
     * 诊断上报：把设备侧的真实情况（谁在播放、它的 AudioAttributes.flags 是多少）
     * 通过这条已建立的控制连接发给电脑。
     *
     * 为什么需要它：手机没开 adb（无线调试）时电脑端读不到 logcat，
     * 而"B 站声音为什么采不到"只能靠设备侧数据定性。
     * 走控制通道既不需要 adb，也不需要额外端口。
     *
     * 注意：TCP 音频模式下这条连接的输出流正在传二进制音频帧，
     * 往里插文本会把音频流冲乱 —— 所以那种模式直接跳过（UDP 模式才是默认）。
     */
    fun sendDiag(text: String) {
        if (useTcpAudio) return
        try {
            val socket = tcp ?: return
            val clean = text.replace('\n', ' ').replace('\r', ' ').replace('\t', ' ')
            val line = "DIAG\t" + clean + "\n"
            socket.getOutputStream().write(line.toByteArray(Charsets.UTF_8))
            socket.getOutputStream().flush()
        } catch (e: Exception) {
            // 诊断上报失败绝不能影响主流程
        }
    }

    /**
     * 把自动配置读到的无线调试信息发给电脑。
     *
     * 这是「先零调试连上、再自动升级到全功能」闭环的关键一步：
     *   手机端无障碍读出端口 → App 路径本来就能连上电脑（不需要 adb）
     *   → 顺手把端口/配对码送去 → 电脑执行 adb pair/connect
     *   → 之后就能走 shell 路径，采到 B 站这类没声明允许采集的应用。
     *
     * 配对只需要做一次，之后的连接不需要配对码。
     */
    fun sendAutoCfg(endpoint: String, pairPort: String, pairCode: String) {
        if (useTcpAudio) return
        try {
            val socket = tcp ?: return
            val parts = endpoint.split(":")
            if (parts.size != 2) return
            val line = "AUTOCFG\t" + parts[0] + "\t" + parts[1] + "\t" + pairPort + "\t" + pairCode + "\n"
            socket.getOutputStream().write(line.toByteArray(Charsets.UTF_8))
            socket.getOutputStream().flush()
        } catch (e: Exception) {
            // 上报失败不影响采集本身
        }
    }

    fun sendKeepAlive() {
        if (!running) return
        try {
            Protocol.buildAudioPacket(packetBuffer, sequence, 1)
            packetBuffer[Protocol.HEADER_SIZE] = 0
            packetBuffer[Protocol.HEADER_SIZE + 1] = 0
            packetBuffer[Protocol.HEADER_SIZE + 2] = 0
            packetBuffer[Protocol.HEADER_SIZE + 3] = 0
            val total = Protocol.HEADER_SIZE + 4

            if (useTcpAudio) {
                val out = audioOut ?: return
                out.write(packetBuffer, 0, total)
                out.flush()
            } else {
                val socket = udp ?: return
                val address = target ?: return
                socket.send(DatagramPacket(packetBuffer, total, address, Protocol.AUDIO_PORT))
            }
            sequence++
        } catch (e: Exception) {
            // 保活失败不算致命，下一次再试
        }
    }

    fun close() {
        running = false
        try { audioOut?.close() } catch (_: Exception) {}
        try { audioTcp?.close() } catch (_: Exception) {}
        try { tcp?.close() } catch (_: Exception) {}
        try { udp?.close() } catch (_: Exception) {}
        audioOut = null
        audioTcp = null
        tcp = null
        udp = null
        target = null
    }
}
