namespace NullLauncher;

/// <summary>Точка входа. Собственное main нужно, чтобы управлять инициализацией и глобальными ошибками.</summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Единая культура — чтобы везде (логи, парсеры, API) был предсказуемый формат чисел.
        CultureInfoEx.Ensure();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
