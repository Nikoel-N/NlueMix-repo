// ============================================================
//  NlueMix 安全测试 —— 验证三道防线是否真的生效
//  用法: node 安全测试.js <正确连接码>
// ============================================================
const net = require('net');
const dgram = require('dgram');

const CODE = process.argv[2] || '';
const HOST = '127.0.0.1';
const CONTROL = 47820, AUDIO = 47821;

function handshake(code) {
  return new Promise((resolve) => {
    const s = net.connect(CONTROL, HOST);
    let buf = '';
    let done = false;
    const finish = (r) => { if (!done) { done = true; try { s.destroy(); } catch {} resolve(r); } };
    s.setTimeout(4000, () => finish('TIMEOUT'));
    s.on('connect', () => {
      s.write(['HELLO','1','sec-test','安全测试','' + code,'48000','2','0'].join('\t') + '\n');
    });
    s.on('data', (d) => {
      buf += d.toString('utf8');
      const i = buf.indexOf('\n');
      if (i >= 0) finish(buf.slice(0, i).trim());
    });
    s.on('error', (e) => finish('ERR:' + e.code));
  });
}

(async () => {
  console.log('');
  console.log('=== NlueMix 安全测试 ===');
  console.log('正确连接码: ' + CODE);
  console.log('');

  // ── 防线① 握手限流
  console.log('【防线①】握手失败限流（错 5 次应该被锁）');
  console.log('--------------------------------------------------');
  for (let i = 1; i <= 5; i++) {
    const r = await handshake('0000');
    console.log('  第 ' + i + ' 次错误尝试 → ' + r);
    await new Promise(res => setTimeout(res, 150));
  }
  console.log('');
  const afterLock = await handshake('0000');
  console.log('  被锁后再试 → ' + afterLock);
  const good = await handshake(CODE);
  console.log('  锁定期内用【正确】连接码 → ' + good);
  console.log('');
  const locked = /锁定|过多|等 60 秒/.test(afterLock);
  const blockedGood = /锁定|过多|等 60 秒/.test(good);
  const correctMsgSeen = /还能试/.test('x'); // 占位
  if (locked && blockedGood) {
    console.log('  ✅ 防线①生效：5 次错误后被锁，且锁定期内【正确的码】也进不来');
  } else {
    console.log('  ❌ 防线①没生效  locked=' + locked + ' blockedGood=' + blockedGood);
  }
  console.log('');

  // ── 防线③ 包校验
  console.log('【防线③】畸形 UDP 包校验');
  console.log('--------------------------------------------------');
  const sock = dgram.createSocket('udp4');
  const send = (buf, label) => new Promise(res => {
    sock.send(buf, AUDIO, HOST, () => { console.log('  已发送: ' + label + '（' + buf.length + ' 字节）'); res(); });
  });

  // 魔术字错误
  const bad1 = Buffer.alloc(3860);
  bad1.writeUInt32LE(0xDEADBEEF, 0);
  bad1[4] = 1;
  await send(bad1, '魔术字错误');

  // 超大包
  const bad2 = Buffer.alloc(70000);
  bad2.writeUInt32LE(0x4D424C55, 0);
  bad2[4] = 1;
  await send(bad2, '超大包 70000 字节');

  // 采样数离谱
  const bad3 = Buffer.alloc(3860);
  bad3.writeUInt32LE(0x4D424C55, 0);
  bad3[4] = 1;
  bad3.writeUInt16LE(60000, 12);
  await send(bad3, '采样数声明 60000');

  // 负载截断（声称 960 采样 = 3840 字节，实际只给 100）
  const bad4 = Buffer.alloc(116);
  bad4.writeUInt32LE(0x4D424C55, 0);
  bad4[4] = 1;
  bad4.writeUInt16LE(960, 12);
  await send(bad4, '负载截断（声称 3840 只给 100）');

  // 太短
  await send(Buffer.alloc(8), '只有 8 字节');

  sock.close();
  await new Promise(res => setTimeout(res, 1200));
  console.log('');
  console.log('  → 去看 NlueMix 日志有没有「丢弃非法音频包」的记录');
  console.log('');
  console.log('=== 测试结束 ===');
  console.log('');
  console.log('提示：防线②（UDP 源校验）没法在单机上伪造，');
  console.log('      它防的是"别的机器抢注"，需要两台设备才能测。');
  process.exit(0);
})();
