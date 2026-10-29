using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Vndb.Contracts;

namespace Vndb.Desktop.Controls;

/// <summary>Image presentation adapter; bounded HTTP/disk policy lives in CoverCache.</summary>
public sealed class CoverView : Border
{
    public static readonly StyledProperty<MetadataSnapshot?> MetadataProperty = AvaloniaProperty.Register<CoverView, MetadataSnapshot?>(nameof(Metadata));
    public MetadataSnapshot? Metadata { get => GetValue(MetadataProperty); set => SetValue(MetadataProperty, value); }
    private CancellationTokenSource? _cancel;
    private Bitmap? _bitmap;
    private bool _attached;
    public CoverView() { ClipToBounds = true; CornerRadius = new(8); Child = Placeholder(); }
    private static TextBlock Placeholder() => new() { Text = "VN", FontSize = 28, Opacity = .45,
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); _attached = true; Load(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { _attached = false; _cancel?.Cancel(); Child = Placeholder(); _bitmap?.Dispose(); _bitmap = null; base.OnDetachedFromVisualTree(e); }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    { base.OnPropertyChanged(change); if (change.Property == MetadataProperty && _attached) Load(); }
    private async void Load()
    {
        _cancel?.Cancel(); _cancel?.Dispose(); _cancel = new(); var ct = _cancel.Token;
        _bitmap?.Dispose(); _bitmap = null; Child = Placeholder();
        try
        {
            if (Metadata is not { } metadata || App.Covers is not { } covers) return;
            var file = await covers.GetAsync(metadata, ct); if (file is null || ct.IsCancellationRequested) return;
            var bitmap = await Task.Run(() => { using var stream = File.OpenRead(file); return Bitmap.DecodeToWidth(stream, 320); }, ct);
            if (ct.IsCancellationRequested) { bitmap.Dispose(); return; }
            _bitmap = bitmap; Child = new Image { Source = bitmap, Stretch = Stretch.Uniform };
        }
        catch (Exception e) when (e is IOException or HttpRequestException or OperationCanceledException or ArgumentException or InvalidOperationException)
        { if (!ct.IsCancellationRequested) Child = Placeholder(); }
    }
}
