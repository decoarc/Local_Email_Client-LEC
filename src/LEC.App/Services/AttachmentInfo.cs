// src/LEC.App/Services/AttachmentInfo.cs
namespace LEC.App.Services;

public sealed record AttachmentInfo(string FileName, string MimeType, long Size, int Index);
