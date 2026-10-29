using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;

namespace Vndb.Desktop.Controls;

/// <summary>One embedded Fluent vector family, no font or runtime network dependency.</summary>
public sealed class AppIcon : PathIcon
{
    protected override Type StyleKeyOverride => typeof(PathIcon);
    public AppIcon() { Width = 18; Height = 18; }
    public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<AppIcon, string>(nameof(Kind), "Book");
    public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e) { base.OnAttachedToLogicalTree(e); Update(); }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) { base.OnPropertyChanged(change); if (change.Property == KindProperty) Update(); }
    private void Update() { if (this.TryFindResource("Icon." + Kind, out var value)) Data = value as Geometry; }
}
