using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace VoiceDock.Services;

/// <summary>
/// Web Speech API ブリッジのローカルサーバー。
/// 127.0.0.1 上で認識用 HTML を配信し、WebSocket でブラウザ(Edge)と双方向通信する。
/// ブラウザ側が Web Speech API で音声認識を行い、確定/暫定テキストと音量レベルを送ってくる。
/// 本体からは「開始」「停止」コマンドを送る。
/// </summary>
public sealed class SpeechBridgeServer : IDisposable
{
    private readonly LogService _log;

    /// <summary>WebSocket 接続用のトークン。ページの中にだけ埋め込み、外には出さない。</summary>
    private readonly string _token = Guid.NewGuid().ToString("N");

    /// <summary>
    /// 認識ページを 1 回だけ取得できる使い捨てトークン。
    /// ページの URL はブラウザの起動引数に載るため、同じ PC の別プロセスから
    /// コマンドラインを読まれる可能性がある。使い捨てにしておけば、読まれた時点では
    /// 既に消費済みでページを取得できず、WebSocket 用トークンも盗まれない。
    /// </summary>
    private string _pageToken = "";

    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private HttpListener? _listener;
    private WebSocket? _socket;
    private CancellationTokenSource? _cts;
    private int _port;

    /// <summary>
    /// 認識ページの URL を新しく発行する。呼ぶたびに使い捨てトークンが更新されるため、
    /// ブラウザを起動・再起動するたびにこれを呼ぶこと。
    /// </summary>
    public string IssuePageUrl()
    {
        _pageToken = Guid.NewGuid().ToString("N");
        return $"http://127.0.0.1:{_port}/?token={_pageToken}";
    }

    /// <summary>ブラウザ(WebSocket)が接続済みかどうか。</summary>
    public bool IsConnected => _socket?.State == WebSocketState.Open;

    /// <summary>
    /// 実際に使われているマイクの名前（まだ録音していない場合は null）。
    /// Web Speech API にはマイクを指定する手段が無く、常に Windows の既定デバイスが使われる。
    /// どのマイクが使われているかは、録音を開始したときに初めて分かる。
    /// </summary>
    public string? CurrentMicrophone { get; private set; }

    /// <summary>使用中のマイクが変わったときに発火（引数は 変更前, 変更後）。</summary>
    public event Action<string?, string>? MicrophoneChanged;

    /// <summary>確定した認識テキスト。</summary>
    public event Action<string>? FinalText;

    /// <summary>暫定（認識途中）の認識テキスト。</summary>
    public event Action<string>? PartialText;

    /// <summary>音量レベル (RMS 0..1)。オーバーレイの波形用。</summary>
    public event Action<float>? LevelChanged;

    /// <summary>ブラウザが接続し、認識準備が整ったとき。</summary>
    public event Action? Ready;

    /// <summary>認識エラー（Web Speech API の error など）。</summary>
    public event Action<string>? RecognitionError;

    /// <summary>
    /// 実際の認識モードの通知。
    /// "local"=端末内処理 / "downloading"=言語パック取得中（今回はクラウド）
    /// / "unsupported"=ローカル非対応（クラウド） / "cloud"=クラウド指定。
    /// </summary>
    public event Action<string>? RecognitionModeReported;

    public SpeechBridgeServer(LogService log)
    {
        _log = log;
    }

    public void Start()
    {
        _port = FindFreePort();
        _cts = new CancellationTokenSource();

        _listener = new HttpListener();
        // 特定 IP + ポートなら管理者権限・URL 予約なしでバインドできる
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _listener.Start();
        _log.Info($"認識ブリッジを起動しました (127.0.0.1:{_port})");

        _ = AcceptLoopAsync(_cts.Token);
    }

    /// <summary>ブラウザに認識開始を指示する。</summary>
    /// <param name="processLocally">
    /// 端末内での認識（Web Speech API の processLocally）を要求するかどうか。
    /// 非対応環境や言語パック未導入の場合はブラウザ側でクラウド認識にフォールバックする。
    /// </param>
    public Task StartRecognitionAsync(bool processLocally = false) =>
        SendCommandAsync("start", $",\"processLocally\":{(processLocally ? "true" : "false")}");

    /// <summary>ブラウザに認識停止を指示する。</summary>
    public Task StopRecognitionAsync() => SendCommandAsync("stop");

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Error($"認識ブリッジの受付でエラー: {ex.Message}");
                continue;
            }

            _ = HandleRequestAsync(ctx, ct);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            if (ctx.Request.Url?.AbsolutePath == "/ws" && ctx.Request.IsWebSocketRequest)
            {
                if (ctx.Request.QueryString["token"] != _token)
                {
                    ctx.Response.StatusCode = 403;
                    ctx.Response.Close();
                    return;
                }
                var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null);
                // 再接続時に古い接続を残さない
                var previous = _socket;
                _socket = wsCtx.WebSocket;
                if (previous != null)
                {
                    try { previous.Abort(); previous.Dispose(); } catch { /* 無視 */ }
                }
                _log.Info("ブラウザが認識ブリッジに接続しました");
                await ReceiveLoopAsync(_socket, ct);
            }
            else
            {
                // ページは使い捨てトークンでのみ取得できる。取得した時点で無効化する
                var pageToken = _pageToken;
                if (pageToken.Length == 0 || ctx.Request.QueryString["token"] != pageToken)
                {
                    ctx.Response.StatusCode = 403;
                    ctx.Response.Close();
                    return;
                }
                _pageToken = "";

                var html = Encoding.UTF8.GetBytes(BuildPageHtml());
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.ContentLength64 = html.Length;
                await ctx.Response.OutputStream.WriteAsync(html, ct);
                ctx.Response.Close();
            }
        }
        catch (Exception ex)
        {
            _log.Error($"認識ブリッジのリクエスト処理でエラー: {ex.Message}");
            try { ctx.Response.Abort(); } catch { /* 無視 */ }
        }
    }

    private async Task ReceiveLoopAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var sb = new StringBuilder();
        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                sb.Clear();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                        return;
                    }
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);

                HandleMessage(sb.ToString());
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // 終了時は無視
        }
        catch (Exception ex)
        {
            _log.Warn($"認識ブリッジの受信が切断されました: {ex.Message}");
        }
    }

    private void HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            switch (type)
            {
                case "final":
                    if (root.TryGetProperty("text", out var ft))
                        FinalText?.Invoke(ft.GetString() ?? "");
                    break;
                case "partial":
                    if (root.TryGetProperty("text", out var pt))
                        PartialText?.Invoke(pt.GetString() ?? "");
                    break;
                case "level":
                    if (root.TryGetProperty("value", out var lv) && lv.TryGetDouble(out var d))
                        LevelChanged?.Invoke((float)d);
                    break;
                case "status":
                    var state = root.TryGetProperty("state", out var st) ? st.GetString() : "";
                    if (state == "ready") Ready?.Invoke();
                    break;
                case "mode":
                    if (root.TryGetProperty("value", out var mv))
                        RecognitionModeReported?.Invoke(mv.GetString() ?? "");
                    break;
                case "mic":
                    if (root.TryGetProperty("label", out var ml))
                    {
                        var label = ml.GetString() ?? "";
                        if (label.Length > 0 && label != CurrentMicrophone)
                        {
                            var previous = CurrentMicrophone;
                            CurrentMicrophone = label;
                            MicrophoneChanged?.Invoke(previous, label);
                        }
                    }
                    break;
                case "error":
                    var detail = root.TryGetProperty("detail", out var de) ? de.GetString() : "";
                    RecognitionError?.Invoke(detail ?? "");
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"認識ブリッジのメッセージ解析に失敗: {ex.Message}");
        }
    }

    /// <param name="extraJson">"cmd" に続けて連結する追加の JSON（先頭にカンマを含める）。</param>
    private async Task SendCommandAsync(string cmd, string extraJson = "")
    {
        var socket = _socket;
        if (socket is not { State: WebSocketState.Open })
        {
            _log.Warn($"ブラウザ未接続のため「{cmd}」を送信できませんでした");
            return;
        }

        var payload = Encoding.UTF8.GetBytes($"{{\"cmd\":\"{cmd}\"{extraJson}}}");
        await _sendLock.WaitAsync();
        try
        {
            await socket.SendAsync(new ArraySegment<byte>(payload),
                WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.Warn($"認識ブリッジへの送信に失敗: {ex.Message}");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static int FindFreePort()
    {
        // ポート 0 で一時的にバインドして OS に空きポートを割り当ててもらう
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        return port;
    }

    private string BuildPageHtml()
    {
        // 認識ページ。ws の URL とトークンを埋め込む。
        return HtmlTemplate
            .Replace("__PORT__", _port.ToString())
            .Replace("__TOKEN__", _token);
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { /* 無視 */ }
        try { _socket?.Abort(); } catch { /* 無視 */ }
        try { _listener?.Stop(); } catch { /* 無視 */ }
        try { _listener?.Close(); } catch { /* 無視 */ }
        _cts?.Dispose();
    }

    // ブラウザ側の認識ロジック。Web Speech API (webkitSpeechRecognition) を日本語・連続認識で動かし、
    // 確定/暫定テキストと音量レベルを WebSocket で本体へ送る。
    private const string HtmlTemplate = """
<!doctype html>
<html lang="ja">
<head>
<meta charset="utf-8">
<title>VoiceDock Recognizer</title>
<style>
  html,body{margin:0;background:#1b1b1f;color:#e8e8ea;font-family:'Yu Gothic UI',sans-serif;
    display:flex;align-items:center;justify-content:center;height:100vh;font-size:12px;}
</style>
</head>
<body>
<div id="s">VoiceDock recognizer</div>
<script>
  const WS_URL = "ws://127.0.0.1:__PORT__/ws?token=__TOKEN__";
  let ws, recog, shouldListen = false;
  let audioCtx, analyser, micStream, levelTimer;

  function send(o){ try{ if(ws && ws.readyState===1) ws.send(JSON.stringify(o)); }catch(_){} }

  function connect(){
    ws = new WebSocket(WS_URL);
    ws.onopen = () => send({type:'status', state:'ready'});
    ws.onmessage = (e) => {
      let m; try{ m = JSON.parse(e.data); }catch(_){ return; }
      if(m.cmd === 'start') startRecog(!!m.processLocally);
      else if(m.cmd === 'stop') stopRecog();
    };
    ws.onclose = () => setTimeout(connect, 1000);
    ws.onerror = () => { try{ ws.close(); }catch(_){} };
  }

  function getSR(){ return window.SpeechRecognition || window.webkitSpeechRecognition; }

  // 端末内での認識(processLocally)が使えるかを判定し、可能なら有効化する。
  // 言語パック未導入なら裏で取得を開始し、今回はクラウド認識で動かす。
  async function applyLocalMode(r, want){
    if(!want){ send({type:'mode', value:'cloud'}); return; }
    const SR = getSR();
    if(!('processLocally' in r)){ send({type:'mode', value:'unsupported'}); return; }
    try{
      const opts = {langs:['ja-JP'], processLocally:true};
      let st = null;
      if(typeof SR.available === 'function') st = await SR.available(opts);
      else if(typeof SR.availableOnDevice === 'function') st = await SR.availableOnDevice('ja-JP');

      if(st === 'available'){
        r.processLocally = true;
        send({type:'mode', value:'local'});
        return;
      }
      if(st === 'downloadable' || st === 'downloading'){
        // 言語パックの導入を裏で開始（完了後の起動からローカル処理になる）
        try{
          if(typeof SR.install === 'function') SR.install({langs:['ja-JP']}).catch(()=>{});
          else if(typeof SR.installOnDevice === 'function') SR.installOnDevice('ja-JP');
        }catch(_){}
        send({type:'mode', value:'downloading'});
        return;
      }
    }catch(_){}
    send({type:'mode', value:'unsupported'});
  }

  function setupRecog(){
    const SR = getSR();
    if(!SR){ send({type:'error', detail:'speech-unsupported'}); return null; }
    const r = new SR();
    r.lang = 'ja-JP';
    r.continuous = true;
    r.interimResults = true;
    r.onresult = (ev) => {
      for(let i = ev.resultIndex; i < ev.results.length; i++){
        const res = ev.results[i];
        const text = res[0].transcript;
        if(res.isFinal) send({type:'final', text:text});
        else send({type:'partial', text:text});
      }
    };
    r.onerror = (ev) => { if(ev.error !== 'no-speech') send({type:'error', detail:ev.error}); };
    // 連続認識は無音などで自動終了することがあるため、継続希望なら再開する
    r.onend = () => { if(shouldListen){ try{ r.start(); }catch(_){} } };
    return r;
  }

  async function startRecog(processLocally){
    shouldListen = true;
    if(!recog) recog = setupRecog();
    if(!recog) return;
    // ローカル処理の可否判定（非対応・未導入なら自動でクラウドにフォールバック）
    await applyLocalMode(recog, processLocally);
    if(!shouldListen) return;   // 判定中に停止された場合
    try{ recog.start(); }catch(_){ /* 既に開始済みなら無視 */ }
    send({type:'status', state:'listening'});
    startLevel();
  }

  function stopRecog(){
    shouldListen = false;
    try{ recog && recog.stop(); }catch(_){}
    stopLevel();
    send({type:'status', state:'stopped'});
  }

  async function startLevel(){
    if(micStream) return;
    try{
      micStream = await navigator.mediaDevices.getUserMedia({audio:true});
      reportMic();
      audioCtx = new (window.AudioContext || window.webkitAudioContext)();
      const src = audioCtx.createMediaStreamSource(micStream);
      analyser = audioCtx.createAnalyser();
      analyser.fftSize = 512;
      src.connect(analyser);
      const buf = new Uint8Array(analyser.frequencyBinCount);
      levelTimer = setInterval(() => {
        analyser.getByteTimeDomainData(buf);
        let sum = 0;
        for(const v of buf){ const x = (v - 128) / 128; sum += x * x; }
        send({type:'level', value: Math.sqrt(sum / buf.length)});
      }, 60);
    }catch(_){ /* レベル取得失敗は致命的ではない */ }
  }

  // どのマイクが使われているかを本体へ知らせる。
  // Web Speech API はデバイスを指定できないため、実際に掴んだストリームから名前を読む。
  function reportMic(){
    try{
      const t = micStream && micStream.getAudioTracks()[0];
      if(t && t.label) send({type:'mic', label:t.label});
    }catch(_){}
  }

  // 既定マイクが差し替わったら掴み直す。
  // 録音していないときは、次に開始した時点で新しい既定マイクが使われる。
  async function onDeviceChange(){
    if(!shouldListen) return;
    stopLevel();
    await startLevel();
    // 認識のストリームも開き直す（onend で自動的に再開される）
    try{ recog && recog.stop(); }catch(_){}
  }

  try{
    let micChangeTimer = null;
    navigator.mediaDevices.addEventListener('devicechange', () => {
      // 抜き差しでは何度もまとめて発火するため、落ち着くまで待つ
      if(micChangeTimer) clearTimeout(micChangeTimer);
      micChangeTimer = setTimeout(onDeviceChange, 800);
    });
  }catch(_){ /* 対応していない環境では何もしない */ }

  function stopLevel(){
    if(levelTimer){ clearInterval(levelTimer); levelTimer = null; }
    if(micStream){ micStream.getTracks().forEach(t => t.stop()); micStream = null; }
    if(audioCtx){ try{ audioCtx.close(); }catch(_){} audioCtx = null; }
  }

  connect();
</script>
</body>
</html>
""";
}
