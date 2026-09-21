// src/LEC.App/CompactPrompt.cs
namespace LEC.App;

internal static class CompactPrompt
{
    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "~0 MB";
        double mb = bytes / (1024.0 * 1024.0);
        if (mb < 0.1) return "~0,1 MB";
        return $"~{mb:0.#} MB".Replace('.', ',');
    }

    public static string BuildClosePrompt(int folderCount, long bytes, bool forSingleProfile)
    {
        string where = forSingleProfile ? " neste perfil" : "";
        return $"Há {folderCount} pasta(s) com espaço a recuperar ({FormatBytes(bytes)}){where}. Compactar agora?";
    }

    public static string BuildCloseAnywayPrompt(string errorMessage)
        => errorMessage
            + Environment.NewLine + Environment.NewLine
            + "Não foi possível verificar o espaço a recuperar. Fechar mesmo assim?";
}
