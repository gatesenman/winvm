using Avalonia.Controls;
using Avalonia.Interactivity;
using WinVM.Services;
using WinVM.ViewModels;

namespace WinVM.Views;

public partial class ImageGalleryWindow : Window
{
    private ImageLibraryViewModel? _vm;

    public ImageGalleryWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as ImageLibraryViewModel);
        Closed += (_, _) => Attach(null);
    }

    /// <summary>The image the user chose ("使用此镜像" / local ISO), or null.</summary>
    public ImagePick? Result { get; private set; }

    private void Attach(ImageLibraryViewModel? vm)
    {
        if (_vm != null)
        {
            _vm.Picked -= OnPicked;
            _vm.OneClickRequested -= OnOneClick;
            _vm.PickLocalIsoAsync = null;
        }
        _vm = vm;
        if (_vm == null) return;
        _vm.Picked += OnPicked;
        _vm.OneClickRequested += OnOneClick;
        _vm.PickLocalIsoAsync = start => DialogService.PickIsoAsync(this, start);
    }

    private void OnPicked(ImagePick p)
    {
        Result = p;
        Close(p);
    }

    private void OnOneClick(IsoEntry e) => Close(null);

    private void OnClose(object? sender, RoutedEventArgs e) => Close(null);
}
