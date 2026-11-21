using Zawed.Api.Platform.Text;

namespace Zawed.Api.Modules.Ledger;

/// <summary>
/// ZAWED's receiving bank accounts and their labels (Java <c>zawed.finance.receiving-accounts</c>, default <c>MAIN</c>,
/// and <c>zawed.finance.receiving-account-labels</c>), read at boot from <c>Zawed:Finance:ReceivingAccounts</c> and
/// <c>Zawed:Finance:ReceivingAccountLabels</c>, or from the variables Spring read for them
/// (<c>ZAWED_FINANCE_RECEIVINGACCOUNTS</c>, <c>ZAWED_FINANCE_RECEIVINGACCOUNTLABELS</c>), so Railway's variables do not
/// change at the cutover. Each is a comma-separated list (as Spring splits a <c>List&lt;String&gt;</c>) or a
/// configuration array.
/// </summary>
public sealed class FinanceSettings
{
    public const string DefaultReceivingAccount = "MAIN";

    public FinanceSettings(IReadOnlyList<string> receivingAccounts, IReadOnlyList<string> labels)
    {
        ReceivingAccounts = receivingAccounts
            .Select(a => JavaText.ToUpperRoot(JavaText.Trim(a)))
            .Where(a => a.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        // A list of CODE=Label entries, for example MAIN=Banque Misr ••4821. A label is shown to Operations and to
        // companies, so it names the bank and at most the last digits of the account. An entry without = stops the boot.
        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in labels)
        {
            if (JavaText.IsBlank(entry))
            {
                continue;
            }
            var split = entry.IndexOf('=', StringComparison.Ordinal);
            if (split <= 0 || JavaText.IsBlank(entry[(split + 1)..]))
            {
                throw new InvalidOperationException("zawed.finance.receiving-account-labels: expected CODE=Label");
            }
            parsed[JavaText.ToUpperRoot(JavaText.Trim(entry[..split]))] = JavaText.Trim(entry[(split + 1)..]);
        }
        Labels = parsed;
    }

    /// <summary>The receiving account codes, upper case.</summary>
    public IReadOnlySet<string> ReceivingAccounts { get; }

    /// <summary>Code → label; a code without a label is shown by its code.</summary>
    public IReadOnlyDictionary<string, string> Labels { get; }

    public string? LabelOf(string receivingAccount) => Labels.GetValueOrDefault(receivingAccount);

    public static FinanceSettings From(IConfiguration configuration) => new(
        List(configuration, "Zawed:Finance:ReceivingAccounts", "ZAWED_FINANCE_RECEIVINGACCOUNTS") ?? [DefaultReceivingAccount],
        List(configuration, "Zawed:Finance:ReceivingAccountLabels", "ZAWED_FINANCE_RECEIVINGACCOUNTLABELS") ?? []);

    private static List<string>? List(IConfiguration configuration, string key, string variable)
    {
        var section = configuration.GetSection(key);
        var items = section.GetChildren().Select(c => c.Value ?? "").ToList();
        if (items.Count > 0)
        {
            return items;
        }
        var text = section.Value ?? configuration[variable];
        // Spring's String-to-List conversion: split on commas, each part trimmed with String.trim().
        return text is null ? null : text.Split(',').Select(JavaText.Trim).ToList();
    }
}
