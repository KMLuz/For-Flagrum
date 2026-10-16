using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Flagrum.Application.Features.Settings.Data;
using Flagrum.Application.Features.Shared;
using Flagrum.Application.Services;
using Flagrum.Abstractions;
using Flagrum.Services;
using Flagrum.Utilities;
using HelixToolkit.SharpDX.Core;
using HelixToolkit.SharpDX.Core.Animations;
using HelixToolkit.SharpDX.Core.Model.Scene;
using HelixToolkit.SharpDX.Core.Utilities;
using HelixToolkit.Wpf.SharpDX;
using HelixToolkit.Wpf.SharpDX.Controls;
using PropertyChanged.SourceGenerator;
using Camera = HelixToolkit.Wpf.SharpDX.Camera;
using Geometry3D = HelixToolkit.SharpDX.Core.Geometry3D;
using Material = HelixToolkit.Wpf.SharpDX.Material;
using PerspectiveCamera = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;
using Microsoft.Extensions.Logging;

namespace Flagrum.Main;

public partial class ViewportViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// If the viewport fails this many times within <see cref="RendererFailureWindow"/>, it stops restarting itself
    /// straight away (the graphics card is probably busy) and tries again when the next model is opened.
    /// </summary>
    private const int MaxRendererFailures = 3;

    private static readonly TimeSpan RendererFailureWindow = TimeSpan.FromSeconds(30);

    private readonly List<BoneSkinMeshNode> _armatureNodes = new();
    private readonly CompositionTargetEx _compositeHelper = new();
    private readonly ILogger<ViewportViewModel> _logger;
    private readonly Queue<DateTime> _rendererFailures = new();
    private DateTime _lastGiveUpMessage = DateTime.MinValue;
    private bool _isRendererLost;
    private bool _isRestartQueued;
    private Viewport3DX? _viewer;

    private NodeAnimationUpdater? _animationUpdater;
    [Notify] private Camera? _camera;
    [Notify] private IEffectsManager? _effectsManager;

    [Notify] private bool _isViewportVisible;
    private Material? _material;
    private Geometry3D? _mesh;
    [Notify] private SceneNodeGroupModel3D? _modelGroup;

    private int _viewportHeight;
    private int _viewportLeft = 514;
    [Notify] private InputGesture? _viewportPanGesture;
    [Notify] private InputGesture? _viewportRotateGesture;
    private int _viewportTop;
    private int _viewportWidth;

    public ViewportViewModel(
        AppStateService appState,
        IProfileService profile,
        IPlatformService platformService,
        IConfiguration configuration,
        TextureConverter textureConverter,
        ILogger<ViewportHelper> viewportLogger,
        ILogger<ViewportViewModel> logger)
    {
        _logger = logger;
        ((PlatformService)platformService).Viewport = this;

        // Set default key bindings for 3D viewer
        if (!configuration.ContainsKey(StateKey.ViewportRotateModifierKey))
        {
            configuration.Set(StateKey.ViewportRotateModifierKey, ModifierKeys.None);
            configuration.Set(StateKey.ViewportRotateMouseAction, MouseAction.MiddleClick);
        }

        if (!configuration.ContainsKey(StateKey.ViewportPanModifierKey))
        {
            configuration.Set(StateKey.ViewportPanModifierKey, ModifierKeys.Shift);
            configuration.Set(StateKey.ViewportPanMouseAction, MouseAction.MiddleClick);
        }

        if (!configuration.ContainsKey(StateKey.CurrentAssetNode))
        {
            configuration.Set(StateKey.CurrentAssetNode, (string?)null);
        }

        // Apply persisted keybindings
        var viewportRotateModifierKey = configuration.Get<ModifierKeys>(StateKey.ViewportRotateModifierKey);
        var viewportRotateMouseAction = configuration.Get<MouseAction>(StateKey.ViewportRotateMouseAction);
        _viewportRotateGesture = new MouseGesture(viewportRotateMouseAction, viewportRotateModifierKey);

        var viewportPanModifierKey = configuration.Get<ModifierKeys>(StateKey.ViewportPanModifierKey);
        var viewportPanMouseAction = configuration.Get<MouseAction>(StateKey.ViewportPanMouseAction);
        _viewportPanGesture = new MouseGesture(viewportPanMouseAction, viewportPanModifierKey);

        ViewportHelper = new ViewportHelper(appState, configuration, textureConverter, this, viewportLogger);
        EffectsManager = new DefaultEffectsManager();
        Camera = new PerspectiveCamera
        {
            Position = new Point3D(50, 30, 100),
            LookDirection = new Vector3D(-50, -30, -100),
            UpDirection = new Vector3D(0, 1, 0),
            NearPlaneDistance = 0,
            FarPlaneDistance = 10000
        };
    }

    public ViewportHelper? ViewportHelper { get; private set; }
    public Viewport3DX? Viewer
    {
        get => _viewer;
        set
        {
            if (_viewer != null)
            {
                _viewer.RenderExceptionOccurred -= Viewer_OnRenderExceptionOccurred;
            }

            _viewer = value;

            if (_viewer != null)
            {
                _viewer.RenderExceptionOccurred += Viewer_OnRenderExceptionOccurred;
            }
        }
    }
    public AirspacePopup? AirspacePopup { get; set; }

    public int ViewportLeft
    {
        get => _viewportLeft;
        set
        {
            SetProperty(ref _viewportLeft, value);
            AirspacePopup!.HorizontalOffset = value;
        }
    }

    public int ViewportTop
    {
        get => _viewportTop;
        set
        {
            SetProperty(ref _viewportTop, value);
            AirspacePopup!.VerticalOffset = value;
        }
    }

    public int ViewportWidth
    {
        get => _viewportWidth;
        set
        {
            SetProperty(ref _viewportWidth, value);
            AirspacePopup!.Width = value;
        }
    }

    public int ViewportHeight
    {
        get => _viewportHeight;
        set
        {
            SetProperty(ref _viewportHeight, value);
            AirspacePopup!.Height = value;
        }
    }

    public void Dispose()
    {
        if (EffectsManager != null)
        {
            var effectsManager = EffectsManager as IDisposable;
            Disposer.RemoveAndDispose(ref effectsManager);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Restarts the viewport if it stopped drawing earlier and hasn't been restarted yet.
    /// </summary>
    public void EnsureRenderer()
    {
        if (_isRendererLost && Viewer is {RenderHost: null})
        {
            RestartRenderer();
        }
    }

    /// <summary>
    /// HelixToolkit throws its renderer away after any error while drawing (for example when the graphics card
    /// is busy with a game), which leaves the viewport black for good. Log the error and start a new renderer.
    /// </summary>
    private void Viewer_OnRenderExceptionOccurred(object? sender, RelayExceptionEventArgs e)
    {
        // Otherwise HelixToolkit writes the error over the viewport
        e.Handled = true;
        _isRendererLost = true;
        _logger.LogError(e.Exception, "The 3D model preview stopped drawing");

        var now = DateTime.UtcNow;
        _rendererFailures.Enqueue(now);
        while (now - _rendererFailures.Peek() > RendererFailureWindow)
        {
            _rendererFailures.Dequeue();
        }

        if (_isRestartQueued)
        {
            return;
        }

        // HelixToolkit disposes the old renderer after this handler, so restart once it's done
        _isRestartQueued = true;
        Viewer!.Dispatcher.InvokeAsync(() =>
        {
            _isRestartQueued = false;

            if (_rendererFailures.Count >= MaxRendererFailures)
            {
                _logger.LogWarning("The 3D model preview failed {Count} times in {Seconds} seconds, "
                                   + "it will be restarted when a model is opened", _rendererFailures.Count,
                    RendererFailureWindow.TotalSeconds);

                ModelGroup = null;
                if (DateTime.UtcNow - _lastGiveUpMessage > RendererFailureWindow)
                {
                    _lastGiveUpMessage = DateTime.UtcNow;
                    MessageBox.Show("The 3D preview stopped working, probably because the graphics card is busy "
                                    + "(for example with a game). It will try again when you open a model.",
                        "Model Preview", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                return;
            }

            RestartRenderer();
            _ = ViewportHelper!.ReloadAsync();
        }, DispatcherPriority.Background);
    }

    private void RestartRenderer()
    {
        if (Viewer == null)
        {
            return;
        }

        _logger.LogInformation("Restarting the 3D model preview");
        _isRendererLost = false;

        // The model's GPU resources belonged to the old renderer, so it is reloaded from scratch
        ModelGroup = null;

        // A new effects manager also creates a new graphics device, in case the old one was lost
        var oldEffectsManager = EffectsManager as IDisposable;
        EffectsManager = new DefaultEffectsManager();
        oldEffectsManager?.Dispose();

        // HelixToolkit only creates its renderer when the viewport's template is applied
        Viewer.OnApplyTemplate();
    }

    private void CompositeHelper_Rendering(object? sender, RenderingEventArgs e)
    {
        _animationUpdater?.Update(Stopwatch.GetTimestamp(), Stopwatch.Frequency);
    }
}