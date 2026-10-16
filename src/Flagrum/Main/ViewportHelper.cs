using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Flagrum.Abstractions;
using Flagrum.Abstractions.AssetExplorer;
using Flagrum.Core.Archive;
using Flagrum.Core.Graphics.Materials;
using Flagrum.Core.Graphics.Models;
using Flagrum.Generators;
using Flagrum.Application.Features.AssetExplorer.Data;
using Flagrum.Application.Features.Shared;
using Flagrum.Application.Services;
using HelixToolkit.SharpDX.Core;
using HelixToolkit.SharpDX.Core.Assimp;
using HelixToolkit.SharpDX.Core.Model.Scene;
using HelixToolkit.Wpf.SharpDX;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpDX;

namespace Flagrum.Main;

[InjectableDependency(ServiceLifetime.Singleton)]
public partial class ViewportHelper
{
    [Inject] private readonly AppStateService _appState;
    [Inject] private readonly IConfiguration _configuration;
    [Inject] private readonly TextureConverter _textureConverter;
    [Inject] private readonly ViewportViewModel _viewModel;
    [Inject] private readonly ILogger<ViewportHelper> _logger;

    /// <summary>
    /// Most textures kept converted between model loads. Several outfits share the same skin textures,
    /// and switching LOD reloads the same model, so this saves converting them again.
    /// </summary>
    private const int MaxCachedTextures = 40;

    private readonly ConcurrentDictionary<string, Lazy<byte[]?>> _textureImageCache = new();
    private int _latestRequest;
    private (IAssetExplorerNode Node, AssetExplorerView View, int LodLevel)? _currentModel;

    /// <summary>
    /// Loads a model into the 3D viewport.
    /// </summary>
    /// <returns>The number of LODs the model has, or 0 if there was nothing to show.</returns>
    /// <remarks>
    /// The model is read and its textures converted on background threads, so Flagrum stays responsive
    /// while it loads. Only adding the finished meshes to the viewport happens on the UI thread.
    /// </remarks>
    public async Task<int> ChangeModelAsync(IAssetExplorerNode gmdlNode, AssetExplorerView view, int lodLevel)
    {
        var request = Interlocked.Increment(ref _latestRequest);
        _currentModel = (gmdlNode, view, lodLevel);
        _logger.LogInformation("Previewing {Path} at LOD {LodLevel}", gmdlNode.Path, lodLevel);

        _viewModel.EnsureRenderer();
        _viewModel.Viewer!.Reset();

        BuiltModel? result;
        try
        {
            result = await Task.Run(() => BuildModel(gmdlNode, view, lodLevel));
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to load model {Path}", gmdlNode.Path);
            if (request == _latestRequest)
            {
                MessageBox.Show($"Flagrum couldn't show this model:\n\n{exception.Message}",
                    "Model Preview", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            return 0;
        }

        // Another model was requested while this one was loading, so don't overwrite it
        if (request != _latestRequest)
        {
            return result?.LodLevels ?? 0;
        }

        if (result == null)
        {
            MessageBox.Show("This model doesn't have a corresponding gpubin file, so there is nothing to show.",
                "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            return 0;
        }

        var modelGroup = new SceneNodeGroupModel3D();
        var scene = new HelixToolkitScene(new GroupNode());
        modelGroup.AddNode(scene.Root);

        foreach (var node in result.Nodes)
        {
            modelGroup.AddNode(node);
        }

        _viewModel.ModelGroup = modelGroup;
        _viewModel.Viewer.ZoomExtents(0);

        return result.LodLevels;
    }

    /// <summary>
    /// Loads the model that was last shown again, after the viewport had to be restarted.
    /// </summary>
    public async Task ReloadAsync()
    {
        if (_currentModel is var (node, view, lodLevel))
        {
            _logger.LogInformation("Reloading the model preview after a restart");

            // Nothing awaits this, so an error here must not escape (unobserved task errors crash Flagrum)
            try
            {
                await ChangeModelAsync(node, view, lodLevel);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to reload the model preview");
            }
        }
    }

    private record BuiltModel(List<SceneNode> Nodes, int LodLevels);

    private BuiltModel? BuildModel(IAssetExplorerNode gmdlNode, AssetExplorerView view, int lodLevel)
    {
        var modelBag = new ConcurrentBag<SceneNode>();

        // Read the model
        var gmdl = gmdlNode.Data;
        var model = new GameModel();
        model.Read(gmdl);

        // Get the gpubin uris
        var gpubinUris = model.Dependencies
            .Where(d => d.Value.EndsWith(".gpubin"))
            .Select(d => d.Value)
            .ToList();

        if (!gpubinUris.Any())
        {
            return null;
        }

        // Find the gpubin locations
        var gpubins = gpubinUris.Select(uri => gmdlNode.Parent.Children
                .Single(c => c.Path.Split('\\', '/').Last()
                    .Equals(uri.Split('\\', '/').Last(), StringComparison.OrdinalIgnoreCase)))
            .OrderBy(f => f.Name)
            .Select(gpubin => gpubin.Data)
            .ToList();

        // Read the gpubins
        model.ReadVertexData(gpubins);

        // Get the texture fidelity settings
        var fidelityInt = _configuration.Get<int>(StateKey.ViewportTextureFidelity);
        var fidelity = fidelityInt == -1 ? ModelViewerTextureFidelity.Low : (ModelViewerTextureFidelity)fidelityInt;

        // Calculate paths
        var gpubinUri = gpubinUris.First();
        var modelContext = new FileSystemModelContext
        {
            RootUri = gpubinUri[..gpubinUri.LastIndexOf('/')],
            RootDirectory = string.Join('\\', gmdlNode.Path.Split('\\')[..^1])
        };

        object? contextObject = view == AssetExplorerView.FileSystem ? modelContext : null;

        // Many meshes share materials and textures, so only read and convert each one once per load.
        // Archives are kept open for the duration of the load instead of being reopened for every file,
        // and are all closed again before returning so the mod manager can still write to them.
        using var archives = new EbonyArchiveManager();
        var materials = new ConcurrentDictionary<string, Lazy<GameMaterial?>>();
        var textures = new ConcurrentDictionary<string, Lazy<TextureModel?>>();

        GameMaterial? GetMaterial(string uri) => materials.GetOrAdd(uri, _ => new Lazy<GameMaterial?>(() =>
        {
            var data = GetDataByUri(uri, contextObject, view, fidelity, archives);
            if (data == null)
            {
                return null;
            }

            var material = new GameMaterial();
            material.Read(data);
            return material;
        })).Value;

        TextureModel? GetTexture(string uri) => textures.GetOrAdd(uri, _ => new Lazy<TextureModel?>(() =>
        {
            var image = GetTextureImage(uri, contextObject, view, fidelity, archives);
            return image == null ? null : TextureModel.Create(new MemoryStream(image));
        })).Value;

        Parallel.ForEach(model.MeshObjects, meshObject =>
        {
            Parallel.ForEach(meshObject.Meshes.Where(m => m.LodLevel == lodLevel && (m.Flags & 67108864) == 0), mesh =>
            {
                var builder = new MeshBuilder();
                var faceIndices = new List<int>();

                for (var i = 0; i < mesh.FaceIndices.Length / 3; i++)
                {
                    var triangle = new List<int>();
                    for (var j = 0; j < 3; j++)
                    {
                        var index = (int)mesh.FaceIndices[i, j];

                        if (index < mesh.VertexCount)
                        {
                            triangle.Add(index);
                        }
                    }

                    if (triangle.Count == 3)
                    {
                        faceIndices.AddRange(triangle);
                    }
                }

                if (mesh.Semantics.TryGetValue(VertexElementSemantic.TexCoord0, out var semantic))
                {
                    builder.Append(
                        ((IList<float[]>)mesh.Semantics[VertexElementSemantic.Position0]).Select(v => new Vector3(v[..3]))
                        .ToList(),
                        faceIndices,
                        ((IList<float[]>)mesh.Semantics[VertexElementSemantic.Normal0]).Select(n => new Vector3(n[..3]))
                        .ToList(),
                        ((IList<float[]>)semantic).Select(c => new Vector2(c[..2]))
                        .ToList());
                }
                else
                {
                    builder.Append(
                        ((IList<float[]>)mesh.Semantics[VertexElementSemantic.Position0]).Select(v => new Vector3(v[..3]))
                        .ToList(),
                        faceIndices,
                        ((IList<float[]>)mesh.Semantics[VertexElementSemantic.Normal0]).Select(n => new Vector3(n[..3]))
                        .ToList(),
                        Enumerable.Range(0, (int)mesh.VertexCount).Select(i => new Vector2(0f, 0f))
                            .ToList());
                }

                var meshNode = new BoneSkinMeshNode();
                var material = new PBRMaterial
                {
                    AlbedoColor = Color4.White
                };

                if (fidelity != ModelViewerTextureFidelity.None)
                {
                    var materialUri = model.Dependencies
                        .FirstOrDefault(d => d.Key == mesh.MaterialHash.ToString())
                        .Value;

                    if (materialUri != null)
                    {
                        var gameMaterial = GetMaterial(materialUri);
                        if (gameMaterial == null)
                        {
                            return;
                        }

                        var diffuseUri = gameMaterial.Textures
                            .FirstOrDefault(t =>
                                t.ShaderGenName.Contains("BaseColor0") || t.ShaderGenName.Contains("BaseColorTexture0"))
                            ?.Uri;

                        var normalUri = gameMaterial.Textures
                            .FirstOrDefault(t =>
                                t.ShaderGenName.Contains("Normal0") || t.ShaderGenName.Contains("NormalTexture0"))
                            ?.Uri;

                        if (diffuseUri != null && normalUri != null)
                        {
                            TextureModel? albedoMap = null;
                            TextureModel? normalMap = null;

                            Parallel.Invoke(
                                () => albedoMap = GetTexture(diffuseUri),
                                () => normalMap = GetTexture(normalUri));

                            if (albedoMap != null)
                            {
                                material.AlbedoMap = albedoMap;
                            }

                            if (normalMap != null)
                            {
                                material.NormalMap = normalMap;
                            }
                        }
                    }
                }

                meshNode.Material = material;
                builder.ComputeTangents(MeshFaces.Default);
                meshNode.Geometry = builder.ToMesh();
                //meshNode.Bones = bones.ToArray();
                modelBag.Add(meshNode);
            });
        });

        return new BuiltModel(modelBag.ToList(), model.LodLevels);
    }

    /// <summary>
    /// Gets a texture as an image the viewport can display, converting it from the game's format if needed.
    /// </summary>
    private byte[]? GetTextureImage(string uri, object? context, AssetExplorerView view,
        ModelViewerTextureFidelity fidelity, EbonyArchiveManager archives)
    {
        byte[]? Convert()
        {
            try
            {
                var data = GetDataByUri(uri, context, view, fidelity, archives);
                if (data is not {Length: > 0})
                {
                    return null;
                }

                var tag = Encoding.UTF8.GetString(data[..8]);
                return tag == "SEDBbtex" || tag.StartsWith("BTEX") ? _textureConverter.ToPng(data) : data;
            }
            catch (Exception exception)
            {
                // Show the mesh untextured rather than failing the whole model
                _logger.LogWarning(exception, "Failed to load texture {Uri} for the model preview", uri);
                return null;
            }
        }

        // Only game files are kept between loads, files on disk may have been re-exported since
        if (view != AssetExplorerView.GameView)
        {
            return Convert();
        }

        if (_textureImageCache.Count > MaxCachedTextures)
        {
            _textureImageCache.Clear();
        }

        return _textureImageCache.GetOrAdd($"{fidelity}|{uri}", _ => new Lazy<byte[]?>(Convert)).Value;
    }

    private byte[]? GetDataByUri(string uri, object? context, AssetExplorerView view,
        ModelViewerTextureFidelity fidelity, EbonyArchiveManager archives)
    {
        if (view == AssetExplorerView.GameView)
        {
            var high = uri.Insert(uri.LastIndexOf('.'), "_$h");
            var highest = high.Replace("/sourceimages/", "/highimages/");

            if (fidelity >= ModelViewerTextureFidelity.Highest)
            {
                var data = _appState.GetFileByUri(highest, archives);
                if (data.Length > 0)
                {
                    return data;
                }
            }

            if (fidelity >= ModelViewerTextureFidelity.High)
            {
                var data = _appState.GetFileByUri(high, archives);
                if (data.Length > 0)
                {
                    return data;
                }
            }

            if (fidelity >= ModelViewerTextureFidelity.Low)
            {
                var data = _appState.GetFileByUri(uri, archives);
                if (data.Length > 0)
                {
                    return data;
                }
            }

            return null;
        }

        var baseContext = (FileSystemModelContext)context!;
        var modelContext = new FileSystemModelContext
        {
            Uri = uri,
            RootUri = baseContext.RootUri,
            RootDirectory = baseContext.RootDirectory
        };

        var path = uri.EndsWith(".gmtl")
            ? UriToRelativePath(modelContext)
            : ResolveTexturePath(modelContext, fidelity);

        return path == null || !File.Exists(path) ? null : File.ReadAllBytes(path);
    }

    private string? UriToRelativePath(FileSystemModelContext context)
    {
        // Get path tokens
        var target = GetMatchingUriStart(context.Uri, context.RootUri);
        var rootUriTokens = context.RootUri.Replace("data://", "data/").Split('/');
        var rootPathTokens = context.RootDirectory.Split('\\');
        var targetTokens = target.Replace("data://", "data/").Split('/');
        var targetToken = targetTokens.Last();

        // Find index of the first folder the URIs have in common
        var index = -1;
        var counter = 0;
        for (var i = rootUriTokens.Length - 1; i >= 0; i--)
        {
            if (rootUriTokens[i].Equals(targetToken, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }

            counter++;
        }

        if (index == -1)
        {
            return null;
        }

        // Calculate the final physical path
        var root = string.Join('\\', rootPathTokens[..^counter]);
        var remainingPath = context.Uri.Replace(target, "").Replace("://", "/");
        var path = $@"{root}{remainingPath.Replace('/', '\\')}";

        // Calculate the true extension
        path = path.Replace(".gmtl", ".gmtl.gfxbin");
        return path;
    }

    private string GetMatchingUriStart(string first, string second)
    {
        var tokens1 = first.Replace("data://", "data/").Split('/');
        var tokens2 = second.Replace("data://", "data/").Split('/');
        var tokens = new List<string>();

        var length = Math.Min(tokens1.Length, tokens2.Length);
        for (var i = 0; i < length; i++)
        {
            if (tokens1[i].Equals(tokens2[i], StringComparison.OrdinalIgnoreCase))
            {
                tokens.Add(tokens1[i]);
            }
            else
            {
                break;
            }
        }

        return string.Join('/', tokens).Replace("data/", "data://");
    }

    private string? ResolveTexturePath(FileSystemModelContext context, ModelViewerTextureFidelity fidelity)
    {
        var extensions = new[] {"dds", "tga", "png", "btex"};
        var high = context.Uri.Insert(context.Uri.LastIndexOf('.'), "_$h");
        var highest = high.Replace("/sourceimages/", "/highimages/");

        if (fidelity >= ModelViewerTextureFidelity.Highest)
        {
            var highestPath = UriToRelativePath(new FileSystemModelContext
            {
                RootDirectory = context.RootDirectory,
                RootUri = context.RootUri,
                Uri = highest
            });

            if (highestPath != null)
            {
                foreach (var extension in extensions)
                {
                    var withoutExtension = highestPath[..highestPath.LastIndexOf('.')];
                    var withExtension = $"{withoutExtension}.{extension}";

                    if (File.Exists(withExtension))
                    {
                        return withExtension;
                    }
                }
            }
        }

        if (fidelity >= ModelViewerTextureFidelity.High)
        {
            var highPath = UriToRelativePath(new FileSystemModelContext
            {
                RootDirectory = context.RootDirectory,
                RootUri = context.RootUri,
                Uri = high
            });

            if (highPath != null)
            {
                foreach (var extension in extensions)
                {
                    var withoutExtension = highPath[..highPath.LastIndexOf('.')];
                    var withExtension = $"{withoutExtension}.{extension}";

                    if (File.Exists(withExtension))
                    {
                        return withExtension;
                    }
                }
            }
        }

        if (fidelity >= ModelViewerTextureFidelity.Low)
        {
            var lowPath = UriToRelativePath(new FileSystemModelContext
            {
                RootDirectory = context.RootDirectory,
                RootUri = context.RootUri,
                Uri = context.Uri
            });

            if (lowPath != null)
            {
                foreach (var extension in extensions)
                {
                    var withoutExtension = lowPath[..lowPath.LastIndexOf('.')];
                    var withExtension = $"{withoutExtension}.{extension}";

                    if (File.Exists(withExtension))
                    {
                        return withExtension;
                    }
                }
            }
        }

        return null;
    }

    private class FileSystemModelContext
    {
        public string Uri { get; set; }
        public string RootUri { get; set; }
        public string? RootDirectory { get; set; }
    }
}