using System.Collections.Generic;

namespace Apocapatrol
{
    internal static class EditorSelection
    {
        internal static string Select(HashSet<string> expanded, string id, bool open)
        {
            expanded.Clear();
            if (open && !string.IsNullOrEmpty(id)) { expanded.Add(id); return id; }
            return "";
        }
    }
}
