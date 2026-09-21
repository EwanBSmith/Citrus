namespace Citrus.Desktop;

/// <summary>Displays a concise error and copyable, redacted technical details.</summary>
public partial class ErrorWindow : Window
{
    /// <summary>Initializes designer-safe error controls.</summary>
    public ErrorWindow() => InitializeComponent();

    /// <summary>Shows redacted failure details and a recovery hint.</summary>
    internal void SetError(string operation, Exception error)
    {
        Title = operation;
        var hint = error switch
        {
            UnauthorizedAccessException => "Check access to the displayed file or folder.",
            HttpRequestException => "Check the endpoint and HTTP status below. Authentication, feed access, and rate limits are different failures.",
            OperationCanceledException => "The request was cancelled or timed out. Retry a smaller date range if it timed out.",
            _ => "Review the details below before retrying."
        };
        summary.Text = ErrorDialog.Redact(error.Message) + Environment.NewLine + hint;
        details.Text = ErrorDialog.Redact(operation + Environment.NewLine + error);
    }

    /// <summary>Copies the redacted details.</summary>
    private void CopyClicked(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(details.Text); }
        catch (System.Runtime.InteropServices.ExternalException) { summary.Text = "The clipboard is busy. Select and copy the details below."; }
    }

    /// <summary>Closes the dialog.</summary>
    private void CloseClicked(object sender, RoutedEventArgs e) => Close();
}
