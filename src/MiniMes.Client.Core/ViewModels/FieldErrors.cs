using CommunityToolkit.Mvvm.ComponentModel;

namespace MiniMes.Client.Core.ViewModels;

// One message per field for an edit screen, from either side: the client rules (ObservableValidator)
// and the server's 400/409 answer. XAML binds FieldErrors[Code] under each box.
public sealed class FieldErrors : ObservableObject
{
    private readonly Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);

    public string this[string field] => _errors.GetValueOrDefault(field, string.Empty);

    public bool HasAny => _errors.Count > 0;

    public void Set(string field, string? message)
    {
        if (string.IsNullOrEmpty(message)) _errors.Remove(field);
        else _errors[field] = message;
        Changed();
    }

    // Server errors arrive as "Code" or "Lines[0].QtyPer"; each field shows its first message.
    public void SetAll(IReadOnlyDictionary<string, string[]> errors)
    {
        foreach (var (field, messages) in errors)
            if (messages.Length > 0) _errors[field] = messages[0];
        Changed();
    }

    public void Clear()
    {
        _errors.Clear();
        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged("Item[]"); // refreshes every indexer binding
        OnPropertyChanged(nameof(HasAny));
    }
}
