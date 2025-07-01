using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TcpReceiver
{
    /// <summary>
    /// 設定データの管理とファイル操作を行うクラス
    /// </summary>
    public class ConfigService
    {
        private const string CONFIG_FILE_PATH = "config_received.ini";
        private const string BACKUP_FILE_PATH = "config_backup.ini";

        private readonly LoggingService loggingService;

        public Dictionary<string, Dictionary<string, string>> ConfigData { get; private set; }
        public Dictionary<string, Dictionary<string, string>> OriginalConfigData { get; private set; }

        public ConfigService(LoggingService logger)
        {
            loggingService = logger;
            ConfigData = new Dictionary<string, Dictionary<string, string>>();
            OriginalConfigData = new Dictionary<string, Dictionary<string, string>>();
        }

        /// <summary>
        /// 設定ファイルから設定を読み込む
        /// </summary>
        public void LoadConfigFromFile()
        {
            try
            {
                if (File.Exists(CONFIG_FILE_PATH))
                {
                    string content = File.ReadAllText(CONFIG_FILE_PATH, Encoding.UTF8);
                    ParseConfigData(content);
                    loggingService.AddEntry($"設定ファイル読み込み成功: {CONFIG_FILE_PATH}");
                }
                else
                {
                    loggingService.AddEntry("設定ファイルが見つかりません");
                }
            }
            catch (Exception ex)
            {
                loggingService.AddEntry($"設定ファイル読み込みエラー: {ex.Message}");
                Console.WriteLine($"設定ファイル読み込みエラー: {ex.Message}");
            }
        }

        /// <summary>
        /// 受信した設定データを処理する
        /// </summary>
        public void ProcessReceivedConfig(string data)
        {
            ParseConfigData(data);

            // 受信したデータを「オリジナル」としてディープコピー
            OriginalConfigData = new Dictionary<string, Dictionary<string, string>>();
            foreach (var section in ConfigData)
            {
                OriginalConfigData[section.Key] = new Dictionary<string, string>(section.Value);
            }

            SaveConfigToFile();
            loggingService.AddEntry("設定データ処理完了、ファイルに保存済み");
        }

        /// <summary>
        /// 設定データをパースする
        /// </summary>
        private void ParseConfigData(string data)
        {
            ConfigData.Clear();
            var lines = data.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            string currentSection = null;
            int parsedItems = 0;

            foreach (var line in lines)
            {
                string trimmedLine = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.StartsWith(";") || trimmedLine.StartsWith("#"))
                    continue;

                if (trimmedLine.StartsWith("[") && trimmedLine.Contains("]"))
                {
                    int sectionEnd = trimmedLine.IndexOf(']');
                    int equalsPos = trimmedLine.IndexOf('=', sectionEnd);

                    if (equalsPos > sectionEnd)
                    {
                        string section = trimmedLine.Substring(1, sectionEnd - 1);
                        string key = trimmedLine.Substring(sectionEnd + 1, equalsPos - (sectionEnd + 1));
                        string value = trimmedLine.Substring(equalsPos + 1).Trim();
                        if (!ConfigData.ContainsKey(section)) ConfigData[section] = new Dictionary<string, string>();
                        ConfigData[section][key] = value;
                        parsedItems++;
                    }
                    else
                    {
                        currentSection = trimmedLine.Substring(1, sectionEnd - 1);
                        if (!ConfigData.ContainsKey(currentSection)) ConfigData[currentSection] = new Dictionary<string, string>();
                    }
                }
                else if (currentSection != null && trimmedLine.Contains("="))
                {
                    var parts = trimmedLine.Split(new[] { '=' }, 2);
                    if (parts.Length == 2)
                    {
                        string key = parts[0].Trim();
                        string value = parts[1].Trim();
                        if (ConfigData.ContainsKey(currentSection))
                        {
                            ConfigData[currentSection][key] = value;
                            parsedItems++;
                        }
                    }
                }
            }
            loggingService.AddEntry($"設定データ解析完了: {parsedItems}項目");
        }

        /// <summary>
        /// 設定をファイルに保存する
        /// </summary>
        public void SaveConfigToFile()
        {
            try
            {
                if (File.Exists(CONFIG_FILE_PATH))
                {
                    File.Copy(CONFIG_FILE_PATH, BACKUP_FILE_PATH, true);
                    loggingService.AddEntry("設定ファイルのバックアップ作成");
                }

                var sb = new StringBuilder();
                sb.AppendLine("# Navigator C++制御アプリケーションの設定ファイル");
                sb.AppendLine("# WPFアプリケーションで編集・同期済み");
                sb.AppendLine($"# 最終更新: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine();

                foreach (var section in ConfigData.OrderBy(s => s.Key))
                {
                    if (section.Key == "NETWORK" || section.Key == "CONFIG_SYNC") continue;

                    sb.AppendLine($"[{section.Key}]");
                    foreach (var kvp in section.Value.OrderBy(k => k.Key))
                    {
                        sb.AppendLine($"{kvp.Key}={kvp.Value}");
                    }
                    sb.AppendLine();
                }

                File.WriteAllText(CONFIG_FILE_PATH, sb.ToString(), Encoding.UTF8);
                loggingService.AddEntry($"設定ファイル保存完了: {CONFIG_FILE_PATH}");
            }
            catch (Exception ex)
            {
                loggingService.AddEntry($"設定ファイル保存エラー: {ex.Message}");
            }
        }

        /// <summary>
        /// C++アプリ用の設定をシリアライズする
        /// </summary>
        public string SerializeConfigForCpp(int wpfRecvPort, int cppSendPort)
        {

            var sb = new StringBuilder();
            foreach (var section in ConfigData)
            {
                foreach (var kvp in section.Value)
                {
                    sb.AppendLine($"[{section.Key}]{kvp.Key}={kvp.Value}");
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// UIの変更を破棄し、最後に受信した設定に戻す
        /// </summary>
        public void ResetToOriginal()
        {
            ConfigData = new Dictionary<string, Dictionary<string, string>>();
            foreach (var section in OriginalConfigData)
            {
                ConfigData[section.Key] = new Dictionary<string, string>(section.Value);
            }
            loggingService.AddEntry("UIの変更を破棄しました");
        }
    }
}
