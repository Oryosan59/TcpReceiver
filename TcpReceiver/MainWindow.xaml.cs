using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TcpReceiver
{
    public partial class MainWindow : Window
    {
        // ネットワーク設定
        private const string CPP_HOST = "192.168.4.100";
        private const int CPP_SEND_PORT = 12348;
        private const int WPF_RECV_PORT = 12347;

        // サービス
        private readonly LoggingService loggingService;
        private readonly ConfigService configService;
        private readonly TcpService tcpService;

        // UI要素の参照
        private readonly Dictionary<string, TextBox> textBoxControls;

        // 状態
        private DateTime lastReceivedTime;

        public MainWindow()
        {
            InitializeComponent();

            // サービスのインスタンス化
            loggingService = new LoggingService();
            configService = new ConfigService(loggingService);
            tcpService = new TcpService(loggingService);

            textBoxControls = new Dictionary<string, TextBox>();
            lastReceivedTime = DateTime.MinValue;

            InitializeApplication();
        }

        private void InitializeApplication()
        {
            // イベントハンドラを接続
            loggingService.LogUpdated += OnLogUpdated;
            tcpService.ConfigReceived += OnConfigReceived;
            tcpService.ConnectionStatusChanged += OnConnectionStatusChanged;

            loggingService.AddEntry("アプリケーション開始");

            // 既存の設定を読み込み、UIを初期化
            configService.LoadConfigFromFile();
            UpdateUI();

            // TCPリスナーを開始
            tcpService.StartListener(WPF_RECV_PORT);

            UpdateStatistics();
        }

        // --- イベントハンドラ ---

        private void OnLogUpdated(string fullLog, int logCount)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (LogTextBox != null)
                {
                    LogTextBox.Text = fullLog;
                    LogTextBox.ScrollToEnd();
                }
                if (LogStatsText != null)
                {
                    LogStatsText.Text = $"ログエントリ: {logCount}件";
                }
            });
        }

        private async Task OnConfigReceived(string data)
        {
            configService.ProcessReceivedConfig(data);

            await Dispatcher.InvokeAsync(() =>
            {
                lastReceivedTime = DateTime.Now;
                StatusText.Text = "設定受信完了";
                LastUpdateText.Text = DateTime.Now.ToString("HH:mm:ss");
                UpdateUI();
            });
        }

        private void OnConnectionStatusChanged(string status)
        {
            Dispatcher.InvokeAsync(() =>
            {
                StatusText.Text = status;
            });
        }

        // --- UIイベント ---

        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            loggingService.Clear();
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            SendButton.IsEnabled = false;
            StatusText.Text = "設定送信中...";

            try
            {
                if (lastReceivedTime == DateTime.MinValue)
                {
                    loggingService.AddEntry("初回設定未受信のため、設定要求を送信します。");
                    await tcpService.RequestConfigAsync(CPP_HOST, CPP_SEND_PORT);
                    MessageBox.Show("C++アプリケーションに設定を要求しました。\nデータがUIに表示されるまでしばらくお待ちください。", "設定要求", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    CollectConfigFromUI();
                    string configString = configService.SerializeConfigForCpp(WPF_RECV_PORT, CPP_SEND_PORT);
                    await tcpService.SendConfigAsync(CPP_HOST, CPP_SEND_PORT, configString);
                    configService.SaveConfigToFile(); // 送信成功後に保存
                    StatusText.Text = "設定送信完了";
                    MessageBox.Show("設定をC++アプリケーションに送信しました。", "送信完了", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = "送信エラー";
                MessageBox.Show($"設定の送信に失敗しました:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SendButton.IsEnabled = true;
            }
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("UI上のすべての変更を破棄しますか？", "変更の破棄", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                configService.ResetToOriginal();
                UpdateUI();
                StatusText.Text = "変更を破棄しました";
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                CollectConfigFromUI();
                configService.SaveConfigToFile();
                StatusText.Text = "設定保存完了";
                MessageBox.Show("設定をファイルに保存しました。", "保存完了", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"設定の保存に失敗しました:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ReloadButton_Click(object sender, RoutedEventArgs e)
        {
            configService.LoadConfigFromFile();
            UpdateUI();
            StatusText.Text = "設定再読み込み完了";
            MessageBox.Show("設定ファイルを再読み込みしました。", "再読み込み完了", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source is TabControl)
            {
                Dispatcher.BeginInvoke(new Action(UpdateUI), System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private void ConfigTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox && textBox.Tag is string key)
            {
                var parts = key.Split('.');
                if (parts.Length == 2)
                {
                    string section = parts[0];
                    string configKey = parts[1];

                    if (configService.OriginalConfigData.TryGetValue(section, out var originalSection) &&
                        originalSection.TryGetValue(configKey, out var originalValue))
                    {
                        textBox.Background = textBox.Text != originalValue
                            ? new SolidColorBrush(Color.FromRgb(255, 255, 224))
                            : null;
                    }
                }
            }
        }

        // --- ヘルパーメソッド ---

        private void UpdateUI()
        {
            textBoxControls.Clear();
            PopulateUiFromConfigData();
            UpdateStatistics();
            loggingService.AddEntry("UI更新完了");
        }

        private void PopulateUiFromConfigData()
        {
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

        private void UpdateTextBox(string section, string key, TextBox textBox)
        {
            if (textBox == null) return;

            string value = null;
            if (configService.ConfigData.TryGetValue(section, out var sectionData))
            {
                sectionData.TryGetValue(key, out value);
            }

            textBox.TextChanged -= ConfigTextBox_TextChanged;
            textBox.Text = value ?? "";
            textBox.ClearValue(BackgroundProperty);
            textBox.TextChanged += ConfigTextBox_TextChanged;

            string fullKey = $"{section}.{key}";
            textBox.Tag = fullKey;
            textBoxControls[fullKey] = textBox;
        }

        private void CollectConfigFromUI()
        {
            foreach (var kvp in textBoxControls)
            {
                var parts = kvp.Key.Split('.');
                string section = parts[0];
                string key = parts[1];
                string value = kvp.Value.Text;

                if (!configService.ConfigData.ContainsKey(section))
                {
                    configService.ConfigData[section] = new Dictionary<string, string>();
                }
                configService.ConfigData[section][key] = value;
            }
            loggingService.AddEntry($"UI設定値収集完了: {textBoxControls.Count}項目");
        }

        private void UpdateStatistics()
        {
            int totalSections = configService.ConfigData.Count;
            int totalKeys = configService.ConfigData.Values.Sum(d => d.Count);

            var statsText = $"📊 セクション数: {totalSections}, 設定項目数: {totalKeys}";
            if (lastReceivedTime != DateTime.MinValue)
            {
                var timeSince = DateTime.Now - lastReceivedTime;
                statsText += timeSince.TotalMinutes < 1
                    ? $", 最終受信: {timeSince.TotalSeconds:F0}秒前"
                    : $", 最終受信: {lastReceivedTime:HH:mm:ss}";
            }
            else
            {
                statsText += ", 最終受信: なし";
            }

            if (StatsText != null)
            {
                StatsText.Text = statsText;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            tcpService?.StopListener();
            base.OnClosed(e);
        }
    }
}