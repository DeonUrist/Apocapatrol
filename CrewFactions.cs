using System;
using System.Collections.Generic;
using System.Linq;
namespace Apocapatrol
{
    internal static class CrewFactions
    {
        internal static readonly string[] Coyotes={"Bodyguard","Bolter","Gunnar","Lugger","Mechanic","Merchant","Organic_Mechanic","Pistoleer","Pup"};
        internal static bool Empty(string person)=>string.IsNullOrWhiteSpace(person)||person=="None";
        internal static string Of(string person)=>Coyotes.Contains((person??"").Trim(),StringComparer.OrdinalIgnoreCase)?"Coyotes":"Scrapyard";
        internal static bool Allowed(string person,string faction)=>Empty(person)||Of(person)==TrafficFactions.Normalize(faction);
        internal static IEnumerable<string> People(CarTemplate t)=>new[]{t.Driver,t.Passenger}.Concat(t.Turrets).Concat(t.Trailer!=null?People(t.Trailer):Enumerable.Empty<string>());
        internal static bool Compatible(CarTemplate t,string faction)=>t!=null&&TrafficFactions.Normalize(t.Faction)==TrafficFactions.Normalize(faction)&&People(t).All(p=>Allowed(p,faction));
        internal static string Infer(TemplateFile t)=>new[]{t.driver,t.passenger,t.rider,t.rider2,t.rider3,t.rider4}.Concat(t.trailer!=null?new[]{t.trailer.driver,t.trailer.passenger,t.trailer.rider,t.trailer.rider2,t.trailer.rider3,t.trailer.rider4}:new string[0]).Any(p=>!Empty(p)&&Of(p)=="Coyotes")?"Coyotes":"Scrapyard";
    }
}
