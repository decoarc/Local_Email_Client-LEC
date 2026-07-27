// src/LEC.App/Services/ImageBlockingWebView2Handler.cs
using Microsoft.Web.WebView2.Core;

namespace LEC.App.Services;

/// <summary>
/// Blocks remote image requests in a WebView2 instance until explicitly
/// allowed. Registered once per CoreWebView2; toggling BlockImages does not
/// require re-navigating — in-flight and future image requests are simply
/// allowed or denied based on the current value.
/// </summary>
public sealed class ImageBlockingWebView2Handler
{
    private readonly CoreWebView2 _coreWebView;

    public bool BlockImages { get; set; } = true;

    public ImageBlockingWebView2Handler(CoreWebView2 coreWebView)
    {
        _coreWebView = coreWebView;
        _coreWebView.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Image);
        _coreWebView.WebResourceRequested += OnWebResourceRequested;
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!BlockImages)
            return;

        e.Response = _coreWebView.Environment.CreateWebResourceResponse(
            Content: null, StatusCode: 403, ReasonPhrase: "Blocked by LEC", Headers: string.Empty);
    }
}
