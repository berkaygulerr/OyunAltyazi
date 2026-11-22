using Velopack;

namespace OyunAltyazi.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack kurulum/güncelleme/kaldırma kancaları: her şeyden önce, WPF başlamadan.
        VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
