using System.Diagnostics.CodeAnalysis;
using Serilog.Core;
using Serilog.Events;

namespace ShoppingList.Api.Common.Extensions;

/// <summary>
/// Redacts credential-bearing values before they reach any sink.
/// <para>
/// Written as a policy rather than relying on developers remembering not to log tokens. Log
/// statements are added constantly, often while debugging, and "we do not log secrets" as a
/// convention fails the first time somebody logs a whole request object at 2am. Enforcing it at
/// the pipeline means the failure mode is a redacted log line, not a token in Loki with a
/// seven-day retention.
/// </para>
/// </summary>
internal sealed class SensitiveDataDestructuringPolicy : IDestructuringPolicy
{
    private const string Redacted = "***REDACTED***";

    private static readonly string[] SensitiveNameFragments =
    [
        "password", "secret", "token", "authorization", "apikey", "api_key",
        "accesskey", "access_key", "secretkey", "secret_key", "credential",
        "connectionstring", "connection_string", "clientsecret", "privatekey"
    ];

    public bool TryDestructure(
        object value,
        ILogEventPropertyValueFactory propertyValueFactory,
        [NotNullWhen(true)] out LogEventPropertyValue? result)
    {
        if (value is not System.Collections.IDictionary dictionary)
        {
            result = null;
            return false;
        }

        var properties = new List<LogEventProperty>();

        foreach (System.Collections.DictionaryEntry entry in dictionary)
        {
            var key = entry.Key?.ToString() ?? string.Empty;

            properties.Add(new LogEventProperty(
                key,
                IsSensitive(key)
                    ? new ScalarValue(Redacted)
                    : propertyValueFactory.CreatePropertyValue(entry.Value, destructureObjects: true)));
        }

        result = new StructureValue(properties);
        return true;
    }

    internal static bool IsSensitive(string propertyName) =>
        SensitiveNameFragments.Any(fragment =>
            propertyName.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
