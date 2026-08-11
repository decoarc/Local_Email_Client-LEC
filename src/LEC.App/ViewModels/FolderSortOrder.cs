using System;
using System.Globalization;
using LEC.MailStore;

namespace LEC.App.ViewModels;

/// <summary>
/// Ordena a árvore de pastas para exibição: pastas especiais primeiro, numa ordem
/// fixa (não a ordem declarada em SpecialFolderKind), depois as demais pastas em
/// ordem alfanumérica natural. Puramente uma decisão de exibição -- não afeta
/// LEC.MailStore nem a ordem física dos registros no .dbx.
/// </summary>
internal static class FolderSortOrder
{
    private static readonly SpecialFolderKind[] SpecialRankOrder =
    {
        SpecialFolderKind.Inbox,
        SpecialFolderKind.Drafts,
        SpecialFolderKind.Outbox,
        SpecialFolderKind.SentItems,
        SpecialFolderKind.DeletedItems,
    };

    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("pt-BR");

    public static int Compare(FolderNodeViewModel a, FolderNodeViewModel b)
    {
        var rankA = SpecialRank(a.Folder.SpecialFolderKind);
        var rankB = SpecialRank(b.Folder.SpecialFolderKind);
        if (rankA != rankB)
            return rankA.CompareTo(rankB);

        return NaturalCompare(a.DisplayName, b.DisplayName);
    }

    private static int SpecialRank(SpecialFolderKind kind)
    {
        var index = Array.IndexOf(SpecialRankOrder, kind);
        return index == -1 ? SpecialRankOrder.Length : index;
    }

    /// <summary>
    /// Compara duas strings em blocos alternados de dígitos/não-dígitos: blocos de
    /// texto comparam como texto (cultura pt-BR, sem diferenciar maiúsc./minúsc.);
    /// blocos numéricos comparam pelo valor (zeros à esquerda descartados, depois
    /// por tamanho e por dígitos) em vez de caractere a caractere -- "Pasta 2" vem
    /// antes de "Pasta 10". Evita fazer parse para long: números arbitrariamente
    /// grandes não estouram, só nunca deveriam ocorrer em nome de pasta.
    /// </summary>
    internal static int NaturalCompare(string x, string y)
    {
        var i = 0;
        var j = 0;

        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var startI = i;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                var startJ = j;
                while (j < y.Length && char.IsDigit(y[j])) j++;

                var numX = x.Substring(startI, i - startI).TrimStart('0');
                var numY = y.Substring(startJ, j - startJ).TrimStart('0');

                if (numX.Length != numY.Length)
                    return numX.Length.CompareTo(numY.Length);

                var digitCompare = string.CompareOrdinal(numX, numY);
                if (digitCompare != 0)
                    return digitCompare;

                continue;
            }

            var startTextI = i;
            while (i < x.Length && !char.IsDigit(x[i])) i++;
            var startTextJ = j;
            while (j < y.Length && !char.IsDigit(y[j])) j++;

            var textX = x.Substring(startTextI, i - startTextI);
            var textY = y.Substring(startTextJ, j - startTextJ);

            var textCompare = string.Compare(textX, textY, Culture, CompareOptions.IgnoreCase);
            if (textCompare != 0)
                return textCompare;
        }

        return (x.Length - i).CompareTo(y.Length - j);
    }
}
