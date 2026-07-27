// src/LEC.App/Services/RenderedMessage.cs
namespace LEC.App.Services;

public sealed record RenderedMessage(string? Subject, string? FromDisplay, DateTimeOffset? Date, string Html);
