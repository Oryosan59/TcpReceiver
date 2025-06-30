using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Linq;
using System.Windows.Input;  // Key, Keyboard, TraversalRequestなどのため
using System.Globalization;  // 数値変換のため（必要に応じて）

namespace TcpReceiver
{
    public partial class MainWindow : Window
    {
        // ネットワーク設定
        private const string CPP_HOST = "192.168.4.100";  // C++アプリのIP
        private const int CPP_SEND_PORT = 12348;          // C++アプリの受信ポート
        private const int WPF_RECV_PORT = 12347;          // WPFの受信ポート

        // ファイルパス
        private const string CONFIG_FILE_PATH = "config_received.ini";
        private const string BACKUP_FILE_PATH = "config_backup.ini";

        // 設定データを格納する辞書
        private Dictionary<string, Dictionary<string, string>> configData;
        private Dictionary<string, Dictionary<string, string>> originalConfigData; // 元の設定を保持

        // UI要素の参照を保持
        private Dictionary<string, TextBox> textBoxControls;

        // TCP受信用のリスナー
        private TcpListener tcpListener;
        private CancellationTokenSource cancellationToken;

        // 最後に受信した時刻
        private DateTime lastReceivedTime;

        // ログ管理
        private List<string> logEntries;
        private readonly object logLock = new object();

        public MainWindow()
        {
            InitializeComponent();
            InitializeApplication();
        }

        private void InitializeApplication()
        {
            configData = new Dictionary<string, Dictionary<string, string>>();
            originalConfigData = new Dictionary<string, Dictionary<string, string>>();
            textBoxControls = new Dictionary<string, TextBox>();
            logEntries = new List<string>();
            lastReceivedTime = DateTime.MinValue;

            // 初期ログエントリを追加
            AddLogEntry("アプリケーション開始");
            AddLogEntry("TCP受信を待機中...");

            // 既存の設定ファイルがあれば読み込む
            LoadConfigFromFile();

            // TCP受信を開始
            StartTcpListener();

            StatusText.Text = "初期化完了 - TCP受信待機中";
            UpdateStatistics();
        }

        /// <summary>
        /// ログエントリを追加する
        /// </summary>
        private void AddLogEntry(string message)
        {
            lock (logLock)
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                string logEntry = $"[{timestamp}] {message}";
                logEntries.Add(logEntry);

                // ログが1000件を超えた場合、古いものを削除
                if (logEntries.Count > 1000)
                {
                    logEntries.RemoveAt(0);
                }

                // UIスレッドでログテキストボックスを更新
                Dispatcher.InvokeAsync(() => {
                    if (LogTextBox != null)
                    {
                        LogTextBox.Text = string.Join(Environment.NewLine, logEntries);
                        LogTextBox.ScrollToEnd();
                    }
                    UpdateLogStatistics();
                });
            }
        }

        /// <summary>
        /// ログ統計を更新する
        /// </summary>
        private void UpdateLogStatistics()
        {
            if (LogStatsText != null)
            {
                lock (logLock)
                {
                    LogStatsText.Text = $"ログエントリ: {logEntries.Count}件";
                }
            }
        }

        /// <summary>
        /// ログをクリアする
        /// </summary>
        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            lock (logLock)
            {
                logEntries.Clear();
                AddLogEntry("ログをクリアしました");
            }

            Dispatcher.InvokeAsync(() => {
                if (LogTextBox != null)
                {
                    LogTextBox.Text = string.Join(Environment.NewLine, logEntries);
                }
                UpdateLogStatistics();
            });
        }

        /// <summary>
        /// 設定ファイルから設定を読み込む
        /// </summary>
        private void LoadConfigFromFile()
        {
            try
            {
                if (File.Exists(CONFIG_FILE_PATH))
                {
                    string content = File.ReadAllText(CONFIG_FILE_PATH, Encoding.UTF8);
                    ParseConfigData(content);
                    UpdateUI();
                    StatusText.Text = "既存の設定ファイルを読み込みました";
                    AddLogEntry($"設定ファイル読み込み成功: {CONFIG_FILE_PATH}");
                }
                else
                {
                    AddLogEntry("設定ファイルが見つかりません");
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = "設定ファイル読み込みエラー";
                AddLogEntry($"設定ファイル読み込みエラー: {ex.Message}");
                Console.WriteLine($"設定ファイル読み込みエラー: {ex.Message}");
            }
        }

        /// <summary>
        /// TCP受信を開始する
        /// </summary>
        private void StartTcpListener()
        {
            try
            {
                cancellationToken = new CancellationTokenSource();
                tcpListener = new TcpListener(IPAddress.Any, WPF_RECV_PORT);
                tcpListener.Start();

                Task.Run(() => ListenForConfigUpdates(cancellationToken.Token));

                Dispatcher.Invoke(() => {
                    StatusText.Text = $"ポート {WPF_RECV_PORT} で待機中";
                });

                AddLogEntry($"TCP受信開始: ポート {WPF_RECV_PORT}");
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => {
                    StatusText.Text = "TCP受信エラー";
                    MessageBox.Show($"TCP受信の開始に失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                });
                AddLogEntry($"TCP受信開始エラー: {ex.Message}");
            }
        }

        /// <summary>
        /// C++アプリからの設定データを受信する（ヘッダー付きメッセージ対応）
        /// </summary>
        private async Task ListenForConfigUpdates(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using (var client = await tcpListener.AcceptTcpClientAsync())
                    {
                        string clientEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "不明";
                        AddLogEntry($"クライアント接続: {clientEndpoint}");

                        Dispatcher.Invoke(() => StatusText.Text = "C++アプリから接続受信中...");

                        using (var stream = client.GetStream())
                        {
                            // 1. ヘッダー（メッセージ長）を読み込む
                            string lengthHeader = await ReadLineAsync(stream, token);
                            if (string.IsNullOrEmpty(lengthHeader))
                            {
                                AddLogEntry("空のヘッダーを受信");
                                continue;
                            }

                            if (!int.TryParse(lengthHeader.Trim(), out int expectedLength) || expectedLength <= 0)
                            {
                                AddLogEntry($"不正なヘッダー形式: {lengthHeader}");
                                Dispatcher.Invoke(() => StatusText.Text = "不正なヘッダー形式");
                                continue;
                            }

                            AddLogEntry($"データ長ヘッダー受信: {expectedLength} bytes");

                            // 2. 指定された長さのデータを読み込む
                            byte[] buffer = new byte[expectedLength];
                            int totalRead = 0;

                            while (totalRead < expectedLength && !token.IsCancellationRequested)
                            {
                                int bytesRead = await stream.ReadAsync(buffer, totalRead, expectedLength - totalRead, token);
                                if (bytesRead == 0)
                                {
                                    throw new EndOfStreamException("接続が予期せず終了しました");
                                }
                                totalRead += bytesRead;
                            }

                            if (totalRead == expectedLength)
                            {
                                string receivedData = Encoding.UTF8.GetString(buffer, 0, totalRead);
                                AddLogEntry($"設定データ受信完了: {totalRead} bytes");
                                await ProcessReceivedConfig(receivedData);
                            }
                        }

                        Dispatcher.Invoke(() => {
                            StatusText.Text = "設定受信完了";
                            LastUpdateText.Text = DateTime.Now.ToString("HH:mm:ss");
                            lastReceivedTime = DateTime.Now;
                        });

                        AddLogEntry($"クライアント切断: {clientEndpoint}");
                    }
                }
                catch (ObjectDisposedException)
                {
                    // アプリケーション終了時の正常な例外
                    AddLogEntry("TCP受信停止（アプリケーション終了）");
                    break;
                }
                catch (OperationCanceledException)
                {
                    // キャンセル時の正常な例外
                    AddLogEntry("TCP受信キャンセル");
                    break;
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        AddLogEntry($"TCP受信エラー: {ex.Message}");
                        Dispatcher.Invoke(() => {
                            StatusText.Text = "受信エラー";
                            Console.WriteLine($"TCP受信エラー: {ex.Message}");
                        });

                        // エラー後、少し待ってから再試行
                        await Task.Delay(1000, token);
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
            byte[] buffer = new byte[1];

            while (!token.IsCancellationRequested)
            {
                int bytesRead = await stream.ReadAsync(buffer, 0, 1, token);
                if (bytesRead == 0)
                {
                    break; // 接続終了
                }

                char c = (char)buffer[0];
                if (c == '\n')
                {
                    break; // 改行で終了
                }

                if (c != '\r') // \r は無視
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// 受信した設定データを処理してUIを更新する
        /// </summary>
        private async Task ProcessReceivedConfig(string data)
        {
            await Task.Run(() => ParseConfigData(data));

            // 受信したデータを「オリジナル」としてディープコピーして保存する
            // これにより、ユーザーによる変更を追跡できる
            await Task.Run(() =>
            {
                originalConfigData = new Dictionary<string, Dictionary<string, string>>();
                foreach (var section in configData)
                {
                    originalConfigData[section.Key] = new Dictionary<string, string>(section.Value);
                }
            });

            await Dispatcher.InvokeAsync(() => UpdateUI());

            // 設定ファイルに自動保存
            await Task.Run(() => SaveConfigToFile());

            AddLogEntry("設定データ処理完了、UI更新済み");
        }

        /// <summary>
        /// 設定データをパースする（改良版）
        /// </summary>
        /// <remarks>
        /// このパーサーは2つの形式に対応します:
        /// 1. 標準INI形式 (ファイルから読み込む場合):
        ///    [SECTION]
        ///    KEY=VALUE
        /// 2. C++アプリからのストリーム形式 (TCPで受信する場合):
        ///    [SECTION]KEY=VALUE
        /// </remarks>
        private void ParseConfigData(string data)
        {
            configData.Clear();

            var lines = data.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

            // 最初の行が数字のみの場合はTCPの長さヘッダーとみなし、スキップする
            int startIndex = 0;
            if (lines.Length > 0 && int.TryParse(lines[0].Trim(), out _))
            {
                startIndex = 1;
            }

            string currentSection = null;
            int parsedItems = 0;
            for (int i = startIndex; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                // コメント行や空行は無視
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith(";") || line.StartsWith("#"))
                    continue;

                // セクション行か？ ([SECTION] または [SECTION]KEY=VALUE)
                if (line.StartsWith("[") && line.Contains("]"))
                {
                    int sectionEnd = line.IndexOf(']');
                    
                    // C++からのストリーム形式 ([SECTION]KEY=VALUE) をチェック
                    int equalsPos = line.IndexOf('=', sectionEnd);
                    if (equalsPos > sectionEnd) {
                        // C++ストリーム形式
                        string section = line.Substring(1, sectionEnd - 1);
                        string key = line.Substring(sectionEnd + 1, equalsPos - (sectionEnd + 1));
                        string value = line.Substring(equalsPos + 1).Trim();
                        if (!configData.ContainsKey(section)) configData[section] = new Dictionary<string, string>();
                        configData[section][key] = value;
                        parsedItems++;
                    } else {
                        // 標準INI形式のセクション定義
                        currentSection = line.Substring(1, sectionEnd - 1);
                        if (!configData.ContainsKey(currentSection)) configData[currentSection] = new Dictionary<string, string>();
                    }
                }
                // キー=値 の行か？ (標準INI形式)
                else if (currentSection != null && line.Contains("="))
                {
                    var parts = line.Split(new[] { '=' }, 2);
                    if (parts.Length == 2)
                    {
                        string key = parts[0].Trim();
                        string value = parts[1].Trim();
                        // currentSectionは既に設定されているはず
                        if (configData.ContainsKey(currentSection))
                        {
                            configData[currentSection][key] = value;
                            parsedItems++;
                        }
                    }
                }
            }

            AddLogEntry($"設定データ解析完了: {parsedItems}項目");
        }

        /// <summary>
        /// 設定をファイルに保存する
        /// </summary>
        private void SaveConfigToFile()
        {
            try
            {
                // バックアップを作成
                if (File.Exists(CONFIG_FILE_PATH))
                {
                    File.Copy(CONFIG_FILE_PATH, BACKUP_FILE_PATH, true);
                    AddLogEntry("設定ファイルのバックアップ作成");
                }

                // 設定をINI形式で保存
                var sb = new StringBuilder();
                sb.AppendLine("# Navigator C++制御アプリケーションの設定ファイル");
                sb.AppendLine("# WPFアプリケーションで編集・同期済み");
                sb.AppendLine($"# 最終更新: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine();

                foreach (var section in configData.OrderBy(s => s.Key))
                {
                    // ネットワーク関連の設定は、固定値として扱うためファイルには保存しない
                    if (section.Key == "NETWORK" || section.Key == "CONFIG_SYNC")
                    {
                        continue;
                    }

                    sb.AppendLine($"[{section.Key}]");
                    foreach (var kvp in section.Value.OrderBy(k => k.Key))
                    {
                        sb.AppendLine($"{kvp.Key}={kvp.Value}");
                    }
                    sb.AppendLine();
                }

                File.WriteAllText(CONFIG_FILE_PATH, sb.ToString(), Encoding.UTF8);

                AddLogEntry($"設定ファイル保存完了: {CONFIG_FILE_PATH}");
                Dispatcher.Invoke(() => {
                    Console.WriteLine($"設定を {CONFIG_FILE_PATH} に保存しました");
                });
            }
            catch (Exception ex)
            {
                AddLogEntry($"設定ファイル保存エラー: {ex.Message}");
                Dispatcher.Invoke(() => {
                    Console.WriteLine($"設定ファイル保存エラー: {ex.Message}");
                });
            }
        }

        /// <summary>
        /// UIを更新する（改良版）
        /// </summary>
        private void UpdateUI()
        {
            // 既存のコントロールをクリア
            textBoxControls.Clear();

            // configDataの内容をUIに反映させる
            PopulateUiFromConfigData();

            // 統計情報を更新
            UpdateStatistics();

            AddLogEntry("UI更新完了");
        }

        /// <summary>
        /// configDataの内容をUIのTextBoxに反映させる
        /// </summary>
        private void PopulateUiFromConfigData()
        {
            // ヘルパー関数: 指定されたTextBoxをconfigDataの値で更新し、追跡用辞書に登録する
            void UpdateTextBox(string section, string key, TextBox textBox)
            {
                // TabControlの仕様上、表示されていないタブのコントロールはnullの場合があるためチェック
                if (textBox == null) return;

                string value = "";
                // configDataから値を取得
                if (configData.TryGetValue(section, out var sectionData) && sectionData.TryGetValue(key, out value))
                {
                    // 値が見つかった場合は何もしない（valueに設定済み）
                }

                // イベントハンドラを一時的に解除して、プログラムによるテキスト変更でイベントが発火するのを防ぐ
                textBox.TextChanged -= ConfigTextBox_TextChanged;

                textBox.Text = value;
                textBox.ClearValue(BackgroundProperty); // 変更ハイライトをリセット

                // イベントハンドラを再設定
                textBox.TextChanged += ConfigTextBox_TextChanged;

                string fullKey = $"{section}.{key}";
                textBox.Tag = fullKey; // Tagにキーを保存してTextChangedイベントで利用
                textBoxControls[fullKey] = textBox; // UIから値を収集するために辞書に登録
            }

            // --- PWM設定タブ ---
            UpdateTextBox("pwm", "PWM_MIN", PwmMinTextBox);
            UpdateTextBox("pwm", "PWM_NEUTRAL", PwmNeutralTextBox);
            UpdateTextBox("pwm", "PWM_NORMAL_MAX", PwmNormalMaxTextBox);
            UpdateTextBox("pwm", "PWM_BOOST_MAX", PwmBoostMaxTextBox);
            UpdateTextBox("PWM", "PWM_FREQUENCY", PwmFrequencyTextBox);

            // --- スラスター設定タブ ---
            UpdateTextBox("thruster_control", "SMOOTHING_FACTOR_HORIZONTAL", SmoothingHorizontalTextBox);
            UpdateTextBox("thruster_control", "SMOOTHING_FACTOR_VERTICAL", SmoothingVerticalTextBox);
            UpdateTextBox("thruster_control", "KP_ROLL", KpRollTextBox);
            UpdateTextBox("thruster_control", "KP_YAW", KpYawTextBox);
            UpdateTextBox("thruster_control", "YAW_THRESHOLD_DPS", YawThresholdTextBox);
            UpdateTextBox("thruster_control", "YAW_GAIN", YawGainTextBox);

            // --- ネットワーク設定タブ (Joystick, LED, Application) ---
            UpdateTextBox("joystick", "DEADZONE", JoystickDeadzoneTextBox);
            UpdateTextBox("led", "CHANNEL", LedChannelTextBox);
            UpdateTextBox("led", "ON_VALUE", LedOnValueTextBox);
            UpdateTextBox("led", "OFF_VALUE", LedOffValueTextBox);
            UpdateTextBox("application", "SENSOR_SEND_INTERVAL", SensorSendIntervalTextBox);
            UpdateTextBox("application", "LOOP_DELAY_US", LoopDelayTextBox);

            // --- カメラ設定タブ ---
            UpdateTextBox("gstreamer_camera_1", "DEVICE", Camera1DeviceTextBox);
            UpdateTextBox("gstreamer_camera_1", "PORT", Camera1PortTextBox);
            UpdateTextBox("gstreamer_camera_1", "WIDTH", Camera1WidthTextBox);
            UpdateTextBox("gstreamer_camera_1", "HEIGHT", Camera1HeightTextBox);
            UpdateTextBox("gstreamer_camera_1", "FRAMERATE_NUM", Camera1FramerateNumTextBox);
            UpdateTextBox("gstreamer_camera_2", "DEVICE", Camera2DeviceTextBox);
            UpdateTextBox("gstreamer_camera_2", "PORT", Camera2PortTextBox);
            UpdateTextBox("gstreamer_camera_2", "WIDTH", Camera2WidthTextBox);
            UpdateTextBox("gstreamer_camera_2", "HEIGHT", Camera2HeightTextBox);
            UpdateTextBox("gstreamer_camera_2", "FRAMERATE_NUM", Camera2FramerateNumTextBox);
            UpdateTextBox("gstreamer_camera_2", "X264_BITRATE", Camera2X264BitrateTextBox);
        }

        /// <summary>
        /// 統計情報を更新する
        /// </summary>
        private void UpdateStatistics()
        {
            int totalSections = configData.Count;
            int totalKeys = configData.Values.Sum(section => section.Count);

            // 統計情報をStatsTextに表示
            var statsText = $"📊 セクション数: {totalSections}, 設定項目数: {totalKeys}";
            if (lastReceivedTime != DateTime.MinValue)
            {
                var timeSinceLastUpdate = DateTime.Now - lastReceivedTime;
                if (timeSinceLastUpdate.TotalMinutes < 1)
                {
                    statsText += $", 最終受信: {timeSinceLastUpdate.TotalSeconds:F0}秒前";
                }
                else
                {
                    statsText += $", 最終受信: {lastReceivedTime:HH:mm:ss}";
                }
            }
            else
            {
                statsText += ", 最終受信: なし";
            }

            // StatsTextラベルを更新
            if (StatsText != null)
            {
                StatsText.Text = statsText;
            }
        }

        /// <summary>
        /// タブが切り替えられたときに呼び出され、表示されたタブのUI要素が
        /// 正しく設定値で更新されるようにする
        /// </summary>
        private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // TabControlのイベントのみを処理
            if (e.Source is TabControl)
            {
                // UIスレッドで、かつ他の処理の後に実行されるように遅延実行することで、
                // 新しいタブのコントロールが完全に読み込まれるのを待つ
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    // UIの再描画（値の再設定）を行う
                    PopulateUiFromConfigData();
                    AddLogEntry($"タブ切り替え: UIを再描画しました");
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
        }


        /// <summary>
        /// 設定値テキストボックスのテキストが変更されたときのイベントハンドラ。
        /// 元の値と異なる場合に背景色を変更して、変更箇所を視覚的に示す。
        /// </summary>
        private void ConfigTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox && textBox.Tag is string key)
            {
                var parts = key.Split('.');
                if (parts.Length == 2)
                {
                    string section = parts[0];
                    string configKey = parts[1];

                    // オリジナルの値と比較
                    if (originalConfigData.TryGetValue(section, out var originalSection) &&
                        originalSection.TryGetValue(configKey, out var originalValue))
                    {
                        if (textBox.Text != originalValue)
                        {
                            // 変更あり: 背景色を薄い黄色に変更
                            textBox.Background = new SolidColorBrush(Color.FromRgb(255, 255, 224));
                        }
                        else
                        {
                            // 変更なし (元の値に戻された): 背景色をスタイル既定値に戻す
                            textBox.ClearValue(BackgroundProperty);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// C++アプリに設定を送信する（改良版）
        /// </summary>
        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SendButton.IsEnabled = false;
                StatusText.Text = "設定送信中...";
                AddLogEntry("C++アプリへの設定送信開始");

                // まだ一度も設定を受信していない場合、設定を「要求」する
                if (lastReceivedTime == DateTime.MinValue)
                {
                    AddLogEntry("初回設定未受信のため、設定要求を送信します。");

                    using (var client = new TcpClient())
                    {
                        client.ReceiveTimeout = 5000;
                        client.SendTimeout = 5000;

                        AddLogEntry($"C++アプリへ接続中: {CPP_HOST}:{CPP_SEND_PORT}");
                        await client.ConnectAsync(CPP_HOST, CPP_SEND_PORT);
                        AddLogEntry("C++アプリへ接続成功");

                        using (var stream = client.GetStream())
                        {
                            // 0バイトのデータを送信することで「要求」とみなす
                            string header = "0\n";
                            byte[] headerBytes = Encoding.UTF8.GetBytes(header);
                            await stream.WriteAsync(headerBytes, 0, headerBytes.Length);
                            await stream.FlushAsync();
                            AddLogEntry("設定要求（0バイトデータ）を送信しました。");
                        }
                    }
                    MessageBox.Show("C++アプリケーションに設定を要求しました。\nデータがUIに表示されるまでしばらくお待ちください。", "設定要求", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else // 通常通り、設定を「送信」する
                {
                    // UIから現在の値を取得
                    CollectConfigFromUI();

                    // 送信する設定データに、このWPFアプリが実際に使用しているポート情報を強制的に含める。
                    if (!configData.ContainsKey("CONFIG_SYNC"))
                    {
                        configData["CONFIG_SYNC"] = new Dictionary<string, string>();
                    }
                    configData["CONFIG_SYNC"]["WPF_RECV_PORT"] = WPF_RECV_PORT.ToString(); // WPFがリッスンするポート
                    configData["CONFIG_SYNC"]["CPP_RECV_PORT"] = CPP_SEND_PORT.ToString(); // C++がリッスンするポート

                    // 設定をシリアライズ（C++側の形式に合わせて）
                    string configString = SerializeConfigForCpp();
                    AddLogEntry($"設定データシリアライズ完了: {configString.Length} 文字");

                    // TCP送信
                    using (var client = new TcpClient())
                    {
                        client.ReceiveTimeout = 5000;
                        client.SendTimeout = 5000;

                        AddLogEntry($"C++アプリへ接続中: {CPP_HOST}:{CPP_SEND_PORT}");
                        await client.ConnectAsync(CPP_HOST, CPP_SEND_PORT);
                        AddLogEntry("C++アプリへ接続成功");

                        using (var stream = client.GetStream())
                        {
                            byte[] configBytes = Encoding.UTF8.GetBytes(configString);
                            string header = configBytes.Length.ToString() + "\n";
                            byte[] headerBytes = Encoding.UTF8.GetBytes(header);

                            await stream.WriteAsync(headerBytes, 0, headerBytes.Length);
                            await stream.WriteAsync(configBytes, 0, configBytes.Length);
                            await stream.FlushAsync();

                            AddLogEntry($"データ送信完了: ヘッダー({headerBytes.Length}B) + データ({configBytes.Length}B)");
                        }
                    }

                    // ローカルファイルにも保存
                    SaveConfigToFile();

                    StatusText.Text = "設定送信完了";
                    AddLogEntry("C++アプリへの設定送信完了");
                    MessageBox.Show("設定をC++アプリケーションに送信しました。", "送信完了", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = "送信エラー";
                AddLogEntry($"設定送信エラー: {ex.Message}");
                MessageBox.Show($"設定の送信に失敗しました:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SendButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// UIから設定値を収集する（既存コードと同じ）
        /// </summary>
        private void CollectConfigFromUI()
        {
            int collectedItems = 0;
            foreach (var kvp in textBoxControls)
            {
                var parts = kvp.Key.Split('.');
                if (parts.Length == 2)
                {
                    string section = parts[0];
                    string key = parts[1];
                    string value = kvp.Value.Text;

                    if (!configData.ContainsKey(section))
                    {
                        configData[section] = new Dictionary<string, string>();
                    }
                    configData[section][key] = value;
                    collectedItems++;
                }
            }
            AddLogEntry($"UI設定値収集完了: {collectedItems}項目");
        }

        /// <summary>
        /// C++アプリ用の設定をシリアライズする
        /// </summary>
        private string SerializeConfigForCpp()
        {
            var sb = new StringBuilder();
            foreach (var section in configData)
            {
                foreach (var kvp in section.Value)
                {
                    sb.AppendLine($"[{section.Key}]{kvp.Key}={kvp.Value}");
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// 設定をリセットする（既存コードと同じ）
        /// </summary>
        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("UI上のすべての変更を破棄し、最後に受信した設定の状態に戻しますか？", "変更の破棄", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                // 現在のconfigDataを、バックアップしておいたオリジナルデータで上書き
                configData = new Dictionary<string, Dictionary<string, string>>();
                foreach (var section in originalConfigData)
                {
                    configData[section.Key] = new Dictionary<string, string>(section.Value);
                }

                // UIを再構築して、すべてのテキストボックスと背景色をリセット
                UpdateUI();

                StatusText.Text = "変更を破棄しました";
                AddLogEntry("UIの変更を破棄しました");
                UpdateStatistics();
            }
        }

        /// <summary>
        /// パネルをクリアする（現在はUpdateUIで全体が再構築されるため、直接は使用されていない）
        /// </summary>
        private void ClearPanel(StackPanel panel)
        {
            if (panel.Children.Count > 1)
            {
                for (int i = panel.Children.Count - 1; i >= 1; i--)
                {
                    panel.Children.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// 設定をファイルに保存する（手動保存）
        /// </summary>
        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                CollectConfigFromUI();
                SaveConfigToFile();

                StatusText.Text = "設定保存完了";
                MessageBox.Show($"設定を {CONFIG_FILE_PATH} に保存しました。", "保存完了", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"設定の保存に失敗しました:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 設定ファイルを再読み込みする
        /// </summary>
        private void ReloadButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                LoadConfigFromFile();
                StatusText.Text = "設定再読み込み完了";
                MessageBox.Show("設定ファイルを再読み込みしました。", "再読み込み完了", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                StatusText.Text = "再読み込みエラー";
                MessageBox.Show($"設定の再読み込みに失敗しました:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// アプリケーション終了時の処理
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            try
            {
                cancellationToken?.Cancel();
                tcpListener?.Stop();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"終了処理エラー: {ex.Message}");
            }
            finally
            {
                base.OnClosed(e);
            }
        }
    }
}