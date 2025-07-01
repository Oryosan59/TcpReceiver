
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TcpReceiver
{
    /// <summary>
    /// アプリケーションのログを管理するクラス
    /// </summary>
    public class LoggingService
    {
        private readonly List<string> logEntries = new List<string>();
        private readonly object logLock = new object();
        private const int MaxLogEntries = 1000;

        /// <summary>
        /// ログが更新されたときに発生するイベント
        /// </summary>
        public event Action<string, int> LogUpdated;

        /// <summary>
        /// 新しいログエントリを追加する
        /// </summary>
        public void AddEntry(string message)
        {
            lock (logLock)
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                string logEntry = $"[{timestamp}] {message}";
                logEntries.Add(logEntry);

                if (logEntries.Count > MaxLogEntries)
                {
                    logEntries.RemoveAt(0);
                }

                // イベントを発行してUIに通知
                LogUpdated?.Invoke(string.Join(Environment.NewLine, logEntries), logEntries.Count);
            }
        }

        /// <summary>
        /// すべてのログをクリアする
        /// </summary>
        public void Clear()
        {
            lock (logLock)
            {
                logEntries.Clear();
                AddEntry("ログをクリアしました");
            }
        }
    }
}
