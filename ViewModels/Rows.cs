using System.Collections.ObjectModel;
using System.Windows.Media;
using ThermalScope.Models;

namespace ThermalScope.ViewModels;

public sealed class SensorRowViewModel : Bindable
{
    private string _valueText = "—";
    private string _unit = string.Empty;
    private Brush _valueBrush = Brushes.Gray;

    public SensorRowViewModel(string key, string component, string category, string name)
    {
        Key = key;
        Component = component;
        Category = category;
        Name = name;
        Detail = $"{component} · {category}";
    }

    public string Key { get; }
    public string Component { get; }
    public string Category { get; }
    public string Name { get; }
    public string Detail { get; }
    public string ValueText { get => _valueText; private set => Set(ref _valueText, value); }
    public string Unit { get => _unit; private set => Set(ref _unit, value); }
    public Brush ValueBrush { get => _valueBrush; private set => Set(ref _valueBrush, value); }

    public void Update(SensorReading reading, bool fahrenheit)
    {
        var isTemperature = reading.Category == nameof(LibreHardwareMonitor.Hardware.SensorType.Temperature);
        if (!reading.Value.HasValue)
        {
            ValueText = "—";
            Unit = string.Empty;
            ValueBrush = ComponentViewModel.Neutral();
            return;
        }

        var value = reading.Value.Value;
        if (isTemperature)
        {
            ValueBrush = Heat.BrushFor(value);
            value = ComponentViewModel.Convert(value, fahrenheit);
            Unit = fahrenheit ? "°F" : "°C";
        }
        else
        {
            ValueBrush = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("TextBrush");
            Unit = reading.Unit;
        }

        ValueText = reading.Category == nameof(LibreHardwareMonitor.Hardware.SensorType.Clock)
            ? value.ToString("0")
            : value.ToString("0.0");
    }
}

public sealed class ProcessRowViewModel : Bindable
{
    private string _title = string.Empty;
    private string _detail = string.Empty;

    public ProcessRowViewModel(string name) => Name = name;

    public string Name { get; }
    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }

    public void Update(GameProcessInfo process)
    {
        Title = string.IsNullOrWhiteSpace(process.WindowTitle) ? "Running in the background" : process.WindowTitle;
        Detail = $"{process.WorkingSetBytes / 1024 / 1024} MB";
    }
}

internal static class CollectionSync
{
    /// <summary>
    /// Updates <paramref name="target"/> to mirror <paramref name="source"/> in place (add, remove, move)
    /// so scroll position, selection and keyboard focus survive a refresh.
    /// </summary>
    public static void Sync<TItem, TVm>(
        ObservableCollection<TVm> target,
        IReadOnlyList<TItem> source,
        IReadOnlyList<string> sourceKeys,
        Func<TVm, string> vmKey,
        Func<TItem, string, TVm> create,
        Action<TVm, TItem> update)
    {
        var wanted = new HashSet<string>(sourceKeys);
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(vmKey(target[i])))
            {
                target.RemoveAt(i);
            }
        }

        for (var i = 0; i < source.Count; i++)
        {
            var key = sourceKeys[i];
            var existing = -1;
            for (var j = i; j < target.Count; j++)
            {
                if (vmKey(target[j]) == key)
                {
                    existing = j;
                    break;
                }
            }

            if (existing < 0)
            {
                target.Insert(i, create(source[i], key));
            }
            else if (existing != i)
            {
                target.Move(existing, i);
            }

            update(target[i], source[i]);
        }
    }
}
