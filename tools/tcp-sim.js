// TCP 模式的模拟手机（验证电脑端的 TCP 音频通道）
const net = require('net');
const HOST = '127.0.0.1', PORT = 47820, RATE = 48000, CH = 2, FRAME = 960;
const code = process.argv[2] || '0000';
const TAB = String.fromCharCode(9);

function hello(sock, id, name, mode) {
  const line = ['HELLO','1',id,name,code,RATE,CH,0,mode].join(TAB) + String.fromCharCode(10);
  sock.write(line);
}

const ctl = net.connect(PORT, HOST, () => hello(ctl, 'tcp-test', 'TCP测试', 'udp'));
ctl.on('data', d => {
  const s = d.toString().trim();
  console.log('控制通道: ' + s);
  if (s.startsWith('WELCOME')) startAudio();
  if (s.startsWith('REJECT')) { console.log('被拒绝'); process.exit(1); }
});
ctl.on('error', e => { console.log('控制错误: ' + e.message); process.exit(1); });

let audio = null, seq = 0;
function startAudio() {
  audio = net.connect(PORT, HOST, () => hello(audio, 'tcp-test-audio', 'TCP测试音频', 'tcp'));
  let buf = '';
  audio.on('data', d => {
    buf += d.toString();
    if (buf.includes('WELCOME')) { console.log('音频通道已就绪 → 开始发送'); send(); }
  });
  audio.on('error', e => console.log('音频通道错误: ' + e.message));
}

const pkt = Buffer.alloc(16 + FRAME * CH * 2);
function send() {
  if (seq >= 200) { console.log('发送完成: ' + seq + ' 包（约 4 秒音频）'); setTimeout(() => process.exit(0), 800); return; }
  const t = seq * FRAME / RATE;
  for (let i = 0; i < FRAME; i++) {
    const v = Math.sin(2 * Math.PI * 440 * (t + i / RATE)) * 0.3;
    const s = Math.round(v * 32767);
    pkt.writeInt16LE(s, 16 + i * 4);
    pkt.writeInt16LE(s, 18 + i * 4);
  }
  pkt.writeUInt32LE(0x4D424C55, 0);
  pkt[4] = 1; pkt[5] = 0; pkt[6] = 0; pkt[7] = 0;
  pkt.writeUInt32LE(seq, 8);
  pkt.writeUInt16LE(FRAME, 12);
  pkt.writeUInt16LE(0, 14);
  audio.write(pkt);
  seq++;
  setTimeout(send, 20);
}
