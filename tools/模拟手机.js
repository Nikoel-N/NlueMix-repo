// ============================================================
//  NlueMix 模拟手机 —— 用来测试"手机发声 → 电脑自动让路"
//
//  用法（在 NlueMix 运行时）：
//      node 模拟手机.js <连接码> [秒数]
//
//  例： node 模拟手机.js 9978 20
//      → 会向电脑发送 20 秒的测试音（440 Hz 正弦波）
//      → 电脑声音应该在 0.3 秒内淡出静音，测试音结束后 2 秒恢复
// ============================================================

const dgram = require('dgram');
const net = require('net');
const os = require('os');

const CODE = process.argv[2] || '';
const SECONDS = Number(process.argv[3] || 15);

if (!CODE || CODE.length !== 4) {
  console.log('');
  console.log('用法: node 模拟手机.js <4位连接码> [秒数]');
  console.log('例:   node 模拟手机.js 9978 20');
  console.log('');
  console.log('连接码在 NlueMix 界面右上角，或者看日志里的这一行：');
  console.log('  MainWindow ctor: 设置已读取，连接码 XXXX');
  console.log('');
  process.exit(1);
}

const CONTROL_PORT = 47820;
const AUDIO_PORT = 47821;
const RATE = 48000, CH = 2, FRAME = 960;

// 找出本机 IP（用于显示）
function localIp() {
  for (const list of Object.values(os.networkInterfaces())) {
    for (const i of list || []) {
      if (i.family === 'IPv4' && !i.internal) return i.address;
    }
  }
  return '127.0.0.1';
}

const HOST = process.env.NLUEMIX_HOST || '127.0.0.1';
console.log('');
console.log('=== NlueMix 模拟手机 ===');
console.log('本机 IP   : ' + localIp());
console.log('目标      : ' + HOST + ':' + CONTROL_PORT);
console.log('连接码    : ' + CODE);
console.log('时长      : ' + SECONDS + ' 秒');
console.log('');

const sock = dgram.createSocket('udp4');
const client = net.connect(CONTROL_PORT, HOST);

let welcomed = false;
let buf = '';

client.on('connect', () => {
  console.log('[1/3] TCP 已连接，发送握手…');
  client.write('HELLO\t1\tnode-simulator\t模拟手机\t' + CODE + '\t' + RATE + '\t' + CH + '\t0\n');
});

client.on('data', (data) => {
  buf += data.toString('utf8');
  let idx;
  while ((idx = buf.indexOf('\n')) >= 0) {
    const line = buf.slice(0, idx).trim();
    buf = buf.slice(idx + 1);
    if (!line) continue;
    console.log('[控制通道] ' + line);
    if (line.startsWith('WELCOME')) {
      welcomed = true;
      console.log('[2/3] 握手成功，电脑已接纳本设备');
      startSending();
    }
    if (line.startsWith('REJECT')) {
      console.log('[X] 被拒绝：' + line);
      console.log('    连接码不对？去 NlueMix 界面右上角看一眼。');
      process.exit(1);
    }
  }
});

client.on('error', (e) => {
  console.log('[X] 控制通道错误: ' + e.message);
  if (e.code === 'ECONNREFUSED') {
    console.log('    NlueMix 没在运行，或者没在监听 47820 端口。');
    console.log('    先在电脑上启动 NlueMix.exe。');
  }
  process.exit(1);
});

const packet = Buffer.alloc(16 + FRAME * CH * 2);
let seq = 0;
let sent = 0;
const startedAt = Date.now();
let timer = null;

function startSending() {
  console.log('[3/3] 开始发送测试音（440 Hz 正弦波，20 ms 一包）');
  console.log('');
  console.log('>>> 现在盯着音响：电脑的声音应该在 0.3 秒内淡出消失，');
  console.log('    只剩这个测试音。测试音停止后约 2 秒，电脑声音会回来。');
  console.log('');

  const total = SECONDS * 50; // 20ms 一包
  timer = setInterval(() => {
    const t = (seq * FRAME) / RATE;

    // 淡入淡出包络，避免首尾爆音
    const fadeIn = Math.min(1, (seq * FRAME) / (RATE * 0.15));
    const fadeOut = Math.min(1, Math.max(0, (SECONDS - t) / 0.3));
    const env = fadeIn * fadeOut;

    for (let i = 0; i < FRAME; i++) {
      const tt = t + i / RATE;
      // 用 440Hz 主音 + 880Hz 泛音，听起来更像"音乐"而不是"滴滴声"
      const v = (Math.sin(2 * Math.PI * 440 * tt) * 0.28 +
                 Math.sin(2 * Math.PI * 880 * tt) * 0.10) * env;
      const s = Math.max(-32768, Math.min(32767, Math.round(v * 32767)));
      packet.writeInt16LE(s, 16 + i * 4);
      packet.writeInt16LE(s, 18 + i * 4);
    }

    packet.writeUInt32LE(0x4D424C55, 0);   // 魔术字
    packet[4] = 1;                          // 版本
    packet[5] = 0;                          // 编码 = PCM16
    packet[6] = 0;
    packet[7] = 0;
    packet.writeUInt32LE(seq >>> 0, 8);     // 序号
    packet.writeUInt16LE(FRAME, 12);        // 采样数
    packet.writeUInt16LE(0, 14);

    sock.send(packet, AUDIO_PORT, HOST);
    seq++;
    sent++;

    if (seq % 50 === 0) {
      process.stdout.write('\r已发送 ' + (seq / 50).toFixed(1) + ' / ' + SECONDS + ' 秒 ');
    }

    if (seq >= total) {
      clearInterval(timer);
      finish();
    }
  }, 20);
}

function finish() {
  console.log('');
  console.log('');
  console.log('=== 发送完毕 ===');
  console.log('共发送 ' + sent + ' 个包（' + SECONDS + ' 秒音频）');
  console.log('');
  console.log('现在听：电脑的声音应该在 2 秒内自动恢复。');
  console.log('如果恢复了 → 让路功能正常 ✅');
  console.log('如果没恢复 → 说明 SilenceDetector 或 Mixer 有问题，把 NlueMix 的日志发我。');
  console.log('');
  client.write('BYE\n');
  setTimeout(() => { try { client.end(); } catch {} sock.close(); process.exit(0); }, 500);
}
