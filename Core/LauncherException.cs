using System.Net.Http;
namespace NullLauncher.Core;

/// <summary>
/// Ошибка, которую можно показать пользователю. Никогда не содержит имён .NET-исключений.
/// </summary>
public class LauncherException : Exception
{
    public string UserMessage { get; }
    public string? Detail { get; }
    public bool Recoverable { get; }

    public LauncherException(string userMessage, string? detail = null, Exception? inner = null, bool recoverable = true)
        : base(userMessage, inner)
    {
        UserMessage = userMessage;
        Detail = detail;
        Recoverable = recoverable;
    }

    public static LauncherException Wrap(Exception ex, string fallback = "Произошла непредвиденная ошибка")
    {
        if (ex is LauncherException le) return le;
        if (ex is OperationCanceledException) return new LauncherException("Операция отменена", null, ex);
        if (ex is HttpRequestException http)
            return new LauncherException("Сервис недоступен. Проверьте подключение к интернету", http.Message, http);
        if (ex is TaskCanceledException tc)
            return new LauncherException("Сервис не ответил вовремя (тайм-аут)", tc.Message, tc);
        if (ex is UnauthorizedAccessException ua)
            return new LauncherException("Нет доступа к файлу или папке", ua.Message, ua);
        if (ex is System.Security.SecurityException sec)
            return new LauncherException("Недостаточно прав для выполнения операции", sec.Message, sec);
        return new LauncherException(fallback, ex.Message, ex);
    }

    /// <summary>Превращает в JSON-ошибку для фронтенда.</summary>
    public object ToDto() => new { code = UserMessage, message = UserMessage, detail = Detail, recoverable = Recoverable };

    /// <summary>В логах показываем и детали (вывод инсталлятора, путь и т.п.) — иначе причина теряется.</summary>
    public override string ToString()
        => Detail is null ? base.ToString() : base.ToString() + "\nДетали: " + Detail;
}
