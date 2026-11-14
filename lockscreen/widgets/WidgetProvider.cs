using System.Runtime.InteropServices;
using Microsoft.Windows.Widgets;
using Microsoft.Windows.Widgets.Providers;

namespace Vantage.Lockscreen.Widgets;

/// <summary>
/// The three HUD widgets: Node Status, Day/Year Progress and Countdown. Each pinned widget is
/// updated when it is created, shown, or resized, and every few minutes while it is on screen.
/// Nothing sensitive leaves here: see <see cref="Cards"/>.
/// </summary>
[ComVisible(true)]
[Guid(ClassId)]
public sealed class WidgetProvider : IWidgetProvider
{
    public const string ClassId = "7E3F9A20-5C84-4E1B-9D6F-2A1C3B4D5E6F";

    // Pinned widgets we're updating: widget id -> (definition id, size).
    static readonly Dictionary<string, (string Kind, WidgetSize Size)> active = new();
    static readonly object gate = new();
    static Timer? timer;

    public void CreateWidget(WidgetContext context) => Track(context.Id, context.DefinitionId, context.Size);

    public void Activate(WidgetContext context) => Track(context.Id, context.DefinitionId, context.Size);

    public void OnWidgetContextChanged(WidgetContextChangedArgs args)
    {
        var c = args.WidgetContext;
        Track(c.Id, c.DefinitionId, c.Size);
    }

    public void Deactivate(string widgetId) { lock (gate) active.Remove(widgetId); }

    public void DeleteWidget(string widgetId, string customState) { lock (gate) active.Remove(widgetId); }

    public void OnActionInvoked(WidgetActionInvokedArgs args) { }   // the widgets have no buttons

    void Track(string id, string kind, WidgetSize size)
    {
        lock (gate)
        {
            active[id] = (kind, size);
            // One shared timer while any widget is on screen; 5 min respects the platform's throttling.
            timer ??= new Timer(_ => UpdateAll(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }
        Update(id, kind, size);
    }

    static void UpdateAll()
    {
        (string Id, string Kind, WidgetSize Size)[] snapshot;
        lock (gate) snapshot = active.Select(kv => (kv.Key, kv.Value.Kind, kv.Value.Size)).ToArray();
        foreach (var (id, kind, size) in snapshot) Update(id, kind, size);
    }

    static void Update(string id, string kind, WidgetSize size)
    {
        try
        {
            WidgetManager.GetDefault().UpdateWidget(new WidgetUpdateRequestOptions(id)
            {
                Template = Cards.Template(kind, size),
                Data = Cards.Data(kind),
            });
        }
        catch (Exception e) when (e is COMException or InvalidOperationException) { }   // host went away
    }
}
