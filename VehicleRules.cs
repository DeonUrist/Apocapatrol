using System;

namespace Apocapatrol
{
    internal static class VehicleRules
    {
        internal static float ExitDistance(string vehicleName)
        {
            string body = TemplateExporter.PrefabName(vehicleName);
            bool large = body.StartsWith("Rustcargo", StringComparison.OrdinalIgnoreCase) || body.StartsWith("Rustchief", StringComparison.OrdinalIgnoreCase) || body.StartsWith("Rustallion", StringComparison.OrdinalIgnoreCase);
            return Plugin.BailDistance * (large ? 2f : 1f);
        }
        internal static bool CanTemplate(string vehicleName)
        {
            string body = TemplateExporter.PrefabName(vehicleName);
            return !body.StartsWith("Rustliner", StringComparison.OrdinalIgnoreCase) && !body.Equals("Bus", StringComparison.OrdinalIgnoreCase) && !body.StartsWith("Bus_", StringComparison.OrdinalIgnoreCase);
        }
    }
}
