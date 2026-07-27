// src/LEC.App/Services/ImageBlockingWebView2Handler.cs
using Microsoft.Web.WebView2.Core;

namespace LEC.App.Services;

/// <summary>
/// Blocks all network-bound resource requests (images, stylesheets, fonts, media,
/// scripts, XHR/fetch, iframes, ...) in a WebView2 instance until explicitly
/// allowed, per ARQUITETURA.md's "reader, no network" decision. data:/cid: requests
/// are always allowed through -- they serve inline content already embedded in the
/// message and never touch the network. Registered once per CoreWebView2; toggling
/// BlockImages does not require re-navigating — in-flight and future requests are
/// simply allowed or denied based on the current value.
/// </summary>
public sealed class ImageBlockingWebView2Handler
{
    private readonly CoreWebView2 _coreWebView;

    public bool BlockImages { get; set; } = true;

    public ImageBlockingWebView2Handler(CoreWebView2 coreWebView)
    {
        _coreWebView = coreWebView;
        _coreWebView.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        _coreWebView.WebResourceRequested += OnWebResourceRequested;
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!BlockImages)
            return;

        string uri = e.Request.Uri;
        bool isInlineScheme = uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("cid:", StringComparison.OrdinalIgnoreCase);
        if (isInlineScheme)
            return;

        e.Response = _coreWebView.Environment.CreateWebResourceResponse(
            Content: null, StatusCode: 403, ReasonPhrase: "Blocked by LEC", Headers: string.Empty);
    }
}
