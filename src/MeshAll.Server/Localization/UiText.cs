using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text;
using System.Text.RegularExpressions;

namespace MeshAll.Server.Localization;

/// <summary>Shared .resx text for Razor, server messages and browser status updates.</summary>
public sealed class UiText
{
    private static readonly ResourceManager Resources = new("MeshAll.Server.Resources.Localization.UiText", typeof(UiText).Assembly);
    private static readonly Dictionary<string, string> OperationLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["command"] = "Command",
        ["processes"] = "Processes",
        ["desktop"] = "Desktop",
        ["terminal"] = "Terminal",
        ["powershell"] = "PowerShell terminal",
        ["rdp"] = "Remote Desktop (RDP)",
        ["vnc"] = "VNC",
        ["ssh"] = "SSH",
        ["amt"] = "Intel AMT KVM",
        ["files.list"] = "Browse files",
        ["files.read"] = "Download file",
        ["files.write"] = "Upload file",
        ["files.mkdir"] = "Create folder",
        ["files.delete"] = "Delete file",
        ["inventory.refresh"] = "Inventory refresh",
        ["power"] = "Power action",
        ["wake"] = "Wake device"
    };
    private static readonly Lazy<string[]> Keys = new(() => Resources.GetResourceSet(CultureInfo.InvariantCulture, true, false)!
        .Cast<DictionaryEntry>().Select(entry => (string)entry.Key).ToArray());
    private static readonly Lazy<MessageTemplate[]> Templates = new(CreateTemplates);
    private static readonly Lazy<string[]> Prefixes = new(() => Keys.Value.Where(key => key.EndsWith(": ", StringComparison.Ordinal)).ToArray());

    public string this[string key] => Get(key);
    public string this[string key, params object[] arguments] => Get(key, arguments);
    public string Culture => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

    public static string Get(string key, params object[] arguments)
    {
        string resourceKey = OperationLabels.GetValueOrDefault(key, key);
        string value = Resources.GetString(resourceKey, CultureInfo.CurrentUICulture) ?? resourceKey;
        return arguments.Length == 0 ? value : string.Format(CultureInfo.CurrentCulture, value, arguments);
    }

    public string Plural(long count, string singular, string plural) => Get(count == 1 ? singular : plural, count);
    public string RoleName(string name, bool builtIn) => builtIn ? Get(name) : name;
    public string GroupName(string name, string id) => id == "default" && name == "Unassigned" ? Get(name) : name;

    public string Message(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return "";
        }

        if (OperationLabels.TryGetValue(message, out string? operation))
        {
            return Get(operation);
        }

        string? translated = Resources.GetString(message, CultureInfo.CurrentUICulture);
        if (translated is not null)
        {
            return translated;
        }

        // Stored events and server exceptions keep their canonical source text.
        // Translate known formatted messages at presentation time, preserving data.
        if (Culture != "en" && message.Length <= 4096)
        {
            foreach (string prefix in Prefixes.Value)
            {
                if (message.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return Get(prefix) + message[prefix.Length..];
                }
            }

            foreach (MessageTemplate template in Templates.Value)
            {
                Match match;
                try
                {
                    match = template.Pattern.Match(message);
                }
                catch (RegexMatchTimeoutException)
                {
                    continue;
                }
                if (!match.Success)
                {
                    continue;
                }

                object[] arguments = new object[template.ArgumentCount];
                for (int index = 0; index < template.Indices.Length; index++)
                {
                    arguments[template.Indices[index]] = match.Groups[index + 1].Value;
                }

                return Get(template.Key, arguments);
            }
        }

        return message;
    }

    public static IReadOnlyList<string> ResourceKeys => Keys.Value;

    private static MessageTemplate[] CreateTemplates()
    {
        List<MessageTemplate> templates = [];
        Regex placeholder = new(@"\{(?<index>\d+)(?:[^}]*)\}", RegexOptions.CultureInvariant);
        foreach (string key in Keys.Value)
        {
            MatchCollection placeholders = placeholder.Matches(key);
            if (placeholders.Count == 0)
            {
                continue;
            }

            StringBuilder pattern = new("^");
            List<int> indices = [];
            int position = 0;
            foreach (Match item in placeholders)
            {
                pattern.Append(Regex.Escape(key[position..item.Index])).Append("(.+?)");
                indices.Add(int.Parse(item.Groups["index"].Value, CultureInfo.InvariantCulture));
                position = item.Index + item.Length;
            }

            pattern.Append(Regex.Escape(key[position..])).Append('$');
            templates.Add(new MessageTemplate(key, new Regex(pattern.ToString(), RegexOptions.CultureInvariant | RegexOptions.Singleline,
                TimeSpan.FromMilliseconds(50)), indices.ToArray(), indices.Max() + 1));
        }

        return templates.ToArray();
    }

    private sealed record MessageTemplate(string Key, Regex Pattern, int[] Indices, int ArgumentCount);
}
