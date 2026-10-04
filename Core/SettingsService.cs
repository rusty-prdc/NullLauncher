using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NullLauncher.Core;

public sealed class SettingsChangedEventArgs : EventArgs
{
    public IReadOnlyDictionary<string, object?> Changed { get; init; } = new Dictionary<string, object?>();
    public IReadOnlyDictionary<string, object?> Snapshot { get; init; } = new Dictionary<string, object?>();
}

/// <summary>
/// Хранилище настроек лаунчера: config/settings.json.
/// Плоский словарь ключ→значение: фронтенд может читать/писать любую настройку одной командой,
/// типизированные доступы обеспечивает Get/Set. Сохранение атомарное.
/// </summary>
public sealed class SettingsService
{
    private readonly string _file;
    private readonly object _sync = new();
    private Dictionary<string, JsonElement> _values = new();

    public event EventHandler<SettingsChangedEventArgs>? Changed;

    public SettingsService(AppPaths paths)
    {
        _file = paths.SettingsFile;
        Load();
    }

    public void Load()
    {
        lock (_sync)
        {
            try
            {
                if (File.Exists(_file))
                {
                    var text = File.ReadAllText(_file);
                    _values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text) ?? new();
                }
                else _values = new();
            }
            catch (Exception ex)
            {
                Log.Error("Не удалось прочитать settings.json, используются значения по умолчанию", ex);
                _values = new();
            }
        }
    }

    private void SaveUnsafe()
    {
        try
        {
            var dir = Path.GetDirectoryName(_file)!;
            Directory.CreateDirectory(dir);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_values, IpcRouter.JsonOptions));
            // атомарная замена: временный файл → основной (раньше аргументы были перевёрнуты,
            // из-за чего первое сохранение падало и настройки не записывались)
            File.Move(tmp, _file, true);
        }
        catch (Exception ex) { Log.Error("Не удалось сохранить настройки", ex); }
    }

    public T Get<T>(string key, T fallback)
    {
        lock (_sync)
        {
            if (!_values.TryGetValue(key, out var el)) return fallback;
            try
            {
                if (el.ValueKind == JsonValueKind.Null) return fallback;
                var v = JsonSerializer.Deserialize<T>(el, IpcRouter.JsonOptions);
                return v is null ? fallback : v;
            }
            catch { return fallback; }
        }
    }

    public void Set<T>(string key, T value, bool save = true, bool raise = true)
    {
        Dictionary<string, object?> changed = new();
        lock (_sync)
        {
            var el = JsonSerializer.SerializeToElement(value, IpcRouter.JsonOptions);
            var isSame = _values.TryGetValue(key, out var old) && old.GetRawText() == el.GetRawText();
            _values[key] = el;
            if (isSame) return;
            changed[key] = value;
            if (save) SaveUnsafe();
        }
        if (raise) Raise(changed);
    }

    /// <summary>Пакетное обновление сырых JSON-значений (из settings.set).</summary>
    public void SetRaw(IDictionary<string, System.Text.Json.JsonElement> values, bool save = true)
    {
        Dictionary<string, object?> changed = new();
        lock (_sync)
        {
            foreach (var (key, el) in values)
            {
                var isSame = _values.TryGetValue(key, out var old) && old.GetRawText() == el.GetRawText();
                _values[key] = el;
                if (!isSame) changed[key] = el;
            }
            if (save && changed.Count > 0) SaveUnsafe();
        }
        if (changed.Count > 0) Raise(changed);
    }

    /// <summary>Пакетное обновление (например, из settings.set с несколькими ключами).</summary>
    public void SetMany(IDictionary<string, object?> values, bool save = true)
    {
        Dictionary<string, object?> changed = new();
        lock (_sync)
        {
            foreach (var (key, value) in values)
            {
                var el = JsonSerializer.SerializeToElement(value, IpcRouter.JsonOptions);
                var isSame = _values.TryGetValue(key, out var old) && old.GetRawText() == el.GetRawText();
                _values[key] = el;
                if (!isSame) changed[key] = value;
            }
            if (save && changed.Count > 0) SaveUnsafe();
        }
        if (changed.Count > 0) Raise(changed);
    }

    /// <summary>Сброс только настроек: сборки, миры, аккаунты и БД не затрагиваются.</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _values.Clear();
            SaveUnsafe();
        }
        Raise(new Dictionary<string, object?> { ["__reset"] = true });
    }

    public Dictionary<string, object?> Snapshot()
    {
        lock (_sync)
        {
            var d = new Dictionary<string, object?>();
            foreach (var (k, v) in _values)
                d[k] = v.ValueKind == JsonValueKind.Null ? null : JsonSerializer.Deserialize<object>(v, IpcRouter.JsonOptions);
            return d;
        }
    }

    public string FilePath => _file;

    /// <summary>Полный объект настроек для фронтенда: дефолты + сохранённые значения.</summary>
    public Dictionary<string, object?> Full()
    {
        var result = SettingsRegistry.Defaults;
        foreach (var (k, v) in Snapshot()) result[k] = v;
        return result;
    }

    private void Raise(Dictionary<string, object?> changed)
        => Changed?.Invoke(this, new SettingsChangedEventArgs { Changed = changed, Snapshot = Full() });
}
