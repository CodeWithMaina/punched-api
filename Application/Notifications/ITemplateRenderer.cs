namespace PunchedApi.Application.Notifications;

public interface ITemplateRenderer
{
    string RenderText(string template, IReadOnlyDictionary<string, object?> data);
    string RenderHtml(string template, IReadOnlyDictionary<string, object?> data);
}