using Avalonia.Markup.Xaml;

namespace LibertyX.Partition;

/// <summary>Texto traduzido no XAML: <c>Text="{l:T 'Cancelar'}"</c> (a chave é o português, ver <see cref="L"/>).</summary>
public sealed class TExtension(string text) : MarkupExtension
{
    public string Text { get; } = text;

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Text);
}
