using System.Configuration;
using System.Data;
using System.Windows;

namespace TcpReceiver
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // アプリケーション全体の例外処理
            this.DispatcherUnhandledException += (sender, args) =>
            {
                MessageBox.Show($"予期しないエラーが発生しました:\n{args.Exception.Message}",
                    "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };
        }

    }
}