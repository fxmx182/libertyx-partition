using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using LibertyX.Partition.ViewModels;

namespace LibertyX.Partition.Views;

public partial class BootMediaView : UserControl
{
    public BootMediaView()
    {
        InitializeComponent();
        // arrastar a ISO/imagem do Explorador direto para a tela, como no balenaEtcher
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None);
        AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            var f = e.Data.GetFiles()?.FirstOrDefault()?.TryGetLocalPath();
            if (f != null && File.Exists(f) && DataContext is BootMediaViewModel vm) await vm.SetImage(f);
        });
    }
}
