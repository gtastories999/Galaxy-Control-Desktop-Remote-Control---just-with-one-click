namespace GalaxyControl;
internal static class Program {
    [STAThread] static void Main() { ApplicationConfiguration.Initialize();
        try{AppProfile.ResetForFreshPackage();}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException){MessageBox.Show("The previous connection settings could not be cleared. Close other Galaxy Control windows, check file permissions and retry. No sign-in was started.","Galaxy Control · Fresh setup",MessageBoxButtons.OK,MessageBoxIcon.Error);return;}
        Application.Run(new MainWindow()); }
}
