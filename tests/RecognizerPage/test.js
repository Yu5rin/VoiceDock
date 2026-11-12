// 認識用ページ（SpeechBridgeServer.cs に埋め込んだ HTML の script）の動きを、偽の WebSocket・マイク・音声認識で確かめる。
// 実行: node tests/RecognizerPage/test.js
const fs = require('fs');
const path = require('path');

const cs = fs.readFileSync(path.join(__dirname, '../../src/VoiceDock/Services/SpeechBridgeServer.cs'), 'utf8');
const pageJs = cs.slice(cs.indexOf('<script>') + 8, cs.indexOf('</script>'));

let ng = 0, ok = 0;
const check = (label, cond) => { if (cond) ok++; else { ng++; console.log('NG  ' + label); } };
const tick = () => new Promise(r => setImmediate(r));

function load({ devices = [], failIds = [] } = {}) {
  const timers = [];
  const sockets = [];
  class FakeWS { constructor() { this.readyState = 0; this.sent = []; sockets.push(this); }
    send(s) { if (this.readyState !== 1) throw new Error('closed'); this.sent.push(JSON.parse(s)); }
    close() { this.readyState = 3; } }
  const opened = [];
  const track = (id) => ({ kind: 'audio', readyState: 'live', label: (devices.find(d => d.deviceId === id) || { label: '既定のマイク' }).label,
    stop() { this.readyState = 'ended'; } });
  const mediaDevices = {
    addEventListener() {},
    async getUserMedia(c) {
      const id = c.audio && c.audio.deviceId ? c.audio.deviceId.exact : '';
      if (id && (failIds.includes(id) || !devices.some(d => d.deviceId === id))) throw new Error('OverconstrainedError');
      const t = track(id); opened.push(id || 'default');
      return { getAudioTracks: () => [t], getTracks: () => [t] };
    },
    async enumerateDevices() { return devices.map(d => ({ kind: 'audioinput', ...d })); },
  };
  const starts = [];
  class FakeSR { constructor() { this.lang = ''; } start(t) { starts.push(t || null); this.onstart && this.onstart(); } stop() { this.onend && this.onend(); } }
  const sandbox = {
    setTimeout: (f) => { timers.push(f); return 0; }, setInterval: () => 1, clearInterval() {},
    WebSocket: FakeWS, window: { addEventListener() {}, webkitSpeechRecognition: FakeSR, AudioContext: class {
      constructor() { this.sampleRate = 48000; } createMediaStreamSource() { return { connect() {} }; }
      createAnalyser() { return { fftSize: 0, getFloatTimeDomainData() {} }; } close() {} } },
    document: { addEventListener() {}, visibilityState: 'visible' },
    navigator: { onLine: true, mediaDevices }, performance: { now: () => 0 },
  };
  const fn = new Function(...Object.keys(sandbox),
    pageJs.replace('__PORT__', '1').replace('__TOKEN__', 't') +
    '\nreturn { send, pending, onMsg: (m) => ws.onmessage({data: JSON.stringify(m)}), get ws(){ return ws; } };');
  const page = fn(...Object.values(sandbox));
  const ws = sockets[0]; ws.readyState = 1; ws.onopen();
  return { page, ws, sockets, timers, starts, opened };
}

(async () => {
  // ── つながりが切れている間の扱い ──
  {
    const { page, ws, sockets, timers } = load();
    check('つながったら ready を送る', ws.sent[0].type === 'status');
    ws.readyState = 3; ws.onclose({ code: 1006, wasClean: false });
    page.send({ type: 'final', text: 'こんにちは' });
    page.send({ type: 'level', value: 0.1 });
    page.send({ type: 'partial', text: '途中' });
    check('切れている間は結果を取っておく（音量・途中経過は捨てる）', page.pending.length === 1);
    timers.shift()();
    const ws2 = sockets[1]; ws2.readyState = 1; ws2.onopen();
    check('つながり直したら ready・診断・結果の順に送る', ws2.sent.map(m => m.type).join(',') === 'status,diag,final');
    check('診断に切断コードが入る', /code=1006/.test(ws2.sent[1].text));
    for (let i = 0; i < 80; i++) { ws2.readyState = 3; page.send({ type: 'final', text: String(i) }); }
    check('取っておく数には上限がある', page.pending.length <= 50);
  }

  // ── マイクの選択 ──
  const devices = [{ deviceId: 'aaa', label: 'USB マイク' }, { deviceId: 'bbb', label: 'ヘッドセット' }];
  {
    const { page, ws, starts, opened } = load({ devices });
    page.onMsg({ cmd: 'start', processLocally: false, lang: 'ja-JP', mic: '', micLabel: '' });
    await tick(); await tick();
    check('既定のマイクなら音声トラックを渡さずに始める', starts.length === 1 && starts[0] === null);
    check('既定のマイクなら既定を開く', opened[0] === 'default');
  }
  {
    const { page, ws, starts, opened } = load({ devices });
    page.onMsg({ cmd: 'start', processLocally: false, lang: 'ja-JP', mic: 'bbb', micLabel: 'ヘッドセット' });
    for (let i = 0; i < 5; i++) await tick();
    check('選んだマイクを開く', opened[0] === 'bbb');
    check('選んだマイクの音声トラックを渡して始める', starts.length === 1 && starts[0] && starts[0].label === 'ヘッドセット');
    check('選んだマイクが見つかれば警告しない', !ws.sent.some(m => m.type === 'micfallback'));
  }
  {
    // 抜き差しで装置 ID が変わった: 名前で探し直す
    const changed = [{ deviceId: 'ccc', label: 'ヘッドセット' }];
    const { page, ws, starts, opened } = load({ devices: changed });
    page.onMsg({ cmd: 'start', processLocally: false, lang: 'ja-JP', mic: 'bbb', micLabel: 'ヘッドセット' });
    for (let i = 0; i < 8; i++) await tick();
    check('装置 ID が変わっても名前で見つけて開く', opened.includes('ccc'));
    check('名前で見つけたマイクで始める', starts.length === 1 && starts[0] && starts[0].label === 'ヘッドセット');
  }
  {
    // 選んだマイクが無い: 既定で続けて知らせる
    const { page, ws, starts } = load({ devices: [{ deviceId: 'aaa', label: 'USB マイク' }] });
    page.onMsg({ cmd: 'start', processLocally: false, lang: 'ja-JP', mic: 'bbb', micLabel: 'ヘッドセット' });
    for (let i = 0; i < 8; i++) await tick();
    check('選んだマイクが無ければ本体へ知らせる', ws.sent.some(m => m.type === 'micfallback' && m.label === 'ヘッドセット'));
    check('選んだマイクが無ければ既定のマイクで始める', starts.length === 1 && starts[0] === null);
  }
  {
    // 一覧を送る
    const { page, ws } = load({ devices });
    page.onMsg({ cmd: 'devices' });
    for (let i = 0; i < 5; i++) await tick();
    const msg = ws.sent.find(m => m.type === 'devices');
    check('マイクの一覧を送る', msg && msg.list.length === 2 && msg.list[1].label === 'ヘッドセット' && msg.list[1].id === 'bbb');
  }

  console.log(`OK ${ok} 件 / NG ${ng} 件`);
  console.log(ng ? `=== ${ng} 件 失敗 ===` : '=== すべて合格 ===');
  process.exit(ng ? 1 : 0);
})();
