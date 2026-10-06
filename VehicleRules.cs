using System;

namespace Apocapatrol
{
    internal static class VehicleRules
    {
        internal static float ExitDistance(string vehicleName)
        {
            string body = TemplateExporter.PrefabName(vehicleName);
            bool large = body.StartsWith("Rustcargo", StringComparison.OrdinalIgnoreCase) || body.StartsWith("Rustchief", StringComparison.OrdinalIgnoreCase) || body.StartsWith("Rustallion", StringComparison.OrdinalIgnoreCase) || body.StartsWith("Rustliner", StringComparison.OrdinalIgnoreCase);
            return Plugin.BailDistance * (large ? 2f : 1f);
        }
        internal static bool CanTemplate(string vehicleName)
        {
            string body = TemplateExporter.PrefabName(vehicleName);
            return true;   // 2.1.0: the Rustliner (bus) too - its DriveTrigger/sitPos and seat hinges follow the car layout (prototype, Denis tests)
        }
    }
}
