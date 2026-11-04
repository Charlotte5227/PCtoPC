using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PCtoPC.Client;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var preview = e.Args.Length == 2 && e.Args[0] == "--render-preview";
            var window = new MainWindow(preview);
            MainWindow = window;
            if (preview)
            {
                window.Width = 1220;
                window.Height = 940;
                var root = (FrameworkElement)window.Content;
                window.Content = null;
                var surface = new System.Windows.Controls.Border { Width = 1220, Height = 940, Background = window.Background, Child = root };
                surface.Measure(new Size(1220, 940));
                surface.Arrange(new Rect(0, 0, 1220, 940));
                surface.UpdateLayout();
                var bitmap = new RenderTargetBitmap(1220, 940, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(surface);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = System.IO.File.Create(e.Args[1]);
                encoder.Save(file);
                Shutdown();
            }
            else window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "PCtoPCを起動できません", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
