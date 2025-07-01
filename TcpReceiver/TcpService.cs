using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TcpReceiver
{
    /// <summary>
    /// TCP通信（受信・送信）を管理するクラス
    /// </summary>
    public class TcpService
    {
        private readonly LoggingService loggingService;
        private TcpListener tcpListener;
        private CancellationTokenSource cancellationTokenSource;

        /// <summary>
        /// 設定データを受信したときに発生するイベント
        /// </summary>
        public event Func<string, Task> ConfigReceived;

        /// <summary>
        /// クライアントとの接続状態が変化したときに発生するイベント
        /// </summary>
        public event Action<string> ConnectionStatusChanged;

        public TcpService(LoggingService logger)
        {
            loggingService = logger;
        }

        /// <summary>
        /// TCPリスナーを開始する
        /// </summary>
        public void StartListener(int port)
        {
            try
            {
                cancellationTokenSource = new CancellationTokenSource();
                tcpListener = new TcpListener(IPAddress.Any, port);
                tcpListener.Start();
                Task.Run(() => ListenForConnections(cancellationTokenSource.Token));
                loggingService.AddEntry($"TCP受信開始: ポート {port}");
                ConnectionStatusChanged?.Invoke($"ポート {port} で待機中");
            }
            catch (Exception ex)
            {
                loggingService.AddEntry($"TCP受信開始エラー: {ex.Message}");
                ConnectionStatusChanged?.Invoke("TCP受信エラー");
            }
        }

        /// <summary>
        /// TCPリスナーを停止する
        /// </summary>
        public void StopListener()
        {
            try
            {
                cancellationTokenSource?.Cancel();
                tcpListener?.Stop();
            }
            catch (Exception ex)
            {
                loggingService.AddEntry($"TCP受信停止エラー: {ex.Message}");
            }
        }

        /// <summary>
        /// クライアントからの接続を待機するループ
        /// </summary>
        private async Task ListenForConnections(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using (var client = await tcpListener.AcceptTcpClientAsync())
                    {
                        string clientEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "不明";
                        loggingService.AddEntry($"クライアント接続: {clientEndpoint}");
                        ConnectionStatusChanged?.Invoke("C++アプリから接続受信中...");

                        await HandleClient(client, token);

                        loggingService.AddEntry($"クライアント切断: {clientEndpoint}");
                    }
                }
                catch (ObjectDisposedException) { break; } // 正常終了
                catch (OperationCanceledException) { break; } // 正常終了
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        loggingService.AddEntry($"TCP受信エラー: {ex.Message}");
                        ConnectionStatusChanged?.Invoke("受信エラー");
                        await Task.Delay(1000, token); // 少し待って再試行
                    }
                }
            }
        }

        /// <summary>
        /// 個々のクライアントからのデータを処理する
        /// </summary>
        private async Task HandleClient(TcpClient client, CancellationToken token)
        {
            using (var stream = client.GetStream())
            {
                string lengthHeader = await ReadLineAsync(stream, token);
                if (string.IsNullOrEmpty(lengthHeader))
                {
                    loggingService.AddEntry("空のヘッダーを受信");
                    return;
                }

                if (!int.TryParse(lengthHeader.Trim(), out int expectedLength) || expectedLength < 0)
                {
                    loggingService.AddEntry($"不正なヘッダー形式: {lengthHeader}");
                    ConnectionStatusChanged?.Invoke("不正なヘッダー形式");
                    return;
                }

                // 0バイトの場合は設定要求とみなす
                if (expectedLength == 0)
                {
                    loggingService.AddEntry("設定要求（0バイトデータ）を受信しました。");
                    // TODO: 要求に対する応答ロジックが必要な場合はここに追加
                    return;
                }

                loggingService.AddEntry($"データ長ヘッダー受信: {expectedLength} bytes");

                byte[] buffer = new byte[expectedLength];
                int totalRead = 0;
                while (totalRead < expectedLength && !token.IsCancellationRequested)
                {
                    int bytesRead = await stream.ReadAsync(buffer, totalRead, expectedLength - totalRead, token);
                    if (bytesRead == 0) throw new EndOfStreamException("接続が予期せず終了しました");
                    totalRead += bytesRead;
                }

                if (totalRead == expectedLength)
                {
                    string receivedData = Encoding.UTF8.GetString(buffer, 0, totalRead);
                    loggingService.AddEntry($"設定データ受信完了: {totalRead} bytes");
                    if (ConfigReceived != null)
                    {
                        await ConfigReceived.Invoke(receivedData);
                    }
                }
            }
        }

        /// <summary>
        /// ストリームから1行読み込む
        /// </summary>
        private async Task<string> ReadLineAsync(NetworkStream stream, CancellationToken token)
        {
            var sb = new StringBuilder();
            var buffer = new byte[1];
            while (!token.IsCancellationRequested)
            {
                int bytesRead = await stream.ReadAsync(buffer, 0, 1, token);
                if (bytesRead == 0) break;
                char c = (char)buffer[0];
                if (c == '\n') break;
                if (c != '\r') sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// C++アプリに設定を送信する
        /// </summary>
        public async Task SendConfigAsync(string host, int port, string configString)
        {
            try
            {
                loggingService.AddEntry($"C++アプリへ接続中: {host}:{port}");
                using (var client = new TcpClient())
                {
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 5000;
                    await client.ConnectAsync(host, port);
                    loggingService.AddEntry("C++アプリへ接続成功");

                    using (var stream = client.GetStream())
                    {
                        byte[] configBytes = Encoding.UTF8.GetBytes(configString);
                        string header = configBytes.Length.ToString() + "\n";
                        byte[] headerBytes = Encoding.UTF8.GetBytes(header);

                        await stream.WriteAsync(headerBytes, 0, headerBytes.Length);
                        await stream.WriteAsync(configBytes, 0, configBytes.Length);
                        await stream.FlushAsync();

                        loggingService.AddEntry($"データ送信完了: ヘッダー({headerBytes.Length}B) + データ({configBytes.Length}B)");
                    }
                }
            }
            catch (Exception ex)
            {
                loggingService.AddEntry($"設定送信エラー: {ex.Message}");
                throw; // UI層に例外を伝播させる
            }
        }

        /// <summary>
        /// C++アプリに設定を要求する
        /// </summary>
        public async Task RequestConfigAsync(string host, int port)
        {
            await SendConfigAsync(host, port, ""); // 0バイトのデータを送信
        }
    }
}