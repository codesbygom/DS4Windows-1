using System;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace DS4Windows.Installation;

// Setup may repair the two reserved names, but launch and uninstall must
// still verify their contents: a failed repair is not permission to run them.
internal static class InstallerStartupTaskPolicy
{
    internal const string ManagedDescription = "DS4Windows managed startup task v1";
    internal const string ViiperArguments = "server --usb.retained-import-authority-id=4923336367393615921";

    internal static bool HasCurrentWarning(string correlationId, string warningCorrelationId, string warning) =>
        Guid.TryParseExact(correlationId, "N", out var current) &&
        Guid.TryParseExact(warningCorrelationId, "N", out var observed) &&
        current == observed && !string.IsNullOrWhiteSpace(warning);

    internal static bool IsManaged(string taskXml, string expectedExecutable,
        string expectedArguments, string expectedSid = null, bool requireEnabled = false)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(taskXml) || taskXml.Length > 1024 * 1024 ||
                !Path.IsPathFullyQualified(expectedExecutable)) return false;
            using var reader = XmlReader.Create(new StringReader(taskXml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = 1024 * 1024,
            });
            var document = XDocument.Load(reader);
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            XElement root = document.Root;
            if (root?.Name != ns + "Task") return false;
            if ((string)root.Element(ns + "RegistrationInfo")?.Element(ns + "Description") != ManagedDescription)
                return false;
            var actions = root.Element(ns + "Actions")?.Elements().ToArray();
            var triggers = root.Element(ns + "Triggers")?.Elements().ToArray();
            var principals = root.Element(ns + "Principals")?.Elements().ToArray();
            if (actions?.Length != 1 || actions[0].Name != ns + "Exec" ||
                triggers?.Length != 1 || triggers[0].Name != ns + "LogonTrigger" ||
                principals?.Length != 1 || principals[0].Name != ns + "Principal") return false;
            XElement action = actions[0], principal = principals[0], trigger = triggers[0];
            string command = (string)action.Element(ns + "Command");
            string working = (string)action.Element(ns + "WorkingDirectory");
            string arguments = ((string)action.Element(ns + "Arguments") ?? "").Trim();
            string sid = (string)principal.Element(ns + "UserId");
            string triggerSid = (string)trigger.Element(ns + "UserId");
            if (!SamePath(command, expectedExecutable) ||
                !SamePath(working, Path.GetDirectoryName(expectedExecutable)) ||
                arguments != expectedArguments ||
                (string)principal.Element(ns + "LogonType") != "InteractiveToken" ||
                (string)principal.Element(ns + "RunLevel") != "HighestAvailable" ||
                string.IsNullOrWhiteSpace(sid) ||
                (!string.IsNullOrEmpty(expectedSid) && !string.Equals(sid, expectedSid, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(triggerSid) && !string.Equals(triggerSid, sid, StringComparison.OrdinalIgnoreCase)))
                return false;
            return !requireEnabled ||
                (Enabled(root.Element(ns + "Settings")?.Element(ns + "Enabled")) && Enabled(trigger.Element(ns + "Enabled")));
        }
        catch { return false; }
    }

    private static bool Enabled(XElement element) => element == null ||
        string.Equals(element.Value, "true", StringComparison.OrdinalIgnoreCase) || element.Value == "1";

    private static bool SamePath(string actual, string expected) =>
        !string.IsNullOrWhiteSpace(actual) && Path.IsPathFullyQualified(actual) &&
        string.Equals(Path.GetFullPath(actual).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(expected).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
}
