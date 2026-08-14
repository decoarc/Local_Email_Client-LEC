// src/LEC.MailStore/DuplicateDetector.cs
namespace LEC.MailStore;

/// <summary>
/// Agrupa mensagens por remetente/destinatário/assunto/data exatos (texto sem diferenciar
/// maiúsculas/minúsculas nem espaços nas pontas; data com precisão total) e marca, em cada grupo
/// com mais de um membro, todas menos a primeira como <see cref="MessageHeader.IsDuplicate"/>.
/// A primeira do grupo -- e qualquer mensagem sem duplicata -- fica com IsDuplicate = false.
///
/// "Primeira" é a ordem de chegada em <paramref name="headers"/>, nunca a Data: como Date já faz
/// parte da chave de agrupamento, todo membro de um grupo válido compartilha o mesmo valor, então
/// não haveria como usá-lo pra desempatar. Quem chama este método precisa preservar a ordem de
/// chegada dos headers dentro de cada grupo empatado -- o que OrderBy/OrderByDescending do LINQ
/// já garante por serem sorts estáveis, mesmo depois de ordenar a lista por Data ou Remetente.
/// </summary>
public static class DuplicateDetector
{
    public static void MarkDuplicates(IReadOnlyList<MessageHeader> headers)
    {
        foreach (var group in headers.GroupBy(Key))
        {
            bool isFirst = true;
            foreach (var header in group)
            {
                header.IsDuplicate = !isFirst;
                isFirst = false;
            }
        }
    }

    private static (string? Sender, string? To, string? Subject, DateTime? Date) Key(MessageHeader header) =>
        (Normalize(header.SenderAddress), Normalize(header.To), Normalize(header.Subject), header.Date);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
}
