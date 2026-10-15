using CommunityToolkit.Mvvm.ComponentModel;
using Flagrum.Abstractions;
using Flagrum.Core.Utilities;
using Flagrum.Services;
using PropertyChanged.SourceGenerator;

namespace Flagrum.Main;

public partial class MainViewModel : ObservableObject
{
    private readonly IConfiguration _configuration;

    [Notify] private bool _hasInitializationStarted;
    [Notify] private bool _hasWebView2Runtime;
    [Notify] private bool _isMigratingFinished;
    [Notify] private ViewportViewModel _viewportViewModel = null!;

    public MainViewModel(
        IPlatformService platformService,
        ViewportViewModel viewportViewModel,
        IConfiguration configuration)
    {
        _configuration = configuration;
        ((PlatformService)platformService).Main = this;
        ViewportViewModel = viewportViewModel;
    }

    public string HostPage => IOHelper.GetWebRoot() + "/index.html";
    public string? FmodPath { get; set; }
}