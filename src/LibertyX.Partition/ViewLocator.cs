using Avalonia.Controls;
using Avalonia.Controls.Templates;
using LibertyX.Partition.ViewModels;

namespace LibertyX.Partition;

/// <summary>XxxViewModel → Views.XxxView, por nome. Cada página e diálogo acha sua tela sozinho.</summary>
public sealed class ViewLocator : IDataTemplate
{
    private static readonly Dictionary<Type, Type?> Cache = [];

    public Control? Build(object? data)
    {
        if (data == null) return null;
        var t = data.GetType();
        if (!Cache.TryGetValue(t, out var view))
        {
            string name = t.FullName!.Replace(".ViewModels.", ".Views.").Replace("ViewModel", "View");
            view = t.Assembly.GetType(name);
            Cache[t] = view;
        }
        return view != null ? (Control)Activator.CreateInstance(view)! : new TextBlock { Text = L.T("Tela não encontrada: ") + t.Name };
    }

    public bool Match(object? data) => data is ViewModelBase;
}
