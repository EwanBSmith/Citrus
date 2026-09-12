namespace Citrus.Desktop;

/// <summary>Shares workbench actions between menus and keyboard bindings.</summary>
public static class WorkbenchCommands
{
    /// <summary>Gets the routed command whose parameter identifies the workspace action.</summary>
    public static RoutedUICommand Action { get; } = new("Workbench action", nameof(Action), typeof(WorkbenchCommands));
}
